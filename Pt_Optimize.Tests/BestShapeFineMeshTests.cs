using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 仪器（2026-09-09）：搜形状第四趟（2 段、管保温 10、R11 口径）最轻的可行形状 盘Ø58／舌宽58／舌长140（2646 g，导航网格）——
/// 单独解一遍，再做**加密复算**（MeshVerify：一档档加密到判据不再变），看导航网格上的「全过」在细网格上还站不站得住。
/// 只看，不判；轨迹与复核结果落 deliverable/细网格复算_盘58舌58.txt。
/// </summary>
public class BestShapeFineMeshTests
{
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    /// <summary>第四趟（截面修正前）最轻：Ø58／舌58；截面修正后单点复算 2614.3 → 细网格 2615.8 g（2026-09-09）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 盘58舌58_细网格复算() => Run(29, 29, 140, "盘58舌58");

    /// <summary>第五趟（截面修正后）最轻：Ø56／舌56 导航网格 2606 g（2026-09-09 傍晚）—— 细网格上站不站得住看这里。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 盘56舌56_细网格复算() => Run(28, 28, 140, "盘56舌56");

    private static void Run(double discR, double halfW, double tabLen, string tag)
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10;                                   // 与搜形状（页面默认 纤维保温 10）同一工况
        d.DiscRadiusMm = discR; d.TabHalfWidthMm = halfW; d.TabLengthMm = tabLen;
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", $"细网格复算_{tag}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, $"═══ 盘Ø{2 * discR:0}／舌宽{2 * halfW:0}／舌长{tabLen:0}（2 段 3 片，管壁 0.8，管保温 10）：先解（导航网格）再加密复算 ═══" + Environment.NewLine);
        var sw = Stopwatch.StartNew();
        var live = new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}" + Environment.NewLine));

        var sr = Solver.Solve(d, p, new SolverOptions { FineMm = 0, MaxRounds = 16 }, live);
        File.AppendAllText(dump, Environment.NewLine + $"═══ 导航网格：耗时 {sw.Elapsed.TotalMinutes:0.0} 分　场解 {sr.Solves} 次　可行 {sr.Feasible}　合计 {sr.MassG:0.0} g　停在：{sr.StopWhy}" + Environment.NewLine);
        Assert.True(sr.Trace.Count > 0);
        if (!sr.Feasible || sr.Design is null) { File.AppendAllText(dump, "导航网格上就不可行 ⇒ 不做加密复算" + Environment.NewLine); return; }
        File.AppendAllText(dump, "解出的设计：" + sr.Design.Describe() + Environment.NewLine + Environment.NewLine + "═══ 加密复算 ═══" + Environment.NewLine);

        var mv = MeshVerify.Run(sr.Design, p, progress: live);
        File.AppendAllText(dump, Environment.NewLine
            + $"═══ 加密复算：收敛 {mv.Converged}　细网格 {mv.FineMm:0.###} mm　撞单元上限 {mv.HitCellCap}　用时 {mv.SecondsTotal / 60:0.0} 分" + Environment.NewLine
            + $"判语：{mv.Verdict}" + Environment.NewLine
            + (mv.PeakOutsideFine is null ? "" : "峰值落在细区外：" + mv.PeakOutsideFine + Environment.NewLine)
            + (mv.MidBandConfirm is null ? "" : "中档确认：" + mv.MidBandConfirm + Environment.NewLine));
        if (mv.Line is { } fine)
        {
            File.AppendAllText(dump, "细网格判据表：" + Environment.NewLine);
            foreach (var c in fine.Checks)
                File.AppendAllText(dump, $"  {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}" + Environment.NewLine);
            File.AppendAllText(dump, $"  全判据 {fine.AllOk}　合计 {fine.TotalMassG:0.0} g" + Environment.NewLine);
            if (!fine.AllOk)
            {
                // ★ 导航网格上的解在细网格上站不住 ⇒ 与「◇ 搜形状」精算胜出形状同一条路：Solver 在判据所在的那张网格上重新求根
                //   （A⑬：根的位置随网格移动；③ 实测导航 → 细网格翻倍）。旋钮只增不减、从导航网格的解出发。
                File.AppendAllText(dump, Environment.NewLine + $"═══ 细网格重解（Solver 第二遍，FineMm {mv.FineMm:0.###}）═══" + Environment.NewLine);
                var (finFine, finFineR) = MeshVerify.RequiredMeshFor(sr.Design);
                var sr2 = Solver.Solve(sr.Design, p, new SolverOptions { FineMm = finFine, FineRadiusMm = finFineR, MaxRounds = 16 }, live);
                File.AppendAllText(dump, Environment.NewLine + $"═══ 细网格重解：耗时 {sw.Elapsed.TotalMinutes:0.0} 分（累计）　场解 {sr2.Solves} 次　可行 {sr2.Feasible}　合计 {sr2.MassG:0.0} g　停在：{sr2.StopWhy}" + Environment.NewLine);
                if (sr2.Design is { } d2) File.AppendAllText(dump, "重解出的设计：" + d2.Describe() + Environment.NewLine);
                if (sr2.Best is { } b2)
                {
                    File.AppendAllText(dump, "细网格重解判据表：" + Environment.NewLine);
                    foreach (var c in b2.Checks)
                        File.AppendAllText(dump, $"  {(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-28} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}" + Environment.NewLine);
                    File.AppendAllText(dump, $"  全判据 {b2.AllOk}　合计 {b2.TotalMassG:0.0} g" + Environment.NewLine);
                }
            }
        }
    }
}
