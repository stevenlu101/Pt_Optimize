using System;

namespace PtOptimize.Core;

/// <summary>
/// 物性库。铂电阻率采用用户提供的二次拟合（SSE = 0.0115，0–1500°C）。
/// 其余为文献拟合，可在 UI 中被输入值覆盖的部分已单独暴露。
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

    /// <summary>铂导热系数 W/(m·K)。71.6 @0°C -> 85.4 @1300°C</summary>
    public static double PtThermalK(double tC) => 71.6 + 0.0106 * tC;

    /// <summary>铂比热 J/(kg·K)。133 @0°C -> ~151 @1300°C</summary>
    public static double PtCp(double tC) => 133.0 + 0.0135 * tC;

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
    /// <summary>0 °C→熔点的平均比热 J/(kg·K)</summary>
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
