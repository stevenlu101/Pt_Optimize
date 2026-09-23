using System;

namespace PtOptimize.Core;

/// <summary>
/// 物性库。铂电阻率采用用户提供的二次拟合（SSE = 0.0115，0–1500°C，出处 鉑金電氣計算.xlsx）。
///
/// 2026-09-18，Opus 5：原来这里只写「其余为文献拟合」，导热系数与比热的**具体出处在仓库里查不到**。
/// 本轮把两条都指回文献并注明覆盖区间与带宽（见 <see cref="PtThermalK"/>、<see cref="PtCp"/>）：
///   · 导热系数 = Touloukian/TPRC 与 Kaye &amp; Laby 推荐值那一支；0–700 °C 与 K&amp;L 一致到 ≤2.5 %，
///     700 °C 以上是**推荐值外推、无本项目实测**，带宽 ±8 %（物理把关人 2026-09-18 定稿）。
///   · 比热 = Kaye &amp; Laby 第 2.3.6 小节四点最小二乘（本轮重定；旧式斜率只有文献的一半）。
/// 按牌号的温度函数（含 Pt-10%Rh 的实测热导率与比热表）在 <see cref="MaterialDb"/>；求解链经 <see cref="PtProps"/> 按牌号取，纯铂一支调的就是本类这几支。
/// 可在 UI 中被输入值覆盖的部分已单独暴露。
/// </summary>
public static class Materials
{
    // ---------------- 铂 ----------------

    public const double PtDensity = 21450.0;          // kg/m^3
    public const double PtMolarMass = 195.084e-3;     // kg/mol
    public const double Faraday = 96485.33;           // C/mol

    /// <summary>用户拟合: rho = 9.83*(1 + a*T + b*T^2) [μΩ·cm] -> 返回 Ω·m</summary>
    public const double RhoRef = 9.83e-8;             // Ω·m  (9.83 μΩ·cm @ 0°C)
    public const double AlphaFit = 0.0039678411655333;      // 鉑金電氣計算.xlsx 全精度
    public const double BetaFit = -5.849309909955442e-07;   // 同上

    /// <summary>
    /// 纯铂熔点 °C。**任何算例只要有一处金属温度越过它，那个「解」就不存在** ——
    /// 求解器不会自己拒绝：上面的二次拟合要到 3392 °C 才反号，
    /// 所以它会心平气和地给出 2981 °C 的舌片温度和一份闭合的能量账。
    /// 实测教训：舌片包 20 mm 保温的算例正是这样一路「可行」到 2981 °C 的。
    /// </summary>
    public const double PtMeltC = 1768.2;

    /// <summary>电阻率拟合的实测覆盖上界 °C（鉑金電氣計算.xlsx 拟合区间 0–1500）。超出即外推。</summary>
    public const double PtFitMaxC = 1500.0;

    public static double PtResistivity(double tC)
        => RhoRef * (1.0 + AlphaFit * tC + BetaFit * tC * tC);

    /// <summary>电阻温度系数 (1/rho)(drho/dT)，1/K。1300°C 约 4.73e-4。</summary>
    public static double PtTcr(double tC)
    {
        double rho = PtResistivity(tC);
        double drho = RhoRef * (AlphaFit + 2.0 * BetaFit * tC);
        return drho / rho;
    }

