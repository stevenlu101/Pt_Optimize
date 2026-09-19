using System;
using System.IO;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路快门：**二分中止时，旋钮必须退回进来时的那个值** —— 2026-09-18，Opus 5
//
//  病是跑出来的，不是想出来的。实测出处：
//    deliverable/R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_021706.txt
//  圆盘保温 10 mm 下重解 W08，**片3 舌保温在 13.036 mm 处场解不出来 ⇒ 二分中止**。
//  中止那一支写的是 `SetKnob(..., lo, ...)`，而 `lo` 在二分循环里已经被抬到走过的中点：
//    片3 舌保温 = **12.997265625 mm = 25.99 层** —— **不在图纸格 0.5 上，现场包不出来**，
//  而它照样被印成「停机时各旋钮的值」交给工程师。
//  第二层错：不变式说 `lo` 是**违反**侧的点，留着它既治不了判据也多花铂。
//
//  ⇒ 中止 = 这一次尝试什么都没得到 ⇒ 退回 `lo0`（进这一步时的原值，一定在图纸格上）。
//
//  ⚠ 与「抬到上界仍不过 ⇒ 留着不退回」（2026-09-05）**不冲突**：
//    那一支留的是**评估过**的合法上界点；这一支丢的是**没评估成**的中间点。
//
//  行为门在两道慢门里（R48LDiscInsul10ResolveW08Tests／W06Tests 的
//  「解出来的舌保温必须落在 {裸舌} ∪ {0.5 的倍数} 上」）—— 本病正是被它们抓到的。
//  这里补源码门：那一行改回 `lo` 会立刻红，而那种改动**不会有任何报错**。
// ════════════════════════════════════════════════════════════════════════════

public class R48LBisectAbortGateTests
{
    private static string Solver() =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));

    /// <summary>中止那一支退回 lo0；lo0 只赋值一次（进来时那一下）。</summary>
    [Fact]
    public void 门_二分中止退回进来时的原值()
    {
        string s = Solver();

        // lo0 存在、只被赋值一次（谁再写一次 lo0 = … 就等于把「原值」也挪了）
        Assert.Contains("double lo0 = lo;", s);
        Assert.Equal(1, Regex.Matches(s, @"\blo0\s*=(?!=)").Count);

        // 中止那一支退回 lo0
        Assert.Matches(new Regex(@"BranchMarks\.UndeterminedBisect", RegexOptions.None), s);
        var abort = Regex.Match(s, @"if \(rMid is null\)[\s\S]{0,2200}?BranchMarks\.UndeterminedBisect");
        Assert.True(abort.Success, "找不到「中点判不了 ⇒ 中止二分」那一支 —— 断言失去了对象");
        Assert.Contains("SetKnob(d, knob, j, lo0, baseIn, res);", abort.Value);
        Assert.DoesNotContain("SetKnob(d, knob, j, lo, baseIn, res);", abort.Value);
    }

    /// <summary>
    /// 自证：二分循环**确实会把 lo 抬上去** —— 没有这一行，上面那道门就是空守
    /// （lo 永远等于 lo0，退回哪一个都一样）。
    /// </summary>
    [Fact]
    public void 自证_二分循环真的会把lo抬到中点()
    {
        string s = Solver();
        Assert.Contains("hi = mid; else lo = mid;", s);

        // 而中点**不落在图纸格上** —— 拿生产的图纸格与二分点函数当场算一次，不手抄
        var o = new SolverOptions();
        double q = Solver_KnobQuantum(o);
        double lo = 12.5, hi = 20.0;                       // 两端都在 0.5 的格子上
        double mid = PtOptimize.Core.Solver.NextBisectPoint(PtOptimize.Core.Solver.Knob.Insul, lo, hi);
        Assert.True(Math.Abs(mid / q - Math.Round(mid / q)) > 1e-9,
            $"二分中点 {mid} 恰好落在图纸格 {q} 上 —— 这个自证要换个例子，否则上面那道门证不出东西");
    }

    private static double Solver_KnobQuantum(SolverOptions o)
        => PtOptimize.Core.Solver.KnobQuantum(o, PtOptimize.Core.Solver.Knob.Insul);
}
