using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **0.8 档对帐为什么 60 分钟跑不完 —— 把轨迹打出来看**（2026-09-06）。
///
/// 已经猜错两次，不再猜：
/// <code>
///   猜① 续轮 MaxPartialRounds     → 改成「整轮抬不动才花预算」，仍超时
///   猜② 候选数（跳过空转候选）     → 跳掉孔拉长/r₁/r₂，仍超时
/// </code>
/// ⇒ 直接跑，把 <c>SolverResult.Trace</c> 落到文件里看它每一轮在干什么。
///
/// 只跑导航网格（细网格那一遍每次贵一个量级），拿的是**轨迹**不是答案。
/// </summary>
public class ReconcileTraceTests
{
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 打出0点8档的求解轨迹()
    {
        var d = DesignSpec.Builtin[0].Clone();       // W08
        var sw = Stopwatch.StartNew();
        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            FineMm = 0, FineRadiusMm = 0,
            MaxRounds = 15,
            MaxPartialRounds = 2,
        });
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine("═══ 0.8 档求解轨迹（导航网格）═══");
        sb.AppendLine($"耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次　"
                    + $"可行 {sr.Feasible}　合计 {sr.MassG:0.0} g");
        sb.AppendLine($"停在：{sr.StopWhy}");
        sb.AppendLine();
        sb.AppendLine("对照：命令行验过的 0.8 档是 3480.7 g 全判据过（细网格口径）");
        sb.AppendLine();
        foreach (var t in sr.Trace) sb.AppendLine(t);

        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "对帐超时_轨迹.txt"),
                          sb.ToString());
        Console.WriteLine($"耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次");
        Assert.True(sr.Trace.Count > 0, "轨迹是空的 —— Trace 没在记");
    }
}