    /// <summary>
    /// 纯铂导热系数 W/(m·K)。71.6 @0 °C → 83.8 @1150 °C → 85.4 @1300 °C。
    ///
    /// **出处**（2026-09-18，Opus 5 补；此前仓库里只有档头那句「其余为文献拟合」，查不到具体来源）：
    /// 常数项与斜率属 **Touloukian/TPRC 与 Kaye &amp; Laby 的推荐值那一支**。核对点（Kaye &amp; Laby，NPL 网络版，
    /// 第 2.3.7 小节 Thermal conductivities of metallic elements，纯多晶实测汇编，
    /// https://web.archive.org/web/2016/http://www.kayelaby.npl.co.uk/general_physics/2_3/2_3_7.html ）：
    /// 273.2 K = 72、373.2 K = 72、573.2 K = 73、973.2 K = 78 W/(m·K)
    /// ⇒ 本式在 **0–700 °C 内与它一致到 ≤ 2.5 %**（最大在 300 °C，+2.44 %）。核对点见 <see cref="KRefTempsC"/>／<see cref="KRefKWmK"/>，门 R48ThermalSourceTests。
    ///
    /// ⚠ **700 °C 以上没有本项目实测支撑，是推荐值外推**（K&amp;L 表到 973.2 K 为止）。
    /// 另一支实测给得高：Terada, Ohkubo, Mohri，*Platinum Metals Rev.*, 2005, 49(1), 21–26（激光闪射法）
    /// 自测纯 Pt 300 K = 77.8、1100 K ≈ 95 W/(m·K)。
    /// **物理把关人 2026-09-18 定稿：中值沿用本式（推荐值那一支），带宽 ±8 %；
    /// Terada 的 95 只作带宽上沿不采用**（它要求 L/L₀ ≈ 1.35，不合理）。
    /// ±8 % 在 1150 °C 给 77.1 … 90.5 W/(m·K)，覆盖了下沿 Wiedemann–Franz 的 78 与上沿 90。
    /// 带宽的下游灵敏度（推理，非实测）见 HANDOVER「0.-6H 第七轮」灵敏度表。
    /// </summary>
    public static double PtThermalK(double tC) => 71.6 + 0.0106 * tC;

    /// <summary>导热系数的实测覆盖上界 °C（Kaye &amp; Laby 表到 973.2 K）。超出即「推荐值外推，无本项目实测」。</summary>
    public const double PtThermalKMeasuredMaxC = 700.0;

    /// <summary>导热系数的带宽（相对值）。物理把关人 2026-09-18 定：±8 %。</summary>
    public const double PtThermalKBandFrac = 0.08;

    /// <summary>导热系数带宽下沿 W/(m·K)（1150 °C 处 = 77.1，覆盖 Wiedemann–Franz 的 78）。</summary>
    public static double PtThermalKLow(double tC) => PtThermalK(tC) * (1.0 - PtThermalKBandFrac);
    /// <summary>导热系数带宽上沿 W/(m·K)（1150 °C 处 = 90.5，覆盖那一支实测的 90）。</summary>
    public static double PtThermalKHigh(double tC) => PtThermalK(tC) * (1.0 + PtThermalKBandFrac);

    /// <summary>导热系数的核对点温度 °C（Kaye &amp; Laby 第 2.3.7 小节的 273.2/373.2/573.2/973.2 K）。</summary>
    public static readonly double[] KRefTempsC = { 0.05, 100.05, 300.05, 700.05 };
    /// <summary>上列温度处 Kaye &amp; Laby 给的纯铂导热系数 W/(m·K)。**核对点，不是本式的拟合点**。</summary>
    public static readonly double[] KRefKWmK = { 72.0, 72.0, 73.0, 78.0 };

    /// <summary>
    /// 纯铂比热 J/(kg·K)。**2026-09-18（Opus 5）重定**：由 <c>133.0 + 0.0135·T</c> 改为
    /// <see cref="CpFitTempsC"/>／<see cref="CpFitCpJKgK"/> 四点的最小二乘直线。
    ///
    /// **出处**：Kaye &amp; Laby, *Tables of Physical and Chemical Constants*（NPL 网络版），
    /// 第 2.3.6 小节 Specific heat capacities of metals, alloys and miscellaneous substances，纯铂行
    /// （273/373/573/773 K = 0.132/0.135/0.141/0.146 J·g⁻¹·K⁻¹，本处按 0/100/300/500 °C 取；
    /// 273 K 与 0 °C 差 0.15 K，远小于该表 ±1 J/(kg·K) 的分辨率）
    /// https://web.archive.org/web/2016/http://www.kayelaby.npl.co.uk/general_physics/2_3/2_3_6.html
    ///
    /// **为什么改**：旧式常温对得上（133 vs 132，0.8 %），但**斜率只有文献的一半**
    /// （K&amp;L 273→773 K 斜率 ≈ 0.028 J/(kg·K)/K，旧式 0.0135）⇒ 300 °C 低 2.8 %、500 °C 低 4.3 %、
    /// 1150 °C 低 10 %（148.5 vs 164.5）。旧式在仓库里查不到出处（2026-09-18 旁证 B 第 5.2 小节「纯铂比热与 APP 现用式对照」）。
    /// 物理把关人 2026-09-18 无异议：Dulong–Petit + 电子项估 cp(1150 °C) 约 165～175 J/(kg·K)，
    /// 新式给 164.5（落在下沿），旧式 148.5 偏低 10～15 %，方向是**偏保守**（把热容算小 ⇒ 升温所需功率算小）。
    ///
    /// 拟合残差（本式对四个原始点）：−0.169 / +0.017 / +0.390 / −0.237 J/(kg·K)，最大 0.39（0.28 %）。
    /// 覆盖区间 0–500 °C 是实测点范围，**500 °C 以上是外推**（K&amp;L 表到 773 K 为止）。
    /// 门：R48ThermalSourceTests 按这四点独立复算最小二乘，逐位对 <see cref="CpFitA"/>／<see cref="CpFitB"/>。
    /// </summary>
    public static double PtCp(double tC) => CpFitA + CpFitB * tC;

