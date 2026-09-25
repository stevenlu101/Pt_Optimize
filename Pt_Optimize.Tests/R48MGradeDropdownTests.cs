using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M　**牌号下拉：数据不全的灰显不可选** —— 2026-09-18，Opus 5
//
//  用户 2026-09-16：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，
//  只用数据全的铂金合金」；2026-09-17/18 又补了第四类（热导率／比热）并点名可选四个。
//
//  合并树交接时这条记的是「**未接**」：MaterialDb.DataCompleteness 全仓没有调用方，
//  界面牌号下拉列的是材料库全部 12 个牌号 —— 齐全度算得出来，而工程师点得到数据不全的牌号。
//
//  ══ 这几条门守什么（每条都能被「改回去」当场打红）
//
//   ① 可选集合 == 齐全集合。把「查齐全度」改回「一律可选」⇒ 12 ≠ 4 ⇒ 红。
//   ② 齐全集合 == 用户点名的那四个。数据或口径被谁悄悄动了 ⇒ 红（这是决定记录，允许写死）。
//   ③ 下拉列的是**全集**，不是子集 —— 少列一个牌号，工程师无从知道是数据缺还是程序漏。
//   ④ 不可选的每一行都点名缺哪几类，且逐字取自 DataCompleteness.Missing（**门不许手抄生产配方**）。
//   ⑤ 参数表那一项真的挂上了这个下拉（TypeConverter + Editor），默认是纯铂。
//   ⑥ 行末与提示里不许出现判据代号，也不许出现命令行开关名。
// ════════════════════════════════════════════════════════════════════════════

public class R48MGradeDropdownTests
{
    private readonly ITestOutputHelper _o;
    public R48MGradeDropdownTests(ITestOutputHelper o) { _o = o; }

    /// <summary>
    /// **决定记录**（用户 2026-09-17/18 点名）：四类数据齐全、因而可选的牌号恰是这四个。
    /// 写死的是「用户定的那份名单」，不是「程序算出来的名单」—— 两者相等才是这条门的内容。
    /// </summary>
    private static readonly string[] DecidedSelectable =
        { "Pt", "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10" };

    /// <summary>同上：用户点名要灰显的两个（说明写「热导率、比热无实测数据」）。</summary>
    private static readonly string[] DecidedGreyed = { "Pt-Rh/80-20", "Umicore-PtRh20" };

    [Fact]
    public void 可选集合恰是齐全集合_且恰是用户点名的那四个()
    {
        var all = GradeChoices.All();
        var selectable = all.Where(c => c.Selectable).Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var complete = MaterialDb.All.Keys.Where(n => MaterialDb.DataCompleteness(n).IsComplete)
                                          .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        foreach (var c in all)
            _o.WriteLine($"{(c.Selectable ? "可选" : "灰显")}　{c.RowText}");

        // ① 可选 == 齐全（把「查齐全度」改回「一律可选」⇒ 这里当场红）
        Assert.Equal(complete, selectable);

        // ② 齐全 == 用户点名的那四个
        Assert.Equal(DecidedSelectable.OrderBy(x => x, StringComparer.Ordinal).ToArray(), selectable);

        // 用户点名要灰的两个确实是灰的，而且理由正是「热导率与比热无实测数据」
        foreach (string g in DecidedGreyed)
        {
            var c = all.Single(x => x.Name == g);
            Assert.False(c.Selectable, $"{g} 应当灰显");
            Assert.Contains(c.Missing, m => m.StartsWith("热导率与比热", StringComparison.Ordinal));
        }

        // 反自证：如果「可选」这件事不看齐全度，本门必须看得出来
        Assert.True(selectable.Length < all.Length,
            "全部牌号都可选 —— 那就说明齐全度根本没进这条路（材料库里确实有数据不全的牌号）");
    }

