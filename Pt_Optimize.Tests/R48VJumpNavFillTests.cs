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
//  R48 V　**跳变诊断 ⑧：把导航网格在 30.00 → 31.00 之间补齐，看它从哪一档开始跟判决网格分家** —— 2026-09-18，Opus 5
//
//  ══ 已量到的（诊断 ②③⑤）
//    盘半径  导航（细区 2.0）      判决（细区 1.0）       差（最热铂）
//    30.00   −9.460／19.922／+6.257  −9.260／19.004／+5.792   0.20 K
//    31.00   +0.903／18.587／+2.757  +27.429／0.178／−7.191  **26.53 K**
//    32.00   +42.369／−2.900／−11.340 +43.168／−3.451／−11.868 0.80 K
//    舌29.063 +32.502／−7.856／−9.276 +33.149／−8.516／−9.804 0.65 K
//    ⇒ 导航网格在**盘半径 31.0 这一点**上错了 26.5 K，其余量过的点都对得上。
//    ⇒ 本支把导航网格在 30.25／30.50／30.75 三档补上（判决网格那三档已有），
//      回答「导航从哪一档开始跟判决分家、又在哪一档回来」。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//    · 「这一档导航跟判决分家」= 同一盘半径上两张网格的「最热铂高出热偶读数」差 > 2.0 K
//      （门槛依据：已量到的三个「对得上」的点差 0.20／0.80／0.65 K，2.0 K 比它们大一倍以上）。
//    · 判不了（没解出来／未收敛）一律不当过。
//
//  ⚠ 生产代码一行未动。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VJumpNavFillTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpNavFillTests(ITestOutputHelper o) { _o = o; }

    /// <summary>「两张网格分家」的门槛 K（跑前写死；对得上的三点实测差 0.20／0.80／0.65 K）。</summary>
    private const double SplitK = 2.0;

    /// <summary>判决网格那几档的实测值（同一份复原设计，出处 deliverable/R48_跳变诊断_判决网格30到31与那堵墙_W08_本次开跑于2026-09-18_144307.txt）。</summary>
    private static readonly (double R, double Cold, double Hot, double Flux)[] Fine =
    {
        (30.00, 19.004, -9.260,  5.792),
        (30.25, 12.406,  3.110,  1.315),
        (30.50, 13.292,  3.748,  1.182),
        (30.75,  1.830, 22.464, -5.544),
        (31.00,  0.178, 27.429, -7.191),
    };

    [Fact]
    public void 跳变诊断_导航网格补齐30到31_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_导航网格补齐30到31_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_导航补齐_{stamp}_进行中.log");
        var probe = new R48VJumpLineProbeTests.Probe(live);
        var total = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ⑧：**导航网格（细区 2.0 mm）在 30.00 → 31.00 之间补齐，与判决网格逐档对照**（W08）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("判决网格那五档出自：deliverable/R48_跳变诊断_判决网格30到31与那堵墙_W08_本次开跑于2026-09-18_144307.txt（同一份复原设计、同一份代码）。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W($"　① 「这一档两张网格分家」= 同一盘半径上「最热铂高出热偶读数」差 > {SplitK:0.0} K（对得上的三点实测差 0.20／0.80／0.65 K）。");
        W("　② 判不了（没解出来／未收敛）一律不当过。");
        W("");
        Flush();

        var got = new List<(double R, double Cold, double Hot, double Flux, double Mass, int Cells, bool Ok, bool Conv, string Failed, double Gen0, double Secs)>();
        foreach (double R in new[] { 30.25, 30.50, 30.75 })
        {
            probe.Report($"── 开始 导航网格 盘半径 {R:0.00}");
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
            got.Add((R,
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.ColdUnderTc),
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.HotOverTc),
                     R48VJumpLineProbeTests.Val(r, LineResult.Key.NetFlux),
                     r.Ok ? r.TotalMassG : double.NaN, r.MeshCells, r.Ok, r.Converged,
                     r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                     r.Ok && r.Flanges.Length > 0 ? r.Flanges[0].QGenW : double.NaN,
                     sw.Elapsed.TotalSeconds));
            var last = got[^1];
            probe.Report($"── 导航 盘 {R:0.00} 结束：管根 {last.Cold:0.000}／最热铂 {last.Hot:0.000}／净流入 {last.Flux:0.000}，{sw.Elapsed.TotalSeconds:0} s");
            W($"════ 导航网格　盘半径 {R:0.00}");
            W($"　网格：细区 {lc.MeshFineMm:0.000} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm　片0单元 {r.MeshCells}");
            W($"　管根低于热偶读数 {last.Cold:0.000}　最热铂高出热偶读数 {last.Hot:0.000}　管孔净流入 {last.Flux:0.000} W"
              + $"　片0 发热 {last.Gen0:0.000} W　铂重 {last.Mass:0} g　耗时 {sw.Elapsed.TotalSeconds:0} s"
              + $"　{(!r.Ok ? "判不了（没解出来）" : !r.Converged ? "判不了（未收敛）" : r.AllOk ? "全过" : "不过：" + last.Failed)}");
            if (r.Ok && r.Flanges is { Length: > 0 })
                for (int j = 0; j < r.Flanges.Length; j++)
                {
                    var t = ThermocoupleBasis.At(r, j);
                    var f = r.Flanges[j];
                    W($"　　片{j} {t.Name}｜最热的是{t.HottestWhat} {t.HottestC:0.0}｜圆盘峰 {f.TDiscMaxC:0.0} @r={f.DiscMaxRMm:0.00} (x={f.DiscMaxXMm:0.0}) J={f.DiscMaxJAPerMm2:0.000} t={f.DiscMaxThickMm:0.000}"
                      + $"｜舌片区峰 {f.TTabMaxC:0.0} @r={f.TabMaxRMm:0.00} (x={f.TabMaxXMm:0.0}) t={f.TabMaxThickMm:0.000}｜热侧 {t.HotK:0.000}｜冷侧 {t.ColdK:0.000}"
                      + $"｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}");
                }
            W("");
            Flush();
        }

        W("═══════ 两张网格逐档对照（同一份复原设计）═══════");
        W("盘半径\t导航 最热铂\t判决 最热铂\t差\t导航 管根\t判决 管根\t差\t导航 净流入\t判决 净流入\t差\t判词");
        var navKnown = new (double R, double Cold, double Hot, double Flux)[]
        {
            (30.00, 19.922, -9.460, 6.257),   // 出处：R48_跳变诊断_盘径0.1步与逐片定位_W08_…140657.txt
            (31.00, 18.587,  0.903, 2.757),   // 同上
        };
        var all = navKnown.Concat(got.Select(g => (g.R, g.Cold, g.Hot, g.Flux))).OrderBy(x => x.R).ToList();
        foreach (var a in all)
        {
            var f = Fine.FirstOrDefault(x => Math.Abs(x.R - a.R) < 1e-9);
            if (f.R == 0) { W($"{a.R:0.00}\t{a.Hot:0.000}\t—（判决网格这一档没跑）"); continue; }
            double dh = a.Hot - f.Hot;
            W($"{a.R:0.00}\t{a.Hot:0.000}\t{f.Hot:0.000}\t{dh:+0.000;−0.000}\t{a.Cold:0.000}\t{f.Cold:0.000}\t{a.Cold - f.Cold:+0.000;−0.000}\t"
              + $"{a.Flux:0.000}\t{f.Flux:0.000}\t{a.Flux - f.Flux:+0.000;−0.000}\t"
              + $"{(Math.Abs(dh) > SplitK ? "**分家**" : "对得上")}");
        }
        W("");
        W("⚠ 30.00 与 31.00 两行的导航值取自 deliverable/R48_跳变诊断_盘径0.1步与逐片定位_W08_本次开跑于2026-09-18_140657.txt（另一次运行、同一份代码与同一份设计）；"
          + "30.25／30.50／30.75 三行是本次运行。判决网格五行全部取自 …判决网格30到31与那堵墙…144307.txt。**三份文件，不是同一次跑** —— 照规矩写明。");
        W("");
        total.Stop();
        W($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh。2026-09-18，Opus 5");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(3, got.Count);
    }
}
