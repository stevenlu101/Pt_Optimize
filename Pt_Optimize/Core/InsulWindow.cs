using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ U 路（2026-09-18，Opus 5）：**每片舌保温的可行窗口** —— 从 L 路的探针提成生产件。
///
/// ══ 它回答什么
///
/// 求解器交出来的是**一个点**（每片一个舌保温值）。现场缠保温是**一层一层**缠的，每层
/// <see cref="InsulationSearch.LayerMm"/> mm，而且不缠时是「裸舌」（等效 <see cref="SolverOptions.InsLoMm"/>）——
/// 所以现场能做出来的只有 <c>{裸舌} ∪ {0.5 的正整数倍}</c> 这一串值。
/// 于是「解出来了」不等于「造得出来」：解值旁边那个能落层的档，判据不一定还过。
///
/// 本件逐片把舌保温上下各挪一点（其余片、其余旋钮**一位不动**），逐点跑整线、按整线的交付判定
/// （<see cref="LineResult.AllOk"/>，与交付同一份）判过不过，量出解值所在的那一段连续可行区间：
///   · 区间 [下, 上]、宽度；
///   · 区间里落得进的层数档（**0 层合法** —— 裸舌本来就是一种做法）；
///   · 左右边界外**第一个不过的是哪条判据**。
///
/// ══ 判定口径（跑前写死，跑完不挪）
///
///   · 任一片的窗口里**一个能做的档都没有** ⇒ 结论「当前判据下无可制造设计」，点名是哪几片，
///     并报最接近的设计（解值）与每片差在哪条判据、差多少。
///     **不改判据、不硬凑**：判据要不要放宽是工程师在参数表里填的事（「管根低于热偶读数 允许值」等两项）。
///   · 窗口宽 &lt; 一层 ⇒ 点名「窗口窄于制造精度」。
///   · 扫到边界仍全过 ⇒ 记「边界没探到」，不许把扫描半宽的两倍当成窗口宽。
///   · 解值那一点自己就不过 ⇒ 照实报，以它为中心的窗口无从谈起；扫到的可行段照样列出来。
///
/// ══ 成本
///
/// 每点一次整线全耦合解（判决网格上 0.5～1.5 min），四片 × 21 档 ≈ 1 小时。
/// ⇒ **只在终验跑一次**（参数表「终验时量每片舌保温的可行窗口」，默认开），优化循环里不跑。
/// 想快看一眼可以把 <see cref="Options.UseNavigationMesh"/> 打开走导航网格 —— 那只是快筛，
/// 判决仍以判决网格为准（导航网格上这两条温度判据实测偏大，见 MeshVerify 的注释表）。
///
/// ⚠ 本类**不自己判判据**：过不过一律问 <see cref="LineRunner.Run"/> 出来的 <see cref="LineResult.AllOk"/>。
///   网格配方也不自己抄，走 <see cref="Solver.ApplyCaseMesh"/> 与 <see cref="MeshVerify.RequiredMeshFor(DesignSpec,bool)"/>。
/// </summary>
public static class InsulWindow
{
    /// <summary>一次扫描里某一点的结果。</summary>
    public sealed class Point
    {
        /// <summary>这一点的舌保温 mm。</summary>
        public double Mm;
        /// <summary>true = 可行（解出来了 ∧ 耦合收敛 ∧ 交付判据全过）；false = 不过；null = 判不了（含下界外）。</summary>
        public bool? Feasible;
        /// <summary>不过／判不了的原因（判据全名，不带代号）。</summary>
        public string Why = "";
        public double HotK = double.NaN, ColdK = double.NaN, NetFluxW = double.NaN;
        /// <summary>这一点是不是现场做得出来的档（裸舌或整数层）。</summary>
        public bool OnLayerGrid;
        /// <summary>这一点是不是求解器交出来的那个值。</summary>
        public bool IsSolved;
    }

