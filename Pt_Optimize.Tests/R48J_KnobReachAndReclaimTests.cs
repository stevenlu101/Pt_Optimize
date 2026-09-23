using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 J 路 J8（合并把关待办 P0-5，2026-09-17，Opus 5）：**求解器手上有哪些旋钮、其中哪几根退得回去** —— 把 J8 的结论钉住。
///
/// J8 要找的是「导航遍抬过头、细网格遍退不回」的实例。跑到手的两件事（出处见
/// deliverable/J路_J8_结论_旋钮不可达与回收名单_2026-09-17.md，实验原始输出在同目录的 J路_J8_*_本次开跑于*.txt）：
///   ① **实测**：导航遍确实会抬起不在回收名单里的旋钮（盘 R45／两段的算例上抬了片0 的圆盘背侧减重槽张角与板厚）；
///   ② **实测**：那条线上求解器先卡在别处 —— 片2 舌保温抬到上界 20 mm 仍不过「管根低于热偶读数」，
///      而盘径与舌半宽（「增宽」那条路）根本不在求解器手里 ⇒ 这一遍没有解可比，「抬过头」本身**判不了**（不是「没有」）。
/// ⇒ 本轮**不改判定口径**，只把这两条读码事实设成门：名单变了、或者哪天求解器真的拿到了盘径／舌半宽，这里会红，
///    提醒把那份结论一起更新（陈旧的结论比没有结论更坏）。
///
/// 门不手抄生产配方：名单读 <see cref="Solver.ReclaimableKnobs"/>（2026-09-17 从 TightenOnJudgeMesh 原样搬出的那一份）。
/// </summary>
public class R48J_KnobReachAndReclaimTests
{
    private const string ConclusionDoc = "deliverable/J路_J8_结论_旋钮不可达与回收名单_2026-09-17.md";

    /// <summary>
    /// 回收名单恰好是舌保温／环倍率 t₁／外级倍率 t₂ 三根，下角取自求解选项；另外六根不在名单里。
    /// 改回收名单（加一根、去一根、或换下角来源）这里就红 —— 那时请连 J8 结论一起改。
    /// </summary>
    [Fact]
    public void J8_回收名单只有三根_另外六根退不回()
    {
        var opt = new SolverOptions { InsLoMm = 0.4, RingLo = 1.1 };
        var list = Solver.ReclaimableKnobs(opt);
        Assert.Equal(new[] { Solver.Knob.Insul, Solver.Knob.Ring, Solver.Knob.RingT2 }, list.Select(x => x.K).ToArray());
        Assert.Equal(new[] { opt.InsLoMm, opt.RingLo, opt.RingLo }, list.Select(x => x.Lo).ToArray());

        var all = Enum.GetValues<Solver.Knob>();
        Assert.Equal(9, all.Length);
        var stuck = all.Except(list.Select(x => x.K)).ToArray();
        Assert.Equal(new[] { Solver.Knob.Thick, Solver.Knob.RingR1, Solver.Knob.RingR2,
                             Solver.Knob.SlotSpan, Solver.Knob.TabHoleR, Solver.Knob.TabHoleAspect }, stuck);

        // 名单是从生产那一处搬出来的，不是门里另写的一份
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        Assert.Contains("var tunable = ReclaimableKnobs(opt);", s);
        Assert.Contains(ConclusionDoc.Replace("/", "/"), s);   // 结论文件在生产注释里点了名 ⇒ DocRefTests 会核它在不在
    }

    /// <summary>
    /// 行为门：九根旋钮**没有一根**能动盘半径或舌半宽 —— 「增宽」那条路不在求解器手里（J8 结论 ②）。
    /// 逐根写两个值（下界侧与上界侧），前后比盘半径、舌半宽；哪天求解器接了增宽，这里红。
    /// </summary>
    [Fact]
    public void J8_九根旋钮都动不了盘径与舌半宽()
    {
        var p = new DesignInputs();
        var d0 = DesignSpec.W08.Clone().Fit();
        double r0 = d0.DiscRadiusMm, w0 = d0.TabHalfWidthMm;
        Assert.True(r0 > 0 && w0 > 0);
        int moved = 0;
        foreach (var k in Enum.GetValues<Solver.Knob>())
            foreach (double v in new[] { 1.0, 2.0 })
            {
                var d = d0.Clone().Fit();
                // 孔径有 R15 的下限门（0 或 ≥ 1 mm）；1.0／2.0 两个值对九根都合法
                Solver.SetKnob(d, k, 0, v, p, null);
                Assert.True(Math.Abs(d.DiscRadiusMm - r0) < 1e-12, $"{k} 写 {v} 改了盘半径 {r0} → {d.DiscRadiusMm}");
                Assert.True(Math.Abs(d.TabHalfWidthMm - w0) < 1e-12, $"{k} 写 {v} 改了舌半宽 {w0} → {d.TabHalfWidthMm}");
                moved++;
            }
        Assert.Equal(18, moved);   // 非空转：九根各写两次都真的走到了
    }
}
