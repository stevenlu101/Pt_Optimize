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

    /// <summary>
    /// ★ 审查欠账（低，2026-09-09）：孔径的桥宽上界（<see cref="DesignSpec.TabHoleRMaxMm"/>）与按 J
    /// 的上界（<see cref="SectionSizing.HoleRadiusMaxByJMm"/>）此前恒按**圆**算；圆角三角／方（<c>TabHoleSides</c> 3/4）
    /// 与「孔径旋钮值 R」等面积时的**外接半径**比 R 大 13–22 %（<see cref="FlangePlate.TabHole.EqualAreaRadius"/>）——
    /// 真正会顶到舌边/顶到孔缘弦的是那个外接半径，按圆算会把三角/方孔的上界算宽。
    /// 本门钉：① 两处上界随形状族收紧；② 收紧后按等面积换算折回外接半径，不超过圆的上界（不会再顶边）。
    /// </summary>
    [Fact]
    public void 孔径上界按真实形状族收紧_圆角三角方比圆大一截()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHalfWidthMm = 20;
        double capCircle = d.TabHoleRMaxMm(sides: 0);
        double capTri = d.TabHoleRMaxMm(sides: 3);
        double capSq = d.TabHoleRMaxMm(sides: 4);
        Assert.True(capTri < capCircle, $"圆角三角孔径（桥宽）上界没有收紧（圆 {capCircle:0.###} / 三角 {capTri:0.###}）");
        Assert.True(capSq < capCircle, $"圆角方孔径（桥宽）上界没有收紧（圆 {capCircle:0.###} / 方 {capSq:0.###}）");
        double ratioTri = FlangePlate.TabHole.EqualAreaRadius(1.0, 3, DesignSpec.TabHoleCornerFracOf(3));
        double ratioSq = FlangePlate.TabHole.EqualAreaRadius(1.0, 4, DesignSpec.TabHoleCornerFracOf(4));
        Assert.True(ratioTri > 1.0 && ratioTri < 1.3, $"三角的等面积外接半径放大倍数不在 13–22% 那个量级（{ratioTri:0.###}）");
        // 抬到上界后折回外接半径，不能超过「桥宽留给圆」的那个上界——否则依旧会顶边
        Assert.True(capTri * ratioTri <= capCircle + 1e-9, "三角孔抬到上界后，外接半径仍超过桥宽给圆留的上界");
        Assert.True(capSq * ratioSq <= capCircle + 1e-9, "方孔抬到上界后，外接半径仍超过桥宽给圆留的上界");

        // 按 J=10 的孔缘弦上界同理：形状比例传进去后应等比收紧
        var g = Plate(1.0);
        double jCapCircle = SectionSizing.HoleRadiusMaxByJMm(g, -100, 300, shapeRadiusRatio: 1.0);   // 15（同上一门）
        double jCapTri = SectionSizing.HoleRadiusMaxByJMm(g, -100, 300, shapeRadiusRatio: ratioTri);
        Assert.True(jCapTri < jCapCircle, $"按 J 的孔径上界没有为圆角三角收紧（圆 {jCapCircle:0.###} / 三角 {jCapTri:0.###}）");
        Assert.Equal(jCapCircle / ratioTri, jCapTri, 6);
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

    /// <summary>★ R11（用户 2026-09-08）：舌片厚不是旋钮，= I/(J·舌片最窄有效宽)。</summary>
    [Fact]
    public void 舌片厚闭式_等于电流除以J乘最窄有效宽()
    {
        Assert.Equal(1.0, SectionSizing.TongueThickMm(Plate(1.0), 600), 9);                       // 600/(10×60)
        Assert.Equal(1.5, SectionSizing.TongueThickMm(Plate(1.0, new FlangePlate.TabHole(-100, 0, 10)), 600), 9);   // 孔处宽 40
        Assert.Equal(1.0, SectionSizing.TongueThickMm(Plate(1.0, new FlangePlate.TabHole(-100, 0, 10)), 600, clampLenMm: 60), 9); // 压接段盖住孔
        Assert.True(double.IsNaN(SectionSizing.TongueThickMm(Plate(1.0), 0)));                      // 没电流 ⇒ 不给数
        Assert.Equal(1.0, SectionSizing.TongueThickMm(Plate(2.0), 600), 9);                       // 与基板无关：基板加倍它不变
    }

    /// <summary>★ R11：舌片解耦后，基板下界只看圆盘侧截面（交界弦／各圈）；终验仍看全体。</summary>
    [Fact]
    public void 舌片解耦后_基板下界只看圆盘侧截面()
    {
        var g = Plate(1.0, new FlangePlate.TabHole(-100, 0, 10));   // 舌片最紧 J=15；舌盘交界弦 60×1 ⇒ J=10；圆盘整圈 J=3.7
        Assert.Equal(1.5, SectionSizing.PlateThickFloorMm(g, 1.0, 600), 6);           // 同厚（旧口径）：舌片截面算进来
        g.TabThicknessMm = 1.5;                                                        // 解耦：舌片自己 1.5
        // 基板只看交界切口与圆盘各圈。交界切口（2026-09-09 修正）= 边条 2×(30−25.8)×1.0 + 焊弧 π×25.8×舌片厚 1.5
        //   = 8.4 + 121.6 = 130.0 mm² ⇒ J 4.62；圆盘最内圈 2π×25.8×1.0 = 162.1 ⇒ J 3.70；最紧是交界 ⇒ 下界 = 1.0 × 4.62/10
        double junction = 2 * (30 - 25.8) * 1.0 + Math.PI * 25.8 * 1.5;
        Assert.Equal(600.0 / junction / 10.0, SectionSizing.PlateThickFloorMm(g, 1.0, 600), 6);
        Assert.All(SectionSizing.Cuts(g, 600).Where(c => c.Where.StartsWith("舌片")), c => Assert.True(c.OnTab));
        Assert.All(SectionSizing.Cuts(g, 600).Where(c => !c.Where.StartsWith("舌片")), c => Assert.False(c.OnTab));
        Assert.Equal(600.0 / 40.0 / 1.5, SectionSizing.Worst(g, 600).JAPerMm2, 6);   // 终验仍看全体：舌片孔处 600/(40×1.5) = 10
    }

    /// <summary>★ 2026-09-09 审查抓到：各圈厚度原取 (−r,0)，R11 解耦后那点在舌片上 ⇒ 读成舌片厚。现在取 (0,r)。</summary>
    [Fact]
    public void 圆盘各圈厚度取的是盘上的点_不是舌片厚()
    {
        var g = Plate(1.0);
        g.TabThicknessMm = 3.0;                                   // 舌片解耦、比基板厚 3 倍
        var ring = SectionSizing.Cuts(g, 600).Where(c => c.Where.StartsWith("圆盘")).ToList();
        Assert.NotEmpty(ring);
        // 圆盘整圈 2πr × 基板 1.0（不是 × 3.0）：最内一圈 r=25.8 ⇒ 162.1 mm²
        Assert.Equal(2 * Math.PI * 25.8 * 1.0, ring.Min(c => c.AreaMm2), 1);
        Assert.All(ring, c => Assert.True(c.AreaMm2 < 2 * Math.PI * 30 * 1.0 + 1, $"{c.Where} {c.AreaMm2:0.0} 像是按舌片厚算的"));
        // 槽张角上界同一处：厚度也按盘上算
        double theta1 = SectionSizing.SlotSpanMaxByJDeg(g, 27, 29, 600);
        g.TabThicknessMm = double.NaN;
        Assert.Equal(theta1, SectionSizing.SlotSpanMaxByJDeg(g, 27, 29, 600), 9);
    }

    /// <summary>
    /// ★ 2026-09-09 修正舌盘交界切口：盘半径 = 舌半宽时切点在 x=0，整条弦有 2×25.8 穿过管腔（没有料）——
    /// 原式 2·hwT·t 把管腔当成料；只剩边条又会把管孔边的焊弧（舌片电流真正的出口）漏掉。
    /// 现在 = 边条 + 焊弧×孔边厚。用手算数钉住。
    /// </summary>
    [Fact]
    public void 交界切口_盘径等于舌宽时扣管孔弦并加焊弧()
    {
        var g = Plate(1.0);                                       // 盘 R30、半宽 30 ⇒ 切点 x=0；孔 R25.8
        var j = SectionSizing.Cuts(g, 600).First(c => c.Where.StartsWith("舌盘交界"));
        // 边条：2×(30−25.8)=8.4 mm × 1.0；焊弧：x<0 且 |z|≤30 的整个左半圆 = π×25.8 = 81.05 mm × 孔边厚 1.0
        double expect = 8.4 * 1.0 + Math.PI * 25.8 * 1.0;
        Assert.Equal(expect, j.AreaMm2, 1);
        Assert.Contains("焊弧", j.Where);
        // 切点在孔外（半宽 20：|xT| = √(30²−20²) = 22.4 < 25.8 仍穿孔）—— 只有 |xT| ≥ 25.8 才回原式；用半宽 10：|xT| = 28.3
        var g2 = new FlangePlate
        {
            DiscRadiusMm = 30, HoleRadiusMm = 25.8, TabEndXMm = -140, TabEndHalfWidthMm = 10,
            ThicknessMm = 1.0, TabThicknessMm = double.NaN, TabParallel = true, WeldFilletLegMm = 0,
        };
        var j2 = SectionSizing.Cuts(g2, 600).First(c => c.Where.StartsWith("舌盘交界"));
        Assert.Equal(2 * 10 * 1.0, j2.AreaMm2, 6);                // 原式逐位相同
        Assert.DoesNotContain("焊弧", j2.Where);
    }

    [Fact]
    public void 常数就是用户给的数()
    {
        Assert.Equal(10.0, SectionSizing.JDesignAPerMm2);
        Assert.Equal(11.0, SectionSizing.JCheckAPerMm2);
    }
}
