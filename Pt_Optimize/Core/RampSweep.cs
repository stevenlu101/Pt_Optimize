using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：升温全程（准静态轨迹）——  生产件。2026-09-17，Opus 5。
//
//  算法从两版探针（R48RampTubeElongationProbeTests / R48RampFlangeExpansionProbeTests）
//  提成，探针改调这里，不许第二份实现。
//  用户 2026-09-16/17：升温期不卡 ±5 K；没有膨胀判据，只报告膨胀量；δ=0.1 mm 退役。
//  门（硬）：场无效 ⇒ 判不了；管 J 或截面 J 超限 ⇒ 不过；否则过。
//  ★ 决 103（业主 2026-09-24）：管 J 限值读 DesignInputs.TubeJLimitAPerMm2（= min(许用 12, 使用上限 11)）；并加热稳定（局部全格、整片）≥ 1，
//    判不了不算过（全局方案第 2 版 1.2 节）；改回口径（决103前）不判热稳定、限值 = 许用，逐位同改前。仍不卡温差、没有膨胀判据，伸长量照印。
// ════════════════════════════════════════════════════════════════════════════

// ══════════ 积分工具：管段伸长 ══════════

/// <summary>
/// 管段的非均匀伸长积分器（梯形权重）。2026-09-17，Opus 5。
/// 从 R48RampTubeElongationProbeTests 的 internal RampElongation 提成公开件。
/// </summary>
public static class TubeElongation
{
    /// <summary>某个节点区间 [i0, i1]（含两端）上的梯形权重（端节点半格）。</summary>
    private static double Weight(double[] x, int i, int i0, int i1)
    {
        if (i == i0) return 0.5 * (x[i + 1] - x[i]);
        if (i == i1) return 0.5 * (x[i] - x[i - 1]);
        return 0.5 * (x[i + 1] - x[i - 1]);
    }

    /// <summary>逐节点积分量 g（无量纲）沿 [i0, i1] 的梯形积分，x 单位 mm ⇒ 返回 mm。</summary>
    public static double Integrate(double[] x, double[] g, int i0, int i1)
    {
        double s = 0;
        for (int i = i0; i <= i1; i++) s += Weight(x, i, i0, i1) * g[i];
        return s;
    }

    /// <summary>以 m 为分界（m 记两个半格）切成两半段积分。</summary>
    public static (double A, double B) Halves(double[] x, double[] g, int m)
        => (Integrate(x, g, 0, m), Integrate(x, g, m, x.Length - 1));

    /// <summary>逐节点应变差 g[i] = ε(牌号, T(x_i)) − ε(牌号, T设定)，并回报最坏覆盖。</summary>
    public static double[] StrainDiff(double[] tMetalC, double tSetC, string grade,
                                      out ExpansionCoverage worst, out string worstNote)
    {
        var g = new double[tMetalC.Length];
        worst = ExpansionCoverage.InRange; worstNote = "";
        for (int i = 0; i < tMetalC.Length; i++)
        {
            var v = PtThermalExpansion.StrainDifference(grade, tMetalC[i], grade, tSetC);
            g[i] = v.Value;
            if ((int)v.Coverage > (int)worst) { worst = v.Coverage; worstNote = v.Note; }
        }
        return g;
    }

    /// <summary>覆盖类别的中文文字。</summary>
    public static string CoverageText(ExpansionCoverage c) => c switch
    {
        ExpansionCoverage.InRange => "区间内",
        ExpansionCoverage.InRangeNearEnd => "区间内·端部",
        ExpansionCoverage.EndDefinedPointInterp => "端部定义点内插",
        ExpansionCoverage.Extrapolated => "外推",
        ExpansionCoverage.ShapeUntrusted => "形状不可信",
        _ => "无数据",
    };
}

// ══════════ 积分工具：法兰片膨胀 ══════════

/// <summary>
/// 法兰片上的三件事：圆盘加权平均应变、沿舌长的一维廓线积分、温度汇总。2026-09-17，Opus 5。
/// 从 R48RampFlangeExpansionProbeTests 的 internal FlangeExpansion 提成公开件。
/// </summary>
public static class FlangeExpansionCalc
{
    /// <summary>安装温度 °C（工程师可改；默认 20 °C）。</summary>
    public const double DefaultTAssemblyC = 20.0;

    /// <summary>一列 = 同一个 x 区间上的所有单元；TMeanC = 该列按单元面积加权的平均温度。</summary>
    public sealed class Column
    {
        public double X0, X1, AreaMm2, TMeanC;
        public double Width => X1 - X0;
    }

