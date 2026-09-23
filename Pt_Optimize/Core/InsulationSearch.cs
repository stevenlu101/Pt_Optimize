using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ R48 E（2026-09-15 Opus 5）：**保温搜索求解器** —— 管保温、逐片圆盘保温、逐片舌保温三样都是设计输出（用户 2026-09-14/15），
/// 每层 0.5 mm，按「先能造能用、再比铂重」找出两态（带玻璃稳态、空管到温稳态）都过的保温厚度分布。界面暂不接。
///
/// ══ 变量（全是层号，整数；厚度 = 层号 × 0.5 mm）
///   · 管保温 T：全线一个值（DesignSpec.TubeInsulMm 还是标量，沿管长不等厚本版不做）。
///   · 第 j 片圆盘保温 D_j、舌保温 W_j：取值域 {0} ∪ {1..上界}。0 层 = 不包（裸铂表面，发射率与「包了 0.5 mm」不同，
///     判定在 DesignScreen.FlangeFaceInsulated：&lt; 0.05 mm 算裸露）⇒ 0 层与 1 层之间是物理不连续，所以只能枚举、不能二分。
///   · 三个上界（<see cref="Options.TubeLayerMax"/>、<see cref="Options.DiscLayerMax"/>、<see cref="Options.TabLayerMax"/>）**无出处、需定**，作为选项传入，报告里照印。
///     选中格点落在上界时逐片标「到顶（上界无出处）」—— 到顶不等于无解。
///
/// ══ 外层：管保温按层枚举（不走步）
///   下界 = 闭式：DesignCurrent 升温所需电流**不被管 J 许用截住**的第一层（<see cref="TubeLayerLowerBound"/>）。
///   每层从传入的设计**重新克隆**（不带上一层的任何状态），重算舌片厚与板厚下角（<see cref="Solver.ApplySectionFloor"/>，内部调 DesignSpec.SizeTongue）。
///   可行的层按合计铂重（整线结果的 TotalMassG）比，全部列出，建议取铂重最小的可行层。
///
/// ══ 中层：工作点不动点（每个 T 层两个起点各跑一条链）
///   选择 S（每片 D_j、W_j）→ 整线两态各解一次（**冷启动**：每次都由 DesignSpec.BuildCase 新造算例，不带 WarmStart、不带基线缓存）
///   → 逐片取工作点（接头电流、各端管根、抽热）→ 内层逐片重选 → 新选择 S′ → 再整线……
///   · 收敛：第 r 轮（r ≥ 2）算出的选择与上一轮**逐位相同**。整线解是确定的、结果按选择缓存，所以「连续两轮」不多花一次整线解。
///   · 周期：算出的选择等于更早某一轮（不是上一轮）⇒ 此后必然循环 ⇒ 报「判不了」，循环里的每个选择**都报**，不许随手挑一个。
///   · 起点：全 0 层（L）与全上界（U）。两条链都收敛、终点逐位相同，才写「与起点无关」；否则两套都报，本层不许宣称可行。
///   · 每轮做的一切（整线、管侧响应、内层、闭合）都只由 S 决定 ⇒ 轮映射是 S 的确定函数，这是「与起点无关」论证成立的前提。
///
/// ══ 网格（★ 2026-09-15 Opus 5 审查修改：此前整线导航档是手写的一份，细区半径停在 LineCase 默认 50 mm，内层是 59 mm，两张网格不同族）
///   整线工作点与内层单片网格都调 <see cref="Solver.ApplyCaseMesh"/>（Solver 造算例的同一份配方），细区半径统一取 MeshVerify.RequiredMeshFor(设计).RadiusMm：
///   · 整线网格选项 <see cref="Options.WholeLineMeshMm"/> ≤ 0 = 导航档（尺寸与粗区照 BuildCase，只统一半径与内带 —— 与 Solver 导航遍逐位同配方）；&gt; 0 = 整张自相似加密；
///   · 内层 h = <see cref="Options.InnerMeshMm"/>（粗筛 1.0、复算 0.5）。两张网格只差 h。
///   · 网格偏差：选中格点上「内层抽热 − 整线抽热」按 γ 折成开尔文（γ·ΔQ ÷ (1 + γ·s)）逐片逐态印出 —— 它就是终点判据值里没算准的那一截。
///
/// ══ 内层：冻结工作点，逐片枚举 (D_j, W_j) 格点
///   · 单片解全走生产函数：DesignSpec.BuildCase（格点的保温写进板件，与整线同一个入口）→ <see cref="LineRunner.PlateThermalInputs"/>
///     → <see cref="LineRunner.SolvePlateThermal"/>；网格 <see cref="LineRunner.PlateMeshAnalytic"/>，电位场 <see cref="LineRunner.PlateCurrentField"/>
///     （网格与电位场与保温无关，每层按「全 0 层」算例造一次；σ(T) 耦合开着时冻结电位场不成立 ⇒ 拒算）。
///   · 配方自检（★ 审查修改：此前是手写的兄弟函数、只在第一条均匀选择上跑）：**每一条**整线解上，拿内层同一个 <c>SolvePoint</c>、同一条造网格与电位场的路径
///     （只把 h 换成整线网格），在整线自己的接头电流与管根上重解每片：格数、电位场、抽热、盘峰、舌区峰必须与整线**逐位相同**。
///     非均匀选择上逐位相同 ⇒「本片热输入不读别片保温」「网格与电位场与保温无关」两个前提被门碰到。不同 ⇒ 抛异常，不许往下算。
///
/// ══ 管根闭合（冻结解里管根是输入；两态各一套）
///   · γ（管根对本片抽热的响应，K/W）—— ★ 审查修改：此前锚在「全 0 层、全上界」两个角的整线解上，依赖无出处的上界、也不是局部斜率。
///     现在是**当前选择 S 的管侧响应**：在 S 的整线收敛态（该次整线算例留下的各段两端抽热与邻段端温，只读、不进任何整线解）上，
///     把第 j 片抽热扰动 ±<see cref="Options.GammaProbeW"/>（按生产的 <see cref="LineRunner.SegmentEndDraws"/> 分到所贴各端），
///     调 <see cref="LineRunner.SolveTubeWithDrawsNewton"/>（只解段、段电流照样按控温点反算；段间端温用牛顿弦法解到「到不动点估计 &lt; <see cref="Options.TubeProbeTolK"/>」，
///     雅可比由 <see cref="LineRunner.NeighbourJacobian"/> 在收敛态上差分一次、本选择本态的各片共用。不用无法兰基线那份欠松弛迭代：共用片那处耦合增益近 1，
///     对拍文件 deliverable\R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt 第 13–20 行：带玻璃 194～207 轮才到 0.25 K、空管 300 轮停在 0.30 K（2026-09-15 Opus 5（I 路） 照文件改，原句「一次性实测 60 轮仍没收敛」无文件）；两种解法同一不动点由慢门 R48TubeResponseNewtonGateTests 对拍），
///     逐端量割线 −(管根₊ − 管根₋)/(2ΔQ)。γ_热 = 工作点上较热那一端的割线，γ_冷 = 较冷那一端的；端片两者相同。
///     每次用前自检：不扰动时管侧单解必须**逐位**复现整线管根（证明管侧单解就是整线的段解配方）。
///   · s（本片抽热对管根，W/K）、κ（盘峰／舌区峰对管根，K/K）：在选中格点上把管根抬 <see cref="Options.RootProbeK"/> 再解一次量出，全格点共用。
///   · 闭合（<see cref="Close"/>，线性、隐式解）：ΔQ₀ = 抽热冻结(格点) − 抽热冻结(选中)，
///       Δ较热端管根 = −γ_热·ΔQ₀ ÷ (1 + γ_热·s)，Δ较冷端管根 = −γ_冷·ΔQ₀ ÷ (1 + γ_热·s)，净流入 = 抽热冻结 + s·Δ较热端，峰值 += κ·Δ较热端。
///     选中格点自己 Δ = 0 ⇒ 选中点的判据值就是工作点上的冻结解；闭合只影响「别的格点排第几」。
///   · 退路（确定性）：选中格点本身判不了、或 s/κ 量不出 ⇒ 本片本态「无闭合」（全格点按冻结管根评，Δ = 0），报告标出原因；
///     γ 量不出或不为正 ⇒ 本轮判不了（那是管侧段解失败，不是格点问题）。
///   · 闭合核对（★ 审查修改：此前没验）：第 r 轮闭合对「第 r+1 轮选择」的预测（两端管根、净流入、盘峰、舌区峰）对第 r+1 轮的实值逐片逐态印出，
///     根与峰对整线（管根）与内层同一张网格（抽热、峰）的实值比；预测修正与实际修正之差超过界限就标 ⚠（界限见 <see cref="Options.ClosureFlagK"/>，跑前写死）。
///     误差里含邻片同时在变的串扰，照印不拆。
///
/// ══ 判据（默认评估函数 <see cref="DefaultCriteria"/>，可注入）
///   热偶读数基准：端片 = 本段设定；共用片 = 两侧设定的对数平均（开尔文里取），取 <see cref="ThermocoupleBasis.At(LineResult,int)"/> 的 RefC（用户 2026-09-14）。
///   · 热侧：<see cref="ThermocoupleBasis.HotSideK"/>（max(盘峰, 舌区峰, 管根较热端) − 基准）≤ 整线算例的 LineCase.HotOverTcMaxK（= 参数表「最热铂高出热偶读数 允许值」；管根较热端 = 整线 FlangeOut.TRootC）；
///   · 冷侧：<see cref="ThermocoupleBasis.ColdSideK"/>（基准 − 管根较冷端）≤ 整线算例的 LineCase.ColdUnderTcMaxK（= 参数表「管根低于热偶读数 允许值」）；
///   · 管孔净流入 &gt; 0（= 本片从管子抽热为正）。
///   归一裕度：热侧、冷侧 = (限值 − 值)/限值；净流入 = γ_均 × 净流入 ÷ 冷侧限值（把抽热折成它压低管根的开尔文，与冷侧同一把尺；γ_均 = 各端割线均值）。
///   ★ 2026-09-15 Opus 5（合并）：B 已合入 —— 基准、热侧与冷侧的式子、两个限值都改调 B 的唯一实现（ThermocoupleBasis.At／HotSideK／ColdSideK、LineCase 的两个限值），
///     删掉了本文件原来那份「max(…) − 基准」「基准 − 管根」与 Options 里的两个 5 K（新口径判据的第二份写法，E 路 open issue）；裕度量化步长的分母也改读算例限值。
///     评估函数仍可注入（默认 = <see cref="DefaultCriteria"/>）；它评的是闭合后的格点量，所以调式子、不调整条判据构造（LineRunner.ThermocoupleChecks 要一次整线解）。
///     本合并树的 E 以 r48_E 最终版（stream.patch 09:03，含 E 自己的审查修改）为底，上面这几处合并改动是在它上面重做的。
///   ★★★★★ K 路（2026-09-15，Opus 5）：**分工况判据已落地**（原注「尚未落地」作废）。用户 2026-09-15 原话：「比如空管时铂过热那条也不该按 5 ℃ 卡，只要J&lt;11即可」。
///     · 哪一态卡哪几条只在整线判据的分工况表 <see cref="LineResult.RequiredByState"/>（与 LineRunner.Judge 同一份）；本类的每一项通过 <see cref="Criterion.LineKey"/> 对到那张表，
///       <see cref="Criterion.Kind"/> 查表得出，本类不自带清单。现表：空管到温稳态的热侧、冷侧、净流入只作参考（冷侧、净流入的归属是主会话按原话的解读，见 LineResult 注释）。
///     · 参考项照算照印，不进可行集、不进排序键（<see cref="PointOutcome.Feasible"/>／<see cref="PointOutcome.MinNormMargin"/>／单态可行计数）。
///     · 场的有效性两态都保留：任一态本格点单片场判不了 ⇒ 整格点判不了；整线任一态场判不了（<see cref="LineResult.FieldUndeterminedReasons"/>）⇒ 该轮判不了。
///     · 本态逐格点没有卡交付的项时，管侧响应（γ）量不出、闭合不成立都不再挡整轮／打成判不了，只让参考值不闭合。
///     · 层可不可行另读终点选择上的整线两态 <see cref="LineResult.AllOk"/>（审查 P1-3：管 J、截面 J、升温、舌片自由段、圆盘盖得住管孔与场的有效性都在里面），见 <see cref="FinalLineVerdict"/>。
///
/// ══ 选择规则（与枚举次序无关，印在输出上）：见 <see cref="SelectionRuleText"/>。
///   裕度量化步长默认 = 耦合容差 ÷ 较小限值（0.25 K ÷ 5 K = 0.05）：比耦合停机不确定度细的差别不算差别（★ 审查修改：旧默认 0.01 → 新默认 0.05，待用户确认）。
///   （2026-09-15 Opus 5（合并）：「较小限值」取两态整线算例的 LineCase 限值，见 <see cref="Options.EffectiveMarginQuantum"/> 与 <see cref="LimitsOf"/>。）
/// </summary>
public static class InsulationSearch
{
    /// <summary>每层厚度 mm（用户 2026-09-14「每层 0.5 mm」）。</summary>
    public const double LayerMm = 0.5;
    public static double Mm(int layers) => layers * LayerMm;

    /// <summary>
    /// ★ R48 L（2026-09-17，Opus 5）：**把 mm 折成「几层」的唯一一份写法**，给界面与安装报告用。
    ///
    /// 现场是**一层一层缠**的（每层 <see cref="LayerMm"/> mm），而交付数一直只印 mm ——
    /// 5.5 mm 是几层要工程师自己心算；印成 5.1 mm 时更糟：那个数根本缠不出来。
    /// 求解器的舌保温图纸格已改成同一个 <see cref="LayerMm"/>（<c>SolverOptions.QuantInsulMm</c>，
    /// 用户 2026-09-17「求解器默认格子改成 0.5」），所以解出来的值要么是整数层，要么是**裸舌**（不到一层 = 什么都不缠）。
    /// 既不是整数层也不是裸舌 ⇒ 照实印「不在层上」，**不许四舍五入蒙混过去**。
    /// </summary>
    public static string LayersText(double mm)
        => double.IsNaN(mm) ? "—"
         : mm < LayerMm - 1e-9 ? "裸舌 0 层"
         : Math.Abs(mm / LayerMm - Math.Round(mm / LayerMm)) < 1e-9 ? $"{Math.Round(mm / LayerMm):0} 层"
         : "**不在层上**";

    /// <summary>逐片的「几层」，与 mm 那一串同序。</summary>
    public static string LayersText(System.Collections.Generic.IReadOnlyList<double> mm)
        => mm is null || mm.Count == 0 ? "—" : string.Join("/", mm.Select(LayersText));

    public const string StateGlass = "带玻璃稳态", StateEmpty = "空管到温稳态";
    public static readonly string[] StateNames = { StateGlass, StateEmpty };

    public const string HotName = "最热处高出热偶读数基准（盘峰、舌区峰、管根取大）";
    public const string ColdName = "管根低于热偶读数基准";
    public const string InflowName = "管孔净流入须为正";

    /// <summary>选择规则全文（报告照印）。{0} 是裕度量化步长。</summary>
    /// K 路（2026-09-15，Opus 5；审查 P1-9）：可行集与排序键只取**卡交付**的项；哪一态卡哪几条不在本文里写死，由报告头按整线判据的分工况表逐态印出。
    public const string SelectionRuleText =
        "每片的可行集 = 两态（带玻璃稳态、空管到温稳态）的场都判得了、且卡交付的判据全过的格点（每一态卡哪几条只看整线判据的分工况表，见报告头；只作参考的项照算照印，不进可行集、不进排序）。在可行集里按全序取第一名："
        + "第一看归一最小裕度（两态所有卡交付的判据里最小的那个，按 {0} 向下取整量化），大者优先；第二看总层数（圆盘层 + 舌层），少者优先；第三看圆盘层数，小者优先；第四看舌层数，小者优先。"
        + "可行集为空时，按同一全序取「最近格点」（判得出的格点里归一最小裕度最大的），并报缺口。"
        + "判不出的格点（任一态的场没收敛、越过熔点、保温分界判不了，或卡交付的判据值不是数）排在最后、不进可行集。"
        + "这四条对不同格点构成全序（第四条之后不会再并列），所以结果与枚举次序、并行次序无关。"
        + "层可不可行另要读终点选择上的整线结果：两态都要外层耦合收敛、本态必备判据齐全且全过、场判得了（整线判据表，与交付判定同一份）。";

