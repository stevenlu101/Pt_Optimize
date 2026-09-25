using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 壳上的稳态温度场，有限体积、单元形心未知量：
///
///   ∇·(k t ∇T) + q_v·t − 2·q″(T) = 0
///
/// 与 <see cref="PlateThermal2D"/> 同一物理，区别只在**不认识网格结构** ——
/// 只用 <see cref="ShellMesh"/> 的面积、面长、形心间距与邻居，
/// 故变步长网格、开槽/阶梯任意形状、将来的三维装配都能直接用。
///
/// 单位统一到 mm 制：k [W/(mm·K)]、q″ [W/mm²]、面积 [mm²]、厚度 [mm]。
/// 表面热流两面各一份，故源项里是 **2·q″**。
/// </summary>
public sealed class ShellThermalResult
{
    /// <summary>本次用的对流特征长度 m（由网格包围盒最大跨度算，不再写死 0.05）。</summary>
    public double CharLenM;

    /// <summary>
    /// 铜排热阻折算成的**等效舌片长度** mm（`k·A_截面/G`）。定温边界下为 0。
    /// 它是「铜排是部分锚点」这件事的量化：既不是理想热沉，也不是绝热。
    /// </summary>
    public double BusEquivLenMm;

    public double[] T = Array.Empty<double>();
    public double TMaxC, TMinC;
    /// <summary>★ R48（2026-09-15，Opus 5）：本次散热表的温度上限 °C（超界即钳住）；TMaxC 超过它 ⇒ 场不可信，LineRunner 判本片「场没解到位」。</summary>
    public double LossTableHiC = double.NaN;
    public double QGenW;          // 整片焦耳热
    public double QLossW;         // 整片表面散热
    public double QFromTubeW;     // 由管孔流入法兰的净热（>0 = 从管子抽热）

    /// <summary>
    /// ★ R48 诊断（2026-09-13，Opus 5 加）：**被钉成管温的那圈孔单元，自身的焦耳热与表面散热**。
    ///
    /// 为什么要量它：<see cref="QFromTubeW"/> 是「流出定温孔单元的净导热」，而那圈单元自己也通着电、
    /// 也在散热 —— 按现行口径它们被整个排除在能量账之外（见下面 Excluded 那一行的注释：
    /// 「定温单元自身的产热与散热由各自的边界吸收」），等于把这部分产热记成了管子吸收掉。
    /// 这圈单元的**总面积正比于孔周长 × 格子尺寸**，网格加密一倍就减半
    /// ⇒ 抽热带着一个**正比于网格尺寸的系统误差**（不是随机噪声）。
    ///
    /// 物理上真正该报的是「通过孔边界那条线的热流」。用这两个诊断量可以把它估出来：
    ///   <c>抽热（与网格无关的口径） ≈ QFromTubeW + QHoleCellGenW − QHoleCellLossW</c>
    /// 本次只**量**不改口径：先验证这个修正量随加密收敛，再谈要不要改 QFromTubeW 的定义。
    /// ⚠ 2026-09-23（F6 审查后补注，findings #38；只改注释，算式与数一个没动）：上面这段写于整格定温口径（holeFaceDirichlet = false），
    ///   **在面上定温口径（holeFaceDirichlet = true，生产缺省，2026-09-13 起）下已不成立**：
    ///   · 孔格不再被钉成管温，是普通自由格，自身的发热与散热已经入能量账（Excluded 只排除 holeCell 且 !holeFaceDirichlet 的格）；
    ///   · QFromTubeW 直接就是穿过孔边界面的热流 Σ gHole·(管温 − T_格心)，没有「那圈单元被当成管子吸收」这回事；
    ///   ⇒ 上面的估算式 QFromTubeW + QHoleCellGenW − QHoleCellLossW 只适用于整格定温口径，面上口径下照用就是把孔格的发热重复计一次。
    ///   面上口径下这两个量只是「带孔面的那圈格」自身发热／散热的诊断，不进任何账。
    ///   F6a（2026-09-23，电流场孔面上定电位）另改了孔格的 J（孔格不再是电流场的电极格），这两个诊断量随之变，变因记 F6a；它们不进任何门与判据。
    /// </summary>
    public double QHoleCellGenW, QHoleCellLossW;
    /// <summary>带孔边界面的格数与总面积 mm²（诊断；整格定温口径下即被钉成管温的孔单元，面上口径下只是那圈自由格 —— 见上一条 ⚠）。</summary>
    public int HoleCellCount; public double HoleCellAreaMm2;
    /// <summary>
    /// 由舌片末端流进铜排的净热 W（>0 = 铜排在带走热）。**与管孔那一项同法直接算**，
    /// 不用能量恒等式反推 —— 否则「对账」就成了循环论证，验证不了任何东西。
    /// </summary>
    public double QToClampW;
    /// <summary>
    /// 能量闭合残差 W：Σ(发热−散热) + 管孔净流入 − 铜排带走。
    /// 应接近 0；显著非零说明场解没收敛或边界处理有漏。
    /// </summary>
    public double EnergyResidualW;
    /// <summary>
    /// ★ R48 G2 复审（2026-09-15 Opus 5；审查意见「节点口径前后不一致」「探针壳解列与集总列不同口径」）：**逐格**的焦耳热 W、表面散热 W（两面），
    /// 与这一格是不是被边界钉住、不入本片能量账（<see cref="BoundaryCell"/>，与 <see cref="EnergyResidualW"/> 排除的格同一个判定）。
    /// 为什么要逐格：升温两节点的法兰节点要在稳态场上**逐项**标定（发热、表面散热、流进铜排、管孔），节点格的发热与散热只能由本热解给 ——
    /// 散热表按格混合了圆盘／舌片两种保温，调用方重算就是手抄配方。求和顺序与 <see cref="QGenW"/>／<see cref="QLossW"/> 相同，那两个数逐位不变。
    /// 求解没走到汇总（提前返回）时为空数组，调用方据此判「没有逐格账」。
    /// </summary>
    public double[] CellGenW = Array.Empty<double>(), CellLossW = Array.Empty<double>();
    /// <summary>★ R48 G2 复审（2026-09-15 Opus 5）：被边界钉住、不入本片能量账的格（定温压接格；孔边不按面定温时的孔格）。见 <see cref="CellGenW"/>。</summary>
    public bool[] BoundaryCell = Array.Empty<bool>();
    public double PhiOverall;     // 自给率 = 自身发热 / 自身散热
    public double TTabEndMeanC;   // 舌片末端平均温度（铜排压接点）

    /// <summary>
    /// ★ 分区能量账（圆盘 / 舌片），按 <c>x &lt; 分界</c> 判为舌片（双舌用 |x| &gt; |分界|）。
    ///
    /// 为什么必须分区：整片只给一个「发热 &lt; 散热」的结论，指不出**哪一段**亏，
    /// 而两段的杠杆完全相反 —— 圆盘亏要缩盘径/包保温，舌片亏要窄舌加厚（J 不变、散热减半）。
    /// §4.3b 曾据闭式断言「窄舌反而更差」，那是把圆盘与舌片的散热混在一个 ΣR 里算的结果；
    /// 只有把两区分开量，才知道该动谁。全部**只统计自由单元**，口径与
    /// <see cref="EnergyResidualW"/> 一致（孔单元/舌端单元是定温边界，其收支归边界）。
    /// </summary>
    public double QGenDiscW, QLossDiscW, QGenTabW, QLossTabW;
    public double AreaDiscMm2, AreaTabMm2;
    /// <summary>面积加权平均温度 °C</summary>
    public double TDiscMeanC, TTabMeanC;
    /// <summary>
    /// 分区**峰值**温度 °C。判据②「法兰温度 ≤ 管温」要看的是 <see cref="TDiscMaxC"/>：
    /// 那是**贴着管子那一段**的温度，它才决定热往不往管里灌。
    /// 整片的 <see cref="TMaxC"/> 在新方案里落在**包了保温的舌片**上（离管子几十毫米、
    /// 中间隔着圆盘），拿它去跟管根比是在比两个不相干的位置。
    /// 两个都留着、都报出来，不要用一个替换另一个。
    /// </summary>
    public double TDiscMaxC, TTabMaxC;
    /// <summary>
    /// ★ 圆盘区峰值**落在哪** —— 判据 ②″ 只给一个差值，分不清病灶：
    /// 峰在**管孔上**（r≈管外径）意味着「盘峰 = 管根」的恒等式在噪声上下摆，
    /// 治它要动管侧（抽热 D、管截面）；峰在**轮毂/舌根**（r 较大、J 不为零）
    /// 才是法兰自身发热顶起来的局部尖峰，治它要动法兰几何。
    /// 两者要用完全不同的旋钮，只看差值必然误诊。
    /// </summary>
    /// <summary>
    /// **局部热稳定**的最小裕度（J_stab ÷ J_实际）与它落在哪。&lt; 1 即该点会自行升温直到烧断。
    ///
    /// ⚠ 不能拿「最热那一格」代替：实测现役档上盘温峰落在外缘，那里
    /// **电流密度接近 0** ⇒ 裕度算出 +∞，看起来无限安全，其实什么都没验。
    /// 判据要的是「最不稳定」的点，不是「最热」的点，两者不是一回事。
    /// </summary>
    public double LocalStabMargin = double.NaN, LocalStabRMm = double.NaN,
                  LocalStabTempC = double.NaN, LocalStabJAPerMm2 = double.NaN,
                  LocalStabThickMm = double.NaN, LocalStabLatLenMm = double.NaN;
    public bool LocalStabOnTab;

    public double DiscMaxXMm = double.NaN, DiscMaxZMm = double.NaN,
                  DiscMaxRMm = double.NaN, DiscMaxJAPerMm2 = double.NaN,
                  DiscMaxThickMm = double.NaN;

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：**舌片区峰值落在哪** —— 圆盘区那边一直有，舌片区一直没有。
    ///
    /// 为什么补：判据 ②″ 只看圆盘区，**有意**把舌片排除（舌片离管子几十毫米，拿它跟管根比没意义）。
    /// 但分区是按 <c>x &lt; 切点</c> 划的，而舌半宽 = 盘半径时切点落在 x = 0 ——
    /// 于是**半圈孔边（x&lt;0）也被划进舌片区**，那可不是「离管子几十毫米」。
    /// 实测（管壁 0.8，09-13 解出的那一点，导航网格）：圆盘区 −0.2～−0.5 K，
    /// 舌片区 **+8.4～+16.6 K**，而 ②″ 的限值是 5 K。
    /// ⇒ 「舌片区那个峰到底在孔边还是在远处舌片上」决定了这是不是漏判，所以必须记下位置。
    /// </summary>
    public double TabMaxXMm = double.NaN, TabMaxZMm = double.NaN,
                  TabMaxRMm = double.NaN, TabMaxJAPerMm2 = double.NaN,
                  TabMaxThickMm = double.NaN;

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：圆盘区/舌片区**这一次是按哪条规则分的**。
    /// 新口径按半径（r ≤ 盘半径）；拿不到盘半径才退回旧的按切点 x 切一刀。
    /// **退回必须看得见** —— 旧口径在「舌半宽 = 盘半径」时只盖住半个零件，
    /// 而判据 ②″（圆盘区最高温）就建在这个分区上。判据的 Note 会把这句原样带出去。
    /// </summary>
    public string DiscZoneRule = "";

    /// <summary>R48（2026-09-14，Opus 5）：这一次保温按哪条规则划（按半径／按 x）。随判据说明一起带出去，退回旧口径必须看得见。</summary>
    public string InsulRule = "";
    public int Iterations;
    /// <summary>
    /// ⚠ 这是**步长**（Picard 一轮里最大的温度改动 K），**不是残差**。
    /// 名字保留是为了不动既有读者；真正的残差见 <see cref="ResidualW"/>。
    /// 步长小 ≠ 解对：收缩因子 g 时，到不动点的距离 ≈ 步长 / (1 − g)。
    /// </summary>
    public double Residual;

    /// <summary>诊断：内层线性解最后一次用了多少 CG 迭代（0 = 没走 CG 支）。</summary>
    public int InnerIterations;

    /// <summary>
    /// ★★ **真残差**：稳态能量方程逐格的不闭合量，单位 **W**（2026-08-29 补）。
    ///
    /// <code>
    ///   r_i = Σ_k g_k (T_c − T_i) + q_v·A − 2·q_s(T_i)·A + G_铜排 (T_冷端 − T_i)
    /// </code>
    ///
    /// ⚠ 用的是**真**散热 q_s(T_i)，不是迭代里为稳定而做的线性化 ——
    ///   线性化只准出现在「怎么走」里，不准出现在「走到没有」里。
    ///
    /// ══ 为什么补这个
    ///
    /// 收敛判据原本只有 <c>maxd &lt; tol</c>，而 maxd 是**步长**。这与 2026-08-28
    /// 在基线循环里抓到的是同一族错误（那次少乘了 25 倍的不动点放大，
    /// 直接推翻了「历史设计 ③ 不合格」这个结论），也与同月 ShellCurrent 那次同族。
    /// **同一个病在本仓库出现三次，前两次都是靠实测撞出来的。**
    ///
    /// ⚠ 本次先**只测不判**：算出来、报出来、留在结果里，但不改停机条件。
    ///   改停机条件会动到全部回归基准，那必须先有数据支撑 —— 见 <see cref="ResidualRel"/>。
    /// </summary>
    public double ResidualW;

    /// <summary>
    /// 真残差的**相对**口径：<c>ResidualW / 自由格总焦耳热</c>（与 ShellCurrent 同形）。
    /// 这才是能定阈值的那个数 —— 绝对瓦数随算例大小变，相对值不变。
    /// </summary>
    public double ResidualRel;

    /// <summary>步长判据的结论。⚠ 它**不**保证解到位，见 <see cref="ResidualW"/>。</summary>
    public bool Converged;

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5；数值把关人第十三轮）：这次热解**实际生效**的热解配方（结构化指纹）。由 <see cref="ShellThermal.Solve"/> 填，
    /// 门拿 <see cref="ThermalRecipe.Rule"/> 与 <see cref="ShellThermal.ProductionThermalRule"/> 比；证据文件头原样印 <see cref="ThermalRecipe.Describe"/>。
    /// </summary>
    public ThermalRecipe Recipe = new();

