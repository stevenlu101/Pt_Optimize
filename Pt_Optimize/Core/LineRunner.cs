using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// ★ R48 审查第 1 条（2026-09-14，Opus 5）：**空管到温稳态的控温点从哪来**（<see cref="DesignSpec.BuildCase"/> 的参数；算例上只作记录，见 <see cref="LineCase.EmptyTubeSetpointFrom"/>）。
///
/// 用户 2026-09-14 只说了「设备到温后、进玻璃前的空管保温以稳态计算」，**没说**空管保温时控温点是多少。仓库里有两种读法：
///   · <see cref="RampTarget"/>：「到温」= 升温目标。用户 2026-09-08 定「升温目标全线 1150 °C」（= <see cref="LineCase.RampTargetC"/>，DesignCurrent.cs 档头）。
///   · <see cref="AsGiven"/>：沿用算例的 <see cref="LineCase.SetpointC"/>（BuildCase 里就是设计记录的生产控温点，如 1150/1080/1050）。
/// 两种读法下游片的结论形态可能不同（梯度 1150→1080→1050 本身就推下游片的热），**待用户定**；探针两种都跑。
/// </summary>
public enum EmptyTubeSetpoint
{
    /// <summary>全线取升温目标 <see cref="LineCase.RampTargetC"/>（用户 2026-09-08：升温目标全线 1150 °C）。</summary>
    RampTarget = 0,
    /// <summary>控温点原样取算例的 <see cref="LineCase.SetpointC"/>（BuildCase 里 = 设计记录的生产控温点）。</summary>
    AsGiven = 1,
}

/// <summary>
/// 整线算例的**全部输入**。管子走数值，法兰形状走 Rhino（每片可用不同 .3dm）。
/// </summary>
public sealed class LineCase
{
    // ── 管（数值输入）
    //
    // ★★ 2026-08-20：TubeIdMm / SegLengthMm / GradeName 改成**哨兵**（NaN / 空串）。
    //
    //   病灶：它们原来写死 50.0 / 300.0 / "Pt"，而 Run 里又用它们**覆盖** p 的同名字段
    //   （见 Normalize 下方的 `p.TubeIdMm = c.TubeIdMm` 那几行）⇒ 工程师在参数表里改
    //   「内径」「段长」「铂材牌号」，**整线链完全无视**，界面上却毫无异样。
    //   这正是用户说的「工程师根本不知道自己目前在算什么」的一半病因。
    //
    //   全仓 45 处 `new LineCase{...}` 没有任何一处显式设过这三项（唯一出现是
    //   FlangeAutoSizer.CloneCase 的**传播**），而 DesignInputs 的默认值恰好逐位相同
    //   （50.0 / 300 / "Pt"）⇒ 接上之后 `new DesignInputs()` 那条路零漂移，
    //   已用 `--cli --selfcheck` 逐档对账证明。
    //
    //   ⚠ 用哨兵而不是直接删字段：`--tscan` 之类的地方要能显式指定段长，
    //     保留「可覆盖」这个能力，只是默认改成「跟着参数表走」。
    public double TubeIdMm = double.NaN;
    public double WallMm = 1.0;
    /// <summary>
    /// 设计电流密度 J（A/mm²，用户 2026-09-09：工程师设定，预设 10）。判据「法兰截面 J」的限值 = J+1
    /// （<see cref="SectionSizing.JCheckOf"/>）只从这里读 —— 限值的唯一来源是 LineCase。
    /// </summary>
    public double JDesignAPerMm2 = SectionSizing.JDesignAPerMm2;
    /// <summary>
    /// ★★★★★ **每段直接加热铂金管的长度，逐段单独设定**（用户 2026-09-03）。
    ///
    /// 原来是**一个标量**（所有段同一个长度，取自参数表的「段长 L」）。
    /// 用户：「每段直接加热铂金管的长度必须是可以单独设定的」——
    /// 现场三段的加热长度本来就可以不一样，一个数按不住。
    ///
    /// ⚠ 空数组 = 还没给 ⇒ <see cref="Normalize"/> 按 <c>Base.TubeLengthMm</c> 铺满。
    ///   **不要在别处补默认**：一个数两处来源，迟早对不上（本仓栽过多次）。
    /// ⚠ 它同时是**支承跨距**（SupportSpanMm）与**该段铂重**的长度，
    ///   所以逐段化之后各段铂重不再等分 —— 报告里的分段铂重会跟着变。
    /// </summary>
    public double[] SegLengthMm = System.Array.Empty<double>();
    public string GradeName = "";

    /// <summary>各段控温点 °C（控温点在每段中点）。长度即段数。</summary>
    public double[] SetpointC = { 1150, 1080, 1050 };
    /// <summary>各段玻璃压力水头 m</summary>
    public double[] HeadM = { 0.3, 0.6, 1.0 };

    // ── 电流：两种模式
    /// <summary>true = 用实测电流（秒级）；false = 由控温点反算（分钟级，可预测新几何）</summary>
    public bool UseMeasuredCurrent = true;

    /// <summary>
    /// ⚠ **这三个数不是实测值** —— 是模型自己在实况保温下反算出来的（§4.2g：1663/1519/1466，
    /// §4.2l：1655）。现场至今**没有给过电流实测**（§6 待补：稳态实测电流）。
    ///
    /// 保留字段名是为了不破坏算例文件，但读结果时必须记得：用它跑出来的
    /// 「实测电流模式」其实是「模型电流模式」，两边同源，**不构成任何验证**。
    /// 用户只给过定性判断「稳态电流密度远低于 10 A/mm²」，而这组数对应 J≈10.3 ——
    /// 即它们本身就被认为偏大（§4.2l）。真拿到实测值请直接覆盖本数组。
    /// （§7 的教训「示例参数被当成实测」，这里差点又犯一次。）
    /// </summary>
    public double[] MeasuredCurrentA = { 1655, 1510, 1446 };

    /// <summary>
    /// **解析几何**的法兰（长度 = 段数+1）。非空时**优先于** <see cref="FlangeFile3dm"/>。
    ///
    /// 用途：参数化搜索阶段用它遍历形状（圆盘半径、舌片长宽、阶梯厚度分布），
    /// 定下来之后再由 Rhino 出 .3dm 走 <see cref="FlangeFile3dm"/> 复核。
    /// 两条路进的是**同一个** ShellMesh → ShellCurrent → ShellThermal，
    /// 差别只在厚度场是解析给的还是量出来的（§4.2j 已做过两者的回归）。
    /// </summary>
    public FlangePlate[] FlangePlates = Array.Empty<FlangePlate>();

    /// <summary>
    /// **只供 ⑤⑥ 几何判据用**的等效片。`.3dm` 模式下 <see cref="FlangePlates"/> 是空的
    /// （几何来自厚度场，不是解析形状），于是 ⑤⑥ 恒为「无法判定」——
    /// 而「无法判定不算通过」⇒ **.3dm 这条路永远解锁不了 ⑤ 交付**。
    ///
    /// 但「分析几何变数」其实已经把盘半径、管孔半径、舌端 X、舌端半宽全反推出来了，
    /// 正是 <see cref="GeometryScreen.Judge"/> 需要的全部输入 —— 只是没接上。
    ///
    /// ⚠ **单独开一个字段，不往 FlangePlates 里塞**：那个数组是**求解**用的，
    ///   .3dm 模式下求解走厚度场，塞进去会让它改用解析形状去解 ——
    ///   那就成了「判的是 A、解的是 B」，比判不了更坏。
    /// ★ R47 B（2026-09-13）：**逐片**（长度 = 片数）。图纸路径的保温分界 insulBoundaryX 与舌盘分界 tabBoundaryX
    ///   取 GeomForJudge[j]（短了就用最后一份 —— 只有一份就是「复制到所有片」）；此前恒取 [0]、
    ///   insulBoundaryX 还用 new FlangePlate() 默认板的切点 —— 与图纸无关的数。
    ///   没有时从材料包络推切点，推不出就把 ②′ 与 ③ 判成无法判定（不许再用默认板）。
    /// </summary>
    public FlangePlate[] GeomForJudge = Array.Empty<FlangePlate>();

    /// <summary>第 j 片供判据／保温分界用的等效几何；没有就 null。</summary>
    public FlangePlate? GeomForJudgeAt(int j)
        => GeomForJudge.Length > 0 ? GeomForJudge[Math.Min(Math.Max(j, 0), GeomForJudge.Length - 1)] : null;

    /// <summary>
    /// ★ 截面 J 判据要用的那一套法兰几何：解析 <see cref="FlangePlates"/> 优先，没有就用 .3dm 的判据几何 <see cref="GeomForJudge"/>。
    /// 2026-09-17，Opus 5：<c>LineRunner.Judge</c> 里的截面 J 与 <see cref="RampSweep"/> 升温逐点按**该点实际电流**
    /// 重算截面 J，调的必须是**同一份**几何选择规则 —— 此前只在 Judge 里就地写了一遍；升温那条要是自己再写一遍，
    /// 图纸路径（<see cref="FlangePlates"/> 为空）上两边就会读到不同的板，而两个数印在同一张表里。
    /// </summary>
    public FlangePlate[] PlatesForJudge()
        => FlangePlates is { Length: > 0 } ? FlangePlates : GeomForJudge;

    /// <summary>
    /// ★ R47 B（2026-09-13）：`.3dm` 模式下的**逐片**舌片保温厚度 mm（长度 = 片数；短了就用最后一片的值）。
    /// NaN 或 &lt; 0.05 = 该片舌片裸露。**空数组** = 退回 <see cref="TabInsul3dmMm"/> 那个「同值填所有片」的旧标量。
    /// 病：此前图纸路径只有一个标量，基线的 5.1/2.8/8.6 三个不同舌保温在这条路上表达不了 ⇒ 两条路输入不可比。
    /// 读值一律走 <see cref="TabInsul3dmAt"/>，别直接读数组或标量。
    /// </summary>
    public double[] TabInsul3dmPerPlateMm = Array.Empty<double>();

    /// <summary>第 j 片在图纸路径上的舌保温 mm（逐片数组优先；空数组时是旧标量）。</summary>
    public double TabInsul3dmAt(int j)
        => TabInsul3dmPerPlateMm.Length > 0
           ? TabInsul3dmPerPlateMm[Math.Min(Math.Max(j, 0), TabInsul3dmPerPlateMm.Length - 1)]
           : _tabInsul3dmAll;

    private double _tabInsul3dmAll = double.NaN;

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5；数值把关人查出逐片圆盘保温在图纸路径上没有入口）：`.3dm` 模式下的**逐片**圆盘保温厚度 mm（长度 = 片数）。
    /// NaN、**空数组**或**下标越界** = 该片沿用整线 <see cref="DesignInputs.FlangeInsulThickMm"/>（旧口径，逐位不变）；整线「不包」（FlangeInsulated = false）时有值也按 0。
    /// 2026-09-14 Opus 5 更正（审查意见「短数组约定不一致」）：原写「短了用最后一片，与 TabInsul3dmPerPlateMm 同约定」，
    ///   与 DesignSpec 的逐片圆盘保温（越界 = 整线）不一致；现统一为越界 = 整线，规则本体在 FlangePlate.DiscInsulEffective（数组形态）。
    /// 解析路径（<see cref="FlangePlates"/> 非空）不读本字段 —— 板件自己带 FlangePlate.DiscInsulThickMm。
    /// 读值一律走 <see cref="DiscInsulEffectiveAt"/>，别直接读数组、板件或 Base。
    /// </summary>
    public double[] DiscInsul3dmPerPlateMm = Array.Empty<double>();

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：第 j 片圆盘保温在热解里**实际用的**厚度 mm —— 全仓唯一取值口径。
    /// 解析路径 = FlangePlates[j].DiscInsulEffectiveMm(Base)；图纸路径 = <see cref="DiscInsul3dmPerPlateMm"/> 的值（非 NaN 时；整线不包仍 0），
    /// 否则 Base.FlangeInsulThickMm。LineRunner 的逐片热解、整片热稳定、升温两节点参考项、设计电流的两节点对照都调它。
    /// 2026-09-14 Opus 5：两支都改调规则本体 FlangePlate.DiscInsulEffective（原图纸分支手写一份）；图纸分支越界 = 整线（原为用最后一片）。
    /// </summary>
    public double DiscInsulEffectiveAt(int j)
    {
        if (FlangePlates.Length > 0)
            return FlangePlates[Math.Min(Math.Max(j, 0), FlangePlates.Length - 1)].DiscInsulEffectiveMm(Base);
        return FlangePlate.DiscInsulEffective(DiscInsul3dmPerPlateMm, j, Base.FlangeInsulThickMm, Base.FlangeInsulated);
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5；审查意见「程序化造的图纸路径算例静默丢值」）：图纸路径上，第 j 片的**判据几何**（GeomForJudge）带了逐片圆盘保温，
    /// 而热解实际用的（<see cref="DiscInsulEffectiveAt"/>，只读 <see cref="DiscInsul3dmPerPlateMm"/>）与它不同 ⇒ 回 true 并给出两个值。
    /// 解析路径、判据几何没带逐片值、或两者相同 ⇒ false。LineRunner 据此在求解备注里写明「判据几何上的值没有用上」，不静默。
    /// 为什么不直接取判据几何上的值：图纸路径的取值约定（规格 2026-09-14）是「数组，否则整线」，判据几何是反推的等效片，不是设定的出处。
    /// </summary>
    public bool JudgeGeomDiscInsulIgnored(int j, out double onJudgeGeomMm, out double usedMm)
    {
        onJudgeGeomMm = double.NaN; usedMm = DiscInsulEffectiveAt(j);
        if (FlangePlates.Length > 0) return false;
        var g = GeomForJudgeAt(j);
        if (g is null || double.IsNaN(g.DiscInsulThickMm)) return false;
        onJudgeGeomMm = g.DiscInsulEffectiveMm(Base);
        return !onJudgeGeomMm.Equals(usedMm);
    }

    /// <summary>
    /// `.3dm` 模式下的**舌片保温厚度** mm。NaN 或 &lt; 0.05 = 舌片裸露（原行为）。
    /// ★ R47 B：已降为「同一值填所有片」的便捷入口 —— 逐片值在 <see cref="TabInsul3dmPerPlateMm"/>，
    ///   逐片数组非空时读这里得到的是第 0 片的值（读旧值的地方不崩）。
    ///
    /// ★ 2026-08-23：在此之前 .3dm 路径把舌保温**写死为裸露**
    ///   （`tabInsulThickMm: double.NaN`，注释「沿用现场实况『仅圆盘保温、舌片裸露』」），
    ///   于是这条路**根本没有守 ②′/③ 的那个主力旋钮**。
    ///
    ///   实测（设计记录几何 Ø60/舌140，只改舌保温）：
    ///       0.40/0.40/0.50/0.30 → ③ = 5.2 K　②′ = +1.12 W　✓ 全过
    ///       减半                → ③ = 35.2 K　②′ = +8.88 W　✗
    ///       0（裸舌）           → ③ = −102 K　②′ = **−256 W**　法兰 **2986 °C**（熔点 1768）
    ///   ⇒ 舌保温不是修饰，它就是把抽热钉住的那颗螺丝。
    ///     ShellThermal 的参数文档也早写着：「一裸就把端片推成净抽热、一全包又过冲成
    ///     净倒灌 —— **中间必然存在一个零点**」。
    ///
    /// ⚠ 默认仍是 NaN（裸露）——**不改既有 .3dm 算例的答案**。要用它得显式给值。
    /// </summary>
    public double TabInsul3dmMm
    {
        get => TabInsul3dmPerPlateMm.Length > 0 ? TabInsul3dmPerPlateMm[0] : _tabInsul3dmAll;
        set => _tabInsul3dmAll = value;
    }

    /// <summary>
    /// ★ R47 B（2026-09-13）：**内存里的厚度场**（长度 = 片数；只给一份就所有片共用）。
    /// 优先级：<see cref="FlangePlates"/>（解析）&gt; 本字段 &gt; <see cref="FlangeFile3dm"/>（走 Rhino 子进程提取）。
    /// 用途：拓扑优化线（Pt_Topo）把 ρ 场变成厚度场后直接进整线链，不必先写 .3dm 再读回；
    /// 测试也靠它把「同一解析板栅格化」喂进图纸路径与解析路径对照。与文件路径进的是同一段代码。
    /// </summary>
    public ThicknessField[] FlangeFields = Array.Empty<ThicknessField>();

    // ── 法兰（每片一个 .3dm，长度 = 段数+1；可重复同一文件）
    public string[] FlangeFile3dm = Array.Empty<string>();
    public string FlangeLayer = "法兰";
    /// <summary>每片在 .3dm 中的平面 Y；NaN = 取该图层第一片</summary>
    public double[] FlangePlaneY = Array.Empty<double>();
    /// <summary>厚度场提取步长 mm（1.0 足够分辨槽与阶梯）</summary>
    public double ThicknessStepMm = 1.0;

    /// <summary>
    /// 每片的**厚度整体标度**（长度 = 片数，缺省全 1）。仅 .3dm 路径生效。
    ///
    /// 为什么需要它：.3dm 给的是**固定**厚度，而 C2（管根温差 &lt;10 K）要求
    /// 法兰厚度精确到 ±0.008 mm（ΔT 对厚度斜率约 1300 K/mm，§4.2w）——
    /// 不可能靠画图碰运气碰到。于是把厚度整体缩放当成自由度：
    /// 自动定厚求出的是「这张图纸的厚度要整体 ×k」，工程师照 k 改一版图即可。
    ///
    /// t=0 的格（轮廓外、管孔、开槽）乘任何数仍是 0，**槽与轮廓不受影响**。
    /// </summary>
    public double[] ThicknessScale = Array.Empty<double>();

    /// <summary>
    /// ★ R47 第三轮 N5（2026-09-13）：**这个算例造不出来的原因**（空 = 正常）。图纸档（GeomSource = 图纸）的 DesignSpec.BuildCase
    /// 不造解析板，填这一句；<see cref="LineRunner.Run"/> 读到非空就原句返回 Ok=false，不算、不抛。
    /// </summary>
    public string RefusedWhy = "";

    /// <summary>
    /// **逐级厚度标度**：`LevelScale[片][级]`。非空时**优先于** <see cref="ThicknessScale"/>。
    ///
    /// 为什么要分级：整体缩放只有 1 个自由度，只能调整片的热平衡（管根温差 C2），
    /// 动不了**局部过热**（C1）—— 后者取决于厚度在半径方向上怎么分配。
    /// 单位面积发热 = ρe·K²/t（K = J·t 是面电流，守恒），
    /// 所以**把某一级加厚，就按比例压低该级的单位面积发热**。
    /// 孔周那一级正是峰值所在，让优化器自己决定各级比例，才谈得上同时满足两条约束。
    ///
    /// 归级方式：把格子的原始厚度与 <see cref="LevelThicknessMm"/> 逐一比较，取最近的那一级。
    /// </summary>
    public double[][] LevelScale = Array.Empty<double[]>();

    /// <summary>`LevelThicknessMm[片][级]` = 该片各级的**原始**厚度 mm（由 PlateShapeAnalyzer 给出）</summary>
    public double[][] LevelThicknessMm = Array.Empty<double[]>();

    /// <summary>
    /// **逐片铜排夹持温度** °C（长度 = 片数；空 = 全部用 <see cref="DesignInputs.BusbarClampTempC"/>）。
    /// &lt;0 表示该片自由端（不夹冷）。
    ///
    /// 为什么必须逐片：共用片走 √3 倍电流、发热 ∝I² 是端片的 2.6 倍
    /// （实测自由端下 490 W vs 156 W）。共用片**需要**夹冷把多余的热带走，
    /// 端片则相反 —— 它连自己的散热都不够，再夹冷就只能从管子抽（§4.3c）。
    /// 四片用同一个夹持温度，等于用一把尺子量两个完全不同的东西。
    /// </summary>
    public double[] ClampTempC = Array.Empty<double>();

    // ── 网格
    public double MeshFineMm = 2.0, MeshCoarseMm = 11.0, MeshFineRadiusMm = 50.0;

    /// <summary>
    /// ★ F7′（2026-09-23，Opus 5.5，C4′；决 29 自适应）：**这张网格的细区半径是从哪来的** —— 细区半径计划（<see cref="MeshAdapt.FineRadiusPlanOf"/>：初值、余量₀ 与输入、每次放大的原因与数、终值）。
    /// 由用计划造算例的一方写（MeshVerify 加密复算、Solver 求根与终局复核、保温搜索、可行窗口）；null = 本算例没经过计划（<see cref="MeshFineRadiusMm"/> 是算例缺省或调用方直接给的）。
    /// 只作记录与证据头（EvidenceHeader.ForLineCase），LineRunner 不读它 —— 网格只读 <see cref="MeshFineRadiusMm"/>。
    /// </summary>
    public FineRadiusPlan? MeshFineRadiusPlan;

    /// <summary>
    /// **内带**网格尺寸 mm（管孔 + 焊脚那一圈）。**0 = 不分内带**，与 2026-08-29 之前逐位一致。
    ///
    /// ★ 为什么要分（算法普查 A⑭）：此前只有一条细化带，而它的两个参数来自相反的两端 ——
    ///   尺寸由**最小**特征（焊脚）定、范围由**最大**特征（盘径/舌长）定
    ///   ⇒ 极细的格子被铺满整个大区域。实测 0.6 档复核 fine 收到 0.146 mm、
    ///   细化半径约 68 mm ⇒ 约 8 万单元，**超过 maxCells 上限，跑了八小时没出数**。
    /// </summary>
    public double MeshInnerMm;

    /// <summary>内带半径 mm（自管轴起算）。0 = 不分内带。</summary>
    public double MeshInnerRadiusMm;


    // ── 玻璃与验证
    public double GlassInC = 1150, GlassOutMeasuredC = 1130;

    // ── 判据（HANDOVER §4.2k 的判据体系）
    /// <summary>规程一：空管升温。关掉可省几秒，但那是**决定最小截面**的那条，默认开。</summary>
    public bool CheckRamp = true;
    public double RampFromC = 25, RampTargetC = 1150;

    /// <summary>
    /// 升温限时 h。**2026-08-14 由 3.0 改为 72.0** —— 3 h 是 §6 旧笔记里的假设，
    /// 而用户给的边界条件是「升温时间上限 **≤ 3 天**」（≈ 现状 20 °C/h：
    /// 25→1150 °C 需 56 h）。
    ///
    /// ⚠ 这个数不是无关紧要的显示项：`RampSolver.Reached` 的语义是
    /// **「限时内到不到」**，不是「渐近能不能到」。用 3 h 去判会把
    /// 「升得慢」误报成「升不到」—— 我据此写过一条「管壁 0.30 是硬物理墙」的结论，
    /// 是错的（见 §4.3i 的更正）。改限时后必须重跑所有带 CheckRamp 的算例。
    /// </summary>
    public double RampHours = 72.0;

    /// <summary>
    /// 现场升温速率 K/h。**20 是用户给的现场值**（§4.2s：「以温度控制功率，升温速率 20 °C/h」，
    /// 2026-08-25 再次确认「起始 71 A、max(法兰−管) +215 K、42.6 h **比较符合现状**」）。
    /// ⚠ 它与 <see cref="RampHours"/> 判的**不是同一件事**：那条判「能不能到」（电流开满的能力），
    ///   这条判「按现场那条慢坡走时，法兰会不会比管热到危险」。
    /// </summary>
    public double RampRateKPerH = 20.0;
    /// <summary>管根温差目标上限 K（③）。下限恒为 0：温差必须为正，即法兰比管冷。</summary>
    /// <remarks>★ R48 B（2026-09-14 Opus 5）：「法兰增量温降」已降为**旧判法参考量**，本限值只给那一行与「偏离本段控温点」参考行显示用，
    ///   不再卡交付。卡交付的冷侧限值是 <see cref="ColdUnderTcMaxK"/>。</remarks>
    public double RootDeltaMaxK = 10.0;

    /// <summary>
    /// ★★★★★ R48 B（2026-09-14 Opus 5）：**控温热偶的读数误差** K —— 热侧、冷侧两条判据的限值都从它来。
    ///
    /// 出处（用户 2026-09-14 定）：控温热偶装在**每段中点**，误差 **5 ℃**；「10 ℃ 是上下各 5 ℃」——
    /// 即旧判法「法兰增量温降 ≤ 10」那个 10 本来就是热偶上下两侧误差之和，拆开后每侧 5。
    /// 于是判据的基准从「模型算的管温」换成「**热偶读数**」：法兰上最热的铂不许高出读数 5 K，
    /// 管根最冷的那一端不许低于读数 5 K —— 超出这个带，热偶就看不出来了。
    /// ⚠ 限值只许这一个来源（照 SingleSourceLimitTests 的做法）；Criteria 的说明文字要写误差也读这里，不许另抄一个 5。
    /// </summary>
    public const double ThermocoupleErrorK = 5.0;

    // ★★★★★ U 路（2026-09-18，Opus 5）：**两条限值改成跟着工程师填的那两项走**（用户 2026-09-17/18：
    //   「冷侧那 5 度能不能放宽」→「请开工」）。写法照本类 TubeIdMm／SegLengthMm／GradeName 那条已经验过的路子：
    //   **哨兵 NaN = 跟着参数表走**，显式赋值仍然管用（CloneCase／ApplyFinalMesh 的传播、命令行造算例、门里造极端值）。
    //   ⇒ 限值只有一个来源：DesignInputs.HotOverTcAllowK／ColdUnderTcAllowK（默认 5 K = 热偶在 1100 °C 的误差）。
    //   ⚠ 别在别处再写一个 5：判据表、保温搜索、加密复算容差、说明书一律从这两个属性读（门：R48UTempBudgetTests）。
    private double _hotOverTcMaxK = double.NaN;
    private double _coldUnderTcMaxK = double.NaN;

    /// <summary>
    /// R48 B（2026-09-14 Opus 5）：热侧判据「最热铂高出热偶读数」的上限 K（硬安全线）。
    /// U 路（2026-09-18 Opus 5）：没显式设过 ⇒ 读 <see cref="Base"/> 的「最热铂高出热偶读数 允许值」（<see cref="DesignInputs.HotOverTcAllowK"/>，默认 = 热偶误差）。
    /// </summary>
    public double HotOverTcMaxK
    {
        get => !double.IsNaN(_hotOverTcMaxK) ? _hotOverTcMaxK
             : Base is not null ? Base.HotOverTcAllowK : ThermocoupleErrorK;
        set => _hotOverTcMaxK = value;
    }

    /// <summary>
    /// R48 B（2026-09-14 Opus 5）：冷侧判据「管根低于热偶读数」的上限 K（硬安全线，取代旧判法「法兰增量温降 ≤ 10」）。
    /// U 路（2026-09-18 Opus 5）：没显式设过 ⇒ 读 <see cref="Base"/> 的「管根低于热偶读数 允许值」（<see cref="DesignInputs.ColdUnderTcAllowK"/>，默认 = 热偶误差）。
    /// </summary>
    public double ColdUnderTcMaxK
    {
        get => !double.IsNaN(_coldUnderTcMaxK) ? _coldUnderTcMaxK
             : Base is not null ? Base.ColdUnderTcAllowK : ThermocoupleErrorK;
        set => _coldUnderTcMaxK = value;
    }

    private double _hotOverContactMaxK = double.NaN;

    /// <summary>
    /// ★★★★★ 决 103（业主 2026-09-24）：带玻璃稳态热侧判据「法兰最热处高出管接触处温度」的上限 K（硬判据；决103前 口径下不出这一行）。
    /// 照 <see cref="HotOverTcMaxK"/> 的写法：没显式设过 ⇒ 读 <see cref="Base"/> 的「法兰最热处高出管接触处温度 允许值」
    /// （<see cref="DesignInputs.HotOverContactAllowK"/>，默认 <see cref="CriteriaRules.HotOverContactMaxKDefault"/> = 10，出处在那里）。
    /// </summary>
    public double HotOverContactMaxK
    {
        get => !double.IsNaN(_hotOverContactMaxK) ? _hotOverContactMaxK
             : Base is not null ? Base.HotOverContactAllowK : CriteriaRules.HotOverContactMaxKDefault;
        set => _hotOverContactMaxK = value;
    }

    /// <summary>决 103：本算例的判据口径（= <see cref="Base"/> 的 <see cref="DesignInputs.CriteriaRuleSet"/>；没有参数表 ⇒ 生产缺省 决103）。</summary>
    public CriteriaRuleSet RuleSet => Base?.CriteriaRuleSet ?? CriteriaRuleSet.决103;

    /// <summary>
    /// 判据 ②″（圆盘区最高温 − 管温）的上限 K。
    /// ★ R48 B（2026-09-14 Opus 5）：这条已降为**参考量**「②″圆盘区最高温 − 管温（旧判法）」（代号 ②″ 不变），本限值只给那一行显示用；
    ///   卡交付的热侧限值是 <see cref="HotOverTcMaxK"/>（基准换成热偶读数，不再是模型算的管温）。下面是它当年的出处，留作病历。
    ///
    /// ★★★★★ 2026-08-15：由 **0 改成 5.0**，来源是用户给的两个现场数：
    ///   · **控温精度 ±5 K** —— 闭环把温度**稳住**的能力
    ///   · **铂热偶 1000 °C 以上测量误差 ±10 K** —— **知道**它是多少度的能力
    /// 两者不是一回事。限制「这个差别是否可分辨」的是较大的那个（±10 K），
    /// 但这里取**较保守的 5 K**；若实测 ②″ 离限值很远，5 与 10 之争即为空。
    /// （IEC 60584 S/R 型 Class 1 在 1100 °C 附近容差约 ±1 K，但那是新偶出厂容差；
    ///   加上漂移、不均质、安装与冷端，现场 1000 °C 以上 ±10 K 是实况 —— 以现场数为准。）
    ///
    /// 为什么原来的 0 不是工程判据（实测量化）：
    ///   · 峰值那一格（约 2×2 mm、厚约 4 mm）到管孔的导热通道 G = kA/L ≈ 0.29 W/K
    ///     ⇒ 0.05 K 的局部超温只对应 **0.014 W** 的倒流，
    ///     而该片净抽热 +4 W、该段加热功率约 3 kW ⇒ **占 0.35 % / 5 ppm**。
    ///   · 它却判掉了 686 g 铂（管壁 1.4 vs 1.2）与是否要加大舌根圆角。
    ///     **用任何仪器都测不出的 14 mW，决定了约 700 g 铂金。**
    ///   · 结构上也没有裕度：限值 0，而物理地板是 −0.04（盘缘 J=0 的峰，
    ///     在八档保温 × 五档倍率下恒定）⇒ **可行带宽只有 0.04 K**，比数值噪声还窄。
    ///
    /// 失效模式（共用法兰升温烧断）的真实物理是**局部热失稳**，
    /// 项目里已有闭式判据 `TCR·ΔT ≤ 2`（§4.3e），它允许的局部温升是几十上百 K。
    /// ⇒ 逐点 ≤ 0 是一个比物理严三个数量级的代理，现按现场可分辨的尺度取 5 K。
    /// </summary>
    public double DiscOverTempMaxK = 5.0;

    // ── 段↔法兰外层耦合的数值参数（见 LineRunner.Run 里为什么必须欠松弛）
    /// <summary>欠松弛因子。1.0 = 裸 Picard，在法兰倒灌的正反馈下会发散。</summary>
    /// <summary>
    /// 舌片**自由段**长度下界 mm（= 舌长 − 圆盘切点 − 压接段）。
    /// 现场参考（用户 2026-08-17）：铜排长 100／宽 60–80 mm，自由段基本留 100 mm。
    /// ⚠ 这是**装配约束**，性质同焊接烧穿下界 —— 不是算出来的，是现场条件给的。
    /// </summary>
    public double FreeTabMinMm = GeometryScreen.FreeTabMinDefaultMm;

    public double CoupleRelax = 0.35;
    /// <summary>
    /// 外层轮数上限。**200 只够贴着设计记录点用**：一改参数，g≈0.96 把扰动放大约 25 倍
    /// （极端档实测 134 倍），200 轮就不够 —— 而那只是「慢」，不是发散。
    /// Anderson 之后设计记录点只要 9 轮，所以放宽上限**只在真需要时才付时间代价**
    /// （循环一收敛就 break）。
    /// </summary>
    public int CoupleMaxRounds = 600;
    /// <summary>Anderson 加速（见 <see cref="Anderson"/>）。关掉即退回纯欠松弛 Picard。</summary>
    public bool UseAnderson = true;
    /// <summary>历史深度。状态维数只有 ≤10，深度 4 已足够张开慢模式子空间。</summary>
    public int AndersonDepth = 4;
    /// <summary>
    /// 安全阀：AA 步长超过 **κ×‖残差‖** 就丢弃并重启历史。
    ///
    /// ⚠⚠ κ 的量级**不能拍脑袋**，它由环路增益定：不动点距离 ≈ ‖F‖/(1−g)，
    ///   而本问题 g≈0.96 ⇒ **正确的那一步本来就有 25‖F‖ 那么长**。
    ///   κ=5 等于把「走对的那一步」当成异常挡掉 —— 实测难工况 139/159 步被丢弃、
    ///   深度永远 0。⇒ 取 50（≈2/(1−g)），只挡真正离谱的步。
    /// ⚠ 参照系是残差、**不是**欠松弛步 —— 挂在 ω 上会随 ω 收紧而把 Anderson 关死。
    /// </summary>
    public double AndersonKappa = 50.0;
    /// <summary>
    /// 收敛判据：相邻两轮管根温度变化 K。
    ///
    /// ★★★★★ 2026-08-15 收紧 1.0 → 0.02：**判据的分辨率必须优于求解器的收敛容差。**
    ///
    /// 病症：同一套几何两次运行，②″ 一次报 −0.00（全过）、一次报 +0.00（不过）。
    /// 追下去是这里：容差 1.0 K 时每次都打「外层耦合 5 轮收敛（管根温差 0.70 K）」，
    /// 而 ②″ 是在 **0.01 K** 量级上判过不过。
    ///
    /// ②″ = T_盘峰 − T_管根，两项同向随管根漂 ⇒ 差值没有 0.70 K 那么敏感；
    /// 实测有效灵敏度约 0.014（残差 0.70 K ⇒ ②″ 动 0.01 K）。
    /// 但这仍与被判的裕度（0.00…0.08 K）**同量级** ——
    /// ⇒ 可行性阶梯上那些「差 0.08 K」的精细区分，有一部分是在读收敛残差。
    ///
    /// ★★ 2026-08-15 再修：语义已改成「**距不动点的估计** Δ∞ ≈ δ·r/(1−r)」，
    /// 不再是「相邻两轮变化 δ」。故这里取 1.0 K —— 它现在的含义是
    /// 「解距真解不超过 1 K」，对着 ③ 的 10 K 限值是十分之一，够用。
    /// 轮数上限同步提到 200：g≈0.96 下从 δ~0.9 走到 Δ∞<1 需要上百轮。
    /// </summary>
    /// ★★★★★ R48 L（2026-09-17，Opus 5）：**1.0 → 0.1，并可按判据裕度再收紧**（见 <see cref="CoupleTolFromMargin"/>）。
    ///
    /// 病（实测，出处 deliverable/R48_L_管根判据_最后一位敏感度_本次开跑于2026-09-17_151814.txt）：
    ///   舌保温输入改 **1 ulp**（相对 1.7e−16），「管根低于热偶读数」从 4.511 K 跳到 4.885 K（动 0.374 K），
    ///   而这条硬安全线的裕度只有 0.489 K；共用片那两个接头的值在 4.42↔4.92 之间摆（0.50 K），
    ///   与本层的耦合剩余误差估计（0.430↔0.933 K）**同量级**。
    /// 根子就在这里：**停机容差 1 K 比这条判据的裕度还粗**（裕度 0.1～0.5 K）——
    ///   判据的分辨率必须**比停机容差细**，否则「过」与「不过」分不开（这句 2026-08-15 就写在上面，当时只落实到 ③ 的 10 K 限值上）。
    /// ⇒ 这里现在是**上限（最粗）**：真正用的那一个由 <see cref="LineRunner.CoupleTolKFor"/> 当场算（全仓唯一读口，不许再有第二处写法）。
    ///
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**「按裕度收紧」退出生产默认；这里改成绝对的、可复现的停机目标（不随当前裕度走）。**
    ///
    /// 病（实测出处 r48_U/deliverable/R48_U_不收敛点诊断_总表_本次开跑于2026-09-18_163414.txt 与九份分段输出）：
    ///   二分求的正是「裕度 = 0」的根 ⇒ 容差 = 0.1 × 当前最小裕度 每逼近一步就压小一分，根附近必撞下限 0.02 K；
    ///   停机还要真残差 × 放大（实测 15.9～33）过关 ⇒ 真残差要压到 6e−4 K，低于段解地板
    ///   （SegmentSolver.Profile 外层 Picard 0.01 K／内层 Bvp1D 0.005 造成的 0.001～0.04 K 极限环）⇒ 能否在 600 轮内停下成了轮数彩票：
    ///   W08 片3 舌保温 13.036 要 98 轮、13.074 要 **591 轮**（上限 600）；关掉按裕度收紧同两点 46／39 轮。
    /// ⇒ 容差改成**绝对目标**（取值与实测阶梯见 HANDOVER §0.-16M；候选「§0.-10 可复现门 0.05 K 的一半」与「段解地板 × 放大反推」两条一起量过才定）。
    ///   分辨率的问题改由**认证误差**接住：认证误差 = 放大 × 停机残差（<see cref="LineResult.CertErrK"/>）随结果交下游，
    ///   格点上硬安全线裕度小于它 ⇒ 该点「判不了那么细」，求解器往保守方向再走一格重判（Solver.RaiseUntil）；
    ///   整线结果里裕度小于它的硬安全线标成判不了（<see cref="LineRunner.MarkUndeterminedIfInsideCertErr"/>，判不了不算过）。
    /// 取值 **0.025 K = §0.-10 可复现门 0.05 K 的一半**，实测阶梯（deliverable/R48_M_停机容差阶梯_两点_本次开跑于2026-09-18_221311.txt，段解地板降十倍之后）：
    ///   常数 0.05 K：13.036 → 57 轮（认证误差 0.013）、13.074 → 21 轮（0.042）；常数 0.025 K：57 轮（0.013）／24 轮（0.015）—— 两档轮数几乎相同，取细的那档。
    ///   ⚠ 旧段解地板上 0.05 K 是彩票（13.036 91 轮、13.074 271 轮没停，同目录 214343 那跑）：绝对目标能不能达到，取决于段解地板 × 放大，不取决于容差怎么写。
    /// 门：R48MStopTolGateTests（快）、R48MStopTolTwoPointTests（慢：两点各 ≤120 轮、注射改回按裕度 ⇒ 13.074 超 300 轮）。
    public double CoupleTolK = 0.025;

    /// <summary>
    /// ★ R48 L（2026-09-17，Opus 5）：**停机容差按判据裕度收紧**。true（默认）= 容差
    /// <c>min(CoupleTolK, max(CoupleTolFloorK, CoupleTolMarginFrac × 当前最小硬安全线温度裕度))</c>；
    /// false = 退回一个常数 <see cref="CoupleTolK"/>（历史口径就是这一支，配 CoupleTolK = 1.0 即改前行为）。
    ///
    /// 为什么按**裕度**而不是按限值：判据的限值是 5 K，而「过不过」是在**裕度**上判的 ——
    /// 裕度 0.489 K 时，容差 1 K 的停机噪声整个盖过它。
    /// 为什么留这个开关：门要能把「改回 1 K」这条病**注射回去**（注射后可复现门必须红）。
    /// </summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**默认改为 false**（生产口径 = 绝对目标 <see cref="CoupleTolK"/>）。
    ///   true 现在只剩一个用途：门把「按裕度收紧」这条病**注射回去**（R48MStopTolTwoPointTests：13.074 那一点轮数必须 > 300，否则门守的是空气）。
    public bool CoupleTolFromMargin = false;

    /// <summary>容差占「当前最小硬安全线温度裕度」的比例（默认 0.1 = 判据分辨率比停机容差粗一个数量级）。</summary>
    public double CoupleTolMarginFrac = 0.1;

    /// <summary>容差下限 K（默认 0.02）：裕度趋 0 时防止容差跟着趋 0 ⇒ 死循环／必然撞轮数上限。</summary>
    public double CoupleTolFloorK = 0.02;

    /// <summary>
    /// ★★★★★ R48 L（2026-09-17，Opus 5）：**无法兰基线那一层自己的停机容差 K**（默认 1.0）。
    ///
    /// 为什么它不跟着主环一起收紧 —— 因为**容差按它服务的那条判据定**：
    ///   · 主环（段↔法兰）定的是两条**硬安全线**（最热铂高出热偶读数／管根低于热偶读数），裕度 0.1～0.5 K
    ///     ⇒ 容差必须细到 <see cref="LineRunner.CoupleTolKFor"/> 那一档。
    ///   · 基线只进**参考量**「法兰增量温降」（<c>LineRunner.ApplyBaseline</c> → <c>SegmentOut.FlangeDipK</c>
    ///     → 那条 <see cref="CheckKind.Reference"/> 判据；限值 10 K，不卡交付）⇒ 1 K = 限值的十分之一，够用。
    ///     这正是 2026-08-15 给 <see cref="CoupleTolK"/> 写 1.0 时的那条理由 —— 那条理由现在归它。
    ///
    /// 实测（deliverable\R48_L_耦合容差收紧_导航_本次开跑于2026-09-17_214736.txt，同一份 W08 导航档设计）：
    ///   两层一起收到 0.1 K：基线 **201 s** + 主环 75～92 s；基线留 1 K：基线 **19 s**。
    ///   ⇒ 收紧的机时**几乎全在基线那一层**，而它一个交付判据都不卡。
    /// ⚠ 门：「法兰增量温降」一旦升回硬安全线，这个 1 K 就不够了 —— R48LCoupleTolGateTests 盯着它的 Kind。
    /// </summary>
    public double BaselineTolK = 1.0;

    /// <summary>
    /// **不动点放大系数** = 1/(1−g)，实测环路增益 g ≈ 0.96 ⇒ **25**。
    ///
    /// 「相邻两轮变化 δ」**乘它**才是「到不动点的距离」。
    /// <see cref="CoupleTolK"/> 的含义是后者（见它自己的注释：「解距真解不超过 1 K」），
    /// 所以任何拿 δ 直接跟 CoupleTolK 比的地方**都是错的**。
    ///
    /// ★ 全程序只有这一份（2026-08-28 提出来）：主环与基线环各写一个 25，
    ///   就会出现「一处改了另一处没改」—— 本仓库最常见的病。
    /// </summary>
    public const double FixedPointAmp = 25.0;

    /// <summary>
    /// ★★★★★ R48 L（2026-09-16，Opus 5）：**段间端温不动点停机放大倍数的口径开关**（数值把关人 2026-09-16 定）。
    ///
    /// true（默认，**新口径**）＝ 当场算 1 + ℓt/Δx（<see cref="SegmentSolver.EndTempFixedPointAt"/>，两端各算、取最慢的那个）；
    /// false ＝ **历史口径**，写死 <see cref="FixedPointAmp"/> = 25。
    ///
    /// 为什么要留这个开关：门要能把新旧两条并排跑出来（把它关掉 = 把病注射回去，门必须红）。
    /// ⚠ 关掉它就是把「25」这个常数接回停机判定 —— 实测本算例闭式是 77～88，
    ///   25 口径在 1150 °C／夹头 100 °C 那点报「剩余误差 0.946 K &lt; 容差 1 K，已收敛」，而闭式是 3.039 K。
    ///   **那个「已收敛」是假的。** 不要为了让测试变绿去关它。
    /// ⚠ 只管**段间端温**这一层；外层法兰↔管耦合的抽热那一半不经过它（两层不能混）。
    /// </summary>
    public bool EndTempAmpFromDecayLength = true;

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：停机放大倍数的第二个来源 —— **实测雅可比放大** ‖(I−J)⁻¹‖∞（<see cref="LineRunner.NeighbourAmplification"/>）。
    /// 每次 <see cref="LineRunner.Run"/> 只量一次（零抽热、各段各解各的那一态；段解只解段，m+1 次段解），量完记在这里；
    /// NaN = 还没量（或量不出来：奇异／段解失败／只有一段 ⇒ 放大只用闭式 × 1.1）。同一算例再跑不重量（与 <see cref="BaselineRootC"/> 同一种缓存）。
    /// 生产口径：放大 = max(闭式 1 + ℓt/Δx × <see cref="LineRunner.StopAmpHeadroom"/>, 这个数)（<see cref="LineRunner.StopAmpOf"/>）。
    /// </summary>
    public double JacobianAmpCache = double.NaN;

    /// <summary>R48 M（2026-09-18，Fable 5.1）：要不要量实测雅可比放大（false = 只用闭式 × 1.1；探针量成本用）。</summary>
    public bool MeasureJacobianAmp = true;

    /// <summary>
    /// ★ 2026-09-23（F4，决 28）：法兰分区热账（圆盘区／舌片区）按有料面积份额拆盘缘上被切开的格（true，生产缺省）；
    /// false = 老口径按格心整格归一边 —— **只供门注入用**（「开 − 关」归因、「改回 ⇒ 红」），不是给工程师的开关，界面与存档不读它。
    /// 经 <see cref="LineRunner.PlateThermalInputs"/> → <see cref="PlateThermalSetup.ZoneByMaterialFraction"/> → ShellThermal.Solve 的同名参数。
    /// 它只动分区账、两区峰各自的值与峰位（及读它们的旧判法参考量「圆盘区最高温 − 管温」），不动温度场：**单片解本身（整线单点解，闭合修正 Δ = 0）**上
    /// 两区峰取大（最热铂）与格心分法逐位相同（R48F4ZoneShareGateTests 门 a 18 例单片、门 c W08 两行整线）。
    /// ⚠ 各自读两区峰的下游不在这句之内（2026-09-23 审查后补，F4-M3／C3-L3）：保温搜索的线性闭合（InsulationSearch 按两区峰各自的 κ 外推后取大：
    ///   Δ ≠ 0 时若 |Δ|·|κ 差| 超过原两区峰差，⑦ 会换项而不再逐位）；Sizer 与命令行外环的控制律读 TDiscMaxC − TRootC。都没量、没门。
    ///   保温搜索、Solver、Sizer 的算例都由 DesignSpec.BuildCase 新造，这一位恒为缺省 true，改回进不了这几条路径；整条搜索的「开 − 关」在现有接线下做不出来。
    /// </summary>
    public bool ZoneByMaterialFraction = true;

    /// ★ 2026-09-23（F3，**只供门**）：true ⇒ 本算例的逐片电流解按改前口径把**重构 J** 交给热场（ShellCurrent.SolveFor 的 faceHeat 改回，那一处读）。
    ///   internal：不进存档、不进界面、不进全量转储（转储只看公开成员）；生产与界面从不设它。设了它的整线解在 Notes 里多一句 <see cref="LineRunner.FaceHeatRevertNote"/>，
    ///   结果不会冒充生产口径。覆盖范围：只有读**这个算例对象**的路径（LineRunner.Run → RunOnce → PlateCurrentField）；
    ///   保温搜索等由设计另建算例的路径不带它（门不覆盖那几条路）。
    /// </summary>
    internal bool GateRevertFaceHeat;

    /// <summary>R48 M（2026-09-18，Fable 5.1）：量雅可比的前差步长 K（与保温搜索 InsulationSearch.Options.TubeJacobianDeltaK 同为 1.0）。</summary>
    public double JacobianDeltaK = 1.0;

    /// <summary>
    /// **无法兰基线**的两端管温缓存 `[段][0=左,1=右]`（空 = 由 LineRunner 自己算）。
    ///
    /// 基线只依赖**管几何 / 保温 / 控温点**，与法兰几何无关 ⇒ 外层搜索里
    /// 只需在「管壁或管保温变了」时重算一次。不缓存的话每次整线解要多跑 4 次基线，
    /// 阶梯搜索直接慢 5 倍。
    /// </summary>
    public double[][] BaselineRootC = Array.Empty<double[]>();

    /// <summary>
    /// ★★★★★ 外层耦合的**热启动**状态（2026-08-17）：`[段][0=左抽热W, 1=右抽热W,
    /// 2=左邻端温°C, 3=右邻端温°C]`。空 = 冷启动（抽热全 0）。
    ///
    /// 为什么需要：定尺寸器每一轮都在**几乎相同**的设计上重解整线，而外层耦合每次
    /// 都从「抽热 = 0」重新爬。环路增益 g ≈ 0.96 ⇒ 放大约 25 倍 ⇒ 冷启动动辄上百轮。
    /// 实测定尺寸一轮要 1–3 分钟，一个形状 26 轮就是**半小时到一小时**，
    /// 而形状搜索要跑十几个形状 —— 这直接决定了「APP 能不能自动改形状」是不是可用的功能。
    ///
    /// 相邻两轮的设计只差 0.1 mm 板厚 / 几 mm 保温 ⇒ 上一轮的不动点离这一轮**很近**。
    ///
    /// ⚠ 热启动**只改到达路径，不改不动点**：收敛判据仍是真残差 ‖G(x)−x‖，
    ///   它对起点无记忆。若热启动会改变答案，那说明不动点不唯一 —— 那是另一个病，
    ///   必须被看见，所以 `--selfcheck` 里有一条**冷/热启动必须给同一个解**的对账。
    /// </summary>
    public double[][] WarmStart = Array.Empty<double[]>();

    /// <summary>
    /// ★ R48 诊断钩子（2026-09-14，Opus 5；常驻数值把关人第十一轮要的「续跑」）：外层耦合**每轮**回调一次
    /// （轮号、本轮结果、步长 δ、真残差、剩余误差估计、放大系数、ω、Anderson 报告）。null = 不回调，生产逐位不变。
    /// 只给探针用：量「剩余误差估计」在共用接头慢模式上是否低估。不许在生产链路里挂它做判定。
    /// </summary>
    public Action<int, LineResult, double, double, double, double, double, string>? CoupleTrace;

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：**空管到温稳态**工况（无玻璃）。默认 false = 带玻璃稳态，逐位不变。
    ///
    /// 用户 2026-09-14：设备到温后、进玻璃前的空管保温「以稳态计算」—— 与带玻璃稳态**并列**的工况。
    /// ★ K 路（2026-09-15，Opus 5；审查 P3-4）：原句「全部判据都要过」**已不成立** —— 用户 2026-09-15：「空管时铂过热那条也不该按 5 ℃ 卡，只要J&lt;11即可」。
    ///   分工况判据已落地：两态各卡哪几条只看 <see cref="LineResult.RequiredByState"/>（空管态热侧、冷侧、管孔净流入降为参考量；场判不了两态都不算过，见 <see cref="LineResult.FieldUndeterminedReasons"/>）。
    /// 为 true 时 <see cref="LineRunner.Run"/> 的**段解与无法兰基线**用 Base 的克隆、把产量与管内玻璃换热置 0
    /// （判别只有 <see cref="SegmentSolver.IsEmptyTube"/> 一处），其余（法兰场解、判据）照常；结果 Notes 写明工况。
    /// 物理：管内玻璃换热那一项 hg·π·D 没了 ⇒ 管的线性化散热系数 hP 变小、接头导热的 K/W 变大（∝ 1/√(kA·hP)）。
    ///   电流升还是降要看玻璃在该段是**吸热还是给热**：玻璃比管热（下游段常见）时它在给管子热，拿掉玻璃反而要更多电流 ——
    ///   不许凭「少了一个散热项」就写「电流更低」，数见 R48EmptyTubeStateTests 的输出。
    ///
    /// ⚠ **与本工况无关的只有尺寸链**：设计电流（DesignCurrent 的管子准静态峰值，只读板件与 Base）、舌片厚、
    ///   硬判据「升温」与「法兰截面 J」的实际值 —— 两工况逐位相同（R48EmptyTubeGateTests 的双工况门守着）。
    ///   2026-09-14 Opus 5（R48 审查第 4 条）改口：此前这里写「升温链与本工况无关」，**不对** —— 下面三样取本次稳态场，随工况变：
    ///   「升温期法兰−管峰值」（两节点的参考电阻 QGen/I² 与参考温度取本次最热片的稳态值；
    ///     R48 G2 复审二 2026-09-15 Opus 5 改注：现为法兰节点发热／表面散热／流进铜排／管孔四项按本次最热片的稳态场标定，判据表那一行复核前暂不给数，标定仍随工况变）、
    ///   「升温到位用时（集总）」（段电流与法兰焦耳热取本次稳态，只在 CheckRamp 时有）、
    ///   「法兰截面 J」说明里的两节点对照电流（参考电阻取本次各片稳态）。空管结果里这三样的 Note 标明「取空管到温稳态场」。
    /// ⚠ 管强度与集总升温两条判据用的段参数**不置 0**：管强度的载荷沿用带玻璃的液柱与流动值，许用应力仍按段设定温度取 ——
    ///   空管时管根可能比设定高（B2 沿用生产设定时 HC3 A 端管根 1153.13 °C、设定 1050，高 103 K，deliverable\R48_空管稳态_空管_2026-09-14.txt），高温许用更低，所以**不能说偏保守**（2026-09-14 Opus 5，审查第 7 条改口）；
    ///   集总升温（RampSolver）本来不读玻璃。
    /// ⚠ 控温点就是 <see cref="SetpointC"/>，本类不改它；空管读哪一种控温点由 <see cref="DesignSpec.BuildCase"/> 的 emptyTubeSetpoint 定，记在 <see cref="EmptyTubeSetpointFrom"/>。
    /// ⚠ 空管才有的两条热路（R48 审查第 3 条提出）：
    ///   · 管腔内轴向辐射 —— R48 G3（2026-09-15，Opus 5）起**进模型**：段解的轴向导热与段间接头导度加一份等效 kA
    ///     （<see cref="SegmentSolver.CavityRadKA"/>／<see cref="SegmentSolver.NeighbourConductanceWPerK"/>，系数 <see cref="DesignInputs.TubeCavityRadKA1150WmPerK"/>，
    ///     默认暂取两档估计里的低档，两档之间没有先验依据取舍、未经实测 —— 空管结论两档并报，见 DesignInputs 注释）；无法兰基线走同一个 RunOnce，同样计入。
    ///     此前（2026-09-14）的空管结论是「不含这一项的模型结果」。⚠ 升温两节点模型（RampTwoNode）的管侧翅片导度没计入管腔辐射（交接 open issue）。
    ///   · 管口辐射散热 —— 用户定铂管两端封住 ⇒ 不计（整线两头的端部边界照旧只有法兰抽热）。
    /// ⚠ 端部额外保温的渐变形状按带玻璃的 hg 定（空管段写 DesignInputs.EndInsulShapeHGlass），硬件不随工况变。
    /// ⚠ 基线缓存 <see cref="BaselineRootC"/> 不记工况：同一个算例改了本位要清空缓存；把缓存在算例间传递的地方（定尺寸器、求解器）
    ///   接本工况时，缓存键要带上工况。
    /// </summary>
    public bool EmptyTube;

    /// <summary>
    /// ★ R48 审查第 1 条（2026-09-14，Opus 5）：本算例控温点的来源，**只作记录**（写进空管工况说明），控温点永远读 <see cref="SetpointC"/>。
    /// 默认 <see cref="EmptyTubeSetpoint.AsGiven"/> = SetpointC 原样；<see cref="DesignSpec.BuildCase"/> 按全线升温目标造空管算例时写 RampTarget。
    /// </summary>
    public EmptyTubeSetpoint EmptyTubeSetpointFrom = EmptyTubeSetpoint.AsGiven;

    /// <summary>其余物性、保温、电气、环境沿用 DesignInputs</summary>
    public DesignInputs Base = new();

    /// <summary>现状整线铂重基准 g（--geom 校核值），用于算省铂率</summary>
    public double BaselineMassG = 7141.0;

    public int SegmentCount => SetpointC.Length;
    public int FlangeCount => LineSolver.FlangeCount(SegmentCount);
}

public sealed class SegmentOut
{
    public string Name = "";
    public double SetpointC, CurrentA, PowerW, TubeJAPerMm2;
    /// <summary>
    /// 管根温度 °C 与温差 K。
    ///
    /// ⚠⚠ 2026-08-14 修的一个**贯穿全项目**的 bug：这两个量原本只取
    /// <c>SolveResult.TFlangeAC</c>（= tm[0]，**左端**）。而每段有**两片**法兰，
    /// 右端的 <c>TFlangeBC</c> 算了却从未被用过。
    /// 实测 HC3 两端是 1040.2 / 1034.0 °C，判据只报了对应 1040.2 的 +9.8 ——
    /// **另一端差 16 K，从来没被检查过**；且最后一片法兰（出口端片）拿到的还是
    /// HC3 的**左端**温度，完全是另一头。
    /// ⇒ 现在 <see cref="RootDeltaK"/> 取**两端中较差的那个**，两端值另存。
    /// </summary>
    public double TRootC, RootDeltaK, GlassInC, GlassOutC, MassG;
    /// <summary>左端（A）与右端（B）的管根温度 °C —— 两片不同的法兰各贴一端</summary>
    public double TRootAC, TRootBC;
    /// <summary>两端各自的管根温差 K（控温点 − 该端温度）</summary>
    public double RootDeltaAK, RootDeltaBK;

    /// <summary>
    /// **无法兰基线**下两端的管温 °C（同几何、同保温、同段间耦合，仅把法兰抽热置零）。
    ///
    /// ⚠⚠ 为什么必须有它（2026-08-14/15）：原来的 C2 判「管根温度偏离**本段控温点** ≤10 K」，
    /// 可共用法兰处的管温是由**两侧控温点**定的 —— 接上段间导热后实测 HC1|HC2 接头停在
    /// **1116 °C**（1150 与 1080 的中间），偏离本段控温点 34 K，**与法兰设计无关**。
    /// 用那个靶子做优化，等于让法兰去背控温点梯度的锅：我为此调了几小时法兰几何。
    ///
    /// ⇒ C2 的正确口径是**法兰造成的增量**：`基线温度 − 实际温度`。
    ///   控温点梯度是设计要的，不是缺陷；法兰挖的坑才是法兰的责任。
    /// </summary>
    public double BaseTRootAC = double.NaN, BaseTRootBC = double.NaN;
    /// <summary>法兰造成的**增量**温降 K（正 = 法兰把该端拉冷了）。两端取较差者。</summary>
    public double FlangeDipK = double.NaN;

    /// <summary>
    /// ★★ 两端**各自**的增量温降 K —— A 端贴第 i 片，B 端贴第 i+1 片。
    ///
    /// 病灶（2026-08-28）：<see cref="FlangeDipK"/> 是 <c>Math.Max(dA, dB)</c>，
    /// 两端算完就把较好的那个**扔了**。于是判据③ 说得出「有一片超了」，
    /// **说不出是哪一片** —— 而③ 恰恰是舌保温要治的那条，
    /// 不知道责任在哪片就没法逐片抬保温，只能四片一起抬，
    /// 而四片一起抬**实测解不出来**（--solve --seedprobe：舌保温顶到上界 20 mm）。
    ///
    /// ⚠ 与已修过的那个病是**同一个位置的另一半**：代码上面记着
    ///   「右端的 TFlangeBC 算了却从未被用过……另一端差 16 K，从来没被检查过」。
    ///   那次修的是「取两端较差者」（不再只看左端）；这次是「两端要各自留着」。
    ///
    /// ⇒ 第 j 片的③ 责任 = max(段 j 的 A 端, 段 j−1 的 B 端)，端片只有一侧。
    /// </summary>
    public double FlangeDipAK = double.NaN, FlangeDipBK = double.NaN;
    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：本段管的线性化散热系数 β（= hP，W/(m·K)，含管内玻璃换热一项）与热衰减长度 ℓt = √(kA/β) mm，
    /// 原样取自 SolveResult.BetaWPerMK / DecayLengthMm（此前算了没带出来）。空管工况比带玻璃少了玻璃那一项 —— 探针拿它对物理把关人的 hP 预测。
    /// 只报数，不进判据。
    /// </summary>
    public double BetaWPerMK = double.NaN, DecayLengthMm = double.NaN;
    /// <summary>R48 G3（2026-09-15，Opus 5）：本段段内用的管腔轴向辐射等效 kA W·m/K（取自 SolveResult.CavityRadKAWmPerK；带玻璃 0）。只报数，不进判据。</summary>
    public double CavityRadKAWmPerK = double.NaN;
    /// <summary>
    /// ★ R48 L（2026-09-16，Opus 5）：本段两端各自的**段间端温不动点停机放大倍数** 1 + ℓt/Δx
    /// （原样取自 SolveResult.EndAmpA/EndAmpB；口径见 <see cref="SegmentSolver.EndTempFixedPointAt"/>）。
    /// ⚠ 这两个**进判定**：外层停机的放大倍数由 <see cref="LineRunner.EndTempFixedPointAmpOf"/> 从它们取最大值（收缩最慢的那个端说了算）。
    /// </summary>
    public double EndAmpA = double.NaN, EndAmpB = double.NaN;
    /// <summary>R48 L（2026-09-16，Opus 5）：两端各自的 ℓt mm 与 β′ W/(m·K)（按该端管根温度取）、本段节点间距 Δx mm。只报数。</summary>
    public double EndDecayLengthAMm = double.NaN, EndDecayLengthBMm = double.NaN,
                  EndBetaAWPerMK = double.NaN, EndBetaBWPerMK = double.NaN,
                  NodeSpacingMm = double.NaN;
    /// <summary>
    /// ★ R48 审查第 5 条（2026-09-14，Opus 5）：本段端部额外保温的厚度分布 mm（自端部起逐子区间，两端对称；没有端部额外保温 = 空数组），
    /// 原样取自 <see cref="SegmentSolver.EndInsulExtraProfileMm"/>（段解用的同一份参数）。它是硬件，空管与带玻璃应相同（只差损失表样条插值）——
    /// 门拿它核 LineRunner 在空管段写了带玻璃的形状 hg；用户 2026-09-14 要报告给保温厚度分布，将来印分布读它。只报数，不进判据。
    /// </summary>
    public double[] EndInsulExtraProfileMm = Array.Empty<double>();
    /// <summary>
    /// ★ R48（2026-09-15，Opus 5；数值把关人第十四轮）：本段管解最高温超出管散热表上限（<see cref="SolveResult.LossTableExceeded"/> 原样带出）⇒
    /// 超出的那段散热被钳住，管温场不是这个设计的场 ⇒ LineRunner.MarkUndeterminedIfTubeLossTableExceeded 把吃管温的判据判不了。
    /// </summary>
    public bool TubeLossTableExceeded;
    /// <summary>本段管散热表上限 °C（<see cref="SolveResult.LossTableHiC"/>）与管解最高温 °C。</summary>
    public double TubeLossTableHiC = double.NaN, TubeTMaxC = double.NaN;
    public double[] X = Array.Empty<double>();
    public double[] TMetal = Array.Empty<double>();
    public double[] TGlass = Array.Empty<double>();
}

public sealed class FlangeOut
{
    public string Name = "";
    public bool Shared;
    public double CurrentA, MassG, JMaxAPerMm2, Phi, QFromTubeW, TMaxC, TMinC, TTabEndC;
    /// <summary>该片实际用的铜排热导 W/K（−1 = 走定温边界）。由该片电流算：G = k_Cu·(I/J许用)/L。</summary>
    public double BusGWPerK = -1;
    /// <summary>按**载流**需要的铜排截面 mm²（A = I/J许用）—— 不循环，可在解前定。</summary>
    public double BusSectionForCurrentMm2;
    /// <summary>按**导热**需要的铜排截面 mm²（A = Q·L/(k·ΔT)）—— 要解完才知道，用来对账。</summary>
    public double BusSectionForHeatMm2;
    /// <summary>自身焦耳热与自身散热 W —— Φ = QGen/QLoss 的两个分子分母，判 §4.2k 时要看得见</summary>
    public double QGenW, QLossW;

    /// <summary>R48（2026-09-14，Opus 5）：圆盘区/舌片区这一次按哪条规则分的（原样带进 ②″ 的说明）。</summary>
    public string DiscZoneRule = "";
    /// <summary>R48 续（2026-09-14，Opus 5）：保温按哪条规则划（原样带进 ②″ 的说明）。</summary>
    public string InsulRule = "";

    /// <summary>R48（2026-09-14，Opus 5）：舌片区峰值的位置（见 ShellThermalResult.TabMaxXMm 的注释）。</summary>
    public double TabMaxXMm = double.NaN, TabMaxZMm = double.NaN,
                  TabMaxRMm = double.NaN, TabMaxJAPerMm2 = double.NaN,
                  TabMaxThickMm = double.NaN;
    /// <summary>
    /// 从舌片末端流进铜排夹的热 W —— **铜排冷却要按这个数选型**。
    ///
    /// 由能量恒等式取：Q_夹持 = Q_发热 + Q_从管吸热 − Q_表面散热。
    /// 不逐面累加（§7：阶梯状边界会漏配对，实测偏小 14 %）。
    /// 夹持温度 &lt; 0（无夹冷）时为 0。
    /// </summary>
    public double QClampW;
    /// <summary>能量闭合残差 W —— 应接近 0，显著非零说明场解有问题</summary>
    public double EnergyResidualW;
    /// <summary>
    /// ★ R48 G2 复审（2026-09-15 Opus 5）：本片最后一次热解的逐格焦耳热 W、逐格表面散热 W（两面）与被边界钉住的格
    /// （原样取自 ShellThermalResult.CellGenW／CellLossW／BoundaryCell）。升温两节点在稳态场上逐项标定法兰节点时要它（RampTwoNode.CalibrateNode）。
    /// </summary>
    public double[] CellGenW = Array.Empty<double>(), CellLossW = Array.Empty<double>();
    public bool[] FieldBoundaryCell = Array.Empty<bool>();
    /// <summary>
    /// ★ 分区能量账（圆盘 / 舌片，以切点为界）—— 见 <see cref="ShellThermalResult.QGenDiscW"/>。
    /// 整片一个「发热 &lt; 散热」指不出该动哪一段，而两段的杠杆方向相反。
    /// </summary>
    public double QGenDiscW, QLossDiscW, QGenTabW, QLossTabW, TDiscMeanC, TTabMeanC;
    /// <summary>分区峰值温度 —— 判据②要用 <see cref="TDiscMaxC"/>，见 ShellThermalResult 同名注释</summary>
    public double TDiscMaxC, TTabMaxC;
    /// <summary>圆盘峰值的**位置与局部电流**，见 ShellThermalResult 同名注释（判据 ②″ 的病灶定位）</summary>
    /// <summary>局部热稳定的最小裕度与落点，见 <see cref="ShellThermalResult.LocalStabMargin"/></summary>
    public double LocalStabMargin = double.NaN, LocalStabRMm = double.NaN,
                  LocalStabTempC = double.NaN, LocalStabJAPerMm2 = double.NaN, LocalStabLatLenMm = double.NaN;
    public bool LocalStabOnTab;
    /// <summary>
    /// ★★★★★ 决 103（2026-09-24；决 99 选项 A）：本片热解的**局部热稳定全格精算**（惰性：只在整线收尾对终局那一份场调用，见 <see cref="LineRunner.ApplyLocalStabFullGrid"/>）。
    /// null = 没带（手造的片、改回口径不调它）。原样取自 <see cref="ShellThermalResult.LocalStabFullGrid"/>。
    /// </summary>
    /// ⚠ internal（不是 public）：它是一段闭包，不是结果里的数 —— 反射转储（R48LineDumpTests.Dump 一类）只读公开成员，不把它当成结果的一部分。
    internal Func<LocalStabScan>? LocalStabFullGrid;
    /// <summary>决 103：全格精算之前（前 12 名口径）报出的最小裕度与落点 r；NaN = 本片没做全格精算。对照「报出值乐观几倍」用。</summary>
    public double LocalStabTop12Margin = double.NaN, LocalStabTop12RMm = double.NaN;
    /// <summary>决 103：本片全格精算实际评过的格数（NaN 场、定温格不在内）与所花秒数（机时量，不进判读）；0 = 没做。</summary>
    public int LocalStabFullCells;
    public double LocalStabFullSec;
    /// <summary>决 103：本片整片热稳定裕度（逐片评，<see cref="LineRunner.FlangeLumped"/> 的 PerPlate 原样带出；判不了 = NaN）。求解器逐片裕度读它。</summary>
    public double FlangeStabMargin = double.NaN;

    /// <summary>
    /// ★★ 这一片的**场解（电位 + 温度）收敛了没有**（2026-08-29 补）。
    ///
    /// 病灶：三条铁律第 ③ 条是「判据只能过 / 不过 / **判不了**，判不了不算过」，
    /// 但这条在**线性解这一层根本没有执行**：
    ///   · <c>ShellCurrent.Converged</c> —— 加了字段却**没有任何人读**；
    ///   · <c>ShellThermal.Converged</c> —— 有人读，但**只记一条 Note，不拦结果**。
    /// ⇒ 场没解到位时，判据照样报出一个数，而那个数**看起来完全正常**。
    ///
    /// 现在：只要这一片的任一场没收敛，凡是**吃这一片的场**的判据一律标成
    /// <see cref="ConstraintOut.Undetermined"/> —— 而 <see cref="LineResult.AllOk"/>
    /// 把「判不了」当阻断，铁律于是真的生效。
    /// </summary>
    public bool FieldsConverged = true;

    /// <summary>这一片的设计电流 A（20 °C/h 空管升温峰值，共用片矢量合成；用户 2026-09-08 设计因果链第 ① 步）。</summary>
    public double DesignCurrentA = double.NaN;
    /// <summary>这一片最紧截面的电流密度 A/mm²（= 设计电流 ÷ 必经截面积，闭式）与那个截面在哪。</summary>
    public double SectionJAPerMm2 = double.NaN;
    public string SectionJWhere = "";

    /// <summary>
    /// ★ **这一片**越过了铂熔点（2026-09-08）。整线位 <see cref="LineResult.OverMelt"/> 只说
    ///   「有一片熔了」，说不出是哪一片；而求解器要**逐片**把下角抬出熔化区，必须知道抬哪片。
    ///   ⚠ 它是布尔，不是温度：熔化区里峰值温度的**大小**不可引用（S1，拟合外推），
    ///     只有「越没越过熔点」这个方向可信。读它的人不许顺手去读温度数值。
    /// </summary>
    public bool OverMelt;

    /// <summary>温度场停机时的**真残差**（相对）—— 诊断用；步长不是残差。</summary>
    public double FieldResidualRel = double.NaN;

    /// <summary>诊断：温度场外层轮数 / 内层 CG 轮数。</summary>
    public int FieldOuterIters, FieldInnerIters;

    /// <summary>没收敛时说清楚是哪个场、残差多少。空 = 收敛了。</summary>
    public string FieldNote = "";

    /// <summary>
    /// R47 B：图纸路径上这一片的保温分界／舌盘分界**从哪来**（给判据附注）。
    /// 空 = 解析路径。<see cref="InsulBoundaryUndetermined"/> = 推不出切点 ⇒ ②′ 与 ③ 判成无法判定。
    /// </summary>
    public string InsulBoundaryNote = "";
    public bool InsulBoundaryUndetermined;
    /// <summary>
    /// ★ R48（2026-09-15，Opus 5）：本片**压接段盖到了管孔**（<see cref="ShellMesh.ClampCoversHole"/> 原样带出）⇒ 吃法兰场的判据与参考行判不了，
    /// 与 <see cref="InsulBoundaryUndetermined"/> 同一种处置（名单 <see cref="LineRunner.DependsOnFlangeFields"/>）。
    /// </summary>
    public bool ClampCoversHole;
    /// <summary>
    /// ★ R48（2026-09-15，Opus 5；审查意见 minor「同一种病只修了一半」）：本片**压接段伸进了圆盘**（<see cref="ShellMesh.ClampIntoDisc"/> 原样带出）——
    /// 舌片比压接长还短，这块板几何上不成立 ⇒ 与 <see cref="ClampCoversHole"/> 同一种处置（LineRunner.MarkUndeterminedIfClampIntoDisc，名单同 <see cref="LineRunner.DependsOnFlangeFields"/>）。
    /// </summary>
    public bool ClampIntoDisc;
    /// <summary>★ R48（2026-09-15，Opus 5）：本片网格与热解实际生效的配方（结构化指纹，见 <see cref="PtOptimize.Core.MeshRecipe"/>／<see cref="PtOptimize.Core.ThermalRecipe"/>）。</summary>
    public MeshRecipe? MeshRecipe;
    public ThermalRecipe? ThermalRecipe;
    /// <summary>R47 F：管孔定温环吃到孔边以外多少 mm（TagHole 面最大半径 − 孔半径），与焊脚以外被钉住的边界长 mm。</summary>
    public double HoleTagOverMm = double.NaN, HoleTagBeyondWeldMm = double.NaN;

    public double DiscMaxXMm = double.NaN, DiscMaxZMm = double.NaN,
                  DiscMaxRMm = double.NaN, DiscMaxJAPerMm2 = double.NaN,
                  DiscMaxThickMm = double.NaN;
    /// <summary>本片贴着的管根温度 °C（管孔定温边界）。TMaxC − TRootC &gt; 0 即「法兰比管热」</summary>
    public double TRootC;
    public double AreaMm2, VolumeMm3;
    public int CellCount;
    public ShellMesh? Mesh;
    public double[] JField = Array.Empty<double>();
    public double[] TField = Array.Empty<double>();
    /// <summary>
    /// 单元电位（归一化 0…1，舌端 1、管孔 0）。R12/R13（2026-09-09）要从**最新收敛的场**取当地电流**方向**
    /// （长椭圆的长轴顺着电流），而 |J| 没有方向 ⇒ 把电位场也留下来，方向 = −∇V。
    /// </summary>
    public double[] VField = Array.Empty<double>();
    /// <summary>
    /// 各级的**局部最高温** °C（与 LineCase.LevelThicknessMm 同序）。
    /// 逐级定厚要靠它：知道是**哪一级**在过热，才知道该加厚哪一级。
    /// </summary>
    public double[] LevelTMaxC = Array.Empty<double>();
    /// <summary>各级的当前厚度 mm（已含标度），供界面与报告显示</summary>
    public double[] LevelThickMm = Array.Empty<double>();
    public string Source = "";        // 用了哪个 .3dm
}

/// <summary>
/// 判据的**分级**。HANDOVER §4.2k 之后三者不再等价：
/// 硬安全线越了就是烧断，目标越了只是不够好，参考量根本不参与判定。
/// 早先把三者混在一张表里，导致「J 越限」与「Φ&gt;1」被同等对待 —— 前者可能只是限值本身存疑
/// （§4.2i：J_allow=10 的物理依据未定），后者是确凿的失效模式。
/// </summary>
public enum CheckKind
{
    /// <summary>硬安全线：越界即失效，没有折衷余地</summary>
    HardSafety,
    /// <summary>设计目标：越界是方案不够好，可与省铂权衡</summary>
    Target,
    /// <summary>参考量：只报数，Ok 恒为 true</summary>
    Reference
}

public sealed class ConstraintOut
{
    public string Name = "", Where = "", Unit = "", Note = "";
    public double Actual, Limit;
    public bool Ok;
    /// <summary>true = 实际值须 ≤ 限值；false = 须 ≥ 限值</summary>
    public bool LessIsBetter = true;
    public CheckKind Kind = CheckKind.Target;
    /// <summary>数据不足以判定（如纯铂在 1100 °C 以下无持久强度实测，见 §6 待补 ③）</summary>
    public bool Undetermined;
    /// <summary>
    /// ★ R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「界面与报告跟升温那一行的状态对不上」）：参考量**暂不给数**（模型待复核，不许引用）——
    /// <see cref="Actual"/> 为 NaN 不是「达不到」也不是「算不出」。显示层（判据表格、安装报告、命令行判据表）先看本位印「暂不给数」。
    /// 现在只有「升温期法兰−管峰值」一行置它（LineRunner.Judge）。
    /// </summary>
    public bool Withheld;

    /// <summary>
    /// 本条判据的**裕度 %**。正 = 还有余量，负 = 越限。方向性判据（限 0）返回 NaN。
    ///
    /// ★★★★★ **必须看 <see cref="LessIsBetter"/>**（2026-08-23 修）。
    ///
    /// 在此之前，同一个公式 `(限−实)/|限|` 在**四处**各写了一遍
    /// （DesignSpec.Margin、ShapeReview 两处、整线设计页的判据表），
    /// 四处都没看方向 ⇒ 对「须 ≥ 限」的判据**符号是反的**。
    ///
    /// 实际后果（.3dm 那条路上现形）：⑤ 舌片自由段 114.8 / 100 是**通过且富余 14.8**，
    /// 而判据表把它显示成「**超 15 %**」—— 裕度列说越限、判定列打 ✓，同一行自相矛盾。
    /// 设计记录上看不出来，是因为舌长按「切点+压接+自由段下界」**算出来**、
    /// ⑤ 恰好贴着 100 ⇒ 裕度 0 %，符号错不错都是 0。**这个 bug 一直躲在那个巧合后面。**
    ///
    /// ⇒ 裕度只有这一处来源。要显示裕度就读它，别再各写一遍。
    /// </summary>
    public double MarginPct => System.Math.Abs(Limit) < 1e-9 ? double.NaN
        : (LessIsBetter ? Limit - Actual : Actual - Limit) / System.Math.Abs(Limit) * 100.0;
}

public sealed class LineResult
{
    /// <summary>
    /// **各段端部实际扣掉的抽热总和** W（Σ 段 i 的 L + R）。0 = 本次没走外层耦合。
    ///
    /// ★ 为什么要留这个数（2026-08-28 第一性原理通查）：
    ///   <c>QFromTubeW</c> 是**一片**法兰经整圈管孔抽走的**总量**（ShellThermal 对孔单元累加）。
    ///   而外层耦合把 <c>targetLR[i] = (Flanges[i].Q, Flanges[i+1].Q)</c> 挂到段 i 两端 ——
    ///   于是**内部共用片**同时是「段 j−1 的右端」与「段 j 的左端」，全额被扣**两次**。
    ///   管子实际失去的是 Q₀ + 2ΣQ内 + Q_n，而法兰实际收到的是 ΣQ。
    ///   ⇒ 差额 = Σ内部片的 Q。此前**没有任何一处在对账**，所以谁也没发现。
    ///   现在做成一条常驻参考判据（Key.HeatBalance），让它自己说话。
    /// </summary>
    public double DrawAppliedW;

    /// <summary>入口片壳网格的单元数 —— 网格无关性验证的横轴，算了就要报得出来。</summary>
    public int MeshCells;
    /// <summary>R47 复修 M12：这次解用的中带（导航）网格尺寸 mm —— 界面说「此前显示的是导航网格上的数」要说真实的数，不写死 2。</summary>
    public double MeshFineMm;

    public SegmentOut[] Segments = Array.Empty<SegmentOut>();
    public FlangeOut[] Flanges = Array.Empty<FlangeOut>();

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5）：配方汇总 —— 逐片与生产配方常量（<see cref="FlangeMesher.ProductionMeshRule"/>／<see cref="ShellThermal.ProductionThermalRule"/>）比，
    /// 不同的片列出来（片名 + 哪一份 + 实际配方）。空 = 每一片网格与热解都是生产配方。没带配方（非生产生成器）的片也列出来，不许当成一样。
    /// </summary>
    public string[] RecipeDeviations => Flanges
        .Where(f => f is not null)
        .SelectMany(f => new[]
        {
            f.MeshRecipe is null ? $"{f.Name}：网格没有配方记录"
              : f.MeshRecipe.Rule != FlangeMesher.ProductionMeshRule ? $"{f.Name}：网格配方与生产不同 —— {f.MeshRecipe.Describe()}" : null,
            f.ThermalRecipe is null ? $"{f.Name}：热解没有配方记录"
              : f.ThermalRecipe.Rule != ShellThermal.ProductionThermalRule ? $"{f.Name}：热解配方与生产不同 —— {f.ThermalRecipe.Describe()}" : null,
        })
        .Where(s => s is not null).Select(s => s!).ToArray();

    /// <summary>R48（2026-09-15，Opus 5）：逐片配方的一行汇总（证据文件头用）；各片相同就只印一次。</summary>
    public string RecipeSummary()
    {
        var mesh = Flanges.Where(f => f?.MeshRecipe is not null).Select(f => f.MeshRecipe!.Rule.Describe()).Distinct().ToArray();
        var th = Flanges.Where(f => f?.ThermalRecipe is not null).Select(f => f.ThermalRecipe!.Describe()).Distinct().ToArray();
        return $"网格：{(mesh.Length == 0 ? "无记录" : string.Join(" ／ ", mesh))}；热解：{(th.Length == 0 ? "无记录" : string.Join(" ／ ", th))}";
    }
    public ConstraintOut[] Checks = Array.Empty<ConstraintOut>();

    /// <summary>
    /// ★ U 路（2026-09-18，Opus 5）：**每片舌保温的可行窗口**（<see cref="PtOptimize.Core.InsulWindow.Measure"/> 量出来的）。
    /// null = 这一次没量 —— 它一点要一次整线解，只在终验跑一次（参数表「终验时量每片舌保温的可行窗口」），优化循环里不跑。
    /// ⚠ 挂在这里只是**带着走**（判据表页、安装报告都从这一份读）；<see cref="LineRunner.Run"/> 自己一个字都不写它。
    /// </summary>
    public PtOptimize.Core.InsulWindow.Result? TabInsulWindow;

    public double TubeMassG, FlangeMassG, TotalMassG, BaselineMassG, SavingPct;
    public double GlassDropModelK, GlassDropMeasuredK;
    public readonly List<string> Notes = new();

    /// <summary>
    /// R48 L（2026-09-17，Opus 5）：升温全程（准静态轨迹）结果。null = 没跑。
    /// 门（硬）：场无效 ⇒ 判不了；管 J 或截面 J 超限 ⇒ 不过；否则过。
    /// 用户 2026-09-16/17：升温期不卡 ±5 K；没有膨胀判据，只报告膨胀量；δ 退役。
    /// </summary>
    public RampSweepResult? RampSweep;

    /// <summary>
    /// ★★★★★ 2026-09-18，Opus 5：**第三关「空管到温」的整线结果**（null = 这一次没跑）。
    ///
    /// 用户 2026-09-15：空管到温稳态只卡电流密度与场的有效性（热侧、冷侧、管孔净流入降参考量）——
    /// 那套分工况判据早就在 <see cref="RequiredFor"/> 里了，可是**生产链上从来没人造过空管算例**：
    /// 界面与安装报告只跑带玻璃那一关。判得了而没人判 = 工程师点不到。
    /// 由 <see cref="FinalCheck.Run"/> 在终验时跑一次挂上来；<see cref="LineRunner"/> 自己一个字都不写它。
    /// ⚠ 挂在带玻璃那份结果上只是**带着走**（结论块与安装报告都从这一份读）；
    ///   空管结果自己的这个字段恒为 null，不会套娃。
    /// </summary>
    public LineResult? EmptyTubeSteady;

    /// <summary>三关各自的耗时秒（**机时量**，不进判读；没跑的那一关是 NaN）。2026-09-18，Opus 5。</summary>
    public double RampSeconds = double.NaN, EmptyTubeSeconds = double.NaN;

    /// <summary>
    /// 三关里**没跑的那几关**照实登记（"升温全程：参数表里关掉了" 之类）。空 = 该跑的都跑了。
    /// ⚠ 没跑 ≠ 过：结论块与安装报告印的是这句话，不是「过」。2026-09-18，Opus 5。
    /// </summary>
    public string[] ThreeStateSkipped = Array.Empty<string>();

    public bool Ok = true;
    /// <summary>段↔法兰外层耦合是否收敛。**为 false 时表内所有数值一律不可引用。**</summary>
    public bool Converged;

    /// <summary>
    /// R48 续（2026-09-14，Opus 5）：**外层耦合停机时到不动点的剩余误差估计** K（= 最后一步步长 × 放大 r/(1−r) 或已知最坏 25）。
    /// NaN = 这次没走外层耦合。加密复核逐档印它：判据的变化量若小于它，那次「在摆」与耦合停机噪声分不开。
    /// </summary>
    public double CoupleRemainK = double.NaN;

    /// <summary>
    /// R48 L（2026-09-17，Opus 5）：**外层耦合实际走了几轮**（收敛时 = 收敛那一轮的序号；没收敛 = 轮数上限）。0 = 这次没走外层耦合。
    /// 收紧停机容差要如实报成本，成本就是这个数与耗时 —— 只活在说明文字里的数没法并列成表。
    /// </summary>
    public int CoupleRounds;

    /// <summary>
    /// R48 L（2026-09-17，Opus 5）：**这次停机实际用的容差 K**（<see cref="LineRunner.CoupleTolKFor"/> 当场算出来的那一个；
    /// 判据裕度小的时候它比 <see cref="LineCase.CoupleTolK"/> 还细）。NaN = 这次没走外层耦合。
    /// ⚠ 报告里引用「剩余误差估计」时必须同时给它 —— 不然读的人不知道那个数是对着什么停下来的。
    /// </summary>
    public double CoupleTolKUsed = double.NaN;

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**认证误差 K** = 放大 × 停机残差（步长那支与真残差那支取大，即停机时的 <see cref="CoupleRemainK"/>）。
    /// 只在**收敛**时有数；未收敛 NaN（那时表内每个数都不可引用，谈不上认证）。
    /// 下游（门在下游）：Solver 格点判决（硬安全线裕度小于它 ⇒ 判不了那么细，往保守方向再走一格）、
    /// <see cref="LineRunner.MarkUndeterminedIfInsideCertErr"/>（整线结果里裕度小于它的硬安全线标成判不了）、三关报告、证据头。
    /// 一份来源：这里只读 <see cref="CoupleRemainK"/>，不另存一个数。
    /// </summary>
    public double CertErrK => Converged ? CoupleRemainK : double.NaN;

    /// <summary>R48 M（2026-09-18，Fable 5.1）：停机时用的放大倍数（= max(闭式 × 1.1, 实测雅可比)）与两个来源，报告要能指回是哪个赢了。NaN = 这次没走外层耦合。</summary>
    public double CoupleAmpUsed = double.NaN, CoupleAmpClosed = double.NaN, CoupleAmpJacobian = double.NaN;

    /// <summary>R48 M（2026-09-18，Fable 5.1）：量雅可比放大花的秒数（成本要如实报；0 = 本次读缓存或没量）。</summary>
    public double JacobianAmpSec;

    /// <summary>
    /// ★ 本次解**越过了铂熔点** ⇒ <see cref="Ok"/> 为 false（2026-09-07 A）。
    ///   单独立一个位而不是让调用方去 match 讯息文字：
    ///   「熔化」与别的失败**处置完全不同** —— 别的失败是「算不出来」，
    ///   熔化是**方向信息**：这一处太薄了，加厚就能走出去。
    ///   定尺寸器靠它把「没解」变成「往加厚那边走」，而不是拿到 NaN 卡死。
    /// ★ 2026-09-15 Opus 5：有段的管温超出管表面散热表上限（<see cref="SegmentOut.TubeLossTableExceeded"/>）时法兰越过熔点**不置这一位**
    ///   —— 那时的熔化可能是管表钳住造出来的，熔不熔判不了；<see cref="Ok"/> 照样为 false，<see cref="Message"/> 说「熔不熔判不了」。
    /// </summary>
    public bool OverMelt;
    public string Message = "";

    // ────────────────────────────────────────────────────────────────
    // ★★★★ 判据的**唯一来源**（2026-08-15）
    //
    // 此前判据定义散在三处：`Judge`、定尺寸器的控制律、汇总表的判定逻辑 ——
    // 同一件事写三遍。改口径时改了三次、每次都漏一处，产生
    // 「判据表说过、汇总表说不过」这种自相矛盾且**不报错**的输出（连错四次）。
    // ⇒ 现在 Judge 出结果，**其余一律只读下面这几个访问器**，不得自行重算。
    // ────────────────────────────────────────────────────────────────

    /// <summary>判据名前缀常量 —— 引用判据只准用它们，不准写字符串字面量</summary>
    public static class Key
    {
        public const string Ramp = "① 升温";
        /// <summary>参考量：集总模型（含法兰质量与自热）算的升温到位用时 h（R20 前它是硬判据 ①）。</summary>
        public const string RampHours = "· 升温到位用时（集总）";
        public const string NetFlux = "②′管孔净流入";      // B：热流方向本身

        // ★★★★★ R48 B（2026-09-14 Opus 5）：**热侧、冷侧两条判据的基准换成热偶读数**（用户 2026-09-14 定：
        //   控温热偶在段中点、误差 5 ℃；「10 ℃ 是上下各 5 ℃」；共用法兰处基准取两侧热偶读数的对数平均）。
        //   物理把关人核过：基准只许用**设定值**算出的对数平均（LineSolver.ThermocoupleReferenceC），
        //   不许用模型算的交界管温 —— 那个温度会被设计变量挪走，拿它当基准等于让靶跟着箭跑。
        //
        //   ⚠⚠ **代号不换主人**（2026-09-14 Opus 5 复审改）：新两条用**新代号** ⑦（热侧）／⑧（冷侧）；
        //     「②″」「③」仍然、而且永远指旧的那两条（圆盘区最高温 − 管温／法兰增量温降），只是降为参考量、名字带「（旧判法）」。
        //     第一版曾让 ②″／③ 改指新判据 —— 那样 HANDOVER、deliverable、存档判词与命令行历史输出里的每一个 ②″／③ 都成了歧义，
        //     按代号前缀取值的地方（R47NavGridInstrumentTests、TabInsulPerPlateTests 的 StartsWith("③")）会悄悄换量而不报错。
        //     代号只给命令行与开发文档用（界面一律走 Criteria.Plain 剥掉），新代号的代价只是 Criteria.CodeChars 多两个字。
        //   ⚠ 「旧判法」而不写「旧口径」：这两个名字会上界面，「口径」是界面不该出现的内部词（ShellThermal 那条规矩，物理把关人 2026-09-14）。
        /// <summary>热侧（硬安全线）：第 j 片 max(盘峰, 舌区峰, 该接头管根较热端) − 热偶读数基准 ≤ <see cref="LineCase.HotOverTcMaxK"/>。</summary>
        public const string HotOverTc = "⑦ 最热铂高出热偶读数";
        /// <summary>冷侧（硬安全线）：第 j 片 热偶读数基准 − 该接头管根较冷端 ≤ <see cref="LineCase.ColdUnderTcMaxK"/>。</summary>
        public const string ColdUnderTc = "⑧ 管根低于热偶读数";
        /// <summary>参考量（2026-09-14 前是硬安全线 ②″，代号不变）：圆盘区最高温 − 贴着的管根温度。旧判法，不卡交付，留作对照。</summary>
        public const string DiscTemp = "②″圆盘区最高温 − 管温（旧判法）";     // C：贴管子那一圈
        /// <summary>参考量（2026-09-14 前是目标 ③，代号不变）：无法兰基线 − 实际管根温度。旧判法，不卡交付，留作对照。</summary>
        public const string FlangeDip = "③ 法兰增量温降（旧判法）";    // 法兰挖的坑

        // ★ 2026-08-20 补三条。它们一直都是判据（都在 Judge 里、都是硬安全线），
        //   只是此前没人用常量引用过 ⇒ 常量表缺了它们。界面门禁（UI/Flow.cs 的 GateSpec）
        //   要按名字读这三条，而门禁**只准用常量** —— 判据改名时编译期就断，
        //   而不是门禁悄悄永远放行。
        public const string FreeTab = "⑤ 舌片自由段";        // 现场铜排装得下吗（几何闭式）
        public const string DiscCover = "⑥ 圆盘盖得住管孔";   // 盘半径 − 管孔半径 − 焊脚（几何闭式）
        /// <summary>
        /// ★ 2026-09-17 Opus 5（用户当日现场限制）：**接合区的保温缠不缠得出来** —— 管–盘接合区
        /// （圆盘靠管孔的内环带）的**圆盘保温**厚度对照 20 圈 × 每圈 0.5 mm = 10 mm。
        /// ★ 2026-09-18 Opus 5 改回只卡圆盘：管保温与舌板保温**不进清单、不设上限**（用户原话「其它地方(舌板与管)好缠绕」）。
        /// ★★ 2026-09-18 Opus 5 **降为参考行**（用户当日「还是只给材质保温厚度方案就行」）：照常算、照常印圈数，
        ///    **不卡交付**（不进 AllOk／HardOk）；怎么包由现场定，超 20 圈时报告提示改用预制保温块。
        /// 闭式、纯输入，实现与参考线都只在 <see cref="WrapLimits"/> 一处。
        /// **不带判据代号** —— 新判据一律用全名（界面不许出现代号，用户 2026-08-30）。
        /// </summary>
        public const string WrapTurns = "接合区保温缠得出来";
        public const string TubeJ = "管 J";
        /// <summary>法兰**截面**电流密度 = 设计电流 ÷ 必经截面积（用户 2026-09-08 设计因果链第 ④ 步，全体 &lt; 11）。</summary>
        public const string SectionJ = "法兰截面 J";
        /// <summary>整片热稳定：dQ_散热/dT ÷ dP_发热/dT，须 &gt; 1</summary>
        public const string FlangeStab = "· 整片热稳定";
        /// <summary>局部热稳定：J_stab ÷ J_实际（圆盘峰值点），须 &gt; 1</summary>
        public const string LocalStab = "· 局部热稳定";                   // 管电流密度上限（≠「· 法兰 J_max」那条参考量）
        /// <summary>现场升温（温控 20 K/h）下「法兰温度 − 管温」的全程最大值 K</summary>
        public const string RampField = "· 升温期法兰−管峰值";
        public const string HeatBalance = "· 管↔法兰热收支";

        // ★ 2026-08-29 补：这几条一直在输出里露面，却没有常量 ⇒ 代号对照表
        //   （<see cref="Criteria"/>）没法从判据自己的名字派生，只能另抄一份名字。
        //   「同一个名字两处来源」正是本仓库最常见的病，所以先补常量再建表。
        /// <summary>④ 管强度利用率 —— 参考量（Pt 持久强度实测区间外时判不了）</summary>
        public const string TubeStrength = "④ 管强度利用率";
        /// <summary>· ② 法兰最高温 − 管温（整片，含舌片）—— 参考量</summary>
        public const string FlangeTopTemp = "· ② 法兰最高温";
        /// <summary>· 偏离本段控温点 —— 参考量（由控温点梯度决定，法兰管不着）</summary>
        public const string SetpointDrift = "· 偏离本段控温点";
        /// <summary>· 法兰自给率 Φ_max —— 参考量</summary>
        public const string SelfSupply = "· 法兰自给率";
        /// <summary>· 法兰 J_max —— 参考量（≠「· 局部热稳定」那条）</summary>
        public const string FlangeJ = "· 法兰 J_max";
        /// <summary>· 法兰热平衡残差 —— 参考量，应接近 0</summary>
        public const string HeatResidual = "· 法兰热平衡残差";
        /// <summary>· 玻璃温降 vs 实测 —— 参考量</summary>
        public const string GlassDrop = "· 玻璃温降";

        // ★★★★★ 决 103（业主 2026-09-24，判据换向）：带玻璃稳态卡交付的热侧、冷侧换成下面两条（**全名，不带代号**，界面不许出现代号）。
        //   ⑦／⑧／②′ 三条照算照印、降为参考（常量不动 —— 按代号前缀取值的地方与历史输出不换主人）。
        //   ⚠ 名字不许与现有任何一条互为前缀（Find 按前缀取）：「法兰最热处…」与「法兰截面 J」在第 3 字分开；「管接触处…」与「管 J」在第 2 字分开。
        /// <summary>热侧（决 103，带玻璃稳态硬判据）：第 j 片 max(该片法兰温度场) − 该片管接触处温度（模型算的管根接触温度）≤ <see cref="LineCase.HotOverContactMaxK"/>（10 K）。</summary>
        public const string HotOverContact = "法兰最热处高出管接触处温度";
        /// <summary>冷侧（决 103，带玻璃稳态硬判据）：第 j 片管孔处由管流入法兰的净热流（= FlangeOut.QFromTubeW，正 = 管 → 法兰）≤ 0 W；法兰不得拉低管在接触处的温度，不给预算。</summary>
        public const string TubeToFlangeHeat = "管接触处流入法兰的净热流";
        /// <summary>决 103：管 J 原许用值（08-15 现场 12）那一行留作对照（参考量；卡交付的「管 J」限值改成与使用上限 11 取小）。</summary>
        /// ⚠ 名字里不许出现「管 J」三个字：判据表（HANDOVER §1.83）按「名字包含 Key」认行，含「管 J」就与上面那一行认成两行。
        public const string TubeJPre103 = "· 管电流密度对原许用值（对照）";
    }

    public ConstraintOut? Find(string keyPrefix)
        => Checks.FirstOrDefault(c => c.Name.StartsWith(keyPrefix, StringComparison.Ordinal));

    /// <summary>某条判据的实测值（找不到则 NaN）</summary>
    public double ValueOf(string keyPrefix) => Find(keyPrefix)?.Actual ?? double.NaN;

    /// <summary>本次是否评了升温 ①（<c>LineCase.CheckRamp</c>）。定尺寸内循环故意关掉它省时间，
    /// 复核那一次必定打开（见 Sizer 的「全判据复核（含升温 ①）」）。</summary>
    public bool RampChecked;

    /// <summary>
    /// **这张表必须有哪几条** —— 「判据缺席」的唯一防线（2026-08-24 补）。
    ///
    /// 为什么需要它：铁律三写着「判据只能过 / 不过 / **无法判定**，绝不允许消失」，
    /// 但此前它的执行方式是**每条判据自己记得写 else 分支**。实际发生的是：
    ///   · ③ 2026-08-15 补上 else（基线算不出来时曾整条消失 ⇒ 报「✓ 全过」而增量降 +32）
    ///   · ⑤ 2026-08-17 补上 else（.3dm 模式下 FlangePlates 为空，同一个形态换个判据）
    ///   · ②″ **一直没补** —— 所有片的 TDiscMaxC 都是 NaN 时（一格都没判进圆盘区，
    ///     见 ShellThermal 的 `IsNegativeInfinity(tDMax) ? NaN`）整条消失，至 2026-08-24 才发现。
    /// 三次都是「就地补一个 else」，于是第四次一定还会发生。**注释不会跑，名单会。**
    ///
    /// ⚠ 只列**判定用**的（HardSafety / Target）。参考量按定义不参与 AllOk，缺了不影响判定，
    ///   硬要它们在场反而会把「参考量算不出来」误判成「设计不合格」。
    ///
    /// ★★★★★ K 路（2026-09-15，Opus 5）：**名单分工况** —— 原来是一份静态名单（两态同一份），现在是下面这张带工况维的表，
    ///   <see cref="RequiredFor"/> 按工况给出本工况判定用的那几条；判据表里每条的 Kind 由 <see cref="StateKindOf"/> 从同一张表取
    ///   （LineRunner.Judge 末尾调 <see cref="LineRunner.ApplyStateCriteria"/> 盖上去）。**分工况只有这一处实现**：
    ///   保温搜索（InsulationSearch）与加密复算（MeshVerify）都读这张表，不许自带清单（源码门 StateCriteriaGateTests 守着）。
    ///   用户 2026-09-15 原话：「比如空管时铂过热那条也不该按 5 ℃ 卡，只要J&lt;11即可」「管内有热玻璃时电流小，法兰成为散热片」
    ///   「希望设计法兰能升温，升温后减少散热的用料最少法兰」。
    ///   ⇒ 空管到温稳态：最热铂高出热偶读数（热侧）降为参考量 —— 这是原话；
    ///     管根低于热偶读数（冷侧）与管孔净流入一并降为参考量 —— 这是**主会话按原话「只要J&lt;11即可」的解读**，已向用户复述、未遭否定。
    ///   其余几条两态照旧（升温、舌片自由段、圆盘盖得住管孔、法兰截面 J 与工况无关，两态逐位相同；管 J 读本工况的段电流，空管态**保持硬判据** ——
    ///     主会话 2026-09-16 裁定：原话「只要 J&lt;11 即可」指的是电流密度类判据整体，管 J 与法兰截面 J 都卡、各按其许用值；2026-09-16 Opus 5 记）。
    ///   工况的地位（用户 2026-09-15 又定）：升温全程先过（升温期判据不在这张表里展开、另行落地）；带玻璃稳态决定法兰设计成不成；空管到温稳态只卡电流密度与场的有效性。
    ///   ⚠ **场的有效性不是判据、两态都保留**：见 <see cref="FieldUndeterminedReasons"/>（场没解到位、越过铂熔点、散热表超界 ⇒ 该工况判不了，判不了不算过）。
    /// </summary>
    public static readonly (string Prefix, CheckKind GlassKind, CheckKind EmptyTubeKind, bool NeedsRamp)[] RequiredByStatePre103 =
    {
        //  判据                         带玻璃稳态             空管到温稳态
        (LineResult.Key.Ramp,        CheckKind.HardSafety, CheckKind.HardSafety, false),  // R20：闭式、每轮都在（此前走集总模型，CheckRamp=false 时合法缺席）
        (LineResult.Key.NetFlux,     CheckKind.HardSafety, CheckKind.Reference,  false),  // K 路（2026-09-15 Opus 5）：空管态降参考（主会话解读，见上）
        // R48 B（2026-09-14 Opus 5）：热侧换成热偶读数基准（原 DiscTemp 降为旧判法参考量，参考量不进名单）
        (LineResult.Key.HotOverTc,   CheckKind.HardSafety, CheckKind.Reference,  false),  // K 路（2026-09-15 Opus 5）：空管态降参考（用户原话）
        (Key.FreeTab,                CheckKind.HardSafety, CheckKind.HardSafety, false),
        (Key.DiscCover,              CheckKind.HardSafety, CheckKind.HardSafety, false),
        // 2026-09-17 Opus 5：现场缠绕圈数（用户当日原话）。闭式、纯输入 ⇒ 两态逐位相同。
        // ★★★ 2026-09-18 Opus 5：**两态都降为参考行**（用户当日「还是只给材质保温厚度方案就行」）——
        //   APP 只出材质与各区厚度方案，怎么包（缠绕还是预制保温块）由现场定 ⇒ 缠不缠得出来不卡交付。
        //   照常算、照常印圈数（WrapLimits.TurnsLine），不进 AllOk／HardOk。见 WrapLimits.PlanOnlyNote。
        (Key.WrapTurns,              CheckKind.Reference,  CheckKind.Reference,  false),
        (Key.TubeJ,                  CheckKind.HardSafety, CheckKind.HardSafety, false),
        (Key.SectionJ,               CheckKind.HardSafety, CheckKind.HardSafety, false),  // 2026-09-08 用户设计因果链第 ④ 步：截面 J 全体 < 11
        // R48 B（2026-09-14 Opus 5）：冷侧取代旧判法「法兰增量温降 ≤ 10（目标）」，**升为硬安全线**（用户 2026-09-14：上下各 5 ℃）
        (LineResult.Key.ColdUnderTc, CheckKind.HardSafety, CheckKind.Reference,  false),  // K 路（2026-09-15 Opus 5）：空管态降参考（主会话解读，见上）
    };

    /// <summary>
    /// ★★★★★ 决 103（业主 2026-09-24）：**生产口径的分工况表**（<see cref="CriteriaRuleSet.决103"/>）。上面那张 <see cref="RequiredByStatePre103"/> 是 09-14 口径，只供改回。
    /// 带玻璃稳态：热侧「法兰最热处高出管接触处温度」、冷侧「管接触处流入法兰的净热流」为硬判据；局部与整片热稳定由参考升为硬判据；
    ///   最热铂高出热偶读数、管根低于热偶读数、管孔净流入须为正三条降为参考（照算照印）。
    /// 空管到温稳态（全局方案第 2 版 1.3 节、业主 09-15）：只卡电流密度与几何闭式 ⇒ 两条新温差类判据与两条热稳定只作参考（升温期的热稳定另由升温全程卡）。
    /// 其余各条两态照旧（升温、舌片自由段、圆盘盖得住管孔、管 J、法兰截面 J）；接合区缠绕 决 104（2026-09-25）起两态硬判据（限值 = 参数表圆盘保温上限，改回 = 正无穷）。
    /// </summary>
    public static readonly (string Prefix, CheckKind GlassKind, CheckKind EmptyTubeKind, bool NeedsRamp)[] RequiredByState =
    {
        //  判据                              带玻璃稳态             空管到温稳态
        (LineResult.Key.Ramp,             CheckKind.HardSafety, CheckKind.HardSafety, false),  // 限值 = 卡交付的管 J 限值（决 103：与使用上限 11 取小）
        (LineResult.Key.HotOverContact,   CheckKind.HardSafety, CheckKind.Reference,  false),  // 决 103 热侧（新）
        (LineResult.Key.TubeToFlangeHeat, CheckKind.HardSafety, CheckKind.Reference,  false),  // 决 103 冷侧（新）
        (LineResult.Key.NetFlux,          CheckKind.Reference,  CheckKind.Reference,  false),  // 决 103：降为参考
        (LineResult.Key.HotOverTc,        CheckKind.Reference,  CheckKind.Reference,  false),  // 决 103：降为参考
        (LineResult.Key.ColdUnderTc,      CheckKind.Reference,  CheckKind.Reference,  false),  // 决 103：降为参考
        (Key.FreeTab,                     CheckKind.HardSafety, CheckKind.HardSafety, false),
        (Key.DiscCover,                   CheckKind.HardSafety, CheckKind.HardSafety, false),
        (Key.WrapTurns,                   CheckKind.HardSafety, CheckKind.HardSafety, false),  // ★ 决 104（业主 2026-09-25「圆盘保温块最大厚度(圆盘之前说过了10mm)」）：两态升回硬判据（纯输入、与工况无关；限值 = 参数表圆盘保温上限）；决103前 仍参考（2026-09-18 口径）
        (Key.TubeJ,                       CheckKind.HardSafety, CheckKind.HardSafety, false),  // 决 103：限值 11（与原许用 12 取小）
        (Key.SectionJ,                    CheckKind.HardSafety, CheckKind.HardSafety, false),
        (Key.FlangeStab,                  CheckKind.HardSafety, CheckKind.Reference,  false),  // 决 103：带玻璃稳态升为硬判据
        (Key.LocalStab,                   CheckKind.HardSafety, CheckKind.Reference,  false),  // 决 103：带玻璃稳态升为硬判据（全格精算）
    };

    /// <summary>决 103：按口径取分工况表（全仓唯一读口）。</summary>
    public static (string Prefix, CheckKind GlassKind, CheckKind EmptyTubeKind, bool NeedsRamp)[] RequiredByStateFor(CriteriaRuleSet rs)
        => rs == CriteriaRuleSet.决103前 ? RequiredByStatePre103 : RequiredByState;

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：本工况**判定用**的必备名单（= <see cref="RequiredByState"/> 里本工况 Kind 不是参考量的那几条）。
    /// 原静态名单 Required 已删 —— 调用方必须说清是哪个工况（一份不带工况的名单会被拿去判空管态，那正是这一路要堵的）。
    /// </summary>
    public static (string Prefix, CheckKind Kind, bool NeedsRamp)[] RequiredFor(bool emptyTube) => RequiredFor(emptyTube, CriteriaRuleSet.决103);

    /// <summary>决 103：按口径取本工况判定用的必备名单（不带口径的重载 = 生产口径 决103）。</summary>
    public static (string Prefix, CheckKind Kind, bool NeedsRamp)[] RequiredFor(bool emptyTube, CriteriaRuleSet rs) => RequiredByStateFor(rs)
        .Select(q => (q.Prefix, Kind: emptyTube ? q.EmptyTubeKind : q.GlassKind, q.NeedsRamp))
        .Where(q => q.Kind != CheckKind.Reference)
        .ToArray();

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：判据名（ConstraintOut.Name 或 Key，按前缀）在本工况下的 Kind；不在分工况表里 ⇒ null（Kind 由构造处定，两态相同）。
    /// </summary>
    public static CheckKind? StateKindOf(string nameOrKey, bool emptyTube) => StateKindOf(nameOrKey, emptyTube, CriteriaRuleSet.决103);

    /// <summary>决 103：按口径取（不带口径的重载 = 生产口径 决103）。</summary>
    public static CheckKind? StateKindOf(string nameOrKey, bool emptyTube, CriteriaRuleSet rs)
    {
        if (string.IsNullOrEmpty(nameOrKey)) return null;
        foreach (var q in RequiredByStateFor(rs))
            if (nameOrKey.StartsWith(q.Prefix, StringComparison.Ordinal))
                return emptyTube ? q.EmptyTubeKind : q.GlassKind;
        return null;
    }

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：分工况表把一条带玻璃稳态的硬判据降为参考量时，写在它说明最前面的那句（进界面，不带判据代号）。
    /// </summary>
    public static string StateDowngradeNote(bool emptyTube) => emptyTube
        ? "空管到温稳态：用户 2026-09-15 定只卡电流密度（管 J 与法兰截面 J，各按其许用值，「只要 J<11 即可」）与场的有效性，本条在空管态照常计算、只作参考，不卡交付；带玻璃稳态下本条仍是硬判据。"
        : "带玻璃稳态：本条只作参考，不卡交付。";

    /// <summary>
    /// ★ K 路（2026-09-15，Opus 5）：本结果属于哪个工况（= 解它的 <see cref="LineCase.EmptyTube"/>，RunOnce 与管侧单解造结果时写入）。
    /// <see cref="MissingChecks"/>／<see cref="HardOk"/> 按它取必备名单。默认 false = 带玻璃稳态（手造的结果不写就是带玻璃，逐位不变）。
    /// </summary>
    public bool EmptyTube;

    /// <summary>决 103：局部热稳定全格精算的代价（秒、格数；机时量，不进判读；0 = 没做）。</summary>
    public double LocalStabFullGridSec;
    public int LocalStabFullGridCells;

    /// <summary>
    /// ★ 决 103（2026-09-24）：本结果按哪个判据口径判的（= 解它的 <see cref="LineCase.RuleSet"/>，RunOnce 与管侧单解造结果时写入）。
    /// <see cref="MissingChecks"/>／<see cref="HardOk"/> 按它取必备名单。手造的结果（门的合成数据）不写 ⇒ 决103前 —— 与 <see cref="EmptyTube"/> 同一个约定：
    /// 手造结果的判据表是按改前口径造的，默认读改前名单，逐位不变；生产结果一律由 RunOnce 写明。
    /// </summary>
    public CriteriaRuleSet RuleSet = CriteriaRuleSet.决103前;

    /// <summary>
    /// ★★★★★ K 路（2026-09-15，Opus 5）：**场的有效性** —— 本工况为什么判不了（空 = 场判得了）。**不是判据、两态都保留**：
    /// 任何一条非空 ⇒ <see cref="AllOk"/>／<see cref="HardOk"/> 为假，<see cref="Failed"/> 点名。
    /// 为什么要单列（审查 P1-9）：空管态的热侧、冷侧、净流入降为参考量之后，场没解到位时把它们标成「判不了」已经拦不住 AllOk ——
    ///   剩下能被打成判不了的硬判据只有「升温」，而它是闭式、只是恰好在「吃法兰场」名单里；AllOk 原先又不读 <see cref="Ok"/>（越过熔点只置 Ok）。
    ///   「空管场坏了照样全过」就是这样来的。现在不靠任何一条判据恰好在名单里，直接读场的状态位。
    /// 读的位（与 LineRunner 的五个「标成无法判定」后置遍历读的是同一组位，不读文字）：
    ///   · <see cref="Ok"/> 为假（越过铂熔点、段解失败、算例被拒）—— 原因取 <see cref="Message"/>；
    ///   · 片的 <see cref="FlangeOut.FieldsConverged"/> 为假（温度场未收敛、电位场未收敛、法兰最高温超过表面散热表上限）；
    ///   · 片的 <see cref="FlangeOut.InsulBoundaryUndetermined"/>（图纸推不出保温分界）、<see cref="FlangeOut.ClampCoversHole"/>、<see cref="FlangeOut.ClampIntoDisc"/>；
    ///   · 段的 <see cref="SegmentOut.TubeLossTableExceeded"/>（管温超出管表面散热表上限）。
    /// 外层耦合没收敛不在这里（<see cref="AllOk"/> 另读 <see cref="Converged"/>，原样）。
    /// </summary>
    public string[] FieldUndeterminedReasons
    {
        get
        {
            var why = new List<string>();
            if (!Ok) why.Add("整线解不出：" + (string.IsNullOrEmpty(Message) ? "（没有给出原因）" : Message));
            foreach (var f in Flanges)
            {
                if (f is null) continue;
                if (!f.FieldsConverged) why.Add($"{f.Name}：{(string.IsNullOrEmpty(f.FieldNote) ? "场没解到位" : f.FieldNote)}");
                if (f.InsulBoundaryUndetermined) why.Add($"{f.Name}：图纸没有切点，保温分界判不了");
                if (f.ClampCoversHole) why.Add($"{f.Name}：压接段盖到了管孔");
                if (f.ClampIntoDisc) why.Add($"{f.Name}：压接段伸进了圆盘");
            }
            foreach (var s in Segments)
                if (s is not null && s.TubeLossTableExceeded)
                    why.Add($"{s.Name}：管温 {s.TubeTMaxC:0} °C 超出管表面散热表上限 {s.TubeLossTableHiC:0} °C");
            return why.ToArray();
        }
    }

    /// <summary>该出现却没出现的判据（按本工况的必备名单）。**缺席 ≠ 通过。**</summary>
    public string[] MissingChecks => RequiredFor(EmptyTube, RuleSet)
        .Where(q => (!q.NeedsRamp || RampChecked) && Find(q.Prefix) is null)
        .Select(q => q.Prefix).ToArray();

    /// <summary>没过的**硬安全线**（含无法判定）。要这份名单就用它，不要另行过滤 Checks。</summary>
    public ConstraintOut[] HardBlocked => Checks
        .Where(c => c.Kind == CheckKind.HardSafety && (!c.Ok || c.Undetermined)).ToArray();

    /// <summary>
    /// 全部**硬安全线**是否通过（**无法判定 ≠ 通过**，**缺席 ≠ 通过**）。
    ///
    /// ⚠ 原来写成 `Checks.Where(硬).All(过)` —— 空集上 `All` **恒真**，
    ///   于是一张空判据表会报「硬安全线全过」。加 MissingHard 之后这条路堵死了。
    /// </summary>
    /// K 路（2026-09-15，Opus 5）：名单按本工况取；场判不了（<see cref="FieldUndeterminedReasons"/> 非空）一律不算过。
    public bool HardOk => !RequiredFor(EmptyTube, RuleSet).Any(q => q.Kind == CheckKind.HardSafety
                                          && (!q.NeedsRamp || RampChecked) && Find(q.Prefix) is null)
                       && HardBlocked.Length == 0
                       && FieldUndeterminedReasons.Length == 0;

    /// <summary>
    /// 硬安全线 + 设计目标是否全部通过 —— **可交付的唯一判定**。
    ///
    /// ⚠⚠ 「**无法判定**」一律**不算通过**。
    /// 原来写成 `c.Ok || c.Undetermined`，于是基线算不出来时 ③ 是 NaN/Undetermined，
    /// 却被计入通过 —— 实测阶梯据此报出「管壁 1.5 ✓ 全过」，而同一行的
    /// 增量降 max 是 +32（上限 10）。**判不了被当成判过了。**
    /// 这是「安静地给出可信外观的错误结果」家族的第五个成员
    /// （前四：两端抽热取平均、C2 只判左端、段间无导热、判据整条消失）。
    /// </summary>
    /// ⚠ `MissingChecks.Length == 0` 是**第六个成员**的解药：判据整条消失时，
    /// 下面那个 `All` 只在**剩下的**判据上取全称量词 —— 缺的那条不投反对票。
    /// ★ K 路（2026-09-15，Opus 5）：加 `FieldUndeterminedReasons.Length == 0` —— 场判不了的工况不许报全过（两态都一样）。
    ///   带玻璃稳态下这一项不改变任何 AllOk：那几种情形都会把管孔净流入等硬判据标成判不了（或 Ok 为假 ⇒ 没收敛），原本就过不了；
    ///   空管态下它是唯一不靠「某条判据恰好吃法兰场」的守门（审查 P1-9）。
    public bool AllOk => Converged && MissingChecks.Length == 0 && FieldUndeterminedReasons.Length == 0 && Checks
        .Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)
        .All(c => c.Ok && !c.Undetermined);

    /// <summary>没过的判据（含**无法判定**，标注区分）。供报告直接引用，不要另行拼装。</summary>
    /// ★★★ 2026-08-30：名字走 <see cref="Criteria.Plain"/> **剥掉判据代号**
    ///   （用户：「UI 内严禁使用 ②′ 这类的表示，工程师看不懂」）。
    ///   这一条是代号流到**状态面板**的出口 —— 用户抓图里那行
    ///   「判定：✗ 2 条没过　②″圆盘区最高温 − 管温 292.1/5.0」就是它拼的。
    ///   ⚠ 只改**显示**：`c.Name` 本身（= LineResult.Key）是全仓唯一来源，不动。
    public string[] Failed => Checks
        .Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target && (!c.Ok || c.Undetermined))
        .Select(c => c.Undetermined
                   ? $"{Criteria.Plain(c.Name)} **无法判定**"
                   : $"{Criteria.Plain(c.Name)} {c.Actual:0.0}/{c.Limit:0.0}")
        // 缺席的也要报出来 —— 否则 AllOk 为 false 而 Failed 是空的，
        // 界面上就是「✗」后面什么都不写，比不报还难查。
        .Concat(MissingChecks.Select(k => $"{Criteria.Plain(k)} **判据缺席**（该出现却整条没出现）"))
        // K 路（2026-09-15，Opus 5）：场判不了也要点名 —— 空管态三条降参考之后，AllOk 为假而判据表里没有一条硬判据不过，界面上不许只剩一个「✗」
        .Concat(FieldUndeterminedReasons.Select(w => $"**本工况的场判不了**（{(EmptyTube ? "空管到温稳态" : "带玻璃稳态")}，判不了不算过）：{w}"))
        .ToArray();
}

/// <summary>
/// ★ R48（2026-09-14，Opus 5；审查意见「门不许手抄生产配方」）：第 j 片**壳热解**的输入 —— 网格与电流场之外的全部。
/// 由 <see cref="LineRunner.PlateThermalInputs"/> 组装、<see cref="LineRunner.SolvePlateThermal"/> 消费；整线求解（RunOnce 逐片循环）与门共用这一份。
/// </summary>
public sealed class PlateThermalSetup
{
    /// <summary>本片的物性／保温／夹持：克隆自 LineCase.Base，写入本片控温点、夹持温度、按电流定的铜排热导、本片圆盘保温（LineCase.DiscInsulEffectiveAt）。</summary>
    public DesignInputs P2 = new();
    /// <summary>保温分界 x（按 x 划时用）。图纸路径推不出分界 ⇒ +∞（整片按裸露）且 <see cref="InsulUndetermined"/>。</summary>
    public double InsulX = double.PositiveInfinity;
    /// <summary>舌盘分界 x（NaN = ShellThermal 自己的默认）。</summary>
    public double TabBoundaryX = double.NaN;
    /// <summary>双舌片对称保温。</summary>
    public bool SymmetricInsul;
    /// <summary>本片舌保温 mm（解析 = 板件；图纸 = LineCase.TabInsul3dmAt）。</summary>
    public double TabInsulThickMm = double.NaN;
    /// <summary>圆盘区与保温边界按半径划时的盘半径、保温半径（NaN = 退回按 x，InsulRule／DiscZoneRule 写明）。</summary>
    public double DiscRadiusMm = double.NaN, InsulDiscRadiusMm = double.NaN;
    /// <summary>2026-09-23（F4）：分区热账按份额（true，生产）还是按格心（false，只供门）；取自 <see cref="LineCase.ZoneByMaterialFraction"/>。</summary>
    public bool ZoneByMaterialFraction = true;
    /// <summary>按本片电流定的铜排热导 W/K 与载流需截面 mm²；-1／0 = 没有按电流定。</summary>
    public double BusGWPerK = -1, BusSectionForCurrentMm2;
    /// <summary>保温分界的出处说明与「判不了」位（进 FlangeOut）。</summary>
    public string InsulNote = "";
    public bool InsulUndetermined;
}

/// <summary>
/// **整线求解的唯一入口** —— CLI 与 WinForms 都只调 <see cref="Run"/>，
/// 于是两边不可能跑出不同结果（此前 CLI 与 UI 各自拼装流程，是长期的不一致来源）。
///
/// 流程：
///   ① 逐段定电流：实测模式直接取；反算模式用 SegmentSolver 二分使中点达设定温度
///   ② 逐段解管温：得管根温度、轴向剖面、玻璃出口温度（串联到下一段）
///   ③ 逐片解法兰：厚度场(Rhino, 带缓存) → 变步长壳网格 → 电流场 → 温度场
///      共用片电流用 §4.2h 的 √(I₁²+I₂²+I₁I₂)
///   ④ 汇总质量、判定约束
/// </summary>
public static class LineRunner
{
    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：空管到温稳态工况的说明（<see cref="LineCase.EmptyTube"/> 为 true 时写进每个结果的 Notes 第一条）。
    /// 会进界面 ⇒ 不写判据代号。
    /// 2026-09-14 Opus 5（R48 审查第 1、3、4、7 条）：由常量改成按算例生成 —— 要写出**本次用的控温点与来源**；
    ///   去掉「管强度偏保守」（许用应力按段设定温度取，空管管根可能比设定高，没有依据说它保守）；
    ///   「升温链与本工况无关」改成准确说法（只有尺寸链无关，三条参考量随工况变）；写明模型不含管腔辐射与管口散热。
    /// R48 G3（2026-09-15，Opus 5）：管腔轴向辐射进了模型 ⇒ 改写成「计入了什么、按什么口径、系数多少」；管口辐射：用户定铂管两端封住 ⇒ 不计。
    ///   控温点来源：用户已定空管保温三段全部升到 1150 °C（= 全线升温目标），「尚待确认」去掉；沿用生产控温点那一种写明是对照。
    ///   各段管腔 kA 调 <see cref="SegmentSolver.CavityRadKAAt"/>（段解同一个函数；Base 带玻璃，故先在克隆上调 <see cref="MakeEmptyTubeSegment"/>，与 RunOnce 空管段同一个函数）。
    /// R48 G3 审查后（2026-09-15，Opus 5）：两档之间没有先验依据取舍（初版默认值理由已撤回，见 DesignInputs.TubeCavityRadKA1150WmPerK）⇒ 取哪一档之后加「两档之间尚无依据取舍」；
    ///   空管段参数的造法原在这里与 RunOnce 各抄一份，提成 <see cref="MakeEmptyTubeSegment"/>。
    /// </summary>
    public static string EmptyTubeNoteFor(LineCase c)
    {
        string sp = string.Join("/", c.SetpointC.Select(v => v.ToString("0.#")));
        string from = c.EmptyTubeSetpointFrom == EmptyTubeSetpoint.RampTarget
            ? $"全线取升温目标 {c.RampTargetC:0.#} °C，即空管保温时全线控温点都升到该温度"
            : "沿用算例给定的控温点（由设计造算例时即生产控温点）；空管保温定的是全线升温目标，本次是对照";
        var pe = SegmentSolver.Clone(c.Base);
        MakeEmptyTubeSegment(pe);
        double k0 = c.Base.TubeCavityRadKA1150WmPerK;
        string cavity;
        if (k0 > 0)
        {
            string kas = string.Join("/", c.SetpointC.Select(t => SegmentSolver.CavityRadKAAt(pe, t).ToString("0.000")));
            bool offD = !(Math.Abs(c.TubeIdMm - SegmentSolver.CavityRadCalibTubeIdMm) <= 1e-9);
            bool offE = !(Math.Abs(c.Base.PtEmissivity - SegmentSolver.CavityRadCalibEmissivity) <= 1e-9);
            cavity = $"管腔内轴向辐射按附加的轴向导热计入：{SegmentSolver.CavityRadRefC:0} °C 时 {k0:0.000} W·m/K，按各段控温点温度的三次方换算为 {kas} W·m/K"
                   + "（段内取常数；两段管腔在接头处连通，接头导热也计入这一份）；"
                   + $"系数按内径 {SegmentSolver.CavityRadCalibTubeIdMm:0} mm、铂发射率 {SegmentSolver.CavityRadCalibEmissivity:0.00} 估算、未经实测，"
                   + (k0 == SegmentSolver.CavityRadKA1150Low ? $"取两档估计 {SegmentSolver.CavityRadKA1150Low:0.000}～{SegmentSolver.CavityRadKA1150High:0.000} 里较低的一档（两档之间尚无依据取舍）"
                      : k0 == SegmentSolver.CavityRadKA1150High ? $"取两档估计 {SegmentSolver.CavityRadKA1150Low:0.000}～{SegmentSolver.CavityRadKA1150High:0.000} 里较高的一档（两档之间尚无依据取舍）"
                      : $"本次不是两档估计 {SegmentSolver.CavityRadKA1150Low:0.000}～{SegmentSolver.CavityRadKA1150High:0.000} 之一")
                   + (offD || offE
                      ? $"（⚠ 本次{(offD ? $"内径 {c.TubeIdMm:0.#} mm" : "")}{(offD && offE ? "、" : "")}{(offE ? $"铂发射率 {c.Base.PtEmissivity:0.00}" : "")} 与估算条件不同，系数没有换算）"
                      : "")
                   + "。";
        }
        else cavity = "管腔内轴向辐射本次未计入（系数为 0）。";
        return "工况：空管到温稳态（无玻璃）—— 段解与无法兰基线按产量 0、管内玻璃换热 0 算，法兰场解与判据照常。"
             + $"控温点 {sp} °C（{from}）。"
             + cavity
             + "铂管两端封住，不计管口辐射散热。"
             + "设计电流、舌片厚与「升温」「法兰截面 J」两条判据的实际值与本工况无关；"
             + "「升温期法兰−管峰值」「升温到位用时（集总）」与截面 J 说明里的两节点对照取的是本次空管稳态场，随工况变。"
             + "管强度：载荷沿用带玻璃的液柱与流动值，许用应力按段设定温度取（空管管根可能高于设定）。玻璃温降不适用。";
    }

    /// <summary>
    /// ★ R48 G3 审查后（2026-09-15，Opus 5）：把一份段参数（已是克隆）**就地**改成空管段 —— 空管段参数的造法只此一处，
    /// RunOnce 的段解与 <see cref="EmptyTubeNoteFor"/> 的各段换算值都调它（原来两处各抄一份，以后多置一个量时工况说明会悄悄和段解对不上）。
    /// 做三件事：端部额外保温形状按置 0 之前（带玻璃）的 hg 定（调用方已显式给过 ≥ 0 就不覆盖，R48 审查第 5 条）；产量置 0；管内玻璃换热置 0
    /// （空管判别见 <see cref="SegmentSolver.IsEmptyTube"/>）。判据用的那份参数（管强度按带玻璃载荷）由调用方在调本函数之前另克隆。
    /// </summary>
    public static void MakeEmptyTubeSegment(DesignInputs p)
    {
        if (p.EndInsulShapeHGlass < 0) p.EndInsulShapeHGlass = p.HGlass;
        p.ThroughputTPerDay = 0; p.HGlass = 0;
    }

    /// <summary>
    /// ★ R48 审查第 4 条（2026-09-14，Opus 5）：取本次稳态场当参考的升温类参考量，空管时在 Note 末尾标明「随工况变」。带玻璃返回空串（逐位不变）。
    /// </summary>
    private static string StateDependentTag(LineCase c) => c.EmptyTube
        ? "　⚠ 本条的参考值取自本次**空管到温稳态**场，随稳态工况变（带玻璃稳态下另有一个数）。"
        : "";

    /// <summary>
    /// 把未显式指定的项接到 <see cref="LineCase.Base"/>（＝界面左侧那张参数表）。
    ///
    /// 幂等：跑过一次之后哨兵已被填实，再跑不会变。
    /// 之所以能安全地就地改传入的 c：这三项**没有任何 UI 会读**，
    /// 而写进去的值只依赖 c.Base，稳定可重现。
    /// FlangeAutoSizer.CloneCase 复制的是已归一化的值，派生算例进 Run 后再归一化一次也无妨。
    /// </summary>
    private static void Normalize(LineCase c)
    {
        if (double.IsNaN(c.TubeIdMm)) c.TubeIdMm = c.Base.TubeIdMm;
        // ★ 逐段长度：没给或给少了，按参数表的「直接加热铂金管的长度」铺满。
        //   给多了不截断 —— 段数是 SetpointC 说了算，多出来的不参与。
        if (c.SegLengthMm.Length < c.SegmentCount)
        {
            var len = new double[c.SegmentCount];
            for (int i = 0; i < len.Length; i++)
                len[i] = i < c.SegLengthMm.Length && c.SegLengthMm[i] > 0
                       ? c.SegLengthMm[i] : c.Base.TubeLengthMm;
            c.SegLengthMm = len;
        }
        if (string.IsNullOrEmpty(c.GradeName)) c.GradeName = c.Base.GradeName;
        // ★ R48 物性接线（2026-09-23，Opus 5.5）：整线只认**一个**牌号来源。段解与强度读 c.GradeName，法兰热解／升温／热稳定／设计电流读 c.Base.GradeName ——
        //   两者不同就会一半按这个牌号、一半按那个牌号算，结果里却看不出来。生产里没有任何地方显式设 LineCase.GradeName（只有本函数与 FlangeAutoSizer.CloneCase 的复制），
        //   所以这里不一致只可能是有人新写了一处：当场抛，不静默取其一。门 R48PropsWiringGateTests.门_整线牌号只有一个来源。
        else if (!string.Equals(c.GradeName, c.Base.GradeName, StringComparison.Ordinal))
            throw new InvalidOperationException($"整线牌号「{c.GradeName}」与参数表牌号「{c.Base.GradeName}」不一致 —— 电、热物性与强度只认一个牌号来源，请只在参数表里选牌号");
    }

    /// <summary>
    /// ★ R48 物性接线（2026-09-23，Opus 5.5）：选的不是纯铂时，在结果说明**末尾**加一条「电、热物性按哪个牌号取／退回纯铂」
    /// （<see cref="PtProps.Note"/>），外加本算例温度区间超出所选牌号数据点之处（<see cref="PtProps.RangeNote"/>）。
    /// 纯铂 ⇒ 什么都不加（结果逐位不变）。温度区间 = 升温起止温度（<see cref="LineCase.RampFromC"/>、<see cref="LineCase.RampTargetC"/>）、各段金属温度、各片法兰最低／最高温（解出来的片）。
    /// 升温起止温度**不论开没开升温核算都并入**：设计电流闭式（<see cref="DesignCurrent.Compute"/>）每轮都跑，
    /// 它从 RampFromC 升到目标，一路按牌号读 ρ、cp、k。
    /// 成功与失败的出口都加（首轮失败、耦合中途失败、正常收尾三处）。
    /// </summary>
    private static void AddGradeNote(LineCase c, LineResult res)
    {
        var props = PtProps.For(c);
        if (props.IsPure && !props.IsFallback) return;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        void Take(double t) { if (double.IsFinite(t)) { lo = Math.Min(lo, t); hi = Math.Max(hi, t); } }
        Take(c.RampFromC); Take(c.RampTargetC);   // 设计电流闭式每轮都从 RampFromC 算起，与 CheckRamp 无关
        foreach (var s in res.Segments) foreach (double t in s.TMetal) Take(t);
        foreach (var f in res.Flanges) if (f.TMaxC > 0) { Take(f.TMinC); Take(f.TMaxC); }
        string range = double.IsFinite(lo) ? props.RangeNote(lo, hi) : "";
        res.Notes.Add("★ " + props.Note + (range.Length > 0 ? "；" + range : ""));
    }

    /// <summary>
    /// 外层耦合的**步长**（两端各自判，不经过会切换分支的标量）与「这一轮有没有管根算不出」。
    /// 2026-09-17 Opus 5（J 路，J7/J6 复核「应修 3」）：从 <see cref="Run"/> 的耦合主循环原样提出来（比较式、Math.Max 顺序、初值 0 逐位不变），
    /// 好让**行为门**造一段 <c>TRootAC = NaN</c> 的合成解直接调生产这一份 —— 上一版 P3-10 改的是判定口径却只有一句注释，全仓没有任何门（复核查出）。
    /// ⚠ 这里对「管根 NaN 是结构性还是偶发」不作前提：出了就是判不了（见 <see cref="CoupleConverged"/>）。
    /// </summary>
    public static (double Delta, bool RootNaN) CoupleRootStep(SegmentOut[] prev, SegmentOut[] next, int segmentCount)
    {
        double delta = 0;
        bool rootNaN = false;
        for (int i = 0; i < segmentCount; i++)
        {
            double da = Math.Abs(next[i].TRootAC - prev[i].TRootAC);
            double db = Math.Abs(next[i].TRootBC - prev[i].TRootBC);
            if (!double.IsNaN(da)) delta = Math.Max(delta, da); else rootNaN = true;
            if (!double.IsNaN(db)) delta = Math.Max(delta, db); else rootNaN = true;
        }
        return (delta, rootNaN);
    }

    /// <summary>
    /// 外层耦合「这一轮算收敛了」的**唯一判定**：三条都要过 —— 步长距离估计 &lt; 容差、真残差 × 已知最坏放大 &lt; 容差、这一轮没有管根算不出。
    /// 2026-09-15 Opus 5（J 路，P3-10）加的第三条：管根出 NaN 时步长可以是 0（NaN 被跳过），前两条照样成立 ⇒ 判不了会被判成收敛。
    /// 2026-09-17 Opus 5（J 路，复核「应修 3」）：提成公开函数，行为门调同一份（不手抄生产配方）。
    /// </summary>
    /// ★ 2026-09-18 Opus 5（合并 J×L）：放大倍数由参数传入 —— 原先写死 <see cref="LineCase.FixedPointAmp"/>（25），
    ///   而 L 路 2026-09-16 已把主环的放大改成当场算的 <see cref="EndTempFixedPointAmpOf"/>（1 + ℓt/Δx，实测本算例 77～88）。
    ///   写死 25 会把「到不动点的距离」低估，主环会提早宣布收敛（L 路实测：25 口径报 0.946 K &lt; 1 K「已收敛」，闭式口径 3.039 K）。
    ///   容差同理由调用方给：主环给的是 <see cref="CoupleTolKFor"/> 当场算的那一个，不是 <see cref="LineCase.CoupleTolK"/> 这个上限。
    public static bool CoupleConverged(double remainK, double resK, double tolK, bool rootNaN, double amp)
        => remainK < tolK && resK * amp < tolK && !rootNaN;

    /// ★★★★★ R48 L（2026-09-17，Opus 5）：**当前最小「硬安全线 · 温度判据」裕度 K**（= 离限值还有多远，取绝对距离）。
    ///
    /// 名单**不由人列**：从结果自己的判据表里挑 <see cref="CheckKind.HardSafety"/> 且单位为 K 的那几条
    /// （本轮就是「最热铂高出热偶读数」与「管根低于热偶读数」；空管工况下这两条降为参考量 ⇒ 挑不到 ⇒ 返回 NaN）。
    /// 判不了／暂不给数／NaN 的一概跳过。
    /// ⚠ 取**绝对距离**：越过限值（裕度为负）时离判决线同样近，同样要细容差；
    ///   而离得远（裕度 5 K）时不必细 —— 由 <see cref="CoupleTolKFor"/> 的上限截住。
    /// ⚠ 单位为 W 的「管孔净流入」**不进这张名单**：它的裕度是瓦，与温度容差之间要一个灵敏度才能换算，
    ///   而那个灵敏度没有实测 —— 编一个出来就是把判据的分辨率建在假数上。
    /// </summary>
    public static double MinHardTempMarginK(LineResult? r)
    {
        if (r is null) return double.NaN;
        double m = double.NaN;
        foreach (var ck in r.Checks)
        {
            if (ck is null || ck.Kind != CheckKind.HardSafety) continue;
            if (!string.Equals(ck.Unit, "K", StringComparison.Ordinal)) continue;
            if (ck.Undetermined || ck.Withheld) continue;
            if (double.IsNaN(ck.Actual) || double.IsNaN(ck.Limit)) continue;
            double d = Math.Abs(ck.Limit - ck.Actual);
            if (double.IsNaN(m) || d < m) m = d;
        }
        return m;
    }

    /// <summary>
    /// ★★★★★ R48 L（2026-09-17，Opus 5）：**这次外层耦合该停在多细的容差上 —— 全仓唯一读口。**
    ///
    /// <c>tol = min(c.CoupleTolK, max(c.CoupleTolFloorK, c.CoupleTolMarginFrac × 最小硬安全线温度裕度))</c>；
    /// <see cref="LineCase.CoupleTolFromMargin"/> 为 false，或还没有判据可读（<paramref name="r"/> 为 null／挑不到温度判据）⇒ 就是 <see cref="LineCase.CoupleTolK"/>。
    ///
    /// 为什么必须只有这一份：容差同时出现在**四处**（主环停机的两支、基线环、未收敛报告里「还要多少轮」与分辨率地板），
    /// 手抄一份就会出现「一处收紧了另一处没收紧」—— 本仓库最常见的病（门：R48LCoupleTolGateTests）。
    /// 上限永远赢（先 max 后 min）：把 <see cref="LineCase.CoupleTolK"/> 设得比下限还细时，结果是那个更细的数，而不是被下限放粗。
    /// </summary>
    /// <summary>
    /// ★ R48 L（2026-09-17，Opus 5）：**无法兰基线那一层的停机容差 —— 唯一读口**（见 <see cref="LineCase.BaselineTolK"/>）。
    /// 它与主环那一个是两件事：主环服务硬安全线，基线只服务参考量「法兰增量温降」。
    /// </summary>
    public static double BaselineTolKFor(LineCase c)
        => c is null ? throw new ArgumentNullException(nameof(c)) : c.BaselineTolK;

    public static double CoupleTolKFor(LineCase c, LineResult? r)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        double ceil = c.CoupleTolK;
        if (!c.CoupleTolFromMargin) return ceil;
        double m = MinHardTempMarginK(r);
        if (double.IsNaN(m)) return ceil;
        return Math.Min(ceil, Math.Max(c.CoupleTolFloorK, c.CoupleTolMarginFrac * m));
    }

    public static LineResult Run(LineCase c, IProgress<string>? progress = null,
                                 CancellationToken cancel = default)
    {
        // ★ 先把「没显式指定」的项接到参数表上。必须在**任何**读取这三项之前。
        Normalize(c);

        // ★★★★★ R48 M（2026-09-18，Fable 5.1）：**实测雅可比放大只量一次**（零抽热、各段各解各的那一态；段解只解段），
        //   基线环与主环共用这一个数（口径不劈叉）。放大 = max(闭式 × 1.1, 它) —— 见 StopAmpOf。量不出来 ⇒ NaN ⇒ 只用闭式 × 1.1（照实印进结果）。
        double ampJac = c.JacobianAmpCache;
        double ampJacSec = 0;
        if (double.IsNaN(ampJac) && c.EndTempAmpFromDecayLength && c.MeasureJacobianAmp)
        {
            var swJ = System.Diagnostics.Stopwatch.StartNew();
            try { ampJac = MeasureJacobianAmp(c, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { ampJac = double.NaN; }
            ampJacSec = swJ.Elapsed.TotalSeconds;
            c.JacobianAmpCache = ampJac;
        }

        // ── ★ 先算**无法兰基线**：同几何、同保温、同段间耦合，只把法兰抽热置零。
        //   C2 要判的是「法兰挖了多深的坑」，不是「偏离本段控温点多少」——
        //   后者在共用法兰处由两侧控温点决定，法兰管不着（见 SegmentOut.BaseTRootAC）。
        //   基线只依赖管几何/保温/控温点，与法兰热解无关 ⇒ 每个构型算一次即可。
        var baseline = new (double A, double B)[c.SegmentCount];
        string baseFailMsg = "";
        // ⚠ 缓存必须校验**内容**，不能只看长度：失败时写进去的是 (NaN,NaN)，
        //   长度照样够 ⇒ 会把一次失败永久固化。含 NaN 一律重算。
        bool cacheOk = c.BaselineRootC.Length >= c.SegmentCount;
        for (int i = 0; cacheOk && i < c.SegmentCount; i++)
            if (c.BaselineRootC[i].Length < 2 ||
                double.IsNaN(c.BaselineRootC[i][0]) || double.IsNaN(c.BaselineRootC[i][1]))
                cacheOk = false;
        if (cacheOk)
        {
            for (int i = 0; i < c.SegmentCount; i++)
                baseline[i] = (c.BaselineRootC[i][0], c.BaselineRootC[i][1]);
        }
        else
        {
            var zero = new double[c.SegmentCount];
            var zeroLR = new (double L, double R)[c.SegmentCount];
            // ★ R48 E 审查修改（2026-09-15 Opus 5）：段间端温的欠松弛循环原样搬进 IterateNeighbourTemps（管侧响应 SolveTubeWithDraws 共用），
            //   这里照原参数调：起步 null、容差 CoupleTolK、轮数 max(30, CoupleMaxRounds)、进度文字「无法兰基线」。纯搬移，全量转储前后逐位相同为证。
            // ★ R48 L（2026-09-17，Opus 5）：基线这一层的容差**按它自己服务的判据定**（唯一读口 BaselineTolKFor）。
            //   基线只进参考量「法兰增量温降」（限值 10 K，不卡交付），不进两条硬安全线 ⇒ 1 K = 限值的十分之一。
            //   实测跟着主环一起收到 0.1 K：基线 201 s、主环 75～92 s —— 机时几乎全花在一个不卡交付的量上
            //   （deliverable\R48_L_耦合容差收紧_导航_本次开跑于2026-09-17_214736.txt）。
            double baseTolK = BaselineTolKFor(c);
            var bIt = IterateNeighbourTemps(c, bnb => RunOnce(c, null, cancel, zero, zeroLR, bnb), null,
                                            Math.Max(30, c.CoupleMaxRounds), baseTolK, progress, "无法兰基线", ampJac);
            LineResult? br = bIt.Last;
            int baseRounds = bIt.Rounds; double baseDmax = bIt.JudgeK;
            if (baseDmax >= baseTolK)
                baseFailMsg += $"基线外层 {baseRounds} 轮**未收敛**（"
                             + (c.Base.BaselineTolAmplified ? "到不动点估计" : "欠松弛步（**历史口径**）")
                             + $" {baseDmax:0.00} K ≥ 容差 {baseTolK:0.###}）；";
            for (int i = 0; i < c.SegmentCount; i++)
                baseline[i] = br is { Ok: true }
                            ? (br.Segments[i].TRootAC, br.Segments[i].TRootBC)
                            : (double.NaN, double.NaN);
            // ★ 基线算不出来时必须**说出原因**：否则 ③ 只会显示 NaN，
            //   而查不到是哪一步挂了（这条判据一度因此被当成「通过」）。
            if (br is not { Ok: true })
                baseFailMsg = br?.Message ?? "基线子解未返回结果";
            // 回写缓存：同一 LineCase 再被调用时不必重算（外层搜索靠这个提速 5 倍）
            c.BaselineRootC = baseline.Select(b => new[] { b.A, b.B }).ToArray();
        }

        // ── 热启动：把上一次同类算例的收敛状态当起点（见 LineCase.WarmStart）
        (double L, double R)[]? warmDraw = null, warmNb = null;
        bool warmOk = c.WarmStart.Length >= c.SegmentCount;
        for (int i = 0; warmOk && i < c.SegmentCount; i++)
            if (c.WarmStart[i].Length < 4 || double.IsNaN(c.WarmStart[i][0]) || double.IsNaN(c.WarmStart[i][1]))
                warmOk = false;      // 抽热两位必须是数；端温两位允许 NaN（整线两头本来就没有邻段）
        if (warmOk)
        {
            warmDraw = new (double, double)[c.SegmentCount];
            warmNb = new (double, double)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                warmDraw[i] = (c.WarmStart[i][0], c.WarmStart[i][1]);
                warmNb[i] = (c.WarmStart[i][2], c.WarmStart[i][3]);
            }
        }

        var res = RunOnce(c, progress, cancel,
                          warmDraw?.Select(MeanDraw).ToArray(),
                          warmDraw, warmNb, baseline);
        if (!res.Ok) { AddGradeNote(c, res); return res; }
        if (baseFailMsg.Length > 0) res.Notes.Add("★ 无法兰基线失败 ⇒ 判据③无法判定：" + baseFailMsg);

        // ── 外层耦合：段 ↔ 法兰。首轮段解用抽热 0，拿到壳温度场后回灌重解。
        //
        // ★ 必须**欠松弛**。这个不动点自带正反馈：法兰热 ⇒ 向管根倒灌 ⇒ 管根更热 ⇒
        //   法兰边界温度更高 ⇒ 法兰更热。裸 Picard（ω=1）在该反馈下发散，
        //   现役几何上实测三轮后管根温差还有 204 K，输出的每个数都不可信 ——
        //   而那正是曾被读成「模型判现役设备烧断」的那批数。
        //   欠松弛不改变不动点，只改变到达方式：**若加了松弛仍发散，那才是物理上的热失控**。
        // ★★★★★ 自适应欠松弛（2026-08-15）。
        //
        // 依据（实测，非推测）：去掉度量假象后残差是干净的几何慢模式，
        //   0.888 → 0.355 用 55 轮 ⇒ r ≈ 0.984（ω=0.35）
        //   ⇒ 裸 Picard 增益 g = 1 − (1−r)/ω ≈ **0.954**
        // ⚠ 这本身是个**物理结论**：段↔法兰热耦合的环路增益 0.95，
        //   离热失控（g=1）只差 5 %。ω=0.35 不是保守，是把 0.954 拖成 0.984。
        //
        // ⇒ ω 自适应：连续几轮单调下降且比值 >0.9（典型慢模式）就放大 ω；
        //   残差一反弹就减半退回。**自限**，且最坏情形退化回原来的 0.35。
        double omega = c.CoupleRelax;
        double omegaMax = 1.8, omegaMin = 0.15;
        // ★ 残差轨迹：判「收敛到精度地板」还是「极限环」要靠它。
        //   单看最终 delta 分不清 —— 前者单调衰减后压平，后者上下摆。
        //   这两种病的处置完全相反（前者收紧内层容差，后者降 ω 或找非光滑环节）。
        var deltaTrace = new List<double>();
        var jumpReports = new List<string>();
        double[]? prevDraws = null; (double L, double R)[]? prevLR = null;
        double rEst = 0.0; int ratioOk = 0, omegaBoosts = 0, omegaCuts = 0;
        double lastRemainK = double.NaN;   // R48 续（2026-09-14，Opus 5）：到不动点的剩余误差估计，逐轮跟踪，结束时写进结果
        double lastTolK = double.NaN; int lastRounds = 0;   // R48 L（2026-09-17，Opus 5）：本次实际用的容差与轮数 —— 收紧的成本要能并列成表
        double[]? draws = null;
        // ★ Anderson 加速器（默认开）。它只改变到达不动点的路径，不改变不动点本身；
        //   最坏情形（安全阀连连丢弃）退化回原来的欠松弛 Picard。
        var aa = c.UseAnderson ? new Anderson(c.AndersonDepth) : null;
        static double[] PicardStep(double[] x, double[] g, double w)
        {
            var y = new double[x.Length];
            for (int i = 0; i < x.Length; i++) y[i] = x[i] + w * (g[i] - x[i]);
            return y;
        }
        // 热启动的状态直接当迭代起点；冷启动时仍是 null，由循环内 ??= 补零
        (double L, double R)[]? drawsLR = warmDraw is null ? null : ((double L, double R)[])warmDraw.Clone();
        (double L, double R)[]? nbT = warmNb is null ? null : ((double L, double R)[])warmNb.Clone();
        double delta = double.NaN;
        double resKLast = double.NaN;   // 末轮真残差，供未收敛报告用
        for (int outer = 0; outer < c.CoupleMaxRounds; outer++)
        {
            cancel.ThrowIfCancellationRequested();
            var target = new double[c.SegmentCount];
            var targetLR = new (double L, double R)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                // 段 i 的两端分别是法兰 i 与 i+1，各贡献自己的抽热
                double a = res.Flanges[i].QFromTubeW, b = res.Flanges[i + 1].QFromTubeW;
                target[i] = 0.5 * (a + b);           // 仅作兼容/汇报用
                // ★★★★★ 能量守恒（2026-08-28）：内部共用片两侧各半、端片整份 —— 分配规则与原注释搬进 SegmentEndDraws
                //   （R48 E 审查修改 2026-09-15 Opus 5：纯搬移，保温搜索扰动单片抽热时调同一份）。
                targetLR[i] = SegmentEndDraws(c, i, a, b);   // ★ 两端各自回灌（原来取平均是另一个 bug，已修）
            }
            draws ??= new double[c.SegmentCount];
            drawsLR ??= new (double, double)[c.SegmentCount];

            // ── 段间端温：段 i 的左邻是段 i−1 的**右**端，右邻是段 i+1 的**左**端。
            //    整线两头没有邻段 ⇒ NaN（退化为纯法兰抽热边界）。
            var nbNew = NeighbourTempsOf(res.Segments);   // R48 E（2026-09-15 Opus 5）：定义搬进 NeighbourTempsOf，纯搬移
            nbT ??= nbNew;
            // 真残差要用**步之前**的 x，故先快照（写回之后 nbT 已经是 x⁺ 了）
            var nbTOld = ((double L, double R)[])nbT.Clone();

            // ★★★★★ 一步不动点迭代：x ← G(x)，x =（各段两端抽热，各段两侧邻段端温）
            //
            // 抽热与端温**必须放进同一个状态向量**一起加速：慢模式正是这两者
            // 耦合起来的那个方向 —— 早先的 Aitken 只作用在抽热向量上，实测无效
            // （残差被推下去又被拉回），HANDOVER §1.85 已记，原因就在这里。
            //
            // NaN 位（整线两头没有邻段）在整个迭代中位置固定，直接跳过不入向量。
            var slotSeg = new List<int>(); var slotIsDraw = new List<bool>();
            var slotIsL = new List<bool>();
            for (int i = 0; i < c.SegmentCount; i++)
            {
                slotSeg.Add(i); slotIsDraw.Add(true); slotIsL.Add(true);
                slotSeg.Add(i); slotIsDraw.Add(true); slotIsL.Add(false);
                if (!double.IsNaN(nbNew[i].L)) { slotSeg.Add(i); slotIsDraw.Add(false); slotIsL.Add(true); }
                if (!double.IsNaN(nbNew[i].R)) { slotSeg.Add(i); slotIsDraw.Add(false); slotIsL.Add(false); }
            }
            int nv = slotSeg.Count;
            var xv = new double[nv]; var gv = new double[nv];
            for (int s = 0; s < nv; s++)
            {
                int i = slotSeg[s];
                if (slotIsDraw[s])
                {
                    xv[s] = slotIsL[s] ? drawsLR[i].L : drawsLR[i].R;
                    gv[s] = slotIsL[s] ? targetLR[i].L : targetLR[i].R;
                }
                else
                {
                    double cur = slotIsL[s] ? nbT[i].L : nbT[i].R;
                    double tgt = slotIsL[s] ? nbNew[i].L : nbNew[i].R;
                    xv[s] = double.IsNaN(cur) ? tgt : cur;      // 首轮直接落到目标上
                    gv[s] = tgt;
                }
            }

            double[] xn = aa is not null
                        ? aa.Step(xv, gv, omega, c.AndersonKappa)
                        : PicardStep(xv, gv, omega);

            for (int s = 0; s < nv; s++)
            {
                int i = slotSeg[s];
                if (slotIsDraw[s])
                    drawsLR[i] = slotIsL[s] ? (xn[s], drawsLR[i].R) : (drawsLR[i].L, xn[s]);
                else
                    nbT[i] = slotIsL[s] ? (xn[s], nbT[i].R) : (nbT[i].L, xn[s]);
            }
            for (int i = 0; i < c.SegmentCount; i++)
            {
                draws[i] = MeanDraw(drawsLR[i]);   // 仅作兼容/汇报（R48 E 2026-09-15 Opus 5：式子搬进 MeanDraw，管侧单解同用）
                if (double.IsNaN(nbNew[i].L)) nbT[i] = (double.NaN, nbT[i].R);
                if (double.IsNaN(nbNew[i].R)) nbT[i] = (nbT[i].L, double.NaN);
            }

            // ★★★★★ **真残差** ‖G(x) − x‖∞（温度分量，K）。2026-08-16 加，起因是一次假收敛。
            //
            // 原来的收敛判据只看「相邻两轮走了多远」δ，再乘几何放大。那套推理默认迭代是
            // **线性定常**的（纯 Picard 成立）。上了 Anderson 之后**不再成立**：
            // 外推可以让 δ→0 而 x 根本不在不动点上（Anderson 的经典停滞模式）。
            // 实测就撞上了：Anderson 报「19 轮收敛、剩余误差 0.86 K」，
            // 纯 Picard 报「收敛、剩余误差 0.65 K」，两者**管根温度差 14 K**、③ 差 12.5 K。
            // 基线完全相同 ⇒ 差的是解本身，不是基线。
            //
            // ⇒ 又一次「代理量不是原量」：δ 是残差的代理，换了迭代格式就不成立。
            //   真残差就在手边（G(x) 与 x 都是现成的），没有任何理由再用代理量。
            // 2026-09-15 Opus 5（J 路，合并把关待办 P3 第 10 条，注记）：下面跳过 NaN 是**结构性的** —— 端段外侧没有邻段，NeighbourTempsOf 在那一侧给 NaN（上面 slot 表同样按 NaN 跳过）。
            //   初值 0 的后果照实写：只有一段时两侧都是 NaN ⇒ resK 恒为 0，「真残差」那一支退化成恒过，停机只剩步长那一支。
            double resK = 0;
            for (int i = 0; i < c.SegmentCount; i++)
            {
                if (!double.IsNaN(nbNew[i].L) && !double.IsNaN(nbTOld[i].L))
                    resK = Math.Max(resK, Math.Abs(nbNew[i].L - nbTOld[i].L));
                if (!double.IsNaN(nbNew[i].R) && !double.IsNaN(nbTOld[i].R))
                    resK = Math.Max(resK, Math.Abs(nbNew[i].R - nbTOld[i].R));
            }

            // ★★★★★ Aitken Δ² 外推（2026-08-15）：专治**慢模式**。
            //
            // 残差轨迹实测：快模式衰完后进入 r ≈ 0.9855 的慢模式，
            // 0.669 → 0.352 用了 44 轮 ⇒ 按几何外推，当前解距不动点还有
            //   Δ∞ ≈ 0.352·r/(1−r) ≈ **24 K** —— 根本没收敛。
            // 反推裸 Picard 增益 g = 1 − (1−r)/ω ≈ 0.959 ⇒ **欠松弛在这里帮倒忙**：
            //   ω=0.35 把速率从 0.959 拖慢到 0.986。ω 是为很久以前那个会发散的构型加的。
            //
            // ⚠ 不直接把 ω 调大：那个「会发散」的构型可能还会回来（薄壁 + 高压接温度）。
            //   改成**保留 ω，另加外推**：只有当残差连续 3 轮单调下降且比值稳定在
            //   (0.6, 0.999) 时才外推一步，外推倍数封顶 —— 不满足就退回原来的行为。
            // （曾在此加 Aitken Δ² 外推，实测**无效且制造毛刺**：残差被推下去又被拉回、
            //   并出现 0.970 / 1.116 的反弹 ⇒ 慢模式不在抽热向量上。已移除，改用自适应 ω。）

            progress?.Report($"外层耦合 {outer + 1}/{c.CoupleMaxRounds}（ω={omega:0.00}）：回灌法兰抽热 + 段间端温…");
            var next = RunOnce(c, progress, cancel, (double[])draws.Clone(),
                               ((double L, double R)[])drawsLR.Clone(),
                               ((double L, double R)[])nbT.Clone(), baseline);
            if (!next.Ok) { AddGradeNote(c, next); return next; }   // R48 物性接线：耦合中途失败也带牌号说明（纯铂不加）
            // ★★★★★ 收敛度量必须**无分支**（2026-08-15）。
            //
            // 原来用 `TRootC` 这一个标量 —— 它在两端之间会**切换报哪一端**。
            // 实测（跳变捕捉）：轮 6 与轮 15 各出现 −21.4 K / −51.8 K 的「残差跳变」，
            // 而同一轮里 **电流没变、抽热没变、两端管根各自只动了 0.1–0.9 K**：
            //     seg3 两端 1061.1/1039.8 → 1060.2/1039.7，而 TRootC 报 1061.1 → 1039.7
            // ⇒ 跳的只有标量本身，物理场一直光滑。**那两次跳变是度量的假象。**
            //
            // 这是「代理量不是原量」在本项目的第三次发作
            //（前两次：拿段内最大偏差当管根、拿 B 当 ③ 的控制靶）。
            // ⇒ 对**两端各自**判，不再经过任何会切换分支的标量。
            // ★ 2026-09-15 Opus 5（J 路，合并把关待办 P3 第 10 条）：步长这一段原来**跳过 NaN、初值 0** ——
            //   有段管根算不出（NaN）时步长照样可以是 0 ⇒ 下面「收敛」那一句照样成立。现在记下来，有 NaN 就不许判收敛（判不了不算过）；步长本身的算法不动。
            // ★ 2026-09-17 Opus 5（J 路，J6/J7 复核「应修 3」）：两处改口径 —— 一、这不是注记，是**判定口径**（Converged=false ⇒ 下游 TierUnusableWhy／Gate 全线判不了）⇒
            //   步长与收敛判定都提成公开函数（CoupleRootStep／CoupleConverged），行为门调同一份（见 R48J_SolverMeshAndMarkerGateTests.P3_10_*）。
            //   二、上一版这里断言「管根两端不是结构性 NaN —— 每段两端都有管根」，**没有实测支撑**（TRootAC 缺省 0、由 SegmentResult.TFlangeAC 赋值，赋不上时是什么没有量过）⇒ 撤回这句断言。
            //   现在的写法对「结构性还是偶发」不作任何前提：出了 NaN 就是判不了，不判收敛。
            var step = CoupleRootStep(res.Segments, next.Segments, c.SegmentCount);
            delta = step.Delta;
            bool rootNaN = step.RootNaN;
            deltaTrace.Add(delta);
            resKLast = resK;
            // ★ 跳变捕捉：残差突然放大 5 倍以上 = 有离散量在翻。
            //   把当轮与上一轮的**全部状态**并排打出来，让它自己说是什么翻了 ——
            //   继续猜「大概是 XX 开关」已经错过太多次（§1.8）。
            if (deltaTrace.Count >= 2 && delta > 5 * deltaTrace[^2] && delta > 1.0)
                jumpReports.Add(
                    $"轮{outer + 1} 残差 {deltaTrace[^2]:0.00}→{delta:0.00}｜" +
                    $"管根 {string.Join(",", res.Segments.Select(sg => sg.TRootC.ToString("0.0")))}" +
                    $" → {string.Join(",", next.Segments.Select(sg => sg.TRootC.ToString("0.0")))}｜" +
                    $"电流 {string.Join(",", res.Segments.Select(sg => sg.CurrentA.ToString("0")))}" +
                    $" → {string.Join(",", next.Segments.Select(sg => sg.CurrentA.ToString("0")))}｜" +
                    $"抽热 {string.Join(",", res.Flanges.Select(f => f.QFromTubeW.ToString("+0;−0")))}" +
                    $" → {string.Join(",", next.Flanges.Select(f => f.QFromTubeW.ToString("+0;−0")))}｜" +
                    $"两端管根 {string.Join(",", res.Segments.Select(sg => $"{sg.TRootAC:0.0}/{sg.TRootBC:0.0}"))}" +
                    $" → {string.Join(",", next.Segments.Select(sg => $"{sg.TRootAC:0.0}/{sg.TRootBC:0.0}"))}");
            // 残差比值：只在**单调下降且比值稳定**时才认为是几何慢模式
            if (deltaTrace.Count >= 2)
            {
                double dPrev = deltaTrace[^2];
                double r = dPrev > 1e-12 ? delta / dPrev : 0.0;
                if (r > 0.9 && r < 0.999) { rEst = 0.5 * rEst + 0.5 * r; ratioOk++; }
                else ratioOk = 0;
                // ★★ ω 自适应**只在没走成 Anderson 步的那些轮**生效（2026-08-16）。
                //   Anderson 步会让残差**非单调**——那是外推的正常表现，不是发散信号。
                //   而原来的规则见到 r>1 就把 ω 减半 ⇒ ω 一路压到下限 0.15
                //   ⇒ 兜底的 Picard 步变得极小 ⇒ 残差几乎不动 ⇒ ΔF 又小又噪
                //   ⇒ 最小二乘病态 ⇒ AA 步被阀门打掉 ⇒ 更依赖 ω…… **自锁**。
                //   实测：难工况 ω 回退 38–39 次、AA 丢弃 139–141 次、末端深度恒为 0。
                //   ⇒ ω 只管兜底那条路；Anderson 走通时不动它。
                bool aaTook = aa is not null && aa.LastAccepted;
                if (!aaTook)
                {
                    if (r > 1.0) { omega = Math.Max(omegaMin, 0.5 * omega); ratioOk = 0; omegaCuts++; }
                    else if (ratioOk >= 4)
                    { omega = Math.Min(omegaMax, 1.5 * omega); ratioOk = 0; omegaBoosts++; }
                }
            }
            res = next;
            // ★★★★★ 收敛判据改成**距不动点的估计**，不是「这一步走了多远」（2026-08-15）。
            //
            // δ 只说明本轮迈了多大一步；环路增益 g≈0.96 时，剩余误差是
            //     Δ∞ ≈ δ·r/(1−r)  ≈ 25 δ
            // 实测就是这么被骗的：δ=1.36 时判「5 轮收敛」，而真实剩余误差约 34 K。
            // ⚠⚠ 第一版在这里写「r 估不出来时退回用 δ 本身」，理由是
            //   「那只发生在快模式阶段，δ 本身很大，不会误判为收敛」——**这个理由是错的**：
            //   容差放到 1.0 之后，快模式窗口里的 δ=0.90 就够小了，于是它在**第 6 轮**
            //   宣布收敛 —— 和被修掉的那个假收敛**同一个位置**。（今天第四次「修一个漏一个」。）
            // ⇒ **没量到 r 时，按已知最坏放大取**，而不是当作没有放大。
            //   实测 g≈0.96 ⇒ 放大 1/(1−g) ≈ 25。这迫使迭代真的走进慢模式、把 r 量出来。
            // ★★★★★ R48 L（2026-09-16，Opus 5；数值把关人 2026-09-16 定口径）：
            //   放大倍数**不再写死 25** —— 25 是按整线环路增益 0.96 定的常数，而这里被放大的
            //   δ 与真残差都是**段间端温**的残差，它的收缩比是 ρ = 1/(1 + Δx/ℓt)，
            //   放大 1/(1 − ρ) = 1 + ℓt/Δx（当场按两端管根温度算，取两端里大的）。
            //   实测本算例闭式 77～88：1150 °C／夹头 100 °C 那点，25 口径报「剩余误差 0.946 K < 容差 1 K，已收敛」，
            //   闭式口径是 3.039 K > 1 K —— **那个「已收敛」是假的**
            //   （deliverable\R48_L_升温管段伸长探针_本次开跑于2026-09-16_205421.txt，14 点每点都印了对照）。
            //   开关见 LineCase.EndTempAmpFromDecayLength（关掉 = 退回 25 的历史口径，门盯着）。
            // ★★★★★ R48 M（2026-09-18，Fable 5.1）：放大 = max(闭式 × 1.1, 实测雅可比)（StopAmpOf，唯一来源，别在这里另写一个数）。
            //   **rEst 那一支删了**（HANDOVER 数值常驻项）：原来「量得到 r 就用 r/(1−r)，量不到才用闭式」—— 而 r 是从 Anderson 步的残差轨迹估的，
            //   Anderson 让残差天然非单调，r 估出来不是几何收缩比；拿它当放大既可能低估也可能高估，且每轮换一个数 ⇒ 停机口径随轮漂。
            //   现在放大只由两份**与轨迹无关**的量定：闭式（段解自己报）与一次量好的雅可比。rEst 只剩两个用途：ω 自适应、未收敛时「还需约几轮」的估计（说明文字，不进判定）。
            double ampWorst = StopAmpOf(c, res, ampJac);
            double amp = ampWorst;
            double remain = delta * amp;
            // ★★★★★ R48 L（2026-09-17，Opus 5）：**停机容差按本轮判据的裕度当场算**（唯一读口 CoupleTolKFor）。
            //   为什么必须逐轮算而不是进环前算一次：裕度是判据表里的数，而判据表每一轮都随场重算 ——
            //   进环前那一份是「首轮那个还没收敛的场」上的裕度，拿它定容差就是拿噪声定分辨率。
            //   实测依据见 LineCase.CoupleTolK 的注释（1 ulp ⇒ 管根动 0.374 K，裕度只有 0.489 K）。
            double tolNow = CoupleTolKFor(c, res);
            double marginNow = MinHardTempMarginK(res);
            // （lastRemainK 在下面 resOk 算完后才写：停机要两支都过，距离估计取两支里大的）
            // ⚠ 两条**都**要过：δ 那条防「步子还很大」，真残差那条防「步子小但不在不动点上」。
            //   放大取已知最坏 25（= 1/(1−g)，g≈0.96）——真残差乘它才是到不动点的距离。
            // ★ 2026-09-18 Opus 5（合并 J×L）：真残差那支与步长那支一起写在 CoupleConverged 里（同一份）；
            //   放大用**当场算的 ampWorst**（L 路口径），不是 CoupleConverged 原先手抄的常数 25 —— 25 会把距离低估，见上面 L 路那段实测。
            // ★ R48 续（2026-09-14，Opus 5；常驻数值讨论人查出）：停机时步长那支与真残差那支**都要过**，
            //   所以到不动点的距离估计取两支里大的，不能只记步长那支。
            lastRemainK = Math.Max(remain, resK * ampWorst);
            lastTolK = tolNow; lastRounds = outer + 1;
            c.CoupleTrace?.Invoke(outer + 1, res, delta, resK, remain, amp, omega, aa is null ? "" : aa.Report());
            // 2026-09-15 Opus 5（J 路，P3-10）：管根有 NaN 不许判收敛。2026-09-17（J7/J6 复核「应修 3」）：判定口径提成 CoupleConverged，行为门调同一份。
            // ★ 2026-09-18 Opus 5（合并 J×L）：容差由 c.CoupleTolK（常数上限）换成本轮当场算的 tolNow（L 路 CoupleTolKFor），放大由常数 25 换成 ampWorst ——
            //   两者都是 L 路已经落地的口径，J 路那份是在 L 之前写的；合并后三条判定（步长、真残差、管根 NaN）全在 CoupleConverged 一处。
            if (CoupleConverged(remain, resK, tolNow, rootNaN, ampWorst))
            {
                res.Notes.Add($"外层耦合 {outer + 1} 轮收敛（剩余误差估计 {remain:0.000} K = 步长 {delta:0.000} × 放大 {amp:0.0}，真残差 {resK:0.000} K，"
                              // R48 M（2026-09-18，Fable 5.1）：放大的两个来源与认证误差都印出来 —— 报告引用判据值时要能指回「这个数还会晃多少」
                              + $"放大 = max(闭式 {EndTempFixedPointAmpOf(c, res):0.0} × {StopAmpHeadroom:0.0}, 实测雅可比 {(double.IsNaN(ampJac) ? "量不出来" : ampJac.ToString("0.0"))})，"
                              + $"**认证误差 {Math.Max(remain, resK * ampWorst):0.000} K**，"
                              + $"**容差 {tolNow:0.000} K**"
                              // ⚠ 三种情形要分开说（不许混成一句「常数口径」）：没开开关（生产口径：绝对目标）/ 开了但本工况没有温度判据可读 / 真的按裕度收紧了（注射口径）
                              + (!c.CoupleTolFromMargin
                                 ? "（绝对目标，不随判据裕度走）"
                                 : double.IsNaN(marginNow)
                                 ? "（**开着**按判据裕度收紧，但本工况没有「硬安全线 + 温度」判据可读 ⇒ 取上限）"
                                 : $" = min(上限 {c.CoupleTolK:0.###}, max(下限 {c.CoupleTolFloorK:0.###}, {c.CoupleTolMarginFrac:0.##} × 最小硬安全线温度裕度 {marginNow:0.###}))")
                              + $"，ω={omega:0.00}，ω 末值 {omega:0.00}／放大 {omegaBoosts} 次／回退 {omegaCuts} 次）"
                              + (aa is null ? "" : "　" + aa.Report()));
                res.Converged = true;
                // ★ 回写热启动状态：**只在收敛时写**。没收敛的状态不是不动点，
                //   拿它去热启动下一轮，等于把一次失败的迭代当成了经验（会连环放大）。
                if (drawsLR is not null && nbT is not null)
                    c.WarmStart = Enumerable.Range(0, c.SegmentCount)
                        .Select(i => new[] { drawsLR[i].L, drawsLR[i].R, nbT[i].L, nbT[i].R })
                        .ToArray();
                break;
            }
        }
        // ★ R48 续（2026-09-14，Opus 5；常驻数值讨论人列为「重解前必做」）：**剩余误差估计必须是个数，不能只活在说明文字里。**
        //   加密复核要拿它区分「判据在摆是网格造成的」还是「是外层耦合停机噪声造成的」——
        //   实测 ③ 最后一步变化 +0.820 K，**小于**耦合停机容差 1.0 K，两者分不开，而此前每档的这个数一次都没记下来。
        // ★★★★★ 决 103（2026-09-24；决 99 选项 A）：局部热稳定换成**全格精算**（只对终局那一份场，四片各一次；改回口径不做）。代价进结果与说明。
        ApplyLocalStabFullGrid(c, res);
        res.CoupleRemainK = lastRemainK;
        res.CoupleRounds = lastRounds; res.CoupleTolKUsed = lastTolK;   // R48 L（2026-09-17，Opus 5）：成本（轮数）与本次容差进结果，报告直接读
        // R48 M（2026-09-18，Fable 5.1）：放大的两个来源与量雅可比的成本进结果（新状态位默认没接上 —— 报告与门从这里读）
        res.CoupleAmpClosed = EndTempFixedPointAmpOf(c, res); res.CoupleAmpJacobian = ampJac; res.CoupleAmpUsed = StopAmpOf(c, res, ampJac);
        res.JacobianAmpSec = ampJacSec;
        res.Notes.Add($"★ 停机放大口径：max(闭式 {res.CoupleAmpClosed:0.0} × {StopAmpHeadroom:0.0}, 实测雅可比 "
                      + (double.IsNaN(ampJac) ? "量不出来（只用闭式 × 1.1）" : $"{ampJac:0.0}") + $") = {res.CoupleAmpUsed:0.0}"
                      + (ampJacSec > 0 ? $"；量雅可比用时 {ampJacSec:0.0} s" : "；雅可比读缓存"));
        if (res.Converged)
        {
            // ★★★★★ R48 M（2026-09-18，Fable 5.1）：裕度比认证误差还小的硬安全线**判不了那么细**（判不了不算过）
            var inside = MarkUndeterminedIfInsideCertErr(res);
            if (inside.Length > 0)
                res.Notes.Add($"★ 判不了那么细：{string.Join("、", inside)} 的裕度小于认证误差 {res.CertErrK:0.000} K —— 既不当过也不当不过（要判得了得把这一点解得更细）");
        }
        if (!res.Converged)
        {
            if (aa is not null) res.Notes.Add("★ " + aa.Report());
            res.Notes.Add($"★ ω 末值 {omega:0.00}（放大 {omegaBoosts} 次／回退 {omegaCuts} 次），末端比值 r={rEst:0.000}" +
                          $"　⇒ 裸 Picard 增益 g≈{1 - (1 - rEst) / Math.Max(1e-9, omega):0.000}（g→1 即热失控）");
            foreach (var jr in jumpReports) res.Notes.Add("★ 跳变 " + jr);
            res.Notes.Add("★ 残差轨迹 " + string.Join(" ", deltaTrace.Select(v => v.ToString("0.000"))));
            // ★★ 「未收敛」要分成两件事说，因为**对策完全不同**（2026-08-16 实测所得）：
            //     ① 还在单调收缩、只是慢  ⇒ 加轮数就行，模型没问题
            //     ② 残差不降甚至在涨      ⇒ 该工况可能真的热失控，加轮数没用
            //   实测：偏离设计记录点的「管保温 1 mm」档，200 轮报未收敛（剩余误差 76.9 K），
            //   **只把轮数上限提到 1000、其余一律不动，第 734 轮收敛，剩余误差 0.99 K**。
            //   ⇒ 那一档从来不是发散，是轮数不够。原来这条提示把两种情形并列写成
            //     「要么加轮数、要么热失控」，等于把判断推给读的人 —— 而判据本身就该给出三态。
            // ⚠ 「还在不在收缩」不能拿**单点**比单点：Anderson 步让残差天然带毛刺，
            //   实测「盘半径 28」档残差 36.91 → 0.081（明明在收缩）却被判成「没在收缩」，
            //   只因为末点恰好落在一个尖峰上。⇒ 比**两段窗口的最小值**，对毛刺免疫。
            int win = Math.Min(8, deltaTrace.Count / 2);
            bool shrinking = false;
            if (win >= 3)
            {
                double recent = double.MaxValue, older = double.MaxValue;
                for (int k = 0; k < win; k++)
                {
                    recent = Math.Min(recent, deltaTrace[^(k + 1)]);
                    older = Math.Min(older, deltaTrace[^(win + k + 1)]);
                }
                shrinking = recent < older * 0.995;
            }
            // R48 L（2026-09-16，Opus 5）：这里原来也写死 25 —— 与上面停机那一处是同一件事，一起换成当场算的闭式。
            // R48 M（2026-09-18，Fable 5.1）：与主环停机同一份放大（StopAmpOf），rEst 那一支已删（见主环那段注记）
            double ampNow = StopAmpOf(c, res, ampJac);
            // R48 L（2026-09-17，Opus 5）：未收敛报告里的容差也是**同一个读口**算出来的那一个（末轮那份裕度），不许在这里另抄 c.CoupleTolK。
            double tolLast = double.IsNaN(lastTolK) ? CoupleTolKFor(c, res) : lastTolK;
            // 还要多少轮：按几何收缩 δ·r^n·amp < tol 解 n
            string need = "";
            if (shrinking && rEst > 0.5 && rEst < 0.999 && delta > 0)
            {
                double n = Math.Log(tolLast / (delta * ampNow)) / Math.Log(rEst);
                if (n > 0 && n < 1e6) need = $"，按当前收缩率还需约 **{Math.Ceiling(n):0} 轮**";
            }
            res.Notes.Add($"★ 外层耦合 {c.CoupleMaxRounds} 轮未收敛（**剩余误差估计 {delta * ampNow:0.0} K**，" +
                          $"步长 {delta:0.0} K，真残差 {resKLast:0.000} K，**容差 {tolLast:0.000} K**，ω={omega:0.00}）—— 本次结果的每个数都不可用。");
            // ★ R48 L（2026-09-16，Opus 5）：放大到 80 倍上下时，步长要压到 容差/放大 才停得下来，600 轮可能不到 ——
            //   那是**分辨率地板**，不是热失控。判词如实点名（判不了），并印当时的步长、真残差、轮数。
            string? floor = ResolutionFloorNote(delta, resKLast, tolLast, ampNow, c.CoupleMaxRounds);
            if (floor is not null) res.Notes.Add(floor);
            res.Notes.Add(shrinking
                ? $"★ 残差**仍在单调收缩** ⇒ 是「慢」不是「发散」：加轮数上限即可{need}" +
                  $"（LineCase.CoupleMaxRounds，本次 {c.CoupleMaxRounds}）。"
                : floor is not null
                ? "★ 残差**没有在收缩**，但步长与真残差都已在容差以内 ⇒ 按上一条判词：分辨率地板，不是热失控。"
                : "★ 残差**没有在收缩** ⇒ 加轮数大概率没用：该工况可能真的热失控，或迭代进了极限环。");
            res.Message = "段↔法兰耦合未收敛（" + (shrinking ? "慢，加轮数可解" : "未收缩，疑似失控") + "）";
        }
        AddGradeNote(c, res);   // R48 物性接线（2026-09-23，Opus 5.5）：纯铂不加
        return res;
    }

    /// <summary>把无法兰基线写进结果，并算出**法兰造成的增量温降**（两端取较差者）。</summary>
    private static void ApplyBaseline(LineResult r, (double A, double B)[] baseline)
    {
        for (int i = 0; i < r.Segments.Length && i < baseline.Length; i++)
        {
            var s = r.Segments[i];
            s.BaseTRootAC = baseline[i].A; s.BaseTRootBC = baseline[i].B;
            double dA = baseline[i].A - s.TRootAC, dB = baseline[i].B - s.TRootBC;
            // ★ 两端各自留着（2026-08-28）：只留 max 就说不出「是哪一片的责任」，
            //   而逐片解必须知道该抬**哪一片**的舌保温。
            s.FlangeDipAK = dA; s.FlangeDipBK = dB;
            s.FlangeDipK = double.IsNaN(dA) || double.IsNaN(dB)
                         ? double.NaN : Math.Max(dA, dB);
        }
    }

    private static LineResult RunOnce(LineCase c, IProgress<string>? progress,
                                      CancellationToken cancel, double[]? drawIn,
                                      (double L, double R)[]? drawLR = null,
                                      (double L, double R)[]? nbT = null,
                                      (double A, double B)[]? baseline = null)
    {
        var res = new LineResult { BaselineMassG = c.BaselineMassG, RampChecked = c.CheckRamp, EmptyTube = c.EmptyTube, RuleSet = c.RuleSet };   // K 路（2026-09-15 Opus 5）：工况；决 103：判据口径位写进结果，必备名单按它取
        int n = c.SegmentCount, nf = c.FlangeCount;
        // ★ R48 E 审查修改（2026-09-15 Opus 5）：算例前置检查搬进 CaseGuardMessage（管侧单解 SolveTubeSegments 共用；纯搬移，文字逐字不变）。
        string guard = CaseGuardMessage(c);
        if (guard.Length > 0) { res.Ok = false; res.Message = guard; return res; }

        // 各段两端的法兰抽热 W（由壳温度场回灌）。首轮未知，置 0；
        // ★ 必须显式回灌：不设 FlangeDrawOverrideSet 时 SegmentSolver 会**静默回退到
        //   已作废的一维环形模型 FlangeRadial**（§5、§7 记过两次，这是第三次）。
        var drawW = drawIn ?? new double[n];

        // ── ①② 逐段
        // ★ R48 E 审查修改（2026-09-15 Opus 5）：逐段循环原样搬进 SolveSegmentsInto —— 保温搜索量「管根对本片抽热的响应」要只解段、
        //   不解法兰（审查意见：γ 要是当前选择的局部响应，不许锚在无出处的上界角上）。纯搬移，整线全量转储前后逐位相同为证。
        if (!SolveSegmentsInto(c, res, progress, cancel, drawW, drawLR, nbT, out var amps, out var segParams)) return res;
        var segs = res.Segments;

        // ── ③ 逐片法兰
        var flanges = new FlangeOut[nf];
        string flangeName(int jj) => jj == 0 ? "入口" : jj >= n ? "出口" : $"{segs[jj - 1].Name}|{segs[jj].Name}";
        for (int j = 0; j < nf; j++)
        {
            cancel.ThrowIfCancellationRequested();
            ThicknessField? fieldUsed = null;          // 图纸路径本片用的厚度场（推切点用）
            ThicknessField? fieldRaw = null;           // 2026-09-23（§0.-20）：缩放之前的那一份（LoadThickness 缓存／LineCase.FlangeFields 里的同一实例，各轮不变）——孔径核对读它，按实例缓存
            bool analytic = c.FlangePlates.Length > 0;
            var plate = analytic ? c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)] : null;
            bool inMemField = !analytic && c.FlangeFields.Length > 0;      // R47 B：内存厚度场优先于文件
            string file = analytic || inMemField ? "" : c.FlangeFile3dm[Math.Min(j, c.FlangeFile3dm.Length - 1)];
            double planeY = j < c.FlangePlaneY.Length ? c.FlangePlaneY[j] : double.NaN;
            progress?.Report(analytic
                ? $"法兰 {j + 1}/{nf}：解析几何 + 建网格 + 解场…"
                : $"法兰 {j + 1}/{nf}：提厚度场 + 建网格 + 解场…");

            double holeR = c.TubeIdMm * 0.5 + c.WallMm;
            ShellMesh mesh;
            if (analytic)
            {
                // ★ R48 E（2026-09-15 Opus 5）：解析路径的建网格搬进 PlateMeshAnalyticCore（纯搬移，数逐位不变）——
                //   保温搜索求解器（InsulationSearch）逐片单解要同一张网格，不许手抄配方。
                mesh = PlateMeshAnalyticCore(c, j);
            }
            else
            {
                // ★ R47 D（2026-09-13）：栅格步长跟着网格走 —— 取 min(ThicknessStepMm, 最细网格/4)。
                //   隔离实验 V4b：h×0.25 上场步 1 给 22.67 W、场步 0.25 给 −0.66 W，1 mm 栅格在细网格上本身是 23 W 级因素。
                //   LoadThickness 的缓存键含 step，不会重复探同一档。
                //   ★ R47 复修 M4：文件路径的栅格步有地板 0.05 mm（FlangeMesher.RasterStepFloorMm，依据写在那里：探针耗时 ∝ 步⁻²），
                //   到了地板才写「已到图纸分辨率」；内存场（FlangeFields）的栅格步是给定的、收不了，网格比它细同样算到了分辨率。
                double hFinest = c.MeshInnerMm > 1e-9 ? Math.Min(c.MeshFineMm, c.MeshInnerMm) : c.MeshFineMm;
                double stepWanted = Math.Min(c.ThicknessStepMm, FlangeMesher.RasterStepForFile(hFinest));
                var tf = inMemField
                       ? c.FlangeFields[Math.Min(j, c.FlangeFields.Length - 1)]
                       : Geometry3dm.LoadThickness(file, c.FlangeLayer, planeY, stepWanted);
                // ★ R47 复修 M3：「已到图纸分辨率」**不许就地追加到 tf.Warning** —— tf 是共享的（LoadThickness 缓存／
                //   LineCase.FlangeFields 同一实例），多片、多档、多次 Run 会越滚越长。只写进本次 res.Notes，按片去重。
                string resNote = inMemField
                    ? (hFinest < tf.Step - 1e-9
                        ? $"已到图纸分辨率：网格 {hFinest:0.###} mm 比厚度场栅格步 {tf.Step:0.###} mm 还细（内存厚度场的栅格步是给定的），再加密网格也分不出更多几何。"
                        : "")
                    : (FlangeMesher.RasterAtFloor(hFinest)
                        ? $"已到图纸分辨率：网格 {hFinest:0.###} mm 要的栅格步 {FlangeMesher.RasterStepFor(hFinest):0.###} mm 已低于地板 {FlangeMesher.RasterStepFloorMm:0.###} mm（Rhino 探针耗时随步长平方反比增长），栅格停在 {tf.Step:0.###} mm，再加密网格也分不出更多几何。"
                        : "");
                string warnAll = tf.Warning + (tf.Warning.Length > 0 && resNote.Length > 0 ? "　" : "") + resNote;
                if (warnAll.Length > 0 && !res.Notes.Contains("⚠ " + flangeName(j) + "：" + warnAll))
                    res.Notes.Add("⚠ " + flangeName(j) + "：" + warnAll);
                // 厚度标度：.3dm 的**形状**固定，但整体厚度可按比例缩放。
                // 这让「自动定厚」在 .3dm 模式下同样可用 —— 求出的不是绝对厚度，
                // 而是「你这张图纸的厚度要整体 ×k」，工程师照着改一版图即可。
                // t=0（无材料：轮廓外、管孔、开槽）乘任何数仍是 0，故槽与轮廓不受影响。
                double[]? lvS = j < c.LevelScale.Length ? c.LevelScale[j] : null;
                double[]? lvT = j < c.LevelThicknessMm.Length ? c.LevelThicknessMm[j] : null;
                fieldRaw = tf;
                bool perLevel = lvS is { Length: > 0 } && lvT is { Length: > 0 };
                double k = j < c.ThicknessScale.Length ? c.ThicknessScale[j] : 1.0;

                if (perLevel || Math.Abs(k - 1.0) > 1e-9)
                {
                    var scaled = new double[tf.T.Length];
                    for (int q = 0; q < tf.T.Length; q++)
                    {
                        double t0 = tf.T[q];
                        if (t0 <= 1e-6) { scaled[q] = 0; continue; }   // 无材料乘任何数仍是无材料
                        // ★ 两个标度**相乘**，不是互相覆盖：
                        //   ThicknessScale = 整片一个数（外层用它调热平衡 → 管根温差）
                        //   LevelScale     = 各级一个数（内层用它调比例 → 局部过热）
                        //   早先写成「有 LevelScale 就忽略 ThicknessScale」，
                        //   于是两层优化里外层那步完全不起作用。
                        double kk = k;
                        if (perLevel)
                        {
                            int best = 0;
                            for (int m = 1; m < lvT!.Length; m++)
                                if (Math.Abs(t0 - lvT[m]) < Math.Abs(t0 - lvT[best])) best = m;
                            kk *= lvS![Math.Min(best, lvS.Length - 1)];
                        }
                        scaled[q] = t0 * kk;
                    }
                    tf = tf.WithThickness(scaled);     // 图幅、包络、警告一并带过来（R47：包络丢了网格轴就退回栅格）
                }
                fieldUsed = tf;
                // ★ R47 D：内带参数与解析路径同口径地传进去 —— 此前图纸路径无从加密。
                mesh = FlangeMesher.BuildFromField(tf, holeR, 0,
                            c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                            c.Base.BusbarClampLengthMm,
                            c.MeshInnerMm, c.MeshInnerRadiusMm);
            }

            // ★ R48（2026-09-14 起，Opus 5）：网格生成器判出的**压接几何退化**（压接段伸进圆盘、压接段盖到管孔）接进本次输出，每片一句、按片去重（多次 Run 不越滚越长）。
            //   来历：此前只写在网格对象上、没人读（「压接长伸进了圆盘」从 R48 实验 b 起就在）；整面接触成了生产配方后，
            //   「压接长盖到了管孔 ⇒ 这块板退回只按舌端外缘接电」是换了压接模型的大事，不许只留在网格上。文字进界面输出框，写成现场工程师看得懂的话。
            // ★ 2026-09-15 Opus 5（数值把关人第十四轮；审查意见 minor「旧注释与现做法相反」改写本段）：**读网格上的布尔位**（ShellMesh.ClampIntoDisc／ClampCoversHole），
            //   不读 ClampAnchorNote 的文字（09-14 那版按「；」切开找「⚠」，是字符串协议，生成器里改一个标点这里就瞎）。说明文字由本处按位与数生成（ClampIntoDiscText／ClampCoversHoleText）。
            //   两种退化的片另由 MarkUndeterminedIfClampCoversHole／MarkUndeterminedIfClampIntoDisc 把吃法兰场的判据判不了（此前只报一句警告，判据照常出数）。
            foreach (var line in new[]
                     {
                         mesh.ClampIntoDisc ? "⚠ " + flangeName(j) + "：" + ClampIntoDiscText(mesh) : null,
                         mesh.ClampCoversHole ? "⚠ " + flangeName(j) + "：" + ClampCoversHoleText(mesh.ClampLenMm) : null,
                     })
                if (line is not null && !res.Notes.Contains(line)) res.Notes.Add(line);
            // ★ 2026-09-23（HANDOVER §0.-20）：孔弧覆盖与图纸孔径核对进本次输出，写法同上（每片一句、按片去重）。**只量不判**：判词、判不了一个都不动（业主决定，见 §0.-20 待决定）。
            //   孔径核对只在图纸路径做（fieldRaw 非空）；解析路径孔弧按构造盖满，缺口那句不会出现。
            //   传缩放之前的场：缩放只乘有料处（t = 0 乘任何数仍是 0），空腔不变；缩放后的场每轮是新实例，传它缓存就失效（外层耦合每轮都走这里）。
            foreach (var hl in HoleArcDrawingNotes(mesh, fieldRaw, holeR))
            {
                string line = "⚠ " + flangeName(j) + "：" + hl;
                if (!res.Notes.Contains(line)) res.Notes.Add(line);
            }
            if (j == 0) { res.MeshCells = mesh.CellCount; res.MeshFineMm = c.MeshFineMm; }
            double iJoint = LineSolver.JointCurrentA(amps, j);
            if (c.GateRevertFaceHeat && !res.Notes.Contains(FaceHeatRevertNote)) res.Notes.Add(FaceHeatRevertNote);   // 2026-09-23（F3）：门用改回照实写进结果
            var sc = PlateCurrentField(c, mesh, j, iJoint);   // R48 E（2026-09-15 Opus 5）：搬进公开函数，纯搬移
            // ★ 电位场的收敛此前**没有任何人读**（2026-08-29 补）。σ(T) 内循环会重解，
            //   所以取**最后一次**的收敛状态 —— 中间那次不收敛而末次收敛，场是好的。
            bool curConverged = sc.Converged;
            double curResidual = sc.Residual; int curIterations = sc.Iterations;

            // 管根温度取相邻段中较高者（保守：抽热更大）
            // ⚠ 每片法兰贴的是**具体哪一端**，不能笼统取 TRootC（那原本恒是左端）：
            //   片 0     → 段 0 的**左**端
            //   片 j（内）→ 段 j−1 的**右**端 与 段 j 的**左**端（两者相邻，取较热者：保守）
            //   片 n     → 段 n−1 的**右**端   ← 原来错取成左端
            double tRoot = j == 0 ? segs[0].TRootAC
                         : j >= n ? segs[n - 1].TRootBC
                         : Math.Max(segs[j - 1].TRootBC, segs[j].TRootAC);

            // ★ R48（2026-09-14，Opus 5；审查意见「门手抄了逐片热解配方」）：本片 p2、铜排热导、保温分界、半径口径的组装搬进 PlateThermalInputs，
            //   壳热解调用搬进 SolvePlateThermal —— 本循环与 R48DiscInsulPerPlateGateTests 共用这一份，门里只换圆盘保温厚度。纯搬移，数逐位不变。
            var ts = PlateThermalInputs(c, j, iJoint, fieldUsed);
            double busGThis = ts.BusGWPerK, busSecCurMm2 = ts.BusSectionForCurrentMm2;
            string insulNote = ts.InsulNote; bool insulUndet = ts.InsulUndetermined;
            // ★ R48（2026-09-14，Opus 5；审查意见「程序化造的图纸路径算例静默丢值」）：判据几何带了逐片圆盘保温而热解没用上 ⇒ 写进求解备注，不静默。
            if (!analytic && c.JudgeGeomDiscInsulIgnored(j, out double discOnGeomMm, out double discUsedMm))
                res.Notes.Add($"⚠ {flangeName(j)}：判据几何上带了本片圆盘保温 {discOnGeomMm:0.0} mm，但图纸路径的圆盘保温只取图纸路径的逐片设定（没有设定则沿用整线）"
                              + $" —— 本次按 {discUsedMm:0.0} mm 算，判据几何上的值没有用上。");
            // ★★ 2026-09-23（F3）：热场发热用**发热等效 J**（面发热，全片 = I·U）；逐格局部量（盘峰／舌峰处报出的 J、局部热稳定）仍用重构 J（jLocalAPerMm2）。
            //   改回（门）时 HeatJAPerMm2 就是 JMagAPerMm2 同一个数组 ⇒ 与改前逐位相同。源码门 R48F3FaceHeatGateTests 禁止这里再把 JMagAPerMm2 当发热 J 传。
            ShellThermalResult Thermal(ShellCurrentResult cur) => SolvePlateThermal(mesh, cur.HeatJAPerMm2, tRoot, ts, jLocalAPerMm2: cur.JMagAPerMm2);
            var th = Thermal(sc);

            // ★★★★★ σ(T) 耦合（2026-08-28 第一性原理通查查出，默认**关**）。
            //
            //   ShellCurrent.Solve 的第五个形参 tempC 就是为 σ(T) 造的
            //   （sig[i] = ρe(T_ref)/ρe(T_i)），但**全仓 12 个调用点没有一个传过它**
            //   ⇒ 交付用的整线链恒按**等温**解电流场，J 分布只由厚度决定。
            //
            //   而同一片法兰上：管孔 1150 °C、压接段 450 °C（或算出来的 209–276）、
            //   舌片可超 1400 °C。ρe(450)/ρe(1150) ≈ 0.45 ⇒ **冷区更导电、电流往那头挤**，
            //   等温模型看不见这个挤 —— 而 ②″ 判的正是局部电流拥塞造成的峰值。
            //
            //   ★ 项目**做对过**：已被取代的 CoupledSolver 每轮都用新温度场重解电流场，
            //     注释写着「铂 700–1300 °C 间 ρe 变化 48 %…等温 σ 会算偏 J 分布」。
            //     重写成 ShellThermal/LineRunner 这条路时**丢掉了**。
            //
            //   ⚠ 默认关：打开会改判据的数（②″ 与局部热稳定首当其冲）。
            //     两轮足够：J 对 T 的反馈是弱的（σ 只改分布、不改总电流）。
            if (c.Base.SigmaOfTCoupling)
                for (int itSig = 0; itSig < 2; itSig++)
                {
                    var sc2 = PlateCurrentField(c, mesh, j, iJoint, tempC: th.T);   // R48 E（2026-09-15 Opus 5）：同上
                    sc = sc2; th = Thermal(sc2);
                    curConverged = sc2.Converged;
                    curResidual = sc2.Residual; curIterations = sc2.Iterations;
                }

            // 逐级峰值温度：按单元厚度归级，取该级内的最高温
            double[] lvTmax = Array.Empty<double>(), lvTh = Array.Empty<double>();
            if (j < c.LevelThicknessMm.Length && c.LevelThicknessMm[j] is { Length: > 0 } lvRef)
            {
                int L = lvRef.Length;
                lvTmax = new double[L]; lvTh = new double[L];
                var kk = j < c.LevelScale.Length && c.LevelScale[j] is { Length: > 0 } ks ? ks : null;
                for (int m = 0; m < L; m++)
                {
                    lvTmax[m] = double.NaN;
                    lvTh[m] = lvRef[m] * (kk is null ? 1.0 : kk[Math.Min(m, kk.Length - 1)]);
                }
                for (int q = 0; q < mesh.CellCount; q++)
                {
                    double tq = mesh.Thickness[q];
                    int best = 0;
                    for (int m = 1; m < L; m++)
                        if (Math.Abs(tq - lvTh[m]) < Math.Abs(tq - lvTh[best])) best = m;
                    if (double.IsNaN(lvTmax[best]) || th.T[q] > lvTmax[best]) lvTmax[best] = th.T[q];
                }
            }

            // R47 F：管孔定温环的自检（只量不判）。焊脚：解析板取它自己的，图纸路径取管壁厚（图上焊缝是画出来的料）。
            double weldLegJ = analytic ? plate!.WeldFilletLegMm : c.WallMm;
            flanges[j] = new FlangeOut
            {
                InsulBoundaryNote = insulNote, InsulBoundaryUndetermined = insulUndet,
                ClampCoversHole = mesh.ClampCoversHole,                       // R48（2026-09-15，Opus 5）：读位，见 MarkUndeterminedIfClampCoversHole
                ClampIntoDisc = mesh.ClampIntoDisc,                           // 2026-09-15 Opus 5：读位，见 MarkUndeterminedIfClampIntoDisc
                MeshRecipe = mesh.Recipe, ThermalRecipe = th.Recipe,          // R48（2026-09-15，Opus 5）：配方指纹原样带出
                HoleTagOverMm = mesh.HoleTagMaxROverMm,
                HoleTagBeyondWeldMm = mesh.HoleTagLengthBeyondMm(weldLegJ),
                LevelTMaxC = lvTmax, LevelThickMm = lvTh,
                Name = j == 0 ? "入口" : j >= n ? "出口" : $"{segs[j - 1].Name}|{segs[j].Name}",
                Shared = j > 0 && j < n,
                CurrentA = iJoint,
                MassG = mesh.VolumeMm3 * Materials.PtDensity * 1e-6,
                JMaxAPerMm2 = sc.JMaxAPerMm2,
                Phi = th.PhiOverall,
                QFromTubeW = th.QFromTubeW,
                BusGWPerK = busGThis,
                BusSectionForCurrentMm2 = busSecCurMm2,
                // ★ 导热需要的截面：A = Q·L/(k·ΔT)。要解完才知道 Q 与夹持温度 ⇒ 不进 G 那条链
                //   （那会循环），只作**一致性对账**：若它 > 载流需截面，说明按载流选的铜排
                //   带不走热，现场那根要更粗 —— 而 G 也就该更大。
                BusSectionForHeatMm2 = busGThis >= 0 && th.QToClampW > 1e-9
                                       && th.TTabEndMeanC - c.Base.BusbarSinkTempC > 1e-6
                    ? th.QToClampW * (c.Base.BusbarLenToSinkMm * 1e-3)
                      / (BusbarSizing.CuK * (th.TTabEndMeanC - c.Base.BusbarSinkTempC)) * 1e6
                    : 0,
                QGenW = th.QGenW, QLossW = th.QLossW,
                // ★ 改用壳解的**直接通量**，不再用恒等式反推 —— 否则对账是循环论证
                QClampW = th.QToClampW,
                EnergyResidualW = th.EnergyResidualW, TRootC = tRoot,
                CellGenW = th.CellGenW, CellLossW = th.CellLossW, FieldBoundaryCell = th.BoundaryCell,   // R48 G2 复审（2026-09-15 Opus 5）：节点标定用
                QGenDiscW = th.QGenDiscW, QLossDiscW = th.QLossDiscW,
                QGenTabW = th.QGenTabW, QLossTabW = th.QLossTabW,
                TDiscMeanC = th.TDiscMeanC, TTabMeanC = th.TTabMeanC,
                TDiscMaxC = th.TDiscMaxC, TTabMaxC = th.TTabMaxC,
                DiscMaxXMm = th.DiscMaxXMm, DiscMaxZMm = th.DiscMaxZMm,
                DiscMaxRMm = th.DiscMaxRMm, DiscMaxJAPerMm2 = th.DiscMaxJAPerMm2,
                DiscZoneRule = th.DiscZoneRule, InsulRule = th.InsulRule,   // R48 续：保温规则一起带出，退回按 x 时要看得见
                // R48（2026-09-14，Opus 5）：舌片区峰位 —— 判断「②″ 排除舌片」有没有把孔边也排掉
                TabMaxXMm = th.TabMaxXMm, TabMaxZMm = th.TabMaxZMm,
                TabMaxRMm = th.TabMaxRMm, TabMaxJAPerMm2 = th.TabMaxJAPerMm2,
                TabMaxThickMm = th.TabMaxThickMm,
                LocalStabMargin = th.LocalStabMargin, LocalStabRMm = th.LocalStabRMm,
                LocalStabTempC = th.LocalStabTempC, LocalStabJAPerMm2 = th.LocalStabJAPerMm2,
                LocalStabLatLenMm = th.LocalStabLatLenMm,
                LocalStabOnTab = th.LocalStabOnTab,
                LocalStabFullGrid = th.LocalStabFullGrid,   // 决 103：全格精算（惰性，整线收尾才调，见 ApplyLocalStabFullGrid）
                DiscMaxThickMm = th.DiscMaxThickMm,
                TMaxC = th.TMaxC, TMinC = th.TMinC, TTabEndC = th.TTabEndMeanC,
                AreaMm2 = mesh.TotalArea, VolumeMm3 = mesh.VolumeMm3,
                CellCount = mesh.CellCount,
                Mesh = mesh, JField = sc.JMagAPerMm2, TField = th.T, VField = sc.V,
                Source = analytic
                    ? $"解析 Ø{2 * plate!.DiscRadiusMm:0}/舌{-plate.TabEndXMm:0}/t{plate.ThicknessMm:0.00}"
                    : System.IO.Path.GetFileName(file)
            };
            if (sc.ConservationError > 1e-3)
                res.Notes.Add($"{flanges[j].Name}：电流守恒误差 {sc.ConservationError:E2}，偏大");
            // ★ 熔点护栏：拟合到 3392 °C 才反号，求解器会给出 2900 °C 的「可行解」并闭合能量账
            if (th.OverMelt)
            {
                // ★★★★★ **代码自己说「该解不存在」，就不许把它当解交出去**（2026-09-07 A）。
                //
                //   在此之前这里**只加一条 Note**：Ok 与 Converged 都留 true，
                //   而 AllOk 只看 Converged + Checks，熔点又不是 Check
                //   ⇒ 一份峰值 2900 °C 的解可以**全绿交付**。
                //   实测（督导 12:47）：OverMelt 之后置 Ok/Converged=false 的次数 **0**；
                //   Criteria 里与「熔」有关的条目 **0**。
                //
                //   ⚠ ②″ 挡不住它：②″ 判的是**圆盘**峰值（TDiscMaxC），
                //     而这里的 TMaxC 是**整片**峰值 —— 舌片、压接段的热点不在 ②″ 口径里。
                //   ⚠ 这不是假想：旧路 FlangeAutoSizer 专门为它写过一道中止闸
                //     （「该级太薄、电流被挤在窄带上，局部发热物理上就下不来」）,
                //     换代到 Solver 之后没了 —— 与 S1 丢掉 Converged 那道闸是同一形状。
                //
                //   ⚠⚠ 督导原话照记：他**没有**造出「现役判据全过却熔化」的算例，
                //     证明的是「没有任何东西挡着它」。两句话不一样，不夸大。
                res.Ok = false;
                // ★ 2026-09-15 Opus 5（审查意见 minor：管表超界造出的假熔化仍被报成「该解不存在」）：
                //   有段的管解最高温超出管表面散热表上限（SegmentOut.TubeLossTableExceeded，段解在法兰之前、上面已填好）⇒ 超出部分管散热被钳住、
                //   发热随 ρ(T) 涨而散热不涨 ⇒ 管算偏热（实测单段 2200 °C，同一电流真散热下约 1150 °C，R48LossTableRangeTests），法兰边界跟着偏热。
                //   这时法兰越过熔点**可能是钳位造出来的**，既不能说熔、也不能说不熔 ⇒ **熔化位不置**（求解器的 MeltFloor 读到熔化位会给
                //   「截面漏一刀或 J 设太高」的诊断，那是另一件事），头条改说「判不了」；Ok 仍为 false（这个场不是这个设计的场，不许当解交出去）。
                //   只有实测电流模式会走到这里（控温反算时中点就是设定，管温到不了设定 + 400 K）。
                var tubeOverTable = segs.Where(s => s is not null && s.TubeLossTableExceeded).ToArray();
                if (tubeOverTable.Length > 0)
                {
                    res.Message = $"{flanges[j].Name}：峰值 {th.TMaxC:0} °C 越过了铂熔点 {Materials.PtMeltC:0} °C，"
                                + $"但管温已超出管表面散热表的温度上限（{string.Join("、", tubeOverTable.Select(s => $"{s.Name} 最高 {s.TubeTMaxC:0} °C，表上限 {s.TubeLossTableHiC:0} °C"))}），"
                                + "超出部分散热被钳住、管算偏热 —— 这个峰值不是这个设计的真值，**熔不熔判不了**。请核对控温点与实测电流";
                }
                else
                {
                    res.OverMelt = true;
                    flanges[j].OverMelt = true;      // ★ 逐片的位：求解器按它决定抬哪一片的下角
                    res.Message = $"{flanges[j].Name}：峰值 {th.TMaxC:0} °C 已越过铂熔点 "
                                + $"{Materials.PtMeltC:0} °C —— **该解不存在**（不是「不够好」，是物理上不成立）";
                }
                res.Notes.Add("✗ " + res.Message);
            }
            else if (th.OverFitRange)
                res.Notes.Add($"⚠ {flanges[j].Name}：峰值 {th.TMaxC:0} °C 超出电阻率拟合区 " +
                              $"{Materials.PtFitMaxC:0} °C，数值系外推");
            // ★ 场没收敛 ⇒ **这一片的判据判不了**，不是「照报一个数」。
            var why = new List<string>();
            // ⚠ th.Residual 是**步长**不是残差 —— 两个都报，别再把步长叫成残差。
            flanges[j].FieldResidualRel = th.ResidualRel;
            flanges[j].FieldOuterIters = th.Iterations;
            flanges[j].FieldInnerIters = th.InnerIterations;
            if (!th.Converged) why.Add($"温度场未收敛（步长 {th.Residual:E2} K，真残差 {th.ResidualW:E2} W／相对 {th.ResidualRel:E2}，{th.Iterations} 轮）");
            if (!curConverged) why.Add($"电位场未收敛（残差 {curResidual:E2}，{curIterations} 轮）");
            // ★ R48（2026-09-15，Opus 5）：最高温超过散热表上限 ⇒ 超出的格子散热被钳住，场不是这个设计的场
            if (!double.IsNaN(th.LossTableHiC) && th.TMaxC > th.LossTableHiC)
                why.Add($"最高温 {th.TMaxC:0} °C 超过表面散热表上限 {th.LossTableHiC:0} °C（铂熔点），超出部分散热被钳住");
            if (why.Count > 0)
            {
                flanges[j].FieldsConverged = false;
                flanges[j].FieldNote = string.Join("；", why);
                res.Notes.Add($"✗ {flanges[j].Name}：{flanges[j].FieldNote}"
                            + " ⇒ **吃这一片场的判据一律判不了**（判不了不算过）");
            }
        }
        res.Flanges = flanges;

        // ── ④ 汇总与判定
        res.TubeMassG = segs.Sum(s => s.MassG);
        res.FlangeMassG = flanges.Sum(f => f.MassG);
        res.TotalMassG = res.TubeMassG + res.FlangeMassG;
        res.SavingPct = c.BaselineMassG > 0
            ? (c.BaselineMassG - res.TotalMassG) / c.BaselineMassG * 100 : 0;

        // ★★ 次序：**数据必须在评判之前完整**。
        //   ③ 的输入是 SegmentOut.FlangeDipK，而它唯一的来源是 ApplyBaseline。
        //   原来 ApplyBaseline 在 Run() 里、RunOnce **返回之后**才调用 ——
        //   于是 Judge 评的是一个还没被填的字段，③ **永远**是 NaN/无法判定，
        //   与基线算得出算不出毫无关系（实测：基线子解 Ok=true，是次序错）。
        //   ⇒ 基线传进来，在 Judge 之前填好。
        //   不选「Judge 之后重评一次」：判据评两遍会产生「以哪遍为准」的歧义，
        //   而**同一件事有多个来源**正是本项目连错四次的结构性根源。
        if (baseline is not null) ApplyBaseline(res, baseline);
        res.Checks = Judge(c, res, segs, flanges, segParams);
        MarkUndeterminedIfFieldsFailed(res, flanges);
        MarkUndeterminedIfInsulBoundaryUnknown(res, flanges);
        MarkUndeterminedIfClampCoversHole(res, flanges);
        MarkUndeterminedIfClampIntoDisc(res, flanges);
        MarkUndeterminedIfTubeLossTableExceeded(res, segs, c.UseMeasuredCurrent);
        return res;
    }

    /// <summary>
    /// 算例前置检查（RunOnce 与管侧单解共用）：返回 "" 表示可以解，否则是拒算原因（文字与原 RunOnce 逐字相同）。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）：纯搬移。
    /// </summary>
    private static string CaseGuardMessage(LineCase c)
    {
        int n = c.SegmentCount;
        if (n < 1) return "段数不能为 0";
        if (c.UseMeasuredCurrent && c.MeasuredCurrentA.Length < n)
            return $"实测电流只给了 {c.MeasuredCurrentA.Length} 个，需要 {n} 个";
        if (c.RefusedWhy.Length > 0) return c.RefusedWhy;   // R47 第三轮 N5：图纸档不造解析板
        if (c.FlangeFile3dm.Length == 0 && c.FlangePlates.Length == 0 && c.FlangeFields.Length == 0)
            return "未指定法兰几何（.3dm／内存厚度场／解析 FlangePlate 三选一）";
        return "";
    }

    /// <summary>一段的两端各挂多少抽热（整线外层耦合用它，保温搜索的管侧响应也用它）：没有这个就是两份配方。</summary>
    public static double MeanDraw((double L, double R) lr) => 0.5 * (lr.L + lr.R);

    /// <summary>
    /// 第 i 段左、右两端落到管子边界上的抽热 W：左端是法兰 i（抽热 <paramref name="qFlangeLeftW"/>），右端是法兰 i+1。
    /// 共用片按 <see cref="DesignInputs.SplitSharedFlangeDraw"/> 两侧各半，端片整份。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）：从 Run 的外层耦合循环原样搬出（纯搬移），保温搜索扰动单片抽热时调同一份。
    /// </summary>
    public static (double L, double R) SegmentEndDraws(LineCase c, int i, double qFlangeLeftW, double qFlangeRightW)
    {
        double a = qFlangeLeftW, b = qFlangeRightW;
        // ★★★★★ 能量守恒（2026-08-28）：**内部共用片属于两段，必须分配**。
        //   端片（法兰 0 与法兰 n）只属于一段 ⇒ 整份。
        //   内部片 j 同时是「段 j−1 的右端」与「段 j 的左端」⇒ 各半，Q_L + Q_R = Q。
        //   不分配时管子失去 Q₀ + 2ΣQ内 + Q_n，法兰只收到 ΣQ —— 实测残差 +2.66/+3.74 W。
        //   ⚠ 默认**关**：打开会改动设计记录的数。见 DesignInputs.SplitSharedFlangeDraw。
        bool sp = c.Base.SplitSharedFlangeDraw;
        double aEff = sp && i > 0 ? 0.5 * a : a;                        // 左端：i>0 ⇒ 内部片
        double bEff = sp && i < c.SegmentCount - 1 ? 0.5 * b : b;       // 右端：i<n−1 ⇒ 内部片
        return (aEff, bEff);
    }

    /// <summary>
    /// **管侧单解**：给定各段两端抽热 <paramref name="drawLR"/> 与邻段端温 <paramref name="nbT"/>，只解段、不解法兰，一次（不迭代段间端温）。
    /// 走的就是 RunOnce 的逐段循环（SolveSegmentsInto）与同一组前置检查；段电流仍按控温点反算（与整线同）。
    /// 在整线收敛态（算例 WarmStart 里的抽热与端温）上调用，管根与整线逐位相同 —— 保温搜索每次用前自检这一点。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）新增。
    /// </summary>
    public static LineResult SolveTubeSegments(LineCase c, (double L, double R)[] drawLR, (double L, double R)[]? nbT,
                                               CancellationToken cancel = default)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (drawLR is null) throw new ArgumentNullException(nameof(drawLR));
        Normalize(c);
        var res = new LineResult { BaselineMassG = c.BaselineMassG, RampChecked = c.CheckRamp, EmptyTube = c.EmptyTube, RuleSet = c.RuleSet };   // 决 103：判据口径同 RunOnce；K 路（2026-09-15 Opus 5）：同 RunOnce
        string guard = CaseGuardMessage(c);
        if (guard.Length > 0) { res.Ok = false; res.Message = guard; return res; }
        if (drawLR.Length < c.SegmentCount || (nbT is not null && nbT.Length < c.SegmentCount))
        { res.Ok = false; res.Message = $"管侧单解：抽热或端温只给了 {drawLR.Length}/{nbT?.Length} 段，需要 {c.SegmentCount} 段"; return res; }
        var drawW = drawLR.Take(c.SegmentCount).Select(MeanDraw).ToArray();
        SolveSegmentsInto(c, res, null, cancel, drawW, drawLR, nbT, out _, out _);
        return res;
    }

    /// <summary>段间端温不动点的结果（无法兰基线与管侧响应共用）。</summary>
    public sealed class NeighbourIteration
    {
        /// <summary>最后一次段解（其管根就是这次迭代的结果）。</summary>
        public LineResult? Last;
        public int Rounds;
        /// <summary>判收敛量 K（BaselineTolAmplified 开 = 到不动点估计；关 = 欠松弛步，历史口径）。没走到判的那一步是 NaN。</summary>
        public double JudgeK = double.NaN;
        public double TolK;
        public bool Converged => Last is { Ok: true } && JudgeK < TolK;
        /// <summary>最后一次段解用的邻段端温 x（null = 各段各解各的那一轮）；Last 就是 G(x)，真残差 = G(x) − x 不必再解一次。</summary>
        public (double L, double R)[]? LastInput;
    }

    /// <summary>
    /// 段间端温的欠松弛不动点：反复调 <paramref name="once"/>(邻段端温)，按无法兰基线的口径判收敛。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）：从 Run 的无法兰基线循环原样搬出（纯搬移：基线以 nbStart = null、tol = CoupleTolK、
    ///   轮数 max(30, CoupleMaxRounds) 调用，进度文字逐字不变），管侧响应 <see cref="SolveTubeWithDraws"/> 调同一份。
    /// </summary>
    private static NeighbourIteration IterateNeighbourTemps(LineCase c, Func<(double L, double R)[]?, LineResult> once,
                                                            (double L, double R)[]? nbStart, int maxRounds, double tolK,
                                                            IProgress<string>? progress, string label, double jacAmp = double.NaN)
    {
        var it = new NeighbourIteration { TolK = tolK };
        (double L, double R)[]? bnb = nbStart is null ? null : ((double L, double R)[])nbStart.Clone();
        LineResult? br = null;
        // ⚠ 基线的段间耦合必须与主解**同样处理**：欠松弛 + 收敛判据。
        //   原来只跑 4 轮、且端温直接赋值（**裸 Picard**）—— 而主解那边的注释
        //   早写明「裸 Picard 会发散，必须欠松弛」。两边收敛程度不同，
        //   就会差出一个**与法兰无关的系统性偏移**：实测 ③ 恒为 31.5±0.4 K，
        //   而净流入从 +1 W 到 +7 W（差 7 倍）它纹丝不动 ——
        //   **不随因变量变，就不是那个因造成的**。
        double wBase = c.CoupleRelax;
        // ★ 基线也必须**报出自己的收敛情况**。收紧 CoupleTolK 之后，如果基线
        //   悄悄地不收敛，③ 会整体偏掉几十 K 而判据表照样打得漂漂亮亮
        //   —— HANDOVER 记过一次：基线没收敛好时 ③ 恒为 31.5±0.4 K、
        //   净流入从 +1 到 +7 W 它纹丝不动。**不随因变量变，就不是那个因造成的。**
        int baseRounds = 0; double baseDmax = double.NaN;
        int baseMaxRounds = maxRounds;
        for (int k = 0; k < baseMaxRounds; k++)
        {
            // ★ R48 审查修改轮（2026-09-14，Opus 5）：基线外层**每轮报进度**。此前整段基线不报（RunOnce 传 null），
            //   空管＋管腔辐射敏感度探针在 0.5 mm 上卡在基线里 30 多分钟一行都不出，看不出是慢还是不收敛（长跑要看得出还活着）。
            //   只报文字、不改任何数；措辞避开「第 N 轮」「外层耦合 n/m」，界面的进度条翻译器（LineDesignPage.PctOf）读不出轮数 ⇒ 走马灯，
            //   不会拿基线的轮数冒充主环进度。
            progress?.Report($"{label} {k + 1} 轮起（最多 {baseMaxRounds}）：上轮判收敛量 "
                           + (double.IsNaN(baseDmax) ? "—" : baseDmax.ToString("0.000")) + $" K，容差 {tolK:0.###} K…");
            var inputSnapshot = bnb is null ? null : ((double L, double R)[])bnb.Clone();   // R48 E（2026-09-15 Opus 5）：只记下来，不改任何数
            br = once(bnb);
            it.LastInput = inputSnapshot;
            if (!br.Ok) break;
            var nb2 = NeighbourTempsOf(br.Segments);   // R48 E（2026-09-15 Opus 5）：定义搬进 NeighbourTempsOf，纯搬移
            if (bnb is null) { bnb = nb2; continue; }
            double dmax = 0;
            for (int i = 0; i < c.SegmentCount; i++)
            {
                double nl = double.IsNaN(nb2[i].L) ? double.NaN
                          : (1 - wBase) * bnb[i].L + wBase * nb2[i].L;
                double nr = double.IsNaN(nb2[i].R) ? double.NaN
                          : (1 - wBase) * bnb[i].R + wBase * nb2[i].R;
                if (!double.IsNaN(nl)) dmax = Math.Max(dmax, Math.Abs(nl - bnb[i].L));
                if (!double.IsNaN(nr)) dmax = Math.Max(dmax, Math.Abs(nr - bnb[i].R));
                bnb[i] = (nl, nr);
            }
            // ★ dmax 是**欠松弛步** = ω×残差，不是残差本身，更不是到不动点的距离。
            //   到不动点 ≈ (dmax/ω) × FixedPointAmp。主环就是这么判的；
            //   基线此前直接拿 dmax 比 CoupleTolK —— 两个错叠在一起（见 BaselineTolAmplified）。
            double baseResid = wBase > 1e-9 ? dmax / wBase : dmax;
            // ★ R48 L（2026-09-16，Opus 5）：放大倍数与主环同一份（EndTempFixedPointAmpOf，当场算 1 + ℓt/Δx）。
            //   这一层判的正是**段间端温不动点**（dmax 就是端温残差）—— 与主环那一处是同一件事，
            //   FixedPointAmp 的注释自己写着「主环与基线环各写一个 25，就会出现『一处改了另一处没改』」，
            //   所以两处一起换，口径不许劈叉。
            // R48 M（2026-09-18，Fable 5.1）：放大与主环同一份口径 max(闭式 × 1.1, 实测雅可比)（StopAmpOf）；管侧单解那条路没量雅可比 ⇒ 闭式 × 1.1
            double baseJudge = c.Base.BaselineTolAmplified
                             ? baseResid * StopAmpOf(c, br, jacAmp)
                             : dmax;                      // 历史口径
            if (baseJudge < tolK) { baseRounds = k + 1; baseDmax = baseJudge; break; }
            baseRounds = k + 1; baseDmax = baseJudge;
        }
        it.Last = br; it.Rounds = baseRounds; it.JudgeK = baseDmax;
        return it;
    }

    /// <summary>
    /// **管侧响应**：给定各段两端抽热，从 <paramref name="nbStart"/>（null = 各段各解各的起步，同无法兰基线）出发迭代段间端温到收敛，只解段。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）新增；迭代与判收敛是无法兰基线的同一份（IterateNeighbourTemps）。
    /// 保温搜索**不用**它量 γ（共用片处段间耦合增益 0.97～0.98，按 ×25 口径「收敛」时离不动点还有 0.35～0.74 K），改用 <see cref="SolveTubeWithDrawsNewton"/>；
    /// 它留作对拍的参照：慢门 R48TubeResponseNewtonGateTests 用它证明两种解法同一不动点。
    /// </summary>
    public static NeighbourIteration SolveTubeWithDraws(LineCase c, (double L, double R)[] drawLR, (double L, double R)[]? nbStart,
                                                        double tolK, int maxRounds, CancellationToken cancel = default)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        Normalize(c);
        return IterateNeighbourTemps(c, nb => SolveTubeSegments(c, drawLR, nb, cancel), nbStart, maxRounds, tolK, null, "管侧单解");
    }

    /// <summary>
    /// 段间端温向量（整线外层耦合与无法兰基线的定义）：段 i 的左邻 = 段 i−1 的 B 端，右邻 = 段 i+1 的 A 端；整线两头没有邻段 ⇒ NaN。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）：给管侧牛顿解读 G(x) 用；与 Run 里两处（nb2、nbNew）同一定义，快门逐位比。
    /// </summary>
    public static (double L, double R)[] NeighbourTempsOf(SegmentOut[] segs)
    {
        int n = segs.Length;
        var nb = new (double L, double R)[n];
        for (int i = 0; i < n; i++)
            nb[i] = (i == 0 ? double.NaN : segs[i - 1].TRootBC,
                     i == n - 1 ? double.NaN : segs[i + 1].TRootAC);
        return nb;
    }

    /// <summary>
    /// ★★★★★ R48 L（2026-09-16，Opus 5；数值把关人 2026-09-16 定口径）：
    /// **段间端温不动点这一层的停机放大倍数** —— 从段解报回来的两端值（<see cref="SegmentOut.EndAmpA"/>／B）取**最大**，
    /// 因为停机判定要挡的是收缩最慢的那个模式。口径与出处见 <see cref="SegmentSolver.EndTempFixedPointAt"/>。
    ///
    /// <para>只用在**段间端温**那一层（主环的 δ／真残差两条停机判定、无法兰基线／管侧单解的判收敛量）。
    /// 外层法兰↔管耦合（抽热那一半）的放大是另一个环路，不走这里 —— 两层不能混。</para>
    ///
    /// <para>⚠ 全 NaN 时**返回 NaN**，不静默退回 25：NaN 乘残差还是 NaN，判定就得不出「已收敛」——
    /// 这正是要的行为（判不了不当过）。只有连段解都没有（判据路上走不到）才退回 <see cref="LineCase.FixedPointAmp"/>。</para>
    /// </summary>
    public static double EndTempFixedPointAmpOf(LineCase c, LineResult? r)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (!c.EndTempAmpFromDecayLength) return LineCase.FixedPointAmp;   // 历史口径（门用它注射旧病）
        if (r is not { Segments.Length: > 0 }) return LineCase.FixedPointAmp;
        double amp = double.NaN;
        foreach (var s in r.Segments)
        {
            if (double.IsFinite(s.EndAmpA)) amp = double.IsNaN(amp) ? s.EndAmpA : Math.Max(amp, s.EndAmpA);
            if (double.IsFinite(s.EndAmpB)) amp = double.IsNaN(amp) ? s.EndAmpB : Math.Max(amp, s.EndAmpB);
        }
        return amp;
    }

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：闭式放大要留的余量。闭式 1 + ℓt/Δx 是按端部单指数衰减推的下界口径，
    /// 实测雅可比（I 路 2026-09-15：带玻璃 36.6、空管 99.7）在共用片处比闭式大 ⇒ 闭式乘 1.1 再与实测取大，两个来源谁大用谁。
    /// </summary>
    public const double StopAmpHeadroom = 1.1;

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**停机判定用的放大倍数 —— 全仓唯一读口** = max(闭式 <see cref="EndTempFixedPointAmpOf"/> × <see cref="StopAmpHeadroom"/>, 实测雅可比 <paramref name="jacAmp"/>)。
    /// <paramref name="jacAmp"/> NaN／非有限 = 没量到 ⇒ 只用闭式 × 1.1。闭式为 NaN（端温不是数）⇒ NaN 原样返回（NaN 乘残差还是 NaN，判定得不出「已收敛」——判不了不当过）。
    /// <see cref="LineCase.EndTempAmpFromDecayLength"/> 关掉 = 历史口径 25 原样（门用它注射旧病），不乘余量也不取实测。
    /// 主环两支停机、未收敛报告、无法兰基线／管侧单解的判收敛量都读这里；rEst/(1−rEst) 那一支已删（原因见主环注记）。
    /// </summary>
    public static double StopAmpOf(LineCase c, LineResult? r, double jacAmp)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        double closed = EndTempFixedPointAmpOf(c, r);
        if (!c.EndTempAmpFromDecayLength) return closed;          // 历史口径：25，原样
        double a = closed * StopAmpHeadroom;
        if (!double.IsNaN(jacAmp) && jacAmp > a) a = jacAmp;   // +∞（奇异）要传出去：NaN 乘残差还是 NaN、∞ 乘残差还是 ∞，都判不出「已收敛」
        return a;
    }

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**量一次实测雅可比放大** ‖(I−J)⁻¹‖∞（零抽热、各段各解各的那一态起步；段解只解段，m+1 次）。
    /// 返回 NaN = 量不出来（段解失败／只有一段没有邻段位）；奇异 ⇒ +∞（<see cref="NeighbourAmplification"/> 的口径，停机判定得不出「已收敛」）。
    /// 与保温搜索量管侧响应用的是同一份 <see cref="NeighbourJacobian"/>（前差步长 <see cref="LineCase.JacobianDeltaK"/>）。
    /// </summary>
    public static double MeasureJacobianAmp(LineCase c, CancellationToken cancel = default)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        Normalize(c);
        var zeroLR = new (double L, double R)[c.SegmentCount];
        var g0 = SolveTubeSegments(c, zeroLR, null, cancel);
        if (!g0.Ok) return double.NaN;
        var nb0 = NeighbourTempsOf(g0.Segments);
        var (slots, jac, gb) = NeighbourJacobian(c, zeroLR, nb0, c.JacobianDeltaK, cancel);
        if (!gb.Ok || slots.Length == 0) return double.NaN;
        return NeighbourAmplification(jac);
    }

    /// <summary>
    /// ★★★★★ R48 M（2026-09-18，Fable 5.1）：**裕度比认证误差还小的硬安全线判不了那么细**（判不了不算过）。
    /// 名单与 <see cref="MinHardTempMarginK"/> 同一条规矩：只认 <see cref="CheckKind.HardSafety"/> 且单位为 K 的（瓦的那条与温度容差之间没有实测灵敏度，不进）；
    /// 已经判不了／暂不给数／NaN 的不动。认证误差 NaN 或 0（没走外层耦合、未收敛）⇒ 一条都不动。
    /// 标成 <see cref="ConstraintOut.Undetermined"/>（Ok=false）并把原因写进 Note ⇒ <see cref="LineResult.AllOk"/>、Failed、判据表、安装报告、求解器 Violations 全部自动跟着。
    /// 返回被标的判据名（去代号）。
    /// </summary>
    public static string[] MarkUndeterminedIfInsideCertErr(LineResult res)
    {
        if (res is null) throw new ArgumentNullException(nameof(res));
        double e = res.CertErrK;
        if (!(e > 0)) return Array.Empty<string>();
        var hit = new List<string>();
        foreach (var ck in res.Checks)
        {
            if (ck is null || ck.Kind != CheckKind.HardSafety) continue;
            if (!string.Equals(ck.Unit, "K", StringComparison.Ordinal)) continue;
            if (ck.Undetermined || ck.Withheld) continue;
            if (double.IsNaN(ck.Actual) || double.IsNaN(ck.Limit)) continue;
            double m = Math.Abs(ck.Limit - ck.Actual);
            if (!(m < e)) continue;
            ck.Undetermined = true; ck.Ok = false;
            ck.Note = (string.IsNullOrEmpty(ck.Note) ? "" : ck.Note + "　")
                    + $"判不了那么细：裕度 {m:0.000} K 小于认证误差 {e:0.000} K（= 放大 × 停机残差）—— 既不当过也不当不过；要判得了得把这一点解得更细（细网格／更细容差重算）";
            hit.Add(Criteria.Plain(ck.Name));
        }
        return hit.ToArray();
    }

    /// <summary>
    /// ★ R48 L（2026-09-16，Opus 5）：**分辨率地板**的判词 —— 没收敛、但步长与真残差**都已在容差以内**，
    /// 卡住的只是放大倍数（要停下来得把步长压到 容差/放大 以下）。那是分辨率地板，**不是热失控**。
    ///
    /// 判据写死（跑之前定，跑完不改）：未收敛 且 末轮步长 δ &lt; 容差 且 末轮真残差 &lt; 容差 ⇒ 分辨率地板。
    /// 其一不满足 ⇒ 返回 null，维持原来的「慢／没在收缩」两态判词（不许拿分辨率地板去盖真的不收敛）。
    ///
    /// ⚠ 返回的是「**判不了**」，永远不是「过」。调用方不许因为它把 <see cref="LineResult.Converged"/> 置 true。
    /// </summary>
    public static string? ResolutionFloorNote(double deltaK, double resK, double tolK, double amp, int rounds)
    {
        if (!(deltaK < tolK) || !(resK < tolK)) return null;
        double remain = Math.Max(deltaK, resK) * amp;
        return $"★ 判不了：分辨率地板 —— 步长 {deltaK:0.0000} K 与真残差 {resK:0.0000} K **都已在容差 {tolK:0.###} K 以内**，"
             + $"但按放大 {amp:0.0} 倍算，到不动点的估计仍有 {remain:0.000} K ≥ 容差；轮数 {rounds}。"
             + $"要停得下来，步长得压到 {tolK / Math.Max(1e-9, amp):0.00000} K 以下 —— **这是分辨率地板，不是热失控**，"
             + "本次判不了（既不当过，也不当不过）。";
    }

    /// <summary>管侧牛顿解的结果。</summary>
    public sealed class TubeNewtonResult
    {
        /// <summary>最后一次段解（在最后一个 x 上；其管根就是结果）。</summary>
        public LineResult? Last;
        /// <summary>段解次数（每次 = 全部段各解一次）。</summary>
        public int Evaluations;
        /// <summary>真残差 ‖G(x) − x‖∞ K（最后一个 x 上，由生产段解直接算出，与雅可比无关）。</summary>
        public double ResidualK = double.NaN;
        /// <summary>
        /// 到不动点的距离估计 K = ‖(I − J)⁻¹ (G(x) − x)‖∞（按量出的雅可比把真残差放大，即「再走一步牛顿会动多少」）。
        /// ★ 不用「真残差 × FixedPointAmp(25)」：那是按整线环路增益 0.96 定的常数，而共用片处段间耦合实测带玻璃 0.9720～0.9729、空管 0.9834～0.9835（放大 ‖(I−J)⁻¹‖∞ 带玻璃 36.7、空管 61.7 倍，见下一段 2026-09-15 J 路的核算），
        ///   按 25 会把距离低估一半以上（deliverable\R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt：欠松弛迭代「判收敛量 0.30 K」处离牛顿解 0.72 K）。
        ///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：这份对拍是在 r48_E 工作树上跑的（底板 f206e70：F 合入前的配方 —— 压接形心整格 + 3·hFine 细带、法兰散热表上限「设定 + 200 K、60 节点」、
        ///     未计空管管腔轴向辐射）；增益 0.972～0.984、放大 36.6／61.7 倍（第二版 M2）这些数只属于那份配方。文件首跑在 r48_E 工作树，已原样拷入本树 deliverable（deliverable/R48_合并拷入证据清单_2026-09-15.txt）。
        ///   ★ 2026-09-15 Opus 5（J 路，合并把关待办 P3 第 6 条）：上一句里「0.972～0.984、放大 36～61 倍」两头口径不一（0.984 是 0.9835 进位、36／61 是截尾）。照文件重写：
        ///     雅可比（该文件第 3、4 行，印到小数点后 4 位）带玻璃主元 0.9720／0.9729／0.9722／0.9723，空管 0.9835／0.9835／0.9834／0.9834；
        ///     按这两张 4×4（含印出来的小元）算 ‖(I−J)⁻¹‖∞：带玻璃 36.69、空管 61.73（本人 2026-09-15 用 numpy 求逆，只读该文件，没有另跑；受 4 位小数所限只作量级）。
        ///     文件现在合并树 deliverable 里有（合并时从 r48_E 原样拷入，见 HANDOVER 合并注记），原句「合并树里没有」已过时。
        ///   ⚠ 2026-09-18 Opus 5（合并 H/I/J/L/P/U）：J 路原句「合并树上没有重跑」与「文件现在合并树里有」两句已删 —— 前者被下面 I 路的重量作废，后者与本段上一行重复。
        ///     2026-09-15 Opus 5（I 路）：合并树配方下已重量（deliverable\R48_段间耦合增益_节点阶梯_本次开跑于2026-09-15_184706.txt，B2、管段 401 节点）：带玻璃谱半径 0.97267、放大 36.6；空管到温稳态 0.98997、放大 99.7（‖(I−J)⁻¹‖∞ 100.7）；增益随节点数涨（201／801 节点带玻璃 0.9467／0.9862，空管 0.9804／0.9950）。
        /// </summary>
        public double DistanceK = double.NaN;
        public double JudgeK => DistanceK;
        public double TolK;
        public bool Converged => Last is { Ok: true } && DistanceK < TolK;
        public string Message = "";
        /// <summary>最后一次段解用的邻段端温 x（Last = G(x)）。</summary>
        public (double L, double R)[]? LastInput;
    }

    /// <summary>
    /// 段间端温不动点 x = G(x) 的雅可比（有限差分，前差 <paramref name="deltaK"/>）：x = 各段邻段端温里不是 NaN 的位（次序：段 0..n−1，每段先左后右），
    /// G(x) = 用 x 解一次段后读出的新邻段端温（<see cref="NeighbourTempsOf"/>）。带玻璃时段与段经玻璃出口温度串联，所以逐位扰动、不做着色。
    /// 返回 (位表, J, G(x₀) 那次段解)。★ R48 E 审查修改（2026-09-15 Opus 5）新增。
    /// </summary>
    public static ((int Seg, bool Left)[] Slots, double[,] J, LineResult Base) NeighbourJacobian(LineCase c, (double L, double R)[] drawLR,
                                                                                               (double L, double R)[] nb, double deltaK,
                                                                                               CancellationToken cancel = default)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        Normalize(c);
        var slots = NeighbourSlots(nb);
        int m = slots.Length;
        var g0 = SolveTubeSegments(c, drawLR, nb, cancel);
        var jac = new double[m, m];
        if (!g0.Ok) return (slots, jac, g0);
        var y0 = SlotValues(NeighbourTempsOf(g0.Segments), slots);
        for (int col = 0; col < m; col++)
        {
            cancel.ThrowIfCancellationRequested();
            var xp = ((double L, double R)[])nb.Clone();
            var (seg, left) = slots[col];
            xp[seg] = left ? (xp[seg].L + deltaK, xp[seg].R) : (xp[seg].L, xp[seg].R + deltaK);
            var gp = SolveTubeSegments(c, drawLR, xp, cancel);
            if (!gp.Ok) return (slots, jac, gp);
            var yp = SlotValues(NeighbourTempsOf(gp.Segments), slots);
            for (int row = 0; row < m; row++) jac[row, col] = (yp[row] - y0[row]) / deltaK;
        }
        return (slots, jac, g0);
    }

    /// <summary>
    /// **管侧响应（牛顿弦法）**：给定各段两端抽热，从 <paramref name="nbStart"/> 出发解段间端温不动点 x = G(x)：
    /// x ← x + (I − J)⁻¹ (G(x) − x)，J 用 <see cref="NeighbourJacobian"/> 在起点量好、全程不重算（弦法）。
    /// 停机：到不动点距离估计 ‖(I − J)⁻¹ (G(x) − x)‖∞ &lt; <paramref name="tolK"/>（见 <see cref="TubeNewtonResult.DistanceK"/>）。
    /// 为什么不用无法兰基线那份欠松弛迭代（<see cref="SolveTubeWithDraws"/>）：共用片那一处段间耦合增益近 1，欠松弛迭代要走很多轮 ——
    /// 2026-09-15 Opus 5（I 路） 照对拍文件改实数（原句「实测 60 轮、每轮约 1.7 s 仍没收敛到 0.25 K」是没留文件的一次性实测）：deliverable\R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt
    /// 第 13–16 行带玻璃共用片 ±2 W 要 194～207 轮才到 0.25 K（381～402 s，每轮约 1.9 s），第 17–20 行空管共用片 300 轮停在 0.300～0.307 K（562～567 s）；端片第 5–12 行 1 轮即 0.10～0.14 K。
    /// 同一文件另有两者同一不动点的对拍。不动点由同一个段解定义，只是到达方式不同。
    ///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：这份对拍是在 r48_E 工作树上跑的（底板 f206e70：F 合入前的配方 —— 压接形心整格 + 3·hFine 细带、法兰散热表上限「设定 + 200 K、60 节点」、
    ///     未计空管管腔轴向辐射）；增益 0.972～0.984、放大 36.6／61.7 倍（第二版 M2）这些数只属于那份配方。文件首跑在 r48_E 工作树，已原样拷入本树 deliverable（deliverable/R48_合并拷入证据清单_2026-09-15.txt）。
    ///     口径按文件重写后的四位实数（带玻璃 0.9720／0.9729／0.9722／0.9723 放大 36.69，空管 0.9835／0.9835／0.9834／0.9834 放大 61.73）见 TubeNewtonResult.DistanceK 注释（2026-09-15 J 路）。
    ///     2026-09-15 Opus 5（I 路）：合并树配方下已重量（deliverable\R48_段间耦合增益_节点阶梯_本次开跑于2026-09-15_184706.txt，B2、管段 401 节点）：带玻璃谱半径 0.97267、放大 36.6；空管到温稳态 0.98997、放大 99.7（‖(I−J)⁻¹‖∞ 100.7）；增益随节点数涨（201／801 节点带玻璃 0.9467／0.9862，空管 0.9804／0.9950）。
    /// ★ R48 E 审查修改（2026-09-15 Opus 5）新增。
    /// </summary>
    public static TubeNewtonResult SolveTubeWithDrawsNewton(LineCase c, (double L, double R)[] drawLR, (double L, double R)[] nbStart,
                                                           ((int Seg, bool Left)[] Slots, double[,] J) jacobian, double tolK, int maxIter,
                                                           CancellationToken cancel = default, int minSteps = 0)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        Normalize(c);
        var res = new TubeNewtonResult { TolK = tolK };
        var slots = jacobian.Slots;
        int m = slots.Length;
        var x = ((double L, double R)[])nbStart.Clone();
        // (I − J)
        var a = new double[m, m];
        for (int r = 0; r < m; r++)
            for (int q = 0; q < m; q++)
                a[r, q] = (r == q ? 1.0 : 0.0) - jacobian.J[r, q];
        for (int it = 0; it <= maxIter; it++)
        {
            res.LastInput = ((double L, double R)[])x.Clone();
            var g = SolveTubeSegments(c, drawLR, x, cancel);
            res.Evaluations++;
            res.Last = g;
            if (!g.Ok) { res.Message = g.Message; return res; }
            var gx = SlotValues(NeighbourTempsOf(g.Segments), slots);
            var xv = SlotValues(x, slots);
            var rv = new double[m];
            double resid = 0;
            for (int k = 0; k < m; k++) { rv[k] = gx[k] - xv[k]; resid = Math.Max(resid, Math.Abs(rv[k])); }
            res.ResidualK = resid;
            var dx = SolveDense(a, rv);
            if (dx is null) { res.Message = "I − J 奇异，牛顿步解不出"; return res; }
            res.DistanceK = dx.Max(v => Math.Abs(v));
            if ((res.Converged && it >= minSteps) || it == maxIter) break;   // minSteps：至少走几步牛顿（起点本身离不动点的那一截对 ± 两个扰动是共同的，走一步把扰动自身的那一截也解掉）
            for (int k = 0; k < m; k++)
            {
                var (seg, left) = slots[k];
                x[seg] = left ? (x[seg].L + dx[k], x[seg].R) : (x[seg].L, x[seg].R + dx[k]);
            }
        }
        if (!res.Converged && res.Message.Length == 0)
            res.Message = $"{maxIter} 次牛顿步后到不动点估计 {res.JudgeK:0.0000} K 仍 ≥ 容差 {tolK} K";
        return res;
    }

    /// <summary>段间端温不动点的误差放大倍数 ‖(I − J)⁻¹‖∞（真残差到「离不动点多远」的换算）；奇异返回 +∞。★ R48 E 审查修改（2026-09-15 Opus 5）。</summary>
    public static double NeighbourAmplification(double[,] j)
    {
        int m = j.GetLength(0);
        var a = new double[m, m];
        for (int r = 0; r < m; r++) for (int q = 0; q < m; q++) a[r, q] = (r == q ? 1.0 : 0.0) - j[r, q];
        var rowSum = new double[m];
        for (int col = 0; col < m; col++)
        {
            var e = new double[m]; e[col] = 1;
            var x = SolveDense(a, e);
            if (x is null) return double.PositiveInfinity;
            for (int r = 0; r < m; r++) rowSum[r] += Math.Abs(x[r]);
        }
        return m == 0 ? 1.0 : rowSum.Max();
    }

    private static (int Seg, bool Left)[] NeighbourSlots((double L, double R)[] nb)
    {
        var l = new List<(int, bool)>();
        for (int i = 0; i < nb.Length; i++)
        {
            if (!double.IsNaN(nb[i].L)) l.Add((i, true));
            if (!double.IsNaN(nb[i].R)) l.Add((i, false));
        }
        return l.ToArray();
    }

    private static double[] SlotValues((double L, double R)[] nb, (int Seg, bool Left)[] slots)
        => slots.Select(s => s.Left ? nb[s.Seg].L : nb[s.Seg].R).ToArray();

    /// <summary>小稠密方程组（列主元高斯消去）；奇异返回 null。</summary>
    public static double[]? SolveDense(double[,] a, double[] b)
    {
        int m = b.Length;
        var A = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
            if (!(Math.Abs(A[piv, col]) > 1e-12)) return null;
            if (piv != col)
            {
                for (int q = 0; q < m; q++) (A[col, q], A[piv, q]) = (A[piv, q], A[col, q]);
                (x[col], x[piv]) = (x[piv], x[col]);
            }
            for (int r = col + 1; r < m; r++)
            {
                double f = A[r, col] / A[col, col];
                if (f == 0) continue;
                for (int q = col; q < m; q++) A[r, q] -= f * A[col, q];
                x[r] -= f * x[col];
            }
        }
        for (int r = m - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int q = r + 1; q < m; q++) s -= A[r, q] * x[q];
            x[r] = s / A[r, r];
        }
        return x;
    }

    /// <summary>
    /// ★ 2026-09-18，Opus 5：段名只有这一份写法（原来 <c>SolveSegmentsInto</c> 就地写 <c>$"HC{i+1}"</c>，
    /// 而判据 ④ 的重判探针要点名到同一个段名 —— 两处各抄一份迟早对不上）。
    /// </summary>
    public static string SegName(int i) => $"HC{i + 1}";

    /// <summary>
    /// ★ 2026-09-18，Opus 5：**第 i 段的基准参数**（从算例来，与场解无关的那一半）——
    /// 原来这七行写死在 <c>SolveSegmentsInto</c> 的循环里，判据 ④ 想在不重新求解的前提下重判时
    /// 只能手抄一份。提成公开函数：求解循环与重判探针**共用这一份**（门不许手抄生产配方）。
    ///
    /// 做的事：克隆算例的基准参数，写入本段的管内径／壁厚／段长（段长同时是支承跨距）／牌号／
    /// 控温点／玻璃进口温度／本段玻璃水头，并关掉「按强度定壁厚」。
    /// ⚠ <paramref name="glassInC"/> 由调用方给：求解循环里它是**逐段串联**的（本段进口 = 上一段出口），
    ///   空管时是 NaN（不适用）。判据 ④ 一格都不读它（<see cref="Mechanics.Check"/> 不碰玻璃温度），
    ///   所以重判探针给算例的进口值即可 —— 这一点由 R48LTubeStrengthGateTests 的「玻璃进口温度不影响 ④」自证。
    /// ⚠ 调用前先归一化算例（哨兵 NaN / 空串），否则牌号会被空串盖掉 —— 本函数自己归一化一次（幂等）。
    /// </summary>
    public static DesignInputs BaseSegParams(LineCase c, int i, double glassInC)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        Normalize(c);
        var p = SegmentSolver.Clone(c.Base);
        p.TubeIdMm = c.TubeIdMm; p.WallMinMm = c.WallMm; p.TubeLengthMm = c.SegLengthMm[i];
        p.SupportSpanMm = c.SegLengthMm[i]; p.GradeName = c.GradeName;
        p.TSetC = c.SetpointC[i]; p.TGlassInC = glassInC;
        p.GlassHeadM = i < c.HeadM.Length ? c.HeadM[i] : 0;
        p.SizeWall = false;
        return p;
    }

    /// <summary>RunOnce 的逐段循环（原样搬出）。段解失败 ⇒ res.Ok = false、写原因、返回 false（res.Segments 不赋值，与原来一致）。</summary>
    private static bool SolveSegmentsInto(LineCase c, LineResult res, IProgress<string>? progress, CancellationToken cancel,
                                          double[] drawW, (double L, double R)[]? drawLR, (double L, double R)[]? nbT,
                                          out double[] amps, out DesignInputs[] segParams)
    {
        int n = c.SegmentCount;
        var segs = new SegmentOut[n];
        amps = new double[n];
        segParams = new DesignInputs[n];   // 各段实际用的参数，判据 ①④ 要拿去复用
        // ★ R48（2026-09-14，Opus 5）：空管到温稳态 —— 没有玻璃串联进来，玻璃进口记 NaN（不适用），段解里玻璃温度不参与。
        // ⚠ tg 是**逐段串联**的：本段的玻璃进口 = 上一段的出口（循环末尾 tg = sr.TGlassOutC）。
        double tg = c.EmptyTube ? double.NaN : c.GlassInC;
        if (c.EmptyTube) res.Notes.Add(EmptyTubeNoteFor(c));
        for (int i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"段 {i + 1}/{n}：{(c.UseMeasuredCurrent ? "按实测电流求解" : "反算电流")}…");

            var p = BaseSegParams(c, i, tg);
            p.FlangeDrawOverrideW = drawW[i]; p.FlangeDrawOverrideSet = true;
            // 两端各挂各的（原来取平均是 bug，见 DesignInputs.FlangeDrawLeftW）
            if (drawLR is not null)
            {
                p.FlangeDrawLeftW = drawLR[i].L; p.FlangeDrawRightW = drawLR[i].R;
                // ★ 记账：这两个数就是**真正落到管子边界上**的抽热，用来与各片实收对账
                res.DrawAppliedW += drawLR[i].L + drawLR[i].R;
            }
            // 段间轴向导热：把相邻段的端温传进去（见 DesignInputs.NeighbourTempLeftC）。
            // 首轮 nbT 为 null ⇒ 退化成原来的「各解各的」，由外层迭代逐步接上。
            if (nbT is not null)
            { p.NeighbourTempLeftC = nbT[i].L; p.NeighbourTempRightC = nbT[i].R; }

            // ★ R48（2026-09-14，Opus 5）：空管 ⇒ 段解（与走同一个 RunOnce 的无法兰基线）用的这份克隆把产量与管内玻璃换热置 0。
            //   判据用的段参数 segParams 另留一份**置 0 之前**的（管强度仍按带玻璃载荷，见 LineCase.EmptyTube）。
            DesignInputs? pJudge = null;
            if (c.EmptyTube)
            {
                pJudge = SegmentSolver.Clone(p);
                pJudge.TGlassInC = c.GlassInC;          // 判据那份不读玻璃进口；给回算例的数，免得 NaN 流进别处
                // ★ R48 审查第 5 条（2026-09-14，Opus 5）：端部额外保温是硬件 —— 渐变形状按置 0 之前（带玻璃）的 hg 定，不随工况变。
                //   调用方显式给过（≥ 0）就不覆盖。
                // R48 G3 审查后（2026-09-15，Opus 5）：原地写的三句提成 MakeEmptyTubeSegment（工况说明的各段换算值调同一个），语句与顺序不变。
                // 2026-09-15 Opus 5（合并）：E 最终版把逐段循环搬进本函数（SolveSegmentsInto），G3 这一处与下面 G3／G1 两行报数原在 RunOnce 的循环里，照原样搬过来。
                MakeEmptyTubeSegment(p);
            }

            SolveResult sr;
            if (c.UseMeasuredCurrent)
            {
                // 实测模式：电流已知，不需要外层二分 —— 这是秒级的来源
                sr = SegmentSolver.SolveAtCurrent(p, c.MeasuredCurrentA[i]);
            }
            else
            {
                sr = SegmentSolver.Solve(p);
            }
            if (!sr.Ok) { res.Ok = false; res.Message = $"段 {i + 1} 求解失败：{sr.Message}"; return false; }

            amps[i] = sr.CurrentA;
            segParams[i] = pJudge ?? p;
            double area = Math.PI * (Math.Pow(c.TubeIdMm * 0.5 + c.WallMm, 2)
                                     - Math.Pow(c.TubeIdMm * 0.5, 2));
            segs[i] = new SegmentOut
            {
                Name = SegName(i),
                SetpointC = c.SetpointC[i],
                CurrentA = sr.CurrentA,
                PowerW = sr.PowerTotalW,
                TubeJAPerMm2 = sr.CurrentA / area,
                // 两端各算，判据取**较差**的那个（偏离控温点最多的）
                TRootAC = sr.TFlangeAC, TRootBC = sr.TFlangeBC,
                RootDeltaAK = c.SetpointC[i] - sr.TFlangeAC,
                RootDeltaBK = c.SetpointC[i] - sr.TFlangeBC,
                TRootC = Math.Abs(c.SetpointC[i] - sr.TFlangeAC)
                       >= Math.Abs(c.SetpointC[i] - sr.TFlangeBC) ? sr.TFlangeAC : sr.TFlangeBC,
                RootDeltaK = Math.Abs(c.SetpointC[i] - sr.TFlangeAC)
                           >= Math.Abs(c.SetpointC[i] - sr.TFlangeBC)
                           ? c.SetpointC[i] - sr.TFlangeAC : c.SetpointC[i] - sr.TFlangeBC,
                GlassInC = tg,
                GlassOutC = sr.TGlassOutC,
                MassG = area * c.SegLengthMm[i] * Materials.PtDensity * 1e-6,
                BetaWPerMK = sr.BetaWPerMK, DecayLengthMm = sr.DecayLengthMm,   // R48（2026-09-14，Opus 5）：只报数
                CavityRadKAWmPerK = sr.CavityRadKAWmPerK,                        // R48 G3（2026-09-15，Opus 5）：只报数
                // R48 L（2026-09-16，Opus 5）：段间端温停机放大倍数，两端各一份 —— **这两个进判定**（EndTempFixedPointAmpOf）
                EndAmpA = sr.EndAmpA, EndAmpB = sr.EndAmpB,
                EndDecayLengthAMm = sr.EndDecayLengthAMm, EndDecayLengthBMm = sr.EndDecayLengthBMm,
                EndBetaAWPerMK = sr.EndBetaAWPerMK, EndBetaBWPerMK = sr.EndBetaBWPerMK,
                NodeSpacingMm = sr.NodeSpacingMm,
                EndInsulExtraProfileMm = SegmentSolver.EndInsulExtraProfileMm(p, c.WallMm),   // R48 审查第 5 条（2026-09-14，Opus 5）：只报数
                TubeLossTableExceeded = sr.LossTableExceeded, TubeLossTableHiC = sr.LossTableHiC, TubeTMaxC = sr.TMaxC,   // R48（2026-09-15，Opus 5）
                X = sr.X, TMetal = sr.TMetal, TGlass = sr.TGlass
            };
            tg = sr.TGlassOutC;
        }
        res.Segments = segs;
        res.GlassDropModelK = c.GlassInC - tg;                  // 空管时 tg 是 NaN ⇒ NaN；判据那条标「判不了（不适用）」，见 Judge 末尾
        res.GlassDropMeasuredK = c.GlassInC - c.GlassOutMeasuredC;
        return true;
    }

    /// <summary>
    /// HANDOVER §4.2k 的判据体系（取代旧的以 J 为中心的那套）：
    ///
    /// <code>
    /// ① 升温：空管 72 h 到 1150 °C       ← 决定最小截面（额定电流）
    /// ② 法兰温度 ≤ 管温（Φ ≤ 1）         ← 硬安全线，不可越
    /// ③ 管根温差 0 &lt; ΔT ≤ 10 K          ← 优化目标，从 ② 的安全侧逼近
    /// ④ 强度利用率 ≤ 1                   ← 真实工况下极宽松
    /// </code>
    ///
    /// **③ 是单边的**：旧代码判 |ΔT| ≤ 10，于是 ΔT = −8 K（法兰比管热 8 K，正在往烧断走）
    /// 会判「✓ 通过」。ΔT ≤ 0 与 Φ &gt; 1 是同一件事的两个视角，两条都列，
    /// 因为一条按段给（看得出卡在哪段），一条按片给（看得出卡在哪片）。
    ///
    /// ★★ 2026-08-28 更正：本段散文是 §4.2k（2026-08-10）那一版的快照，此后两次改动
    ///   只落到了代码与 HANDOVER，**没回头改这里**，于是它对活方法说了两句反话：
    ///
    ///   · 「① 升温 **3 h**」—— 2026-08-14 已改为 <c>RampHours = 72.0</c>（见本文件 166 行）。
    ///     用 3 h 去判会把「升得慢」误报成「升不到」，据此写过的结论已撤回。
    ///   · 「**J 不再单独判**…降级为 Reference，只报数不判」—— 2026-08-15 **反过来了**：
    ///     管 J 从「参考」**升为硬判据**，限值 <c>TubeJAllowAPerMm2 = 12</c>（现场依据见
    ///     DesignInputs 那一段），并且列在 <see cref="LineResult.RequiredByState"/> 里
    ///     ⇒ 缺席即翻 AllOk。而 0.6 档正贴着这条线交付（10.96/12）。
    ///     ⚠ 那句「J_allow = 10 待定」说的是**法兰**那条参考量（<c>JAllowAPerMm2</c>），
    ///       与管 J 是两个数，2026-08-15 已分家。
    ///
    /// ⇒ **判据清单以 <see cref="LineResult.RequiredByState"/> 与 HANDOVER §1.83 为唯一来源**
    ///   （后者由 CriteriaTableTests 对着代码核）。本段只讲**为什么**这么判，不再列清单 ——
    ///   第三份需要人工同步的判据描述，注定还会漂。
    /// </summary>
    /// <summary>
    /// ★★★ **场没解到位 ⇒ 吃它的判据一律「判不了」**（2026-08-29 补）。
    ///
    /// 三条铁律第 ③ 条：「判据只能过 / 不过 / **判不了**，判不了不算过」。
    /// 而这条此前在**线性解这一层根本没有执行**：
    ///   · <c>ShellCurrent.Converged</c> —— 字段有，**没有任何人读**；
    ///   · <c>ShellThermal.Converged</c> —— 有人读，但**只记一条 Note，不拦结果**。
    /// ⇒ 场没解到位时判据照样报一个数，而那个数**看起来完全正常** ——
    ///   正是本项目最怕的错误形态。
    ///
    /// 做成**后置一遍**而不是散落在每条判据里：散着写就会「补一条漏一条」，
    /// 本仓库为「判据缺席」栽过三次，最后也是靠一份**名单**解决的（见 LineResult.RequiredByState，K 路 2026-09-15 起带工况维）。
    ///
    /// ⚠ 只标**吃法兰场**的那些。纯几何（⑤⑥）与纯管子的（管 J、④）不受影响 ——
    ///   把它们一并标成判不了是**过度**，会掩盖真正该看的东西。
    /// </summary>
    /// <summary>
    /// **吃法兰场的判据名单**（判据与参考行）。**加判据时要同步加这里** —— 不加的后果是它在场没解到位／保温分界判不了时
    /// 照样报数，而那正是 <see cref="MarkUndeterminedIfFieldsFailed"/> 与 <see cref="MarkUndeterminedIfInsulBoundaryUnknown"/> 要挡的事。
    /// ★ R47 第三轮 N2（2026-09-13）：名单只此一份，两个「标成无法判定」的后置遍历都读它 —— 此前保温分界那一份只标了两条，
    ///   法兰最高温／自给率／热平衡残差这些同样吃法兰场的参考行照样报数。
    /// </summary>
    private static readonly string[] dependsOnFlangeFields =
    {
        LineResult.Key.NetFlux, LineResult.Key.DiscTemp, LineResult.Key.FlangeDip, LineResult.Key.Ramp,
        // R48 B（2026-09-14 Opus 5）：热侧、冷侧新判据都吃法兰场（盘峰/舌区峰来自温度场，管根温度随法兰抽热动）
        LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc,
        LineResult.Key.FlangeStab, LineResult.Key.LocalStab, LineResult.Key.RampField, LineResult.Key.HeatBalance,
        LineResult.Key.FlangeTopTemp, LineResult.Key.SelfSupply, LineResult.Key.FlangeJ, LineResult.Key.HeatResidual,
        // ★ 2026-09-15 Opus 5（审查意见 minor：名单原有缺口）：集总升温用时参考量读逐片 QGenW（法兰电流场算的焦耳热）与 MassG（网格体积），
        //   吃法兰场，却不以名单里任何一项开头（Ramp 是「① 升温」、RampField 是「· 升温期法兰−管峰值」）⇒ 场没解到位、保温分界判不了、
        //   压接盖到管孔这三遍都漏掉了它，坏场上照样报用时。补进名单。
        LineResult.Key.RampHours,
        // 决 103（2026-09-24）：两条新硬判据都吃法兰场（法兰最高温、管孔净热流）
        LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat,
    };
    /// <summary>同一份名单的只读出口（测试拿它核对）；名单本体是上面那个字段（FieldConvergenceGateTests 的源码门按它的名字找）。</summary>
    public static IReadOnlyList<string> DependsOnFlangeFields => dependsOnFlangeFields;

    private static bool EatsFlangeFields(ConstraintOut ck)
        => DependsOnFlangeFields.Any(k => ck.Name.StartsWith(k, StringComparison.Ordinal));

    /// <summary>
    /// ★★★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-4）：**「判不了」后置标记的唯一定义** —— 逐片四道（场没解到位、保温分界判不了、压接段盖到管孔、压接段伸进圆盘）。
    /// 下面四个 MarkUndeterminedIf* 挑片、<see cref="PlateUndeterminedWhy"/>（求解器 Gate／PlateSlack、安装报告读它）都调这一份 ——
    /// 此前五道标记只写进 Checks，求解器的 Gate 只看 Ok／Converged、PlateSlack 读逐片原始量，没收敛的场照样被拿去抬旋钮。
    /// 管散热表超界那一道是按段的（<see cref="TubeUndetermined"/>），同样进 <see cref="PlateUndeterminedWhy"/>。
    /// </summary>
    public enum PlateMark { FieldsNotConverged, InsulBoundaryUnknown, ClampCoversHole, ClampIntoDisc }

    /// <summary>第 <paramref name="f"/> 片带不带标记 <paramref name="m"/>（唯一读法）。</summary>
    public static bool HasMark(FlangeOut f, PlateMark m) => f is not null && m switch
    {
        PlateMark.FieldsNotConverged => !f.FieldsConverged,
        PlateMark.InsulBoundaryUnknown => f.InsulBoundaryUndetermined,
        PlateMark.ClampCoversHole => f.ClampCoversHole,
        PlateMark.ClampIntoDisc => f.ClampIntoDisc,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    /// <summary>这一段的管解超出管表面散热表上限（管温场判不了；唯一读法）。</summary>
    public static bool TubeUndetermined(SegmentOut s) => s is not null && s.TubeLossTableExceeded;

    /// <summary>
    /// 第 <paramref name="j"/> 片吃法兰场的判据判不了的原因（人话，"" = 没有标记）：本片四道逐片标记 + 任一段管散热表超界（管温场错了，本片边界跟着错）。
    /// <paramref name="j"/> &lt; 0 = 全线任一片（求解器 Gate 用）。
    /// </summary>
    public static string PlateUndeterminedWhy(LineResult r, int j)
    {
        if (r is null) return "";
        var why = new List<string>();
        for (int k = 0; k < r.Flanges.Length; k++)
        {
            if (j >= 0 && k != j) continue;
            var f = r.Flanges[k];
            if (f is null) continue;
            if (HasMark(f, PlateMark.FieldsNotConverged)) why.Add($"{f.Name} 场没解到位（{f.FieldNote}）");
            if (HasMark(f, PlateMark.InsulBoundaryUnknown)) why.Add($"{f.Name} 图纸没有切点、保温分界判不了");
            if (HasMark(f, PlateMark.ClampCoversHole)) why.Add($"{f.Name} {ClampCoversHoleText(f.Mesh?.ClampLenMm ?? double.NaN)}");
            if (HasMark(f, PlateMark.ClampIntoDisc)) why.Add($"{f.Name} " + (f.Mesh is { } m ? ClampIntoDiscText(m) : "压接段伸进了圆盘"));
        }
        var tube = r.Segments.Where(TubeUndetermined).ToArray();
        if (tube.Length > 0)
            why.Add("管温超出管表面散热表的温度上限（" + string.Join("、", tube.Select(t => $"{t.Name} 最高 {t.TubeTMaxC:0} °C，表上限 {t.TubeLossTableHiC:0} °C")) + "）");
        return string.Join("；", why);
    }

    private static void MarkUndeterminedIfFieldsFailed(LineResult res, FlangeOut[] flanges)
    {
        var bad = flanges.Where(f => HasMark(f, PlateMark.FieldsNotConverged)).ToArray();   // 2026-09-15 Opus 5（J 路）：挑片走唯一定义
        if (bad.Length == 0) return;

        string who = string.Join("、", bad.Select(f => $"{f.Name}（{f.FieldNote}）"));

        foreach (var ck in res.Checks)
            if (EatsFlangeFields(ck))
            {
                ck.Undetermined = true;
                ck.Ok = false;
                ck.Note = "★ **无法判定**：这条判据吃法兰的场，而场没解到位 —— " + who
                        + "。**判不了不算过。** 原值仅供诊断，不得引用。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    /// <summary>
    /// R47 B（2026-09-13）：图纸路径推不出切点的片 ⇒ 舌片保温包到哪不知道 ⇒ 这片法兰的场就不是这个设计的场，
    /// 所有吃法兰场的判据与参考行（名单 <see cref="DependsOnFlangeFields"/>，与场没解到位那一遍**同一份**）
    /// 都判成**无法判定**并附注。判不了不算过。
    /// </summary>
    private static void MarkUndeterminedIfInsulBoundaryUnknown(LineResult res, FlangeOut[] flanges)
    {
        var bad = flanges.Where(f => HasMark(f, PlateMark.InsulBoundaryUnknown)).ToArray();   // 2026-09-15 Opus 5（J 路）：挑片走唯一定义
        if (bad.Length == 0) return;
        string who = string.Join("、", bad.Select(f => f.Name));
        foreach (var ck in res.Checks)
            if (EatsFlangeFields(ck))
            {
                ck.Undetermined = true; ck.Ok = false;
                ck.Note = "★ **无法判定**：图纸没有切点，保温分界判不了（" + who + "）—— "
                        + "舌片保温包到哪不知道，这片法兰的场就不是这个设计的场，吃法兰场的判据与参考行都不可信。请先做「分析几何变数」。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    /// <summary>R48（2026-09-15，Opus 5）：「压接段盖到了管孔」的说明文字（进界面输出框与判据附注，说人话、不带网格内部说法）。文字与 2026-09-14 生成器里那句相同。</summary>
    public static string ClampCoversHoleText(double clampLenMm)
        => $"压接长 {clampLenMm:0.###} mm 盖到了管孔：这块板没有足够长的舌片可供压接，本片电流与温度结果不可信";

    /// <summary>
    /// ★ 2026-09-23（HANDOVER §0.-20，F6 审查 #11／#12）：本片网格的**孔弧覆盖**与**图纸孔径核对**的说明句（不带片名；调用处照压接退化那两句的写法加「⚠ 片名：」、按片去重）。
    /// **只量不判**：不改任何判词、不把任何判据标成判不了（缺口或失配到多大该判不了，是业主的决定，见 §0.-20 待决定）。
    ///   · 孔弧缺口：<see cref="ShellMesh.HoleArcGapCount"/> &gt; 0（缺口弧长 &gt; ShellMesh.GeomTolMm，沿用已有值）⇒ 一句「管孔弧缺 x.xxx mm @ θ°，覆盖率 y」。
    ///   · 孔径核对（只在图纸路径，<paramref name="field"/> 非空）：图纸孔半径 = <see cref="PlateShapeAnalyzer.HoleRadiusOf"/>（与读图分析器同一个定义；按场实例缓存，见 DrawingHoleRadiusCached），
    ///     与建网格用的 rh = 管内径/2 + 壁厚（<paramref name="holeRadiusMm"/>）差 &gt; 一个栅格步 <see cref="ThicknessField.Step"/> ⇒ 一句写明差多少
    ///     （容差 = 栅格步：栅格分辨不出小于一步的差；等面积半径的误差界 s/√2 &lt; s，孔径真相同时不误报，推导见 HoleRadiusOf）。
    ///     图纸上找不到被材料包围的管孔 ⇒ 一句「无法核对」。
    ///   · ★ 2026-09-23（RING 审查后，M1）浮空料块：<see cref="MeshRecipe.FloatingComponents"/> &gt; 0（网格里有既不连管孔面、也不连压接格的料块）⇒ 一句写明块数与面积。
    ///     阈值只有「&gt; 0」；不剔除、不判（怎么处理是决 98 一并定的尺）。
    /// </summary>
    public static List<string> HoleArcDrawingNotes(ShellMesh mesh, ThicknessField? field, double holeRadiusMm)
    {
        var lines = new List<string>();
        string path = mesh.SourceField is not null ? "图纸路径" : "网格";
        static string Mm(double v) => Math.Abs(v) >= 0.0005 ? v.ToString("0.000") : v.ToString("0.###E+0");
        if (mesh.HoleArcGapCount > 0)
        {
            double totalGap = (1 - mesh.HoleArcCoverage) * 2 * Math.PI * mesh.HoleRadiusMm;
            lines.Add($"{path}管孔弧缺 {Mm(mesh.HoleArcMaxGapMm)} mm @ θ {HoleArcAngleText(mesh.HoleArcMaxGapMidDeg)}°，覆盖率 {mesh.HoleArcCoverage:0.0000}"
                    + $"（孔圆 r = {mesh.HoleRadiusMm:0.###} mm 上共 {mesh.HoleArcGapCount} 段没有孔边界面、合计 {Mm(totalGap)} mm；θ 从 +x 轴量，180° 是舌片一侧、电流进孔处）："
                    + "缺口处孔边等于绝缘，电流要绕到缺口两端进孔，缺口越长孔边电流密度峰（· 法兰 J_max）偏得越多（实测一例：W08 盘 R31 舌半宽 30 判决档缺 2.0 mm，J_max 比解析路径高 49 %，F3 核实记录 A1(5)）；"
                    + "缺不缺取决于栅格原点。本条只量不判，判据照常出数。");
        }
        if (mesh.Recipe is { FloatingComponents: > 0 } rc)   // 2026-09-23（RING 审查后，M1）
            lines.Add($"{path}网格里有 {rc.FloatingComponents} 块料（合计 {Mm(rc.FloatingAreaMm2)} mm²）与管孔面、压接格都不相连：这些料块没有电位与温度边界，电流为 0、温度只靠表面散热，"
                    + "片内最低温度等取全片最值的量会把它们算进去（合成盘探针上最低温度因此落到约 25 °C）；图纸孔径与 rh 失配时孔带按解析圆判料会切出这种料块（决 98 待定）。本条只量不判，判据照常出数。");
        if (field is not null)
        {
            double rDraw = DrawingHoleRadiusCached(field);
            if (double.IsNaN(rDraw))
                lines.Add($"图纸上没找到被材料包围的管孔，孔径无法与 rh = 管内径/2 + 壁厚 = {holeRadiusMm:0.000} mm 核对。本条只量不判。");
            else
            {
                double diff = rDraw - holeRadiusMm;
                if (Math.Abs(diff) > field.Step)
                    lines.Add($"图纸孔半径 {rDraw:0.000} mm（被材料包围的最大空腔的等面积圆）与 rh = 管内径/2 + 壁厚 = {holeRadiusMm:0.000} mm 差 {diff:+0.000;-0.000} mm，"
                            + $"超过一个栅格步 {field.Step:0.###} mm：网格按 rh 建孔边，图纸上的孔与它错位，孔边电流与热流按错位的孔算。本条只量不判，判据照常出数。");
            }
        }
        return lines;
    }

    /// <summary>
    /// 2026-09-23（§0.-20）：<see cref="PlateShapeAnalyzer.HoleRadiusOf"/> 按厚度场**实例**缓存。为什么要缓存：逐片循环在 RunOnce 里，外层耦合每轮都走一遍（F6 后生产细网格一次整线 119 轮），
    /// 空腔填充在栅格步 0.1 上约 0.1 s／片（探针实测，§0.-20 成本），不缓存就是每轮每片再付一次。键是实例：厚度场在全仓只在构造时写 T（Rasterize、LoadThickness 读文件），
    /// 之后当不可变用（LoadThickness 缓存、LineCase.FlangeFields 共享同一实例，缩放走 WithThickness 另造新实例）—— 与「tf 是共享的，不许就地追加」同一个约定。
    /// 守一道：命中时 T 数组引用、步长、图幅不同就重算。
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ThicknessField, Tuple<double[], double, double, double, int, int, double>> _drawHoleR = new();
    internal static double DrawingHoleRadiusCached(ThicknessField f)
    {
        if (_drawHoleR.TryGetValue(f, out var hit) && ReferenceEquals(hit.Item1, f.T) && hit.Item2 == f.Step && hit.Item3 == f.X0 && hit.Item4 == f.Z0 && hit.Item5 == f.Nx && hit.Item6 == f.Nz)
            return hit.Item7;
        double r = PlateShapeAnalyzer.HoleRadiusOf(f);
        _drawHoleR.AddOrUpdate(f, Tuple.Create(f.T, f.Step, f.X0, f.Z0, f.Nx, f.Nz, r));
        return r;
    }

    /// <summary>2026-09-23（§0.-20）：缺口中点角的文字（一位小数；舍入后的 −0 印成 0、−180 印成 180，同一个点不印两种写法）。</summary>
    public static string HoleArcAngleText(double deg)
    {
        double t = Math.Round(deg, 1);
        if (t == 0) t = 0.0;          // −0.0 == 0 为真，赋成 +0
        if (t == -180) t = 180;
        return t.ToString("0.#");
    }

    /// <summary>R48（2026-09-15，Opus 5）：「压接段伸进了圆盘」的说明文字（按 <see cref="ShellMesh.ClampIntoDisc"/> 与两个 x 生成；文字与 2026-09-14 生成器里那句相同）。</summary>
    public static string ClampIntoDiscText(ShellMesh m)
        => $"压接长 {m.ClampLenMm:0.###} mm 伸进了圆盘（压接段到 x = {m.ClampEndXMm:0.###} mm，舌片与圆盘在 x = {m.ClampTangentXMm:0.###} mm 相接）：舌片比压接长还短，这块板几何上不成立";

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5；数值把关人第十四轮）：**压接段盖到了管孔的片 ⇒ 吃法兰场的判据与参考行一律判不了**。
    /// 照 <see cref="MarkUndeterminedIfInsulBoundaryUnknown"/> 的做法，名单同 <see cref="DependsOnFlangeFields"/>。
    /// 此前（2026-09-14）这种板只在输出里报一句「⚠ …结果不可信」，判据照常出数、Ok 照常为真 —— 嘴上说不可信、表上当可信用。
    /// 读的是网格上的布尔位（<see cref="FlangeOut.ClampCoversHole"/> ← <see cref="ShellMesh.ClampCoversHole"/>），不读文字。
    /// </summary>
    private static void MarkUndeterminedIfClampCoversHole(LineResult res, FlangeOut[] flanges)
    {
        var bad = flanges.Where(f => HasMark(f, PlateMark.ClampCoversHole)).ToArray();   // 2026-09-15 Opus 5（J 路）：挑片走唯一定义
        if (bad.Length == 0) return;
        string who = string.Join("、", bad.Select(f => f.Name));
        double len = bad.Select(f => f.Mesh?.ClampLenMm ?? double.NaN).FirstOrDefault(v => !double.IsNaN(v), double.NaN);
        foreach (var ck in res.Checks)
            if (EatsFlangeFields(ck))
            {
                ck.Undetermined = true; ck.Ok = false;
                ck.Note = $"★ **无法判定**：压接长 {len:0.###} mm 盖到了管孔（{who}）—— 这块板没有足够长的舌片可供压接，"
                        + "电流从哪里进、压接处多热都算不成这个设计的样子，吃法兰场的判据与参考行都不可信。请加长舌片或缩短压接长。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5）：「压接段伸进了圆盘」的片 ⇒ 吃法兰场的判据与参考行一律判不了（审查意见 minor「同一种病只修了一半」）。
    /// 与 <see cref="MarkUndeterminedIfClampCoversHole"/> 同一个做法、同一份名单：此前这种板只进一句「⚠ …这块板几何上不成立」，吃法兰场的判据照常出数 ——
    /// 正是盖到管孔那次修掉的「嘴上说不可信、表上当可信用」。几何上的「舌片自由段」判据自己会不过，但图纸路径没有判据几何时那条本身判不了，兜不住别的判据。
    /// 读网格上的布尔位（<see cref="FlangeOut.ClampIntoDisc"/> ← <see cref="ShellMesh.ClampIntoDisc"/>），不读文字。
    /// </summary>
    private static void MarkUndeterminedIfClampIntoDisc(LineResult res, FlangeOut[] flanges)
    {
        var bad = flanges.Where(f => HasMark(f, PlateMark.ClampIntoDisc)).ToArray();   // 2026-09-15 Opus 5（J 路）：挑片走唯一定义
        if (bad.Length == 0) return;
        string who = string.Join("、", bad.Select(f => f.Name));
        double len = bad.Select(f => f.Mesh?.ClampLenMm ?? double.NaN).FirstOrDefault(v => !double.IsNaN(v), double.NaN);
        foreach (var ck in res.Checks)
            if (EatsFlangeFields(ck))
            {
                ck.Undetermined = true; ck.Ok = false;
                ck.Note = $"★ **无法判定**：压接长 {len:0.###} mm 伸进了圆盘（{who}）—— 舌片比压接长还短，这块板几何上不成立，"
                        + "电流从哪里进、压接处多热都算不成这个设计的样子，吃法兰场的判据与参考行都不可信。请加长舌片或缩短压接长。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    /// <summary>
    /// R48（2026-09-15，Opus 5）：**不吃管温场**的判据名单 —— 纯几何（舌片自由段、圆盘盖得住管孔）、闭式截面电流密度（设计电流 ÷ 必经截面积，与工况无关，
    /// 见 Judge 里那条的附注）。管散热表超界时除了这几条，其余一律判不了（管温场错了，电流、管根、法兰边界全跟着错）。
    /// ★ 2026-09-15 Opus 5（审查意见 minor「豁免名单自相矛盾」）：加「① 升温」—— 它与「法兰截面 J」出自同一个 DesignCurrent.Compute，
    ///   实际值是管子准静态电流沿 起点→目标 取峰值 ÷ 管截面（RampTwoNode.QuasiStaticCurrentA，闭式、不读段解与法兰场），附注里也只印这个闭式电流；
    ///   原先豁免了截面 J 却把它打成判不了，硬判据被白白判不了。实测电流模式另见 <see cref="IndependentOfTubeFieldFor"/>。
    /// </summary>
    // 2026-09-17 Opus 5：加「接合区保温缠得出来」—— 它只读算例里的保温厚度（WrapLimits.JointItems），一格场都不吃。
    private static readonly string[] independentOfTubeField = { LineResult.Key.FreeTab, LineResult.Key.DiscCover, LineResult.Key.SectionJ, LineResult.Key.Ramp, LineResult.Key.WrapTurns };
    /// <summary>同一份名单的只读出口（测试核对用）；与电流模式无关的那部分。</summary>
    public static IReadOnlyList<string> IndependentOfTubeField => independentOfTubeField;

    /// <summary>
    /// 2026-09-15 Opus 5（审查意见 minor）：按电流模式给出不吃管温场的判据名单。**实测电流模式**下「管 J」= 实测电流 ÷ 管截面
    /// （段解 SolveAtCurrent 原样带回给定电流），不吃管温场，一并豁免；控温反算模式下电流由管温场二分出来，管 J 吃管温场，不豁免。
    /// </summary>
    public static IReadOnlyList<string> IndependentOfTubeFieldFor(bool useMeasuredCurrent)
        => useMeasuredCurrent ? independentOfTubeField.Append(LineResult.Key.TubeJ).Append(LineResult.Key.TubeJPre103).ToArray() : independentOfTubeField;   // 决 103：原许用值对照行与「管 J」同一个值、同一个依赖

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5；数值把关人第十四轮「其余散热表超界检测」）：**有段的管解最高温超出管散热表上限 ⇒ 吃管温场的判据一律判不了**。
    /// 管散热表（SegmentSolver 里的 TubeLossTable）上限是 设定（带玻璃时与玻璃进口取大）+ 400 K，超出的部分 LossTable.Eval 静默钳住 ——
    /// 与法兰散热表那次（ShellThermal.LossTableHiC 的注释）同一个病：超出的那段散热冻在上限处，管算偏热、负反馈被削弱。
    /// 典型触发：实测电流模式下控温点填得比实际低很多。
    /// ★ 2026-09-16 Opus 5（J 路，合并把关待办 P3 第 9 条：同文件两处对管 J 的说法矛盾）：
    ///   <see cref="MarkUndeterminedIfFieldsFailed"/> 那段写着「纯管子的（管 J、④）不受影响」—— 那句说的是**法兰场**那一道标记（法兰场没解到位不影响管 J）；
    ///   **本道标记不同** —— 控温反算模式下电流是从管温场二分出来的，管 J 吃管温场，管散热表超界时管 J **一样判不了**；
    ///   只有实测电流模式才豁免（见下面 <see cref="IndependentOfTubeFieldFor"/>）。两句不矛盾，是两道不同的标记；判定口径一个字没改。
    /// </summary>
    private static void MarkUndeterminedIfTubeLossTableExceeded(LineResult res, SegmentOut[] segs, bool useMeasuredCurrent)
    {
        var bad = segs.Where(TubeUndetermined).ToArray();   // 2026-09-15 Opus 5（J 路）：挑段走唯一定义
        if (bad.Length == 0) return;
        string who = string.Join("、", bad.Select(s => $"{s.Name} 最高 {s.TubeTMaxC:0} °C，表上限 {s.TubeLossTableHiC:0} °C"));
        res.Notes.Add($"✗ 管温超出管表面散热表的温度上限（{who}），超出部分散热被钳住 ⇒ **吃管温的判据一律判不了**（判不了不算过）");
        var independent = IndependentOfTubeFieldFor(useMeasuredCurrent);   // 2026-09-15 Opus 5：实测电流模式下管 J 也豁免
        foreach (var ck in res.Checks)
            if (!independent.Any(k => ck.Name.StartsWith(k, StringComparison.Ordinal)))
            {
                ck.Undetermined = true; ck.Ok = false;
                ck.Note = $"★ **无法判定**：管温超出管表面散热表的温度上限（{who}）—— 超出部分的散热被钳在上限处，管温、电流与法兰边界都不是这个设计的真值。"
                        + "请核对控温点与实测电流。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5；审查意见：R48DiscInsulPerPlateGateTests 的逐片重解手抄了本处的逐片热解配方 —— 生产一改，门要么红、要么逼人再抄一遍）：
    /// 第 j 片壳热解的输入组装。原在 RunOnce 逐片循环里就地写，**原样搬出**（数逐位不变），整线求解与门共用。
    /// <paramref name="iJoint"/> = 本片接头电流 A（按电流定铜排热导）；<paramref name="fieldUsed"/> = 图纸路径本片实际用的厚度场（推切点的退路；
    /// 解析路径不读它）。图纸路径上它就是本片网格的 ShellMesh.SourceField（ShellMesh.BuildFromField 写入的正是传进去的场）。
    /// </summary>
    public static PlateThermalSetup PlateThermalInputs(LineCase c, int j, double iJoint, ThicknessField? fieldUsed)
    {
        int n = c.SegmentCount;
        bool analytic = c.FlangePlates.Length > 0;
        var plate = analytic ? c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)] : null;
        var s = new PlateThermalSetup();
        var p2 = SegmentSolver.Clone(c.Base);
        p2.TSetC = c.SetpointC[Math.Min(j, n - 1)];
        if (j < c.ClampTempC.Length) p2.BusbarClampTempC = c.ClampTempC[j];
        // ★ R48（2026-09-14，Opus 5）：逐片圆盘保温 —— 唯一取值 LineCase.DiscInsulEffectiveAt（解析 = 板件；图纸 = DiscInsul3dmPerPlateMm；
        //   都没有 = 整线值，与 p2 克隆来的逐位相同）。原先分两支写、图纸路径只留一句「逐片值被忽略」，现两条路同一个入口。
        p2.FlangeInsulThickMm = c.DiscInsulEffectiveAt(j);
        // ★★★★★ 逐片热导 G（2026-08-28，B 项）：**由该片自己的电流算出来**，不再靠人抄。
        //
        //   此前 --busg 要人手填「40,21.8,300」，那个 40×21.8 是从 `--cli --busbar`
        //   选型表里**抄**过来的 —— 同一个数两处来源，而且抄的是**共用片**那一行。
        //   可四片电流本来就不同（实测 685/1099/975/542 A），
        //   共用片走 √3 倍电流 ⇒ 需要的铜排更粗 ⇒ **G 本来就该逐片不同**。
        //
        //   第一性原理链（不循环）：A_j = I_j / J许用 ⇒ G_j = k_Cu·A_j/L。
        //   截面只依赖**载流**，不依赖夹持温度 —— 所以可以在解之前定下来。
        //   ⚠ 选型表里「导热需截面」那一支要 heatW 与夹持温度，是**循环**的，
        //     故不进这条链；它在解完之后作为**一致性检查**（见 BusbarConsistency）。
        if (c.Base.BusbarConductanceWPerK >= 0 && c.Base.BusbarJAllowAPerMm2 > 1e-9
            && c.Base.BusbarLenToSinkMm > 1e-9)
        {
            double aMm2 = iJoint / c.Base.BusbarJAllowAPerMm2;
            s.BusSectionForCurrentMm2 = aMm2;
            p2.BusbarConductanceWPerK = s.BusGWPerK =
                BusbarSizing.CuK * (aMm2 * 1e-6) / (c.Base.BusbarLenToSinkMm * 1e-3);
        }
        // 保温分界：解析几何用该片自己的分界（可为「全裸」= +∞ 之外）。
        // ★ R47 B（2026-09-13）：图纸路径**逐片、从几何来**。此前 insulX 用 new FlangePlate() 默认板的切点
        //   （盘 R60／舌 −200 那块与图纸无关的板）、tabBoundaryX 永远取 GeomForJudge[0]。
        //   现在：GeomForJudge[j]（分析几何变数反推的等效片，与 ⑤⑥ 同一组几何）→ 没有就从材料包络推切点
        //   → 推不出就把 ②′ 与 ③ 判成无法判定。**不许再用默认板**。
        double insulX, tabBoundX = double.NaN;
        // ★ R48（2026-09-14，Opus 5）：圆盘区按**半径**圈（见 ShellThermal 里那段），所以要把盘半径接进去。
        //   接不到就是 NaN ⇒ ShellThermal 退回旧的按切点口径，并把「退回了」写进 DiscZoneRule 让人看得见。
        //   ⚠ 本项目栽过两次的形态：**赋了值 ≠ 用它的人读得到**。这一行就是那根接线。
        double discRForZone = double.NaN;
        // ★ R48 续（2026-09-14，Opus 5）：**保温边界**也按半径 —— 由板件的唯一判定给出
        //   （FlangePlate.InsulDiscRadiusMm：默认分界给盘半径，显式分界给 NaN 仍按 x）。
        //   拿不到板件（图纸路径推不出等效片）时是 NaN ⇒ 退回按 x，并由 InsulRule 写明。
        double insulRForZone = double.NaN;
        string insulNote = ""; bool insulUndet = false;
        if (analytic) { insulX = plate!.InsulBoundaryXResolved; discRForZone = plate.DiscRadiusMm; insulRForZone = plate.InsulDiscRadiusMm; }
        else
        {
            var eqJ = c.GeomForJudgeAt(j);
            if (eqJ is not null)
            {
                insulX = eqJ.InsulBoundaryXResolved; tabBoundX = eqJ.Tangent().X;
                discRForZone = eqJ.DiscRadiusMm;
                insulRForZone = eqJ.InsulDiscRadiusMm;
                insulNote = $"保温分界 x={insulX:0.0}／舌盘分界 x={tabBoundX:0.0}，来自本片分析几何变数的等效片";
            }
            else if (fieldUsed is not null && TangentFromField(fieldUsed, out double xT, out string howT))
            {
                insulX = xT; tabBoundX = xT;
                insulNote = $"保温分界／舌盘分界 x={xT:0.0}，{howT}（没有分析几何变数，比等效片粗）";
            }
            else
            {
                insulX = double.PositiveInfinity;   // 没有分界 ⇒ 整片按裸露算，但判据要标成判不了
                insulUndet = true;
                insulNote = "图纸没有切点，保温分界判不了" + (fieldUsed is null ? "" : "（材料包络推不出舌盘分界）");
            }
        }
        s.P2 = p2;
        s.InsulX = insulX;
        s.SymmetricInsul = analytic && plate!.TwoTabs;
        s.TabBoundaryX = analytic ? plate!.Tangent().X : tabBoundX;
        s.TabInsulThickMm = analytic ? plate!.TabInsulThickMm : c.TabInsul3dmAt(j);
        s.DiscRadiusMm = discRForZone;
        s.InsulDiscRadiusMm = insulRForZone;
        s.ZoneByMaterialFraction = c.ZoneByMaterialFraction;   // 2026-09-23（F4）：只供门注入，生产恒 true
        s.InsulNote = insulNote; s.InsulUndetermined = insulUndet;
        return s;
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：第 j 片壳热解（ShellThermal.Solve）—— 边界与物性全部取自 <see cref="PlateThermalInputs"/> 的结果。
    /// 原是 RunOnce 里的局部函数 Thermal，原样搬出；σ(T) 内循环仍在 RunOnce 里（它重解电流场后再调本函数）。
    /// ★ 2026-09-15 Opus 5（合并）：加 <paramref name="lossTableHiC"/>／<paramref name="lossTableNodes"/> 两个**只供测试用**的参数，原样转给 ShellThermal.Solve（缺省 = 生产表，
    ///   RunOnce 不传，数逐位不变）。为什么：F 路的快门 R48ClampFaceGateTests.c 要在「改动前代码」的表（设定 + 200 K、60 节点）上逐位比基线树记录，
    ///   F 写于散热表上限改铂熔点之前；与 G1 门 d 的正规化同一个做法。不加这两个参数，门就只能手抄本函数的参数组装去调 ShellThermal.Solve。
    /// </summary>
    /// <param name="jHeatAPerMm2">★ 2026-09-23（F3）：**发热用**的 J（生产传 ShellCurrentResult.HeatJAPerMm2；形参原名 jMagAPerMm2，改名只为说清它是发热口径）。</param>
    /// <param name="jLocalAPerMm2">F3：逐格局部 J —— 盘峰／舌峰处报出的 J 与局部热稳定用它（ShellThermal.Solve 同名参数；生产传重构 J，null ⇒ 取 jHeatAPerMm2，与改前逐位相同）。</param>
    public static ShellThermalResult SolvePlateThermal(ShellMesh mesh, double[] jHeatAPerMm2, double tRootC, PlateThermalSetup s,
                                                       double lossTableHiC = double.NaN, int lossTableNodes = 0,
                                                       double[]? jLocalAPerMm2 = null)
        => ShellThermal.Solve(mesh, jHeatAPerMm2, s.P2, tRootC, s.InsulX,
                              symmetricInsul: s.SymmetricInsul,
                              tabBoundaryX: s.TabBoundaryX,
                              tabInsulThickMm: s.TabInsulThickMm,
                              discRadiusMm: s.DiscRadiusMm,
                              insulDiscRadiusMm: s.InsulDiscRadiusMm,
                              lossTableHiC: lossTableHiC, lossTableNodes: lossTableNodes,
                              zoneByMaterialFraction: s.ZoneByMaterialFraction,   // 2026-09-23（F4）
                              jLocalAPerMm2: jLocalAPerMm2);

    /// <summary>
    /// ★ R48 E（2026-09-15 Opus 5）：**解析路径**第 j 片的网格 —— 原在 RunOnce 逐片循环里就地写（管孔半径跟管外径 + FlangeMesher.Build），
    /// 原样搬出、数逐位不变；保温搜索求解器（<see cref="InsulationSearch"/>）逐片单解要用整线求解**同一个配方**造的网格（门与求解器不许手抄配方）。
    /// ⚠ 会把板件的管孔半径改成 管内径/2 + 管壁（与 RunOnce 一样，是原有副作用）；图纸路径（FlangePlates 为空）不在这里，抛异常。
    /// 网格尺寸读算例的 MeshFineMm／MeshCoarseMm／MeshFineRadiusMm／MeshInner*；要加密先调 <see cref="MeshAdapt.RefineWholeMesh"/>。
    /// 2026-09-15 Opus 5（合并）：要与 Solver 造算例同一个配方（含导航档统一细区半径）就调 <see cref="Solver.ApplyCaseMesh"/> —— 保温搜索就是这么改的（E 路审查 major）。
    /// </summary>
    public static ShellMesh PlateMeshAnalytic(LineCase c, int j)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (c.FlangePlates.Length == 0) throw new InvalidOperationException("图纸路径没有解析板件，逐片网格只能随整线求解读图纸造（本函数只管解析路径）。");
        Normalize(c);   // 管内径哨兵等（幂等；RunOnce 里已做过，走 Core 不再调）
        return PlateMeshAnalyticCore(c, j);
    }

    private static ShellMesh PlateMeshAnalyticCore(LineCase c, int j) => PlateMeshAnalyticWith(c, j, MeshRules.Production, null);

    /// <summary>
    /// ★ 2026-09-19，Fable 5.1（网格修复第二轮复核第 9 条）：解析路径逐片网格的**唯一配方**（片的孔半径跟管走 + FlangeMesher.Build 的参数表），
    /// 生产走上面那一行（生产规则、解析板材料）；门经 InternalsVisibleTo 传别的网格层规则／测试侧材料源做注入对照，不在测试里手抄这份参数表。
    /// </summary>
    internal static ShellMesh PlateMeshAnalyticWith(LineCase c, int j, MeshRules? rules, Func<FlangePlate, IMaterialField>? material)
    {
        var plate = c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)];
        // 管孔必须跟着管外径走，否则法兰与管子对不上
        plate.HoleRadiusMm = c.TubeIdMm * 0.5 + c.WallMm;
        return FlangeMesher.BuildWith(plate, rules, material?.Invoke(plate), 0, c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                                      c.Base.BusbarClampLengthMm,
                                      c.MeshInnerMm, c.MeshInnerRadiusMm);
    }

    /// <summary>
    /// ★ R48 E（2026-09-15 Opus 5）：第 j 片的电位场（ShellCurrent.SolveFor，电阻率取本片所属段的控温点）—— 原在 RunOnce 里写了两份（首解与 σ(T) 内循环），
    /// 原样搬出、数逐位不变；<paramref name="tempC"/> 非 null 即 σ(T) 重解。保温搜索求解器逐片单解调它。
    /// </summary>
    /// <summary>
    /// ★ 2026-09-23（F3）：门用改回的整线说明（只在 <see cref="LineCase.GateRevertFaceHeat"/> 为真时写进 Notes；生产从不出现）。
    ///   门 R48F3FaceHeatGateTests 拿它核「开关真的生效」，并在逐位对拍改前记录时把这一句剔掉。
    /// </summary>
    public const string FaceHeatRevertNote = "⚠ 本次是门用改回：热场发热按重构 J 的 ρJ²tA（改前口径，F3 前），不是交付口径的面发热。";

    public static ShellCurrentResult PlateCurrentField(LineCase c, ShellMesh mesh, int j, double iJointA, double[]? tempC = null)
    {
        int n = c.SegmentCount;
        double tSet = c.SetpointC[Math.Min(j, n - 1)];
        return ShellCurrent.SolveFor(c, mesh, iJointA, PtProps.For(c).Rho(tSet) * 1e3, tSet, tempC: tempC);
    }

    /// <summary>
    /// R47 B：从材料包络推舌盘分界（切点）—— 舌尖那一列的材料半宽 w、全场最大半宽 R（盘半径）：
    /// 盘的圆弧半宽 √(R²−x²) 首次等于舌半宽 w 的地方就是舌盘分界，x = −√(R²−w²)（w ≥ R 时为 0，即盘Ø56／舌 56 那种）。
    /// 这是**等宽舌**的几何；锥形舌的切点在直线与圆相切处，会偏 —— 所以它只是没有等效片时的退路，附注里说明。
    /// 推不出（没有材料、舌尖不在管轴左侧、盘半径为 0）返回 false。
    /// </summary>
    public static bool TangentFromField(ThicknessField f, out double xTangent, out string how)
        => FlangeMesher.TangentFromField(f, out xTangent, out _, out _, out how);   // R47 复修 M1：推法只有一份（网格锚点也用它）

    /// <summary>
    /// ★★ R48（2026-09-14，Opus 5 复审补；审查意见 major「集总模型漏改的调用点」）：**整片热稳定**（FlangeStability）与
    /// **升温两节点**（RampTwoNode）两个集总模型的输入与求解，从 Judge 里提出来的公开函数 —— 门与探针拿同一份算
    /// 「排除压接格」前后的数（R48ClampRecipeTests、R48ClampRecipeImpactTests），不手抄配方。
    /// </summary>
    public sealed class FlangeLumpedOut
    {
        /// <summary>最不利的一片（发热最大那片）的下标，及其板件与本函数建的网格</summary>
        public int Index;
        public FlangePlate Plate = null!;
        public ShellMesh Mesh = null!;
        /// <summary>排除压接格了没有（要求排除且网格带整面接触的压接格）</summary>
        public bool ClampExcluded;
        /// <summary>被排除的压接格面积 mm²（单面；没排除 = 0）</summary>
        public double ClampAreaMm2;
        /// <summary>热稳定用：圆盘区／舌片区单面面积 mm²（按切点分）、舌片到铜排的导热长 mm</summary>
        public double DiscAreaMm2, TabAreaMm2, TabLenMm;
        /// <summary>升温两节点用：包保温／裸露单面面积 mm²（按板件保温判定分）、体积 mm³、面积 mm²</summary>
        public double RampInsulAreaMm2, RampBareAreaMm2, VolumeMm3, AreaMm2;
        public FlangeStability.Result Stab = null!;
        public RampTwoNode.Inputs? RampIn;
        /// <summary>R48 G2（2026-09-15 Opus 5）：升温两节点吃的 DesignInputs（整线克隆、写入本片圆盘保温）—— 探针关掉新补两项复现老口径时用同一份。</summary>
        public DesignInputs? RampP;
        /// <summary>升温两节点结果；算不出来时为 null，原因在 <see cref="RampError"/></summary>
        public RampTwoNodeResult? Ramp;
        public string RampError = "";
        /// <summary>
        /// ★ R48 G2（2026-09-15 Opus 5）：本片热解的输入（<see cref="PlateThermalInputs"/>，舌保温／夹持温度／铜排热导都从这里取，与场解同一份）
        /// 与法兰节点的稳态场标定（<see cref="RampTwoNode.CalibrateNode"/>；R48 G2 复审改为四项一个节点口径）。
        /// 整片热稳定的夹持导度（<see cref="RampTwoNode.NodeCalibration.StabClampWPerK"/>）与升温两节点的四项都用 <see cref="Calib"/> 这一份。
        /// </summary>
        public PlateThermalSetup ThermalSetup = null!;
        public RampTwoNode.NodeCalibration Calib = null!;
        /// <summary>R48 G2（2026-09-15 Opus 5）：本片圆盘保温 mm（LineCase.DiscInsulEffectiveAt，只供附注与探针打印）</summary>
        public double DiscInsulMm = double.NaN;
        /// <summary>R48 G2 复审（2026-09-15 Opus 5）：网格取自本片场（FlangeOut.Mesh）没有 —— false 时是本函数按板件建的，那时节点标定判不了</summary>
        public bool MeshFromField;
        /// <summary>R48 G2 复审（2026-09-15 Opus 5）：升温两节点法兰节点的面积 mm²、体积 mm³（节点格 = 标定那一份；不排除时取网格原值）</summary>
        public double NodeAreaMm2, NodeVolumeMm3;
        /// <summary>R48 G2 复审（2026-09-15 Opus 5）：表面散热的场标定系数 κ（RampTwoNode.Model.SurfaceScale）与标定点上的管节点温度 °C（Model.TubeNodeTempAtRoot）；没解升温时 NaN</summary>
        public double SurfaceScale = double.NaN, TubeNodeAtCalibC = double.NaN;
        /// <summary>
        /// R48 G2 复审（2026-09-15 Opus 5）：改动前升温两节点吃的参考电阻 Ω（整片发热 ÷ 电流²，当时配管根温度 FlangeOut.TRootC）。
        /// **生产不再用**，只留给前后对照探针复现老口径（R48G2RampClampChannelTests 的 J0 回归逐位核它）。
        /// </summary>
        public double LegacyRefOhm = double.NaN;
        /// <summary>
        /// 2026-09-15 Opus 5（J 路，合并把关待办 P1-7）：<see cref="LineRunner.FlangeLumped"/> 逐片评时每片一份（下标 = 片号；FlangeLumpedAt 单独调时为空）。
        /// ⚠ 2026-09-17 Opus 5（J 路，复核「应修 5」）：**自引用环** —— 选中的那一片就是 <c>PerPlate[Index]</c>，即这个对象自己。
        ///   现在没人序列化 <see cref="FlangeLumpedOut"/>（DesignSpecStore 只序列化 DTO），不出事；哪天有人把它 JSON 出去就是环。
        ///   ⇒ 挂 <c>JsonIgnore</c> 先把这条路堵上（逐片结果本来也不该进存档，它是附注用的）；结构本身不动 —— 附注与门都按「下标 = 片号」读。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public FlangeLumpedOut[] PerPlate = Array.Empty<FlangeLumpedOut>();
        /// <summary>2026-09-15 Opus 5（J 路）：本片名（FlangeOut.Name），附注逐片并列用。</summary>
        public string PlateName = "";
        /// <summary>2026-09-15 Opus 5（J 路）：逐片评里整片热稳定判不了的片（发热 NaN 或模型判不了）；非空时返回的就是其中第一片、整条判不了。</summary>
        public int[] StabUndeterminedPlates = Array.Empty<int>();
        /// <summary>2026-09-15 Opus 5（J 路）：挑片的说明（逐片裕度，或哪几片判不了），进判据表附注。</summary>
        public string SelectNote = "";
    }

    /// <summary>
    /// ★ R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor 三条）：判据表「整片热稳定」那一行的附注，从 Judge 里原样提出来（门拿合成的 FlangeLumpedOut 就能验，不必跑整线）。改三处：
    ///   · 追不追加「夹持导度」那一段读 <see cref="FlangeStability.Result.ClampFromField"/>（原拿界面文字与「稳态场标定」比，文字一改就静默失效）；
    ///   · 夹持导度标定不出来而判不了时，末尾追加标定为什么不成（<see cref="RampTwoNode.NodeCalibration.Note"/>；原只有命令行附上，界面只写「标定不出来」）；
    ///   · 「割线值」换成人话（按稳态带走的热 ÷ 温差算，把舌片自身发热直接流进铜排的那份也算成了导热，可能偏大）。
    /// 其余文字（前缀、散热侧分项、参考量说明）与复审版逐字相同。
    /// </summary>
    public static string FlangeStabNote(FlangeLumpedOut lumped)
    {
        var st = lumped.Stab;
        bool clampNotCalibrated = st.ClampFromField && double.IsNaN(st.DClampDT);
        return (lumped.SelectNote.Length > 0 ? lumped.SelectNote + "。" : "")   // 2026-09-15 Opus 5（J 路）：逐片评的结果（哪片最小／哪片判不了）
             + "dQ_散热/dT ÷ dP_发热/dT，**须 > 1**；< 1 即正反馈失控（保温过头那条路）。"
             + (st.Undetermined
                ? "　" + st.Note + (clampNotCalibrated && lumped.Calib is { } k && k.Note.Length > 0 ? "；原因：" + k.Note + "。" : "")
                : $"　散热侧 表面 {st.DSurfDT:0.000} + 夹持 {st.DClampDT:0.000}（{st.ClampSource}）"
                  + $" + 管孔 {st.DTubeDT:0.000} = {st.DLossDT:0.000} W/K，"
                  + $"发热侧 {st.DGenDT:0.000} W/K")
             + (st.ClampGeomFallbackWhy.Length > 0 ? "　" + st.ClampGeomFallbackWhy : "")   // 2026-09-25：场标定不出 ⇒ 几何回退的原因句（DesignInputs.FlangeStabGeomClampFallback）
             + (st.Undetermined || !st.ClampFromField ? ""
                // R48 G2 复审二（2026-09-15 Opus 5）：按边界模式分句 —— 自由端没有铜排，原句「流进铜排的热 ÷（法兰均温 − 夹持温度）× (1 − 0.00)」对它是空话（合成门首跑看到）
                : lumped.Calib?.Mode == ShellThermal.ClampBoundary.Free
                ? "　舌端没有接铜排，夹持这一项为 0。"
                : $"　夹持导度 = 本片稳态场里流进铜排的热 ÷（法兰均温 − {(lumped.Calib?.Mode == ShellThermal.ClampBoundary.FixedTemp ? "夹持温度" : "铜排冷端温度")}）"
                  + $"× (1 − 夹持温度随法兰升的比例 {lumped.Calib?.FollowRatio ?? double.NaN:0.00})，与升温那一行同一个夹持假设；"
                  + "这样按稳态带走的热 ÷ 温差算，把舌片自身发热直接流进铜排的那一份也算成了导热，当「温度每升 1 K 多带走多少热」用可能偏大 —— "
                  + $"改按舌片导热截面算（{st.DClampGeomDT:0.000} W/K），裕度为 {st.MarginGeomClamp:0.00}。")
             + "　⚠ 现为**参考量**：限 1.0 是精确物理，但跨几何的量级还没攒够，"
             + "攒够再升为硬判据（管 J 当年也是这么升上去的）。";
    }

    /// <summary>
    /// ★ R48 G2 复审（2026-09-15 Opus 5；审查意见 blocker「修后的数仍作为正常参考量进判据表」）：升温期「法兰 − 管」那一行的附注。
    /// 物理把关人复核前这一行**判不了、不给数**。附注说清为什么不给数、模型补了什么、四项怎么标定、夹持温度的假设、舌片／圆盘按多厚保温算；
    /// 不与 215 K 比（旧模型的现役基准），不下「偏高／偏保守」的方向结论。写给现场工程师：说人话、不带判据代号与开关名。
    /// （第一版叫 RampChannelNote，把修后的数当正常参考量显示。）
    /// ★ R48 G2 复审二（2026-09-15 Opus 5；审查意见 major「位置说法没有依据」「标定只在标定点上成立」）改两处，依据都是复审探针输出：
    ///   ① 原句「不代表孔边最热处 —— 孔边比管热时……」撤回：壳解的孔面是定温边界、孔边温度就等于管温，孔边本身不会比管热；
    ///      升温末段最热铂在圆盘区以外（圆盘区按 r ≤ 盘半径圈；deliverable/R48_G2_复审_升温两节点逐项标定_B2_2026-09-15.txt 表一管温 1101.6 °C：最热 1122.8 > 盘峰 1116.2 °C，
    ///      误差预算同名文件管温 1098.3 °C：1109.2 > 1108.1 °C）。附注改说「不代表片上最热处」，**不写位置** —— 本函数对任何设计出同一句话，位置是算例的事：
    ///      复审二探针表三（deliverable/R48_G2_复审二_升温两节点_{B2,误差预算}_{带玻璃,空管}_2026-09-15.txt）：升温末段最热格都在**舌片区、舌保温底下、紧贴圆盘外缘**（圆盘区按 r ≤ 30 mm 圈，管孔半径 25.8 mm）—— B2 带玻璃 管温 1101.6 °C 最热 1122.8 °C 在 r 35.0 mm（离管孔半径 9.2 mm，x −35，厚 3.10 mm，J 11.7 A/mm²）；误差预算带玻璃 1098.3 °C 最热 1109.2 °C 在 r 31.0 mm（5.2 mm，J 12.9）；B2 空管 1101.6 °C 最热 1137.6 °C 在 r 37.0 mm（11.2 mm）；误差预算空管 1098.3 °C 最热 1119.1 °C 在 r 33.0 mm（7.2 mm）；圆盘区峰值都在 r 29.0～29.8 mm。升温前段（管温 300～500 °C）最热格与管温相差不到 0.2 K，在孔面上或离管孔半径 1.2 mm 以内（片上别处都比管冷）。
    ///   ② 补一句「四项只在稳态那一点上按场对上」：同两份输出表一，集总法兰温度与逐时刻壳解节点均温最大差 74.6 K（B2）／108.9 K（误差预算），
    ///      比第一版（56.5／59.2 K，deliverable/R48_G2_升温两节点补铜排通道_{B2,误差预算}_2026-09-15.txt）偏得更远。界面只说离开那一点不保证准，数写在这里。
    ///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：出处层次 —— 本段引的 G2 各份输出（改前基线、第一版、复审、复审二）都是在 r48_G2 工作树上跑的（底板 460d3b，即 **F 合入前的配方**：
    ///     压接按形心整格定电位与定温、3·hFine 自相似细带；未计空管管腔轴向辐射）。合并树的生产配方（F 压接面上定温、缺省不铺细带；G3 空管计管腔辐射）下**没有重跑**，
    ///     这些数只读作「那份配方下量到的」，不是合并树的数；文件首跑在 r48_G2 工作树，已原样拷入本树 deliverable（2026-09-15 Opus 5（I 路） 核：deliverable/R48_合并拷入证据清单_2026-09-15.txt 第一、二批），原句「合并树里没有」作废。
    /// </summary>
    public static string RampPendingNote(LineCase c, FlangeLumpedOut o)
    {
        var k = o.Calib; var gi = o.RampIn;
        string Insul(double mm) => DesignScreen.FlangeFaceInsulated(mm) ? $"{mm:0.0} mm 保温" : "裸铂表面";
        string head = $"★ **暂不给数，待复核**：现场升温方式（温控 {c.RampRateKPerH:0} K/h、空管）下「法兰温度 − 管温」的全程最大值。"
                    + "两节点模型已补经舌片流进铜排的通道与舌片保温，法兰这一节点的发热、表面散热、流进铜排、经管孔与管子换热四项都按本片稳态场标定；"
                    + "但这四项只在稳态那一点上与温度场对得上，升温途中离开那一点就不保证准。"
                    + "而且这个法兰温度是整片（含越来越冷的舌片）按体积平均的温度，不代表片上最热处 —— 片上局部比管热时整片平均仍可能比管冷，这个数看不到局部的危险，复核前不可引用。"
                    + "也不与 215 K 比：那是旧模型（没有这条通道、舌片按裸铂）对现役设备算的基准。";
        if (k is null) return head;
        string clamp = k.Mode switch
        {
            ShellThermal.ClampBoundary.Free => "舌端没有接铜排；",
            ShellThermal.ClampBoundary.FixedTemp => $"夹持 {k.TClampC:0} °C，升温期假设夹持温度随法兰温度按同一比例升（从铜排冷端 {k.TColdC:0} °C 起），铜排自身发热与热惯性没算；",
            _ => $"铜排冷端 {k.TClampC:0} °C，升温期冷端不变，铜排自身发热与热惯性没算；",
        };
        // R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「负零」）：带号数走 SizerResult.Signed（原 {x:+0.0;-0.0}，-0.05～0 之间 net8 印成「-+0.0」）
        string detail = k.Ok && gi is not null
            ? $"　本片稳态场：法兰均温 {k.TNodeC:0} °C、发热 {k.QGenW:0} W、表面散热 {k.QLossW:0} W、流进铜排 {k.QClampW:0} W、管孔流入 {SizerResult.Signed(k.QFromTubeW)} W（管根 {k.TRootC:0} °C）；"
              + clamp + $"舌片区按 {Insul(gi.TabInsulThickMm)}、圆盘按 {Insul(o.DiscInsulMm)}算散热。"
            : "　" + k.Note + "。";
        return head + (o.ClampExcluded ? "　压接段压在铜排下，不算进法兰这一节点。" : "") + detail + RampPerPlateNote(o);
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-7）：逐片都解了升温两节点 ⇒ 逐片结果并列（这一行复核前不给数、不挑片，但每片解没解出来要看得见）。
    /// ★ 2026-09-17 Opus 5（J 路，J6 复核「必须修 2」）：原来这一段是 <see cref="RampPendingNote"/> 里的一个局部串，而 RampPendingNote **只在选中片解出来的那一支**被调
    ///   （<see cref="Judge"/> 里 <c>if (lumped.Ramp is not null)</c>）；生产实测走的恰恰是另一支 —— 两份 J6 证据里四片全「升温两节点 算不出」
    ///   （deliverable/J路_J6_整片热稳定逐片评_{0,1}_本次开跑于2026-09-17_*.txt）⇒「逐片结果进附注」这条在实测算例上一次都没兑现，
    ///   界面上只剩选中那一片的错误原因，连是哪一片都没说。⇒ 提成公开函数，两支都调同一份（门也调这一份，不手抄）。
    /// </summary>
    public static string RampPerPlateNote(FlangeLumpedOut o)
        => o.PerPlate.Length > 1
            ? "　逐片：" + string.Join("；", o.PerPlate.Select((x, j) => (x.PlateName.Length > 0 ? x.PlateName : $"片{j}") + " "
                  + (x.Ramp is not null ? "两节点解出（复核前不给数）" : "算不出" + (x.RampError.Length > 0 ? "（" + x.RampError + "）" : ""))))
            : "";

    /// <summary>
    /// 见 <see cref="FlangeLumpedOut"/>。没有片或没有板件（解析板 FlangePlates／判据用等效片 GeomForJudge 都空）时返回 null。
    ///
    /// ══ excludeClampCells（生产传 true）—— 2026-09-14 Opus 5
    ///   生产网格压接段整面接触（<see cref="ShellMesh.ClampCell"/>，FlangeMesher.BuildFromField 配方 ③）：压接段里的铂压在铜排下、钉在夹持温度上，
    ///   ShellCurrent 把它当电极、不再算它的发热 ⇒ 壳解给的 QGenW 已不含这一段（量级见 deliverable/R48_判定网格配方影响_2026-09-14.txt）。
    ///   发热降了而散热面积、质量、导热长还按整片算 ⇒ 热稳定裕度偏大、升温「法兰−管温」偏小，**两个都偏乐观**。
    ///   ⇒ true 时：盘／舌散热面积、保温分区面积、质量（体积）、等效外半径与平均厚度（面积）都只数压接格以外的格；
    ///     舌片到铜排的导热长减去压接长（定温边界从舌端挪到了压接段内边）。
    ///   false，或网格不带压接格（老口径；压接段盖到管孔的退化几何回退）：与改动前逐位相同（面积、体积直接取网格原值，不重新累加）。
    ///   ⚠ 舌片导热长仍从 |TabEndXMm| 起算（没计延长段，改动前就如此，本次不改）。
    ///   ★ R48 G2 复审（2026-09-15 Opus 5）：升温两节点的节点格改取 <see cref="RampTwoNode.NodeCalibration.NodeCell"/>（场里不被边界钉住的格，与标定同一份）；
    ///     定温边界的生产网格上它就是压接格以外的格（前后对照探针的老口径逐位回归核过）；热导边界下场里不钉格 ⇒ 压接格也在节点里（它们在场的热平衡里）。
    ///     整片热稳定的盘／舌面积、导热长仍按压接格排除（本次不改）。
    ///
    /// ══ 量过的前后（deliverable/R48_集总模型排除压接格_2026-09-14.txt：误差预算设计、导航网格、生产配方，最不利片 HC1|HC2，排除 2400 mm²）
    ///   整片热稳定裕度 16.397（不排除）→ 15.246（排除）；升温两节点「法兰−管」峰值 22.05 K → 144.29 K（质量 564 → 383 g、裸露面积 6979 → 4579 mm²）。
    ///   改动前（老口径、同一算例）是 14.489 与 71.62 K（deliverable/R48A_基线树老口径记录_2026-09-14.txt，基线树上跑的）。
    ///   ⚠ 升温两节点模型**本来就没有铜排导热这条通道**（RampTwoNode 类注释列的三条机理里没有它；改动前老口径靠压接段那块面积的表面散热顶着）。
    ///     排除压接格后这条参考量少了那块面积、又没有铜排通道补上 ⇒ 现在偏保守一侧；要不要给 RampTwoNode 加铜排通道，待定（2026-09-14 Opus 5）。
    ///   ⛔ 上一句的「偏保守一侧」**撤回**（2026-09-15 Opus 5，物理把关人第十一轮）：22.05 K 与 144.29 K 都不可引用 ——
    ///     22 K 是两个假项（压接面按裸铂散热约 130 W、多算 180 g 热容）顶替出来的；144 K 缺铜排通道，同一模型里舌片又按裸铂 ε 0.18 多算散热约 150 W 量级，两项方向相反，谈不上哪一侧。
    ///
    /// ══ R48 G2（2026-09-15 Opus 5）：铜排通道与舌片保温一起补
    ///   · 舌片区（圆盘保温圈以外）按本片舌保温算散热与保温热容；整片热稳定的舌保温也改取同一份（解析路径与原来的板件值相同；图纸路径原取判据等效片上的值）。
    ///   · 升温期夹持温度随法兰温度按定比走（假设与局限见 RampTwoNode.Inputs.ClampFollowRatio）。
    ///   ⛔ 第一版只把铜排通道按节点格均温标定，参考电阻仍配管根温度、管孔仍按几何式 —— 修后 0.29／0.09 K 是两处口径错互相抵消的数，撤回、不可引用（审查意见 blocker）。
    ///     ⛔ 其中「互相抵消」一说撤回（R48 G2 复审二 2026-09-15 Opus 5）：两处改成节点口径后峰值 0.288 → 0.387 K（B2）、0.087 → 0.136 K（误差预算），仍在升温起点，不是抵消出来的；
    ///       「撤回、不可引用」不变 —— 理由换成：整片平均的节点温度看不到片上最热处，且沿轨迹与壳解对不上（见下面复审二那段）。
    ///
    /// ══ R48 G2 复审（2026-09-15 Opus 5）：一个节点口径、四项在稳态场上标定（<see cref="RampTwoNode.CalibrateNode"/>）
    ///   · 发热：参考电阻 = 节点格焦耳热 ÷ 场电流²，参考温度 = 节点温度（原：整片发热 ÷ 电流² 配管根温度）；
    ///   · 表面散热：配方 × κ，κ 在节点温度上还原节点格场散热；
    ///   · 流进铜排：G_夹 = 流进铜排 ÷（节点温度 − 夹持参考温度），夹持随法兰按比例升；
    ///   · 管孔：孔侧导度 = 管孔流入 ÷（管根温度 − 节点温度），与管壁翅片导度串联（原：孔壁几何式）。
    ///   · 整片热稳定的夹持导度 = G_夹·(1 − r)（与升温同一个夹持假设；原传 G_夹）；舌片几何导度的裕度另算一份进附注当灵敏度对照。
    ///   · 图纸路径：网格取本片场的那一张（原按判据等效片另建一张，节点质量与标定不在同一张网格上）；没有场网格才自己建，那时标定判不了。
    ///   · 判据表那一行在物理把关人裁决前**判不了、不给数**（<see cref="RampPendingNote"/>）；升温两节点照样解，数留在 <see cref="FlangeLumpedOut.Ramp"/> 给前后对照探针。
    ///   前后对照：deliverable/R48_G2_复审_升温两节点逐项标定_B2_2026-09-15.txt 与 …_误差预算_2026-09-15.txt（R48G2RampClampChannelTests）；
    ///   场对账快门 R48G2RampClampChannelGateTests 五。
    ///
    /// ══ R48 G2 复审二（2026-09-15 Opus 5；审查意见 major「管孔按稳态割线标定被自己的壳解数据证伪」）：如实写明，**模型没改**，交物理把关人
    ///   · 四项标定只保证在标定点上逐项还原场（快门五）；沿升温轨迹比第一版偏得更远（两版输出各自的表一，准静态壳解同一片网格）：
    ///     集总法兰 − 壳解节点均温 最大差 B2 56.5 → 74.6 K、误差预算 59.2 → 108.9 K；升温末段 B2 +16.6 → +34.9 K、误差预算 +19.8 → +39.3 K
    ///     （第一版 deliverable/R48_G2_升温两节点补铜排通道_{B2,误差预算}_2026-09-15.txt；复审 deliverable/R48_G2_复审_升温两节点逐项标定_{B2,误差预算}_2026-09-15.txt）。
    ///   · 管孔孔侧「导度」在壳解上沿轨迹不是常数：Q孔 ÷（管温 − 节点均温）误差预算管温 298/502/698/898/1098 °C 上 0.258/0.193/0.116/0.0215/−0.086 W/K（标定值 0.02166），
    ///     B2 0.228 → −0.008 W/K（标定值 0.1179）—— 被分布发热的偏置主导，单点割线不是导度。审查另指：整片热稳定的管孔项按孔壁几何式 4.05 W/K，与升温两节点的 0.022 W/K 差约 180 倍，两处各算各的。
    ///   · 管孔流向与温差反号时标定直接失败 ⇒ 设计记录 W08（一段、三段）的升温两节点不解，判据表那一行写算不出来并说原因（快门四乙记录的是这个缺陷，不是成立条件）。
    ///   · 建议的改法（切线导度 + 发热偏置，两处同源）交物理把关人定；复审二探针在标定点上量了切线导度与偏置、沿轨迹给了预测对照（只描述），见 R48G2RampClampChannelTests：
    ///     铜排：切线 0.301／0.366 W/K（B2／误差预算带玻璃），与整片热稳定现取的 G·(1−r) 0.296／0.330 同量级；按「切线 + 偏置 × 节点发热比」沿轨迹预测与壳解差 0.2～7.5 %（四跑，不计升温起点那几十毫瓦的点）；
    ///     管孔：切线 0.698／0.685 W/K、偏置 −96／−122 W，割线 0.118／0.022，整片热稳定的孔壁几何式 4.045／4.052 W/K（约大 6 倍）；同法预测升温前中段（管温 300～900 °C）比壳解大 1.9～5.6 倍（B2 302 °C：+31.1 对 +13.8 W；误差预算 898 °C：+18.2 对 +3.3 W）⇒ 管孔那一项换成切线 + 偏置也不够。
    ///   · 空管到温稳态上（升温轨迹的终点）两个设计的最不利片 HC2|HC3 管孔流向与温差反号（B2 −21.5 W、误差预算 −19.1 W）⇒ 空管那张判据表的升温行写算不出来。
    ///   · 现役锚点（Program.cs「--flangestab」现役几何走本函数）：最不利片 HC1|HC2 管孔 +37.4 W 而节点均温 1280 °C 高于管根 1044 °C，同样标定不了；老模型（甲）峰值 333.4 K（与 §4.2s 的 215 K 不是同一条输入路径）；
    ///     准静态壳解最热铂比管温高 +466 K，在舌端（r 约 186 mm、离管孔约 160 mm、裸舌）。数见 deliverable/R48_G2_复审二_升温两节点_现役锚点_2026-09-15.txt。
    ///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：出处层次 —— 本段引的 G2 各份输出（改前基线、第一版、复审、复审二）都是在 r48_G2 工作树上跑的（底板 460d3b，即 **F 合入前的配方**：
    ///     压接按形心整格定电位与定温、3·hFine 自相似细带；未计空管管腔轴向辐射）。合并树的生产配方（F 压接面上定温、缺省不铺细带；G3 空管计管腔辐射）下**没有重跑**，
    ///     这些数只读作「那份配方下量到的」，不是合并树的数；文件首跑在 r48_G2 工作树，已原样拷入本树 deliverable（2026-09-15 Opus 5（I 路） 核：deliverable/R48_合并拷入证据清单_2026-09-15.txt 第一、二批），原句「合并树里没有」作废。
    /// </summary>
    /// <summary>
    /// ★ R48 G2 复审二（2026-09-15 Opus 5；审查意见 minor「探针和门手抄了热稳定裕度的公式」）：<see cref="FlangeLumped"/> 里整片热稳定那一次调用原样搬出，
    /// 夹持导度由调用方给（生产传 <see cref="RampTwoNode.NodeCalibration.StabClampWPerK"/>；前后对照探针传 G 复现第一版口径、传 null 复现改前几何口径）——
    /// 其余参数（盘／舌面积、本片圆盘保温、舌片导热截面与导热长、管孔截面与盘宽、本片舌保温）逐个取自 <paramref name="o"/> 与本片场 <paramref name="fw"/>，与搬移前逐位相同。
    /// <paramref name="o"/> 必须是 FlangeLumped 算好面积与热解输入之后的那一份。
    /// </summary>
    /// <summary>
    /// ★ 2026-09-25：整片热稳定的夹持导度要不要从「稳态场标定」回退到「舌片几何」（<see cref="DesignInputs.FlangeStabGeomClampFallback"/>）。
    /// 回退条件（同时成立）：口径 = 决103；开关开；本片有稳态场（节点格数 &gt; 0）且不是自由端；场标定的铜排项没成、而且 法兰均温 − 夹持参考温度 &lt; <see cref="RampTwoNode.CalibMinDeltaK"/>。
    /// 标定失败的其他原因不回退（没有场、带走的热为负而温差够）。返回给判据附注的原因句。
    /// </summary>
    internal static (bool Fallback, string Why) GeomClampFallback(RampTwoNode.NodeCalibration? k, CriteriaRuleSet rs, bool enabled)
    {
        if (rs != CriteriaRuleSet.决103 || !enabled || k is null) return (false, "");
        if (k.ClampOk || k.Mode == ShellThermal.ClampBoundary.Free || k.NodeCells <= 0) return (false, "");
        double dT = k.TNodeC - k.TClampC;
        if (!double.IsFinite(dT) || dT >= RampTwoNode.CalibMinDeltaK) return (false, "");
        string refName = k.Mode == ShellThermal.ClampBoundary.FixedTemp ? "夹持温度" : "铜排冷端温度";
        return (true, $"★ 经舌片流进铜排的导度从稳态场标定不出来：法兰均温 {k.TNodeC:0.0} °C 不比{refName} {k.TClampC:0.0} °C 高出 {RampTwoNode.CalibMinDeltaK:0.#} K"
                    + "（这一点铜排在给法兰加热，热 ÷ 温差没有物理意义）⇒ 决 103 口径改按舌片导热截面的几何导度算这一项（FlangeStability 一直算着的那份，比场标定值偏小、裕度偏保守；"
                    + "改回 DesignInputs.FlangeStabGeomClampFallback = false 或口径改回决103前 ⇒ 仍判不了）。");
    }

    public static FlangeStability.Result StabilityCheck(LineCase c, FlangeOut fw, FlangeLumpedOut o, double? clampConductanceWPerK)
    {
        var pl = o.Plate;
        double tThick = double.IsNaN(pl.TabThicknessMm) ? pl.ThicknessMm : pl.TabThicknessMm;
        // ⚠ 评估温度取**管根温度**，不取片上最高温：FlangeStability 的护栏写明
        //   发散几何上片温会跑到几千度，拿那个温度判出来的全是垃圾。
        return FlangeStability.Check(
            c.Base, fw.QGenW, fw.TRootC,
            o.DiscAreaMm2, o.TabAreaMm2, c.DiscInsulEffectiveAt(o.Index),   // R48：逐片圆盘保温走唯一取值 LineCase.DiscInsulEffectiveAt（合并 A/D，2026-09-15 Opus 5）
            2 * pl.TabEndHalfWidthMm * tThick, o.TabLenMm,
            2 * Math.PI * pl.HoleRadiusMm * pl.ThicknessMm,
            pl.DiscRadiusMm - pl.HoleRadiusMm,
            o.ThermalSetup.TabInsulThickMm,                             // R48 G2：本片热解实际用的舌保温（原读板件 pl.TabInsulThickMm，解析路径同值）
            clampConductanceWPerK: clampConductanceWPerK);
    }

    public static FlangeLumpedOut? FlangeLumped(LineCase c, IReadOnlyList<FlangeOut> flanges, bool excludeClampCells = true)
    {
        var plates = c.PlatesForJudge();   // 2026-09-17 Opus 5：同一句就地三元式原是四处各写一份，统一走 LineCase.PlatesForJudge
        if (flanges.Count == 0 || plates is not { Length: > 0 }) return null;

        // ★★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-7）：**逐片评，取整片热稳定裕度最小的那片**；有片判不了 ⇒ 整条判不了。
        //   此前是「最不利的一片 = 发热最大那片（dP/dT ∝ 发热）」：只评一片，而裕度 = 散热导数 ÷ 发热导数，散热导数随本片保温、面积、夹持逐片不同，
        //   发热最大的不一定裕度最小；发热为 NaN 的片在 `>` 比较里直接被跳过、一次都没评。
        //   实测 W08（导航网格）：发热最大的入口片 964.85 W 裕度 10.5231，出口片 796.07 W 裕度 9.6754 更小（deliverable/J路_J6_整片热稳定逐片评_0_本次开跑于2026-09-15_191234.txt）。
        //   升温两节点那一行复核前不给数（见 RampPendingNote），它的逐片结果放进附注（RampPendingNote 读 PerPlate），不单独挑片。
        var all = new FlangeLumpedOut[flanges.Count];
        for (int j = 0; j < flanges.Count; j++) all[j] = FlangeLumpedAt(c, flanges, j, excludeClampCells)!;
        var blind = Enumerable.Range(0, all.Length).Where(j => double.IsNaN(flanges[j].QGenW) || all[j].Stab.Undetermined).ToArray();
        int wj;
        if (blind.Length > 0)
            wj = blind[0];
        else
        {
            wj = 0;
            for (int j = 1; j < all.Length; j++)
                if (all[j].Stab.Margin < all[wj].Stab.Margin) wj = j;
        }
        var o = all[wj];
        o.PerPlate = all;
        if (blind.Length > 0)
        {
            o.StabUndeterminedPlates = blind;
            o.SelectNote = "逐片评整片热稳定：" + string.Join("、", blind.Select(j => flanges[j].Name
                         + (double.IsNaN(flanges[j].QGenW) ? " 发热算不出（NaN）" : " 判不了" + (all[j].Stab.Note.Length > 0 ? "（" + all[j].Stab.Note + "）" : ""))))
                         + " ⇒ **整条判不了**（一片没评到就不能说全线稳定）";
        }
        else
            o.SelectNote = "逐片评整片热稳定，取裕度最小的一片：" + string.Join("　", all.Select((x, j) => $"{flanges[j].Name} {x.Stab.Margin:0.00}"));
        return o;
    }

    /// <summary>
    /// 第 <paramref name="wj"/> 片的两个集总模型（整片热稳定、升温两节点）的输入与求解。
    /// 2026-09-15 Opus 5（J 路）：从 <see cref="FlangeLumped"/> 原样搬出（纯搬移，数逐位不变），逐片评时每片调一次。
    /// </summary>
    public static FlangeLumpedOut? FlangeLumpedAt(LineCase c, IReadOnlyList<FlangeOut> flanges, int wj, bool excludeClampCells = true)
    {
        // ★ 2026-09-18 Opus 5（合并 J×L）：J 路把本函数从 FlangeLumped 搬出来时，把那句就地三元式一起搬了过来；
        //   L 路 2026-09-17 已把同一句提成 LineCase.PlatesForJudge（全仓唯一一份），这里改调它 —— 逐字等价，数逐位不变。
        //   门 R48RampActualCurrentGateTests.f 盯着「不许留第二份」。
        var plates = c.PlatesForJudge();
        if (flanges.Count == 0 || plates is not { Length: > 0 } || wj < 0 || wj >= flanges.Count) return null;
        var fw = flanges[wj];
        var pl = plates[Math.Min(wj, plates.Length - 1)];

        // ★ R48 G2 复审（2026-09-15 Opus 5；审查意见 minor「图纸路径上两份格子不是同一张网格」）：有本片场的网格就用它 —— 质量、面积、排除位与标定同一张。
        //   解析路径场网格就是下面同参数建的那一张（RunOnce 对同一块板件调同一个 FlangeMesher.Build）⇒ 逐位不变；
        //   图纸路径原先按判据等效片另建一张，标定却在图纸网格上。没有场网格（片没解出来）才自己建，那时节点标定判不了。
        bool meshFromField = fw.Mesh is { CellCount: > 0 };
        var mesh = meshFromField
                 ? fw.Mesh!
                 : FlangeMesher.Build(pl, 0, c.MeshFineMm, c.MeshCoarseMm,
                                      c.MeshFineRadiusMm, c.Base.BusbarClampLengthMm,
                                      c.MeshInnerMm, c.MeshInnerRadiusMm);
        bool skip = excludeClampCells && mesh.ClampFullFaceActive;   // 2026-09-15 Opus 5（J 路，P2-5）：整面接触的唯一定义（原为长度 = 单元数：全假数组也会扣压接长）
        var o = new FlangeLumpedOut { Index = wj, PlateName = fw.Name, Plate = pl, Mesh = mesh, ClampExcluded = skip, MeshFromField = meshFromField };

        // 盘/舌面积按**切点**分 —— 与 DesignScreen.Extract 同一个口径（同一个函数 AreaByTangent），不另立标准。
        // （原来调 DesignScreen.Extract 只取这两个面积，那里还解一遍电流场、结果没人用 ⇒ 改调它分面积的那一份，skip = false 时面积逐位相同。）
        (o.DiscAreaMm2, o.TabAreaMm2) = DesignScreen.AreaByTangent(mesh, pl.Tangent().X, excludeClampCells: skip);
        if (skip)
        {
            double a = 0, v = 0, ac = 0;
            for (int k = 0; k < mesh.CellCount; k++)
                if (mesh.ClampCell[k]) ac += mesh.Area[k];
                else { a += mesh.Area[k]; v += mesh.Area[k] * mesh.Thickness[k]; }
            o.AreaMm2 = a; o.VolumeMm3 = v; o.ClampAreaMm2 = ac;
        }
        else { o.AreaMm2 = mesh.TotalArea; o.VolumeMm3 = mesh.VolumeMm3; }
        o.TabLenMm = Math.Abs(pl.TabEndXMm) - (skip ? c.Base.BusbarClampLengthMm : 0.0);

        // ★ R48 G2（2026-09-15 Opus 5）：本片热解的输入（舌保温、夹持温度、铜排热导）与场解同一份。
        // ★ R48 G2 复审（2026-09-15 Opus 5）：法兰节点四项从本片稳态场标定（一个节点口径，见 RampTwoNode.CalibrateNode）；节点格 = 场里不被边界钉住的格（excludeClampCells 同一个开关）。
        o.ThermalSetup = PlateThermalInputs(c, wj, fw.CurrentA, fw.Mesh?.SourceField);
        o.DiscInsulMm = c.DiscInsulEffectiveAt(wj);
        o.Calib = RampTwoNode.CalibrateNode(meshFromField ? mesh : null, fw.TField, fw.CellGenW, fw.CellLossW, fw.FieldBoundaryCell,
                                            fw.QClampW, fw.QFromTubeW, fw.TRootC, fw.CurrentA, o.ThermalSetup.P2, excludeClampCells);

        // R48 G2 复审：夹持导度与升温两节点同一份标定、同一个夹持假设 G·(1 − r)（原传 G）；标定不出来 = NaN ⇒ 判不了。
        // R48 G2 复审二（2026-09-15 Opus 5）：调用搬进 StabilityCheck（参数逐个不变），探针复现第一版口径时换夹持导度调同一份。
        // ★ 2026-09-25：标定不出来且原因是温差不够（铜排比法兰热、或只差不到 CalibMinDeltaK）⇒ 决 103 下改按几何导度（DesignInputs.FlangeStabGeomClampFallback；改回 = false 或口径决103前 ⇒ 照旧判不了）
        var (geomFallback, geomWhy) = GeomClampFallback(o.Calib, c.RuleSet, c.Base?.FlangeStabGeomClampFallback ?? true);
        o.Stab = StabilityCheck(c, fw, o, geomFallback ? null : o.Calib.StabClampWPerK);
        if (geomFallback) o.Stab.ClampGeomFallbackWhy = geomWhy;

        try
        {
            double shareF = fw.Shared ? Math.Sqrt(3.0) : 1.0;
            double iSeg = fw.CurrentA / Math.Max(1e-9, shareF);
            o.LegacyRefOhm = fw.QGenW / Math.Max(1e-9, fw.CurrentA * fw.CurrentA);   // 改动前的参考电阻，生产不再用（见字段注释）
            // ★ R48 续（2026-09-14，Opus 5）：保温面积按板件的唯一判定分（默认按半径），不再各抄一份 x 规则。
            // ★ R48 G2 复审（2026-09-15 Opus 5）：节点格与标定同一份（Calib.NodeCell）；没有节点格（没有场，升温不解）时退回压接格排除位，只给老口径对照的输入用。
            bool nodeMask = o.Calib.NodeCell.Length == mesh.CellCount;
            double aIns = 0, aBare = 0, aNode = 0, vNode = 0;
            for (int k = 0; k < mesh.CellCount; k++)
            {
                if (nodeMask ? !o.Calib.NodeCell[k] : skip && mesh.ClampCell[k]) continue;   // R48 复审补（2026-09-14 Opus 5）：压接格不在铂的热平衡里
                if (pl.UnderDiscInsulation(mesh.Centroid[k].X, mesh.Centroid[k].Z)) aIns += mesh.Area[k]; else aBare += mesh.Area[k];
                aNode += mesh.Area[k]; vNode += mesh.Area[k] * mesh.Thickness[k];
            }
            if (!excludeClampCells) { aNode = o.AreaMm2; vNode = o.VolumeMm3; }        // 老口径：直接取网格原值（与改动前逐位相同）
            o.NodeAreaMm2 = aNode; o.NodeVolumeMm3 = vNode;
            o.RampInsulAreaMm2 = aIns; o.RampBareAreaMm2 = aBare;
            double holeR = pl.HoleRadiusMm;
            double tubeAreaMm2 = Math.PI * ((holeR * holeR)
                               - (holeR - c.WallMm) * (holeR - c.WallMm));
            var gRamp = new RampTwoNode.Inputs
            {
                WallMm = c.WallMm,
                FlangeMassG = vNode * Materials.PtDensity * 1e-6,
                FlangeAreaInsulMm2 = aIns, FlangeAreaBareMm2 = aBare,
                // ★ R48 G2 复审（2026-09-15 Opus 5）：参考电阻与参考温度是同一节点口径的一对（原 整片发热 ÷ 电流² 配管根温度 ⇒ 节点温度下发热少算 ρ(均温)/ρ(管根)）
                FlangeResistanceRefOhm = o.Calib.ResistanceRefOhm, FlangeRefTempC = o.Calib.TNodeC,
                HoleRadiusMm = holeR,
                PlateEqOuterRadiusMm = Math.Sqrt(aNode / Math.PI + holeR * holeR),
                FlangeThickMm = vNode / Math.Max(1e-9, aNode),
                DesignCurrentA = iSeg,
                MaxCurrentA = c.Base.TubeJLimitAPerMm2 * tubeAreaMm2,   // 决 103：升温电流上限读卡交付的管 J 限值（改回 = 许用，逐位同改前）
                FromC = c.RampFromC, TargetC = c.RampTargetC,
                RampRateKPerH = c.RampRateKPerH,
                MaxHours = (c.RampTargetC - c.RampFromC)
                           / Math.Max(0.1, c.RampRateKPerH) * 1.4,
                SharedFactor = shareF,
                Mode = RampControl.TemperatureRamp,
                // ★ R48 G2（2026-09-15 Opus 5）：舌片区按本片舌保温；铜排通道取本片稳态场标定（与整片热稳定同一份）
                TabInsulThickMm = o.ThermalSetup.TabInsulThickMm,
                ClampConductanceWPerK = o.Calib.GEffWPerK,
                ClampFollowRatio = o.Calib.FollowRatio,
                ClampColdEndC = o.Calib.TColdC,
                // ★ R48 G2 复审（2026-09-15 Opus 5）：表面散热与管孔孔侧导度也在同一个节点温度上标定
                SurfaceCalibLossW = o.Calib.QLossW, SurfaceCalibTempC = o.Calib.TNodeC,
                HolePlateConductanceWPerK = o.Calib.HolePlateGWPerK,
            };
            o.RampIn = gRamp;
            // R48（2026-09-14，Opus 5）：升温两节点也按本片圆盘保温（原读整线 c.Base）
            var pRamp = SegmentSolver.Clone(c.Base);
            pRamp.FlangeInsulThickMm = c.DiscInsulEffectiveAt(wj);   // 合并 A/D（2026-09-15 Opus 5）：唯一取值
            o.RampP = pRamp;
            // R48 G2：标定不出来就不解（不许拿 NaN 或 0 顶上去算出一个看起来正常的数）；原因进判据表那一行
            if (!o.Calib.Ok) throw new InvalidOperationException(o.Calib.Note);
            var model = new RampTwoNode.Model(pRamp, gRamp);
            o.SurfaceScale = model.SurfaceScale;
            if (!(double.IsFinite(model.SurfaceScale) && model.SurfaceScale > 0))
                throw new InvalidOperationException($"法兰节点标定不了：表面散热的场标定系数不是正数（节点散热 {o.Calib.QLossW:0.0} W、配方在 {o.Calib.TNodeC:0} °C 上 {model.FlangeSurfaceRecipeW(o.Calib.TNodeC):0.0} W）");
            o.TubeNodeAtCalibC = model.TubeNodeTempAtRoot(fw.TRootC, o.Calib.TNodeC, o.Calib.QFromTubeW);
            o.Ramp = RampTwoNode.Solve(pRamp, gRamp);
        }
        catch (Exception ex) { o.Ramp = null; o.RampError = ex.Message; }
        return o;
    }

    /// ★★★★★ R48 B（2026-09-14 Opus 5）：**热侧「最热铂高出热偶读数」与冷侧「管根低于热偶读数」两条硬安全线的构造** —— Judge 只调这一处。
    /// 提成公开函数是为了让快门能拿**合成的** SegmentOut／FlangeOut 直接验「取哪一片、共用接头读哪两端、判不了怎么报」，
    /// 不必跑一次分钟级的整线解（Judge 本身还要解网格与升温两节点，够不着）。
    /// 逐片读数只走 <see cref="ThermocoupleBasis"/>，限值只从 <see cref="LineCase.HotOverTcMaxK"/>／<see cref="LineCase.ColdUnderTcMaxK"/> 读。
    /// </summary>
    public static (ConstraintOut Hot, ConstraintOut Cold) ThermocoupleChecks(LineCase c, SegmentOut[] segs, FlangeOut[] flanges)
    {
        ConstraintOut hot, cold;
        // ★ 2026-09-14 Opus 5（复审）：片数必须 = 段数 + 1。函数公开之后，传进来少几片时下面只按 flanges.Length 逐片判，
        //   缺的那几片热侧、冷侧**一次都没判过**却照样给出「过／不过」—— 正是「任何一片判不了 ⇒ 整条判不了」没盖住的洞。
        //   Judge 目前总传 n+1 片（RunOnce 按 FlangeCount 建数组），这道闸守的是以后别的调用方。
        int nSeg = segs?.Length ?? 0, nFl = flanges?.Length ?? 0;
        if (nSeg < 1 || nFl != LineSolver.FlangeCount(nSeg))
        {
            string why = nSeg < 1 ? "没有管段，热偶读数基准无从取"
                                  : $"法兰片数 {nFl} 与段数 {nSeg} 对不上（应为 {LineSolver.FlangeCount(nSeg)} 片）";
            ConstraintOut Blind(string name, double lim) => new()
            {
                Name = name, Unit = "K", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = lim, Ok = false, Undetermined = true, Where = "—",
                Note = $"★ **无法判定**：{why} —— 缺的那几片一次都没判过，只判剩下的几片等于让这条硬安全线在那几片上从来没判过。不要把它读成通过。",
            };
            return (Blind(LineResult.Key.HotOverTc, c.HotOverTcMaxK), Blind(LineResult.Key.ColdUnderTc, c.ColdUnderTcMaxK));
        }
        var tcJoints = Enumerable.Range(0, flanges!.Length)
                                 .Select(j => ThermocoupleBasis.At(segs, flanges, j)).ToArray();
        string PerPlate(Func<ThermocoupleBasis.Joint, double> v)
            => "逐片：" + string.Join("／", tcJoints.Select(t => $"{t.Name} {v(t):+0.00;−0.00}")) + " K";
        var blindHot = tcJoints.Where(t => t.HotBlind.Length > 0).ToArray();
        if (tcJoints.Length > 0 && blindHot.Length == 0)
        {
            var wh = tcJoints.OrderByDescending(t => t.HotK).First();
            var fh = flanges[wh.Plate];
            // 2026-09-15 Opus 5（J 路，合并把关待办 P3 第 8 条，注记）：这里带 1e-6 K 的浮点容差；保温搜索的默认评估函数（InsulationSearch.DefaultCriteria）归一裕度不带 ——
            //   两处「同一份」说的是式子（ThermocoupleBasis.HotSideK／ColdSideK）与限值（LineCase），判「恰在限值上」时两边差这 1e-6 K。冷侧同。
            bool hotOk = wh.HotK <= c.HotOverTcMaxK + 1e-6;
            hot = new ConstraintOut
            {
                Name = LineResult.Key.HotOverTc, Unit = "K", Kind = CheckKind.HardSafety,
                Actual = wh.HotK, Limit = c.HotOverTcMaxK, Ok = hotOk, Where = wh.Name,
                Note = $"基准 = {wh.RefHow}；最热的是{wh.HottestWhat} {wh.HottestC:0.0} °C"
                     + $"（圆盘峰 {wh.DiscPeakC:0.0}，峰位 r={fh.DiscMaxRMm:0.0} mm、J={fh.DiscMaxJAPerMm2:0.00}；"
                     + $"舌片区峰 {wh.TabPeakC:0.0}" + (double.IsNaN(fh.TabMaxRMm) ? "" : $"，峰位 r={fh.TabMaxRMm:0.0} mm")
                     + $"；管根 {wh.RootEnds}）；{ThermocoupleBasis.ModelJointNote(wh)}；{PerPlate(t => t.HotK)}"
                     + $"。控温热偶在段中点；允许高出 {c.HotOverTcMaxK:0.###} K（参数表「最热铂高出热偶读数 允许值」，默认 = 热偶在 1100 °C 的误差）：高出这个带就说不清了。"
                     + $"分区：{fh.DiscZoneRule}；{fh.InsulRule}"   // R48 B：进界面的说明不写「口径」这类内部词（同 ShellThermal.DiscZoneRule 那条规矩）
                     + (hotOk ? "" : NextAction.HotOverTcHigh)
            };
        }
        else
            hot = new ConstraintOut
            {
                Name = LineResult.Key.HotOverTc, Unit = "K", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = c.HotOverTcMaxK, Ok = false, Undetermined = true,
                Where = blindHot.Length > 0 ? string.Join("、", blindHot.Select(t => t.Name)) : "—",
                Note = (blindHot.Length > 0
                        ? $"★ **无法判定**：{blindHot.Length} 片判不了（" + string.Join("；", blindHot.Select(t => $"{t.Name}：{t.HotBlind}")) + "）。"
                        + "**任何一片判不了，整条就判不了** —— 只报剩下几片里最热的那个，等于让这条硬安全线在那几片上从来没判过。"
                        : "★ **无法判定**：没有法兰片。")
                     + "常见成因：几何来自 .3dm 厚度场而盘/舌分区认不出来，或盘半径 ≤ 孔半径，"
                     + "或舌半宽 = 盘半径（切点落在 x=0）且用双舌片 —— 那时分区规则会把整片都算成舌片、圆盘区成为空集。"
            };

        var blindCold = tcJoints.Where(t => t.ColdBlind.Length > 0).ToArray();
        if (tcJoints.Length > 0 && blindCold.Length == 0)
        {
            var wc = tcJoints.OrderByDescending(t => t.ColdK).First();
            bool coldOk = wc.ColdK <= c.ColdUnderTcMaxK + 1e-6;
            // 无法兰时这一端就已经低出限值 ⇒ 那一部分不是法兰造成的，照实说（按基线的定义，不是推断）
            string notFlange = !double.IsNaN(wc.ModelJointC) && wc.RefC - wc.ModelJointC > c.ColdUnderTcMaxK
                ? $"　⚠ 无法兰时交界管温就已比基准低 {wc.RefC - wc.ModelJointC:0.0} ℃（超过限值）—— 这一部分不是法兰造成的，法兰旋钮治不了它，要看控温点、管保温与段长。"
                : "";
            cold = new ConstraintOut
            {
                Name = LineResult.Key.ColdUnderTc, Unit = "K", Kind = CheckKind.HardSafety,
                Actual = wc.ColdK, Limit = c.ColdUnderTcMaxK, Ok = coldOk, Where = wc.Name,
                Note = $"基准 = {wc.RefHow}；管根较冷的是 {wc.RootColdWhere} {wc.RootColdC:0.0} °C（{wc.RootEnds}）；"
                     + $"{ThermocoupleBasis.ModelJointNote(wc)}；{PerPlate(t => t.ColdK)}"
                     + $"。控温热偶在段中点；允许低出 {c.ColdUnderTcMaxK:0.###} K（参数表「管根低于热偶读数 允许值」，默认 = 热偶在 1100 °C 的误差）：低出这个带，热偶就看不出来了。"
                     + notFlange
                     + (coldOk ? "" : NextAction.ColdUnderTcHigh)
            };
        }
        else
            cold = new ConstraintOut
            {
                Name = LineResult.Key.ColdUnderTc, Unit = "K", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = c.ColdUnderTcMaxK, Ok = false, Undetermined = true,
                Where = blindCold.Length > 0 ? string.Join("、", blindCold.Select(t => t.Name)) : "—",
                Note = (blindCold.Length > 0
                        ? $"★ **无法判定**：{blindCold.Length} 片判不了（" + string.Join("；", blindCold.Select(t => $"{t.Name}：{t.ColdBlind}")) + "）。"
                        + "**任何一片判不了，整条就判不了** —— 只报剩下几片里最冷的那个，等于让这条硬安全线在那几片上从来没判过。"
                        : "★ **无法判定**：没有法兰片。")
                     + "不要把它读成通过。"
            };
        return (hot, cold);
    }


    private static ConstraintOut[] Judge(LineCase c, LineResult res, SegmentOut[] segs,
                                         FlangeOut[] flanges, DesignInputs[] segParams)
    {
        var checks = new List<ConstraintOut>();
        int n = segs.Length;
        var rs = c.RuleSet;   // 决 103（2026-09-24）：判据口径（生产 = 决103；决103前 = 改回，下面每一处按它分支的地方都与改前逐位相同）

        // ── ① 升温：空管能否在限时内到目标温度
        //    法兰随管一起被加热，且**自身也发热**，故用本算例真实的法兰质量与自身焦耳热
        //    （RampSolver 据此反推法兰电阻）。这比 --ramp 另跑一次稳态耦合解取参考值更准。
        if (c.CheckRamp)
        {
            RampResult? worst = null; string where = "";
            // ★ R48（2026-09-15，Opus 5；数值把关人第十四轮）：RampSolver 解完自己核查表用到的温度在不在管散热表里（RampResult.LossTableExceeded）。
            //   **任何一段超界 ⇒ 这条参考量判不了**（与「任何一片判不了整条就判不了」同一条规矩），不是只看最不利那段。
            //   2026-09-15 Opus 5（审查意见 minor）：表上限已改为 目标、本段控温点、起点三者之最 + 200 K（RampSolver 建表处），「升温目标比控温点低 200 K 以上」
            //   这种合法输入不再出表；这里的检测留作兜底（能触发的主要是控温点或升温目标低于环境温度）。说明文字按结构化的 LossTableOut 在本处生成，用界面上的叫法。
            var rampOverTable = new List<(string Seg, RampResult R)>();
            for (int i = 0; i < n; i++)
            {
                double massPairG = flanges[i].MassG + flanges[i + 1].MassG;
                // ★ 折算到**段电流**基准：RampSolver 由 R_f = QGen_ref / I_ref² 反推法兰电阻，
                //   而它拿到的 I_ref 是段电流；共用片实际走的是 √3 倍的接头电流（§4.2h）。
                //   直接把共用片的 QGen 配段电流会把 R_f 高估 (√3)² = 3 倍。
                //   故逐片按各自电流折算 QGen → 段电流下的等效值，再取两片均值（RampSolver 内部 ×2）。
                double GenAtSegCurrent(FlangeOut f, double iSeg)
                    => f.CurrentA > 1e-9 ? f.QGenW * (iSeg / f.CurrentA) * (iSeg / f.CurrentA) : 0;
                double genRefW = 0.5 * (GenAtSegCurrent(flanges[i], segs[i].CurrentA)
                                        + GenAtSegCurrent(flanges[i + 1], segs[i].CurrentA));
                var rr = RampSolver.Solve(segParams[i], c.WallMm, massPairG, genRefW,
                                          segs[i].CurrentA, segs[i].SetpointC,
                                          c.RampFromC, c.RampTargetC, c.RampHours);
                // 最不利 = 升不到的优先，其次用时最长
                bool worse = worst is null
                    || (!rr.Reached && worst.Reached)
                    || (rr.Reached == worst.Reached &&
                        (rr.Reached ? rr.HoursToTarget > worst.HoursToTarget : rr.TPeakC < worst.TPeakC));
                if (worse) { worst = rr; where = segs[i].Name; }
                if (rr.LossTableExceeded) rampOverTable.Add((segs[i].Name, rr));
            }
            // ⚠ 这里**故意不写 else**：n < 1 在 RunOnce 入口就被挡掉（「段数不能为 0」），
            //   所以 n >= 1 ⇒ 循环至少跑一轮 ⇒ worst 必非 null。写个 else 就是一段
            //   永远跑不到、也永远没被验过的代码，而「死代码里的错答案」正是本项目的病灶之一。
            //   万一将来这个前提被改掉：兜底的是 LineResult.RequiredFor（按工况的必备名单） —— CheckRamp 为真时
            //   ① 在必备名单里，缺了 AllOk 直接为 false 并在 Failed 里报「判据缺席」。
            // ★ R20（用户 2026-09-08）：判据 ① 改闭式（见下面「① 升温」那条），这条集总升温用时**降为参考量**——
            //   它仍是有用的对照（含法兰质量与自热），但硬判据只有一条、不许两处来源。
            // 2026-09-15 Opus 5（审查意见 minor：原先超界标记靠「表上最后一条恰好是用时」，中间插一条判据就静默跳过）：留住这条的引用直接改。
            ConstraintOut? ckHours = null;
            if (worst is not null)
                checks.Add(ckHours = new ConstraintOut
                {
                    Name = LineResult.Key.RampHours, Unit = "h", Kind = CheckKind.Reference,
                    Actual = worst.Reached ? worst.HoursToTarget : double.NaN,
                    Limit = c.RampHours, Ok = worst.Reached && worst.HoursToTarget <= c.RampHours,
                    Where = where,
                    Note = (worst.Reached
                        ? $"{c.RampFromC:0}→{c.RampTargetC:0} °C，J={worst.JAPerMm2:0.00}" +
                          (worst.StabilityLimited ? "（电流被热稳定极限压低，不是故障）" : "")
                        : worst.Note)
                        + (worst.Reached && worst.HoursToTarget <= c.RampHours ? "" : NextAction.RampSlow)
                        // ★ 把这条判据的**前提**说出来（2026-08-24 补）。
                        //   本条走 RampSolver：集总空管模型，功率随需给足、只受 J_allow 约束，
                        //   **不含二次侧闭环方式**。现场是「温控」（用户 2026-08-11 确认），
                        //   冷态所需功率极小、电流只有几十安 —— 这个前提下不建电源模型是站得住的。
                        //   但换成恒压/恒流/恒功率就是另一族工况：恒压冷启电流可达设计值的 4.4 倍
                        //   （冷态 ρe 只有热态的 1/4.4），那一族由 RampTwoNode 建模，
                        //   只在 `--cli --ramp2` 里跑得到，**本表不覆盖**。
                        //   前提不写出来，读表的人会以为 ① 过了就等于升温这一段全过。
                        + "　⚠ 前提：按**温控**（功率随需给足、只受 J 上限）算，不含二次侧闭环方式。"
                        + "改恒压/恒流/恒功率是另一族工况（恒压冷启电流约 4.4 倍），用 `--cli --ramp2` 单独扫。"
                        // R48 审查第 4 条（2026-09-14，Opus 5）：段电流与法兰焦耳热取本次稳态 ⇒ 空管时标明随工况变
                        + StateDependentTag(c)
                });
            // R48（2026-09-15，Opus 5）：散热表超界 ⇒ 判不了（原值只供诊断）
            if (rampOverTable.Count > 0)
            {
                // 有段超界却没有这条可标 ⇒ 程序错（worst 必非 null，见上面那段说明），当场炸，不静默跳过
                var ckR = ckHours ?? throw new InvalidOperationException("集总升温散热表超界，却没有「升温到位用时」这条可标判不了 —— 程序错");
                // 界面上的叫法：参考温度在整线里就是本段控温点
                static string Probe(RampTableProbe w) => w switch
                {
                    RampTableProbe.Start => "升温起点", RampTableProbe.Peak => "升温达到的最高温",
                    RampTableProbe.Reference => "本段控温点", _ => "升温目标",
                };
                string who = string.Join("；", rampOverTable.Select(o =>
                    $"{o.Seg}：{string.Join("、", o.R.LossTableOut.Select(x => $"{Probe(x.Probe)} {x.TempC:0.#} °C"))} 不在 {o.R.LossTableLoC:0.#}～{o.R.LossTableHiC:0.#} °C 内"));
                ckR.Undetermined = true; ckR.Ok = false;
                ckR.Note = "★ **无法判定**：升温积分要查的管表面散热表没覆盖到这些温度（" + who + "），表外那部分散热只能按表端点的值算，用时不是这个设计的真值。"
                         + "原值仅供诊断，不得引用。请核对环境温度、升温目标与各段控温点（控温点与升温目标都要高于环境温度）。"
                         + "　（原注：" + ckR.Note + "）";
            }
        }

        // ── ② 硬安全线：法兰温度 ≤ 管温（用户原话）。**按温度直接判，不用 Φ 代理**。
        //
        //    ⚠ §4.2k 写的「等价于 Φ ≤ 1」只在**舌片末端绝热**时成立。
        //      Φ = 自身发热 / 自身**表面**散热，不含铜排夹带走的导热。
        //      夹冷一开（BusbarClampTempC ≥ 0），法兰变冷 ⇒ 表面散热变小 ⇒ Φ **反而升高**，
        //      而它其实更安全了。实测到的反例：夹持 80 °C 时 HC2|HC3 片 Φ = 1.313，
        //      但管孔净流入 +47 W（仍在从管子抽热，方向安全）。
        //      故硬安全线判 T_max(法兰) − T_root(管)，Φ 降为佐证。
        var hottest = flanges.OrderByDescending(f => f.TMaxC - f.TRootC).First();
        checks.Add(new ConstraintOut
        {
            // ★ 用户 2026-08-15 拍板：判据 ② 取「**B + C 并列**」——
            //   B = ②′ 管孔净流入须为正（热流方向本身，直接量）
            //   C = ②″ 圆盘区不得高于管温（贴着管子那一圈，仍用温度）
            //   本条（**整片**逐点，含舌片）**降为参考量**：
            //   · 舌片中段离管子 50 mm，中间隔着通电+包保温+远端被 450 °C 铜排拽住的一长条铂，
            //     它比管热不代表有热流进管 —— §4.2m 早写过「拿它跟管根比是在比两个不相干的位置」。
            //   · 且它与 C2 **数学上互斥**：管孔一圈净流量≈0 ⇒ ∮q dθ=0 ⇒ q 必然有正有负
            //     ⇒ 必然存在比管热的点。要整圈都比管冷需冷点深 32 K，而 C2 只许 10 K。
            //   ⚠ 降级**不等于**放松对舌片的要求：舌片仍由**熔点**与**局部热失稳 J≤J_stab** 管着。
            Name = "· ② 法兰最高温 − 管温（整片，含舌片）", Unit = "K", Kind = CheckKind.Reference,
            Actual = hottest.TMaxC - hottest.TRootC, Limit = 0, Ok = true,
            Where = hottest.Name,
            Note = $"法兰 {hottest.TMaxC:0.0} °C vs 管根 {hottest.TRootC:0.0} °C；" +
                   $"Φ={hottest.Phi:0.000}（发热 {hottest.QGenW:0} / 表面散热 {hottest.QLossW:0} W）；" +
                   $"管孔净流入 {hottest.QFromTubeW:+0;-0} W" +
                   (hottest.TMaxC > hottest.TRootC
                        ? "。**热量向管子倒灌 —— 这是烧断的过程，不是数值不好看**" : "") +
                   (hottest.TMaxC > 1768 ? $" ★已超铂熔点 1768 °C" : "")
        });

        // ── ②″ 同一条安全线，但**只看贴着管子那一段**（圆盘区）。
        //
        // 为什么要单列而不是把 ② 改掉：② 取的是**整片**最高温。舌片包保温之后
        // （§4.3e 的新自由度）峰值就落到舌片上 —— 离管子几十毫米、中间还隔着圆盘，
        // 拿它跟管根比是在比两个不相干的位置。真正决定「热往不往管里灌」的是圆盘区。
        // **但 ② 不能因此删掉**：舌片跑多热本身仍要盯（熔点、局部失稳），
        // 而且一旦哪天圆盘重新成为峰值所在，② 与 ②″ 会自动重合。两条都报，谁不过都要交代。
        // ⚠⚠ **判据绝不允许消失**（2026-08-24 补 else；③ 2026-08-15 补过、⑤ 2026-08-17 补过，
        //    ②″ 是同一个形态的第三次，一直漏到今天）。
        //    TDiscMaxC 在「一格都没被判进圆盘区」时是 NaN（ShellThermal:
        //    `IsNegativeInfinity(tDMax) ? NaN`）。所有片都这样 ⇒ hottestDisc 为 null
        //    ⇒ 原来整条判据不出现 ⇒ AllOk 少判一条硬安全线还报「全过」。
        //    现在补 else 报「无法判定」，并且 LineResult.RequiredFor（按工况的必备名单）会**独立地**再兜一次底 ——
        //    两道是有意重复的：else 给得出原因，名单保证下一条新判据漏写 else 时也不会溜过去。
        // ★★★★★ R48（2026-09-14，Opus 5）：**任何一片判不了 ⇒ 整条判不了**。
        //
        //   上面那段注释只堵了「**所有**片都 NaN」那一种。实际的漏洞是「**有几片** NaN」：
        //   `Where(!IsNaN)` 把它们**静默滤掉**，然后在剩下的片里挑最热的报出来 ——
        //   于是一条硬安全线在那几片上**从来没判过**，而输出上完全看不出来。
        //   这与同一文件里局部热稳定那条 2026-08-24 修过的病**逐字同形**
        //   （当时：`Where(!NaN).OrderBy(margin).First()` 取「剩下几片里最差的」，
        //     而判不了的恰恰是发散的那一片，于是发散算例报出一个由健康片算来的漂亮数）。
        //   照它的修法办：先看有没有片判不了，有就整条判不了，并点名是哪几片。
        //
        //   ⚠ 实测（09-14，管壁 0.8）：两个内置设计的四片圆盘区都不空（NaN 0/4），
        //     所以本次修改**不改变现役档的任何判定**。它堵的是换个几何才会发作的那个洞
        //     （舌半宽 = 盘半径且用双舌片时，分区规则 |x| > |切点| 会让圆盘区成为空集）。
        // ★★★★★ R48 B（2026-09-14 Opus 5）：**热侧硬安全线换成「最热铂高出热偶读数」**，上面这段讲的「圆盘区最高温 − 管温」降为旧判法参考量（紧跟在后）。
        //
        //   为什么换基准（用户 2026-09-14 定）：控温热偶在段中点、误差 5 ℃ —— 现场知道的是**读数**，不是模型算的管根温度。
        //   旧判法拿「贴着的管根温度」作基准，而那个管根温度本身会被法兰抽热、段间导热挪动：
        //   法兰把管根拉冷 2 K，旧判法的基准就跟着降 2 K，圆盘峰「看上去」反而更高 —— 靶跟着箭跑。
        //   新判法：H_j = max(圆盘峰, 舌片区峰, 该接头管根较热端) − 热偶读数基准（ThermocoupleBasis，唯一来源）。
        //   · 舌片区峰也算进来：舌片是同一块铂，比读数高出误差带同样说不清（旧判法把舌片交给熔点与局部失稳，现在那两项是参考）。
        //   · 管根较热端也算进来：共用接头处管子本身可能被一侧控温点顶得比对数平均高。
        //   ⚠ 判不了的片照旧：**任何一片判不了 ⇒ 整条判不了**，点名是哪几片、缺的是哪个量。
        // ★★★★★ 决 103（业主 2026-09-24）：带玻璃稳态卡交付的热侧、冷侧换成「法兰最热处高出管接触处温度 ≤ 10 K」「管接触处流入法兰的净热流 ≤ 0」。
        //   构造在公开函数 ContactChecks（快门拿合成的片直接验）；改回口径不出这两行（判据表与改前逐位相同）。
        if (rs == CriteriaRuleSet.决103)
        {
            var cc = ContactChecks(c, segs, flanges);
            checks.Add(cc.Hot);
            checks.Add(cc.Cold);
        }
        var tcChecks = ThermocoupleChecks(c, segs, flanges);   // R48 B（2026-09-14 Opus 5）：热侧、冷侧两条的构造提成公开函数，快门用合成数据直接验
        checks.Add(tcChecks.Hot);

        // ── 旧判法参考量：圆盘区最高温 − 贴着的管根温度（2026-09-14 前是硬安全线，现在只作对照，不卡交付）
        //   R48 B（2026-09-14 Opus 5）：计算照旧（同一个最热片、同一段病灶位置），只改 Kind 与名字；
        //   原附注里的操作建议（NextAction.DiscHot）是按旧判法测的斜率写的、且带代号与命令行开关名，不再附在参考行上；
        //   2026-09-14 Opus 5（复审）：那个常量从此没人挂 ⇒ 连同 NextAction.DipHigh 一起删掉（死字串还钉着门，等于门在空转）。
        var blindDisc = flanges.Where(f => double.IsNaN(f.TDiscMaxC)).ToArray();
        var hottestDisc = blindDisc.Length > 0 ? null
                        : flanges.OrderByDescending(f => f.TDiscMaxC - f.TRootC).FirstOrDefault();
        if (hottestDisc is not null)
            checks.Add(new ConstraintOut
            {
                Name = LineResult.Key.DiscTemp, Unit = "K", Kind = CheckKind.Reference, Ok = true,
                Actual = hottestDisc.TDiscMaxC - hottestDisc.TRootC, Limit = c.DiscOverTempMaxK,
                Where = hottestDisc.Name,
                // ★ 判据必须自带**病灶位置**：只报差值时，「盘峰贴在管孔上」与
                //   「轮毂上被自身发热顶起一个尖峰」给出同一个数，却要用相反的旋钮去治。
                //   r≈管外径 且 J≈0 ⇒ 病在管侧；r 更大且 J 不为零 ⇒ 病在法兰侧。
                Note = "旧判法参考量（不卡交付；卡交付的是「最热铂高出热偶读数」）：基准是模型算的贴着的管根温度。" +
                       $"圆盘区 {hottestDisc.TDiscMaxC:0.0} vs 管根 {hottestDisc.TRootC:0.0} °C；" +
                       $"峰位 r={hottestDisc.DiscMaxRMm:0.0} mm（x={hottestDisc.DiscMaxXMm:+0.0;−0.0}, " +
                       $"z={hottestDisc.DiscMaxZMm:+0.0;−0.0}）J={hottestDisc.DiscMaxJAPerMm2:0.00} " +
                       $"t={hottestDisc.DiscMaxThickMm:0.00} mm；" +
                       $"分区口径：{hottestDisc.DiscZoneRule}；{hottestDisc.InsulRule}；" +
                       $"舌片区峰值 {hottestDisc.TTabMaxC:0.0} °C" +
                       (double.IsNaN(hottestDisc.TabMaxRMm) ? "" : $"（峰位 r={hottestDisc.TabMaxRMm:0.0} mm）") +
                       $"；管孔净流入 {hottestDisc.QFromTubeW:+0;-0} W"
            });
        else
            checks.Add(new ConstraintOut
            {
                Name = LineResult.Key.DiscTemp, Unit = "K", Kind = CheckKind.Reference, Ok = true,
                Actual = double.NaN, Limit = c.DiscOverTempMaxK, Undetermined = true,
                Where = blindDisc.Length > 0 ? string.Join("、", blindDisc.Select(f => f.Name)) : "—",
                Note = "旧判法参考量（不卡交付）：" + (blindDisc.Length > 0
                        ? $"{blindDisc.Length} 片（{string.Join("、", blindDisc.Select(f => f.Name))}）的网格里没有落在圆盘区的单元，算不出。"
                        : "没有法兰片，算不出。")
            });

        // ── ②′ 同一条安全线的管侧视角：**热不能往管子里灌**
        //
        // ⚠ 口径已改（2026-08-15）：原来判「管根温差（控温点 − 管根）须为正」，
        //   那是拿温差当「热流方向」的代理量 —— 而接上段间导热后，共用法兰处的管温
        //   由**两侧控温点**决定（实测基线本身就比本段控温点高 63 K，HC2 端被 HC1 拉起来），
        //   代理量整个被控温点梯度污染，判出 −63 K「法兰在加热管子」，其实法兰只加了几 K。
        //
        // 而「热往哪边流」有**直接量**：管孔净流入 QFromTubeW（>0 = 从管子抽热 = 安全）。
        // 直接量就在手里，没有任何理由再用代理量。
        var worstFlux = flanges.OrderBy(f => f.QFromTubeW).First();
        // R47 F（2026-09-13）：管孔定温环的自检写进附注，**不改判定**。
        //   TagHole 的口径是 |r − 孔半径| < 3 mm：环宽小于 3 mm 的盘（如盘 R28／孔 25.8）整段盘外缘会被钉成管孔温度。
        // ★ 2026-09-23（F6c）：生产网格（FlangeMesher 弧面路径，解析与图纸两条路都是）孔面只认孔圆上的弧面（MeshRules.HoleTagArcOnly），
        //   弧面中点半径恰 = 孔半径 ⇒ HoleTagMaxROverMm ≈ 0（浮点尾巴 ≤ 1e-12），这条附注在生产路径上恒不触发。
        //   留着：它量的是「孔面有没有越出孔圆」这件事本身，阶梯孔边的老网格（QuadMesher、HoleArcFaces 关的对照）上照样会说话。
        string holeTagNote = "";
        {
            var over = flanges.Where(f => !double.IsNaN(f.HoleTagOverMm)).OrderByDescending(f => f.HoleTagOverMm).FirstOrDefault();
            if (over is not null && over.HoleTagOverMm > 1e-6)
                holeTagNote = $"　管孔定温环吃到孔边以外 {over.HoleTagOverMm:0.0} mm（{over.Name}"
                            + (over.HoleTagBeyondWeldMm > 1e-6 ? $"，焊脚以外被钉住的边界长 {over.HoleTagBeyondWeldMm:0.0} mm" : "")
                            + "；管孔边界口径 |r−孔半径|<3 mm，本处只量不判）。";
        }
        string insulSrcNote = string.IsNullOrEmpty(worstFlux.InsulBoundaryNote) ? "" : "　" + worstFlux.InsulBoundaryNote + "。";
        checks.Add(new ConstraintOut
        {
            Name = "②′管孔净流入 须为正", Unit = "W", Kind = CheckKind.HardSafety,
            Actual = worstFlux.QFromTubeW, Limit = 0, LessIsBetter = false,
            Ok = worstFlux.QFromTubeW > 0, Where = worstFlux.Name,
            Note = (worstFlux.QFromTubeW <= 0
                 ? "★ 热正在往管子里灌 —— 这是烧断的过程。" + NextAction.NetFluxLow
                 : "法兰在从管子抽热，方向安全。" + NextAction.DrawWindow)
                 + holeTagNote + insulSrcNote
        });

        // ── ③ **法兰造成的增量温降** ≤ 上限
        //   ⚠ 口径已改（2026-08-15）：原来判「偏离本段控温点」，但接上段间导热后，
        //     共用法兰处的管温由**两侧控温点**决定（实测 HC1|HC2 接头停在 1116 °C
        //     = 1150 与 1080 的中间，偏离本段控温点 34 K），**与法兰设计无关**。
        //     让法兰去背控温点梯度的锅，等于给优化器一个它够不着的靶子。
        //   现在判的是「有法兰 vs 无法兰」的同位置之差 —— 那才是法兰的责任。
        // ⚠⚠ 判据只能「过 / 不过 / **无法判定**」，**绝不允许消失**。
        //   原来写成 `if (dips.Length > 0) checks.Add(...)` —— 基线算失败时这条
        //   整个不出现，于是 AllOk 少判一条还报「全过」。实测阶梯就这么虚报过一次：
        //   增量降 max 是 +32（上限 10）却判「✓ 全过」。
        //   **判据消失比判据不过危险得多**：不过会被看见，消失不会。
        // ★★★★★ R48 续（2026-09-14，Opus 5）：**任何一段判不了 ⇒ 整条判不了。**
        //
        //   原来是 `segs.Where(!IsNaN)`：算不出增量温降的段被**静默丢掉**，然后在剩下的段里报最深的那个。
        //   紧挨着的注释只堵了「**所有**段都 NaN」（那时 dips.Length == 0 才走 else）。
        //   这与同一文件里 ②″（blindDisc）和局部热稳定（anyUnknownPlate，2026-08-24 修）
        //   **逐字同形** —— 同一个形态的第三次。规矩早就定了：任何一片/段判不了，整条就判不了。
        //   ⚠ NaN 的来路：ApplyBaseline 逐段做 `baseline − 实际`，而基线是全有全无；
        //     所以部分 NaN 只能来自某一段主解的管根温度是 NaN —— 正是 2026-08-24 那个「发散的那一片」，
        //     只是层级从片换成段。结构上没有任何东西挡着。
        // ★★★★★ R48 B（2026-09-14 Opus 5）：**冷侧硬安全线「管根低于热偶读数」取代上面这段讲的「法兰增量温降 ≤ 10（目标）」**。
        //
        //   用户 2026-09-14：「10 ℃ 是上下各 5 ℃」—— 旧判法的 10 本来就是热偶上下两侧误差之和。
        //   新判法：C_j = 热偶读数基准 − 该接头管根较冷端 ≤ 热偶误差（ThermocoupleBasis，唯一来源），升为**硬安全线**。
        //   ⚠ 与旧判法的差别不止限值：旧判法「无法兰基线 − 实际」把控温点梯度剔掉了（那是 2026-08-15 的理由），
        //     新判法不剔 —— 基准是**设定值算出的**对数平均，与法兰设计无关、不会被设计变量挪动，所以同样不是「够不着的靶」；
        //     而模型算的无法兰交界管温照旧并列在说明里，差超过 1 ℃ 时写明（ThermocoupleBasis.ModelJointNote）。
        //   ⚠ 判不了照旧：**任何一片判不了 ⇒ 整条判不了**。
        checks.Add(tcChecks.Cold);   // R48 B：冷侧（热偶读数基准）—— 构造在 ThermocoupleChecks（公开，快门直接验）

        // ── 旧判法参考量：法兰增量温降 = 无法兰基线 − 实际（2026-09-14 前是目标，现在只作对照，不卡交付）
        //   R48 B（2026-09-14 Opus 5）：计算照旧（两端取较差、任何一段判不了整条判不了），只改 Kind 与名字；
        //   原附注里的操作建议（NextAction.DipHigh）按旧判法写，不再附在参考行上（常量已删，见上面圆盘区那条的说明）。
        var blindDip = segs.Where(s => double.IsNaN(s.FlangeDipK)).ToArray();
        var dips = blindDip.Length > 0 ? Array.Empty<SegmentOut>()
                 : segs.Where(s => !double.IsNaN(s.FlangeDipK)).ToArray();
        if (dips.Length > 0)
        {
            var deepest = dips.OrderByDescending(s => s.FlangeDipK).First();
            checks.Add(new ConstraintOut
            {
                Name = LineResult.Key.FlangeDip, Unit = "K", Kind = CheckKind.Reference, Ok = true,
                Actual = deepest.FlangeDipK, Limit = c.RootDeltaMaxK, Where = deepest.Name,
                Note = "旧判法参考量（不卡交付；卡交付的是「管根低于热偶读数」）：" +
                       $"= 无法兰基线 − 实际（{deepest.BaseTRootAC:0.0}/{deepest.BaseTRootBC:0.0} " +
                       $"vs {deepest.TRootAC:0.0}/{deepest.TRootBC:0.0} °C）；控温点梯度不算在内。" + NextAction.DrawWindow
            });
        }
        else
        {
            checks.Add(new ConstraintOut
            {
                Name = LineResult.Key.FlangeDip, Unit = "K", Kind = CheckKind.Reference, Ok = true,
                Actual = double.NaN, Limit = c.RootDeltaMaxK, Undetermined = true,
                Where = blindDip.Length > 0 ? string.Join("、", blindDip.Select(x => x.Name)) : "—",
                Note = "旧判法参考量（不卡交付）：" + (blindDip.Length > 0 && blindDip.Length < segs.Length
                        ? $"{blindDip.Length} 段（{string.Join("、", blindDip.Select(x => x.Name))}）的增量温降算不出来。"
                        : "无法兰基线没算出来（LineCase.BaselineRootC 为空且基线子解失败）。")
            });
        }

        // 旧口径降为参考量：它反映的是控温点梯度，读的时候别当成法兰的问题
        var deepestAbs = segs.OrderByDescending(s => s.RootDeltaK).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 偏离本段控温点", Unit = "K", Kind = CheckKind.Reference, Ok = true,
            Actual = deepestAbs.RootDeltaK, Limit = c.RootDeltaMaxK, Where = deepestAbs.Name,
            Note = "参考：共用法兰处主要由**两侧控温点之差**决定，不是法兰造成的"
        });

        // ── ④ 强度利用率（管）。法兰不承重 —— 铂管由氧化铝管托底（§4.2d，用户确认），
        //     故此处只校核管，不校核法兰舌片。
        //
        // ★★ 2026-09-18，Opus 5：整段搬进 TubeStrength（逐段算、逐段印、上界规则、空管无玻璃载荷都在那里）。
        //    原来这里 20 行，藏了三个「看起来正常的错数」：
        //      (a) 区间外的段 continue 跳过 ⇒ 印出来的是**幸存段**的最大值，却标着「整线」；
        //      (b) 空管那张表的载荷仍带玻璃 ⇒ 混血数（许用按 1150 °C、载荷按带玻璃）；
        //      (c) 参考量写死 Ok = true ⇒ 输出里出现「过，裕度 −0.401」。
        //    三件各有门（R48LTubeStrengthGateTests）。**判据本体只在这里调一次**，谁再就地判一遍，那道门当场红。
        checks.Add(TubeStrength.Judge(c, segParams,
                                      segs.Select(x => x.Name).ToArray(),
                                      segs.Select(x => x.SetpointC).ToArray()));

        // ── 参考量：Φ（②的佐证，夹冷时会失真，见 ② 的说明）
        var worstPhi = flanges.OrderByDescending(f => double.IsNaN(f.Phi) ? -1 : f.Phi).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰自给率 Φ_max", Unit = "—", Kind = CheckKind.Reference, Ok = true,
            Actual = worstPhi.Phi, Limit = 1.0, Where = worstPhi.Name,
            Note = "参考：仅在舌片末端绝热时才等价于 ②；夹冷时 Φ 偏高但更安全"
        });

        // ★★★★★ **法兰 J：从「参考量」改成硬判据里的「判不了」**（2026-09-07 C1，用户拍板）。
        //
        //   用户 2026-08-25：「给予铂金实际的电流密度最大为 10」。而现况是：
        //     管侧   TubeJ   在 Required、HardSafety           ✅ 有门
        //     法兰侧 FlangeJ Kind = Reference                  ❌ 没门
        //   而**挖孔恰恰挖在舌片上**，②″ 只判圆盘峰值（TDiscMaxC），
        //   舌片峰值（TTabMaxC）与 FlangeTopTemp 也都是参考量 ⇒ 舌片那一侧两头都没门。
        //
        //   为什么不直接判「≤ 10」：这个数**现在不可信**。
        //   实测（deliverable/孔的网格分辨率.txt）孔那一带的网格从未细化过，
        //   同一个孔 7.920 → 10.068 且**仍在上升、没有收敛平台**；
        //   README 自己写着「现值是**下界**，+3.5 % 网格敏感」。
        //   拿一个未收敛的数去判，红绿都是假的。
        //
        //   ⇒ 走第三条路：**判不了（Undetermined）**。按仓库铁律「判不了不算过」，
        //     它会挡住交付，直到孔那一带的网格修好、这个数收敛为止。
        //   ⚠ 这是有代价的、用户明知并拍板的：**在网格修好之前，交付被挡住。**
        //     另两条路都被否掉了 —— 提成「≤10」会拿假数判；留作参考量则等于判据消失，
        //     而「判据绝不允许消失」是本项目的铁律。
        // ★★★★★ 2026-09-08 下午改口径（用户设计因果链第 ④ 步：「核算电流通过**截面**的电流密度，全体必须小于 11」）：
        //   判据看的是**截面** J = 设计电流 ÷ 必经截面积（闭式、与网格无关，当场判得了）；
        //   场的逐点峰值（凹角处随网格涨、无收敛平台）是局部发热问题，归温度场与熔化门管，
        //   在这里降为**参考量**（诊断，给第 ③ 步「电流密度低处定孔」用）。
        //   ⚠ 这是推翻前任的口径（他把逐点峰值当判据去追网格 ⇒ 永远判不了，挡住每一档交付），按用户第 8 条说出来。
        var worstJf = flanges.OrderByDescending(f => f.JMaxAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰 J_max", Unit = "A/mm²", Kind = CheckKind.Reference,
            Ok = true, Undetermined = false,
            Actual = worstJf.JMaxAPerMm2, Limit = c.Base.JAllowAPerMm2, Where = worstJf.Name,
            Note = "场的**逐点峰值**（凹角处随网格涨，是局部发热问题，由温度场与熔化门管）—— 诊断量，"
                 + "给「电流密度低处定孔」用；**判据看「法兰截面 J」**（设计电流 ÷ 必经截面积，闭式）。"
        });
        {
            // ── 法兰截面 J（用户 2026-09-08 设计因果链第 ①④ 步）
            //   设计电流：温控 20 °C/h 空管升温全程峰值（RampTwoNode），每段各算、共用片矢量合成；
            //   截面：舌片各处（含开孔弦）、舌盘交界弦、孔缘环与各级环（含槽带）—— SectionSizing.Cuts。
            try
            {
                var platesJ = c.PlatesForJudge();   // 2026-09-17 Opus 5：就地那句提成 LineCase.PlatesForJudge，升温逐点重算调同一份
                if (platesJ is null || platesJ.Length == 0) throw new InvalidOperationException("没有法兰几何（解析 FlangePlate 或 .3dm 的判据几何都没有）");
                var dcr = DesignCurrent.Compute(platesJ, c.WallMm, c.Base, c.RampFromC, c.RampTargetC, c.RampRateKPerH,
                              segs.Length, c.SetpointC,
                              jj => jj < flanges.Length && flanges[jj] is { } f0 && f0.QGenW > 0 && f0.CurrentA > 0
                                    ? (f0.QGenW / (f0.CurrentA * f0.CurrentA), f0.TRootC) : null,
                              c.DiscInsulEffectiveAt);   // R48（2026-09-14，Opus 5）：两节点对照印在本条 Note 里，原读整线圆盘保温
                // ── ① 升温（R20，用户 2026-09-08 纠正：全体截面 J<11 就不会熔，不判峰值；
                //    ① = 升温所需电流没被管 J 许用上限截住 ⇒ 按 20 °C/h 升得到目标）。闭式、与网格无关、每轮都在。
                bool rampUndet = double.IsNaN(dcr.RampTubeJPeakAPerMm2);
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.Ramp, Unit = "A/mm²", Kind = CheckKind.HardSafety,
                    Actual = dcr.RampTubeJPeakAPerMm2, Limit = dcr.TubeJAllowAPerMm2, LessIsBetter = true,
                    Ok = !rampUndet && !dcr.Clipped, Undetermined = rampUndet, Where = "全线",
                    Note = $"{dcr.RampRateKPerH:0} °C/h 空管升温 {dcr.FromC:0}→{dcr.TargetC:0} °C 所需电流（管子准静态 I²R = 散热 + C·Ṫ）"
                         + $"全程峰值 {(dcr.SegPeakA.Length > 0 ? dcr.SegPeakA.Max() : double.NaN):0} A ⇒ 管 J 峰值 {dcr.RampTubeJPeakAPerMm2:0.00} A/mm²，"
                         + $"许用 {dcr.TubeJAllowAPerMm2:0}。没被截住 = 升得到；闭式、与网格无关。"
                         + (dcr.Clipped ? NextAction.RampClipped : "")
                });
                double jChk = SectionSizing.JCheckOf(c.JDesignAPerMm2);   // 计算极限值 = 设定 J + 1（用户 2026-09-09）
                double worstJ = double.NegativeInfinity; string where = ""; bool undet = false;
                for (int jj = 0; jj < flanges.Length; jj++)
                {
                    var plj = platesJ[Math.Min(jj, platesJ.Length - 1)];
                    double iA = jj < dcr.PlateA.Length ? dcr.PlateA[jj] : double.NaN;
                    var cut = SectionSizing.Worst(plj, iA, c.Base.BusbarClampLengthMm);
                    flanges[jj].DesignCurrentA = iA;
                    flanges[jj].SectionJAPerMm2 = cut.JAPerMm2;
                    flanges[jj].SectionJWhere = cut.Where;
                    if (double.IsNaN(cut.JAPerMm2)) undet = true;
                    else if (cut.JAPerMm2 > worstJ) { worstJ = cut.JAPerMm2; where = $"{flanges[jj].Name} {cut.Where}"; }
                }
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.SectionJ, Unit = "A/mm²", Kind = CheckKind.HardSafety,
                    Actual = undet ? double.NaN : worstJ, Limit = jChk, LessIsBetter = true,
                    Ok = !undet && worstJ < jChk, Undetermined = undet, Where = where,
                    Note = dcr.Describe()
                         + "　截面 J = 设计电流 ÷ 必经截面积（舌片各处含开孔／舌盘交界弦／孔缘环与各级环含槽带，圆盘按整圈），"
                         + $"闭式、与网格无关；按设定 J={c.JDesignAPerMm2:0.#} 定尺寸（工程师在 ① 输入设定，预设 10），终验全体 < J+1 = {jChk:0.#}。"
                         + (!undet && worstJ < jChk ? "" : NextAction.SectionJHigh)
                         // R48 审查第 4 条（2026-09-14，Opus 5）：Describe 里的两节点对照取本次各片稳态参考电阻 ⇒ 空管时标明；实际值不随工况变
                         + (c.EmptyTube ? "　⚠ 说明里的两节点对照电流取自本次空管到温稳态场，随工况变；本条实际值（截面 J）与工况无关。" : "")
                });
            }
            catch (Exception ex)
            {
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.SectionJ, Unit = "A/mm²", Kind = CheckKind.HardSafety,
                    Actual = double.NaN, Limit = SectionSizing.JCheckOf(c.JDesignAPerMm2), LessIsBetter = true,
                    Ok = false, Undetermined = true, Where = "—",
                    Note = "★ **算不出来**（判不了不算过）：" + ex.Message
                });
            }
        }
        var worstJt = segs.OrderByDescending(s => s.TubeJAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            // ★ 2026-08-15：**从「参考」升为硬判据** —— 限值有来源了
            //   （用户现场：一般上限 15；管壁 0.6 时 12 是极限 ⇒ 全档取 12）。
            //   原来它是参考量，只因为限值 10 是个「物理依据待定」的占位值。
            // ★ 决 103（业主 2026-09-24「J < 11」）：限值 = 卡交付的管 J 限值（唯一读法 DesignInputs.TubeJLimitAPerMm2 = min(原许用 12, 使用上限 11)）；
            //   改回口径下它就是原许用值（与改前逐位相同，说明文字也照旧）。原 12 那一行在下面照印作对照。
            Name = "管 J", Unit = "A/mm²", Kind = CheckKind.HardSafety,
            Actual = worstJt.TubeJAPerMm2, Limit = c.Base.TubeJLimitAPerMm2, Where = worstJt.Name,
            Ok = worstJt.TubeJAPerMm2 <= c.Base.TubeJLimitAPerMm2,
            Note = (rs == CriteriaRuleSet.决103前
                    ? "限值来源：用户 2026-08-15 现场（一般 15；管壁 0.6 时 12 是极限）。"
                    : $"限值来源：2026-09-24 定「管的最大使用电流密度 J < 11」（参数表「管 J 使用上限」{c.Base.TubeJUseCapAPerMm2:0.###}）与原许用值（08-15 现场，{c.Base.TubeJAllowAPerMm2:0.###}）取小 = {c.Base.TubeJLimitAPerMm2:0.###}；原许用值那一行下面照印作对照。") +
                   (c.Base.LossScale == 1.0
                ? "⚠ 散热未标定，本值系统性偏高（§4.2l）⇒ 判定偏保守"
                : $"散热已按 LossScale={c.Base.LossScale:0.000} 标定") +
                   (worstJt.TubeJAPerMm2 > c.Base.TubeJLimitAPerMm2 ? NextAction.TubeJHigh : "")
        });
        if (rs == CriteriaRuleSet.决103)
            checks.Add(new ConstraintOut
            {
                // 决 103：原许用值（12）那一行留作对照（参考量）。变因：2026-09-24 定 J < 11，卡交付的换成上一行。
                Name = LineResult.Key.TubeJPre103, Unit = "A/mm²", Kind = CheckKind.Reference,
                Actual = worstJt.TubeJAPerMm2, Limit = c.Base.TubeJAllowAPerMm2, Where = worstJt.Name,
                Ok = worstJt.TubeJAPerMm2 <= c.Base.TubeJAllowAPerMm2,
                Note = "对照（不卡交付）：2026-09-24 之前卡交付的管 J 限值 = 参数表「管许用电流密度」（08-15 现场：一般 15；管壁 0.6 时 12 是极限）。"
                     + "变因：2026-09-24 定「管的最大使用电流密度 J < 11」，卡交付的换成上一行（两者取小）。"
            });

        // ── ⑤⑥ 几何闭式判据 —— 实现已搬到 Core/GeometryScreen.cs
        //
        // 为什么搬走：这两条**不需要解场**，给定几何就有答案。而界面的阶段门禁
        // 要在跑分钟级整线解**之前**就回答「这个几何造不造得出来」。
        // 更要紧的是，⑤ 此前在 LineDesignPage 里还有**第二份实现**（连限值 100.0
        // 都各存一份）—— 铁律三点名的形状。现在两边调同一个函数：
        // **界面上看到的 ⑤⑥，与这里跑出来的，是同一段代码算的。**
        // 解析模式用求解用的那组片；.3dm 模式用「分析几何变数」反推出来的等效片。
        // 两者都空时 Judge 自己会给出「无法判定」（绝不省略这两条）。
        checks.AddRange(GeometryScreen.Judge(
            c.PlatesForJudge(),   // 2026-09-17 Opus 5：同上，统一走 LineCase.PlatesForJudge
            c.Base.BusbarClampLengthMm, c.FreeTabMinMm));

        // ── 「接合区保温缠得出来」：现场缠绕圈数上限（用户 2026-09-17）。
        //   与 ⑤⑥ 同型 —— 闭式、纯输入、不吃场；上限与口径只在 Core/WrapLimits.cs 一处，这里只是**调**它。
        //   ⚠ 越界不外推：缠过 20 圈会渐成球形，那个形状本程序没有模型，硬算出来的是「看起来正常的错数」。
        //   ★ 决 104（业主 2026-09-25）：决103 口径下是硬安全线（上限 = 参数表圆盘保温上限，缺省 10 mm）；决103前 仍是 2026-09-18 的参考行。
        checks.Add(WrapLimits.Judge(c, c.RuleSet));

        // ── 数值自洽：法兰热平衡残差。**始终露出来**（2026-08-17 加）。
        //
        // 它一直被算出来（FlangeOut.EnergyResidualW），却从来没进过判据表 ——
        // 只有 `--selfcheck` / `--vary` 看得见。于是界面上永远不知道
        // 「这个解到底守不守恒」。
        //
        // 为什么值得单列：它是**唯一与任何一条判据都无关**的自洽性检查。
        // 别的判据回答「这个设计好不好」，它回答「**这组数能不能信**」——
        // 后者若不成立，前者全部作废，而且不会有任何一条判据变红。
        //
        // 标定（2026-08-17 `--vary` 23 次求解）：正常构型残差 **0.014–0.036 W**；
        // 而「舌长 30 mm（短于压接段 40）」这个退化几何是 **120.6 W**，差四个数量级。
        // ⇒ 先按参考量报，让它的量级先积累起来；限值有出处了再谈升为硬判据
        //   （管 J 当年就是这么从参考量升上去的）。
        if (flanges.Length > 0)
        {
            var worstRes = flanges.OrderByDescending(f => Math.Abs(f.EnergyResidualW)).First();
            double resW = Math.Abs(worstRes.EnergyResidualW);
            checks.Add(new ConstraintOut
            {
                Name = "· 法兰热平衡残差", Unit = "W", Kind = CheckKind.Reference, Ok = true,
                Actual = resW, Limit = 0, Where = worstRes.Name,
                Note = "= 发热 − 散热 − 净流出，理想为 0。**它与任何一条判据都无关**，" +
                       "回答的是「这组数能不能信」而不是「设计好不好」。" +
                       $"标定：正常构型 0.01–0.05 W。" +
                       (resW > 1.0
                        ? "　★★ **本次远高于正常量级** ⇒ 先别读上面任何一个数，" +
                          "多半是几何退化（例如舌长短于压接段、盘盖不住孔）或网格没解开。"
                        : "")
            });
        }

        // ── 热稳定两条：**判据一直存在，只是从来没进过这张表**（2026-08-23 补）
        //
        // 机理（用户 2026-08-13 亲述）：保温过头 → 温度↑ → 电阻↑ → 同电流下发热↑
        // → 温度更↑ → 烧断。这是**正反馈失控**，与净热流方向无关 ——
        // 一个系统可以处在平衡态却是不稳定平衡。FlangeStability 的类头写着它是为了补
        // 「管侧早有同类判据（RampSolver 的 I_stab），法兰侧此前完全没有」这个洞而建的，
        // 可它建好之后只接到 CLI 的 --flangestab / --localstab 上，
        // **界面上的判据表里一条都没有** ⇒ 换个几何就没人再算它。
        //
        // 先按**参考量**报（管 J 与能量残差当年都是这么进来的）：
        // 限值 1.0 是精确物理不是经验阈值，本可直接当硬判据；
        // 但它在**设计记录以外的几何**上的量级从没量过，
        // 拿一条没量过分布的判据去卡交付，风险在另一侧。
        // ⇒ 这一轮先让它在每个算例上露出来、把量级攒起来，再谈升为硬判据。
        {
            var plates = c.PlatesForJudge();   // 2026-09-17 Opus 5：同一句就地三元式原是四处各写一份，统一走 LineCase.PlatesForJudge
            if (flanges.Length > 0 && plates is { Length: > 0 })
            {
                // ★ R48（2026-09-14，Opus 5 复审补）：最不利片的选取、建网格、两个集总模型的输入与求解整块提成 FlangeLumped（公开，门与探针调同一份）。
                //   生产传 excludeClampCells: true —— 压接段整面接触后，压接格不在铂的热平衡里，面积／质量／导热长都要把它排除（见 FlangeLumped 的注释）。
                var lumped = FlangeLumped(c, flanges, excludeClampCells: true)!;   // 与本 if 同一个前提（有片、有板件）⇒ 非空
                var fw = flanges[lumped.Index];
                var st = lumped.Stab;
                // 决 103（2026-09-24）：逐片整片热稳定裕度带到片上（求解器逐片裕度读它；只多写片上字段，判据表不变）
                if (lumped.PerPlate is { } perPlate)
                    for (int jj = 0; jj < flanges.Length && jj < perPlate.Length; jj++)
                        flanges[jj].FlangeStabMargin = perPlate[jj] is { } pp && !pp.Stab.Undetermined && !double.IsNaN(flanges[jj].QGenW) ? pp.Stab.Margin : double.NaN;
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.FlangeStab, Unit = "×", Kind = CheckKind.Reference,
                    Actual = st.Undetermined ? double.NaN : st.Margin, Limit = 1.0,
                    LessIsBetter = false, Ok = st.Stable, Undetermined = st.Undetermined,
                    Where = fw.Name,
                    Note = FlangeStabNote(lumped)   // R48 G2 复审二（2026-09-15 Opus 5）：附注提成公开函数（门验判不了时写原因、出处读位不读文字）
                });

                // ════════════════════════════════════════════════════════
                //  ★★★★★ 现场升温工况（温控 20 K/h）—— 2026-08-25 补进判据表
                //
                //  用户 2026-08-25：「**实际的工况是电流密度是为升温而设计的**」，
                //  并确认 §4.2s 那一行「温控 20 K/h：起始 71 A、max(法兰−管) +215 K、
                //  出现在 42.6 h」**比较符合现状**。
                //
                //  ⚠ 而判据 ① 用的是 `RampSolver` —— 它把法兰并进管子当**同一个温度**，
                //    于是「法兰比管热」这个失效模式在那个模型里**结构性地不可能出现**
                //    （这句话就写在 RampTwoNode 的类注释里）。而 §4.2k 说的烧断正是这一条。
                //    ⇒ 现场那条升温**此前没有任何判据在读**，只有一个要人记得跑的 CLI 探针
                //      （`--ramp2`）。**造好了没接线**，而且落在决定设计的那个量上。
                //
                //  ⚠ **先按参考量进表，不升硬判据**：+215 K 是**现役基准**（设备在跑），
                //    拿它去判现役等于判掉自己。限值需要出处 —— 与「热稳定」当初一样，
                //    先把量露出来、攒跨几何的分布，有出处了再谈升硬判据。
                // 输入与求解在 FlangeLumped 里（原来这里的 try 包的就是那一段；异常信息原样带回 RampError）
                //
                // ★★ R48 G2 复审（2026-09-15 Opus 5；审查意见 blocker「修后的数仍作为正常参考量进判据表」）：物理把关人复核前这一行**判不了、不给数**。
                //   依据：两节点模型的法兰温度是整片体积平均，不代表片上最热处 —— 准静态壳解升温末段最热铂比管温高、整片平均仍比管冷
                //   （deliverable/R48_G2_复审_升温两节点逐项标定_B2_2026-09-15.txt 表一管温 1101.6 °C：最热 1122.8 °C、节点均温 953.6 °C）；工单标题「修完前标不可引用」。
                //   ⛔ 本段原句「看不到孔边比管热 —— 孔边已比管热」撤回（R48 G2 复审二 2026-09-15 Opus 5，审查意见 major）：壳解孔面是定温边界，孔边温度就是管温；
                //     最热铂在圆盘区以外（同一行 最热 1122.8 > 盘峰 1116.2 °C）；位置见 RampPendingNote 注释 ①（复审二探针表三：舌片区、舌保温底下、离管孔半径 5～11 mm）。
                //   原先：Actual = 峰值、Ok = true，附注对着 215 K 现役基准、写「本条用两节点模型补上」—— 看起来比现役安全得多，而那个数按构造就不对。
                //   Limit 仍写 215（与交接文档的判据表同一行对得上），附注写明不与它比。升温两节点照样解，数留在 FlangeLumped 的结果里给前后对照探针。
                //   ⚠ 2026-09-15 Opus 5（合并，复审后改）：上面引的 1122.8／953.6 °C 等出自 r48_G2 工作树（底板 460d3b，F 合入前：压接形心整格 + 3·hFine 细带、无空管管腔辐射），
                //     合并树的配方下没有重跑；只作那份配方下的出处。文件已原样拷入本树 deliverable（2026-09-15 Opus 5（I 路） 核：deliverable/R48_合并拷入证据清单_2026-09-15.txt），原句「文件不在合并树」作废。
                if (lumped.Ramp is not null)
                {
                    checks.Add(new ConstraintOut
                    {
                        Name = LineResult.Key.RampField, Unit = "K", Kind = CheckKind.Reference,
                        Actual = double.NaN, Limit = 215.0,
                        LessIsBetter = true, Ok = true, Undetermined = true, Where = fw.Name,
                        Withheld = true,   // R48 G2 复审二（2026-09-15 Opus 5）：显示层据此印「暂不给数」，不印「达不到／算不出」
                        Note = RampPendingNote(c, lumped)
                             // R48 审查第 4 条（2026-09-14，Opus 5）：标定取本次最热片的稳态场 ⇒ 空管时标明随工况变
                             + StateDependentTag(c)
                    });
                }
                else
                {
                    checks.Add(new ConstraintOut
                    {
                        Name = LineResult.Key.RampField, Unit = "K", Kind = CheckKind.Reference,
                        Actual = double.NaN, Limit = 215.0, LessIsBetter = true,
                        Ok = true, Undetermined = true, Where = "—",   // 没给数 ⇒ 位置栏留「—」；是哪一片算不出来写在附注里（逐片都写）
                        // R48 G2 复审二（2026-09-15 Opus 5）：这一行复核前本来就不给数 ⇒ 算不出来时同样标「暂不给数」，附注再说算不出来的原因；
                        //   「不参与 AllOk」是代码名，换成人话「不卡交付」（界面不许出现代号）。
                        Withheld = true,
                        // ★ 2026-09-17 Opus 5（J 路，J6 复核「必须修 2」）：这一支**才是生产实测走的那一支**（两份 J6 证据四片全「算不出」），
                        //   而「升温两节点逐片结果进附注」原来只接在上面那一支 ⇒ 这里只印选中片的错误、连是哪一片都没说。
                        //   现在：① 点名选中的是哪一片、以及它是**按整片热稳定裕度最小**选出来的（挑片口径 09-15 从「发热最大」改了，界面上得说）；
                        //        ② 逐片各印一条（同一份 RampPerPlateNote，与解出来那一支同一个函数）。
                        Note = $"★ **暂不给数，待复核**；本设计上还**算不出来**（{fw.Name}，按整片热稳定裕度最小挑出来的那一片）：" + lumped.RampError
                             // R48 G2 复审（2026-09-15 Opus 5）：算不出来时也说清模型现状（已补通道与舌保温、逐项标定、待复核）与取的是哪个稳态场
                             + "　（两节点模型已补经舌片流进铜排的通道与舌片保温、按本片稳态场逐项标定，待复核；参考量，不卡交付；但算不出来就该说，不能装作没有这一条）"
                             + RampPerPlateNote(lumped)
                             + StateDependentTag(c)
                    });
                }


                // ★★★★★ 管↔法兰热收支对账（2026-08-28 第一性原理通查补上）
                //
                //   QFromTubeW 是**一片**法兰经整圈管孔抽走的**总量**。
                //   而外层耦合把 targetLR[i] = (Flanges[i].Q, Flanges[i+1].Q) 挂到段 i 两端，
                //   于是**内部共用片**同时是「段 j−1 的右端」与「段 j 的左端」——
                //   它的全额被扣**两次**。管子失去 Q₀ + 2ΣQ内 + Q_n，法兰只收到 ΣQ。
                //
                //   ⇒ 这条差额此前**没有任何一处在对账**，所以谁也没发现。
                //     做成参考判据：不参与 AllOk（改判定会动所有历史结果），但**永远看得见**。
                //   ⚠ 它**不是**「误差」，是**模型口径**：先把它量出来，再决定改不改。
                if (Math.Abs(res.DrawAppliedW) > 1e-12)
                {
                    double gotW = flanges.Sum(f2 => f2.QFromTubeW);
                    double residW = res.DrawAppliedW - gotW;
                    double innerW = flanges.Length > 2
                        ? flanges.Skip(1).Take(flanges.Length - 2).Sum(f2 => f2.QFromTubeW) : 0.0;
                    checks.Add(new ConstraintOut
                    {
                        Name = LineResult.Key.HeatBalance, Unit = "W", Kind = CheckKind.Reference,
                        Actual = residW, Limit = 0.0, LessIsBetter = true, Ok = true, Where = "整线",
                        Note = $"段端共扣 {res.DrawAppliedW:0.00} W，各片实收 {gotW:0.00} W"
                             + $"　⇒ 差 {residW:+0.00;−0.00} W（内部共用片合计 {innerW:0.00} W）"
                             + "　守恒时应为 0；不为 0 表示内部片被两段各扣一次"
                    });
                }


                // 局部热稳定：取**全线最不稳定的那一格**（由场解逐格筛出，见
                // ShellThermalResult.LocalStabMargin）。
                //
                // ★ 第一版拿「圆盘最热那一格」当代表，**是错的**：实测两个现役档上
                //   盘温峰落在外缘，那里电流密度≈0 ⇒ 裕度算出 +∞ ⇒ 判据表上会写着
                //   「无限安全」而其实一格都没验。最热 ≠ 最不稳定。
                // ★★ **任何一片判不了 ⇒ 整条判不了**（2026-08-24 修）。
                //
                //   原来是 `Where(!NaN).OrderBy(margin).First()` —— 取「剩下几片里最差的」。
                //   可判不了的恰恰是**发散的那一片**（温度出了电阻率拟合区间），
                //   于是发散算例上会报出一个由**健康片**算来的漂亮数：
                //   实测 selfcheck B 段「板厚 ×0.5」四片最高 4361 °C，
                //   而这一条报 **1.9×**，看着比设计记录还安全。
                //   这是同一个偏乐观偏差在**片这一层**的重演（格那一层已修）。
                checks.Add(LocalStabCheck(c, flanges));   // 决 103（2026-09-24）：构造提成 LocalStabCheck —— 整线收尾换成全格值后同一个函数重建这一行（改回口径逐位同改前）
            }
        }

        // ── 现场验证点：玻璃温降。这是全模型唯一一个拿实测校准的量，必须始终露出来。
        // ★ R48（2026-09-14，Opus 5）：空管到温稳态没有玻璃 ⇒ 这条**不适用**。ConstraintOut 没有「不适用」语义，
        //   用「判不了」表达（参考量，不进 AllOk），原因写进 Note；不许报 0 或 NaN 冒充模型值。
        checks.Add(c.EmptyTube
            ? new ConstraintOut
            {
                Name = "· 玻璃温降 vs 实测", Unit = "K", Kind = CheckKind.Reference, Ok = true, Undetermined = true,
                Actual = double.NaN, Limit = res.GlassDropMeasuredK, Where = "整线",
                Note = "不适用：本次是空管到温稳态（管内无玻璃），没有玻璃温降可与实测比。实测值只对应带玻璃的稳态。"
            }
            : new ConstraintOut
            {
                Name = "· 玻璃温降 vs 实测", Unit = "K", Kind = CheckKind.Reference, Ok = true,
                Actual = res.GlassDropModelK, Limit = res.GlassDropMeasuredK, Where = "整线",
                Note = $"偏差 {res.GlassDropModelK - res.GlassDropMeasuredK:+0.0;-0.0} K"
            });

        // ★★★★★ K 路（2026-09-15，Opus 5）：分工况判据 —— 每条判据在本工况下是硬判据还是参考量，只从 LineResult.RequiredByState 取（唯一一处）。
        //   上面各条照常按带玻璃稳态的写法构造（计算、说明、判不了的处理两态一样），这里按工况把 Kind 盖上去；带玻璃稳态没有一条会变（逐位不变）。
        ApplyStateCriteria(c.EmptyTube, checks, rs);   // 决 103：按口径取分工况表（改回口径 = 改前那张表，逐位同改前）
        return checks.ToArray();
    }

    /// <summary>
    /// 局部热稳定那一行的构造（Judge 与 <see cref="ApplyLocalStabFullGrid"/> 共用；决 103 2026-09-24 从 Judge 原样提出）。
    /// 取全线最不稳定的那一片（任何一片判不了 ⇒ 整条判不了）。改回口径下与改前逐字相同（含末句「现为参考量」）；
    /// 决 103 口径下末句换成全格精算的说明与代价（全格精算做了的话），Kind 由分工况表盖（带玻璃稳态硬判据）。
    /// </summary>
    public static ConstraintOut LocalStabCheck(LineCase c, FlangeOut[] flanges)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (flanges is null) throw new ArgumentNullException(nameof(flanges));
        // 局部热稳定：取**全线最不稳定的那一格**（由场解逐格筛出，见
        // ShellThermalResult.LocalStabMargin）。
        //
        // ★ 第一版拿「圆盘最热那一格」当代表，**是错的**：实测两个现役档上
        //   盘温峰落在外缘，那里电流密度≈0 ⇒ 裕度算出 +∞ ⇒ 判据表上会写着
        //   「无限安全」而其实一格都没验。最热 ≠ 最不稳定。
        // ★★ **任何一片判不了 ⇒ 整条判不了**（2026-08-24 修）。
        //
        //   原来是 `Where(!NaN).OrderBy(margin).First()` —— 取「剩下几片里最差的」。
        //   可判不了的恰恰是**发散的那一片**（温度出了电阻率拟合区间），
        //   于是发散算例上会报出一个由**健康片**算来的漂亮数：
        //   实测 selfcheck B 段「板厚 ×0.5」四片最高 4361 °C，
        //   而这一条报 **1.9×**，看着比设计记录还安全。
        //   这是同一个偏乐观偏差在**片这一层**的重演（格那一层已修）。
        bool anyUnknownPlate = flanges.Any(f => double.IsNaN(f.LocalStabMargin));
        var wl = anyUnknownPlate ? null
               : flanges.OrderBy(f => f.LocalStabMargin).FirstOrDefault();
        return new ConstraintOut
        {
            Name = LineResult.Key.LocalStab, Unit = "×", Kind = CheckKind.Reference,
            Actual = wl?.LocalStabMargin ?? double.NaN, Limit = 1.0,
            LessIsBetter = false, Ok = wl is not null && wl.LocalStabMargin > 1.0,
            Undetermined = wl is null,
            Where = wl is null ? "—"
                  : $"{wl.Name} {(wl.LocalStabOnTab ? "舌" : "盘")} r={wl.LocalStabRMm:0.0}",
            Note = "J_stab ÷ J_实际，**须 > 1**；< 1 即该点会自行升温直到烧断。"
                 + (wl is null
                    ? "　有片的温度超出电阻率拟合区间 ⇒ **整条判不了**（多半是场解已发散）。"
                      + "　不拿健康片的数充数 —— 那会报出一个比设计记录还安全的假象"
                    : $"　该点 {wl.LocalStabTempC:0} °C、J={wl.LocalStabJAPerMm2:0.00} A/mm²。"
                      + $"　横向导热长 L={wl.LocalStabLatLenMm:0.0} mm（到最近**定温锚点**：管孔 / 压接段）。"
                      + "　L 若按「不计横向导热」取 ∞，两个现役设计记录会被判成 0.6×（失稳）——保守到失真不叫保守，叫判据坏了。")
                 + (c.RuleSet == CriteriaRuleSet.决103前 ? "　⚠ 现为参考量，同上。" : LocalStabFullGridNote(flanges))
        };
    }

    /// <summary>决 103：局部热稳定一行末尾的全格精算说明（做了 ⇒ 逐片「前 12 名口径 → 全格真值」与代价；没做 ⇒ 说明本值还是前 12 名口径、偏乐观）。</summary>
    public static string LocalStabFullGridNote(FlangeOut[] flanges)
    {
        var done = flanges.Where(f => f is not null && f.LocalStabFullCells > 0).ToArray();
        if (done.Length == 0)
            return "　⚠ 本值还是「两区各取前 12 名」口径（决 99 实测偏乐观 3.5～4.9 倍、落点偏近）；整线收尾时换成全格精算值。";
        string Per(FlangeOut f) => double.IsNaN(f.LocalStabMargin)
            ? $"{f.Name} 判不了（前 12 名口径 {f.LocalStabTop12Margin:0.###}）"
            : $"{f.Name} {f.LocalStabMargin:0.###}（r {f.LocalStabRMm:0.0}）← 前 12 名口径 {f.LocalStabTop12Margin:0.###}（r {f.LocalStabTop12RMm:0.0}）"
              + (f.LocalStabMargin > 0 && !double.IsNaN(f.LocalStabTop12Margin) ? $"，前 12 名偏乐观 {f.LocalStabTop12Margin / f.LocalStabMargin:0.00} 倍" : "");
        // 秒数写成「用时 x s」：挂钟量不是模型的数，转储对拍一律按这个写法去挂钟（R48PropsWiringGateTests.DumpNoTiming、R48CriteriaSwapDump.Comparable）
        return $"　全格精算（决 99 选项 A；全片自由格逐格同一个判法）：{string.Join("；", done.Select(Per))}。"
             + $"代价：{done.Sum(f => f.LocalStabFullCells)} 格、用时 {done.Sum(f => f.LocalStabFullSec):0.0} s（只对终局那一份场各片算一次）。";
    }

    /// <summary>
    /// ★★★★★ 决 103（业主 2026-09-24「局部热稳定落点按决 99 修全格」；决 99 选项 A）：整线收尾对终局那一份场逐片做**局部热稳定全格精算**，
    /// 片上 LocalStab* 换成全格值（原前 12 名口径的值留在 LocalStabTop12*），判据表那一行用 <see cref="LocalStabCheck"/> 重建并按分工况表盖 Kind；
    /// 那一行已被后置标记判成「判不了」的（场没解到位、保温分界、压接、管散热表超界）不动 —— 判不了照旧。
    /// 片上 LocalStabRMm 换成全格落点 ⇒ 细区半径的热点核对（MeshVerify.HotspotRadiusMm，C4′）读到的是真位置。
    /// 改回口径（决103前）什么都不做（逐位同改前）。代价写进 <see cref="LineResult.LocalStabFullGridSec"/> 与 Notes。
    /// </summary>
    public static void ApplyLocalStabFullGrid(LineCase c, LineResult res)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        if (res is null) throw new ArgumentNullException(nameof(res));
        if (c.RuleSet == CriteriaRuleSet.决103前 || res.Flanges.Length == 0) return;
        double sec = 0; int cells = 0, done = 0;
        foreach (var f in res.Flanges)
        {
            if (f?.LocalStabFullGrid is not { } eval) continue;
            var sc = eval();
            f.LocalStabTop12Margin = f.LocalStabMargin; f.LocalStabTop12RMm = f.LocalStabRMm;
            f.LocalStabMargin = sc.Margin; f.LocalStabRMm = sc.RMm; f.LocalStabTempC = sc.TempC;
            f.LocalStabJAPerMm2 = sc.JAPerMm2; f.LocalStabLatLenMm = sc.LatLenMm; f.LocalStabOnTab = sc.OnTab;
            f.LocalStabFullCells = sc.CellsEvaluated; f.LocalStabFullSec = sc.Seconds;
            f.LocalStabFullGrid = null;   // 用过即弃：同一份场不重复算，也放掉闭包
            sec += sc.Seconds; cells += sc.CellsEvaluated; done++;
        }
        if (done == 0) return;
        res.LocalStabFullGridSec += sec; res.LocalStabFullGridCells += cells;
        int idx = Array.FindIndex(res.Checks, x => x is not null && x.Name == LineResult.Key.LocalStab);
        if (idx >= 0 && !res.Checks[idx].Undetermined)
        {
            var nw = LocalStabCheck(c, res.Flanges);
            ApplyStateCriteria(c.EmptyTube, new[] { nw }, c.RuleSet);
            res.Checks[idx] = nw;
        }
        res.Notes.Add($"局部热稳定全格精算（决 99 选项 A）：{done} 片、{cells} 格、用时 {sec:0.0} s（只对终局那一份场）。");
    }

    /// <summary>
    /// ★★★★★ 决 103（业主 2026-09-24）：**带玻璃稳态卡交付的热侧、冷侧两条的构造** —— Judge 只调这一处；公开是为了让快门拿合成的片直接验（取哪一片、判不了怎么报）。
    /// 热侧：第 j 片 H_j = max(该片法兰温度场) − 该片管接触处温度 = <see cref="FlangeOut.TMaxC"/> − <see cref="FlangeOut.TRootC"/> ≤ <see cref="LineCase.HotOverContactMaxK"/>（10 K）。
    ///   接触处温度的取法见 <see cref="CriteriaRules.ContactBasisNote"/>（旧判法「圆盘区最高温 − 管温」同一基准）；热偶设定值只印对照。
    /// 冷侧：第 j 片 C_j = 管孔处由管流入法兰的净热流 = <see cref="FlangeOut.QFromTubeW"/>（正 = 管 → 法兰，即法兰在抽管子的热、把管在接触处拉低）≤ 0 W，不给预算。
    ///   与「管孔净流入须为正」是同一个量，合格方向相反（原判据要它 &gt; 0；决 103 要它 ≤ 0）。
    /// 任何一片判不了（NaN）⇒ 整条判不了，点名是哪几片。片数必须 = 段数 + 1（同 <see cref="ThermocoupleChecks"/> 的那道闸）。
    /// </summary>
    public static (ConstraintOut Hot, ConstraintOut Cold) ContactChecks(LineCase c, SegmentOut[] segs, FlangeOut[] flanges)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        int nSeg = segs?.Length ?? 0, nFl = flanges?.Length ?? 0;
        double hotLim = c.HotOverContactMaxK, coldLim = CriteriaRules.TubeToFlangeHeatMaxW;
        if (nSeg < 1 || nFl != LineSolver.FlangeCount(nSeg))
        {
            string why = nSeg < 1 ? "没有管段，管接触处温度无从取"
                                  : $"法兰片数 {nFl} 与段数 {nSeg} 对不上（应为 {LineSolver.FlangeCount(nSeg)} 片）";
            ConstraintOut Blind(string name, string unit, double lim) => new()
            {
                Name = name, Unit = unit, Kind = CheckKind.HardSafety, LessIsBetter = true,
                Actual = double.NaN, Limit = lim, Ok = false, Undetermined = true, Where = "—",
                Note = $"★ **无法判定**：{why} —— 缺的那几片一次都没判过。不要把它读成通过。",
            };
            return (Blind(LineResult.Key.HotOverContact, "K", hotLim), Blind(LineResult.Key.TubeToFlangeHeat, "W", coldLim));
        }
        var fl = flanges!; var sg = segs!;
        int n = sg.Length;
        // 第 j 片两侧的段端温度（端片只有一侧）：段 i 的 A 端贴第 i 片、B 端贴第 i+1 片
        string Ends(int j) => j == 0 ? $"{sg[0].Name} 首端 {sg[0].TRootAC:0.0}"
                            : j >= n ? $"{sg[n - 1].Name} 末端 {sg[n - 1].TRootBC:0.0}"
                            : $"{sg[j - 1].Name} 末端 {sg[j - 1].TRootBC:0.0}／{sg[j].Name} 首端 {sg[j].TRootAC:0.0}";
        string Zone(FlangeOut f) => f.TMaxC == f.TDiscMaxC ? "圆盘区" : f.TMaxC == f.TTabMaxC ? "舌片区" : "孔边或压接段的格";

        ConstraintOut hot;
        var hs = fl.Select(f => f.TMaxC - f.TRootC).ToArray();
        var blindH = Enumerable.Range(0, nFl).Where(j => double.IsNaN(hs[j])).ToArray();
        string PerH() => "逐片：" + string.Join("／", Enumerable.Range(0, nFl).Select(j => $"{fl[j].Name} {hs[j]:+0.00;−0.00}")) + " K";
        if (blindH.Length == 0)
        {
            int w = 0;
            for (int j = 1; j < nFl; j++) if (hs[j] > hs[w]) w = j;
            var f = fl[w];
            bool ok = hs[w] <= hotLim;
            double refC = ThermocoupleBasis.At(sg, fl, w).RefC;
            hot = new ConstraintOut
            {
                Name = LineResult.Key.HotOverContact, Unit = "K", Kind = CheckKind.HardSafety, LessIsBetter = true,
                Actual = hs[w], Limit = hotLim, Ok = ok, Where = f.Name,
                Note = $"该片法兰温度场最高 {f.TMaxC:0.0} °C（落在{Zone(f)}；圆盘区峰 {f.TDiscMaxC:0.0}、舌片区峰 {f.TTabMaxC:0.0}）"
                     + $" − 管接触处 {f.TRootC:0.0} °C（段端：{Ends(w)} °C）。{CriteriaRules.ContactBasisNote}"
                     + $"　热偶设定值基准 {refC:0.0} °C 只印对照（最热处比它高 {f.TMaxC - refC:+0.0;−0.0} K，不判）。{PerH()}。"
                     + $"允许高出 {hotLim:0.###} K（参数表「法兰最热处高出管接触处温度 允许值」，2026-09-24 定：法兰比管接触处略热的方向是对的，可容许到 10 °C 以内）。"
                     + (ok ? "" : "　【下一步】法兰偏热：板厚↑（或舌保温↓）；求解器只抬不降，这一条由板厚与内外级倍率治。")
            };
        }
        else
            hot = new ConstraintOut
            {
                Name = LineResult.Key.HotOverContact, Unit = "K", Kind = CheckKind.HardSafety, LessIsBetter = true,
                Actual = double.NaN, Limit = hotLim, Ok = false, Undetermined = true,
                Where = string.Join("、", blindH.Select(j => fl[j].Name)),
                Note = $"★ **无法判定**：{blindH.Length} 片的法兰最高温或管接触处温度算不出（NaN）—— **任何一片判不了，整条就判不了**，不拿剩下几片充数。不要把它读成通过。"
            };

        ConstraintOut cold;
        var qs = fl.Select(f => f.QFromTubeW).ToArray();
        var blindC = Enumerable.Range(0, nFl).Where(j => double.IsNaN(qs[j])).ToArray();
        string PerC() => "逐片：" + string.Join("／", Enumerable.Range(0, nFl).Select(j => $"{fl[j].Name} {qs[j]:+0.00;−0.00}")) + " W";
        if (blindC.Length == 0)
        {
            int w = 0;
            for (int j = 1; j < nFl; j++) if (qs[j] > qs[w]) w = j;
            bool ok = qs[w] <= coldLim;
            cold = new ConstraintOut
            {
                Name = LineResult.Key.TubeToFlangeHeat, Unit = "W", Kind = CheckKind.HardSafety, LessIsBetter = true,
                Actual = qs[w], Limit = coldLim, Ok = ok, Where = fl[w].Name,
                Note = "管孔处由管流入法兰的净热流（正 = 法兰在抽管子的热、把管在接触处的温度拉低；负 = 法兰给管子送热）；须 ≤ 0，不给预算"
                     + "（2026-09-24 定：拉低管温的方向是错的）。与「管孔净流入须为正」是同一个量，合格方向相反。"
                     + $"{PerC()}。"
                     + (ok ? "" : "　【下一步】法兰在抽管子的热：舌保温↑、板厚↓；铜排夹持温度↑（少冷却，第二阶段旋钮）。求解器只抬不降，这一条由舌保温、圆盘槽、舌孔治。")
            };
        }
        else
            cold = new ConstraintOut
            {
                Name = LineResult.Key.TubeToFlangeHeat, Unit = "W", Kind = CheckKind.HardSafety, LessIsBetter = true,
                Actual = double.NaN, Limit = coldLim, Ok = false, Undetermined = true,
                Where = string.Join("、", blindC.Select(j => fl[j].Name)),
                Note = $"★ **无法判定**：{blindC.Length} 片的管孔净热流算不出（NaN）—— **任何一片判不了，整条就判不了**。不要把它读成通过。"
            };
        return (hot, cold);
    }

    /// <summary>
    /// ★★★★★ K 路（2026-09-15，Opus 5）：按工况把判据表里每条的 Kind 盖成 <see cref="LineResult.StateKindOf"/> 给的那个（分工况表只此一份，见 <see cref="LineResult.RequiredByState"/>）。
    /// 由硬判据降为参考量的，说明最前面加 <see cref="LineResult.StateDowngradeNote"/>；值、Ok、判不了照原样留着（照常计算与打印）。
    /// 不在分工况表里的判据不动。Judge 末尾调它；公开是为了让门拿合成的判据条目直接验「同一条在两个工况下判不判」，不必跑整线解。
    /// </summary>
    public static void ApplyStateCriteria(bool emptyTube, IEnumerable<ConstraintOut> checks)
        => ApplyStateCriteria(emptyTube, checks, CriteriaRuleSet.决103);

    /// <summary>
    /// 决 103（2026-09-24）：按口径盖 Kind。改回口径（决103前）与改前逐位相同（只有空管态降参考、说明加 <see cref="LineResult.StateDowngradeNote"/>）。
    /// 决 103 口径：带玻璃稳态下本身就只作参考的（三条热偶／净流入）降级时说明加 <see cref="CriteriaRules.DowngradeNote"/>；
    ///   由参考升为硬判据的（两条热稳定）说明加 <see cref="CriteriaRules.UpgradeNote"/>；只在空管态降参考的照旧加 StateDowngradeNote。
    /// </summary>
    public static void ApplyStateCriteria(bool emptyTube, IEnumerable<ConstraintOut> checks, CriteriaRuleSet rs)
    {
        if (checks is null) throw new ArgumentNullException(nameof(checks));
        foreach (var ck in checks)
        {
            if (ck is null) continue;
            var k = LineResult.StateKindOf(ck.Name, emptyTube, rs);
            if (k is null || k.Value == ck.Kind) continue;
            bool downgraded = k.Value == CheckKind.Reference && ck.Kind != CheckKind.Reference;
            bool upgraded = k.Value != CheckKind.Reference && ck.Kind == CheckKind.Reference;
            ck.Kind = k.Value;
            if (downgraded)
            {
                // 带玻璃稳态下本条也只作参考 ⇒ 是口径换向降的级，不是工况降的级
                bool glassRef = rs != CriteriaRuleSet.决103前 && LineResult.StateKindOf(ck.Name, false, rs) is CheckKind.Reference;
                ck.Note = (glassRef ? CriteriaRules.DowngradeNote : LineResult.StateDowngradeNote(emptyTube))
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　" + ck.Note);
            }
            else if (upgraded)
                ck.Note = CriteriaRules.UpgradeNote + (string.IsNullOrEmpty(ck.Note) ? "" : "　" + ck.Note);
        }
    }
}
