using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 从《鉑金材料蠕變應力壽命估算.xlsx》F 列扫出原始持久强度块（门与 MaterialDbTests 共用这一份读法）。2026-09-15，Opus 5。
///
/// ⚠ 数值把关（2026-09-15）：Q32、Q34、Q36、Q38、Q40 有零散数字（10.9、6.5、2.4、2.65、1.05），其中 Q34、Q40 在 time 行上 ——
///   按行抓全部数字会把 6.5 当成第 6 个时间点。⇒ **只读 G:L**，并断言 time 行与 stress 行一样长、从 G 连续。
/// </summary>
internal static class CreepXlsxRaw
{
    public sealed record Block(string Title, int TitleRow, double TempC, int TempRow, int TimeRow, int StressRow,
        double[] Hours, double[] Stress, string[] StressCells);

    private static readonly string[] Cols = { "G", "H", "I", "J", "K", "L" };

    public static List<Block> Read(XlsxSheet sh, int fromRow = 28, int toRow = 116)
    {
        var blocks = new List<Block>();
        string? title = null; int titleRow = 0;
        for (int r = fromRow; r <= toRow; r++)
        {
            if (sh.IsBlank("F" + r)) continue;
            string f = sh.Str("F" + r).Trim();
            var tm = Regex.Match(f, @"^(\d+)C$");
            if (f is "time" or "stress") continue;   // 由温度标签那一行带着读
            if (!tm.Success) { title = f; titleRow = r; continue; }
            if (title is null) throw new InvalidOperationException($"F{r}「{f}」前面没有牌号标题");
            if (sh.Str("F" + (r + 1)).Trim() != "time" || sh.Str("F" + (r + 2)).Trim() != "stress")
                throw new InvalidOperationException($"F{r} 温度标签下面不是 time／stress 两行");
            List<double> Row(int rr, out List<string> cells)
            {
                var v = new List<double>(); cells = new List<string>();
                foreach (var c in Cols)
                {
                    if (sh.IsBlank(c + rr)) break;
                    v.Add(sh.Num(c + rr)); cells.Add(c + rr);
                }
                // 断言连续：断开之后 G:L 里不许再有数
                foreach (var c in Cols.Skip(v.Count))
                    if (!sh.IsBlank(c + rr)) throw new InvalidOperationException($"{c}{rr} 在断开之后还有数");
                return v;
            }
            var hours = Row(r + 1, out _);
            var stress = Row(r + 2, out var sc);
            if (hours.Count != stress.Count || hours.Count == 0)
                throw new InvalidOperationException($"F{r} 块 time 行 {hours.Count} 个、stress 行 {stress.Count} 个，不等长");
            blocks.Add(new Block(title, titleRow, double.Parse(tm.Groups[1].Value), r, r + 1, r + 2,
                hours.ToArray(), stress.ToArray(), sc.ToArray()));
        }
        return blocks;
    }
}

/// <summary>
/// 持久强度的门：系数表、名称映射、原始点、R10–R27 计算区两向公式，全都**直接读 xlsx** 核对。2026-09-15，Opus 5。
///
/// 容差：系数与原始点逐位；N/O 缓存 |A(T) − N| ≤ 8ε·Σ|c_k·M^k|；R 列（给应力求寿命 / 给寿命求应力）相对 ≤ 1e-9；
/// 两向互逆沿用既有 1e-9。R 列的 1e-9 是数值把关**看过工作簿数据后**（实测最大相对差 5.7e-11）、**改代码前**写死的。
/// 2026-09-15 第二轮（Opus 5）：U10:W24 的 FKS／Umicore 四行字面值入门，沿用 N/O 的 8ε·Σ 界与 R 列的 1e-9，不另定容差。
/// 2026-09-15 第四轮（Opus 5，复核 minor）：时间轴端点两向覆盖（容差沿用互逆的 1e-9）、判不了时间外推排到时间外推之后、
///   a(T) ≥ 0 规则用人造系数牌号测、包络门打印 0.1–1e5 h 范围外的违规段，都不另定容差。
/// 2026-09-16 第五轮（Opus 5，核验员 V12／V6／V7）：拟合区间边界（原始点门用 xlsx 读出的 tMin/tMax，δ = 1e-6／0.5／5 K 两向无数据，
///   InCreepRange 容差 1e-9 钉死：外 1e-10 在内、外 1e-6 在外；包络门两端各半 K 外探针）、时间轴端点容差内外各一点专门门、
///   推定区间门改断言等于 —— 都不另定容差。
/// </summary>
public class R48CreepWorkbookTests
{
    private readonly ITestOutputHelper _o;
    public R48CreepWorkbookTests(ITestOutputHelper o) => _o = o;

    private const double Eps = 2.220446049250313e-16;
    private static bool Bit(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static XlsxBook Book() => XlsxBook.OpenInRepo(PtCreepWorkbook.WorkbookFile);
    private static readonly string[] CoefCols = { "U", "V", "W", "X", "Y", "Z" };   // c5..c0

    [Fact]
    public void 系数逐位等于工作簿系数表_名称映射一一对应()
    {
        using var book = Book();
        var sh = book.Sheet(PtCreepWorkbook.SheetName);
        string[] hdr = { "c5", "c4", "c3", "c2", "c1", "c0" };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(hdr[i], sh.Str(CoefCols[i] + "117").Trim());
            Assert.Equal(hdr[i], sh.Str(CoefCols[i] + "128").Trim());
        }
        var namesA = Enumerable.Range(118, 9).Select(r => sh.Str("S" + r).Trim()).ToList();
        var namesB = Enumerable.Range(129, 9).Select(r => sh.Str("S" + r).Trim()).ToList();
        Assert.True(sh.IsBlank("S127") && sh.IsBlank("S138"), "系数表比 9 行多出了一行，映射要跟着登记");
        Assert.True(namesA.ToHashSet().SetEquals(PtCreepWorkbook.CoefRows.Select(c => c.WorkbookName)), "a 表名称与代码映射不一一对应");
        Assert.True(namesB.ToHashSet().SetEquals(PtCreepWorkbook.CoefRows.Select(c => c.WorkbookName)), "b 表名称与代码映射不一一对应");
        Assert.True(PtCreepWorkbook.CoefRows.Select(c => c.Grade).ToHashSet().SetEquals(MaterialDb.WithCreep.Select(g => g.Name)),
            "有蠕变的材料库牌号与映射不一一对应");

        foreach (var c in PtCreepWorkbook.CoefRows)
        {
            Assert.Equal(c.WorkbookName, sh.Str("S" + c.RowA).Trim());
            Assert.Equal("a", sh.Str("T" + c.RowA).Trim());
            Assert.Equal(c.WorkbookName, sh.Str("S" + c.RowB).Trim());
            Assert.Equal("b", sh.Str("T" + c.RowB).Trim());
            var g = MaterialDb.Get(c.Grade);
            for (int i = 0; i < 6; i++)
            {
                Assert.True(Bit(sh.Num(CoefCols[i] + c.RowA), g.Ca[i]), $"{c.Grade} a 的 {hdr[i]}（{CoefCols[i]}{c.RowA}）与代码不同");
                Assert.True(Bit(sh.Num(CoefCols[i] + c.RowB), g.Cb[i]), $"{c.Grade} b 的 {hdr[i]}（{CoefCols[i]}{c.RowB}）与代码不同");
            }
        }
    }

