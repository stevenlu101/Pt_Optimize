using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **渐变环形状 r₁/r₂/t₂ 补成页面输入**（2026-08-30，清单第 3 组第 10 件）。
///
/// ══ 为什么这不只是「多三个输入框」
///
/// 本页 2026-08-28 立过一条规矩：
/// <code>
///   页面上每一个进计算的量都有输入来源，
///   DesignSpec 不再是任何计算的起点或兜底，只剩回归基准这一个角色。
/// </code>
/// 而这三个是**最后三个例外** —— <c>PageToDesignSpec</c> 从 <c>DesignSpec.Current.Clone()</c>
/// 起手，于是它们被**静默继承**自设计记录。正是那条规矩点名要禁的形态。
///
/// ══ 默认值怎么表达
///
/// 模型默认是 <b>NaN = 用旧规则</b>，而 NumericUpDown 表达不了 NaN ⇒ 用复选框切换。
/// 不勾（默认）传 NaN，框子禁用但**照样显示规则算出来的值** ——
/// 看得见的默认值才学得会那条规则；一个空的禁用框只说「不能改」，
/// 不说「不改时是多少」，而**看不见又在起作用的量是安静失败的温床**。
///
/// ══ 它们不是求解器旋钮
///
/// `--monotone` 实测三条全单调，但只有 ②′ 变好、③ 与 ②″ 都变坏 ⇒
/// 是「花铂换抽热」的旋钮，不是「治判据」的旋钮。见 <see cref="MonotoneMeasuredTests"/>。
/// </summary>
public class RingShapeInputTests
{
    private static string Ui() =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>★ 三组控件与那个开关都在。</summary>
    [Fact]
    public void 三组控件与开关都在()
    {
        string s = Ui();
        Assert.Contains("private readonly CheckBox _ringShapeCustom", s);
        foreach (var f in new[] { "_ringR1", "_ringR2", "_ringT2" })
            Assert.Contains($"private readonly NumericUpDown[] {f} =", s);
    }

    /// <summary>
    /// ★★ **静默继承已经断掉**：<c>PageToDesignSpec</c> 必须显式写这三个数组，
    /// 不勾自定时写 NaN。不写 = 继续从 <c>DesignSpec.Current</c> 捡，那条规矩就还有例外。
    /// </summary>
    [Fact]
    public void 页面显式写入三个数组()
    {
        string s = Ui();
        foreach (var a in new[] { "RingW1Mm", "RingW2Mm", "RingMul2" })
            Assert.Contains($"d.{a}[i] = _ringShapeCustom.Checked ?", s);
        Assert.Contains(": double.NaN;", s);
    }

    /// <summary>
    /// ★★ **必须进快照**，否则是「假新鲜」：改了形状而上一次的解还显示新鲜。
    /// 本页为这件事专门立过 <c>Snap</c> 这个记录类型。
    /// </summary>
    [Fact]
    public void 进了快照()
    {
        string s = Ui();
        Assert.Contains("public bool RingCustom;", s);
        Assert.Contains("public double RingR1, RingR2, RingT2;", s);
        Assert.Contains("RingCustom = _ringShapeCustom.Checked,", s);
    }

    /// <summary>
    /// ★★ 不勾时显示的默认值必须是**模型那条规则**算的，不许另立一份。
    /// 两边漂开的话，界面显示的和实际算的就不是一回事 —— 本仓库栽过多次的形状。
    /// </summary>
    [Fact]
    public void 默认值镜像模型的规则而不是另立一份()
    {
        string s = Ui();
        Assert.Contains("private void SyncRingShape()", s);
        Assert.Contains("_ringR1[j].Value = C(w, _ringR1[j]);", s);            // r₁ = 环宽
        Assert.Contains("_ringR2[j].Value = C(2 * w, _ringR2[j]);", s);        // r₂ = 2×环宽
        Assert.Contains("_ringT2[j].Value = C(1 + (t1 - 1) * 0.4, _ringT2[j]);", s);

        // ★ 与模型实测对上：同样的输入，模型给的必须是同一个数
        var d = DesignSpec.W08.Clone();
        for (int j = 0; j < 4; j++) d.RingMul[j] = 1.50;
        Assert.Equal(1 + (1.50 - 1) * 0.4, d.RingMulOuter(0), 12);
        var (r1, r2) = (d.HoleRadiusMm + d.RingWidthMm, d.HoleRadiusMm + 2 * d.RingWidthMm);
        var rr = d.RingRadiiOf(0);
        Assert.Equal(r1, rr[0], 12);
        Assert.Equal(r2, rr[1], 12);
    }

