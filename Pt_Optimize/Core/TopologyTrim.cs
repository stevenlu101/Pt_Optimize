using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// ★ 2026-09-25 拓扑去料原型（全局解决方案 §9.4 第二步、§9.6「拓扑去料的候选生成器」选项 B 的最小版：**栅格路径逐轮去料**）。
///
/// 做法（舌片与圆盘同一套，都是栅格厚度场里的格）：
/// · 第 0 轮：解析板逐片栅格化（<see cref="AnalyticSurrogate.Rasterize"/>，步与留白由选项给）成 <see cref="ThicknessField"/>，走 LineRunner 图纸路径
///   （LineCase.FlangeFields 非空、FlangePlates 清空、GeomForJudge = 解析板、TabInsul3dmPerPlateMm 逐片；与 R48ArcGapDiagTests 门 f 同一做法）。
///   0 轮 = 纯栅格化对照，与图纸路径直跑逐位相同（改回值：<see cref="TopologyTrimOptions.MaxRounds"/> = 0）。
/// · 每轮：LineRunner.Run → 若解不出、硬判据不过（<see cref="LineResult.HardBlocked"/>；按选项另把决 103 的整片／局部热稳定当硬判据）或
///   栅格截面 J 不满足「&lt; J_设计 + 1」（与 LineRunner「法兰截面 J」同一比较式，<see cref="SectionSizing.JCheckOf"/>）⇒ 撤销上一轮去料、停，记停因。
/// · 否则按每片最新收敛场算逐格移除优先级 —— **复用** <see cref="RemovalPriority.Compute"/>（P = 导热贡献 q ÷ 电流密度 J，网格单元量），
///   栅格格取它所在网格单元的 P（不另编公式；同一单元里的格同分，按格序号定序）。
/// · 排除区：压接段格（x ≤ 材料 xMin + 压接长 + 一步；两舌片对称）、管孔环带（r ≤ 孔半径 + 焊脚 + 桥宽，桥宽照 DesignSpec.TabHoleRMaxMm 缺省 4.0，出处未查到）、
///   当前最紧截面所在列（或圈）、去了会让料块浮空的格（本地 4 邻接单点判别，保守：只要 3×3 邻域里的料不再互相 4 连通就不去）。
/// · 每轮去掉优先级最高的 CellsPerRound 格（缺省 = 每片有料格数的 2 %，选定），去料后重算每处截面 J（沿 x 逐列：有料长 × 厚；绕孔逐圈：有料弧长 × 厚；
///   |x| &lt; 孔半径的列另加焊弧项，与 SectionSizing.Cuts 舌盘交界那一段同一算式），不满足限值的那一处把本轮去掉的格退回；退无可退 ⇒ 撤销本轮、停。
///
/// 没做（写在实施记录里）：解析板反推（去料轮廓回写成解析族）、拓扑板第二遍细网格、升温期逐点复判、去料后叉臂式加厚。
/// </summary>
public sealed class TopologyTrimOptions
{
    /// <summary>栅格步 mm（选定 0.5：与 R48ArcGapDiagTests／R48RecipeFingerprintTests 图纸路径同一步）。</summary>
    public double StepMm = 0.5;
    /// <summary>图幅留白 mm（选定 2.0：同上两门的图纸路径）。</summary>
    public double MarginMm = 2.0;
    /// <summary>最多去料轮数；0 = 纯栅格化对照（改回值）。</summary>
    public int MaxRounds = 6;
    /// <summary>每轮每片去掉的格数；≤ 0 ⇒ 按 <see cref="CellsPerRoundFrac"/> × 该片有料格数（向下取整，至少 1）。</summary>
    public int CellsPerRound = 0;
    /// <summary>每轮去料比例（选定 2 %：每轮去料量小到一轮场解能分辨、六轮合计 ≈ 11 % 面积；无制造出处）。</summary>
    public double CellsPerRoundFrac = 0.02;
    /// <summary>管孔环带外扩桥宽 mm：照 DesignSpec.TabHoleRMaxMm 缺省 minBridgeMm = 4.0（DesignSpec.cs:753；制造出处未查到，§9.6 已列待决定）。</summary>
    public double BridgeMm = 4.0;
    /// <summary>true = 决 103 的整片热稳定与局部热稳定也当硬判据（§9.4 第二步；LineRunner 里这两行 Kind 仍是 Reference，LineRunner.cs:4627／4793，HardBlocked 不含它们）。false = 只按 HardBlocked（改回值）。</summary>
    public bool TreatStabilityAsHard = true;
    /// <summary>true = 优先级按网格单元面积归一（P ÷ 单元面积；q 是整个单元的瓦数，粗格天然大）。false = 照 RemovalPriority 原样（改回值，缺省）。</summary>
    public bool PriorityPerArea = false;
    /// <summary>退回循环上限（每次退回一处截面的格，正常几次就收）。</summary>
    public int MaxRestoreLoops = 200;
}

