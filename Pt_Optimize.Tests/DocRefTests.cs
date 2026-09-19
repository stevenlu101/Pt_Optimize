using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using PtOptimize.UI;
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
/// ⚠ 两级口径，因为两种引用的确定性不同：
///   · **点名了文件**的（「理论模型 §X」「工程师版 §X」）—— 严格核到那个文件。
///   · **裸写的「§X.Y」**（238 处）—— 只要求它在四份文件（HANDOVER / 理论模型 /
///     工程师版 / APP 使用说明书）之一里存在。这一级**抓得到「号码根本不存在」，
///     抓不到「指错了文件」** —— 说清楚，不假装更强。实测 238 处只有 3 处落空，
///     噪声足够低；要求每处都点名文件才会制造噪声，而**噪声大的门迟早被人关掉**。
///   · UiWiring/Program.cs **自己**档里的裸「§N」指的是它自己的节号（如「§28 别催」），
///     那一档另外允许 UiWiring 的 Head 编号。
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
        // UiWiring 不是 markdown 档，节号另由 UiWiringSections 解析（见下）
    };

    private static HashSet<string> HeadingsOf(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            // ⚠ 末尾的 [a-z]? 不能省：HANDOVER 用了大量**字母尾**小节（4.2b、4.2l …，共 40 个）。
            //   漏掉它们会让「四份文件里都没有这一节」大面积误报 ——
            //   2026-08-24 装门时正是自证那条（小节数 >= 80）把这个解析错误当场挡下的。
            var m = Regex.Match(line, "^#+[ ]+([0-9]+(?:[.][0-9]+)*[a-z]?)");
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

    private const char Q = (char)34;

    /// <summary>UiWiring 自己的节号（Head("28 …")）。</summary>
    private static HashSet<string> UiWiringSections(string root)
    {
        string f = Path.Combine(root, "tests", "UiWiring", "Program.cs");
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(f)) return set;
        foreach (Match m in Regex.Matches(File.ReadAllText(f), "Head[(]" + Q + "([0-9]+)"))
            set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>裸写的「§N」：只要求它在四份文件之一里存在（口径见类注释）。</summary>
    [Fact]
    public void EveryBareSectionReference_ExistsInSomeDoc()
    {
        string root = RepoRoot();
        string[] all = { "HANDOVER.md", "docs/Pt_理论模型.md",
                         "docs/Pt_工程师版.md", "docs/APP使用说明书.md" };
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rel in all)
        {
            string full = Path.Combine(root, rel);
            Assert.True(File.Exists(full), $"{rel} 不在 —— 断言失去了对象");
            known.UnionWith(HeadingsOf(full));
        }
        Assert.True(known.Count >= 80,
            $"四份文件合起来只解析出 {known.Count} 个小节号 —— 解析多半坏了");

        var uiw = UiWiringSections(root);
        Assert.True(uiw.Count >= 20, $"UiWiring 只解析出 {uiw.Count} 个节号 —— 解析坏了");

        var bad = new List<string>();
        int seen = 0;
        foreach (string f in SourceFiles(root))
        {
            bool isWire = f.Replace('/', Path.DirectorySeparatorChar)
                           .EndsWith(Path.Combine("tests", "UiWiring", "Program.cs"),
                                     StringComparison.Ordinal);
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                // 点名文件的那一级归上面那条断言；ManualPage 用的是它自己的编号
                if (lines[i].Contains("理论模型", StringComparison.Ordinal)
                 || lines[i].Contains("工程师版", StringComparison.Ordinal)
                 || lines[i].Contains("UiWiring", StringComparison.Ordinal)   // 交给严格那一级
                 || lines[i].Contains("ManualPage", StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(lines[i], "§[ ]*([0-9]+(?:[.][0-9]+)*[a-z]?)"))
                {
                    seen++;
                    string sec = m.Groups[1].Value;
                    if (known.Contains(sec)) continue;
                    if (isWire && uiw.Contains(sec)) continue;   // 本档内的自指
                    bad.Add($"{Path.GetRelativePath(root, f)}:{i + 1}  「§{sec}」 "
                          + $"—— 四份文件里都没有这一节：{lines[i].Trim()}");
                }
            }
        }
        Assert.True(seen >= 100, $"全仓只抓到 {seen} 处裸「§N」—— 正则多半失效了");
        Assert.True(bad.Count == 0,
            "有指不到的出处（**凭空的出处比没有出处更坏**）：" + Environment.NewLine
            + string.Join(Environment.NewLine, bad));
    }

    /// <summary>
    /// **注释里不许拿「档名.cs:行号」当引用。**
    ///
    /// 行号在活文件里每编辑一次就漂一次 —— 它今天指到的地方，明天指到别处，
    /// **而且不会有任何东西报错**。2026-08-24 全仓只有两处，**两处都已经错了**：
    ///   · `GeometryScreen` → `PlateCurrent2D.cs:231`（本意是 HalfWidthClamped 那句赋值，
    ///     当天被我自己的另一处编辑挤到 248 行；231 行现在是「阶梯已给，外缘取基准厚」）
    ///   · `Flow` → `tests/UiWiring/Program.cs:502`（本意是反射 TextFmt 那两处；
    ///     502 行现在是 `Set(page, "_suppressAuto", false)`）
    /// 同族的还有 `ShellThermal` 把 HANDOVER 的**行号**写成了节号（§2038）。
    ///
    /// ⇒ 引用只准指**名字**（型别、成员、小节号、UiWiring 节号）—— 名字改了会编译错或被本档抓到，
    ///   行号改了什么都不会发生。
    /// </summary>
    [Fact]
    public void NoLineNumberReferencesInComments()
    {
        string root = RepoRoot();
        var bad = new List<string>();
        int scanned = 0;
        foreach (string f in SourceFiles(root))
        {
            string[] lines = File.ReadAllLines(f);
            scanned += lines.Length;
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], "[A-Za-z_][A-Za-z0-9_.]*[.]cs:[0-9]+"))
                    bad.Add($"{Path.GetRelativePath(root, f)}:{i + 1}  「{m.Value}」"
                          + $"  {lines[i].Trim()}");
        }
        // 自证：一行都没扫到不是「没问题」
        Assert.True(scanned > 5000, $"只扫到 {scanned} 行原始码 —— 档案枚举多半坏了");
        Assert.True(bad.Count == 0,
            "注释里拿行号当引用（**行号一编辑就漂，而且不会有任何东西报错**）：" + Environment.NewLine
            + string.Join(Environment.NewLine, bad));
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
        // ★ 「UiWiring §N」也走严格这一级（2026-08-24 补）：接线门的节号会随着加节变动，
        //   而全仓有好几处注释指着它（Flow、StagePanel、各节之间互指）。
        //   节号一旦重编，陈旧引用当场现形 —— 这正是本门最有价值的用法。
        headings["UiWiring"] = UiWiringSections(root);
        Assert.True(headings["UiWiring"].Count >= 20,
            $"UiWiring 只解析出 {headings["UiWiring"].Count} 个节号 —— 解析坏了");

        var bad = new List<string>();
        int seen = 0;
        foreach (string f in SourceFiles(root))
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
                foreach (string word in headings.Keys)
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
    /// <summary>
    /// ★ R47 第三轮 N6（2026-09-13）：说明书是给工程师看的 HTML，**不许出现字面 `**`**（那是源码里的加粗记号，页面上得转成粗体）。
    /// 病：首段「咬住它的：{fd.Binding}」没过 Md()，Binding 里的 **本档不过** 就原样印成两对星号。
    /// 四档内置档 + 一份图纸档（N5：说明书读到图纸档不造解析板、印拒绝那句）都走一遍。
    /// </summary>
    [Fact]
    public void ManualPage_BuildHtml_输出不含字面星号()
    {
        foreach (var fd in DesignSpec.Builtin)
        {
            string html = ManualPage.BuildHtml(fd);
            int at = html.IndexOf("**", StringComparison.Ordinal);
            Assert.True(at < 0, $"档「{fd.Name}」的说明书里有字面 **：…{html[Math.Max(0, at - 60)..Math.Min(html.Length, at + 60)]}…");
        }
        var dr = DesignSpec.Builtin[0].Clone();
        dr.Name = "图纸档（说明书门）";
        dr.GeomSource = DesignSpec.GeomSourceDrawing;
        dr.FlangeFile3dm = new[] { "D:/图/入口.3dm" };
        dr.ThicknessScale = new[] { 1.15, 0.90, 1.25, 1.05 };
        dr.TabThickMm = Enumerable.Repeat(double.NaN, 4).ToArray();
        string h2 = ManualPage.BuildHtml(dr);
        Assert.DoesNotContain("**", h2);
        Assert.Contains("本档出自图纸路径", h2);
        Assert.Contains("请在界面里载入并先分析几何", h2);
        Assert.DoesNotContain("NaN", h2);
    }
}
