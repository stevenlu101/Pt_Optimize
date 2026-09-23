using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **界面上不许再出现判据代号**（用户 2026-09-02 排的最后一件工作）。
///
/// 用户原话：「APP 内判据代号全表的代号全换成全名（+单位）」。更早一次说得更硬：
/// 「UI 内严禁使用 ②′ 这类的表示」——「②′／②″／③ 工程师看不懂」。
///
/// ══ 这道门难在「分得清两种圈号」
///
/// 界面上的圈号有**两族**，只有一族该死：
/// <code>
///   判据代号   「③ 法兰增量温降」「②′ 管孔净流入」「⑤⑥ 会是无法判定」   ← 禁
///   列表序号   「① 先加舌保温；② 再削薄板厚；③ 都用尽了才动形状」        ← 留
///   区域编号   界面地图上标 ①②③④ 指参数表/状态面板/阶段轨/工具条        ← 留
///   阶段号     「① 整线核算 ★」「② 交付」（Flow.Stages 的 Title）        ← 留
/// </code>
/// 一刀切扫圈号 ⇒ 天天误报，最后被人加豁免加到失效。
/// ⇒ 本门只认**判据代号的形状**：圈号后面紧跟着的，是不是某条判据的名字。
///   名字从 <see cref="LineResult.Key"/> 反射取 —— 判据改名，门自动跟着改。
///
/// ══ 顺带钉住第二件事：**代号后面跟不跟名字都不行**
///
/// 2026-08-29 做过一版「代号必须带解释」，2026-08-30 被用户推翻：判据代号 ⑤
/// （舌片自由段）与页签上的阶段号 ⑤ 形状相同、含义无关，摆在同一个界面上必然误读。
/// ⇒ 界面侧根本不出现代号，对照表只留给命令行与 HANDOVER（读者是开发者）。
/// </summary>
public class NoCriterionCodeInUiTests
{
    /// <summary>
    /// APP 会显示给工程师看的那几个档。HANDOVER / Program.cs（命令行）不在内。
    ///
    /// ★★★ 2026-09-18，Opus 5（接线复核查出）：**这份名单原来是手抄的六个 <c>UI/*.cs</c>**。
    ///   而工程师看得见的字还写在 <c>Core/FinalCheckReport.cs</c>（三关结论块）、
    ///   <c>Core/InstallReport.cs</c>（安装报告正文）、<c>Core/GradeChoices.cs</c>（牌号下拉每一行的字）、
    ///   <c>UI/GradeNameEditor.cs</c>（当天新加的档，六个档名里根本没有它）——
    ///   这几份里写出代号，这道门一个字都看不见。**手抄的门守不住手抄的病。**
    /// ⇒ 名单改成读生产侧那一份公开清单 <see cref="VisibleText.Sources"/>：
    ///   界面目录整层自动扫（新加一页不必记得回来改名单），Core 侧点名那几份正文。
    /// </summary>
    private static IReadOnlyList<string> UiFiles()
        => VisibleText.Sources(HandoverDoc.Root())
                      .Select(s => s.Replace('/', Path.DirectorySeparatorChar))
                      .ToArray();

