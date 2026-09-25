using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **搜形状的步长必须会收缩**（2026-08-28 算法普查 A 类第 ⑥ 条）。
///
/// 病灶：此前盘径步长写死 5 mm、**永不收缩**，停机条件是「四个邻点都不更好」。
/// 那句话真正的意思只是「**在 ±5 mm 这个分辨率上**没有更好」——
/// 而铂重对盘径是一阶敏感的，5 mm 完全可能跨过最优点。
/// 把它当成「局部最优」，与把 2 mm 网格上的判据当成结论是**同一种错**：
/// 都是拿一个碰巧的分辨率冒充结论。
///
/// 改法与网格无关性同构：没有更好 ⇒ **步长减半再试**，直到 <see cref="ShapeSearchPlan.MinDiscStepMm"/>。
/// 于是交付里能写一句站得住的话：「在 ±0.625 mm 分辨率上是局部最优」。
/// </summary>
public class ShapeStepRefineTests
{
    [Fact]
    public void 步长减半()
    {
        Assert.Equal(2.5, ShapeSearchPlan.Refine(5.0), 9);
        Assert.Equal(1.25, ShapeSearchPlan.Refine(2.5), 9);
    }

    /// <summary>5 → 2.5 → 1.25 → 0.625 恰好三次减半到下界，再减一次就到头。</summary>
    [Fact]
    public void 从初始步长恰好三次减半到分辨率下界()
    {
        double s = ShapeSearchPlan.DiscStepMm;
        int n = 0;
        while (!ShapeSearchPlan.StepExhausted(s)) { s = ShapeSearchPlan.Refine(s); n++; }
        Assert.Equal(4, n);                                    // 5→2.5→1.25→0.625→0.3125(到头)
        Assert.Equal(ShapeSearchPlan.MinDiscStepMm, 0.625, 9);
    }

    [Fact]
    public void 下界之上不算到头下界之下才算()
    {
        Assert.False(ShapeSearchPlan.StepExhausted(ShapeSearchPlan.MinDiscStepMm));
        Assert.False(ShapeSearchPlan.StepExhausted(1.25));
        Assert.True(ShapeSearchPlan.StepExhausted(ShapeSearchPlan.MinDiscStepMm * 0.5));
    }

    /// <summary>
    /// ★ 两个方向要**同步**收缩。一个细一个粗的话，「四个邻点都不更好」
    /// 在盘径方向和舌宽方向说的就不是同一件事，停机结论也就没有意义。
    /// </summary>
    [Fact]
    public void 舌宽比例的步长按同样比例缩()
    {
        const double R = 40, hw = 20;                          // f0 = 0.5
        var full = ShapeSearchPlan.Neighbours(R, hw, ShapeSearchPlan.DiscStepMm);
        var half = ShapeSearchPlan.Neighbours(R, hw, ShapeSearchPlan.DiscStepMm * 0.5);

        // 盘径方向：步长确实减半
        Assert.Equal(R + 5.0, full[0].R, 9);
        Assert.Equal(R + 2.5, half[0].R, 9);

        // 舌宽方向：比例增量也减半（0.125 → 0.0625，换算成半宽是 R×增量）
        double dFull = full[2].HalfW - hw, dHalf = half[2].HalfW - hw;
        Assert.Equal(R * ShapeSearchPlan.FracStep, dFull, 9);
        Assert.Equal(dFull * 0.5, dHalf, 9);
    }

    /// <summary>无参重载必须与「传入初始步长」完全等价 —— 否则老调用点会悄悄换行为。</summary>
    [Fact]
    public void 无参重载等价于传初始步长()
    {
        var a = ShapeSearchPlan.Neighbours(40, 20);
        var b = ShapeSearchPlan.Neighbours(40, 20, ShapeSearchPlan.DiscStepMm);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].R, b[i].R, 9);
            Assert.Equal(a[i].HalfW, b[i].HalfW, 9);
        }
    }

    /// <summary>盘半径下界可以由调用方按判据⑥ 给（而不是写死的 5）。</summary>
    [Fact]
    public void 盘半径下界可由判据六给出()
    {
        var cand = new[] { (R: 20.0, HalfW: 10.0), (R: 30.0, HalfW: 15.0) };
        var kept = ShapeSearchPlan.Worth(cand, new System.Collections.Generic.HashSet<string>(), 26.6);
        Assert.Single(kept);
        Assert.Equal(30.0, kept[0].R, 9);
    }

    /// <summary>
    /// ★★ **接线验**。这个项目的头号病是「造好了没接线」——
    /// 收缩逻辑写在 Core 里而搜形状循环没用，等于什么都没改。
    /// </summary>
    [Fact]
    public void 搜形状循环真的用了收缩后的步长()
    {
        // 2026-09-25：搜形状循环在 Core/ShapeSearchDriver（界面只调它），门改读驱动
        string ui = File.ReadAllText(
            Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShapeSearchDriver.cs"));

        Assert.Contains("double step = ShapeSearchPlan.DiscStepMm;", ui);
        Assert.Contains("ShapeSearchPlan.Neighbours(R0, hw0, curTaper, step)", ui);   // R38：邻域探索多带一维锥形
        Assert.Contains("ShapeSearchPlan.Refine(step)", ui);
        Assert.Contains("ShapeSearchPlan.StepExhausted", ui);

        // 「没有更好」不许再直接 break —— 必须先收缩
        Assert.DoesNotContain("if (!better) break;", ui);
    }
}
