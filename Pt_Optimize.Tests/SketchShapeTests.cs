using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（慢，2026-09-10）：用户「拍脑袋」画的侧 Y 形（锥形舌片 + 长圆角三角槽，宽端朝盘、圆头朝铜排，两臂各约 13 mm）
/// 按图上的比例喂给 APP：Ø56、内径 50、舌长 146、舌端半宽 15、槽长约 80、宽端约 30。
/// 舌片厚与叉臂厚按 J=10 闭式定（R23/R29），然后在导航网格上解一次整线，看它的铂重与「法兰增量温降」，
/// 和 APP 自己搜出来的 Ø56 平行舌（2603.8 g、③ 9.06 K）摆在一起。只看不判：落 deliverable/拍脑袋Y形_核算.txt。
/// </summary>
public class SketchShapeTests
{
    [Trait("速度", "慢")]
    [Fact]
    public void 用户拍脑袋的Y形_按J定臂厚后核算()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "拍脑袋Y形";
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 15; d.TabLengthMm = 146; d.TabTaper = true;
        for (int j = 0; j < d.FlangeCount; j++)
        {
            d.TabHoleSides[j] = 3;                     // 圆角三角
            // 圆角三角：0° 时一个顶点朝 +z，AspectXZ 拉伸的是本地 x（= 底边方向）。要「顺舌片方向长、横向窄」的等腰三角，
            //   就得把本地 x **压扁**（aspect < 1），再转 90° 让顶点朝铜排、底边朝盘。
            //   第一版写成 aspect 2.2 + 90°，槽横过来 60 mm 宽把舌片切断（J=∞）——仪器抓到的，记在档案里。
            d.TabHoleRMm[j] = 28;                      // 等面积半径 ⇒ 外接半径约 48，沿舌片长约 1.6×48 ≈ 77 mm
            d.TabHoleAspect[j] = 0.36;                 // 横向压到约 36 mm 宽（图上宽端约 30，两臂各约 12）
            d.TabHoleXMm[j] = -50;                     // 宽端到 x≈−4（切点 −2.5 旁），圆头到 x≈−80
            d.TabHoleRotDeg[j] = 90;                   // 宽端朝盘（0° 顶点朝 +z ⇒ 90° 顶点朝铜排）
            d.TabInsulMm[j] = 3.0;
        }
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "拍脑袋Y形_核算.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══ 用户拍脑袋的侧 Y 形（2026-09-10 图），按图上比例：Ø56／内径 50／舌长 146／舌端半宽 15 锥形／圆角三角槽 宽端朝盘 ═══");

        var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, new SolverOptions(), res, null, null);     // 设计电流 + 舌片厚／叉臂厚闭式
        var g = d.Plate(1, d.DiscFloorMm(p));
        var h = g.TabHoles[0];
        double xLo = double.NaN, xHi = double.NaN, zMax = 0;
        for (double x = -146; x <= 0; x += 0.5) for (double z = -30; z <= 30; z += 0.5)
            if (h.Contains(x, z)) { xLo = double.IsNaN(xLo) ? x : Math.Min(xLo, x); xHi = double.IsNaN(xHi) ? x : Math.Max(xHi, x); zMax = Math.Max(zMax, Math.Abs(z)); }
        sb.AppendLine($"槽的实际范围：x ∈ [{xLo:0.0}, {xHi:0.0}]（长 {xHi - xLo:0.0}），|z| ≤ {zMax:0.0}（宽 {2 * zMax:0.0}）；切点 x = {d.TangentXMm():0.0}，切点半宽 {g.Tangent().HalfW:0.0}");
        var widths = SectionSizing.TabWidths(g, d.ClampLengthMm);
        var wMin = widths.OrderBy(t => t.WidthMm).First();
        sb.AppendLine($"舌片最窄有效宽 {wMin.WidthMm:0.0} mm @x={wMin.X:0.0}（两臂合计）");
        sb.AppendLine("设计：" + d.Describe());
        sb.AppendLine($"闭式截面：{string.Join("　", Enumerable.Range(0, d.FlangeCount).Select(j => { var c = SectionSizing.Worst(d.Plate(j, d.DiscFloorMm(p)), res.DesignCurrent!.PlateA[j], d.ClampLengthMm); return $"片{j} 最紧 {c.Where} J={c.JAPerMm2:0.00}"; }))}");

        var lc = d.BuildCase(p, checkRamp: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lr = LineRunner.Run(lc, null, default);
        sb.AppendLine($"═══ 导航网格解一次：{sw.Elapsed.TotalMinutes:0.0} 分　收敛 {lr.Converged}　全判据 {lr.AllOk}　合计 {lr.TotalMassG:0.0} g（法兰 {lr.FlangeMassG:0.0} g）");
        foreach (var c in lr.Checks)
            sb.AppendLine($"  {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}");
        sb.AppendLine();
        sb.AppendLine("对照：APP 自己搜出来的 Ø56/舌56 平行舌（不开孔）细网格全判据过 2603.8 g，③ 9.06/10 K（deliverable/细网格复算_盘56舌56.txt）。");
        File.WriteAllText(dump, sb.ToString());
        Assert.True(lr.Checks.Count() > 0);
    }
}
