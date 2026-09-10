using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R29（2026-09-10）：舌根加厚带（叉臂）—— 切口那一段舌片厚 = I/(J·带内最窄宽)，带外杆厚 = I/(J·完整宽)。
/// R23 仪器证明加厚落在整条舌片时导热涨七倍、孔必然更差；本门钉的是「加厚只落在切口那一段」这条闭式与它进场、进截面、进出图规格的接线。
/// </summary>
public class TabArmBandTests
{
    private static (DesignSpec d, DesignInputs p, SolverOptions o, SolverResult res) Corner56()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        var o = new SolverOptions(); var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, o, res, null, null);
        Assert.NotNull(res.DesignCurrent);
        return (d, p, o, res);
    }

    [Fact]
    public void 没有切口就没有带_舌片厚与从前逐位相同()
    {
        var (d, p, _, res) = Corner56();
        double iA = res.DesignCurrent!.PlateA[1];
        Assert.False(d.HasTabArm(1));
        double expect = Math.Ceiling(iA / (d.JDesignAPerMm2 * 56.0) / 0.01 - 1e-9) * 0.01;
        Assert.Equal(expect, d.TongueThickMm[1], 6);
    }

    [Fact]
    public void 舌根开孔_加厚只落在孔那一段_杆厚不变()
    {
        var (d, p, _, res) = Corner56();
        double iA = res.DesignCurrent!.PlateA[1];
        double stem0 = d.TongueThickMm[1];
        d.TabHoleXMm[1] = -12.0;                                   // 孔心靠舌根（切点 x=0）
        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 8.0, p, res);
        Assert.True(d.HasTabArm(1), "舌根开孔后该有加厚带");
        // 带 = [孔最远处 −2, 孔最近处 +2] = [−22, −2]（离切点 2 mm 之外 ⇒ 不并到切点）
        Assert.InRange(d.TabArmX0Mm[1], -22.5, -21.5);
        Assert.InRange(d.TabArmX1Mm[1], -2.5, -1.5);
        double armExpect = Math.Ceiling(iA / (d.JDesignAPerMm2 * 40.0) / 0.01 - 1e-9) * 0.01;   // 带内最窄 56−16
        Assert.Equal(armExpect, d.TabArmThickMm[1], 6);
        Assert.Equal(stem0, d.TongueThickMm[1], 9);                  // 杆厚按完整宽，不跟着孔涨
        Assert.True(d.TabArmThickMm[1] > d.TongueThickMm[1]);

        // 场与截面都按位置取厚
        var g = d.Plate(1, d.DiscFloorMm(p));
        Assert.Equal(d.TabArmThickMm[1], g.ThicknessAt(-12, 20), 9);   // 带内（避开孔）
        Assert.Equal(d.TongueThickMm[1], g.ThicknessAt(-60, 0), 9);    // 带外
        var cuts = SectionSizing.Cuts(g, iA, d.ClampLengthMm).Where(c => c.OnTab).ToList();
        Assert.All(cuts, c => Assert.True(c.JAPerMm2 <= d.JDesignAPerMm2 + 0.05, $"{c.Where} J={c.JAPerMm2:0.00}"));   // ④ 由构造满足
        Assert.Contains(cuts, c => c.Where.Contains("叉臂"));

        // 退回：带消失、杆厚回原值（纯函数）
        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 0.0, p, res);
        Assert.False(d.HasTabArm(1));
        Assert.Equal(stem0, d.TongueThickMm[1], 9);
    }

    [Fact]
    public void 孔贴着切点时带并到切点()
    {
        var (d, p, _, res) = Corner56();
        d.TabHoleXMm[1] = -9.0;                                    // 孔缘到 x=−1 ⇒ 2 mm 内 ⇒ 带到切点 0
        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 8.0, p, res);
        Assert.True(d.HasTabArm(1));
        Assert.Equal(0.0, d.TabArmX1Mm[1], 6);
    }

    [Fact]
    public void 出图规格带着带的三个数_没带就不写()
    {
        var (d, p, _, res) = Corner56();
        string s0 = Geometry3dm.BuildFinalSpec(d, baseIn: p);
        Assert.DoesNotContain("tabArmT", s0);
        d.TabHoleXMm[1] = -12.0;
        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 8.0, p, res);
        string s1 = Geometry3dm.BuildFinalSpec(d, baseIn: p);
        Assert.Contains("\"tabArmT\":", s1);
        Assert.Contains("\"tabArmX0\":", s1);
    }
    /// <summary>R29 补（2026-09-10 拍脑袋 Y 形仪器抓到）：起点／每轮开头的 ApplySectionFloor 也要走杆／叉臂分段，不许把叉臂抹回整条舌片。</summary>
    [Fact]
    public void 起点就分杆臂_ApplySectionFloor不许抹回整条舌片()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        for (int j = 0; j < d.FlangeCount; j++) { d.TabHoleRMm[j] = 8; d.TabHoleXMm[j] = -12; }   // 记录里已经有舌根孔
        var res = new SolverResult();
        var trace = new System.Collections.Generic.List<string>();
        Solver.ApplySectionFloor(d, p, new SolverOptions(), res, null, s => trace.Add(s));
        double iA = res.DesignCurrent!.PlateA[1];
        Assert.True(d.HasTabArm(1), "起点该有叉臂");
        Assert.Equal(Math.Ceiling(iA / (d.JDesignAPerMm2 * 40.0) / 0.01 - 1e-9) * 0.01, d.TabArmThickMm[1], 6);   // 带内 56−16
        Assert.Equal(Math.Ceiling(iA / (d.JDesignAPerMm2 * 56.0) / 0.01 - 1e-9) * 0.01, d.TongueThickMm[1], 6);   // 杆按完整宽
        Assert.Contains(trace, s => s.Contains("叉臂", StringComparison.Ordinal));
    }
}
