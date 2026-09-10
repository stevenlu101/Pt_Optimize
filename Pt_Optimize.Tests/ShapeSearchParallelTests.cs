using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R36（2026-09-11）：「◇ 搜形状」并行化的门——⑥「剩余舌宽比例」与 ⑦「邻域探索」
/// 改走 <see cref="ShapeBatchEval"/> 之后，串行（lanes=1）与并行（lanes=4）必须给出
/// **逐位相同**的结果。仪器（慢）：拿 <see cref="ShapeSearchPlan.Neighbours(double,double,double)"/>
/// 真造出的 4 个邻点候选，各自走 <see cref="Solver.Solve"/>，比较 <c>Trace</c> 全文、
/// 判据表、铂重、<c>Design.Describe()</c>——字符串逐位相等。
///
/// 案例的盘径/舌宽照抄 <c>SketchShapeTests</c> 里最省时的慢测那一套（2 段、导航网格、
/// 不开细网格）；但那边 <c>MaxRounds = 12</c> 实测在本仪器的 8 个真实解上太贵（单个候选
/// 就跑过 1 小时未收敛），而 <b>MaxRounds 的大小不影响这道门要不要过</b>——门只要求
/// 「同一个候选、同一段代码，串行与并行给出的结果逐位相同」，不要求算到收敛；所以把
/// <c>MaxRounds</c> 降到 2，只求真实走一遍 <c>LineRunner</c> 网格与判据表，不求收敛。
/// </summary>
public class ShapeSearchParallelResultTests
{
    private static DesignSpec BaseCase()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10;
        return d;
    }

    /// <summary>与 <c>SearchShapeAsync</c> ⑦「邻域探索」同一条公式：舌长 = 切点 + 压接段 + 自由段下界。</summary>
    private static DesignSpec Candidate(DesignSpec baseD, double R, double hw)
    {
        var d = baseD.Clone();
        d.DiscRadiusMm = R;
        d.TabHalfWidthMm = hw;
        d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
        return d;
    }

    private static string ChecksTable(LineResult? best)
    {
        if (best is null) return "(Best=null)";
        return string.Join("\n", best.Checks.Select(c =>
            $"{(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}"));
    }

    [Trait("速度", "慢")]
    [Fact]
    public async Task 搜形状_串行并行逐位相同()
    {
        var baseD = BaseCase();
        var p = new DesignInputs();
        // ⑦「邻域探索」真实产出的 4 个邻点——R0/hw0 照抄 SketchShapeTests 那套最省时的慢测
        // （Ø56／舌端半宽 15），步长用 ShapeSearchPlan.DiscStepMm 同源，不是自己拍的数
        // （用户原话：「别自己拍」）。
        double R0 = 28, hw0 = 15;
        var neigh = ShapeSearchPlan.Neighbours(R0, hw0, ShapeSearchPlan.DiscStepMm);
        var specs = neigh.Select(n => Candidate(baseD, n.R, n.HalfW)).ToList();

        // 轮数：MaxRounds=12 时单候选超过 1 小时（子代理 2026-09-11 实测），降到 2 ——
        // 门钉的是「同一候选串行与并行结果逐位相同」，不钉「收敛」，轮数多小都不影响这道门成立。
        // ⚠ 不挂 Progress 回调（合入时去掉了子代理写实时轨迹的那份：四路并发往同一个文件追加，
        //   回调又落在线程池上，出了事整个测试主机一起倒）；求解轨迹全在 Trace 里，够比。
        SolverResult SolveOne(DesignSpec d, CancellationToken tok)
            => Solver.Solve(d, p, new SolverOptions { MaxRounds = 2 }, null, tok);

        var swSerial = Stopwatch.StartNew();
        var serial = await ShapeBatchEval.RunAsync(specs, SolveOne, lanes: 1, CancellationToken.None);
        swSerial.Stop();

        var swParallel = Stopwatch.StartNew();
        var parallel = await ShapeBatchEval.RunAsync(specs, SolveOne, lanes: 4, CancellationToken.None);
        swParallel.Stop();

        var report = new StringBuilder();
        report.AppendLine("═══ R36 搜形状并行化：串行(lanes=1) vs 并行(lanes=4) 逐位对拍 ═══");
        report.AppendLine($"候选（ShapeSearchPlan.Neighbours({R0:0}, {hw0:0}, {ShapeSearchPlan.DiscStepMm:0})）：" +
            string.Join("　", neigh.Select(n => $"盘Ø{2 * n.R:0}／舌宽{2 * n.HalfW:0}")));
        report.AppendLine($"串行耗时 {swSerial.Elapsed.TotalMinutes:0.00} 分　并行(4 路)耗时 {swParallel.Elapsed.TotalMinutes:0.00} 分");
        report.AppendLine();

        Assert.True(serial.Done.All(x => x), "串行批次里有候选没跑完——不该发生（lanes=1、ct=None）");
        Assert.True(parallel.Done.All(x => x), "并行批次里有候选没跑完——不该发生（lanes=4、ct=None）");

        for (int i = 0; i < specs.Count; i++)
        {
            var sr = serial.Results[i]; var pr = parallel.Results[i];
            string traceS = string.Join("\n", sr.Trace), traceP = string.Join("\n", pr.Trace);
            string checksS = ChecksTable(sr.Best), checksP = ChecksTable(pr.Best);
            string descS = sr.Design.Describe(), descP = pr.Design.Describe();

            report.AppendLine($"── 候选 {i}：盘Ø{2 * neigh[i].R:0}／舌宽{2 * neigh[i].HalfW:0} ──");
            report.AppendLine($"  可行 串{sr.Feasible}／并{pr.Feasible}　合计 串{sr.MassG:0.0}／并{pr.MassG:0.0} g" +
                $"　Trace 逐位相同 {traceS == traceP}　判据表逐位相同 {checksS == checksP}　Describe 逐位相同 {descS == descP}");

            Assert.Equal(traceS, traceP);
            Assert.Equal(checksS, checksP);
            Assert.Equal(sr.MassG, pr.MassG);
            Assert.Equal(sr.Feasible, pr.Feasible);
            Assert.Equal(sr.Message, pr.Message);
            Assert.Equal(descS, descP);
        }

        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "搜形状并行化_逐位对拍_2026-09-11.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, report.ToString());
    }
}

