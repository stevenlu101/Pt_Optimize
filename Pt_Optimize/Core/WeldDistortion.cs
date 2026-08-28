using System;

namespace PtOptimize.Core;

/// <summary>
/// **焊接变形定下界** —— 用户 2026-08-14：
/// 「这个下限必须是工艺能够焊接铂金不变形的情况厚度」「先计算…这应该是第一步」。
///
/// 此前程序用的 0.4 mm 来自「太薄没意义」的拍板，是个任意数。本类把它换成可算的量。
///
/// ── 能算的那一半：**屈曲变形（buckling distortion）**
///
/// 焊缝冷却时纵向收缩，在焊缝附近留下一条受拉区，靠板的其余部分受压来平衡。
/// 板越薄，抗压屈曲能力越弱（σ_cr ∝ t²），到某个厚度以下就会失稳鼓曲 —— 这就是
/// 薄板焊完像波浪的原因，也是「焊接变形」在工程上最主要的一种。
///
///   ① 收缩力（Okerblom / White 的 tendon force）：
///        F = C·(α·E/(ρ·c))·(η·Q/v)，  C ≈ 0.2（在钢上标定的经验系数）
///   ② 全熔透所需线能量：
///        η·Q/v = ρ·A_f·(c̄·(T_m−T_0) + L_f)/η_melt ，  A_f ≈ β·t²（焊道宽 ≈ β·t）
///        ⇒ 代入后 **ρ 正好约掉**
///   ③ 远场压应力 σ_c = F/(b·t)；板屈曲临界 σ_cr = k_b·π²E/(12(1−ν²))·(t/b)²
///   ④ σ_c > σ_cr 即屈曲，解出
///
///        **t_min = 12(1−ν²)·C·β·α·(c̄ΔT_m + L_f)·b / (k_b·π²·c·η_melt)**
///
///   **E 也约掉了** ⇒ 结果只取决于材料的热学量与三个工艺系数，且**正比于板的无支撑宽度 b**。
///   所以它给不出「一个厚度」，只给出「t/b 的下限」——必须按每条焊缝各自的 b 来算。
///
/// ── 算不出的那一半：**烧穿 / 熔池失控**
///
/// 薄板的另一条下界是熔池能不能被表面张力托住、以及热输入波动会不会直接烧穿。
/// 前者可估（毛细长度 √(2γ/ρg)，铂约 4.3 mm，对 1 mm 级板不是限制），
/// 后者取决于焊接方法与设备稳定性（TIG / 激光 / 电阻缝焊差一个量级），
/// **不可能从材料常数推出来，必须由现场焊工/工艺给**。
///
/// ⇒ 真正的下界 = max(本类算出的屈曲下界, 现场给的烧穿下界)。
///
/// ── 可信度锚点
///
/// 同一套公式代入低碳钢，给出 t_min ≈ 0.0073·b ⇒ 300 mm 宽钢板约 2.2 mm，
/// 与「薄钢板 2–3 mm 以下焊后必鼓曲」的车间常识一致。**公式先在钢上对得上，再用于铂。**
/// </summary>
public static class WeldDistortion
{
    /// <summary>tendon force 的经验系数（Okerblom/White，在钢上标定）</summary>
    public const double TendonC = 0.2;
    /// <summary>焊道宽/板厚比 β：自熔对接焊常规 1.5–3</summary>
    public const double BeadWidthRatio = 2.0;
    /// <summary>熔化效率 η_melt：电弧焊常规 0.3–0.5</summary>
    public const double MeltEfficiency = 0.35;
    /// <summary>
    /// 板屈曲系数 k_b —— **四边简支**长板 = 4（Timoshenko 经典值）。
    /// ⚠ **法兰盘不是这一种**，别拿它算法兰：见 <see cref="PlateBucklingKFreeEdge"/>。
    /// 本常数只服务于 `--welddistort` 报告里的**对照列**。
    /// </summary>
    public const double PlateBucklingK = 4.0;

    /// <summary>
    /// 板屈曲系数 k_b —— **三边简支、一边自由**的长板 ≈ 0.43（Timoshenko k≈0.425，工程取 0.43）。
    /// **法兰盘用的是这一个**：内边焊在管上、外边自由。
    ///
    /// ★★ 2026-08-28：此前它是**14 处无名字面量** `kb: 0.43`（FinalDesign 1 处 + Program 13 处），
    ///   而被命名、被 XML 文档、被报告表头印出来的却是 <see cref="PlateBucklingK"/> = 4.0
    ///   —— 同一页自相矛盾。更要命的是**任何新调用点忘写 `kb:` 就静默拿到 4.0**，
    ///   下界小 9.3 倍（0.06 而不是 0.55 mm），不报错、格式正常、结论错。
    ///   ⇒ 给它名字，14 处字面量全部换成它。
    ///
    /// ⚠ 仍未论证的一点（**记着，别当已解决**）：把「一边自由的长条板」这个理想化
    ///   用在**内边焊死、外边自由的环**上，是一次**没有论证的模型替换** ——
    ///   环缝是周向收缩把环往里箍，与长条板受单向压不是一回事。
    /// </summary>
    public const double PlateBucklingKFreeEdge = 0.43;

