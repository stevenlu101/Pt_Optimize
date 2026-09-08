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

        // ★★★★★ **仪器变成门**（2026-09-08 督导第 15/16 封）。
        //   慢门跑完 0.8 档从 3480.7 g 变成 NaN，而本测试报「已通过」—— 它只 dump 轨迹，
        //   对答案零断言，放在慢门道里永远不会红。
        //   钉的是「**解得出**」，**不钉 3480.7** —— 那会因为变好（更省铂）而红，犯门 A。
        Assert.True(double.IsFinite(sr.MassG),
            $"0.8 档合计是 NaN ⇒ 求解器没解出来。停在：{sr.StopWhy}");
        Assert.False(sr.StopWhy.Contains("判不了", StringComparison.Ordinal),
            $"0.8 档在第一步就停了：{sr.StopWhy}");
        // ★ 第三条（2026-09-08 我加的，不在督导给的两条里）：**0.8 档必须解得出且全过**。
        //   实测：下角补进「不熔化」之后，上面两条都过了，但可行 False（3257.7 g，第 1 轮就收场：
        //   候选探到上界时邻片熔 ⇒ 探针一律「解不出来」⇒ 片1 三个候选全败）。
        //   两条绿着而交不出东西 = 「让人以为的和事实不一样」。这条只会因为变差而红，不犯门 A。
        Assert.True(sr.Feasible,
            $"0.8 档解出来了但**不可行**（合计 {sr.MassG:0.0} g）：{sr.StopWhy}");
    }
}
