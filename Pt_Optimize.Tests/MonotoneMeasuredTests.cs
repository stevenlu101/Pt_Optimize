using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **`--monotone` 第一次真跑之后**（2026-08-30，清单第 3 组第 8/9 件）。
///
/// 代码 2026-08-28 就接好了，一次没跑过。跑完 42 次整线解（六个旋钮各 7 点）之后，
/// 有两条结论必须**钉在代码里**，否则下一个人只会看见一张没有出处的分配表。
///
/// ══ 一：②″ 对环倍率是「抬起来更差」，全量程坐实
///
/// <code>
///   环倍率 1.00 → 2.50     ②″  −0.208 → −0.124      （限值 ≤ 5，越大越差）
/// </code>
///
/// 此前分配表只写「方向存疑」。⚠ 但**不删**那一行 —— 灵敏度随形状变号
/// （窄舌 −1.4／宽舌 +0.056），一次形状上的实测不能定一张跨形状的表。
/// 权威是 <c>RaiseUntil</c> 里那道**每次都实测**的前提自检；
/// 删表项 = 把「每次实测」换成「一次实测」，是退步。
///
/// ══ 二：r₁/r₂/t₂ 三条全单调 —— 但**不进分配表**
///
/// 「能二分」不等于「该进表」。三者对 ③ 与 ②″ 都往坏走，只对 ②′ 往好走，
/// 而 ②′ 已经有板厚 ⇒ 加进来就把「一判据一旋钮」变成「一判据四候选」，
/// 而「抬哪个」是取舍，查表定不了。
///
/// 且 `--monotone` 这张表**回答不了**那个取舍：四片同步、跨度远大于设计点，
/// 各支不在同一个工作点上（命令自己的告示：「斜率的绝对值不作依据」）。
/// ⇒ 要加先做第 13 件。**13 从「可做」升级成「9 的前置」。**
/// </summary>
public class MonotoneMeasuredTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>
    /// ★ 分配表必须带**出处**：命令、日期、以及那张单调性表本身。
    /// 一张没有出处的分派表，下一个人只能选择相信或推翻，两样都不该是他要做的事。
    /// </summary>
    [Fact]
    public void 分配表带得出出处()
    {
        string s = Core("Solver.cs");
        Assert.Contains("--cli --monotone --wall 0.8", s);   // 复现命令
        Assert.Contains("六个旋钮各 7 点", s);
        foreach (var knob in new[] { "板厚", "舌保温", "环倍率", "r₁", "r₂", "t₂" })
            Assert.Contains(knob, s);
    }

    /// <summary>
    /// ★★ ②″ 那一行的措辞必须跟着实测走：「方向存疑」→「实测反向」，并带上数。
    /// 「存疑」是没量过时的说法；量过了还写「存疑」，等于把已有的证据丢掉。
    /// </summary>
    [Fact]
    public void 二撇撇的方向已经从存疑变成实测反向()
    {
        string s = Core("Solver.cs");
        Assert.DoesNotContain("②″ 圆盘区最高温：**方向存疑**", s);
        Assert.Contains("**实测反向**", s);
        Assert.Contains("−0.208", s);           // 全量程两端的数
        Assert.Contains("−0.124", s);
    }

    /// <summary>
    /// ★★ **不删那一行**的理由要写在旁边 —— 否则下一个人看见「实测反向」
    /// 只会顺手把它删掉，而那是把「每次实测」换成「一次实测」。
    /// </summary>
    [Fact]
    public void 不删表项的理由写在旁边()
    {
        string s = Core("Solver.cs");
        Assert.Contains("灵敏度随形状变号", s);
        Assert.Contains("RaiseUntil", s);
        Assert.Contains("一次实测", s);
        // 表项本身还在 —— 只是从「唯一指派」降为「候选之一」（2026-08-30）
        Assert.Contains("Knob.Ring", s);
        Assert.Contains("环倍率**留在候选里**", s);
    }

    /// <summary>
    /// ★★ 表里仍然只有三条 —— r₁/r₂/t₂ **没有**被顺手加进去。
    /// 加它们是个**决定**，不是个改动；决定的依据（第 13 件）还没有。
    /// </summary>
    [Fact]
    public void 分配表仍然只有三条()
    {
        Assert.Equal(3, Solver.Allocation.Length);
        Assert.Equal(3, Enum.GetValues<Solver.Knob>().Length);

        // 三条各治一条判据，不重不漏
        var keys = Solver.Allocation.Select(a => a.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Contains(LineResult.Key.NetFlux, keys);
        Assert.Contains(LineResult.Key.FlangeDip, keys);
        Assert.Contains(LineResult.Key.DiscTemp, keys);
    }

    /// <summary>
    /// ★ 「为什么不加」的理由必须落在代码里，且必须点名**前置是第 13 件** ——
    /// 不点名的话，这个决定过两周就会变成「不知道为什么没加」。
    /// </summary>
    [Fact]
    public void 不加的理由与前置都点了名()
    {
        string s = Core("Solver.cs");
        Assert.Contains("一判据四候选", s);
        Assert.Contains("取舍", s);
        Assert.Contains("第 13 件", s);
        // ⚠ 并且要说清「这张表回答不了那个取舍」，免得有人拿它的斜率去比
        Assert.Contains("斜率的绝对值不作依据", s);
    }
}
