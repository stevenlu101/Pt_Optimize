using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（2026-09-08 晚）：搜形状在 盘Ø70／舌宽70 那一点报「片0 ③ 法兰侧候选都不成立：舌保温已在上界…」，
/// 而单次场解的扫描（deliverable/杠杆扫描_盘70舌70.txt）显示舌保温 0.3→20 mm 能把 ③ 从 286 K 压到 5.9 K。
/// 把这一点用求解器**单独**跑一遍、轨迹边跑边落档，看它是在哪一轮、被哪根旋钮推出可行窗口的。只看，不判。
/// </summary>
public class ShapePointTraceTests
{
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 打出盘70舌70的求解轨迹()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.DiscRadiusMm = 35; d.TabHalfWidthMm = 35;
        d.TabLengthMm = 0 + d.ClampLengthMm + 100;
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "形状_盘70舌70_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, "═══ 盘Ø70／舌宽70／舌长 140（2 段 3 片，0.8 档）求解轨迹（与搜形状同一构型，16 轮筛）═══" + Environment.NewLine);
        var sw = Stopwatch.StartNew();
        var live = new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}" + Environment.NewLine));
        var sr = Solver.Solve(d, p, new SolverOptions { FineMm = 0, MaxRounds = 16 }, live);
        sw.Stop();
        File.AppendAllText(dump, Environment.NewLine + $"═══ 合计：耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g" + Environment.NewLine + $"停在：{sr.StopWhy}" + Environment.NewLine);
        Assert.True(sr.Trace.Count > 0);
    }
}