    /// <summary>把网格按 x 列合并成廓线（列按 X0 升序）。温度按面积加权。</summary>
    public static List<Column> Columns(ShellMesh m, double[] tC)
    {
        var acc = new Dictionary<(long, long), (double x0, double x1, double a, double at)>();
        for (int i = 0; i < m.CellCount; i++)
        {
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity;
            foreach (int nd in m.Cells[i]) { var v = m.Nodes[nd]; x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); }
            var key = ((long)Math.Round(x0 * 1e6), (long)Math.Round(x1 * 1e6));
            double a = m.Area[i];
            acc.TryGetValue(key, out var cur);
            acc[key] = (x0, x1, cur.a + a, cur.at + a * tC[i]);
        }
        return acc.Values.Where(v => v.a > 1e-12)
                  .Select(v => new Column { X0 = v.x0, X1 = v.x1, AreaMm2 = v.a, TMeanC = v.at / v.a })
                  .OrderBy(c => c.X0).ToList();
    }

    /// <summary>廓线在 [xLo, xHi] 上的积分 ∫ f(T̄(x)) dx。</summary>
    public static double Integrate(IEnumerable<Column> cols, double xLo, double xHi, Func<double, double> f)
    {
        double s = 0;
        foreach (var c in cols)
        {
            double ov = Math.Min(xHi, c.X1) - Math.Max(xLo, c.X0);
            if (ov <= 1e-12) continue;
            s += ov * f(c.TMeanC);
        }
        return s;
    }

    /// <summary>廓线在 [xLo, xHi] 上的长度加权平均温度 °C。</summary>
    public static double MeanTempOver(IEnumerable<Column> cols, double xLo, double xHi)
    {
        double s = 0, L = 0;
        foreach (var c in cols)
        {
            double ov = Math.Min(xHi, c.X1) - Math.Max(xLo, c.X0);
            if (ov <= 1e-12) continue;
            s += ov * c.TMeanC; L += ov;
        }
        return L > 1e-12 ? s / L : double.NaN;
    }

    /// <summary>圆盘区权重 = 面积 × 板厚 × 份额（走生产件 MaterialFraction）。</summary>
    public static double[] DiscWeights(ShellMesh m, double discRadiusMm, out int fellBackCells)
    {
        fellBackCells = 0;
        var w = new double[m.CellCount];
        for (int i = 0; i < m.CellCount; i++)
        {
            double f = FlangeMesher.MaterialFractionInCircle(m, i, discRadiusMm);   // 2026-09-18 Fable 5.1：解析板按精确积分、栅格按方格中心（同一入口；老的谓词版对解析路径已无栅格可查）
            if (double.IsNaN(f))
            {
                fellBackCells++;
                var c = m.Centroid[i];
                f = FlangePlate.InsideInsulCircle(c.X, c.Z, discRadiusMm) ? 1.0 : 0.0;
            }
            w[i] = m.Area[i] * m.Thickness[i] * f;
        }
        return w;
    }

    /// <summary>按给定权重求加权平均。</summary>
    public static double WeightedMean(double[] w, double[] v)
    {
        double sw = 0, sv = 0;
        for (int i = 0; i < w.Length; i++) { if (w[i] <= 0) continue; sw += w[i]; sv += w[i] * v[i]; }
        return sw > 1e-12 ? sv / sw : double.NaN;
    }

    /// <summary>ε(牌号, T) —— 相对 0 °C 长度的热应变。</summary>
    public static double Strain(string grade, double tC, ref ExpansionCoverage worst, ref string worstNote)
    {
        var v = PtThermalExpansion.Strain(grade, tC);
        if ((int)v.Coverage > (int)worst) { worst = v.Coverage; worstNote = v.Note; }
        return v.Value;
    }
}

// ══════════ 选项与结果 ══════════

/// <summary>升温全程扫描选项。2026-09-17，Opus 5。</summary>
public sealed class RampSweepOptions
{
    /// <summary>轨迹设定点 °C（默认 300–1150 全线同值）。</summary>
    public double[] TrajectoryC = { 300, 450, 600, 750, 900, 1000, 1080, 1150 };

    /// <summary>安装温度 °C（报告量的零基准）。</summary>
    public double TAssemblyC = FlangeExpansionCalc.DefaultTAssemblyC;

    /// <summary>true = 在 ≥600 °C 的设定点额外跑一遍 ClampAltC 夹头温度。本轮默认 false 省时。</summary>
    public bool RunClampAlt = false;

    /// <summary>ClampAlt 的夹头温度 °C（仅 RunClampAlt 时用）。</summary>
    public double ClampAltC = 450.0;

    /// <summary>ClampAlt 的起跑温度 °C。</summary>
    public double ClampAltFromC = 600.0;

    /// <summary>铂材牌号（默认纯铂）。</summary>
    public string Grade = "Pt";

