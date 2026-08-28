using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// **动态网格求解器的决策层** —— 纯函数，因为每一次解都是分钟级的，
/// 而「下一步该多细、停没停」是纯算术，微秒可验（本项目一贯做法）。
///
/// ★★ 为什么必须有它（2026-08-28 实测，用户：「那还等啥！做」）
///
///   `--meshconv` 在定案 0.8 档上量到：
/// <code>
///   细网格mm  单元数   ②′W      ②″K      ③K
///   4.00       275    4.377   −0.002   13.439
///   2.00       676    1.123   −0.208    5.182   ← **定案用的就是这一档**
///   1.00      1866    2.655   −0.077    9.656
///   Δ(2→1)           +1.533   +0.131   **+4.474**
///   容差              0.5      0.2       1.0
/// </code>
///   ⇒ ③ 的离散误差是容差的 **4.5 倍**，②′ 是 **3 倍**，而且**不单调**
///     （③ 走 13.4 → 5.2 → 9.7，是在摆）。
///   ⇒ **判据值根本没有网格无关**。定案报的 ③ = 5.18/10（看着 48 % 裕度），
///     在 1 mm 上是 9.66/10（只剩 3.4 %）。2 mm 那一档恰好落在一个舒服的位置。
///
///   这条盖过本轮其它所有发现：热收支 2.66 W、夹持温度假设、屈曲宽度，
///   量级都比这个离散误差小。
///
/// ⚠ 本类**不**做逐格 AMR：ShellMesh 是**分级结构网格**
///   （细区 hFine 在 ±fineRadius 内，外面 hCoarse），能调的就是这三个。
///   所以这里做的是**自适应分区**：由几何特征与解的峰位决定细区多细、多大。
/// </summary>
public static class MeshAdapt
{
    /// <summary>
    /// 一个几何特征至少要跨几格才算「解得出来」。
    /// 3 是有限体积里表达一个台阶/圆角的最低要求（进、出、中各一格）；
    /// 少于它，那个特征在场里就是个数值噪声。
    /// </summary>
    public const int CellsPerFeature = 3;

    /// <summary>
    /// 由**几何特征**定的最粗可用网格 mm：最小特征 ÷ <see cref="CellsPerFeature"/>。
    ///
    /// ★ 直接起因：舌根圆角 R3 与环宽 3 mm 落在 2 mm 网格上各只有 **1.5 格**，
    ///   而 FinalDesign 自己写着「网格 2 mm，**小于它的圆角在场里看不出来**」，
    ///   ②″ 的峰又**可能就落在舌根凹角** ⇒ 优化器在调一个自己分辨不出来的几何。
    ///
    /// ⚠ 只看几何，不看解。它给的是**下限**：满足它只是「特征画得出来」，
    ///   离「答案不再随网格变」还差一步 —— 那要靠 <see cref="Converged"/>。
    /// </summary>
    public static double RequiredFineMm(IEnumerable<double> featureSizesMm)
    {
        var fs = (featureSizesMm ?? Array.Empty<double>()).Where(x => x > 1e-9).ToArray();
        if (fs.Length == 0)
            throw new ArgumentException("没有给任何几何特征尺寸 —— 无法判断网格够不够细。"
                + "宁可拒答，也不给一个「看起来够」的默认值。");
        return fs.Min() / CellsPerFeature;
    }

    /// <summary>某个特征在给定网格上跨几格（&lt; 1 就是看不出来）。</summary>
    public static double CellsAcross(double featureMm, double fineMm) =>
        fineMm > 1e-12 ? featureMm / fineMm : double.PositiveInfinity;

    /// <summary>一次加密：对半。结构网格上单元数约 ×4，是可承受的最陡步长。</summary>
    public static double Refine(double fineMm) => fineMm * 0.5;

    /// <summary>判据在两档网格之间动了多少 —— 与各自容差比。</summary>
    public sealed class Delta
    {
        public string Name = "";
        public double Change, Tol;
        public bool Within => Math.Abs(Change) <= Tol;
    }

    /// <summary>
    /// 收敛判据 = **判据本身**：每一条判据在相邻两档网格之间的变化都落进它自己的容差。
    ///
    /// ⚠ 不用「残差」「单元数」这类替身：我们要的不是「网格看起来够密」，
    ///   而是「**这个判据的数不再随网格变**」。那才叫算出来了。
    /// ⚠ 空集不算收敛 —— 「没有判据可比」被当成「都通过了」是本项目记过案的
    ///   「空集恒真」那一型。
    /// </summary>
    public static bool Converged(IReadOnlyList<Delta> deltas)
        => deltas is { Count: > 0 } && deltas.All(d => d.Within);

    /// <summary>
    /// 细区半径 mm：必须**盖住峰所在的地方**，否则峰落在粗区，加密再多也没用。
    /// 取「最远的那个峰 + 余量」，并有下限（管孔周围永远要在细区里）。
    /// </summary>
    public static double RequiredFineRadiusMm(IEnumerable<double> peakRadiiMm,
                                              double holeRadiusMm, double marginMm = 10.0)
    {
        double far = holeRadiusMm;
        foreach (double r in peakRadiiMm ?? Array.Empty<double>())
            if (r > far) far = r;
        return far + marginMm;
    }

    /// <summary>
    /// 一句话结论。**没收敛就必须明说**，不许含糊 ——
    /// 「加密到上限仍在动」和「已经不动了」是完全不同的两件事，
    /// 而交付的人只看这一句。
    /// </summary>
    public static string Verdict(double fineMm, IReadOnlyList<Delta> deltas, bool hitCap)
    {
        if (deltas is not { Count: > 0 })
            return "**说不出是否收敛** —— 一条判据都没比到（空集不算通过）";
        var bad = deltas.Where(d => !d.Within).ToArray();
        if (bad.Length == 0)
            return $"✓ **网格无关**：在 {fineMm:0.00} mm 上，每条判据的变化都落进各自容差 "
                 + $"（{string.Join("／", deltas.Select(d => $"{d.Name} {d.Change:+0.000;−0.000}/{d.Tol:0.###}"))}）";
        return (hitCap ? "✗ **加密到上限仍未收敛**" : "✗ **尚未网格无关**")
             + $"：{string.Join("／", bad.Select(d => $"{d.Name} 动了 {d.Change:+0.000;−0.000}（容差 {d.Tol:0.###}）"))}"
             + "　⇒ 这些判据值**还带着离散误差**，不能当作算准了的数";
    }
}
