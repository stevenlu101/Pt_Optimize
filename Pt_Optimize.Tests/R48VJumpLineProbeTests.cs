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
//  R48 V　**跳变诊断 ②：盘半径 31 → 32 按 0.1 步解整线，并逐片定位峰在哪** —— 2026-09-18，Opus 5
//
//  ══ 要查的事实（deliverable/R48_形状探针_方向实测_W08_本次开跑于2026-09-18_090213.txt）
//    盘 31 → 32 一步：管根 18.587 → −2.900、最热铂 0.903 → 42.369、净流入 +2.757 → −11.340。
//    1 mm 盘径把「最热铂高出热偶读数」动 41 K —— 要先看清那 41 K 落在哪片哪区。
//
//  ══ 本支做两件事（一次运行、同一进程、同一份代码）
//    ① **定位**：每个点把逐片的热偶读数基准、圆盘峰／舌片区峰／管根两端、最热的是谁、峰位 r 与 x,z、
//       当地 J、当地板厚、当地保温厚、逐片管孔净流入与能量分项（发热／表面散热／进铜排）全印出来。
//    ② **找切换**：盘半径 31.0 → 32.0 按 0.1 步（11 点）+ 基准 30.0，只动盘半径解整线，
//       看三条判据在哪一小段翻面。
//    ③ **顺手核**：舌半宽 30 → 29.063 那一跳同法只做定位（1 点）。
//
//  ══ 判读门槛（**跑前写死，跑完不挪**）
//    · 「跳变」= 相邻 0.1 档之间某条判据的变化 > 该段其余相邻变化中位数的 5 倍。
//    · 判不了（没解出来／外层耦合未收敛）**一律不当过**，照印。
//    · 峰位只报**解出来的那一格**（形心 x,z 与 r），不插值、不外推。
//    · 舌长、板厚下角、舌片厚都不是自由旋钮：形状一动就由生产件重定（规则见下「口径」）。
//
//  ⚠ 生产代码一行未动。本文件只读生产件、只写 deliverable 输出。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48VJumpLineProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48VJumpLineProbeTests(ITestOutputHelper o) { _o = o; }

    private sealed record Shot(string Tag, string Fam, double R, double HalfW, double TabLen,
                               double GridRadius, double Cold, double Hot, double Flux,
                               double SecJ, double TubeJ, double Mass, bool Ok, bool Conv, bool AllOk,
                               string Failed, double RemainK, int Rounds, int Cells, double Secs,
                               string Thick, string Tongue, string HotWhere, string HotWhat);

    [Fact]
    public void 跳变诊断_盘径0点1步与逐片定位_W08()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_跳变诊断_盘径0.1步与逐片定位_W08_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_跳变_盘径_{stamp}_进行中.log");
        var probe = new Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 V　跳变诊断 ②：**盘半径 31 → 32 按 0.1 步解整线 + 逐片峰值定位**（W08，导航网格）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W("⚠ 基准设计的舌保温 5.1/2.3/3.6/10.4 mm 不落 0.5 格 ⇒ 只作诊断基准点，**不作可交付设计**。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W("　① 「跳变」= 相邻 0.1 档之间某条判据的变化 > 该段相邻变化中位数的 5 倍。");
        W("　② 判不了（没解出来／外层耦合未收敛）一律不当过，照印。");
        W("　③ 峰位只报解出来的那一格（形心 x,z 与 r），不插值、不外推。");
        W("");
        W("═══════ 口径 ═══════");
        W("网格：生产导航档配方 Solver.ApplyCaseMesh(lc, FineMm=0, FineRadiusMm=MeshVerify.RequiredMeshFor(本形状).RadiusMm)。");
        W($"舌长 = √(盘半径² − 舌半宽²) + 压接段 {d0.ClampLengthMm:0} + 自由段下界 {GeometryScreen.FreeTabMinDefaultMm:0}（搜形状规则）。");
        W("舌片厚与板厚下角：Solver.ApplySectionFloor 闭式重定（不手抄配方）。");
        W($"温差预算：冷侧 {p.ColdUnderTcAllowK:0.#} K／热侧 {p.HotOverTcAllowK:0.#} K；圆盘保温 {d0.FlangeInsulMm:0.#} mm；接合区缠绕上限 {WrapLimits.JointZoneMaxMm:0.#} mm。");
        W("热侧「最热铂高出热偶读数」= max(圆盘峰, 舌片区峰, 该接头管根较热端) − 热偶读数基准（ThermocoupleBasis）。");
        W("当地保温厚：峰位 r ≤ 盘半径 ⇒ 圆盘保温；r > 盘半径 ⇒ 本片舌保温（规则 FlangePlate.UnderDiscInsulation）。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var cands = new List<(string Tag, string Fam, double R, double HalfW)>
        {
            ("基准　盘半径 30.0／舌半宽 30", "基准", 30.0, 30.0),
        };
        for (int i = 0; i <= 10; i++)
            cands.Add(($"盘半径 {31.0 + i * 0.1:0.0}（舌半宽不动 30）", "盘径", 31.0 + i * 0.1, 30.0));
        cands.Add(("舌半宽 29.063（比例 0.96875，盘半径不动 30）", "舌宽", 30.0, 30.0 * (1 - ShapeSearchPlan.Refine(ShapeSearchPlan.Refine(ShapeSearchPlan.FracStep)))));

        var shots = new List<Shot>();
        foreach (var (tag, fam, R, hw) in cands)
        {
            probe.Report($"── 开始 {tag}");
            var sw = Stopwatch.StartNew();
            var d = d0.Clone();
            d.DiscRadiusMm = R;
            d.TabHalfWidthMm = hw;
            d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);

            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });

            LineResult r;
            try { r = LineRunner.Run(lc, probe); }
            catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
            sw.Stop();

            var hotC = Get(r, LineResult.Key.HotOverTc);
            var shot = new Shot(tag, fam, R, hw, d.TabLengthMm, reqRadius,
                                Val(r, LineResult.Key.ColdUnderTc), Val(r, LineResult.Key.HotOverTc),
                                Val(r, LineResult.Key.NetFlux), Val(r, LineResult.Key.SectionJ),
                                Val(r, LineResult.Key.TubeJ), r.Ok ? r.TotalMassG : double.NaN,
                                r.Ok, r.Converged, r.AllOk,
                                r.Ok ? string.Join("／", r.Failed.Select(Criteria.Plain)) : r.Message,
                                r.CoupleRemainK, r.CoupleRounds, r.MeshCells, sw.Elapsed.TotalSeconds,
                                string.Join("/", d.TabThickMm.Select(v => v.ToString("0.000"))),
                                string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.000"))),
                                hotC?.Where ?? "—", HottestWhatOf(r));
            shots.Add(shot);
            probe.Report($"── {tag} 结束：管根 {F3(shot.Cold)}／最热铂 {F3(shot.Hot)}／净流入 {F3(shot.Flux)}，耗时 {sw.Elapsed.TotalSeconds:0} s");

            // ── 逐点落盘：先四条判据，再逐片定位
            W($"════ {tag}");
            W($"　几何：盘半径 {R:0.000}／舌半宽 {hw:0.000}／舌长 {d.TabLengthMm:0.00}（切点 {Math.Sqrt(Math.Max(0, R * R - hw * hw)):0.000}）"
              + $"　板厚 {shot.Thick}　舌片厚 {shot.Tongue}　圆盘板厚工艺下界 {d.DiscFloorMm(p):0.000}");
            W($"　网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.0} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm　片0单元 {r.MeshCells}");
            W($"　结论：{(!r.Ok ? "整线没解出来 ⇒ 判不了（" + r.Message + "）" : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K，轮 {r.CoupleRounds}）⇒ 判不了" : r.AllOk ? "全判据通过" : "不过：" + shot.Failed)}");
            W($"　管根低于热偶读数 {F3(shot.Cold)}／{p.ColdUnderTcAllowK:0.#}（位置 {Get(r, LineResult.Key.ColdUnderTc)?.Where}）"
              + $"　最热铂高出热偶读数 {F3(shot.Hot)}／{p.HotOverTcAllowK:0.#}（位置 {shot.HotWhere}，最热的是{shot.HotWhat}）");
            W($"　管孔净流入 {F3(shot.Flux)} W（位置 {Get(r, LineResult.Key.NetFlux)?.Where}）　法兰截面 J {F3(shot.SecJ)}　管 J {F3(shot.TubeJ)}"
              + $"　铂重 {F(shot.Mass)} g　外层耦合剩余 {F3(r.CoupleRemainK)} K／轮 {r.CoupleRounds}　耗时 {sw.Elapsed.TotalSeconds:0} s");
            if (r.Ok && r.Segments is { Length: > 0 })
            {
                W("　── 管段（设定值／两端管根温度 °C）");
                foreach (var s in r.Segments)
                    W($"　　{s.Name}：设定 {s.SetpointC:0.0}　首端 {s.TRootAC:0.00}　末端 {s.TRootBC:0.00}"
                      + $"　（无法兰基线 首 {s.BaseTRootAC:0.00}／末 {s.BaseTRootBC:0.00}）");
                W("　── 逐片定位（片｜热偶基准 °C｜最热的是谁 °C｜圆盘峰 °C @r,x,z, J, 板厚, 保温｜舌片区峰 °C @r,x,z, J, 板厚, 保温｜管根两端 °C｜热侧 K｜冷侧 K｜管孔净流入 W｜发热 W｜表面散热 W｜进铜排 W）");
                for (int j = 0; j < r.Flanges.Length; j++)
                {
                    var t = ThermocoupleBasis.At(r, j);
                    var f = r.Flanges[j];
                    double discIns = lc.DiscInsulEffectiveAt(j);
                    double tabIns = j < d.TabInsulMm.Length ? d.TabInsulMm[j] : double.NaN;
                    string InsAt(double rr) => double.IsNaN(rr) ? "—" : (rr <= R + 1e-9 ? $"圆盘保温 {discIns:0.0}" : $"舌保温 {tabIns:0.0}") + " mm";
                    W($"　　片{j} {t.Name}｜基准 {t.RefC:0.00}｜最热的是{t.HottestWhat} {t.HottestC:0.0}"
                      + $"｜圆盘峰 {f.TDiscMaxC:0.0} @r={f.DiscMaxRMm:0.00} (x={f.DiscMaxXMm:0.0}, z={f.DiscMaxZMm:0.0}) J={f.DiscMaxJAPerMm2:0.000} t={f.DiscMaxThickMm:0.000} {InsAt(f.DiscMaxRMm)}"
                      + $"｜舌片区峰 {f.TTabMaxC:0.0} @r={f.TabMaxRMm:0.00} (x={f.TabMaxXMm:0.0}, z={f.TabMaxZMm:0.0}) J={f.TabMaxJAPerMm2:0.000} t={f.TabMaxThickMm:0.000} {InsAt(f.TabMaxRMm)}"
                      + $"｜管根 {t.RootEnds}｜热侧 {F3(t.HotK)}｜冷侧 {F3(t.ColdK)}"
                      + $"｜净流入 {f.QFromTubeW:+0.000;−0.000}｜发热 {f.QGenW:0.000}｜表面散热 {f.QLossW:0.000}｜进铜排 {f.QClampW:0.000}"
                      + $"｜片温 {f.TMinC:0.0}～{f.TMaxC:0.0}｜{f.DiscZoneRule}｜{f.InsulRule}");
                }
            }
            W("");
            Flush();
        }

        // ══════ 曲线表 ══════
        W("═══════ ①　三条判据随盘半径（0.1 步）═══════");
        W("盘半径\t舌长\t细区半径\t板厚(4片)\t管根低于热偶读数\t最热铂高出热偶读数\t最热铂位置\t最热的是谁\t管孔净流入 W\t法兰截面 J\t铂重 g\t片0单元\t耦合剩余 K\t轮\t耗时 s\t判词");
        foreach (var s in shots.Where(s2 => s2.Fam is "基准" or "盘径").OrderBy(s2 => s2.R))
            W($"{s.R:0.0}\t{s.TabLen:0.00}\t{s.GridRadius:0.00}\t{s.Thick}\t{F3(s.Cold)}\t{F3(s.Hot)}\t{s.HotWhere}\t{s.HotWhat}\t{F3(s.Flux)}\t{F3(s.SecJ)}\t{F(s.Mass)}\t{s.Cells}\t{F3(s.RemainK)}\t{s.Rounds}\t{s.Secs:0}\t"
              + $"{(!s.Ok ? "判不了（没解出来）" : !s.Conv ? "判不了（未收敛）" : s.AllOk ? "全过" : s.Failed)}");
        W("");

        W("═══════ ②　单步位移（找跳变；判读①）═══════");
        var seq = shots.Where(s2 => s2.Fam is "基准" or "盘径").OrderBy(s2 => s2.R).ToList();
        void StepOf(string name, Func<Shot, double> f)
        {
            var ds = new List<(double At, double D)>();
            for (int i = 1; i < seq.Count; i++)
            {
                if (seq[i - 1].R < 30.5) continue;             // 30.0 → 31.0 是 1 mm，不与 0.1 步混比
                ds.Add((seq[i].R, f(seq[i]) - f(seq[i - 1])));
            }
            if (ds.Count == 0) { W($"{name}\t（没有可比的相邻档）"); return; }
            var abs = ds.Select(t => Math.Abs(t.D)).OrderBy(v => v).ToArray();
            double med = abs[abs.Length / 2];
            var mx = ds.OrderByDescending(t => Math.Abs(t.D)).First();
            W($"{name}\t中位单步 {med:0.0000}\t最大单步 {mx.D:+0.0000;−0.0000} 落在 {mx.At - 0.1:0.0}→{mx.At:0.0}\t"
              + $"倍率 {(med > 1e-12 ? (Math.Abs(mx.D) / med).ToString("0.0") : "—")}\t"
              + $"{(med > 1e-12 && Math.Abs(mx.D) / med > 5 ? "★ 跳变" : "平滑")}");
        }
        StepOf("管根低于热偶读数", s => s.Cold);
        StepOf("最热铂高出热偶读数", s => s.Hot);
        StepOf("管孔净流入 W", s => s.Flux);
        StepOf("铂重 g", s => s.Mass);
        StepOf("片0单元数", s => s.Cells);
        W("");
        W("每一档相邻差（盘径 31.0 起）：");
        for (int i = 1; i < seq.Count; i++)
        {
            if (seq[i - 1].R < 30.5) continue;
            W($"　{seq[i - 1].R:0.0} → {seq[i].R:0.0}：管根 {D3(seq[i].Cold - seq[i - 1].Cold)}　最热铂 {D3(seq[i].Hot - seq[i - 1].Hot)}"
              + $"　净流入 {D3(seq[i].Flux - seq[i - 1].Flux)}　铂重 {D0(seq[i].Mass - seq[i - 1].Mass)}　片0单元 {seq[i].Cells - seq[i - 1].Cells:+0;-0}");
        }
        W("");

        Finish(sb, file, total);
        Assert.Equal(cands.Count, shots.Count);
        Assert.True(shots.All(s => s.Secs > 0), "有候选一次整线解都没跑 —— 那不是实测");
    }

    // ══════════════════════════════════════════════════════════════════════
    internal static string HottestWhatOf(LineResult r)
    {
        if (!r.Ok || r.Flanges is null || r.Flanges.Length == 0 || r.Segments is null) return "—";
        var all = Enumerable.Range(0, r.Flanges.Length).Select(j => ThermocoupleBasis.At(r, j))
                            .Where(t => t.HotBlind.Length == 0).ToArray();
        if (all.Length == 0) return "判不了";
        return all.OrderByDescending(t => t.HotK).First().HottestWhat;
    }

    internal static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));
    internal static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    internal static string F(double v) => double.IsNaN(v) ? "—" : v.ToString("0");
    internal static string F3(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000");
    internal static string D3(double v) => double.IsNaN(v) ? "—" : v.ToString("+0.000;-0.000");
    internal static string D0(double v) => double.IsNaN(v) ? "—" : v.ToString("+0;-0");

    private void Finish(StringBuilder sb, string file, Stopwatch total)
    {
        total.Stop();
        sb.AppendLine($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
                    + "舌片厚／板厚下角 = Solver.ApplySectionFloor；逐片读数 = ThermocoupleBasis.At；峰位 = FlangeOut.DiscMax*／TabMax*（原样取自 ShellThermal）。");
        sb.AppendLine("本文件里每一个数都来自这一次运行（同一进程、同一份代码）。2026-09-18，Opus 5");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
    }

    /// <summary>进度活页：每行带时刻与自上一行的耗时，当场落盘（长跑要放探针）。</summary>
    internal sealed class Probe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _last;
        public Probe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [+{now - _last,7:0.0}s 累计{now / 60,7:0.0}min]  {value}\r\n");
                _last = now;
            }
        }
    }
}