    // ════════════════════════════════════════════════════════════════
    //  通用骨架（与物理无关；快门用合成模型直接验它）
    // ════════════════════════════════════════════════════════════════

    /// <summary>一次选择：每片的圆盘层号与舌层号。按值比较（逐位）。</summary>
    public sealed class Selection : IEquatable<Selection>
    {
        public readonly int[] Disc;
        public readonly int[] Tab;
        public Selection(int[] disc, int[] tab)
        {
            if (disc is null || tab is null || disc.Length != tab.Length) throw new ArgumentException("圆盘与舌的层号数组要一样长（= 片数）");
            Disc = (int[])disc.Clone(); Tab = (int[])tab.Clone();
        }
        public int PlateCount => Disc.Length;
        public static Selection Uniform(int plates, int disc, int tab)
            => new(Enumerable.Repeat(disc, plates).ToArray(), Enumerable.Repeat(tab, plates).ToArray());
        public string Key => string.Join(",", Disc) + "|" + string.Join(",", Tab);
        public bool Equals(Selection? o) => o is not null && Disc.SequenceEqual(o.Disc) && Tab.SequenceEqual(o.Tab);
        public override bool Equals(object? obj) => Equals(obj as Selection);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
        /// <summary>「圆盘 a/b/c/d 层、舌 e/f/g/h 层」</summary>
        public string Describe() => $"圆盘 {string.Join("/", Disc)} 层、舌 {string.Join("/", Tab)} 层";
        public string DescribeMm() => $"圆盘 {string.Join("/", Disc.Select(v => Mm(v).ToString("0.0")))} mm、舌 {string.Join("/", Tab.Select(v => Mm(v).ToString("0.0")))} mm";
    }

    /// <summary>一条判据在一个格点、一个工况下的值。</summary>
    public sealed class Criterion
    {
        public string Name = "";
        public string State = "";
        public double Value, Limit;
        public string Unit = "";
        public bool LessIsBetter;
        /// <summary>true = 严格不等式（净流入 &gt; 0）：归一裕度必须 &gt; 0 才算过。</summary>
        public bool Strict;
        /// <summary>归一裕度：正 = 过（严格判据要 &gt; 0），越大越好。NaN ⇒ 判不了。</summary>
        public double NormMargin;
        public bool Pass => Strict ? NormMargin > 0 : NormMargin >= 0;
        /// <summary>
        /// ★ K 路（2026-09-15，Opus 5）：本项对应整线判据表里的哪一条（<see cref="LineResult.Key"/> 前缀，默认评估函数写入）。空 = 不对应（合成模型），按卡交付算。
        /// </summary>
        public string LineKey = "";
        /// <summary>K 路（2026-09-15，Opus 5）：本项属于空管到温稳态（由 <see cref="EvaluatePoint"/> 按解出本态的整线算例 LineCase.EmptyTube 盖上；评估函数自己写的会被覆盖）。</summary>
        public bool EmptyTube;
        /// <summary>
        /// ★★★★★ K 路（2026-09-15，Opus 5）：本项在本工况下卡不卡交付 —— **只调整线判据的分工况表** <see cref="LineResult.StateKindOf"/>（与 LineRunner.Judge 同一份），本类不自带清单。
        /// 参考项照常算、照常印，不进可行集、不进排序键（<see cref="PointOutcome.Feasible"/>／<see cref="PointOutcome.MinNormMargin"/>）。
        /// </summary>
        public CheckKind Kind => LineKey.Length == 0 ? CheckKind.HardSafety : (LineResult.StateKindOf(LineKey, EmptyTube) ?? CheckKind.HardSafety);
        public bool IsReference => Kind == CheckKind.Reference;
        public string Show() => $"{State}·{Name} {Value:+0.000;-0.000} {Unit}（限 {(LessIsBetter ? "≤" : Strict ? ">" : "≥")} {Limit:0.###}，归一裕度 {NormMargin:+0.000;-0.000}{(IsReference ? "，本态只作参考" : "")}）";
    }

