using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  P2 决 99 探针（只在工作树 wtP2，不合入；慢；只量不判）：局部热稳定热点真位置与细区半径代价。
//  每个算例（W08／W06 现役设计 × 判决／导航网格）：
//   ① 现役细区半径（MeshVerify.RequiredMeshFor）整线解 → 逐片按整线最终输出（片电流 CurrentA、管根 TRootC）重建同一片热解
//      （PlateMeshAnalytic → PlateCurrentField → PlateThermalInputs → SolvePlateThermal，与 RunOnce 同一组公开函数），
//      打开 ShellThermal.ProbeAllCellsLocalStab 钩子：候选两区**全部**格按生产同一 insMm／LatLen／LocalStability.Check 精算；
//      先核重建解报出的 LocalStabMargin／LocalStabRMm 与整线输出逐位相同（不同则写明，数不作数）。
//   ② 细区半径 = MeshAdapt.RequiredFineRadiusMm({各片 盘峰 r、舌峰 r、全格真最小裕度格 r}, 孔半径)（= 最远 + PeakMarginMm）→ 再解一次，同一探针；
//   ③ 细区半径 = 53.7（编排给的 C4′ 初值，未核；本树只换半径，C4′ 其余改动不在本树）→ 再解一次，同一探针。
//  三条硬判据（最热铂高出热偶读数／管根低于热偶读数／管孔净流入）逐次印出，对 ① 作差，只印。
// ════════════════════════════════════════════════════════════════════════════
public class ZZP2LocalStabTests
{
    private readonly ITestOutputHelper _o;
    public ZZP2LocalStabTests(ITestOutputHelper o) => _o = o;
    const string Sign = "2026-09-23，P2 决 99 探针（wtP2，基于 d6ae1f6，不合入）";
    const double C4Init = 53.7;
    static readonly string[] HardKeys = { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux };

    sealed class PlateProbe
    {
        public string Name = ""; public int Cells; public bool Repro; public string ReproNote = "";
        public double RepMargin, RepR, RepT, RepJ, RepL; public bool RepTab;
        public double TrueMargin = double.NaN, TrueR = double.NaN, TrueX, TrueZ, TrueT, TrueJ, TrueThick, TrueL, TrueIns, TrueProxy;
        public int TrueZone, TrueRankInZone, ZoneSize, NBelowRep, NEval, NNaN; public bool TrueInTop;
        public double DiscR, TabR; public string Rings = "";
        public List<string> CellRows = new();
    }

    static double V(LineResult r, string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;

    static LineCase MakeCase(string which, bool judge, double radius, out DesignSpec d, out double fineReq, out double radiusReq)
    {
        var d0 = R48NMeshGateTests.Design(which);
        (fineReq, _) = MeshVerify.RequiredMeshFor(d0);
        d = d0.Clone();
        var p = new DesignInputs();
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        (_, radiusReq) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p);
        double rad = double.IsNaN(radius) ? radiusReq : radius;
        Solver.ApplyCaseMesh(lc, judge ? new SolverOptions { FineMm = fineReq, FineRadiusMm = rad }
                                       : new SolverOptions { FineMm = 0, FineRadiusMm = rad });
        return lc;
    }

