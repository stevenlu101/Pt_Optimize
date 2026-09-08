using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 把 <see cref="PlateShapeAnalyzer.Shape"/>（从 .3dm 反推出来的几何）翻成
/// **解析路的那套参数**（盘径 / 舌长 / 舌半宽 / 管壁 / 基板厚 / 管孔两级台阶 r₁·t₁、r₂·t₂）。
///
/// ★ 为什么要有这一步（用户 2026-08-25：「3DM 只读几何数据，为何不能带入计算？」）
///
///   两条输入路线此前给不出接近的答案，本质原因只有一个：**.3dm 路改不了形状**。
///   逐级定厚的自由度是「各级厚度」，盘径/舌长/舌宽由图纸钉死；
///   而设计的关键一步恰恰是改形状。反推早就做到了（PlateShapeAnalyzer 一直在印那几个数），
///   缺的只是**把它交过去**。本类就是那一步，且刻意做成**纯函数**：一条翻译规则不该只能靠开界面才验得到。
///
/// ★★★★★ 2026-09-08（用户要求 R8／R14）：**多级厚度不再压成一个平均数**。
///   此前多级图被取「面积加权平均」当板厚 —— 那是近似，而且把图纸的台阶信息整个丢掉了。
///   现在按解析模型本来就有的形状翻译（<see cref="DesignSpec.RingRadiiOf"/>／<see cref="DesignSpec.RingMul"/>／
///   <see cref="DesignSpec.RingMul2"/>）：
///   <code>
///     图纸各级（按外半径排序）      解析模型
///     够到盘外缘那一级（含舌片）  →  基板厚 t（<see cref="Knobs.PlateThickMm"/>）
///     盘内第 1 级（孔边）          →  r₁ = 该级外半径 − 管孔半径，t₁ = 该级厚 ÷ t
///     盘内第 2 级                  →  r₂、t₂ 同理
///     只有 1 级台阶                →  外级厚度倍率 t₂ = 1（= 基板，**等于没有**），r₂ = 2·r₁ 只是放着（历史关系）
///     ≥3 级台阶                    →  **抛**：解析模型只有基板 + 两级，不猜、不并
///   </code>
///   逐位可逆：把翻出来的参数造成板再栅格化、反推，级数与厚度应与原图逐位相同
///   （<c>ShapeToAnalyticTests.三级图_翻过去再翻回来_逐位相同</c>）。
///
/// ⚠ 仍要说出口的近似（都写进 <see cref="Knobs.Note"/>，调用方必须原样呈现）：
///  · **管壁是反推的**：孔半径 = 管壁 + 25（<see cref="DesignSpec.HoleRadiusMm"/>），
///    所以管壁 = 孔半径 − 25。图纸的孔若不是按这条画的，这个数就不对。
///  · **舌片族是等宽**：解析路的搜形状只在等宽舌片上走（见 ShapeSearchPlan）。
///    图纸若是梯形舌，转过去之后优化的是**等宽舌**，不再是原图那一片。
///  · **舌片厚由 APP 定**（R11，用户 2026-09-08：舌片厚 = I/(10·舌宽)，不是旋钮）：图纸上舌片那一级多厚只作对照。
///  · **图纸的槽不翻**：解析路的槽是求解器按场定位、按判据定张角的旋钮（从 0 起），
///    图上画的槽只报数、不带过去。
///
/// ⚠ 本类**不判断**「够不够像」。那件事已经有唯一来源
///   （<see cref="AnalyticSurrogate.Usable"/> 与它的 Tol），调用方去问它 ——
///   这里再立一套门槛就是「同一件事两处来源」。
/// </summary>
public static class ShapeToAnalytic
{
    /// <summary>「够到盘外缘」的判定容差 mm：级的外半径 ≥ 盘半径 − 此值即视为够到外缘（厚度场栅格 0.5 mm，取两格）。</summary>
    public const double RimTolMm = 1.0;

