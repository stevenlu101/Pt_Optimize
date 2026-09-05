using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **用户说的要求不许只留在对话里**（2026-09-05）。
///
/// 用户原话：「总是要我反复的提醒，这样真的很累」。
/// 起因：用户要求「舌板开孔，尺寸、孔径、厚度都要能优化」，我实测发现它不划算
/// （圆盘槽好 60 倍），**就自己把它关了** —— 把「不划算」当成了「不需要」。
///
/// 根因不在那一次，在**要求只记在对话里，而对话会滑走**。
/// ⇒ HANDOVER §0.0.3 建了「用户明确要求登记表」：说了就登记、标状态，做完才改。
///   本门盯着那张表别消失、别退化成空表。
///
/// ⚠ 本门**不判要求做完没有** —— 那是登记表自己那一列的事。
///   它判的是「**登记这件事还在做**」。
/// </summary>
public class RequirementRegisterTests
{
    private static string Section()
    {
        string doc = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "HANDOVER.md"));
        int a = doc.IndexOf("用户明确要求登记表", StringComparison.Ordinal);
        Assert.True(a > 0, "HANDOVER 里找不到「用户明确要求登记表」—— 那张表是防止要求被悄悄丢掉的唯一机制");
        int b = doc.IndexOf("#### 对帐表", a, StringComparison.Ordinal);
        Assert.True(b > a, "找不到登记表的结尾");
        return doc[a..b];
    }

    /// <summary>★★★ 表里要真有要求（空表等于没有机制）。</summary>
    [Fact]
    public void 登记表里真的记着要求()
    {
        var rows = Section().Split('\n')
            .Where(l => l.TrimStart().StartsWith("| R", StringComparison.Ordinal)).ToArray();
        Assert.True(rows.Length >= 5,
            $"登记表只有 {rows.Length} 条 —— 表还在但空了，等于机制没了");
        // 每一条都要有状态列（✅ / ◐ / ✗），不许留白
        Assert.All(rows, r => Assert.True(
            r.Contains('✅') || r.Contains('◐') || r.Contains('✗'),
            "这一条没有状态：" + r.Trim()));
    }

    /// <summary>
    /// ★★★ 那条规矩本身必须写在表上：**「实测不划算」不是关闭要求的理由**。
    /// 这正是 2026-09-05 犯的那次错 —— 少了这句话，同样的事会再发生。
    /// </summary>
    [Fact]
    public void 不许拿实测结论替用户撤要求()
    {
        string sec = Section();
        Assert.Contains("不是", sec);
        Assert.Contains("不划算", sec);
        Assert.Contains("由**用户**决定", sec);
    }

    /// <summary>
    /// ★★★★★ **门 C：状态只有三种，没有「不做」**（2026-09-05 用户拍板）。
    ///
    /// 当天的错就出在这里：我把 R5 从 `✗` 改成「◐ 算得对但不当旋钮」，
    /// 门看不出「做完了」和「我决定不做」的区别 —— 它只看到状态被填了。
    /// ⇒ 取消「不做」这一档。我认为某条不该做，只能把理由写进备注，
    ///   **那一行仍然是 ✗**，用户下次看登记表就会看到，撤不撤由用户决定。
    /// </summary>
    [Fact]
    public void 状态只有三种没有不做这一档()
    {
        string sec = Section();
        Assert.Contains("没有「不做」", sec);
        Assert.Contains("仍然是", sec);

        // 表里不许出现「不做／不需要／不采用」这类自作主张的状态词
        var rows = sec.Split(((char)10).ToString())
            .Where(l => l.TrimStart().StartsWith("| R", StringComparison.Ordinal)).ToArray();
        foreach (string bad in new[] { "不做", "不需要", "不采用", "已放弃", "取消" })
            Assert.DoesNotContain(rows, r => r.Contains("| " + bad, StringComparison.Ordinal)
                                          || r.Contains("｜" + bad, StringComparison.Ordinal));
    }

    /// <summary>
    /// ★★★ **✅ 必须附证据**：提交号、测试名或实测档案。
    /// 光写「做完了」不算 —— 那正是「让用户以为的和事实不一样」。
    /// </summary>
    [Fact]
    public void 标了做完的必须附证据()
    {
        var done = Section().Split(((char)10).ToString())
            .Where(l => l.TrimStart().StartsWith("| R", StringComparison.Ordinal) && l.Contains('✅'))
            .ToArray();
        Assert.All(done, r => Assert.True(
            r.Contains("实测", StringComparison.Ordinal) || r.Contains("跑通", StringComparison.Ordinal)
         || r.Contains("Tests", StringComparison.Ordinal) || r.Contains("deliverable", StringComparison.Ordinal)
         || r.Contains("已做", StringComparison.Ordinal),
            "这条标了 ✅ 却没有证据（提交号／测试名／实测档案）：" + r.Trim()));
    }

    /// <summary>★★ 最高准则要写在最前面 —— 它是这一整套的出发点。</summary>
    [Fact]
    public void 最高准则在文档里()
    {
        string doc = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "HANDOVER.md"));
        Assert.Contains("我可以做错，不可以让你以为的和事实不一样", doc);
        Assert.Contains("把事情搞混我不允许", doc);
    }

    /// <summary>★★ 反复犯的错那张清单也要在 —— 它和登记表是同一套防线。</summary>
    [Fact]
    public void 反复犯的错清单还在()
    {
        string doc = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "HANDOVER.md"));
        Assert.Contains("反复犯的错", doc);
        Assert.Contains("门只钉**意图**", doc);
        Assert.Contains("先确认**它看得见那件事**", doc);
    }
}
