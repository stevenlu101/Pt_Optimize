using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写）：**圆盘区判据「有几片判不了」时，整条必须判不了。**
///
/// ══ 洞在哪
///
/// 原来是：
/// <code>
///   var hottestDisc = flanges.Where(f => !double.IsNaN(f.TDiscMaxC))
///                            .OrderByDescending(f => f.TDiscMaxC - f.TRootC).FirstOrDefault();
/// </code>
/// `Where(!IsNaN)` 把算不出圆盘区温度的片**静默滤掉**，再从剩下的片里挑最热的报出来。
/// 于是一条**硬安全线**在那几片上从来没判过，而输出上一点看不出来。
/// 代码注释当年只堵了「**所有**片都 NaN」那一种（`FirstOrDefault()` 返回 null 才报无法判定）。
///
/// ⚠ 这与同一文件里局部热稳定那条 2026-08-24 修过的病**逐字同形**：
/// 当时也是 `Where(!NaN).OrderBy(...).First()`，取「剩下几片里最差的」，
/// 而判不了的恰恰是发散的那一片 —— 发散算例于是报出一个由健康片算来的漂亮数
/// （四片最高 4361 °C，判据却报 1.9× 比设计记录还安全）。
/// 那次定下的规矩是**任何一片判不了 ⇒ 整条判不了**；②″ 漏到 09-14 才照办。
///
/// ══ 什么时候会发作
///
/// TDiscMaxC 在「一格都没被判进圆盘区」时是 NaN。分区规则是
/// <c>onTab = 双舌片 ? |x| &gt; |切点x| : x &lt; 切点x</c>，
/// 而**舌半宽 = 盘半径**时切点落在 x = 0 —— 双舌片情况下 <c>|x| &gt; 0</c> 几乎每格都算舌片，
/// 圆盘区就成了空集。
///
/// ⚠ 实测：两个现役内置档（管壁 0.8／0.6）四片都是**单舌片**，圆盘区为空的片数 **0/4**，
///   所以本次修改**不改变现役档的任何判定**。堵的是换个几何才会发作的那个洞。
/// </summary>
public class R48DiscBlindPlateTests
{
    private static string Src(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    /// <summary>源码门：不许再用 `Where(!IsNaN)` 把判不了的片悄悄滤掉。</summary>
    [Fact]
    public void 圆盘区判据不许静默滤掉判不了的片()
    {
        string s = Src("Pt_Optimize/Core/LineRunner.cs");
        int i = s.IndexOf("var hottestDisc", StringComparison.Ordinal);
        Assert.True(i > 0, "找不到圆盘区判据挑片那一段 —— 门失去了守护对象，先修门");
        string blk = s.Substring(i, Math.Min(400, s.Length - i));
        Assert.DoesNotContain("Where(f => !double.IsNaN(f.TDiscMaxC))", blk);
        // 必须先看「有没有片判不了」
        Assert.Contains("blindDisc", blk);
    }

    /// <summary>
    /// 「无法判定」那一支必须**点名是哪几片**。只说「判不了」而不说是谁，
    /// 读的人无从下手 —— 这是 2026-08-30 定下的规矩（说了「不过」就必须说是哪一条／哪一片）。
    /// </summary>
    [Fact]
    public void 判不了的时候要点名是哪几片()
    {
        string s = Src("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("blindDisc.Select(f => f.Name)", s);
        Assert.Contains("任何一片判不了，整条就判不了", s);
    }

    /// <summary>
    /// 现役两个内置档**不受影响**：四片都算得出圆盘区温度。
    /// 这一条同时是上面那条修改的「不改变现役判定」的凭据 —— 不是嘴上说说。
    /// ⚠ 只建几何、不解场（分区只看几何），毫秒级。
    /// </summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.6)]
    public void 现役内置档四片都不是双舌片_圆盘区不会是空集(double wallMm)
    {
        var d = (wallMm > 0.7 ? DesignSpec.W08 : DesignSpec.W06).Clone();
        var p = new DesignInputs();
        double floor = d.DiscFloorMm(p);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            var g = d.Plate(j, floor);
            Assert.False(g.TwoTabs,
                $"片{j} 是双舌片，而切点 x = {g.Tangent().X:0.000}（舌半宽 {g.TabEndHalfWidthMm:0.0}，盘半径 {g.DiscRadiusMm:0.0}）"
              + " —— 切点落在 0 附近时双舌片会让圆盘区成为空集，这一档的 ②″ 会变成「无法判定」。"
              + "若这是有意改的几何，请连同 ②″ 的分区口径一起重新评估。");
        }
    }
}
