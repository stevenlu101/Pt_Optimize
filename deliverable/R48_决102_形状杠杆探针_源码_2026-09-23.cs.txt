using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  决 102 形状杠杆探针（2026-09-23，临时探针，只量不判；不入库为门）。
//  W08 现役设计（R48NMeshGateTests.Design("W08") = R48LW08NavDesign.Build()），法兰侧旋钮固定在现役值，
//  只换盘径（30／35／40／45）与舌半宽（37.5 = 30×1.25 配盘 40；45 = 30×1.5 配盘 45，因舌半宽须 ≤ 盘半径）。
//  舌长按搜形状规则 R48NMeshGateTests.SetRW：√(R²−w²) + 压接 + 自由段下界（现役 R30 w30 恰为 140 = 现役舌长）。
//  整线一次解：R48NMeshGateTests.SolveLine 同一写法（ApplySectionFloor → RequiredMeshFor → BuildCase → ApplyCaseMesh(FineMm 0 导航) → LineRunner.Run），不求根。
// ════════════════════════════════════════════════════════════════════════════
public class ZZD102ShapeProbeTests
{
    private readonly ITestOutputHelper _o;
    public ZZD102ShapeProbeTests(ITestOutputHelper o) { _o = o; }

    const double HotLimK = 5.0, ColdLimK = 5.0, FluxLimW = 0.0;   // HANDOVER ## 1.83：⑦ 5 K、⑧ 5 K、管孔净流入须 > 0 W
    const double CaseCapMin = 15.0;

