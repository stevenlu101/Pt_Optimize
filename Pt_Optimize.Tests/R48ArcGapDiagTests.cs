using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  §0.-20（2026-09-23）：图纸（栅格）路径的**管孔弧覆盖诊断**与**图纸孔径核对**（F6 审查 #11／#12；F3 核实记录 A 项）。
//  诊断在 FlangeMesher.BuildFromMaterialWith 建完弧面之后量（ShellMesh.MeasureHoleArcCoverage → HoleArcCoverage／HoleArcMaxGapMm／HoleArcMaxGapMidDeg／HoleArcGapCount），
//  经 LineRunner.HoleArcDrawingNotes 进整线结果说明。**只量不判**：不改任何判词，不把任何判据标成判不了（业主决定，见 HANDOVER §0.-20 待决定）。
//
//  阈值与记录值（出处逐条写明）：
//    · 缺口的定义：弧长 > ShellMesh.GeomTolMm（= 建面边长容差 EdgeTolMm = 1e-7 mm，沿用已有值；诊断里没有新常数）。
//    · 门 a 记录值：合计缺 2.141 mm —— F3 实测，deliverable/R48_F6_审查探针输出_2026-09-23/geom_probe/out1.txt 第 6 行「弧长 159.966/2πrh 162.106（缺 2.141）」
//      （改前代码同值：f3probe_base/out_base.txt 第 4 行）；缺口段数 3（θ = 180° 一段 + ±157.2° 两段，F3 核实记录 A1(2)）。
//      最长缺口 = 2·rh·asin(1/rh)（数学推出：丢掉的是 z ∈ [−1, 1] 那一格，孔弧在 z = ±1 两条格线之间的那段；F3 记为「约 2.00 mm」），容差 1e-9 mm（选定：浮点余量）。
//      中点角在 180° ± 一个栅格步对应的角度 step/rh（rad）之内（数学推出：栅格原点挪一步，缺口最多挪一步）。
//    · 门 b：解析路径覆盖率 = 1，容差 1e-9（选定：浮点余量；Σ 约 200 条弧面长的舍入在 1e-14 量级，1e-9 只是留足）；缺口段数 0。
//    · 门 c 记录值：不接首尾时报出的最长缺口 0.070 mm —— 审查者探针（它的扫描不接 ±180°）实印「弧缺口 θ ±157.195°～±157.351° 长 0.0700 mm」
//      （scratchpad review_f6/refute_gap/detail.txt，F3 核实记录 A1(2) 引用；那份文件在会话暂存区，没入库，3 位小数的数在 F3 记录里）。
//    · 门 d：孔径失配容差 = 一个栅格步（F3 核实记录 A4 第 4 条：栅格分辨不出小于一步的差）；等面积半径的误差界 s/√2（数学推出，见 PlateShapeAnalyzer.HoleRadiusOf），
//      失配量 Δ 报出值须满足 |报出差 − Δ| ≤ s/√2。几何 = **W08 默认**（F3 失配探针 MISMATCH 那组不调盘径／舌半宽；写门时先按 R31 w30 跑、对不上 F3 数，查探针源码才知道几何不同 ——
//      改的是几何，不是记录值）。孔弧合计缺口对 F3 实测：rh+0.3 判决档 33.646 mm、rh+1.0 判决档 133.742 mm
//      （deliverable/R48_F6_审查探针输出_2026-09-23/geom_probe/out_mismatch.txt「W08 判决 图纸孔半径 = rh+0.3／+1.0 … 生产」两行）。
//
//  覆盖：诊断在已知缺口几何上会响、报出的位置对、跨 ±180° 的缺口能量到（改回不接首尾 ⇒ 门红）；解析路径不误报；
//        孔径失配时说明句出现、数对；孔径与 rh 相同时不误报；LineRunner 整线说明真的带出这两句（门 f，图纸路径小算例）。
//  不覆盖：真 .3dm 图纸的栅格原点（缺不缺取决于原点，无法事先排除）；缺口与失配对各判据量的定量影响（F3 A1(5)／(6) 只在 W08 R31 w30 上量过）；
//        导航档（栅格步 0.5）上 rh+0.3 的失配**不报**（0.3 < 0.5，门只印）；孔心偏离管轴（图纸孔不在原点）没核；
//        说明句进了 res.Notes，没挂到 · 法兰 J_max 的判据附注上，判词与判不了一个没动。
// ════════════════════════════════════════════════════════════════════════════
public class R48ArcGapDiagTests
{
    private readonly ITestOutputHelper _o;
    public R48ArcGapDiagTests(ITestOutputHelper o) { _o = o; }

