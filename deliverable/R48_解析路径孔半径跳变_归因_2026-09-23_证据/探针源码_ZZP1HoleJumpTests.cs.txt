using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  P1 慢探针（2026-09-23，只量不判，不入库）：生产解析 Build 抽热对孔半径的跳变归因。
//  两例（DrawingPathParityTests.Case：0 = 盘Ø56 片1，1 = Builtin[0] 片0；解析板），孔半径 25.5～26.5 逐 0.005（201 点）。
//  每点：建网格（支见 Variant），记格数、并格数（规则 A 管孔外角薄片／规则 B 碎格）、弧面数、孔面总长、带格数（带弧面的格）、
//  最热格（位置、温度）、抽热／发热（单片冻结热解，口径逐字照抄 DrawingPathParityTests.SolveOne，另取温度场）。
//  生产代码一个字没动；改回只经 MeshRules（internal，只供门）与 BuildFromMaterialWith 的锚点参数。
// ════════════════════════════════════════════════════════════════════════════
public class ZZP1HoleJumpTests
{
    private readonly ITestOutputHelper _o;
    public ZZP1HoleJumpTests(ITestOutputHelper o) { _o = o; }
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    static string EvidenceDir()
    {
        string d = Path.Combine(HandoverDoc.Root(), "deliverable", "R48_解析路径孔半径跳变_归因_2026-09-23_证据");
        Directory.CreateDirectory(d);
        return d;
    }

    // 支：
    //  V0 生产 Build，hFine 2（与决 98 甲表 3 同）
    //  V1 生产 Build，hFine 1（判决档细步；粗区、细区半径、内带照 lc）
    //  V2 hFine 2，x 格线平移 +0.5 mm（1/4 格：x 轴多两个锚点 +0.5／−1.5，细带里 x 节点 ≡ 0.5 (mod 2)）；生产规则
    //  V3 hFine 2，规则 A（MergeHoleCorners）关
    //  V4 hFine 2，规则 B（MergeSlivers）关
    //  V5 hFine 2，A、B 都关
    //  V6 hFine 2，x、z 格线都平移 +0.5 mm；生产规则
    //  V7 hFine 2，经 BuildFromMaterialWith（生产规则、AnchorsOf 锚点）—— 与 V0 逐位对拍的自检支
    public static readonly string[] VName = { "V0_生产hF2", "V1_生产hF1", "V2_x平移0p5", "V3_规则A关", "V4_规则B关", "V5_AB都关", "V6_xz平移0p5", "V7_自检With", "V8_hF1规则A关" };

