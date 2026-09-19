using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

// ============================================================================
//  电阻率：工作簿测试值 + 覆盖查询（2026-09-15，Opus 5）
//
//  式子不变：ρ(T) = ρ0·[1 + α(T−T0) + β(T−T0)²]（μΩ·cm），即 PtGrade.ResistivityOhmM —— 本档**不另写一份**，
//  只加「这个温度落在工作簿测试值的什么位置」。
//
//  两位把关人实测（2026-09-15）：
//    · 「測試值」**不全是实测**：FKS16/Pt、Pt-Rh 90-10、FKS16 PtRh-9010 三行 100–600 °C 是等差数（疑为插补，出处未注），
//      拟合残差恰在这段最大（FKS16/Pt 400 °C +6.4 %，90-10 600 °C +7.7 %）。
//    · 「区间内」要按**相邻测点间距**判，不能只看首尾：Pt-Rh 80-20 只有 20、1000、1200–1500 °C，
//      20–1000 °C 空了 980 K（法兰冷端正好在这段）；I33:Q33、S33 是字面 0（无公式）= 缺测。
//    · Cu 只有 H44 = 1.7（20 °C），X44/Y44 字面 0 = 缺测当 0 ⇒ 既有 MaterialDb 让 Cu 在任何温度都是 1.7；
//      本查询标「单点」，20 °C 以外无数据。
//    · Pd 100 °C 测试值 10.25，计算值 13.44，一个点占该行 SSE 的 91 % ⇒ 疑为抄错（只点名，不改）。
//    · Pd 700 °C（O38 = 31.76）、900 °C（Q38 = 35.74）恰为相邻两点的中点（两位小数的数据）⇒ 登记为疑插补（2026-09-15 第二轮，Opus 5）。
//    · 测试值区间外一律「外推」，只有二次式 dρ/dT ≤ 0 的温度段标「无数据」（口径 D4④，2026-09-15 第二轮，Opus 5）：
//      Ni 在 T0 − α/(2β) ≈ 1337.2 °C 以上、Pd 在 ≈ 1900.0 °C 以上（门按式子实算并打印各牌号的这个温度）。
//    · 工作簿 α、β 是 Solver 近似解：纯铂的 SSE 0.011488 比该式真最小 0.011435 高 0.46 %，其余 6 行都在真最小值上（高出 < 0.01 %）
//      ⇒ 门只复现 SSE 缓存，不按算法复算 α、β。
//
//  门：Pt_Optimize.Tests/R48ResistivityWorkbookTests.cs 直接读 xlsx 逐格核对（不手抄）。
// ============================================================================

/// <summary>电阻率查询的覆盖类别，数值越大越不可信。2026-09-15，Opus 5。</summary>
public enum ResistivityCoverage
{
    /// <summary>落在两个相邻工作簿测试值之间（间距 ≤ 100 K），或正落在一个测试值上。</summary>
    BetweenTestValues = 0,
    /// <summary>同上，但夹住它的测试值里有「疑插补／疑抄错」的点。</summary>
    BetweenSuspectTestValues = 1,
    /// <summary>
    /// 首尾测试值之间，但相邻测试值间距 &gt; 100 K（测点空档）。阈值依据：工作簿 100 °C 以上温度栏步长就是 100 K，
    /// 所以只有真缺点才标（0→20→100 °C 那两格间距 20／80 K 不算）；目前只有 Pt-Rh/80-20 触发。
    /// </summary>
    InsideGap = 2,
    /// <summary>整行只有一个测试值，正落在它上面（Cu 20 °C）。</summary>
    SinglePointOnly = 3,
    /// <summary>测试值区间外：外推（二次式在该温度 dρ/dT &gt; 0）。</summary>
    Extrapolated = 4,
    /// <summary>无数据：值为 NaN（温度 NaN；整行无测试值；单点行的其他温度；测试值区间外且二次式 dρ/dT ≤ 0）。</summary>
    NoData = 5,
}

/// <summary>测试值疑点的种类。2026-09-15 第二轮，Opus 5。</summary>
public enum ResistivitySuspectKind
{
    /// <summary>100 K 栏上的最长等差段（≥ 3 步），之后跳变：疑插补。</summary>
    ArithmeticRun,
    /// <summary>单点残差占该行 SSE 大半：疑抄错。</summary>
    Outlier,
    /// <summary>两位小数的数据恰为相邻两点中点：疑插补（证据弱于等差段）。</summary>
    Midpoint,
}

/// <summary>一行工作簿测试值。Values 与 <see cref="PtResistivityData.GridTempsC"/> 对齐，NaN = 空格（缺测）。2026-09-15，Opus 5。</summary>
public sealed record ResistivityTestRow(
    string DataGrade, string WorkbookLabelCell, int TestRow, int CalcRow, int SseRow, double[] Values,
    IReadOnlyList<(double FromC, double ToC, ResistivitySuspectKind Kind, string Why)> Suspect);

/// <summary>带覆盖类别的电阻率。2026-09-15，Opus 5。</summary>
public readonly record struct ResistivityValue(
    double OhmM, ResistivityCoverage Coverage, bool Borrowed, string DataGrade, string Note)
{
    public bool HasValue => Coverage != ResistivityCoverage.NoData;
}

