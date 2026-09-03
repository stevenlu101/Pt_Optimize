using System.Reflection;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **改一项参数、再看一次结果 —— 整个 APP 当场崩掉**（2026-09-03 抓到）。
///
/// ══ 怎么抓到的：跑 F（用 APP 读 Pt_Heater1.3dm）
///
/// 图纸路走到第 3 步「◈ 图纸几何 → 参数」，进程带栈崩掉：
/// <code>
///   NullReferenceException
///     at LineDesignPage.Show(LineResult r, String autoNote)   ← _pendingReview.Length
///     at LineDesignPage.AdoptShapeToAnalytic()                ← 它先调了 MarkParamsChanged
/// </code>
///
/// ══ 病灶
///
/// <c>MarkParamsChanged</c> 把 <c>_pendingReview</c> 设成 <b>null</b>，而这个字段
/// 声明是 <c>private string _pendingReview = ""</c>（**非可空**），<c>Show</c> 直接
/// <c>.Length</c>。Nullable 是开着的 ⇒ 编译器给了 CS8625，但**警告不是错误**，过了。
///
/// ══ ⚠ 影响面比「图纸路」大得多 —— 这才是要记住的部分
///
/// <c>MarkParamsChanged</c> 有两个调用点，图纸路只是其中之一。另一个是
/// <c>MainForm._grid.PropertyValueChanged</c> —— **左边参数表改任何一项**。
/// 而它开头有 <c>if (_solvedSnap is null &amp;&amp; _last is null) return;</c> ⇒ 只在
/// **解过之后**才会踩到。所以现场工程师的路径是：
/// <code>
///   核算整线（十几分钟）→ 觉得某个控温点要调 → 改左边一格 → 再核算一次 → 崩
/// </code>
/// 也就是**每一次迭代设计**都会崩，而第一次跑不会 —— 最容易在验收时漏掉的形状。
///
/// ══ 为什么以前的门没拦住
///
/// 走查器改参数走的是 <c>_wall.Value = …</c>（触发 <c>ParamChanged</c>），
/// **不是** PropertyGrid 那条（<c>MarkParamsChanged</c>）。两条路名字只差一个词，
/// 一条被走查天天走，另一条一次都没走过。
/// </summary>
public class ParamChangedThenShowTests
{
    private static object? F(object o, string n) => typeof(LineDesignPage)
        .GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o);

    private const BindingFlags Any =
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    // ⚠ Show 有重载 ⇒ 必须指定签名，否则 AmbiguousMatchException（那不是 APP 的错）
    private static void Show(object page, LineResult r) => typeof(LineDesignPage)
        .GetMethod("Show", Any, null, new[] { typeof(LineResult), typeof(string) }, null)!
        .Invoke(page, new object?[] { r, "" });

    private static void MarkChanged(object page, string what) => typeof(LineDesignPage)
        .GetMethod("MarkParamsChanged", Any)!.Invoke(page, new object?[] { what });

    private static LineResult Solved() => new()
    {
        Ok = true,
        Converged = true,
        Checks = new[]
        {
            new ConstraintOut
            {
                Name = LineResult.Key.NetFlux, Kind = CheckKind.HardSafety,
                Actual = 1.12, Limit = 0.0, Ok = true,
            },
        },
    };

    /// <summary>
    /// ★★★ 现场工程师那条路：解过一次 → 改左边参数表 → 再显示一次结果。
    /// 这条门直接调**真的** <c>MarkParamsChanged</c> 与**真的** <c>Show</c>。
    /// </summary>
    [Fact]
    public void 改过参数表之后再显示结果不许崩()
    {
        var page = new LineDesignPage(new DesignInputs());
        typeof(LineDesignPage).GetField("_last", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(page, Solved());

        MarkChanged(page, "控温点 HC1");     // ← 左边参数表改了一格
        Show(page, Solved());                // ← 下一次解完，画结果

        // 走到这里就算过：上一版在 Show 里 NullReferenceException。
        Assert.NotNull(F(page, "_last"));
    }

    /// <summary>
    /// ★★ 治根因：<c>_pendingReview</c> 是**非可空** string，任何地方都不许塞 null。
    ///
    /// 上面那条查行为，这条查**源码里还有没有第二处**同样的写法 ——
    /// 行为门只覆盖它走到的那一条路，而这个字段有 5 个使用点。
    /// </summary>
    [Fact]
    public void 形状体检报告字段不许被塞成null()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.DoesNotContain("_pendingReview = null", src);
        // 自证：字段还在，且声明仍是非可空 —— 否则上面这条是在查一个不存在的东西
        Assert.Contains("private string _pendingReview = \"\";", src);
    }

    /// <summary>
    /// ★ 自证：<c>MarkParamsChanged</c> 在这个状态下**真的做了事**。
    /// 它开头有 <c>if (_solvedSnap is null &amp;&amp; _last is null) return;</c> ——
    /// 要是它直接返回了，上面那条行为门就是空转的。
    /// </summary>
    [Fact]
    public void 自证_这个状态下改参数真的会作废旧解()
    {
        var page = new LineDesignPage(new DesignInputs());
        typeof(LineDesignPage).GetField("_last", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(page, Solved());          // 有解 ⇒ 开头那个 early-return 不成立

        MarkChanged(page, "控温点 HC1");
        // 它跑进函数体的证据：输出框顶上多了那句「参数表改了…」
        Assert.Contains("控温点 HC1", ((Control)F(page, "_out")!).Text);
    }
}