/// <summary>一处截面（列或圈）的截面 J。</summary>
public readonly record struct TopologyCut(string Where, bool IsColumn, int Index, double AtMm, double LengthMm, double AreaMm2, double JAPerMm2);

public sealed class TopologyTrimRound
{
    public int Round;
    public bool Accepted;
    public double MassG, FlangeMassG;
    /// <summary>各片有料面积合计 mm²（栅格）。</summary>
    public double AreaMm2;
    /// <summary>本轮相对第 0 轮的累计去料面积 mm²。</summary>
    public double RemovedMm2;
    public int CellsRemoved, CellsRestored;
    public double WorstSectionJ = double.NaN; public string WorstSectionWhere = "";
    public double[] PlateWorstJ = Array.Empty<double>(); public string[] PlateWorstWhere = Array.Empty<string>();
    /// <summary>LineRunner 自己那条「法兰截面 J」（读的是等效解析板 GeomForJudge，看不见去料；只印作对照）。</summary>
    public double RunnerSectionJ = double.NaN;
    public double JLimit = double.NaN;
    public bool DesignCurrentFallback;
    public double HotOverK = double.NaN, HotOverLimitK = double.NaN, NetInflowW = double.NaN, NetInflowLimitW = double.NaN, LocalStab = double.NaN, FlangeStab = double.NaN;
    public string HotOverWhere = "", NetInflowWhere = "", LocalStabWhere = "", FlangeStabWhere = "";
    public string[] Blocked = Array.Empty<string>();
    public int FloatingRaster, FloatingMesh;
    public string StopReason = "";
    public double Seconds;
    public bool RunnerOk; public string RunnerMessage = "";
}

public sealed class TopologyTrimResult
{
    public List<TopologyTrimRound> Rounds = new();
    public string[] PlateNames = Array.Empty<string>();
    /// <summary>第 0 轮（纯栅格化）的场。</summary>
    public ThicknessField[] Fields0 = Array.Empty<ThicknessField>();
    /// <summary>最终接受的场（最后一轮没过就是上一轮的）。</summary>
    public ThicknessField[] Fields = Array.Empty<ThicknessField>();
    public LineResult? Round0, Final;
    public string StopReason = "";
    public double JLimit = double.NaN;
    public TopologyTrimOptions Options = new();