    internal static ShellMesh MeshFor(int v, FlangePlate g, LineCase lc, double r)
    {
        double r0 = g.HoleRadiusMm; g.HoleRadiusMm = r;
        try
        {
            double hF = v == 1 || v == 8 ? 1.0 : lc.MeshFineMm;
            if (v == 0 || v == 1)
                return FlangeMesher.Build(g, 0, hF, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm,
                                          clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            var (xa, za) = FlangeMesher.AnchorsOf(g);
            var xl = new List<double>(xa); var zl = new List<double>(za);
            if (v == 2 || v == 6) { xl.Add(0.5); xl.Add(-1.5); }
            if (v == 6) { zl.Add(0.5); zl.Add(-1.5); }
            MeshRules rules = v switch
            {
                3 or 8 => new MeshRules { MergeHoleCorners = false },
                4 => new MeshRules { MergeSlivers = false },
                5 => new MeshRules { MergeHoleCorners = false, MergeSlivers = false },
                _ => MeshRules.Production,
            };
            return FlangeMesher.BuildFromMaterialWith(new AnalyticMaterial(g), g.HoleRadiusMm, rules, 0, hF, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                                      lc.MeshInnerMm, lc.MeshInnerRadiusMm, twoTabs: g.TwoTabs, xAnchors: xl, zAnchors: zl,
                                                      clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        }
        finally { g.HoleRadiusMm = r0; }
    }

    /// <summary>口径逐字照抄 DrawingPathParityTests.SolveOne（另回传温度场）。</summary>
    internal static (double QGen, double QTube, ShellThermalResult th) Solve(ShellMesh m, LineCase lc, FlangePlate g, double iA, double tRoot, double tSet, double clampC, double tabInsul)
    {
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet; p2.BusbarClampTempC = clampC;
        var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
        if (!sc.Converged) throw new InvalidOperationException("电流场没收敛");
        var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved, false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabInsul);
        if (!th.Converged) throw new InvalidOperationException("温度场没收敛");
        return (th.QGenW, th.QFromTubeW, th);
    }

    static string R(double v) => double.IsNaN(v) ? "NaN" : v.ToString("R", IC);

    [Trait("速度", "慢")]
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void 扫孔半径_25p5到26p5_步0p005(int v)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string start = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC);
        var sb = new StringBuilder();
        string file = Path.Combine(EvidenceDir(), DeliverableOut.StampedName($"扫描_{VName[v]}.tsv"));
        void W(string t) { sb.AppendLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"# P1 解析路径孔半径跳变归因 慢探针，支 {VName[v]}；开跑 {start}；Linux 镜像 net8.0（预跑，待 Windows 重录）；树 wtP1 = d6ae1f6（detached），生产代码未改；4 核机器、另有子代理同机（争用下量得）");
        foreach (int which in new[] { 0, 1 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = DrawingPathParityTests.Case(which);
            double rh = g.HoleRadiusMm;
            // 网格节点（孔附近）：取 r = rh 时的网格
            var m0 = MeshFor(v, g, lc, rh);
            var xs = m0.Nodes.Select(n => n.X).Distinct().OrderBy(x => x).ToArray();
            var zs = m0.Nodes.Select(n => n.Z).Distinct().OrderBy(z => z).ToArray();
            W($"# 例 {which} {name}（rh {R(rh)}）hFine {(v == 1 || v == 8 ? 1.0 : lc.MeshFineMm)} hCoarse {lc.MeshCoarseMm} 细区半径 {lc.MeshFineRadiusMm} 内带 {lc.MeshInnerMm}/{lc.MeshInnerRadiusMm} 压接长 {lc.Base.BusbarClampLengthMm}；iA {iA} tRoot {tRoot} tSet {tSet} clamp {clampC} tabInsul {tabInsul}");
            W($"# 例 {which} x节点(|x|<=30) " + string.Join(",", xs.Where(x => Math.Abs(x) <= 30.0001).Select(R)));
            W($"# 例 {which} z节点(|z|<=30) " + string.Join(",", zs.Where(z => Math.Abs(z) <= 30.0001).Select(R)));
            W("例\tr\tQtube\tQgen\t格数\t并格A\t并格B\t弧面数\t孔面总长\t带格数\t带格最小覆盖率\t带格最小面积\t全网最小面积\t覆盖率\t最热格T\t最热格x\t最热格z\t最热格i\tTmin\t迭代状态");
            for (int k = 0; k <= 200; k++)
            {
                double r = Math.Round(25.5 + 0.005 * k, 6);
                string row;
                try
                {
                    var m = MeshFor(v, g, lc, r);
                    var arc = m.Faces.Where(fc => fc.B < 0 && !double.IsNaN(fc.ArcRadiusMm)).ToList();
                    double holeLen = m.Faces.Where(fc => fc.B < 0 && fc.Tag == ShellMesh.TagHole).Sum(fc => fc.Length);
                    var bandCells = arc.Select(fc => fc.A).Distinct().ToArray();
                    double bandMinFrac = bandCells.Length == 0 ? double.NaN : bandCells.Min(c => m.Frac[c]);
                    double bandMinArea = bandCells.Length == 0 ? double.NaN : bandCells.Min(c => m.Area[c]);
                    double minArea = m.Area.Min();
                    var (qg, qt, th) = Solve(m, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                    int im = 0; for (int i = 1; i < th.T.Length; i++) if (th.T[i] > th.T[im]) im = i;
                    row = $"{which}\t{r:0.000}\t{R(qt)}\t{R(qg)}\t{m.CellCount}\t{m.HoleSliversMerged}\t{m.SliversMerged}\t{arc.Count}\t{R(holeLen)}\t{bandCells.Length}\t{R(bandMinFrac)}\t{R(bandMinArea)}\t{R(minArea)}\t{R(m.HoleArcCoverage)}\t{R(th.T[im])}\t{R(m.Centroid[im].X)}\t{R(m.Centroid[im].Z)}\t{im}\t{R(th.T.Min())}\tok";
                }
                catch (Exception e)
                {
                    row = $"{which}\t{r:0.000}\tNaN\tNaN\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t异常 {e.GetType().Name}: {e.Message.Replace('\t', ' ').Replace('\n', ' ')}";
                }
                W(row);
            }
        }
        W($"# 耗时 {sw.Elapsed.TotalMinutes:0.00} min（4 核机器、另有子代理同机，争用下量得）；证据 {Path.GetFileName(file)}");
    }

    /// <summary>自检：V7（BuildFromMaterialWith + AnchorsOf + 生产规则）与 V0（生产 Build）在 rh 与 26.005 上格数、抽热逐位相同；本探针的 Solve 与 DrawingPathParityTests.SolveOne 抽热逐位相同。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 自检_With支与生产Build逐位同_Solve与SolveOne逐位同()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(EvidenceDir(), DeliverableOut.StampedName("自检.txt"));
        void W(string t) { sb.AppendLine(t); _o.WriteLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"P1 自检　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（Linux 镜像，争用下）");
        bool all = true;
        foreach (int which in new[] { 0, 1 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = DrawingPathParityTests.Case(which);
            foreach (double r in new[] { 25.8, 26.0, 26.005, 26.08 })
            {
                var a = MeshFor(0, g, lc, r); var b = MeshFor(7, g, lc, r);
                var qa = Solve(a, lc, g, iA, tRoot, tSet, clampC, tabInsul).QTube;
                var qb = Solve(b, lc, g, iA, tRoot, tSet, clampC, tabInsul).QTube;
                double r0 = g.HoleRadiusMm; g.HoleRadiusMm = r;
                var a2 = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
                g.HoleRadiusMm = r0;
                var qs = DrawingPathParityTests.SolveOne(a2, lc, g, iA, tRoot, tSet, clampC, tabInsul).QFromTube;
                bool ok = qa == qb && a.CellCount == b.CellCount && qa == qs;
                all &= ok;
                W($"{name} r {r}：V0 抽热 {R(qa)} 格 {a.CellCount}｜V7 抽热 {R(qb)} 格 {b.CellCount}｜SolveOne 抽热 {R(qs)}｜逐位同 {(ok ? "是" : "否")}");
            }
        }
        W($"合计：{(all ? "全部逐位同" : "有不同")}；证据 {Path.GetFileName(file)}");
    }
    static ShellMesh MeshH(FlangePlate g, LineCase lc, double r, double hF, bool ruleA)
    {
        double r0 = g.HoleRadiusMm; g.HoleRadiusMm = r;
        try
        {
            var (xa, za) = FlangeMesher.AnchorsOf(g);
            var rules = ruleA ? MeshRules.Production : new MeshRules { MergeHoleCorners = false };
            return FlangeMesher.BuildFromMaterialWith(new AnalyticMaterial(g), r, rules, 0, hF, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                                      lc.MeshInnerMm, lc.MeshInnerRadiusMm, twoTabs: g.TwoTabs, xAnchors: xa, zAnchors: za,
                                                      clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        }
        finally { g.HoleRadiusMm = r0; }
    }

    /// <summary>收敛参照：hFine 2／1／0.5／0.25 × 规则 A 开／关，五个孔半径（避开节点半径）。只印。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 收敛参照_hFine四档_规则A开关_五个半径()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder();
        string file = Path.Combine(EvidenceDir(), DeliverableOut.StampedName("收敛参照.tsv"));
        void W(string t) { sb.AppendLine(t); _o.WriteLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"# P1 收敛参照　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（Linux 镜像，争用下）；网格经 BuildFromMaterialWith（AnchorsOf 锚点，规则 A 开 = 生产规则，与 Build 逐位同见自检）；粗区、细区半径、内带照 lc");
        W("例\tr\thFine\t规则A\tQtube\tQgen\t格数\t并格A\t弧面数\t最热格T");
        foreach (int which in new[] { 0, 1 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = DrawingPathParityTests.Case(which);
            foreach (double r in new[] { 25.59, 25.8, 26.04, 26.2, 26.44 })
                foreach (double hF in new[] { 2.0, 1.0, 0.5, 0.25 })
                    foreach (bool a in new[] { true, false })
                    {
                        try
                        {
                            var m = MeshH(g, lc, r, hF, a);
                            var (qg, qt, th) = Solve(m, lc, g, iA, tRoot, tSet, clampC, tabInsul);
                            int arcN = m.Faces.Count(fc => fc.B < 0 && !double.IsNaN(fc.ArcRadiusMm));
                            W($"{which}\t{r:0.000}\t{hF}\t{(a ? "开" : "关")}\t{R(qt)}\t{R(qg)}\t{m.CellCount}\t{m.HoleSliversMerged}\t{arcN}\t{R(th.TMaxC)}");
                        }
                        catch (Exception e) { W($"{which}\t{r:0.000}\t{hF}\t{(a ? "开" : "关")}\t异常 {e.GetType().Name}: {e.Message}"); }
                    }
        }
        W($"# 耗时 {sw.Elapsed.TotalMinutes:0.00} min（争用下量得）；证据 {Path.GetFileName(file)}");
    }

    /// <summary>并格明细：生产 hFine 2，每个跳点两侧（及一个不跳的节点事件作对照），列出所有并过格的单元（矩形、各矩形的料面积）与其弧面。只印。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 并格明细_跳点两侧()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(EvidenceDir(), DeliverableOut.StampedName("并格明细.txt"));
        void W(string t) { sb.AppendLine(t); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); }
        W($"P1 并格明细　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（Linux 镜像）；生产 Build hFine 2；每个并过格的单元：目标矩形 + 并入矩形（料面积 mm²，AnalyticMaterial.Integrate）；该单元的弧面（条数、总长、DistAB 最小）；形心");
        var pairs = new[] { (25.610, 25.615), (25.995, 26.000), (26.000, 26.005), (26.075, 26.080), (26.305, 26.310), (26.100, 26.105) };
        foreach (int which in new[] { 0, 1 })
        {
            var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = DrawingPathParityTests.Case(which);
            foreach (var (ra, rb) in pairs)
                foreach (double r in new[] { ra, rb })
                {
                    var m = MeshFor(0, g, lc, r);
                    double r0 = g.HoleRadiusMm; g.HoleRadiusMm = r; var mat = new AnalyticMaterial(g); g.HoleRadiusMm = r0;
                    var qt = Solve(m, lc, g, iA, tRoot, tSet, clampC, tabInsul).QTube;
                    W($"══ {name} r {r:0.000}：抽热 {R(qt)} 格 {m.CellCount} 并格A {m.HoleSliversMerged} 并格B {m.SliversMerged}");
                    var rects = m.CellRects!;
                    // 孔边被孔圆穿过、未并的小格（料面积 < 0.05 mm²）也列出
                    for (int c = 0; c < m.CellCount; c++)
                    {
                        var rl = rects[c];
                        var arc = m.Faces.Where(fc => fc.A == c && fc.B < 0 && !double.IsNaN(fc.ArcRadiusMm)).ToList();
                        bool merged = rl.Count > 1;
                        bool smallHole = arc.Count > 0 && m.Area[c] < 0.05;
                        if (!merged && !smallHole) continue;
                        string rs = string.Join(" + ", rl.Select(q => $"[{q.x0:0.###},{q.x1:0.###}]×[{q.z0:0.###},{q.z1:0.###}] 料 {mat.Integrate(q.x0, q.x1, q.z0, q.z1).Area:0.#####}"));
                        W($"   格 {c}{(merged ? "（并过）" : "（未并小孔格）")}：{rs}｜形心 ({m.Centroid[c].X:0.###},{m.Centroid[c].Z:0.###}) 面积 {m.Area[c]:0.#####}｜弧面 {arc.Count} 条 总长 {arc.Sum(f => f.Length):0.####} DistAB min {(arc.Count > 0 ? arc.Min(f => f.DistAB) : double.NaN):0.####}");
                    }
                }
        }
        W($"证据 {Path.GetFileName(file)}");
    }
}
