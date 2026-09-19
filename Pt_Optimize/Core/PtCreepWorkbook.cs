using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

// ============================================================================
//  持久强度：工作簿原始点、名称映射、残差报告、形状扫描（包络判法）与覆盖查询（2026-09-15，Opus 5）
//
//  式子不变：T(K)·log10σ = a(T)·log10 t + b(T)，a、b 为 T(K) 五次多项式（PtGrade.A/B/RuptureStressMPa/RuptureLifeHours）。
//  ⚠ 本档**一律调用现有函数**，不另写多项式求值（数值把关：Horner 与现在的 Math.Pow 在 a(T) 上差 1.6e-11，
//    不逐位；另写一份就与设计链不同源）。
//
//  两位把关人实测（2026-09-15）：
//    · 工作簿 R10–R27 计算区 9 行 P 列全是 1 ⇒ 只有「给应力求寿命」这一向有缓存；「给寿命求应力」只能靠代数互逆检验。
//    · 系数只有 7 位：按半个末位单位最坏累积，σ(10000 h) 偏差倍数 Tanaka-ZGS-Pt 1.055–1.098、ZGS-PtRh10 1.053–1.086、
//      FKS-Pt 1.013–1.021、Pure Pt 1.001 —— 工作簿自带，照实现就继承。
//    · 多项式是按实测温度点的插值（4 个温度配 3 次、6 个温度配 5 次）⇒ 区间外 NaN 是对的，不放宽。
//    · Tanaka-ZGS-Pt 在已确认区间内有驼峰：1000 h 强度 1000 °C 21.42 → 1027 °C 23.43（峰）→ 1045 °C 22.80
//      → 1061 °C 21.50 → 1100 °C 17.58（原始点 1000 °C 21.7、1100 °C 17.5）；数据点残差 ≤ 4.6 % 抓不到 ⇒ 加形状扫描与覆盖标记。
//    · Tanaka-Pure Pt 1e5 h（超出原始点时间轴 0.1–10000 h）下强度随温度升：局部斜率为正的是 1227–1308 °C〔第一轮门实测〕，
//      包络判法违规的是 1228–1349 °C〔第二轮门实测〕⇒ 寿命超出原始点时间轴要标「时间外推」。
//      纯铂在原始点时间轴下端 0.1 h 也有违规段 1101–1183 °C〔第二轮门实测〕—— 在确认区间、确认时间轴之内。
//
//  ⚠ 2026-09-15 第二轮（Opus 5，主会话口径 D2）：「形状不物理」改用**包络判法** —— 同一寿命下从该牌号区间下端
//    按 1 K 扫，σ(T) 高于 T 以下任一扫描温度的 σ 即标形状不物理。第一轮只看 ±0.5 K 局部斜率，驼峰后半段
//    （如 ZGS-Pt 1000 h 1028–1061 °C：斜率已转负、强度仍高于 1000 °C）照样标「实测确认区间内」，偏不安全。
//    高温端的上升段（如 ZGS-Pt 1 h 1466–1500 °C；1465 °C 是最低点，本身不违规）同一判法一并标出。
//
//  ⚠ 2026-09-15 第四轮（Opus 5，复核 minor；值一位不变，只动覆盖类别）：
//    · 原始点时间轴判断带相对 1e-9 容差（TimeAxisRelTol，与两向互逆 1e-9 同口径）：第三轮在端点寿命上
//      「给寿命求应力」与「由该应力反求寿命」两向类别会翻面（Pt 1213 °C × 0.1 h 反求得 0.09999999999999985 h）；
//    · 「推定区间·无原始点·判不了时间外推」排到「时间外推」之后（原 InUnconfirmedRange = 1 < TimeExtrapolated = 2，
//      调用方按「≥ 时间外推 ⇒ 判不了」取值时 FKS16/Pt 与 Umicore 会漏过）—— 主会话定；
//    · RuptureStress／RuptureLife 另给 PtGrade 重载，门用人造系数的牌号测 a(T) ≥ 0 规则（不进 MaterialDb.All）。
//    · 工作簿 T18 = 3.166 **不是拟合误差**：L18 = 1200 °C 而 Q18 = 16.3 是 1300 °C·1000 h 的点（算例温度打错）；
//      按 1300 °C 算模型给 986 h（−1.4 %）。T 列是比值不是百分数。
//
//  门：Pt_Optimize.Tests/R48CreepWorkbookTests.cs 与 MaterialDbTests.cs 直接读 xlsx 逐格核对（不手抄）。
// ============================================================================