    /// <summary>每片最终厚度场的文本转储：X0/Z0/Step/Nx/Nz + 逐行厚度（z 从大到小一行一行印，好直接画），另附 ASCII 图（# 有料、. 无料、o 去掉的）。</summary>
    public string DumpFields()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (int p = 0; p < Fields.Length; p++)
        {
            var f = Fields[p]; var f0 = p < Fields0.Length ? Fields0[p] : null;
            sb.AppendLine($"## 片 {p + 1} {(p < PlateNames.Length ? PlateNames[p] : "")}　X0 {f.X0.ToString("0.###", ci)}　Z0 {f.Z0.ToString("0.###", ci)}　Step {f.Step.ToString("0.###", ci)}　Nx {f.Nx}　Nz {f.Nz}　有料面积 {f.AreaMm2.ToString("0.##", ci)} mm²　体积 {f.VolumeMm3.ToString("0.##", ci)} mm³");
            sb.AppendLine("### ASCII 图（每行一个 z，自上而下 z 从大到小；每列一个 x，自左而右 x 从小到大）");
            for (int j = f.Nz - 1; j >= 0; j--)
            {
                var row = new char[f.Nx];
                for (int i = 0; i < f.Nx; i++)
                {
                    bool has = f.T[i * f.Nz + j] > 1e-9, had = f0 is not null && f0.T[i * f.Nz + j] > 1e-9;
                    row[i] = has ? '#' : had ? 'o' : '.';
                }
                sb.Append(new string(row)).Append('\n');
            }
            sb.AppendLine("### 逐行厚度 mm（行 = z 自上而下从大到小，列 = x 从小到大，0 = 无料）");
            for (int j = f.Nz - 1; j >= 0; j--)
            {
                sb.Append('z').Append((f.Z0 + j * f.Step).ToString("0.###", ci)).Append(':');
                for (int i = 0; i < f.Nx; i++)
                {
                    double t = f.T[i * f.Nz + j];
                    sb.Append(' ').Append(t > 1e-9 ? t.ToString("0.00", ci) : "0");
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}

public static class TopologyTrim
{
    /// <summary>从解析设计出发：BuildCase 拿 FlangePlates，其余同 <see cref="Run(LineCase, TopologyTrimOptions, IProgress{string}?, CancellationToken)"/>。</summary>
    public static TopologyTrimResult Run(DesignSpec d, DesignInputs p, TopologyTrimOptions o,
                                         IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (p is null) throw new ArgumentNullException(nameof(p));
        return Run(d.BuildCase(p), o, progress, cancel);
    }

    /// <summary>
    /// 从一个带解析板（FlangePlates 非空）的算例出发。算例本身不改；每轮 <see cref="FlangeAutoSizer.CloneCase"/> 一份副本改走图纸路径。
    /// </summary>
    public static TopologyTrimResult Run(LineCase template, TopologyTrimOptions o,
                                         IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        o ??= new TopologyTrimOptions();
        if (template.FlangePlates is not { Length: > 0 })
            throw new InvalidOperationException("拓扑去料要从解析板出发（LineCase.FlangePlates 为空）：图纸档没有解析板可栅格化");
        if (!(o.StepMm > 0)) throw new ArgumentOutOfRangeException(nameof(o), "栅格步必须为正");

        var plates = template.FlangePlates;
        int np = plates.Length;
        double holeR = template.TubeIdMm * 0.5 + template.WallMm;
        double clampLen = template.Base.BusbarClampLengthMm;
        double jLimit = SectionSizing.JCheckOf(template.JDesignAPerMm2);

        var res = new TopologyTrimResult { Options = o, JLimit = jLimit, PlateNames = plates.Select((pl, j) => PlateName(template, j)).ToArray() };
        var fields0 = plates.Select(pl => AnalyticSurrogate.Rasterize(pl, o.StepMm, o.MarginMm)).ToArray();
        res.Fields0 = fields0;
        var cur = fields0.Select(f => f.WithThickness((double[])f.T.Clone())).ToArray();
        ThicknessField[] accepted = fields0;
        LineResult? acceptedRes = null;
        int[] countRound0 = fields0.Select(f => f.T.Count(v => v > 1e-9)).ToArray();

        for (int round = 0; round <= o.MaxRounds; round++)
        {
            cancel.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rr = new TopologyTrimRound { Round = round, JLimit = jLimit };
            progress?.Report($"拓扑去料 第 {round} 轮／{o.MaxRounds}：图纸路径解场…");
            var lc = MakeCase(template, cur);
            LineResult r;
            try { r = LineRunner.Run(lc, progress, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rr.RunnerOk = false; rr.RunnerMessage = ex.GetType().Name + ": " + ex.Message;
                rr.StopReason = $"第 {round} 轮 LineRunner 抛异常：{rr.RunnerMessage}";
                rr.Seconds = sw.Elapsed.TotalSeconds;
                res.Rounds.Add(rr);
                break;
            }
            rr.RunnerOk = r.Ok; rr.RunnerMessage = r.Message;
            rr.MassG = r.TotalMassG; rr.FlangeMassG = r.FlangeMassG;
            rr.AreaMm2 = cur.Sum(f => f.AreaMm2);
            rr.RemovedMm2 = Enumerable.Range(0, np).Sum(j => (countRound0[j] - cur[j].T.Count(v => v > 1e-9)) * cur[j].Step * cur[j].Step);
            if (round == 0) res.Round0 = r;

            // ── 判据读数（决 103 四条 + 运行器自己的截面 J）
            ReadChecks(r, rr, o);
            // ── 栅格截面 J（每片）
            var cutsPer = new List<TopologyCut>[np];
            rr.PlateWorstJ = new double[np]; rr.PlateWorstWhere = new string[np];
            double worst = double.NegativeInfinity; string worstWhere = ""; bool undet = false;
            for (int j = 0; j < np; j++)
            {
                var (iA, fb) = DesignCurrentOf(r, j);
                if (fb) rr.DesignCurrentFallback = true;
                cutsPer[j] = Cuts(cur[j], plates[j], iA, holeR, clampLen);
                var w = WorstOf(cutsPer[j]);
                rr.PlateWorstJ[j] = w.JAPerMm2; rr.PlateWorstWhere[j] = w.Where;
                if (double.IsNaN(w.JAPerMm2)) undet = true;
                else if (w.JAPerMm2 > worst) { worst = w.JAPerMm2; worstWhere = $"{res.PlateNames[j]} {w.Where}"; }
            }
            rr.WorstSectionJ = undet ? double.NaN : worst; rr.WorstSectionWhere = undet ? "判不了（设计电流缺）" : worstWhere;
            // ── 浮空
            rr.FloatingRaster = Enumerable.Range(0, np).Sum(j => FloatingComponents(cur[j], plates[j], holeR, clampLen));
            rr.FloatingMesh = r.Flanges.Sum(f => f?.MeshRecipe?.FloatingComponents ?? 0);

            bool sectionFail = undet || !(worst < jLimit);
            bool fail = !r.Ok || rr.Blocked.Length > 0 || sectionFail;
            if (fail)
            {
                string why = !r.Ok ? $"解不出：{r.Message}"
                           : rr.Blocked.Length > 0 ? "硬判据不过：" + string.Join("；", rr.Blocked)
                           : $"栅格截面 J {worst:0.###} 不满足 < {jLimit:0.#}（{worstWhere}）";
                rr.Accepted = false;
                rr.StopReason = round == 0
                    ? $"第 0 轮（纯栅格化，未去料）已不过 ⇒ 不去料，停。{why}"
                    : $"第 {round} 轮去料后不过 ⇒ 撤销第 {round} 轮去料，回到第 {round - 1} 轮。{why}";
                rr.Seconds = sw.Elapsed.TotalSeconds;
                res.Rounds.Add(rr);
                progress?.Report(rr.StopReason);
                break;
            }
            rr.Accepted = true;
            accepted = cur.Select(f => f.WithThickness((double[])f.T.Clone())).ToArray();
            acceptedRes = r;
            if (round == o.MaxRounds)
            {
                rr.StopReason = o.MaxRounds == 0 ? "0 轮：纯栅格化对照（改回值），不去料" : $"到轮数上限 {o.MaxRounds}，停";
                rr.Seconds = sw.Elapsed.TotalSeconds;
                res.Rounds.Add(rr);
                break;
            }

            // ── 生成下一轮：逐片去料
            progress?.Report($"拓扑去料 第 {round} 轮：按场排优先级、去料…");
            int removedAll = 0, restoredAll = 0; string? abort = null;
            var next = cur.Select(f => f.WithThickness((double[])f.T.Clone())).ToArray();
            for (int j = 0; j < np; j++)
            {
                var fo = j < r.Flanges.Length ? r.Flanges[j] : null;
                if (fo?.Mesh is null || fo.JField.Length < fo.Mesh.CellCount || fo.TField.Length < fo.Mesh.CellCount)
                { abort = $"{res.PlateNames[j]}：结果里没有网格或场（Mesh/JField/TField），排不了优先级"; break; }
                var (iA, _) = DesignCurrentOf(r, j);
                var f = next[j];
                var pr = RasterPriority(f, fo, o);
                var excl = ExclusionMask(f, plates[j], holeR, clampLen, o);
                MarkTightest(f, cutsPer[j], excl);
                int nMat = f.T.Count(v => v > 1e-9);
                int k = o.CellsPerRound > 0 ? o.CellsPerRound : Math.Max(1, (int)Math.Floor(nMat * o.CellsPerRoundFrac));
                var order = Enumerable.Range(0, f.T.Length)
                    .Where(q => f.T[q] > 1e-9 && !excl[q] && !double.IsNaN(pr[q]))
                    .OrderByDescending(q => pr[q]).ThenBy(q => q).ToList();
                var removed = new List<(int q, double t)>();
                foreach (int q in order)
                {
                    if (removed.Count >= k) break;
                    if (!SimplePoint(f, q)) continue;
                    removed.Add((q, f.T[q])); f.T[q] = 0;
                }
                // 去料后重算每处截面 J，不满足的那一处退回
                int loops = 0, restored = 0;
                while (true)
                {
                    var cuts = Cuts(f, plates[j], iA, holeR, clampLen);
                    var w = WorstOf(cuts);
                    if (double.IsNaN(w.JAPerMm2) || w.JAPerMm2 < jLimit) break;
                    if (++loops > o.MaxRestoreLoops) { abort = $"{res.PlateNames[j]}：退回循环超过 {o.MaxRestoreLoops} 次仍有截面 J ≥ {jLimit:0.#}（{w.Where}）"; break; }
                    int n0 = restored;
                    for (int m = removed.Count - 1; m >= 0; m--)
                    {
                        var (q, t) = removed[m];
                        if (f.T[q] > 1e-9) continue;
                        if (OnCut(f, q, w))
                        { f.T[q] = t; restored++; }
                    }
                    if (restored == n0)
                    { abort = $"{res.PlateNames[j]}：截面 J {w.JAPerMm2:0.###} ≥ {jLimit:0.#}（{w.Where}）而那一处没有本轮去掉的格可退"; break; }
                }
                if (abort is not null) break;
                removedAll += removed.Count(rm => f.T[rm.q] <= 1e-9);
                restoredAll += restored;
            }
            rr.CellsRemoved = removedAll; rr.CellsRestored = restoredAll;
            rr.Seconds = sw.Elapsed.TotalSeconds;
            if (abort is not null)
            {
                rr.StopReason = $"第 {round} 轮已过；生成第 {round + 1} 轮去料失败 ⇒ 停在第 {round} 轮。{abort}";
                res.Rounds.Add(rr); progress?.Report(rr.StopReason); break;
            }
            if (removedAll == 0)
            {
                rr.StopReason = $"第 {round} 轮已过；没有可去的格（排除区之外没有不破坏连通的候选）⇒ 停在第 {round} 轮";
                res.Rounds.Add(rr); progress?.Report(rr.StopReason); break;
            }
            res.Rounds.Add(rr);
            cur = next;
        }

        res.Fields = accepted;
        res.Final = acceptedRes;
        res.StopReason = res.Rounds.Count > 0 ? res.Rounds[^1].StopReason : "没跑任何一轮";
        if (res.StopReason.Length == 0) res.StopReason = "未记停因";
        return res;
    }

    // ───────────────────────────── 算例 ─────────────────────────────

    /// <summary>与 R48ArcGapDiagTests 门 f 同一做法把算例改走图纸路径：FlangeFields 非空、GeomForJudge = 解析板、TabInsul3dmPerPlateMm 逐片、FlangePlates 清空。</summary>
    public static LineCase MakeCase(LineCase template, ThicknessField[] fields)
    {
        var lc = FlangeAutoSizer.CloneCase(template);
        lc.FlangeFields = fields;
        lc.GeomForJudge = template.FlangePlates;
        lc.TabInsul3dmPerPlateMm = template.FlangePlates.Select(pl => pl.TabInsulThickMm).ToArray();
        lc.FlangePlates = Array.Empty<FlangePlate>();
        return lc;
    }

    private static string PlateName(LineCase c, int j)
        => j == 0 ? "入口" : j == c.FlangePlates.Length - 1 ? "出口" : $"共用{j}";

    /// <summary>设计电流：图纸路径上 LineRunner 只在 CheckRamp 开时算（DesignCurrent.Compute 在「法兰截面 J」那一段）；缺时退回该片稳态电流并标记（不是升温峰值）。</summary>
    private static (double A, bool Fallback) DesignCurrentOf(LineResult r, int j)
    {
        var fo = j < r.Flanges.Length ? r.Flanges[j] : null;
        if (fo is null) return (double.NaN, false);
        if (!double.IsNaN(fo.DesignCurrentA) && fo.DesignCurrentA > 0) return (fo.DesignCurrentA, false);
        return (fo.CurrentA > 0 ? fo.CurrentA : double.NaN, true);
    }

    private static void ReadChecks(LineResult r, TopologyTrimRound rr, TopologyTrimOptions o)
    {
        var blocked = r.HardBlocked.Select(c => $"{Criteria.Plain(c.Name)} {c.Actual:0.###}/{c.Limit:0.###}{(c.Undetermined ? "（判不了）" : "")}").ToList();
        var hot = r.Find(LineResult.Key.HotOverContact);
        if (hot is not null) { rr.HotOverK = hot.Actual; rr.HotOverLimitK = hot.Limit; rr.HotOverWhere = hot.Where; }
        var cold = r.Find(LineResult.Key.TubeToFlangeHeat);
        if (cold is not null) { rr.NetInflowW = cold.Actual; rr.NetInflowLimitW = cold.Limit; rr.NetInflowWhere = cold.Where; }
        var ls = r.Find(LineResult.Key.LocalStab);
        if (ls is not null) { rr.LocalStab = ls.Actual; rr.LocalStabWhere = ls.Where; }
        var fs = r.Find(LineResult.Key.FlangeStab);
        if (fs is not null) { rr.FlangeStab = fs.Actual; rr.FlangeStabWhere = fs.Where; }
        if (o.TreatStabilityAsHard)
        {
            foreach (var c in new[] { ls, fs })
                if (c is not null && (c.Undetermined || !c.Ok) && c.Kind != CheckKind.HardSafety)
                    blocked.Add($"{Criteria.Plain(c.Name)} {c.Actual:0.###}/{c.Limit:0.###}{(c.Undetermined ? "（判不了）" : "")}（决 103 当硬判据；运行器 Kind = {c.Kind}）");
        }
        var sj = r.Find(LineResult.Key.SectionJ);
        if (sj is not null) rr.RunnerSectionJ = sj.Actual;
        rr.Blocked = blocked.ToArray();
    }

    // ───────────────────────────── 优先级：网格单元 → 栅格格 ─────────────────────────────

    /// <summary>栅格每格的移除优先级：取它的格心所在网格单元（<see cref="ShellMesh.CellRects"/>，没有就读节点矩形）的 <see cref="RemovalPriority.Compute"/>。没落进任何单元的格 NaN。</summary>
    public static double[] RasterPriority(ThicknessField f, FlangeOut fo, TopologyTrimOptions o)
    {
        var mesh = fo.Mesh ?? throw new ArgumentException("片结果里没有网格", nameof(fo));
        var p = RemovalPriority.Compute(mesh, fo.JField, fo.TField, fo.TRootC);
        var pr = new double[f.T.Length];
        Array.Fill(pr, double.NaN);
        for (int c = 0; c < mesh.CellCount; c++)
        {
            if (mesh.Part[c] != 0 || !(mesh.Area[c] > 0) || double.IsNaN(p[c])) continue;
            double v = o.PriorityPerArea ? p[c] / mesh.Area[c] : p[c];
            var rects = mesh.CellRects is not null && c < mesh.CellRects.Length && mesh.CellRects[c] is { } rl
                ? rl : new List<(double, double, double, double)> { FlangeMesher.CellRect(mesh, c) };
            foreach (var (x0, x1, z0, z1) in rects)
            {
                int i0 = (int)Math.Ceiling((x0 - f.X0) / f.Step - 1e-9), i1 = (int)Math.Floor((x1 - f.X0) / f.Step + 1e-9);
                int j0 = (int)Math.Ceiling((z0 - f.Z0) / f.Step - 1e-9), j1 = (int)Math.Floor((z1 - f.Z0) / f.Step + 1e-9);
                for (int i = Math.Max(0, i0); i <= Math.Min(f.Nx - 1, i1); i++)
                    for (int j = Math.Max(0, j0); j <= Math.Min(f.Nz - 1, j1); j++)
                        pr[i * f.Nz + j] = v;
            }
        }
        return pr;
    }

    // ───────────────────────────── 排除区 ─────────────────────────────

    /// <summary>压接段格 + 管孔环带（不含「最紧截面所在处」，那个每轮按 <see cref="MarkTightest"/> 另标）。true = 不许去。</summary>
    public static bool[] ExclusionMask(ThicknessField f, FlangePlate pl, double holeR, double clampLenMm, TopologyTrimOptions o)
    {
        var env = f.MaterialEnvelope();
        double xMin = env.XMin, xMax = env.XMax;
        double rBand = holeR + Math.Max(0, pl.WeldFilletLegMm) + o.BridgeMm;
        var m = new bool[f.T.Length];
        for (int i = 0; i < f.Nx; i++)
        {
            double x = f.X0 + i * f.Step;
            bool clamp = x <= xMin + clampLenMm + f.Step + 1e-9 || (pl.TwoTabs && x >= xMax - clampLenMm - f.Step - 1e-9);
            for (int j = 0; j < f.Nz; j++)
            {
                double z = f.Z0 + j * f.Step;
                m[i * f.Nz + j] = clamp || x * x + z * z <= rBand * rBand + 1e-9;
            }
        }
        return m;
    }

    /// <summary>把当前最紧截面所在的那一列（或那一圈 ±一步）标进排除区。</summary>
    public static void MarkTightest(ThicknessField f, List<TopologyCut> cuts, bool[] excl)
    {
        var w = WorstOf(cuts);
        if (double.IsNaN(w.JAPerMm2) || w.Index < 0) return;
        for (int q = 0; q < f.T.Length; q++) if (OnCut(f, q, w)) excl[q] = true;
    }

    private static bool OnCut(ThicknessField f, int q, TopologyCut w)
    {
        int i = q / f.Nz, j = q % f.Nz;
        if (w.IsColumn) return i == w.Index;
        double x = f.X0 + i * f.Step, z = f.Z0 + j * f.Step, r = Math.Sqrt(x * x + z * z);
        return Math.Abs(r - w.AtMm) <= f.Step + 1e-9;
    }

    // ───────────────────────────── 连通 ─────────────────────────────

    /// <summary>
    /// 本地单点判别（保守）：去掉格 q 之后，它 3×3 邻域里剩下的料仍互相 4 连通才允许。
    /// 判法：邻域 8 格里有料的按 4 邻接分组（只在这 8 格里连），q 的 4 邻居有料的全落在同一组 ⇒ 允许。
    /// 会漏放一些其实靠远路仍连通的格（保守），不会放过任何真会断的格。仓库里现有的连通检查只在网格上量（FlangeMesher.MeshConnectivity），栅格上没有，故自做并写明。
    /// </summary>
    public static bool SimplePoint(ThicknessField f, int q)
    {
        int i = q / f.Nz, j = q % f.Nz;
        bool Mat(int a, int b) => a >= 0 && a < f.Nx && b >= 0 && b < f.Nz && f.T[a * f.Nz + b] > 1e-9;
        // 环上 8 格按顺时针：N, NE, E, SE, S, SW, W, NW（di, dj）
        var ring = new (int di, int dj)[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) };
        var mat = new bool[8];
        for (int k = 0; k < 8; k++) mat[k] = Mat(i + ring[k].di, j + ring[k].dj);
        // 环上相邻两格（k, k+1）是 4 邻接的（一个正向一个斜向，坐标差 (1,0)/(0,1)）；隔一个的不是
        var grp = new int[8]; Array.Fill(grp, -1); int g = 0;
        for (int k = 0; k < 8; k++)
        {
            if (!mat[k] || grp[k] >= 0) continue;
            grp[k] = g;
            // 沿环两个方向扩展
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int a = k;
                while (true)
                {
                    int b = ((a + dir) % 8 + 8) % 8;
                    if (!mat[b] || grp[b] >= 0) break;
                    grp[b] = g; a = b;
                }
            }
            g++;
        }
        int gN = -1;
        foreach (int k in new[] { 0, 2, 4, 6 })
        {
            if (!mat[k]) continue;
            if (gN < 0) gN = grp[k];
            else if (grp[k] != gN) return false;
        }
        return true;
    }

    /// <summary>栅格上的浮空料块数：4 邻接连通分量里既不含压接段格（x ≤ xMin + 压接长）也不含孔边格（r ≤ 孔半径 + 一步）的。</summary>
    public static int FloatingComponents(ThicknessField f, FlangePlate pl, double holeR, double clampLenMm)
    {
        var env = f.MaterialEnvelope();
        int n = f.T.Length; var seen = new bool[n]; int floating = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (seen[s] || f.T[s] <= 1e-9) continue;
            bool anchored = false; seen[s] = true; stack.Push(s);
            while (stack.Count > 0)
            {
                int q = stack.Pop(); int i = q / f.Nz, j = q % f.Nz;
                double x = f.X0 + i * f.Step, z = f.Z0 + j * f.Step;
                if (x <= env.XMin + clampLenMm + 1e-9 || (pl.TwoTabs && x >= env.XMax - clampLenMm - 1e-9) || x * x + z * z <= (holeR + f.Step) * (holeR + f.Step)) anchored = true;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || a >= f.Nx || b < 0 || b >= f.Nz) continue;
                    int q2 = a * f.Nz + b;
                    if (seen[q2] || f.T[q2] <= 1e-9) continue;
                    seen[q2] = true; stack.Push(q2);
                }
            }
            if (!anchored) floating++;
        }
        return floating;
    }

