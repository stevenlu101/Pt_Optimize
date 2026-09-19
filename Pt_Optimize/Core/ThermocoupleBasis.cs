using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ R48 B（2026-09-14 Opus 5）：**热侧、冷侧两条判据的逐片读数** —— 判据（LineRunner.Judge）、求解器（Solver.PlateSlack）、
/// 安装报告（InstallReport）三处只读这一份，不许各算各的（本仓「同一件事三处来源」栽过四次）。
///
/// ══ 口径（用户 2026-09-14 定；物理把关人核过基准只许用设定值）
/// <code>
///   基准 R_j    = LineSolver.ThermocoupleReferenceC(设定值, j, 段数)   端片取本段设定；共用片取两侧设定的对数平均（开尔文）
///   热侧 H_j    = max(圆盘峰 TDiscMaxC, 舌片区峰 TTabMaxC, 该接头管根两端较热者) − R_j   ≤ LineCase.HotOverTcMaxK
///   冷侧 C_j    = R_j − 该接头管根两端较冷者                                        ≤ LineCase.ColdUnderTcMaxK
/// </code>
/// 接头两端：第 j 片贴「段 j 的首端（A 端，TRootAC）」与「段 j−1 的末端（B 端，TRootBC）」；端片只有一侧。
/// 映射只有一份：<see cref="Solver.EndsOf"/>（SolverPerPlateTests 钉着它不重不漏）。
///
/// ══ 为什么基准不用模型的交界管温
/// 模型算的无法兰交界管温（<see cref="SegmentOut.BaseTRootAC"/>／<see cref="SegmentOut.BaseTRootBC"/>）随管保温、段长等设计变量挪动，
/// 拿它当基准等于让靶跟着箭跑；而热偶读数是现场**真正看得到**的那个数。模型交界温度只**并列**在说明里作对照，
/// 与基准差超过 <see cref="ModelGapNoteK"/> 时写明。
///
/// ══ 判不了
/// 任何一个输入是 NaN（圆盘区／舌片区没有单元、管根温度算不出、设定值缺）⇒ 这一片这一侧**判不了**，
/// 原因写进 <see cref="Joint.HotBlind"/>／<see cref="Joint.ColdBlind"/>。判据那边照「任何一片判不了 ⇒ 整条判不了」处理。
/// </summary>
public static class ThermocoupleBasis
{
    /// <summary>模型算的无法兰交界管温与热偶读数基准差多少 ℃ 以上要在说明里写明（2026-09-14 定：&gt; 1 ℃）。</summary>
    public const double ModelGapNoteK = 1.0;

    /// <summary>一片法兰（一个接头）的热偶基准读数。只装数，不做判定（判定在 LineRunner.Judge，限值只从 LineCase 读）。</summary>
    public sealed class Joint
    {
        public int Plate;
        public string Name = "";
        /// <summary>热偶读数基准 °C。</summary>
        public double RefC = double.NaN;
        /// <summary>基准怎么来的（给人看，不带判据代号）。</summary>
        public string RefHow = "";
        public double DiscPeakC = double.NaN, TabPeakC = double.NaN;
        /// <summary>该接头管根两端较热者／较冷者 °C 与各自是哪一端。</summary>
        public double RootHotC = double.NaN, RootColdC = double.NaN;
        public string RootHotWhere = "", RootColdWhere = "";
        /// <summary>两端逐个列出，如「HC1 末端 1116.2／HC2 首端 1115.9 °C」。</summary>
        public string RootEnds = "";
        /// <summary>热侧取的最热那一点 °C 与它是谁（圆盘峰／舌片区峰／管根某端）。</summary>
        public double HottestC = double.NaN;
        public string HottestWhat = "";
        /// <summary>模型算的**无法兰**交界管温 °C（接头各端基线的平均）；没有基线时 NaN。只作对照。</summary>
        public double ModelJointC = double.NaN;
        /// <summary>非空 = 这一侧判不了，写原因。</summary>
        public string HotBlind = "", ColdBlind = "";

