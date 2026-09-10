using System.IO;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R38（2026-09-11）：源码钉子（照 <see cref="SolverWiredToUiTests"/> 的写法）——
/// 「◇ 搜形状」把锥形舌片与孔族（不挖舌孔／挖舌孔）都接进搜索空间之后，两件事必须
/// 在源码里看得见，不能只是「概念上做了」：
///
///   ① 胜出者的锥形要写回 <c>_tabTaper.Checked</c>——与盘径/舌宽/舌长同一处、
///      同一 <c>_suppressAuto</c> 包裹，否则会出现「盘径写回了、锥形没跟着走」
///      这种半吊子接线（本项目最常见的病）。
///   ② 「① 页解法 = 两个都算」时，搜形状要对两族**各跑一遍完整搜索**——用户
///      09-11 原话「工程师看到每一族最轻的可行形状……不用手动改勾选反复跑」。
///      钉的是 <c>FamilyBoth</c> 为真时确实调了两次搜索主体，不是只调了一次
///      再拿同一个结果分两行印。
/// </summary>
public class ShapeSearchTaperFamilyWiringTests
{
    private static string Ui() =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));

    /// <summary>写回处含 <c>_tabTaper.Checked = </c>，且与盘径/舌宽/舌长写回在同一段
    /// （同一个 <c>_suppressAuto = true; … _suppressAuto = false;</c> 区块里）。</summary>
    [Fact]
    public void 胜出锥形写回控件_与盘径舌宽舌长同一处()
    {
        string s = Ui();
        Assert.Contains("_tabTaper.Checked = fin1.Design.TabTaper;", s);

        int atDisc = s.IndexOf("_discD.Value = C(2 * fin1.Design.DiscRadiusMm, _discD);");
        int atTabW = s.IndexOf("_tabW.Value = C(fin1.Design.TabHalfWidthMm, _tabW);");
        int atTaper = s.IndexOf("_tabTaper.Checked = fin1.Design.TabTaper;");
        int atSuppressOff = s.IndexOf("_suppressAuto = false;", atDisc);
        Assert.True(atDisc > 0 && atTabW > atDisc, "盘径/舌宽写回顺序不对，钉子本身要先更新");
        Assert.True(atTaper > atTabW && atTaper < atSuppressOff,
            "锥形写回必须夹在盘径/舌宽写回与那次 _suppressAuto = false 之间——同一段、同一个抑制块");
    }

    /// <summary>两族独立搜索的主体只写一份 —— 不许把整段搜索逻辑复制两遍
    /// （那是本项目「同一件事写两遍、改一处忘一处」最典型的病灶）。</summary>
    [Fact]
    public void 搜索主体只有一份局部函数()
    {
        string s = Ui();
        var m = System.Text.RegularExpressions.Regex.Matches(s,
            @"async Task<SolverResult\?> SearchOneFamilyAsync\(");
        Assert.Single(m);
    }

    /// <summary><c>FamilyBoth</c> 为真时，搜索主体确实被调了两次——一次不挖舌孔、
    /// 一次挖舌孔；不是「两个都算」时只调一次（当前唯一在跑的那族）。</summary>
    [Fact]
    public void 两个都算时搜索主体被调用两次()
    {
        string s = Ui();
        var calls = System.Text.RegularExpressions.Regex.Matches(s,
            @"await SearchOneFamilyAsync\(");
        Assert.Equal(2, calls.Count);   // fin1 那一次 + `if (both)` 里 fin2 那一次

        // 第二次调用必须挂在 `if (both)` 分支里，且第一个实参跟着 both 变
        int ifBoth = s.IndexOf("if (both)", System.StringComparison.Ordinal);
        Assert.True(ifBoth > 0);
        int secondCall = calls[1].Index;
        Assert.True(secondCall > ifBoth, "第二次调用必须在 `if (both)` 分支内部，不是无条件跑两次");

        // 第二次固定传 allowCuts=true（挖舌孔族）
        Assert.Contains("fin2 = await SearchOneFamilyAsync(true, \"挖舌孔\",", s);
        // 第一次的族随 both 变：两个都算 ⇒ 固定先不挖；不是两个都算 ⇒ 当前选的那族
        Assert.Contains("both ? false : FamilyAllowsCuts,", s);
    }

    /// <summary>下拉选形状（<c>PickShape</c>）也要把锥形写回 —— 与搜形状自动写回赢家
    /// 走的是「同一条路」（<c>PickShape</c> 自己的文档注释就是这么说的），若只有自动写回
    /// 那一处补了 <c>_tabTaper.Checked</c>，这一处漏了，就会出现「下拉条目写着锥形、
    /// 选中之后页面勾选框却是空的」——赋了值却没接到这个控件，本项目最常见的那类病。</summary>
    [Fact]
    public void 下拉选形状也写回锥形()
    {
        string s = Ui();
        int atPick = s.IndexOf("private void PickShape()", System.StringComparison.Ordinal);
        Assert.True(atPick > 0);
        int atAdopt = s.IndexOf("AdoptSolvedDesign(r.d, null);", atPick);
        Assert.True(atAdopt > atPick, "钉子本身要先更新：PickShape 的收尾调用变了");
        string block = s.Substring(atPick, atAdopt - atPick);
        Assert.Contains("_tabTaper.Checked = r.d.TabTaper;", block);
    }

    /// <summary>写回页面的固定是家族 1（不挖舌孔那族，或不是「两个都算」时唯一在跑的那族）；
    /// 家族 2（挖舌孔）不写回控件，只进「搜形状结果 ▾」——R32 的「不比重量」原则在
    /// 搜形状里也要成立。</summary>
    [Fact]
    public void 家族二不直接写回控件_只进下拉()
    {
        string s = Ui();
        int atFin2 = s.IndexOf("fin2 = await SearchOneFamilyAsync(true, \"挖舌孔\",", System.StringComparison.Ordinal);
        int atWriteBack = s.IndexOf("_tabTaper.Checked = fin1.Design.TabTaper;", System.StringComparison.Ordinal);
        Assert.True(atFin2 > 0 && atWriteBack > 0);
        // 写回段只认 fin1，不出现 fin2.Design 被写进任何 NumericUpDown/CheckBox
        string writeBackBlock = s.Substring(atWriteBack, 400);
        Assert.DoesNotContain("fin2.Design", writeBackBlock);
    }
}
