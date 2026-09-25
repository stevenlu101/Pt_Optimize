using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// P3（2026-09-23，只量不判）：SEG 门 b 判别跑。算例与扰动方式逐行照抄 R48SegContinuousRootSlowTests 的 CostKitCase／GateFCase(20)（开 = 连续根），
// 只多两处注入：段解容差 SegPicardTolK／SegBvpTolK（只经本探针的 DesignInputs 实例，生产缺省常数不动），与 LineCase.UseAnderson（只本探针设）。
// 无断言、无阈值；门 b 本身不改。工作树外不留受跟踪改动。
[Trait("速度", "慢")]
public class ZZP3SegGateBDiscrimProbeTests
{
    private readonly ITestOutputHelper _o;
    public ZZP3SegGateBDiscrimProbeTests(ITestOutputHelper o) { _o = o; }
    static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string LoadAvg() { try { return File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Trim() : "—"; } catch { return "—"; } }

    sealed class Cfg { public string Name = ""; public double Picard, Bvp; public bool Anderson = true; }

    static DesignInputs P(Cfg g) => new DesignInputs { SegCurrentContinuousRoot = true, SegPicardTolK = g.Picard, SegBvpTolK = g.Bvp };

    static LineCase CostKitCase(Cfg g)
    {
        var d = R48MCostKit.Design(DesignSpec.W08.FlangeInsulMm);
        var mesh = R48MCostKit.Mesh(true, d);
        var lc = d.Clone().BuildCase(P(g));
        Solver.ApplyCaseMesh(lc, mesh);
        lc.MeasureJacobianAmp = true;
        lc.UseAnderson = g.Anderson;
        return lc;
    }

