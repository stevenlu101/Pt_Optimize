using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 壳上的稳态电流场，有限体积、单元形心未知量：
///
///   ∇·(σ t ∇V) = 0 ,   J = −σ ∇V
///
/// 与 <see cref="PlateCurrent2D"/> 的区别只有一个：**不认识网格结构**。
/// 它只用 <see cref="ShellMesh"/> 给的单元面积、面长、形心间距与邻居关系，
/// 因此同一份求解器可直接用于变步长网格、任意法兰轮廓、以及将来管+法兰的三维装配。
///
/// 守恒性：面通量在相邻两单元上等值反号，故**电流严格守恒**——
/// 流入舌片末端的电流必等于流出管孔的电流，由 <see cref="ShellCurrentResult.ConservationError"/> 检验。
/// 这与 PlateCurrent2D 的五点格式是同一个道理，只是推广到非结构网格。
/// </summary>
public sealed class ShellCurrentResult
{
    public double[] V = Array.Empty<double>();        // 单元电位（归一化 0…1）
    public double[] JMagAPerMm2 = Array.Empty<double>();
    public double CurrentInA, CurrentOutA, ConservationError;
    public double JMaxAPerMm2, JMeanAPerMm2;
    public int JMaxCell = -1;
    public double TotalGenW;
    public int Iterations;
    /// <summary>**真残差** ‖Ax−b‖∞（不是步长）。见 Solve 里的说明。</summary>
    public double Residual;
    /// <summary>
    /// 线性解**收敛了没有**。false ⇒ 电位场没解到位，**下游一切都不作数**。
    /// ⚠ 2026-08-29 之前没有这个字段：不收敛时静默返回一个半成品。
    /// </summary>
    public bool Converged;
}

public static class ShellCurrent
{
    /// <summary>
    /// 解电位场。边界：<see cref="ShellMesh.TagTabEnd"/> 取 V=1，
    /// <see cref="ShellMesh.TagHole"/> 取 V=0，其余自然 Neumann（零通量，无需显式处理）。
    /// ★ R48（2026-09-14，Opus 5）：生产网格还带 <see cref="ShellMesh.ClampCell"/>（压接段整面接触，FlangeMesher 的配方 ③）——
    ///   那些格一并取 V=1，电极 = 整个压接面，电流主要从压接段内边（x = 舌尖 + 压接长）进铂。
    /// ★ R48 F（2026-09-15，Opus 5）：<see cref="ShellMesh.ClampFaceDirichlet"/>（配方 ⑤，默认开）时 V=1 施加在压接面上而不是压接格形心，见 Solve 里那段。
    /// </summary>
    /// <param name="rhoRefOhmMm">参考电阻率 Ω·mm（= Ω·m × 1000）</param>
    /// <param name="tempC">可选单元温度，用于 σ(T)；null 则等温</param>
    /// <summary>
    /// 按算例的开关选解法。**开关只在这一处读** —— 各调用点自己判会漏掉一处，
    /// 而漏掉的那处会静默用另一种解法，两边的数对不上却没人知道是为什么。
    /// </summary>
    public static ShellCurrentResult SolveFor(LineCase c, ShellMesh m, double totalCurrentA,
                                              double rhoRefOhmMm, double tRefC = 1300,
                                              double[]? tempC = null,
                                              int maxIter = 20000, double tol = 1e-9)
        => Solve(m, totalCurrentA, rhoRefOhmMm, tRefC, tempC, maxIter, tol,
                 useGaussSeidel: c?.Base?.LinearGaussSeidel ?? false);

