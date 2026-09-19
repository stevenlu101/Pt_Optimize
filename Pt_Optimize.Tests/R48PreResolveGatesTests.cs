using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写）：**重解两个设计之前两位把关人列为「必做」的两件事**。
///
/// ① 常驻数值讨论人：外层耦合停在离不动点多远，**必须是个数、逐档印出来**。
///    实测法兰增量温降最后一步变化 +0.820 K，小于耦合停机容差 1.0 K ——
///    那次「在摆」分不清是网格还是耦合停机造成的，而此前每档的这个数一次都没记下来（只活在说明文字里）。
/// ② 常驻物理讨论人：**峰落在粗区 ⇒ 不许判收敛**。
///    MeshAdapt.PeakVerdict 原来只打印、只挂在结果上，既不影响 Converged，界面也一处不读；
///    而且只设不清，早档峰在粗区、后档盖住了照样挂着。
/// </summary>
public class R48PreResolveGatesTests
{
    [Fact]
    public void 峰落在粗区的原话不许带判据代号()
    {
        string? s = MeshAdapt.PeakVerdict(peakRadiusMm: 40, innerRadiusMm: 31, fineRadiusMm: 30);
        Assert.NotNull(s);
        Assert.DoesNotContain("②", s!);               // 这句要进界面，界面不许出现判据代号
        Assert.Contains("热点", s!);
        Assert.Null(MeshAdapt.PeakVerdict(29, 31, 59)); // 29 + 余量 10 ≤ 59：盖住了且留足余量，不说
        // ★ 2026-09-14 物理把关人：判峰位要带与定半径同一个余量 —— 峰在 R−0.5 处不算盖住
        Assert.NotNull(MeshAdapt.PeakVerdict(52, 31, 59));
        Assert.Equal(10.0, MeshAdapt.PeakMarginMm);
        // ★ 峰位算不出来不许算过（原来返回 null，与 ⑤⑥ 同病）
        Assert.NotNull(MeshAdapt.PeakVerdict(double.NaN, 31, 59));
    }

    [Fact]
    public void 峰落在粗区时加密复核不许判收敛_界面要照印()
    {
        string mv = Code("Pt_Optimize/Core/MeshVerify.cs");
        Assert.Contains("res.PeakOutsideFine = peakBad;", mv);                          // 每档以这一档为准，不许只设不清
        Assert.DoesNotContain("if (peakBad is not null) { progress?.Report(\"   \" + peakBad); res.PeakOutsideFine = peakBad; }", mv);
        int g = mv.IndexOf("if (res.PeakOutsideFine is not null) res.Converged = false;", StringComparison.Ordinal);
        int u = mv.IndexOf("res.Undecidable = res.HitCellCap", StringComparison.Ordinal);
        Assert.True(g > 0, "峰在粗区没有挡住收敛 —— 「这次的圆盘区最高温不算数」只会被打印");
        Assert.True(u > g, "挡收敛那一行必须排在判不了之前，否则判不了用的是没挡过的 Converged");

        // 峰在粗区时判词必须以 ✗ 开头 —— 读的人只看第一句
        Assert.Contains("\"✗ **本次复核不算数** —— \" + res.PeakOutsideFine", mv);
        // 热点取三种里最远的：圆盘区、舌片区、局部热稳定（只看圆盘区时这道门永远不会响）
        Assert.Contains("new[] { f.DiscMaxRMm, f.TabMaxRMm, f.LocalStabRMm }", mv);

        string ui = Code("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("res.PeakOutsideFine is { Length: > 0 }", ui);
    }

    [Fact]
    public void 外层耦合剩余误差必须是个数_加密复核逐档印出()
    {
        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("public double CoupleRemainK = double.NaN;", lr);
        Assert.Contains("lastRemainK = Math.Max(remain, resK * ampWorst);", lr);   // 停机要两支都过，距离取两支里大的
        Assert.Contains("res.CoupleRemainK = lastRemainK;", lr);

        string mv = Code("Pt_Optimize/Core/MeshVerify.cs");
        Assert.Contains("外层耦合剩余误差估计 {r.CoupleRemainK:0.00} K", mv);
        Assert.Contains("与耦合停机噪声分不开", mv);
        // 档间变化是两次独立停机之差 ⇒ 噪声按两档之和比，不是只看这一档
        Assert.Contains("r.CoupleRemainK + (double.IsNaN(prevRemainK) ? 0 : prevRemainK)", mv);
    }

    private static string Code(string rel)
        => string.Join("\n", File.ReadAllText(Path.Combine(Root(), rel)).Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
