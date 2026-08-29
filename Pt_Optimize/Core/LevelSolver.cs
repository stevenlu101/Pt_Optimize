using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **逐级求解器**（`.3dm` 路）—— 把 <see cref="FlangeAutoSizer.SolveByLevel"/> 的
/// 「搜索」换成「求根」，与 <see cref="Solver"/>（解析路）同一套论证。
///
/// ══ 为什么要换（算法普查 A⑫，2026-08-28 查出）
///
/// `SolveByLevel` 把我在解析路上拆掉的东西**一条不缺地全有**：
/// <code>
///   scale[j][m] *= adj[m];                                   // 增量行走
///   st = Clamp(Damping * e / SensitivityK, -0.30, 0.30);     // 比例控制器
///      SensitivityK = 800.0   Damping = 0.6   步长上限 ±0.30 // 三个都是挑出来的数
///   if (last.Converged &amp;&amp; worstOver &lt; 15) break;             // 启发式停机（15 无出处）
///   cur.Damping *= 0.5;                                      // 遇振荡减半，也是启发式
/// </code>
/// 起点 <c>scale = 1.0</c>（照图纸原样）**不算合成种子**（图纸是真实输入，合规），
/// 但结果**仍然路径相关**：<b>同一个零件的两张不同画法的图，会优化出两个不同的答案。</b>
/// 种子问题戴了一顶合法的帽子。
///
/// ══ 换成什么
///
/// 与 <see cref="Solver"/> 完全同构的最优性论证：
///
///   目标 min 铂重；铂重对**每一级的厚度**都严格递增
///   ⇒ 最小铂重解落在「每一级都取到仍可行的最小厚度」那一点
///   ⇒ **从制造下界出发、只增不减、每步二分求根** ⇒ 解与初值无关
///
/// ★ 起点是 <b>制造下界</b>（焊接屈曲 / 烧穿），不是图纸原样 ——
///   这一条正是「同一零件两张图给两个答案」的解药：**图纸只提供轮廓与分级，不提供起点**。
///
/// ══ 两层是**有依据**的，归一化不是
///
/// | 层 | 自由度 | 管哪族约束 |
/// |---|---|---|
/// | 整片标度 | 每片一个 | ②′／③ 这类**整片**量（整片发热总量 ⇒ 从管子抽多少热） |
/// | 逐级厚度 | 每片每级一个 | **局部**过热（单位面积发热 = ρe·K²/t） |
///
/// 两族约束、两族自由度 —— 这个分层保留。
/// **拆掉的是那个「几何平均归一化」**：它存在只是为了让两层别互相打架，
/// 而「只增不减 + 外层每轮重查」已经解决同一个问题（解析路就是这么做的）。
///
/// ══ 前提自检（与 <see cref="Solver"/> 同）
///
/// 二分成立的前提是「该判据对该旋钮单调」。抬到上界仍不变好 ⇒ **报前提不成立并停**，
/// 绝不返回一个看着正常的错数。逐级之后它还多管一件事：
/// 验「第 m 级的过热真的归第 m 级的厚度管」——**这条不假设，每次抬之前实测**。
///
/// ══ 这里**没有**解决的
///   · 轮廓与分级由图纸给定，本类不改（要改轮廓请回 Rhino）。
///   · 尚未做过数值验证 —— <b>写完即用是不许的</b>，见 `LevelSolverTests` 的说明。
/// </summary>
public static class LevelSolver
{
    /// <summary>
    /// 求解结果。字段刻意与 <see cref="SolverResult"/> 对齐 —— 两条路的调用方
    /// 读到的是同一组概念，不必各记一套。
    /// </summary>
    public sealed class Result
    {
        /// <summary>`[片][级]` 的**绝对厚度** mm（解出来的那一点）。</summary>
        public double[][] ThickMm = Array.Empty<double[]>();
        /// <summary>喂回 <see cref="LineCase.LevelScale"/> 的倍数 = 绝对厚度 ÷ 图纸级厚。</summary>
        public double[][] LevelScale = Array.Empty<double[]>();
        public LineResult? Best;
        public bool Feasible;
        public double MassG = double.NaN;
        public string Message = "";
        public readonly List<string> Trace = new();
        /// <summary>场解次数 —— 求根的真实成本，比「轮数」诚实。</summary>
        public int Solves;
        /// <summary>true = 顶到上界／前提不成立 ⇒ **不可行的证明**，不是没搜到。</summary>
        public bool HitBound;
        public string StopWhy = "";
        /// <summary>第二遍（细网格）做了没有。false ⇒ **不可交付**（A⑬）。</summary>
        public bool FineRefined;
        public double FineMmUsed;
    }

