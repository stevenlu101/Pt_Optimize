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
    /// <summary>盘半径**第一轮**一步走多少 mm。之后由 <see cref="Refine"/> 逐次减半。</summary>
    public const double DiscStepMm = 5.0;

    /// <summary>舌宽比例**第一轮**一步走多少（半宽 / 盘半径）。同样逐次减半。</summary>
    public const double FracStep = 0.125;

    /// <summary>
    /// ★★ **步长收缩**（2026-08-28 算法普查 A 类第 ⑥ 条）。
    ///
    /// 病灶：此前步长写死 5 mm 且**永不收缩**，停机条件是「四个邻点都不更好」。
    /// 那句话真正的意思只是「**在 ±5 mm 这个分辨率上**没有更好」——
    /// 而铂重对盘径是**一阶敏感**的，5 mm 完全可能跨过最优点。
    /// 把它当成「局部最优」，与把 2 mm 网格上的判据当成结论是同一种错。
    ///
    /// 改法与网格无关性同构：**没有更好 ⇒ 步长减半再试**，直到步长落到
    /// <see cref="MinDiscStepMm"/>。于是停机条件变成一句能写进交付的话：
    /// 「在 ±<c>MinDiscStepMm</c> mm 分辨率上没有更好」——**分辨率是声明出来的，
    /// 不是碰巧的**。
    /// </summary>
    public static double Refine(double step) => step * 0.5;

    /// <summary>
    /// 形状分辨率下界 mm。5 → 2.5 → 1.25 → 0.625，减半三次。
    /// 0.625 mm 已远细于法兰下料公差，再细没有工程意义。
    /// **这个数必须印在交付里**，因为「局部最优」这句话只在它上面成立。
    /// </summary>
    public const double MinDiscStepMm = 0.625;

    /// <summary>步长收到头了吗 —— 收到头才谈得上「局部最优」。</summary>
    public static bool StepExhausted(double step) => step < MinDiscStepMm - 1e-9;

    /// <summary>
    /// 把第 1 轮网格里**造不出来**的盘半径抬到下界上。
    ///
    /// ★ 病灶（2026-08-25 查出）：界面的网格写死 {25, 30, 35}，
    ///   而判据⑥ 的下界是 25 + 2×壁厚 —— 壁 0.6 要 26.2、壁 0.8 要 26.6。
    ///   ⇒ **R25 对两个现役档都会被跳过**，那一点从来没算过，
    ///   第 1 轮实际只探了 R30/R35 两个盘径。网格少了三分之一而没人知道。
    ///
    /// ⚠ 抬上来而不是删掉：网格的作用是**给出发点与方向**，
    ///   点少一个，方向就少一个依据。而且下界那一点恰恰是最省铂的方向。
    /// ⚠ 只抬**低于下界**的；已经合法的点原样保留 —— 不要顺手把整个网格重排，
    ///   那会把「这次改动」和「搜索策略变了」混成一件事。
    /// </summary>
    public static double[] LiveDiscs(IReadOnlyList<double> grid, double minDiscMm)
    {
        // 下界通常是 26.2 / 26.6 这样的零头，向上取到 0.5 mm —— 图纸上不画三位小数。
        double lo = Math.Ceiling(minDiscMm * 2 - 1e-9) / 2.0;
        var outp = new List<double>();
        foreach (double g in grid ?? Array.Empty<double>())
            outp.Add(g < minDiscMm - 1e-9 ? lo : g);
        if (outp.Count == 0) outp.Add(lo);
        return outp.Distinct().OrderBy(v => v).ToArray();
    }


    /// <summary>
    /// 从当前最好点出发的四个邻点：盘径 ±一步（**舌宽比例不变**）、舌宽比例 ±一步。
    ///
    /// ⚠ 盘径变化时半宽要**按比例跟着走**，不能保持绝对值：
    ///   等宽舌片的半宽受盘半径限制（半宽 &gt; 盘半径就没有切点，会被静默夹住），
    ///   保持绝对值往小盘径走会撞上那个夹持，等于试了一个自己都说不清的形状。
    /// ⚠ 比例上限 1.0（半宽 = 盘半径）、下限 0.25：再窄就没有过流截面可言。
    /// </summary>
    public static (double R, double HalfW)[] Neighbours(double R0, double hw0)
        => Neighbours(R0, hw0, DiscStepMm);

    /// <summary>
    /// 同上，但步长由调用方给 —— 步长收缩（<see cref="Refine"/>）要用这个重载。
    /// 舌宽比例的步长**按同样比例缩**，两个方向不能一个细一个粗，
    /// 否则「四个邻点都不更好」在两个方向上说的不是同一件事。
    /// </summary>
    public static (double R, double HalfW)[] Neighbours(double R0, double hw0, double stepMm)
    {
        double f0 = R0 > 1e-9 ? hw0 / R0 : 1.0;
        double fs = FracStep * (stepMm / DiscStepMm);
        double up = Math.Min(1.0, f0 + fs), dn = Math.Max(0.25, f0 - fs);
        return new[]
        {
            (R0 + stepMm, (R0 + stepMm) * f0),
            (R0 - stepMm, (R0 - stepMm) * f0),
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

    /// <summary>
    /// 把邻点滤成「值得一试」的：几何上说得通、且没算过。
    ///
    /// ⚠ 无参版用的 <c>R &gt; 5</c> 是个**挑出来的数**，不是几何下界 ——
    ///   真正的下界是判据⑥：盘半径 ≥ 管孔半径 + 焊脚。用 <see cref="Worth(IEnumerable{ValueTuple{double,double}}, ISet{string}, double)"/>
    ///   把那个下界传进来，才不会把「造不出来的形状」当候选算上几十分钟。
    /// </summary>
    public static List<(double R, double HalfW)> Worth(
        IEnumerable<(double R, double HalfW)> cand, ISet<string> seen)
        => Worth(cand, seen, 5.0);

    /// <summary>同上，但盘半径下界由调用方按判据⑥ 给出（第一性原理，不是挑的数）。</summary>
    public static List<(double R, double HalfW)> Worth(
        IEnumerable<(double R, double HalfW)> cand, ISet<string> seen, double minDiscMm)
        => cand.Where(c => c.R > minDiscMm && c.HalfW > 1 && seen.Add(Key(c.R, c.HalfW))).ToList();
}
