using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **设计记录几何的唯一来源**（2026-08-15 建立，08-16 改为双档）。
///
/// 为什么要有这个文件：设计记录值此前是**各命令各手抄一份**。
/// `--final2` 往前推进之后，`--hotspot` 与 `--busbarplan` 还钉着几代之前的几何
/// （管壁 0.6、舌厚 {1.37,2.02,1.80,1.04}、管保温 10 mm、无管孔环），
/// 它们照样跑得出漂亮的数 —— 但那是**另一个设计**的数。
/// 这正是 HANDOVER §1.8「安静失败」家族的形状：不报错、格式正常、结论错。
///
/// ⇒ 与判据收敛到 <c>LineRunner.Judge</c> 同理：**几何也只能有一个来源**。
///   任何辅助命令要「对着设计记录构型量」，就从这里取，不许再抄。
///   `--final2` 是**唯一**有权更新这里的地方（它是定尺寸器）。
///
/// ★★ 2026-08-16 改为**双档**：<see cref="W08"/> 与 <see cref="W06"/> 都全判据通过，
///   差别只在裕度与铂重，取舍属于业主。原来「改三行切档」的做法本身就是
///   手抄的另一种形式 —— 两档并存、由 <see cref="Current"/> 指定，才是单一来源。
///
/// ⚠ 改这里之前先想清楚：下面每个数都是某一轮实测收敛的结果，
///   不是可以随手调的参数。改了就要重跑 `--final2` 复核全判据。
/// </summary>
public sealed class DesignSpec
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
    /// 两档设计记录**当场就不过了**（自由段只有 24 mm，铜排压根装不上）。
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
    /// <summary>
    /// ★ 设计电流密度 J（A/mm²）—— 用户 2026-09-09：「J 让工程师设定（实况风险工程师承担）；J 是设定值，J+1 是计算极限值；J 预设值为 10」。
    /// 按它定舌片厚 = I/(J·舌宽)、圆盘各截面的板厚下界（约束盒下角）、孔径/槽张角的 J 上界；终验全体截面 &lt; <see cref="JCheckAPerMm2"/>。
    /// 进设计记录（漏存就是另一个设计，样本门盯着）；进 <see cref="LineCase.JDesignAPerMm2"/>（判据限值的唯一来源）。
    /// </summary>
    public double JDesignAPerMm2 = SectionSizing.JDesignAPerMm2;
    /// <summary>计算极限值 = 设定 J + 1（用户 2026-09-09）。</summary>
    public double JCheckAPerMm2 => SectionSizing.JCheckOf(JDesignAPerMm2);
    public double[] SetpointC = { 1150.0, 1080.0, 1050.0 };

    /// <summary>
    /// ★★★ **每段直接加热铂金管的长度 mm**，逐段（用户 2026-09-03）。
    /// 长度不足段数时由 <c>LineRunner.Normalize</c> 按参数表铺满 —— 兜底只有那一处。
    /// </summary>
    public double[] SegLengthMm = { 300.0, 300.0, 300.0 };

    // ── 法兰（四片：入口 / 共用1 / 共用2 / 出口）
    public double DiscRadiusMm = 30.0;
    public double TabLengthMm = 90.0;
    public double TabHalfWidthMm = 15.0;
    /// <summary>舌根过渡圆角 R。⚠ 网格 2 mm，小于它的圆角在场里看不出来（§1.8 的分辨率坑）</summary>
    public double TabFilletMm = 3.0;
    public double[] TabThickMm = new double[4];
    public double[] TabInsulMm = { 18.7, 1.6, 1.4, 3.9 };

    /// <summary>
    /// ★★★★★ **舌片自己的厚度 mm**（逐片；R11，用户 2026-09-08：「舌片厚度不是旋钮，是截面积 I/10 ÷ 舌宽」）。
    /// 由 <see cref="SizeTongues"/> 闭式给：t_舌 = I_设计 /(J_设计 × 舌片最窄有效宽)，与圆盘基板 <see cref="TabThickMm"/>
    /// 及各级台阶**解耦**。NaN = 与基板同厚（四份内置记录早于这条规则，只报不判）。
    /// ⚠ 名字里的「Tab」历史上指整片板（<see cref="TabThickMm"/> 其实是基板厚），所以这里用 Tongue 区分。
    /// </summary>
    public double[] TongueThickMm = { double.NaN, double.NaN, double.NaN, double.NaN };

    /// <summary>
    /// ★ R29（2026-09-10）：舌根加厚带（叉臂）—— **派生量，不是旋钮**：舌片上有切口（舌孔／落到舌根的圆盘槽）时，
    /// 带 = [切口最远处 − 2 mm, 切口最近处 + 2 mm]（切口贴着切点就到切点），带厚 = I/(J·带内最窄有效宽)，
    /// 杆厚 <see cref="TongueThickMm"/> = I/(J·带外最窄有效宽)。都闭式，由 <see cref="SizeTongue"/> 每次动切口时重算。NaN = 没有带。
    /// </summary>
    public double[] TabArmX0Mm = { double.NaN, double.NaN, double.NaN, double.NaN };
    public double[] TabArmX1Mm = { double.NaN, double.NaN, double.NaN, double.NaN };
    public double[] TabArmThickMm = { double.NaN, double.NaN, double.NaN, double.NaN };

    // ── 管孔渐变环：**相对量**（绝对值写法已两次造成安静失败，见 §1.8 ⑥⑦）
    /// <summary>环宽 mm，相对管孔外扩；两级台阶在 孔+w 与 孔+2w</summary>
    public double RingWidthMm = 3.0;
    /// <summary>内圈厚度倍率（相对板厚）。外圈见 <see cref="RingMulOuter"/>。</summary>
    public double[] RingMul = new double[4];

    // ══ 把渐变环的**形状**放开成变量（2026-08-28，算法普查 A 类第 ⑨ 条）
    //
    // 病灶：一个两级台阶本来要 4 个数（r₁、r₂、t₁、t₂），而此前只有 t₁ 那一个
    // 是优化变量，另外三个全被写死：
    //   r₁ = 孔 + w，r₂ = 孔 + **2**w（「2 倍」写死），t₂ = t₁ 按 **0.4** 过渡（0.4 写死）。
    // ⇒ 解析路上称不上「厚度梯度优化」，只是「一个受单旋钮控制、形状写死的台阶」。
    // 对照 `.3dm` 路的 LevelScale[片][级]：**每片每级一个自由度**，那才是逐级优化。
    //
    // ⚠ 三个都做成**逐片**且 NaN = 退回历史关系：
    //   ① 默认逐位复现今天的几何（`RingShapeDefaultsTests` 钉住）；
    //   ② 历史关系变成**显式的兜底**而不是藏在式子里的常数；
    //   ③ 求解器要不要动它们，等 `--monotone` 量完单调性再定 —— **不假设方向**。

    /// <summary>内级外扩 mm，逐片。NaN = 用全局 <see cref="RingWidthMm"/>（历史行为）。</summary>
    public double[] RingW1Mm = { double.NaN, double.NaN, double.NaN, double.NaN };
    /// <summary>外级外扩 mm，逐片。NaN = 用 2×<see cref="RingWidthMm"/>（历史行为，「2 倍」原是写死的）。</summary>
    public double[] RingW2Mm = { double.NaN, double.NaN, double.NaN, double.NaN };
    /// <summary>外级厚度倍率，逐片。NaN = 用 1+0.4(μ−1)（历史行为，0.4 原是写死的）。</summary>
    public double[] RingMul2 = { double.NaN, double.NaN, double.NaN, double.NaN };

    /// <summary>
    /// 第 j 片两级台阶的**外半径** mm（自小到大）。
    /// ⚠ 必须严格递增：<see cref="PlateCurrent2D"/> 按 `r ≤ 各级半径` 依次命中，
    ///   顺序错了会**静默**取到错的那一级（几何照画，温度全错）。
    /// </summary>
    public double[] RingRadiiOf(int j)
    {
        double r1 = HoleRadiusMm + (double.IsNaN(RingW1Mm[j]) ? RingWidthMm : RingW1Mm[j]);
        double r2 = HoleRadiusMm + (double.IsNaN(RingW2Mm[j]) ? 2 * RingWidthMm : RingW2Mm[j]);
        if (!(r2 > r1))
            throw new InvalidOperationException(
                $"第 {j} 片的环台阶半径没有递增：r1={r1:0.###}、r2={r2:0.###} mm。" +
                "阶梯表按「r ≤ 各级半径」依次命中，不递增会静默取到错的那一级。");
        return new[] { r1, r2 };
    }

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
    ///   **设计记录当天（08-15）那次运行**的数 —— 收敛度量与 ②″ 限值都是在那之后才改的。
    ///   复核发现 ②′ 与 ③ 两项与实算对不上，**且两档之间的大小关系是反的**
    ///   （抄的说 0.8 档 ③ 更小，实算是 0.8 档 ③ 更大）。
    ///   判定结论没变（两档仍全过、铂重不变），但「哪一档在 ③ 上更宽裕」这句话说反了。
    ///   ⇒ 收敛到本处。要更新就点界面上的「▶ 复现设计记录」重跑一遍照抄。
    /// </summary>
    public double RampH, DiscOverK, HoleFluxW, FlangeDipK, TubeJ;

    /// <summary>
    /// ★★★ **网格无关复核之后的值**（2026-08-30 补）。<c>NaN</c> = 这一档还没复核过。
    ///
    /// ══ 为什么要单独立一组，而不是把上面那组改掉
    ///
    /// 上面那组是**导航网格（2 mm）**上的数 —— 它是**历史记录**，记的是当天那次运行。
    /// 而实测：同一个设计，加密到判据不再变之后，「法兰增量温降」会大出 3 K 以上：
    /// <code>
    ///   0.8 档   记录 4.720 K   复核 7.950 K（0.250 mm，14982 单元）
    ///   0.6 档   记录 6.124 K   复核 9.453 K（0.125 mm，47567 单元）
    /// </code>
    /// 限值是 10 ⇒ 记录说「余量 53 %」，复核说「余量 20 %」。**说明书照原样显示记录值，
    /// 而工程师无从知道那是粗网格上的数** —— 这正是「对话的知识没落到 APP 上」那一族。
    ///
    /// ⚠ **不覆盖**原记录：改记录是「落档」——那是有正式流程的动作，不该顺手做。
    ///   ⇒ 两组并存，界面**两个都显示并标明口径**。
    ///
    /// ⚠ 复现命令：<c>--cli --quiet --solve --verifymesh --wall &lt;壁厚&gt;</c>
    /// </summary>
    public double VerifiedMeshMm = double.NaN, VerifiedFlangeDipK = double.NaN,
                  VerifiedHoleFluxW = double.NaN, VerifiedDiscOverK = double.NaN;

    /// <summary>
    /// ★★★ **这组复核值是什么时候、用哪一版代码测的**（2026-09-02 补）。空串 = 与当前代码同版。
    ///
    /// 为什么必须有它：0.8 档那组数（7.950 K）是 2026-08-30 **换 CG 之前**跑的，
    /// 而 CG 那一改把停机判据从「步长」换成了「真残差」，解的位置微移过。
    /// 0.6 档已用新代码重跑（9.453，全过），0.8 档还没有。
    ///
    /// ⚠⚠ 而当天下午我把这组**旧代码的数**填进来、说明书当作「加密到位、可信」那一列显示，
    ///   口径一个字没标 —— **我刚立的规则，自己当天就犯了一次**。
    ///   源码注释救不了这件事：工程师看不到源码。⇒ 立成字段，说明书**必须**印出来。
    /// </summary>
    public string VerifiedNote = "";

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

    /// <summary>
    /// ★ R30（2026-09-10，用户要看大管径对比）：管内径进几何 —— 管孔半径 = 内径/2 + 管壁。此前写死 25.0（Ø50），
    /// 改内径只有求解器跟着走、几何／图纸／下界不动，BuildCase 干脆拒算。现在几何只有这一个来源：本设计的 <see cref="TubeIdMm"/>；
    /// 页面读控件成设计时从参数表「内径 ID」抄进来，载入记录时反过来写回参数表。
    /// </summary>
    public double TubeIdMm = 50.0;
    public double HoleRadiusMm => WallMm + 0.5 * TubeIdMm;
    /// <summary>
    /// 两级台阶外半径的**第 0 片视角** —— 只给出图与显示用。
    /// ⚠ 求解与建模一律走 <see cref="RingRadiiOf"/>（逐片），别用这个属性，
    ///   否则各片放开成不同形状之后这里会悄悄只画第 0 片的样子。
    /// </summary>
    public double[] RingRadiiMm => RingRadiiOf(0);

    /// <summary>
    /// 外圈厚度倍率。<see cref="RingMul2"/> 给了就用它；
    /// NaN 时退回历史关系「内圈的 40 % 过渡回板身」——
    /// **那个 0.4 是挑出来的数，不是算出来的**（算法普查 A⑨）。
    /// </summary>
    public double RingMulOuter(int j) =>
        double.IsNaN(RingMul2[j]) ? 1 + (RingMul[j] - 1) * 0.4 : RingMul2[j];

    /// <summary>
    /// 深拷贝 —— 供**参数扰动验证**用（`--vary`）：扰动必须作用在副本上，
    /// 否则 <see cref="W08"/>/<see cref="W06"/> 这两个 static 实例会被就地改掉，
    /// 后面每一次「设计记录」都变成上一次扰动的结果（典型的静默污染）。
    /// </summary>
    /// <summary>
    /// 法兰片数 = 段数 + 1（<c>LineSolver.FlangeCount</c> 的口径，别在别处再算一遍）。
    /// </summary>
    /// <summary>段数 —— 只有一个口径：控温点的个数。</summary>
    public int SegmentCount => SetpointC.Length;

    public int FlangeCount => SetpointC.Length + 1;

    /// <summary>
    /// ★★★★★ **把逐片数组调成当前段数该有的长度**（2026-09-02 用户：「UI 段数是必须可调整的」）。
    ///
    /// 病灶：核心一直是 n 段的（<c>SegmentCount => SetpointC.Length</c>、<c>FlangeCount = n+1</c>），
    /// 而**逐片数组一律写死四个**，存档也写死 <c>NeedA(..., 4)</c>，界面写死 <c>for i &lt; 4</c>。
    /// ⇒ 段表加一段（用户实测加到 HC4），核心要 5 片而这些只有 4 片 ——
    /// 要么当场崩，要么算的是**另一个零件**。
    ///
    /// ⚠ 补/删的位置**不是末尾**：这几个数组的第一个是**入口片**、最后一个是**出口片**，
    /// 中间才是共用片。加一段加的是**共用片**，所以从倒数第二个位置增删，
    /// 新的那片沿用上一片共用片的值。往末尾加会把「出口片」挤成共用片，
    /// 而出口片的厚度/保温与共用片差着一倍以上 —— 那是静默换零件。
    /// </summary>
    public DesignSpec Fit()
    {
        int n = FlangeCount;
        TabThickMm = FitArr(TabThickMm, n);
        TabInsulMm = FitArr(TabInsulMm, n);
        TongueThickMm = FitArr(TongueThickMm, n);
        TabArmX0Mm = FitArr(TabArmX0Mm, n); TabArmX1Mm = FitArr(TabArmX1Mm, n); TabArmThickMm = FitArr(TabArmThickMm, n);   // R29
        RingMul    = FitArr(RingMul,    n);
        RingW1Mm   = FitArr(RingW1Mm,   n);
        RingW2Mm   = FitArr(RingW2Mm,   n);
        RingMul2   = FitArr(RingMul2,   n);
        // ★ 槽张角也是**逐片**的 —— 漏了它，改段数之后 Plate(j) 会越界或读到别片的槽。
        //   （逐片数组少接一个的坑，2026-09-03 已经栽过一次：ringW1/W2/Mul2 那批。）
        SlotSpanDeg = FitArr(SlotSpanDeg, n);
        TabHoleRMm  = FitArr(TabHoleRMm,  n);
        TabHoleAspect = FitArr(TabHoleAspect, n);
        // ★ R12/R13（2026-09-09）：场定的位置与离散形状也是逐片数组，同一张清单
        TabHoleXMm = FitArr(TabHoleXMm, n);
        SlotCenterDeg = FitArr(SlotCenterDeg, n);
        TabHoleSides = FitArr(TabHoleSides, n);
        DiscCutShape = FitArr(DiscCutShape, n);
        DiscCutRotDeg = FitArr(DiscCutRotDeg, n);
        // ★ 逐段长度是**按段**的（n-1），不是按片 —— 别跟上面六个混在一起。
        //   ⚠ 新增的段插在**倒数第二**（同 FitArr 的理由：首段/末段有各自的边界），
        //     所以这里也走同一个函数，只是长度不同。
        SegLengthMm = FitArr(SegLengthMm, SegmentCount);
        return this;
    }

    /// <summary>在**倒数第二个**位置增删，保住「首=入口、末=出口」。</summary>
    private static double[] FitArr(double[] a, int n)
    {
        a ??= System.Array.Empty<double>();
        if (a.Length == n) return a;
        if (a.Length < 2) return System.Linq.Enumerable.Repeat(
            a.Length == 1 ? a[0] : 0.0, n).ToArray();
        var list = new System.Collections.Generic.List<double>(a);
        while (list.Count < n) list.Insert(list.Count - 1, list[list.Count - 2]);
        while (list.Count > n && list.Count > 2) list.RemoveAt(list.Count - 2);
        return list.ToArray();
    }

    public DesignSpec Clone()
    {
        var c = (DesignSpec)MemberwiseClone();
        c.SetpointC = (double[])SetpointC.Clone();
        c.SegLengthMm = (double[])SegLengthMm.Clone();
        c.TabThickMm = (double[])TabThickMm.Clone();
        c.TabInsulMm = (double[])TabInsulMm.Clone();
        c.TongueThickMm = (double[])TongueThickMm.Clone();
        c.TabArmX0Mm = (double[])TabArmX0Mm.Clone(); c.TabArmX1Mm = (double[])TabArmX1Mm.Clone(); c.TabArmThickMm = (double[])TabArmThickMm.Clone();   // R29
        c.RingMul = (double[])RingMul.Clone();
        c.RingW1Mm = (double[])RingW1Mm.Clone();
        c.RingW2Mm = (double[])RingW2Mm.Clone();
        c.RingMul2 = (double[])RingMul2.Clone();
        // ★★★★★ **形状那三根也必须逐份拷**（2026-09-06 被出图对账门抓到）。
        //   漏了它们 ⇒ MemberwiseClone 按引用共享 ⇒ 改一个副本的孔径，
        //   会**就地改掉 DesignSpec.Builtin[0] 那个 static 实例**，
        //   之后每一次 Clone 都带着污染。
        //   实测现场：一条测试把孔设成 R32.71×3，下一条测试拿「干净的 Builtin[0]」
        //   出图，出来的图**法兰实心穿过铂金管** —— 而它自己一个孔都没设过。
        //   ⚠ 本仓库为这个形状栽过（见 Clone 上方 --vary 那段注释：
        //     「扰动必须作用在副本上，否则 W08/W06 这两个 static 实例会被就地改掉」）。
        //     同一个坑，新字段又踩一次 ⇒ 配反射门 CloneCopiesEveryArrayTests，
        //     以后**任何**新加的数组字段漏拷都会当场红。
        c.SlotSpanDeg = (double[])SlotSpanDeg.Clone();
        c.TabHoleRMm = (double[])TabHoleRMm.Clone();
        c.TabHoleAspect = (double[])TabHoleAspect.Clone();
        // ★ R12/R13（2026-09-09）：五个新逐片数组，反射门 CloneCopiesEveryArrayTests 会盯着
        c.TabHoleXMm = (double[])TabHoleXMm.Clone();
        c.SlotCenterDeg = (double[])SlotCenterDeg.Clone();
        c.TabHoleSides = (double[])TabHoleSides.Clone();
        c.DiscCutShape = (double[])DiscCutShape.Clone();
        c.DiscCutRotDeg = (double[])DiscCutRotDeg.Clone();
        // ★ InvalidChecks 也是栏位（2026-09-07 督导 ② 抓到）。
        //   目前**无害** —— 全仓没有一处就地改它的元素（只有宣告 + 两处整体赋值，
        //   换引用不伤原件），督导也没实测到污染。补上不是因为它现在坏了，
        //   而是「哪天有人写一句 InvalidChecks[0] = …」这件事不该由运气决定。
        c.InvalidChecks = (string[])InvalidChecks.Clone();
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
            WeldDistortion.ForPt(1.0, kb: WeldDistortion.PlateBucklingKFreeEdge).SlopePerB
                * (DiscRadiusMm - HoleRadiusMm) * baseInputs.WeldSafetyFactor,
            baseInputs.WeldMinThicknessMm);

    /// <summary>按本设计记录构型造第 j 片（0=入口, 1=共用1, 2=共用2, 3=出口）。</summary>
    public FlangePlate Plate(int j, double discFloorMm)
    {
        double td = System.Math.Max(TabThickMm[j], discFloorMm);
        return new FlangePlate
        {
            DiscRadiusMm = DiscRadiusMm, HoleRadiusMm = HoleRadiusMm,
            TabEndXMm = -TabLengthMm, TabEndHalfWidthMm = TabHalfWidthMm,
            ThicknessMm = td,
            DiscStepRadiiMm = RingRadiiOf(j),
            DiscStepThicknessMm = new[] { td * RingMul[j], td * RingMulOuter(j) },
            TabThicknessMm = j < TongueThickMm.Length ? TongueThickMm[j] : double.NaN,   // R11：舌片自己的厚度（NaN = 与基板同）
            InsulBoundaryXMm = double.NaN, TabInsulThickMm = TabInsulMm[j],
            TabParallel = true, TabFilletMm = TabFilletMm,
            WeldFilletLegMm = System.Math.Max(td, WallMm),
            DiscSlots = SlotsOf(j, System.Math.Max(td, WallMm)),
            TabHoles = HolesOf(j),
            TabArmX0Mm = j < TabArmX0Mm.Length ? TabArmX0Mm[j] : double.NaN,          // R29：舌根加厚带（派生）
            TabArmX1Mm = j < TabArmX1Mm.Length ? TabArmX1Mm[j] : double.NaN,
            TabArmThicknessMm = j < TabArmThickMm.Length ? TabArmThickMm[j] : double.NaN,
            DiscCutHoles = DiscCutsOf(j, System.Math.Max(td, WallMm)),   // R13：圆盘上的长椭圆（形状族 1）
        };
    }

    /// <summary>
    /// ★★★ **圆盘背侧减重槽**：张角 0 = 不开槽（开箱默认，行为与从前逐位相同）。
    ///
    /// 位置不是拍的，是**场算出来的**（2026-09-05，deliverable/移除优先级.txt）：
    /// 移除优先级 = 导热贡献 ÷ 电流密度，最高的单元全部落在管孔外缘、**背对舌片**那侧
    /// （舌片在 −x，所以槽心取 +x 方向 = 0°）。电流从舌片进来绕过管孔基本不走那半圈，
    /// 而热照样从那里抽走 ⇒ 「只抽热、不导电」的死重。
    ///
    /// 实测效果（deliverable/按场开槽_效果.txt）：27–40 mm / 180° ⇒
    /// **抽热 −42.2 %**，峰值电流密度只 +2.9 %，体积 −5.9 %。
    /// 同样的料挖在舌片上只换到抽热 −0.5 % —— **每 1 % 体积的收益差 60 倍**。
    /// </summary>
    public double[] SlotSpanDeg = new double[4];

    /// <summary>
    /// ★★★ **舌板开孔的孔径**（逐片，半径 mm；0 = 无孔）。2026-09-05 用户要求：
    /// 「3DM 输入要能实现舌板开孔，**尺寸、孔径、厚度也都要能优化**」。
    ///
    /// ⚠ 我一度把它降级成「算得对但不当旋钮」，理由是实测**不划算**
    /// （挖同样的料，舌片只换到抽热 −0.5 %，圆盘 −37 %）。那是把
    /// **「不划算」当成了「不需要」—— 两件事**：
    ///   · 不划算 = 拿它当**省铂手段**时，圆盘槽好 60 倍
    ///   · 但工程师图上**本来就有孔**（装配、工艺、走线要求）⇒
    ///     APP 必须回答的是「**这个孔该多大**」，而不是「要不要开孔」
    /// ⇒ 孔径是旋钮：下界 = 用途要求的最小孔，上界 = 桥宽闭式反解。
    /// </summary>
    public double[] TabHoleRMm = new double[4];

    /// <summary>
    /// ★★★ **孔的长短轴比**（逐片；1 = 圆，>1 = 顺着电流拉长的椭圆）。2026-09-05。
    ///
    /// 用户：「优化孔的形状不只是孔径，例如可以是类圆角三角形」「或者是椭圆形」。
    /// 等面积实测（deliverable/孔形对比.txt，孔心 x=-50）：
    /// <code>
    ///   椭圆 2:1 横挡   抽热 -3.00 %   峰值 J **+28.9 %**   ← 挖得多但电流挤爆
    ///   圆 R8           抽热 -2.22 %   峰值 J  -0.4 %
    ///   椭圆 2:1 顺流   抽热 -2.03 %   峰值 J  **-7.6 %**
    ///   椭圆 3:1 顺流   抽热 -1.76 %   峰值 J **-10.6 %**  ← 挖了料，峰值反而更低
    /// </code>
    /// ⇒ **顺流拉长能一边挖料一边降峰值电流密度** —— 同面积下峰值 J 的跨度达 40 个百分点，
    ///   比孔径本身的影响大得多。所以它必须是**旋钮**，不是写死的默认。
    ///
    /// ⚠ 转角固定 0°（顺流）：横挡方向实测把峰值 J 顶高 29 %，那是纯粹的害处，
    ///   不该让求解器有机会选它。离散的形状（圆角三角/方）已量过、暂不入旋钮，
    ///   理由记在 HANDOVER 要求登记表 R6 备注里 —— **撤不撤由用户定**。
    /// </summary>
    public double[] TabHoleAspect = System.Linq.Enumerable.Repeat(1.0, 4).ToArray();

    /// <summary>
    /// ★★★ **舌孔孔心沿舌轴的位置 mm**（逐片；负向为舌端）。
    ///
    /// ★ NaN = **默认规则**：切点（舌片与圆盘相切处）与压接段边界这两点的**中点**
    /// （<see cref="TabHoleCenterXMm()"/>：0.5·(切点 x ＋ 压接段边界 x)）；<see cref="TabHoleCenterXMm(int)"/>
    /// 就是这条规则的逐片实现——非 NaN 就直接用那个值，NaN 才回落默认规则。
    /// 非 NaN = 由外部**给定并冻结**的一个值（来自加载的设计记录，或 <c>LineDesignPage</c>「逐片自定」勾上时的存量），
    /// 直接采用、不再回落默认规则。
    ///
    /// ⚠ 审查欠账（低，2026-09-09）：这里原来的说明写「R12：孔心位置由场逐案定，求解器每轮把
    /// 优先级最高处写进这里」——**与代码不符**。R12 场定确实对**槽心角**／**长椭圆当地电流方向**生效
    /// （<see cref="Solver.FieldPlacement"/> 里真的写回 <see cref="SlotCenterDeg"/>／<see cref="DiscCutRotDeg"/>，
    /// 且槽一开口就冻结），但对**孔心**只算出场给的位置**打印对照，没有写回这个数组**——
    /// 2026-09-09 实测发现「移除优先级最高处」（x=−14.5）与逐点实测最优（x=−50）不是一回事
    /// （deliverable/形状族_对比.txt 末段），前提不站得住就不接上；接上之前 R12 场定对孔心不生效，
    /// 求解过程中孔心恒等于默认规则（<see cref="Solver.Solve"/> 每轮开头也确实把这个数组清回 NaN，
    /// 与「解与初值无关」同一条铁律）。2026-09-09 之前它是整线一个数（不逐片）；旧档里的标量
    /// <c>tabHoleXMm</c> 读进来铺到每一片。
    /// </summary>
    public double[] TabHoleXMm = { double.NaN, double.NaN, double.NaN, double.NaN };

    /// <summary>
    /// ★★★ **圆盘槽的槽心角 °**（逐片；+x 轴为 0°、逆时针为正，与 <see cref="FlangePlate.DiscSlot"/> 同口径）。
    /// NaN = 默认规则 0°（背对舌片，与 2026-09-09 之前写死的值逐位相同）。
    ///
    /// R12：不再写死 0°。求解器每轮开头在槽带内取移除优先级最高的角向写进这里 ——
    /// 单舌片自然落在 0° 一带；双舌片对称进电（<see cref="FlangePlate.TwoTabs"/>）时落到 ±90° 一带
    /// （<c>FieldPlacementTests</c> 两条钉住）。
    /// </summary>
    public double[] SlotCenterDeg = { double.NaN, double.NaN, double.NaN, double.NaN };

    /// <summary>
    /// ★★★ **舌孔的形状族**（逐片；R13，用户 2026-09-08：圆角三角／圆角方）：
    /// 0 = 圆／椭圆（拉长比由 <see cref="TabHoleAspect"/> 给，现状），3 = 圆角三角，4 = 圆角方。
    /// 是**离散选择**不是旋钮：同一挖料面积下（同一个孔径旋钮值 R ⇒ 面积 π·R²·拉长比，多边形按
    /// <see cref="FlangePlate.TabHole.EqualAreaRadius"/> 缩放外接半径）逐个探针量 Δ裕度/Δ铂重，由求解器的比价挑。
    /// 圆角比例与转角是常数（<see cref="TabHoleCornerFracOf"/>／<see cref="TabHoleRotDegOf"/>，出处 deliverable/孔形对比.txt）。
    /// 用 double 存是为了让「逐片数组」的固定清单（Fit／Clone／存档／样本门）对它一视同仁。
    /// </summary>
    public double[] TabHoleSides = new double[4];

    /// <summary>
    /// ★★★ **圆盘挖料的形状族**（逐片；R13，用户原话「长椭圆与弯椭圆仅用于法兰」= 圆盘区）：
    /// 0 = 弯椭圆槽（沿圆周的胶囊形，现状），1 = 长椭圆·**切向**（直的椭圆孔放在圆盘上，长轴垂直于半径），
    /// 2 = 长椭圆·**顺当地电流**（长轴 = 场算出的当地电流方向 <see cref="DiscCutRotDeg"/>）。
    /// 大小仍由同一根旋钮 <see cref="SlotSpanDeg"/> 给：长椭圆取与同张角弯椭圆槽**等面积**（<see cref="DiscEllipseOf"/>），
    /// 位置 = 槽带中径 × 槽心角（<see cref="SlotCenterDeg"/>，场定）。
    ///
    /// ⚠ 为什么切向与顺电流是**两个成员**而不是一条阈值（2026-09-09 实测，deliverable/形状族_对比.txt）：
    ///   Pt_Heater1 构型、等面积 1045 mm²：弯椭圆槽 抽热 −30.6 %、长椭圆·切向 −22.6 %、长椭圆·顺当地电流 **−4.7 %**。
    ///   背侧电流几乎不走（|∇V| 只有全片最大的 1.8 %），「顺电流」在那里等于顺着半径 —— 正好顺着热流，挡不住热。
    ///   哪个对，让求解器在同一挖料面积上各探一针、用同一套比价定；不由我拍一个「电流小于多少算没有」的阈值。
    /// </summary>
    public double[] DiscCutShape = new double[4];

    /// <summary>
    /// 当地电流方向 °（逐片；+x 轴 0°、逆时针正），求解器每轮从最新收敛的场在槽心处取 −∇V 写进来；
    /// 梯度退化时 NaN。只有 <see cref="DiscCutShape"/> = 2（长椭圆·顺当地电流）用它当长轴方向；切向那一族不看它。
    /// </summary>
    public double[] DiscCutRotDeg = { double.NaN, double.NaN, double.NaN, double.NaN };

    /// <summary>
    /// ★★★ **「能造」清单：孔径 &lt; 1 mm 的孔不考虑**（R15，用户 2026-09-08：「孔径小于 1 mm 可以忽视」）。
    /// 孔径旋钮的取值只能是 0（无孔）或 ≥ 这个数；(0, 1) 之间的值在几何、出图、求解器里一律按无孔处理
    /// （<see cref="TabHoleREffective"/>），二分与量化不许落进这段（<c>Solver.NextBisectPoint</c>）。
    /// </summary>
    public const double TabHoleRMinMm = 1.0;

    /// <summary>第 j 片**生效的**孔径：≥ <see cref="TabHoleRMinMm"/> 才算孔，否则 0（R15）。几何、出图、求解器读孔径都走这里。</summary>
    public double TabHoleREffective(int j)
    {
        double r = j < TabHoleRMm.Length ? TabHoleRMm[j] : 0;
        return r >= TabHoleRMinMm - 1e-9 ? r : 0;
    }

    /// <summary>第 j 片的舌孔形状族成员（0／3／4）。数组外或非法值一律按 0（圆）。</summary>
    public int TabHoleSidesOf(int j)
    {
        double s = j < TabHoleSides.Length ? TabHoleSides[j] : 0;
        return double.IsNaN(s) ? 0 : (int)System.Math.Round(s) is 3 or 4 ? (int)System.Math.Round(s) : 0;
    }

    /// <summary>圆角占外接半径的比例：三角 0.35、方 0.30 —— deliverable/孔形对比.txt（2026-09-05）量的就是这两个。</summary>
    public static double TabHoleCornerFracOf(int sides) => sides == 3 ? 0.35 : sides == 4 ? 0.30 : 1.0;

    /// <summary>
    /// 转角（相对电流方向）：三角 0°（三个朝向实测一样：峰值 J +8.1～8.2 %）、方 45°（边迎流：+4.8 %，角迎流 +16.9 %）。
    /// 出处 deliverable/孔形对比.txt。
    /// </summary>
    public static double TabHoleRotDegOf(int sides) => sides == 4 ? 45.0 : 0.0;

    /// <summary>第 j 片的圆盘挖料形状（0 = 弯椭圆槽，1 = 长椭圆·切向，2 = 长椭圆·顺当地电流）。数组外或非法值按 0。</summary>
    public int DiscCutShapeOf(int j)
    {
        double s = j < DiscCutShape.Length ? DiscCutShape[j] : 0;
        if (double.IsNaN(s)) return 0;
        int k = (int)System.Math.Round(s);
        return k is 1 or 2 ? k : 0;
    }

    /// <summary>第 j 片的槽心角 °：给了用给的，NaN 走默认规则 0°（背对舌片）。</summary>
    public double SlotCenterDegOf(int j)
    {
        double c = j < SlotCenterDeg.Length ? SlotCenterDeg[j] : double.NaN;
        return double.IsNaN(c) ? 0.0 : c;
    }

    /// <summary>
    /// ★★★★★ **孔径的闭式上界** —— 开过头孔缘会咬到舌边。
    /// 与「圆盘盖得住管孔＋焊脚」「槽张角」同一个套路：闭式反解，不用试。
    /// 桥宽 = 舌半宽 − 孔的**外接半径** ≥ minBridgeMm。
    ///
    /// ★ <paramref name="sides"/>（审查欠账·低，2026-09-09）：传该片的孔形状族（<see cref="TabHoleSidesOf"/>）。
    ///   此前恒按圆算，而圆角三角／方（sides=3/4）与「孔径旋钮值 R」等面积的外接半径比 R **大 13–22 %**
    ///   （<see cref="FlangePlate.TabHole.EqualAreaRadius"/>）——真正顶到舌边的是那个外接半径，不是 R 本身，
    ///   按圆算会把三角/方孔的桥宽上界算宽。这里复用等面积换算把上界折回到「R」的口径，不另写几何。
    /// </summary>
    public double TabHoleRMaxMm(double minBridgeMm = 4.0, int sides = 0)
    {
        double bridgeCapMm = System.Math.Max(0, TabHalfWidthMm - minBridgeMm);   // 对圆孔（外接半径 = R）的上界
        double ratio = FlangePlate.TabHole.EqualAreaRadius(1.0, sides, TabHoleCornerFracOf(sides));
        return ratio > 1e-9 ? bridgeCapMm / ratio : bridgeCapMm;
    }

    /// <summary>
    /// ★★★★★ 孔心位置：默认落在**舌片自由段**的中点。
    ///
    /// ⚠ 第一版写 `-舌长/2`，在 舌长 120／盘半径 60 上正好压到**圆盘**上 ——
    ///   而孔只从舌片实体上切，圆盘那块料还在 ⇒ 出图上看起来「孔被削掉一半」。
    ///   实测：z 向孔宽 17 mm（对的，2r），x 向只有 ~11 mm。
    ///   这不是测试问题，是**默认孔位压在圆盘上**。
    ///
    /// ⇒ 自由段 = 从盘缘切点到舌端，再去掉压接段（那里要夹铜排，不能开孔）。
    ///   取它的中点。
    /// </summary>
    /// <summary>
    /// 第 j 片的孔心 x：场定了（<see cref="TabHoleXMm"/> 非 NaN）就用它，否则默认规则（自由段中点）。
    /// 求解、截面、出图一律走这个逐片版本。
    /// </summary>
    public double TabHoleCenterXMm(int j)
    {
        double x = j < TabHoleXMm.Length ? TabHoleXMm[j] : double.NaN;
        return double.IsNaN(x) ? TabHoleCenterXMm() : x;
    }

    /// <summary>
    /// **默认规则**的孔心（自由段中点，不看逐片场定值）。只给显示与旧调用用 ——
    /// 求解与出图要走 <see cref="TabHoleCenterXMm(int)"/>（逐片），否则场定的位置会被这条默认规则悄悄盖掉。
    /// </summary>
    public double TabHoleCenterXMm()
    {
        // 盘缘切点（等宽舌）：|x| = √(R² − 半宽²)；舌片在 −x 侧
        double hw = System.Math.Min(TabHalfWidthMm, DiscRadiusMm);
        double xTan = -System.Math.Sqrt(System.Math.Max(0, DiscRadiusMm * DiscRadiusMm - hw * hw));
        double xClamp = -TabLengthMm + ClampLengthMm;      // 压接段占住舌端那一截
        return 0.5 * (xTan + xClamp);
    }

    /// <summary>槽的内外半径 mm。默认取「管孔外缘 + 一点」到盘径的 2/3 —— 与实测最优带一致。</summary>
    public double SlotRInMm = double.NaN, SlotROutMm = double.NaN;

    /// <summary>
    /// 第 j 片的舌孔。R15：孔径 &lt; 1 mm 按无孔（<see cref="TabHoleREffective"/>）。
    /// R13：形状族按 <see cref="TabHoleSides"/>，多边形外接半径按等面积缩放 —— 同一个孔径旋钮值挖同样多的料。
    /// R12：孔心逐片（<see cref="TabHoleCenterXMm(int)"/>）。
    /// </summary>
    public FlangePlate.TabHole[] HolesOf(int j)
    {
        double r = TabHoleREffective(j);
        if (!(r > 0)) return System.Array.Empty<FlangePlate.TabHole>();
        double asp = j < TabHoleAspect.Length && TabHoleAspect[j] > 0 ? TabHoleAspect[j] : 1.0;
        int sides = TabHoleSidesOf(j);
        double corner = TabHoleCornerFracOf(sides);
        double rr = FlangePlate.TabHole.EqualAreaRadius(r, sides, corner);
        return new[] { new FlangePlate.TabHole(TabHoleCenterXMm(j), 0, rr,
                                               Sides: sides, CornerFrac: corner,
                                               RotDeg: TabHoleRotDegOf(sides), AspectXZ: asp) };
    }

    /// <summary>第 j 片的弯椭圆槽（圆盘形状族 0）。槽心角走 <see cref="SlotCenterDegOf"/>（场定，NaN = 0°）。形状族 1 时这里为空，槽由 <see cref="DiscCutsOf"/> 给。</summary>
    public FlangePlate.DiscSlot[] SlotsOf(int j, double weldLegMm)
    {
        double span = j < SlotSpanDeg.Length ? SlotSpanDeg[j] : 0;
        if (!(span > 0.5)) return System.Array.Empty<FlangePlate.DiscSlot>();
        if (DiscCutShapeOf(j) != 0) return System.Array.Empty<FlangePlate.DiscSlot>();
        var (rin, rout) = SlotBandMm(weldLegMm);
        if (!(rout > rin)) return System.Array.Empty<FlangePlate.DiscSlot>();
        return new[] { new FlangePlate.DiscSlot(rin, rout, CenterDeg: SlotCenterDegOf(j), SpanDeg: span) };
    }

    /// <summary>第 j 片圆盘上的直孔（圆盘形状族 1 = 长椭圆）。形状族 0 时为空。</summary>
    public FlangePlate.TabHole[] DiscCutsOf(int j, double weldLegMm)
    {
        double span = j < SlotSpanDeg.Length ? SlotSpanDeg[j] : 0;
        if (!(span > 0.5) || DiscCutShapeOf(j) == 0) return System.Array.Empty<FlangePlate.TabHole>();
        var e = DiscEllipseOf(j, weldLegMm, span);
        return e is { } h ? new[] { h } : System.Array.Empty<FlangePlate.TabHole>();
    }

    /// <summary>弯椭圆槽（胶囊形）的面积 mm²：2·hw·rm·span + π·hw²。长椭圆按它取等面积。</summary>
    public static double SlotAreaMm2(double rin, double rout, double spanDeg)
    {
        double rm = 0.5 * (rin + rout), hw = 0.5 * (rout - rin);
        if (!(hw > 0) || !(spanDeg > 0)) return 0;
        return 2 * hw * rm * spanDeg * System.Math.PI / 180.0 + System.Math.PI * hw * hw;
    }

    /// <summary>
    /// ★★★ **长椭圆**（R13）：与同张角的弯椭圆槽**等面积**的直椭圆，放在槽带中径 × 槽心角处，
    /// 短半轴 = 槽带半宽（径向），长半轴 = 面积/(π·短半轴)，长轴方向：形状 2 取 <see cref="DiscCutRotDeg"/>（NaN 时退回切向），其余切向。
    /// 带子退化（rout ≤ rin）⇒ null。不判装不装得下 —— 那由 <see cref="DiscEllipseFits"/>／<see cref="DiscEllipseSpanMaxDeg"/> 管。
    /// </summary>
    public FlangePlate.TabHole? DiscEllipseOf(int j, double weldLegMm, double spanDeg)
    {
        var (rin, rout) = SlotBandMm(weldLegMm);
        if (!(rout > rin) || !(spanDeg > 0)) return null;
        double rm = 0.5 * (rin + rout), hw = 0.5 * (rout - rin);
        double area = SlotAreaMm2(rin, rout, spanDeg);
        double a = area / (System.Math.PI * hw);                       // 长半轴
        double th = SlotCenterDegOf(j) * System.Math.PI / 180.0;
        double rot = DiscCutShapeOf(j) == 2 && j < DiscCutRotDeg.Length ? DiscCutRotDeg[j] : double.NaN;
        if (double.IsNaN(rot)) rot = SlotCenterDegOf(j) + 90.0;         // 切向（形状 1；形状 2 方向退化时也退到这里）
        return new FlangePlate.TabHole(rm * System.Math.Cos(th), rm * System.Math.Sin(th), hw,
                                       Sides: 0, CornerFrac: 1.0, RotDeg: rot, AspectXZ: a / hw);
    }

    /// <summary>
    /// 长椭圆装不装得下槽带：沿椭圆边界采样，每一点的半径都要落在 [rin, rout] 内（与弯椭圆槽同一条带、同样的内外桥）。
    /// 闭式几何，不解场。
    /// </summary>
    public bool DiscEllipseFits(int j, double weldLegMm, double spanDeg)
    {
        var (rin, rout) = SlotBandMm(weldLegMm);
        var e = DiscEllipseOf(j, weldLegMm, spanDeg);
        if (e is not { } h) return false;
        double a = h.RMm * h.AspectXZ, b = h.RMm, rot = h.RotDeg * System.Math.PI / 180.0;
        const int N = 180;
        for (int i = 0; i < N; i++)
        {
            double t = 2 * System.Math.PI * i / N;
            double lx = a * System.Math.Cos(t), lz = b * System.Math.Sin(t);
            double x = h.XMm + lx * System.Math.Cos(rot) - lz * System.Math.Sin(rot);
            double z = h.ZMm + lx * System.Math.Sin(rot) + lz * System.Math.Cos(rot);
            double r = System.Math.Sqrt(x * x + z * z);
            if (r < rin - 1e-9 || r > rout + 1e-9) return false;
        }
        return true;
    }

    /// <summary>
    /// 长椭圆的张角上界（按「装得下槽带」）：面积随张角单调增 ⇒ 二分找最大还装得下的张角，向下落到 1° 格。
    /// 与 <see cref="SlotSpanMaxDeg"/>（弯椭圆槽的桥宽上界）是同一类量：闭式反解，不用试。0 = 连最小的也装不下。
    /// </summary>
    public double DiscEllipseSpanMaxDeg(int j, double weldLegMm)
    {
        double hiCap = SlotSpanMaxDeg(weldLegMm);
        if (!(hiCap > 0)) return 0;
        if (!DiscEllipseFits(j, weldLegMm, 1.0)) return 0;
        double lo = 1.0, hi = hiCap;
        if (DiscEllipseFits(j, weldLegMm, hi)) return System.Math.Floor(hi);
        for (int it = 0; it < 30 && hi - lo > 0.5; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (DiscEllipseFits(j, weldLegMm, mid)) lo = mid; else hi = mid;
        }
        return System.Math.Floor(lo);
    }

    /// <summary>槽带 [r内, r外]。NaN 时按默认规则给 —— 规则只有这一份。</summary>
    public (double RIn, double ROut) SlotBandMm(double weldLegMm)
    {
        // ★★★★★ 内边距不能是写死的 1 mm（2026-09-05 出图时抓到）。
        //   焊脚 = max(板厚, 管壁)，板厚解到 4.71 mm 时 rin = 26+4.71+1 = 31.71，
        //   内桥只剩 1.0 mm < 下限 ⇒ SlotSpanMaxDeg 返回 0 ⇒ **槽这根旋钮被自己的默认带卡死**。
        //   而它是治「法兰增量温降」最有效的一根（实测抽热 −42 %）。
        //   表现极隐蔽：求解器照常报「不可行」，只字不提「槽根本没得开」。
        //   ⇒ 内边距按**桥宽下限**给，与 SlotSpanMaxDeg 用的是同一个数。
        const double MinBridgeMm = 6.0;
        double rin = double.IsNaN(SlotRInMm)
                   ? HoleRadiusMm + weldLegMm + MinBridgeMm
                   : SlotRInMm;
        // 外边也要留够桥：盘缘往里退一个桥宽
        double rout = double.IsNaN(SlotROutMm)
                    ? System.Math.Max(rin + 2.0, DiscRadiusMm - MinBridgeMm)
                    : SlotROutMm;
        return (rin, rout);
    }

    /// <summary>
    /// ★★★★★ **张角的闭式上界** —— 开过头会把圆盘割断。
    ///
    /// 实测（2026-09-05）：27–55 mm / 180° 那条槽几乎割断圆盘 ⇒ 电流没有回路、
    /// 解发散，**测试宿主当场崩掉，一行结果都没留下**。
    /// ⇒ 与判据「圆盘盖得住管孔＋焊脚」同一个套路：**闭式反解，不用试**。
    ///
    /// 三道桥都要留够（minBridgeMm）：
    ///   内桥 = r内 − (管孔 + 焊脚)      —— 槽与管孔之间
    ///   外桥 = 盘半径 − r外              —— 槽与盘缘之间
    ///   周向桥 = (360 − 张角)/360 × 2π·r内 —— 电流绕过去的那条路
    /// 返回的是**周向桥**反解出来的张角上界（另两道由 SlotBandMm 的取值保证）。
    /// </summary>
    public double SlotSpanMaxDeg(double weldLegMm, double minBridgeMm = 6.0)
    {
        var (rin, rout) = SlotBandMm(weldLegMm);
        if (rin - (HoleRadiusMm + weldLegMm) < minBridgeMm * 0.5) return 0;   // 内桥不够 ⇒ 不许开
        if (DiscRadiusMm - rout < minBridgeMm * 0.5) return 0;                 // 外桥不够 ⇒ 不许开
        double arc = 2 * System.Math.PI * rin;
        if (!(arc > minBridgeMm)) return 0;
        return System.Math.Max(0, 360.0 * (1.0 - minBridgeMm / arc));
    }

    /// <summary>
    /// ★★★★★ 由本设计记录**造出可直接求解的整线算例** —— 复现设计记录数字的唯一入口。
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
        // ★★★★★ 管孔半径曾有**两处来源**（2026-08-28 查出：本类写死 25.0，求解器用 TubeIdMm*0.5 + WallMm），
        //   那时对不上就拒算。R30（2026-09-10）：内径进了几何（TubeIdMm 字段），整线的管内径也从本设计取 ⇒ 只剩一个来源。
        //   参数表的「内径 ID」只是新设计的默认值（页面读控件成设计时抄进来）；两边不等时以**设计**为准并说出来。
        if (System.Math.Abs(baseInputs.TubeIdMm - TubeIdMm) > 1e-9)
            System.Diagnostics.Debug.WriteLine($"参数表内径 {baseInputs.TubeIdMm:0.0} ≠ 设计内径 {TubeIdMm:0.0} —— 整线按设计的内径算（几何只有一个来源）");

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
            TubeIdMm = TubeIdMm,       // R30：整线的管内径从本设计取（与法兰管孔同一个来源）
            Base = p,
            WallMm = WallMm,
            JDesignAPerMm2 = JDesignAPerMm2,      // ★ 判据「法兰截面 J」的限值 = J+1 从这里来（用户 2026-09-09）
            UseMeasuredCurrent = false,      // 由控温反算 —— 第一性
            CheckRamp = checkRamp,
            SetpointC = SetpointC,
            SegLengthMm = SegLengthMm,
            // ★★★ 按**实际片数**造（用户 2026-09-03：段数由 UI 决定）。
            //   原来是写死的 Plate(0..3) 与四个 ClampTempC —— 分 4 段（5 片）时
            //   第 5 片根本不进 LineCase，而判据表照样出数：**算的是另一个零件**。
            //   ⚠ 扫「循环 <4」抓不到这两行：它们是字面枚举，不是循环。
            FlangePlates = Enumerable.Range(0, FlangeCount)
                                     .Select(j => Plate(j, discFloor)).ToArray(),
            ClampTempC = Enumerable.Repeat(ClampTempC, FlangeCount).ToArray()
        };
    }

    /// <summary>
    /// ★★★★★ **舌片厚度按截面定**（用户 2026-09-08，R11）：「舌片厚度不是旋钮，是截面积 I/10 ÷ 舌宽」。
    /// 逐片：t_舌 = I_设计(该片) /(J_设计 × 舌片最窄有效宽)，向上落图纸格，不低于板料下限（烧穿 <see cref="DesignInputs.WeldMinThicknessMm"/>）。
    /// 设计电流由 20 °C/h 空管升温算（<see cref="DesignCurrent"/>），只与管、保温、工况有关 ⇒ 舌片厚**不随圆盘板厚变**，与圆盘各级解耦。
    /// 谁调用：求解器每轮开头（<c>Solver.ApplySectionFloor</c>）、页面读控件成设计时（照图纸核算也按这条）。
    /// 电流为 0（没有段）时该片留 NaN，不给一个看起来正常的数。
    /// </summary>
    public double[] SizeTongues(DesignInputs baseIn, LineResult? last = null,
                                double? jDesign = null, double quantMm = 0.01)
    {
        double jD = jDesign ?? JDesignAPerMm2;      // 不传就用本设计设定的 J（工程师在 ① 输入定的）
        var dc = DesignCurrent.ForLine(this, baseIn, last);
        double floorD = DiscFloorMm(baseIn);
        int n = FlangeCount;
        if (TongueThickMm.Length != n) TongueThickMm = FitArr(TongueThickMm, n);
        for (int j = 0; j < n; j++)
            SizeTongue(j, j < dc.PlateA.Length ? dc.PlateA[j] : 0, baseIn, jD, quantMm);
        return TongueThickMm;
    }

    /// <summary>
    /// ★ R23（2026-09-10）：**一片**的舌片厚按当前几何闭式重定 —— 求解器每动一次孔径／拉长比／槽张角都调它，
    /// 开孔让最窄有效宽变小 ⇒ 舌片按 I/(J·w_min) 加厚（用户：「挖孔会造成 J 超过设计值，所以舌片必须重新搜形状」），
    /// 铂重记在候选头上，④ 终验由构造满足。几何退回去时它也退回去（纯函数，不是只增旋钮）。
    /// 返回新值；电流为 0 或舌片被切断 ⇒ NaN（不给一个看起来正常的数）。
    /// </summary>
    public double SizeTongue(int j, double iA, DesignInputs baseIn, double? jDesign = null, double quantMm = 0.01)
    {
        double jD = jDesign ?? JDesignAPerMm2;
        int n = FlangeCount;
        if (TongueThickMm.Length != n) TongueThickMm = FitArr(TongueThickMm, n);
        if (j < 0 || j >= n) return double.NaN;
        if (TabArmX0Mm.Length != n) { TabArmX0Mm = FitArr(TabArmX0Mm, n); TabArmX1Mm = FitArr(TabArmX1Mm, n); TabArmThickMm = FitArr(TabArmThickMm, n); }
        double Q(double v) => System.Math.Ceiling(System.Math.Max(v, baseIn.WeldMinThicknessMm) / quantMm - 1e-9) * quantMm;
        var g = Plate(j, DiscFloorMm(baseIn));
        if (!(iA > 0)) { TongueThickMm[j] = double.NaN; TabArmX0Mm[j] = TabArmX1Mm[j] = TabArmThickMm[j] = double.NaN; return double.NaN; }
        var widths = SectionSizing.TabWidths(g, ClampLengthMm);
        if (widths.Count == 0) { TongueThickMm[j] = double.NaN; TabArmX0Mm[j] = TabArmX1Mm[j] = TabArmThickMm[j] = double.NaN; return double.NaN; }
        double xT = g.Tangent().X;
        // ★ R29：切口所在的那一段横坐标 ⇒ 加厚带；带外按完整宽定杆厚。切口贴到切点（2 mm 内）带就到切点。
        double cutLo = double.NaN, cutHi = double.NaN;
        foreach (var (x, w) in widths)
            if (w < 2 * g.HalfWidth(x) - 1e-6)
            { cutLo = double.IsNaN(cutLo) ? x : System.Math.Min(cutLo, x); cutHi = double.IsNaN(cutHi) ? x : System.Math.Max(cutHi, x); }
        double wMinAll = widths.Min(t => t.WidthMm);
        if (double.IsNaN(cutLo))
        {
            TabArmX0Mm[j] = TabArmX1Mm[j] = TabArmThickMm[j] = double.NaN;
            if (wMinAll <= 1e-9) { TongueThickMm[j] = double.NaN; return double.NaN; }
            TongueThickMm[j] = Q(iA / (jD * wMinAll));
            return TongueThickMm[j];
        }
        double x0 = System.Math.Max(widths.Min(t => t.X), cutLo - 2.0);
        double x1 = cutHi + 2.0 >= xT - 1e-9 ? xT : cutHi + 2.0;
        double wArm = widths.Where(t => t.X >= x0 - 1e-9 && t.X <= x1 + 1e-9).Select(t => t.WidthMm).DefaultIfEmpty(wMinAll).Min();
        double wStem = widths.Where(t => t.X < x0 - 1e-9 || t.X > x1 + 1e-9).Select(t => t.WidthMm).DefaultIfEmpty(2 * g.HalfWidth(widths[0].X)).Min();
        if (wArm <= 1e-9) { TongueThickMm[j] = double.NaN; TabArmX0Mm[j] = TabArmX1Mm[j] = TabArmThickMm[j] = double.NaN; return double.NaN; }   // 被切断
        TabArmX0Mm[j] = x0; TabArmX1Mm[j] = x1; TabArmThickMm[j] = Q(iA / (jD * wArm));
        TongueThickMm[j] = Q(iA / (jD * System.Math.Max(wStem, 1e-9)));
        return TongueThickMm[j];
    }

    /// <summary>R29：这一片有没有加厚带。</summary>
    public bool HasTabArm(int j) => j < TabArmThickMm.Length && !double.IsNaN(TabArmThickMm[j]) && !double.IsNaN(TabArmX0Mm[j]) && !double.IsNaN(TabArmX1Mm[j]);

    /// <summary>R29：加厚带的一句话（给 Describe／界面／轨迹）。</summary>
    public string DescribeTabArms()
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int j = 0; j < TabArmThickMm.Length; j++)
            if (HasTabArm(j)) parts.Add($"片{j} {TabArmThickMm[j]:0.00} mm×[{TabArmX0Mm[j]:0},{TabArmX1Mm[j]:0}]");
        return parts.Count == 0 ? "" : "／叉臂 " + string.Join(" ", parts);
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

    /// <summary>舌片厚逐片格式化：NaN 印「=板」（与基板同厚，旧口径），不印 NaN。</summary>
    public static string FmtT(double[] a) =>
        string.Join("/", System.Linq.Enumerable.Select(a, v => double.IsNaN(v) ? "=板" : v.ToString("0.00")));

    public string Describe() =>
        (Invalid.Length > 0 ? "★ **已失效** " : "") +
        $"[{Name}] 管壁 {WallMm:0.0}／{(System.Math.Abs(TubeIdMm - 50.0) > 1e-9 ? $"内径 {TubeIdMm:0}／" : "")}管保温 {TubeInsulMm:0}／盘Ø{2 * DiscRadiusMm:0}／" +
        // ⚠ 必须逐个格式化。`string.Join("/", double[])` 打出来的是
        //   「1.3600000000000003/2.55656893078647」这种二进制残渣，而它会**直接进报告**——
        //   读的人无从分辨那是「算出来的精度」还是「忘了格式化」。（2026-08-17 实际发生。）
        $"舌 {TabLengthMm:0}×{2 * TabHalfWidthMm:0}／板厚 {Fmt(TabThickMm, "0.00")}／舌片厚 {FmtT(TongueThickMm)}{DescribeTabArms()}／" +
        $"舌保温 {Fmt(TabInsulMm, "0.0")}／" +
        $"环 r≤孔+{RingWidthMm:0}→×{Fmt(RingMul, "0.00")}／舌根圆角 R{TabFilletMm:0}／" +
        $"压接 {ClampLengthMm:0} 夹 {ClampTempC:0} °C" +
        (System.Math.Abs(JDesignAPerMm2 - SectionSizing.JDesignAPerMm2) > 1e-9 ? $"／J {JDesignAPerMm2:0.#}（工程师设定，极限 {JCheckAPerMm2:0.#}）" : "") +
        $"　合计 {TotalMassG:0} g";

    // ════════════════════════════════════════════════════════════════════
    // ★★★★★ 设计记录（**全部重解**，2026-08-17 —— 这个日期是对的，
    //   由 deliverable/设计记录_管壁0.8mm.3dm 的内容实证，详见 W08 的 Provenance）
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
    public static readonly DesignSpec W08 = new()
    {
        Name = "管壁 0.8 · 留余量",
        Provenance = "--shape + D8 定尺寸（Core/Sizer.cs）；60 轮，取最轻的**有裕度**的全过点。" +
                     "数值已按图纸精度量化（板厚 0.01／舌保温 0.1 mm）后复核，`--window` 逐条对上。" +
                     "★ 2026-08-25 把这条出处查清了（此前更正过一次，**那次说过头了**，一并纠正）：" +
                     "**日期是对的**。deliverable/设计记录_管壁0.8mm.3dm 生成于 2026-08-17 13:45，" +
                     "二进制里舌长 −140.0 出现 64 次、板厚 0.89/2.45/2.35/0.73 各 114 次，与本档逐位相同；" +
                     "作废档的 −90.0 与 2.11 一次都没有 ⇒ **本档的几何那天确实已经存在**。" +
                     "错的只是**工具归属**：`--shape`、`Core/Sizer.cs`、代号 D8 三样都是 2026-08-20 的" +
                     "提交 09c8d9b 才写的（那次提交标题是「输出框改用 Excel 式对齐」，正文没提设计记录被换掉）" +
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
        RampH = 0.058, DiscOverK = -0.208, HoleFluxW = 1.123, FlangeDipK = 4.720, TubeJ = 9.506,
        // ★★★★★ 网格无关复核（2026-09-02 重测，**复核的是本档这个设计本身**）。
        //   命令：--cli --quiet --judge --verifymesh --wall 0.8
        //   阶梯：1.000 mm 2166 单元 ③ 7.742 ／ 0.500 mm 5218 单元 ③ 9.785（差 +2.043/1，未收敛）
        //         0.250 mm 15055 单元 ③ 10.329（差 +0.544/1 ⇒ 收敛）
        //   中带确认也通过（中带一并加密后 ③ 仅动 +0.054/1）⇒ 按特征分区没有把判据带偏。
        //   交叉验证：界面那条路（载入设计记录 → 核算整线 → ◆ 网格无关复核）独立跑出
        //   同一组数（③ 10.33、②′ 3.39、②″ −0.00、3548 g、0.250 mm/15055 单元）。
        VerifiedMeshMm = 0.250, VerifiedFlangeDipK = 10.329,
        VerifiedHoleFluxW = 3.386, VerifiedDiscOverK = -0.004,
        VerifiedNote = "⚠ **本档这个设计加密复算到数不再变之后是不过的**：法兰增量温降 "
                     + "10.329 K，**越限**（上限 10）。上面「记录值」那一列（4.720 K）是"
                     + "**导航网格 2 mm** 上的数，粗网格把孔边与焊脚那一圈的梯度抹平了。"
                     + "　⚠ 2026-09-02 之前这里填的是 7.950/2.628 —— 那组数**不是本档的**，"
                     + "是「求解器解出来的那个设计」（≈3480 g）的复核值，被误抄到本档记录上"
                     + "（`--solve --verifymesh` 复核的是解出来的那一点，`--judge --verifymesh` 才复核本档；"
                     + "单元数也对不上：14982 vs 本档 15055）。现已按本档实测重填。",
    };

    /// <summary>底档：管壁压到焊接烧穿下界。</summary>
    public static readonly DesignSpec W06 = new()
    {
        Name = "管壁 0.6 · 底档",
        Provenance = "--shape + D8 定尺寸（Core/Sizer.cs）；60 轮，取最轻的**有裕度**的全过点。" +
                     "数值已按图纸精度量化（板厚 0.01／舌保温 0.1 mm）后复核。" +
                     "★ 2026-08-25 查清：日期**是对的**（deliverable/设计记录_管壁0.6mm.3dm 里板厚 " +
                     "0.64/1.86/1.73/0.62 各 114 次，与本档逐位相同），错的是工具归属。同 W08 那条，不再重复。" +
                     "⚠ 同样**不能拿来复现**：今天的 --shape 种子默认就是本档，用它重推等于从答案出发",

        Binding = "管壁 0.6 = 焊接烧穿下界（余量 0）；端片板厚 0.62–0.64 也逼近同一条下界 0.60",
        WallMm = 0.6,
        DiscRadiusMm = 30.0, TabLengthMm = 140.0, TabHalfWidthMm = 30.0,
        TabThickMm = new[] { 0.64, 1.86, 1.73, 0.62 },
        TabInsulMm = new[] { 0.4, 0.4, 0.4, 0.6 },
        RingMul = new[] { 1.00, 1.00, 1.00, 1.00 },
        TotalMassG = 2656, TubeMassG = 1841, FlangeMassG = 815, ResidualK = 0.5,
        RampH = 0.073, DiscOverK = -0.284, HoleFluxW = 0.820, FlangeDipK = 6.124, TubeJ = 10.961,
        // ★★★★★ 网格无关复核（2026-09-02 重测，**复核的是本档这个设计本身**）。
        //   命令：--cli --quiet --judge --verifymesh --wall 0.6
        //   阶梯：1.000 mm 2182 ③ 8.521 ／ 0.500 mm 5201 ③ 9.386 ／ 0.250 mm 14842 ③ 10.120
        //         0.125 mm 47884 单元 ③ 10.859（差 +0.739/1 ⇒ 收敛）
        //   中带确认也通过（③ 仅动 +0.017/1）⇒ 按特征分区没有把判据带偏。
        //   ⚠ 单路测量（0.8 档另有界面那条路交叉验证过，本档只跑了命令行这一条）。
        VerifiedMeshMm = 0.125, VerifiedFlangeDipK = 10.859,
        VerifiedHoleFluxW = 2.134, VerifiedDiscOverK = -0.003,
        VerifiedNote = "⚠ **本档这个设计加密复算到数不再变之后是不过的**：法兰增量温降 "
                     + "10.859 K，**越限**（上限 10）。上面「记录值」那一列（6.124 K）是"
                     + "**导航网格 2 mm** 上的数。"
                     + "　⚠ 2026-09-02 之前这里填的是 9.453/1.595 —— 那组数**不是本档的**，"
                     + "是「求解器解出来的那个设计」（2616 g，本档是 2656 g）的复核值，"
                     + "被误抄到本档记录上；单元数也对不上（47567 vs 本档 47884）。现已按本档实测重填。",
    };

    // ── 已作废的两档：**留着**，不删。
    //   删掉就没人知道 3DM／论文／说明书里那些 3106 / 2388 g 是哪来的、为什么不能再用；
    //   而且自检门需要它们当**活样本**：⑤ 这条判据必须始终抓得住它们（见 --selfcheck A）。
    public static readonly DesignSpec Retired08 = new()
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
        RampH = 0.057, DiscOverK = 1.050, HoleFluxW = 1.351, FlangeDipK = 5.517, TubeJ = 9.506,
    };

    public static readonly DesignSpec Retired06 = new()
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
        RampH = 0.078, DiscOverK = 1.186, HoleFluxW = 1.583, FlangeDipK = 5.161, TubeJ = 10.961,
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
    public static readonly DesignSpec[] Builtin = { W08, W06, Retired08, Retired06 };

    /// <summary>
    /// 内置档 + <c>finaldesigns/*.fd.json</c>。顺序：内置在前（下拉里先看到基准），文件档在后。
    ///
    /// ⚠ 读档失败**不静默** —— 见 <see cref="DesignSpecStore.LoadErrors"/>，
    ///   启动路径与 --selfcheck 都会把它当失败报出来。少一个档 = 少一组判据。
    /// </summary>
    public static DesignSpec[] All { get; private set; } = Scan();

    private static DesignSpec[] Scan()
        => Builtin.Concat(DesignSpecStore.LoadAll(Builtin.Select(x => x.Name))).ToArray();

    /// <summary>
    /// 重扫 <c>finaldesigns/</c> 并重建 <see cref="All"/>，然后广播 <see cref="Reloaded"/>。
    ///
    /// **另存新档之后必须调**：在此之前 <c>All</c> 只在启动时算一次，
    /// 于是刚存的档要重启 APP 才出现在下拉里 —— 而对着一个「已经存好了」的提示
    /// 却在下拉里找不到它，工程师最可能的反应是再存一次（撞重名被拒），
    /// 或者以为没存上。
    ///
    /// <see cref="DesignSpecStore.LoadAll"/> 每次进来先清 <c>LoadErrors</c>，
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

    /// <summary><see cref="All"/> 变过了。界面上每个列出设计记录的下拉都该挂上来。</summary>
    public static event System.EventHandler? Reloaded;

    /// <summary>
    /// 当前生效的档。**默认取保守的 0.8** —— 业主尚未在两档间拍板，
    /// 而 0.6 把壁厚压在焊接下界上、管 J 只剩 9 %，这两条都属于现场判断，不属于计算。
    /// </summary>
    public static DesignSpec Current = W08;

    /// <summary>按管壁取档（命令行 `--wall 0.6`）。找不到返回 null —— **不要静默回退**。</summary>
    public static DesignSpec? ByWall(double wallMm)
    {
        foreach (var d in All)
            if (System.Math.Abs(d.WallMm - wallMm) < 1e-6) return d;
        return null;
    }

    /// <summary>解析 `--wall &lt;mm&gt;`，缺省用 <see cref="Current"/>；给了但不认识就抛，不静默。</summary>
    public static DesignSpec Select(string[] args)
    {
        int i = System.Array.IndexOf(args, "--wall");
        if (i < 0 || i + 1 >= args.Length) return Current;
        if (!double.TryParse(args[i + 1], out double w))
            throw new System.ArgumentException($"--wall 的值解析不了：{args[i + 1]}");
        return ByWall(w) ?? throw new System.ArgumentException(
            $"没有管壁 {w:0.0} mm 的设计记录。现有：{string.Join("、", System.Linq.Enumerable.Select(All, d => d.WallMm.ToString("0.0")))}");
    }
}
