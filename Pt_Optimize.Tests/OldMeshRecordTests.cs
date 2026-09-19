using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R48（2026-09-13，Opus 5 写）：<see cref="DesignSpec.RecordFromOldMesh"/> 这个开关的门。
///
/// 它存在的理由：R47 把网格轴修对之后，同一个设计的抽热约为原来的 4～8 倍（旧轴在管孔的一侧留了 8.46 mm 的粗格，从未被加密），
/// 旧记录里的热学项与铂重与新实算**必然对不上**。已作废、不再重解的档只能把记录值留作历史 ⇒ 自检门对它们只报不判。
///
/// ⚠ 这个开关一旦被用到**现役**档上，自检门 A 的「复现设计记录」就等于被关掉了 —— 那是本项目最怕的一类退化
/// （不报错、格式正常、结论错）。所以本门钉的是：**只有已作废的档准用它，而且必须在失效告示里说清热学结论不可引用**。
/// </summary>
public class OldMeshRecordTests
{
    [Fact]
    public void 现役档一律不许标成修网格前的记录值()
    {
        foreach (var d in new[] { DesignSpec.W08, DesignSpec.W06 })
        {
            Assert.False(d.RecordFromOldMesh,
                $"「{d.Name}」是现役档，不许用 RecordFromOldMesh 绕过复现门 —— 它要按 R48 重解并写回修好网格上的记录值");
            Assert.Equal("", d.Invalid);          // 现役档没有失效告示
        }
    }

    [Fact]
    public void 已作废两档标了修网格前_且失效告示说清热学结论不可引用()
    {
        foreach (var d in new[] { DesignSpec.Retired08, DesignSpec.Retired06 })
        {
            Assert.True(d.RecordFromOldMesh, $"「{d.Name}」不再重解，记录值出自修网格前，要标 RecordFromOldMesh");
            Assert.Contains("热学结论不可再引用", d.Invalid);
            // 修好的网格上实算出来的那条新失败（热往管里灌）必须写进声明，否则自检门会报「声明之外的失败」
            Assert.Contains("管孔净流入", d.Invalid);
            Assert.Contains(LineResult.Key.NetFlux, d.InvalidChecks);
            Assert.Contains(LineResult.Key.DiscTemp, d.InvalidChecks);
            Assert.Contains(LineResult.Key.FlangeDip, d.InvalidChecks);
        }
    }

    [Fact]
    public void 失效告示与咬住它的那句都不许出现判据代号()
    {
        // 这两段都会原样印到「使用说明」页第一块（ManualPage.BuildHtml）⇒ 归「界面不许出现判据代号」管。
        foreach (var d in DesignSpec.Builtin)
            foreach (var (what, text) in new[] { ("失效告示", d.Invalid), ("咬住它的", d.Binding) })
                Assert.False(Criteria.HasCode(text),
                    $"「{d.Name}」的{what}里有判据代号，工程师看不懂：{text}");
    }

    [Fact]
    public void 自检门只对标了修网格前的档放行_打印照旧()
    {
        // 源码门：放行只改「计不计 bad」，不改打印内容 —— 数与差值照印，人看得见。
        string src = File.ReadAllText(SrcPath("Pt_Optimize/Program.cs"));
        int i = src.IndexOf("bool oldMesh = fd.RecordFromOldMesh;", StringComparison.Ordinal);
        Assert.True(i > 0, "自检门 A 里找不到 RecordFromOldMesh 的放行 —— 它被删了还是改名了？");
        string blk = src.Substring(i, Math.Min(1400, src.Length - i));
        Assert.Contains("if (!ok && !oldMesh) bad++;", blk);                 // 只影响计数
        Assert.Contains("记录 {want,8:0.000}", blk);                          // 记录值照印
        Assert.Contains("差 {got - want,+7:0.000}", blk);                     // 差值照印
        Assert.Contains("只报不判", blk);                                      // 说清楚为什么没红
    }

    private static string SrcPath(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, rel);
    }
}
