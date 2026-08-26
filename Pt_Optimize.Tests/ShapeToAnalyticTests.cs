using System;
using System.Collections.Generic;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 「.3dm 的几何能不能带进解析路」—— 翻译规则本身。
///
/// 用户 2026-08-25：「3DM 只读几何数据（画网格的依据），为何不能带入计算？」
/// 答案是能：PlateShapeAnalyzer 早就把整套数反推出来了，缺的只是交过去这一步。
/// 本文件钉住那一步的算术，以及**三条近似必须说出口**。
///
/// 基准用的是 Pt_Heater1.3dm 的实测值（`--surrogate Pt_Heater1.3dm 法兰`）：
///   孔 R26.00　盘 R59.99　舌端 X−199.5／半宽 40.0　厚度 1 级 2.000 mm
/// </summary>
public class ShapeToAnalyticTests
{
    private static PlateShapeAnalyzer.Shape Heater1()
    {
        var sh = new PlateShapeAnalyzer.Shape
        {
            HoleRadiusMm = 26.0,
            DiscRadiusMm = 59.99,
            TabEndXMm = -199.5,
            TabEndHalfWidthMm = 40.0,
        };
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 2.0, AreaMm2 = 23566 });
        return sh;
    }

    [Fact]
    public void Pt_Heater1_翻出来的就是界面要填的那几个数()
    {
        var k = ShapeToAnalytic.From(Heater1());
        Assert.Equal(119.98, k.DiscDiameterMm, 2);   // 盘 R59.99 ⇒ Ø120
        Assert.Equal(199.5, k.TabLengthMm, 3);       // 舌端 X 取绝对值
        Assert.Equal(40.0, k.TabHalfWidthMm, 3);     // ★ 是**半**宽，不是舌宽
        Assert.Equal(1.0, k.WallMm, 6);              // 孔 26 − 25
        Assert.Equal(2.0, k.PlateThickMm, 6);
    }

    [Fact]
    public void 单级不算近似_并且明说不是近似()
    {
        var k = ShapeToAnalytic.From(Heater1());
        Assert.False(k.ThicknessApproximated);
        Assert.Contains("不是近似", k.Note);
    }

    [Fact]
    public void 多级压成一个数_取面积加权平均_并标为近似()
    {
        var sh = Heater1();
        sh.Levels.Clear();
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 1.0, AreaMm2 = 300 });
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 3.0, AreaMm2 = 100 });
        var k = ShapeToAnalytic.From(sh);
        Assert.Equal((1.0 * 300 + 3.0 * 100) / 400.0, k.PlateThickMm, 6);   // = 1.5
        Assert.True(k.ThicknessApproximated);
        Assert.Contains("近似", k.Note);
    }

    [Fact]
    public void 管壁是反推的_这件事必须写在说明里()
    {
        // 反推靠的是 孔半径 = 管壁 + 25 这条约定。图纸没按它画，数就不对 —— 必须出声。
        Assert.Contains("反推", ShapeToAnalytic.From(Heater1()).Note);
        Assert.Contains("25", ShapeToAnalytic.From(Heater1()).Note);
    }

    [Fact]
    public void 舌片族是等宽_这件事也必须写在说明里()
    {
        // Pt_Heater1 的舌片实测是**梯形**（等宽当它会差 12 %）。
        // 转过去之后优化的是等宽舌 —— 不说，人会以为还在优化原图那一片。
        Assert.Contains("等宽", ShapeToAnalytic.From(Heater1()).Note);
    }

    [Fact]
    public void 没有舌片就抛_不给一个看起来正常的默认值()
    {
        var sh = Heater1();
        sh.TabEndXMm = double.NaN;
        Assert.Throws<ArgumentException>(() => ShapeToAnalytic.From(sh));
    }

    [Fact]
    public void 一级厚度都没有就抛()
    {
        var sh = Heater1();
        sh.Levels.Clear();
        Assert.Throws<ArgumentException>(() => ShapeToAnalytic.From(sh));
    }

    [Fact]
    public void 说明永远不为空()
    {
        Assert.False(string.IsNullOrWhiteSpace(ShapeToAnalytic.From(Heater1()).Note));
    }
}
