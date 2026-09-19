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
//  R48 V　**跳变诊断 ④：31.0 → 32.0 之间，代码里每一条 if 的翻面点在哪** —— 2026-09-18，Opus 5
//
//  ══ 为什么要它
//    诊断 ① 已经量到：31.0 → 32.0 之间闭式量与网格有两处非平滑（片0 厚度从 31.05 起开始随盘径长、
//    网格格数在 31.05 掉 70 格）。本支把**分支本身**按 0.01 mm 扫出来，报出每一条判断在哪一档翻面：
//      ① 板厚由谁定：旋钮 TabThickMm ／ 焊接屈曲下界 DesignSpec.DiscFloorMm ／ 焊接烧穿底 WeldMinThicknessMm ／ 按 J 的截面 ApplySectionFloor
//      ② 焊脚 max(板厚, 管壁) 由谁定（FlangePlate.WeldFilletLegMm）
//      ③ 舌根圆角画不画：FlangePlate.HalfWidthSingle 的 `rf > 0 && 舌半宽 < 盘半径`
//      ④ 舌盘交界截面走哪一支：SectionSizing.Cuts 的 `|切点x| < 孔半径`
//      ⑤ 圆盘台阶半径 RingRadiiOf = 孔 + 环宽、孔 + 2×环宽（= 28.8 与 **31.8**）落不落进盘内（ThicknessAt 的逐级命中）
//      ⑥ 判据⑥ 的闭式下界 GeometryScreen.MinDiscRadiusMm
//      ⑦ 网格：格数、管孔定温环吃到孔边以外多少、压接退化位、保温分界混合格数、按半径与按 x 两条分区线各圈住多少格
//
//  ══ 判读（跑前写死，跑完不挪）
//    · 「分支翻面」= 相邻 0.01 档之间该布尔量或整数标签改变。逐条报翻面档位。
//    · 连续量只报「导数变号／折点」：相邻差从 0 变成非 0（或反之）那一档。
//    · 没翻面就写「本段内没翻」。**不许因为「应该在那里」就说它在那里。**
//
//  ⚠ 生产代码一行未动。
// ════════════════════════════════════════════════════════════════════════════

