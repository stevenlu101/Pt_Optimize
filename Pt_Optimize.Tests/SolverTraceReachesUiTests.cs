using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **「比价」说了印在输出框里，实际一次都没印过**（2026-09-03 查出）。
///
/// 环倍率那个输入框的提示语原文：
/// <code>
///   ⇒ 你不必自己判断动不动它：求解器每轮会对你手上这个形状当场量一次，
///     抬它有没有用、值不值那点铂，比价结果**印在输出框里**。
/// </code>
/// 而 <c>SolverResult.Trace</c>（比价就在里面）**一次都没到过界面** ——
/// 它只进 <c>progress</c> 回调，那条路的终点是状态栏**一行、被下一行盖掉**。
///
/// 求解器每轮真的量出来这样的东西：
/// <code>
///   板厚   补得上、每克铂买 0.545（裕度 +515.60／铂 +945.3 g）
///   环倍率 补不上、每克铂买 5.080（裕度  +34.40／铂   +6.8 g）
/// </code>
/// 这正是「值不值那点铂」的答案 —— 工程师一次也没看见。
/// **说了没做到，比没说更坏**：他会以为程序替他比过了。
///
/// ⇒ 接进「求解器诊断」那个开关下面（它是**为什么这么调**，不是判定）。
/// </summary>
public class SolverTraceReachesUiTests
{
    private static string Src() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>★★★ 求解器跑完，推理过程要被带回页面。</summary>
    [Fact]
    public void 求解器的推理过程被带回界面()
    {
        Assert.Contains("_lastTrace = srD8.Trace.ToArray();", Src());
    }

    /// <summary>★★★ 而且要真的印出来 —— 带回来不印等于没带。</summary>
    [Fact]
    public void 诊断展开时把它印出来()
    {
        string s = Src();
        int a = s.IndexOf("if (_showDiag.Checked || !r.Converged)", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到诊断那一段 —— 断言失去了对象");
        int b = s.IndexOf("_out.Text = sb.ToString();", a, StringComparison.Ordinal);
        Assert.True(b > a);
        Assert.Contains("_lastTrace", s[a..b]);
    }

    /// <summary>
    /// ★★ 参数一动就作废 —— 留着上一组参数的推理会张冠李戴。
    /// 两条入口（左边参数表 / 页面控件）都要清。
    /// </summary>
    [Fact]
    public void 参数动过就不留旧推理()
    {
        Assert.True(Src().Split("_lastTrace = System.Array.Empty<string>();").Length - 1 >= 3,
            "清除点少于三处（声明 + 两条参数变更入口）—— 会留着上一组参数的推理");
    }

    /// <summary>
    /// ★★★ 自证：求解器**真的**会产生比价那几行 —— 否则上面三条都是空转。
    /// 这里不解整线（太贵），只钉住产生比价的那段代码还在、格式没变。
    /// </summary>
    [Fact]
    public void 自证_求解器真的会写出比价那几行()
    {
        string solver = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        Assert.Contains("每克铂买", solver);
        Assert.Contains("补得上", solver);
        // 它必须进 Trace（而不是只进 progress）—— 那正是这次修的东西
        Assert.Contains("res.Trace.Add(s)", solver);
    }

    /// <summary>
    /// ★★ 提示语里那句承诺还在的话，实现就必须还在（反过来也一样）。
    /// 承诺与实现分家正是这次的病根。
    /// </summary>
    [Fact]
    public void 提示语的承诺与实现绑在一起()
    {
        string s = Src();
        bool promises = s.Contains("比价结果印在输出框里", StringComparison.Ordinal);
        bool delivers = s.Contains("_lastTrace = srD8.Trace.ToArray();", StringComparison.Ordinal);
        Assert.True(promises == delivers,
            promises ? "界面还承诺「比价结果印在输出框里」，但实现已经没了"
                     : "实现在，但界面不再承诺 —— 那就把承诺加回去，别让工程师错过它");
    }
}
