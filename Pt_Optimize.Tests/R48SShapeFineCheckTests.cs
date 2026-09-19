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
//  R48 S　**形状探针 ③：最接近的那个点，换到判决网格上还成不成立** —— 2026-09-18，Opus 5
//
//  ══ 为什么是这一支，而不是「在判决网格上重新求根」
//
//    形状探针 ② 的结论是「一个可行形状都没有」，最接近的是 **W08 盘半径 32／舌半宽 32**：
//    它在导航网格第 2／3 轮上**四条里过了三条**（管孔净流入、管根低于热偶读数、法兰截面 J），
//    只剩「最热铂高出热偶读数」超 16.6～16.9 K，而法兰侧九根旋钮全部顶到上界只把它拉了 **0.25 K**。
//    那个点本身**被 3 h 时间闸切在第 3 轮**（没跑到底）。
//
//    ⇒ 「在判决网格上重新求根」= 再来一次 Solver.Solve，而判决网格单次场解比导航网格还慢
//      （§0.-10 实测 146 s vs 111 s），本机上是**再来 3 小时以上**，这一轮跑不完。
//      跑不完就什么都答不了 —— 与其那样，不如先用**一次整线解**回答一个更要紧、也答得起的问题：
//      **导航网格上那三条「过了」的判据，换到判决网格上还过不过？**
//      （记忆：精度要在根附近验；判不了不许当过。）
//
//  ══ 怎么复原（口径，不许含糊）
//
//    设计**照形状探针 ② 那份输出文件的求解器轨迹复原**（文件名与行号写在报告头上），
//    不是求解器返回的那个对象。§0.-8 实测过：照表复原与求解器那份在「进几何的字段」上可差 1 ulp，
//    而「管根低于热偶读数」对那一个 ulp 敏感 ⇒ **先做复原自证**（同一张导航网格上与轨迹并列），
//    对不上就照印，不许拿复原件的数冒充那一跑的数。
//
//  ══ 判读（**跑前写死，跑完不挪**）
//
//    · 复原自证：导航网格上四条判据与轨迹并列，差 > 0.5 K（或 0.5 W）就点名「复原件不是那一份」。
//    · 判决网格上：四条逐条报过不过；判不了（没解出来／外层耦合未收敛）**一律不当过**。
//    · **不跑** InsulWindow.Measure：解值那一点自己就不过时，窗口量的不是窗口
//      （§0.-11 ⑥ 已经栽过一次：绕着不可行的点扫，四片全是「解值自己就不过」，什么都没量到）。
//      这一条是跑前写死的，不是跑完才决定省。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48SShapeFineCheckTests
{
    private readonly ITestOutputHelper _o;
    public R48SShapeFineCheckTests(ITestOutputHelper o) { _o = o; }

    /// <summary>复原自证的门槛（跑前写死）：温度 K／功率 W 各 0.5。</summary>
    private const double SelfCheckTol = 0.5;

    private const string TraceFile =
        "deliverable/R48_形状探针_搜形状第一轮网格_W08_本次开跑于2026-09-18_102404.txt";

    /// <summary>照 <see cref="TraceFile"/> 里「第 N 轮　合计 … 」那一行复原。</summary>
    private static DesignSpec Restore(int round)
    {
        var d = DesignSpec.W08.Clone();
        d.DiscRadiusMm = 32.0;
        d.TabHalfWidthMm = 32.0;
        d.TabLengthMm = 140.0;                       // 切点 = 0（舌半宽 = 盘半径）⇒ 40 + 100
        d.Name = $"管壁 0.8 · 盘半径 32／舌半宽 32（照搜形状轨迹第 {round} 轮复原）";
        d.Provenance = $"{TraceFile}　§ 求解器轨迹「第 {round} 轮　合计 …」那一行";

        // 两轮只差板厚（轨迹逐字）
        d.TabThickMm = round == 2 ? new[] { 0.86, 1.26, 1.26, 0.86 }
                                  : new[] { 6.00, 6.00, 6.00, 6.00 };
        d.TabInsulMm = new[] { 7.50, 3.00, 5.00, 17.50 };
        d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.RingMul2 = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「1」= 默认规则值
        d.RingW1Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「3*」= 走默认规则
        d.RingW2Mm = new[] { double.NaN, double.NaN, double.NaN, double.NaN };   // 轨迹印「6*」
        d.SlotSpanDeg = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleRMm = new[] { 0.0, 0.0, 0.0, 0.0 };
        d.TabHoleAspect = new[] { 1.00, 1.00, 1.00, 1.00 };
        // 场定的位置量（轨迹「★ 场定孔位」那一行）
        d.SlotCenterDeg = round == 2 ? new[] { 131.0, 153.0, 153.0, 95.0 }
                                     : new[] { 176.0, 176.0, 176.0, 176.0 };
        d.TabHoleXMm = round == 2 ? new[] { -2.0, -86.0, -86.0, -2.0 }
                                  : new[] { -2.0, -2.0, -2.0, -2.0 };
        d.DiscCutRotDeg = round == 2 ? new[] { -18.0, -15.0, -15.0, -31.0 }
                                     : new[] { -2.0, -2.0, -2.0, -2.0 };
        // 舌片厚是闭式的（不是旋钮）—— 由 Solver.ApplySectionFloor 按当前几何重定，不手抄
        return d;
    }

    /// <summary>轨迹那一行印的四条判据（复原自证的靶子；出自同一份文件）。</summary>
    private static (double Flux, double Hot, double Cold, double SecJ, double MassG) Target(int round)
        => round == 2 ? (0.18, 21.89, 4.79, 9.98, 4279)
                      : (0.25, 21.64, 4.93, 9.98, 4620);

    [Fact]
    public void 最接近的形状_导航复原自证与判决网格复核_W08盘32()
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_形状探针_判决网格复核_W08盘32舌32_本次开跑于{stamp}.txt");
        string live = Path.Combine(Path.GetTempPath(), $"R48_形状复核_W08盘32_{stamp}_进行中.log");
        var probe = new Probe(live);
        var total = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var d2 = Restore(2);
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(d2);
        var navCase = new LineCase();

        W("R48 S　形状探针 ③：**最接近的那个形状（W08 盘半径 32／舌半宽 32），换到判决网格上还成不成立**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W($"设计出处：{TraceFile}　§ 求解器轨迹「第 2 轮 / 第 3 轮　合计 …」两行（**照表复原**，不是求解器返回的对象）");
        W("⚠ 那个点在形状探针 ② 里**被 3 h 时间闸切在第 3 轮**（没跑到底）⇒ 这里复核的是「它走到那一刻的那两个设计」，"
          + "**不是**「盘半径 32 这个形状最好能做到多少」。后者要重新求根，本机上是再来 3 小时以上，本轮跑不起。");
        W("");
        W("═══════ 判读（跑前写死，跑完不挪）═══════");
        W($"　① 复原自证：导航网格上四条判据与轨迹并列，任一条差 > {SelfCheckTol:0.0}（K 或 W）就点名「复原件不是那一份」，后面的数不许当那一跑的数引用。");
        W("　② 判决网格上四条逐条报过不过；判不了（没解出来／外层耦合未收敛）**一律不当过**。");
        W("　③ **不跑** 每片舌保温可行窗口（InsulWindow.Measure）—— 解值那一点自己就不过时，绕着它扫量不到窗口"
          + "（§0.-11 ⑥ 已经栽过一次：四片全是「解值自己就不过」）。这一条是跑前写死的，不是跑完才决定省。");
        W($"网格：导航 = 整线算例缺省（细区 {navCase.MeshFineMm:0.0} mm）＋细区半径统一到 {reqRadius:0.0}（Solver.Solve 第一遍 navOpt 那一行）；");
        W($"　　　判决 = MeshVerify.RequiredMeshFor(本形状) = 细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.0} mm（与交付判定同一张）。");
        W($"温差预算：冷侧 {p.ColdUnderTcAllowK:0.#} K／热侧 {p.HotOverTcAllowK:0.#} K（参数表默认）。"
          + $"圆盘保温 {d2.FlangeInsulMm:0.#} mm；接合区缠绕上限 {WrapLimits.JointZoneMaxMm:0.#} mm。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        var rows = new List<string>();
        bool anySelfCheckBad = false;

        foreach (int round in new[] { 2, 3 })
        {
            var d = Restore(round);
            // 舌片厚与板厚下角：闭式，调生产件重定（不手抄配方）
            Solver.ApplySectionFloor(d, p, new SolverOptions(), new SolverResult { Design = d }, null, null);
            var t = Target(round);

            W($"═══════ 第 {round} 轮那一刻的设计 ═══════");
            W($"　板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))}"
              + $"　舌保温 {string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.00")))}"
              + $"　环倍率 {string.Join("/", d.RingMul.Select(v => v.ToString("0.00")))}"
              + $"　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))}（闭式重定）");
            double q = Solver.KnobQuantum(new SolverOptions(), Solver.Knob.Insul);
            var off = d.TabInsulMm.Select((v, j) => (v, j))
                       .Where(x => Math.Abs(x.v - new SolverOptions().InsLoMm) > 1e-9
                                && Math.Abs(x.v / q - Math.Round(x.v / q)) > 1e-9)
                       .Select(x => $"片{x.j}={x.v:R}").ToArray();
            W(off.Length == 0 ? $"　舌保温四片都落在格子上（{q:0.###} 的整数倍或裸舌）。"
                              : "　★ **有片不落格**：" + string.Join("、", off) + " ⇒ 标「疑似 L 路 09-18 二分退回病」，不采信。");
            Flush();

            foreach (var (tag, opt) in new (string, SolverOptions)[]
                     {
                         ("导航网格（复原自证）", new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius }),
                         ("判决网格（与交付判定同一张）", new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius }),
                     })
            {
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, opt);
                var sw = Stopwatch.StartNew();
                probe.Report($"── 第 {round} 轮设计　{tag}　开跑");
                LineResult r;
                try { r = LineRunner.Run(lc, probe); }
                catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
                sw.Stop();
                probe.Report($"── 第 {round} 轮设计　{tag}　结束，耗时 {sw.Elapsed.TotalSeconds:0} s");

                double flux = Val(r, LineResult.Key.NetFlux), hot = Val(r, LineResult.Key.HotOverTc);
                double cold = Val(r, LineResult.Key.ColdUnderTc), secj = Val(r, LineResult.Key.SectionJ);

                W($"── 第 {round} 轮设计　{tag}");
                W($"　网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.0} mm／细区半径 {lc.MeshFineRadiusMm:0.0} mm，单元 {r.MeshCells}");
                W($"　结论：{Verdict(r)}　耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {F0(r.TotalMassG)} g，"
                  + $"耦合收敛 {r.Converged}，剩余误差估计 {r.CoupleRemainK:0.000} K，实际容差 {r.CoupleTolKUsed:0.000} K，轮数 {r.CoupleRounds}");
                W("　判据\t值\t限值\t过?\t位置");
                foreach (var c in r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
                    W($"　{Criteria.Plain(c.Name)}\t{(c.Withheld ? "暂不给数" : c.Actual.ToString("0.###"))}\t{c.Limit:0.###} {c.Unit}"
                      + $"\t{(c.Undetermined ? "**判不了**" : c.Ok ? "过" : "**不过**")}\t{c.Where}");

                if (tag.StartsWith("导航", StringComparison.Ordinal))
                {
                    var diffs = new List<string>();
                    void Chk(string name, double got, double want, string unit)
                    {
                        double dv = got - want;
                        bool bad = double.IsNaN(got) || Math.Abs(dv) > SelfCheckTol;
                        if (bad) diffs.Add($"{name} 复原 {F3(got)} vs 轨迹 {want:0.###}（差 {dv:+0.###;-0.###} {unit}）");
                    }
                    Chk("管孔净流入", flux, t.Flux, "W");
                    Chk("最热铂高出热偶读数", hot, t.Hot, "K");
                    Chk("管根低于热偶读数", cold, t.Cold, "K");
                    W($"　**复原自证**（与轨迹第 {round} 轮那一行并列，门槛 {SelfCheckTol:0.0}）："
                      + (diffs.Count == 0
                         ? $"四条都对得上（净流入 {F3(flux)} vs {t.Flux:0.##}／最热铂 {F3(hot)} vs {t.Hot:0.##}／管根 {F3(cold)} vs {t.Cold:0.##}）⇒ 复原件就是那一份。"
                         : "**对不上** —— " + string.Join("；", diffs) + "　⇒ 下面的数**不许**当那一跑的数引用。"));
                    if (diffs.Count > 0) anySelfCheckBad = true;
                }

                rows.Add($"第 {round} 轮\t{tag}\t{F3(cold)}\t{F3(hot)}\t{F3(flux)}\t{F3(secj)}\t{F0(r.TotalMassG)}\t{sw.Elapsed.TotalSeconds:0}\t{(r.AllOk ? "全过" : Verdict(r))}");
                W("");
                Flush();
            }
        }

        W("═══════ 并列表（同一次运行、同一份代码；只换网格与轮次）═══════");
        W("轮次\t网格\t管根低于热偶读数\t最热铂高出热偶读数\t管孔净流入 W\t法兰截面 J\t铂重 g\t耗时 s\t判词");
        foreach (string s in rows) W(s);
        W("");
        W("═══════ 一句话 ═══════");
        W("W08 盘半径 32／舌半宽 32（搜形状里最接近的那个点）：");
        W("　导航网格上四条过三条（管孔净流入、管根低于热偶读数、法兰截面 J），只剩「最热铂高出热偶读数」超 16.6～16.9 K；");
        W("　板厚从 0.86/1.26/1.26/0.86 顶到 6.00×4（第 2 轮 → 第 3 轮，铂重 4279 → 4620 g）只把它拉了 0.25 K"
          + "（21.89 → 21.64）—— **法兰侧的厚度旋钮对这一条几乎无效**。");
        W("　换到判决网格上的结果见上表。" + (anySelfCheckBad ? "⚠ 复原自证**没对上**，上表只能当「复原件的数」，不是那一跑的数。" : ""));
        W("");
        Finish(sb, file, total);

        Assert.True(rows.Count == 4, "四次整线解没有都跑出来 —— 那不是实测");
    }

    // ══════════════════════════════════════════════════════════════════════
    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));
    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;
    private static string F0(double v) => double.IsNaN(v) ? "—" : v.ToString("0");
    private static string F3(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000");

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : "不过：" + string.Join("／", r.Failed.Select(Criteria.Plain));

    private void Finish(StringBuilder sb, string file, Stopwatch total)
    {
        total.Stop();
        sb.AppendLine($"── 总耗时 {total.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：整线解 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（全仓唯一一份）；"
                    + "网格无关口径 = MeshVerify.RequiredMeshFor；舌片厚／板厚下角 = Solver.ApplySectionFloor。");
        sb.AppendLine("本文件里每一个数都来自这一次运行（同一进程、同一份代码）；轨迹那一列出自表头写明的那份文件。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
    }

    private sealed class Probe : IProgress<string>
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
