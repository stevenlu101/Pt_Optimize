using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  通用性审计 B：合成尺寸实跑（只量不判；2026-09-23，审计员 B；探针，不入库）
//  业主原则 2026-09-23 16:2x：「通用型解法是要是配不同尺寸的铂金管与法兰输入，都能优化出结果」（HANDOVER §0.-21）。
//  从 Builtin[0]（W08）／Builtin[1]（W06）出发改尺寸，逐组：
//    ① 导航与判决网格（MeshVerify.RequiredMeshFor + Solver.ApplyCaseMesh + LineRunner.PlateMeshAnalytic，逐片）
//    ② 生产整线解（LineRunner.Run，导航网格 = FineMm 0 ／细区半径 = RequiredMeshFor 的半径；与门 f 同一调用方式；闸 1800 s）
//    ③ Solver.Solve 第一遍求根（界面「自动定厚」预设 MaxRounds 40／不挖舌孔／FineMm 0 ⇒ 只跑导航遍；闸 20 min）
//  记录：出数／拒答（原文）／崩（异常）／超时 与 卡在哪条约束或上界。不改任何判据，不判对错。
// ════════════════════════════════════════════════════════════════════════════
public class ZZGenSizeProbeTests
{
    private readonly ITestOutputHelper _o;
    public ZZGenSizeProbeTests(ITestOutputHelper o) { _o = o; }

    static readonly TimeSpan LineCap = TimeSpan.FromSeconds(1800);
    static readonly TimeSpan RootCap = TimeSpan.FromMinutes(20);

    sealed class Case
    {
        public string Name = "", Group = "", Summary = "";
        public DesignSpec D = null!;
        public DesignInputs P = null!;
        public bool SelfConsistent = true;
    }

