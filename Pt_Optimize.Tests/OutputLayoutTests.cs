using System;
using System.IO;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **输出只留看得懂的**（2026-09-02 用户拍板）。
///
/// 用户：「输出也可以简化，例如：只保留场图与前后尺寸比较与其它你觉得重要的计算结果」。
///
/// 改之前一屏 22 行里有这些：
/// <code>
///   ω=0.35，ω 末值 0.35／放大 0 次／回退 0 次
///   Anderson：接受 19／丢弃 0／重启 0，末端深度 4
///   剩余误差估计 0.97 K = 步长 0.04 × 放大 25.0，真残差 0.003 K
/// </code>
/// 全是**求解器内部状态**，而且那一整行**印了两次**（一次带 ⓘ、一次在 Notes 的 foreach 里）。
/// 而工程师真正要的「这次改了什么」——**一个字都没有**。
/// </summary>
public class OutputLayoutTests
{
    private static string Ui() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>★★★ 本轮的主角：「这次改了什么（前 → 后）」。</summary>
    [Fact]
    public void 有前后对比这一块()
    {
        string s = Ui();
        Assert.Contains("◆ **这次改了什么**（前 → 后）", s);
        Assert.Contains("private Snap? _beforeSnap;", s);
        // 基准取「第 1 步解完那一刻」——比的是流水线动过什么
        Assert.Contains("_beforeSnap = CurrentSnap();", s);
        // 没动过也要说一句，别让人以为漏印了
        Assert.Contains("这次没改动你填的任何一个数", s);
    }

    /// <summary>★★ Notes 只有**一个**打印点 —— 那一行曾经逐字印两次。</summary>
    [Fact]
    public void 求解器诊断只印一次()
    {
        string s = Ui();
        Assert.Equal(1, s.Split("foreach (var n in r.Notes)").Length - 1);
        // 带 ⓘ 的那条旧打印点不许回来
        Assert.DoesNotContain("sb.AppendLine(\"  ⓘ \" + nt);", s);
    }

    /// <summary>★★★ 折叠不许把坏消息藏起来：**未收敛时诊断强制展开**。</summary>
    [Fact]
    public void 未收敛时诊断强制展开()
    {
        string s = Ui();
        Assert.Contains("if (_showDiag.Checked || !r.Converged)", s);
        Assert.Contains("未收敛，已强制展开", s);
        // 而且未收敛这件事本身不受开关控制
        Assert.Contains("**未收敛 ⇒ 上面每个数都不可引用**", s);
    }

    /// <summary>★ 明细与诊断默认收起，且是**开关**不是删除。</summary>
    [Fact]
    public void 明细与诊断收进开关()
    {
        string s = Ui();
        Assert.Contains("if (_showDetail.Checked)", s);
        Assert.Contains("private readonly CheckBox _showDetail", s);
        Assert.Contains("private readonly CheckBox _showDiag", s);
        // 勾了要立刻重排（数据都在 _last 里，不重算）
        Assert.Contains("_showDetail.CheckedChanged += (_, _) => Show(_last);", s);
        Assert.Contains("_showDiag.CheckedChanged += (_, _) => Show(_last);", s);
    }

    /// <summary>★ 结论区留的是工程师要的那几样，收敛只留一句话。</summary>
    [Fact]
    public void 结论区留的是要紧的()
    {
        string s = Ui();
        Assert.Contains("★ 整线总铂", s);
        Assert.Contains("模型唯一的现场验证点", s);
        Assert.Contains("sb.AppendLine(\"  收敛　\"", s);
    }
}
