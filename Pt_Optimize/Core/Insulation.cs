using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace PtOptimize.Core;

/// <summary>单层保温/结构层。导热系数按 k = K0 + K1*T̄ 线性温变。</summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class InsulationLayer
{
    [DisplayName("启用"), Description("关闭则该层不参与计算")]
    public bool Enabled { get; set; } = true;

    [DisplayName("名称")]
    public string Name { get; set; } = "层";

    [DisplayName("厚度 [mm]")]
    public double ThicknessMm { get; set; } = 10.0;

    [DisplayName("k0 [W/m·K]"), Description("导热系数常数项")]
    public double K0 { get; set; } = 0.04;

    [DisplayName("k1 [W/m·K²]"), Description("导热系数温度斜率，k = k0 + k1·T̄(°C)")]
    public double K1 { get; set; } = 3.0e-4;

    // ── 以下两项只在**升温**核算里用到（稳态解只需要导热系数）。
    //    默认值是典型值，不是实测：高纯氧化铝纤维毡 ~200 kg/m³、致密氧化铝套管 ~3000 kg/m³，
    //    两者比热在 1000 °C 附近都约 1000～1100 J/(kg·K)。
    //    ★ 升温时间对这两个数敏感（保温层热容占比大于铂本身），有实测值请替换。
    [DisplayName("密度 [kg/m³]"), Description("仅升温核算用。纤维毡约 200，致密氧化铝约 3000。默认为典型值，非实测")]
    public double DensityKgM3 { get; set; } = 200.0;

    [DisplayName("比热 [J/kg·K]"), Description("仅升温核算用。氧化铝类约 1000～1100。默认为典型值，非实测")]
    public double CpJKgK { get; set; } = 1050.0;

    public double KAt(double tMeanC) => Math.Max(1e-4, K0 + K1 * tMeanC);

    public override string ToString()
        => Enabled ? $"{Name} {ThicknessMm:0.#}mm" : $"{Name} (关)";
}

public sealed class SurfaceLossResult
{
    /// <summary>单位长度热损失 W/m</summary>
    public double QPerLength;
    /// <summary>最外表面温度 °C</summary>
    public double TOuterC;
    /// <summary>最外半径 m</summary>
    public double ROuter;
    /// <summary>各界面温度 °C，[0]=内表面(金属)，末项=外表面</summary>
    public double[] InterfaceT = Array.Empty<double>();
}

public static class Insulation
{
    /// <summary>
    /// 多层圆筒径向导热 + 外表面辐射/对流，求单位长度热损失。
    /// tInnerC 为金属温度（铂壁薄且 k 高，径向温降可忽略）。
    /// </summary>
    public static SurfaceLossResult CylinderLoss(
        double tInnerC, double tAmbC, double rInner,
        IReadOnlyList<InsulationLayer> layers,
        double epsOuter, bool vertical, double verticalLength)
    {
        var active = new List<InsulationLayer>();
        foreach (var l in layers)
            if (l.Enabled && l.ThicknessMm > 1e-6) active.Add(l);

        // 半径序列
        int n = active.Count;
        var r = new double[n + 1];
        r[0] = rInner;
        for (int i = 0; i < n; i++) r[i + 1] = r[i] + active[i].ThicknessMm * 1e-3;
        double rOut = r[n];

        // 裸露：外表面即金属表面
        if (n == 0)
        {
            double q0 = OuterFlux(tInnerC, tAmbC, epsOuter, rOut, vertical, verticalLength)
                        * 2.0 * Math.PI * rOut;
            return new SurfaceLossResult
            {
                QPerLength = q0,
                TOuterC = tInnerC,
                ROuter = rOut,
                InterfaceT = new[] { tInnerC }
            };
        }

        // 初值：界面温度线性分布
        var tI = new double[n + 1];
        for (int i = 0; i <= n; i++) tI[i] = tInnerC + (tAmbC - tInnerC) * i / (double)n * 0.8;

        double q = 0;
        for (int iter = 0; iter < 40; iter++)
        {
            // 各层热阻（用当前界面温度算平均温度下的 k）
            var rth = new double[n];
            double sumR = 0;
            for (int i = 0; i < n; i++)
            {
                double tMean = 0.5 * (tI[i] + tI[i + 1]);
                rth[i] = Math.Log(r[i + 1] / r[i]) / (2.0 * Math.PI * active[i].KAt(tMean));
                sumR += rth[i];
            }

            // 对外表面温度做二分：内侧导热流 == 外侧散热流
            double lo = tAmbC, hi = tInnerC, tOut = tI[n];
            for (int b = 0; b < 80; b++)
            {
                tOut = 0.5 * (lo + hi);
                double qIn = (tInnerC - tOut) / sumR;
                double qOut = OuterFlux(tOut, tAmbC, epsOuter, rOut, vertical, verticalLength)
                              * 2.0 * Math.PI * rOut;
                if (qIn > qOut) lo = tOut; else hi = tOut;
            }

            q = (tInnerC - tOut) / sumR;

            // 由 q 反推各界面温度
            var tNew = new double[n + 1];
            tNew[0] = tInnerC;
            for (int i = 0; i < n; i++) tNew[i + 1] = tNew[i] - q * rth[i];

            double err = 0;
            for (int i = 0; i <= n; i++) err = Math.Max(err, Math.Abs(tNew[i] - tI[i]));
            // 欠松弛，保证数值稳定
            for (int i = 0; i <= n; i++) tI[i] = 0.5 * tI[i] + 0.5 * tNew[i];
            if (err < 1e-5) break;
        }

        return new SurfaceLossResult
        {
            QPerLength = Math.Max(0, q),
            TOuterC = tI[n],
            ROuter = rOut,
            InterfaceT = tI
        };
    }

