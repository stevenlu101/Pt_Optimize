using System.IO;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **灵敏度随形状变号，不许当成常数印给工程师**（2026-08-28 实测确诊）。
///
/// 界面提示与 <see cref="PtOptimize.Core.NextAction.DiscHot"/>（②″ 超限时印给人的操作指令）
/// 各写着两个数：
///
/// <code>
///   d②″/d倍率 ≈ **−1.4** K/单位      ← 加环压得住 ②″
///   ∂②″/∂板厚 ≈ **−1.6** K/mm        ← 加厚也压得住
/// </code>
///
/// 而 `--monotone` 在**现役 0.8 档**实测出来的是**相反方向**：
///
/// <code>
///   倍率 1.0 → ②″ = −0.208　　2.5 → −0.124   ⇒ **+0.056** K/单位（且多花 137 g 铂）
///   板厚 0.6 → ②″ = −0.793　　4.0 → −0.066   ⇒ **+0.214** K/mm
/// </code>
///
/// ⚠ **但这不等于「那两个数是错的」**：本次扫描是在 ②″ ≈ −0.2（离限值 5 很远）处做的；
///   −1.4 多半是在**窄舌**形状、②″ 真的超限时测的 —— 那时孔周确有电流拥塞，加环可能真有效。
///
/// ⇒ 病不在数值，在**把局部线性化当成普适常数** —— 与 γ 那条（A⑦）同一个病。
///   现役宽舌形状把孔周拥塞消掉了，同一个旋钮于是变成纯花铂。
///
/// ★ 处理方式：**两个数都留着、都标出测量工况、并要求使用者对手上的形状实测**。
///   删掉建议是错的（②″ 真超限时它可能仍然对）；把单个数当普适也是错的。
/// </summary>
public class SensitivitySignTests
{
    private static string Read(string dir, string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", dir, f));

    /// <summary>②″ 超限时印给人的指令，必须**先**警告方向会变号。</summary>
    [Fact]
    public void 操作指令先警告灵敏度会变号()
    {
        string s = Read("Core", "NextAction.cs");
        int warn = s.IndexOf("随形状变号", System.StringComparison.Ordinal);
        int act = s.IndexOf("① 加大管孔渐变环的倍率", System.StringComparison.Ordinal);

        Assert.True(warn > 0, "DiscHot 里没有「随形状变号」的警告");
        Assert.True(act > 0, "DiscHot 里找不到第 ① 条操作");
        Assert.True(warn < act, "★ 警告必须在操作之前 —— 人是照着顺序做的");
    }

    /// <summary>两个方向的实测都要在，并且标明各自的工况。</summary>
    [Fact]
    public void 两个工况的实测都留着并标明出处()
    {
        string s = Read("Core", "NextAction.cs");
        Assert.Contains("**窄舌**", s);          // −1.4 是在哪测的
        Assert.Contains("宽舌", s);              // +0.056 是在哪测的
        Assert.Contains("+0.056", s);
        Assert.Contains("+0.214", s);
        Assert.Contains("137 g", s);             // 代价也要说
    }

    /// <summary>
    /// ★ 必须要求使用者**自己实测**，并点名工具。
    /// 只说「这个数不一定准」而不给测法，等于把人卡在原地。
    /// </summary>
    [Fact]
    public void 要求对手上的形状实测并点名工具()
    {
        foreach (var (dir, f) in new[] { ("Core", "NextAction.cs"), ("UI", "LineDesignPage.cs") })
        {
            string s = Read(dir, f);
            Assert.Contains("--monotone", s);
        }
        Assert.Contains("别照抄任何一个数", Read("Core", "NextAction.cs"));
        Assert.Contains("别照抄任何一个数", Read("UI", "LineDesignPage.cs"));
    }

    /// <summary>
    /// **建议本身不许删**。②″ 真的超限时（窄舌、孔周拥塞），加环可能仍然对 ——
    /// 今天的扫描没有覆盖那个工况，没资格否定它。
    /// </summary>
    [Fact]
    public void 原建议保留而不是删掉()
    {
        string s = Read("Core", "NextAction.cs");
        Assert.Contains("加大管孔渐变环的倍率", s);
        Assert.Contains("加厚该片板", s);
        Assert.Contains("两条路子仍然值得试", s);
    }
}