    /// <summary>
    /// ★★★★★ 2026-09-17，Opus 5（补上一轮 Opus 4.6 留下的洞）：
    /// true（默认，**生产口径**）= 升温每个设定点的截面 J／管 J 除了「设计电流」那条，还按**该点实际解出来的电流**
    /// 逐片／逐段重算一条，**两条都要过**才算这一点过；
    /// false = 退回「只读设计电流」的历史口径 —— **只供门注入用**，不是给工程师的开关。
    ///
    /// 为什么必须有实际电流那条：设计电流是 20 °C/h 空管升温**全程峰值**（<see cref="DesignCurrent"/>，闭式、与工况无关），
    /// 它对每一个设定点都是同一个数；而升温轨迹上每一点的段电流是当场解出来的，低温点小、接近目标时大。
    /// 只看设计电流 ⇒ 轨迹上所有点的截面 J 完全相同（上一轮的输出表里 27.71 印了八遍），
    /// 那张表回答不了「升温到哪一段才超限」，而且一旦某点的实际电流**高过**设计电流（共用片矢量合成、段间不同步时会发生），
    /// 就会漏判。
    /// </summary>
    public bool SectionJFromActualCurrent = true;

    /// <summary>
    /// ★ R48 L（2026-09-17，Opus 5）：升温全程每个设定点整线算例的**网格口径**。
    ///
    /// null（默认）= 一行不动，用 <see cref="DesignSpec.BuildCase"/> 造出来的网格（细区 2.0 mm／细区半径 50 mm），
    /// 即改前的行为；给一份 <see cref="SolverOptions"/> ⇒ 原样交给 <see cref="Solver.ApplyCaseMesh"/>
    /// （全仓唯一那份造算例网格的配方），升温这一关就跑在与求根／判决同一张网格上。
    ///
    /// 为什么要有它：细网格第二遍求出来的根，若升温这一关仍判在导航网格上，
    /// 三关就不在同一张网格上 —— 「根的位置随网格移动」（Solver 类注释 A⑬，实测差到 2.03 倍）
    /// 对升温里那几条场解出来的量（各段实际电流 ⇒ 实际电流口径的管 J 与截面 J）同样成立。
    /// ⚠ 只改网格，不改判据、不改轨迹；设计电流那条是闭式的，本来就与网格无关。
    /// </summary>
    public SolverOptions? Mesh = null;
}

/// <summary>升温全程每个设定点的结果。2026-09-17，Opus 5。</summary>
public sealed class RampSweepPointResult
{
    public double SetpointC;
    public double ClampC;
    public LineResult LineRes = null!;

    // ── 门判定依据 ──
    public bool FieldValid;
    public string FieldInvalidReason = "";

    /// <summary>
    /// 本点的**设计电流**管 J（= 20 °C/h 空管升温全程峰值 ÷ 管截面，闭式、与本点工况无关）
    /// —— 从整线判据「① 升温」原样带出，不在这里另算一份。2026-09-17，Opus 5。
    /// </summary>
    public double TubeJDesignAPerMm2 = double.NaN;
    public double TubeJDesignLimit = double.NaN;
    public bool TubeJDesignOk;
    /// <summary>设计电流那条算不出来（判不了，不许当过）。</summary>
    public bool TubeJDesignUndetermined = true;

    // ── 决 103（业主 2026-09-24；全局方案第 2 版 1.2 节「升温期硬：J、不熔断、热稳定 ≥ 1、场有效」）：本点的两条热稳定 ──
    /// <summary>这一点判了热稳定没有（改回口径 = false：与改前逐位相同，判定只看 J 与场有效）。</summary>
    public bool StabChecked;
    /// <summary>整片热稳定、局部热稳定（全格精算）裕度 ×（原样取自本点整线判据表那两行，不另算一份）。</summary>
    public double FlangeStabMargin = double.NaN, LocalStabMargin = double.NaN;
    public string FlangeStabWhere = "", LocalStabWhere = "";
    /// <summary>两条都过（且都判得了）。</summary>
    public bool StabOk;
    /// <summary>任一条判不了 ⇒ 不许当过。</summary>
    public bool StabUndetermined;

    // ── 逐段 ──
    public RampSegInfo[] Segs = Array.Empty<RampSegInfo>();

    // ── 逐片 ──
    public RampFlangeInfo[] Flanges = Array.Empty<RampFlangeInfo>();
}

/// <summary>升温全程某设定点某段的信息。</summary>
public sealed class RampSegInfo
{
    public string Name = "";

    /// <summary>该点该段**实际解出来的**电流 A。</summary>
    public double CurrentA;

    /// <summary>该点该段实际电流下的管 J（<see cref="SegmentOut.TubeJAPerMm2"/> 原样带出，不另算一份）。</summary>
    public double TubeJAPerMm2;
    public double TubeJLimit;
    public bool TubeJOk;

    /// <summary>管段总伸长（相对 T装）mm。</summary>
    public double TotalElongMm;
    public ExpansionCoverage ElongCoverage;
}

/// <summary>
/// 升温某点某片截面 J 的**两条并列判定**：设计电流那条与该点实际电流那条。2026-09-17，Opus 5。
/// 由 <see cref="RampSweep.JudgeSectionJ"/> 造，生产与门共用同一份 —— 门不许自己再拼一遍规则。
/// </summary>
public readonly record struct SectionJVerdict(
    double DesignJ, string DesignWhere, bool DesignOk,
    double ActualCurrentA, double ActualJ, string ActualWhere, bool ActualOk,
    double Limit, bool Undetermined, bool Ok, bool Disagree, bool ActualUsed);

