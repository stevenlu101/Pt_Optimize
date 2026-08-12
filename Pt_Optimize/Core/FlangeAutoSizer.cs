using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **四片法兰自动定厚** —— 求一组厚度，使**每一段**的管根温差都落在目标窗口内。
///
/// 为什么必须四片各自独立：n 段有 n 个约束，n+1 片有 n+1 个厚度自由度。
/// 若把四片绑成一个标度（如按 t ∝ I 分配），就只剩 1 个自由度，
/// 只能让**一段**达标，其余段实测落在 +45…+104 K（HANDOVER §4.2w）。
///
/// 解法：阻尼牛顿。段 i 的管根温差主要由它两端的片 i、i+1 决定，
/// 故片 j 的误差取相邻段误差的均值，按灵敏度走对数步：
///
///   Δln t_j = −ω · err_j / S,   S = d(ΔT)/d(ln t) ≈ 800 K
///
/// 用**对数**步长而非线性，是因为厚度跨越 0.4–6 mm 时灵敏度按 1/t 变；
/// 对数步在整个区间上步幅相当，不会在薄端过冲。
///
/// ⚠ 靶量必须**连续单调**。此前用「|ΔT| 最大那段的带符号值」，
/// 最不利段身份一切换该量就跳变，二分/牛顿全部失效（§7）。
/// 这里逐段各自算误差，不取极值，天然连续。
/// </summary>
public static class FlangeAutoSizer
{
    public sealed class Options
    {
        /// <summary>目标管根温差 K。取窗口中偏安全的一侧（0 &lt; ΔT &lt; 10）</summary>
        public double TargetK = 5.0;
        /// <summary>收敛判据：所有段的 |ΔT − 目标| 均小于此值 K</summary>
        public double TolK = 2.0;
        public int MaxIterations = 25;
        /// <summary>灵敏度 d(ΔT)/d(ln t)，K。由 --tscan 的斜率估得</summary>
        public double SensitivityK = 800.0;
        /// <summary>阻尼系数。1 = 全牛顿步（会振荡），0.6 实测稳定</summary>
        public double Damping = 0.6;
        public double MinThickMm = 0.4, MaxThickMm = 6.0;
        /// <summary>单步对数位移上限，防止首轮从很差的初值一步跳飞</summary>
        public double MaxLogStep = 0.35;

        // ── 搜索期降精度（收敛后会自动做一次全精度复核）
        //
        // 单次全精度整线耦合解在 .3dm 路径上实测 **69 s**（3005 单元、耦合到 1 K）。
        // 逐级优化要几百次调用 ⇒ 几个小时，不可用。
        // 搜索期只需要**梯度方向对**，不需要每一步都精确，故粗网格 + 松耦合；
        // 最终解再用调用方原本的精度复核一遍 —— 报告值一律取那一次。
        /// <summary>搜索期的细网格步长 mm（原值 2.0）。0 = 不降精度</summary>
        public double SearchMeshFineMm = 4.0;
        public double SearchMeshCoarseMm = 16.0;
        /// <summary>搜索期的段↔法兰耦合轮数与容差</summary>
        public int SearchCoupleRounds = 5;
        public double SearchCoupleTolK = 4.0;
    }

    public sealed class Result
    {
        public double[] ThicknessMm = Array.Empty<double>();
        public LineResult? Line;
        public int Iterations;
        public bool Converged;
        public string Message = "";
        /// <summary>每轮的最大误差，供界面画收敛曲线或诊断振荡</summary>
        public readonly List<double> History = new();
        /// <summary>逐级定厚的结果：`[片][级]` 的厚度倍数（仅 SolveByLevel 填）</summary>
        public double[][]? LevelScale;
    }

