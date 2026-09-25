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
/// ★★★★★ R48 探针（2026-09-14，Opus 5 写）：**两条判据卡在不同的片上 —— 只动片2 能不能两头都拿厚裕量？**
///
/// ══ 下行扫描（R48DescentWindowTests）逼出来的结构性发现
///
/// 沿 λ × (4.60, 2.10, 2.90, 7.50) 下行，λ 从 0.97 走到 0.96 时**只有片2 变了**（保温 2.8 → 2.7）：
/// <code>
///   λ=0.97  4.4/2.0/2.8/7.2   管孔净流入 −0.027 ✗   法兰增量温降 5.377
///   λ=0.96  4.4/2.0/2.7/7.2   管孔净流入 +1.201 ✓   法兰增量温降 5.377   ← 一模一样
/// </code>
/// ⇒ **净流入由片2 定，增量温降由别的片定，两者互不干涉。**
/// 这两条判据不是「互相打架只能折中」的关系；逐片调就能同时满足。
///
/// ══ 于是该问的
///
/// λ 那条直线把四片**一起**缩，为了救净流入，把增量温降的余量也一起赔掉了：
/// <code>
///   λ=1.00  增量温降 0.586（余 9.4 K）   净流入 −1.760 ✗
///   λ=0.96  增量温降 5.377（余 4.6 K）   净流入 +1.201 ✓
///   λ=0.95  增量温降 9.717（余 0.3 K ← 比容差 1.0 还小，等于没裕量）  净流入 +1.794 ✓
/// </code>
/// 若把片0/1/3 留在 λ=1.00 的 4.6/2.1/7.5（增量温降 0.586 不动），**只削片2**，
/// 应当能同时拿到「增量温降余 9 K 以上」与「净流入为正且高于噪声」。
///
/// ══ 判据（这个探针要回答什么）
///
///   · 找到某个片2 值，两条判据的裕量**都** ≥ 3 倍各自容差（净流入容差 0.5 W、增量温降 1.0 K）
///     ⇒ 存在**裕量厚**的可行点，求解器「只增不减」错过的不只是一个边角点。
///   · 削片2 时增量温降跟着明显变差 ⇒ 「互不干涉」这个结论是假的，当场推翻，回头重看。
/// </summary>
[Trait("速度", "慢")]
public class R48Plate2OnlyTests
{
    private readonly ITestOutputHelper _out;
    public R48Plate2OnlyTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 只削片2看两条判据能不能都拿厚裕量()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_只削片2_2026-09-14.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 探针：只削片2（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8。片0/1/3 钉在求解器解出的 4.6/2.1/7.5 不动，只扫片2。");
        Say("网格 = 复核第 2 档（0.5 mm，整张一起缩）。容差：净流入 0.5 W／增量温降 1.0 K。");
        Say("");
        Say($"{"片2保温",9}{"管孔净流入W",13}{"裕/容差",10}{"增量温降K",12}{"裕/容差",10}{"圆盘区K",10}{"截面J",8}{"全过",6}{"用时s",7}");

        var p = new DesignInputs();
        var (_, radius) = MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone(), p);
        bool thick = false;

        foreach (double ins2 in new[] { 2.90, 2.50, 2.10, 1.70 })
        {
            var d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d.TabInsulMm = new[] { 4.60, 2.10, ins2, 7.50 };
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d = d.Fit();
            d.SizeTongues(p);

            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var sw = Stopwatch.StartNew();
            double last = 0;
            var tick = new Progress<string>(m =>
            {
                if (sw.Elapsed.TotalSeconds - last < 40) return;
                last = sw.Elapsed.TotalSeconds;
                Say($"     · 片2={ins2:0.0}　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
            });
            LineResult r;
            try { r = LineRunner.Run(lc, tick, default); }
            catch (Exception ex) { Say($"{ins2,9:0.00}　解不出来：{ex.Message}"); continue; }
            sw.Stop();
            if (!r.Ok) { Say($"{ins2,9:0.00}　解不出来：{r.Message}"); continue; }

            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            double flux = V(LineResult.Key.NetFlux), dip = V(LineResult.Key.FlangeDip);
            double fluxRatio = flux / 0.5, dipRatio = (10.0 - dip) / 1.0;
            if (r.AllOk && fluxRatio >= 3.0 && dipRatio >= 3.0) thick = true;

            Say($"{ins2,9:0.00}{flux,13:+0.000;-0.000}{fluxRatio,10:0.0}×"
              + $"{dip,12:0.000}{dipRatio,10:0.0}×{V(LineResult.Key.DiscTemp),10:0.000}"
              + $"{V(LineResult.Key.SectionJ),8:0.00}{(r.AllOk ? "  ✓" : "  ✗"),6}{sw.Elapsed.TotalSeconds,7:0}");
        }

        Say("");
        Say(thick
            ? "★ **找到裕量都 ≥3 倍容差的可行点** ⇒ 可行域不止一个边角点，"
            + "求解器「只增不减」错过的是一整片有厚裕量的区域。"
            : "★ 没找到两条裕量都 ≥3 倍容差的点 —— 这条线上只能二选一，"
            + "得再找别的旋钮，或承认这个几何的窗口就是薄的。");
        Say("对照（下行扫描，四片一起缩）：λ=1.00 增量温降 0.586／净流入 −1.760；"
          + "λ=0.96 5.377／+1.201；λ=0.95 9.717／+1.794。");

        Assert.True(sb.ToString().Split('\n').Count(l => l.Contains("✓") || l.Contains("✗")) >= 2,
            "扫描没产生足够数据行 —— 仪器空转了，别拿它下结论");
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
