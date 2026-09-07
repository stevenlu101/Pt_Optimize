using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **等子进程一律要有上限**（2026-09-07 督导 S9）。
///
/// 督导实测：全仓 9 处 <c>WaitForExit()</c>，**一处上限都没有**，其中 7 处在
/// <c>Geometry3dm.cs</c> 的生产路径上 —— 工程师点「分析几何变数」「导出本页 3DM」
/// 走的就是它。Rhino 子进程一挂，APP 无限期等下去、一句话不说。
///
/// ⚠ 督导的原话我照记：「我没有实测触发过它，我只证明了没有任何东西挡着它发生」。
///   这条门守的正是后半句 —— **不许再有没挡的地方**。
///
/// 同一形状我自己刚栽过：一条 `cat &gt; /tmp/chk.csx` 在读 stdin，阻塞 13.52 小时、
/// 产出为零。那是命令不是产品，但病是同一种：**等一个永远不会来的东西，还不吭声**。
///
/// ⚠ 本门只钉「有没有上限」这个结构，不钉超时值也不钉措辞 —— 值该多大是工程判断。
/// </summary>
public class SubprocessMustNotHangTests
{
    [Fact]
    public void 全仓不许有裸的WaitForExit()
    {
        string root = HandoverDoc.Root();
        var bad = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => Bare(f))
            .ToList();

        Assert.True(bad.Count == 0,
            "这些地方在**无限期**等子进程：" + string.Join("、", bad)
          + "。子进程一挂，调用方就永远卡住而且一句话不说 —— "
          + "请走 Geometry3dm.WaitOrKill 那个形状：给上限、超时杀掉子进程、并明说是超时。"
          + "（只加个数字然后静默返回 = 把无限等待换成静默错误，更糟。）");
    }

    /// <summary>
    /// 一份档里「**没有上限**的等待」有哪些。
    ///
    /// ⚠ 判据按**前文**给，不按单行：带上限那次成立之后，再来一次裸的
    /// <c>WaitForExit()</c> 是**让异步读收尾**的标准写法，合法。
    /// 只有「从头到这一行为止，一次带上限的调用都没有」才算真的无限期等。
    /// （2026-09-07：本门第一版按单行判，把帮手自己的收尾那一次也判红了 ——
    ///   门太严会逼人放宽门，那比没门更坏；所以改判**结构**，不改宽度。）
    /// </summary>
    private static IEnumerable<string> Bare(string file)
    {
        var lines = File.ReadAllLines(file);
        bool boundedSeen = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string ln = lines[i];
            if (ln.TrimStart().StartsWith("//") || ln.TrimStart().StartsWith("///")) continue;
            if (Regex.IsMatch(ln, @"WaitForExit\(\s*[^)\s]")) boundedSeen = true;   // 带上限
            else if (Regex.IsMatch(ln, @"\.WaitForExit\(\s*\)") && !boundedSeen)
                yield return $"{Path.GetFileName(file)}:{i + 1}";
        }
    }
}
