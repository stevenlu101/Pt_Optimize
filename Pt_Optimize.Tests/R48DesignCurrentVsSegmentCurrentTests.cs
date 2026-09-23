using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P1-15）：**设计电流是不是段电流的上界** —— 同一次整线解（同一次 Judge）里比段电流与设计电流，只测不判。
///
/// ══ 起因
///   DesignCurrent 类注释写「取管子单独所需的电流（上界、确定、闭式）」。审查【5a】跨树拿 B2 片0 空管 1076.62 A（G3 树、F 前配方）比设计电流 1072 A（保温搜索报告头、闭式）说超 0.43 %，
///   但两个数不是同一次跑出来的，可比性没核。
///
/// ══ 做法（跑前写死）
///   算例 B2（与 R48G2RampClampChannelTests B2 同一组输入，耦合容差 0.25 K）两态：带玻璃稳态；空管到温稳态（控温点全线升温目标、默认管腔系数）。各 LineRunner.Run 一次。
///   设计电流取自**同一次 Judge**：FlangeOut.DesignCurrentA 就是 Judge 里「法兰截面 J」那一行写进去的 dcr.PlateA[j]；
///   dcr.SegPeakA 没有带出，本探针用同一次算例的同一组输入调 DesignCurrent.Compute 复算一份，**先断言复算的 PlateA 与 FlangeOut.DesignCurrentA 逐位相同**（锚住「是同一次 Judge 的那份」），再用它的 SegPeakA。
///   逐段印：段电流 SegmentOut.CurrentA、SegPeakA[i]、差（A）与相对差；逐片印：法兰电流 FlangeOut.CurrentA（场解用的接头电流）、按段电流合成的接头电流 LineSolver.JointCurrentA(段电流, j)、设计电流 PlateA[j]。
///   没有门槛：只报「段电流 &gt; 设计电流」的段与超出量。输出只写 deliverable\R48_设计电流与段电流_同一次整线_本次开跑于{yyyy-MM-dd_HHmmss}.txt。
/// </summary>
[Trait("速度", "慢")]
public class R48DesignCurrentVsSegmentCurrentTests
{
    private readonly ITestOutputHelper _out;
    public R48DesignCurrentVsSegmentCurrentTests(ITestOutputHelper o) { _out = o; }
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void B2_带玻璃与空管_段电流对设计电流()
    {
        string file = Path.Combine(HandoverDoc.Root(), "deliverable", $"R48_设计电流与段电流_同一次整线_本次开跑于{DateTime.Now.ToString("yyyy-MM-dd_HHmmss", Inv)}.txt");
        if (File.Exists(file)) throw new InvalidOperationException("输出文件已存在：" + file);
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        string root = HandoverDoc.Root();
        Say($"R48 设计电流与段电流·同一次整线（Opus 5，I 路，{DateTime.Now:yyyy-MM-dd HH:mm:ss}）　出处 Pt_Optimize.Tests/R48DesignCurrentVsSegmentCurrentTests.cs　做法见类注释（跑前写死，只测不判）");
        Say($"仓库 {root}　git HEAD {EvidenceHeader.GitHead(root)}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(root)}");

        foreach (bool empty in new[] { false, true })
        {
            var p = new DesignInputs { SplitSharedFlangeDraw = true };
            var d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d.TubeInsulMm = 7.5;
            d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
            d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
            d = d.Fit(); d.SizeTongues(p);
            var lc = empty
                ? d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.RampTarget)
                : d.BuildCase(p, checkRamp: false);
            lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = 4000;
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            string state = empty ? "空管到温稳态" : "带玻璃稳态";
            Say("");
            Say($"══ {state}：Ok {r.Ok}　收敛 {r.Converged}　{sw.Elapsed.TotalSeconds:0} s　控温点 {string.Join("/", lc.SetpointC.Select(v => v.ToString("0", Inv)))} °C　升温 {lc.RampFromC:0}→{lc.RampTargetC:0} °C、{lc.RampRateKPerH:0} K/h");
            Assert.True(r.Ok && r.Converged, state + " 整线没解出：" + r.Message);

            // 同一次算例的同一组输入复算 dcr（Judge 里那一次的参数表：LineRunner.cs「法兰截面 J」段）；先锚 PlateA 与 Judge 写进 FlangeOut 的逐位相同
            var platesJ = lc.FlangePlates is { Length: > 0 } ? lc.FlangePlates : lc.GeomForJudge;
            var fl = r.Flanges;
            var dcr = DesignCurrent.Compute(platesJ, lc.WallMm, lc.Base, lc.RampFromC, lc.RampTargetC, lc.RampRateKPerH, r.Segments.Length, lc.SetpointC,
                jj => jj < fl.Length && fl[jj] is { } f0 && f0.QGenW > 0 && f0.CurrentA > 0 ? (f0.QGenW / (f0.CurrentA * f0.CurrentA), f0.TRootC) : null,
                lc.DiscInsulEffectiveAt);
            bool anchor = fl.Length == dcr.PlateA.Length && Enumerable.Range(0, fl.Length).All(j => fl[j].DesignCurrentA.Equals(dcr.PlateA[j]));
            Say($"   锚：复算 PlateA {string.Join("/", dcr.PlateA.Select(v => v.ToString("R", Inv)))} 对 Judge 写进 FlangeOut.DesignCurrentA {string.Join("/", fl.Select(f => f.DesignCurrentA.ToString("R", Inv)))} ⇒ {(anchor ? "逐位相同" : "★ 对不上（本跑作废）")}");
            Assert.True(anchor, "复算的设计电流与同一次 Judge 的不逐位相同");

            double[] segA = r.Segments.Select(s => s.CurrentA).ToArray();
            for (int i = 0; i < r.Segments.Length; i++)
            {
                double diff = segA[i] - dcr.SegPeakA[i];
                Say($"   段{i} {r.Segments[i].Name}：段电流 {segA[i].ToString("R", Inv)} A　设计电流 SegPeakA {dcr.SegPeakA[i].ToString("R", Inv)} A　差 {diff.ToString("+0.000;-0.000", Inv)} A（{(diff / dcr.SegPeakA[i] * 100).ToString("+0.000;-0.000", Inv)} %）"
                  + $"{(diff > 0 ? "　★ 段电流超过设计电流" : "")}　管 J {r.Segments[i].TubeJAPerMm2.ToString("0.000", Inv)} A/mm²");
            }
            for (int j = 0; j < fl.Length; j++)
            {
                double joint = LineSolver.JointCurrentA(segA, j);
                double dj = joint - dcr.PlateA[j];
                Say($"   片{j} {fl[j].Name}：场解接头电流 FlangeOut.CurrentA {fl[j].CurrentA.ToString("R", Inv)} A　按段电流合成 {joint.ToString("R", Inv)} A　设计电流 PlateA {dcr.PlateA[j].ToString("R", Inv)} A　"
                  + $"合成 − 设计 {dj.ToString("+0.000;-0.000", Inv)} A（{(dj / dcr.PlateA[j] * 100).ToString("+0.000;-0.000", Inv)} %）{(dj > 0 ? "　★ 超过" : "")}");
            }
            var sec = r.Checks.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.SectionJ, StringComparison.Ordinal));
            Say($"   同一次判据表「{LineResult.Key.SectionJ}」：实际 {sec?.Actual.ToString("R", Inv) ?? "（无）"}　限值 {sec?.Limit.ToString("R", Inv) ?? "（无）"}　（截面 J 用的是设计电流，不是本工况段电流）");
        }
    }
}