    static LineCase GateFCase(double disc, Cfg g)
    {
        var d0 = R48NMeshGateTests.Design("W08");
        var (reqFine, _) = MeshVerify.RequiredMeshFor(d0);
        var d = d0.Clone();
        d.FlangeInsulated = true; d.FlangeInsulMm = disc; d.DiscInsulMm = Array.Empty<double>();
        var p = P(g);
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius });
        lc.UseAnderson = g.Anderson;
        return lc;
    }

    sealed class AbortProbe : Exception { public AbortProbe(string m) : base(m) { } }

    [Fact]
    public void P3_门b判别跑_段解容差收紧与关Anderson()
    {
        string file = DeliverableOut.Stamped("R48_段电流连续根_门b判别跑.txt");
        string trDir = DeliverableOut.StampedDir("R48_段电流连续根_门b判别跑_逐轮轨迹");
        var sb = new StringBuilder(); void W(string s = "") { lock (sb) { sb.AppendLine(s); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } }
        var clk = Stopwatch.StartNew();
        // 截止：环境变量 P3_DEADLINE_MIN（分钟，自开跑起）；到点后在逐轮回调里抛出中止，已记的轮保留，该跑标「截断」。
        double deadlineMin = double.TryParse(Environment.GetEnvironmentVariable("P3_DEADLINE_MIN"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dm) ? dm : 100;
        int par = int.TryParse(Environment.GetEnvironmentVariable("P3_PAR"), out var pp) ? pp : 4;
        string only = Environment.GetEnvironmentVariable("P3_ONLY") ?? "";
        // 无 Anderson 的跑另设轮数上限（纯欠松弛 Picard 预计远多于 33 轮；本探针只要前段轨迹）：环境变量 P3_NOAA_MAXK，缺省 40。
        int noAaMaxK = int.TryParse(Environment.GetEnvironmentVariable("P3_NOAA_MAXK"), out var nk) ? nk : 40;
        W("R48 段电流连续根　门 b 判别跑（P3，只量不判）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　git HEAD {EvidenceHeader.GitHead(HandoverDoc.Root())}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(HandoverDoc.Root())}　{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　"
          + $"无 Anderson 跑轮数上限 {noAaMaxK}　并发 {par}（机器 4 核，同机另有其他子代理在跑、负载不受控，开跑时 /proc/loadavg = {LoadAvg()}：耗时只作量级，争用下量得）　截止 {deadlineMin} 分钟");
        W("生产缺省（DesignInputs 常数，本探针不改）：SegPicardTolKDefault = " + R(DesignInputs.SegPicardTolKDefault) + " K，SegBvpTolKDefault = " + R(DesignInputs.SegBvpTolKDefault) + " K，SegCurrentTolADefault = " + R(DesignInputs.SegCurrentTolADefault) + " A（电流容差本探针不动）");
        W(EvidenceHeader.ForLineCase("门 b 判别跑（现役容差 门c算例 那一跑的头）", CostKitCase(new Cfg { Picard = DesignInputs.SegPicardTolKDefault, Bvp = DesignInputs.SegBvpTolKDefault })).TrimEnd());
        W("算例：A = 门c算例（R48SegContinuousRootSlowTests.CostKitCase 照抄），B = 门f盘20判决（GateFCase(20) 照抄，= A + 多调一次 ApplySectionFloor 的 1e−6 级扰动）；段电流收尾 = 连续根（开）。");
        W("逐轮列：q = max(δ,真残差)·放大/0.025（与门 b 逐轮轨迹同一写法）；另列 qStop = max(δ,真残差)·放大/当轮容差（CoupleTolKFor，停机判定 qStop < 1 的口径）。");
        W($"逐轮轨迹目录：{trDir}");
        W();

        double d0P = DesignInputs.SegPicardTolKDefault, d0B = DesignInputs.SegBvpTolKDefault;
        var cfgs = new[]
        {
            new Cfg { Name = "a_现役容差", Picard = d0P, Bvp = d0B },
            new Cfg { Name = "b_收紧10倍", Picard = d0P / 10, Bvp = d0B / 10 },
            new Cfg { Name = "c_收紧100倍", Picard = d0P / 100, Bvp = d0B / 100 },
            new Cfg { Name = "a'_现役容差_无Anderson", Picard = d0P, Bvp = d0B, Anderson = false },
        }.Where(c => only.Length == 0 || only.Split(',').Contains(c.Name.Split('_')[0])).ToArray();
        var jobs = cfgs.SelectMany(g => new[] { (Cfg: g, Case: "A_门c算例"), (Cfg: g, Case: "B_门f盘20判决") }).ToArray();
        var done = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = par }, j =>
        {
            string key = $"{j.Cfg.Name}__{j.Case}";
            string trFile = Path.Combine(trDir, key + ".tsv");
            File.WriteAllText(trFile, "轮\tδ K\t真残差 K\t放大\tq=max(δ,r)·放大/0.025\t当轮容差 K\tqStop=max(δ,r)·放大/当轮容差\tω\tAnderson\t段电流 A（R）\t开跑后 s\n", new UTF8Encoding(true));
            var sw = Stopwatch.StartNew();
            LineCase lc = j.Case.StartsWith("A") ? CostKitCase(j.Cfg) : GateFCase(20, j.Cfg);
            string pchk = $"lc.Base.SegPicardTolK={R(lc.Base.SegPicardTolK)} SegBvpTolK={R(lc.Base.SegBvpTolK)} SegCurrentContinuousRoot={lc.Base.SegCurrentContinuousRoot} UseAnderson={lc.UseAnderson} MeasureJacobianAmp={lc.MeasureJacobianAmp}";
            W($"· 开始 {key}（开跑后 {clk.Elapsed.TotalMinutes:0.0} 分钟）：{pchk}");
            int last = 0; bool truncated = false;
            lc.CoupleTrace = (k, res, delta, resK, remain, amp, omega, aa) =>
            {
                double tol = LineRunner.CoupleTolKFor(lc, res);
                double m = Math.Max(delta, resK) * amp;
                File.AppendAllText(trFile, $"{k}\t{R(delta)}\t{R(resK)}\t{R(amp)}\t{R(m / 0.025)}\t{R(tol)}\t{R(m / tol)}\t{R(omega)}\t{aa}\t{string.Join(" ", res.Segments.Select(s => R(s.CurrentA)))}\t{sw.Elapsed.TotalSeconds:0}\n");
                last = k;
                if (!j.Cfg.Anderson && k >= noAaMaxK) { truncated = true; throw new AbortProbe($"无 Anderson 跑到轮数上限 {noAaMaxK}，第 {k} 轮后中止"); }
                if (clk.Elapsed.TotalMinutes > deadlineMin) { truncated = true; throw new AbortProbe($"到截止 {deadlineMin} 分钟，第 {k} 轮后中止"); }
            };
            LineResult r = new(); string err = "";
            try { r = LineRunner.Run(lc); } catch (Exception ex) { err = ex.GetType().Name + "：" + ex.Message; }
            if (err.Length == 0 && truncated) err = "截断（LineRunner 吞了中止异常）";
            string row = $"{key}\t{r.Converged}\t{r.CoupleRounds}\t{last}\t{sw.Elapsed.TotalSeconds:0}\t{R(r.CertErrK)}\t{R(r.ValueOf(LineResult.Key.HotOverTc))}\t{R(r.ValueOf(LineResult.Key.ColdUnderTc))}\t{R(r.ValueOf(LineResult.Key.NetFlux))}\t{r.MeshCells}\t{err}";
            done[key] = row;
            W($"· 完成 {row}（开跑后 {clk.Elapsed.TotalMinutes:0.0} 分钟）");
        });
        W();
        W("跑\t收敛\t停机轮（CoupleRounds）\t末记录轮\t耗时 s\t认证误差 K（R）\t最热铂高出热偶读数 K（R）\t管根低于热偶读数 K（R）\t管孔净流入 W（R）\t单元\t异常");
        foreach (var j in jobs) W(done.TryGetValue($"{j.Cfg.Name}__{j.Case}", out var s) ? s : $"{j.Cfg.Name}__{j.Case}\t（无结果）");
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（{clk.Elapsed.TotalMinutes:0.0} 分钟）");
        _o.WriteLine(file);
    }
}
