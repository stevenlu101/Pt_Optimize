using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　**圆盘保温 10 mm 下，两份可交付设计还过不过三关** —— 2026-09-17，Opus 5
//
//  ══ 为什么跑这一组
//
//  用户 2026-09-17 现场：「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕(会渐成球形)」
//  「圆盘与管子接触区都是20圈以下，其它地方(舌板与管)好缠绕」⇒ 每圈 0.5 mm ⇒ **接合区上限 10 mm**。
//  而 APP 的圆盘保温默认一直是 **20 mm（40 圈）**、仓库里查不到出处 ——
//  也就是说 §0.-6～§0.-10 交出来的那两份「可交付设计」都站在一个**现场缠不出来**的数上。
//  ⇒ 把默认与上界改成 10 之后，第一件事就是问：**那两份设计还成不成立。**
//
//  ══ 本轮跑什么（一个进程、一份代码，不拼两份仪器输出）
//
//    两份设计各跑 2（圆盘保温 10 / 20 mm）× 2（导航网格 / 细网格）× 三关：
//      ① 升温全程（RampSweep）　② 带玻璃稳态（LineRunner）　③ 空管到温稳态（LineRunner）
//    **20 mm 那一档是对照**，而且是在**同一次运行、同一份代码、同一套耦合容差**下跑的 ——
//    §0.-10 收紧耦合容差之后，旧文件里那些 1 K 口径的数不能直接拿来当 20 mm 档
//    （「两个数的差」要先验两边可比）。这就是为什么 20 mm 也要重跑，而不是抄旧文件。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//
//    ① 三条热学硬安全线（最热铂高出热偶读数／管根低于热偶读数／管孔净流入）**按裕度并列**；
//       10 mm 档任一条不过 ⇒ **就写不过**，不许改限值、不许换网格重跑充数。
//    ② 「接合区保温缠得出来」这条新判据在 **20 mm 档必然不过**（20 > 10）——
//       这是构造上的，不是发现；它在报告里**单列**，不与三条热学判据混在一起说。
//       ⚠ 所以 20 mm 档的「整体判定」一定是「不过」；要看热学，看那三条裕度。
//    ③ 铂重变化如实报，不设门槛、不做解释性加工。
//    ④ 复原件先跑一次 ② 与上一跑的靶子对照；对不上就照实印、照实继续（不许拿「差不多」当同一份设计）。
//       ⚠ 靶子是 **1 K 旧容差**那一跑的数，本次是收紧后的口径 ⇒ **本来就会有 0.2–0.3 K 的差**，
//         那不是复原失败。判读：差 ≤ 0.5 K 记「在收紧口径已知的位移量级内」，> 0.5 K 才点名。
//    ⑤ 任何一关判不了 ⇒ 记「判不了」，**判不了不算过**。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LDiscInsul10GateRunTests
{
    private readonly ITestOutputHelper _o;
    public R48LDiscInsul10GateRunTests(ITestOutputHelper o) { _o = o; }

    /// <summary>复原自证的判读门槛（跑前写死）：靶子出自 1 K 旧容差那一跑，收紧后位移 ≤ 这个数不算对不上。</summary>
    private const double RestoreTolK = 0.5;

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 圆盘保温10下重判三关(string which)
    {
        var d0 = which == "W08" ? R48LW08NavDesign.Build()
               : which == "W06" ? R48LW06FineDesign.Build()
               : throw new ArgumentException(which);
        string source = which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source;

        // 上一跑（1 K 旧容差）印出来的靶子 —— 只用于复原自证，不参与本轮任何结论
        double tHot = which == "W08" ? R48LW08NavDesign.NavHotOverTcK : R48LW06FineDesign.FineHotOverTcK;
        double tCold = which == "W08" ? R48LW08NavDesign.NavColdUnderTcK : R48LW06FineDesign.FineColdUnderTcK;
        double tFlux = which == "W08" ? R48LW08NavDesign.NavNetFluxW : R48LW06FineDesign.FineNetFluxW;
        double tMass = which == "W08" ? R48LW08NavDesign.NavTotalMassG : R48LW06FineDesign.FineTotalMassG;
        string tMesh = which == "W08" ? "导航（细区 2.0 mm）" : "细网格（细区 1.000 mm）";

        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_圆盘保温10_重判三关_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var navCase = new LineCase();
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d0);
        var meshNav = new SolverOptions { FineMm = navCase.MeshFineMm, FineRadiusMm = navCase.MeshFineRadiusMm };
        var meshFine = new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius };

        W($"R48 L 路　**圆盘保温 {WrapLimits.JointZoneMaxMm:0.#} mm 下重判三关**　{which}（{d0.Name}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 为什么跑 ═══════");
        W($"现场限制（{WrapLimits.SourceNote}）⇒ 接合区上限 {WrapLimits.JointZoneMaxMm:0.#} mm"
          + $"（= {WrapLimits.MaxTurnsAtJoint} 圈 × 每圈 {InsulationSearch.LayerMm:0.#} mm）。");
        W($"APP 原来的圆盘保温默认是 **20 mm（40 圈）**、仓库里查不到出处 ⇒ 上一跑那两份「可交付设计」站在一个现场缠不出来的数上。");
        W($"本轮把默认与上界都改成 {WrapLimits.JointZoneMaxMm:0.#} mm（DesignSpec.FlangeInsulMm、WrapLimits.JointZoneMaxMm），再问这两份设计还成不成立。");
        W($"接合区口径：{WrapLimits.ZoneNote}");
        W("");
        W("═══════ 本轮跑什么 ═══════");
        W($"2（圆盘保温 {WrapLimits.JointZoneMaxMm:0.#} / 20 mm）× 2（导航网格 {navCase.MeshFineMm:0.0} mm / 细网格 {reqFine:0.000} mm，细区半径 {reqRadius:0.0} mm）× 三关。");
        W("**20 mm 那一档是在同一次运行里重跑的**，不是抄旧文件：§0.-10 把耦合停机容差从 1 K 收紧到判据相关口径之后，");
        W("　旧文件里那些数带着 0.4–0.9 K 的停机噪声，拿它当对照就是「两个数的差」两边不可比。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("　① 三条热学硬安全线按裕度并列；10 mm 档任一条不过 ⇒ **就写不过**，不改限值、不换网格重跑。");
        W("　② 「接合区保温缠得出来」在 20 mm 档**必然不过**（构造上的，不是发现）⇒ 单列，不与三条热学判据混说。");
        W("　③ 铂重变化如实报，不设门槛、不做解释性加工。");
        W($"　④ 复原自证：与上一跑靶子差 ≤ {RestoreTolK:0.0} K 记「在收紧口径已知的位移量级内」，> {RestoreTolK:0.0} K 才点名。");
        W("　⑤ 任何一关判不了 ⇒ 记「判不了」，判不了不算过。");
        W("");
        W("═══════ 这一份设计是照表复原的，不是求解器返回的对象 ═══════");
        W($"出处：{source}");
        W("⚠ §0.-8 已实测：复原件与求解器那份在进几何的字段上差 1 ulp。§0.-10 收紧容差后，管根那条的摆幅已降到 0.015 K。");
        W($"旋钮终值：板厚 {string.Join("/", d0.TabThickMm.Select(v => v.ToString("0.00")))} mm　"
          + $"舌保温 {string.Join("/", d0.TabInsulMm.Select(v => v.ToString("0.0")))} mm　"
          + $"舌片厚 {string.Join("/", d0.TongueThickMm.Select(v => v.ToString("0.00")))} mm");
        W($"⚠ 舌保温最厚一片 {d0.TabInsulMm.Max():0.0} mm —— **舌板不受接合区限**（用户原话「其它地方(舌板与管)好缠绕」）。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_盘10三关_{which}_{stamp}_进行中.log");
        var probe = new SlowProbe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        // ── 复原自证 ──
        W("═══════ 复原自证：② 带玻璃稳态 vs 上一跑的靶子 ═══════");
        W($"靶子网格 {tMesh}；本次用**同一张**网格、圆盘保温按上一跑那个值 20 mm ⇒ 只差耦合容差口径。");
        var meshSame = which == "W08" ? meshNav : meshFine;
        var dChk = Clone(d0, 20.0);
        var lcChk = dChk.BuildCase(p);
        Solver.ApplyCaseMesh(lcChk, meshSame);
        probe.Report("── 复原自证 ②（20 mm，靶子那张网格）");
        var swx = Stopwatch.StartNew();
        var rChk = SafeRun(lcChk, probe);
        swx.Stop();
        W($"耗时 {swx.Elapsed.TotalSeconds:0} s，网格单元 {rChk.MeshCells}，耦合 {rChk.CoupleRounds} 轮、容差 {rChk.CoupleTolKUsed:0.000} K、剩余误差估计 {rChk.CoupleRemainK:0.000} K。");
        W("判据\t本次（收紧后容差）\t上一跑靶子（1 K 容差）\t差\t判读");
        foreach (var (nm, now, tgt) in new[]
        {
            ("最热铂高出热偶读数", Val(rChk, LineResult.Key.HotOverTc), tHot),
            ("管根低于热偶读数", Val(rChk, LineResult.Key.ColdUnderTc), tCold),
            ("管孔净流入", Val(rChk, LineResult.Key.NetFlux), tFlux),
        })
            W($"{nm}\t{Fmt(now, "0.###")}\t{tgt:0.###}\t{now - tgt:+0.###;-0.###}\t"
              + (double.IsNaN(now) ? "**算不出来**" : Math.Abs(now - tgt) <= RestoreTolK ? "在收紧口径已知的位移量级内" : "**点名：超出跑前写死的 " + RestoreTolK.ToString("0.0") + " K**"));
        W($"铂重 g\t{rChk.TotalMassG:0}\t{tMass:0}\t{rChk.TotalMassG - tMass:+0;-0}\t（铂重与耦合容差无关，应逐位相同）");
        W("");
        Flush();

        // ── 主表：2 × 2 × 三关 ──
        var rows = new List<Row>();
        foreach (double disc in new[] { WrapLimits.JointZoneMaxMm, 20.0 })
            foreach (var (meshName, mesh) in new[] { ("导航", meshNav), ("细网格", meshFine) })
            {
                string tag = $"圆盘保温 {disc:0.#} mm ／ {meshName}";
                W($"═══════ {tag} ═══════");
                Flush();
                var d = Clone(d0, disc);
                var row = new Row { Disc = disc, Mesh = meshName };

                // ① 升温全程
                probe.Report($"── {tag}：① 升温全程");
                var sw = Stopwatch.StartNew();
                RampSweepResult? ramp = null;
                try { ramp = RampSweep.Run(d, p, new RampSweepOptions { RunClampAlt = false, Mesh = mesh }, probe); }
                catch (Exception ex) { W($"── ① 升温全程：**抛异常** {ex.GetType().Name}：{ex.Message}"); }
                sw.Stop();
                row.Ramp = ramp?.Verdict ?? "抛异常";
                W($"── ① 升温全程　结论：{row.Ramp}　耗时 {sw.Elapsed.TotalSeconds:0} s，{ramp?.Points.Length ?? 0} 个设定点");
                if (ramp is not null && ramp.VerdictDetail.Length > 0) W(ramp.VerdictDetail);
                Flush();

                // ② 带玻璃稳态
                probe.Report($"── {tag}：② 带玻璃稳态");
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, mesh);
                sw = Stopwatch.StartNew();
                var rg = SafeRun(lc, probe);
                sw.Stop();
                row.Glass = rg;
                W($"── ② 带玻璃稳态　网格 细区 {lc.MeshFineMm:0.000} mm／细区半径 {lc.MeshFineRadiusMm:0.0} mm，单元 {rg.MeshCells}");
                W($"结论：{Verdict(rg)}");
                W($"耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {rg.TotalMassG:0} g，耦合 {rg.CoupleRounds} 轮、容差 {rg.CoupleTolKUsed:0.000} K、剩余误差估计 {rg.CoupleRemainK:0.000} K。");
                W("  交付判据逐条：");
                foreach (var ck in rg.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(ck));
                Flush();

                // ③ 空管到温稳态
                probe.Report($"── {tag}：③ 空管到温稳态");
                var lcE = d.BuildCase(p, emptyTube: true);
                Solver.ApplyCaseMesh(lcE, mesh);
                sw = Stopwatch.StartNew();
                var re = SafeRun(lcE, probe);
                sw.Stop();
                row.Empty = re;
                W($"── ③ 空管到温稳态　结论：{Verdict(re)}　耗时 {sw.Elapsed.TotalSeconds:0} s，"
                  + $"耦合 {re.CoupleRounds} 轮、容差 {re.CoupleTolKUsed:0.000} K、剩余误差估计 {re.CoupleRemainK:0.000} K");
                W("  交付判据逐条：");
                foreach (var ck in re.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(ck));
                W("");
                rows.Add(row);
                Flush();
            }

        // ── 并列表 ──
        W("═══════ 并列：三条热学硬安全线的裕度（正 = 还有余量）═══════");
        W("⚠ 「接合区保温缠得出来」不在这张表里 —— 它在 20 mm 档必然不过，单列在下面。");
        W("圆盘保温\t网格\t最热铂高出热偶读数\t管根低于热偶读数\t管孔净流入\t铂重 g\t① 升温\t② 整体\t③ 整体");
        foreach (var r in rows)
            W($"{r.Disc:0.#} mm\t{r.Mesh}\t{Slack3(r.Glass, LineResult.Key.HotOverTc)}\t{Slack3(r.Glass, LineResult.Key.ColdUnderTc)}\t"
              + $"{Slack3(r.Glass, LineResult.Key.NetFlux)}\t{Fmt(r.Glass?.TotalMassG ?? double.NaN, "0")}\t{r.Ramp}\t"
              + $"{Short(r.Glass)}\t{Short(r.Empty)}");
        W("");
        W("── 同一张网格上 10 mm 与 20 mm 的差（10 − 20；负 = 收薄之后变差）");
        W("网格\t最热铂高出热偶读数 裕度差 K\t管根低于热偶读数 裕度差 K\t管孔净流入 裕度差 W\t铂重差 g");
        foreach (string mesh in new[] { "导航", "细网格" })
        {
            var a = rows.FirstOrDefault(r => r.Mesh == mesh && Math.Abs(r.Disc - WrapLimits.JointZoneMaxMm) < 1e-9);
            var b = rows.FirstOrDefault(r => r.Mesh == mesh && Math.Abs(r.Disc - 20.0) < 1e-9);
            W($"{mesh}\t{Diff(a?.Glass, b?.Glass, LineResult.Key.HotOverTc)}\t{Diff(a?.Glass, b?.Glass, LineResult.Key.ColdUnderTc)}\t"
              + $"{Diff(a?.Glass, b?.Glass, LineResult.Key.NetFlux)}\t"
              + $"{((a?.Glass is null || b?.Glass is null) ? "—" : (a.Glass.TotalMassG - b.Glass.TotalMassG).ToString("+0;-0"))}");
        }
        W("");
        W("═══════ 单列：接合区保温缠得出来（新硬安全线）═══════");
        W($"限值 {WrapLimits.JointZoneMaxMm:0.#} mm = {WrapLimits.MaxTurnsAtJoint} 圈。");
        W("圆盘保温\t网格\t实际 mm\t判定\t位置");
        foreach (var r in rows)
        {
            var w = Get(r.Glass, LineResult.Key.WrapTurns);
            W($"{r.Disc:0.#} mm\t{r.Mesh}\t{(w is null ? "—" : Fmt(w.Actual, "0.###"))}\t"
              + $"{(w is null ? "**判据缺席**" : w.Undetermined ? "**判不了**" : w.Ok ? "过" : "**不过**")}\t{w?.Where ?? "—"}");
        }
        W("");

        // ── 一句话 ──
        var fine10 = rows.First(r => r.Mesh == "细网格" && Math.Abs(r.Disc - WrapLimits.JointZoneMaxMm) < 1e-9);
        var nav10 = rows.First(r => r.Mesh == "导航" && Math.Abs(r.Disc - WrapLimits.JointZoneMaxMm) < 1e-9);
        W("═══════ 一句话 ═══════");
        W($"{which}（圆盘保温 {WrapLimits.JointZoneMaxMm:0.#} mm）：导航网格上 {Short(nav10.Glass)}、细网格上 {Short(fine10.Glass)}；"
          + $"细网格三条热学裕度 {Slack3(fine10.Glass, LineResult.Key.HotOverTc)} / {Slack3(fine10.Glass, LineResult.Key.ColdUnderTc)} / {Slack3(fine10.Glass, LineResult.Key.NetFlux)}；"
          + $"铂重 {Fmt(fine10.Glass?.TotalMassG ?? double.NaN, "0")} g。"
          + $"　① {fine10.Ramp}　③ {Short(fine10.Empty)}。");
        W("");
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；升温全程 = RampSweep.Run；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
          + "接合区上限 = WrapLimits.JointZoneMaxMm；耦合容差 = LineRunner.CoupleTolKFor（本文件逐行印了实际用的那个数）。");
        W("本文件里**每一个数**都来自这一次运行（同一进程、同一份代码）；只有「上一跑靶子」那一列标着出处文件，是另一次跑。");
        Flush();
        _o.WriteLine(sb.ToString());

        // 只守「每一步都有结果」——判据不过是**结果**，不是测试失败
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.Glass));
    }

    private sealed class Row
    {
        public double Disc;
        public string Mesh = "", Ramp = "";
        public LineResult? Glass, Empty;
    }

    /// <summary>复制一份设计，只改圆盘保温（其余一位不动）。</summary>
    private static DesignSpec Clone(DesignSpec d0, double discMm)
    {
        var d = d0.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = discMm;
        d.DiscInsulMm = Array.Empty<double>();   // 逐片留空 = 全线沿用这个值（两份复原设计本来就没有逐片值）
        return d;
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

    private static string Short(LineResult? r)
        => r is null ? "没跑" : !r.Ok ? "判不了" : !r.Converged ? "未收敛" : r.AllOk ? "过" : "不过／判不了";

    /// <summary>
    /// 判据表那一行 —— 走生产的 <see cref="Criteria.OneLine"/>，**不在门里手抄一份**。
    /// ★ 2026-09-18，Opus 5：原来这里各抄了一份判词，而手抄那份把参考量印成「过」——
    ///   于是输出里出现「过，裕度 −0.401」（旁证 A 第 2.4(c) 节）。门不许手抄生产配方。
    /// </summary>
    private static string Line(ConstraintOut c) => Criteria.OneLine(c);

    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN
         : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string Slack3(LineResult? r, string key) => Fmt(Slack(Get(r, key)), "+0.###;-0.###");

    private static string Diff(LineResult? a, LineResult? b, string key)
    {
        double x = Slack(Get(a, key)), y = Slack(Get(b, key));
        return double.IsNaN(x) || double.IsNaN(y) ? "—" : (x - y).ToString("+0.###;-0.###");
    }

    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);
}

/// <summary>长跑的进度活页：每行带时刻**与自上一行的耗时**当场落盘（长跑要放探针）。本轮三个慢门共用这一份。</summary>
internal sealed class SlowProbe : IProgress<string>
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private double _last;
    public SlowProbe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
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
