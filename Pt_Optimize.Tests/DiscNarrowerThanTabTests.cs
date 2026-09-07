using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **盘比舌还窄的零件，不许算出数来**（2026-09-06 用户看图抓到）。
///
/// 我按 ⑥ 的不动点解出 R27、取 R35 跑 Pt_Heater1，交出来的图**没有圆盘**：
/// <code>
///   盘R 35 ⇒ 盘直径 70
///   舌半宽 40 ⇒ 舌全宽 80
///   70 &lt; 80 ⇒ 圆盘整个藏在舌片宽度里，零件退化成一块开了孔的矩形板
/// </code>
///
/// 两边同时错、各自都自洽 —— 本项目最怕的那种：
/// <code>
///   解析侧  TabParallel = true ⇒ HalfWidth(x) 恒 = 舌半宽
///          ⇒ Inside 判的是**矩形板**，「可行=True、5070 g」是在它上面算的
///   出图侧  BodyOutline 的分支判据是 `discR &gt; w`，35 &gt; 40 为假
///          ⇒ 走了「盘比舌宽」那一支，画出一张坏图
/// </code>
///
/// ⚠ 而我**专门做过**「把俯视图印进报告」这件事，理由就是「不用开 Rhino 也看得见」——
///   报告里那张俯视图当时就是一个矩形，**我没看**。做了工具没用，等于没做。
/// </summary>
public class DiscNarrowerThanTabTests
{
    private static FlangePlate Plate(double discR, double tabHW, double t) => new()
    {
        DiscRadiusMm = discR, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = tabHW,
        ThicknessMm = t, ThickenedMm = t, TabThicknessMm = t,
        TabParallel = true, WeldFilletLegMm = Math.Max(t, 1.0),
    };

    /// <summary>
    /// ★★★★★ 最小盘半径必须同时管住两条：盖得住管孔+焊脚、**而且不比舌片窄**。
    /// </summary>
    /// <summary>
    /// ★★★★★ **两个理由各判各的，不许并成一个数**（2026-09-07 督导 S⑤ 打回来的）。
    ///
    /// 我第一版把「不比舌片窄」并进了 <c>MinDiscRadiusMm</c>，理由是「判据只有一个来源」。
    /// 错在：⑥ 的含义是**「盖得住管孔＋焊脚」**，并进去之后处方会说
    /// 「⑥ 盖不住、要 30」，而管孔那条其实只要 28.25 —— **两个理由印成同一个原因**。
    /// 督导那条红（DiscCoverPrescription 28.25→30）问的正是「是门钉了数字还是真回归」：
    /// **都不是，是我把位置放错了**。
    /// </summary>
    [Theory]
    // 盘R, 舌半宽, 板厚, ⑥ 要的（管孔+焊脚）, 舌宽要的
    [InlineData(35, 40, 1.25, 27.25, 40.0)]
    [InlineData(60, 40, 4.71, 30.71, 40.0)]
    [InlineData(60, 20, 4.71, 30.71, 20.0)]
    [InlineData(60, 10, 8.00, 34.00, 10.0)]
    public void 两条下界各算各的(double discR, double tabHW, double t, double wantBore, double wantTab)
    {
        var p = Plate(discR, tabHW, t);
        Assert.Equal(wantBore, GeometryScreen.MinDiscRadiusMm(p), 2);
        Assert.Equal(wantTab,  GeometryScreen.MinDiscRadiusForTabMm(p), 2);
    }

    /// <summary>
    /// ★★★★★ **盘比舌窄 ⇒ ⑥ 必须红**，而且要给出处方（盘径要改到多大）。
    /// 红不了的话，求解器会在一块「退化成矩形板」的几何上算出一个漂亮的数。
    /// </summary>
    [Fact]
    public void 盘比舌窄时要红且原因要指对()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 35; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 1.25;

        var (ok, why) = Solver.CoverCheck(d, new DesignInputs());
        Assert.False(ok, "盘R35 < 舌半宽40 —— 圆盘整个藏在舌片里，却判过了。"
                       + "这正是让求解器在矩形板上算出「可行=True、5070 g」的那道缺口。");
        Assert.Contains("舌片长不出圆盘", why);   // ★ 原因要指对：不是「⑥ 盖不住管孔」
        Assert.Contains("40", why);               //   处方里要出现「要改到 40」
    }

    /// <summary>
    /// ★★★★ 图纸构型（盘R60、舌半宽40）**不许被这条新约束误伤** ——
    /// 60 ≥ 40，本来就成立。加约束不能把好的挡掉。
    /// </summary>
    [Fact]
    public void 图纸构型不受误伤()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.71;
        var (ok, why) = Solver.CoverCheck(d, new DesignInputs());
        Assert.True(ok, "图纸构型被新约束误伤了：" + why);
    }

    /// <summary>
    /// ★★★ 0.8 / 0.6 两个现役档也不许被误伤（回归锚）。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 现役档不受误伤(int idx)
    {
        var d = DesignSpec.Builtin[idx].Clone();
        var (ok, why) = Solver.CoverCheck(d, new DesignInputs());
        Assert.True(ok, $"内置档 {d.Name}（盘R {d.DiscRadiusMm}、舌半宽 {d.TabHalfWidthMm}）"
                      + "被新约束误伤了：" + why);
    }
}
