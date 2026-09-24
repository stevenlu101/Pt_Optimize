using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **网格无关复核** —— 流水线的**最后一段**（2026-08-28）。
///
/// ★★ 用户 2026-08-28 要的流水线，一句话：
/// <code>
///   输入（3DM / UI 输入框）→ 优化（粗网格导航）→ **网格无关复核** → 报告 + 出图
/// </code>
///   「**我不要设计记录这种模式（这坑太大），要严格遵守第一性原理**」。
///   ⇒ 本类**不是**为了产出一个「设计记录」。它就是**这一次运行的判据以什么为准**。
///     跑完即有结果与图纸，没有谁需要去「落档」。
///
/// ★★ 为什么非要分两级（这是实测逼出来的，不是设计偏好）
///
///   `--meshadapt` 实测：网格无关要到 **0.408 mm**，单次求解 **910 秒**。
///   而 D8 一个形状 40 轮、搜形状几十个形状 ⇒ **在网格无关的网格上做优化，算不完**。
///
///   ⇒ ① 优化用粗网格**导航**（快，找方向与形状）；
///     ② 最终解在网格无关的网格上**复核一次**（慢，只跑一次）；
///     ③ **判据以复核为准** —— 优化过程中的判据值只是导航，**不是结论**。
///
///   ★ APP 此前**只有第 ① 步**，并把第 ① 步的判据值当成了结论。
///     实测后果：那两个历史档记的 ③ 是 5.182/10（看着 48 % 裕度），
///     而网格无关值是 **10.539/10 —— 不过**。
///     几何没问题（铂重 3548 vs 3547 逐位对上），**错的是判定**：
///     2 mm 网格连舌根圆角、环宽、焊脚都画不出来（1.5 / 1.5 / 1.2 格）。
///
/// ⚠ 本类**只复核，不优化**。它不改设计，只回答一句：
///   「这个设计的判据，在算得准的网格上是多少、过不过。」
/// </summary>
public static class MeshVerify
{
    public sealed class Result
    {
        /// <summary>复核停在哪个细网格 mm 上。</summary>
        public double FineMm;
        public int Cells;
        /// <summary>**网格无关的**那一次解 —— 判据以它为准，不是以导航网格那次为准。</summary>
        public LineResult? Line;
        public bool Converged;
        public bool HitCellCap;

        /// <summary>
        /// ★★★★★ R48（2026-09-14，Opus 5）：**这个量在这个网格族上判不了** ——
        /// 加到单元上限了，而序列仍不在渐近区（在摆或不缩）。
        ///
        /// 与 <see cref="Converged"/> = false 的区别：后者含「还能再加一档」，前者是**到头了**。
        /// 振荡不会因为再加密而消失，所以这是个终态，不是「跑久一点就好」。
        ///
        /// ⚠ **调用方必须读它**：判不了时**不许**拿这次的解去顶替原来的解
        /// （LineDesignPage.VerifyMeshAsync 此前只看 Converged 就换 _last）。
        /// 赋了值没人读，是本仓库当天已经栽了三次的形态。
        /// </summary>
        public bool Undecidable;
        /// <summary>最后两档之间每条判据动了多少。</summary>
        public List<MeshAdapt.Delta> LastDeltas = new();
        /// <summary>一句话结论。**没收敛必须明说**，不许含糊。</summary>
        public string Verdict = "";

        /// <summary>
        /// **②″ 的峰落在粗区**时的原话（null = 没这回事）。
        /// 分区加密（A⑭）之后必须查：峰若在粗区，②″ 会被静默算低 ——
        /// 而算低了**不会报错**，只会给一个看着正常的数。调用方必须原样呈现。
        /// </summary>
        public string? PeakOutsideFine;

        /// <summary>
        /// **中带确认**：收敛之后额外做一次「中带也加密」的对照，看判据动不动。
        /// null = 没做；否则是该原样呈现的一句话。
        ///
        /// 为什么必须有它：只加密内带的话，判据可能收敛到一个**由中带的粗糙度决定**的
        /// 错值上 —— 而「内带加密判据不动」这个证据**看不出**这件事。
        /// </summary>
        public string? MidBandConfirm;
        public double SecondsTotal;
        /// <summary>
        /// ★ F7′（2026-09-23，决 29 自适应）：细区半径计划的**终态**（初值、余量₀ 与输入、每次放大的原因与数、终值；拒答时 Refused 非空）。
        /// 判词、证据与界面读它；null = 本次没走到计划（入口拒答）。
        /// </summary>
        public FineRadiusPlan? RadiusPlan;
        /// <summary>
        /// F7′：每一次整线解用的细区半径与解后读到的最远热点、处置（「盖住」「放大 → 重来」「到上限拒答」「峰位算不出」「计划不放大」）—— 按解的先后。
        /// 单向门读它：半径序列必须非降。
        /// </summary>
        public readonly List<(double Fine, double RadiusMm, double PeakRMm, string Outcome)> RadiusTrace = new();
        /// <summary>F7′：因放大半径而作废的档（在旧半径上解的，不参与档间比较；只供打印）。</summary>
        public readonly List<(double Fine, int Cells, double RadiusMm, double Sec)> DiscardedTiers = new();
        /// <summary>逐档轨迹，供报告打印。</summary>
        public readonly List<(double Fine, int Cells, double N2p, double N2pp, double N3, double MassG, double Sec)>
            Trace = new();
    }

    /// <summary>
    /// 每条判据的复核容差（相邻两档加密之间，这条判据允许动多少还算「数不再变」）。命令行 `--meshadapt` 也读这一份，不另抄。
    ///
    /// ★★★★★ 2026-09-14 Opus 5（复审）：复核的三条从「管孔净流入／圆盘区最高温 − 管温／法兰增量温降」换成
    /// 「管孔净流入／最热铂高出热偶读数／管根低于热偶读数」（R48 B），**容差与名字一起重定**：
    /// <code>
    ///   判据                      旧容差                            新容差
    ///   管孔净流入                0.5 W（= --selfcheck 对账容差）    0.5 W（不变）
    ///   圆盘区最高温 − 管温       0.2 K（= --selfcheck）             —— 降为参考量，不再复核
    ///   法兰增量温降              1.0 K（限值 10 的 10 %，= CoupleTolK）—— 降为参考量，不再复核
    ///   最热铂高出热偶读数        —                                  0.5 K = 热偶误差 5 K 的 10 %
    ///   管根低于热偶读数          —                                  0.5 K = 热偶误差 5 K 的 10 %
    /// </code>
    /// ⚠ R48 L（2026-09-17，Opus 5）：上表里那句「= CoupleTolK」说的是**当时**的耦合停机容差（1 K）。
    ///   现在外层耦合的停机容差是**按判据裕度算出来的**（<see cref="LineRunner.CoupleTolKFor"/>，实测落在 0.036～0.1 K），
    ///   无法兰基线那一层才留着 1 K（<see cref="LineCase.BaselineTolK"/>，它服务的正是这条已降为参考量的「法兰增量温降」）。
    ///   ⚠⚠ **本表自己还没按同一条原则重定**：两条新判据的复核容差 0.5 K，比在跑的那份设计的裕度（0.44～0.49 K）**还粗** ——
    ///   与本轮修掉的那个病是同一个形状，只是发在「网格复核」这一层。要动它得先实测，见 HANDOVER §0.-10 ⑥ 第 7 条。
    /// 为什么是「限值的 10 %」：旧的法兰增量温降就是这个比例（1.0／10）；两条新判据都是「某点温度 − 常数基准」，
    /// 外层耦合的剩余误差**直接**进值，不像「盘峰 − 管根」那样被相减抵掉 —— 所以旧的 0.2 K 不能照搬（审查 2026-09-14：
    /// 那次单次判定耦合剩余误差估计已有 0.29 K，deliverable/r48B_热偶基准_现役档单次判定_2026-09-14.txt）。
    /// 冷侧限值从 10 降到 5，若沿用 1.0 K 就占到限值的 20 %，放行变松。
    /// ⚠ 0.5 K **还没在复核网格上实测过**：两档耦合剩余误差之和可能超过它（上面那次 0.29 × 2 = 0.58 K）。
    ///   届时贴着限值的设计会一直判不收敛 —— 进度行对这两条都会印「与耦合停机噪声分不开」，远离限值时由「结论稳」放行。
    ///   该取多少要在复核网格上量过再定（交接列为待办）；改这里时 MeshVerifyTests 一起改。
    /// ⚠ 名字用 <see cref="Criteria.Plain"/> 的全名：判词（<see cref="MeshAdapt.Verdict"/>）原样印 Delta.Name，经判据页输出框进界面，
    ///   此前印的是「②″ +0.004/0.2」这种代号。
    /// ⚠ 与 `--selfcheck` 不再「同口径」：自检对账核的是**记录栏位**（DiscOverK／FlangeDipK 装的是旧判法的数），容差仍是 0.20／1.00 K。
    /// </summary>
    /// ★★★★★ K 路（2026-09-15，Opus 5；审查 P1-9「第三处写死三条」）：**复核哪几条不在这里写死** —— 随网格变的判据各自的容差放在 <see cref="MeshTolerances"/>，
    ///   按整线判据的分工况表（<see cref="LineResult.StateKindOf"/>，与 LineRunner.Judge 同一份）过滤：本工况只作参考的不复核。
    ///   带玻璃稳态 = 原来那三条（次序、名字、容差逐位不变）；空管到温稳态 = 空（用户 2026-09-15：空管态只卡电流密度（管 J 与法兰截面 J）与场的有效性，三条都只作参考；2026-09-16 Opus 5 改措辞）
    ///   ⇒ <see cref="Run(Func{double, double, LineCase}, double, double, double, int, int, IProgress{string}?, CancellationToken)"/> 对复核名单与主循环读的三条对不上的算例拒答。
    /// ★ U 路（2026-09-18，Opus 5）：签名从 <c>bool emptyTube</c> 换成**整线算例** —— 热侧／冷侧的复核容差是
    ///   「各自限值的 10 %」，而限值现在跟着工程师填的温差预算走（<see cref="LineCase.HotOverTcMaxK"/>／<see cref="LineCase.ColdUnderTcMaxK"/>）。
    ///   传 bool 就只能再抄一个 5 —— 那正是本项目最常见的失效。工况仍从算例的 <see cref="LineCase.EmptyTube"/> 读。
    public static IReadOnlyList<MeshAdapt.Delta> TolTemplate(LineCase c) => MeshTolerances
        .Where(m => LineResult.StateKindOf(m.Key, c.EmptyTube, c.RuleSet) is not CheckKind.Reference)   // 决 103：按算例的判据口径取（改回口径 = 改前三条）
        .Select(m => new MeshAdapt.Delta { Name = Criteria.Plain(m.Key), Tol = m.Tol(c) })
        .ToArray();

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：随网格变、需要加密复核的判据与各自的容差（出处与理由见 <see cref="TolTemplate"/> 的注释表）。**只是容差表，不含工况**：
    /// 哪个工况复核哪几条由 <see cref="TolTemplate"/> 按分工况表过滤。次序 = 主循环 Trace 的列次序（净流入／热侧／冷侧）。
    /// </summary>
    /// ★ U 路（2026-09-18，Opus 5）：容差那一列改成**按算例取**的函数 —— 热侧／冷侧是各自限值的
    ///   <see cref="TcMeshTolFrac"/>，限值跟着工程师填的温差预算走；净流入是绝对值，与温差预算无关。
    public static readonly (string Key, Func<LineCase, double> Tol)[] MeshTolerances =
    {
        (LineResult.Key.NetFlux,     _ => NetFluxMeshTolW),
        (LineResult.Key.HotOverTc,   c => TcMeshTolFrac * c.HotOverTcMaxK),
        (LineResult.Key.ColdUnderTc, c => TcMeshTolFrac * c.ColdUnderTcMaxK),
    };

