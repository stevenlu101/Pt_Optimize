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
//  R48 M 路慢门：**停机容差改成绝对目标之后，那两个「轮数彩票」点各自要在导航网格上 ≤120 轮收敛、认证误差 ≤0.05 K；
//  把那条病（「按裕度收紧」＋ 旧段解地板）注射回去，13.074 那一点必须 >300 轮停不下来** —— 2026-09-18，Fable 5.1
//
//  ══ 病（出处 r48_U/deliverable/R48_U_不收敛点诊断_总表_本次开跑于2026-09-18_163414.txt 与九份分段输出，只读）
//    §0.-10 把停机容差改成 min(0.1, max(0.02, 0.1 × 当前最小硬安全线裕度))。二分求的正是「裕度 = 0」的根 ⇒ 每逼近一步容差就压小一分，
//    根附近必撞下限 0.02 K；停机还要真残差 × 放大（15.9～33）过关 ⇒ 真残差要压到 6e−4 K，低于段解地板（Picard 0.01／Bvp1D 0.005
//    造成的 0.001～0.04 K 极限环）⇒ 能否在 600 轮内停下成了轮数彩票：片3 舌保温 13.036 要 98 轮、13.074 要 591 轮（上限 600）；
//    关掉按裕度收紧同两点 46／39 轮。
//
//  ══ 修（LineCase.CoupleTolK 注记）：容差 = 绝对目标（不随裕度走）；认证误差 = 放大 × 停机残差 随结果交下游。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//    (a) 生产口径：13.0357421875 与 13.07421875 两点在导航网格上各 收敛 且 轮数 ≤ 120 且 认证误差 ≤ 0.05 K；
//    注射：容差改回按裕度收紧（§0.-10 口径）＋ 旧段解地板 ⇒ 13.07421875 那一点 300 轮内停不下来（!Converged 或 轮数 > 300）—— 否则门守的是空气。
//    ⚠ 注射配方 23:12 改过一次（写明变因，见 Tol 枚举的注记）：只改回口径、地板留新的 ⇒ 24 轮就收敛 —— 病根是地板，两味要一起注射；门槛 >300 没挪。
//    另量（只量不判，写进文件给 HANDOVER 定容差用）：段解地板 = 40 轮以后真残差与步长的最小值／中位数；放大的两个来源；量雅可比的秒数。
//
//  ══ 复现所用的设计：照 r48_U 那份探针（R48UInsulNonConvergeProbeTests.StopDesign）逐项复原 ——
//    出处 r48_L/deliverable/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt §「停机时各旋钮的值，逐片」四行 + 第 1 轮场定位置。
//    ⚠ 圆盘保温写死 10 mm（那一跑就是 10 mm 档；本树默认已退回 20 mm，不许拿默认冒充那一跑）。
//  ══ 网格：导航网格 = 求解器第一遍那张（Solver.NavOptionsOf 的口径：细区尺寸不动、细区半径按 MeshVerify.RequiredMeshFor 统一）。
//  ══ 阶梯（只量不判）：R48M_TOL_LADDER=0.1,0.05,0.035,0.025 时把两点各按常数容差跑一遍，给「容差取多少」当依据。
// ════════════════════════════════════════════════════════════════════════════

internal static class R48MTwoPointKit
{
    internal const string Source =
        "r48_L/deliverable/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt §「停机时（或解出来时）各旋钮的值，逐片」四行 + §「求解轨迹」第 1 轮的场定位置；"
      + "复原写法照 r48_U/Pt_Optimize.Tests/R48UInsulNonConvergeProbeTests.cs（StopDesign）逐项抄，圆盘保温写死 10 mm";

    /// <summary>那一跑的圆盘保温档（10 mm）。本树默认已退回 20（§0.-15M），这里必须写死才是同一份设计。</summary>
    internal const double DiscInsulMmOfThatRun = 10.0;

