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
//  R48 V　**跳变诊断 ⑨：盘 30 → 31 那 +48 W 焦耳热，是在哪一块长出来的** —— 2026-09-18，Opus 5
//
//  ══ 为什么改用这个量法（反事实那条路走不通，已实测）
//
//    诊断 ⑦ 的反事实（把舌片厚压成板厚，关掉 ThicknessAt 的舌／盘分界）**判不了**：
//    舌片厚由 I/(J·舌宽) 闭式定，压到板厚 0.73 mm 后舌片截面 J 远超限，片温读到高出热偶读数 2668 K
//    （逐格发热已是 NaN = 越出电阻率拟合区间）—— 那是模型外推区，数值不可引用（S1）。
//    ⇒ 那一跑的输出文件按「判不了不当过」作废删除（同 §0.-7／§0.-10 的处置），改用**直接量**：
//
//    <see cref="FlangeOut.CellGenW"/> 是逐格焦耳热（生产件原样带出）。按格子形心分三块累加：
//      ① 管孔环 · 舌片厚那半（r ≤ 盘半径 且 x &lt; 切点x）—— ThicknessAt 判成舌片的那块盘
//      ② 管孔环 · 板厚那块（r ≤ 盘半径 且 x ≥ 切点x）—— 盘半径一涨就从 ① 抢过来的那一楔
//      ③ 环外（r &gt; 盘半径）—— 伸出去的舌片本身
//    盘 30 → 31 片0 总发热由 385.5 涨到 433.5 W（判决网格，诊断 ⑦ 原设计组实测）。
//    **那 +48 W 落在哪一块，就是这一支要回答的唯一问题。**
//
//  ══ 判读（**跑前写死，跑完不挪**）
//    · 「这一块是主因」= 该块的发热增量 ≥ 总增量的 50 %。
//    · 三块都不到 50 % ⇒ 写「增量是摊开的，没有主因块」。
//    · 判不了（没解出来／未收敛／逐格发热有 NaN）一律不当过，照印。
//
//  ⚠ 生产代码一行未动。本支不改任何设计字段，只对同一次解的逐格量分块累加。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VJumpGenSplitTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpGenSplitTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 跳变诊断_逐格发热分块_盘30与盘31_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_逐格发热分块_W08盘30与盘31_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_发热分块_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑨：**盘 30 → 31 的焦耳热增量落在哪一块**（W08，判决网格，逐格分块）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「这一块是主因」= 该块发热增量 ≥ 总增量的 50 %。");
        W("　② 三块都不到 50 % ⇒ 「增量是摊开的，没有主因块」。");
        W("　③ 判不了（没解出来／未收敛／逐格发热有 NaN）一律不当过。");
        W("");
        W("═══════ 分块口径 ═══════");
        W("按格子形心分三块：① 管孔环·舌片厚那半（r ≤ 盘半径 且 x < 切点x）；② 管孔环·板厚那块（r ≤ 盘半径 且 x ≥ 切点x）；③ 环外（r > 盘半径）。");
        W("「x < 切点x」就是 FlangePlate.ThicknessAt 判「算舌片」的那条线（竖线）；「r ≤ 盘半径」是保温与分区热账那条线（圆）—— 两条线不是同一条，本支正是要看它们错开的那一楔。");
        W("逐格发热 = FlangeOut.CellGenW（生产件原样带出，求和 = FlangeOut.QGenW）。网格 = Solver.ApplyCaseMesh(FineMm=1.0)。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var rows = new List<(double R, int J, double GenTabTh, double GenDiscTh, double GenOut, double GenTot,
                             int NTabTh, int NDiscTh, int NOut, double XT, bool Bad)>();
        foreach (double R in new[] { 30.0, 31.0 })
        {
            probe.Report($"── 开始 盘半径 {R:0.0}（判决网格）");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = 30.0;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - 900.0)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 1.0, FineRadiusMm = reqRadius });
            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();
            probe.Report($"── 盘 {R:0.0} 解完，{sw.Elapsed.TotalSeconds:0} s");

            W($"════ 盘半径 {R:0.0}（判决网格　细区 {lc.MeshFineMm:0.000} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm）");
            W($"　板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000")))}"
              + $"　管根低于热偶读数 {R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc):0.000}"
              + $"　最热铂高出热偶读数 {R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc):0.000}"
              + $"　管孔净流入 {R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux):0.000} W");
            if (!r.Ok || !r.Converged) { W($"　★ 判不了：{(!r.Ok ? r.Message : "外层耦合未收敛")} ⇒ 本档不当过。"); W(""); Flush(); continue; }

            W("　片\t切点x\t①环内·舌片厚 W\t②环内·板厚 W\t③环外 W\t合计 W\t①格数\t②格数\t③格数\t逐格发热有 NaN?");
            for (int j = 0; j < r.Flanges.Length; j++)
            {
                var f = r.Flanges[j];
                var m = f.Mesh;
                if (m is null || f.CellGenW.Length != m.CellCount)
                { W($"　片{j}\t**拿不到逐格发热或网格 ⇒ 判不了**"); continue; }
                double xT = lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)].Tangent().X;
                double g1 = 0, g2 = 0, g3 = 0; int n1 = 0, n2 = 0, n3 = 0; bool bad = false;
                for (int i = 0; i < m.CellCount; i++)
                {
                    double gw = f.CellGenW[i];
                    if (double.IsNaN(gw)) { bad = true; continue; }
                    var c = m.Centroid[i];
                    double rr = Math.Sqrt(c.X * c.X + c.Z * c.Z);
                    if (rr > R) { g3 += gw; n3++; }
                    else if (c.X < xT) { g1 += gw; n1++; }
                    else { g2 += gw; n2++; }
                }
                rows.Add((R, j, g1, g2, g3, g1 + g2 + g3, n1, n2, n3, xT, bad));
                W($"　片{j}\t{xT:0.000}\t{g1:0.000}\t{g2:0.000}\t{g3:0.000}\t{g1 + g2 + g3:0.000}（FlangeOut.QGenW {f.QGenW:0.000}）\t{n1}\t{n2}\t{n3}\t{(bad ? "**有**" : "无")}");
            }
            W("");
            Flush();
        }

        W("═══════ 增量分块（盘 31 − 盘 30，逐片）═══════");
        W("片\t①环内·舌片厚 Δ\t②环内·板厚 Δ\t③环外 Δ\t合计 Δ\t②占合计 %\t判词");
        for (int j = 0; j < 4; j++)
        {
            var a = rows.FirstOrDefault(x => Math.Abs(x.R - 30.0) < 1e-9 && x.J == j);
            var b = rows.FirstOrDefault(x => Math.Abs(x.R - 31.0) < 1e-9 && x.J == j);
            if (a.GenTot == 0 || b.GenTot == 0) { W($"片{j}\t（有一档没量到）⇒ **判不了**"); continue; }
            double d1 = b.GenTabTh - a.GenTabTh, d2 = b.GenDiscTh - a.GenDiscTh, d3 = b.GenOut - a.GenOut;
            double dt = d1 + d2 + d3;
            double share = Math.Abs(dt) > 1e-9 ? 100.0 * d2 / dt : double.NaN;
            string verdict = a.Bad || b.Bad ? "**逐格发热有 NaN ⇒ 判不了**"
                           : double.IsNaN(share) ? "合计增量≈0，不判"
                           : share >= 50 ? "**② 管孔环·板厚那一楔是主因**"
                           : (100.0 * d1 / dt) >= 50 ? "① 管孔环·舌片厚那半是主因"
                           : (100.0 * d3 / dt) >= 50 ? "③ 环外舌片是主因"
                           : "增量是摊开的，没有主因块";
            W($"片{j}\t{d1:+0.000;−0.000}\t{d2:+0.000;−0.000}\t{d3:+0.000;−0.000}\t{dt:+0.000;−0.000}\t{share:0.0}\t{verdict}");
        }
        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；逐格发热 = FlangeOut.CellGenW（= ShellThermal 的 res.CellGenW，求和即 QGenW）；");
        W("　切点 = FlangePlate.Tangent；厚度分界规则 = FlangePlate.ThicknessAt 的 onTab（x < 切点x）；网格 = Solver.ApplyCaseMesh。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(rows.Count >= 4, "至少要量到一档的四片 —— 否则不是实测");
    }
}
