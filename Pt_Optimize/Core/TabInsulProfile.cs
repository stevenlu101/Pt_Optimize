using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★ R48 R（2026-09-17，Opus 5）：**沿舌轴 x 的分段常值舌保温剖面**。
///
/// ══ 为什么要它
///
/// 此前舌保温**每片只有一个数**（<see cref="FlangePlate.TabInsulThickMm"/>／<see cref="LineCase.TabInsul3dmPerPlateMm"/>）：
/// 整条舌板从圆盘切点一路包到舌尖都是同一厚度。实测（r48_L、r48_P 两路）这根旋钮是刀刃 ——
/// 管孔净流入对它约 −8～−10 W/mm，可行窗口只有 0.1～0.2 mm，连一个 0.5 mm 的现场包法档都落不进去。
/// 病根不在旋钮本身，在**自由度只有一个**：一个数要同时满足「热端少散热」与「冷端把热交给夹头」两件事。
/// 给它一条沿 x 的剖面，两件事就能分开办。
///
/// ══ 约定（写死，别改）
///
/// · <see cref="EdgeXMm"/> 沿舌轴**严格递减**（舌板长在 −x 方向：圆盘切点 x≈0 → 舌尖 x = −舌长）。
///   长度 = 段数 + 1。第 i 段覆盖 [EdgeXMm[i+1], EdgeXMm[i])（边界上那一点归**靠圆盘那一侧**的段；
///   格子形心正好落在边界上是零测度事件，这条只为「同一个 x 永远只有一个答案」）。
/// · 超出两端一律**钳住**：x ≥ EdgeXMm[0] ⇒ 第 0 段；x ≤ EdgeXMm[^1] ⇒ 最后一段。
///   ⇒ 剖面只铺自由段时，压接段（x &lt; 最后一条边）自动沿用最后一段的厚度。
/// · 厚度的物理口径与单值**完全一样**：进 <see cref="DesignScreen.PlateFluxWPerM2"/> 的那个 insulThickMm，
///   包不包仍按 <see cref="DesignScreen.FlangeFaceInsulated"/>（≥ 0.05 mm）。**只换厚度取值，不换散热配方。**
/// · 剖面为 null ⇒ 走单值那条原路径，**逐位不变**（门：R48TabInsulProfileGateTests）。
/// </summary>
public sealed class TabInsulProfile
{
    /// <summary>分段边界 x（mm，沿舌轴严格递减；长度 = 段数 + 1）。</summary>
    public readonly double[] EdgeXMm;

    /// <summary>各段保温厚度 mm（长度 = 段数）。</summary>
    public readonly double[] ThickMm;

    public TabInsulProfile(double[] edgeXMm, double[] thickMm)
    {
        if (edgeXMm is null) throw new ArgumentNullException(nameof(edgeXMm));
        if (thickMm is null) throw new ArgumentNullException(nameof(thickMm));
        if (thickMm.Length < 1) throw new ArgumentException("舌保温剖面至少要有一段");
        if (edgeXMm.Length != thickMm.Length + 1)
            throw new ArgumentException($"舌保温剖面：边界 {edgeXMm.Length} 条、段 {thickMm.Length} 段，边界数必须 = 段数 + 1");
        for (int i = 1; i < edgeXMm.Length; i++)
            if (!(edgeXMm[i] < edgeXMm[i - 1]))
                throw new ArgumentException($"舌保温剖面的边界必须沿舌轴严格递减（舌板长在 −x）：第 {i} 条 {edgeXMm[i]:0.###} 不小于第 {i - 1} 条 {edgeXMm[i - 1]:0.###}");
        foreach (double t in thickMm)
            if (double.IsNaN(t) || t < 0) throw new ArgumentException($"舌保温剖面的厚度不许是 NaN 或负数：{t}");
        EdgeXMm = (double[])edgeXMm.Clone();
        ThickMm = (double[])thickMm.Clone();
    }

    public int SegmentCount => ThickMm.Length;