    /// <summary>场里有金属越过铂熔点 ⇒ **该解不存在**，不管能量账闭合得多好。</summary>
    public bool OverMelt => TMaxC > Materials.PtMeltC;
    /// <summary>场里有金属越过电阻率拟合覆盖区（1500 °C）⇒ 数值是外推的，不可引用。</summary>
    public bool OverFitRange => TMaxC > Materials.PtFitMaxC;
}

/// <summary>R48（2026-09-15，Opus 5）：保温分界圆上的格子按份额混合两种保温的散热 —— 这一次实际怎样。</summary>
public enum InsulBlendState
{
    /// <summary>保温按半径划、网格带厚度场 ⇒ 分界格按有料面积份额混合（生产配方 ②）。</summary>
    On,
    /// <summary>保温按半径划，但网格没有厚度场 ⇒ 分界格按形心整格归一边。</summary>
    OffNoField,
    /// <summary>保温按 x 划（指定分界或没拿到保温半径）⇒ 没有分界圆，谈不上混合。</summary>
    NotByRadius,
}

/// <summary>R48（2026-09-15，Opus 5）：舌端（铜排压接）边界这一次按哪种方式施加。见 ShellThermal.Solve 里「舌端边界的三种模式」。</summary>
public enum ClampBoundaryMode
{
    /// <summary>定温：压接格钉在夹持温度（BusbarClampTempC ≥ 0 且没给铜排热导）。</summary>
    Fixed,
    /// <summary>热导：铜排热导 G 按面积分摊到压接格（BusbarConductanceWPerK ≥ 0）。</summary>
    Conductance,
    /// <summary>自由：两样都没给，舌端是自然边界。</summary>
    Free,
}

/// <summary>
/// ★★ R48（2026-09-15，Opus 5；常驻数值把关人第十三、十四轮）：一次壳热解**实际生效**的热解配方（结构化指纹）。
///   · <see cref="InsulBlend"/>：分界格混合开没开（见 <see cref="InsulBlendState"/>）；
///   · <see cref="LossTableLoC"/>／<see cref="LossTableHiC"/>／<see cref="LossTableNodes"/>：表面散热表的温度区间与节点数；
///   · <see cref="HoleFaceDirichlet"/>：管温施加在孔边界面上（true，生产）还是把孔边整格钉住（false，老口径）；
///     <see cref="HoleFaceCount"/>：网格上的孔边界面数（管温从这些面进来）。
///     ★ 2026-09-15 Opus 5（审查意见 minor：网格上一个孔边界面都没有时，原先记 HoleFaceDirichlet = true、规则等于生产，指纹却说「管温施加在孔边界面上」，
///       而那时管温根本没施加 —— 图纸孔半径与管外半径差超过孔边判定带 3 mm 就会这样）：改为**没有孔边界面 ⇒ HoleFaceDirichlet = false**，
///       规则另带 <see cref="ThermalRecipeRule.HasHoleBoundary"/>（孔边界面数 &gt; 0），生产常量要求为真 ⇒ 行为门与 LineResult.RecipeDeviations 直接看得见；
///   · <see cref="ClampMode"/>：舌端边界定温／热导／自由（算例的选择，不进规则）；<see cref="ClampFullFace"/>：压接格取整面接触（网格 ClampCell）还是只取外圈。
///   · <see cref="ClampFaceDirichlet"/>（2026-09-15 Opus 5（合并）补 F 的配方 ⑤）：压接边界施加在压接面上（true，生产）还是按压接格形心整格（false）。
///     定温模式：在求解里量 —— 面导度循环里真走了「只取自由格一侧半距」那一支的压接面数（<see cref="ClampFaceCount"/>）&gt; 0；
///     热导模式与自由端：热场里**没有**定温边界（见 ShellMesh.ClampFaceDirichlet 注释「三种舌端模式」），面上还是形心只经电流场进来，
///     热解里量不到 ⇒ 如实记网格的口径（ShellMesh.ClampFaceCount &gt; 0，与电流场施加面上定电位同一个集合与判定），描述文字写明「经电流场」。
/// ★ 2026-09-15 Opus 5（审查意见 minor：原先 Solve 的「逐项核对」拿 RecipeFor 里的同一个式子互比，改坏生效分支照样通过）——
///   <see cref="ShellThermal.Solve"/> 填的这一份改为**在求解里数出来**：分界格混合看保温分区循环里真按半径划、真因为没有厚度场跳过混合的格数，
///   孔边看真钉住的孔边格数与孔边界面导度真有没有非零，压接整面看真并进压接格的格数，表看真建出来的那张表；
///   <see cref="ShellThermal.RecipeFor"/> 只按输入**预判**（探针跑前写证据头用），Solve 拿两份比，对不上当场炸。
///   <see cref="ClampMode"/> 仍按输入分支记（算例的选择，不进规则）。
/// </summary>
public sealed class ThermalRecipe
{
    public InsulBlendState InsulBlend { get; init; } = InsulBlendState.NotByRadius;
    /// <summary>
    /// 2026-09-15 Opus 5：本次**真按有料面积份额混合了**的分界格数（0 &lt; 份额 &lt; 1 的格）；−1 = 没解、只是预判（<see cref="ShellThermal.RecipeFor"/>）。
    /// 与算例几何有关，不进规则、不进 <see cref="Describe"/>（整线汇总按描述去重）；门拿它确认混合真的发生了。
    /// </summary>
    public int InsulBlendCells { get; init; } = -1;
    public double LossTableLoC { get; init; } = double.NaN;
    public double LossTableHiC { get; init; } = double.NaN;
    public int LossTableNodes { get; init; }
    /// <summary>管温施加在孔边界面上（生产）。2026-09-15 Opus 5：网格上没有孔边界面时为 false（那时管温没有施加，谈不上施加在哪）。</summary>
    public bool HoleFaceDirichlet { get; init; }
    /// <summary>2026-09-15 Opus 5：网格上的孔边界面数（边界面且带管孔标签；Solve 在组装边界时数，RecipeFor 按同一个定义数）。与网格尺寸有关，不进规则；规则只看它是否 &gt; 0。</summary>
    public int HoleFaceCount { get; init; }
    public ClampBoundaryMode ClampMode { get; init; } = ClampBoundaryMode.Free;
    public bool ClampFullFace { get; init; }
    /// <summary>2026-09-15 Opus 5（合并）：压接边界施加在压接面上（F 配方 ⑤）；false = 形心整格。定温模式在求解里量，其余模式记网格口径，见类注释。</summary>
    public bool ClampFaceDirichlet { get; init; }
    /// <summary>
    /// ★ 2026-09-23（F6d）：局部热稳定的管孔锚点取**孔圆本身**（r = ShellMesh.HoleRadiusMm）；false = 老口径「孔格形心的最小半径」（随格集合跳）或没有孔边界面。
    /// Solve 按真走了哪一支记，RecipeFor 按输入预判，两份对不上当场炸。
    /// </summary>
    public bool HoleAnchorOnCircle { get; init; }
    /// <summary>
    /// 2026-09-15 Opus 5（合并）：压接面数 —— 定温模式 = 求解里真按半距施加了面导度的压接面数；其余模式 = 网格上的压接面数（ShellMesh.ClampFaceCount）；
    /// −1 = 只是预判（<see cref="ShellThermal.RecipeFor"/>）。与网格尺寸有关，不进规则。
    /// </summary>
    public int ClampFaceCount { get; init; } = -1;

    /// <summary>节点数是不是按 <see cref="ShellThermal.LossTableNodes"/> 这条规则（约 22 K 一个节点）定的。</summary>
    public bool LossTableNodesByRule => LossTableNodes == ShellThermal.LossTableNodes(LossTableLoC, LossTableHiC);

    /// <summary>与算例无关的规则部分 —— 生产配方常量就是这个类型，门用 == 比（舌端定温方式是算例的选择，不进规则）。</summary>
    public ThermalRecipeRule Rule => new(InsulBlend, LossTableHiC, LossTableNodesByRule, HoleFaceCount > 0, HoleFaceDirichlet, ClampFullFace, ClampFaceDirichlet, HoleAnchorOnCircle);

    public string Describe()
        => "分界格混合 " + (InsulBlend switch { InsulBlendState.On => "开", InsulBlendState.OffNoField => "关（网格没有厚度场）", _ => "不适用（保温按 x 划）" })
         + $"；表面散热表 {LossTableLoC:0.#}～{LossTableHiC:0.#} °C、{LossTableNodes} 节点{(LossTableNodesByRule ? "" : "（不是按约 22 K 一个节点的规则）")}"
         + $"；管温{(HoleFaceCount <= 0 ? "没有施加（网格上没有孔边界面）" : HoleFaceDirichlet ? "施加在孔边界面上" : "钉住孔边整格")}"
         + "；舌端 " + (ClampMode switch { ClampBoundaryMode.Fixed => "定温", ClampBoundaryMode.Conductance => "按铜排热导", _ => "自由" })
         + (ClampFullFace ? "（整个压接面）" : "（只取压接外缘一圈）")
         // 2026-09-15 Opus 5（合并）：F 配方 ⑤；非定温模式热场没有定温边界，写明是经电流场
         + "；压接边界" + (ClampFaceDirichlet ? "施加在压接面上" : "按压接格形心整格")
         + (ClampMode == ClampBoundaryMode.Fixed ? "" : "（热场无定温边界，经电流场）")
         + "；局部热稳定的管孔锚点" + (HoleAnchorOnCircle ? "在孔圆上" : "按孔格形心（或没有孔边界面）");   // 2026-09-23 F6d
}

/// <summary>R48（2026-09-15，Opus 5）：热解配方里与算例无关的规则部分（<see cref="ThermalRecipe.Rule"/>）。</summary>
/// <remarks>2026-09-15 Opus 5：加 <see cref="HasHoleBoundary"/>（网格上有孔边界面，管温才施加得上）；有孔边界面时描述文字与原先逐字相同。
/// 2026-09-15 Opus 5（合并）：加 <see cref="ClampFaceDirichlet"/>（F 配方 ⑤ 压接面上定温 vs 形心整格），描述文字末尾多一段。
/// 2026-09-23（F6d）：加 <see cref="HoleAnchorOnCircle"/>（局部热稳定的管孔锚点在孔圆上 vs 孔格形心），描述文字末尾多一段。</remarks>
public readonly record struct ThermalRecipeRule(InsulBlendState InsulBlend, double LossTableHiC, bool LossTableNodesByRule, bool HasHoleBoundary, bool HoleFaceDirichlet, bool ClampFullFace, bool ClampFaceDirichlet,
                                                bool HoleAnchorOnCircle)
{
    public string Describe()
        => "分界格混合 " + (InsulBlend switch { InsulBlendState.On => "开", InsulBlendState.OffNoField => "关（网格没有厚度场）", _ => "不适用（保温按 x 划）" })
         + $"；表面散热表上限 {LossTableHiC:0.#} °C、节点{(LossTableNodesByRule ? "按约 22 K 一个" : "不按规则")}"
         + $"；管温{(!HasHoleBoundary ? "没有施加（网格上没有孔边界面）" : HoleFaceDirichlet ? "施加在孔边界面上" : "钉住孔边整格")}；压接格取{(ClampFullFace ? "整个压接面" : "压接外缘一圈")}"
         + $"；压接边界{(ClampFaceDirichlet ? "施加在压接面上" : "按压接格形心整格")}"   // 2026-09-15 Opus 5（合并）
         + $"；局部热稳定的管孔锚点{(HoleAnchorOnCircle ? "在孔圆上" : "按孔格形心")}";         // 2026-09-23 F6d
}

/// <summary>R48（2026-09-15，Opus 5）：一次壳热解用的三张表面热流表（<see cref="ShellThermal.SurfaceLossTablesFor"/>）。没包保温的面与裸面是同一个实例。</summary>
public sealed class SurfaceLossTables
{
    public LossTable Bare = null!, Disc = null!, Tab = null!;
    public bool DiscInsulated, TabInsulated;
}

