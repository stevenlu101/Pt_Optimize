using System;
using PtOptimize.Core;
using Xunit;
using Cand = PtOptimize.Core.Solver.Cand;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **「又管用、又省铂」的旋钮不许排在最后**（2026-09-06 用户看图抓到）。
///
/// `Solver.Better` 原来只有两级：补得上优先、同级按 `Δ裕度 ÷ Δ铂重`。
/// 后一句是给**加料**旋钮写的（Δ铂重 &gt; 0）。挖料旋钮（圆盘槽、舌板孔）
/// 的 Δ铂重 &lt; 0 ⇒ 效率是负数 ⇒ **省得越多、排得越后**：
/// <code>
///   圆盘槽 27–40 mm 180°   抽热 −47.6 %、体积 −6.4 %  ⇒ 47.6/(−6.4) = −7.44
///   舌片圆孔 R10           抽热 − 0.5 %、体积 −1.3 %  ⇒  0.5/(−1.3) = −0.38
///                                                       −0.38 &gt; −7.44 ⇒ **孔赢**
/// </code>
/// 实测这两个差 90 倍（`deliverable/按场开槽_效果.txt`），后果就是交付件
/// `整机.3dm` 上「舌片一个 R36 的大洞、圆盘一条槽都没有」——**用户一眼看出来的那张图**。
///
/// ⚠ 本门是**纯函数门**，微秒级 —— 排序规则不该靠一小时的全解去验。
/// </summary>
public class KnobPricingOrderTests
{
    // 实测数（deliverable/按场开槽_效果.txt，基准 617.2 W / 47014 mm³）
    // 裕度用「抽热降的百分点」代，铂重用「体积变化的百分点」代 —— 排序只看相对大小。
    private static readonly Cand 圆盘槽 = new(DSlack: 47.6, DMass: -6.4, Closes: true);
    private static readonly Cand 舌片孔 = new(DSlack: 0.5, DMass: -1.3, Closes: true);
    private static readonly Cand 板厚   = new(DSlack: 30.0, DMass: +200.0, Closes: true);
    private static readonly Cand 舌保温 = new(DSlack: 12.0, DMass: 0.0, Closes: true);

    /// <summary>★★★★★ 就是这一条把图画错的：槽必须赢孔。</summary>
    [Fact]
    public void 省铂多又管用的必须赢过省铂少又不管用的()
    {
        Assert.True(Solver.Better(圆盘槽, 舌片孔),
            "圆盘槽抽热 −47.6 %、还多省铂，却输给只降 0.5 % 的舌片孔 —— "
          + "这就是「每克铂买多少」在分母为负时的符号翻转，交付图上那个大洞的来源。");
        Assert.False(Solver.Better(舌片孔, 圆盘槽), "反过来也不许成立（排序必须反对称）");
    }

    /// <summary>★★★★ 省铂的对上花铂的：省铂的是白捡的，没有对手。</summary>
    [Fact]
    public void 省铂的赢过花铂的()
    {
        Assert.True(Solver.Better(圆盘槽, 板厚), "又补得上、又省 6.4 的槽输给了要花 200 g 铂的板厚");
        Assert.True(Solver.Better(舌片孔, 板厚), "省铂的哪怕买得少，也不该输给花铂的");
    }

    /// <summary>★★★ 不花铂与省铂同级，彼此按「买到多少裕度」排。</summary>
    [Fact]
    public void 免费与省铂同级按买得多少排()
    {
        Assert.True(Solver.Better(圆盘槽, 舌保温), "槽买 47.6 > 舌保温买 12.0，两个都不花铂 ⇒ 槽赢");
        Assert.True(Solver.Better(舌保温, 舌片孔), "舌保温买 12.0 > 孔买 0.5 ⇒ 舌保温赢");
    }

    /// <summary>★★★★ 都要花铂时，才轮到「每克铂买多少」—— 原来那条规则在它的定义域内仍然对。</summary>
    [Fact]
    public void 都花铂时仍按每克铂买多少排()
    {
        var 贵而有效 = new Cand(DSlack: 30.0, DMass: 200.0, Closes: true);   // 0.150/g
        var 便宜更划算 = new Cand(DSlack: 20.0, DMass: 50.0, Closes: true);  // 0.400/g
        Assert.True(Solver.Better(便宜更划算, 贵而有效),
            "两个都花铂，效率高的该赢 —— 这一支是原规则，不许被这次修改带坏");
    }

    /// <summary>
    /// ★★★★★ **补得上永远优先**（2026-08-30 实测打回来的那一条，不许被这次修改推翻）。
    /// 按效率挑而不看补不补得上，第一轮就会宣告「这组输入不可行」——**结论是错的**。
    /// </summary>
    [Fact]
    public void 补得上永远优先于补不上()
    {
        var 补得上但要花铂 = new Cand(DSlack: 5.0, DMass: 500.0, Closes: true);
        var 补不上但省铂 = new Cand(DSlack: 400.0, DMass: -50.0, Closes: false);
        Assert.True(Solver.Better(补得上但要花铂, 补不上但省铂),
            "补不上的候选爬到了补得上的前面 ⇒ 求解器会拿一个补不上的顶着，"
          + "然后宣告不可行 —— 2026-08-30 栽过一次的那个坑");
    }

    /// <summary>★★ 都补不上时按「买得最多」排 —— 让这一轮有进展，下一轮缺口才会变小。</summary>
    [Fact]
    public void 都补不上时按买得最多排()
    {
        var 买得多 = new Cand(DSlack: 400.0, DMass: 500.0, Closes: false);
        var 买得少但省铂 = new Cand(DSlack: 10.0, DMass: -50.0, Closes: false);
        Assert.True(Solver.Better(买得多, 买得少但省铂),
            "都补不上时该先用买得最多的顶上去（ChooseKnob 的说明里写着这一句）");
    }

    /// <summary>★★★ 反对称性：任意两个候选，不能互相都「更好」。</summary>
    [Theory]
    [InlineData(47.6, -6.4, true, 0.5, -1.3, true)]
    [InlineData(30.0, 200.0, true, 20.0, 50.0, true)]
    [InlineData(5.0, 500.0, true, 400.0, -50.0, false)]
    [InlineData(12.0, 0.0, true, 47.6, -6.4, true)]
    public void 排序必须反对称(double s1, double m1, bool c1, double s2, double m2, bool c2)
    {
        var a = new Cand(s1, m1, c1);
        var b = new Cand(s2, m2, c2);
        Assert.False(Solver.Better(a, b) && Solver.Better(b, a),
            "两个候选互相都「更好」⇒ 排序不是良序，挑到谁取决于遍历顺序");
    }
}
