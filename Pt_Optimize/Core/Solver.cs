using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **求解器**：不搜索，求根。用来替掉 <see cref="Sizer"/> 的「增量行走 + 已访问点集 argmin」。
///
/// ══ 为什么 Sizer 不是第一性原理，而这个是
///
/// Sizer 的结构是「三个旋钮各配一个靶，按误差比例走步，连坏 N 轮就停，
/// 最后在**走过的点**里挑最轻的」。那是**搜索**：答案取决于从哪出发（⇒ 必须有种子）、
/// 步长多大、停机规则怎么定。这三样都没有物理依据，而第一样正是「种子」这个病根。
///
/// 这里换成一个**最优性论证**：
///
///   目标：min 铂重 m
///   实测（`--monotone`，2026-08-28，0.8 mm 壁厚档）：
///     · m 对 板厚   严格递增（0.6→4.0 mm 时 2867→5209 g）
///     · m 对 舌保温 **完全不变**（0.3→8 mm 全程 3547 g）⇒ 保温是**免费**变量
///     · m 对 环倍率 严格递增（1.0→2.5 时 3547→3684 g）
///
///   ⇒ 最小铂重解**必然**落在「每个花铂的旋钮都取到仍可行的最小值」那一点。
///     这不是启发式，是「单调目标 + 单调约束」的直接推论。
///
///   那一点怎么到达？——**从约束盒的下角出发，只许往上走**：
///
///     板厚   t 起于 t_lo = <see cref="DesignSpec.DiscFloorMm"/>（焊接屈曲 / 烧穿下界）
///     舌保温 s 起于 s_lo = 0.3 mm（裸舌）
///     环倍率 r 起于 r_lo = 1.0（无台阶）
///
///   ★ 这三个下界**不是种子**：它们是约束集自己的角点，每一个都有第一性原理来源
///     （屈曲、烧穿、裸露、无台阶）。种子是「随便挑一个内点」，这里没有那个动作。
///
///     每轮：对每条**违反**的判据，把**它自己的旋钮**二分抬到刚好不违反，一点不多；
///     任何旋钮**只增不减**。
///     ⇒ 序列单调、有上界 ⇒ 必收敛；起点固定在盒的下角
///     ⇒ 收敛到的是**最小的可行点**（单调映射的最小不动点）。
///
///   ⇒ **解唯一、与传进来的旋钮值无关、且就是最小铂重解。**
///     `SolverIsSeedFreeTests` 拿两组差得离谱的入参跑出同一个答案，把这句话钉住。
///
/// ══ 求解器**检查自己的前提**（三条铁律第 ③ 条用在自己身上）
///
/// 二分求根成立的前提是「该判据对该旋钮单调」。前提不成立时**绝不能**假装解出来了：
/// <see cref="RaiseUntil"/> 每次都先测一次「旋钮顶到上界时判据有没有变好」，
/// 没变好就**报前提不成立**并停，而不是返回一个看着正常的错数。
/// —— 环倍率→②″ 那一支就是这么被抓出来的：实测 d②″/d倍率 = **+0.056**（文档写 −1.4），
///    方向相反，即「加环压不住 ②″，只是花铂」。
///
/// ══ 这里**没有**解决的（照实说，别当成已完成）
///   · 四片各自的厚度还没分开解：现在四片同步抬。分开解要先证 Jacobian 对角占优。
///   （量化 A⑤ 已拆：二分完**向上对齐到图纸格**，解直接落在可制造集合上，
///     不再存在「解完再四舍五入把判据四舍五入掉」这件事。）
///   · 形状（盘径 / 舌宽）仍归 ShapeSearchPlan 的固定步长模式搜索 —— A 类第 ⑥ 条还没拆。
/// </summary>
public static class Solver
{
    /// <summary>三个可解旋钮。每个都只往上走。</summary>
    public enum Knob { Thick, Insul, Ring }

