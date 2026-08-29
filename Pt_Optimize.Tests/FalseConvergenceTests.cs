using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **「变化小」不等于「收敛」**（2026-08-30，一次 4 小时 22 分的实测推翻了旧判据）。
///
/// ══ 实测
///
/// 同一个 0.6 档设计，两条网格阶梯：
///
/// <code>
///   粗阶梯（现行默认）        细阶梯（--weldfeature）
///   1.000  ③ 8.573           0.583  ③ 7.547
///   0.500  ③ 7.685  ⇒ 收敛    0.292  ③ 9.102
///                             0.146  ③ 9.462  ⇒ 收敛
/// </code>
///
/// 粗阶梯报的 7.685 与真值 9.462 差 **1.777 K —— 比 ③ 的容差 1.0 还大**。
///
/// ══ 病因：③ 随网格**非单调**（先降后升）
///
/// 粗阶梯那两级恰好跨在拐点两侧，差值 −0.888 小于容差**纯属巧合**。
/// 这正是「碰巧两级之间没动」的假收敛 —— 而它**不报错**，报的是「✓ 网格无关」。
///
/// ══ 修法（两条都是从「收敛」这个词本身推出来的）
///
/// <code>
///   ① 至少三档     —— 两个差值才谈得上「趋势」，一个差值只是一个数
///   ② 变化在缩小   —— 收敛的定义就是余项趋零；不缩小就不是在收敛
/// </code>
///
/// ⚠ 一并更正我自己 2026-08-29 的说法：那次 0.6 档「8 分钟跑完」被我归给
///   「CG 36× + 焊脚出表」两件事。**CG 的 36× 是真的，起步粗一级不是** ——
///   省下的那部分时间是假收敛买来的。
/// </summary>
public class FalseConvergenceTests
{
    private static List<MeshAdapt.Delta> D(double d2p, double d3, double d2pp) => new()
    {
        new() { Name = "②′", Change = d2p,  Tol = 0.5 },
        new() { Name = "③",  Change = d3,   Tol = 1.0 },
        new() { Name = "②″", Change = d2pp, Tol = 0.2 },
    };

    /// <summary>
    /// ★★★ **就是那一次**：粗阶梯只有一对差值，全在容差内 —— 旧判据说收敛，新判据说不。
    /// 这一条是本组的核心：它用的就是那次跑出来的真数。
    /// </summary>
    [Fact]
    public void 粗阶梯那一对不算收敛()
    {
        // 1.000 → 0.500：②′ 1.235→1.090　②″ −0.019→−0.009　③ 8.573→7.685
        var pair = D(1.090 - 1.235, 7.685 - 8.573, -0.009 - -0.019);
        Assert.All(pair, d => Assert.True(d.Within, $"{d.Name} 本来就在容差内：{d.Change}"));

        Assert.False(MeshAdapt.Converged(pair),           // 没有上一对 ⇒ 只有两档
            "只有一对差值就判收敛 —— 这正是那次 1.777 K 错值的来路");
        Assert.False(MeshAdapt.Converged(pair, null));
    }

    /// <summary>★★★ 细阶梯三档、变化在缩小 ⇒ 该判收敛。门不许两边都红。</summary>
    [Fact]
    public void 细阶梯三档且在缩小算收敛()
    {
        // 0.583 → 0.292：②′ 1.033→1.486　②″ −0.013→−0.007　③ 7.547→9.102
        var first = D(1.486 - 1.033, 9.102 - 7.547, -0.007 - -0.013);
        // 0.292 → 0.146：②′ 1.486→1.609　②″ −0.007→−0.004　③ 9.102→9.462
        var second = D(1.609 - 1.486, 9.462 - 9.102, -0.004 - -0.007);

        // 第一对里 ③ 变了 +1.555 > 1.0 ⇒ 本来就不该收敛
        Assert.False(MeshAdapt.Converged(first, null));
        // 第二对：都在容差内，且每条都比上一对小
        Assert.True(MeshAdapt.Converged(second, first),
            "细阶梯这三档是**真**收敛（变化在缩小），判据不该把它也拒掉");
    }

    /// <summary>★★ 变化没在缩小 ⇒ 不算收敛，哪怕两次都在容差内。</summary>
    [Fact]
    public void 变化没缩小就不算收敛()
    {
        var prev = D(0.10, 0.30, 0.01);
        var now = D(0.10, 0.35, 0.01);          // ③ 反而变大了
        Assert.All(now, d => Assert.True(d.Within));
        Assert.False(MeshAdapt.Converged(now, prev));

        var same = D(0.10, 0.30, 0.01);         // 一点没动也不算「在缩小」
        Assert.False(MeshAdapt.Converged(same, prev));
    }

    /// <summary>
    /// ★ 噪声出口：两边都已远小于分辨得出的差别（≤ 容差的十分之一）时不比大小 ——
    /// 否则会为了噪声无限加密，而加密到最后仍然只是在比噪声。
    /// </summary>
    [Fact]
    public void 落进噪声就不再比大小()
    {
        var prev = D(0.001, 0.001, 0.0001);
        var now = D(0.002, 0.002, 0.0002);      // 变大了，但都 ≤ 0.1×容差
        Assert.True(MeshAdapt.Converged(now, prev));
    }

    /// <summary>空集不算收敛 —— 「没有判据可比」被当成「都通过了」是记过案的形态。</summary>
    [Fact]
    public void 空集仍然不算收敛()
    {
        Assert.False(MeshAdapt.Converged(new List<MeshAdapt.Delta>(), D(0.1, 0.1, 0.01)));
        Assert.False(MeshAdapt.Converged(D(0.1, 0.1, 0.01), new List<MeshAdapt.Delta>()));
    }

    /// <summary>复核循环真的把**上一对**传下去了 —— 不传的话上面这些全是摆设。</summary>
    [Fact]
    public void 复核循环传了上一对()
    {
        string mv = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "MeshVerify.cs"));
        Assert.Contains("MeshAdapt.Converged(res.LastDeltas, prevDeltas)", mv);
        Assert.Contains("prevDeltas = res.LastDeltas;", mv);
        Assert.DoesNotContain("MeshAdapt.Converged(res.LastDeltas))", mv);
    }

    /// <summary>
    /// ★★ 判据的注释里必须留着**那组真数** —— 下一个人要能看出这条规矩是被什么逼出来的。
    /// 一条没有出处的规矩，过两周就会被人当成保守而放宽。
    /// </summary>
    [Fact]
    public void 判据带得出那次实测()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "MeshAdapt.cs"));
        Assert.Contains("8.573", s);
        Assert.Contains("7.685", s);
        Assert.Contains("9.462", s);
        Assert.Contains("1.777", s);
        Assert.Contains("非单调", s);
    }

    /// <summary>
    /// ★★★ **说了「不过」就必须说是哪一条**。
    ///
    /// 那趟跑了 4 小时 22 分，末行报「在算得准的网格上，这个设计不过」，
    /// 而日志里**找不到任何一条判据的名字** —— 表里印的 ②′/②″/③ 三条又都在限值内。
    /// 读的人无从下手，只能把 4 小时再跑一遍。
    ///
    /// 病根是**两条路各印各的**：`--solve` 的最终复核印全表，`--verifymesh` 只印一句话。
    /// </summary>
    [Fact]
    public void 不过的时候印得出是哪一条()
    {
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("foreach (var ck in lvv.HardBlocked)", prog);
        Assert.Contains("foreach (var miss in lvv.MissingChecks)", prog);
        Assert.Contains("整条缺席 —— 缺席不算通过", prog);
        Assert.Contains("**判不了**（判不了不算过）", prog);
    }
}