    // ───────────────────────────── 截面 J（栅格） ─────────────────────────────

    /// <summary>
    /// 栅格板上每一处必经截面的 J = 设计电流 ÷ 面积（闭式，不解场；与 SectionSizing.Cuts 同一口径，只是几何从栅格量）：
    /// · 列：x 从压接段末端到舌盘切点（解析板 <see cref="FlangePlate.Tangent"/> 的 X；两舌片时对称另一侧），面积 = Σ 有料格 × 步 × 厚；
    ///   |x| &lt; 孔半径的列另加焊弧项 2·min(acos(|x|/rh), asin(hw/rh))·rh × 孔边厚（SectionSizing.Cuts 舌盘交界那一段的算式，SectionSizing.cs:196～212）；
    /// · 圈：r 从 孔半径 + 焊脚 到 压接段距离，120 等分（同 SectionSizing.Cuts 的 M = 120）；沿圆周按弧长 ≈ 步/2 采样，面积 = Σ 有料弧长 × 该点厚。
    /// 两舌片的板：每侧的列都按全电流算（保守，偏大；每舌分流多少这里不解）。设计电流 NaN ⇒ 全部 NaN（判不了）。
    /// </summary>
    public static List<TopologyCut> Cuts(ThicknessField f, FlangePlate pl, double currentA, double holeR, double clampLenMm)
    {
        var cuts = new List<TopologyCut>();
        var env = f.MaterialEnvelope();
        double xMin = env.XMin, xMax = env.XMax;
        var (xT, _) = pl.Tangent();
        double tEdge = Math.Max(f.At(-holeR - f.Step, 0), 1e-9);
        double J(double a) => double.IsNaN(currentA) ? double.NaN : a > 1e-9 ? currentA / a : double.PositiveInfinity;

        for (int i = 0; i < f.Nx; i++)
        {
            double x = f.X0 + i * f.Step;
            bool left = x > xMin + clampLenMm + 1e-9 && x <= xT + 1e-9;
            bool right = pl.TwoTabs && x < xMax - clampLenMm - 1e-9 && x >= -xT - 1e-9;
            if (!left && !right) continue;
            double len = 0, a = 0, hw = 0;
            for (int j = 0; j < f.Nz; j++)
            {
                double t = f.T[i * f.Nz + j];
                if (t <= 1e-9) continue;
                len += f.Step; a += f.Step * t;
                hw = Math.Max(hw, Math.Abs(f.Z0 + j * f.Step));
            }
            string where;
            if (Math.Abs(x) < holeR)
            {
                double th0 = Math.Acos(Math.Min(1.0, Math.Abs(x) / holeR)), thMax = Math.Asin(Math.Min(1.0, hw / holeR));
                double arc = 2 * Math.Min(th0, thMax) * holeR;
                a += arc * tEdge;
                where = $"列 x={x:0.#}（边条 {len:0.#} mm + 焊弧 {arc:0.#} mm）";
            }
            else where = $"列 x={x:0.#}（有料 {len:0.#} mm）";
            cuts.Add(new TopologyCut(where, true, i, x, len, a, J(a)));
        }

        double rIn = holeR + Math.Max(0, pl.WeldFilletLegMm);
        double rOut = Math.Min(Math.Abs(xMin + clampLenMm), pl.TwoTabs ? Math.Abs(xMax - clampLenMm) : double.PositiveInfinity);
        if (rOut > rIn + 1e-9)
        {
            const int M = 120;
            for (int k = 0; k <= M; k++)
            {
                double r = rIn + (rOut - rIn) * k / M;
                int ns = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r / (0.5 * f.Step)));
                double dth = 2 * Math.PI / ns, len = 0, a = 0;
                for (int s = 0; s < ns; s++)
                {
                    double th = (s + 0.5) * dth;
                    double t = f.At(r * Math.Cos(th), r * Math.Sin(th));
                    if (t <= 1e-9) continue;
                    len += r * dth; a += r * dth * t;
                }
                cuts.Add(new TopologyCut($"圈 r={r:0.#}（有料弧长 {len:0.#} mm）", false, k, r, len, a, J(a)));
            }
        }
        return cuts;
    }

    /// <summary>最紧的一处（J 最大；有 NaN ⇒ NaN）。空表 ⇒ NaN。</summary>
    public static TopologyCut WorstOf(List<TopologyCut> cuts)
    {
        if (cuts.Count == 0) return new TopologyCut("（没有截面）", true, -1, double.NaN, 0, 0, double.NaN);
        if (cuts.Any(c => double.IsNaN(c.JAPerMm2))) return new TopologyCut("判不了（设计电流缺）", true, -1, double.NaN, 0, 0, double.NaN);
        var w = cuts[0];
        foreach (var c in cuts) if (c.JAPerMm2 > w.JAPerMm2) w = c;
        return w;
    }
}