public static class ShellThermal
{
    /// <summary>
    /// ★★ R48 生产热解配方常量（2026-09-15，Opus 5；数值把关人第十三轮）：生产链上每一片热解的 <see cref="ThermalRecipe.Rule"/> 都必须等于它 ——
    /// 分界格混合开、散热表上限 = 铂熔点且节点按约 22 K 规则、网格上有孔边界面且管温施加在孔边界面上、压接格取整面接触。
    /// （2026-09-15 Opus 5：加「有孔边界面」一项，见 <see cref="ThermalRecipe.HoleFaceCount"/>）
    /// ★ 2026-09-15 Opus 5（合并）：G1 写于 F 之前 —— 按合并后的真实生产配方加一项「压接边界施加在压接面上」（F 配方 ⑤，ShellMesh.ClampFaceDirichlet 缺省 true）。
    /// </summary>
    public static readonly ThermalRecipeRule ProductionThermalRule = new(
        InsulBlend: InsulBlendState.On, LossTableHiC: LossTableHiC, LossTableNodesByRule: true, HasHoleBoundary: true, HoleFaceDirichlet: true, ClampFullFace: true,
        ClampFaceDirichlet: true, HoleAnchorOnCircle: true);   // 2026-09-23 F6d

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5）：三张法兰表面热流表（裸面／圆盘保温面／舌保温面）W/mm² —— 自 <see cref="Solve"/> 原样提出（算式逐字未改），
    /// 门（R48ClampRecipeTests 门 d 推容差）拿同一份比新旧表，不手抄配方。<paramref name="hiC"/> NaN ⇒ <see cref="LossTableHiC"/>；
    /// <paramref name="nodes"/> ≤ 0 ⇒ <see cref="LossTableNodes"/>(环境, 上限)。
    /// </summary>
    public static SurfaceLossTables SurfaceLossTablesFor(DesignInputs p, double tabInsulThickMm, double hiC = double.NaN, int nodes = 0)
    {
        double tHi = double.IsNaN(hiC) ? LossTableHiC : hiC;
        int nLossTab = nodes > 0 ? nodes : LossTableNodes(p.TAmbC, tHi);
        var bareTab = new LossTable(p.TAmbC, tHi, nLossTab,
            x => DesignScreen.PlateFluxWPerM2(p, x, 0.0) * 1e-6);
        // ★ 风速必须传进保温面（2026-08-28）：此前保温区走 airVelocity=0 的默认值，
        //   于是同一片法兰上裸露区吹得到风、保温区吹不到 —— 而默认圆盘正是包着的那一半。（R48 起由唯一配方负责传）
        bool discInsul = DesignScreen.FlangeFaceInsulated(p.FlangeInsulThickMm);
        var insTab = discInsul
            ? new LossTable(p.TAmbC, tHi, nLossTab,
                x => DesignScreen.PlateFluxWPerM2(p, x, p.FlangeInsulThickMm) * 1e-6)
            : bareTab;

        // 舌片自己的保温（见 Solve 参数注释）。厚度 0 也不等于裸露 —— 裸露是铂表面 ε=0.18，
        // 而「包了 0 mm」在 PlateFlux 里走的是外覆材料 ε=0.45，两者差 2.5 倍。
        bool tabInsul = DesignScreen.FlangeFaceInsulated(tabInsulThickMm);   // = !NaN && ≥ 0.05（R48 2026-09-14 Opus 5：与圆盘同一判定）
        var tabInsTab = tabInsul
            ? new LossTable(p.TAmbC, tHi, nLossTab,
                x => DesignScreen.PlateFluxWPerM2(p, x, tabInsulThickMm) * 1e-6)
            : bareTab;
        return new SurfaceLossTables { Bare = bareTab, Disc = insTab, Tab = tabInsTab, DiscInsulated = discInsul, TabInsulated = tabInsul };
    }

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5）：法兰表面散热表的温度上限 °C = 铂熔点。原「设定 + 200 K」会被共用片与厚保温格点超过并静默钳住（见 Solve 里的注释）。
    /// PlateThermal2D 同用这一份。
    /// </summary>
    public const double LossTableHiC = Materials.PtMeltC;

    /// <summary>散热表节点数：保持原来约 22 K 的节点间距（原 60 点铺 [环境, 设定+200]），至少 60。</summary>
    public static int LossTableNodes(double tLoC, double tHiC) => Math.Max(60, (int)Math.Ceiling((tHiC - tLoC) / 22.0) + 1);

    /// <summary>舌端（铜排压接段）边界的三种模式，见 <see cref="ClampBoundaryOf"/>。</summary>
    public enum ClampBoundary { Conductance, FixedTemp, Free }

    /// <summary>
    /// ★ R48 G2（2026-09-15 Opus 5）：舌端边界是哪一种的**唯一判定** —— 本文件的热解（busG／clamped 两个位）与
    /// 升温两节点的节点标定（RampTwoNode.CalibrateNode）共用这一份，免得「场按定温解、标定按热导读」各判各的。
    /// 规则与原来就地写的两行逐字相同：铜排热导 ≥ 0 ⇒ 热导；否则夹持温度 ≥ 0 ⇒ 定温；否则自由端。
    /// </summary>
    public static ClampBoundary ClampBoundaryOf(DesignInputs p)
        => p.BusbarConductanceWPerK >= 0 ? ClampBoundary.Conductance
         : p.BusbarClampTempC >= 0 ? ClampBoundary.FixedTemp
         : ClampBoundary.Free;

    /// <summary>
    /// 解温度场。
    /// 边界：<see cref="ShellMesh.TagHole"/> 定温 = 管根温度；
    /// <see cref="ShellMesh.TagTabEnd"/> 在 <c>BusbarClampTempC ≥ 0</c> 时定温，否则自由（自然边界）。
    /// </summary>
    /// <param name="jMagAPerMm2">各单元电流密度，来自 <see cref="ShellCurrent"/></param>
    /// <param name="tRootC">管根温度 °C（管孔处定温）</param>
    /// <param name="insulBoundaryX">保温分界 x：≥ 此值包纤维，其余裸露</param>
    /// <param name="symmetricInsul">
    /// 双舌片时置 true：改判 |x| ≤ |分界| 为保温区（两侧舌片都裸露）。
    /// ★ 为什么分区而不是全包：舌片离冷源远（约 45 mm），横向导热只有约 34 W/(m²·K)，
    ///   **表面散热是它抵抗局部热失稳的主要恢复力**；包保温会把允许电流密度
    ///   从 24.6 砍到 15.0 A/mm²（实算）。圆盘则不同 —— 它紧贴管子，
    ///   横向导热约 2400，表面项只占 1 %，包保温无害且能降低自给所需厚度。
    /// </param>
    /// <param name="tabBoundaryX">
    /// 圆盘/舌片的**几何**分界 x（用于分区能量账）。NaN = 取 <paramref name="insulBoundaryX"/>。
    /// 两者通常是同一个切点，但保温分界可以被单独挪（如「舌片也包保温」），
    /// 那时分区仍应按几何切点，否则分区账会跟着保温方案一起变，失去可比性。
    /// </param>
    /// <param name="tabInsulThickMm">
    /// ★ **舌片自己的保温厚度** mm。NaN 或 &lt;0.05 = 舌片裸露（原行为）。
    ///
    /// 为什么要把它从圆盘的 <see cref="DesignInputs.FlangeInsulThickMm"/> 里分出来：
    /// 总纲的自由度 ④ 明写「保温条件：管与法兰**分别**；哪些部位要保温、保多厚」，
    /// 而此前程序只有「以切点为界：圆盘包 / 舌片裸」这**一个二值开关**。
    /// 舌片裸露是全片最大的热漏（实测占端片散热的 90 % 以上），
    /// 它一裸就把端片推成净抽热、一全包又过冲成净倒灌 —— 中间必然存在一个零点。
    /// 给它一个连续厚度，「端片能不能自给」才从一道是非题变成一个可解的方程。
    /// </param>
    /// <summary>
    /// ★★★ **停机判据本身** —— 真残差的相对阈值。2026-08-30 起它不再只是事后校验。
    ///
    /// ══ 为什么换掉步长
    ///
    /// 步长小 ≠ 解到位。配对实测（`--thermcg`，0.8 档、1.0 mm 网格）把放大倍数量了出来：
    /// <code>
    ///   步长容差   停机残差(相对)   GS 与 CG 的场差
    ///   1e-4 K     ~2.5E-7          **0.132 K**
    ///   1e-6 K     ~2.5E-9          **0.00101 K**
    /// </code>
    /// 两条路收敛到**同一个不动点**（场差随容差线性下降，130 倍），
    /// 但步长 1e-4 K 只把位置钉到 **0.13 K** —— **不动点放大约 1300 倍**。
    /// 这与基线循环那次的 25 倍、与线性解那次，是同一族的第四次。
    ///
    /// ⚠ 0.13 K 有多要紧：网格无关复核判 ③ 用的容差是 **1.0 K** ——
    ///   也就是说**解算器自身的位置噪声占了那条容差的 13%**。
    ///   拿它去判「判据还随不随网格变」，等于用一把自己在抖的尺子量抖动。
    ///
    /// ══ 阈值怎么定的（实测标定，不是拍的）
    ///
    /// 上表给出 <c>场差 ≈ 5×10⁵ × 相对残差</c>。要把场钉到 **0.01 K**
    /// （= ③ 那条 1.0 K 容差的 1%，小到不会污染网格无关的判断）⇒ 残差 ≤ **2e-8**。
    ///
    /// ⚠ 代价：比原来的停机点紧约 12 倍。这正是把 GS 换成 CG 的理由 ——
    ///   GS 在 47 567 单元上连 1e-4 的**步长**都到不了（60000 轮撞上限），
    ///   而收紧只会让它更到不了。
    /// </summary>
    public const double ResidualRelTol = 2e-8;

    /// <summary>
    /// **退回旧的纯 Gauss–Seidel 扫描**（只为配对对照，默认关）。
    ///
    /// 与 <see cref="ShellCurrent"/> 同款：换解法这种事，**必须能把两条路各跑一次逐位对账**，
    /// 否则「快了」和「答案变了」分不开。2026-08-29 电位场那次就是靠它证明
    /// **CG 买到的是速度不是精度**（V 场差 ≤ 1.3e-5）。
    /// </summary>
    public static bool UseGaussSeidel;

    /// <summary>
    /// 步长判据的临时覆写（只给 `--thermcg` 做容差扫描用；&lt;=0 = 用调用方给的）。
    /// ⚠ 不许在交付路径上用它 —— 这是量「解算器精度地板随容差怎么走」的探针。
    /// </summary>
    public static double StepTolOverride;