    static PlateProbe Probe(LineCase lc, LineResult r, int j, bool dumpCells)
    {
        var f = r.Flanges[j];
        var mesh = LineRunner.PlateMeshAnalytic(lc, j);
        var sc = LineRunner.PlateCurrentField(lc, mesh, j, f.CurrentA);
        var ts = LineRunner.PlateThermalInputs(lc, j, f.CurrentA, null);
        ShellThermalResult th;
        ShellThermal.ProbeAllCellsLocalStab = true;
        try { th = LineRunner.SolvePlateThermal(mesh, sc.HeatJAPerMm2, f.TRootC, ts, jLocalAPerMm2: sc.JMagAPerMm2); }
        finally { ShellThermal.ProbeAllCellsLocalStab = false; }
        var q = new PlateProbe { Name = f.Name, Cells = mesh.CellCount, RepMargin = f.LocalStabMargin, RepR = f.LocalStabRMm,
                                 DiscR = f.DiscMaxRMm, TabR = f.TabMaxRMm };
        bool same(double a, double b) => a.Equals(b) || (double.IsNaN(a) && double.IsNaN(b));
        q.Repro = same(th.LocalStabMargin, f.LocalStabMargin) && same(th.LocalStabRMm, f.LocalStabRMm)
               && same(th.DiscMaxRMm, f.DiscMaxRMm) && same(th.TabMaxRMm, f.TabMaxRMm);
        if (!q.Repro) q.ReproNote = $"重建 裕度 {th.LocalStabMargin:R} r {th.LocalStabRMm:R} 盘峰r {th.DiscMaxRMm:R} 舌峰r {th.TabMaxRMm:R} ≠ 整线 {f.LocalStabMargin:R} {f.LocalStabRMm:R} {f.DiscMaxRMm:R} {f.TabMaxRMm:R}";
        q.RepT = th.LocalStabTempC; q.RepJ = th.LocalStabJAPerMm2; q.RepL = th.LocalStabLatLenMm; q.RepTab = th.LocalStabOnTab;
        var M = th.ProbeLsMargin!; int n = mesh.CellCount; int bi = -1;
        for (int i = 0; i < n; i++)
        {
            if (th.ProbeLsZone![i] < 0) continue;
            q.NEval++;
            if (double.IsNaN(M[i])) { q.NNaN++; continue; }
            if (M[i] < q.RepMargin) q.NBelowRep++;
            if (bi < 0 || M[i] < M[bi]) bi = i;
        }
        double R(int i) => Math.Sqrt(mesh.Centroid[i].X * mesh.Centroid[i].X + mesh.Centroid[i].Z * mesh.Centroid[i].Z);
        if (bi >= 0)
        {
            q.TrueMargin = M[bi]; q.TrueR = R(bi); q.TrueX = mesh.Centroid[bi].X; q.TrueZ = mesh.Centroid[bi].Z;
            q.TrueT = th.T[bi]; q.TrueJ = sc.JMagAPerMm2[bi]; q.TrueThick = mesh.Thickness[bi]; q.TrueL = th.ProbeLsLatLen![bi];
            q.TrueIns = th.ProbeLsInsMm![bi]; q.TrueProxy = th.ProbeLsProxy![bi]; q.TrueZone = th.ProbeLsZone![bi]; q.TrueInTop = th.ProbeLsInTop![bi];
            int z = q.TrueZone;
            q.ZoneSize = Enumerable.Range(0, n).Count(i => th.ProbeLsZone[i] == z);
            q.TrueRankInZone = 1 + Enumerable.Range(0, n).Count(i => th.ProbeLsZone[i] == z && th.ProbeLsProxy[i] > q.TrueProxy);
        }
        // 按 r 分 2 mm 环：每环最小裕度与该格 L、proxy 排名（只印到 r ≤ 最大 r）
        var sb = new StringBuilder();
        var byRing = Enumerable.Range(0, n).Where(i => th.ProbeLsZone![i] >= 0 && !double.IsNaN(M[i]))
                               .GroupBy(i => (int)Math.Floor(R(i) / 2.0)).OrderBy(g => g.Key);
        foreach (var g in byRing)
        {
            int k = g.OrderBy(i => M[i]).First();
            sb.Append(CultureInfo.InvariantCulture, $"[{g.Key * 2},{g.Key * 2 + 2}) {M[k]:0.000}/L{th.ProbeLsLatLen![k]:0.0}/区{th.ProbeLsZone![k]}{(th.ProbeLsInTop![k] ? "/前12" : "")}　");
        }
        q.Rings = sb.ToString();
        if (dumpCells)
            for (int i = 0; i < n; i++)
                if (th.ProbeLsZone![i] >= 0)
                    q.CellRows.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{i}\t{mesh.Centroid[i].X:0.###}\t{mesh.Centroid[i].Z:0.###}\t{R(i):0.###}\t{th.T[i]:0.###}\t{sc.JMagAPerMm2[i]:0.#####}\t{mesh.Thickness[i]:0.####}\t{th.ProbeLsInsMm![i]:0.##}\t{th.ProbeLsLatLen![i]:0.###}\t{th.ProbeLsProxy![i]:0.####E0}\t{th.ProbeLsZone[i]}\t{(th.ProbeLsInTop![i] ? 1 : 0)}\t{M[i]:0.#####}"));
        return q;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 决99_全格局部热稳定真最小_报出值并列_细区半径代价_W08W06判决导航()
    {
        string file = DeliverableOut.Stamped("R48_决99_局部热稳定全格探针_W08W06判决导航.txt");
        string dir = DeliverableOut.StampedDir("R48_决99_局部热稳定热点真位置与代价_逐格");
        var sb = new StringBuilder(); object gate = new();
        var swAll = Stopwatch.StartNew();
        void W(string t = "") { lock (gate) { sb.AppendLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } }
        int par = int.TryParse(Environment.GetEnvironmentVariable("PT_P2_PAR"), out int pp) ? pp : 4;
        string only = Environment.GetEnvironmentVariable("PT_P2_ONLY") ?? "";
        W("决 99 探针：局部热稳定全格真最小裕度 × 现役报出值 × 细区半径代价（只量不判）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　基于 d6ae1f6 ＋ 探针钩子（ShellThermal.ProbeAllCellsLocalStab，缺省关、生产逐位不变）　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　并发 {par}　同机另有子代理长跑（耗时是争用下量得）　{Sign}");
        W("口径：现役报出值 = 整线输出 FlangeOut.LocalStabMargin／LocalStabRMm（前 12 名预筛）；全格真最小 = 候选两区全部格（与预筛同一候选集合、同一 insMm、同一 LatLen、同一 LocalStability.Check）的最小裕度。");
        W("细区半径需求 = MeshAdapt.RequiredFineRadiusMm({各片 盘峰 r, 舌峰 r, 全格真最小格 r}, 孔半径)，即最远 + PeakMarginMm(10)。53.7 = 编排给出的 C4′ 初值（未核），本树只换细区半径。");
        var jobs = new List<(string which, bool judge)>();
        foreach (var w in new[] { "W08", "W06" }) foreach (bool jg in new[] { true, false })
            if (only.Length == 0 || only.Contains($"{w}{(jg ? "判决" : "导航")}", StringComparison.Ordinal)) jobs.Add((w, jg));
        var summary = new List<string>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = par }, job =>
        {
            string tag = $"{job.which}{(job.judge ? "判决" : "导航")}";
            double[]? baseHard = null;
            var radii = new List<(string lab, double rad)> { ("现役", double.NaN) };
            for (int step = 0; step < radii.Count; step++)
            {
                var (lab, radIn) = radii[step];
                var sw = Stopwatch.StartNew();
                var lc = MakeCase(job.which, job.judge, radIn, out var d, out double fineReq, out double radReq);
                double rad = lc.MeshFineRadiusMm;
                LineResult r;
                try { r = LineRunner.Run(lc); }
                catch (Exception ex) { W($"{tag}\t{lab}\t细区半径 {rad:0.00}\t**抛异常：{ex.GetType().Name} {ex.Message}**"); break; }
                double sec = sw.Elapsed.TotalSeconds;
                var hard = HardKeys.Select(k => V(r, k)).ToArray();
                if (step == 0) baseHard = hard;
                var sw2 = Stopwatch.StartNew();
                var probes = new List<PlateProbe>();
                for (int j = 0; j < r.Flanges.Length; j++) probes.Add(Probe(lc, r, j, dumpCells: step == 0));
                double secProbe = sw2.Elapsed.TotalSeconds;
                int cellsAll = probes.Sum(p => p.Cells);
                var s = new StringBuilder();
                s.AppendLine($"══ {tag}　{lab}　细区半径 {rad:0.00} mm（现役需求 {radReq:0.00}）　细区尺寸 {lc.MeshFineMm:0.000} mm　粗区 {lc.MeshCoarseMm:0.000}　内带 {lc.MeshInnerMm:0.000}/{lc.MeshInnerRadiusMm:0.00}　"
                    + $"整线 Ok={r.Ok} Converged={r.Converged}　片0 单元 {r.MeshCells}，四片合计 {cellsAll}　整线解耗时 {sec:0} s（争用下）　探针 {secProbe:0} s");
                if (!r.Ok) s.AppendLine($"　整线说明：{r.Message}");
                s.AppendLine($"　三条硬判据：最热铂高出热偶读数 {hard[0]:0.0000} K　管根低于热偶读数 {hard[1]:0.0000} K　管孔净流入 {hard[2]:0.0000} W"
                    + (step == 0 ? "" : $"　对现役：{hard[0] - baseHard![0]:+0.0000;-0.0000;0}／{hard[1] - baseHard[1]:+0.0000;-0.0000;0}／{hard[2] - baseHard[2]:+0.0000;-0.0000;0}"));
                s.AppendLine("　片\t单元\t重建逐位\t报出裕度\t报出 r\t报出 T\t报出 J\t报出 L\t报出区\t真最小裕度\t真 r\t真 x\t真 z\t真 T\t真 J\t真 厚\t真 L\t真 保温\t真 proxy\t真 所在区\t区内 proxy 名次/区格数\t在前12\t低于报出值的格数/精算格数\tNaN格\t盘峰 r\t舌峰 r\t报出/真");
                foreach (var q in probes)
                    s.AppendLine($"　{q.Name}\t{q.Cells}\t{(q.Repro ? "是" : "**否** " + q.ReproNote)}\t{q.RepMargin:0.0000}\t{q.RepR:0.000}\t{q.RepT:0.0}\t{q.RepJ:0.0000}\t{q.RepL:0.00}\t{(q.RepTab ? "舌侧" : "盘侧")}"
                        + $"\t{q.TrueMargin:0.0000}\t{q.TrueR:0.000}\t{q.TrueX:0.000}\t{q.TrueZ:0.000}\t{q.TrueT:0.0}\t{q.TrueJ:0.0000}\t{q.TrueThick:0.000}\t{q.TrueL:0.00}\t{q.TrueIns:0.0}\t{q.TrueProxy:0.000E0}\t{(q.TrueZone == 1 ? "舌侧" : "盘侧")}\t{q.TrueRankInZone}/{q.ZoneSize}\t{(q.TrueInTop ? "是" : "否")}\t{q.NBelowRep}/{q.NEval}\t{q.NNaN}\t{q.DiscR:0.000}\t{q.TabR:0.000}\t{q.RepMargin / q.TrueMargin:0.000}");
                foreach (var q in probes) s.AppendLine($"　{q.Name} 分环（2 mm）最小裕度／该格 L／区：{q.Rings}");
                double peakCur = probes.SelectMany(q => new[] { q.DiscR, q.TabR, q.RepR }).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
                double peakTrue = probes.SelectMany(q => new[] { q.DiscR, q.TabR, q.TrueR }).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
                double need = MeshAdapt.RequiredFineRadiusMm(probes.SelectMany(q => new[] { q.DiscR, q.TabR, q.TrueR }).Where(v => !double.IsNaN(v)), d.HoleRadiusMm);
                double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
                s.AppendLine($"　最远热点（现役报出口径）r {peakCur:0.000} ⇒ PeakVerdict：{MeshAdapt.PeakVerdict(peakCur, innerR, rad) ?? "（不说：盖住了）"}");
                s.AppendLine($"　最远热点（全格真最小口径）r {peakTrue:0.000} ⇒ 需要细区半径 {need:0.000} mm　PeakVerdict（本档半径）：{MeshAdapt.PeakVerdict(peakTrue, innerR, rad) ?? "（不说：盖住了）"}");
                s.AppendLine($"　板件：孔半径 {d.HoleRadiusMm:0.000}　盘半径 {d.DiscRadiusMm:0.000}　舌长 {d.TabLengthMm:0.000}　压接长 {lc.Base.BusbarClampLengthMm:0.000}");
                W(s.ToString());
                lock (gate) summary.Add($"{tag}\t{lab}\t{rad:0.00}\t{cellsAll}\t{r.MeshCells}\t{sec:0}\t{hard[0]:0.0000}\t{hard[1]:0.0000}\t{hard[2]:0.0000}\t{probes.Min(q => q.RepMargin):0.0000}\t{probes.Min(q => q.TrueMargin):0.0000}\t{peakCur:0.000}\t{peakTrue:0.000}\t{need:0.000}");
                if (step == 0)
                {
                    foreach (var q in probes)
                        File.WriteAllText(Path.Combine(dir, $"{tag}_现役_{q.Name.Replace('|', '-')}_逐格.tsv"),
                            "格\tx\tz\tr\tT\tJ重构\t厚\t保温mm\tL\tproxy\t区(0盘1舌)\t前12\t裕度\n" + string.Join("\n", q.CellRows) + "\n", new UTF8Encoding(true));
                    radii.Add(("盖真位置", need));
                    radii.Add(("C4′初值", C4Init));
                }
            }
        });
        W("══ 汇总（设计网格\t档\t细区半径\t四片单元\t片0单元\t整线耗时 s（争用下）\t最热铂高出热偶读数\t管根低于热偶读数\t管孔净流入\t报出最小裕度\t全格真最小\t最远热点 r 报出口径\t最远热点 r 真口径\t需要半径）");
        foreach (var l in summary.OrderBy(x => x, StringComparer.Ordinal)) W(l);
        W($"── 总耗时 {swAll.Elapsed.TotalMinutes:0.0} 分钟（争用下量得）　{Sign}");
        _o.WriteLine(file);
    }

