using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R32（用户 2026-09-10：「R23(不挖孔)，R29(挖孔)，在R23与R29分别独立选出最优解，R23与R29不比较重量」）：
/// 解法族开关 —— 不挖舌孔族里舌孔两根旋钮不进候选，其余照旧；挖舌孔族全进。两族各自走到最小可行点，互不比价。
/// </summary>
public class SolverFamilyTests
{
    [Fact]
    public void 不挖舌孔族_舌孔两根旋钮不进候选_其余照旧()
    {
        var no = new SolverOptions { AllowTabCuts = false };
        var yes = new SolverOptions { AllowTabCuts = true };
        // R48 B（2026-09-14 Opus 5）：有意改动 —— 分派表「抽热太多」那一排的键从旧判法 FlangeDip 换成冷侧 ColdUnderTc（旋钮候选原样继承），依据 Pt_Optimize/Core/Solver.cs 的 Allocation。
        var kNo = Solver.KnobsFor(LineResult.Key.ColdUnderTc, no);
        var kYes = Solver.KnobsFor(LineResult.Key.ColdUnderTc, yes);
        Assert.DoesNotContain(Solver.Knob.TabHoleR, kNo);
        Assert.DoesNotContain(Solver.Knob.TabHoleAspect, kNo);
        Assert.Contains(Solver.Knob.Insul, kNo);
        Assert.Contains(Solver.Knob.SlotSpan, kNo);                 // 圆盘背侧槽不是「舌孔」，两族都有
        Assert.Contains(Solver.Knob.TabHoleR, kYes);
        Assert.Contains(Solver.Knob.TabHoleAspect, kYes);
        Assert.Equal(kYes.Length - 2, kNo.Length);
        // 别的判据不受族影响
        Assert.Equal(Solver.KnobsFor(LineResult.Key.NetFlux, yes), Solver.KnobsFor(LineResult.Key.NetFlux, no));
    }

    [Fact]
    public void 默认是挖舌孔族_旧调用逐位不变()
    {
        Assert.True(new SolverOptions().AllowTabCuts);
    }
}