    public sealed class Options
    {
        /// <summary>每级厚度上界 mm。只用来判「不可行」与做二分的右端。</summary>
        public double ThickHiMm = 8.0;
        /// <summary>图纸格：解**直接落在**它上面，不是解完再四舍五入。</summary>
        public double QuantMm = 0.01;
        /// <summary>二分停在图纸格的一半上。</summary>
        public double BisectTolMm = 0.005;
        public int BisectMaxIter = 14;
        public int MaxRounds = 40;
        /// <summary>
        /// 局部过热的容差 K：某一级比管根热多少算「不过」。
        /// ⚠ **这是个约定值，不是算出来的** —— 与 `SolveByLevel` 里那个写死的 15 同源。
        ///   它该由现场给（像 ②″ 的 5 K 那样），目前只是把它**显式化**，还没找到出处。
        /// </summary>
        public double LevelOverMaxK = 15.0;
        /// <summary>第二遍求根的细网格 mm。0 = 不做，结果**不可交付**。</summary>
        public double FineMm;
        public double FineRadiusMm;
        public Options Clone() => (Options)MemberwiseClone();
    }

    /// <summary>
    /// 解。<paramref name="levelThicknessMm"/> 只提供**分级结构**（几级、各级原始厚度，
    /// 用来换算 <see cref="LineCase.LevelScale"/>）；**起点不取自它**。
    /// </summary>
    public static Result Solve(LineCase baseCase, double[][] levelThicknessMm, double floorMm,
                               Options? opt = null, IProgress<string>? progress = null,
                               CancellationToken cancel = default)
    {
        if (baseCase is null) throw new ArgumentNullException(nameof(baseCase));
        if (levelThicknessMm is null) throw new ArgumentNullException(nameof(levelThicknessMm));
        if (!(floorMm > 0))
            throw new ArgumentOutOfRangeException(nameof(floorMm),
                "制造下界必须为正 —— 它是起点，不是可选项。没有下界就没有「最小可行点」这回事。");

        opt ??= new Options();
        var res = new Result();
        void Log(string s) { res.Trace.Add(s); progress?.Report(s); }

        int nf = levelThicknessMm.Length;
        // ★ 起点 = **制造下界**，不是图纸原样。
        //   这一条正是「同一零件两张不同画法的图给两个答案」的解药。
        double lo0 = Math.Ceiling(floorMm / opt.QuantMm - 1e-9) * opt.QuantMm;
        var t = new double[nf][];
        for (int j = 0; j < nf; j++)
        {
            t[j] = new double[levelThicknessMm[j].Length];
            for (int m = 0; m < t[j].Length; m++) t[j][m] = lo0;
        }
        res.ThickMm = t;

        Log($"起点 = **制造下界** {lo0:0.00} mm（焊接屈曲/烧穿），**不是图纸原样** —— "
          + "图纸只提供轮廓与分级，不提供起点。");
        Log($"图纸各级厚度（只用来换算 LevelScale）：{Describe(levelThicknessMm)}");

        LineResult? last = null;

        bool Rounds(Options o, string tag)
        {
            Log($"── {tag}" + (o.FineMm > 0 ? $"（细网格 {o.FineMm:0.000} mm）" : "（导航网格）"));
            var inner = o.FineMm > 0
                      ? new ThrottledProgress(progress, 20, $"     · {o.FineMm:0.000} mm ")
                      : null;

            for (int round = 1; round <= o.MaxRounds; round++)
            {
                cancel.ThrowIfCancellationRequested();
                last = Eval(baseCase, levelThicknessMm, t, o, res, cancel, inner);
                if (last is null) { res.StopWhy = "场解不收敛，判不了"; return false; }

                double mass = MassOf(last);
                Log($"第 {round,2} 轮　合计 {mass:0} g　厚度 {Describe(t)}");

                // 逐（片,级）列出过热
                var todo = new List<(int J, int M, double Over)>();
                for (int j = 0; j < nf && j < last.Flanges.Length; j++)
                {
                    var f = last.Flanges[j];
                    if (f.LevelTMaxC.Length != t[j].Length) continue;
                    for (int m = 0; m < t[j].Length; m++)
                    {
                        double tm = f.LevelTMaxC[m];
                        if (double.IsNaN(tm)) continue;          // 该级没被网格覆盖到 —— 不当成过
                        double over = tm - f.TRootC;
                        if (over > o.LevelOverMaxK)
                        {
                            todo.Add((j, m, over));
                            Log($"     片{j} 级{m}：比管根热 {over:0.0} K（限 {o.LevelOverMaxK:0.0}）⇒ 加厚该级");
                        }
                    }
                }

                if (todo.Count == 0)
                {
                    var bad = last.Checks
                        .Where(c => c.Kind != CheckKind.Reference && (!c.Ok || c.Undetermined))
                        .ToList();
                    if (bad.Count == 0)
                    {
                        res.Feasible = true;
                        res.StopWhy = $"第 {round} 轮全过；因为**只往上走过**，这就是最小的可行点";
                        return true;
                    }
                    res.HitBound = true;
                    res.StopWhy = "逐级过热都压住了，但这些判据**没有旋钮能治**（要回 Rhino 改轮廓）："
                                + string.Join("、", bad.Select(c => c.Name));
                    Log("  ✗ " + res.StopWhy);
                    return false;
                }

                foreach (var (j, m, _) in todo.OrderByDescending(x => x.Over))
                {
                    var (ok, why) = RaiseLevel(baseCase, levelThicknessMm, t, o, j, m,
                                               res, Log, cancel, inner);
                    if (!ok) { res.StopWhy = why; res.HitBound = true; Log("  ✗ " + why); return false; }
                }
            }
            return false;
        }

        var nav = opt.Clone(); nav.FineMm = 0; nav.FineRadiusMm = 0;
        bool okNav = Rounds(nav, "第一遍：导航网格上定位");

        if (okNav && opt.FineMm > 0)
        {
            res.Feasible = false; res.StopWhy = ""; res.HitBound = false;
            bool okFine = Rounds(opt, "第二遍：细网格上重新求根（**判据以此为准**）");
            res.FineRefined = true; res.FineMmUsed = opt.FineMm;
            if (!okFine && res.StopWhy.Length == 0)
                res.StopWhy = $"细网格第二遍跑满 {opt.MaxRounds} 轮仍未全过 —— **未收敛**，不作数";
        }
        else if (opt.FineMm <= 0)
            Log("⚠ **没做第二遍**（FineMm = 0）—— 这个解只在导航网格上成立、**不可交付**（A⑬）。");

        if (res.StopWhy.Length == 0)
            res.StopWhy = $"跑满 {opt.MaxRounds} 轮仍未全过 —— 结果**未收敛**，不作数";

        res.LevelScale = ToScale(levelThicknessMm, t);
        res.Best = last;
        if (last is not null && last.Ok) res.MassG = MassOf(last);
        res.Message = res.StopWhy;
        return res;
    }

