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
//  R48 V　**跳变诊断 ③：把网格加密两档，看 31 → 32 那一跳还在不在** —— 2026-09-18，Opus 5
//
//  ══ 判读（**跑前写死，跑完不挪**）
//
//    在盘半径 31.0 与 32.0 两点各解一次整线，网格三档：
//      A 导航（细区 2.0 mm，生产求根用的那一档）
//      B 判决（细区 1.0 mm，MeshAdapt.RefineWholeMesh 整张一起缩 —— 生产复核用的配方）
//      C 再加密一档（细区 0.5 mm，同一配方）
//
//    跳幅 Δ(判据) = 该网格上「盘 32 的值 − 盘 31 的值」。
//      · 加密后 **跳幅消失或缩到导航档的一半以下** ⇒ 判「**数值跳变**」（并写明是哪一档网格上量的）。
//      · 加密后 **跳幅基本不动**（三档都在导航档的 0.5～2 倍之间）⇒ 判「**模型分支**」，
//        分支由诊断 ④（源码比对）指名。
//      · 两者都不是（例如跳幅反而更大、或加密解不出来）⇒ 如实写「**没找到**」，不许硬塞一个结论。
//    · 判不了（没解出来／外层耦合未收敛）一律不当过，照印。
//    · 网格越细越慢：本支**按 A→B→C 的顺序跑，每点算完立刻落盘**，中途切断也留得住前面几档。
//
//  ⚠ 生产代码一行未动。本文件只读生产件、只写 deliverable 输出。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VJumpMeshProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpMeshProbeTests(ITestOutputHelper o) { _o = o; }

    private sealed record Cell(string Mesh, double H, double R, double Cold, double Hot, double Flux,
                               double SecJ, double Mass, bool Ok, bool Conv, bool AllOk, string Failed,
                               double RemainK, int Cells, double Secs, string HotWhere, string HotWhat,
                               string Detail);

    [Fact]
    public void 跳变诊断_三档网格上的盘31与盘32_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_三档网格复测_W08盘31与盘32_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_网格_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ③：**盘半径 31.0 与 32.0，三档网格上各解一次整线**（W08）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}（舌保温不落 0.5 格 ⇒ 只作诊断基准点）");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　跳幅 Δ = 同一张网格上「盘 32 的值 − 盘 31 的值」。");
        W("　· 加密后跳幅 **消失或 ≤ 导航档的一半** ⇒ 判「数值跳变」。");
        W("　· 三档跳幅都在导航档的 0.5～2 倍之间 ⇒ 判「模型分支」（分支由源码比对指名）。");
        W("　· 都不是 ⇒ 如实写「没找到」。");
        W("　· 判不了（没解出来／未收敛）一律不当过。");
        W("");
        W("═══════ 口径 ═══════");
        W("A 导航：Solver.ApplyCaseMesh(FineMm=0, FineRadiusMm=RequiredMeshFor) —— 细区 2.0 mm，生产求根那一档。");
        W("B 判决：Solver.ApplyCaseMesh(FineMm=1.0, …) = MeshAdapt.RefineWholeMesh 整张一起缩（细粗比恒定，生产复核配方）。");
        W("C 再加密：同一配方 FineMm=0.5。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var plan = new List<(string Mesh, double H)>
        {
            ("A 导航", 0.0), ("B 判决", 1.0), ("C 再加密", 0.5),
        };
        var pts = new[] { 31.0, 32.0 };
        var cells = new List<Cell>();

        foreach (var (meshName, h) in plan)
            foreach (double R in pts)
            {
                probe.Report($"── 开始 {meshName}（细区 {(h <= 0 ? 2.0 : h):0.0}）盘半径 {R:0.0}");
                var sw = Stopwatch.StartNew();
                var d = d0.Clone();
                d.DiscRadiusMm = R;
                d.TabHalfWidthMm = 30.0;
                d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - 900.0)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
                var dummy = new SolverResult { Design = d };
                Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

                var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = h, FineRadiusMm = reqRadius });

                LineResult r;
                try { r = LineRunner.Run(lc, probe); }
                catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
                sw.Stop();

                var hotC = R48VJumpLineProbeTests.Get(r, LineResult.Key.HotOverTc);
                var detail = new StringBuilder();
                if (r.Ok && r.Flanges is { Length: > 0 })
                    for (int j = 0; j < r.Flanges.Length; j++)
                    {
                        var t = ThermocoupleBasis.At(r, j);
                        var f = r.Flanges[j];
                        detail.Append($"　　片{j} {t.Name}｜基准 {t.RefC:0.00}｜最热的是{t.HottestWhat} {t.HottestC:0.0}"
                                    + $"｜圆盘峰 {f.TDiscMaxC:0.0} @r={f.DiscMaxRMm:0.00} J={f.DiscMaxJAPerMm2:0.000} t={f.DiscMaxThickMm:0.000}"
                                    + $"｜舌片区峰 {f.TTabMaxC:0.0} @r={f.TabMaxRMm:0.00} J={f.TabMaxJAPerMm2:0.000} t={f.TabMaxThickMm:0.000}"
                                    + $"｜管根 {t.RootEnds}｜热侧 {t.HotK:0.000}｜冷侧 {t.ColdK:0.000}"
                                    + $"｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}｜表面散热 {f.QLossW:0.000}｜进铜排 {f.QClampW:0.000}\r\n");
                    }

                var c = new Cell(meshName, h <= 0 ? 2.0 : h, R,
                                 R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc),
                                 R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc),
                                 R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux),
                                 R48VJumpLineProbeTests.Val(r, LineResult.Key.SectionJ),
                                 r.Ok ? r.TotalMassG : double.NaN, r.Ok, r.Converged, r.AllOk,
                                 r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                                 r.CoupleRemainK, r.MeshCells, sw.Elapsed.TotalSeconds,
                                 hotC?.Where ?? "—", R48VJumpLineProbeTests.HottestWhatOf(r), detail.ToString());
                cells.Add(c);
                probe.Report($"── {meshName} 盘 {R:0.0} 结束：管根 {c.Cold:0.000}／最热铂 {c.Hot:0.000}／净流入 {c.Flux:0.000}，{sw.Elapsed.TotalSeconds:0} s");

                W($"════ {meshName}（细区 {c.H:0.000} mm／粗区 {lc.MeshCoarseMm:0.00} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm）　盘半径 {R:0.0}");
                W($"　板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000")))}"
                  + $"　舌长 {d.TabLengthMm:0.00}　片0单元 {r.MeshCells}");
                W($"　结论：{(!r.Ok ? "整线没解出来 ⇒ 判不了（" + r.Message + "）" : !r.Converged ? $"外层耦合未收敛（剩余 {r.CoupleRemainK:0.000} K）⇒ 判不了" : r.AllOk ? "全判据通过" : "不过：" + c.Failed)}");
                W($"　管根低于热偶读数 {c.Cold:0.000}　最热铂高出热偶读数 {c.Hot:0.000}（位置 {c.HotWhere}，最热的是{c.HotWhat}）"
                  + $"　管孔净流入 {c.Flux:0.000} W　法兰截面 J {c.SecJ:0.000}　铂重 {c.Mass:0} g　耦合剩余 {c.RemainK:0.000} K　耗时 {sw.Elapsed.TotalSeconds:0} s");
                if (detail.Length > 0) { W("　── 逐片定位"); W(detail.ToString().TrimEnd()); }
                W("");
                Flush();
            }

        // ══════ 跳幅表 ══════
        W("═══════ 跳幅表（同一张网格上 盘32 − 盘31）═══════");
        W("网格\t细区 mm\t片0单元(31/32)\t管根31\t管根32\tΔ管根\t最热铂31\t最热铂32\tΔ最热铂\t净流入31\t净流入32\tΔ净流入\t判不了?");
        var navRow = cells.Where(c => c.Mesh.StartsWith("A")).ToArray();
        (double dc, double dh, double df)? nav = null;
        foreach (var g in cells.GroupBy(c => c.Mesh))
        {
            var a = g.FirstOrDefault(c => Math.Abs(c.R - 31.0) < 1e-9);
            var b = g.FirstOrDefault(c => Math.Abs(c.R - 32.0) < 1e-9);
            if (a is null || b is null) { W($"{g.Key}\t（这一档没跑完两点）"); continue; }
            double dc2 = b.Cold - a.Cold, dh2 = b.Hot - a.Hot, df2 = b.Flux - a.Flux;
            if (g.Key.StartsWith("A")) nav = (dc2, dh2, df2);
            W($"{g.Key}\t{a.H:0.000}\t{a.Cells}/{b.Cells}\t{a.Cold:0.000}\t{b.Cold:0.000}\t{dc2:+0.000;−0.000}\t"
              + $"{a.Hot:0.000}\t{b.Hot:0.000}\t{dh2:+0.000;−0.000}\t{a.Flux:0.000}\t{b.Flux:0.000}\t{df2:+0.000;−0.000}\t"
              + $"{((!a.Ok || !a.Conv ? "盘31 " : "") + (!b.Ok || !b.Conv ? "盘32 " : "")).Trim()}");
        }
        W("");
        if (nav is { } n0)
        {
            W("═══════ 判读（按跑前写死的门槛）═══════");
            foreach (var g in cells.GroupBy(c => c.Mesh).Where(g => !g.Key.StartsWith("A")))
            {
                var a = g.FirstOrDefault(c => Math.Abs(c.R - 31.0) < 1e-9);
                var b = g.FirstOrDefault(c => Math.Abs(c.R - 32.0) < 1e-9);
                if (a is null || b is null) { W($"{g.Key}：没跑完两点 ⇒ 这一档判不了。"); continue; }
                void One(string name, double navD, double d)
                {
                    double ratio = Math.Abs(navD) > 1e-9 ? Math.Abs(d) / Math.Abs(navD) : double.NaN;
                    W($"　{g.Key} {name}：导航跳幅 {navD:+0.000;−0.000} → 本档 {d:+0.000;−0.000}，比值 {(double.IsNaN(ratio) ? "—" : ratio.ToString("0.00"))}"
                      + $" ⇒ {(double.IsNaN(ratio) ? "判不了" : ratio <= 0.5 ? "**缩到一半以下（数值跳变的证据）**" : ratio >= 0.5 && ratio <= 2.0 ? "跳幅基本不动（模型分支的证据）" : "跳幅反而更大（**两者都不是**）")}");
                }
                One("管根", n0.dc, b.Cold - a.Cold);
                One("最热铂", n0.dh, b.Hot - a.Hot);
                One("净流入", n0.df, b.Flux - a.Flux);
            }
        }
        W("");

        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（= MeshAdapt.RefineWholeMesh，全仓唯一一份）；"
          + "逐片读数 = ThermocoupleBasis.At；峰位 = FlangeOut.DiscMax*／TabMax*。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(cells.Count >= 2, "一档都没跑完 —— 那不是实测");
        Assert.True(navRow.Length == 2, "导航档两点必须都跑到");
    }
}
