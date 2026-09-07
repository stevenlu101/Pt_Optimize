using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **代码自己说「该解不存在」，就不许把它当解交出去**（2026-09-07，督导第 8 封）。
///
/// 改之前：<c>LineRunner</c> 遇到 <c>th.OverMelt</c> **只加一条 Note**，
/// <c>Ok</c> 与 <c>Converged</c> 都留 true；而 <c>AllOk</c> 只看 Converged + Checks，
/// 熔点又不是 Check ⇒ **一份峰值 2900 °C 的解可以全绿交付**。
/// 实测：OverMelt 之后置 Ok/Converged=false 的次数 **0**；Criteria 里与「熔」有关的条目 **0**。
///
/// ⚠ ②″ 挡不住：它判**圆盘**峰值（TDiscMaxC），而熔点看的是**整片**峰值（TMaxC）——
///   舌片、压接段的热点不在 ②″ 口径里。
/// ⚠ 旧路 <c>FlangeAutoSizer</c> 专门为它写过中止闸，换代到 Solver 之后没了 ——
///   与 S1 丢掉 Converged 是同一形状。
/// ⚠ 督导原话照记：他**没有**造出「判据全过却熔化」的算例，
///   证明的是「没有任何东西挡着它」。两句不一样。
/// </summary>
public class MeltIsNotASolutionTests
{
    /// <summary>★★★★★ 结构：越熔点那一支**必须**让结果不可交付，不能只留一条 Note。</summary>
    [Fact]
    public void 越过熔点必须让结果不可交付()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        int at = s.IndexOf("if (th.OverMelt)", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到熔点那一支 —— 它改名了？本门要跟着改");

        // 取到**下一个分支**为止，不用「前 N 字符」那种魔法窗口 ——
        // 第一版写 900 字符，被我自己那段长注释撑爆，门当场误报（2026-09-07 现场栽的）。
        int end = s.IndexOf("else if (th.OverFitRange)", at, StringComparison.Ordinal);
        Assert.True(end > at, "找不到熔点分支的结尾（下一支 OverFitRange）—— 结构变了，本门要跟着改");
        string body = s[at..end];
        Assert.True(body.Contains("res.Ok = false") || body.Contains("res.Converged = false"),
            "越过铂熔点之后**只加了一条 Note**，Ok/Converged 都还是 true ⇒ "
          + "AllOk 不看熔点，这份『该解不存在』的解可以全绿交付。"
          + "Note 是给人看的，门是给流程用的 —— 这里需要的是门。");
    }

    /// <summary>
    /// ★★★★ 行为：<c>OverMelt</c> 的判据本身是闭式的，直接验它的定义没有漂移。
    /// （造一个真熔化的整线算例代价太高；这一条守的是「阈值没被悄悄放宽」。）
    /// </summary>
    [Fact]
    public void 熔点阈值就是铂熔点()
    {
        var th = new ShellThermalResult { TMaxC = Materials.PtMeltC + 1 };
        Assert.True(th.OverMelt, "峰值高于铂熔点却不算越过 —— 阈值被放宽了");
        var ok = new ShellThermalResult { TMaxC = Materials.PtMeltC - 1 };
        Assert.False(ok.OverMelt, "峰值低于铂熔点却算越过 —— 会天天误报");
    }
}
