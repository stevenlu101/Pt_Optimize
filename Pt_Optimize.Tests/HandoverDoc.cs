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
    public static string Path()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            string p = System.IO.Path.Combine(d.FullName, "HANDOVER.md");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(
            "往上找 8 层都没有 HANDOVER.md —— 断言失去了对象，**不能算通过**");
    }

    public static string Text() => File.ReadAllText(Path());
}