    /// <summary>管孔净流入这一条的复核容差 W（与温差预算无关；出处与理由见 <see cref="TolTemplate"/>）。2026-09-14 Opus 5。</summary>
    public const double NetFluxMeshTolW = 0.5;

    /// <summary>热侧／冷侧两条的复核容差 = **各自限值的这个比例**（出处与理由见 <see cref="TolTemplate"/>：旧的法兰增量温降就是 1.0／10）。2026-09-14 Opus 5；U 路 2026-09-18 由「热偶误差的 10 %」改写成「限值的 10 %」，默认 5 K 下逐位不变（0.5 K）。</summary>
    public const double TcMeshTolFrac = 0.1;

    /// <summary>
    /// K 路（2026-09-15，Opus 5）：本工况能不能用加密复算的主循环 —— 主循环逐档读、比的是 <see cref="MeshTolerances"/> 那三列；
    /// 本工况按分工况表该复核的（<see cref="TolTemplate"/>）不是恰好这三条 ⇒ 返回拒答原句（进判词、会上界面，不带判据代号），否则 null。
    /// 空管到温稳态：三条都只作参考、没有随网格变的卡交付判据 ⇒ 拒答；该工况的场有效性（没收敛、越过熔点、散热表超界）由整线结果自己判（LineResult.FieldUndeterminedReasons）。
    /// </summary>
    public static string? RefuseForState(LineCase c)
    {
        bool emptyTube = c.EmptyTube;
        var want = TolTemplate(c).Select(d => d.Name).ToArray();
        var loop = MeshTolerances.Select(m => Criteria.Plain(m.Key)).ToArray();
        if (want.SequenceEqual(loop)) return null;
        // ★ 决 103（2026-09-24）：生产口径的带玻璃稳态卡交付的是「法兰最热处高出管接触处温度」「管接触处流入法兰的净热流」与两条热稳定，
        //   而本循环逐档比的仍是改前那三条 ⇒ 拒答，不拿参考量的收敛冒充硬判据的收敛。比对列换成新判据要先定新判据各自的网格容差（热侧可沿用「限值的 10 %」、
        //   冷侧沿用 0.5 W；两条热稳定没有出处）—— 列为待办，不在这里编。
        if (!emptyTube && c.RuleSet != CriteriaRuleSet.决103前)
            return "✗ 加密复算的逐档比对列还是 2026-09-24 之前卡交付的三条（" + string.Join("、", loop) + "），"
                 + "现行带玻璃稳态卡交付的「" + string.Join("、", LineResult.RequiredFor(false, c.RuleSet).Select(q => Criteria.Plain(q.Prefix))
                        .Where(nm => MeshTolerances.All(m => Criteria.Plain(m.Key) != nm))) + "」没有进比对列"
                 + "　⇒ 不在这个口径上做加密复算，**不能据此说这个设计过了**（比对列换新判据待定网格容差）。";
        return $"✗ 加密复算不适用于{(emptyTube ? "空管到温稳态" : "带玻璃稳态")}："
             + (want.Length == 0 ? "本工况没有进加密复算比对列的卡交付判据（空管到温稳态只卡电流密度（管 J 与法兰截面 J）与场的有效性；两条电流密度判据两态都不在逐档比对列里）"
                                 : $"本工况要复核的是「{string.Join("、", want)}」，加密复算逐档比的是「{string.Join("、", loop)}」")
             + "　⇒ 不在这个工况上做加密复算，**不能据此说这个设计过了**；该工况的场有没有解到位看整线结果自己的判定。";
    }