    /// <param name="holeFaceDirichlet">
    /// ★★ R48（2026-09-13，Opus 5；用户拍板「按第一性原理，当然是动」）：**管温施加在孔边界面上**，默认开启。
    ///
    /// 旧口径（false）：把「有孔边界面的那**一整格**」钉成管温。等于把定温位置放在**形心**上，
    ///   而形心距真实孔边界半个格子 ⇒ 边界位置带 O(h) 误差；那圈格子自身的发热与散热还被当成管子吸收掉。
    /// 新口径（true）：管温施加在孔边界**面**上（形心到面正好是 <see cref="MeshFace.DistAB"/>），
    ///   抽热 = 通过那些面的热流；孔单元回到普通自由单元，自身发热与散热照常入账。
    ///
    /// 实测（管壁 0.8 片 0，管侧固定，正方形网格五档 2／1／0.5／0.25／0.125 mm）：
    ///   旧口径 7.449／7.493／8.495／9.499／10.067 W，外推真值 10.81
    ///   新口径 13.059／11.712／11.204／10.994／10.871 W，外推真值 10.70
    ///   两者**从相反方向逼近同一个值**（差 0.1 W）⇒ 交叉验证，实现没写错。
    ///   收敛阶都是一阶（差值比 0.566 / 0.582 —— 原本预期新口径能到二阶，**这一点被实测证伪**），
    ///   但新口径的**误差常数小 6～10 倍**：0.5 mm 网格上误差 0.4 W，旧口径要 0.1 mm 才有同等精度（单元数差 20 倍）。
    ///   判据窗口只有 3 W（管孔净流入 &gt; 0 且法兰增量温降 ≤ 10 K），0.4 W 判得动、2.3 W 判不动 —— 这是改口径的理由。
    /// </param>
    public static ShellThermalResult Solve(ShellMesh m, double[] jMagAPerMm2, DesignInputs p,
                                           double tRootC, double insulBoundaryX,
                                           bool symmetricInsul = false,
                                           int maxIter = 60000, double tol = 1e-4,
                                           double tabBoundaryX = double.NaN,
                                           double tabInsulThickMm = double.NaN,
                                           bool holeFaceDirichlet = true,
                                           double discRadiusMm = double.NaN,
                                           double insulDiscRadiusMm = double.NaN,
                                           double lossTableHiC = double.NaN,
                                           int lossTableNodes = 0,
                                           bool holeAnchorOnCircle = true)
    {
        // ★ 2026-09-23（F6d）：holeAnchorOnCircle **只供测试用**（写法照 lossTableHiC 先例）—— 局部热稳定的管孔锚点取孔圆 r = ShellMesh.HoleRadiusMm（缺省，生产），
        //   false = 老口径「孔格形心的最小半径」（那个数随被孔圆切到的格集合跳，与 F6 同源），门拿它做「改回 ⇒ 红」对照。生产代码不许传。
        // ★ R48（2026-09-15，Opus 5；数值把关人第十四轮）：lossTableHiC／lossTableNodes **只供测试用** —— 门 d 要用改动前的表（设定 + 200 K、60 节点）
        //   复现基线树的逐位记录，才能把「散热表换了」与「别的东西动了」分开。缺省（NaN／0）= 生产：上限 LossTableHiC、节点 LossTableNodes(环境, 上限)。
        //   生产代码不许传这两个参数（生产链配方由 R48RecipeFingerprintTests 的行为门守：每片热解的 Recipe.Rule == ProductionThermalRule）。
        int n = m.CellCount;
        var res = new ShellThermalResult { T = new double[n] };
        if (n == 0) return res;

        // ── 表面热流表 q″(T) [W/mm²]（原始 W/m² → ×1e-6），与 PlateThermal2D 同口径
        // ★★★★★ 对流特征长度（2026-08-28）：**唯一来源** = DesignInputs.ConvCharLenM。
        //
        //   此前写死 0.05 且**四处各存一份**（本处、DesignScreen×2、RampTwoNode），
        //   全都与几何脱钩 —— 而盘径与舌长正是被优化的变量。
        //
        //   ⚠ 我一度改成「网格包围盒最大跨度」（约 0.17 m）并实测：
        //     设计记录 0.8 档的 ②′ 从 +1.123 W 翻成 **−1.880 W**（负 = 热往管里灌，烧断方向），
        //     ③ 从 +5.182 翻成 −0.643 K，两档双双「设计记录自己不过判据」。
        //   ⚠ **但 0.17 同样是猜的**：Churchill–Chu 要的是**竖直板高度**，
        //     而这片板在现场怎么摆没有确认过（舌片朝下 ~170 mm；盘立舌横 ~60 mm）。
        //     把一个拍的数换成另一个拍的数、并借此翻掉设计记录，**那不叫修复**。
        //
        //   ⇒ 处置：升为**显式输入**、带出处、标「待现场确认」，默认仍取 0.05
        //     （**保持现状**，不是有依据）。灵敏度记在 HANDOVER §0.0.3 ⑱。
        double charLen = p.ConvCharLenM;
        res.CharLenM = charLen;
        // ★★ R48（2026-09-14，Opus 5；审查意见「圆盘保温 0 mm 时四个消费方物理含义不一致」）：三张表面热流表都改调**唯一配方**
        //   DesignScreen.PlateFluxWPerM2（裸面／保温面都传真实风速 p.FlangeAirVelocityMPerS，包不包按 DesignScreen.FlangeFaceInsulated）。
        //   修的病：圆盘保温表原先无条件走 PlateFlux，厚度 0（整线「不包」、或逐片 0 层）时退到外覆材料 ε=0.45，
        //   而本文件的局部热稳定（LocalStability → PlateFluxWPerM2）与整片热稳定（FlangeStability）按裸铂 ε=0.18 —— 同一片板两种表面，辐射差约 2.5 倍。
        //   包着时（≥ 0.05 mm）调用参数与原来逐项相同 ⇒ 默认口径逐位不变；变的只有「圆盘 0 mm」这一种。
        // ★★ R48（2026-09-15，Opus 5；常驻数值把关人第十三轮查出）：表的温度上限原为「设定 + 200 K」，LossTable.Eval 超界**静默钳住** ——
        //   共用片 1 取段 1 设定 1080 ⇒ 上限 1280 °C，而方案 A X=7 片1 管根 1304.89、舌区峰 1369 °C，第一轮 R4 舌区峰 1286.59 °C 都超了：
        //   超出的格子散热冻在上限处，法兰算偏热、负反馈被削弱，可能造出假的热失控。
        //   改为统一上限 = 铂熔点（LossTableHiC），节点数按原间距（约 22 K）放大；解完若最高温超过它，LineRunner 把本片判成「场没解到位」。
        // R48（2026-09-15，Opus 5）：三张表的建法原样提成 SurfaceLossTablesFor（门 d 推容差要比新旧表，调同一份），算式逐字未改。
        var tables = SurfaceLossTablesFor(p, tabInsulThickMm, lossTableHiC, lossTableNodes);
        double tHi = tables.Bare.HiC;
        res.LossTableHiC = tHi;
        bool discInsul = tables.DiscInsulated;
        var insTab = tables.Disc;
        bool tabInsul = tables.TabInsulated;
        var tabInsTab = tables.Tab;

        var insulated = new bool[n];
        var lossFor = new LossTable[n];
        // ★★★★★ R48（2026-09-14，Opus 5）：保温边界默认按**半径**划（规则见 FlangePlate.UnderDiscInsulation，
        //   那里是唯一定义，本处是它的无板件表达）。insulDiscRadiusMm > 0 ⇒ r ≤ 盘半径包法兰保温；
        //   NaN ⇒ 旧的按 x 划（命令行仪器与显式指定分界的调用方，逐位不变）。
        //   为什么：旧口径下舌半宽 = 盘半径时 −x 半个圆盘包的是**舌保温旋钮**，而圆盘区最高温的峰就在那半边。
        bool insulByRadius = insulDiscRadiusMm > 1e-9;
        // ★★ R48（2026-09-14，Opus 5；实验 c 与两位常驻把关人第四／七轮）：**分界圆穿过的格子按有料面积份额混合两种保温的热流**。
        //   病：原来整格按形心归一边。分界半径在一个格宽内平移，抽热直线残差 0.40～0.90 W（deliverable/R48_实验c_温度场台阶抖动_2026-09-14.txt c1），
        //   即设计的抽热随「保温圆落在格子哪里」跳 ±0.5 W；而逐片窗口在抽热上只宽约 4 W。
        //   修法：份额 f = 格内有料面积里落在 r ≤ 保温半径的份额（FlangeMesher.MaterialFraction，分子分母同一张栅格），
        //   q(T) = f·q法兰保温(T) + (1−f)·q舌保温(T)（LossTable.Blend，混合热流不混合厚度）。
        //   只改散热：圆盘区判据的分区、分区能量账的归属、局部热稳定的格子仍按形心（insulated[]）；局部热稳定在混合格取两种保温里较厚的（偏保守）。
        //   ⚠ 这只修离散，不改物理假设 —— 保温仍在 r = 保温半径处突变。实际包层怎么收尾是另一件事，见 deliverable/R48_压接对齐与保温台阶_2026-09-14.md §4。
        //   没有厚度场（非 BuildFromField 生成的网格）⇒ 退回按形心，并写进 InsulRule。
        var insulFrac = new double[n];
        int nBlend = 0;
        bool noField = insulByRadius && !m.HasMaterialSource;   // 2026-09-18 Fable 5.1：材料来源改读 ShellMesh.Material（解析路径不再有栅格 SourceField）
        // R48 配方指纹（2026-09-15 Opus 5）：在循环里数「真按半径划的格」「因为没有厚度场真跳过混合的格」—— 配方的混合状态按这两个数记，不按输入重判
        int nByRadiusCells = 0, nNoFieldCells = 0;
        for (int i = 0; i < n; i++)
        {
            double cx = m.Centroid[i].X, cz = m.Centroid[i].Z;
            if (insulByRadius) nByRadiusCells++;
            insulated[i] = insulByRadius
                         ? FlangePlate.InsideInsulCircle(cx, cz, insulDiscRadiusMm)
                         : symmetricInsul
                             ? Math.Abs(cx) <= Math.Abs(insulBoundaryX)
                             : cx >= insulBoundaryX;
            lossFor[i] = insulated[i] ? insTab : tabInsTab;
            insulFrac[i] = double.NaN;
            if (!insulByRadius) continue;
            if (noField) { nNoFieldCells++; continue; }
            // 只有格子矩形跨过分界圆才可能 0 < f < 1：最近点半径 < R < 最远角半径
            var nd = m.Cells[i];
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
            foreach (int k in nd)
            {
                var v = m.Nodes[k];
                x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z);
            }
            double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
            double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            if (FlangePlate.InsideInsulCircle(fx, fz, insulDiscRadiusMm) || !FlangePlate.InsideInsulCircle(nx, nz, insulDiscRadiusMm)) continue;
            double fr = FlangeMesher.MaterialFractionInCircle(m, i, insulDiscRadiusMm);   // 2026-09-18 Fable 5.1：解析板按精确积分、栅格按方格中心（同一入口）
            if (double.IsNaN(fr)) continue;
            insulFrac[i] = fr;
            if (fr >= 1.0) lossFor[i] = insTab;
            else if (fr <= 0.0) lossFor[i] = tabInsTab;
            else { lossFor[i] = LossTable.Blend(insTab, tabInsTab, fr); nBlend++; }
        }
        res.InsulRule = insulByRadius
            ? $"保温：r ≤ {insulDiscRadiusMm:0.0} mm 包法兰保温，其余包舌保温"
              + (noField ? "（这张网格没有厚度场，分界圆上的格子按形心整格归一边）" : $"（分界圆上 {nBlend} 格按有料面积份额混合两种保温的散热）")
            : $"保温：x ≥ {insulBoundaryX:0.0} 包法兰保温（按指定分界，或没拿到盘半径）";
        // R48（2026-09-14，Opus 5）：圆盘保温 0 mm 时上面那句「包法兰保温」只是分区，表面按裸铂算 —— 写明，免得读成「包了一层 0 mm 的保温」
        if (!discInsul) res.InsulRule += "；法兰保温厚度为 0 ⇒ 这一区按裸铂表面散热";

        // ── 舌端边界的三种模式（见 DesignInputs.BusbarConductanceWPerK 的注释）
        //   ① 热导（G ≥ 0）：q = G·(T − T_冷端)，**物理上唯一自洽的一种**，接头温度是输出
        //   ② 定温（G < 0 且 ClampTempC ≥ 0）：假设铜排能把接触点按住
        //   ③ 自由（都不给）：假设铜排完全不导热
        bool busG = ClampBoundaryOf(p) == ClampBoundary.Conductance;   // R48 G2（2026-09-15 Opus 5）：改调唯一判定，规则逐字相同
        // 2026-09-15 Opus 5（合并，复审后改）：舌端「定温」只判这一次 —— 下面钉压接格（外圈与整面两处 isFixed）、面上定温（clampSetT）、铜排带走（qc）四处都读它。
        //   复审查出：原先钉格的两处仍是就地式子 `!busG && 夹持温度 ≥ 0`，面上定温与铜排带走调 ClampBoundaryOf；规则现在逐字相同、数逐位不变，
        //   但 ClampBoundaryOf 一改，面上导度就会施加到没被钉住的压接格上、而自检只比 clampSetT 与 tabCell 查不出 ⇒ 静默错数。
        bool clampFixed = ClampBoundaryOf(p) == ClampBoundary.FixedTemp;

        // ── 定温边界
        var isFixed = new bool[n];
        var holeCell = new bool[n];
        var tabCell = new bool[n];
        int nHoleFaces = 0, nHolePinned = 0, nFullFaceCells = 0;   // R48 配方指纹（2026-09-15 Opus 5）：真遇到的孔边界面、真钉住的孔边格、真并进的压接格
        foreach (var f in m.Faces)
        {
            if (f.B >= 0) continue;
            if (f.Tag == ShellMesh.TagHole)
            {
                holeCell[f.A] = true;
                nHoleFaces++;
                // ★ R48（2026-09-13，Opus 5）：holeFaceDirichlet = true 时**不把整格钉死**，
                //   改在孔边界**面**上施加管温（见下面 gHole）。理由：钉整格等于把定温位置放在**形心**上，
                //   而形心距真实孔边界有半个格子 ⇒ 边界位置带 O(h) 误差 ⇒ 抽热只有一阶收敛
                //   （实测正方形网格五档：7.441/7.485/8.487/9.491/10.059，差值比 0.57 ≈ 一阶）。
                //   面上施加则定温落在真实边界，形心到面正好是 MeshFace.DistAB（边界面的定义就是形心到边中点）。
                if (!holeFaceDirichlet) { isFixed[f.A] = true; res.T[f.A] = tRootC; nHolePinned++; }
            }
            else if (f.Tag == ShellMesh.TagTabEnd)
            {
                tabCell[f.A] = true;
                if (clampFixed) { isFixed[f.A] = true; res.T[f.A] = p.BusbarClampTempC; }   // 2026-09-15 Opus 5（合并，复审后改）：原就地式子 !busG && 夹持温度 ≥ 0，同一规则
            }
        }
        // 2026-09-23（F6d）：局部热稳定的管孔锚点取孔圆（判定调 HoleAnchorOnCircleFor，与 RecipeFor 同一处；是否真走了那一支由 anchorOnCircleUsed 记）
        bool anchorOnCircle = HoleAnchorOnCircleFor(m, holeAnchorOnCircle, nHoleFaces);
        bool anchorOnCircleUsed = false;
        // ★ R48 生产配方（2026-09-14，Opus 5）：压接段整面接触 —— 形心在压接段内的格一并当压接格（ShellMesh.ClampCell 空 = 老口径只钉外圈，逐位不变）。
        //   依据 deliverable/R48_压接整面接触AB_2026-09-14.txt；配方声明见 FlangeMesher.BuildFromField。
        //   tabCell 从此是「整个接触面」而不只是外圈。下游用到它的量在整面口径下逐条核过（2026-09-14 Opus 5）：
        //   · 铜排带走（QToClampW，定温）：只累加压接格与邻格的温差导热 —— 压接格之间同温、互相不传热，只剩内边与自由段的交界面，语义不变；
        //   · 能量账与分区账的排除格（Excluded）、局部热稳定候选：定温时整段压接格都是边界、不入账，正确（老口径反而把压接段中间当自由格记账）；
        //   · 铜排热导按面积分摊（gBus）：分摊到整个接触面，正是「接触面整体导热」的本意；老口径只摊到外圈；
        //   · 局部热稳定的压接锚点 xClamp：取的是下标最后一个锚点格的 x，单舌时即离舌尖最远那一列（内边），整面与外圈同一列，语义不变；
        //     ⚠ 双舌时那个式子落在 +x 端舌尖那一列（外边，不是内边）—— 两种口径一样、早就如此，双舌板只在命令行仪器里造，本次不改，记为待办；
        //     【R48 F 2026-09-15 Opus 5：面上定温（配方 ⑤，缺省）下锚点改为**逐格取最近的压接面**（双舌两侧内边取近者），这条待办在面上口径下做掉了；
        //       形心口径（开关关）仍是上面这个式子，逐位不变。见下面 clampFaceX 那段。】
        //   · 铜排串联热阻的舌宽 = 压接格总面积 ÷ 压接长：整面口径下才真等于舌宽（老口径是外圈面积，约 2h + h·宽/压接长，不是舌宽）；
        //   · 舌端平均温度 TTabEndMeanC、铜排串联热阻里的舌厚与舌端温度：原为按格数平均，整面 + 分级网格上压接段内格子大小不一（2026-09-14 时还铺了细带，配方 ④；
        //     R48 F 2026-09-15 Opus 5：细带缺省已不铺，锚点两侧与远场粗格照样大小不一，理由不变）⇒ 整面口径下改面积加权，
        //     老口径（ClampCell 空）保持按格数平均、与改动前逐位相同（2026-09-14 Opus 5 复审修），见各处注释。
        //   · 下游集总模型（整片热稳定、升温两节点）从网格取面积、质量时同样要把压接格排除在外 —— 那两处在 LineRunner.FlangeLumped（2026-09-14 Opus 5 复审补）。
        //   ★ R48 F（2026-09-15 Opus 5）：定温模式下夹持温度默认施加在**压接面**上（ShellMesh.ClampFaceDirichlet，见下面 clampSetT 那段）；
        //     上面各条语义不变 —— 压接格仍是定温格、仍排除在账外，变的只有压接面的导度（半距）与局部热稳定的压接锚点（逐格取最近的压接面，不是压接格形心）。
        bool fullFace = m.ClampFullFaceActive;                               // 整面接触口径（生产配方）；false = 老口径只钉外圈。2026-09-15 Opus 5（J 路，P2-5）：原为 ClampCell.Length == n —— 全假数组也走面积加权，与配方指纹的定义不一致
        if (fullFace)
            for (int i = 0; i < n; i++)
                if (m.ClampCell[i])
                {
                    tabCell[i] = true;
                    nFullFaceCells++;
                    if (clampFixed) { isFixed[i] = true; res.T[i] = p.BusbarClampTempC; }   // 2026-09-15 Opus 5（合并，复审后改）：同上
                }
        for (int i = 0; i < n; i++) if (!isFixed[i]) res.T[i] = tRootC;

