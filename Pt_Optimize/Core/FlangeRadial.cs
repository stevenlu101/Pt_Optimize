using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

public enum FlangeShape
{
    /// <summary>等厚圆盘（长方形剖面）</summary>
    Rectangular,
    /// <summary>线性渐变（梯形剖面）</summary>
    Trapezoid,
    /// <summary>理想 1/r² 渐变（局部自给率 φ 处处为 1）</summary>
    IdealTaper,
    /// <summary>理想渐变在 t_min 处截断（可制造版）</summary>
    IdealClipped
}

public sealed class FlangeRadialResult
{
    public double[] R = Array.Empty<double>();          // m
    public double[] T = Array.Empty<double>();          // °C
    public double[] Thick = Array.Empty<double>();      // m 实际厚度
    public double[] ThickIdeal = Array.Empty<double>(); // m 理想厚度 C/r²
    public double[] Phi = Array.Empty<double>();        // 局部自给率 = t_ideal/t_actual
    public double[] J = Array.Empty<double>();          // A/m²
    public double[] Qv = Array.Empty<double>();         // W/m³

    public double QRootW;            // 从管根抽走的净热流 W（正 = 管子被抽热）
    public double PhiOverall, PGenW, PLossW, MassKg;
    public double TMinC, TMaxC;
    public double ROMaxMm;           // 理想厚度降到 t_min 的半径
    public double QFluxRequired;     // 使 t_min 在 r_i 处自给所需的表面热流 W/m²
    public double IdealMassKg;       // 闭式理想渐变质量
}

/// <summary>
/// 法兰径向模型（变厚度），复用 <see cref="Bvp1D"/> 内核：
///     s = r,  K(r) = k·t_f(r)·r,  S(r,T) = ρe·I²/(4π²·r·t_f) − 2·r·q″(T)
/// 全部量按「每弧度」计，最后乘 2π 还原。
/// </summary>
public static class FlangeRadial
{
    public const int Nodes = 121;

    /// <summary>理想厚度系数 C，使 t_ideal(r) = C/r²（理论模型 §8.12）。</summary>
    public static double IdealCoefficient(DesignInputs p, double current, double tWorkC)
    {
        double flux = FlangeFlux(p, tWorkC);
        if (flux < 1e-9) return 1e9;
        return Materials.PtResistivity(tWorkC) * current * current
               / (8.0 * Math.PI * Math.PI * flux);
    }

    public static double Thickness(DesignInputs p, double r, double cIdeal)
    {
        double ri = p.FlangeRiMm * 1e-3, ro = p.FlangeRoMm * 1e-3;
        double tMin = p.FlangeThickMinMm * 1e-3;
        return p.FlangeShapeMode switch
        {
            FlangeShape.Rectangular => p.FlangeThickMm * 1e-3,
            FlangeShape.Trapezoid => Math.Max(1e-5,
                p.FlangeThickInnerMm * 1e-3 +
                (p.FlangeThickOuterMm - p.FlangeThickInnerMm) * 1e-3 *
                (ro > ri ? (r - ri) / (ro - ri) : 0)),
            FlangeShape.IdealTaper => Math.Max(1e-5, cIdeal / (r * r)),
            _ => Math.Max(tMin, cIdeal / (r * r)),
        };
    }

    /// <summary>法兰表面热流密度 W/m²（单面，含或不含保温）</summary>
    public static double FlangeFlux(DesignInputs p, double tC)
    {
        double charLen = Math.Max(0.005, (p.FlangeRoMm - p.FlangeRiMm) * 1e-3);
        if (!p.FlangeInsulated)
            return Insulation.FlatOuterFlux(tC, p.TAmbC, p.PtEmissivity, charLen, p.FlangeAirVelocityMPerS);

        var layers = new List<InsulationLayer>
        {
            new() { Name = "法兰保温", ThicknessMm = p.FlangeInsulThickMm,
                    K0 = p.Layer1.K0, K1 = p.Layer1.K1,
                    Enabled = p.FlangeInsulThickMm > 1e-6 }
        };
        return Insulation.PlateFlux(tC, p.TAmbC, layers, p.OuterEmissivity, charLen);
    }

    public static LossTable BuildFluxTable(DesignInputs p)
        => new(p.TAmbC, p.TSetC + 200, 50, x => FlangeFlux(p, x));

