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
/// ★★★★★ R48 探针（2026-09-13，Opus 5 写）：**舌保温的可行窗口在哪？**
///
/// ══ 为什么要问这一条
///
/// 09-13 重解之后两个设计都卡在 管孔净流入 变负（热往管子里灌），而 法兰增量温降 反而剩一大截余量
/// （0.57～4.94 K 对限值 10）。两条判据被同一根旋钮往相反方向推：
///   舌保温 ↑ ⇒ 法兰更热 ⇒ 增量温降 ↓（变好），但管孔净流入 ↓（变坏）
/// ⇒ 保温**加过头**就会把一个本来可行的设计推成不可行。
///
/// 而求解器的两遍机制正好有让它加过头的路子：
///   第一遍跑**导航网格**（2.0 mm）定位，第二遍从第一遍的解出发、**只增不减**。
///   导航网格若把增量温降算大，第一遍就会多加保温；第二遍**下不来**。
///   「收敛到最小可行点」这句话只有在第一遍的点**低于**真根时才成立。
///
/// 实测佐证（同一设计、同一条链，只换网格）：
///   法兰增量温降 = 6.618 K（22:1 那张网格）／4.935（1.0 mm）／0.573（0.5）／1.408（0.25）
///   —— 网格越粗，这条判据读数越大 ⇒ 导航网格（2.0 mm）只会更大。
///
/// ══ 本仪器
///
/// 在**算得准的那张网格**（0.5 mm，整张一起缩 = 复核第 2 档）上，把四片舌保温一起扫过一串值，
/// 逐点记 管孔净流入 与 法兰增量温降。只记录、不判定 —— 要看的是**两条判据的窗口有没有交集**。
///
/// 读法：
///   · 若存在某个保温值让两条同时过 ⇒ 可行点存在，求解器是**求过头了**，
///     病在「第一遍用导航网格 + 第二遍只增不减」，不在设计本身。
///   · 若整条扫下来都没有交集 ⇒ 这根旋钮救不了，得换别的旋钮或承认该解不存在。
///
/// ⚠ 慢：每点一次整线全耦合场解（0.5 mm 约 5 分钟）。每点算完**立刻落盘**，
///   跑一半也看得到已经算出来的那一半（用户 09-13：长跑每轮要出结果）。
/// </summary>
[Trait("速度", "慢")]
public class R48InsulWindowTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulWindowTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 扫舌保温_看两条判据的窗口有没有交集()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_舌保温窗口_2026-09-13.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 探针：舌保温的可行窗口（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8，板厚取闭式下角，只扫舌保温（四片同值）；网格 = 复核第 2 档（0.5 mm，整张一起缩）。");
        Say("");
        Say($"{"舌保温mm",9}{"单元数",8}{"管孔净流入W",13}{"圆盘区最高温K",15}{"法兰增量温降K",15}{"截面J",8}{"全过",6}{"用时s",7}");

        var p = new DesignInputs();
        double[] sweep = { 0.30, 1.00, 1.50, 2.00, 3.00, 4.60 };
        var (_, radius) = MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone(), p);

        foreach (double ins in sweep)
        {
            var d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };          // 闭式下角（按 J=10 的圆盘侧截面）
            d.TabInsulMm = new[] { ins, ins, ins, ins };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d = d.Fit();
            d.SizeTongues(p);                                          // 舌片厚是闭式的，不是旋钮

            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);                // 与复核第 2 档同一个配方
            var sw = Stopwatch.StartNew();
            double last = 0;
            var tick = new Progress<string>(m =>
            {
                if (sw.Elapsed.TotalSeconds - last < 30) return;
                last = sw.Elapsed.TotalSeconds;
                Say($"     · 保温 {ins:0.00}　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
            });
            LineResult r;
            try { r = LineRunner.Run(lc, tick, default); }
            catch (Exception ex) { Say($"{ins,9:0.00}　解不出来：{ex.Message}"); continue; }
            sw.Stop();
            if (!r.Ok) { Say($"{ins,9:0.00}　解不出来：{r.Message}"); continue; }

            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            bool Ok(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Ok ?? false;
            Say($"{ins,9:0.00}{r.MeshCells,8}"
              + $"{V(LineResult.Key.NetFlux),13:0.000}{(Ok(LineResult.Key.NetFlux) ? "" : "✗")}"
              + $"{V(LineResult.Key.DiscTemp),15:0.000}{(Ok(LineResult.Key.DiscTemp) ? "" : "✗")}"
              + $"{V(LineResult.Key.FlangeDip),15:0.000}{(Ok(LineResult.Key.FlangeDip) ? "" : "✗")}"
              + $"{V(LineResult.Key.SectionJ),8:0.00}{(r.AllOk ? "  ✓" : "  ✗"),6}{sw.Elapsed.TotalSeconds,7:0}");
        }

        Say("");
        Say("读法：");
        Say("  · 有某一行「全过 ✓」⇒ 可行点存在。那么 09-13 重解得到的 4.60/2.10/2.90/7.50 就是**求过头了**，");
        Say("    病在「第一遍用导航网格定位 + 第二遍只增不减」，不在设计本身。");
        Say("  · 整列都没有 ✓ ⇒ 这根旋钮救不了这两条判据，要换旋钮或承认该解不存在。");
        Say("  · 注意逐片保温本来可以各不相同（求解器就是逐片求的）；本扫描四片同值，只看趋势与窗口在不在。");

        // 门：这一扫必须真的扫出数来（空转的仪器比没有更坏）
        Assert.Contains("管孔净流入", sb.ToString());
        Assert.True(sb.ToString().Split('\n').Count(l => l.Contains("✓") || l.Contains("✗")) >= 3,
            "扫描没有产生足够的数据行 —— 仪器空转了，别拿它下结论");
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
