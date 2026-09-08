using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 「.3dm 的几何能不能带进解析路」—— 翻译规则本身。
///
/// 用户 2026-08-25：「3DM 只读几何数据（画网格的依据），为何不能带入计算？」
/// 答案是能：PlateShapeAnalyzer 早就把整套数反推出来了，缺的只是交过去这一步。
///
/// ★★★★★ 2026-09-08（R8／R14）：**多级厚度不再压平均** —— 图纸各级逐级成为
/// 基板 t + 内级 r₁·t₁ + 外级 r₂·t₂（解析模型本来就有的形状）。本文件钉住：
///   · 1 级图：基板 = 那一级，没有台阶；
///   · 2 级图：内级 r₁/t₁ 逐位来自图纸，外级 t₂ = 1（等于没有）；
///   · 3 级图（Pt_Heater3.3dm 实测 1.0／2.0／3.0 mm，R26–36–46–203.5）：四个数逐位来自图纸；
///   · ≥4 级：抛，不并、不猜；
///   · **闭环**：翻出来的参数造成板 → 栅格化 → 反推，级数与厚度逐位相同、半径对得上。
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
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 2.0, RInnerMm = 26.0, ROuterMm = 203.5, AreaMm2 = 23566 });
        return sh;
    }

    /// <summary>Pt_Heater3.3dm 实测（`--surrogate Pt_Heater3.3dm 法兰`，2026-09-08）：三级 1.0／2.0／3.0 mm。</summary>
    private static PlateShapeAnalyzer.Shape Heater3()
    {
        var sh = new PlateShapeAnalyzer.Shape
        {
            HoleRadiusMm = 26.0,
            DiscRadiusMm = 59.99,
            TabEndXMm = -199.5,
            TabEndHalfWidthMm = 40.0,
        };
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 1.0, RInnerMm = 26.0, ROuterMm = 36.0, AreaMm2 = 1414 });
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 2.0, RInnerMm = 36.0, ROuterMm = 46.0, AreaMm2 = 2580 });
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 3.0, RInnerMm = 46.0, ROuterMm = 203.5, AreaMm2 = 19049 });
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
        Assert.Equal(1, k.LevelCount);
        Assert.False(k.HasRing);
        Assert.False(k.TabThickApproximated);
    }

    [Fact]
    public void 单级不算近似_并且明说不是近似()
    {
        var k = ShapeToAnalytic.From(Heater1());
        Assert.Contains("不是近似", k.Note);
        Assert.DoesNotContain("平均", k.Note);
    }

    [Fact]
    public void 三级图_基板与两级台阶逐位来自图纸_不取平均()
    {
        var k = ShapeToAnalytic.From(Heater3());
        Assert.Equal(3, k.LevelCount);
        Assert.Equal(3.0, k.PlateThickMm, 9);            // 基板 = 够到盘外缘那一级，**不是** 面积加权平均 2.85
        Assert.True(k.HasRing);
        Assert.Equal(10.0, k.RingW1Mm, 9);               // r₁ = 36 − 26
        Assert.Equal(1.0 / 3.0, k.RingMul, 9);           // t₁ = 1.0 / 3.0
        Assert.Equal(20.0, k.RingW2Mm, 9);               // r₂ = 46 − 26
        Assert.Equal(2.0 / 3.0, k.RingMul2, 9);          // t₂ = 2.0 / 3.0
        Assert.Equal(3.0, k.TabThickMm, 9);
        Assert.False(k.TabThickApproximated);
        Assert.Contains("不取平均", k.Note);
        Assert.Contains("薄", k.Note);                    // 孔边比基板薄 —— 要说出口
        Assert.DoesNotContain("面积加权", k.Note);
    }

    [Fact]
    public void 三级图_翻出来的四个数写进设计后_台阶半径与厚度对得上模型()
    {
        var k = ShapeToAnalytic.From(Heater3());
        var d = DesignSpec.Builtin[0].Clone();
        d.WallMm = k.WallMm; d.DiscRadiusMm = k.DiscDiameterMm / 2;
        d.TabLengthMm = k.TabLengthMm; d.TabHalfWidthMm = k.TabHalfWidthMm;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = k.PlateThickMm; k.ApplyRing(d, j); }
        var rr = d.RingRadiiOf(0);
        Assert.Equal(36.0, rr[0], 9);
        Assert.Equal(46.0, rr[1], 9);
        var g = d.Plate(0, 0);
        Assert.Equal(1.0, g.DiscStepThicknessMm[0], 9);
        Assert.Equal(2.0, g.DiscStepThicknessMm[1], 9);
        Assert.Equal(3.0, g.ThicknessMm, 9);
    }

    /// <summary>
    /// ★★ 闭环：翻出来的参数造成板 → 栅格化成厚度场 → 反推 → 级数、厚度、半径与原图逐位相同。
    /// 这一条红了，就说明翻译规则与 PlateShapeAnalyzer 的口径漂开了（同一张图两边说法不一）。
    /// </summary>
    [Fact]
    public void 三级图_翻过去再翻回来_逐位相同()
    {
        var src = Heater3();
        var k = ShapeToAnalytic.From(src);
        var d = DesignSpec.Builtin[0].Clone();
        d.WallMm = k.WallMm; d.DiscRadiusMm = k.DiscDiameterMm / 2;
        d.TabLengthMm = k.TabLengthMm; d.TabHalfWidthMm = k.TabHalfWidthMm;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = k.PlateThickMm; k.ApplyRing(d, j); }
        var g = d.Plate(0, 0);
        g.WeldFilletLegMm = 0;          // 图纸不含焊缝堆料（与 AnalyticSurrogate.Build 同一口径）
        var f = AnalyticSurrogate.Rasterize(g, 0.5);
        var back = PlateShapeAnalyzer.Analyze(f);

        Assert.Equal(src.Levels.Count, back.Levels.Count);
        for (int i = 0; i < src.Levels.Count; i++)
        {
            Assert.Equal(src.Levels[i].ThicknessMm, back.Levels[i].ThicknessMm, 2);
            Assert.InRange(back.Levels[i].RInnerMm, src.Levels[i].RInnerMm - 1.0, src.Levels[i].RInnerMm + 1.0);
            if (i < src.Levels.Count - 1)   // 最外那级的外半径是舌端，另一条断言管
                Assert.InRange(back.Levels[i].ROuterMm, src.Levels[i].ROuterMm - 1.0, src.Levels[i].ROuterMm + 1.0);
        }
        Assert.InRange(back.DiscRadiusMm, 59.0, 61.0);
        Assert.InRange(back.HoleRadiusMm, 25.5, 26.5);
        // 再翻一次应逐位回到同一组数（翻译是幂等的）
        var k2 = ShapeToAnalytic.From(back);
        Assert.Equal(k.PlateThickMm, k2.PlateThickMm, 2);
        Assert.Equal(k.RingW1Mm, k2.RingW1Mm, 0);
        Assert.Equal(k.RingW2Mm, k2.RingW2Mm, 0);
        Assert.Equal(k.RingMul, k2.RingMul, 2);
        Assert.Equal(k.RingMul2, k2.RingMul2, 2);
        // 盘上那两级的面积也对得上：外级与夹具的实测值（2580）直接比；
        // 内级图纸上有 4 个槽（R31–35.9）挖掉了一块，解析路不带槽 ⇒ 与**整圈**面积 π(36²−26²) = 1948 比
        Assert.InRange(back.Levels[1].AreaMm2, src.Levels[1].AreaMm2 * 0.9, src.Levels[1].AreaMm2 * 1.1);
        double ring0 = Math.PI * (36.0 * 36.0 - 26.0 * 26.0);
        Assert.InRange(back.Levels[0].AreaMm2, ring0 * 0.95, ring0 * 1.05);
    }

    [Fact]
    public void 两级图_只有内级_外级倍率1等于没有()
    {
        var sh = Heater1();
        sh.Levels.Clear();
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 1.0, RInnerMm = 26.0, ROuterMm = 36.0, AreaMm2 = 1414 });
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 3.0, RInnerMm = 36.0, ROuterMm = 203.5, AreaMm2 = 21600 });
        var k = ShapeToAnalytic.From(sh);
        Assert.Equal(3.0, k.PlateThickMm, 9);
        Assert.True(k.HasRing);
        Assert.Equal(10.0, k.RingW1Mm, 9);
        Assert.Equal(1.0 / 3.0, k.RingMul, 9);
        Assert.Equal(1.0, k.RingMul2, 9);               // 外级 = 基板 ⇒ 等于没有
        Assert.True(k.RingW2Mm > k.RingW1Mm);            // 半径仍要递增（RingRadiiOf 的不变式）
        Assert.Contains("等于没有", k.Note);
        // 造成板之后厚度场与「只有一级台阶」逐位相同：r₁ 内 1.0，r₁ 外一律 3.0
        var d = DesignSpec.Builtin[0].Clone();
        d.WallMm = k.WallMm; d.DiscRadiusMm = k.DiscDiameterMm / 2;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = k.PlateThickMm; k.ApplyRing(d, j); }
        var g = d.Plate(0, 0); g.WeldFilletLegMm = 0;
        Assert.Equal(1.0, g.ThicknessAt(30, 0), 9);
        Assert.Equal(3.0, g.ThicknessAt(40, 0), 9);
        Assert.Equal(3.0, g.ThicknessAt(55, 0), 9);
    }

    [Fact]
    public void 四级图_解析模型装不下_抛而不并()
    {
        var sh = Heater3();
        sh.Levels.Insert(2, new PlateShapeAnalyzer.Level { ThicknessMm = 2.5, RInnerMm = 46.0, ROuterMm = 52.0, AreaMm2 = 1800 });
        sh.Levels[3].RInnerMm = 52.0;
        var ex = Assert.Throws<ArgumentException>(() => ShapeToAnalytic.From(sh));
        Assert.Contains("3 级台阶", ex.Message);
        Assert.Contains("不并", ex.Message);
    }

    [Fact]
    public void 舌片另有一级不同厚_先按盘缘算并标为近似()
    {
        var sh = Heater3();
        // 盘缘那级只到盘外缘，舌片另有一级 4.0 mm
        sh.Levels[2].ROuterMm = 59.99;
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 4.0, RInnerMm = 60.0, ROuterMm = 203.5, AreaMm2 = 12000 });
        var k = ShapeToAnalytic.From(sh);
        Assert.Equal(3.0, k.PlateThickMm, 9);
        Assert.Equal(4.0, k.TabThickMm, 9);
        Assert.True(k.TabThickApproximated);
        Assert.Contains("近似", k.Note);
        Assert.Equal(10.0, k.RingW1Mm, 9);
        Assert.Equal(20.0, k.RingW2Mm, 9);
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
    public void 图纸的槽不带过去_但要说出口()
    {
        var sh = Heater3();
        sh.Slot.Count = 4; sh.Slot.WidthDeg = 44.9; sh.Slot.RInnerMm = 31.0; sh.Slot.ROuterMm = 35.9;
        Assert.Contains("不带过去", ShapeToAnalytic.From(sh).Note);
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

    /// <summary>★ 真图纸：Pt_Heater3.3dm 读进来（要 Geom 探针在），翻出来的就是上面钉的那四个数。</summary>
    [Fact]
    public void 真图纸_Pt_Heater3_三级逐位带过来()
    {
        string file = Path.Combine(HandoverDoc.Root(), "Pt_Heater3.3dm");
        if (!File.Exists(file) || Geometry3dm.FindProbe() is null) return;   // 没探针就不判（不是过）
        var f = Geometry3dm.LoadThickness(file, "法兰", double.NaN, 0.5);
        var sh = PlateShapeAnalyzer.Analyze(f);
        var k = ShapeToAnalytic.From(sh);
        Assert.Equal(3, k.LevelCount);
        Assert.InRange(k.PlateThickMm, 2.95, 3.05);
        Assert.InRange(k.RingW1Mm, 9.5, 10.5);
        Assert.InRange(k.RingW2Mm, 19.5, 20.5);
        Assert.InRange(k.RingMul, 0.32, 0.35);
        Assert.InRange(k.RingMul2, 0.65, 0.68);
        Assert.InRange(k.WallMm, 0.95, 1.05);
    }
}