/// <summary>《鉑金電氣計算.xlsx》R22–R44 的测试值目录与覆盖查询。2026-09-15，Opus 5。</summary>
public static class PtResistivityData
{
    public const string WorkbookFile = "鉑金電氣計算.xlsx";
    public const string SheetName = "鉑金電氣計算";

    /// <summary>G22:W22 温度 °C。</summary>
    public static readonly double[] GridTempsC =
        { 0, 20, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 };

    /// <summary>G..W 列字母，与 GridTempsC 对齐。</summary>
    public static readonly string[] GridCols =
        { "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W" };

    /// <summary>相邻测试值间距超过它就算「测点空档」：工作簿 100 °C 以上的温度栏步长。</summary>
    public const double MaxRegularGapK = 100;

    private const double N = double.NaN;   // 空格 = 缺测

    /// <summary>「測試值」行，逐格照 xlsx 存储值入码。E 列标签里的换行在 APP 牌号名里写成「/」。</summary>
    public static readonly IReadOnlyList<ResistivityTestRow> Rows = new[]
    {
        new ResistivityTestRow("Pt", "E23", 23, 24, 25,
            new[] { 9.83, 10.6, 13.7, 17.399999999999999, 21, 24.5, 27.9, 31.2, 34.299999999999997, 37.299999999999997,
                    40.299999999999997, 43.1, 45.8, 48.3, 50.8, 53.2, 55.4 },
            Array.Empty<(double, double, ResistivitySuspectKind, string)>()),
        new ResistivityTestRow("FKS16/Pt", "E26", 26, 27, 28,
            new[] { 10, 11.7, 13, 16, 19, 22, 25, 28, 34.200000000000003, 36.9, 39, 41.8, 44, 46.3, 48.3, 50.2, 52.1 },
            new[] { (100.0, 600.0, ResistivitySuspectKind.ArithmeticRun, "I26:N26 等差 3（13…28），之后跳到 34.2：疑为插补，出处未注") }),
        new ResistivityTestRow("Pt-Rh/90-10", "E29", 29, 30, 31,
            new[] { 19.100000000000001, 19.399999999999999, 22, 24, 26, 28, 30, 32, 38.5, 41.1, 43.1, 45.7, 47.9, 50.1,
                    52.1, 54.2, 56.1 },
            new[] { (100.0, 600.0, ResistivitySuspectKind.ArithmeticRun, "I29:N29 等差 2（22…32），之后跳到 38.5：疑为插补，出处未注") }),
        new ResistivityTestRow("Pt-Rh/80-20", "E32", 32, 33, 34,
            new[] { N, 20.8, N, N, N, N, N, N, N, N, N, 43.3, N, 48.2, 50, 51.6, 53.2 },   // T0 = 20（AA32）
            Array.Empty<(double, double, ResistivitySuspectKind, string)>()),
        new ResistivityTestRow("FKS16/PtRh-9010", "E35", 35, 36, 37,
            new[] { 19.899999999999999, 20.100000000000001, 23, 26, 29, 32, 35, 38, 40, 42.7, 44.6, 47.7, 50, 52.3, 54.4,
                    56.6, 58.6 },
            new[] { (100.0, 600.0, ResistivitySuspectKind.ArithmeticRun, "I35:N35 等差 3（23…38），之后 40：疑为插补，出处未注") }),
        new ResistivityTestRow("Pd", "E38", 38, 39, 40,
            new[] { 9.73, 10, 10.25, 16.809999999999999, 20.22, 23.62, 26.47, 29.31, 31.76, 34.21, 35.74,
                    37.270000000000003, N, N, N, N, N },
            new[]
            {
                (100.0, 100.0, ResistivitySuspectKind.Outlier, "I38 = 10.25，计算值 13.44（残差 +31 %），一个点占该行 SSE 的九成：疑为抄错（只点名，不改）"),
                (700.0, 700.0, ResistivitySuspectKind.Midpoint, "O38 = 31.76 恰为 N38 29.31 与 P38 34.21 的中点（两位小数的数据）：疑为插补（证据弱于等差段；只点名，不改）"),
                (900.0, 900.0, ResistivitySuspectKind.Midpoint, "Q38 = 35.74 恰为 P38 34.21 与 R38 37.27 的中点（两位小数的数据）：疑为插补（证据弱于等差段；只点名，不改）"),
            }),
        new ResistivityTestRow("Ni", "E41", 41, 42, 43,
            new[] { 8.5, 9, 13, 19, 26, 33, 37, 40, 43, 45, 48, 51, N, N, N, N, N },
            Array.Empty<(double, double, ResistivitySuspectKind, string)>()),
        new ResistivityTestRow("Cu", "E44", 44, -1, -1,   // 没有計算值／SSE 行；X44、Y44 字面 0 = 缺测
            new[] { N, 1.7, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N },
            Array.Empty<(double, double, ResistivitySuspectKind, string)>()),
    };