    static DesignSpec Base(int k) => DesignSpec.Builtin[k].Clone();
    static void SetHole(DesignSpec d, double holeR) => d.TubeIdMm = 2.0 * (holeR - d.WallMm);
    static void SetRWRule(DesignSpec d, double R, double w)
    {   // 舌长按搜形状规则 = √(R²−w²) + 压接 + 自由段下界（与 R48NMeshGateTests.SetRW 同式）
        d.DiscRadiusMm = R; d.TabHalfWidthMm = w;
        d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - w * w)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
    }
    static DesignInputs InputsFor(DesignSpec d) { var p = new DesignInputs(); p.TubeIdMm = d.TubeIdMm; return p; }

    static List<Case> Cases()
    {
        var L = new List<Case>();
        Case Mk(string name, string grp, int k, Action<DesignSpec> f, bool consistent = true)
        {
            var d = Base(k); f(d); d.Fit();
            d.Name = $"合成_{name}";
            var c = new Case { Name = name, Group = grp, D = d, P = InputsFor(d), SelfConsistent = consistent };
            c.Summary = Describe(d);
            return c;
        }
        L.Add(Mk("G00_W08原样", "对照", 0, d => { }));
        L.Add(Mk("G01_W06原样", "对照", 1, d => { }));
        L.Add(Mk("G02_管小_孔15_盘20_舌半宽18", "管径", 0, d => { SetHole(d, 15.0); SetRWRule(d, 20.0, 18.0); }));
        L.Add(Mk("G03_管大_孔40_盘48_舌半宽45", "管径", 0, d => { SetHole(d, 40.0); SetRWRule(d, 48.0, 45.0); }));
        L.Add(Mk("G04_舌短80", "舌长", 0, d => { d.TabLengthMm = 80.0; }));
        L.Add(Mk("G05_舌长230", "舌长", 0, d => { d.TabLengthMm = 230.0; }));
        L.Add(Mk("G06_板薄0p6", "板厚", 0, d => { for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 0.6; }));
        L.Add(Mk("G07_板厚3", "板厚", 0, d => { for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 3.0; }));
        L.Add(Mk("G08_2段", "段数", 0, d => { d.SetpointC = new[] { 1150.0, 1080.0 }; }));
        L.Add(Mk("G09_6段", "段数", 0, d => { d.SetpointC = new[] { 1150.0, 1120.0, 1100.0, 1080.0, 1060.0, 1050.0 }; }));
        L.Add(Mk("G10_控温高1200", "控温", 0, d => { d.SetpointC = new[] { 1200.0, 1200.0, 1200.0 }; }));
        L.Add(Mk("G11_控温低950", "控温", 0, d => { d.SetpointC = new[] { 950.0, 950.0, 950.0 }; }));
        L.Add(Mk("G12_W06管大_孔40_盘50_舌半宽45", "管径", 1, d => { SetHole(d, 40.0); SetRWRule(d, 50.0, 45.0); }));
        // 盘盖不住孔 + 焊脚：孔 25.8 ＜ 盘 26.0（孔 < 盘、舌半宽 24 < 盘，几何自洽），但 孔 + 焊脚(max(板厚,壁厚)) ＞ 盘
        L.Add(Mk("G13_盘盖不住孔加焊脚_盘26_舌半宽24", "⑥拒答", 0, d => { SetRWRule(d, 26.0, 24.0); }));
        // 另列：几何不自洽
        L.Add(Mk("X01_孔大于盘_孔40_盘30", "不自洽", 0, d => { SetHole(d, 40.0); }, consistent: false));
        L.Add(Mk("X02_舌半宽大于盘_盘30_舌半宽40", "不自洽", 0, d => { d.TabHalfWidthMm = 40.0; }, consistent: false));
        return L;
    }

    static string Describe(DesignSpec d) =>
        $"壁 {d.WallMm:0.00}／内径 {d.TubeIdMm:0.###}／孔半径 {d.HoleRadiusMm:0.###}／盘半径 {d.DiscRadiusMm:0.###}／舌半宽 {d.TabHalfWidthMm:0.###}／舌长 {d.TabLengthMm:0.###}"
      + $"／板厚 {string.Join(",", d.TabThickMm.Select(v => v.ToString("0.00")))}／舌保温 {string.Join(",", d.TabInsulMm.Select(v => v.ToString("0.0")))}"
      + $"／段数 {d.SegmentCount}（片 {d.FlangeCount}）／控温 {string.Join(",", d.SetpointC.Select(v => v.ToString("0")))}／段长 {string.Join(",", d.SegLengthMm.Select(v => v.ToString("0")))}"
      + $"／盘保温 {(d.FlangeInsulated ? d.FlangeInsulMm.ToString("0.#") : "无")}／圆角 {d.TabFilletMm:0.#}／环宽 {d.RingWidthMm:0.#}／压接 {d.ClampLengthMm:0}";

    static string Load() { try { return File.ReadAllText("/proc/loadavg").Trim(); } catch { return "未查到"; } }
    static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    static string Oneline(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ⏎ ");

    // ─────────────────────────────── ① 网格
    static string MeshPart(Case c, out string brief)
    {
        var sb = new StringBuilder();
        var bl = new List<string>();
        var d = c.D.Clone(); var p = c.P;
        double reqFine = double.NaN, reqRadius = double.NaN;
        try { (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d); sb.AppendLine($"  RequiredMeshFor(设计)：判决细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.00} mm（盘 {d.DiscRadiusMm:0.##}、0.35×舌长 {0.35 * Math.Abs(d.TabLengthMm):0.##}、孔 {d.HoleRadiusMm:0.###}）"); }
        catch (Exception ex) { sb.AppendLine($"  RequiredMeshFor 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}"); bl.Add("RequiredMeshFor 崩"); }
        try
        {
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            sb.AppendLine($"  ApplySectionFloor 后：板厚 {string.Join(",", d.TabThickMm.Select(v => v.ToString("0.00")))}／舌片厚 {string.Join(",", d.TongueThickMm.Select(v => double.IsNaN(v) ? "NaN" : v.ToString("0.00")))}（盘下界 {d.DiscFloorMm(p):0.00}）");
        }
        catch (Exception ex) { sb.AppendLine($"  ApplySectionFloor 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}"); bl.Add("ApplySectionFloor 崩"); }
        foreach (var (grade, fine) in new[] { ("导航", 0.0), ("判决", reqFine) })
        {
            try
            {
                var lc = d.BuildCase(p);
                if (lc.RefusedWhy.Length > 0) { sb.AppendLine($"  {grade}：BuildCase 拒答「{lc.RefusedWhy}」"); bl.Add($"{grade} BuildCase 拒答"); continue; }
                if (double.IsNaN(fine) || double.IsNaN(reqRadius)) { sb.AppendLine($"  {grade}：细区参数 NaN，不建"); bl.Add($"{grade} 无参数"); continue; }
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fine, FineRadiusMm = reqRadius });
                if (grade == "导航")
                {
                    try
                    {
                        var geo = GeometryScreen.Judge(lc.FlangePlates, lc.Base.BusbarClampLengthMm, lc.FreeTabMinMm);
                        foreach (var g in geo) sb.AppendLine($"  几何闭式 {g.Name}：实际 {g.Actual:0.###} 限 {g.Limit:0.###} {(g.Ok ? "过" : "不过")} {(g.Undetermined ? "判不了 " : "")}{Oneline(g.Note)}");
                        sb.AppendLine($"  盘半径需求：盖孔+焊脚 {GeometryScreen.MinDiscRadiusMm(lc.FlangePlates):0.###}／舌从盘长出 {GeometryScreen.MinDiscRadiusForTabMm(lc.FlangePlates):0.###}");
                    }
                    catch (Exception ex) { sb.AppendLine($"  GeometryScreen.Judge 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}"); }
                }
                var cells = new List<string>();
                int nFail = 0;
                for (int j = 0; j < lc.FlangePlates.Length; j++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var m = LineRunner.PlateMeshAnalytic(lc, j);
                        cells.Add($"片{j} {m.CellCount} 格 {sw.Elapsed.TotalSeconds:0.0}s");
                    }
                    catch (Exception ex) { nFail++; cells.Add($"片{j} 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}"); }
                }
                sb.AppendLine($"  {grade}网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.000} mm／细区半径 {lc.MeshFineRadiusMm:0.00} mm；{string.Join("；", cells)}");
                bl.Add($"{grade} 细{lc.MeshFineMm:0.000}/半径{lc.MeshFineRadiusMm:0.0}/片0 {cells.FirstOrDefault()}{(nFail > 0 ? $"（{nFail} 片崩）" : "")}");
            }
            catch (Exception ex) { sb.AppendLine($"  {grade}：抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}"); bl.Add($"{grade} 崩：{ex.GetType().Name}"); }
        }
        brief = string.Join("；", bl);
        return sb.ToString();
    }

    // ─────────────────────────────── ② 整线解（导航网格，生产停机口径）
    sealed class LineOut { public string Status = "", Brief = "", Text = "", Stuck = ""; }
    static LineOut LinePart(Case c)
    {
        var o = new LineOut(); var sb = new StringBuilder();
        var d = c.D.Clone(); var p = c.P;
        var sw = Stopwatch.StartNew();
        sb.AppendLine($"  开跑 {Now}　load {Load()}");
        try
        {
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
            using var cts = new CancellationTokenSource(LineCap);
            LineResult r;
            try { r = LineRunner.Run(lc, null, cts.Token); }
            catch (OperationCanceledException)
            {
                o.Status = "超时"; o.Brief = $"超时（>{LineCap.TotalSeconds:0} s，争用下量得）"; o.Stuck = "整线解超时";
                sb.AppendLine($"  ★ 超时：{sw.Elapsed.TotalSeconds:0} s 时被 {LineCap.TotalSeconds:0} s 闸切断（争用下量得）");
                o.Text = sb.ToString(); return o;
            }
            double sec = sw.Elapsed.TotalSeconds;
            sb.AppendLine($"  网格：细区 {lc.MeshFineMm:0.000}／粗区 {lc.MeshCoarseMm:0.000}／半径 {lc.MeshFineRadiusMm:0.00}；单元 {r.MeshCells}；耦合轮 {r.CoupleRounds}；耗时 {sec:0} s（争用下量得）；Ok={r.Ok} Converged={r.Converged} Melted/Message=「{Oneline(r.Message)}」");
            if (!r.Ok)
            {
                bool refused = lc.RefusedWhy.Length > 0 || r.Message.Length > 0;
                o.Status = "拒答"; o.Brief = $"拒答：「{Oneline(r.Message)}」（{sec:0} s）"; o.Stuck = "整线解拒答：" + Oneline(r.Message);
                if (r.Checks.Length > 0) foreach (var k in r.Checks) sb.AppendLine($"    {k.Name}\t{k.Actual:0.###}\t限 {k.Limit:0.###}\t{(k.Undetermined ? "判不了" : k.Ok ? "过" : "不过")}\t{k.Kind}\t{k.Where}");
                o.Text = sb.ToString(); return o;
            }
            string[] keys = { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux };
            var three = new List<string>(); bool nan = false;
            foreach (var key in keys)
            {
                var k = r.Find(key);
                if (k is null) { three.Add($"{key} 缺"); nan = true; continue; }
                if (double.IsNaN(k.Actual)) nan = true;
                three.Add($"{k.Name} {k.Actual:0.000}/{k.Limit:0.###} {(k.Undetermined ? "判不了" : k.Ok ? "过" : "不过")}");
            }
            sb.AppendLine("  三条硬判据：" + string.Join("；", three));
            sb.AppendLine($"  铂重 {r.TotalMassG:0.0} g（管 {r.TubeMassG:0.0} ＋ 法兰 {r.FlangeMassG:0.0}）；认证误差 {r.CertErrK:0.000} K；剩余 {r.CoupleRemainK:0.000} K；放大 {r.CoupleAmpUsed:0.0}（闭式 {r.CoupleAmpClosed:0.0}／雅可比 {r.CoupleAmpJacobian:0.0}）");
            sb.AppendLine("  全判据表（名称／实际／限值／状态／类别／位置）：");
            var failHard = new List<string>();
            foreach (var k in r.Checks)
            {
                string st = k.Withheld ? "扣下" : k.Undetermined ? "判不了" : k.Ok ? "过" : "不过";
                sb.AppendLine($"    {k.Name}\t{k.Actual:0.###}\t限 {k.Limit:0.###}\t{st}\t{k.Kind}\t{k.Where}");
                if (k.Kind != CheckKind.Target && k.Kind != CheckKind.HardSafety) continue;
                if (!k.Ok || k.Undetermined) failHard.Add($"{k.Name}({st} {k.Actual:0.###}/{k.Limit:0.###}{(k.Where.Length > 0 ? " @" + k.Where : "")})");
            }
            foreach (var n in r.Notes.Take(12)) sb.AppendLine("  注：" + Oneline(n));
            if (nan) sb.AppendLine("  ⚠ 三条硬判据中有 NaN／缺项");
            o.Status = r.Converged ? "出数" : "出数（外层耦合未收敛）";
            o.Brief = $"{o.Status}：{string.Join("；", three)}；{r.TotalMassG:0} g；{r.CoupleRounds} 轮；{sec:0} s（争用）{(nan ? "；含 NaN" : "")}";
            o.Stuck = failHard.Count == 0 ? "（本设计旋钮下无不过项）" : "不过／判不了：" + string.Join("；", failHard);
        }
        catch (Exception ex)
        {
            o.Status = "崩"; o.Brief = $"崩：{ex.GetType().Name}：{Oneline(ex.Message)}"; o.Stuck = "异常";
            sb.AppendLine($"  ★ 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}");
            sb.AppendLine("  " + Oneline(ex.StackTrace ?? "").Substring(0, Math.Min(1500, (ex.StackTrace ?? "").Length)));
        }
        o.Text = sb.ToString(); return o;
    }

    // ─────────────────────────────── ③ 第一遍求根
    sealed class RootOut { public string Status = "", Brief = "", Text = "", Stuck = ""; }
    sealed class ListProbe : IProgress<string>
    {
        public readonly ConcurrentQueue<string> Q = new();
        readonly Stopwatch _sw = Stopwatch.StartNew();
        public void Report(string s) { Q.Enqueue($"[{_sw.Elapsed.TotalSeconds,6:0}s] {s}"); while (Q.Count > 4000) Q.TryDequeue(out _); }
    }
    static RootOut RootPart(Case c)
    {
        var o = new RootOut(); var sb = new StringBuilder();
        var d = c.D.Clone(); var p = c.P;
        double reqRadius;
        try { reqRadius = MeshVerify.RequiredMeshFor(d).RadiusMm; } catch { reqRadius = 0; }
        var opt = new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = 0, FineRadiusMm = reqRadius };
        sb.AppendLine($"  选项：MaxRounds 40、AllowTabCuts false、FineMm 0（只导航遍）、FineRadiusMm {reqRadius:0.00}；盒上界 板厚 {opt.ThickHiMm}／舌保温 {opt.InsHiMm}／环倍率 {opt.RingHi}／r1 {opt.RingR1HiMm}／r2 {opt.RingR2HiMm}；开跑 {Now}　load {Load()}");
        var probe = new ListProbe();
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(RootCap);
            var sr = Solver.Solve(d, p, opt, probe, cts.Token);
            double sec = sw.Elapsed.TotalSeconds;
            string verdict = Solver.VerdictOf(sr);
            sb.AppendLine($"  耗时 {sec / 60:0.0} min（争用下量得）；场解 {sr.Solves} 次；Feasible={sr.Feasible} HitBound={sr.HitBound} Undetermined={sr.Undetermined} FineRefined={sr.FineRefined}");
            sb.AppendLine($"  结论：{Oneline(verdict)}");
            sb.AppendLine($"  停因：{Oneline(sr.StopWhy)}");
            if (sr.UndeterminedWhy.Length > 0) sb.AppendLine($"  判不了原因：{Oneline(sr.UndeterminedWhy)}");
            if (sr.NullWhy.Length > 0) sb.AppendLine($"  最近一次场解没解出来：{Oneline(sr.NullWhy)}");
            if (sr.Message.Length > 0) sb.AppendLine($"  Message：{Oneline(sr.Message)}");
            sb.AppendLine($"  铂重 {sr.MassG:0.0} g；终值 板厚 {string.Join(",", sr.Design.TabThickMm.Select(v => v.ToString("0.00")))}／舌保温 {string.Join(",", sr.Design.TabInsulMm.Select(v => v.ToString("0.0")))}／环倍率 {string.Join(",", sr.Design.RingMul.Select(v => v.ToString("0.00")))}／外级倍率 {string.Join(",", sr.Design.RingMul2.Select(v => v.ToString("0.00")))}");
            if (sr.Best != null)
            {
                foreach (var key in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux, LineResult.Key.DiscCover, LineResult.Key.FreeTab })
                {
                    var k = sr.Best.Find(key);
                    if (k != null) sb.AppendLine($"    终局 {k.Name} {k.Actual:0.000}/{k.Limit:0.###} {(k.Undetermined ? "判不了" : k.Ok ? "过" : "不过")} {k.Where}");
                }
            }
            sb.AppendLine("  轨迹末 40 行：");
            foreach (var t in sr.Trace.Skip(Math.Max(0, sr.Trace.Count - 40))) sb.AppendLine("    " + Oneline(t));
            o.Status = sr.Feasible ? "收敛（可行）" : sr.Undetermined ? "判不了" : sr.HitBound ? "结构性停机" : "没搜到";
            o.Brief = $"{o.Status}；停因「{Oneline(sr.StopWhy)}」；{sr.Solves} 次场解；{sec / 60:0.0} min（争用）";
            o.Stuck = sr.Feasible ? "" : Oneline(sr.StopWhy.Length > 0 ? sr.StopWhy : sr.UndeterminedWhy);
        }
        catch (OperationCanceledException)
        {
            double sec = sw.Elapsed.TotalSeconds;
            o.Status = "超时"; o.Brief = $"超时（{RootCap.TotalMinutes:0} min 闸，争用下量得）";
            var tail = probe.Q.ToArray();
            o.Stuck = "求根 20 min 闸切断；切断前末行：" + (tail.Length > 0 ? Oneline(tail[^1]) : "（无）");
            sb.AppendLine($"  ★ 超时：{sec / 60:0.0} min 被 {RootCap.TotalMinutes:0} min 闸切断（争用下量得）");
            sb.AppendLine("  进度末 30 行：");
            foreach (var t in tail.Skip(Math.Max(0, tail.Length - 30))) sb.AppendLine("    " + Oneline(t));
        }
        catch (Exception ex)
        {
            o.Status = "崩"; o.Brief = $"崩：{ex.GetType().Name}：{Oneline(ex.Message)}"; o.Stuck = "异常";
            sb.AppendLine($"  ★ 抛异常 {ex.GetType().Name}：{Oneline(ex.Message)}");
            sb.AppendLine("  " + Oneline(ex.StackTrace ?? "").Substring(0, Math.Min(1500, (ex.StackTrace ?? "").Length)));
            var tail = probe.Q.ToArray();
            foreach (var t in tail.Skip(Math.Max(0, tail.Length - 15))) sb.AppendLine("    " + Oneline(t));
        }
        o.Text = sb.ToString(); return o;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 通用性审计B_合成尺寸实跑()
    {
        var cases = Cases();
        string file = DeliverableOut.Stamped("R48_通用性审计_B_合成尺寸实跑.txt");
        string json = Path.ChangeExtension(file, ".tsv");
        var startedAt = Now;
        var total = Stopwatch.StartNew();
        string head;
        {
            var h = new StringBuilder();
            h.AppendLine("R48 通用性审计 B：合成尺寸实跑（只量不判）");
            h.AppendLine($"本次开跑于 {startedAt}（RunStamp {DeliverableOut.RunStamp}）　工作树 {HandoverDoc.Root()}　分支头 73e7bd3（未改任何生产码）　Linux 镜像 net8.0 Release");
            h.AppendLine($"机器 4 核、与别的子代理同机并跑；开跑时 load {Load()}；本测试内部 2 条并行道（道甲：网格＋整线解逐组串行，完后接求根；道乙：求根）。所有耗时均为「争用下量得」。");
            h.AppendLine("依据：业主原则 2026-09-23 16:2x「通用型解法是要是配不同尺寸的铂金管与法兰输入，都能优化出结果」（HANDOVER §0.-21）。");
            h.AppendLine("口径：");
            h.AppendLine("  ① 网格 = MeshVerify.RequiredMeshFor(设计) 给判决细区与细区半径；导航 = FineMm 0 ＋ 同一细区半径（Solver.ApplyCaseMesh），判决 = FineMm reqFine；逐片 LineRunner.PlateMeshAnalytic；另印 GeometryScreen.Judge（⑤⑥ 几何闭式）。");
            h.AppendLine("  ② 整线解 = LineRunner.Run（生产停机口径，不改任何选项），前置 Solver.ApplySectionFloor；网格 = 导航（门 f 的 SolveLine 同一调用序列，fineMm = 0）；闸 1800 s（CancellationToken）。旋钮取内置设计原值（形状被改的组也沿用 W08／W06 的板厚、舌保温、环倍率）。");
            h.AppendLine("  ③ 求根 = Solver.Solve，选项 = 界面「自动定厚」预设（MaxRounds 40、AllowTabCuts false）、FineMm 0（只跑第一遍导航）、FineRadiusMm = RequiredMeshFor 半径；闸 20 min。");
            h.AppendLine("  设计输入表 = DesignInputs 默认构造，只把 TubeIdMm 设成设计的内径（BuildCase 本就按设计内径算，避免 ApplySectionFloor 等处读到两份内径）。");
            h.AppendLine("  判据、判词、阈值一律不动；本档只记「出数／拒答（原文）／崩（异常）／超时」与卡在哪条约束或上界。");
            h.AppendLine();
            h.AppendLine("═══════ 输入清单 ═══════");
            foreach (var c in cases) h.AppendLine($"{c.Name}\t[{c.Group}]{(c.SelfConsistent ? "" : "（几何不自洽，另列）")}\t{c.Summary}");
            head = h.ToString();
        }
        var meshT = new ConcurrentDictionary<string, string>(); var meshB = new ConcurrentDictionary<string, string>();
        var lineO = new ConcurrentDictionary<string, LineOut>(); var rootO = new ConcurrentDictionary<string, RootOut>();
        object io = new();
        void Flush(bool final = false)
        {
            lock (io)
            {
                var sb = new StringBuilder(head);
                sb.AppendLine();
                foreach (var c in cases)
                {
                    sb.AppendLine($"═══════ {c.Name}　[{c.Group}] ═══════");
                    sb.AppendLine("输入：" + c.Summary);
                    sb.AppendLine("── ① 网格");
                    sb.Append(meshT.TryGetValue(c.Name, out var mt) ? mt : "  （未跑到）\n");
                    sb.AppendLine("── ② 整线解（导航网格）");
                    sb.Append(lineO.TryGetValue(c.Name, out var lo) ? lo.Text : "  （未跑到）\n");
                    sb.AppendLine("── ③ 第一遍求根");
                    sb.Append(rootO.TryGetValue(c.Name, out var ro) ? ro.Text : "  （未跑到）\n");
                    sb.AppendLine();
                }
                sb.AppendLine("═══════ 汇总（每组一行）═══════");
                sb.AppendLine("组\t网格\t整线解\t整线卡在\t求根\t求根卡在");
                foreach (var c in cases)
                {
                    meshB.TryGetValue(c.Name, out var mb); lineO.TryGetValue(c.Name, out var lo); rootO.TryGetValue(c.Name, out var ro);
                    sb.AppendLine($"{c.Name}\t{mb ?? "—"}\t{lo?.Brief ?? "—"}\t{lo?.Stuck ?? "—"}\t{ro?.Brief ?? "—"}\t{ro?.Stuck ?? "—"}");
                }
                sb.AppendLine(final ? $"── 全部结束 {Now}　总耗时 {total.Elapsed.TotalMinutes:0.0} min（争用下量得）　结束时 load {Load()}" : $"── 进行中（写于 {Now}，已 {total.Elapsed.TotalMinutes:0.0} min）");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
                var t = new StringBuilder("name\tmesh\tline_status\tline\tline_stuck\troot_status\troot\troot_stuck\n");
                foreach (var c in cases)
                {
                    meshB.TryGetValue(c.Name, out var mb); lineO.TryGetValue(c.Name, out var lo); rootO.TryGetValue(c.Name, out var ro);
                    t.AppendLine($"{c.Name}\t{mb}\t{lo?.Status}\t{lo?.Brief}\t{lo?.Stuck}\t{ro?.Status}\t{ro?.Brief}\t{ro?.Stuck}");
                }
                File.WriteAllText(json, t.ToString(), new UTF8Encoding(true));
            }
        }
        Flush();
        var rootQ = new ConcurrentQueue<Case>(cases);
        void RootWorker()
        {
            while (rootQ.TryDequeue(out var c))
            {
                rootO[c.Name] = new RootOut { Status = "进行中", Text = $"  （进行中，开跑 {Now}）\n" };
                Flush();
                rootO[c.Name] = RootPart(c);
                Flush();
            }
        }
        var laneB = Task.Run(RootWorker);
        var laneA = Task.Run(() =>
        {
            foreach (var c in cases)
            {
                meshT[c.Name] = MeshPart(c, out var mb); meshB[c.Name] = mb; Flush();
                lineO[c.Name] = new LineOut { Status = "进行中", Text = $"  （进行中，开跑 {Now}）\n" }; Flush();
                lineO[c.Name] = LinePart(c); Flush();
            }
            RootWorker();
        });
        Task.WaitAll(laneA, laneB);
        Flush(final: true);
        _o.WriteLine(file);
        _o.WriteLine(File.ReadAllText(file));
    }
}