    public static string KnobName(Knob k) => k switch
    {
        Knob.Thick => "板厚",
        Knob.Insul => "舌保温",
        Knob.Ring  => "环倍率",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>
    /// 判据 → 抬哪个旋钮。**这张表是可证伪的**：<see cref="RaiseUntil"/> 每次都验一遍
    /// 「抬它到底有没有用」，没用就报前提不成立。旧 Sizer 的分派表没有这道验。
    /// </summary>
    public static readonly (string Key, Knob Knob)[] Allocation =
    {
        (LineResult.Key.NetFlux,   Knob.Thick),  // ②′ 管孔净流入：对板厚递增（实测）
        (LineResult.Key.FlangeDip, Knob.Insul),  // ③  法兰增量温降：对舌保温递减（实测）
        (LineResult.Key.DiscTemp,  Knob.Ring),   // ②″ 圆盘区最高温：**方向存疑**，见类注释
    };

    public static SolverResult Solve(DesignSpec geometry, DesignInputs baseIn, SolverOptions opt,
                                     IProgress<string>? progress = null,
                                     CancellationToken cancel = default)
    {
        if (geometry is null) throw new ArgumentNullException(nameof(geometry));
        if (baseIn   is null) throw new ArgumentNullException(nameof(baseIn));
        if (opt      is null) throw new ArgumentNullException(nameof(opt));

        var res = new SolverResult();
        void Log(string s) { res.Trace.Add(s); progress?.Report(s); }

        // 只留几何与工况；**五个旋钮一律丢掉**，改用盒的下角。
        var d = geometry.Clone();
        d.Invalid = ""; d.InvalidChecks = Array.Empty<string>();

        // 下界也得落在图纸格子上（向**上**对齐：向下会掉到屈曲/烧穿下界以下）
        double tLo = Math.Ceiling(d.DiscFloorMm(baseIn) / opt.QuantThickMm - 1e-9) * opt.QuantThickMm;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = tLo;
            d.TabInsulMm[j] = opt.InsLoMm;
            d.RingMul[j]    = opt.RingLo;
        }

        Log($"起点 = **约束盒的下角**（不是种子）：板厚 {tLo:0.00} mm（焊接屈曲/烧穿下界）／" +
            $"舌保温 {opt.InsLoMm:0.00} mm（裸舌）／环倍率 {opt.RingLo:0.00}（无台阶）");
        Log($"传进来的旋钮值**一个都没用**（板厚 {string.Join("/", geometry.TabThickMm.Select(x => x.ToString("0.00")))} 被丢弃）—— " +
            "这就是「与初值无关」的实现方式。");

        LineResult? last = null;

        for (int round = 1; round <= opt.MaxRounds; round++)
        {
            cancel.ThrowIfCancellationRequested();
            last = Eval(d, baseIn, res, cancel);
            if (last is null) { res.StopWhy = "场解不收敛，判不了"; break; }

            var bad = Violations(last).ToList();
            double mass = MassOf(last);
            Log($"第 {round,2} 轮　板厚 {d.TabThickMm[0]:0.000}　舌保温 {d.TabInsulMm[0]:0.000}　" +
                $"环倍率 {d.RingMul[0]:0.000}　合计 {mass:0} g　" +
                $"违反 {(bad.Count == 0 ? "无" : string.Join("、", bad.Select(c => c.Name)))}");

            if (bad.Count == 0)
            {
                res.Feasible = true;
                res.StopWhy = $"第 {round} 轮全过；因为**只往上走过**，这就是最小的可行点";
                break;
            }

            bool moved = false;
            foreach (var c in bad)
            {
                var hit = Allocation.FirstOrDefault(a => c.Name.StartsWith(a.Key, StringComparison.Ordinal));
                if (hit.Key is null)
                {
                    res.StopWhy = $"判据「{c.Name}」没有配旋钮 —— **抬不动它**，不是解不出来";
                    res.HitBound = true;
                    Log("  ✗ " + res.StopWhy);
                    return Finish(res, d, last, baseIn, cancel);
                }

                var (ok, why) = RaiseUntil(d, baseIn, opt, hit.Knob, hit.Key, res, Log, cancel);
                if (!ok)
                {
                    res.StopWhy = why;
                    res.HitBound = true;
                    Log("  ✗ " + why);
                    return Finish(res, d, last, baseIn, cancel);
                }
                moved = true;
            }

            if (!moved) { res.StopWhy = "有违反但没有旋钮可抬"; res.HitBound = true; break; }
        }

        if (res.StopWhy.Length == 0)
            res.StopWhy = $"跑满 {opt.MaxRounds} 轮仍未全过 —— 结果**未收敛**，不作数";

        return Finish(res, d, last, baseIn, cancel);
    }

