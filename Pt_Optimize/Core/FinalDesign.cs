using System.Linq;

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

    /// <summary>
    /// ★★★★★ 非空 = **本档已失效，不得当作可交付结果引用**；内容说明为什么、下一步做什么。
    ///
    /// 为什么需要这个字段（2026-08-17）：判据 ⑤（舌片自由段 ≥ 100 mm）加进 <c>Judge</c> 之后，
    /// 两档定案**当场就不过了**（自由段只有 24 mm，铜排压根装不上）。
    /// 而此处的 <see cref="Binding"/> 仍写着「无 —— 每条判据都有裕度」、
    /// <see cref="RampH"/> 等实测值仍是旧形状下的数 —— 判据变了，**记录没跟着变**。
    ///
    /// 这正是 §1.8「安静失败」的形状：`--hotspot`、`--busbarplan`、说明书页、3DM、论文
    /// 全都从这里取几何，它们会**照常跑出漂亮的数**，而那是一个装不上的设计的数。
    /// ⇒ 与其把这两档删掉（那样历史与出处就断了），不如让它们**自己说出自己已失效**，
    ///   每一个引用点都能读到。重解出新档后把本字段清空。
    /// </summary>
    public string Invalid = "";

    /// <summary>
    /// 非空 = 本档来自 <c>finaldesigns/</c> 下的文件（值即文件名）；空 = 写死在本文件里的内置档。
    ///
    /// ⚠ 界面下拉与 --selfcheck 都要**标出来**：你得能分清手上这个是
    ///   「守内核的回归基准」还是「别人昨天存的方案」。分不清就会拿错的那个去出图。
    /// </summary>
    public string FromFile = "";

    /// <summary>
    /// 失效声明点名的判据**前缀**（对应 <c>ConstraintOut.Name</c> 的开头）。
    /// 自检门用它判「不过的地方是不是正好是声明的那一条」。
    ///
    /// ⚠ 必须是**独立字段**，不能拿 <see cref="Invalid"/> 的正文去做子串匹配 ——
    ///   正文里写着「热学五条（①②′②″③管J）仍全过」，一做子串匹配就**每条都命中**，
    ///   于是「声明之外的失败」永远为空，门看着在跑、其实什么都没判。
    ///   （这正是 §1.8 那一族：不报错、格式正常、结论错。差点又写了一个。）
    /// </summary>
    public string[] InvalidChecks = System.Array.Empty<string>();

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

    // ── 圆盘保温（舌片保温另见 TabInsulMm，两者是不同部位、不同量级）
    /// <summary>圆盘外包的纤维厚度 mm。原来写死在 BuildCase 里，2026-08-17 提为字段。</summary>
    public double FlangeInsulMm = 20.0;
    public bool FlangeInsulated = true;

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

    // ================================================================
    // ★★★★★ 2026-08-16 第二次修正：**旧板厚是优化器停早了一轮的结果**。
    //
    // 收敛判据原来看「相邻两轮走多远 δ」，那套推理只在**线性定常**迭代下成立。
    // 上了 Anderson 之后 δ→0 而 x 并不在不动点上（Anderson 的经典停滞模式）——
    // 两个解算器一个报 ③=6.18、一个报 ③=18.70，**都自称收敛**，才把它逼出来。
    // 改判**真残差 ‖G(x)−x‖** 之后，Anderson 与纯 Picard（tol 0.2／2000 轮）
    // 落到同一点，差 0.09 K。
    //
    // ⇒ 旧值 2.11/3.40/3.18/1.76 的真实 ③ 是 **18.7（限 10）**，根本不通过。
    //   控制律本身没错，它只是被喂了一个假的 ③ —— **再走一轮就到位**
    //   （0.8 档第 3→4 轮，0.6 档第 4→5 轮）。
    //
    // ⇒ 教训：判据的分辨率取决于**解收敛到什么程度**，
    //   而收敛度量本身也可能是代理量 —— 这是「代理量不是原量」的第五次发作。
    // ================================================================

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

    /// <summary>
    /// 圆盘的焊接屈曲下界 mm。<see cref="Plate"/> 会把板厚**静默顶到**它 ——
    /// 所以定尺寸器必须用**同一个**值当搜索下界，否则它以为自己在 0.30 mm 上搜，
    /// 实际几何一直是 0.44 mm：旋钮转了而模型没动，是「安静失败」的标准形状。
    /// ⇒ 下界只有一个来源，就是这里。
    /// </summary>
    /// <summary>
    /// ★ 板厚下界由**两条**工艺线取大，不是一条（2026-08-17 补齐）：
    ///   · 焊接**屈曲**下界：随盘径线性增长（盘越大越容易翘）；R30 时是 0.55 mm；
    ///   · 焊接**烧穿**下界：<c>WeldMinThicknessMm</c> = 0.6 mm，手工 TIG 的工艺硬底，与尺寸无关。
    ///
    /// 为什么补：原来只用屈曲那一条。在 R30 上它是 **0.55 < 0.6** ⇒ 真正咬住的那条
    /// **不在下界里**。以前没暴露是因为定尺寸器从来没把板削到 1 mm 以下；
    /// D8 的省铂漂移一上来就压到 0.75–0.87 mm，正好走进这个缺口。
    /// memory「焊接定的工艺下界」早已记明：**屈曲不构成限制，下界 = 焊接方法**。
    /// </summary>
    /// <summary>
    /// 圆盘的板厚工艺下界 = max(抗焊接屈曲, 焊接烧穿底)。
    ///
    /// ★★ 2026-08-28 更正：屈曲的**无支撑宽度 b** 原写成 <c>DiscRadiusMm - 26.0</c>，
    ///   那个 26.0 是字面量，而同一个几何量在本类第一处定义是
    ///   <see cref="HoleRadiusMm"/> = 管壁 + 25（0.6 档 25.6／0.8 档 25.8）。
    ///   26.0 的出处查得到：PlateCurrent2D 里 <c>HoleRadiusMm = 26.0  // Ø52（= 管外径）</c>
    ///   —— 那是**管壁 1.0 的旧构型**，两个现役档都不是它。
    ///   ⇒ 同一个数两处来源（破铁律②），而且 b 被少算 0.2–0.4 mm，
    ///     下界随之偏低约 5–10 %，**偏在危险侧**（允许更薄的板）。
    ///   ⚠ 对现役两档的**数值没有影响**：R30 上屈曲支算出 0.55→0.58，仍低于烧穿底 0.6，
    ///     max() 取的还是 0.6。只有大盘（屈曲主导）才会变，例如 R60 由 4.71 升到 4.74。
    /// </summary>
    public double DiscFloorMm(DesignInputs baseInputs) =>
        System.Math.Max(
            WeldDistortion.ForPt(1.0, kb: 0.43).SlopePerB
                * (DiscRadiusMm - HoleRadiusMm) * baseInputs.WeldSafetyFactor,
            baseInputs.WeldMinThicknessMm);

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
        // ★ 圆盘保温从**字段**取，不再写死 20（2026-08-17，做 1b 时补）。
        //   写死的后果：界面把「法兰保温 / 法兰保温厚」两个控件接过来之后，
        //   它们会被这一行**静默吃掉** —— 用户在界面上把保温改成 0，模型里仍然是 20 mm。
        //   那就是第七项「表达不了」，而且比前六项更隐蔽（控件在、能动、不起作用）。
        p.FlangeInsulThickMm = FlangeInsulated ? FlangeInsulMm : 0;
        p.FlangeInsulated = FlangeInsulated;
        p.BusbarClampLengthMm = ClampLengthMm;
        p.BusbarClampTempC = ClampTempC;

        double discFloor = DiscFloorMm(baseInputs);

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

    /// <summary>自由段 = 舌长 − 圆盘切点 − 压接段。判据 ⑤ 判的就是它。</summary>
    public double FreeTabMm =>
        TabLengthMm
        - System.Math.Sqrt(System.Math.Max(0, DiscRadiusMm * DiscRadiusMm
              - System.Math.Min(TabHalfWidthMm, DiscRadiusMm) * System.Math.Min(TabHalfWidthMm, DiscRadiusMm)))
        - ClampLengthMm;

    /// <summary>数组按给定格式串逐个格式化后用 / 连起来。</summary>
    public static string Fmt(double[] a, string f) =>
        string.Join("/", System.Linq.Enumerable.Select(a, v => v.ToString(f)));

    public string Describe() =>
        (Invalid.Length > 0 ? "★ **已失效** " : "") +
        $"[{Name}] 管壁 {WallMm:0.0}／管保温 {TubeInsulMm:0}／盘Ø{2 * DiscRadiusMm:0}／" +
        // ⚠ 必须逐个格式化。`string.Join("/", double[])` 打出来的是
        //   「1.3600000000000003/2.55656893078647」这种二进制残渣，而它会**直接进报告**——
        //   读的人无从分辨那是「算出来的精度」还是「忘了格式化」。（2026-08-17 实际发生。）
        $"舌 {TabLengthMm:0}×{2 * TabHalfWidthMm:0}／板厚 {Fmt(TabThickMm, "0.00")}／" +
        $"舌保温 {Fmt(TabInsulMm, "0.0")}／" +
        $"环 r≤孔+{RingWidthMm:0}→×{Fmt(RingMul, "0.00")}／舌根圆角 R{TabFilletMm:0}／" +
        $"压接 {ClampLengthMm:0} 夹 {ClampTempC:0} °C　合计 {TotalMassG:0} g";

    // ════════════════════════════════════════════════════════════════════
    // ★★★★★ 定案档（**全部重解**，2026-08-17 —— 这个日期是对的，
    //   由 deliverable/定案_管壁0.8mm.3dm 的内容实证，详见 W08 的 Provenance）
    //
    // 为什么重解：判据 ⑤（舌片自由段 ≥ 100 mm）加进来之后，原来两档当场不过 ——
    // 舌长 90 mm 的自由段只有 24 mm，铜排根本装不上。那不是余量不够，是**设计上不成立**。
    //
    // 重解的两处关键改动：
    //  ① **舌长不再是自由变量**，改成算出来的：舌长 = 圆盘切点 + 压接段 + 自由段下界。
    //     切点(半宽=盘径时为 0) + 40 + 100 = **140 mm**。更长只多花铂多发热。
    //  ② 定尺寸器换成 D8（`Core/Sizer.cs`）：**舌保温 → 抽热窗口**（免费旋钮）、
    //     环倍率 → ②″、板厚 → 接力+省铂。旧的 D7 把板厚拿去追 ②′，而 **③ 根本没有旋钮**。
    //
    // 形状前沿（`--shape --discs 30 --halfws 30,22,15 --wall 0.8`，舌长各自按装配下界算）：
    //     半宽 30（舌宽 60，舌长 140）→ **3512 g ✓**
    //     半宽 22（舌宽 44，舌长 160）→  4171 g ✓
    //     半宽 15（舌宽 30，舌长 166）→  **无解**（板厚顶到 6.00 上限、保温压到 0.3 下界，
    //                                     仍有 −52 W 往管里灌 —— 两个旋钮同时饱和）
    //     盘 R36（Ø72，舌宽 72）      →  4075 g ✓（盘大了反而更重）
    // ⇒ 盘 Ø60 + 舌 140×60 是本网格里最轻的可行形状。用户也确认过 Ø60 接近解析模式。
    //
    // ★ 一个顺带的结论：**管孔渐变环在新形状上不需要了**（倍率收敛到 1.00）。
    //   舌片宽了 2 倍，孔周电流不再拥塞，②″ 只有 −0.2 K（限 +5）。少一道两级台阶的加工。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>留余量档：没有任何判据贴限值。</summary>
    public static readonly FinalDesign W08 = new()
    {
        Name = "管壁 0.8 · 留余量",
        Provenance = "--shape + D8 定尺寸（Core/Sizer.cs）；60 轮，取最轻的**有裕度**的全过点。" +
                     "数值已按图纸精度量化（板厚 0.01／舌保温 0.1 mm）后复核，`--window` 逐条对上。" +
                     "★ 2026-08-25 把这条出处查清了（此前更正过一次，**那次说过头了**，一并纠正）：" +
                     "**日期是对的**。deliverable/定案_管壁0.8mm.3dm 生成于 2026-08-17 13:45，" +
                     "二进制里舌长 −140.0 出现 64 次、板厚 0.89/2.45/2.35/0.73 各 114 次，与本档逐位相同；" +
                     "作废档的 −90.0 与 2.11 一次都没有 ⇒ **本档的几何那天确实已经存在**。" +
                     "错的只是**工具归属**：`--shape`、`Core/Sizer.cs`、代号 D8 三样都是 2026-08-20 的" +
                     "提交 09c8d9b 才写的（那次提交标题是「输出框改用 Excel 式对齐」，正文没提定案被换掉）" +
                     "—— 它们是**事后补写来固化**当天已得到的结果，不是当天跑出这组数的那个东西。" +
                     "推算过程多半发生在落库前的对话里，代码是事后补写的。" +
                     "⚠ 故这句出处**不能拿来复现**，而且这一条已经**实测**：用今天的 --shape 在同一形状上" +
                     "从**板厚压平**的种子（--seedflat 2.0，其余旋钮仍是本档的）重跑，落在 **3664 g**，比本档重 117 g（HANDOVER §0.0.3 ⑦）。★ 这是**单变量**对照：只有板厚起点变了 ⇒ 那 117 g 只归因于板厚起点；**真正独立的复现**还须把舌保温与环倍率也脱开本档，尚未做。" +
                     "今天的 --shape 种子默认就是本档（见 Core/ShapeSeed.cs），用它重推等于从答案出发",

        Binding = "抽热窗口：②′ 最小 1.1 W（须 >0）、③ 5.2/10 —— 两者是同一个量的两侧。" +
                  "舌保温已接近下界（0.3–0.5 mm，≈裸舌）⇒ 再削板厚就要转为往管里灌热",
        WallMm = 0.8,
        DiscRadiusMm = 30.0, TabLengthMm = 140.0, TabHalfWidthMm = 30.0,
        TabThickMm = new[] { 0.89, 2.45, 2.35, 0.73 },
        TabInsulMm = new[] { 0.4, 0.4, 0.5, 0.3 },
        RingMul = new[] { 1.00, 1.00, 1.00, 1.00 },
        TotalMassG = 3547, TubeMassG = 2465, FlangeMassG = 1082, ResidualK = 0.5,
        RampH = 0.058, DiscOverK = -0.208, HoleFluxW = 1.123, FlangeDipK = 5.182, TubeJ = 9.506,
    };

    /// <summary>底档：管壁压到焊接烧穿下界。</summary>
    public static readonly FinalDesign W06 = new()
    {
        Name = "管壁 0.6 · 底档",
        Provenance = "--shape + D8 定尺寸（Core/Sizer.cs）；60 轮，取最轻的**有裕度**的全过点。" +
                     "数值已按图纸精度量化（板厚 0.01／舌保温 0.1 mm）后复核。" +
                     "★ 2026-08-25 查清：日期**是对的**（deliverable/定案_管壁0.6mm.3dm 里板厚 " +
                     "0.64/1.86/1.73/0.62 各 114 次，与本档逐位相同），错的是工具归属。同 W08 那条，不再重复。" +
                     "⚠ 同样**不能拿来复现**：今天的 --shape 种子默认就是本档，用它重推等于从答案出发",

        Binding = "管壁 0.6 = 焊接烧穿下界（余量 0）；端片板厚 0.62–0.64 也逼近同一条下界 0.60",
        WallMm = 0.6,
        DiscRadiusMm = 30.0, TabLengthMm = 140.0, TabHalfWidthMm = 30.0,
        TabThickMm = new[] { 0.64, 1.86, 1.73, 0.62 },
        TabInsulMm = new[] { 0.4, 0.4, 0.4, 0.6 },
        RingMul = new[] { 1.00, 1.00, 1.00, 1.00 },
        TotalMassG = 2656, TubeMassG = 1841, FlangeMassG = 815, ResidualK = 0.5,
        RampH = 0.073, DiscOverK = -0.284, HoleFluxW = 0.820, FlangeDipK = 6.478, TubeJ = 10.961,
    };

    // ── 已作废的两档：**留着**，不删。
    //   删掉就没人知道 3DM／论文／说明书里那些 3106 / 2388 g 是哪来的、为什么不能再用；
    //   而且自检门需要它们当**活样本**：⑤ 这条判据必须始终抓得住它们（见 --selfcheck A）。
    public static readonly FinalDesign Retired08 = new()
    {
        Name = "（已作废）管壁 0.8 · 舌长 90",
        Provenance = "--final2 可行性阶梯 D7，2026-08-16",
        Binding = "热学五条都有裕度；败在装配",
        Invalid = "★ **判据 ⑤ 不过**：舌长 90 ⇒ 自由段只有 24.0 mm（下界 100）。" +
                  "24 mm 里装不下现场铜排（长 100／宽 60–80）与压接块，**设计上不成立**。" +
                  " 热学五条（①②′②″③管J）仍全过，故本档的**热学结论仍可引用**，" +
                  "但几何与铂重已由 2026-08-17 的重解取代（3106 → 3547 g；那次重解的**工具归属**写错过，见 W08 的 Provenance）。",
        InvalidChecks = new[] { "⑤" },
        WallMm = 0.8, TabLengthMm = 90.0, TabHalfWidthMm = 15.0,
        TabThickMm = new[] { 2.11, 3.33, 3.12, 1.76 },
        TabInsulMm = new[] { 18.7, 1.6, 1.4, 3.9 },
        RingMul = new[] { 1.22, 1.22, 1.22, 1.22 },
        TotalMassG = 3106, TubeMassG = 2465, FlangeMassG = 641, ResidualK = 0.86,
        RampH = 0.057, DiscOverK = 1.050, HoleFluxW = 1.351, FlangeDipK = 5.522, TubeJ = 9.506,
    };

    public static readonly FinalDesign Retired06 = new()
    {
        Name = "（已作废）管壁 0.6 · 舌长 90",
        Provenance = "--final2 可行性阶梯 D7，2026-08-16",
        Binding = "焊接烧穿下界 0.6 mm ＋ 管 J 10.96/12 —— 两条同点咬住；败在装配",
        Invalid = "★ **判据 ⑤ 不过**：舌长 90 ⇒ 自由段只有 24.0 mm（下界 100）。同上，" +
                  "几何已由 2026-08-17 的重解取代（2388 → 2656 g；同上，见 W08 的 Provenance）。",
        InvalidChecks = new[] { "⑤" },
        WallMm = 0.6, TabLengthMm = 90.0, TabHalfWidthMm = 15.0,
        TabThickMm = new[] { 1.82, 2.85, 2.66, 1.49 },
        TabInsulMm = new[] { 18.7, 1.6, 1.4, 3.9 },
        RingMul = new[] { 1.20, 1.20, 1.20, 1.20 },
        TotalMassG = 2388, TubeMassG = 1841, FlangeMassG = 547, ResidualK = 0.37,
        RampH = 0.078, DiscOverK = 1.186, HoleFluxW = 1.583, FlangeDipK = 5.965, TubeJ = 10.961,
    };

    /// <summary>
    /// 现役两档在前，已作废两档在后 —— 顺序就是界面下拉的顺序，别调换。
    /// 作废档留在表里是有用的：自检门每次都要验「⑤ 仍然抓得住它们」。
    /// </summary>
    /// <summary>
    /// **内置**四档 —— 写死在代码里，走 PR 变更、被 diff 记录。
    /// 它们是守内核的回归基准：内核哪天算出别的数，--selfcheck A 段当场红。
    /// 测试只认这一组（文件档在磁盘上，会让测试结果依赖机器状态）。
    /// </summary>
    public static readonly FinalDesign[] Builtin = { W08, W06, Retired08, Retired06 };

    /// <summary>
    /// 内置档 + <c>finaldesigns/*.fd.json</c>。顺序：内置在前（下拉里先看到基准），文件档在后。
    ///
    /// ⚠ 读档失败**不静默** —— 见 <see cref="FinalDesignStore.LoadErrors"/>，
    ///   启动路径与 --selfcheck 都会把它当失败报出来。少一个档 = 少一组判据。
    /// </summary>
    public static FinalDesign[] All { get; private set; } = Scan();

    private static FinalDesign[] Scan()
        => Builtin.Concat(FinalDesignStore.LoadAll(Builtin.Select(x => x.Name))).ToArray();

    /// <summary>
    /// 重扫 <c>finaldesigns/</c> 并重建 <see cref="All"/>，然后广播 <see cref="Reloaded"/>。
    ///
    /// **另存新档之后必须调**：在此之前 <c>All</c> 只在启动时算一次，
    /// 于是刚存的档要重启 APP 才出现在下拉里 —— 而对着一个「已经存好了」的提示
    /// 却在下拉里找不到它，工程师最可能的反应是再存一次（撞重名被拒），
    /// 或者以为没存上。
    ///
    /// <see cref="FinalDesignStore.LoadAll"/> 每次进来先清 <c>LoadErrors</c>，
    /// 所以反复调不会把错误堆起来；但**新出现的读档错误会覆盖旧的**，
    /// 调用方要在调完之后再看 <c>LoadErrors</c>。
    /// </summary>
    public static void Reload()
    {
        All = Scan();
        // Current 可能是个已被删掉的文件档 —— 让它退回内置首档，别留一个不在 All 里的 Current
        if (System.Array.IndexOf(All, Current) < 0) Current = Builtin[0];
        Reloaded?.Invoke(null, System.EventArgs.Empty);
    }

    /// <summary><see cref="All"/> 变过了。界面上每个列出定案档的下拉都该挂上来。</summary>
    public static event System.EventHandler? Reloaded;

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