    /// <summary>外表面热流密度 W/m²（辐射 + 自然对流）</summary>
    public static double OuterFlux(double tSurfC, double tAmbC, double eps,
                                   double rOuter, bool vertical, double verticalLength)
    {
        double hr = Materials.HRad(eps, tSurfC, tAmbC);
        double hc = vertical
            ? Materials.HConvVertical(tSurfC, tAmbC, Math.Max(0.05, verticalLength))
            : Materials.HConvHorizCylinder(tSurfC, tAmbC, Math.Max(0.005, 2.0 * rOuter));
        return (hr + hc) * (tSurfC - tAmbC);
    }

    /// <summary>
    /// 平板（法兰盘面）多层损失，返回热流密度 W/m²。
    /// </summary>
    public static double PlateFlux(double tInnerC, double tAmbC,
                                   IReadOnlyList<InsulationLayer> layers,
                                   double epsOuter, double charLength)
    {
        double sumR = 0;
        double tPrev = tInnerC;
        var active = new List<InsulationLayer>();
        foreach (var l in layers) if (l.Enabled && l.ThicknessMm > 1e-6) active.Add(l);

        if (active.Count == 0)
            return Insulation.FlatOuterFlux(tInnerC, tAmbC, epsOuter, charLength);

        // 迭代 k(T)
        double tOut = 0.5 * (tInnerC + tAmbC);
        double q = 0;
        for (int iter = 0; iter < 30; iter++)
        {
            sumR = 0; tPrev = tInnerC;
            double tGuessOut = tOut;
            for (int i = 0; i < active.Count; i++)
            {
                double frac = (i + 0.5) / active.Count;
                double tMean = tInnerC + (tGuessOut - tInnerC) * frac;
                sumR += (active[i].ThicknessMm * 1e-3) / active[i].KAt(tMean);
            }
            double lo = tAmbC, hi = tInnerC;
            for (int b = 0; b < 80; b++)
            {
                tOut = 0.5 * (lo + hi);
                double qIn = (tInnerC - tOut) / sumR;
                double qOut = FlatOuterFlux(tOut, tAmbC, epsOuter, charLength);
                if (qIn > qOut) lo = tOut; else hi = tOut;
            }
            q = (tInnerC - tOut) / sumR;
        }
        _ = tPrev;
        return Math.Max(0, q);
    }

    public static double FlatOuterFlux(double tSurfC, double tAmbC, double eps, double charLength,
                                       double airVelocity = 0)
    {
        double hr = Materials.HRad(eps, tSurfC, tAmbC);
        double l = Math.Max(0.02, charLength);
        double hn = Materials.HConvVertical(tSurfC, tAmbC, l);
        double hf = Materials.HConvForcedPlate(tSurfC, tAmbC, l, airVelocity);
        return (hr + Materials.HConvMixed(hn, hf)) * (tSurfC - tAmbC);
    }
}
