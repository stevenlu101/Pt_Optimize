using System.IO;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **二分求根的「根」是网格相关的**（2026-08-28 算法普查 A⑬）。
///
/// 这是 <see cref="Solver"/> **自己的缺陷**，由实测暴出，不是推测：
///
/// <code>
///   同一份 0.8 档设计，同一份代码，只改网格：
///     ③  导航 2 mm      = 4.720 K
///     ③  细网格 0.408   = 9.572 K      ← **翻 2.03 倍**
///     ②′ 导航 2 mm      = 1.123 W
///     ②′ 细网格 0.408   = 3.103 W      ← 2.76 倍（安全侧）
/// </code>
///
/// ⇒ 求解器给的「刚好不违反」是**粗网格上的刚好**，到细网格可能已越限。
///
/// ★ 两级机制对「**搜索**」成立、对「**求根**」不成立 —— 这是本条的要点：
///   搜索只要方向对（粗网格够了）；求根要的是**根的位置**，而位置随网格移动。
///
/// 修法：**同一个循环跑两遍，只有网格不同**。第二遍从第一遍的解出发、
/// 照样只增不减 ⇒「收敛到最小可行点」与「与初值无关」两条都还成立
/// （第一遍本身与初值无关，第二遍是它的确定性函数）。
/// </summary>
public class SolverFineRootTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>
    /// 网格必须从选项取 —— 求根跑在哪张网格上不许由别处悄悄决定。
    ///
    /// ★ R48 改钉法（2026-09-13，Opus 5）：本门原来钉 <c>lc.MeshFineMm = o.FineMm;</c> 这一行，
    ///   而那行**只设了尺寸这一维** —— 粗区留在 11 mm、内带不分。于是求根用的网格与加密复核用的网格
    ///   标称同为 0.500 mm，结构却不是同一张（细粗比 22 倍 vs 5.5 倍），
    ///   09-13 两个内置设计都因此得出相反结论（求根「全过」、复核 管孔净流入 −1.79 W）。
    ///   现在钉的是「走共用配方 <see cref="MeshAdapt.RefineWholeMesh"/>」，三维一起接过去。
    /// </summary>
    [Fact]
    public void 求根的网格由选项决定()
    {
        string s = Core("Solver.cs");
        Assert.Contains("public double FineMm;", s);
        Assert.Contains("MeshAdapt.RefineWholeMesh(lc, o.FineMm, o.FineRadiusMm)", s);
        Assert.Contains("if (o.FineMm > 0)", s);
    }

    /// <summary>
    /// ★ **两遍必须是同一个循环**。各写一份的话，两遍的判据口径、最小性论证、
    /// 只增不减的不变式就会各走各的 —— 而那三样正是「解成立」的全部依据。
    /// </summary>
    [Fact]
    public void 两遍跑的是同一个循环()
    {
        string s = Core("Solver.cs");
        Assert.Contains("bool Rounds(SolverOptions o, string tag)", s);

        // 恰好两次调用：导航一次、细网格一次
        Assert.Equal(2, Regex.Matches(s, @"Rounds\(\w+, ").Count);
        Assert.Contains("Rounds(navOpt,", s);
        Assert.Contains("Rounds(opt,", s);
    }

    /// <summary>
    /// 第二遍**从第一遍的解出发**（不重置旋钮）——「只增不减」跨遍也要成立，
    /// 否则最小性与初值无关性都塌。
    /// </summary>
    [Fact]
    public void 第二遍不重置旋钮()
    {
        string s = Core("Solver.cs");
        int p1 = s.IndexOf("Rounds(navOpt,", System.StringComparison.Ordinal);
        int p2 = s.IndexOf("Rounds(opt,", System.StringComparison.Ordinal);
        Assert.True(p1 > 0 && p2 > p1);

        // 两遍之间不许再把旋钮压回下角
        string between = s[p1..p2];
        Assert.DoesNotContain("d.TabThickMm[j] = tLo;", between);
        Assert.DoesNotContain("= opt.InsLoMm;", between);
        Assert.DoesNotContain("= opt.RingLo;", between);
    }

    /// <summary>
    /// ★★ **没做第二遍必须喊出来**。这个项目的错误形态是「看着正常的错数」——
    /// 一个只在导航网格上成立的解，数字长得和可交付的解一模一样。
    /// </summary>
    [Fact]
    public void 没做第二遍必须标成不可交付()
    {
        string s = Core("Solver.cs");
        Assert.Contains("public bool FineRefined;", s);
        Assert.Contains("**没做第二遍**", s);
        Assert.Contains("不可交付", s);

        // 命令行也要照实说，不许吞
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("rr.FineRefined", prog);
        Assert.Contains("**没做第二遍** ⇒ 这个解只在导航网格上成立，**不可交付**（A⑬）", prog);

        // ★ 纪律：**先验、不过才付**。复核便宜、二次求根昂贵；
        //   实测 0.8 档的解在网格无关分辨率上 ③ = 7.333/10 全过 ⇒ 这一步常常不必付。
        Assert.Contains("先验、不过才付", prog);
        Assert.Contains("先验，不过才付", Core("Solver.cs"));
    }

    /// <summary>
    /// ★ **求根的网格与判决的网格必须是同一张**。各算各的话，
    /// 求出来的根照样不作数 —— 那正是 A⑬ 要修的病，不能在修它的过程里重犯。
    /// </summary>
    [Fact]
    public void 网格该多细只有一处来源()
    {
        string mv = Core("MeshVerify.cs");
        Assert.Contains("RequiredMeshFor(DesignSpec d", mv);

        // MeshVerify 自己也得走这个方法，不许留一份旧的内联算法
        Assert.Contains("var (h0, radius) = RequiredMeshFor(d, weldAsGeometricFeature);", mv);
        Assert.Single(Regex.Matches(mv, @"MeshAdapt\.RequiredFineMm\("));

        // 命令行取网格也走同一份
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("MeshVerify.RequiredMeshFor(geoS)", prog);
    }

    /// <summary>
    /// ★★ **终局复核也必须跑在那张网格上** —— 2026-08-29 抓到的漏洞。
    ///
    /// <c>Finish</c> 原先是 <c>d.BuildCase(baseIn, checkRamp: true)</c> 之后**什么都不设**，
    /// 于是悄悄退回 <see cref="LineCase.MeshFineMm"/> 的默认 <b>2.0 mm</b>，
    /// 并**用它覆盖 res.Best**：
    /// <code>
    ///   第二遍求根 → 0.146 mm 上把旋钮抬到全过
    ///   Finish    → 2.0 mm 重算 ⇒ res.Feasible 与所有印出来的判据值都成了粗网格的数
    /// </code>
    /// 上面那段类注释里 ③ **差 2.03 倍**的实测，说的就是这两张网格。
    /// ⇒ 会出现「第二遍说全过、终局说不可行」的自相矛盾，而**两边都不报错**。
    ///
    /// ⚠ 这条与「求根的网格由选项决定」是**同一条不变量的两半**。只守前一半的门，
    ///   挡不住后一半 —— 本仓库为「门只挡得住自己当初那一行」栽过不止一次。
    /// </summary>
    [Fact]
    public void 终局复核跑在最后一遍求根的网格上()
    {
        string s = Core("Solver.cs");

        // Finish 必须**收得到**最后一遍用的选项 —— 收不到就无从谈起
        Assert.Contains("DesignInputs baseIn, SolverOptions lastOpt,", s);
        Assert.Contains("Finish(res, d, last, baseIn, lastOpt, cancel, progress);", s);

        // 而且真的把网格设上去了 —— R48 起走共用配方（尺寸／粗区／内带三维一起），
        // 不再只设尺寸那一维（只设尺寸正是 09-13「求根与复核结论相反」的来源）
        Assert.Contains("MeshAdapt.RefineWholeMesh(lcF, lastOpt.FineMm, lastOpt.FineRadiusMm)", s);

        // ★ lastOpt 要随第二遍**改过去**；不改就永远是导航网格，等于没修
        Assert.Contains("lastOpt = opt;", s);

        // ★★ 不许再有「建完 case 直接丢给 LineRunner、一个网格字段都不设」的写法
        Assert.DoesNotContain(
            "LineRunner.Run(d.BuildCase(baseIn, checkRamp: true), null, cancel)", s);
    }
}
