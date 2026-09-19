using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 离散误差预算（2026-09-14，Opus 5 写；规格出自常驻数值把关人第九轮）：
/// **在整线工作点上，单片抽热的离散不确定度 U 有多大？加密路线够不够？**
///
/// ══ 为什么重做
/// 实验 a/b/c 与门四～八的单片探针喂的是升温设计电流（1214/2102/2102/1214 A）与旧管根，焦耳热比整线稳态多 0～41 %
/// （deliverable/R48_接头电流核对_2026-09-14.txt）。数值把关人撤回了在那个工况上给的量级（片2 理查森剩余 1.26 W、需 h≈0.09 mm、整线衰减 0.39）。
/// 0.45 W 的预算也作废：它来自 09-13 旧落点离限值 1.35 W ÷ 3，那个设计状态已不存在。
/// 预算不是一个固定瓦数 —— 求解器把净流入二分到 0⁺，离散不确定度 U 决定根必须留的裕度（净流入 ≥ 3U）。本探针只量 U，不判可行。
///
/// ══ 工作点（出处写死）
/// 电流 1213 / 1984 / 1817 / 1022 A、管根 1172.63 / 1151.19 / 1093.09 / 1056.80 °C：R48_接头电流核对_2026-09-14.txt（14:19，
/// 导航网格整线一次；配方：保温按半径、圆盘保温 20 mm、管保温 5 mm、压接边界锚点与分界格混合都已打开）。
/// 数值把关人：导航网格的工作点够用，但加一组核对 —— 均匀 h=1、0.5，管根 ±5 K，看 Δ(1→0.5) 变化是否 &lt; 10 %。
///
/// ══ 三组网格（同一片、同一工作点）
///   均匀：Build(h, h, 细区无穷)；
///   分级（生产配方）：MeshAdapt.RefineWholeMesh(h, 细区半径 RequiredMeshFor) 之后 FlangeMesher.Build，与 LineRunner 解析路径同一调用；
///   分级 + 压接段细带：同上，压接边界两侧各 3 mm 铺细步（BuildFromField 的实验开关 clampBandMm，生产默认 0）。
///   ⚠ 2026-09-14 Opus 5：生产默认已改为压接整面接触 + 自相似细带（见 FlangeMesher.BuildFromField 的配方声明）；本探针的建网格调用显式钉回改动前口径
///   （只钉外圈；「均匀」「分级」不铺细带，「细带」组照旧 3 mm），deliverable/R48_离散误差预算_2026-09-14.txt 重跑不换口径。
///   h = 1 / 0.5 / 0.25；均匀组片1、片2 再补 h = 0.125（两组三档，比值一致时安全系数可由 3 降到 1.25）。
///
/// ══ 报什么（每片每组）
///   各档抽热、步长 Δ、比 r = Δ后/Δ前、理查森剩余 R = |Δ末|·r/(1−r)（0 &lt; r &lt; 1 才有）、U = 3R；片1、片2 均匀组两组三档比值差 ≤ 0.1 时另报 U = 1.25R。
///   d = 分级 − 均匀、d细带 = 细带 − 均匀，逐档。另报圆盘区−管温的步长（K）。
///
/// ══ 跑前写死的判读（数值把关人第九轮）
///   · 分级组 d 随 h 减半约减半：相邻两档 d 之比落在 [0.35, 0.65] ⇒ 两族收敛到同一极限；否则是失败（两族极限不同）。
///   · 细带组 |d| 在每档都 ≤ 分级组 |d| 的 1/2 ⇒ 粗格整格钉温是分级偏差的主要来源，细带有效。
///   · 管根 ±5 K 时 Δ(1→0.5) 变化 &lt; 10 % ⇒ 导航网格工作点够用；否则要用 0.5 mm 整线的工作点重做。
///   · 系统偏差核对（门八第二条的事后解释，不解场）：均匀网格片0 +x 半边「在网格里（覆盖 ≥ 1/4）且矩形中心 r &gt; 盘半径」的格子面积，
///     h = 1 → 0.5 → 0.25 的两个比值应接近形心−混合差的比值 0.55、0.31（各 ±0.1）。
/// 只记录与判读，不改生产配方。
/// </summary>
[Trait("速度", "慢")]
public class R48ErrorBudgetTests
{
    private readonly ITestOutputHelper _out;
    public R48ErrorBudgetTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 整线工作点上的单片离散误差预算()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_离散误差预算_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 离散误差预算（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　整线工作点（R48_接头电流核对_2026-09-14.txt）");

