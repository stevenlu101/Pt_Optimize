using System;

namespace PtOptimize.Core;

/// <summary>
/// 法兰厚度分布反设计。
///
/// 均匀厚度必须按 J_max 定尺，而 J 在法兰上分布很不均（本几何 6.4 ~ 10.0 A/mm²），
/// 于是低 J 区的材料被白白加厚。最省铂的分布是让电流密度处处刚好等于许用值：
///
///     |J(x,z)| = J_allow   ⇔   t(x,z) = |K(x,z)| / J_allow
///
/// 其中 K = J·t 是面电流密度 [A/mm]。由于改 t 会反过来改 K，须迭代：
///
///     ① 以当前 t 解 ∇·(σt∇V)=0，得 K
///     ② t ← max( t_min , |K| / J_allow )
///     ③ 欠松弛，回 ①
///
/// 收敛后所有 t > t_min 的区域 J 恰为 J_allow，其余受可制造下限约束。
/// 这是二维版的理论模型 §8.12「理想渐变」。
/// </summary>
public sealed class ThicknessDesignResult
{
    public double[,] Thick = new double[0, 0];   // mm
    public PlateField Field = new();
    public double MassG;                          // 单片铂重
    public double MassUniformG;                   // 同等约束下的均匀厚度铂重（对照）
    public double TMinMm, TMaxMm, TMeanMm;
    public double JMaxAPerMm2;
    public double AreaAtFloorPct;                 // 受 t_min 约束的面积占比
    public int Iterations;
    public double Delta;
    public bool Converged;
}

public static class FlangeThicknessDesign
{
    public static ThicknessDesignResult Solve(
        FlangePlate g, DesignInputs p, double currentA, double tMinMm,
        double[,]? tempField = null, double h = 1.0,
        int maxIter = 30, double tolMm = 2e-3)
    {
        var res = new ThicknessDesignResult();
        double jAllow = p.JAllowAPerMm2;

        // 初值：均匀 t_min
        var probe = PlateCurrent2D.Solve(g, currentA, Materials.PtResistivity(p.TSetC), h,
                                         tempField: tempField, tRefC: p.TSetC);
        int nx = probe.Nx, nz = probe.Nz;
        var t = new double[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                t[i, j] = probe.Mask[i, j] ? tMinMm : 0;

        PlateField f = probe;
        int it = 0; double delta = 0;
        for (; it < maxIter; it++)
        {
            f = PlateCurrent2D.Solve(g, currentA, Materials.PtResistivity(p.TSetC), h,
                                     tempField: tempField, tRefC: p.TSetC, thickField: t);
            delta = 0;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    if (!f.Mask[i, j]) continue;
                    double want = Math.Max(tMinMm, f.Sheet[i, j] / jAllow);
                    delta = Math.Max(delta, Math.Abs(want - t[i, j]));
                    t[i, j] = 0.5 * t[i, j] + 0.5 * want;      // 欠松弛
                }
            if (delta < tolMm) { res.Converged = true; it++; break; }
        }

        // 汇总
        double hh = f.H, area = 0, vol = 0, tmin = double.MaxValue, tmax = 0, tsum = 0;
        int n = 0, nFloor = 0;
        double jmax = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                if (!f.Mask[i, j]) continue;
                area += hh * hh;
                vol += t[i, j] * hh * hh;
                tmin = Math.Min(tmin, t[i, j]); tmax = Math.Max(tmax, t[i, j]);
                tsum += t[i, j]; n++;
                if (t[i, j] <= tMinMm * 1.02) nFloor++;
                jmax = Math.Max(jmax, f.Jmag[i, j]);
            }

        res.Thick = t; res.Field = f;
        res.MassG = vol * Materials.PtDensity * 1e-6;
        res.TMinMm = tmin; res.TMaxMm = tmax; res.TMeanMm = n > 0 ? tsum / n : 0;
        res.JMaxAPerMm2 = jmax;
        res.AreaAtFloorPct = n > 0 ? nFloor * 100.0 / n : 0;
        res.Iterations = it; res.Delta = delta;

        // 对照：同一电流下均匀厚度必须取 t_uni = t_ref·(J_max_ref/J_allow)
        double tUni = Math.Max(tMinMm, g.ThicknessMm * (probe.JMaxAPerMm2 / jAllow));
        res.MassUniformG = area * tUni * Materials.PtDensity * 1e-6;
        return res;
    }
}
