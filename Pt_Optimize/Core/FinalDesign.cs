namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **定案几何的唯一来源**（2026-08-15 建立，08-16 改为双档）。
///
/// 为什么要有这个文件：定案值此前是**各命令各手抄一份**。
/// `--final2` 往前推进之后，`--hotspot` 与 `--busbarplan` 还钉着几代之前的几何
/// （管壁 0.6、舌厚 {1.37,2.02,1.80,1.04}、管保温 10 mm、无管孔环），
/// 它们照样跑得出漂亮的数 —— 但那是**另一个设计**的数。
/// 这正是 HANDOVER §1.8「安静失败」家族的形状：不报错、格式正常、结论错。
///
/// ⇒ 与判据收敛到 <c>LineRunner.Judge</c> 同理：**几何也只能有一个来源**。
///   任何辅助命令要「对着定案构型量」，就从这里取，不许再抄。
///   `--final2` 是**唯一**有权更新这里的地方（它是定尺寸器）。
///
/// ★★ 2026-08-16 改为**双档**：<see cref="W08"/> 与 <see cref="W06"/> 都全判据通过，
///   差别只在裕度与铂重，取舍属于业主。原来「改三行切档」的做法本身就是
///   手抄的另一种形式 —— 两档并存、由 <see cref="Current"/> 指定，才是单一来源。
///
/// ⚠ 改这里之前先想清楚：下面每个数都是某一轮实测收敛的结果，
///   不是可以随手调的参数。改了就要重跑 `--final2` 复核全判据。
/// </summary>
public sealed class FinalDesign
{
    // ── 标识与出处
    public string Name = "";
    /// <summary>本组数值出自哪一次运行 —— 报告里要能追溯到源头</summary>
    public string Provenance = "";
    /// <summary>本档被哪条约束咬住（说明是「贴着谁」，不是「过没过」）</summary>
    public string Binding = "";

    // ── 管
    public double WallMm;
    public double TubeInsulMm = 5.0;
    public double[] SetpointC = { 1150.0, 1080.0, 1050.0 };

    // ── 法兰（四片：入口 / 共用1 / 共用2 / 出口）
    public double DiscRadiusMm = 30.0;
    public double TabLengthMm = 90.0;
    public double TabHalfWidthMm = 15.0;
    /// <summary>舌根过渡圆角 R。⚠ 网格 2 mm，小于它的圆角在场里看不出来（§1.8 的分辨率坑）</summary>
    public double TabFilletMm = 3.0;
    public double[] TabThickMm = new double[4];
    public double[] TabInsulMm = { 18.7, 1.6, 1.4, 3.9 };

    // ── 管孔渐变环：**相对量**（绝对值写法已两次造成安静失败，见 §1.8 ⑥⑦）
    /// <summary>环宽 mm，相对管孔外扩；两级台阶在 孔+w 与 孔+2w</summary>
    public double RingWidthMm = 3.0;
    /// <summary>内圈厚度倍率（相对板厚）；外圈取 1 + 0.4(μ−1)</summary>
    public double[] RingMul = new double[4];

    // ── 压接
    public double ClampLengthMm = 40.0;
    public double ClampTempC = 450.0;

    // ── 实测结果（供报告与 UI 直接引用，避免再去翻日志）
    public double TotalMassG, TubeMassG, FlangeMassG;
    /// <summary>外层耦合的**剩余误差估计** K（不是步长，见 §1.85）</summary>
    public double ResidualK;

    /// <summary>
    /// 五条判据的**复核实测值**（2026-08-16 由 <see cref="BuildCase"/> + `LineRunner.Run`
    /// 重跑得到，checkRamp=true，两档均收敛且全过）。
    ///
    /// ⚠ 为什么放在这里而不是各处硬编码：说明书页此前自己抄了一份，而那一份是
    ///   **定案当天（08-15）那次运行**的数 —— 收敛度量与 ②″ 限值都是在那之后才改的。
    ///   复核发现 ②′ 与 ③ 两项与实算对不上，**且两档之间的大小关系是反的**
    ///   （抄的说 0.8 档 ③ 更小，实算是 0.8 档 ③ 更大）。
    ///   判定结论没变（两档仍全过、铂重不变），但「哪一档在 ③ 上更宽裕」这句话说反了。
    ///   ⇒ 收敛到本处。要更新就点界面上的「▶ 复现定案」重跑一遍照抄。
    /// </summary>
    public double RampH, DiscOverK, HoleFluxW, FlangeDipK, TubeJ;

