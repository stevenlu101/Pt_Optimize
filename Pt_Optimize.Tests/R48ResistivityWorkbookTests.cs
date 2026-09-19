using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 电阻率的门：**每次直接读《鉑金電氣計算.xlsx》**核对系数、测试值、计算值缓存、SSE 缓存与覆盖查询。2026-09-15，Opus 5。
///
/// 容差（改前由数值把关写死）：系数／测试值逐位；「計算值」缓存 ResistivityOhmM·1e8 相对 ≤ 4ε
/// （实测 65/98 逐位，最大 2.9e-16，1e-8 乘了再除回来）；Materials.PtResistivity 与 MaterialDb "Pt" 逐位。
/// ⚠ 工作簿 α、β 是 Solver 近似解不是 SSE 最小 ⇒ 只复现 SSE 缓存，不按算法复算 α、β。
/// </summary>
public class R48ResistivityWorkbookTests
{
    private readonly ITestOutputHelper _o;
    public R48ResistivityWorkbookTests(ITestOutputHelper o) => _o = o;

    private const double Eps = 2.220446049250313e-16;
    private static bool Bit(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static XlsxBook Book() => XlsxBook.OpenInRepo(PtResistivityData.WorkbookFile);
    private static string Label(XlsxSheet sh, string cell) => Regex.Replace(sh.Str(cell).Trim(), @"\s+", "/");

    [Fact]
    public void 系数ρ0与T0逐位等于单元格_每个材料库牌号的电阻率都来自工作簿某一行()
    {
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        var T = PtResistivityData.GridTempsC;
        for (int i = 0; i < T.Length; i++)
            Assert.True(Bit(sh.Num(PtResistivityData.GridCols[i] + "22"), T[i]), $"{PtResistivityData.GridCols[i]}22 温度与代码不同");

        foreach (var row in PtResistivityData.Rows)
        {
            Assert.Equal(row.DataGrade, Label(sh, row.WorkbookLabelCell));
            Assert.Equal("E" + row.TestRow, row.WorkbookLabelCell);
            var g = MaterialDb.Get(row.DataGrade);
            Assert.Equal(row.DataGrade, g.ResistivityFrom);
            int r = row.TestRow;
            Assert.True(Bit(sh.Num("X" + r), g.Alpha), $"X{r} α 与代码不同");
            Assert.True(Bit(sh.Num("Y" + r), g.Beta), $"Y{r} β 与代码不同");
            Assert.True(Bit(sh.Num("AA" + r), g.T0C), $"AA{r} T0 与代码不同");
            // Z 列 ρ0 = 公式指向 T0 那一栏的测试值格
            string z = sh.Formula("Z" + r) ?? throw new InvalidOperationException($"Z{r} 没有公式");
            var m = Regex.Match(z, @"^([A-Z]+)" + r + "$");
            Assert.True(m.Success, $"Z{r} 公式「{z}」不是指向本行一个测试值格");
            int col = Array.IndexOf(PtResistivityData.GridCols, m.Groups[1].Value);
            Assert.True(col >= 0, $"Z{r} 指向的格不在 G:W");
            bool singlePointPlaceholder = row.Values.Count(v => !double.IsNaN(v)) == 1 && g.Alpha == 0 && g.Beta == 0;
            if (singlePointPlaceholder)
            {
                // 工作簿自带的不一致（物理把关 2026-09-15 点名）：Cu 的 ρ0 取自 20 °C 栏（H44）而 AA44 的 T0 = 0；
                // α、β 是字面 0（缺测当 0），所以式子恒等于 1.7，T0 不起作用。只许这一种情形豁免，且必须正指着那唯一的测试值。
                Assert.False(double.IsNaN(row.Values[col]), $"Z{r} 没指着该行唯一的测试值");
                _o.WriteLine($"{row.DataGrade}：Z{r} 指向 {m.Groups[1].Value}{r}（{T[col]} °C）而 AA{r} T0 = {g.T0C} —— α、β 字面 0，式子恒为常数，登记为缺测当 0");
            }
            else
                Assert.True(Bit(T[col], g.T0C), $"Z{r} 指向 {m.Groups[1].Value}{r}（{T[col]} °C），与 T0 = {g.T0C} 不符");
            Assert.True(Bit(sh.Num("Z" + r), g.Rho0MicroOhmCm), $"Z{r} ρ0 与代码不同");
        }

        foreach (var g in MaterialDb.All.Values)
        {
            Assert.True(g.HasResistivity, $"{g.Name} 没有电阻率");
            var src = PtResistivityData.Row(g.ResistivityFrom);   // 找不到就抛
            var sg = MaterialDb.Get(src.DataGrade);
            Assert.True(Bit(g.Alpha, sg.Alpha) && Bit(g.Beta, sg.Beta) && Bit(g.Rho0MicroOhmCm, sg.Rho0MicroOhmCm) && Bit(g.T0C, sg.T0C),
                $"{g.Name} 标着借 {src.DataGrade}，系数却不相同");
            _o.WriteLine($"{g.Name}：电阻率取自 {src.DataGrade} 行{(g.ResistivityFrom == g.Name ? "" : "（借用）")}");
        }
    }

    [Fact]
    public void 测试值逐格等于代码_空格对应代码缺测()
    {
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        int blanks = 0, vals = 0;
        foreach (var row in PtResistivityData.Rows)
            for (int i = 0; i < PtResistivityData.GridCols.Length; i++)
            {
                string c = PtResistivityData.GridCols[i] + row.TestRow;
                double code = row.Values[i];
                if (sh.IsBlank(c)) { blanks++; Assert.True(double.IsNaN(code), $"{c} 空格，代码却有值 {code}"); }
                else { vals++; Assert.True(Bit(sh.Num(c), code), $"{c}：xlsx {sh.Num(c):R} ≠ 代码 {code:R}"); }
            }
        _o.WriteLine($"测试值 {vals} 格，空格 {blanks} 格");
        Assert.True(blanks > 0 && vals > 0);
    }

    [Fact]
    public void 計算值缓存复现_没有公式的字面0不当缓存而且正对着缺测()
    {
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        int n = 0, bitSame = 0, literalZero = 0; double worst = 0;
        foreach (var row in PtResistivityData.Rows.Where(r => r.CalcRow > 0))
        {
            var g = MaterialDb.Get(row.DataGrade);
            string? master = null;
            for (int i = 0; i < PtResistivityData.GridCols.Length; i++)
            {
                string col = PtResistivityData.GridCols[i];
                var cell = sh.Cell(col + row.CalcRow);
                if (cell?.RawValue is null) { Assert.True(double.IsNaN(row.Values[i]), $"{col}{row.CalcRow} 没有计算值，测试值却有"); continue; }
                if (!cell.HasFormula)
                {
                    literalZero++;
                    Assert.True(sh.Num(col + row.CalcRow) == 0 && double.IsNaN(row.Values[i]),
                        $"{col}{row.CalcRow} 没有公式：只允许是正对着缺测格的字面 0");
                    continue;
                }
                if (cell.FormulaText is { } f)
                {
                    // =$ρ0格*(1+$X$r*(温度格-$T0格)+$Y$r*(温度格-$T0格)^2)
                    var m = Regex.Match(f, @"^\$([A-Z]+)\$" + row.TestRow + @"\*\(1\+\$X\$" + row.TestRow + @"\*\(" + col + @"22-\$([A-Z]+)\$22\)\+\$Y\$"
                                           + row.TestRow + @"\*\(" + col + @"22-\$([A-Z]+)\$22\)\^2\)$");
                    Assert.True(m.Success, $"{col}{row.CalcRow} 公式形状不是 ρ0·[1+α(T−T0)+β(T−T0)²]：{f}");
                    int r0 = Array.IndexOf(PtResistivityData.GridCols, m.Groups[1].Value);
                    int t0 = Array.IndexOf(PtResistivityData.GridCols, m.Groups[2].Value);
                    Assert.True(m.Groups[2].Value == m.Groups[3].Value && r0 == t0 && Bit(PtResistivityData.GridTempsC[t0], g.T0C)
                                && Bit(row.Values[r0], g.Rho0MicroOhmCm),
                        $"{col}{row.CalcRow}：公式的 ρ0／T0 格与代码 ρ0 = {g.Rho0MicroOhmCm}、T0 = {g.T0C} 不符");
                    master = f;
                }
                double cache = sh.Num(col + row.CalcRow);
                double code = g.ResistivityOhmM(PtResistivityData.GridTempsC[i]) * 1e8;
                double rel = Math.Abs(code - cache) / Math.Abs(cache);
                worst = Math.Max(worst, rel); n++; if (Bit(code, cache)) bitSame++;
                Assert.True(rel <= 4 * Eps, $"{col}{row.CalcRow}：代码 {code:R} vs 缓存 {cache:R}，相对差 {rel:E2} > 4ε");
            }
            Assert.True(master is not null, $"{row.DataGrade} 计算值行一个公式文本都没读到");
        }
        _o.WriteLine($"計算值缓存 {n} 格：逐位相同 {bitSame}，最大相对差 {worst:E2}；没有公式的字面 0 {literalZero} 格（跳过，正对缺测）");
        Assert.True(literalZero > 0, "反自证：Pt-Rh 80-20 的 I33:Q33、S33 字面 0 应被识别出来");
    }

    [Fact]
    public void SSE缓存复现_且工作簿SSE不小于该式真最小值()
    {
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        foreach (var row in PtResistivityData.Rows.Where(r => r.SseRow > 0))
        {
            var g = MaterialDb.Get(row.DataGrade);
            Assert.Equal($"SUM(G{row.SseRow}:W{row.SseRow})", sh.Formula("Y" + row.SseRow));
            double sse = 0, bound = 0;
            var xs = new List<Q>(); var ys = new List<Q>();
            for (int i = 0; i < PtResistivityData.GridTempsC.Length; i++)
            {
                if (double.IsNaN(row.Values[i])) continue;
                double calc = g.ResistivityOhmM(PtResistivityData.GridTempsC[i]) * 1e8, d = calc - row.Values[i];
                sse += d * d;
                bound += 2 * Math.Abs(d) * 4 * Eps * Math.Abs(calc) + 4 * Eps * d * d;
                xs.Add(Q.FromDouble(PtResistivityData.GridTempsC[i] - g.T0C)); ys.Add(Q.FromDouble(row.Values[i]));
            }
            double cache = sh.Num("Y" + row.SseRow);
            Assert.True(Math.Abs(sse - cache) <= bound + 4 * Eps * cache, $"Y{row.SseRow} SSE：代码 {sse:R} vs 缓存 {cache:R}");

            // ρ0 固定时 (α,β) 是线性最小二乘：ρ/ρ0 − 1 = α·d + β·d²（精确解）
            var rho0 = Q.FromDouble(g.Rho0MicroOhmCm);
            Q s11 = Q.Zero, s12 = Q.Zero, s22 = Q.Zero, b1 = Q.Zero, b2 = Q.Zero;
            for (int k = 0; k < xs.Count; k++)
            {
                var d1 = rho0 * xs[k]; var d2 = rho0 * xs[k] * xs[k]; var yy = ys[k] - rho0;
                s11 += d1 * d1; s12 += d1 * d2; s22 += d2 * d2; b1 += d1 * yy; b2 += d2 * yy;
            }
            string min = "（点数不足以定 α、β）";
            var det = s11 * s22 - s12 * s12;
            if (xs.Count >= 3 && det.Sign != 0)
            {
                var a = (b1 * s22 - b2 * s12) / det; var b = (s11 * b2 - s12 * b1) / det;
                Q best = Q.Zero;
                for (int k = 0; k < xs.Count; k++) { var e = rho0 * (Q.One + a * xs[k] + b * xs[k] * xs[k]) - ys[k]; best += e * e; }
                double bd = best.ToDouble();
                min = $"真最小 {bd:0.000000}（α {a.ToDouble():E6}、β {b.ToDouble():E6}；工作簿高 {(cache / bd - 1) * 100:0.00} %）";
                Assert.True(cache >= bd * (1 - 1e-12), $"{row.DataGrade}：工作簿 SSE {cache} 竟小于真最小 {bd} —— 复算器坏了");
            }
            _o.WriteLine($"{row.DataGrade}：SSE 缓存 {cache:0.000000}，代码复现 {sse:0.000000}；{min}");
        }
    }

    [Fact]
    public void Materials纯铂电阻率与MaterialDb纯铂逐位相同()
    {
        var pt = MaterialDb.Get("Pt");
        Assert.True(Bit(Materials.AlphaFit, pt.Alpha) && Bit(Materials.BetaFit, pt.Beta), "Materials 的 α/β 与 MaterialDb 纯铂不同");
        Assert.True(Bit(Materials.RhoRef, pt.Rho0MicroOhmCm * 1e-8), "RhoRef 与 9.83·1e-8 不逐位相同");
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        Assert.True(Bit(sh.Num("X23"), Materials.AlphaFit) && Bit(sh.Num("Y23"), Materials.BetaFit), "Materials 的 α/β 与 X23/Y23 不同");
        for (int t = 0; t <= 1600; t++)
            Assert.True(Bit(Materials.PtResistivity(t), pt.ResistivityOhmM(t)), $"{t} °C 两处纯铂电阻率不逐位相同");
        foreach (double t in new[] { 0.5, 20.25, 1099.999, 1234.5678, 1450.125 })
            Assert.True(Bit(Materials.PtResistivity(t), pt.ResistivityOhmM(t)), $"{t} °C 两处纯铂电阻率不逐位相同");
    }

    /// <summary>
    /// 疑插补段 = 工作簿测试值里 100 K 栏上的**最长等差段**：代码登记的每段都是等差且不能再往两边延伸；没登记的等差段都比最短登记段短。
    /// 2026-09-15 第二轮（Opus 5）：疑点分种类；**两位小数的行**里恰为相邻两点中点的格必须登记为「中点」
    /// （一位小数与整数的行里中点天然常见 —— 如纯铂 800、1200 °C —— 只打印不登记）。
    /// </summary>
    [Fact]
    public void 登记的疑插补段正是工作簿里的等差段_Pd100度是该行残差最大点_两位小数行的中点都登记()
    {
        using var book = Book();
        var sh = book.Sheet(PtResistivityData.SheetName);
        int firstGrid = Array.IndexOf(PtResistivityData.GridTempsC, 100.0);
        var runs = new List<(string Grade, double From, double To, int Steps)>();
        foreach (var row in PtResistivityData.Rows)
        {
            var cols = Enumerable.Range(firstGrid, PtResistivityData.GridCols.Length - firstGrid).ToArray();
            int i = 0;
            while (i < cols.Length - 1)
            {
                string C(int k) => PtResistivityData.GridCols[cols[k]] + row.TestRow;
                if (sh.IsBlank(C(i)) || sh.IsBlank(C(i + 1))) { i++; continue; }
                decimal step = (decimal)sh.Num(C(i + 1)) - (decimal)sh.Num(C(i));
                int j = i + 1;
                while (j + 1 < cols.Length && !sh.IsBlank(C(j + 1)) && (decimal)sh.Num(C(j + 1)) - (decimal)sh.Num(C(j)) == step) j++;
                if (j - i >= 2) runs.Add((row.DataGrade, PtResistivityData.GridTempsC[cols[i]], PtResistivityData.GridTempsC[cols[j]], j - i));
                i = j;
            }
        }
        foreach (var r in runs) _o.WriteLine($"等差段：{r.Grade} {r.From:0}–{r.To:0} °C（{r.Steps} 步）");

        var flagged = PtResistivityData.Rows.SelectMany(r => r.Suspect.Where(s => s.Kind == ResistivitySuspectKind.ArithmeticRun).Select(s => (r.DataGrade, s.FromC, s.ToC))).ToList();
        Assert.NotEmpty(flagged);
        int minSteps = int.MaxValue;
        foreach (var (grade, from, to) in flagged)
        {
            var hit = runs.SingleOrDefault(x => x.Grade == grade && x.From == from && x.To == to);
            Assert.True(hit.Grade is not null, $"{grade} 登记的 {from}–{to} °C 不是工作簿里的最长等差段");
            minSteps = Math.Min(minSteps, hit.Steps);
        }
        foreach (var r in runs.Where(x => !flagged.Contains((x.Grade, x.From, x.To))))
            Assert.True(r.Steps < minSteps, $"{r.Grade} {r.From}–{r.To} °C 等差 {r.Steps} 步没登记为疑插补");

        // Pd 100 °C 单点疑抄错：是该行 (计算−测试)² 最大的点
        var pd = PtResistivityData.Row("Pd");
        var g = MaterialDb.Get("Pd");
        var single = pd.Suspect.Single(s => s.Kind == ResistivitySuspectKind.Outlier);
        var sq = PtResistivityData.GridTempsC.Select((t, i) => (t, v: pd.Values[i]))
            .Where(p => !double.IsNaN(p.v)).Select(p => (p.t, e: Math.Pow(g.ResistivityOhmM(p.t) * 1e8 - p.v, 2))).ToList();
        var worst = sq.OrderByDescending(p => p.e).First();
        _o.WriteLine($"Pd：最大残差平方 {worst.e:0.00} @ {worst.t:0} °C，占该行 SSE {worst.e / sq.Sum(p => p.e) * 100:0} %");
        Assert.Equal(single.FromC, worst.t);
        Assert.True(PtResistivityData.Rows.SelectMany(r => r.Suspect).Count(s => s.Kind == ResistivitySuspectKind.Outlier) == 1
                    && single.ToC == single.FromC, "疑抄错只登记了 Pd 100 °C 这一个单点");

        // 中点：两位小数的行里，100 K 栏上恰为相邻两点中点（decimal 精确比）的格必须登记为 Midpoint；登记的也必须真是中点
        var midRegistered = new HashSet<(string, double)>();
        foreach (var row in PtResistivityData.Rows)
            foreach (var m in row.Suspect.Where(s => s.Kind == ResistivitySuspectKind.Midpoint))
            {
                Assert.True(m.FromC == m.ToC, $"{row.DataGrade} 中点疑点应是单点");
                midRegistered.Add((row.DataGrade, m.FromC));
            }
        Assert.NotEmpty(midRegistered);
        var midFound = new HashSet<(string, double)>();
        foreach (var row in PtResistivityData.Rows)
        {
            var idx = Enumerable.Range(firstGrid, PtResistivityData.GridCols.Length - firstGrid).ToArray();
            string C(int k) => PtResistivityData.GridCols[k] + row.TestRow;
            if (idx.Count(k => !sh.IsBlank(C(k))) < 3) continue;   // Cu 100 °C 以上全空
            int decimals = idx.Where(k => !sh.IsBlank(C(k))).Max(k => ((decimal)sh.Num(C(k)) % 1m == 0m) ? 0
                : BitConverter.GetBytes(decimal.GetBits((decimal)sh.Num(C(k)))[3])[2]);
            for (int k = 1; k < idx.Length - 1; k++)
            {
                string a = C(idx[k - 1]), b = C(idx[k]), c = C(idx[k + 1]);
                if (sh.IsBlank(a) || sh.IsBlank(b) || sh.IsBlank(c)) continue;
                if (2 * (decimal)sh.Num(b) != (decimal)sh.Num(a) + (decimal)sh.Num(c)) continue;
                double t = PtResistivityData.GridTempsC[idx[k]];
                bool inRun = row.Suspect.Any(x => x.Kind == ResistivitySuspectKind.ArithmeticRun && t >= x.FromC && t <= x.ToC);
                _o.WriteLine($"中点：{row.DataGrade} {b} = {sh.Num(b)}（{t:0} °C，该行 {decimals} 位小数）{(inRun ? "，在已登记的等差段内" : "")}");
                if (decimals >= 2 && !inRun) midFound.Add((row.DataGrade, t));
            }
        }
        Assert.True(midFound.SetEquals(midRegistered),
            $"两位小数行的中点与登记的不一致：工作簿 {string.Join("、", midFound)}；登记 {string.Join("、", midRegistered)}");
    }

    [Fact]
    public void 覆盖分档_空档外推单点无数据与借用()
    {
        void Is(string g, double t, ResistivityCoverage c)
        {
            var v = PtResistivityData.Read(g, t);
            _o.WriteLine($"{g} {t} °C → {v.Coverage}：{v.Note}");
            Assert.True(v.Coverage == c, $"{g} {t} °C 应为 {c}，却是 {v.Coverage}：{v.Note}");
            Assert.True(v.HasValue == !double.IsNaN(v.OhmM));
            if (v.HasValue) Assert.True(Bit(v.OhmM, MaterialDb.Get(g).ResistivityOhmM(t)), "值必须就是原函数的值");
        }
        Is("Pt", 1150, ResistivityCoverage.BetweenTestValues);
        Is("Pt", 1600, ResistivityCoverage.Extrapolated);
        Is("Pt-Rh/80-20", 500, ResistivityCoverage.InsideGap);
        Is("Pt-Rh/80-20", 1100, ResistivityCoverage.InsideGap);
        Is("Pt-Rh/80-20", 1250, ResistivityCoverage.BetweenTestValues);
        Is("FKS16/Pt", 300, ResistivityCoverage.BetweenSuspectTestValues);
        Is("FKS16/Pt", 650, ResistivityCoverage.BetweenSuspectTestValues);
        Is("FKS16/Pt", 1200, ResistivityCoverage.BetweenTestValues);
        Is("Pd", 50, ResistivityCoverage.BetweenSuspectTestValues);
        Is("Pd", 750, ResistivityCoverage.BetweenSuspectTestValues);   // 700、800 之间，700 °C 是登记的中点
        Is("Pd", 1100, ResistivityCoverage.Extrapolated);
        // 口径 D4④（2026-09-15 第二轮）：区间外一律外推，只有 dρ/dT ≤ 0 的温度段无数据（期望取 HANDOVER 覆盖表的决定记录）
        Is("Ni", 1100, ResistivityCoverage.Extrapolated);
        Is("Ni", 1337, ResistivityCoverage.Extrapolated);
        Is("Ni", 1338, ResistivityCoverage.NoData);
        Is("Pd", 1899, ResistivityCoverage.Extrapolated);
        Is("Pd", 1901, ResistivityCoverage.NoData);
        Is("Pt", 3391, ResistivityCoverage.Extrapolated);
        Is("Pt", 3392, ResistivityCoverage.NoData);
        Is("Cu", 20, ResistivityCoverage.SinglePointOnly);
        Is("Cu", 100, ResistivityCoverage.NoData);
        var z = PtResistivityData.Read("Tanaka-ZGS-Pt", 1200);
        Assert.True(z.Borrowed && z.DataGrade == "Pt" && z.Note.Contains("借用"), z.Note);
        Assert.False(PtResistivityData.Read("Pt", 1200).Borrowed);

        foreach (string g in MaterialDb.All.Keys)
            for (double t = -20; t <= 1700; t += 5)
            {
                var v = PtResistivityData.Read(g, t);
                Assert.True(v.HasValue == !double.IsNaN(v.OhmM), $"{g} {t}：NaN 与无数据不一致");
            }
        foreach (var g in MaterialDb.All.Values.Where(x => x.Beta < 0))
            _o.WriteLine($"{g.Name}：二次式 dρ/dT 在 T0 − α/(2β) = {g.T0C - g.Alpha / (2 * g.Beta):0.0} °C 归零，以上无数据（只报）");
    }
}
