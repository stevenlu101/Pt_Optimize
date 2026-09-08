using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **截面电流密度：按 J = 10 定尺寸，终验全体 &lt; 11**（2026-09-08，用户给的设计因果链第 ②／④ 步）。
///
/// 用户原话：「在 20 °C/h 下所须通过铂金管的电流是多少 → 再去决定铂金法兰大小与尺寸（r1/t1/r2/t2）与舌片横截面积
/// [按 J=10 为限制] → 分析温场与电场定孔 → 最后复核：核算电流通过**截面**的电流密度，全体必须小于 11。」
///
/// ══ 什么是「截面」
/// 电流路径上每一个**必经**的切口：
///   · 舌片：沿舌轴每一处 x 的横截面（宽 × 厚），开孔处扣掉孔的弦长；压接段（铜排短接）不算
///   · 舌盘交界：切点处的弦 × 厚
///   · 圆盘：绕管孔的每一圈 r（周长 × 该级厚度），减重槽带扣掉槽的弧长
/// 截面电流密度 J = I_设计 / A_截面，**闭式、与网格无关** ⇒ 当场判得了。
///
/// ══ 它推翻了什么（按用户第 8 条：可以推翻不当的逻辑，要说出来）
/// 前任把「法兰 J」定义成电流场的**逐点峰值**（凹角处 37 A/mm²，随网格涨、无收敛平台），追网格到收敛才肯判
/// ⇒ 永远「判不了」，挡住每一档交付。逐点峰值是局部发热问题，由温度场（含横向导热）与熔化门管；
/// 尺寸规则看的是截面。场峰值现在降为诊断量，给第 ③ 步（电流密度低处定孔）用。
///
/// ══ 单调性
/// 所有截面积都正比于板厚（舌片 t、各级 t×倍率、弦 t）⇒ 给定电流，J 对板厚严格递减 ⇒
/// 板厚下界 = 当前厚度 × max_截面(J/10)，闭式一步到位，是约束盒下角的一个来源。
/// 孔径与槽张角**减小**截面 ⇒ 它们的上界也由这里给（比桥宽那条更紧的那个生效）。
/// </summary>
public static class SectionSizing
{
    /// <summary>设计用电流密度 A/mm²（用户 2026-09-08：按 J=10 为限制定尺寸）。</summary>
    public const double JDesignAPerMm2 = 10.0;
    /// <summary>终验限值 A/mm²（用户 2026-09-08：全体必须小于 11）。</summary>
    public const double JCheckAPerMm2 = 11.0;

    /// <summary>一个截面：在哪、面积、电流密度。</summary>
    public readonly record struct Cut(string Where, double AreaMm2, double JAPerMm2);

