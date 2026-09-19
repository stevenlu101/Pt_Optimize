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
//  R48 V　**导航网格可信带：盘半径 28 → 34 每 0.25 mm，导航与判决各解一次整线** —— 2026-09-18，Opus 5
//
//  ══ 为什么要这张表
//    诊断 ⑧（deliverable/R48_跳变诊断_导航网格补齐30到31_W08_本次开跑于2026-09-18_151028.txt）量到：
//    同一份复原设计上，导航网格（细区 2.0 mm）与判决网格（细区 1.0 mm）在盘 30.00／30.25／30.50 对得上
//    （最热铂差 0.20／0.43／0.46 K），在 30.75 与 31.00 差 −19.2／−26.5 K。
//    诊断 ③（…三档网格复测…140700.txt）在盘 31.0 上加过第三档（细区 0.5 mm）：最热铂 判决 27.429 vs 再加密 27.820
//    ⇒ **判决网格在那一点是收敛的，错的是导航网格**。
//    ⇒ 本支把「哪一段能用导航网格找路」量出来，不靠推断。
//
//  ══ 判读门槛（**跑前写死，跑完不挪**；工单给的口径）
//    · 同一盘半径上 |导航 − 判决| > 2.0 K（管根低于热偶读数 或 最热铂高出热偶读数）
//      或 > 1.0 W（管孔净流入）⇒ 这一档**导航不可信**。
//    · 三条都在门槛内 ⇒ 这一档导航可信。
//    · 任一侧判不了（没解出来／外层耦合未收敛）⇒ 这一档写「判不了」，**既不当可信也不当不可信**。
//
//  ══ 口径
//    · 只动盘半径；舌半宽填 30 不动。⚠ 盘半径 < 30 时舌半宽被几何夹住（FlangePlate.Tangent 的
//      Math.Min(舌半宽, 盘半径) 并置 HalfWidthClamped）⇒ 那些档实际上是「舌半宽 = 盘半径」的退化形状，
//      本文件逐档印出夹住与否，不当作「只动了一根」。
//    · 舌长 = √(盘半径² − 舌半宽²) + 压接段 + 自由段下界（搜形状规则，与诊断 ② 同一行）。
//    · 舌片厚与板厚下角：Solver.ApplySectionFloor 闭式重定（不手抄配方）。
//    · 网格：导航 = Solver.ApplyCaseMesh(FineMm=0, FineRadiusMm=MeshVerify.RequiredMeshFor)；
//           判决 = 同一份配方 FineMm=1.0。两支跑在**两个进程**里 ⇒ 合表时照规矩写明不是同一次跑。
//    · 每个点算完立刻落盘（长跑要放探针）。
//
//  ⚠ 生产代码一行未动。本文件只读生产件、只写 deliverable 输出。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VTrustBandTests
{
    private readonly ITestOutputHelper _o;
    public R48VTrustBandTests(ITestOutputHelper o) { _o = o; }

    /// <summary>盘半径扫描档位：28.00 → 34.00 步 0.25（25 点）。两支共用一份，不许各写一遍。</summary>
    internal static double[] Radii()
    {
        var v = new List<double>();
        for (int k = 0; k <= 24; k++) v.Add(28.0 + 0.25 * k);
        return v.ToArray();
    }

    internal const double GateK = 2.0;   // 温度类判据的分家门槛 K
    internal const double GateW = 1.0;   // 净流入的分家门槛 W

    [Fact]
    public void 可信带_导航网格_盘28到34_W08() => Sweep(0.0, "导航", Radii());

    [Fact]
    public void 可信带_判决网格_盘28到34_W08() => Sweep(1.0, "判决", Radii());

    /// <summary>
    /// 第三档（细区 0.5 mm）只在**判决网格与导航网格分家的那一段**的两端各量一点 —— 用来验
    /// 「判决网格在这一段自己是不是收敛的」这个前提。盘 31.0 那一点诊断 ③ 已经量过（27.429 vs 27.820）。
    /// 档位由环境变量 R48V_FINE05_R 给（逗号分隔），缺省 30.50,30.75。
    /// </summary>
    [Fact]
    public void 可信带_再加密复核_分家两端_W08()
    {
        string s = Environment.GetEnvironmentVariable("R48V_FINE05_R") ?? "30.50,30.75";
        var rs = s.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(t => double.Parse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Sweep(0.5, "再加密", rs);
    }

    // ══════════════════════════════════════════════════════════════════════
    private void Sweep(double fineMm, string tag, double[] radii)
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_可信带_{tag}网格_W08盘{radii.First():0.##}到{radii.Last():0.##}_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_可信带_{tag}_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string t = "") => sb.AppendLine(t);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W($"R48 V　可信带 ①：**盘半径 {radii.First():0.##} → {radii.Last():0.##} 每 {(radii.Length > 1 ? radii[1] - radii[0] : 0):0.##} mm，{tag}网格上逐点解整线**（W08）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("⚠ 基准设计的舌保温 5.1/2.3/3.6/10.4 mm 不落 0.5 格 ⇒ 只作诊断基准点，**不作可交付设计**。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W($"　① 同一盘半径上 |导航 − 判决| > {GateK:0.0} K（管根低于热偶读数 或 最热铂高出热偶读数）或 > {GateW:0.0} W（管孔净流入）⇒ **导航不可信**。");
        W("　② 三条都在门槛内 ⇒ 这一档导航可信。");
        W("　③ 任一侧判不了（没解出来／外层耦合未收敛）⇒ 写「判不了」，既不当可信也不当不可信。");
        W("　（合表与判词在另一份文件里出；本文件只负责把这一档网格的实测值逐点落下来。）");
        W("");
        W("═══════ 口径 ═══════");
        W(fineMm <= 0
            ? "网格：生产导航档配方 Solver.ApplyCaseMesh(lc, FineMm=0, FineRadiusMm=MeshVerify.RequiredMeshFor(本形状).RadiusMm)。"
            : $"网格：Solver.ApplyCaseMesh(lc, FineMm={fineMm:0.0}, FineRadiusMm=MeshVerify.RequiredMeshFor(本形状).RadiusMm) = MeshAdapt.RefineWholeMesh 整张一起缩。");
        W($"舌长 = √(盘半径² − 舌半宽²) + 压接段 {d0.ClampLengthMm:0} + 自由段下界 {GeometryScreen.FreeTabMinDefaultMm:0}（搜形状规则）。");
        W("舌片厚与板厚下角：Solver.ApplySectionFloor 闭式重定（不手抄配方）。");
        W("舌半宽一律填 30；盘半径 < 30 时被 FlangePlate.Tangent 夹成盘半径（HalfWidthClamped），逐档照印。");
        W($"温差预算：冷侧 {p.ColdUnderTcAllowK:0.#} K／热侧 {p.HotOverTcAllowK:0.#} K；圆盘保温 {d0.FlangeInsulMm:0.#} mm。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        W("═══════ 逐点实测（每点算完立刻落盘）═══════");
        W("盘半径\t舌半宽实际\t夹住?\t舌长\t细区mm\t细区半径\t片0单元\t管根低于热偶读数\t最热铂高出热偶读数\t管孔净流入W\t法兰截面J\t铂重g\t耦合剩余K\t片0发热W\t最热的是谁\t耗时s\t判词");
        Flush();

        int done = 0, blind = 0;
        foreach (double R in radii)
        {
            probe.Report($"── 开始 盘半径 {R:0.00}（{tag}）");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R;
            d.TabHalfWidthMm = 30.0;
            double wEff = Math.Min(30.0, R);
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - wEff * wEff)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
            var g0 = lc.FlangePlates[0];
            var tan = g0.Tangent();
            bool clamped = g0.HalfWidthClamped;

            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();

            bool bad = !r.Ok || !r.Converged;
            double cold = R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc);
            double hot = R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc);
            double flux = R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux);
            double secJ = R48VJumpLineProbeTests.Val(r, LineResult.Key.SectionJ);
            int cells = r.Ok && r.Flanges.Length > 0 ? r.Flanges[0].CellCount : 0;
            double gen0 = r.Ok && r.Flanges.Length > 0 ? r.Flanges[0].QGenW : double.NaN;
            string what = bad ? "—" : R48VJumpLineProbeTests.HottestWhatOf(r);
            string verdict = bad ? $"**判不了：{(!r.Ok ? r.Message : "外层耦合未收敛")} ⇒ 不当过也不当不过**"
                                 : (r.AllOk ? "全过" : "不过：" + string.Join("／", r.Checks.Where(c => !c.Ok && !c.Undetermined).Select(c => c.Name)));
            if (bad) blind++;

            W($"{R:0.00}\t{tan.HalfW:0.000}\t{(clamped ? "是" : "否")}\t{d.TabLengthMm:0.00}\t{lc.MeshFineMm:0.000}\t{lc.MeshFineRadiusMm:0.00}\t{cells}"
              + $"\t{R48VJumpLineProbeTests.F3(cold)}\t{R48VJumpLineProbeTests.F3(hot)}\t{R48VJumpLineProbeTests.F3(flux)}\t{R48VJumpLineProbeTests.F3(secJ)}"
              + $"\t{(r.Ok ? r.TotalMassG.ToString("0") : "—")}\t{r.CoupleRemainK:0.000}\t{R48VJumpLineProbeTests.F3(gen0)}\t{what}\t{sw.Elapsed.TotalSeconds:0}\t{verdict}");
            // 机读行（合表只认这一行；字段与上表一一对应）
            W($"#TSV\t{tag}\t{R:0.00}\t{(bad ? "NaN" : cold.ToString("0.000000"))}\t{(bad ? "NaN" : hot.ToString("0.000000"))}\t{(bad ? "NaN" : flux.ToString("0.000000"))}"
              + $"\t{(bad ? "NaN" : secJ.ToString("0.000000"))}\t{(r.Ok ? r.TotalMassG.ToString("0.0") : "NaN")}\t{cells}\t{(bad ? 1 : 0)}\t{(clamped ? 1 : 0)}\t{sw.Elapsed.TotalSeconds:0.0}");
            if (!bad)
                for (int j = 0; j < r.Flanges.Length; j++)
                {
                    var t = ThermocoupleBasis.At(r, j);
                    var f = r.Flanges[j];
                    W($"　　片{j} {f.Name}｜基准 {t.RefC:0.00}｜最热的是{t.HottestWhat} ｜圆盘峰 {t.DiscPeakC:0.0} @r={f.DiscMaxRMm:0.00}"
                      + $"｜舌片区峰 {t.TabPeakC:0.0} @r={f.TabMaxRMm:0.00}｜热侧 {t.HotK:0.000}｜冷侧 {t.ColdK:0.000}｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}");
                }
            done++;
            probe.Report($"── 盘 {R:0.00} 完，{sw.Elapsed.TotalSeconds:0} s（累计 {done}/{radii.Length}，判不了 {blind}）");
            Flush();
        }

        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）　跑完 {done}/{radii.Length} 点，其中判不了 {blind} 点");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（= MeshAdapt.RefineWholeMesh）；网格无关口径 = MeshVerify.RequiredMeshFor；");
        W("　舌片厚／板厚下角 = Solver.ApplySectionFloor；逐片读数 = ThermocoupleBasis.At；峰位 = FlangeOut.DiscMax*／TabMax*；夹住位 = FlangePlate.HalfWidthClamped。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(radii.Length, done);
    }
}
