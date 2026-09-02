using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **段表的排版事件不许被当成「用户改了参数」**（2026-09-02，探针取证后才修对）。
///
/// ══ 实物
///
/// `--reconcile 0.8` 报「界面这边解出来了 **(null)**」，而输出框写着
/// 「参数已改…正在后台重算」—— 看起来像解不出来，其实是**被自己掐了**：
/// <code>
///   DataGridView.OnColumnCollectionChanged_PostNotification
///     → AddNewRow → RowsAdded → ParamChanged() → _cts?.Cancel()
///                                                 ↑ 掐掉正在跑的整线解
/// </code>
/// 栈探针实测：10 次未被抑制的 ParamChanged **全部**来自 RowsAdded，
/// 而没有一次是人加的段 —— 全是排版/换列。
///
/// ══ 我在这条上判断错了四次
///
/// <code>
///   ① 猜 _suppressAuto      方向就错
///   ② 猜 RowsAdded          方向对、修法错（靠 _plateBox.Controls 的状态判断，排版期它会变）
///   ③ 装探针                探针**没编译过**（反斜杠转义），我读了一个坏仪器
///   ④ 记住片数              修对了一半 —— 漏了**同一事件上的第二个处理器**
/// </code>
/// 每一步真正的前进都来自取证（打印输出框 → 二分 worktree → 栈探针），不是推断。
///
/// ⚠ 那个无守卫的 `RowsAdded += ParamChanged()` **不是本次新加的**（4f9c6ee 就有），
///   是我在同一事件上又挂一个之后把它推到了会出事的时机。
/// </summary>
public class SegGridLayoutTests
{
    private static string Ui() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>★★★ RowsAdded 只能挂**带守卫**的那个，不许再直接调 ParamChanged。</summary>
    [Fact]
    public void 段表加行事件不许直接调ParamChanged()
    {
        string s = Ui();
        Assert.DoesNotContain("_segGrid.RowsAdded += (_, _) => ParamChanged();", s);
        Assert.Contains("_segGrid.RowsAdded += (_, _) => SegsChanged();", s);
        // 同一个事件只许挂一个 —— 两个处理器时守卫会被另一个绕过（这次就是）
        Assert.Equal(1, Regex.Matches(s, @"_segGrid\.RowsAdded \+=").Count);
    }

    /// <summary>★★ 守卫是「片数真的变了」，不是控件状态（后者在排版期会变）。</summary>
    [Fact]
    public void 守卫看的是片数不是控件状态()
    {
        string s = Ui();
        Assert.Contains("private int _plateCountShown", s);
        Assert.Contains("if (n == _plateCountShown) return;", s);
    }

    /// <summary>
    /// ★★ 进度回调必须回到 UI 线程。
    /// 二分时抓到：`Progress&lt;T&gt;` 取不到同步上下文就退到线程池，
    /// `_status.Text = s` 变成跨线程动 UI，整个进程带栈崩掉 ——
    /// 在工程师机器上同样会随机崩，且崩在进度回调里，最难查。
    /// </summary>
    [Fact]
    public void 进度回调回到UI线程()
    {
        string s = Ui();
        Assert.Contains("private void OnUi(Action a)", s);
        // 五处 Progress 回调一个都不许漏
        int total = Regex.Matches(s, @"new Progress<string>\(").Count;
        int wrapped = Regex.Matches(s, @"new Progress<string>\([sm] => OnUi\(").Count;
        Assert.True(total > 0 && wrapped == total,
            $"{total} 处进度回调里只有 {wrapped} 处回到了 UI 线程 —— 漏的那些会随机崩");
    }
}
