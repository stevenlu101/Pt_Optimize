using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// 搜形状驱动（Core，无界面依赖）的选项。
///
/// 缺省值出处：除 <see cref="MaxDiscMm"/>、<see cref="DiscGridMm"/> 两项与 2026-09-24 按夜跑数据改的几项（<see cref="WFrac"/>、<see cref="ScreenRounds"/>、
/// <see cref="ParallelFirstPass"/>、<see cref="ScreenSkipRadiusGrowth"/>、<see cref="ScreenReuseSameState"/>、<see cref="ShoulderJPrescreen"/>，各自注释写变因与改回）外，
/// 全部**照抄界面 <c>UI/LineDesignPage.cs</c> 的 <c>SearchOneFamilyAsync</c>（07644dc）里的值**，判定与阈值一个不挪：
/// 二分 ≤ 3 次、区间 ≤ 1 mm 停；黄金分割 ≤ 4 点、区间 ≤ 1 mm 停、往可行侧留 +6 mm 余地、已算过 ±0.5 mm 去重；
/// 不动点 ≤ 3 次、贴下界 0.05 mm 算到了；精算 40 轮；邻域最多 6 轮；
/// 改善门槛 0.5 g 与步长下界 0.625 mm 由 <see cref="ShapeSearchPlan"/> 给（同一份实现，本类不另写）。
/// </summary>
public sealed class ShapeSearchOptions
{
    /// <summary>
    /// 盘半径的**上端** mm（外推到这里为止；缺省起点表也铺到这里）。调用方必须给，NaN ⇒ 抛。
    /// 出处由 <see cref="MaxDiscSource"/> 说明，并必须印进证据头。
    /// </summary>
    public double MaxDiscMm = double.NaN;

    /// <summary>上端的出处（原样印进证据头与无解报告）。缺省「探针给的表，无出处」。</summary>
    public string MaxDiscSource = "探针给的表，无出处";

    /// <summary>
    /// 起点表 mm（盘半径）。null ⇒ 从闭式下界起每 <see cref="ShapeSearchPlan.DiscStepMm"/> 一点铺到 <see cref="MaxDiscMm"/>，
    /// 再过 <see cref="ShapeSearchPlan.LiveDiscs"/>。界面原来写死 {25, 30, 35}；本驱动不写死（改动 (a)）。
    /// 调用方给了表 ⇒ 照用（仍过 LiveDiscs），外推从表最大值往 <see cref="MaxDiscMm"/> 走。
    /// </summary>
    public double[]? DiscGridMm;

    /// <summary>
    /// 舌宽比例（半宽 / 盘半径）。①～④ 用最宽的那个（切线族 w = R），⑤ 再按表里的顺序试其余的。
    /// **选定（2026-09-24）**：{1.00, 0.875, 0.75}，先切线族。0.875 = 1 − <see cref="ShapeSearchPlan.FracStep"/>（与邻域的舌宽步长同一格）。
    /// 依据：决 102 探针（deliverable/R48_决102_形状杠杆探针_W08_盘径_本次开跑于2026-09-23_203224.txt）「盘+5」例（R 35、w 30）
    ///   舌盘交界截面 J 11.670 &gt; 限 11；夜跑 021617 ⑤ 舌宽 55.5（盘 Ø74、比例 0.75）三条硬判据比同盘径切线族全劣化
    ///   （最热铂高出热偶读数 36.4 对 16.1 K、管根低于热偶读数 9.0 对 4.7 K、管孔净流入 −10.1 对 −1.9 W）。
    /// 变因：旧值 {0.75, 1.00}（照抄界面 SearchWFrac）→ 新值 {1.00, 0.875, 0.75}；两份证据如上。改回 = 传 {0.75, 1.00}。
    /// </summary>
    public double[] WFrac = { 1.00, 0.875, 0.75 };

    /// <summary>
    /// 粗筛每点的轮数上限。**选定：8**（2026-09-24）。依据：夜跑 deliverable/R48_搜形状_Core驱动_W08_本次开跑于2026-09-24_001304.txt
    /// （粗筛 16 轮：① 盘 Ø94 每轮实测 2 h 10 min～2 h 55，12 h 闸切在第 6 轮，没走出 ①）与 …_021617.txt（粗筛 2 轮：每形状 49～54 次场解 = 2.1～2.4 h）；
    /// 界面的 16 轮在 4 核 Linux 上一形状 30 h 以上，不可用。跑过再校。
    /// 变因：旧值 16（照抄界面 SearchScreenRounds）→ 新值 8；两份证据如上。改回 = 传 16。
    /// 全过即停：求解器本身在第 k 轮三条逐片判据与其余硬判据全过时就停（Solver.Solve 的 Rounds 里「第 k 轮全过」那一支 break），驱动不另加。
    /// </summary>
    public int ScreenRounds = 8;
    /// <summary>赢家精算的轮数上限。照抄界面 SearchFinalRounds。</summary>
    public int FinalRounds = 40;
    /// <summary>邻域爬山最多几轮。照抄界面 SearchMaxExtend。</summary>
    public int MaxExtend = 6;

    /// <summary>不动点（闭式下界再解）最多几次。照抄界面 `for (int fix = 0; fix &lt; 3; fix++)`。</summary>
    public int FixpointMax = 3;
    /// <summary>不动点「已贴着下界」的容差 mm。照抄界面 `Rnext &gt;= Rbest - 0.05`。</summary>
    public double FixpointTolMm = 0.05;
    /// <summary>二分最多几次。照抄界面 `bi &lt; 3`。</summary>
    public int BisectMax = 3;
    /// <summary>二分区间停止宽度 mm。照抄界面 `bHi - bLo &gt; 1.0`。</summary>
    public double BisectStopMm = 1.0;
    /// <summary>黄金分割最多几点。照抄界面 `gi &lt; 4`。</summary>
    public int GoldenMax = 4;
    /// <summary>黄金分割区间停止宽度 mm。照抄界面 `gHi - gLo &gt; 1.0`。</summary>
    public double GoldenStopMm = 1.0;
    /// <summary>黄金分割往可行侧留的余地 mm。照抄界面 `Math.Min(bHi + 6.0, Rsafe)`。</summary>
    public double GoldenSlackMm = 6.0;
    /// <summary>黄金分割「已算过」的去重距离 mm，也是去重后下端前挪的量。照抄界面 `&lt; 0.5` 与 `gLo += 0.5`。</summary>
    public double GoldenDedupMm = 0.5;

    /// <summary>解法族：false = 不挖舌孔、true = 挖舌孔（<see cref="SolverOptions.AllowTabCuts"/>）。本驱动一次只跑一族。</summary>
    public bool AllowTabCuts;

    /// <summary>粗筛平坦区网格 mm（<see cref="SolverOptions.ScreenCoarseMm"/>）。照抄界面 SearchScreenCoarseMm = 0（关）。</summary>
    public double ScreenCoarseMm;

    /// <summary>批内（其余舌宽比、邻域）并发路数。界面写死 4；调用方按机器给。</summary>
    public int Lanes = 4;

    /// <summary>先把种子自己的形状算一遍作基准（照抄界面「先算你现在这个形状，作基准」）。</summary>
    public bool EvalSeedFirst = true;

    /// <summary>
    /// ① 并行首遍（2026-09-24，算力工程 D）：起点表全部点（舌宽取最宽比例）用 <see cref="ShapeBatchEval"/> 并行解一遍（<see cref="Lanes"/> 路）；
    /// 有可行点 ⇒ 最小可行点与左邻不可行点间二分、黄金分割照旧；全不可行 ⇒ 向上外推（并行一批到上端）；全可行（或最小可行点就是表首点）⇒ 从下界做 ②。
    /// 这是对界面算法的变更（界面：只在表最大值上串行解一次）。变因：4 核并行、串行首遍是瓶颈（夜跑 001304 在 ① 一个形状上用满 12 h）。
    /// 判定与阈值不动。改回 = false（界面原算法：表最大值解一次、不可行时逐步外推）。
    /// </summary>
    public bool ParallelFirstPass = true;

    /// <summary>
    /// 粗筛评估传 <see cref="SolverOptions.SkipRadiusGrowthAfterFinalCheck"/> = true（2026-09-24，算力工程 B）：终局复核后细区没盖住热点时不放大、不重做，
    /// 本该放大到的半径印进逐形状行。变因：夜跑 021617 第 3 形状（盘 Ø74／舌 55.5）粗筛 2 轮跑完后整个形状从第 1 轮重解一遍，21853 s（前两形状 7595、8613 s）。
    /// 赢家精算不传（照旧放大重做）。改回 = false。
    /// </summary>
    public bool ScreenSkipRadiusGrowth = true;

    /// <summary>粗筛评估传 <see cref="SolverOptions.ReuseSameStateSolves"/> = true（2026-09-24，算力工程 A：同状态整线解复用，结果逐位不变、只少解场）。改回 = false。</summary>
    public bool ScreenReuseSameState = true;

    /// <summary>赢家精算是否传 <see cref="SolverOptions.ReuseSameStateSolves"/>。缺省 false：精算照改前逐位（复用的逐位门只在粗筛网格上跑过）。</summary>
    public bool FinalReuseSameState;