    /// <summary>x 落在第几段（两端钳住）。</summary>
    public int IndexAt(double x)
    {
        for (int i = 0; i < ThickMm.Length; i++)
            if (x >= EdgeXMm[i + 1]) return i;
        return ThickMm.Length - 1;
    }

    /// <summary>x 处的舌保温厚度 mm（两端钳住）。</summary>
    public double At(double x) => ThickMm[IndexAt(x)];

    /// <summary>每一段都等于同一个值（与单值口径等价）。</summary>
    public bool IsUniform(double mm) => ThickMm.All(t => t.Equals(mm));

    /// <summary>整条剖面同时平移 delta mm（下限 0 —— 负厚度没有物理意义；钳住时钳住的段数由调用方自己数）。</summary>
    public TabInsulProfile Shifted(double deltaMm)
        => new(EdgeXMm, ThickMm.Select(t => Math.Max(0.0, t + deltaMm)).ToArray());

    /// <summary>只改第 k 段。</summary>
    public TabInsulProfile WithSegment(int k, double mm)
    {
        var t = (double[])ThickMm.Clone();
        t[Math.Clamp(k, 0, t.Length - 1)] = mm;
        return new TabInsulProfile(EdgeXMm, t);
    }

    /// <summary>全段等厚的剖面（给「剖面 ≡ 单值」的门用）。</summary>
    public static TabInsulProfile Uniform(double[] edgeXMm, double mm)
        => new(edgeXMm, Enumerable.Repeat(mm, edgeXMm.Length - 1).ToArray());

    /// <summary>沿 [x0, x1]（x0 &gt; x1）等分 n 段。</summary>
    public static double[] EvenEdges(double x0Mm, double x1Mm, int n)
    {
        if (n < 1) throw new ArgumentException("段数至少 1");
        var e = new double[n + 1];
        for (int i = 0; i <= n; i++) e[i] = x0Mm + (x1Mm - x0Mm) * i / n;
        return e;
    }

    /// <summary>
    /// ★★ R48 R（2026-09-17，Opus 5）：[x1, x0]（x0 &gt; x1）上**按长度加权的平均厚度** mm —— 只给**集总模型**用。
    ///
    /// 谁用它：整片热稳定 <see cref="FlangeStability"/> 与升温两节点 <see cref="RampTwoNode"/>。
    /// 这两个模型整片只有一个节点、舌保温只能吃一个数；剖面进不去。
    /// ⚠ 这是**有意的近似**，不是等价代换：散热对厚度是非线性的（薄段散得多），
    ///   按长度平均会**低估**薄段的散热。二维场解（<see cref="ShellThermal.Solve"/>）不走这条，它逐格取真厚度。
    /// ⇒ 剖面在场里是精确的，在这两个集总量上是近似的；报告里必须写明这一句。
    /// 剖面为 null 时这条路根本不走（<see cref="PlateThermalSetup.TabInsulLumpedMm"/> 直接回单值）⇒ 默认口径逐位不变。
    /// </summary>
    public double LengthWeightedMeanMm(double x0Mm, double x1Mm)
    {
        double lo = Math.Min(x0Mm, x1Mm), hi = Math.Max(x0Mm, x1Mm);
        double span = hi - lo;
        if (!(span > 0)) return At(hi);
        int n = ThickMm.Length;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            // 两端钳住（与 At 同一条规则）：第 0 段向上到 +∞，末段向下到 −∞
            double segHi = i == 0 ? double.PositiveInfinity : EdgeXMm[i];
            double segLo = i == n - 1 ? double.NegativeInfinity : EdgeXMm[i + 1];
            double a = Math.Max(lo, segLo), b = Math.Min(hi, segHi);
            if (b > a) sum += ThickMm[i] * (b - a);
        }
        return sum / span;
    }

    public string Describe()
        => string.Join("　", Enumerable.Range(0, SegmentCount)
              .Select(i => $"x {EdgeXMm[i]:0.#}～{EdgeXMm[i + 1]:0.#}：{ThickMm[i]:0.00} mm"));

    public TabInsulProfile Clone() => new(EdgeXMm, ThickMm);
}