/// <summary>升温全程某设定点某片的信息。</summary>
public sealed class RampFlangeInfo
{
    public string Name = "";

    /// <summary>截面 J 的两条并列判定（设计电流／该点实际电流）。</summary>
    public SectionJVerdict SectionJ;

    /// <summary>限值 A/mm²（= 设定 J + 1）。</summary>
    public double SectionJLimit;

    /// <summary>两条都过（且都判得了）。</summary>
    public bool SectionJOk;

    /// <summary>任一条算不出来 ⇒ 判不了，**不许当过**。</summary>
    public bool SectionJUndetermined;

    /// <summary>管根−舌板面积加权平均温差 K。</summary>
    public double TRootMinusTTabMeanK;

    /// <summary>舌板相对管的位移 mm（探针的 Q2）。</summary>
    public double TabDisplacementMm;

    /// <summary>舌尖相对装配态总径向位移 mm。</summary>
    public double TipTotalDisplacementMm;

    /// <summary>盘外缘径向伸长 mm。</summary>
    public double DiscRadialMm;

    /// <summary>压接段伸长 mm。</summary>
    public double ClampElongMm;

    /// <summary>覆盖类别（报告量的，含 ε(T装)）。</summary>
    public ExpansionCoverage ReportCoverage;

    /// <summary>>1000 °C 时 true。</summary>
    public bool IsExtrapolated;
}

/// <summary>升温全程扫描结果。2026-09-17，Opus 5。</summary>
public sealed class RampSweepResult
{
    public RampSweepPointResult[] Points = Array.Empty<RampSweepPointResult>();
    public double ElapsedSeconds;

    /// <summary>
    /// ★ 2026-09-18，Opus 5：**这一跑实际用的安装温度与牌号**（原样取自 <see cref="RampSweepOptions"/>）。
    /// 伸长量是「相对安装温度」的差、按牌号的膨胀曲线算出来的 —— 报告里不写这两个数，
    /// 那张伸长表就回答不了「相对什么、按哪个牌号」。⚠ 报告只许读这里，不许自己再取一遍默认值。
    /// </summary>
    public double TAssemblyC = double.NaN;
    /// <summary>同上：这一跑用的铂材牌号。</summary>
    public string Grade = "";

    // ── 门结论 ──

    /// <summary>"过"/"不过"/"判不了"。</summary>
    public string Verdict = "判不了";

    /// <summary>不过/判不了的具体原因。</summary>
    public string VerdictDetail = "";

    /// <summary>计算汇总文本（给报告用的几行人话）。</summary>
    public string Summary = "";

    /// <summary>
    /// 「设计电流那条与实际电流那条**结论不同**」的逐条说明（空 = 两条处处同结论）。2026-09-17，Opus 5。
    /// 用户 2026-09-17：两者结论不同要写明。
    /// </summary>
    public string[] DisagreeNotes = Array.Empty<string>();
}

// ══════════ 扫描器 ══════════

/// <summary>
/// 升温全程（准静态轨迹）扫描。2026-09-17，Opus 5。
///
/// 门（硬，写在代码里）：
///   任一点场无效、或任一条 J 算不出来 ⇒ 升温「判不了」并点名点与原因；
///   任一点管 J 或截面 J 超限 ⇒ 升温「不过」并点名；
///   否则「过」。
///
/// ★ 2026-09-17，Opus 5：J 一律**两条并列**判 —— 设计电流那条（闭式、全程峰值、对所有设定点是同一个数）
///   与**该点实际解出来的电流**那条，两条都要过。上一轮（Opus 4.6）只读设计电流，
///   于是八个设定点的截面 J 印出来是同一个 27.71，那张表回答不了「升到哪里才超」，
///   而且实际电流高过设计电流时会漏判。开关见 <see cref="RampSweepOptions.SectionJFromActualCurrent"/>。
/// </summary>
public static class RampSweep
{
    /// <summary>
    /// ★★★★★ 一片法兰的截面 J **两条并列判定**（生产与门共用的唯一实现）。2026-09-17，Opus 5。
    ///
    /// <paramref name="designJ"/>／<paramref name="designWhere"/> 是整线判据「法兰截面 J」算好的那一条
    /// （设计电流 = 20 °C/h 升温全程峰值，<see cref="DesignCurrent"/>），原样带进来，不在这里重算；
    /// <paramref name="actualCurrentA"/> 是**该设定点**这一片实际解出来的电流（<c>FlangeOut.CurrentA</c>，
    /// 由 <see cref="LineSolver.JointCurrentA"/> 从各段实际电流矢量合成），按它走一遍
    /// <see cref="SectionSizing.Worst"/>（与判据同一个截面配方）。
    ///
    /// 判不了不算过：任一条算不出来（几何没有／电流 ≤ 0／截面积为 0 ⇒ NaN）就置
    /// <see cref="SectionJVerdict.Undetermined"/>，<see cref="SectionJVerdict.Ok"/> 为 false。
    /// </summary>
    public static SectionJVerdict JudgeSectionJ(FlangePlate? plate, double designJ, string designWhere,
        double actualCurrentA, double clampLenMm, double limit, bool useActualCurrent)
    {
        bool designUndet = double.IsNaN(designJ);
        bool designOk = !designUndet && designJ < limit;   // 与 LineRunner 的截面 J 判据同一个不等号（严格小于）

        double actualJ = double.NaN; string actualWhere = ""; bool actualUndet = false, actualOk = true;
        if (useActualCurrent)
        {
            if (plate is null || !(actualCurrentA > 0)) { actualUndet = true; actualOk = false; actualWhere = plate is null ? "没有法兰几何" : "实际电流 ≤ 0"; }
            else
            {
                var cut = SectionSizing.Worst(plate, actualCurrentA, clampLenMm);
                actualJ = cut.JAPerMm2; actualWhere = cut.Where;
                actualUndet = double.IsNaN(actualJ);
                actualOk = !actualUndet && actualJ < limit;
            }
        }

        bool undet = designUndet || actualUndet;
        return new SectionJVerdict(
            DesignJ: designJ, DesignWhere: designWhere, DesignOk: designOk,
            ActualCurrentA: actualCurrentA, ActualJ: actualJ, ActualWhere: actualWhere, ActualOk: actualOk,
            Limit: limit, Undetermined: undet,
            Ok: !undet && designOk && actualOk,
            Disagree: useActualCurrent && !undet && designOk != actualOk,
            ActualUsed: useActualCurrent);
    }

