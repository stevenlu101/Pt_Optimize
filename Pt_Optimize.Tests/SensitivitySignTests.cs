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
///
/// ══ 2026-09-14 Opus 5（R48 B 复审）：钉 NextAction.DiscHot 的三条退役
///
/// DiscHot 是「圆盘区最高温 − 管温」超限时附在判据说明上的操作指令。R48 B 把那条降为参考量（旧判法，不卡交付），
/// 参考行不再附操作指令 ⇒ DiscHot（连同 DipHigh）**再也没有任何地方挂它**，界面上看不到；它还带代号与命令行开关名，
/// 本来也不许上界面。于是原来钉它的三条（「先警告再给操作」「两个工况的实测都在」「建议本身不许删」）守的是一段**死字串**，
/// 等于门在空转 —— 常量删掉，这三条退役：
///   · 操作指令先警告灵敏度会变号 —— 删（没有这段指令了）；
///   · 两个工况的实测都留着并标明出处 —— **改钉界面上真看得见的那一处**：LineDesignPage 的「环倍率」提示（窄舌 −1.4／宽舌 +0.056、137 g）；
///     「∂圆盘区最高温/∂板厚 = +0.214」只在 DiscHot 里有，随它一起离开 APP，数与出处留在本档上面的说明里；
///   · 原建议保留而不是删掉 —— 删：它的理由是「②″ 真超限时加环可能仍然对，没资格否定」，而 ②″ 已不卡交付，
///     加环现在是热侧「最热铂高出热偶读数」的候选，抬不抬由求解器对手上的形状当场实测（Solver.ChooseKnob），不靠这段文字。
/// </summary>
public class SensitivitySignTests
{
    private static string Read(string dir, string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", dir, f));

    /// <summary>两个方向的实测都要在，并且标明各自的工况 —— 钉在界面真显示的「环倍率」提示上（2026-09-14 Opus 5 由 NextAction 挪来）。</summary>
    [Fact]
    public void 两个工况的实测都留着并标明出处()
    {
        string s = Read("UI", "LineDesignPage.cs");
        Assert.Contains("窄舌形状上曾测得", s);   // −1.4 是在哪测的
        Assert.Contains("−1.4", s);
        Assert.Contains("现役宽舌形状", s);        // 反方向是在哪测的
        Assert.Contains("137 g", s);             // 代价也要说
        // DiscHot／DipHigh 已删：不许留着一段没人挂的指令让门空转
        string na = Read("Core", "NextAction.cs");
        Assert.DoesNotContain("public const string DiscHot", na);
        Assert.DoesNotContain("public const string DipHigh", na);
    }

    /// <summary>
    /// ★ 必须要求使用者**自己实测**，并点名工具。
    /// 只说「这个数不一定准」而不给测法，等于把人卡在原地。
    /// </summary>
    [Fact]
    public void 要求对手上的形状实测并点名工具()
    {
        // 2026-09-14 Opus 5（复审）：NextAction.cs 那一半随 DiscHot 删掉（见类说明）；界面那一处照旧钉。
        string s = Read("UI", "LineDesignPage.cs");
        Assert.Contains("--monotone", s);
        Assert.Contains("别照抄任何一个数", s);
    }
}
