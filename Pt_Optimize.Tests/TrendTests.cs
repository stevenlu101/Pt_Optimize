using System.Collections.Generic;
using PtOptimize.Core;
using Xunit;
using T = PtOptimize.Core.FlangeAutoSizer.Trend;

namespace PtOptimize.Tests;

/// <summary>
/// 逐级定厚的「还值不值得再跑一轮」判据。
///
/// 这条判断的两个出口给的是**相反**的建议（加大轮数 / 回图上改几何），
/// 判反了，代价是工程师往错的方向再花几十分钟。所以把它从循环里抽出来单独验：
/// 循环本身要跑真解、分钟级，验不动；这个函数是纯的，全部走完不到 1 ms。
/// </summary>
public class TrendTests
{
    [Theory]
    // 数据不够就说不够 —— 别拿两个点去判趋势
    [InlineData(T.数据不足, new double[0])]
    [InlineData(T.数据不足, new[] { 10.0 })]
    [InlineData(T.数据不足, new[] { 10.0, 9.0 })]
    // 一路往下走 ⇒ 轮数不够，加轮数有用
    [InlineData(T.还在缩, new[] { 100.0, 80.0, 64.0, 51.0 })]
    [InlineData(T.还在缩, new[] { 100.0, 50.0, 25.0 })]
    // 进平台 ⇒ 再跑也是这个数
    [InlineData(T.已停滞, new[] { 100.0, 99.0, 98.5 })]
    [InlineData(T.已停滞, new[] { 100.0, 50.0, 49.0, 48.9 })]
    // ★ 震荡：只看最后一个值会读成「又变好了」或「越跑越差」，两个方向都会骗人。
    //   取窗口内最好值就不会被一次抖动带跑。
    [InlineData(T.已停滞, new[] { 100.0, 80.0, 100.0, 80.0 })]
    [InlineData(T.已停滞, new[] { 80.0, 100.0, 80.0, 100.0 })]
    public void TrendOf_ClassifiesResidualHistory(T expect, double[] hist)
        => Assert.Equal(expect, FlangeAutoSizer.TrendOf(hist));

    /// <summary>
    /// 判不了的时候要说判不了。NaN 混进来（某片那一级没算出温度）若被当成「停滞」，
    /// 会把一个还有救的解劝退 —— 这正是本项目的铁律③：不许把「无法判定」当成一个结论。
    /// </summary>
    [Fact]
    public void TrendOf_NaNIsUndetermined_NotStalled()
    {
        var hist = new List<double> { 100.0, double.NaN, 50.0 };
        Assert.Equal(T.数据不足, FlangeAutoSizer.TrendOf(hist));
    }

    /// <summary>
    /// 阈值真的在起作用：同一组数，把「算改善」的门槛调高就该翻面。
    /// 少了这一条，上面那些用例即使阈值被写死成常数也照样全过。
    /// </summary>
    [Fact]
    public void TrendOf_GainThreshold_ActuallyApplies()
    {
        var hist = new[] { 100.0, 90.0, 90.0 };          // 只改善了 10 %
        Assert.Equal(T.还在缩, FlangeAutoSizer.TrendOf(hist, minGain: 0.05));
        Assert.Equal(T.已停滞, FlangeAutoSizer.TrendOf(hist, minGain: 0.20));
    }
}
