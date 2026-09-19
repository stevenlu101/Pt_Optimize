using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-13，Opus 5 写）：**「加密」只准有一个配方，四个调用方共用。**
///
/// 病历（09-13 正式重解两个设计都咬上）：求根的 <c>--fine</c> 那一遍只设网格**尺寸**，
/// 粗区留在 11 mm、内带不分；加密复核则整张一起缩。标称同为 0.500 mm，
/// 实测结论相反 —— 求根报「第 1 轮全过」，复核报 管孔净流入 −1.792 / −1.789 W（不过）。
///
/// Program.cs 那行注释当年就写着「求根的网格和判决的网格必须是同一张」，
/// 只是接线时漏了粗区与内带两维。⇒ 光靠注释守不住，要有门。
///
/// 本组全是**毫秒级**的（只造算例、只建网格，不解场），进快测。
/// </summary>
public class R48OneRefineRecipeTests
{
    [Fact]
    public void 加密配方缩粗区_且比例以原中带为基准()
    {
        var d = DesignSpec.W08.Clone();
        var lc = d.BuildCase(new DesignInputs(), checkRamp: false);
        double coarse0 = lc.MeshCoarseMm, fine0 = lc.MeshFineMm;

        MeshAdapt.RefineWholeMesh(lc, 0.5, fineRadiusMm: 59.0);
        Assert.Equal(0.5, lc.MeshFineMm, 9);
        Assert.Equal(coarse0 * 0.5 / fine0, lc.MeshCoarseMm, 9);     // 粗区同比例缩
        Assert.Equal(59.0, lc.MeshFineRadiusMm, 9);
        Assert.Equal(0.5, lc.MeshInnerMm, 9);                        // 内带与中带同尺寸 = 不分内带
        // 细粗比必须与加密前一致 —— 这就是「自相似」的定义
        Assert.Equal(coarse0 / fine0, lc.MeshCoarseMm / lc.MeshFineMm, 9);
    }

    [Fact]
    public void 半径参数给零时不动原值()
    {
        var lc = DesignSpec.W08.Clone().BuildCase(new DesignInputs(), checkRamp: false);
        double r0 = lc.MeshFineRadiusMm;
        MeshAdapt.RefineWholeMesh(lc, 0.5);
        Assert.Equal(r0, lc.MeshFineRadiusMm, 9);
    }

    /// <summary>
    /// 内带半径在「内带 = 中带」时是**惰性的** —— 这不是猜的，是 <c>ShellMesh.BuildFromField</c> 里
    /// 两条带重叠取最细的必然结果。求解器那一路传 0、复核那一路传 31.3，
    /// 两边必须建出**逐节点相同**的网格，否则「同一个配方」只是嘴上说说。
    /// </summary>
    [Fact]
    public void 内带等于中带时_内带半径给多少都建出同一张网格()
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        var g = d.Plate(1, d.DiscFloorMm(p));
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        g.HoleRadiusMm = holeR;
        double innerR = MeshAdapt.InnerRadiusFor(holeR, Math.Max(d.TabThickMm.Max(), d.WallMm));
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        const double H = 0.5;

        ShellMesh Build(double innerRadius)
        {
            var c = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(c, H, 59.0, innerRadius);
            var tf = FlangeMesher.Rasterize(g, Math.Max(H / 4, 0.02));
            return FlangeMesher.BuildFromField(tf, holeR, 0, c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                                               c.Base.BusbarClampLengthMm, c.MeshInnerMm, c.MeshInnerRadiusMm,
                                               g.TwoTabs, xa, za);
        }

