using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **界面必须走 <see cref="PtOptimize.Core.Solver"/>，不许再走 <c>Sizer</c>**
/// （2026-08-29，第 2 组接线）。
///
/// ══ 为什么这是最要紧的一道门
///
/// 本项目的头号病是「**造好了没接线**」。2026-08-28 一整天把求解器做出来、
/// 证明它与初值无关（`--solve --seedprobe` 逐位相同）、证明它比历史记录轻 65 g
/// 且 ③ 裕度更大 —— 但**界面上的三个按钮全都还在调旧 `Sizer`**：
/// 工程师点界面，用到的仍是那个有种子、有增量行走、有 <c>bestFeas ?? bestAny</c> 的东西。
/// **所有成果对用户为零。**
///
/// ⚠ 清点时我第一次只找到 2 处，实际是 **3 处**（第三处在「自动定厚」按钮里）。
///   所以这道门数的是「**一处都不许剩**」，不是「改了几处」。
///
/// ══ 两遍求根在界面上的分工
///
/// | 场景 | 第二遍（细网格） | 为什么 |
/// |---|---|---|
/// | 搜形状 · 粗筛 | **不做** | 它只负责给方向，几十个形状每个都做细网格不现实 |
/// | 搜形状 · 精算 | **做** | 这是要交付的那一个 |
/// | 自动定厚按钮 | **不做** | 按钮要等得起 |
///
/// ⇒ 凡是没做第二遍的路径，**必须当场喊出来**：
/// 实测 ③ 在两张网格上差 **2.03 倍**，而一个只在导航网格上成立的解，
/// **数字长得和可交付的解一模一样**。
/// </summary>
public class SolverWiredToUiTests
{
    private static string Ui(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f));

    /// <summary>★ 界面里**一处** <c>Sizer.Solve</c> 都不许剩（<c>FlangeAutoSizer</c> 是另一条路，不算）。</summary>
    [Fact]
    public void 界面不再调用旧的定尺寸器()
    {
        foreach (var f in new[] { "LineDesignPage.cs", "Flow.cs", "AnalysisPage.cs", "MainForm.cs" })
        {
            string s = Ui(f);
            var hits = Regex.Matches(s, @"(?<!FlangeAuto)Sizer\.Solve");
            Assert.True(hits.Count == 0,
                $"{f} 里还有 {hits.Count} 处 Sizer.Solve —— 界面还在走「搜索」，"
                + "求解器的一切成果对用户为零。");
        }
    }

    /// <summary>三个调用点都换成了 <c>Solver.Solve</c>。</summary>
    [Fact]
    public void 三个调用点都换过来了()
    {
        string s = Ui("LineDesignPage.cs");
        // 数**真调用**（注释里也会提到它，不能算进去）
        // 2026-09-25：搜形状的粗筛与精算搬进 Core/ShapeSearchDriver（界面只调它）⇒ 页面里剩两处真调用（自动定厚、两个都算的挖舌孔族）
        Assert.Equal(2, Regex.Matches(s, @"Task\.Run\(\(\) => Solver\.Solve\(").Count);
        Assert.Contains("Task.Run(() => ShapeSearchDriver.Run(input, _base, opt, prog2, ct), ct)", s);   // 搜形状 ⇒ 驱动
        string drv = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShapeSearchDriver.cs"));
        Assert.Contains("Solver.Solve(d, b, o, p, t)", drv);   // 驱动里粗筛与精算都走 Solver.Solve
        Assert.Contains("Solver.Solve(seedD8, _base,", s);    // 自动定厚按钮
        Assert.Contains("Solver.Solve(seedAlt, _base,", s);   // R32：两个都算 ⇒ 挖舌孔族
    }

    /// <summary>
    /// ★ **精算必须开第二遍**，而且网格该多细要与复核同一个来源 ——
    /// 各算各的话，求出来的根照样不作数。
    /// </summary>
    [Fact]
    public void 精算开了细网格第二遍且网格来源唯一()
    {
        // 2026-09-25：精算在 Core/ShapeSearchDriver（界面只调它）
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShapeSearchDriver.cs"));
        Assert.Contains("MeshVerify.RequiredMeshFor(win.Design, baseIn)", s);   // F7′（2026-09-23，变因 = 决 29 自适应：签名加工艺参数；原钉 "MeshVerify.RequiredMeshFor(win.d)"）
        Assert.Contains("FineMm = finFine, FineRadiusMm = finFineR", s);
    }

    /// <summary>
    /// ★★ 没做第二遍的路径必须**当场说出来**。
    /// 少了这句，用户拿到的就是一个「看着正常的错数」。
    /// </summary>
    [Fact]
    public void 没做第二遍的路径都会喊出来()
    {
        string s = Ui("LineDesignPage.cs");
        // 精算：做没做都要说
        Assert.Contains("fin.FineRefined", s);
        Assert.Contains("**没做第二遍**", s);
        // 自动定厚按钮：没做，必须说
        Assert.Contains("if (!srD8.FineRefined)", s);
        // ★★ 2026-08-30 改口径：原来这里说「未经复核、不可直接交付。要可交付请跑「搜形状」」——
        //   **后半句是错的**：第二遍求根（在一张固定细网格上重解）≠ 网格无关复核
        //   （一档档加密到判据不再变）。而正牌按钮当天已经补上了。
        //   ⇒ 现在必须指向那个按钮，而且要带**实测的代价**，不许只说「可能不准」。
        Assert.Contains("这些数还没验过准不准", s);
        Assert.Contains(PtOptimize.UI.Flow.Cmd("core.verifyMesh").Text, s);   // 名字从 Flow 读，别再抄
        Assert.DoesNotContain("要可交付请跑「搜形状」", s);
        // 实测依据：粗网格 7.7 → 加密到位 9.5，差 1.8 K
        Assert.Contains("差 1.8 K", s);
        Assert.Contains("2.03 倍", s);   // 精算那一支仍然用它
    }

    /// <summary>流程图上的文字也要跟着改 —— 它是给用户看的，不是注释。</summary>
    [Fact]
    public void 流程图文字同步()
    {
        string s = Ui("Flow.cs");
        Assert.Contains("Solver.Solve", s);
        Assert.DoesNotContain("\"Sizer.Solve\"", s);
    }

    /// <summary>粗筛的进度条改成数「第 N 轮」—— 旧的按行首数字解析对 Solver 无效。</summary>
    [Fact]
    public void 进度条按新格式推进()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("if (s.StartsWith(\"第\", StringComparison.Ordinal)) seenRound++;", s);
        Assert.DoesNotContain("int.TryParse(s.AsSpan(0, 4).Trim(), out int rd)", s);
    }
}
