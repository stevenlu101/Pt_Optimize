using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ 截面电流密度的闭式（用户 2026-09-08 设计因果链第 ②／④ 步）—— 用手算得出的数钉住，不钉措辞。
/// 几何：等宽舌 半宽 30（宽 60）、盘 R30、管孔 R25.8、板厚 1 mm、舌端 x=−140。
/// </summary>
public class SectionSizingTests
{
    private static FlangePlate Plate(double t, params FlangePlate.TabHole[] holes) => new()
    {
        DiscRadiusMm = 30, HoleRadiusMm = 25.8, TabEndXMm = -140, TabEndHalfWidthMm = 30,
        ThicknessMm = t, TabThicknessMm = double.NaN, TabParallel = true, WeldFilletLegMm = 0,
        TabHoles = holes,
    };

    [Fact]
    public void 等宽舌片_截面J等于电流除以宽乘厚()
    {
        var g = Plate(1.0);
        var w = SectionSizing.Worst(g, 600);           // 600 A / (60 mm × 1 mm) = 10
        Assert.StartsWith("舌片", w.Where);
        Assert.Equal(10.0, w.JAPerMm2, 3);
        // 圆盘整圈（用户 09-08：先按整圈）：2π·25.8·1 = 162.1 mm² ⇒ J = 3.70，比舌片松
        var ring = SectionSizing.Cuts(g, 600).Where(c => c.Where.StartsWith("圆盘")).Min(c => c.AreaMm2);
        Assert.Equal(2 * Math.PI * 25.8, ring, 1);
    }

    [Fact]
    public void 开孔处扣掉孔的弦_截面变紧()
    {
        var g = Plate(1.0, new FlangePlate.TabHole(-100, 0, 10));    // 孔心处宽 60 − 20 = 40
        var w = SectionSizing.Worst(g, 600);
        Assert.Contains("x=-100", w.Where);
        Assert.Equal(600.0 / 40.0, w.JAPerMm2, 3);                     // 15
        // 孔径上界（按 J=10）：(60 − 600/(10·1))/2 = 0
        Assert.Equal(0.0, SectionSizing.HoleRadiusMaxByJMm(g, -100, 600), 6);
        // 电流小一半 ⇒ (60 − 30)/2 = 15
        Assert.Equal(15.0, SectionSizing.HoleRadiusMaxByJMm(g, -100, 300), 6);
    }

    [Fact]
    public void 板厚下界按最紧截面线性放大()
    {
        var g = Plate(1.0, new FlangePlate.TabHole(-100, 0, 10));    // 最紧 J = 15 @ t=1
        Assert.Equal(1.5, SectionSizing.PlateThickFloorMm(g, 1.0, 600), 6);   // 1 × 15/10
        // 压接段（铜排短接）不算截面：把压接段拉到盖住孔，最紧截面回到无孔处
        Assert.Equal(1.0, SectionSizing.PlateThickFloorMm(g, 1.0, 600, clampLenMm: 60), 6);
    }

    [Fact]
    public void 槽张角上界_没有电流就是360()
    {
        var g = Plate(1.0);
        Assert.Equal(360.0, SectionSizing.SlotSpanMaxByJDeg(g, 27, 29, 0), 6);
        // 有电流：θ ≤ 2π − I/(10·t·r)，r=27..29 取最紧（r 小的那圈）：2π − 600/(10·1·27) = 6.283 − 2.222 = 4.061 rad = 232.7°
        Assert.Equal((2 * Math.PI - 600.0 / (10 * 27)) * 180 / Math.PI, SectionSizing.SlotSpanMaxByJDeg(g, 27, 29, 600), 3);
    }

    [Fact]
    public void 常数就是用户给的数()
    {
        Assert.Equal(10.0, SectionSizing.JDesignAPerMm2);
        Assert.Equal(11.0, SectionSizing.JCheckAPerMm2);
    }
}