    /// <summary>一片的窗口。</summary>
    public sealed class PlateWindow
    {
        public int Plate;
        public string Name = "";
        /// <summary>求解器交出来的值 mm。</summary>
        public double SolvedMm;
        /// <summary>解值那一点自己过不过。</summary>
        public bool SolvedFeasible;
        /// <summary>解值所在的连续可行区间 [下, 上]；解值不可行时是 NaN。</summary>
        public double LoMm = double.NaN, HiMm = double.NaN;
        /// <summary>区间宽度 mm；解值不可行时是 NaN。</summary>
        public double WidthMm = double.NaN;
        /// <summary>扫到扫描下／上边界仍可行 ⇒ 这一头**没探到**，真实窗口更宽。</summary>
        public bool OpenLo, OpenHi;
        /// <summary>窗口里落得进的层数档 mm（裸舌记作第一档；空 = 一个都落不进）。</summary>
        public double[] LayerMm = Array.Empty<double>();
        /// <summary>那几档各是几层（与 <see cref="LayerMm"/> 同序，走 <see cref="InsulationSearch.LayersText"/>）。</summary>
        public string[] LayerText = Array.Empty<string>();
        /// <summary>窗口左／右边界外第一个不过的是哪条判据（含实际值／限值）。</summary>
        public string BlockedBelow = "", BlockedAbove = "";
        /// <summary>解值那一点不过时，不过的是哪几条。</summary>
        public string SolvedWhy = "";
        /// <summary>解值不可行时扫到的可行段（有比没有强）。</summary>
        public (double Lo, double Hi)[] OtherFeasible = Array.Empty<(double, double)>();
        public Point[] Points = Array.Empty<Point>();

        /// <summary>这一片在现场的格子上有没有可落的档。</summary>
        public bool HasLayer => LayerMm.Length > 0;
        /// <summary>窗口窄于一层（两头都探到才算得准）。</summary>
        public bool NarrowerThanOneLayer(double layerMm)
            => SolvedFeasible && !OpenLo && !OpenHi && !double.IsNaN(WidthMm) && WidthMm < layerMm - 1e-9;
    }

    public sealed class Options
    {
        /// <summary>扫描半宽 mm（解值 ±）。</summary>
        public double HalfWidthMm = 1.0;
        /// <summary>扫描步长 mm。</summary>
        public double StepMm = 0.1;
        /// <summary>
        /// false（默认）= 在**判决用的那张网格**上量（<see cref="MeshVerify.RequiredMeshFor(DesignSpec,bool)"/>，与交付判定同一张）；
        /// true = 导航网格快筛（只统一细区半径，尺寸照 BuildCase）——**只作快筛，不作判决**。
        /// </summary>
        public bool UseNavigationMesh;
        /// <summary>并发几条整线解。默认留两个核给界面。</summary>
        public int MaxDegreeOfParallelism = Math.Max(1, Math.Min(5, Environment.ProcessorCount - 2));
        /// <summary>跑前写死的时间闸：超了就切断，被切断的片**不完整、不许当结果读**。</summary>
        public TimeSpan Cap = TimeSpan.FromHours(3);
        /// <summary>求解器选项 —— 裸舌下界与舌保温图纸格都从它读，本类不抄常数。</summary>
        public SolverOptions Solver = new();
        /// <summary>
        /// 可注入的「这一点过不过」。null（默认）= 真跑整线（<see cref="LineRunner.Run"/>）。
        /// 只给门用：窗口的切段、落档、判定这套逻辑要能在毫秒内验，不必等一小时的整线解。
        /// </summary>
        public Func<DesignSpec, int, double, Point>? Evaluate;
    }

    /// <summary>整条线的窗口结果。</summary>
    public sealed class Result
    {
        public PlateWindow[] Plates = Array.Empty<PlateWindow>();
        /// <summary>一层多厚 mm（现场包法）。</summary>
        public double LayerThickMm;
        /// <summary>裸舌（0 层）的等效厚度 mm。</summary>
        public double BareTabMm;
        public double HalfWidthMm, StepMm;
        public bool NavigationMesh;
        public double MeshFineMm = double.NaN, MeshRadiusMm = double.NaN;
        public double Seconds;
        /// <summary>撞上时间闸被切断 ⇒ 有片不完整。</summary>
        public bool Cut;
        public string DesignName = "";

        /// <summary>每一片的窗口里都至少落得进一档 ⇒ 这份设计现场做得出来。</summary>
        public bool Manufacturable => Plates.Length > 0 && !Cut && Plates.All(p => p.HasLayer);
        /// <summary>一个档都落不进的片。</summary>
        public PlateWindow[] NoLayerPlates => Plates.Where(p => !p.HasLayer).ToArray();
        /// <summary>窗口窄于一层的片（两头都探到）。</summary>
        public PlateWindow[] NarrowPlates => Plates.Where(p => p.NarrowerThanOneLayer(LayerThickMm)).ToArray();

