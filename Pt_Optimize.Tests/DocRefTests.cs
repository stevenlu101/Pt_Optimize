using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 注释里写的「见 XX §N」必须**指得到真东西**。
///
/// 为什么值得一道门：本项目的注释承担着「判断的出处」这个职责 ——
/// §1.83 立的规矩就是「每条判据的限值必须有来源」。
/// 而**凭空的出处比没有出处更坏**：它让人以为查得到，于是不再追问。
///
/// 2026-08-24 实测抓到三处：
///   · Numerics.cs 与 SegmentSolver.cs 指向「理论模型 6.2.1 节」—— 该节不存在（应为 4.4）
///   · VerificationTests.cs 指向「理论模型 6.1.1 节」（两处）—— 不但该节不存在，
///     **理论模型通篇没有讲过保温层 k(T) 的积分平均**。
/// 同一天还在同族里抓到三处更严重的：注释宣称的**机制**根本没实现
/// （FlowState.RampScreen 零赋值、① 门禁名单少一条、`--cli --manual` 不存在）。
///
/// ⚠ 本档只核**点名了文件**的引用（「理论模型 §X」「工程师版 §X」）。
///   裸写的「§X.Y」按惯例指 HANDOVER，但那有 170 多处、且历史上有跨文档引用，
///   一刀切会制造噪声 —— 噪声大的门迟早被关掉。**先把能确定的那部分钉死。**
/// </summary>
public class DocRefTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "HANDOVER.md"))) return d.FullName;
        throw new DirectoryNotFoundException("往上找 8 层都没有仓根 —— 断言失去了对象");
    }

    /// <summary>被点名的文件 → 它实际有哪些小节号。</summary>
    private static readonly (string Word, string File)[] Docs =
    {
        ("理论模型", "docs/Pt_理论模型.md"),
        ("工程师版", "docs/Pt_工程师版.md"),
    };

    private static HashSet<string> HeadingsOf(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            var m = Regex.Match(line, "^#+[ ]+([0-9]+(?:[.][0-9]+)*)");
            if (m.Success) set.Add(m.Groups[1].Value);
        }
        return set;
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (string dir in new[] { "Pt_Optimize", "Pt_Optimize.Geom", "Pt_Optimize.Tests", "tests" })
        {
            string d = Path.Combine(root, dir);
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            {
                // ⚠ 跳过本档自己：它必须**引用那些坏引用的原文**才说得清病灶，
                //   否则本门会把自己的病历当成新病例。
                //   （别的地方一律不豁免 —— 不设可被滥用的开关；
                //     要写历史上的错误编号，就别带 § 号，像 VerificationTests 那样。）
                if (Path.GetFileName(f) == "DocRefTests.cs") continue;
                yield return f;
            }
        }
    }

    [Fact]
    public void EveryNamedDocReference_ResolvesToARealHeading()
    {
        string root = RepoRoot();

        // 自证一：被引的文件在，且真的解析得出小节号
        var headings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (word, rel) in Docs)
        {
            string full = Path.Combine(root, rel);
            Assert.True(File.Exists(full), $"{rel} 不在 —— 断言失去了对象，不能算通过");
            var hs = HeadingsOf(full);
            Assert.True(hs.Count >= 10,
                $"{rel} 只解析出 {hs.Count} 个小节号 —— 多半是标题写法变了，解析坏了");
            headings[word] = hs;
        }

        var bad = new List<string>();
        int seen = 0;
        foreach (string f in SourceFiles(root))
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
                foreach (var (word, _) in Docs)
                {
                    // 「理论模型 §4.4」「理论模型 §4.4「标题」」都算
                    foreach (Match m in Regex.Matches(lines[i], word + @"[^§]{0,8}§[ ]*([0-9]+(?:[.][0-9]+)*)"))
                    {
                        seen++;
                        string sec = m.Groups[1].Value;
                        if (headings[word].Contains(sec)) continue;
                        bad.Add($"{Path.GetRelativePath(root, f)}:{i + 1}  「{word} §{sec}」 "
                              + $"—— 该文件里没有这一节：{lines[i].Trim()}");
                    }
                }
        }

        // 自证二：一处都没抓到不是「没问题」（空集恒真是本项目反复栽的跟头）
        Assert.True(seen >= 3,
            $"全仓只抓到 {seen} 处「理论模型/工程师版 §N」引用 —— 正则多半失效了");

        Assert.True(bad.Count == 0,
            "有指不到的出处（**凭空的出处比没有出处更坏**）：" + Environment.NewLine
            + string.Join(Environment.NewLine, bad));
    }
}