    [Fact]
    public void 原始持久强度点逐格等于代码_只读G到L列_牌号标题有映射()
    {
        using var book = Book();
        var sh = book.Sheet(PtCreepWorkbook.SheetName);
        var xl = CreepXlsxRaw.Read(sh);
        Assert.NotEmpty(xl);
        Assert.True(xl.Count == PtCreepWorkbook.RawBlocks.Count,
            $"工作簿扫出 {xl.Count} 块原始点，代码登记 {PtCreepWorkbook.RawBlocks.Count} 块");
        int pts = 0;
        foreach (var b in xl)
        {
            var map = PtCreepWorkbook.CoefRows.SingleOrDefault(c => c.RawBlockTitle == b.Title)
                ?? throw new InvalidOperationException($"原始块标题「{b.Title}」（F{b.TitleRow}）没有映射到牌号");
            var code = PtCreepWorkbook.RawBlocks.SingleOrDefault(k => k.Title == b.Title && k.TempC == b.TempC)
                ?? throw new InvalidOperationException($"代码漏了 {b.Title} {b.TempC} °C 那一块（F{b.TempRow}）");
            Assert.Equal(map.Grade, code.Grade);
            Assert.True(code.TitleRow == b.TitleRow && code.TempRow == b.TempRow && code.TimeRow == b.TimeRow && code.StressRow == b.StressRow,
                $"{b.Title} {b.TempC} °C 行号与代码不同");
            Assert.True(code.Hours.Length == b.Hours.Length && code.StressMPa.Length == b.Stress.Length,
                $"{b.Title} {b.TempC} °C 点数：工作簿 {b.Hours.Length}，代码 {code.Hours.Length}");
            for (int i = 0; i < b.Hours.Length; i++)
            {
                Assert.True(Bit(code.Hours[i], b.Hours[i]), $"{b.Title} {b.TempC} °C 时间 {i} 不同");
                Assert.True(Bit(code.StressMPa[i], b.Stress[i]), $"{b.StressCells[i]} 应力与代码不同");
                pts++;
            }
            var g = MaterialDb.Get(map.Grade);
            Assert.True(g.InCreepRange(b.TempC), $"{map.Grade} 的原始点 {b.TempC} °C 不在代码的拟合区间内");
        }
        // ── 2026-09-16 第五轮（Opus 5，核验员 V12／V12b／V12c）：拟合区间**边界**此前无门守 —— MaterialDb.InCreepRange 上端 +5 K／+0.5 K、
        //    下端 −5 K 放宽后 45 条门全绿，RuptureStress("Pt", 1403, 1000) 给值且标「实测确认区间内」（纯铂原始点只到 1400 °C）。
        //    这里对每个有原始点的牌号，用 **xlsx 读出的**原始点温度范围 [tMin, tMax]（不是代码的 CreepTMinC／CreepTMaxC）核：
        //    tMax+δ、tMin−δ（δ = 1e-6、0.5、5 K）求应力与求寿命都无数据且值 NaN；tMax、tMin 本身有值；
        //    容差钉死：端点外 1e-10 K 仍在区间内、1e-6 K 已在区间外（两端同做）。推定区间的四个牌号没有原始点，用 CreepTMinC／CreepTMaxC 同法。
        int edgeGrades = 0, edgeProbes = 0;
        void Edges(PtGrade g, double tMin, double tMax, string from)
        {
            double[] hs = { 0.1, 1, 1000, 10000, 1e5 };
            double[] sigmas = { 2, 10 };
            foreach (double d in new[] { 1e-6, 0.5, 5.0 })
                foreach (double t in new[] { tMax + d, tMin - d })
                {
                    foreach (double h in hs)
                    {
                        var v = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                        Assert.True(v.Coverage == CreepCoverage.NoData && double.IsNaN(v.Value) && !v.HasValue,
                            $"{g.Name} {t:R} °C × {h:g} h 在{from}区间 {tMin}–{tMax} °C 外 {d:g} K，应无数据：{v.Coverage}（值 {v.Value:R}）{v.Note}");
                        Assert.True(double.IsNaN(g.RuptureStressMPa(t, h)), $"{g.Name} RuptureStressMPa({t:R}, {h:g}) 在区间外应为 NaN");
                        edgeProbes++;
                    }
                    foreach (double sg in sigmas)
                    {
                        var l = PtCreepWorkbook.RuptureLife(g.Name, t, sg);
                        Assert.True(l.Coverage == CreepCoverage.NoData && double.IsNaN(l.Value) && !l.HasValue,
                            $"{g.Name} {t:R} °C × {sg:g} MPa 在{from}区间外 {d:g} K，求寿命应无数据：{l.Coverage}（值 {l.Value:R}）{l.Note}");
                        Assert.True(double.IsNaN(g.RuptureLifeHours(t, sg)), $"{g.Name} RuptureLifeHours({t:R}, {sg:g}) 在区间外应为 NaN");
                        edgeProbes++;
                    }
                }
            foreach (double t in new[] { tMin, tMax })
            {
                foreach (double h in hs)
                {
                    var v = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                    Assert.True(v.HasValue && Bit(v.Value, g.RuptureStressMPa(t, h)) && double.IsFinite(v.Value) && v.Value > 0,
                        $"{g.Name} 区间端点 {t} °C × {h:g} h 本身应有值：{v.Coverage} {v.Note}");
                    var l = PtCreepWorkbook.RuptureLife(g.Name, t, v.Value);
                    Assert.True(l.HasValue && Bit(l.Value, g.RuptureLifeHours(t, v.Value)), $"{g.Name} 区间端点 {t} °C 由 {v.Value:g} MPa 求寿命应有值：{l.Coverage} {l.Note}");
                }
            }
            // 容差钉死（MaterialDb.InCreepRange 的 1e-9 —— 门槛写死不挪）：端点外 1e-10 K 在内、1e-6 K 在外，两端同做
            Assert.True(g.InCreepRange(tMax + 1e-10) && g.InCreepRange(tMin - 1e-10), $"{g.Name}：端点外 1e-10 K 应仍算区间内（容差 1e-9）");
            Assert.True(!g.InCreepRange(tMax + 1e-6) && !g.InCreepRange(tMin - 1e-6), $"{g.Name}：端点外 1e-6 K 应算区间外（容差 1e-9 被放宽了）");
            Assert.True(g.InCreepRange(tMax) && g.InCreepRange(tMin), $"{g.Name}：端点本身应在区间内");
            edgeGrades++;
        }
        var confirmedNames = new List<string>(); var unconfirmedNames = new List<string>();
        foreach (var g in MaterialDb.WithCreep.Where(g => g.RangeConfirmed))
        {
            var ts = xl.Where(b => PtCreepWorkbook.CoefRows.Single(c => c.RawBlockTitle == b.Title).Grade == g.Name).Select(b => b.TempC).ToList();
            Assert.True(ts.Count > 0 && ts.Min() == g.CreepTMinC && ts.Max() == g.CreepTMaxC,
                $"{g.Name} 标着区间已确认 {g.CreepTMinC}–{g.CreepTMaxC} °C，原始点却是 {(ts.Count == 0 ? "无" : $"{ts.Min()}–{ts.Max()}")} °C");
            Edges(g, ts.Min(), ts.Max(), "原始点");   // 边界取 xlsx 读出的原始点温度，不取代码
            confirmedNames.Add(g.Name);
        }
        foreach (var g in MaterialDb.WithCreep.Where(g => !g.RangeConfirmed))
        {
            Assert.False(xl.Any(b => PtCreepWorkbook.CoefRows.Single(c => c.RawBlockTitle == b.Title).Grade == g.Name), $"{g.Name} 标着区间推定，工作簿里却有原始点");
            Edges(g, g.CreepTMinC, g.CreepTMaxC, "推定");
            unconfirmedNames.Add(g.Name);
        }
        Assert.Equal(MaterialDb.WithCreep.Count(), edgeGrades);
        foreach (string must in new[] { "Pt", "Pt-Rh/90-10", "Pt-Rh/80-20", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10" }) Assert.Contains(must, confirmedNames);
        foreach (string must in new[] { "FKS16/Pt", "FKS16/PtRh-9010", "Umicore-PtRh10", "Umicore-PtRh20" }) Assert.Contains(must, unconfirmedNames);
        _o.WriteLine($"原始持久强度 {xl.Count} 块 {pts} 点（旧 MaterialDbTests 手抄 110 点）");
        _o.WriteLine($"区间边界：{edgeGrades} 个牌号 × 两端 × δ = 1e-6／0.5／5 K 共 {edgeProbes} 个区间外探针都无数据；"
                   + $"原始点区间 {string.Join("、", confirmedNames)}；推定区间 {string.Join("、", unconfirmedNames)}");
    }

    [Fact]
    public void 计算区两向公式与现有函数核对_公式文本照工作簿()
    {
        using var book = Book();
        var sh = book.Sheet(PtCreepWorkbook.SheetName);
        static string N(string s) => Regex.Replace(s, @"\s+", "");
        int rows = 0, dir2 = 0;
        for (int r = 10; !sh.IsBlank("D" + r); r += 2)
        {
            string name = sh.Str("D" + r).Trim();
            var map = PtCreepWorkbook.CoefRows.SingleOrDefault(c => c.WorkbookName == name)
                ?? throw new InvalidOperationException($"D{r}「{name}」没有映射");
            var g = MaterialDb.Get(map.Grade);
            double L = sh.Num("L" + r), M = sh.Num("M" + r), P = sh.Num("P" + r), Qv = sh.Num("Q" + r);
            Assert.Equal($"L{r}+273.15", N(sh.Formula("M" + r)!));
            Assert.True(Bit(M, L + 273.15), $"M{r} ≠ L+273.15");
            for (int i = 0; i < 6; i++)
            {
                string colF = ((char)('F' + i)).ToString();
                Assert.True(Bit(sh.Num(colF + r), sh.Num(CoefCols[i] + map.RowA)), $"{colF}{r} 查表结果与 {CoefCols[i]}{map.RowA} 不同");
                Assert.True(Bit(sh.Num(colF + (r + 1)), sh.Num(CoefCols[i] + map.RowB)), $"{colF}{r + 1} 查表结果与 {CoefCols[i]}{map.RowB} 不同");
            }
            Assert.Equal($"F{r}*M{r}^5+G{r}*M{r}^4+H{r}*M{r}^3+I{r}*M{r}^2+J{r}*M{r}+K{r}", N(sh.Formula("N" + r)!));
            Assert.Equal($"F{r + 1}*M{r}^5+G{r + 1}*M{r}^4+H{r + 1}*M{r}^3+I{r + 1}*M{r}^2+J{r + 1}*M{r}+K{r + 1}", N(sh.Formula("O" + r)!));
            double Bound(double[] c) => 8 * Eps * Enumerable.Range(0, 6).Sum(k => Math.Abs(c[k] * Math.Pow(M, 5 - k)));
            double nA = g.A(L), nB = g.B(L), nC = sh.Num("N" + r), oC = sh.Num("O" + r);
            Assert.True(Math.Abs(nA - nC) <= Bound(g.Ca), $"N{r}：代码 a(T) {nA:R} vs 缓存 {nC:R}");
            Assert.True(Math.Abs(nB - oC) <= Bound(g.Cb), $"O{r}：代码 b(T) {nB:R} vs 缓存 {oC:R}");

            Assert.Equal("VLOOKUP($Q$9,$Q$118:$R$119,2,FALSE)", N(sh.Formula("P" + r)!));
            Assert.Equal($"IF(P{r}=2,10^(N{r}/M{r}*LOG(Q{r})+O{r}/M{r}),10^((M{r}*LOG(Q{r})-O{r})/N{r}))", N(sh.Formula("R" + r)!));
            double rc = sh.Num("R" + r);
            double code = P == 2 ? g.RuptureStressMPa(L, Qv) : g.RuptureLifeHours(L, Qv);
            double rel = Math.Abs(code - rc) / Math.Abs(rc);
            Assert.True(rel <= 1e-9, $"R{r}（P={P}）：代码 {code:R} vs 缓存 {rc:R}，相对差 {rel:E2}");
            if (P == 2) dir2++;
            string tNote = sh.IsBlank("T" + r) ? "" : $"；T{r} = {sh.Num("T" + r):0.####}（ABS(S−R)/S，比值不是百分数，S{r} = {sh.Num("S" + r)}）";
            _o.WriteLine($"R{r} {name}→{map.Grade}：{L} °C，P={P}（{(P == 2 ? "给寿命求应力" : "给应力求寿命")}），输入 {Qv} → 工作簿 {rc:0.####} / 代码 {code:0.####}（相对差 {rel:E1}）{tNote}");
            rows++;
        }
        Assert.True(rows > 0);
        _o.WriteLine(dir2 == 0
            ? "计算区全部 P=1：「给寿命求应力」这一向工作簿没有缓存可对，只能靠代数互逆检验（见下一条门）"
            : $"计算区有 {dir2} 行 P=2，已按「给寿命求应力」核对");
    }

    [Fact]
    public void 两向互逆_每个牌号整个拟合区间与寿命一到十万小时()
    {
        double[] hours = { 0.1, 1, 10, 100, 1000, 8760, 10000, 1e5 };
        int n = 0;
        foreach (var g in MaterialDb.WithCreep)
            for (double t = g.CreepTMinC; t <= g.CreepTMaxC + 1e-9; t += 10)
                foreach (double h in hours)
                {
                    double s = g.RuptureStressMPa(t, h);
                    Assert.True(s > 0 && double.IsFinite(s), $"{g.Name} {t} °C {h} h 强度 {s}");
                    double life = g.RuptureLifeHours(t, s);
                    Assert.True(life > 0 && double.IsFinite(life), $"{g.Name} {t} °C 由 {s} MPa 求寿命得 {life}");
                    Assert.InRange(Math.Abs(g.RuptureStressMPa(t, life) - s) / s, 0, 1e-9);
                    Assert.InRange(Math.Abs(life - h) / h, 0, 1e-9);
                    n++;
                }
        _o.WriteLine($"两向互逆核对 {n} 点");
    }

    [Fact]
    public void 残差函数用的是工作簿原始点与现有函数()
    {
        using var book = Book();
        var sh = book.Sheet(PtCreepWorkbook.SheetName);
        foreach (var b in CreepXlsxRaw.Read(sh))
        {
            string grade = PtCreepWorkbook.CoefRows.Single(c => c.RawBlockTitle == b.Title).Grade;
            var g = MaterialDb.Get(grade);
            var res = PtCreepWorkbook.Residuals(grade);
            for (int i = 0; i < b.Hours.Length; i++)
            {
                var e = res.Single(x => x.TempC == b.TempC && x.Hours == b.Hours[i]);
                Assert.True(Bit(e.MeasuredMPa, b.Stress[i]) && e.StressCell == b.StressCells[i], $"{b.StressCells[i]} 残差用的原始点不对");
                Assert.True(Bit(e.ModelMPa, g.RuptureStressMPa(b.TempC, b.Hours[i])), "残差没用现有 RuptureStressMPa");
                Assert.True(Bit(e.LogLifeResid, Math.Log10(g.RuptureLifeHours(b.TempC, b.Stress[i])) - Math.Log10(b.Hours[i])), "寿命当量没用现有 RuptureLifeHours");
            }
        }
    }

    /// <summary>
    /// 登记入门（2026-09-15 第二轮，Opus 5）：U10:W24 四块（U = T[K]、V = a／b／σ 标签、W = 字面值，U12 等 = 寿命 h）
    /// 是 FKS／Umicore 四个牌号唯一的工作簿核对点（它们没有原始点）。块 i 对应系数表第 118+i 行（按值唯一匹配，不手写牌号）；
    /// a、b 沿用 N/O 缓存的 8ε·Σ|c_k·M^k| 界，σ 沿用 R 列的相对 1e-9。
    /// </summary>
    [Fact]
    public void U10到W24四块字面值_是FKS与Umicore牌号的核对点_与现有函数一致()
    {
        using var book = Book();
        var sh = book.Sheet(PtCreepWorkbook.SheetName);
        int blocks = 0;
        for (int r = 10; !sh.IsBlank("U" + r); r += 4)
        {
            Assert.Equal("a", sh.Str("V" + r).Trim());
            Assert.Equal("b", sh.Str("V" + (r + 1)).Trim());
            Assert.Equal("σ", sh.Str("V" + (r + 2)).Trim());
            foreach (string c in new[] { "W" + r, "W" + (r + 1), "W" + (r + 2), "U" + r, "U" + (r + 2) })
                Assert.False(sh.Cell(c)!.HasFormula, $"{c} 应是字面值");
            double tk = sh.Num("U" + r), h = sh.Num("U" + (r + 2));
            double wa = sh.Num("W" + r), wb = sh.Num("W" + (r + 1)), ws = sh.Num("W" + (r + 2));
            double tC = Math.Round(tk - 273.15, 6);
            Assert.True(Bit(tC + 273.15, tk), $"U{r} = {tk:R} K 换成 °C 再加回 273.15 不逐位（{tC + 273.15:R}）");

            double Bound(double[] c) => 8 * Eps * Enumerable.Range(0, 6).Sum(k => Math.Abs(c[k] * Math.Pow(tk, 5 - k)));
            var hits = MaterialDb.WithCreep.Where(g => g.InCreepRange(tC) && Math.Abs(g.A(tC) - wa) <= Bound(g.Ca)).ToList();
            Assert.True(hits.Count == 1, $"U{r} 块的 a = {wa:R} 应恰好对上一个牌号，却对上 {hits.Count} 个：{string.Join("、", hits.Select(g => g.Name))}");
            var gg = hits[0];
            var row = PtCreepWorkbook.CoefRows.Single(c => c.Grade == gg.Name);
            int i = (r - 10) / 4;
            Assert.True(row.RowA == 118 + i, $"U{r} 块对上 {gg.Name}（系数表第 {row.RowA} 行），按排列应是第 {118 + i} 行");
            Assert.True(Math.Abs(gg.B(tC) - wb) <= Bound(gg.Cb), $"W{r + 1}：{gg.Name} b({tC}) = {gg.B(tC):R} vs 字面 {wb:R}");
            double s = gg.RuptureStressMPa(tC, h), rel = Math.Abs(s - ws) / Math.Abs(ws);
            Assert.True(rel <= 1e-9, $"W{r + 2}：{gg.Name} σ({tC} °C, {h} h) = {s:R} vs 字面 {ws:R}，相对差 {rel:E2}");
            Assert.False(gg.RangeConfirmed, $"{gg.Name} 有原始点，U10:W24 不是它唯一的核对点 —— 登记说明要改");
            _o.WriteLine($"U{r}:W{r + 2} → {gg.Name}（S{row.RowA}「{row.WorkbookName}」）：{tC} °C、{h} h，a {wa:0.####}／b {wb:0.####}／σ {ws:0.####} MPa，代码 σ {s:0.####}（相对差 {rel:E1}）");
            blocks++;
        }
        int unconfirmed = PtCreepWorkbook.CoefRows.Count(c => !MaterialDb.Get(c.Grade).RangeConfirmed);
        Assert.True(blocks == unconfirmed, $"U10:W24 扫到 {blocks} 块，推定区间牌号有 {unconfirmed} 个 —— 每个都应有一块核对点");
    }

    /// <summary>
    /// 口径 D2（2026-09-15 第二轮）：形状扫描改包络判法，打印各牌号的违规段（HANDOVER 覆盖表照这里的输出写）。
    /// 门：ZGS-Pt 1045 °C × 1000 h（驼峰后半段，第一轮漏标）必须是「形状不物理」；ZGS-Pt 1 h 高温端上升段也要标。
    /// 第三轮（2026-09-15，Opus 5，复核阻断 2）：第二轮这条门拿 ShapeScan 与分档互比，只证明两份生产实现一致、不证明判法对 ——
    /// 两处同步改成「只和 T−50 K 以内比」（复核注入 W50）照样全绿。改成**门自己按定义算**：从 CreepTMinC 起按 1 K 取
    /// RuptureStressMPa 的累计最小值，σ(T) 高于 T 以下（不含 T）任一网格温度的 σ ⇒ 包络违规，另加 a(T) ≥ 0；
    /// 在全部 1 K 网格点与网格间的半 K 点 × 8 个寿命上，与 RuptureStress 的覆盖类别（连同时间外推、推定区间）和值逐点比，
    /// ShapeScan 在网格点上也与门算的比。再加离区间下端 &gt; 50 K、只有远处参照抓得到的违规点，
    /// 并把 HANDOVER 覆盖表的违规段端点当决定记录核。
    /// </summary>
    [Fact]
    public void 包络判法_门按定义独立算全网格八个寿命_离下端远的违规点与驼峰后半段都标形状不物理()
    {
        double[] hours = { 0.1, 1, 10, 100, 1000, 8760, 10000, 1e5 };   // 0.1 h = 纯铂原始点时间轴下端

        // 决定记录：HANDOVER 0.-6H 节「出处与覆盖表」持久强度一栏的包络违规段（寿命 h, 首 °C, 末 °C）。
        // 判法或系数一变这里就红 —— 先改那张表，再改这里。没列出的牌号 = 无违规段；a(T) ≥ 0 段表里一段都没有。
        var recorded = new Dictionary<string, (double H, double Lo, double Hi)[]>
        {
            ["Pt"] = new[] { (0.1, 1101.0, 1183.0), (1e5, 1228.0, 1349.0) },
            ["FKS16/PtRh-9010"] = new[] { (0.1, 1466.0, 1500.0), (1.0, 1481.0, 1500.0), (10.0, 1496.0, 1500.0) },
            ["Tanaka-ZGS-Pt"] = new[]
            {
                (0.1, 1447.0, 1500.0), (1.0, 1001.0, 1042.0), (1.0, 1466.0, 1500.0), (10.0, 1001.0, 1054.0), (10.0, 1481.0, 1500.0),
                (100.0, 1001.0, 1059.0), (100.0, 1492.0, 1500.0), (1000.0, 1001.0, 1061.0), (1000.0, 1500.0, 1500.0),
                (8760.0, 1001.0, 1063.0), (10000.0, 1001.0, 1063.0), (1e5, 1001.0, 1064.0),
            },
            ["Tanaka-ZGS-PtRh10"] = new[]
            {
                (0.1, 1001.0, 1127.0), (0.1, 1164.0, 1349.0), (0.1, 1491.0, 1500.0), (1.0, 1001.0, 1077.0), (10.0, 1001.0, 1054.0),
                (100.0, 1001.0, 1038.0), (1000.0, 1001.0, 1025.0), (8760.0, 1001.0, 1014.0), (10000.0, 1001.0, 1013.0), (1e5, 1001.0, 1003.0),
            },
        };

        int points = 0, envViol = 0, aUpN = 0, outProbes = 0;
        foreach (var g in MaterialDb.WithCreep)
        {
            var segs = PtCreepWorkbook.ShapeScan(g.Name, hours);
            _o.WriteLine($"{g.Name}（{g.CreepTMinC:0}–{g.CreepTMaxC:0} °C{(g.RangeConfirmed ? "" : "，推定")}）："
                + (segs.Count == 0 ? "无" : string.Join("；", segs.Select(s => $"{s.What} {s.LoC:0}–{s.HiC:0} °C"))));

            var rec = recorded.TryGetValue(g.Name, out var r) ? r : Array.Empty<(double H, double Lo, double Hi)>();
            var scan = segs.Select(s => (s.Hours, s.LoC, s.HiC)).ToList();
            Assert.True(scan.Count == rec.Length && rec.All(x => scan.Contains(x)),
                $"{g.Name}：扫描违规段与 HANDOVER 覆盖表的决定记录不同 —— 扫描 {string.Join("；", scan.Select(s => $"{s.Hours:g} h {s.LoC:0}–{s.HiC:0}"))}，"
                + $"记录 {string.Join("；", rec.Select(s => $"{s.H:g} h {s.Lo:0}–{s.Hi:0}"))}");

            var hs = PtCreepWorkbook.RawBlocks.Where(b => b.Grade == g.Name).SelectMany(b => b.Hours).ToArray();   // 原始点逐格由上面的门对 xlsx 核
            int n = (int)Math.Round(g.CreepTMaxC - g.CreepTMinC);
            Assert.True(Bit(g.CreepTMinC + n, g.CreepTMaxC), $"{g.Name} 拟合区间不是整 K");
            foreach (double h in hours)
            {
                bool timeOut = hs.Length > 0 && (h < hs.Min() || h > hs.Max());

                void Check(double tt, double lowerMin, bool onGrid)
                {
                    double s = g.RuptureStressMPa(tt, h);
                    bool env = s > lowerMin;           // 定义：高于区间下端到 tt 之间（不含 tt）任一 1 K 网格温度的 σ
                    bool aUp = g.A(tt) >= 0;           // 强度随时间升高
                    var expect = CreepCoverage.InConfirmedRange;   // 第四轮（2026-09-15，Opus 5）：判不了时间外推排到时间外推之后
                    if (timeOut) expect = CreepCoverage.TimeExtrapolated;
                    if (!g.RangeConfirmed || hs.Length == 0) expect = CreepCoverage.UnconfirmedRangeTimeUnjudgeable;
                    if (env || aUp) expect = CreepCoverage.ShapeNonMonotone;
                    var v = PtCreepWorkbook.RuptureStress(g.Name, tt, h);
                    Assert.True(v.Coverage == expect && Bit(v.Value, s),
                        $"{g.Name} {tt} °C × {h:g} h：σ {s:0.#####} MPa，下方最低 {lowerMin:0.#####}（包络{(env ? "违规" : "不违规")}，a {(aUp ? "≥" : "<")} 0）"
                        + $" ⇒ 应为 {expect}，分档却是 {v.Coverage}（值 {v.Value:R}）：{v.Note}");
                    if (onGrid)
                    {
                        bool inEnv = segs.Any(x => x.Hours == h && tt >= x.LoC && tt <= x.HiC);
                        bool inA = segs.Any(x => double.IsNaN(x.Hours) && tt >= x.LoC && tt <= x.HiC);
                        Assert.True(inEnv == env && inA == aUp,
                            $"{g.Name} {tt} °C × {h:g} h：ShapeScan 包络段{(inEnv ? "含" : "不含")}、a≥0 段{(inA ? "含" : "不含")}，门按定义算的是{(env ? "违规" : "不违规")}／a {(aUp ? "≥" : "<")} 0");
                    }
                    points++; if (env) envViol++; if (aUp) aUpN++;
                }

                double runMin = double.PositiveInfinity;
                for (int k = 0; k <= n; k++)
                {
                    double t = g.CreepTMinC + k;                       // 1 K 网格（口径 D2 写死，不读 EnvelopeStepC）
                    Check(t, runMin, true);                             // 网格点：参照不含自己
                    runMin = Math.Min(runMin, g.RuptureStressMPa(t, h));
                    if (k < n) Check(t + 0.5, runMin, false);           // 网格间：参照含 t
                }
                // 第五轮（2026-09-16，Opus 5，核验员 V12c）：网格两端各半 K 的区间外探针，期望无数据（值 NaN）—— 区间放宽 0.5 K 这里就红
                foreach (double tOut in new[] { g.CreepTMinC - 0.5, g.CreepTMaxC + 0.5 })
                {
                    var vo = PtCreepWorkbook.RuptureStress(g.Name, tOut, h);
                    Assert.True(vo.Coverage == CreepCoverage.NoData && double.IsNaN(vo.Value) && !vo.HasValue,
                        $"{g.Name} {tOut} °C × {h:g} h 在拟合区间 {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C 外半 K，应无数据：{vo.Coverage}（值 {vo.Value:R}）{vo.Note}");
                    outProbes++;
                }
            }
        }
        _o.WriteLine($"门按定义独立核对 {points} 点（网格 + 半 K × 8 个寿命）：包络违规 {envViol} 点、a(T) ≥ 0 {aUpN} 点；区间外半 K 探针 {outProbes} 个都无数据");
        Assert.True(envViol > 0 && outProbes > 0, "反自证：包络违规点与区间外探针都必须真的核到");

        // 第四轮（2026-09-15，Opus 5，复核 minor）：上面的决定记录只扫了 0.1–1e5 h 八个寿命，「无违规段」只在这个范围内成立。
        // 范围外只报（HANDOVER 覆盖表照这里的输出写范围外的例子）；纯铂 0.01 h 那段是 HANDOVER 写出的例子，断言它。
        foreach (double hOut in new[] { 0.01, 1e6 })
            foreach (var g in MaterialDb.WithCreep)
            {
                var segsOut = PtCreepWorkbook.ShapeScan(g.Name, new[] { hOut }).Where(x => !double.IsNaN(x.Hours)).ToList();
                if (segsOut.Count > 0)
                    _o.WriteLine($"【范围外，只报】{g.Name} × {hOut:g} h 包络违规段：{string.Join("；", segsOut.Select(x => $"{x.LoC:0}–{x.HiC:0} °C"))}");
            }
        var ptOut = PtCreepWorkbook.ShapeScan("Pt", new[] { 0.01 }).Where(x => !double.IsNaN(x.Hours)).Select(x => (x.LoC, x.HiC)).ToList();
        Assert.True(ptOut.Count == 1 && ptOut[0] == (1101.0, 1217.0), "HANDOVER 写的范围外例子（纯铂 0.01 h 1101–1217 °C）与扫描不同：" + string.Join("；", ptOut));

        // 离区间下端远、只有远处参照（T−50 K 之外）才抓得到的违规点：复核注入 W50（两处同步只和 T−50 K 以内比）会把它们退回「确认区间内」
        foreach (var (grade, t, h) in new[] { ("Tanaka-ZGS-Pt", 1060.0, 1000.0), ("Tanaka-ZGS-PtRh10", 1070.0, 1.0), ("Tanaka-ZGS-PtRh10", 1320.0, 0.1), ("Pt", 1180.0, 0.1) })
        {
            var g = MaterialDb.Get(grade);
            double s = g.RuptureStressMPa(t, h);
            double near = double.PositiveInfinity, farMin = double.PositiveInfinity, tFar = double.NaN;
            for (double u = g.CreepTMinC; u < t - 1e-9; u += 1)
            {
                double su = g.RuptureStressMPa(u, h);
                if (u >= t - 50 - 1e-9) near = Math.Min(near, su);
                else if (su < farMin) { farMin = su; tFar = u; }
            }
            Assert.True(t - g.CreepTMinC > 50 && s <= near,
                $"反自证：{grade} {t} °C × {h:g} h 应离区间下端 > 50 K，且 T−50 K 以内没有更低的强度（σ {s:0.###}，窗口内最低 {near:0.###}）");
            Assert.True(s > farMin, $"{grade} {t} °C × {h:g} h 按定义应违规：σ {s:0.###} 不高于远处最低 {farMin:0.###}（{tFar:0} °C）");
            var v = PtCreepWorkbook.RuptureStress(grade, t, h);
            _o.WriteLine($"{grade} {t:0} °C × {h:g} h：{s:0.###} MPa 高于 {tFar:0} °C 的 {farMin:0.###}（T−50 K 以内最低 {near:0.###}）→ {v.Coverage}");
            Assert.True(v.Coverage == CreepCoverage.ShapeNonMonotone && v.HasValue && Bit(v.Value, s), $"{grade} {t} °C × {h:g} h 必须标形状不物理：{v.Note}");
        }

        var zgs = MaterialDb.Get("Tanaka-ZGS-Pt");
        var at = PtCreepWorkbook.RuptureStress("Tanaka-ZGS-Pt", 1045, 1000);
        _o.WriteLine($"Tanaka-ZGS-Pt 1045 °C × 1000 h：{at.Value:0.###} MPa，{at.Coverage}：{at.Note}");
        Assert.True(at.Coverage == CreepCoverage.ShapeNonMonotone && at.HasValue, "驼峰后半段（斜率已转负、强度仍高于 1000 °C）必须标形状不物理：" + at.Note);
        Assert.True(zgs.RuptureStressMPa(1046, 1000) < zgs.RuptureStressMPa(1045, 1000), "反自证：1045 °C 处局部斜率应已为负（第一轮的局部斜率判法看不见它）");

        var hi = PtCreepWorkbook.RuptureStress("Tanaka-ZGS-Pt", 1490, 1);
        Assert.True(hi.Coverage == CreepCoverage.ShapeNonMonotone, "高温端上升段（1 h）必须标：" + hi.Note);
        var low = PtCreepWorkbook.RuptureStress("Tanaka-ZGS-Pt", 1100, 1000);
        Assert.True(low.Coverage == CreepCoverage.InConfirmedRange, "1100 °C × 1000 h 已回到包络下方：" + low.Note);
    }

    /// <summary>
    /// 口径 D2（2026-09-15 第二轮）：原始点所在温度（各牌号各块的 1000／1100／…°C）× 该块的每个寿命，
    /// 在包络单调段**不许被误标**；若某原始点本身落在包络违规段，照实点名打印（不改规则去躲）。
    /// 「是否违规」由本门按定义（σ(T) 高于区间下端到 T 之间任一 1 K 温度的 σ）直接用 RuptureStressMPa 算。
    /// </summary>
    [Fact]
    public void 原始点所在温度在单调段不被误标_落在违规段的照实点名()
    {
        int clean = 0, inViolation = 0;
        foreach (var b in PtCreepWorkbook.RawBlocks)
        {
            var g = MaterialDb.Get(b.Grade);
            foreach (double h in b.Hours)
            {
                double s = g.RuptureStressMPa(b.TempC, h);
                bool viol = false; double tLow = double.NaN, sLow = double.NaN;
                for (double t = g.CreepTMinC; t < b.TempC - 1e-9; t += 1)
                {
                    double st = g.RuptureStressMPa(t, h);
                    if (s > st && (!viol || st < sLow)) { viol = true; tLow = t; sLow = st; }
                }
                var v = PtCreepWorkbook.RuptureStress(b.Grade, b.TempC, h);
                if (viol)
                {
                    inViolation++;
                    _o.WriteLine($"【点名】原始点 {b.Grade} {b.TempC:0} °C × {h:g} h 落在包络违规段：模型 {s:0.###} MPa 高于 {tLow:0} °C 的 {sLow:0.###} MPa → {v.Coverage}");
                    Assert.True(v.Coverage == CreepCoverage.ShapeNonMonotone, $"{b.Grade} {b.TempC} °C × {h} h 违规却标 {v.Coverage}");
                }
                else
                {
                    clean++;
                    Assert.True(v.Coverage == CreepCoverage.InConfirmedRange, $"原始点 {b.Grade} {b.TempC} °C × {h:g} h 在单调段却被标 {v.Coverage}：{v.Note}");
                }
            }
        }
        _o.WriteLine($"原始点（温度 × 该块寿命）：单调段 {clean} 个都标实测确认区间内；落在违规段 {inViolation} 个（上面逐个点名）");
        Assert.True(clean > 0, "反自证：单调段的原始点必须真的核到");
    }

    /// <summary>
    /// 阻断 3④（2026-09-15 第二轮）：「时间外推」的测试点取 ShapeScan 证明单调（不在任何违规段、a &lt; 0）的点，断言**等于**；
    /// 推定区间同理。第一轮的点（纯铂 1300 °C × 1e5 h）正落在上升段，删掉时间外推规则门也不红。
    /// </summary>
    [Fact]
    public void 时间外推与推定区间_取扫描证明单调的点断言等于()
    {
        void Clean(string grade, double tC, double h)
        {
            var segs = PtCreepWorkbook.ShapeScan(grade, new[] { h });
            Assert.False(segs.Any(s => tC >= s.LoC - 1e-9 && tC <= s.HiC + 1e-9), $"反自证：{grade} {tC} °C × {h:g} h 应在扫描证明单调的段上");
            Assert.True(MaterialDb.Get(grade).A(tC) < 0);
        }
        var span = PtCreepWorkbook.RawHourSpan("Pt");
        double far = span.MaxH * 10, near = span.MinH / 10;
        Clean("Pt", 1150, far);
        var tf = PtCreepWorkbook.RuptureStress("Pt", 1150, far);
        Assert.True(tf.Coverage == CreepCoverage.TimeExtrapolated && tf.HasValue, tf.Note);
        Clean("Pt", 1300, near);   // 纯铂 0.01 h 的包络违规段在 1101–1217 °C，1150 °C 不能用
        Assert.Equal(CreepCoverage.TimeExtrapolated, PtCreepWorkbook.RuptureStress("Pt", 1300, near).Coverage);
        Clean("Pt", 1150, 1000);
        Assert.Equal(CreepCoverage.InConfirmedRange, PtCreepWorkbook.RuptureStress("Pt", 1150, 1000).Coverage);

        Clean("Umicore-PtRh10", 1200, 1000);
        var um = PtCreepWorkbook.RuptureStress("Umicore-PtRh10", 1200, 1000);
        Assert.True(um.Coverage == CreepCoverage.UnconfirmedRangeTimeUnjudgeable && um.Note.Contains("没有时间上限"), um.Note);
        Clean("Umicore-PtRh10", 1200, 1e6);
        Assert.Equal(CreepCoverage.UnconfirmedRangeTimeUnjudgeable, PtCreepWorkbook.RuptureStress("Umicore-PtRh10", 1200, 1e6).Coverage);   // 无原始点 ⇒ 判不了时间外推

        // 第四轮（2026-09-15，Opus 5，复核 minor）：时间轴端点的相对容差写死 1e-9（与两向互逆同口径），两侧各取 2 倍与半倍核
        Assert.Equal(1e-9, PtCreepWorkbook.TimeAxisRelTol);
        foreach (var (tt, hh, outside) in new[]
                 {
                     (1150.0, span.MaxH * (1 + 2e-9), true), (1150.0, span.MaxH * (1 + 0.5e-9), false),
                     (1300.0, span.MinH * (1 - 2e-9), true), (1300.0, span.MinH * (1 - 0.5e-9), false),
                 })
        {
            Clean("Pt", tt, hh);
            var v = PtCreepWorkbook.RuptureStress("Pt", tt, hh);
            Assert.True(v.Coverage == (outside ? CreepCoverage.TimeExtrapolated : CreepCoverage.InConfirmedRange),
                $"纯铂 {tt} °C × {hh:R} h（时间轴 {span.MinH:g}–{span.MaxH:g} h，容差 1e-9）应{(outside ? "" : "不")}算时间外推：{v.Coverage} {v.Note}");
        }

        var cold = PtCreepWorkbook.RuptureStress("Pt", MaterialDb.Get("Pt").CreepTMinC - 100, 1000);
        Assert.True(cold.Coverage == CreepCoverage.NoData && double.IsNaN(cold.Value), cold.Note);
        var none = PtCreepWorkbook.RuptureStress("Cu", 1200, 1000);
        Assert.True(none.Coverage == CreepCoverage.NoData && double.IsNaN(none.Value), none.Note);
        foreach (double bad in new[] { double.NaN, 0, -5 })
        {
            var v = PtCreepWorkbook.RuptureStress("Pt", 1150, bad);
            Assert.True(v.Coverage == CreepCoverage.NoData && double.IsNaN(v.Value) && !v.HasValue, $"寿命 {bad} 应无数据：{v.Coverage} {v.Note}");
            var l = PtCreepWorkbook.RuptureLife("Pt", 1150, bad);
            Assert.True(l.Coverage == CreepCoverage.NoData && double.IsNaN(l.Value), $"应力 {bad} 应无数据：{l.Coverage} {l.Note}");
        }
        Assert.Equal(CreepCoverage.NoData, PtCreepWorkbook.RuptureStress("Pt", double.NaN, 1000).Coverage);
        _o.WriteLine($"纯铂 1150 °C × {far:g} h：{tf}");
    }

    /// <summary>
    /// 阻断 3⑤（2026-09-15 第二轮）：在互逆点上，「给寿命求应力」与「给应力求寿命」两个方向的覆盖类别必须相同，值互逆。
    /// 寿命避开原始点时间轴端点（端点另有一条门：原始点时间轴端点寿命上两向覆盖类别相同_时间轴判断带相对1e9容差）。
    /// </summary>
    [Fact]
    public void 互逆点上两向覆盖类别相同()
    {
        double[] hours = { 0.03, 3, 300, 5000, 3e4, 3e5 };
        int n = 0; var seen = new HashSet<CreepCoverage>();
        foreach (var g in MaterialDb.WithCreep)
            for (double t = g.CreepTMinC; t <= g.CreepTMaxC + 1e-9; t += 7)
                foreach (double h in hours)
                {
                    var s = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                    Assert.True(s.HasValue, s.Note);
                    var l = PtCreepWorkbook.RuptureLife(g.Name, t, s.Value);
                    Assert.True(l.Coverage == s.Coverage, $"{g.Name} {t} °C × {h:g} h：求应力标 {s.Coverage}，由该应力求寿命标 {l.Coverage}（{l.Note}）");
                    Assert.InRange(Math.Abs(l.Value - h) / h, 0, 1e-9);
                    seen.Add(s.Coverage); n++;
                }
        _o.WriteLine($"两向覆盖核对 {n} 点，覆盖到的类别：{string.Join("、", seen)}");
        Assert.True(seen.Contains(CreepCoverage.TimeExtrapolated) && seen.Contains(CreepCoverage.ShapeNonMonotone)
                    && seen.Contains(CreepCoverage.UnconfirmedRangeTimeUnjudgeable) && seen.Contains(CreepCoverage.InConfirmedRange),
            "反自证：时间外推、形状不物理、推定区间、确认区间四类都要在互逆点上核到");
        var life = PtCreepWorkbook.RuptureLife("Pt", 1300, 2.0);
        Assert.True(life.HasValue && Bit(life.Value, MaterialDb.Get("Pt").RuptureLifeHours(1300, 2.0)), life.Note);
    }

    /// <summary>
    /// 复核 minor（2026-09-15 第四轮，Opus 5）：原始点时间轴端点寿命（纯铂 0.1／10000 h，其余 Tanaka 1／10000 h）× 1 K 网格上，
    /// 「给寿命求应力」与「由该应力反求寿命」两向覆盖类别相同。第三轮时间轴判断不带容差，反求寿命差 1e-15 量级就翻面
    /// （Pt 1213 °C × 0.1 h 反求 0.09999999999999985 h 标时间外推）；现带相对 1e-9 容差（与互逆容差同口径）。
    /// </summary>
    [Fact]
    public void 原始点时间轴端点寿命上两向覆盖类别相同_时间轴判断带相对1e9容差()
    {
        int n = 0, strictOut = 0, flip = 0;
        foreach (var g in MaterialDb.WithCreep)
        {
            var (hMin, hMax) = PtCreepWorkbook.RawHourSpan(g.Name);
            if (double.IsNaN(hMin)) continue;
            int k = (int)Math.Round(g.CreepTMaxC - g.CreepTMinC);
            for (int i = 0; i <= k; i++)
                foreach (double h in new[] { hMin, hMax })
                {
                    double t = g.CreepTMinC + i;
                    var s = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                    Assert.True(s.HasValue, s.Note);
                    var l = PtCreepWorkbook.RuptureLife(g.Name, t, s.Value);
                    Assert.InRange(Math.Abs(l.Value - h) / h, 0, 1e-9);
                    Assert.True(l.Coverage == s.Coverage, $"{g.Name} {t} °C × {h:g} h：求应力标 {s.Coverage}，反求寿命 {l.Value:R} h 标 {l.Coverage}（{l.Note}）");
                    if (l.Value < hMin || l.Value > hMax)
                    {
                        strictOut++;   // 反求寿命落在时间轴外的组
                        // 其中类别真会翻面的：求应力那向不是「形状不物理」（形状不物理排在时间外推之后，翻不动）。2026-09-16，Opus 5
                        if (s.Coverage < CreepCoverage.ShapeNonMonotone) flip++;
                    }
                    n++;
                }
        }
        _o.WriteLine($"时间轴端点两向覆盖核对 {n} 组；反求寿命落在时间轴外的 {strictOut} 组，其中不带容差类别会翻面的 {flip} 组（其余求应力那向已是形状不物理）");
        Assert.True(strictOut > 0 && flip > 0, "反自证：反求寿命越过端点、且不带容差会翻面的组必须真的核到，否则容差没被考到");
    }

    /// <summary>
    /// 2026-09-16 第五轮（Opus 5，核验员 V6）：时间轴端点容差 1e-9 此前只有「时间外推与推定区间」那条的纯铂两个 2e-9 探针守着，
    /// 上端写死成 1e-3 只红 1 条。这里对五个有原始点的牌号 × 区间内每 1 K × 时间轴两端 × 端点 ±1e-9 相对容差内外各一点
    /// （内 0.5e-9、外 2e-9），两向（求应力、由该应力反求寿命）覆盖类别都断言**等于**：形状规则（门按定义算）没插手时内点「实测确认区间内」、
    /// 外点「时间外推」；插手时两向都「形状不物理」。容差 1e-9 写死。
    /// </summary>
    [Fact]
    public void 时间轴端点容差1e9内外各一点_五个牌号全区间两端两向类别断言等于()
    {
        Assert.Equal(1e-9, PtCreepWorkbook.TimeAxisRelTol);
        int inside = 0, outside = 0, shape = 0; var grades = new List<string>();
        foreach (var g in MaterialDb.WithCreep)
        {
            var (hMin, hMax) = PtCreepWorkbook.RawHourSpan(g.Name);
            if (double.IsNaN(hMin)) continue;
            grades.Add(g.Name);
            int k = (int)Math.Round(g.CreepTMaxC - g.CreepTMinC);
            for (int i = 0; i <= k; i++)
            {
                double t = g.CreepTMinC + i;
                foreach (var (h, isOut) in new[] { (hMin * (1 - 2e-9), true), (hMin * (1 - 0.5e-9), false), (hMax * (1 + 0.5e-9), false), (hMax * (1 + 2e-9), true) })
                {
                    double runMin = double.PositiveInfinity;
                    for (double u = g.CreepTMinC; u < t - 1e-9; u += 1) runMin = Math.Min(runMin, g.RuptureStressMPa(u, h));
                    bool shapeBad = g.RuptureStressMPa(t, h) > runMin || g.A(t) >= 0;
                    var expect = shapeBad ? CreepCoverage.ShapeNonMonotone : isOut ? CreepCoverage.TimeExtrapolated : CreepCoverage.InConfirmedRange;
                    var s = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                    Assert.True(s.HasValue && s.Coverage == expect,
                        $"{g.Name} {t} °C × {h:R} h（时间轴 {hMin:g}–{hMax:g} h，端点{(isOut ? "外 2e-9" : "内 0.5e-9")}）应为 {expect}，求应力标 {s.Coverage}：{s.Note}");
                    var l = PtCreepWorkbook.RuptureLife(g.Name, t, s.Value);
                    Assert.True(l.HasValue && l.Coverage == expect && Math.Abs(l.Value - h) / h <= 1e-9,
                        $"{g.Name} {t} °C × {h:R} h：由 {s.Value:R} MPa 反求寿命 {l.Value:R} h 标 {l.Coverage}，应为 {expect}：{l.Note}");
                    if (shapeBad) shape++; else if (isOut) outside++; else inside++;
                }
            }
        }
        _o.WriteLine($"时间轴端点容差核对 {string.Join("、", grades)}：内点 {inside}、外点 {outside}、形状规则插手 {shape}（每点两向）");
        Assert.True(inside > 0 && outside > 0 && shape > 0, "反自证：内点、外点、形状插手三种都必须真的核到");
        Assert.Contains("Pt", grades); Assert.Contains("Tanaka-ZGS-Pt", grades);
    }

    /// <summary>
    /// 复核 minor（2026-09-15 第四轮，Opus 5，主会话定）：「推定区间·无原始点·判不了时间外推」排在「时间外推」之后。
    /// 调用方按「覆盖 ≥ 时间外推 ⇒ 判不了／不可信」取值时，FKS16 两个、Umicore 两个牌号在任何温度与寿命上都不许漏过
    /// （第三轮它们标 InUnconfirmedRange = 1 &lt; TimeExtrapolated = 2，会漏过；FKS16/Pt 在「选最省」里与 ZGS-Pt 并列，HANDOVER M1）。
    /// 同时核数据模型不变量：推定区间的牌号恰是没有原始点的牌号。
    /// </summary>
    [Fact]
    public void 推定区间无原始点的牌号判不了时间外推_按大于等于时间外推取值不漏过()
    {
        Assert.True(CreepCoverage.InConfirmedRange < CreepCoverage.TimeExtrapolated
                    && CreepCoverage.TimeExtrapolated < CreepCoverage.UnconfirmedRangeTimeUnjudgeable
                    && CreepCoverage.UnconfirmedRangeTimeUnjudgeable < CreepCoverage.ShapeNonMonotone
                    && CreepCoverage.ShapeNonMonotone < CreepCoverage.NoData, "覆盖类别次序不是「确认 < 时间外推 < 判不了 < 形状不物理 < 无数据」");
        foreach (var g in MaterialDb.WithCreep)
        {
            bool noRaw = double.IsNaN(PtCreepWorkbook.RawHourSpan(g.Name).MinH);
            Assert.True(g.RangeConfirmed == !noRaw, $"{g.Name}：区间{(g.RangeConfirmed ? "已确认" : "推定")}，却{(noRaw ? "没有" : "有")}原始点");
        }

        double[] hours = { 0.01, 0.1, 1, 10, 100, 1000, 1e4, 1e5, 1e6, 1e7 };
        int n = 0, unjudgeable = 0, shape = 0; var names = new List<string>();
        foreach (var g in MaterialDb.WithCreep.Where(x => !x.RangeConfirmed))
        {
            names.Add(g.Name);
            for (double t = g.CreepTMinC; t <= g.CreepTMaxC + 1e-9; t += 5)
                foreach (double h in hours)
                {
                    // 第五轮（2026-09-16，Opus 5，核验员 V7）：断言**等于**，不再是「≥ 时间外推」—— 无原始点牌号降档成「时间外推」也满足 ≥，本门曾不红。
                    // 形状规则由门按定义算（包络：σ(t) 高于区间下端到 t 之间任一 1 K 温度的 σ；或 a(t) ≥ 0），插手时两向都应是「形状不物理」。
                    double runMin = double.PositiveInfinity;
                    for (double u = g.CreepTMinC; u < t - 1e-9; u += 1) runMin = Math.Min(runMin, g.RuptureStressMPa(u, h));
                    bool shapeBad = g.RuptureStressMPa(t, h) > runMin || g.A(t) >= 0;
                    var expect = shapeBad ? CreepCoverage.ShapeNonMonotone : CreepCoverage.UnconfirmedRangeTimeUnjudgeable;
                    var v = PtCreepWorkbook.RuptureStress(g.Name, t, h);
                    Assert.True(v.HasValue && v.Coverage == expect && v.Note.Contains("判不了"),
                        $"{g.Name} {t} °C × {h:g} h：应为 {expect}，分档却是 {v.Coverage}（{v.Note}）");
                    var l = PtCreepWorkbook.RuptureLife(g.Name, t, v.Value);
                    Assert.True(l.Coverage == expect, $"{g.Name} {t} °C 由 {v.Value:g} MPa 求寿命：应为 {expect}，分档却是 {l.Coverage}（{l.Note}）");
                    if (shapeBad) shape++; else unjudgeable++;
                    n++;
                }
        }
        Assert.True(unjudgeable > 0 && shape > 0, "反自证：形状规则没插手（应恰为「判不了」）与插手（形状不物理）的点都必须真的核到");
        Assert.Contains("FKS16/Pt", names);
        Assert.Contains("FKS16/PtRh-9010", names);
        Assert.Contains("Umicore-PtRh10", names);
        Assert.Contains("Umicore-PtRh20", names);
        // 反自证：同一取值规则对有原始点、在时间轴内、形状干净的点不误伤（Tanaka-ZGS-Pt 与 FKS16/Pt 同为 1300 °C × 1000 h 对照）
        var zgs = PtCreepWorkbook.RuptureStress("Tanaka-ZGS-Pt", 1300, 1000);
        var fks = PtCreepWorkbook.RuptureStress("FKS16/Pt", 1300, 1000);
        Assert.True(zgs.Coverage == CreepCoverage.InConfirmedRange && fks.Coverage == CreepCoverage.UnconfirmedRangeTimeUnjudgeable, $"ZGS-Pt {zgs.Coverage}，FKS16/Pt {fks.Coverage}");
        _o.WriteLine($"推定区间牌号 {string.Join("、", names)} × 每 5 K × {hours.Length} 个寿命共 {n} 点：{unjudgeable} 点恰为「推定区间·无原始点·判不了时间外推」、{shape} 点形状不物理；"
                   + $"对照 1300 °C × 1000 h：Tanaka-ZGS-Pt {zgs.Value:0.###} MPa {zgs.Coverage}，FKS16/Pt {fks.Value:0.###} MPa {fks.Coverage}");
    }

    /// <summary>
    /// 复核 minor（2026-09-15 第四轮，Opus 5）：a(T) ≥ 0（强度随时间升高）规则没有门守 —— 现有 9 个牌号区间内 a 处处 &lt; 0，
    /// 包络门里的 aUp 比对是空转（注入 J3 把规则改成 if (false) 全绿）。用测试里构造的人造系数牌号测（不进 MaterialDb.All）：
    /// a(T) = T(K) − 1523.4（在 1250.25 °C 过零），b(T) = 3000；寿命取 1 h ⇒ log10 t = 0，σ = 10^(b/T) 与 a 无关、随温度严格下降，
    /// 包络判法抓不到任何点 ⇒ a ≥ 0 的温度只有这条规则能标「形状不物理」。
    /// </summary>
    [Fact]
    public void a大于等于0规则由人造系数的牌号测_只有这条规则能标出的点必须标形状不物理()
    {
        var g = new PtGrade
        {
            Name = "门内人造牌号·a在1250.25度过零", HasCreep = true, CreepTMinC = 1000, CreepTMaxC = 1500, RangeConfirmed = false,
            Ca = new[] { 0, 0, 0, 0, 1.0, -1523.4 }, Cb = new[] { 0, 0, 0, 0, 0, 3000.0 },
        };
        Assert.False(MaterialDb.All.ContainsKey(g.Name));
        const double h = 1;
        int up = 0, down = 0; double prev = double.PositiveInfinity;
        for (int i = 0; i <= 1000; i++)
        {
            double t = 1000 + 0.5 * i;
            double s = g.RuptureStressMPa(t, h);
            Assert.True(s < prev, $"反自证：人造牌号 {t} °C × 1 h 的强度 {s:R} 不低于前一点 {prev:R} —— 包络判法会插手，测不出 a 规则");
            prev = s;
            bool aUp = g.A(t) >= 0;
            var v = PtCreepWorkbook.RuptureStress(g, t, h);
            var expect = aUp ? CreepCoverage.ShapeNonMonotone : CreepCoverage.UnconfirmedRangeTimeUnjudgeable;
            Assert.True(v.Coverage == expect && Bit(v.Value, s), $"人造牌号 {t} °C × 1 h：a = {g.A(t):R}，应为 {expect}，分档 {v.Coverage}（{v.Note}）");
            if (aUp) { up++; Assert.Contains("a(", v.Note); } else down++;
        }
        _o.WriteLine($"人造牌号：a ≥ 0 的 {up} 点标形状不物理，a < 0 的 {down} 点标推定区间·判不了时间外推；1250 °C a = {g.A(1250):R}，1250.5 °C a = {g.A(1250.5):R}");
        Assert.True(up > 0 && down > 0, "反自证：a ≥ 0 与 a < 0 两边都必须真的核到");
    }
}
