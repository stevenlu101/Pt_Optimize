using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 「**一路变坏就即刻停**」（用户 2026-08-25）的判断本身。
///
/// 两个定尺寸器各有一个几十分钟的循环都要用它；写在循环里就只能跑满几十分钟才验得到，
/// 而它是纯算术，微秒可验 —— 同一个教训今天第四次
/// （TrendOf / LevelThicknessFor / JointThickness 都是这么抽出来的）。
///
/// 口径：hist **越小越好**；只数**严格更差**的连续轮数，持平不算
/// （持平归 <see cref="FlangeAutoSizer.TrendOf"/> 的「已停滞」管，两条规则不抢同一件事）。
/// </summary>
public class WorseningRunTests
{
    [Fact]
    public void Improving_CountsZero()
        => Assert.Equal(0, FlangeAutoSizer.WorseningRun(new[] { 9.0, 7.0, 5.0, 3.0 }));

    [Fact]
    public void EveryRoundWorse_CountsAll()
        => Assert.Equal(3, FlangeAutoSizer.WorseningRun(new[] { 1.0, 2.0, 3.0, 4.0 }));

    /// <summary>只数**末尾**那一段：中间变差过、最后又好起来 ⇒ 不该停。</summary>
    [Fact]
    public void OnlyTheTailRunCounts()
        => Assert.Equal(0, FlangeAutoSizer.WorseningRun(new[] { 1.0, 5.0, 6.0, 2.0 }));

    /// <summary>持平不算变差 —— 否则会和「已停滞」抢同一件事，且把正常收敛尾段误判。</summary>
    [Fact]
    public void FlatIsNotWorse()
        => Assert.Equal(0, FlangeAutoSizer.WorseningRun(new[] { 3.0, 3.0, 3.0 }));

    /// <summary>单轮抖动不该触发停止（阻尼/反饱和会让某一轮短暂变差）。</summary>
    [Fact]
    public void SingleBumpIsNotEnoughToStop()
    {
        int n = FlangeAutoSizer.WorseningRun(new[] { 5.0, 4.0, 4.5 });
        Assert.Equal(1, n);
        Assert.True(n < 3, "单轮回升就停会误杀正常抖动");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TooShortToJudge(int n)
        => Assert.Equal(0, FlangeAutoSizer.WorseningRun(new double[n]));

    /// <summary>自证：这条判断确实会在「连续 3 轮变差」时越过门槛，不是永远进不去。</summary>
    [Fact]
    public void ThreeConsecutiveWorse_CrossesTheThreshold()
        => Assert.True(FlangeAutoSizer.WorseningRun(new[] { 10.0, 11.0, 12.0, 13.0 }) >= 3);
}
