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

    /// <summary>浅拷贝算例，只换法兰几何 —— 不能直接改传入的 LineCase（界面还在用它）</summary>
    private static LineCase CloneCase(LineCase c) => new()
    {
        TubeIdMm = c.TubeIdMm, WallMm = c.WallMm, SegLengthMm = c.SegLengthMm,
        GradeName = c.GradeName, SetpointC = c.SetpointC, HeadM = c.HeadM,
        UseMeasuredCurrent = c.UseMeasuredCurrent, MeasuredCurrentA = c.MeasuredCurrentA,
        FlangeLayer = c.FlangeLayer, FlangePlaneY = c.FlangePlaneY,
        FlangeFile3dm = c.FlangeFile3dm, ThicknessScale = c.ThicknessScale,
        ThicknessStepMm = c.ThicknessStepMm,
        MeshFineMm = c.MeshFineMm, MeshCoarseMm = c.MeshCoarseMm,
        MeshFineRadiusMm = c.MeshFineRadiusMm,
        GlassInC = c.GlassInC, GlassOutMeasuredC = c.GlassOutMeasuredC,
        Base = c.Base, BaselineMassG = c.BaselineMassG, CheckRamp = c.CheckRamp
    };
}
