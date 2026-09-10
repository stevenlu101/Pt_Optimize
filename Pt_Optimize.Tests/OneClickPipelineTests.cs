using System;
using System.IO;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **【核算整线】一键跑到底**（2026-09-02 用户拍板）。
///
/// 用户：「自动定厚 / ◇ 搜形状 / ◆ 加密复算，能自动吗？全整到核算整线」，
/// 并选定「搜形状也自动跑，一路跑到底」+「要有进度条 + 状态说明，蓝色指示保留（说明要改）」。
///
/// ══ 关键：**不新造决策逻辑**
///
/// 「下一步该干什么」<see cref="Flow.Next"/> 早就在回答（蓝色指示读的就是它）。
/// 一键 = 照它一直走。自动跑与手动点走的是**同一条判断** ——
/// 否则会出现「链路说 A、一键做 B」，那是两个真相。
/// </summary>
public class OneClickPipelineTests
{
    private static string Ui(string f) => File.ReadAllText(
        Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f));

    [Fact]
    public void 核算整线按钮跑的是流水线()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("_btnRun = Btn(\"核算整线\", (_, _) => _ = RunPipelineAsync());", s);
        Assert.Contains("private async Task RunPipelineAsync()", s);
    }

    /// <summary>★ 决策必须来自 Flow.Next，不许在流水线里另写一套「什么时候该定厚」。</summary>
    [Fact]
    public void 流水线的决策来自蓝色指示()
    {
        string s = Ui("LineDesignPage.cs");
        int a = s.IndexOf("private async Task RunPipelineAsync()", StringComparison.Ordinal);
        int b = s.IndexOf("private async Task ReproduceAsync()", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        string body = s[a..b];
        Assert.Contains("Flow.Next(Shared!, App)", body);
        // 四条腿都接上了（core.fineResolve 是第五条：R26，2026-09-09 加密复算不过后的出口）
        foreach (string id in new[] { "core.autoThick", "shape.search", "core.verifyMesh",
                                       "core.fineResolve", "core.runLine" })
            Assert.Contains(id, body);
    }

    /// <summary>★★ 三条护栏：取消断链、原地打转会停、走满上限要说出来。</summary>
    [Fact]
    public void 三条护栏都在()
    {
        string s = Ui("LineDesignPage.cs");
        int a = s.IndexOf("private async Task RunPipelineAsync()", StringComparison.Ordinal);
        int b = s.IndexOf("private async Task ReproduceAsync()", StringComparison.Ordinal);
        string body = s[a..b];

        Assert.Contains("if (_pipeAborted) break;", body);          // ① 取消/出错断整条
        Assert.Contains("逐字未变", body);                            // ② 原地打转就停
        Assert.Contains("走满 8 步", body);                           // ③ 撞上限要说出来
        // 取消/出错的每一处都要把 _pipeAborted 置起 —— 少一处就是「取消了还往下跑」
        Assert.True(s.Split("_pipeAborted = true").Length - 1 >= 5,
            "取消/出错的置位点太少 —— 漏一处就会「取消了还继续往下走」");
    }

    /// <summary>
    /// ★ 指到「要人决定」的命令（.3dm 那条路的 ◈ 图纸几何 → 参数）必须**停下来说清楚**，
    /// 不许替工程师把设计从图纸路搬到解析路 —— 那是决定，不是一步计算。
    /// </summary>
    [Fact]
    public void 指到要人决定的就停下来()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("自动到此为止 —— 下一步要你决定", s);
    }

    /// <summary>★ 进度要说清「第几步／在做什么」，否则跑一小时只看到一行滚动的轮数。</summary>
    [Fact]
    public void 进度带步号()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("private string _pipeStep", s);
        Assert.Contains("_pipeStep.Length > 0 ? _pipeStep", s);
        Assert.Contains("第 1 步／解一次整线", s);
    }

    /// <summary>
    /// ★★ 蓝色指示**保留但改说法**：不再教工程师去点「自动定厚」——
    /// 那已经是程序自己做的事；它现在说的是「点核算整线，它会自己往下走」。
    /// </summary>
    [Fact]
    public void 蓝链不再教人点自动定厚()
    {
        string flow = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "Flow.cs"));
        int a = flow.IndexOf("public static NextStep? Next(", StringComparison.Ordinal);
        int b = flow.IndexOf("═══ 查询", a, StringComparison.Ordinal);
        string body = flow[a..b];

        Assert.DoesNotContain("用「自动定厚」把厚度调到过", body);
        Assert.DoesNotContain("点「◇ 搜形状」（几十分钟）", body);
        // 而且要说清「它会自己往下走」
        Assert.Contains("可以出图", body);            // R34（2026-09-10）：文案改成五步链，落点仍是「可以出图」
        Assert.Contains("它会自己", body);
    }
}