    public static ResistivityTestRow Row(string dataGrade)
        => Rows.FirstOrDefault(r => r.DataGrade == dataGrade)
           ?? throw new KeyNotFoundException($"《{WorkbookFile}》没有牌号「{dataGrade}」的测试值行");

    private static bool IsSuspect(ResistivityTestRow r, double t)
        => r.Suspect.Any(s => t >= s.FromC - 1e-9 && t <= s.ToC + 1e-9);

    /// <summary>
    /// 带覆盖类别的电阻率 [Ω·m]。值 = <see cref="PtGrade.ResistivityOhmM"/>（原函数，不另写）；无数据时 NaN。
    ///
    /// 规则（2026-09-15，Opus 5）：
    ///   · 借用：牌号的 <see cref="PtGrade.ResistivityFrom"/> ≠ 自身 ⇒ Borrowed = true；
    ///   · 整行只有一个测试值：正落在它上面 ⇒ 单点；否则无数据；
    ///   · 首尾测试值之间：相邻间距 &gt; 100 K ⇒ 测点空档；夹住它的点有疑点 ⇒ 疑点；否则区间内；
    ///   · 首尾之外：一律外推；只有二次式在**该温度** dρ/dT ≤ 0 的温度段 ⇒ 形状已不物理（金属电阻率随温度单调增）⇒ 无数据。
    ///     dρ/dT 对 T 是线性的，所以无数据段就是 T0 − α/(2β) 以上（β &lt; 0）：Ni ≈ 1337.2 °C、Pd ≈ 1900.0 °C。
    ///     （2026-09-15 第二轮，Opus 5，口径 D4④：删掉第一轮「查 [末测点, max(T,1500)] 整段」的规则 —— 那条让 Ni 从 1001 °C 起就无数据。）
    /// </summary>
    public static ResistivityValue Read(string grade, double tC)
    {
        var g = MaterialDb.Get(grade);
        string from = string.IsNullOrEmpty(g.ResistivityFrom) ? g.Name : g.ResistivityFrom;
        bool borrowed = from != g.Name;
        var row = Row(from);
        string pre = borrowed ? $"【借用】{grade} 的电阻率取自 {from} 行；" : "";
        var pts = GridTempsC.Zip(row.Values).Where(p => !double.IsNaN(p.Second)).Select(p => p.First).OrderBy(x => x).ToArray();

        ResistivityValue Make(ResistivityCoverage c, string note)
            => new(c == ResistivityCoverage.NoData ? double.NaN : g.ResistivityOhmM(tC), c, borrowed, from, pre + note);

        if (double.IsNaN(tC)) return Make(ResistivityCoverage.NoData, "温度为 NaN");
        if (pts.Length == 0) return Make(ResistivityCoverage.NoData, $"{from} 整行没有测试值");

        if (pts.Length == 1)
            return tC == pts[0]
                ? Make(ResistivityCoverage.SinglePointOnly, $"{from} 整行只有 {pts[0]:0} °C 一个测试值，正落在它上面")
                : Make(ResistivityCoverage.NoData, $"{from} 整行只有 {pts[0]:0} °C 一个测试值（α、β 字面 0 是缺测，不是数据），{tC:0.#} °C 无数据");

        double first = pts[0], last = pts[^1];
        if (tC < first || tC > last)
        {
            // ρ'(T) ∝ α + 2β(T − T0)
            double slope = g.Alpha + 2 * g.Beta * (tC - g.T0C);
            if (!(slope > 0))
                return Make(ResistivityCoverage.NoData,
                    $"{from} 测试值 {first:0}–{last:0} °C 之外，且二次式在 {tC:0.#} °C 的 dρ/dT 已不为正（形状不物理）：无数据");
            return Make(ResistivityCoverage.Extrapolated, $"{from} 测试值 {first:0}–{last:0} °C 之外：外推");
        }

        int hit = Array.IndexOf(pts, tC);
        if (hit >= 0)
            return IsSuspect(row, tC)
                ? Make(ResistivityCoverage.BetweenSuspectTestValues, $"{from} 正落在测试值 {tC:0} °C 上，但该点{Why(row, tC)}")
                : Make(ResistivityCoverage.BetweenTestValues, $"{from} 正落在测试值 {tC:0} °C 上");

        int i = Array.FindLastIndex(pts, p => p < tC);
        double lo = pts[i], hi = pts[i + 1];
        if (hi - lo > MaxRegularGapK)
            return Make(ResistivityCoverage.InsideGap, $"{from} 相邻测试值 {lo:0}–{hi:0} °C 间距 {hi - lo:0} K > {MaxRegularGapK:0} K：测点空档");
        if (IsSuspect(row, lo) || IsSuspect(row, hi))
            return Make(ResistivityCoverage.BetweenSuspectTestValues,
                $"{from} 夹在测试值 {lo:0}–{hi:0} °C 之间，但{Why(row, IsSuspect(row, lo) ? lo : hi)}");
        return Make(ResistivityCoverage.BetweenTestValues, $"{from} 夹在测试值 {lo:0}–{hi:0} °C 之间");
    }

    private static string Why(ResistivityTestRow r, double t)
        => r.Suspect.First(s => t >= s.FromC - 1e-9 && t <= s.ToC + 1e-9).Why;
}