        /// <summary>热侧 H_j K（判不了时 NaN）。</summary>
        public double HotK => HotBlind.Length > 0 ? double.NaN : HottestC - RefC;
        /// <summary>冷侧 C_j K（判不了时 NaN）。</summary>
        public double ColdK => ColdBlind.Length > 0 ? double.NaN : RefC - RootColdC;
        /// <summary>模型交界温度 − 基准 K（NaN = 没有基线）。</summary>
        public double ModelMinusRefK => ModelJointC - RefC;
    }

    /// <summary>第 j 片的读数（从一次整线解里取）。</summary>
    public static Joint At(LineResult r, int j) => At(r.Segments, r.Flanges, j);

    /// <summary>
    /// 第 j 片的读数。<paramref name="flanges"/> 可以短于 j+1（只用得到冷侧时），那样热侧判不了。
    /// ⚠ 基准只用 <see cref="SegmentOut.SetpointC"/>（= LineCase.SetpointC 原值），不用任何模型温度。
    /// </summary>
    public static Joint At(SegmentOut[] segs, FlangeOut[] flanges, int j)
    {
        int n = segs?.Length ?? 0;
        var t = new Joint { Plate = j };
        if (n < 1 || j < 0 || j > n)
        {
            t.Name = $"片{j}";
            t.HotBlind = t.ColdBlind = $"片号 {j} 不在 0…{n} 之内（段数 {n}）";
            return t;
        }
        t.Name = j < (flanges?.Length ?? 0) && !string.IsNullOrEmpty(flanges![j].Name) ? flanges[j].Name
               : j == 0 ? "入口" : j == n ? "出口" : $"{segs![j - 1].Name}|{segs[j].Name}";

        var sp = segs!.Select(s => s.SetpointC).ToArray();
        t.RefC = LineSolver.ThermocoupleReferenceC(sp, j, n);
        t.RefHow = j == 0 || j == n
            ? $"{segs[j == 0 ? 0 : n - 1].Name} 控温热偶读数 {t.RefC:0.0} °C（端片取本段）"
            : $"{segs[j - 1].Name} {sp[j - 1]:0.0} 与 {segs[j].Name} {sp[j]:0.0} °C 两个热偶读数的对数平均 {t.RefC:0.00} °C（按开尔文算）";

        // ── 接头两端（映射只有一份：Solver.EndsOf）
        var ends = Solver.EndsOf(j, n);
        var parts = new List<string>();
        double hot = double.NegativeInfinity, cold = double.PositiveInfinity;
        bool rootNaN = ends.Length == 0;
        var baseVals = new List<double>();
        bool baseNaN = ends.Length == 0;
        foreach (var (seg, aEnd) in ends)
        {
            var s = segs[seg];
            double v = aEnd ? s.TRootAC : s.TRootBC;
            string where = $"{s.Name} {(aEnd ? "首端" : "末端")}";
            parts.Add($"{where} {v:0.0}");
            if (double.IsNaN(v)) { rootNaN = true; continue; }
            if (v > hot) { hot = v; t.RootHotWhere = where; }
            if (v < cold) { cold = v; t.RootColdWhere = where; }
            double b = aEnd ? s.BaseTRootAC : s.BaseTRootBC;
            if (double.IsNaN(b)) baseNaN = true; else baseVals.Add(b);
        }
        t.RootEnds = string.Join("／", parts) + " °C";
        if (!rootNaN) { t.RootHotC = hot; t.RootColdC = cold; }
        t.ModelJointC = baseNaN || baseVals.Count == 0 ? double.NaN : baseVals.Average();

        if (double.IsNaN(t.RefC)) t.ColdBlind = "设定值缺（热偶读数基准算不出）";
        else if (rootNaN) t.ColdBlind = "管根温度算不出（" + t.RootEnds + "）";

        // ── 热侧：圆盘峰、舌片区峰、管根较热端 三者取最热
        if (j >= (flanges?.Length ?? 0)) t.HotBlind = "没有这一片的法兰场";
        else
        {
            var f = flanges![j];
            t.DiscPeakC = f.TDiscMaxC; t.TabPeakC = f.TTabMaxC;
            var why = new List<string>();
            if (double.IsNaN(t.RefC)) why.Add("设定值缺（热偶读数基准算不出）");
            if (double.IsNaN(f.TDiscMaxC)) why.Add("圆盘区没有单元（圆盘峰算不出）");
            if (double.IsNaN(f.TTabMaxC)) why.Add("舌片区没有单元（舌片区峰算不出）");
            if (rootNaN) why.Add("管根温度算不出");
            if (why.Count > 0) t.HotBlind = string.Join("、", why);
            else
            {
                t.HottestC = f.TDiscMaxC; t.HottestWhat = "圆盘峰";
                if (f.TTabMaxC > t.HottestC) { t.HottestC = f.TTabMaxC; t.HottestWhat = "舌片区峰"; }
                if (t.RootHotC > t.HottestC) { t.HottestC = t.RootHotC; t.HottestWhat = "管根（" + t.RootHotWhere + "）"; }
            }
        }
        return t;
    }