    /// <summary>
    /// 把第 j 片第 m 级二分抬到「该级不再过热」的最小厚度。
    /// **先验前提**：抬到上界时该级必须变凉，否则报前提不成立。
    /// </summary>
    private static (bool Ok, string Why) RaiseLevel(
        LineCase bc, double[][] lv, double[][] t, Options o, int j, int m,
        Result res, Action<string> Log, CancellationToken cancel, IProgress<string>? inner)
    {
        double lo = t[j][m], hi = o.ThickHiMm;
        string nm = $"片{j} 级{m}";

        double Over(double v)
        {
            t[j][m] = v;
            var r = Eval(bc, lv, t, o, res, cancel, inner);
            if (r is null || j >= r.Flanges.Length) return double.PositiveInfinity;
            var f = r.Flanges[j];
            if (m >= f.LevelTMaxC.Length || double.IsNaN(f.LevelTMaxC[m])) return double.PositiveInfinity;
            return f.LevelTMaxC[m] - f.TRootC;
        }

        if (lo >= hi - 1e-12)
            return (false, $"**{nm} 已在上界 {hi:0.000} mm**，仍过热 ⇒ 这张图纸不可行（是证明，不是搜索失败）");

        double before = Over(lo);
        double after = Over(hi);

        // ★ 前提自检：加厚到底也没让这一级变凉 ⇒ 分派对这一级是错的，**不许假装解出来**
        if (!(after < before - 1e-9))
        {
            t[j][m] = lo;
            return (false,
                $"**分派前提不成立**：{nm} 从 {lo:0.000} 加厚到上界 {hi:0.000} mm，"
              + $"过热 {before:0.0} → {after:0.0} K（**没变凉**）"
              + " ⇒ 加厚这一级压不住它自己的过热，二分不适用");
        }
        if (after > o.LevelOverMaxK)
        {
            t[j][m] = lo;
            return (false, $"**{nm} 加厚到上界 {hi:0.000} mm 仍过热 {after:0.0} K** ⇒ 这张图纸不可行");
        }

        for (int i = 0; i < o.BisectMaxIter && hi - lo > o.BisectTolMm; i++)
        {
            cancel.ThrowIfCancellationRequested();
            double mid = 0.5 * (lo + hi);
            if (Over(mid) <= o.LevelOverMaxK) hi = mid; else lo = mid;
        }

        // 量化在**解之内**：向上取整可证明安全（该级过热对该级厚度递减，前提已验）
        double q = o.QuantMm;
        double snapped = Math.Min(Math.Ceiling(hi / q - 1e-9) * q, o.ThickHiMm);
        t[j][m] = snapped;
        Log($"  ↑ {nm} → {snapped:0.000} mm（二分求根 {hi:0.0000} → 向上对齐到图纸格 {q:0.###}）");
        return (true, "");
    }