    /// <summary>
    /// 把 <paramref name="knob"/> 二分抬到「<paramref name="key"/> 刚好不违反」的最小值。
    /// **先验前提**：旋钮顶到上界时判据必须变好，否则报前提不成立。
    /// </summary>
    private static (bool Ok, string Why) RaiseUntil(
        DesignSpec d, DesignInputs baseIn, SolverOptions opt, Knob knob, string key,
        SolverResult res, Action<string> Log, CancellationToken cancel)
    {
        double lo = Get(d, knob);                 // 当前值：已知**违反**
        double hi = HiOf(opt, knob);
        string nm = KnobName(knob);

        if (lo >= hi - 1e-12)
            return (false, $"**{nm} 已在上界 {hi:0.000}**，判据「{key}」仍不过 ⇒ 这组输入不可行（是证明，不是搜索失败）");

        double before = Slack(Eval(d, baseIn, res, cancel), key);

        Set(d, knob, hi);
        double after = Slack(Eval(d, baseIn, res, cancel), key);

        // ★ 前提自检：抬到底也没让判据变好 ⇒ 这条分派是错的，**不许假装解出来**
        if (!(after > before + 1e-9))
        {
            Set(d, knob, lo);
            return (false,
                $"**分派前提不成立**：{nm} 从 {lo:0.000} 抬到上界 {hi:0.000}，" +
                $"「{key}」的裕度 {before:+0.000;-0.000} → {after:+0.000;-0.000}（**没变好**）" +
                " ⇒ 这个旋钮压不住这条判据，二分不适用");
        }

        if (after < 0)
        {
            Set(d, knob, lo);
            return (false, $"**{nm} 抬到上界 {hi:0.000} 仍不过**「{key}」⇒ 这组输入不可行");
        }

        // 二分：找「刚好不违反」的最小值。不变式：lo 违反、hi 不违反。
        for (int i = 0; i < opt.BisectMaxIter && hi - lo > TolOf(opt, knob); i++)
        {
            cancel.ThrowIfCancellationRequested();
            double mid = 0.5 * (lo + hi);
            Set(d, knob, mid);
            if (Slack(Eval(d, baseIn, res, cancel), key) >= 0) hi = mid; else lo = mid;
        }

        // ★ 量化在**解之内**，不在解之后（算法普查 A⑤）。
        //   往**上**取整是可证明安全的：本判据对本旋钮递增（前提已在上面验过），
        //   抬到格点只会让它更过，不会翻回去。
        //   抬高可能让**别的**判据变差 —— 那由外层下一轮再抬它自己的旋钮补上，
        //   「只增不减」的不变式不受影响。
        //   ∴ 解出来就**直接落在图纸格子上**，不需要事后四舍五入——
        //   而正是那一步曾把②′与③两条同时翻掉过。
        double q = QuantOf(opt, knob);
        double snapped = Math.Min(Math.Ceiling(hi / q - 1e-9) * q, HiOf(opt, knob));
        Set(d, knob, snapped);
        Log($"  ↑ {nm} → {snapped:0.000}（二分求根 {hi:0.0000} → 向上对齐到图纸格 {q:0.###}）");
        return (true, "");
    }

