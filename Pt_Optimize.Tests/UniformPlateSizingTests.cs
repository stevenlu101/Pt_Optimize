using System;
using System.IO;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **等厚板不该被拒在门外**（2026-09-03，F 端到端就撞死在这）。
///
/// `Pt_Heater1.3dm` 反推出来是**等厚板（只有一级）**，而 `.3dm` 的「自动定厚」
/// 要求 **≥2 级**，于是整个功能被拒绝：
/// <code>
///   【自动定厚：已拒绝】…反推出来只有一级厚度（等厚板），
///     「自动定厚」是逐级定厚，没有可调的级
/// </code>
///
/// ══ 那道闸卡错了地方
///
/// <see cref="FlangeAutoSizer.SolveByLevel"/> 是**两层**的：
/// <code>
///   外层：每片一个整体厚度 t[j]　靶 = 抽热误差（②′ 抽不够 ⇒ 加厚；③ 抽太多 ⇒ 削薄）
///   内层：每片各级的**相对比例**　靶 = 局部过热
/// </code>
/// 1 级时**只有内层**失效 —— 内层调完要做几何平均归一化（`adj[m] / norm`），
/// 而只有一级时 <c>norm == adj[0]</c> ⇒ 比例恒等于 1，永远不动。
/// **外层与级数无关，照常能调整片厚度。**
/// ⇒ 一并拒掉，等于把能用的那一半也砍了。
///
/// ⚠ 这不是「把 LevelSolver 融合进来」——那是另一件事（第三步）。
///   这里只是拆掉一道人为的闸。
/// </summary>
public class UniformPlateSizingTests
{
    private static string Page() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>★★★ 闸的口径：只剩「还没分析」，不再看级数。</summary>
    [Fact]
    public void 只有还没分析才拒()
    {
        string s = Page();
        // 反面：不许再要求 ≥2 级
        Assert.DoesNotContain("_levels[0].Length > 1", s);
        // 正面：两处（指路读的那一位、RunAsync 里真正拒绝的那一处）口径相同
        Assert.Contains("f.SizerNoLevels = !_srcAnalytic.Checked && _levels is not { Length: > 0 };", s);
        Assert.Contains("if (!_srcAnalytic.Checked && _levels is not { Length: > 0 })", s);
    }

    /// <summary>
    /// ★★★ 放行之后**必须说清只有外层在动** —— 不说的话工程师会以为
    /// 逐级比例也在被优化，而 1 级时内层恒等于没动。
    /// 「让人以为程序做了它没做的事」是本项目最贵的一类错。
    /// </summary>
    [Fact]
    public void 等厚板要说清只有整片厚度在调()
    {
        string s = Page();
        Assert.Contains("bool oneLevel = lvl is { Length: > 0 } && lvl[0].Length == 1;", s);
        Assert.Contains("等厚板（只有一级）", s);
        Assert.Contains("只有**整片厚度**在调", s);
    }

    /// <summary>
    /// ★★ 指路不再把等厚板的人推去「◈ 图纸几何 → 参数」。
    /// 那一步是**改几何来源**的决定，把它当成等厚板的唯一出路是错的。
    /// </summary>
    [Fact]
    public void 指路不再因为等厚板就把人推走()
    {
        var st = new FlowState
        {
            Last = new LineResult
            {
                Ok = true,
                Converged = true,
                Checks = new[]
                {
                    new ConstraintOut
                    {
                        Name = LineResult.Key.FlangeDip, Kind = CheckKind.HardSafety,
                        Actual = 422.8, Limit = 10.0, Ok = false,
                    },
                },
            },
            SizerNoLevels = false,        // 分析过了、等厚板 —— 现在这一位是 false
        };
        st.CurrentSnap = st.SolvedSnap = "同一个快照";

        var ns = Flow.Next(st, c => c is "core.autoThick" or "core.runLine" or "geom.toanalytic");
        Assert.NotNull(ns);
        Assert.Equal("core.autoThick", ns!.CmdId);     // 指的是自动定厚，不是换几何来源
    }

    /// <summary>
    /// ★ 自证：还没分析时它**仍然**拒，且指的是「分析几何变数」——
    /// 放宽不等于放弃把关。
    /// </summary>
    [Fact]
    public void 自证_还没分析时仍然拒并指去分析()
    {
        var st = new FlowState
        {
            Last = new LineResult
            {
                Ok = true,
                Converged = true,
                Checks = new[]
                {
                    new ConstraintOut
                    {
                        Name = LineResult.Key.FlangeDip, Kind = CheckKind.HardSafety,
                        Actual = 422.8, Limit = 10.0, Ok = false,
                    },
                },
            },
            SizerNoLevels = true,
        };
        st.CurrentSnap = st.SolvedSnap = "同一个快照";
        var ns = Flow.Next(st, c => c is "core.autoThick" or "core.runLine" or "geom.analyze");
        Assert.NotNull(ns);
        Assert.Equal("geom.analyze", ns!.CmdId);
        Assert.Contains("还没反推", ns.Why);
    }

    /// <summary>
    /// ★★ 治根因：内层归一化对 1 级恒等 —— 这条钉住那段代码还在，
    /// 免得将来有人「顺手」把归一化删了却不知道它正是 1 级失效的原因。
    /// </summary>
    [Fact]
    public void 内层归一化那段还在并且注明了它对一级恒等()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "FlangeAutoSizer.cs"));
        Assert.Contains("几何平均拉回 1", s);
        Assert.Contains("scale[j][m] * adj[m] / norm", s);
    }
}
