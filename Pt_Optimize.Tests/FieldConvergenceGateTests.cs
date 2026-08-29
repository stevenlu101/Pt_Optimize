using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **场没解到位 ⇒ 吃它的判据一律「判不了」**（2026-08-29）。
///
/// ══ 铁律第 ③ 条此前在线性解这一层**根本没有执行**
///
/// 「判据只能过 / 不过 / **判不了**，判不了不算过」—— 而：
/// <code>
///   ShellCurrent.Converged   字段有，**没有任何人读**
///   ShellThermal.Converged   有人读，但**只记一条 Note，不拦结果**
/// </code>
/// ⇒ 场没解到位时判据照样报出一个数，而那个数**看起来完全正常**。
/// 正是本项目最怕的错误形态。
///
/// ★ 这条是用户 2026-08-29 追问「此前不收敛时静默返回半成品电位场，有可能吗」
///   时挖出来的。那一次的 4 K 漂移**不是**它（配对证明是网格），
///   但**漏洞是真的**，而且比那次漂移更要紧：漂移会被回归基准抓到，
///   而「场没解到位却报了个数」**不会被任何东西抓到**。
///
/// ══ 为什么做成后置一遍
///
/// 散落在每条判据里就会「补一条漏一条」—— 本仓库为「判据缺席」栽过**三次**，
/// 最后也是靠一份**名单**解决的（<see cref="LineResult.Required"/>）。
/// 这里同理：一份「吃法兰场的判据」名单，加判据时同步加。
///
/// ⚠ 只标**吃法兰场**的。纯几何（⑤⑥）与纯管子的（管 J、④）不标 ——
///   一并标成判不了是**过度**，会掩盖真正该看的东西。
/// </summary>
public class FieldConvergenceGateTests
{
    private static string Src(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>★ 电位场的收敛状态必须被**读到**（此前定义了却没人用）。</summary>
    [Fact]
    public void 电位场的收敛被读到了()
    {
        string s = Src("LineRunner.cs");
        Assert.Contains("bool curConverged = sc.Converged;", s);
        Assert.Contains("curConverged = sc2.Converged;", s);   // σ(T) 内循环重解后要更新
    }

    /// <summary>没收敛要落到**片**上，不是只飘一条 Note。</summary>
    [Fact]
    public void 没收敛落到片上()
    {
        Assert.Contains("public bool FieldsConverged = true;", Src("LineRunner.cs"));
        Assert.Contains("flanges[j].FieldsConverged = false;", Src("LineRunner.cs"));
        Assert.Contains("flanges[j].FieldNote = ", Src("LineRunner.cs"));
    }

    /// <summary>★★ 后置一遍：把吃法兰场的判据标成「判不了」。</summary>
    [Fact]
    public void 后置一遍把相关判据标成判不了()
    {
        string s = Src("LineRunner.cs");
        Assert.Contains("MarkUndeterminedIfFieldsFailed(res, flanges);", s);
        Assert.Contains("ck.Undetermined = true;", s);
        Assert.Contains("ck.Ok = false;", s);
        Assert.Contains("**判不了不算过。**", s);
    }

    /// <summary>
    /// 名单要覆盖所有吃法兰场的判据。**加判据时要同步加** ——
    /// 不加的后果是它在场没解到位时照样报数。
    /// </summary>
    [Fact]
    public void 名单覆盖吃法兰场的判据()
    {
        string s = Src("LineRunner.cs");
        foreach (var k in new[]
        {
            "LineResult.Key.NetFlux", "LineResult.Key.DiscTemp", "LineResult.Key.FlangeDip",
            "LineResult.Key.Ramp", "LineResult.Key.FlangeStab", "LineResult.Key.LocalStab",
            "LineResult.Key.RampField", "LineResult.Key.HeatBalance",
        })
            Assert.Contains(k, s);
    }

    /// <summary>
    /// ★ 纯几何（⑤⑥）与纯管子的（管 J、④）**不在名单里** ——
    /// 一并标成判不了是过度，会掩盖真正该看的东西。
    /// </summary>
    [Fact]
    public void 不该标的没被标进去()
    {
        string s = Src("LineRunner.cs");
        int a = s.IndexOf("string[] dependsOnFlangeFields", System.StringComparison.Ordinal);
        int b = s.IndexOf("};", a, System.StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        string list = s[a..b];

        Assert.DoesNotContain("FreeTab", list);      // ⑤ 舌片自由段：几何闭式
        Assert.DoesNotContain("DiscCover", list);    // ⑥ 圆盘盖得住：几何闭式
        Assert.DoesNotContain("TubeJ", list);        // 管 J：管子的量
        Assert.DoesNotContain("TubeStrength", list); // ④ 管强度：管子的量
    }

    /// <summary>
    /// <see cref="LineResult.AllOk"/> 必须把「判不了」当阻断 ——
    /// 否则上面这一切都只是换个说法，结果照样放行。
    /// </summary>
    [Fact]
    public void 判不了确实会阻断全过()
    {
        string s = Src("LineRunner.cs");
        Assert.Contains(".All(c => c.Ok && !c.Undetermined);", s);
    }

    /// <summary>
    /// ⚠ 线性解自己也要**报**收敛与否 —— 没有这个字段，上面一切无从谈起。
    /// </summary>
    [Fact]
    public void 线性解报得出收敛与否()
    {
        Assert.Contains("public bool Converged;", Src("ShellCurrent.cs"));
        Assert.Contains("res.Converged = nf == 0 || resid <= tol * bNorm;", Src("ShellCurrent.cs"));
        // 判的是**真残差**相对右端项，不是步长
        Assert.Contains("resid <= tol * bNorm", Src("ShellCurrent.cs"));
    }
}
