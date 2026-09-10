using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 「搜形状」的每轮决策 —— 用户 2026-08-25：
/// 「改变法兰直径与扫梯度分布**算一轮**」「有好的方向则继续，如果都是变坏即刻停止」。
///
/// 这一轮本身要跑几十分钟，所以决策必须能**离开那个循环**单独验。
/// </summary>
public class ShapeSearchPlanTests
{
    [Fact]
    public void FourNeighbours_TwoOnDiameter_TwoOnWidth()
    {
        var n = ShapeSearchPlan.Neighbours(30, 30 * 0.75);
        Assert.Equal(4, n.Length);
        Assert.Contains(n, x => Math.Abs(x.R - 35) < 1e-9);
        Assert.Contains(n, x => Math.Abs(x.R - 25) < 1e-9);
        Assert.Equal(2, n.Count(x => Math.Abs(x.R - 30) < 1e-9));
    }

    /// <summary>★ 盘径变了，半宽要**按比例跟着走** —— 保持绝对值会撞上「半宽被盘径夹住」。</summary>
    [Fact]
    public void WidthFollowsTheDiameter_ByRatio()
    {
        var n = ShapeSearchPlan.Neighbours(30, 30 * 0.75);
        var smaller = n.First(x => Math.Abs(x.R - 25) < 1e-9);
        Assert.Equal(25 * 0.75, smaller.HalfW, 9);
        var bigger = n.First(x => Math.Abs(x.R - 35) < 1e-9);
        Assert.Equal(35 * 0.75, bigger.HalfW, 9);
    }

    /// <summary>比例夹在 [0.25, 1.0]：半宽不该超过盘半径，也不该窄到没有过流截面。</summary>
    [Fact]
    public void RatioIsClamped()
    {
        var hi = ShapeSearchPlan.Neighbours(30, 30 * 1.00);
        Assert.All(hi, x => Assert.True(x.HalfW <= x.R + 1e-9, $"半宽 {x.HalfW} > 盘半径 {x.R}"));
        var lo = ShapeSearchPlan.Neighbours(30, 30 * 0.25);
        Assert.All(lo, x => Assert.True(x.HalfW >= 0.25 * x.R - 1e-9));
    }

    /// <summary>省不到门槛就不算变好 —— 否则会为了 0.1 g 再跑一轮几十分钟。</summary>
    [Theory]
    [InlineData(3000, 2990, true)]     // 省 10 g，算
    [InlineData(3000, 2999.8, false)]  // 省 0.2 g，落在噪声里，不算
    [InlineData(3000, 3100, false)]    // 变差
    [InlineData(3000, 3000, false)]    // 持平
    public void ImprovedNeedsARealMargin(double before, double after, bool want)
        => Assert.Equal(want, ShapeSearchPlan.Improved(before, after));

    /// <summary>无解（NaN）不算变好 —— 「算不出来」不是「更好」。</summary>
    [Fact]
    public void NaNIsNotImprovement()
    {
        Assert.False(ShapeSearchPlan.Improved(3000, double.NaN));
        Assert.False(ShapeSearchPlan.Improved(double.NaN, 2000));
    }

