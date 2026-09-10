using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R35（2026-09-11，功能落地排查后的三条低欠账之一）：图纸读回要认锥形舌（R31）与叉臂（R29）。
/// 用解析板栅格化成厚度场（AnalyticSurrogate.Rasterize），再交给 PlateShapeAnalyzer／ShapeToAnalytic，看它认不认。
/// </summary>
public class DrawingTaperArmReadbackTests
{
    [Fact]
    public void 锥形舌的图_读回勾锥形()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 15; d.TabLengthMm = 140; d.TabTaper = true;
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        g.WeldFilletLegMm = 0;   // 栅格化会把焊脚斜坡画成多级台阶；真图纸走图层探针没有这段斜坡，这里去掉只看形状
        var field = AnalyticSurrogate.Rasterize(g, 0.5);
        var sh = PlateShapeAnalyzer.Analyze(field);
        Assert.InRange(sh.TabEndHalfWidthMm, 14, 16);
        Assert.True(sh.TabRootHalfWidthMm > sh.TabEndHalfWidthMm + 5, $"舌根半宽 {sh.TabRootHalfWidthMm:0.0} 该明显大于舌端 {sh.TabEndHalfWidthMm:0.0}");
        var k = ShapeToAnalytic.From(sh);
        Assert.True(k.TabTaper, "读回该勾锥形舌片");
        Assert.True(double.IsNaN(k.TabArmThickMm), "没有叉臂就不该报叉臂");
    }

    [Fact]
    public void 平行舌的图_读回不勾锥形_逐位同前()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 30; d.TabLengthMm = 140;
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        g.WeldFilletLegMm = 0;   // 栅格化会把焊脚斜坡画成多级台阶；真图纸走图层探针没有这段斜坡，这里去掉只看形状
        var sh = PlateShapeAnalyzer.Analyze(AnalyticSurrogate.Rasterize(g, 0.5));
        var k = ShapeToAnalytic.From(sh);
        Assert.False(k.TabTaper);
    }

    [Fact]
    public void 舌根加厚段的图_读回认出叉臂与带起点()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 30; d.TabLengthMm = 140;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = 1.0; d.TongueThickMm[j] = 1.8; }
        d.TabArmX0Mm[0] = -48; d.TabArmX1Mm[0] = 0; d.TabArmThickMm[0] = 3.2;    // 带 [−48, 0] 厚 3.2（切口本身不画，只看厚度）
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        g.WeldFilletLegMm = 0;   // 栅格化会把焊脚斜坡画成多级台阶；真图纸走图层探针没有这段斜坡，这里去掉只看形状
        var sh = PlateShapeAnalyzer.Analyze(AnalyticSurrogate.Rasterize(g, 0.5));
        Assert.InRange(sh.TabRootThickMm, 3.1, 3.3);
        Assert.InRange(sh.TabEndThickMm, 1.7, 1.9);
        Assert.InRange(sh.TabArmX0Mm, -50, -46);
        var k = ShapeToAnalytic.From(sh);
        Assert.InRange(k.TabArmThickMm, 3.1, 3.3);
        Assert.InRange(k.TabArmX0Mm, -50, -46);
    }
}
