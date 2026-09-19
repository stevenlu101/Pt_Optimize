using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

// ============================================================================
//  铂系材料热膨胀：按牌号的温度函数（2026-09-15，Opus 5）
//
//  用户 2026-09-15 原话：「把它变成各种铂金的温度函数」「Excel里面都有算法」
//  「还是以纯铂为设计标准，除非工程师在APP特别设定」。
//
//  ⇒ 本档**只照《鉑金热膨胀计算.xlsx》自己的算法**，不另起插值／拟合口径：
//    · 平均线膨胀系数 α_mean(T)（×1e-6 /K，0 °C → T 的平均）= 该合金在工作簿里的
//      **趋势线多项式**（纯铂 = X14 单元格公式，其余 = 图表 chart1 的多项式趋势线）；
//    · 热态长度 LT = L0·(1 + α·0.000001·T)（工作簿 Y14:Y16 公式），伸长 ΔL = LT − L0（Z14:Z16）。
//
//  ⚠ 为什么不对 LT/L0 表插值取差（两位把关人已算）：表只有 4 位小数，
//    两个表值直接相减求 10 K 温差的应变差误差可近 100 %；逐格线性插值约 10 %；
//    PCHIP 遇舍入平台会把导数压成 0（偏不安全）。光滑趋势线把舍入平均掉 —— 与工作簿作者同一做法。
//    表只作实测点与门的核对对象。
//
//  ⚠ 覆盖类别**随每个返回值带出去**（不许调用方自己猜）：
//    区间内 / 区间内·端部 / 端部·定义点内插 / 外推 / 拟合形状不可信 / 无数据（NaN）。
//    借用他牌号数据的，Link = Borrowed，也随返回值带出去。
//
//  ⚠ 2026-09-15 第二轮（Opus 5，主会话定的口径 D1/D3/D4）：
//    · 删掉「外推·有旁证」：纯铂 1000 °C 以上**所有量**一律「外推」。依据：工作簿作者自己在 X15/X16
//      用 X14 算 1150 °C（用户「Excel里面都有算法」）⇒ 照用并标外推；与 ZGSPt 的比较只作门打印的诊断数，
//      不进覆盖类别（那把尺在 1000 °C 附近不成立：1010/1000 °C 的 X14 应变差就落在 ZGSPt 舍入带外）。
//    · 0 ≤ T < 100 °C（所有曲线统一）：ε(0) = 0 是定义值 ⇒ ε 在 0 与 100 °C 之间线性，平均 α 取该曲线
//      100 °C 的拟合值，标「端部·定义点内插」；不再返回 NaN（升温期温差判据从室温起算）。
//    · 形状不可信 = 瞬时 α 随温度下降的温度段（函数本身的性质，照标，不返回 NaN）。
//
//  ⚠ 2026-09-15 第四轮（Opus 5，复核 minor）：值一位不变，只补两处返回值的说法：
//    · 值算出 NaN（HotLength／Elongation 的 L0 为 NaN，或 L0 = ±∞ 时伸长 ∞ − ∞）而温度在区间内 ⇒ 标无数据，
//      守 ExpansionValue「Value 为 NaN ⇔ Coverage == NoData」（第三轮 HotLength("Pt", NaN, 500) 标区间内）；
//      2026-09-16 第五轮（Opus 5，核验员小备注）收紧为「L0 非有限 ⇒ 无数据」、不变式改为「Value 非有限 ⇔ Coverage == NoData」：
//      第四轮 HotLength("Pt", +∞, 500) 返回 +∞ 且 HasValue = true，字面守住了 NaN 规则却不是可用值。有限输入的值一位不变；
//    · 瞬时 α 在 100 °C 的台阶：0–200 °C 的 InstantAlphaE6 注记点名该曲线台阶两侧的值与百分数（由系数现算，不手写）。
//    覆盖分档的门改由测试**按定义独立算**（不调本档 Classify），见 R48ExpansionWorkbookTests 的 ExpansionCoverageSpec。
//
//  2026-09-16 续做（Opus 5）：上一次实施中断后续做，每一项重编、重跑、重注入过；本档换行由 LF 统一为 CRLF（复核 minor 8），
//    数值与门不受影响 —— 膨胀函数的逐位不变由核验员 2026-09-16 另做的膨胀转储证明（第三轮源 vs 第四轮源 149707 行 0 个位差）；
//    此前这里写的「网格转储」只含 MaterialDb 电阻率／持久强度与 Materials，不含本档任何函数（第五轮更正，膨胀转储已收进
//    R48PropertyDumpTests 慢测试，2026-09-16，Opus 5）。分档门的注入 J1／J2／J4／R1b／R1c 与前两轮 32 处在第四轮门下全红，见 HANDOVER 0.-6。
//
//  ⚠ 2026-09-18（Opus 5，用户原话「改」）：工作簿 **G18（PtRh20 @1400 °C）1.0157 → 1.0152**，
//    依据 B. Barter & A. S. Darling, "Thermal Expansion of Rhodium-Platinum Alloys",
//    Platinum Metals Rev., 1960, 4 (4), 138-140，表在 p.139「20 % Rh」栏（D:\WinForm\Pt_Optimize\pmr0004-0138.pdf）。
//    工作簿的 LT/L0 表就是这张表舍到 4 位小数的副本（96 格逐格对拍，只有 G18 一格超出舍入能解释的范围：
//    Δ = +5.0e-4，Δε = +3.29 %）。改前那一格把 PtRh20 6 阶趋势线的**瞬时 α** 在 1322.1–1500 °C 压成下降段
//    （1500 °C 差 −43 %），正落在工作温度 1100–1450 °C 的上半段；论文的 20 % Rh 平均 α 全程单调增。
//    ⇒ 改正后该下降段整个消失，ShapeUntrusted 那面旗不再挂在 Pt-Rh/80-20 上。
//    G18 一改，Excel 不会自己重算图表里存的趋势线方程文本 ⇒ **PtRh20 的系数改由数据重算**
//    （ExpansionCoeffOrigin.RecomputedLabelStale，与其余复算曲线同一最小二乘、同取 7 位），
//    标签文本留作旧值物证、门另核它确实是旧的。其余八条曲线仍以标签／单元格公式为准。
//
//  ⚠ 本轮不接任何判据、不改求解链、不改 Materials.PtAlphaExp（WeldDistortion 仍用它）。
//
//  出处与门：Pt_Optimize.Tests/R48ExpansionWorkbookTests.cs 每次**直接读 xlsx** 核对本档每个数
//  （不手抄），并用精确有理数最小二乘复算趋势线系数。
// ============================================================================