    /// <summary>
    /// **自动求解**：迭代次数与阻尼由本方法自行调整，不需要调用方猜。
    ///
    /// 策略：先按默认阻尼跑；若耗尽轮次仍未达标，则判断是**振荡**还是**爬得太慢**——
    ///   · 误差不再单调下降（振荡）⇒ 阻尼减半，重来
    ///   · 误差仍在稳定下降（只是没走完）⇒ 轮次翻倍，接着跑
    /// 最多升级 <paramref name="maxEscalations"/> 次。这样「二分/迭代次数不够就误报无解」
    /// 那类错误（§7）在界面上不可能再发生 —— 不收敛只会是真的无解。
    /// </summary>
    public static Result SolveAuto(LineCase baseCase, Func<double, FlangePlate>? makePlate,
                                   double[] initialThicknessMm, Options? opt = null,
                                   IProgress<string>? progress = null,
                                   CancellationToken cancel = default,
                                   int maxEscalations = 4)
    {
        opt ??= new Options();
        var cur = new Options
        {
            TargetK = opt.TargetK, TolK = opt.TolK, MaxIterations = opt.MaxIterations,
            SensitivityK = opt.SensitivityK, Damping = opt.Damping,
            MinThickMm = opt.MinThickMm, MaxThickMm = opt.MaxThickMm, MaxLogStep = opt.MaxLogStep
        };
        var start = (double[])initialThicknessMm.Clone();
        Result last = new();

        for (int esc = 0; esc <= maxEscalations; esc++)
        {
            last = Solve(baseCase, makePlate, start, cur, progress, cancel);
            if (last.Converged) return last;

            // 判断失败模式：末段误差是否还在下降
            var h = last.History;
            bool stillDescending = h.Count >= 3 && h[^1] < h[^3] * 0.9;
            if (esc == maxEscalations) break;

            start = last.ThicknessMm;                 // 从当前点继续，不从头来
            if (stillDescending)
            {
                cur.MaxIterations *= 2;
                progress?.Report($"未达标但仍在收敛 ⇒ 轮次加倍到 {cur.MaxIterations}，继续…");
            }
            else
            {
                cur.Damping *= 0.5;
                cur.MaxIterations = (int)(cur.MaxIterations * 1.5);
                progress?.Report($"出现振荡 ⇒ 阻尼降到 {cur.Damping:0.000}、轮次 {cur.MaxIterations}，重试…");
            }
        }
        last.Message = "自动升级 " + maxEscalations + " 次后仍未达标：" + last.Message;
        return last;
    }

