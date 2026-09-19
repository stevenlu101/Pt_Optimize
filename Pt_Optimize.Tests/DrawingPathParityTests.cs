using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 的**验收门**（工单 §3「两条路对得上」）。
///
/// ★ R47 复修 M5（2026-09-13）：验收门不许同义反复 —— Build 现在就是 BuildFromField(Rasterize)，两者「逐位相同」是构造保证，
/// 不是验证。所以加**第三方参照**：R47 之前的精确几何生成器（4×4 子采样点上判 Inside、形心取厚，
/// <see cref="R47MeshDiagInstrumentTests.BuildExactOnNewAxis"/>）铺在同一套新轴（GradedAxisCentered + 锚点）上。钉：
///   · Build（栅格积分）vs 精确几何：导航网格上抽热差 &lt; 1 W、发热差 &lt; 1 %；网格倍率 0.5 上仍 &lt; 1 W；
///   · 图纸路径 BuildFromField(Rasterize 留白 2) vs 精确几何：栅格步 = 网格/4（生产口径）、生产旧默认 1.0 mm、0.1 mm 各一档。
/// 两个案例：盘Ø56 片 1（Pt_Topo meshcmp 口径：1572.1 A、管根 1107.8 °C、夹持 450、控温 1080、舌保温 2.8）
/// 与 DesignSpec.Builtin[0] 片 0（1213.7 A、管根 1146.4、控温 1150、档的夹持温度与舌保温）。
/// 审查量出的（M1 轴锚点之前）：盘Ø56 片 1 精确 4.101/−3.443/−0.231 vs Build 4.996/−3.250/−0.263 W（×1／×0.5／×0.25）；
/// Builtin[0] 片 0 精确 13.258/12.746/13.744 vs Build 12.454/12.660/13.754 W。改前（隔离实验 V0 vs V2）：3.41 W vs 26.4 W。
///
/// ★ R47 第三轮 N4（2026-09-13）：**导航网格上那 0.9 W 是什么** —— 两种生成器的**方法差**，不是几何差：
///   精确几何生成器在每个单元 4×4 子采样点上判 Inside、**形心一点取厚**（焊脚这种比格子细的堆料在 2 mm 格上几乎量不到）；
///   栅格积分生成器对栅格方格做**面积积分**、厚度 = 体积积分 ÷ 覆盖面积（焊脚按料算）。两者随网格加密收敛到同一个数：
///   盘Ø56 片 1 差 ×1 0.90 W → ×0.5 0.19 W → ×0.25 0.03 W；Builtin[0] 片 0 ×1 0.80 → ×0.5 0.09 → ×0.25 0.01 W。
///   所以门不是「一个数」而是「随倍率收敛」：倍率 1 差 &lt; 1 W **且**倍率 0.5 差 &lt; 0.3 W —— 只钉 ×1 抓不到「差不收敛」这种病。
/// </summary>
public class DrawingPathParityTests
{
    internal static (double QGen, double QFromTube, int Cells) SolveOne(ShellMesh m, LineCase lc, FlangePlate g,
                                                                     double iA, double tRoot, double tSet, double clampC, double tabInsul)
    {
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet; p2.BusbarClampTempC = clampC;
        var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
        Assert.True(sc.Converged, "电流场没收敛");
        var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved, false,
                                    tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabInsul);
        Assert.True(th.Converged, "温度场没收敛");
        return (th.QGenW, th.QFromTubeW, m.CellCount);
    }

    /// <summary>两个案例：0 = 盘Ø56 片 1（meshcmp 口径），1 = Builtin[0] 片 0。</summary>
    internal static (FlangePlate g, LineCase lc, double iA, double tRoot, double tSet, double clampC, double tabInsul, string name) Case(int which)
    {
        if (which == 0)
        {
            var g = MeshAxisTests.Disc56Plate1(out var lc);
            return (g, lc, 1572.1, 1107.8, 1080, 450, 2.8, "盘Ø56 片1");
        }
        var p = new DesignInputs();
        var d0 = DesignSpec.Builtin[0].Clone();
        var lc0 = d0.BuildCase(p);
        var g0 = d0.Plate(0, d0.DiscFloorMm(p));
        g0.HoleRadiusMm = lc0.TubeIdMm * 0.5 + lc0.WallMm;
        return (g0, lc0, 1213.7, 1146.4, 1150, d0.ClampTempC, d0.TabInsulMm[0], "Builtin[0] 片0");
    }

    [Trait("速度", "慢")]
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(0, 0.5)]
    [InlineData(1, 1.0)]
    [InlineData(1, 0.5)]
    public void 精确几何_栅格积分_图纸路径三方在同一轴上抽热差倍率1小于1W倍率0p5小于0p3W发热差小于1percent(int which, double meshScale)
    {
        var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
        double hF = lc.MeshFineMm * meshScale, hC = lc.MeshCoarseMm * meshScale, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;

        var mE = R47MeshDiagInstrumentTests.BuildExactOnNewAxis(g, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var mA = FlangeMesher.Build(g, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        // 图纸路径：栅格步 = 网格/4（生产口径，与 LineRunner 同）；留白 2（病发时的图幅）
        double step = FlangeMesher.RasterStepFor(hF);
        var f = AnalyticSurrogate.Rasterize(g, step, 2.0);
        var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);

        var e = SolveOne(mE, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        var a = SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        var b = SolveOne(mF, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        Console.WriteLine($"{name} 倍率 {meshScale}：精确几何 {e.Cells} 格 发热 {e.QGen:0.0} W 抽热 {e.QFromTube:0.000} W；" +
                          $"Build 栅格积分 {a.Cells} 格 发热 {a.QGen:0.0} W 抽热 {a.QFromTube:0.000} W；图纸(步 {step}) {b.Cells} 格 发热 {b.QGen:0.0} W 抽热 {b.QFromTube:0.000} W");
        // 三方同一套轴 ⇒ 节点数相同
        Assert.Equal(mE.Nodes.Count, mA.Nodes.Count);
        Assert.Equal(mA.Nodes.Count, mF.Nodes.Count);
        // ① 第三方参照：Build vs 精确几何 —— R47 第三轮 N4：倍率 1 差 < 1 W，倍率 0.5 差 < 0.3 W（方法差随网格收敛）
        double tolQ = meshScale <= 0.5 + 1e-9 ? 0.3 : 1.0;
        Assert.True(Math.Abs(e.QFromTube - a.QFromTube) < tolQ,
            $"Build vs 精确几何 抽热差 {Math.Abs(e.QFromTube - a.QFromTube):0.000} W ≥ {tolQ} W（倍率 {meshScale}；精确 {e.QFromTube:0.000}，Build {a.QFromTube:0.000}）");
        Assert.True(Math.Abs(e.QGen - a.QGen) / e.QGen < 0.01,
            $"Build vs 精确几何 发热差 {Math.Abs(e.QGen - a.QGen) / e.QGen * 100:0.00} % ≥ 1 %（精确 {e.QGen:0.0}，Build {a.QGen:0.0}）");
        // ② 图纸路径 vs 精确几何（不是 vs Build —— 那是同义反复）；同一把尺：×1 < 1 W、×0.5 < 0.3 W
        Assert.True(Math.Abs(e.QFromTube - b.QFromTube) < tolQ,
            $"图纸 vs 精确几何 抽热差 {Math.Abs(e.QFromTube - b.QFromTube):0.000} W ≥ {tolQ} W（倍率 {meshScale}；精确 {e.QFromTube:0.000}，图纸 {b.QFromTube:0.000}）");
        Assert.True(Math.Abs(e.QGen - b.QGen) / e.QGen < 0.01,
            $"图纸 vs 精确几何 发热差 {Math.Abs(e.QGen - b.QGen) / e.QGen * 100:0.00} % ≥ 1 %（精确 {e.QGen:0.0}，图纸 {b.QGen:0.0}）");
    }

    /// <summary>
    /// 图纸侧再加两档栅格步（都不是 Build 用的 h/4）：0.1 mm 对精确几何要 &lt; 1 W／&lt; 1 %；
    /// 生产旧默认 1.0 mm **只记录、并自证它不够**：盘Ø56 片 1（焊脚 1.02 mm、环宽 2.2 mm）实测 2.298 vs 精确 4.101 W（偏 1.8 W）——
    /// 1 mm 栅格只在焊脚斜坡上采到一个点，积不准；这正是生产口径改成「最细网格/4」的依据。若哪天 1.0 也对得上，这条会红，提醒改这段话。
    /// </summary>
    [Trait("速度", "慢")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 图纸路径栅格步0p1对精确几何抽热差小于1W_旧默认1p0记录并自证不够(int which)
    {
        var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
        double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
        var mE = R47MeshDiagInstrumentTests.BuildExactOnNewAxis(g, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var e = SolveOne(mE, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        var sb = new System.Text.StringBuilder();
        double Diff(double rasterStep, out (double QGen, double QFromTube, int Cells) b)
        {
            var f = AnalyticSurrogate.Rasterize(g, rasterStep, 2.0);
            var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
            b = SolveOne(mF, lc, g, iA, tRoot, tSet, clampC, tabInsul);
            string line = $"{name} 栅格步 {rasterStep}：精确几何 {e.Cells} 格 发热 {e.QGen:0.0} W 抽热 {e.QFromTube:0.000} W；图纸 {b.Cells} 格 发热 {b.QGen:0.0} W 抽热 {b.QFromTube:0.000} W（差 {b.QFromTube - e.QFromTube:+0.000;-0.000} W）";
            Console.WriteLine(line); sb.AppendLine(line);
            return Math.Abs(b.QFromTube - e.QFromTube);
        }
        double d10 = Diff(1.0, out var b10), dH4 = Diff(FlangeMesher.RasterStepFor(hF), out _), d01 = Diff(0.1, out var b01);
        System.IO.File.AppendAllText(System.IO.Path.Combine(HandoverDoc.Root(), "deliverable", "R47_复修M5_栅格步对拍_2026-09-13.txt"),
                                     $"{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{sb}", new System.Text.UTF8Encoding(false));
        Assert.True(d01 < 1.0, $"栅格步 0.1：抽热差 {d01:0.000} W ≥ 1 W（精确 {e.QFromTube:0.000}，图纸 {b01.QFromTube:0.000}）");
        Assert.True(Math.Abs(e.QGen - b01.QGen) / e.QGen < 0.01, $"栅格步 0.1：发热差 {Math.Abs(e.QGen - b01.QGen) / e.QGen * 100:0.00} % ≥ 1 %");
        Assert.True(dH4 < 1.0, $"栅格步 网格/4：抽热差 {dH4:0.000} W ≥ 1 W");
        if (which == 0)
            Assert.True(d10 > 1.0 && d10 > dH4, $"盘Ø56 片 1 旧默认栅格步 1.0 本该偏 > 1 W（实测 2026-09-13 偏 1.803 W），现在只偏 {d10:0.000} W（网格/4 偏 {dH4:0.000}）—— 把这段话改掉");
    }
}