/// <summary>返回值落在拟合的什么位置。数值越大越不可信（取两端最坏时按此序比较）。2026-09-15，Opus 5。</summary>
public enum ExpansionCoverage
{
    /// <summary>拟合数据温度区间内。</summary>
    InRange = 0,
    /// <summary>区间内·端部：离数据端点不足一个数据间隔。只用于瞬时 α（导数类量）——端点附近多项式导数会振荡。</summary>
    InRangeNearEnd = 1,
    /// <summary>
    /// 端部·定义点内插：0 ≤ T &lt; 100 °C。ε(0) = 0 是定义值，ε 在 0 °C 与该曲线 100 °C 拟合值之间线性
    /// （平均 α = 瞬时 α = 该曲线 100 °C 的平均 α 拟合值）。100 °C 那个值本身若更差（ZGS 两条的 100 °C 是外推），取更差的。
    /// 2026-09-15，Opus 5（主会话口径 D3）。
    /// </summary>
    EndDefinedPointInterp = 2,
    /// <summary>区间外·外推·无直接数据（只对 3 阶及以下的趋势线给值）。纯铂 1000 °C 以上的所有量都在这一档。</summary>
    Extrapolated = 3,
    /// <summary>
    /// 拟合形状不可信：该温度落在曲线**瞬时 α 随温度下降**的温度段（fcc 固溶体无相变，物理上应单调增）。
    /// 是函数本身的性质，照标、照给值。⚠ 这可能源于工作簿 LT/L0 只有 4 位小数的抖动被高阶拟合放大，不是物理。
    /// </summary>
    ShapeUntrusted = 4,
    /// <summary>无数据：值为 NaN。</summary>
    NoData = 5,
}

/// <summary>牌号到膨胀曲线的关系。2026-09-15，Opus 5。</summary>
public enum ExpansionLink
{
    /// <summary>工作簿里就是这个材料（同名义成分，工作簿未注明供应商）。</summary>
    Direct,
    /// <summary>借用他牌号曲线（必须随值带出，报告里点名）。</summary>
    Borrowed,
    /// <summary>工作簿没有此材料。</summary>
    None,
}

/// <summary>
/// FKS16 两个牌号借不借、借谁 —— **工程判断，默认不借（无数据）**。2026-09-15，Opus 5。
///
/// 两位把关人意见冲突：物理把关主张借同基体（FKS16/Pt→Pt，FKS16/PtRh-9010→PtRh10，
/// 理由：弥散相体积分数 &lt;1 %，工作簿 ZGS 与基体差值都在 ZGS 表 3 位有效数字附近）；
/// 数值把关主张默认无数据、做成显式开关（借同基体还是借同为弥散强化的 ZGS 是工程判断）。
/// 按「冲突取保守」⇒ 默认 None；要借必须显式传参，返回值 Link = Borrowed。
/// 以往做法（2026-09-15 第二轮核实，第四轮更正出处，Opus 5）：旧 VB 工具 D:\PtDesign_V2 MainForm1.vb 的 Diffusion_Pt／
/// Diffusion_Pt_Rh_90_10 两个对象（:80、:117 注释 FKS_Pt／FKS_Pt_Rh_90_10；中文名「彌散鉑金」「彌散鉑銠合金」是
/// CreepForm.vb:10-11 与 MainForm1.Designer.vb:600 的下拉项，MainForm1.vb 里只有变量名与 :243-244 注释掉的下拉项）
/// 膨胀与持久强度都取 ZGS 的系数、电阻率取 FKS16 行 ——
/// 与 MaterialDb 现在「FKS16 持久强度 → Umicore-FKS-Rigilit」的做法相反；只是以往做法，不是供应商资料。
/// </summary>
public enum ExpansionBorrowOption
{
    None,
    /// <summary>借同基体合金：FKS16/Pt→Pt，FKS16/PtRh-9010→PtRh10。</summary>
    SameBaseAlloy,
    /// <summary>借同为弥散强化的 ZGS：FKS16/Pt→ZGSPt，FKS16/PtRh-9010→ZGSPtRh10。</summary>
    DispersionStrengthened,
}

/// <summary>系数从哪来。2026-09-15，Opus 5。</summary>
public enum ExpansionCoeffOrigin
{
    /// <summary>工作簿单元格公式里的字面量（纯铂 X14:X16）。</summary>
    CellFormula,
    /// <summary>图表趋势线标签里存着的方程文本。</summary>
    ChartLabelText,
    /// <summary>图表趋势线未存方程文本：按工作簿设定的阶数与数据区间，用与 Excel 同一最小二乘复算、取 7 位（与标签格式 0.000000E+00 同口径）。</summary>
    RecomputedSameAlgorithm,
    /// <summary>
    /// 图表趋势线**存了**方程文本，但那是 Excel 按**改前**数据算的缓存：工作簿的数据格已按公开文献改正、Excel 没有重算过
    /// ⇒ 系数以数据重算为准（算法、阶数、数据区间、7 位取法都与 <see cref="RecomputedSameAlgorithm"/> 相同），
    /// 标签文本只留作旧值物证。门对这一档改成「复算 == 代码」与「标签 != 复算」两条都断言。
    /// 为什么不叫「公开文献表」：系数仍是对**工作簿自己那一列**做最小二乘，文献是**数据**的出处，写在
    /// <see cref="ExpansionCurve.DataSource"/> 里（见 <see cref="PtThermalExpansion.PublishedTableSource"/>）。2026-09-18，Opus 5。
    /// </summary>
    RecomputedLabelStale,
}

/// <summary>
/// 一个带覆盖类别与出处的返回值。**Value 非有限（NaN／±∞）⇔ Coverage == NoData**，无数据时 Value 一律 NaN
/// （2026-09-16 第五轮从「Value 为 NaN ⇔ NoData」收紧，Opus 5；门覆盖 L0 = NaN／±∞）。2026-09-15，Opus 5。
/// </summary>
public readonly record struct ExpansionValue(
    double Value, ExpansionCoverage Coverage, ExpansionLink Link, string Curve, string Note)
{
    public bool HasValue => Coverage != ExpansionCoverage.NoData;
    public bool IsBorrowed => Link == ExpansionLink.Borrowed;
}

/// <summary>APP 牌号 → 膨胀曲线的显式映射（每个 MaterialDb 牌号都必须有一条，不许默认）。2026-09-15，Opus 5。</summary>
public sealed record GradeExpansionMap(string Grade, string? CurveId, ExpansionLink Link, string Why);

/// <summary>
/// 一条工作簿趋势线：平均线膨胀系数 α_mean(T) = Σ c_k·T^k（×1e-6 /K，T 为 °C，参考 0 °C 长度）。
/// 2026-09-15，Opus 5。
/// </summary>
public sealed class ExpansionCurve
{
    /// <summary>曲线代号（= 工作簿列头去掉「(α)」）："Pt"、"PtRh10"、…、"ZGSPt"、"ZGSPtRh10"。</summary>
    public string Id { get; }
    /// <summary>多项式阶数（= 图表 trendline 的 order）。</summary>
    public int Order { get; }
    /// <summary>系数 c0..cN（升幂）。</summary>
    public IReadOnlyList<double> Coeffs { get; }
    public ExpansionCoeffOrigin Origin { get; }
    /// <summary>系数出处（工作簿名 + 单元格／图表系列）。</summary>
    public string CoeffSource { get; }
    /// <summary>趋势线实际用到的数据点温度 °C（图表系列 x 引用，按 y 个数配对）。</summary>
    public IReadOnlyList<double> DataTempsC { get; }
    /// <summary>对应的数据点 α（×1e-6）——按工作簿 α 列公式由 LT/L0 表算出，或 ZGS 列直接值。</summary>
    public IReadOnlyList<double> DataAlphaE6 { get; }
    /// <summary>数据点出处。</summary>
    public string DataSource { get; }
    /// <summary>
    /// 拟合形状不可信的温度段 = **瞬时 α 随温度下降**的段，在多项式真正作数的 100–1500 °C 上
    /// **由瞬时 α 导数符号算出**，不手写；边界二分到 1e-6 K。
    /// 0–100 °C 的函数是定义点内插（瞬时 α 为常数），不在此列。
    /// ⚠ 可能源于 LT/L0 表 4 位小数的抖动被高阶拟合放大，不是物理。2026-09-15，Opus 5（口径 D4①）。
    /// </summary>
    public IReadOnlyList<(double LoC, double HiC, string Why)> UntrustedSegments { get; }