        // 总热导按舌端单元面积分摊（R48 2026-09-14 Opus 5：生产网格上舌端单元 = 整个压接接触面，见上）
        double tabAreaTot = 0;
        for (int i = 0; i < n; i++) if (tabCell[i]) tabAreaTot += m.Area[i];
        var gBus = new double[n];
        if (busG && tabAreaTot > 1e-9)
            for (int i = 0; i < n; i++)
                if (tabCell[i]) gBus[i] = p.BusbarConductanceWPerK * m.Area[i] / tabAreaTot;

        // ★ R48（Opus 5）：孔边界面的半格导度 —— q = gHole·(管温 − T_格心)，与 gBus 同构。
        //   只在 holeFaceDirichlet 开启时非零；随温度更新（放在 UpdateG 里）。
        var gHole = new double[n];

        // ★★ R48 F（2026-09-15，Opus 5；物理把关人）：**压接面上定温**（ShellMesh.ClampFaceDirichlet，默认开；只在整面接触 + 定温模式下起作用）。
        //   形心整格口径：压接格整格钉在夹持温度，压接面导度按两格形心距 ⇒ 定温位置落在内边那排压接格形心，比真实内边偏 h/2（自由舌片热长度偏长半格）。
        //   面上口径：压接格仍是固定格（值 = 夹持温度，不作未知数），**压接面**（一侧压接格、一侧自由格的内部面）导度只取自由格一侧：
        //     g = k(T_自由)·t_自由·L ÷ (自由格形心到面中点的距离 ShellMesh.CentroidToFaceMm) —— 与孔边 gHole 同一写法（kA·L/DistAB，边界面 DistAB 就是这个距离）。
        //   ⇒ 自由格方程里那一项正是 g·(T_夹持 − T_自由)；夹持带走 QToClampW（对压接格累加邻面导度 × 温差）自动就是穿过压接面的热流；能量账、分区账不用改。
        //   热导模式（busG）与自由端：热场里没有定温边界，压接格是热未知数 ⇒ 面导度照旧（clampSetT = null），两种口径的差只经电流场进来（见 ShellMesh.ClampFaceDirichlet）。
        //   开关关 ⇒ clampSetT 为 null，逐位回到形心整格口径。
        // 2026-09-15 Opus 5（合并）：F 写这行时汇总处的 clamped 还是就地式子（!busG && 夹持温度 ≥ 0），G2 已把那处改调唯一判定 ClampBoundaryOf ——
        //   这里跟着改调同一个函数（规则逐字相同，结果逐位不变），免得 F 的面上定温与 G2 的定温判定各判各的。
        bool clampedBC = clampFixed;   // 2026-09-15 Opus 5（合并，复审后改）：与钉格同一个判定（见 busG 下 clampFixed 那段）
        bool[]? clampSetT = m.ClampFaceActive && clampedBC ? m.ClampSetCells() : null;
        if (clampSetT != null)
            for (int i = 0; i < n; i++)
            {
                if (clampSetT[i] != tabCell[i])
                    throw new InvalidOperationException($"温度场：压接格集合（ShellMesh.ClampSetCells）与舌端格对不上（格 {i}）—— 两份压接格定义漂开了，面上定温会施加到错的面上。");
                // 2026-09-15 Opus 5（合并，复审后改）：面上定温只许施加在真被钉住的压接格上（复审 minor：原自检只比 clampSetT 与 tabCell，不比 isFixed）
                if (clampSetT[i] && !isFixed[i])
                    throw new InvalidOperationException($"温度场：压接格 {i} 要施加面上定温却没有被钉在夹持温度 —— 定温判定漂开了（钉格与面上定温不是同一个判定）。");
            }

        // ── 面导度 G = k·t·L/d（k 取两侧调和平均；k 随 T 变化不大，用当前 T 更新）
        int nf = m.Faces.Count;
        var gcond = new double[nf];
        // 2026-09-15 Opus 5（合并）：热解配方的「压接面上定温」在这里量 —— 记下真走了面上那一支的面（G1 配方指纹 × F 配方 ⑤）
        var clampFaceUsed = new bool[nf];
        void UpdateG()
        {
            for (int k = 0; k < nf; k++)
            {
                var f = m.Faces[k];
                if (f.B < 0 || f.DistAB < 1e-12) { gcond[k] = 0; continue; }
                if (clampSetT != null && ShellMesh.IsClampFace(f, clampSetT, out int fc) && !isFixed[fc])
                {
                    clampFaceUsed[k] = true;
                    // 压接面上定温（R48 F 2026-09-15 Opus 5，见 clampSetT 那段）：只取自由格一侧，距离 = 形心到面中点
                    double dFace = m.CentroidToFaceMm(fc, f);
                    gcond[k] = dFace < 1e-12 ? 0 : Materials.PtThermalK(res.T[fc]) * 1e-3 * m.Thickness[fc] * f.Length / dFace;
                    continue;
                }
                double kA = Materials.PtThermalK(res.T[f.A]) * 1e-3 * m.Thickness[f.A]; // W/(mm·K)·mm
                double kB = Materials.PtThermalK(res.T[f.B]) * 1e-3 * m.Thickness[f.B];
                double kf = (kA * kB) > 0 ? 2 * kA * kB / (kA + kB) : 0;
                gcond[k] = kf * f.Length / f.DistAB;
            }
            if (!holeFaceDirichlet) return;
            Array.Clear(gHole);
            for (int k = 0; k < nf; k++)
            {
                var f = m.Faces[k];
                if (f.B >= 0 || f.Tag != ShellMesh.TagHole) continue;
                // 2026-09-23（F6 审查后统一，findings #6／#39）：原写法 `|| f.DistAB < 1e-12) continue;` 对过小的孔面静默跳过（跳过 = 那段孔边不定温，
                //   孔面仍计进 nHoleFaces／HoleFaceCount），电流场（ShellCurrent，F6a）对同一条面却抛异常 —— 两个场口径不一。改成与电流场同一条判据、同样抛：
                //   弧面距离下限 ShellMesh.GeomTolMm = 1e-7 mm；直边距离 = 形心到边中点，面积为正的格上不会趋近 0；生产网格触发不到，抛出来是自证。
                //   NaN 也挡住（原写法 NaN < 1e-12 为假，会把 NaN 写进 gHole）。
                if (!(f.DistAB >= 1e-12))
                    throw new InvalidOperationException($"热场：孔面 {k}（格 {f.A}）的形心—边界距离 {f.DistAB:R} mm 过小 —— 面导度无界，孔面不许静默跳过（与 ShellCurrent 同一条判据）。");
                double kA = Materials.PtThermalK(res.T[f.A]) * 1e-3 * m.Thickness[f.A];
                gHole[f.A] += kA * f.Length / f.DistAB;      // DistAB = ShellMesh.BoundaryDistMm：弧面 = 形心到孔圆的法向距（F6b 2026-09-23），直边 = 形心到边中点
            }
        }

        var nbr = new List<(int cell, int face)>[n];
        for (int i = 0; i < n; i++) nbr[i] = new List<(int, int)>();
        for (int k = 0; k < nf; k++)
        {
            var f = m.Faces[k];
            if (f.B < 0) continue;
            nbr[f.A].Add((f.B, k)); nbr[f.B].Add((f.A, k));
        }

        if (StepTolOverride > 0) tol = StepTolOverride;
        // 步长判据保留为**兜底**（防止残差算不出来时无限转），但它不再是停机的依据。

        // ══ Picard 迭代：q″(T) 与 k(T) 用上一轮温度，欠松弛
        //
        // ★★★ 2026-08-30：内层由**一次 Gauss–Seidel 扫描**换成 **CG + Jacobi**。
        //
        // 病灶（实测，不是设想）：0.6 档网格无关复核跑到 0.125 mm（47 567 单元）时，
        // 三片**全部撞上 60000 轮上限**（步长 9.69E-4 / 9.04E-4 / 1.78E-4，目标 1e-4）。
        // ⇒ 铁律第 ③ 条生效，吃这些片的判据一律「判不了」⇒ 整趟 4 小时白跑。
        //
        // 病因是**复杂度**：GS 的迭代数随单元数线性涨 ⇒ 总功 O(n²)。
        //   --shell   2 727 单元 → 5 805 轮
        //   复核     47 567 单元 → 按 O(n) 外推需 ~100 000 轮 > 60 000
        // 抬上限没用：抬到 15 万就是一档 5.7 小时。
        //
        // ⚠ **换解法不改答案**，这可以证明而不是指望：不动点处
        //   (Σg + 2·slope·A + G)·T = ΣgT + (qv − 2(qs − slope·T))·A + G·T_冷端
        //   两边的 slope·T 相消 ⇒ Σg(T_c − T) + (qv − 2qs)A + G(T_冷 − T) = 0，
        //   正是**真非线性残差为零**。所以不动点与内层怎么解无关，
        //   欠松弛也只影响路径不影响终点。⇒ 收敛解必须逐位相同（`--thermcg` 配对实测）。
        const double relax = 0.7;
        int it = 0; double maxd = 0;

        /// <summary>
        /// **真残差**（相对）：逐格代回稳态能量方程的不闭合量 / 自由格总焦耳热。
        /// 用**真**散热 q_s(T)，不是迭代里为稳定做的线性化 ——
        /// 线性化只准出现在「怎么走」里，不准出现在「走到没有」里。
        /// ⚠ 调用前必须 UpdateG()：gcond 是它的一部分。
        /// </summary>
        (double W, double Rel) ResidRel()
        {
            double rMax = 0, bSum = 0;
            for (int i = 0; i < n; i++)
            {
                if (isFixed[i]) continue;
                double sumG = 0, sumGT = 0;
                foreach (var (c, k) in nbr[i]) { sumG += gcond[k]; sumGT += gcond[k] * res.T[c]; }
                if (sumG <= 0) continue;
                double ti = res.T[i], t = m.Thickness[i], A = m.Area[i];
                double qv = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t;
                double qs = lossFor[i].Eval(ti);
                double r = sumGT - sumG * ti + (qv - 2 * qs) * A
                         + gBus[i] * (p.BusbarSinkTempC - ti)
                         + gHole[i] * (tRootC - ti);
                rMax = Math.Max(rMax, Math.Abs(r));
                bSum += Math.Abs(qv * A);
            }
            return (rMax, bSum > 1e-12 ? rMax / bSum : double.NaN);
        }

        // 自由单元编号（固定单元的值搬到右端项）——与 ShellCurrent 同一套写法
        //
        // ★★ 先 UpdateG()：gcond 在它跑之前**全是 0**，而下面用 `Σg > 0` 挑自由单元 ——
        //   不先算就一个都挑不出来，nFree = 0，CG 什么也没解，而外层看不出异常：
        //   它照样跑完、照样报一整套判据值、还快了 2.4 倍。
        //   2026-08-30 实测就是这样，被配对对照（--thermcg）当场抓住：温度场差 **697 K**。
        //   ⇒ 换解法必须配对对账，这条不是形式。
        UpdateG();
        var freeC = new List<int>(n);
        var idxC = new int[n];
        for (int i = 0; i < n; i++)
        {
            idxC[i] = -1;
            if (isFixed[i]) continue;
            double sg = 0;
            foreach (var (_, k) in nbr[i]) sg += gcond[k];
            if (sg > 0) { idxC[i] = freeC.Count; freeC.Add(i); }
        }
        int nFree = freeC.Count;
        // ★ 有自由单元却一个都没挑出来 = 上面那个 bug 又回来了。**当场炸**，不许静默跑空。
        {
            int movable = 0;
            for (int i = 0; i < n; i++) if (!isFixed[i] && nbr[i].Count > 0) movable++;
            if (movable > 0 && nFree == 0)
                throw new InvalidOperationException(
                    $"温度场：{movable} 个可动单元，却一个自由单元都没挑出来 —— "
                    + "面导度还没算（UpdateG 没跑）。跑空的解会给出一整套看起来正常的错数。");
        }
        var diagC = new double[nFree];
        var rhsC = new double[nFree];
        var xC = new double[nFree];
        var rC = new double[nFree]; var zC = new double[nFree];
        var pC = new double[nFree]; var apC = new double[nFree];

        /// 在当前 T 上把线性化系统装出来（对称正定：对角 = Σg + 2·slope·A + G，非对角 = −g）
        void Assemble()
        {
            for (int a = 0; a < nFree; a++)
            {
                int i = freeC[a];
                double sumG = 0, fixedPart = 0;
                foreach (var (c, k) in nbr[i])
                {
                    sumG += gcond[k];
                    if (idxC[c] < 0) fixedPart += gcond[k] * res.T[c];
                }
                double ti = res.T[i], t = m.Thickness[i], A = m.Area[i];
                double qv = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t;
                var tab = lossFor[i];
                double qs = tab.Eval(ti);
                double slope = Math.Max(0, tab.Slope(ti));
                diagC[a] = sumG + 2 * slope * A + gBus[i] + gHole[i];
                rhsC[a] = fixedPart + (qv - 2 * (qs - slope * ti)) * A + gBus[i] * p.BusbarSinkTempC + gHole[i] * tRootC;
                xC[a] = ti;
            }
        }

        void MatVec(double[] src, double[] dst)
        {
            for (int a = 0; a < nFree; a++)
            {
                int i = freeC[a];
                double v = diagC[a] * src[a];
                foreach (var (c, k) in nbr[i]) { int jj = idxC[c]; if (jj >= 0) v -= gcond[k] * src[jj]; }
                dst[a] = v;
            }
        }