    /// <summary>
    /// 闭式肩部预筛（2026-09-24，全局解决方案第 2 版 §4 L0）：舌宽 w &lt; 盘半径 R 的候选，先按 I/(t·2w) 估盘舌交界截面 J，
    /// 超过计算极限（<see cref="DesignSpec.JCheckAPerMm2"/> = 设定 J + 1）⇒ 不进场解，印「闭式跳过：肩部 J」。
    /// I 取参照形状（同盘径的切线族那一行，邻域取当前最优行）求解后的设计电流（<see cref="SolverResult.DesignCurrent"/>），t 取参照形状求解后的板厚；
    /// **是估计、只作预筛，不是判据**：求解器每轮按 J 抬板厚（ApplySectionFloor），被跳过的形状加厚后可能过 J，本预筛不覆盖那种形状。改回 = false。
    /// </summary>
    public bool ShoulderJPrescreen = true;

    /// <summary>把求解器每轮「第 N 轮…」那一行带上形状标签转给 progress（整夜挂机看得出还活着）。</summary>
    public bool ForwardSolverRounds = true;

    /// <summary>
    /// **只供门用**：替换单点求解（生产不传 = <see cref="Solver.Solve"/>）。
    /// 快门用它注入假的求解，验外推、无解报告等控制流分支，不跑场。
    /// </summary>
    internal Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult>? SolveOverride;
}

/// <summary>一条硬判据在某次解上的值（从 <see cref="LineResult.Checks"/> 取，本类不自己算判据）。</summary>
public readonly record struct HardCritValue(string Name, bool Present, double Actual, double Limit, bool LessIsBetter,
                                            bool Ok, bool Undetermined, string Where)
{
    /// <summary>
    /// 裕度（判据的单位，正 = 有余量）= 限值 − 实际（须 ≤ 限值的判据）或 实际 − 限值（须 ≥ 限值的判据）。
    /// 只是 <see cref="ConstraintOut.Actual"/>、<see cref="ConstraintOut.Limit"/>、<see cref="ConstraintOut.LessIsBetter"/> 三个字段相减，
    /// 与 <see cref="ConstraintOut.MarginPct"/> 同一个方向约定（限值为 0 的「管孔净流入」没有百分比，所以印绝对值）。
    /// </summary>
    public double Margin => !Present ? double.NaN : (LessIsBetter ? Limit - Actual : Actual - Limit);

    public static HardCritValue Of(LineResult? r, string key)
    {
        var c = r?.Find(key);
        string name = Criteria.Plain(key);
        if (c is null) return new HardCritValue(name, false, double.NaN, double.NaN, true, false, false, "");
        return new HardCritValue(name, true, c.Actual, c.Limit, c.LessIsBetter, c.Ok, c.Undetermined, c.Where ?? "");
    }

    public string Text()
    {
        if (!Present) return "未解出";
        string u = Undetermined ? "判不了" : (Ok ? "过" : "不过");
        return string.Create(CultureInfo.InvariantCulture,
            $"{Fx(Actual)}/{(LessIsBetter ? "≤" : "≥")}{Fx(Limit)}/裕度{Fx(Margin)}/{u}");
    }

    internal static string Fx(double v) => double.IsNaN(v) ? "NaN" : v.ToString("0.000", CultureInfo.InvariantCulture);
}

/// <summary>搜形状里的一个形状（一行）。</summary>
public sealed class ShapeRow
{
    public int Index;
    /// <summary>这一行出自哪一步：基准、①、①外推、②、③、④、⑤舌宽、⑥邻域第 n 轮。</summary>
    public string Phase = "";
    public double R, HalfW;
    public bool Taper;
    /// <summary>闭式早筛跳过（盘半径低于「圆盘盖得住管孔＋焊脚」的真下界），没有求解。</summary>
    public bool Skipped;
    public string SkipWhy = "";
    public bool Ok;
    public double MassG = double.NaN;
    public double TabLengthMm = double.NaN;
    public string Message = "", StopWhy = "";
    public bool HitBound, Undetermined;
    public HardCritValue Hot, Cold, Flux;
    /// <summary>没过或判不了的硬安全线全名（<see cref="LineResult.HardBlocked"/>）。</summary>
    public string[] Blocked = Array.Empty<string>();
    /// <summary>场解次数（<see cref="SolverResult.Solves"/>）。</summary>
    public int Solves;
    /// <summary>同状态复用次数（<see cref="SolverResult.SameStateReuses"/>，省下的场解）。</summary>
    public int Reuses;
    /// <summary>粗筛跳过的细区半径放大：本该放大到的半径 mm（<see cref="SolverResult.SkippedRadiusGrowthToMm"/>）；NaN = 没发生。</summary>
    public double SkippedGrowthToMm = double.NaN;
    /// <summary>这一行是闭式肩部预筛跳过的（<see cref="ShapeSearchOptions.ShoulderJPrescreen"/>）。</summary>
    public bool Prescreened;
    /// <summary>进度里以「第」开头的行数（与界面推进度条同一数法）。</summary>
    public int Rounds;
    public double Seconds;
    public DesignSpec? Design;
    public SolverResult? Result;

    public string Tag => $"盘Ø{2 * R:0.0}／舌宽{2 * HalfW:0.0}{(Taper ? "／锥形" : "")}";

    public const string Header =
        "序\t阶段\t盘径Ø\t舌宽\t舌边\t舌长\t结果\t铂重g\t法兰最热处高出管接触处温度 K（实际/限/裕度/判）\t管接触处流入法兰的净热流 W\t局部热稳定 ×\t卡住的硬安全线\t场解\t轮\t秒\t停因／消息\t同状态复用\t粗筛本该放大到 mm";

    public string Line()
    {
        var ci = CultureInfo.InvariantCulture;
        if (Skipped)
            return string.Create(ci, $"{Index}\t{Phase}\t{2 * R:0.00}\t{2 * HalfW:0.00}\t{(Taper ? "锥形" : "平行")}\t—\t跳过\t—\t—\t—\t—\t—\t0\t0\t0\t{SkipWhy}\t0\t—");
        string verdict = Ok ? "✓可行" : Undetermined ? "判不了" : HitBound ? "到顶" : "不可行";
        string why = (Ok ? Message : (string.IsNullOrWhiteSpace(StopWhy) ? Message : StopWhy)).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        string grown = double.IsNaN(SkippedGrowthToMm) ? "—" : SkippedGrowthToMm.ToString("0.00", ci) + "（没放大、没重做；热点处温度类判据不算数，只作排序）";
        return string.Create(ci,
            $"{Index}\t{Phase}\t{2 * R:0.00}\t{2 * HalfW:0.00}\t{(Taper ? "锥形" : "平行")}\t{TabLengthMm:0.0}\t{verdict}\t"
          + $"{(double.IsNaN(MassG) ? "—" : MassG.ToString("0.0", ci))}\t{Hot.Text()}\t{Cold.Text()}\t{Flux.Text()}\t"
          + $"{(Blocked.Length == 0 ? "—" : string.Join("；", Blocked))}\t{Solves}\t{Rounds}\t{Seconds:0}\t{why}\t{Reuses}\t{grown}");
    }
}

/// <summary>搜形状的结果。</summary>
public sealed class ShapeSearchResult
{
    public readonly List<ShapeRow> Rows = new();
    public ShapeRow? Winner;
    /// <summary>赢家精算（判决网格第二遍）的结果；没有可行形状时为 null。</summary>
    public SolverResult? Final;
    public bool FinalWriteBack;
    public string FinalHeadline = "";
    /// <summary>方案卡（有赢家并精算过时）或空。</summary>
    public string SchemeCard = "";
    /// <summary>「无可行形状」报告（没有任何可行形状时）或空。</summary>
    public string NoFeasibleReport = "";
    public bool NoFeasible;
    /// <summary>盒子：盘半径闭式下界、起点表、上端与出处。</summary>
    public double MinDiscMm, MaxDiscMm;
    public string MaxDiscSource = "";
    public double[] DiscGrid = Array.Empty<double>();
    /// <summary>① 用的起点（表最大值，或外推后第一个可行点）。</summary>
    public double StartDiscMm = double.NaN;
    /// <summary>外推是否发生、到了哪里。</summary>
    public List<double> ExtrapolatedMm = new();
    /// <summary>
    /// ⑥ 邻域爬山的停因（原样印进方案卡）。只有「步长已收到分辨率下界仍无改善」才可以说「在 ±MinDiscStepMm 分辨率上是局部最优」；
    /// 轮数用完（MaxExtend）停下的不是（2026-09-23 对照核实补：原稿方案卡无条件写「局部最优只在 ±0.625 mm 分辨率上成立」）。
    /// </summary>
    public string ClimbStop = "";
    public bool ClimbConverged;
    /// <summary>① 并行首遍走了哪一支（有可行点二分／全可行从下界做 ②／全不可行…）；界面原算法（改回）时为空。</summary>
    public string FirstPassBranch = "";
    /// <summary>③ 二分的起始区间 [不可行, 可行]（盘半径 mm），取自并行首遍；没走二分那一支时为 NaN。</summary>
    public double BisectLoMm = double.NaN, BisectHiMm = double.NaN;
    public readonly List<string> Log = new();
}