    /// <summary>二分轨迹 replay 出来的第 9／第 7 个中点（r48_U 总表：13.0357421875 印成 13.036；13.07421875 印成 13.074）。</summary>
    internal const double Pt13036 = 13.0357421875, Pt13074 = 13.07421875;

    /// <summary>门槛（跑前写死）。</summary>
    internal const int RoundsCap = 120, InjectRoundsMin = 300;
    internal const double CertErrCapK = 0.05;

    internal static DesignSpec StopDesign(double tab3Mm)
    {
        var d = DesignSpec.W08.Clone();
        d.Name = "管壁 0.8 · 留余量（照 圆盘保温10 重解 052851 的停机态复原）";
        d.Provenance = Source;
        d.FlangeInsulated = true;
        d.FlangeInsulMm = DiscInsulMmOfThatRun;
        d.DiscInsulMm = Array.Empty<double>();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 6.0, 3.0, 4.5, tab3Mm };
        d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.RingMul2 = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.RingW1Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.RingW2Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.TongueThickMm = new[] { 2.03, 3.51, 3.51, 2.03 };
        d.SlotSpanDeg = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleRMm = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleAspect = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.SlotCenterDeg = new[] { 103.0, 106.0, 106.0, 103.0 };
        d.TabHoleXMm = new[] { -2.0, -2.0, -2.0, -2.0 };
        d.DiscCutRotDeg = new[] { -27.0, -27.0, -27.0, -27.0 };
        return d;
    }

    /// <summary>导航网格（求解器第一遍那张）：走生产的 Solver.ApplyCaseMesh，不另写配方。</summary>
    internal static SolverOptions NavMesh(DesignSpec seed)
    {
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(seed, new DesignInputs());
        return new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = 0, FineRadiusMm = reqRadius };
    }

    /// <summary>
    /// FromMarginInject = 只把容差改回按裕度（新地板）；LegacyFloorFromMargin = 按裕度 **＋ 旧段解地板**（= r48_U 那一跑的完整病）。
    /// ★ 2026-09-18 23:12 实跑（deliverable/R48_M_停机容差绝对目标_两点门_本次开跑于2026-09-18_230343.txt）：只改回按裕度、地板留新的，13.074 **24 轮就收敛**（容差撞下限 0.02、认证误差 0.015）——
    ///   彩票的根子是段解地板 × 放大，不是容差口径；地板降了之后「按裕度」也停得下来。⇒ 复现那条病得两味一起注射（LegacyFloorFromMargin）；
    ///   「只按裕度」那一行照跑照印，作为「病根在地板」的证据（只报不判）。门槛 >300 轮没挪，挪的是注射的配方（写明变因）。
    /// </summary>
    internal enum Tol { Production, FromMarginInject, LegacyFloorFromMargin, Const }

    internal sealed class Row
    {
        public double Tab3, TolSet;
        public string TolName = "";
        public bool Converged, Ok, Threw;
        public string ThrewWhat = "";
        public int Rounds, MaxRounds;
        public double TolUsed, RemainK, CertErr, AmpUsed, AmpClosed, AmpJac, JacSec, Sec;
        public double Cold = double.NaN, Hot = double.NaN, Flux = double.NaN;
        public double P3Cold = double.NaN, P3Hot = double.NaN;
        public double FloorResMin = double.NaN, FloorResMed = double.NaN, FloorStepMin = double.NaN, FloorStepMed = double.NaN;
        public int Cells;
        public string[] Notes = Array.Empty<string>();
    }

    internal static Row Run(double tab3, Tol tol, double constTol, int maxRounds, TimeSpan cap, IProgress<string> probe)
    {
        var d = StopDesign(tab3);
        var opt = NavMesh(d);
        var p = new DesignInputs();
        if (tol == Tol.LegacyFloorFromMargin)
        {   // 旧段解地板（r48_U 那一跑的地板）注射回去：电流二分 0.05 A／Picard 0.01 K／Bvp1D 0.005 K
            p.SegCurrentTolA = DesignInputs.SegCurrentTolALegacy; p.SegPicardTolK = DesignInputs.SegPicardTolKLegacy; p.SegBvpTolK = DesignInputs.SegBvpTolKLegacy;
        }
        var lc = d.BuildCase(p, checkRamp: false);
        Solver.ApplyCaseMesh(lc, opt);
        string tolName;
        switch (tol)
        {
            case Tol.FromMarginInject:
                lc.CoupleTolFromMargin = true; lc.CoupleTolK = 0.1;   // §0.-10 的生产口径：上限 0.1／下限 0.02／比例 0.1（后两个走 LineCase 默认）
                tolName = $"注射（只改口径，新地板）：按判据裕度收紧（上限 {lc.CoupleTolK:0.###}／下限 {lc.CoupleTolFloorK:0.###}／比例 {lc.CoupleTolMarginFrac:0.##}）";
                break;
            case Tol.LegacyFloorFromMargin:
                lc.CoupleTolFromMargin = true; lc.CoupleTolK = 0.1;
                tolName = $"注射（口径 + 旧地板 = r48_U 那一跑的病）：按判据裕度收紧 + 段解地板 {DesignInputs.SegCurrentTolALegacy} A／{DesignInputs.SegPicardTolKLegacy} K／{DesignInputs.SegBvpTolKLegacy} K";
                break;
            case Tol.Const:
                lc.CoupleTolFromMargin = false; lc.CoupleTolK = constTol;
                tolName = $"常数 {constTol:0.###} K（阶梯）";
                break;
            default:
                tolName = $"生产默认（绝对目标 {lc.CoupleTolK:0.###} K，不按裕度）";
                break;
        }
        if (maxRounds > 0) lc.CoupleMaxRounds = maxRounds;
        var w = new Row { Tab3 = tab3, TolName = tolName, TolSet = lc.CoupleTolK, MaxRounds = lc.CoupleMaxRounds };

        var res = new List<double>(); var steps = new List<double>();
        lc.CoupleTrace = (n, r, delta, resK, remain, amp, omega, aa) =>
        {
            if (n > 40) { res.Add(resK); steps.Add(delta); }
            if (n <= 3 || n % 20 == 0) probe.Report($"轮{n,4}　步长 {delta:0.000000}　真残差 {resK:0.000000}　剩余估计 {remain:0.000000}　放大 {amp:0.0}　ω {omega:0.00}");
        };

        probe.Report($"── 开跑：片3 舌保温 {tab3:R} mm｜{tolName}｜轮数上限 {lc.CoupleMaxRounds}");
        var sw = Stopwatch.StartNew();
        LineResult r;
        using (var cts = new CancellationTokenSource(cap))
        {
            try { r = LineRunner.Run(lc, probe, cts.Token); }
            catch (OperationCanceledException) { r = new LineResult { Ok = false, Message = $"被时间闸（{cap.TotalMinutes:0} 分钟）切断" }; w.Threw = true; w.ThrewWhat = "时间闸切断"; }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = ex.Message }; w.Threw = true; w.ThrewWhat = $"{ex.GetType().Name}：{ex.Message}"; }
        }
        sw.Stop();
        w.Sec = sw.Elapsed.TotalSeconds;
        w.Converged = r.Converged; w.Ok = r.Ok;
        w.Rounds = r.CoupleRounds; w.TolUsed = r.CoupleTolKUsed; w.RemainK = r.CoupleRemainK; w.CertErr = r.CertErrK;
        w.AmpUsed = r.CoupleAmpUsed; w.AmpClosed = r.CoupleAmpClosed; w.AmpJac = r.CoupleAmpJacobian; w.JacSec = r.JacobianAmpSec;
        w.Cells = r.MeshCells;
        w.Cold = Val(r, LineResult.Key.ColdUnderTc); w.Hot = Val(r, LineResult.Key.HotOverTc); w.Flux = Val(r, LineResult.Key.NetFlux);
        w.P3Cold = Solver.PlateSlack(r, LineResult.Key.ColdUnderTc, 3, lc.ColdUnderTcMaxK, lc.HotOverTcMaxK);
        w.P3Hot = Solver.PlateSlack(r, LineResult.Key.HotOverTc, 3, lc.ColdUnderTcMaxK, lc.HotOverTcMaxK);
        if (res.Count > 0)
        {
            w.FloorResMin = res.Min(); w.FloorResMed = Median(res);
            w.FloorStepMin = steps.Min(); w.FloorStepMed = Median(steps);
        }
        w.Notes = r.Notes.ToArray();
        probe.Report($"── 算完：片3 {tab3:0.####}｜{tolName} ⇒ 收敛 {w.Converged}　{w.Sec:0} s　{w.Rounds} 轮　容差 {w.TolUsed:0.0000}　认证误差 {w.CertErr:0.0000}　放大 {w.AmpUsed:0.0}");
        return w;
    }

    internal static double Val(LineResult r, string key)
    {
        var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
        return c is null ? double.NaN : c.Actual;   // 判不了那么细的也照印数（那是「值」不是「判词」）
    }

    private static double Median(List<double> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    internal static string F(double v, string fmt = "0.###") => double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.InvariantCulture);

    internal static string RowLine(Row w)
        => $"{w.Tab3:0.######}\t{w.TolName}\t{(w.Threw ? "**" + w.ThrewWhat + "**" : w.Converged ? "收敛" : "未收敛")}\t{w.Rounds}/{w.MaxRounds}\t{F(w.TolUsed, "0.0000")}\t{F(w.CertErr, "0.0000")}\t"
         + $"{F(w.AmpUsed, "0.0")}={F(w.AmpClosed, "0.0")}×1.1|{F(w.AmpJac, "0.0")}\t{w.JacSec:0.0}\t{w.Sec:0}\t{w.Cells}\t"
         + $"{F(w.Hot)}\t{F(w.Cold)}\t{F(w.Flux)}\t{F(w.P3Cold, "0.0000")}\t{F(w.P3Hot, "0.0000")}\t"
         + $"{F(w.FloorResMin, "0.00000")}/{F(w.FloorResMed, "0.00000")}\t{F(w.FloorStepMin, "0.00000")}/{F(w.FloorStepMed, "0.00000")}";

    internal const string Header =
        "片3 舌保温 mm\t容差口径\t判读\t轮数/上限\t实际容差 K\t认证误差 K\t放大=闭式×1.1|雅可比\t量雅可比 s\t耗时 s\t单元\t整线最热铂 K\t整线管根 K\t净流入 W\t片3管根裕度\t片3最热铂裕度\t40轮后真残差 最小/中位 K\t40轮后步长 最小/中位 K";

    internal static void Head(Action<string> W, string title, string stamp, string live)
    {
        W(title);
        W($"开跑 {stamp}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Fable 5.1");
        W("");
        W("═══════ 病 ═══════");
        W("出处 r48_U/deliverable/R48_U_不收敛点诊断_总表_本次开跑于2026-09-18_163414.txt（只读）：容差按裕度收紧 ⇒ 根附近撞下限 0.02 K ⇒ 真残差要压到 6e−4 K，");
        W("低于段解地板 ⇒ 轮数彩票：13.036 要 98 轮、13.074 要 591 轮（上限 600）；关掉按裕度收紧同两点 46／39 轮。");
        W("");
        W("═══════ 设计与网格 ═══════");
        W($"设计 = {Source}");
        W($"圆盘保温 = {DiscInsulMmOfThatRun:0.#} mm（写死 = 那一跑的档；本树默认 {DesignSpec.W08.FlangeInsulMm:0.#} mm 不用）　舌保温 6.0/3.0/4.5/**变量**　板厚 0.73/1.26/1.26/0.73");
        W("网格 = 导航网格（求解器第一遍那张：细区尺寸不动、细区半径按 MeshVerify.RequiredMeshFor 统一；配方走 Solver.ApplyCaseMesh）");
        W("放大口径 = max(闭式 × 1.1, 实测雅可比)（LineRunner.StopAmpOf，R48 M）；认证误差 = 放大 × 停机残差（LineResult.CertErrK）");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
    }
}

