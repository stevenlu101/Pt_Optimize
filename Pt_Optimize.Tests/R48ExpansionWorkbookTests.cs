using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 热膨胀按牌号温度函数的门：**每次直接读《鉑金热膨胀计算.xlsx》**核对代码里的每个数与算法（不手抄）。
/// 2026-09-15，Opus 5。
///
/// 容差全部是**改前**由两位把关人写死的（不许事后挪）：
///   · 代码常数 vs 单元格／图表存储值：逐位；α 列缓存：逐位；Y、Z 用缓存 X 复现：逐位；
///   · X14:X16 缓存：≤ 4ε·Σ|项|；
///   · 趋势线系数：复算精确解按「0.000000E+00」取 7 位后与标签文本逐字相同；R²：|Δ| ≤ 1e-7
///     （PtRh20、Rh 的标签与精确值末位不一致：精确值 …2039、…6547，按舍入应显示 …204、…547，文本却是 …203、…546，原因不明）；
///   · 纯铂函数与 LT/L0 表的应变差 ≤ 60 µε（其余合金只报不断言）；
///   · 小表 U4／U5 与 X14：500 °C ±0.10、1000 °C ±0.05（4 位小数 LT/L0 对 α 的分辨率）。
///
/// 2026-09-15 第二轮（Opus 5，主会话口径 D1/D3/D4 与审查汇总阻断 2、3）：
///   · 纯铂 1000 °C 以上所有量一律「外推」，与 ZGSPt 的比较只打印诊断数、不断言；
///   · 0–100 °C 定义点内插；形状不可信只看瞬时 α 下降；应变差覆盖取两端较差；
///   · 映射门逐条断言「牌号 → 曲线 → 直接／借用」；按牌号的 HotLength／Elongation／LengthRatio 复现 X、Y、Z 缓存。
/// 2026-09-15 第三轮（Opus 5，复核阻断 1）：映射门对七个按牌号函数的值逐位核「决定记录那条曲线」的值（0–100 °C 在内）；
///   曲线层 At 函数 100 °C 起等于裸多项式、裸平均 α／应变对精确有理数核 —— 映射门拿曲线层值当期望，曲线层自己必须另有门。
/// 2026-09-15 第四轮（Opus 5，两位复核独立指出的同一条阻断）：第三轮映射门与应变差门的**覆盖期望**直接调生产
///   ExpansionCurve.Classify（拿生产比生产），注入 J1（ZGS 数据上端 +100 K）、J2（删数据下端端部间隔）、J4（100 °C 进定义点内插）、
///   R1b（2 阶曲线上端外标区间内）、R1c（端部间隔严格不等）全绿。改成门自己按定义算（<see cref="ExpansionCoverageSpec"/>，
///   不调任何生产分档函数）：9 条曲线 × 导数／非导数 × 0–1500 °C 每 0.5 K 全网格比对，分段端点对 HANDOVER 覆盖表的决定记录；
///   映射门与应变差门的覆盖期望一律取它。另补：瞬时 α 100 °C 台阶量级与注记、单点函数「值 NaN ⇔ 无数据」含 L0 非有限数。
/// </summary>
public class R48ExpansionWorkbookTests
{
    private readonly ITestOutputHelper _o;
    public R48ExpansionWorkbookTests(ITestOutputHelper o) => _o = o;

