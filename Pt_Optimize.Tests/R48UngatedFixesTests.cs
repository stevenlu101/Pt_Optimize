using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 补门（2026-09-14，Opus 5 写）：**给 09-14 落地却没有门守着的五处改动补门。**
///
/// 程序中途退出后逐项核对磁盘，发现这五处改动**写进了生产代码、却没有任何测试会因为改回旧写法而变红**：
///   ① ⑤ 舌片自由段：算不出舌长的片曾被静默跳过，全片 NaN 时凭初值 +∞ 报「过」
///   ② ⑥ 圆盘盖得住管孔＋焊脚：同形，全片 NaN 时凭初值报「过」
///   ③ ③ 法兰增量温降：部分段 NaN 被静默丢掉
///   ④ 图纸路径 ApplyFinalMesh：不缩粗区，细粗比随加密 11→22→44
///   ⑤ 进度条：认不出百分比的行把实心条打回走马灯
///   ⑥ 命令行复核：判不了被印成「这个设计不过」
///
/// 能做**行为门**的一律做行为门（⑤⑥ 直接调 GeometryScreen.Judge、配方直接调 ApplyFinalMesh）；
/// 生产代码藏在大方法里够不着的（③、进度条、命令行分支）才做源码门，并写明为什么只能如此。
/// </summary>
public class R48UngatedFixesTests
{
    private static FlangePlate[] RealPlates()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();
        double floor = d.DiscFloorMm(p);
        return Enumerable.Range(0, d.FlangeCount).Select(j => d.Plate(j, floor)).ToArray();
    }

    private static ConstraintOut Find(ConstraintOut[] cs, string prefix)
        => cs.Single(c => c.Name.StartsWith(prefix, StringComparison.Ordinal));

    // ───────────────────────── ⑤ 舌片自由段（行为门）

    [Fact]
    public void 舌长算不出来时自由段判据报无法判定而不是过()
    {
        var plates = RealPlates();
        foreach (var g in plates) g.TabEndXMm = double.NaN;      // 图纸路径认不出伸出的舌片时就是这样
        var c = Find(GeometryScreen.Judge(plates, 40, 100), "⑤");
        Assert.True(c.Undetermined, "全片舌长 NaN 时 ⑤ 必须报「无法判定」—— 旧写法凭初值 +∞ 报「过」");
        Assert.False(c.Ok, "判不了不许当成过（这是硬安全线）");
        Assert.Contains("入口", c.Where);                          // 必须点名是哪几片
    }

    [Fact]
    public void 只有一片舌长算不出来时自由段判据也报无法判定()
    {
        var plates = RealPlates();
        plates[2].TabEndXMm = double.NaN;
        var c = Find(GeometryScreen.Judge(plates, 40, 100), "⑤");
        Assert.True(c.Undetermined, "任何一片判不了，整条就判不了 —— 不许只报剩下几片里最差的");
        Assert.Contains("共用2", c.Where);
    }

    // ───────────────────────── ⑥ 圆盘盖得住管孔＋焊脚（行为门）

    [Fact]
    public void 盘半径算不出来时圆盘覆盖判据报无法判定而不是过()
    {
        var plates = RealPlates();
        foreach (var g in plates) g.DiscRadiusMm = double.NaN;
        var c = Find(GeometryScreen.Judge(plates, 40, 100), "⑥");
        Assert.True(c.Undetermined, "全片盘半径 NaN 时 ⑥ 必须报「无法判定」—— 旧写法凭初值 +∞ 报「过」");
        Assert.False(c.Ok);
    }

    [Fact]
    public void 正常几何上五六两条照常判定_补门不许把能判的也判成判不了()
    {
        var cs = GeometryScreen.Judge(RealPlates(), 40, 100);
        Assert.False(Find(cs, "⑤").Undetermined);
        Assert.False(Find(cs, "⑥").Undetermined);
        Assert.False(double.IsNaN(Find(cs, "⑤").Actual));
        Assert.False(double.IsNaN(Find(cs, "⑥").Actual));
    }

    // ───────────────────────── 图纸路径的加密配方（行为门）

    [Fact]
    public void 图纸路径终局细网格必须整张一起缩()
    {
        var lc0 = DesignSpec.W08.Clone().BuildCase(new DesignInputs(), checkRamp: false);
        double ratio0 = lc0.MeshCoarseMm / lc0.MeshFineMm;
        var opt = new FlangeAutoSizer.Options { FinalMeshFineMm = 0.5, FinalMeshFineRadiusMm = 59.0 };
        var lc = FlangeAutoSizer.ApplyFinalMesh(lc0, opt);
        Assert.Equal(0.5, lc.MeshFineMm, 9);
        Assert.Equal(ratio0, lc.MeshCoarseMm / lc.MeshFineMm, 9);   // 细粗比不变 = 自相似
        Assert.Equal(59.0, lc.MeshFineRadiusMm, 9);
        Assert.Equal(lc0.MeshCoarseMm, 11.0, 9);                    // 原算例不许被改（ApplyFinalMesh 要克隆）
    }

    [Fact]
    public void 配方只有一处的门要覆盖图纸路径与逐级求解器()
    {
        foreach (string f in new[] { "Pt_Optimize/Core/FlangeAutoSizer.cs" })
        {
            string code = CodeOnly(f);
            Assert.DoesNotContain("MeshCoarseMm *=", code);
            Assert.DoesNotContain("lc.MeshFineMm = opt.FinalMeshFineMm", code);
        }
        // LevelSolver 生产代码零调用（2026-09-14 读码核实），但它仍手写两处旧配方。
        // 不删它、也不让它悄悄留着：要求它顶上挂着「未接生产、配方未统一」的告示，
        // 谁把它接进生产，得先读到这句话。
        string ls = Src("Pt_Optimize/Core/LevelSolver.cs");
        Assert.Contains("未接进生产", ls);
    }

    // ───────────────────────── ③ 法兰增量温降（源码门：判据构造在 LineRunner.Run 的大方法里，够不着）

    [Fact]
    public void 增量温降判据不许静默丢掉判不了的段()
    {
        string s = CodeOnly("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("var blindDip = segs.Where(s => double.IsNaN(s.FlangeDipK))", s);
        Assert.Contains("blindDip.Length > 0 ? Array.Empty<SegmentOut>()", s);
    }

    // ───────────────────────── 进度条（源码门：进度回调是界面里的 lambda）

    [Fact]
    public void 进度条认不出百分比时保留上一个()
    {
        string s = CodeOnly("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("int lastPct = -1;", s);
        Assert.Contains("if (pct >= 0) lastPct = pct; else pct = lastPct;", s);
    }

    // ───────────────────────── 命令行复核（源码门：分支在 Program 的本地函数里）

    [Fact]
    public void 命令行复核判不了时不许印成设计不过()
    {
        string s = CodeOnly("Pt_Optimize/Program.cs");
        int u = s.IndexOf("if (mvv.Undecidable)", StringComparison.Ordinal);
        int f = s.IndexOf("else if (mvv.Line is { } lvv && !lvv.AllOk)", StringComparison.Ordinal);
        Assert.True(u > 0, "命令行复核没读 Undecidable —— 判不了会被印成「这个设计不过」");
        Assert.True(f > u, "「不过」那一支必须排在「判不了」之后且互斥（else if）");
    }

    private static string CodeOnly(string rel)
        => string.Join("\n", Src(rel).Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Src(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }
}