[Trait("速度", "慢")]
public class R48MStopTolTwoPointTests
{
    private readonly ITestOutputHelper _o;
    public R48MStopTolTwoPointTests(ITestOutputHelper o) { _o = o; }

    private static string Env(string k, string dflt)
    { var v = Environment.GetEnvironmentVariable(k); return string.IsNullOrWhiteSpace(v) ? dflt : v.Trim(); }

    /// <summary>门 (a)：两点各 ≤120 轮、认证误差 ≤0.05 K；注射改回按裕度 ⇒ 13.074 超 300 轮。</summary>
    [Fact]
    public void 门_两点在导航网格上各不超120轮且认证误差不超0点05_注射按裕度13074超300轮()
    {
        double capMin = double.TryParse(Env("R48M_TWOPT_CAPMIN", "40"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) ? cm : 40;
        string stamp = DeliverableOut.RunStamp;
        string file = DeliverableOut.Stamped("R48_M_停机容差绝对目标_两点门.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_M_两点门_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        R48MTwoPointKit.Head(W, "R48 M 路　**停机容差改成绝对目标：两个「轮数彩票」点各自 ≤120 轮、认证误差 ≤0.05 K；注射按裕度 ⇒ 13.074 超 300 轮**", stamp, live);
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W($"生产口径：两点各 收敛 且 轮数 ≤ {R48MTwoPointKit.RoundsCap} 且 认证误差 ≤ {R48MTwoPointKit.CertErrCapK:0.###} K。");
        W($"注射（把 r48_U 那一跑的病整个放回去：容差改回按判据裕度收紧 + 旧段解地板，轮数上限 {R48MTwoPointKit.InjectRoundsMin}）：13.074 那一点 未收敛 或 轮数 > {R48MTwoPointKit.InjectRoundsMin} —— 否则门守的是空气。");
        W("另跑一行「只改回按裕度、地板留新的」只报不判：2026-09-18 23:12 那一跑它 24 轮就收敛（容差撞下限 0.02）—— 彩票的根子是段解地板 × 放大，不是容差口径。");
        W("段解地板只量不判（40 轮以后真残差与步长的最小值／中位数）。");
        W("");
        Flush();

        var rows = new List<R48MTwoPointKit.Row>();
        W(R48MTwoPointKit.Header);
        foreach (double t in new[] { R48MTwoPointKit.Pt13036, R48MTwoPointKit.Pt13074 })
        {
            var w = R48MTwoPointKit.Run(t, R48MTwoPointKit.Tol.Production, double.NaN, 0, TimeSpan.FromMinutes(capMin), probe);
            rows.Add(w); W(R48MTwoPointKit.RowLine(w)); Flush();
        }
        var injOnly = R48MTwoPointKit.Run(R48MTwoPointKit.Pt13074, R48MTwoPointKit.Tol.FromMarginInject, double.NaN, R48MTwoPointKit.InjectRoundsMin, TimeSpan.FromMinutes(capMin), probe);
        W(R48MTwoPointKit.RowLine(injOnly)); Flush();
        var inj = R48MTwoPointKit.Run(R48MTwoPointKit.Pt13074, R48MTwoPointKit.Tol.LegacyFloorFromMargin, double.NaN, R48MTwoPointKit.InjectRoundsMin, TimeSpan.FromMinutes(capMin), probe);
        W(R48MTwoPointKit.RowLine(inj)); Flush();
        W("");
        W("── 生产判词原文（LineResult.Notes 里停机放大口径那一句与收敛那一句）");
        foreach (var w in rows.Append(injOnly).Append(inj))
        {
            W($"· 片3 {w.Tab3:0.####}｜{w.TolName}");
            foreach (var n in w.Notes.Where(n => n.Contains("停机放大口径") || n.Contains("轮收敛") || n.Contains("未收敛") || n.Contains("分辨率地板") || n.Contains("判不了那么细")))
                W("　" + n);
        }
        W("");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；逐片裕度 = Solver.PlateSlack；容差 = LineRunner.CoupleTolKFor；放大 = LineRunner.StopAmpOf；认证误差 = LineResult.CertErrK。");
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Flush();
        _o.WriteLine($"报告：{file}");

        foreach (var w in rows)
        {
            Assert.False(w.Threw, $"片3 {w.Tab3:0.####}：{w.ThrewWhat}");
            Assert.True(w.Converged, $"片3 {w.Tab3:0.####} 生产口径没收敛（{w.Rounds} 轮）—— 见 {file}");
            Assert.True(w.Rounds <= R48MTwoPointKit.RoundsCap, $"片3 {w.Tab3:0.####} 生产口径 {w.Rounds} 轮 > {R48MTwoPointKit.RoundsCap}");
            Assert.True(w.CertErr <= R48MTwoPointKit.CertErrCapK, $"片3 {w.Tab3:0.####} 认证误差 {w.CertErr:0.0000} K > {R48MTwoPointKit.CertErrCapK}");
        }
        Assert.False(inj.Threw, "注射那一跑：" + inj.ThrewWhat);
        Assert.True(!inj.Converged || inj.Rounds > R48MTwoPointKit.InjectRoundsMin,
            $"注射（按裕度收紧 + 旧段解地板）之后 13.074 竟然 {inj.Rounds} 轮就收敛了 —— 这道门守的是空气（那条病本树复现不出来），见 {file}");
    }

    /// <summary>阶梯（只量不判）：两点各按常数容差跑一遍，给「容差取多少」当依据（HANDOVER §0.-16M 引它）。</summary>
    [Fact]
    public void 阶梯_两点按常数容差各跑一遍_只量不判()
    {
        string ladder = Env("R48M_TOL_LADDER", "0.1,0.05,0.035,0.025");
        double capMin = double.TryParse(Env("R48M_TWOPT_CAPMIN", "40"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) ? cm : 40;
        string stamp = DeliverableOut.RunStamp;
        string file = DeliverableOut.Stamped("R48_M_停机容差阶梯_两点.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_M_阶梯_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        R48MTwoPointKit.Head(W, "R48 M 路　**停机容差阶梯：两个「轮数彩票」点按常数容差各跑一遍（只量不判，给容差取值当依据）**", stamp, live);
        W($"阶梯 = {ladder}（环境变量 R48M_TOL_LADDER）");
        W("");
        W(R48MTwoPointKit.Header);
        Flush();
        var rows = new List<R48MTwoPointKit.Row>();
        foreach (var tok in ladder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            double tol = double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
            foreach (double t in new[] { R48MTwoPointKit.Pt13036, R48MTwoPointKit.Pt13074 })
            {
                var w = R48MTwoPointKit.Run(t, R48MTwoPointKit.Tol.Const, tol, 0, TimeSpan.FromMinutes(capMin), probe);
                rows.Add(w); W(R48MTwoPointKit.RowLine(w)); Flush();
            }
        }
        W("");
        W("── 生产判词原文");
        foreach (var w in rows)
        {
            W($"· 片3 {w.Tab3:0.####}｜{w.TolName}");
            foreach (var n in w.Notes.Where(n => n.Contains("停机放大口径") || n.Contains("轮收敛") || n.Contains("未收敛") || n.Contains("分辨率地板")))
                W("　" + n);
        }
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Flush();
        _o.WriteLine($"报告：{file}");
        Assert.NotEmpty(rows);
        Assert.All(rows, w => Assert.False(w.Threw, $"片3 {w.Tab3:0.####}｜{w.TolName}：{w.ThrewWhat}"));
    }
}
