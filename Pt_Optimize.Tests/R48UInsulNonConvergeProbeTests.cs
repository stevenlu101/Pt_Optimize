using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 U 路　**不收敛点诊断探针**：片3 舌保温 13.036 处「场解不出来 ⇒ 二分中止」 —— 2026-09-18，Opus 5
//
//  ══ 病灶（出处：deliverable(r48_L)/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt）
//     圆盘保温 10 mm 下重解 W08：片0/1/2 的舌保温分别二分求出 6.0/3.0/4.5 mm，
//     轮到片3 时二分走到中点 **13.0357421875 mm** 上「外层耦合未收敛」⇒ 二分中止 ⇒
//     整跑被报成「**没有可行点**」。三种病因的修法完全相反：
//       ① 停摆（轮数/容差不够）② 发散（物理热失控）③ 几何或场坏（上界 20 mm 把几何抬坏）
//     —— 本探针只**量**，一行生产代码都不改。
//
//  ══ 复现所依据的那条二分轨迹（不是猜的，逐点replay）
//     RaiseUntil 的二分是纯中点法（Solver.NextBisectPoint），起点 lo = 裸舌 0.30、hi = 上界 20.0。
//     依次取中点 10.15 / 15.075 / 12.6125 / 13.84375 / 13.228125 / 12.9203125 / 13.07421875 /
//     12.997265625 / **13.0357421875**（第 9 个中点，报告里印成 13.036）。
//     ⇒ 前八个中点**都解出来了**（否则轨迹早就中止），失败的是第九个。
//     旁证：r48_L 于 2026-09-18 修「二分中止退回值」时，注释里写的被抬过的 lo 正是 **12.997265625**
//     （r48_L/Pt_Optimize/Core/Solver.cs，本树没有这一改）——与本replay 的第八个中点逐位相同。
//
//  ══ 判读（**跑前写死在这里，跑完不挪**）
//     · 收敛           = LineResult.Converged == true。
//     · 停摆（轮数/容差）= 未收敛，且**生产判词**（LineResult.Notes，LineRunner 自己写的那几句）说
//                        「残差**仍在单调收缩**」或给出「分辨率地板」那一条。
//     · 发散           = 未收敛，且生产判词说「残差**没有在收缩**」且**没有**分辨率地板那一条。
//     · 几何或场坏      = LineRunner.Run 抛异常，或 r.Ok == false，或三条交付判据出现 NaN。
//     ⚠ 判读依据取生产自己的判词，不在探针里另抄一份收缩判别（门不许手抄生产配方）。
//     ⚠ 「判不了」既不算「过」也不算「不过」，表里单列一列。
//
//  ══ 怎么跑（每个点算完立刻落盘；点由环境变量给，可分多次开跑、同一个报告文件续写）
//     R48U_PROBE_PTS    片3 舌保温 mm，逗号分隔；特殊记号 BIS9 = replay 出来的第九个中点、
//                       BIS8 / BIS7 = 它两侧的括号端点（12.997265625 / 13.07421875）
//     R48U_PROBE_MESH   nav（求解器第一遍那张：细区 2.0 mm／细区半径 59.0，= RequiredMeshFor 的半径）
//                       gate（那份报告三关用的：细区 2.0 mm／半径 50.0）
//                       judge（判决网格：RequiredMeshFor = 1.000 mm／半径 59.0）
//     R48U_PROBE_TOL    prod（生产默认：停机容差按判据裕度收紧）／const（关掉按裕度收紧，留常数上限）
//     R48U_PROBE_ROUNDS 外层耦合轮数上限（不给 = 生产默认 600）
//     R48U_PROBE_STAMP  报告文件的开跑时刻（不给 = 当前时刻）
//     R48U_PROBE_CAPMIN 每点时间闸（分钟，不给 = 90）
// ════════════════════════════════════════════════════════════════════════════

/// <summary>本探针唯一的一份：设计怎么复原、点怎么跑、一行怎么写。</summary>
internal static class R48UNonConvKit
{
    /// <summary>
    /// **停机态设计**（照 052851 那份报告 §「停机时各旋钮的值，逐片」复原）。
    /// 片3 的舌保温由调用方给 —— 二分就是在这一根上走的。
    /// 出处：deliverable(r48_L)/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt
    /// </summary>
    internal const string Source =
        "r48_L/deliverable/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt　"
      + "§「停机时（或解出来时）各旋钮的值，逐片」四行 + §「求解轨迹」第 1 轮那一行的场定位置";

