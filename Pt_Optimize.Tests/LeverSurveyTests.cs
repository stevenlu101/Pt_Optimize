using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（2026-09-08 晚，用户拍板「先做 1+3，做完再搜形状」之前先量）：
/// 在 盘Ø70／舌宽70（2 段 3 片，0.8 档）、按 J=10 定完截面的构型上，两根杠杆各能把 ③ 压到哪：
///   杠杆 1 管保温加厚 ⇒ 设计电流下降 ⇒ 截面/板厚/铜排导热同比缩（闭式，毫秒）
///   杠杆 3 法兰全包保温（圆盘保温 × 舌保温）⇒ 表面散热下降 ⇒ 抽热 D 与 ③ 下降（每格一次场解）
/// 只量不判不改，落档 deliverable/杠杆扫描_盘70舌70.txt。
/// </summary>
public class LeverSurveyTests
{
    private static DesignSpec Shape(double tubeInsMm, double flangeInsMm, double tabInsMm, DesignInputs p, SolverResult res, double jDesign = 10)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.DiscRadiusMm = 35; d.TabHalfWidthMm = 35;
        d.TabLengthMm = 0 + d.ClampLengthMm + 100;       // 切点 0（舌宽 = 盘径）+ 压接 + 自由段下界
        d.TubeInsulMm = tubeInsMm; d.FlangeInsulated = true; d.FlangeInsulMm = flangeInsMm;
        var o = new SolverOptions();
        double tLo = Math.Ceiling(d.DiscFloorMm(p) / o.QuantThickMm - 1e-9) * o.QuantThickMm;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = tLo; d.TabInsulMm[j] = tabInsMm; d.RingMul[j] = 1; d.RingMul2[j] = 1; }
        Solver.ApplySectionFloor(d, p, o, res, null, null, jDesign);
        return d;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 量管保温与法兰全包对三的杠杆()
    {
        var p = new DesignInputs();
        var sb = new StringBuilder();
        sb.AppendLine("═══ 盘Ø70／舌宽70／舌长 140（2 段 3 片，0.8 档），按 J=10 定完截面 ═══");
        sb.AppendLine();
        sb.AppendLine("── 杠杆 1：管保温 ⇒ 设计电流与 J=10 板厚（闭式）");
        sb.AppendLine("管保温 mm\t段电流 A\t共用片 A\t板厚 mm（片0/共用/片2）");
        foreach (double ti in new[] { 5.0, 10, 20, 40, 80 })
        {
            var res = new SolverResult();
            var d = Shape(ti, 20, 0.3, p, res);
            sb.AppendLine($"{ti:0}\t{res.DesignCurrent!.SegPeakA[0]:0}\t{res.DesignCurrent.PlateA[1]:0}\t{string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))}");
        }
        sb.AppendLine();
        sb.AppendLine("── 杠杆 3（与 1 组合）：每格一次场解");
        sb.AppendLine("管保温\t圆盘保温\t舌保温\t合计 g\t片\t管根°C\t抽热D W\t表面散热 W(盘/舌)\t导到铜排 W\t自身发热 W\t③max K\t②′ W\t②″ K\t整片热稳定×\t判定摘要");
        foreach (var (ti, fi, tabi) in new[] { (5.0, 20.0, 0.3), (5.0, 20.0, 20.0), (5.0, 40.0, 40.0), (5.0, 80.0, 80.0), (40.0, 20.0, 20.0), (40.0, 80.0, 80.0) })
        {
            var res = new SolverResult();
            var d = Shape(ti, fi, tabi, p, res);
            var r = LineRunner.Run(d.BuildCase(p, checkRamp: false), null, default);
            double dip = r.Segments.Max(s => Math.Max(double.IsNaN(s.FlangeDipAK) ? -1 : s.FlangeDipAK, double.IsNaN(s.FlangeDipBK) ? -1 : s.FlangeDipBK));
            var c2p = r.Checks.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.NetFlux, StringComparison.Ordinal));
            var c2pp = r.Checks.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.DiscTemp, StringComparison.Ordinal));
            var stab = r.Checks.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
            string verdict = !r.Ok ? "✗ " + r.Message : !r.Converged ? "未收敛" :
                string.Join("；", r.Checks.Where(c => c.Kind != CheckKind.Reference && !(c.Ok && !c.Undetermined)).Select(c => c.Name + (c.Undetermined ? "判不了" : "不过")));
            bool first = true;
            foreach (var f in r.Flanges)
            {
                sb.AppendLine((first ? $"{ti:0}\t{fi:0}\t{tabi:0.#}\t{r.TotalMassG:0}" : "\t\t\t")
                    + $"\t{f.Name}\t{f.TRootC:0}\t{f.QFromTubeW:0.0}\t{f.QLossDiscW:0}/{f.QLossTabW:0}\t{f.QClampW:0.0}\t{f.QGenW:0}"
                    + (first ? $"\t{dip:0.0}\t{c2p?.Actual:0.00}\t{c2pp?.Actual:0.00}\t{stab?.Actual:0.0}\t{verdict}" : ""));
                first = false;
            }
        }
        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "杠杆扫描_盘70舌70.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
        Assert.True(sb.Length > 0);
    }
}