    /// <summary>
    /// 对一个**已经算出来的设计**做网格无关复核。
    ///
    /// 起点由几何特征定（最小特征 ÷ 3），每轮对半加密，
    /// 停机由**判据本身**定（每条判据的变化落进各自容差），或撞上单元数上限。
    /// </summary>
    /// <summary>
    /// **这个几何要多细的网格** —— 由几何特征算出，不是挑的数。
    ///
    /// ★ 全程序只有这一份（2026-08-28 提出来）：<see cref="Solver"/> 的第二遍求根
    ///   与本类的复核**必须用同一张网格**，否则「求根的网格」与「判决的网格」不是同一个，
    ///   求出来的根照样不作数 —— 而那正是 A⑬ 要修的病。
    /// </summary>
    /// <param name="weldAsGeometricFeature">
    /// 焊脚算不算「**几何特征**」（2026-08-29 提出来的问题，默认 true = 保持历史口径）。
    ///
    /// ★ 「每个特征 3 格」这条规则是给**几何特征**用的，而这三样不是一类东西：
    /// <code>
    ///   舌根圆角 R3 —— **几何边界**（真实轮廓曲线）      ⇒ 格子要跨过它
    ///   环宽        —— 厚度的**阶跃**（不连续）         ⇒ 格子要定位那个跳变
    ///   焊脚        —— **平滑的厚度斜坡**（凹圆弧函数）  ⇒ 只要积得准，不需要分得开边
    /// </code>
    /// 代码自己写着焊缝是厚度场不是边界：
    /// <c>weld = 2·WeldFilletHeightMm(r − 孔半径, 焊脚)</c>，直接加在 <c>ThicknessAt</c> 上。
    ///
    /// ⇒ 焊脚在表里会把网格拖细：0.6 档实测 weldLeg = 1.75 ⇒ 0.583 mm，
    ///   而圆角/环宽给的是 1.0 mm。**拿掉它，起始网格粗 1.7 倍，每档单元数少约 3 倍。**
    ///
    /// ★★ **2026-08-29 默认已翻为 false（焊脚不算几何特征）。**
    ///
    /// ⚠ 我先前说「必须实测对照才准改默认」——**那句话把守门人认错了**，在此更正：
    ///   本参数**只影响网格无关阶梯从哪一级起步**，**不改变任何给定网格上的物理**：
    ///   同一个 h，加不加焊脚进特征表，算出来的判据**逐位相同**。
    ///   ⇒ 「焊脚出表会不会把判据算偏」这个问法不成立。
    ///     真正的守门人是**收敛判据**：从 1.0 mm 起步照样要一路加密到
    ///     「判据不再变」才算数。**起步粗是少爬两级，不是少验一道。**
    ///
    /// ⚠ 唯一的真风险是通用的、与焊脚无关的：起步粗会让「碰巧两级之间没动」的
    ///   假收敛概率略升 —— 那是收敛判据本身固有的风险。
    ///   `--weldfeature` 可以退回旧口径做对照。
    /// </param>
    /// <param name="baseIn">工艺参数 —— 细区半径初值的余量₀ 是设计的热长度 ℓ_t（<see cref="MeshAdapt.ThermalLengthMm"/>），要按本设计的算例（控温点、管、保温、牌号）算。</param>
    /// <param name="tabLengthFactor">
    /// ★ F7′（2026-09-23，Opus 5.5，C4′；决 29 改定为「自适应」）：只供门用的「改回」参数，生产一律不传。
    ///   缺省 0 = 生产：细区半径**初值** = max(盘半径, 孔半径) + ℓ_t（规则与放大见 <see cref="MeshAdapt.FineRadiusPlanOf"/>／<see cref="MeshAdapt.GrowFineRadius"/>）。
    ///   传 <see cref="LegacyTabLengthFactor"/>（0.35）= F7 之前的规则 max(盘半径, 0.35·|舌长|, 孔半径) + 10，半径逐位等于改前、且不放大（门：R48F7AdaptiveRadiusTests 门 e）。
    ///   ⚠ 本函数只给**初值**（解之前能知道的那个数）；解出场之后按热点放大的在用计划的各处（本类 Run、Solver、保温搜索、可行窗口）。
    ///   决 29 (1)（开发者缺省，不是业主原话）已作废：它与热点检查相顶，见 C4 实施记录 §3.2 与 §10。
    /// </param>
    public static (double FineMm, double RadiusMm) RequiredMeshFor(DesignSpec d, DesignInputs baseIn,
                                                                   bool weldAsGeometricFeature = false,
                                                                   double tabLengthFactor = 0.0)
        => (RequiredFineMmFor(d, weldAsGeometricFeature), FineRadiusPlanFor(d, baseIn, tabLengthFactor).RadiusMm);

