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
///     `--solve --seedprobe` 实测：两组差得离谱的入参给出**逐位相同**的答案。
///
/// ══ **逐片求解**（2026-08-28，被实测逼出来的）
///
/// 第一版四片同步抬，实测**解不出来**：板厚↑修好②′却打坏③，舌保温↑修好③却打坏②′，
/// 两条判据互相追到盒顶（舌保温顶到 20 mm）。
/// 而它把舌保温收到 **18.826** —— 历史记录第一片正是 **18.7**：
/// 求解器**找对了那一片的答案**，却被「四片必须相同」逼着把它糊到另外三片上。
///
/// 四片物理上本来就不同：端片只接一段，共用片接两段且两段电流相差 120°
/// （矢量合成 √(I₁²+I₂²+I₁I₂) ≈ √3 倍），发热 ∝I² 是端片的 **2.6 倍**
/// （实测自由端下 490 W vs 156 W）。⇒ **四片同步不是简化，是错的模型。**
///
/// ⚠ 三条判据的**索引不是同一套**：②′ 与 ②″ 逐**片**，③ 逐**段**（3 段 4 片，错开一位）。
///   段 i 的 A 端贴第 i 片、B 端贴第 i+1 片 ⇒
///   **第 j 片的③ 责任 = max(段 j 的 A 端, 段 j−1 的 B 端)**，端片只有一侧。
///   为此 <see cref="SegmentOut.FlangeDipAK"/>/<c>BK</c> 才把两端各自存下来
///   （原先只留 <c>Math.Max</c>，说得出「有一片超了」，说不出**是哪一片**）。
///
/// ══ 求解器**检查自己的前提**（三条铁律第 ③ 条用在自己身上）
///
/// 二分求根成立的前提是「该判据对该旋钮单调」。前提不成立时**绝不能**假装解出来了：
/// <see cref="RaiseUntil"/> 每次都先测一次「旋钮顶到上界时判据有没有变好」，
/// 没变好就**报前提不成立**并停，而不是返回一个看着正常的错数。
/// —— 环倍率→②″ 那一支就是这么被抓出来的：实测 d②″/d倍率 = **+0.056**（文档写 −1.4），
///    方向相反，即「加环压不住 ②″，只是花铂」。
/// —— 逐片之后它还多管一件事：验「第 j 片的判据真的归第 j 片的旋钮管」
///    （Jacobian 对角占优）。**这条不假设，每次抬之前都实测。**
///
/// ══ 这里**没有**解决的（照实说，别当成已完成）
///   · 判据⑥（圆盘盖得住管孔＋焊脚）**没有旋钮能治** —— 它要改形状（盘径）。
///     求解器会照实报「抬不动它」，但**求解器与搜形状还没联动**。
///   · 形状（盘径 / 舌宽）仍归 ShapeSearchPlan 的固定步长模式搜索。
///   · ★★ **根的位置是网格相关的**（2026-08-28 实测暴出，这是本类自己的缺陷）。
///     二分跑在**导航网格**上，而判据以**网格无关复核**为准。实测同一份 0.8 档设计：
///     <code>
///       ③  导航 2 mm     = 4.720 K
///       ③  细网格 0.408  = 9.572 K      ← **翻 2.03 倍**
///       ②′ 导航 2 mm     = 1.123 W
///       ②′ 细网格 0.408  = 3.103 W      ← 2.76 倍（安全侧）
///     </code>
///     ⇒ 求解器给的「刚好不违反」是**粗网格上的刚好**，到细网格上可能已经越限。
///     两级机制对「搜索」成立（粗网格指方向够了），对「**求根**」不成立 ——
///     搜索只要方向对，求根要的是**根的位置**，而位置随网格移动。
///     ⚠ 直接在细网格上二分不可行：单次场解 ~900 s × 16 次/二分。
///       可行的做法（尚未做）：粗网格二分定位 → 对**咬住的那个旋钮**在细网格上再二分一次，
///       仍是确定性的、与初值无关。
///   · **不解圆盘厚度梯度。** 解析路的渐变环此前只有 **1 个自由度**（`RingMul`）：
///     一个两级台阶本要 4 个数（r₁、r₂、t₁、t₂），而 r₁=孔+w、r₂=孔+**2**w、
///     t₂=1+**0.4**(μ−1) 三个全写死。2026-08-28 已把三者放开成逐片真变量
///     （<see cref="DesignSpec.RingW1Mm"/>／<see cref="DesignSpec.RingW2Mm"/>／
///     <see cref="DesignSpec.RingMul2"/>），**但求解器还没把它们当旋钮** ——
///     要先用 `--monotone` 量出方向再定，不假设。
///   · **解析路表达不了开孔／开槽。** 那要走 `.3dm` 厚度场（`t=0` 即无材料，
///     轮廓/管孔/开槽用同一个量表达），由 `FlangeAutoSizer.SolveByLevel` 解
///     `LevelScale[片][级]` —— **那才是每片每级一个自由度的真逐级优化**。
///     ⚠ 走 `ShapeToAnalytic`（3DM→解析参数）会把多级厚度**面积加权平均压成一个数**，
///     代码自己标着「这是近似，铂重与温度都会跟着变」。
///   （量化 A⑤ 已拆：二分完**向上对齐到图纸格**，解直接落在可制造集合上，
///     不再存在「解完再四舍五入把判据四舍五入掉」这件事。）
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
        int np = d.TabThickMm.Length;

        // 下界也得落在图纸格子上（向**上**对齐：向下会掉到屈曲/烧穿下界以下）
        double tLo = Math.Ceiling(d.DiscFloorMm(baseIn) / opt.QuantThickMm - 1e-9) * opt.QuantThickMm;
        for (int j = 0; j < np; j++)
        {
            d.TabThickMm[j] = tLo;
            d.TabInsulMm[j] = opt.InsLoMm;
            d.RingMul[j]    = opt.RingLo;
        }

        // ★ 限值**只从 LineCase 读**（判据的唯一来源）。求解器不许自带第二份。
        var lc = d.BuildCase(baseIn, checkRamp: false);
        double dipMax = lc.RootDeltaMaxK, discMax = lc.DiscOverTempMaxK;

        Log($"起点 = **约束盒的下角**（不是种子）：板厚 {tLo:0.00} mm（焊接屈曲/烧穿下界）／" +
            $"舌保温 {opt.InsLoMm:0.00} mm（裸舌）／环倍率 {opt.RingLo:0.00}（无台阶）　× {np} 片");
        Log($"传进来的旋钮值**一个都没用**（板厚 {string.Join("/", geometry.TabThickMm.Select(x => x.ToString("0.00")))} 被丢弃）—— " +
            "这就是「与初值无关」的实现方式。");
        Log($"限值只从 LineCase 读：③ ≤ {dipMax:0.0} K　②″ ≤ {discMax:0.0} K");

        // ★★ **⑥ 排在第一次场解之前**（2026-08-29）。它是闭式的、零成本 ——
        //   几何造不出来就不必解场。此前它排在场解之后，代价不只是白烧一次解：
        //   实测盘半径 27.0（孔 25.8 ⇒ 盘环只剩 1.2 mm）时，导航网格**一格都落不进圆盘区**，
        //   于是先被「②″ 判不了（NaN）」挡下 —— 报出来的是「算不出来」，
        //   而真正的毛病是「**盘太小**」，且那句话还带着能直接照做的处方。
        //   ⚠ 判据本身没错（判不了不算过是对的），错的是**顺序**：
        //     一条不解场就能回答的判据，不该让一条要解场的判据先替它开口。
        //   ⚠ 这里用的是**起点板厚**（盒的下角）⇒ 只挡「怎么解都造不出来」的几何。
        //     抬板厚之后才越界的那一类，由 RaiseUntil 里那道当场检查接住。
        var (pre6Ok, pre6Why) = CoverCheck(d, baseIn);
        if (!pre6Ok)
        {
            res.Design = d; res.Feasible = false; res.HitBound = true;
            res.StopWhy = pre6Why
                + "　（在**任何场解之前**就判定：⑥ 是闭式的，且这里用的是**起点板厚**"
                + "—— 抬旋钮只会让焊脚更长，救不回来。）";
            res.Message = res.StopWhy;
            Log("  ✗ " + res.StopWhy);
            return res;
        }

        LineResult? last = null;

        // ★★ 求根**跑两遍，只有网格不同**（算法普查 A⑬）。
        //   第一遍在导航网格上把根定位到附近（便宜）；
        //   第二遍在细网格上重新求根 —— **判据以细网格为准**，而根的位置随网格移动
        //   （实测 ③ 导航 4.720 → 细网格 9.572，翻 2.03 倍）。
        //   ⚠ 第二遍**从第一遍的解出发**、且照样只增不减 ⇒
        //     「收敛到最小可行点」与「与初值无关」两条都还成立
        //     （第一遍本身与初值无关，第二遍是它的确定性函数）。
        bool Rounds(SolverOptions o, string tag)
        {
            // 细网格那一遍才转内层进度：导航网格单次几秒，转了只是噪音。
            var inner = o.FineMm > 0
                      ? new ThrottledProgress(progress, 20, $"     · {o.FineMm:0.000} mm ")
                      : null;
            Log($"── {tag}" + (o.FineMm > 0 ? $"（细网格 {o.FineMm:0.000} mm）" : "（导航网格）"));
            for (int round = 1; round <= o.MaxRounds; round++)
            {
                cancel.ThrowIfCancellationRequested();
                last = Eval(d, baseIn, o, res, cancel, inner);
                if (last is null) { res.StopWhy = "场解不收敛，判不了"; break; }

                double mass = MassOf(last);
                Log($"第 {round,2} 轮　合计 {mass:0} g" +
                    $"　板厚 {Join(d.TabThickMm)}　舌保温 {Join(d.TabInsulMm)}　环倍率 {Join(d.RingMul)}");

                // ── 逐片逐条列违反
                var todo = new List<(int J, Knob Knob, string Key)>();
                for (int j = 0; j < np; j++)
                    foreach (var (key, knob) in Allocation)
                    {
                        double sl = PlateSlack(last, key, j, dipMax, discMax);
                        if (double.IsNaN(sl))
                        {
                            res.StopWhy = $"第 {j} 片的「{key}」**判不了**（值是 NaN）—— 判不了不算过";
                            res.HitBound = true;
                            Log("  ✗ " + res.StopWhy);
                            return false;   // 判不了 ⇒ 本遍失败；收尾统一交给 Solve 末尾
                        }
                        if (sl < 0)
                        {
                            todo.Add((j, knob, key));
                            Log($"     片{j}「{key}」裕度 {sl:+0.000;-0.000} ⇒ 抬{KnobName(knob)}");
                        }
                    }

                if (todo.Count == 0)
                {
                    // 三条逐片判据全过。还有**没有旋钮**的判据（⑥、⑤、①、管 J…）要看。
                    var rest = Violations(last)
                        .Where(c => !Allocation.Any(a => c.Name.StartsWith(a.Key, StringComparison.Ordinal)))
                        .ToList();
                    if (rest.Count == 0)
                    {
                        res.Feasible = true;
                        res.StopWhy = $"第 {round} 轮全过；因为**只往上走过**，这就是最小的可行点";
                    }
                    else
                    {
                        res.HitBound = true;
                        res.StopWhy = "三条逐片判据都过了，但这些判据**没有旋钮能治**（要改形状）："
                                    + string.Join("、", rest.Select(c => c.Name));
                        // ★ 只报名字等于把活推回给人。⑥ 是闭式的 —— 处方当场就能算出来。
                        if (rest.Any(c => c.Name.StartsWith(LineResult.Key.DiscCover,
                                                            StringComparison.Ordinal)))
                        {
                            var (_, why6) = CoverCheck(d, baseIn);
                            if (why6.Length > 0) res.StopWhy += "　" + why6;
                        }
                        Log("  ✗ " + res.StopWhy);
                    }
                    break;
                }

                bool bad = false;
                foreach (var (j, knob, key) in todo)
                {
                    var (ok, why) = RaiseUntil(d, baseIn, o, j, knob, key, dipMax, discMax, res, Log, cancel, inner);
                    if (!ok) { res.StopWhy = why; res.HitBound = true; Log("  ✗ " + why); bad = true; break; }
                }
                if (bad) break;
            }
            return res.Feasible;
        }

        // 第一遍：导航网格
        var navOpt = opt.Clone(); navOpt.FineMm = 0; navOpt.FineRadiusMm = 0;
        bool okNav = Rounds(navOpt, "第一遍：导航网格上定位");
        // ★ 终局复核必须跑在**最后一遍求根所用的那张网格**上（见 Finish）。
        var lastOpt = navOpt;

        // 第二遍：细网格。第一遍没走通就不做 —— StopWhy 已经说明了原因。
        if (okNav && opt.FineMm > 0)
        {
            res.Feasible = false; res.StopWhy = ""; res.HitBound = false;
            bool okFine = Rounds(opt, "第二遍：细网格上重新求根（**判据以此为准**）");
            res.FineRefined = true; res.FineMmUsed = opt.FineMm; lastOpt = opt;
            if (!okFine && res.StopWhy.Length == 0)
                res.StopWhy = $"细网格第二遍跑满 {opt.MaxRounds} 轮仍未全过 —— **未收敛**，不作数";
        }
        else if (opt.FineMm <= 0)
        {
            Log("⚠ **没做第二遍**（FineMm = 0）—— 这个解只在导航网格上成立、**不可交付**："
                + "实测 ③ 在两张网格上差 2.03 倍（A⑬）。");
        }

        if (res.StopWhy.Length == 0)
            res.StopWhy = $"跑满 {opt.MaxRounds} 轮仍未全过 —— 结果**未收敛**，不作数";

        return Finish(res, d, last, baseIn, lastOpt, cancel, progress);
    }

    /// <summary>
    /// 把第 <paramref name="j"/> 片的 <paramref name="knob"/> 二分抬到
    /// 「该片的 <paramref name="key"/> 刚好不违反」的最小值。
    /// **先验前提**：旋钮顶到上界时该片的判据必须变好，否则报前提不成立。
    /// </summary>
    private static (bool Ok, string Why) RaiseUntil(
        DesignSpec d, DesignInputs baseIn, SolverOptions opt, int j, Knob knob, string key,
        double dipMax, double discMax, SolverResult res, Action<string> Log, CancellationToken cancel,
        IProgress<string>? inner = null)
    {
        double lo = Get(d, knob, j);
        double hi = HiOf(opt, knob);
        string nm = $"片{j} {KnobName(knob)}";

        double before = PlateSlack(Eval(d, baseIn, opt, res, cancel, inner), key, j, dipMax, discMax);

        // ★ 上一片抬完可能已经把这一片捎带治好了 —— 那就**不抬**（最小性）
        if (before >= 0)
        {
            Log($"  · {nm}：上一步之后「{key}」已经不违反（裕度 {before:+0.000;-0.000}）⇒ **不抬**");
            return (true, "");
        }

        if (lo >= hi - 1e-12)
            return (false, $"**{nm} 已在上界 {hi:0.000}**，「{key}」仍不过 ⇒ 这组输入不可行（是证明，不是搜索失败）");

        Set(d, knob, j, hi);
        double after = PlateSlack(Eval(d, baseIn, opt, res, cancel, inner), key, j, dipMax, discMax);

        // ★ 前提自检：抬到底也没让这一片的判据变好 ⇒ 这条分派对这一片是错的，**不许假装解出来**
        if (!(after > before + 1e-9))
        {
            Set(d, knob, j, lo);
            return (false,
                $"**分派前提不成立**：{nm} 从 {lo:0.000} 抬到上界 {hi:0.000}，" +
                $"「{key}」的裕度 {before:+0.000;-0.000} → {after:+0.000;-0.000}（**没变好**）" +
                " ⇒ 这个旋钮压不住这一片的这条判据，二分不适用");
        }

        if (after < 0)
        {
            Set(d, knob, j, lo);
            return (false, $"**{nm} 抬到上界 {hi:0.000} 仍不过**「{key}」⇒ 这组输入不可行");
        }

        // 二分：找「刚好不违反」的最小值。不变式：lo 违反、hi 不违反。
        for (int i = 0; i < opt.BisectMaxIter && hi - lo > TolOf(opt, knob); i++)
        {
            cancel.ThrowIfCancellationRequested();
            double mid = 0.5 * (lo + hi);
            Set(d, knob, j, mid);
            if (PlateSlack(Eval(d, baseIn, opt, res, cancel, inner), key, j, dipMax, discMax) >= 0) hi = mid; else lo = mid;
        }

        // ★ 量化在**解之内**，不在解之后（算法普查 A⑤）。
        //   往**上**取整是可证明安全的：本判据对本旋钮递增（前提已在上面验过），
        //   抬到格点只会让它更过，不会翻回去。
        //   抬高可能让**别的**判据变差 —— 那由外层下一轮再抬它自己的旋钮补上，
        //   「只增不减」的不变式不受影响。
        double q = QuantOf(opt, knob);
        double snapped = Math.Min(Math.Ceiling(hi / q - 1e-9) * q, HiOf(opt, knob));
        Set(d, knob, j, snapped);
        Log($"  ↑ {nm} → {snapped:0.000}（二分求根 {hi:0.0000} → 向上对齐到图纸格 {q:0.###}）");

        // ★★ 上面那句「抬高可能让别的判据变差 —— 由外层下一轮再抬它自己的旋钮补上」
        //   对 **⑥ 不成立**：⑥ 没有旋钮可补。而抬板厚会一对一地吃掉它的裕度
        //   （焊脚 = max(板厚, 壁厚)）⇒ 这里必须**当场**验一次。
        //   闭式、零成本、不用解场；不验的话要等三条逐片判据全过才发现，
        //   而那时已经白抬了一整轮的板厚。
        if (knob == Knob.Thick)
        {
            var (coverOk, coverWhy) = CoverCheck(d, baseIn);
            if (!coverOk) { Log("  ✗ " + coverWhy); return (false, coverWhy); }
        }
        return (true, "");
    }

    /// <summary>
    /// **⑥ 圆盘盖得住管孔＋焊脚** —— 闭式、零成本，抬板厚之后必须当场验。
    ///
    /// 不过时给的是**处方**（盘径要改到多大），不是抱怨。这一点是本条的要点：
    /// ⑥ 没有旋钮，但它**不需要搜索** —— 反解一次就得到确切的盘径
    /// （<see cref="GeometryScreen.MinDiscRadiusMm(System.Collections.Generic.IReadOnlyList{FlangePlate})"/>）。
    ///
    /// ⚠ 求解器**自己不改盘径**：盘径是形状，不是它的变量。改了就不再是
    ///   「这个形状下的最小可行点」，而是另一个形状的解 —— 那会把
    ///   「解与初值无关」和「最小可行点」两句话同时说不清。它只负责把处方交出去。
    /// </summary>
    public static (bool Ok, string Why) CoverCheck(DesignSpec d, DesignInputs baseIn)
    {
        double floor = d.DiscFloorMm(baseIn);
        var plates = new FlangePlate[d.TabThickMm.Length];
        for (int j = 0; j < plates.Length; j++) plates[j] = d.Plate(j, floor);

        double need = GeometryScreen.MinDiscRadiusMm(plates);
        if (double.IsNaN(need))
            return (false, "**⑥ 判不了**：一片法兰都没有 —— 判不了不算过");
        if (d.DiscRadiusMm >= need - 1e-9) return (true, "");

        double leg = plates.Max(q => Math.Max(q.WeldFilletLegMm, 0));
        return (false,
            $"**⑥ 圆盘盖不住管孔＋焊脚**：盘半径 {d.DiscRadiusMm:0.000} mm ＜ 需要 {need:0.000} mm"
          + $"（缺 {need - d.DiscRadiusMm:0.000} mm；管孔 {d.HoleRadiusMm:0.000} + 焊脚 {leg:0.000}）。"
          + " ⑥ **没有旋钮能治** —— 抬板厚只会让焊脚更长、⑥ 更差。"
          + $"　【处方】盘半径改到 ≥ {need:0.000} mm 再解。"
          + "这是 ⑥ 的**闭式反解**，不是搜出来的 —— 不用试，就是这个数。");
    }

    /// <summary>
    /// 终局复核：把升温 ① 带上再算一次（导航时为省时关掉的那条）。
    ///
    /// ★★ <b>必须跑在最后一遍求根所用的那张网格上</b>（<paramref name="lastOpt"/>）。
    /// 2026-08-29 之前这里是 <c>d.BuildCase(baseIn, checkRamp: true)</c> 后**什么都不设**,
    /// 于是悄悄退回 <see cref="LineCase.MeshFineMm"/> 的默认值 <b>2.0 mm</b>：
    /// <code>
    ///   第二遍求根  → 细网格（如 0.146 mm）上把旋钮抬到全过
    ///   Finish     → 回到 2.0 mm 重算，**并用它覆盖 res.Best**
    ///   ⇒ res.Feasible / 所有印出来的判据值 = 粗网格的数
    /// </code>
    /// 而本类自己的 <see cref="Eval"/> 就写着「根的位置随网格移动」、实测 ③ 在两张网格上
    /// **差 2.03 倍**（A⑬）。⇒ 求解器会出现「第二遍说全过、终局说不可行」这种自相矛盾，
    /// 而两边**都不报错**。正是本项目最怕的错误形态：看起来正常的错数。
    ///
    /// ⚠ 只在 <c>--fine</c>（opt.FineMm &gt; 0）时发作；FineMm = 0 的跑法两边同为默认网格,
    ///   所以历史上那些 <c>--solve --verifymesh</c> 的结论**不受影响**。
    /// </summary>
    private static SolverResult Finish(SolverResult res, DesignSpec d, LineResult? last,
                                       DesignInputs baseIn, SolverOptions lastOpt,
                                       CancellationToken cancel, IProgress<string>? progress = null)
    {
        res.Design = d;
        var lcF = d.BuildCase(baseIn, checkRamp: true);
        if (lastOpt.FineMm > 0)
        {
            lcF.MeshFineMm = lastOpt.FineMm;
            if (lastOpt.FineRadiusMm > 0) lcF.MeshFineRadiusMm = lastOpt.FineRadiusMm;
        }
        // 细网格上带 ① 的这一次可能跑很久 —— 不转进度就是几十分钟静默。
        var innerF = new ThrottledProgress(progress, 20, "     · 终局复核 ");
        try { res.Best = LineRunner.Run(lcF, innerF, cancel); res.Solves++; }
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

    /// <summary>
    /// 算一次。★ 网格从 <paramref name="o"/> 取 —— 求根**跑在哪张网格上**是本类的关键量
    /// （见类注释 A⑬：根的位置随网格移动），不许由别处悄悄决定。
    /// </summary>
    private static LineResult? Eval(DesignSpec d, DesignInputs baseIn, SolverOptions o,
                                    SolverResult res, CancellationToken cancel,
                                    IProgress<string>? inner = null)
    {
        try
        {
            var lc = d.BuildCase(baseIn, checkRamp: false);
            if (o.FineMm > 0)
            {
                lc.MeshFineMm = o.FineMm;
                if (o.FineRadiusMm > 0) lc.MeshFineRadiusMm = o.FineRadiusMm;
            }
            // ★ 细网格那一遍单次可能跑 ~900 s；不转内层进度就是几十分钟静默，
            //   看不出「慢」和「挂了」的区别（用户 2026-08-29）。
            var r = LineRunner.Run(lc, inner, cancel);
            res.Solves++;
            return r.Ok ? r : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { res.Solves++; return null; }
    }

    /// <summary>违反的硬判据（整体口径，用来兜住**没有旋钮**的那些）。判不了 = 不算过。</summary>
    private static IEnumerable<ConstraintOut> Violations(LineResult r) =>
        r.Checks.Where(c => c.Kind != CheckKind.Reference && (!c.Ok || c.Undetermined));

    /// <summary>
    /// **第 j 片**这条判据的裕度，正 = 过。NaN = 判不了（调用方必须当成不过）。
    ///
    /// ⚠ 这里读的是**逐片原始量**，不是 <c>Checks</c> 里那条汇总后的
    ///   「最差那片」——汇总量说得出「有一片超了」，说不出是哪一片，
    ///   而逐片求解必须知道该抬哪一片的旋钮。
    ///
    /// ⚠ 方向与整体判据同一口径：②′ 须 ≥ 0（越大越好），③ 与 ②″ 越小越好。
    ///   写反会把「越限」读成「有余量」，二分就朝错的方向收 —— 本仓库出过这个错。
    /// </summary>
    private static double PlateSlack(LineResult? r, string key, int j, double dipMax, double discMax)
    {
        if (r is null) return double.NegativeInfinity;

        if (key == LineResult.Key.NetFlux)
        {
            if (j >= r.Flanges.Length) return double.NegativeInfinity;
            double q = r.Flanges[j].QFromTubeW;
            return double.IsNaN(q) ? double.NaN : q;              // 须 > 0，限值就是 0
        }
        if (key == LineResult.Key.DiscTemp)
        {
            if (j >= r.Flanges.Length) return double.NegativeInfinity;
            var f = r.Flanges[j];
            double over = f.TDiscMaxC - f.TRootC;
            return double.IsNaN(over) ? double.NaN : discMax - over;   // 越小越好
        }
        if (key == LineResult.Key.FlangeDip)
        {
            double dip = PlateDip(r, j);
            return double.IsNaN(dip) ? double.NaN : dipMax - dip;      // 越小越好
        }
        throw new ArgumentOutOfRangeException(nameof(key), key, "没有这条判据的逐片口径");
    }

    /// <summary>
    /// **第 j 片要为哪些「段端」负责** —— 纯映射，微秒可验。
    ///
    /// 段 i 的 A 端贴第 i 片、B 端贴第 i+1 片（n 段 ⇒ n+1 片，错开一位）⇒
    ///   第 j 片 = 段 j 的 A 端（若存在） ＋ 段 j−1 的 B 端（若存在）。
    /// 端片（j=0 与 j=n）只有一侧，中间的共用片两侧都算。
    ///
    /// ★ 不变式：把所有片的结果并起来，**恰好覆盖 n 段 × 2 端，不重不漏**。
    ///   漏一个端就是「有一片的责任没人担」，重一个端就是「同一个违反被两片抢着治」——
    ///   两种都会让「只增不减」抬出不必要的铂重。`SolverPerPlateTests` 盯着这条。
    ///
    /// ⚠ 抽成纯函数是仓库的既定做法：这种错位一位的下标最容易写反，
    ///   而写反之后**跑起来一切正常，只是抬错了片**——正是本项目最怕的错误形态。
    /// </summary>
    public static (int Seg, bool AEnd)[] EndsOf(int j, int nSeg)
    {
        var outp = new List<(int, bool)>(2);
        if (j >= 0 && j < nSeg) outp.Add((j, true));       // 段 j 的 A 端
        if (j - 1 >= 0 && j - 1 < nSeg) outp.Add((j - 1, false));  // 段 j−1 的 B 端
        return outp.ToArray();
    }

    /// <summary>
    /// 第 j 片的③ 值：它负责的那些段端里**最差的那个**
    /// —— 与整体判据同一口径（两端取较差）。任一端判不了则整体判不了。
    /// </summary>
    private static double PlateDip(LineResult r, int j)
    {
        var ends = EndsOf(j, r.Segments.Length);
        if (ends.Length == 0) return double.NaN;
        double worst = double.NegativeInfinity;
        foreach (var (seg, aEnd) in ends)
        {
            double v = aEnd ? r.Segments[seg].FlangeDipAK : r.Segments[seg].FlangeDipBK;
            if (double.IsNaN(v)) return double.NaN;
            worst = Math.Max(worst, v);
        }
        return worst;
    }

    private static double MassOf(LineResult r) =>
        r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);

    private static string Join(double[] v) => string.Join("/", v.Select(x => x.ToString("0.00")));

    private static double Get(DesignSpec d, Knob k, int j) => k switch
    {
        Knob.Thick => d.TabThickMm[j],
        Knob.Insul => d.TabInsulMm[j],
        Knob.Ring  => d.RingMul[j],
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static void Set(DesignSpec d, Knob k, int j, double v)
    {
        switch (k)
        {
            case Knob.Thick: d.TabThickMm[j] = v; break;
            case Knob.Insul: d.TabInsulMm[j] = v; break;
            case Knob.Ring:  d.RingMul[j]    = v; break;
            default: throw new ArgumentOutOfRangeException(nameof(k));
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

    /// <summary>
    /// **第二遍（细网格）求根做了没有**。false ⇒ 这个解只在导航网格上成立、
    /// <b>不可交付</b> —— 实测 ③ 在两张网格上差 2.03 倍（A⑬）。调用方必须呈现，不许吞。
    /// </summary>
    public bool FineRefined;

    /// <summary>第二遍用的细网格 mm（0 = 没做第二遍）。</summary>
    public double FineMmUsed;
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
    /// <summary>逐片之后一轮要处理的抬升更多，轮数要给够。</summary>
    public int    MaxRounds     = 60;

    // ══ 第二遍求根的网格（算法普查 A⑬）
    //
    // 病灶（2026-08-28 实测）：二分跑在导航网格，判据以网格无关复核为准。
    // 同一份 0.8 档：③ 导航 2 mm = 4.720 → 细网格 0.408 mm = **9.572**（翻 2.03 倍）。
    // ⇒ 只在导航网格上求根，给的是「**粗网格上的刚好**」，到细网格可能已越限。
    //
    // 修法：**同一个循环跑两遍，只有网格不同**。第二遍从第一遍的解出发、
    // 仍然只增不减 ⇒「最小可行点」与「与初值无关」两条都还成立。

    /// <summary>
    /// 第二遍求根的细网格 mm。**0 = 不做第二遍**。
    ///
    /// ★★ **正确的用法是「先验，不过才付」**（2026-08-28 实测定下来的纪律）：
    /// <code>
    ///   粗网格求根（~40 min） → 网格无关复核（~35 min） → 过  ⇒ 完事，不必付第二遍
    ///                                                   → 不过 ⇒ 才开 --fine（贵得多）
    /// </code>
    ///   实测 0.8 档：求解器的解在网格无关分辨率（0.388 mm）上 ③ = **7.333 / 10**，
    ///   ②′ = 2.355 W，②″ = −0.008 —— **全过**，粗网格上的根恰好落在安全侧。
    ///   ⇒ A⑬ 的担心是真的（③ 两张网格差 2.03 倍），但**不是每次都咬**。
    ///     验证便宜、二次求根昂贵 ⇒ 先验。
    ///
    /// 不做第二遍时结果只在导航网格上成立、
    /// <b>不可交付</b>（<see cref="SolverResult.FineRefined"/> 为 false，调用方必须呈现）。
    /// 该由 <see cref="MeshAdapt.RequiredFineMm"/> 从几何特征算出，不该手填。
    /// </summary>
    public double FineMm;

    /// <summary>细网格的作用半径 mm。0 = 沿用 LineCase 的默认。</summary>
    public double FineRadiusMm;

    public SolverOptions Clone() => (SolverOptions)MemberwiseClone();
}