        var p = new DesignInputs();
        DesignSpec Design()
        {
            var d0 = DesignSpec.W08.Clone();
            d0.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d0.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
            d0.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d0 = d0.Fit();
            d0.SizeTongues(p);
            return d0;
        }
        var d = Design();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        double[] iJ = { 1213, 1984, 1817, 1022 };
        double[] tRoot = { 1172.63, 1151.19, 1093.09, 1056.80 };
        Say($"电流 {string.Join("/", iJ)} A；管根 {string.Join("/", tRoot.Select(v => v.ToString("0.00")))} °C；生产细区半径 {radius:0}");

        (double q, double disc, int cells, string note) Solve(int j, string group, double h, double tr)
        {
            var lcH = d.BuildCase(p, checkRamp: false);
            var g = d.Plate(j, d.DiscFloorMm(p));
            g.HoleRadiusMm = lcH.TubeIdMm * 0.5 + lcH.WallMm;
            ShellMesh m;
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
            if (group == "均匀")
                m = FlangeMesher.Build(g, 0, h, h, 1e6, lcH.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false);
            else
            {
                MeshAdapt.RefineWholeMesh(lcH, h, radius);
                m = FlangeMesher.Build(g, 0, lcH.MeshFineMm, lcH.MeshCoarseMm, lcH.MeshFineRadiusMm, lcH.Base.BusbarClampLengthMm,
                                       lcH.MeshInnerMm, lcH.MeshInnerRadiusMm, clampBandMm: group == "细带" ? 3.0 : 0.0, clampFullFace: false);
            }
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var sc = ShellCurrent.SolveFor(lcH, m, iJ[j], Materials.PtResistivity(tSet) * 1e3, tSet);
            var p2 = SegmentSolver.Clone(lcH.Base); p2.TSetC = tSet;
            if (j < lcH.ClampTempC.Length) p2.BusbarClampTempC = lcH.ClampTempC[j];
            var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tr, g.InsulBoundaryXResolved, g.TwoTabs,
                                        tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                        discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
            Assert.True(th.Converged, $"片{j} {group} h={h} 热场没收敛");
            return (th.QFromTubeW, th.TDiscMaxC - tr, m.CellCount, m.ClampAnchorNote);
        }

