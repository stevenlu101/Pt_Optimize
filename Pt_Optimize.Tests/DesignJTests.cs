using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 用户 2026-09-09：「J 让工程师设定（实况风险工程师承担）；J 是设定值，J+1 是计算极限值；J 预设值为 10」。
/// 这里钉的是**闭式**那一半：设定 J 进设计 → 舌片厚／板厚下界／孔径与槽角上界都按它算，计算极限值 = J+1，
/// 进 LineCase（判据限值的唯一来源）、进设计记录（往返不丢由 DesignSpecStoreTests 的样本门盯）。
/// 场解那一半（判据表「法兰截面 J」的限值随 J 变）由 UiWiring 走查 16″ 覆盖。
/// </summary>
public class DesignJTests
{
    [Fact]
    public void 预设值10_计算极限值等于设定值加1()
    {
        Assert.Equal(10.0, new DesignSpec().JDesignAPerMm2);
        Assert.Equal(11.0, new DesignSpec().JCheckAPerMm2);
        Assert.Equal(13.5, SectionSizing.JCheckOf(12.5));
        var d = DesignSpec.Builtin[0].Clone();
        Assert.DoesNotContain("工程师设定", d.Describe());          // 预设 10 不特别标
        d.JDesignAPerMm2 = 12.5;
        Assert.Equal(13.5, d.JCheckAPerMm2);
        Assert.Contains("J 12.5（工程师设定，极限 13.5）", d.Describe());   // 改过的 J 要印出来，不许看起来像预设
    }

    [Fact]
    public void 舌片厚随设定J反比_J翻倍舌片厚减半()
    {
        var p = new DesignInputs();
        var d10 = DesignSpec.Builtin[0].Clone(); d10.JDesignAPerMm2 = 10; d10.SizeTongues(p);
        var d20 = DesignSpec.Builtin[0].Clone(); d20.JDesignAPerMm2 = 20; d20.SizeTongues(p);
        int n = 0;
        for (int j = 0; j < d10.TongueThickMm.Length; j++)
        {
            if (double.IsNaN(d10.TongueThickMm[j])) continue;
            n++;
            // 向上落 0.01 格 ⇒ 允许 0.02 的量化差；W08 舌片厚 2 mm 上下，减半后仍远高于板料下限 0.6，不会被咬住
            Assert.True(Math.Abs(d20.TongueThickMm[j] * 2 - d10.TongueThickMm[j]) <= 0.02 + 1e-9,
                $"片{j}：J10 {d10.TongueThickMm[j]:0.00}，J20 {d20.TongueThickMm[j]:0.00} —— 不是一半");
        }
        Assert.True(n >= 2, "舌片厚全 NaN ⇒ 本门空转");
    }

    [Fact]
    public void 板厚下界与孔径槽角上界随J放宽()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        var g = d.Plate(1, d.DiscFloorMm(p));
        const double I = 2500;
        double f10 = SectionSizing.PlateThickFloorMm(g, g.ThicknessMm, I, d.ClampLengthMm, 10);
        double f20 = SectionSizing.PlateThickFloorMm(g, g.ThicknessMm, I, d.ClampLengthMm, 20);
        Assert.True(f10 > 0 && Math.Abs(f20 * 2 - f10) < 1e-9, $"板厚下界 J10 {f10:0.000} / J20 {f20:0.000} —— 不是一半");
        double r10 = SectionSizing.HoleRadiusMaxByJMm(g, d.TabHoleCenterXMm(1), I, 10);
        double r20 = SectionSizing.HoleRadiusMaxByJMm(g, d.TabHoleCenterXMm(1), I, 20);
        Assert.True(r20 >= r10, $"孔径上界 J10 {r10:0.00} / J20 {r20:0.00}");
        var (rin, rout) = d.SlotBandMm(Math.Max(g.ThicknessMm, d.WallMm));
        double s10 = SectionSizing.SlotSpanMaxByJDeg(g, rin, rout, I, 10);
        double s20 = SectionSizing.SlotSpanMaxByJDeg(g, rin, rout, I, 20);
        Assert.True(s20 >= s10, $"槽张角上界 J10 {s10:0.0} / J20 {s20:0.0}");
        // 不传 J 就是预设 10（改预设值要连用户口径一起改，这里会响）
        Assert.Equal(f10, SectionSizing.PlateThickFloorMm(g, g.ThicknessMm, I, d.ClampLengthMm), 12);
    }

    [Fact]
    public void 设定J进算例_判据限值与下角只从设计读不读常数()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone(); d.JDesignAPerMm2 = 12;
        var lc = d.BuildCase(p, checkRamp: false);
        Assert.Equal(12.0, lc.JDesignAPerMm2);
        // 禁用词式的门：判据行不许再读常数 11，求解器不许再读常数 10 —— 否则页面上改了 J，一半模型不动、不报错
        string lr = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.DoesNotContain("SectionSizing.JCheckAPerMm2", lr);
        string so = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        Assert.DoesNotContain("SectionSizing.JDesignAPerMm2", so);
        Assert.DoesNotContain("SectionSizing.JCheckAPerMm2", so);
    }
}