    internal static DesignSpec StopDesign(double tab3Mm)
    {
        var d = DesignSpec.W08.Clone();
        d.Name = "管壁 0.8 · 留余量（照 圆盘保温10 重解 052851 的停机态复原）";
        d.Provenance = Source;

        // 圆盘保温 = 接合区上限（那一跑的设置：seed.FlangeInsulMm = WrapLimits.JointZoneMaxMm；逐片圆盘保温留空）
        d.FlangeInsulated = true;
        d.FlangeInsulMm = WrapLimits.JointZoneMaxMm;
        d.DiscInsulMm = Array.Empty<double>();

        // 停机态的旋钮（报告 §「停机时各旋钮的值」）
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 6.0, 3.0, 4.5, tab3Mm };
        d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.RingMul2 = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「外级t₂ 1/1/1/1」＝默认规则
        d.RingW1Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「内级r₁ 3*」，星号 = 走默认
        d.RingW2Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 同上「外级r₂ 6*」
        d.TongueThickMm = new[] { 2.03, 3.51, 3.51, 2.03 };                      // 闭式，不是旋钮
        d.SlotSpanDeg = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleRMm = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleAspect = new[] { 1.00, 1.00, 1.00, 1.00 };

        // 场定的位置量（轨迹「★ 场定孔位」那一行）—— 槽张角与孔径都是 0，它们此刻不进几何，照抄只为同源
        d.SlotCenterDeg = new[] { 103.0, 106.0, 106.0, 103.0 };
        d.TabHoleXMm = new[] { -2.0, -2.0, -2.0, -2.0 };
        d.DiscCutRotDeg = new[] { -27.0, -27.0, -27.0, -27.0 };
        return d;
    }

    /// <summary>
    /// **把 RaiseUntil 的二分逐点 replay 出来**（纯中点法，Solver.NextBisectPoint）。
    /// 返回第 1…n 个中点；第 8 个应为 12.997265625（r48_L 的修法注释里那个数），第 9 个 = 报告里的 13.036。
    /// 走向（lo/hi 各挪哪一边）取自那一跑的轨迹：失败点 13.0357421875 唯一确定了前八步的走向。
    /// </summary>
    internal static double[] BisectMidpoints(int n)
    {
        //  bit = true ⇒ 那一步 lo = mid（这一点「不过」）；false ⇒ hi = mid（这一点「过」）
        bool[] bits = { true, false, true, false, false, true, false, true };
        double lo = 0.30, hi = 20.0;                    // SolverOptions.InsLoMm / InsHiMm 的生产默认
        var mids = new List<double>();
        for (int i = 0; i < n; i++)
        {
            double mid = Solver.NextBisectPoint(Solver.Knob.Insul, lo, hi);
            mids.Add(mid);
            if (i >= bits.Length) break;
            if (bits[i]) lo = mid; else hi = mid;
        }
        return mids.ToArray();
    }

    internal enum MeshKind { Nav, Gate, Judge }

    /// <summary>三张网格的配方（都走 Solver.ApplyCaseMesh 那一份，探针不另写）。</summary>
    internal static SolverOptions MeshOptions(MeshKind k, DesignSpec seed)
    {
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed);
        var navCase = new LineCase();
        return k switch
        {
            // 求解器第一遍：Solve 里 navOpt = opt.Clone(); FineMm = 0; FineRadiusMm = opt.FineRadiusMm
            MeshKind.Nav => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = 0, FineRadiusMm = reqRadius },
            // 052851 那份报告三关用的：new SolverOptions { FineMm = navCase.MeshFineMm, FineRadiusMm = navCase.MeshFineRadiusMm }
            MeshKind.Gate => new SolverOptions { FineMm = navCase.MeshFineMm, FineRadiusMm = navCase.MeshFineRadiusMm },
            // 判决网格 = 与交付判定同一张
            _ => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = reqFine, FineRadiusMm = reqRadius },
        };
    }

    internal static string MeshTag(MeshKind k) => k switch
    {
        MeshKind.Nav => "导航网格（求解器第一遍：细区 2.0 mm／细区半径 59.0）",
        MeshKind.Gate => "三关网格（052851 报告 ② 那一张：细区 2.0 mm／半径 50.0）",
        _ => "判决网格（RequiredMeshFor：细区 1.000 mm／半径 59.0）",
    };

    internal sealed class Row
    {
        public double Tab3;
        public string MeshName = "", TolName = "";
        public bool Converged, Threw, OkField;
        public string ThrewWhat = "";
        public int Rounds;
        public double TolUsed, RemainK, Sec;
        public double Cold = double.NaN, Hot = double.NaN, Flux = double.NaN;
        public double P3Cold = double.NaN, P3Hot = double.NaN, P3Flux = double.NaN;
        public double P3TMaxC = double.NaN, P3TTabEndC = double.NaN, P3QFromTubeW = double.NaN;
        public double P3QGenW = double.NaN, P3QLossW = double.NaN, P3QClampW = double.NaN;
        public double P3StabMargin = double.NaN;
        public double MinHardMarginK = double.NaN;
        public int Cells;
        public string Verdict = "";
        public string[] Notes = Array.Empty<string>();
        public List<string> Trace = new();
    }

    internal static double Val(LineResult r, string key)
    {
        var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
        return c is null || c.Undetermined ? double.NaN : c.Actual;
    }

    /// <summary>判读（跑前写死）：四态。依据取生产自己的判词，不另抄收缩判别。</summary>
    internal static string Classify(Row w)
    {
        if (w.Threw) return "**几何或场坏**（场解抛异常）";
        if (!w.OkField) return "**几何或场坏**（场解回报失败/熔化）";
        if (w.Converged)
        {
            bool nan = double.IsNaN(w.Cold) || double.IsNaN(w.Hot) || double.IsNaN(w.Flux);
            return nan ? "**几何或场坏**（收敛但交付判据出现判不了/NaN）" : "收敛";
        }
        bool shrinking = w.Notes.Any(s => s.Contains("仍在单调收缩", StringComparison.Ordinal));
        bool floorNote = w.Notes.Any(s => s.Contains("分辨率地板", StringComparison.Ordinal));
        bool notShrinking = w.Notes.Any(s => s.Contains("没有在收缩", StringComparison.Ordinal));
        if (shrinking || floorNote) return "**停摆**（轮数/容差问题）";
        if (notShrinking) return "**发散**";
        return "**未收敛，生产判词没给收缩判别** —— 判不了是哪一种";
    }

    internal static Row RunPoint(double tab3, MeshKind mesh, bool prodTol, int rounds, TimeSpan cap,
                                 R48LSolveOrderTests.LiveProbe probe, bool checkRamp = false)
    {
        var d = StopDesign(tab3);
        var opt = MeshOptions(mesh, d);
        var p = new DesignInputs();
        var w = new Row
        {
            Tab3 = tab3,
            MeshName = MeshTag(mesh),
            TolName = prodTol ? "生产默认（按判据裕度收紧）" : "关掉按裕度收紧（常数上限）",
        };

        // EvalRaw 那一份：BuildCase → ApplyCaseMesh → LineRunner.Run（探针不另写网格配方）
        var lc = d.BuildCase(p, checkRamp: checkRamp);
        Solver.ApplyCaseMesh(lc, opt);
        if (!prodTol) lc.CoupleTolFromMargin = false;
        if (rounds > 0) lc.CoupleMaxRounds = rounds;

        var tr = w.Trace;
        lc.CoupleTrace = (n, res, delta, resK, remain, amp, omega, aa) =>
        {
            if (n <= 12 || n % 10 == 0 || n >= lc.CoupleMaxRounds - 3)
                tr.Add($"轮{n,4}　步长 {delta:0.000000}　真残差 {resK:0.000000}　剩余估计 {remain:0.000000}"
                     + $"　放大 {amp:0.0}　ω {omega:0.00}"
                     + $"　片3管根 {(res.Segments.Length > 2 ? res.Segments[^1].TRootBC.ToString("0.000") : "—")}");
        };

        probe.Report($"── 开跑：片3 舌保温 {tab3:0.##########} mm｜{w.MeshName}｜{w.TolName}｜轮数上限 {lc.CoupleMaxRounds}");
        var sw = Stopwatch.StartNew();
        LineResult r;
        using (var cts = new CancellationTokenSource(cap))
        {
            try { r = LineRunner.Run(lc, probe, cts.Token); }
            catch (OperationCanceledException)
            { r = new LineResult { Ok = false, Message = $"被时间闸（{cap.TotalMinutes:0} 分钟）切断" }; w.Threw = true; w.ThrewWhat = "时间闸切断"; }
            catch (Exception ex)
            { r = new LineResult { Ok = false, Message = ex.Message }; w.Threw = true; w.ThrewWhat = $"{ex.GetType().Name}：{ex.Message}"; }
        }
        sw.Stop();
        w.Sec = sw.Elapsed.TotalSeconds;
        w.Converged = r.Converged; w.OkField = r.Ok;
        w.Rounds = r.CoupleRounds; w.TolUsed = r.CoupleTolKUsed; w.RemainK = r.CoupleRemainK;
        w.Cells = r.MeshCells;
        w.Cold = Val(r, LineResult.Key.ColdUnderTc);
        w.Hot = Val(r, LineResult.Key.HotOverTc);
        w.Flux = Val(r, LineResult.Key.NetFlux);
        double coldMax = lc.ColdUnderTcMaxK, hotMax = lc.HotOverTcMaxK;
        // 逐片裕度只走 Solver.PlateSlack（全仓唯一读口）
        w.P3Cold = Solver.PlateSlack(r, LineResult.Key.ColdUnderTc, 3, coldMax, hotMax);
        w.P3Hot = Solver.PlateSlack(r, LineResult.Key.HotOverTc, 3, coldMax, hotMax);
        w.P3Flux = Solver.PlateSlack(r, LineResult.Key.NetFlux, 3, coldMax, hotMax);
        if (r.Flanges.Length > 3)
        {
            var f = r.Flanges[3];
            w.P3TMaxC = f.TMaxC; w.P3TTabEndC = f.TTabEndC; w.P3QFromTubeW = f.QFromTubeW;
            w.P3QGenW = f.QGenW; w.P3QLossW = f.QLossW; w.P3QClampW = f.QClampW;
            w.P3StabMargin = f.LocalStabMargin;
        }
        w.MinHardMarginK = r.Checks
            .Where(c => c.Kind == CheckKind.HardSafety && c.Unit == "K" && !c.Undetermined && !double.IsNaN(c.Actual))
            .Select(c => Math.Abs(c.Limit - c.Actual))
            .DefaultIfEmpty(double.NaN).Min();
        w.Notes = r.Notes.ToArray();
        w.Verdict = Classify(w);
        probe.Report($"── 算完：片3 {tab3:0.####} ⇒ {w.Verdict}　{w.Sec:0} s　{w.Rounds} 轮　容差 {w.TolUsed:0.0000}　剩余 {w.RemainK:0.000}");
        return w;
    }

    internal static string F(double v, string fmt = "0.###") => double.IsNaN(v) ? "—"
        : double.IsNegativeInfinity(v) ? "−∞" : double.IsPositiveInfinity(v) ? "+∞" : v.ToString(fmt, CultureInfo.InvariantCulture);
}