    /// <summary>一个格点（两态合起来）的判定。</summary>
    public sealed class PointOutcome
    {
        public int Disc, Tab;
        public bool Determined = true;
        public string Why = "";
        public List<Criterion> Terms = new();
        /// <summary>物理模型挂的明细（闭合量等），骨架不读。</summary>
        public object? Detail;
        /// <summary>K 路（2026-09-15，Opus 5）：卡交付的项（按 <see cref="Criterion.Kind"/>，即整线判据的分工况表）。</summary>
        public IEnumerable<Criterion> HardTerms => Terms.Where(t => !t.IsReference);
        /// <summary>
        /// K 路（2026-09-15，Opus 5；审查 P1-9）：只看卡交付的项（原来取全部项 ⇒ 空管态降为参考的三项照样决定可行）。
        /// 一条卡交付的项都没有 ⇒ 不算可行（逐格点说不出「过」，不许空集恒真）。场判不了的格点在评估时已置 <see cref="Determined"/> = false。
        /// </summary>
        public bool Feasible => Determined && HardTerms.Any() && HardTerms.All(t => t.Pass);
        /// <summary>K 路（2026-09-15，Opus 5；审查 P1-9）：排序键只取卡交付的项（参考项不进排序）。</summary>
        public double MinNormMargin => !Determined || !HardTerms.Any() ? double.NegativeInfinity : HardTerms.Min(t => t.NormMargin);
        /// <summary>缺口最大的卡交付项（K 路 2026-09-15 Opus 5：参考项不算缺口）。</summary>
        public Criterion? Worst => HardTerms.OrderBy(t => t.NormMargin).ThenBy(t => t.State, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>一片的选择结果。</summary>
    public sealed class PlateChoice
    {
        public int Plate;
        public int Evaluated, FeasibleCount, UndeterminedCount;
        /// <summary>
        /// ★ 2026-09-15 Opus 5（小规模实跑后补）：**单态可行集大小** —— 判得出的格点里，该工况（Criterion.State）的判据全过的个数。
        /// 两态可行集为空时，靠它分清「两态各自有窗口但不相交」与「某一态单独就没有窗口」（对策不同：前者要动几何或工况口径，后者先查那一态）。
        /// ★ K 路（2026-09-15，Opus 5；审查 P1-9）：只数**卡交付**的项；某一态在本片的项全是参考项（空管到温稳态）⇒ 不进这张表、进 <see cref="ReferenceOnlyStates"/>
        ///   （原来空管态三项照样按「全过」数，空管态恒 0 会印成「空管单态没有窗口」—— 按新口径那一态根本不逐格点卡）。
        /// </summary>
        public Dictionary<string, int> FeasibleByState = new();
        public int FeasibleIn(string state) => FeasibleByState.TryGetValue(state, out int v) ? v : 0;
        /// <summary>K 路（2026-09-15，Opus 5）：本片各格点上项全是参考项的工况（逐格点只过滤场判不了的格点，不卡判据值）。</summary>
        public string[] ReferenceOnlyStates = Array.Empty<string>();
        public PointOutcome Chosen = null!;
        public bool IsFeasible => Chosen.Feasible;
        public PointOutcome[] All = Array.Empty<PointOutcome>();
    }

    private static long Quantize(double m, double quantum)
    {
        if (double.IsNaN(m) || double.IsNegativeInfinity(m)) return long.MinValue;
        if (double.IsPositiveInfinity(m)) return long.MaxValue;
        double v = Math.Floor(m / quantum);
        if (v > 1e15) return (long)1e15;
        if (v < -1e15) return (long)-1e15;
        return (long)v;
    }

    /// <summary>
    /// 选择全序：返回 &lt; 0 表示 a 排在 b 前面（更好）。规则见 <see cref="SelectionRuleText"/>。
    /// 对不同格点（圆盘层或舌层不同）永不返回 0 ⇒ 最优格点唯一、与枚举次序无关。
    /// </summary>
    public static int Compare(PointOutcome a, PointOutcome b, double marginQuantum)
    {
        if (!(marginQuantum > 0)) throw new ArgumentOutOfRangeException(nameof(marginQuantum), "裕度量化步长必须为正");
        int c;
        if ((c = b.Determined.CompareTo(a.Determined)) != 0) return c;
        if ((c = b.Feasible.CompareTo(a.Feasible)) != 0) return c;
        if ((c = Quantize(b.MinNormMargin, marginQuantum).CompareTo(Quantize(a.MinNormMargin, marginQuantum))) != 0) return c;
        if ((c = (a.Disc + a.Tab).CompareTo(b.Disc + b.Tab)) != 0) return c;
        if ((c = a.Disc.CompareTo(b.Disc)) != 0) return c;
        return a.Tab.CompareTo(b.Tab);
    }

    /// <summary>
    /// 逐片选点：按 <paramref name="points"/> 给的次序（任意）并行评估每个格点，再按全序 <see cref="Compare"/> 取第一名。
    /// 评估结果按下标落位，取最优只看全序 ⇒ 结果与次序、并行调度无关（快门打乱次序逐位比）。
    /// </summary>
    public static PlateChoice ChoosePlate(int plate, IReadOnlyList<(int Disc, int Tab)> points,
                                          Func<int, int, PointOutcome> evaluate,
                                          int maxDegreeOfParallelism, double marginQuantum,
                                          CancellationToken cancel = default)
    {
        if (points is null || points.Count == 0) throw new ArgumentException("没有格点可选", nameof(points));
        if (points.Distinct().Count() != points.Count) throw new ArgumentException("格点有重复", nameof(points));
        var outs = new PointOutcome[points.Count];
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism), CancellationToken = cancel };
        Parallel.For(0, points.Count, po, i =>
        {
            var (d, w) = points[i];
            var o = evaluate(d, w) ?? throw new InvalidOperationException($"片{plate} 格点 ({d},{w}) 的评估返回了空");
            o.Disc = d; o.Tab = w;
            // 判据值不是数 ⇒ 判不了（不许让 NaN 在比较里悄悄排到某个位置）
            // K 路（2026-09-15，Opus 5）：只看卡交付的项 —— 参考项算不出数照印「不是数」，不把格点打成判不了（场判不了另由评估函数置 Determined）
            if (o.Determined && (o.Terms.Count == 0 || o.HardTerms.Any(t => double.IsNaN(t.NormMargin))))
            {
                o.Determined = false;
                o.Why = string.IsNullOrEmpty(o.Why) ? "判据值不是数" : o.Why;
            }
            outs[i] = o;
        });
        var best = outs[0];
        for (int i = 1; i < outs.Length; i++)
            if (Compare(outs[i], best, marginQuantum) < 0) best = outs[i];
        return new PlateChoice
        {
            Plate = plate, Evaluated = outs.Length, All = outs, Chosen = best,
            FeasibleCount = outs.Count(o => o.Feasible),
            UndeterminedCount = outs.Count(o => !o.Determined),
            // K 路（2026-09-15，Opus 5）：只数卡交付的项；有卡交付项的工况即使 0 个全过也要进表（印 0，不是「没有这一态」）
            FeasibleByState = outs.SelectMany(o => o.HardTerms.Select(t => t.State)).Distinct()
                                  .ToDictionary(sn => sn, sn => outs.Count(o => o.Determined
                                      && o.HardTerms.Any(t => t.State == sn)
                                      && o.HardTerms.Where(t => t.State == sn).All(t => t.Pass))),
            ReferenceOnlyStates = outs.SelectMany(o => o.Terms).GroupBy(t => t.State).Where(g => g.All(t => t.IsReference))
                                      .Select(g => g.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>给定上界的全部格点（圆盘 0..dMax × 舌 0..wMax），规范次序。</summary>
    public static List<(int Disc, int Tab)> GridPoints(int discMax, int tabMax)
    {
        var l = new List<(int, int)>((discMax + 1) * (tabMax + 1));
        for (int d = 0; d <= discMax; d++)
            for (int w = 0; w <= tabMax; w++) l.Add((d, w));
        return l;
    }

    /// <summary>不动点的一轮：从选择 From 出发（整线 → 逐片选），给出 Next。</summary>
    public sealed class RoundRecord
    {
        public int Round;
        public Selection From = null!;
        public Selection? Next;
        public bool LineOk = true;
        public string Why = "";
        public PlateChoice[] Choices = Array.Empty<PlateChoice>();
        public object? Detail;
        public double Seconds;
    }

    public enum ChainStatus { Converged, Cycle, NotConverged, Undetermined }

    public static string StatusText(ChainStatus s) => s switch
    {
        ChainStatus.Converged => "收敛",
        ChainStatus.Cycle => "判不了（选择进入周期）",
        ChainStatus.NotConverged => "判不了（轮数用完仍未收敛）",
        _ => "判不了（整线或工作点解不出）",
    };

    public sealed class ChainResult
    {
        public string StartName = "";
        public Selection Start = null!;
        public ChainStatus Status;
        public Selection? Final;
        public List<Selection> Sequence = new();
        public List<Selection> CycleMembers = new();
        public List<RoundRecord> Rounds = new();
        public string Why = "";
        /// <summary>收敛时终点那一轮（其 Choices 就是终点工作点上的判定）。</summary>
        public RoundRecord? FinalRound => Status == ChainStatus.Converged && Rounds.Count > 0 ? Rounds[^1] : null;
    }

    /// <summary>
    /// 不动点迭代（与物理无关）。<paramref name="round"/>(r, S) 必须是 S 的**确定函数**（同一 S 永远给同一 Next）——
    /// 物理模型靠冷启动整线解 + 按选择缓存保证这一点。规则见类注释「中层」。
    /// </summary>
    public static ChainResult FixedPoint(string startName, Selection start, Func<int, Selection, RoundRecord> round,
                                         int maxRounds, Action<RoundRecord>? onRound = null, CancellationToken cancel = default)
    {
        var res = new ChainResult { StartName = startName, Start = start };
        res.Sequence.Add(start);
        for (int r = 1; r <= maxRounds; r++)
        {
            cancel.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var rec = round(r, res.Sequence[^1]);
            rec.Round = r; rec.From = res.Sequence[^1];
            if (rec.Seconds <= 0) rec.Seconds = sw.Elapsed.TotalSeconds;
            res.Rounds.Add(rec);
            onRound?.Invoke(rec);
            if (!rec.LineOk || rec.Next is null)
            {
                res.Status = ChainStatus.Undetermined;
                res.Why = $"第 {r} 轮：" + (string.IsNullOrEmpty(rec.Why) ? "没有给出下一个选择" : rec.Why);
                return res;
            }
            var next = rec.Next;
            if (r >= 2 && next.Equals(res.Sequence[^1]))
            {
                res.Sequence.Add(next);
                res.Status = ChainStatus.Converged; res.Final = next;
                res.Why = $"第 {r - 1}、{r} 轮算出的选择逐位相同";
                return res;
            }
            int k = res.Sequence.FindIndex(s => s.Equals(next));
            if (k >= 0 && k <= res.Sequence.Count - 2)
            {
                res.CycleMembers = res.Sequence.Skip(k).ToList();
                res.Sequence.Add(next);
                res.Status = ChainStatus.Cycle;
                res.Why = $"第 {r} 轮算出的选择回到第 {k} 轮{(k == 0 ? "（起点）" : "")}的选择 ⇒ 周期 {res.CycleMembers.Count}，此后必然循环；循环里的选择全部列出";
                return res;
            }
            res.Sequence.Add(next);
        }
        res.Status = ChainStatus.NotConverged;
        res.Why = $"{maxRounds} 轮用完，选择仍在变";
        return res;
    }

    /// <summary>两端起点的结论。</summary>
    public sealed class StartsOutcome
    {
        public ChainResult Low = null!, High = null!;
        public bool StartIndependent => Low.Status == ChainStatus.Converged && High.Status == ChainStatus.Converged
                                        && Low.Final!.Equals(High.Final);
        public Selection? Final => StartIndependent ? Low.Final : null;
        /// <summary>与起点无关且终点每片都在可行集里。</summary>
        /// <summary>与起点无关且终点每片都在可行集里（逐片冻结解上的判定；层可不可行还要读终点整线结果，见 <see cref="LayerResult.Feasible"/>）。</summary>
        public bool Feasible => StartIndependent && Low.FinalRound!.Choices.All(c => c.IsFeasible);
        public string Verdict()
        {
            if (StartIndependent)
                return Feasible ? "两端起点收敛到同一选择，每片都在可行集里 ⇒ 与起点无关、逐片可行（层可不可行另看终点整线判定）"
                                : "两端起点收敛到同一选择（与起点无关），但有片的可行集为空 ⇒ 不可行，终点是最近格点";
            if (Low.Status == ChainStatus.Converged && High.Status == ChainStatus.Converged)
                return "两端起点各自收敛，但终点不同 ⇒ 结果依赖起点，不许宣称可行；两套都报";
            return $"起点全 0 层：{StatusText(Low.Status)}；起点全上界：{StatusText(High.Status)} ⇒ 与起点无关证不出，不许宣称可行";
        }
    }

    /// <summary>两端起点各跑一条链（可并行；round 必须线程安全且确定）。</summary>
    public static StartsOutcome RunStarts(Selection low, Selection high, Func<int, Selection, RoundRecord> round,
                                          int maxRounds, bool parallel, Action<string, RoundRecord>? onRound = null,
                                          CancellationToken cancel = default)
    {
        ChainResult? a = null, b = null;
        if (parallel)
        {
            var ta = Task.Factory.StartNew(() => FixedPoint("起点全 0 层", low, round, maxRounds, r => onRound?.Invoke("起点全 0 层", r), cancel),
                                           cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var tb = Task.Factory.StartNew(() => FixedPoint("起点全上界", high, round, maxRounds, r => onRound?.Invoke("起点全上界", r), cancel),
                                           cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task.WaitAll(ta, tb);
            a = ta.Result; b = tb.Result;
        }
        else
        {
            a = FixedPoint("起点全 0 层", low, round, maxRounds, r => onRound?.Invoke("起点全 0 层", r), cancel);
            b = FixedPoint("起点全上界", high, round, maxRounds, r => onRound?.Invoke("起点全上界", r), cancel);
        }
        return new StartsOutcome { Low = a, High = b };
    }

    // ════════════════════════════════════════════════════════════════
    //  物理模型：整线工作点 + 管侧响应 + 单片冻结解 + 线性闭合
    // ════════════════════════════════════════════════════════════════

    /// <summary>评估函数看到的一片、一态的量（已闭合）。</summary>
    public sealed class PlateStateView
    {
        public int Plate;
        public bool Shared;
        public int State;
        public string StateName = "";
        /// <summary>K 路（2026-09-15，Opus 5）：本态是空管到温稳态（取自解出本态的整线算例 LineCase.EmptyTube，不按态号猜）。</summary>
        public bool EmptyTube;
        /// <summary>热偶读数基准 °C。</summary>
        public double ReferenceC;
        /// <summary>热侧、冷侧限值 K —— 取自本态整线算例 LineCase.HotOverTcMaxK／ColdUnderTcMaxK（2026-09-15 Opus 5（合并）：与整线判据同一份，原 Options 里另有两个 5 K；U 路 2026-09-18：那两个又只从参数表的温差预算读）。</summary>
        public double HotLimitK = double.NaN, ColdLimitK = double.NaN;
        /// <summary>管孔净流入 W（正 = 本片从管子抽热）。</summary>
        public double NetInflowW;
        public double DiscPeakC, TabPeakC;
        /// <summary>本片所贴各端管根里较热的／较冷的 °C（已闭合）。</summary>
        public double RootHotC, RootColdC;
        /// <summary>γ 各端割线均值 K/W（净流入归一用）。</summary>
        public double GammaKPerW;
        /// <summary>较热端／较冷端的 γ K/W（闭合用）。</summary>
        public double GammaHotKPerW, GammaColdKPerW;
        /// <summary>闭合给的较热端／较冷端管根修正 K（选中格点为 0）。</summary>
        public double RootShiftK, RootColdShiftK;
        /// <summary>false = 本片本态「无闭合」（退路，见类注释），<see cref="ClosureNote"/> 写原因。</summary>
        public bool Closed = true;
        public string ClosureNote = "";
    }

    /// <summary>可注入的评估函数：给一片一态的量，回判据清单（归一裕度正 = 过）。</summary>
    public delegate List<Criterion> PlateCriteria(PlateStateView v, Options o);

    /// <summary>
    /// 默认评估函数（新口径，见类注释「判据」）。
    /// ★ 2026-09-15 Opus 5（合并）：式子改调 B 的唯一实现 <see cref="ThermocoupleBasis.HotSideK"/>／<see cref="ThermocoupleBasis.ColdSideK"/>，
    ///   限值读 <see cref="PlateStateView.HotLimitK"/>／<see cref="PlateStateView.ColdLimitK"/>（= 整线算例 LineCase 的两个限值）；有限输入下数逐位不变（取大的次序同 ThermocoupleBasis.HottestOf），
    ///   输入里有 NaN 时热侧仍是 NaN（原式 Math.Max 传 NaN，HotSideK 先查 NaN）。<paramref name="o"/> 保留在签名里（可注入的评估函数可能要读选项）。
    /// </summary>
    /// ★ K 路（2026-09-15，Opus 5）：每项写上它对应的整线判据（<see cref="PlateTerms"/>），卡不卡交付由 <see cref="Criterion.Kind"/> 查整线的分工况表 —— 本函数不判工况。
    public static List<Criterion> DefaultCriteria(PlateStateView v, Options o)
    {
        double hot = ThermocoupleBasis.HotSideK(v.ReferenceC, v.DiscPeakC, v.TabPeakC, v.RootHotC);
        double cold = ThermocoupleBasis.ColdSideK(v.ReferenceC, v.RootColdC);
        return new List<Criterion>
        {
            new() { Name = HotName, LineKey = LineKeyOf(HotName), State = v.StateName, EmptyTube = v.EmptyTube, Value = hot, Limit = v.HotLimitK, Unit = "K", LessIsBetter = true,
                    NormMargin = (v.HotLimitK - hot) / v.HotLimitK },
            new() { Name = ColdName, LineKey = LineKeyOf(ColdName), State = v.StateName, EmptyTube = v.EmptyTube, Value = cold, Limit = v.ColdLimitK, Unit = "K", LessIsBetter = true,
                    NormMargin = (v.ColdLimitK - cold) / v.ColdLimitK },
            new() { Name = InflowName, LineKey = LineKeyOf(InflowName), State = v.StateName, EmptyTube = v.EmptyTube, Value = v.NetInflowW, Limit = 0, Unit = "W", LessIsBetter = false, Strict = true,
                    NormMargin = v.GammaKPerW > 0 ? v.GammaKPerW * v.NetInflowW / v.ColdLimitK : double.NaN },
        };
    }

    /// <summary>
    /// ★ K 路（2026-09-15，Opus 5）：默认评估函数的三项各对应整线判据表里哪一条（名字 → <see cref="LineResult.Key"/>）。
    /// 只是「这一项就是那一条判据的逐片值」的对应关系，**不含工况**：哪一态卡哪几条只在 <see cref="LineResult.RequiredByState"/>。
    /// </summary>
    public static readonly (string Name, string LineKey)[] PlateTerms =
    {
        (HotName, LineResult.Key.HotOverTc),
        (ColdName, LineResult.Key.ColdUnderTc),
        (InflowName, LineResult.Key.NetFlux),
    };

    private static string LineKeyOf(string name) => PlateTerms.First(p => p.Name == name).LineKey;

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：本工况下默认评估函数的项里有没有卡交付的（查整线的分工况表）。没有 ⇒ 这一态逐格点只作参考：
    /// 管侧响应（γ）量不出不再把整轮打成判不了（γ 只用于闭合参考值），闭合不成立时按冻结管根报参考值。评估函数被注入时一律按「有」（旧行为，保守）。
    /// </summary>
    public static bool StateHasHardPlateTerms(bool emptyTube, Options o)
        => o.Criteria != DefaultCriteria
           || PlateTerms.Any(p => LineResult.StateKindOf(p.LineKey, emptyTube) is not CheckKind.Reference);

    public sealed class Options
    {
        /// <summary>要算的管保温层号（显式给）；null ⇒ 从闭式下界逐层到 <see cref="TubeLayerMax"/>。低于闭式下界的层照样列出但标「升温电流被截住」、不算。</summary>
        public int[]? TubeLayers;
        /// <summary>管保温层号上界（40 层 = 20 mm）。⚠ **无出处、需定**。</summary>
        public int TubeLayerMax = 40;
        /// <remarks>
        /// ★ 2026-09-17（Opus 5）曾改成 <see cref="WrapLimits.MaxTurnsAtJoint"/>（20 层 = 10 mm，出处是用户当日现场那句「20 圈以下」）。
        /// ★ **2026-09-18（Opus 5，用户当日「还是只给材质保温厚度方案就行」）：退回 §0.-11 前的值 40。**
        ///   「缠得出来」已由硬安全线降为参考行（见 <see cref="WrapLimits"/>）⇒ 它不再给这条搜索上界当出处，
        ///   这条上界因此退回原来那个无出处的 40 —— 照实标，不假装它有出处。
        /// </remarks>
        /// <summary>圆盘保温层号上界（40 层 = 20 mm）。⚠ **无出处、需定**（2026-09-18 退回，见 remarks）。</summary>
        public int DiscLayerMax = 40;
        /// <summary>舌保温层号上界（40 层 = 20 mm）。⚠ **无出处、需定**。</summary>
        public int TabLayerMax = 40;
        /// <summary>内层单片网格 h mm（粗筛 1.0、复算 0.5）。必须为正。</summary>
        public double InnerMeshMm = 1.0;
        /// <summary>整线工作点的网格 h mm；≤ 0 = 导航档（Solver.ApplyCaseMesh 的导航支：尺寸照 BuildCase，细区半径统一）。</summary>
        public double WholeLineMeshMm = 0;
        /// <summary>整线外层耦合容差 K（deliverable\R48_耦合续跑_2026-09-14.txt：0.25 K 可用）。</summary>
        public double CoupleTolK = 0.25;
        public int CoupleMaxRounds = 4000;
        /// <summary>内层单片解、管侧响应的并发度。</summary>
        public int MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2);
        /// <summary>各 T 层是否并行跑（整线解本身单线程，层间并行主要省整线的墙钟）。</summary>
        public bool LayersInParallel;
        /// <summary>两端起点的链是否并行跑。</summary>
        public bool StartsInParallel = true;
        public int MaxFixedPointRounds = 8;
        /// <summary>
        /// 归一裕度的量化步长；≤ 0（默认）⇒ 取 <see cref="EffectiveMarginQuantum"/> = 耦合容差 ÷ 较小限值（0.25 ÷ 5 = 0.05）。
        /// ★ 2026-09-15 Opus 5 审查修改：旧默认 0.01（= 0.05 K，比耦合容差 0.25 K 细 5 倍 ⇒ 近乎并列的格点实际由耦合噪声定先后）→ 新默认按耦合容差折算。⚠ 待用户确认。
        /// </summary>
        public double MarginQuantum = 0;
        /// <summary>
        /// 实际用的量化步长：<see cref="MarginQuantum"/> &gt; 0 用它，否则 = 耦合容差 ÷ min(热侧限值, 冷侧限值)。
        /// ★ 2026-09-15 Opus 5（合并）：E 最终版这里是属性、分母读本类的 HotLimitK／ColdLimitK（各 5.0，限值的第二份）；两个选项已删，限值由调用方从整线算例 LineCase 读来传入
        ///   （求解器取两态算例里较小的那个，见 <see cref="LimitsOf"/>）。默认算例限值 5 K 下数值不变（0.25 ÷ 5 = 0.05）。
        /// </summary>
        public double EffectiveMarginQuantum(double hotLimitK, double coldLimitK)
            => MarginQuantum > 0 ? MarginQuantum : CoupleTolK / Math.Min(hotLimitK, coldLimitK);
        // 2026-09-15 Opus 5（合并）：原有 HotLimitK／ColdLimitK 两个选项（各 5.0）删去 —— 限值只从整线算例 LineCase.HotOverTcMaxK／ColdUnderTcMaxK 读
        //   （U 路 2026-09-18：那两个属性又只从参数表的两项温差预算读，默认 = 热偶在 1100 °C 的误差），
        //   经 PlateWorkPoint／PlateStateView 带进评估函数、经 LimitsOf 带进量化步长，不再有第二个来源。
        /// <summary>量本片自身响应 s、κ 时管根抬高的量 K（管根 ±5 K 内抽热线性，出处 deliverable\R48_管根接线与灵敏度_2026-09-14.txt）。</summary>
        public double RootProbeK = 2.0;
        /// <summary>量 γ 时本片抽热的扰动量 ±W。⚠ 无出处：取 2 W ⇒ 管根动约 5 K（γ≈2.5 K/W），远大于 <see cref="TubeProbeTolK"/>、又在工作点附近。</summary>
        public double GammaProbeW = 2.0;
        /// <summary>
        /// 管侧响应牛顿解的停机容差 K（到不动点距离估计 ‖(I − J)⁻¹ 真残差‖，见 LineRunner.TubeNewtonResult.DistanceK），默认取整线耦合容差 0.25 K。
        /// 不取更小：段解本身有噪声底（2026-09-15 Opus 5 一次性实测：端片欠松弛迭代 60 轮后按 ×25 口径的判收敛量停在 0.04～0.06 K 不再降）；
        /// ⚠ 2026-09-15 Opus 5（I 路）：上句「一次性实测」没有留文件，与唯一的对拍文件对不上 —— deliverable\R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt 第 5–12 行端片 1 轮就停（判收敛量 0.10～0.14 K），没有跑满 60 轮的端片记录 ⇒ 「噪声底 0.04～0.06 K」无出处，取 0.25 K 的理由只剩「与整线耦合容差同值」；
        /// 两种解法同一不动点的对拍见慢门 R48TubeResponseNewtonGateTests。
        /// </summary>
        public double TubeProbeTolK = 0.25;
        /// <summary>管侧响应牛顿弦法最多几步（每步一次全部段解）；至少走 1 步（起点是整线收敛态，离不动点约 0.13～0.18 K 的那一截对 ± 扰动是共同的，走一步把扰动自身未平衡的部分也解掉）。</summary>
        public int TubeProbeMaxIter = 10;
        /// <summary>段间端温雅可比的有限差分步 K。⚠ 无出处：1 K 远大于段解噪声、又远小于辐射非线性的尺度。</summary>
        public double TubeJacobianDeltaK = 1.0;
        /// <summary>
        /// 闭合核对标 ⚠ 的界限（跑前写死，2026-09-15 Opus 5）：|实际修正 − 预测修正| &gt; max(绝对界, 相对界 × |预测修正|)。
        /// 管根与峰值的绝对界 1.0 K（≈ 审查量出的网格偏差折开尔文量级）；净流入 0.4 W（γ≈2.5 K/W 时约 1 K）；相对界 20 %。⚠ 无出处。
        /// </summary>
        public double ClosureFlagK = 1.0, ClosureFlagW = 0.4, ClosureFlagRel = 0.2;
        /// <summary>空管算例的控温点取法（DesignSpec.BuildCase 同名参数；待用户定）。</summary>
        public EmptyTubeSetpoint EmptyTubeSetpoint = EmptyTubeSetpoint.RampTarget;
        public PlateCriteria Criteria = DefaultCriteria;
        /// <summary>进度与报告行（线程安全地串行调用）。</summary>
        public Action<string>? Log;
        public CancellationToken Cancel;
    }

    // ── 纯函数（快门直接验）

    /// <summary>一个格点相对选中格点的闭合修正。</summary>
    public readonly struct ClosureShift
    {
        public readonly bool Ok;
        public readonly string Why;
        public readonly double RootHotK, RootColdK, DrawW, DiscK, TabK;
        public ClosureShift(bool ok, string why, double rootHotK, double rootColdK, double drawW, double discK, double tabK)
        { Ok = ok; Why = why; RootHotK = rootHotK; RootColdK = rootColdK; DrawW = drawW; DiscK = discK; TabK = tabK; }
        /// <summary>「无闭合」：全部修正为 0。</summary>
        public static readonly ClosureShift None = new(true, "", 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// 线性闭合（类注释「管根闭合」）：ΔQ₀ = qGrid − qSel；Δ较热端 = −γ_热·ΔQ₀/(1+γ_热·s)；Δ较冷端 = −γ_冷·ΔQ₀/(1+γ_热·s)；
    /// 净流入修正 = s·Δ较热端；峰值修正 = κ·Δ较热端。分母不为正（或不是数）⇒ Ok = false。
    /// </summary>
    public static ClosureShift Close(double qGridW, double qSelW, double gammaHot, double gammaCold, double s, double kappaDisc, double kappaTab)
    {
        double denom = 1 + gammaHot * s;
        if (!(denom > 0)) return new ClosureShift(false, $"闭合分母 1+γ·s = {denom:0.000} 不为正", double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double dq0 = qGridW - qSelW;
        double dtHot = -gammaHot * dq0 / denom;
        double dtCold = -gammaCold * dq0 / denom;
        return new ClosureShift(true, "", dtHot, dtCold, s * dtHot, kappaDisc * dtHot, kappaTab * dtHot);
    }

    /// <summary>管侧响应的一端割线 K/W：−(管根₊ − 管根₋)/(2ΔQ)。</summary>
    public static double TubeSlopeKPerW(double rootPlusC, double rootMinusC, double dqW) => -(rootPlusC - rootMinusC) / (2 * dqW);

    /// <summary>
    /// 把第 <paramref name="plate"/> 片的抽热加 <paramref name="dqW"/>：按生产的 <see cref="LineRunner.SegmentEndDraws"/> 分到它所贴的各段端
    /// （段 i 左端 = 片 i、右端 = 片 i+1；共用片按 SplitSharedFlangeDraw 各半），其余各端原样。
    /// </summary>
    public static (double L, double R)[] PerturbPlateDraw(LineCase c, (double L, double R)[] drawLR, int plate, double dqW)
    {
        var o = ((double L, double R)[])drawLR.Clone();
        for (int i = 0; i < c.SegmentCount && i < o.Length; i++)
        {
            if (i != plate && i + 1 != plate) continue;
            var e = LineRunner.SegmentEndDraws(c, i, i == plate ? dqW : 0, i + 1 == plate ? dqW : 0);
            o[i] = (o[i].L + e.L, o[i].R + e.R);
        }
        return o;
    }

    /// <summary>网格偏差折开尔文：内层与整线在选中格点上的抽热差 ΔQ 若由整线承受，较热端管根会动 −γ·ΔQ/(1+γ·s)；这里返回其绝对值的有号量 γ·ΔQ/(1+γ·s)。s 不是数按 0。</summary>
    public static double MeshBiasK(double gridDrawW, double lineDrawW, double gammaHot, double s)
    {
        double ss = double.IsNaN(s) ? 0 : s;
        double denom = 1 + gammaHot * ss;
        return denom > 0 ? gammaHot * (gridDrawW - lineDrawW) / denom : double.NaN;
    }

    /// <summary>选中格点落在上界时的标记（空串 = 没到顶）。</summary>
    public static string CapNote(int disc, int tab, Options o)
    {
        var parts = new List<string>();
        if (disc >= o.DiscLayerMax) parts.Add($"圆盘层 = 上界 {o.DiscLayerMax}");
        if (tab >= o.TabLayerMax) parts.Add($"舌层 = 上界 {o.TabLayerMax}");
        return parts.Count == 0 ? "" : "到顶（" + string.Join("、", parts) + "；上界无出处，到顶不等于无解）";
    }

    /// <summary>闭合核对的标出规则：|实际修正 − 预测修正| &gt; max(绝对界, 相对界 × |预测修正|)。任一不是数 ⇒ 不标（报告另写判不了）。</summary>
    public static bool ClosureFlagged(double predShift, double actualShift, double absTol, double rel)
        => !double.IsNaN(predShift) && !double.IsNaN(actualShift)
           && Math.Abs(actualShift - predShift) > Math.Max(absTol, rel * Math.Abs(predShift));

    // ── 数据

    /// <summary>一次整线（两态）。</summary>
    public sealed class LineSolve
    {
        public Selection Sel = null!;
        public LineResult?[] ByState = new LineResult?[2];
        /// <summary>两态解完的整线算例（其 WarmStart 是本次整线的收敛态，管侧响应只读它）。</summary>
        public LineCase?[] Cases = new LineCase?[2];
        public double[] Seconds = new double[2];
        public bool Ok;
        public string Why = "";
        public string RecipeCheck = "";
        public string TubeBaseCheck = "";
        public Lazy<TubeResponse[,]> Tube = null!;
    }

    /// <summary>第 j 片在一个工况下的工作点（取自整线结果）。</summary>
    public sealed class PlateWorkPoint
    {
        public int Plate, State;
        public bool Shared;
        public double JointA, RootHotC, RootColdC, LineDrawW, ReferenceC;
        /// <summary>热侧、冷侧限值 K（本态整线算例的 LineCase.HotOverTcMaxK／ColdUnderTcMaxK；2026-09-15 Opus 5（合并））。</summary>
        public double HotLimitK = double.NaN, ColdLimitK = double.NaN;
        /// <summary>本片所贴各段端（Solver.EndsOf 次序）与各端管根。</summary>
        public (int Seg, bool AEnd)[] Ends = Array.Empty<(int, bool)>();
        public double[] EndRootsC = Array.Empty<double>();
        /// <summary>较热端／较冷端在 Ends 里的下标（相等取先出现的）。</summary>
        public int HotEnd, ColdEnd;
    }

    /// <summary>冻结管根下的单片原始解。</summary>
    public sealed class RawPoint
    {
        public double Q = double.NaN, TDisc = double.NaN, TTab = double.NaN;
        public bool Ok;
        public string Why = "";
    }

    public sealed class RawGrid
    {
        public RawPoint[,] P = new RawPoint[0, 0];
        public double Seconds;
        public int Cells;
    }

    /// <summary>本片自身对管根的响应（在选中格点上量）。</summary>
    public sealed class SelfResponse
    {
        public double S = double.NaN, KappaDisc = double.NaN, KappaTab = double.NaN;
        public bool Ok;
        public string Why = "";
    }

    /// <summary>管侧响应（一条整线选择、一片、一态）。</summary>
    public sealed class TubeResponse
    {
        public int Plate, State;
        public double DqW, TolK;
        public (int Seg, bool AEnd)[] Ends = Array.Empty<(int, bool)>();
        public double[] RootPlusC = Array.Empty<double>(), RootMinusC = Array.Empty<double>(), SlopeKPerW = Array.Empty<double>();
        public int HotEnd, ColdEnd;
        public double GammaHot = double.NaN, GammaCold = double.NaN, GammaMean = double.NaN;
        public int EvaluationsPlus, EvaluationsMinus;
        public double JudgePlusK = double.NaN, JudgeMinusK = double.NaN;
        public double Seconds;
        public bool Ok;
        public string Why = "";
        public string EndName(int k) => $"段{Ends[k].Seg}{(Ends[k].AEnd ? "A" : "B")}端";
    }

    /// <summary>一轮里一片一态的明细（报告与闭合核对用）。</summary>
    public sealed class PlateStateRound
    {
        public PlateWorkPoint Wp = null!;
        public bool SelOk;
        public double GridDrawAtSelW = double.NaN, GridDiscAtSelC = double.NaN, GridTabAtSelC = double.NaN;
        public SelfResponse Resp = new();
        public TubeResponse Tube = null!;
        public bool Closed;
        public string ClosureWhy = "";
        public double MeshBiasW = double.NaN, MeshBiasK = double.NaN;
        public double GridSeconds;
    }

    /// <summary>挂在 PointOutcome.Detail 上：两态各自的闭合量。</summary>
    public sealed class PointDetail
    {
        public PlateStateView[] Views = new PlateStateView[2];
    }

    public sealed class RoundDetail
    {
        public LineSolve Line = null!;
        public PlateStateRound[,] PS = new PlateStateRound[0, 0];   // [片, 态]
    }

    /// <summary>闭合核对一行：上一轮闭合对本轮选择的预测 vs 本轮实值。</summary>
    public sealed class ClosureCheckRow
    {
        public int Plate, State;
        public string Quantity = "", Unit = "";
        /// <summary>本片的选择这一轮有没有变（没变 ⇒ 预测修正为 0，实际修正全是邻片串扰）。</summary>
        public bool PlateChanged;
        public double BaseValue, Predicted, Actual;
        public double PredShift => Predicted - BaseValue;
        public double ActualShift => Actual - BaseValue;
        public double Error => Actual - Predicted;
        public bool Flag;
    }

    /// <summary>
    /// 闭合核对：<paramref name="prev"/> 轮在「成为 <paramref name="cur"/> 轮选择的那个格点」上的闭合预测，对 <paramref name="cur"/> 轮的实值。
    /// 管根比整线（本轮整线 − 上轮整线）；净流入、盘峰、舌区峰比内层同一张网格上选中格点的冻结解（本轮 − 上轮）。
    /// </summary>
    public static List<ClosureCheckRow> CheckClosure(RoundRecord prev, RoundRecord cur, Options o)
    {
        var rows = new List<ClosureCheckRow>();
        if (!prev.LineOk || !cur.LineOk || prev.Detail is not RoundDetail pd || cur.Detail is not RoundDetail cd) return rows;
        int n = Math.Min(prev.Choices.Length, cur.From.PlateCount);
        for (int j = 0; j < n; j++)
        {
            var pdv = prev.Choices[j]?.Chosen?.Detail as PointDetail;
            if (pdv is null) continue;
            bool changed = prev.From.Disc[j] != cur.From.Disc[j] || prev.From.Tab[j] != cur.From.Tab[j];
            for (int s = 0; s < 2; s++)
            {
                var v = pdv.Views[s];
                var pp = pd.PS[j, s]; var cp = cd.PS[j, s];
                if (v is null || pp is null || cp is null) continue;
                void Add(string q, string unit, double b, double pr, double ac)
                {
                    var row = new ClosureCheckRow { Plate = j, State = s, Quantity = q, Unit = unit, PlateChanged = changed, BaseValue = b, Predicted = pr, Actual = ac };
                    row.Flag = ClosureFlagged(row.PredShift, row.ActualShift, unit == "W" ? o.ClosureFlagW : o.ClosureFlagK, o.ClosureFlagRel);
                    rows.Add(row);
                }
                Add("较热端管根", "K", pp.Wp.RootHotC, v.RootHotC, cp.Wp.RootHotC);
                Add("较冷端管根", "K", pp.Wp.RootColdC, v.RootColdC, cp.Wp.RootColdC);
                Add("管孔净流入", "W", pp.GridDrawAtSelW, v.NetInflowW, cp.GridDrawAtSelW);
                Add("盘峰", "K", pp.GridDiscAtSelC, v.DiscPeakC, cp.GridDiscAtSelC);
                Add("舌区峰", "K", pp.GridTabAtSelC, v.TabPeakC, cp.GridTabAtSelC);
            }
        }
        return rows;
    }

    /// <summary>一个 T 层的全部结果。</summary>
    public sealed class LayerResult
    {
        public int TubeLayer;
        public bool BelowLowerBound;
        public string SkipWhy = "";
        public double[] TongueThickMm = Array.Empty<double>(), PlateThickMm = Array.Empty<double>();
        public double MassG = double.NaN;
        public StartsOutcome? Starts;
        /// <summary>
        /// ★★★★★ K 路（2026-09-15，Opus 5；审查 P1-3）：**终点选择上的整线两态判定** —— 读整线结果的全部判定（<see cref="LineResult.AllOk"/>：外层耦合收敛、
        /// 本工况必备判据齐全且卡交付的全过、场判得了 —— 压接盖孔、压接进盘、管表超界、保温分界判不了都在里面），不只逐片冻结解上的三项。
        /// null = 没有唯一终点（与起点无关证不出），无从判。
        /// </summary>
        public bool? FinalLineAllOk;
        public string FinalLineWhy = "";
        /// <summary>本层可行 = 逐片可行（<see cref="StartsOutcome.Feasible"/>）且终点整线两态 <see cref="LineResult.AllOk"/>。★ 建议层只从这里取。</summary>
        public bool Feasible => Starts?.Feasible == true && FinalLineAllOk == true;
        /// <summary>每条整线解的配方自检与管侧单解复现自检（按选择键排序）。</summary>
        public List<string> RecipeChecks = new(), TubeBaseChecks = new();
        /// <summary>两条链各自的闭合核对（链名 → 逐轮行）。</summary>
        public Dictionary<string, List<(int Round, List<ClosureCheckRow> Rows)>> Closure = new();
        public double Seconds;
        public int LineSolves;
        public List<string> Lines = new();
    }

    public sealed class Report
    {
        public int LowerBoundLayer = -1;
        public List<LayerResult> Layers = new();
        public LayerResult? Recommended;
        public List<string> Lines = new();
        public string Text => string.Join(Environment.NewLine, Lines);
    }

    /// <summary>
    /// 管保温闭式下界：DesignCurrent 升温所需电流不被管 J 许用截住的第一层（0..maxLayer），没有就 −1。
    /// 只读管（DesignCurrent.ForLine 的尺寸依据是管子自己的准静态电流），与法兰保温无关。
    /// </summary>
    public static int TubeLayerLowerBound(DesignSpec d, DesignInputs p, int maxLayer)
    {
        for (int L = 0; L <= maxLayer; L++)
        {
            var dd = d.Clone().Fit();
            dd.TubeInsulMm = Mm(L);
            if (!DesignCurrent.ForLine(dd, p).Clipped) return L;
        }
        return -1;
    }

    /// <summary>
    /// 第 j 片在一个工况下的工作点。各端按 Solver.EndsOf（段 j 的 A 端、段 j−1 的 B 端）；出口片只有段 n−1 的 B 端。
    /// <paramref name="c"/> = 解出 <paramref name="r"/> 的那个整线算例（判据限值从它读）。
    /// ★ 2026-09-15 Opus 5（合并）：热偶读数基准、管根较热端／较冷端改取 B 的唯一实现 <see cref="ThermocoupleBasis.At(LineResult,int)"/>
    ///   （E 最终版原先基准自调 ThermocoupleBasis.ReferenceC(设定数组, j)、较冷端自己取 Min —— 同一件事的第二份写法）；限值取 LineCase.HotOverTcMaxK／ColdUnderTcMaxK。
    ///   E 最终版的端号下标（HotEnd／ColdEnd，管侧响应按端取割线用）照原样在本函数里数，但必须与 At 选出的较热端／较冷端逐位是同一个值；
    ///   管根较热端还必须与整线本片热解用的管根 FlangeOut.TRootC 逐位相同（两边都是「本片所贴各端取较热者」）—— 任一对不上就抛，不许让内层解与整线不是同一个工作点。
    /// </summary>
    public static PlateWorkPoint WorkPointOf(LineResult r, LineCase c, int state, int j)
    {
        if (c is null) throw new ArgumentNullException(nameof(c), "工作点要整线算例（判据限值从它读）");
        var f = r.Flanges[j];
        int nSeg = r.Segments.Length;
        var ends = Solver.EndsOf(j, nSeg);
        var roots = ends.Select(e => e.AEnd ? r.Segments[e.Seg].TRootAC : r.Segments[e.Seg].TRootBC).ToArray();
        int hot = 0, cold = 0;
        for (int k = 1; k < roots.Length; k++)
        {
            if (roots[k] > roots[hot]) hot = k;
            if (roots[k] < roots[cold]) cold = k;
        }
        var tc = ThermocoupleBasis.At(r, j);
        if (roots.Length == 0 || !tc.RootHotC.Equals(roots[hot]) || !tc.RootColdC.Equals(roots[cold]))
            throw new InvalidOperationException($"片{j}：热偶读数的管根较热端／较冷端 {tc.RootHotC:R}／{tc.RootColdC:R} °C 与按端号数出的不是同一个值 —— 两份「各端取较热／较冷」漂开了");
        if (!tc.RootHotC.Equals(f.TRootC))
            throw new InvalidOperationException($"片{j}：热偶读数的管根较热端 {tc.RootHotC:R} °C 与整线本片热解用的管根 {f.TRootC:R} °C 不逐位相同 —— 两份「所贴各端取较热者」漂开了，内层冻结解不是整线那个工作点");
        return new PlateWorkPoint
        {
            Plate = j, State = state, Shared = f.Shared,
            JointA = f.CurrentA, RootHotC = tc.RootHotC, RootColdC = tc.RootColdC, LineDrawW = f.QFromTubeW,
            ReferenceC = tc.RefC,
            HotLimitK = c.HotOverTcMaxK, ColdLimitK = c.ColdUnderTcMaxK,
            Ends = ends, EndRootsC = roots, HotEnd = hot, ColdEnd = cold,
        };
    }

    /// <summary>
    /// 两态整线算例里较小的热侧、冷侧限值（裕度量化步长的分母）。2026-09-15 Opus 5（合并）：限值只从 LineCase 读，量化步长不再有自己的一份。
    /// </summary>
    public static (double HotK, double ColdK) LimitsOf(IReadOnlyList<LineCase?> cases)
    {
        if (cases is null || cases.Count == 0 || cases.Any(x => x is null)) throw new ArgumentException("要两态都造好的整线算例", nameof(cases));
        return (cases.Min(x => x!.HotOverTcMaxK), cases.Min(x => x!.ColdUnderTcMaxK));
    }

    /// <summary>一态的整线算例（整线解与限值读取同一个入口 DesignSpec.BuildCase；state 1 = 空管）。2026-09-15 Opus 5（合并）：原 CaseFor 的式子搬到这里，Run 读限值也调它。</summary>
    private static LineCase CaseOf(DesignSpec dd, DesignInputs p, Options o, int state)
        => dd.BuildCase(p, checkRamp: false, emptyTube: state == 1, emptyTubeSetpoint: o.EmptyTubeSetpoint);

    /// <summary>网格配方选项（整线与内层都走 Solver.ApplyCaseMesh；细区半径统一）。</summary>
    private static SolverOptions MeshOpt(double hMm, double radiusMm) => new() { FineMm = hMm, FineRadiusMm = radiusMm };

    /// <summary>
    /// 跑保温搜索。<paramref name="design"/> 是当前几何（不改它）；<paramref name="baseIn"/> 是工艺参数。
    /// 返回全部层的结果与报告行；报告行也逐行送 <see cref="Options.Log"/>（层并行时进度行会交错，最终报告按层序另排一遍）。
    /// </summary>
    public static Report Run(DesignSpec design, DesignInputs baseIn, Options o)
    {
        if (design is null) throw new ArgumentNullException(nameof(design));
        if (baseIn is null) throw new ArgumentNullException(nameof(baseIn));
        if (o is null) throw new ArgumentNullException(nameof(o));
        if (design.IsDrawingRecord) throw new InvalidOperationException("图纸档没有解析板件，保温搜索只做解析路径（图纸路径逐片圆盘保温另有入口，本版不接）。");
        if (baseIn.SigmaOfTCoupling) throw new InvalidOperationException("电导率随温度耦合开着：电位场随温度场变，冻结电位场逐格点换保温不成立 —— 本求解器不支持，先关掉再算。");
        if (!design.FlangeInsulated) throw new InvalidOperationException("设计里法兰整线「不包」：圆盘保温层号不起作用（逐片值一律按 0），保温搜索没有意义。");
        if (o.DiscLayerMax < 0 || o.TabLayerMax < 0 || o.TubeLayerMax < 0) throw new ArgumentOutOfRangeException(nameof(o), "层号上界不能为负");
        if (!(o.InnerMeshMm > 0)) throw new ArgumentOutOfRangeException(nameof(o), "内层网格 h 必须为正");
        if (!(o.GammaProbeW > 0) || !(o.TubeProbeTolK > 0) || !(o.TubeJacobianDeltaK > 0)) throw new ArgumentOutOfRangeException(nameof(o), "管侧响应的扰动量与容差必须为正");

        var logLock = new object();
        var rep = new Report();
        void Log(string s) { lock (logLock) { o.Log?.Invoke(s); } }

        var d0 = design.Clone().Fit();
        rep.LowerBoundLayer = TubeLayerLowerBound(d0, baseIn, o.TubeLayerMax);
        int[] layers = o.TubeLayers is { Length: > 0 }
            ? o.TubeLayers.Distinct().OrderBy(v => v).ToArray()
            : rep.LowerBoundLayer < 0 ? Array.Empty<int>() : Enumerable.Range(rep.LowerBoundLayer, o.TubeLayerMax - rep.LowerBoundLayer + 1).ToArray();

        double radius = MeshVerify.RequiredMeshFor(d0).RadiusMm;
        // 2026-09-15 Opus 5（合并）：量化步长的分母读两态整线算例的限值（与每轮整线解同一个入口 CaseOf；Round 里对本轮算例再折一次、必须逐位相同）
        var (hotLim, coldLim) = LimitsOf(new[] { CaseOf(d0, baseIn, o, 0), CaseOf(d0, baseIn, o, 1) });
        double quantum = o.EffectiveMarginQuantum(hotLim, coldLim);
        // K 路（2026-09-15，Opus 5）：判据头按整线的分工况表逐态生成（不在本类写死哪一态卡哪几条）
        var stateCases = new[] { CaseOf(d0, baseIn, o, 0), CaseOf(d0, baseIn, o, 1) };
        string StateLine(int s)
        {
            bool et = stateCases[s].EmptyTube;
            var hard = PlateTerms.Where(p => LineResult.StateKindOf(p.LineKey, et) is not CheckKind.Reference).Select(p => p.Name).ToArray();
            var refs = PlateTerms.Where(p => LineResult.StateKindOf(p.LineKey, et) is CheckKind.Reference).Select(p => p.Name).ToArray();
            var lineHard = LineResult.RequiredFor(et).Select(q => Criteria.Plain(q.Prefix)).ToArray();
            return $"{StateNames[s]}：逐格点卡 {(hard.Length == 0 ? "（无，本态逐格点只过滤场判不了的格点）" : string.Join("、", hard))}"
                 + (refs.Length == 0 ? "" : $"；只作参考 {string.Join("、", refs)}")
                 + $"；整线终点卡 {string.Join("、", lineHard)}";
        }
        var head = new List<string>
        {
            $"保温搜索（R48 E，2026-09-15 Opus 5；出处 Pt_Optimize/Core/InsulationSearch.cs）　设计「{design.Name}」　片数 {d0.FlangeCount}",
            $"层厚 {LayerMm} mm；管保温层 {(o.TubeLayers is { Length: > 0 } ? "显式 " + string.Join("/", layers) : $"闭式下界起到上界 {o.TubeLayerMax}")}；"
            + $"圆盘层上界 {o.DiscLayerMax}（{Mm(o.DiscLayerMax):0.0} mm）、"
            + $"舌层上界 {o.TabLayerMax}（{Mm(o.TabLayerMax):0.0} mm）、管层上界 {o.TubeLayerMax}（{Mm(o.TubeLayerMax):0.0} mm）—— ⚠ 这三个上界都**无出处、需定**"
            + $"（「{Criteria.Plain(LineResult.Key.WrapTurns)}」2026-09-18 已降为参考行、不卡交付，也就不再给圆盘那条上界当出处：{WrapLimits.PlanOnlyNote} 2026-09-18 Opus 5 退回 §0.-11 前的 40 层）",
            $"管保温闭式下界（升温所需电流不被管 J 许用截住的第一层）：{(rep.LowerBoundLayer < 0 ? "到上界都被截住" : $"{rep.LowerBoundLayer} 层 = {Mm(rep.LowerBoundLayer):0.0} mm")}",
            $"网格（整线与内层同一份配方 Solver.ApplyCaseMesh，细区半径统一 {radius:0.0} mm）：内层单片 h = {o.InnerMeshMm} mm；整线工作点 {(o.WholeLineMeshMm > 0 ? $"h = {o.WholeLineMeshMm} mm" : "导航档（尺寸照 BuildCase，只统一半径）")}；"
            + $"耦合容差 {o.CoupleTolK} K；共用片抽热 {(baseIn.SplitSharedFlangeDraw ? "两侧各半" : "两段各扣一次")}；"
            + $"空管控温点 {(o.EmptyTubeSetpoint == EmptyTubeSetpoint.RampTarget ? "全线升温目标" : "沿用生产设定")}；并发度 {o.MaxDegreeOfParallelism}",
            // ★ U 路（2026-09-18，Opus 5）：两条限值是参数表里的输入（默认 = 热偶在 1100 °C 的误差），这里印的是**本次实际用的**那两个数，不另抄默认值。
            "判据：" + $"{HotName} ≤ {hotLim:0.###} K、{ColdName} ≤ {coldLim:0.###} K（整线算例的限值，与整线判据同一份 = 参数表填的「最热铂高出热偶读数 允许值」「管根低于热偶读数 允许值」，默认 = 热偶在 1100 °C 的误差；逐轮报告照印每项限值）；{InflowName}。热偶读数基准：端片 = 本段设定，共用片 = 两侧设定的对数平均（开尔文）。",
            // K 路（2026-09-15，Opus 5）：原句「⚠ 分工况判据尚未落地 …」—— 已落地，改印落地后的实际口径（逐态由分工况表生成）
            "分工况（整线判据的分工况表 LineResult.RequiredByState，与交付判定同一份；用户 2026-09-15：空管到温稳态只卡电流密度（管 J 与法兰截面 J）与场的有效性）："
            + string.Join("｜", Enumerable.Range(0, 2).Select(StateLine))
            + "。两态的场有效性都要过：任一态的场没收敛、越过熔点、散热表超界、保温分界判不了 ⇒ 该格点（逐格点）或该选择（整线）判不了，判不了不算过。"
            + (o.Criteria == DefaultCriteria ? "" : "　⚠ 本次评估函数是调用方注入的，不是默认口径（两态逐格点一律按卡交付处理管侧响应）"),
            "选择规则：" + string.Format(SelectionRuleText, quantum)
            + (o.MarginQuantum > 0 ? "（量化步长由调用方给定）" : $"（量化步长 = 耦合容差 {o.CoupleTolK} K ÷ 限值 {Math.Min(hotLim, coldLim):0.###} K：比耦合停机不确定度细的差别不算差别；⚠ 待用户确认）"),
            $"管根闭合：γ = 当前选择的管侧响应（在该次整线收敛态上把本片抽热扰动 ±{o.GammaProbeW} W、只解段、段间端温用牛顿弦法解到到不动点估计 < {o.TubeProbeTolK} K，逐端割线；与上界无关）；"
            + $"Δ较热端 = −γ_热·ΔQ₀ ÷ (1 + γ_热·s)，Δ较冷端 = −γ_冷·ΔQ₀ ÷ (1 + γ_热·s)；s、κ 在选中格点上抬管根 {o.RootProbeK} K 量；"
            + $"闭合核对：|实际修正 − 预测修正| > max({o.ClosureFlagK} K 或 {o.ClosureFlagW} W, {o.ClosureFlagRel:P0} × |预测修正|) 标 ⚠（界限跑前写死、无出处）。",
        };
        foreach (var s in head) { rep.Lines.Add(s); Log(s); }

        var results = new LayerResult[layers.Length];
        void DoLayer(int idx)
        {
            var lr = RunLayer(d0, baseIn, o, layers[idx], rep.LowerBoundLayer, radius, quantum, Log);
            results[idx] = lr;
        }
        if (o.LayersInParallel && layers.Length > 1)
        {
            var tasks = Enumerable.Range(0, layers.Length)
                .Select(i => Task.Factory.StartNew(() => DoLayer(i), o.Cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks);
        }
        else
            for (int i = 0; i < layers.Length; i++) DoLayer(i);

        rep.Layers = results.ToList();
        rep.Lines.Add("");
        rep.Lines.Add("════════ 逐层报告（按管保温层号排） ════════");
        foreach (var lr in rep.Layers) rep.Lines.AddRange(lr.Lines);

        // ── 跨层比铂重
        rep.Lines.Add("");
        rep.Lines.Add("════════ 跨层汇总（可行的层按合计铂重比；与起点无关证不出的层、终点整线两态没有全过的层不算可行） ════════");
        rep.Lines.Add("  管保温 mm   合计铂重 g   两端起点                                   终点整线两态判定   可行");
        foreach (var lr in rep.Layers)
        {
            string st = lr.BelowLowerBound ? "低于闭式下界，不算" : lr.Starts is null ? lr.SkipWhy : lr.Starts.Verdict();
            string lineV = lr.FinalLineAllOk is null ? "没有唯一终点，不判" : lr.FinalLineAllOk.Value ? "两态全过" : "没全过：" + lr.FinalLineWhy;
            rep.Lines.Add($"  {Mm(lr.TubeLayer),8:0.0}   {lr.MassG,10:0}   {st}   {lineV}   {(lr.Feasible ? "是" : "否")}");   // K 路（2026-09-15 Opus 5）：审查 P1-3，读整线结果的全部判定
        }
        var feas = rep.Layers.Where(l => l.Feasible && !double.IsNaN(l.MassG)).OrderBy(l => l.MassG).ThenBy(l => l.TubeLayer).ToList();
        rep.Recommended = feas.FirstOrDefault();
        if (rep.Recommended is null)
        {
            rep.Lines.Add("★ 没有可行的层（在给定的层号上界内）。各层终点或末轮的缺口见上面逐层报告。⚠ 上界无出处，到顶不等于无解。");
        }
        else
        {
            var lr = rep.Recommended;
            var fin = lr.Starts!.Final!;
            rep.Lines.Add($"★ 建议：管保温 {Mm(lr.TubeLayer):0.0} mm（可行层里铂重最小，{lr.MassG:0} g）。⚠ 管保温上界 {Mm(o.TubeLayerMax):0.0} mm 无出处。");
            rep.Lines.Add("  保温厚度分布（报告用）：");
            rep.Lines.Add("    片   位置         圆盘保温            舌保温             管保温");
            int n = fin.PlateCount;
            for (int j = 0; j < n; j++)
            {
                string where = j == 0 ? "入口端片" : j == n - 1 ? "出口端片" : "共用片";
                string cap = CapNote(fin.Disc[j], fin.Tab[j], o);
                rep.Lines.Add($"    {j}    {where,-8}   {fin.Disc[j],2} 层 = {Mm(fin.Disc[j]),4:0.0} mm   {fin.Tab[j],2} 层 = {Mm(fin.Tab[j]),4:0.0} mm   {Mm(lr.TubeLayer):0.0} mm（全线一个值）{(cap.Length > 0 ? "　" + cap : "")}");
            }
        }
        return rep;
    }

    // ── 一个 T 层
    private sealed class LayerCtx
    {
        public int T;
        public DesignSpec D = null!;
        public DesignInputs P = null!;
        public Options O = null!;
        public int N;
        public double RadiusMm;
        /// <summary>跑前按两态算例限值折出的裕度量化步长（2026-09-15 Opus 5（合并）；Round 对本轮整线算例再折一次核对）。</summary>
        public double MarginQuantum;
        public ShellMesh[] InnerMesh = Array.Empty<ShellMesh>();
        public Lazy<ShellMesh[]> LineMesh = null!;
        public Selection Low = null!, High = null!;
        public List<(int Disc, int Tab)> Points = new();
        public readonly ConcurrentDictionary<string, Lazy<LineSolve>> Lines = new();
        public readonly ConcurrentDictionary<string, Lazy<ShellCurrentResult>> Currents = new();
        public readonly ConcurrentDictionary<string, Lazy<RawGrid>> Grids = new();
        public readonly ConcurrentDictionary<string, Lazy<SelfResponse>> Resp = new();
        public int LineSolveCount;
        public Action<string> Log = _ => { };
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（I 路，合并把关待办 P0-4）：第 <paramref name="tubeLayer"/> 层管保温下的设计 —— 从传入设计克隆、换管保温，
    /// **板厚逐片先置约束盒下角**（<see cref="Solver.ThickLowerCornerMm"/>，与求解器起点同一份），再 <see cref="Solver.ApplySectionFloor"/> 按设定 J 抬、重定舌片厚。
    /// 为什么：ApplySectionFloor 只增不减，原先直接在传入板厚上调 ⇒ 传入 0.73/1.26/1.26/0.73 时 7.5／8.0／8.5 mm 三层都停在传入值（deliverable\R48_保温搜索_小规模_2026-09-15.txt 第 15、20、26 行），
    /// 而实测这三层的下角是 0.65/1.12/1.12/0.65、0.63/1.09/1.09/0.63、0.62/1.07/1.07/0.62 —— 保温搜索算的是一块比下角厚的板（多花铂、判据被板厚带偏），结果随传入板厚变（「优化不许有起点」）。
    /// 圆盘各级倍率、槽、孔等其余几何照传入设计（本搜索只动保温），不在本函数改。<paramref name="thickLoMm"/> 印报告用。
    /// </summary>
    public static DesignSpec LayerDesign(DesignSpec d0, DesignInputs p, int tubeLayer, SolverResult sres, Action<string>? log, out double thickLoMm)
    {
        if (d0 is null) throw new ArgumentNullException(nameof(d0));
        var d = d0.Clone().Fit();
        d.TubeInsulMm = Mm(tubeLayer);
        var so = new SolverOptions();
        thickLoMm = Solver.ThickLowerCornerMm(d, p, so);
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = thickLoMm;
        Solver.ApplySectionFloor(d, p, so, sres, null, log);
        return d;
    }

    private static LayerResult RunLayer(DesignSpec d0, DesignInputs p, Options o, int T, int lowerBound, double radiusMm, double marginQuantum, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var lr = new LayerResult { TubeLayer = T };
        void Say(string s) { lr.Lines.Add(s); log($"[管保温 {Mm(T):0.0} mm] " + s); }
        Say("");
        Say($"════ 管保温 {T} 层 = {Mm(T):0.0} mm ════");
        if (lowerBound < 0 || T < lowerBound)
        {
            lr.BelowLowerBound = true;
            lr.SkipWhy = lowerBound < 0 ? "到上界升温所需电流都被管 J 许用截住" : $"低于闭式下界 {lowerBound} 层（升温所需电流被管 J 许用截住）";
            Say("不算：" + lr.SkipWhy);
            return lr;
        }

        // 每层从传入设计重新克隆，重算舌片厚与板厚下角（LayerDesign：板厚先置约束盒下角再按 J 抬，与传入板厚无关）
        double[] thickIn = (double[])d0.TabThickMm.Clone();
        var sres = new SolverResult();
        var floorLog = new List<string>();
        var d = LayerDesign(d0, p, T, sres, floorLog.Add, out double thickLo);
        int n = d.FlangeCount;
        lr.TongueThickMm = (double[])d.TongueThickMm.Clone();
        lr.PlateThickMm = (double[])d.TabThickMm.Clone();
        Say($"舌片厚（I/(J·舌宽)）{string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm；板厚 传入 {string.Join("/", thickIn.Select(v => v.ToString("0.00")))}（不用）→ 约束盒下角 {thickLo:0.00} → 按 J 抬后 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))} mm；"
          + $"设计电流 片 {string.Join("/", sres.DesignCurrent?.PlateA.Select(a => a.ToString("0")) ?? Array.Empty<string>())} A");
        foreach (var s in floorLog) Say("   " + s);

        var ctx = new LayerCtx
        {
            T = T, D = d, P = p, O = o, N = n, RadiusMm = radiusMm, MarginQuantum = marginQuantum,
            Low = Selection.Uniform(n, 0, 0), High = Selection.Uniform(n, o.DiscLayerMax, o.TabLayerMax),
            Points = GridPoints(o.DiscLayerMax, o.TabLayerMax),
            Log = s => log($"[管保温 {Mm(T):0.0} mm] " + s),
        };
        // 网格与保温无关：按本层设计（原保温）造一次；内层 h 与整线 h 走同一份配方，只差 h
        ctx.InnerMesh = MeshesFor(ctx, o.InnerMeshMm);
        ctx.LineMesh = new Lazy<ShellMesh[]>(() => MeshesFor(ctx, o.WholeLineMeshMm), LazyThreadSafetyMode.ExecutionAndPublication);
        Say($"内层网格 h={o.InnerMeshMm} mm、细区半径 {ctx.RadiusMm:0.0} mm：逐片 {string.Join("/", ctx.InnerMesh.Select(m => m.CellCount))} 格");

        var starts = RunStarts(ctx.Low, ctx.High, (r, sel) => Round(ctx, r, sel), o.MaxFixedPointRounds, o.StartsInParallel,
                               (name, rec) => ctx.Log($"{name} 第 {rec.Round} 轮 {rec.Seconds:0} s：{(rec.LineOk ? "→ " + rec.Next!.Describe() : "判不了：" + rec.Why)}"),
                               o.Cancel);
        lr.Starts = starts;
        lr.LineSolves = ctx.LineSolveCount;
        var solved = ctx.Lines.Values.Where(v => v.IsValueCreated).Select(v => v.Value).OrderBy(v => v.Sel.Key, StringComparer.Ordinal).ToList();
        lr.RecipeChecks = solved.Where(v => v.Ok).Select(v => $"{v.Sel.DescribeMm()}：{v.RecipeCheck}").ToList();
        lr.TubeBaseChecks = solved.Where(v => v.Ok && v.Tube.IsValueCreated).Select(v => $"{v.Sel.DescribeMm()}：{v.TubeBaseCheck}").ToList();
        lr.MassG = solved.FirstOrDefault(v => v.Ok)?.ByState[0]?.TotalMassG ?? double.NaN;

        // ── 报告
        Say($"配方自检（每条整线解，{lr.RecipeChecks.Count} 条）：");
        foreach (var rc in lr.RecipeChecks) Say("   " + rc);
        Say($"管侧单解复现整线管根自检（每条整线解，{lr.TubeBaseChecks.Count} 条）：");
        foreach (var tc in lr.TubeBaseChecks) Say("   " + tc);
        foreach (var chain in new[] { starts.Low, starts.High })
        {
            Say($"── {chain.StartName}（{chain.Start.Describe()}）：{StatusText(chain.Status)}　{chain.Why}");
            var closure = new List<(int, List<ClosureCheckRow>)>();
            for (int k = 0; k < chain.Rounds.Count; k++)
            {
                var rec = chain.Rounds[k];
                ReportRound(ctx, rec, Say);
                if (k >= 1)
                {
                    var rows = CheckClosure(chain.Rounds[k - 1], rec, o);
                    closure.Add((rec.Round, rows));
                    ReportClosure(rec.Round, rows, Say);
                }
            }
            lr.Closure[chain.StartName] = closure;
            if (chain.Status == ChainStatus.Cycle)
                Say("   周期里的选择（全部列出）：" + string.Join("；", chain.CycleMembers.Select(m => m.DescribeMm())));
            var all = closure.SelectMany(c => c.Item2).ToList();
            var moved = all.Where(r => r.Unit == "K" && r.Quantity.EndsWith("管根", StringComparison.Ordinal) && Math.Abs(r.PredShift) > 1e-9 && !double.IsNaN(r.Error)).ToList();
            Say($"   闭合核对汇总：{closure.Count} 次比较、{all.Count} 行、标 ⚠ {all.Count(r => r.Flag)} 行"
              + (moved.Count == 0 ? "" : $"；有预测修正的管根行 {moved.Count} 行，|误差| 最大 {moved.Max(r => Math.Abs(r.Error)):0.00} K、|误差|/|预测修正| 最大 {moved.Max(r => Math.Abs(r.Error) / Math.Abs(r.PredShift)):P0}"));
        }
        // ★ K 路（2026-09-15，Opus 5；审查 P1-3）：终点选择上读整线结果的全部判定（两态 AllOk），不只逐片冻结解上的三项
        if (starts.StartIndependent)
        {
            var fin = starts.Final!;
            if (ctx.Lines.TryGetValue(fin.Key, out var lz) && lz.IsValueCreated)
            {
                var (ok, why) = FinalLineVerdict(lz.Value);
                lr.FinalLineAllOk = ok; lr.FinalLineWhy = why;
            }
            else { lr.FinalLineAllOk = false; lr.FinalLineWhy = "终点选择的整线没有解过（不该发生：终点那一轮就是从它出发的）"; }
        }
        Say("本层结论：" + starts.Verdict()
            + (lr.FinalLineAllOk is null ? "" : lr.FinalLineAllOk.Value ? "；终点整线两态全过" + (starts.Feasible ? " ⇒ 本层可行" : "（逐片可行集有空 ⇒ 本层仍不可行）")
                                                                  : "；终点整线两态没全过（" + lr.FinalLineWhy + "）⇒ 本层不可行"));
        if (starts.Low.Status == ChainStatus.Converged && starts.High.Status == ChainStatus.Converged && !starts.StartIndependent)
            Say($"   两套终点：全 0 层起 {starts.Low.Final!.DescribeMm()}；全上界起 {starts.High.Final!.DescribeMm()}");
        var finalRound = starts.StartIndependent ? starts.Low.FinalRound : null;
        if (finalRound is not null)
        {
            Say($"   终点 {starts.Final!.DescribeMm()}；合计铂重 {lr.MassG:0} g");
            if (finalRound.Detail is RoundDetail fd)
                for (int j = 0; j < n; j++)
                {
                    var ch = finalRound.Choices[j];
                    string cap = CapNote(ch.Chosen.Disc, ch.Chosen.Tab, o);
                    string bias = string.Join("、", Enumerable.Range(0, 2).Select(s => fd.PS[j, s]).Where(ps => ps is not null)
                        .Select(ps => $"{StateNames[ps.Wp.State]} {ps.MeshBiasK:+0.00;-0.00} K"));
                    if (!ch.IsFeasible)
                    {
                        var w = ch.Chosen.Worst;
                        Say($"   片{j} 可行集空：最近格点 圆盘 {Mm(ch.Chosen.Disc):0.0}／舌 {Mm(ch.Chosen.Tab):0.0} mm{(cap.Length > 0 ? "，" + cap : "")}，缺口最大的判据 {(w is null ? ch.Chosen.Why : w.Show())}");
                    }
                    else if (cap.Length > 0) Say($"   片{j} 可行：{cap}");
                    Say($"      片{j} 终点判据值的不确定度：终点格点自己不经闭合（Δ管根 = 0），带内层与整线的网格偏差（折开尔文 {bias}）与耦合容差 {o.CoupleTolK} K；"
                      + "「最近格点」是不是真最近，带闭合误差（见上面闭合核对）");
                }
        }
        lr.Seconds = sw.Elapsed.TotalSeconds;
        Say($"本层耗时 {lr.Seconds / 60:0.0} 分，整线解 {lr.LineSolves} 次（每次两态）");
        return lr;
    }

    private static void ReportClosure(int round, List<ClosureCheckRow> rows, Action<string> say)
    {
        if (rows.Count == 0) { say($"     闭合核对（第 {round - 1} 轮预测 → 第 {round} 轮实值）：没有可比的行（上一轮或本轮整线没解出）"); return; }
        say($"     闭合核对（第 {round - 1} 轮闭合对本轮选择的预测 → 第 {round} 轮实值；管根比整线、净流入与峰值比内层同一张网格）：");
        foreach (var g in rows.GroupBy(r => (r.Plate, r.State)))
        {
            var first = g.First();
            say($"       片{g.Key.Plate} {StateNames[g.Key.State]}（本片选择{(first.PlateChanged ? "变了" : "没变 ⇒ 预测修正 0，实际修正全是邻片串扰")}）："
              + string.Join("；", g.Select(r => double.IsNaN(r.Error)
                    ? $"{r.Quantity} 判不了"
                    : $"{r.Quantity} 预测修正 {r.PredShift:+0.00;-0.00} 实际 {r.ActualShift:+0.00;-0.00} 误差 {r.Error:+0.00;-0.00} {r.Unit}{(Math.Abs(r.PredShift) > 1e-9 ? $"（{Math.Abs(r.Error) / Math.Abs(r.PredShift):P0}）" : "")}{(r.Flag ? " ⚠" : "")}")));
        }
    }

    private static void ReportRound(LayerCtx ctx, RoundRecord rec, Action<string> say)
    {
        say($"   第 {rec.Round} 轮（{rec.Seconds:0} s）从 {rec.From.DescribeMm()}：" + (rec.LineOk ? $"→ {rec.Next!.DescribeMm()}" : "判不了 —— " + rec.Why));
        if (rec.Detail is not RoundDetail det) return;
        say($"     整线两态用时 {det.Line.Seconds[0]:0}/{det.Line.Seconds[1]:0} s；合计铂重 {det.Line.ByState[0]?.TotalMassG:0} g；剩余误差估计 {det.Line.ByState[0]?.CoupleRemainK:0.000}/{det.Line.ByState[1]?.CoupleRemainK:0.000} K");
        foreach (var ch in rec.Choices)
        {
            if (ch is null) continue;
            int j = ch.Plate;
            var c = ch.Chosen;
            string cap = CapNote(c.Disc, c.Tab, ctx.O);
            // K 路（2026-09-15，Opus 5）：单态可行只数卡交付的项；本态逐格点全是参考项时照实说「不逐格点卡」，不印成「0 个可行」
            string perState = string.Join("、", StateNames.Select(sn => ch.ReferenceOnlyStates.Contains(sn)
                ? $"{sn} 不逐格点卡（只作参考，场判得了 {ch.Evaluated - ch.UndeterminedCount} 格）"
                : $"{sn} {ch.FeasibleIn(sn)}"));
            say($"     片{j}：可行 {ch.FeasibleCount}/{ch.Evaluated}（判不了 {ch.UndeterminedCount}；单态可行 {perState}）　选 圆盘 {c.Disc} 层 = {Mm(c.Disc):0.0} mm、舌 {c.Tab} 层 = {Mm(c.Tab):0.0} mm"
              + $"　{(c.Feasible ? "可行" : "不可行（最近格点）")}　归一最小裕度 {c.MinNormMargin:+0.000;-0.000}" + (cap.Length > 0 ? "　" + cap : "") + (c.Determined ? "" : "　判不了：" + c.Why));
            for (int s = 0; s < 2; s++)
            {
                var ps = det.PS[j, s];
                if (ps is null) continue;
                var v = (c.Detail as PointDetail)?.Views[s];
                var t = ps.Tube;
                say($"       {StateNames[s]}：接头电流 {ps.Wp.JointA:0} A　管根 热/冷 {ps.Wp.RootHotC:0.00}/{ps.Wp.RootColdC:0.00} °C　基准 {ps.Wp.ReferenceC:0.00} °C"
                  + $"　整线抽热 {ps.Wp.LineDrawW:+0.000;-0.000} W　内层网格同格点抽热 {ps.GridDrawAtSelW:+0.000;-0.000} W（网格偏差 {ps.MeshBiasW:+0.000;-0.000} W，折 {ps.MeshBiasK:+0.00;-0.00} K）"
                  + $"　s {ps.Resp.S:0.000} W/K　κ 盘/舌 {ps.Resp.KappaDisc:0.000}/{ps.Resp.KappaTab:0.000}　内层 {ps.GridSeconds:0} s"
                  + (ps.Closed ? "" : $"　⚠ 无闭合（{ps.ClosureWhy}）"));
                if (t is not null)
                    say($"         γ（管侧响应，±{t.DqW} W）：热端 {t.GammaHot:0.000}、冷端 {t.GammaCold:0.000}、均 {t.GammaMean:0.000} K/W　各端 "
                      + string.Join("、", Enumerable.Range(0, t.Ends.Length).Select(k => $"{t.EndName(k)} {t.SlopeKPerW[k]:0.000}"))
                      + $"　扰动解 段解 {t.EvaluationsPlus}/{t.EvaluationsMinus} 次、到不动点估计 {t.JudgePlusK:0.0000}/{t.JudgeMinusK:0.0000} K（容差 {t.TolK} K）、{t.Seconds:0} s" + (t.Ok ? "" : "　判不了：" + t.Why));
                foreach (var tm in c.Terms.Where(tm => tm.State == StateNames[s]))
                    say($"         {tm.Name}：{tm.Value:+0.000;-0.000} {tm.Unit}（限 {(tm.LessIsBetter ? "≤" : ">")} {tm.Limit:0.###}）归一裕度 {tm.NormMargin:+0.000;-0.000}"
                      + (tm.IsReference ? (tm.Pass ? "　参考（本态不卡）" : "　参考（超出限值，本态不卡）") : tm.Pass ? "" : "　✗"));   // K 路（2026-09-15 Opus 5）
                if (v is not null && (Math.Abs(v.RootShiftK) > 1e-12 || Math.Abs(v.RootColdShiftK) > 1e-12))
                    say($"         （闭合管根修正 热端 {v.RootShiftK:+0.000;-0.000} K、冷端 {v.RootColdShiftK:+0.000;-0.000} K）");
            }
        }
    }

    private static ShellMesh[] MeshesFor(LayerCtx ctx, double hMm)
    {
        var lc = ctx.D.BuildCase(ctx.P, checkRamp: false);
        Solver.ApplyCaseMesh(lc, MeshOpt(hMm, ctx.RadiusMm));
        return Enumerable.Range(0, ctx.N).Select(j => LineRunner.PlateMeshAnalytic(lc, j)).ToArray();
    }

    /// <summary>
    /// ★★★★★ K 路（2026-09-15，Opus 5；审查 P1-3）：一条整线选择的两态判定 —— **只读整线结果自己的判定** <see cref="LineResult.AllOk"/>
    /// （外层耦合收敛、本工况必备判据齐全且卡交付的全过、场判得了；哪一态卡哪几条由 LineRunner.Judge 按分工况表盖好），原因取 <see cref="LineResult.Failed"/>。
    /// 本类不另判任何一条整线判据。公开给门：造一个「逐片三项都过、管 J 超限」的整线结果，这里必须说不可行。
    /// </summary>
    public static (bool Ok, string Why) FinalLineVerdict(LineSolve ls)
    {
        if (ls is null) throw new ArgumentNullException(nameof(ls));
        bool ok = true;
        var parts = new List<string>();
        for (int s = 0; s < ls.ByState.Length; s++)
        {
            var r = ls.ByState[s];
            string sn = s < StateNames.Length ? StateNames[s] : $"态{s}";
            if (r is null) { ok = false; parts.Add($"{sn}：没有整线结果"); continue; }
            if (r.AllOk) continue;
            ok = false;
            var why = new List<string>();
            if (!r.Converged) why.Add("外层耦合没收敛" + (string.IsNullOrEmpty(r.Message) ? "" : "（" + r.Message + "）"));
            why.AddRange(r.Failed);
            parts.Add($"{sn}：{(why.Count == 0 ? "判定为不过（没有给出原因）" : string.Join("；", why))}");
        }
        if (ls.ByState.Length < 2) { ok = false; parts.Add($"只有 {ls.ByState.Length} 态的整线结果，两态都要有"); }
        return (ok, string.Join("｜", parts));
    }

    /// <summary>
    /// ★ 2026-09-16 Opus 5（K 路复审修，审查 M2）：一条整线结果在第 s 态**能不能用**（空串 = 能用；否则是原因，进 <see cref="LineSolve.Why"/>）。
    /// 次序：解不出（<see cref="LineResult.Ok"/> 为假）→ 外层耦合没收敛 → 场判不了（<see cref="LineResult.FieldUndeterminedReasons"/> 非空：
    /// 场没收敛、越过熔点、散热表超界、保温分界判不了、压接盖孔、压接进盘、管表超界 —— 读位不读文字）。**两态都要过**（场的有效性不是判据）。
    /// 公开纯函数，门造一个「Ok、Converged、FieldsConverged 都为真、只有压接盖孔」的结果 ⇒ 这里必须非空；只看 FieldsConverged 的旧写法过不了。
    /// </summary>
    public static string LineStateWhy(LineResult? r, int s)
    {
        string sn = s >= 0 && s < StateNames.Length ? StateNames[s] : $"态{s}";
        if (r is null) return $"{sn}：没有整线结果";
        if (!r.Ok) return $"{sn}整线解不出：{r.Message}";
        if (!r.Converged) return $"{sn}外层耦合没收敛：{r.Message}";
        var bad = r.FieldUndeterminedReasons;
        if (bad.Length > 0) return $"{sn}场判不了：" + string.Join("；", bad);
        return "";
    }

    private static LineSolve SolveLine(LayerCtx ctx, Selection sel)
        => ctx.Lines.GetOrAdd(sel.Key, _ => new Lazy<LineSolve>(() => SolveLineCore(ctx, sel), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static DesignSpec DesignFor(LayerCtx ctx, Selection sel)
    {
        var dd = ctx.D.Clone();
        dd.DiscInsulMm = sel.Disc.Select(Mm).ToArray();
        dd.TabInsulMm = sel.Tab.Select(Mm).ToArray();
        return dd;
    }

    private static LineCase CaseFor(LayerCtx ctx, DesignSpec dd, int state) => CaseOf(dd, ctx.P, ctx.O, state);   // 2026-09-15 Opus 5（合并）：与 Run 读限值同一个入口

    private static LineSolve SolveLineCore(LayerCtx ctx, Selection sel)
    {
        Interlocked.Increment(ref ctx.LineSolveCount);
        var ls = new LineSolve { Sel = sel };
        ls.Tube = new Lazy<TubeResponse[,]>(() => MeasureTube(ctx, ls), LazyThreadSafetyMode.ExecutionAndPublication);
        var dd = DesignFor(ctx, sel);
        var whys = new string[2];
        ctx.Log($"整线开算 {sel.DescribeMm()}");
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ctx.O.Cancel }, s =>
        {
            var sw = Stopwatch.StartNew();
            var lc = CaseFor(ctx, dd, s);
            Solver.ApplyCaseMesh(lc, MeshOpt(ctx.O.WholeLineMeshMm, ctx.RadiusMm));
            lc.CoupleTolK = ctx.O.CoupleTolK;
            // ★ R48 L（2026-09-17，Opus 5）：保温搜索**留常数口径**（查过之后没改）：本类的量化步长
            //   EffectiveMarginQuantum = 耦合容差 ÷ 限值，整条推理的前提就是「容差是个常数」；
            //   容差改成逐轮随裕度变之后那个折算没有意义。要接新口径得连量化步长一起重新定义，本轮没实测。
            lc.CoupleTolFromMargin = false;
            lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, ctx.O.CoupleMaxRounds);
            if (lc.WarmStart.Length != 0 || lc.BaselineRootC.Length != 0)
                throw new InvalidOperationException("整线算例带着热启动或基线缓存 —— 搜索要求每次冷启动");
            var r = LineRunner.Run(lc, null, ctx.O.Cancel);
            ls.ByState[s] = r;
            ls.Cases[s] = lc;
            ls.Seconds[s] = sw.Elapsed.TotalSeconds;
            // K 路（2026-09-15，Opus 5；审查 P1-3）：场的有效性读整线结果的唯一一份 LineResult.FieldUndeterminedReasons（原来只查 Ok／Converged／FieldsConverged，
            //   漏了压接盖孔、压接进盘、管表超界、保温分界判不了）；两态都要过（场的有效性不是判据，空管态也保留）。
            // 2026-09-16 Opus 5（K 路复审修，审查 M1/M2）：判法提成公开纯函数 LineStateWhy —— 上一轮审查注入时这里被改回只看 FieldsConverged 而没还原、
            //   且没有行为门能发现；现在门直接调 LineStateWhy（StateCriteriaGateTests 门2c）。
            whys[s] = LineStateWhy(r, s);
        });
        ls.Ok = whys.All(string.IsNullOrEmpty);
        ls.Why = string.Join("；", whys.Where(w => !string.IsNullOrEmpty(w)));
        ctx.Log($"整线算完 {sel.DescribeMm()}：{(ls.Ok ? "两态都解出" : ls.Why)}　{ls.Seconds[0]:0}/{ls.Seconds[1]:0} s");
        if (ls.Ok) RecipeSelfCheck(ctx, ls);
        return ls;
    }

    /// <summary>
    /// 配方自检（每条整线解）：拿内层**同一个** <see cref="SolvePoint"/>、同一条造网格（<see cref="MeshesFor"/>）与电位场（<see cref="CurrentFor"/>）的路径，
    /// 只把 h 换成整线网格，在整线自己的接头电流与管根上重解每片；格数、电位场、抽热、盘峰、舌区峰必须与整线逐位相同。不同 ⇒ 抛异常。
    /// </summary>
    private static void RecipeSelfCheck(LayerCtx ctx, LineSolve ls)
    {
        var meshes = ctx.LineMesh.Value;
        var items = new (bool Same, string Text)[2 * ctx.N];
        Parallel.For(0, 2 * ctx.N, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ctx.O.MaxDegreeOfParallelism), CancellationToken = ctx.O.Cancel }, k =>
        {
            int s = k / ctx.N, j = k % ctx.N;
            var r = ls.ByState[s]!;
            var f = r.Flanges[j];
            var m = meshes[j];
            var cur = CurrentFor(ctx, "整线", s, j, f.CurrentA, m, ctx.O.WholeLineMeshMm);
            var rp = SolvePoint(ctx, s, j, f.CurrentA, f.TRootC, m, cur, ls.Sel.Disc[j], ls.Sel.Tab[j]);
            bool same = m.CellCount == f.CellCount && cur.JMagAPerMm2.SequenceEqual(f.JField)
                        && rp.Q.Equals(f.QFromTubeW) && rp.TDisc.Equals(f.TDiscMaxC) && rp.TTab.Equals(f.TTabMaxC);
            items[k] = (same, $"{StateNames[s]}片{j} 抽热 {rp.Q:R}{(same ? "=" : "≠")}{f.QFromTubeW:R}");
        });
        bool ok = items.All(t => t.Same);
        ls.RecipeCheck = (ok ? "逐位相同 ✓ " : "✗ 对不上 ") + string.Join("；", items.Select(t => t.Text));
        ctx.Log($"配方自检 {ls.Sel.DescribeMm()}：{ls.RecipeCheck}");
        if (!ok) throw new InvalidOperationException("保温搜索的单片解与整线逐片解配方对不上 —— 内层算的不是整线里那片板，停：" + ls.RecipeCheck);
    }

    /// <summary>
    /// 管侧响应（每条整线选择一次）：每态先在该次整线收敛态上量段间端温的雅可比（<see cref="LineRunner.NeighbourJacobian"/>，其不扰动那次段解顺带自检
    /// 「管侧单解逐位复现整线管根」），再逐片 ±ΔQ 扰动、用牛顿弦法（<see cref="LineRunner.SolveTubeWithDrawsNewton"/>）解到收敛、量各端割线。
    /// 读的是该次整线算例留下的收敛态（只读，不进任何整线解），所以结果只由选择决定。
    /// </summary>
    private static TubeResponse[,] MeasureTube(LayerCtx ctx, LineSolve ls)
    {
        var sw = Stopwatch.StartNew();
        int n = ctx.N;
        var o = ctx.O;
        var tr = new TubeResponse[n, 2];
        var draws = new (double L, double R)[2][];
        var nbs = new (double L, double R)[2][];
        var jacs = new ((int Seg, bool Left)[] Slots, double[,] J)[2];
        var checks = new string[2];
        var sames = new bool[2];
        for (int s = 0; s < 2; s++)
        {
            var lc = ls.Cases[s]!;
            int nSeg = ls.ByState[s]!.Segments.Length;
            if (lc.WarmStart.Length < nSeg || lc.WarmStart.Take(nSeg).Any(a => a.Length < 4))
                throw new InvalidOperationException($"{StateNames[s]}整线算例没有留下收敛态（抽热与段间端温），管侧响应量不了");
            draws[s] = lc.WarmStart.Take(nSeg).Select(a => (a[0], a[1])).ToArray();
            nbs[s] = lc.WarmStart.Take(nSeg).Select(a => (a[2], a[3])).ToArray();
        }
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = o.Cancel }, s =>
        {
            var r = ls.ByState[s]!;
            int nSeg = r.Segments.Length;
            var (slots, jac, g0) = LineRunner.NeighbourJacobian(CaseFor(ctx, DesignFor(ctx, ls.Sel), s), draws[s], nbs[s], o.TubeJacobianDeltaK, o.Cancel);
            jacs[s] = (slots, jac);
            sames[s] = g0.Ok && g0.Segments.Length == nSeg
                       && Enumerable.Range(0, nSeg).All(i => g0.Segments[i].TRootAC.Equals(r.Segments[i].TRootAC) && g0.Segments[i].TRootBC.Equals(r.Segments[i].TRootBC));
            checks[s] = $"{StateNames[s]} {(sames[s] ? "逐位相同 ✓" : "✗ 对不上")}（段0 A 端 {(g0.Ok && g0.Segments.Length > 0 ? g0.Segments[0].TRootAC.ToString("R") : g0.Message)} {(sames[s] ? "=" : "≠")} {r.Segments[0].TRootAC:R}）";
        });
        ls.TubeBaseCheck = string.Join("；", checks);
        if (!sames.All(v => v))
            throw new InvalidOperationException("管侧单解在整线收敛态上没有逐位复现整线管根 —— 管侧响应量的不是整线里那根管，停：" + ls.TubeBaseCheck);

        var jobs = new List<(int S, int J, int Sign)>();
        for (int s = 0; s < 2; s++) for (int j = 0; j < n; j++) foreach (int sg in new[] { +1, -1 }) jobs.Add((s, j, sg));
        var its = new LineRunner.TubeNewtonResult[jobs.Count];
        var secs = new double[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxDegreeOfParallelism), CancellationToken = o.Cancel }, k =>
        {
            var (s, j, sg) = jobs[k];
            var jsw = Stopwatch.StartNew();
            var c = CaseFor(ctx, DesignFor(ctx, ls.Sel), s);
            var pert = PerturbPlateDraw(c, draws[s], j, sg * o.GammaProbeW);
            its[k] = LineRunner.SolveTubeWithDrawsNewton(c, pert, nbs[s], jacs[s], o.TubeProbeTolK, o.TubeProbeMaxIter, o.Cancel, minSteps: 1);
            secs[k] = jsw.Elapsed.TotalSeconds;
        });
        for (int s = 0; s < 2; s++)
            for (int j = 0; j < n; j++)
            {
                int kp = jobs.FindIndex(t => t.S == s && t.J == j && t.Sign == +1), km = jobs.FindIndex(t => t.S == s && t.J == j && t.Sign == -1);
                var wp = WorkPointOf(ls.ByState[s]!, ls.Cases[s]!, s, j);
                var t = new TubeResponse
                {
                    Plate = j, State = s, DqW = o.GammaProbeW, TolK = o.TubeProbeTolK, Ends = wp.Ends, HotEnd = wp.HotEnd, ColdEnd = wp.ColdEnd,
                    EvaluationsPlus = its[kp].Evaluations, EvaluationsMinus = its[km].Evaluations, JudgePlusK = its[kp].JudgeK, JudgeMinusK = its[km].JudgeK,
                    Seconds = secs[kp] + secs[km],
                };
                var why = new List<string>();
                foreach (var (k, name) in new[] { (kp, "+"), (km, "−") })
                    if (!its[k].Converged)
                        why.Add($"扰动 {name}{o.GammaProbeW} W 的管侧解{(its[k].Last is { Ok: true } ? $"没收敛（段解 {its[k].Evaluations} 次、到不动点估计 {its[k].JudgeK:0.0000} K）{its[k].Message}" : "解不出：" + its[k].Message)}");
                if (why.Count == 0)
                {
                    RootsAt(its[kp].Last!, wp.Ends, out t.RootPlusC);
                    RootsAt(its[km].Last!, wp.Ends, out t.RootMinusC);
                    t.SlopeKPerW = Enumerable.Range(0, wp.Ends.Length).Select(e => TubeSlopeKPerW(t.RootPlusC[e], t.RootMinusC[e], o.GammaProbeW)).ToArray();
                    t.GammaHot = t.SlopeKPerW[wp.HotEnd];
                    t.GammaCold = t.SlopeKPerW[wp.ColdEnd];
                    t.GammaMean = t.SlopeKPerW.Average();
                    if (t.SlopeKPerW.Any(v => !double.IsFinite(v))) why.Add("割线不是数");
                }
                t.Ok = why.Count == 0;
                t.Why = string.Join("；", why);
                tr[j, s] = t;
            }
        ctx.Log($"管侧响应 {ls.Sel.DescribeMm()}：{jobs.Count} 个扰动解 {sw.Elapsed.TotalSeconds:0} s；复现自检 {ls.TubeBaseCheck}");
        return tr;
    }

    private static void RootsAt(LineResult r, (int Seg, bool AEnd)[] ends, out double[] roots)
        => roots = ends.Select(e => e.AEnd ? r.Segments[e.Seg].TRootAC : r.Segments[e.Seg].TRootBC).ToArray();

    /// <summary>电位场（与保温无关：用全 0 层的算例，网格配方与所给网格同 h）。kind 区分整线网格与内层网格的缓存。</summary>
    private static ShellCurrentResult CurrentFor(LayerCtx ctx, string kind, int s, int j, double iJoint, ShellMesh mesh, double meshMm)
        => ctx.Currents.GetOrAdd($"{kind}|{s}|{j}|{iJoint:R}", _ => new Lazy<ShellCurrentResult>(() =>
        {
            var cg = CaseFor(ctx, DesignFor(ctx, ctx.Low), s);
            Solver.ApplyCaseMesh(cg, MeshOpt(meshMm, ctx.RadiusMm));
            return LineRunner.PlateCurrentField(cg, mesh, j, iJoint);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// 格点 (D,W) 写进第 j 片的单片冻结解（网格与电位场由调用方给：内层用内层网格，配方自检用整线网格）。
    /// 其它片的保温不进单片解（PlateThermalInputs 只读本片板件与算例公共量）—— 全片同写一个值只为省事，配方自检在非均匀选择上逐位验这一点。
    /// </summary>
    private static RawPoint SolvePoint(LayerCtx ctx, int s, int j, double iJoint, double tRoot, ShellMesh mesh, ShellCurrentResult cur, int disc, int tab)
    {
        var rp = new RawPoint();
        try
        {
            var dd = ctx.D.Clone();
            dd.DiscInsulMm = Enumerable.Repeat(Mm(disc), ctx.N).ToArray();
            dd.TabInsulMm = Enumerable.Repeat(Mm(tab), ctx.N).ToArray();
            var cg = CaseFor(ctx, dd, s);
            var ts = LineRunner.PlateThermalInputs(cg, j, iJoint, mesh.SourceField);
            var th = LineRunner.SolvePlateThermal(mesh, cur.HeatJAPerMm2, tRoot, ts);   // 2026-09-23（F3）：发热用面发热等效 J，与整线逐片解（LineRunner.RunOnce）同一口径（配方自检逐位核）
            rp.Q = th.QFromTubeW; rp.TDisc = th.TDiscMaxC; rp.TTab = th.TTabMaxC;
            var why = new List<string>();
            if (!cur.Converged) why.Add("电位场未收敛");
            if (!th.Converged) why.Add($"温度场未收敛（相对残差 {th.ResidualRel:E2}）");
            if (th.OverMelt) why.Add($"峰值 {th.TMaxC:0} °C 越过熔点");
            if (ts.InsulUndetermined) why.Add("保温分界判不了");
            rp.Ok = why.Count == 0;
            rp.Why = string.Join("、", why);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rp.Ok = false; rp.Why = "单片解抛出异常：" + ex.Message;
        }
        return rp;
    }

    private static RawGrid GridFor(LayerCtx ctx, int s, int j, double iJoint, double tRoot)
        => ctx.Grids.GetOrAdd($"{s}|{j}|{iJoint:R}|{tRoot:R}", _ => new Lazy<RawGrid>(() =>
        {
            var sw = Stopwatch.StartNew();
            var mesh = ctx.InnerMesh[j];
            var cur = CurrentFor(ctx, "内层", s, j, iJoint, mesh, ctx.O.InnerMeshMm);
            var g = new RawGrid { P = new RawPoint[ctx.O.DiscLayerMax + 1, ctx.O.TabLayerMax + 1], Cells = mesh.CellCount };
            var pts = ctx.Points;
            Parallel.For(0, pts.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ctx.O.MaxDegreeOfParallelism), CancellationToken = ctx.O.Cancel }, i =>
            {
                var (dd, ww) = pts[i];
                g.P[dd, ww] = SolvePoint(ctx, s, j, iJoint, tRoot, mesh, cur, dd, ww);
            });
            g.Seconds = sw.Elapsed.TotalSeconds;
            ctx.Log($"内层 {StateNames[s]} 片{j}（接头电流 {iJoint:0} A、管根 {tRoot:0.00} °C）：{pts.Count} 格点 {g.Seconds:0} s，判不了 {pts.Count(t => !g.P[t.Disc, t.Tab].Ok)}");
            return g;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static SelfResponse ResponseFor(LayerCtx ctx, int s, int j, double iJoint, double tRoot, int disc, int tab, RawPoint at)
        => ctx.Resp.GetOrAdd($"{s}|{j}|{iJoint:R}|{tRoot:R}|{disc}|{tab}", _ => new Lazy<SelfResponse>(() =>
        {
            var r = new SelfResponse();
            if (!at.Ok) { r.Why = "选中格点本身判不了：" + at.Why; return r; }
            double dT = ctx.O.RootProbeK;
            var mesh = ctx.InnerMesh[j];
            var up = SolvePoint(ctx, s, j, iJoint, tRoot + dT, mesh, CurrentFor(ctx, "内层", s, j, iJoint, mesh, ctx.O.InnerMeshMm), disc, tab);
            if (!up.Ok) { r.Why = "抬管根重解判不了：" + up.Why; return r; }
            r.S = (up.Q - at.Q) / dT; r.KappaDisc = (up.TDisc - at.TDisc) / dT; r.KappaTab = (up.TTab - at.TTab) / dT;
            r.Ok = true;
            return r;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static RoundRecord Round(LayerCtx ctx, int round, Selection sel)
    {
        var rec = new RoundRecord { Round = round, From = sel };
        var sw = Stopwatch.StartNew();
        var line = SolveLine(ctx, sel);
        var det = new RoundDetail { Line = line, PS = new PlateStateRound[ctx.N, 2] };
        rec.Detail = det;
        if (!line.Ok) { rec.LineOk = false; rec.Why = "整线：" + line.Why; rec.Seconds = sw.Elapsed.TotalSeconds; return rec; }
        var tube = line.Tube.Value;
        // 2026-09-15 Opus 5（合并）：量化步长的分母 = 本轮两态整线算例的限值；必须与跑前印在报告头上的逐位相同（同一个入口造的算例，不同 ⇒ 限值有了第二个来源）
        var (hotLim, coldLim) = LimitsOf(line.Cases);
        double quantum = ctx.O.EffectiveMarginQuantum(hotLim, coldLim);
        if (!quantum.Equals(ctx.MarginQuantum))
            throw new InvalidOperationException($"本轮整线算例限值折出的裕度量化步长 {quantum:R} 与跑前报告头上的 {ctx.MarginQuantum:R} 不同 —— 限值不是同一份");
        var choices = new PlateChoice[ctx.N];
        var why = new List<string>();
        for (int j = 0; j < ctx.N; j++)
        {
            var grids = new RawGrid[2];
            bool plateOk = true;
            for (int s = 0; s < 2; s++)
            {
                var wp = WorkPointOf(line.ByState[s]!, line.Cases[s]!, s, j);
                var t = tube[j, s];
                // ★ K 路（2026-09-15，Opus 5；审查 P1-9）：原来两态都要求 γ > 0，量不出就整轮判不了。γ 只用于闭合（与净流入归一），
                //   本态逐格点没有卡交付的项（分工况表：空管到温稳态）⇒ γ 量不出只让本态「无闭合」、参考值按冻结管根报，不再挡整轮。
                bool judged = StateHasHardPlateTerms(line.Cases[s]!.EmptyTube, ctx.O);
                string tubeBad = !t.Ok ? $"管侧响应量不出：{t.Why}"
                               : !(t.GammaHot > 0 && t.GammaCold > 0) ? $"γ 不为正（热端 {t.GammaHot:0.000}、冷端 {t.GammaCold:0.000} K/W）" : "";
                if (tubeBad.Length > 0 && judged) { why.Add($"{StateNames[s]}片{j} {tubeBad}"); plateOk = false; }
                var grid = GridFor(ctx, s, j, wp.JointA, wp.RootHotC);
                var at = grid.P[sel.Disc[j], sel.Tab[j]];
                var ps = new PlateStateRound
                {
                    Wp = wp, Tube = t, SelOk = at.Ok, GridSeconds = grid.Seconds,
                    GridDrawAtSelW = at.Q, GridDiscAtSelC = at.TDisc, GridTabAtSelC = at.TTab,
                };
                // 退路（确定性）：选中格点判不了或 s/κ 量不出 ⇒ 本片本态无闭合
                if (!at.Ok) { ps.Closed = false; ps.ClosureWhy = "选中格点本身判不了：" + at.Why; }
                else if (tubeBad.Length > 0) { ps.Closed = false; ps.ClosureWhy = tubeBad + (judged ? "" : "（本态逐格点只作参考，按冻结管根报参考值）"); }   // K 路（2026-09-15 Opus 5）
                else
                {
                    ps.Resp = ResponseFor(ctx, s, j, wp.JointA, wp.RootHotC, sel.Disc[j], sel.Tab[j], at);
                    ps.Closed = ps.Resp.Ok;
                    if (!ps.Resp.Ok) ps.ClosureWhy = "本片对管根的响应量不出：" + ps.Resp.Why;
                }
                ps.MeshBiasW = at.Q - wp.LineDrawW;
                ps.MeshBiasK = MeshBiasK(at.Q, wp.LineDrawW, t.GammaHot, ps.Closed ? ps.Resp.S : 0);
                det.PS[j, s] = ps;
                grids[s] = grid;
            }
            if (!plateOk || why.Count > 0) continue;
            int jj = j;
            choices[j] = ChoosePlate(j, ctx.Points, (dsk, wsk) => EvaluatePoint(ctx, det, jj, grids, dsk, wsk), 1, quantum, ctx.O.Cancel);
        }
        rec.Seconds = sw.Elapsed.TotalSeconds;
        if (why.Count > 0) { rec.LineOk = false; rec.Why = string.Join("；", why); rec.Choices = choices; return rec; }
        rec.Choices = choices;
        rec.Next = new Selection(choices.Select(c => c.Chosen.Disc).ToArray(), choices.Select(c => c.Chosen.Tab).ToArray());
        return rec;
    }

    private static PointOutcome EvaluatePoint(LayerCtx ctx, RoundDetail det, int j, RawGrid[] grids, int disc, int tab)
    {
        var o = new PointOutcome { Disc = disc, Tab = tab };
        var pd = new PointDetail();
        o.Detail = pd;
        for (int s = 0; s < 2; s++)
        {
            var raw = grids[s].P[disc, tab];
            // K 路（2026-09-15，Opus 5）：场的有效性两态都保留 —— 任一态本格点的单片场判不了（场没收敛、越过熔点、保温分界判不了）⇒ 整格点判不了（逐格点过滤，用户口径「判不了不许当过」）
            if (!raw.Ok) { o.Determined = false; o.Why = $"{StateNames[s]}：{raw.Why}"; return o; }
            var ps = det.PS[j, s];
            var t = ps.Tube;
            bool emptyTube = det.Line.Cases[s]!.EmptyTube;
            bool judged = StateHasHardPlateTerms(emptyTube, ctx.O);
            var sh = ps.Closed
                ? Close(raw.Q, ps.GridDrawAtSelW, t.GammaHot, t.GammaCold, ps.Resp.S, ps.Resp.KappaDisc, ps.Resp.KappaTab)
                : ClosureShift.None;
            bool closedHere = ps.Closed;
            string closureNote = ps.ClosureWhy;
            if (!sh.Ok)
            {
                // K 路（2026-09-15，Opus 5）：闭合不成立是线性化的问题、不是场判不了 —— 本态逐格点只作参考时按冻结管根报参考值，不把格点打成判不了
                if (judged) { o.Determined = false; o.Why = $"{StateNames[s]}：{sh.Why}"; return o; }
                closureNote = sh.Why + "（本态逐格点只作参考，按冻结管根报参考值）";
                sh = ClosureShift.None; closedHere = false;
            }
            var v = new PlateStateView
            {
                Plate = j, Shared = ps.Wp.Shared, State = s, StateName = StateNames[s], EmptyTube = emptyTube,
                ReferenceC = ps.Wp.ReferenceC,
                HotLimitK = ps.Wp.HotLimitK, ColdLimitK = ps.Wp.ColdLimitK,       // 2026-09-15 Opus 5（合并）
                NetInflowW = raw.Q + sh.DrawW,
                DiscPeakC = raw.TDisc + sh.DiscK,
                TabPeakC = raw.TTab + sh.TabK,
                RootHotC = ps.Wp.RootHotC + sh.RootHotK, RootColdC = ps.Wp.RootColdC + sh.RootColdK,
                GammaKPerW = t.GammaMean, GammaHotKPerW = t.GammaHot, GammaColdKPerW = t.GammaCold,
                RootShiftK = sh.RootHotK, RootColdShiftK = sh.RootColdK,
                Closed = closedHere, ClosureNote = closureNote,
            };
            pd.Views[s] = v;
            // K 路（2026-09-15，Opus 5）：工况位由 EvaluateView 按 v.EmptyTube（= 解出本态的整线算例 LineCase.EmptyTube）盖上；卡不卡交付由 Criterion.Kind 查整线的分工况表
            o.Terms.AddRange(EvaluateView(v, ctx.O));
        }
        return o;
    }

    /// <summary>
    /// ★ K 路（2026-09-15，Opus 5）：调评估函数，并把每一项的工况位（<see cref="Criterion.State"/>、<see cref="Criterion.EmptyTube"/>）按 <paramref name="v"/> 盖上 ——
    /// 评估函数可注入，不许靠它自己记得写工况位（写漏了，空管态的项就会按带玻璃稳态卡交付）。<see cref="EvaluatePoint"/> 只经这里调评估函数；公开给门直接验。
    /// </summary>
    public static List<Criterion> EvaluateView(PlateStateView v, Options o)
    {
        if (v is null) throw new ArgumentNullException(nameof(v));
        if (o is null) throw new ArgumentNullException(nameof(o));
        var terms = o.Criteria(v, o) ?? throw new InvalidOperationException($"片{v.Plate} {v.StateName} 的评估函数返回了空");
        foreach (var tm in terms)
        {
            if (tm is null) throw new InvalidOperationException($"片{v.Plate} {v.StateName} 的评估函数返回了空项");
            tm.State = v.StateName; tm.EmptyTube = v.EmptyTube;
        }
        return terms;
    }
}
