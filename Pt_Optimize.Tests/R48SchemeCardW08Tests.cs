using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 2026-09-25：首款方案卡的三关补判（只印不判；Linux 预跑、待 Windows 重录）。
/// 搜形状驱动 W08（deliverable/R48_搜形状_Core驱动_W08_本次开跑于2026-09-24_224012.txt）的赢家只做了带玻璃稳态精算（Solver），
/// 升温期 ① 与空管到温稳态 ③ 没判、决 103 四条硬判据的实际值与裕度没印（该跑程序集的三列是旧参考项）。
/// 本探针按方案卡印出的几何与旋钮终值复原设计（下面逐项照抄，出处 = 方案卡「几何」「旋钮终值」两行），
/// 在判决网格（MeshVerify.RequiredMeshFor 那一对；细区盖不住热点就按 MeshAdapt.GrowFineRadius 放大重跑，最多两次）上跑三关：
/// ① RampSweep（决 103：J 11、热稳定、夹持导度几何回退）、② 带玻璃稳态 LineRunner.Run、③ 空管到温稳态 LineRunner.Run（emptyTube）。
/// 不覆盖：驱动精算时旋钮以外的隐含状态（场定孔位等）按默认规则重定，不是逐位复原；Windows。
/// </summary>
[Trait("速度", "慢")]
public class R48SchemeCardW08Tests
{
    private readonly ITestOutputHelper _o;
    public R48SchemeCardW08Tests(ITestOutputHelper o) { _o = o; }

    /// <summary>赢家：盘Ø54.12／舌宽 54.12／锥形／舌长 140／不挖舌孔；板厚 0.60 ×4；舌保温 4.5/2.5/3.5/9.0；环倍率与外级倍率全 1。</summary>
    internal static DesignSpec Winner()
    {
        var d = R48NMeshGateTests.Design("W08");
        R48NMeshGateTests.SetRW(d, 27.06, 27.06);
        d.TabTaper = true;
        d.TabThickMm = new[] { 0.60, 0.60, 0.60, 0.60 };
        d.TabInsulMm = new[] { 4.5, 2.5, 3.5, 9.0 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.RingMul2 = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.TabHoleRMm = new[] { 0.0, 0.0, 0.0, 0.0 };
        // 2026-09-25 12:5x 第一跑（…_122412）漏了这一项 ⇒ 舌片厚退回板厚 0.60，铂重 4024 g、截面 J 11.07 ⇒ 那一跑不是赢家（证据档留着作教训）。
        // 舌片厚是求解器按 I/(J·舌宽) 闭式算出后写进 DesignSpec.TongueThickMm 的（Solver.SizeTongue），复原设计必须照方案卡填回。
        d.TongueThickMm = new[] { 2.25, 3.89, 3.89, 2.25 };
        // ★ 决 104（业主 2026-09-25）：圆盘保温块最大厚度 10 mm。2026-09-24_224012 那张方案卡是在设计记录默认 20 mm 下解的 ⇒ 作废；
        //   本门改在 10 mm 下重判同一几何，过不过照实记（不预设）。
        d.FlangeInsulated = true; d.FlangeInsulMm = Math.Min(d.FlangeInsulMm, new DesignInputs().DiscInsulCapMm); d.DiscInsulMm = Array.Empty<double>();
        d.Name = "W08 搜形状赢家 盘Ø54.12 舌宽54.12 锥形（2026-09-24_224012 方案卡复原；圆盘保温改 10 mm，决 104）";
        return d;
    }

    private static string Row(ConstraintOut c)
    {
        double slack = double.IsNaN(c.Actual) ? double.NaN : (c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit);
        return $"　{Criteria.Plain(c.Name)}\t{c.Actual:0.###}\t{(c.LessIsBetter ? "≤" : "≥")} {c.Limit:0.###} {c.Unit}\t裕度 {slack:+0.###;-0.###}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Where}";
    }

    [Fact]
    public void 方案卡_W08赢家_判决网格三关_只印()
    {
        var d = Winner();
        var p = new DesignInputs();
        string path = DeliverableOut.Stamped("R48_方案卡_W08赢家_判决网格三关.txt");
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); }
        W("# 首款方案卡 W08 赢家　判决网格三关补判（Linux 预跑、待 Windows 重录；只印不判）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　树 {HandoverDoc.Root()}　工艺参数 new DesignInputs()（决 103 口径，FlangeStabGeomClampFallback 开）");
        W("设计复原自 deliverable/R48_搜形状_Core驱动_W08_本次开跑于2026-09-24_224012.txt 方案卡：盘Ø54.12／舌半宽 27.06／锥形／舌长 140／不挖舌孔；板厚 0.60/0.60/0.60/0.60；舌保温 4.5/2.5/3.5/9.0；环倍率 1、外级倍率 1（NaN = 默认）");
        W($"复原后：盘半径 {d.DiscRadiusMm:0.###}　舌半宽 {d.TabHalfWidthMm:0.###}　舌长 {d.TabLengthMm:0.###}　锥形 {d.TabTaper}　板厚 {string.Join("/", d.TabThickMm.Select(x => x.ToString("0.00")))}　舌保温 {string.Join("/", d.TabInsulMm.Select(x => x.ToString("0.0")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(x => x.ToString("0.00")))}");
        var (fine, radius) = MeshVerify.RequiredMeshFor(d, p);
        var innerR = d.HoleRadiusMm;
        W($"判决网格：细区 {fine:0.000} mm／细区半径初值 {radius:0.000} mm（MeshVerify.RequiredMeshFor）");
        var progress = new SyncProgress<string>(s => _o.WriteLine(s));
        var total = Stopwatch.StartNew();