    [Fact]
    public void 下拉列全集_不可选的逐项点名缺什么_而且不是手抄的()
    {
        var all = GradeChoices.All();

        // ③ 列的是全集
        Assert.Equal(MaterialDb.All.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                     all.Select(c => c.Name).ToArray());

        foreach (var c in all)
        {
            // ④ 缺什么逐字取自生产那份判定，不是这里抄一遍
            var dc = MaterialDb.DataCompleteness(c.Name);
            Assert.Equal(dc.IsComplete, c.Selectable);
            Assert.Equal(dc.Missing, c.Missing);
            Assert.Equal(dc.Borrowed, c.Borrowed);

            if (c.Selectable) Assert.Equal(c.Name, c.RowText);
            else
            {
                Assert.NotEmpty(c.Missing);
                Assert.Contains("不可选", c.RowText);
                // 行上写缺哪几类（短名，放得下），整句在提示里给全 —— 两处都要**逐条**齐，一条都不许漏
                Assert.Equal(c.Missing.Length, c.MissingShort.Length);
                foreach (var s in c.MissingShort) Assert.Contains(s, c.RowText);
                foreach (var m in c.Missing) Assert.Contains(m, c.Tip);
                Assert.Contains("不能选", c.Tip);
            }
        }
    }

    [Fact]
    public void 参数表那一项真的挂上了下拉与灰显编辑器_默认纯铂()
    {
        var p = TypeDescriptor.GetProperties(typeof(DesignInputs))["GradeName"]!;

        // TypeConverter 只让四个齐全的落到值上
        var conv = p.Converter;
        Assert.True(conv.GetStandardValuesSupported());
        Assert.True(conv.GetStandardValuesExclusive());
        var std = conv.GetStandardValues()!.Cast<string>().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(GradeChoices.Selectable().OrderBy(x => x, StringComparer.Ordinal).ToArray(), std);
        foreach (string g in DecidedGreyed) Assert.False(conv.IsValid(g), $"{g} 不该被接受");
        foreach (string g in DecidedSelectable) Assert.True(conv.IsValid(g), $"{g} 应该被接受");

        // 灰显下拉（UITypeEditor）挂上了 —— 没挂的话 PropertyGrid 只会画一张平铺名单，灰行无从画起
        var ed = p.Attributes.OfType<EditorAttribute>().ToArray();
        Assert.True(ed.Length > 0, "「铂材牌号」上没有 EditorAttribute —— 灰显下拉没接上");
        Assert.Contains(ed, a => a.EditorTypeName.Contains("GradeNameEditor", StringComparison.Ordinal));

        // 默认纯铂（用户 2026-09-15）
        Assert.Equal("Pt", new DesignInputs().GradeName);
        Assert.Equal("Pt", GradeChoices.DefaultGrade);
    }

    [Fact]
    public void 灰显下拉这块控件真的由生产那份工厂造_而且抓得到图()
    {
        // 抓图那条路（UiShot）与界面走的必须是**同一个**工厂方法，不许各造一份列表
        var t = typeof(DesignInputs).Assembly.GetType("PtOptimize.UI.GradeNameEditor");
        Assert.NotNull(t);
        var m = t!.GetMethod("BuildList", BindingFlags.Public | BindingFlags.Static);
        Assert.True(m is not null, "GradeNameEditor.BuildList 不见了 —— 抓图那一张就成了仿造的示意图");

        string root = HandoverDoc.Root();
        string uishot = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "UI", "UiShot.cs"));
        Assert.Contains("GradeNameEditor.BuildList", uishot);

        string editor = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "UI", "GradeNameEditor.cs"));
        // 灰不灰这件事只在 GradeChoices 判，编辑器里不许再判一次（判两遍迟早各判各的）
        Assert.DoesNotContain("MaterialDb.DataCompleteness(", editor);

        // ⑥ 工程师看得见的那几句话里不许有判据代号，也不许有命令行开关名
        var visible = GradeChoices.All().SelectMany(c => new[] { c.RowText, c.Tip })
                                        .Append(GradeChoices.Legend).ToArray();
        foreach (string s in visible)
        {
            Assert.DoesNotContain("--", s);
            foreach (char code in Criteria.CodeChars) Assert.DoesNotContain(code.ToString(), s);
        }
    }
}