    /// <summary>
    /// ★★★★★ 把逐点结果汇总成一句判定（生产与门共用的唯一实现）。2026-09-17，Opus 5。
    /// 三个桶按硬顺序：判不了（场无效／任一条 J 算不出来）＞ 不过（任一条 J 超限）＞ 过。
    /// </summary>
    public static (string Verdict, string Detail, string[] Disagree) Aggregate(IReadOnlyList<RampSweepPointResult> points)
    {
        var undetReasons = new List<string>();
        var jFailReasons = new List<string>();
        var disagree = new List<string>();

        foreach (var pt in points)
        {
            string at = $"{pt.SetpointC:0} °C（夹头 {pt.ClampC:0}）";
            if (!pt.FieldValid) { undetReasons.Add($"{at}：{pt.FieldInvalidReason}"); continue; }

            if (pt.TubeJDesignUndetermined)
                undetReasons.Add($"{at}：设计电流下的管 J 算不出来（整线判据「① 升温」判不了）");
            else if (!pt.TubeJDesignOk)
                jFailReasons.Add($"{at} 管 J（设计电流 = 升温全程峰值）= {pt.TubeJDesignAPerMm2:0.00} > {pt.TubeJDesignLimit:0.#}");

            foreach (var s in pt.Segs)
            {
                if (double.IsNaN(s.TubeJAPerMm2)) undetReasons.Add($"{at} 段「{s.Name}」：实际电流下的管 J 算不出来");
                else if (!s.TubeJOk)
                    jFailReasons.Add($"{at} 段「{s.Name}」管 J（该点实际电流 {s.CurrentA:0} A）= {s.TubeJAPerMm2:0.00} > {s.TubeJLimit:0.#}");
            }

            // 决 103：本点的两条热稳定（改回口径 StabChecked = false，一句不加 ⇒ 逐位同改前）
            if (pt.StabChecked)
            {
                if (pt.StabUndetermined)
                    undetReasons.Add($"{at}：热稳定判不了（整片 {pt.FlangeStabMargin:0.###}、局部 {pt.LocalStabMargin:0.###}）");
                else if (!pt.StabOk)
                    jFailReasons.Add($"{at} 热稳定 < 1：整片 {pt.FlangeStabMargin:0.###}（{pt.FlangeStabWhere}）、局部 {pt.LocalStabMargin:0.###}（{pt.LocalStabWhere}）");
            }

            foreach (var f in pt.Flanges)
            {
                var v = f.SectionJ;
                if (f.SectionJUndetermined)
                {
                    undetReasons.Add($"{at} 片「{f.Name}」：截面 J 算不出来（"
                        + (double.IsNaN(v.DesignJ) ? "设计电流那条" : $"实际电流那条：{v.ActualWhere}") + "）");
                    continue;
                }
                if (!v.DesignOk)
                    jFailReasons.Add($"{at} 片「{f.Name}」截面 J（设计电流 = 升温全程峰值）= {v.DesignJ:0.00} > {v.Limit:0.#}（{v.DesignWhere}）");
                if (v.ActualUsed && !v.ActualOk)
                    jFailReasons.Add($"{at} 片「{f.Name}」截面 J（该点实际电流 {v.ActualCurrentA:0} A）= {v.ActualJ:0.00} > {v.Limit:0.#}（{v.ActualWhere}）");
                if (v.Disagree)
                    disagree.Add($"{at} 片「{f.Name}」：设计电流那条{(v.DesignOk ? "过" : "不过")}（{v.DesignJ:0.00}）、"
                        + $"该点实际电流那条{(v.ActualOk ? "过" : "不过")}（{v.ActualJ:0.00}，{v.ActualCurrentA:0} A）—— 限值 {v.Limit:0.#}");
            }
        }

        if (undetReasons.Count > 0)
            return ("判不了", "判不了的点：\n" + string.Join("\n", undetReasons)
                   + (jFailReasons.Count > 0 ? "\n另有已经看得出超限的点：\n" + string.Join("\n", jFailReasons) : ""),
                   disagree.ToArray());
        bool stab = points.Any(pt => pt.StabChecked);   // 决 103：判了热稳定才改措辞（改回口径原句逐字不变）
        if (jFailReasons.Count > 0)
            return ("不过", (stab ? "J 超限或热稳定 < 1 的点：\n" : "J 超限的点：\n") + string.Join("\n", jFailReasons), disagree.ToArray());
        return ("过", $"{points.Count} 个设定点全部场有效；管 J 与截面 J 在**设计电流**与**该点实际电流**两条上都不超限。"
                    + (stab ? "局部热稳定（全格精算）与整片热稳定都 ≥ 1。" : ""),
                disagree.ToArray());
    }