    /// <summary>同一个形状不许算两次 —— 每次都是几十分钟。</summary>
    [Fact]
    public void AlreadyTriedShapesAreSkipped()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = ShapeSearchPlan.Worth(ShapeSearchPlan.Neighbours(30, 22.5), seen);
        Assert.Equal(4, first.Count);                     // 自证：第一次四个都值得试
        var again = ShapeSearchPlan.Worth(ShapeSearchPlan.Neighbours(30, 22.5), seen);
        Assert.Empty(again);                              // 第二次一个都不该重算
    }

    /// <summary>几何上说不通的邻点要滤掉（盘径太小、半宽太窄）。</summary>
    [Fact]
    public void NonsenseShapesAreDropped()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var got = ShapeSearchPlan.Worth(
            new[] { (R: 3.0, HalfW: 2.0), (R: 30.0, HalfW: 0.5), (R: 30.0, HalfW: 22.5) }, seen);
        Assert.Single(got);
        Assert.Equal(30.0, got[0].R, 9);
    }

    // ════════════════════════════════════════════════════════════════
    // R38（2026-09-11）：锥形舌片也进搜索空间 —— 用户 09-10 工单第 2 条点名
    // 「搜形状不搜锥形与孔族，只搜盘径／舌宽」，09-11 定了目标「一次搜完，看到
    // 每一族最轻的可行形状，以及锥形有没有帮助」。这几道门钉的是：
    // 带锥形的新重载给 5 个邻点、只有第 5 个翻锥形；去重键认锥形；
    // 旧签名（不带锥形）逐位不变，与新重载 Taper=false 时的前 4 个邻点逐元素相同。
    // ════════════════════════════════════════════════════════════════

    /// <summary>带锥形的新重载给 5 个邻点：前 4 个与旧的 4 邻点一样（盘径±/舌宽比例±），
    /// 第 5 个是「原地不动，翻转锥形」。</summary>
    [Fact]
    public void 带锥形的邻点是五个_只有第五个翻锥形()
    {
        var n = ShapeSearchPlan.Neighbours(30, 30 * 0.75, false);
        Assert.Equal(5, n.Length);
        for (int i = 0; i < 4; i++)
            Assert.False(n[i].Taper, $"前 4 个邻点不该动锥形（第 {i} 个）");
        Assert.Equal(30.0, n[4].R, 9);
        Assert.Equal(30 * 0.75, n[4].HalfW, 9);
        Assert.True(n[4].Taper, "第 5 个邻点该是「原地不动、翻转锥形」");

        // 从锥形出发时同理：前 4 个仍是锥形，第 5 个翻成平行边
        var n2 = ShapeSearchPlan.Neighbours(30, 30 * 0.75, true);
        for (int i = 0; i < 4; i++) Assert.True(n2[i].Taper);
        Assert.False(n2[4].Taper);
    }

    /// <summary>新重载前 4 个邻点的盘径/半宽与旧的（不带锥形的）4 邻点重载逐元素相同——
    /// 锥形只是多加的一维，不改盘径/舌宽这两维原来的算法。</summary>
    [Fact]
    public void 新重载前四个邻点与旧重载逐位相同()
    {
        var old4 = ShapeSearchPlan.Neighbours(30, 22.5, 3.0);          // 旧签名（步长收缩版）
        var new5F = ShapeSearchPlan.Neighbours(30, 22.5, false, 3.0);  // 新重载，Taper=false
        var new5T = ShapeSearchPlan.Neighbours(30, 22.5, true, 3.0);   // 新重载，Taper=true

        Assert.Equal(4, old4.Length);
        Assert.Equal(5, new5F.Length);
        Assert.Equal(5, new5T.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(old4[i].R, new5F[i].R, 9);
            Assert.Equal(old4[i].HalfW, new5F[i].HalfW, 9);
            Assert.Equal(old4[i].R, new5T[i].R, 9);
            Assert.Equal(old4[i].HalfW, new5T[i].HalfW, 9);
        }
    }

    /// <summary>无参（默认步长）版本与传入 DiscStepMm 完全等价 —— 与旧的无参重载同一个约定。</summary>
    [Fact]
    public void 带锥形的无参重载等价于传初始步长()
    {
        var a = ShapeSearchPlan.Neighbours(40, 20, false);
        var b = ShapeSearchPlan.Neighbours(40, 20, false, ShapeSearchPlan.DiscStepMm);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].R, b[i].R, 9);
            Assert.Equal(a[i].HalfW, b[i].HalfW, 9);
            Assert.Equal(a[i].Taper, b[i].Taper);
        }
    }

    /// <summary>去重键把锥形也写进去：同一个（盘径/半宽），平行边与锥形边是两个不同的候选，
    /// 不该被互相去重掉。</summary>
    [Fact]
    public void Key带锥形时平行边与锥形边不互相去重()
    {
        Assert.NotEqual(ShapeSearchPlan.Key(30, 22.5, false), ShapeSearchPlan.Key(30, 22.5, true));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cand = new[] { (R: 30.0, HalfW: 22.5, Taper: false), (R: 30.0, HalfW: 22.5, Taper: true) };
        var kept = ShapeSearchPlan.Worth(cand, seen);
        Assert.Equal(2, kept.Count);      // 两个都值得试——不是同一个形状
        var again = ShapeSearchPlan.Worth(cand, seen);
        Assert.Empty(again);              // 但各自第二次出现时都该被挡住
    }

    /// <summary>不带锥形的旧 Key/Worth 签名逐位不变 —— 新重载不改老调用点的行为。</summary>
    [Fact]
    public void 旧的Key与Worth签名不受影响()
    {
        Assert.Equal("30/22.5", ShapeSearchPlan.Key(30, 22.5));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var got = ShapeSearchPlan.Worth(new[] { (R: 30.0, HalfW: 22.5) }, seen);
        Assert.Single(got);
    }
}
