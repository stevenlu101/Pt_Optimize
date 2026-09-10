using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R39（2026-09-11 边角料）：「导出可回读 3DM」要把舌根加厚带（R29 叉臂）画出来，读回来的舌根厚才与算的是一份。
/// 真走 Rhino 探针：写一张带叉臂的可回读图 → 逐点量厚度 → 形状分析认出叉臂带。
/// </summary>
public class ReadableArmExportTests
{
    [Fact]
    public void 可回读图画出叉臂段_读回认出带与厚()
    {
        if (Geometry3dm.FindProbe() is null) return;   // 没装 Rhino 的机器上不判（与 TabHoleTests 同一约定）
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 30; d.TabHalfWidthMm = 30; d.TabLengthMm = 140;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = 1.2; d.TongueThickMm[j] = 1.8; }
        d.TabArmX0Mm[0] = -48; d.TabArmX1Mm[0] = -8; d.TabArmThickMm[0] = 3.2;
        var b = new DesignInputs();
        double floor = d.DiscFloorMm(b);
        var pa = Geometry3dm.BuildSteppedPlateArgs(d, 0, floor);
        Assert.Equal(-48, pa.TabArmX0Mm, 6);
        Assert.Equal(3.2, pa.TabArmThickMm, 6);

        string tmp = Path.Combine(Path.GetTempPath(), $"pt_arm_{Guid.NewGuid():N}.3dm");
        try
        {
            Geometry3dm.WriteStepped3dm(tmp, d.HoleRadiusMm, pa.RadiiMm, pa.ThickMm,
                -d.TabLengthMm, d.TabHalfWidthMm, pa.TabThickMm,
                slotCount: pa.SlotCount, slotWidthDeg: pa.SlotWidthDeg, slotRInMm: pa.SlotRInMm, slotROutMm: pa.SlotROutMm,
                tabHoleXMm: pa.TabHoleXMm, tabHoleRMm: pa.TabHoleRMm,
                tabArmX0Mm: pa.TabArmX0Mm, tabArmX1Mm: pa.TabArmX1Mm, tabArmThickMm: pa.TabArmThickMm);
            var f = Geometry3dm.LoadThickness(tmp, "法兰", double.NaN, 0.5);
            double At(double x, double z)
            {
                int ix = (int)Math.Round((x - f.X0) / f.Step), iz = (int)Math.Round((z - f.Z0) / f.Step);
                return f.T[ix * f.Nz + iz];
            }
            Assert.InRange(At(-100, 0), 1.75, 1.85);   // 杆：舌片厚 1.8
            Assert.InRange(At(-40, 0), 3.15, 3.25);    // 叉臂带、圆盘外：3.2
            Assert.InRange(At(-20, 29), 3.15, 3.25);   // 叉臂带、伸进圆盘 x 范围但在圆外（r = 35 > 30）：仍是 3.2，不与环叠加
            Assert.InRange(At(-28, 0), 1.15, 1.25);    // 圆盘内、管孔（r 25.8）外：环厚 1.2（舌片段减掉了圆盘，不叠加）
            Assert.InRange(At(-60, 10), 1.75, 1.85);   // 带外（x < x0）仍是杆厚
            var sh = PlateShapeAnalyzer.Analyze(f);
            Assert.InRange(sh.TabRootThickMm, 3.15, 3.25);
            Assert.InRange(sh.TabEndThickMm, 1.75, 1.85);
            Assert.InRange(sh.TabArmX0Mm, -50, -46);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