/// <summary>
/// ★ R36：源码钉子（照 <see cref="SolverWiredToUiTests"/> 的写法）——⑥⑦两段调的是
/// <see cref="ShapeBatchEval"/>.RunAsync，且 ⑦ 那段不再有逐邻点 <c>await EvalShape(</c> 的循环。
/// ③④⑤（不动点迭代／二分／黄金分割）与 ⑧（精算）仍然是单点 <c>await EvalShape(</c>——
/// 这道门只钉「⑥⑦换了派发方式」，不钉「别的地方也不许再出现 EvalShape」（那些本来就该留着）。
/// </summary>
public class ShapeSearchParallelWiringTests
{
    private static string Ui() =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    [Fact]
    public void 搜形状的批处理函数交给ShapeBatchEval()
    {
        string s = Ui();
        Assert.Contains("async Task EvalShapesBatch(IReadOnlyList<(double R, double hw, bool Taper)> pts)", s);   // R38：候选多带一维锥形
        Assert.Contains("outcome = await ShapeBatchEval.RunAsync(specs, (spec, tok) =>", s);
    }

    [Fact]
    public void 剩余舌宽比例与邻域探索都改调批量并行()
    {
        string s = Ui();
        // ⑥ 剩余舌宽比例
        Assert.Contains("await EvalShapesBatch(batch6);", s);
        // ⑦ 邻域探索
        Assert.Contains("await EvalShapesBatch(todo);", s);
        // 旧的「逐邻点 await EvalShape(」循环必须不在了
        Assert.DoesNotContain("foreach (var (R2, hw2) in todo) await EvalShape(R2, hw2);", s);
    }
}
