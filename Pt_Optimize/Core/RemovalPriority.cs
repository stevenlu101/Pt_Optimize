using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **场决定该挖哪里**（R12，用户 2026-09-08 设计因果链第 ③ 步：
/// 「分析舌片与法兰的温场与电场，电流密度低的区域就是定位孔的形状与位置」）。
///
/// 算法与 <c>RemovalPriorityTests</c>（2026-09-05，deliverable/移除优先级.txt）**同一份**，
/// 那条测试现在直接调这里 —— 不另立一套：
/// <code>
///   移除优先级 P = 导热贡献 q [W] ÷ 电流密度 J [A/mm²]
/// </code>
/// · **导热贡献** q：该单元各内部面上传导热流的绝对值之和 ÷ 2（有限体积通量；每条面被两个单元各算一次）。
///   大 = 它在「法兰从管子抽热」的主路径上 ⇒ 挖掉它，③（法兰增量温降）直接改善。
/// · **电流密度代价** J：挖掉高 J 的单元会把电流挤到旁边 ⇒ 峰值 J 升高。
///
/// 输入只有**最新收敛的场**（<see cref="FlangeOut.Mesh"/>／<see cref="FlangeOut.JField"/>／<see cref="FlangeOut.TField"/>），
/// 不读任何设计记录、不接受起点 —— 场变了位置就跟着变，每轮开头重算一次（<c>Solver.FieldPlacement</c>）。
///
/// ⚠ 这里只给「**哪个角向／哪个 x 最该挖**」，不给孔的大小 —— 大小仍是旋钮，由比价定。
/// </summary>
public static class RemovalPriority
{
    /// <summary>
    /// 逐单元的移除优先级 P = q/(J+ε)。非法兰单元（Part≠0）或零面积单元为 NaN。
    /// <paramref name="tRootC"/>：导热系数取管根温度处的 k（与 2026-09-05 的量法一致：整片用一个 k）。
    /// </summary>
    public static double[] Compute(ShellMesh mesh, double[] jField, double[] tField, double tRootC)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        int n = mesh.CellCount;
        if (jField is null || tField is null || jField.Length < n || tField.Length < n)
            throw new ArgumentException($"场的长度（J {jField?.Length ?? 0}／T {tField?.Length ?? 0}）与网格单元数 {n} 对不上 —— 不是这张网格的场");

        var q = ConductionW(mesh, tField, tRootC);

        double jMax = 0;
        for (int i = 0; i < n; i++)
            if (mesh.Part[i] == 0 && mesh.Area[i] > 0 && !double.IsNaN(jField[i])) jMax = Math.Max(jMax, jField[i]);
        double jEps = jMax * 1e-3;

