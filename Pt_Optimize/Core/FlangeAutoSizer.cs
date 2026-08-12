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
    /// 迭代求解四片厚度。<paramref name="makePlate"/> 把厚度变成几何 ——
    /// 由调用方提供，于是本类不关心形状（圆盘/舌片/阶梯都行）。
    /// </summary>
    public static Result Solve(LineCase baseCase, Func<double, FlangePlate> makePlate,
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
            lc.FlangePlates = t.Select(makePlate).ToArray();

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
        ThicknessStepMm = c.ThicknessStepMm,
        MeshFineMm = c.MeshFineMm, MeshCoarseMm = c.MeshCoarseMm,
        MeshFineRadiusMm = c.MeshFineRadiusMm,
        GlassInC = c.GlassInC, GlassOutMeasuredC = c.GlassOutMeasuredC,
        Base = c.Base, BaselineMassG = c.BaselineMassG, CheckRamp = c.CheckRamp
    };
}
