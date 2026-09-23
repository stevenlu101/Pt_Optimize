using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48 前提核对（2026-09-14，Opus 5 写；物理把关人第五轮第 0 节）：
/// **实验 a / b / c 与保温分界敏感度的单片探针，喂的电流对不对？**
///
/// 起因：同一管根 1093.63 °C 下片2 单片（管侧固定）抽热 −102.9 W、圆盘区 +37～38 K，而整线耦合（圆盘保温 20 mm）是 −12.001 W、+7.897 K；
/// 片0 对得上（−8.2 vs −10.0 W）。单片探针的电流取 1214 / 2102 / 2102 / 1214 A，注释说出自「R48_正式重解_管壁08_2026-09-13.txt 设计电流行」——
/// 物理把关人指出 1214 A 是**升温设计电流**（按 20 °C/h 空管升温算、定截面用），整线里法兰接头走的是反算出的**稳态段电流**经
/// LineSolver.JointCurrentA 合成的值。两者不同，单片探针的工作点就不是整线的工作点。
///
/// 做法：同一设计（管壁 0.8 落点、保温按半径、圆盘保温取算例实际值），整线在**导航网格**上解一次，打印每段稳态电流、每片接头电流（FlangeOut.CurrentA）、
/// 设计电流（DesignCurrent.ForLine 的 PlateA），与 1214 / 2102 并排。只记录，不判定；导航网格只影响电流的小数位，不影响「差不差一个量级」。
/// </summary>
[Trait("速度", "慢")]
public class R48JointCurrentCheckTests
{
    private readonly ITestOutputHelper _out;
    public R48JointCurrentCheckTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 单片探针电流与整线稳态接头电流对照()
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string file = DeliverableOut.Stamped("R48_接头电流核对_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 接头电流核对（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 落点，保温按半径，导航网格");
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        var dc = DesignCurrent.ForLine(d, p, null);
        Say($"圆盘保温（算例实际值）{lc.Base.FlangeInsulThickMm:0.0} mm；网格 {lc.MeshFineMm:0.00}/{lc.MeshCoarseMm:0.00} mm 细区半径 {lc.MeshFineRadiusMm:0}");
        Say($"设计电流（DesignCurrent.ForLine，升温）：段峰值 {string.Join(" / ", dc.SegPeakA.Select(v => v.ToString("0")))} A；接头 {string.Join(" / ", dc.PlateA.Select(v => v.ToString("0")))} A");
        Say("单片探针用的：1214 / 2102 / 2102 / 1214 A");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc, null, default);
        Assert.True(r.Ok, "整线解不出来：" + r.Message);
        Say($"整线（{sw.Elapsed.TotalMinutes:0.0} 分，耦合收敛 {r.Converged}）：段稳态电流 {string.Join(" / ", r.Segments.Select(s => s.CurrentA.ToString("0")))} A");
        Say($"   接头电流（FlangeOut.CurrentA）：{string.Join(" / ", r.Flanges.Select(f => f.CurrentA.ToString("0")))} A");
        Say($"   逐片抽热 {string.Join(" / ", r.Flanges.Select(f => f.QFromTubeW.ToString("+0.00;-0.00")))} W；管根 {string.Join(" / ", r.Flanges.Select(f => f.TRootC.ToString("0.00")))} °C");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            double probe = j == 0 || j == 3 ? 1214 : 2102;
            Say($"   片{j}：稳态接头 {r.Flanges[j].CurrentA:0} A　探针 {probe:0} A　比 {probe / r.Flanges[j].CurrentA:0.000}　焦耳热比约 {Math.Pow(probe / r.Flanges[j].CurrentA, 2):0.00}");
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
