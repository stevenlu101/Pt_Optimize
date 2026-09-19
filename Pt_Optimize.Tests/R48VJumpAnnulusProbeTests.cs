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
//  R48 V　**跳变诊断 ⑥：舌半宽 = 盘半径 这个退化点上，管孔那一圈的厚度是怎么翻的** —— 2026-09-18，Opus 5
//
//  ══ 要验的前提（先验前提再谈因果）
//
//    <see cref="FlangePlate.ThicknessAt"/> 判「这一点算舌片还是圆盘」用的是 **x &lt; 切点x**（一条竖线），
//    而保温与分区热账已在 2026-09-14 改成按**半径**圈（<see cref="FlangePlate.UnderDiscInsulation"/>／ShellThermal）。
//    两个内置档 **舌半宽 = 盘半径 = 30** ⇒ 切点恰在 x = 0 ⇒ **整个 x&lt;0 半边（含半圈管孔环）按舌片厚**（2.03／3.51 mm），
//    而不是板厚（0.73／1.26 mm）。盘半径一变大（或舌半宽一变小），切点按 −√(R²−w²) 往左跑，
//    这半圈环里 x &gt; 切点x 的那一楔就**从舌片厚掉到板厚**。
//
//    ⇒ 本支只量一件事，不解场：管孔环（孔半径 ≤ r ≤ 盘半径）里
//       **按舌片厚的面积 / 按板厚的面积**，随盘半径与舌半宽怎么走；并同时量网格体积。
//       切点 x = −√(R²−w²) 在 w = R 处**导数无穷**（√ 奇点）⇒ 若这一楔的面积确实按 √ 张开，
//       就能解释「离开退化点的第一小步，量就变一大截」。
//
//  ══ 判读（跑前写死，跑完不挪）
//    · 「这一楔随第一小步张开」= 盘半径从 30.00 走到 30.05（0.05 mm）时，环内板厚面积占比升幅
//      ≥ 从 30.50 走到 31.00（0.5 mm，十倍的步）时的升幅。达不到就写「不是 √ 形态」。
//    · 只量几何与网格，**不解场** ⇒ 本支不下任何关于温度的结论。
//
//  ⚠ 生产代码一行未动。
// ════════════════════════════════════════════════════════════════════════════

