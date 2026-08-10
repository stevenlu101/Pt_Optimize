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
    public double Residual;
}

public static class ShellCurrent
{
    /// <summary>
    /// 解电位场。边界：<see cref="ShellMesh.TagTabEnd"/> 取 V=1，
    /// <see cref="ShellMesh.TagHole"/> 取 V=0，其余自然 Neumann（零通量，无需显式处理）。
    /// </summary>
    /// <param name="rhoRefOhmMm">参考电阻率 Ω·mm（= Ω·m × 1000）</param>
    /// <param name="tempC">可选单元温度，用于 σ(T)；null 则等温</param>
    public static ShellCurrentResult Solve(ShellMesh m, double totalCurrentA,
                                           double rhoRefOhmMm, double tRefC = 1300,
                                           double[]? tempC = null,
                                           int maxIter = 20000, double tol = 1e-9)
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
        for (int i = 0; i < n; i++) if (isFixed[i]) res.V[i] = fixedVal[i];

        // 面传导系数 G = σ_f · t_f · L / d   （调和平均取界面值，厚度突变处才不会失真）
        var g = new double[m.Faces.Count];
        for (int k = 0; k < m.Faces.Count; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0 || f.DistAB < 1e-12) continue;
            double sA = sig[f.A] * m.Thickness[f.A], sB = sig[f.B] * m.Thickness[f.B];
            double sf = (sA * sB) > 0 ? 2 * sA * sB / (sA + sB) : 0;   // 调和平均
            g[k] = sf * f.Length / f.DistAB;
        }

        // Gauss–Seidel + SOR。n 约数千，直接迭代足够；换更大网格再上 CG。
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

        const double omega = 1.7;
        double resid = 0;
        int it = 0;
        for (; it < maxIter; it++)
        {
            resid = 0;
            for (int i = 0; i < n; i++)
            {
                if (isFixed[i] || diag[i] <= 0) continue;
                double s = 0;
                foreach (var (c, k) in nbr[i]) s += g[k] * res.V[c];
                double vNew = s / diag[i];
                double d = vNew - res.V[i];
                res.V[i] += omega * d;
                resid = Math.Max(resid, Math.Abs(d));
            }
            if (resid < tol) { it++; break; }
        }
        res.Iterations = it; res.Residual = resid;

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
            double jnA = q / Math.Max(1e-12, L * m.Thickness[f.A]);
            mxx[f.A] += L * nx * nx; mxz[f.A] += L * nx * nz; mzz[f.A] += L * nz * nz;
            bx[f.A] += L * jnA * nx; bz[f.A] += L * jnA * nz;

            // 单元 B：外法向 −n̂，流出 B 的电流 = −q ⇒ 法向分量 = (−q)/(L·t_B)，法向取 −n̂
            double jnB = -q / Math.Max(1e-12, L * m.Thickness[f.B]);
            mxx[f.B] += L * nx * nx; mxz[f.B] += L * nx * nz; mzz[f.B] += L * nz * nz;
            bx[f.B] += L * jnB * (-nx); bz[f.B] += L * jnB * (-nz);
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
