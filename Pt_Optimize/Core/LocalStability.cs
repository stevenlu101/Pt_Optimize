using System;

namespace PtOptimize.Core;

/// <summary>
/// **局部热失稳判据** —— 用户 2026-08-13 二次澄清：
///
///   **局部**温度提高 → 该处电阻提高 → 功率在该处堆 → 恶性循环 → 烧断
///   **升温过程中也会发生。**
///
/// 与 <see cref="FlangeStability"/>（整片集总）的区别：
/// 集总判的是「整片会不会一起跑掉」，本类判的是「某一点会不会自己跑掉」。
/// 后者更严，因为热点处 J 是全片峰值，而它周围的材料并不跟着一起热。
///
/// 逐点能量平衡（单位面积）：
///
///   加热 = ρe(T)·J²·t            ⇒  d/dT = ρe·J²·t·TCR(T)
///   散热 = 2q″(T) + 横向导热      ⇒  d/dT = 2·dq″/dT + k·t/L²
///
/// 其中 L 是热点到**定温边界**（管子）的距离 —— 盘越窄，横向导热越强。
/// 稳定要求后者大于前者，解出**局部失稳电流密度上限**：
///
///   J_stab = √[ ( 2·dq″/dT + k·t/L² ) / ( ρe(T)·t·TCR(T) ) ]
///
/// ★ 为什么升温期更危险：**TCR 随温度下降而升高**。
///   铂在 25 °C 的 TCR ≈ 3.58e-3 /K，在 1150 °C 只有 5.48e-4 /K —— 相差 6.5 倍。
///   即冷态下同样的温升会带来 6.5 倍的电阻增量，正反馈更强。
///   与此同时冷态的 q″ 与 dq″/dT 都趋近于零（辐射 ∝T⁴）。
///   两头夹击 ⇒ **升温初段是局部失稳最危险的窗口**，而稳态反而宽松。
/// </summary>
public static class LocalStability
{
    public sealed class Point
    {
        public double TempC, JActual, JStab, HeatDeriv, CoolDeriv, CoolSurf, CoolLateral;
        /// <summary>裕度 = J_stab / J_实际。&lt;1 即该点会自行升温直到烧断</summary>
        public double Margin => JActual > 1e-9 ? JStab / JActual : double.PositiveInfinity;
        public bool Stable => Margin > 1.0;
    }

    /// <summary>铂电阻率拟合的有效上界 °C —— 超出后二次项翻号，TCR 会变负</summary>
    public const double FitMaxC = 1400;

    /// <summary>
    /// 舌片上最不利点到**最近**定温锚点的距离 mm —— 即 J_stab 公式里的 L。
    ///
    /// 舌片两端各有一个定温锚点：一端是管孔（经圆盘导过去），另一端是铜排压接段
    /// （压接段被铜短接成等位体、又被夹到设定温度，热学上就是个定温边界）。
    /// 最不利点在两者正中 ⇒ L = 自由段长/2，自由段 = 舌长 − 压接长 − |切点x|。
    ///
    /// ★ 早先按「到管孔的距离」写成 |xt| + (舌长−|xt|)/2，**把压接那个锚点漏了**：
    ///   130 mm 舌片下 L 被高估 68.5/41.5 ≈ 1.65 倍 ⇒ J_stab 被低估同样倍数
    ///   ⇒ 整张 (舌长,舌厚) 扫描表被误判成「全部局部失稳」。
    /// </summary>
    public static double TabHalfSpanMm(double tabLenMm, double clampLenMm, double tangentXMm)
        => Math.Max(1.0, (tabLenMm - clampLenMm - Math.Abs(tangentXMm)) * 0.5);

    /// <summary>
    /// 判一个点。<paramref name="lateralLenMm"/> 是热点到定温边界（管孔）的距离；
    /// 传 NaN 表示不计横向导热（保守）。
    /// </summary>
    public static Point Check(DesignInputs p, double tempC, double jAPerMm2, double thickMm,
                              double insulThickMm, double lateralLenMm)
    {
        var pt = new Point { TempC = tempC, JActual = jAPerMm2 };
        if (tempC > FitMaxC || tempC < 0) { pt.JStab = double.NaN; return pt; }

        double rho = Materials.PtResistivity(tempC);              // Ω·m
        double tcr = Materials.PtTcr(tempC);                      // 1/K
        double t = thickMm * 1e-3;                                // m

        // 加热的温度导数，单位面积 W/(m²·K)
        double j = jAPerMm2 * 1e6;                                // A/m²
        pt.HeatDeriv = rho * j * j * t * tcr;

        // 散热①：两面表面，数值微分
        double d = 5.0;
        double Q(double tt) => 2.0 * DesignScreen.PlateFluxWPerM2(p, tt, insulThickMm);
        pt.CoolSurf = (Q(tempC + d) - Q(tempC - d)) / (2 * d);

        // 散热②：横向导到定温边界。单位面积的等效导度 = k·t/L²
        double k = Materials.PtThermalK(tempC);                   // W/(m·K)
        pt.CoolLateral = double.IsNaN(lateralLenMm) || lateralLenMm <= 1e-6
                       ? 0 : k * t / Math.Pow(lateralLenMm * 1e-3, 2);

        pt.CoolDeriv = pt.CoolSurf + pt.CoolLateral;
        pt.JStab = Math.Sqrt(Math.Max(0, pt.CoolDeriv / Math.Max(1e-30, rho * t * tcr))) * 1e-6;
        return pt;
    }
}
