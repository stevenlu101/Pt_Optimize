using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **补不上的时候要留着、还要接着补** —— 组合就是这么攒出来的（2026-09-05 出图时抓到）。
///
/// 用户：「最有效的那根旋钮（举例：可能是**圆盘槽 + 舌板椭圆孔**），
/// 要各种在不同位置的孔形状**组合**所得出」。
///
/// 两个连着的坑，一个比一个隐蔽：
/// <code>
///   坑1  ChooseKnob 写「都补不上时…先用买得最多的**顶上去**，下一轮缺口变小再挑便宜的」
///        RaiseUntil 却 `Set(d, knob, j, lo)` **退回原值** ⇒ 什么都不累积
///   坑2  改成留着之后，调用方仍然 `bad = true; break;` ⇒ 留完就收摊，
///        全程**只动了一根**旋钮 —— 「下一轮」根本不存在
/// </code>
///
/// 实测三档（deliverable/）：
/// <code>
///   修前（全退回）        11582 g   圆盘槽 0°、孔径 0.0        ← 原始几何，白算 14 分钟
///   只留不续轮            11168 g   只有片0 拿到孔径 36        ← 其余三片一根没动
///   留 + 续轮             见 优化后出图.txt
/// </code>
///
/// ⚠ 本文件的主门是**行为门**（真跑求解器，看它自己攒不攒得出组合）。
///   贴源码字面的断言只留「反向不许发生」那几条 —— 它们钉的是**不变式**，不是措辞。
/// </summary>
public class ComboAccumulateTests
{
    private static string Src() => File.ReadAllText(
        Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));

    // ══════════════════════════════════════════════════════════════════════
    //  行为门：单独用都不过 ⇒ 求解器必须**自己**把几根旋钮攒到一起
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★★★ **主门**：拿一个「任何单根旋钮抬到上界都补不上」的构型
    /// （Pt_Heater1 交接过来那个，③ 法兰增量温降 427 K / 限 10）跑求解器，
    /// 要求交出来的设计里**不止一根**旋钮离开了原值。
    ///
    /// 这一条正是用户 R10 的验收标准：「造一个『单独用都不过、合起来才过』的算例，
    /// 求解器**自己找到那个组合**」。这里先钉住**组合真的形成**这一半 ——
    /// 「合起来就过」那一半由 <c>ExportOptimized3dmTests</c> 的交付物负责，
    /// 因为它要跑细网格，几十分钟起跳，不适合当门。
    ///
    /// ⚠ **成本：实测 1 小时 21 分**（2026-09-05，四片 × 三段 × 导航网格）。
    ///   我原以为「只跑导航网格就是几分钟」—— 错了，贵的是 ChooseKnob：
    ///   每个候选都要抬到上界量一次，一次就是一整遍场解。
    ///   这是本仓库最慢的门，但它是「求解器到底会不会自己攒组合」的**唯一**实测证据，
    ///   源码字面断言替代不了。跑全套测试时要把这一小时算进去。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 单根补不上时求解器自己攒出组合()
    {
        var baseIn = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        // 与 ExportOptimized3dmTests 同一个构型（Pt_Heater1 交接到解析路之后）
        d.Name = "组合累积门";
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 2.0;

        var before = d.Clone();
        var sr = Solver.Solve(d, baseIn, new SolverOptions
        {
            FineMm = 0,              // 只走导航网格 —— 本门看的是「攒不攒」，不是「准不准」
            FineRadiusMm = 0,
            MaxRounds = 12,
            MaxPartialRounds = 3,
        });

        var o = sr.Design;
        Assert.NotNull(o);

        // 数一数：有几根旋钮离开了原值
        var moved = Moved(before, o!);
        Assert.True(moved.Count >= 2,
            "求解器只动了 " + moved.Count + " 根旋钮（" + string.Join("、", moved) + "）—— "
          + "任何单根都补不上这个构型，只动一根说明「补不上就收摊」那条路又回来了。"
          + "停在：" + sr.Message);
    }

    /// <summary>
    /// ★★★ **续轮不许无限续**。两道闸都得在：缺口没变小当场停、续轮次数封顶。
    /// 少任何一道，遇到「每轮只小一点点」的构型就会磨到用户等不下去。
    /// </summary>
    [Fact]
    public void 续轮有两道终止闸()
    {
        string s = Src();
        Assert.Contains("MaxPartialRounds", s);          // 闸②：次数封顶
        Assert.Contains("缺口没再变小", s);               // 闸①：不许原地打转
        Assert.True(new SolverOptions().MaxPartialRounds > 0,
            "默认封顶是 0 ⇒ 续轮等于没开，组合又攒不出来了");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  反向门：这几件事**不许**发生
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★ 二分成功那一支**不许**跟着改 —— 它要的是「刚好够」的最小值（最省铂）。
    /// 把「留着」推广到成功分支就会得到又重又能用的解。
    /// </summary>
    [Fact]
    public void 二分成功那一支仍取刚好够的最小值()
    {
        string s = Src();
        Assert.Contains("二分：找「刚好不违反」的最小值", s);
        Assert.Contains("Set(d, knob, j, snapped);", s);
    }

    /// <summary>
    /// ★★★★ **留着 ≠ 报成过了**。这是「把失败渲染成通过」那一族的入口 ——
    /// 判据仍然不过，返回值就必须还是 false，续轮只是「还没到认输的时候」。
    /// </summary>
    [Fact]
    public void 留着不等于报成过了()
    {
        string s = Src();
        int at = s.IndexOf("bool kept = ", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到那支「补不上但留着」的分支");
        int ret = s.IndexOf("return (", at, StringComparison.Ordinal);
        Assert.True(ret > 0);
        Assert.StartsWith("return (false,", s[ret..]);
    }

    /// <summary>★★ 一点没变好才退回 —— 那是白花铂，留着没意义。</summary>
    [Fact]
    public void 一点没变好才退回()
    {
        string s = Src();
        int at = s.IndexOf("bool kept = ", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("Set(d, knob, j, lo);        // 一点没变好", s[at..]);
    }

    // ── 帮手 ────────────────────────────────────────────────────────────────

    /// <summary>逐片逐旋钮比一遍，列出「离开了原值」的那些（人话名字）。</summary>
    private static System.Collections.Generic.List<string> Moved(DesignSpec a, DesignSpec b)
    {
        var list = new System.Collections.Generic.List<string>();
        int np = a.TabThickMm.Length;
        void Chk(string nm, Func<DesignSpec, int, double> get)
        {
            for (int j = 0; j < np; j++)
                if (Math.Abs(get(a, j) - get(b, j)) > 1e-9)
                    list.Add($"片{j} {nm} {get(a, j):0.###}→{get(b, j):0.###}");
        }
        Chk("板厚",   (x, j) => x.TabThickMm[j]);
        Chk("舌保温", (x, j) => x.TabInsulMm[j]);
        Chk("环倍率", (x, j) => x.RingMul[j]);
        Chk("外级",   (x, j) => x.RingMulOuter(j));
        Chk("圆盘槽", (x, j) => j < x.SlotSpanDeg.Length ? x.SlotSpanDeg[j] : 0);
        Chk("孔径",   (x, j) => j < x.TabHoleRMm.Length ? x.TabHoleRMm[j] : 0);
        Chk("孔拉长", (x, j) => j < x.TabHoleAspect.Length ? x.TabHoleAspect[j] : 1);
        return list;
    }
}