    /// <summary>代价补充：① 加密阶梯（MeshVerify.AnalyticCaseFactory，h = 1／0.5／0.25）在细区半径 53.7／59／需求 下的逐片单元数（只造网格、不解）；
    /// ② W08 判决网格 59 与需求半径两次整线解**同时起跑**（并发 2，同一争用窗口）印耗时与外层耦合轮数。需求半径由环境变量 PT_P2_NEED 给（取自第一跑证据档）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 决99_代价补充_加密阶梯单元数与同窗耗时()
    {
        string file = DeliverableOut.Stamped("R48_决99_代价补充_加密阶梯单元数与同窗耗时.txt");
        var sb = new StringBuilder(); object gate = new();
        void W(string t = "") { lock (gate) { sb.AppendLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } }
        double need = double.Parse(Environment.GetEnvironmentVariable("PT_P2_NEED") ?? "76.412", CultureInfo.InvariantCulture);
        W("决 99 代价补充（只量不判）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　同机另有子代理长跑　需求半径 {need:0.000}（取自 R48_决99_局部热稳定全格探针_W08W06判决导航_本次开跑于2026-09-23_192317.txt）　{Sign}");
        W("① 加密阶梯单元数：MeshVerify.AnalyticCaseFactory(d, p, 细区半径, 内带半径 = MeshAdapt.InnerRadiusFor(孔半径, max(舌厚, 管壁)))，h = 1／0.5／0.25；逐片 LineRunner.PlateMeshAnalytic 数格（阶梯上限按片0 单元 > 40000 停，MeshVerify.cs:591）");
        W("设计\th\t细区半径\t片0\t四片合计");
        foreach (var which in new[] { "W08", "W06" })
        {
            var d = R48NMeshGateTests.Design(which).Clone();
            var p = new DesignInputs();
            Solver.ApplySectionFloor(d, p, new SolverOptions(), new SolverResult { Design = d }, null, null);
            double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
            foreach (double h in new[] { 1.0, 0.5, 0.25 })
                foreach (double rad in new[] { C4Init, 59.0, need })
                {
                    var lc = MeshVerify.AnalyticCaseFactory(d, p, rad, innerR)(h, 0);
                    int n = lc.FlangeCount;
                    var cells = Enumerable.Range(0, n).Select(j => LineRunner.PlateMeshAnalytic(lc, j).CellCount).ToArray();
                    W($"{which}\t{h:0.00}\t{rad:0.000}\t{cells[0]}\t{cells.Sum()}");
                }
        }
        W();
        W("② W08 判决网格（与第一跑同一搭法）细区半径 59 与需求半径同时起跑（并发 2）：");
        var rows = new (double rad, double sec, int rounds, int cells, double[] hard)[2];
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, k =>
        {
            double rad = k == 0 ? double.NaN : need;
            var lc = MakeCase("W08", true, rad, out _, out _, out _);
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            rows[k] = (lc.MeshFineRadiusMm, sw.Elapsed.TotalSeconds, r.CoupleRounds, r.MeshCells, HardKeys.Select(q => V(r, q)).ToArray());
        });
        W("细区半径\t片0单元\t整线解耗时 s（争用下）\t外层耦合轮数\t最热铂高出热偶读数\t管根低于热偶读数\t管孔净流入");
        foreach (var x in rows) W($"{x.rad:0.000}\t{x.cells}\t{x.sec:0}\t{x.rounds}\t{x.hard[0]:0.0000}\t{x.hard[1]:0.0000}\t{x.hard[2]:0.0000}");
        W($"耗时比（需求／59）{rows[1].sec / rows[0].sec:0.000}　单元比 {(double)rows[1].cells / rows[0].cells:0.000}　{Sign}");
        _o.WriteLine(file);
    }

    /// <summary>全格精算的每次热解开销：W08／W06 判决网格片 0（细区 59 与需求半径），设计电流 1214 A、管根 1150 °C 的单片热解，钩子关／开交替各 7 次取中位。钩子开 = 生产前 12 名 + 全部候选格再算一遍，差值即全格扫描的开销（推断：生产改全格后单次热解约增这么多）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 决99_全格精算单次热解开销()
    {
        string file = DeliverableOut.Stamped("R48_决99_全格精算单次热解开销.txt");
        var sb = new StringBuilder();
        double need = double.Parse(Environment.GetEnvironmentVariable("PT_P2_NEED") ?? "76.412", CultureInfo.InvariantCulture);
        sb.AppendLine("决 99 全格精算单次热解开销（只量不判）");
        sb.AppendLine($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　同机另有子代理长跑（争用下量得）　{Sign}");
        sb.AppendLine("设计网格\t细区半径\t片0单元\t候选格数\t关 中位 ms\t开 中位 ms\t开−关 ms\t开／关");
        foreach (var which in new[] { "W08", "W06" })
            foreach (double rad in new[] { double.NaN, need })
            {
                var lc = MakeCase(which, true, rad, out _, out _, out _);
                var mesh = LineRunner.PlateMeshAnalytic(lc, 0);
                var sc = LineRunner.PlateCurrentField(lc, mesh, 0, 1214.0);
                var ts = LineRunner.PlateThermalInputs(lc, 0, 1214.0, null);
                LineRunner.SolvePlateThermal(mesh, sc.HeatJAPerMm2, 1150.0, ts, jLocalAPerMm2: sc.JMagAPerMm2);   // 预热
                var off = new List<double>(); var on = new List<double>(); int nc = 0;
                for (int k = 0; k < 7; k++)
                    foreach (bool flag in new[] { false, true })
                    {
                        ShellThermal.ProbeAllCellsLocalStab = flag;
                        var sw = Stopwatch.StartNew();
                        var th = LineRunner.SolvePlateThermal(mesh, sc.HeatJAPerMm2, 1150.0, ts, jLocalAPerMm2: sc.JMagAPerMm2);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        ShellThermal.ProbeAllCellsLocalStab = false;
                        (flag ? on : off).Add(ms);
                        if (flag) nc = th.ProbeLsZone!.Count(z => z >= 0);
                    }
                double Med(List<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
                double mo = Med(off), mn = Med(on);
                sb.AppendLine($"{which}判决\t{lc.MeshFineRadiusMm:0.000}\t{mesh.CellCount}\t{nc}\t{mo:0.0}\t{mn:0.0}\t{mn - mo:+0.0;-0.0}\t{mn / mo:0.000}");
            }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
    }

    /// <summary>一次整线解里 ShellThermal.Solve 被调了几次（W08 判决网格，细区 59）：乘上单次全格扫描开销，就是「每次热解都全格精算」的整线代价（推断）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 决99_一次整线解的热解次数()
    {
        string file = DeliverableOut.Stamped("R48_决99_一次整线解的热解次数.txt");
        var lc = MakeCase("W08", true, double.NaN, out _, out _, out _);
        int c0 = ShellThermal.ProbeSolveCount;
        var sw = Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        int calls = ShellThermal.ProbeSolveCount - c0;
        File.WriteAllText(file, $"决 99 一次整线解的热解次数（只量）\n开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（结束时刻）　工作树 {HandoverDoc.Root()}　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　同机争用　{Sign}\n"
            + $"W08 判决网格 细区 {lc.MeshFineRadiusMm:0.000}　Ok={r.Ok} Converged={r.Converged}　外层耦合轮数 {r.CoupleRounds}　ShellThermal.Solve 调用 {calls} 次　整线解耗时 {sw.Elapsed.TotalSeconds:0} s（争用下）　最热铂高出热偶读数 {V(r, LineResult.Key.HotOverTc):0.0000}\n", new UTF8Encoding(true));
        _o.WriteLine(file);
    }
}
