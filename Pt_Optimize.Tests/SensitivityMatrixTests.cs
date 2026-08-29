using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **实测敏感度矩阵**（2026-08-30，清单第 6 组第 13 件 = A① 的拆法）。
///
/// ══ 它跑出来的第一版是错的，而**数据自己把错暴出来了**
///
/// 第一版用中心差分、步长 ±1 mm 扰动舌保温，而基准值是 0.3–0.5 mm：
///
/// <code>
///   下侧扰到 −0.6 / −0.6 / −0.5 / −0.7 mm      —— **负保温层**
///   ⇒ 十二格全报「两侧符号不一致」，看着像非线性，其实是**扰出了定义域**
///   ⇒ 而且符号是**反的**：第一版 ∂②′/∂舌保温 = +5.3，改对之后 = **−248.9**
/// </code>
///
/// 交叉验证：改对之后的符号与 `--monotone` 全量程扫描一致（舌保温 0.3→8 mm，
/// 抽热 D 28.3 → −1595，②′ 跟着掉）。**第一版与它相反。**
///
/// ══ 由此得到的方法论：**求解器只往上抬 ⇒ 决策要的是「向上」的单侧导数**
///
/// 而且多数量的基准值**就压在下界上**（舌保温 0.3、环倍率 1.00、t₂ 1.00 都是下界）——
/// 中心差分在这种点上必然有一半落在定义域外，报出来是**一个看起来正常的错数**。
///
/// ══ 第二个错：`0.0000` 不等于「不敏感」
///
/// r₁/r₂ 十六格全是 0.0000。不是「测过了，很小」，是**结构性无效**：
/// t₁ = t₂ = 1.00 ⇒ 三段厚度全相等，**台阶不存在**，挪它的半径不可能有任何作用。
/// （`--monotone` 扫这三支时特意先把 t₁ 钉到 1.50 让台阶存在。）
/// 两者对下一步的含义完全不同：前者说「别动它」，后者说「先把台阶造出来再谈」。
/// </summary>
public class SensitivityMatrixTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>
    /// ★★ **扰动必须留在定义域内**。这一条直接对着那次翻车：
    /// 舌保温下界 0.3，基准 0.3–0.5，步长 1.0 ⇒ 硬减会变负。
    /// </summary>
    [Fact]
    public void 扰动留在定义域内()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();

        double lo = SensitivityMatrix.Lo(SensitivityMatrix.Var.Insul, d, p);
        Assert.Equal(SizerOptions.InsLoMmConst, lo, 12);
        // 基准 0.3（片3）减去步长 1.0 会变负 —— 门要保证代码里有夹
        Assert.True(d.TabInsulMm[3] - SensitivityMatrix.Step(SensitivityMatrix.Var.Insul) < lo,
            "这一条的前提没了（基准值离下界够远）—— 那就该重新想它还验不验得到东西");
        Assert.Contains("Math.Max(x0 - h, lo)", Core("SensitivityMatrix.cs"));
        Assert.Contains("Math.Min(x0 + h, hi)", Core("SensitivityMatrix.cs"));
    }

    /// <summary>
    /// ★★ 下界处的量：**向上那一侧必须仍然测得到**（求解器只往上抬），
    /// 而向下那一侧要明说「没验到」，**不许记成「验过且一致」**。
    /// </summary>
    [Fact]
    public void 下侧出界时只测向上并且说出来()
    {
        string s = Core("SensitivityMatrix.cs");
        Assert.Contains("public bool DownInDomain;", s);
        Assert.Contains("c.DownInDomain = c.StepDown > 1e-12;", s);
        Assert.Contains("if (c.DownInDomain) { var dLo = d0.Clone();", s);

        string prog = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("!c.DownInDomain ? \"下侧出界\"", prog);
    }

    /// <summary>
    /// ★★★ **结构性无效 ≠ 不敏感**。现役两档 t₁ = 1.00 ⇒ 台阶不存在 ⇒ r₁/r₂ 无从起作用。
    /// 这必须报成一句话，不许报成 <c>0.0000</c>。
    /// </summary>
    [Fact]
    public void 台阶不存在时报结构性无效而不是零()
    {
        var d = DesignSpec.W08.Clone();          // RingMul 全 1.00
        foreach (var v in new[] { SensitivityMatrix.Var.RingR1, SensitivityMatrix.Var.RingR2 })
        {
            string? why = SensitivityMatrix.Degenerate(d, v, 0);
            Assert.NotNull(why);
            Assert.Contains("台阶不存在", why!);
            Assert.Contains("结构性无效", why!);
            Assert.Contains("不是「不敏感」", why!);
        }

        // 台阶存在时就该真去测（--monotone 扫这三支时把 t₁ 钉到 1.50，正是为此）
        for (int j = 0; j < 4; j++) d.RingMul[j] = 1.50;
        Assert.Null(SensitivityMatrix.Degenerate(d, SensitivityMatrix.Var.RingR1, 0));

        // 板厚这类量永远不会「结构性无效」
        Assert.Null(SensitivityMatrix.Degenerate(d, SensitivityMatrix.Var.Thick, 0));
    }

    /// <summary>
    /// ★ 读写只有一处入口，且 <c>Get</c> 要把「NaN = 用旧规则」坐实成数值 ——
    /// 否则基准点根本不存在，扰动无从谈起。规则**镜像模型**，不另立一份。
    /// </summary>
    [Fact]
    public void 读写唯一入口且NaN先坐实()
    {
        var d = DesignSpec.W08.Clone();
        // 现役档三个都是 NaN
        Assert.True(double.IsNaN(d.RingW1Mm[0]) && double.IsNaN(d.RingW2Mm[0]) && double.IsNaN(d.RingMul2[0]));

        Assert.Equal(d.RingWidthMm,     SensitivityMatrix.Get(d, SensitivityMatrix.Var.RingR1, 0), 12);
        Assert.Equal(2 * d.RingWidthMm, SensitivityMatrix.Get(d, SensitivityMatrix.Var.RingR2, 0), 12);
        Assert.Equal(d.RingMulOuter(0), SensitivityMatrix.Get(d, SensitivityMatrix.Var.RingT2, 0), 12);

        SensitivityMatrix.Set(d, SensitivityMatrix.Var.RingR1, 0, 4.5);
        Assert.Equal(4.5, SensitivityMatrix.Get(d, SensitivityMatrix.Var.RingR1, 0), 12);
    }

    /// <summary>
    /// ★★ 裕度只有**一处**来源 —— 与求解器同一个 <see cref="Solver.PlateSlack"/>。
    /// 另立一份「差不多的」读法，是本仓库栽过多次的形状。
    /// </summary>
    [Fact]
    public void 裕度走求解器那一份()
    {
        string s = Core("SensitivityMatrix.cs");
        Assert.Contains("Solver.PlateSlack(r, k, j, dipMax, discMax)", s);
        Assert.DoesNotContain("f.QFromTubeW", s);      // 不许自己去 FlangeOut 里捞
        Assert.DoesNotContain("TDiscMaxC", s);
    }

    /// <summary>
    /// ★ 舌保温**不花铂** ⇒ 标「免费」，**不返回 ±∞**。
    /// 无穷会被排序、被格式化、被读成一个数。
    /// </summary>
    [Fact]
    public void 不花铂的量标免费而不是无穷()
    {
        var c = new SensitivityMatrix.Cell { DMassG = 0.0 };
        c.D[0] = 123.4;
        Assert.True(c.Free);
        Assert.True(double.IsNaN(c.PerGram(0)));
        Assert.False(double.IsInfinity(c.PerGram(0)));

        c.DMassG = 168.0;
        Assert.False(c.Free);
        Assert.Equal(123.4 / 168.0, c.PerGram(0), 12);
    }

    /// <summary>
    /// ★ 步长要有出处，不许是拍的数。
    /// </summary>
    [Fact]
    public void 步长有出处()
    {
        string s = Core("SensitivityMatrix.cs");
        Assert.Contains("与 `--vary` 同步长", s);
        Assert.Contains("与界面控件的 Increment 同", s);
        Assert.Equal(0.1,  SensitivityMatrix.Step(SensitivityMatrix.Var.Thick), 12);
        Assert.Equal(0.05, SensitivityMatrix.Step(SensitivityMatrix.Var.Ring), 12);
    }

    /// <summary>
    /// ★★ 与 `--monotone` 的**分工**必须写清楚 —— 两条命令量的是不同的东西，
    /// 混用会得出错的结论（`--monotone` 自己就写着「斜率的绝对值不作依据」）。
    /// </summary>
    [Fact]
    public void 与单调性扫描的分工写清楚了()
    {
        string prog = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("--monotone   四片同步、大跨度", prog);
        Assert.Contains("--sensmatrix 逐片、小扰动、同一工作点", prog);
    }
}
