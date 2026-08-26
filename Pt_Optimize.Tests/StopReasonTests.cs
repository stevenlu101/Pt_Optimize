using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 定尺寸循环「为什么停」—— 纯函数，因为 Solve 本身是分钟级的，
/// 而这是一条**判断**，判断该有微秒级的门守着（本项目的老规矩）。
///
/// ★ 病灶（2026-08-25）：此前 SizerResult 既不报轮数也不报停因 ⇒
///   「这个形状真的无解」与「轮数不够、被截断了」在输出上**长得一模一样**。
///   界面粗筛只跑 16 轮（比 CLI 默认的 40 更容易截断），偏偏那是最常走的那条路 ——
///   于是一个只是没跑够的形状，会被读成「这个形状不行」而被划掉。
/// </summary>
public class StopReasonTests
{
    [Fact]
    public void 有早停原因时_照原样交回且不算跑满()
    {
        var r = Sizer.StopReason(7, 40, "连续 3 轮越调越差，方向错了");
        Assert.False(r.HitCap);
        Assert.Equal("连续 3 轮越调越差，方向错了", r.Why);
    }

    [Fact]
    public void 跑满上限_要明说可能只是被截断()
    {
        var r = Sizer.StopReason(40, 40, "");
        Assert.True(r.HitCap);
        Assert.Contains("上限", r.Why);
        Assert.Contains("截断", r.Why);      // 不能只说「跑满了」，要说这意味着什么
    }

    [Fact]
    public void 早停原因优先于跑满_两者同时成立时不误报截断()
    {
        // 最后一轮才触发早停：轮数正好等于上限，但它不是被截断的。
        var r = Sizer.StopReason(40, 40, "所有旋钮都到位或都顶死");
        Assert.False(r.HitCap);
        Assert.Equal("所有旋钮都到位或都顶死", r.Why);
    }

    [Fact]
    public void 没跑满又没早停_不许静默装成收敛()
    {
        // 理论上不该出现。出现了要出声 —— 宁可说「说不出为什么停」，
        // 也不要给一句听起来像收敛的话。
        var r = Sizer.StopReason(5, 40, "");
        Assert.False(r.HitCap);
        Assert.Contains("说不出为什么停", r.Why);
    }

    [Fact]
    public void 轮数超过上限也算跑满_防守性()
    {
        Assert.True(Sizer.StopReason(41, 40, "").HitCap);
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 40, "")]
    [InlineData(40, 40, "")]
    [InlineData(16, 16, "")]
    [InlineData(3, 40, "方向错了")]
    public void 停因永远不为空(int used, int max, string early)
    {
        // 「无法判定」不算通过，「说不出话」也不算解释 —— 任何一条路都必须给出一句人话。
        Assert.False(string.IsNullOrWhiteSpace(Sizer.StopReason(used, max, early).Why));
    }
}
