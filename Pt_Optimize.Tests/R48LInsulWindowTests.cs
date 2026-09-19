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
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　**舌保温的可行窗口有多宽** —— 2026-09-17，Opus 5。
//
//  ══ 为什么要单跑这一组
//
//  用户 2026-09-17 要的是「对**解出的设计**逐片扫舌保温，量可行窗口，窗口比制造精度窄的点名」。
//  而本轮把图纸格改成 0.5（= 包法每层）之后，W08 与 W06 **都没有解出可行设计**
//  （R48LQuantInsul05EndToEndTests 那两份输出：结构性停机）——
//  绕着一份不可行的设计扫，量到的不是「窗口」，只是「到处都不过」。
//
//  ⇒ 这一组把窗口量在**已知可行的那两份设计**上（0.1 格那一跑交出来的）：
//    · W08：导航档解出来的那一份（4245 g），上一跑在**细网格**上重判三关**全过**（那一跑的 D 段）；
//    · W06：细网格档解出来的那一份（3379 g），求根与判决同一张网格、三关全过。
//    两份都按输出表复原（R48LW08NavDesign / R48LW06FineDesign），复原件先跑一次 ② 与靶子对照，
//    对不上就照实印、照实继续（不许拿「差不多」当同一份设计）。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//    · 窗口宽 &lt; 每层 0.5 mm ⇒ **点名**：判据窗口窄于制造精度。判据不许因此放宽。
//    · 窗口里**一个 0.5 的倍数都没有** ⇒ 再点一次：这一片在 0.5 的格子上**没有可行的层数**，
//      不是求解器没找到 —— 是那张格子上不存在。
//    · 扫到 ±1.0 mm 边界仍全过 ⇒ 印「边界没探到，真实窗口更宽」，不许把 2.0 当成窗口宽。
//    · 复原件与靶子对不上、或解值那一点自己就不过 ⇒ 照实印，不补、不挪。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LInsulWindowTests
{
    private readonly ITestOutputHelper _o;
    public R48LInsulWindowTests(ITestOutputHelper o) { _o = o; }

    private const double ScanStepMm = 0.1, ScanHalfMm = 1.0;
    private static readonly TimeSpan ScanCap = TimeSpan.FromHours(2.0);
    private static readonly int ScanDop = Math.Max(1, Math.Min(5, Environment.ProcessorCount - 2));

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 舌保温可行窗口(string which)
    {
        var d0 = which == "W08" ? R48LW08NavDesign.Build()
               : which == "W06" ? R48LW06FineDesign.Build()
               : throw new ArgumentException(which);
        string source = which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source;
        double tHot = which == "W08" ? 4.556 : R48LW06FineDesign.FineHotOverTcK;      // W08 的靶子取那一跑 **D 段**（细网格上重判）
        double tCold = which == "W08" ? 3.826 : R48LW06FineDesign.FineColdUnderTcK;
        double tFlux = which == "W08" ? 0.343 : R48LW06FineDesign.FineNetFluxW;
        double tMass = which == "W08" ? 4245 : R48LW06FineDesign.FineTotalMassG;
        string tWhere = which == "W08"
            ? "同一份文件 §「D 段：A 段解出来的那份设计，三关改判在细网格上」的 ②"
            : "同一份文件 §「B 段 ② 带玻璃稳态」";

        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_舌保温可行窗口_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var o0 = new SolverOptions();
        double layer = InsulationSearch.LayerMm;
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d0);
        var meshOpt = new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius };

        W($"R48 L 路　**舌保温的可行窗口有多宽**　{which}（{d0.Name}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W($"网格 = 判决那张（细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）。");
        W($"包法每层 {layer:0.###} mm（InsulationSearch.LayerMm）；求解器的舌保温图纸格本轮已改成同一个数。");
        W("");
        W("── 这一份设计是**照表复原**的，不是求解器返回的对象");
        W($"出处：{source}");
        W("⚠ §0.-8 已实测：复原件与求解器那份在进几何的字段上差 1 ulp（舌保温、舌片厚各一处），");
        W("　而「管根低于热偶读数」对那一个 ulp 敏感（同一网格摆 0.37–0.50 K）。靠近这条边界的窗口端点带着这一档噪声。");
        W("");
        W("── 判读（**跑前写死，跑完不挪**）");
        W($"　① 窗口宽 < 每层 {layer:0.###} mm ⇒ **点名**：判据窗口窄于制造精度。判据不许因此放宽。");
        W($"　② 窗口里一个 {layer:0.###} 的倍数都没有 ⇒ 再点一次：这一片在这张格子上**没有可行的层数**。");
        W($"　③ 扫到 ±{ScanHalfMm:0.0} mm 边界仍全过 ⇒ 印「边界没探到」，不许把 {2 * ScanHalfMm:0.0} 当窗口宽。");
        W("　④ 复原件与靶子对不上、或解值那一点自己就不过 ⇒ 照实印，不补、不挪。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_窗口_{which}_{stamp}_进行中.log");
        var probe = new Probe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        // ── 复原自证：先在判决网格上跑一次 ②，与靶子并列 ──
        W("═══════ 复原自证：② 带玻璃稳态（判决网格）vs 上一跑印出来的靶子 ═══════");
        W($"靶子出处：{tWhere}");
        probe.Report("── 复原自证 ②");
        var swx = Stopwatch.StartNew();
        var lc0 = d0.BuildCase(p);
        Solver.ApplyCaseMesh(lc0, meshOpt);
        var r0 = SafeRun(lc0, probe);
        swx.Stop();
        W($"耗时 {swx.Elapsed.TotalSeconds:0} s，网格单元 {r0.MeshCells}，耦合收敛 {r0.Converged}，剩余误差估计 {r0.CoupleRemainK:0.000} K。");
        W("判据\t本次（复原件）\t上一跑（靶子）\t差");
        W($"最热铂高出热偶读数\t{Fmt(Val(r0, LineResult.Key.HotOverTc), "0.###")}\t{tHot:0.###}\t{Val(r0, LineResult.Key.HotOverTc) - tHot:+0.###;-0.###}");
        W($"管根低于热偶读数\t{Fmt(Val(r0, LineResult.Key.ColdUnderTc), "0.###")}\t{tCold:0.###}\t{Val(r0, LineResult.Key.ColdUnderTc) - tCold:+0.###;-0.###}");
        W($"管孔净流入\t{Fmt(Val(r0, LineResult.Key.NetFlux), "0.###")}\t{tFlux:0.###}\t{Val(r0, LineResult.Key.NetFlux) - tFlux:+0.###;-0.###}");
        W($"铂重 g\t{r0.TotalMassG:0}\t{tMass:0}\t{r0.TotalMassG - tMass:+0;-0}");
        W($"整体：{Verdict(r0)}");
        W("");
        W("  交付判据逐条：");
        foreach (var c in r0.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(c));
        W("");
        Flush();

        // ③ 空管只卡 J —— 扫描只跑 ② 的理由，当场验一次（不是假设）
        W("═══════ ③ 空管到温稳态（只在解值这一点跑一次）═══════");
        W("扫描只跑 ② 的理由：空管这一态卡交付的是 J 那几条，而舌保温动的是热平衡。这里当场跑一次留底，不靠推断。");
        var lcE = d0.BuildCase(p, emptyTube: true);
        Solver.ApplyCaseMesh(lcE, meshOpt);
        var rE = SafeRun(lcE, probe);
        W($"结论：{Verdict(rE)}");
        foreach (var c in rE.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(c));
        W("");
        Flush();

        // ── 逐片扫 ──
        W("═══════ 逐片扫舌保温（其余片与其余旋钮一位不动）═══════");
        W($"范围 解值 ±{ScanHalfMm:0.0} mm，步长 {ScanStepMm:0.0} mm；并发 {ScanDop} 条整线解；"
          + "可行 = 整线解出来了 ∧ 外层耦合收敛 ∧ 卡交付的判据全过（LineResult.AllOk，与交付判定同一份）。");
        W("");
        Flush();

        var narrow = new List<string>();
        var noLayer = new List<string>();
        var summary = new List<string>();
        using var cts = new CancellationTokenSource(ScanCap);
        bool cut = false, parallelChecked = false, parallelSame = false;

        for (int j = 0; j < d0.TabInsulMm.Length; j++)
        {
            double v0 = d0.TabInsulMm[j];
            int half = (int)Math.Round(ScanHalfMm / ScanStepMm);
            var pts = Enumerable.Range(-half, 2 * half + 1).Select(k => Math.Round(v0 + k * ScanStepMm, 6)).ToList();
            var ok = new bool?[pts.Count];
            var why = new string[pts.Count];
            var hot = new double[pts.Count];
            var cold = new double[pts.Count];
            var flux = new double[pts.Count];
            LineResult? atSolved = null;
            for (int i = 0; i < pts.Count; i++) { hot[i] = cold[i] = flux[i] = double.NaN; why[i] = ""; }

            var sw = Stopwatch.StartNew();
            probe.Report($"── 片{j}：{pts.Count} 点（{pts[0]:0.0}…{pts[^1]:0.0} mm），并发 {ScanDop}");
            try
            {
                Parallel.For(0, pts.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = ScanDop, CancellationToken = cts.Token }, i =>
                {
                    double v = pts[i];
                    if (v < o0.InsLoMm - 1e-9) { ok[i] = null; why[i] = "下界外（低于裸舌 0.30）"; return; }
                    var d = d0.Clone();
                    d.TabInsulMm[j] = v;
                    var lc = d.BuildCase(p);
                    Solver.ApplyCaseMesh(lc, meshOpt);
                    LineResult r;
                    try { r = LineRunner.Run(lc, null); }
                    catch (Exception ex) { ok[i] = null; why[i] = $"{ex.GetType().Name}：{ex.Message}"; return; }
                    hot[i] = Val(r, LineResult.Key.HotOverTc);
                    cold[i] = Val(r, LineResult.Key.ColdUnderTc);
                    flux[i] = Val(r, LineResult.Key.NetFlux);
                    if (!r.Ok) { ok[i] = null; why[i] = "整线解没解出来：" + r.Message; return; }
                    if (!r.Converged) { ok[i] = null; why[i] = $"外层耦合未收敛（剩余 {r.CoupleRemainK:0.000} K）"; return; }
                    ok[i] = r.AllOk;
                    if (!r.AllOk) why[i] = string.Join("／", r.Failed.Select(Criteria.Plain));
                    if (Math.Abs(v - v0) < 1e-12) atSolved = r;
                });
            }
            catch (OperationCanceledException)
            {
                cut = true;
                W($"★ 片{j} 扫描**被跑前写死的时间闸切断** —— 这一片的窗口不完整，不许当结果读。");
            }
            sw.Stop();
            probe.Report($"── 片{j} 结束，耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟");

            if (!parallelChecked && atSolved is not null)
            {
                parallelChecked = true;
                parallelSame = Val(atSolved, LineResult.Key.HotOverTc).Equals(Val(r0, LineResult.Key.HotOverTc))
                            && Val(atSolved, LineResult.Key.ColdUnderTc).Equals(Val(r0, LineResult.Key.ColdUnderTc))
                            && Val(atSolved, LineResult.Key.NetFlux).Equals(Val(r0, LineResult.Key.NetFlux));
                W($"并发自检（片{j} 的解值点 vs 上面那次 ②，同一设计同一网格）："
                  + (parallelSame ? "**逐位相同** ⇒ 并发没有串味，下面的扫描可读。" : "**对不上** ⇒ 并发下的数不可引用！"));
                W("");
            }

            int c = pts.FindIndex(v => Math.Abs(v - v0) < 1e-12);
            string head;
            if (c < 0 || ok[c] != true)
            {
                head = $"片{j}　解值 {v0:0.0} mm　**解值那一点自己就不过**（{(c >= 0 ? why[c] : "没扫到")}）⇒ 以它为中心的窗口无从谈起";
                // 仍然把扫到的**可行段**列出来（有比没有强：那是这一片在这条线上唯一的可行去处）
                var segs = FeasibleSegments(pts, ok);
                head += segs.Count == 0 ? "；扫描范围内**一个可行点都没有**"
                      : "；扫描范围内的可行段：" + string.Join("、", segs.Select(s => $"[{s.Lo:0.0}, {s.Hi:0.0}] 宽 {s.Hi - s.Lo:0.0}"));
            }
            else
            {
                int a = c, b = c;
                while (a - 1 >= 0 && ok[a - 1] == true) a--;
                while (b + 1 < pts.Count && ok[b + 1] == true) b++;
                double lo = pts[a], hi = pts[b], width = hi - lo;
                bool openLo = a == 0, openHi = b == pts.Count - 1;
                var layers = new List<double>();
                for (double t = Math.Ceiling(lo / layer - 1e-9) * layer; t <= hi + 1e-9; t += layer)
                    if (t >= o0.InsLoMm - 1e-9) layers.Add(Math.Round(t, 6));
                head = $"片{j}　解值 {v0:0.0} mm　可行窗口 [{lo:0.0}, {hi:0.0}] mm　宽 {width:0.0} mm"
                     + (openLo || openHi ? $"（{(openLo ? "下" : "")}{(openLo && openHi ? "、" : "")}{(openHi ? "上" : "")}边界**没探到**，真实窗口更宽）" : "")
                     + $"　窗口里的 {layer:0.###} 档：{(layers.Count == 0 ? "**一个都没有**" : string.Join("/", layers.Select(t => t.ToString("0.0"))))}"
                     + $"　左边界外首个不过：{(a > 0 ? why[a - 1] : "—（没探到）")}"
                     + $"　右边界外首个不过：{(b < pts.Count - 1 ? why[b + 1] : "—（没探到）")}";
                if (!openLo && !openHi && width < layer - 1e-9) narrow.Add($"片{j}（宽 {width:0.0} mm）");
                if (layers.Count == 0) noLayer.Add($"片{j}");
            }
            summary.Add(head);
            W(head);
            W("　逐点（值 mm｜可行？｜最热铂高出热偶读数｜管根低于热偶读数｜管孔净流入｜不过的是哪条）");
            for (int i = 0; i < pts.Count; i++)
                W($"　　{pts[i]:0.0}\t{(ok[i] is null ? "判不了" : ok[i] == true ? "过" : "不过")}\t"
                  + $"{Fmt(hot[i], "0.###")}\t{Fmt(cold[i], "0.###")}\t{Fmt(flux[i], "0.###")}\t{why[i]}"
                  + (Math.Abs(pts[i] - v0) < 1e-12 ? "\t← 解值" : "")
                  + (Math.Abs(pts[i] / layer - Math.Round(pts[i] / layer)) < 1e-9 ? "\t（0.5 档）" : ""));
            W("");
            Flush();
        }

        W("═══════ 小结 ═══════");
        foreach (string s in summary) W(s);
        W("");
        W(narrow.Count == 0 ? "没有片的窗口窄于每层厚（在扫到的边界之内）。"
                            : $"**窗口窄于制造精度（每层 {layer:0.###} mm）的片**：" + string.Join("、", narrow) + " —— 点名。判据不放宽（用户 2026-09-17）。");
        W(noLayer.Count == 0 ? "每一片的窗口里都至少有一个 0.5 档可选。"
                             : "**窗口里一个 0.5 档都没有的片**：" + string.Join("、", noLayer)
                               + " —— 这一片在 0.5 的格子上**没有可行的层数**，不是求解器没找到。");
        if (cut) W("⚠ 扫描中途被时间闸切断，上面有片不完整。");
        if (parallelChecked && !parallelSame) W("⚠ 并发自检没对上 ⇒ 本文件的扫描数不可引用。");
        W("");
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
          + "层厚 = InsulationSearch.LayerMm。每个数来自这一次运行（同一进程）；靶子那一列标着出处文件，是另一次跑。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.True(parallelChecked, "一片都没扫到解值那一点 —— 扫描没跑起来");
    }

    private static List<(double Lo, double Hi)> FeasibleSegments(List<double> pts, bool?[] ok)
    {
        var segs = new List<(double, double)>();
        int i = 0;
        while (i < pts.Count)
        {
            if (ok[i] == true)
            {
                int s = i;
                while (i + 1 < pts.Count && ok[i + 1] == true) i++;
                segs.Add((pts[s], pts[i]));
            }
            i++;
        }
        return segs;
    }

    private static LineResult SafeRun(LineCase lc, Probe? probe)
    {
        try { return LineRunner.Run(lc, probe); }
        catch (Exception ex) { return new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
    }

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    /// <summary>
    /// 判据表那一行 —— 走生产的 <see cref="Criteria.OneLine"/>，**不在门里手抄一份**。
    /// ★ 2026-09-18，Opus 5：原来这里各抄了一份判词，而手抄那份把参考量印成「过」——
    ///   于是输出里出现「过，裕度 −0.401」（旁证 A 第 2.4(c) 节）。门不许手抄生产配方。
    /// </summary>
    private static string Line(ConstraintOut c) => Criteria.OneLine(c);

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);

    private sealed class Probe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _last;
        public Probe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [+{now - _last,7:0.0}s 累计{now / 60,7:0.0}min]  {value}\r\n");
                _last = now;
            }
        }
    }
}
