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
/// R48 追查（2026-09-13，Opus 5 写）：**整线加密时，到底是哪一片的抽热在跳**。
///
/// 用户的质疑（09-13）：「网格 1 → 0.5 → 0.25，管孔净流入 5.090 → 7.134 → 5.068，这真的是噪声吗？
/// 动网格已是解决噪声的终极手段。」——这句把我从「网格噪声」那个错框架里拉出来了。加密不收敛就不是离散误差。
///
/// 已经排掉的：
///   · **不是单片场解的问题**：同一片管侧固定时五档单调收敛（13.059 → 11.712 → 11.204 → 10.994 → 10.871 W）。
///   · **不是「报的数与回灌的数不一致」**：LineRunner 回灌的就是 res.Flanges[i].QFromTubeW 本身。
///
/// 还没排掉的关键矛盾：**净流入跳 2 W，而法兰增量温降只动 0.2 K**。
///   两者本该是同一件事的两面（多抽热 ⇒ 管子多降温 ⇒ 增量温降变大），按设计层面的比例 2 W 该对应约 6 K。
///   一个可能：净流入判据取的是**抽热最小的那一片**，增量温降取的是**最差的那一段** ——
///   若跳的是某一片而它不在最差段上，两个数就会脱节。
///
/// 本仪器：同一设计、同一条链，三档网格各解一次整线，打印**逐片**抽热与**逐段**增量温降、
/// 以及判据各自取的是哪一片／哪一段。只记录、不判定。
/// </summary>
[Trait("速度", "慢")]
public class R48PerPlateGridTests
{
    private readonly ITestOutputHelper _out;
    public R48PerPlateGridTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 量整线加密时逐片抽热与逐段增量温降()
    {
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say($"R48 逐片追查（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　管壁 0.8 记录，整线全耦合，只换网格");
        Say("");

        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();
        foreach (double h in new[] { 1.0, 0.5, 0.25 })
        {
            var lc = d.BuildCase(p, checkRamp: false);
            lc.MeshFineMm = h;
            lc.MeshCoarseMm = Math.Max(h, 11.0 * h / 2.0);
            lc.MeshInnerMm = h; lc.MeshInnerRadiusMm = 0;      // 整档一起加密（R48 口径）
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc, null, default);
            sw.Stop();
            if (!r.Ok) { Say($"h={h:0.00} 解不出来：{r.Message}"); continue; }

            double Q(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            string W(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Where ?? "—";
            Say($"── 网格 {h:0.00} mm　{r.MeshCells} 格　{sw.Elapsed.TotalMinutes:0.0} 分　外层耦合收敛 {r.Converged}");
            Say($"   判据：管孔净流入 {Q(LineResult.Key.NetFlux):0.000} W（取自 {W(LineResult.Key.NetFlux)}）"
              + $"　法兰增量温降 {Q(LineResult.Key.FlangeDip):0.000} K（取自 {W(LineResult.Key.FlangeDip)}）");
            Say("   逐片：" + string.Join("　", r.Flanges.Select((f, j) =>
                $"片{j} {f.Name} 抽热 {f.QFromTubeW:+0.000;-0.000} 管根 {f.TRootC:0.0}")));
            Say("   逐段：" + string.Join("　", r.Segments.Select((s, i) =>
                $"段{i} 增量温降 A {s.FlangeDipAK:+0.000;-0.000} / B {s.FlangeDipBK:+0.000;-0.000}"
              + $"（基线 {s.BaseTRootAC:0.0}/{s.BaseTRootBC:0.0} 实际 {s.TRootAC:0.0}/{s.TRootBC:0.0}）")));
            Say("");
        }
        Say("读法：");
        Say("  · 若某一片的抽热在跳而别片稳 ⇒ 那一片的几何/边界有特殊之处，去查它。");
        Say("  · 若「净流入取自哪一片」在三档之间换了片 ⇒ 判据在不同片之间跳，数字不可比，是判据取法的问题。");
        Say("  · 若逐片抽热都稳、只有判据那一行在跳 ⇒ 问题在判据怎么挑，不在场解。");

        File.WriteAllText(Path.Combine(SolutionDir(), "deliverable", "R48_逐片追查_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
