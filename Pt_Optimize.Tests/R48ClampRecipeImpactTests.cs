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
/// ★★★ R48 判定网格配方（压接整面接触 + 自相似压接细带）进生产默认之后的**补测与影响量**（2026-09-14，Opus 5 写；复审意见要的数）。
/// 四个探针各写一个**新文件**（不覆盖改动前的证据文件）；每个建网格调用都显式写明两个开关（R48ClampRecipeTests 门 g 守着）。
/// 口径钉住（2026-09-15 Opus 5）：生产默认改为压接面上定温（配方 ⑤）后，本类整面接触的建网格调用一律显式 clampFaceDirichlet: false（形心整格，跑出这四个文件时的口径）。
/// 口径钉住（2026-09-15 Opus 5，续）：同日生产缺省又改为不铺压接细带（FlangeMesher.ClampBandPerHFine 3.0 → 0.0），本类原写 clampBandMm: double.NaN（= 当时的自相似 3·hFine）的地方
///   一律改成显式 3.0 × hFine，数与跑出这四个文件时逐位相同。
///   ⚠ 探针「四」走 LineRunner.Run（生产缺省），重跑会是面上定温 + 不铺细带的新配方，与 R48_集总模型排除压接格_2026-09-14.txt 的数不再相同；
///     探针「三」原来的防漂移断言（复刻值 == HolePlacementTests.Run 的生产缺省）因生产配方已变而无从成立，改为只打印生产缺省那一行（见那里的注释）。
///     复审修（2026-09-15 Opus 5，审查意见 minor「门变弱了，没有替代」）：探针「三」加一行按**当前**生产配方显式复刻
///     （clampBandMm 0、clampFullFace true、clampFaceDirichlet true），断言它与 HolePlacementTests.Run（生产缺省）的峰值 J 逐位相等 —— 防漂移断言恢复，钉的是当前配方；
///     2026-09-14 配方那一行（「新配方（两个都开）」）照旧打印，不断言。
///
///   一  组合配方收敛（四片）→ deliverable/R48_组合配方收敛_2026-09-14.txt
///       自相似细带的原证据（R48_压接细带自相似_2026-09-14.txt）是在**只钉外圈**口径下量的；复审人在片0 上补量过组合（差 ≤ 0.0064 W），这里补全四片。
///       设置同 R48ClampBandSelfSimilarTests（误差预算工作点、单片管侧固定），两组：整面接触均匀网格、整面接触 + 自相似细带 + 分级网格；h = 1／0.5／0.25。
///       跑前写死的判读：四片三档「组合 − 整面均匀」都 ≤ 0.05 W（与 R48_压接细带自相似 同一门槛）⇒ 组合口径成立；
///       比值只打印不判（复审人量到片0 整面均匀的步长 −0.0098 → +0.0652 W，不单调，比值没有意义）。
///   二  片0 配方影响 → R48_判定网格配方影响_2026-09-14.txt
///       只钉外圈 vs 整面（均匀三档）；老口径 vs 各开一个 vs 新配方（分级 h=1、导航网格）：抽热、圆盘区−管温（限值 LineCase.DiscOverTempMaxK）、法兰 J 峰与位置、发热。
///   三  孔位对比 x = −185 那一行的分解 → R48_孔位对比x185分解_2026-09-14.txt
///       HolePlacementTests 的板与解法（压接长取 DesignInputs 缺省 3 mm），整面、细带各开各关；每行记孔边最小格边，看各行孔边网格密度是否一致。
///   四  整线（误差预算设计、导航网格）：整片热稳定与升温两节点「排除压接格」前后 → R48_集总模型排除压接格_2026-09-14.txt
///       前后两个数由同一个生产函数 LineRunner.FlangeLumped 给（excludeClampCells false／true），并核对判据表里的数就是 true 那一份。
/// </summary>
[Trait("速度", "慢")]
public class R48ClampRecipeImpactTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampRecipeImpactTests(ITestOutputHelper o) { _out = o; }

    // 误差预算工作点：片电流与管根（出处 deliverable/R48_接头电流核对_2026-09-14.txt，R48ErrorBudgetTests／R48ClampBandSelfSimilarTests 同用）
    private static readonly double[] IJ = { 1213, 1984, 1817, 1022 };
    private static readonly double[] TRoot = { 1172.63, 1151.19, 1093.09, 1056.80 };

    private static (DesignSpec d, DesignInputs p) Design()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        return (d, p);
    }

    private static FlangePlate PlateOf(DesignSpec d, DesignInputs p, LineCase lc, int j)
    {
        var g = d.Plate(j, d.DiscFloorMm(p));
        g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        return g;
    }

    private Action<string> Writer(string name)
    {
        var sb = new StringBuilder();
        string file = Path.Combine(HandoverDoc.Root(), "deliverable", name);
        return s =>
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        };
    }

    private sealed record Sol(int Cells, int ClampCells, double Q, double DiscK, double JMax, double JX, double JZ, double QGen, string Note);

    /// <summary>单片解（与 R48ErrorBudgetTests.Solve 同一套调用：SolveFor + ShellThermal，管侧固定、夹持定温）。</summary>
    private static Sol Solve(DesignSpec d, LineCase lc, int j, ShellMesh m, FlangePlate g)
    {
        double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
        var sc = ShellCurrent.SolveFor(lc, m, IJ[j], Materials.PtResistivity(tSet) * 1e3, tSet);
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
        if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
        var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, TRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                    tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                    discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
        Assert.True(sc.Converged, $"片{j} 电流场没收敛");
        Assert.True(th.Converged, $"片{j} 热场没收敛");
        int iMax = 0;
        for (int i = 1; i < m.CellCount; i++) if (sc.JMagAPerMm2[i] > sc.JMagAPerMm2[iMax]) iMax = i;
        return new Sol(m.CellCount, m.ClampCell.Count(b => b), th.QFromTubeW, th.TDiscMaxC - TRoot[j], sc.JMagAPerMm2[iMax],
                       m.Centroid[iMax].X, m.Centroid[iMax].Z, th.QGenW, m.ClampAnchorNote);
    }

    private static string Row(string name, Sol s)
        => $"   {name,-26} {s.Cells,7} 格  压接格 {s.ClampCells,5}  抽热 {s.Q:+0.0000;-0.0000} W  圆盘区−管温 {s.DiscK:+0.000;-0.000} K"
         + $"  J 峰 {s.JMax:0.00} A/mm²（x {s.JX:0.##}，z {s.JZ:0.##}）  发热 {s.QGen:0.0} W";

    [Fact]
    public void 一_组合配方收敛_整面均匀对整面自相似细带分级_四片()
    {
        var Say = Writer("R48_组合配方收敛_2026-09-14.txt");
        Say($"R48 组合配方收敛（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("配方：两组都是压接整面接触。「整面均匀」= 均匀 h、不铺细带（clampBandMm 0、clampFullFace true）；");
        Say("      「组合」= MeshAdapt.RefineWholeMesh(h) 分级网格 + 自相似压接细带（clampBandMm 3×hFine、clampFullFace true、形心整格）= 2026-09-14 的生产配方。");
        Say("工作点：电流 1213/1984/1817/1022 A，管根 1172.63/1151.19/1093.09/1056.80 °C（R48_接头电流核对_2026-09-14.txt）。");
        Say("跑前写死的判读：四片三档「组合 − 整面均匀」都 ≤ 0.05 W ⇒ 组合口径成立；比值只打印。");
        var (d, p) = Design();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        double[] hs = { 1.0, 0.5, 0.25 };
        bool ok = true;
        for (int j = 0; j < 4; j++)
        {
            Say("");
            Say($"── 片{j}");
            var U = new Dictionary<double, Sol>();
            var G = new Dictionary<double, Sol>();
            {
                var lcN = d.BuildCase(p, checkRamp: false);
                var gN = PlateOf(d, p, lcN, j);
                var mN = FlangeMesher.Build(gN, 0, lcN.MeshFineMm, lcN.MeshCoarseMm, lcN.MeshFineRadiusMm, lcN.Base.BusbarClampLengthMm,
                                            lcN.MeshInnerMm, lcN.MeshInnerRadiusMm, clampBandMm: 3.0 * lcN.MeshFineMm, clampFullFace: true, clampFaceDirichlet: false);
                Say(Row("组合 导航网格（只打印）", Solve(d, lcN, j, mN, gN)));
            }
            foreach (double h in hs)
            {
                var lcU = d.BuildCase(p, checkRamp: false);
                var gU = PlateOf(d, p, lcU, j);
                var mU = FlangeMesher.Build(gU, 0, h, h, 1e6, lcU.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: false);
                U[h] = Solve(d, lcU, j, mU, gU);
                Say(Row($"整面均匀 h={h}", U[h]));

                var lcG = d.BuildCase(p, checkRamp: false);
                MeshAdapt.RefineWholeMesh(lcG, h, radius);
                var gG = PlateOf(d, p, lcG, j);
                var mG = FlangeMesher.Build(gG, 0, lcG.MeshFineMm, lcG.MeshCoarseMm, lcG.MeshFineRadiusMm, lcG.Base.BusbarClampLengthMm,
                                            lcG.MeshInnerMm, lcG.MeshInnerRadiusMm, clampBandMm: 3.0 * lcG.MeshFineMm, clampFullFace: true, clampFaceDirichlet: false);
                G[h] = Solve(d, lcG, j, mG, gG);
                Say(Row($"组合 h={h}", G[h]) + $"  − 整面均匀 {G[h].Q - U[h].Q:+0.0000;-0.0000} W");
                if (Math.Abs(G[h].Q - U[h].Q) > 0.05) ok = false;
            }
            double RatioOf(Dictionary<double, Sol> s) => (s[0.25].Q - s[0.5].Q) / (s[0.5].Q - s[1.0].Q);
            Say($"   步长：整面均匀 {U[0.5].Q - U[1.0].Q:+0.0000;-0.0000} → {U[0.25].Q - U[0.5].Q:+0.0000;-0.0000} W（比 {RatioOf(U):0.000}）；"
              + $"组合 {G[0.5].Q - G[1.0].Q:+0.0000;-0.0000} → {G[0.25].Q - G[0.5].Q:+0.0000;-0.0000} W（比 {RatioOf(G):0.000}）");
            Say($"   组合 − 整面均匀：{string.Join(" / ", hs.Select(h => (G[h].Q - U[h].Q).ToString("+0.0000;-0.0000")))} W"
              + (hs.All(h => Math.Abs(G[h].Q - U[h].Q) <= 0.05) ? "　三档都 ≤ 0.05 ✓" : "　**有档 > 0.05**"));
        }

        // 压接边界两侧的格宽（片0；以 hFine 为单位）：细带是不是自相似，要看网格相对压接边界的位置
        Say("");
        Say("── 片0 压接边界 x = 舌尖 + 压接长 两侧的 x 向格宽 ÷ hFine（生产分级轴，组合配方；| 标压接边界）");
        foreach (double h in new[] { 0.0, 1.0, 0.5, 0.25 })
        {
            var lc = d.BuildCase(p, checkRamp: false);
            if (h > 0) MeshAdapt.RefineWholeMesh(lc, h, radius);
            var g = PlateOf(d, p, lc, 0);
            var m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                       lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: 3.0 * lc.MeshFineMm, clampFullFace: true, clampFaceDirichlet: false);
            double hF = lc.MeshFineMm, ca = g.TabTipXMm + lc.Base.BusbarClampLengthMm;
            var xs = m.Nodes.Select(v => v.X).Distinct().OrderBy(v => v).ToArray();
            int k = Array.FindIndex(xs, x => Math.Abs(x - ca) < 1e-9);
            Assert.True(k > 0, $"hFine {hF}：压接边界 {ca} 不是节点");
            var left = Enumerable.Range(Math.Max(1, k - 5), k - Math.Max(1, k - 5) + 1).Select(i => (xs[i] - xs[i - 1]) / hF);
            var right = Enumerable.Range(k + 1, Math.Min(xs.Length - 1, k + 5) - k).Select(i => (xs[i] - xs[i - 1]) / hF);
            Say($"   {(h > 0 ? $"h={h}" : "导航网格"),-8} hFine {hF:0.###}：{string.Join(" ", left.Select(v => v.ToString("0.000")))} | "
              + $"{string.Join(" ", right.Select(v => v.ToString("0.000")))}");
        }
        Say("");
        Say(ok ? "★ 四片三档「组合 − 整面均匀」都 ≤ 0.05 W ⇒ 组合口径（整面接触 + 自相似细带 + 分级网格）成立。"
               : "★ **有片有档 > 0.05 W** ⇒ 组合口径不成立，先查原因，不许把自相似细带的旧证据照搬到整面接触上。");
        Assert.True(ok, "组合配方收敛没过，见 deliverable/R48_组合配方收敛_2026-09-14.txt");
    }

    [Fact]
    public void 二_片0配方影响_抽热_圆盘区温差_J峰_发热()
    {
        var Say = Writer("R48_判定网格配方影响_2026-09-14.txt");
        Say($"R48 判定网格配方对片0 生产数的影响（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("工作点：片0 电流 1213 A、管根 1172.63 °C（R48_接头电流核对_2026-09-14.txt）；单片管侧固定、夹持定温（与 R48ErrorBudgetTests 同一套解法）。");
        Say("口径：外圈 = clampFullFace false（改动前）；整面 = clampFullFace true；细带 3 mm／自相似 = clampBandMm 3／3×hFine（形心整格）。");
        var (d, p) = Design();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc0 = d.BuildCase(p, checkRamp: false);
        Say($"圆盘区−管温的限值 {lc0.DiscOverTempMaxK} K（LineCase.DiscOverTempMaxK）。");
        Say("");
        Say("── 均匀网格（不铺细带）");
        foreach (double h in new[] { 1.0, 0.5, 0.25 })
        {
            var lc = d.BuildCase(p, checkRamp: false);
            var g = PlateOf(d, p, lc, 0);
            Say(Row($"外圈 h={h}", Solve(d, lc, 0, FlangeMesher.Build(g, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false), g)));
            Say(Row($"整面 h={h}", Solve(d, lc, 0, FlangeMesher.Build(g, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: false), g)));
        }
        foreach (double h in new[] { 0.0, 1.0 })
        {
            var lc = d.BuildCase(p, checkRamp: false);
            if (h > 0) MeshAdapt.RefineWholeMesh(lc, h, radius);
            var g = PlateOf(d, p, lc, 0);
            Say("");
            Say(h > 0 ? "── 分级网格 h=1（MeshAdapt.RefineWholeMesh，误差预算「分级」组同一做法）" : "── 导航网格（BuildCase 给的生产参数，不加密）");
            ShellMesh B(double band, bool full) => FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                                                                    lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: band, clampFullFace: full, clampFaceDirichlet: false);
            Say(Row("老口径（外圈、不铺细带）", Solve(d, lc, 0, B(0, false), g)));
            Say(Row("外圈 + 固定 3 mm 细带", Solve(d, lc, 0, B(3.0, false), g)));
            Say(Row("外圈 + 自相似细带", Solve(d, lc, 0, B(3.0 * lc.MeshFineMm, false), g)));
            Say(Row("整面、不铺细带", Solve(d, lc, 0, B(0, true), g)));
            Say(Row("新配方（整面 + 自相似细带）", Solve(d, lc, 0, B(3.0 * lc.MeshFineMm, true), g)));
        }
        Say("");
        Say("读法：均匀网格与 h=1 分级的外圈口径下 J 峰落在压接角（x ≈ 舌尖 + 压接长、z ≈ ±舌半宽），均匀三档随 h 变细而长 ⇒ 是钉外圈的角点奇异，不是物理峰；");
        Say("      导航网格（2 mm）太粗，角点奇异显不出来，峰落在孔边。整面接触各档的峰都在孔边（x ≈ −26～−27）。");
    }

    [Fact]
    public void 三_孔位对比x185那一行的分解()
    {
        var Say = Writer("R48_孔位对比x185分解_2026-09-14.txt");
        Say($"R48 孔位对比表的分解（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        var baseIn = new DesignInputs();
        double clampLen = baseIn.BusbarClampLengthMm;
        var g0 = HolePlacementTests.Heater1(0, 0);
        Say($"板同 HolePlacementTests.Heater1：舌尖 x {g0.TabTipXMm}、压接长 {clampLen} mm（DesignInputs 缺省）⇒ 压接段 x ∈ [{g0.TabTipXMm}, {g0.TabTipXMm + clampLen}]；R8 孔；1000 A；"
          + "网格 hFine 2、hCoarse 8、细区半径 50（HolePlacementTests.Run 同一调用，只多写配方开关：细带、整面接触、面上定电位）。");
        Say("列：峰值 J 与所在格形心、孔边最小格边（形心距孔心 < 孔半径 + 1.5 mm 的格，√面积）、单元数。");
        // 第五行（2026-09-15 Opus 5 复审修）：当前生产配方的显式复刻（整面接触 + 面上定电位 + 不铺细带），与 HolePlacementTests.Run 逐位比（见循环里的断言）
        var configs = new (string name, double band, bool full, bool face)[]
        {
            ("老口径（外圈、不铺细带）", 0, false, false), ("只开整面接触", 0, true, false), ("只铺自相似细带", 3.0 * 2.0, false, false), ("新配方（两个都开）", 3.0 * 2.0, true, false),
            ("生产配方 2026-09-15（整面 + 面上定电位 + 不铺细带）", 0, true, true),
        };
        foreach (double? hx in new double?[] { null, -20, -50, -100, -150, -185 })
        {
            var g = hx is null ? HolePlacementTests.Heater1(0, 0) : HolePlacementTests.Heater1(hx.Value, 8);
            Say("");
            Say(hx is null ? "── 无孔" : $"── 孔心 x = {hx}（孔占 x ∈ [{hx - 8}, {hx + 8}]）");
            foreach (var (name, band, full, face) in configs)
            {
                var mesh = FlangeMesher.Build(g, 0, 2.0, 8.0, 50.0, clampLen, 0, 0, clampBandMm: band, clampFullFace: full, clampFaceDirichlet: face);
                var cur = ShellCurrent.SolveFor(new LineCase { Base = baseIn }, mesh, 1000, 1.1e-4, maxIter: 8000, tol: 1e-7);
                int iMax = -1;
                for (int i = 0; i < mesh.CellCount; i++)
                    if (!double.IsNaN(cur.JMagAPerMm2[i]) && (iMax < 0 || cur.JMagAPerMm2[i] > cur.JMagAPerMm2[iMax])) iMax = i;
                double jPk = iMax < 0 ? 0 : cur.JMagAPerMm2[iMax];
                string edge = "—";
                if (hx is not null)
                {
                    var near = Enumerable.Range(0, mesh.CellCount).Where(i =>
                    {
                        double dx = mesh.Centroid[i].X - hx.Value, dz = mesh.Centroid[i].Z;
                        return Math.Sqrt(dx * dx + dz * dz) < 8 + 1.5;
                    }).ToArray();
                    edge = near.Length == 0 ? "（无）" : $"{near.Min(i => Math.Sqrt(mesh.Area[i])):0.000} mm（{near.Length} 格）";
                }
                Say($"   {name,-16} 峰值 J {jPk:0.000}（x {mesh.Centroid[Math.Max(iMax, 0)].X:0.##}，z {mesh.Centroid[Math.Max(iMax, 0)].Z:0.##}）　孔边最小格边 {edge}　{mesh.CellCount} 格");
                if (face)
                {
                    // 防漂移（2026-09-14 立；2026-09-15 Opus 5 复审修恢复）：本探针对「当前生产配方」的显式复刻与 HolePlacementTests.Run（生产缺省）逐位同一个峰值。
                    //   2026-09-15 上午第一版因生产缺省已变，把原断言（钉 2026-09-14 配方那一行）降成了只打印 —— 门变弱、没有替代；
                    //   现在断言钉在这一行（显式写出当前配方的三个开关），2026-09-14 那一行照旧只打印。
                    //   生产缺省再变 ⇒ 这里红 ⇒ 要么改这一行的显式开关、要么查 HolePlacementTests.Run 走的是不是生产缺省。
                    var run = HolePlacementTests.Run(g);
                    Assert.True(run.Err.Length == 0, run.Err);
                    Say($"   {"（HolePlacementTests.Run，生产缺省）",-16} 峰值 J {run.JPeak:0.000}");
                    Assert.Equal(run.JPeak, jPk);
                }
            }
        }
        Say("");
        Say("读法：孔（x ∈ [−193, −177]）不在压接段（[−199.5, −196.5]）里；哪一列把 x=−185 行的峰值从老口径改掉，看「只开整面」「只铺细带」两行。");
    }

    [Fact]
    public void 四_整线集总模型排除压接格前后()
    {
        var Say = Writer("R48_集总模型排除压接格_2026-09-14.txt");
        Say($"R48 整片热稳定与升温两节点：排除压接格前后（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("算例：误差预算设计（R48ErrorBudgetTests.Design）、导航网格、生产配方；LineRunner.Run 一次，前后两个数都由 LineRunner.FlangeLumped 给。");
        var (d, p) = Design();
        var lc = d.BuildCase(p, checkRamp: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Say($"LineRunner.Run：收敛 {r.Converged}　{sw.Elapsed.TotalSeconds:0} s");
        foreach (var f in r.Flanges)
            Say($"   {f.Name,-8} 发热 {f.QGenW:0.000} W　抽热 {f.QFromTubeW:+0.000;-0.000} W　管根 {f.TRootC:0.00} °C　单元 {f.CellCount}"
              + $"　压接格 {f.Mesh?.ClampCell.Count(b => b) ?? -1}　{(f.Mesh?.ClampAnchorNote.Contains("铺细步") == true ? "有细带" : "**没细带**")}");
        Say("   改动前（老口径）同一算例：发热 467.069 / 710.923 / 578.428 / 311.183 W，抽热 −9.660 / −14.847 / −11.674 / −4.137 W，"
          + "整片热稳定 14.4887（HC1|HC2），升温法兰−管峰值 71.6197 K —— 出处 deliverable/R48A_基线树老口径记录_2026-09-14.txt（基线树上跑的）");
        Say("");
        foreach (bool ex in new[] { false, true })
        {
            var o = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: ex)!;
            Say($"── excludeClampCells = {ex}：最不利片 {r.Flanges[o.Index].Name}　排除了 {(o.ClampExcluded ? $"{o.ClampAreaMm2:0.0} mm² 压接格" : "（没排除）")}");
            Say($"   热稳定输入：圆盘区 {o.DiscAreaMm2:0.0} mm²　舌片区 {o.TabAreaMm2:0.0} mm²　舌片导热长 {o.TabLenMm:0.0} mm");
            Say($"   热稳定：裕度 {o.Stab.Margin:0.0000}　散热侧 表面 {o.Stab.DSurfDT:0.0000} + 夹持 {o.Stab.DClampDT:0.0000} + 管孔 {o.Stab.DTubeDT:0.0000} W/K　发热侧 {o.Stab.DGenDT:0.0000} W/K");
            if (o.RampIn is { } gi)
                Say($"   升温输入：质量 {gi.FlangeMassG:0.00} g　包保温 {gi.FlangeAreaInsulMm2:0.0} mm²　裸露 {gi.FlangeAreaBareMm2:0.0} mm²"
                  + $"　等效外半径 {gi.PlateEqOuterRadiusMm:0.00} mm　平均厚 {gi.FlangeThickMm:0.0000} mm　参考电阻 {gi.FlangeResistanceRefOhm:E4} Ω");
            Say(o.Ramp is { } rt
                ? $"   升温：法兰−管峰值 {rt.MaxFlangeMinusTubeK:0.0000} K（{rt.TimeAtMaxDeltaS / 3600:0.0} h、管温 {rt.TTubeAtMaxDeltaC:0} °C）　法兰峰值 {rt.TFlangePeakC:0} °C"
                : $"   升温：算不出来 {o.RampError}");
        }
        var yes = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        var stab = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var ramp = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        Assert.Equal(yes.Stab.Margin, stab.Actual);
        // ★ R48 G2 复审（2026-09-15 Opus 5）有意改断言：原 Assert.Equal(yes.Ramp.MaxFlangeMinusTubeK, ramp.Actual) → 现「那一行判不了、不给数，排除那一份照样解出来」。
        //   依据：审查意见 blocker —— 物理把关人复核前升温那一行不许给数（两节点模型的法兰温度是整片平均，看不到孔边比管热），见 LineRunner.RampPendingNote。
        Assert.NotNull(yes.Ramp);
        Assert.True(ramp.Undetermined && double.IsNaN(ramp.Actual), "升温那一行复核前应判不了、不给数");
        Say("");
        Say("✓ 判据表里的整片热稳定就是 excludeClampCells = true 那一份；升温那一行复核前判不了、不给数（R48 G2 复审 2026-09-15），排除那一份照样解出来。");
        Say("");
        Say("── 判据表（生产配方，导航网格）");
        foreach (var c in r.Checks)
            Say($"   {c.Name}　{c.Actual:0.####} {c.Unit}　限 {c.Limit:0.####}　{(c.Undetermined ? "判不了" : c.Ok ? "过" : "**不过**")}　{c.Where}");
    }
}
