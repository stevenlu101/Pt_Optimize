using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PtOptimize.Tests;

/// <summary>
/// 读 xlsx 的最小工具（只用 BCL：System.IO.Compression + System.Xml.Linq，不加 NuGet）。2026-09-15，Opus 5。
///
/// 为什么门要自己读 xlsx：物性数据的门若把数字手抄进测试，手抄的门守不住手抄的病 ——
/// 旧 MaterialDbTests 就手抄漏了纯铂 10000 h 一列与 ZGS-PtRh10 两行。
///
/// ⚠ 口径（数值把关 2026-09-15）：
///   · 数值只认没有 t 属性或 t="n" 的格；遇到 t="e"/"s"/"str" 取数直接失败；
///   · double.Parse 用 NumberStyles.Float + InvariantCulture（.NET Core 3.0 起正确舍入，可逐位比）；
///   · 共享公式只有主格带公式文本，但每格都有 &lt;f&gt; 元素与缓存值 —— HasFormula 按 &lt;f&gt; 元素判；
///   · 共享字符串拼接 &lt;si&gt; 下全部 &lt;t&gt;（含富文本 &lt;r&gt;），排除注音 &lt;rPh&gt;；
///   · 找不到工作簿**必须失败**，不许跳过。
/// </summary>
internal sealed class XlsxBook : IDisposable
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive _zip;
    private readonly List<string> _shared = new();
    public string FilePath { get; }

    private XlsxBook(string path)
    {
        FilePath = path;
        _zip = ZipFile.OpenRead(path);
        var ss = _zip.GetEntry("xl/sharedStrings.xml");
        if (ss is not null)
        {
            var doc = Load(ss);
            foreach (var si in doc.Root!.Elements(S + "si"))
                _shared.Add(string.Concat(si.Descendants(S + "t")
                    .Where(t => t.Ancestors(S + "rPh").FirstOrDefault() is null)
                    .Select(t => t.Value)));
        }
    }

    /// <summary>从仓库根打开工作簿；不在 ⇒ 抛出（门失败，不跳过）。</summary>
    public static XlsxBook OpenInRepo(string fileName)
    {
        string p = Path.Combine(HandoverDoc.Root(), fileName);
        if (!File.Exists(p))
            throw new FileNotFoundException($"物性工作簿「{fileName}」不在仓库根 —— 门失去了对象，不能算通过", p);
        return new XlsxBook(p);
    }

    private static XDocument Load(ZipArchiveEntry e) { using var s = e.Open(); return XDocument.Load(s); }

    public XDocument Part(string path)
        => Load(_zip.GetEntry(path) ?? throw new FileNotFoundException($"{Path.GetFileName(FilePath)} 里没有 {path}"));

    public bool HasPart(string path) => _zip.GetEntry(path) is not null;

    /// <summary>包里全部部件路径（门登记外部链接用）。2026-09-15 第二轮，Opus 5。</summary>
    public IReadOnlyList<string> PartNames => _zip.Entries.Select(e => e.FullName).ToList();

    public string SharedString(int i) => _shared[i];

    public XlsxSheet Sheet(string name)
    {
        var wb = Part("xl/workbook.xml");
        var sh = wb.Root!.Element(S + "sheets")!.Elements(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == name)
            ?? throw new KeyNotFoundException($"{Path.GetFileName(FilePath)} 里没有工作表「{name}」");
        string rid = (string)sh.Attribute(R + "id")!;
        var rels = Part("xl/_rels/workbook.xml.rels");
        string target = (string)rels.Root!.Elements(PR + "Relationship").Single(x => (string?)x.Attribute("Id") == rid).Attribute("Target")!;
        string path = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
        return new XlsxSheet(this, name, Part(path));
    }

    public void Dispose() => _zip.Dispose();
}

internal sealed record XlsxCell(string Ref, string? Type, string? RawValue, bool HasFormula, string? FormulaText);

