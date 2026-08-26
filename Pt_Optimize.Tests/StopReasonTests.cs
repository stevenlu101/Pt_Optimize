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

    // ── 跑满上限有**两种**，说成一种就是误导（2026-08-25 实测撞到） ──────

    /// <summary>
    /// R30／壁0.8 跑 200 轮的结果与 40 轮**逐位相同**（3547 g），
    /// 轨迹是个极限环（3547 → 3553 → 3537 越界 → 回来）—— 它早就稳了。
    /// 而停因照旧说「可能只是被截断」：**那句话把人指向加轮数**，
    /// 可真正卡住它的是判据边界（②′ 第 3 片），加多少轮都没用。
    /// </summary>
    [Fact]
    public void 跑满上限但早就不再改善_要说已经稳定而不是被截断()
    {
        var r = Sizer.StopReason(200, 200, "", bestRound: 10);
        Assert.True(r.HitCap);
        Assert.Contains("已经稳定", r.Why);
        Assert.Contains("不是被截断", r.Why);
        Assert.Contains("10", r.Why);              // 得说清最好点在第几轮
        Assert.DoesNotContain("可能只是被截断", r.Why);
    }

    [Fact]
    public void 跑满上限且最好点就在最后_才说可能被截断()
    {
        var r = Sizer.StopReason(200, 200, "", bestRound: 199);
        Assert.True(r.HitCap);
        Assert.Contains("可能只是被截断", r.Why);
        Assert.Contains("仍在改善", r.Why);
    }

    [Fact]
    public void 自证_两种跑满上限说的不是同一句话()
    {
        // 没有这一条，上面两条可能都在验同一段文字里碰巧都有的词。
        Assert.NotEqual(Sizer.StopReason(200, 200, "", 10).Why,
                        Sizer.StopReason(200, 200, "", 199).Why);
    }

    [Fact]
    public void 阈值边界_正好停滞够久就算稳定()
    {
        int b = 200 - SizerOptions.StaleRounds;      // idle 正好 = StaleRounds
        Assert.Contains("已经稳定", Sizer.StopReason(200, 200, "", b).Why);
        Assert.Contains("可能只是被截断", Sizer.StopReason(200, 200, "", b + 1).Why);
    }

    [Fact]
    public void 没给最好点轮号时_退回保守说法_不假装知道()
    {
        // 老调用方（不传 bestRound）不该被说成「已经稳定」—— 那是**没有依据的乐观**。
        Assert.Contains("可能只是被截断", Sizer.StopReason(40, 40, "").Why);
    }

}
