using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// HANDOVER §1.83「每条判据的限值出处」那张**常驻表**必须与代码对得上。
///
/// 为什么值得一整个测试档：这张表的全部用途就是回答
/// 「**哪条判据卡交付、限值是多少、依据在哪**」。它 2026-08-24 被发现有两处错，
/// 而且都在同一行：`管 J` 写着「限值 10 / 判定 参考」，实际是「限值 12 / 硬判据」。
/// 表里还整条缺了 ⑤ 与 ⑥ —— 两条现役硬判据。
///
/// 一张失真的判据表比没有这张表更坏：接手的人会照它判断「这条不过要不要紧」，
/// 而它说「参考」的那条其实会挡住出图。**注释与文档不会自己跑，所以给它配一道门。**
///
/// ⚠ 本档只核**三件可机器核对的事**：判据在不在表里、判定一列对不对、限值数字对不对。
///   「来源」那一列是人话，核不了，也不该由机器核 —— 但缺了来源正是当年那次审计的主题，
///   所以另有一条断言要求它非空。
/// </summary>
public class CriteriaTableTests
{
    private const string SectionAnchor = "## 1.83";

    /// <summary>判定一列的合法取值 —— 与 CheckKind 一一对应。</summary>
    private static string KindWord(CheckKind k) => k switch
    {
        CheckKind.HardSafety => "硬判据",
        CheckKind.Target => "目标",
        _ => "参考",
    };

    /// <summary>
    /// 代码这一侧的真相：判定由 <see cref="LineResult.RequiredByState"/> 决定（K 路 2026-09-15 Opus 5：带工况维，两态各一列）
    /// （名单里没有的判据一律是参考量），限值取自各处的**默认常数**。
    ///
    /// ⚠ 判定**不在本档里另抄一份** —— 那就成了第三处来源。只抄限值，
    ///   而限值本来就只能从代码常数读（这正是抓住「管 J 10 vs 12」的那一环）。
    /// </summary>
    private static (string Key, string Kind, string EmptyKind, double? Limit)[] FromCode()
    {
        var lc = new LineCase();
        var di = new DesignInputs();

        // K 路（2026-09-15，Opus 5）：两态各取一次（名单里没有的一律参考量）
        string KindIn(string key, bool emptyTube) => LineResult.RequiredFor(emptyTube)
            .Where(q => q.Prefix == key)
            .Select(q => KindWord(q.Kind))
            .DefaultIfEmpty("参考")
            .First();
        string KindOf(string key) => KindIn(key, false) + "|" + KindIn(key, true);

        return new (string, string, double?)[]
        {
            // 决 103（2026-09-24）：有意改动 —— 限值换成卡交付的管 J 限值（= min(许用 12, 使用上限 11) = 11）；新热侧、新冷侧两行与原许用值对照行进表；
            //   两条热稳定升为带玻璃稳态硬判据、热偶两条与管孔净流入降为参考（KindOf 读生产分工况表自然给出）。
            //   ⚠ 本档对着 HANDOVER §1.83 核；本路不改 HANDOVER ⇒ §1.83 那张表合并时要照 HANDOVER_决103_节草稿.md 里的「应改成的行」改，否则这几条会红（已列入交付说明）。
            (LineResult.Key.Ramp,       KindOf(LineResult.Key.Ramp),       di.TubeJLimitAPerMm2),   // R20：① = 升温所需电流折成管 J ≤ 许用（决 103：卡交付的管 J 限值）
            (LineResult.Key.HotOverContact,   KindOf(LineResult.Key.HotOverContact),   lc.HotOverContactMaxK),
            (LineResult.Key.TubeToFlangeHeat, KindOf(LineResult.Key.TubeToFlangeHeat), CriteriaRules.TubeToFlangeHeatMaxW),
            (LineResult.Key.TubeJPre103,      KindOf(LineResult.Key.TubeJPre103),      di.TubeJAllowAPerMm2),
            (LineResult.Key.RampHours,  KindOf(LineResult.Key.RampHours),  lc.RampHours),
            (LineResult.Key.NetFlux,    KindOf(LineResult.Key.NetFlux),    0.0),
            // R48 B（2026-09-14 Opus 5）：有意改动 —— 热侧／冷侧换成热偶读数基准的两条新硬判据（限值 = 热偶误差，LineCase），旧判法两条降为参考量（KindOf 自然给「参考」）。
            //   ⚠ 本档对着 HANDOVER §1.83 核；本路不许改 HANDOVER ⇒ §1.83 那张表合并时要照交接说明补两行、改两行，否则这几条会红（已在交付说明里列出）。
            (LineResult.Key.HotOverTc,   KindOf(LineResult.Key.HotOverTc),   lc.HotOverTcMaxK),
            (LineResult.Key.ColdUnderTc, KindOf(LineResult.Key.ColdUnderTc), lc.ColdUnderTcMaxK),
            (LineResult.Key.DiscTemp,   KindOf(LineResult.Key.DiscTemp),   lc.DiscOverTempMaxK),
            (LineResult.Key.FlangeDip,  KindOf(LineResult.Key.FlangeDip),  lc.RootDeltaMaxK),
            (LineResult.Key.FreeTab,    KindOf(LineResult.Key.FreeTab),    GeometryScreen.FreeTabMinDefaultMm),
            (LineResult.Key.DiscCover,  KindOf(LineResult.Key.DiscCover),  0.0),
            (LineResult.Key.TubeJ,      KindOf(LineResult.Key.TubeJ),      di.TubeJLimitAPerMm2),   // 决 103：11（与原许用 12 取小）
            (LineResult.Key.FlangeStab, KindOf(LineResult.Key.FlangeStab), 1.0),
            (LineResult.Key.LocalStab,  KindOf(LineResult.Key.LocalStab),  1.0),
            // 现场升温那条（2026-08-25 新增）。215 是**现役基准**不是通过线，
            // 但它一样要与代码对得上 —— 基准漂了而文档没跟上，同样会误导。
            (LineResult.Key.RampField,  KindOf(LineResult.Key.RampField),  215.0),
        }.Select(x => (x.Item1, x.Item2.Split('|')[0], x.Item2.Split('|')[1], x.Item3)).ToArray();
    }

