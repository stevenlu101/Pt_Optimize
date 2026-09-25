using PtOptimize.Core;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 L（2026-09-17，Opus 5）：**照端到端输出文件复原**的 W06 细网格档设计（3379 g 那一份）。
///
/// 出处（全仓唯一一份，不许各抄一遍）：
///   <c>deliverable/R48_L_端到端_细网格_W06_本次开跑于2026-09-17_093015.txt</c>
///   §「导航 vs 细网格：旋钮终值（逐片）」表里 **B 细网格档** 那四行；
///   形状与工况取自同一份文件 §「输入」段 = <see cref="DesignSpec.W06"/> 原样（求解器一根形状旋钮都不动）。
///
/// ⚠ 与 <see cref="R48LW08NavDesign"/> 同理，这是**照表复原**，不是求解器返回的那个对象。
///   §0.-8 已实测：复原件与求解器那份在「进几何的字段」上只差 1 ulp（舌保温、舌片厚各一处），
///   而「管根低于热偶读数」对那一个 ulp 敏感（同一网格上摆 0.37–0.50 K，与耦合停机噪声同量级）。
///   ⇒ 拿它做扫描时，**靠近「管根」那条边界的窗口端点带着这一档噪声**，报告里要跟着说。
/// </summary>
public static class R48LW06FineDesign
{
    /// <summary>复原所依据的那一份输出文件。</summary>
    public const string Source =
        "deliverable/R48_L_端到端_细网格_W06_本次开跑于2026-09-17_093015.txt　§「旋钮终值（逐片）」表的 B 细网格档四行";

    /// <summary>那一跑在**细网格**（1.000 mm／半径 59.0）上 ② 印出来的数 —— 复原自证拿它当靶子。</summary>
    public const double FineHotOverTcK = 4.355;    // 位置 HC1|HC2
    public const double FineNetFluxW = 0.592;      // 位置 出口
    public const double FineColdUnderTcK = 4.889;  // 位置 HC2|HC3
    public const double FineCoupleRemainK = 0.463;
    public const double FineTotalMassG = 3379;

    public static DesignSpec Build()
    {
        var d = DesignSpec.W06.Clone();
        d.Name = "管壁 0.6 · 留余量（照 B 细网格档输出表复原）";
        d.Provenance = Source;

        d.TabThickMm = new[] { 0.64, 1.10, 1.10, 0.64 };
        d.TabInsulMm = new[] { 6.4, 3.1, 4.5, 13.1 };
        d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.RingMul2 = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 表印 1.00 = 默认规则值
        d.RingW1Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「3*」= 走默认规则
        d.RingW2Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「6*」
        d.TongueThickMm = new[] { 1.75, 3.02, 3.02, 1.75 };
        d.SlotSpanDeg = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleRMm = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleAspect = new[] { 1.00, 1.00, 1.00, 1.00 };

        // 场定的位置量（B 细网格档那四行）
        d.SlotCenterDeg = new[] { 100.0, 153.0, 157.0, 162.0 };
        d.TabHoleXMm = new[] { -98.0, -98.0, -98.0, -98.0 };
        d.DiscCutRotDeg = new[] { -17.0, -14.0, -12.0, -10.0 };

        return d;
    }
}