    /// <summary>整线逐片读数（片数 = 段数 + 1）。</summary>
    public static Joint[] All(LineResult r)
        => Enumerable.Range(0, LineSolver.FlangeCount(r.Segments.Length)).Select(j => At(r, j)).ToArray();

    /// <summary>
    /// 「模型算的无法兰交界管温」那半句（判据说明与安装报告共用）。与基准差 &gt; <see cref="ModelGapNoteK"/> ℃ 时写明判定按哪个算。
    /// </summary>
    public static string ModelJointNote(Joint t)
    {
        if (double.IsNaN(t.ModelJointC))
            return "模型算的无法兰交界管温：这次没有无法兰基线，算不出（不影响判定，判定只用热偶读数基准）";
        double d = t.ModelMinusRefK;
        return $"模型算的无法兰交界管温 {t.ModelJointC:0.0} °C，与热偶读数基准差 {d:+0.0;−0.0} ℃"
             + (Math.Abs(d) > ModelGapNoteK
                ? $"（差超过 {ModelGapNoteK:0} ℃ —— 判定按热偶读数算，不按模型交界温度：模型交界温度会随设计变量挪动，只作对照）"
                : "");
    }

    // ── 合并 B/C（2026-09-15 Opus 5）：C 路空管探针用过的三个入口，保留签名、全部转调 B 的唯一实现，不另写式子。
    /// <summary>两个温度（°C）的对数平均 °C（开尔文里取）—— 转调 <see cref="LineSolver.LogMeanC"/>。</summary>
    public static double LogMeanC(double aC, double bC) => LineSolver.LogMeanC(aC, bC);

    /// <summary>第 <paramref name="plate"/> 片的热偶读数基准 °C；片号越界返回 NaN —— 转调 <see cref="LineSolver.ThermocoupleReferenceC"/>。</summary>
    public static double ReferenceC(double[] setpointC, int plate)
        => setpointC is null || setpointC.Length == 0 || plate < 0 || plate > setpointC.Length
           ? double.NaN : LineSolver.ThermocoupleReferenceC(setpointC, plate, setpointC.Length);

    /// <summary>该片圆盘峰与舌区峰取大 °C。⚠ 不含管根 —— 判据的热侧用 <see cref="At(LineResult,int)"/>（含管根），这里只给探针对照。</summary>
    public static double HottestPtC(FlangeOut f) => Math.Max(f.TDiscMaxC, f.TTabMaxC);

    /// <summary>「圆盘峰与舌区峰取大 − 热偶读数基准」K（探针对照用，不是判据；判据热侧含管根，见 <see cref="At(LineResult,int)"/>）。</summary>
    public static double HottestMinusReferenceK(LineResult r, int plate)
    {
        if (r?.Flanges is null || plate < 0 || plate >= r.Flanges.Length || r.Segments is null) return double.NaN;
        var sp = new double[r.Segments.Length];
        for (int k = 0; k < sp.Length; k++) sp[k] = r.Segments[k].SetpointC;
        return HottestPtC(r.Flanges[plate]) - ReferenceC(sp, plate);
    }
}
