using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R39（2026-09-11）：焊接下界小算盘 —— 独立于求解链之外，但口径必须与求解链的下角（DesignSpec.DiscFloorMm）逐位相同，
/// 否则工程师在小算盘上看到的数与 APP 起步用的数是两个数（本项目最忌的形态）。
/// </summary>
public class WeldFloorCalcTests
{
    [Fact]
    public void 与求解链的下角逐位相同()
    {
        var b = new DesignInputs();
        foreach (var d in DesignSpec.Builtin)
        {
            var i = new WeldFloorCalc.Inputs
            {
                DiscDiaMm = 2 * d.DiscRadiusMm, TubeIdMm = d.TubeIdMm, WallMm = d.WallMm,
                SafetyFactor = b.WeldSafetyFactor, BurnThroughMm = b.WeldMinThicknessMm,
            };
            var o = WeldFloorCalc.Compute(i);
            Assert.Equal(d.HoleRadiusMm, o.HoleRadiusMm, 9);
            Assert.Equal(d.DiscFloorMm(b), o.FloorMm, 9);
        }
    }

    [Fact]
    public void 盘越大下界越高_小盘由烧穿控制_大盘由屈曲控制()
    {
        var i = new WeldFloorCalc.Inputs();
        var s = WeldFloorCalc.Series(i, new[] { 60.0, 72, 90, 120, 150 });
        for (int k = 1; k < s.Count; k++)
            Assert.True(s[k].o.BucklingWithSfMm > s[k - 1].o.BucklingWithSfMm, "屈曲下界该随盘径单调上升");
        Assert.Equal("烧穿", s[0].o.Governing);   // Ø60：0.55×2 = 1.1？不——按 DesignSpec 口径 R30 上屈曲 0.58 mm（含 SF 后仍要与 0.6 比）
        Assert.Equal("屈曲", s[^1].o.Governing);
        Assert.True(s.All(x => x.o.ShellRatio < 1), "铂管永不被环缝压屈");
    }

    [Fact]
    public void 四边简支的对照值小九点三倍()
    {
        var a = WeldFloorCalc.Compute(new WeldFloorCalc.Inputs { Kb = WeldDistortion.PlateBucklingKFreeEdge });
        var b = WeldFloorCalc.Compute(new WeldFloorCalc.Inputs { Kb = WeldDistortion.PlateBucklingK });
        Assert.InRange(a.BucklingRawMm / b.BucklingRawMm, 9.2, 9.4);
    }

    [Fact]
    public void 说明文字把每一步的数都写出来()
    {
        var o = WeldFloorCalc.Compute(new WeldFloorCalc.Inputs());
        Assert.Contains("无支撑宽度", o.Explain);
        Assert.Contains("屈曲下界", o.Explain);
        Assert.Contains("烧穿下界", o.Explain);
        Assert.Contains("由「", o.Explain);
        Assert.DoesNotContain("**", o.Explain);   // 给工程师看的，不许 Markdown 星号
    }
}
