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
        // ★ 用户 2026-09-08：每次论证验算跑 2 段（3 片法兰）；3 段的 3480.7 g 只当归档基准，不再重跑。
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        // ★ 边跑边落轨迹（2026-09-08）：此前只在跑完才写档，2.5 小时里一行都看不到，分不清慢和挂。
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "对帐超时_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, "═══ 0.8 档求解轨迹（2 段 3 片，导航网格；边跑边写，末尾有合计）═══" + Environment.NewLine
                              + "对照：3 段的归档基准 3480.7 g（bba08c7 之前口径，不可与 2 段直接比）" + Environment.NewLine + Environment.NewLine);
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

        // ★★★★★ 2026-09-08 晚，用户设计因果链落地后这条门的**意思变了**：
        //   0.8 档（W08：盘 R30、舌宽 60）在「20 °C/h 升温设计电流 ⇒ 按 J=10 定截面」下，共用片要 4.26 mm，
        //   焊脚随之 4.26 ⇒ 盘 R30 盖不住管孔（要 30.06）⇒ **第一次场解之前就按 ⑥ 停、给处方**。
        //   这在新链下是对的答案：这个形状太小，该走第 ② 步「搜形状」（真实路径：UiWiring --segs 2 --searchshape）。
        //   本门钉：① 设计电流印出来了；② J=10 下角抬了；③ 零场解、⑥ 处方且处方数可闭式复算；④ 停因不是「判不了／不收敛」。
        //   固定形状 3480.7 g 那套口径已作废：那份解舌根 J_max 37、热点 +99 K，用户 09-08：完全不可用。
        Assert.Contains(sr.Trace, s => s.Contains("设计电流", StringComparison.Ordinal));
        Assert.Contains(sr.Trace, s => s.TrimStart().StartsWith(BranchMarks.JFloorRaised, StringComparison.Ordinal));
        Assert.NotNull(sr.DesignCurrent);
        Assert.Equal(0, sr.Solves);
        Assert.Contains("⑥", sr.StopWhy);
        var mNeed = System.Text.RegularExpressions.Regex.Match(sr.StopWhy, @"需要 ([0-9.]+) mm");
        Assert.True(mNeed.Success, "⑥ 处方里没有「需要 X mm」：" + sr.StopWhy);
        double need = double.Parse(mNeed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(d.HoleRadiusMm + Math.Max(sr.Design.TabThickMm.Max(), d.WallMm), need, 2);
        Assert.False(sr.StopWhy.Contains("判不了", StringComparison.Ordinal), $"停因不该是判不了：{sr.StopWhy}");
        Assert.False(sr.StopWhy.Contains("不收敛", StringComparison.Ordinal), $"停因不该是不收敛：{sr.StopWhy}");
    }
}