    /// <summary>解析路那套参数，连同**必须原样呈现**的近似说明。</summary>
    public sealed class Knobs
    {
        public double DiscDiameterMm;
        public double TabLengthMm;
        public double TabHalfWidthMm;
        public double WallMm;
        /// <summary>基板厚 mm = 够到盘外缘那一级（舌片也在这一级上）。**不是平均**。</summary>
        public double PlateThickMm;
        /// <summary>图纸解析出的级数（含基板）。</summary>
        public int LevelCount;
        /// <summary>内级外扩 mm（相对管孔半径）。NaN = 图纸没有台阶。</summary>
        public double RingW1Mm = double.NaN;
        /// <summary>内级厚度倍率（相对基板）。NaN = 图纸没有台阶。可以 &lt; 1（孔边减薄的图）。</summary>
        public double RingMul = double.NaN;
        /// <summary>外级外扩 mm。图纸只有一级台阶时 = 2·r₁（历史关系，对零件没有影响，因为 t₂ = 1）。</summary>
        public double RingW2Mm = double.NaN;
        /// <summary>外级厚度倍率。图纸只有一级台阶时 = 1.00（= 基板，等于没有）。</summary>
        public double RingMul2 = double.NaN;
        /// <summary>图纸有没有台阶（≥2 级）。</summary>
        public bool HasRing => !double.IsNaN(RingW1Mm);
        /// <summary>图纸上舌片那一级的厚度 mm（覆盖舌端的那级）。R11 前解析模型舌片 = 基板，不同时标为近似。</summary>
        public double TabThickMm;
        /// <summary>舌片那级与盘缘那级不同厚 ⇒ 解析模型（舌片 = 基板）是近似。</summary>
        public bool TabThickApproximated;
        /// <summary>近似与反推的说明。调用方**必须**原样呈现，不许吞。</summary>
        public string Note = "";

        /// <summary>
        /// 把台阶写进一份设计的第 j 片（r₁/r₂/t₁/t₂ 四个数一起写，成对的自由度不许只写一半）。
        /// 没有台阶 ⇒ 倍率 1.00、半径 NaN（= 用默认关系；t₁ = t₂ = 1 时台阶根本不存在）。
        /// </summary>
        public void ApplyRing(DesignSpec d, int j)
        {
            if (HasRing)
            {
                d.RingW1Mm[j] = RingW1Mm; d.RingW2Mm[j] = RingW2Mm;
                d.RingMul[j] = RingMul;   d.RingMul2[j] = RingMul2;
            }
            else
            {
                d.RingW1Mm[j] = double.NaN; d.RingW2Mm[j] = double.NaN;
                d.RingMul[j] = 1.0;         d.RingMul2[j] = 1.0;
            }
        }
    }