/// <summary>APP 牌号 ↔ 工作簿系数表名称 ↔ 原始数据块标题（名字在工作簿里写法不一，必须显式映射）。2026-09-15，Opus 5。</summary>
public sealed record CreepCoefRow(string Grade, string WorkbookName, int RowA, int RowB, string RawBlockTitle);

/// <summary>一块原始持久强度点：标题格 F{TitleRow}，温度标签 F{TempRow}，时间 G..{TimeRow}，应力 G..{StressRow}。2026-09-15，Opus 5。</summary>
public sealed record CreepRawBlock(string Grade, string Title, int TitleRow, double TempC, int TempRow,
    int TimeRow, int StressRow, double[] Hours, double[] StressMPa);

/// <summary>一个原始点上的拟合残差。2026-09-15，Opus 5。</summary>
public readonly record struct CreepResidual(
    string Grade, double TempC, double Hours, double MeasuredMPa, double ModelMPa,
    double RelErr,          // (模型 − 实测)/实测，正 = 模型偏高 = 偏不安全
    double LogStressResid,  // log10(模型σ) − log10(实测σ)
    double LogLifeResid,    // log10(模型在实测应力下的寿命) − log10(实测寿命)；≈ −T/a × LogStressResid（寿命当量）
    string StressCell);

/// <summary>
/// 持久强度查询的覆盖类别，数值越大越不可信。2026-09-15，Opus 5。
/// 2026-09-15 第四轮（Opus 5，主会话定）：「推定区间·无原始点·判不了时间外推」由 1 挪到 2、排在「时间外推」之后，
/// 并改名 <see cref="UnconfirmedRangeTimeUnjudgeable"/>（原名 InUnconfirmedRange）—— 判不了不比「判得出是外推」可信。
/// </summary>
public enum CreepCoverage
{
    /// <summary>温度在实测确认区间内，寿命在原始点时间轴内（端点带 <see cref="PtCreepWorkbook.TimeAxisRelTol"/> 相对容差）。</summary>
    InConfirmedRange = 0,
    /// <summary>
    /// 寿命超出该牌号原始点时间轴（如纯铂 0.1–10000 h 之外，端点带 <see cref="PtCreepWorkbook.TimeAxisRelTol"/> 相对容差）。
    /// 只有有原始点的牌号（Tanaka 五个）能判。
    /// </summary>
    TimeExtrapolated = 1,
    /// <summary>
    /// 推定区间·无原始点·判不了时间外推：FKS16／Umicore 系列工作簿只给了系数，没有原始点（RangeConfirmed = false）。
    /// ⚠ 这些牌号**没有原始点时间轴，所以也没有时间上限**：寿命多长都判不了是否外推 —— 不是说没外推，是判不了，
    ///   所以排在「时间外推」之后：调用方按「≥ 时间外推 ⇒ 判不了／不可信」取值时它们不会漏过。
    /// 数据模型里「推定区间」与「没有原始点」恰是同一批牌号（门核）；两者任一成立都归这一档（偏保守）。
    /// </summary>
    UnconfirmedRangeTimeUnjudgeable = 2,
    /// <summary>
    /// 形状不物理：同一寿命下 σ(T) 高于该牌号区间下端到 T 之间任一 1 K 扫描温度的 σ（包络判法），
    /// 或 a(T) ≥ 0（强度随时间升高）。2026-09-15 第二轮，Opus 5（口径 D2）。
    /// </summary>
    ShapeNonMonotone = 3,
    /// <summary>无数据：温度在蠕变拟合区间外、牌号无蠕变数据、或输入为 NaN／寿命与应力非正 —— 值为 NaN。</summary>
    NoData = 4,
}

public readonly record struct CreepValue(double Value, CreepCoverage Coverage, string Note)
{
    public bool HasValue => Coverage != CreepCoverage.NoData;
}

/// <summary>《鉑金材料蠕變應力壽命估算.xlsx》目录与报告。2026-09-15，Opus 5。</summary>
public static class PtCreepWorkbook
{
    public const string WorkbookFile = "鉑金材料蠕變應力壽命估算.xlsx";
    public const string SheetName = "鉑金材料蠕變應力壽命估算";