        /// 内层线性解：CG + Jacobi。返回用了多少次矩阵乘。
        int SolveLinear(double jouleTotalW)
        {
            // ★★ 归一化**不能**用 max|rhs|（2026-08-30 实测翻车）：
            //   rhs 里含定温边界项 g·T_根（T_根 ≈ 1150 °C），量级上万 W；
            //   而真残差只有 ~1e-2 W ⇒ `1e-7 × bNorm` 比残差还大，
            //   **第一次检查就通过，CG 一轮都不跑**，xC 原地不动、外层空转 300 轮。
            //   而它跑得飞快、报了一整套数 —— 又是「看起来正常的错数」。
            //   ⇒ 归一化用与真残差**同一个**量：自由格总焦耳热。
            MatVec(xC, rC);
            for (int a = 0; a < nFree; a++) rC[a] = rhsC[a] - rC[a];
            double r0 = 0;
            for (int a = 0; a < nFree; a++) r0 = Math.Max(r0, Math.Abs(rC[a]));
            // 不精确牛顿的强迫项：每一外层轮把线性残差压掉三个量级，
            // 但不必比外层的目标还紧（那是白费）。
            double target = Math.Max(1e-3 * r0, 0.1 * ResidualRelTol * Math.Max(jouleTotalW, 1e-12));
            double rz = 0;
            for (int a = 0; a < nFree; a++) { zC[a] = rC[a] / diagC[a]; pC[a] = zC[a]; rz += rC[a] * zC[a]; }
            int k2 = 0;
            for (; k2 < 2000; k2++)
            {
                double resid = 0;
                for (int a = 0; a < nFree; a++) resid = Math.Max(resid, Math.Abs(rC[a]));
                if (resid <= target) break;
                MatVec(pC, apC);
                double pap = 0;
                for (int a = 0; a < nFree; a++) pap += pC[a] * apC[a];
                if (!(Math.Abs(pap) > 1e-300)) break;
                double alpha = rz / pap;
                for (int a = 0; a < nFree; a++) { xC[a] += alpha * pC[a]; rC[a] -= alpha * apC[a]; }
                double rzNew = 0;
                for (int a = 0; a < nFree; a++) { zC[a] = rC[a] / diagC[a]; rzNew += rC[a] * zC[a]; }
                double beta = rz > 1e-300 ? rzNew / rz : 0;
                for (int a = 0; a < nFree; a++) pC[a] = zC[a] + beta * pC[a];
                rz = rzNew;
            }
            return k2;
        }

        if (UseGaussSeidel)
        {
            for (; it < maxIter; it++)
            {
                if (it % 20 == 0) UpdateG();
                maxd = 0;
                for (int i = 0; i < n; i++)
                {
                    if (isFixed[i]) continue;
                    double sumG = 0, sumGT = 0;
                    foreach (var (c, k) in nbr[i]) { sumG += gcond[k]; sumGT += gcond[k] * res.T[c]; }
                    if (sumG <= 0) continue;

                    double ti = res.T[i], t = m.Thickness[i], A = m.Area[i];
                    double qv = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t;
                    var tab = lossFor[i];
                    double qs = tab.Eval(ti);
                    double slope = Math.Max(0, tab.Slope(ti));
                    double denom = sumG + 2 * slope * A + gBus[i] + gHole[i];
                    double rhs = sumGT + (qv - 2 * (qs - slope * ti)) * A + gBus[i] * p.BusbarSinkTempC + gHole[i] * tRootC;
                    double tNew = rhs / denom;
                    double d = tNew - ti;
                    res.T[i] = ti + relax * d;
                    maxd = Math.Max(maxd, Math.Abs(d));
                }
                // ★ 停机判**残差**，不判步长（步长小 ≠ 解到位，实测放大约 1300 倍）。
                //   GS 每 20 轮算一次（与 UpdateG 同频）—— 每轮都算会把 GS 的成本翻倍，
                //   而 GS 这一支现在只用于配对对照。
                if (it % 20 == 19 && ResidRel().Rel <= ResidualRelTol) { it++; break; }
            }
        }
        else
        {
            // 外层 Picard：装一次线性化系统 → CG 解到位 → 欠松弛更新 → 看步长
            // ⚠ 外层轮数上限按**外层**给：内层已经把 O(n) 那一段吃掉了，
            //   外层只处理非线性，轮数与网格大小基本无关。
            int outerCap = Math.Max(200, maxIter / 200);
            int lastInner = 0;
            for (; it < outerCap; it++)
            {
                UpdateG();
                Assemble();
                double joule = 0;
                for (int a = 0; a < nFree; a++)
                {
                    int i = freeC[a];
                    joule += Math.Abs(Materials.PtResistivity(res.T[i]) * 1e3
                           * jMagAPerMm2[i] * jMagAPerMm2[i] * m.Thickness[i] * m.Area[i]);
                }
                lastInner = SolveLinear(joule);
                maxd = 0;
                for (int a = 0; a < nFree; a++)
                {
                    int i = freeC[a];
                    double d = xC[a] - res.T[i];
                    res.T[i] += relax * d;
                    maxd = Math.Max(maxd, Math.Abs(d));
                }
                // ★ 停机判**残差**。外层每轮都算得起：这一遍是 O(n)，
                //   而同一轮里的 CG 内解是 O(n·√n)。
                UpdateG();
                if (ResidRel().Rel <= ResidualRelTol) { it++; break; }
            }
            res.InnerIterations = lastInner;
        }
        res.Iterations = it; res.Residual = maxd;
        bool stepOk = maxd < tol;          // ★ 只是**步长**判据；真正的判定在下面

        // 收尾：复用**同一个** ResidRel（不再内联第二份 —— 两份迟早漂开）
        UpdateG();
        (res.ResidualW, res.ResidualRel) = ResidRel();

        // ★★ **收敛 = 真残差达标**。步长不再参与判定 —— 它是路径的性质，不是解的性质。
        //   ⚠ 残差算不出来（NaN，例如一格自由格都没有）时**不算过** —— 判不了不算过。
        res.Converged = !double.IsNaN(res.ResidualRel) && res.ResidualRel <= ResidualRelTol;
        _ = stepOk;   // 仍算出来放进 res.Residual 供诊断；不参与判定

        // ── 汇总
        double gen = 0, loss = 0;
        res.CellGenW = new double[n]; res.CellLossW = new double[n];   // R48 G2 复审（2026-09-15 Opus 5）：逐格账，求和顺序不变 ⇒ QGenW／QLossW 逐位不变
        for (int i = 0; i < n; i++)
        {
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            res.CellGenW[i] = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            res.CellLossW[i] = 2 * lossFor[i].Eval(ti) * A;
            gen += res.CellGenW[i];
            loss += res.CellLossW[i];
        }
        res.QGenW = gen; res.QLossW = loss;
        res.PhiOverall = loss > 1e-12 ? gen / loss : double.NaN;

        // 管孔净流入（>0 表示热从管子流进法兰）
        //  · 现行口径：对**被钉死的**孔单元，Σ 邻面导度×温差 —— 那圈单元自身的发热与散热被当成管子吸收（见 QHoleCellGenW）。
        //  · R48（Opus 5）holeFaceDirichlet：直接就是**通过孔边界面**的热流 Σ gHole·(管温 − T_格心)，
        //    定义干净（真实边界上的通量），孔单元也不再被排除在能量账之外。
        double q = 0;
        if (holeFaceDirichlet)
            for (int i = 0; i < n; i++) q += gHole[i] * (tRootC - res.T[i]);
        else
            for (int i = 0; i < n; i++)
            {
                if (!holeCell[i]) continue;
                foreach (var (c, k) in nbr[i]) q += gcond[k] * (res.T[i] - res.T[c]);
            }
        res.QFromTubeW = q;

        // 铜排带走的热：与管孔同法，对**定温的**舌端单元累加邻面导度×温差
        // （>0 表示热从法兰流进铜排 ⇒ 取负号，因为下式算的是「流出定温单元」）
        //
        // ★ 自由端（BusbarClampTempC < 0）时这一项恒等于 0：那时舌端单元不是边界，
        //   它自己发热、自己散热。此前不分情况一律把舌端单元排除在收支之外，
        //   再把「流进它们的净热」记到「铜排带走」名下 —— 残差照样闭合（因为稳态下
        //   那个净流入正等于它们的散热减发热），但**账目是错的**：
        //   90 mm 舌片有 40 mm 压接段，自由端时那 44 % 的发热与散热被整段抹掉，
        //   还被贴上「铜排」的标签。§4.3c/§4.3d 的自由端数就是这么读出来的。
        bool clamped = clampFixed;   // R48 G2（2026-09-15 Opus 5）：唯一判定，= !busG && 夹持温度 ≥ 0；2026-09-15 Opus 5（合并，复审后改）：读边界组装时算好的同一个值
        double qc = 0;
        if (clamped)
            for (int i = 0; i < n; i++)
            {
                if (!tabCell[i]) continue;
                foreach (var (c, k) in nbr[i]) qc += gcond[k] * (res.T[c] - res.T[i]);
            }
        else if (busG)
            for (int i = 0; i < n; i++)
                if (tabCell[i]) qc += gBus[i] * (res.T[i] - p.BusbarSinkTempC);
        res.QToClampW = qc;

        // 能量闭合：自由单元的净产热 + 管孔流入 = 铜排带走
        // （定温单元自身的产热与散热由各自的边界吸收，故只累加自由单元）
        // R48（Opus 5）：面上施加定温时，孔单元是**普通自由单元**（它自己的发热与散热照常入账），
        //   排除的只有真正被钉死的那些。
        bool Excluded(int i) => (holeCell[i] && !holeFaceDirichlet) || (tabCell[i] && clamped);
        res.BoundaryCell = new bool[n];                                  // R48 G2 复审（2026-09-15 Opus 5）：与本账同一个排除判定，交给节点标定
        for (int i = 0; i < n; i++) res.BoundaryCell[i] = Excluded(i);
        // R48 诊断（Opus 5）：那圈被排除在账外的孔单元，自身发热与散热各是多少（见 QHoleCellGenW 的注释）
        for (int i = 0; i < n; i++)
        {
            if (!holeCell[i]) continue;
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            res.QHoleCellGenW += Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            res.QHoleCellLossW += 2 * lossFor[i].Eval(ti) * A;
            res.HoleCellCount++; res.HoleCellAreaMm2 += A;
        }
        double genFree = 0, lossFree = 0;
        for (int i = 0; i < n; i++)
        {
            if (Excluded(i)) continue;
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            genFree += Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            lossFree += 2 * lossFor[i].Eval(ti) * A;
        }
        res.EnergyResidualW = genFree - lossFree + res.QFromTubeW - res.QToClampW;