    public static ShellCurrentResult Solve(ShellMesh m, double totalCurrentA,
                                           double rhoRefOhmMm, double tRefC = 1300,
                                           double[]? tempC = null,
                                           int maxIter = 20000, double tol = 1e-9,
                                           bool useGaussSeidel = false)
    {
        int n = m.CellCount;
        var res = new ShellCurrentResult { V = new double[n], JMagAPerMm2 = new double[n] };
        if (n == 0) return res;

        // 单元电导率（相对参考值）。σ ∝ 1/ρe(T)
        var sig = new double[n];
        double rhoRef = Math.Max(1e-30, Materials.PtResistivity(tRefC));
        for (int i = 0; i < n; i++)
            sig[i] = tempC == null ? 1.0 : rhoRef / Math.Max(1e-30, Materials.PtResistivity(tempC[i]));

        // 固定边界：把 Dirichlet 施加在**边界面所属单元**上
        var fixedVal = new double[n];
        var isFixed = new bool[n];
        foreach (var f in m.Faces)
        {
            if (f.B >= 0) continue;
            if (f.Tag == ShellMesh.TagTabEnd) { isFixed[f.A] = true; fixedVal[f.A] = 1.0; }
            else if (f.Tag == ShellMesh.TagHole) { isFixed[f.A] = true; fixedVal[f.A] = 0.0; }
        }
        // ★ R48 生产配方（2026-09-14，Opus 5）：压接段整面接触 —— 形心在压接段内的格一并作电极（ShellMesh.ClampCell 空 = 老口径只钉外圈，逐位不变）。
        //   整面口径下各量的语义逐条核过（2026-09-14 Opus 5）：
        //   · 下面「总电流」只累加定温格与邻格的净电流 —— 压接格之间 V 相等、面电流为 0，只剩内边那一排面，正是进铂的电流，定标不受影响；
        //   · 压接格内部 J ≈ 0（电流在铜排里走），内边那排压接格的 J 由最小二乘重构只拿到一侧面的通量，约为邻格的一半
        //     （由下面的重构式推得：x 向两面只有一面有通量 jn ⇒ Jx = jn/2；推导，没有单测）—— 这是电极格本身的离散假象，量级 O(h)（配方 ④ 的细带让这排格随 h 变细）【R48 F 2026-09-15 Opus 5：面上定电位（配方 ⑤，缺省）下压接格 J 取 0，这排假象没有了；细带缺省也改为不铺】；定温模式下这些格被热场排除在账外，热导模式下计入发热；
        //   · TotalGenW 因此不含压接段里的铂发热，这是模型的本意（deliverable/R48_压接整面接触AB_2026-09-14.txt：整面比外圈片0 发热少 60.6～63.4 W；
        //     该文件列名写「舌区发热」，取的其实是热场整片 QGenW）。
        if (m.ClampFullFaceActive)   // 2026-09-15 Opus 5（J 路，P2-5）：整面接触的唯一定义（原为 ClampCell.Length == n；全假数组不钉任何格，逐位不变）
            for (int i = 0; i < n; i++) if (m.ClampCell[i]) { isFixed[i] = true; fixedVal[i] = 1.0; }
        for (int i = 0; i < n; i++) if (isFixed[i]) res.V[i] = fixedVal[i];

        // ★★ R48 F（2026-09-15，Opus 5；物理把关人）：**压接面上定电位**（ShellMesh.ClampFaceDirichlet，默认开；只在整面接触时起作用）。
        //   形心整格口径：内边那排压接格整格 V = 1，面导度按两格形心距 ⇒ 电极落在压接格形心，比真实内边往压接区里偏 h/2（自由舌片电长度偏长半格，一阶误差）。
        //   面上口径：压接格仍是固定格（V = 1 搬到右端项，不作未知数），但**压接面**（一侧压接格、一侧自由格的内部面）的导度只取自由格一侧：
        //     G = σ_自由·t_自由·L ÷ (自由格形心到面中点的距离)，距离 = ShellMesh.CentroidToFaceMm（边界面 DistAB 的同一个定义）
        //   ⇒ 自由格方程里那一项正是 G·(1 − V_自由)，电位 1 落在真实内边上。下面的总电流（从固定格流出的净电流）自动就是穿过压接面的电流。
        //   压接格内的 J 在面上口径下取 0（见重构那段）。开关关 ⇒ clampSet 为 null，下面每一步的算术与改动前逐位相同。
        bool[]? clampSet = m.ClampFaceActive ? m.ClampSetCells() : null;
        if (clampSet != null)
            for (int i = 0; i < n; i++)
            {
                if (!clampSet[i]) continue;
                // 自检：集合里的格必须都被上面钉住了 —— 没钉住说明两份定义漂开了，当场炸，不许把边界值施加到错的面上
                if (!isFixed[i])
                    throw new InvalidOperationException($"电流场：压接格集合（ShellMesh.ClampSetCells）里的格 {i} 没被钉成电极 —— 两份压接格定义漂开了。");
                // 退化：同一格既带压接标签又带管孔标签、按面序被钉成 V=0（压接段贴到了管孔）⇒ 它不是电极，面上口径不认它，与它相邻的面照旧
                if (fixedVal[i] != 1.0) clampSet[i] = false;
            }

        // 面传导系数 G = σ_f · t_f · L / d   （调和平均取界面值，厚度突变处才不会失真）
        var g = new double[m.Faces.Count];
        for (int k = 0; k < m.Faces.Count; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0 || f.DistAB < 1e-12) continue;
            if (clampSet != null && ShellMesh.IsClampFace(f, clampSet, out int fc) && !isFixed[fc])
            {
                double dFace = m.CentroidToFaceMm(fc, f);
                g[k] = dFace < 1e-12 ? 0 : sig[fc] * m.Thickness[fc] * f.Length / dFace;   // 面上定电位：只取自由格一侧的半距
                continue;
            }
            double sA = sig[f.A] * m.Thickness[f.A], sB = sig[f.B] * m.Thickness[f.B];
            double sf = (sA * sB) > 0 ? 2 * sA * sB / (sA + sB) : 0;   // 调和平均
            g[k] = sf * f.Length / f.DistAB;
        }

