using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

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
    /// </summary>
    public FlangePlate[] GeomForJudge = Array.Empty<FlangePlate>();

    /// <summary>
    /// `.3dm` 模式下的**舌片保温厚度** mm。NaN 或 &lt; 0.05 = 舌片裸露（原行为）。
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
    public double TabInsul3dmMm = double.NaN;

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
    public double RootDeltaMaxK = 10.0;

    /// <summary>
    /// 判据 ②″（圆盘区最高温 − 管温）的上限 K。
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
    public double CoupleTolK = 1.0;

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

    public SegmentOut[] Segments = Array.Empty<SegmentOut>();
    public FlangeOut[] Flanges = Array.Empty<FlangeOut>();
    public ConstraintOut[] Checks = Array.Empty<ConstraintOut>();
    public double TubeMassG, FlangeMassG, TotalMassG, BaselineMassG, SavingPct;
    public double GlassDropModelK, GlassDropMeasuredK;
    public readonly List<string> Notes = new();
    public bool Ok = true;
    /// <summary>段↔法兰外层耦合是否收敛。**为 false 时表内所有数值一律不可引用。**</summary>
    public bool Converged;

    /// <summary>
    /// ★ 本次解**越过了铂熔点** ⇒ <see cref="Ok"/> 为 false（2026-09-07 A）。
    ///   单独立一个位而不是让调用方去 match 讯息文字：
    ///   「熔化」与别的失败**处置完全不同** —— 别的失败是「算不出来」，
    ///   熔化是**方向信息**：这一处太薄了，加厚就能走出去。
    ///   定尺寸器靠它把「没解」变成「往加厚那边走」，而不是拿到 NaN 卡死。
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
        public const string NetFlux = "②′管孔净流入";      // B：热流方向本身
        public const string DiscTemp = "②″圆盘区最高温";     // C：贴管子那一圈
        public const string FlangeDip = "③ 法兰增量温降";    // 法兰挖的坑

        // ★ 2026-08-20 补三条。它们一直都是判据（都在 Judge 里、都是硬安全线），
        //   只是此前没人用常量引用过 ⇒ 常量表缺了它们。界面门禁（UI/Flow.cs 的 GateSpec）
        //   要按名字读这三条，而门禁**只准用常量** —— 判据改名时编译期就断，
        //   而不是门禁悄悄永远放行。
        public const string FreeTab = "⑤ 舌片自由段";        // 现场铜排装得下吗（几何闭式）
        public const string DiscCover = "⑥ 圆盘盖得住管孔";   // 盘半径 − 管孔半径 − 焊脚（几何闭式）
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
    /// </summary>
    public static readonly (string Prefix, CheckKind Kind, bool NeedsRamp)[] Required =
    {
        (LineResult.Key.Ramp,      CheckKind.HardSafety, true),   // CheckRamp=false 时**合法缺席**
        (LineResult.Key.NetFlux,   CheckKind.HardSafety, false),
        (LineResult.Key.DiscTemp,  CheckKind.HardSafety, false),
        (Key.FreeTab,   CheckKind.HardSafety, false),
        (Key.DiscCover, CheckKind.HardSafety, false),
        (Key.TubeJ,     CheckKind.HardSafety, false),
        (Key.SectionJ,  CheckKind.HardSafety, false),   // 2026-09-08 用户设计因果链第 ④ 步：截面 J 全体 < 11
        (LineResult.Key.FlangeDip, CheckKind.Target,     false),
    };

    /// <summary>该出现却没出现的判据。**缺席 ≠ 通过。**</summary>
    public string[] MissingChecks => Required
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
    public bool HardOk => !Required.Any(q => q.Kind == CheckKind.HardSafety
                                          && (!q.NeedsRamp || RampChecked) && Find(q.Prefix) is null)
                       && HardBlocked.Length == 0;

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
    public bool AllOk => Converged && MissingChecks.Length == 0 && Checks
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
        .ToArray();
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
    }

    public static LineResult Run(LineCase c, IProgress<string>? progress = null,
                                 CancellationToken cancel = default)
    {
        // ★ 先把「没显式指定」的项接到参数表上。必须在**任何**读取这三项之前。
        Normalize(c);

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
            (double L, double R)[]? bnb = null;
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
            int baseMaxRounds = Math.Max(30, c.CoupleMaxRounds);
            for (int k = 0; k < baseMaxRounds; k++)
            {
                br = RunOnce(c, null, cancel, zero, zeroLR, bnb);
                if (!br.Ok) break;
                var nb2 = new (double L, double R)[c.SegmentCount];
                for (int i = 0; i < c.SegmentCount; i++)
                    nb2[i] = (i == 0 ? double.NaN : br.Segments[i - 1].TRootBC,
                              i == c.SegmentCount - 1 ? double.NaN : br.Segments[i + 1].TRootAC);
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
                double baseJudge = c.Base.BaselineTolAmplified
                                 ? baseResid * LineCase.FixedPointAmp
                                 : dmax;                      // 历史口径
                if (baseJudge < c.CoupleTolK) { baseRounds = k + 1; baseDmax = baseJudge; break; }
                baseRounds = k + 1; baseDmax = baseJudge;
            }
            if (baseDmax >= c.CoupleTolK)
                baseFailMsg += $"基线外层 {baseRounds} 轮**未收敛**（"
                             + (c.Base.BaselineTolAmplified ? "到不动点估计" : "欠松弛步（**历史口径**）")
                             + $" {baseDmax:0.00} K ≥ 容差 {c.CoupleTolK:0.00}）；";
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
                          warmDraw?.Select(w => 0.5 * (w.L + w.R)).ToArray(),
                          warmDraw, warmNb, baseline);
        if (!res.Ok) return res;
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
                // ★★★★★ 能量守恒（2026-08-28）：**内部共用片属于两段，必须分配**。
                //   端片（法兰 0 与法兰 n）只属于一段 ⇒ 整份。
                //   内部片 j 同时是「段 j−1 的右端」与「段 j 的左端」⇒ 各半，Q_L + Q_R = Q。
                //   不分配时管子失去 Q₀ + 2ΣQ内 + Q_n，法兰只收到 ΣQ —— 实测残差 +2.66/+3.74 W。
                //   ⚠ 默认**关**：打开会改动设计记录的数。见 DesignInputs.SplitSharedFlangeDraw。
                bool sp = c.Base.SplitSharedFlangeDraw;
                double aEff = sp && i > 0 ? 0.5 * a : a;                        // 左端：i>0 ⇒ 内部片
                double bEff = sp && i < c.SegmentCount - 1 ? 0.5 * b : b;       // 右端：i<n−1 ⇒ 内部片
                targetLR[i] = (aEff, bEff);          // ★ 两端各自回灌（原来取平均是另一个 bug，已修）
            }
            draws ??= new double[c.SegmentCount];
            drawsLR ??= new (double, double)[c.SegmentCount];

            // ── 段间端温：段 i 的左邻是段 i−1 的**右**端，右邻是段 i+1 的**左**端。
            //    整线两头没有邻段 ⇒ NaN（退化为纯法兰抽热边界）。
            var nbNew = new (double L, double R)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
                nbNew[i] = (i == 0 ? double.NaN : res.Segments[i - 1].TRootBC,
                            i == c.SegmentCount - 1 ? double.NaN : res.Segments[i + 1].TRootAC);
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
                draws[i] = 0.5 * (drawsLR[i].L + drawsLR[i].R);   // 仅作兼容/汇报
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
            if (!next.Ok) return next;
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
            delta = 0;
            for (int i = 0; i < c.SegmentCount; i++)
            {
                double da = Math.Abs(next.Segments[i].TRootAC - res.Segments[i].TRootAC);
                double db = Math.Abs(next.Segments[i].TRootBC - res.Segments[i].TRootBC);
                if (!double.IsNaN(da)) delta = Math.Max(delta, da);
                if (!double.IsNaN(db)) delta = Math.Max(delta, db);
            }
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
            const double ampWorst = LineCase.FixedPointAmp;   // 唯一来源，别在这里另写一个数
            double amp = (rEst > 0.5 && rEst < 0.999) ? rEst / (1 - rEst) : ampWorst;
            double remain = delta * amp;
            // ⚠ 两条**都**要过：δ 那条防「步子还很大」，真残差那条防「步子小但不在不动点上」。
            //   放大取已知最坏 25（= 1/(1−g)，g≈0.96）——真残差乘它才是到不动点的距离。
            bool resOk = resK * ampWorst < c.CoupleTolK;
            if (remain < c.CoupleTolK && resOk)
            {
                res.Notes.Add($"外层耦合 {outer + 1} 轮收敛（剩余误差估计 {remain:0.00} K = 步长 {delta:0.00} × 放大 {amp:0.0}，真残差 {resK:0.000} K，ω={omega:0.00}，ω 末值 {omega:0.00}／放大 {omegaBoosts} 次／回退 {omegaCuts} 次）"
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
            double ampNow = rEst > 0.5 && rEst < 0.999 ? rEst / (1 - rEst) : 25.0;
            // 还要多少轮：按几何收缩 δ·r^n·amp < tol 解 n
            string need = "";
            if (shrinking && rEst > 0.5 && rEst < 0.999 && delta > 0)
            {
                double n = Math.Log(c.CoupleTolK / (delta * ampNow)) / Math.Log(rEst);
                if (n > 0 && n < 1e6) need = $"，按当前收缩率还需约 **{Math.Ceiling(n):0} 轮**";
            }
            res.Notes.Add($"★ 外层耦合 {c.CoupleMaxRounds} 轮未收敛（**剩余误差估计 {delta * ampNow:0.0} K**，" +
                          $"步长 {delta:0.0} K，真残差 {resKLast:0.000} K，ω={omega:0.00}）—— 本次结果的每个数都不可用。");
            res.Notes.Add(shrinking
                ? $"★ 残差**仍在单调收缩** ⇒ 是「慢」不是「发散」：加轮数上限即可{need}" +
                  $"（LineCase.CoupleMaxRounds，本次 {c.CoupleMaxRounds}）。"
                : "★ 残差**没有在收缩** ⇒ 加轮数大概率没用：该工况可能真的热失控，或迭代进了极限环。");
            res.Message = "段↔法兰耦合未收敛（" + (shrinking ? "慢，加轮数可解" : "未收缩，疑似失控") + "）";
        }
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
        var res = new LineResult { BaselineMassG = c.BaselineMassG, RampChecked = c.CheckRamp };
        int n = c.SegmentCount, nf = c.FlangeCount;
        if (n < 1) { res.Ok = false; res.Message = "段数不能为 0"; return res; }
        if (c.UseMeasuredCurrent && c.MeasuredCurrentA.Length < n)
        { res.Ok = false; res.Message = $"实测电流只给了 {c.MeasuredCurrentA.Length} 个，需要 {n} 个"; return res; }
        if (c.FlangeFile3dm.Length == 0 && c.FlangePlates.Length == 0)
        { res.Ok = false; res.Message = "未指定法兰几何（.3dm 或解析 FlangePlate 二选一）"; return res; }

        // 各段两端的法兰抽热 W（由壳温度场回灌）。首轮未知，置 0；
        // ★ 必须显式回灌：不设 FlangeDrawOverrideSet 时 SegmentSolver 会**静默回退到
        //   已作废的一维环形模型 FlangeRadial**（§5、§7 记过两次，这是第三次）。
        var drawW = drawIn ?? new double[n];

        // ── ①② 逐段
        var segs = new SegmentOut[n];
        var amps = new double[n];
        var segParams = new DesignInputs[n];   // 各段实际用的参数，判据 ①④ 要拿去复用
        double tg = c.GlassInC;
        for (int i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"段 {i + 1}/{n}：{(c.UseMeasuredCurrent ? "按实测电流求解" : "反算电流")}…");

            var p = SegmentSolver.Clone(c.Base);
            p.TubeIdMm = c.TubeIdMm; p.WallMinMm = c.WallMm; p.TubeLengthMm = c.SegLengthMm[i];
            p.SupportSpanMm = c.SegLengthMm[i]; p.GradeName = c.GradeName;
            p.TSetC = c.SetpointC[i]; p.TGlassInC = tg;
            p.GlassHeadM = i < c.HeadM.Length ? c.HeadM[i] : 0;
            p.SizeWall = false;
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
            if (!sr.Ok) { res.Ok = false; res.Message = $"段 {i + 1} 求解失败：{sr.Message}"; return res; }

            amps[i] = sr.CurrentA;
            segParams[i] = p;
            double area = Math.PI * (Math.Pow(c.TubeIdMm * 0.5 + c.WallMm, 2)
                                     - Math.Pow(c.TubeIdMm * 0.5, 2));
            segs[i] = new SegmentOut
            {
                Name = $"HC{i + 1}",
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
                X = sr.X, TMetal = sr.TMetal, TGlass = sr.TGlass
            };
            tg = sr.TGlassOutC;
        }
        res.Segments = segs;
        res.GlassDropModelK = c.GlassInC - tg;
        res.GlassDropMeasuredK = c.GlassInC - c.GlassOutMeasuredC;

        // ── ③ 逐片法兰
        var flanges = new FlangeOut[nf];
        for (int j = 0; j < nf; j++)
        {
            cancel.ThrowIfCancellationRequested();
            bool analytic = c.FlangePlates.Length > 0;
            var plate = analytic ? c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)] : null;
            string file = analytic ? "" : c.FlangeFile3dm[Math.Min(j, c.FlangeFile3dm.Length - 1)];
            double planeY = j < c.FlangePlaneY.Length ? c.FlangePlaneY[j] : double.NaN;
            progress?.Report(analytic
                ? $"法兰 {j + 1}/{nf}：解析几何 + 建网格 + 解场…"
                : $"法兰 {j + 1}/{nf}：提厚度场 + 建网格 + 解场…");

            double holeR = c.TubeIdMm * 0.5 + c.WallMm;
            ShellMesh mesh;
            if (analytic)
            {
                // 管孔必须跟着管外径走，否则法兰与管子对不上
                plate!.HoleRadiusMm = holeR;
                mesh = FlangeMesher.Build(plate, 0, c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                                          c.Base.BusbarClampLengthMm,
                                          c.MeshInnerMm, c.MeshInnerRadiusMm);
            }
            else
            {
                var tf = Geometry3dm.LoadThickness(file, c.FlangeLayer, planeY, c.ThicknessStepMm);
                // 厚度标度：.3dm 的**形状**固定，但整体厚度可按比例缩放。
                // 这让「自动定厚」在 .3dm 模式下同样可用 —— 求出的不是绝对厚度，
                // 而是「你这张图纸的厚度要整体 ×k」，工程师照着改一版图即可。
                // t=0（无材料：轮廓外、管孔、开槽）乘任何数仍是 0，故槽与轮廓不受影响。
                double[]? lvS = j < c.LevelScale.Length ? c.LevelScale[j] : null;
                double[]? lvT = j < c.LevelThicknessMm.Length ? c.LevelThicknessMm[j] : null;
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
                    tf = new ThicknessField
                    {
                        X0 = tf.X0, Z0 = tf.Z0, Step = tf.Step,
                        Nx = tf.Nx, Nz = tf.Nz, T = scaled
                    };
                }
                mesh = FlangeMesher.BuildFromField(tf, holeR, 0,
                            c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                            c.Base.BusbarClampLengthMm);
            }

            if (j == 0) res.MeshCells = mesh.CellCount;
            double iJoint = LineSolver.JointCurrentA(amps, j);
            var sc = ShellCurrent.SolveFor(c, mesh, iJoint,
                        Materials.PtResistivity(c.SetpointC[Math.Min(j, n - 1)]) * 1e3,
                        c.SetpointC[Math.Min(j, n - 1)]);
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

            var p2 = SegmentSolver.Clone(c.Base);
            p2.TSetC = c.SetpointC[Math.Min(j, n - 1)];
            if (j < c.ClampTempC.Length) p2.BusbarClampTempC = c.ClampTempC[j];
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
            double busGThis = -1, busSecCurMm2 = 0;
            if (c.Base.BusbarConductanceWPerK >= 0 && c.Base.BusbarJAllowAPerMm2 > 1e-9
                && c.Base.BusbarLenToSinkMm > 1e-9)
            {
                double aMm2 = iJoint / c.Base.BusbarJAllowAPerMm2;
                busSecCurMm2 = aMm2;
                p2.BusbarConductanceWPerK = busGThis =
                    BusbarSizing.CuK * (aMm2 * 1e-6) / (c.Base.BusbarLenToSinkMm * 1e-3);
            }
            // 保温分界：解析几何用该片自己的分界（可为「全裸」= +∞ 之外），
            // .3dm 路径沿用现场实况「仅圆盘保温、舌片裸露」的切点。
            double insulX = analytic ? plate!.InsulBoundaryXResolved
                                     : new FlangePlate().InsulBoundaryXResolved;
            // ★ .3dm 也能包舌保温（2026-08-23）。切点取自「分析几何变数」反推的等效片
            //   —— 与 ⑤⑥ 用的是同一组几何，不另立一套。
            //   没有等效片（没分析过）或没给厚度时，仍按裸舌走，行为与从前一致。
            var eq3 = !analytic && c.GeomForJudge is { Length: > 0 } ? c.GeomForJudge[0] : null;
            ShellThermalResult Thermal(ShellCurrentResult cur) =>
                ShellThermal.Solve(mesh, cur.JMagAPerMm2, p2, tRoot, insulX,
                                   symmetricInsul: analytic && plate!.TwoTabs,
                                   tabBoundaryX: analytic ? plate!.Tangent().X
                                                : eq3?.Tangent().X ?? double.NaN,
                                   tabInsulThickMm: analytic ? plate!.TabInsulThickMm
                                                             : c.TabInsul3dmMm);
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
                    var sc2 = ShellCurrent.SolveFor(c, mesh, iJoint,
                                  Materials.PtResistivity(c.SetpointC[Math.Min(j, n - 1)]) * 1e3,
                                  c.SetpointC[Math.Min(j, n - 1)], tempC: th.T);
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

            flanges[j] = new FlangeOut
            {
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
                QGenDiscW = th.QGenDiscW, QLossDiscW = th.QLossDiscW,
                QGenTabW = th.QGenTabW, QLossTabW = th.QLossTabW,
                TDiscMeanC = th.TDiscMeanC, TTabMeanC = th.TTabMeanC,
                TDiscMaxC = th.TDiscMaxC, TTabMaxC = th.TTabMaxC,
                DiscMaxXMm = th.DiscMaxXMm, DiscMaxZMm = th.DiscMaxZMm,
                DiscMaxRMm = th.DiscMaxRMm, DiscMaxJAPerMm2 = th.DiscMaxJAPerMm2,
                LocalStabMargin = th.LocalStabMargin, LocalStabRMm = th.LocalStabRMm,
                LocalStabTempC = th.LocalStabTempC, LocalStabJAPerMm2 = th.LocalStabJAPerMm2,
                LocalStabLatLenMm = th.LocalStabLatLenMm,
                LocalStabOnTab = th.LocalStabOnTab,
                DiscMaxThickMm = th.DiscMaxThickMm,
                TMaxC = th.TMaxC, TMinC = th.TMinC, TTabEndC = th.TTabEndMeanC,
                AreaMm2 = mesh.TotalArea, VolumeMm3 = mesh.VolumeMm3,
                CellCount = mesh.CellCount,
                Mesh = mesh, JField = sc.JMagAPerMm2, TField = th.T,
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
                res.OverMelt = true;
                flanges[j].OverMelt = true;      // ★ 逐片的位：求解器按它决定抬哪一片的下角
                res.Message = $"{flanges[j].Name}：峰值 {th.TMaxC:0} °C 已越过铂熔点 "
                            + $"{Materials.PtMeltC:0} °C —— **该解不存在**（不是「不够好」，是物理上不成立）";
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
        return res;
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
    ///     DesignInputs 那一段），并且列在 <see cref="LineResult.Required"/> 里
    ///     ⇒ 缺席即翻 AllOk。而 0.6 档正贴着这条线交付（10.96/12）。
    ///     ⚠ 那句「J_allow = 10 待定」说的是**法兰**那条参考量（<c>JAllowAPerMm2</c>），
    ///       与管 J 是两个数，2026-08-15 已分家。
    ///
    /// ⇒ **判据清单以 <see cref="LineResult.Required"/> 与 HANDOVER §1.83 为唯一来源**
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
    /// 本仓库为「判据缺席」栽过三次，最后也是靠一份**名单**解决的（见 LineResult.Required）。
    ///
    /// ⚠ 只标**吃法兰场**的那些。纯几何（⑤⑥）与纯管子的（管 J、④）不受影响 ——
    ///   把它们一并标成判不了是**过度**，会掩盖真正该看的东西。
    /// </summary>
    private static void MarkUndeterminedIfFieldsFailed(LineResult res, FlangeOut[] flanges)
    {
        var bad = flanges.Where(f => f is not null && !f.FieldsConverged).ToArray();
        if (bad.Length == 0) return;

        // 吃法兰场的判据名单。**加判据时要同步加这里** —— 不加的后果是它在场没解到位时
        // 照样报数，而那正是本方法要挡的事。
        string[] dependsOnFlangeFields =
        {
            LineResult.Key.NetFlux, LineResult.Key.DiscTemp, LineResult.Key.FlangeDip, LineResult.Key.Ramp,
            LineResult.Key.FlangeStab, LineResult.Key.LocalStab, LineResult.Key.RampField, LineResult.Key.HeatBalance,
            LineResult.Key.FlangeTopTemp, LineResult.Key.SelfSupply, LineResult.Key.FlangeJ, LineResult.Key.HeatResidual,
        };
        string who = string.Join("、", bad.Select(f => $"{f.Name}（{f.FieldNote}）"));

        foreach (var ck in res.Checks)
            if (dependsOnFlangeFields.Any(k => ck.Name.StartsWith(k, StringComparison.Ordinal)))
            {
                ck.Undetermined = true;
                ck.Ok = false;
                ck.Note = "★ **无法判定**：这条判据吃法兰的场，而场没解到位 —— " + who
                        + "。**判不了不算过。** 原值仅供诊断，不得引用。"
                        + (string.IsNullOrEmpty(ck.Note) ? "" : "　（原注：" + ck.Note + "）");
            }
    }

    private static ConstraintOut[] Judge(LineCase c, LineResult res, SegmentOut[] segs,
                                         FlangeOut[] flanges, DesignInputs[] segParams)
    {
        var checks = new List<ConstraintOut>();
        int n = segs.Length;

        // ── ① 升温：空管能否在限时内到目标温度
        //    法兰随管一起被加热，且**自身也发热**，故用本算例真实的法兰质量与自身焦耳热
        //    （RampSolver 据此反推法兰电阻）。这比 --ramp 另跑一次稳态耦合解取参考值更准。
        if (c.CheckRamp)
        {
            RampResult? worst = null; string where = "";
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
            }
            // ⚠ 这里**故意不写 else**：n < 1 在 RunOnce 入口就被挡掉（「段数不能为 0」），
            //   所以 n >= 1 ⇒ 循环至少跑一轮 ⇒ worst 必非 null。写个 else 就是一段
            //   永远跑不到、也永远没被验过的代码，而「死代码里的错答案」正是本项目的病灶之一。
            //   万一将来这个前提被改掉：兜底的是 LineResult.Required —— CheckRamp 为真时
            //   ① 在必备名单里，缺了 AllOk 直接为 false 并在 Failed 里报「判据缺席」。
            if (worst is not null)
                checks.Add(new ConstraintOut
                {
                    Name = "① 升温 空管到目标", Unit = "h", Kind = CheckKind.HardSafety,
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
                });
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
        //    现在补 else 报「无法判定」，并且 LineResult.Required 会**独立地**再兜一次底 ——
        //    两道是有意重复的：else 给得出原因，名单保证下一条新判据漏写 else 时也不会溜过去。
        var hottestDisc = flanges.Where(f => !double.IsNaN(f.TDiscMaxC))
                                 .OrderByDescending(f => f.TDiscMaxC - f.TRootC).FirstOrDefault();
        if (hottestDisc is not null)
            checks.Add(new ConstraintOut
            {
                Name = "②″圆盘区最高温 − 管温", Unit = "K", Kind = CheckKind.HardSafety,
                Actual = hottestDisc.TDiscMaxC - hottestDisc.TRootC, Limit = c.DiscOverTempMaxK,
                Ok = hottestDisc.TDiscMaxC - hottestDisc.TRootC <= c.DiscOverTempMaxK + 1e-6,
                Where = hottestDisc.Name,
                // ★ 判据必须自带**病灶位置**：只报差值时，「盘峰贴在管孔上」与
                //   「轮毂上被自身发热顶起一个尖峰」给出同一个数，却要用相反的旋钮去治。
                //   r≈管外径 且 J≈0 ⇒ 病在管侧；r 更大且 J 不为零 ⇒ 病在法兰侧。
                Note = $"限值 = 现场控温精度 ±5 K（用户 2026-08-15）；原限值 0 判的是 14 mW 倒流（占段功率 5 ppm）。" +
                       $"圆盘区 {hottestDisc.TDiscMaxC:0.0} vs 管根 {hottestDisc.TRootC:0.0} °C；" +
                       $"峰位 r={hottestDisc.DiscMaxRMm:0.0} mm（x={hottestDisc.DiscMaxXMm:+0.0;−0.0}, " +
                       $"z={hottestDisc.DiscMaxZMm:+0.0;−0.0}）J={hottestDisc.DiscMaxJAPerMm2:0.00} " +
                       $"t={hottestDisc.DiscMaxThickMm:0.00} mm；" +
                       $"舌片区峰值 {hottestDisc.TTabMaxC:0.0} °C（另由熔点与局部失稳管）；" +
                       $"管孔净流入 {hottestDisc.QFromTubeW:+0;-0} W" +
                       (hottestDisc.TDiscMaxC - hottestDisc.TRootC > c.DiscOverTempMaxK
                        ? NextAction.DiscHot : "")
            });
        else
            checks.Add(new ConstraintOut
            {
                Name = "②″圆盘区最高温 − 管温", Unit = "K", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = c.DiscOverTempMaxK, Ok = false,
                Undetermined = true, Where = "—",
                Note = "★ **无法判定**：没有任何一片算出圆盘区温度（每一片的网格里都没有" +
                       "落在圆盘区的单元）。**不要把它读成通过** —— 这一关没被检查过。" +
                       "常见成因：几何来自 .3dm 厚度场而盘/舌分区认不出来，或盘半径 ≤ 孔半径。"
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
        checks.Add(new ConstraintOut
        {
            Name = "②′管孔净流入 须为正", Unit = "W", Kind = CheckKind.HardSafety,
            Actual = worstFlux.QFromTubeW, Limit = 0, LessIsBetter = false,
            Ok = worstFlux.QFromTubeW > 0, Where = worstFlux.Name,
            Note = worstFlux.QFromTubeW <= 0
                 ? "★ 热正在往管子里灌 —— 这是烧断的过程。" + NextAction.NetFluxLow
                 : "法兰在从管子抽热，方向安全。" + NextAction.DrawWindow
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
        var dips = segs.Where(s => !double.IsNaN(s.FlangeDipK)).ToArray();
        if (dips.Length > 0)
        {
            var deepest = dips.OrderByDescending(s => s.FlangeDipK).First();
            checks.Add(new ConstraintOut
            {
                Name = "③ 法兰增量温降 ≤ 上限", Unit = "K", Kind = CheckKind.Target,
                Actual = deepest.FlangeDipK, Limit = c.RootDeltaMaxK,
                Ok = deepest.FlangeDipK <= c.RootDeltaMaxK, Where = deepest.Name,
                Note = $"= 无法兰基线 − 实际（{deepest.BaseTRootAC:0.0}/{deepest.BaseTRootBC:0.0} " +
                       $"vs {deepest.TRootAC:0.0}/{deepest.TRootBC:0.0} °C）；控温点梯度不算在内。" +
                       (deepest.FlangeDipK > c.RootDeltaMaxK ? NextAction.DipHigh : NextAction.DrawWindow)
            });
        }
        else
        {
            checks.Add(new ConstraintOut
            {
                Name = "③ 法兰增量温降 ≤ 上限", Unit = "K", Kind = CheckKind.Target,
                Actual = double.NaN, Limit = c.RootDeltaMaxK, Ok = false,
                Undetermined = true, Where = "—",
                Note = "★ **无法判定**：无法兰基线没算出来（LineCase.BaselineRootC 为空且基线子解失败）。" +
                       "不要把它读成通过。"
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
        double utilMax = 0; string utilWhere = ""; bool undetermined = false; string undetNote = "";
        for (int i = 0; i < n; i++)
        {
            var mr = Mechanics.Check(segParams[i], c.WallMm, 2.0, new FlangePlate());
            Mechanics.ApplyAllowable(mr, segParams[i], segs[i].SetpointC, segs[i].SetpointC);
            if (double.IsNaN(mr.TubeAllowMPa))
            {
                undetermined = true;
                var g = MaterialDb.Get(segParams[i].GradeName);
                undetNote = $"{segs[i].Name} {segs[i].SetpointC:0} °C 落在 {segParams[i].GradeName} " +
                            $"持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 之外（§6 待补 ③）";
                continue;
            }
            if (mr.TubeUtil > utilMax) { utilMax = mr.TubeUtil; utilWhere = segs[i].Name; }
        }
        checks.Add(new ConstraintOut
        {
            Name = "④ 管强度利用率", Unit = "—", Kind = CheckKind.Reference,
            Actual = utilMax, Limit = 1.0, Ok = true,
            Where = undetermined ? undetNote : utilWhere,
            Undetermined = undetermined,
            // ★ 2026-08-14 用户：「蠕变、铂金加热蒸发也都不必考虑」「都留有膨胀考量」
            //   ⇒ 持久强度不再是约束，本项降为**参考量**，不再因它判不可行。
            //   同时也就不再需要「落在实测区间之外 ⇒ 无法判定」那条护栏去卡整个方案。
            //   注意：熔点与局部热失稳**不属于**被划掉的那三条，仍然是硬判据。
            Note = "参考（用户 2026-08-14：蠕变不必考虑，本项降级）；" +
                   "法兰不承重（氧化铝管托底，§4.2d），故不校核舌片"
        });

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
                var platesJ = c.FlangePlates is { Length: > 0 } ? c.FlangePlates : c.GeomForJudge;
                if (platesJ is null || platesJ.Length == 0) throw new InvalidOperationException("没有法兰几何（解析 FlangePlate 或 .3dm 的判据几何都没有）");
                var dcr = DesignCurrent.Compute(platesJ, c.WallMm, c.Base, c.RampFromC, c.RampTargetC, c.RampRateKPerH,
                              segs.Length, c.SetpointC,
                              jj => jj < flanges.Length && flanges[jj] is { } f0 && f0.QGenW > 0 && f0.CurrentA > 0
                                    ? (f0.QGenW / (f0.CurrentA * f0.CurrentA), f0.TRootC) : null);
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
                    Actual = undet ? double.NaN : worstJ, Limit = SectionSizing.JCheckAPerMm2, LessIsBetter = true,
                    Ok = !undet && worstJ < SectionSizing.JCheckAPerMm2, Undetermined = undet, Where = where,
                    Note = dcr.Describe()
                         + "　截面 J = 设计电流 ÷ 必经截面积（舌片各处含开孔／舌盘交界弦／孔缘环与各级环含槽带，圆盘按整圈），"
                         + "闭式、与网格无关；按 J=10 定尺寸，终验全体 < 11。"
                         + (!undet && worstJ < SectionSizing.JCheckAPerMm2 ? "" : NextAction.SectionJHigh)
                });
            }
            catch (Exception ex)
            {
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.SectionJ, Unit = "A/mm²", Kind = CheckKind.HardSafety,
                    Actual = double.NaN, Limit = SectionSizing.JCheckAPerMm2, LessIsBetter = true,
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
            Name = "管 J", Unit = "A/mm²", Kind = CheckKind.HardSafety,
            Actual = worstJt.TubeJAPerMm2, Limit = c.Base.TubeJAllowAPerMm2, Where = worstJt.Name,
            Ok = worstJt.TubeJAPerMm2 <= c.Base.TubeJAllowAPerMm2,
            Note = "限值来源：用户 2026-08-15 现场（一般 15；管壁 0.6 时 12 是极限）。" +
                   (c.Base.LossScale == 1.0
                ? "⚠ 散热未标定，本值系统性偏高（§4.2l）⇒ 判定偏保守"
                : $"散热已按 LossScale={c.Base.LossScale:0.000} 标定") +
                   (worstJt.TubeJAPerMm2 > c.Base.TubeJAllowAPerMm2 ? NextAction.TubeJHigh : "")
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
            c.FlangePlates is { Length: > 0 } ? c.FlangePlates : c.GeomForJudge,
            c.Base.BusbarClampLengthMm, c.FreeTabMinMm));

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
            var plates = c.FlangePlates is { Length: > 0 } ? c.FlangePlates : c.GeomForJudge;
            if (flanges.Length > 0 && plates is { Length: > 0 })
            {
                // 最不利的一片 = 发热最大那片（dP/dT ∝ 发热）
                int wj = 0;
                for (int j = 1; j < flanges.Length; j++)
                    if (flanges[j].QGenW > flanges[wj].QGenW) wj = j;
                var fw = flanges[wj];
                var pl = plates[Math.Min(wj, plates.Length - 1)];

                // 盘/舌面积按**切点**分 —— 与 DesignScreen.Extract 同一个口径，不另立标准
                var mesh = FlangeMesher.Build(pl, 0, c.MeshFineMm, c.MeshCoarseMm,
                                              c.MeshFineRadiusMm, c.Base.BusbarClampLengthMm,
                                              c.MeshInnerMm, c.MeshInnerRadiusMm);
                var sf = DesignScreen.Extract(mesh, 1000.0, 1050.0, pl.Tangent().X);
                double tThick = double.IsNaN(pl.TabThicknessMm) ? pl.ThicknessMm : pl.TabThicknessMm;

                // ⚠ 评估温度取**管根温度**，不取片上最高温：FlangeStability 的护栏写明
                //   发散几何上片温会跑到几千度，拿那个温度判出来的全是垃圾。
                var st = FlangeStability.Check(
                    c.Base, fw.QGenW, fw.TRootC,
                    sf.DiscAreaMm2, sf.TabAreaMm2, c.Base.FlangeInsulThickMm,
                    2 * pl.TabEndHalfWidthMm * tThick, Math.Abs(pl.TabEndXMm),
                    2 * Math.PI * pl.HoleRadiusMm * pl.ThicknessMm,
                    pl.DiscRadiusMm - pl.HoleRadiusMm,
                    pl.TabInsulThickMm);
                checks.Add(new ConstraintOut
                {
                    Name = LineResult.Key.FlangeStab, Unit = "×", Kind = CheckKind.Reference,
                    Actual = st.Undetermined ? double.NaN : st.Margin, Limit = 1.0,
                    LessIsBetter = false, Ok = st.Stable, Undetermined = st.Undetermined,
                    Where = fw.Name,
                    Note = "dQ_散热/dT ÷ dP_发热/dT，**须 > 1**；< 1 即正反馈失控（保温过头那条路）。"
                         + (st.Undetermined ? "　" + st.Note
                            : $"　散热侧 表面 {st.DSurfDT:0.000} + 夹持 {st.DClampDT:0.000}"
                              + $" + 管孔 {st.DTubeDT:0.000} = {st.DLossDT:0.000} W/K，"
                              + $"发热侧 {st.DGenDT:0.000} W/K")
                         + "　⚠ 现为**参考量**：限 1.0 是精确物理，但跨几何的量级还没攒够，"
                         + "攒够再升为硬判据（管 J 当年也是这么升上去的）。"
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
                try
                {
                    double shareF = fw.Shared ? Math.Sqrt(3.0) : 1.0;
                    double iSeg = fw.CurrentA / Math.Max(1e-9, shareF);
                    double rRef = fw.QGenW / Math.Max(1e-9, fw.CurrentA * fw.CurrentA);
                    double insulX = pl.InsulBoundaryXResolved;
                    double aIns = 0, aBare = 0;
                    for (int k = 0; k < mesh.CellCount; k++)
                        if (mesh.Centroid[k].X >= insulX) aIns += mesh.Area[k]; else aBare += mesh.Area[k];
                    double holeR = pl.HoleRadiusMm;
                    double tubeAreaMm2 = Math.PI * ((holeR * holeR)
                                       - (holeR - c.WallMm) * (holeR - c.WallMm));
                    var gRamp = new RampTwoNode.Inputs
                    {
                        WallMm = c.WallMm,
                        FlangeMassG = mesh.VolumeMm3 * Materials.PtDensity * 1e-6,
                        FlangeAreaInsulMm2 = aIns, FlangeAreaBareMm2 = aBare,
                        FlangeResistanceRefOhm = rRef, FlangeRefTempC = fw.TRootC,
                        HoleRadiusMm = holeR,
                        PlateEqOuterRadiusMm = Math.Sqrt(mesh.TotalArea / Math.PI + holeR * holeR),
                        FlangeThickMm = mesh.VolumeMm3 / Math.Max(1e-9, mesh.TotalArea),
                        DesignCurrentA = iSeg,
                        MaxCurrentA = c.Base.TubeJAllowAPerMm2 * tubeAreaMm2,
                        FromC = c.RampFromC, TargetC = c.RampTargetC,
                        RampRateKPerH = c.RampRateKPerH,
                        MaxHours = (c.RampTargetC - c.RampFromC)
                                   / Math.Max(0.1, c.RampRateKPerH) * 1.4,
                        SharedFactor = shareF,
                        Mode = RampControl.TemperatureRamp,
                    };
                    var rt = RampTwoNode.Solve(c.Base, gRamp);
                    checks.Add(new ConstraintOut
                    {
                        Name = LineResult.Key.RampField, Unit = "K", Kind = CheckKind.Reference,
                        Actual = rt.MaxFlangeMinusTubeK, Limit = 215.0,
                        LessIsBetter = true, Ok = true, Where = fw.Name,
                        Note = $"现场升温方式（温控 {c.RampRateKPerH:0} K/h、空管）下"
                             + $"「法兰温度 − 管温」的**全程最大值**，出现在 "
                             + $"{rt.TimeAtMaxDeltaS / 3600.0:0.0} h、当时管温 {rt.TTubeAtMaxDeltaC:0} °C；"
                             + $"法兰峰值 {rt.TFlangePeakC:0} °C（铂熔点 {Materials.PtMeltC:0}）；"
                             + $"电流 {rt.CurrentStartA:0}→{rt.CurrentEndA:0} A"
                             + (rt.CurrentClipped ? "，**被二次侧上限截住 ⇒ 跟不上设定速率**" : "")
                             + (rt.FlangeMelts ? "　★★ **法兰在升温期熔化**" : "")
                             + "。★ 限值 215 是**现役基准**（用户 2026-08-25 确认「比较符合现状」），"
                             + "**不是通过线** —— 这一条现为参考量，"
                             + "要升硬判据得先有限值的出处（同「热稳定」当初的路子）。"
                             + " ⚠ 判据 ① 用的 RampSolver 把法兰并进管子当同一个温度，"
                             + "「法兰比管热」在那个模型里结构性地看不见；本条用两节点模型补上。"
                    });
                }
                catch (Exception ex)
                {
                    checks.Add(new ConstraintOut
                    {
                        Name = LineResult.Key.RampField, Unit = "K", Kind = CheckKind.Reference,
                        Actual = double.NaN, Limit = 215.0, LessIsBetter = true,
                        Ok = true, Undetermined = true, Where = "—",
                        Note = "★ **算不出来**：" + ex.Message
                             + "　（参考量，不参与 AllOk；但算不出来就该说，不能装作没有这一条）"
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
                bool anyUnknownPlate = flanges.Any(f => double.IsNaN(f.LocalStabMargin));
                var wl = anyUnknownPlate ? null
                       : flanges.OrderBy(f => f.LocalStabMargin).FirstOrDefault();
                checks.Add(new ConstraintOut
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
                         + "　⚠ 现为参考量，同上。"
                });
            }
        }

        // ── 现场验证点：玻璃温降。这是全模型唯一一个拿实测校准的量，必须始终露出来。
        checks.Add(new ConstraintOut
        {
            Name = "· 玻璃温降 vs 实测", Unit = "K", Kind = CheckKind.Reference, Ok = true,
            Actual = res.GlassDropModelK, Limit = res.GlassDropMeasuredK, Where = "整线",
            Note = $"偏差 {res.GlassDropModelK - res.GlassDropMeasuredK:+0.0;-0.0} K"
        });

        return checks.ToArray();
    }
}