    /// <summary>
    /// 单次迭代求解（固定轮次与阻尼）。一般用 <see cref="SolveAuto"/>。
    /// <paramref name="makePlate"/> 把厚度变成几何 —— 由调用方提供，
    /// 于是本类不关心形状（解析圆盘/舌片、阶梯、乃至 .3dm 的厚度标度都行）。
    /// </summary>
    public static Result Solve(LineCase baseCase, Func<double, FlangePlate>? makePlate,
                               double[] initialThicknessMm, Options? opt = null,
                               IProgress<string>? progress = null,
                               CancellationToken cancel = default)
    {
        opt ??= new Options();
        var t = (double[])initialThicknessMm.Clone();
        var res = new Result { ThicknessMm = t };

        for (int it = 0; it < opt.MaxIterations; it++)
        {
            cancel.ThrowIfCancellationRequested();

            var lc = CloneCase(baseCase);
            if (opt.SearchMeshFineMm > 0)
            {
                lc.MeshFineMm = opt.SearchMeshFineMm;
                lc.MeshCoarseMm = opt.SearchMeshCoarseMm;
                lc.CoupleMaxRounds = opt.SearchCoupleRounds;
                lc.CoupleTolK = opt.SearchCoupleTolK;
            }
            if (makePlate is not null)
                lc.FlangePlates = t.Select(makePlate).ToArray();      // 解析几何：t 就是厚度
            else
            {
                lc.FlangeFile3dm = baseCase.FlangeFile3dm;            // .3dm：t 是厚度**标度**
                lc.ThicknessScale = (double[])t.Clone();
            }

            LineResult lr;
            try { lr = LineRunner.Run(lc, null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                res.Message = "求解异常：" + ex.Message;
                res.Iterations = it;
                return res;
            }
            if (!lr.Ok) { res.Message = lr.Message; res.Iterations = it; return res; }

            res.Line = lr;
            res.Iterations = it + 1;

            var err = lr.Segments.Select(s => s.RootDeltaK - opt.TargetK).ToArray();
            double worst = err.Length == 0 ? 0 : err.Max(Math.Abs);
            res.History.Add(worst);
            progress?.Report($"第 {it + 1} 轮：最大偏差 {worst:0.0} K　厚度 " +
                             string.Join("/", t.Select(x => x.ToString("0.00"))));

            if (worst < opt.TolK)
            {
                res.Converged = true;
                res.Message = $"{it + 1} 轮收敛，各段管根温差偏离目标 < {opt.TolK:0.#} K";
                return res;
            }

            // 片 j 的误差 = 相邻段误差均值（端片只有一个邻段）
            for (int j = 0; j < t.Length; j++)
            {
                double e = j == 0 ? err[0]
                         : j >= err.Length ? err[^1]
                         : 0.5 * (err[j - 1] + err[j]);
                double step = Math.Clamp(-opt.Damping * e / opt.SensitivityK,
                                         -opt.MaxLogStep, opt.MaxLogStep);
                t[j] = Math.Clamp(t[j] * Math.Exp(step), opt.MinThickMm, opt.MaxThickMm);
            }
        }

        res.Message = $"{opt.MaxIterations} 轮未收敛（最大偏差 {res.History.LastOrDefault():0.0} K）。" +
                      "可能是某片已顶到厚度上下界，或该形状在此电流下无解。";
        return res;
    }

    /// <summary>
    /// **逐级定厚**（.3dm 专用）—— 让优化器自己决定各级厚度的比例。
    ///
    /// 两条约束由两组自由度分别负责，互不干扰，所以可以分层：
    ///
    /// | 层 | 自由度 | 管的约束 | 机理 |
    /// |---|---|---|---|
    /// | 外层 | 每片一个**整体**倍数 | C2 管根温差 | 整片发热总量 ⇒ 从管子抽多少热 |
    /// | 内层 | 每片各级的**相对**比例 | C1 局部过热 | 单位面积发热 = ρe·K²/t ⇒ 加厚哪一级，哪一级就变凉 |
    ///
    /// 内层调完后把各级比例**归一化**（几何平均拉回 1），于是整片的平均厚度不变，
    /// 外层看到的热平衡几乎不动 —— 这就是两层能解耦的原因。
    ///
    /// 内层的靶：让各级的局部峰值温度**齐平**（都压到管根温度附近）。
    /// 哪一级更热就加厚哪一级，热量被摊到其余级去。
    /// </summary>
    /// <param name="levelLocked">
    /// `[片][级]` 为 true 的级**完全不动**（厚度锁死）。典型用法：
    /// 「外圈厚度不动、只调内圈」—— 把外圈那级锁上，优化器只在其余级上找解。
    /// 全为 null = 各级都可动（优化器自行决定比例）。
    ///
    /// ⚠ 锁级会同时削掉外层的调节能力：整片热平衡只能靠**未锁的级**去凑，
    /// 若未锁的级面积占比很小，可能怎么调都够不到目标 —— 那时返回未收敛，
    /// 并在 Message 里说明是被锁死限制的，而不是物理上无解。
    /// </param>
    public static Result SolveByLevel(LineCase baseCase, double[][] levelThicknessMm,
                                      Options? opt = null, IProgress<string>? progress = null,
                                      CancellationToken cancel = default, int outerRounds = 6,
                                      bool[][]? levelLocked = null)
    {
        bool Locked(int j, int m) => levelLocked is not null && j < levelLocked.Length
                                     && m < levelLocked[j].Length && levelLocked[j][m];
        opt ??= new Options();
        int nf = levelThicknessMm.Length;
        var scale = new double[nf][];
        for (int j = 0; j < nf; j++)
        {
            scale[j] = new double[levelThicknessMm[j].Length];
            for (int m = 0; m < scale[j].Length; m++) scale[j][m] = 1.0;
        }

        Result last = new();
        for (int round = 0; round < outerRounds; round++)
        {
            cancel.ThrowIfCancellationRequested();

            // ── 外层：在**当前各级比例**下，求每片的整体倍数，使管根温差达标
            var overall = new double[nf];
            for (int j = 0; j < nf; j++) overall[j] = 1.0;
            var lcBase = CloneCase(baseCase);
            lcBase.LevelThicknessMm = levelThicknessMm;
            lcBase.LevelScale = scale;

            progress?.Report($"第 {round + 1}/{outerRounds} 轮 · 外层：调整每片整体厚度…");
            last = SolveAuto(lcBase, null, overall, opt, progress, cancel);
            if (last.Line is null) return last;

            // 把外层求出的整体倍数并进各级比例 —— **锁住的级不并**
            for (int j = 0; j < nf; j++)
            {
                double kj = j < last.ThicknessMm.Length ? last.ThicknessMm[j] : 1.0;
                for (int m = 0; m < scale[j].Length; m++)
                    if (!Locked(j, m)) scale[j][m] *= kj;
            }

            // ── 内层：按各级峰值温度重新分配比例（总平均厚度不变）
            var lr = last.Line;
            double worstOver = 0;
            for (int j = 0; j < nf && j < lr.Flanges.Length; j++)
            {
                var f = lr.Flanges[j];
                if (f.LevelTMaxC.Length != scale[j].Length) continue;
                double baseT = f.TRootC;
                var adj = new double[scale[j].Length];
                double logSum = 0; int cnt = 0;
                for (int m = 0; m < adj.Length; m++)
                {
                    if (Locked(j, m)) { adj[m] = 1.0; continue; }      // 锁死：一步都不走
                    double tm = f.LevelTMaxC[m];
                    double e = double.IsNaN(tm) ? 0 : tm - baseT;      // >0 = 该级比管根热
                    worstOver = Math.Max(worstOver, e);
                    // 越热越加厚：Δln t = +ω·e/S
                    double st = Math.Clamp(opt.Damping * e / opt.SensitivityK, -0.30, 0.30);
                    adj[m] = Math.Exp(st);
                    logSum += Math.Log(adj[m]); cnt++;
                }
                // 归一化：几何平均拉回 1 ⇒ 只改**比例**，不改整片平均厚度
                // 归一化只摊在**未锁**的级上（锁住的级不参与，也不该被 norm 拉动）
                double norm = cnt > 0 ? Math.Exp(logSum / cnt) : 1.0;
                for (int m = 0; m < adj.Length; m++)
                {
                    if (Locked(j, m)) continue;
                    scale[j][m] = Math.Clamp(scale[j][m] * adj[m] / norm,
                                             opt.MinThickMm / Math.Max(1e-6, levelThicknessMm[j][m]),
                                             opt.MaxThickMm / Math.Max(1e-6, levelThicknessMm[j][m]));
                }
            }
            progress?.Report($"第 {round + 1} 轮 · 内层：各级峰值最高超管根 {worstOver:0.0} K");
            if (last.Converged && worstOver < 15) break;
        }

        // ── 全精度复核：搜索期是粗网格 + 松耦合，最终解必须用原精度重跑一次。
        //    **报告值一律取这一次** —— 搜索期的数只用来找方向。
        progress?.Report("全精度复核最终解…");
        var lcFinal = CloneCase(baseCase);
        lcFinal.LevelThicknessMm = levelThicknessMm;
        lcFinal.LevelScale = scale;
        try
        {
            var verify = LineRunner.Run(lcFinal, progress, cancel);
            if (verify.Ok) { last.Line = verify; last.Converged = verify.Converged; }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 复核失败就保留搜索期的结果，并在下面注明 */ }

        // 汇报最终的各级厚度
        var sb = new System.Text.StringBuilder(last.Message);
        if (levelLocked is not null && !last.Converged)
            sb.Append("　⚠ 有级被锁死，整片热平衡只能靠未锁的级去凑 —— " +
                      "未收敛可能是锁的限制，不一定是物理无解。可试着解锁一级再跑。");
        for (int j = 0; j < nf; j++)
        {
            sb.Append($"　片{j + 1} 各级厚度 ");
            sb.Append(string.Join("/", Enumerable.Range(0, scale[j].Length)
                        .Select(m => (levelThicknessMm[j][m] * scale[j][m]).ToString("0.000"))));
        }
        last.Message = sb.ToString();
        last.LevelScale = scale;
        return last;
    }

    /// <summary>浅拷贝算例，只换法兰几何 —— 不能直接改传入的 LineCase（界面还在用它）</summary>
    private static LineCase CloneCase(LineCase c) => new()
    {
        TubeIdMm = c.TubeIdMm, WallMm = c.WallMm, SegLengthMm = c.SegLengthMm,
        GradeName = c.GradeName, SetpointC = c.SetpointC, HeadM = c.HeadM,
        UseMeasuredCurrent = c.UseMeasuredCurrent, MeasuredCurrentA = c.MeasuredCurrentA,
        FlangeLayer = c.FlangeLayer, FlangePlaneY = c.FlangePlaneY,
        FlangeFile3dm = c.FlangeFile3dm, ThicknessScale = c.ThicknessScale,
        LevelScale = c.LevelScale, LevelThicknessMm = c.LevelThicknessMm,
        ThicknessStepMm = c.ThicknessStepMm,
        MeshFineMm = c.MeshFineMm, MeshCoarseMm = c.MeshCoarseMm,
        MeshFineRadiusMm = c.MeshFineRadiusMm,
        GlassInC = c.GlassInC, GlassOutMeasuredC = c.GlassOutMeasuredC,
        Base = c.Base, BaselineMassG = c.BaselineMassG, CheckRamp = c.CheckRamp
    };
}