    /// <summary>
    /// 全部必经截面。<paramref name="currentA"/> 是这一片的设计电流（共用片已矢量合成）。
    /// <paramref name="clampLenMm"/>：舌端压接段长度，段内由铜排短接、不算截面。
    /// </summary>
    public static List<Cut> Cuts(FlangePlate g, double currentA, double clampLenMm = 0)
    {
        var cuts = new List<Cut>();
        if (!(currentA > 0)) return cuts;
        double tTab = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm;
        var (xT, hwT) = g.Tangent();

        // ── 舌片：从压接段末端到切点，沿 x 采样（孔心处必采）
        double x0 = g.TabTipXMm + Math.Max(0, clampLenMm), x1 = xT;
        if (x1 > x0 + 1e-9)
        {
            var xs = new SortedSet<double>();
            const int N = 240;
            for (int i = 0; i <= N; i++) xs.Add(x0 + (x1 - x0) * i / N);
            foreach (var h in g.TabHoles) { xs.Add(Math.Clamp(h.XMm, x0, x1)); }
            foreach (double x in xs)
            {
                double w = 2 * g.HalfWidth(x);
                foreach (var h in g.TabHoles)
                {
                    double rx = h.RMm * Math.Max(1e-9, h.AspectXZ);          // 顺流拉长在 x 方向
                    double dx = (x - h.XMm) / rx;
                    if (Math.Abs(dx) < 1) w -= 2 * h.RMm * Math.Sqrt(1 - dx * dx);   // 扣掉孔的弦
                }
                if (w <= 1e-9) { cuts.Add(new Cut($"舌片 x={x:0.#}（被孔切断）", 0, double.PositiveInfinity)); continue; }
                double a = w * tTab;
                cuts.Add(new Cut($"舌片 x={x:0.#}", a, currentA / a));
            }
        }

        // ── 舌盘交界：切点处的弦
        {
            double a = 2 * hwT * Math.Max(g.ThicknessAt(xT, 0), 1e-9);
            cuts.Add(new Cut($"舌盘交界弦 x={xT:0.#}", a, currentA / a));
        }

        // ── 圆盘：绕管孔每一圈（焊脚之外到盘缘），槽带扣掉槽的弧长
        {
            double rIn = g.HoleRadiusMm + Math.Max(0, g.WeldFilletLegMm), rOut = g.DiscRadiusMm;
            if (rOut > rIn + 1e-9)
            {
                var rs = new SortedSet<double>();
                const int M = 120;
                for (int i = 0; i <= M; i++) rs.Add(rIn + (rOut - rIn) * i / M);
                foreach (double r in g.DiscStepRadiiMm) if (r > rIn && r < rOut) { rs.Add(r - 1e-6); rs.Add(r + 1e-6); }
                foreach (var s in g.DiscSlots) { if (s.RInMm > rIn && s.RInMm < rOut) rs.Add(s.RInMm + 1e-6); if (s.ROutMm > rIn && s.ROutMm < rOut) rs.Add(s.ROutMm - 1e-6); }
                foreach (double r in rs)
                {
                    double arc = 2 * Math.PI * r;
                    foreach (var s in g.DiscSlots)
                        if (r >= s.RInMm && r <= s.ROutMm) arc -= r * s.SpanDeg * Math.PI / 180.0;
                    // 厚度取舌片对侧（+x，槽心方向）之外的一点：−x 轴在盘上是各级台阶的厚度
                    double t = Math.Max(g.ThicknessAt(-r, 0), 1e-9);
                    if (arc <= 1e-9) { cuts.Add(new Cut($"圆盘 r={r:0.#}（被槽切断）", 0, double.PositiveInfinity)); continue; }
                    double a = arc * t;
                    cuts.Add(new Cut($"圆盘 r={r:0.#}", a, currentA / a));
                }
            }
        }
        return cuts;
    }

    /// <summary>最紧的截面（J 最大）。</summary>
    public static Cut Worst(FlangePlate g, double currentA, double clampLenMm = 0)
    {
        var cuts = Cuts(g, currentA, clampLenMm);
        if (cuts.Count == 0) return new Cut("（无截面：电流为 0）", double.NaN, double.NaN);
        return cuts.OrderByDescending(c => c.JAPerMm2).First();
    }

    /// <summary>
    /// 板厚下界（按 J = 10）：截面积都正比于板厚 ⇒ t_min = t_now × max_截面(J/10)。
    /// 返回值已是**基板厚**（各级倍率保持不变时，环的截面随基板同比例长）。
    /// </summary>
    public static double PlateThickFloorMm(FlangePlate g, double tNowMm, double currentA, double clampLenMm = 0,
                                           double jDesign = JDesignAPerMm2)
    {
        var w = Worst(g, currentA, clampLenMm);
        if (double.IsNaN(w.JAPerMm2) || double.IsInfinity(w.JAPerMm2)) return tNowMm;
        return tNowMm * w.JAPerMm2 / jDesign;
    }

    /// <summary>孔径上界（按 J = 10）：孔心处 (宽 − 2R)·t ≥ I/10 ⇒ R ≤ (宽 − I/(10 t))/2。</summary>
    public static double HoleRadiusMaxByJMm(FlangePlate g, double holeXMm, double currentA)
    {
        double tTab = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm;
        double w = 2 * g.HalfWidth(holeXMm);
        double need = currentA / (JDesignAPerMm2 * Math.Max(tTab, 1e-9));
        return Math.Max(0, 0.5 * (w - need));
    }

    /// <summary>槽张角上界（按 J = 10）：槽带各圈 (2πr − r·θ)·t ≥ I/10 ⇒ θ ≤ 2π − I/(10 t r)，取槽带内最紧的一圈。</summary>
    public static double SlotSpanMaxByJDeg(FlangePlate g, double rInMm, double rOutMm, double currentA)
    {
        if (!(rOutMm > rInMm) || !(currentA > 0)) return 360.0;
        double best = 360.0;
        const int M = 40;
        for (int i = 0; i <= M; i++)
        {
            double r = rInMm + (rOutMm - rInMm) * i / M;
            double t = Math.Max(g.ThicknessAt(-r, 0), 1e-9);
            double thetaRad = 2 * Math.PI - currentA / (JDesignAPerMm2 * t * r);
            best = Math.Min(best, Math.Max(0, thetaRad) * 180.0 / Math.PI);
        }
        return best;
    }
}