        var a = Build(0);            // 求解器那一路
        var b = Build(innerR);       // 复核那一路
        Assert.Equal(a.CellCount, b.CellCount);
        Assert.Equal(a.Nodes.Count, b.Nodes.Count);
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            Assert.Equal(a.Nodes[i].X, b.Nodes[i].X, 9);
            Assert.Equal(a.Nodes[i].Z, b.Nodes[i].Z, 9);
        }
    }

    /// <summary>
    /// 源码门：求解器**不许**再自己设网格尺寸 —— 那正是「求根一张网格、判决另一张网格」的来源。
    /// 两处（逐轮的 EvalRaw、终局复核的 Finish）都必须走 <c>MeshAdapt.RefineWholeMesh</c>。
    /// </summary>
    [Fact]
    public void 求解器必须走共用配方_不许自己设网格()
    {
        string src = Src("Pt_Optimize/Core/Solver.cs");
        int calls = Count(src, "MeshAdapt.RefineWholeMesh(");
        Assert.True(calls >= 2,
            $"Solver.cs 只调了 {calls} 次 MeshAdapt.RefineWholeMesh —— "
          + "逐轮求根（EvalRaw）与终局复核（Finish）两处都要走共用配方，否则两遍又会用两张网格。");
        // 注释里出现是解释，代码里出现就是又各写各的了：把注释行剔掉再查。
        string code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                                                               && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        Assert.DoesNotContain("lc.MeshFineMm =", code);
        Assert.DoesNotContain("lcF.MeshFineMm =", code);
    }

    /// <summary>
    /// 源码门：加密配方只准有一处实现。四个调用方（解析复核、图纸复核、求根、终局复核）
    /// 都必须调它 —— 谁再手写一遍 <c>MeshCoarseMm *=</c>，这里当场红。
    /// </summary>
    [Fact]
    public void 加密配方只准有一处实现()
    {
        foreach (string f in new[] { "Pt_Optimize/Core/MeshVerify.cs", "Pt_Optimize/UI/LineDesignPage.cs", "Pt_Optimize/Core/Solver.cs" })
        {
            string code = string.Join("\n", Src(f).Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.True(!code.Contains("MeshCoarseMm *="),
                $"{f} 里又手写了一遍缩粗区 —— 配方只能有 MeshAdapt.RefineWholeMesh 一处，"
              + "否则四个调用方迟早再次各走各的（09-13 就是这么出的事）。");
        }
        Assert.Contains("lc.MeshCoarseMm *=", Src("Pt_Optimize/Core/MeshAdapt.cs"));
    }

    /// <summary>
    /// ★★★★★ R48 续（2026-09-14，Opus 5）：**细区半径是第四维，三遍必须同族。**
    ///
    /// 反方查出、我核实：加密配方只收了中带／粗区／内带三维，而 `MeshFineRadiusMm` 各调用方各给各的 ——
    /// 第一遍（导航）`FineRadiusMm = 0` ⇒ `EvalRaw` 的 `if (o.FineMm > 0)` 不触发 ⇒ 留在 LineCase 默认 **50**；
    /// 第二遍与加密复核是 **59**。实测同一设计、同一 2.0 mm，只差这一维：
    /// 管孔净流入 **+3.267（R=50）对 −6.533（R=59）**，差 9.8 W，**符号相反**（容差 0.5、限值 0）。
    /// ⇒「同一个设计点，只有网格不同」那句话在修好之前是假的：有两个变量在动。
    /// </summary>
    [Fact]
    public void 导航那一遍也要统一细区半径()
    {
        string s = Src("Pt_Optimize/Core/Solver.cs");
        // 导航选项不许再把半径清零
        Assert.DoesNotContain("navOpt.FineMm = 0; navOpt.FineRadiusMm = 0;", s);
        Assert.Contains("navOpt.FineRadiusMm = opt.FineRadiusMm;", s);
        // 且 EvalRaw 在导航档（FineMm ≤ 0）时要把半径接过去
        Assert.Contains("if (o.FineMm <= 0 && o.FineRadiusMm > 0)", s);
        Assert.Contains("MeshAdapt.RefineWholeMesh(lc, lc.MeshFineMm, o.FineRadiusMm);", s);
    }

    /// <summary>
    /// 行为门：导航档走共用配方之后，**尺寸与粗区必须逐位不变**（缩放比恒为 1），只统一半径与内带。
    /// 导航那一遍本来就该粗、该快 —— 统一半径不等于把它也加密了。
    /// </summary>
    [Fact]
    public void 导航档统一半径时不许把尺寸也改了()
    {
        var lc = DesignSpec.W08.Clone().BuildCase(new DesignInputs(), checkRamp: false);
        double fine0 = lc.MeshFineMm, coarse0 = lc.MeshCoarseMm;
        MeshAdapt.RefineWholeMesh(lc, lc.MeshFineMm, 59.0);
        Assert.Equal(fine0, lc.MeshFineMm, 9);
        Assert.Equal(coarse0, lc.MeshCoarseMm, 9);      // 比例 = 1 ⇒ 粗区不动
        Assert.Equal(59.0, lc.MeshFineRadiusMm, 9);
        Assert.Equal(fine0, lc.MeshInnerMm, 9);         // 内带 = 中带 ⇒ 等于不分内带
    }

    private static int Count(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    private static string Src(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }
}
