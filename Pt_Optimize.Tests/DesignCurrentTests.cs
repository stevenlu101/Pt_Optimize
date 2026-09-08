using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ 设计电流（20 °C/h 空管升温峰值）—— 用户 2026-09-08 设计因果链第 ① 步。
///
/// 用户同日提醒「对前任的计算逻辑必须小心验证」：本档不只验接线，还**独立核一次数**：
/// 升温末端（管温 = 目标）电流应≈准静态电流 I = √((Q_散热 + C·dT/dt)/R_管)，
/// 两节点模型多了法兰耦合项，只会略高。管电阻 R = ρ(T)·L/A 由本档自己按材料常数算，不引模型内部的值。
/// </summary>
public class DesignCurrentTests
{
    private static (DesignSpec D, DesignInputs P) W08TwoSeg()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };      // 用户 09-08：验算跑 2 段（3 片）
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        return (d, p);
    }

    [Fact]
    public void 两段三片_端片等于段峰值_共用片矢量合成()
    {
        var (d, p) = W08TwoSeg();
        var dc = DesignCurrent.ForLine(d, p);
        Assert.Equal(2, dc.SegPeakA.Length);
        Assert.Equal(3, dc.PlateA.Length);
        Assert.All(dc.SegPeakA, a => Assert.True(a > 100, $"段峰值电流 {a} A 不像样"));
        Assert.Equal(dc.SegPeakA[0], dc.PlateA[0], 9);
        Assert.Equal(dc.SegPeakA[1], dc.PlateA[2], 9);
        double i1 = dc.SegPeakA[0], i2 = dc.SegPeakA[1];
        Assert.Equal(Math.Sqrt(i1 * i1 + i2 * i2 + i1 * i2), dc.PlateA[1], 6);   // 120° 相位差的相量差
        Assert.True(dc.PlateA[1] > Math.Max(i1, i2), "共用片电流必须大于两侧任一段");
        Console.WriteLine(dc.Describe());
    }

    [Fact]
    public void 独立核数_升温末端电流约等于准静态电流()
    {
        var (d, p) = W08TwoSeg();
        var dc = DesignCurrent.ForLine(d, p);
        // ⚠ 手算要用**设计记录的保温层**（BuildCase 写进 lc.Base），不是开箱默认的 p —— 传错过一次，比值 0.82 假红
        var lc = d.BuildCase(p, checkRamp: false);
        double iQs = RampTwoNode.QuasiStaticCurrentA(lc.Base, d.WallMm, 1150.0, 20.0);   // 管子自己：I²R = Q + C·Ṫ
        Assert.True(iQs > 100, $"准静态电流 {iQs} A 不像样");
        double ratio = dc.SegPeakA[0] / iQs;
        Console.WriteLine($"段 1 峰值 {dc.SegPeakA[0]:0} A　准静态(1150 °C) {iQs:0} A　比 {ratio:0.000}");
        Assert.InRange(ratio, 0.99, 1.01);     // 设计电流就是准静态全程峰值，峰在目标温度处 ⇒ 应逐位相同
        Assert.True(dc.TwoNodeSegPeakA[0] > 0.5 * iQs && dc.TwoNodeSegPeakA[0] < 1.5 * iQs,
            $"两节点对照 {dc.TwoNodeSegPeakA[0]:0} A 与准静态 {iQs:0} A 差得离谱 —— 两个模型有一个接错了");

        // 管电阻由本档自己算：ρ(1150) L / A，功率 I²R 应落在千瓦级、与散热同量级
        double holeR = d.HoleRadiusMm, ri = holeR - d.WallMm;
        double areaM2 = Math.PI * (holeR * holeR - ri * ri) * 1e-6;
        double rOhm = Materials.PtResistivity(1150.0) * p.TubeLength / areaM2;
        double pW = dc.SegPeakA[0] * dc.SegPeakA[0] * rOhm;
        Console.WriteLine($"R_管(1150) = {rOhm * 1e3:0.000} mΩ　I²R = {pW:0} W");
        Assert.InRange(pW, 300, 20000);
    }

    /// <summary>★ 杠杆 1：管保温加厚 ⇒ 管散热降 ⇒ 设计电流降（2026-09-08 实测过一次是哑的：传了原始 p 没带设计记录的保温层）。</summary>
    [Fact]
    public void 管保温加厚_设计电流下降()
    {
        var (d, p) = W08TwoSeg();
        d.TubeInsulMm = 5;  var a = DesignCurrent.ForLine(d, p);
        d.TubeInsulMm = 40; var b = DesignCurrent.ForLine(d, p);
        Console.WriteLine($"管保温 5 mm：{a.SegPeakA[0]:0} A　40 mm：{b.SegPeakA[0]:0} A");
        Assert.True(b.SegPeakA[0] < a.SegPeakA[0] * 0.95, $"管保温 5→40 mm 设计电流 {a.SegPeakA[0]:0} → {b.SegPeakA[0]:0} A 没降 —— 杠杆是哑的");
    }

    /// <summary>★ R20（用户 2026-09-08）：① = 升温所需电流没被管 J 许用上限截住。把许用压到很小就必截住。</summary>
    [Fact]
    public void 管J许用压小_升温电流被截住_判据一不过()
    {
        var (d, p) = W08TwoSeg();
        var a = DesignCurrent.ForLine(d, p);
        Assert.False(a.Clipped, "开箱工况下 0.8 档不该被截住");
        Assert.InRange(a.RampTubeJPeakAPerMm2, 1, a.TubeJAllowAPerMm2);
        double holeR = d.HoleRadiusMm, ri = holeR - d.WallMm, area = Math.PI * (holeR * holeR - ri * ri);
        Assert.Equal(a.SegPeakA[0] / area, a.RampTubeJPeakAPerMm2, 6);          // 没截住时峰值就是段电流

        var lc = d.BuildCase(p, checkRamp: true);          // 开着集总升温：参考行「升温到位用时」只在那时出现
        var r = LineRunner.Run(lc);
        var one = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.Ramp, StringComparison.Ordinal));
        Assert.True(one.Ok, one.Note);
        Assert.Equal(CheckKind.HardSafety, one.Kind);
        Assert.Contains(r.Checks, c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal) && c.Kind == CheckKind.Reference);

        p.TubeJAllowAPerMm2 = 2.0;                                                // 许用压到 2 ⇒ 1214 A 的段必截住
        var b = DesignCurrent.ForLine(d, p);
        Assert.True(b.Clipped);
        Assert.True(b.RampTubeJPeakAPerMm2 > b.TubeJAllowAPerMm2);
        Assert.Equal(a.RampTubeJPeakAPerMm2, b.RampTubeJPeakAPerMm2, 6);          // 所需电流不变，只是被截住
    }

    [Fact]
    public void 法兰更重_设计电流不降()
    {
        var (d, p) = W08TwoSeg();
        var a = DesignCurrent.ForLine(d, p);
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] *= 2;
        var b = DesignCurrent.ForLine(d, p);
        for (int i = 0; i < a.SegPeakA.Length; i++)
            Assert.True(b.SegPeakA[i] >= a.SegPeakA[i] - 1e-6, $"段 {i}：法兰加倍后电流反而降 {a.SegPeakA[i]} → {b.SegPeakA[i]}");
    }
}