/// <summary>
/// ★ 搜形状驱动：把界面 <c>UI/LineDesignPage.cs</c> 的 <c>SearchOneFamilyAsync</c>（07644dc，约 3177～3650 行）
/// ①～⑥ 逐段照搬进 Core，判定与阈值不动；界面、命令行、测试将来都可以调它（界面改调是 Windows 待办）。
///
/// 照搬的步骤（与界面同序）：
///   基准：先把种子自己的形状算一遍（不可行只是表上多一行，不作出发点）。
///   ① 在起点表最大盘径（舌宽取最宽比例，即切线族 hw = R）上解一次，拿到板厚。
///      ★ 2026-09-24 改（<see cref="ShapeSearchOptions.ParallelFirstPass"/>，缺省开）：起点表**全部点**并行解一遍，见下「并行首遍」。
///   ② 不动点：由判据「圆盘盖得住管孔＋焊脚」的闭式反解 <see cref="GeometryScreen.MinDiscRadiusMm(IReadOnlyList{FlangePlate})"/> 定最紧下界，在下界上再解。
///   ③ 下界不可行 ⇒ 在 [不可行, 可行] 上二分（≤ 3 次，区间 ≤ 1 mm 停）。
///   ④ 黄金分割在可行区间里按质量找最轻（≤ 4 点）。
///   ⑤ 在最优盘径上试其余舌宽比例（批内并发）。
///   ⑥ 邻域爬山：<see cref="ShapeSearchPlan.Neighbours(double,double,bool,double)"/>、<see cref="ShapeSearchPlan.Worth(IEnumerable{ValueTuple{double,double,bool}}, ISet{string})"/>、
///      <see cref="ShapeSearchPlan.Improved"/>、步长 <see cref="ShapeSearchPlan.Refine"/> 减半到 <see cref="ShapeSearchPlan.MinDiscStepMm"/>，最多 MaxExtend 轮。
///   赢家精算：<see cref="MeshVerify.RequiredMeshFor"/> 给细网格与细区半径初值，<see cref="Solver.Solve"/> 跑 FinalRounds 轮，
///      <see cref="ShapeSearchPlan.RefineVerdict"/> 定能不能叫「最轻的全过形状」。
///
/// 两处必改（业主 2026-09-23 指出的病灶）：
///   (a) 起点表不再写死 {25, 30, 35}：缺省由调用方给上端 MaxDiscMm，表 = LiveDiscs(闭式下界起每 DiscStepMm 一点到 MaxDiscMm)。
///       变因：写死的表把上端封在 35，大盘与切线族宽舌（探针里唯一的正面信号 R = w = 45）根本到不了。
///   (b) ① 的起点不可行时向上外推：从表最大值起每次 + DiscStepMm 再解，直到可行或到 MaxDiscMm。
///       变因：界面默认表最大值可行（二分区间的可行上端没有核对，UI/LineDesignPage.cs 的 SearchOneFamilyAsync 二分那段），上端不可行时二分是在一个假区间上做的。
///       外推到头仍无可行点 ⇒ 不二分（没有可行上端）、不爬山（没有可行出发点）；② 不动点仍照做（它只用板厚，不依赖上端可行），
///       最后仍无可行 ⇒ 出「无可行形状」报告：每个形状的停因、三条硬判据值与裕度、铂重、卡住的判据、盒子的范围与上端出处。
///
/// 另一处与界面不同（写明，不静默）：早筛的管孔半径取 <see cref="DesignSpec.HoleRadiusMm"/>（= 壁厚 + 内径/2），
///   界面写的是「壁厚 + 25」。变因：业主「通用型解法」原则（换内径也要出结果）；内径 50 时两者逐位相同（现役 W08、W06 都是 50）。
///
/// 2026-09-24 按夜跑数据改的几处（实施记录「09-24 按夜跑数据改的六件事」；每处都有改回参数，判定与阈值不动）：
///   并行首遍（D，对界面算法的变更，变因：4 核并行、串行首遍是瓶颈）：起点表全部点（舌宽取最宽比例）用 <see cref="ShapeBatchEval"/> 并行解一遍，
///     按盘径从大到小提交（大盘解得慢，先占道，整批墙钟最短）。然后：
///     · 有可行点、且最小可行点 Rhi 左边还有表点 ⇒ 取左邻表点 Rlo（不可行，因为 Rhi 是最小可行点）⇒ 在 [Rlo, Rhi] 上 ③ 二分、④ 黄金分割照旧（不再做 ②：区间已由首遍给出）；
///     · 全可行，或最小可行点就是表首点（没有左邻）⇒ 从下界做 ②，板厚取最小可行点那一行的；
///     · 全不可行 ⇒ 向上外推：表最大值之上每 DiscStepMm 一点到上端，并行一批；外推里有可行点 ⇒ 同第一支（左邻是外推批或表最大值）；
///       仍全不可行 ⇒ 不二分、不黄金分割，② 取表最大值那一行的板厚（与界面「② 只用板厚」同一理由）。
///     黄金分割上沿 min(bHi + 余地, Rsafe) 里的 Rsafe = 首遍（含外推）里已核实可行的最大盘径（界面里它是表最大值，那里默认可行）。
///   粗筛不放大重做（B）、同状态复用（A）：只改粗筛传给求解器的选项；赢家精算照旧。
///   舌宽比表 {1.00, 0.875, 0.75} 与闭式肩部预筛（E）、粗筛 8 轮（C）：见 <see cref="ShapeSearchOptions"/> 各项注释。
/// </summary>
public static class ShapeSearchDriver
{
    public static ShapeSearchResult Run(DesignSpec seed, DesignInputs baseIn, ShapeSearchOptions opt,
                                        IProgress<string>? progress, CancellationToken ct)
    {
        if (seed is null) throw new ArgumentNullException(nameof(seed));
        if (baseIn is null) throw new ArgumentNullException(nameof(baseIn));
        if (opt is null) throw new ArgumentNullException(nameof(opt));
        if (double.IsNaN(opt.MaxDiscMm) || opt.MaxDiscMm <= 0)
            throw new ArgumentException("盘半径上端 MaxDiscMm 必须由调用方给（本驱动不写死上端），并写明出处 MaxDiscSource。", nameof(opt));
        if (opt.WFrac is null || opt.WFrac.Length == 0) throw new ArgumentException("舌宽比例表不能为空", nameof(opt));
        if (opt.Lanes < 1) throw new ArgumentException("Lanes 至少 1", nameof(opt));

        var solve = opt.SolveOverride ?? ((d, b, o, p, t) => Solver.Solve(d, b, o, p, t));
        var res = new ShapeSearchResult { MaxDiscMm = opt.MaxDiscMm, MaxDiscSource = opt.MaxDiscSource };
        var ci = CultureInfo.InvariantCulture;
        void Say(string s) { lock (res.Log) res.Log.Add(s); progress?.Report(s); }

        double step0 = ShapeSearchPlan.DiscStepMm;
        double wall = seed.WallMm;
        double minDiscAll = GeometryScreen.MinDiscRadiusMm(
            holeRadiusMm: seed.HoleRadiusMm, thickMm: baseIn.WeldMinThicknessMm, wallMm: wall);
        res.MinDiscMm = minDiscAll;

        // (a) 起点表：不写死。缺省从闭式下界（按 LiveDiscs 的 0.5 mm 取整规则）起每 DiscStepMm 一点铺到上端。
        double[] grid;
        if (opt.DiscGridMm is { Length: > 0 } g) grid = g;
        else
        {
            double lo = ShapeSearchPlan.LiveDiscs(new[] { double.NegativeInfinity }, minDiscAll)[0];
            var pts = new List<double>();
            for (int k = 0; lo + k * step0 <= opt.MaxDiscMm + 1e-9; k++) pts.Add(lo + k * step0);
            if (pts.Count == 0) pts.Add(lo);
            grid = pts.ToArray();
        }
        double[] discs = ShapeSearchPlan.LiveDiscs(grid, minDiscAll);
        res.DiscGrid = discs;
        double[] wFrac = opt.WFrac;
        bool taperPage = seed.TabTaper;   // 界面：①～⑤ 按页面当前勾选；这里 = 种子的锥形
        string fam = opt.AllowTabCuts ? "挖舌孔" : "不挖舌孔";

        Say($"盒子：盘半径下界 {minDiscAll.ToString("0.000", ci)} mm（判据「圆盘盖得住管孔＋焊脚」闭式，管孔半径 {seed.HoleRadiusMm.ToString("0.000", ci)}、焊脚下界 max(烧穿 {baseIn.WeldMinThicknessMm.ToString("0.00", ci)}, 壁厚 {wall.ToString("0.00", ci)})）；"
          + $"盘半径上端 {opt.MaxDiscMm.ToString("0.0", ci)} mm（出处：{opt.MaxDiscSource}）；起点表（盘半径）{{{string.Join(", ", discs.Select(x => x.ToString("0.0##", ci)))}}} mm"
          + (opt.DiscGridMm is null ? "（缺省：下界起每 " + step0.ToString("0.###", ci) + " mm 一点到上端）" : "（调用方给的表）"));
        Say($"解法族：{fam}（本驱动一次只跑一族）；舌宽比例 {{{string.Join(", ", wFrac.Select(x => x.ToString("0.###", ci)))}}}；粗筛 {opt.ScreenRounds} 轮、精算 {opt.FinalRounds} 轮；"
          + $"邻域最多 {opt.MaxExtend} 轮、步长 {step0.ToString("0.###", ci)} 减半到 {ShapeSearchPlan.MinDiscStepMm.ToString("0.###", ci)} mm；改善门槛 0.5 g（ShapeSearchPlan.Improved）；并发 {opt.Lanes} 路；粗筛平坦区网格 {opt.ScreenCoarseMm.ToString("0.###", ci)} mm（0 = 关）");
        Say("算力选项（2026-09-24，判定与阈值不动）：① " + (opt.ParallelFirstPass ? "并行首遍（起点表全部点并行解一遍）" : "界面原算法（只在表最大值上解一次，不可行再逐步外推）")
          + "；粗筛终局复核后" + (opt.ScreenSkipRadiusGrowth ? "不放大重做（SkipRadiusGrowthAfterFinalCheck）" : "照旧放大重做")
          + "；粗筛同状态复用 " + (opt.ScreenReuseSameState ? "开" : "关")
          + "；赢家精算照旧放大重做、同状态复用 " + (opt.FinalReuseSameState ? "开" : "关")
          + "；闭式肩部预筛 " + (opt.ShoulderJPrescreen ? string.Create(ci, $"开（w < R 的候选按 I/(t·2w) 估盘舌交界截面 J，> {seed.JCheckAPerMm2:0.#} 不进场解；估计、只作预筛）") : "关"));
        Say(ShapeRow.Header);

        var solved = new List<ShapeRow>();       // 界面的 rows：只收真解出来的行（跳过的不收）
        int idx = 0;
        object rowLock = new();

        DesignSpec Spec(double R, double hw, bool taper)
        {
            var s = seed.Clone();
            s.DiscRadiusMm = R;
            s.TabHalfWidthMm = hw;
            s.TabTaper = taper;
            s.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + s.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            return s;
        }

        SolverOptions ScreenOpt() => new SolverOptions
        {
            AllowTabCuts = opt.AllowTabCuts, MaxRounds = opt.ScreenRounds, ScreenCoarseMm = opt.ScreenCoarseMm,
            SkipRadiusGrowthAfterFinalCheck = opt.ScreenSkipRadiusGrowth, ReuseSameStateSolves = opt.ScreenReuseSameState,
        };

        ShapeRow SkipRow(double R, double hw, bool taper, string phase)
            => new ShapeRow
            {
                Phase = phase, R = R, HalfW = hw, Taper = taper, Skipped = true,
                SkipWhy = string.Create(ci, $"跳过：管壁 {wall:0.0} 时盘半径至少要 {minDiscAll:0.0}（判据「圆盘盖得住管孔＋焊脚」早筛）"),
            };

        // E：闭式肩部预筛（w < R）。参照行没有设计电流或板厚 ⇒ 估不出 ⇒ 照常求解（说出来）。
        ShapeRow? ShoulderSkip(double R, double hw, bool taper, string phase, ShapeRow? reference)
        {
            if (!opt.ShoulderJPrescreen || !(hw < R - 1e-9) || reference is null) return null;
            var (jEst, detail) = ShoulderJEstimate(hw, reference.Result, reference.Design);
            if (double.IsNaN(jEst))
            {
                Say(string.Create(ci, $"　（盘Ø{2 * R:0.0}／舌宽{2 * hw:0.0}：肩部 J 估不出（{detail}）⇒ 照常求解）"));
                return null;
            }
            double lim = seed.JCheckAPerMm2;
            if (!(jEst > lim)) return null;
            return new ShapeRow
            {
                Phase = phase, R = R, HalfW = hw, Taper = taper, Skipped = true, Prescreened = true,
                SkipWhy = string.Create(ci, $"闭式跳过：肩部 J 估计 {jEst:0.00} > 计算极限 {lim:0.#} A/mm²（盘舌交界截面按 I/(t·2w) 估，{detail}；参照形状 {reference.Tag}）；")
                        + "估计、只作预筛，不是判据：求解器按 J 抬板厚后这个形状可能过 J，本预筛不覆盖",
            };
        }

        (SolverResult sr, int rounds, double sec) SolveOne(DesignSpec spec, string tag, CancellationToken tok)
        {
            int rounds = 0;
            IProgress<string> p = new SyncProgress<string>(s =>
            {
                if (!s.StartsWith("第", StringComparison.Ordinal)) return;
                Interlocked.Increment(ref rounds);
                if (opt.ForwardSolverRounds) progress?.Report($"　　[{tag}] " + s.Split('\n')[0]);
            });
            var sw = Stopwatch.StartNew();
            var sr = solve(spec, baseIn, ScreenOpt(), p, tok);
            sw.Stop();
            return (sr, rounds, sw.Elapsed.TotalSeconds);
        }

        ShapeRow Fill(ShapeRow row, SolverResult sr, int rounds, double sec)
        {
            row.Result = sr;
            row.Design = sr.Design;
            row.Ok = sr.Feasible;
            row.MassG = sr.MassG;
            row.Message = sr.Message ?? "";
            row.StopWhy = sr.StopWhy ?? "";
            row.HitBound = sr.HitBound;
            row.Undetermined = sr.Undetermined;
            row.TabLengthMm = sr.Design?.TabLengthMm ?? double.NaN;
            row.Hot = HardCritValue.Of(sr.Best, LineResult.Key.HotOverContact);
            row.Cold = HardCritValue.Of(sr.Best, LineResult.Key.TubeToFlangeHeat);
            row.Flux = HardCritValue.Of(sr.Best, LineResult.Key.LocalStab);
            row.Blocked = sr.Best?.HardBlocked.Select(c => Criteria.Plain(c.Name)).ToArray() ?? Array.Empty<string>();
            row.Solves = sr.Solves;
            row.Reuses = sr.SameStateReuses;
            row.SkippedGrowthToMm = sr.SkippedRadiusGrowthToMm;
            row.Rounds = rounds;
            row.Seconds = sec;
            return row;
        }

        void Emit(ShapeRow row)
        {
            lock (rowLock) { row.Index = ++idx; res.Rows.Add(row); if (!row.Skipped) solved.Add(row); }
            Say(row.Line());
        }

        // 单点（界面 EvalShape）
        ShapeRow Eval(double R, double hw, bool taper, string phase, ShapeRow? reference = null)
        {
            ct.ThrowIfCancellationRequested();
            if (R < minDiscAll - 1e-9) { var sk = SkipRow(R, hw, taper, phase); Emit(sk); return sk; }
            if (ShoulderSkip(R, hw, taper, phase, reference) is { } sh) { Emit(sh); return sh; }
            var spec = Spec(R, hw, taper);
            var row = new ShapeRow { Phase = phase, R = R, HalfW = hw, Taper = taper };
            var (sr, rounds, sec) = SolveOne(spec, row.Tag, ct);
            Emit(Fill(row, sr, rounds, sec));
            return row;
        }

        // 批（界面 EvalShapesBatch）：批内互相独立，并发；结果按候选原顺序贴回。返回按候选原顺序的行（被取消打断的为 null）。
        ShapeRow?[] EvalBatch(IReadOnlyList<(double R, double hw, bool Taper)> pts, string phase, ShapeRow? reference = null)
        {
            ct.ThrowIfCancellationRequested();
            var outRows = new ShapeRow?[pts.Count];
            if (pts.Count == 0) return outRows;
            var rowsB = new ShapeRow[pts.Count];
            var specs = new List<(int i, DesignSpec s)>();
            for (int i = 0; i < pts.Count; i++)
            {
                var (R, hw, taper) = pts[i];
                if (R < minDiscAll - 1e-9) { rowsB[i] = SkipRow(R, hw, taper, phase); continue; }
                if (ShoulderSkip(R, hw, taper, phase, reference) is { } sh) { rowsB[i] = sh; continue; }
                rowsB[i] = new ShapeRow { Phase = phase, R = R, HalfW = hw, Taper = taper };
                specs.Add((i, Spec(R, hw, taper)));
            }
            ShapeBatchEval.Outcome<bool>? outcome = null;
            if (specs.Count > 0)
                outcome = ShapeBatchEval.RunAsync(specs, (c, tok) =>
                {
                    var (sr, rounds, sec) = SolveOne(c.s, rowsB[c.i].Tag, tok);
                    Fill(rowsB[c.i], sr, rounds, sec);
                    // 批内先到先印一行（进程被杀也留得住已算完的）；批完再按候选原顺序正式编号贴一遍
                    Say("　（批内先到）" + rowsB[c.i].Line());
                    return true;
                }, opt.Lanes, ct).GetAwaiter().GetResult();
            int si = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (rowsB[i].Skipped) { Emit(rowsB[i]); outRows[i] = rowsB[i]; continue; }
                int k = si++;
                if (outcome is null || !outcome.Done[k]) continue;    // 被取消打断的不留痕（与界面一致）
                Emit(rowsB[i]);
                outRows[i] = rowsB[i];
            }
            if (outcome is { Cancelled: true }) ct.ThrowIfCancellationRequested();
            return outRows;
        }

        // ── 基准：先算种子自己的形状
        if (opt.EvalSeedFirst)
        {
            double R0now = seed.DiscRadiusMm, hw0now = seed.TabHalfWidthMm;
            if (R0now > 5 && hw0now > 1)
            {
                Say("（先算种子自己的形状，作基准）");
                Eval(R0now, Math.Min(hw0now, R0now), taperPage, "基准");
            }
        }

        double fWide = wFrac.Max();
        double Rsafe = discs[^1];
        double Rbest = Rsafe;
        bool rbestOk = false;
        bool topOk = false;
        bool doFixpoint = true;
        ShapeRow last;

        // ③ 二分 ＋ ④ 黄金分割（照搬界面 for (fix…) 段里的那两步；并行首遍「有可行点」一支直接调它，区间取自首遍）
        void BisectGolden(double bLo, double bHi)     // bLo 不可行、bHi 可行（已核实）
        {
            for (int bi = 0; bi < opt.BisectMax && bHi - bLo > opt.BisectStopMm; bi++)
            {
                double mid = 0.5 * (bLo + bHi);
                Say($"③ 二分：{(2 * bLo).ToString("0.0", ci)} 不可行 / {(2 * bHi).ToString("0.0", ci)} 可行 ⇒ 试盘Ø{(2 * mid).ToString("0.0", ci)}");
                var rm = Eval(mid, mid * fWide, taperPage, "③");
                if (!rm.Skipped && rm.Ok) { bHi = mid; Rbest = mid; }
                else bLo = mid;
            }
            double gLo = bLo, gHi = Math.Min(bHi + opt.GoldenSlackMm, Rsafe);
            const double Phi = 0.6180339887;
            for (int gi = 0; gi < opt.GoldenMax && gHi - gLo > opt.GoldenStopMm; gi++)
            {
                double x1 = gHi - Phi * (gHi - gLo), x2 = gLo + Phi * (gHi - gLo);
                double probe = (gi % 2 == 0) ? x1 : x2;
                if (solved.Any(r2 => r2.Design is not null && Math.Abs(r2.Design.DiscRadiusMm - probe) < opt.GoldenDedupMm)) { gLo += opt.GoldenDedupMm; continue; }
                Say($"④ 找最轻：区间 盘Ø{(2 * gLo).ToString("0.0", ci)}–{(2 * gHi).ToString("0.0", ci)} ⇒ 试盘Ø{(2 * probe).ToString("0.0", ci)}");
                Eval(probe, probe * fWide, taperPage, "④");
                var okRows = solved.Where(r2 => r2.Ok && r2.Design is not null && !double.IsNaN(r2.MassG)).ToList();
                if (okRows.Count == 0) break;
                double Rmin = okRows.OrderBy(r2 => r2.MassG).First().Design!.DiscRadiusMm;
                if (probe < Rmin) gLo = probe; else gHi = probe;
                Rbest = Rmin;
            }
        }

        if (opt.ParallelFirstPass)
        {
            // ── ① 并行首遍（D）：起点表全部点，舌宽取最宽比例；按盘径从大到小提交（大盘解得慢，先占道）
            Say($"① 并行首遍：起点表全部 {discs.Length} 点 盘Ø{{{string.Join(", ", discs.Select(x => (2 * x).ToString("0.0", ci)))}}}、舌宽比 {fWide.ToString("0.###", ci)}，"
              + $"用 ShapeBatchEval 并行解一遍（并发 {opt.Lanes} 路，按盘径从大到小提交）；拿到每点可不可行与板厚（界面原算法只解表最大值一点：本步是对界面算法的变更，变因：4 核并行、串行首遍是瓶颈）");
            var pass = EvalBatch(discs.OrderByDescending(x => x).Select(R => (R, R * fWide, taperPage)).ToList(), "①")
                       .Where(r => r is not null).Select(r => r!).ToList();
            if (!pass.Any(r => !r.Skipped && r.Ok))
            {
                var ext = new List<double>();
                for (int k = 1; discs[^1] + k * step0 <= opt.MaxDiscMm + 1e-9; k++) ext.Add(discs[^1] + k * step0);
                if (ext.Count > 0)
                {
                    Say($"①外推：起点表全部不可行 ⇒ 向上一批并行解到上端：盘Ø{{{string.Join(", ", ext.Select(x => (2 * x).ToString("0.0", ci)))}}}"
                      + $"（每步 {step0.ToString("0.###", ci)} mm；盘半径上端 {opt.MaxDiscMm.ToString("0.0", ci)} mm，出处：{opt.MaxDiscSource}）");
                    res.ExtrapolatedMm.AddRange(ext);
                    pass.AddRange(EvalBatch(ext.OrderByDescending(x => x).Select(R => (R, R * fWide, taperPage)).ToList(), "①外推")
                                  .Where(r => r is not null).Select(r => r!));
                }
            }
            var okPass = pass.Where(r => !r.Skipped && r.Ok).OrderBy(r => r.R).ToList();
            if (okPass.Count > 0)
            {
                var hiRow = okPass[0];
                Rsafe = okPass[^1].R;
                Rbest = hiRow.R; rbestOk = true; topOk = true; last = hiRow;
                res.StartDiscMm = hiRow.R;
                var loRow = pass.Where(r => r.R < hiRow.R - 1e-9).OrderByDescending(r => r.R).FirstOrDefault();
                if (loRow is not null)
                {
                    res.FirstPassBranch = "有可行点：最小可行点与左邻不可行点之间二分、黄金分割（不再做 ②）";
                    res.BisectLoMm = loRow.R; res.BisectHiMm = hiRow.R;
                    Say($"① 首遍有可行点：最小可行点 盘Ø{(2 * hiRow.R).ToString("0.0", ci)}，左邻不可行点 盘Ø{(2 * loRow.R).ToString("0.0", ci)} ⇒ 在这两点之间 ③ 二分、④ 黄金分割（照旧：≤ {opt.BisectMax} 次到 {opt.BisectStopMm.ToString("0.###", ci)} mm、≤ {opt.GoldenMax} 点）；"
                      + $"黄金分割上沿取首遍已核实可行的最大盘径 盘Ø{(2 * Rsafe).ToString("0.0", ci)}");
                    BisectGolden(loRow.R, hiRow.R);
                    doFixpoint = false;
                }
                else
                {
                    bool all = okPass.Count == pass.Count(r => !r.Skipped);
                    res.FirstPassBranch = all ? "全可行：从下界做 ②（板厚取最小可行点）" : "最小可行点就是表首点（没有左邻不可行点）：同「全可行」一支，从下界做 ②（板厚取最小可行点）";
                    Say("① 首遍" + (all ? "全可行" : $"最小可行点就是表首点 盘Ø{(2 * hiRow.R).ToString("0.0", ci)}（没有左邻不可行点）")
                      + $" ⇒ 直接从下界做 ②：板厚取最小可行点 盘Ø{(2 * hiRow.R).ToString("0.0", ci)} 那一行的");
                }
            }
            else
            {
                var topRow = pass.Where(r => !r.Skipped && Math.Abs(r.R - discs[^1]) < 1e-9).FirstOrDefault()
                          ?? pass.Where(r => !r.Skipped).OrderByDescending(r => r.R).FirstOrDefault();
                last = topRow ?? SkipRow(discs[^1], discs[^1] * fWide, taperPage, "①");
                Rbest = discs[^1]; Rsafe = discs[^1];
                res.StartDiscMm = discs[^1];
                res.FirstPassBranch = "全不可行（外推到上端也无可行）：不二分、不黄金分割；② 取表最大值那一行的板厚";
                Say($"① 首遍（含外推）到盘半径上端 {opt.MaxDiscMm.ToString("0.0", ci)} mm 全不可行 ⇒ 没有可行上端：不二分、不做黄金分割；② 不动点仍照做，板厚取表最大值 盘Ø{(2 * discs[^1]).ToString("0.0", ci)} 那一行的（只用板厚，不依赖上端可行）");
            }
        }
        else
        {
            // ── ① 起点表最大盘径，舌宽取最宽比例（界面原算法；改回 ParallelFirstPass = false）
            Say($"① 先在盘Ø{(2 * Rsafe).ToString("0.0", ci)}（起点表最大值）、舌宽比 {fWide.ToString("0.###", ci)} 解一次，拿到板厚；判据「圆盘盖得住管孔＋焊脚」据此给出最紧的盘径下界（闭式）");
            last = Eval(Rsafe, Rsafe * fWide, taperPage, "①");
            topOk = !last.Skipped && last.Ok;

            // ── (b) 起点不可行 ⇒ 向上外推（界面没有这一步：它默认表最大值可行）
            if (!topOk)
            {
                double R = Rsafe;
                while (R + step0 <= opt.MaxDiscMm + 1e-9)
                {
                    R += step0;
                    Say($"①外推：盘Ø{(2 * (R - step0)).ToString("0.0", ci)} 不可行 ⇒ 向上一步 {step0.ToString("0.###", ci)} mm，试盘Ø{(2 * R).ToString("0.0", ci)}（盘半径上端 {opt.MaxDiscMm.ToString("0.0", ci)} mm，即盘Ø{(2 * opt.MaxDiscMm).ToString("0.0", ci)}；出处：{opt.MaxDiscSource}）");
                    res.ExtrapolatedMm.Add(R);
                    last = Eval(R, R * fWide, taperPage, "①外推");
                    if (!last.Skipped && last.Ok) { topOk = true; Rsafe = R; break; }
                }
                if (!topOk)
                    Say($"①外推到盘半径上端 {opt.MaxDiscMm.ToString("0.0", ci)} mm 仍无可行点 ⇒ 没有可行上端：不二分、不做黄金分割；② 不动点仍照做（只用板厚，不依赖上端可行）");
            }
            res.StartDiscMm = Rsafe;
            Rbest = Rsafe;
            rbestOk = topOk;
        }

        // ── ② 不动点 ＋ ③ 二分 ＋ ④ 黄金分割（照搬界面 for (fix…) 段）
        for (int fix = 0; doFixpoint && fix < opt.FixpointMax; fix++)
        {
            var lastD = last.Skipped ? null : last.Design;
            if (lastD is null) break;
            double floorMm = lastD.DiscFloorMm(baseIn);
            var plates = new FlangePlate[lastD.TabThickMm.Length];
            for (int j2 = 0; j2 < plates.Length; j2++) plates[j2] = lastD.Plate(j2, floorMm);
            double need = GeometryScreen.MinDiscRadiusMm(plates);
            if (double.IsNaN(need) || need <= 0) break;
            double Rnext = Math.Max(need, minDiscAll);
            if (Rnext >= Rbest - opt.FixpointTolMm) break;
            Say($"② 判据下界给出 盘半径 ≥ {need.ToString("0.000", ci)} mm ⇒ 在盘Ø{(2 * Rnext).ToString("0.0", ci)} 再解一次");
            last = Eval(Rnext, Rnext * fWide, taperPage, "②");
            if (last.Skipped || !last.Ok)
            {
                if (!rbestOk)
                {
                    Say("② 下界处也不可行，且没有已核实的可行上端 ⇒ 不二分、不做黄金分割（改动 (b)：界面此处默认上端可行）");
                    break;
                }
                BisectGolden(Rnext, Rbest);      // Rnext 不可行、Rbest 可行（已核实）
                break;
            }
            Rbest = Rnext;
            rbestOk = true;
        }

        // ── ⑤ 在最优盘径上试其余舌宽比例（界面「③ 在最优盘径上把其余舌宽比例各试一次」）
        //   照界面：不看 Rbest 可不可行，无条件试（2026-09-23 对照核实改回：原稿在「没有可行点」时跳过这一步，
        //   那是界面没有、业主也没要的改动；无可行点时更窄的舌宽正是该试的方向，跳过会把搜索盒缩小而不说）。
        //   Rbest 在全程无可行点时 = ① 的起点表最大值（外推没找到可行点时不挪 Rbest，与界面的数据流相同）。
        //   2026-09-24：w < R 的候选先过闭式肩部预筛（参照行 = 盘径 Rbest 上切线族那一行）。
        {
            var batch6 = wFrac.Where(f => Math.Abs(f - fWide) > 1e-9).Select(f => (Rbest, Rbest * f, taperPage)).ToList();
            if (batch6.Count > 0)
            {
                var ref5 = solved.LastOrDefault(r => Math.Abs(r.R - Rbest) < 1e-9 && Math.Abs(r.HalfW - Rbest * fWide) < 1e-9 && r.Taper == taperPage);
                Say($"⑤ 在盘Ø{(2 * Rbest).ToString("0.0", ci)} 上试其余舌宽比例 {{{string.Join(", ", batch6.Select(b => (b.Item2 / Rbest).ToString("0.###", ci)))}}}"
                  + (rbestOk ? "" : "（盘Ø" + (2 * Rbest).ToString("0.0", ci) + " 未核实可行，照界面仍试）"));
                EvalBatch(batch6, "⑤舌宽", ref5);
            }
        }

        // ── ⑥ 邻域爬山（照搬界面，规则全在 ShapeSearchPlan）
        var seen = new HashSet<string>();
        foreach (var r0 in solved)
            if (r0.Design is not null)
                seen.Add(ShapeSearchPlan.Key(r0.Design.DiscRadiusMm, r0.Design.TabHalfWidthMm, r0.Design.TabTaper));
        double BestMass() => solved.Where(x => x.Ok && !double.IsNaN(x.MassG)).Select(x => x.MassG).DefaultIfEmpty(double.NaN).Min();
        double step = ShapeSearchPlan.DiscStepMm;
        for (int ext = 1; ext <= opt.MaxExtend; ext++)
        {
            ct.ThrowIfCancellationRequested();
            var cur = solved.Where(x => x.Ok && !double.IsNaN(x.MassG)).OrderBy(x => x.MassG).FirstOrDefault();
            if (cur?.Design is null) { res.ClimbStop = "没有可行解 ⇒ 没有出发点，没有爬山"; Say("⑥ 没有可行解 ⇒ 没有出发点，不爬山"); break; }
            double before = cur.MassG, R0 = cur.Design.DiscRadiusMm, hw0 = cur.Design.TabHalfWidthMm;
            bool curTaper = cur.Design.TabTaper;
            var todo = ShapeSearchPlan.Worth(ShapeSearchPlan.Neighbours(R0, hw0, curTaper, step), seen);
            if (todo.Count == 0)
            {
                double nx0 = ShapeSearchPlan.Refine(step);
                if (ShapeSearchPlan.StepExhausted(nx0))
                {
                    res.ClimbConverged = true;
                    res.ClimbStop = string.Create(ci, $"第 {ext + 1} 轮：±{step:0.###} mm 的邻点都试过了，步长已收到分辨率下界 {ShapeSearchPlan.MinDiscStepMm:0.###} mm");
                    Say($"⑥ 第 {ext + 1} 轮：±{step.ToString("0.###", ci)} mm 的邻点都试过了，且步长已收到分辨率下界 {ShapeSearchPlan.MinDiscStepMm.ToString("0.###", ci)} mm ⇒ 停");
                    break;
                }
                step = nx0;
                Say($"⑥ 第 {ext + 1} 轮：邻点都试过了 ⇒ 步长减半到 {step.ToString("0.###", ci)} mm，继续");
                continue;
            }
            Say($"⑥ 第 {ext + 1} 轮：从盘Ø{(2 * R0).ToString("0.0", ci)}／舌宽{(2 * hw0).ToString("0.0", ci)}{(curTaper ? "／锥形" : "")}（{before.ToString("0.0", ci)} g）出发，试 {todo.Count} 个邻点（含翻转锥形），步长 {step.ToString("0.###", ci)} mm");
            EvalBatch(todo, $"⑥邻域{ext + 1}", cur);
            double after = BestMass();
            bool better = ShapeSearchPlan.Improved(before, after);
            Say($"⑥ 第 {ext + 1} 轮：{before.ToString("0.0", ci)} → {after.ToString("0.0", ci)} g　" + (better ? "变好，继续" : "没有更好的方向"));
            if (!better)
            {
                double nx = ShapeSearchPlan.Refine(step);
                if (ShapeSearchPlan.StepExhausted(nx))
                {
                    res.ClimbConverged = true;
                    res.ClimbStop = string.Create(ci, $"第 {ext + 1} 轮：步长已收到 {step:0.###} mm（下界 {ShapeSearchPlan.MinDiscStepMm:0.###} mm）仍无改善 ⇒ 在该分辨率上是局部最优");
                    Say($"⑥ 步长已收到 {step.ToString("0.###", ci)} mm（下界 {ShapeSearchPlan.MinDiscStepMm.ToString("0.###", ci)} mm）仍无改善 ⇒ 在 ±{step.ToString("0.###", ci)} mm 分辨率上是局部最优，停");
                    break;
                }
                step = nx;
                Say($"⑥ 这个步长上没有更好 ⇒ 步长减半到 {step.ToString("0.###", ci)} mm 再问一次");
            }
        }

        if (res.ClimbStop.Length == 0)
        {
            res.ClimbStop = string.Create(ci, $"邻域轮数用完（MaxExtend = {opt.MaxExtend}），步长停在 {step:0.###} mm，没有收到分辨率下界 {ShapeSearchPlan.MinDiscStepMm:0.###} mm ⇒ 不能说是局部最优");
            Say("⑥ " + res.ClimbStop);
        }

        // ── 赢家与精算
        var win = solved.Where(x => x.Ok && !double.IsNaN(x.MassG)).OrderBy(x => x.MassG).FirstOrDefault();
        if (win?.Design is null)
        {
            res.NoFeasible = true;
            res.NoFeasibleReport = NoFeasibleReport(res, seed, baseIn, opt, fam);
            Say(res.NoFeasibleReport);
            return res;
        }
        res.Winner = win;
        var (finFine, finFineR) = MeshVerify.RequiredMeshFor(win.Design, baseIn);
        Say($"精算胜出形状 {win.Tag}（粗筛 {win.MassG.ToString("0.0", ci)} g）：细网格 {finFine.ToString("0.000", ci)} mm、细区半径初值 {finFineR.ToString("0.0", ci)} mm（MeshVerify.RequiredMeshFor），最多 {opt.FinalRounds} 轮"
          + "；终局复核后照旧放大重做（不传 SkipRadiusGrowthAfterFinalCheck）" + (opt.FinalReuseSameState ? "；同状态复用开" : ""));
        int finRounds = 0;
        IProgress<string> pf = new SyncProgress<string>(s =>
        {
            if (!s.StartsWith("第", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref finRounds);
            if (opt.ForwardSolverRounds) progress?.Report("　　[精算] " + s.Split('\n')[0]);
        });
        var swF = Stopwatch.StartNew();
        var fin = solve(win.Design, baseIn,
            new SolverOptions { AllowTabCuts = opt.AllowTabCuts, MaxRounds = opt.FinalRounds, FineMm = finFine, FineRadiusMm = finFineR, ReuseSameStateSolves = opt.FinalReuseSameState },
            pf, ct);
        swF.Stop();
        res.Final = fin;
        var refine = ShapeSearchPlan.RefineVerdict(fin.Feasible, fin.StopWhy ?? "", "");
        res.FinalWriteBack = refine.WriteBack;
        res.FinalHeadline = refine.Headline;
        Say(string.Create(ci, $"精算结束：{swF.Elapsed.TotalMinutes:0.0} 分钟、场解 {fin.Solves} 次、同状态复用 {fin.SameStateReuses} 次、进度轮 {finRounds}；") + refine.Headline
          + (fin.FineRefined ? string.Create(ci, $"　已做第二遍细网格求根（{fin.FineMmUsed:0.000} mm）") : "　⚠ 没做第二遍细网格求根 ⇒ 这个解只在导航网格上成立，不可交付"));
        res.SchemeCard = SchemeCard(fin, win, seed, baseIn, opt, fam, res);
        Say(res.SchemeCard);
        return res;
    }

    /// <summary>
    /// 闭式肩部预筛的估计（2026-09-24，E）：盘舌交界截面 J ≈ I /(t · 2w)，逐片取最大。I = 参照形状求解后的设计电流
    /// （<see cref="SolverResult.DesignCurrent"/>.PlateA，20 °C/h 空管升温峰值、共用片已矢量合成），t = 参照形状求解后的板厚（<see cref="DesignSpec.TabThickMm"/>），
    /// w = 候选的舌半宽。**是估计**：真判据（法兰截面 J，<see cref="SectionSizing.Cuts"/>）的交界截面是「边条（弦扣管孔）× 圆盘侧厚 + 焊弧 × 孔边厚」，
    /// 不是 2w × t；本估计只作预筛。估不出（参照没有设计电流或板厚、或全非正）⇒ (NaN, 原因)。
    /// </summary>
    public static (double J, string Detail) ShoulderJEstimate(double halfWidthMm, SolverResult? refResult, DesignSpec? refDesign)
    {
        var ci = CultureInfo.InvariantCulture;
        var I = refResult?.DesignCurrent?.PlateA;
        var t = refDesign?.TabThickMm;
        if (I is null || I.Length == 0) return (double.NaN, "参照形状没有设计电流（SolverResult.DesignCurrent 空）");
        if (t is null || t.Length == 0) return (double.NaN, "参照形状没有板厚");
        if (!(halfWidthMm > 0)) return (double.NaN, "舌半宽非正");
        double worst = double.NegativeInfinity; int at = -1;
        for (int j = 0; j < Math.Min(I.Length, t.Length); j++)
        {
            if (!(I[j] > 0) || !(t[j] > 0)) continue;
            double jj = I[j] / (t[j] * 2 * halfWidthMm);
            if (jj > worst) { worst = jj; at = j; }
        }
        if (at < 0) return (double.NaN, "设计电流或板厚全非正");
        return (worst, string.Create(ci, $"最紧在片{at}：I {I[at]:0} A ÷（t {t[at]:0.00} mm × 2w {2 * halfWidthMm:0.0} mm）；I、t 取参照形状求解后的值"));
    }

    /// <summary>方案卡：几何 ＋ 旋钮终值 ＋ 边界保温条件 ＋ 铂重 ＋ 三条硬判据裕度 ＋ 判决网格精算是否全过 ＋ 可否交付。</summary>
    public static string SchemeCard(SolverResult fin, ShapeRow win, DesignSpec seed, DesignInputs baseIn,
                                    ShapeSearchOptions opt, string fam, ShapeSearchResult res)
    {
        var ci = CultureInfo.InvariantCulture;
        var d = fin.Design ?? win.Design!;
        var sb = new StringBuilder();
        string F(double[]? a, string f) => a is null || a.Length == 0 ? "未查到" : DesignSpec.Fmt(a, f);
        sb.AppendLine("══ 方案卡（Linux 预跑，待 Windows 重录）");
        // 2026-09-23 对照核实改：原稿 Feasible 即写「判决网格精算全过」，没做第二遍细网格求根时这句是假的（只在导航网格上全过）。
        sb.AppendLine(fin.Feasible && fin.FineRefined ? "状态：判决网格精算全过"
                    : fin.Feasible ? "状态：精算全过，但只在导航网格上（没做第二遍细网格求根），不是判决网格上的全过"
                    : "状态：精算没有全过（" + (fin.StopWhy ?? "") + "）");
        sb.AppendLine(fin.FineRefined
            ? string.Create(ci, $"网格：已做第二遍细网格求根（{fin.FineMmUsed:0.000} mm）；细区半径 {(fin.RadiusPlan is null ? "未查到" : fin.RadiusPlan.Describe())}")
            : "网格：没做第二遍细网格求根 ⇒ 这个解只在导航网格上成立，不可交付");
        if (!fin.Feasible || !fin.FineRefined) sb.AppendLine("★ 不可交付");
        sb.AppendLine(string.Create(ci,
            $"几何：盘径 Ø{2 * d.DiscRadiusMm:0.00} mm；舌半宽 {d.TabHalfWidthMm:0.00} mm（舌宽 {2 * d.TabHalfWidthMm:0.00}）；舌长 {d.TabLengthMm:0.0} mm（= 切点 + 压接 {d.ClampLengthMm:0} + 自由段下界 {GeometryScreen.FreeTabMinDefaultMm:0}）；"
          + $"{(d.TabTaper ? "锥形舌边" : "平行舌边")}；族 {fam}；管壁 {d.WallMm:0.00}、内径 {d.TubeIdMm:0.0} mm"));
        sb.AppendLine("旋钮终值（逐片，入口→出口）：板厚 " + F(d.TabThickMm, "0.00") + " mm；舌保温 " + F(d.TabInsulMm, "0.0")
                    + " mm；环倍率 t₁ " + F(d.RingMul, "0.00") + "；外级倍率 t₂ " + F(d.RingMul2, "0.00")
                    + "；内级环宽 " + F(d.RingW1Mm, "0.0") + "、外级环宽 " + F(d.RingW2Mm, "0.0") + " mm（NaN = 默认规则）"
                    + "；舌片厚 " + F(d.TongueThickMm, "0.00") + " mm（闭式，不是旋钮）；槽张角 " + F(d.SlotSpanDeg, "0") + "°；舌孔 R " + F(d.TabHoleRMm, "0.0") + " mm");
        // 边界保温条件：从 DesignSpec／DesignInputs／LineResult 现有字段取
        var disc = Enumerable.Range(0, d.TabThickMm.Length).Select(j => d.DiscInsulMmOf(j)).ToArray();
        string insulRule = "未查到";
        if (fin.Best is { Flanges.Length: > 0 } b)
        {
            var rules = b.Flanges.Select(f => string.IsNullOrWhiteSpace(f.InsulRule) ? "未查到" : f.InsulRule).Distinct().ToArray();
            insulRule = string.Join("；", rules) + "（取自 LineResult.Flanges[j].InsulRule）";
        }
        sb.AppendLine("边界保温条件（这个方案只在这些条件下成立）：");
        foreach (var l in BoundaryLines(d, baseIn)) sb.AppendLine("　" + l);
        sb.AppendLine("　圆盘保温（逐片，算例实际用的）" + DesignSpec.Fmt(disc, "0.0") + string.Create(ci, $" mm（整线 FlangeInsulMm {d.FlangeInsulMm:0.0}、包不包 {(d.FlangeInsulated ? "包" : "不包")}；逐片 DiscInsulMm {(d.DiscInsulMm.Length == 0 ? "空 = 沿用整线" : DesignSpec.Fmt(d.DiscInsulMm, "0.0"))}）"));
        sb.AppendLine("　保温边界半径规则：" + insulRule);
        sb.AppendLine(string.Create(ci, $"铂重：{fin.MassG:0.0} g（粗筛时 {win.MassG:0.0} g）"));
        var hot = HardCritValue.Of(fin.Best, LineResult.Key.HotOverContact);
        var cold = HardCritValue.Of(fin.Best, LineResult.Key.TubeToFlangeHeat);
        var flux = HardCritValue.Of(fin.Best, LineResult.Key.LocalStab);
        sb.AppendLine("决 103 稳态硬判据三条（实际/限值/裕度/判；整片热稳定、管 J、法兰截面 J 与几何两条见「没过或判不了的硬安全线」那行，全过即无）：");
        sb.AppendLine($"　{hot.Name}　{hot.Text()}　{hot.Where}");
        sb.AppendLine($"　{cold.Name}　{cold.Text()}　{cold.Where}");
        sb.AppendLine($"　{flux.Name}　{flux.Text()}　{flux.Where}");
        if (fin.Best is not null && fin.Best.HardBlocked.Length > 0)
            sb.AppendLine("没过或判不了的硬安全线：" + string.Join("；", fin.Best.HardBlocked.Select(c => Criteria.Plain(c.Name))));
        sb.AppendLine(string.Create(ci, $"盒子：盘半径 [{res.MinDiscMm:0.000}, {res.MaxDiscMm:0.0}] mm，上端出处：{res.MaxDiscSource}；粗筛 {opt.ScreenRounds} 轮")
                    + "；邻域停因：" + res.ClimbStop
                    + (res.ClimbConverged ? "" : "（未收敛到分辨率下界，不是局部最优）"));
        sb.AppendLine(ScreenSettingsLine(res, opt));
        sb.AppendLine("精算消息：" + (fin.Message ?? "").Replace("\n", " "));
        return sb.ToString();
    }

    /// <summary>无可行形状报告：每个形状的停因、三条硬判据值与裕度、铂重、卡住的判据、盒子的范围与上端出处。不静默。</summary>
    public static string NoFeasibleReport(ShapeSearchResult res, DesignSpec seed, DesignInputs baseIn, ShapeSearchOptions opt, string fam)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("══ 无可行形状（Linux 预跑，待 Windows 重录）");
        sb.AppendLine(string.Create(ci,
            $"盒子：盘半径 [{res.MinDiscMm:0.000}, {res.MaxDiscMm:0.0}] mm（下界：判据「圆盘盖得住管孔＋焊脚」闭式；上端出处：{res.MaxDiscSource}）；起点表 {{{string.Join(", ", res.DiscGrid.Select(x => x.ToString("0.0##", ci)))}}}；"
          + $"外推到 {{{string.Join(", ", res.ExtrapolatedMm.Select(x => x.ToString("0.0##", ci)))}}}；舌宽比 {{{string.Join(", ", opt.WFrac.Select(x => x.ToString("0.###", ci)))}}}；族 {fam}；粗筛 {opt.ScreenRounds} 轮"));
        int nSolved = res.Rows.Count(r => !r.Skipped), nSkip = res.Rows.Count(r => r.Skipped);
        int nUnd = res.Rows.Count(r => !r.Skipped && r.Undetermined), nHit = res.Rows.Count(r => !r.Skipped && r.HitBound);
        sb.AppendLine($"计数：评估 {res.Rows.Count} 个（求解 {nSolved}、闭式跳过 {nSkip}，其中肩部预筛 {res.Rows.Count(r => r.Prescreened)}）；其中到顶 {nHit}、判不了 {nUnd}、可行 0");
        sb.AppendLine(ScreenSettingsLine(res, opt));
        sb.AppendLine("逐形状（停因原样取 SolverResult.StopWhy，空则取 Message）：");
        foreach (var r in res.Rows)
        {
            string why = r.Skipped ? r.SkipWhy : (string.IsNullOrWhiteSpace(r.StopWhy) ? r.Message : r.StopWhy);
            sb.AppendLine(string.Create(ci, $"　#{r.Index} {r.Phase} {r.Tag}：铂重 {(double.IsNaN(r.MassG) ? "—" : r.MassG.ToString("0.0", ci))} g；")
                + $"{r.Hot.Name} {r.Hot.Text()}；{r.Cold.Name} {r.Cold.Text()}；{r.Flux.Name} {r.Flux.Text()}；"
                + $"卡住：{(r.Blocked.Length == 0 ? "—" : string.Join("、", r.Blocked))}；停因：{why.Replace("\n", " ")}");
        }
        // 每条硬判据在盒内最好的裕度与位置（只在解出来的行里取）
        void BestOf(string name, Func<ShapeRow, HardCritValue> pick)
        {
            var c = res.Rows.Where(r => !r.Skipped && pick(r).Present && !double.IsNaN(pick(r).Margin))
                            .OrderByDescending(r => pick(r).Margin).FirstOrDefault();
            sb.AppendLine(c is null ? $"　{name}：没有一个形状解出这条"
                                    : string.Create(ci, $"　{name}：最好裕度 {pick(c).Margin:0.000}，在 #{c.Index} {c.Tag}"));
        }
        sb.AppendLine("每条硬判据在盒内最好的裕度：");
        BestOf(Criteria.Plain(LineResult.Key.HotOverContact), r => r.Hot);
        BestOf(Criteria.Plain(LineResult.Key.TubeToFlangeHeat), r => r.Cold);
        BestOf(Criteria.Plain(LineResult.Key.LocalStab), r => r.Flux);
        sb.AppendLine("边界保温条件（全程固定，不是搜索维度）：");
        foreach (var l in BoundaryLines(seed, baseIn)) sb.AppendLine("　" + l);
        sb.AppendLine("　圆盘保温（逐片）" + DesignSpec.Fmt(Enumerable.Range(0, seed.TabThickMm.Length).Select(j => seed.DiscInsulMmOf(j)).ToArray(), "0.0") + " mm");
        sb.AppendLine("盒外下一根杠杆（未在本次搜索里动）：圆盘保温、管保温、铜排夹持温度、控温点、盘径上端（本次上端无出处）、另一解法族");
        sb.AppendLine("不覆盖：粗筛轮数截断的形状（停因写「跑满」的）不等于不可行；肩部预筛跳过的形状（估计，不是判据）求解器加厚后可能过；"
                    + "粗筛跳过放大重做的形状（「粗筛本该放大到」一列有数的）热点处温度类判据不算数；本报告只覆盖上面列出的形状点");
        return sb.ToString();
    }

    /// <summary>粗筛设置与算力选项的一行（方案卡与无解报告共用）：轮数、放大重做、同状态复用、舌宽比、肩部预筛、首遍分支，与跳过放大的形状。</summary>
    internal static string ScreenSettingsLine(ShapeSearchResult res, ShapeSearchOptions opt)
    {
        var ci = CultureInfo.InvariantCulture;
        var grown = res.Rows.Where(r => !r.Skipped && !double.IsNaN(r.SkippedGrowthToMm)).ToList();
        return string.Create(ci, $"粗筛设置：{opt.ScreenRounds} 轮（2026-09-24 选定，界面 16；依据夜跑 001304／021617，跑过再校）；")
             + "终局复核后" + (opt.ScreenSkipRadiusGrowth ? "不放大重做" : "照旧放大重做")
             + "；同状态复用 " + (opt.ScreenReuseSameState ? "开" : "关") + string.Create(ci, $"（粗筛共省 {res.Rows.Sum(r => r.Reuses)} 次场解、解了 {res.Rows.Sum(r => r.Solves)} 次）")
             + "；舌宽比 {" + string.Join(", ", opt.WFrac.Select(x => x.ToString("0.###", ci))) + "}"
             + "；肩部预筛 " + (opt.ShoulderJPrescreen ? "开" : "关") + string.Create(ci, $"（跳过 {res.Rows.Count(r => r.Prescreened)} 个）")
             + "；① " + (res.FirstPassBranch.Length > 0 ? "并行首遍：" + res.FirstPassBranch : "界面原算法")
             + (grown.Count == 0 ? "；粗筛跳过放大重做的形状：无"
                : "；粗筛跳过放大重做的形状 " + grown.Count + " 个（" + string.Join("、", grown.Select(r => string.Create(ci, $"#{r.Index} {r.Tag} 本该放大到 {r.SkippedGrowthToMm:0.00} mm"))) + "；这些行热点处的温度类判据不算数，只作排序）");
    }

    /// <summary>
    /// 边界条件行（管保温三层、舌端铜排边界、环境温度）。全部取现有字段，规则与 <see cref="DesignSpec"/> 组算例那段相同：
    /// 管保温内层厚 = DesignSpec.TubeInsulMm（启用 = 厚 &gt; 0），中层、外层取 DesignInputs.Layer2／Layer3；
    /// 舌端边界的种类由 <see cref="ShellThermal.ClampBoundaryOf"/> 判（与热解同一份），夹持温度 = DesignSpec.ClampTempC。
    /// 2026-09-23 对照核实补：原稿只印内层厚（读来像管保温只有这一层），且不论边界种类一律印「铜排夹持温度」（热导边界下夹持温度不用）。
    /// </summary>
    internal static IEnumerable<string> BoundaryLines(DesignSpec d, DesignInputs baseIn)
    {
        var ci = CultureInfo.InvariantCulture;
        var p = SegmentSolver.Clone(baseIn);
        p.Layer1.ThicknessMm = d.TubeInsulMm;
        p.Layer1.Enabled = d.TubeInsulMm > 1e-6;
        p.BusbarClampTempC = d.ClampTempC;
        string L(string pos, InsulationLayer l) => string.Create(ci, $"{pos}「{l.Name}」{l.ThicknessMm:0.0} mm{(l.Enabled ? "" : "（未启用）")}");
        yield return "管保温：" + L("内层", p.Layer1) + "（厚度取 DesignSpec.TubeInsulMm）；" + L("中层", p.Layer2) + "；" + L("外层", p.Layer3) + "（中外层取 DesignInputs.Layer2／Layer3）";
        var kind = ShellThermal.ClampBoundaryOf(p);
        yield return kind switch
        {
            ShellThermal.ClampBoundary.FixedTemp => string.Create(ci, $"舌端铜排边界：定温，夹持温度 {d.ClampTempC:0} °C（DesignSpec.ClampTempC 写进算例的 BusbarClampTempC）；压接段 {d.ClampLengthMm:0} mm"),
            ShellThermal.ClampBoundary.Conductance => string.Create(ci, $"舌端铜排边界：热导（DesignInputs.BusbarConductanceWPerK = {baseIn.BusbarConductanceWPerK:0.###} W/K；许用电流密度与到冷端长度都给了时由 LineRunner 逐片重算），夹持温度不用；压接段 {d.ClampLengthMm:0} mm"),
            _ => string.Create(ci, $"舌端铜排边界：自由端（热导与夹持温度都没给）；压接段 {d.ClampLengthMm:0} mm"),
        };
        yield return string.Create(ci, $"环境温度 {baseIn.TAmbC:0.0} °C（DesignInputs.TAmbC）");
    }

    // 2026-09-25：同步进度接收器改用 Core/SyncProgress.cs 那一份（全仓只许一份）。
}
