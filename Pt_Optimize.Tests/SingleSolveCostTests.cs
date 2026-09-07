using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **单次场解要多久 —— 对帐超时到底是谁的锅**（2026-09-06）。
///
/// 轨迹已经证明（deliverable/对帐超时_轨迹.txt）：
/// <code>
///   合计 3480.7 g   ← 与命令行验过的**逐字相同**，答案没退化
///   场解 72 次      ← 与历史记录「0.8 档整趟 68 → 75 次」同一量级，次数没变多
///   只跑了 3 轮     ← ③ 在 0.8 档从没被违反过 ⇒ 我新加的候选一次都没花过场解
///   耗时 76.0 分钟  ⇒ **每次场解 ~63 秒**
/// </code>
/// ⇒ 慢的既不是次数也不是逻辑，是**单次场解**。本文件把它单独量出来，
///   并顺手量 <c>DesignSpec.Plate()</c>（我今天在它里面加了 SlotsOf/HolesOf，
///   而耦合解每轮每片都会调它，CoupleMaxRounds 默认 600）。
/// </summary>
public class SingleSolveCostTests
{
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 单次场解与建几何各要多久()
    {
        var d = DesignSpec.Builtin[0].Clone();
        var baseIn = new DesignInputs();
        var sb = new StringBuilder();
        sb.AppendLine("═══ 单次成本分解（0.8 档，导航网格）═══");

        // ① DesignSpec.Plate()：今天在里面加了 SlotsOf / HolesOf
        double floor = d.DiscFloorMm(baseIn);
        var swp = Stopwatch.StartNew();
        const int N = 20000;
        for (int i = 0; i < N; i++) { var g = d.Plate(i & 3, floor); GC.KeepAlive(g); }
        swp.Stop();
        double usPerPlate = swp.Elapsed.TotalMilliseconds * 1000.0 / N;
        sb.AppendLine($"DesignSpec.Plate()　{usPerPlate:0.00} µs/次"
                    + $"（{N} 次共 {swp.Elapsed.TotalMilliseconds:0} ms）");
        sb.AppendLine($"  ⇒ 耦合解最坏 600 轮 × 4 片 = 2400 次 ⇒ {usPerPlate * 2400 / 1000.0:0.0} ms/场解");

        // ② 一次整场解
        var lc0 = d.BuildCase(baseIn, checkRamp: false);
        var sw = Stopwatch.StartNew();
        var r1 = LineRunner.Run(lc0, null, default);
        sw.Stop();
        sb.AppendLine($"Solver.Eval() 第 1 次　{sw.Elapsed.TotalSeconds:0.0} s"
                    + $"（收敛 {r1 is not null}）");

        var lc1 = d.BuildCase(baseIn, checkRamp: false);
        var sw2 = Stopwatch.StartNew();
        var r2 = LineRunner.Run(lc1, null, default);
        sw2.Stop();
        GC.KeepAlive(r2);
        sb.AppendLine($"Solver.Eval() 第 2 次　{sw2.Elapsed.TotalSeconds:0.0} s");

        sb.AppendLine();
        sb.AppendLine("对照：轨迹实测 72 次场解 / 76.0 分钟 ⇒ 63 s/次");
        sb.AppendLine("⇒ 若 Plate() 那一项只有几十 µs，它不是原因；");
        sb.AppendLine("  单次 Eval 若本来就是几十秒，那 60 分钟的对帐预算**从来就不够**，");
        sb.AppendLine("  「超时」拦的是预算，不是退化 —— 预算该按实测重定，并且要打印实际耗时。");

        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "对帐超时_单次成本.txt"),
                          sb.ToString());
        Console.WriteLine(sb.ToString());
        Assert.NotNull(r1);
    }
}