    /// <summary>
    /// F7′（2026-09-23）：只要**细区尺寸**（由几何特征定，与工艺参数无关）的调用方用这一个 —— 原 <c>RequiredMeshFor(d).FineMm</c> 的那一半，逐字同式。
    /// </summary>
    public static double RequiredFineMmFor(DesignSpec d, bool weldAsGeometricFeature = false)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        double weldLeg = Math.Max(d.TabThickMm.Max(), d.WallMm);
        var feats = weldAsGeometricFeature
                  ? new[] { d.TabFilletMm, d.RingWidthMm, weldLeg }
                  : new[] { d.TabFilletMm, d.RingWidthMm };
        return FineFromFeatures(feats);
    }

    /// <summary>
    /// ★ F7′（2026-09-23，Opus 5.5，C4′）：解析设计的**细区半径计划**（全仓唯一来源 <see cref="MeshAdapt.FineRadiusPlanOf"/> 的解析入口）。
    /// 热长度与板料外缘都按本设计的算例（<see cref="DesignSpec.BuildCase"/>，带玻璃稳态）量：ℓ_t = <see cref="MeshAdapt.ThermalLengthMm"/>，上限 = <see cref="MeshAdapt.PlateOuterRadiusMm(LineCase)"/>。
    /// <paramref name="adaptive"/> = false 只供归因门（新初值、不放大）。改回（tabLengthFactor &gt; 0）不量热长度（旧规则不用它）。
    /// </summary>
    public static FineRadiusPlan FineRadiusPlanFor(DesignSpec d, DesignInputs baseIn, double tabLengthFactor = 0.0, bool adaptive = true)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (baseIn is null) throw new ArgumentNullException(nameof(baseIn));
        if (d.IsDrawingRecord) throw new InvalidOperationException(DesignSpec.DrawingRefusal + $"（档「{d.Name}」）—— 图纸档的细区半径计划走 FineRadiusPlanFor(Shape, …)。");
        if (double.IsNaN(tabLengthFactor) || tabLengthFactor < 0)
            throw new ArgumentOutOfRangeException(nameof(tabLengthFactor), "舌长系数只能是 0（生产）或正数（改回对照）。");
        // 改回（旧规则、不放大）既不用热长度也不用上限 ⇒ 不造算例（门在大网格上逐点调它，造算例要几十毫秒一次）。
        if (tabLengthFactor > 0)
            return MeshAdapt.FineRadiusPlanOf(d.DiscRadiusMm, d.HoleRadiusMm, d.TabLengthMm, double.NaN, "", double.NaN, "改回旧规则不放大，不量上限", tabLengthFactor, adaptive);
        var lc = d.BuildCase(baseIn, checkRamp: false);
        var (cap, capSrc) = MeshAdapt.PlateOuterRadiusMm(lc);
        var (ell, ellSrc) = MeshAdapt.ThermalLengthMm(lc);
        return MeshAdapt.FineRadiusPlanOf(d.DiscRadiusMm, d.HoleRadiusMm, d.TabLengthMm, ell, ellSrc, cap, capSrc, tabLengthFactor, adaptive);
    }

    /// <summary>
    /// ★ F7′（2026-09-23）：图纸路径的细区半径计划 —— 盘半径、孔半径、舌端取图纸分析结果；热长度按 <paramref name="thermalCase"/>（走图纸路径的整线算例）量；
    /// 上限先从算例量（内存厚度场的材料包络），量不到（只有 .3dm 文件路径）再取图纸分析结果的外廓（<see cref="MeshAdapt.PlateOuterRadiusMm(PlateShapeAnalyzer.Shape)"/>）。
    /// </summary>
    public static FineRadiusPlan FineRadiusPlanFor(PlateShapeAnalyzer.Shape sh, LineCase thermalCase, double tabLengthFactor = 0.0, bool adaptive = true)
    {
        if (sh is null) throw new ArgumentNullException(nameof(sh));
        if (thermalCase is null) throw new ArgumentNullException(nameof(thermalCase));
        if (double.IsNaN(tabLengthFactor) || tabLengthFactor < 0)
            throw new ArgumentOutOfRangeException(nameof(tabLengthFactor), "舌长系数只能是 0（生产）或正数（改回对照）。");
        double tabLen = double.IsNaN(sh.TabEndXMm) ? 0 : Math.Abs(sh.TabEndXMm);
        if (tabLengthFactor > 0)
            return MeshAdapt.FineRadiusPlanOf(sh.DiscRadiusMm, sh.HoleRadiusMm, tabLen, double.NaN, "", double.NaN, "改回旧规则不放大，不量上限", tabLengthFactor, adaptive);
        var (cap, capSrc) = MeshAdapt.PlateOuterRadiusMm(thermalCase);
        if (!double.IsFinite(cap)) (cap, capSrc) = MeshAdapt.PlateOuterRadiusMm(sh);
        var (ell, ellSrc) = MeshAdapt.ThermalLengthMm(thermalCase);
        return MeshAdapt.FineRadiusPlanOf(sh.DiscRadiusMm, sh.HoleRadiusMm, tabLen, ell, ellSrc, cap, capSrc, tabLengthFactor, adaptive);
    }

    /// <summary>F7 之前的规则里舌长那一项的系数（出处未查到：代码自 6f191b2 起就是这个数，HANDOVER 只列了公式）。只供门做「改回」对照，生产不用。</summary>
    public const double LegacyTabLengthFactor = 0.35;

    /// <summary>「特征尺寸 → 网格尺寸」只有这一处（解析设计与图纸路径共用）：最小特征 ÷ 每特征格数。</summary>
    private static double FineFromFeatures(IEnumerable<double> featureSizesMm) => MeshAdapt.RequiredFineMm(featureSizesMm);

    /// <summary>
    /// ★ R47 C（2026-09-13）：**图纸路径**的「这个几何要多细的网格」—— 特征尺寸从 <see cref="PlateShapeAnalyzer"/>
    /// 的分级取（各级的环宽、槽的径向宽；焊脚可选 = max(最厚一级, 管壁)），不再拿解析设计的圆角／环宽去猜图纸。
    /// 取不到（图纸没分析出任何一级）就**拒答**：返回 Refused 非空，调用方把它原样写进结果，不抛。
    /// 病：此前 .3dm 模式的加密复算拿 PageToDesignSpec 造的解析板复核 —— 验的是另一个零件。
    /// </summary>
    /// <remarks>F7′（2026-09-23）：半径是细区半径计划（<see cref="FineRadiusPlanFor(PlateShapeAnalyzer.Shape, LineCase, double, bool)"/>）的初值，热长度按 <paramref name="thermalCase"/>（走图纸路径的整线算例）量。</remarks>
    public static (double FineMm, double RadiusMm, double InnerRadiusMm, string? Refused)
        RequiredMeshFor(PlateShapeAnalyzer.Shape sh, double wallMm, LineCase thermalCase, bool weldAsGeometricFeature = false, double tabLengthFactor = 0.0)
    {
        var (fine, innerR, refused) = ShapeFeatureMesh(sh, wallMm, weldAsGeometricFeature);
        if (refused is not null) return (double.NaN, double.NaN, double.NaN, refused);
        // F7′（2026-09-23）：半径 = 细区半径计划的初值（全仓唯一来源；热长度按 thermalCase 量）；tabLengthFactor 只供门改回，生产不传。
        double radius = FineRadiusPlanFor(sh, thermalCase, tabLengthFactor).RadiusMm;
        return (fine, radius, innerR, null);
    }

    /// <summary>F7′（2026-09-23）：图纸路径的细区尺寸、内带半径与拒答（原 RequiredMeshFor(Shape) 里与半径无关的那部分，逐字搬出；不需要算例）。</summary>
    private static (double FineMm, double InnerRadiusMm, string? Refused) ShapeFeatureMesh(PlateShapeAnalyzer.Shape sh, double wallMm, bool weldAsGeometricFeature)
    {
        if (sh is null) throw new ArgumentNullException(nameof(sh));
        var feats = new List<double>();
        // 环宽（盘半径 − 孔半径）：与解析设计的 RingWidthMm 同一个量，是圆盘上最小的几何特征之一
        if (sh.HoleRadiusMm > 0 && sh.DiscRadiusMm > sh.HoleRadiusMm + 1e-9) feats.Add(sh.DiscRadiusMm - sh.HoleRadiusMm);
        // 各级的径向宽：**只取宽于 3 个栅格步的级** —— 分析器按 0.011 mm 聚类厚度，焊缝的凹圆弧、倒角这类
        //   连续过渡会被切成几十条发丝级「级」（实测 0.006 mm 宽 ⇒ 起始网格 0.002 mm，直接把内存吃光）；
        //   窄于 3 格的级本来就在栅格的分辨能力之下，不是图纸上的设计特征。
        double floorW = double.IsNaN(sh.StepMm) || sh.StepMm <= 0 ? 0 : 3.0 * sh.StepMm;
        foreach (var lv in sh.Levels)
            if (lv.RInnerMm < double.MaxValue && lv.ROuterMm - lv.RInnerMm >= Math.Max(floorW, 1e-9)) feats.Add(lv.ROuterMm - lv.RInnerMm);
        if (sh.Slot.Found && sh.Slot.ROuterMm > sh.Slot.RInnerMm + 1e-9) feats.Add(sh.Slot.ROuterMm - sh.Slot.RInnerMm);
        double tMax = sh.Levels.Count > 0 ? sh.Levels.Max(l => l.ThicknessMm) : 0;
        double weldLeg = Math.Max(tMax, wallMm);
        if (weldAsGeometricFeature && weldLeg > 1e-9) feats.Add(weldLeg);
        if (feats.Count == 0 || !(sh.HoleRadiusMm > 0))
            return (double.NaN, double.NaN,
                    "图纸没分析出特征尺寸（没有厚度分级或没有管孔），加密复算不能判 —— 请先做「分析几何变数」并确认图层里有这片法兰。");
        double fine = FineFromFeatures(feats);
        double innerR = MeshAdapt.InnerRadiusFor(sh.HoleRadiusMm, weldLeg);
        return (fine, innerR, null);
    }

    public static Result Run(DesignSpec d, DesignInputs baseIn,
                             int maxCells = 40000, int maxRounds = 6,
                             bool weldAsGeometricFeature = false,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        // ★ R47 第三轮 N5（2026-09-13）：图纸档没有解析板 —— 拒答、不抛、不算（Converged=false，Verdict 原句）。
        if (d.IsDrawingRecord)
        {
            var refused = new Result { Converged = false, Verdict = DesignSpec.DrawingRefusal + $"（档「{d.Name}」）" };
            progress?.Report("⚠ " + refused.Verdict);
            return refused;
        }
        // F7′（2026-09-23，决 29 自适应）：细区尺寸由几何特征定；细区半径由计划给（初值 + 解后按热点放大，全仓唯一来源）。
        double h0 = RequiredFineMmFor(d, weldAsGeometricFeature);
        var plan = FineRadiusPlanFor(d, baseIn);
        double radius = plan.RadiusMm;
        double weldLegV = Math.Max(d.TabThickMm.Max(), d.WallMm);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, weldLegV);
        // ★ R47 C（2026-09-13）：每档造 LineCase 的活抽成工厂，本重载只负责「解析设计怎么造」。
        //   .3dm 模式由页面把自己的 LineCase（FlangePlates 为空、FlangeFile3dm 非空）交给下面那个工厂重载，
        //   不再拿 PageToDesignSpec 造的解析板去复核另一个零件。
        return Run(AnalyticCaseFactory(d, baseIn, radius, innerR),
                   h0, plan, innerR, maxCells, maxRounds, progress, cancel);
    }

    /// <summary>
    /// ★★★★★ R48（2026-09-13，Opus 5）：**解析路径「这一档的算例长什么样」—— 公开，只有这一处。**
    ///
    /// 抽出来是为了让门能拿到**生产代码真正用的那个工厂**去比对。
    /// 此前它是 <see cref="Run(DesignSpec,DesignInputs,int,int,bool,IProgress{string}?,CancellationToken)"/>
    /// 里的一个匿名 lambda，测试够不着 ⇒ 只能自己手抄一份配方去对比 ——
    /// 而「各自手抄一份」正是 09-13 那次事故的形态本身：
    /// 求根抄的那份漏了粗区与内带，于是求根说「全过」、复核说 管孔净流入 −1.79 W。
    /// 手抄的门守不住手抄的病。
    ///
    /// 加密的配方本身在 <see cref="MeshAdapt.RefineWholeMesh"/>（全项目唯一一处）：
    /// 粗区按比例缩、内带与中带同尺寸 ⇒ 细粗比恒定、加密是自相似的。
    /// 实测同一设计（管壁 0.8，整线全耦合）：
    /// 只缩细区 管孔净流入 5.090／7.134／5.068 W（跳，加密也不收敛）；
    /// 整张缩 5.919／6.196／6.325 W、法兰增量温降 22.719／22.113／21.846 K（差值比 0.47 与 0.44，干净的一阶收敛）。
    /// </summary>
    /// <param name="hInnerOverride">分区加密的老口径出口：内带与中带不同尺寸时才用得上。
    ///   整档一起加密（R48 起的默认）时它等于中带，这里不做任何事。</param>
    public static Func<double, double, LineCase> AnalyticCaseFactory(
        DesignSpec d, DesignInputs baseIn, double radiusMm, double innerRadiusMm)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        return (hMid, hInnerOverride) =>
        {
            var lc = d.BuildCase(baseIn, checkRamp: true);
            MeshAdapt.RefineWholeMesh(lc, hMid, radiusMm, innerRadiusMm);
            if (hInnerOverride > 0 && Math.Abs(hInnerOverride - hMid) > 1e-9) lc.MeshInnerMm = hInnerOverride;
            return lc;
        };
    }

    /// <summary>
    /// ★ R47 C（2026-09-13）：图纸路径的复核入口 —— 特征尺寸从分析结果取（<see cref="RequiredMeshFor(PlateShapeAnalyzer.Shape,double,bool)"/>），
    /// 取不到就拒答（Result.Verdict 写清楚、Converged=false，不抛）。<paramref name="caseFactory"/> 由调用方给：
    /// 每档 (中带 h, 内带 h) 造一个走图纸路径的 LineCase（FlangePlates 为空、FlangeFile3dm 或 FlangeFields 非空），
    /// 栅格步长由 LineRunner 按网格自己收（min(ThicknessStepMm, h/4)）。
    /// </summary>
    public static Result Run(PlateShapeAnalyzer.Shape shape, double wallMm,
                             Func<double, double, LineCase> caseFactory,
                             int maxCells = 40000, int maxRounds = 6,
                             bool weldAsGeometricFeature = false,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
    {
        if (caseFactory is null) throw new ArgumentNullException(nameof(caseFactory));
        // F7′（2026-09-23）：热长度要按走图纸路径的整线算例量 ⇒ 先让工厂造一个（只造不解；造算例不载 .3dm、不解场）。
        //   拒答（没分析出特征尺寸）在造算例之前判，与改前同序。
        var (h0, innerR, refused) = ShapeFeatureMesh(shape, wallMm, weldAsGeometricFeature);
        if (refused is not null)
        {
            var r0 = new Result { Verdict = "✗ " + refused + "　⇒ **不能说这个设计过了**", Converged = false };
            progress?.Report(r0.Verdict);
            return r0;
        }
        var plan = FineRadiusPlanFor(shape, caseFactory(h0, h0));
        return Run(caseFactory, h0, plan, innerR, maxCells, maxRounds, progress, cancel);
    }

    /// <summary>
    /// ★ F7（2026-09-23，Opus 5.5）：「细区盖没盖住热点」—— 由 <see cref="Run(Func{double, double, LineCase}, double, double, double, int, int, IProgress{string}?, CancellationToken)"/> 主循环原样抽出（逐字同式），
    /// 热点门（R48F7FineRadiusDecoupleTests）调同一份。取三种热点里最远的：每片 圆盘区峰 DiscMaxRMm、舌片区峰 TabMaxRMm、局部热稳定 LocalStabRMm；
    /// 任一片三者全算不出 ⇒ 判不了。null = 盖住了（峰 + 余量 ≤ 细区半径）；否则返回原样呈现给人的那句话（MeshAdapt.PeakVerdict）。
    /// </summary>
    public static string? HotspotVerdict(LineResult r, double innerR, double radius)
    {
        if (r is null) throw new ArgumentNullException(nameof(r));
        var blindPeak = r.Flanges.Where(f => double.IsNaN(f.DiscMaxRMm) && double.IsNaN(f.TabMaxRMm)
                                          && double.IsNaN(f.LocalStabRMm)).Select(f => f.Name).ToArray();
        double peakR = HotspotRadiusMm(r);
        string? peakBad = MeshAdapt.PeakVerdict(peakR, innerR, radius);
        if (peakBad is not null && blindPeak.Length > 0) peakBad += $"（算不出热点位置的片：{string.Join("、", blindPeak)}）";
        return peakBad;
    }

    /// <summary>F7（2026-09-23）：<see cref="HotspotVerdict"/> 用的「最远热点半径」mm；没有片、或任一片三种热点全算不出 ⇒ NaN。</summary>
    public static double HotspotRadiusMm(LineResult r)
    {
        if (r is null) throw new ArgumentNullException(nameof(r));
        bool blind = r.Flanges.Any(f => double.IsNaN(f.DiscMaxRMm) && double.IsNaN(f.TabMaxRMm) && double.IsNaN(f.LocalStabRMm));
        return r.Flanges.Length == 0 || blind ? double.NaN
             : r.Flanges.SelectMany(f => new[] { f.DiscMaxRMm, f.TabMaxRMm, f.LocalStabRMm })
                .Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
    }

    /// <summary>
    /// ★★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-5）：这一档的整线解能不能拿来比「网格变没变」—— "" = 能；非空 = 不能的原因（人话）。
    /// 三件事任一件都不能用：外层耦合没收敛（LineResult.Converged 自己写着「为 false 时表内所有数值一律不可引用」）；
    /// 带「判不了」后置标记（与判据表、求解器同一个定义 <see cref="LineRunner.PlateUndeterminedWhy"/>）；
    /// 复核的三条交付判据（管孔净流入、最热铂高出热偶读数、管根低于热偶读数）里有判不了或没有数的。
    /// 此前主循环只挡 Ok = false，没收敛或判不了的数照样进档间差值，照样可能打上「网格无关」并被界面当已验（LineDesignPage.VerifyMeshAsync 读 Converged）。
    /// </summary>
    /// <summary>F7′：按判据名前缀取实际值（与主循环里的 V 同一式；到上限拒答那一支记轨迹用）。</summary>
    private static double V0(LineResult r, string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;

    public static string TierUnusableWhy(LineResult r)
    {
        if (r is null) return "没有整线解";
        var why = new List<string>();
        if (!r.Converged)
            why.Add("外层耦合没收敛" + (double.IsNaN(r.CoupleRemainK) ? "" : $"（剩余误差估计 {r.CoupleRemainK:0.###} K）"));
        string und = LineRunner.PlateUndeterminedWhy(r, -1);
        if (und.Length > 0) why.Add("判不了：" + und);
        foreach (var key in new[] { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc })
        {
            var c = r.Find(key);
            if (c is null) why.Add($"判据表里没有「{Criteria.Plain(key)}」");
            else if (c.Undetermined || double.IsNaN(c.Actual)) why.Add($"「{Criteria.Plain(key)}」判不了");
        }
        return string.Join("；", why);
    }

    /// <summary>
    /// ★ R47 C（2026-09-13）：**工厂重载** —— 逐档加密的主循环。<paramref name="caseFactory"/>(中带 h, 内带 h) 每档造一个 LineCase；
    /// 解析设计与图纸路径共用这一段，差别只在工厂怎么造。
    /// </summary>
    /// <param name="h0">起始网格 mm（由几何特征算出，不是挑的数）。</param>
    /// <param name="radius">中带（细化）半径的**初值** mm（调用方给定；F7′ 起按热点自适应放大，同生产，见下一个重载）。</param>
    /// <param name="innerR">内带半径 mm。</param>
    public static Result Run(Func<double, double, LineCase> caseFactory,
                             double h0, double radius, double innerR,
                             int maxCells = 40000, int maxRounds = 6,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
        => Run(caseFactory, h0, MeshAdapt.GivenFineRadiusPlan(radius), innerR, maxCells, maxRounds, progress, cancel);

    /// <summary>
    /// ★ F7′（2026-09-23，Opus 5.5，C4′；决 29 自适应）：**逐档加密的主循环**（工厂重载的本体）。细区半径由 <paramref name="plan"/> 给：
    /// 每档解完核「细区盖没盖住热点」（<see cref="HotspotVerdict"/>，判法 <see cref="MeshAdapt.PeakVerdict"/> 一个数不动），
    /// 盖不住且计划自适应 ⇒ 按 <see cref="MeshAdapt.GrowFineRadius"/> 放大半径、**从第一档重来**（档间比较必须同一族网格：只差 h，半径不许在档间变 —— 同 Solver 里 navOpt 那段记的 9.8 W 病）；
    /// 放大到上限仍盖不住 ⇒ 当场拒答返回（不再空跑完阶梯，审查 P8）。计划不放大（改回旧规则、归因用的 adaptive = false）⇒ 与改前**同一条控制流**：每档记下那句话、跑完阶梯、判「不算数」
    /// （审查 T6：这条控制流只由门 d 的合成注入验过；逐位只在「固定半径单次场解」上由门 e 验过，本循环在改回计划下没有实场逐位门）。
    /// 终止：每次放大至少一粗格、上限有限 ⇒ 放大次数有限；每次重来最多 <paramref name="maxRounds"/> 档。
    /// </summary>
    public static Result Run(Func<double, double, LineCase> caseFactory,
                             double h0, FineRadiusPlan plan, double innerR,
                             int maxCells = 40000, int maxRounds = 6,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
        => RunCore(caseFactory, h0, plan, innerR, maxCells, maxRounds, progress, cancel, (lc, pr, ct) => LineRunner.Run(lc, pr, ct));

    /// <summary>
    /// F7′（2026-09-23）：主循环本体。<paramref name="solve"/> = 整线解（生产恒为 <see cref="LineRunner.Run"/>）；只给门（InternalsVisibleTo）注入合成的整线结果，
    /// 在毫秒内验「放大 → 重来 → 半径只增不减 → 到上限拒答、不死循环」这套控制逻辑（放大、判法、拒答文字全是生产这一份）。
    /// </summary>
    internal static Result RunCore(Func<double, double, LineCase> caseFactory,
                                   double h0, FineRadiusPlan plan, double innerR,
                                   int maxCells, int maxRounds,
                                   IProgress<string>? progress, CancellationToken cancel,
                                   Func<LineCase, IProgress<string>?, CancellationToken, LineResult> solve)
    {
        if (caseFactory is null) throw new ArgumentNullException(nameof(caseFactory));
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (solve is null) throw new ArgumentNullException(nameof(solve));
        if (!(h0 > 0)) throw new ArgumentOutOfRangeException(nameof(h0), "起始网格必须为正");
        var res = new Result { RadiusPlan = plan };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report("加密复算：" + plan.Describe());

        // ★★ 按特征分区（A⑭，2026-08-29）：**只加密内带**。
        //   此前是把「按最小特征（焊脚）定的极细尺寸」铺满「按最大特征（盘径/舌长）定的大区域」，
        //   每加密一档单元数 ×4 —— 0.6 档实测收到 0.146 mm 时约 8 万单元，
        //   **超过 maxCells 上限，跑了八小时没出数**。
        //   现在：中带（盘 + 舌根）固定在特征尺寸 h0 上；内带（孔 + 焊脚那一圈）逐档减半。
        //   ⚠ 这么做的合法性由**本循环自己**检验：判据不再变才算网格无关；
        //     并在收敛后**额外做一次「中带也加密」的确认**（见下面 confirm）。
        double hMid = h0;
        double h = h0;

        (double n2p, double n2pp, double n3, double m)? prev = null;
        List<MeshAdapt.Delta>? prevDeltas = null;
        double prevRemainK = double.NaN;   // R48 续：上一档外层耦合剩余误差，档间噪声 = 两档之和     // 上一对差值 —— 判「变化在不在缩小」要它
        for (int it = 0; it < maxRounds; it++)
        {
            cancel.ThrowIfCancellationRequested();
            // ★ 长任务必须说清楚「这一档有多大、大概要多久」。
            //   不说的话，人会把一个正常的几小时任务当死机 kill 掉（实测我自己就差点这么干）。
            //   单元数 ∝ 1/h²，每加密一档约 ×4；用**上一档的实测耗时**推下一档，
            //   比拍脑袋准 —— 而且推错了下一档会自己纠正。
            string eta = "";
            if (res.Trace.Count > 0)
            {
                var lastT = res.Trace[^1];
                // ★★ **按单元数线性外推是错的**（2026-08-29 实测推翻我自己的公式）。
                //   实测 0.6 档：单元数只涨约 6 倍，而**每轮外层耦合的成本涨了约 96 倍**
                //   （第 1 档 ~30 s/轮 → 第 3 档 ~48 分/轮）—— 细网格上内层线性解的
                //   迭代次数也在涨，条件数变差。⇒ 时间对单元数是**超线性**的。
                //
                //   ⇒ 有两档实测时就**用实测的耗时比**外推（它自带超线性）；
                //     只有一档时才退回「单元数 ×4」那个**下界**，并说明它偏乐观。
                //   ⚠ 这条以前看不见，因为档内是静默的 —— 是 2026-08-29 补进度提示后
                //     挣来的第一个发现。
                double grow;
                string how;
                if (res.Trace.Count >= 2 && res.Trace[^2].Sec > 1e-9)
                {
                    grow = lastT.Sec / res.Trace[^2].Sec;      // 实测的档间耗时比，自带超线性
                    how = $"按上两档实测耗时比 ×{grow:0.#}";
                }
                else
                {
                    grow = lastT.Fine > 0 ? (lastT.Fine / h) * (lastT.Fine / h) : 4.0;
                    how = $"按单元数 ×{grow:0.#}（**偏乐观** —— 实测耗时涨得比单元数快得多）";
                }
                // ⚠ ×{grow} 是**整体加密**的增长率，而分区之后只有内带在加密
                //   ⇒ 这个数是**上界**，不是估计。说成「预计」会让人以为它准，
                //     而实际往往快得多 —— 报一个偏高的数比报一个偏低的好，
                //     但**必须说清楚它是上界**，否则人会拿它当依据去排时间。
                eta = $"（{how}，上一档 {ThrottledProgress.Fmt(TimeSpan.FromSeconds(lastT.Sec))}"
                    + $" ⇒ 本档估 **~{ThrottledProgress.Fmt(TimeSpan.FromSeconds(lastT.Sec * grow))}**）";
            }
            progress?.Report($"加密复算：{h:0.000} mm（第 {it + 1} 档）{eta}…");
            // ★★ R48（2026-09-13，Opus 5）：**整档一起加密**，中带跟着 h 走，不再「中带钉死、只减内带」。
            //
            // 为什么改（实测，不是道理）：分区加密让网格在舌片上变成 44:1 的长条 ——
            //   内带半径 = 孔半径 + 2×焊脚 + 3，对现役两档算出来 31.3／30.8 mm，**比盘半径还大**
            //   ⇒ 整个 z 范围都落在内带里被铺成最细，而 x 方向在舌片上渐变到粗区 11 mm。
            //   电流沿 x 流，偏偏 x 分辨率最差 ⇒ x 节点一动焦耳热就动 6～7 %，
            //   实测逐片焦耳热 489／**458**／488 W（中带 1.0／0.5／0.25）——两头一致、中间掉下去，
            //   这不是收敛序列的样子，而「中带确认」恰恰拿减半那一档做对照 ⇒ 这道门永远判不过。
            // 整档加密反而**更省**：正方形格 0.5 mm 单片 13846 格，分区（中带 1.0／内带 0.25）要 28034 格。
            // ⚠ 当初分区是为了治「极细特征（焊脚 0.146 mm）把细格铺满大区域」那件事（见本文件开头）。
            //   那条顾虑仍在，靠 maxCells 与「判据不再变就停」兜住：实测 0.5 mm 上抽热误差已 0.4 W，
            //   窗口 3.3 W 容得下，根本不必细到特征尺寸。
            var lc = caseFactory(h, h);
            if (lc is null) throw new InvalidOperationException("加密复算：工厂没造出 LineCase");
            // ★ F7′ 审查 L-1（2026-09-23）：调用方直接给的初值（GivenFineRadiusPlan，上限未量）超过本算例的板料外缘 ⇒ 截到上限（R ≥ 上限网格逐节点相同），
            //   免得同一张全细网格判词取决于初值有没有超过上限。改回／不放大的计划不截（CapToPlate 只动自适应计划）。
            if (plan.Adaptive && !double.IsFinite(plan.CapMm))
            {
                var capped = MeshAdapt.CapToPlate(plan, MeshAdapt.PlateOuterRadiusMm(lc).Mm, "本次算例的材料包络（PlateOuterRadiusMm）");
                if (!ReferenceEquals(capped, plan)) { plan = capped; res.RadiusPlan = plan; }
            }
            // ★ F7′（2026-09-23）：细区半径**只由计划给**（工厂里写的是初值；放大之后以计划为准）。改回与初值相同时逐位不变。
            lc.MeshFineRadiusMm = plan.RadiusMm;
            lc.MeshFineRadiusPlan = plan;
            // ★ K 路（2026-09-15，Opus 5）：主循环按「管孔净流入／最热铂高出热偶读数／管根低于热偶读数」三列写成；本工况的复核名单（按分工况表）与之不同 ⇒ 拒答、不算，不许拿位置去对名字。
            if (RefuseForState(lc) is { } stateRefused)
            {
                res.Verdict = stateRefused; res.Converged = false; res.SecondsTotal = sw.Elapsed.TotalSeconds;
                progress?.Report("⚠ " + stateRefused);
                return res;
            }
            var swOne = System.Diagnostics.Stopwatch.StartNew();
            // ★ 内层**一直在报**（外层耦合 n/600、段 i/n），此前这里传 null 把它全扔了。
            //   限流转发：内层一秒可能报几十条，全转会把日志淡掉（淡掉 = 等于没报）。
            var inner = new ThrottledProgress(progress, 20, $"     · {h:0.000} mm ");
            var r = solve(lc, inner, cancel);
            swOne.Stop();
            if (!r.Ok)
            {
                res.Verdict = $"✗ 复核在 {h:0.000} mm 上解不出来：{r.Message}"
                            + "　⇒ **不能说这个设计过了** —— 算不出来不等于通过";
                res.SecondsTotal = sw.Elapsed.TotalSeconds;
                return res;
            }
            // ★★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-5）：解出来了但**没收敛或判不了**的档，数不可引用 —— 不许进档间差值，更不许打「网格无关」。
            //   判词如实说是哪一档、为什么；Converged 保持 false（界面据此不换判据表、不打已验戳）。门 R48J_SolverMeshAndMarkerGateTests。
            string unusable = TierUnusableWhy(r);
            if (unusable.Length > 0)
            {
                res.Verdict = $"✗ 复核在 {h:0.000} mm 上的整线解不可引用：{unusable}"
                            + "　⇒ **不能说网格无关，也不能说这个设计过了** —— 没收敛或判不了的数不参与比较";
                progress?.Report(res.Verdict);
                res.SecondsTotal = sw.Elapsed.TotalSeconds;
                return res;
            }

            double V(string k) => r.Checks
                .FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            // R48 B（2026-09-14 Opus 5）：复核的是**卡交付的**三条 —— 圆盘区最高温 − 管温／法兰增量温降换成热偶读数基准的热侧（⑦）／冷侧（⑧）
            //   （旧判法已是参考量，不复核）。Trace 的 N2pp／N3 两列从此装 ⑦／⑧（字段名是历史名，没改）；容差随之重定，见 TolTemplate。
            double a2p = V(LineResult.Key.NetFlux), a2pp = V(LineResult.Key.HotOverTc), a3 = V(LineResult.Key.ColdUnderTc);
            double mass = r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);

            // ★ 峰位落在粗区就会被静默算漏 —— 每档核对一次
            // ★★ R48 续（2026-09-14，Opus 5；物理把关人查出这道门「永远不会响」）：
            //   原来只看圆盘区峰位，而圆盘区现在按 r ≤ 盘半径圈 ⇒ 峰位**必然** ≤ 盘半径 < 细化半径，门形同虚设。
            //   现在取三种热点里最远的那个：圆盘区峰、舌片区峰（TabMaxRMm，两个内置档在 31–33，
            //   换个几何可以远在舌片上）、局部热稳定最不稳那一格（LocalStabRMm）。
            //   任一片三者全算不出 ⇒ 判不了（原来 Where(!NaN) 会把那一片静默丢掉 —— 与 ⑤⑥ 同病）。
            //   F7（2026-09-23）：这一段原样抽成 HotspotVerdict（逐字同式），让热点门拿生产这一份去判，不手抄。
            string? peakBad = HotspotVerdict(r, innerR, plan.RadiusMm);
            // ★★★★★ F7′（2026-09-23，决 29 自适应）：盖不住 ⇒ 按计划放大、从第一档重来；到上限仍盖不住 ⇒ 当场拒答（P8：不再空跑完阶梯）。
            //   峰位算不出（NaN）与计划不放大 ⇒ GrowFineRadius 原样返回，走下面的旧路（记下那句话、继续、最后判「不算数」）。
            {
                double peakR = HotspotRadiusMm(r);
                if (peakBad is null) res.RadiusTrace.Add((h, plan.RadiusMm, peakR, "盖住"));
                else
                {
                    var (capCase, _) = MeshAdapt.PlateOuterRadiusMm(lc);
                    var g = MeshAdapt.GrowFineRadius(plan, peakR, innerR, lc.MeshCoarseMm, capCase, $"加密复算第 {it + 1} 档（{h:0.000} mm）解后");
                    if (g.Refused)
                    {
                        plan = g.Plan; res.RadiusPlan = plan;
                        res.RadiusTrace.Add((h, lc.MeshFineRadiusMm, peakR, "到上限拒答"));
                        res.PeakOutsideFine = g.Verdict;
                        res.Line = r; res.FineMm = h; res.Cells = r.MeshCells; res.Converged = false;
                        res.Trace.Add((h, r.MeshCells, V0(r, LineResult.Key.NetFlux), V0(r, LineResult.Key.HotOverTc), V0(r, LineResult.Key.ColdUnderTc),
                                       r.Segments.Sum(x => x.MassG) + r.Flanges.Sum(f => f.MassG), swOne.Elapsed.TotalSeconds));
                        res.SecondsTotal = sw.Elapsed.TotalSeconds;
                        res.Verdict = "✗ **本次复核不算数** —— " + g.Verdict!.TrimStart('★', ' ')
                                    + "　（加密阶梯在这一档停下：再放大网格也不变，后面的档同样盖不住）　" + plan.Describe();
                        progress?.Report("   " + g.Verdict);
                        return res;
                    }
                    if (g.Grew)
                    {
                        res.RadiusTrace.Add((h, plan.RadiusMm, peakR, $"放大 → {g.Plan.RadiusMm:0.###} mm，从第一档重来"));
                        plan = g.Plan; res.RadiusPlan = plan;
                        progress?.Report($"   细区没盖住热点 ⇒ 放大：{plan.Steps[^1].Why}；已解的 {res.Trace.Count + 1} 档作废（旧半径上解的，不与新半径比），从 {h0:0.000} mm 重来。");
                        foreach (var t in res.Trace) res.DiscardedTiers.Add((t.Fine, t.Cells, lc.MeshFineRadiusMm, t.Sec));
                        res.DiscardedTiers.Add((h, r.MeshCells, lc.MeshFineRadiusMm, swOne.Elapsed.TotalSeconds));
                        res.Trace.Clear(); res.LastDeltas = new(); res.PeakOutsideFine = null;
                        prev = null; prevDeltas = null; prevRemainK = double.NaN;
                        h = h0; it = -1;
                        continue;
                    }
                    res.RadiusTrace.Add((h, plan.RadiusMm, peakR, double.IsNaN(peakR) ? "峰位算不出" : "计划不放大"));
                }
            }
            // ★ R48 续（2026-09-14，Opus 5）：**每档以这一档为准**，不许只设不清 ——
            //   原来早档峰在粗区、后档盖住了，那句话照样一直挂在结果上。
            res.PeakOutsideFine = peakBad;
            if (peakBad is not null) progress?.Report("   " + peakBad);

            res.Line = r; res.FineMm = h; res.Cells = r.MeshCells;
            res.Trace.Add((h, r.MeshCells, a2p, a2pp, a3, mass, swOne.Elapsed.TotalSeconds));
            // ★★ 判据值**当场就报**。此前这一行只报单元数与耗时，判据值要等整趟跑完
            //   才随表印出来 —— 于是 2026-08-29 那趟：阶梯三档全部算完、已跑 4 时 22 分，
            //   而**日志里一个判据数字都没有**，被 kill 掉就等于四小时全丢。
            //   「看得出还活着」只解决了一半；另一半是**中间结果要落地**。
            progress?.Report($"加密复算：{h:0.000} mm 完成 —— {r.MeshCells} 单元，用时 {ThrottledProgress.Fmt(swOne.Elapsed)}（累计 {ThrottledProgress.Fmt(sw.Elapsed)}）"
                + $"　管孔净流入 {a2p:0.000} W　最热铂高出热偶读数 {a2pp:0.000} K　管根低于热偶读数 {a3:0.000} K　合计 {mass:0} g"   // R48 B：进度行会上界面，写全名不写代号
                // ★ R48 续（2026-09-14，Opus 5）：每档都印外层耦合停在离不动点多远 —— 判据在两档间的变化
                //   若小于它，那次「在摆」分不清是网格还是耦合停机造成的（实测 ③ 变化 +0.820 K < 耦合容差 1.0 K）。
                + (double.IsNaN(r.CoupleRemainK) ? "" : $"　外层耦合剩余误差估计 {r.CoupleRemainK:0.00} K"
                    + (double.IsNaN(prevRemainK) ? "" : $"（与上一档合计 {r.CoupleRemainK + prevRemainK:0.00} K）")));
            // 档间变化是**两次独立停机之差**，噪声上界是两档剩余误差之和，不是只看这一档（2026-09-14 数值把关人查出）。
            double coupleNoiseK = double.IsNaN(r.CoupleRemainK) ? double.NaN
                                : r.CoupleRemainK + (double.IsNaN(prevRemainK) ? 0 : prevRemainK);
            prevRemainK = r.CoupleRemainK;

            if (prev is { } pv)
            {
                var tol = TolTemplate(lc);   // K 路（2026-09-15 Opus 5）：按工况取（上面已拒答名单对不上的工况）
                // R48（2026-09-13，Opus 5）：除了「变化多大」，还要记「离限值多远」——
                //   收敛要的是**结论稳**（再加密也翻不过限值），不是小数点后几位不动。见 MeshAdapt.Delta.MarginOverChange。
                double Lim(string key)
                {
                    var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
                    return c is null ? double.NaN : c.Limit;
                }
                double Margin(double value, string key, double change)
                {
                    double lim = Lim(key);
                    if (double.IsNaN(lim) || double.IsNaN(value) || Math.Abs(change) < 1e-12) return double.NaN;
                    return Math.Abs(value - lim) / Math.Abs(change);
                }
                // ★★★★★ R48 续（2026-09-14，Opus 5）：**振荡要认出来，不能拿最后一次变化当误差。**
                //
                //   实测三档 +0.841 → −1.693 → −1.350：Δ₁ = −2.534、Δ₂ = +0.343，**换号**。
                //   Δ 换号 = oscillatory convergence，其定义就是「不在渐近区」，此时
                //   「Δ 小 ⇒ 收敛」与 Richardson 一律失效，不确定度要用**振荡半幅** (max−min)/2 = ±1.267，
                //   而判词当时用的是 Δ₂ = 0.344 —— **差 3.7 倍**。
                //   ⇒ 这里把「历史三档的极差」与「相邻两次变化之比 r」一并算出来交给 Delta，
                //     由它决定该用哪个误差尺度、门槛该是几倍（见 MeshAdapt.Delta 的注释）。
                //   ⚠ 只有三档以上才谈得上振荡；两档时 Oscillating 一律为 false（没有 Δ₁ 可比）。
                (bool osc, double half, double ratio) Shape(Func<(double Fine, int Cells, double N2p,
                        double N2pp, double N3, double MassG, double Sec), double> pick, double now, double change)
                {
                    // ⚠⚠ res.Trace **已经包含当前这一档**（本方法上面几行就 Add 过了）。
                    //   第一版在这里又 hist.Add(now) 加了一遍 ⇒ hist[^2] 与 hist[^3] 变成
                    //   f_k 与 f_{k−1} ⇒ prevChange 恒等于 change ⇒ Oscillating 恒 false、RatioR 恒 1.0
                    //   ⇒ **整个特性是个 no-op**。（2026-09-14 常驻数值讨论人查出；这是同一天第三次栽在
                    //   「赋了值没人读」上。）不许再往 hist 里补当前档。
                    var hist = res.Trace.Select(pick).Where(v => !double.IsNaN(v)).ToList();
                    if (hist.Count < 3) return (false, double.NaN, double.NaN);
                    double prevChange = hist[^2] - hist[^3];
                    bool o = prevChange * change < 0 && Math.Abs(prevChange) > 1e-12;
                    // 半幅只取**最后三档**：最粗那档按构造就不在渐近区（起点是「特征画得出来」的下限），
                    // 而全档极差只增不减 —— 一个不随加密变小的量不是不确定度，是历史记录。
                    var last3 = hist.Skip(Math.Max(0, hist.Count - 3)).ToList();
                    double h = (last3.Max() - last3.Min()) * 0.5;
                    double r = Math.Abs(prevChange) > 1e-12 ? Math.Abs(change / prevChange) : double.NaN;
                    return (o, h, r);
                }
                var s2p  = Shape(t => t.N2p,  a2p,  a2p - pv.n2p);
                var s2pp = Shape(t => t.N2pp, a2pp, a2pp - pv.n2pp);
                var s3   = Shape(t => t.N3,   a3,   a3 - pv.n3);
                res.LastDeltas = new List<MeshAdapt.Delta>
                {
                    new() { Name = tol[0].Name, Change = a2p - pv.n2p,   Tol = tol[0].Tol,
                            MarginOverChange = Margin(a2p, LineResult.Key.NetFlux, a2p - pv.n2p),
                            Oscillating = s2p.osc, HalfRange = s2p.half, RatioR = s2p.ratio },
                    new() { Name = tol[1].Name, Change = a2pp - pv.n2pp, Tol = tol[1].Tol,
                            MarginOverChange = Margin(a2pp, LineResult.Key.HotOverTc, a2pp - pv.n2pp),
                            Oscillating = s2pp.osc, HalfRange = s2pp.half, RatioR = s2pp.ratio },
                    new() { Name = tol[2].Name, Change = a3 - pv.n3,     Tol = tol[2].Tol,
                            MarginOverChange = Margin(a3, LineResult.Key.ColdUnderTc, a3 - pv.n3),
                            Oscillating = s3.osc, HalfRange = s3.half, RatioR = s3.ratio },
                };
                // 差值也当场报 —— 「收没收敛」是读的人最想先知道的那一条
                progress?.Report("   较上一档：" + string.Join("　", res.LastDeltas.Select(
                    x => $"{x.Name} {x.Change:+0.000;-0.000}/{x.Tol:0.###}"
                       // 2026-09-14 Opus 5（复审）：热侧也要查 —— 两条都是「温度 − 常数基准」，耦合停机噪声直接进值（原来只查冷侧那一行）
                       + ((x.Name == tol[1].Name || x.Name == tol[2].Name) && !double.IsNaN(coupleNoiseK) && Math.Abs(x.Change) <= coupleNoiseK
                            ? $"（⚠ 变化 {Math.Abs(x.Change):0.000} K ≤ 这两档外层耦合剩余误差之和 {coupleNoiseK:0.00} K ⇒ **与耦合停机噪声分不开**）" : "")
                       + (x.NotAsymptotic ? $"（**不在渐近区**：{x.WhyNotAsymptotic}；末三档极差半幅**至少** ±{x.HalfRange:0.###}"
                                          + " —— 误差按它算，不按这一次的变化）" : "")
                       + (x.Within && !x.Oscillating ? "" : x.ConclusionStable
                            ? $"（数在动，但离限值还有 {x.MarginOverChange:0.#} 倍这个动幅 ⇒ 结论翻不过来）"
                            : "（**还没算准**）"))));
                // ★★ 判据要**上一对**差值（趋势），只有一对时一律不算收敛 ——
                //   2026-08-30 实测：一对差值小可能纯属两级跨在拐点两侧（见 MeshAdapt.Converged）。
                if (MeshAdapt.Converged(res.LastDeltas, prevDeltas)) { res.Converged = true; break; }
                prevDeltas = res.LastDeltas;
            }
            prev = (a2p, a2pp, a3, mass);
            if (r.MeshCells > maxCells) { res.HitCellCap = true; break; }
            h = MeshAdapt.Refine(h);
        }

        res.SecondsTotal = sw.Elapsed.TotalSeconds;
        // ★★★★★ R48 续（2026-09-14，Opus 5）：**加密阶梯必须有一个有名字的失败出口。**
        //
        //   「不在渐近区」做成一票否决之后，出现了一个没有出口的状态：
        //   序列在摆 ⇒ 永远判不了收敛，而振荡**不会因为再加密就消失**（实测那组三条里两条在摆）。
        //   此前 Verdict 的「不在渐近区」那一支**完全没有用 hitCap**，于是这两件事印出来是同一句话：
        //     「还在爬，再加一档就好」  与  「已经 61154 单元、单档 68 分钟、到顶了」
        //   ⇒ 加一个第三态：**判不了**（到上限且仍不在渐近区）。
        //   这与本项目「判据只能过／不过／**无法判定**」那条铁律是同一条规矩，只是搬到了网格这一层。
        // ★★ R48 续（2026-09-14，Opus 5；物理把关人列为「重解前必做」）：**峰在粗区 ⇒ 不许判收敛。**
        //   PeakVerdict 原来只打印、只挂在结果上：既不影响 Converged，界面也一处不读 ——
        //   于是「这次的圆盘区最高温不算数」这句话，工程师看不到，放行逻辑也不管。赋了值没人读。
        //   现在最后一档峰若仍在粗区，Converged 置假（界面据此不换 _last），并把原话接进判词。
        if (res.PeakOutsideFine is not null) res.Converged = false;
        res.Undecidable = res.HitCellCap && !res.Converged
                       && res.LastDeltas.Any(d => d.NotAsymptotic);
        // ★ 峰在粗区时判词**必须以 ✗ 开头**（2026-09-14 数值把关人查出）：原来把原话接在判词后面，
        //   而三条变化都落进容差时判词第一句是「✓ 数已经不再变了」—— 读的人只看第一句。
        string deltaVerdict = MeshAdapt.Verdict(res.FineMm, res.LastDeltas, res.HitCellCap);
        res.Verdict = res.PeakOutsideFine is null
            ? deltaVerdict
            : "✗ **本次复核不算数** —— " + res.PeakOutsideFine.TrimStart('★', ' ')
              + "　（网格变化本身：" + deltaVerdict + "）";

        // ★★ **中带确认**的历史与理由（留着，别再写第二遍）：
        //   它当初（A⑭）要验的是「只加密内带够不够 —— 判据会不会收敛到一个由**中带粗糙度**决定的错值上」，
        //   因为「内带加密判据不动」这个证据看不出那件事。那时它是整趟里最贵的单步
        //   （2026-08-29 实测：内带 0.146 mm 那档 39618 单元跑了 3 时 43 分，中带减半还要再加一批单元）。
        //
        // ★★ R48（2026-09-13，Opus 5）：主循环已改成**整档一起加密**（中带与内带同尺寸，见上面 caseFactory 那处），
        //   中带不再固定 ⇒ 主循环的停止条件「判据变化落进容差」本身就是「整张网格都不再影响判据」的证据，
        //   再单独跑一次「中带减半」等于把下一档重算一遍，白花几十分钟。
        //   ⚠ 若哪天主循环改回「中带钉死、只减内带」，这一步必须一并恢复 —— 否则那条安全线就没了。
        if (res.Line is { Ok: true } && res.Trace.Count > 0)
        {
            res.MidBandConfirm = "· 中带确认：R48 起主循环**整档一起加密**（中带与内带同尺寸），"
                + "「判据不再变」本身已经覆盖了中带粗糙度这一条，故不再单独解一遍场。";
            progress?.Report("   " + res.MidBandConfirm);
        }

        res.SecondsTotal = sw.Elapsed.TotalSeconds;
        return res;
    }
}
