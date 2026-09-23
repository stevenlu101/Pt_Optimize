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
//  R48 L 路　**圆盘保温 10 mm 下，逐片舌保温的可行窗口** —— 2026-09-17，Opus 5
//
//  ══ 为什么重量
//
//  §0.-9 那张「可行窗口」表是在**圆盘保温 20 mm**（现场缠不出来的那个数）＋**1 K 旧耦合容差**下量的。
//  两个前提这两轮都换了 ⇒ 那张表的端点不能直接引用。本轮在**同一次运行**里把 10 与 20 两档都量一遍，
//  两边才可比（「两个数的差」要先验两边可比）。
//
//  ══ 本轮跑什么
//
//    两份复原的可交付设计（W08 导航档 4245 g／W06 细网格档 3379 g），
//    圆盘保温分别取 **10 mm（生产默认 = 接合区上限）** 与 **20 mm（旧值，对照）**，
//    逐片把舌保温在解值 ±1.0 mm 内按 0.1 mm 扫，网格 = 判决那张（MeshVerify.RequiredMeshFor）。
//    可行 = 整线解出来了 ∧ 外层耦合收敛 ∧ 卡交付的判据全过（LineResult.AllOk，与交付判定同一份）。
//
//  ⚠ **20 mm 那一档的「可行」一定是假的** —— 新硬安全线「接合区保温缠得出来」在 20 mm 上必然不过，
//    AllOk 恒为假。所以 20 mm 档改用**三条热学硬安全线全过**当窗口口径，并在表头写明这是**另一把尺子**，
//    不与 10 mm 档的「可行」混为一谈。10 mm 档两把尺子都印（应当一致，当场核对）。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//    · 窗口宽 < 每层 0.5 mm ⇒ **点名**：判据窗口窄于制造精度。判据不许因此放宽。
//    · 窗口里一个 0.5 的倍数都没有 ⇒ 再点一次：这一片在这张格子上没有可行的层数。
//    · 扫到 ±1.0 mm 边界仍全过 ⇒ 印「边界没探到，真实窗口更宽」，不许把 2.0 当窗口宽。
//    · 解值那一点自己就不过 ⇒ 照实印，窗口无从谈起；扫到的可行段照样列出来。
// ════════════════════════════════════════════════════════════════════════════

public abstract class R48LDiscInsul10WindowBase
{
    protected readonly ITestOutputHelper _o;
    protected R48LDiscInsul10WindowBase(ITestOutputHelper o) { _o = o; }

    private const double ScanStepMm = 0.1, ScanHalfMm = 1.0;
    private static readonly TimeSpan ScanCap = TimeSpan.FromHours(3.0);
    private static readonly int ScanDop = Math.Max(1, Math.Min(5, Environment.ProcessorCount - 2));

    /// <summary>三条热学硬安全线 —— 20 mm 档的窗口口径（那一档「可行」被缠绕上限一票否决，看不出热学）。</summary>
    private static readonly string[] ThermalKeys =
        { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux };

