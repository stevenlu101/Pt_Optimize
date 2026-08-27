using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **判据限值只有一处来源**，定尺寸器不许自己再存一份。
///
/// ★ 病灶（2026-08-28 第一性原理通查抓到，主进程独立复验）：
///   SizerOptions 里存着 `DipLimitK = 10.0` 与 `DiscOverTargetK = 3.0`，
///   而注释声称「与 <c>LineCase.RootDeltaMaxK</c> **同源**」——
///   grep 证实 Sizer 从未读过 RootDeltaMaxK，**「同源」是假的**。
///
///   ③ 的限值还是**承重**的：`drawMax = 限值 / γ` 是整个 D8 分派的硬上界。
///   判据一收紧而这里不动，就是本项目连犯过四次的
///   **「优化器在调 A、判据在判 B」**。
///
/// ⇒ 现在限值只从 LineCase 读，Sizer 只保留自己的**裕度**（DiscOverMarginK）。
/// </summary>
public class SingleSourceLimitTests
{
    private static string Src() =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Sizer.cs"));

    [Fact]
    public void 定尺寸器不许再存一份限值副本()
    {
        string s = Src();
        // ⚠ 只查**声明与取用**，不查字面出现 —— 那段更正注释本身要引用旧字段名
        //   才说得清改了什么。「一提到就红」会逼着后人把病历删掉。
        Assert.DoesNotContain("public double DipLimitK", s);
        Assert.DoesNotContain("public double DiscOverTargetK", s);
        Assert.DoesNotContain("opt.DipLimitK", s);
        Assert.DoesNotContain("opt.DiscOverTargetK", s);
    }

    [Fact]
    public void 限值必须从LineCase读()
    {
        string s = Src();
        Assert.Contains("RootDeltaMaxK", s);        // ③ 的限值
        Assert.Contains("DiscOverTempMaxK", s);     // ②″ 的限值
    }

    [Fact]
    public void 裕度换算保持原行为_限值5减裕度2等于原来的靶3()
    {
        // 行为不变是这次收口的前提：只把「两份限值」并成「一份限值 + 一份裕度」。
        var c = new LineCase();
        double target = c.DiscOverTempMaxK - new SizerOptions().DiscOverMarginK;
        Assert.Equal(3.0, target, 9);
        // 自证：限值本身确实是 5，否则上面这条只是巧合
        Assert.Equal(5.0, c.DiscOverTempMaxK, 9);
    }

    [Fact]
    public void 三的限值就是判据那一份_不是另一个数()
    {
        Assert.Equal(10.0, new LineCase().RootDeltaMaxK, 9);
    }

    /// <summary>
    /// 屈曲的无支撑宽度 b 必须用 <see cref="FinalDesign.HoleRadiusMm"/>，不许再出现字面量 26.0。
    ///
    /// ★ 26.0 是**管壁 1.0 的旧构型**（PlateCurrent2D 注明「Ø52 = 管外径」），
    ///   而两个现役档是 25.6／25.8 ⇒ b 少算 0.2–0.4 mm，下界偏低 5–10 %，**偏在危险侧**。
    /// </summary>
    [Fact]
    public void 屈曲宽度跟着管壁走_不是写死的26()
    {
        var p = new DesignInputs();
        var a = FinalDesign.W08.Clone(); a.DiscRadiusMm = 60;   // 大盘 ⇒ 屈曲主导，看得出差别
        var b = FinalDesign.W08.Clone(); b.DiscRadiusMm = 60; b.WallMm = 0.6;
        // 管壁薄 ⇒ 孔小 ⇒ 无支撑宽度大 ⇒ 下界**更高**
        Assert.True(b.DiscFloorMm(p) > a.DiscFloorMm(p),
            $"壁 0.6 的下界应高于壁 0.8：{b.DiscFloorMm(p):0.000} vs {a.DiscFloorMm(p):0.000}");
        // 自证：若用写死的 26.0，两者会**完全相等**（那正是修掉的那个 bug）
        Assert.NotEqual(a.DiscFloorMm(p), b.DiscFloorMm(p), 6);
    }

    /// <summary>
    /// 这次修正**对两档的影响不一样**，实测如下（写死在断言里，将来一改就红）：
    ///
    /// <code>
    ///           无支撑宽度 b   屈曲支     旧值(b=R−26)   烧穿底
    ///  W08 R30      4.20       0.5818      0.5541        0.600  ⇒ 下界仍 0.600（不变）
    ///  W06 R30      4.40      **0.6094**   0.5541        0.600  ⇒ 下界抬到 0.6094
    /// </code>
    ///
    /// ★ 也就是说：**旧代码在 0.6 档上允许了比抗屈曲极限更薄的板。**
    ///   定案 W06 最薄一片是 0.62，仍在新下界之上 ⇒ **定案安全**，
    ///   但裕度从 0.020 mm 缩到 0.0106 mm。
    /// </summary>
    [Fact]
    public void 修正对两档的影响_08不变_06下界抬高()
    {
        var p = new DesignInputs();
        // 0.8 档：屈曲支仍低于烧穿底 ⇒ 下界不变
        Assert.Equal(p.WeldMinThicknessMm, FinalDesign.W08.DiscFloorMm(p), 6);
        // 0.6 档：屈曲支反超 ⇒ 下界高于烧穿底
        double f06 = FinalDesign.W06.DiscFloorMm(p);
        Assert.True(f06 > p.WeldMinThicknessMm,
            $"0.6 档的下界应由屈曲主导：{f06:0.0000} vs 烧穿底 {p.WeldMinThicknessMm:0.000}");
        Assert.Equal(0.6094, f06, 4);   // 实测 0.60943
        // ★ 定案 W06 仍造得出来 —— 这一条不过，就是这次修正把现役档判死了，必须当场知道
        Assert.True(FinalDesign.W06.TabThickMm.Min() > f06,
            $"定案 W06 最薄片 {FinalDesign.W06.TabThickMm.Min():0.000} 必须仍高于新下界 {f06:0.0000}");
    }
}
