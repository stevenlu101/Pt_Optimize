using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 B（2026-09-13）：图纸路径的舌保温**逐片**进各自那片的热解（工单 §3「逐片舌保温」「保温分界从几何来」）。
/// 做法：三片三个不同舌保温（5.1／2.8／8.6）走内存厚度场（LineCase.FlangeFields）解一次整线，
/// 再对每一片用整线给出的电流、管根温度、铜排热导**单独重解一次壳**（同一片、同一输入、只带该片的舌保温），
/// 抽热必须逐位对上；换成别片的舌保温则对不上（证明门咬得住）。
/// 保温分界／舌盘分界来自 GeomForJudge[j]（该片自己的等效几何），不是默认板。
/// </summary>
public class TabInsulPerPlateTests
{
    [Trait("速度", "慢")]
    [Fact]
    public void 三片各自的舌保温进了各自那片的热解()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        var lc = d.BuildCase(p);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        int nf = lc.FlangeCount;
        var plates = Enumerable.Range(0, nf).Select(j => { var g = d.Plate(j, d.DiscFloorMm(p)); g.HoleRadiusMm = holeR; return g; }).ToArray();
        double[] ins = { 5.1, 2.8, 8.6 };
        Assert.Equal(3, nf);

        // 图纸路径：解析板栅格化成厚度场（留白 2，与病发时同图幅），FlangePlates 清空
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = plates.Select(g => AnalyticSurrogate.Rasterize(g, 0.5, 2.0)).ToArray();
        lc.GeomForJudge = plates;
        lc.TabInsul3dmPerPlateMm = ins;
        lc.CheckRamp = false;
        Assert.Equal(5.1, lc.TabInsul3dmMm);          // 旧标量入口读得到（第 0 片）
        Assert.Equal(8.6, lc.TabInsul3dmAt(2));

        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(nf, r.Flanges.Length);
        var dip = r.Checks.First(c => c.Name.StartsWith("③"));
        var flux = r.Checks.First(c => c.Name.StartsWith("②′"));
        Assert.False(dip.Undetermined, dip.Note);
        Assert.False(flux.Undetermined, flux.Note);
        Assert.Contains("来自本片分析几何变数的等效片", flux.Note);
        int n = lc.SegmentCount;

        for (int j = 0; j < nf; j++)
        {
            var f = r.Flanges[j];
            var g = plates[j];
            var mesh = FlangeMesher.BuildFromField(lc.FlangeFields[j], holeR, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                                   lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
            double tSet = lc.SetpointC[Math.Min(j, n - 1)];
            var sc = ShellCurrent.SolveFor(lc, mesh, f.CurrentA, Materials.PtResistivity(tSet) * 1e3, tSet);
            double Q(double tabInsul)
            {
                var p2 = SegmentSolver.Clone(lc.Base);
                p2.TSetC = tSet;
                if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                if (f.BusGWPerK >= 0) p2.BusbarConductanceWPerK = f.BusGWPerK;
                var th = ShellThermal.Solve(mesh, sc.JMagAPerMm2, p2, f.TRootC, g.InsulBoundaryXResolved, false,
                                            tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabInsul);
                return th.QFromTubeW;
            }
            double qOwn = Q(ins[j]);
            double qOther = Q(ins[(j + 1) % nf]);
            Console.WriteLine($"片{j} {f.Name}：整线抽热 {f.QFromTubeW:0.000} W；单独重解 本片舌保温 {ins[j]} ⇒ {qOwn:0.000} W；别片舌保温 {ins[(j + 1) % nf]} ⇒ {qOther:0.000} W");
            Assert.Equal(f.QFromTubeW, qOwn, 6);
            Assert.True(Math.Abs(f.QFromTubeW - qOther) > 1e-3, "换成别片的舌保温应当对不上，否则这条门没咬住");
        }
    }

    [Fact]
    public void 没有等效片也没有切点时判成无法判定_不许用默认板()
    {
        // 一张只有圆环、没有舌片的「图纸」：材料半宽处处等于盘半径 ⇒ 推不出舌盘分界（x<0 侧第一列就满足）——
        // 这里直接验 TangentFromField 对空场拒答，以及 LineRunner 对拒答片的标法（用假场：全 0）。
        var f = new ThicknessField { X0 = -10, Z0 = -10, Step = 1, Nx = 21, Nz = 21, T = new double[21 * 21] };
        Assert.False(LineRunner.TangentFromField(f, out _, out _));

        var g = MeshAxisTests.Disc56Plate1(out _);
        var fr = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        Assert.True(LineRunner.TangentFromField(fr, out double xT, out string how));
        // 盘Ø56：舌半宽 28 = 盘半径 28 ⇒ 舌盘分界 x = 0，与解析板的切点 Tangent().X = −0.0 一致
        Assert.Equal(g.Tangent().X, xT, 6);
        Assert.Contains("材料包络", how);
        // 等宽舌半宽 20、盘 R28 ⇒ x = −√(28²−20²) = −19.6
        var g2 = new FlangePlate { DiscRadiusMm = 28, HoleRadiusMm = 25.8, TabEndXMm = -140, TabEndHalfWidthMm = 20, ThicknessMm = 1, TabParallel = true };
        Assert.True(LineRunner.TangentFromField(AnalyticSurrogate.Rasterize(g2, 0.5, 2.0), out double xT2, out _));
        Assert.Equal(g2.Tangent().X, xT2, 1);
    }