        LineResult glass = null!; double radiusUsed = radius;
        for (int grow = 0; grow < 3; grow++)
        {
            var mesh = new SolverOptions { FineMm = fine, FineRadiusMm = radiusUsed };
            var lc = d.BuildCase(p); Solver.ApplyCaseMesh(lc, mesh);
            var sw = Stopwatch.StartNew();
            glass = LineRunner.Run(lc, progress);
            W($"── ② 带玻璃稳态　半径 {radiusUsed:0.###} mm　耗时 {sw.Elapsed.TotalSeconds:0} s　解出 {glass.Ok}　收敛 {glass.Converged}　AllOk {glass.AllOk}　铂重 {glass.TotalMassG:0.0} g（管 {glass.TubeMassG:0.0} + 法兰 {glass.FlangeMassG:0.0}）");
            string? peak = MeshVerify.HotspotVerdict(glass, innerR, radiusUsed);
            double peakR = MeshVerify.HotspotRadiusMm(glass);
            W($"　热点 r = {peakR:0.##} mm：{(peak is null ? "细区盖住" : peak)}");
            if (peak is null || double.IsNaN(peakR)) break;
            double next = Math.Max(peakR + MeshAdapt.PeakMarginMm + MeshAdapt.GrowOvershootCoarseCells * lc.MeshCoarseMm, radiusUsed + lc.MeshCoarseMm);
            W($"　⇒ 放大到 {next:0.###} mm 重跑");
            radiusUsed = next;
        }
        W("判据（带玻璃稳态；硬判据与目标项）：");
        foreach (var c in glass.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W(Row(c));
        W("参考项：");
        foreach (var c in glass.Checks.Where(c => c.Kind == CheckKind.Reference)) W(Row(c));
        W();

        var meshFinal = new SolverOptions { FineMm = fine, FineRadiusMm = radiusUsed };
        var lcE = d.BuildCase(p, emptyTube: true); Solver.ApplyCaseMesh(lcE, meshFinal);
        var swE = Stopwatch.StartNew();
        var empty = LineRunner.Run(lcE, progress);
        W($"── ③ 空管到温稳态　半径 {radiusUsed:0.###} mm　耗时 {swE.Elapsed.TotalSeconds:0} s　解出 {empty.Ok}　收敛 {empty.Converged}　AllOk {empty.AllOk}");
        foreach (var c in empty.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W(Row(c));
        W();

        var swR = Stopwatch.StartNew();
        var ramp = RampSweep.Run(d.Clone(), p, new RampSweepOptions { RunClampAlt = false, Mesh = meshFinal }, progress);
        W($"── ① 升温全程　耗时 {swR.Elapsed.TotalSeconds:0} s　结论：{ramp.Verdict}");
        if (ramp.VerdictDetail.Length > 0) W(ramp.VerdictDetail);
        foreach (var pt in ramp.Points)
            W($"　{pt.SetpointC:0} °C（夹头 {pt.ClampC:0}）　场有效 {pt.FieldValid}　管J(设计) {pt.TubeJDesignAPerMm2:0.###}/{pt.TubeJDesignLimit:0.#}　整片热稳定 {pt.FlangeStabMargin:0.###}　局部热稳定 {pt.LocalStabMargin:0.###}　热稳定判不了 {pt.StabUndetermined}");
        W();
        W($"══ 三关结论：① {ramp.Verdict}　② {(glass.AllOk ? "过" : "不过／判不了")}　③ {(empty.Ok && empty.Converged && empty.AllOk ? "过" : "不过／判不了")}　铂重 {glass.TotalMassG:0.0} g　总耗时 {total.Elapsed.TotalMinutes:0.0} min");
        W($"结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(path, sb.ToString());
        _o.WriteLine(sb.ToString());
        Assert.True(glass.Ok, glass.Message);
    }
}
