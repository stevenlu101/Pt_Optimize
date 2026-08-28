using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **旧收敛口径不许爬回判据路**（2026-08-28 算法普查 A⑪）。
///
/// 同一件事在本仓库有**三处**判「外层耦合收没收敛」：
///
/// | 处 | 参照系 | 乘不动点放大 1/(1−g)？ | 在判据路上？ |
/// |---|---|---|---|
/// | `LineRunner` 主环 | 真残差 | ✓（且 δ 与真残差**两条都要过**） | ✓ |
/// | `LineRunner` 基线环 | ~~欠松弛步~~ → 真残差 | 由 `DesignInputs.BaselineTolAmplified` 控 | ✓ |
/// | `CoupledSolver` | 相邻两轮之差 | ✗ **没有** | ✗ 已被取代 |
///
/// 第三处**故意没改**：它已被取代（<c>LineRunner</c> 那条判据路根本不调它），
/// 只服务 <c>LineSolver</c> 与几条 CLI 诊断。在那里换口径既动不了交付数，
/// 又会改掉诊断的历史值 —— 净亏。
///
/// ★ 但「不改」的前提是**它一直待在判据路之外**。
///   谁把它接回去，就等于把旧口径一起接回去了，而且**不会有任何报错** ——
///   只会得到一个「收敛了」的假答案。这正是本项目最怕的错误形态。
///   ⇒ 用一道门把这个前提钉住。
/// </summary>
public class CoupledSolverStaysOffJudgePathTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>
    /// ★ <c>LineRunner</c> 是「整线求解的唯一入口」（CLI 与 WinForms 都只调它）。
    /// 它里面出现的 <c>CoupledSolver</c> 只准是**注释**，不准是调用。
    /// </summary>
    [Fact]
    public void 判据路不许调用被取代的求解器()
    {
        string s = Core("LineRunner.cs");

        // 去掉所有 // 行注释与 /* */ 块注释之后，不该再剩下 CoupledSolver
        string noBlock = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
        string noLine = Regex.Replace(noBlock, @"//[^\r\n]*", "");
        Assert.DoesNotContain("CoupledSolver", noLine);

        // 而注释里那句「已被取代」要留着 —— 它是这道门存在的理由
        Assert.Contains("已被取代的 CoupledSolver", s);
    }

    /// <summary>
    /// 旧口径那一行必须**带着警告**。没有警告的话，下一个人读到
    /// 「<c>if (res.OuterDelta &lt; tolW)</c>」只会以为这就是正确写法，照抄到新代码里。
    /// </summary>
    [Fact]
    public void 旧口径那一行必须留字说明它是旧的()
    {
        string s = Core("CoupledSolver.cs");
        Assert.Contains("**这条收敛判据是旧口径**", s);
        Assert.Contains("没乘不动点放大", s);
        Assert.Contains("已被取代", s);
        // 要指出正确的那一份在哪，否则读的人不知道该照谁写
        Assert.Contains("LineCase.FixedPointAmp", s);
    }

    /// <summary>
    /// 放大系数**只准有一处定义**。主环与基线环各写一个 25，
    /// 就会出现「一处改了另一处没改」—— 本仓库最常见的病。
    /// </summary>
    [Fact]
    public void 不动点放大系数只有一处来源()
    {
        string s = Core("LineRunner.cs");
        Assert.Contains("public const double FixedPointAmp = 25.0;", s);

        // 除了那一处定义，别处不许再出现字面量 25.0 当放大用
        int defs = Regex.Matches(s, @"=\s*25\.0\s*;").Count;
        Assert.Equal(1, defs);
    }
}