    /// <summary>
    /// 对一个已定型的设计跑升温全程。
    /// <paramref name="spec"/> 已 Fit 的设计；
    /// <paramref name="inputs"/> 参数表；
    /// <paramref name="options"/> 轨迹等选项。
    /// </summary>
    public static RampSweepResult Run(DesignSpec spec, DesignInputs inputs, RampSweepOptions? options = null,
                                      IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var opt = options ?? new RampSweepOptions();
        var pts = new List<RampSweepPointResult>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (double tSet in opt.TrajectoryC)
        {
            cancel.ThrowIfCancellationRequested();
            pts.Add(RunOnePoint(spec, inputs, tSet, spec.ClampTempC, opt, progress, cancel));
            if (opt.RunClampAlt && tSet >= opt.ClampAltFromC)
                pts.Add(RunOnePoint(spec, inputs, tSet, opt.ClampAltC, opt, progress, cancel));
        }

        sw.Stop();

        // ── 门结论（唯一实现在 Aggregate，门与生产共用）──
        var (verdict, detail, disagree) = Aggregate(pts);

        var summary = $"升温全程（{opt.TrajectoryC[0]:0}–{opt.TrajectoryC[^1]:0} °C，{pts.Count} 点）：{verdict}。{detail.Split('\n')[0]}";

        return new RampSweepResult
        {
            Points = pts.ToArray(),
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
            Verdict = verdict,
            VerdictDetail = detail,
            Summary = summary,
            DisagreeNotes = disagree,
            TAssemblyC = opt.TAssemblyC,   // 2026-09-18 Opus 5：伸长表要印「相对什么、按哪个牌号」，从这一跑实际用的选项带出
            Grade = opt.Grade,
        };
    }

