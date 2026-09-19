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
            "LineResult.Key.HotOverTc", "LineResult.Key.ColdUnderTc",   // R48 B（2026-09-14 Opus 5）：加严 —— 新两条硬判据同样吃法兰场
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

    /// <summary>
    /// ★★ **温度场那一半：Picard 的停机判据原本也是「步长」**（2026-08-29 补）。
    ///
    /// <code>
    ///   if (maxd &lt; tol) break;          maxd = 一轮里最大的温度改动 K
    ///   res.Converged = maxd &lt; tol;
    /// </code>
    ///
    /// 与线性解那一半、与基线循环那一次，是**同一族**：步长小 ≠ 解对。
    /// 收缩因子 g 时，到不动点的距离 ≈ 步长 / (1 − g)。
    /// **同一个病在本仓库出现三次**，前两次都是撞出来的。
    ///
    /// ══ 实测（`--cli --shell`，2727 单元，2026-08-29）
    ///
    /// <code>
    ///   步长判据停在   9.99E-005 K（tol 1e-4）
    ///   此时真残差     1.18E-004 W　相对 1.58E-007      ← 这次**碰巧是保守的**
    /// </code>
    ///
    /// ⚠ 「碰巧对」不是判据。改法：收敛 = 步长小 **且** 真残差小。
    ///   合取只会更严 ⇒ 现役算例逐位不变（同一条命令重跑，5805 轮、两个数都没动），
    ///   而未来收缩变慢的算例不会再悄悄放行。
    /// </summary>
    [Fact]
    public void 温度场也报得出真残差()
    {
        string s = Src("ShellThermal.cs");
        Assert.Contains("public double ResidualW;", s);
        Assert.Contains("public double ResidualRel;", s);
        // 步长不许再自己定收敛
        Assert.DoesNotContain("res.Converged = maxd < tol;", s);
        Assert.Contains("bool stepOk = maxd < tol;", s);
    }

    /// <summary>
    /// ★★ 残差必须用**真**散热 q_s(T)，不是迭代里为稳定做的线性化 ——
    /// 线性化只准出现在「怎么走」里，不准出现在「走到没有」里。
    /// 用线性化算残差，等于拿近似去验近似，残差会**系统性偏小**。
    /// </summary>
    [Fact]
    public void 残差用真散热不用线性化()
    {
        string s = Src("ShellThermal.cs");
        // 2026-08-30：残差抽成了 ResidRel()，**全程序只此一份**（此前收尾处还内联着第二份）
        int a = s.IndexOf("(double W, double Rel) ResidRel()", System.StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 ResidRel()");
        int b = s.IndexOf("return (rMax,", a, System.StringComparison.Ordinal);
        Assert.True(b > a);
        string blk = s[a..b];
        Assert.Contains("double qs = lossFor[i].Eval(ti);", blk);
        Assert.DoesNotContain("Slope", blk);
        // 收尾复用同一份，不许再内联
        Assert.Contains("(res.ResidualW, res.ResidualRel) = ResidRel();", s);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(s, @"bSum \+= Math\.Abs").Count);
    }

    /// <summary>
    /// ★★★ **收敛 = 真残差达标；步长不参与判定**（2026-08-30 起）。
    ///
    /// 前一版是「步长 **且** 残差」的合取。那一版比只判步长严，但仍然把**路径的性质**
    /// 写进了**解的判定**。配对实测（`--thermcg`）证明它不够：
    /// <code>
    ///   步长容差 1e-4 K ⇒ GS 与 CG 停在同一个不动点附近，但相差 **0.132 K**
    ///   步长容差 1e-6 K ⇒ 相差 0.00101 K          （线性下降 130 倍）
    ///   ⇒ 不动点放大约 **1300 倍**，步长根本钉不住位置
    /// </code>
    /// ⇒ 判据换成方程本身：真残差。步长仍算出来放进 <c>res.Residual</c> 供诊断。
    ///
    /// ⚠ 残差算不出来（NaN）**不算过** —— 判不了不算过。
    /// </summary>
    [Fact]
    public void 收敛只判真残差不判步长()
    {
        string s = Src("ShellThermal.cs");
        Assert.Contains("res.Converged = !double.IsNaN(res.ResidualRel) && res.ResidualRel <= ResidualRelTol;", s);
        Assert.DoesNotContain("res.Converged = stepOk", s);
        // 循环也必须**停在残差上**，不是停在步长上
        Assert.Contains("if (ResidRel().Rel <= ResidualRelTol) { it++; break; }", s);
        Assert.Contains("if (it % 20 == 19 && ResidRel().Rel <= ResidualRelTol) { it++; break; }", s);
    }

    /// <summary>
    /// ★ 阈值必须**声明出处**，不许是拍的数。
    /// 本项目既定要求：限值要写得出它是从哪来的（HANDOVER「限值的出处」那张表）。
    /// </summary>
    /// <summary>
    /// ★ 阈值必须**由实测标定**，不是拍的数：
    /// <c>场差 ≈ 5×10⁵ × 相对残差</c>（`--thermcg --ttol` 扫描得到）⇒
    /// 要把场钉到 0.01 K（= ③ 那条 1.0 K 容差的 1%）⇒ 残差 ≤ 2e-8。
    /// </summary>
    [Fact]
    public void 残差阈值有出处()
    {
        string s = Src("ShellThermal.cs");
        Assert.Contains("public const double ResidualRelTol = 2e-8;", s);
        Assert.Contains("0.132 K", s);            // 步长 1e-4 时两条路的差
        Assert.Contains("0.00101 K", s);          // 步长 1e-6 时
        Assert.Contains("1300 倍", s);            // 不动点放大
        Assert.Contains("--thermcg", s);          // 复现命令
        Assert.Equal(2e-8, ShellThermal.ResidualRelTol);

        // ⚠ 并且要说清它有多要紧：解算器噪声占 ③ 那条容差的多大比例
        Assert.Contains("13%", s);
    }

    /// <summary>
    /// ★★★ **换解法必须配对对账**（2026-08-30）。
    ///
    /// 这条不是形式：CG 版第一次写出来时 <c>UpdateG()</c> 没先跑，<c>gcond</c> 全是 0，
    /// **一个自由单元都没挑出来、一格都没解** —— 而它照样跑完、照样报一整套判据值、
    /// 还报「提速 2.4×」。配对对照当场量出温度场差 **697 K**。
    ///
    /// 第二次翻车：内层 CG 的收敛判据用 <c>max|rhs|</c> 归一，而 rhs 含定温边界项
    /// （g·T_根，量级上万 W）⇒ 阈值比真残差还大，**CG 一轮都不跑**，外层空转 300 轮。
    /// 两次都是「看起来正常的错数」，两次都是配对对照抓的。
    /// </summary>
    [Fact]
    public void 换解法有配对对照且有跑空的保险()
    {
        string s = Src("ShellThermal.cs");
        Assert.Contains("public static bool UseGaussSeidel;", s);
        // 跑空保险：有可动单元却挑不出自由单元 ⇒ 当场炸
        Assert.Contains("却一个自由单元都没挑出来", s);
        Assert.Contains("throw new InvalidOperationException(", s);
        // 内层归一化不许再用 max|rhs|
        Assert.Contains("归一化**不能**用 max|rhs|", s);
        Assert.Contains("0.1 * ResidualRelTol * Math.Max(jouleTotalW, 1e-12)", s);

        string prog = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("--thermcg", prog);
        Assert.Contains("逐单元温度场最大差", prog);
    }

    /// <summary>
    /// ★ 不许再把**步长**叫成「残差」—— 读的人会据此判断解到位没有，
    /// 而那两个数可以差好几个量级。
    /// </summary>
    [Fact]
    public void 不许再把步长叫残差()
    {
        string s = Src("LineRunner.cs");
        Assert.DoesNotContain("温度场未收敛（残差 {th.Residual:E2}", s);
        Assert.Contains("温度场未收敛（步长 {th.Residual:E2} K，真残差 {th.ResidualW:E2} W", s);
    }
}
