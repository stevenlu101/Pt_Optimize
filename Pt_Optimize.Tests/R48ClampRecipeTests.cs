using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 判定网格配方的快门（2026-09-14，Opus 5 写）：**压接整面接触 + 自相似压接细带**进了生产默认，门造在默认入口与下游上。
///
/// ══ 配方与依据（声明在 FlangeMesher.BuildFromField 的注释里，本门不重述算式，只调生产函数）
///   · 整面接触：deliverable/R48_压接整面接触AB_2026-09-14.txt（整面 − 外圈：片0 抽热 +16.4～+17.6 W，片1 +32.1～+34.3 W）；
///   · 自相似细带：deliverable/R48_压接细带自相似_2026-09-14.txt（只钉外圈口径下量的：两侧各 3·h 与均匀网格差 ≤ 0.028 W，比值差 ≤ 0.005）；
///     整面接触下的组合验证见 R48ClampRecipeImpactTests（慢，新文件名）。
///
/// ══ 八道门（工单四道 + 第一轮补的两道 + 复审补的两道）
///   (a) 解析板片0 均匀 h=2：默认网格的 ClampCell 数 = 形心在压接段内的格数 = 舌宽/h × 压接长/h（直舌的闭式数），
///       外圈格（压接边界面相邻格）全在整面格里；与老口径外圈格数比。
///   (b) **不带两个开关**调 Build（导航网格与 h=1 分级网格两种）：压接边界两侧 ResolveClampBandMm(NaN, hFine) 内的 x 向节点间距细到 hFine 量级；
///       不铺细带时同一窗口里有远场粗格（证明门不是空转）；图纸路径 BuildFromField 不带开关同样整面 + 细带。
///   (c) 不带开关的默认网格上 ShellCurrent 解后所有 ClampCell 的 V == 1、定温模式下所有 ClampCell 的 T == 夹持温度；热场收敛、能量闭合。
///   (d) 显式 clampFullFace: false、clampBandMm: 0 与改动前一致：ClampCell 空、网格记录里没有细带；
///       格数、抽热，以及**自由端与热导模式**下的舌端均温、铜排等效长度，对得上**基线树**（改动前代码）跑出来的记录（逐位）。
///   (e) 退化几何（压接段盖到管孔）不填 ClampCell、退回只钉外圈，⚠ 警告进 LineRunner 输出，而且说人话（改后第一遍快套件抓到的）。
///   (f) LineRunner 建网格不覆写这两个开关（生产链读的就是默认配方）。
///   (g) 被生产代码或配方声明引作依据的证据探针，建网格时必须显式写明两个开关（复审 major：默认值一改，证据探针悄悄换口径、重跑还覆盖同名证据）。
///   (h) 下游：LineRunner.Run 出来的每片网格都带整面接触与细带；判据表里整片热稳定、升温两节点两条参考量排除了压接格
///       （复审 major：发热已不含压接段，而面积／质量还按整片算，两个参考量都偏乐观）；排除后升温那条偏保守的原因写进了界面附注。
///
/// ══ 与工单原文不同的两处（如实写明，2026-09-14 Opus 5）
///   · 工单 (a) 要求 h=2 时整面格数 ≥ 10 × 外圈格数。本板舌宽 60、压接长 40：整面 30×20 = 600 格，外圈 30 + 2×19 = 68 格，比值 8.8 ——
///     几何上到不了 10（实测 600／68，与 deliverable/R48_实验b_压接边界落节点_2026-09-14.txt 的 R=∞ 行「钉住格数 68」一致）。
///     ⇒ h=2 断言闭式数 600 与外圈 68、比值 ≥ 8；另在 h=1 上断言 ≥ 10（2400 vs 138，外圈数同 R48_压接整面接触AB 文件 h=1 行）。
///   · 工单 (b) 要求窗口内节点间距 ≤ hFine。生产的渐变轴（FlangeMesher.GradedAxisCentered）有两条规则让窗口里个别格略大于 hFine：
///     进细带那一步之后的下一步受增长率 1.3 限制（实测 h=1 时 1.011 mm），压接锚点贴节点时允许把一格拉长到 1.25·hWant（R47 第三轮 N3）。
///     这两条都是改动前就有的生产规则，自相似细带的证据文件也是在这套规则下跑出来的，不为了门去改它。
///     ⇒ 断言：窗口内整格都 ≤ 1.25·hFine，超过 hFine 的至多 2 格，且窗口里至少有 2×3 − 2 格；不铺细带时窗口里必须有 &gt; 1.25·hFine 的格。
/// </summary>
public class R48ClampRecipeTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampRecipeTests(ITestOutputHelper o) { _out = o; }

    /// <summary>同误差预算的设计（deliverable/R48_离散误差预算_2026-09-14.txt 的 R48ErrorBudgetTests.Design）。</summary>
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

    private static FlangePlate Plate0(DesignSpec d, DesignInputs p, LineCase lc)
    {
        var g = d.Plate(0, d.DiscFloorMm(p));
        g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        return g;
    }

    /// <summary>老口径的压接格：带压接标签的边界面相邻的格（与 R48ClampFullFaceTests 数「钉住格」同一个数法）。</summary>
    private static int[] RingCellIds(ShellMesh m)
        => m.Faces.Where(f => f.B < 0 && f.Tag == ShellMesh.TagTabEnd).Select(f => f.A).Distinct().ToArray();
    private static int RingCells(ShellMesh m) => RingCellIds(m).Length;

    /// <summary>形心在压接段内的格数 —— 判定调生产的 FlangeMesher.InClampSegment，舌尖取板件自己的 TabTipXMm（不取网格内部量）。</summary>
    private static int CentroidInClamp(ShellMesh m, FlangePlate g, double clampLen)
        => Enumerable.Range(0, m.CellCount).Count(i => FlangeMesher.InClampSegment(m.Centroid[i].X, g.TabTipXMm, clampLen, g.TwoTabs));

    /// <summary>
    /// 误差预算工作点上解片0：电流 1213 A、管根 1172.63 °C（出处 deliverable/R48_接头电流核对_2026-09-14.txt，误差预算同用）。
    /// mode：定温（生产，夹持温度取算例）／自由端（夹持温度 −1、铜排热导 −1）／热导（夹持温度 −1、G = 1.1 W/K，DesignInputs.BusbarConductanceWPerK 说明里的典型值）。
    /// 与基线树探针（deliverable/R48A_基线树老口径记录_探针源码_2026-09-14.cs.txt）同一套调用。
    /// </summary>
    private static (ShellCurrentResult sc, ShellThermalResult th) SolvePlate0(DesignSpec d, LineCase lc, FlangePlate g, ShellMesh m, string mode = "定温")
    {
        double tSet = d.SetpointC[0];
        var sc = ShellCurrent.SolveFor(lc, m, 1213, Materials.PtResistivity(tSet) * 1e3, tSet);
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
        switch (mode)
        {
            case "定温": if (lc.ClampTempC.Length > 0) p2.BusbarClampTempC = lc.ClampTempC[0]; break;
            case "自由端": p2.BusbarClampTempC = -1; p2.BusbarConductanceWPerK = -1; break;
            case "热导": p2.BusbarClampTempC = -1; p2.BusbarConductanceWPerK = 1.1; break;
            default: throw new ArgumentException(mode);
        }
        var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, 1172.63, g.InsulBoundaryXResolved, g.TwoTabs,
                                    tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                    discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
        return (sc, th);
    }

    /// <summary>生产导航网格参数（BuildCase 给的）与 h=1 整片加密（MeshAdapt.RefineWholeMesh，误差预算「分级」组同一做法）。</summary>
    private static IEnumerable<(string name, LineCase lc)> GradedCases(DesignSpec d, DesignInputs p)
    {
        yield return ("导航网格", d.BuildCase(p, checkRamp: false));
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lcH = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lcH, 1.0, radius);
        yield return ("h=1 分级", lcH);
    }

    /// <summary>
    /// **缺省入口**：不带两个开关调生产的 Build（复审 minor 2026-09-14 Opus 5：第一轮 BuildGraded 显式传了 NaN／true，
    /// 门量的是配方值不是缺省值，有人把缺省改回去门照样绿）。
    /// </summary>
    private static ShellMesh BuildDefault(FlangePlate g, LineCase lc)
        => FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                              lc.MeshInnerMm, lc.MeshInnerRadiusMm);

    /// <summary>老口径（只钉外圈），细带宽度显式给（0 = 不铺）。</summary>
    private static ShellMesh BuildRing(FlangePlate g, LineCase lc, double clampBandMm = 0)
        => FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm,
                              lc.MeshInnerMm, lc.MeshInnerRadiusMm, clampBandMm: clampBandMm, clampFullFace: false);

    [Fact]
    public void a_整面接触格数等于形心在压接段内的格数_远多于外圈()
    {
        var (d, p) = Design();
        var lc = d.BuildCase(p, checkRamp: false);
        var g = Plate0(d, p, lc);
        double clampLen = lc.Base.BusbarClampLengthMm;
        // 闭式数的前提：直舌、没有延长段、舌尖到切点都是等宽 2·TabEndHalfWidthMm，压接段整段在直舌上
        Assert.False(g.TwoTabs);
        Assert.True(g.ExtensionMm < 1e-9, $"本门的闭式数假设没有延长段，实际 {g.ExtensionMm}");
        Assert.Equal(g.TabEndHalfWidthMm, g.Tangent().HalfW, 9);
        Assert.True(g.TabTipXMm + clampLen < g.Tangent().X, "压接段伸进了圆盘，闭式数不成立");

        foreach (var (h, minRatio, ringRecord) in new[] { (2.0, 8.0, 68), (1.0, 10.0, 138) })
        {
            var m = FlangeMesher.Build(g, 0, h, h, 1e6, clampLen);                                  // 默认 = 生产配方
            var mOld = FlangeMesher.Build(g, 0, h, h, 1e6, clampLen, clampBandMm: 0, clampFullFace: false);
            int full = m.ClampCell.Count(b => b);
            int inClamp = CentroidInClamp(m, g, clampLen);
            long closed = (long)Math.Round(2 * g.TabEndHalfWidthMm / h) * (long)Math.Round(clampLen / h);
            int ringOld = RingCells(mOld);
            _out.WriteLine($"h={h}：单元 {m.CellCount}　整面 {full}　形心在段内 {inClamp}　闭式 {closed}　老口径外圈 {ringOld}　比 {full / (double)ringOld:0.00}");

            Assert.Equal(m.CellCount, m.ClampCell.Length);
            Assert.Equal(inClamp, full);
            Assert.Equal(closed, full);
            // 外圈格数的记录：h=2 → 68（deliverable/R48_实验b_压接边界落节点_2026-09-14.txt，片0 R=∞ 行），
            //                 h=1 → 138（deliverable/R48_压接整面接触AB_2026-09-14.txt，片0 h=1 外圈行；两文件板外形相同，只差厚度与保温）
            Assert.Equal(ringRecord, ringOld);
            Assert.True(full >= minRatio * ringOld, $"h={h}：整面 {full} 格不到外圈 {ringOld} 格的 {minRatio} 倍");
            // 默认网格上外圈标签照旧打，而且每个外圈格都在整面格里（并集 = 整面，见 BuildFromField 的注释）
            var ringCells = RingCellIds(m);
            Assert.Equal(ringOld, ringCells.Length);
            Assert.All(ringCells, i => Assert.True(m.ClampCell[i], $"外圈格 {i}（x={m.Centroid[i].X:0.###}）不在整面格里"));
        }
    }

    [Fact]
    public void b_不带开关的网格在压接边界两侧自相似细带内是细步()
    {
        var (d, p) = Design();
        foreach (var (name, lc) in GradedCases(d, p))
        {
            var g = Plate0(d, p, lc);
            double hF = lc.MeshFineMm;
            double band = FlangeMesher.ResolveClampBandMm(double.NaN, hF);
            double ca = g.TabTipXMm + lc.Base.BusbarClampLengthMm;
            Assert.Equal(FlangeMesher.ClampBandPerHFine * hF, band, 12);
            // 窗口必须在细区外（否则细带本来就细，门量不到东西）
            Assert.True(Math.Abs(ca) - band > lc.MeshFineRadiusMm, $"{name}：压接边界 {ca} 的细带窗口落在细区半径 {lc.MeshFineRadiusMm} 内，门空转");
            Assert.True(lc.MeshCoarseMm > 1.25 * hF, $"{name}：远场步 {lc.MeshCoarseMm} 不比细步粗，门空转");

            (double[] inside, double maxTouch, string note) Window(ShellMesh m)
            {
                var xs = m.Nodes.Select(v => v.X).Distinct().OrderBy(v => v).ToArray();
                var ins = new List<double>(); double touch = 0;
                for (int k = 0; k + 1 < xs.Length; k++)
                {
                    double a = xs[k], b = xs[k + 1];
                    if (b > ca - band + 1e-9 && a < ca + band - 1e-9) touch = Math.Max(touch, b - a);
                    if (a >= ca - band - 1e-9 && b <= ca + band + 1e-9) ins.Add(b - a);
                }
                Assert.Contains(xs, x => Math.Abs(x - ca) < 1e-9);                                   // 压接边界是节点（配方 ①）
                return (ins.ToArray(), touch, m.ClampAnchorNote);
            }

            var mDef = BuildDefault(g, lc);
            var (ins, _, note) = Window(mDef);
            _out.WriteLine($"{name}：hFine {hF}　细带单侧 {band}　窗口内整格 {ins.Length} 个：{string.Join(" ", ins.Select(v => v.ToString("0.###")))}　记录：{note}");
            Assert.Contains($"压接边界两侧各 {band:0.###} mm 铺细步 {hF:0.###} mm", note);
            Assert.True(ins.Length >= 2 * FlangeMesher.ClampBandPerHFine - 2, $"{name}：窗口里只有 {ins.Length} 格");
            Assert.All(ins, w => Assert.True(w <= 1.25 * hF + 1e-9, $"{name}：窗口内有 {w:0.###} mm 的格，超过 1.25·hFine"));
            Assert.True(ins.Count(w => w > hF + 1e-9) <= 2, $"{name}：窗口内超过 hFine 的格多于 2 个：{string.Join(" ", ins.Select(v => v.ToString("0.###")))}");
            Assert.True(mDef.ClampCell.Length == mDef.CellCount && mDef.ClampCell.Count(b => b) > RingCells(mDef), $"{name}：缺省入口没有整面接触");

            // 非空转：显式不铺细带时，同一窗口里有远场粗格
            var (_, touchOld, noteOld) = Window(BuildRing(g, lc));
            _out.WriteLine($"{name}：不铺细带时窗口里最大格 {touchOld:0.###} mm　记录：{noteOld}");
            Assert.True(touchOld > 1.25 * hF + 1e-9, $"{name}：不铺细带时窗口里最大格只有 {touchOld:0.###}，门空转");
        }

        // 图纸路径的缺省入口：BuildFromField 不带两个开关（厚度场由解析板栅格化，锚点从场推）同样整面接触 + 铺细带
        {
            var lc = d.BuildCase(p, checkRamp: false);
            var g = Plate0(d, p, lc);
            var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
            var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                                 lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
            double band = FlangeMesher.ResolveClampBandMm(double.NaN, lc.MeshFineMm);
            _out.WriteLine($"图纸路径缺省入口：单元 {mF.CellCount}　压接格 {mF.ClampCell.Count(b => b)}　外圈 {RingCells(mF)}　记录：{mF.ClampAnchorNote}");
            Assert.True(mF.ClampCell.Length == mF.CellCount && mF.ClampCell.Count(b => b) > RingCells(mF), "图纸路径缺省入口没有整面接触");
            Assert.Contains($"压接边界两侧各 {band:0.###} mm 铺细步 {lc.MeshFineMm:0.###} mm", mF.ClampAnchorNote);
        }
    }

    [Fact]
    public void c_整面接触格全部是电极且钉在夹持温度_定温热场闭合()
    {
        var (d, p) = Design();
        foreach (var (name, lc) in GradedCases(d, p))
        {
            var g = Plate0(d, p, lc);
            var m = BuildDefault(g, lc);
            int nClamp = m.ClampCell.Count(b => b);
            Assert.True(nClamp > RingCells(m), $"{name}：整面格 {nClamp} 不多于外圈格 {RingCells(m)}");
            var (sc, th) = SolvePlate0(d, lc, g, m);
            Assert.True(sc.Converged, $"{name}：电流场没收敛");
            Assert.True(th.Converged, $"{name}：热场没收敛");
            var bad = Enumerable.Range(0, m.CellCount).Where(i => m.ClampCell[i] && sc.V[i] != 1.0).Take(5).ToArray();
            Assert.True(bad.Length == 0, $"{name}：压接格上 V ≠ 1：" + string.Join("；", bad.Select(i => $"x={m.Centroid[i].X:0.###} V={sc.V[i]:R}")));
            Assert.True(sc.ConservationError < 1e-6, $"{name}：电流不守恒 {sc.ConservationError:E2}");
            // 定温模式：整段压接格都要被钉在夹持温度上（复审 minor：第一轮断言「舌端平均温度 == 夹持温度」，而定温分支直接返回夹持温度，恒真）
            Assert.True(lc.ClampTempC.Length > 0 && lc.ClampTempC[0] >= 0, "本门要定温模式");
            double tClamp = lc.ClampTempC[0];
            var hot = Enumerable.Range(0, m.CellCount).Where(i => m.ClampCell[i] && th.T[i] != tClamp).Take(5).ToArray();
            Assert.True(hot.Length == 0, $"{name}：压接格没钉在夹持温度 {tClamp}：" + string.Join("；", hot.Select(i => $"x={m.Centroid[i].X:0.###} T={th.T[i]:R}")));
            // 反证：压接段内边外侧紧邻的自由格不是夹持温度（否则上面那条也可能是整片被钉住）
            double ca = g.TabTipXMm + lc.Base.BusbarClampLengthMm;
            var free = Enumerable.Range(0, m.CellCount).Where(i => !m.ClampCell[i] && m.Centroid[i].X > ca && m.Centroid[i].X < ca + 2 * lc.MeshFineMm).ToArray();
            Assert.NotEmpty(free);
            Assert.All(free, i => Assert.True(th.T[i] > tClamp + 1e-6, $"{name}：压接段外的格 x={m.Centroid[i].X:0.###} 也是夹持温度"));
            // 能量闭合（门槛同 R48FaceDirichletTests）：压接格整段排除在账外、铜排带走只剩内边那排面，账要平
            Assert.True(Math.Abs(th.EnergyResidualW) < 1e-3 * Math.Max(1, th.QGenW),
                $"{name}：能量不闭合 残差 {th.EnergyResidualW:0.0000} W、发热 {th.QGenW:0.0} W");
            _out.WriteLine($"{name}：单元 {m.CellCount}　整面格 {nClamp}　抽热 {th.QFromTubeW:+0.0000;-0.0000} W　铜排带走 {th.QToClampW:0.00} W　发热 {th.QGenW:0.00} W　残差 {th.EnergyResidualW:+0.000000;-0.000000} W");
        }
    }

    [Fact]
    public void d_显式老口径与改动前一致()
    {
        var (d, p) = Design();
        var cases = GradedCases(d, p).ToDictionary(c => c.name, c => c.lc);

        // 导航网格（hFine 2、远场 11、细区半径 50）：老口径格数 1006、外圈 36 ——
        // 记录出处 deliverable/R48_实验b_压接边界落节点_2026-09-14.txt 片0 h=2 R=50 行（改动前跑的，单元 1006、钉住格数 36）
        {
            var lc = cases["导航网格"];
            Assert.Equal(2.0, lc.MeshFineMm); Assert.Equal(11.0, lc.MeshCoarseMm); Assert.Equal(50.0, lc.MeshFineRadiusMm);
            var g = Plate0(d, p, lc);
            var m = BuildRing(g, lc);
            _out.WriteLine($"导航网格 老口径：单元 {m.CellCount}　外圈 {RingCells(m)}　记录：{m.ClampAnchorNote}");
            Assert.Empty(m.ClampCell);
            Assert.DoesNotContain("铺细步", m.ClampAnchorNote);
            Assert.Contains("已落成节点", m.ClampAnchorNote);
            Assert.Equal(1006, m.CellCount);
            Assert.Equal(36, RingCells(m));

            // ★ 复审补（2026-09-14 Opus 5；审查意见 minor「老口径不是逐位相同」）：三种舌端边界模式下的**逐位**记录。
            //   记录值出处：deliverable/R48A_基线树老口径记录_2026-09-14.txt —— 在基线树（.basetree，改动前代码）上跑的探针
            //   （源码 deliverable/R48A_基线树老口径记录_探针源码_2026-09-14.cs.txt，与本类 SolvePlate0 同一套调用），打印用 R 格式、可逐位比。
            //   第一轮把舌端均温、铜排等效长度的面积加权也套到了老口径上，自由端与热导模式的这两个数就会变（非空转证据见下面的「面积加权」对照）。
            var ring = RingCellIds(m);
            foreach (var (mode, tTab, q, busLen) in new[]
            {
                ("定温",   450.0,              -9.645643968612903,   0.0),
                ("自由端", 1041.8128051597412, -54.527245549214356,  0.0),
                ("热导",   180.89233437852974, -5.7002974071605745,  1.8722891720599724),
            })
            {
                var (_, th) = SolvePlate0(d, lc, g, m, mode);
                double areaW = ring.Sum(i => th.T[i] * m.Area[i]) / ring.Sum(i => m.Area[i]);
                _out.WriteLine($"导航网格 老口径 {mode}：舌端均温 {th.TTabEndMeanC:R}（面积加权会是 {areaW:R}）　抽热 {th.QFromTubeW:R}　铜排等效长 {th.BusEquivLenMm:R}");
                Assert.True(th.Converged, $"{mode}：热场没收敛");
                Assert.Equal(tTab, th.TTabEndMeanC);
                Assert.Equal(q, th.QFromTubeW);
                Assert.Equal(busLen, th.BusEquivLenMm);
                if (mode != "定温")
                    Assert.True(Math.Abs(areaW - tTab) > 1e-6, $"{mode}：面积加权与按格数平均分不开（{areaW:R}），这条对照空转");
            }
        }

        // h=1 分级：同误差预算的「分级」组（clampBandMm 0，那时还没有整面接触）与「细带」组（clampBandMm 3）。
        {
            var lc = cases["h=1 分级"];
            var g = Plate0(d, p, lc);
            var mOld = BuildRing(g, lc);
            var (_, thOld) = SolvePlate0(d, lc, g, mOld);
            _out.WriteLine($"h=1 分级 老口径：单元 {mOld.CellCount}　抽热 {thOld.QFromTubeW:R} W");
            Assert.Empty(mOld.ClampCell);
            Assert.DoesNotContain("铺细步", mOld.ClampAnchorNote);
            // 记录：deliverable/R48_离散误差预算_2026-09-14.txt 片0「分级 h=1.000　4122 格　抽热 -9.225 W」（打印三位小数 ⇒ 容差 0.0005）；
            //       逐位值 −9.225247067674745 出自 deliverable/R48A_基线树老口径记录_2026-09-14.txt（复审补，2026-09-14 Opus 5）
            Assert.Equal(4122, mOld.CellCount);
            Assert.True(Math.Abs(thOld.QFromTubeW - (-9.225)) <= 0.0005 + 1e-9, $"老口径抽热 {thOld.QFromTubeW:0.0000}，记录 -9.225");
            Assert.Equal(-9.225247067674745, thOld.QFromTubeW);

            var mBand3 = BuildRing(g, lc, clampBandMm: 3.0);
            var (_, thBand3) = SolvePlate0(d, lc, g, mBand3);
            _out.WriteLine($"h=1 分级 显式 3 mm 细带、外圈：单元 {mBand3.CellCount}　抽热 {thBand3.QFromTubeW:+0.0000;-0.0000} W");
            // 记录：R48_离散误差预算「细带 h=1.000　4722 格」；R48_压接细带自相似_2026-09-14.txt 片0 固定3mm 第一档 -7.3434 W（四位小数 ⇒ 容差 0.00005）
            Assert.Equal(4722, mBand3.CellCount);
            Assert.True(Math.Abs(thBand3.QFromTubeW - (-7.3434)) <= 0.00005 + 1e-9, $"显式 3 mm 细带抽热 {thBand3.QFromTubeW:0.00000}，记录 -7.3434");

            // h=1 时自相似 3·h = 3 mm ⇒ 不带开关的缺省网格与显式 3 mm 的节点逐位相同，只多了整面接触
            var mDef = BuildDefault(g, lc);
            Assert.Equal(mBand3.Nodes.Count, mDef.Nodes.Count);
            for (int k = 0; k < mDef.Nodes.Count; k++)
            {
                Assert.Equal(mBand3.Nodes[k].X, mDef.Nodes[k].X);
                Assert.Equal(mBand3.Nodes[k].Z, mDef.Nodes[k].Z);
            }
            Assert.Equal(mBand3.CellCount, mDef.CellCount);
            Assert.Empty(mBand3.ClampCell);
            Assert.NotEmpty(mDef.ClampCell);
        }
    }

    /// <summary>
    /// (e) 退化几何（改后第一次跑快套件时 TabInsulPerPlateTests「推不出切点的场」红了才发现，2026-09-14 Opus 5 补）：
    /// 没有舌片、材料全在压接长以内的图纸，整面接触会把电极压到管孔上 ⇒ 生成器不填 ClampCell、退回只钉外圈、写 ⚠ 记录，
    /// 而且这句警告要进 LineRunner 的输出（门造在下游：赋了值不等于用它的人读得到）。板与算例同 TabInsulPerPlateTests 那条。
    /// 复审补：这句进界面输出框，不许带网格内部说法。
    /// </summary>
    [Fact]
    public void e_压接段盖到管孔时退回只钉外圈_警告进输出且说人话()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = new FlangePlate { DiscRadiusMm = 28, HoleRadiusMm = holeR, TabEndXMm = 0, TabEndHalfWidthMm = 28, ThicknessMm = 1.0, TabParallel = true };
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        Assert.True(lc.Base.BusbarClampLengthMm > 28, "本门要压接长盖过整片（材料 x ∈ [0, 28]）");

        var m = FlangeMesher.BuildFromField(f, holeR, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                            lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        _out.WriteLine("网格记录：" + m.ClampAnchorNote);
        Assert.Empty(m.ClampCell);
        Assert.Contains("盖到了管孔", m.ClampAnchorNote);
        Assert.True(RingCells(m) > 0, "退回只钉外圈后应当还有外圈电极");

        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { f, f };
        lc.GeomForJudge = Array.Empty<FlangePlate>();
        lc.TabInsul3dmPerPlateMm = new[] { 2.8, 2.8 };
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        var warn = r.Notes.Where(s => s.Contains("盖到了管孔")).ToArray();
        _out.WriteLine(string.Join("\n", warn));
        Assert.Equal(r.Flanges.Length, warn.Length);                       // 每片一句、按片去重
        Assert.All(warn, s => Assert.StartsWith("⚠ ", s));
        // 进界面的字串：不带网格内部说法（复审 minor 2026-09-14 Opus 5），也不带判据代号与开关名
        foreach (string jargon in new[] { "整面接触", "外圈", "孔边格", "边界面", "clamp", "②", "③" })
            Assert.All(warn, s => Assert.DoesNotContain(jargon, s));
    }

    /// <summary>
    /// (f) 生产链读的是默认配方：出判据的 LineRunner 建网格时不许显式关掉整面接触或改细带宽度
    /// （默认值在 FlangeMesher 一处定；调用点各自传参会让生产链与门量的不是同一张网格）。源码级，2026-09-14 Opus 5。
    /// </summary>
    [Fact]
    public void f_LineRunner建网格不覆写配方开关()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("FlangeMesher.Build(plate,", s);
        Assert.Contains("FlangeMesher.BuildFromField(tf, holeR,", s);
        Assert.Contains("FlangeMesher.Build(pl,", s);                  // 集总模型那一份（FlangeLumped）
        Assert.DoesNotContain("clampFullFace", s);
        Assert.DoesNotContain("clampBandMm", s);
        // 生成器的缺省值就是配方
        string mesh = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShellMesh.cs"));
        Assert.Equal(2, Regex.Matches(mesh, @"double clampBandMm = double\.NaN, bool clampFullFace = true\)").Count);
    }

    /// <summary>
    /// (g) ★ 复审 major（2026-09-14 Opus 5）：**证据探针必须显式钉住两个开关**。
    /// 默认值一改，没传开关的探针悄悄换了口径，重跑还用同名文件覆盖被引的证据（R48_压接细带自相似 三组全变成整面接触，
    /// 而配方 ④ 引的正是这份文件）。源码级：
    ///   受管的测试文件 = 写了「生产代码（Pt_Optimize/Core）注释里引作依据的证据文件」的，或以字面量写 R48_…_日期.txt 证据文件的；
    ///   其中每个 FlangeMesher.Build／BuildFromField 调用都必须写出 clampFullFace: 与 clampBandMm:（值随探针原义，老证据钉 false／0）。
    /// 非空转：BuildFromField 配方声明里引的每个证据文件都要找得到写它的受管探针（实验 a 例外：R48ClampPhaseTests 已改写到实验 b 的文件，见该类注释）。
    /// 本类自己不在扫描之列（门里要写那几个文件名）。
    /// </summary>
    [Fact]
    public void g_证据探针建网格必须显式写明两个开关()
    {
        string root = HandoverDoc.Root();
        const string namePat = @"R4\d_[^\s""'（）()，、；。：:<>／]*?_2026-\d\d-\d\d\.txt";
        string core = string.Concat(Directory.GetFiles(Path.Combine(root, "Pt_Optimize", "Core"), "*.cs").Select(File.ReadAllText));
        var cited = Regex.Matches(core, namePat).Select(x => x.Value).Distinct().ToArray();
        var tests = Directory.GetFiles(Path.Combine(root, "Pt_Optimize.Tests"), "*.cs")
                             .Where(f => Path.GetFileName(f) != nameof(R48ClampRecipeTests) + ".cs")
                             .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
        var gated = tests.Where(kv => cited.Any(n => kv.Value.Contains("\"" + n + "\""))
                                   || Regex.IsMatch(kv.Value, @"""R48_[^""]*_2026-\d\d-\d\d\.txt"""))
                         .ToDictionary(kv => kv.Key, kv => kv.Value);
        var bad = new List<string>();
        int calls = 0;
        foreach (var (file, src) in gated)
            foreach (Match mm in Regex.Matches(src, @"FlangeMesher\.Build(FromField)?\("))
            {
                int start = mm.Index + mm.Length, i = start, depth = 1;
                while (i < src.Length && depth > 0) { if (src[i] == '(') depth++; else if (src[i] == ')') depth--; i++; }
                string args = src.Substring(start, Math.Max(0, i - 1 - start));
                calls++;
                if (!Regex.IsMatch(args, @"\bclampFullFace\s*:") || !Regex.IsMatch(args, @"\bclampBandMm\s*:"))
                    bad.Add($"{file} 第 {src.Take(mm.Index).Count(ch => ch == '\n') + 1} 行");
            }
        _out.WriteLine($"生产代码引的证据文件 {cited.Length} 个；受管探针 {gated.Count} 个：{string.Join("、", gated.Keys.OrderBy(k => k))}；建网格调用 {calls} 处");
        Assert.True(bad.Count == 0, "这些证据探针建网格没显式写明 clampFullFace／clampBandMm（生产默认一改就悄悄换口径）：" + string.Join("；", bad));
        Assert.True(calls >= 20, $"只扫到 {calls} 处建网格调用，门空转");

        string sm = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Core", "ShellMesh.cs"));
        int i0 = sm.IndexOf("判定网格配方声明", StringComparison.Ordinal);
        Assert.True(i0 >= 0, "BuildFromField 的配方声明不见了");
        int i1 = sm.IndexOf("</summary>", i0, StringComparison.Ordinal);
        var declared = Regex.Matches(sm.Substring(i0, i1 - i0), namePat).Select(x => x.Value).Distinct().ToArray();
        _out.WriteLine("配方声明引的证据：" + string.Join("、", declared));
        Assert.True(declared.Length >= 7, $"配方声明只引了 {declared.Length} 个证据文件");
        var orphan = declared.Where(n => !gated.Values.Any(s => s.Contains("\"" + n + "\""))).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "R48_实验a_压接段相位_2026-09-14.txt" }, orphan);
    }

    /// <summary>
    /// (h) ★ 复审 major 与 minor（2026-09-14 Opus 5）：**门造在下游**。
    ///   · LineRunner.Run 出来的每片 Flanges[j].Mesh 都带整面接触（ClampCell 非空）与压接细带（ClampAnchorNote 含「铺细步」）；
    ///   · 判据表里整片热稳定、升温两节点这两条参考量就是 LineRunner.FlangeLumped(excludeClampCells: true) 那一份，而且与不排除的那一份不同（非空转）；
    ///     排除的方向：舌片区面积、体积、导热长都变小，少掉的面积正好是压接格面积。
    /// 算例：设计记录 W08 只跑一段（两片端片），为了快；误差预算设计四片整线约 90 s，放在慢探针 R48ClampRecipeImpactTests 四 里。
    /// </summary>
    [Fact]
    public void h_整线网格带整面接触与细带_集总参考量排除了压接格()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        _out.WriteLine($"LineRunner.Run：{sw.Elapsed.TotalSeconds:0.0} s　Ok {r.Ok}　{r.Message}");
        Assert.True(r.Ok, r.Message);
        Assert.NotEmpty(r.Flanges);
        foreach (var f in r.Flanges)
        {
            Assert.NotNull(f.Mesh);
            var m = f.Mesh!;
            _out.WriteLine($"{f.Name}：单元 {m.CellCount}　压接格 {m.ClampCell.Count(b => b)}　记录：{m.ClampAnchorNote}");
            Assert.True(m.ClampCell.Length == m.CellCount && m.ClampCell.Any(b => b), $"{f.Name}：整线出来的网格没有整面接触");
            Assert.Contains("铺细步", m.ClampAnchorNote);
        }

        var yes = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: true)!;
        var no = LineRunner.FlangeLumped(lc, r.Flanges, excludeClampCells: false)!;
        Assert.True(yes.ClampExcluded && yes.ClampAreaMm2 > 0, "生产那一份没排除压接格");
        Assert.False(no.ClampExcluded);
        var stab = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var ramp = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampField, StringComparison.Ordinal));
        _out.WriteLine($"热稳定：判据表 {stab.Actual:R}　排除 {yes.Stab.Margin:R}　不排除 {no.Stab.Margin:R}");
        _out.WriteLine($"升温法兰−管：判据表 {ramp.Actual:R}　排除 {yes.Ramp?.MaxFlangeMinusTubeK:R}　不排除 {no.Ramp?.MaxFlangeMinusTubeK:R}");
        Assert.False(double.IsNaN(stab.Actual), "热稳定判不了，本门量不到东西");
        Assert.Equal(yes.Stab.Margin, stab.Actual);
        Assert.NotEqual(no.Stab.Margin, stab.Actual);
        Assert.NotNull(yes.Ramp); Assert.NotNull(no.Ramp);
        Assert.Equal(yes.Ramp!.MaxFlangeMinusTubeK, ramp.Actual);
        Assert.NotEqual(no.Ramp!.MaxFlangeMinusTubeK, ramp.Actual);
        Assert.True(yes.TabAreaMm2 < no.TabAreaMm2 && yes.VolumeMm3 < no.VolumeMm3 && yes.TabLenMm < no.TabLenMm,
            "排除压接格后舌片区面积、体积、导热长应当都变小");
        Assert.Equal(yes.ClampAreaMm2, (no.TabAreaMm2 + no.DiscAreaMm2) - (yes.TabAreaMm2 + yes.DiscAreaMm2), 6);
        Assert.Equal(lc.Base.BusbarClampLengthMm, no.TabLenMm - yes.TabLenMm, 9);
        // 排除后升温两节点偏保守（模型没有铜排通道）—— 这件事要写在界面那条参考量的附注里，不只写在代码注释里
        Assert.Contains("偏保守", ramp.Note);
        Assert.Contains("铜排", ramp.Note);
    }
}
