using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  U 路　**冷侧预算放宽之后，每片舌保温的可行窗口宽多少** —— 2026-09-18，Opus 5。
//
//  ⚠⚠ 这是**演示，不是设计**。
//     · 只为回答一句话：「冷侧那 5 度放宽到 10，窗口会变多宽」——
//       用户 2026-09-17 问的是这一件事，2026-09-18 批的是「把限值改成界面输入」，
//       **没有**批「把限值改成 10」。默认仍是 5，本档不动那个默认值。
//     · 网格用的是**导航网格**（BuildCase 缺省：细区 2.0 mm／细区半径 50 mm），
//       **不是判决那张**。导航网格上这两条温度判据实测偏大（见 MeshVerify 的注释表与
//       HANDOVER §0.-8／§0.-10 的三网格对照）⇒ 这里量到的窗口只能看**相对变化**，
//       绝对值不可引用、不可据以出图、不可写进交付。
//     · 两个预算在**同一次运行、同一个进程、同一份设计、同一张网格**上量，只换那一个数 ——
//       两份仪器输出拼一起的那种「差」在本档不存在。
//
//  设计：W08 导航档解出来的那一份（4245 g，照 R48LW08NavDesign 复原）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48UColdBudgetDemoTests
{
    private readonly ITestOutputHelper _o;
    public R48UColdBudgetDemoTests(ITestOutputHelper o) { _o = o; }

    /// <summary>演示用的两个冷侧预算 K（跑前写死：默认值，与放宽一倍）。</summary>
    private static readonly double[] ColdBudgets = { LineCase.ThermocoupleErrorK, 2 * LineCase.ThermocoupleErrorK };

    /// <summary>导航网格快筛（便宜，只看相对变化）。</summary>
    [Fact]
    public void 冷侧预算放宽后的可行窗口_导航网格演示() => Run(nav: true);

    /// <summary>
    /// **判决用的那张网格**（与交付判定同一张）——回答用户 2026-09-17 那句「冷侧那 5 度放宽，窗口会变多宽」时，
    /// 只有这一张网格上的数说得上话。仍是**演示**：默认值本轮一位没动。
    /// </summary>
    [Fact]
    public void 冷侧预算放宽后的可行窗口_判决网格() => Run(nav: false);

    private void Run(bool nav)
    {
        var d0 = R48LW08NavDesign.Build();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_U_冷侧预算演示_{(nav ? "导航网格" : "判决网格")}_W08_本次开跑于{stamp}.txt");
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var navCase = new LineCase();
        W("U 路　**冷侧预算放宽之后，每片舌保温的可行窗口宽多少**　W08（" + d0.Name + "）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W();
        W("⚠⚠ **这是演示，不是设计。**");
        W("　· 回答的只有一句：冷侧那 5 度放宽，窗口会变多宽。默认值本轮一位没动，仍是 "
          + $"{LineCase.ThermocoupleErrorK:0.###} K；放宽多少由工程师在参数表里填。");
        if (nav)
        {
            W($"　· 网格 = **导航网格**（整线算例缺省：细区 {navCase.MeshFineMm:0.0} mm／粗区 {navCase.MeshCoarseMm:0.0} mm／细区半径 {navCase.MeshFineRadiusMm:0.0} mm），"
              + "**不是判决那张**。导航网格上这两条温度判据实测偏大 ⇒ 本文件的窗口**只能看相对变化**，");
            W("　　绝对值不可引用、不可据以出图、不可写进交付。要判决必须在判决网格上重量（InsulWindow 的默认口径）。");
            W("　⚠ 这张导航网格是 BuildCase 的缺省配方（细区半径 50），**不是**求解器第一遍那张（细区半径 59）—— 两者不是同一个配方，别拿本文件的数去和求解轨迹对。");
        }
        else
        {
            var (rf, rr) = MeshVerify.RequiredMeshFor(d0);
            W($"　· 网格 = **判决用的那张**（MeshVerify.RequiredMeshFor：细区 {rf:0.000} mm／细区半径 {rr:0.0} mm），与交付判定同一张。");
        }
        W("　· 两个预算在**同一次运行、同一个进程、同一份设计、同一张网格**上量，只换那一个数。");
        W($"　· 设计出处：{R48LW08NavDesign.Source}");
        W();

        string live = Path.Combine(Path.GetTempPath(), $"R48_U_冷侧演示_{stamp}_进行中.log");
        var probe = new Probe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W();
        Flush();

        var results = new InsulWindow.Result[ColdBudgets.Length];
        for (int b = 0; b < ColdBudgets.Length; b++)
        {
            double cold = ColdBudgets[b];
            var p = new DesignInputs { ColdUnderTcAllowK = cold };     // 热侧不动 —— 用户问的是冷侧
            W($"═══════ 冷侧预算 {cold:0.###} K（热侧仍 {p.HotOverTcAllowK:0.###} K）═══════");
            probe.Report($"── 冷侧预算 {cold:0.###} K 开始");
            var opt = new InsulWindow.Options { UseNavigationMesh = nav, Cap = TimeSpan.FromHours(3.0) };
            var res = InsulWindow.Measure(d0, p, opt, probe, CancellationToken.None);
            results[b] = res;

            // 解值那一点的判据值：两个预算下**值应当一位不动**（放宽的是限值，不是数）——当场对给自己看
            var solved = res.Plates.Select(x => x.Points.First(y => y.IsSolved)).ToArray();
            W("解值那一点（四片同一次解，值与预算无关；限值那一列才跟着预算走）：");
            W("片\t最热铂高出热偶读数 K\t管根低于热偶读数 K\t管孔净流入 W\t可行？");
            for (int j = 0; j < res.Plates.Length; j++)
                W($"{res.Plates[j].Name}\t{Fmt(solved[j].HotK)}\t{Fmt(solved[j].ColdK)}\t{Fmt(solved[j].NetFluxW)}\t"
                  + (solved[j].Feasible is null ? "判不了" : solved[j].Feasible == true ? "过" : "不过"));
            W();
            W(res.Report());
            W();
            Flush();
        }

        // ═══════ 并列：窗口变宽多少 ═══════
        W($"═══════ 窗口宽度对照（同一次运行、同一张{(nav ? "导航" : "判决")}网格，只换冷侧预算）═══════");
        W($"片\t解值 mm\t冷侧 {ColdBudgets[0]:0.###} K 窗口\t宽 mm\t可落档\t冷侧 {ColdBudgets[1]:0.###} K 窗口\t宽 mm\t可落档\t宽了多少 mm");
        var a = results[0]; var c2 = results[1];
        for (int j = 0; j < Math.Min(a.Plates.Length, c2.Plates.Length); j++)
        {
            var pa = a.Plates[j]; var pb = c2.Plates[j];
            W($"{pa.Name}\t{pa.SolvedMm:0.###}"
              + $"\t{Win(pa)}\t{Wid(pa)}\t{Lay(pa)}"
              + $"\t{Win(pb)}\t{Wid(pb)}\t{Lay(pb)}"
              + $"\t{(double.IsNaN(pa.WidthMm) || double.IsNaN(pb.WidthMm) ? "—" : (pb.WidthMm - pa.WidthMm).ToString("+0.###;-0.###;0"))}");
        }
        W();
        W($"结论（冷侧 {ColdBudgets[0]:0.###} K，= 现在的默认）：{a.Verdict}");
        W($"结论（冷侧 {ColdBudgets[1]:0.###} K，**演示**）：{c2.Verdict}");
        W();
        W(nav ? "⚠ 再说一次：上面两行都是**导航网格**上的数，只能看相对变化。判决要在判决网格上重量。"
              : "⚠ 上面两行是**判决网格**上的数（与交付判定同一张）。但这仍是**演示**：默认值本轮一位没动，放宽多少由工程师在参数表里填。");
        W("⚠ 本档没有、也不许有「所以应该改成 10」这句话 —— 那是工程师在参数表里填的事。");
        W();
        sw.Stop();
        W($"── 总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        W("出处：窗口 = InsulWindow.Measure（APP 终验点的就是它）；整线解 = LineRunner.Run；判定 = LineResult.AllOk；"
          + "层厚 = InsulationSearch.LayerMm；裸舌 = SolverOptions.InsLoMm。每个数来自这一次运行（同一进程）。");
        Flush();
        _o.WriteLine(sb.ToString());

        // 门：两个预算真的跑出了数，且**判据值与预算无关**（放宽的是限值）
        Assert.Equal(a.Plates.Length, c2.Plates.Length);
        Assert.All(a.Plates, x => Assert.NotEmpty(x.Points));
        for (int j = 0; j < a.Plates.Length; j++)
        {
            var sa = a.Plates[j].Points.First(x => x.IsSolved);
            var sbp = c2.Plates[j].Points.First(x => x.IsSolved);
            Assert.Equal(sa.ColdK, sbp.ColdK, 9);      // 同一份设计同一张网格：判据值一位不动
            Assert.Equal(sa.HotK, sbp.HotK, 9);
        }
        // 放宽只会让窗口**不变窄**（冷侧是上界那一侧的约束之一）
        for (int j = 0; j < a.Plates.Length; j++)
            if (!double.IsNaN(a.Plates[j].WidthMm) && !double.IsNaN(c2.Plates[j].WidthMm))
                Assert.True(c2.Plates[j].WidthMm >= a.Plates[j].WidthMm - 1e-9,
                    $"片{j}：放宽冷侧之后窗口反而变窄（{a.Plates[j].WidthMm:0.###} → {c2.Plates[j].WidthMm:0.###}）—— 先查是不是量错了");
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");
    private static string Win(InsulWindow.PlateWindow p)
        => p.SolvedFeasible ? $"[{p.LoMm:0.###}, {p.HiMm:0.###}]" + (p.OpenLo || p.OpenHi ? "（边界没探到）" : "") : "解值自己就不过";
    private static string Wid(InsulWindow.PlateWindow p) => double.IsNaN(p.WidthMm) ? "—" : p.WidthMm.ToString("0.###");
    private static string Lay(InsulWindow.PlateWindow p) => p.LayerMm.Length == 0 ? "**一个都没有**" : string.Join("/", p.LayerMm.Select(m => m.ToString("0.###")));

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