    public double DataMinC => DataTempsC.Min();
    public double DataMaxC => DataTempsC.Max();

    internal ExpansionCurve(string id, ExpansionCoeffOrigin origin, string coeffSource, double[] c,
        double[] dataT, double[] dataA, string dataSource)
    {
        if (dataT.Length != dataA.Length) throw new ArgumentException($"{id}：数据点温度与 α 个数不等");
        if (dataT.Length < c.Length) throw new ArgumentException($"{id}：数据点少于系数个数");
        Id = id; Origin = origin; CoeffSource = coeffSource;
        Coeffs = Array.AsReadOnly((double[])c.Clone()); Order = c.Length - 1;
        DataTempsC = Array.AsReadOnly((double[])dataT.Clone());
        DataAlphaE6 = Array.AsReadOnly((double[])dataA.Clone());
        DataSource = dataSource;
        UntrustedSegments = ScanUntrusted().AsReadOnly();
    }

    /// <summary>T^k，逐次连乘（与工作簿 X14 的 U^3 在整数温度上逐位一致；门在 X14:X16 核）。</summary>
    private static double Pow(double t, int k) { double p = 1; for (int i = 0; i < k; i++) p *= t; return p; }

    /// <summary>
    /// 平均线膨胀系数 ×1e-6 /K（0 °C → T），**不带覆盖判断的裸多项式**。
    /// 从最高次项往低次项加（与 X14「c3·U^3 − c2·U^2 + c1·U + c0」同序）。
    /// </summary>
    public double RawMeanAlphaE6(double tC)
    {
        double acc = 0;
        for (int k = Order; k >= 0; k--) acc += Coeffs[k] * Pow(tC, k);
        return acc;
    }

    /// <summary>瞬时线膨胀系数 ×1e-6 /K = d(α_mean·T)/dT = Σ (k+1)·c_k·T^k。**派生量**：工作簿没有直接给它。</summary>
    public double RawInstantAlphaE6(double tC)
    {
        double acc = 0;
        for (int k = Order; k >= 0; k--) acc += (k + 1) * Coeffs[k] * Pow(tC, k);
        return acc;
    }

    /// <summary>dα_mean/dT。</summary>
    internal double RawMeanAlphaSlope(double tC)
    {
        double acc = 0;
        for (int k = Order; k >= 1; k--) acc += k * Coeffs[k] * Pow(tC, k - 1);
        return acc;
    }

    /// <summary>d(瞬时 α)/dT。</summary>
    internal double RawInstantAlphaSlope(double tC)
    {
        double acc = 0;
        for (int k = Order; k >= 1; k--) acc += k * (k + 1) * Coeffs[k] * Pow(tC, k - 1);
        return acc;
    }

    /// <summary>热应变 ε = α_mean·1e-6·T（相对 0 °C 长度）。裸多项式。</summary>
    public double RawStrain(double tC) => RawMeanAlphaE6(tC) * 0.000001 * tC;

    /// <summary>
    /// 函数值用的平均 α ×1e-6 /K（不带覆盖判断）：0 ≤ T &lt; 100 °C 取 100 °C 的拟合值（ε 在定义点 ε(0)=0 与 100 °C
    /// 拟合值之间线性，口径 D3）；其余温度 = 裸多项式。温度合不合法由 <see cref="Classify"/> 管。2026-09-15，Opus 5。
    /// </summary>
    public double MeanAlphaE6At(double tC)
        => tC >= PtThermalExpansion.TableMinC && tC < PtThermalExpansion.DefinedPointInterpBelowC
            ? RawMeanAlphaE6(PtThermalExpansion.DefinedPointInterpBelowC) : RawMeanAlphaE6(tC);

    /// <summary>函数值用的热应变 ε = <see cref="MeanAlphaE6At"/>·1e-6·T（相对 0 °C 长度）。</summary>
    public double StrainAt(double tC) => MeanAlphaE6At(tC) * 0.000001 * tC;

    /// <summary>函数值用的瞬时 α ×1e-6 /K：0 ≤ T &lt; 100 °C 为 ε 线性段的斜率（= 100 °C 平均 α 拟合值）；其余 = 裸多项式导数。</summary>
    public double InstantAlphaE6At(double tC)
        => tC >= PtThermalExpansion.TableMinC && tC < PtThermalExpansion.DefinedPointInterpBelowC
            ? RawMeanAlphaE6(PtThermalExpansion.DefinedPointInterpBelowC) : RawInstantAlphaE6(tC);

    /// <summary>
    /// 瞬时 α 在 100 °C 的台阶（2026-09-15 第四轮，Opus 5）：左侧（0 ≤ T &lt; 100 °C）= 100 °C 平均 α 拟合值，
    /// 右侧（T = 100 °C 起）= 多项式导数；百分数 = 右/左 − 1。由系数现算，门用精确有理数复核并对 HANDOVER 表的决定记录。
    /// ε、平均 α、LT/L0 在 100 °C 连续，只有瞬时 α 有这个台阶。
    /// </summary>
    public (double LeftE6, double RightE6, double Percent) InstantAlphaStepAt100
    {
        get
        {
            double l = RawMeanAlphaE6(PtThermalExpansion.DefinedPointInterpBelowC);
            double r = RawInstantAlphaE6(PtThermalExpansion.DefinedPointInterpBelowC);
            return (l, r, (r / l - 1) * 100);
        }
    }

    /// <summary>台阶的注记（0–200 °C 的 <see cref="PtThermalExpansion.InstantAlphaE6"/> 附上）。</summary>
    public string InstantAlphaStepNote
    {
        get
        {
            var (l, r, p) = InstantAlphaStepAt100;
            double a = PtThermalExpansion.DefinedPointInterpBelowC;
            return $"⚠ {Id} 的瞬时 α 在 {a:0} °C 有台阶：{a:0} °C 以下 = {a:0} °C 平均 α {l:0.0000}，"
                 + $"{a:0} °C 起 = 多项式导数 {r:0.0000}（台阶 {p:+0.0;−0.0} %）";
        }
    }

    private List<(double, double, string)> ScanUntrusted()
    {
        var segs = new List<(double, double, string)>();
        Func<double, double> f = RawInstantAlphaSlope;
        const double lo = PtThermalExpansion.DefinedPointInterpBelowC, hi = PtThermalExpansion.TableMaxC, step = 0.5;
        const string why = "瞬时 α 随温度下降";
        bool bad = f(lo) < 0; double start = lo;
        for (double t = lo + step; t <= hi + 1e-9; t += step)
        {
            bool b = f(t) < 0;
            if (b == bad) continue;
            double edge = Bisect(f, t - step, t);
            if (bad) segs.Add((start, edge, why)); else start = edge;
            bad = b;
        }
        if (bad) segs.Add((start, hi, why));
        return segs;
    }

    private static double Bisect(Func<double, double> f, double a, double b)
    {
        bool fa = f(a) < 0;
        for (int i = 0; i < 40; i++)
        {
            double m = 0.5 * (a + b);
            if ((f(m) < 0) == fa) a = m; else b = m;
        }
        return 0.5 * (a + b);
    }