        string[] groups = { "均匀", "分级", "细带" };
        double[] hs = { 1.0, 0.5, 0.25 };
        var Q = new Dictionary<(int j, string g, double h), double>();
        var D = new Dictionary<(int j, string g, double h), double>();
        bool dHalves = true, bandHelps = true;
        for (int j = 0; j < 4; j++)
        {
            Say("");
            Say($"══ 片{j}");
            foreach (var grp in groups)
                foreach (double h in hs.Concat(grp == "均匀" && (j == 1 || j == 2) ? new[] { 0.125 } : Array.Empty<double>()))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var s = Solve(j, grp, h, tRoot[j]);
                    Q[(j, grp, h)] = s.q; D[(j, grp, h)] = s.disc;
                    Say($"   {grp} h={h,5:0.000}　{s.cells,7} 格　抽热 {s.q:+0.000;-0.000} W　圆盘区−管温 {s.disc:+0.000;-0.000} K　{sw.Elapsed.TotalSeconds:0} s"
                      + (grp == "细带" && h == 1.0 ? $"　（{s.note}）" : ""));
                }
            foreach (var grp in groups)
            {
                double d1 = Q[(j, grp, 0.5)] - Q[(j, grp, 1.0)], d2 = Q[(j, grp, 0.25)] - Q[(j, grp, 0.5)];
                double r = d2 / d1;
                double R = r > 0 && r < 1 ? Math.Abs(d2) * r / (1 - r) : double.NaN;
                double k1 = D[(j, grp, 0.5)] - D[(j, grp, 1.0)], k2 = D[(j, grp, 0.25)] - D[(j, grp, 0.5)];
                string line = $"   {grp}：抽热步长 {d1:+0.000;-0.000} → {d2:+0.000;-0.000} W　比 {r:0.00}　理查森剩余 {R:0.000} W　U(×3) {3 * R:0.000} W"
                            + $"　｜圆盘区步长 {k1:+0.000;-0.000} → {k2:+0.000;-0.000} K";
                if (grp == "均匀" && (j == 1 || j == 2))
                {
                    double d3 = Q[(j, grp, 0.125)] - Q[(j, grp, 0.25)];
                    double r2 = d3 / d2;
                    double R2 = r2 > 0 && r2 < 1 ? Math.Abs(d3) * r2 / (1 - r2) : double.NaN;
                    bool consistent = Math.Abs(r2 - r) <= 0.1;
                    line += $"\n        补 0.125：步长 {d3:+0.000;-0.000} W　比 {r2:0.00}（前一组 {r:0.00}，差 {Math.Abs(r2 - r):0.00}{(consistent ? " ≤ 0.1 ⇒ 可用 1.25" : " > 0.1 ⇒ 仍用 3")}）"
                          + $"　理查森剩余 {R2:0.000} W　U {(consistent ? 1.25 : 3) * R2:0.000} W（安全系数 {(consistent ? 1.25 : 3)}）";
                }
                Say(line);
            }
            string dLine(string grp)
            {
                var ds = hs.Select(h => Q[(j, grp, h)] - Q[(j, "均匀", h)]).ToArray();
                return $"{grp} − 均匀：{string.Join(" / ", ds.Select(v => v.ToString("+0.000;-0.000")))} W（比 {ds[1] / ds[0]:0.00}、{ds[2] / ds[1]:0.00}）";
            }
            var dG = hs.Select(h => Q[(j, "分级", h)] - Q[(j, "均匀", h)]).ToArray();
            var dB = hs.Select(h => Q[(j, "细带", h)] - Q[(j, "均匀", h)]).ToArray();
            bool halves = dG[1] / dG[0] is >= 0.35 and <= 0.65 && dG[2] / dG[1] is >= 0.35 and <= 0.65;
            bool helps = Enumerable.Range(0, 3).All(i => Math.Abs(dB[i]) <= 0.5 * Math.Abs(dG[i]));
            if (!halves) dHalves = false;
            if (!helps) bandHelps = false;
            Say($"   {dLine("分级")}　{(halves ? "随 h 约减半 ✓" : "**不随 h 减半**")}");
            Say($"   {dLine("细带")}　{(helps ? "每档 ≤ 分级的 1/2 ✓" : "**有档 > 分级的 1/2**")}");
        }

        // ── 管根 ±5 K
        Say("");
        Say("══ 管根 ±5 K 核对（均匀 h=1、0.5）");
        bool tOk = true;
        for (int j = 0; j < 4; j++)
        {
            double dn = Q[(j, "均匀", 0.5)] - Q[(j, "均匀", 1.0)];
            var parts = new List<string>();
            foreach (double dt in new[] { -5.0, 5.0 })
            {
                double a1 = Solve(j, "均匀", 1.0, tRoot[j] + dt).q, a05 = Solve(j, "均匀", 0.5, tRoot[j] + dt).q;
                double rel = Math.Abs((a05 - a1) - dn) / Math.Abs(dn);
                if (!(rel < 0.10)) tOk = false;
                parts.Add($"{dt:+0;-0} K：Δ {a05 - a1:+0.000;-0.000} W（变 {rel:0.0%}）");
            }
            Say($"   片{j}：标称 Δ(1→0.5) {dn:+0.000;-0.000} W　" + string.Join("　", parts));
        }

        // ── 系统偏差核对：+x 半边矩形中心在盘外的格子面积
        Say("");
        Say("══ 系统偏差核对（片0，均匀，不解场）");
        var areas = new List<double>();
        foreach (double h in hs)
        {
            var lcH = d.BuildCase(p, checkRamp: false);
            var g = d.Plate(0, d.DiscFloorMm(p));
            g.HoleRadiusMm = lcH.TubeIdMm * 0.5 + lcH.WallMm;
            var m = FlangeMesher.Build(g, 0, h, h, 1e6, lcH.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false);   // 口径钉住，同上
            double a = 0; int n = 0;
            for (int i = 0; i < m.CellCount; i++)
            {
                double cx = m.Centroid[i].X, cz = m.Centroid[i].Z;
                if (cx > 0 && !FlangePlate.InsideInsulCircle(cx, cz, g.DiscRadiusMm)) { a += m.Area[i]; n++; }
            }
            areas.Add(a);
            Say($"   h={h:0.00}：{n} 格，有料面积 {a:0.000} mm²");
        }
        double ar1 = areas[1] / areas[0], ar2 = areas[2] / areas[1];
        bool areaOk = Math.Abs(ar1 - 0.55) <= 0.1 && Math.Abs(ar2 - 0.31) <= 0.1;
        Say($"   面积比 {ar1:0.00}、{ar2:0.00}（形心−混合差之比 0.55、0.31）⇒ {(areaOk ? "对得上（各 ±0.1）" : "**对不上**")}");

        Say("");
        Say($"★ 分级 d 随 h 减半：{(dHalves ? "四片都是" : "**有片不是 ⇒ 两族极限不同**")}；细带有效：{(bandHelps ? "四片每档都 ≤ 1/2" : "**有片有档不满足**")}；"
          + $"管根 ±5 K：{(tOk ? "Δ 变化都 < 10 %，导航网格工作点够用" : "**有 ≥ 10 % 的，要用 0.5 mm 整线工作点**")}；系统偏差面积核对：{(areaOk ? "对得上" : "**对不上**")}");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