    /// <summary>比热拟合的原始点温度 °C（Kaye &amp; Laby 第 2.3.6 小节纯铂行 273/373/573/773 K）。**唯一来源**。</summary>
    public static readonly double[] CpFitTempsC = { 0.0, 100.0, 300.0, 500.0 };
    /// <summary>上列温度处的比热 J/(kg·K)（同上出处）。</summary>
    public static readonly double[] CpFitCpJKgK = { 132.0, 135.0, 141.0, 146.0 };
    /// <summary>比热最小二乘直线的截距 J/(kg·K)（由上面四点算出；门按点复算核对）。</summary>
    public const double CpFitA = 132.16949152542373;
    /// <summary>比热最小二乘直线的斜率 J/(kg·K²)（同上）。</summary>
    public const double CpFitB = 0.028135593220338983;
    /// <summary>比热的实测覆盖上界 °C（K&amp;L 表到 773 K）。超出即外推。</summary>
    public const double PtCpMeasuredMaxC = 500.0;

    // ---------------- 铂：焊接变形判据用的物性（文献值，非本项目实测） ----------------
    //
    // 只服务于 WeldDistortion。它们不进热/电求解链，故与上面的实测拟合分开放，
    // 免得被误当成同等可信的量。

    /// <summary>线膨胀系数 1/K（室温 8.8e-6，到熔点均值约 1.0e-5，取均值）</summary>
    public const double PtAlphaExp = 1.0e-5;
    /// <summary>杨氏模量 Pa（室温 168 GPa）</summary>
    public const double PtYoungPa = 168e9;
    /// <summary>泊松比</summary>
    public const double PtPoisson = 0.39;
    /// <summary>熔化潜热 J/kg</summary>
    public const double PtLatentFusion = 113e3;
    /// <summary>
    /// 0 °C→熔点的平均比热 J/(kg·K)。
    ///
    /// ⚠ **仍开放（2026-09-18，Opus 5 记，本轮不动）**：<see cref="PtCp"/> 本轮按 Kaye &amp; Laby 重定之后，
    /// 同一个量由新式取中点是 PtCp(884.1) ≈ 157 J/(kg·K)，与这里的 145 差 8 %。
    /// 不跟着改的原因：这个常数只进 <see cref="WeldDistortion"/>（焊接屈曲下界），
    /// 而那条下界是**约束盒的下角**之一 —— 动它会直接挪板厚下界与铂重，
    /// 属于另一件事，要动得单独立项、单独复核。改之前，两个数不一致这件事**要摆在这里**，
    /// 不许让人以为 145 与新的 <see cref="PtCp"/> 是同一支来源。
    /// </summary>
    public const double PtCpMeanToMelt = 145.0;

    // ---------------- 空气（自然对流用） ----------------

    /// <summary>空气导热系数 W/(m·K)，T 为绝对温度 K，250–1600 K 有效</summary>
    public static double AirK(double tK)
        => 1.5207e-11 * tK * tK * tK - 4.8574e-8 * tK * tK + 1.0184e-4 * tK - 3.9333e-4;

    /// <summary>动力粘度 Pa·s，Sutherland</summary>
    public static double AirMu(double tK) => 1.458e-6 * Math.Pow(tK, 1.5) / (tK + 110.4);