    static string Sh(string cmd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!; string s = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return s;
        }
        catch (Exception ex) { return "未查到（" + ex.GetType().Name + "）"; }
    }
    static string Load() { try { return File.ReadAllText("/proc/loadavg").Trim(); } catch { return "未查到"; } }
    static string A(double[] a) => a is null || a.Length == 0 ? "（空＝沿用整线值）" : string.Join("/", a.Select(x => double.IsNaN(x) ? "NaN" : x.ToString("0.###")));

    [Trait("速度", "慢")]
    [Fact]
    public void 决102_W08_盘径与舌半宽_导航整线一次解()
    {
        var p = new DesignInputs();
        var d0 = R48NMeshGateTests.Design("W08");
        var cases = new List<(string tag, double R, double w)>
        {
            ("现役", 30.0, 30.0), ("盘+5", 35.0, 30.0), ("盘+10", 40.0, 30.0), ("盘+15", 45.0, 30.0),
            ("舌×1.25（盘40）", 40.0, 37.5), ("舌×1.5（盘45）", 45.0, 45.0),
        };
        string file = DeliverableOut.Stamped("R48_决102_形状杠杆探针_W08_盘径.txt");
        string root = HandoverDoc.Root();
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("决 102 形状杠杆探针　W08 现役设计　盘径／舌半宽　导航档整线一次解（只量不判）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 Linux 镜像（{Environment.OSVersion}，.NET {Environment.Version}，{Environment.ProcessorCount} 核）　Linux、待 Windows 重录");
        W($"工作树 {root}　提交 {Sh("git", $"-C {root} rev-parse --short HEAD")}（分离头，未提交的只有本探针测试档）");
        W($"同机并跑：是（另有慢门与合并在跑）；开跑时 /proc/loadavg = {Load()}　⇒ 耗时是争用下量得，只作量级");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W($"现役旋钮（全部例固定）：板厚 {A(d0.TabThickMm)}　舌保温 {A(d0.TabInsulMm)}　环倍率 {A(d0.RingMul)}　外级倍率 {A(d0.RingMul2)}　内级环宽 {A(d0.RingW1Mm)}　外级环宽 {A(d0.RingW2Mm)}　槽张角 {A(d0.SlotSpanDeg)}　舌孔 R {A(d0.TabHoleRMm)}");
        W($"边界保温条件（全部例相同）：管保温 {d0.TubeInsulMm:0.###} mm；圆盘保温 {(d0.FlangeInsulated ? d0.FlangeInsulMm.ToString("0.###") + " mm" : "不包")}，逐片 {A(d0.DiscInsulMm)}；舌保温逐片 {A(d0.TabInsulMm)} mm");
        W($"现役形状：盘半径 {d0.DiscRadiusMm} mm　舌半宽 {d0.TabHalfWidthMm} mm　舌长 {d0.TabLengthMm} mm　压接 {d0.ClampLengthMm} mm　自由段下界 {GeometryScreen.FreeTabMinDefaultMm} mm");
        W("只换：盘径 R 与舌半宽 w，舌长按搜形状规则 = √(R²−w²) + 压接 + 自由段下界（R48NMeshGateTests.SetRW；现役 R30 w30 给 140 = 现役舌长）。");
        W("舌半宽不能在盘 30 上加宽：GeometryScreen.MinDiscRadiusForTabMm 与 Solver.cs:1505 要盘半径 ≥ 舌半宽（舌片长不出圆盘）⇒ 舌 ×1.25 = 37.5 配盘 40、舌 ×1.5 = 45 配盘 45，与同盘径 w30 那例对照。");
        W("盘径上界：Core 里未查到盘径上界（只有下界：GeometryScreen.MinDiscRadiusMm 孔+焊脚、MinDiscRadiusForTabMm 舌半宽）；界面控件量程 Ø30～300（审计 W09）。");
        W("整线解写法：R48NMeshGateTests.SolveLine 同一路：Solver.ApplySectionFloor → MeshVerify.RequiredMeshFor(d) → d.BuildCase → Solver.ApplyCaseMesh(FineMm 0 = 导航) → LineRunner.Run；不求根、不动旋钮。");
        W($"判据限值（HANDOVER ## 1.83）：最热铂高出热偶读数 ≤ {HotLimK} K；管根低于热偶读数 ≤ {ColdLimK} K；管孔净流入 > {FluxLimW} W。裕度 = 限值 − 值（前两条）、值 − 0（净流入）；正 = 有余。只印不判。");
        W();
        W("标签\tR\tw\t舌长\t解出\t收敛\t单元\t细区半径\t最热铂K\t裕度K\t管根K\t裕度K\t净流入W\t裕度W\t铂重g\t法兰重g\tAllOk\t耦合轮\t耗时s\tloadavg");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var detail = new StringBuilder();
        foreach (var (tag, R, w) in cases)
        {
            var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, w);
            var sw = Stopwatch.StartNew();
            LineResult? res = null; string err = ""; double reqRadius = double.NaN; int cells = 0;
            var thickAfter = Array.Empty<double>();
            var task = Task.Run(() =>
            {
                try
                {
                    var dummy = new SolverResult { Design = d };
                    Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
                    thickAfter = d.TabThickMm.ToArray();
                    (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
                    var lc = d.BuildCase(p);
                    Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0.0, FineRadiusMm = reqRadius });
                    res = LineRunner.Run(lc, null);
                }
                catch (Exception ex) { err = $"{ex.GetType().Name}：{ex.Message}"; }
            });
            bool done = task.Wait(TimeSpan.FromMinutes(CaseCapMin));
            double sec = sw.Elapsed.TotalSeconds;
            string line;
            if (!done) line = $"{tag}\t{R:0.0}\t{w:0.0}\t{d.TabLengthMm:0.0}\t**超过 {CaseCapMin} 分钟未返回（线程未取消，后面各例受它争用）**\t\t\t\t\t\t\t\t\t\t\t\t\t\t{sec:0}\t{Load()}";
            else if (res is null) line = $"{tag}\t{R:0.0}\t{w:0.0}\t{d.TabLengthMm:0.0}\t**抛异常：{err}**\t\t\t\t\t\t\t\t\t\t\t\t\t\t{sec:0}\t{Load()}";
            else
            {
                var r = res; cells = r.MeshCells;
                double hot = r.ValueOf(LineResult.Key.HotOverTc), cold = r.ValueOf(LineResult.Key.ColdUnderTc), flux = r.ValueOf(LineResult.Key.NetFlux);
                line = $"{tag}\t{R:0.0}\t{w:0.0}\t{d.TabLengthMm:0.0}\t{(r.Ok ? "是" : "否")}\t{(r.Converged ? "是" : "否")}\t{cells}\t{reqRadius:0.0}"
                     + $"\t{hot:0.000}\t{HotLimK - hot:+0.000;-0.000}\t{cold:0.000}\t{ColdLimK - cold:+0.000;-0.000}\t{flux:0.000}\t{flux - FluxLimW:+0.000;-0.000}"
                     + $"\t{r.TotalMassG:0.0}\t{r.FlangeMassG:0.0}\t{(r.AllOk ? "是" : "否")}\t{r.CoupleRounds}\t{sec:0}\t{Load()}";
                detail.AppendLine($"── {tag}（R {R}、w {w}、舌长 {d.TabLengthMm:0.###}）");
                detail.AppendLine($"   板厚（ApplySectionFloor 之后）{A(thickAfter)}　现役 {A(d0.TabThickMm)}");
                detail.AppendLine($"   Message：{(string.IsNullOrEmpty(r.Message) ? "（空）" : r.Message)}");
                detail.AppendLine($"   没过（LineResult.Failed）：{(r.Failed.Length == 0 ? "（无）" : string.Join("；", r.Failed))}");
                detail.AppendLine($"   场判不了：{(r.FieldUndeterminedReasons.Length == 0 ? "（无）" : string.Join("；", r.FieldUndeterminedReasons))}");
                detail.AppendLine($"   最热铂位置 {r.Find(LineResult.Key.HotOverTc)?.Where}　管根位置 {r.Find(LineResult.Key.ColdUnderTc)?.Where}　净流入位置 {r.Find(LineResult.Key.NetFlux)?.Where}");
                detail.AppendLine($"   硬判据与目标全表：");
                foreach (var c in r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
                    detail.AppendLine($"     {c.Name}\t{c.Actual:0.000}\t限 {c.Limit:0.###}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Where}");
                detail.AppendLine($"   逐片：" + string.Join("　", r.Flanges.Select(f => $"{f.Name} {f.MassG:0.0} g 发热 {f.QGenW:0.00} W 抽热 {f.QFromTubeW:0.00} W")));
            }
            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            _o.WriteLine(line);
            if (!done) break;
        }
        File.AppendAllText(file, Environment.NewLine + "═══════ 逐例明细 ═══════" + Environment.NewLine + detail + Environment.NewLine
            + $"收尾 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　loadavg {Load()}" + Environment.NewLine
            + "出处：整线解 = LineRunner.Run；读数 = LineResult.ValueOf／Checks／Failed／FieldUndeterminedReasons；每个数出自本次运行。探针源码另存 R48_决102_形状杠杆探针_源码_2026-09-23.cs.txt。" + Environment.NewLine,
            new UTF8Encoding(false));
        _o.WriteLine(file);
    }
}