    /// <summary>[loC, hiC] 是否碰到形状不可信段（碰到时 why 点名是哪段、为什么）。</summary>
    public bool InUntrusted(double loC, double hiC, out string why)
    {
        foreach (var (l, h, w) in UntrustedSegments)
            if (hiC >= l && loC <= h) { why = $"{w}（{l:0.#}–{h:0.#} °C）"; return true; }
        why = "";
        return false;
    }

    /// <summary>
    /// 覆盖类别。derivative = true 时（瞬时 α）区间内离端点不足一个数据间隔标「端部」。
    /// 规则（2026-09-15，Opus 5；第二轮按主会话口径 D1/D3/D4 改）：
    ///   · 工作簿表格温度栏 0–1500 °C 之外 ⇒ 无数据；
    ///   · 0 ≤ T &lt; 100 °C ⇒ 端部·定义点内插（100 °C 那个值本身的类别更差时取更差的）；
    ///   · 数据区间外且阶数 ≥ 4 ⇒ 无数据（6 阶在 PtAu5 1300 °C 外即发散，1500 °C 瞬时 α 68.7）；
    ///   · 该温度落在瞬时 α 下降段 ⇒ 形状不可信；
    ///   · 区间内 ⇒ 区间内（导数类量在端部间隔内标端部）；
    ///   · 区间外 3 阶及以下 ⇒ 外推（纯铂 1000 °C 以上一律在此，没有「有旁证」这一档）。
    /// </summary>
    public ExpansionCoverage Classify(double tC, bool derivative, out string note)
    {
        if (double.IsNaN(tC) || tC < PtThermalExpansion.TableMinC || tC > PtThermalExpansion.TableMaxC)
        { note = $"{Id}：{tC:0.#} °C 在工作簿表格温度栏 0–1500 °C 之外，无数据"; return ExpansionCoverage.NoData; }
        if (tC < PtThermalExpansion.DefinedPointInterpBelowC)
        {
            double a = PtThermalExpansion.DefinedPointInterpBelowC;
            var anchor = ClassifyPolynomial(a, false, out string an);
            string head = $"{Id}：{tC:0.#} °C 在 0–{a:0} °C，ε 在定义点 ε(0) = 0 与 {a:0} °C 拟合值之间线性"
                        + $"（平均 α 取 {a:0} °C 的拟合值）·端部·定义点内插";
            if (anchor <= ExpansionCoverage.EndDefinedPointInterp)
            { note = head; return ExpansionCoverage.EndDefinedPointInterp; }
            note = head + $"；而 {a:0} °C 那个值本身：{an}";
            return anchor;
        }
        return ClassifyPolynomial(tC, derivative, out note);
    }

    /// <summary>多项式段（100–1500 °C）的覆盖类别。</summary>
    private ExpansionCoverage ClassifyPolynomial(double tC, bool derivative, out string note)
    {
        double lo = DataMinC, hi = DataMaxC;
        bool inRange = tC >= lo && tC <= hi;
        if (!inRange && Order >= 4)
        { note = $"{Id}：{Order} 阶趋势线在数据区间 {lo:0}–{hi:0} °C 外不外推（会发散），无数据"; return ExpansionCoverage.NoData; }
        if (InUntrusted(tC, tC, out string why))
        { note = $"{Id}：拟合形状不可信 —— {why}" + (inRange ? "" : $"；且在数据区间 {lo:0}–{hi:0} °C 外"); return ExpansionCoverage.ShapeUntrusted; }
        if (inRange)
        {
            if (derivative)
            {
                var ts = DataTempsC.OrderBy(x => x).ToArray();
                if (tC <= ts[1] || tC >= ts[^2])
                { note = $"{Id}：数据区间 {lo:0}–{hi:0} °C 内·端部间隔（导数类量在端部会振荡）"; return ExpansionCoverage.InRangeNearEnd; }
            }
            note = $"{Id}：数据区间 {lo:0}–{hi:0} °C 内";
            return ExpansionCoverage.InRange;
        }
        note = $"{Id}：外推（数据区间 {lo:0}–{hi:0} °C，{Order} 阶）·无直接数据";
        return ExpansionCoverage.Extrapolated;
    }
}

/// <summary>
/// 《鉑金热膨胀计算.xlsx》的数据目录与按牌号的温度函数。2026-09-15，Opus 5。
///
/// 默认设计牌号纯铂（"Pt"）：X14 公式 3 阶，数据 100–1000 °C；工作温度 1100–1450 °C 永远是**外推**，
/// 1000 °C 以上的平均 α、LT/L0、应变、热态长度、伸长、应变差**一律标「外推」**（工作簿作者自己在 X15/X16
/// 用 X14 算 1150 °C）。与 ZGSPt 表的比较只是门打印的诊断数，不进覆盖类别。借 ZGSPt 还是 PtRh10 是工程选择，
/// 本档不替它选。
///
/// ⚠ 工作簿里有一条外部链接（xl/externalLinks/externalLink1.xml → <see cref="ExternalLinkFile"/>），
///   表内没有任何公式引用它（门核）；本档的数一个都不来自那份 xlsm。
/// </summary>
public static class PtThermalExpansion
{
    public const string WorkbookFile = "鉑金热膨胀计算.xlsx";
    public const string SheetName = "鉑金热膨胀计算";

    /// <summary>
    /// 工作簿外部链接指向的文件名（辊上热态实验 GSF44 的 xlsm，路径是作者本机 D 盘）。登记出处用；门核链接真是它、且表内无公式引用。
    /// 2026-09-15，Opus 5。
    /// </summary>
    public const string ExternalLinkFile = "辊上热态实验-GSF44(含一维拉薄)-V7 (20240807).xlsm";

    /// <summary>工作簿表格温度栏上下界 °C（B4 = 0，B19 = 1500）。</summary>
    public const double TableMinC = 0, TableMaxC = 1500;

    /// <summary>
    /// LT/L0 表（C:N 六列）的**数据出处**（2026-09-18，Opus 5；此前本档写「来源不明」，对这六列可以撤销了）：
    /// 逐格对拍证明工作簿这六列就是这张 1960 年的表舍到 4 位小数的副本（96 格里 95 格 |Δ| ≤ 5e-5 = 半个末位，
    /// 只有 G18 一格是转录错误，已按论文改正）。⚠ **纯铂那一栏不是该文自己测的**，也**没有 1000 °C 以上的值**。
    /// PtAu5 与 ZGS 四列与这篇文献无关，来源仍不明。
    /// </summary>
    public const string PublishedTableSource =
        "数据来源 Barter & Darling, Platinum Metals Rev., 1960, 4 (4), 138-140，表在 p.139（Lt/Lo）；"
        + "10/20/30/95 % Rh 四栏是该文实测（⅛ in 拉丝棒，标距 3 in，空气中，望远显微镜 ±0.0001 in = ±3.3e-5 in/in，"
        + "取 1500 °C 保温约 1 h 后的冷却曲线 = 已退火稳定态，不含硬拉态件首次退火的永久缩短约 1.0e-3）；"
        + "Rh 栏系 Ebert, Phys. Zeit., 1938, 39, 6 转载；"
        + "纯铂栏系 Holborn/Scheel/Henning, Wärmetabellen, 1919, p.54 转载、止于 1000 °C（该文献没有纯铂 1000 °C 以上实测值）；"
        + "表内 300 °C 以上只印 4 位小数（量化 ±5e-5），工作簿逐格照抄这张表";

