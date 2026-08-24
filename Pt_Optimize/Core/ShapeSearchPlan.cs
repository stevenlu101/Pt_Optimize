using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 「搜形状」每一轮的**决策**：下一轮试哪几个形状、这一轮算不算变好。
///
/// ★ 为什么抽成纯函数（2026-08-25，用户：「改变法兰直径与扫梯度分布**算一轮**」，
///   「有好的方向则继续，如果都是变坏即刻停止」）：
///   搜形状一轮是**几十分钟**（每个候选都要把梯度分布扫一遍）。
///   把「试哪几个 / 停不停」写在那个循环里，就只能靠跑满几十分钟才验得到 ——
///   而这两件事本身是纯算术，微秒可验。
///   **同一个教训今天第五次**（TrendOf / LevelThicknessFor / JointThickness /
///   WorseningRun 都是这么抽出来的）。
/// </summary>
public static class ShapeSearchPlan
{
    /// <summary>盘半径一步走多少 mm。</summary>
    public const double DiscStepMm = 5.0;

    /// <summary>舌宽比例一步走多少（半宽 / 盘半径）。</summary>
    public const double FracStep = 0.125;

    /// <summary>
    /// 从当前最好点出发的四个邻点：盘径 ±一步（**舌宽比例不变**）、舌宽比例 ±一步。
    ///
    /// ⚠ 盘径变化时半宽要**按比例跟着走**，不能保持绝对值：
    ///   等宽舌片的半宽受盘半径限制（半宽 &gt; 盘半径就没有切点，会被静默夹住），
    ///   保持绝对值往小盘径走会撞上那个夹持，等于试了一个自己都说不清的形状。
    /// ⚠ 比例上限 1.0（半宽 = 盘半径）、下限 0.25：再窄就没有过流截面可言。
    /// </summary>
    public static (double R, double HalfW)[] Neighbours(double R0, double hw0)
    {
        double f0 = R0 > 1e-9 ? hw0 / R0 : 1.0;
        double up = Math.Min(1.0, f0 + FracStep), dn = Math.Max(0.25, f0 - FracStep);
        return new[]
        {
            (R0 + DiscStepMm, (R0 + DiscStepMm) * f0),
            (R0 - DiscStepMm, (R0 - DiscStepMm) * f0),
            (R0, R0 * up),
            (R0, R0 * dn),
        };
    }

    /// <summary>
    /// 这一轮算不算「变好」。<paramref name="tolG"/> 是**不算改善的门槛**：
    /// 省下不到 0.5 g 就再多跑一轮几十分钟，不划算，且那点差已经落进数值噪声。
    /// </summary>
    public static bool Improved(double beforeG, double afterG, double tolG = 0.5)
        => !double.IsNaN(afterG) && !double.IsNaN(beforeG) && afterG < beforeG - tolG;

    /// <summary>形状的去重键 —— 同一个形状不该被算两次（每次都是几十分钟）。</summary>
    public static string Key(double R, double halfW) => $"{R:0.###}/{halfW:0.###}";

    /// <summary>把邻点滤成「值得一试」的：几何上说得通、且没算过。</summary>
    public static List<(double R, double HalfW)> Worth(
        IEnumerable<(double R, double HalfW)> cand, ISet<string> seen)
        => cand.Where(c => c.R > 5 && c.HalfW > 1 && seen.Add(Key(c.R, c.HalfW))).ToList();
}
