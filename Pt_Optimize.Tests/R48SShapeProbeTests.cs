using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 S　**形状探针 ①：盘半径与舌半宽的方向实测** —— 2026-09-18，Opus 5
//
//  ══ 为什么跑这一支（背景，主会话 09-18 核过）
//
//    圆盘保温按现场缠绕上限压到 10 mm（WrapLimits.JointZoneMaxMm）之后：
//      · W08 那份复原设计的「管根低于热偶读数」读 19～20 K（限值 5）；
//      · 求解器（法兰侧九根旋钮）与 U 树端到端两路都自报**下一根杠杆是盘径与舌半宽**
//        （Core/Solver.cs 的三处停机出口、Core/FlangeAutoSizer.cs 的「下一根是增宽过流带」、Core/NextAction.cs 的指路句）——
//        那两根不在求解器手里，归「◇ 搜形状」。
//
//    ⇒ 在花几小时跑搜形状之前，**先用十几次整线解把方向量出来**：
//      盘半径往上走、舌半宽往下走，到底是让管根变好还是变坏、代价是什么。
//
//  ══ 这一版（v2）比第一版多做的三件事（第一版的输出文件已删，理由见下）
//
//    ① **判词按跑前写死的规矩③ 判**：第一版那句「一句话」只看了管根一列就说
//       「方向有用 ⇒ 往下跑」，而同一份文件的规矩③ 写着「管根变好而最热铂或管孔净流入
//       翻过限值 ⇒ 记代价，不算净改善」—— 那四个点的最热铂全部 65～161 K（限值 5）、
//       净流入全部为负（须 >0）。**是我这份仪器的判词与它自己的规矩矛盾**，不是数错。
//       数留着（本版逐点重跑，同一进程），判词改成真的走规矩③。
//    ② **步长太粗，跨过了根**：第一版盘半径一步 4 mm，管孔净流入从 +6.26 W（R30）
//       直接翻到 −17.29 W（R34）—— 中间必有过零点，而那正是要找的那一段（记忆：
//       「精度要在根附近验」）。本版在 31/32/33 各加一点；舌半宽同理按搜形状自己的
//       步长减半再减半（FracStep → /2 → /4）各加一点。
//    ③ **网格半径这一维要有对照**：本仓生产配方 MeshVerify.RequiredMeshFor 让细区半径
//       跟着几何走（盘半径一变，网格半径也变）。§0.-10 实测过：同一设计只改这一维，
//       管孔净流入曾差 9.8 W 且符号相反。⇒ 本版加两点「形状不动、只把网格半径换成
//       别的点用的那个」，把网格的份额与几何的份额分开。**先验前提再谈因果。**
//
//  ══ 判读门槛（**跑前写死在这里，跑完不挪**）
//
//    · 「管根变好」= 管根低于热偶读数**下降 ≥ 1.0 K**。
//      依据：§0.-10 实测收紧后同一设计的复现摆幅 0.015 K、耦合剩余误差估计 0.02～0.9 K；
//      1.0 K 比这一档噪声大一个量级。小于 1.0 K 的位移一律记「在噪声量级，不算方向」。
//    · 规矩③（**净改善**的定义）：管根变好 **且** 最热铂高出热偶读数 ≤ 限值 **且**
//      管孔净流入 > 0 **且** 法兰截面 J ≤ 限值。任一条翻过去 ⇒ 记「代价」，**不算净改善**。
//    · 判不了（整线没解出来／外层耦合未收敛）**一律不当过**，照印。
//    · 过零点只报**区间**（在哪两点之间变号），不外推、不插值出一个没解过的数。
//
//  ══ 口径声明（每个数指回出处）
//
//    · 基准设计 = R48LW08NavDesign.Build()（照 09-17 093013 输出表复原的 4245 g 那一份）。
//      ⚠ 它的舌保温 5.1/2.3/3.6/10.4 是 **0.1 格**那一跑的值，**不落 0.5 格**；
//        本支只拿它当「方向探针的基准点」，**不作为可交付设计报出**。
//    · 舌长**不是自由旋钮**：按搜形状自己的规则算 = 切点 + 压接段 + 自由段下界
//      （UI/LineDesignPage.cs EvalShape 那一行）。盘径一动舌长就跟着动，这是规则不是我加的。
//    · 舌片厚与板厚下角**不是自由旋钮**：形状一动就按 I/(J·最窄有效宽) 闭式重定 ——
//      调生产件 Solver.ApplySectionFloor（不手抄配方）。
//    · 网格 = 生产的**导航档配方** Solver.ApplyCaseMesh(lc, {FineMm = 0, FineRadiusMm = …})，
//      与 Solver.Solve 第一遍那一行（navOpt）同一份。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48SShapeDirectionProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48SShapeDirectionProbeTests(ITestOutputHelper o) { _o = o; }

    /// <summary>「管根变好」的门槛 K（跑前写死；比 §0.-10 实测的复现摆幅 0.015 K 大一个量级以上）。</summary>
    private const double ImproveK = 1.0;

    private sealed record Shot(string Tag, string Family, double R, double HalfW, double TabLen,
                               double ReqRadius, double GridRadius,
                               double Cold, double Hot, double Flux, double TubeJ, double SecJ, double Wrap,
                               double Mass, double MinDiscNeed, bool Converged, bool Ok, bool AllOk,
                               string Failed, double RemainK, double Secs, string Thick, string Tongue);

    [Fact]
    public void 方向实测_盘半径与舌半宽_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_形状探针_方向实测_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_形状方向_W08_{stamp}_进行中.log");
        var probe = new Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 S　形状探针 ①：**盘半径与舌半宽的方向实测**（W08，导航网格）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("⚠ 基准设计的舌保温 5.1/2.3/3.6/10.4 mm 出自 **0.1 格**那一跑，**不落 0.5 格**（= 包法每层 "
          + $"{InsulationSearch.LayerMm:0.###} mm）⇒ 本支只拿它当方向探针的基准点，**不作为可交付设计**。");
        W("⚠ 本文件是这支探针的 **v2**。v1（同日 08:47:53 那一份）已删：它的「一句话」只看管根一列就判");
        W("　「方向有用」，与同一份文件里跑前写死的规矩③ 自相矛盾（那四个点的最热铂 65～161 K／限值 5、");
        W("　净流入全为负／须 >0）。**是判词与自己的规矩打架，不是数错**；本版把那些点原样重跑在同一进程里，");
        W("　并按规矩③ 重判，另补了「盘径细档」与「网格半径对照」两组（同一件事不留两份带开跑时刻的报告）。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W($"　① 「管根变好」= 管根低于热偶读数下降 ≥ {ImproveK:0.0} K（§0.-10 实测复现摆幅 0.015 K、耦合剩余误差 0.02～0.9 K ⇒ 门槛大一个量级）。");
        W("　② **净改善** = 管根变好 **且** 最热铂 ≤ 限值 **且** 管孔净流入 > 0 **且** 法兰截面 J ≤ 限值。");
        W("　③ 任一条翻过去 ⇒ 记「代价」，**不算净改善**（这一条 v1 写了但没照着判，见上）。");
        W("　④ 判不了（没解出来／外层耦合未收敛）一律不当过。");
        W("　⑤ 过零点只报**区间**（在哪两点之间变号），不插值、不外推出一个没解过的数。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：生产导航档配方 Solver.ApplyCaseMesh(lc, FineMm=0, FineRadiusMm=…)（= Solver.Solve 第一遍 navOpt 同一行）。");
        W("　缺省取 MeshVerify.RequiredMeshFor(本形状).RadiusMm —— **细区半径随几何走**，所以另设「网格半径对照」组把这一维单独量出来。");
        W($"舌长 = √(盘半径² − 舌半宽²) + 压接段 {d0.ClampLengthMm:0} + 自由段下界 {GeometryScreen.FreeTabMinDefaultMm:0}"
          + "（搜形状自己的规则，UI/LineDesignPage.cs EvalShape）—— **舌长不是自由旋钮**。");
        W("舌片厚与板厚下角：形状一动就调生产件 Solver.ApplySectionFloor 闭式重定（不手抄配方）。");
        W($"温差预算：冷侧 {p.ColdUnderTcAllowK:0.#} K／热侧 {p.HotOverTcAllowK:0.#} K（参数表默认，DesignInputs）。"
          + $"圆盘保温 {d0.FlangeInsulMm:0.#} mm；接合区缠绕上限 {WrapLimits.JointZoneMaxMm:0.#} mm。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        // ── 候选（跑前写死）────────────────────────────────────────────────
        //   盘径：v1 的 34/38 原样重跑 + 31/32/33 三个细档（v1 在 30→34 之间跨过了净流入的变号）
        //   舌宽：按搜形状自己的比例步长 FracStep 及其两次减半（Refine 的口径）
        //   网格：形状不动、只换细区半径 —— 把「网格半径」这一维从「几何」里分出来
        double fs = ShapeSearchPlan.FracStep;                                  // 0.125（搜形状自己的步长，不是我拍的）
        double fs2 = ShapeSearchPlan.Refine(fs), fs4 = ShapeSearchPlan.Refine(fs2);
        var cands = new List<(string Tag, string Fam, double R, double HalfW, double GridR)>
        {
            ("基准　盘半径 30／舌半宽 30", "基准", 30.0, 30.0, double.NaN),
            ("盘半径 31（舌半宽不动 30）", "盘径", 31.0, 30.0, double.NaN),
            ("盘半径 32（舌半宽不动 30）", "盘径", 32.0, 30.0, double.NaN),
            ("盘半径 33（舌半宽不动 30）", "盘径", 33.0, 30.0, double.NaN),
            ("盘半径 34（舌半宽不动 30）", "盘径", 34.0, 30.0, double.NaN),
            ("盘半径 38（舌半宽不动 30）", "盘径", 38.0, 30.0, double.NaN),
            ($"舌半宽 {30 * (1 - fs4):0.###}（比例 {1 - fs4:0.#####}，盘半径不动 30）", "舌宽", 30.0, 30.0 * (1 - fs4), double.NaN),
            ($"舌半宽 {30 * (1 - fs2):0.###}（比例 {1 - fs2:0.####}，盘半径不动 30）", "舌宽", 30.0, 30.0 * (1 - fs2), double.NaN),
            ($"舌半宽 {30 * (1 - fs):0.###}（比例 {1 - fs:0.###}，盘半径不动 30）", "舌宽", 30.0, 30.0 * (1 - fs), double.NaN),
            ($"舌半宽 {30 * (1 - 2 * fs):0.###}（比例 {1 - 2 * fs:0.###}，盘半径不动 30）", "舌宽", 30.0, 30.0 * (1 - 2 * fs), double.NaN),
            ("网格对照：形状同基准，细区半径换成 64.6（= 盘半径 34 那点用的）", "网格", 30.0, 30.0, 64.6),
            ("网格对照：形状同基准，细区半径换成 67.2（= 盘半径 38 那点用的）", "网格", 30.0, 30.0, 67.2),
        };

        var shots = new List<Shot>();
        foreach (var (tag, fam, R, hw, gridR) in cands)
        {
            probe.Report($"── 开始 {tag}");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R;
            d.TabHalfWidthMm = hw;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;

            // 形状一动，舌片厚与板厚下角闭式重定 —— 调生产件，不手抄
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            double useRadius = double.IsNaN(gridR) ? reqRadius : gridR;
            var navOpt = new SolverOptions { FineMm = 0, FineRadiusMm = useRadius };
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, navOpt);

            // 判据 ⑥ 的闭式下界（盘半径至少要多大）—— 与判据同一份实现
            double floorD = d.DiscFloorMm(p);
            var plates = Enumerable.Range(0, d.TabThickMm.Length).Select(j => d.Plate(j, floorD)).ToArray();
            double need = GeometryScreen.MinDiscRadiusMm(plates);

            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();

            var shot = new Shot(tag, fam, R, hw, d.TabLengthMm, reqRadius, useRadius,
                                Val(r, LineResult.Key.ColdUnderTc), Val(r, LineResult.Key.HotOverTc),
                                Val(r, LineResult.Key.NetFlux), Val(r, LineResult.Key.TubeJ),
                                Val(r, LineResult.Key.SectionJ), Val(r, LineResult.Key.WrapTurns),
                                r.Ok ? r.TotalMassG : double.NaN, need,
                                r.Converged, r.Ok, r.AllOk,
                                r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                                r.CoupleRemainK, sw.Elapsed.TotalSeconds,
                                string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00"))),
                                string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00"))));
            shots.Add(shot);
            probe.Report($"── {tag} 结束：管根 {F3(shot.Cold)}／最热铂 {F3(shot.Hot)}／净流入 {F3(shot.Flux)}，耗时 {sw.Elapsed.TotalSeconds:0} s");

            // 逐轮落盘（长跑要放探针；中途切断也留得住）
            W($"── {tag}");
            W($"　几何：盘半径 {R:0.###}／舌半宽 {hw:0.###}／舌长 {d.TabLengthMm:0.0}（切点 {Math.Sqrt(Math.Max(0, R * R - hw * hw)):0.0}）"
              + $"　板厚 {shot.Thick}　舌片厚 {shot.Tongue}");
            W($"　网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.0} mm／细区半径 {lc.MeshFineRadiusMm:0.0} mm"
              + (double.IsNaN(gridR) ? "（本形状 RequiredMeshFor）" : $"（**人为换成 {gridR:0.0}**；本形状 RequiredMeshFor 本来是 {reqRadius:0.0}）")
              + $"　单元 {r.MeshCells}");
            W($"　判据「圆盘盖得住管孔＋焊脚」闭式下界：盘半径要 ≥ {F3(need)} mm，现在 {R:0.###} ⇒ {(R >= need - 1e-9 ? "盖得住" : "**盖不住**")}");
            W($"　结论：{(!r.Ok ? "整线没解出来 ⇒ 判不了（" + r.Message + "）" : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）⇒ 判不了" : r.AllOk ? "全判据通过" : "不过：" + shot.Failed)}");
            W($"　管根低于热偶读数 {F3(shot.Cold)}／{p.ColdUnderTcAllowK:0.#}　最热铂高出热偶读数 {F3(shot.Hot)}／{p.HotOverTcAllowK:0.#}"
              + $"　管孔净流入 {F3(shot.Flux)} W（须 >0）　法兰截面 J {F3(shot.SecJ)}　管 J {F3(shot.TubeJ)}"
              + $"　接合区保温 {F3(shot.Wrap)}／{WrapLimits.JointZoneMaxMm:0.#}　铂重 {F(shot.Mass)} g　耗时 {sw.Elapsed.TotalSeconds:0} s");
            W("");
            Flush();
        }

        var b = shots[0];

        // ══════════════ 网格半径对照（先验前提）══════════════
        W("═══════ ①　先验前提：细区半径这一维自己值多少 ═══════");
        W("形状**完全不动**（盘半径 30／舌半宽 30／舌长 140），只把网格细区半径换成别的点用的那个。");
        W("细区半径\t管根低于热偶读数\t最热铂高出热偶读数\t管孔净流入 W\t铂重 g");
        foreach (var s in shots.Where(s2 => s2.Family is "基准" or "网格"))
            W($"{s.GridRadius:0.0}\t{F3(s.Cold)}\t{F3(s.Hot)}\t{F3(s.Flux)}\t{F(s.Mass)}");
        var gridShots = shots.Where(s2 => s2.Family is "基准" or "网格").ToList();
        double gridSpanCold = Span(gridShots.Select(s2 => s2.Cold));
        double gridSpanHot = Span(gridShots.Select(s2 => s2.Hot));
        double gridSpanFlux = Span(gridShots.Select(s2 => s2.Flux));
        W($"⇒ 只换网格半径（59.0 → 64.6 → 67.2），三条判据各摆 管根 {F3(gridSpanCold)} K／最热铂 {F3(gridSpanHot)} K／净流入 {F3(gridSpanFlux)} W。");
        W("　下面「几何」两组的位移要**减去这一档**才是几何自己的贡献。§0.-10 记过同一个病（那一次净流入差 9.8 W 且符号相反）。");
        W("");

        // ══════════════ 方向表 ══════════════
        W("═══════ ②　方向实测表（同一次运行、同一份代码、同一基准设计；只动形状）═══════");
        W("形状\t组\t盘半径\t舌半宽\t舌长\t细区半径\t管根低于热偶读数\tΔ管根\t最热铂高出热偶读数\t管孔净流入 W\t法兰截面 J\t铂重 g\t耗时 s\t判词");
        foreach (var s in shots)
            W($"{s.Tag}\t{s.Family}\t{s.R:0.###}\t{s.HalfW:0.###}\t{s.TabLen:0.0}\t{s.GridRadius:0.0}\t{F3(s.Cold)}\t"
              + $"{(ReferenceEquals(s, b) ? "—" : D3(s.Cold - b.Cold))}\t{F3(s.Hot)}\t{F3(s.Flux)}\t{F3(s.SecJ)}\t{F(s.Mass)}\t{s.Secs:0}\t"
              + $"{(!s.Ok ? "判不了（没解出来）" : !s.Converged ? "判不了（未收敛）" : s.AllOk ? "全过" : s.Failed)}");
        W("");
        W("（Δ管根 为负 = 管根离热偶读数更近 = 这一条变好；正 = 变坏。门槛 ±" + ImproveK.ToString("0.0") + " K。）");
        W("");

        // ══════════════ 逐方向判词（规矩③）══════════════
        W("═══════ ③　逐方向判词（按跑前写死的规矩②③）═══════");
        foreach (var s in shots.Where(s2 => s2.Family is "盘径" or "舌宽"))
        {
            double dCold = s.Cold - b.Cold;
            bool coldBetter = !double.IsNaN(dCold) && dCold <= -ImproveK;
            bool hotOk = !double.IsNaN(s.Hot) && s.Hot <= p.HotOverTcAllowK + 1e-9;
            bool fluxOk = !double.IsNaN(s.Flux) && s.Flux > 0;
            bool secOk = double.IsNaN(s.SecJ) || s.SecJ <= d0.JCheckAPerMm2 + 1e-9;
            bool net = coldBetter && hotOk && fluxOk && secOk;
            var broke = new List<string>();
            if (!hotOk) broke.Add($"最热铂 {F3(s.Hot)} > {p.HotOverTcAllowK:0.#}");
            if (!fluxOk) broke.Add($"管孔净流入 {F3(s.Flux)} ≤ 0（倒灌进管子 = 烧断的方向）");
            if (!secOk) broke.Add($"法兰截面 J {F3(s.SecJ)} > {d0.JCheckAPerMm2:0.#}");
            W($"· {s.Tag}：管根 {D3(dCold)} K ⇒ "
              + (double.IsNaN(dCold) ? "**判不了**"
                 : coldBetter ? "这一条变好"
                 : dCold >= ImproveK ? "这一条变坏"
                 : $"在噪声量级（< {ImproveK:0.0} K），不算方向"));
            W($"　规矩③：{(net ? "**净改善**（四条都没翻）" : broke.Count == 0 ? "不是净改善（管根这一条自己就没变好）" : "**不算净改善** —— 翻过去的是：" + string.Join("；", broke))}");
            W($"　代价：最热铂 {D3(s.Hot - b.Hot)} K、管孔净流入 {D3(s.Flux - b.Flux)} W、铂重 {D0(s.Mass - b.Mass)} g、舌长 {s.TabLen - b.TabLen:+0.0;-0.0} mm、板厚 {b.Thick} → {s.Thick}。");
        }
        W("");

        // ══════════════ 变号区间（规矩⑤：只报区间）══════════════
        W("═══════ ④　变号区间（只报区间，不插值）═══════");
        foreach (var fam in new[] { "盘径", "舌宽" })
        {
            var seq = new List<Shot> { b };
            seq.AddRange(shots.Where(s2 => s2.Family == fam)
                              .OrderBy(s2 => fam == "盘径" ? s2.R : -s2.HalfW));
            W($"── {fam}（按{(fam == "盘径" ? "盘半径递增" : "舌半宽递减")}排）");
            W($"{(fam == "盘径" ? "盘半径" : "舌半宽")}\t管根低于热偶读数\t最热铂高出热偶读数\t管孔净流入 W");
            foreach (var s in seq)
                W($"{(fam == "盘径" ? s.R : s.HalfW):0.###}\t{F3(s.Cold)}\t{F3(s.Hot)}\t{F3(s.Flux)}");
            W("　" + Cross(seq, fam, s2 => s2.Flux, 0.0, "管孔净流入", "W", "（>0 是安全侧）"));
            W("　" + Cross(seq, fam, s2 => s2.Cold, p.ColdUnderTcAllowK, "管根低于热偶读数", "K", $"（限值 {p.ColdUnderTcAllowK:0.#}，越过即由不过转过）"));
            W("　" + Cross(seq, fam, s2 => s2.Hot, p.HotOverTcAllowK, "最热铂高出热偶读数", "K", $"（限值 {p.HotOverTcAllowK:0.#}，越过即由过转不过）"));
            W("");
        }

        // ══════════════ 结论 ══════════════
        var moved = shots.Where(s2 => s2.Family is "盘径" or "舌宽").ToList();
        bool anyNet = moved.Any(s2 =>
        {
            double dc = s2.Cold - b.Cold;
            return !double.IsNaN(dc) && dc <= -ImproveK
                && s2.Hot <= p.HotOverTcAllowK + 1e-9 && s2.Flux > 0
                && (double.IsNaN(s2.SecJ) || s2.SecJ <= d0.JCheckAPerMm2 + 1e-9);
        });
        bool anyAllOk = moved.Any(s2 => s2.AllOk);

        W("═══════ ⑤　一句话 ═══════");
        W($"基准（盘半径 30／舌半宽 30）：管根低于热偶读数 {F3(b.Cold)}／{p.ColdUnderTcAllowK:0.#}（缺口 {F3(b.Cold - p.ColdUnderTcAllowK)} K）、"
          + $"最热铂 {F3(b.Hot)}／{p.HotOverTcAllowK:0.#}（余 {F3(p.HotOverTcAllowK - b.Hot)} K）、管孔净流入 {F3(b.Flux)} W（须 >0）。");
        W(anyNet
          ? "扫过的这些形状里**有净改善点**（管根变好且三条硬线都没翻）⇒ 值得往下跑「◇ 搜形状」。"
          : "扫过的这些形状里**一个净改善点都没有**（规矩③）：管根确实被拉好了，但代价是最热铂与管孔净流入一起翻过硬线。");
        W(anyAllOk ? "其中有形状**全判据通过**（见表判词栏）。" : "扫过的形状**没有一个全判据通过**。");
        W("");
        W("⚠ 一起读的四条：");
        W("　(a) 细区半径这一维自己的份额已在 ① 量出来（见那三行），几何组的位移要减去它。");
        W("　(b) 基准设计的舌保温不落 0.5 格 ⇒ 本表的绝对值只作方向判读，不作交付判定。");
        W("　(c) 都是**导航网格**（细区 2.0 mm）上的数，不是判决网格。");
        W("　(d) 本表**没有动法兰侧那九根旋钮**（板厚、环倍率、舌保温…）—— 它们由求解器在每个形状上重新求根。");
        W("　　　所以这张表回答的是「形状往哪个方向走，热学上会发生什么」，**不是**「这个形状最好能做到多少」。");
        W("　　　后一个问题只有搜形状（每个形状跑一次 Solver.Solve）答得了。");
        W("");
        Finish(sb, file, total);

        // 断言只守「每一步都有结果」，方向不利是**结果**不是失败
        Assert.Equal(cands.Count, shots.Count);
        Assert.True(shots.All(s => s.Secs > 0), "有候选一次整线解都没跑 —— 那不是实测");
    }

    // ══════════════════════════════════════════════════════════════════════
    /// <summary>只报「在哪两点之间越过了这条线」；一个都没越过就照说，**不插值、不外推**。</summary>
    private static string Cross(List<Shot> seq, string fam, Func<Shot, double> f, double line,
                                string what, string unit, string note)
    {
        string key(Shot s) => (fam == "盘径" ? s.R : s.HalfW).ToString("0.###");
        for (int i = 1; i < seq.Count; i++)
        {
            double a = f(seq[i - 1]), c = f(seq[i]);
            if (double.IsNaN(a) || double.IsNaN(c)) continue;
            if ((a - line) * (c - line) < 0)
                return $"{what}{note}：在 {key(seq[i - 1])} 与 {key(seq[i])} **之间越过 {line:0.###} {unit}**"
                     + $"（{a:0.###} → {c:0.###}）—— 只报区间，真值在这两点之间，没解过就不给数。";
        }
        return $"{what}{note}：扫到的点里**没有越过 {line:0.###} {unit}**。";
    }

    private static double Span(IEnumerable<double> vs)
    {
        var a = vs.Where(v => !double.IsNaN(v)).ToArray();
        return a.Length == 0 ? double.NaN : a.Max() - a.Min();
    }

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));
    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string F(double v) => double.IsNaN(v) ? "—" : v.ToString("0");
    private static string F3(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000");
    private static string D3(double v) => double.IsNaN(v) ? "—" : v.ToString("+0.000;-0.000");
    private static string D0(double v) => double.IsNaN(v) ? "—" : v.ToString("+0;-0");

    private void Finish(StringBuilder sb, string file, Stopwatch total)
    {
        total.Stop();
        sb.AppendLine($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（全仓唯一一份）；"
                    + "网格无关口径 = MeshVerify.RequiredMeshFor；舌片厚／板厚下角 = Solver.ApplySectionFloor；"
                    + "舌宽比例步长 = ShapeSearchPlan.FracStep 与 Refine；判据 ⑥ 下界 = GeometryScreen.MinDiscRadiusMm。");
        sb.AppendLine("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
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