public class R48VJumpAnnulusProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpAnnulusProbeTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 跳变诊断_退化点上管孔环的厚度归属_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_退化点管孔环厚度归属_W08_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑥：**舌半宽 = 盘半径 这个退化点上，管孔那一圈的厚度归属**（W08，只量几何与网格，不解场）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("");
        W("═══════ 要验的前提 ═══════");
        W("FlangePlate.ThicknessAt 的舌／盘分界是 **x < 切点x**（竖线）；保温与分区热账 2026-09-14 已改成按半径圈。");
        W("两个内置档 舌半宽 = 盘半径 = 30 ⇒ 切点在 x = 0 ⇒ **x<0 半边（含半圈管孔环）按舌片厚**。");
        W("切点 x = −√(盘半径² − 舌半宽²)，在 舌半宽 = 盘半径 处导数无穷（√ 奇点）。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「这一楔随第一小步张开」= 30.00 → 30.05（0.05 mm）的板厚面积占比升幅 ≥ 30.50 → 31.00（0.5 mm）的升幅；");
        W("　　 达不到 ⇒ 写「不是 √ 形态」。");
        W("　② 本支不解场 ⇒ 不下任何关于温度的结论。");
        W("");

        // 环内面积：数值积分（极坐标，闭式判定 x < 切点x）
        static (double TabA, double DiscA) AnnulusSplit(double holeR, double R, double xT, FlangePlate g)
        {
            const int NR = 400, NT = 1440;
            double tabA = 0, discA = 0;
            for (int i = 0; i < NR; i++)
            {
                double r0 = holeR + (R - holeR) * i / NR, r1 = holeR + (R - holeR) * (i + 1) / NR;
                double rm = 0.5 * (r0 + r1), dA = 0.5 * (r1 * r1 - r0 * r0) * (2 * Math.PI / NT);
                for (int k = 0; k < NT; k++)
                {
                    double th = (k + 0.5) * 2 * Math.PI / NT;
                    double x = rm * Math.Cos(th), z = rm * Math.Sin(th);
                    if (!g.Inside(x, z)) continue;
                    if (x < xT) tabA += dA; else discA += dA;
                }
            }
            return (tabA, discA);
        }

        var cases = new List<(string Fam, double R, double HalfW)>();
        foreach (double R in new[] { 30.00, 30.01, 30.02, 30.05, 30.10, 30.20, 30.50, 31.00, 31.50, 32.00 })
            cases.Add(("盘径", R, 30.0));
        foreach (double w in new[] { 30.00, 29.99, 29.98, 29.95, 29.90, 29.80, 29.50, 29.063, 28.125 })
            cases.Add(("舌宽", 30.0, w));

        W("═══════ 管孔环（孔半径 25.8 ≤ r ≤ 盘半径）里的厚度归属 ═══════");
        W("组\t盘半径\t舌半宽\t切点x\t环总面积 mm²\t按舌片厚的面积\t按板厚的面积\t板厚占比 %\t片0舌片厚\t片0板厚\t片0网格体积 mm³\t片0单元");
        var rows = new List<(string Fam, double R, double W2, double XT, double Frac)>();
        foreach (var (fam, R, hw) in cases)
        {
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = hw;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            double floorD = d.DiscFloorMm(p);
            var g = d.Plate(0, floorD);
            g.HoleRadiusMm = d.HoleRadiusMm;
            double xT = g.Tangent().X;
            var (tabA, discA) = AnnulusSplit(g.HoleRadiusMm, R, xT, g);
            double tot = tabA + discA;
            var lc = d.BuildCase(p);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
            var mesh = LineRunner.PlateMeshAnalytic(lc, 0);
            double frac = tot > 1e-9 ? 100.0 * discA / tot : double.NaN;
            rows.Add((fam, R, hw, xT, frac));
            W($"{fam}\t{R:0.000}\t{hw:0.000}\t{xT:0.0000}\t{tot:0.00}\t{tabA:0.00}\t{discA:0.00}\t{frac:0.00}\t"
              + $"{d.TongueThickMm[0]:0.000}\t{Math.Max(d.TabThickMm[0], floorD):0.000}\t{mesh.VolumeMm3:0.0}\t{mesh.CellCount}");
        }
        W("");

        W("═══════ 判读 ═══════");
        double F(string fam, double R, double w) => rows.First(r => r.Fam == fam && Math.Abs(r.R - R) < 1e-9 && Math.Abs(r.W2 - w) < 1e-9).Frac;
        double first = F("盘径", 30.05, 30.0) - F("盘径", 30.00, 30.0);
        double tenx = F("盘径", 31.00, 30.0) - F("盘径", 30.50, 30.0);
        W($"　盘径：30.00 → 30.05（步 0.05）板厚占比 {first:+0.00;−0.00} 个百分点；30.50 → 31.00（步 0.50，十倍）{tenx:+0.00;−0.00} 个百分点 ⇒ "
          + $"{(first >= tenx ? "**第一小步张得更大 ⇒ 是 √ 形态（退化点处导数无穷）**" : "第一小步没有张得更大 ⇒ **不是 √ 形态**")}");
        double firstW = F("舌宽", 30.0, 29.95) - F("舌宽", 30.0, 30.00);
        double tenxW = F("舌宽", 30.0, 29.063) - F("舌宽", 30.0, 29.50);
        W($"　舌宽：30.00 → 29.95（步 0.05）板厚占比 {firstW:+0.00;−0.00} 个百分点；29.50 → 29.063（步 0.437，约九倍）{tenxW:+0.00;−0.00} 个百分点 ⇒ "
          + $"{(firstW >= tenxW ? "**第一小步张得更大 ⇒ 同一个 √ 形态**" : "第一小步没有张得更大 ⇒ **不是 √ 形态**")}");
        W("");

        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：切点 = FlangePlate.Tangent；厚度归属 = FlangePlate.ThicknessAt 的 onTab 规则（x < 切点x）；有没有料 = FlangePlate.Inside；");
        W("　板厚下角／舌片厚 = Solver.ApplySectionFloor 与 DesignSpec.DiscFloorMm；网格 = LineRunner.PlateMeshAnalytic。");
        W("环内面积按极坐标数值积分（400 × 1440 格），只作面积占比，不作场量。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(File.Exists(file));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  反事实：把**舌片厚压成板厚**（舌／盘没有厚度台阶）之后，盘 30 → 31 那段陡升还在不在
    //
    //  为什么这么做：ThicknessAt 的舌／盘分界（x < 切点x）只有在**舌片厚 ≠ 板厚**时才起作用
    //  （它返回的两个值相等时，这条分界一点区别都造不出来）。⇒ 把两者压成相等，
    //  这条分支就**被关掉了**，而几何、保温圈、分区热账、网格配方一位不动。
    //
    //  ⚠ 这是**反事实对照件，不是可交付设计**：舌片厚本来由 I/(J·舌宽) 闭式定，压成板厚会让舌片截面 J 超限。
    //    本支只看「陡升还在不在」这一件事，不看它过不过判据。
    //
    //  判读（跑前写死，跑完不挪）：
    //    · 关掉这条分支后，盘 30 → 31 的「最热铂」升幅 ≤ 原设计升幅的一半 ⇒ 判**这条分支是那段陡升的主因**。
    //    · 升幅仍在原设计的一半以上 ⇒ 判**不是这条分支**（另找）。
    //    · 判不了（没解出来／未收敛）一律不当过。
    // ══════════════════════════════════════════════════════════════════════
    [Trait("速度", "慢")]
    [Fact]
    public void 跳变诊断_反事实_舌片厚压成板厚后陡升还在不在_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_反事实_舌片厚压成板厚_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_反事实_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑦：**反事实 —— 舌片厚压成板厚（关掉 ThicknessAt 的舌／盘分界）之后，盘 30 → 31 那段陡升还在不在**（W08，判决网格）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("⚠ **反事实对照件，不是可交付设计**：舌片厚本由 I/(J·舌宽) 闭式定，压成板厚后舌片截面 J 会超限。本支只看陡升还在不在。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 关掉分支后盘 30 → 31 的「最热铂」升幅 ≤ 原设计升幅的一半 ⇒ 这条分支是那段陡升的主因。");
        W("　② 升幅仍在一半以上 ⇒ 不是这条分支。");
        W("　③ 判不了（没解出来／未收敛）一律不当过。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：Solver.ApplyCaseMesh(FineMm=1.0, FineRadiusMm=RequiredMeshFor) —— 与判决同一张，两组同配方。");
        W("原设计组：Solver.ApplySectionFloor 定舌片厚（2.03/3.51/3.51/2.03）。");
        W("反事实组：ApplySectionFloor 之后把 TongueThickMm[j] 覆盖成 max(TabThickMm[j], 圆盘板厚工艺下界) —— 舌与盘同厚，分界失效。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var rows = new List<(string Grp, double R, double Cold, double Hot, double Flux, double Mass,
                             bool Ok, bool Conv, string Failed, double Gen0, int Cells, double Secs, string Tongue, string Thick)>();
        foreach (string grp in new[] { "原设计（舌片厚 ≠ 板厚）", "反事实（舌片厚 = 板厚）" })
            foreach (double R in new[] { 30.0, 31.0 })
            {
                probe.Report($"── 开始 {grp} 盘半径 {R:0.0}");
                var sw = Stopwatch.StartNew();
                var d = d0.Clone();
                d.DiscRadiusMm = R; d.TabHalfWidthMm = 30.0;
                d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - 900.0)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
                var dummy = new SolverResult { Design = d };
                Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
                double floorD = d.DiscFloorMm(p);
                if (grp.StartsWith("反事实"))
                    for (int j = 0; j < d.TongueThickMm.Length; j++)
                        d.TongueThickMm[j] = Math.Max(d.TabThickMm[j], floorD);

                var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 1.0, FineRadiusMm = reqRadius });
                LineResult r;
                try { r = LineRunner.Run(lc, probe); }
                catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
                sw.Stop();
                rows.Add((grp, R,
                          R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc),
                          R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc),
                          R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux),
                          r.Ok ? r.TotalMassG : double.NaN, r.Ok, r.Converged,
                          r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                          r.Ok && r.Flanges.Length > 0 ? r.Flanges[0].QGenW : double.NaN,
                          r.MeshCells, sw.Elapsed.TotalSeconds,
                          string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000"))),
                          string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000")))));
                var last = rows[^1];
                probe.Report($"── {grp} 盘 {R:0.0} 结束：管根 {last.Cold:0.000}／最热铂 {last.Hot:0.000}／净流入 {last.Flux:0.000}，{sw.Elapsed.TotalSeconds:0} s");
                W($"════ {grp}　盘半径 {R:0.0}（判决网格）");
                W($"　板厚 {last.Thick}　舌片厚 {last.Tongue}　片0单元 {r.MeshCells}");
                W($"　管根低于热偶读数 {last.Cold:0.000}　最热铂高出热偶读数 {last.Hot:0.000}　管孔净流入 {last.Flux:0.000} W"
                  + $"　片0 发热 {last.Gen0:0.000} W　铂重 {last.Mass:0} g　耗时 {sw.Elapsed.TotalSeconds:0} s"
                  + $"　{(!r.Ok ? "判不了（没解出来）" : !r.Converged ? "判不了（未收敛）" : r.AllOk ? "全过" : "不过：" + last.Failed)}");
                if (r.Ok && r.Flanges is { Length: > 0 })
                    for (int j = 0; j < r.Flanges.Length; j++)
                    {
                        var t = ThermocoupleBasis.At(r, j);
                        var f = r.Flanges[j];
                        W($"　　片{j} {t.Name}｜最热的是{t.HottestWhat} {t.HottestC:0.0}｜圆盘峰 {f.TDiscMaxC:0.0} @r={f.DiscMaxRMm:0.00} t={f.DiscMaxThickMm:0.000}"
                          + $"｜舌片区峰 {f.TTabMaxC:0.0} @r={f.TabMaxRMm:0.00} t={f.TabMaxThickMm:0.000}｜热侧 {t.HotK:0.000}｜冷侧 {t.ColdK:0.000}"
                          + $"｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}");
                    }
                W("");
                Flush();
            }

        W("═══════ 对照（盘 31 − 盘 30，同一组内）═══════");
        W("组\t最热铂 盘30\t最热铂 盘31\tΔ最热铂\t管根 盘30\t管根 盘31\tΔ管根\t净流入 盘30\t净流入 盘31\tΔ净流入\t片0发热 盘30\t片0发热 盘31");
        double baseDelta = double.NaN, cfDelta = double.NaN;
        foreach (var g in rows.GroupBy(x => x.Grp))
        {
            var a = g.First(x => Math.Abs(x.R - 30.0) < 1e-9);
            var b = g.First(x => Math.Abs(x.R - 31.0) < 1e-9);
            double dh = b.Hot - a.Hot;
            if (g.Key.StartsWith("原设计")) baseDelta = dh; else cfDelta = dh;
            W($"{g.Key}\t{a.Hot:0.000}\t{b.Hot:0.000}\t{dh:+0.000;−0.000}\t{a.Cold:0.000}\t{b.Cold:0.000}\t{b.Cold - a.Cold:+0.000;−0.000}\t"
              + $"{a.Flux:0.000}\t{b.Flux:0.000}\t{b.Flux - a.Flux:+0.000;−0.000}\t{a.Gen0:0.000}\t{b.Gen0:0.000}");
        }
        W("");
        W("═══════ 判读（按跑前写死的门槛）═══════");
        if (!double.IsNaN(baseDelta) && !double.IsNaN(cfDelta))
        {
            double ratio = Math.Abs(baseDelta) > 1e-9 ? Math.Abs(cfDelta) / Math.Abs(baseDelta) : double.NaN;
            W($"　原设计 Δ最热铂 {baseDelta:+0.000;−0.000} K；反事实 {cfDelta:+0.000;−0.000} K；比值 {(double.IsNaN(ratio) ? "—" : ratio.ToString("0.00"))} ⇒ "
              + $"{(double.IsNaN(ratio) ? "判不了" : ratio <= 0.5 ? "**关掉 ThicknessAt 的舌／盘分界之后，那段陡升掉到一半以下 ⇒ 这条分支是主因**" : "**升幅仍在一半以上 ⇒ 不是这条分支**")}");
        }
        else W("　有一组没跑出来 ⇒ **判不了**，不当过。");
        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；舌／盘厚度分界 = FlangePlate.ThicknessAt 的 onTab 规则。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(4, rows.Count);
    }
}
