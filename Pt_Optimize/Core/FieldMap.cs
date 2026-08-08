using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// 子午面 (x, r) 场。
///
/// 问题是轴对称的，故子午面已是完备表示——绕轴旋转不增加任何信息。
/// 管段温度沿径向均匀（理论模型假设 A1，径向温降 0.54 K），
/// 法兰温度沿轴向均匀（厚度 ≪ 半径跨度），
/// 因此二维场由两个一维解拼装而成，不是独立求解的结果。
/// </summary>
public sealed class MeridionalField
{
    public double[,] V = new double[0, 0];   // [yIndex, xIndex]，NaN = 材料外
    public double XMinMm, XMaxMm;
    /// <summary>纵轴为「显示坐标」0–1，不是真实半径 —— 见 RTickPos / RTickLab</summary>
    public double YMin = 0, YMax = 1;
    public double VMin = double.MaxValue, VMax = double.MinValue;
    public string Label = "", Unit = "";

    /// <summary>纵轴刻度：显示坐标位置 + 对应的真实半径标签</summary>
    public double[] RTickPos = Array.Empty<double>();
    public string[] RTickLab = Array.Empty<string>();

    /// <summary>管壁在显示坐标里的放大倍数（径向不等比，必须在图上标明）</summary>
    public double WallExaggeration = 1;

    /// <summary>几何轮廓（显示坐标），每条为 (x[] mm, y[] 0–1)</summary>
    /// <summary>真实半径 mm → 显示坐标 0–1</summary>
    public Func<double,double> ToY = x => x;

    public (double[] X, double[] R) TubeOutline;
    public (double[] X, double[] R) FlangeOutline;
}

public static class FieldMap
{
    public enum Quantity
    {
        /// <summary>金属温度 °C</summary>
        Temperature,
        /// <summary>电流密度 A/mm²</summary>
        CurrentDensity,
        /// <summary>体积发热率 MW/m³</summary>
        VolumetricHeat
    }