internal sealed class XlsxSheet
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly XlsxBook _book;
    public string Name { get; }
    public IReadOnlyDictionary<string, XlsxCell> Cells { get; }

    public XlsxSheet(XlsxBook book, string name, XDocument doc)
    {
        _book = book; Name = name;
        var d = new Dictionary<string, XlsxCell>(StringComparer.Ordinal);
        foreach (var c in doc.Descendants(S + "c"))
        {
            string r = (string)c.Attribute("r")!;
            var f = c.Element(S + "f");
            string? ftext = f is null || string.IsNullOrEmpty(f.Value) ? null : f.Value;
            string? v = c.Element(S + "v")?.Value;
            if (v is null && c.Attribute("t")?.Value == "inlineStr")
                v = string.Concat(c.Descendants(S + "t").Select(t => t.Value));
            d[r] = new XlsxCell(r, (string?)c.Attribute("t"), v, f is not null, ftext);
        }
        Cells = d;
    }

    /// <summary>
    /// 取格子（不存在 ⇒ null）。⚠ **有公式却没有缓存值（有 &lt;f&gt; 无 &lt;v&gt;）直接抛**（2026-09-15 第二轮，Opus 5）：
    /// 那种格子不是「空格」—— 当空格读，门会把它当缺测放过，<c>CreepXlsxRaw</c> 会在那里截断整行。
    /// 三份工作簿现在没有这种格（第二轮扫过），出现了就是工作簿被改过、没重算。
    /// </summary>
    public XlsxCell? Cell(string r)
    {
        if (!Cells.TryGetValue(r, out var c)) return null;
        if (c.HasFormula && c.RawValue is null)
            throw new InvalidOperationException($"{Name}!{r} 有公式却没有缓存值（没重算过？）—— 不许当空格读");
        return c;
    }

    /// <summary>格子空（不存在或没有值；有公式无缓存值的格抛出，不算空）。</summary>
    public bool IsBlank(string r) => Cell(r)?.RawValue is null;

    /// <summary>数值格的存储值。只认无 t 或 t="n"；别的类型直接失败。</summary>
    public double Num(string r)
    {
        var c = Cell(r) ?? throw new InvalidOperationException($"{Name}!{r} 不存在");
        if (c.RawValue is null) throw new InvalidOperationException($"{Name}!{r} 是空格，不是数");
        if (c.Type is not (null or "n"))
            throw new InvalidOperationException($"{Name}!{r} 类型 t=\"{c.Type}\"，不是数值格");
        return double.Parse(c.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>字符串格的文本（共享字符串拼接全部 &lt;t&gt;）。</summary>
    public string Str(string r)
    {
        var c = Cell(r) ?? throw new InvalidOperationException($"{Name}!{r} 不存在");
        return c.Type switch
        {
            "s" => _book.SharedString(int.Parse(c.RawValue!, CultureInfo.InvariantCulture)),
            "str" or "inlineStr" => c.RawValue ?? "",
            _ => throw new InvalidOperationException($"{Name}!{r} 类型 t=\"{c.Type}\"，不是字符串格"),
        };
    }

    /// <summary>公式文本（XML 实体已解码）。无公式或共享公式子格 ⇒ null。</summary>
    public string? Formula(string r) => Cell(r)?.FormulaText;
}

/// <summary>图表系列（含 Excel 2013+ 的隐藏系列 c15:filteredScatterSeries）。2026-09-15，Opus 5。</summary>
internal sealed record ChartSeries(
    int Idx, bool Filtered, string NameRef, string XRef, string YRef,
    double[] XCache, double[] YCache, int XCount, int YCount,
    string? TrendType, int? TrendOrder, bool DispEq, bool DispRSqr, bool HasIntercept, string? LabelNumFmt, string LabelText);

internal static class XlsxChart
{
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace C15 = "http://schemas.microsoft.com/office/drawing/2012/chart";

    public static List<ChartSeries> Series(XDocument chart)
    {
        var list = new List<ChartSeries>();
        foreach (var ser in chart.Descendants().Where(e => e.Name.LocalName == "ser" && (e.Name.Namespace == C || e.Name.Namespace == C15)))
        {
            bool filtered = ser.Name.Namespace == C15;
            int idx = int.Parse((string)ser.Element(C + "idx")!.Attribute("val")!, CultureInfo.InvariantCulture);
            string Ref(XElement? parent)
            {
                if (parent is null) return "";
                var f = parent.Descendants(C + "f").FirstOrDefault();
                if (f is not null) return f.Value;
                var sq = parent.Descendants(C15 + "sqref").FirstOrDefault();
                return sq?.Value ?? "";
            }
            (double[] vals, int count) Cache(XElement? parent)
            {
                var nc = parent?.Descendants(C + "numCache").FirstOrDefault();
                if (nc is null) return (Array.Empty<double>(), 0);
                int n = int.Parse((string)nc.Element(C + "ptCount")!.Attribute("val")!, CultureInfo.InvariantCulture);
                var arr = Enumerable.Repeat(double.NaN, n).ToArray();
                foreach (var pt in nc.Elements(C + "pt"))
                    arr[int.Parse((string)pt.Attribute("idx")!, CultureInfo.InvariantCulture)]
                        = double.Parse(pt.Element(C + "v")!.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return (arr, n);
            }
            var tl = ser.Element(C + "trendline");
            var (xc, xn) = Cache(ser.Element(C + "xVal"));
            var (yc, yn) = Cache(ser.Element(C + "yVal"));
            string label = tl is null ? "" : string.Concat(tl.Descendants(A + "t").Select(t => t.Value));
            list.Add(new ChartSeries(idx, filtered, Ref(ser.Element(C + "tx")), Ref(ser.Element(C + "xVal")), Ref(ser.Element(C + "yVal")),
                xc, yc, xn, yn,
                (string?)tl?.Element(C + "trendlineType")?.Attribute("val"),
                tl?.Element(C + "order") is { } o ? int.Parse((string)o.Attribute("val")!, CultureInfo.InvariantCulture) : null,
                (string?)tl?.Element(C + "dispEq")?.Attribute("val") == "1",
                (string?)tl?.Element(C + "dispRSqr")?.Attribute("val") == "1",
                tl?.Element(C + "intercept") is not null,
                (string?)tl?.Descendants(C + "numFmt").FirstOrDefault()?.Attribute("formatCode"),
                label));
        }
        return list;
    }

    /// <summary>
    /// 把「表!$B$5:$B$19」或并集「(表!$B$9,表!$B$14,…)」展开成格子名（按引用顺序）。
    /// 返回 (表名, 格子)；多个表名 ⇒ 抛出。
    /// </summary>
    public static (string Sheet, List<string> Cells) Expand(string reference)
    {
        string s = reference.Trim();
        if (s.StartsWith("(") && s.EndsWith(")")) s = s[1..^1];
        string? sheet = null;
        var cells = new List<string>();
        foreach (string part in s.Split(','))
        {
            int bang = part.LastIndexOf('!');
            string sh = part[..bang].Trim('\'');
            if (sheet is not null && sheet != sh) throw new InvalidOperationException($"引用跨表：{reference}");
            sheet = sh;
            string rng = part[(bang + 1)..].Replace("$", "");
            var m = Regex.Match(rng, @"^([A-Z]+)(\d+)(?::([A-Z]+)(\d+))?$");
            if (!m.Success) throw new InvalidOperationException($"看不懂的引用：{reference}");
            if (!m.Groups[3].Success) { cells.Add(rng); continue; }
            if (m.Groups[1].Value != m.Groups[3].Value) throw new InvalidOperationException($"只支持单列区域：{reference}");
            int r0 = int.Parse(m.Groups[2].Value), r1 = int.Parse(m.Groups[4].Value);
            for (int r = r0; r <= r1; r++) cells.Add(m.Groups[1].Value + r);
        }
        return (sheet ?? "", cells);
    }
}