public sealed class R48UInsulNonConvergeProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48UInsulNonConvergeProbeTests(ITestOutputHelper o) { _o = o; }

    private static string Env(string k, string dflt)
    { var v = Environment.GetEnvironmentVariable(k); return string.IsNullOrWhiteSpace(v) ? dflt : v.Trim(); }

    [Fact(DisplayName = "R48U 探针：片3 舌保温不收敛点 —— 复现与三态判读")]
    [Trait("速度", "慢")]
    public void 片3舌保温不收敛点_复现与三态判读()
    {
        var mids = R48UNonConvKit.BisectMidpoints(9);
        string ptsRaw = Env("R48U_PROBE_PTS", "BIS9");
        var kind = Env("R48U_PROBE_MESH", "nav").ToLowerInvariant() switch
        {
            "judge" => R48UNonConvKit.MeshKind.Judge,
            "gate" => R48UNonConvKit.MeshKind.Gate,
            _ => R48UNonConvKit.MeshKind.Nav,
        };
        bool prodTol = Env("R48U_PROBE_TOL", "prod").ToLowerInvariant() != "const";
        int rounds = int.TryParse(Env("R48U_PROBE_ROUNDS", "0"), out var rr) ? rr : 0;
        double capMin = double.TryParse(Env("R48U_PROBE_CAPMIN", "90"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) ? cm : 90;
        string stamp = Env("R48U_PROBE_STAMP", DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        // checkRamp：求解器二分那条路（EvalRaw）传 false；052851 报告 ② 那一格走的是 BuildCase 的默认 true。
        bool rampChk = Env("R48U_PROBE_RAMPCHK", "0") == "1";

        var pts = new List<double>();
        foreach (var tok in ptsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tok.Equals("BIS9", StringComparison.OrdinalIgnoreCase)) pts.Add(mids[8]);
            else if (tok.Equals("BIS8", StringComparison.OrdinalIgnoreCase)) pts.Add(mids[7]);
            else if (tok.Equals("BIS7", StringComparison.OrdinalIgnoreCase)) pts.Add(mids[6]);
            else pts.Add(double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_U_不收敛点诊断_W08片3舌保温_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_U_不收敛点诊断_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));

        bool fresh = !File.Exists(file);
        if (fresh)
        {
            W("R48 U 路　**不收敛点诊断**：W08（圆盘保温 10 mm）片3 舌保温 13.036 处「场解不出来 ⇒ 二分中止」");
            W($"开跑 {stamp}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
            W("⚠ 本报告可分多次开跑续写（每个点算完立刻落盘）；每段都带它自己的开始时刻。");
            W("");
            W("═══════ 被诊断的那一跑 ═══════");
            W($"出处 {R48UNonConvKit.Source}");
            W("那一跑的原话：**片3 舌保温 在 13.036 处解不出来 ⇒ 二分中止**（外层耦合未收敛；上界存疑）；");
            W("整跑一句话：「W08（圆盘 10 mm、舌保温格 0.5）：**没有可行点**」。");
            W("");
            W("═══════ 二分轨迹 replay（纯中点法，Solver.NextBisectPoint；起点 lo = 裸舌 0.30、hi = 上界 20.0）═══════");
            for (int i = 0; i < mids.Length; i++)
                W($"　第 {i + 1} 个中点　{mids[i]:R}");
            W($"　⇒ 第 9 个中点 = {mids[8]:R}（报告印成 13.036）；它两侧的括号端点 = "
              + $"{mids[7]:R}（第 8 个，**不过**侧）与 {mids[6]:R}（第 7 个，**过**侧）。");
            W("　旁证：r48_L 于 2026-09-18 修「二分中止退回值」时注释里写的被抬过的 lo 正是 12.997265625 —— 与第 8 个中点逐位相同。");
            W("　⇒ **前八个中点都解出来了**（否则轨迹在更早的点就中止）。失败的是夹在 12.997265625 与 13.07421875 之间的那一个。");
            W("");
            W("═══════ 判读（跑前写死，跑完不挪）═══════");
            W("　收敛      = LineResult.Converged == true");
            W("　**停摆**  = 未收敛，且生产判词（LineResult.Notes）说「残差仍在单调收缩」或给出「分辨率地板」那一条");
            W("　**发散**  = 未收敛，且生产判词说「残差没有在收缩」，且没有分辨率地板那一条");
            W("　**几何或场坏** = 场解抛异常／r.Ok 为假／收敛但交付判据出现判不了或 NaN");
            W("　⚠ 判读依据取生产自己的判词，探针不另抄一份收缩判别；「判不了」既不算过也不算不过。");
            W("");
            W("═══════ 复现所用的设计（照 052851 的停机态逐项复原）═══════");
            var d0 = R48UNonConvKit.StopDesign(0.30);
            W($"板厚 {string.Join("/", d0.TabThickMm.Select(v => v.ToString("0.00")))} mm　"
              + $"舌保温 6.0/3.0/4.5/**变量** mm　环倍率 {string.Join("/", d0.RingMul.Select(v => v.ToString("0.00")))}　"
              + $"舌片厚 {string.Join("/", d0.TongueThickMm.Select(v => v.ToString("0.00")))} mm　"
              + $"槽张角 0/0/0/0°　舌孔 R 0/0/0/0");
            W($"圆盘保温 {d0.FlangeInsulMm:0.#} mm（= WrapLimits.JointZoneMaxMm）　盘半径 {d0.DiscRadiusMm:0.#} mm　"
              + $"管壁 {d0.WallMm:0.00} mm　舌长 {d0.TabLengthMm:0.#}／舌半宽 {d0.TabHalfWidthMm:0.#} mm");
            W("⚠ 盘半径 30 固定 —— 另一路在修的「盘半径 30.75～31.0 的网格病」够不到本探针。");
            W("");
            Flush(); sb.Clear();
        }

        W("");
        W($"═══════ 本段开跑于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　网格 {R48UNonConvKit.MeshTag(kind)}　"
          + $"停机容差口径 {(prodTol ? "生产默认（按判据裕度收紧）" : "**关掉**按裕度收紧（常数上限）")}　"
          + $"轮数上限 {(rounds > 0 ? rounds.ToString() : "生产默认 600")}　时间闸 {capMin:0} 分钟"
          + $"　BuildCase checkRamp = {rampChk}（求解器二分那条路 EvalRaw 传 false；报告 ② 那一格是默认 true）═══════");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush(); sb.Clear();

        var rows = new List<R48UNonConvKit.Row>();
        foreach (double t in pts)
        {
            var w = R48UNonConvKit.RunPoint(t, kind, prodTol, rounds, TimeSpan.FromMinutes(capMin), probe, rampChk);
            rows.Add(w);
            W($"── 片3 舌保温 **{t:R}** mm（印成 {t:0.000}）　⇒ **{w.Verdict}**");
            W($"　耗时 {w.Sec:0} s／外层 {w.Rounds} 轮／实际容差 {R48UNonConvKit.F(w.TolUsed, "0.00000")} K／"
              + $"剩余误差估计 {R48UNonConvKit.F(w.RemainK, "0.0000")} K／单元 {w.Cells}"
              + (w.Threw ? $"／**{w.ThrewWhat}**" : ""));
            W($"　整线判据：最热铂 {R48UNonConvKit.F(w.Hot)} K／管根 {R48UNonConvKit.F(w.Cold)} K／净流入 {R48UNonConvKit.F(w.Flux)} W"
              + $"　最小硬安全线温度裕度（绝对距离）{R48UNonConvKit.F(w.MinHardMarginK, "0.0000")} K");
            W($"　片3 逐片裕度（Solver.PlateSlack）：管根 {R48UNonConvKit.F(w.P3Cold, "0.0000")}／"
              + $"最热铂 {R48UNonConvKit.F(w.P3Hot, "0.0000")}／净流入 {R48UNonConvKit.F(w.P3Flux, "0.0000")} W");
            W($"　片3 热平衡：最高温 {R48UNonConvKit.F(w.P3TMaxC, "0.0")} °C／舌端 {R48UNonConvKit.F(w.P3TTabEndC, "0.0")} °C／"
              + $"发热 {R48UNonConvKit.F(w.P3QGenW, "0.00")} W／表面散热 {R48UNonConvKit.F(w.P3QLossW, "0.00")} W／"
              + $"进铜排 {R48UNonConvKit.F(w.P3QClampW, "0.00")} W／从管抽热 {R48UNonConvKit.F(w.P3QFromTubeW, "0.00")} W／"
              + $"局部热稳定裕度 {R48UNonConvKit.F(w.P3StabMargin, "0.000")} 倍（<1 即热失控）");
            if (w.Trace.Count > 0)
            {
                W("　外层耦合逐轮（前 12 轮 + 每 10 轮 + 末 3 轮）：");
                foreach (var s in w.Trace) W("　　" + s);
            }
            if (w.Notes.Length > 0)
            {
                W("　生产判词（LineResult.Notes 原文）：");
                foreach (var s in w.Notes) W("　　" + s);
            }
            W("");
            Flush(); sb.Clear();       // ★ 每个点算完立刻落盘
        }

        W("── 本段小结");
        W("片3 舌保温 mm\t判读\t轮数\t容差 K\t剩余 K\t耗时 s\t最热铂 K\t管根 K\t净流入 W\t片3 管根裕度");
        foreach (var w in rows)
            W($"{w.Tab3:0.######}\t{w.Verdict}\t{w.Rounds}\t{R48UNonConvKit.F(w.TolUsed, "0.00000")}\t"
              + $"{R48UNonConvKit.F(w.RemainK, "0.0000")}\t{w.Sec:0}\t{R48UNonConvKit.F(w.Hot)}\t"
              + $"{R48UNonConvKit.F(w.Cold)}\t{R48UNonConvKit.F(w.Flux)}\t{R48UNonConvKit.F(w.P3Cold, "0.0000")}");
        W($"── 本段结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（探针不另写）；逐片裕度 = Solver.PlateSlack；"
          + "二分点 = Solver.NextBisectPoint replay；停机容差 = LineRunner.CoupleTolKFor（结果里的 CoupleTolKUsed）。");
        Flush(); sb.Clear();
        _o.WriteLine($"报告：{file}");
        Assert.NotEmpty(rows);
    }
}
