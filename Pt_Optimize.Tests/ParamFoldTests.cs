using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **参数表按当前这一格折叠**（2026-09-02）。
///
/// 实测：左边 53 项同等字重铺开，其中 10 项属「8 ✗ 被页面/设计记录接管」
/// 与「9 ✗ 对整线链无效」—— **改了不起作用**，却和管用的长得一模一样。
///
/// 而 <c>StageSpec.ParamCategoryPrefixes</c> 2026-08-20 就建好了，两个毛病叠在一起：
/// <code>
///   ① 写好没接线    —— 全仓 0 个消费者
///   ② 数据是错的    —— 填「C 整线」，而真实类别名是「5 C 整线 — 管几何」
///                     用 StartsWith 永远不中；就算接上也是空转
/// </code>
/// 所以它错了两周没人知道。本门盯的就是这两条。
///
/// ⚠ **折叠，不是隐藏**：类别标题还在，点一下就开。
///   用 <c>BrowsableAttributes</c> 过滤会让没标注到的属性静默消失，
///   而「看不见又在起作用」正是本项目反复栽的那一类（Flow.cs 里写死过这条判断）。
/// </summary>
public class ParamFoldTests
{
    private static string[] Categories() =>
        typeof(DesignInputs).GetProperties()
            .Select(p => p.GetCustomAttribute<System.ComponentModel.CategoryAttribute>()?.Category)
            .Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct().ToArray();

    /// <summary>★★ 每个前缀都得**真的命中**至少一个类别 —— 否则接上了也是空转。</summary>
    [Fact]
    public void 每个前缀都匹配得到真实类别()
    {
        var cats = Categories();
        Assert.True(cats.Length >= 5, $"只读到 {cats.Length} 个类别 —— 下面是空转");

        var ghost = Flow.Stages
            .SelectMany(st => st.ParamCategoryPrefixes.Select(p => (st.Title, p)))
            .Where(x => !cats.Any(c => c.Contains(x.p, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(ghost.Length == 0,
            "这些前缀一个类别都匹配不到（接上了也是空转）："
            + string.Join("、", ghost.Select(x => $"{x.Title}:「{x.p}」"))
            + "。现有类别：" + string.Join(" / ", cats));
    }

    /// <summary>★ 两类「✗ 改了不起作用」的**谁都不该展开**。</summary>
    [Fact]
    public void 不起作用的那两类不被任何一格展开()
    {
        var dead = Categories().Where(c => c.Contains("✗", StringComparison.Ordinal)).ToArray();
        Assert.True(dead.Length >= 1, "找不到标 ✗ 的类别 —— 下面是空转");
        foreach (var st in Flow.Stages)
            foreach (string p in st.ParamCategoryPrefixes)
                Assert.DoesNotContain(dead, d => d.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>★★ 接线在：切页时真的会去折叠（不是又一个写好没人用的字段）。</summary>
    [Fact]
    public void 折叠真的接上了()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "MainForm.cs"));
        Assert.Contains("private void FoldParams(StageId stage)", s);
        Assert.Contains("FoldParams(st0);", s);
        Assert.Contains("ParamCategoryPrefixes", s);
        // 折叠 ≠ 隐藏：不许改用 BrowsableAttributes 过滤
        // ⚠ 钉的是**真的赋值**，不是这三个字出现过 —— 上面那段注释里就写着它。
        //   （2026-09-02 第一版写成 DoesNotContain("BrowsableAttributes")，
        //     当场断在自己的注释上；同 --monotone 那次一个错。）
        Assert.DoesNotContain("_grid.BrowsableAttributes =", s);
    }

    /// <summary>
    /// ★★ 主线那一格要展开它真正用得上的那几类（否则折叠成了碍事）。
    ///
    /// ⚠ 钉的是**真实类别有没有被展开**，不是前缀字面量。
    ///   上一版写死了 <c>Assert.Contains("C 整线", p)</c> —— 那是**链代号**，
    ///   2026-09-03 把类别改成人话（工程师看不懂 A·B·C / 整线链）时它当场假红：
    ///   界面明明更好了，门却说坏了。**门要钉意图，不钉当时的措辞。**
    /// </summary>
    [Theory]
    [InlineData("管几何")]
    [InlineData("法兰")]
    [InlineData("电气")]
    [InlineData("保温")]
    [InlineData("玻璃")]
    [InlineData("数值")]
    public void 主线那一格展开的是它用得上的(string need)
    {
        var prefixes = Flow.Stage(StageId.整线核算).ParamCategoryPrefixes;
        var hit = Categories()
            .Where(c => c.Contains(need, StringComparison.Ordinal)
                     && !c.Contains("✗", StringComparison.Ordinal))
            .ToArray();
        Assert.True(hit.Length > 0, $"参数表里根本没有「{need}」这一类 —— 这条是空转");
        Assert.True(hit.Any(c => prefixes.Any(p => c.Contains(p, StringComparison.Ordinal))),
            $"「{string.Join("、", hit)}」在主线那一格是折起来的 —— 那正是整线核算要填的东西");
    }
}