    /// <summary>
    /// 系数表 S118:S126（a）与 S129:S137（b）的名称 → APP 牌号；原始数据块标题（F 列）→ 同一牌号。
    /// ⚠ FKS16/Pt、FKS16/PtRh-9010 映射到 Umicore-FKS-Rigilit 是既有做法（MaterialDb）；旧 VB 工具（D:\PtDesign_V2 MainForm1.vb
    ///   的 Diffusion_Pt／Diffusion_Pt_Rh_90_10 对象，:80、:117 注释 FKS_Pt／FKS_Pt_Rh_90_10；中文名「彌散鉑金／彌散鉑銠合金」
    ///   出自 CreepForm.vb:10-11 与 MainForm1.Designer.vb:600 的下拉项）的做法相反，持久强度与膨胀都接 Tanaka ZGS；
    ///   没有供应商资料 —— 记在 HANDOVER 待定，本轮不改。（2026-09-15 第二轮核实，第四轮更正出处，Opus 5）
    /// </summary>
    public static readonly IReadOnlyList<CreepCoefRow> CoefRows = new[]
    {
        new CreepCoefRow("FKS16/PtRh-9010",   "Umicore-FKS-Rigilit-PtRh10", 118, 129, ""),
        new CreepCoefRow("FKS16/Pt",          "Umicore-FKS-Rigilit-Pt",     119, 130, ""),
        new CreepCoefRow("Umicore-PtRh10",    "Umicore-PtRh 10",            120, 131, ""),
        new CreepCoefRow("Umicore-PtRh20",    "Umicore-PtRh 20",            121, 132, ""),
        new CreepCoefRow("Tanaka-ZGS-Pt",     "Tanaka-ZGS-Pt",              122, 133, "Tanaka-ZGS-Pt"),
        new CreepCoefRow("Tanaka-ZGS-PtRh10", "Tanaka-ZGS-PtRh 10",         123, 134, "Tanaka-ZGSPtRh10"),
        new CreepCoefRow("Pt-Rh/90-10",       "Tanaka-PtRh 10",             124, 135, "Tanaka-PtRh10"),
        new CreepCoefRow("Pt-Rh/80-20",       "Tanaka-PtRh 20",             125, 136, "Tanaka-PtRh20"),
        new CreepCoefRow("Pt",                "Tanaka-Pure Pt",             126, 137, "Tanaka-Pure Pt"),
    };

    private static readonly double[] H5 = { 1, 10, 100, 1000, 10000 };            // G:K
    private static readonly double[] H6 = { 0.1, 1, 10, 100, 1000, 10000 };       // G:L（只有 Tanaka-Pure Pt）

    private static CreepRawBlock B(string grade, string title, int titleRow, double tC, int tempRow, double[] h, double[] s)
        => new(grade, title, titleRow, tC, tempRow, tempRow + 1, tempRow + 2, h, s);