    /// <summary>对一个设定点跑整线解并提取膨胀信息。</summary>
    private static RampSweepPointResult RunOnePoint(DesignSpec spec, DesignInputs inputs,
        double tSetC, double clampC, RampSweepOptions opt,
        IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report($"升温全程：设定点 {tSetC:0} °C，夹头 {clampC:0} °C …");
        var d = spec.Clone();
        d.ClampTempC = clampC;
        var lc = d.BuildCase(inputs, checkRamp: false, emptyTube: true,
                             emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        // ★ R48 L（2026-09-17，Opus 5）：网格口径**跟着调用方**走 —— 与求根／判决同一张网格。
        //   配方不在这里另写一份，原样调 Solver.ApplyCaseMesh（全仓唯一那份造算例网格的配方）。
        //   opt.Mesh 为 null（默认）⇒ 一行不动，= 改前行为。
        if (opt.Mesh is not null) Solver.ApplyCaseMesh(lc, opt.Mesh);
        double rampTargetBefore = lc.RampTargetC;
        lc.SetpointC = Enumerable.Repeat(tSetC, lc.SetpointC.Length).ToArray();
        // RampTargetC 不许被改（它喂设计电流与截面 J）
        if (Math.Abs(lc.RampTargetC - rampTargetBefore) > 1e-9)
            throw new InvalidOperationException("RampTargetC 被篡改");

        LineResult res;
        string runErr = "";
        try { res = LineRunner.Run(lc, progress, cancel); }
        catch (Exception ex) { res = new LineResult { Ok = false, Message = ex.Message }; runErr = ex.GetType().Name; }

        // 场有效？
        string fieldBad = !res.Ok ? $"Ok=false（{res.Message}{(runErr.Length > 0 ? " / " + runErr : "")}）"
                        : !res.Converged ? $"外层耦合未收敛（剩余误差估计 {res.CoupleRemainK:0.000} K）"
                        : res.OverMelt ? "越过铂熔点" : "";
        bool fieldValid = fieldBad.Length == 0;

        double tubeJLimit = lc.Base.TubeJLimitAPerMm2;   // 决 103（2026-09-24「J < 11」）：卡交付的管 J 限值（改回口径 = 许用 12，逐位同改前）
        double sectionJLimit = SectionSizing.JCheckOf(lc.JDesignAPerMm2);

        // ── 设计电流那条管 J（整线判据「① 升温」算的，原样带出）──
        //   判定不在这里重写，读判据自己的 Ok／Undetermined（它是 !Clipped，即「升温所需电流没被管 J 许用截住」）
        var rampChk = res.Checks.FirstOrDefault(ck => ck.Name == LineResult.Key.Ramp);
        bool rampUndet = rampChk is null || rampChk.Undetermined || double.IsNaN(rampChk.Actual);
        bool rampOk = rampChk is not null && rampChk.Ok && !rampUndet;
        double tubeJDesign = rampChk?.Actual ?? double.NaN;
        double tubeJDesignLimit = rampChk?.Limit ?? tubeJLimit;

        // ── 逐段 ──
        var segs = new List<RampSegInfo>();
        for (int i = 0; i < res.Segments.Length; i++)
        {
            var s = res.Segments[i];
            // ★ 2026-09-17 Opus 5：管 J 不在这里另算一份截面积 —— 读整线解自己算的那个
            //   （SegmentOut.TubeJAPerMm2 = 该段实际电流 ÷ 管截面）。此前这里手抄了一遍环形面积公式。
            double tubeJ = s.TubeJAPerMm2;

            // 管段总伸长（相对 T装）
            double totalElong = double.NaN;
            var covA = ExpansionCoverage.InRange;
            if (s.X.Length >= 3 && s.TMetal.Length == s.X.Length)
            {
                var gAsm = TubeElongation.StrainDiff(s.TMetal, opt.TAssemblyC, opt.Grade, out covA, out _);
                totalElong = TubeElongation.Integrate(s.X, gAsm, 0, s.X.Length - 1);
            }

            segs.Add(new RampSegInfo
            {
                Name = s.Name,
                CurrentA = s.CurrentA,
                TubeJAPerMm2 = tubeJ,
                TubeJLimit = tubeJLimit,
                TubeJOk = fieldValid && !double.IsNaN(tubeJ) && tubeJ <= tubeJLimit,
                TotalElongMm = totalElong,
                ElongCoverage = covA,
            });
        }

        // ── 逐片 ──
        // 判据几何走 LineCase.PlatesForJudge（与整线截面 J 判据同一份选择规则）
        var platesJ = lc.PlatesForJudge();
        var flanges = new List<RampFlangeInfo>();
        for (int j = 0; j < res.Flanges.Length; j++)
        {
            var fo = res.Flanges[j];
            var plate = lc.FlangePlates.Length > 0
                ? lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)]
                : null;
            var plateJ = platesJ.Length > 0 ? platesJ[Math.Min(j, platesJ.Length - 1)] : null;

            // ★ 两条并列：设计电流那条（判据算好的）与**该点实际电流**那条
            var sj = fieldValid
                   ? JudgeSectionJ(plateJ, fo.SectionJAPerMm2, fo.SectionJWhere,
                                   fo.CurrentA, lc.Base.BusbarClampLengthMm, sectionJLimit, opt.SectionJFromActualCurrent)
                   : new SectionJVerdict(fo.SectionJAPerMm2, fo.SectionJWhere, false,
                                         fo.CurrentA, double.NaN, "场无效", false,
                                         sectionJLimit, true, false, false, opt.SectionJFromActualCurrent);

            var fi = new RampFlangeInfo
            {
                Name = fo.Name,
                SectionJ = sj,
                SectionJLimit = sectionJLimit,
                SectionJOk = fieldValid && sj.Ok,
                SectionJUndetermined = !fieldValid || sj.Undetermined,
                TRootMinusTTabMeanK = double.NaN,
                TabDisplacementMm = double.NaN,
                TipTotalDisplacementMm = double.NaN,
                DiscRadialMm = double.NaN,
                ClampElongMm = double.NaN,
                ReportCoverage = ExpansionCoverage.InRange,
                IsExtrapolated = tSetC > 1000,
            };

            // 膨胀报告量：需要 mesh 和 plate
            if (fieldValid && fo.Mesh is not null && fo.TField.Length == fo.Mesh.CellCount && plate is not null)
            {
                ComputeFlangeExpansion(fo, plate, lc, opt, fi);
            }

            flanges.Add(fi);
        }

