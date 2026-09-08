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
    /// <summary>同步的进度接收器（Progress&lt;T&gt; 是异步投递的，落档会乱序）。</summary>
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 打出0点8档的求解轨迹()
    {
        var d = DesignSpec.Builtin[0].Clone();       // W08
        // ★ 边跑边落轨迹（2026-09-08）：此前只在跑完才写档，2.5 小时里一行都看不到，分不清慢和挂。
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "对帐超时_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, "═══ 0.8 档求解轨迹（导航网格；边跑边写，末尾有合计）═══" + Environment.NewLine
                              + "对照：命令行验过的 0.8 档是 3480.7 g 全判据过（细网格口径）" + Environment.NewLine + Environment.NewLine);
        var sw = Stopwatch.StartNew();
        var live = new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}" + Environment.NewLine));
        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            FineMm = 0, FineRadiusMm = 0,
            MaxRounds = 15,
            MaxPartialRounds = 2,
        }, live);
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"═══ 合计：耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次　"
                    + $"可行 {sr.Feasible}　合计 {sr.MassG:0.0} g");
        sb.AppendLine($"停在：{sr.StopWhy}");
        File.AppendAllText(dump, sb.ToString());
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
        // ★ 2026-09-08 实测：熔化两层修好后 0.8 档回到 3480.7 g、三条逐片判据全过，唯一剩下的是
        //   **法兰 J 判不了**（C1 已知阻塞，R18 未做）⇒ Feasible 仍 False。门改准：剩余的不过／判不了
        //   **只许是法兰 J**，多一条就红；R18 做完把这段换回 Assert.True(sr.Feasible)。
        Assert.NotNull(sr.Best);
        var leftover = sr.Best!.Checks
            .Where(c => c.Kind != CheckKind.Reference && !(c.Ok && !c.Undetermined))
            .Select(c => c.Name).ToArray();
        Assert.True(leftover.All(n => n.Contains("法兰 J", StringComparison.Ordinal)),
            $"0.8 档除了已知的「法兰 J 判不了」（R18）之外还有判据不过：{string.Join("、", leftover)}；停在：{sr.StopWhy}");
        Assert.True(sr.Feasible || leftover.Length > 0,
            "Feasible=false 却找不到任何不过的判据 —— AllOk 与 Checks 对不上");
        // ★ 探针态邻片熔 ⇒ 副本上抬：0.8 档第 1 轮必踩（板厚探 6 mm／t₂ 探 2.5 让邻片熔）—— 断言「走到了」
        Assert.Contains(sr.Trace, s => s.TrimStart().StartsWith(BranchMarks.MeltProbeRaised, StringComparison.Ordinal));
    }
}
