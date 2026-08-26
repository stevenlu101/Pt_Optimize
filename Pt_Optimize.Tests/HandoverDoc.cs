using System;
using System.IO;

namespace PtOptimize.Tests;

/// <summary>
/// HANDOVER.md 的定位与读取 —— **唯一来源**。
///
/// 本来只有 HandoverGateCountTests 一处，2026-08-24 加 CriteriaTableTests 时抽出来：
/// 同一个「往上找 8 层」若各写一份，两份将来会漂开（本项目栽在「同一件事两处来源」上不止一次）。
/// </summary>
internal static class HandoverDoc
{
    /// <summary>仓库根 = 含 HANDOVER.md 的那一层。**「往上找 8 层」只有这一份实现。**</summary>
    public static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(d.FullName, "HANDOVER.md"))) return d.FullName;
        }
        throw new FileNotFoundException(
            "往上找 8 层都没有 HANDOVER.md —— 断言失去了对象，**不能算通过**");
    }

    public static string Path() => System.IO.Path.Combine(Root(), "HANDOVER.md");

    public static string Text() => File.ReadAllText(Path());
}
