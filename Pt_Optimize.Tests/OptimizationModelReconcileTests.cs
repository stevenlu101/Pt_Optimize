using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **总纲说的自由度 ↔ 代码真搜的旋钮，必须对得上**（2026-09-03）。
///
/// 用户 2026-09-03：「如果逻辑不对，整个 APP 的开发就没有任何意义」。
///
/// §0.0 的目标函数与 X1–X6 是 2026-08-11 定的**意图**；代码里真正能搜的是另一回事。
/// 两者会漂，而且**漂了看不出来** —— APP 会安静地去解一个和总纲不同的问题，
/// 判据照样出数、报告照样好看。这正是本项目最贵的那种错。
///
/// ⇒ HANDOVER §0.0.3 那张对帐表是**唯一**记「实况」的地方，本门盯着它别过期：
///   · 代码新增了旋钮 → 表里没有 → 红（提醒把新能力记进对帐）
///   · 表里写了旋钮 → 代码没有 → 红（提醒别把没做的写成做了）
///
/// ⚠ 本门**不判对错**，只判「说的」与「做的」一不一致。
///   要改变能力去改代码，改完把表改了；不许只改一边。
/// </summary>
public class OptimizationModelReconcileTests
{
    private const string Anchor = "0.0.3 自由度对帐";

    private static string Section()
    {
        string doc = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "HANDOVER.md"));
        int a = doc.IndexOf(Anchor, StringComparison.Ordinal);
        Assert.True(a > 0, "HANDOVER 里找不到「" + Anchor + "」这一节 —— 断言失去了对象");
        int b = doc.IndexOf("### 边界条件", a, StringComparison.Ordinal);
        Assert.True(b > a, "找不到该节的结尾");
        return doc[a..b];
    }

    /// <summary>★★★ 每一个真旋钮都要在对帐表里露面（新增旋钮不许悄悄溜进来）。</summary>
    [Fact]
    public void 每个旋钮都在对帐表里()
    {
        string sec = Section();
        var knobs = Enum.GetValues<Solver.Knob>();
        Assert.NotEmpty(knobs);                       // 自证
        foreach (var k in knobs)
            Assert.True(sec.Contains("Knob." + k, StringComparison.Ordinal),
                $"新旋钮 Knob.{k}（{Solver.KnobName(k)}）没进 HANDOVER §0.0.3 对帐表 —— "
              + "能力变了就得把实况记下来，否则下一轮又要从头查一遍");
    }

    /// <summary>
    /// ★★★ 表里说「不是旋钮」的，代码里就**真的不许有**。
    /// 哪天把它做出来了，这条会红 —— 那时该做的是把表改成「已接」。
    /// </summary>
    [Fact]
    public void 表里说没做的代码里就真的没做()
    {
        string sec = Section();
        var names = Enum.GetValues<Solver.Knob>().Select(k => k.ToString()).ToArray();

        if (sec.Contains("各级**半径** r₁…rₙ | ✗", StringComparison.Ordinal))
            Assert.DoesNotContain(names, n => n.StartsWith("RingR", StringComparison.Ordinal));
    }

    /// <summary>
    /// ★★★ 目标必须写在那一节里，而且写清「最省不是收尾工序」。
    /// 只写「能用」的目标会带出一个又重又能用的解 —— 那不是交付物。
    /// </summary>
    [Fact]
    public void 目标函数与最省的口径都写清楚了()
    {
        string sec = Section();
        Assert.Contains("min", sec);
        Assert.Contains("判据全过", sec);
        Assert.Contains("每克铂买到", sec);
        Assert.Contains("不是收尾工序", sec);
    }

    /// <summary>
    /// ★★ 两条输入路径的链路图要在（用户 2026-09-03 建议用链路图，
    /// 而「在对话里的清单等于没有清单」—— 图必须在仓库里）。
    /// </summary>
    [Fact]
    public void 两条输入路径的链路图在仓库里()
    {
        string sec = Section();
        Assert.Contains("mermaid", sec);
        Assert.Contains("Solver.Solve", sec);
        Assert.Contains("SolveByLevel", sec);
        Assert.Contains("LineRunner.Judge", sec);      // 判据唯一来源要画进去
    }

    /// <summary>
    /// ★★ 那个尚未确认的瓶颈要留在文档里 —— 「算得出方案却交不出图」不算交付。
    /// 确认之后把它改写成结论，而不是让它悄悄消失。
    /// </summary>
    [Fact]
    public void 出图瓶颈还挂在文档上()
    {
        string sec = Section();
        Assert.Contains("ScalePlate3dm", sec);
        Assert.Contains("交不出图", sec);
    }

    /// <summary>
    /// ★ 自证：几何层**确实**支持任意级数 —— 这是「缺口全在搜索层」这句话的依据。
    /// 它若哪天不成立了，上面整节的推论都要重写。
    /// </summary>
    [Fact]
    public void 自证_几何层支持任意级数()
    {
        var p = new FlangePlate
        {
            DiscStepRadiiMm = new[] { 30.0, 35.0, 40.0, 45.0 },
            DiscStepThicknessMm = new[] { 2.0, 1.6, 1.2, 0.9 },
        };
        Assert.Equal(4, p.DiscStepRadiiMm.Length);
        Assert.Equal(p.DiscStepRadiiMm.Length, p.DiscStepThicknessMm.Length);
    }
}
