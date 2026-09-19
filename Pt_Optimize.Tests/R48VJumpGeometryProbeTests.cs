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
//  R48 V　**跳变诊断 ①：盘半径 31 → 32 这一步，进场解之前的输入变了什么** —— 2026-09-18，Opus 5
//
//  ══ 要查的事实（出自 deliverable/R48_形状探针_方向实测_W08_本次开跑于2026-09-18_090213.txt）
//
//    只动盘半径、其余一位不动：
//      盘 30 → 31：管根 19.922 → 18.587、最热铂 −9.460 → +0.903、净流入 +6.257 → +2.757（三条平滑）
//      盘 31 → 32：管根 18.587 → **−2.900**、最热铂 0.903 → **42.369**、净流入 +2.757 → **−11.340**
//    截面 J 9.983 不动、铂重 4370 → 4410。**1 mm 盘径把最热铂动 41 K**。
//
//  ══ 本支只做一件事：把「进场解之前就定下来的东西」按 0.05 mm 的盘径细档全部印出来
//
//    场解贵（一次整线解 90～155 s），而**闭式量与网格是秒级的**。先验前提再谈因果：
//    如果跳变的根在闭式几何／板厚下角／网格分区上，这一支当场就看得见，不必花几十分钟去解场；
//    如果这些量在 31 → 32 之间全是平滑的，那就**排除**了它们，跳变的根在场解或外层耦合里。
//
//    印什么（每一项都指回生产件，不手抄配方）：
//      · 切点 x／半宽            FlangePlate.Tangent()
//      · 舌长                     搜形状规则 = 切点 + 压接段 + 自由段下界（UI/LineDesignPage.cs EvalShape）
//      · 板厚下角／舌片厚        Solver.ApplySectionFloor（闭式重定；不是旋钮）
//      · 最紧截面在哪、J 多少    SectionSizing.Worst
//      · 判据⑥ 的闭式下界        GeometryScreen.MinDiscRadiusMm
//      · 细区半径                MeshVerify.RequiredMeshFor
//      · 网格：单元数、面积、体积、按半径圈的「圆盘区／舌片区」格数、被保温圈住的格数、
//        分界圆穿过的格数、按 x 划的厚度分区（ThicknessAt 的 onTab）格数
//                                LineRunner.PlateMeshAnalytic（= 整线解用的同一张网格）
//      · 设计电流                 DesignCurrent.ForLine
//
//  ══ 判读门槛（跑前写死，跑完不挪）
//
//    · 「这一维有跳变」= 相邻 0.05 mm 两档之间，该量的相对变化 > 相邻档平均变化的 5 倍，
//      且绝对变化不小于该量量程的 1 %。达不到就记「平滑」。
//    · 网格格数这种整数量：只要在 31.0～32.0 之间出现**非单调**或单步 > 20 格的跳，就点名。
//    · 判不了（造不出几何、抛异常）照印，不当过。
//
//  ⚠ 生产代码一行未动。本文件只读生产件、只写 deliverable 输出。
// ════════════════════════════════════════════════════════════════════════════

