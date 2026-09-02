using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **界面上不许出现命令行开关名**（2026-09-02 用户拍板）。
///
/// 用户原话：「APP 不要有命令行形式操作，所需必要的计算全由 APP 程序操控」
/// 「这个 APP 不是给程序员用的是给现场工程师的」。
///
/// ══ 实物有多大（改之前实测）
///
/// <code>
///   ManualPage.cs       9 处   —— 其中整整一节「§10 常用命令行」，
///                                 一张教工程师敲 --cli --final2 / --hotspot 的表
///   LineDesignPage.cs   5 处   —— 渐变环提示语里印着 `--monotone`
///   MainForm.cs         1 处   —— 「请用 --cli --matrix / --save 等批处理模式」
///   Flow.cs             1 处   —— 「抄错要跑 8 分钟 --selfcheck 才知道」
/// </code>
///
/// 这与「界面不许出现判据代号」是同一条：<b>那是我的词汇，不是他的</b>。
/// 说明书里教命令行，还等于承认那几件事界面做不到 —— 做得到的就该在界面上，
/// 做不到的该去把它接上，而不是把开关名印给工程师。
///
/// ⚠ 开关本身**留着**：它们是我的验收工装（`--uishot`、`--selfcheck`、`--verifymesh`…），
///   只是不再出现在工程师看得见的任何一个字符串里。
///
/// ══ 为什么不能用通配正则
///
/// ManualPage 的 HTML 里有 **CSS 自定义属性**：<c>var(--ink)</c>、<c>--card:#fff</c>、
/// <c>var(--clamp)</c>。而 <c>--clamp</c> **同时也是**一个真的命令行开关 ——
/// 靠形状分不开。⇒ 本门先把 CSS 用法（<c>var(--x)</c> 与 <c>--x:</c> 声明）剔掉，
/// 再拿**从 Program.cs 里读出来的真开关清单**去比。
///
/// 清单从 Program.cs **读**、不另抄一份：开关改名时这里自动跟上，
/// 而不是留下一份对不上的死名单（`ParamCategoryPrefixes` 就是那样烂掉的）。
/// </summary>
public class NoCliFlagInUiTests
{
    /// <summary>工程师看得见的那几个文件。</summary>
    private static readonly string[] UiFiles =
    {
        "Flow.cs", "MainForm.cs", "LineDesignPage.cs", "ManualPage.cs",
        "AnalysisPage.cs", "StagePanel.cs", "TextFmt.cs",
    };

    /// <summary>Program.cs 里所有 <c>"--xxx"</c> 字面量 = 真开关清单。</summary>
    private static HashSet<string> RealFlags()
    {
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        var set = Regex.Matches(prog, "\"(--[a-z0-9]+)\"")
                       .Select(m => m.Groups[1].Value)
                       .ToHashSet(StringComparer.Ordinal);
        return set;
    }

    /// <summary>剔掉 CSS 用法之后的源码 —— <c>var(--x)</c> 与 <c>--x:</c> 声明都不算。</summary>
    private static string StripCss(string src)
    {
        src = Regex.Replace(src, @"var\(\s*--[a-z0-9-]+\s*\)", " ");
        src = Regex.Replace(src, @"--[a-z0-9-]+\s*:", " ");
        return src;
    }

    /// <summary>只看**字符串字面量**里的内容 —— 注释里提开关是给我看的，不进界面。</summary>
    private static IEnumerable<(int Line, string Text)> StringLiteralLines(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i];
            string trimmed = t.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;  // 注释行
            if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;   // 块注释续行
            foreach (Match m in Regex.Matches(t, "\"([^\"\\\\]|\\\\.)*\""))
                yield return (i + 1, m.Value);
        }
    }

    [Fact]
    public void 界面文字里不出现命令行开关名()
    {
        var flags = RealFlags();
        var hits = new List<string>();

        foreach (string f in UiFiles)
        {
            string path = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f);
            if (!File.Exists(path)) continue;
            string src = File.ReadAllText(path);

            foreach (var (line, text) in StringLiteralLines(src))
            {
                string clean = StripCss(text);
                foreach (Match m in Regex.Matches(clean, @"--[a-z0-9]+"))
                    if (flags.Contains(m.Value))
                        hits.Add($"{f}:{line}　{m.Value}　{Snip(text)}");
            }
        }

        Assert.True(hits.Count == 0,
            "界面文字里出现了命令行开关名 —— 这个 APP 是给现场工程师的，"
            + "开关名是我的工装词汇，不是他的操作。" + Environment.NewLine
            + string.Join(Environment.NewLine, hits.Take(20)));
    }

    private static string Snip(string s) =>
        s.Length <= 70 ? s : s[..70] + "…";

    /// <summary>
    /// ★ 自证：断言不能建立在空集上。清单读不到东西 ⇒ 上面那条恒真，
    /// 而它会一直报「通过」——本仓库最怕的那种形态。
    /// </summary>
    public class 自证
    {
        [Fact]
        public void 真开关清单不是空的()
        {
            var flags = RealFlags();
            Assert.True(flags.Count > 50, $"只读到 {flags.Count} 个开关，清单多半没读对");
            Assert.Contains("--cli", flags);
            Assert.Contains("--verifymesh", flags);
        }

        [Fact]
        public void 扫到的界面文件不是空集()
        {
            int n = UiFiles.Count(f => File.Exists(
                Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f)));
            Assert.True(n >= 5, $"只找到 {n} 个界面文件 —— 路径多半错了");
        }

        /// <summary>★ CSS 变量不许被误伤：<c>var(--clamp)</c> 与开关 <c>--clamp</c> 同名。</summary>
        [Fact]
        public void CSS变量不算违规()
        {
            Assert.DoesNotContain("--clamp", StripCss("background:var(--clamp);"));
            Assert.DoesNotContain("--card", StripCss("--card:#fff;"));
            // 而真的命令行用法必须留得下来
            Assert.Contains("--verifymesh", StripCss("请跑 --cli --verifymesh"));
        }
    }
}
