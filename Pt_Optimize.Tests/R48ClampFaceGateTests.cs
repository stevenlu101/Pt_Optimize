using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48 F 快门（2026-09-15，Opus 5 写；依据物理把关人 2026-09-14 晚）：**压接边界施加在面上**（ShellMesh.ClampFaceDirichlet，配方 ⑤）。
///
/// ══ 改了什么（算式在 ShellMesh.ClampFaceDirichlet／ShellCurrent／ShellThermal 的注释里，本门不重述，只调生产函数）
///   整面接触后压接区原按形心整格定电位、定温 ⇒ 等效边界比真实内边（x = 舌尖 + 压接长）往压接区里偏 h/2，一阶误差。
///   面上口径：压接格不作未知数，压接面（一侧压接格一侧自由格）的导度只取自由格一侧、距离 = ShellMesh.CentroidToFaceMm（边界面 DistAB 的定义）。
///
/// ══ 五道门（跑前写死；(e) 是 2026-09-15 复审修加的）
///   (a) 电流一维条带：均匀厚度直条，一端压接段（整面接触）面上 V=1、另一端管孔标签（ShellCurrent 的管孔仍是**格**上 V=0，故解析电阻量到管孔格形心）；
///       x 向步长专挑不等（线性解对任意步长的两点通量都精确）。面上口径：电位线性、总电流与解析值相对差 ≤ 1e-9；
///       形心口径：与「电极在内边压接格形心」的解析值相对差 ≤ 1e-9，与面上解析值差恰好半格（相对差 = (h_c/2)/(L_面 + h_c/2)，且 &gt; 1e-3，证明两口径真分得开）。
///   (b) 导热一维条带：同一条带、焦耳热 0、表面散热压到下限（散热系数取 0 ⇒ 生产下限 1e-6、发射率 0、不吹风、环境 = 管端温度、
///       控温点取成让散热表只覆盖 [20, 20.01] °C）、两端温差 0.001 K（k(T) 线性，温差小到 k 的变化只进 1e-10 量级）。
///       管孔面上定温（R48 已有），压接面上定温：穿过两端的热流与解析 k(T_均)·t·W·ΔT/(x_孔面 − x_压接面) 相对差 ≤ 1e-9
///       （首跑实测 1.4e-11／6.8e-12，散热 0 W）；形心口径与形心解析值同精度（首跑 1.6e-10）、与面上解析值差半格。
///   (c) 开关关（Build 显式 clampFaceDirichlet: false，细带显式写回 2026-09-14 的 3·hFine）逐位等于改动前代码：第二批 B2 设计片0（1072 A、管根 1139.95 °C，出处 R48_第二批保温扫描_B2_2026-09-14.txt），
///       导航网格与均匀 h=2，定温／自由端／热导三种模式的电位、电流密度、温度场哈希与各输出量，对得上基线树记录
///       （deliverable/R48_压接面上定温_基线树形心口径记录_2026-09-15.txt，探针源码 R48_压接面上定温_基线树形心口径记录_探针源码_2026-09-15.cs.txt，R 格式逐位）。
///       开关开时电位哈希不同、三种模式抽热都与记录不同（非空转）。
///   ★ 2026-09-15 Opus 5（合并）：F 写 (b)(c) 时 ShellThermal 的散热表还是「环境～设定 + 200 K、60 节点」；合并树里已改为「环境～铂熔点、约 22 K 一个节点」，
///     (b) 的「控温点压表」构造失效（散热 5.3e-13 W 进到 1e-8）、(c) 的热场记录不再逐位 —— 是两路门互相不认，不是 F 的面上定温坏了（(c) 的电流场哈希照旧逐位）。
///     两道门都经只供测试的散热表参数把表钉回 F 写门时的那一张（与 G1 门 d 的正规化同一个做法），门槛原样：(b) 1e-9、(c) 逐位。
///   (d) 缺省入口（Build／BuildFromField 不带参数）就是面上；只钉外圈的老口径上开关不激活。开关开时：压接格 V == 1、J == 0，定温模式 T == 夹持温度；
///       逐面用生产的面判定（ShellMesh.IsClampFace）与距离（ShellMesh.CentroidToFaceMm）重算「穿过压接面的电流／热流」，
///       分别等于求解器的总电流与铜排带走 QToClampW；能量闭合。
///   (e) 【2026-09-15 Opus 5 复审修，审查意见 minor「锚点由面序决定」】面上定温时局部热稳定的压接锚点**逐格取最近的压接面**：
///       双舌板（平行边、压接长 40 mm、整面 + 面上 + 不铺细带、均匀 h = 2），两份左右镜像的电流密度（只在一侧舌片自由段 120 &lt; |x| &lt; 内边 给 J）。
///       几何与边界左右对称 ⇒ 最不稳格互为镜像：横向导热长逐位相同（只由几何定）、半径相同（1e-9）、裕度相对差 ≤ 1e-3（温度场是迭代解）。
///       非空转：横向导热长 + 20 mm &lt; 最不稳格半径 − 孔半径（锚点是压接面、不是退回管孔）。
///       注入对照（跑前写死，第一版代码「取 |x| 最小的第一个压接面」上跑）：一侧会退回管孔 ⇒ 横向导热长两边不同，本门红。
/// </summary>
public class R48ClampFaceGateTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceGateTests(ITestOutputHelper o) { _out = o; }

    // ── 条带：x ∈ [0, 20]，压接段 [0, 4]（压接格 h = 1），自由段步长不等，z ∈ [0, 4] 两排格（也不等），厚 1
    private const double StripL = 20.0, StripLc = 4.0, StripW = 4.0, StripT = 1.0;
    private static readonly double[] StripX = { 0, 1, 2, 3, 4, 4.5, 5.25, 6.25, 7.75, 9.5, 11.75, 14.0, 16.0, 17.5, 18.75, 20.0 };
    private static readonly double[] StripZ = { 0, 1.5, 4.0 };

    private static ShellMesh Strip(bool faceMode)
    {
        // 2026-09-15 Opus 5（合并，复审后改）：ShellMesh.ClampFaceDirichlet 改为 init（建完不许再改，否则网格配方指纹说假话）⇒ 原末尾的 m.ClampFaceDirichlet = faceMode; 挪到这里，条带与门槛不变
        var m = new ShellMesh { ClampFaceDirichlet = faceMode };
        int nx = StripX.Length, nz = StripZ.Length;
        var id = new int[nx, nz];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) { id[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(StripX[i], 0, StripZ[j])); }
        for (int i = 0; i + 1 < nx; i++)
            for (int j = 0; j + 1 < nz; j++)
            {
                m.Cells.Add(new[] { id[i, j], id[i + 1, j], id[i + 1, j + 1], id[i, j + 1] });
                m.Area.Add((StripX[i + 1] - StripX[i]) * (StripZ[j + 1] - StripZ[j]));
                m.Centroid.Add(new Vec3(0.5 * (StripX[i] + StripX[i + 1]), 0, 0.5 * (StripZ[j] + StripZ[j + 1])));
                m.Thickness.Add(StripT); m.Part.Add(0); m.Frac.Add(1.0);
            }
        // 边界标签：右端 x = 20 管孔；压接段判定调生产的 FlangeMesher.InClampSegment（舌尖 x = 0），边界面按面中点、整面接触格按形心（与 BuildFromField 同）
        m.BuildFaces(mid => Math.Abs(mid.X - StripL) < 1e-9 ? ShellMesh.TagHole
                          : FlangeMesher.InClampSegment(mid.X, 0.0, StripLc, false) ? ShellMesh.TagTabEnd : ShellMesh.TagFree);
        m.ClampCell = Enumerable.Range(0, m.CellCount).Select(i => FlangeMesher.InClampSegment(m.Centroid[i].X, 0.0, StripLc, false)).ToArray();
        return m;
    }

    [Fact]
    public void a_电流一维条带_面上定电位精确_形心口径差半格()
    {
        double xHoleCell = 0.5 * (StripX[^2] + StripX[^1]);      // 管孔格形心（ShellCurrent 管孔仍在格上钉 V=0）
        double hC = StripX[4] - StripX[3];                         // 内边那排压接格的宽
        foreach (bool face in new[] { true, false })
        {
            var m = Strip(face);
            Assert.Equal(face, m.ClampFaceActive);
            var r = ShellCurrent.Solve(m, 1.0, 1.0, 1300, null, 20000, 1e-15);
            Assert.True(r.Converged, "条带电流场没收敛");
            double xElec = face ? StripLc : StripLc - 0.5 * hC;   // 电极位置：面上 = 内边；形心 = 内边压接格形心
            double iExact = StripT * StripW / (xHoleCell - xElec); // 等温 σ 取 1 ⇒ 归一化电流 = t·W/长
            double relI = Math.Abs(r.CurrentInA - iExact) / iExact;
            double vErr = 0;
            for (int i = 0; i < m.CellCount; i++)
            {
                double x = m.Centroid[i].X;
                if (m.ClampCell[i] || x > xHoleCell - 1e-9) continue;
                vErr = Math.Max(vErr, Math.Abs(r.V[i] - (xHoleCell - x) / (xHoleCell - xElec)));
            }
            double iFaceExact = StripT * StripW / (xHoleCell - StripLc);
            double relVsFace = Math.Abs(r.CurrentInA - iFaceExact) / iFaceExact;
            _out.WriteLine($"{(face ? "面上" : "形心")}：归一化电流 {r.CurrentInA:R}　解析 {iExact:R}　相对差 {relI:E2}　电位线性最大偏差 {vErr:E2}　与面上解析值相对差 {relVsFace:E3}　守恒 {r.ConservationError:E2}");
            Assert.True(relI <= 1e-9, $"{(face ? "面上" : "形心")}口径总电流与解析值相对差 {relI:E2} > 1e-9");
            Assert.True(vErr <= 1e-9, $"{(face ? "面上" : "形心")}口径电位不线性：最大偏差 {vErr:E2}");
            if (face)
                Assert.All(Enumerable.Range(0, m.CellCount).Where(i => m.ClampCell[i]), i => Assert.Equal(0.0, r.JMagAPerMm2[i]));
            else
            {
                double expect = (0.5 * hC) / (xHoleCell - StripLc + 0.5 * hC);   // 半格：I_面 = I_形心·(L + h/2)/L
                Assert.True(Math.Abs(relVsFace - expect) <= 1e-9 && relVsFace > 1e-3, $"形心口径与面上解析值相对差 {relVsFace:E6}，应为半格 {expect:E6}");
            }
        }
    }

    [Fact]
    public void b_导热一维条带_面上定温精确_形心口径差半格()
    {
        const double tCold = 20.0, dT = 0.001;
        var p = new DesignInputs { TAmbC = tCold, LossScale = 0.0, PtEmissivity = 0.0, FlangeAirVelocityMPerS = 0.0 };
        p.TSetC = tCold + 0.01 - 200;                             // ShellThermal 的散热表覆盖 [TAmb, TSet + 200] = [20, 20.01]
        // ★ 2026-09-15 Opus 5（合并）：上一行是 F 写门时的构造（那时 ShellThermal 的散热表是「环境～设定 + 200 K、60 节点」）；合并树里散热表上限已改为铂熔点、
        //   约 22 K 一个节点（ShellThermal.LossTableHiC／LossTableNodes），这个构造不再把表压到 [20, 20.01] ⇒ 表面散热 5.3e-13 W 对 1.8e-5 W 的热流进到 1e-8，本门红。
        //   不挪门槛：用 G1 为同一件事加的只供测试参数把表显式钉回 F 的构造（上限 = 设定 + 200 = 20.01 °C、60 节点），门槛 1e-9 原样。
        double oldHiC = p.TSetC + 200; const int oldNodes = 60;
        p.BusbarConductanceWPerK = -1; p.BusbarClampTempC = tCold + dT;
        double xHoleFace = StripL;                                 // 管孔面上定温（holeFaceDirichlet 缺省开）
        double hC = StripX[4] - StripX[3];
        double kMean = Materials.PtThermalK(tCold + 0.5 * dT) * 1e-3;   // W/(mm·K)；k(T) 线性 ⇒ ∫k dT = k(T_均)·ΔT
        foreach (bool face in new[] { true, false })
        {
            var m = Strip(face);
            var th = ShellThermal.Solve(m, new double[m.CellCount], p, tCold, double.PositiveInfinity, lossTableHiC: oldHiC, lossTableNodes: oldNodes);
            Assert.Equal(oldHiC, th.Recipe.LossTableHiC); Assert.Equal(oldNodes, th.Recipe.LossTableNodes);   // 2026-09-15 Opus 5（合并）：构造真的生效
            double xAnchor = face ? StripLc : StripLc - 0.5 * hC;
            double qExact = kMean * StripT * StripW * dT / (xHoleFace - xAnchor);
            double relTube = Math.Abs(-th.QFromTubeW - qExact) / qExact;       // 热从压接端流向管孔：抽热为负
            double relClamp = Math.Abs(-th.QToClampW - qExact) / qExact;       // 铜排带走为负（热从铜排流进板）
            double qFaceExact = kMean * StripT * StripW * dT / (xHoleFace - StripLc);
            double relVsFace = Math.Abs(-th.QToClampW - qFaceExact) / qFaceExact;
            _out.WriteLine($"{(face ? "面上" : "形心")}：抽热 {th.QFromTubeW:R} W　铜排带走 {th.QToClampW:R} W　解析 {qExact:R}　相对差 管孔 {relTube:E2}／压接 {relClamp:E2}　与面上解析值相对差 {relVsFace:E3}　散热 {th.QLossW:E2} W");
            Assert.True(relTube <= 1e-9 && relClamp <= 1e-9, $"{(face ? "面上" : "形心")}口径热流与解析值相对差 管孔 {relTube:E2}／压接 {relClamp:E2} > 1e-9");
            Assert.All(Enumerable.Range(0, m.CellCount).Where(i => m.ClampCell[i]), i => Assert.Equal(p.BusbarClampTempC, th.T[i]));
            if (!face)
            {
                double expect = (0.5 * hC) / (xHoleFace - StripLc + 0.5 * hC);
                Assert.True(Math.Abs(relVsFace - expect) <= 1e-9 && relVsFace > 1e-3, $"形心口径与面上解析值相对差 {relVsFace:E6}，应为半格 {expect:E6}");
            }
        }
    }

    // ── (c)(d) 的工作点：第二批 B2 设计片0（出处 R48_第二批保温扫描_B2_2026-09-14.txt），与基线树记录探针同一套调用
    private static (DesignSpec d, LineCase lc, FlangePlate g) B2()
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        return (d, lc, lc.FlangePlates[0]);
    }

    private const double B2I0 = 1072.0, B2TRoot0 = 1139.95;

    private static (ShellCurrentResult sc, double vHash, double jHash) Current(DesignSpec d, LineCase lc, ShellMesh m)
    {
        double tSet = d.SetpointC[0];
        var sc = ShellCurrent.SolveFor(lc, m, B2I0, Materials.PtResistivity(tSet) * 1e3, tSet);
        double vHash = 0, jHash = 0;
        for (int i = 0; i < m.CellCount; i++) { vHash += sc.V[i] * (i + 1); jHash += sc.JMagAPerMm2[i] * (i + 1); }
        return (sc, vHash, jHash);
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（合并）：<paramref name="oldTable"/> = 用「改动前代码」的散热表（设定 + 200 K、60 节点；经 LineRunner.SolvePlateThermal 的只供测试参数）。
    /// 基线树记录是在那张表上跑的，而合并树里散热表上限已改为铂熔点（F 写门时还没有这项改动）⇒ 门 c 逐位比记录时必须钉回旧表，否则比的是「散热表换了」而不是「开关关逐位不变」。
    /// </summary>
    private static (ShellThermalResult th, double tHash) Thermal(LineCase lc, ShellMesh m, double[] j, string mode, bool oldTable = false)
    {
        var ts = LineRunner.PlateThermalInputs(lc, 0, B2I0, null);
        switch (mode)
        {
            case "定温": break;
            case "自由端": ts.P2.BusbarClampTempC = -1; ts.P2.BusbarConductanceWPerK = -1; break;
            case "热导": ts.P2.BusbarClampTempC = -1; ts.P2.BusbarConductanceWPerK = 1.1; break;
            default: throw new ArgumentException(mode);
        }
        var th = oldTable ? LineRunner.SolvePlateThermal(m, j, B2TRoot0, ts, lossTableHiC: ts.P2.TSetC + 200, lossTableNodes: 60)
                          : LineRunner.SolvePlateThermal(m, j, B2TRoot0, ts);
        if (oldTable) { Assert.Equal(ts.P2.TSetC + 200, th.Recipe.LossTableHiC); Assert.Equal(60, th.Recipe.LossTableNodes); }
        double tHash = 0;
        for (int i = 0; i < m.CellCount; i++) tHash += th.T[i] * (i + 1);
        return (th, tHash);
    }

    /// <summary>
    /// face = null ⇒ 缺省入口（不带开关）；否则显式整面接触 + 2026-09-14 的自相似细带（单侧 3·hFine，显式写数：生产缺省 2026-09-15 起不铺）+ 给定的面上开关。
    /// 基线树记录是在「整面 + 自相似细带 + 形心」这个 A 路配方上跑的，要逐位复现就得把细带宽度显式写回去。
    /// </summary>
    private static ShellMesh B2Mesh(LineCase lc, FlangePlate g, string grid, bool? face)
        => (grid, face) switch
        {
            ("导航网格", null) => FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm),
            ("导航网格", bool f) => FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm,
                                                     clampBandMm: 3.0 * lc.MeshFineMm, clampFullFace: true, clampFaceDirichlet: f),
            ("均匀h2", null) => FlangeMesher.Build(g, 0, 2.0, 2.0, 1e6, lc.Base.BusbarClampLengthMm),
            ("均匀h2", bool f) => FlangeMesher.Build(g, 0, 2.0, 2.0, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 3.0 * 2.0, clampFullFace: true, clampFaceDirichlet: f),
            _ => throw new ArgumentException(grid),
        };

    private sealed record Rec(double THash, double QTube, double QClamp, double TTabEnd, double BusLen, double Stab, double StabLat, double QGen, double Resid, double TTabMax, double TDiscMax);

    [Fact]
    public void c_开关关逐位等于改动前代码_开关开抽热不同()
    {
        // 记录：deliverable/R48_压接面上定温_基线树形心口径记录_2026-09-15.txt（改动前代码上跑，R 格式）
        // ★★ 2026-09-18，Fable 5.1（网格生成根因修复）：**基线树记录作废、改为本树记录**。下面这组数不再是「改动前代码」的数 ——
        //   网格生成层改了（解析板精确积分、面长按材料裁剪、留格门槛 25 % → 1e-9、管孔边界 = 圆弧、外角薄片并入邻格；HANDOVER「网格生成 2026-09-18」），
        //   同一块板的格数 1306 → 1326、1966 → 1986，vHash／jHash／发热逐位全变；本门的意思保持不变：**压接面开关关 = 本树老口径逐位**，
        //   开关开抽热不同（非空转）。记录值出处：本树 2026-09-18 19:1x 用同一套调用（B2Mesh／Current／Thermal(oldTable)）打印的 R 格式（临时探针 TMP_记录_c，印完即删）。
        //   旧记录（deliverable/R48_压接面上定温_基线树形心口径记录_2026-09-15.txt）仍是改动前代码的真数，只是不再是本门的靶。
        // ★ 2026-09-19，Fable 5.1（网格修复第二轮，复核第 1 条）：**重录，变因 = J 重构的截断**（ShellCurrent.SliverKappaMin：重构方向条件数 κ′ < 0.2 的格只沿强方向重构）。
        //   电位场一个字没动：格数、压接格数、vHash、归一化电流、Jmax 逐位不变；变的只是盘缘 12（导航）／约 40（均匀 h2）个 J ≈ 1e-3 的碎格的 J ⇒
        //   jHash −2.55／−3.93、发热 −2.3e-10／−2.3e-10 W、热场各量 1e-9～1e-7 量级（tHash、抽热、舌端均温等），都在本次运行 CUR／REC 两行照抄。
        //   2026-09-18 那组记录（jHash 5966857.600154902／14281629.348040197 等）是第一轮改后的数，不再是靶。
        var cur = new Dictionary<string, (int cells, int clamp, double vHash, double jHash, double gen, double iIn, double jMax)>
        {
            ["导航网格"] = (1326, 300, 231177.96593454733, 5966855.046062591, 395.88859371995227, 1.3674804632595619, 14.603356588017592),
            ["均匀h2"] = (1986, 600, 750551.0799378727, 14281625.42106636, 395.88859841161434, 1.3674810795744017, 14.603419970216896),
        };
        var rec = new Dictionary<(string, string), Rec>
        {
            [("导航网格", "定温")] = new(901222668.0345403, 20.80420654168605, 157.58311500728217, 450, 0, 6.937187020761078, 7.107682782174056, 342.3999626593976, -0.0008263666993002516, 1138.907850640168, 1141.736072089707),
            [("导航网格", "自由端")] = new(1005725478.4444997, -21.206489030974627, 0, 874.4790962988949, 0, 7.032293600117668, 7.107682782174056, 395.48635246407224, -0.0014820449002748148, 1202.226359280077, 1165.217404858932),
            [("导航网格", "热导")] = new(879284170.3684318, 27.930782759645997, 162.7696696058644, 172.97242691442233, 7.1697806633603784, 6.921262832589267, 7.107682782174056, 332.85266448562254, -0.0007490978205169085, 1134.1116323244398, 1140.248374566774),
            [("均匀h2", "定温")] = new(1898961064.4670322, 20.810679527121412, 157.6075730907102, 450, 0, 6.937029038616588, 7.107682782174056, 342.22812733663693, -0.000804713836174642, 1138.903269720746, 1141.734568430806),
            [("均匀h2", "自由端")] = new(2234555707.7572303, -21.206389501299178, 0, 875.1540364700546, 0, 7.0321494262766375, 7.107682782174056, 395.3862509491708, -0.0015684073515771502, 1202.225503540798, 1165.217851230469),
            [("均匀h2", "热导")] = new(1813969728.6528518, 28.022500875235785, 162.8797934688639, 173.07253951714884, 7.169884274443488, 6.92091483996859, 7.107682782174056, 332.55570421450483, -0.0036992562932880446, 1134.0518440477217, 1140.230013411673),
        };
        var (d, lc, g) = B2();
        // 2026-09-19 Fable 5.1：逐位比对的不等不再当场炸 —— 先把两张网格 × 三种模式的记录全印出来（CUR／REC 两行照抄即可重录），最后一起判
        var bad = new List<string>();
        void Eq(string what, double exp, double act) { if (!(exp == act)) bad.Add($"{what}：记录 {exp:R} 实际 {act:R}"); }
        foreach (string grid in new[] { "导航网格", "均匀h2" })
        {
            var mOff = B2Mesh(lc, g, grid, false);
            var mOn = B2Mesh(lc, g, grid, true);
            Assert.False(mOff.ClampFaceActive); Assert.True(mOn.ClampFaceActive);
            var c = cur[grid];
            Assert.Equal(c.cells, mOff.CellCount);
            Assert.Equal(c.clamp, mOff.ClampCell.Count(b => b));
            var (scOff, vH, jH) = Current(d, lc, mOff);
            _out.WriteLine($"{grid} 关：vHash {vH:R}　jHash {jH:R}　发热 {scOff.TotalGenW:R}");
            _out.WriteLine($"CUR [\"{grid}\"] = ({mOff.CellCount}, {mOff.ClampCell.Count(b => b)}, {vH:R}, {jH:R}, {scOff.TotalGenW:R}, {scOff.CurrentInA:R}, {scOff.JMaxAPerMm2:R}),");
            Eq($"{grid} vHash", c.vHash, vH); Eq($"{grid} jHash", c.jHash, jH); Eq($"{grid} 发热", c.gen, scOff.TotalGenW);
            Eq($"{grid} 归一化电流", c.iIn, scOff.CurrentInA); Eq($"{grid} Jmax", c.jMax, scOff.JMaxAPerMm2);
            var (scOn, vHOn, _) = Current(d, lc, mOn);
            Assert.NotEqual(c.vHash, vHOn);
            _out.WriteLine($"{grid} 开：发热 {scOn.TotalGenW:R}（关 {scOff.TotalGenW:R}）　归一化电流 {scOn.CurrentInA:R}（关 {scOff.CurrentInA:R}）");
            foreach (string mode in new[] { "定温", "自由端", "热导" })
            {
                var r = rec[(grid, mode)];
                // 2026-09-15 Opus 5（合并）：逐位比基线树记录 ⇒ 用改动前的散热表（见 Thermal 的注释）；门槛（逐位）原样
                var (th, tH) = Thermal(lc, mOff, scOff.JMagAPerMm2, mode, oldTable: true);
                _out.WriteLine($"{grid} {mode} 关：tHash {tH:R}　抽热 {th.QFromTubeW:R}　铜排带走 {th.QToClampW:R}　舌端均温 {th.TTabEndMeanC:R}");
                // 2026-09-19 Fable 5.1：整条记录按 Rec 的字段顺序印成 R 格式，重录时直接抄（不再靠临时探针）
                _out.WriteLine($"REC [(\"{grid}\", \"{mode}\")] = new({tH:R}, {th.QFromTubeW:R}, {th.QToClampW:R}, {th.TTabEndMeanC:R}, {th.BusEquivLenMm:R}, {th.LocalStabMargin:R}, {th.LocalStabLatLenMm:R}, {th.QGenW:R}, {th.EnergyResidualW:R}, {th.TTabMaxC:R}, {th.TDiscMaxC:R}),");
                Assert.True(th.Converged);
                string w = $"{grid} {mode}";
                Eq($"{w} tHash", r.THash, tH); Eq($"{w} 抽热", r.QTube, th.QFromTubeW); Eq($"{w} 铜排带走", r.QClamp, th.QToClampW);
                Eq($"{w} 舌端均温", r.TTabEnd, th.TTabEndMeanC); Eq($"{w} 铜排等效长", r.BusLen, th.BusEquivLenMm);
                Eq($"{w} 稳定裕度", r.Stab, th.LocalStabMargin); Eq($"{w} 稳定横向长", r.StabLat, th.LocalStabLatLenMm);
                Eq($"{w} 发热", r.QGen, th.QGenW); Eq($"{w} 能量残差", r.Resid, th.EnergyResidualW);
                Eq($"{w} 舌区峰", r.TTabMax, th.TTabMaxC); Eq($"{w} 盘区峰", r.TDiscMax, th.TDiscMaxC);
                var (thOn, _) = Thermal(lc, mOn, scOn.JMagAPerMm2, mode, oldTable: true);   // 非空转那半与记录同一张表比（2026-09-15 Opus 5（合并））
                _out.WriteLine($"{grid} {mode} 开：抽热 {thOn.QFromTubeW:R}（差 {thOn.QFromTubeW - th.QFromTubeW:+0.0000;-0.0000} W）　铜排带走 {thOn.QToClampW:R}　舌区峰 {thOn.TTabMaxC:0.000}（差 {thOn.TTabMaxC - th.TTabMaxC:+0.000;-0.000} K）　发热 {thOn.QGenW:0.000}");
                Assert.True(thOn.Converged, $"{grid} {mode} 开：热场没收敛");
                Assert.NotEqual(r.QTube, thOn.QFromTubeW);
            }
        }
        Assert.True(bad.Count == 0, "开关关的记录与本树不逐位相同（" + bad.Count + " 项）：" + string.Join("；", bad));
    }

    [Fact]
    public void e_双舌板局部热稳定锚点逐格取最近压接面_左右镜像结果相同()
    {
        var g = new FlangePlate
        {
            DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
            ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0, TabParallel = true, WeldFilletLegMm = 0, TwoTabs = true,
        };
        const double clampLen = 40.0;
        double xInner = Math.Abs(g.TabTipXMm) - clampLen;                       // 两侧内边 |x|（舌尖 + 压接长）
        var m = FlangeMesher.Build(g, 0, 2.0, 2.0, 1e6, clampLen, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        Assert.True(m.ClampFaceActive);
        // 两侧都有压接面（非空转：双舌板真的是两条内边）
        var set = m.ClampSetCells();
        var faceX = m.Faces.Where(f => ShellMesh.IsClampFace(f, set, out _)).Select(f => f.Mid.X).Distinct().OrderBy(x => x).ToArray();
        _out.WriteLine($"网格 {m.CellCount} 格，压接格 {m.ClampCell.Count(b => b)}，压接面中点 x：{string.Join("、", faceX.Select(x => x.ToString("0.###")))}");
        Assert.Contains(faceX, x => x < 0); Assert.Contains(faceX, x => x > 0);
        // 镜像格对照表（网格须左右对称，否则本门无从比）
        var key = new Dictionary<(long, long), int>();
        for (int i = 0; i < m.CellCount; i++) key[((long)Math.Round(m.Centroid[i].X * 1e6), (long)Math.Round(m.Centroid[i].Z * 1e6))] = i;
        var mirror = new int[m.CellCount];
        for (int i = 0; i < m.CellCount; i++)
        {
            Assert.True(key.TryGetValue(((long)Math.Round(-m.Centroid[i].X * 1e6), (long)Math.Round(m.Centroid[i].Z * 1e6)), out int k), $"格 {i}（x {m.Centroid[i].X}）没有左右镜像格：网格不对称，本门无从比");
            mirror[i] = k;
        }
        var p = new DesignInputs { BusbarClampTempC = 450, TSetC = 1150 };
        double[] J(int sign) => Enumerable.Range(0, m.CellCount)
            .Select(i => Math.Sign(m.Centroid[i].X) == sign && Math.Abs(m.Centroid[i].X) > 120 && Math.Abs(m.Centroid[i].X) < xInner && !m.ClampCell[i] ? 0.5 : 0.0).ToArray();
        var jp = J(+1); var jm = J(-1);
        for (int i = 0; i < m.CellCount; i++) Assert.Equal(jp[i], jm[mirror[i]]);
        Assert.True(jp.Count(v => v > 0) > 100, "给 J 的格太少，本门量不到东西");
        ShellThermalResult S(double[] j) => ShellThermal.Solve(m, j, p, 1150.0, double.NaN, true);
        var rp = S(jp); var rm = S(jm);
        _out.WriteLine($"+x 侧给 J：裕度 {rp.LocalStabMargin:R}　横向导热长 {rp.LocalStabLatLenMm:R} mm　半径 {rp.LocalStabRMm:R} mm　温度 {rp.LocalStabTempC:0.00} °C");
        _out.WriteLine($"−x 侧给 J：裕度 {rm.LocalStabMargin:R}　横向导热长 {rm.LocalStabLatLenMm:R} mm　半径 {rm.LocalStabRMm:R} mm　温度 {rm.LocalStabTempC:0.00} °C");
        Assert.True(rp.Converged && rm.Converged, "热场没收敛");
        Assert.False(double.IsNaN(rp.LocalStabLatLenMm) || double.IsNaN(rm.LocalStabLatLenMm), "局部热稳定判不了，本门量不到东西");
        Assert.Equal(rp.LocalStabLatLenMm, rm.LocalStabLatLenMm, 9);
        Assert.Equal(rp.LocalStabRMm, rm.LocalStabRMm, 9);
        Assert.True(Math.Abs(rp.LocalStabMargin - rm.LocalStabMargin) <= 1e-3 * Math.Abs(rm.LocalStabMargin),
            $"左右镜像的局部热稳定裕度不同：{rp.LocalStabMargin:R} vs {rm.LocalStabMargin:R}");
        foreach (var r in new[] { rp, rm })
            Assert.True(r.LocalStabLatLenMm + 20 < r.LocalStabRMm - g.HoleRadiusMm,
                $"横向导热长 {r.LocalStabLatLenMm:0.00} mm 不比到管孔的距离短 —— 锚点退回了管孔，没取到本侧压接面");
    }

    [Fact]
    public void d_缺省入口是面上_压接面上的电流与热流对得上求解器()
    {
        var (d, lc, g) = B2();
        var mDef = B2Mesh(lc, g, "导航网格", null);
        Assert.True(mDef.ClampFaceDirichlet && mDef.ClampFaceActive, "Build 缺省入口不是面上口径");
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        Assert.True(mF.ClampFaceActive, "BuildFromField 缺省入口不是面上口径");
        // 老口径（只钉外圈）上开关不激活：ClampCell 空
        var mRing = FlangeMesher.Build(g, 0, 2.0, 2.0, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false, clampFaceDirichlet: true);
        Assert.True(mRing.ClampFaceDirichlet && !mRing.ClampFaceActive);

        foreach (string grid in new[] { "导航网格", "均匀h2" })
        {
            var m = B2Mesh(lc, g, grid, null);
            var (sc, _, _) = Current(d, lc, m);
            Assert.True(sc.Converged && sc.ConservationError < 1e-6, $"{grid}：电流场没收敛或不守恒 {sc.ConservationError:E2}");
            var clamp = Enumerable.Range(0, m.CellCount).Where(i => m.ClampCell[i]).ToArray();
            Assert.All(clamp, i => Assert.Equal(1.0, sc.V[i]));
            Assert.All(clamp, i => Assert.Equal(0.0, sc.JMagAPerMm2[i]));
            var set = m.ClampSetCells();
            // 穿过压接面的归一化电流（等温 σ = 1）= 求解器总电流：逐面用生产的面判定与距离重算
            double iFace = 0; int nFace = 0;
            foreach (var fc in m.Faces)
                if (ShellMesh.IsClampFace(fc, set, out int fr))
                { iFace += m.Thickness[fr] * fc.Length / m.CentroidToFaceMm(fr, fc) * (1.0 - sc.V[fr]); nFace++; }
            _out.WriteLine($"{grid}：压接格 {clamp.Length}　压接面 {nFace}　穿过压接面的归一化电流 {iFace:R}　求解器总电流 {sc.CurrentInA:R}");
            Assert.True(nFace > 0);
            Assert.True(Math.Abs(iFace - sc.CurrentInA) <= 1e-12 * Math.Abs(sc.CurrentInA), $"{grid}：穿过压接面的电流 {iFace:R} ≠ 总电流 {sc.CurrentInA:R}");

            var (th, _) = Thermal(lc, m, sc.JMagAPerMm2, "定温");
            double tClamp = lc.ClampTempC[0];
            Assert.True(th.Converged);
            Assert.All(clamp, i => Assert.Equal(tClamp, th.T[i]));
            Assert.True(Math.Abs(th.EnergyResidualW) < 1e-3 * Math.Max(1, th.QGenW), $"{grid}：能量不闭合 {th.EnergyResidualW:0.0000} W");
            // 铜排带走 = 穿过压接面的热流（面导度 k(T_自由)·t·L/距离，与孔边 gHole 同一写法）
            double qFace = 0;
            foreach (var fc in m.Faces)
                if (ShellMesh.IsClampFace(fc, set, out int fr))
                    qFace += Materials.PtThermalK(th.T[fr]) * 1e-3 * m.Thickness[fr] * fc.Length / m.CentroidToFaceMm(fr, fc) * (th.T[fr] - tClamp);
            _out.WriteLine($"{grid}：铜排带走 {th.QToClampW:R} W　穿过压接面 {qFace:R} W　抽热 {th.QFromTubeW:0.0000} W　残差 {th.EnergyResidualW:0.000000} W");
            Assert.True(Math.Abs(qFace - th.QToClampW) <= 1e-9 * Math.Abs(th.QToClampW), $"{grid}：铜排带走 {th.QToClampW:R} ≠ 穿过压接面的热流 {qFace:R}");
        }
    }

}