public class R48VJumpBranchScanTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpBranchScanTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void 跳变诊断_分支翻面点扫描_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_分支翻面点_W08_本次开跑于{stamp}.txt");
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ④：**盘半径 30.90 → 32.10 按 0.01 步，代码里每一条分支的翻面点**（W08，不解场）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 布尔／整数标签：相邻 0.01 档之间改变 ⇒ 报翻面档位；没变 ⇒ 写「本段内没翻」。");
        W("　② 连续量：只报折点（相邻差由 0 变非 0，或斜率突变）。");
        W("　③ 不许因为「应该在那里」就说它在那里。");
        W("");

        var rows = new List<(double R, double Floor, double KnobT0, double Td0, double Td1, string Driver0,
                            double WeldLeg0, bool WeldByPlate0, bool Fillet, bool TangentInHole,
                            bool R1In, bool R2In, double Need6, int Cells0, double HoleTagOver0,
                            bool ClampCover0, bool ClampIntoDisc0, int Blend0, int InsulCells0, int OnTabTh0,
                            double Vol0, string Worst0Where, double Worst0J, double TabLen, double XT)>();

        for (int i = 0; i <= 120; i++)
        {
            double R = 30.90 + i * 0.01;
            double hw = 30.0;
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = hw;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            double knobT0 = d.TabThickMm[0];
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

            double floorD = d.DiscFloorMm(p);
            double buckling = WeldDistortion.ForPt(1.0, kb: WeldDistortion.PlateBucklingKFreeEdge).SlopePerB
                            * (R - d.HoleRadiusMm) * p.WeldSafetyFactor;
            var plates = Enumerable.Range(0, d.TabThickMm.Length).Select(j => d.Plate(j, floorD)).ToArray();
            double td0 = Math.Max(d.TabThickMm[0], floorD), td1 = Math.Max(d.TabThickMm[1], floorD);
            string driver0 = Math.Abs(d.TabThickMm[0] - knobT0) > 1e-12 ? "按J的截面(ApplySectionFloor)"
                           : floorD > d.TabThickMm[0] + 1e-12
                             ? (buckling >= p.WeldMinThicknessMm - 1e-12 ? "焊接屈曲下界(DiscFloorMm)" : "焊接烧穿底(WeldMinThicknessMm)")
                             : "旋钮 TabThickMm";
            var (xt, _) = plates[0].Tangent();
            var rr = d.RingRadiiOf(0);
            var dc = DesignCurrent.ForLine(d, p, null);
            var wst = SectionSizing.Worst(plates[0], dc.PlateA.Length > 0 ? dc.PlateA[0] : 0, d.ClampLengthMm);

            var lc = d.BuildCase(p);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
            var mesh = LineRunner.PlateMeshAnalytic(lc, 0);
            var pl0 = lc.FlangePlates[0];
            int blend = 0, ins = 0, onTabTh = 0;
            double xt0 = pl0.Tangent().X;
            for (int k = 0; k < mesh.CellCount; k++)
            {
                var cc = mesh.Centroid[k];
                if (FlangePlate.InsideInsulCircle(cc.X, cc.Z, pl0.InsulDiscRadiusMm)) ins++;
                if (cc.X < xt0) onTabTh++;
                double f = FlangeMesher.MaterialFraction(mesh, k, (x, z) => FlangePlate.InsideInsulCircle(x, z, pl0.InsulDiscRadiusMm));
                if (!double.IsNaN(f) && f > 1e-9 && f < 1 - 1e-9) blend++;
            }

            rows.Add((R, floorD, knobT0, td0, td1, driver0,
                      plates[0].WeldFilletLegMm, td0 >= d.WallMm - 1e-12,
                      d.TabFilletMm > 1e-9 && hw < R - 1e-9,
                      Math.Abs(xt) < plates[0].HoleRadiusMm,
                      rr[0] > plates[0].HoleRadiusMm && rr[0] < R, rr[1] > plates[0].HoleRadiusMm && rr[1] < R,
                      GeometryScreen.MinDiscRadiusMm(plates), mesh.CellCount, mesh.HoleTagMaxROverMm,
                      mesh.ClampCoversHole, mesh.ClampIntoDisc, blend, ins, onTabTh, mesh.VolumeMm3,
                      wst.Where, wst.JAPerMm2, d.TabLengthMm, xt));
        }

        W("═══════ 逐档表（盘半径 30.90 → 32.10，步 0.01）═══════");
        W("盘半径\t屈曲下界\t板厚旋钮\t片0板厚\t片1板厚\t片0板厚由谁定\t片0焊脚\t焊脚由板厚定?\t舌根圆角画?\t切点在孔内?\t"
          + "台阶r1=28.8在盘内?\t台阶r2=31.8在盘内?\t⑥下界\t片0单元\t孔环吃出孔边mm\t压接盖孔?\t压接进盘?\t分界混合格\t保温圈住格\t厚度onTab格\t片0体积\t最紧截面\tJ\t舌长\t切点x");
        foreach (var r in rows)
            W($"{r.R:0.00}\t{r.Floor:0.0000}\t{r.KnobT0:0.0000}\t{r.Td0:0.0000}\t{r.Td1:0.0000}\t{r.Driver0}\t{r.WeldLeg0:0.0000}\t{(r.WeldByPlate0 ? "板厚" : "管壁")}\t"
              + $"{(r.Fillet ? "画" : "不画")}\t{(r.TangentInHole ? "是" : "否")}\t{(r.R1In ? "是" : "否")}\t{(r.R2In ? "是" : "否")}\t{r.Need6:0.000}\t{r.Cells0}\t{r.HoleTagOver0:0.000}\t"
              + $"{(r.ClampCover0 ? "是" : "否")}\t{(r.ClampIntoDisc0 ? "是" : "否")}\t{r.Blend0}\t{r.InsulCells0}\t{r.OnTabTh0}\t{r.Vol0:0.0}\t{r.Worst0Where}\t{r.WorstJ():0.000}\t{r.TabLen:0.00}\t{r.XT:0.000}");
        W("");

        W("═══════ 分支翻面点（判读①）═══════");
        void Flip<T>(string name, Func<int, T> f) where T : notnull
        {
            var flips = new List<string>();
            for (int i = 1; i < rows.Count; i++)
                if (!f(i).Equals(f(i - 1)))
                    flips.Add($"{rows[i - 1].R:0.00} → {rows[i].R:0.00}（{f(i - 1)} → {f(i)}）");
            W($"{name}：{(flips.Count == 0 ? "**本段内没翻**" : string.Join("；", flips))}");
        }
        Flip("片0 板厚由谁定", i => rows[i].Driver0);
        Flip("焊脚由板厚还是管壁定", i => rows[i].WeldByPlate0);
        Flip("舌根圆角画不画", i => rows[i].Fillet);
        Flip("舌盘交界截面：切点在孔内?", i => rows[i].TangentInHole);
        Flip("台阶 r1 = 孔+环宽 = 28.8 在盘内?", i => rows[i].R1In);
        Flip("台阶 r2 = 孔+2×环宽 = **31.8** 在盘内?", i => rows[i].R2In);
        Flip("压接段盖到管孔?", i => rows[i].ClampCover0);
        Flip("压接段伸进圆盘?", i => rows[i].ClampIntoDisc0);
        Flip("片0 最紧截面在哪", i => rows[i].Worst0Where);
        W("");

        W("═══════ 连续量的折点（判读②：相邻差由 0 变非 0，或反之）═══════");
        void Kink(string name, Func<int, double> f)
        {
            var ks = new List<string>();
            for (int i = 2; i < rows.Count; i++)
            {
                double d1 = f(i - 1) - f(i - 2), d2 = f(i) - f(i - 1);
                bool z1 = Math.Abs(d1) < 1e-12, z2 = Math.Abs(d2) < 1e-12;
                if (z1 != z2) ks.Add($"{rows[i - 1].R:0.00} → {rows[i].R:0.00}（前一步 {d1:+0.00000;−0.00000}，这一步 {d2:+0.00000;−0.00000}）");
            }
            W($"{name}：{(ks.Count == 0 ? "本段内没有折点" : string.Join("；", ks))}");
        }
        Kink("片0 板厚", i => rows[i].Td0);
        Kink("片1 板厚", i => rows[i].Td1);
        Kink("片0 焊脚", i => rows[i].WeldLeg0);
        Kink("⑥ 下界", i => rows[i].Need6);
        W("");

        W("═══════ 网格格数的非单调与大跳（判读①②合并）═══════");
        W("盘半径\t片0单元\tΔ\t厚度onTab格\tΔ\t保温圈住格\tΔ\t体积\tΔ");
        for (int i = 1; i < rows.Count; i++)
        {
            int dc2 = rows[i].Cells0 - rows[i - 1].Cells0;
            int dt = rows[i].OnTabTh0 - rows[i - 1].OnTabTh0;
            int di = rows[i].InsulCells0 - rows[i - 1].InsulCells0;
            double dv = rows[i].Vol0 - rows[i - 1].Vol0;
            if (Math.Abs(dc2) >= 10 || Math.Abs(dt) >= 10 || Math.Abs(di) >= 10 || Math.Abs(dv) >= 100)
                W($"{rows[i].R:0.00}\t{rows[i].Cells0}\t{dc2:+0;-0}\t{rows[i].OnTabTh0}\t{dt:+0;-0}\t{rows[i].InsulCells0}\t{di:+0;-0}\t{rows[i].Vol0:0.0}\t{dv:+0.0;-0.0}");
        }
        W("（只列出单步 ≥ 10 格或体积变化 ≥ 100 mm³ 的档；其余略。）");
        W("");

        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：板厚工艺下界 = DesignSpec.DiscFloorMm（屈曲项 = WeldDistortion.ForPt(1.0, PlateBucklingKFreeEdge).SlopePerB×(盘半径−孔半径)×WeldSafetyFactor）；");
        W("　按 J 的截面下角 = Solver.ApplySectionFloor；焊脚 = max(板厚, 管壁)（DesignSpec.Plate）；舌根圆角分支 = FlangePlate.HalfWidthSingle；");
        W("　舌盘交界截面分支 = SectionSizing.Cuts；台阶半径 = DesignSpec.RingRadiiOf；⑥ 下界 = GeometryScreen.MinDiscRadiusMm；");
        W("　网格 = LineRunner.PlateMeshAnalytic；管孔环 = ShellMesh.HoleTagMaxROverMm；压接退化位 = ShellMesh.ClampCoversHole／ClampIntoDisc。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.True(File.Exists(file));
    }
}

internal static class R48VRowExt
{
    public static double WorstJ(this (double R, double Floor, double KnobT0, double Td0, double Td1, string Driver0,
                                      double WeldLeg0, bool WeldByPlate0, bool Fillet, bool TangentInHole,
                                      bool R1In, bool R2In, double Need6, int Cells0, double HoleTagOver0,
                                      bool ClampCover0, bool ClampIntoDisc0, int Blend0, int InsulCells0, int OnTabTh0,
                                      double Vol0, string Worst0Where, double Worst0J, double TabLen, double XT) r) => r.Worst0J;
}