        // ── 决 103：升温期也卡热稳定（≥ 1；读本点整线判据表那两行的 Ok／判不了 —— 判法不在这里另写一份）。改回口径不判（逐位同改前）。
        bool stabChecked = lc.RuleSet != CriteriaRuleSet.决103前;
        var fsC = res.Checks.FirstOrDefault(ck => ck.Name.StartsWith(LineResult.Key.FlangeStab, StringComparison.Ordinal));
        var lsC = res.Checks.FirstOrDefault(ck => ck.Name.StartsWith(LineResult.Key.LocalStab, StringComparison.Ordinal));
        bool stabUndet = !fieldValid || fsC is null || lsC is null || fsC.Undetermined || lsC.Undetermined
                      || double.IsNaN(fsC.Actual) || double.IsNaN(lsC.Actual);
        return new RampSweepPointResult
        {
            SetpointC = tSetC,
            ClampC = clampC,
            LineRes = res,
            FieldValid = fieldValid,
            FieldInvalidReason = fieldBad,
            TubeJDesignAPerMm2 = tubeJDesign,
            TubeJDesignLimit = tubeJDesignLimit,
            TubeJDesignOk = fieldValid && rampOk,
            TubeJDesignUndetermined = !fieldValid || rampUndet,
            Segs = segs.ToArray(),
            Flanges = flanges.ToArray(),
            StabChecked = stabChecked,
            FlangeStabMargin = fsC?.Actual ?? double.NaN, LocalStabMargin = lsC?.Actual ?? double.NaN,
            FlangeStabWhere = fsC?.Where ?? "", LocalStabWhere = lsC?.Where ?? "",
            StabUndetermined = stabChecked && stabUndet,
            StabOk = stabChecked && !stabUndet && fsC!.Ok && lsC!.Ok,
        };
    }

    /// <summary>计算一片法兰的膨胀报告量。</summary>
    private static void ComputeFlangeExpansion(FlangeOut fo, FlangePlate plate, LineCase lc,
        RampSweepOptions opt, RampFlangeInfo fi)
    {
        var mesh = fo.Mesh!;
        var tF = fo.TField;
        double R = plate.DiscRadiusMm;
        double aOuter = lc.TubeIdMm * 0.5 + lc.WallMm;
        double xTan = plate.Tangent().X;
        double xTip = plate.TabTipXMm;
        double clampLen = lc.Base.BusbarClampLengthMm;
        double xClampEntry = xTip + clampLen;

        var cov = ExpansionCoverage.InRange; string covNote = "";

        // ε(T管根)
        double eRoot = FlangeExpansionCalc.Strain(opt.Grade, fo.TRootC, ref cov, ref covNote);

        // 廓线
        var cols = FlangeExpansionCalc.Columns(mesh, tF);

        // 舌板平均温度
        double tTabMean = FlangeExpansionCalc.MeanTempOver(cols, xClampEntry, xTan);
        fi.TRootMinusTTabMeanK = fo.TRootC - tTabMean;

        // Q2 = 舌板长度方向的膨胀差
        fi.TabDisplacementMm = FlangeExpansionCalc.Integrate(cols, xClampEntry, xTan,
            t => FlangeExpansionCalc.Strain(opt.Grade, t, ref cov, ref covNote) - eRoot);

        // 报告量覆盖单独记（含 ε(T装)）
        var covRep = cov; string covRepNote = covNote;
        double eAsm = FlangeExpansionCalc.Strain(opt.Grade, opt.TAssemblyC, ref covRep, ref covRepNote);

        // 圆盘
        var wDisc = FlangeExpansionCalc.DiscWeights(mesh, R, out _);
        var epsCell = new double[mesh.CellCount];
        for (int i = 0; i < mesh.CellCount; i++)
            epsCell[i] = FlangeExpansionCalc.Strain(opt.Grade, tF[i], ref covRep, ref covRepNote);
        double epsDisc = FlangeExpansionCalc.WeightedMean(wDisc, epsCell);

        fi.DiscRadialMm = R * (epsDisc - eAsm);

        // 压接段
        double aClamp = 0, atClamp = 0;
        for (int i = 0; i < mesh.CellCount; i++)
            if (FlangeMesher.InClampSegment(mesh.Centroid[i].X, xTip, clampLen, plate.TwoTabs))
            { aClamp += mesh.Area[i]; atClamp += mesh.Area[i] * tF[i]; }
        double tClampMean = aClamp > 1e-12 ? atClamp / aClamp : double.NaN;
        fi.ClampElongMm = double.IsNaN(tClampMean) ? double.NaN
            : clampLen * (FlangeExpansionCalc.Strain(opt.Grade, tClampMean, ref covRep, ref covRepNote) - eAsm);

        // 舌尖总径向位移
        fi.TipTotalDisplacementMm = FlangeExpansionCalc.Integrate(cols, xTip, 0.0,
            t => FlangeExpansionCalc.Strain(opt.Grade, t, ref covRep, ref covRepNote) - eAsm);

        fi.ReportCoverage = covRep;
    }
}
