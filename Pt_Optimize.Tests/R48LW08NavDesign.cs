using PtOptimize.Core;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 L（2026-09-17，Opus 5）：**照端到端输出文件复原**的 W08 导航档设计（4245 g 那一份）。
///
/// 出处（全仓唯一一份，三个测试共用 —— 不许各抄一遍）：
///   <c>deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt</c>
///   §「导航 vs 细网格：旋钮终值（逐片）」表里 **A 导航档** 那四行；
///   形状与工况取自同一份文件 §「输入」段 = <see cref="DesignSpec.W08"/> 原样（求解器一根形状旋钮都不动）。
///
/// ⚠ 这是**照表复原**，不是求解器返回的那个对象。
///   「照表复原出来的，是不是同一份设计」正是 <see cref="R48LSolveOrderTests"/> 要测的事 ——
///   在那个测试给出结论之前，不许把这份设计当成「就是 A 段那一份」。
/// </summary>
public static class R48LW08NavDesign
{
    /// <summary>复原所依据的那一份输出文件（报告里要能指回出处）。</summary>
    public const string Source =
        "deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt　§「旋钮终值（逐片）」表的 A 导航档四行";

    /// <summary>那一跑 ② 带玻璃稳态印出来的数（同一份文件 §「A 段 ② 带玻璃稳态」）—— 复原自证拿它当靶子。</summary>
    public const double NavHotOverTcK = 4.529;   // 位置 入口
    public const double NavNetFluxW   = 0.656;   // 位置 出口
    public const double NavColdUnderTcK = 4.511; // 位置 出口
    public const double NavCoupleRemainK = 0.430;
    public const double NavTotalMassG = 4245;
    public const string NavColdWhere = "出口";

    public static DesignSpec Build()
    {
        var d = DesignSpec.W08.Clone();
        d.Name = "管壁 0.8 · 留余量（照 A 导航档输出表复原）";
        d.Provenance = Source;

        // ── 旋钮终值（A 导航档四行）
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };      // 板厚 mm
        d.TabInsulMm = new[] { 5.1, 2.3, 3.6, 10.4 };         // 舌保温 mm
        d.RingMul    = new[] { 1.00, 1.00, 1.00, 1.00 };      // 环倍率 t₁
        // 外级 t₂ 表印 1.00；求解器从没动过它（A 段轨迹 外级t₂ 1/1/1/1）⇒ 留默认规则（RingMulOuter = 1+0.4(t₁−1) = 1.00）
        d.RingMul2   = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        // 内级 r₁ 表印 28.8 = 孔 25.8 + 环宽 3.0（默认）；外级 r₂ 表印 31.8 = 孔 + 2×3.0（默认）
        // 轨迹里印的是「3*／6*」—— 星号 = 这一片没自定、走默认规则 ⇒ 复原成 NaN 才是同一份
        d.RingW1Mm   = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.RingW2Mm   = new[] { double.NaN, double.NaN, double.NaN, double.NaN };
        d.TongueThickMm = new[] { 2.03, 3.51, 3.51, 2.03 };   // 舌片厚 mm（闭式，不是旋钮）
        d.SlotSpanDeg   = new[] { 0.0, 0.0, 0.0, 0.0 };       // 槽张角 °
        d.TabHoleRMm    = new[] { 0.0, 0.0, 0.0, 0.0 };       // 舌孔 R mm
        d.TabHoleAspect = new[] { 1.00, 1.00, 1.00, 1.00 };   // 孔拉长比

        // ── 场定的位置量（Solver.FieldPlacement 按收敛的场写回设计；不是旋钮，但进几何）
        d.SlotCenterDeg  = new[] { 103.0, 153.0, 153.0, 158.0 };  // 槽心 °
        d.TabHoleXMm     = new[] { -2.0, -2.0, -86.0, -2.0 };     // 孔心 x mm
        d.DiscCutRotDeg  = new[] { -27.0, -14.0, -14.0, -14.0 };  // 盘槽当地电流 °

        return d;
    }
}
