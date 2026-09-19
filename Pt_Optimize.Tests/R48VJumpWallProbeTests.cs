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
//  R48 V　**跳变诊断 ⑤：跳变在判决网格上挪到哪里去了 —— 以及 §0.-11 那堵墙（盘30）站不站得住** —— 2026-09-18，Opus 5
//
//  ══ 为什么要它（诊断 ②③ 跑出来之后才写得出这一支的问题）
//
//    导航网格（细区 2.0 mm）上：盘 30 → 31 平滑，盘 31 → 31.1 一步跳（最热铂 0.903 → 28.909）。
//    判决网格（细区 1.0 mm）上：盘 31.0 已经读 **最热铂 27.429／管根 0.178／净流入 −7.191**
//    —— 与导航网格同一点差 26.5 K。而 §0.-11 那堵墙（盘 30、圆盘保温 10 mm）在两张网格上只差 0.87 K
//    （导航 19.875 vs 判决 19.004，出处 deliverable/R48_U_冷侧预算演示_*）。
//    ⇒ 那么**跳变在判决网格上是消失了，还是挪到了 30 → 31 之间**？这两件事的结论完全不同：
//      消失 ⇒ 纯数值；挪位 ⇒ 真有一处陡变，只是位置随网格走。
//    ⇒ 本支在**判决网格**上把 30.0 → 31.0 按 0.25 步补齐，并把盘 30 那一点（= 那堵墙）在同一进程里重量。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//
//    · 判决网格上 30.0 → 31.0 之间某一 0.25 档的「最热铂」变化 > 其余档中位数的 5 倍 ⇒ 判「跳变挪到了这一档」。
//    · 判决网格上 30.0 → 31.0 各档变化都在同一量级、且总变化与 31 → 32 那一档同量级 ⇒ 判「没有跳变，是陡但连续」。
//    · 盘 30 的「管根低于热偶读数」在判决网格上仍 ≥ 15 K ⇒ §0.-11 那堵墙**不受本次诊断影响**；
//      落到 < 15 K ⇒ 那堵墙要重报。门槛 15 K 是跑前定的（墙报的是 19 K，掉一半以上才算动摇）。
//    · 判不了（没解出来／未收敛）一律不当过。
//
//  ⚠ 生产代码一行未动。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VJumpWallProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpWallProbeTests(ITestOutputHelper o) { _o = o; }

    /// <summary>那堵墙「还站着」的门槛 K（跑前写死）：墙报 19 K，掉到 15 K 以下才算动摇。</summary>
    private const double WallStandsK = 15.0;

    [Fact]
    public void 跳变诊断_判决网格上的30到31与那堵墙_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_判决网格30到31与那堵墙_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_墙_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑤：**判决网格（细区 1.0 mm）上 30.0 → 31.0 补齐，并重量 §0.-11 那堵墙**（W08）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}（舌保温不落 0.5 格 ⇒ 只作诊断基准点，不作可交付设计）");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 判决网格上 30.0 → 31.0 某一 0.25 档的最热铂变化 > 其余档中位数的 5 倍 ⇒ 跳变挪到了这一档。");
        W("　② 各档同量级 ⇒ 陡但连续，不是跳变。");
        W($"　③ 盘 30 的「管根低于热偶读数」在判决网格上 ≥ {WallStandsK:0} K ⇒ §0.-11 那堵墙不受影响；< {WallStandsK:0} ⇒ 要重报。");
        W("　④ 判不了一律不当过。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：Solver.ApplyCaseMesh(FineMm=1.0, FineRadiusMm=MeshVerify.RequiredMeshFor(本形状).RadiusMm) = MeshAdapt.RefineWholeMesh 整张一起缩。");
        W("对照出处（导航网格，同一份复原设计）：deliverable/R48_形状探针_方向实测_W08_本次开跑于2026-09-18_090213.txt；");
        W("　§0.-11 那堵墙：deliverable/R48_U_冷侧预算演示_导航网格_W08_…044247.txt（管根 19.875）与 …判决网格…061106.txt（管根 19.004）。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var pts = new List<(string Tag, double R, double HalfW)>
        {
            ("盘半径 30.00／舌半宽 30（= §0.-11 那堵墙那一点）", 30.00, 30.0),
            ("盘半径 30.25／舌半宽 30", 30.25, 30.0),
            ("盘半径 30.50／舌半宽 30", 30.50, 30.0),
            ("盘半径 30.75／舌半宽 30", 30.75, 30.0),
            ("盘半径 31.00／舌半宽 30", 31.00, 30.0),
            ($"舌半宽 {30 * (1 - ShapeSearchPlan.Refine(ShapeSearchPlan.Refine(ShapeSearchPlan.FracStep))):0.###}（盘半径不动 30）",
             30.00, 30.0 * (1 - ShapeSearchPlan.Refine(ShapeSearchPlan.Refine(ShapeSearchPlan.FracStep)))),
        };

        var res = new List<(string Tag, double R, double HalfW, double Cold, double Hot, double Flux, double SecJ,
                            double Mass, bool Ok, bool Conv, bool AllOk, string Failed, double Remain, int Cells, double Secs,
                            string HotWhere, string HotWhat)>();

        foreach (var (tag, R, hw) in pts)
        {
            probe.Report($"── 开始 {tag}");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = hw;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 1.0, FineRadiusMm = reqRadius });

            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();

            var hotC = R48VJumpLineProbeTests.Get(r, LineResult.Key.HotOverTc);
            res.Add((tag, R, hw,
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc),
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc),
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux),
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.SectionJ),
                     r.Ok ? r.TotalMassG : double.NaN, r.Ok, r.Converged, r.AllOk,
                     r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                     r.CoupleRemainK, r.MeshCells, sw.Elapsed.TotalSeconds,
                     hotC?.Where ?? "—", R48VJumpLineProbeTests.HottestWhatOf(r)));
            var last = res[^1];
            probe.Report($"── {tag} 结束：管根 {last.Cold:0.000}／最热铂 {last.Hot:0.000}／净流入 {last.Flux:0.000}，{sw.Elapsed.TotalSeconds:0} s");

            W($"════ {tag}（判决网格）");
            W($"　几何：盘半径 {R:0.000}／舌半宽 {hw:0.000}／舌长 {d.TabLengthMm:0.00}　板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000")))}"
              + $"　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000")))}　圆盘板厚工艺下界 {d.DiscFloorMm(p):0.0000}");
            W($"　网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.00} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm　片0单元 {r.MeshCells}");
            W($"　结论：{(!r.Ok ? "整线没解出来 ⇒ 判不了（" + r.Message + "）" : !r.Converged ? $"外层耦合未收敛（剩余 {r.CoupleRemainK:0.000} K）⇒ 判不了" : r.AllOk ? "全判据通过" : "不过：" + last.Failed)}");
            W($"　管根低于热偶读数 {last.Cold:0.000}　最热铂高出热偶读数 {last.Hot:0.000}（位置 {last.HotWhere}，最热的是{last.HotWhat}）"
              + $"　管孔净流入 {last.Flux:0.000} W　法兰截面 J {last.SecJ:0.000}　铂重 {last.Mass:0} g　耦合剩余 {last.Remain:0.000} K／容差 {r.CoupleTolKUsed:0.000} K　耗时 {sw.Elapsed.TotalSeconds:0} s");
            if (r.Ok && r.Flanges is { Length: > 0 })
            {
                W("　── 逐片定位");
                for (int j = 0; j < r.Flanges.Length; j++)
                {
                    var t = ThermocoupleBasis.At(r, j);
                    var f = r.Flanges[j];
                    W($"　　片{j} {t.Name}｜基准 {t.RefC:0.00}｜最热的是{t.HottestWhat} {t.HottestC:0.0}"
                      + $"｜圆盘峰 {f.TDiscMaxC:0.0} @r={f.DiscMaxRMm:0.00} (x={f.DiscMaxXMm:0.0}, z={f.DiscMaxZMm:0.0}) J={f.DiscMaxJAPerMm2:0.000} t={f.DiscMaxThickMm:0.000}"
                      + $"｜舌片区峰 {f.TTabMaxC:0.0} @r={f.TabMaxRMm:0.00} (x={f.TabMaxXMm:0.0}, z={f.TabMaxZMm:0.0}) J={f.TabMaxJAPerMm2:0.000} t={f.TabMaxThickMm:0.000}"
                      + $"｜管根 {t.RootEnds}｜热侧 {t.HotK:0.000}｜冷侧 {t.ColdK:0.000}"
                      + $"｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}｜表面散热 {f.QLossW:0.000}｜进铜排 {f.QClampW:0.000}｜片温 {f.TMinC:0.0}～{f.TMaxC:0.0}");
                }
            }
            W("");
            Flush();
        }

        W("═══════ ①　判决网格上的盘径细档（30.00 → 31.00，步 0.25）═══════");
        W("盘半径\t管根低于热偶读数\tΔ\t最热铂高出热偶读数\tΔ\t管孔净流入 W\tΔ\t铂重 g\t片0单元\t耦合剩余 K\t判词");
        var disc = res.Where(x => Math.Abs(x.HalfW - 30.0) < 1e-9).OrderBy(x => x.R).ToList();
        for (int i = 0; i < disc.Count; i++)
        {
            var a = disc[i];
            W($"{a.R:0.00}\t{a.Cold:0.000}\t{(i == 0 ? "—" : (a.Cold - disc[i - 1].Cold).ToString("+0.000;−0.000"))}\t"
              + $"{a.Hot:0.000}\t{(i == 0 ? "—" : (a.Hot - disc[i - 1].Hot).ToString("+0.000;−0.000"))}\t"
              + $"{a.Flux:0.000}\t{(i == 0 ? "—" : (a.Flux - disc[i - 1].Flux).ToString("+0.000;−0.000"))}\t"
              + $"{a.Mass:0}\t{a.Cells}\t{a.Remain:0.000}\t{(!a.Ok ? "判不了（没解出来）" : !a.Conv ? "判不了（未收敛）" : a.AllOk ? "全过" : a.Failed)}");
        }
        W("");
        W("═══════ ②　判读（按跑前写死的门槛）═══════");
        if (disc.Count >= 3)
        {
            var ds = new List<(double At, double D)>();
            for (int i = 1; i < disc.Count; i++) ds.Add((disc[i].R, disc[i].Hot - disc[i - 1].Hot));
            var abs = ds.Select(t => Math.Abs(t.D)).OrderBy(v => v).ToArray();
            double med = abs[abs.Length / 2];
            var mx = ds.OrderByDescending(t => Math.Abs(t.D)).First();
            W($"　最热铂：中位单步 {med:0.000} K，最大单步 {mx.D:+0.000;−0.000} K 落在 {mx.At - 0.25:0.00} → {mx.At:0.00}，倍率 "
              + $"{(med > 1e-12 ? (Math.Abs(mx.D) / med).ToString("0.0") : "—")} ⇒ "
              + $"{(med > 1e-12 && Math.Abs(mx.D) / med > 5 ? "**跳变挪到了这一档**" : "**各档同量级 ⇒ 陡但连续，判决网格上没有跳变**")}");
        }
        var wall = disc.FirstOrDefault(x => Math.Abs(x.R - 30.0) < 1e-9);
        if (wall.Tag is not null)
            W($"　§0.-11 那堵墙（盘 30，判决网格）：管根低于热偶读数 {wall.Cold:0.000} K ⇒ "
              + $"{(!wall.Ok || !wall.Conv ? "**判不了，不当过**" : wall.Cold >= WallStandsK ? "**那堵墙站得住**（≥ " + WallStandsK.ToString("0") + " K），本次诊断不影响它" : "**那堵墙要重报**（< " + WallStandsK.ToString("0") + " K）")}");
        var tabRow = res.FirstOrDefault(x => Math.Abs(x.HalfW - 30.0) > 1e-9);
        if (tabRow.Tag is not null && wall.Tag is not null)
            W($"　舌半宽那一跳（判决网格，盘 30 不动）：舌半宽 30 → {tabRow.HalfW:0.###}，"
              + $"管根 {wall.Cold:0.000} → {tabRow.Cold:0.000}（{tabRow.Cold - wall.Cold:+0.000;−0.000}）、"
              + $"最热铂 {wall.Hot:0.000} → {tabRow.Hot:0.000}（{tabRow.Hot - wall.Hot:+0.000;−0.000}）、"
              + $"净流入 {wall.Flux:0.000} → {tabRow.Flux:0.000}（{tabRow.Flux - wall.Flux:+0.000;−0.000}）。");
        W("");

        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（= MeshAdapt.RefineWholeMesh）；逐片读数 = ThermocoupleBasis.At。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(pts.Count, res.Count);
    }
}
