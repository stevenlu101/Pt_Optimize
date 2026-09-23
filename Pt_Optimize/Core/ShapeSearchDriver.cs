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
/// 缺省值出处：除 <see cref="MaxDiscMm"/>、<see cref="DiscGridMm"/> 两项外，全部**照抄界面
/// <c>UI/LineDesignPage.cs</c> 的 <c>SearchOneFamilyAsync</c>（07644dc）里的值**，判定与阈值一个不挪：
/// 二分 ≤ 3 次、区间 ≤ 1 mm 停；黄金分割 ≤ 4 点、区间 ≤ 1 mm 停、往可行侧留 +6 mm 余地、已算过 ±0.5 mm 去重；
/// 不动点 ≤ 3 次、贴下界 0.05 mm 算到了；舌宽比 0.75／1.00；粗筛 16 轮、精算 40 轮；邻域最多 6 轮；
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

    /// <summary>舌宽比例（半宽 / 盘半径）。照抄界面 SearchWFrac。</summary>
    public double[] WFrac = { 0.75, 1.00 };

    /// <summary>粗筛每点的轮数上限。照抄界面 SearchScreenRounds。</summary>
    public int ScreenRounds = 16;
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
    /// <summary>进度里以「第」开头的行数（与界面推进度条同一数法）。</summary>
    public int Rounds;
    public double Seconds;
    public DesignSpec? Design;
    public SolverResult? Result;

    public string Tag => $"盘Ø{2 * R:0.0}／舌宽{2 * HalfW:0.0}{(Taper ? "／锥形" : "")}";

    public const string Header =
        "序\t阶段\t盘径Ø\t舌宽\t舌边\t舌长\t结果\t铂重g\t最热铂高出热偶读数 K（实际/限/裕度/判）\t管根低于热偶读数 K\t管孔净流入 W\t卡住的硬安全线\t场解\t轮\t秒\t停因／消息";

    public string Line()
    {
        var ci = CultureInfo.InvariantCulture;
        if (Skipped)
            return string.Create(ci, $"{Index}\t{Phase}\t{2 * R:0.00}\t{2 * HalfW:0.00}\t{(Taper ? "锥形" : "平行")}\t—\t跳过\t—\t—\t—\t—\t—\t0\t0\t0\t{SkipWhy}");
        string verdict = Ok ? "✓可行" : Undetermined ? "判不了" : HitBound ? "到顶" : "不可行";
        string why = (Ok ? Message : (string.IsNullOrWhiteSpace(StopWhy) ? Message : StopWhy)).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return string.Create(ci,
            $"{Index}\t{Phase}\t{2 * R:0.00}\t{2 * HalfW:0.00}\t{(Taper ? "锥形" : "平行")}\t{TabLengthMm:0.0}\t{verdict}\t"
          + $"{(double.IsNaN(MassG) ? "—" : MassG.ToString("0.0", ci))}\t{Hot.Text()}\t{Cold.Text()}\t{Flux.Text()}\t"
          + $"{(Blocked.Length == 0 ? "—" : string.Join("；", Blocked))}\t{Solves}\t{Rounds}\t{Seconds:0}\t{why}");
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
    public readonly List<string> Log = new();
}

