using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **两级机制** —— 流水线的最后一段（2026-08-28）。
///
/// ★ 用户要的流水线：**输入（3DM / UI）→ 优化（粗网格导航）→ 网格无关复核 → 报告出图**。
///   「**我不要定档这种模式（这坑太大），要严格遵守第一性原理**」
///   ⇒ 复核**不是**为了产出「定案档」，它就是**本次运行的判据以什么为准**。
///
/// ★ 为什么非两级不可（实测逼出来的）：网格无关要到 **0.408 mm**、单次求解 **910 秒**，
///   而 D8 一个形状 40 轮、搜形状几十个形状 ⇒ 在网格无关的网格上做优化**算不完**。
/// </summary>
public class MeshVerifyTests
{
    private static string Src(string f) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), f));

    [Fact]
    public void 复核容差与自检对账同口径_不另立一套()
    {
        var t = MeshVerify.TolTemplate();
        Assert.Equal(3, t.Count);
        Assert.Equal(0.5, t.First(x => x.Name == "②′").Tol, 9);
        Assert.Equal(0.2, t.First(x => x.Name == "②″").Tol, 9);
        Assert.Equal(1.0, t.First(x => x.Name == "③").Tol, 9);
    }

    [Fact]
    public void 复核只复核不优化_不许改设计()
    {
        // 它的入参是一个**已经算出来的**设计；类里不许出现 Sizer/优化字样。
        string s = Src("Pt_Optimize/Core/MeshVerify.cs");
        Assert.DoesNotContain("Sizer.", s);
        Assert.Contains("只复核，不优化", s);
    }

    [Fact]
    public void 解不出来不等于通过()
    {
        // 复核那一档若解不出来，必须明说「不能说这个设计过了」——
        // 「算不出来」被读成「没问题」是本项目记过案的形态。
        Assert.Contains("算不出来不等于通过", Src("Pt_Optimize/Core/MeshVerify.cs"));
    }

    /// <summary>
    /// ★ 没复核的那句警告必须**无论有没有赢家都印**。
    ///   第一版把它放进「有赢家」那一支 —— 于是**没选出赢家时反而不提醒**，
    ///   而那正是人最可能去放宽条件重跑的时候，也最需要知道「上表还没算准」。
    /// </summary>
    [Fact]
    public void 没复核必须无条件提醒()
    {
        string s = Src("Pt_Optimize/Program.cs");
        Assert.Contains("上表是「导航网格」上的值，还没做网格无关复核", s);
        Assert.Contains("过与不过都可能是假的", s);
        // 提醒必须在 win 分支**之外**：紧跟在「没有全过的形状」那句之后
        int noWin = s.IndexOf("本网格内**没有全过的形状**", System.StringComparison.Ordinal);
        int warn = s.IndexOf("上表是「导航网格」上的值", System.StringComparison.Ordinal);
        Assert.True(noWin > 0 && warn > noWin, "提醒必须放在 win 分支之外，否则没赢家时不提醒");
    }

    [Fact]
    public void 复核跑出来时_判据以复核为准()
    {
        string s = Src("Pt_Optimize/Program.cs");
        Assert.Contains("判据以复核为准", s);
        Assert.Contains("在算得准的网格上，这个设计不过", s);   // 复核不过时要说重话
    }
}
