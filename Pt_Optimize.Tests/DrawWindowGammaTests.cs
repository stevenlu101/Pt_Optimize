using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **抽热窗口不许用常数 γ 算**（2026-08-28 算法普查 A⑦ 的实测确诊）。
///
/// γ = ③/D 是**局部量**，不是常数。同一次 `--selfcheck` 的 C 段，
/// 两个工况就差 11 %：
///
/// <code>
///   舌保温 ×1.0 → ③  4.72 K　D 1.12 W　**γ = 2.65**
///   舌保温 ×0.4 → ③ 27.39 K　D 1.76 W　**γ = 2.39**
/// </code>
///
/// 而 <see cref="PtOptimize.Core.ShapeReview"/> 原来拿写死的 <c>2.40</c> 算安全窗口：
///
/// <code>
///   double dMax = 10.0 / GammaKPerW;      // = 4.17 W
/// </code>
///
/// γ 真值 2.65 时窗口应是 <b>3.77 W</b> ⇒ **报告把余量报宽了 10 %**。
/// 这份报告是给工程师照着改设计的，报宽余量就是把人往越限的方向推。
///
/// ⇒ 改成**用本次这一解自己测出来的 γ**；常数只在测不出来时兜底，且必须标出来。
///
/// ⚠ 同时修掉的第二件事：那里的 <c>10.0</c> 是**第二份限值来源**。
///   判据自己带着 <c>Limit</c>，写死一份就会出现「印出来的 ≠ 判的」——
///   本仓库反复出过这个错（见 `SingleSourceLimitTests`）。
/// </summary>
public class DrawWindowGammaTests
{
    private static string Src =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShapeReview.cs"));

    /// <summary>窗口必须由**本次实测的 γ** 算，不是常数。</summary>
    [Fact]
    public void 窗口用本次实测的伽马()
    {
        string s = Src;
        Assert.Contains("double gUse = gMeasured ? dipAct / dWorstForG : GammaKPerW;", s);
        Assert.Contains("**本次实测**", s);

        // 不许再出现「常数除常数」那个老写法
        Assert.DoesNotContain("dMax = 10.0 / GammaKPerW", s);
    }

    /// <summary>限值只从判据读 —— 报告里不许再写死一份 10.0。</summary>
    [Fact]
    public void 限值只从判据读()
    {
        string s = Src;
        Assert.Contains("double dipLim = dipC?.Limit ?? double.NaN;", s);
        Assert.Contains("LineResult.Key.FlangeDip", s);
    }

    /// <summary>
    /// ★ **测不出来时要照实说**，不许拿常数冒充实测。
    /// D 接近 0 时 ③/D 没有意义 —— 那时说「退回历史基准，这个窗口只是估计」。
    /// </summary>
    [Fact]
    public void 测不出来时不许拿常数冒充实测()
    {
        string s = Src;
        Assert.Contains("bool gMeasured = dWorstForG > 0.05", s);
        Assert.Contains("γ 测不出来", s);
        Assert.Contains("**下面这个窗口只是估计**", s);
    }

    /// <summary>
    /// γ 与历史基准差得多时要**当场说出来**，并点明「γ 是局部量，不是常数」——
    /// 否则下一个人还会把它当常数用。
    /// </summary>
    [Fact]
    public void 与历史基准差得多时要报出来()
    {
        string s = Src;
        Assert.Contains("**γ 是局部量，不是常数**", s);
        Assert.Contains("0.1 * GammaKPerW", s);      // 10 % 门槛
    }

    /// <summary>
    /// 常数本身留着（兜底 + 历史对照），但**不许再有第二处**拿它直接算窗口。
    /// </summary>
    [Fact]
    public void 常数只剩兜底与对照两个用途()
    {
        string s = Src;
        Assert.Contains("public const double GammaKPerW = 2.40;", s);

        // ★ 数次数不是好判据（一行可以出现两次）。直接钉住**它没被拿去算窗口**：
        //   窗口只准由 gUse（本次实测，测不出来才兜底成常数）与判据自己的限值算出来。
        Assert.Contains("double dMax = (double.IsNaN(dipLim) ? 10.0 : dipLim) / gUse;", s);

        // 常数只准出现在：声明、兜底赋值、三处对照文案。留一个宽松上限当气味计。
        Assert.True(Regex.Matches(s, "GammaKPerW").Count <= 10,
            "GammaKPerW 出现次数变多了 —— 检查是不是又有人拿常数 γ 去算什么");
    }
}
