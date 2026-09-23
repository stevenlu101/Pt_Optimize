using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M　**终验三关接线：拿 W08（圆盘保温 10 mm）跑一次真实输出存档** —— 2026-09-18，Opus 5
//
//  ══ 为什么要这一跑
//
//  三关的接线门（R48MThreeStateWiringTests）是毫秒级的：判定所需的结果**注入**，
//  受审的是顺序、呈现、挂载这套生产逻辑。它守不住一件事 ——
//  **这条路在真设计上跑不跑得动**（八个设定点各一次整线解、空管一次、结果能不能挂上、报告长什么样）。
//  源码写了不等于跑得起来，这是本项目「改 UI 必须自己抓图」同一条理由的另一半。
//
//  ⇒ 本跑用 W08 那份 10 mm 设计走**生产那条路**（FinalCheck.Run + FinalCheckReport），
//    把工程师真会看到的那两块（输出框的结论与伸长表、安装报告那一节）原样写进带开跑时刻的文件。
//
//  ══ 判读（跑前写死，跑完不挪）
//
//   ① 三关**各自有结果**（过／不过／判不了都算结果；抛异常、卡住不算）。判不了照实记，不算过。
//   ② 结论块里四个关名按 升温全程 → 带玻璃稳态 → 空管到温 → 铂重 出现。
//   ③ 伸长表逐格印的就是升温那一关算出来的字段（本跑不重算、也不核算法，那条在快门里逐格钉）。
//   ④ 判据过不过是**结果**，不是测试失败 —— 这一档在 10 mm 下本来就可能不可行（§0.-13U ⑥）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48MThreeStateW08RunTests
{
    private readonly ITestOutputHelper _o;
    public R48MThreeStateW08RunTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void W08在圆盘保温10下走生产那条路跑一次三关()
    {
        var d = R48LW08NavDesign.Build();
        var p = new DesignInputs();
        var navCase = new LineCase();
        var mesh = new SolverOptions { FineMm = 0, FineRadiusMm = navCase.MeshFineRadiusMm };   // 导航网格（细区尺寸不动，只统一半径）

        string file = DeliverableOut.Stamped("R48_M_终验三关_生产路_W08.txt");
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        W("R48 M　**终验三关（生产那条路）**　W08（圆盘保温 10 mm，导航网格）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W();
        W("═══════ 跑的是哪条路 ═══════");
        W("① 带玻璃稳态 = LineRunner.Run(d.BuildCase(p))　—— 界面上「核算整线／加密复算」交出来的那一份；");
        W("② 另两关 = Core/FinalCheck.Run(spec, inputs, glass, …)　—— **③ 结果页终验调的就是它**（UI/LineDesignPage.VerifyMeshAsync）；");
        W("③ 下面两块正文 = Core/FinalCheckReport.Conclusions / .Elongation　—— **输出框与安装报告印的就是它们**，本文件一个字都没另写。");
        W();
        W($"设计出处：{R48LW08NavDesign.Source}");
        W($"圆盘保温：整线 {d.WholeLineDiscInsulMm:0.#} mm（包 = {d.FlangeInsulated}）；"
          + $"逐片 {string.Join("／", Enumerable.Range(0, d.FlangeCount).Select(j => d.DiscInsulMmOf(j).ToString("0.#")))} mm；"
          + $"接合区上限 {WrapLimits.JointZoneMaxMm:0.#} mm。");
        W($"舌保温逐片 {string.Join("／", d.TabInsulMm.Select(v => v.ToString("0.0")))} mm；管保温 {d.TubeInsulMm:0.0} mm。");
        W($"牌号 {p.GradeName}（升温那一关的膨胀按它取 —— FinalCheck 把 DesignInputs.GradeName 交给 RampSweepOptions.Grade）。");
        W();
        W("── 判读（跑前写死）：三关各自有结果就算这条路通了；判据过不过是**结果**，不是测试失败。判不了照实记，不算过。");
        W();
        Flush();

        string live = Path.Combine(Path.GetTempPath(), $"R48_M_三关_W08_{DeliverableOut.RunStamp}_进行中.log");
        var probe = new SlowProbe(live);
        W($"进度活页（临时，非交付物）：{live}");
        Flush();

        // ── 第二关：带玻璃稳态（终验交出来的那一份）──
        var swAll = Stopwatch.StartNew();
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, mesh);
        var sw = Stopwatch.StartNew();
        LineResult glass;
        try { glass = LineRunner.Run(lc, probe); }
        catch (Exception ex) { glass = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
        sw.Stop();
        W($"── 带玻璃稳态跑完：{sw.Elapsed.TotalSeconds:0} s，网格 细区 {glass.MeshFineMm:0.000} mm、单元 {glass.MeshCells}");
        Flush();

        // ── 另两关：生产那条路 ──
        var run = FinalCheck.Run(d, p, glass, new FinalCheckOptions { Mesh = mesh }, probe);
        swAll.Stop();

        W();
        W("═══════ 输出框会印出来的（结论块，原样）═══════");
        W(FinalCheckReport.Conclusions(glass));
        W("═══════ 输出框会印出来的（伸长表，原样）═══════");
        W(FinalCheckReport.Elongation(glass));
        W("═══════ 安装报告第 7b 那一节（原样）═══════");
        W(FinalCheckReport.Section(glass));
        W();
        W($"── 总耗时 {swAll.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）"
          + $"；其中升温全程 {run.RampSeconds:0} s、空管到温 {run.EmptyTubeSeconds:0} s（机时量，不进判读）。");
        W("本文件里每一个数都来自这一次运行（同一进程、同一份代码），没有拼接任何旧文件。");
        Flush();

        _o.WriteLine(sb.ToString());
        _o.WriteLine("已写出：" + file);

        // ── 判读（跑前写死）──
        Assert.True(File.Exists(file));
        // ① 三关各自有结果
        Assert.NotNull(glass);
        Assert.NotNull(run.Ramp);
        Assert.NotNull(run.EmptyTube);
        Assert.False(string.IsNullOrWhiteSpace(run.Ramp!.Verdict));
        Assert.True(run.Ramp.Points.Length == 8, $"升温轨迹应有 8 个设定点，实际 {run.Ramp.Points.Length}");
        // 结果真的挂到了带玻璃那一份上（下游读得到）
        Assert.Same(run.Ramp, glass.RampSweep);
        Assert.Same(run.EmptyTube, glass.EmptyTubeSteady);
        // ② 顺序
        string text = FinalCheckReport.Conclusions(glass);
        int i1 = text.IndexOf(FinalCheckReport.Step1, StringComparison.Ordinal);
        int i2 = text.IndexOf(FinalCheckReport.Step2, StringComparison.Ordinal);
        int i3 = text.IndexOf(FinalCheckReport.Step3, StringComparison.Ordinal);
        int i4 = text.IndexOf(FinalCheckReport.Step4, StringComparison.Ordinal);
        Assert.True(i1 < i2 && i2 < i3 && i3 < i4, "三关结论没按顺序印");
        // ③ 伸长表真有逐点逐段的行（八个设定点都在）
        string el = FinalCheckReport.Elongation(glass);
        foreach (var pt in run.Ramp.Points) Assert.Contains($"{pt.SetpointC:0}\t", el);
    }
}