    public sealed class Result
    {
        /// <summary>t_min / b —— 无量纲斜率，材料与工艺一定就定了</summary>
        public double SlopePerB;
        /// <summary>给定无支撑宽度下的最小厚度 mm</summary>
        public double TMinMm;
        /// <summary>用到的无支撑宽度 mm</summary>
        public double WidthMm;
        public string Note = "";
    }

    /// <summary>
    /// 屈曲下界的斜率 t_min/b。
    /// </summary>
    /// <param name="alphaExp">线膨胀系数 1/K</param>
    /// <param name="cpMean">0→熔点平均比热 J/(kg·K)</param>
    /// <param name="latent">熔化潜热 J/kg</param>
    /// <param name="meltRiseK">熔点与初温之差 K</param>
    /// <param name="poisson">泊松比</param>
    public static double Slope(double alphaExp, double cpMean, double latent,
                               double meltRiseK, double poisson,
                               double beta = BeadWidthRatio, double etaMelt = MeltEfficiency,
                               double kb = PlateBucklingK, double tendonC = TendonC)
    {
        double num = 12.0 * (1 - poisson * poisson) * tendonC * beta * alphaExp
                     * (cpMean * meltRiseK + latent);
        double den = kb * Math.PI * Math.PI * cpMean * etaMelt;
        return num / den;
    }

    /// <summary>铂的屈曲下界。<paramref name="widthMm"/> 是**该条焊缝所在板的无支撑宽度**。</summary>
    public static Result ForPt(double widthMm, double startTempC = 20,
                               double beta = BeadWidthRatio, double etaMelt = MeltEfficiency,
                               double kb = PlateBucklingK)
    {
        double s = Slope(Materials.PtAlphaExp, Materials.PtCpMeanToMelt, Materials.PtLatentFusion,
                         Materials.PtMeltC - startTempC, Materials.PtPoisson, beta, etaMelt, kb);
        return new Result { SlopePerB = s, WidthMm = widthMm, TMinMm = s * widthMm };
    }

    /// <summary>
    /// **圆筒侧的屈曲判据**：环缝在筒上会不会把筒压屈。
    ///
    /// 与平板/环完全不同的一条路，而且结论是「与厚度无关」：
    ///   收缩力     F   = K·t²      （K = C·β·α·E·(c̄ΔT_m+L_f)/(c̄·η_melt)，见 <see cref="Slope"/> 的推导）
    ///   轴压应力   σ_c = F/(2πR·t) = K·t/(2πR)
    ///   筒轴压临界 σ_cr = E·t/(R·√(3(1−ν²)))
    ///   σ_c > σ_cr  ⇔  K > 2πE/√(3(1−ν²))    ← **t 与 R 同时约掉**
    ///
    /// 即：某种材料的筒要么在任何厚度下都会被环缝压屈，要么在任何厚度下都不会。
    /// 返回「实际 K / 临界 K」，&lt;1 即永不屈曲。铂实测约 0.007 ⇒ 差两个量级，**永不屈曲**。
    /// 所以管壁的下界只能由烧穿定，不可能由变形定。
    /// </summary>
    public static double ShellBucklingRatio(double alphaExp, double cpMean, double latent,
                                            double meltRiseK, double poisson,
                                            double beta = BeadWidthRatio,
                                            double etaMelt = MeltEfficiency,
                                            double tendonC = TendonC)
    {
        // K/E —— E 在两边都出现，故只算 K/E 与临界值 2π/√(3(1−ν²)) 比
        double kOverE = tendonC * beta * alphaExp * (cpMean * meltRiseK + latent) / (cpMean * etaMelt);
        double crit = 2 * Math.PI / Math.Sqrt(3 * (1 - poisson * poisson));
        return kOverE / crit;
    }

    /// <summary>铂的圆筒屈曲比值（&lt;1 = 任何壁厚都不会被环缝压屈）</summary>
    public static double PtShellBucklingRatio(double startTempC = 20)
        => ShellBucklingRatio(Materials.PtAlphaExp, Materials.PtCpMeanToMelt,
                              Materials.PtLatentFusion, Materials.PtMeltC - startTempC,
                              Materials.PtPoisson);

    /// <summary>
    /// 熔池被表面张力托住的最大宽度 mm（毛细长度 √(2γ/(ρ_l·g))）。
    /// 超过它，仰焊/无衬垫的熔池会掉下去。对 1 mm 级铂板不是限制，列出只为说明
    /// 「烧穿」那条下界不是这个机理，而是热输入控制精度。
    /// </summary>
    public static double CapillaryWidthMm(double gammaNPerM = 1.75, double rhoLiquid = 19000)
        => Math.Sqrt(2 * gammaNPerM / (rhoLiquid * 9.81)) * 1e3;
}