        /// <summary>
        /// 结论一句话。**判定口径跑前写死**：任一片落不进任何档 ⇒「当前判据下无可制造设计」，点名那几片。
        /// 不改判据、不硬凑 —— 要放宽在参数表的两项温差预算里填。
        /// </summary>
        public string Verdict
        {
            get
            {
                if (Plates.Length == 0) return "没有量到任何一片 —— 判不了，不许当成过。";
                if (Cut) return "⚠ 扫描撞上时间闸被切断，有片不完整 —— **判不了**，不要据此说这份设计造得出来或造不出来。";
                var none = NoLayerPlates;
                if (none.Length > 0)
                    return $"**当前判据下无可制造设计**：{string.Join("、", none.Select(p => p.Name))} 的可行窗口里一个能缠的档都没有"
                         + $"（现场一层 {LayerThickMm:0.###} mm，不缠时是裸舌 {BareTabMm:0.###} mm）。"
                         + "最接近的设计就是求解器交出来的这一份，逐片差在哪条判据见下表。"
                         + "判据没有被放宽 —— 要放宽，在输入页最上面那组「判据限值与可行窗口」里填"
                         + "（「管根低于热偶读数 允许值」「最热铂高出热偶读数 允许值」）。";
                var narrow = NarrowPlates;
                return "每一片的窗口里都至少落得进一档 ⇒ 这份设计缠得出来。"
                     + (narrow.Length == 0 ? "" : $"　⚠ 窗口窄于一层的片：{string.Join("、", narrow.Select(p => $"{p.Name}（宽 {p.WidthMm:0.###} mm）"))} —— 现场缠偏一层就出界。");
            }
        }

        /// <summary>给输出框／安装报告的一张小表（制表位，界面由 TextFmt 排成 Excel 式）。</summary>
        public string Table()
        {
            var sb = new StringBuilder();
            sb.AppendLine("片\t解值 mm\t可行窗口 mm\t宽 mm\t可落层数\t左边界外不过的是\t右边界外不过的是");
            foreach (var p in Plates)
                sb.AppendLine($"{p.Name}\t{p.SolvedMm:0.###}\t"
                    + (p.SolvedFeasible
                        ? $"[{p.LoMm:0.###}, {p.HiMm:0.###}]{(p.OpenLo || p.OpenHi ? "（边界没探到）" : "")}\t{p.WidthMm:0.###}"
                        : $"解值自己就不过（{p.SolvedWhy}）\t—")
                    + $"\t{(p.LayerText.Length == 0 ? "**一个都没有**" : string.Join("／", p.LayerText))}"
                    + $"\t{(p.BlockedBelow.Length == 0 ? "—（没探到）" : p.BlockedBelow)}"
                    + $"\t{(p.BlockedAbove.Length == 0 ? "—（没探到）" : p.BlockedAbove)}");
            return sb.ToString();
        }

        /// <summary>表 + 结论 + 口径（安装报告与界面都印这一份，来源只有一个）。</summary>
        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"**每片舌保温的可行窗口**（{DesignName}）");
            sb.AppendLine($"  量法：逐片把舌保温在解值 ±{HalfWidthMm:0.###} mm 内按 {StepMm:0.###} mm 挪，其余片与其余旋钮一位不动，"
                        + $"每点跑一次整线、按交付判定过不过；网格 = {(NavigationMesh ? "导航网格（**只作快筛，不作判决**）" : "判决用的那张")}"
                        + (double.IsNaN(MeshFineMm) ? "" : $"（细区 {MeshFineMm:0.###} mm／半径 {MeshRadiusMm:0.#} mm）")
                        + $"；用时 {Seconds / 60:0.0} 分钟。");
            sb.AppendLine($"  现场包法：一层 {LayerThickMm:0.###} mm，不缠时是裸舌 {BareTabMm:0.###} mm（0 层也是一种做法）。");
            sb.Append(Table());
            sb.AppendLine("  结论：" + Verdict);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 现场做得出来的舌保温值：**裸舌** ∪ **一层厚的正整数倍**（出处见 <see cref="SolverOptions.QuantInsulMm"/> 的说明，
    /// 那里写着「合法值 = {裸舌 0.30} ∪ {0.5 的正整数倍}」）。落在 <paramref name="loMm"/>…<paramref name="hiMm"/> 内的那些，升序。
    /// ⚠ 只有这一份写法，窗口落档与门都从它读，不许在别处再数一遍格子。
    /// </summary>
    public static double[] LayerGridWithin(double loMm, double hiMm, SolverOptions o)
    {
        double bare = o.InsLoMm, q = PtOptimize.Core.Solver.KnobQuantum(o, PtOptimize.Core.Solver.Knob.Insul);
        var list = new List<double>();
        if (q <= 0) return list.ToArray();
        if (bare >= loMm - 1e-9 && bare <= hiMm + 1e-9) list.Add(bare);          // 0 层 = 裸舌，合法
        for (double t = Math.Ceiling(Math.Max(loMm, q) / q - 1e-9) * q; t <= hiMm + 1e-9; t += q)
        {
            double v = Math.Round(t, 9);
            if (v >= loMm - 1e-9 && !list.Any(x => Math.Abs(x - v) < 1e-9)) list.Add(v);
        }
        list.Sort();
        return list.ToArray();
    }

    /// <summary>
    /// 量一份设计逐片的舌保温可行窗口。
    /// </summary>
    /// <param name="design">要量的那份设计（求解器交出来的、或照表复原的）。本方法只读它，逐点用 <see cref="DesignSpec.Clone"/> 改一片。</param>
    /// <param name="baseIn">基准工艺参数（判据限值就从它来 —— 温差预算是工程师填的）。</param>
    /// <param name="o">扫描选项；null = 默认（判决网格、±1.0 mm、0.1 步）。</param>
    public static Result Measure(DesignSpec design, DesignInputs baseIn, Options? o = null,
                                 IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (design is null) throw new ArgumentNullException(nameof(design));
        if (baseIn is null) throw new ArgumentNullException(nameof(baseIn));
        o ??= new Options();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d0 = design.Clone();
        double layer = PtOptimize.Core.Solver.KnobQuantum(o.Solver, PtOptimize.Core.Solver.Knob.Insul);
        var res = new Result
        {
            DesignName = d0.Name,
            LayerThickMm = layer,
            BareTabMm = o.Solver.InsLoMm,
            HalfWidthMm = o.HalfWidthMm,
            StepMm = o.StepMm,
            NavigationMesh = o.UseNavigationMesh,
        };

        // 网格配方：判决那张（默认）或导航档。两支都走 Solver.ApplyCaseMesh，本类不抄配方。
        var meshOpt = new SolverOptions();
        if (!o.UseNavigationMesh)
        {
            var (fine, radius) = MeshVerify.RequiredMeshFor(d0);
            meshOpt.FineMm = fine; meshOpt.FineRadiusMm = radius;
            res.MeshFineMm = fine; res.MeshRadiusMm = radius;
        }

        Point RunOne(DesignSpec d, int j, double mm)
        {
            var pt = new Point { Mm = mm };
            var lc = d.BuildCase(baseIn);
            PtOptimize.Core.Solver.ApplyCaseMesh(lc, meshOpt);
            LineResult r;
            try { r = LineRunner.Run(lc, null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { pt.Feasible = null; pt.Why = $"{ex.GetType().Name}：{ex.Message}"; return pt; }
            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            pt.HotK = V(LineResult.Key.HotOverTc);
            pt.ColdK = V(LineResult.Key.ColdUnderTc);
            pt.NetFluxW = V(LineResult.Key.NetFlux);
            if (!r.Ok) { pt.Feasible = null; pt.Why = "整线解没解出来：" + r.Message; return pt; }
            if (!r.Converged) { pt.Feasible = null; pt.Why = $"外层耦合未收敛（剩余 {r.CoupleRemainK:0.000} K）"; return pt; }
            pt.Feasible = r.AllOk;
            if (!r.AllOk) pt.Why = WhyText(r);
            return pt;
        }

        var eval = o.Evaluate ?? ((DesignSpec d, int j, double mm) => RunOne(d, j, mm));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(o.Cap);

        int nPlate = d0.TabInsulMm.Length;
        var windows = new List<PlateWindow>();
        for (int j = 0; j < nPlate; j++)
        {
            double v0 = d0.TabInsulMm[j];
            int half = (int)Math.Round(o.HalfWidthMm / o.StepMm);
            var vals = Enumerable.Range(-half, 2 * half + 1).Select(k => Math.Round(v0 + k * o.StepMm, 6)).ToArray();
            var pts = new Point[vals.Length];
            var swJ = System.Diagnostics.Stopwatch.StartNew();
            progress?.Report($"可行窗口：第 {j + 1}/{nPlate} 片，{vals.Length} 点（{vals[0]:0.0}…{vals[^1]:0.0} mm），并发 {o.MaxDegreeOfParallelism}");
            int done = 0;
            try
            {
                Parallel.For(0, vals.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxDegreeOfParallelism), CancellationToken = cts.Token },
                    i =>
                    {
                        Point pt;
                        // 低于裸舌 = 根本不是一个做得出来的设计（不缠就是裸舌）⇒ 不解，标「下界外」。
                        // ⚠ 这一条在**注入的评估函数之外**判：注入进来的那份不必知道下界在哪。
                        if (vals[i] < o.Solver.InsLoMm - 1e-9)
                            pt = new Point { Feasible = null, Why = $"下界外（低于裸舌 {o.Solver.InsLoMm:0.###} mm）" };
                        else
                        {
                            var d = d0.Clone();
                            d.TabInsulMm[j] = vals[i];
                            pt = eval(d, j, vals[i]);
                        }
                        pt.Mm = vals[i];
                        pts[i] = pt;
                        int k = Interlocked.Increment(ref done);
                        progress?.Report($"可行窗口：第 {j + 1}/{nPlate} 片 {k}/{vals.Length} 点　{vals[i]:0.0} mm ⇒ "
                                       + (pt.Feasible is null ? "判不了" : pt.Feasible == true ? "过" : "不过"));
                    });
            }
            catch (OperationCanceledException)
            {
                res.Cut = true;
                progress?.Report($"⚠ 可行窗口：第 {j + 1} 片撞上时间闸被切断 —— 这一片不完整，不许当结果读。");
            }
            for (int i = 0; i < pts.Length; i++)
                pts[i] ??= new Point { Mm = vals[i], Feasible = null, Why = "没跑到（扫描被切断）" };

            var grid = LayerGridWithin(vals[0] - 1e-9, vals[^1] + 1e-9, o.Solver);
            foreach (var pt in pts)
            {
                pt.OnLayerGrid = grid.Any(g => Math.Abs(g - pt.Mm) < 1e-9);
                pt.IsSolved = Math.Abs(pt.Mm - v0) < 1e-12;
            }

            var w = new PlateWindow
            {
                Plate = j,
                Name = PlateName(d0, j),
                SolvedMm = v0,
                Points = pts,
            };
            int c = Array.FindIndex(pts, x => x.IsSolved);
            w.SolvedFeasible = c >= 0 && pts[c].Feasible == true;
            if (!w.SolvedFeasible)
            {
                w.SolvedWhy = c >= 0 ? (pts[c].Why.Length > 0 ? pts[c].Why : "判不了") : "没扫到解值那一点";
                w.OtherFeasible = FeasibleSegments(pts);
            }
            else
            {
                int a = c, b = c;
                while (a - 1 >= 0 && pts[a - 1].Feasible == true) a--;
                while (b + 1 < pts.Length && pts[b + 1].Feasible == true) b++;
                w.LoMm = pts[a].Mm; w.HiMm = pts[b].Mm; w.WidthMm = w.HiMm - w.LoMm;
                w.OpenLo = a == 0; w.OpenHi = b == pts.Length - 1;
                w.BlockedBelow = a > 0 ? pts[a - 1].Why : "";
                w.BlockedAbove = b < pts.Length - 1 ? pts[b + 1].Why : "";
                w.LayerMm = LayerGridWithin(w.LoMm, w.HiMm, o.Solver);
                w.LayerText = w.LayerMm.Select(m => $"{m:0.###}（{InsulationSearch.LayersText(m)}）").ToArray();
            }
            windows.Add(w);
            progress?.Report($"可行窗口：第 {j + 1}/{nPlate} 片完成，用时 {swJ.Elapsed.TotalMinutes:0.0} 分钟　"
                           + (w.SolvedFeasible
                               ? $"窗口 [{w.LoMm:0.###}, {w.HiMm:0.###}] 宽 {w.WidthMm:0.###} mm，可落 {(w.LayerMm.Length == 0 ? "**一个档都没有**" : string.Join("/", w.LayerMm.Select(m => m.ToString("0.###"))))}"
                               : $"解值那一点自己就不过（{w.SolvedWhy}）"));
        }

        res.Plates = windows.ToArray();
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    /// <summary>不过的是哪几条 —— 判据全名 + 实际／限值（界面不许出现判据代号）。</summary>
    private static string WhyText(LineResult r)
    {
        var bad = r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target && (!c.Ok || c.Undetermined)).ToArray();
        if (bad.Length == 0) return string.Join("／", r.Failed.Select(Criteria.Plain));
        return string.Join("／", bad.Select(c => $"{Criteria.Plain(c.Name)} "
            + (c.Undetermined ? "判不了" : $"{c.Actual:0.###}/{c.Limit:0.###}")));
    }

    private static string PlateName(DesignSpec d, int j)
    {
        int nSeg = d.SetpointC?.Length ?? 0;
        return j == 0 ? "入口" : nSeg > 0 && j >= nSeg ? "出口" : $"共用{j}";
    }

    private static (double Lo, double Hi)[] FeasibleSegments(Point[] pts)
    {
        var segs = new List<(double, double)>();
        int i = 0;
        while (i < pts.Length)
        {
            if (pts[i].Feasible == true)
            {
                int s = i;
                while (i + 1 < pts.Length && pts[i + 1].Feasible == true) i++;
                segs.Add((pts[s].Mm, pts[i].Mm));
            }
            i++;
        }
        return segs.ToArray();
    }
}