    /// <summary>密度 kg/m^3，理想气体 @1 atm</summary>
    public static double AirRho(double tK) => 101325.0 / (287.05 * tK);

    public static double AirNu(double tK) => AirMu(tK) / AirRho(tK);

    public const double AirPr = 0.71;

    public const double Sigma = 5.670374e-8;          // Stefan-Boltzmann

    // ---------------- 自然对流关联式 ----------------

    /// <summary>
    /// 水平圆柱自然对流 (Churchill-Chu)。tSurfC/tAmbC 摄氏，d 米。返回 h [W/m²K]。
    /// </summary>
    public static double HConvHorizCylinder(double tSurfC, double tAmbC, double d)
    {
        double dT = tSurfC - tAmbC;
        if (dT <= 0.01 || d <= 0) return 0.0;
        double tf = (tSurfC + tAmbC) * 0.5 + 273.15;
        double nu = AirNu(tf), k = AirK(tf), alpha = nu / AirPr;
        double ra = 9.81 * (1.0 / tf) * dT * d * d * d / (nu * alpha);
        if (ra < 1e-3) return 0.0;
        double den = Math.Pow(1.0 + Math.Pow(0.559 / AirPr, 9.0 / 16.0), 8.0 / 27.0);
        double s = 0.60 + 0.387 * Math.Pow(ra, 1.0 / 6.0) / den;
        return s * s * k / d;
    }

    /// <summary>竖直圆柱/平板自然对流 (Churchill-Chu)。l = 特征高度 m。</summary>
    public static double HConvVertical(double tSurfC, double tAmbC, double l)
    {
        double dT = tSurfC - tAmbC;
        if (dT <= 0.01 || l <= 0) return 0.0;
        double tf = (tSurfC + tAmbC) * 0.5 + 273.15;
        double nu = AirNu(tf), k = AirK(tf), alpha = nu / AirPr;
        double ra = 9.81 * (1.0 / tf) * dT * l * l * l / (nu * alpha);
        if (ra < 1e-3) return 0.0;
        double den = Math.Pow(1.0 + Math.Pow(0.492 / AirPr, 9.0 / 16.0), 8.0 / 27.0);
        double s = 0.825 + 0.387 * Math.Pow(ra, 1.0 / 6.0) / den;
        return s * s * k / l;
    }

    /// <summary>
    /// 平板强制对流 h [W/m²K]。v = 来流风速 m/s，L = 特征长度 m。
    /// Re &lt; 5e5 层流 Nu = 0.664·Re^0.5·Pr^(1/3)；否则湍流 Nu = 0.037·Re^0.8·Pr^(1/3)。
    /// 压缩空气射流冲击的实际 h 更高且强烈依赖喷嘴几何，此式为保守下界。
    /// </summary>
    public static double HConvForcedPlate(double tSurfC, double tAmbC, double l, double v)
    {
        if (v <= 1e-6 || l <= 0) return 0.0;
        double tf = (tSurfC + tAmbC) * 0.5 + 273.15;
        double nu = AirNu(tf), k = AirK(tf);
        double re = v * l / nu;
        double nuNum = re < 5e5
            ? 0.664 * Math.Sqrt(re) * Math.Pow(AirPr, 1.0 / 3.0)
            : 0.037 * Math.Pow(re, 0.8) * Math.Pow(AirPr, 1.0 / 3.0);
        return nuNum * k / l;
    }

    /// <summary>自然与强制对流混合：h = (h_nat³ + h_forced³)^(1/3)</summary>
    public static double HConvMixed(double hNat, double hForced)
        => Math.Pow(hNat * hNat * hNat + hForced * hForced * hForced, 1.0 / 3.0);

    /// <summary>辐射等效换热系数 W/m²K</summary>
    public static double HRad(double eps, double tSurfC, double tAmbC)
    {
        double dT = tSurfC - tAmbC;
        if (Math.Abs(dT) < 1e-6) return 0.0;
        double ts = tSurfC + 273.15, ta = tAmbC + 273.15;
        return eps * Sigma * (Math.Pow(ts, 4) - Math.Pow(ta, 4)) / dT;
    }
}
