using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-13，Opus 5 写）：
/// **求根用的那张网格，和判决用的那张网格，必须逐个参数相同。**
///
/// ══ 病历（09-13 正式重解，两个内置设计都咬上）
///
/// 第二遍求根跑在「细网格 0.500 mm」上，停因写着「第 1 轮全过」；
/// 紧接着的加密复核在**同样标称 0.500 mm** 的那一档上报 管孔净流入 −1.792 W（不过）。
/// 同一个设计、同一个尺寸数字、两个相反的结论。
///
/// 原因是两边造网格的配方不同：求根只设中带尺寸，粗区留 11 mm、内带不分（细粗比 22 倍）；
/// 复核整张一起缩（细粗比 5.5 倍）。两区之间那段 growth 过渡正压在舌片（电流主通路）上，
/// 结构一变焦耳热差 6～7 %，而抽热判据只有几瓦宽。
///
/// ══ 实测（本门的前身，慢探针，deliverable/R48_求根网格与复核网格_2026-09-13.txt）
///
/// 同一设计（管壁 0.8 的 09-13 重解点）、同一标称 0.500 mm，只换配方：
/// <code>
///   配方              细粗比   单元数   管孔净流入   法兰增量温降   用时
///   求根（粗区 11）    22 倍   13486   +1.789 W（过）   6.618 K    298 s
///   复核（整张缩）    5.5 倍   15646   −1.760 W（不过） 0.586 K    624 s
/// </code>
/// 连正负号都是反的。
///
/// ══ 这道门为什么改成现在这个写法
///
/// 探针那一版**自己手抄了两份配方**去对比 —— 那等于把「各自手抄一份」这个病灶
/// 又在测试里重演一遍：它永远红（手抄的旧配方按定义就与新配方不同），
/// 而且改了生产代码它也不知道。现在两边都取**生产代码真正在用的那个东西**：
///   · 求根那一路 = `Solver.EvalRaw` 里那一行（由 SolverFineRootTests 逐字钉住）；
///   · 复核那一路 = `MeshVerify.AnalyticCaseFactory`（生产代码自己调的那个工厂）。
/// 谁改动其中一边而没改另一边，这里当场红。
///
/// 场解那一步删掉了：两边网格参数逐位相同时，解出来的数按构造就相同 ——
/// 再跑两次 15 分钟的全耦合场解只是烧机器。数字留在上面的注释和 deliverable 里。
/// </summary>
public class R48SolveMeshMatchTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void 求根的网格与复核的网格逐个参数相同(double h)
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        var p = new DesignInputs();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));

        // ── 求根那一路：Solver.EvalRaw 就是这两行（checkRamp 在导航/求根阶段为 false）
        var solve = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(solve, h, radius);

        // ── 复核那一路：直接用生产代码的工厂，不手抄
        var verify = MeshVerify.AnalyticCaseFactory(d, p, radius, innerR)(h, h);

        Assert.Equal(solve.MeshFineMm, verify.MeshFineMm, 9);
        Assert.Equal(solve.MeshCoarseMm, verify.MeshCoarseMm, 9);
        Assert.Equal(solve.MeshFineRadiusMm, verify.MeshFineRadiusMm, 9);
        Assert.Equal(solve.MeshInnerMm, verify.MeshInnerMm, 9);
        // 内带半径两边不同是**允许**的，且必须是无害的：内带 = 中带时它建不出任何新节点。
        // 「无害」由 R48OneRefineRecipeTests 的逐节点比对钉死，不在这里假设。
        Assert.Equal(h, solve.MeshInnerMm, 9);
        Assert.Equal(h, verify.MeshInnerMm, 9);
    }

    /// <summary>
    /// 细粗比必须与算例出厂时相同 —— 这就是「自相似」，也是三档之间可比的前提。
    /// 不是钉死 5.5：出厂默认哪天改了，跟着改就是，但**加密不许改变这个比值**。
    /// </summary>
    [Fact]
    public void 三档加密的细粗比必须完全一致()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();
        double born = d.BuildCase(p, checkRamp: false) is { } b0 ? b0.MeshCoarseMm / b0.MeshFineMm : double.NaN;
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
        var factory = MeshVerify.AnalyticCaseFactory(d, p, radius, innerR);

        foreach (double h in new[] { 1.0, 0.5, 0.25 })
        {
            var lc = factory(h, h);
            Assert.Equal(born, lc.MeshCoarseMm / lc.MeshFineMm, 9);
        }
    }

    /// <summary>
    /// 源码门：复核工厂只准有一处，且生产代码自己就在调它 ——
    /// 否则「门用的工厂」和「跑的时候用的工厂」又成了两个东西。
    /// </summary>
    [Fact]
    public void 复核工厂是生产代码自己在调的那一个()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "Pt_Optimize", "Core", "MeshVerify.cs"));
        Assert.Contains("public static Func<double, double, LineCase> AnalyticCaseFactory(", src);
        Assert.Contains("return Run(AnalyticCaseFactory(d, baseIn, radius, innerR),", src);
    }
}
