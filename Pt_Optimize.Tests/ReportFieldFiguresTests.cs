using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 用户 2026-09-11：「文章中需要有计算的温场与电场」「Y型法兰需有挖孔前温场/电场与挖孔后温场/电场，相互佐证，并说明」。
/// 这道慢仪器把三种构型各解一次，用 ③ 页画场图的同一函数（FieldPlots.DrawShellField）把最不利那片的
/// 温度场与电流密度场存成 PNG，落在 docs/fig/v6/ 给两份 docx 用；数字写进 deliverable/场图_报告用_2026-09-11.txt。
///   A 现役设计记录（管壁 0.8 · 留余量，3 段 4 片）：解一次（LineRunner.Run）；
///   B 用户手画的侧 Y 形（2 段 3 片，圆角三角槽宽端朝盘）交给求解器定旋钮 —— 挖孔后；
///   C 同一锥形舌片、不开槽（其余一样）交给求解器 —— 挖孔前。
/// </summary>
public class ReportFieldFiguresTests
{
    private static string FigDir => Path.Combine(HandoverDoc.Root(), "docs", "fig", "v6");

    private static void SavePlate(FlangeOut f, string tag, double tubeOuterR, StringBuilder log)
    {
        if (f.Mesh is null) { log.AppendLine($"  {tag}：没有场（Mesh 为空）"); return; }
        var fpT = FieldPlots.NewPlot();
        FieldPlots.DrawShellField(fpT, f.Mesh, f.TField, $"法兰温度场　{f.Name}", "温度", "°C", tubeOuterR);
        fpT.Plot.SavePng(Path.Combine(FigDir, $"field_{tag}_T.png"), 1400, 800);
        var fpJ = FieldPlots.NewPlot();
        FieldPlots.DrawShellField(fpJ, f.Mesh, f.JField, $"电流密度场　{f.Name}", "J", "A/mm²", tubeOuterR);
        fpJ.Plot.SavePng(Path.Combine(FigDir, $"field_{tag}_J.png"), 1400, 800);
        log.AppendLine($"  {tag}：{f.Name}　最高温 {f.TMaxC:0.0} °C　J 峰 {f.JMaxAPerMm2:0.00} A/mm²　抽热 {f.QFromTubeW:0.00} W　舌端 {f.TTabEndC:0.0} °C　铂 {f.MassG:0.0} g");
    }

    private static void SaveChecks(LineResult r, StringBuilder log)
    {
        foreach (var c in r.Checks)
            log.AppendLine($"    {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}");
    }

    private static DesignSpec YSketch(bool withSlot)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = withSlot ? "拍脑袋Y形（挖孔后）" : "锥形舌片不开槽（挖孔前）";
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 15; d.TabLengthMm = 146; d.TabTaper = true;
        for (int j = 0; j < d.FlangeCount; j++)
        {
            if (withSlot)
            {
                d.TabHoleSides[j] = 3; d.TabHoleRMm[j] = 28; d.TabHoleAspect[j] = 0.36;
                d.TabHoleXMm[j] = -50; d.TabHoleRotDeg[j] = 90;
            }
            else { d.TabHoleRMm[j] = 0; d.TabHoleAspect[j] = 1; }
            d.TabInsulMm[j] = 3.0;
        }
        return d;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 报告用场图_现役记录与Y形挖孔前后()
    {
        Directory.CreateDirectory(FigDir);
        var p = new DesignInputs();
        var log = new StringBuilder();
        log.AppendLine("═══ 报告用场图（FieldPlots.DrawShellField，与 ③ 页同一函数）═══");

        // A 现役记录：解一次
        var rec = DesignSpec.Builtin[0].Clone();
        var swA = System.Diagnostics.Stopwatch.StartNew();
        var lrA = LineRunner.Run(rec.BuildCase(p, checkRamp: true), null, default);
        log.AppendLine($"A 现役记录「{rec.Name}」解一次：{swA.Elapsed.TotalMinutes:0.0} 分　收敛 {lrA.Converged}　全判据 {lrA.AllOk}　合计 {lrA.TotalMassG:0.0} g");
        double rA = rec.WallMm + 25.0;
        foreach (var f in lrA.Flanges) SavePlate(f, "rec_" + f.Name.Replace("|", "_"), rA, log);
        SaveChecks(lrA, log);

        // B / C：Y 形挖孔后 vs 不开槽 —— 都交给求解器定旋钮（同一工况、同一轮数），才有可比性
        foreach (var (with, tag) in new[] { (true, "Y_after"), (false, "Y_before") })
        {
            var d = YSketch(with);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sr = Solver.Solve(d, p, new SolverOptions { MaxRounds = 12 }, null, default);
            log.AppendLine($"{(with ? "B 挖孔后" : "C 挖孔前")}「{d.Name}」求解器：{sw.Elapsed.TotalMinutes:0.0} 分　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g　停在：{sr.StopWhy}");
            if (sr.Design is { } dd) log.AppendLine("  解出的设计：" + dd.Describe());
            if (sr.Best is { } best)
            {
                foreach (var f in best.Flanges) SavePlate(f, tag + "_" + f.Name.Replace("|", "_"), d.WallMm + 25.0, log);
                SaveChecks(best, log);
            }
            else log.AppendLine("  没有 Best（求解器没解出来）");
        }

        string txt = Path.Combine(HandoverDoc.Root(), "deliverable", "场图_报告用_2026-09-11.txt");
        File.WriteAllText(txt, log.ToString(), new UTF8Encoding(false));
        Assert.True(Directory.GetFiles(FigDir, "field_*.png").Length >= 6, "场图没存够");
    }
}
