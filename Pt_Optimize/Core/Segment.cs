using System.ComponentModel;

namespace PtOptimize.Core;

/// <summary>
/// 一个受控加热段。铂管为分段控制，**每段的温度与压力水头都不同**，
/// 故温度、水头、牌号、几何都按段独立给定。
/// </summary>
public class Segment
{
    [DisplayName("段号")] public string Name { get; set; } = "HC1";
    [DisplayName("设定温度 °C")] public double TSetC { get; set; } = 1200;
    [DisplayName("玻璃进口 °C")] public double TGlassInC { get; set; } = 1200;
    [DisplayName("压力水头 m")] public double GlassHeadM { get; set; } = 0.5;
    [DisplayName("段长 mm")] public double LengthMm { get; set; } = 300;
    [DisplayName("内径 mm")] public double TubeIdMm { get; set; } = 50;
    [DisplayName("壁厚 mm")] public double WallMm { get; set; } = 1.0;
    [DisplayName("牌号")] public string GradeName { get; set; } = "Pt";
    [DisplayName("液相线 °C")] public double TLiquidusC { get; set; } = 1050;

    public override string ToString() => $"{Name} {TSetC:0}°C {GlassHeadM:0.0}m {GradeName}";
}

/// <summary>整条线的分段结果</summary>
public sealed class SegmentResult
{
    public Segment Seg = new();
    public double MinWallStrengthMm;   // 强度允许的最小壁厚
    public double AllowMPa;            // 该段温度下的许用应力
    public double VonMisesMPa;         // 实际合成应力
    public double Utilization;         // 利用率
    public double MassG;               // 该段铂重
    public double CostRelative;        // 相对成本（纯铂同质量 = 1）
    public string Binding = "";        // 卡住的约束
    public bool Feasible;
}