        var p = new double[n];
        for (int i = 0; i < n; i++)
            p[i] = mesh.Part[i] == 0 && mesh.Area[i] > 0 && !double.IsNaN(jField[i])
                 ? q[i] / (jField[i] + jEps)
                 : double.NaN;
        return p;
    }

    /// <summary>逐单元的导热贡献 q [W]：各内部面上传导热流的绝对值之和 ÷ 2。<see cref="Compute"/> 的分子；报表也要印它，所以单独给。</summary>
    public static double[] ConductionW(ShellMesh mesh, double[] tField, double tRootC)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        int n = mesh.CellCount;
        if (tField is null || tField.Length < n) throw new ArgumentException("温度场与网格对不上", nameof(tField));
        double k = Materials.PtThermalK(tRootC);      // W/(m·K)
        var q = new double[n];
        foreach (var f in mesh.Faces)
        {
            if (f.A < 0 || f.B < 0) continue;            // 边界面不算「内部导热」
            if (f.DistAB <= 1e-9) continue;
            double tMm = 0.5 * (mesh.Thickness[f.A] + mesh.Thickness[f.B]);
            // k[W/(m·K)] × 1e-3 → W/(mm·K)；截面 = 边长 × 厚度 [mm²]
            double flux = Math.Abs(k * 1e-3 * f.Length * tMm * (tField[f.A] - tField[f.B]) / f.DistAB);
            q[f.A] += 0.5 * flux; q[f.B] += 0.5 * flux;
        }
        return q;
    }

    /// <summary>
    /// ★ **圆盘槽的槽心角**：在槽带 [rIn, rOut] 内，找面积加权平均优先级最高的角向（1° 步）。
    /// 窗口 ±<paramref name="halfWindowDeg"/>：槽是一段弧，看的是一段角向的平均，不是单个单元的尖峰
    /// （单元级的最高点在对称场里成对出现在 ±θ，单看一个会随网格噪声左右跳）。
    ///
    /// 同分（差 &lt; 0.1 %）时取 |θ| 最小的、再取正角 —— 对称场下答案才是**确定的**：
    /// 单舌片背对舌片是 0°；双舌对称进电时两舌各在 0°/180°，最该挖的在 ±90°，取 +90°。
    /// 返回 (角度 °，落在 (−180, 180]；分数)。带内没有单元 ⇒ (NaN, NaN)。
    /// </summary>
    public static (double Deg, double Score) SlotCenterDeg(ShellMesh mesh, double[] p,
                                                           double rInMm, double rOutMm,
                                                           double halfWindowDeg = 15.0)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        if (p is null || p.Length < mesh.CellCount) throw new ArgumentException("优先级数组与网格对不上", nameof(p));
        var cells = new List<(double Deg, double A, double P)>();
        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (double.IsNaN(p[i])) continue;
            var c = mesh.Centroid[i];
            double r = Math.Sqrt(c.X * c.X + c.Z * c.Z);
            if (r < rInMm || r > rOutMm) continue;
            cells.Add((Math.Atan2(c.Z, c.X) * 180.0 / Math.PI, mesh.Area[i], p[i]));
        }
        if (cells.Count == 0) return (double.NaN, double.NaN);

        var score = new double[360];
        for (int t = 0; t < 360; t++)
        {
            double sPA = 0, sA = 0;
            foreach (var (deg, a, pv) in cells)
            {
                // ★ 角差先按 360 取模再折到 [0,180]：deg ∈ (−180,180]、t ∈ [0,360)，直接相减可到 −540 ——
                //   第一版 `360 − |Δ|` 在 |Δ| > 360 时变成负数，于是 −177° 的单元被算进 359° 的窗口，
                //   W08 对帐第 1 轮印出「槽心 −1°」而带内单元全在 ±180° 一带（FieldPlacementTests 的不变式门抓到）。
                double d = Math.Abs(deg - t) % 360.0; if (d > 180) d = 360 - d;
                if (d <= halfWindowDeg) { sPA += pv * a; sA += a; }
            }
            score[t] = sA > 0 ? sPA / sA : double.NegativeInfinity;
        }
        double best = score.Max();
        if (double.IsNegativeInfinity(best)) return (double.NaN, double.NaN);
        double bestDeg = double.NaN, bestAbs = double.PositiveInfinity;
        for (int t = 0; t < 360; t++)
        {
            if (!(score[t] >= best * (1 - 1e-3) - 1e-12)) continue;
            double deg = t > 180 ? t - 360 : t;              // (−180, 180]
            double abs = Math.Abs(deg);
            if (abs < bestAbs - 1e-9 || (Math.Abs(abs - bestAbs) < 1e-9 && deg > bestDeg))
            { bestAbs = abs; bestDeg = deg; }
        }
        return (bestDeg, best);
    }

    /// <summary>
    /// ★ **舌孔孔心 x**：在自由段 [xFrom, xTo] 内（<paramref name="stepMm"/> 步），找面积加权平均优先级最高的位置。
    /// 窗口 ±<paramref name="halfWindowMm"/>（孔有大小，看的是一段舌片的平均）。
    /// 同分时取**靠圆盘**的那个（x 大）—— 离管子近、截断的热流多；这条只在同分时起作用。
    /// 返回 (x mm，分数)。段内没有单元 ⇒ (NaN, NaN)。
    /// </summary>
    public static (double XMm, double Score) TabHoleXMm(ShellMesh mesh, double[] p,
                                                        double xFromMm, double xToMm,
                                                        double halfWindowMm = 5.0, double stepMm = 0.5)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        if (p is null || p.Length < mesh.CellCount) throw new ArgumentException("优先级数组与网格对不上", nameof(p));
        if (xToMm < xFromMm || !(stepMm > 0)) return (double.NaN, double.NaN);     // xFrom == xTo：只量这一点（剖面表用）
        var cells = new List<(double X, double A, double P)>();
        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (double.IsNaN(p[i])) continue;
            var c = mesh.Centroid[i];
            if (c.X < xFromMm - halfWindowMm || c.X > xToMm + halfWindowMm) continue;
            cells.Add((c.X, mesh.Area[i], p[i]));
        }
        if (cells.Count == 0) return (double.NaN, double.NaN);

        double best = double.NegativeInfinity, bestX = double.NaN;
        int steps = (int)Math.Floor((xToMm - xFromMm) / stepMm + 1e-9);
        for (int s = 0; s <= steps; s++)
        {
            double xc = xFromMm + s * stepMm;
            double sPA = 0, sA = 0;
            foreach (var (x, a, pv) in cells)
                if (Math.Abs(x - xc) <= halfWindowMm) { sPA += pv * a; sA += a; }
            if (!(sA > 0)) continue;
            double sc = sPA / sA;
            if (sc > best * (1 + 1e-3) + 1e-12 || (Math.Abs(sc - best) <= Math.Abs(best) * 1e-3 + 1e-12 && xc > bestX))
            { best = sc; bestX = xc; }
        }
        return (bestX, best);
    }

    /// <summary>
    /// ★ **当地电流方向**（R13 长椭圆的长轴要顺着它）：离 (x,z) 最近的法兰单元上，
    /// 用相邻单元的电位差做最小二乘梯度 ∇V，电流方向 = −∇V（<c>J = −σ∇V</c>）。
    /// 返回 (方向角 °，梯度模)；梯度退化（邻居不足）时角度 NaN。
    /// 调用方拿梯度模与 <see cref="MaxGradient"/> 比：电流几乎不走的地方（圆盘背侧）方向是噪声，不该用。
    /// </summary>
    public static (double Deg, double GradMag) CurrentDirectionDeg(ShellMesh mesh, double[] v, double xMm, double zMm)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        if (v is null || v.Length < mesh.CellCount) throw new ArgumentException("电位数组与网格对不上", nameof(v));
        int c = -1; double dBest = double.PositiveInfinity;
        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (mesh.Part[i] != 0 || !(mesh.Area[i] > 0)) continue;
            double dx = mesh.Centroid[i].X - xMm, dz = mesh.Centroid[i].Z - zMm;
            double d2 = dx * dx + dz * dz;
            if (d2 < dBest) { dBest = d2; c = i; }
        }
        if (c < 0) return (double.NaN, double.NaN);
        var (gx, gz, ok) = GradientAt(mesh, v, c);
        if (!ok) return (double.NaN, double.NaN);
        double mag = Math.Sqrt(gx * gx + gz * gz);
        if (!(mag > 0)) return (double.NaN, 0);
        return (Math.Atan2(-gz, -gx) * 180.0 / Math.PI, mag);
    }

    /// <summary>全片法兰单元上 |∇V| 的最大值 —— 判「这里的电流方向靠不靠得住」的尺。</summary>
    public static double MaxGradient(ShellMesh mesh, double[] v)
    {
        if (mesh is null) throw new ArgumentNullException(nameof(mesh));
        if (v is null || v.Length < mesh.CellCount) throw new ArgumentException("电位数组与网格对不上", nameof(v));
        double m = 0;
        for (int i = 0; i < mesh.CellCount; i++)
        {
            if (mesh.Part[i] != 0 || !(mesh.Area[i] > 0)) continue;
            var (gx, gz, ok) = GradientAt(mesh, v, i);
            if (ok) m = Math.Max(m, Math.Sqrt(gx * gx + gz * gz));
        }
        return m;
    }

    /// <summary>单元 c 上的最小二乘梯度（用所有内部面的邻居）。邻居不足两个方向 ⇒ ok=false。</summary>
    private static (double Gx, double Gz, bool Ok) GradientAt(ShellMesh mesh, double[] v, int c)
    {
        // 正规方程：Σ d dᵀ g = Σ d·dv
        double axx = 0, axz = 0, azz = 0, bx = 0, bz = 0; int cnt = 0;
        foreach (var f in mesh.Faces)
        {
            if (f.A < 0 || f.B < 0) continue;
            int nb = f.A == c ? f.B : f.B == c ? f.A : -1;
            if (nb < 0 || mesh.Part[nb] != 0) continue;
            double dx = mesh.Centroid[nb].X - mesh.Centroid[c].X, dz = mesh.Centroid[nb].Z - mesh.Centroid[c].Z;
            double dv = v[nb] - v[c];
            axx += dx * dx; axz += dx * dz; azz += dz * dz; bx += dx * dv; bz += dz * dv; cnt++;
        }
        double det = axx * azz - axz * axz;
        if (cnt < 2 || Math.Abs(det) < 1e-12) return (0, 0, false);
        return ((bx * azz - bz * axz) / det, (axx * bz - axz * bx) / det, true);
    }
}
