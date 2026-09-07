using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **圆盘背侧减重槽这根旋钮，在交付构型上必须是「活的」**（2026-09-05）。
///
/// 为什么单独立一道门：这根旋钮**被自己的默认带卡死过一次**，而且极其隐蔽 ——
/// 内边距写死 1 mm 时，板厚解到 4.71 ⇒ 内桥 = 1.0 mm &lt; 下限 ⇒
/// <c>SlotSpanMaxDeg</c> 返回 0 ⇒ 上界 = 下界 ⇒ 求解器**连试都不会试**，
/// 而报表照常写「这组输入不可行」，**只字不提「槽根本没得开」**。
/// 它偏偏又是治「法兰增量温降」最狠的一根（实测抽热 −42 %）。
///
/// ⇒ 本门把「旋钮是活的」与「求解器没选它」分开：
///   前者是**结构问题**（必须永远为真），后者是**比价结果**（可以变）。
///   出图报表上看到「圆盘槽 0°」时，先看这道门 —— 绿的说明是没选，红的说明是没得选。
/// </summary>
public class SlotKnobAliveTests
{
    /// <summary>
    /// ★★★★ 交付构型（Pt_Heater1 交接过来那个）上，槽带要开得出、张角上界要 &gt; 0。
    /// 板厚从制造下界一路扫到上界 —— 任何一档卡死都算红。
    /// </summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(2.0)]
    [InlineData(4.71)]     // ← 求解器在 Pt_Heater1 上实际解到的板厚
    [InlineData(8.0)]
    public void 任何板厚下槽都开得出来(double tMm)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = tMm;

        double leg = Math.Max(tMm, d.WallMm);
        var (rin, rout) = d.SlotBandMm(leg);
        Assert.True(rout > rin + 1.0,
            $"板厚 {tMm} mm：槽带 r{rin:0.00}–{rout:0.00} 宽 {rout - rin:0.00} mm ⇒ 开不出槽。"
          + "（内桥按 max(板厚, 管壁) + 桥宽下限 算，外桥从盘缘往里退一个桥宽）");

        double maxDeg = d.SlotSpanMaxDeg(leg);
        Assert.True(maxDeg > 5.0,
            $"板厚 {tMm} mm：张角上界只有 {maxDeg:0.0}° ⇒ 这根旋钮上界=下界，求解器**连试都不会试**，"
          + "而报表只会说「这组输入不可行」。");
    }

    /// <summary>
    /// ★★★ 出图 spec 里带出去的槽带，必须与求解器用的**同一份规则**算出来。
    /// 两处各算各的 = 「算一个、画另一个」，而且两边各自都自洽。
    /// </summary>
    [Fact]
    public void 出图用的槽带与求解器同源()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.71;

        // 交付件 deliverable/优化后3dm/整机.spec.json 里实测是 36.71 / 54
        var (rin, rout) = d.SlotBandMm(Math.Max(4.71, d.WallMm));
        Assert.Equal(d.HoleRadiusMm + 4.71 + 6.0, rin, 3);
        Assert.Equal(d.DiscRadiusMm - 6.0, rout, 3);
    }
}
