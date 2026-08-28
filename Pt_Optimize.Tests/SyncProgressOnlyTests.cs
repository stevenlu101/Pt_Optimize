using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **命令行不许用异步的 <c>Progress&lt;T&gt;</c>**（2026-08-28 抓到 4 处漏网）。
///
/// <c>Progress&lt;T&gt;</c> 会把回调 post 到同步上下文；控制台程序**没有**同步上下文
/// ⇒ post 到线程池 ⇒ 与主线程的 <c>Console.WriteLine</c> 交错成**乱序**。
///
/// 实测同一次 `--solve --seedprobe`：
/// <code>
///   入参 A　片0 / 片1 / 片2 / 片3      ← 循环顺序
///   入参 B　片1 / 片2 / 片3 / 片0      ← 乱了
/// </code>
/// 计算没乱（求解器按 j 递增遍历，<c>SolverResult.Trace</c> 也是按序 Add 的），
/// **乱的只是打印**。
///
/// ⚠ 这不是「只是好看」的问题：本项目的 trace 是拿来**判因果**的
///   （「抬了谁之后谁才不违反」「哪一步导致哪一步」）。
///   一份会乱序的日志让人读出**错的因果**，而且**它看起来完全正常** ——
///   正是本仓库反复在抓的那种错误形态。
///
/// ★★ 真正的教训不是「缺一个同步适配器」——<c>SyncProgress&lt;T&gt;</c> **早就有了**，
///   连注释都写明了病因。漏的是**4 个调用点没用它**。
///   「造好了没接线」在本项目里出现过太多次，所以这里不靠人记得，靠门。
///
/// ⚠ 界面（WinForms）**照旧用 <c>Progress&lt;T&gt;</c>**：那里有真正的同步上下文，
///   而且**需要**它把回调 marshal 回 UI 线程，不会乱序。所以本门只管 CLI。
/// </summary>
public class SyncProgressOnlyTests
{
    private static string Prog =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));

    /// <summary>★ CLI 里一处都不许再有 <c>new Progress&lt;</c>。</summary>
    [Fact]
    public void 命令行里没有异步进度回调()
    {
        var hits = Regex.Matches(Prog, @"new\s+Progress<");
        Assert.True(hits.Count == 0,
            $"Program.cs 里还有 {hits.Count} 处 new Progress< —— 控制台没有同步上下文，"
            + "它会把日志打成乱序，而乱序的 trace 会让人读出错的因果。改用 SyncProgress<T>。");
    }

    /// <summary>同步适配器**只准有一份**。我 2026-08-28 差点又在 Core 里造了第二份。</summary>
    [Fact]
    public void 同步适配器只有一份()
    {
        string root = HandoverDoc.Root();
        int n = 0;
        foreach (var f in Directory.GetFiles(Path.Combine(root, "Pt_Optimize"), "*.cs",
                                             SearchOption.AllDirectories))
            n += Regex.Matches(File.ReadAllText(f), @"class\s+SyncProgress").Count;
        Assert.Equal(1, n);
    }

    /// <summary>
    /// 病因与「本类存在不等于都用了它」这条教训要留在代码里 ——
    /// 否则下一个人只会看到一个平平无奇的适配器，不知道它为什么必须被用。
    /// </summary>
    [Fact]
    public void 病因与漏网教训都留字()
    {
        string s = Prog;
        Assert.Contains("控制台没有同步上下文", s);
        Assert.Contains("**本类存在，不等于所有地方都用了它**", s);
        Assert.Contains("判因果", s);
    }
}