    private static bool Bit(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static string Norm(string s) => Regex.Replace(s, @"\s+", "");

    private static XlsxBook Book() => XlsxBook.OpenInRepo(PtThermalExpansion.WorkbookFile);

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 膨胀表逐格与代码逐位相同_写0的格在代码里是缺测_α列公式照工作簿()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var T = PtThermalExpansion.TableTempsC;
        for (int i = 0; i < T.Length; i++)
            Assert.True(Bit(sh.Num("B" + (4 + i)), T[i]), $"B{4 + i} 温度与代码不同");

        int missing = 0, data = 0;
        foreach (var (alloy, lt, al, vals) in PtThermalExpansion.Table)
        {
            Assert.Equal(alloy + "(LT/L0)", Norm(sh.Str(lt + "3")));
            Assert.Equal(alloy + "(α)", Norm(sh.Str(al + "3")));
            Assert.True(vals.Length == T.Length, $"{alloy} 列长度与 B4:B19 不等");
            int formulas = 0;
            for (int i = 0; i < T.Length; i++)
            {
                int r = 4 + i;
                double cell = sh.Num(lt + r), code = vals[i];
                var aCell = sh.Cell(al + r)!;
                Assert.True(aCell.HasFormula, $"{al}{r} 应是 α 公式格");
                if (aCell.FormulaText is { } f)
                {
                    formulas++;
                    Assert.Equal($"IF(AND({lt}{r}>=1,$B{r}>0),({lt}{r}-1)/$B{r}*1000000,0)", f);
                }
                double aCache = sh.Num(al + r);
                if (cell == 0)
                {
                    missing++;
                    Assert.True(double.IsNaN(code), $"{lt}{r} 工作簿写 0（无数据），代码必须是缺测 NaN，却是 {code}");
                    Assert.True(aCache == 0, $"{al}{r} 无数据格 α 公式应给 0");
                    Assert.True(double.IsNaN(PtThermalExpansion.TableAlphaE6(T[i], code)), "无数据格代码 α 必须 NaN，不许给 0");
                }
                else
                {
                    data++;
                    Assert.True(Bit(cell, code), $"{lt}{r}：xlsx {cell:R} ≠ 代码 {code:R}");
                    double codeA = PtThermalExpansion.TableAlphaE6(T[i], code);
                    if (T[i] > 0) Assert.True(Bit(aCache, codeA), $"{al}{r}：α 缓存 {aCache:R} ≠ 代码按公式 {codeA:R}");
                    else Assert.True(aCache == 0 && double.IsNaN(codeA), $"{al}{r}：0 °C 行 α 不是数据");
                }
            }
            Assert.True(formulas > 0, $"{al} 列一个公式文本都没读到 —— 共享公式解析坏了");
        }
        _o.WriteLine($"LT/L0 表：{data} 格有数，{missing} 格工作簿写 0（= 无数据，代码为 NaN）");
        Assert.True(missing > 0 && data > 0, "反自证：缺测格与数据格都应存在，否则这条门在空转");
    }

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ZGS两列与右上小表逐格与代码相同_小表字符串按格式解析()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        Assert.Equal("ZGSPt", Norm(sh.Str("Q3")));
        Assert.Equal("ZGSPtRh10%", Norm(sh.Str("R3")));
        int[] rows = { 9, 14, 16, 17 };
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.True(Bit(sh.Num("B" + rows[i]), PtThermalExpansion.ZgsTempsC[i]));
            Assert.True(Bit(sh.Num("Q" + rows[i]), PtThermalExpansion.ZgsPtAlphaE6[i]), $"Q{rows[i]} 与代码不同");
            Assert.True(Bit(sh.Num("R" + rows[i]), PtThermalExpansion.ZgsPtRh10AlphaE6[i]), $"R{rows[i]} 与代码不同");
        }
        for (int r = 4; r <= 19; r++)
            if (!rows.Contains(r))
                Assert.True(sh.IsBlank("Q" + r) && sh.IsBlank("R" + r), $"Q{r}/R{r} 有代码没登记的数");

        var re = new Regex(@"^(\d+(?:\.\d+)?)×10-6$");
        for (int i = 0; i < PtThermalExpansion.SideTableTempsC.Length; i++)
            Assert.True(Bit(sh.Num("T" + (4 + i)), PtThermalExpansion.SideTableTempsC[i]));
        foreach (var (header, col, vals) in PtThermalExpansion.SideTable)
        {
            Assert.Equal(header, Norm(sh.Str(col + "3")));
            for (int i = 0; i < vals.Length; i++)
            {
                string s = Norm(sh.Str(col + (4 + i)));
                if (s == "-") { Assert.True(double.IsNaN(vals[i]), $"{col}{4 + i} 是「-」（无数据），代码必须 NaN"); continue; }
                var m = re.Match(s);
                Assert.True(m.Success, $"{col}{4 + i}「{s}」不是「数×10-6」格式");
                Assert.True(Bit(double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture), vals[i]),
                    $"{col}{4 + i}「{s}」与代码 {vals[i]} 不同");
            }
        }

        // 登记（2026-09-15 第二轮，Opus 5）：ZGSPtAu5 小表 Y 列对 PtAu5 LT/L0 表推出的平均 α —— 只报比值，不断言
        var y = PtThermalExpansion.SideTable.Single(s => s.Col == "Y");
        var au = PtThermalExpansion.Table.Single(t => t.Alloy == "PtAu5");
        for (int i = 0; i < y.AlphaE6.Length; i++)
        {
            double t = PtThermalExpansion.SideTableTempsC[i];
            int k = Array.IndexOf(PtThermalExpansion.TableTempsC, t);
            double tabA = PtThermalExpansion.TableAlphaE6(t, au.LtOverL0[k]);
            _o.WriteLine($"ZGSPtAu5 Y{4 + i}（{t:0} °C）= {y.AlphaE6[i]}，PtAu5 表 α = {tabA:0.000}，高 {(y.AlphaE6[i] / tabA - 1) * 100:+0.0;-0.0} %（登记，不断言）");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// X14:X16 公式字面量＝纯铂曲线系数；X 缓存 ≤ 4ε·Σ|项|；Y、Z 用缓存 X 逐位复现；
    /// 2026-09-15 第二轮（阻断 3②）：再用**按牌号**的 LengthRatio("Pt")、HotLength("Pt")、Elongation("Pt") 逐位复现
    /// X、Y、Z 缓存（第一轮只走 HotLengthFromAlpha，按牌号的三个函数改错门也不红）。
    /// </summary>
    [Fact]
    public void 纯铂曲线系数等于X14到X16公式字面量_X列缓存与Y和Z公式复现_按牌号函数也复现()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var pt = PtThermalExpansion.Curves["Pt"];
        Assert.Equal(ExpansionCoeffOrigin.CellFormula, pt.Origin);
        foreach (int r in new[] { 14, 15, 16 })
        {
            string f = Norm(sh.Formula("X" + r) ?? throw new InvalidOperationException($"X{r} 没有公式"));
            var m = Regex.Match(f, $@"^([0-9.]+)\*U{r}\^3-([0-9.]+)\*U{r}\^2\+([0-9.]+)\*U{r}\+([0-9.]+)$");
            Assert.True(m.Success, $"X{r} 公式形状变了：{f}");
            double P(int g) => double.Parse(m.Groups[g].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            double[] lit = { P(4), P(3), -P(2), P(1) };   // c0..c3
            Assert.Equal(3, pt.Order);
            for (int k = 0; k <= 3; k++)
                Assert.True(Bit(lit[k], pt.Coeffs[k]), $"X{r} 字面量 c{k} = {lit[k]:R} ≠ 代码 {pt.Coeffs[k]:R}");

            double u = sh.Num("U" + r), x = sh.Num("X" + r);
            double code = pt.RawMeanAlphaE6(u);
            double bound = 4 * 2.220446049250313e-16 * Enumerable.Range(0, 4).Sum(k => Math.Abs(pt.Coeffs[k] * Math.Pow(u, k)));
            Assert.True(Math.Abs(code - x) <= bound, $"X{r}：代码 {code:R} 与缓存 {x:R} 差 {Math.Abs(code - x):E2} > 4ε·Σ|项| = {bound:E2}");

            Assert.Equal($"V{r}*(1+X{r}*0.000001*U{r})", Norm(sh.Formula("Y" + r)!));
            Assert.Equal($"Y{r}-V{r}", Norm(sh.Formula("Z" + r)!));
            double v = sh.Num("V" + r), y = sh.Num("Y" + r), z = sh.Num("Z" + r);
            double yc = PtThermalExpansion.HotLengthFromAlpha(v, x, u);
            Assert.True(Bit(yc, y), $"Y{r}：代码 {yc:R} ≠ 缓存 {y:R}");
            Assert.True(Bit(yc - v, z), $"Z{r}：代码 {yc - v:R} ≠ 缓存 {z:R}");

            // 按牌号的函数（设计链将来调的就是它们）
            var ma = PtThermalExpansion.MeanAlphaE6("Pt", u);
            var lr = PtThermalExpansion.LengthRatio("Pt", u);
            var hl = PtThermalExpansion.HotLength("Pt", v, u);
            var el = PtThermalExpansion.Elongation("Pt", v, u);
            foreach (var e in new[] { ma, lr, hl, el })
                Assert.True(e.HasValue && e.Curve == "Pt" && e.Link == ExpansionLink.Direct, e.Note);
            Assert.True(Bit(ma.Value, x), $"MeanAlphaE6(\"Pt\", {u})：{ma.Value:R} ≠ X{r} 缓存 {x:R}");
            Assert.True(Bit(lr.Value, 1 + x * 0.000001 * u), $"LengthRatio(\"Pt\", {u})：{lr.Value:R} ≠ 1 + X{r}·1e-6·U{r} = {1 + x * 0.000001 * u:R}");
            Assert.True(Bit(hl.Value, y), $"HotLength(\"Pt\", V{r}, U{r})：{hl.Value:R} ≠ Y{r} 缓存 {y:R}");
            Assert.True(Bit(el.Value, z), $"Elongation(\"Pt\", V{r}, U{r})：{el.Value:R} ≠ Z{r} 缓存 {z:R}");
            _o.WriteLine($"X{r}：U = {u} °C，α 代码 {code:R} / 缓存 {x:R}（逐位{(Bit(code, x) ? "相同" : "不同")}）；"
                       + $"按牌号：LT/L0 {lr.Value:R}、LT {hl.Value:R} = Y{r}、ΔL {el.Value:R} = Z{r}（{el.Coverage}）");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>图表 9 条系列：引用从图表 XML 读（不手写区间），缓存 == 单元格，拟合 y 里没有缺测 0，代码数据点与阶数照它。</summary>
    [Fact]
    public void 图表趋势线引用与缓存等于单元格_代码曲线的数据点与阶数照图表()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var series = XlsxChart.Series(book.Part("xl/charts/chart1.xml"));
        Assert.NotEmpty(series);
        var seen = new HashSet<string>();
        foreach (var s in series)
        {
            var (ns, nc) = XlsxChart.Expand(s.NameRef);
            var (xs, xc) = XlsxChart.Expand(s.XRef);
            var (ys, yc) = XlsxChart.Expand(s.YRef);
            Assert.True(ns == PtThermalExpansion.SheetName && xs == ns && ys == ns, $"系列 {s.Idx} 引用了别的表");
            string id = Regex.Replace(Norm(sh.Str(nc.Single())), @"\(α\)$|%$", "");
            Assert.True(PtThermalExpansion.Curves.TryGetValue(id, out var curve), $"图表系列 {s.Idx}「{id}」在代码里没有曲线");
            Assert.True(seen.Add(id), $"两条图表系列指向同一曲线 {id}");

            Assert.Equal(xc.Count, s.XCount);
            Assert.Equal(yc.Count, s.YCount);
            Assert.True(xc.Count >= yc.Count, $"{id}：x 少于 y");
            int n = yc.Count;
            Assert.True(curve!.DataTempsC.Count == n, $"{id}：代码数据点 {curve.DataTempsC.Count} 个，图表 y 引用 {n} 个");
            for (int i = 0; i < n; i++)
            {
                double xv = sh.Num(xc[i]), yv = sh.Num(yc[i]);
                Assert.True(Bit(s.XCache[i], xv), $"{id} x 缓存 {i} ≠ {xc[i]}");
                Assert.True(Bit(s.YCache[i], yv), $"{id} y 缓存 {i} ≠ {yc[i]}");
                Assert.True(yv != 0, $"{id}：{yc[i]} 是 0（缺测记号）却进了拟合");
                Assert.True(Bit(curve.DataTempsC[i], xv), $"{id}：代码数据点温度 {i} ≠ {xc[i]}");
                Assert.True(Bit(curve.DataAlphaE6[i], yv), $"{id}：代码数据点 α {i} = {curve.DataAlphaE6[i]:R} ≠ {yc[i]} = {yv:R}");
            }
            Assert.Equal("poly", s.TrendType);
            Assert.True(s.TrendOrder == curve.Order, $"{id}：图表趋势线 {s.TrendOrder} 阶，代码 {curve.Order} 阶");
            Assert.False(s.HasIntercept, $"{id}：趋势线设了固定截距 —— 算法就不是自由截距最小二乘了");
            Assert.True(s.DispEq, $"{id}：趋势线没显示方程");
            Assert.Equal("0.000000E+00", s.LabelNumFmt);
            bool hasText = s.LabelText.Contains("y =", StringComparison.Ordinal);
            // 2026-09-18（Opus 5）：RecomputedLabelStale 这一档**也**存了方程文本，只是那是改前数据的缓存
            bool shouldHaveText = curve.Origin is ExpansionCoeffOrigin.ChartLabelText or ExpansionCoeffOrigin.RecomputedLabelStale;
            Assert.True(hasText == shouldHaveText,
                $"{id}：标签{(hasText ? "存了" : "没存")}方程文本，代码却标 {curve.Origin}");
            _o.WriteLine($"系列 {s.Idx}{(s.Filtered ? "（隐藏）" : "")} {id}：{curve.Order} 阶，x {s.XRef} y {s.YRef}，{n} 点 "
                       + $"{curve.DataMinC:0}–{curve.DataMaxC:0} °C，系数来源 {curve.Origin}");
        }
        Assert.True(seen.SetEquals(PtThermalExpansion.Curves.Keys), "代码有曲线在图表里找不到系列：" + string.Join("、", PtThermalExpansion.Curves.Keys.Except(seen)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static (Dictionary<int, string> Terms, string R2) ParseLabel(string label)
    {
        int cut = label.IndexOf("R²", StringComparison.Ordinal);
        string eq = Norm(label[..cut]), r2 = Norm(label[cut..]);
        Assert.StartsWith("y=", eq);
        var terms = new Dictionary<int, string>();
        string rebuilt = "y=";
        foreach (Match m in Regex.Matches(eq[2..], @"([+-]?)(\d\.\d+E[+-]\d+)(x(\d*))?"))
        {
            int p = !m.Groups[3].Success ? 0 : m.Groups[4].Value == "" ? 1 : int.Parse(m.Groups[4].Value);
            terms.Add(p, (m.Groups[1].Value == "-" ? "-" : "") + m.Groups[2].Value);
            rebuilt += m.Value;
        }
        Assert.Equal(eq, rebuilt);
        var rm = Regex.Match(r2, @"^R²=(\d\.\d+E[+-]\d+)\(");
        Assert.True(rm.Success, $"R² 文本看不懂：{r2}");
        return (terms, rm.Groups[1].Value);
    }

    private static (List<double> X, List<double> Y) SeriesCells(XlsxSheet sh, ChartSeries s)
    {
        var (_, xc) = XlsxChart.Expand(s.XRef);
        var (_, yc) = XlsxChart.Expand(s.YRef);
        return (xc.Take(yc.Count).Select(sh.Num).ToList(), yc.Select(sh.Num).ToList());
    }

    private static string IdOf(XlsxSheet sh, ChartSeries s)
        => Regex.Replace(Norm(sh.Str(XlsxChart.Expand(s.NameRef).Cells.Single())), @"\(α\)$|%$", "");

    /// <summary>
    /// 作废的旧趋势线标签（2026-09-18，Opus 5）：工作簿 G18 按 Platinum Metals Rev., 1960, 4 (4), 139 改正后，
    /// Excel 存在图表里的方程文本没有重算 —— 这里登记那条**改前**的方程，门用它证明「标签确实是旧的」。
    /// 逐字抄自 chart1.xml 的 c:trendlineLbl（c0..c6 升幂）。
    /// </summary>
    private static readonly Dictionary<string, string[]> StaleLabelCoeffs = new(StringComparer.Ordinal)
    {
        ["PtRh20"] = new[] { "4.206619E+00", "2.352578E-02", "-6.760337E-05", "1.233031E-07", "-1.256153E-10", "6.567769E-14", "-1.362330E-17" },
    };

    /// <summary>作废的旧标签 R²（同上）。</summary>
    private static readonly Dictionary<string, string> StaleLabelRSquared = new(StringComparer.Ordinal)
    {
        ["PtRh20"] = "9.972203E-01",
    };

    [Fact]
    public void 趋势线复算器_存了方程文本的逐系数一致且R2在1e7内_纯铂复算等于X14字面量()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var series = XlsxChart.Series(book.Part("xl/charts/chart1.xml"));
        int checkedText = 0, checkedStale = 0; bool checkedPt = false;
        foreach (var s in series)
        {
            string id = IdOf(sh, s);
            var curve = PtThermalExpansion.Curves[id];
            var (x, y) = SeriesCells(sh, s);
            var fit = ExactPolyFit.Fit(x, y, curve.Order);
            if (curve.Origin == ExpansionCoeffOrigin.ChartLabelText)
            {
                var (terms, r2Text) = ParseLabel(s.LabelText);
                Assert.True(terms.Count == curve.Order + 1, $"{id}：标签项数与阶数不符");
                for (int k = 0; k <= curve.Order; k++)
                {
                    var ((_, _), margin) = fit.Coeffs[k].ToSigWithMargin(7);
                    string mine = fit.Coeffs[k].ToExcelSci(7);
                    _o.WriteLine($"{id} c{k}：复算 {mine}（离舍入分界 {margin:0.00} 个末位单位）  标签 {terms[k]}");
                    Assert.Equal(terms[k], mine);
                    Assert.True(Bit(curve.Coeffs[k], double.Parse(terms[k], NumberStyles.Float, CultureInfo.InvariantCulture)),
                        $"{id} c{k}：代码 {curve.Coeffs[k]:R} ≠ 标签 {terms[k]}");
                }
                var r2t = Q.FromDouble(double.Parse(r2Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                double dr2 = (fit.RSquared - r2t).Abs().ToDouble();
                _o.WriteLine($"{id} R²：精确 {fit.RSquared.ToExcelSci(10)}，标签 {r2Text}，|Δ| = {dr2:E2}（门 1e-7）");
                Assert.True(dr2 <= 1e-7, $"{id} R² 与标签差 {dr2:E2} > 1e-7");
                checkedText++;
            }
            else if (curve.Origin == ExpansionCoeffOrigin.RecomputedLabelStale)
            {
                // 2026-09-18（Opus 5，用户原话「改」）：工作簿的数据格已按公开文献改正，Excel 没有重算过图表，
                // 标签里那条方程是**改前**的缓存 ⇒ 这一档的门是两条，不是一条：
                //   ① 代码系数 == 按**现在**的数据复算取 7 位（数据说了算）；
                //   ② 标签 != 复算，且标签逐字等于登记的**旧值**（证明标签真的是旧的，而不是「改了个寂寞」）。
                var (terms, r2Text) = ParseLabel(s.LabelText);
                Assert.True(terms.Count == curve.Order + 1, $"{id}：标签项数与阶数不符");
                Assert.True(StaleLabelCoeffs.TryGetValue(id, out var stale) && stale.Length == curve.Order + 1,
                    $"{id}：标 RecomputedLabelStale 却没有登记旧标签系数");
                int differ = 0;
                for (int k = 0; k <= curve.Order; k++)
                {
                    var ((_, _), margin) = fit.Coeffs[k].ToSigWithMargin(7);
                    string mine = fit.Coeffs[k].ToExcelSci(7);
                    _o.WriteLine($"{id} c{k}：复算 {mine}（离舍入分界 {margin:0.00} 个末位单位）  代码 {curve.Coeffs[k]:R}  标签（旧值）{terms[k]}");
                    Assert.True(Bit(double.Parse(mine, NumberStyles.Float, CultureInfo.InvariantCulture), curve.Coeffs[k]),
                        $"{id} c{k}：代码 {curve.Coeffs[k]:R} ≠ 复算 7 位 {mine} —— 这一档以数据重算为准");
                    Assert.Equal(stale[k], terms[k]);
                    if (terms[k] != mine) differ++;
                }
                Assert.True(differ > 0,
                    $"{id}：标签与复算逐位相同 —— 那它就不是旧值，这条曲线不该标 RecomputedLabelStale");
                var r2t = Q.FromDouble(double.Parse(r2Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                double dr2 = (fit.RSquared - r2t).Abs().ToDouble();
                Assert.Equal(StaleLabelRSquared[id], r2Text);
                _o.WriteLine($"{id} R²：复算 {fit.RSquared.ToExcelSci(10)}，标签（旧值）{r2Text}，|Δ| = {dr2:E2}"
                           + $"；系数出处注记：{curve.CoeffSource}");
                Assert.True(dr2 > 1e-7, $"{id}：标签 R² 与复算只差 {dr2:E2} —— 标签看不出是旧的，这一档失去理由");
                Assert.True(curve.CoeffSource.Contains("改正", StringComparison.Ordinal) && curve.CoeffSource.Contains("作废", StringComparison.Ordinal),
                    $"{id}：系数出处注记没说清标签为什么作废：{curve.CoeffSource}");
                checkedStale++;
            }
            else if (curve.Origin == ExpansionCoeffOrigin.CellFormula)
            {
                for (int k = 0; k <= curve.Order; k++)
                {
                    string mine = fit.Coeffs[k].ToExcelSci(7);
                    var ((_, _), margin) = fit.Coeffs[k].ToSigWithMargin(7);
                    _o.WriteLine($"{id} c{k}：复算 {mine}（离舍入分界 {margin:0.00}）  X14 字面量 {curve.Coeffs[k]:R}");
                    Assert.True(Bit(double.Parse(mine, NumberStyles.Float, CultureInfo.InvariantCulture), curve.Coeffs[k]),
                        $"{id} c{k}：复算 7 位 {mine} ≠ X14 字面量 {curve.Coeffs[k]:R} —— X14 公式与图表趋势线不是同一条");
                }
                _o.WriteLine($"{id} R²（标签未存文本）：精确 {fit.RSquared.ToExcelSci(10)}");
                checkedPt = true;
            }
        }
        Assert.True(checkedText > 0 && checkedPt, "反自证：存了方程文本的系列与纯铂 X14 都必须真的核过");
        Assert.True(checkedStale == StaleLabelCoeffs.Count,
            $"反自证：登记了 {StaleLabelCoeffs.Count} 条旧标签，只核到 {checkedStale} 条");
    }

    [Fact]
    public void 未存方程文本的趋势线_代码系数等于复算器按7位舍入()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        int n = 0;
        foreach (var s in XlsxChart.Series(book.Part("xl/charts/chart1.xml")))
        {
            string id = IdOf(sh, s);
            var curve = PtThermalExpansion.Curves[id];
            if (curve.Origin != ExpansionCoeffOrigin.RecomputedSameAlgorithm) continue;
            Assert.False(s.LabelText.Contains("y =", StringComparison.Ordinal), $"{id} 标签存了方程文本，应改用文本");
            var (x, y) = SeriesCells(sh, s);
            var fit = ExactPolyFit.Fit(x, y, curve.Order);
            for (int k = 0; k <= curve.Order; k++)
            {
                string mine = fit.Coeffs[k].ToExcelSci(7);
                var ((_, _), margin) = fit.Coeffs[k].ToSigWithMargin(7);
                _o.WriteLine($"{id} c{k}：复算 {mine}（离舍入分界 {margin:0.00} 个末位单位）  代码 {curve.Coeffs[k]:R}");
                Assert.True(Bit(double.Parse(mine, NumberStyles.Float, CultureInfo.InvariantCulture), curve.Coeffs[k]),
                    $"{id} c{k}：代码 {curve.Coeffs[k]:R} ≠ 复算 7 位 {mine}");
            }
            _o.WriteLine($"{id} R²：精确 {fit.RSquared.ToExcelSci(10)}（{(s.DispRSqr ? "图上显示" : "图上不显示")}）");
            n++;
        }
        Assert.True(n > 0, "反自证：一条复算系数的曲线都没核");
    }

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 趋势线与LT_L0表的应变差_纯铂不超过60微应变_其余只报()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        foreach (var (alloy, lt, _, _) in PtThermalExpansion.Table)
        {
            var c = PtThermalExpansion.Curves[alloy];
            double worst = 0, at = 0;
            foreach (double t in c.DataTempsC)
            {
                int r = 4 + Array.IndexOf(PtThermalExpansion.TableTempsC, t);
                double tab = sh.Num(lt + r) - 1;
                double d = Math.Abs(c.RawStrain(t) - tab) * 1e6;
                if (d > worst) { worst = d; at = t; }
            }
            _o.WriteLine($"{alloy}：函数应变 vs LT/L0 表（数据点上）最大差 {worst:0.0} µε @ {at:0} °C（表的舍入 ±50 µε）");
            if (alloy == "Pt") Assert.True(worst <= 60, $"纯铂函数与表差 {worst:0.0} µε > 60 µε");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 右上小表纯铂两值只作核对点_与X14的差在4位小数分辨率内()
    {
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var pt = PtThermalExpansion.Curves["Pt"];
        var u = PtThermalExpansion.SideTable.Single(s => s.Col == "U");
        double[] tol = { 0.10, 0.05 };   // 500 °C、1000 °C：物理把关事先给定
        for (int i = 0; i < 2; i++)
        {
            double t = PtThermalExpansion.SideTableTempsC[i];
            double x14 = pt.RawMeanAlphaE6(t);
            int r = 4 + Array.IndexOf(PtThermalExpansion.TableTempsC, t);
            double tableA = sh.Num("D" + r);
            _o.WriteLine($"{t:0} °C：小表 U{4 + i} = {u.AlphaE6[i]}，X14 = {x14:0.000}，表 D{r} = {tableA:0.000}（两个来源都登记，主用 X14）");
            Assert.True(Math.Abs(u.AlphaE6[i] - x14) <= tol[i] + 1e-12, $"{t} °C 小表与 X14 差 {Math.Abs(u.AlphaE6[i] - x14):0.000} > {tol[i]}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 覆盖分档_外推与无数据与借用随值带出()
    {
        var pt = PtThermalExpansion.Curves["Pt"];
        double lo = pt.DataMinC, hi = pt.DataMaxC;
        Assert.Equal(ExpansionCoverage.InRange, PtThermalExpansion.MeanAlphaE6("Pt", 0.5 * (lo + hi)).Coverage);
        Assert.Equal(ExpansionCoverage.InRangeNearEnd, PtThermalExpansion.InstantAlphaE6("Pt", hi - 1).Coverage);
        var e1 = PtThermalExpansion.MeanAlphaE6("Pt", hi + 100);
        Assert.True(e1.Coverage == ExpansionCoverage.Extrapolated && e1.HasValue, e1.Note);   // 第二轮：1000 °C 以上一律外推
        Assert.Equal(ExpansionCoverage.Extrapolated, PtThermalExpansion.MeanAlphaE6("Pt", PtThermalExpansion.TableMaxC).Coverage);
        Assert.Equal(ExpansionCoverage.EndDefinedPointInterp, PtThermalExpansion.MeanAlphaE6("Pt", lo / 2).Coverage);
        foreach (double t in new[] { PtThermalExpansion.TableMaxC + 1, -1.0, double.NaN })
        {
            var v = PtThermalExpansion.Strain("Pt", t);
            Assert.True(v.Coverage == ExpansionCoverage.NoData && double.IsNaN(v.Value), $"{t} °C 应无数据：{v.Note}");
        }

        int highOrderChecked = 0;
        foreach (var c in PtThermalExpansion.Curves.Values.Where(c => c.Order >= 4 && c.DataMaxC < PtThermalExpansion.TableMaxC))
        {
            Assert.Equal(ExpansionCoverage.NoData, c.Classify(0.5 * (c.DataMaxC + PtThermalExpansion.TableMaxC), false, out _));
            highOrderChecked++;
        }
        Assert.True(highOrderChecked > 0, "反自证：≥4 阶在数据上端之外无数据，至少要核到 PtAu5");

        // 2026-09-18（Opus 5）：G18 改正后 PtRh20 已经没有瞬时 α 下降段 —— 形状不可信的探针改挂仍有下降段的
        // ZGSPtRh10（100–315.9 °C），同时**正面**断言 PtRh20 一段都没有（把 G18 改回 1.0157 这条立刻红）。
        Assert.Empty(PtThermalExpansion.Curves["PtRh20"].UntrustedSegments);
        var seg = PtThermalExpansion.Curves["ZGSPtRh10"].UntrustedSegments.Single();
        var bad = PtThermalExpansion.InstantAlphaE6("Tanaka-ZGS-PtRh10", 0.5 * (seg.LoC + seg.HiC));
        Assert.True(bad.Coverage == ExpansionCoverage.ShapeUntrusted && bad.HasValue, bad.Note);

        var pd = PtThermalExpansion.MeanAlphaE6("Pd", 1000);
        Assert.True(pd.Coverage == ExpansionCoverage.NoData && pd.Link == ExpansionLink.None && double.IsNaN(pd.Value));

        var fks = PtThermalExpansion.Strain("FKS16/Pt", 1200);
        Assert.True(fks.Coverage == ExpansionCoverage.NoData, "FKS16 默认不借（两位把关意见冲突，取保守）");
        var fksB = PtThermalExpansion.Strain("FKS16/Pt", 1200, ExpansionBorrowOption.SameBaseAlloy);
        Assert.True(fksB.IsBorrowed && fksB.Curve == "Pt" && fksB.HasValue && fksB.Note.Contains("借用"), fksB.Note);

        var um = PtThermalExpansion.Strain("Umicore-PtRh10", 1200);
        var dir = PtThermalExpansion.Strain("Pt-Rh/90-10", 1200);
        Assert.True(um.IsBorrowed && !dir.IsBorrowed && Bit(um.Value, dir.Value), um.Note);
        var diff = PtThermalExpansion.StrainDifference("Umicore-PtRh10", 1200, "Pt", 1200);
        Assert.True(diff.IsBorrowed && diff.Note.Contains("借用"), "跨牌号应变差：借用标签必须随值带出");

        Assert.Throws<KeyNotFoundException>(() => PtThermalExpansion.Strain("PtAu5", 500));   // 只入数据目录，不是设计牌号

        // 值 NaN ⇔ 无数据，全网格
        foreach (string g in MaterialDb.All.Keys)
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
                for (double t = -10; t <= 1600; t += 10)
                {
                    var v = PtThermalExpansion.MeanAlphaE6(g, t, b);
                    Assert.True(v.HasValue == !double.IsNaN(v.Value), $"{g} {t} °C：NaN 与无数据不一致");
                }
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 口径 D1（2026-09-15 第二轮）：纯铂 1000 °C 以上**所有量**一律「外推」—— 平均 α、LT/L0、应变、热态长度、伸长、
    /// 瞬时 α、应变差。与 ZGSPt 表的比较（1050–1300 °C 每 50 K 的绝对 α 差、10/30 K 应变差是否落在 ZGSPt 舍入带）
    /// **只打印诊断数，不断言、不进覆盖类别**。第一轮的「外推·有旁证」判据在 1000 °C 附近不成立（见打印的反例）。
    /// </summary>
    [Fact]
    public void 纯铂1000度以上所有量一律外推_与ZGSPt的比较只打印诊断数()
    {
        var pt = PtThermalExpansion.Curves["Pt"];
        Assert.Equal(1000.0, pt.DataMaxC);
        int n = 0;
        for (double t = pt.DataMaxC + 0.5; t <= PtThermalExpansion.TableMaxC + 1e-9; t += 0.5)
        {
            var all = new[]
            {
                PtThermalExpansion.MeanAlphaE6("Pt", t), PtThermalExpansion.LengthRatio("Pt", t), PtThermalExpansion.Strain("Pt", t),
                PtThermalExpansion.InstantAlphaE6("Pt", t), PtThermalExpansion.HotLength("Pt", 780, t), PtThermalExpansion.Elongation("Pt", 780, t),
                PtThermalExpansion.StrainDifference("Pt", t, "Pt", t - 10), PtThermalExpansion.StrainDifference("Pt", t, "Pt", 900),
            };
            foreach (var v in all)
                Assert.True(v.Coverage == ExpansionCoverage.Extrapolated && v.HasValue, $"纯铂 {t} °C 应一律外推：{v.Coverage}，{v.Note}");
            n++;
        }
        Assert.Equal(ExpansionCoverage.InRange, PtThermalExpansion.Strain("Pt", pt.DataMaxC).Coverage);
        Assert.True(n > 0);

        // 诊断数（只报）
        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        int[] rows = { 9, 14, 16, 17 };
        var zx = rows.Select(r => sh.Num("B" + r)).ToList();
        var zy = rows.Select(r => sh.Num("Q" + r)).ToList();
        const double half = 0.05;   // ZGS 表 3 位有效数字的舍入（物理把关事先给定）
        var fits = new List<double[]>();
        for (int mask = 0; mask < 16; mask++)
        {
            var yy = zy.Select((v, i) => v + (((mask >> i) & 1) == 1 ? half : -half)).ToList();
            fits.Add(ExactPolyFit.Fit(zx, yy, 2).CoeffsDouble);
        }
        var central = ExactPolyFit.Fit(zx, zy, 2).CoeffsDouble;
        double A(double[] c, double t) => c[0] + c[1] * t + c[2] * t * t;
        double Eps(double[] c, double t) => A(c, t) * 1e-6 * t;
        void Line(double t, double dT)
        {
            double x = pt.RawStrain(t) - pt.RawStrain(t - dT);
            double lo = fits.Min(c => Eps(c, t) - Eps(c, t - dT)), hi = fits.Max(c => Eps(c, t) - Eps(c, t - dT));
            _o.WriteLine($"    ΔT {dT:0} K：X14 应变差 {x * 1e6:0.00} µε，ZGSPt 舍入带 {lo * 1e6:0.00}–{hi * 1e6:0.00} µε ⇒ {(x >= lo && x <= hi ? "带内" : "带外")}");
        }
        for (double t = 1050; t <= 1300 + 1e-9; t += 50)
        {
            double ax = pt.RawMeanAlphaE6(t), ac = A(central, t);
            double alo = fits.Min(c => A(c, t)), ahi = fits.Max(c => A(c, t));
            _o.WriteLine($"{t:0} °C（诊断，只报）：平均 α X14 {ax:0.0000}，ZGSPt 二次拟合 {ac:0.0000}，绝对差 {ax - ac:+0.0000;-0.0000}"
                       + $"（{(ax / ac - 1) * 100:+0.00;-0.00} %），舍入带 {alo:0.0000}–{ahi:0.0000} ⇒ {(ax >= alo && ax <= ahi ? "带内" : "带外")}");
            Line(t, 10); Line(t, 30);
        }
        _o.WriteLine("第一轮审查指出的反例（只报）：");
        _o.WriteLine("  1010 °C："); Line(1010, 10);
        _o.WriteLine("  1020 °C："); Line(1020, 30);
        for (int i = 0; i < rows.Length; i++)
            if (zx[i] > pt.DataMaxC)
                _o.WriteLine($"{zx[i]:0} °C 绝对值：X14 相对 ZGSPt 表 Q{rows[i]} {(pt.RawMeanAlphaE6(zx[i]) / zy[i] - 1) * 100:+0.00;-0.00} %（表舍入带 ±{half / zy[i] * 100:0.00} %，只报）");
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 口径 D3（2026-09-15 第二轮）：0 ≤ T &lt; 100 °C 所有曲线统一取「ε 在定义点 ε(0)=0 与 100 °C 拟合值之间线性」，
    /// 标「端部·定义点内插」；100 °C 那个值本身更差（ZGS 两条）时取更差的。室温不再是 NaN（下一轮升温温差判据从室温起算）。
    /// 期望类别取 HANDOVER 0.-6 节覆盖表的决定记录。
    /// </summary>
    [Fact]
    public void 零到100度按定义点内插_所有曲线统一_室温不再是NaN()
    {
        double a0 = PtThermalExpansion.DefinedPointInterpBelowC;
        foreach (var c in PtThermalExpansion.Curves.Values)
        {
            double a100 = c.RawMeanAlphaE6(a0);
            foreach (double t in new[] { 0, 0.5, 20, 50, 99.999 })
            {
                Assert.True(Bit(c.MeanAlphaE6At(t), a100), $"{c.Id} {t} °C：平均 α 应取 100 °C 的拟合值");
                Assert.True(Bit(c.StrainAt(t), a100 * 0.000001 * t), $"{c.Id} {t} °C：ε 应在 0 与 100 °C 拟合值之间线性");
                Assert.True(Bit(c.InstantAlphaE6At(t), a100), $"{c.Id} {t} °C：线性段瞬时 α 应为常数");
                var cov = c.Classify(t, false, out string note);
                Assert.True(cov >= ExpansionCoverage.EndDefinedPointInterp && cov != ExpansionCoverage.NoData, $"{c.Id} {t} °C：{cov} {note}");
            }
            Assert.True(Bit(c.StrainAt(a0), c.RawStrain(a0)) && Bit(c.MeanAlphaE6At(a0), a100), $"{c.Id}：100 °C 本身取多项式");
            Assert.True(Math.Abs(c.StrainAt(a0 - 1e-6) - c.RawStrain(a0)) <= Math.Abs(a100) * 2e-12, $"{c.Id}：ε 在 100 °C 处不连续");
            // 第三轮（2026-09-15，Opus 5）：100 °C 起曲线层三个函数就是裸多项式（裸多项式另由精确有理数核）——
            // 否则曲线层自己错了，映射门拿曲线层值当期望会跟着错（复核注入 I6f：InstantAlphaE6At 100 °C 以上改用平均 α，第二轮 37 条全绿）。
            for (int i = 200; i <= 3000; i++)   // 100–1500 °C 每 0.5 K
            {
                double t = 0.5 * i;
                Assert.True(Bit(c.MeanAlphaE6At(t), c.RawMeanAlphaE6(t)) && Bit(c.StrainAt(t), c.RawStrain(t))
                            && Bit(c.InstantAlphaE6At(t), c.RawInstantAlphaE6(t)),
                    $"{c.Id} {t} °C：100 °C 起平均 α／应变／瞬时 α 应就是裸多项式");
            }
        }

        var expect = new (string Grade, ExpansionCoverage Cov)[]
        {
            ("Pt", ExpansionCoverage.EndDefinedPointInterp),
            ("Pt-Rh/90-10", ExpansionCoverage.EndDefinedPointInterp),
            ("Pt-Rh/80-20", ExpansionCoverage.EndDefinedPointInterp),
            ("Umicore-PtRh10", ExpansionCoverage.EndDefinedPointInterp),
            ("Umicore-PtRh20", ExpansionCoverage.EndDefinedPointInterp),
            ("Tanaka-ZGS-Pt", ExpansionCoverage.Extrapolated),          // ZGSPt 数据 500–1300：100 °C 本身是外推
            ("Tanaka-ZGS-PtRh10", ExpansionCoverage.ShapeUntrusted),    // ZGSPtRh10 瞬时 α 在 100–316 °C 下降
        };
        foreach (var (g, cov) in expect)
            foreach (double t in new[] { 0.0, 20, 50 })
            {
                var all = new[]
                {
                    PtThermalExpansion.MeanAlphaE6(g, t), PtThermalExpansion.LengthRatio(g, t), PtThermalExpansion.Strain(g, t),
                    PtThermalExpansion.InstantAlphaE6(g, t), PtThermalExpansion.HotLength(g, 500, t), PtThermalExpansion.Elongation(g, 500, t),
                };
                foreach (var v in all)
                    Assert.True(v.Coverage == cov && v.HasValue && !double.IsNaN(v.Value), $"{g} {t} °C 应为 {cov}：{v.Coverage}，{v.Note}");
                if (t == 0)
                    Assert.True(Bit(PtThermalExpansion.LengthRatio(g, 0).Value, 1) && Bit(PtThermalExpansion.Strain(g, 0).Value, 0), $"{g}：0 °C 的 LT/L0 必须是定义值 1");
                _o.WriteLine($"{g} {t} °C：平均 α {all[0].Value:0.0000}，ε {all[2].Value * 1e6:0.0} µε（{all[0].Coverage}）");
            }
        var room = PtThermalExpansion.StrainDifference("Pt-Rh/80-20", 1100, "Pt-Rh/80-20", 20);
        Assert.True(room.HasValue && room.Coverage == ExpansionCoverage.EndDefinedPointInterp, "反自证：第一轮 ≥4 阶曲线室温一端是 NaN —— " + room.Note);
        foreach (double t in new[] { -0.001, PtThermalExpansion.TableMaxC + 0.001 })
            Assert.Equal(ExpansionCoverage.NoData, PtThermalExpansion.Strain("Pt", t).Coverage);
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 口径 D4①（2026-09-15 第二轮）：形状不可信 = 瞬时 α 下降段，多项式段 100–1500 °C 上由精确有理数导数符号独立复核；
    /// 只有平均 α 下降、瞬时 α 不降的温度**不许**再标（第一轮把两者都算）。
    /// </summary>
    [Fact]
    public void 形状不可信段由精确瞬时α导数符号独立复核_纯铂全程没有此段()
    {
        foreach (var c in PtThermalExpansion.Curves.Values)
        {
            var q = c.Coeffs.Select(Q.FromDouble).ToArray();
            var dInst = Enumerable.Range(1, c.Order).Select(k => q[k] * new Q(k * (k + 1), 1)).ToArray();
            int mismatch = 0;
            for (int t = 100; t <= 1500; t++)
            {
                if (c.UntrustedSegments.Any(s => Math.Abs(t - s.LoC) < 0.01 || Math.Abs(t - s.HiC) < 0.01)) continue;
                bool exactBad = ExactPolyFit.Eval(dInst, new Q(t, 1)).Sign < 0;
                if (exactBad != c.InUntrusted(t, t, out _)) mismatch++;
            }
            _o.WriteLine($"{c.Id}（数据 {c.DataMinC:0}–{c.DataMaxC:0} °C）瞬时 α 下降段："
                + (c.UntrustedSegments.Count == 0 ? "无" : string.Join("；", c.UntrustedSegments.Select(s => $"{s.LoC:0.0}–{s.HiC:0.0} °C"))));
            Assert.True(mismatch == 0, $"{c.Id}：{mismatch} 个整数温度上代码的段与精确瞬时 α 导数符号不一致");
            Assert.True(c.UntrustedSegments.All(s => s.LoC >= PtThermalExpansion.DefinedPointInterpBelowC), $"{c.Id}：形状段伸进了 0–100 °C 的定义点内插段");
        }
        Assert.Empty(PtThermalExpansion.Curves["Pt"].UntrustedSegments);
        // 2026-09-18（Opus 5，用户原话「改」）：这一条翻了面 —— 改前断言「PtRh20 高温段必须有下降段」，
        // 而那段是工作簿 G18 抄错（1.0157，论文 1.0152）插出来的，不是 fcc 固溶体的物理。改正后必须一段都没有。
        Assert.Empty(PtThermalExpansion.Curves["PtRh20"].UntrustedSegments);
        // 扫描器不许恒空的反自证改挂在仍有下降段的两条上（PtAu5 的段在数据区间**内部**，最能证明扫描器真在扫）
        var au = PtThermalExpansion.Curves["PtAu5"];
        Assert.True(au.UntrustedSegments.Any(s => s.LoC > au.DataMinC && s.HiC < au.DataMaxC),
            "反自证：PtAu5 数据区间内部的瞬时 α 下降段是已知的，扫描器不许恒空");
        Assert.NotEmpty(PtThermalExpansion.Curves["ZGSPtRh10"].UntrustedSegments);

        // 只有平均 α 下降的点：ZGSPtRh10 400 °C（α_mean 顶点 474 °C 以下下降，瞬时 α 约 316 °C 以上已上升）⇒ 不标形状不可信
        var z = PtThermalExpansion.Curves["ZGSPtRh10"];
        var zq = z.Coeffs.Select(Q.FromDouble).ToArray();
        var dMean = Enumerable.Range(1, z.Order).Select(k => zq[k] * new Q(k, 1)).ToArray();
        var dInstZ = Enumerable.Range(1, z.Order).Select(k => zq[k] * new Q(k * (k + 1), 1)).ToArray();
        Assert.True(ExactPolyFit.Eval(dMean, new Q(400, 1)).Sign < 0 && ExactPolyFit.Eval(dInstZ, new Q(400, 1)).Sign > 0,
            "反自证：400 °C 应是「平均 α 降、瞬时 α 升」的点");
        Assert.Equal(ExpansionCoverage.Extrapolated, z.Classify(400, false, out _));
    }

    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void 应变差函数就是两温度应变之差_不是平均α乘温差_瞬时α是精确导数()
    {
        var pt = PtThermalExpansion.Curves["Pt"];
        var q = pt.Coeffs.Select(Q.FromDouble).ToArray();
        const double eps = 2.220446049250313e-16;
        foreach (int t in new[] { 1100, 1200, 1300, 1400, 1500 })
            foreach (int dT in new[] { 10, 30 })
            {
                var v = PtThermalExpansion.StrainDifference("Pt", t, "Pt", t - dT);
                Q E(int tt) => ExactPolyFit.Eval(q, new Q(tt, 1)) * new Q(tt, 1) * new Q(1, 1000000);
                double exact = (E(t) - E(t - dT)).ToDouble();
                double bound = 16 * eps * (Math.Abs(pt.RawStrain(t)) + Math.Abs(pt.RawStrain(t - dT)));
                Assert.True(Math.Abs(v.Value - exact) <= bound, $"{t}/{dT}：{v.Value:R} vs 精确 {exact:R}");
                double naive = pt.RawMeanAlphaE6(t) * dT * 1e-6;
                _o.WriteLine($"纯铂 {t} °C、ΔT {dT} K：Δε = {exact * 1e6:0.0} µε（{v.Coverage}）；误用 α_mean·ΔT = {naive * 1e6:0.0} µε，低估 {(exact - naive) / exact * 100:0.0} %");
                Assert.True((exact - naive) / exact > 0.10, "反自证：α_mean·ΔT 与真应变差应明显不同，否则专门函数没有存在理由");
            }

        foreach (var c in PtThermalExpansion.Curves.Values)
        {
            var cq = c.Coeffs.Select(Q.FromDouble).ToArray();
            var inst = Enumerable.Range(0, c.Order + 1).Select(k => cq[k] * new Q(k + 1, 1)).ToArray();
            for (int t = 50; t <= 1500; t += 50)
            {
                // 第三轮（2026-09-15，Opus 5）：裸平均 α 与裸应变也对精确有理数核（曲线层 At 函数在 100 °C 起等于它们，映射门拿 At 当期望）
                Q qa = ExactPolyFit.Eval(cq, new Q(t, 1));
                double sumA = Enumerable.Range(0, c.Order + 1).Sum(k => Math.Abs(c.Coeffs[k] * Math.Pow(t, k)));
                Assert.True(Math.Abs(c.RawMeanAlphaE6(t) - qa.ToDouble()) <= 16 * eps * sumA, $"{c.Id} {t} °C 平均 α 不是系数多项式的值");
                double exEps = (qa * new Q(t, 1) * new Q(1, 1000000)).ToDouble();
                Assert.True(Math.Abs(c.RawStrain(t) - exEps) <= 16 * eps * sumA * t * 1e-6, $"{c.Id} {t} °C 应变不是 α_mean·1e-6·T");
                double ex = ExactPolyFit.Eval(inst, new Q(t, 1)).ToDouble();
                double sumAbs = Enumerable.Range(0, c.Order + 1).Sum(k => Math.Abs((k + 1) * c.Coeffs[k] * Math.Pow(t, k)));
                Assert.True(Math.Abs(c.RawInstantAlphaE6(t) - ex) <= 16 * eps * sumAbs, $"{c.Id} {t} °C 瞬时 α 不是精确导数");
                const double h = 0.01;
                double fd = (c.RawStrain(t + h) - c.RawStrain(t - h)) / (2 * h) * 1e6;
                Assert.True(Math.Abs(fd - ex) <= 1e-6 * Math.Abs(ex), $"{c.Id} {t} °C 瞬时 α 与应变数值导数不符");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 口径 D4②／阻断 3③（2026-09-15 第二轮）：应变差的覆盖类别 = 两端（各按 Strain 的口径）中较差的一端；
    /// 任一端无数据 ⇒ NaN；值 = 两端 Strain 之差；任一端借用 ⇒ 借用。全部材料库牌号 × 一组温度两两组合核。
    /// </summary>
    [Fact]
    public void 应变差的覆盖取两端较差的一端_任一端无数据即NaN_值是两端应变之差()
    {
        double[] temps = { double.NaN, -1, 0, 20, 99.5, 100, 150, 250, 500, 950, 1000, 1001, 1100, 1350, 1400, 1450, 1500, 1501 };
        var grades = MaterialDb.All.Keys.ToArray();
        var specs = ExpansionCoverageSpec.All.Value;
        int n = 0, differ = 0, oneMissing = 0;
        foreach (string ga in grades)
            foreach (double ta in temps)
            {
                var a = PtThermalExpansion.Strain(ga, ta);
                // 第四轮（2026-09-15，Opus 5）：两端 Strain 的覆盖本身也对门按定义算的期望（映射取 Resolve，映射门另核），不只核「取较差」
                string? ca = PtThermalExpansion.Resolve(ga).CurveId;
                var ea = ca is null ? ExpansionCoverage.NoData : specs[ca].Expected(ta, false);
                Assert.True(a.Coverage == ea, $"Strain({ga}, {ta} °C)：分档 {a.Coverage}，门按定义算 {ea}");
                foreach (string gb in grades)
                    foreach (double tb in temps)
                    {
                        var b = PtThermalExpansion.Strain(gb, tb);
                        var d = PtThermalExpansion.StrainDifference(ga, ta, gb, tb);
                        var worse = (ExpansionCoverage)Math.Max((int)a.Coverage, (int)b.Coverage);
                        Assert.True(d.Coverage == worse,
                            $"{ga} {ta} °C（{a.Coverage}）− {gb} {tb} °C（{b.Coverage}）：应变差标 {d.Coverage}，应取较差的 {worse}");
                        if (!a.HasValue || !b.HasValue)
                        {
                            Assert.True(double.IsNaN(d.Value) && !d.HasValue, $"{ga} {ta} − {gb} {tb}：一端无数据却给了值 {d.Value}");
                            oneMissing++;
                        }
                        else
                        {
                            Assert.True(Bit(d.Value, a.Value - b.Value), $"{ga} {ta} − {gb} {tb}：{d.Value:R} ≠ 两端应变之差 {a.Value - b.Value:R}");
                            Assert.True(d.IsBorrowed == (a.IsBorrowed || b.IsBorrowed), $"{ga} − {gb}：借用标记没随值带出");
                        }
                        if (a.Coverage != b.Coverage) differ++;
                        n++;
                    }
            }
        _o.WriteLine($"应变差覆盖核对 {n} 组，其中两端类别不同 {differ} 组、一端无数据 {oneMissing} 组");
        Assert.True(differ > 0 && oneMissing > 0, "反自证：两端类别不同、一端无数据的组合都必须真的核到");
        // 删掉的旧规则「两端一律按导数类量标端部」不许回来：数据区间内两点的应变差就是区间内
        Assert.Equal(ExpansionCoverage.InRange, PtThermalExpansion.StrainDifference("Pt", 950, "Pt", 900).Coverage);
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 阻断 3①（2026-09-15 第二轮）：映射逐条断言「牌号 → 曲线 → 直接／借用」三元组。
    /// 期望值取 HANDOVER 0.-6 节「出处与覆盖表」热膨胀一栏的**决定记录**（不是抄生产配方）；新加牌号必须先在那里定。
    /// 同时核按牌号的函数真的走这条映射（值 = 该曲线的值）。
    /// 第三轮（2026-09-15，Opus 5，复核阻断 1）：值与覆盖类别扩到**全部七个按牌号函数** × 3 个借用开关 ×
    /// {0, 20, 50, 99.5, 100, 500, 1000, 1100, 1500} °C，逐位；应变差全部牌号两两 × 温度两两。
    /// </summary>
    [Fact]
    public void 每个材料库牌号的膨胀映射逐条等于决定记录_函数真的走这条映射()
    {
        const ExpansionBorrowOption None = ExpansionBorrowOption.None, Same = ExpansionBorrowOption.SameBaseAlloy,
            Disp = ExpansionBorrowOption.DispersionStrengthened;
        var decided = new (string Grade, ExpansionBorrowOption Borrow, string? Curve, ExpansionLink Link)[]
        {
            ("Pt", None, "Pt", ExpansionLink.Direct),
            ("Pt-Rh/90-10", None, "PtRh10", ExpansionLink.Direct),
            ("Pt-Rh/80-20", None, "PtRh20", ExpansionLink.Direct),
            ("Tanaka-ZGS-Pt", None, "ZGSPt", ExpansionLink.Direct),
            ("Tanaka-ZGS-PtRh10", None, "ZGSPtRh10", ExpansionLink.Direct),
            ("Umicore-PtRh10", None, "PtRh10", ExpansionLink.Borrowed),
            ("Umicore-PtRh20", None, "PtRh20", ExpansionLink.Borrowed),
            ("FKS16/Pt", None, null, ExpansionLink.None),
            ("FKS16/Pt", Same, "Pt", ExpansionLink.Borrowed),
            ("FKS16/Pt", Disp, "ZGSPt", ExpansionLink.Borrowed),
            ("FKS16/PtRh-9010", None, null, ExpansionLink.None),
            ("FKS16/PtRh-9010", Same, "PtRh10", ExpansionLink.Borrowed),
            ("FKS16/PtRh-9010", Disp, "ZGSPtRh10", ExpansionLink.Borrowed),
            ("Pd", None, null, ExpansionLink.None),
            ("Ni", None, null, ExpansionLink.None),
            ("Cu", None, null, ExpansionLink.None),
        };
        Assert.True(decided.Select(d => d.Grade).ToHashSet().SetEquals(MaterialDb.All.Keys),
            "决定记录与材料库牌号不一一对应 —— 新加牌号先在 HANDOVER 覆盖表定「有／借／无」：缺 "
            + string.Join("、", MaterialDb.All.Keys.Except(decided.Select(d => d.Grade))));
        Assert.True(PtThermalExpansion.GradeMap.Select(m => m.Grade).ToHashSet().SetEquals(MaterialDb.All.Keys),
            "膨胀映射与材料库牌号不一一对应");
        Assert.True(PtThermalExpansion.GradeMap.Select(m => m.Grade).Distinct().Count() == PtThermalExpansion.GradeMap.Count, "膨胀映射里有重复牌号");

        (string Grade, ExpansionBorrowOption Borrow, string? Curve, ExpansionLink Link) Decided(string g, ExpansionBorrowOption b)
        {
            var d = decided.SingleOrDefault(x => x.Grade == g && x.Borrow == b);
            return d.Grade is null ? decided.Single(x => x.Grade == g && x.Borrow == None) : d;   // 借用开关只对 FKS16 起作用
        }

        int n = 0;
        foreach (string g in MaterialDb.All.Keys)
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
            {
                var d = Decided(g, b);
                var m = PtThermalExpansion.Resolve(g, b);
                Assert.True(m.CurveId == d.Curve && m.Link == d.Link,
                    $"{g}（{b}）：映射是 {m.CurveId ?? "无"}／{m.Link}，决定记录是 {d.Curve ?? "无"}／{d.Link}");
                Assert.False(string.IsNullOrWhiteSpace(m.Why));

                var v = PtThermalExpansion.MeanAlphaE6(g, 500, b);
                if (d.Curve is null)
                    Assert.True(v.Coverage == ExpansionCoverage.NoData && double.IsNaN(v.Value) && v.Link == ExpansionLink.None, $"{g}（{b}）应无数据：{v.Note}");
                else
                {
                    Assert.True(v.Curve == d.Curve && v.Link == d.Link, $"{g}（{b}）：函数走的是 {v.Curve}／{v.Link}，决定记录是 {d.Curve}／{d.Link}");
                    Assert.True(Bit(v.Value, PtThermalExpansion.Curves[d.Curve].MeanAlphaE6At(500)), $"{g}（{b}）：值不是曲线 {d.Curve} 的值");
                    Assert.True(v.IsBorrowed == (d.Link == ExpansionLink.Borrowed) && (!v.IsBorrowed || v.Note.Contains("借用")), $"{g}（{b}）：借用标签没随值带出：{v.Note}");
                }
                n++;
            }
        _o.WriteLine($"膨胀映射三元组核对 {n} 组（{MaterialDb.All.Count} 个牌号 × 借用开关）");

        // ── 复核阻断 1（2026-09-15 第三轮，Opus 5）：按牌号的七个函数的**值**逐位等于「决定记录那条曲线」的曲线层值。
        //    第二轮只核了 MeanAlphaE6 在 500 °C 一个点：LengthRatio／HotLength／Elongation／InstantAlphaE6／Strain／StrainDifference
        //    在 0–100 °C 退回裸多项式、或值取错曲线（如取纯铂），37 条门全绿〔复核注入 I3b1–I3b7、I6a–I6c〕。
        //    期望曲线取决定记录 d.Curve（不取返回值自报的 Curve）；LT/L0、热态长度、伸长照工作簿 Y、Z 公式
        //    （=V*(1+X*0.000001*U)、=Y−V）由曲线层平均 α 算；覆盖类别也按决定记录那条曲线的分档核。
        //    曲线层函数本身由「零到100度」那条门（0–100 °C 定义点内插、100 °C 起等于裸多项式）、X14 缓存与精确有理数独立守住。
        // ── 第四轮（2026-09-15，Opus 5，复核阻断）：覆盖类别的期望不再调生产 c.Classify（拿生产比生产，J1/J2/J4/R1b/R1c 全绿），
        //    改取门按定义独立算的 ExpansionCoverageSpec.Expected（输入只有图表引用、B4/B19/B5 与已核的系数）。
        var specs = ExpansionCoverageSpec.All.Value;
        double[] temps = { 0, 20, 50, 99.5, 100, 500, 1000, 1100, 1500 };
        const double L0 = 780;   // 任取的 0 °C 长度（mm）
        // 单点函数另加 −10–1510 °C 每 10 K（含表格温度栏外的无数据点）；应变差两两组合只用上面 9 个温度
        var pointTemps = temps.Concat(Enumerable.Range(-1, 153).Select(i => 10.0 * i)).Distinct().OrderBy(x => x).ToArray();
        int vals = 0, nod = 0, outOfRange = 0;
        foreach (string g in MaterialDb.All.Keys)
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
            {
                var d = Decided(g, b);
                foreach (double t in pointTemps)
                {
                    var got = new (string Fn, ExpansionValue V, bool Deriv)[]
                    {
                        ("MeanAlphaE6", PtThermalExpansion.MeanAlphaE6(g, t, b), false),
                        ("LengthRatio", PtThermalExpansion.LengthRatio(g, t, b), false),
                        ("Strain", PtThermalExpansion.Strain(g, t, b), false),
                        ("HotLength", PtThermalExpansion.HotLength(g, L0, t, b), false),
                        ("Elongation", PtThermalExpansion.Elongation(g, L0, t, b), false),
                        ("InstantAlphaE6", PtThermalExpansion.InstantAlphaE6(g, t, b), true),
                    };
                    if (d.Curve is null)
                    {
                        foreach (var (fn, v, _) in got)
                            Assert.True(v.Coverage == ExpansionCoverage.NoData && double.IsNaN(v.Value) && v.Link == ExpansionLink.None,
                                $"{fn}({g}, {t} °C, {b}) 决定记录无曲线，应无数据：{v.Coverage} {v.Value:R}");
                        nod++;
                        continue;
                    }
                    var c = PtThermalExpansion.Curves[d.Curve];
                    double a = c.MeanAlphaE6At(t);
                    double y = L0 * (1 + a * 0.000001 * t);   // 工作簿 Y 列 =V*(1+X*0.000001*U)
                    var expect = new Dictionary<string, double>
                    {
                        ["MeanAlphaE6"] = a,
                        ["LengthRatio"] = 1 + a * 0.000001 * t,
                        ["Strain"] = c.StrainAt(t),
                        ["HotLength"] = y,
                        ["Elongation"] = y - L0,                 // 工作簿 Z 列 =Y−V
                        ["InstantAlphaE6"] = c.InstantAlphaE6At(t),
                    };
                    foreach (var (fn, v, deriv) in got)
                    {
                        var cov = specs[d.Curve].Expected(t, deriv);
                        Assert.True(v.Curve == d.Curve && v.Link == d.Link && v.Coverage == cov,
                            $"{fn}({g}, {t} °C, {b})：走的是 {v.Curve}／{v.Link}／{v.Coverage}，决定记录是 {d.Curve}／{d.Link}／门按定义算 {cov}（{v.Note}）");
                        if (cov == ExpansionCoverage.NoData)
                        {
                            Assert.True(double.IsNaN(v.Value), $"{fn}({g}, {t} °C, {b}) 无数据却给了值 {v.Value:R}");
                            outOfRange++;
                        }
                        else
                            Assert.True(Bit(v.Value, expect[fn]),
                                $"{fn}({g}, {t} °C, {b}) = {v.Value:R} ≠ 决定记录曲线 {d.Curve} 的值 {expect[fn]:R}（差 {v.Value - expect[fn]:E3}）");
                        vals++;
                    }
                }
            }

        // StrainDifference：全部牌号两两 × 借用开关 × 温度两两，值 = 两端决定记录曲线的 StrainAt 之差，覆盖 = 两端较差，任一端无曲线 ⇒ NaN
        int diffs = 0;
        foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
            foreach (string ga in MaterialDb.All.Keys)
                foreach (string gb in MaterialDb.All.Keys)
                {
                    var da = Decided(ga, b); var db = Decided(gb, b);
                    foreach (double ta in temps)
                        foreach (double tb in temps)
                        {
                            var v = PtThermalExpansion.StrainDifference(ga, ta, gb, tb, b);
                            if (da.Curve is null || db.Curve is null)
                            {
                                Assert.True(v.Coverage == ExpansionCoverage.NoData && double.IsNaN(v.Value),
                                    $"StrainDifference({ga} {ta}, {gb} {tb}, {b})：一端决定记录无曲线，应无数据：{v.Coverage} {v.Value:R}");
                                diffs++;
                                continue;
                            }
                            var ca = PtThermalExpansion.Curves[da.Curve]; var cb = PtThermalExpansion.Curves[db.Curve];
                            var worse = ExpansionCoverageSpec.Worse(specs[da.Curve].Expected(ta, false), specs[db.Curve].Expected(tb, false));
                            var link = da.Link == ExpansionLink.Borrowed || db.Link == ExpansionLink.Borrowed ? ExpansionLink.Borrowed : ExpansionLink.Direct;
                            Assert.True(v.Coverage == worse && v.Link == link,
                                $"StrainDifference({ga} {ta}, {gb} {tb}, {b})：{v.Coverage}／{v.Link}，应为 {worse}／{link}");
                            double e = ca.StrainAt(ta) - cb.StrainAt(tb);
                            if (worse == ExpansionCoverage.NoData) Assert.True(double.IsNaN(v.Value));
                            else Assert.True(Bit(v.Value, e),
                                $"StrainDifference({ga} {ta}, {gb} {tb}, {b}) = {v.Value:R} ≠ 曲线 {da.Curve}、{db.Curve} 的 StrainAt 之差 {e:R}（差 {v.Value - e:E3}）");
                            diffs++;
                        }
                }
        _o.WriteLine($"按牌号函数值核对：六个单点函数 {vals} 个（其中有曲线但无数据 {outOfRange} 个，另 {nod} 组无曲线全 NaN），温度 {string.Join("／", temps)} °C 与 −10–1510 °C 每 10 K；"
                   + $"应变差 {diffs} 组，温度两两取 {string.Join("／", temps)} °C");
        Assert.True(vals > 0 && nod > 0 && diffs > 0 && outOfRange > 0, "反自证：有曲线、无曲线、有曲线但无数据、应变差四类都必须真的核到");

        var used = PtThermalExpansion.GradeMap.Where(m => m.CurveId is not null).Select(m => m.CurveId!).ToHashSet();
        foreach (var id in PtThermalExpansion.Curves.Keys.Except(used))
            Assert.False(MaterialDb.All.ContainsKey(id), $"只有膨胀数据的 {id} 进了材料库（无电阻率，选了求解会崩）");
        Assert.Equal("Pt", new DesignInputs().GradeName);   // 用户 09-15：以纯铂为设计标准
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<ExpansionCoverage, string> CovName = new()
    {
        [ExpansionCoverage.InRange] = "区间内", [ExpansionCoverage.InRangeNearEnd] = "区间内·端部",
        [ExpansionCoverage.EndDefinedPointInterp] = "端部·定义点内插", [ExpansionCoverage.Extrapolated] = "外推",
        [ExpansionCoverage.ShapeUntrusted] = "形状不可信", [ExpansionCoverage.NoData] = "无数据",
    };

    /// <summary>
    /// 复核阻断（2026-09-15 第四轮，Opus 5）：曲线层覆盖分档由门**按定义独立算**（<see cref="ExpansionCoverageSpec"/>，不调
    /// Classify／InUntrusted／UntrustedSegments／DataMinC／DataMaxC），9 条曲线 × 导数／非导数 × −1–1501 °C 每 0.5 K
    /// （外加 NaN、±∞）与生产 <c>ExpansionCurve.Classify</c> 逐点比；0–1500 °C 上各类别的分段（0.5 K 网格上的首、末温度，含两端）
    /// 对 HANDOVER 0.-6「曲线层覆盖分段」表的**决定记录**（与包络门同法：系数、图表引用或口径一变就红 —— 先改那张表再改这里）。
    /// 前提在门里自证：瞬时 α 导数在网格点上不为 0，且每个变号区间里的根离两侧网格点都 &gt; 1e-6 K（二分边界与精确根的差不会翻网格点）。
    /// </summary>
    [Fact]
    public void 曲线层覆盖分档由门按定义独立算_九条曲线导数与非导数全网格_分段端点等于决定记录()
    {
        var specs = ExpansionCoverageSpec.All.Value;
        Assert.True(specs.Keys.ToHashSet().SetEquals(PtThermalExpansion.Curves.Keys), "图表系列与代码曲线不一一对应");
        // 门的类别次序（数值越大越不可信）是口径的一部分，生产的应变差按枚举数值取较差 —— 两边必须同序
        for (int i = 0; i < ExpansionCoverageSpec.Rank.Length; i++)
            Assert.True((int)ExpansionCoverageSpec.Rank[i] == i, $"覆盖类别次序变了：第 {i} 档是 {ExpansionCoverageSpec.Rank[i]}（数值 {(int)ExpansionCoverageSpec.Rank[i]}）");
        Assert.True(Bit(PtThermalExpansion.TableMinC, specs["Pt"].TableMinC) && Bit(PtThermalExpansion.TableMaxC, specs["Pt"].TableMaxC)
                    && Bit(PtThermalExpansion.DefinedPointInterpBelowC, specs["Pt"].InterpBelowC), "代码的表格上下界／定义点内插分界与 B4、B19、B5 不同");

        // 决定记录：HANDOVER 0.-6「曲线层覆盖分段」表。键 = 曲线 + 「值」（平均 α、LT/L0、应变、热态长度、伸长）或「导数」（瞬时 α）
        var recorded = new Dictionary<string, string>
        {
            ["Pt 值"] = "端部·定义点内插 0–99.5；区间内 100–1000；外推 1000.5–1500",
            ["Pt 导数"] = "端部·定义点内插 0–99.5；区间内·端部 100–200；区间内 200.5–899.5；区间内·端部 900–1000；外推 1000.5–1500",
            ["PtRh10 值"] = "端部·定义点内插 0–99.5；区间内 100–1500",
            ["PtRh10 导数"] = "端部·定义点内插 0–99.5；区间内·端部 100–200；区间内 200.5–1399.5；区间内·端部 1400–1500",
            // 2026-09-18（Opus 5）：G18 改正后形状不可信段消失，PtRh20 与 PtRh10／Rh 同形
            ["PtRh20 值"] = "端部·定义点内插 0–99.5；区间内 100–1500",
            ["PtRh20 导数"] = "端部·定义点内插 0–99.5；区间内·端部 100–200；区间内 200.5–1399.5；区间内·端部 1400–1500",
            ["PtRh30 值"] = "形状不可信 0–174.5；区间内 175–1500",
            ["PtRh30 导数"] = "形状不可信 0–174.5；区间内·端部 175–200；区间内 200.5–1399.5；区间内·端部 1400–1500",
            ["PtRh95 值"] = "形状不可信 0–179；区间内 179.5–1500",
            ["PtRh95 导数"] = "形状不可信 0–179；区间内·端部 179.5–200；区间内 200.5–1399.5；区间内·端部 1400–1500",
            ["Rh 值"] = "端部·定义点内插 0–99.5；区间内 100–1500",
            ["Rh 导数"] = "端部·定义点内插 0–99.5；区间内·端部 100–200；区间内 200.5–1399.5；区间内·端部 1400–1500",
            ["PtAu5 值"] = "端部·定义点内插 0–99.5；区间内 100–879.5；形状不可信 880–1193；区间内 1193.5–1300；无数据 1300.5–1500",
            ["PtAu5 导数"] = "端部·定义点内插 0–99.5；区间内·端部 100–200；区间内 200.5–879.5；形状不可信 880–1193；区间内 1193.5–1199.5；区间内·端部 1200–1300；无数据 1300.5–1500",
            ["ZGSPt 值"] = "外推 0–499.5；区间内 500–1300；外推 1300.5–1500",
            ["ZGSPt 导数"] = "外推 0–499.5；区间内·端部 500–1000；区间内 1000.5–1199.5；区间内·端部 1200–1300；外推 1300.5–1500",
            ["ZGSPtRh10 值"] = "形状不可信 0–315.5；外推 316–499.5；区间内 500–1300；外推 1300.5–1500",
            ["ZGSPtRh10 导数"] = "形状不可信 0–315.5；外推 316–499.5；区间内·端部 500–1000；区间内 1000.5–1199.5；区间内·端部 1200–1300；外推 1300.5–1500",
        };
        Assert.True(recorded.Count == 2 * specs.Count, "决定记录应每条曲线值、导数各一行");

        int points = 0; var seen = new HashSet<ExpansionCoverage>();
        foreach (var (id, spec) in specs)
        {
            var curve = PtThermalExpansion.Curves[id];

            // 前提自证：根不贴网格点
            int jMax = (int)Math.Round(2 * spec.TableMaxC), roots = 0;
            for (int j = 0; j <= jMax; j++)
            {
                int sj = spec.SlopeSign(0.5 * j);
                Assert.True(sj != 0, $"{id}：瞬时 α 导数在 {0.5 * j} °C 恰为 0，网格点上的类别不确定");
                if (j == 0 || sj == spec.SlopeSign(0.5 * (j - 1))) continue;
                roots++;
                var d6 = new Q(1, 1000000);
                Assert.True(spec.SlopeSignExact(new Q(j - 1, 2) + d6) == spec.SlopeSign(0.5 * (j - 1)) && spec.SlopeSignExact(new Q(j, 2) - d6) == sj,
                    $"{id}：瞬时 α 导数的根离网格点 {0.5 * (j - 1)} 或 {0.5 * j} °C 不到 1e-6 K");
            }

            foreach (bool deriv in new[] { false, true })
            {
                var runs = new List<(ExpansionCoverage Cov, double Lo, double Hi)>();
                var probe = Enumerable.Range(-2, jMax + 5).Select(j => 0.5 * j).Concat(new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity });
                foreach (double t in probe)
                {
                    var exp = spec.Expected(t, deriv);
                    var got = curve.Classify(t, deriv, out string note);
                    Assert.True(got == exp, $"{id} {t} °C（{(deriv ? "导数类量" : "值")}）：生产分档 {got}（{note}），门按定义算 {exp}");
                    seen.Add(exp); points++;
                    if (!(t >= spec.TableMinC && t <= spec.TableMaxC)) continue;
                    if (runs.Count > 0 && runs[^1].Cov == exp) runs[^1] = (exp, runs[^1].Lo, t);
                    else runs.Add((exp, t, t));
                }
                string key = $"{id} {(deriv ? "导数" : "值")}";
                string text = string.Join("；", runs.Select(r => $"{CovName[r.Cov]} {r.Lo:0.#}–{r.Hi:0.#}"));
                _o.WriteLine($"{key}：{text}");
                Assert.True(recorded.TryGetValue(key, out var rec) && rec == text,
                    $"{key}：门按定义算的分段与 HANDOVER 覆盖表的决定记录不同 —— 算得「{text}」，记录「{rec}」");
            }
            _o.WriteLine($"{id}：{spec.Order} 阶，数据 {spec.DataC[0]:0}–{spec.DataC[^1]:0} °C（端部间隔到 {spec.DataC[1]:0} 与自 {spec.DataC[^2]:0} °C），瞬时 α 导数变号 {roots} 处");
        }
        _o.WriteLine($"门按定义独立核对 {points} 点（9 条曲线 × 值／导数 × −1–1501 °C 每 0.5 K 与 NaN、±∞）");
        Assert.True(seen.SetEquals(ExpansionCoverageSpec.Rank), "反自证：六个覆盖类别都必须在网格上真的核到：缺 " + string.Join("、", ExpansionCoverageSpec.Rank.Except(seen)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 复核 minor（2026-09-15 第四轮，Opus 5）：瞬时 α 在 100 °C 的台阶（左 = 100 °C 平均 α，右 = 多项式导数）逐曲线量级由门用精确有理数算，
    /// 百分数（一位小数）对 HANDOVER 0.-6「瞬时 α 的 100 °C 台阶」表的决定记录；0–200 °C 的按牌号 InstantAlphaE6 注记必须点名该曲线的台阶两侧值与百分数，
    /// 200 °C 以上不带。
    /// </summary>
    [Fact]
    public void 瞬时α在100度的台阶_逐曲线量级由精确有理数算_零到200度的注记点名台阶大小()
    {
        var recorded = new Dictionary<string, string>
        {
            // PtRh20 2026-09-18 由 +22.1 改为 +19.3（G18 改正后重算的系数，Opus 5）
            ["Pt"] = "+2.3", ["PtRh10"] = "+0.4", ["PtRh20"] = "+19.3", ["PtRh30"] = "−1.7", ["PtRh95"] = "−12.7",
            ["Rh"] = "−1.4", ["PtAu5"] = "−2.8", ["ZGSPt"] = "+1.2", ["ZGSPtRh10"] = "−1.1",
        };
        var hundred = new Q(100, 1);
        var steps = new Dictionary<string, (double L, double R, double P)>();
        foreach (var c in PtThermalExpansion.Curves.Values)
        {
            var q = c.Coeffs.Select(Q.FromDouble).ToArray();
            Q left = ExactPolyFit.Eval(q, hundred);
            Q right = ExactPolyFit.Eval(Enumerable.Range(0, q.Length).Select(k => q[k] * new Q(k + 1, 1)).ToArray(), hundred);
            Q pct = (right / left - Q.One) * new Q(100, 1);
            var (l, r, p) = c.InstantAlphaStepAt100;
            Assert.True(Math.Abs(l - left.ToDouble()) <= 1e-12 * Math.Abs(l) && Math.Abs(r - right.ToDouble()) <= 1e-12 * Math.Abs(r)
                        && Math.Abs(p - pct.ToDouble()) <= 1e-9, $"{c.Id}：台阶 {l:R}→{r:R}（{p:R} %）与精确有理数 {left.ToDouble():R}→{right.ToDouble():R}（{pct.ToDouble():R} %）不符");
            Assert.True(Bit(c.InstantAlphaE6At(99.999), l) && Bit(c.InstantAlphaE6At(100), r), $"{c.Id}：台阶两侧不是函数真正返回的值");
            string pText = pct.ToDouble().ToString("+0.0;−0.0", CultureInfo.InvariantCulture);
            _o.WriteLine($"{c.Id}：100 °C 以下瞬时 α {left.ToDouble():0.0000}，100 °C 起 {right.ToDouble():0.0000}，台阶 {pct.ToDouble():+0.000;−0.000} %");
            Assert.True(recorded.TryGetValue(c.Id, out var rec) && rec == pText, $"{c.Id}：台阶 {pText} % 与 HANDOVER 表的决定记录 {rec} % 不同");
            steps[c.Id] = (left.ToDouble(), right.ToDouble(), pct.ToDouble());
        }

        int named = 0;
        foreach (string g in MaterialDb.All.Keys)
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
                foreach (double t in new[] { 0, 20, 99.5, 100, 150, 200, 200.5, 500, 1000 })
                {
                    var v = PtThermalExpansion.InstantAlphaE6(g, t, b);
                    if (!v.HasValue) { Assert.DoesNotContain("台阶", v.Note); continue; }
                    var (l, r, p) = steps[v.Curve];
                    bool must = t <= 200;
                    bool has = v.Note.Contains("台阶") && v.Note.Contains($"{l:0.0000}") && v.Note.Contains($"{r:0.0000}")
                               && v.Note.Contains(p.ToString("+0.0;−0.0", CultureInfo.CurrentCulture) + " %");
                    Assert.True(must ? has : !v.Note.Contains("台阶"), $"InstantAlphaE6({g}, {t} °C, {b})：{(must ? "0–200 °C 注记应点名台阶" : "200 °C 以上不该带台阶注记")}：{v.Note}");
                    if (must) named++;
                }
        _o.WriteLine($"注记点名台阶 {named} 处；例：{PtThermalExpansion.InstantAlphaE6("Pt-Rh/80-20", 150).Note}");
        Assert.True(named > 0, "反自证：点名台阶的注记必须真的核到");
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 复核 minor（2026-09-15 第四轮，Opus 5）：「Value 为 NaN ⇔ Coverage == NoData」对**全部单点函数**成立 ——
    /// 第三轮只对 MeanAlphaE6 全网格核，HotLength("Pt", NaN, 500) 返回 NaN 却标区间内。L0 取 NaN、±∞、0、负数，
    /// 温度取 NaN、±∞、表内外，全部牌号 × 借用开关；应变差同样核。
    /// 2026-09-16 第五轮（Opus 5，核验员小备注）：不变式收紧为「Value 非有限 ⇔ NoData」（无数据 ⇒ 值 NaN），L0 = ±∞ 也必须无数据
    /// —— 第四轮 HotLength("Pt", +∞, 500) 给 +∞ 且标有值。方法名沿用（HANDOVER 注入表引用它）。
    /// </summary>
    [Fact]
    public void 全部单点函数值为NaN当且仅当无数据_L0不是有限数时也守住()
    {
        double[] temps = { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -10, -0.5, 0, 20, 99.5, 100, 500, 1000, 1000.5, 1300.5, 1500, 1500.5, 1600 };
        double[] l0s = { 780, 0, -780, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        int n = 0, nanL0InTable = 0;
        // 第五轮（2026-09-16，Opus 5，核验员小备注）：不变式收紧为「有值 ⇔ 值有限；无数据 ⇒ 值 NaN」—— 第四轮 HotLength("Pt", +∞, 500)
        // 返回 +∞ 且 HasValue = true，字面守住「NaN ⇔ 无数据」却不是可用值。L0 = ±∞ 的组合与 NaN 一样必须无数据。
        int infL0InTable = 0;
        void Check(string what, ExpansionValue v)
        {
            Assert.True(v.HasValue == double.IsFinite(v.Value) && (v.HasValue || double.IsNaN(v.Value)),
                $"{what}：值 {v.Value:R} 与覆盖 {v.Coverage} 不一致（有值 ⇔ 值有限；无数据 ⇒ 值 NaN）（{v.Note}）");
            n++;
        }
        foreach (string g in MaterialDb.All.Keys)
            foreach (var b in Enum.GetValues<ExpansionBorrowOption>())
                foreach (double t in temps)
                {
                    Check($"MeanAlphaE6({g}, {t}, {b})", PtThermalExpansion.MeanAlphaE6(g, t, b));
                    Check($"LengthRatio({g}, {t}, {b})", PtThermalExpansion.LengthRatio(g, t, b));
                    Check($"Strain({g}, {t}, {b})", PtThermalExpansion.Strain(g, t, b));
                    Check($"InstantAlphaE6({g}, {t}, {b})", PtThermalExpansion.InstantAlphaE6(g, t, b));
                    bool tableValue = PtThermalExpansion.Strain(g, t, b).HasValue;
                    foreach (double l0 in l0s)
                    {
                        var hl = PtThermalExpansion.HotLength(g, l0, t, b);
                        var el = PtThermalExpansion.Elongation(g, l0, t, b);
                        Check($"HotLength({g}, L0 {l0}, {t}, {b})", hl);
                        Check($"Elongation({g}, L0 {l0}, {t}, {b})", el);
                        if (!double.IsFinite(l0))
                        {
                            Assert.True(!hl.HasValue && !el.HasValue && double.IsNaN(hl.Value) && double.IsNaN(el.Value),
                                $"{g} L0 = {l0}、{t} °C：热态长度／伸长必须无数据且值 NaN：{hl.Coverage} {hl.Value:R}／{el.Coverage} {el.Value:R}");
                            if (tableValue && double.IsNaN(l0)) nanL0InTable++;
                            if (tableValue && double.IsInfinity(l0)) { infL0InTable++; Assert.Contains("不是有限数", hl.Note); Assert.Contains("不是有限数", el.Note); }
                        }
                        if (tableValue && double.IsFinite(l0))
                            Assert.True(hl.HasValue && el.HasValue, $"{g} L0 = {l0}、{t} °C：有限的 L0 不该被标无数据");
                    }
                    foreach (double tb in new[] { double.NaN, 20, 1000, 1500.5 })
                        Check($"StrainDifference({g}, {t}, Pt, {tb}, {b})", PtThermalExpansion.StrainDifference(g, t, "Pt", tb, b));
                }
        _o.WriteLine($"值有限 ⇔ 有数据：核对 {n} 个返回值；其中 L0 = NaN 而温度在表内有值的 {nanL0InTable} 组（第三轮这些标成有数据）、"
                   + $"L0 = ±∞ 的 {infL0InTable} 组（第四轮 HotLength 给 ±∞ 且标有值）");
        Assert.True(nanL0InTable > 0 && infL0InTable > 0, "反自证：L0 = NaN 与 L0 = ±∞ 且温度有数据的组合都必须真的核到");
        var probe = PtThermalExpansion.HotLength("Pt", double.NaN, 500);
        Assert.True(probe.Coverage == ExpansionCoverage.NoData && double.IsNaN(probe.Value) && probe.Curve == "Pt", probe.Note);
        var pInf = PtThermalExpansion.HotLength("Pt", double.PositiveInfinity, 500);
        Assert.True(pInf.Coverage == ExpansionCoverage.NoData && double.IsNaN(pInf.Value) && !pInf.HasValue && pInf.Note.Contains("不是有限数"), $"{pInf.Value:R} {pInf.Coverage} {pInf.Note}");
        var nInf = PtThermalExpansion.Elongation("Pt", double.NegativeInfinity, 500);
        Assert.True(nInf.Coverage == ExpansionCoverage.NoData && double.IsNaN(nInf.Value) && !nInf.HasValue, $"{nInf.Value:R} {nInf.Coverage} {nInf.Note}");
        var zInf = PtThermalExpansion.HotLength("Pt", double.NegativeInfinity, 0);   // 0 °C：α·T = 0，−∞·1 仍非有限
        Assert.True(zInf.Coverage == ExpansionCoverage.NoData && double.IsNaN(zInf.Value), $"{zInf.Value:R} {zInf.Coverage} {zInf.Note}");
        var fin = PtThermalExpansion.HotLength("Pt", 780, 500);
        Assert.True(fin.HasValue && double.IsFinite(fin.Value) && fin.Coverage == ExpansionCoverage.InRange, "反自证：有限 L0 照常有值");
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// 登记（2026-09-15 第二轮，Opus 5）：膨胀工作簿有外部链接（辊上热态实验 GSF44 的 xlsm）。
    /// 门核：链接目标就是登记的那个文件；表内没有任何公式、定义名引用外部工作簿 —— 本档的数一个都不来自它。
    /// </summary>
    [Fact]
    public void 膨胀工作簿的外部链接已登记_表内没有公式引用外部工作簿()
    {
        using var book = Book();
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        var linkRels = book.PartNames.Where(p => p.StartsWith("xl/externalLinks/_rels/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(linkRels);
        var files = new HashSet<string>();
        foreach (var p in linkRels)
            foreach (var rel in book.Part(p).Root!.Elements(pr + "Relationship").Where(e => (string?)e.Attribute("TargetMode") == "External"))
            {
                string target = Uri.UnescapeDataString((string)rel.Attribute("Target")!);
                string file = target.Split('/', '\\').Last();
                files.Add(file);
                _o.WriteLine($"{p}：{target}");
            }
        Assert.True(files.SetEquals(new[] { PtThermalExpansion.ExternalLinkFile }),
            $"外部链接目标与登记的不同：{string.Join("、", files)}（登记 {PtThermalExpansion.ExternalLinkFile}）");

        var sh = book.Sheet(PtThermalExpansion.SheetName);
        int formulas = 0;
        foreach (var c in sh.Cells.Values.Where(c => c.FormulaText is not null))
        {
            formulas++;
            Assert.False(c.FormulaText!.Contains('['), $"{c.Ref} 公式引用了外部工作簿：{c.FormulaText}");
        }
        Assert.True(formulas > 0, "反自证：一个公式都没扫到");
        var wb = book.Part("xl/workbook.xml");
        foreach (var dn in wb.Descendants().Where(e => e.Name.LocalName == "definedName"))
            Assert.False(dn.Value.Contains('['), $"定义名 {(string?)dn.Attribute("name")} 引用了外部工作簿：{dn.Value}");
        _o.WriteLine($"表内 {formulas} 个公式文本、{wb.Descendants().Count(e => e.Name.LocalName == "definedName")} 个定义名，都不引用外部工作簿");
    }

    /// <summary>
    /// xlsx 读取器（2026-09-15 第二轮，Opus 5）：有公式却没缓存值的格**直接抛**，不许当空格读 ——
    /// 当空格读，门会把它当缺测放过，CreepXlsxRaw 会在那里截断整行。
    /// </summary>
    [Fact]
    public void xlsx读取器_有公式没缓存值的格直接抛_不当空格()
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var doc = new XDocument(new XElement(s + "worksheet", new XElement(s + "sheetData", new XElement(s + "row",
            new XElement(s + "c", new XAttribute("r", "A1"), new XElement(s + "f", "1+1")),
            new XElement(s + "c", new XAttribute("r", "B1"), new XElement(s + "f", "1+1"), new XElement(s + "v", "2")),
            new XElement(s + "c", new XAttribute("r", "C1"))))));
        var sh = new XlsxSheet(null!, "自测", doc);
        Assert.Throws<InvalidOperationException>(() => sh.IsBlank("A1"));
        Assert.Throws<InvalidOperationException>(() => sh.Num("A1"));
        Assert.Throws<InvalidOperationException>(() => sh.Cell("A1"));
        Assert.True(Bit(sh.Num("B1"), 2) && sh.IsBlank("C1") && sh.IsBlank("D1"), "有缓存的公式格、真空格、不存在的格照常读");
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// G18 的改正与出处（2026-09-18，Opus 5；用户原话「改」）。
    ///
    /// 工作簿 G18（PtRh20 @1400 °C）原写 1.0157，Platinum Metals Rev., 1960, 4 (4), 139「20 % Rh」栏印的是 1.0152
    /// （Δ = +5.0e-4，是全表 96 格里唯一超出「4 位小数舍入」能解释的一格）。这条门每次**直接读 xlsx**：
    ///   · G18 的存储值必须是 1.0152（论文值），不许是 1.0157，也不许是别的第五位；
    ///   · H18 的 α 缓存必须与 G18 按工作簿公式算出来的一致（改了值不改缓存 = 自相矛盾的工作簿）；
    ///   · 图表 PtRh20 系列 y 缓存的 1400 °C 那点必须等于 H18（图表缓存也是同一个数的拷贝）；
    ///   · G18 必须带批注，写清日期、出处与改前改后（xl/comments1.xml）；
    ///   · 曲线与牌号映射的注记必须写出文献出处，纯铂那条必须写明「Holborn 1919 转载、止于 1000 °C」。
    /// 反自证：论文这一格与工作簿改前那一格必须真的不同，否则这条门在空转。
    /// </summary>
    [Fact]
    public void G18按文献改正_工作簿的值与缓存与图表缓存一致_批注与出处注记齐全()
    {
        const double paper = 1.0152, transcribed = 1.0157;   // 论文 p.139「20 % Rh」1400 °C ／ 工作簿改前
        Assert.True(!Bit(paper, transcribed), "反自证：论文值与改前值必须不同");

        using var book = Book();
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        double g18 = sh.Num("G18");
        Assert.True(Bit(g18, paper), $"G18 = {g18:R}，应为论文 p.139 的 {paper:R}（改前是 {transcribed:R}）");
        Assert.True(Bit(sh.Num("B18"), 1400), "G18 那一行不是 1400 °C");

        double h18 = sh.Num("H18");
        double byFormula = PtThermalExpansion.TableAlphaE6(1400, g18);
        Assert.True(Bit(h18, byFormula), $"H18 的 α 缓存 {h18:R} ≠ 按 G18 与工作簿公式算的 {byFormula:R} —— 改了值没重算");

        var ser = XlsxChart.Series(book.Part("xl/charts/chart1.xml"))
            .Single(x => XlsxChart.Expand(x.YRef).Cells.Contains("H18"));
        int at = XlsxChart.Expand(ser.YRef).Cells.IndexOf("H18");
        Assert.True(Bit(ser.YCache[at], h18), $"图表 PtRh20 系列 y 缓存 idx {at} = {ser.YCache[at]:R} ≠ H18 {h18:R}");

        // 代码里的表与曲线数据点也必须是这个数（其余门逐格核，这里只钉 1400 °C 这一格）
        var col = PtThermalExpansion.Table.Single(t => t.Alloy == "PtRh20");
        Assert.True(Bit(col.LtOverL0[Array.IndexOf(PtThermalExpansion.TableTempsC, 1400.0)], paper), "代码 LT/L0 表的 PtRh20 1400 °C 不是论文值");
        var curve = PtThermalExpansion.Curves["PtRh20"];
        Assert.True(Bit(curve.DataAlphaE6[Array.IndexOf(curve.DataTempsC.ToArray(), 1400.0)], byFormula), "曲线数据点的 1400 °C α 不是按改正值算的");

        // 批注
        Assert.True(book.HasPart("xl/comments1.xml"), "G18 的改正没有留批注（xl/comments1.xml 不存在）");
        string note = string.Concat(book.Part("xl/comments1.xml").Descendants()
            .Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        _o.WriteLine($"G18 = {g18:R}（论文 p.139）；H18 α 缓存 {h18:R}；批注：{note}");
        foreach (string must in new[] { "2026-09-18", "JM 1960", "p.139", "1.0157", "1.0152", "Opus 5" })
            Assert.True(note.Contains(must, StringComparison.Ordinal), $"G18 批注里没有「{must}」：{note}");

        // 出处注记
        Assert.False(string.IsNullOrWhiteSpace(PtThermalExpansion.PublishedTableSource));
        foreach (string must in new[] { "Platinum Metals Rev.", "1960", "p.139", "Holborn", "1919", "1000 °C" })
            Assert.True(PtThermalExpansion.PublishedTableSource.Contains(must, StringComparison.Ordinal),
                $"PublishedTableSource 里没有「{must}」");
        foreach (string must in new[] { "Holborn", "1919", "p.139", "外推" })
            Assert.True(PtThermalExpansion.PurePtColumnSource.Contains(must, StringComparison.Ordinal),
                $"PurePtColumnSource 里没有「{must}」");
        foreach (string id in new[] { "PtRh10", "PtRh20", "PtRh30", "PtRh95", "Rh" })
            Assert.True(PtThermalExpansion.Curves[id].DataSource.Contains("Platinum Metals Rev.", StringComparison.Ordinal),
                $"曲线 {id} 的数据出处没写文献：{PtThermalExpansion.Curves[id].DataSource}");
        Assert.True(PtThermalExpansion.Curves["Pt"].DataSource.Contains("Holborn", StringComparison.Ordinal)
                    && PtThermalExpansion.Curves["Pt"].DataSource.Contains("止于 1000 °C", StringComparison.Ordinal),
            "纯铂曲线的数据出处必须写明 Holborn 1919 转载、止于 1000 °C：" + PtThermalExpansion.Curves["Pt"].DataSource);
        foreach (string g in new[] { "Pt", "Pt-Rh/90-10", "Pt-Rh/80-20" })
            Assert.True(PtThermalExpansion.Resolve(g).Why.Contains("p.139", StringComparison.Ordinal),
                $"牌号 {g} 的映射注记没写文献页码：{PtThermalExpansion.Resolve(g).Why}");
        Assert.True(PtThermalExpansion.Resolve("Pt-Rh/80-20").Why.Contains("1.0157", StringComparison.Ordinal)
                    && PtThermalExpansion.Resolve("Pt-Rh/80-20").Why.Contains("1.0152", StringComparison.Ordinal),
            "Pt-Rh/80-20 的映射注记要把改前改后都写出来：" + PtThermalExpansion.Resolve("Pt-Rh/80-20").Why);
        Assert.Equal(ExpansionCoeffOrigin.RecomputedLabelStale, curve.Origin);
    }
}

/// <summary>
/// 门自己按定义算的膨胀覆盖类别（2026-09-15 第四轮，Opus 5，复核阻断：第三轮映射门与应变差门的覆盖期望直接调生产
/// ExpansionCurve.Classify，注入 J1／J2／J4／R1b／R1c 全绿）。**不调任何生产分档函数**（Classify、InUntrusted、UntrustedSegments、
/// DataMinC／DataMaxC 都不用），输入只取已由其他门从 xlsx 核过的量：
///   · 每条曲线的数据温度与阶数：直接读图表 chart1 的系列 x／y 引用（x 按 y 个数配对）与 trendline order
///     （图表那条门核代码数据点、阶数与之逐位相同）；
///   · 趋势线系数：曲线的 Coeffs（X14 字面量／标签文本／精确复算 7 位三条门逐位核过）；
///   · 表格温度栏上下界：直接读 B4、B19；定义点内插的分界：直接读 B5（表格曲线的数据从这一行起）。
/// 规则（HANDOVER 0.-6「覆盖规则」，逐条）：
///   ① T 非数、&lt; B4 或 &gt; B19 ⇒ 无数据；
///   ② B4 ≤ T &lt; B5 ⇒「端部·定义点内插」与「B5 那个值（按非导数类量）」的类别取较差；
///   ③ 数据区间 [最小, 最大] 外：阶数 ≥ 4 ⇒ 无数据，否则外推；
///   ④ 区间内：导数类量在「排序后第 2 个数据点及以下、倒数第 2 个及以上」（含端点）⇒ 区间内·端部，否则区间内；
///   ⑤ 不是无数据、且瞬时 α 的导数 Σ k(k+1)·c_k·T^(k−1) 在 T 处**精确为负** ⇒ 与「形状不可信」取较差。
/// 类别次序（较差 = 靠后）由门自己的 <see cref="Rank"/> 定，门另核生产枚举数值与它同序。
/// </summary>
internal sealed class ExpansionCoverageSpec
{
    /// <summary>门的类别次序：越靠后越不可信。</summary>
    public static readonly ExpansionCoverage[] Rank =
    {
        ExpansionCoverage.InRange, ExpansionCoverage.InRangeNearEnd, ExpansionCoverage.EndDefinedPointInterp,
        ExpansionCoverage.Extrapolated, ExpansionCoverage.ShapeUntrusted, ExpansionCoverage.NoData,
    };

    public static ExpansionCoverage Worse(ExpansionCoverage a, ExpansionCoverage b)
        => Array.IndexOf(Rank, a) >= Array.IndexOf(Rank, b) ? a : b;

    /// <summary>全部 9 条曲线（读一次 xlsx，门之间共用）。</summary>
    public static readonly Lazy<Dictionary<string, ExpansionCoverageSpec>> All = new(ReadAll);

    public string Id { get; }
    public int Order { get; }
    /// <summary>数据温度 °C，升序。</summary>
    public double[] DataC { get; }
    public double TableMinC { get; }
    public double TableMaxC { get; }
    public double InterpBelowC { get; }

    private readonly Q[] _slope;          // 瞬时 α 的导数 Σ_i s_i·T^i，s_i = (i+1)(i+2)·c_{i+1}，精确有理数
    private readonly sbyte[] _gridSign;   // T = j/2（j = 0..2·B19）处的精确符号

    private ExpansionCoverageSpec(string id, int order, double[] dataC, IReadOnlyList<double> coeffs, double tMin, double tMax, double interpBelow)
    {
        if (coeffs.Count != order + 1) throw new InvalidOperationException($"{id}：代码系数 {coeffs.Count} 个，图表趋势线 {order} 阶");
        if (dataC.Length < 3) throw new InvalidOperationException($"{id}：数据点不到 3 个，端部间隔无从定义");
        Id = id; Order = order; DataC = dataC; TableMinC = tMin; TableMaxC = tMax; InterpBelowC = interpBelow;
        var q = coeffs.Select(Q.FromDouble).ToArray();
        _slope = Enumerable.Range(0, order).Select(i => q[i + 1] * new Q((i + 1) * (i + 2), 1)).ToArray();

        // 网格上判号用整数：系数乘公分母 L（都是二进分数），p(j/2)·2^(n−1) = Σ m_i·j^i·2^(n−1−i)，符号与 p(j/2) 相同
        BigInteger lcm = BigInteger.One;
        foreach (var s in _slope) lcm = lcm / BigInteger.GreatestCommonDivisor(lcm, s.D) * s.D;
        var m = _slope.Select(s => s.N * (lcm / s.D)).ToArray();
        int n = m.Length;
        int jMax = (int)Math.Round(2 * tMax);
        _gridSign = new sbyte[jMax + 1];
        for (int j = 0; j <= jMax; j++)
        {
            BigInteger acc = BigInteger.Zero, jp = BigInteger.One;
            for (int i = 0; i < n; i++) { acc += m[i] * jp * BigInteger.Pow(2, n - 1 - i); jp *= j; }
            _gridSign[j] = (sbyte)acc.Sign;
        }
    }

    private static Dictionary<string, ExpansionCoverageSpec> ReadAll()
    {
        using var book = XlsxBook.OpenInRepo(PtThermalExpansion.WorkbookFile);
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        double tMin = sh.Num("B4"), tMax = sh.Num("B19"), interp = sh.Num("B5");
        var d = new Dictionary<string, ExpansionCoverageSpec>(StringComparer.Ordinal);
        foreach (var s in XlsxChart.Series(book.Part("xl/charts/chart1.xml")))
        {
            string id = Regex.Replace(Regex.Replace(sh.Str(XlsxChart.Expand(s.NameRef).Cells.Single()), @"\s+", ""), @"\(α\)$|%$", "");
            var (_, xc) = XlsxChart.Expand(s.XRef);
            var (_, yc) = XlsxChart.Expand(s.YRef);
            var data = xc.Take(yc.Count).Select(sh.Num).OrderBy(x => x).ToArray();
            int order = s.TrendOrder ?? throw new InvalidOperationException($"{id}：图表趋势线没有阶数");
            d.Add(id, new ExpansionCoverageSpec(id, order, data, PtThermalExpansion.Curves[id].Coeffs, tMin, tMax, interp));
        }
        return d;
    }

    /// <summary>瞬时 α 导数在 T 处的精确符号（0.5 K 网格查表，其余精确有理数现算）。</summary>
    public int SlopeSign(double t)
    {
        double j2 = 2 * t;
        if (j2 >= 0 && j2 < _gridSign.Length && j2 == Math.Floor(j2)) return _gridSign[(int)j2];
        return SlopeSignExact(Q.FromDouble(t));
    }

    public int SlopeSignExact(Q t) => ExactPolyFit.Eval(_slope, t).Sign;

    /// <summary>按定义的覆盖类别（规则见类说明）。derivative = 瞬时 α。</summary>
    public ExpansionCoverage Expected(double t, bool derivative)
    {
        if (double.IsNaN(t) || t < TableMinC || t > TableMaxC) return ExpansionCoverage.NoData;
        if (t < InterpBelowC) return Worse(ExpansionCoverage.EndDefinedPointInterp, Expected(InterpBelowC, false));
        ExpansionCoverage cov;
        if (t >= DataC[0] && t <= DataC[^1])
            cov = derivative && (t <= DataC[1] || t >= DataC[^2]) ? ExpansionCoverage.InRangeNearEnd : ExpansionCoverage.InRange;
        else
            cov = Order >= 4 ? ExpansionCoverage.NoData : ExpansionCoverage.Extrapolated;
        if (cov != ExpansionCoverage.NoData && SlopeSign(t) < 0) cov = Worse(cov, ExpansionCoverage.ShapeUntrusted);
        return cov;
    }
}
