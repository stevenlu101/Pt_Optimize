using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　**舌保温的可行窗口有多宽** —— 2026-09-17，Opus 5。
//  U 路改写（2026-09-18，Opus 5）：**扫描本身已经提成生产件** InsulWindow，
//  本档从「自带一套扫描」改成「调生产件 + 把它的结果落成一份带时刻的证据」。
//
//  ══ 为什么要改
//
//  原来这里有一份完整的扫描实现（并发、切段、落档、判读）。它量出来的结论
//  （W08 共用1 那一片窗口 [2.3, 2.3]、里面一个 0.5 的倍数都没有）只活在一份 txt 里，
//  工程师在 APP 上点不到；而一旦要让 APP 也会量，就会出现**第二份实现** ——
//  两份迟早漂开，那正是本项目最常见的失效。⇒ 生产件只有 InsulWindow.Measure 一份，
//  APP 的终验调它，本探针也调它，门（R48UInsulWindowGateTests）验的也是它。
//
//  ══ 判读（**跑前写死在生产件里，跑完不挪**，见 InsulWindow 的类注释）
//    · 窗口里一个能缠的档都没有 ⇒「当前判据下无可制造设计」，点名那几片；
//    · 窗口宽 < 一层 ⇒ 点名「窗口窄于制造精度」。判据不许因此放宽（用户 2026-09-17）；
//    · 扫到边界仍全过 ⇒ 印「边界没探到」，不许把 2.0 当窗口宽；
//    · 复原件与靶子对不上、或解值那一点自己就不过 ⇒ 照实印，不补、不挪。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LInsulWindowTests
{
    private readonly ITestOutputHelper _o;
    public R48LInsulWindowTests(ITestOutputHelper o) { _o = o; }

    private static readonly TimeSpan ScanCap = TimeSpan.FromHours(2.0);

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

        var opt = new InsulWindow.Options { Cap = ScanCap };
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d0);
        var meshOpt = new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius };

        W($"R48 L 路　**舌保温的可行窗口有多宽**　{which}（{d0.Name}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5；2026-09-18 Opus 5 改调生产件 InsulWindow.Measure");
        W($"网格 = 判决那张（细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）。");
        W($"包法每层 {InsulationSearch.LayerMm:0.###} mm（InsulationSearch.LayerMm）；求解器的舌保温图纸格本轮已改成同一个数。");
        W($"温差预算（工程师填的，本次是默认）：最热铂高出热偶读数 ≤ {p.HotOverTcAllowK:0.###} K、管根低于热偶读数 ≤ {p.ColdUnderTcAllowK:0.###} K。");
        W("");
        W("── 这一份设计是**照表复原**的，不是求解器返回的对象");
        W($"出处：{source}");
        W("⚠ §0.-8 已实测：复原件与求解器那份在进几何的字段上差 1 ulp（舌保温、舌片厚各一处），");
        W("　而「管根低于热偶读数」对那一个 ulp 敏感（同一网格摆 0.37–0.50 K）。靠近这条边界的窗口端点带着这一档噪声。");
        W("");
        W("── 判读（**跑前写死在生产件 InsulWindow 里，跑完不挪**）");
        W($"　① 窗口宽 < 每层 {InsulationSearch.LayerMm:0.###} mm ⇒ **点名**：判据窗口窄于制造精度。判据不许因此放宽。");
        W($"　② 窗口里一个能缠的档都没有（含 0 层 = 裸舌 {opt.Solver.InsLoMm:0.###} mm）⇒ 判「当前判据下无可制造设计」并点名。");
        W($"　③ 扫到 ±{opt.HalfWidthMm:0.0} mm 边界仍全过 ⇒ 印「边界没探到」，不许把 {2 * opt.HalfWidthMm:0.0} 当窗口宽。");
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

        // ── 逐片扫：**调生产件**，本档不再自带一套扫描 ──
        W("═══════ 逐片扫舌保温（其余片与其余旋钮一位不动）═══════");
        W($"量法：生产件 InsulWindow.Measure（APP 终验点的就是它）。解值 ±{opt.HalfWidthMm:0.0} mm、步长 {opt.StepMm:0.0} mm、"
          + $"并发 {opt.MaxDegreeOfParallelism} 条整线解；可行 = 整线解出来了 ∧ 外层耦合收敛 ∧ 卡交付的判据全过（LineResult.AllOk，与交付判定同一份）。");
        W("");
        Flush();

        var win = InsulWindow.Measure(d0, p, opt, probe, CancellationToken.None);

        foreach (var pw in win.Plates)
        {
            W(pw.SolvedFeasible
                ? $"片{pw.Plate}（{pw.Name}）　解值 {pw.SolvedMm:0.###} mm　可行窗口 [{pw.LoMm:0.###}, {pw.HiMm:0.###}] mm　宽 {pw.WidthMm:0.###} mm"
                  + (pw.OpenLo || pw.OpenHi ? $"（{(pw.OpenLo ? "下" : "")}{(pw.OpenLo && pw.OpenHi ? "、" : "")}{(pw.OpenHi ? "上" : "")}边界**没探到**，真实窗口更宽）" : "")
                  + $"　窗口里的档：{(pw.LayerText.Length == 0 ? "**一个都没有**" : string.Join("/", pw.LayerText))}"
                  + $"　左边界外首个不过：{(pw.BlockedBelow.Length == 0 ? "—（没探到）" : pw.BlockedBelow)}"
                  + $"　右边界外首个不过：{(pw.BlockedAbove.Length == 0 ? "—（没探到）" : pw.BlockedAbove)}"
                : $"片{pw.Plate}（{pw.Name}）　解值 {pw.SolvedMm:0.###} mm　**解值那一点自己就不过**（{pw.SolvedWhy}）⇒ 以它为中心的窗口无从谈起"
                  + (pw.OtherFeasible.Length == 0 ? "；扫描范围内**一个可行点都没有**"
                     : "；扫描范围内的可行段：" + string.Join("、", pw.OtherFeasible.Select(s => $"[{s.Lo:0.###}, {s.Hi:0.###}] 宽 {s.Hi - s.Lo:0.###}"))));
            W("　逐点（值 mm｜可行？｜最热铂高出热偶读数｜管根低于热偶读数｜管孔净流入｜不过的是哪条）");
            foreach (var pt in pw.Points)
                W($"　　{pt.Mm:0.###}\t{(pt.Feasible is null ? "判不了" : pt.Feasible == true ? "过" : "不过")}\t"
                  + $"{Fmt(pt.HotK, "0.###")}\t{Fmt(pt.ColdK, "0.###")}\t{Fmt(pt.NetFluxW, "0.###")}\t{pt.Why}"
                  + (pt.IsSolved ? "\t← 解值" : "") + (pt.OnLayerGrid ? "\t（现场能缠的档）" : ""));
            W("");
            Flush();
        }

        W("═══════ 小结（生产件自己的报告，APP 判据页与安装报告印的就是这一份）═══════");
        W(win.Report());
        if (win.Cut) W("⚠ 扫描中途被时间闸切断，上面有片不完整。");
        W("");
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：窗口 = InsulWindow.Measure；整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
          + "层厚 = InsulationSearch.LayerMm。每个数来自这一次运行（同一进程）；靶子那一列标着出处文件，是另一次跑。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.NotEmpty(win.Plates);
        Assert.All(win.Plates, pw => Assert.NotEmpty(pw.Points));
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
