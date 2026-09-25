using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 的**验收门**（工单 §3「两条路对得上」）—— ★ 2026-09-19，Fable 5.1（网格修复第二轮复核第 3 条）**重定靶**。
///
/// 原靶（R47 复修 M5／第三轮 N4，2026-09-13）：以测试侧「精确几何」生成器（<see cref="R47MeshDiagInstrumentTests.BuildExactOnNewAxis"/>：4×4 子采样判 Inside、形心一点取厚）
/// 为第三方参照，钉 Build（当时 = 栅格积分）与图纸路径对它的抽热差 &lt; 1 W／0.3 W、发热差 &lt; 1 %，并钉三方节点数相同。
/// 为什么改靶（2026-09-18 网格修复之后 6/6 红，改前快照上也红）：
///   ① 那份「精确几何」不再是参照：它是 R47 之前的老生成器，只钉外圈电极（不带 R48 的压接整面接触与压接面上定电位），发热比生产 Build 高 60～130 W（12～28 %）、
///      抽热差 2.1～21 W —— 差的是**压接模型**，不是几何；2026-09-18 起生产 Build 走解析板精确积分（F1），它本身就是精确几何，再拿一个粗的老生成器当参照是倒过来的；
///   ② 三方节点数：解析路径的锚点由板件精确给（AnchorsOf）、图纸路径从栅格推（AnchorsFromField），两条路的轴不由构造保证逐位相同（1508 ≠ 1537 就是压接锚点那一列），
///      节点数不是这道门要守的东西 —— 要守的是两条路算出来的**数**对不对得上。
/// 新靶（复核者三方对拍已量到：栅格步 0.1 mm 时 图纸 − Build 发热 0.00 %、抽热 +0.019／+0.086 W）：
///   · 图纸路径（栅格）对生产 Build（解析板精确积分）：栅格步 0.1 mm 时 发热差 ≤ 0.1 %、抽热差 ≤ 0.1 W；
///   · 生产口径的栅格步（最细网格/4）：发热差 ≤ 0.1 %、抽热差 ≤ 1 W（图纸路径的格点采样在直边两侧各有半个栅格步的幻影，见 HANDOVER §0.-15N ⑥；这里记录它有多大），
///     并且抽热差随栅格步 1.0 → 网格/4 → 0.1 单调收窄（差不收敛才是病）；
///   · 老生成器的数只印（沿革），不再断言。
/// 两个案例照旧：盘Ø56 片 1（Pt_Topo meshcmp 口径：1572.1 A、管根 1107.8 °C、夹持 450、控温 1080、舌保温 2.8）与 DesignSpec.Builtin[0] 片 0（1213.7 A、管根 1146.4、控温 1150）。
/// </summary>
public class DrawingPathParityTests
{
    // 跑前写死（2026-09-19）
    const double GenTolPct = 0.1;       // 发热差
    const double TubeTolFineW = 0.1;    // 栅格步 0.1 mm 的抽热差
    const double TubeTolProdW = 1.0;    // 生产口径栅格步（网格/4）的抽热差

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
    public void 图纸路径对生产Build_生产栅格步_发热差小于0p1percent_抽热差小于1W(int which, double meshScale)
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
        double dGen = Math.Abs(b.QGen - a.QGen) / a.QGen * 100, dTube = b.QFromTube - a.QFromTube;
        Console.WriteLine($"{name} 倍率 {meshScale}：生产 Build（解析板精确积分）{a.Cells} 格／{mA.Nodes.Count} 节点 发热 {a.QGen:0.0} W 抽热 {a.QFromTube:0.000} W；" +
                          $"图纸(步 {step}) {b.Cells} 格／{mF.Nodes.Count} 节点 发热 {b.QGen:0.0} W 抽热 {b.QFromTube:0.000} W（图纸 − Build：发热 {dGen:0.000} %、抽热 {dTube:+0.000;-0.000} W）；" +
                          $"老生成器（4×4 子采样、形心取厚、只钉外圈，只印）{e.Cells} 格／{mE.Nodes.Count} 节点 发热 {e.QGen:0.0} W 抽热 {e.QFromTube:0.000} W");
        Assert.True(dGen <= GenTolPct, $"图纸 vs Build 发热差 {dGen:0.000} % > {GenTolPct} %（Build {a.QGen:0.0}，图纸 {b.QGen:0.0}）");
        Assert.True(Math.Abs(dTube) <= TubeTolProdW, $"图纸 vs Build 抽热差 {dTube:+0.000;-0.000} W 超 {TubeTolProdW} W（倍率 {meshScale}、栅格步 {step}；Build {a.QFromTube:0.000}，图纸 {b.QFromTube:0.000}）");
    }

    /// <summary>
    /// 图纸侧三档栅格步（1.0 = 生产旧默认、网格/4 = 生产口径、0.1）对生产 Build：0.1 mm 时发热差 ≤ 0.1 %、抽热差 ≤ 0.1 W；抽热差随步长单调收窄。
    /// 2026-09-19：原句「盘Ø56 片 1 旧默认栅格步 1.0 偏 1.803 W」量的是对老生成器（压接模型不同），作废；对 Build 的三档差本轮实测记进输出。
    /// </summary>
    [Trait("速度", "慢")]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 图纸路径栅格步0p1对生产Build_发热差小于0p1percent_抽热差小于0p1W_随步长收窄(int which)
    {
        var (g, lc, iA, tRoot, tSet, clampC, tabInsul, name) = Case(which);
        double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
        var mA = FlangeMesher.Build(g, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var a = SolveOne(mA, lc, g, iA, tRoot, tSet, clampC, tabInsul);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{name}：生产 Build（解析板精确积分）{a.Cells} 格 发热 {a.QGen:0.000} W 抽热 {a.QFromTube:0.000} W（2026-09-19 重定靶：参照 = 生产 Build，不再是老生成器）");
        (double dGenPct, double dTubeW) Diff(double rasterStep)
        {
            var f = AnalyticSurrogate.Rasterize(g, rasterStep, 2.0);
            var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
            var b = SolveOne(mF, lc, g, iA, tRoot, tSet, clampC, tabInsul);
            string line = $"{name} 栅格步 {rasterStep}：图纸 {b.Cells} 格 发热 {b.QGen:0.000} W 抽热 {b.QFromTube:0.000} W（图纸 − Build：发热 {(b.QGen - a.QGen) / a.QGen * 100:+0.000;-0.000} %、抽热 {b.QFromTube - a.QFromTube:+0.000;-0.000} W）";
            Console.WriteLine(line); sb.AppendLine(line);
            return (Math.Abs(b.QGen - a.QGen) / a.QGen * 100, b.QFromTube - a.QFromTube);
        }
        double stepProd = FlangeMesher.RasterStepFor(hF);
        var d10 = Diff(1.0); var dH4 = Diff(stepProd); var d01 = Diff(0.1);
        System.IO.File.AppendAllText(DeliverableOut.Stamped("R47_复修M5_栅格步对拍_2026-09-13.txt"),
                                     $"{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{sb}", new System.Text.UTF8Encoding(false));
        Assert.True(d01.dGenPct <= GenTolPct, $"栅格步 0.1：发热差 {d01.dGenPct:0.000} % > {GenTolPct} %");
        Assert.True(Math.Abs(d01.dTubeW) <= TubeTolFineW, $"栅格步 0.1：抽热差 {d01.dTubeW:+0.000;-0.000} W 超 {TubeTolFineW} W");
        Assert.True(dH4.dGenPct <= GenTolPct && Math.Abs(dH4.dTubeW) <= TubeTolProdW, $"栅格步 网格/4（{stepProd}）：发热差 {dH4.dGenPct:0.000} %、抽热差 {dH4.dTubeW:+0.000;-0.000} W");
        Assert.True(Math.Abs(d10.dTubeW) >= Math.Abs(dH4.dTubeW) - 1e-9 && Math.Abs(dH4.dTubeW) >= Math.Abs(d01.dTubeW) - 1e-9,
            $"抽热差没随栅格步收窄：1.0 {d10.dTubeW:+0.000;-0.000} → 网格/4 {dH4.dTubeW:+0.000;-0.000} → 0.1 {d01.dTubeW:+0.000;-0.000} W");
    }
}