    /// <summary>
    /// 装配法兰—管连接区的子午面场。
    /// </summary>
    /// <param name="xViewMm">轴向视野（从法兰端面算起），建议 4–6 倍热衰减长度</param>
    public static MeridionalField Build(DesignInputs p, SolveResult r, Quantity q,
                                        double xViewMm, int nx = 420, int nr = 260)
    {
        var f = new MeridionalField();
        if (!r.Ok || r.TMetal.Length < 2 || r.FlangeA.R.Length < 2) return f;

        double riTube = p.TubeIdMm * 0.5;
        double roTube = riTube + r.WallDesignMm;
        var fa = r.FlangeA;
        double friMm = fa.R[0] * 1000.0, froMm = fa.R[^1] * 1000.0;

        // 法兰内缘与管外壁之间若有间隙，按管外壁起算（焊接过渡）
        double flangeInner = Math.Min(friMm, roTube);

        f.XMinMm = 0; f.XMaxMm = xViewMm;

        // ── 径向分段拉伸 ──────────────────────────────────────────
        // 管壁 0.6 mm 与法兰 30 mm 差 50 倍，等比画则管壁只有 2 % 高度不可读。
        // 故把管壁映射到显示坐标的下 WallFrac，法兰映射到上面，
        // 并输出真实半径刻度，保证读数不失真。
        const double WallFrac = 0.32;
        double rTop = Math.Max(froMm, roTube + 1);
        double wallSpan = roTube - riTube;
        double flangeSpan = rTop - roTube;
        f.WallExaggeration = flangeSpan > 0 && wallSpan > 0
            ? (WallFrac / wallSpan) / ((1 - WallFrac) / flangeSpan) : 1;

        double ToY(double rMm) => rMm <= roTube
            ? (rMm - riTube) / wallSpan * WallFrac
            : WallFrac + (rMm - roTube) / flangeSpan * (1 - WallFrac);

        double ToR(double y) => y <= WallFrac
            ? riTube + y / WallFrac * wallSpan
            : roTube + (y - WallFrac) / (1 - WallFrac) * flangeSpan;

        var tp = new List<double>(); var tl = new List<string>();
        foreach (var rv in new[] { riTube, roTube })
        { tp.Add(ToY(rv)); tl.Add($"{rv:0.0}"); }
        for (int k = 1; k <= 5; k++)
        {
            double rv = roTube + (rTop - roTube) * k / 5.0;
            tp.Add(ToY(rv)); tl.Add($"{rv:0.0}");
        }
        f.RTickPos = tp.ToArray(); f.RTickLab = tl.ToArray();

        (f.Label, f.Unit) = q switch
        {
            Quantity.Temperature => ("金属温度", "°C"),
            Quantity.CurrentDensity => ("电流密度 J", "A/mm²"),
            _ => ("体积发热率 q_v", "MW/m³")
        };

        var V = new double[nr, nx];
        double dx = (f.XMaxMm - f.XMinMm) / (nx - 1);
        double current = r.CurrentA;
        double aTube = r.TubeAreaMm2 * 1e-6;

        for (int j = 0; j < nr; j++)
        {
            double rr = ToR(j / (double)(nr - 1));   // 显示坐标 → 真实半径
            bool inTubeBand = rr >= riTube - 1e-9 && rr <= roTube + 1e-9;
            bool inFlangeBand = rr >= flangeInner - 1e-9 && rr <= froMm + 1e-9;
            double tfMm = inFlangeBand ? ThicknessAtMm(fa, Math.Max(rr, friMm)) : 0;

            for (int i = 0; i < nx; i++)
            {
                double xx = f.XMinMm + i * dx;
                double val = double.NaN;

                if (inFlangeBand && !inTubeBand && xx <= tfMm)
                {
                    double tMetal = InterpByR(fa.R, fa.T, Math.Max(rr, friMm) * 1e-3);
                    double jj = current / (2.0 * Math.PI * (rr * 1e-3) * (tfMm * 1e-3)); // A/m²
                    val = q switch
                    {
                        Quantity.Temperature => tMetal,
                        Quantity.CurrentDensity => jj * 1e-6,
                        _ => Materials.PtResistivity(tMetal) * jj * jj * 1e-6
                    };
                }
                else if (inTubeBand)
                {
                    double tMetal = InterpByX(r.X, r.TMetal, xx);
                    double jj = current / aTube;
                    val = q switch
                    {
                        Quantity.Temperature => tMetal,
                        Quantity.CurrentDensity => jj * 1e-6,
                        _ => Materials.PtResistivity(tMetal) * jj * jj * 1e-6
                    };
                }

                V[j, i] = val;
                if (!double.IsNaN(val))
                {
                    if (val < f.VMin) f.VMin = val;
                    if (val > f.VMax) f.VMax = val;
                }
            }
        }
        f.V = V;

        // 轮廓（显示坐标）
        f.TubeOutline = (new[] { 0, xViewMm, xViewMm, 0.0, 0 },
                         new[] { ToY(riTube), ToY(riTube), ToY(roTube), ToY(roTube), ToY(riTube) });

        int m = fa.R.Length;
        var ox = new double[2 * m + 1];
        var oy = new double[2 * m + 1];
        for (int k = 0; k < m; k++)                       // 上表面（x = t_f(r)）
        { oy[k] = ToY(fa.R[k] * 1000); ox[k] = fa.Thick[k] * 1000; }
        for (int k = 0; k < m; k++)                       // 下表面（x = 0）回来
        { oy[m + k] = ToY(fa.R[m - 1 - k] * 1000); ox[m + k] = 0; }
        ox[2 * m] = ox[0]; oy[2 * m] = oy[0];
        f.FlangeOutline = (ox, oy);

        f.ToY = ToY;
        return f;
    }

    private static double ThicknessAtMm(FlangeRadialResult fa, double rMm)
        => InterpByR(fa.R, fa.Thick, rMm * 1e-3) * 1000.0;

    private static double InterpByR(double[] xs, double[] ys, double x)
        => Interp(xs, ys, x);

    private static double InterpByX(double[] xs, double[] ys, double x)
        => Interp(xs, ys, x);

    private static double Interp(double[] xs, double[] ys, double x)
    {
        int n = xs.Length;
        if (n == 0) return double.NaN;
        if (x <= xs[0]) return ys[0];
        if (x >= xs[n - 1]) return ys[n - 1];
        double dx = (xs[n - 1] - xs[0]) / (n - 1);          // 等距网格
        int i = (int)((x - xs[0]) / dx);
        i = Math.Clamp(i, 0, n - 2);
        double t = (x - xs[i]) / (xs[i + 1] - xs[i]);
        return ys[i] * (1 - t) + ys[i + 1] * t;
    }
}