    /// <summary>翻译。几何缺失（无舌片 / 无分级 / 台阶超过两级）时**抛**，不给一个看起来正常的默认值。</summary>
    public static Knobs From(PlateShapeAnalyzer.Shape sh)
    {
        if (sh is null) throw new ArgumentNullException(nameof(sh));
        if (double.IsNaN(sh.TabEndXMm) || double.IsNaN(sh.TabEndHalfWidthMm))
            throw new ArgumentException(
                "这张图没有舌片（TabEndX/TabEndHalfWidth 是 NaN）—— 解析模型是「圆盘 + 舌片」，" +
                "没有舌片就不是同一类零件，不能翻。");
        if (sh.Levels is null || sh.Levels.Count == 0)
            throw new ArgumentException("这张图一级厚度都没解析出来 —— 没有板厚可交给解析路。");
        if (sh.DiscRadiusMm <= 0) throw new ArgumentException("圆盘外半径不是正数：" + sh.DiscRadiusMm);

        double wall = sh.HoleRadiusMm - 25.0;
        string nl = Environment.NewLine;

        // ── 各级按外半径排序；盘内的是台阶，第一个够到盘外缘的是基板，半径最大的那级覆盖舌片
        //    （与 AnalyticSurrogate.Build 同一条规则，只是容差取栅格两格而不是 1e-6）
        var byR = sh.Levels.OrderBy(l => l.ROuterMm).ToList();
        var steps = byR.Where(l => l.ROuterMm < sh.DiscRadiusMm - RimTolMm).ToList();
        var rimLv = byR.FirstOrDefault(l => l.ROuterMm >= sh.DiscRadiusMm - RimTolMm);
        if (rimLv is null)
        {
            // 没有一级够到盘外缘（图纸的「盘」比最外那级还大 —— 只可能是反推的盘径偏大）：
            // 最外那级同时当基板与舌片，与 AnalyticSurrogate.Build 一致
            rimLv = steps[^1]; steps.RemoveAt(steps.Count - 1);
        }
        var tabLv = byR[^1];
        double baseT = rimLv.ThicknessMm;
        if (!(baseT > 0)) throw new ArgumentException("基板那一级厚度不是正数：" + baseT);
        if (steps.Count > 2)
            throw new ArgumentException(
                $"这张图在盘内有 {steps.Count} 级台阶（" +
                string.Join("／", steps.Select(l => $"R{l.ROuterMm:0.0} {l.ThicknessMm:0.00} mm")) +
                "），而解析模型只有「基板 + 两级台阶」（r₁·t₁、r₂·t₂）。不并、不取平均 —— " +
                "请在 Rhino 里并成 ≤2 级台阶再读，或留在 .3dm 模式按图纸逐级定厚。");

        var k = new Knobs
        {
            DiscDiameterMm = 2 * sh.DiscRadiusMm,
            TabLengthMm = Math.Abs(sh.TabEndXMm),
            TabHalfWidthMm = sh.TabEndHalfWidthMm,
            WallMm = wall,
            PlateThickMm = baseT,
            LevelCount = sh.Levels.Count,
            TabThickMm = tabLv.ThicknessMm,
            TabThickApproximated = Math.Abs(tabLv.ThicknessMm - baseT) > 1e-9,
        };

        var lines = new List<string>
        {
            "· 管壁 " + wall.ToString("0.00") + " mm 是**反推**的：孔半径 "
            + sh.HoleRadiusMm.ToString("0.00") + " − 25（DesignSpec.HoleRadiusMm 的定义）。"
            + "图纸的孔若不是按这条画的，这个数就不对。",
            "· 舌片族是**等宽**：解析路的搜形状只在等宽舌片上走。"
            + "原图若是梯形舌，转过去之后优化的是等宽舌，**不再是原图那一片**。",
        };

        if (steps.Count == 0)
        {
            lines.Add("· 板厚 " + baseT.ToString("0.00") + " mm（原图只有一级，没有台阶，**不是近似**）。");
        }
        else
        {
            double r1 = steps[0].ROuterMm, w1 = r1 - sh.HoleRadiusMm;
            if (!(w1 > 0))
                throw new ArgumentException($"内级外半径 R{r1:0.00} 不比管孔 R{sh.HoleRadiusMm:0.00} 大 —— 这一级不在盘上。");
            k.RingW1Mm = w1;
            k.RingMul = steps[0].ThicknessMm / baseT;
            if (steps.Count == 2)
            {
                double r2 = steps[1].ROuterMm, w2 = r2 - sh.HoleRadiusMm;
                if (!(w2 > w1))
                    throw new ArgumentException($"外级外半径 R{r2:0.00} 不比内级 R{r1:0.00} 大 —— 台阶半径没有递增。");
                k.RingW2Mm = w2;
                k.RingMul2 = steps[1].ThicknessMm / baseT;
                lines.Add("· 原图 " + sh.Levels.Count + " 级厚度**逐级带过去，不取平均**：基板 "
                    + baseT.ToString("0.00") + " mm；内级 r₁ = 孔+" + w1.ToString("0.0") + " mm、t₁ = "
                    + steps[0].ThicknessMm.ToString("0.00") + " mm（倍率 " + k.RingMul.ToString("0.000") + "）；外级 r₂ = 孔+"
                    + w2.ToString("0.0") + " mm、t₂ = " + steps[1].ThicknessMm.ToString("0.00") + " mm（倍率 "
                    + k.RingMul2.ToString("0.000") + "）。");
            }
            else
            {
                k.RingW2Mm = 2 * w1;
                k.RingMul2 = 1.0;
                lines.Add("· 原图 " + sh.Levels.Count + " 级厚度**逐级带过去，不取平均**：基板 "
                    + baseT.ToString("0.00") + " mm；内级 r₁ = 孔+" + w1.ToString("0.0") + " mm、t₁ = "
                    + steps[0].ThicknessMm.ToString("0.00") + " mm（倍率 " + k.RingMul.ToString("0.000")
                    + "）。图上只有这一级台阶 ⇒ 外级倍率 t₂ = 1.00（= 基板，**等于没有**），"
                    + "r₂ = 2·r₁ 只是放着，对零件没有影响。");
            }
            if (k.RingMul < 1 - 1e-9)
                lines.Add("· 孔边那一级比基板**薄**（倍率 " + k.RingMul.ToString("0.000")
                    + "）：照图纸核算时按图；求解器的环倍率从 1.00（无台阶）起只增不减，解出来的台阶不会比基板薄。");
        }

        if (k.TabThickApproximated)
            lines.Add("· ⚠ 图纸上舌片那一级 " + tabLv.ThicknessMm.ToString("0.00") + " mm 与盘缘 "
                + baseT.ToString("0.00") + " mm 不同厚。解析路的舌片厚由 APP 按 I/(10·舌宽) 定（用户 2026-09-08，不是旋钮），"
                + "图纸的 " + tabLv.ThicknessMm.ToString("0.00") + " mm 只作对照 —— 算的舌片与图纸不同，**这是近似**。");
        else
            lines.Add("· 舌片厚由 APP 按 I/(10·舌宽) 定（用户 2026-09-08，不是旋钮）；图纸舌片 "
                + tabLv.ThicknessMm.ToString("0.00") + " mm 只作对照。");

        if (sh.Slot.Found)
            lines.Add("· 图纸上的 " + sh.Slot.Count + " 个槽（角宽约 " + sh.Slot.WidthDeg.ToString("0.0")
                + "°，R" + sh.Slot.RInnerMm.ToString("0.0") + "–" + sh.Slot.ROuterMm.ToString("0.0")
                + "）**不带过去**：解析路的槽由求解器按场定位、按判据定张角（从 0 起）。");

        k.Note = string.Join(nl, lines);
        return k;
    }
}