    /// <summary>
    /// R32–R112 原始持久强度点，逐格照 xlsx 存储值入码。**只读 G:L 列**：Q32、Q34、Q36、Q38、Q40 的零散数字
    /// （10.9、6.5、2.4、2.65、1.05，其中 Q34、Q40 在 time 行上）不是数据点。
    /// ⚠ 旧 MaterialDbTests 手抄时漏了纯铂 10000 h 那一列（L103/L106/L109/L112）与 ZGS-PtRh10 的 1500、1000 °C 两行。
    /// </summary>
    public static readonly IReadOnlyList<CreepRawBlock> RawBlocks = new[]
    {
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1500, 33, H5, new[] { 24, 13, 7.1, 4, 2.2999999999999998 }),
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1400, 36, H5, new[] { 25.5, 16, 10, 6.5, 4.3 }),
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1300, 39, H5, new[] { 34, 22.6, 15.5, 10.5, 6.9 }),
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1200, 42, H5, new[] { 40, 26, 18, 12, 8.4 }),
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1100, 45, H5, new[] { 44.5, 32.5, 24, 17.5, 13.4 }),
        B("Tanaka-ZGS-Pt", "Tanaka-ZGS-Pt", 32, 1000, 48, H5, new[] { 48, 36.799999999999997, 28.3, 21.7, 16.600000000000001 }),

        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1500, 53, H5, new[] { 43, 22, 11.7, 6.2, 3.3 }),
        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1400, 56, H5, new[] { 57, 31.7, 18.5, 10.9, 6.2 }),
        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1300, 59, H5, new[] { 71, 43, 26.5, 16.3, 10 }),
        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1200, 62, H5, new[] { 74, 48, 31.5, 21.3, 14 }),
        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1100, 65, H5, new[] { 81.2, 58.3, 41.5, 29.3, 21.3 }),
        B("Tanaka-ZGS-PtRh10", "Tanaka-ZGSPtRh10", 52, 1000, 68, H5, new[] { 85, 64.5, 49, 37.799999999999997, 28.7 }),

        B("Pt-Rh/90-10", "Tanaka-PtRh10", 72, 1400, 73, H5, new[] { 18, 9.3000000000000007, 4.8, 2.4, 1.25 }),
        B("Pt-Rh/90-10", "Tanaka-PtRh10", 72, 1300, 76, H5, new[] { 24.6, 12.6, 6.6, 3.4, 1.75 }),
        B("Pt-Rh/90-10", "Tanaka-PtRh10", 72, 1200, 79, H5, new[] { 33.1, 17.399999999999999, 9.1, 4.7, 2.4300000000000002 }),
        B("Pt-Rh/90-10", "Tanaka-PtRh10", 72, 1100, 82, H5, new[] { 55, 27.5, 13.5, 6.8, 3.4 }),

        B("Pt-Rh/80-20", "Tanaka-PtRh20", 86, 1400, 87, H5, new[] { 28.9, 13.3, 5.8, 2.65, 1.2 }),
        B("Pt-Rh/80-20", "Tanaka-PtRh20", 86, 1300, 90, H5, new[] { 38.6, 18.7, 9, 4.3499999999999996, 2.12 }),
        B("Pt-Rh/80-20", "Tanaka-PtRh20", 86, 1200, 93, H5, new[] { 53, 26.2, 12.8, 6.3, 3 }),
        B("Pt-Rh/80-20", "Tanaka-PtRh20", 86, 1100, 96, H5, new[] { 85.5, 42, 20.399999999999999, 9.9, 4.8 }),

        B("Pt", "Tanaka-Pure Pt", 100, 1400, 101, H6, new[] { 6.7, 4.2, 2.65, 1.7, 1.05, 0.66 }),
        B("Pt", "Tanaka-Pure Pt", 100, 1300, 104, H6, new[] { 14.5, 8.4, 5, 2.9, 1.7, 0.95 }),
        B("Pt", "Tanaka-Pure Pt", 100, 1200, 107, H6, new[] { 25, 13.3, 7, 3.7, 2, 1.03 }),
        B("Pt", "Tanaka-Pure Pt", 100, 1100, 110, H6, new[] { 26.3, 15.7, 9.6, 5.7, 3.4, 2.0499999999999998 }),
    };

    private static string Col(int i) => ((char)('G' + i)).ToString();

    /// <summary>该牌号全部原始点上的残差（调用现有 RuptureStressMPa / RuptureLifeHours）。无原始点的牌号返回空。</summary>
    public static IReadOnlyList<CreepResidual> Residuals(string grade)
    {
        var g = MaterialDb.Get(grade);
        var list = new List<CreepResidual>();
        foreach (var b in RawBlocks.Where(x => x.Grade == grade))
            for (int i = 0; i < b.Hours.Length; i++)
            {
                double h = b.Hours[i], s = b.StressMPa[i];
                double model = g.RuptureStressMPa(b.TempC, h);
                double life = g.RuptureLifeHours(b.TempC, s);
                list.Add(new CreepResidual(grade, b.TempC, h, s, model, (model - s) / s,
                    Math.Log10(model) - Math.Log10(s), Math.Log10(life) - Math.Log10(h), Col(i) + b.StressRow));
            }
        return list;
    }

    /// <summary>该牌号原始点的时间轴 [h]；没有原始点 ⇒ (NaN, NaN)。</summary>
    public static (double MinH, double MaxH) RawHourSpan(string grade)
    {
        var hs = RawBlocks.Where(b => b.Grade == grade).SelectMany(b => b.Hours).ToArray();
        return hs.Length == 0 ? (double.NaN, double.NaN) : (hs.Min(), hs.Max());
    }

    /// <summary>形状扫描与包络判法的温度步长 K（口径 D2：按 1 K 扫）。</summary>
    public const double EnvelopeStepC = 1;

    /// <summary>
    /// 原始点时间轴端点的相对容差：寿命在 [hMin·(1−tol), hMax·(1+tol)] 内不算时间外推。
    /// 与两向互逆的 1e-9 同口径（2026-09-15 第四轮，Opus 5：第三轮不带容差，端点寿命上两向类别会翻面）。
    /// </summary>
    public const double TimeAxisRelTol = 1e-9;

    /// <summary>
    /// 形状扫描（报告用，2026-09-15 第二轮改为包络判法，Opus 5）：在牌号拟合区间内从下端按 <see cref="EnvelopeStepC"/> 扫，给出
    ///   ① 指定寿命下 σ(T) 高于 T 以下任一扫描温度 σ 的温度段（Hours = 该寿命）；
    ///   ② a(T) ≥ 0（强度随时间升高）的温度段（Hours = NaN）。
    /// LoC、HiC 是扫描网格上违规的首、末温度（含两端）。
    /// </summary>
    public static IReadOnlyList<(double Hours, double LoC, double HiC, string What)> ShapeScan(
        string grade, IEnumerable<double> hours)
    {
        var g = MaterialDb.Get(grade);
        var outp = new List<(double, double, double, string)>();
        if (!g.HasCreep) return outp;
        double lo = g.CreepTMinC, hi = g.CreepTMaxC;
        int n = (int)Math.Round((hi - lo) / EnvelopeStepC);

        void Collect(double h, Func<int, bool> bad, string what)
        {
            int start = -1;
            for (int k = 0; k <= n; k++)
            {
                bool b = bad(k);
                if (b && start < 0) start = k;
                if (!b && start >= 0) { outp.Add((h, lo + start * EnvelopeStepC, lo + (k - 1) * EnvelopeStepC, what)); start = -1; }
            }
            if (start >= 0) outp.Add((h, lo + start * EnvelopeStepC, lo + n * EnvelopeStepC, what));
        }

        foreach (double h in hours)
        {
            var viol = new bool[n + 1];
            double runMin = double.PositiveInfinity;
            for (int k = 0; k <= n; k++)
            {
                double s = g.RuptureStressMPa(lo + k * EnvelopeStepC, h);
                viol[k] = s > runMin;
                runMin = Math.Min(runMin, s);
            }
            Collect(h, k => viol[k], $"{h:g} h 强度高于更低温度（包络判法）");
        }
        Collect(double.NaN, k => g.A(lo + k * EnvelopeStepC) >= 0, "a(T) ≥ 0：强度随时间升高");
        return outp;
    }

    /// <summary>
    /// 带覆盖类别的断裂强度 [MPa]。值 = <see cref="PtGrade.RuptureStressMPa"/>（原函数）。
    /// 覆盖取最坏：区间外／无蠕变数据／寿命 NaN 或非正 ⇒ 无数据（NaN）；包络判法违规或 a(T) ≥ 0 ⇒ 形状不物理；
    /// 寿命超出原始点时间轴 ⇒ 时间外推；区间推定 ⇒ 推定区间内。
    /// </summary>
    public static CreepValue RuptureStress(string grade, double tC, double hours)
        => RuptureStress(MaterialDb.Get(grade), tC, hours);

    /// <summary>
    /// 同上，直接给牌号对象（2026-09-15 第四轮，Opus 5）：门用测试里构造的人造系数牌号测分档规则（如 a(T) ≥ 0），
    /// 那种牌号不进 <see cref="MaterialDb.All"/>，也没有原始点。
    /// </summary>
    public static CreepValue RuptureStress(PtGrade g, double tC, double hours)
    {
        ArgumentNullException.ThrowIfNull(g);
        var cov = Classify(g, tC, hours, out string note);
        if (cov == CreepCoverage.NoData) return new(double.NaN, cov, note);
        return new(g.RuptureStressMPa(tC, hours), cov, note);
    }

    /// <summary>带覆盖类别的断裂寿命 [h]（覆盖按算出的寿命判时间外推与包络）。</summary>
    public static CreepValue RuptureLife(string grade, double tC, double sigmaMPa)
        => RuptureLife(MaterialDb.Get(grade), tC, sigmaMPa);

    /// <summary>同上，直接给牌号对象（2026-09-15 第四轮，Opus 5）。</summary>
    public static CreepValue RuptureLife(PtGrade g, double tC, double sigmaMPa)
    {
        ArgumentNullException.ThrowIfNull(g);
        double life = g.RuptureLifeHours(tC, sigmaMPa);
        if (double.IsNaN(life) || !(life > 0))
            return new(double.NaN, CreepCoverage.NoData, $"{g.Name}：{tC:0.#} °C 在蠕变拟合区间外、无蠕变数据，或应力 {sigmaMPa:g} MPa 非正／NaN，无数据");
        var cov = Classify(g, tC, life, out string note);
        if (cov == CreepCoverage.NoData) return new(double.NaN, cov, note);
        return new(life, cov, note);
    }

    /// <summary>
    /// 包络判法：同一寿命下，σ(tC) 是否高于区间下端到 tC 之间（不含 tC）任一 1 K 扫描温度的 σ。
    /// 违规时 tMin、sMin 是那段扫描温度里 σ 最低的点。调用前温度须已在拟合区间内。
    /// </summary>
    public static bool AboveLowerEnvelope(PtGrade g, double tC, double hours, out double tMin, out double sMin)
    {
        tMin = double.NaN; sMin = double.PositiveInfinity;
        for (int k = 0; g.CreepTMinC + k * EnvelopeStepC < tC - 1e-9; k++)
        {
            double t = g.CreepTMinC + k * EnvelopeStepC;
            double s = g.RuptureStressMPa(t, hours);
            if (s < sMin) { sMin = s; tMin = t; }
        }
        return g.RuptureStressMPa(tC, hours) > sMin;
    }

    private static CreepCoverage Classify(PtGrade g, double tC, double hours, out string note)
    {
        if (!g.HasCreep || double.IsNaN(tC) || !g.InCreepRange(tC) || !(hours > 0))
        {
            note = !g.HasCreep ? $"{g.Name}：无蠕变数据"
                 : !(hours > 0) ? $"{g.Name}：寿命 {hours:g} h 不是正数（或 NaN），无数据"
                 : $"{g.Name}：{tC:0.#} °C 在蠕变拟合区间 {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C 外，无数据";
            return CreepCoverage.NoData;
        }
        var notes = new List<string>();
        var cov = CreepCoverage.InConfirmedRange;
        void Up(CreepCoverage c, string n) { if (c > cov) cov = c; notes.Add(n); }

        var (hMin, hMax) = RawHourSpan(g.Name);
        bool noTimeAxis = double.IsNaN(hMin);
        if (!g.RangeConfirmed || noTimeAxis)
            Up(CreepCoverage.UnconfirmedRangeTimeUnjudgeable,
               (g.RangeConfirmed ? "区间已确认" : $"区间 {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C 为推定（工作簿只给了系数，没有原始点）")
               + (noTimeAxis ? "；没有原始点时间轴，寿命是否外推判不了（没有时间上限）" : ""));
        if (!noTimeAxis && (hours < hMin * (1 - TimeAxisRelTol) || hours > hMax * (1 + TimeAxisRelTol)))
            Up(CreepCoverage.TimeExtrapolated, $"寿命 {hours:g} h 超出原始点时间轴 {hMin:g}–{hMax:g} h");
        double a = g.A(tC);
        if (a >= 0) Up(CreepCoverage.ShapeNonMonotone, $"a({tC:0.#} °C) = {a:0.#} ≥ 0：强度随时间升高");
        if (AboveLowerEnvelope(g, tC, hours, out double tMin, out double sMin))
            Up(CreepCoverage.ShapeNonMonotone,
               $"{hours:g} h 下 {tC:0.#} °C 的强度 {g.RuptureStressMPa(tC, hours):0.###} MPa 高于更低温度 {tMin:0} °C 的 {sMin:0.###} MPa（包络判法：强度不许随温度回升）");
        note = $"{g.Name}：" + (notes.Count == 0 ? "实测确认区间内" : string.Join("；", notes));
        return cov;
    }
}
