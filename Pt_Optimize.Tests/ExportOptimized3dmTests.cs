using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **优化完出一版 .3dm 给用户看**（用户 2026-09-05）。
///
/// 用的是 Pt_Heater1.3dm 交接到解析路之后的那个构型 ——
/// 也就是「③ 法兰增量温降 427 K / 限 10」那个**用不了的法兰**。
/// 让求解器**组合使用**全部旋钮（板厚／舌保温／t₁/t₂／r₁/r₂／圆盘槽／孔径／孔拉长），
/// 然后把结果写成可回读的 .3dm。
///
/// ⚠ 这不是回归门 —— 它是**交付物**。断言只钉「真的解出来了、图真的写出来了」。
/// </summary>
// ★ 2026-09-09：起 Rhino 子进程的测试类**串行**（同一 xunit collection）—— 并行起两个 RhinoCore 会互相挂死到 10 分钟超时（合并 R12/R13 时抓到，单跑 12 s 过）
[Collection("Rhino 子进程")]
public class ExportOptimized3dmTests
{
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 出一版优化后的3dm()
    {
        var baseIn = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        // Pt_Heater1 交接过来的几何（见 deliverable/F_成对后.txt 第 4 步）
        d.Name = "Pt_Heater1 优化版";
        // ★★★★★ **盘径由约束算出来，不由我拍**（2026-09-06 第二次修）。
        //
        //   第一次我解 ⑥ 的不动点得 R27、取 R35 —— **交出来的图没有圆盘**：
        //   盘直径 70 < 舌全宽 80，零件退化成一块开了孔的矩形板，
        //   而解析侧（TabParallel ⇒ HalfWidth 恒定）与出图侧（BodyOutline 走错分支）
        //   各自都自洽地算/画了两个不同的错东西。
        //   ⇒ 现在 GeometryScreen.MinDiscRadiusMm 同时管住「盖得住管孔+焊脚」与
        //     「不比舌片窄」，盘径从它取，**不再手填**。
        //
        //   为什么仍不用图纸的 R60：R60 ⇒ 无支撑宽 34 ⇒ 焊接屈曲下界 4.709 mm
        //   ⇒ 四片全被顶到下界、③ 差 42 倍，怎么调旋钮都过不了
        //   （deliverable/盘径不动点.txt、四片为什么等厚.txt）。
        d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        {
            // 盘径与板厚互相咬着（缩盘径 → 无支撑宽小 → 板厚下界降 → 焊脚小 → 需要的盘更小），
            // 迭代到不动点；上取整到 0.5 mm 的图纸格。
            double rr = d.DiscRadiusMm;
            for (int it = 0; it < 12; it++)
            {
                d.DiscRadiusMm = rr;
                double tf = d.DiscFloorMm(baseIn);
                for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = tf;
                double need = GeometryScreen.MinDiscRadiusMm(
                    Enumerable.Range(0, d.TabThickMm.Length).Select(j => d.Plate(j, tf)).ToArray());
                double nxt = Math.Ceiling(need / 0.5 - 1e-9) * 0.5;
                if (Math.Abs(nxt - rr) < 1e-9) break;
                rr = nxt;
            }
            d.DiscRadiusMm = rr;
        }
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 2.0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sr = Solver.Solve(d, baseIn, new SolverOptions { MaxRounds = 24 });
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine("═══ 优化后出图（Pt_Heater1 构型）═══");
        sb.AppendLine($"求解 {sw.Elapsed.TotalMinutes:0.0} 分钟　可行={sr.Feasible}　"
                    + $"合计 {(double.IsNaN(sr.MassG) ? "—" : sr.MassG.ToString("0"))} g");
        sb.AppendLine(sr.Message);
        sb.AppendLine();

        var o = sr.Design ?? d;

        // ★★★★ **九根旋钮全印出来，一根都不许少**（2026-09-06 用户：「r₁/r₂ 完全没有优化」）。
        //   在此之前这张表只有七列 —— **r₁/r₂ 根本没印**，
        //   于是「它们有没有被优化」这个问题，看报表**答不出来**。
        //   决定依据不落到人手上就等于没有依据（HANDOVER §0.-3）。
        //   r₁/r₂ 走 RingRadiiOf(j)，与出图 spec 的 ringR **同源**，不另算一份。
        double floorMm = o.DiscFloorMm(baseIn);
        sb.AppendLine($"板厚下界（焊接屈曲）= {floorMm:0.000} mm"
                    + "　← 板厚等于它 ⇒ 这根旋钮**没被抬过**，印的是制造下界，不是优化结果");
        sb.AppendLine("求解器选了什么（逐片，九根旋钮全在）：");
        sb.AppendLine("片\t板厚 mm\t舌保温 mm\t环倍率 t₁\t外级 t₂\t内级 r₁\t外级 r₂"
                    + "\t圆盘槽 °\t孔径 mm\t孔拉长");
        string[] nm = { "入口", "共用1", "共用2", "出口" };
        for (int j = 0; j < o.TabThickMm.Length; j++)
        {
            var rr = o.RingRadiiOf(j);                       // 与出图 spec 的 ringR 同一份
            bool atFloor = Math.Abs(o.TabThickMm[j] - floorMm) < 5e-4;
            sb.AppendLine($"{(j < nm.Length ? nm[j] : "片" + j)}"
                        + $"\t{o.TabThickMm[j]:0.000}{(atFloor ? "*" : "")}\t{o.TabInsulMm[j]:0.00}"
                        + $"\t{o.RingMul[j]:0.00}\t{o.RingMulOuter(j):0.00}"
                        + $"\t{rr[0] - o.HoleRadiusMm:0.00}\t{rr[1] - o.HoleRadiusMm:0.00}"
                        + $"\t{(j < o.SlotSpanDeg.Length ? o.SlotSpanDeg[j] : 0):0}"
                        + $"\t{(j < o.TabHoleRMm.Length ? o.TabHoleRMm[j] : 0):0.0}"
                        + $"\t{(j < o.TabHoleAspect.Length ? o.TabHoleAspect[j] : 1):0.0}");
        }
        sb.AppendLine("* = 卡在板厚下界上（没被抬过）；r₁/r₂ 是**环宽**（从管孔边算起）");

        // ★★★★★ **把图直接印进报告**（2026-09-06 用户：「上面的结果都是你报给我的」）。
        //
        //   在此之前，这张图长什么样只有两个途径：开 Rhino，或者听我转述。
        //   而我转述过「孔的面积 4064 mm² 对得上 π·36²」——数对，方案却是荒唐的
        //   （四片只有一片有孔、四片厚度一模一样），是用户开 Rhino 才看出来的。
        //
        //   ⇒ 用 PlateCurrent2D.Inside(x, z) —— **求解器判「这里有没有金属」的同一个判据** ——
        //     把每一片栅格化成字符画印进报告。
        //     图不对 = 解错了；图对而 .3dm 不对 = 写图器错了。两件事就此分得开。
        sb.AppendLine();
        sb.AppendLine("逐片俯视图（由 Inside(x,z) 直接栅格化 —— 与求解器同一个判据）：");
        double floorAsc = o.DiscFloorMm(baseIn);
        for (int j = 0; j < o.TabThickMm.Length; j++)
        {
            var g = o.Plate(j, floorAsc);
            sb.AppendLine();
            sb.AppendLine($"── {(j < nm.Length ? nm[j] : "片" + j)}　"
                        + $"盘R {g.DiscRadiusMm:0.#}　孔R {g.HoleRadiusMm:0.#}　"
                        + $"舌 {-g.TabEndXMm:0.#}×{2 * g.TabEndHalfWidthMm:0.#}　"
                        + $"槽 {(j < o.SlotSpanDeg.Length ? o.SlotSpanDeg[j] : 0):0}°　"
                        + $"孔 R{(j < o.TabHoleRMm.Length ? o.TabHoleRMm[j] : 0):0.#}"
                        + $"×{(j < o.TabHoleAspect.Length ? o.TabHoleAspect[j] : 1):0.#}");
            foreach (var row in Ascii(g))
                sb.AppendLine("   " + row);
        }

        // ★★★★★ **比价过程要落到交付物**（2026-09-05 用户看图问「槽为什么没开」）。
        //   在此之前 sr.Trace 全在内存里，报表上只有结论 ——
        //   于是「圆盘槽 0°」到底是**没被选**还是**没得选**，谁也看不出来，
        //   我只能再花一小时重跑去猜。决定依据不落到人手上，就等于没有依据。
        sb.AppendLine();
        sb.AppendLine("求解器每一轮怎么挑的（比价与淘汰理由）：");
        foreach (var line in sr.Trace.Where(t => t.Contains("比价") || t.Contains("淘汰")
                                              || t.Contains("候选") || t.Contains("抬**")
                                              || t.Contains("留着")))
            sb.AppendLine("  " + line.Trim());
        sb.AppendLine();

        // ── 出图：每片一张可回读的 .3dm
        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable", "优化后3dm");
        Directory.CreateDirectory(outDir);
        sb.AppendLine();
        sb.AppendLine("出图：");
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { sb.AppendLine("  ⚠ 没有 Rhino 探针，跳过出图"); }
        else
        {
            // ★★★★★ **交付件走 APP 自己那条路**（2026-09-05，用户「我要的是 APP 自己能出结果图」）。
            //   WriteFinal3dm 就是「导出本页 3DM」按钮调的那一个 —— 整机、逐片分层、
            //   槽与孔都在。此前这里只写 WriteStepped3dm（单层、逐片、**丢掉孔的拉长比**），
            //   那是我读图用的仪器，不是工程师拿到的东西。
            string whole = Path.Combine(outDir, "整机.3dm");
            string log = Geometry3dm.WriteFinal3dm(o, whole);
            sb.AppendLine($"  整机.3dm　{(File.Exists(whole) ? new FileInfo(whole).Length / 1024 + " KB" : "✗ 没写出来")}"
                        + "　←「导出本页 3DM」按钮出的就是这个");
            Assert.True(File.Exists(whole), "整机图没写出来");

            double floor = o.DiscFloorMm(baseIn);
            for (int j = 0; j < o.TabThickMm.Length; j++)
            {
                double td = Math.Max(o.TabThickMm[j], floor);
                var radii = o.RingRadiiMm.Concat(new[] { o.DiscRadiusMm }).ToArray();
                var thick = new[] { td * o.RingMul[j], td * o.RingMulOuter(j), td };
                string file = Path.Combine(outDir, $"{(j < nm.Length ? nm[j] : "片" + j)}.3dm");
                var band = o.SlotBandMm(Math.Max(td, o.WallMm));
                Geometry3dm.WriteStepped3dm(file, o.HoleRadiusMm, radii, thick,
                    -o.TabLengthMm, o.TabHalfWidthMm, td,
                    slotCount: (j < o.SlotSpanDeg.Length && o.SlotSpanDeg[j] > 0.5) ? 1 : 0,
                    slotWidthDeg: j < o.SlotSpanDeg.Length ? o.SlotSpanDeg[j] : 0,
                    slotRInMm: band.RIn, slotROutMm: band.ROut,
                    tabHoleXMm: o.TabHoleCenterXMm(),
                    tabHoleRMm: j < o.TabHoleRMm.Length ? o.TabHoleRMm[j] : 0);
                sb.AppendLine($"  {Path.GetFileName(file)}　"
                            + $"{(File.Exists(file) ? new FileInfo(file).Length / 1024 + " KB" : "✗ 没写出来")}");
                Assert.True(File.Exists(file), "图没写出来：" + file);
            }
        }

        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "优化后出图.txt"), sb.ToString());
    }

    /// <summary>
    /// 把一片法兰栅格化成字符画。**唯一的判据是 <c>Inside(x, z)</c>** ——
    /// 不另写一份「哪里有料」，那样迟早会与求解器不一致。
    /// 列 = x（右为 +x，圆盘那头），行 = z（上为 +z）。'#' 有料、' ' 无料。
    /// </summary>
    private static System.Collections.Generic.List<string> Ascii(FlangePlate g)
    {
        const int cols = 96, rows = 33;
        double x0 = g.TabEndXMm, x1 = g.DiscRadiusMm;
        double zMax = Math.Max(g.DiscRadiusMm, g.TabEndHalfWidthMm) * 1.05;
        var outp = new System.Collections.Generic.List<string>();
        for (int r = 0; r < rows; r++)
        {
            double z = zMax - 2.0 * zMax * r / (rows - 1);
            var line = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                double x = x0 + (x1 - x0) * c / (cols - 1.0);
                line.Append(g.Inside(x, z) ? '#' : ' ');
            }
            outp.Add(line.ToString().TrimEnd());
        }
        // 全空的边缘行去掉，图才紧凑
        while (outp.Count > 0 && outp[0].Length == 0) outp.RemoveAt(0);
        while (outp.Count > 0 && outp[^1].Length == 0) outp.RemoveAt(outp.Count - 1);
        return outp;
    }
}