        // ── 分区账：圆盘 vs 舌片（口径同上，只统计自由单元）
        //
        // ★★★★★ R48（2026-09-14，Opus 5；用户拍板「改」）：**圆盘区改按半径圈，不再按切点切一刀。**
        //
        // ══ 旧口径错在哪（实测，管壁 0.8，09-13 解出的那一点）
        //
        //   旧规则 onTab = 双舌片 ? |x| > |切点x| : x < 切点x。
        //   而这两个内置设计**舌半宽 = 盘半径**（都是 30）⇒ 切点正好落在 **x = 0**
        //   ⇒ 整个 x<0 半边（连同半圈环）被算成「舌片」，圆盘区**只剩 +x 半边**。
        //   逐片实测：
        //     圆盘区 − 管温  −0.482 / −0.243 / −0.224 / −0.409 K   ← 判据看的是这一列，轻松过
        //     舌片区 − 管温  +16.588 / +12.872 / +9.080 / +8.434 K ← 全部超限值 5 K，却不在判据里
        //     舌片区峰位 r = 31～33 mm（盘半径 30，孔半径 25.8）—— 离孔只有 5～7 mm
        //   代码原本排除舌片的理由写着「舌片离管子几十毫米、中间还隔着圆盘」——
        //   这个形状下该理由**不成立**：峰就在圆盘外缘外 1～3 mm，中间什么也没隔。
        //   ⇒ 一条硬安全线在**半个零件**上是瞎的。
        //   ⛔ 上面这段理由**已撤回**（2026-09-14，Opus 5 记）：舌片区 +16.6 K 的峰在保温底下、厚度 = 舌片厚，
        //     就是舌片本身，是判据有意排除的东西，不是盲区。改按半径**不撤**，理由改为：x<0 那半圈环（r 25.8–30）
        //     几何上属于圆盘；改后实测那半圈在求解器落点上 +7～+12 K（峰 r≈29、x=−29），超限值 5，旧口径恰好瞎在这半边。
        //
        // ══ 新口径
        //
        //   圆盘区 = **r ≤ 盘半径**（r 自管轴起算），其余是舌片区。几何上就是「盘内 / 盘外」，
        //   与 x 的正负无关，也不受「舌半宽是否等于盘半径」影响。
        //   拿不到盘半径时（<paramref name="discRadiusMm"/> ≤ 0，例如图纸路径没分析出盘）
        //   退回旧的 x 口径，并把用了哪条规则记进 <see cref="ShellThermalResult.DiscZoneRule"/> ——
        //   **退回必须看得见**，不许静默（本仓库栽过多次的形态）。
        //
        // ══ ⚠ 只改这一处的用途，局部热稳定的候选筛选**逐位不动**
        //
        //   同一个 onTab 此前被两件事共用：
        //     ① 分区热账 + 圆盘区最高温（②″）—— 问的是「离管子近不近」，该按**半径**；
        //     ② 局部热稳定的候选预筛 —— 问的是「冷却侧一样不一样」（盘包保温、舌常裸露），
        //        该按**保温**，也就是按 x。
        //   把两件事绑在一个判断上正是这个洞的根。现在拆开：②用 insulOnTab（旧规则，逐位不变），
        //   ①用 zoneOnTab（新规则）。这样本次改动**不触碰局部热稳定的任何数**。
        double xb = double.IsNaN(tabBoundaryX) ? insulBoundaryX : tabBoundaryX;
        bool byRadius = discRadiusMm > 1e-9;
        res.DiscZoneRule = byRadius
            // 这两句会进界面（判据说明）：不许有修订号、不许有「口径」这类内部词（2026-09-14 物理把关人查出）。
            ? $"圆盘区按 r ≤ {discRadiusMm:0.0} mm 圈"
            : $"圆盘区按 x ≥ {xb:0.0} 圈（没拿到盘半径，**可能只圈到半个圆盘**）";
        double gD = 0, lD = 0, aD = 0, tD = 0, gT = 0, lT = 0, aT = 0, tT = 0;
        double tDMax = double.NegativeInfinity, tTMax = double.NegativeInfinity;
        int iDMax = -1, iTMax = -1;
        // 局部热稳定的候选：按**不稳定判据自己的分子** ρe(T)·J²·t·TCR(T) 排（= LocalStability 的 HeatDeriv）。
        // 逐格精算太贵（本后处理每次场解都跑一遍，一次整线解要跑两千多次）⇒ 先筛后算。
        // **分区各筛各的**：盘包保温、舌常裸露，冷却侧差一个量级，混在一起筛会漏掉裸舌那侧。
        var candD = new List<(double Proxy, int I)>();
        var candT = new List<(double Proxy, int I)>();
        // 超出电阻率拟合区间的自由单元数 —— 见下方「判不了」那一段
        int hotOutOfRange = 0;
        for (int i = 0; i < n; i++)
        {
            if (Excluded(i)) continue;
            double t = m.Thickness[i], A = m.Area[i], ti = res.T[i];
            double g = Materials.PtResistivity(ti) * 1e3 * jMagAPerMm2[i] * jMagAPerMm2[i] * t * A;
            double l = 2 * lossFor[i].Eval(ti) * A;
            // ② 局部热稳定的候选预筛用这条 —— 它问的是「冷却侧一样不一样」，所以**跟着保温走**。
            //   R48 续（2026-09-14，Opus 5）：保温按半径划时它直接取 !insulated[i]（与保温同一个判定）；
            //   保温仍按 x 划时保留旧式子，逐位不变。
            bool insulOnTab = insulByRadius
                            ? !insulated[i]
                            : symmetricInsul
                                ? Math.Abs(m.Centroid[i].X) > Math.Abs(xb)
                                : m.Centroid[i].X < xb;
            // ① 分区热账与圆盘区最高温用这条（按半径分，R48 新口径；拿不到盘半径才退回旧规则）
            bool onTab = byRadius
                       ? Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z) > discRadiusMm
                       : insulOnTab;
            double jj = jMagAPerMm2[i];
            if (ti > LocalStability.FitMaxC) hotOutOfRange++;
            double proxy = Materials.PtResistivity(ti) * jj * jj * t * Materials.PtTcr(ti);
            if (onTab) { gT += g; lT += l; aT += A; tT += ti * A;
                          if (ti > tTMax) { tTMax = ti; iTMax = i; } }   // R48：峰位也要记（见 TabMaxXMm）
            else        { gD += g; lD += l; aD += A; tD += ti * A;
                          if (ti > tDMax) { tDMax = ti; iDMax = i; } }
            // 候选预筛走**保温**那条口径，与上面的热账分区互不影响（见本段开头的说明）
            if (insulOnTab) candT.Add((proxy, i)); else candD.Add((proxy, i));
        }
        // ── 局部热稳定：两区各取前 12 个候选精算，取最小裕度
        {
            const int NCand = 12;
            double best = double.PositiveInfinity; int bi = -1; bool bTab = false;
            int skipped = 0;

            // ── 每格到**最近定温锚点**的距离 L（J_stab 公式里的横向导热项 k·t/L²）
            //
            // 锚点有两个：管孔（被控温的管子按住）与压接段（被铜排夹按住）。
            // ★ 不能传 NaN「不计横向导热」：实测那样把两个现役设计记录判成 0.6×（失稳），
            //   而 HANDOVER 旧记的是 3.1–9.8（§4「核心结论」里「本轮新增的判据」那张表）。
            //   ⚠ 原文写的是「HANDOVER 2038」（此处**刻意不写 § 号**，免得 DocRefTests
            //     把这句历史说明当成一条真引用）—— 那不是节号，是**行号**。
            //     行号在活文件里每编辑一次就漂一次，是最差的一种引用：
            //     它今天指到的地方，明天指到别处，而且**不会有任何东西报错**。
            //     （2026-08-24 DocRefTests 上线时抓到。）
            //   这正是 LocalStability.TabHalfSpanMm
            //   注释里记载过的那个错 —— L 取错 1.65 倍就「整张扫描表被误判成全部局部失稳」
            //   —— 只是这次把 L 当成了 ∞，错得更彻底。
            //   **保守到失真不叫保守，叫判据坏了**：它会把能造的方案全否掉。
            double rHole = double.PositiveInfinity, xClamp = double.NaN;
            for (int i = 0; i < n; i++)
            {
                if (holeCell[i])
                    rHole = Math.Min(rHole, Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X
                                                    + m.Centroid[i].Z * m.Centroid[i].Z));
                // ★★★★★ 只有**真被按住**的格子才配当定温锚点（2026-08-28 修）。
                //   原来只判 tabCell[i] —— 那仅表示「这一格带 TagTabEnd 标签」，
                //   **与舌端是不是定温边界无关**：真正决定的是上面那句
                //   `if (!busG && p.BusbarClampTempC >= 0)`（2026-09-15 Opus 5（合并，复审后改）：该句现为 `if (clampFixed)`，clampFixed = ClampBoundaryOf(p) == FixedTemp，同一规则）。
                //   舌端自由（ClampTempC < 0，如端片）或走**热导边界**（busG，见 --busg）时，
                //   舌端根本不是定温面，却仍被当成锚点 ⇒ 舌尖附近 L 被算得极小
                //   ⇒ J_stab 极大 ⇒ **裕度虚高**。而这条判据取的是**最小**裕度，
                //   虚高会把真正最不稳定的那一格挤掉 —— 方向是**偏乐观**，本项目最忌的那一侧。
                //   ⚠ 现役设计记录四片都夹 450 °C，所以此前不咬；是 --busg 把它激活的。
                //   没有合格锚点时 xClamp 保持 NaN ⇒ toClamp = +∞ ⇒ 退回只看管孔
                //   （管孔恒是定温边界），那是**保守**的退路。
                if (tabCell[i] && (isFixed[i] || busG))
                    xClamp = double.IsNaN(xClamp) ? m.Centroid[i].X
                                                  : Math.Max(xClamp, Math.Abs(m.Centroid[i].X)) * Math.Sign(m.Centroid[i].X);
            }
            if (double.IsInfinity(rHole)) rHole = 0;
            // ★ 2026-09-23（F6d）：管孔锚点取孔圆本身。老口径「孔格形心的最小半径」比孔圆大约半格、且随被孔圆切到的格集合跳（与 F6 同源）；
            //   管子按住的是焊接圆 r = rh 这条线，不是某个格的形心。网格上有孔边界面、孔半径已知时才走新口径，否则照旧（没有孔 ⇒ 0）。
            if (anchorOnCircle) { rHole = m.HoleRadiusMm; anchorOnCircleUsed = true; }
            // R48 F（2026-09-15 Opus 5）：面上定温时锚点在**压接面**上（真实内边 x = 舌尖 + 压接长），不在内边那排压接格形心。
            //   收集所有压接面中点的 x（去重），下面 LatLen 里**逐格取最近的那个**：平行边单舌时只有内边一个 x，与形心口径「下标最后一个锚点格」同一列、差 h/2；
            //   双舌时两侧内边各一个 x，各格取近的那侧。（锥形舌在内边外侧若有面中点恰落在内边上的边界面，那一格也带压接标签、是定温格，
            //   它朝自由格的面会多出别的 x —— 逐格取近者对它照样成立。）
            //   ★ 2026-09-15 Opus 5 复审修（审查意见 minor「锚点由面序决定」）：第一版取「压接面中点里 |x| 最小的第一个」，双舌两侧内边 |x| 相同时
            //     由面序定取哪一侧（通常 −x 侧），+x 那条舌上的格被量到对侧内边、退回只看管孔 —— 与形心口径（+x 外边）取的侧、取的边都不同。
            //     门：R48ClampFaceGateTests (e)（双舌板左右镜像的电流密度，局部热稳定的横向导热长与裕度两边相同）。
            //   形心口径（clampSetT 为 null）不进这段，照旧用上面的 xClamp，逐位不变。
            double[]? clampFaceX = null;
            if (clampSetT != null)
            {
                var xs = new List<double>();
                foreach (var f in m.Faces)
                    if (ShellMesh.IsClampFace(f, clampSetT, out int fc) && !isFixed[fc] && !xs.Contains(f.Mid.X))
                        xs.Add(f.Mid.X);
                if (xs.Count > 0) clampFaceX = xs.ToArray();
            }

            // ★★★★★ 铜排是**部分锚点**（2026-08-28 建模）。
            //
            //   定温边界下舌端是理想热沉，L 就是几何距离。
            //   但热导边界（--busg）下它只是个**有限热阻的出口** ——
            //   既不是理想热沉（旧写法，裕度虚高 2.0×），也不是绝热（保守退路，虚低 0.7×）。
            //
            //   串联热阻给出等效距离：
            //     R = L_几何/(k·A_截面) + 1/G   ⇒   **L_等效 = L_几何 + k·A_截面/G**
            //   后一项就是「把铜排热阻折算成多长的舌片」。
            //
            //   A_截面 = 舌宽 × 舌厚。舌宽由压接带反推：tabAreaTot / 压接段长度。
            //   k 取舌端实际温度下的铂导热系数（PtThermalK 随 T 变，不用常数）。
            //   ★ R48（2026-09-14，Opus 5）：「舌宽 = 压接格总面积 ÷ 压接长」只在整面接触口径下成立（老口径只有外圈面积，舌宽被算成约 2h + h·宽/压接长）。
            //     整面接触（ShellMesh.ClampCell 非空）时舌厚与舌端温度按**面积加权**：分级网格上压接段内格子大小不一（锚点两侧的细格、段中远场粗格；
            //     2026-09-14 时还铺了压接细带 hFine，R48 F 2026-09-15 Opus 5 起缺省不铺，理由照样成立），
            //     按格数平均会偏向细格那几列；面积加权与 gBus 按面积分摊同一个口径。只影响热导模式（busG），定温模式不进这段。
            //   ★ 2026-09-14 Opus 5 复审修（审查意见 minor「老口径不是逐位相同」）：老口径（ClampCell 空）**保持按格数平均** ——
            //     老口径只留给复现改动前的证据，必须与改动前逐位相同；第一轮把面积加权也套到了老口径上，热导／自由端模式的老数就变了。
            //     记录值见 R48ClampRecipeTests 门 (d)（基线树上跑出来的）。
            double busExtraMm = 0.0;
            if (busG && tabAreaTot > 1e-9 && p.BusbarConductanceWPerK > 1e-12
                && p.BusbarClampLengthMm > 1e-9)
            {
                double wTabMm = tabAreaTot / p.BusbarClampLengthMm;          // 舌宽 mm
                double tTabMm, tTabC;
                if (fullFace)
                {
                    double tVol = 0, tTempA = 0;
                    for (int i = 0; i < n; i++)
                        if (tabCell[i]) { tVol += m.Thickness[i] * m.Area[i]; tTempA += res.T[i] * m.Area[i]; }
                    tTabMm = tVol / tabAreaTot;                              // 舌厚 mm（面积加权）
                    tTabC = tTempA / tabAreaTot;                             // 舌端温度 °C（面积加权）
                }
                else
                {
                    double tSum = 0, tCnt = 0, tTempSum = 0;                 // 老口径：按格数平均（改动前原式，逐字未改）
                    for (int i = 0; i < n; i++)
                        if (tabCell[i]) { tSum += m.Thickness[i]; tTempSum += res.T[i]; tCnt++; }
                    tTabMm = tCnt > 0 ? tSum / tCnt : 0;                     // 舌厚 mm
                    tTabC = tCnt > 0 ? tTempSum / tCnt : p.TSetC;
                }
                double aCrossM2 = wTabMm * tTabMm * 1e-6;                    // mm² → m²
                busExtraMm = Materials.PtThermalK(tTabC) * aCrossM2
                           / p.BusbarConductanceWPerK * 1e3;                 // m → mm
            }
            res.BusEquivLenMm = busExtraMm;

            double LatLen(int i)
            {
                double x = m.Centroid[i].X, z = m.Centroid[i].Z;
                double toHole = Math.Sqrt(x * x + z * z) - rHole;
                // 舌端：几何距离 + 铜排热阻的等效长度（定温时后者为 0）
                // R48 F（2026-09-15 Opus 5）：面上定温时逐格取最近的压接面（clampFaceX，见上）；形心口径照旧
                double toClamp = clampFaceX != null
                               ? clampFaceX.Min(xf => Math.Abs(x - xf)) + busExtraMm
                               : double.IsNaN(xClamp)
                               ? double.PositiveInfinity
                               : Math.Abs(x - xClamp) + busExtraMm;
                return Math.Max(1.0, Math.Min(toHole, toClamp));
            }
            void Scan(List<(double Proxy, int I)> cand, bool onTab)
            {
                foreach (var (_, i) in cand.OrderByDescending(x => x.Proxy).Take(NCand))
                {
                    // 保温厚度跟 lossFor 用**同一个** insulated[] 判定，不另立一份
                    double insMm = insulated[i] ? p.FlangeInsulThickMm
                                 : (tabInsul ? tabInsulThickMm : 0.0);
                    // R48（2026-09-14，Opus 5；物理把关人第四轮条件 2）：分界圆上混合了两种保温的格子，局部热稳定不跟着混合，
                    //   取两者里**较厚**的 —— 保温越厚冷却越弱，偏保守。
                    if (insulFrac[i] > 0 && insulFrac[i] < 1)
                        insMm = Math.Max(p.FlangeInsulThickMm, tabInsul ? tabInsulThickMm : 0.0);
                    // ★★ 2026-08-28 更正：这段注释**描述的是一个已经不做了的做法**。
                    //   它说「传 NaN = 不计横向导热…**不猜**每一格到定温边界的距离：
                    //   猜错会把裕度算大（偏危险侧）」—— 而下一行传的正是 LatLen(i)，
                    //   就是在猜那个距离。**注释警告的那件事，代码在做。**
                    //   ⇒ 现在猜得**有依据**：锚点只取真被按住的格子（见上面 isFixed 那处），
                    //     没有合格锚点就退回只看管孔（保守）。
                    //   ⚠ 这仍是一个**估计**，不是精确解：它把「到最近定温边界的直线距离」
                    //     当成横向导热长度。保守侧的做法（传 NaN）仍在 LocalStability 里可用。
                    var pt = LocalStability.Check(p, res.T[i], jMagAPerMm2[i], m.Thickness[i],
                                                  insMm, LatLen(i));
                    // ★★★★★ 超拟合区间的格子**不能只是跳过**（2026-08-24 修）。
                    //
                    //   跳过之后，报出来的是「剩下那些**凉**格子里最差的」——
                    //   而判不了的恰恰是最热、最可能失稳的那些。于是发散算例上会给出
                    //   一个**看起来很安全**的数：实测 selfcheck B 段「管保温 1 mm」
                    //   （片温 5277 °C）报 **10.8×**，而那一片根本没有一格热区被评过。
                    //   这是**偏乐观**的偏差，正是本项目最忌的方向。
                    //   ⇒ 记下跳过数；只要有跳过，整条就报**判不了**（见下方）。
                    if (double.IsNaN(pt.JStab)) { skipped++; continue; }
                    if (pt.Margin < best) { best = pt.Margin; bi = i; bTab = onTab; }
                }
            }
            Scan(candD, false); Scan(candT, true);
            // ★★ 只要**场里有一格**超出电阻率拟合区间，整条就判不了。
            //
            //   光数「候选里被跳过几个」不够：候选是按 ρe·J²·t·**TCR(T)** 排的，
            //   而 TCR 在拟合区间外会翻号/变小 ⇒ **最热的格子根本进不了前 12**，
            //   既没被评、也没被记成跳过。实测 selfcheck B 段「板厚 ×0.5」
            //   （片温 4361 °C）就这样报出 1.9×，而热区一格没评过。
            //   ⇒ 判据的有效性取决于**场**在不在模型的适用范围内，不取决于候选。
            if (skipped > 0 || hotOutOfRange > 0) bi = -1;
            if (bi >= 0)
            {
                var cb = m.Centroid[bi];
                res.LocalStabMargin = best;
                res.LocalStabRMm = Math.Sqrt(cb.X * cb.X + cb.Z * cb.Z);
                res.LocalStabTempC = res.T[bi];
                res.LocalStabJAPerMm2 = jMagAPerMm2[bi];
                res.LocalStabThickMm = m.Thickness[bi];
                res.LocalStabOnTab = bTab;
                res.LocalStabLatLenMm = LatLen(bi);
            }
        }

        if (iDMax >= 0)
        {
            var cD = m.Centroid[iDMax];
            // 板面在 X–Z 平面（Vec3(cx, yPlane, cz)）⇒ 半径由 X、Z 定，与 Y 无关
            res.DiscMaxXMm = cD.X; res.DiscMaxZMm = cD.Z;
            res.DiscMaxRMm = Math.Sqrt(cD.X * cD.X + cD.Z * cD.Z);
            res.DiscMaxJAPerMm2 = jMagAPerMm2[iDMax];
            res.DiscMaxThickMm = m.Thickness[iDMax];
        }
        if (iTMax >= 0)
        {
            var cT = m.Centroid[iTMax];
            res.TabMaxXMm = cT.X; res.TabMaxZMm = cT.Z;
            res.TabMaxRMm = Math.Sqrt(cT.X * cT.X + cT.Z * cT.Z);
            res.TabMaxJAPerMm2 = jMagAPerMm2[iTMax];
            res.TabMaxThickMm = m.Thickness[iTMax];
        }
        res.QGenDiscW = gD; res.QLossDiscW = lD; res.AreaDiscMm2 = aD;
        res.QGenTabW = gT; res.QLossTabW = lT; res.AreaTabMm2 = aT;
        res.TDiscMeanC = aD > 1e-9 ? tD / aD : double.NaN;
        res.TTabMeanC = aT > 1e-9 ? tT / aT : double.NaN;
        res.TDiscMaxC = double.IsNegativeInfinity(tDMax) ? double.NaN : tDMax;
        res.TTabMaxC = double.IsNegativeInfinity(tTMax) ? double.NaN : tTMax;

        // ★ R48（2026-09-14，Opus 5）：舌端平均温度由按格数平均改为**面积加权**。
        //   为什么：生产网格上舌端单元是整个压接接触面（配方 ③），分级网格上压接段内格子大小不一（锚点两侧细、段中远场粗；
        //   2026-09-14 时还铺了压接细带，配方 ④ —— R48 F 2026-09-15 Opus 5 起缺省不铺，理由照样成立），
        //   按格数平均偏向细格那几列（压接段内边、贴着自由段的那几列）。热导模式下 QToClampW = Σ gBus·(T − T_冷端)、gBus 按面积分摊
        //   ⇒ QToClampW = G·(面积加权均温 − T_冷端)，LineRunner 拿它反推「导热需截面」时两边必须同一个均温口径。
        //   定温模式所有舌端格都钉在夹持温度上，直接取夹持温度（与原来的按格平均逐位相同，不引入面积加权的浮点尾巴）。
        //   ★ 2026-09-14 Opus 5 复审修：面积加权只用在整面接触（ClampCell 非空）上；老口径（ClampCell 空）保持改动前的按格数平均原式，
        //     热导／自由端模式下与改动前逐位相同（记录值：R48ClampRecipeTests 门 (d)，基线树上跑的）。
        if (fullFace)
        {
            int nTab = 0; double aTab = 0, taTab = 0;
            for (int i = 0; i < n; i++)
                if (tabCell[i]) { nTab++; aTab += m.Area[i]; taTab += res.T[i] * m.Area[i]; }
            res.TTabEndMeanC = nTab == 0 ? double.NaN
                             : clamped ? p.BusbarClampTempC
                             : taTab / aTab;
        }
        else
        {
            var tabT = Enumerable.Range(0, n).Where(i => tabCell[i]).Select(i => res.T[i]).ToArray();
            res.TTabEndMeanC = tabT.Length > 0 ? tabT.Average() : double.NaN;
        }
        res.TMaxC = res.T.Max(); res.TMinC = res.T.Min();
        // ★ R48 配方指纹（2026-09-15，Opus 5）：本次实际生效的热解配方。
        // ★ 2026-09-15 Opus 5（审查意见 minor）：原先 res.Recipe 直接取 RecipeFor（按输入预判），再拿与 RecipeFor 同一个式子「核对」—— 自己比自己，
        //   改坏生效分支（例如混合循环提前 continue、孔边界面导度那段提前 return）照样通过。改为**按上面循环里数出来的量记**，再与 RecipeFor 的预判比：
        //   · 分界格混合：一格都没按半径划 ⇒ 不适用；有格因为没有厚度场跳过了混合 ⇒ 关；否则开（真混合了几格另记 InsulBlendCells）；
        //   · 孔边面上定温：有孔边界面、没有一格被钉住、孔边界面导度真有非零；
        //     ★ 2026-09-15 Opus 5（审查意见 minor）：原式「没有孔边界面也记 true」—— 那时管温根本没施加，指纹却说施加在孔边界面上。
        //       改为没有孔边界面 ⇒ false，并记孔边界面数（规则里的 HasHoleBoundary = 数 > 0，生产常量要求为真）；
        //   · 压接整面：真并进压接格的格数 > 0（与网格配方同一个定义：ClampCell 至少一格为真）；
        //   · 表：真建出来的那张表的区间与节点数；
        //   · 压接面上定温（2026-09-15 Opus 5（合并），F 配方 ⑤）：定温模式 = 面导度循环里真走了面上那一支的面数 > 0；
        //     热导与自由端热场里没有定温边界，量不到，记网格口径（ShellMesh.ClampFaceCount，电流场施加面上定电位用的同一个集合与判定）。
        var modeUsed = busG ? ClampBoundaryMode.Conductance : clamped ? ClampBoundaryMode.Fixed : ClampBoundaryMode.Free;
        int nClampFaces = modeUsed == ClampBoundaryMode.Fixed ? clampFaceUsed.Count(b => b) : m.ClampFaceCount();
        res.Recipe = new ThermalRecipe
        {
            InsulBlend = nByRadiusCells == 0 ? InsulBlendState.NotByRadius : nNoFieldCells > 0 ? InsulBlendState.OffNoField : InsulBlendState.On,
            InsulBlendCells = nBlend,
            LossTableLoC = tables.Bare.LoC, LossTableHiC = tables.Bare.HiC, LossTableNodes = tables.Bare.Nodes,
            HoleFaceDirichlet = nHoleFaces > 0 && nHolePinned == 0 && gHole.Any(g => g > 0),
            HoleFaceCount = nHoleFaces,
            ClampMode = modeUsed,
            ClampFullFace = nFullFaceCells > 0,
            ClampFaceDirichlet = nClampFaces > 0,
            ClampFaceCount = nClampFaces,
            HoleAnchorOnCircle = anchorOnCircleUsed,   // 2026-09-23 F6d：按局部热稳定那段真走的一支记
        };
        var rc = res.Recipe;
        var pred = RecipeFor(m, p, holeFaceDirichlet, insulDiscRadiusMm, lossTableHiC, lossTableNodes, holeAnchorOnCircle);
        if (pred.InsulBlend != rc.InsulBlend || pred.LossTableLoC != rc.LossTableLoC || pred.LossTableHiC != rc.LossTableHiC || pred.LossTableNodes != rc.LossTableNodes
            || pred.HoleFaceDirichlet != rc.HoleFaceDirichlet || pred.HoleFaceCount != rc.HoleFaceCount
            || pred.HoleAnchorOnCircle != rc.HoleAnchorOnCircle                                    // 2026-09-23 F6d
            || pred.ClampMode != rc.ClampMode || pred.ClampFullFace != rc.ClampFullFace
            || pred.ClampFaceDirichlet != rc.ClampFaceDirichlet)                                   // 2026-09-15 Opus 5（合并）
            throw new InvalidOperationException("热解配方：按输入的预判（RecipeFor）与求解里数出来的对不上 —— 生效分支与配方记录漂开了，配方指纹不许说假话。"
                + $"预判「{pred.Describe()}」，实际「{rc.Describe()}」");
        return res;
    }

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5）：在网格 <paramref name="m"/>、物性 <paramref name="p"/> 上按 <see cref="Solve"/> 的参数解一次热场**会用哪套热解配方** ——
    /// <see cref="Solve"/> 自己填 <see cref="ShellThermalResult.Recipe"/> 就调这一处（并逐项核对实际用到的量），探针跑前写证据头也调它，不另判一遍。
    /// 参数与 <see cref="Solve"/> 同名同义（<paramref name="lossTableHiC"/>／<paramref name="lossTableNodes"/> 缺省 = 生产表）。
    /// </summary>
    public static ThermalRecipe RecipeFor(ShellMesh m, DesignInputs p, bool holeFaceDirichlet = true, double insulDiscRadiusMm = double.NaN,
                                          double lossTableHiC = double.NaN, int lossTableNodes = 0, bool holeAnchorOnCircle = true)
    {
        bool byRadius = insulDiscRadiusMm > 1e-9;
        // 2026-09-15 Opus 5（合并）：G1 写 RecipeFor 时 G2 的唯一判定 ClampBoundaryOf 还不在，这里原有一份就地的「铜排热导 ≥ 0 ⇒ 热导；夹持温度 ≥ 0 ⇒ 定温」——
        //   与 G2「场按定温解、标定按热导读不许各判各的」同一个病，改调 ClampBoundaryOf（规则逐字相同，结果逐位不变）。
        var mode = ClampBoundaryOf(p);
        double hi = double.IsNaN(lossTableHiC) ? LossTableHiC : lossTableHiC;
        int nodes = lossTableNodes > 0 ? lossTableNodes : LossTableNodes(p.TAmbC, hi);
        // 孔边界面数：与 Solve 组装边界时同一个定义（边界面 B < 0 且带管孔标签）
        int holeFaces = m.Faces.Count(f => f.B < 0 && f.Tag == ShellMesh.TagHole);
        return new ThermalRecipe
        {
            InsulBlend = m.CellCount == 0 || !byRadius ? InsulBlendState.NotByRadius : !m.HasMaterialSource ? InsulBlendState.OffNoField : InsulBlendState.On,   // 2026-09-18 Fable 5.1：同上
            LossTableLoC = p.TAmbC, LossTableHiC = hi, LossTableNodes = Math.Max(8, nodes),        // LossTable 构造时节点数至少 8
            // ★ 2026-09-15 Opus 5（审查意见 minor）：没有孔边界面 ⇒ 管温没施加 ⇒ 记 false（原先记 true，指纹说假话），孔边界面数另记
            HoleFaceDirichlet = holeFaceDirichlet && holeFaces > 0,
            HoleFaceCount = holeFaces,
            ClampMode = mode == ClampBoundary.Conductance ? ClampBoundaryMode.Conductance : mode == ClampBoundary.FixedTemp ? ClampBoundaryMode.Fixed : ClampBoundaryMode.Free,
            // 2026-09-15 Opus 5（审查意见 minor：与网格配方定义不一致）：与 MeshRecipe.ClampFullFace 同一个定义 —— 长度 = 单元数且至少一格为真
            ClampFullFace = m.ClampFullFaceActive,   // 2026-09-15 Opus 5（J 路）：唯一定义
            // 2026-09-15 Opus 5（合并）：F 配方 ⑤ 的预判 = 网格上有压接面（ShellMesh.ClampFaceCount，与两个求解器同一个判定）；面数本身不预判（−1）
            ClampFaceDirichlet = m.ClampFaceCount() > 0,
            HoleAnchorOnCircle = HoleAnchorOnCircleFor(m, holeAnchorOnCircle, holeFaces),   // 2026-09-23 F6d
        };
    }

    /// <summary>2026-09-23（F6d）：局部热稳定的管孔锚点是否取孔圆 —— 开关开、网格上有孔边界面、孔半径有限且为正。Solve 与 RecipeFor 调这一处。</summary>
    internal static bool HoleAnchorOnCircleFor(ShellMesh m, bool holeAnchorOnCircle, int holeFaces)
        => holeAnchorOnCircle && holeFaces > 0 && double.IsFinite(m.HoleRadiusMm) && m.HoleRadiusMm > 0;
}