    const double RecTotalGapMm = 2.141;       // F3 out1.txt 第 6 行
    const int RecGapCount = 3;                // F3 核实记录 A1(2)
    const double RecNoWrapGapMm = 0.070;      // 审查者探针（不接首尾）实印 0.0700
    const double FloatTol = 1e-9;             // 选定：浮点余量

    /// <summary>
    /// W08 判决档（fine 1）：解析网格、栅格替身（图纸孔半径 = rh + <paramref name="dHole"/>）与生产入口 BuildFromField 建的图纸网格。与 F3 geom 探针同一做法
    /// （review_f6/geom/probe/Program.cs：缺口那组是 SetRW(31, 30)，失配那组 MISMATCH 是 **W08 默认几何**、不调 SetRW；<paramref name="r31"/> 选哪一组）。
    /// </summary>
    static (LineCase lc, double rh, double step, ShellMesh analytic, ThicknessField tf, ShellMesh drawing) W08R31(double fineMm = 1.0, double dHole = 0.0, bool r31 = true)
    {
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design("W08");
        if (r31) R48NMeshGateTests.SetRW(d, 31.0, 30.0);
        var (lc, g, mA, _) = R48NMeshGateTests.Build(d, p, fineMm);
        double rh = g.HoleRadiusMm;
        double hFinest = lc.MeshInnerMm > 1e-9 ? Math.Min(lc.MeshFineMm, lc.MeshInnerMm) : lc.MeshFineMm;
        double step = Math.Min(lc.ThicknessStepMm, FlangeMesher.RasterStepForFile(hFinest));   // LineRunner 图纸路径取栅格步的同一式
        g.HoleRadiusMm = rh + dHole;
        var tf = FlangeMesher.Rasterize(g, step);
        g.HoleRadiusMm = rh;
        var mD = FlangeMesher.BuildFromField(tf, rh, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                             lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);   // LineRunner 图纸路径的同一调用
        return (lc, rh, step, mA, tf, mD);
    }

    /// <summary>门 a 的判法（抽出来，门 c 拿「不接首尾」的量法套同一个判法，必须红）。</summary>
    static List<string> GateA((double Coverage, double MaxGapMm, double MaxGapMidDeg, int GapCount) r, double rh, double step)
    {
        var bad = new List<string>();
        double total = (1 - r.Coverage) * 2 * Math.PI * rh;
        if (Math.Round(total, 3) != RecTotalGapMm) bad.Add($"合计缺口 {total:0.000000} mm，记录 {RecTotalGapMm:0.000}");
        if (r.GapCount != RecGapCount) bad.Add($"缺口段数 {r.GapCount}，记录 {RecGapCount}");
        double expMax = 2 * rh * Math.Asin(1 / rh);
        if (!(Math.Abs(r.MaxGapMm - expMax) <= FloatTol)) bad.Add($"最长缺口 {r.MaxGapMm:0.000000000} mm，推出 2·rh·asin(1/rh) = {expMax:0.000000000}");
        double tolDeg = step / rh * 180 / Math.PI;
        double off = Math.Abs(((r.MaxGapMidDeg - 180) % 360 + 540) % 360 - 180);   // 到 180° 的角距离（±180° 视为同一点）
        if (!(off <= tolDeg)) bad.Add($"缺口中点 {r.MaxGapMidDeg:0.000}°，离 180° {off:0.000}°，容许 {tolDeg:0.000}°（一个栅格步 {step} mm 对应的角）");
        return bad;
    }

