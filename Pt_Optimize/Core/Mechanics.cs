using System;

namespace PtOptimize.Core;

/// <summary>
/// 力学校核：铂管与法兰舌片在工作温度下的应力。
///
/// 纯铂在 1300 °C 的持久强度极低（文献量级 0.5–2 MPa @ 10⁴ h），
/// 而减薄同时抬高应力与电流密度，二者同向恶化。故薄壁方案必须做此校核。
///
/// 本模块只给出**应力**（几何与载荷决定，可靠）；
/// **许用应力**必须由用户按材料牌号、温度、寿命提供——
/// 程序不内置蠕变数据，改为反算「所需许用应力」，避免用猜的数据得出结论。
/// </summary>
public sealed class MechResult
{
    // 管
    public double TubeHoopMPa;        // 环向（内压）
    public double TubeBendMPa;        // 弯曲（自重 + 玻璃）
    public double TubeVonMisesMPa;    // 合成
    public double TubeReqAllowMPa;    // ×安全系数后所需许用应力

    // 法兰舌片
    public double TabBendSimplyMPa;   // 两端支承
    public double TabBendCantMPa;     // 纯悬臂
    public double TabReqAllowMPa;

    public double SafetyFactor;

    // 许用应力与利用率（>1 = 超限）
    public double TubeAllowMPa, TabAllowMPa;
    public double TubeUtil, TabUtil;
    public double TubeTempC, TabTempC;
}

public static class Mechanics
{
    /// <param name="glassHeadM">玻璃液柱高度 m（内压来源）</param>
    /// <param name="supportSpanMm">管的支承跨距 mm</param>
    /// <param name="sf">安全系数</param>
    /// <summary>
    /// R47 复修 M10：**只校核管**时的占位板 —— <see cref="Check"/> 的签名要一块板算舌片那几项，而 LineRunner 的 ④ 强度
    /// 只读 TubeUtil／TubeAllowMPa（法兰不承重）。它不进任何法兰判定；图纸路径的保温分界那类**不许**再拿默认板。
    /// </summary>
    public static readonly FlangePlate TubeOnlyPlate = new();

    public static MechResult Check(DesignInputs p, double wallMm, double flangeThickMm,
                                   FlangePlate g, double? glassHeadOverrideM = null,
                                   double? supportSpanOverrideMm = null, double? sfOverride = null)
    {
        double glassHeadM = glassHeadOverrideM ?? p.GlassHeadM;
        double supportSpanMm = supportSpanOverrideMm ?? p.SupportSpanMm;
        double sf = sfOverride ?? p.SafetyFactor;
        var res = new MechResult { SafetyFactor = sf };
        const double gAcc = 9.81;

        // ── 铂管 ──────────────────────────────────────────────
        double ri = p.TubeIdMm * 0.5e-3, t = wallMm * 1e-3;
        double rm = ri + 0.5 * t;

        // 内压 = 玻璃液柱 + 流动压降（后者本工况仅数百 Pa，量级上可忽略但仍计入）
        double q = p.MassFlow / p.GlassDensity;
        double dpFlow = 128.0 * p.GlassViscosity * p.TubeLength * q
                        / (Math.PI * Math.Pow(p.TubeId, 4));
        double pInt = p.GlassDensity * gAcc * glassHeadM + dpFlow;
        res.TubeHoopMPa = pInt * rm / t * 1e-6;                     // σθ = p·r/t

        // 弯曲：铂管自重 + 管内玻璃，按简支梁
        double aPt = Math.PI * t * (p.TubeId + t);                  // m²
        double wPt = Materials.PtDensity * aPt * gAcc;              // N/m
        double wGlass = p.GlassDensity * Math.PI * ri * ri * gAcc;  // N/m
        double w = wPt + wGlass;
        double span = supportSpanMm * 1e-3;
        double m = w * span * span / 8.0;                           // 简支最大弯矩
        double zTube = Math.PI * rm * rm * t;                       // 薄壁圆管截面模量
        res.TubeBendMPa = zTube > 0 ? m / zTube * 1e-6 : 0;

        // 合成（薄壁：σθ 环向、σb 轴向，取 von Mises）
        double s1 = res.TubeHoopMPa, s2 = res.TubeBendMPa;
        res.TubeVonMisesMPa = Math.Sqrt(s1 * s1 - s1 * s2 + s2 * s2);
        res.TubeReqAllowMPa = res.TubeVonMisesMPa * sf;

        // ── 法兰舌片 ──────────────────────────────────────────
        // 自圆盘切点悬伸到末端，按变宽度梁的根部截面校核
        var (xt, wt) = g.Tangent();
        double tabLen = (xt - g.TabTipXMm) * 1e-3;                  // m
        double tf = flangeThickMm * 1e-3;
        double bRoot = 2 * wt * 1e-3;                               // 根部全宽 m

        // 舌片自重线载荷（用平均宽度）
        double bMean = (2 * wt + 2 * g.TabEndHalfWidthMm) * 0.5e-3;
        double wTab = Materials.PtDensity * bMean * tf * gAcc;      // N/m
        double zTab = bRoot * tf * tf / 6.0;                        // 矩形截面模量

        double mSimply = wTab * tabLen * tabLen / 8.0;
        double mCant = wTab * tabLen * tabLen / 2.0;
        res.TabBendSimplyMPa = zTab > 0 ? mSimply / zTab * 1e-6 : 0;
        res.TabBendCantMPa = zTab > 0 ? mCant / zTab * 1e-6 : 0;
        // 舌片由铜排夹支承（用户确认），故取两端支承而非悬臂
        res.TabReqAllowMPa = res.TabBendSimplyMPa * sf;

        return res;
    }