    /// <summary>
    /// ★ R47 复修 M10（2026-09-13）：**真门** —— 造一张推不出切点的场（材料全在 x ≥ 0：舌片没有）喂 LineRunner.Run
    /// （FlangeFields 非空、GeomForJudge 空），「管孔净流入」与「法兰增量温降」两条必须 Undetermined、Ok=false、
    /// Note 含「保温分界判不了」，FlangeOut.InsulBoundaryUndetermined 为真。判不了不算过。
    /// </summary>
    [Fact]
    public void 推不出切点的场_两条判据无法判定_不用默认板()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        // 半个圆盘：舌端 x = 0 ⇒ 材料全在 x ≥ 0，XMinMaterial = 0，推不出舌盘分界
        var g = new FlangePlate { DiscRadiusMm = 28, HoleRadiusMm = holeR, TabEndXMm = 0, TabEndHalfWidthMm = 28, ThicknessMm = 1.0, TabParallel = true };
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        Assert.True(f.T.Count(v => v > 1e-9) > 100, "场里得有料");
        Assert.False(LineRunner.TangentFromField(f, out _, out _));
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { f, f };
        lc.GeomForJudge = Array.Empty<FlangePlate>();
        lc.TabInsul3dmPerPlateMm = new[] { 2.8, 2.8 };
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        Assert.All(r.Flanges, fo => Assert.True(fo.InsulBoundaryUndetermined, fo.Name + " 应标成保温分界判不了"));
        Assert.All(r.Flanges, fo => Assert.Contains("保温分界判不了", fo.InsulBoundaryNote));
        // ★ R47 第三轮 N2：不只两条 —— **所有吃法兰场的判据与参考行**（法兰最高温−管温／自给率／热平衡残差／圆盘区最高温…）
        //   都要标 Undetermined，名单与「场没解到位」那一遍同一份（LineRunner.DependsOnFlangeFields），不许另写一份。
        Assert.Contains(LineResult.Key.FlangeTopTemp, LineRunner.DependsOnFlangeFields);
        Assert.Contains(LineResult.Key.SelfSupply, LineRunner.DependsOnFlangeFields);
        Assert.Contains(LineResult.Key.HeatResidual, LineRunner.DependsOnFlangeFields);
        Assert.Contains(LineResult.Key.DiscTemp, LineRunner.DependsOnFlangeFields);
        int seen = 0;
        foreach (var key in LineRunner.DependsOnFlangeFields)
            foreach (var ck in r.Checks.Where(c => c.Name.StartsWith(key, StringComparison.Ordinal)))
            {
                seen++;
                Assert.True(ck.Undetermined, ck.Name + " 应无法判定");
                Assert.False(ck.Ok, ck.Name + " 判不了不算过");
                Assert.Contains("保温分界判不了", ck.Note);
            }
        Assert.True(seen >= 6, $"这张表上只找到 {seen} 条吃法兰场的判据／参考行，名单是不是没对上");
        foreach (var key in new[] { LineResult.Key.NetFlux, LineResult.Key.FlangeDip, LineResult.Key.FlangeTopTemp, LineResult.Key.SelfSupply, LineResult.Key.HeatResidual })
            Assert.Contains(r.Checks, c => c.Name.StartsWith(key, StringComparison.Ordinal) && c.Undetermined);
        // 不吃法兰场的（管 J）不许被顺手标掉
        Assert.Contains(r.Checks, c => c.Name.StartsWith(LineResult.Key.TubeJ, StringComparison.Ordinal) && !c.Note.Contains("保温分界判不了"));
        Assert.False(r.AllOk);
    }

    /// <summary>源码门：LineRunner.cs 的代码行（注释除外）不再出现 new FlangePlate() —— 图纸路径没有默认板。</summary>
    [Fact]
    public void 源码门_LineRunner不再new默认板()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        var codeLines = src.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)).ToArray();
        Assert.True(codeLines.Length > 500, "读到的不像 LineRunner.cs");
        var hits = codeLines.Where(l => l.Contains("new FlangePlate()", StringComparison.Ordinal)).ToArray();
        Assert.True(hits.Length == 0, "LineRunner.cs 仍在 new 默认板：" + string.Join(" | ", hits));
    }
}