    // ───────────────────────────── a ─────────────────────────────
    [Fact]
    public void 门a_判决档栅格替身_诊断报出缺口与位置_说明句带出()
    {
        var (_, rh, step, _, tf, mD) = W08R31();
        Assert.Equal(0.25, step);
        var r = (mD.HoleArcCoverage, mD.HoleArcMaxGapMm, mD.HoleArcMaxGapMidDeg, mD.HoleArcGapCount);
        _o.WriteLine($"W08 R31 w30 判决档栅格替身（步 {step}）：覆盖率 {r.Item1:0.000000}　合计缺 {(1 - r.Item1) * 2 * Math.PI * rh:0.000000} mm　最长缺口 {r.Item2:0.000000} mm @ {r.Item3:0.000}°　段数 {r.Item4}");
        Assert.Equal(ShellMesh.MeasureHoleArcCoverage(mD), r);   // 字段 = 量法（生产入口真的调了）
        var bad = GateA(r, rh, step);
        Assert.True(bad.Count == 0, string.Join("；", bad));

        var notes = LineRunner.HoleArcDrawingNotes(mD, tf, rh);
        foreach (var n in notes) _o.WriteLine("说明：" + n);
        Assert.Equal("180", LineRunner.HoleArcAngleText(r.Item3));
        string gapLine = $"图纸路径管孔弧缺 {r.Item2:0.000} mm @ θ 180°，覆盖率 {r.Item1:0.0000}";
        Assert.Contains(notes, n => n.StartsWith(gapLine, StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains($"合计 {(1 - r.Item1) * 2 * Math.PI * rh:0.000} mm", StringComparison.Ordinal));
        // 孔径与 rh 相同：不误报失配（误差界 s/√2 < s）
        double rDraw = PlateShapeAnalyzer.HoleRadiusOf(tf);
        _o.WriteLine($"图纸孔半径（等面积）{rDraw:0.000000} mm，rh {rh}，差 {rDraw - rh:+0.000000;-0.000000}，误差界 s/√2 = {step / Math.Sqrt(2):0.000}");
        Assert.True(Math.Abs(rDraw - rh) <= step / Math.Sqrt(2));
        Assert.DoesNotContain(notes, n => n.Contains("图纸孔半径", StringComparison.Ordinal));
    }

    // ───────────────────────────── b ─────────────────────────────
    [Fact]
    public void 门b_同几何解析路径_覆盖率为1_不误报()
    {
        foreach (double fine in new[] { 1.0, 0.0 })
        {
            var (_, rh, _, mA, _, _) = W08R31(fine);
            _o.WriteLine($"W08 R31 w30 {(fine == 0 ? "导航" : "判决")}档解析：覆盖率 {mA.HoleArcCoverage:R}　|覆盖率 − 1| = {Math.Abs(mA.HoleArcCoverage - 1):E2}　缺口段数 {mA.HoleArcGapCount}　最长 {mA.HoleArcMaxGapMm:E2} mm");
            Assert.True(Math.Abs(mA.HoleArcCoverage - 1) <= FloatTol, $"解析路径覆盖率 {mA.HoleArcCoverage:R} 离 1 超过 {FloatTol}");
            Assert.Equal(0, mA.HoleArcGapCount);
            Assert.Equal(0.0, mA.HoleArcMaxGapMm);
            Assert.True(double.IsNaN(mA.HoleArcMaxGapMidDeg));
            Assert.Empty(LineRunner.HoleArcDrawingNotes(mA, null, rh));
        }
        // 阶梯孔边对照（弧面关）不量：字段留「没量」
        var (lc0, _, _, _, _, _) = W08R31(0.0);
        var mStep = LineRunner.PlateMeshAnalyticWith(lc0, 0, new MeshRules { HoleArcFaces = false }, null);
        Assert.True(double.IsNaN(mStep.HoleArcCoverage) && mStep.HoleArcGapCount == -1);
    }

    // ───────────────────────────── c ─────────────────────────────
    [Fact]
    public void 门c_改回不接首尾_最长缺口变成0p070_门红()
    {
        var (_, rh, step, _, _, mD) = W08R31();
        var off = ShellMesh.MeasureHoleArcCoverage(mD, joinAt180: false);
        _o.WriteLine($"不接首尾：覆盖率 {off.Coverage:0.000000}　最长缺口 {off.MaxGapMm:0.000000} mm @ {off.MaxGapMidDeg:0.000}°　段数 {off.GapCount}");
        Assert.Equal(RecNoWrapGapMm, Math.Round(off.MaxGapMm, 3));
        var bad = GateA(off, rh, step);
        foreach (var b in bad) _o.WriteLine("门 a 判法对「不接首尾」：" + b);
        Assert.True(bad.Count > 0, "不接首尾时门 a 没红 —— 门空守");
        Assert.Contains(bad, b => b.StartsWith("最长缺口", StringComparison.Ordinal));
    }

    // ───────────────────────────── d ─────────────────────────────
    [Theory]
    [InlineData(0.3, 33.646)]
    [InlineData(1.0, 133.742)]
    public void 门d_图纸孔径失配_说明句出现_数值对(double dHole, double recTotalGapMm)
    {
        var (_, rh, step, _, tf, mD) = W08R31(1.0, dHole, r31: false);   // F3 失配探针用的是 W08 默认几何
        double totalGap = (1 - mD.HoleArcCoverage) * 2 * Math.PI * rh;
        _o.WriteLine($"W08 默认几何，图纸孔半径 = rh + {dHole}（判决档，步 {step}）：孔弧合计缺 {totalGap:0.000} mm（F3 记录 {recTotalGapMm:0.000}）　最长 {mD.HoleArcMaxGapMm:0.000} mm @ {mD.HoleArcMaxGapMidDeg:0.0}°");
        Assert.Equal(recTotalGapMm, Math.Round(totalGap, 3));

        // 测试侧独立数：孔圆 rh + dHole 内的栅格点数 ⇒ 等面积半径（与 HoleRadiusOf 的定义相同，这里不经空腔填充、直接按圆数点）
        double rTrue = rh + dHole; int nIn = 0;
        for (int i = 0; i < tf.Nx; i++)
            for (int j = 0; j < tf.Nz; j++)
            {
                double x = tf.X0 + i * tf.Step, z = tf.Z0 + j * tf.Step;
                if (x * x + z * z < rTrue * rTrue) nIn++;
            }
        double rEq = Math.Sqrt(nIn * (tf.Step * tf.Step) / Math.PI), diff = rEq - rh;
        _o.WriteLine($"测试侧：孔内栅格点 {nIn}，等面积半径 {rEq:0.000000}，对 rh 差 {diff:+0.000000}；误差界 |差 − {dHole}| ≤ s/√2 = {step / Math.Sqrt(2):0.000}");
        Assert.Equal(rEq, PlateShapeAnalyzer.HoleRadiusOf(tf), 12);
        // LineRunner 按场实例缓存（外层耦合每轮都走说明句）：第一次算、第二次命中，都等于直算
        Assert.Equal(PlateShapeAnalyzer.HoleRadiusOf(tf), LineRunner.DrawingHoleRadiusCached(tf));
        Assert.Equal(PlateShapeAnalyzer.HoleRadiusOf(tf), LineRunner.DrawingHoleRadiusCached(tf));
        Assert.True(Math.Abs(diff - dHole) <= step / Math.Sqrt(2));

        var notes = LineRunner.HoleArcDrawingNotes(mD, tf, rh);
        foreach (var n in notes) _o.WriteLine("说明：" + n);
        Assert.Contains(notes, n => n.StartsWith($"图纸孔半径 {rEq:0.000} mm", StringComparison.Ordinal)
                                  && n.Contains($"rh = 管内径/2 + 壁厚 = {rh:0.000} mm 差 {diff:+0.000;-0.000} mm", StringComparison.Ordinal)
                                  && n.Contains($"超过一个栅格步 {step:0.###} mm", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith($"图纸路径管孔弧缺 {mD.HoleArcMaxGapMm:0.000} mm", StringComparison.Ordinal));

        // 只印：导航档（步 0.5）上同样的失配报不报（0.3 < 0.5 时不报，写在头注的不覆盖里）
        var nav = W08R31(0.0, dHole, r31: false);
        double rNav = PlateShapeAnalyzer.HoleRadiusOf(nav.tf);
        bool navFlag = LineRunner.HoleArcDrawingNotes(nav.drawing, nav.tf, nav.rh).Any(n => n.Contains("图纸孔半径", StringComparison.Ordinal));
        _o.WriteLine($"（只印）导航档步 {nav.step}：等面积半径 {rNav:0.000}，差 {rNav - nav.rh:+0.000}，{(navFlag ? "报失配" : "不报失配（差不超过一个栅格步）")}");
    }

    // ───────────────────────────── f ─────────────────────────────
    /// <summary>
    /// 整线说明真的带出这两句（LineRunner 接线）：门 1（R48PropsWiringGateTests）的整线小算例改走图纸路径（内存厚度场，AnalyticSurrogate.Rasterize 0.5／留白 2，
    /// 与 R48RecipeFingerprintTests 图纸路径同一做法），图纸孔半径 = rh + 1.0 ⇒ 每片都要有失配句与孔弧缺口句；孔径 = rh 的对照不许有失配句（缺口句有没有看栅格原点，只印）。
    /// 覆盖：LineRunner.Run 图纸路径那一支调了 HoleArcDrawingNotes 并按「⚠ 片名：」写进 res.Notes。不覆盖：文件路径（Geometry3dm.LoadThickness）那一支的栅格；判词（按规矩不动，也不查）。
    /// </summary>
    [Fact]
    public void 门f_整线说明带出失配句与缺口句_图纸路径小算例()
    {
        foreach (double dHole in new[] { 1.0, 0.0 })
        {
            var lc = R48PropsWiringGateTests.QuickCase();
            double rh = lc.TubeIdMm * 0.5 + lc.WallMm;
            var plates = lc.FlangePlates;
            lc.FlangeFields = plates.Select(pl =>
            {
                pl.HoleRadiusMm = rh + dHole; var f = AnalyticSurrogate.Rasterize(pl, 0.5, 2.0); pl.HoleRadiusMm = rh; return f;
            }).ToArray();
            lc.GeomForJudge = plates;
            lc.TabInsul3dmPerPlateMm = plates.Select(pl => pl.TabInsulThickMm).ToArray();
            lc.FlangePlates = Array.Empty<FlangePlate>();
            var r = LineRunner.Run(lc);
            Assert.True(r.Ok, r.Message);
            var mine = r.Notes.Where(n => n.Contains("管孔弧缺", StringComparison.Ordinal) || n.Contains("图纸孔半径", StringComparison.Ordinal)).ToList();
            _o.WriteLine($"图纸孔半径 = rh + {dHole}：{r.Flanges.Length} 片，本诊断的说明 {mine.Count} 句");
            foreach (var n in mine) _o.WriteLine("  " + n);
            foreach (var fo in r.Flanges)
            {
                if (dHole > 0)
                {
                    Assert.Contains(mine, n => n.StartsWith("⚠ " + fo.Name + "：图纸孔半径 ", StringComparison.Ordinal));
                    Assert.Contains(mine, n => n.StartsWith("⚠ " + fo.Name + "：图纸路径管孔弧缺 ", StringComparison.Ordinal));
                }
                else Assert.DoesNotContain(mine, n => n.Contains("图纸孔半径", StringComparison.Ordinal));
            }
        }
    }
}