    private sealed record Row(string Name, string Limit, string Kind, string Source, string EmptyKind);

    /// <summary>把 §1.83 那张表读成行。</summary>
    private static Row[] ReadTable()
    {
        string text = HandoverDoc.Text().Replace("\r\n", "\n");
        int i = text.IndexOf(SectionAnchor, StringComparison.Ordinal);
        Assert.True(i >= 0, $"HANDOVER 里找不到「{SectionAnchor}」这一节 —— 断言失去了对象");

        // 到下一个同级或更高级标题为止
        int j = text.IndexOf("\n### ", i, StringComparison.Ordinal);
        int k = text.IndexOf("\n## ", i + SectionAnchor.Length, StringComparison.Ordinal);
        int end = new[] { j, k }.Where(x => x > 0).DefaultIfEmpty(text.Length).Min();

        var rows = new List<Row>();
        foreach (string raw in text[i..end].Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("|", StringComparison.Ordinal)) continue;
            // 表头与分隔行不要
            if (line.Contains("---", StringComparison.Ordinal)) continue;
            var cells = SplitCells(line);
            if (cells.Length < 4) continue;
            if (cells[0] == "判据") continue;
            rows.Add(new Row(cells[0], cells[1], cells[2], cells[3], cells.Length > 4 ? cells[4] : ""));   // K 路（2026-09-15 Opus 5）：第五列 = 空管到温稳态
        }
        return rows.ToArray();
    }

    /// <summary>
    /// 按**未转义**的竖线切格：GFM 表格里的字面竖线写成反斜线加竖线，那不是分隔符。
    /// ⚠ 不用正则：本仓的写档钩子会把源码里的转义序列吃掉 ——
    ///   我第一版写的 lookbehind 落到档案里少了一个反斜线，**编译得过、运行时才炸**
    ///   （Invalid pattern，20 条全红）。反斜线一律用 (char)92 表示。
    /// </summary>
    private static string[] SplitCells(string line)
    {
        const char BS = (char)92;
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == BS && i + 1 < line.Length && line[i + 1] == '|') { cur.Append('|'); i++; continue; }
            if (line[i] == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(line[i]);
        }
        cells.Add(cur.ToString().Trim());
        // 行以 | 开头也以 | 结尾 ⇒ 首尾各多出一个空格
        if (cells.Count >= 2) { cells.RemoveAt(cells.Count - 1); cells.RemoveAt(0); }
        return cells.ToArray();
    }

    private static double? FirstNumber(string cell)
    {
        var m = Regex.Match(cell, @"-?\d+(\.\d+)?");
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    public static IEnumerable<object[]> CodeSide() => FromCode().Select(x => new object[] { x.Key });

    /// <summary>自证：表读得出来、行数合理、判定一列只出现约定的四个词。</summary>
    [Fact]
    public void Table_IsParseable_AndWellFormed()
    {
        var rows = ReadTable();
        // 空集通过是本项目反复栽的跟头：一行都没读到不是「没问题」
        Assert.True(rows.Length >= 12,
            $"只从 §1.83 读到 {rows.Length} 行 —— 要么表被改了写法，要么解析坏了，两种都得响");

        string[] legal = { "硬判据", "目标", "参考", "校准" };
        Assert.All(rows, r => Assert.True(
            legal.Any(w => r.Kind.Contains(w, StringComparison.Ordinal)),
            $"「{r.Name}」的判定一列写着「{r.Kind}」—— 只准填 {string.Join(" / ", legal)}"));
        // K 路（2026-09-15，Opus 5）：空管到温稳态一列同样只准填这四个词（每行都要有，不许空着）
        Assert.All(rows, r => Assert.True(
            legal.Any(w => r.EmptyKind.Contains(w, StringComparison.Ordinal)),
            $"「{r.Name}」的「空管到温稳态」一列写着「{r.EmptyKind}」—— 只准填 {string.Join(" / ", legal)}"));

        // 当年那次审计的主题就是「每条判据的限值必须有来源」
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Source),
            $"「{r.Name}」没写来源 —— §1.83 立的规矩就是这一条"));
    }

    /// <summary>每条判据都必须在表上，且**判定**一列与代码一致。</summary>
    [Theory]
    [MemberData(nameof(CodeSide))]
    public void EveryCriterion_IsListed_WithMatchingKind(string key)
    {
        var want = FromCode().First(x => x.Key == key);
        var rows = ReadTable();
        var hit = rows.Where(r => r.Name.Contains(key, StringComparison.Ordinal)).ToArray();

        Assert.True(hit.Length == 1,
            hit.Length == 0
                ? $"§1.83 的表里找不到「{key}」—— 判据在代码里跑着，表上却没有它"
                : $"「{key}」在表里出现了 {hit.Length} 次，无法确定以哪一行为准");

        Assert.True(hit[0].Kind.Contains(want.Kind, StringComparison.Ordinal),
            $"「{key}」：代码里是 **{want.Kind}**，表上写的是「{hit[0].Kind}」。"
            + (want.Kind == "参考"
               ? "　参考量不进 AllOk。"
               : "　这一条**卡交付**，表上说成参考会让人以为不过也能出图。"));
        // K 路（2026-09-15，Opus 5）：空管到温稳态一列按分工况表核；「参考」与「硬判据」互斥（写了硬判据的格子不许同时出现参考字样，反之亦然）
        string other = want.EmptyKind == "参考" ? "硬判据" : "参考";
        Assert.True(hit[0].EmptyKind.Contains(want.EmptyKind, StringComparison.Ordinal) && !hit[0].EmptyKind.Contains(other, StringComparison.Ordinal),
            $"「{key}」空管到温稳态：代码里是 **{want.EmptyKind}**，表上写的是「{hit[0].EmptyKind}」。");
    }

    /// <summary>限值一列的数字也必须与代码常数一致 —— 「管 J 写 10 而实际 12」就是这么漏的。</summary>
    [Theory]
    [MemberData(nameof(CodeSide))]
    public void EveryCriterion_IsListed_WithMatchingLimit(string key)
    {
        var want = FromCode().First(x => x.Key == key);
        if (want.Limit is null) return;                    // 没有数值限值的不核
        var row = ReadTable().Single(r => r.Name.Contains(key, StringComparison.Ordinal));
        double? got = FirstNumber(row.Limit);

        Assert.True(got is not null,
            $"「{key}」的限值一列「{row.Limit}」里读不出数字");
        Assert.True(Math.Abs(got!.Value - want.Limit!.Value) < 1e-9,
            $"「{key}」：代码里限值是 {want.Limit}，表上写的是 {got}（「{row.Limit}」）。"
            + "限值漂了而文档没跟上，接手的人会照错的那个判断裕度够不够");
    }

    /// <summary>
    /// 反方向：表上标成「硬判据」的，代码里必须真的是硬安全线。
    /// 否则文档可以凭空发明一条卡交付的判据，而没有任何东西会拦它。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoPhantomHardCriteria_InTheDoc(bool emptyTube)
    {
        string[] hardKeys = LineResult.RequiredFor(emptyTube)
            .Where(q => q.Kind == CheckKind.HardSafety).Select(q => q.Prefix).ToArray();
        Assert.NotEmpty(hardKeys);                          // 自证

        var claimed = ReadTable()
            .Where(r => (emptyTube ? r.EmptyKind : r.Kind).Contains("硬判据", StringComparison.Ordinal)).ToArray();
        Assert.True(claimed.Length == hardKeys.Length,
            $"表上标「硬判据」的有 {claimed.Length} 行，代码里的硬安全线有 {hardKeys.Length} 条");

        Assert.All(claimed, r => Assert.True(
            hardKeys.Any(k => r.Name.Contains(k, StringComparison.Ordinal)),
            $"表上「{r.Name}」标成硬判据，但代码里没有这条硬安全线 —— 文档不能凭空发明门槛"));
    }
}
