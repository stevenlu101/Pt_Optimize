using System;

namespace PtOptimize.Core;

/// <summary>
/// **铜排校核** —— 用户 2026-08-13：「铜排计算也一样」（该加重就加重，做不出来就没意义）。
///
/// 此前模型把铜排当成**完美接触的定温边界**：那条舌片末端被强制按在 300 °C，
/// 不管需要导走多少热。等于假设「你能做到 300 °C」，而不是算出「要做到需要什么」。
/// 本类把它算出来。
///
/// 铜排要同时满足四件事：
///   ① **载流**：自身截面的电流密度在自然/强制冷却下的常规范围内
///   ② **压接**：与铂舌片的接触界面电流密度 ≤ 约 1 A/mm²
///   ③ **导热**：把从铂件传来的热（本项目每片 119–269 W）带到冷端而接触端仍维持设定温度
///   ④ **接触热阻**：压紧力足够，界面温降可控
///
/// ③ 是最容易被忽略的：铜排既是导电体也是**散热器**，它得把那几百瓦带走。
/// 若带不走，接触端温度就会高于设定值，法兰随之变热 —— 整个热平衡跟着漂。
/// </summary>
public static class BusbarSizing
{
    /// <summary>铜的电阻率 Ω·m（20 °C）与温度系数</summary>
    public const double CuRho20 = 1.724e-8, CuAlpha = 3.93e-3;
    /// <summary>铜导热系数 W/(m·K)（约 300 °C）</summary>
    public const double CuK = 385.0;

    public static double CuRho(double tC) => CuRho20 * (1 + CuAlpha * (tC - 20));

    public sealed class Result
    {
        public double CurrentA, HeatW;
        /// <summary>① 载流所需截面 mm²（按给定的许用电流密度）</summary>
        public double SectionForCurrentMm2;
        /// <summary>② 压接所需接触面积 mm² 与对应的压接长度 mm</summary>
        public double ContactAreaMm2, ContactLenMm;
        /// <summary>③ 导热所需截面 mm²：把 HeatW 沿 <see cref="LengthToSinkMm"/> 传到冷端</summary>
        public double SectionForHeatMm2;
        public double LengthToSinkMm, SinkTempC, ClampTempC;
        /// <summary>三者取大 = 实际需要的截面</summary>
        public double SectionRequiredMm2 => Math.Max(SectionForCurrentMm2, SectionForHeatMm2);
        /// <summary>该截面下铜排自身的焦耳热 W（每米）</summary>
        public double CuJouleWPerM;
        public string Note = "";
    }

    /// <summary>
    /// <param name="currentA">该片承载的电流</param>
    /// <param name="heatW">要从铂件带走的热（LineRunner 的 FlangeOut.QClampW）</param>
    /// <param name="jBusAllow">铜排许用电流密度 A/mm²。自然对流 1.5–2，强制风冷 3–4</param>
    /// <param name="jContactAllow">压接界面许用电流密度 A/mm²，常规 ≤1</param>
    /// <param name="tabWidthMm">舌片宽度（= 接触宽度）</param>
    /// <param name="lengthToSinkMm">从压接点到冷端（散热器/环境）的铜排长度</param>
    /// <param name="clampTempC">压接点要维持的温度</param>
    /// <param name="sinkTempC">冷端温度</param>
    /// <param name="doubleSided">是否两面夹（接触面积翻倍）</param>
    /// </summary>
    public static Result Check(double currentA, double heatW,
                               double jBusAllow, double jContactAllow,
                               double tabWidthMm, double lengthToSinkMm,
                               double clampTempC, double sinkTempC,
                               bool doubleSided = true)
    {
        var r = new Result
        {
            CurrentA = currentA, HeatW = heatW,
            LengthToSinkMm = lengthToSinkMm, SinkTempC = sinkTempC, ClampTempC = clampTempC
        };

        // ① 载流
        r.SectionForCurrentMm2 = currentA / Math.Max(1e-9, jBusAllow);

        // ② 压接：接触面积 = 电流 / 许用界面电流密度；长度 = 面积 /(宽 × 面数)
        r.ContactAreaMm2 = currentA / Math.Max(1e-9, jContactAllow);
        r.ContactLenMm = r.ContactAreaMm2 / Math.Max(1e-9, tabWidthMm * (doubleSided ? 2 : 1));

        // ③ 导热：Q = k·A·ΔT/L  ⇒  A = Q·L/(k·ΔT)
        double dT = clampTempC - sinkTempC;
        r.SectionForHeatMm2 = dT > 1e-6
            ? heatW * (lengthToSinkMm * 1e-3) / (CuK * dT) * 1e6
            : double.PositiveInfinity;

        // 该截面下铜排自身发热（每米）—— 它也要一并带走
        double a = r.SectionRequiredMm2 * 1e-6;
        r.CuJouleWPerM = a > 1e-12 ? currentA * currentA * CuRho(clampTempC) / a : 0;

        r.Note = r.SectionForHeatMm2 > r.SectionForCurrentMm2
            ? "★ **导热**是控制项 —— 铜排截面由「把热带走」决定，不是由载流决定"
            : "载流是控制项";
        return r;
    }
}
