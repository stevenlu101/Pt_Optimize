using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 S　**形状探针 ②：按「◇ 搜形状」的规则真跑一遍** —— 2026-09-18，Opus 5
//
//  ══ 为什么是这个形状、为什么在这里跑
//
//    「◇ 搜形状」的入口是 <c>LineDesignPage.SearchShapeAsync()</c> —— 一个 **private 的 UI 方法**，
//    它读的是页面控件（_discD/_tabW/_wall/_family/_tabTaper）、写的是 _out/_prog/_stages。
//    无头进程里**调不到它**。⇒ 本探针不是「再写一份搜形状」，而是：
//      · **决策规则**一律调生产件 <see cref="ShapeSearchPlan"/>（LiveDiscs／Neighbours／Worth／
//        Improved／Refine／StepExhausted／Key）—— 那个类的类头就写着它被抽成纯函数正是为了
//        「不必跑满几十分钟才验得到」；
//      · **每个形状怎么评估**一律与 EvalShape 同一行：`Solver.Solve(seed, baseIn,
//        new SolverOptions { AllowTabCuts, MaxRounds = 粗筛轮数, ScreenCoarseMm = 界面那一份 }, …)`；
//      · **并发**走生产件 <see cref="ShapeBatchEval"/>（lanes = 4，与 R36 那一版同一个数）；
//      · **形状 → 设计**的映射（盘半径／舌半宽／锥形／舌长 = 切点 + 压接段 + 自由段下界）
//        与 EvalShape 逐位相同。
//    ⚠ 照搬的只有「把这几件生产件串起来」这段胶水，**规则一条都没改**。
//      串法与 UI 那条路的差别（本探针不跑 ①～④ 那几步的不动点／二分／黄金分割，见下）**写在报告里**。
//
//  ══ 本探针跑哪几个形状（**跑前写死**），以及为什么只有两个
//
//    ★ 先量成本，再定跑几个（2026-09-18 实测，不是估的）：
//      第一版按生产第 1 轮网格排了 6 个形状、4 路并发跑了 45 分钟 ——
//      **一个点都没跑完**，跑在最前面的那个（盘35/舌35）才走到「第 1 轮 · 片1 舌保温」，
//      四片里的第二片。⇒ 单点是**小时级**（U 路端到端那一跑同样量到：W08 一次求解 213 分钟／93 次场解，
//      而它在第 2 轮就结构性停机了）。6 个点 × 两个档在本机跑不完，时间闸会把每一个都切在半路，
//      切在半路的点**什么都答不了**。
//      ⇒ 改成跑**生产那条路自己会先去解的那两个点**，让它们真的跑到底：
//        ① Rsafe = 第 1 轮网格里最大的盘径（生产「① 先在盘Ø{2·Rsafe} 解一次，拿到板厚」那一步），舌宽取最宽（fWide = 1.00）；
//        ② ⑥ 的**闭式反解**给出的盘径（生产「② 判据下界给出 盘半径 ≥ …⇒ 在这里再解一次」那一步）——
//           取**最保守**的那个 need：板厚顶到上界时 焊脚 = max(板厚上界, 壁厚)，need = 管孔半径 + 焊脚，
//           再按 LiveDiscs 自己的规则向上取到 0.5 mm。这不是我挑的数，是判据自己算的
//           （W08 那一跑印的就是「盘半径 30.000 ＜ 需要 31.800（管孔 25.800 + 焊脚 6.000）」）。
//    ⚠ **不跑**生产那条路的 ③二分／④黄金分割／⑥剩余舌宽比例／⑦邻域探索，也不跑第 1 轮网格的其余点。
//      本探针的职责是「在跑得起的预算内，把这两个点的答案真的交出来」，**不假装跑完了整条搜索**。
//      剩下几步没跑这件事写在结论里，不许省。
//    ⚠ 每个点的求解器轨迹**当场进环形缓冲**：时间闸切断时也印得出「跑到第几轮、抬了哪根旋钮、缺口还剩多少」——
//      长跑要放探针，每轮要出结果，不能只印输入。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//
//    · 「可行」= <c>SolverResult.Feasible</c>（= 终局复核那一次 <c>LineResult.AllOk</c>，与交付判定同一份）。
//    · 解出来的舌保温必须落在 {裸舌 <c>InsLoMm</c>} ∪ {图纸格的整数倍} 上 —— 不落格的**标「疑似 L 路 09-18
//      那个二分退回病」并不采信**（该病 U 树没有修，见交接记忆）。
//    · 时间闸到点即切断，报「被时间闸切断」，**不许装作跑完了**；已经解完的点照常落盘。
//    · 一个可行点都没有 ⇒ 报**最接近**的那个（按「还差几条判据、每条差多少」排），并逐条列缺口。
//    · 铂重只从可行点上引用；不可行点的 MassG 照印但标「不可行」。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48SShapeSearchProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48SShapeSearchProbeTests(ITestOutputHelper o) { _o = o; }

    /// <summary>整批的时间闸（跑前写死）。到点切断，已解完的点保留，切在半路的点印轨迹尾巴。</summary>
    private static readonly TimeSpan BatchCap = TimeSpan.FromHours(3.0);

    /// <summary>并发路数 —— 生产那一版是 4（UI/LineDesignPage.cs EvalShapesBatch `lanes: 4`）；
    /// 本探针只有 2 个候选，且两个档（W08/W06）同机并跑 ⇒ 每边 2 路，合计 4 路，与生产同量级。</summary>
    private const int Lanes = 2;

    /// <summary>每个点留最后多少行求解器轨迹（时间闸切断时靠它说话）。</summary>
    private const int TraceTail = 60;

    /// <summary>粗筛轮数 —— 界面 <c>LineDesignPage.SearchScreenRounds</c> 的那个数。</summary>
    private const int ScreenRounds = 16;

    /// <summary>界面第 1 轮网格（<c>LineDesignPage.SearchDiscs</c>）。</summary>
    private static readonly double[] SearchDiscs = { 25, 30, 35 };

    /// <summary>界面舌宽比例（<c>LineDesignPage.SearchWFrac</c>）。</summary>
    private static readonly double[] SearchWFrac = { 1.00, 0.75 };

    /// <summary>界面粗筛底层网格（<c>LineDesignPage.SearchScreenCoarseMm</c> = 0 ⇒ 不放粗）。</summary>
    private const double ScreenCoarseMm = 0;

    private sealed record Row(double R, double HalfW, bool Taper, double TabLen,
                              bool Done, bool Feasible, bool HitBound, double MassG,
                              string StopWhy, string Message,
                              double Cold, double Hot, double Flux, double SecJ, double Wrap,
                              string Insul, string Thick, string OffGrid, int Solves, double Minutes);

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 搜形状_第一轮网格(string which)
    {
        var seed0 = which == "W08" ? DesignSpec.W08.Clone()
                  : which == "W06" ? DesignSpec.W06.Clone()
                  : throw new ArgumentException(which);
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_形状探针_搜形状第一轮网格_{which}_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_搜形状_{which}_{stamp}_进行中.log");
        var probe = new Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        var sbLock = new object();
        void W(string s = "") { lock (sbLock) sb.AppendLine(s); }
        void Flush() { lock (sbLock) File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); }

        var o0 = new SolverOptions();
        double qIns = Solver.KnobQuantum(o0, Solver.Knob.Insul);

        // ── ⑥ 的闭式下界（标量版，与生产 EvalShape 同一行：孔半径 = 壁 + 25，厚度取板料下限）
        double wall = seed0.WallMm;
        double minDiscAll = GeometryScreen.MinDiscRadiusMm(
            holeRadiusMm: wall + 25.0, thickMm: p.WeldMinThicknessMm, wallMm: wall);
        double[] discs = ShapeSearchPlan.LiveDiscs(SearchDiscs, minDiscAll);
        double rSafe = discs[^1];                                   // 生产「① 先在盘Ø{2·Rsafe} 解一次」那一步
        // ⑥ 的闭式反解，取**最保守**的那个 need：板厚顶到上界时焊脚最长（= max(板厚上界, 壁厚)）
        double holeR = wall + 25.0;                                  // 与生产 EvalShape 同一行
        double weldWorst = Math.Max(o0.ThickHiMm, wall);
        double needWorst = GeometryScreen.MinDiscRadiusMm(holeRadiusMm: holeR, thickMm: o0.ThickHiMm, wallMm: wall);
        double rNeed = ShapeSearchPlan.LiveDiscs(new[] { 0.0 }, needWorst)[0];   // 借 LiveDiscs 自己那条「向上取 0.5 格」的规则

        W($"R48 S　形状探针 ②：**按「◇ 搜形状」的规则跑它自己会先解的那两个点**（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W("");
        W("═══════ 这一跑与界面那条路的关系（先说清楚，免得被读成「搜形状跑完了」）═══════");
        W("「◇ 搜形状」的入口 LineDesignPage.SearchShapeAsync 是 **private 的 UI 方法**（读页面控件、写输出框），无头进程调不到。");
        W("本探针**不重写规则**：决策全调 ShapeSearchPlan（LiveDiscs/Worth/Key），盘径下界调 GeometryScreen.MinDiscRadiusMm（= 判据 ⑥ 的闭式反解），");
        W($"每个形状的评估与 EvalShape 同一行 —— Solver.Solve(seed, 输入, {{AllowTabCuts=false, MaxRounds={ScreenRounds}, ScreenCoarseMm={ScreenCoarseMm:0.#}}})；");
        W($"并发走生产件 ShapeBatchEval.RunAsync(lanes={Lanes})；形状→设计的映射与 EvalShape 逐位相同。");
        W("**没跑**的是生产那条路的 ③ 二分／④ 黄金分割／⑥ 剩余舌宽比例／⑦ 邻域探索，以及第 1 轮网格的其余点 —— ");
        W("理由是**量出来的**，不是省事：本探针第一版按第 1 轮网格排了 6 个点、4 路并发跑了 45 分钟，**一个点都没跑完**");
        W("（最前面那个才走到「第 1 轮 · 片1 舌保温」，四片里的第二片）；U 路端到端同样量到 W08 一次求解 213 分钟／93 次场解。");
        W("⇒ 与其把 6 个点全切在半路（切在半路什么都答不了），不如把生产**会先去解的那两个点**真的跑到底。");
        W("");
        W("═══════ 跑哪几个形状（跑前写死）═══════");
        W($"判据「圆盘盖得住管孔＋焊脚」闭式下界（标量版，管壁 {wall:0.0}，厚度取板料下限）：盘半径 ≥ {minDiscAll:0.000} mm");
        W($"第 1 轮网格 = ShapeSearchPlan.LiveDiscs(界面 {{{string.Join(", ", SearchDiscs.Select(v => v.ToString("0")))}}}, 下界) = "
          + $"{{{string.Join(", ", discs.Select(v => v.ToString("0.###")))}}}（低于下界的那一点被抬到下界上，不是删掉）");
        W($"⇒ **① Rsafe** = 网格里最大的盘径 = {rSafe:0.###}，舌宽取最宽比例 fWide = {SearchWFrac.Max():0.00} ⇒ 舌半宽 {rSafe * SearchWFrac.Max():0.###}");
        W($"⇒ **② ⑥ 的闭式反解（最保守）**：板厚顶到上界 {o0.ThickHiMm:0.00} mm 时焊脚 = max(板厚, 壁厚) = {weldWorst:0.00} mm，"
          + $"need = 管孔半径 {holeR:0.###} + 焊脚 = **{needWorst:0.###} mm**，按 LiveDiscs 自己的规则向上取到 0.5 格 ⇒ 盘半径 {rNeed:0.###}，舌半宽同取最宽 {rNeed:0.###}");
        W($"　（W08 那一跑印的就是这句：「盘半径 30.000 ＜ 需要 31.800（管孔 25.800 + 焊脚 6.000）」——"
          + " 出处 deliverable/R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-18_005505.txt 第 20 行。）");
        W($"舌长 = √(盘半径² − 舌半宽²) + 压接段 {seed0.ClampLengthMm:0} + 自由段下界 {GeometryScreen.FreeTabMinDefaultMm:0}（规则，不是旋钮）");
        W($"⚠ 舌半宽 = 盘半径 时切点落在 x = 0，舌长 = 压接段 + 自由段 = {seed0.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm:0}（两个内置档本来就是这个形状）。");
        W($"锥形舌边：按内置档当前值 {(seed0.TabTaper ? "锥形" : "平行")}（生产第 1 轮也只按一个锥形值跑，翻转锥形只在邻域探索里出现）");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「可行」= SolverResult.Feasible（= 终局复核那一次 LineResult.AllOk，与交付判定同一份）。");
        W($"　② 解出来的舌保温必须落在 {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{qIns:0.###} 的整数倍}} 上；不落格的标「疑似 L 路 09-18 二分退回病」**不采信**。");
        W($"　③ 时间闸 {BatchCap.TotalHours:0.#} h：到点切断，报「被时间闸切断」，不许装作跑完了；已解完的点照常落盘。");
        W("　④ 一个可行点都没有 ⇒ 报最接近的那个并逐条列缺口；铂重只从可行点上引用。");
        W($"温差预算：冷侧 {p.ColdUnderTcAllowK:0.#} K／热侧 {p.HotOverTcAllowK:0.#} K（参数表默认）。"
          + $"圆盘保温 {seed0.FlangeInsulMm:0.#} mm；接合区缠绕上限 {WrapLimits.JointZoneMaxMm:0.#} mm；舌保温图纸格 {qIns:0.###} mm。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        // ── 候选（去重用生产件 ShapeSearchPlan.Worth/Key）────────────────────
        var seen = new HashSet<string>();
        double fWide = SearchWFrac.Max();
        var raw = new List<(double R, double HalfW, bool Taper)>
        {
            (rSafe, rSafe * fWide, seed0.TabTaper),     // ① 生产的 Rsafe 那一点
            (rNeed, rNeed * fWide, seed0.TabTaper),     // ② ⑥ 闭式反解（最保守）那一点
        };
        var cands = ShapeSearchPlan.Worth(raw, seen, minDiscAll);

        W($"═══════ 候选（去重后 {cands.Count} 个；去重键 = ShapeSearchPlan.Key）═══════");
        foreach (var c in cands)
            W($"　盘半径 {c.R:0.###}／舌半宽 {c.HalfW:0.###}／{(c.Taper ? "锥形" : "平行")}"
              + $"／舌长 {Math.Sqrt(Math.Max(0, c.R * c.R - c.HalfW * c.HalfW)) + seed0.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm:0.0}");
        W("");
        Flush();

        var specs = cands.Select(c =>
        {
            var s = seed0.Clone();
            s.DiscRadiusMm = c.R;
            s.TabHalfWidthMm = c.HalfW;
            s.TabTaper = c.Taper;
            s.TabLengthMm = Math.Sqrt(Math.Max(0, c.R * c.R - c.HalfW * c.HalfW))
                          + s.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            return s;
        }).ToList();

        using var cts = new CancellationTokenSource(BatchCap);
        int finished = 0;
        var batchSw = Stopwatch.StartNew();
        // ★ 每个点一条环形缓冲：时间闸切断时也印得出「跑到第几轮、抬了哪根旋钮、缺口还剩多少」
        var tails = specs.Select(_ => new TraceTail_()).ToArray();
        var tailOf = new Dictionary<DesignSpec, TraceTail_>();
        for (int i = 0; i < specs.Count; i++) tailOf[specs[i]] = tails[i];

        var outcome = ShapeBatchEval.RunAsync(specs, (spec, tok) =>
        {
            var sw = Stopwatch.StartNew();
            probe.Report($"── 开跑 盘半径 {spec.DiscRadiusMm:0.###}／舌半宽 {spec.TabHalfWidthMm:0.###}");
            var tail = tailOf[spec];
            var inner = new FanOut(
                new ThrottledProgress(probe, 60, $"     · 盘{spec.DiscRadiusMm:0.#}/舌{spec.TabHalfWidthMm:0.#} "),
                tail);
            SolverResult r;
            try
            {
                r = Solver.Solve(spec, p, new SolverOptions
                {
                    AllowTabCuts = false,                 // 界面「不挖舌孔」那一族（内置档一律走这族）
                    MaxRounds = ScreenRounds,
                    ScreenCoarseMm = ScreenCoarseMm,
                }, inner, tok);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                r = new SolverResult { Design = spec, Message = $"{ex.GetType().Name}：{ex.Message}", Feasible = false };
            }
            sw.Stop();
            int n = Interlocked.Increment(ref finished);
            // ★ 每个点算完当场落盘一行（长跑要放探针；方向错要立刻看得见）
            probe.Report($"── 完成 {n}/{specs.Count} 盘半径 {spec.DiscRadiusMm:0.###}／舌半宽 {spec.TabHalfWidthMm:0.###}："
                       + $"{(r.Feasible ? "可行" : "不可行")}　{(double.IsNaN(r.MassG) ? "—" : r.MassG.ToString("0") + " g")}"
                       + $"　场解 {r.Solves} 次　{sw.Elapsed.TotalMinutes:0.0} 分钟　停因 {r.StopWhy}");
            W($"[当场落盘 {DateTime.Now:HH:mm:ss}] 盘半径 {spec.DiscRadiusMm:0.###}／舌半宽 {spec.TabHalfWidthMm:0.###}："
              + $"{(r.Feasible ? "**可行**" : "不可行")}　铂重 {(double.IsNaN(r.MassG) ? "—" : r.MassG.ToString("0"))} g"
              + $"　管根 {F3(Val(r.Best, LineResult.Key.ColdUnderTc))}／最热铂 {F3(Val(r.Best, LineResult.Key.HotOverTc))}"
              + $"／净流入 {F3(Val(r.Best, LineResult.Key.NetFlux))}"
              + $"　场解 {r.Solves} 次　{sw.Elapsed.TotalMinutes:0.0} 分钟　停因：{r.StopWhy}");
            Flush();
            return (r, sw.Elapsed);
        }, Lanes, cts.Token).GetAwaiter().GetResult();

        batchSw.Stop();
        W("");
        W($"═══════ 批次结束：{finished}/{specs.Count} 个点跑完，耗时 {batchSw.Elapsed.TotalHours:0.00} h"
          + (outcome.Cancelled ? "　★ **被跑前写死的时间闸切断**（没跑完的点下面标「时间闸」）" : "") + " ═══════");
        W("");

        // ── 收尾：按候选原顺序装表 ──
        var rows = new List<Row>();
        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (!outcome.Done[i])
            {
                rows.Add(new Row(spec.DiscRadiusMm, spec.TabHalfWidthMm, spec.TabTaper, spec.TabLengthMm,
                                 false, false, false, double.NaN, "时间闸切断（这个点没跑完）", "", double.NaN, double.NaN,
                                 double.NaN, double.NaN, double.NaN, "—", "—", "", 0, double.NaN));
                W($"── 盘半径 {spec.DiscRadiusMm:0.###}／舌半宽 {spec.TabHalfWidthMm:0.###}：**被时间闸切断，没跑完** ——"
                  + $" 它跑到哪了（求解器轨迹最后 {TraceTail} 行，**不是结论**，只说明进度）：");
                foreach (string s in tails[i].Lines()) W("　　" + s);
                W("");
                continue;
            }
            var (r, el) = outcome.Results[i];
            var d = r.Design ?? spec;
            var off = new List<string>();
            for (int j = 0; j < d.TabInsulMm.Length; j++)
            {
                double v = d.TabInsulMm[j];
                bool bare = Math.Abs(v - o0.InsLoMm) < 1e-9;
                double lay = v / qIns;
                if (!bare && Math.Abs(lay - Math.Round(lay)) > 1e-9) off.Add($"片{j}={v:R}");
            }
            rows.Add(new Row(d.DiscRadiusMm, d.TabHalfWidthMm, d.TabTaper, d.TabLengthMm,
                             true, r.Feasible, r.HitBound, r.MassG, r.StopWhy, r.Message,
                             Val(r.Best, LineResult.Key.ColdUnderTc), Val(r.Best, LineResult.Key.HotOverTc),
                             Val(r.Best, LineResult.Key.NetFlux), Val(r.Best, LineResult.Key.SectionJ),
                             Val(r.Best, LineResult.Key.WrapTurns),
                             string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0"))),
                             string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00"))),
                             string.Join("、", off), r.Solves, el.TotalMinutes));
        }

        W("═══════ 第 1 轮网格结果表 ═══════");
        W("盘半径\t舌半宽\t舌边\t舌长\t可行?\t铂重 g\t管根低于热偶读数\t最热铂高出热偶读数\t管孔净流入 W\t法兰截面 J\t舌保温 mm\t板厚 mm\t场解\t分钟\t停因");
        foreach (var r in rows)
            W($"{r.R:0.###}\t{r.HalfW:0.###}\t{(r.Taper ? "锥形" : "平行")}\t{r.TabLen:0.0}\t"
              + $"{(!r.Done ? "时间闸" : r.Feasible ? "**可行**" : "不可行")}\t{F0(r.MassG)}\t"
              + $"{F3(r.Cold)}\t{F3(r.Hot)}\t{F3(r.Flux)}\t{F3(r.SecJ)}\t{r.Insul}\t{r.Thick}\t{r.Solves}\t{F1(r.Minutes)}\t{r.StopWhy}");
        W("");

        // ── 格子门：舌保温落不落在包法层上 ──
        W($"═══════ 舌保温落格检查（合法集合 = {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{qIns:0.###} 的整数倍}}）═══════");
        var badGrid = rows.Where(r => r.Done && r.OffGrid.Length > 0).ToList();
        W(badGrid.Count == 0
          ? "　跑完的点里，解出来的舌保温**都落在格子上**（裸舌或整数层）。"
          : "　★ **有点不落格**：" + string.Join("；", badGrid.Select(r => $"盘{r.R:0.#}/舌{r.HalfW:0.#} → {r.OffGrid}"))
            + "　⇒ 这几个点标「**疑似 L 路 09-18 那个二分退回病**（中点判不了时退回被抬过的 lo；U 树没有这个修）」，**本报告不采信它们的舌保温值**。");
        W("");

        // ── 结论 ──
        var feas = rows.Where(r => r.Done && r.Feasible).OrderBy(r => r.MassG).ToList();
        W("═══════ 一句话 ═══════");
        if (feas.Count > 0)
        {
            var best = feas[0];
            double refMass = which == "W08" ? R48LW08NavDesign.NavTotalMassG : R48LW06FineDesign.FineTotalMassG;
            W($"{which}：第 1 轮网格上**有可行形状** {feas.Count} 个，最轻的是 盘半径 {best.R:0.###}／舌半宽 {best.HalfW:0.###}"
              + $"／舌长 {best.TabLen:0.0}，铂重 {F0(best.MassG)} g，舌保温 {best.Insul} mm，板厚 {best.Thick} mm。");
            W($"与参照（{(which == "W08" ? "W08 4245 g" : "W06 3379 g")}）的铂重差：{D0(best.MassG - refMass)} g"
              + "　⚠ 那份参照是 **0.1 格、圆盘保温 20 mm** 世界里的数（出处见 R48LW08NavDesign/R48LW06FineDesign 的 Source），"
              + "与本跑**不是同一次运行、也不是同一份代码**，这个差只作对照。");
            W("⚠ 这些点是**粗筛**（MaxRounds " + ScreenRounds + "、第二遍细网格没做）⇒ 只在导航网格上成立，**不可交付**；"
              + "要交付得在判决网格上重解 + 三关 + 逐片舌保温可行窗口。");
        }
        else
        {
            var done = rows.Where(r => r.Done).ToList();
            W($"{which}：第 1 轮网格上**一个可行形状都没有**（跑完 {done.Count}/{rows.Count} 个点）。");
            if (done.Count > 0)
            {
                W("── 最接近的点（按缺口逐条列；缺口 = 判据值与限值之差，正数 = 还差这么多）");
                foreach (var r in done.OrderBy(r2 => Gap(r2, p)))
                {
                    var gaps = new List<string>();
                    if (!double.IsNaN(r.Cold) && r.Cold > p.ColdUnderTcAllowK) gaps.Add($"管根低于热偶读数 还差 {r.Cold - p.ColdUnderTcAllowK:0.###} K");
                    if (!double.IsNaN(r.Hot) && r.Hot > p.HotOverTcAllowK) gaps.Add($"最热铂高出热偶读数 还差 {r.Hot - p.HotOverTcAllowK:0.###} K");
                    if (!double.IsNaN(r.Flux) && r.Flux <= 0) gaps.Add($"管孔净流入 还差 {-r.Flux:0.###} W（现在是倒灌进管子）");
                    if (!double.IsNaN(r.SecJ) && r.SecJ > seed0.JCheckAPerMm2) gaps.Add($"法兰截面 J 还差 {r.SecJ - seed0.JCheckAPerMm2:0.###}");
                    if (!double.IsNaN(r.Wrap) && r.Wrap > WrapLimits.JointZoneMaxMm) gaps.Add($"接合区保温 还差 {r.Wrap - WrapLimits.JointZoneMaxMm:0.###} mm");
                    W($"· 盘半径 {r.R:0.###}／舌半宽 {r.HalfW:0.###}：{(gaps.Count == 0 ? "判据值读不到（见停因）" : string.Join("；", gaps))}"
                      + $"　停因：{r.StopWhy}{(r.HitBound ? "　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）——再算一次是同一句话，不是「没搜到」" : "")}");
                }
            }
        }
        W("");
        W("═══════ 搜形状**穷尽了什么**（口径，不许含糊）═══════");
        W($"· 穷尽了：本跑排的 {rows.Count} 个形状里**跑到底**的那 {rows.Count(r => r.Done)} 个 ——"
          + $" 每个都从约束盒下角把**法兰侧九根旋钮**求了一遍根（MaxRounds {ScreenRounds}，与界面粗筛同一个数）。");
        W($"· **没有穷尽**：第 1 轮网格的其余点（盘半径 {string.Join("/", discs.Select(v => v.ToString("0.###")))} × 舌宽比例 "
          + $"{string.Join("/", SearchWFrac.Select(v => v.ToString("0.00")))} 里没排进本跑的那几个）；");
        W("　生产那条路的 ③ 可行区二分／④ 区间内找最轻（黄金分割）／⑥ 剩余舌宽比例／⑦ 邻域探索（含翻转锥形），");
        W($"　以及步长收缩到 {ShapeSearchPlan.MinDiscStepMm:0.###} mm 的那一层 —— 都**没跑**。");
        W("　⇒ **不许说「盘径与舌半宽这条路走完了」**，只能说「生产会先解的那两个点是这个结果」。");
        W("· 也没有穷尽：挖舌孔那一族（本跑固定 AllowTabCuts = false，与内置档的解法族一致）、锥形舌边（只按内置档当前值跑）。");
        W("· ⚠ 成本口径：本跑与另一个档（W08/W06）**同机并跑**，每边 2 路 ⇒ 合计 4 路。"
          + "表里的「分钟」带着这一档机器争用；**判据值不带**（同输入同输出的纯函数，见 ShapeBatchEval 类头）。");
        W("");
        Finish(sb, sbLock, file, total, which);

        Assert.True(rows.Count(r => r.Done) > 0,
            $"{which}：一个形状都没跑完 —— 那不是实测（报告已落盘：{file}）");
    }

    // ══════════════════════════════════════════════════════════════════════
    /// <summary>缺口合计（只用来排「最接近」，不进结论的数）。</summary>
    private static double Gap(Row r, DesignInputs p)
    {
        double g = 0;
        if (!double.IsNaN(r.Cold) && r.Cold > p.ColdUnderTcAllowK) g += r.Cold - p.ColdUnderTcAllowK;
        if (!double.IsNaN(r.Hot) && r.Hot > p.HotOverTcAllowK) g += r.Hot - p.HotOverTcAllowK;
        if (!double.IsNaN(r.Flux) && r.Flux <= 0) g += -r.Flux;
        return double.IsNaN(g) ? double.MaxValue : g;
    }

    /// <summary>把一条进度同时送给两个接收方（一个节流到活页、一个进环形缓冲）。</summary>
    private sealed class FanOut : IProgress<string>
    {
        private readonly IProgress<string>[] _to;
        public FanOut(params IProgress<string>[] to) { _to = to; }
        public void Report(string value) { foreach (var t in _to) t.Report(value); }
    }

    /// <summary>求解器轨迹的环形缓冲 —— 只留最后 <see cref="TraceTail"/> 行。</summary>
    private sealed class TraceTail_ : IProgress<string>
    {
        private readonly Queue<string> _q = new();
        private readonly object _lock = new();
        public void Report(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (_lock)
            {
                _q.Enqueue($"{DateTime.Now:HH:mm:ss}  {value.Split('\n')[0]}");
                while (_q.Count > TraceTail) _q.Dequeue();
            }
        }
        public string[] Lines() { lock (_lock) return _q.ToArray(); }
    }

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));
    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string F0(double v) => double.IsNaN(v) ? "—" : v.ToString("0");
    private static string F1(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0");
    private static string F3(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000");
    private static string D0(double v) => double.IsNaN(v) ? "—" : v.ToString("+0;-0");

    private void Finish(StringBuilder sb, object sbLock, string file, Stopwatch total, string which)
    {
        total.Stop();
        lock (sbLock)
        {
            sb.AppendLine($"── 总耗时 {total.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
            sb.AppendLine("出处：求解 = Solver.Solve（Core/Solver.cs）；决策规则 = ShapeSearchPlan；并发 = ShapeBatchEval.RunAsync；"
                        + "网格配方 = Solver.ApplyCaseMesh；判据 ⑥ 下界 = GeometryScreen.MinDiscRadiusMm；舌保温图纸格 = SolverOptions.QuantInsulMm。");
            sb.AppendLine($"本文件里每一个数都来自这一次运行（{which}，同一进程、同一份代码）。");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            _o.WriteLine(sb.ToString());
        }
    }

    /// <summary>进度活页：每行带时刻与自上一行的耗时，当场落盘（长跑要放探针）。</summary>
    private sealed class Probe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _last;
        public Probe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [+{now - _last,7:0.0}s 累计{now / 60,7:0.0}min]  {value}\r\n");
                _last = now;
            }
        }
    }
}