    /// <summary>
    /// ★ 量程要有出处 —— 取 `--monotone` **实测扫过**的区间，不是拍的数。
    /// 量程之外没有单调性依据。
    /// </summary>
    [Fact]
    public void 量程取自实测扫过的区间()
    {
        string s = Ui();
        Assert.Contains("RingR() => Num((decimal)StartPoint.RingWidthMm, 1m, 10m", s);   // r₁ 1→10
        Assert.Contains("RingR2() => Num((decimal)(2 * StartPoint.RingWidthMm), 4m, 16m", s); // r₂ 4→16
        Assert.Contains("实测扫过的量程", s);
    }

    /// <summary>
    /// ★★ 提示里必须说清那个**反直觉**的事实：「外扩」量的是**离管轴的半径**，
    /// 越过盘缘之后它继续作用在**舌根**。
    ///
    /// 不说的话，工程师会以为 r₂ 超过盘半径就没用了 —— 而 `--monotone` 把 r₂ 扫到
    /// 16 mm（盘 Ø60 时盘面只到 孔+4.2）仍持续见效，正是这个缘故。
    /// </summary>
    [Fact]
    public void 提示说清了越过盘缘之后作用在舌根()
    {
        string s = Ui();
        Assert.Contains("从管轴量的半径", s);
        Assert.Contains("超过盘半径之后它继续作用在舌片根部", s);
        Assert.Contains("不是「治判据」的旋钮", s);
    }

    /// <summary>
    /// ★★ **更正一处假出处**（2026-08-29 是我自己写的）：环倍率提示里那句
    /// 「实测（--monotone，0.8 档）：+0.08 K」写于 `d1ea3c1`，而 `--monotone`
    /// 到 2026-08-30 才第一次跑 —— 那个数是从别处的导数 +0.056 线性外推的。
    ///
    /// 跑完之后两者对上了（+0.084），**但当时那个出处是假的**。
    /// 数对不等于出处对：一条带着假出处的数，下一个人无法复核。
    /// </summary>
    [Fact]
    public void 环倍率提示的出处已经更正()
    {
        string s = Ui();
        Assert.DoesNotContain("**+0.08 K（方向相反）**", s);   // 外推值
        Assert.Contains("−0.208 挪到 −0.124", s);              // 实测两端
        Assert.Contains("+0.084 K，方向相反", s);
        // ⚠ 钉**语义核心**，不钉整句（2026-09-02）：清命令行开关名时把句里的「它」
        //   一并去掉了（「它」指的正是那个开关），断言当场断在措辞上。
        //   要守的是「更正有没有说出口」，不是那一个代词。
        Assert.Contains("**一次没跑过**", s);                  // 把更正说出口
        // ⚠ 「界面不许出现开关名」交给 NoCliFlagInUiTests —— 它只扫**字符串字面量**、
        //   跳过 /// 注释。在这里用整份源码做 DoesNotContain 会把注释也算进去，
        //   粒度是错的（我 2026-09-02 就这么写了一版，当场断在自己的注释上）。
    }

    /// <summary>
    /// ★ 圆盘直径的提示要说清：**最优就在下界**，而下界是 ⑥ 且**算得出来**。
    /// 这正是这个输入框存在的用途（用户：让工程师随意给个法兰直径优化用）。
    /// </summary>
    [Fact]
    public void 圆盘直径提示指向六的闭式下界()
    {
        string s = Ui();
        Assert.Contains("**最优盘径就在下界上**", s);
        Assert.Contains("盘半径 ≥ 管孔半径 + 焊脚", s);
        Assert.Contains("Solver.CoverCheck", s);
        // 而且要更正那句「渐变环占满圆盘」的旧说法 —— 它不是硬下界
        Assert.Contains("那不是**硬**下界", s);
    }
}
