using System.IO;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路 第五轮：**端到端必须经过求解器**。2026-09-17，Opus 5。
//
//  病灶（上一轮，Opus 4.6）：端到端把内置设计 W08／W06 **原样**过三关，
//  截面 J 27.71/11 全不过 —— 那是**未求解的输入**，不是设计结果，
//  而报告的标题写着「端到端三步结论」。真正的链是
//    内置输入 → Solver 求解（约束盒下角二分求根）→ 求得的设计过三关 → 铂重。
//
//  本门钉的是接线本身（慢测里那两条 Assert.Same／Solves>0 要跑 40 分钟才看得到，
//  接线错了应该在快门上当场红）。
// ════════════════════════════════════════════════════════════════════════════

public class R48LSolverInLineGateTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), Path.Combine(parts)));

    private static string E2E() => Src("Pt_Optimize.Tests", "R48LEndToEndTests.cs");

    [Fact]
    public void a_端到端必须先求解再拿求得的设计过三关()
    {
        string s = E2E();
        Assert.Contains("Solver.Solve(", s);

        // 三关吃的都必须是 solved（= sr.Design），不许是 seed（内置输入）
        Assert.Contains("RampSweep.Run(solved", s);
        Assert.Contains("solved.BuildCase(p)", s);
        Assert.Contains("solved.BuildCase(p, emptyTube: true)", s);
        Assert.DoesNotContain("RampSweep.Run(seed", s);
        Assert.DoesNotContain("seed.BuildCase(", s);

        // 慢测里那两条最低限度断言也得在（它们是运行期的同一件事）
        Assert.Contains("Assert.Same(sr.Design, solved)", s);
        Assert.Contains("Assert.True(sr.Solves > 0", s);
    }

    [Fact]
    public void b_求解器选项必须与界面自动定厚那一份一致()
    {
        // 界面（生产）那一份：MaxRounds = 40、解法族由下拉定（预设第 0 项「不挖舌孔」）、第二遍细网格取界面预设
        string ui = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        Assert.Matches(new Regex(@"SolverOptions\s+OptsFor\(bool\s+cuts\)\s*=>\s*new\s+SolverOptions\s*\{\s*MaxRounds\s*=\s*40\b"), ui);
        Assert.Contains("_family.SelectedIndex = 0;", ui);              // 下拉预设 = 不挖舌孔
        Assert.Contains("AllowTabCuts = cuts", ui);                     // 族由下拉定
        Assert.Contains("OptsFor(fam == 1)", ui);                       // fam 0 ⇒ cuts = false

        // 端到端那一份必须照抄这三条（数写死在门里，改一边就红）
        string s = E2E();
        Assert.Matches(new Regex(@"ProductionOptions\(\)\s*=>\s*new\s+SolverOptions\s*\{\s*MaxRounds\s*=\s*40\s*,\s*AllowTabCuts\s*=\s*false\s*\}"), s);
        // FineMm 不许在端到端里被另设（界面预设是 0 ⇒ 没做第二遍，这件事必须原样呈现，不许偷偷开）
        Assert.DoesNotContain("FineMm =", s);
        Assert.Contains("没做第二遍", s);
        Assert.Contains("不可交付", s);
    }

    [Fact]
    public void c_求解器抛异常或没有解时不许跑三关()
    {
        string s = E2E();
        Assert.Contains("不许拿未求解的输入冒充设计结果", s);
        Assert.Contains("Assert.Fail(", s);
        // 不可行也要如实印出来，不许只印铂重
        Assert.Contains("**不可行**（求解器没把全部判据补上）", s);
        Assert.Contains("停因", s);
    }

    [Fact]
    public void d_求解器缺省选项没有任何起点参数()
    {
        // 「解与初值无关」这条的形式保证：SolverOptions 上只有盒的上下界与网格／容差，没有任何叫「起点／种子」的字段。
        var t = typeof(SolverOptions);
        foreach (var f in t.GetFields())
        {
            Assert.DoesNotContain("Seed", f.Name);
            Assert.DoesNotContain("Start", f.Name);
            Assert.DoesNotContain("Initial", f.Name);
        }
        // 求解器开头把传进来的旋钮丢掉这件事，源码里钉住
        string so = Src("Pt_Optimize", "Core", "Solver.cs");
        Assert.Contains("五个旋钮一律丢掉", so);
    }
}
