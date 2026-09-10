using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R23（用户 2026-09-09：「挖孔会造成 J 超过设计值，所以舌片必须重新搜形状」；叉口用直椭圆+弯椭圆或圆角三角拼）：
/// 孔径旋钮不再被「舌片厚不变」的 J 上界杀死；孔一开、舌片厚就按 I/(J·最窄有效宽) 闭式重定，退回就回原值。
/// </summary>
public class TongueResizeTests
{
    private static (DesignSpec d, DesignInputs p, SolverOptions o, SolverResult res) Corner56()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        var o = new SolverOptions();
        var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, o, res, null, null);      // 设计电流 + 舌片厚闭式（与求解器起点同一口径）
        Assert.NotNull(res.DesignCurrent);
        return (d, p, o, res);
    }

    [Fact]
    public void 孔径上界不再被J定厚杀死_只剩桥宽()
    {
        var (d, p, o, res) = Corner56();
        double hi = Solver.HoleRadiusUpperRawMm(d, p, o, 1, res);
        Assert.True(hi >= 10, $"Ø56/舌56 共用片的孔径上界 {hi:0.00} mm —— R23 之前它恒为 0（舌片按 J 定厚后开孔就超 J）");
        Assert.True(hi <= d.TabHoleRMaxMm(sides: d.TabHoleSidesOf(1)) + 1e-9, "上界不许越过桥宽给的界");
    }

    [Fact]
    public void 开孔就重定舌片厚_退回就回原值()
    {
        var (d, p, o, res) = Corner56();
        double t0 = d.TongueThickMm[1];
        double iA = res.DesignCurrent!.PlateA[1];
        Assert.True(t0 > 0 && iA > 0);

        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 8.0, p, res);
        double t1 = d.TongueThickMm[1];
        // 孔心默认在自由段中点，那里舌宽 56 ⇒ 最窄有效宽 = 56 − 2×8 = 40 ⇒ t = I/(J·40)，向上落 0.01 格
        double expect = Math.Ceiling(iA / (d.JDesignAPerMm2 * 40.0) / 0.01 - 1e-9) * 0.01;
        Assert.Equal(expect, t1, 6);
        Assert.True(t1 > t0, $"开了 8 mm 孔舌片厚该变厚：{t0} → {t1}");

        Solver.SetKnob(d, Solver.Knob.TabHoleR, 1, 0.0, p, res);
        Assert.Equal(t0, d.TongueThickMm[1], 9);                   // 纯函数：几何退回去，厚度也退回去
    }

    [Fact]
    public void 没有圆盘切口时舌片宽度逐位不变()
    {
        var (d, p, _, _) = Corner56();
        var g = d.Plate(1, d.DiscFloorMm(p));
        Assert.Empty(g.DiscSlots); Assert.Empty(g.DiscCutHoles);
        foreach (var (x, w) in SectionSizing.TabWidths(g, d.ClampLengthMm))
        {
            Assert.Equal(0.0, SectionSizing.DiscCutChordMm(g, x, g.HalfWidth(x)), 12);
            Assert.Equal(2 * g.HalfWidth(x), w, 9);
        }
    }
}

/// <summary>
/// ★ 仪器（慢）：把舌保温封在 3 mm，让「法兰增量温降」在 Ø56/舌56 上没免费旋钮可抬 ——
/// 看求解器会不会真的去探舌孔（R23 之前那根旋钮的上界恒为 0、从来没被探过），落地时留痕「★ 舌片厚随切口重定」。
/// 只看不判：轨迹与结果落 deliverable/R23_开孔候选_轨迹.txt。
/// </summary>
public class TongueResizeReachableTests
{
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 舌保温封顶后舌孔进候选_不再被J上界挡在门外()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "R23_开孔候选_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, "═══ R23 仪器：Ø56/舌56/140（2 段 3 片，管保温 10），舌保温上界压到 3 mm ⇒ 法兰增量温降只能靠别的旋钮 ═══" + Environment.NewLine);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var live = new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}" + Environment.NewLine));
        var sr = Solver.Solve(d, p, new SolverOptions { InsHiMm = 3, MaxRounds = 12 }, live);
        File.AppendAllText(dump, Environment.NewLine + $"═══ 耗时 {sw.Elapsed.TotalMinutes:0.0} 分　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g　停在：{sr.StopWhy}" + Environment.NewLine
            + (sr.Design is null ? "" : "解出的设计：" + sr.Design.Describe() + Environment.NewLine)
            + $"舌片厚随切口重定留痕：{sr.Trace.Count(s => s.Contains(BranchMarks.TongueResized, StringComparison.Ordinal))} 次" + Environment.NewLine);
        Assert.True(sr.Trace.Count > 0);
        Assert.DoesNotContain(sr.Trace, s => s.Contains("上界就是 0**（舌片按 J", StringComparison.Ordinal));
    }
}