    private static LineResult? Eval(LineCase bc, double[][] lv, double[][] t, Options o,
                                    Result res, CancellationToken cancel, IProgress<string>? inner)
    {
        try
        {
            var lc = FlangeAutoSizer.CloneCase(bc);   // 拷贝只有这一份，不另写
            lc.LevelThicknessMm = lv;
            lc.LevelScale = ToScale(lv, t);
            if (o.FineMm > 0)
            {
                lc.MeshFineMm = o.FineMm;
                if (o.FineRadiusMm > 0) lc.MeshFineRadiusMm = o.FineRadiusMm;
            }
            var r = LineRunner.Run(lc, inner, cancel);
            res.Solves++;
            return r.Ok ? r : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { res.Solves++; return null; }
    }

    /// <summary>绝对厚度 → <see cref="LineCase.LevelScale"/> 的倍数。图纸级厚为 0 时给 1（不放大零）。</summary>
    public static double[][] ToScale(double[][] levelThicknessMm, double[][] wantMm)
    {
        var s = new double[levelThicknessMm.Length][];
        for (int j = 0; j < s.Length; j++)
        {
            s[j] = new double[levelThicknessMm[j].Length];
            for (int m = 0; m < s[j].Length; m++)
            {
                double d = levelThicknessMm[j][m];
                s[j][m] = d > 1e-9 && j < wantMm.Length && m < wantMm[j].Length
                        ? wantMm[j][m] / d : 1.0;
            }
        }
        return s;
    }

    private static double MassOf(LineResult r) =>
        r.Segments.Sum(x => x.MassG) + r.Flanges.Sum(f => f.MassG);

    private static string Describe(double[][] v) =>
        string.Join("　", v.Select(a => string.Join("/", a.Select(x => x.ToString("0.00")))));
}
