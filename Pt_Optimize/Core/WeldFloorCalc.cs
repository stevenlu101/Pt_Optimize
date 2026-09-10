using System;
using System.Collections.Generic;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★ R39（2026-09-11，用户：「焊接屈曲下界作为判据行做一个独立于系统外的计算小工具」）：
/// **焊接下界小算盘** —— 独立于求解链之外的一个纯计算，输入几个数、当场给出圆盘板厚的工艺下界。
///
/// 为什么不做成判据行：屈曲下界早就在 <see cref="DesignSpec.DiscFloorMm"/> 里当**约束盒的下角**用
/// （求解器从它起步、只会往上抬），判据表里再列一行永远是「过」，只会多一行噪音。
/// 工程师真正要的是「换个盘径／换个焊法，下界是多少、由哪条控制」—— 那是一张算盘，不是一条判据。
///
/// 公式与系数**全部来自** <see cref="WeldDistortion"/>（屈曲：t_min = 斜率 × 无支撑宽度 b；烧穿：现场给的工艺硬底），
/// 无支撑宽度 b = 盘半径 − 管孔半径，管孔半径 = 管内径/2 + 管壁 —— 与 <see cref="DesignSpec.DiscFloorMm"/> 同一口径，
/// <c>WeldFloorCalcTests</c> 钉着两边逐位相等。
/// </summary>
public static class WeldFloorCalc
{
    public sealed class Inputs
    {
        public double DiscDiaMm = 60;
        public double TubeIdMm = 50;
        public double WallMm = 0.8;
        /// <summary>焊接下界安全系数（参数表「焊接下界安全系数」，预设 2）</summary>
        public double SafetyFactor = 2.0;
        /// <summary>烧穿下界 mm（参数表「焊接工艺最小厚度」，手工 TIG 0.6；由现场焊工／工艺给，算不出来）</summary>
        public double BurnThroughMm = 0.6;
        public double StartTempC = 20;
        /// <summary>焊道宽/板厚比 β（自熔对接 1.5–3）</summary>
        public double Beta = WeldDistortion.BeadWidthRatio;
        /// <summary>熔化效率 η_melt（电弧焊 0.3–0.5）</summary>
        public double EtaMelt = WeldDistortion.MeltEfficiency;
        /// <summary>板屈曲系数 k_b：法兰盘（内边焊死、外边自由）取 0.43；四边简支长板 4.0 只作对照</summary>
        public double Kb = WeldDistortion.PlateBucklingKFreeEdge;
    }

    public sealed class Outputs
    {
        public double HoleRadiusMm, WidthMm, SlopePerB;
        public double BucklingRawMm, BucklingWithSfMm, BurnThroughMm, FloorMm;
        /// <summary>「屈曲」或「烧穿」—— 取大之后哪条在控制</summary>
        public string Governing = "";
        /// <summary>管侧：环缝会不会把筒压屈（&lt;1 = 任何壁厚都不会）</summary>
        public double ShellRatio;
        /// <summary>熔池被表面张力托住的最大宽度 mm（说明「烧穿」不是这个机理）</summary>
        public double CapillaryMm;
        public string Explain = "";
    }

    public static Outputs Compute(Inputs i)
    {
        if (i is null) throw new ArgumentNullException(nameof(i));
        double holeR = i.TubeIdMm * 0.5 + i.WallMm;
        double b = Math.Max(0, i.DiscDiaMm * 0.5 - holeR);
        var r = WeldDistortion.ForPt(b, i.StartTempC, i.Beta, i.EtaMelt, i.Kb);
        double raw = r.TMinMm, sf = raw * i.SafetyFactor;
        double floor = Math.Max(sf, i.BurnThroughMm);
        var o = new Outputs
        {
            HoleRadiusMm = holeR, WidthMm = b, SlopePerB = r.SlopePerB,
            BucklingRawMm = raw, BucklingWithSfMm = sf, BurnThroughMm = i.BurnThroughMm, FloorMm = floor,
            Governing = sf >= i.BurnThroughMm ? "屈曲" : "烧穿",
            ShellRatio = WeldDistortion.PtShellBucklingRatio(i.StartTempC),
            CapillaryMm = WeldDistortion.CapillaryWidthMm(),
        };
        var sb = new StringBuilder();
        sb.AppendLine($"管孔半径 = 管内径/2 + 管壁 = {i.TubeIdMm:0.0}/2 + {i.WallMm:0.00} = {holeR:0.00} mm");
        sb.AppendLine($"无支撑宽度 b = 盘半径 − 管孔半径 = {i.DiscDiaMm * 0.5:0.00} − {holeR:0.00} = {b:0.00} mm");
        sb.AppendLine($"屈曲斜率 t/b = 12(1−ν²)·C·β·α·(c̄·ΔT + L) ÷ (k_b·π²·c̄·η) = {r.SlopePerB:0.00000}（铂：α {Materials.PtAlphaExp:0.0e0}/K，ν {Materials.PtPoisson}，熔点 {Materials.PtMeltC:0} °C）");
        sb.AppendLine($"屈曲下界 = {r.SlopePerB:0.00000} × {b:0.00} = {raw:0.000} mm；× 安全系数 {i.SafetyFactor:0.0} = {sf:0.000} mm");
        sb.AppendLine($"烧穿下界 = {i.BurnThroughMm:0.00} mm（现场焊法给的硬底，算不出来）");
        sb.AppendLine($"⇒ 板厚工艺下界 = max({sf:0.000}, {i.BurnThroughMm:0.00}) = {floor:0.000} mm，由「{o.Governing}」控制");
        sb.AppendLine($"管侧：环缝压屈比 {o.ShellRatio:0.0000} < 1 ⇒ 任何壁厚都不会被环缝压屈，管壁下界只能由烧穿定");
        sb.AppendLine($"熔池毛细宽度 {o.CapillaryMm:0.0} mm ≫ 板厚 ⇒ 烧穿不是表面张力托不住，而是热输入控制精度");
        o.Explain = sb.ToString();
        return o;
    }

    /// <summary>盘径系列（同一组输入只换盘径）：给工程师一眼看到「盘越大下界越高、从哪一档起屈曲接管」。</summary>
    public static List<(double discDiaMm, Outputs o)> Series(Inputs i, IEnumerable<double> discDiasMm)
    {
        var list = new List<(double, Outputs)>();
        foreach (double dia in discDiasMm)
        {
            var c = new Inputs
            {
                DiscDiaMm = dia, TubeIdMm = i.TubeIdMm, WallMm = i.WallMm, SafetyFactor = i.SafetyFactor,
                BurnThroughMm = i.BurnThroughMm, StartTempC = i.StartTempC, Beta = i.Beta, EtaMelt = i.EtaMelt, Kb = i.Kb,
            };
            list.Add((dia, Compute(c)));
        }
        return list;
    }
}