/// <summary>
/// ★ 搜形状驱动：把界面 <c>UI/LineDesignPage.cs</c> 的 <c>SearchOneFamilyAsync</c>（07644dc，约 3177～3650 行）
/// ①～⑥ 逐段照搬进 Core，判定与阈值不动；界面、命令行、测试将来都可以调它（界面改调是 Windows 待办）。
///
/// 照搬的步骤（与界面同序）：
///   基准：先把种子自己的形状算一遍（不可行只是表上多一行，不作出发点）。
///   ① 在起点表最大盘径（舌宽取最宽比例，即切线族 hw = R）上解一次，拿到板厚。
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
///       变因：界面默认表最大值可行（二分区间的可行上端没有核对，LineDesignPage.cs:3436），上端不可行时二分是在一个假区间上做的。
///       外推到头仍无可行点 ⇒ 不二分（没有可行上端）、不爬山（没有可行出发点）；② 不动点仍照做（它只用板厚，不依赖上端可行），
///       最后仍无可行 ⇒ 出「无可行形状」报告：每个形状的停因、三条硬判据值与裕度、铂重、卡住的判据、盒子的范围与上端出处。
///
/// 另一处与界面不同（写明，不静默）：早筛的管孔半径取 <see cref="DesignSpec.HoleRadiusMm"/>（= 壁厚 + 内径/2），
///   界面写的是「壁厚 + 25」。变因：业主「通用型解法」原则（换内径也要出结果）；内径 50 时两者逐位相同（现役 W08、W06 都是 50）。
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
        };

        ShapeRow SkipRow(double R, double hw, bool taper, string phase)
            => new ShapeRow
            {
                Phase = phase, R = R, HalfW = hw, Taper = taper, Skipped = true,
                SkipWhy = string.Create(ci, $"跳过：管壁 {wall:0.0} 时盘半径至少要 {minDiscAll:0.0}（判据「圆盘盖得住管孔＋焊脚」早筛）"),
            };

        (SolverResult sr, int rounds, double sec) SolveOne(DesignSpec spec, string tag, CancellationToken tok)
        {
            int rounds = 0;
            IProgress<string> p = new SyncProgress(s =>
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
            row.Hot = HardCritValue.Of(sr.Best, LineResult.Key.HotOverTc);
            row.Cold = HardCritValue.Of(sr.Best, LineResult.Key.ColdUnderTc);
            row.Flux = HardCritValue.Of(sr.Best, LineResult.Key.NetFlux);
            row.Blocked = sr.Best?.HardBlocked.Select(c => Criteria.Plain(c.Name)).ToArray() ?? Array.Empty<string>();
            row.Solves = sr.Solves;
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
        ShapeRow Eval(double R, double hw, bool taper, string phase)
        {
            ct.ThrowIfCancellationRequested();
            if (R < minDiscAll - 1e-9) { var sk = SkipRow(R, hw, taper, phase); Emit(sk); return sk; }
            var spec = Spec(R, hw, taper);
            var row = new ShapeRow { Phase = phase, R = R, HalfW = hw, Taper = taper };
            var (sr, rounds, sec) = SolveOne(spec, row.Tag, ct);
            Emit(Fill(row, sr, rounds, sec));
            return row;
        }

        // 批（界面 EvalShapesBatch）：批内互相独立，并发；结果按候选原顺序贴回
        void EvalBatch(IReadOnlyList<(double R, double hw, bool Taper)> pts, string phase)
        {
            ct.ThrowIfCancellationRequested();
            if (pts.Count == 0) return;
            var rowsB = new ShapeRow[pts.Count];
            var specs = new List<(int i, DesignSpec s)>();
            for (int i = 0; i < pts.Count; i++)
            {
                var (R, hw, taper) = pts[i];
                if (R < minDiscAll - 1e-9) { rowsB[i] = SkipRow(R, hw, taper, phase); continue; }
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
                if (rowsB[i].Skipped) { Emit(rowsB[i]); continue; }
                int k = si++;
                if (outcome is null || !outcome.Done[k]) continue;    // 被取消打断的不留痕（与界面一致）
                Emit(rowsB[i]);
            }
            if (outcome is { Cancelled: true }) ct.ThrowIfCancellationRequested();
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

        // ── ① 起点表最大盘径，舌宽取最宽比例
        double fWide = wFrac.Max();
        double Rsafe = discs[^1];
        Say($"① 先在盘Ø{(2 * Rsafe).ToString("0.0", ci)}（起点表最大值）、舌宽比 {fWide.ToString("0.###", ci)} 解一次，拿到板厚；判据「圆盘盖得住管孔＋焊脚」据此给出最紧的盘径下界（闭式）");
        ShapeRow last = Eval(Rsafe, Rsafe * fWide, taperPage, "①");
        bool topOk = !last.Skipped && last.Ok;

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

        // ── ② 不动点 ＋ ③ 二分 ＋ ④ 黄金分割（照搬界面 for (fix…) 段）
        double Rbest = Rsafe;
        bool rbestOk = topOk;
        for (int fix = 0; fix < opt.FixpointMax; fix++)
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
                double bLo = Rnext, bHi = Rbest;      // bLo 不可行、bHi 可行（已核实）
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
                break;
            }
            Rbest = Rnext;
            rbestOk = true;
        }

        // ── ⑤ 在最优盘径上试其余舌宽比例（界面「③ 在最优盘径上把其余舌宽比例各试一次」）
        //   照界面：不看 Rbest 可不可行，无条件试（2026-09-23 对照核实改回：原稿在「没有可行点」时跳过这一步，
        //   那是界面没有、业主也没要的改动；无可行点时更窄的舌宽正是该试的方向，跳过会把搜索盒缩小而不说）。
        //   Rbest 在全程无可行点时 = ① 的起点表最大值（外推没找到可行点时不挪 Rbest，与界面的数据流相同）。
        {
            var batch6 = wFrac.Where(f => Math.Abs(f - fWide) > 1e-9).Select(f => (Rbest, Rbest * f, taperPage)).ToList();
            if (batch6.Count > 0)
            {
                Say($"⑤ 在盘Ø{(2 * Rbest).ToString("0.0", ci)} 上试其余舌宽比例 {{{string.Join(", ", batch6.Select(b => (b.Item2 / Rbest).ToString("0.###", ci)))}}}"
                  + (rbestOk ? "" : "（盘Ø" + (2 * Rbest).ToString("0.0", ci) + " 未核实可行，照界面仍试）"));
                EvalBatch(batch6, "⑤舌宽");
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
            EvalBatch(todo, $"⑥邻域{ext + 1}");
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
        Say($"精算胜出形状 {win.Tag}（粗筛 {win.MassG.ToString("0.0", ci)} g）：细网格 {finFine.ToString("0.000", ci)} mm、细区半径初值 {finFineR.ToString("0.0", ci)} mm（MeshVerify.RequiredMeshFor），最多 {opt.FinalRounds} 轮");
        int finRounds = 0;
        IProgress<string> pf = new SyncProgress(s =>
        {
            if (!s.StartsWith("第", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref finRounds);
            if (opt.ForwardSolverRounds) progress?.Report("　　[精算] " + s.Split('\n')[0]);
        });
        var swF = Stopwatch.StartNew();
        var fin = solve(win.Design, baseIn,
            new SolverOptions { AllowTabCuts = opt.AllowTabCuts, MaxRounds = opt.FinalRounds, FineMm = finFine, FineRadiusMm = finFineR },
            pf, ct);
        swF.Stop();
        res.Final = fin;
        var refine = ShapeSearchPlan.RefineVerdict(fin.Feasible, fin.StopWhy ?? "", "");
        res.FinalWriteBack = refine.WriteBack;
        res.FinalHeadline = refine.Headline;
        Say(string.Create(ci, $"精算结束：{swF.Elapsed.TotalMinutes:0.0} 分钟、场解 {fin.Solves} 次、进度轮 {finRounds}；") + refine.Headline
          + (fin.FineRefined ? string.Create(ci, $"　已做第二遍细网格求根（{fin.FineMmUsed:0.000} mm）") : "　⚠ 没做第二遍细网格求根 ⇒ 这个解只在导航网格上成立，不可交付"));
        res.SchemeCard = SchemeCard(fin, win, seed, baseIn, opt, fam, res);
        Say(res.SchemeCard);
        return res;
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
        var hot = HardCritValue.Of(fin.Best, LineResult.Key.HotOverTc);
        var cold = HardCritValue.Of(fin.Best, LineResult.Key.ColdUnderTc);
        var flux = HardCritValue.Of(fin.Best, LineResult.Key.NetFlux);
        sb.AppendLine("三条硬判据（实际/限值/裕度/判）：");
        sb.AppendLine($"　{hot.Name}　{hot.Text()}　{hot.Where}");
        sb.AppendLine($"　{cold.Name}　{cold.Text()}　{cold.Where}");
        sb.AppendLine($"　{flux.Name}　{flux.Text()}　{flux.Where}");
        if (fin.Best is not null && fin.Best.HardBlocked.Length > 0)
            sb.AppendLine("没过或判不了的硬安全线：" + string.Join("；", fin.Best.HardBlocked.Select(c => Criteria.Plain(c.Name))));
        sb.AppendLine(string.Create(ci, $"盒子：盘半径 [{res.MinDiscMm:0.000}, {res.MaxDiscMm:0.0}] mm，上端出处：{res.MaxDiscSource}；粗筛 {opt.ScreenRounds} 轮")
                    + "；邻域停因：" + res.ClimbStop
                    + (res.ClimbConverged ? "" : "（未收敛到分辨率下界，不是局部最优）"));
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
        sb.AppendLine($"计数：评估 {res.Rows.Count} 个（求解 {nSolved}、闭式跳过 {nSkip}）；其中到顶 {nHit}、判不了 {nUnd}、可行 0");
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
        BestOf(Criteria.Plain(LineResult.Key.HotOverTc), r => r.Hot);
        BestOf(Criteria.Plain(LineResult.Key.ColdUnderTc), r => r.Cold);
        BestOf(Criteria.Plain(LineResult.Key.NetFlux), r => r.Flux);
        sb.AppendLine("边界保温条件（全程固定，不是搜索维度）：");
        foreach (var l in BoundaryLines(seed, baseIn)) sb.AppendLine("　" + l);
        sb.AppendLine("　圆盘保温（逐片）" + DesignSpec.Fmt(Enumerable.Range(0, seed.TabThickMm.Length).Select(j => seed.DiscInsulMmOf(j)).ToArray(), "0.0") + " mm");
        sb.AppendLine("盒外下一根杠杆（未在本次搜索里动）：圆盘保温、管保温、铜排夹持温度、控温点、盘径上端（本次上端无出处）、另一解法族");
        sb.AppendLine("不覆盖：粗筛轮数截断的形状（停因写「跑满」的）不等于不可行；本报告只覆盖上面列出的形状点");
        return sb.ToString();
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

    /// <summary>同步进度接收器（不经同步上下文，调用线程上当场执行）。</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _a;
        public SyncProgress(Action<string> a) => _a = a;
        public void Report(string value) => _a(value);
    }
}
