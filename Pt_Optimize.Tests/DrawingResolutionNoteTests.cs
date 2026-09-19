using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 复修 M3／M4（2026-09-13）：「已到图纸分辨率」这句话**不许就地追加到共享的 ThicknessField.Warning**
/// （LoadThickness 缓存与 LineCase.FlangeFields 都是同一实例：多片、多档、多次 Run 会越滚越长），
/// 只写进本次 LineResult.Notes 且按片去重；文件路径的栅格步有地板 0.05 mm，到地板才算「已到图纸分辨率」。
/// </summary>
public class DrawingResolutionNoteTests
{
    private const string Tag = "已到图纸分辨率";

    [Fact]
    public void 文件路径栅格步有地板_到地板才算已到图纸分辨率()
    {
        Assert.Equal(0.05, FlangeMesher.RasterStepFloorMm);
        Assert.Equal(0.5, FlangeMesher.RasterStepForFile(2.0));           // 2/4 = 0.5，没到地板
        Assert.Equal(0.05, FlangeMesher.RasterStepForFile(0.2));          // 0.2/4 = 0.05，恰在地板
        Assert.Equal(0.05, FlangeMesher.RasterStepForFile(0.1));          // 0.1/4 = 0.025 → 抬到地板
        Assert.False(FlangeMesher.RasterAtFloor(0.2));
        Assert.True(FlangeMesher.RasterAtFloor(0.1));
        Assert.Equal(0.025, FlangeMesher.RasterStepFor(0.1));             // 内存栅格化没有地板
    }

    [Fact]
    public void 同一场跑三次Run_该句每次只出现一次_共享的Warning不变()
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        var g = d.Plate(1, d.DiscFloorMm(p)); g.HoleRadiusMm = holeR;
        // 内存厚度场栅格步 1.0，网格内带 0.5 ⇒ 网格比栅格细 ⇒ 到了图纸分辨率
        var f = AnalyticSurrogate.Rasterize(g, 1.0, 2.0);
        f.Warning = "（原有警告）";
        lc.FlangePlates = Array.Empty<FlangePlate>();
        lc.FlangeFields = new[] { f, f };
        lc.GeomForJudge = new[] { g, g };
        lc.TabInsul3dmPerPlateMm = new[] { 2.8, 2.8 };
        lc.MeshInnerMm = 0.5; lc.MeshInnerRadiusMm = MeshAdapt.InnerRadiusFor(holeR, g.WeldFilletLegMm);
        string warn0 = f.Warning; int len0 = f.Warning.Length;
        for (int run = 0; run < 3; run++)
        {
            var r = LineRunner.Run(lc);
            Assert.True(r.Ok, r.Message);
            var notes = r.Notes.Where(n => n.Contains(Tag, StringComparison.Ordinal)).ToArray();
            // 两片各一句（按片去重），不随第几次 Run 变
            Assert.Equal(2, notes.Length);
            Assert.Single(notes, n => n.StartsWith("⚠ 入口", StringComparison.Ordinal));
            Assert.Single(notes, n => n.StartsWith("⚠ 出口", StringComparison.Ordinal));
            Assert.All(notes, n => Assert.Contains("（原有警告）", n));       // 原有警告也带出去
            Assert.All(notes, n => Assert.Equal(1, n.Split(Tag).Length - 1));   // 一句里只出现一次
            Assert.Equal(len0, f.Warning.Length);
            Assert.Equal(warn0, f.Warning);
            Assert.DoesNotContain(Tag, f.Warning);
            Assert.Same(f, lc.FlangeFields[0]);
        }
    }
}
