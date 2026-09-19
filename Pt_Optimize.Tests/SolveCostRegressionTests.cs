using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **0.8 档对帐从「跑得完」变成「60 分钟跑满超时」——是哪一处改动吃掉的时间**
/// （2026-09-06）。
///
/// 事实：`UiWiring.exe --reconcile 0.8 3480.7` 在 2026-08-30 是绿的（3480.7 g 全判据过）；
/// 今天改完求解器之后，同一条命令在 **3,600,000 ms 预算内跑不完**。
/// 答案退没退化**不知道** —— 它压根没跑到出答案那一步。
///
/// 今天动过、且会影响成本的四处：
/// <code>
///   ① 续轮 MaxPartialRounds = 6      每续一轮 = **一整遍场解**
///   ② 一片卡住不再 break             同一轮里多跑其余片的候选比价
///   ③ TabHoleAspect 进 FlangeDip     该判据的候选 3 → 4，每次挑多一次场解
///   ④ 比价符号                       只改挑谁，不改次数（但会改轨迹 ⇒ 间接改轮数）
/// </code>
///
/// 本文件用 <c>SolverResult.Solves</c>（代码自己说的「求根的**真实成本**，比轮数诚实」）
/// 把 ① 单独量出来 —— 导航网格、可关可开，分钟级，不用等一小时。
/// </summary>
public class SolveCostRegressionTests
{
    /// <summary>0.8 档（W08）那个构型 —— 对帐用的就是它。</summary>
    private static DesignSpec W08() => DesignSpec.Builtin[0].Clone();   // Builtin = { W08, W06, Retired08, Retired06 }

    private static (int Solves, double Sec, double MassG, bool Feasible, string Why) Run(int partial)
    {
        var d = W08();
        var sw = Stopwatch.StartNew();
        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            FineMm = 0, FineRadiusMm = 0,     // 只走导航网格 —— 本门比的是**成本比值**
            MaxRounds = 60,
            MaxPartialRounds = partial,
        });
        sw.Stop();
        return (sr.Solves, sw.Elapsed.TotalSeconds,
                sr.MassG, sr.Feasible, sr.StopWhy);
    }

    /// <summary>
    /// ★★★★★ 续轮开与关，成本差多少。**只记录事实**，把数摆出来给人判断。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 续轮吃掉多少成本()
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ 0.8 档：续轮（MaxPartialRounds）吃掉多少成本 ═══");
        sb.AppendLine("构型 = DesignSpec 内置 0.8 档（盘R30、孔R25.8、舌 140×60），导航网格，无细网格第二遍");
        sb.AppendLine();
        sb.AppendLine("续轮上限\t场解次数\t耗时 s\t合计 g\t可行\t停在哪");

        var off = Run(0);
        sb.AppendLine($"0（关）\t{off.Solves}\t{off.Sec:0.0}\t{off.MassG:0.0}\t{off.Feasible}\t{Short(off.Why)}");

        var on = Run(6);
        sb.AppendLine($"6（现默认）\t{on.Solves}\t{on.Sec:0.0}\t{on.MassG:0.0}\t{on.Feasible}\t{Short(on.Why)}");

        sb.AppendLine();
        double ratio = off.Solves > 0 ? (double)on.Solves / off.Solves : double.NaN;
        sb.AppendLine($"⇒ 场解次数 ×{ratio:0.00}、耗时 ×{on.Sec / Math.Max(off.Sec, 1e-9):0.00}");
        sb.AppendLine($"⇒ 合计铂重 {off.MassG:0.0} g → {on.MassG:0.0} g"
                    + (Math.Abs(on.MassG - off.MassG) < 0.05 ? "（**没变**）" : "（**变了**）"));
        sb.AppendLine();
        sb.AppendLine("⚠ 这里是导航网格的口径，绝对值不能拿去对帐；");
        sb.AppendLine("   对帐要的是细网格第二遍，那一遍每轮还要贵一个量级。");

        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped("对帐超时_成本归因.txt"),
                          sb.ToString());
        Console.WriteLine(sb.ToString());

        Assert.True(off.Solves > 0 && on.Solves > 0, "场解次数没记下来 —— SolverResult.Solves 没在数");
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
