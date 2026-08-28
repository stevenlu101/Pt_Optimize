using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 把 <see cref="PlateShapeAnalyzer.Shape"/>（从 .3dm 反推出来的几何）翻成
/// **解析路的那几个旋钮**（盘径 / 舌长 / 舌半宽 / 管壁 / 板厚）。
///
/// ★ 为什么要有这一步（用户 2026-08-25：「3DM 只读几何数据，为何不能带入计算？」）
///
///   两条输入路线此前给不出接近的答案，本质原因只有一个：**.3dm 路改不了形状**。
///   逐级定厚的自由度是「各级厚度」，盘径/舌长/舌宽由图纸钉死；
///   而设计记录的关键一步恰恰是改形状（Ø120 → Ø60）。
///
///   但「从图纸反推参数」这件事**早就做到了** —— PlateShapeAnalyzer 一直在印
///   「圆盘外半径 R59.99／舌片 长 199.5 末端半宽 40.0／管孔 R26.00」，
///   那就是解析路要的整套数。缺的只是**把它交过去**。
///   本类就是那一步，且刻意做成**纯函数**：一条翻译规则不该只能靠开界面才验得到。
///
/// ⚠ 三条必须说出口的近似（都写进 <see cref="Knobs.Note"/>，调用方必须原样呈现）：
///  · **管壁是反推的**：孔半径 = 管壁 + 25（<see cref="DesignSpec.HoleRadiusMm"/>），
///    所以管壁 = 孔半径 − 25。图纸的孔若不是按这条画的，这个数就不对。
///  · **多级板厚会被压成一个数**：解析模型每片只有一个厚度（外加环倍率），
///    多级时取**面积加权平均**，这是近似，必须标出来。
///  · **舌片族是等宽**：解析路的搜形状只在等宽舌片上走（见 ShapeSearchPlan）。
///    图纸若是梯形舌，转过去之后优化的是**等宽舌**，不再是原图那一片。
///
/// ⚠ 本类**不判断**「够不够像」。那件事已经有唯一来源
///   （<see cref="AnalyticSurrogate.Usable"/> 与它的 Tol），调用方去问它 ——
///   这里再立一套门槛就是「同一件事两处来源」。
/// </summary>
public static class ShapeToAnalytic
{
    /// <summary>解析路那几个旋钮，连同**必须原样呈现**的近似说明。</summary>
    public sealed class Knobs
    {
        public double DiscDiameterMm;
        public double TabLengthMm;
        public double TabHalfWidthMm;
        public double WallMm;
        public double PlateThickMm;
        /// <summary>板厚是多级压出来的近似值（而非图纸上真有的那一个数）。</summary>
        public bool ThicknessApproximated;
        /// <summary>近似与反推的说明。调用方**必须**原样呈现，不许吞。</summary>
        public string Note = "";
    }

    /// <summary>翻译。几何缺失（无舌片 / 无分级）时**抛**，不给一个看起来正常的默认值。</summary>
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
        double area = sh.Levels.Sum(l => l.AreaMm2);
        bool multi = sh.Levels.Count > 1;
        double thick = area > 1e-9
            ? sh.Levels.Sum(l => l.ThicknessMm * l.AreaMm2) / area
            : sh.Levels.Average(l => l.ThicknessMm);

        string nl = Environment.NewLine;
        string note =
            "· 管壁 " + wall.ToString("0.00") + " mm 是**反推**的：孔半径 "
            + sh.HoleRadiusMm.ToString("0.00") + " − 25（DesignSpec.HoleRadiusMm 的定义）。"
            + "图纸的孔若不是按这条画的，这个数就不对。" + nl
            + "· 舌片族是**等宽**：解析路的搜形状只在等宽舌片上走。"
            + "原图若是梯形舌，转过去之后优化的是等宽舌，**不再是原图那一片**。" + nl
            + (multi
               ? "· ⚠ 原图有 " + sh.Levels.Count + " 级厚度（"
                 + string.Join("/", sh.Levels.Select(l => l.ThicknessMm.ToString("0.00")))
                 + " mm），而解析模型每片只有一个厚度 ⇒ 取**面积加权平均** "
                 + thick.ToString("0.00") + " mm。**这是近似**，铂重与温度都会跟着变。"
               : "· 板厚 " + thick.ToString("0.00") + " mm（原图只有一级，**不是近似**）。");

        return new Knobs
        {
            DiscDiameterMm = 2 * sh.DiscRadiusMm,
            TabLengthMm = Math.Abs(sh.TabEndXMm),
            TabHalfWidthMm = sh.TabEndHalfWidthMm,
            WallMm = wall,
            PlateThickMm = thick,
            ThicknessApproximated = multi,
            Note = note,
        };
    }
}