    /// <summary>某条判据的裕度 %（(限−实)/限）。方向性判据（限 0）不适用，返回 NaN。</summary>
    public static double Margin(double actual, double limit) =>
        System.Math.Abs(limit) < 1e-9 ? double.NaN : (limit - actual) / System.Math.Abs(limit) * 100.0;

    public double HoleRadiusMm => WallMm + 25.0;
    public double[] RingRadiiMm =>
        new[] { HoleRadiusMm + RingWidthMm, HoleRadiusMm + 2 * RingWidthMm };
    /// <summary>外圈倍率（内圈的 40 % 过渡回板身）</summary>
    public double RingMulOuter(int j) => 1 + (RingMul[j] - 1) * 0.4;

    /// <summary>
    /// 深拷贝 —— 供**参数扰动验证**用（`--vary`）：扰动必须作用在副本上，
    /// 否则 <see cref="W08"/>/<see cref="W06"/> 这两个 static 实例会被就地改掉，
    /// 后面每一次「定案」都变成上一次扰动的结果（典型的静默污染）。
    /// </summary>
    public FinalDesign Clone()
    {
        var c = (FinalDesign)MemberwiseClone();
        c.SetpointC = (double[])SetpointC.Clone();
        c.TabThickMm = (double[])TabThickMm.Clone();
        c.TabInsulMm = (double[])TabInsulMm.Clone();
        c.RingMul = (double[])RingMul.Clone();
        return c;
    }

    /// <summary>按本定案构型造第 j 片（0=入口, 1=共用1, 2=共用2, 3=出口）。</summary>
    public FlangePlate Plate(int j, double discFloorMm)
    {
        double td = System.Math.Max(TabThickMm[j], discFloorMm);
        return new FlangePlate
        {
            DiscRadiusMm = DiscRadiusMm, HoleRadiusMm = HoleRadiusMm,
            TabEndXMm = -TabLengthMm, TabEndHalfWidthMm = TabHalfWidthMm,
            ThicknessMm = td,
            DiscStepRadiiMm = RingRadiiMm,
            DiscStepThicknessMm = new[] { td * RingMul[j], td * RingMulOuter(j) },
            TabThicknessMm = double.NaN,
            InsulBoundaryXMm = double.NaN, TabInsulThickMm = TabInsulMm[j],
            TabParallel = true, TabFilletMm = TabFilletMm,
            WeldFilletLegMm = System.Math.Max(td, WallMm)
        };
    }

    /// <summary>
    /// ★★★★★ 由本定案档**造出可直接求解的整线算例** —— 复现定案数字的唯一入口。
    ///
    /// 为什么必须在这里：此前 `--busbarplan`、`--hotspot` 各手抄一份构造代码，
    /// 界面上则**根本没有**能复现的路径（页面控件表达不了渐变环与逐片舌保温）。
    /// 三份手抄已经开始漂：`--hotspot` 把夹持温度写成常量 450 而不是 <see cref="ClampTempC"/>，
    /// 网格细化半径写 45 而 <c>LineCase</c> 的默认是 50 —— 都还没出事，
    /// 但形状与 §1.8「同一个数存两处然后悄悄漂开」完全一样。
    /// ⇒ 构造收敛到这一处；调用方只准传「基准工艺参数」与「要不要判升温」。
    ///
    /// ⚠ 这里**不设任何几何默认值**：几何全部读本实例的字段。
    ///   若某天新增一个几何自由度，加在字段上，本方法自动带上，不会漏。
    /// </summary>
    /// <param name="baseInputs">基准工艺参数（材料、散热、力学等），几何会被本档覆盖</param>
    /// <param name="checkRamp">是否连 ① 升温一起判。判它更慢，但**少判一条就不是全判据**</param>
    public LineCase BuildCase(DesignInputs baseInputs, bool checkRamp = true)
    {
        var p = SegmentSolver.Clone(baseInputs);
        p.WallMinMm = WallMm;
        p.Layer1.ThicknessMm = TubeInsulMm;
        p.Layer1.Enabled = TubeInsulMm > 1e-6;
        p.FlangeInsulThickMm = 20; p.FlangeInsulated = true;
        p.BusbarClampLengthMm = ClampLengthMm;
        p.BusbarClampTempC = ClampTempC;

        // 圆盘的焊接屈曲下界：板厚不得低于它（`Plate` 里取大）
        double discFloor = WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                           * (DiscRadiusMm - 26.0) * baseInputs.WeldSafetyFactor;

        return new LineCase
        {
            Base = p,
            WallMm = WallMm,
            UseMeasuredCurrent = false,      // 由控温反算 —— 第一性
            CheckRamp = checkRamp,
            SetpointC = SetpointC,
            FlangePlates = new[] { Plate(0, discFloor), Plate(1, discFloor),
                                   Plate(2, discFloor), Plate(3, discFloor) },
            ClampTempC = new[] { ClampTempC, ClampTempC, ClampTempC, ClampTempC }
        };
    }

