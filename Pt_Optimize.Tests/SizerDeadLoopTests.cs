using System.IO;
using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **定尺寸器说了「不可行」，指路不许再指它**（2026-09-03，跑 F 抓到）。
///
/// ══ 实况（Pt_Heater1.3dm，图纸几何交给解析路之后）
///
/// <code>
///   点「自动定厚」→ 「片0 舌保温 抬到上界 20.000 仍不过『法兰增量温降』⇒ 这组输入不可行」
///   判据表逐字不变 → 蓝色指示：点「自动定厚」→ 再点 → 同一句话 → …
///   走查器实测：**连指 3 次**，工程师跟着走会一直点下去。
/// </code>
///
/// 病灶与 <c>SizerNoLevels</c>（等厚板那条）**完全同形**：引擎自己给了结论，
/// 而指路只看「判据过没过」。当时补了「等厚板」这个入口，没补「顶到上界」这个。
///
/// ══ ⚠ 我修这条时又踩了一次「造好了没接线」
///
/// 第一版把 <c>f.SizerProvedInfeasible = _sizerInfeasible</c> 放进 <c>SyncAnalysisPending()</c>，
/// 而那个函数**只在首屏与「分析几何变数」跑完时被调**，解完之后一次都不会调
/// ⇒ 位永远推不到指路手上，走查照旧连指「自动定厚」，看起来像没修。
/// ⇒ 本门盯的是**位真的到了 <see cref="Flow.Next"/> 手上**（造一个 FlowState 去问它），
///   不是盯字段被赋过值 —— 后者那种门会陪着我一起绿。
/// </summary>
public class SizerDeadLoopTests
{
    /// <summary>判据没全过、解是新鲜的、几何判据都过 —— 只卡热-电量。</summary>
    private static FlowState Stuck()
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
                        Actual = 429.58, Limit = 10.0, Ok = false,     // ← 卡住的就是它
                    },
                    new ConstraintOut
                    {
                        Name = LineResult.Key.FreeTab, Kind = CheckKind.HardSafety,
                        Actual = 114.8, Limit = 100.0, Ok = true,      // 几何两条都过
                    },
                    new ConstraintOut
                    {
                        Name = LineResult.Key.DiscCover, Kind = CheckKind.HardSafety,
                        Actual = 29.27, Limit = 0.0, Ok = true,
                    },
                },
            },
        };
        st.CurrentSnap = st.SolvedSnap = "同一个快照";
        return st;
    }

    /// <summary>解析路：能搜形状、能自动定厚。</summary>
    private static bool Analytic(string cmd) =>
        cmd is "shape.search" or "core.autoThick" or "core.runLine" or "core.verifyMesh";

    /// <summary>★ 自证：没宣告不可行时，指的**就是**「自动定厚」（否则下面那条无意义）。</summary>
    [Fact]
    public void 自证_没宣告不可行时指的是自动定厚()
    {
        var ns = Flow.Next(Stuck(), Analytic);
        Assert.NotNull(ns);
        Assert.Equal("core.autoThick", ns!.CmdId);
    }

    /// <summary>★★★ 主门：宣告过不可行之后，指路必须换一根杠杆。</summary>
    [Fact]
    public void 宣告不可行之后不再指自动定厚()
    {
        var st = Stuck();
        st.SizerProvedInfeasible = true;

        var ns = Flow.Next(st, Analytic);
        Assert.NotNull(ns);
        Assert.NotEqual("core.autoThick", ns!.CmdId);
        Assert.Equal("shape.search", ns.CmdId);          // 引擎自己给的下一根杠杆就是形状
        Assert.Contains("走到头", ns.Why);                // 而且要说清为什么换
    }

    /// <summary>
    /// ★★ .3dm 模式下搜形状用不了 ⇒ 该指「◈ 图纸几何 → 参数」，
    /// **绝不指一个灰按钮**（本仓栽过这个坑，Flow.cs 里有原案）。
    /// </summary>
    [Fact]
    public void 图纸模式下改指把几何交给解析路()
    {
        var st = Stuck();
        st.SizerProvedInfeasible = true;
        var ns = Flow.Next(st, c => c is "geom.toanalytic" or "core.autoThick" or "core.runLine");
        Assert.NotNull(ns);
        Assert.Equal("geom.toanalytic", ns!.CmdId);
    }

    /// <summary>
    /// ★★ 两根自动杠杆都用不了时，要**老实说没有自动的下一步**，
    /// 而不是继续指一个必然无效的按钮。
    /// </summary>
    [Fact]
    public void 自动的招用完时老实说()
    {
        var st = Stuck();
        st.SizerProvedInfeasible = true;
        var ns = Flow.Next(st, c => c is "core.runLine");
        Assert.NotNull(ns);
        Assert.Equal("core.runLine", ns!.CmdId);
        Assert.Contains("自动的招用完了", ns.Why);
        Assert.Contains("风险由你判断", ns.Why);          // 用户拍板的口径：结果如何就如何
    }

    /// <summary>
    /// ★★★★ **接线门**：位要真的从页面推到 <see cref="FlowState"/> 上。
    ///
    /// 这条查的是我第一版栽的那个坑：赋值写在一个「解完之后不会被调」的函数里。
    /// ⇒ 钉住它必须出现在 <c>PushFlow()</c>（每次发布状态都会走的那一个）。
    /// </summary>
    [Fact]
    public void 不可行这一位是在PushFlow里发布的()
    {
        string src = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

        int a = src.IndexOf("private void PushFlow()", System.StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 PushFlow —— 断言失去了对象");
        int b = src.IndexOf("\n    }", a, System.StringComparison.Ordinal);
        Assert.True(b > a);
        string body = src[a..b];

        Assert.Contains("SizerProvedInfeasible", body);
        // 反面：别再挪回那个只调两次的函数里
        int c = src.IndexOf("private void SyncAnalysisPending()", System.StringComparison.Ordinal);
        int d = src.IndexOf("\n    }", c, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SizerProvedInfeasible", src[c..d]);
    }

    /// <summary>
    /// ★★★★ **`.3dm` 那个器的结构性停机也要接上**（2026-09-03，同一个死循环的第三个入口）。
    ///
    /// 实测（Pt_Heater1.3dm 等厚板，熔点闸停机）：
    /// <code>
    ///   【自动定厚】★ 第 2 片第 1 级峰值 3860 °C，已超铂熔点 1768 °C —— 停止迭代。
    ///     这不是迭代不够：该级太薄、电流被挤在窄带上，局部发热物理上就下不来。
    ///   → 指路仍指「自动定厚」→ 再点 → 判据表与总铂**逐字未变**
    /// </code>
    /// 引擎把话说得很清楚了，而指路只看「判据过没过」⇒ 继续推人去点同一个按钮。
    ///
    /// ⚠ 三个入口同形，前两个补过了（等厚板被拒、解析路旋钮顶到上界），这是第三个。
    ///   ⇒ 别再等第四个：任何「结构性停机」都必须能被指路读到。
    /// </summary>
    [Fact]
    public void 图纸路的结构性停机也接进了指路()
    {
        string page = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("_sizerInfeasible = r.Terminal;", page);

        // 引擎那一侧：三处结构性停机都要置位（熔点 / 越调越差 / 残差进平台）
        string sizer = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "FlangeAutoSizer.cs"));
        Assert.True(sizer.Split("Terminal = true").Length - 1 >= 3,
            "结构性停机少于三处置位 —— 漏掉的那一种会变成死循环的下一个入口");
        // 而且要说得出**为什么**停（指路要拿它给人看）
        Assert.Contains("TerminalWhy", sizer);
    }

    /// <summary>
    /// ★★ 只有**顶到上界**才算「不可行的证明」。<c>Feasible=false</c> 但没顶到上界
    /// 是「这一轮没搜到」—— 那种再点一次是有意义的，不许一并堵掉。
    /// </summary>
    [Fact]
    public void 只有顶到上界才算证明()
    {
        string src = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("_sizerInfeasible = srD8.HitBound && !srD8.Feasible;", src);
    }

    /// <summary>★★ 参数一动，上一次那句「不可行」就失效 —— 两条入口都要清。</summary>
    [Fact]
    public void 参数动过就不再算不可行()
    {
        string src = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        // MarkParamsChanged（左边参数表）与 ParamChanged（页面控件，含搜形状改几何）各一处
        Assert.True(src.Split("_sizerInfeasible = false;").Length - 1 >= 2,
            "「参数动了就清掉不可行」只接了一条路 —— 另一条会让旧结论挂着不走");
    }
}