    public static FlangeRadialResult Solve(DesignInputs p, double current, double tRootC,
                                           LossTable? cachedFlux = null)
    {
        var res = new FlangeRadialResult();
        double ri = p.FlangeRiMm * 1e-3, ro = p.FlangeRoMm * 1e-3;
        if (ro <= ri) return res;

        var flux = cachedFlux ?? BuildFluxTable(p);
        double cIdeal = IdealCoefficient(p, current, p.TSetC);
        double kf = Materials.PtThermalK(p.TSetC);
        double rho = Materials.PtResistivity(p.TSetC);
        double gBus = p.BusbarConductanceWPerK / (2.0 * Math.PI);  // 每弧度
        double tf(double r) => Thickness(p, r, cIdeal);

        double KOf(double r) => kf * tf(r) * r;
        double Src(double r, double T) =>
            rho * current * current / (4.0 * Math.PI * Math.PI * r * tf(r))
            - 2.0 * r * flux.Eval(T);
        double DSrc(double r, double T) => -2.0 * r * flux.Slope(T);

        // 外缘：环带辐射 + 铜排导热（每弧度）
        double FOut(double T) => ro * tf(ro) * flux.Eval(T) + gBus * (T - p.TAmbC);
        double DFOut(double T) => ro * tf(ro) * flux.Slope(T) + gBus;

        var T = Bvp1D.Solve(ri, ro, KOf, Src, DSrc,
            Bvp1D.Boundary.Dirichlet(tRootC),
            Bvp1D.Boundary.WithFlux(FOut, DFOut),
            init: null,
            opt: new Bvp1D.Options { Nodes = Nodes, Relax = 0.6, MaxIter = 200, Tol = 0.005 });

        // 管根抽热：r_i 处沿 +r 的传导热流 = −K·dT/dr，再乘 2π
        double qRoot = -2.0 * Math.PI * Bvp1D.EdgeFlux(ri, ro, KOf, T, atLeft: true);
        // 内孔被玻璃润湿，玻璃向法兰回灌
        qRoot -= p.HGlass * (2.0 * Math.PI * ri * tf(ri)) * (p.TGlassInC - T[0]);

        int n = T.Length;
        double dr = (ro - ri) / (n - 1);
        var R = new double[n]; var th = new double[n]; var tid = new double[n];
        var phi = new double[n]; var jj = new double[n]; var qv = new double[n];
        double pGen = 0, pLoss = 0, mass = 0;

        for (int i = 0; i < n; i++)
        {
            double r = ri + i * dr, w = (i == 0 || i == n - 1) ? dr * 0.5 : dr;
            R[i] = r; th[i] = tf(r); tid[i] = cIdeal / (r * r);
            phi[i] = tid[i] / Math.Max(1e-9, th[i]);
            jj[i] = current / (2.0 * Math.PI * r * th[i]);
            qv[i] = Materials.PtResistivity(T[i]) * jj[i] * jj[i];
            pGen += qv[i] * (2.0 * Math.PI * r * th[i]) * w;
            pLoss += 2.0 * flux.Eval(T[i]) * (2.0 * Math.PI * r) * w;
            mass += Materials.PtDensity * 2.0 * Math.PI * r * th[i] * w;
        }

        double tMin = p.FlangeThickMinMm * 1e-3;
        res.R = R; res.T = T; res.Thick = th; res.ThickIdeal = tid;
        res.Phi = phi; res.J = jj; res.Qv = qv;
        res.QRootW = qRoot; res.PGenW = pGen; res.PLossW = pLoss;
        res.PhiOverall = pLoss > 0 ? pGen / pLoss : 0;
        res.MassKg = mass;
        res.TMinC = double.MaxValue; res.TMaxC = double.MinValue;
        for (int i = 0; i < n; i++)
        { res.TMinC = Math.Min(res.TMinC, T[i]); res.TMaxC = Math.Max(res.TMaxC, T[i]); }
        res.ROMaxMm = Math.Sqrt(cIdeal / tMin) * 1000.0;
        res.QFluxRequired = rho * current * current
                            / (8.0 * Math.PI * Math.PI * ri * ri * tMin);
        res.IdealMassKg = Materials.PtDensity * rho * current * current
                          * Math.Log(ro / ri) / (4.0 * Math.PI * FlangeFlux(p, p.TSetC));
        return res;
    }

    /// <summary>法兰在无管根导热时的自平衡温度（Φ = 1 的温度）。</summary>
    public static double FloatTemp(DesignInputs p, double current, LossTable? flux = null)
    {
        var f = flux ?? BuildFluxTable(p);
        return Roots.Smooth(t =>
        {
            var r = Solve(p, current, t, f);
            return r.PGenW - r.PLossW;
        }, p.TAmbC + 5, p.TSetC + 150, 0.05, 60);
    }
}
