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
}