    /// <summary>
    /// 反解「强度允许的最小壁厚」：σ_vm(t)·SF = σ_allow(T, 寿命)。
    /// σ_vm 随 t 单调降，故二分求解，无需耦合解，极快。
    /// </summary>
    public static double MinWallForStrengthMm(DesignInputs p, FlangePlate g,
                                              double tubeTempC, double loMm = 0.15, double hiMm = 6.0)
    {
        double allow = MaterialDb.Get(p.GradeName).AllowableMPa(tubeTempC, p.DesignLifeHours, 1.0);
        double F(double t) => Check(p, t, 2.0, g).TubeVonMisesMPa * p.SafetyFactor - allow;
        if (F(hiMm) > 0) return double.NaN;          // 再厚也不够
        if (F(loMm) < 0) return loMm;                // 再薄也够
        for (int i = 0; i < 80; i++)
        {
            double m = 0.5 * (loMm + hiMm);
            if (F(m) > 0) loMm = m; else hiMm = m;
        }
        return 0.5 * (loMm + hiMm);
    }

    /// <summary>
    /// 沿舌片逐点校核（变宽度简支梁 + 变温度）。
    /// 最大弯矩在跨中而截面模量随宽度变化，且温度沿程下降 ——
    /// 三者不在同一位置取极值，故必须逐点扫描利用率。
    /// </summary>
    /// <param name="tabTempAt">给定 x[mm] 返回该处温度 °C</param>
    public static (double sigMaxMPa, double xAtMaxMm, double tAtMaxC,
                   double utilMax, double xAtUtilMaxMm, double tAtUtilMaxC)
        ScanTab(DesignInputs p, FlangePlate g, double flangeThickMm,
                Func<double, double> tabTempAt)
    {
        var (xt, _) = g.Tangent();
        double x0 = g.TabTipXMm, x1 = xt;          // 舌片自末端到切点
        double L = (x1 - x0) * 1e-3;               // m
        double tf = flangeThickMm * 1e-3;
        const int N = 401;
        double dx = (x1 - x0) / (N - 1);

        // 线载荷 w(x) = ρ·g·b(x)·t，b 为当地全宽
        double W(double xmm) => Materials.PtDensity * 9.81 * (2 * g.HalfWidth(xmm) * 1e-3) * tf;

        // 简支梁：左端反力 R = ∫w·(L−s)/L ds（s 自左端量）
        double rLeft = 0, wTot = 0;
        for (int i = 0; i < N; i++)
        {
            double xmm = x0 + i * dx, sm = (xmm - x0) * 1e-3;
            double wt = dx * 1e-3 * (i == 0 || i == N - 1 ? 0.5 : 1.0);
            rLeft += W(xmm) * (L - sm) / L * wt;
            wTot += W(xmm) * wt;
        }
        _ = wTot;

        double sigMax = 0, xSig = 0, tSig = 0;
        double utilMax = 0, xUtil = 0, tUtil = 0;
        double acc = 0, accM = 0;                  // ∫w ds 与 ∫w·(s−s')ds
        for (int i = 0; i < N; i++)
        {
            double xmm = x0 + i * dx, sm = (xmm - x0) * 1e-3;
            double wt = dx * 1e-3 * (i == 0 || i == N - 1 ? 0.5 : 1.0);
            // M(s) = R·s − ∫₀ˢ w(u)(s−u) du，用累积量递推
            accM += acc * dx * 1e-3;
            acc += W(xmm) * wt;
            double mBend = rLeft * sm - accM;
            double b = 2 * g.HalfWidth(xmm) * 1e-3;
            double z = b * tf * tf / 6.0;
            if (z <= 0) continue;
            double sig = Math.Abs(mBend) / z * 1e-6;      // MPa
            double tC = tabTempAt(xmm);
            double allow = MaterialDb.Get(p.GradeName).AllowableMPa(tC, p.DesignLifeHours, 1.0);
            double util = allow > 0 ? sig * p.SafetyFactor / allow : 999;
            if (sig > sigMax) { sigMax = sig; xSig = xmm; tSig = tC; }
            if (util > utilMax) { utilMax = util; xUtil = xmm; tUtil = tC; }
        }
        return (sigMax, xSig, tSig, utilMax, xUtil, tUtil);
    }

    /// <summary>叠加材料许用应力，给出利用率。tubeTempC / tabTempC 由热解给出。</summary>
    public static void ApplyAllowable(MechResult r, DesignInputs p,
                                      double tubeTempC, double tabTempC)
    {
        r.TubeTempC = tubeTempC; r.TabTempC = tabTempC;
        var g = MaterialDb.Get(p.GradeName);
        r.TubeAllowMPa = g.AllowableMPa(tubeTempC, p.DesignLifeHours, 1.0);
        r.TabAllowMPa = g.AllowableMPa(tabTempC, p.DesignLifeHours, 1.0);
        r.TubeUtil = r.TubeAllowMPa > 0 ? r.TubeReqAllowMPa / r.TubeAllowMPa : 999;
        r.TabUtil = r.TabAllowMPa > 0 ? r.TabReqAllowMPa / r.TabAllowMPa : 999;
    }
}