    /// <summary>纯铂栏的上游出处（与 <see cref="PublishedTableSource"/> 配套；1000 °C 以上一律外推的依据之一）。2026-09-18，Opus 5。</summary>
    public const string PurePtColumnSource =
        "纯铂栏 = Holborn/Scheel/Henning, Wärmetabellen, Braunschweig, 1919, p.54，经 Platinum Metals Rev., 1960, 4 (4),"
        + " p.139「Platinum (Ref. 2)」栏转载；该栏止于 1000 °C ⇒ 1000 °C 以上一律外推，不因这份出处改档";

    /// <summary>
    /// 低于这个温度（且 ≥ 0 °C）ε 取定义点 ε(0) = 0 与该曲线在此温度拟合值之间的线性内插 —— 所有曲线统一
    /// （表格曲线的数据从 B5 = 100 °C 起）。2026-09-15，Opus 5（口径 D3）。
    /// </summary>
    public const double DefinedPointInterpBelowC = 100;

    private const double N = double.NaN;   // 工作簿写 0 = 无数据（α 列公式此时也给 0），**绝不当 0**

    /// <summary>B4:B19 温度 °C。</summary>
    public static readonly double[] TableTempsC =
        { 0, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 };

    /// <summary>
    /// LT/L0 表（参考 0 °C），逐格照 xlsx 存储值入码（不四舍五入）。NaN = 工作簿写 0（无数据）。
    /// (合金, LT/L0 列, α 列, 行 4..19 的值)
    /// </summary>
    public static readonly IReadOnlyList<(string Alloy, string LtCol, string AlphaCol, double[] LtOverL0)> Table = new[]
    {
        ("Pt", "C", "D", new[] { 1, 1.0008999999999999, 1.0018, 1.0027999999999999, 1.0038, 1.0047999999999999, 1.0058,
            1.0068999999999999, 1.0079, 1.0091000000000001, 1.0102, N, N, N, N, N }),   // C15:C19 写 0
        ("PtRh10", "E", "F", new[] { 1, 1.0009999999999999, 1.002, 1.0029999999999999, 1.0041, 1.0051000000000001, 1.0061,
            1.0072000000000001, 1.0083, 1.0094000000000001, 1.0105999999999999, 1.0117, 1.0130999999999999, 1.0144, 1.0158,
            1.0176000000000001 }),
        // ⚠ G18（1400 °C）2026-09-18 由 1.0157 改为 1.0152（Opus 5，用户原话「改」）：工作簿抄错，
        //   论文 Platinum Metals Rev., 1960, 4 (4), 139「20 % Rh」栏印的是 1.0152。工作簿那一格已改、并加了批注；
        //   门每次读 xlsx 逐位核这一格（不手抄）。见 PublishedTableSource 与档头。
        ("PtRh20", "G", "H", new[] { 1, 1.0005999999999999, 1.0014000000000001, 1.0023, 1.0032000000000001, 1.0043,
            1.0053000000000001, 1.0063, 1.0075000000000001, 1.0086999999999999, 1.0099, 1.0112000000000001, 1.0125, 1.0138,
            1.0152, 1.0166999999999999 }),
        ("PtRh30", "I", "J", new[] { 1, 1.0008999999999999, 1.0017, 1.0026999999999999, 1.0035000000000001,
            1.0044999999999999, 1.0054000000000001, 1.0064, 1.0075000000000001, 1.0085, 1.0097, 1.0108999999999999, 1.0121,
            1.0135000000000001, 1.0148999999999999, 1.0165 }),
        ("PtRh95", "K", "L", new[] { 1, 1.0009999999999999, 1.0018, 1.0026999999999999, 1.0036, 1.0044999999999999,
            1.0054000000000001, 1.0065, 1.0076000000000001, 1.0086999999999999, 1.01, 1.0113000000000001, 1.0127999999999999,
            1.0145999999999999, 1.0164, 1.0184 }),
        ("Rh", "M", "N", new[] { 1, 1.0008999999999999, 1.0018, 1.0027999999999999, 1.0039, 1.0048999999999999, 1.006,
            1.0071000000000001, 1.0083, 1.0095000000000001, 1.0107999999999999, 1.0121, 1.0135000000000001,
            1.0149999999999999, 1.0165, 1.0181 }),
        ("PtAu5", "O", "P", new[] { 1, 1.0008999999999999, 1.0018, 1.0027999999999999, 1.0038, 1.0047999999999999, 1.0059,
            1.0069999999999999, 1.0081, 1.0093000000000001, 1.0105999999999999, 1.0116000000000001, 1.0125,
            1.0135000000000001, N, N }),   // O18:O19 写 0
    };

    /// <summary>
    /// 工作簿 α 列公式 =IF(AND(LT/L0&gt;=1,T&gt;0),(LT/L0−1)/T*1000000,0) 的数据版：
    /// **无数据（NaN）或 T≤0 给 NaN，不给 0**。运算次序与公式相同（门逐位核 α 列缓存）。
    /// </summary>
    public static double TableAlphaE6(double tC, double ltOverL0)
        => double.IsNaN(ltOverL0) || !(ltOverL0 >= 1) || !(tC > 0) ? double.NaN : (ltOverL0 - 1) / tC * 1000000;

    /// <summary>ZGS 两列的温度（B9、B14、B16、B17）。</summary>
    public static readonly double[] ZgsTempsC = { 500, 1000, 1200, 1300 };
    /// <summary>Q9、Q14、Q16、Q17：ZGSPt 平均 α ×1e-6（工作簿存储值）。</summary>
    public static readonly double[] ZgsPtAlphaE6 = { 9.5, 10.199999999999999, 10.6, 10.7 };
    /// <summary>R9、R14、R16、R17：ZGSPtRh10 平均 α ×1e-6（工作簿存储值）。</summary>
    public static readonly double[] ZgsPtRh10AlphaE6 = { 10.199999999999999, 10.6, 11, 11.2 };

    /// <summary>
    /// 右上小表 T3:Y7（字符串「9.54×10」+ 上标「-6」；「-」= 无数据 ⇒ NaN）。温度 T4:T7。
    /// ⚠ 只作核对点：没有任何公式或图表系列引用它。U4（Pt 500 °C 9.54）与 LT/L0 表推出的 D9 9.60
    ///   不同 —— 物理把关算过不是矛盾：9.54 ⇒ LT/L0 1.00477，保留 4 位小数正是 C9 的 1.0048
    ///   （4 位小数对 α 的分辨率 500 °C ±0.10）。两值都登记，代码不替它选；主用 X14（9.580）。
    ///   ZGSPtAu5（Y 列）没有趋势线 ⇒ 工作簿里没有它的算法，只入数据点，不出函数。
    /// ⚠ 登记（2026-09-15，Opus 5）：ZGSPtAu5 的 Y5:Y7（1000／1200／1300 °C：12.5／13.1／14.3）比 PtAu5 LT/L0 表
    ///   推出的平均 α（10.6／10.42／10.38）高 18–38 %（门打印比值）。弥散强化相不该让膨胀系数高出这么多 ——
    ///   来源未注，只点名，不改数；ZGSPtAu5 不是设计牌号。
    /// </summary>
    public static readonly double[] SideTableTempsC = { 500, 1000, 1200, 1300 };
    public static readonly IReadOnlyList<(string Header, string Col, double[] AlphaE6)> SideTable = new[]
    {
        ("Pt", "U", new[] { 9.54, 10.19, N, N }),
        ("ZGSPt", "V", new[] { 9.5, 10.2, 10.6, 10.7 }),
        ("PtRh10%", "W", new[] { 10.2, 10.6, 10.9, 11.1 }),
        ("ZGSPtRh10%", "X", new[] { 10.2, 10.6, 11.0, 11.2 }),
        ("ZGSPtAu5%", "Y", new[] { 10.0, 12.5, 13.1, 14.3 }),
    };