        // ★★ **共轭梯度 + Jacobi 预条件**（2026-08-29 换）。
        //
        // 此前是 Gauss–Seidel + SOR，注释写着「n 约数千，直接迭代足够；**换更大网格再上 CG**」。
        // **那个条件已经满足了**：网格无关复核现在要 34000+ 单元，而当时是「约数千」。
        //
        // 为什么非换不可（实测，不是理论）：
        //   GS 在泊松型问题上迭代次数 ~ 条件数 ~ h⁻² ~ n ⇒ 总成本 ~ n²。
        //   实测 0.6 档：**单元 6.4× → 每轮外层耦合耗时 96×**（≈ n^2.5，含外层 Picard）。
        //   0.146 mm 那一档因此**跑不完**（3 轮走了 2 时 26 分）。
        //   CG + 对角预条件：迭代 ~ √条件数 ~ √n ⇒ 成本 ~ n^1.5。
        //
        // ⚠ 矩阵是带 Dirichlet 的**加权图拉普拉斯**，对称正定 ⇒ CG 适用且无需对称化。
        //
        // ★★ 顺带修掉同一族的第三个病：**旧的收敛判据拿的是「步长」不是「残差」**
        //   （`resid = max|ΔV|`）。慢收敛时步长可以很小而残差很大 ——
        //   与基线耦合环、CoupledSolver 那两处**同一个错**。
        //   现在判的是真残差 ‖Ax−b‖∞ 相对 ‖b‖∞，且**不收敛要说出来**（Converged）。
        var diag = new double[n];
        var nbr = new List<(int cell, int face)>[n];
        for (int i = 0; i < n; i++) nbr[i] = new List<(int, int)>();
        for (int k = 0; k < m.Faces.Count; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0) continue;
            nbr[f.A].Add((f.B, k)); nbr[f.B].Add((f.A, k));
            diag[f.A] += g[k]; diag[f.B] += g[k];
        }

        if (useGaussSeidel)
        {
            // ── 旧解法：Gauss–Seidel + SOR，**判据是「步长」不是残差**。
            //   只保留做**配对对照**用（--gslinear）：换 CG 的同时今天还改过网格，
            //   两个变量混在一起就说不清是谁把 ③ 改了。**不得用于交付。**
            const double omegaGs = 1.7;
            double residGs = 0; int itGs = 0;
            for (; itGs < maxIter; itGs++)
            {
                residGs = 0;
                for (int i = 0; i < n; i++)
                {
                    if (isFixed[i] || diag[i] <= 0) continue;
                    double s = 0;
                    foreach (var (c, k) in nbr[i]) s += g[k] * res.V[c];
                    double d = s / diag[i] - res.V[i];
                    res.V[i] += omegaGs * d;
                    residGs = Math.Max(residGs, Math.Abs(d));
                }
                if (residGs < tol) { itGs++; break; }
            }
            res.Iterations = itGs; res.Residual = residGs; res.Converged = residGs < tol;
            goto scale;
        }

        // 只在**自由**单元上解；固定单元的值搬到右端项
        var free = new List<int>(n);
        var idx = new int[n];
        for (int i = 0; i < n; i++)
        {
            idx[i] = -1;
            if (!isFixed[i] && diag[i] > 0) { idx[i] = free.Count; free.Add(i); }
        }
        int nf = free.Count;
        var x = new double[nf];
        var b = new double[nf];
        for (int a = 0; a < nf; a++)
        {
            int i = free[a];
            x[a] = res.V[i];
            double rhs = 0;
            foreach (var (c, k) in nbr[i]) if (idx[c] < 0) rhs += g[k] * res.V[c];
            b[a] = rhs;
        }

        void MatVec(double[] src, double[] dst)
        {
            for (int a = 0; a < nf; a++)
            {
                int i = free[a];
                double s = diag[i] * src[a];
                foreach (var (c, k) in nbr[i]) { int j = idx[c]; if (j >= 0) s -= g[k] * src[j]; }
                dst[a] = s;
            }
        }

        double bNorm = 0;
        for (int a = 0; a < nf; a++) bNorm = Math.Max(bNorm, Math.Abs(b[a]));
        if (bNorm <= 0) bNorm = 1;

        var rv = new double[nf]; var z = new double[nf];
        var pv = new double[nf]; var ap = new double[nf];
        MatVec(x, rv);
        for (int a = 0; a < nf; a++) rv[a] = b[a] - rv[a];

        double Precond(int a) => 1.0 / diag[free[a]];      // Jacobi
        double rz = 0;
        for (int a = 0; a < nf; a++) { z[a] = rv[a] * Precond(a); pv[a] = z[a]; rz += rv[a] * z[a]; }

        double resid = 0; int it = 0;
        for (; it < maxIter && nf > 0; it++)
        {
            resid = 0;
            for (int a = 0; a < nf; a++) resid = Math.Max(resid, Math.Abs(rv[a]));
            if (resid <= tol * bNorm) break;

            MatVec(pv, ap);
            double pap = 0;
            for (int a = 0; a < nf; a++) pap += pv[a] * ap[a];
            if (!(Math.Abs(pap) > 1e-300)) break;          // 退化：不再前进
            double alpha = rz / pap;
            for (int a = 0; a < nf; a++) { x[a] += alpha * pv[a]; rv[a] -= alpha * ap[a]; }

            double rzNew = 0;
            for (int a = 0; a < nf; a++) { z[a] = rv[a] * Precond(a); rzNew += rv[a] * z[a]; }
            double beta = rz > 1e-300 ? rzNew / rz : 0;
            for (int a = 0; a < nf; a++) pv[a] = z[a] + beta * pv[a];
            rz = rzNew;
        }
        for (int a = 0; a < nf; a++) res.V[free[a]] = x[a];

        res.Iterations = it; res.Residual = resid;
        res.Converged = nf == 0 || resid <= tol * bNorm;

    scale:

        // 归一化电位下的总电流，用来把面通量定标到实际安培
        double inSum = 0, outSum = 0;
        for (int i = 0; i < n; i++)
        {
            if (!isFixed[i]) continue;
            double net = 0;
            foreach (var (c, k) in nbr[i]) net += g[k] * (res.V[i] - res.V[c]);
            if (fixedVal[i] > 0.5) inSum += net; else outSum -= net;
        }
        res.CurrentInA = inSum; res.CurrentOutA = outSum;
        res.ConservationError = inSum > 0 ? Math.Abs(inSum - outSum) / inSum : double.NaN;

        // 定标：归一化解的总电流 inSum（量纲是 σ·t·L/d），实际电流 totalCurrentA
        double scale = inSum > 1e-30 ? totalCurrentA / inSum : 0;

        // ── 由**面法向分量**最小二乘重构单元 J 向量。
        //    面 f 上的法向电流密度  Jn_f = Q_f / (L_f · t_f)   [A/mm²]
        //    对每个单元求 J 使 Σ_f L_f (J·n̂_f − Jn_f)² 最小 ⇒ 解 2×2 正规方程
        //      M = Σ L_f (n̂⊗n̂) ,  b = Σ L_f Jn_f n̂ ,  J = M⁻¹b
        //    （早先用「Σ通量·方向 / Σ(厚度·面长) ×2」的拍脑袋加权，J_mean 差 51%）
        var mxx = new double[n]; var mxz = new double[n]; var mzz = new double[n];
        var bx = new double[n]; var bz = new double[n];
        for (int k = 0; k < m.Faces.Count; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0) continue;
            double q = g[k] * (res.V[f.A] - res.V[f.B]) * scale;   // A→B 的实际电流 [A]
            var dir = m.Centroid[f.B] - m.Centroid[f.A];
            double len = Math.Max(1e-12, dir.Norm);
            double nx = dir.X / len, nz = dir.Z / len;             // A 的外法向
            double L = f.Length;

            // 单元 A：外法向 n̂，法向分量 = +q/(L·t_A)
            // R48 F（2026-09-15 Opus 5）：面上定电位时压接格不重构 J（电流在铜排里走，压接格 J = 0）；自由格一侧照常 ——
            //   形心整格口径下内边那排压接格只拿到一侧面的通量、J 约为邻格一半，那是电极格的离散假象，面上口径下没有这排假象。
            if (clampSet == null || !clampSet[f.A])
            {
                double jnA = q / Math.Max(1e-12, L * m.Thickness[f.A]);
                mxx[f.A] += L * nx * nx; mxz[f.A] += L * nx * nz; mzz[f.A] += L * nz * nz;
                bx[f.A] += L * jnA * nx; bz[f.A] += L * jnA * nz;
            }

            // 单元 B：外法向 −n̂，流出 B 的电流 = −q ⇒ 法向分量 = (−q)/(L·t_B)，法向取 −n̂
            if (clampSet == null || !clampSet[f.B])
            {
                double jnB = -q / Math.Max(1e-12, L * m.Thickness[f.B]);
                mxx[f.B] += L * nx * nx; mxz[f.B] += L * nx * nz; mzz[f.B] += L * nz * nz;
                bx[f.B] += L * jnB * (-nx); bz[f.B] += L * jnB * (-nz);
            }
        }

        double jmax = 0, jmean = 0, aSum = 0;
        for (int i = 0; i < n; i++)
        {
            double det = mxx[i] * mzz[i] - mxz[i] * mxz[i];
            double vx = 0, vz = 0;
            if (Math.Abs(det) > 1e-20)
            {
                vx = (mzz[i] * bx[i] - mxz[i] * bz[i]) / det;
                vz = (mxx[i] * bz[i] - mxz[i] * bx[i]) / det;
            }
            res.JMagAPerMm2[i] = Math.Sqrt(vx * vx + vz * vz);
            if (res.JMagAPerMm2[i] > jmax) { jmax = res.JMagAPerMm2[i]; res.JMaxCell = i; }
            jmean += res.JMagAPerMm2[i] * m.Area[i]; aSum += m.Area[i];
        }
        res.JMaxAPerMm2 = jmax;
        res.JMeanAPerMm2 = aSum > 0 ? jmean / aSum : 0;

        // 焦耳热 W：q = ρe·J²·t·A，J 单位 A/mm² → A/m² 乘 1e6；ρe 单位 Ω·m
        double gen = 0;
        for (int i = 0; i < n; i++)
        {
            double rho = tempC == null ? Materials.PtResistivity(tRefC)
                                       : Materials.PtResistivity(tempC[i]);
            double jSi = res.JMagAPerMm2[i] * 1e6;
            gen += rho * jSi * jSi * (m.Thickness[i] * 1e-3) * (m.Area[i] * 1e-6);
        }
        res.TotalGenW = gen;
        _ = rhoRefOhmMm;
        return res;
    }
}
