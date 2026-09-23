using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// **.3dm 图纸的解析替身** —— 把 <see cref="PlateShapeAnalyzer.Shape"/> 造成一片
/// <see cref="FlangePlate"/>，让定尺寸器的搜索期可以不碰图纸几何。
///
/// ⚠ **先说它不能干什么**：它省不了多少时间。实测（`--surrogate --bench`）每片
///   3.1 ms（.3dm，读缓存 + 建网格）vs 2.7 ms（解析），只差 13 %；
///   <see cref="Geometry3dm.LoadThickness"/> 本来就带缓存，Geom 子进程每个文件只起一次。
///   一次整线解的开销在几百轮耦合场解上，几何这一步微不足道。
///   ——「不必每次评估都起子进程」这个说法**是错的**，别照它推断。
///
/// ★★★★★ 这是一件**天然危险**的事，所以本类的重点不是「怎么造」，是「怎么证明它能用」。
///
/// 危险在哪：优化器若在替身上求解，而出图与判据用的是原图，那就是
/// 「解的是 A、判的是 B」——本项目 §1.8「安静失败」的标准形态。
/// 它不会报错，只会给出一组**针对另一片板**算出来的厚度。
/// 同样的界线在 <see cref="LineCase.GeomForJudge"/> 那里已经画过一次：
/// 那片等效几何只喂判据、**绝不碰 FlangePlates**。
///
/// 所以替身只有在**被量过、且量出来足够接近**时才可用。判「接近」不看轮廓像不像，
/// 看**电学上像不像** —— 定尺寸器关心的只有三件事：
///   · 净面积（决定质量与散热面）
///   · <see cref="DesignScreen.ShapeFactors.ShapeR"/>（电阻形状因子 ⇒ 发热总量）
///   · <see cref="DesignScreen.ShapeFactors.ShapeJ"/>（峰值电流密度形状因子 ⇒ 局部过热）
/// 这三个量由 <see cref="DesignScreen.Extract"/> 从网格直接给出，
/// **毫秒级**，不用跑全解 —— 保真门比它守护的那次求解便宜五个数量级。
///
/// 已知造不出来的东西（都会在上面三个量上现形，不必靠人记得枚举）：
///   · **开槽** —— <see cref="FlangePlate"/> 没有槽这个概念。Pt_Heater3 那片
///     4 个槽开在 R31–R35.9、每个 44.9°，那一圈环带近一半被挖掉，电流必须绕行。
///   · **舌片锥度** —— 分析器只量末端半宽，量不出中间怎么收。故两种都造一遍
///     （等宽 / 梯形），取量出来更接近的那一种，并把残差报出来。
///   · 任何非圆轮廓、偏心、局部凸台。
/// </summary>
public static class AnalyticSurrogate
{
    /// <summary>
    /// 替身与图纸的差距（相对误差，0 = 完全一致）。**只描述，不判定** ——
    /// 容差多少算够，由调用方按用途定：给优化器找方向可以松，给判据必须严。
    /// </summary>
    public sealed class Fidelity
    {
        /// <summary>哪一种舌片（等宽 / 梯形）</summary>
        public bool TabParallel;
        public double AreaRel, ShapeRRel, ShapeJRel;
        /// <summary>三项里最差的那一项 —— 判「能不能用」看它，不看平均</summary>
        public double Worst => Math.Max(AreaRel, Math.Max(ShapeRRel, ShapeJRel));

        /// <summary>结构上就表达不了的东西。非空 ⇒ 无论残差多小都别用（数字接近是巧合）</summary>
        public List<string> Blockers = new();

        public double AreaAnalytic, AreaField;
        public double ShapeRAnalytic, ShapeRField;
        public double ShapeJAnalytic, ShapeJField;

        public string Report() =>
            $"{(TabParallel ? "等宽舌" : "梯形舌")}：" +
            $"面积 {AreaAnalytic:0} vs {AreaField:0} mm²（{AreaRel * 100:+0.0;−0.0} %）　" +
            $"ShapeR {ShapeRAnalytic:0.0000} vs {ShapeRField:0.0000}（{ShapeRRel * 100:+0.0;−0.0} %）　" +
            $"ShapeJ {ShapeJAnalytic:0.00000} vs {ShapeJField:0.00000}（{ShapeJRel * 100:+0.0;−0.0} %）" +
            (Blockers.Count > 0 ? "　★ 结构性差异：" + string.Join("；", Blockers) : "");
    }

