using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48 仪器（2026-09-13，Opus 5 写）：**导航网格（2.0 mm）到底把判据读偏多少、偏哪一边？**
///
/// ══ 为什么非量不可
///
/// 求解器两遍机制的最小性论证是：第一遍在导航网格定位，第二遍从它出发、**只增不减**。
/// 这句话只有在**第一遍的点不高于真根**时才成立。若导航网格把 法兰增量温降 读大，
/// 第一遍就会多加舌保温，第二遍**下不来** —— 而保温加过头正是把 管孔净流入 压成负数的原因。
///
/// ⚠ 我一度把这条当成已知事实报出去过，其实**没量过**。同族三档的实测是
/// 4.935（1.0 mm）→ 0.573（0.5）→ 1.408（0.25），**不单调**；
/// 「2.0 mm 只会更大」是推测不是数据。本仪器就是去把 2.0 mm 那一档补上。
///
/// ══ 做法
///
/// 同一个设计（09-13 重解落到的那一点），同一个自相似网格族（`MeshAdapt.RefineWholeMesh`），
/// 四档 2.0／1.0／0.5／0.25 各解一次整线，逐档记两条判据。只记录、不判定。
///
/// 读法：
///   · 2.0 mm 的 法兰增量温降 明显高于收敛值 ⇒ 导航网格会让第一遍多加保温，
///     「只增不减」的最小性论证在这条判据上不成立，两遍机制要改。
///   · 2.0 mm 的读数已经接近收敛值 ⇒ 过度保温另有来源，别赖导航网格。
/// </summary>
[Trait("速度", "慢")]
public class R48NavGridBiasTests
{
    private readonly ITestOutputHelper _out;
    public R48NavGridBiasTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 量导航网格把判据读偏多少()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_导航网格偏差_2026-09-13.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 仪器：导航网格的读数偏差（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8，设计 = 09-13 重解落到的那一点（板厚 0.73/1.26/1.26/0.73，舌保温 4.60/2.10/2.90/7.50）。");
        Say("同一个自相似网格族，只换档位。2.0 mm 就是求解器第一遍用的那张。");
        Say("");
        Say($"{"网格mm",8}{"单元数",8}{"管孔净流入W",13}{"圆盘区最高温K",15}{"法兰增量温降K",15}{"全过",6}{"用时s",7}");

        var p = new DesignInputs();
        var d0 = DesignSpec.W08.Clone();
        d0.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d0.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d0.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d0 = d0.Fit();
        d0.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d0);

        foreach (double h in new[] { 2.0, 1.0, 0.5, 0.25 })
        {
            var lc = d0.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, h, radius);
            var sw = Stopwatch.StartNew();
            double last = 0;
            var tick = new Progress<string>(m =>
            {
                if (sw.Elapsed.TotalSeconds - last < 30) return;
                last = sw.Elapsed.TotalSeconds;
                Say($"     · {h:0.00} mm　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
            });
            LineResult r;
            try { r = LineRunner.Run(lc, tick, default); }
            catch (Exception ex) { Say($"{h,8:0.00}　解不出来：{ex.Message}"); continue; }
            sw.Stop();
            if (!r.Ok) { Say($"{h,8:0.00}　解不出来：{r.Message}"); continue; }

            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            Say($"{h,8:0.00}{r.MeshCells,8}{V(LineResult.Key.NetFlux),13:0.000}"
              + $"{V(LineResult.Key.DiscTemp),15:0.000}{V(LineResult.Key.FlangeDip),15:0.000}"
              + $"{(r.AllOk ? "  ✓" : "  ✗"),6}{sw.Elapsed.TotalSeconds,7:0}");
        }

        Say("");
        Say("读法：2.0 mm 是求解器第一遍用的网格。它把 法兰增量温降 读得比收敛值高多少，");
        Say("      就是第一遍会多加多少保温 —— 而第二遍只增不减，多加的下不来。");

        Assert.True(sb.ToString().Split('\n').Count(l => l.Contains("✓") || l.Contains("✗")) >= 2,
            "至少要量出两档才有对照 —— 仪器空转了，别拿它下结论");
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
