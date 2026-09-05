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
    /// ★★★ **第 9 件的决定**（2026-08-30，依据是第 13 件的实测矩阵）：
    ///
    /// <code>
    ///   t₂     ⇒ **加**，而且排在板厚**前面**（②′ 的首选候选）
    ///   r₁/r₂  ⇒ **不加**，且不是「暂时」—— 结构性无效
    /// </code>
    ///
    /// 三条依据，缺一条都不该加：
    /// ① **单调**（第 8 件，`--monotone` 全量程）⇒ 二分适用；
    /// ② **省铂**（第 13 件）：每克铂买到的 ②′ 裕度是板厚的 **1.7–3.3 倍**，
    ///    而每单位 ②′ 的 ③ 代价几乎相同（2.3–2.4 K/W）；
    /// ③ **逐片二分收敛**（第 9 件）：行和 ρ_有害向(t₂, ②′) = **0.023** &lt; 1,
    ///    比现役的环倍率（0.219）还安全一个数量级。
    ///
    /// ⚠ ③ 这一条差点被我自己判反：矩阵第一版把跨片项取了**绝对值**，
    ///   看着是「对角只占 2.4–2.9 倍」很危险；带上符号才看清那些耦合**几乎全是有益的**
    ///   （ρ 全部 0.685、有害向只有 0.023）。见 SensitivityMatrixTests。
    /// </summary>
    [Fact]
    public void 分配表按第9件的决定长成了这样()
    {
        // 判据仍是三条（一条判据一行），但 ②′ 有两个候选
        // ⚠ 不钉个数（门 A）：加一条判据的分派是**变好**，不该让门红。
        //   钉的是不变量：每条判据都要有分派，且分派里的旋钮都真实存在。
        Assert.NotEmpty(Solver.Allocation);
        Assert.All(Solver.Allocation, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Key));
            Assert.NotEmpty(a.Knobs);
        });
        var keys = Solver.Allocation.Select(a => a.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Contains(LineResult.Key.NetFlux, keys);
        Assert.Contains(LineResult.Key.FlangeDip, keys);
        Assert.Contains(LineResult.Key.DiscTemp, keys);

        // ★★ t₂ 是旋钮，且是 ②′ 的**首选**（顺序不是随意的：先试便宜的那个）
        var netflux = Solver.Allocation.First(a => a.Key == LineResult.Key.NetFlux).Knobs;
        Assert.Equal(Solver.Knob.RingT2, netflux[0]);
        Assert.Equal(Solver.Knob.Thick,  netflux[^1]);   // 板厚永远排最后（最贵）

        // ★★★★★ 2026-09-05 决定变了：r₁/r₂ **成了旋钮**，与 t₁/t₂ 成对。
        //
        //   用户 2026-09-04：「自动定厚应该有 t₁/t₂ 的结果，要与搜形状的 r₁/r₂ 是配对的」。
        //   一个台阶 = (半径, 厚度) 两个数；只搜厚度等于在搜「一条别人定了宽度的带」。
        //
        //   2026-08-30 不做它的理由是「现役档 t₁=t₂=1.00 ⇒ 台阶不存在 ⇒ 挪半径结构性无效」。
        //   那个理由**在当时成立**，现在仍然成立 —— 但它不该由一条 if 写死，
        //   而该由**实测**筛掉：ChooseKnob 会把每个旋钮抬到上界量一次，
        //   t=1 时挪半径量不出改善 ⇒ 自动淘汰，并报「这一级还是平的，没有台阶可挪」。
        //   ⇒ 门从「禁止它存在」改成「**必须与配对的 t 同排**」。
        // ⚠ **不钉个数**（2026-09-05）。这里原来写 Assert.Equal(6, …)，
        //   而这已经是同一天第四次「门钉的是当时的数量/措辞，不是不变量」——
        //   加一根旋钮就假红，红的却不是能力变坏，是门过期了。
        //   ⇒ 钉真不变量：每根旋钮都要有**独一无二的名字**（新旋钮不许无名溜进来），
        //     成对关系另由下面几条钉。个数由 OptimizationModelReconcileTests 与
        //     HANDOVER §0.0.3 对帐表把关 —— 那里改了才算「记下来了」。
        var allKnobs = Enum.GetValues<Solver.Knob>();
        var names = allKnobs.Select(Solver.KnobName).ToArray();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("RingR1", Enum.GetNames<Solver.Knob>());
        Assert.Contains("RingR2", Enum.GetNames<Solver.Knob>());

        // ★ 成对：r₂ 要和 t₂ 在同一条判据行上；r₁ 要和 t₁ 在同一条上
        Assert.Contains(Solver.Knob.RingR2, netflux);
        var disc = Solver.Allocation.First(x => x.Key == LineResult.Key.DiscTemp).Knobs;
        Assert.Contains(Solver.Knob.Ring,   disc);
        Assert.Contains(Solver.Knob.RingR1, disc);

        // ★ 「没有台阶就没得挪」这句话必须在代码里说得出来，不能只报「没变好」
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        Assert.Contains("没有台阶可挪", src);

        // 三条依据都要写在代码里 —— 少一条，下一个人就无从判断这个决定还成不成立
        string s = Core("Solver.cs");
        Assert.Contains("1.7–3.3 倍", s);        // 省铂
        Assert.Contains("2.3–2.4 K/W", s);       // ③ 的代价相同
        Assert.Contains("结构性无效", s);         // r₁/r₂ 为什么不加
    }

    /// <summary>
    /// ★★ t₂ 作为旋钮，**起点必须显式钉在盒的下角**，不许留 NaN。
    ///
    /// 模型里 <c>RingMul2 = NaN</c> 的含义是「跟着 t₁ 走」（t₂ = 1+0.4(t₁−1)）。
    /// 留着它当旋钮的起点，等于同一个量有**两处来源**，而且它会随 t₁ 悄悄变 ——
    /// 二分的不变式（lo 违反、hi 不违反）当场失效，而且**不报错**。
    /// </summary>
    [Fact]
    public void t2的起点显式钉在盒下角()
    {
        string s = Core("Solver.cs");
        Assert.Contains("d.RingMul2[j]   = opt.RingLo;", s);
        Assert.Contains("NaN 的含义是「跟着 t₁ 走」", s);
        // 读的时候也要把规则坐实，不许把 NaN 直接当数用
        Assert.Contains("Knob.RingT2 => double.IsNaN(d.RingMul2[j]) ? d.RingMulOuter(j) : d.RingMul2[j],", s);
    }

    /// <summary>
    /// ★★★ **界面必须把解出来的 t₂ 收回去** —— 不收就是静默丢弃。
    ///
    /// <c>PageToDesignSpec</c> 在「逐片自定」没勾时给这三个写 <b>NaN</b>（= 用旧规则）⇒
    /// 求解器解出 t₂ → 页面读回来时把它丢掉 → **重解得到另一个答案**。
    /// 本仓库为「传进来的旋钮值被丢弃」这一族栽过多次，这是同一个形状。
    /// </summary>
    [Fact]
    public void 界面把解出来的t2收回去并勾上逐片自定()
    {
        string ui = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("_ringShapeCustom.Checked = true;", ui);
        Assert.Contains("_ringT2[j].Value = Math.Clamp((decimal)d.RingMul2[j],", ui);
        // ★ 那句「求解器不会替你动它们」现在是**假话**，必须已经改掉
        Assert.DoesNotContain("求解器不会替你动它们", ui);
        Assert.Contains("**t₂ 是求解器旋钮**", ui);
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