    private static SolverResult Finish(SolverResult res, DesignSpec d, LineResult? last,
                                       DesignInputs baseIn, CancellationToken cancel)
    {
        res.Design = d;
        // 终局复核带上升温 ①（导航时为省时关掉的那条）
        try { res.Best = LineRunner.Run(d.BuildCase(baseIn, checkRamp: true), null, cancel); res.Solves++; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { res.Message = "终局复核失败：" + ex.Message; res.Best = last; }

        if (res.Best is not null && res.Best.Ok)
        {
            res.MassG = MassOf(res.Best);
            res.Feasible = res.Best.AllOk;
        }
        else res.Feasible = false;

        if (res.Message.Length == 0) res.Message = res.StopWhy;
        return res;
    }

    private static LineResult? Eval(DesignSpec d, DesignInputs baseIn, SolverResult res, CancellationToken cancel)
    {
        try
        {
            var r = LineRunner.Run(d.BuildCase(baseIn, checkRamp: false), null, cancel);
            res.Solves++;
            return r.Ok ? r : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { res.Solves++; return null; }
    }

    /// <summary>违反的硬判据。**判不了 = 不算过**（三条铁律第 ③ 条）。</summary>
    private static IEnumerable<ConstraintOut> Violations(LineResult r) =>
        r.Checks.Where(c => c.Kind != CheckKind.Reference && (!c.Ok || c.Undetermined));

    /// <summary>
    /// 判据裕度，**正 = 过**。方向由 <see cref="ConstraintOut.LessIsBetter"/> 决定 ——
    /// 弄反了会把「越限」当成「有余量」，是本仓库出过的错。
    /// </summary>
    private static double Slack(LineResult? r, string key)
    {
        if (r is null) return double.NegativeInfinity;
        var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
        if (c is null || c.Undetermined || double.IsNaN(c.Actual)) return double.NegativeInfinity;
        return c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;
    }

    private static double MassOf(LineResult r) =>
        r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);

    private static double Get(DesignSpec d, Knob k) => k switch
    {
        Knob.Thick => d.TabThickMm[0],
        Knob.Insul => d.TabInsulMm[0],
        Knob.Ring  => d.RingMul[0],
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static void Set(DesignSpec d, Knob k, double v)
    {
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            switch (k)
            {
                case Knob.Thick: d.TabThickMm[j] = v; break;
                case Knob.Insul: d.TabInsulMm[j] = v; break;
                case Knob.Ring:  d.RingMul[j]    = v; break;
                default: throw new ArgumentOutOfRangeException(nameof(k));
            }
        }
    }

    private static double HiOf(SolverOptions o, Knob k) => k switch
    {
        Knob.Thick => o.ThickHiMm,
        Knob.Insul => o.InsHiMm,
        Knob.Ring  => o.RingHi,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>图纸格。求解**直接落在这张格子上**，不是解完再四舍五入。</summary>
    private static double QuantOf(SolverOptions o, Knob k) => k switch
    {
        Knob.Thick => o.QuantThickMm,
        Knob.Insul => o.QuantInsulMm,
        Knob.Ring  => o.QuantRing,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static double TolOf(SolverOptions o, Knob k) => k switch
    {
        Knob.Thick => o.BisectTolMm,
        Knob.Insul => o.BisectTolMm,
        Knob.Ring  => o.BisectTolRing,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };
}

public sealed class SolverResult
{
    public DesignSpec Design = null!;
    public LineResult? Best;
    public bool Feasible;
    public double MassG = double.NaN;
    public string Message = "";
    public readonly List<string> Trace = new();
    /// <summary>场解次数 —— 求根的**真实成本**，比「轮数」诚实。</summary>
    public int Solves;
    /// <summary>true = 某旋钮顶到上界／分派前提不成立 ⇒ **不可行的证明**，不是搜索没找到。</summary>
    public bool HitBound;
    public string StopWhy = "";
}

public sealed class SolverOptions
{
    // ── 盒的**上界**。注意：起点永远是下界，上界只用来判「不可行」与做二分的右端。
    public double ThickHiMm = 6.0;
    public double InsHiMm   = 20.0;
    public double RingHi    = 2.5;

    // ── 盒的**下界**：每个都有第一性原理来源，不是挑出来的起点。
    /// <summary>裸舌 —— 舌片上什么都不缠时的等效保温厚度。</summary>
    public double InsLoMm = 0.3;
    /// <summary>无台阶 —— 环不加厚。</summary>
    public double RingLo  = 1.0;
    // （板厚下界来自 DesignSpec.DiscFloorMm：max(焊接屈曲, 烧穿)，不在这里给。）

    // ── **图纸格**（制造分辨率）。求解落在它上面，所以交付值就是被判过的值。
    /// <summary>板厚按 0.01 mm 出图。</summary>
    public double QuantThickMm = 0.01;
    /// <summary>舌保温按 0.1 mm 出图。</summary>
    public double QuantInsulMm = 0.1;
    /// <summary>环倍率按 0.01 出图。</summary>
    public double QuantRing = 0.01;

    /// <summary>二分停在旋钮分辨率上。0.005 mm 严于制造量化的 0.01 mm。</summary>
    public double BisectTolMm   = 0.005;
    public double BisectTolRing = 0.005;
    public int    BisectMaxIter = 14;
    public int    MaxRounds     = 30;
}
