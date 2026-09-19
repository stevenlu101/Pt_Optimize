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
//  R48 V　**把导航网格的轴拆到底：盘 30.50／30.75／31.00／31.01／31.25 的每一条格线** —— 2026-09-18，Opus 5
//
//  ══ 要回答的唯一问题
//    诊断 ④（…分支翻面点…144309.txt）量到：盘半径 31.00 → 31.01（+0.01 mm）
//    片0 单元 1230 → 1158、网格体积 16271.3 → 16138.3 mm³（盘变大而体积变小）、按 ThicknessAt 判成舌片的格 1062 → 988。
//    **哪一条格线动了、动了以后哪一格换了厚度或被丢掉、丢掉多少体积与发热** —— 本支逐条打出来。
//
//  ══ 三个候选机理（跑前列清楚，量完只许指其中之一或写「都不是」）
//    (a) **锚点贴边取整**：GradedAxisCentered 的锚点吸附窗口（sAnc < s + h + 0.25·hWant）在某个盘半径上翻面，
//        锚点从「拉长这一格」变成「缩短下一格」⇒ 锚点下游整排节点平移、格数 ±1。
//    (b) **格线跨过盘缘**：z 轴末格贴边（zMax = 盘半径）与 z 锚点 ±舌半宽 之间那条窄带，
//        盘半径一动带宽就变，带里的格覆盖率跨过 BuildFromField 的 25 % 丢弃线 ⇒ 整排格连料一起丢掉。
//    (c) **ThicknessAt 的格心取样**：厚度由栅格面积积分给（BuildFromField），不是格心取样 ⇒ 若是这条，
//        则格心判别只影响分块统计、不影响体积与发热。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//    · 「丢格是主因」= 31.00 → 31.01 的网格体积减少量里，**被 25 % 线丢掉的格**占 ≥ 50 %。
//    · 「锚点平移是主因」= 丢格占 < 50 %，而锚点下游节点整排平移（同一条锚点的吸附方式翻面）。
//    · 两条都不到 ⇒ 写「没找到单一主因，照印分项」。
//    · 判不了（拿不到网格／拿不到逐格发热）一律不当过。
//
//  ⚠ 生产代码一行未动。网格由 LineRunner.PlateMeshAnalytic 原样造；覆盖率用生产件公开的
//    FlangeMesher.RasterOverlap 重算（与 BuildFromField 同一份算式，不手抄配方）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VAxisDissectTests
{
    private readonly ITestOutputHelper _o;
    public R48VAxisDissectTests(ITestOutputHelper o) { _o = o; }

    private static readonly double[] Rs = { 30.50, 30.75, 31.00, 31.01, 31.25 };

    [Fact]
    public void 跳变诊断_导航网格轴拆到底_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_导航网格轴拆到底_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_轴拆_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string t = "") => sb.AppendLine(t);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑩：**导航网格（细区 2.0 mm）的轴拆到底 —— 盘 30.50／30.75／31.00／31.01／31.25 的每一条格线**（W08）");
        string mirrorOf = Environment.GetEnvironmentVariable("R48V_MIRROR_OF") ?? "";
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　编译运行目录 {HandoverDoc.Root()}"
          + (mirrorOf.Length > 0 ? $"（源码镜像自工作树 {mirrorOf}，逐字相同；镜像只为避开两支可信带长跑占住的 bin）" : "")
          + "　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「丢格是主因」= 31.00 → 31.01 网格体积减少量里，被 BuildFromField 的 25 % 覆盖线丢掉的格占 ≥ 50 %。");
        W("　② 「锚点平移是主因」= 丢格占 < 50 % 且锚点下游节点整排平移（吸附方式翻面）。");
        W("　③ 两条都不到 ⇒ 写「没找到单一主因，照印分项」。");
        W("　④ 判不了（拿不到网格或逐格发热）一律不当过。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：LineRunner.PlateMeshAnalytic(lc, 0)（= FlangeMesher.Build → BuildFromField，生产原样）；导航档 Solver.ApplyCaseMesh(FineMm=0, FineRadiusMm=RequiredMeshFor)。");
        W("轴：从网格节点还原（节点按 x 外层、z 内层张量排布 ⇒ 去重排序即两条轴，逐位就是 GradedAxisCentered 吐出来的那两条）。");
        W("锚点：FlangeMesher.AnchorsOf(板)（x = 切点 x；z = ±舌端半宽、±切点半宽）；压接边界 x = 舌尖 + 压接长 由 BuildFromField 另加。");
        W("覆盖率与丢格：FlangeMesher.RasterOverlap（生产件公开函数，与 BuildFromField 同一份算式）对 m.SourceField 重算每个 (i,j) 格；");
        W("　网格里没有的 (i,j) 若覆盖率 > 0 就是**被丢掉的格**，它的体积 = Σ 栅格重叠面积 × 栅格厚度。");
        W("厚度归属：FlangePlate.ThicknessAt 的 onTab 规则（单舌 x < 切点x），按格心判 —— 与生产逐格分块同一条线。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var snap = new List<Snap>();
        foreach (double R in Rs)
        {
            probe.Report($"── 建网格 盘半径 {R:0.00}");
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = 30.0;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - 900.0)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
            var g = lc.FlangePlates[0];
            g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
            var m = LineRunner.PlateMeshAnalytic(lc, 0);
            var s = Dissect(R, d, lc, g, m);
            snap.Add(s);

            W($"════ 盘半径 {R:0.00}　舌半宽 30（实际 {g.Tangent().HalfW:0.000}）　切点x {g.Tangent().X:0.0000}　舌长 {d.TabLengthMm:0.00}");
            W($"　板厚 {d.TabThickMm[0]:0.0000}　舌片厚 {d.TongueThickMm[0]:0.0000}　圆盘板厚工艺下界 {d.DiscFloorMm(p):0.0000}　孔半径 {g.HoleRadiusMm:0.000}");
            W($"　网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.000} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm　节点 {s.Nx} × {s.Nz}　单元 {m.CellCount}　体积 {m.VolumeMm3:0.0} mm³");
            W($"　栅格（m.SourceField）：步 {s.Step:0.0000} mm　{s.FNx} × {s.FNz}　栅格体积 {s.RasterVolume:0.0} mm³　⇒ 网格 − 栅格 = {m.VolumeMm3 - s.RasterVolume:+0.0;−0.0} mm³");
            W($"　被 25 % 覆盖线丢掉的格：{s.DroppedCells} 个，合计体积 {s.DroppedVolume:0.0} mm³（覆盖率 {s.DroppedFracMin:0.000}～{s.DroppedFracMax:0.000}）");
            W($"　x 锚点 {string.Join("、", s.XAnchors.Select(v => v.ToString("0.0000")))}（都落成节点? {(s.XAnchorsOnNode ? "是" : "**否**")}）"
              + $"；z 锚点 {string.Join("、", s.ZAnchors.Select(v => v.ToString("0.0000")))}（都落成节点? {(s.ZAnchorsOnNode ? "是" : "**否**")}）"
              + $"；压接边界 x {s.ClampX:0.0000}（落成节点? {(s.ClampOnNode ? "是" : "**否**")}）");
            W($"　按格心 ThicknessAt 判成舌片的格 {s.NOnTab}（体积 {s.VolOnTab:0.0} mm³）／判成盘的格 {s.NOnDisc}（体积 {s.VolOnDisc:0.0} mm³）");
            W("");
            W("　── x 轴格线（i｜x_i｜步长 h_i = x_{i+1}−x_i｜是不是锚点｜本列格数｜本列舌片厚格／盘格｜本列体积 mm³｜本列丢格数／丢格体积）");
            for (int i = 0; i < s.Nx; i++)
            {
                double h = i < s.Nx - 1 ? s.Xs[i + 1] - s.Xs[i] : double.NaN;
                var col = i < s.Nx - 1 ? s.Cols[i] : default;
                W($"　x{i,3}\t{s.Xs[i],10:0.0000}\t{(double.IsNaN(h) ? "—" : h.ToString("0.0000")),9}\t{s.XAnchorMark(s.Xs[i]),-6}"
                  + (i < s.Nx - 1
                     ? $"\t{col.N,4}\t{col.NTab,4}/{col.NDisc,-4}\t{col.Vol,10:0.000}\t{col.NDrop,3}/{col.VolDrop,8:0.000}"
                     : ""));
            }
            W("");
            W("　── z 轴格线（j｜z_j｜步长｜是不是锚点）");
            for (int j = 0; j < s.Nz; j++)
            {
                double h = j < s.Nz - 1 ? s.Zs[j + 1] - s.Zs[j] : double.NaN;
                W($"　z{j,3}\t{s.Zs[j],10:0.0000}\t{(double.IsNaN(h) ? "—" : h.ToString("0.0000")),9}\t{s.ZAnchorMark(s.Zs[j])}");
            }
            W("");
            if (s.DroppedList.Count > 0)
            {
                W("　── 被丢掉的格（最多印 40 个；覆盖率 < 0.25 ⇒ BuildFromField 的 continue）");
                W("　　i,j\tx 区间\tz 区间\t覆盖率\t丢掉的体积 mm³\t格心 r\t格心判 onTab?");
                foreach (var t in s.DroppedList.OrderByDescending(v => v.Vol).Take(40))
                    W($"　　{t.I},{t.J}\t[{s.Xs[t.I]:0.000}, {s.Xs[t.I + 1]:0.000}]\t[{s.Zs[t.J]:0.000}, {s.Zs[t.J + 1]:0.000}]\t{t.Frac:0.0000}\t{t.Vol:0.0000}\t{t.R:0.000}\t{(t.OnTab ? "是" : "否")}");
                W("");
            }
            Flush();
        }

        // ══════ 跨档对照
        W("═══════ ① 跨档对照（同一份代码、同一次运行）═══════");
        W("盘半径\t切点x\tzMax(=盘半径)\t节点 nx×nz\t单元\t网格体积\t栅格体积\t丢格数\t丢格体积\t舌片厚格数\t舌片厚体积\t盘格数\t盘体积");
        foreach (var s in snap)
            W($"{s.R:0.00}\t{s.TangentX:0.0000}\t{s.R:0.00}\t{s.Nx}×{s.Nz}\t{s.Cells}\t{s.MeshVolume:0.0}\t{s.RasterVolume:0.0}\t{s.DroppedCells}\t{s.DroppedVolume:0.0}\t{s.NOnTab}\t{s.VolOnTab:0.0}\t{s.NOnDisc}\t{s.VolOnDisc:0.0}");
        W("");

        W("═══════ ② 锚点附近的 x 格线怎么跟着锚点走（窗口 [切点x − 6, 切点x + 6]）═══════");
        W("盘半径\t切点x\t窗口内的 x 格线（★ = 锚点）");
        foreach (var s in snap)
        {
            var win = s.Xs.Where(v => v >= s.TangentX - 6 && v <= s.TangentX + 6)
                          .Select(v => Math.Abs(v - s.TangentX) < 1e-9 ? $"★{v:0.000}" : $"{v:0.000}");
            W($"{s.R:0.00}\t{s.TangentX:0.0000}\t{string.Join("  ", win)}");
        }
        W("");
        W("═══════ ③ 盘缘那条窄带（z 从 ±舌半宽 到 ±盘半径）═══════");
        W("盘半径\t舌半宽\t带宽=盘半径−舌半宽 mm\t带里的 z 格线\t带里有料的格数\t被丢掉的格数\t丢掉的体积 mm³\t栅格步 mm\t半个栅格步÷带宽 = 覆盖率");
        foreach (var s in snap)
            W($"{s.R:0.00}\t30.000\t{s.R - 30.0:0.0000}\t{string.Join("  ", s.Zs.Where(v => Math.Abs(v) > 30.0 - 1e-9).Select(v => v.ToString("0.000")))}"
              + $"\t{s.BandCells}\t{s.BandDropped}\t{s.BandDroppedVol:0.000}\t{s.Step:0.0000}\t{0.5 * s.Step / (s.R - 30.0):0.0000}");
        W("");
        W("　── 带里逐格（只印舌片那一段 x < 切点x 的，每档最多 12 行；「料的宽度」= 覆盖面积 ÷ 本列宽，");
        W("　　 「面长」= 网格自己吐出来的那条内部面的长度 —— 两者不等就是「面积折了面长没折」）");
        W("　盘半径\ti,j\t列宽 mm\t带宽 mm\t覆盖率\t料的宽度 mm\t面长 mm\t面长÷料宽\t留下了?");
        foreach (var s in snap)
            foreach (var b in s.BandList.Where(b => b.CX < s.TangentX).OrderBy(b => b.I).Take(12))
                W($"　{s.R:0.00}\t{b.I},{b.J}\t{(b.I + 1 < s.Nx ? s.Xs[b.I + 1] - s.Xs[b.I] : double.NaN):0.000}\t{b.BandH:0.0000}\t{b.Frac:0.0000}"
                  + $"\t{b.MatW:0.0000}\t{(double.IsNaN(b.FaceLen) ? "—" : b.FaceLen.ToString("0.0000"))}"
                  + $"\t{(double.IsNaN(b.FaceLen) || b.MatW <= 1e-12 ? "—" : (b.FaceLen / b.MatW).ToString("0.00"))}\t{(b.Kept ? "是" : "否")}");
        W("");
        W("　── 带里合计（舌片那一段）");
        W("　盘半径\t带里舌段格数\t留下的\t丢掉的\t留下的料宽合计 mm\t留下的面长合计 mm\t面长÷料宽");
        foreach (var s in snap)
        {
            var seg = s.BandList.Where(b => b.CX < s.TangentX).ToList();
            var kept = seg.Where(b => b.Kept && !double.IsNaN(b.FaceLen)).ToList();
            double mw = kept.Sum(b => b.MatW), fl2 = kept.Sum(b => b.FaceLen);
            W($"　{s.R:0.00}\t{seg.Count}\t{seg.Count(b => b.Kept)}\t{seg.Count(b => !b.Kept)}\t{mw:0.000}\t{fl2:0.000}\t{(mw > 1e-9 ? (fl2 / mw).ToString("0.00") : "—")}");
        }
        W("⚠ 面长不按覆盖率折算是**生产的口径**：ShellMesh.ScaleInteriorFacesByCoverage 写在那里但**没有任何调用点**");
        W("　（ShellMesh.cs 第 254 行注释「这里**不调**」，2026-09-13 实测后决定不用）。⇒ 带里留下的格按整条边导电导热。");
        W("");
        Flush();

        // ══════ 31.00 vs 31.01 的逐格发热（真解整线，导航网格）
        W("═══════ ④ 31.00 与 31.01 的逐格发热（真解整线，导航网格，同一进程）═══════");
        var heat = new List<(double R, bool Ok, string Msg, double Gen0, double GenTab, double GenDisc, int NTab, int NDisc, double VolTab, double VolDisc, double Cold, double Hot, double Flux, int Cells)>();
        foreach (double R in new[] { 31.00, 31.01 })
        {
            probe.Report($"── 解整线 盘半径 {R:0.00}（导航网格）");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R; d.TabHalfWidthMm = 30.0;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - 900.0)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();
            probe.Report($"── 盘 {R:0.00} 解完 {sw.Elapsed.TotalSeconds:0} s");
            if (!r.Ok || !r.Converged || r.Flanges.Length == 0 || r.Flanges[0].Mesh is null
                || r.Flanges[0].CellGenW.Length != r.Flanges[0].Mesh!.CellCount)
            { heat.Add((R, false, !r.Ok ? r.Message : "外层耦合未收敛或拿不到逐格发热", 0, 0, 0, 0, 0, 0, 0, double.NaN, double.NaN, double.NaN, 0)); W($"　盘 {R:0.00}：**判不了**（{(!r.Ok ? r.Message : "未收敛／拿不到逐格发热")}）⇒ 不当过也不当不过"); Flush(); continue; }
            var f0 = r.Flanges[0]; var mm = f0.Mesh!;
            double xT = lc.FlangePlates[0].Tangent().X;
            double gTab = 0, gDisc = 0, vTab = 0, vDisc = 0; int nTab = 0, nDisc = 0; bool nan = false;
            for (int i = 0; i < mm.CellCount; i++)
            {
                double gw = f0.CellGenW[i];
                if (double.IsNaN(gw)) { nan = true; continue; }
                double vol = mm.Area[i] * mm.Thickness[i];
                if (mm.Centroid[i].X < xT) { gTab += gw; vTab += vol; nTab++; }
                else { gDisc += gw; vDisc += vol; nDisc++; }
            }
            if (nan) W($"　盘 {R:0.00}：**逐格发热有 NaN ⇒ 这一档判不了**");
            heat.Add((R, !nan, "", f0.QGenW, gTab, gDisc, nTab, nDisc, vTab, vDisc,
                      R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc),
                      R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc),
                      R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux), mm.CellCount));
            W($"　盘 {R:0.00}｜片0 单元 {mm.CellCount}｜片0 发热 {f0.QGenW:0.000} W｜按格心判舌片厚 {nTab} 格 发热 {gTab:0.000} W 体积 {vTab:0.0} mm³"
              + $"｜判盘 {nDisc} 格 发热 {gDisc:0.000} W 体积 {vDisc:0.0} mm³｜网格体积 {mm.VolumeMm3:0.0} mm³"
              + $"｜管根 {R48VJumpLineProbeTests.F3(R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc))}"
              + $"｜最热铂 {R48VJumpLineProbeTests.F3(R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc))}"
              + $"｜净流入 {R48VJumpLineProbeTests.F3(R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux))}｜耗时 {sw.Elapsed.TotalSeconds:0} s");
            Flush();
        }
        if (heat.Count == 2 && heat[0].Ok && heat[1].Ok)
        {
            var a = heat[0]; var b = heat[1];
            W($"　Δ（31.01 − 31.00）：片0 发热 {b.Gen0 - a.Gen0:+0.000;−0.000} W　舌片厚那块 {b.GenTab - a.GenTab:+0.000;−0.000} W（格 {b.NTab - a.NTab:+0;-0}，体积 {b.VolTab - a.VolTab:+0.0;−0.0} mm³）"
              + $"　盘那块 {b.GenDisc - a.GenDisc:+0.000;−0.000} W（格 {b.NDisc - a.NDisc:+0;-0}，体积 {b.VolDisc - a.VolDisc:+0.0;−0.0} mm³）");
        }
        W("");

        // ══════ 判读
        W("═══════ ⑤ 判读（按跑前写死的门槛）═══════");
        var s100 = snap.First(v => Math.Abs(v.R - 31.00) < 1e-9);
        var s101 = snap.First(v => Math.Abs(v.R - 31.01) < 1e-9);
        double dVol = s101.MeshVolume - s100.MeshVolume;
        double dDrop = s101.DroppedVolume - s100.DroppedVolume;
        double share = Math.Abs(dVol) > 1e-9 ? 100.0 * (-dDrop) / dVol : double.NaN;
        W($"　31.00 → 31.01：网格体积 {s100.MeshVolume:0.0} → {s101.MeshVolume:0.0}（{dVol:+0.0;−0.0} mm³）；"
          + $"丢格体积 {s100.DroppedVolume:0.0} → {s101.DroppedVolume:0.0}（{dDrop:+0.0;−0.0} mm³）；"
          + $"栅格体积 {s100.RasterVolume:0.0} → {s101.RasterVolume:0.0}（{s101.RasterVolume - s100.RasterVolume:+0.0;−0.0} mm³，= 真几何该变的量）");
        W($"　丢格在体积减少量里占 {share:0.0} %　⇒ {(share >= 50 ? "**(b) 丢格（25 % 覆盖线）是主因**" : "丢格不到一半")}");
        W($"　锚点：31.00 切点x {s100.TangentX:0.0000}、31.01 {s101.TangentX:0.0000}（差 {s101.TangentX - s100.TangentX:+0.0000;−0.0000} mm）；"
          + $"锚点下游 x 节点整排平移量（取锚点后第 3 个节点）= {s101.NodeAfterAnchor(3) - s100.NodeAfterAnchor(3):+0.0000;−0.0000} mm");
        W($"　x 节点数 {s100.Nx} → {s101.Nx}；z 节点数 {s100.Nz} → {s101.Nz}");
        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：网格 = LineRunner.PlateMeshAnalytic（= FlangeMesher.Build → BuildFromField）；轴 = GradedAxisCentered（从节点还原）；");
        W("　锚点 = FlangeMesher.AnchorsOf；覆盖率 = FlangeMesher.RasterOverlap（生产件公开函数）；厚度归属 = FlangePlate.ThicknessAt 的 onTab；");
        W("　逐格发热 = FlangeOut.CellGenW；整线解 = LineRunner.Run。本文件里每一个数都来自这一次运行。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(Rs.Length, snap.Count);
    }

    /// <summary>
    /// ★ 只量几何、不解场（秒级）：**三档网格上「盘缘窄带那一排格」的翻面点**。
    /// 上一个用例在盘 31 附近量到的机理若成立，它的闭式就是
    ///   带里覆盖率 = (栅格步 ÷ 2) ÷ (盘半径 − 舌半宽)，而 BuildFromField 丢弃线是 0.25
    ///   ⇒ **盘半径 − 舌半宽 > 2 × 栅格步 = 细区网格 ÷ 2 就整排丢掉**（栅格步 = FlangeMesher.RasterStepFor = 细区网格 ÷ 4）。
    /// 本用例把三档细区网格 × 盘半径 28～34 每 0.25 都量一遍，**预测与实测同表**，对不上就当场看得见。
    /// </summary>
    [Fact]
    public void 跳变诊断_三档网格的丢格翻面点_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_三档网格的丢格翻面点_W08_本次开跑于{stamp}.txt");
        var sb = new StringBuilder();
        void W(string t = "") => sb.AppendLine(t);
        var total = Stopwatch.StartNew();

        W("R48 V　跳变诊断 ⑪：**盘缘窄带那一排格在三档网格上各在哪里翻面**（W08，只量几何、不解场）");
        string mirrorOf = Environment.GetEnvironmentVariable("R48V_MIRROR_OF") ?? "";
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　编译运行目录 {HandoverDoc.Root()}"
          + (mirrorOf.Length > 0 ? $"（源码镜像自工作树 {mirrorOf}，逐字相同）" : "") + "　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 闭式预测「整排丢掉」= (栅格步 ÷ 2) ÷ (盘半径 − 舌半宽) < 0.25，即 盘半径 − 舌半宽 > 细区网格 ÷ 2。");
        W("　② 实测与预测**逐档一致** ⇒ 机理成立；有一档不一致 ⇒ 机理不成立，照印不一致的那一档。");
        W("　③ 盘半径 ≤ 舌半宽（舌半宽被夹成盘半径）⇒ 没有这条带，写「无带」，不算可信也不算不可信。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：LineRunner.PlateMeshAnalytic（生产原样），细区尺寸由 Solver.ApplyCaseMesh 给；栅格步 = FlangeMesher.RasterStepFor(细区)。");
        W("「带」= z ∈ (舌半宽, 盘半径] 那一排格（两侧各一排）；只数**舌片那一段**（格心 x < 切点x）。");
        W("丢弃线 = BuildFromField 的 covered < 0.25 × 整格面积。");
        W("");
        W("细区mm\t盘半径\t舌半宽实际\t带宽\t栅格步\t预测覆盖率\t预测整排丢?\t实测带里舌段格数\t实测留下\t实测丢掉\t网格体积 mm³\t单元\t一致?");
        foreach (double fine in new[] { 2.0, 1.0, 0.5 })
            foreach (double R in R48VTrustBandTests.Radii())
            {
                var d = d0.Clone();
                d.DiscRadiusMm = R; d.TabHalfWidthMm = 30.0;
                double wEff = Math.Min(30.0, R);
                d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - wEff * wEff)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
                var dummy = new SolverResult { Design = d };
                Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
                var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fine <= 2.0 - 1e-9 ? fine : 0.0, FineRadiusMm = reqRadius });
                var g = lc.FlangePlates[0];
                g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
                var m = LineRunner.PlateMeshAnalytic(lc, 0);
                var s = Dissect(R, d, lc, g, m);
                double band = R - wEff;
                double step = FlangeMesher.RasterStepFor(lc.MeshFineMm);
                var seg = s.BandList.Where(b => b.CX < s.TangentX).ToList();
                if (band <= 1e-9)
                { W($"{lc.MeshFineMm:0.000}\t{R:0.00}\t{wEff:0.000}\t0\t{step:0.0000}\t—\t无带\t{seg.Count}\t{seg.Count(b => b.Kept)}\t{seg.Count(b => !b.Kept)}\t{m.VolumeMm3:0.0}\t{m.CellCount}\t无带"); continue; }
                double predFrac = 0.5 * step / band;
                bool predDrop = predFrac < 0.25 - 1e-12;
                bool gotDrop = seg.Count > 0 && seg.All(b => !b.Kept);
                bool gotKeep = seg.Count > 0 && seg.Count(b => b.Kept) >= seg.Count - 2;   // 两端那一两格压在盘弧上，单独算
                string agree = seg.Count == 0 ? "没有舌段带格"
                             : predDrop == gotDrop && predDrop != gotKeep ? "一致"
                             : "**不一致**";
                W($"{lc.MeshFineMm:0.000}\t{R:0.00}\t{wEff:0.000}\t{band:0.0000}\t{step:0.0000}\t{predFrac:0.0000}\t{(predDrop ? "丢" : "留")}"
                  + $"\t{seg.Count}\t{seg.Count(b => b.Kept)}\t{seg.Count(b => !b.Kept)}\t{m.VolumeMm3:0.0}\t{m.CellCount}\t{agree}");
            }
        W("");
        W("═══════ 翻面点（闭式）═══════");
        foreach (double fine in new[] { 2.0, 1.0, 0.5 })
            W($"　细区 {fine:0.0} mm ⇒ 栅格步 {FlangeMesher.RasterStepFor(fine):0.000} mm ⇒ 盘半径 − 舌半宽 > {2 * FlangeMesher.RasterStepFor(fine):0.000} mm 就整排丢掉");
        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：网格 = LineRunner.PlateMeshAnalytic；栅格步 = FlangeMesher.RasterStepFor；覆盖率 = FlangeMesher.RasterOverlap；");
        W("　丢弃线 = ShellMesh.cs BuildFromField 的 covered < 0.25 × cellArea。本文件里每一个数都来自这一次运行。2026-09-18，Opus 5");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Assert.True(sb.Length > 0);
    }

    // ══════════════════════════════════════════════════════════════════════
    private readonly record struct Col(int N, int NTab, int NDisc, double Vol, int NDrop, double VolDrop);
    private readonly record struct Drop(int I, int J, double Frac, double Vol, double R, bool OnTab);
    /// <summary>盘缘窄带（|z| 从舌半宽到盘半径）里的一格：留下了没有、覆盖率、料的真实宽度、这一格与 +x 邻格之间那条内部面的面长。</summary>
    private readonly record struct BandCell(int I, int J, bool Kept, double Frac, double MatW, double BandH, double FaceLen, double CX);

    private sealed class Snap
    {
        public double R, TangentX, MeshVolume, RasterVolume, DroppedVolume, Step;
        public double DroppedFracMin = double.NaN, DroppedFracMax = double.NaN;
        public int Nx, Nz, FNx, FNz, Cells, DroppedCells, NOnTab, NOnDisc, BandCells, BandDropped;
        public double VolOnTab, VolOnDisc, BandDroppedVol, ClampX;
        public double[] Xs = Array.Empty<double>(), Zs = Array.Empty<double>();
        public double[] XAnchors = Array.Empty<double>(), ZAnchors = Array.Empty<double>();
        public bool XAnchorsOnNode, ZAnchorsOnNode, ClampOnNode;
        public Col[] Cols = Array.Empty<Col>();
        public List<Drop> DroppedList = new();
        public List<BandCell> BandList = new();
        public string XAnchorMark(double x) => XAnchors.Any(a => Math.Abs(a - x) < 1e-9) ? "★锚点"
                                             : Math.Abs(x - ClampX) < 1e-9 ? "★压接" : "";
        public string ZAnchorMark(double z) => ZAnchors.Any(a => Math.Abs(a - z) < 1e-9) ? "★锚点" : "";
        /// <summary>锚点（切点 x）之后第 k 个 x 节点（沿 −x 方向）—— 用来量「锚点下游整排平移了多少」。</summary>
        public double NodeAfterAnchor(int k)
        {
            var below = Xs.Where(v => v < TangentX - 1e-9).OrderByDescending(v => v).ToArray();
            return k - 1 < below.Length ? below[k - 1] : double.NaN;
        }
    }

    private static Snap Dissect(double R, DesignSpec d, LineCase lc, FlangePlate g, ShellMesh m)
    {
        var s = new Snap { R = R, TangentX = g.Tangent().X, MeshVolume = m.VolumeMm3, Cells = m.CellCount };
        s.Xs = m.Nodes.Select(n => n.X).Distinct().OrderBy(v => v).ToArray();
        s.Zs = m.Nodes.Select(n => n.Z).Distinct().OrderBy(v => v).ToArray();
        s.Nx = s.Xs.Length; s.Nz = s.Zs.Length;
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        s.XAnchors = xa.OrderBy(v => v).ToArray(); s.ZAnchors = za.OrderBy(v => v).ToArray();
        bool OnNode(double v) => s.Xs.Any(x => Math.Abs(x - v) <= 1e-9);
        bool OnNodeZ(double v) => s.Zs.Any(z => Math.Abs(z - v) <= 1e-9);
        s.XAnchorsOnNode = s.XAnchors.All(a => a <= s.Xs[0] + 1e-9 || a >= s.Xs[^1] - 1e-9 || OnNode(a));
        s.ZAnchorsOnNode = s.ZAnchors.All(a => a <= s.Zs[0] + 1e-9 || a >= s.Zs[^1] - 1e-9 || OnNodeZ(a));
        s.ClampX = s.Xs[0] + lc.Base.BusbarClampLengthMm;
        s.ClampOnNode = OnNode(s.ClampX);

        var f = m.SourceField;
        if (f is null) return s;
        s.Step = f.Step; s.FNx = f.Nx; s.FNz = f.Nz;
        s.RasterVolume = f.T.Sum() * f.Step * f.Step;

        // 网格里真有的 (i,j)：节点按 x 外层、z 内层张量排 ⇒ 第一个节点索引 = i*nz + j
        var have = new HashSet<long>();
        var cellOf = new Dictionary<long, int>();
        for (int k = 0; k < m.CellCount; k++)
        {
            int n0 = m.Cells[k][0];
            int i = n0 / s.Nz, j = n0 % s.Nz;
            have.Add((long)i * 100000 + j);
            cellOf[(long)i * 100000 + j] = k;
        }
        // 内部面按「两侧单元」索引，量的是**网格自己吐出来的面长**（不是我算的）
        var faceLen = new Dictionary<long, double>();
        foreach (var fc in m.Faces)
        {
            if (fc.B < 0) continue;
            long key = (long)Math.Min(fc.A, fc.B) * 1000000 + Math.Max(fc.A, fc.B);
            faceLen[key] = fc.Length;
        }

        var cols = new Col[s.Nx - 1];
        double xT = s.TangentX;
        var ovZ = new List<(int k, double ov)>[s.Nz - 1];
        for (int j = 0; j < s.Nz - 1; j++) ovZ[j] = FlangeMesher.RasterOverlap(s.Zs[j], s.Zs[j + 1], f.Z0, f.Nz, f.Step);
        for (int i = 0; i < s.Nx - 1; i++)
        {
            var ovX = FlangeMesher.RasterOverlap(s.Xs[i], s.Xs[i + 1], f.X0, f.Nx, f.Step);
            int n = 0, nt = 0, nd = 0, ndrop = 0; double vol = 0, voldrop = 0;
            for (int j = 0; j < s.Nz - 1; j++)
            {
                double covered = 0, volume = 0;
                foreach (var (ix, ox) in ovX)
                    foreach (var (iz, oz) in ovZ[j])
                    {
                        double t = f.T[ix * f.Nz + iz];
                        if (t <= 1e-9) continue;
                        double a = ox * oz;
                        covered += a; volume += a * t;
                    }
                if (covered <= 1e-12) continue;
                double cellArea = (s.Xs[i + 1] - s.Xs[i]) * (s.Zs[j + 1] - s.Zs[j]);
                double frac = covered / cellArea;
                double cx = 0.5 * (s.Xs[i] + s.Xs[i + 1]), cz = 0.5 * (s.Zs[j] + s.Zs[j + 1]);
                bool onTab = cx < xT;
                if (have.Contains((long)i * 100000 + j))
                {
                    n++; vol += volume;
                    if (onTab) { nt++; s.NOnTab++; s.VolOnTab += volume; } else { nd++; s.NOnDisc++; s.VolOnDisc += volume; }
                }
                else
                {
                    ndrop++; voldrop += volume; s.DroppedCells++; s.DroppedVolume += volume;
                    s.DroppedList.Add(new Drop(i, j, frac, volume, Math.Sqrt(cx * cx + cz * cz), onTab));
                    s.DroppedFracMin = double.IsNaN(s.DroppedFracMin) ? frac : Math.Min(s.DroppedFracMin, frac);
                    s.DroppedFracMax = double.IsNaN(s.DroppedFracMax) ? frac : Math.Max(s.DroppedFracMax, frac);
                }
                if (Math.Abs(cz) > 30.0)
                {
                    s.BandCells++;
                    bool kept = have.Contains((long)i * 100000 + j);
                    if (!kept) { s.BandDropped++; s.BandDroppedVol += volume; }
                    double colW = s.Xs[i + 1] - s.Xs[i], bandH = s.Zs[j + 1] - s.Zs[j];
                    double fl = double.NaN;
                    if (kept && cellOf.TryGetValue((long)i * 100000 + j, out int ca)
                             && cellOf.TryGetValue((long)(i + 1) * 100000 + j, out int cb)
                             && faceLen.TryGetValue((long)Math.Min(ca, cb) * 1000000 + Math.Max(ca, cb), out double v))
                        fl = v;
                    s.BandList.Add(new BandCell(i, j, kept, frac, covered / colW, bandH, fl, cx));
                }
            }
            cols[i] = new Col(n, nt, nd, vol, ndrop, voldrop);
        }
        s.Cols = cols;
        return s;
    }
}