    protected void Run(string which)
    {
        var d0 = which == "W08" ? R48LW08NavDesign.Build()
               : which == "W06" ? R48LW06FineDesign.Build()
               : throw new ArgumentException(which);
        string source = which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source;

        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_圆盘保温10_舌保温可行窗口_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var o0 = new SolverOptions();
        double layer = InsulationSearch.LayerMm;
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d0, p);
        var meshOpt = new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius };

        W($"R48 L 路　**圆盘保温 {WrapLimits.JointZoneMaxMm:0.#} mm 下的舌保温可行窗口**　{which}（{d0.Name}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W($"网格 = 判决那张（细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）。");
        W($"包法每层 {layer:0.###} mm；舌保温扫描范围 解值 ±{ScanHalfMm:0.0} mm，步长 {ScanStepMm:0.0} mm，并发 {ScanDop} 条整线解。");
        W("");
        W("═══════ 为什么重量 ═══════");
        W("§0.-9 那张窗口表是在**圆盘保温 20 mm**（现场缠不出来）＋**1 K 旧耦合容差**下量的，两个前提这两轮都换了。");
        W($"现场限制：{WrapLimits.SourceNote}");
        W("本轮把 10 与 20 两档放在**同一次运行**里量，两边才可比。");
        W("");
        W("── 两把尺子（**跑前写死**）");
        W($"　· 10 mm 档：窗口 = 「可行」= LineResult.AllOk（与交付判定同一份）。同时并印三条热学硬安全线那把尺子，当场核对两者一致。");
        W($"　· 20 mm 档：AllOk 恒为假（新硬安全线「{Criteria.Plain(LineResult.Key.WrapTurns)}」在 20 mm 上必然不过）⇒ 只能用**三条热学硬安全线全过**当尺子。");
        W("　　⇒ 两档的窗口**不是同一把尺子量的**，表里各自标着；只有热学那一列可以直接比。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W($"　① 窗口宽 < 每层 {layer:0.###} mm ⇒ 点名：判据窗口窄于制造精度。判据不许因此放宽。");
        W($"　② 窗口里一个 {layer:0.###} 的倍数都没有 ⇒ 再点一次。");
        W($"　③ 扫到 ±{ScanHalfMm:0.0} mm 边界仍全过 ⇒ 印「边界没探到」，不许把 {2 * ScanHalfMm:0.0} 当窗口宽。");
        W("　④ 解值那一点自己就不过 ⇒ 照实印；扫到的可行段照样列出来。");
        W("");
        W("── 这一份设计是照表复原的，不是求解器返回的对象");
        W($"出处：{source}");
        W($"舌保温解值 {string.Join("/", d0.TabInsulMm.Select(v => v.ToString("0.0")))} mm；"
          + $"最厚一片 {d0.TabInsulMm.Max():0.0} mm —— **舌板不受接合区限**（用户原话「其它地方(舌板与管)好缠绕」）。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_盘10窗口_{which}_{stamp}_进行中.log");
        var probe = new SlowProbe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var summary = new List<string>();
        foreach (double disc in new[] { WrapLimits.JointZoneMaxMm, 20.0 })
        {
            bool isCap = Math.Abs(disc - WrapLimits.JointZoneMaxMm) < 1e-9;
            string ruler = isCap ? "可行（AllOk，与交付判定同一份）" : "三条热学硬安全线全过（**另一把尺子**：AllOk 在这一档恒假）";
            W($"═══════ 圆盘保温 {disc:0.#} mm（{InsulationSearch.LayersText(disc)}）　尺子 = {ruler} ═══════");
            Flush();

            var dBase = Clone(d0, disc);

            // 解值那一点先跑一次 ②，给并发自检当靶子
            probe.Report($"── 盘 {disc:0.#}：解值点 ②");
            var swx = Stopwatch.StartNew();
            var lc0 = dBase.BuildCase(p);
            Solver.ApplyCaseMesh(lc0, meshOpt);
            var r0 = SafeRun(lc0, probe);
            swx.Stop();
            W($"解值点 ②：耗时 {swx.Elapsed.TotalSeconds:0} s，网格单元 {r0.MeshCells}，"
              + $"耦合 {r0.CoupleRounds} 轮、容差 {r0.CoupleTolKUsed:0.000} K、剩余误差估计 {r0.CoupleRemainK:0.000} K；结论 {Verdict(r0)}");
            W("  交付判据逐条：");
            foreach (var ck in r0.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(ck));
            W($"  铂重 {r0.TotalMassG:0} g");
            W("");
            Flush();

            for (int j = 0; j < dBase.TabInsulMm.Length; j++)
            {
                double v0 = dBase.TabInsulMm[j];
                int half = (int)Math.Round(ScanHalfMm / ScanStepMm);
                var pts = Enumerable.Range(-half, 2 * half + 1).Select(k => Math.Round(v0 + k * ScanStepMm, 6)).ToList();
                var ok = new bool?[pts.Count];
                var why = new string[pts.Count];
                var hot = new double[pts.Count];
                var cold = new double[pts.Count];
                var flux = new double[pts.Count];
                var deliverable = new bool?[pts.Count];
                LineResult? atSolved = null;
                for (int i = 0; i < pts.Count; i++) { hot[i] = cold[i] = flux[i] = double.NaN; why[i] = ""; }

                var sw = Stopwatch.StartNew();
                bool cut = false;
                probe.Report($"── 盘 {disc:0.#}　片{j}：{pts.Count} 点（{pts[0]:0.0}…{pts[^1]:0.0} mm），并发 {ScanDop}");
                using (var cts = new CancellationTokenSource(ScanCap))
                {
                    try
                    {
                        Parallel.For(0, pts.Count,
                            new ParallelOptions { MaxDegreeOfParallelism = ScanDop, CancellationToken = cts.Token }, i =>
                        {
                            double v = pts[i];
                            if (v < o0.InsLoMm - 1e-9) { ok[i] = null; why[i] = "下界外（低于裸舌 0.30）"; return; }
                            var d = dBase.Clone();
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
                            deliverable[i] = r.AllOk;
                            ok[i] = isCap ? r.AllOk : ThermalOk(r);
                            if (ok[i] != true)
                                why[i] = isCap ? string.Join("／", r.Failed.Select(Criteria.Plain))
                                               : string.Join("／", ThermalKeys.Select(k => Get(r, k))
                                                     .Where(c => c is null || !c.Ok || c.Undetermined)
                                                     .Select(c => c is null ? "热学判据缺席" : Criteria.Plain(c.Name)));
                            if (Math.Abs(v - v0) < 1e-12) atSolved = r;
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        cut = true;
                        W($"★ 盘 {disc:0.#} 片{j} 扫描**被跑前写死的时间闸切断** —— 这一片的窗口不完整，不许当结果读。");
                    }
                }
                sw.Stop();
                probe.Report($"── 盘 {disc:0.#}　片{j} 结束，耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟");

                if (j == 0 && atSolved is not null)
                {
                    bool same = Val(atSolved, LineResult.Key.HotOverTc).Equals(Val(r0, LineResult.Key.HotOverTc))
                             && Val(atSolved, LineResult.Key.ColdUnderTc).Equals(Val(r0, LineResult.Key.ColdUnderTc))
                             && Val(atSolved, LineResult.Key.NetFlux).Equals(Val(r0, LineResult.Key.NetFlux));
                    W($"并发自检（片0 的解值点 vs 上面那次 ②，同一设计同一网格）："
                      + (same ? "**逐位相同** ⇒ 并发没有串味，下面的扫描可读。" : "**对不上** ⇒ 并发下的数不可引用！"));
                    W("");
                }

                int c0 = pts.FindIndex(v => Math.Abs(v - v0) < 1e-12);
                string head;
                if (c0 < 0 || ok[c0] != true)
                {
                    var segs = FeasibleSegments(pts, ok);
                    head = $"盘{disc:0.#}　片{j}　解值 {v0:0.0} mm　**解值那一点自己就不过**（{(c0 >= 0 ? why[c0] : "没扫到")}）⇒ 以它为中心的窗口无从谈起"
                         + (segs.Count == 0 ? "；扫描范围内**一个可行点都没有**"
                                            : "；扫描范围内的可行段：" + string.Join("、", segs.Select(s => $"[{s.Lo:0.0}, {s.Hi:0.0}] 宽 {s.Hi - s.Lo:0.0}")));
                }
                else
                {
                    int a = c0, b = c0;
                    while (a - 1 >= 0 && ok[a - 1] == true) a--;
                    while (b + 1 < pts.Count && ok[b + 1] == true) b++;
                    double lo = pts[a], hi = pts[b], width = hi - lo;
                    bool openLo = a == 0, openHi = b == pts.Count - 1;
                    var layers = new List<double>();
                    for (double t = Math.Ceiling(lo / layer - 1e-9) * layer; t <= hi + 1e-9; t += layer)
                        if (t >= o0.InsLoMm - 1e-9) layers.Add(Math.Round(t, 6));
                    head = $"盘{disc:0.#}　片{j}　解值 {v0:0.0} mm　可行窗口 [{lo:0.0}, {hi:0.0}] mm　宽 {width:0.0} mm"
                         + (openLo || openHi ? $"（{(openLo ? "下" : "")}{(openLo && openHi ? "、" : "")}{(openHi ? "上" : "")}边界**没探到**，真实窗口更宽）" : "")
                         + $"　窗口里的 {layer:0.###} 档：{(layers.Count == 0 ? "**一个都没有**" : string.Join("/", layers.Select(t => t.ToString("0.0"))))}"
                         + (width < layer - 1e-9 && !openLo && !openHi ? "　★ **窄于制造精度，点名**" : "")
                         + $"　左边界外首个不过：{(a > 0 ? why[a - 1] : "—（没探到）")}"
                         + $"　右边界外首个不过：{(b < pts.Count - 1 ? why[b + 1] : "—（没探到）")}";
                }
                if (cut) head += "　⚠ 被时间闸切断，不完整";
                summary.Add(head);
                W(head);
                W("　逐点（值 mm｜本档尺子｜可交付？｜最热铂高出热偶读数｜管根低于热偶读数｜管孔净流入｜不过的是哪条）");
                for (int i = 0; i < pts.Count; i++)
                    W($"　　{pts[i]:0.0}\t{(ok[i] is null ? "判不了" : ok[i] == true ? "过" : "不过")}\t"
                      + $"{(deliverable[i] is null ? "—" : deliverable[i] == true ? "可交付" : "不可交付")}\t"
                      + $"{Fmt(hot[i], "0.###")}\t{Fmt(cold[i], "0.###")}\t{Fmt(flux[i], "0.###")}\t{why[i]}"
                      + (Math.Abs(pts[i] - v0) < 1e-12 ? "\t← 解值" : "")
                      + (Math.Abs(pts[i] / layer - Math.Round(pts[i] / layer)) < 1e-9 ? "\t（0.5 档）" : ""));
                W("");
                Flush();
            }
        }

        W("═══════ 小结（两档并列）═══════");
        W("⚠ 两档的窗口**不是同一把尺子量的**（见表头）：10 mm 档是「可行」，20 mm 档是「三条热学硬安全线全过」。");
        foreach (string s in summary) W(s);
        W("");
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
          + "层厚 = InsulationSearch.LayerMm；接合区上限 = WrapLimits.JointZoneMaxMm。");
        W("本文件里**两档的每个数**都来自这一次运行（同一进程、同一份代码、同一套耦合容差）。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.True(summary.Count == 8, $"应当有 2 档 × 4 片 = 8 行小结，实为 {summary.Count}");
    }

    private static DesignSpec Clone(DesignSpec d0, double discMm)
    {
        var d = d0.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = discMm;
        d.DiscInsulMm = Array.Empty<double>();
        return d;
    }

    private static bool ThermalOk(LineResult r)
        => ThermalKeys.Select(k => Get(r, k)).All(c => c is not null && c.Ok && !c.Undetermined);

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

    private static LineResult SafeRun(LineCase lc, IProgress<string>? probe)
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
}

// ★ 2026-09-17 Opus 5：一份设计一个测试类 —— 理由同 R48LDiscInsul10ResolveBase 那一段（同类里的 InlineData 会排队）。
[Trait("速度", "慢")]
public class R48LDiscInsul10WindowW08Tests : R48LDiscInsul10WindowBase
{
    public R48LDiscInsul10WindowW08Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 圆盘保温10下的舌保温可行窗口_W08() => Run("W08");
}

[Trait("速度", "慢")]
public class R48LDiscInsul10WindowW06Tests : R48LDiscInsul10WindowBase
{
    public R48LDiscInsul10WindowW06Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 圆盘保温10下的舌保温可行窗口_W06() => Run("W06");
}
