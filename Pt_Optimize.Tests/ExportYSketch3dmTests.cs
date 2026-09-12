using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 用户 2026-09-11：「Y型法兰有计算的3DM结果吗?」—— 此前只有核算记录（deliverable/拍脑袋Y形_核算.txt），没出过图。
/// 这道慢门把 SketchShapeTests 里那个 Y 形（用户 09-10 手画：Ø56／舌 146×30 锥形／圆角三角槽宽端朝盘）交给求解器定旋钮，
/// 再用 APP 出图的同一条路（Geometry3dm.WriteFinal3dm，与「导出本页 3DM」同一个函数）把解出的几何写成 .3dm，
/// 落在 deliverable/拍脑袋Y形_解出/ 下，并把解出的设计与判据写进旁边的 txt。
/// </summary>
public class ExportYSketch3dmTests
{
    [Trait("速度", "慢")]
    [Fact]
    public void 用户拍脑袋的Y形_解出后出3DM()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "拍脑袋Y形";
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 15; d.TabLengthMm = 146; d.TabTaper = true;
        for (int j = 0; j < d.FlangeCount; j++)
        {
            d.TabHoleSides[j] = 3;
            d.TabHoleRMm[j] = 28;
            d.TabHoleAspect[j] = 0.36;
            d.TabHoleXMm[j] = -50;
            d.TabHoleRotDeg[j] = 90;
            d.TabInsulMm[j] = 3.0;
        }
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable", "拍脑袋Y形_解出");
        Directory.CreateDirectory(dir);
        string txt = Path.Combine(dir, "核算与出图.txt");
        var sb = new StringBuilder();
        sb.AppendLine("═══ 用户手画的侧 Y 形（2026-09-10 图）：与 SketchShapeTests 同一构型，交给求解器定旋钮后出 3DM ═══");
        sb.AppendLine("起点：" + d.Describe());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sr = Solver.Solve(d, p, new SolverOptions { MaxRounds = 12 }, null, default);
        sb.AppendLine($"═══ 求解器：耗时 {sw.Elapsed.TotalMinutes:0.0} 分　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g　停在：{sr.StopWhy}");
        Assert.NotNull(sr.Design);
        sb.AppendLine("解出的设计：" + sr.Design!.Describe());
        if (sr.Best is { } bb)
            foreach (var c in bb.Checks)
                sb.AppendLine($"  {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}");
        // 出图：与「导出本页 3DM」同一个函数（2 段 3 片）
        string out3dm = Path.Combine(dir, "拍脑袋Y形_整机.3dm");
        string echo = Geometry3dm.WriteFinal3dm(sr.Design!, out3dm, tubeIdMm: 50.0, segLenMm: 300.0, segCount: 2, baseIn: p);
        sb.AppendLine("═══ 出图（Geometry3dm.WriteFinal3dm）═══");
        sb.AppendLine(echo);
        File.WriteAllText(txt, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(out3dm), "3dm 没写出来");
        Assert.True(new FileInfo(out3dm).Length > 10_000, "3dm 太小，多半是空的");
    }
}
