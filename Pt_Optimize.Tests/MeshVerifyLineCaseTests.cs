using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 C（2026-09-13）：加密复算的**工厂重载** —— .3dm 模式复核用的 LineCase 必须走图纸路径
/// （FlangePlates 为空、FlangeFile3dm 或 FlangeFields 非空），每档格数递增；图纸没分析出特征尺寸时拒答不抛。
/// 病：MeshVerify.Run 只接 DesignSpec，.3dm 模式拿 PageToDesignSpec 造的解析板复核 —— 验的是另一个零件。
/// </summary>
public class MeshVerifyLineCaseTests
{
    private static (LineCase lc, FlangePlate g, ThicknessField f) Drawing1Seg()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = d.Plate(1, d.DiscFloorMm(p)); g.HoleRadiusMm = holeR;
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { f };
        lc.GeomForJudge = new[] { g };
        lc.TabInsul3dmPerPlateMm = new[] { 2.8 };
        return (lc, g, f);
    }

    [Fact]
    public void 工厂每档造的LineCase走图纸路径_格数随内带递增()
    {
        var (lc0, g, f) = Drawing1Seg();
        var sh = PlateShapeAnalyzer.Analyze(f);
        var (h0, radius, innerR, refused) = MeshVerify.RequiredMeshFor(sh, lc0.WallMm, lc0);
        Assert.Null(refused);
        Assert.True(h0 > 0 && radius > g.HoleRadiusMm && innerR > g.HoleRadiusMm);
        // 盘Ø56／孔 25.8 ⇒ 环宽 2.2 mm ⇒ 起始网格 ≈ 0.73 mm；焊缝凹圆弧切出来的发丝级不许把它拖到 0.01 mm 以下
        Assert.InRange(h0, 0.3, 1.0);

        var made = new List<LineCase>();
        LineCase Factory(double hMid, double hInner)
        {
            var lc = FlangeAutoSizer.CloneCase(lc0);
            lc.MeshFineMm = hMid; lc.MeshFineRadiusMm = radius; lc.MeshInnerMm = hInner; lc.MeshInnerRadiusMm = innerR;
            made.Add(lc);
            return lc;
        }
        // 只验工厂造出来的东西与网格；真解场的门见下面那条慢的
        double h = h0;
        int prev = 0;
        for (int it = 0; it < 3; it++)
        {
            var lc = Factory(h0, h);
            Assert.Empty(lc.FlangePlates);
            Assert.True(lc.FlangeFields.Length > 0 || lc.FlangeFile3dm.Length > 0, "复核用的 LineCase 必须走图纸路径");
            Assert.Equal(2.8, lc.TabInsul3dmAt(0));
            Assert.Single(lc.GeomForJudge);
            int cells = FlangeMesher.BuildFromField(lc.FlangeFields[0], g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                                    lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm).CellCount;
            Assert.True(cells > prev, $"第 {it + 1} 档 {cells} 格应多于上一档 {prev}");
            prev = cells;
            h = MeshAdapt.Refine(h);
        }
        Assert.Equal(3, made.Count);
    }

    [Fact]
    public void 图纸没分析出特征尺寸时拒答不抛()
    {
        var empty = new PlateShapeAnalyzer.Shape();       // 没有分级、没有管孔
        var (fine, _, _, refused) = MeshVerify.RequiredMeshFor(empty, 0.8, new LineCase());
        Assert.NotNull(refused);
        Assert.True(double.IsNaN(fine));
        Assert.Contains("加密复算不能判", refused);
        var res = MeshVerify.Run(empty, 0.8, (a, b) => throw new InvalidOperationException("拒答时不该造 LineCase"));
        Assert.False(res.Converged);
        Assert.Contains("加密复算不能判", res.Verdict);
        Assert.Null(res.Line);
        // 界面不许出现判据代号／开关名：拒答句子里只有全名
        Assert.DoesNotContain("--", res.Verdict);
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 真解_图纸路径加密复算每档格数递增()
    {
        var (lc0, g, f) = Drawing1Seg();
        var sh = PlateShapeAnalyzer.Analyze(f);
        var (_, radius, innerR, refused) = MeshVerify.RequiredMeshFor(sh, lc0.WallMm, lc0);
        Assert.Null(refused);
        // 工厂重载直接给起始网格 2 mm（导航口径），两档就够看「格数递增、走图纸路径」；特征尺寸那条由上面的快门验
        var res = MeshVerify.Run((hMid, hInner) =>
        {
            var lc = FlangeAutoSizer.CloneCase(lc0);
            lc.MeshFineMm = hMid; lc.MeshInnerMm = hInner;
            lc.MeshFineRadiusMm = radius; lc.MeshInnerRadiusMm = innerR;
            return lc;
        }, 2.0, radius, innerR, maxCells: 40000, maxRounds: 2, progress: new Progress<string>(s => Console.WriteLine(s)));
        Assert.NotNull(res.Line);
        Assert.True(res.Trace.Count >= 2, "至少两档");
        for (int i = 1; i < res.Trace.Count; i++)
            Assert.True(res.Trace[i].Cells > res.Trace[i - 1].Cells, $"第 {i + 1} 档 {res.Trace[i].Cells} 格应多于上一档 {res.Trace[i - 1].Cells}");
    }
}