    /// <summary>
    /// 容许的最大电学差（相对）。
    ///
    /// **这个数有据，不是挑的 —— 它等于量具在「它要守的那条路」上的地板。**
    ///
    /// ★ 2026-08-24 重标：原值 2 % 标在**进程内**闭环上（解析板 → Rasterize →
    ///   反推 → 替身，同一片板量到 1.5 %）。可它要守的是 **.3dm 那条路**，
    ///   那条路多了 Rhino 写读与射线采样。实测（`--surrogate` 打同一片板，
    ///   由 Geom 的 steps 模式以**等宽舌**写出、再原路读回）：
    ///     采样格 0.50 mm：面积 +0.7 %　ShapeR +0.8 %　**ShapeJ +2.4 %**
    ///     采样格 0.25 mm：面积 +0.2 %　ShapeR +0.0 %　**ShapeJ +2.0 %**
    ///   加密采样把面积与 ShapeR 压到 0，**ShapeJ 停在 2 % 不动** ——
    ///   因为它是**峰值**量，而两侧用的是两个不同的网格生成器
    ///   （解析轮廓 4×4 子采样 vs 厚度场 t&gt;0），峰值格不可能完全重合。
    ///   ⇒ 2 % 是这条路上的**地板**，拿它当门槛等于「同一片板也判不过」。
    ///
    /// 取 3 %：仍比真实差异小一个量级 —— Pt_Heater3（4 个开槽）实测 ShapeJ 差
    /// **+31.8 %**，而且它先被结构性差异一票否决，根本轮不到看残差。
    ///
    /// ⚠ 放宽容差是**危险动作**，只有一种理由站得住：量具在这条路上的地板确实高于旧值，
    ///   而且是**量出来**的，不是因为「差一点点就过了」。上面那两行数就是证据。
    /// </summary>
    public const double Tol = 0.03;

    /// <summary>
    /// 能不能拿这片替身去搜。结构性差异一票否决 —— 那种情况下残差小是巧合，
    /// 缺的材料不在几何里，再小的数也不代表两片板一样。
    /// </summary>
    public static bool Usable(Fidelity f) => f.Blockers.Count == 0 && f.Worst <= Tol;

    /// <summary>
    /// 按一组**各级厚度**造一片解析法兰。级的次序与
    /// <see cref="PlateShapeAnalyzer.Shape.Levels"/> 一致（分析器按半径自小到大给）。
    ///
    /// 映射依据 <see cref="FlangePlate.ThicknessAt"/> 的优先级：
    ///   · 舌片（x &lt; 切点）先判，取 <see cref="FlangePlate.TabThicknessMm"/>
    ///   · 圆盘按半径落进 <see cref="FlangePlate.DiscStepRadiiMm"/> 的第一级
    ///   · 都不命中取 <see cref="FlangePlate.ThicknessMm"/>（外缘）
    ///
    /// ⚠ 焊脚一律取 0：厚度场画的是什么就是什么，图纸里已经含了实际堆料。
    ///   再加一道解析焊角 = 替身比图纸厚，而那正是替身该老实反映的东西。
    /// </summary>
    public static FlangePlate Build(PlateShapeAnalyzer.Shape sh,
                                    IReadOnlyList<double> levelThickMm,
                                    bool tabParallel)
    {
        if (sh.Levels.Count == 0) throw new ArgumentException("形状里一级都没有，造不出替身");
        if (levelThickMm.Count != sh.Levels.Count)
            throw new ArgumentException(
                $"给了 {levelThickMm.Count} 个厚度，而形状有 {sh.Levels.Count} 级 —— " +
                "对不上就别猜，猜错不会报错只会算出另一片板的数");

        // 按外半径排序，同时把厚度带着走（厚度是逐级优化的自由度，不能与级脱钩）
        var idx = Enumerable.Range(0, sh.Levels.Count)
                            .OrderBy(i => sh.Levels[i].ROuterMm).ToArray();

        var stepR = new List<double>();
        var stepT = new List<double>();
        double rimT = double.NaN, tabT = double.NaN;
        foreach (int i in idx)
        {
            double rOut = sh.Levels[i].ROuterMm, t = levelThickMm[i];
            if (rOut < sh.DiscRadiusMm - 1e-6) { stepR.Add(rOut); stepT.Add(t); }
            else
            {
                // 第一个够到盘外缘的级 = 圆盘外缘厚；半径最大的级覆盖舌片
                if (double.IsNaN(rimT)) rimT = t;
                tabT = t;
            }
        }
        // 全部级都在盘内（没有级够到外缘）⇒ 最外那级同时当外缘与舌片
        if (double.IsNaN(rimT))
        {
            rimT = stepT[^1]; tabT = rimT;
            stepR.RemoveAt(stepR.Count - 1); stepT.RemoveAt(stepT.Count - 1);
        }

        return new FlangePlate
        {
            HoleRadiusMm = sh.HoleRadiusMm,
            DiscRadiusMm = sh.DiscRadiusMm,
            TabEndXMm = sh.TabEndXMm,
            TabEndHalfWidthMm = sh.TabEndHalfWidthMm,
            TabParallel = tabParallel,
            ThicknessMm = rimT,
            ThickenedMm = rimT,
            TabThicknessMm = tabT,
            DiscStepRadiiMm = stepR.ToArray(),
            DiscStepThicknessMm = stepT.ToArray(),
            WeldFilletLegMm = 0.0,          // 见类注释：图纸已含实际堆料
            InsulBoundaryXMm = double.NaN,  // 切点（仅圆盘保温），与 .3dm 路径一致
        };
    }