    private static double[] TableDataTemps(int count)
        => TableTempsC.Skip(1).Take(count).ToArray();   // 图表 x 引用从 B5（100 °C）起，按 y 个数配对

    private static double[] TableDataAlpha(string alloy, int count)
    {
        var col = Table.Single(c => c.Alloy == alloy);
        return Enumerable.Range(1, count).Select(i => TableAlphaE6(TableTempsC[i], col.LtOverL0[i])).ToArray();
    }

    private static ExpansionCurve FromTable(string id, ExpansionCoeffOrigin o, string src, double[] c, int n, string dataSrc)
        => new(id, o, src, c, TableDataTemps(n), TableDataAlpha(id, n), dataSrc);

    /// <summary>全部趋势线（含只入数据目录、不进设计牌号下拉的 PtRh30/PtRh95/Rh/PtAu5）。</summary>
    public static readonly IReadOnlyDictionary<string, ExpansionCurve> Curves = BuildCurves();

    private static Dictionary<string, ExpansionCurve> BuildCurves()
    {
        const string chart = "chart1";
        var list = new List<ExpansionCurve>
        {
            // 纯铂：X14:X16 公式 0.0000000007798529*U^3 - 0.000001636835*U^2 + 0.002337597*U + 8.722923；
            //   = 图表系列 idx 0（x B5:B19 按 y 个数配对前 10 个，y D5:D14）3 阶趋势线（dispEq=1 但未存方程文本；
            //   复算器取 7 位与 X14 字面量逐位相同 —— 门核）。
            new("Pt", ExpansionCoeffOrigin.CellFormula,
                $"{WorkbookFile} X14:X16 单元格公式（= {chart} 系列 idx 0 的 3 阶趋势线）",
                new[] { 8.722923, 0.002337597, -0.000001636835, 0.0000000007798529 },
                TableDataTemps(10), TableDataAlpha("Pt", 10),
                $"{chart} 系列 idx 0：x B5:B14，y D5:D14（D 列 = C 列 LT/L0 按 α 公式）；{PurePtColumnSource}"),
            // PtRh10：系列 idx 1（隐藏），4 阶，dispEq=1 dispRSqr=0，未存方程文本 ⇒ 复算 7 位。
            FromTable("PtRh10", ExpansionCoeffOrigin.RecomputedSameAlgorithm,
                $"{chart} 系列 idx 1（F5:F19）4 阶趋势线，未存方程文本：精确最小二乘复算取 7 位",
                new[] { 9.952648, 2.217910E-04, 8.824078E-07, -1.165209E-09, 6.649451E-13 }, 15,
                $"{chart} 系列 idx 1：x B5:B19，y F5:F19；{PublishedTableSource}"),
            // PtRh20：系列 idx 2，6 阶。
            // ⚠ 2026-09-18（Opus 5，用户原话「改」）：工作簿 G18（1400 °C）由 1.0157 改正为 1.0152
            //   （Platinum Metals Rev., 1960, 4 (4), 139「20 % Rh」栏）⇒ H18 跟着变，这条趋势线的系数**必须重算**。
            //   Excel 不会自己重算图表里存的方程文本，标签仍是**改前**那一条：
            //   「y = -1.362330E-17x6 + 6.567769E-14x5 - 1.256153E-10x4 + 1.233031E-07x3 - 6.760337E-05x2
            //     + 2.352578E-02x + 4.206619E+00」，R² = 9.972203E-01 —— 留作旧值物证，门另核它确实 != 复算值。
            //   下面这组是按改正后的 15 个数据点、同阶数、同最小二乘（精确有理数）复算取 7 位；复算 R² = 9.987691E-01。
            //   改前 → 改后（7 位）：c0 4.206619E+00 → 4.611381E+00；c1 2.352578E-02 → 1.663118E-02；
            //   c2 -6.760337E-05 → -2.934794E-05；c3 1.233031E-07 → 2.885434E-08；c4 -1.256153E-10 → -1.130789E-11；
            //   c5 6.567769E-14 → -5.417849E-16；c6 -1.362330E-17 → 1.017062E-18。
            //   后果：瞬时 α 的下降段（改前 1322.1–1500 °C）整个消失 ⇒ Pt-Rh/80-20 不再标「拟合形状不可信」；
            //   500 mm 件 0 → 1400 °C 伸长 7.751329 mm → 7.597210 mm（差 0.154119 mm，下游有门）。
            FromTable("PtRh20", ExpansionCoeffOrigin.RecomputedLabelStale,
                $"{chart} 系列 idx 2（H5:H19）6 阶趋势线：标签存的方程文本是 Excel 按 G18 改正**前**的数据算的缓存，"
                + "已作废；系数按改正后的数据点用精确最小二乘复算取 7 位（2026-09-18，Opus 5）",
                new[] { 4.611381E+00, 1.663118E-02, -2.934794E-05, 2.885434E-08, -1.130789E-11, -5.417849E-16, 1.017062E-18 }, 15,
                $"{chart} 系列 idx 2：x B5:B19，y H5:H19（G18 已按文献改正）；{PublishedTableSource}"),
            FromTable("PtRh30", ExpansionCoeffOrigin.RecomputedSameAlgorithm,
                $"{chart} 系列 idx 3（J5:J19）4 阶趋势线，未存方程文本：精确最小二乘复算取 7 位",
                new[] { 9.103090, -2.727107E-03, 6.772323E-06, -4.899401E-09, 1.440492E-12 }, 15,
                $"{chart} 系列 idx 3：x B5:B19，y J5:J19" + $"；{PublishedTableSource}"),
            FromTable("PtRh95", ExpansionCoeffOrigin.ChartLabelText,
                $"{chart} 系列 idx 4（L5:L19）6 阶趋势线标签文本",
                new[] { 1.189567E+01, -2.740299E-02, 9.520664E-05, -1.626077E-07, 1.485282E-10, -6.802056E-14, 1.235069E-17 }, 15,
                $"{chart} 系列 idx 4：x B5:B19，y L5:L19" + $"；{PublishedTableSource}"),
            FromTable("Rh", ExpansionCoeffOrigin.ChartLabelText,
                $"{chart} 系列 idx 5（N5:N19）6 阶趋势线标签文本",
                new[] { 9.468245E+00, -9.488164E-03, 5.711332E-05, -1.232810E-07, 1.302049E-10, -6.631743E-14, 1.306742E-17 }, 15,
                $"{chart} 系列 idx 5：x B5:B19，y N5:N19" + $"；{PublishedTableSource}"),
            // PtAu5：y 引用 P5:P17（13 点到 1300 °C），不是表格的 1500。
            FromTable("PtAu5", ExpansionCoeffOrigin.ChartLabelText,
                $"{chart} 系列 idx 6（P5:P17）6 阶趋势线标签文本",
                new[] { 9.804849E+00, -1.560074E-02, 9.673000E-05, -2.461481E-07, 3.129411E-10, -1.928555E-13, 4.566959E-17 }, 13,
                $"{chart} 系列 idx 6：x B5:B19 配对前 13 个，y P5:P17"),
            // ZGS 两条：系列 idx 7/8，2 阶，并集引用 (B9,B14,B16,B17)/(Q…)/(R…)，未存方程文本 ⇒ 复算 7 位。
            //   ⚠ ZGSPtRh10 二次式 α_mean 顶点在 474 °C：500 °C 以下外推时 α_mean 反而增大；瞬时 α 在约 316 °C 以下
            //   随温度下降 ⇒ 形状不可信段（由导数算出）。
            //   旧 VB MainForm1.vb:111 用的是 1 次式 —— 按用户指令跟工作簿，分歧记在 HANDOVER。
            new("ZGSPt", ExpansionCoeffOrigin.RecomputedSameAlgorithm,
                $"{chart} 系列 idx 7（Q9,Q14,Q16,Q17）2 阶趋势线，未存方程文本：精确最小二乘复算取 7 位",
                new[] { 8.921111, 1.005340E-03, 2.954788E-07 }, ZgsTempsC, ZgsPtAlphaE6,
                $"{chart} 系列 idx 7：x (B9,B14,B16,B17)，y (Q9,Q14,Q16,Q17)"),
            new("ZGSPtRh10", ExpansionCoeffOrigin.RecomputedSameAlgorithm,
                $"{chart} 系列 idx 8（R9,R14,R16,R17）2 阶趋势线，未存方程文本：精确最小二乘复算取 7 位",
                new[] { 1.053136E+01, -1.406906E-03, 1.484514E-06 }, ZgsTempsC, ZgsPtRh10AlphaE6,
                $"{chart} 系列 idx 8：x (B9,B14,B16,B17)，y (R9,R14,R16,R17)"),
        };
        return list.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// APP 牌号 → 曲线。**MaterialDb.All 的每个牌号都必须在这里显式出现一次**（门核），不许默认。
    /// FKS16 两个牌号见 <see cref="ExpansionBorrowOption"/>：默认无数据。
    /// </summary>
    public static readonly IReadOnlyList<GradeExpansionMap> GradeMap = new[]
    {
        new GradeExpansionMap("Pt", "Pt", ExpansionLink.Direct,
            "工作簿列头 Pt（纯铂）；APP 设计默认牌号。" + PurePtColumnSource),
        new GradeExpansionMap("Pt-Rh/90-10", "PtRh10", ExpansionLink.Direct,
            "同名义成分 PtRh10；出处 Platinum Metals Rev., 1960, 4 (4), p.139「10 % Rh」实测栏"
            + "（工作簿 E 列与该栏 16/16 逐位相同；供应商仍未注明）"),
        new GradeExpansionMap("Pt-Rh/80-20", "PtRh20", ExpansionLink.Direct,
            "同名义成分 PtRh20；出处 Platinum Metals Rev., 1960, 4 (4), p.139「20 % Rh」实测栏"
            + "（工作簿 G 列是这张表舍到 4 位小数的副本；G18 那一格的转录错误 1.0157 已于 2026-09-18 按论文改正为 1.0152，"
            + "改前它使 1322.1–1500 °C 的瞬时 α 下降、被标「形状不可信」，改正后该段消失）"),
        new GradeExpansionMap("Tanaka-ZGS-Pt", "ZGSPt", ExpansionLink.Direct, "工作簿 ZGSPt 列（Q 列）"),
        new GradeExpansionMap("Tanaka-ZGS-PtRh10", "ZGSPtRh10", ExpansionLink.Direct, "工作簿 ZGSPtRh10% 列（R 列）"),
        new GradeExpansionMap("Umicore-PtRh10", "PtRh10", ExpansionLink.Borrowed, "同名义成分借用 PtRh10，膨胀表未注明供应商"),
        new GradeExpansionMap("Umicore-PtRh20", "PtRh20", ExpansionLink.Borrowed, "同名义成分借用 PtRh20，膨胀表未注明供应商"),
        new GradeExpansionMap("FKS16/Pt", null, ExpansionLink.None,
            "膨胀工作簿没有 FKS16；借同基体 Pt 还是借 ZGSPt 是工程判断，默认无数据（见 ExpansionBorrowOption）"),
        new GradeExpansionMap("FKS16/PtRh-9010", null, ExpansionLink.None,
            "膨胀工作簿没有 FKS16；借同基体 PtRh10 还是借 ZGSPtRh10 是工程判断，默认无数据（见 ExpansionBorrowOption）"),
        new GradeExpansionMap("Pd", null, ExpansionLink.None, "膨胀工作簿没有 Pd"),
        new GradeExpansionMap("Ni", null, ExpansionLink.None, "膨胀工作簿没有 Ni"),
        new GradeExpansionMap("Cu", null, ExpansionLink.None, "膨胀工作簿没有 Cu"),
    };

    /// <summary>按牌号解析映射（含 FKS16 显式借用开关）。牌号不在材料库 ⇒ 抛出（与 MaterialDb.Get 同口径）。</summary>
    public static GradeExpansionMap Resolve(string grade, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
    {
        MaterialDb.Get(grade);   // 名字打错就在这里说清楚
        var m = GradeMap.FirstOrDefault(x => x.Grade == grade)
            ?? throw new KeyNotFoundException($"牌号「{grade}」在材料库里，却没有膨胀映射 —— 新加牌号必须显式登记（有/借/无）");
        if (borrow == ExpansionBorrowOption.None || m.CurveId is not null) return m;
        return (grade, borrow) switch
        {
            ("FKS16/Pt", ExpansionBorrowOption.SameBaseAlloy) =>
                new(grade, "Pt", ExpansionLink.Borrowed, "显式借用：FKS16/Pt 借同基体纯铂 Pt（工程判断，待定）"),
            ("FKS16/Pt", ExpansionBorrowOption.DispersionStrengthened) =>
                new(grade, "ZGSPt", ExpansionLink.Borrowed, "显式借用：FKS16/Pt 借同为弥散强化的 ZGSPt（工程判断，待定）"),
            ("FKS16/PtRh-9010", ExpansionBorrowOption.SameBaseAlloy) =>
                new(grade, "PtRh10", ExpansionLink.Borrowed, "显式借用：FKS16/PtRh-9010 借同基体 PtRh10（工程判断，待定）"),
            ("FKS16/PtRh-9010", ExpansionBorrowOption.DispersionStrengthened) =>
                new(grade, "ZGSPtRh10", ExpansionLink.Borrowed, "显式借用：FKS16/PtRh-9010 借同为弥散强化的 ZGSPtRh10（工程判断，待定）"),
            _ => m,
        };
    }

    private static ExpansionValue Missing(GradeExpansionMap m)
        => new(double.NaN, ExpansionCoverage.NoData, ExpansionLink.None, "", $"{m.Grade}：{m.Why}");

    private static ExpansionValue Point(string grade, double tC, ExpansionBorrowOption borrow, bool derivative,
        Func<ExpansionCurve, double> f)
    {
        var m = Resolve(grade, borrow);
        if (m.CurveId is null) return Missing(m);
        var c = Curves[m.CurveId];
        var cov = c.Classify(tC, derivative, out string note);
        string link = m.Link == ExpansionLink.Borrowed ? $"【借用】{m.Why}；" : "";
        double v = cov == ExpansionCoverage.NoData ? double.NaN : f(c);
        // 2026-09-15 第四轮（Opus 5，复核 minor）：温度在区间内、值却算出 NaN（HotLength／Elongation 的 L0 为 NaN，
        // 或 L0 = ±∞ 时伸长 ∞ − ∞）⇒ 无数据，守「Value 为 NaN ⇔ Coverage == NoData」。
        // 2026-09-16 第五轮（Opus 5，核验员小备注）：L0 = +∞ 时 HotLength 曾返回 +∞ 且 HasValue = true —— 定义为「L0 非有限 ⇒ 无数据」，
        // 不变式收紧为「Value 非有限 ⇔ Coverage == NoData」（无数据时 Value 一律 NaN）。有限输入的值一位不变。
        if (!double.IsFinite(v) && cov != ExpansionCoverage.NoData)
        { cov = ExpansionCoverage.NoData; v = double.NaN; note += "；按输入算出的值不是有限数（长度 L0 不是有限数），无数据"; }
        return new(v, cov, m.Link, c.Id, link + note);
    }

    /// <summary>平均线膨胀系数 ×1e-6 /K（0 °C → T）。0 ≤ T &lt; 100 °C 取 100 °C 拟合值（定义点内插）。</summary>
    public static ExpansionValue MeanAlphaE6(string grade, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
        => Point(grade, tC, borrow, false, c => c.MeanAlphaE6At(tC));

    /// <summary>热伸长比 LT/L0 = 1 + α_mean·1e-6·T（L0 = 0 °C 长度）。</summary>
    public static ExpansionValue LengthRatio(string grade, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
        => Point(grade, tC, borrow, false, c => 1 + c.MeanAlphaE6At(tC) * 0.000001 * tC);

    /// <summary>热应变 ε = α_mean·1e-6·T（**相对 0 °C 长度**）。</summary>
    public static ExpansionValue Strain(string grade, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
        => Point(grade, tC, borrow, false, c => c.StrainAt(tC));

    /// <summary>
    /// 瞬时线膨胀系数 ×1e-6 /K = d(α_mean·T)/dT —— **派生量**（工作簿没直接给），解析求导。
    /// 端部间隔内标 InRangeNearEnd；0 ≤ T &lt; 100 °C 为 ε 线性段的斜率（= 100 °C 平均 α），
    /// ⚠ 所以瞬时 α 在 100 °C 处有台阶（左侧 = 100 °C 的平均 α，右侧 = 多项式导数）。
    /// 2026-09-15 第四轮（Opus 5）：0–<see cref="InstantAlphaStepNoteMaxC"/> °C 的返回值注记点名台阶大小
    /// （<see cref="ExpansionCurve.InstantAlphaStepAt100"/>；Pt-Rh/80-20 与借用它的 Umicore-PtRh20 是 +19.3 %
    /// —— 2026-09-18 G18 改正前是 +22.1 %，见 HANDOVER 0.-6）。
    /// </summary>
    public static ExpansionValue InstantAlphaE6(string grade, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
    {
        var v = Point(grade, tC, borrow, true, c => c.InstantAlphaE6At(tC));
        if (v.HasValue && tC <= InstantAlphaStepNoteMaxC)
            v = v with { Note = v.Note + "；" + Curves[v.Curve].InstantAlphaStepNote };
        return v;
    }

    /// <summary>瞬时 α 的注记在 0 °C 到这个温度（含）之间点名 100 °C 台阶。2026-09-15 第四轮，Opus 5。</summary>
    public const double InstantAlphaStepNoteMaxC = 200;

    /// <summary>
    /// 工作簿 Y 列公式 =V*(1+X*0.000001*U) 的原样实现（门拿 Y14:Y16 缓存逐位核）。
    /// ⚠ L0 是 **0 °C 长度**；图纸长度若是 20 °C 的，伸长偏大约 175 µε（950 °C 时约 2 %）。
    /// </summary>
    public static double HotLengthFromAlpha(double l0, double alphaE6, double tC) => l0 * (1 + alphaE6 * 0.000001 * tC);

    /// <summary>热态长度 LT（与 L0 同单位；L0 为 0 °C 长度）。L0 非有限（NaN／±∞）⇒ 无数据、值 NaN（2026-09-16，Opus 5）。</summary>
    public static ExpansionValue HotLength(string grade, double l0At0C, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
        => Point(grade, tC, borrow, false, c => HotLengthFromAlpha(l0At0C, c.MeanAlphaE6At(tC), tC));

    /// <summary>伸长 ΔL = LT − L0（工作簿 Z 列公式 =Y−V）。L0 非有限（NaN／±∞）⇒ 无数据、值 NaN（2026-09-16，Opus 5）。</summary>
    public static ExpansionValue Elongation(string grade, double l0At0C, double tC, ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
        => Point(grade, tC, borrow, false, c => HotLengthFromAlpha(l0At0C, c.MeanAlphaE6At(tC), tC) - l0At0C);

    /// <summary>
    /// **两温度（可跨牌号）热应变差** Δε = ε_A(T_A) − ε_B(T_B) = [α_A(T_A)·T_A − α_B(T_B)·T_B]·1e-6。
    ///
    /// 为什么要专门给：调用方最容易写成 α_mean·ΔT —— 纯铂 1200–1500 °C、ΔT 10 K 时会**低估 17–26 %**
    /// （物理把关算）；两个 LT/L0 表值直接相减在 10 K 时误差可近 100 %。
    ///
    /// ⚠ 口径：两个应变都**相对各自 0 °C 长度**。若要「A 相对 B 热态长度」的失配应变，
    ///   是 Δε/(1+ε_B)，与本值相差约 1.1–1.5 %（1100–1500 °C）。
    /// ⚠ 覆盖类别 = 两端（各按 <see cref="Strain"/> 的口径）中**较差**的一端；任一端无数据 ⇒ NaN。
    ///   任一端借用 ⇒ Link = Borrowed。（2026-09-15 第二轮，Opus 5，口径 D4②：删掉「两端一律按导数类量标端部」
    ///   与两温度之间另查形状段 —— 覆盖只看两端。）
    /// </summary>
    public static ExpansionValue StrainDifference(string gradeA, double tA, string gradeB, double tB,
        ExpansionBorrowOption borrow = ExpansionBorrowOption.None)
    {
        var ma = Resolve(gradeA, borrow);
        var mb = Resolve(gradeB, borrow);
        if (ma.CurveId is null) return Missing(ma);
        if (mb.CurveId is null) return Missing(mb);
        var ca = Curves[ma.CurveId];
        var cb = Curves[mb.CurveId];
        var covA = ca.Classify(tA, false, out string na);
        var covB = cb.Classify(tB, false, out string nb);
        var cov = (ExpansionCoverage)Math.Max((int)covA, (int)covB);
        string note = na + "；" + nb;
        var link = ma.Link == ExpansionLink.Borrowed || mb.Link == ExpansionLink.Borrowed ? ExpansionLink.Borrowed : ExpansionLink.Direct;
        if (ma.Link == ExpansionLink.Borrowed) note = $"【借用】{ma.Why}；" + note;
        if (mb.Link == ExpansionLink.Borrowed && mb.Grade != ma.Grade) note = $"【借用】{mb.Why}；" + note;
        double v = cov == ExpansionCoverage.NoData ? double.NaN : ca.StrainAt(tA) - cb.StrainAt(tB);
        return new(v, cov, link, ca.Id == cb.Id ? ca.Id : $"{ca.Id}−{cb.Id}", note);
    }
}