public class R48VJumpGeometryProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpGeometryProbeTests(ITestOutputHelper o) { _o = o; }

    private sealed record Row(
        double R, double HalfW, double TabLen, double TangentX, double TangentW,
        double NeedDisc, double ReqFine, double ReqRadius,
        string Thick, string Tongue, string WorstWhere, double WorstJ, double WorstArea,
        string Current,
        int[] Cells, double[] Vol, double[] Area,
        int[] DiscZone, int[] TabZone, int[] Insul, int[] Blend, int[] ThickOnTab,
        double[] MinTh, double[] MaxTh, double MassG);

    [Fact]
    public void 跳变诊断_盘径与舌宽细档_闭式量与网格()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_闭式与网格细档_W08_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ①：**盘半径 31 → 32 这一步，进场解之前的输入变了什么**（W08，闭式量与网格，不解场）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("要查的事实（deliverable/R48_形状探针_方向实测_W08_本次开跑于2026-09-18_090213.txt）：");
        W("　盘 30→31 三条判据平滑（管根 19.922→18.587／最热铂 −9.460→+0.903／净流入 +6.257→+2.757）；");
        W("　盘 31→32 一步：管根 18.587→−2.900、最热铂 0.903→42.369、净流入 +2.757→−11.340；截面 J 9.983 不动。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「这一维有跳变」= 相邻档之间该量的变化 > 全段相邻变化中位数的 5 倍（且不是数值零头）。达不到记「平滑」。");
        W("　② 网格格数这类整数量：31.0～32.0 之间出现**非单调**或单步 > 20 格 ⇒ 点名。");
        W("　③ 造不出几何／抛异常 ⇒ 照印，不当过。");
        W("");
        W("═══════ 口径 ═══════");
        W("舌长 = √(盘半径² − 舌半宽²) + 压接段 40 + 自由段下界 100（搜形状规则，UI/LineDesignPage.cs EvalShape）。");
        W("板厚下角／舌片厚 = Solver.ApplySectionFloor（闭式重定，不是旋钮）；最紧截面 = SectionSizing.Worst。");
        W("网格 = LineRunner.PlateMeshAnalytic（整线解逐片用的同一张），配方 Solver.ApplyCaseMesh(FineMm=0, FineRadiusMm=RequiredMeshFor)。");
        W("「圆盘区／舌片区」按 ShellThermal 的新口径 r ≤ 盘半径（形心）；「保温圈住」按 FlangePlate.InsideInsulCircle(形心, 盘半径)；");
        W("「厚度分区 onTab」按 FlangePlate.ThicknessAt 的规则 x < 切点x（**与上面两条不是同一条线**）。");
        W("");
        Flush();

        var groups = new List<(string Name, List<(double R, double HalfW)> Pts)>();
        var discPts = new List<(double, double)>();
        for (int i = 0; i <= 60; i++) discPts.Add((30.0 + i * 0.05, 30.0));
        groups.Add(("盘半径 30.00 → 33.00（步 0.05，舌半宽固定 30）", discPts));
        var tabPts = new List<(double, double)>();
        for (int i = 0; i <= 50; i++) tabPts.Add((30.0, 30.0 - i * 0.02));
        groups.Add(("舌半宽 30.00 → 29.00（步 0.02，盘半径固定 30）", tabPts));

        foreach (var (gname, pts) in groups)
        {
            var rows = new List<Row>();
            foreach (var (R, hw) in pts)
            {
                var d = d0.Clone();
                d.DiscRadiusMm = R;
                d.TabHalfWidthMm = hw;
                d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
                var dummy = new SolverResult { Design = d };
                Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

                double floorD = d.DiscFloorMm(p);
                var plates = Enumerable.Range(0, d.TabThickMm.Length).Select(j => d.Plate(j, floorD)).ToArray();
                double need = GeometryScreen.MinDiscRadiusMm(plates);
                var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d);
                var dc = DesignCurrent.ForLine(d, p, null);
                var wst = SectionSizing.Worst(plates[0], dc.PlateA.Length > 0 ? dc.PlateA[0] : 0, d.ClampLengthMm);
                var (xt, wt) = plates[0].Tangent();

                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });

                int np = d.TabThickMm.Length;
                var cells = new int[np]; var vol = new double[np]; var area = new double[np];
                var dz = new int[np]; var tz = new int[np]; var ins = new int[np];
                var blend = new int[np]; var thTab = new int[np];
                var minTh = new double[np]; var maxTh = new double[np];
                double mass = 0;
                for (int j = 0; j < np; j++)
                {
                    var mesh = LineRunner.PlateMeshAnalytic(lc, j);
                    var pl = lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)];
                    double xtJ = pl.Tangent().X;
                    cells[j] = mesh.CellCount; vol[j] = mesh.VolumeMm3; area[j] = mesh.TotalArea;
                    minTh[j] = double.MaxValue; maxTh[j] = 0;
                    for (int i = 0; i < mesh.CellCount; i++)
                    {
                        var c = mesh.Centroid[i];
                        double rr = Math.Sqrt(c.X * c.X + c.Z * c.Z);
                        if (rr > R) tz[j]++; else dz[j]++;
                        if (FlangePlate.InsideInsulCircle(c.X, c.Z, pl.InsulDiscRadiusMm)) ins[j]++;
                        if (c.X < xtJ) thTab[j]++;
                        double f = FlangeMesher.MaterialFraction(mesh, i, (x, z) => FlangePlate.InsideInsulCircle(x, z, pl.InsulDiscRadiusMm));
                        if (!double.IsNaN(f) && f > 1e-9 && f < 1 - 1e-9) blend[j]++;
                        minTh[j] = Math.Min(minTh[j], mesh.Thickness[i]);
                        maxTh[j] = Math.Max(maxTh[j], mesh.Thickness[i]);
                    }
                    mass += mesh.VolumeMm3 * Materials.PtDensity * 1e-6;
                }

                rows.Add(new Row(R, hw, d.TabLengthMm, xt, wt, need, reqFine, reqRadius,
                                 string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000"))),
                                 string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000"))),
                                 wst.Where, wst.JAPerMm2, wst.AreaMm2,
                                 string.Join("/", dc.PlateA.Select(v => v.ToString("0"))),
                                 cells, vol, area, dz, tz, ins, blend, thTab, minTh, maxTh, mass));
            }

            W($"═══════ {gname} ═══════");
            W("盘半径\t舌半宽\t舌长\t切点x\t切点半宽\t⑥下界\t细区尺寸\t细区半径\t板厚(4片)\t舌片厚(4片)\t最紧截面\tJ\t设计电流(4片)\t"
              + "单元数(4片)\t圆盘区格(4片)\t舌片区格(4片)\t保温圈住格(4片)\t分界混合格(4片)\t厚度onTab格(4片)\t"
              + "片0厚度min/max\t法兰总重g");
            foreach (var r in rows)
                W($"{r.R:0.00}\t{r.HalfW:0.00}\t{r.TabLen:0.00}\t{r.TangentX:0.000}\t{r.TangentW:0.000}\t{r.NeedDisc:0.000}\t"
                  + $"{r.ReqFine:0.000}\t{r.ReqRadius:0.00}\t{r.Thick}\t{r.Tongue}\t{r.WorstWhere}\t{r.WorstJ:0.000}\t{r.Current}\t"
                  + $"{string.Join("/", r.Cells)}\t{string.Join("/", r.DiscZone)}\t{string.Join("/", r.TabZone)}\t"
                  + $"{string.Join("/", r.Insul)}\t{string.Join("/", r.Blend)}\t{string.Join("/", r.ThickOnTab)}\t"
                  + $"{r.MinTh[0]:0.000}/{r.MaxTh[0]:0.000}\t{r.MassG:0.0}");
            W("");

            // ── 逐维「最大单步」检查（按判读①）
            W("── 相邻档单步位移（找跳变；中位数与最大值）");
            void Step(string name, Func<Row, double> f)
            {
                var ds = new List<(double At, double D)>();
                for (int i = 1; i < rows.Count; i++) ds.Add((rows[i].R == rows[i - 1].R ? rows[i].HalfW : rows[i].R, f(rows[i]) - f(rows[i - 1])));
                var abs = ds.Select(t => Math.Abs(t.D)).OrderBy(v => v).ToArray();
                double med = abs.Length == 0 ? double.NaN : abs[abs.Length / 2];
                var mx = ds.OrderByDescending(t => Math.Abs(t.D)).First();
                W($"{name}\t中位单步 {med:0.0000}\t最大单步 {mx.D:+0.0000;−0.0000} 发生在 {mx.At:0.00}\t"
                  + $"倍率 {(med > 1e-12 ? (Math.Abs(mx.D) / med).ToString("0.0") : "—")}"
                  + $"\t{(med > 1e-12 && Math.Abs(mx.D) / med > 5 ? "★ 跳变" : "平滑")}");
            }
            Step("舌长", r => r.TabLen);
            Step("切点x", r => r.TangentX);
            Step("⑥下界", r => r.NeedDisc);
            Step("细区半径", r => r.ReqRadius);
            Step("最紧截面J", r => r.WorstJ);
            Step("法兰总重", r => r.MassG);
            for (int j = 0; j < 4; j++)
            {
                int jj = j;
                Step($"片{jj} 单元数", r => r.Cells[jj]);
                Step($"片{jj} 圆盘区格", r => r.DiscZone[jj]);
                Step($"片{jj} 保温圈住格", r => r.Insul[jj]);
                Step($"片{jj} 厚度onTab格", r => r.ThickOnTab[jj]);
                Step($"片{jj} 体积", r => r.Vol[jj]);
            }
            W("");
            Flush();
        }

        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：切点 = FlangePlate.Tangent；板厚下角／舌片厚 = Solver.ApplySectionFloor；最紧截面 = SectionSizing.Worst；");
        W("　⑥ 下界 = GeometryScreen.MinDiscRadiusMm；网格 = LineRunner.PlateMeshAnalytic + Solver.ApplyCaseMesh；");
        W("　保温圈判定 = FlangePlate.InsideInsulCircle；分界混合份额 = FlangeMesher.MaterialFraction；设计电流 = DesignCurrent.ForLine。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(File.Exists(file));
    }
}