    /// <summary>判据名（已剥壳）—— 从 Key 常量反射取，不手抄。</summary>
    /// <remarks>R48 B（2026-09-14 Opus 5）：旧判法两条降为参考量后名字带「（旧判法）」后缀（「法兰增量温降（旧判法）」「圆盘区最高温 − 管温（旧判法）」），
    ///   「③ 法兰增量温降」这种**旧写法**就不再以任何判据名开头 ⇒ 门会悄悄漏掉它（自证那条当场红了）。
    ///   ⇒ 带「（旧判法）」的名字把**词干**（去掉后缀、去掉「 − 管温」）也加进名单：旧代号配旧名字照样禁。这是加严，不是放宽。</remarks>
    private static string[] PlainNames() =>
        typeof(LineResult.Key)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => Criteria.Plain((string)f.GetValue(null)!))
            .SelectMany(n => n.Contains("（旧判法）")
                ? new[] { n, n.Split('（')[0].Trim(), n.Split('（')[0].Split(" − ")[0].Trim() }
                : new[] { n })
            .Where(n => n.Length >= 2)          // 「管 J」没有代号，不参与
            .Distinct()
            .ToArray();

    /// <summary>
    /// 判据代号的形状：圈号（只取生产代码那一份 <see cref="Criteria.CodeChars"/>）后面可带 ′ ″。
    /// 2026-09-14 Opus 5（复审）：原来三处各手抄一遍「①…⑥」—— 热偶读数基准的两条用了新代号 ⑦⑧，手抄的门就看不见它们（门不许手抄生产配方）。
    /// </summary>
    private static readonly string CodePattern = "[" + Criteria.CodeChars + "][′″]?";

    /// <summary>
    /// 自证：新代号 ⑦⑧ 配新判据名，门照样抓得到（2026-09-14 Opus 5 复审补）。
    /// </summary>
    [Fact]
    public void 自证_新代号配判据名也抓得到()
    {
        var names = PlainNames();
        foreach (var offender in new[] { "卡的是 ⑦ 最热铂高出热偶读数", "卡的是 ⑧管根低于热偶读数" })
        {
            bool caught = Regex.Matches(offender, CodePattern).Any(m =>
            {
                string rest = offender[(m.Index + m.Length)..].TrimStart(' ', '　');
                return names.Any(n => rest.StartsWith(n, StringComparison.Ordinal));
            });
            Assert.True(caught, $"门抓不到「{offender}」");
        }
    }

    /// <summary>字符串字面量（去掉整行注释）。够用：本仓的界面文字都写在字面量里。</summary>
    private static IEnumerable<(int Line, string Text)> Literals(string src)
    {
        var pat = new Regex("\"([^\"]*)\"");
        int n = 0;
        foreach (var l in src.Split('\n'))
        {
            n++;
            string t = l.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)
             || t.StartsWith("*", StringComparison.Ordinal)) continue;
            foreach (Match m in pat.Matches(l)) yield return (n, m.Groups[1].Value);
        }
    }

    /// <summary>
    /// ★★★ 主门：界面字串里，圈号（含 ′ ″）后面**紧跟判据名**的，一个都不许有。
    /// </summary>
    [Fact]
    public void 界面上不出现判据代号()
    {
        string root = HandoverDoc.Root();
        var names = PlainNames();
        Assert.NotEmpty(names);                 // 自证：名字表空的话下面恒真

        var bad = new List<string>();
        var files = UiFiles();
        Assert.True(files.Count >= 7, $"可见文本来源只有 {files.Count} 份 —— 清单缩水了，下面等于空扫");
        foreach (var rel in files)
        {
            string path = Path.Combine(root, rel);
            // 清单点名的档必须真的在 —— 改了名而清单没跟着改，「跳过不存在的档」会让这道门悄悄少扫一份
            Assert.True(File.Exists(path), $"可见文本来源清单点名的「{rel}」不存在 —— 档改名了，清单没跟着改");
            foreach (var (line, text) in Literals(File.ReadAllText(path)))
                foreach (Match m in Regex.Matches(text, CodePattern))
                {
                    // 代号后面（跳过空格与全角空格）是不是一个判据名
                    string rest = text[(m.Index + m.Length)..].TrimStart(' ', '　');
                    if (names.Any(n => rest.StartsWith(n, StringComparison.Ordinal)))
                        bad.Add($"{Path.GetFileName(rel)}:{line}  {m.Value}{rest[..Math.Min(24, rest.Length)]}");
                }
        }

        Assert.True(bad.Count == 0,
            "界面上还有判据代号（工程师看不懂，且与页签上的阶段号形状相同）：\n  "
            + string.Join("\n  ", bad));
    }

    /// <summary>
    /// ★★ 自证之一：这道门**抓得到**它该抓的东西。
    /// 拿一段真的违规文字喂给同一套判定 —— 判不出来的话，上面那条是空转的。
    /// </summary>
    [Fact]
    public void 自证_门抓得到人造的违规()
    {
        var names = PlainNames();
        const string offender = "卡的是 ③ 法兰增量温降，先解决它";
        bool caught = Regex.Matches(offender, CodePattern).Any(m =>
        {
            string rest = offender[(m.Index + m.Length)..].TrimStart(' ', '　');
            return names.Any(n => rest.StartsWith(n, StringComparison.Ordinal));
        });
        Assert.True(caught, "门抓不到「③ 法兰增量温降」—— 那它什么都抓不到");
    }

    /// <summary>
    /// ★★ 自证之二：这道门**不会**误伤列表序号。
    /// 「① 先加舌保温；② 再削薄板厚」这种是引擎给的处置建议，圈号是编号不是代号。
    /// </summary>
    [Fact]
    public void 自证_门不误伤列表序号()
    {
        var names = PlainNames();
        const string fine = "① 先加这一片的舌保温；② 再削薄该片板；③ 都用尽了才动形状";
        bool caught = Regex.Matches(fine, CodePattern).Any(m =>
        {
            string rest = fine[(m.Index + m.Length)..].TrimStart(' ', '　');
            return names.Any(n => rest.StartsWith(n, StringComparison.Ordinal));
        });
        Assert.False(caught, "门把列表序号也当成判据代号了 —— 那它会天天误报，最后被加豁免加到失效");
    }

    /// <summary>
    /// ★★★ 判据全表：<see cref="Criteria.Html"/>（说明书里那张）不许有代号，
    /// 而且**每条判据的全名与单位都要在**。这是用户那句「全换成全名（+单位）」的正面。
    /// </summary>
    [Fact]
    public void 判据全表给的是全名和单位()
    {
        string html = Criteria.Html();
        Assert.True(html.Length > 500, "全表是空的 —— 下面每一条都会恒真");

        foreach (var e in Criteria.All)
        {
            string plain = Criteria.Plain(e.Key);
            Assert.True(html.Contains(plain, StringComparison.Ordinal),
                $"判据全表里查不到「{plain}」—— 工程师在判据表上看到这行字，回表查不到");
            Assert.True(html.Contains(e.Unit, StringComparison.Ordinal),
                $"「{plain}」的单位（{e.Unit}）没印出来");
        }

        // 反面：表里一个代号都不许剩
        foreach (var e in Criteria.All.Where(x => x.Code.Length > 0))
            Assert.False(html.Contains("<b>" + e.Code + "</b>", StringComparison.Ordinal),
                $"判据全表还在印代号 {e.Code}");
    }

    /// <summary>
    /// ★ 命令行那张表（<see cref="Criteria.Table"/>）**照旧带代号** —— 它的读者是开发者。
    /// 这条不是凑数：哪天有人把 UI 的规矩顺手推到命令行，`--glossary` 就查不了代号了，
    /// 而 HANDOVER 与历史记录里到处是代号。
    /// </summary>
    [Fact]
    public void 命令行对照表仍然带代号()
    {
        string t = Criteria.Table();
        Assert.Contains("②′", t);
        Assert.Contains("③", t);
    }

    /// <summary>
    /// ★★★ 2026-09-18，Opus 5（接线复核第 4 条）：**扫描表不许再是手抄的六个档名**。
    ///
    /// 复核查出的实情：工程师看得见的字还写在 <c>Core/FinalCheckReport.cs</c>、<c>Core/InstallReport.cs</c>、
    /// <c>UI/GradeNameEditor.cs</c>、<c>Core/GradeChoices.cs</c> 里 —— 手抄的名单一份都没包含它们。
    ///
    /// 这道门钉三件事：
    /// <code>
    ///   ① 那四份**确实在**清单里（少一份当场红）；
    ///   ② 界面目录是**整层自动扫**的 —— 拿 UI 下一个没被任何地方点名的档来自证
    ///      （手抄清单的形态下它必然缺席）；
    ///   ③ Core/Criteria.cs **故意不在**清单里（命令行那张对照表照旧带代号，读者是开发者）。
    /// </code>
    /// </summary>
    [Fact]
    public void 门_可见文本来源清单不是手抄的六个界面档()
    {
        string root = HandoverDoc.Root();
        var src = VisibleText.Sources(root);

        // ① 复核点名的那四份
        foreach (var must in new[]
        {
            "Pt_Optimize/Core/FinalCheckReport.cs",
            "Pt_Optimize/Core/InstallReport.cs",
            "Pt_Optimize/UI/GradeNameEditor.cs",
            "Pt_Optimize/Core/GradeChoices.cs",
        })
            Assert.True(src.Contains(must, StringComparer.Ordinal),
                $"可见文本来源清单里没有「{must}」—— 工程师看得见的字写在那里，门却扫不到它");

        // ② 界面目录整层自动扫：UI 下的档一个都不许漏，且 CoreSources 没有点过它们的名
        var uiOnDisk = Directory.GetFiles(Path.Combine(root, "Pt_Optimize", "UI"), "*.cs")
                                .Select(f => "Pt_Optimize/UI/" + Path.GetFileName(f)).ToArray();
        Assert.True(uiOnDisk.Length >= 8, $"UI 目录只数出 {uiOnDisk.Length} 个档 —— 这条自证失去了对象");
        foreach (var f in uiOnDisk)
        {
            Assert.True(src.Contains(f, StringComparer.Ordinal), $"界面档「{f}」没进清单");
            Assert.DoesNotContain(f, VisibleText.CoreSources);   // 它是被目录扫到的，不是被点名的
        }

        // ③ 命令行那张对照表的来源**不许**被扫（扫了它，「命令行照旧带代号」那条当场自相矛盾）
        Assert.DoesNotContain("Pt_Optimize/Core/Criteria.cs", src);

        // 自证：清单少一份就该被上面的主门看出来 —— 这里直接验「不存在的档 ⇒ 炸」那一半
        Assert.Throws<DirectoryNotFoundException>(
            () => VisibleText.Sources(Path.Combine(root, "这个目录不存在")));
    }
}