    public string Describe() =>
        $"[{Name}] 管壁 {WallMm:0.0}／管保温 {TubeInsulMm:0}／盘Ø{2 * DiscRadiusMm:0}／" +
        $"舌 {TabLengthMm:0}×{2 * TabHalfWidthMm:0}／板厚 {string.Join("/", TabThickMm)}／" +
        $"舌保温 {string.Join("/", TabInsulMm)}／" +
        $"环 r≤孔+{RingWidthMm:0}→×{string.Join("/", RingMul)}／舌根圆角 R{TabFilletMm:0}／" +
        $"压接 {ClampLengthMm:0} 夹 {ClampTempC:0} °C　合计 {TotalMassG:0} g";

    // ════════════════════════════════════════════════════════════════════
    // 两个定案档。**都全判据通过**，差别只在裕度与铂重。
    // 出处：`--final2` 可行性阶梯 D7（舌厚→B 净流入靶 2 W／环倍率→②″／舌保温抗饱和接力）；
    //       ②″ 限值 5 K（现场控温精度）、管 J 限值 12（现场：一般 15，管壁 0.6 时 12 是极限）。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>留余量档：没有任何判据贴限值。</summary>
    public static readonly FinalDesign W08 = new()
    {
        Name = "管壁 0.8 · 留余量",
        Provenance = "尺寸出自 --final2 可行性阶梯 D7（2026-08-15）；判据值 2026-08-16 复核重跑",
        Binding = "无 —— 每条判据都有裕度：②″ 80 %／管 J 21 %／③ 38 %／壁厚高于焊接下界 33 %",
        WallMm = 0.8,
        TabThickMm = new[] { 2.11, 3.40, 3.18, 1.76 },
        RingMul = new[] { 1.24, 1.24, 1.24, 1.24 },
        TotalMassG = 3117, TubeMassG = 2466, FlangeMassG = 652, ResidualK = 0.65,
        RampH = 0.057, DiscOverK = 0.981, HoleFluxW = 1.669, FlangeDipK = 6.184, TubeJ = 9.506,
    };

    /// <summary>底档：可行域的底。焊接烧穿下界与管 J 12 **在同一点咬住**。</summary>
    public static readonly FinalDesign W06 = new()
    {
        Name = "管壁 0.6 · 底档",
        Provenance = "尺寸出自 --final2 可行性阶梯 D7（2026-08-15）；判据值 2026-08-16 复核重跑",
        Binding = "焊接烧穿下界 0.6 mm（余量 0）＋ 管 J 10.96/12（余量 9 %）—— 两条同点咬住",
        WallMm = 0.6,
        TabThickMm = new[] { 1.82, 2.91, 2.71, 1.49 },
        RingMul = new[] { 1.22, 1.22, 1.22, 1.22 },
        TotalMassG = 2398, TubeMassG = 1842, FlangeMassG = 557, ResidualK = 0.75,
        RampH = 0.079, DiscOverK = 1.121, HoleFluxW = 1.912, FlangeDipK = 5.297, TubeJ = 10.961,
    };

    public static readonly FinalDesign[] All = { W08, W06 };

    /// <summary>
    /// 当前生效的档。**默认取保守的 0.8** —— 业主尚未在两档间拍板，
    /// 而 0.6 把壁厚压在焊接下界上、管 J 只剩 9 %，这两条都属于现场判断，不属于计算。
    /// </summary>
    public static FinalDesign Current = W08;

    /// <summary>按管壁取档（命令行 `--wall 0.6`）。找不到返回 null —— **不要静默回退**。</summary>
    public static FinalDesign? ByWall(double wallMm)
    {
        foreach (var d in All)
            if (System.Math.Abs(d.WallMm - wallMm) < 1e-6) return d;
        return null;
    }

    /// <summary>解析 `--wall &lt;mm&gt;`，缺省用 <see cref="Current"/>；给了但不认识就抛，不静默。</summary>
    public static FinalDesign Select(string[] args)
    {
        int i = System.Array.IndexOf(args, "--wall");
        if (i < 0 || i + 1 >= args.Length) return Current;
        if (!double.TryParse(args[i + 1], out double w))
            throw new System.ArgumentException($"--wall 的值解析不了：{args[i + 1]}");
        return ByWall(w) ?? throw new System.ArgumentException(
            $"没有管壁 {w:0.0} mm 的定案档。现有：{string.Join("、", System.Linq.Enumerable.Select(All, d => d.WallMm.ToString("0.0")))}");
    }
}
