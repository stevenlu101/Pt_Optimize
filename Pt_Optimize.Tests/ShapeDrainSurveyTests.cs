using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（2026-09-08 晚）：设计因果链下搜形状在网格里找不到可行形状，每点都卡在片0 的 ③（法兰增量温降 ≤ 10 K）。
/// 把 盘Ø70／舌宽70（2 段 3 片，0.8 档）这一点的数量出来：按 J=10 定完截面后，各片抽热多少瓦、③ 多少 K、
/// 舌片往铜排导多少瓦、舌端多少度 —— 让「J=10 与 ③ 对立、只剩舌长一个出口」有数可看。只解一次，不判、不改。
/// </summary>
public class ShapeDrainSurveyTests
{
    [Trait("速度", "慢")]
    [Fact]
    public void 量盘70舌70在J10截面下的抽热与三()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.DiscRadiusMm = 35; d.TabHalfWidthMm = 35;
        d.TabLengthMm = Math.Sqrt(Math.Max(0, 35.0 * 35.0 - 35.0 * 35.0)) + d.ClampLengthMm + 100;   // 切点 + 压接 + 自由段下界（与搜形状同一算法）
        var o = new SolverOptions();
        double tLo = Math.Ceiling(d.DiscFloorMm(p) / o.QuantThickMm - 1e-9) * o.QuantThickMm;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = tLo; d.TabInsulMm[j] = o.InsLoMm; d.RingMul[j] = 1; d.RingMul2[j] = 1; }
        var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, o, res, null, s => res.Trace.Add(s));
        var r = LineRunner.Run(d.BuildCase(p, checkRamp: false), null, default);

        var sb = new StringBuilder();
        sb.AppendLine("═══ 盘Ø70／舌宽70／舌长 " + d.TabLengthMm.ToString("0") + "（2 段 3 片，0.8 档）按 J=10 定完截面之后 ═══");
        sb.AppendLine(res.DesignCurrent!.Describe());
        sb.AppendLine("板厚 " + string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00"))) + " mm（舌保温 0.3、无台阶）");
        sb.AppendLine($"Ok={r.Ok} Converged={r.Converged} 合计 {r.TotalMassG:0} g");
        sb.AppendLine("片\t管根°C\t抽热D W\t舌端°C\t导到铜排 W\t表面散热 W\t自身发热 W\t截面J\t整片峰值−管根 K");
        foreach (var f in r.Flanges)
            sb.AppendLine($"{f.Name}\t{f.TRootC:0}\t{f.QFromTubeW:0.0}\t{f.TTabEndC:0}\t{f.QClampW:0.0}\t{f.QLossW:0.0}\t{f.QGenW:0.0}\t{f.SectionJAPerMm2:0.0}\t{f.TMaxC - f.TRootC:+0.0;-0.0}");
        sb.AppendLine("段\t③ A端 K\t③ B端 K");
        foreach (var s in r.Segments) sb.AppendLine($"{s.Name}\t{s.FlangeDipAK:0.0}\t{s.FlangeDipBK:0.0}");
        sb.AppendLine();
        foreach (var c in r.Checks.Where(c => c.Kind != CheckKind.Reference))
            sb.AppendLine($"  {c.Name}\t实际 {c.Actual:0.000}\t限 {c.Limit:0.###}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Where}");
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped("形状抽热_盘70舌70.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
        Assert.True(r.Ok, r.Message);
    }
}
