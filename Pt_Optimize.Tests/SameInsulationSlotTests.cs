using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 报告 v6.1 审查（2026-09-12）：「槽切断导热路径／去掉散热面 ⇒ 同保温下抽热变少」在 B（挖孔后）与 C（挖孔前）
/// 两案例里分不出来 —— 两案例的舌保温不同（5.7/3.6/11.7 对 9.3/6.3/18.2），抽热是保温与开槽两者的合成。
/// 这道慢仪器把两者拆开：同一锥形舌片、**同一舌保温**（钉在 C 解出的 9.3/6.3/18.2），
///   C′ 不开槽解一次；B′ 开槽 + 叉臂（槽与叉臂由求解器按场定，与 ReportFieldFiguresTests 同一调用）解一次；
/// 逐片印抽热 W、法兰增量温降、最高温，差值就是「开槽 + 按 J 加厚叉臂」在同保温下对抽热的净效应。
/// 结果落 deliverable/同保温对照_开槽前后_2026-09-12.txt，给理论模型 10.6 节引用。
/// </summary>
public class SameInsulationSlotTests
{
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

    private static void Dump(LineResult r, string tag, StringBuilder log)
    {
        log.AppendLine($"{tag}：收敛 {r.Converged}　全判据 {r.AllOk}　合计 {r.TotalMassG:0.0} g");
        foreach (var f in r.Flanges)
            log.AppendLine($"  {f.Name,-8} 抽热 {f.QFromTubeW:0.000} W　最高温 {f.TMaxC:0.0} °C　J 峰 {f.JMaxAPerMm2:0.00} A/mm²　铂 {f.MassG:0.0} g");
        foreach (var c in r.Checks.Where(c => c.Name.Contains("法兰增量温降") || c.Name.Contains("管孔净流入") || c.Name.Contains("圆盘区最高温")))
            log.AppendLine($"    {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}");
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 同保温对照_开槽前后的抽热()
    {
        var p = new DesignInputs();
        var log = new StringBuilder();
        double[] insul = { 9.3, 6.3, 18.2 };           // C 案例（挖孔前）求解器落点，deliverable/场图_报告用_2026-09-11.txt
        double[] plate = { 0.60, 1.02, 0.60 };          // 两案例板厚都停在下角
        double[] tongue = { 2.64, 4.57, 2.64 };         // 舌片厚闭式 I/(J·w_min)，两案例相同；解一次要钉住，否则 NaN = 「等于板厚」会把舌片算成 0.60 mm
        log.AppendLine("═══ 同保温对照：同一锥形舌片（Ø56／舌 146×30 锥形，2 段 3 片，管保温 10），舌保温钉在 9.3/6.3/18.2 mm ═══");

        // C′：不开槽，解一次（就是 C 案例解出的设计原样再算一遍，数应与场图记录一致）
        var c = YSketch(false);
        for (int j = 0; j < c.FlangeCount; j++) { c.TabInsulMm[j] = insul[j]; c.TabThickMm[j] = plate[j]; c.TongueThickMm[j] = tongue[j]; }
        var lrC = LineRunner.Run(c.BuildCase(p, checkRamp: true), null, default);
        Dump(lrC, "C′ 不开槽（同保温）", log);
        // 门：C′ 必须复现 C 案例（场图记录 2755.1 g、抽热 3.60/3.21/3.86 W），否则这道对照没有意义
        Assert.InRange(lrC.TotalMassG, 2754.0, 2756.5);
        Assert.InRange(lrC.Flanges[1].QFromTubeW, 3.1, 3.4);

        // B′：先让求解器把槽与叉臂按场定出来（与场图仪器同一调用），再把舌保温钉回 C 的值解一次
        var b = YSketch(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sr = Solver.Solve(b, p, new SolverOptions { MaxRounds = 12 }, null, default);
        log.AppendLine($"B 求解器：{sw.Elapsed.TotalMinutes:0.0} 分　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g");
        Assert.True(sr.Design is not null, "求解器没解出 B");
        var bs = sr.Design!.Clone();
        log.AppendLine("  B 解出的设计：" + bs.Describe());
        for (int j = 0; j < bs.FlangeCount; j++) bs.TabInsulMm[j] = insul[j];
        var lrB = LineRunner.Run(bs.BuildCase(p, checkRamp: true), null, default);
        Dump(lrB, "B′ 开槽＋叉臂（舌保温钉回 9.3/6.3/18.2）", log);

        log.AppendLine("═══ 同保温下开槽＋叉臂对抽热的净效应（B′ − C′）═══");
        for (int j = 0; j < lrB.Flanges.Length && j < lrC.Flanges.Length; j++)
            log.AppendLine($"  {lrB.Flanges[j].Name,-8} Δ抽热 {lrB.Flanges[j].QFromTubeW - lrC.Flanges[j].QFromTubeW:+0.000;-0.000} W　Δ最高温 {lrB.Flanges[j].TMaxC - lrC.Flanges[j].TMaxC:+0.0;-0.0} K　Δ铂 {lrB.Flanges[j].MassG - lrC.Flanges[j].MassG:+0.0;-0.0} g");

        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string txt = DeliverableOut.Stamped("同保温对照_开槽前后_2026-09-12.txt");
        File.WriteAllText(txt, log.ToString(), new UTF8Encoding(false));
        Assert.True(lrC.Converged && lrB.Converged, "有一边没收敛");
    }
}