    /// <summary>
    /// 量替身与图纸的电学差距。两边**必须用同一套网格参数** ——
    /// 否则量到的是网格差异，不是几何差异，而它看起来一模一样。
    /// </summary>
    public static Fidelity Measure(PlateShapeAnalyzer.Shape sh, ThicknessField field,
                                   FlangePlate surrogate,
                                   double hFine = 2.0, double hCoarse = 11.0,
                                   double fineRadius = 50.0, double clampLenMm = 4.0)
    {
        var mA = FlangeMesher.Build(surrogate, 0, hFine, hCoarse, fineRadius, clampLenMm);
        var mF = FlangeMesher.BuildFromField(field, sh.HoleRadiusMm, 0, hFine, hCoarse, fineRadius, clampLenMm);

        double tanX = surrogate.Tangent().X;
        var fa = DesignScreen.Extract(mA, 1000.0, 1050.0, tanX);
        var ff = DesignScreen.Extract(mF, 1000.0, 1050.0, tanX);

        double Rel(double a, double b) => Math.Abs(b) < 1e-12 ? (Math.Abs(a) < 1e-12 ? 0 : 1) : (a - b) / b;

        var fid = new Fidelity
        {
            TabParallel = surrogate.TabParallel,
            AreaAnalytic = fa.AreaMm2, AreaField = ff.AreaMm2,
            ShapeRAnalytic = fa.ShapeR, ShapeRField = ff.ShapeR,
            ShapeJAnalytic = fa.ShapeJ, ShapeJField = ff.ShapeJ,
            AreaRel = Rel(fa.AreaMm2, ff.AreaMm2),
            ShapeRRel = Rel(fa.ShapeR, ff.ShapeR),
            ShapeJRel = Rel(fa.ShapeJ, ff.ShapeJ),
        };
        // 相对误差取绝对值来比大小 —— 符号留在 Report 里给人看方向
        fid.AreaRel = Math.Abs(fid.AreaRel);
        fid.ShapeRRel = Math.Abs(fid.ShapeRRel);
        fid.ShapeJRel = Math.Abs(fid.ShapeJRel);

        if (sh.Slot.Found)
            fid.Blockers.Add(
                $"图上有 {sh.Slot.Count} 个开槽（R{sh.Slot.RInnerMm:0.0}–R{sh.Slot.ROuterMm:0.0}，" +
                $"各约 {sh.Slot.WidthDeg:0.0}°）—— FlangePlate 没有槽这个概念，替身是实心的");

        return fid;
    }

    /// <summary>
    /// 把一片**解析**法兰栅格化成厚度场 —— 本类的**自证工具**，不用于生产路径。
    ///
    /// 为什么必须有它：<see cref="Measure"/> 报「差 31.8 %」这种话，
    /// 只有在它也能报「差 0 %」时才有意义。一个从不说「相同」的量尺，
    /// 跟没有量尺是一回事 —— 本项目栽过好几次的「空集通过的断言」就是这个形状。
    /// 解析板 → 厚度场 → 分析器反推 → 造替身 → 量，这条闭环上残差应当≈0；
    /// 量不出 0 就说明**量法坏了**，而不是几何真有差。
    ///
    /// <paramref name="marginMm"/> 是图幅四周留白：分析器靠从图幅边界做连通域填充
    /// 来分「外部空白」与「内部空腔」，板贴着边就分不开了。
    /// </summary>
    public static ThicknessField Rasterize(FlangePlate g, double step = 0.5, double marginMm = 4.0)
        => FlangeMesher.Rasterize(g, step, marginMm);   // R47：栅格化只有一份（生产路径 Build 也走它），这里只是带留白的入口

    /// <summary>
    /// 造替身并在**等宽 / 梯形**两种舌片里挑量出来更接近的那一种。
    /// 挑的依据是量到的残差，不是猜 —— 分析器量不出舌片中段怎么收。
    /// </summary>
    public static (FlangePlate Plate, Fidelity Fid, Fidelity Other) BestFit(
        PlateShapeAnalyzer.Shape sh, ThicknessField field, IReadOnlyList<double> levelThickMm)
    {
        var pPar = Build(sh, levelThickMm, tabParallel: true);
        var pTap = Build(sh, levelThickMm, tabParallel: false);
        var fPar = Measure(sh, field, pPar);
        var fTap = Measure(sh, field, pTap);
        return fPar.Worst <= fTap.Worst ? (pPar, fPar, fTap) : (pTap, fTap, fPar);
    }
}
