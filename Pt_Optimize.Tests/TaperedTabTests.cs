using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R31（2026-09-10，用户给了「拍脑袋」的 Y 形图）：锥形舌片（两边与圆盘相切、舌端窄舌根宽）与舌孔朝向（圆角三角底边朝盘）
/// 进设计层 —— 几何层本来就支持，本门钉的是设计层开关接进几何、截面、出图规格与默认朝向规则。
/// </summary>
public class TaperedTabTests
{
    private static DesignSpec Tapered()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 15; d.TabLengthMm = 140; d.TabTaper = true;
        return d;
    }

    [Fact]
    public void 锥形舌片_舌根按切线变宽_舌端还是舌端半宽()
    {
        var d = Tapered();
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        Assert.False(g.TabParallel);
        var (xT, hwT) = g.Tangent();
        Assert.Equal(d.TangentXMm(), xT, 9);                       // 设计层与几何层同一条切线
        Assert.True(hwT > 25, $"切点半宽 {hwT:0.0} 该接近盘半径（切线从舌端角到盘）");
        Assert.Equal(15.0, g.HalfWidth(-140), 6);                   // 舌端
        double mid = g.HalfWidth(0.5 * (xT + -140));
        Assert.InRange(mid, 15.0 + 1e-6, hwT - 1e-6);               // 中间在两者之间（线性张开）
        Assert.True(g.HalfWidth(-100) < g.HalfWidth(-30), "越靠舌根越宽");
    }

    [Fact]
    public void 平行边不动_逐位同前()
    {
        var d = DesignSpec.Builtin[0].Clone();
        Assert.False(d.TabTaper);
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        Assert.True(g.TabParallel);
        double hw = Math.Min(d.TabHalfWidthMm, d.DiscRadiusMm);
        Assert.Equal(-Math.Sqrt(d.DiscRadiusMm * d.DiscRadiusMm - hw * hw), d.TangentXMm(), 9);
    }

    [Fact]
    public void 锥形舌片_舌片截面按各处宽算_舌片厚按舌端定()
    {
        var d = Tapered();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit(); d.TabTaper = true;
        var p = new DesignInputs(); var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, new SolverOptions(), res, null, null);
        double iA = res.DesignCurrent!.PlateA[1];
        var g = d.Plate(1, d.DiscFloorMm(p));
        var widths = SectionSizing.TabWidths(g, d.ClampLengthMm);
        // 截面枚举从压接段之外开始（舌端 40 mm 是铜排夹着的）⇒ 最窄在 x = −(140−40) = −100，那里锥形舌比舌端宽、比舌根窄
        double w0 = widths.Min(t => t.WidthMm);
        Assert.Equal(2 * g.HalfWidth(-100), w0, 6);
        Assert.InRange(w0, 30.0 + 1e-6, 2 * g.Tangent().HalfW - 1e-6);
        double expect = Math.Ceiling(iA / (d.JDesignAPerMm2 * w0) / 0.01 - 1e-9) * 0.01;
        Assert.Equal(expect, d.TongueThickMm[1], 6);
        Assert.True(widths.First().WidthMm < widths.Last().WidthMm, "舌片宽度从舌端往舌根递增");
    }

    [Fact]
    public void 圆角三角够到舌根时默认底边朝盘_够不到用形状族默认()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleSides[0] = 3; d.TabHoleRMm[0] = 6;
        d.TabHoleXMm[0] = d.TangentXMm() - 10;                       // 孔缘够到切点 ⇒ 90°（底边朝盘）
        Assert.Equal(90.0, d.TabHoleRotDegFor(0), 6);
        d.TabHoleXMm[0] = d.TangentXMm() - 60;                       // 够不到 ⇒ 0°
        Assert.Equal(0.0, d.TabHoleRotDegFor(0), 6);
        d.TabHoleRotDeg[0] = 90;                                     // 记录给了就用记录的
        Assert.Equal(90.0, d.TabHoleRotDegFor(0), 6);
        // 朝向真的进了孔：多边形 0° 时一个顶点朝 +z ⇒ 90° 时顶点朝 −x（铜排侧）、底边（宽端）朝 +x（盘侧）
        d.TabHoleRotDeg[0] = 90; d.TabHoleXMm[0] = -50;
        var h = d.HolesOf(0)[0];
        Assert.Equal(90.0, h.RotDeg, 6);
        double rr = h.RMm;
        Assert.True(h.Contains(-50 + 0.2 * rr, 0.4 * rr), "底边朝盘：孔心偏盘侧、偏上的点该在孔里（核心三角内）");
        Assert.False(h.Contains(-50 - 0.9 * rr, 0.45 * rr), "圆头朝铜排：孔心偏铜排侧、偏上的点该在孔外（顶角外）");
        Assert.True(h.Contains(-50 - 0.9 * rr, 0.0), "顶角朝铜排：轴线上偏铜排侧的点在孔里");
    }

    [Fact]
    public void 出图规格带锥形标记_默认不带()
    {
        var p = new DesignInputs();
        var d0 = DesignSpec.Builtin[0].Clone();
        Assert.Contains("\"tabTaper\":false", Geometry3dm.BuildFinalSpec(d0, baseIn: p));
        var d1 = Tapered();
        Assert.Contains("\"tabTaper\":true", Geometry3dm.BuildFinalSpec(d1, baseIn: p));
    }
}
