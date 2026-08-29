using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **判据代号必须有对照表，且不许拿代号当说明**
/// （2026-08-29 用户：「①②′②″ 这种代号要有对照表，APP 内任何一处讯息与 UI 说明书内
/// **不可以用代号说明**」）。
///
/// ══ 实测的现状
///
/// 全仓扫描：**197 行在判据语境里用了代号，其中 155 行没带自己的名字**。
/// 最大一类是**表头**（<c>②′W</c>、<c>②″K</c>、<c>③K</c>）——
/// 那里确实放不下全名，但那正是最需要图例的地方：
/// 一张全是代号的表，读的人得去别处查才看得懂，而**「得去别处查」在现场就等于「不查，猜」**。
///
/// ⚠ 第一次扫出 322 行，是**虚高**的：多数是普通序号（「阶段 A ①」「── ② 舌片末端宽度」），
///   不是判据代号。收紧判别（要有判据语境词）之后才是 197/155。
///   把 322 当依据去改，会改一堆不该改的地方 —— 先量准，再动手。
///
/// ══ 单一来源
///
/// 对照表**不另写一份名字**：判据自己的常量已经是「代号 + 名字」
/// （<c>LineResult.Key.FlangeDip == "③ 法兰增量温降"</c>），<see cref="Criteria"/> 从它拆。
/// 为此先给 ④ 等几条补上 <c>Key</c> 常量 —— 没有常量就只能抄一份名字。
/// </summary>
public class CriteriaGlossaryTests
{
    /// <summary>★ 名字必须**从 Key 拆出来**，不许自己写一份。</summary>
    [Fact]
    public void 名字来自判据自己的常量()
    {
        foreach (var e in Criteria.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Key));
            // 代号 + 名字 拼回去必须还原成 Key（允许中间有一个空格）
            string joined = (e.Code + e.Name).Replace("·", "");
            string key = e.Key.Replace(" ", "").Replace("·", "");
            Assert.Equal(key, joined.Replace(" ", ""));
        }
    }

    /// <summary>七条硬判据（<c>LineResult.Required</c> 名单）一条都不能漏。</summary>
    [Fact]
    public void 必须出现的判据全在表里()
    {
        foreach (var (prefix, _, _) in LineResult.Required)
            Assert.True(Criteria.All.Any(e => e.Key == prefix),
                $"对照表缺了「{prefix}」—— 它在 Required 名单里，读的人一定会遇到");
    }

    /// <summary>代号不许重复 —— 重复就说不清 ③ 到底指哪一条。</summary>
    [Fact]
    public void 代号不重复()
    {
        // ★ 只管**非空**代号：没有圈号的参考量本来就没有代号。
        //   此前它们全被塑成同一个「·」—— 那不是代号，是**分类标记**，
        //   于是九条参考量共用一个「代号」，Of("·") 说不清指哪条。
        var codes = Criteria.All.Select(e => e.Code).Where(c => c.Length > 0).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Null(Criteria.Of(""));
        Assert.Null(Criteria.Of("·"));
    }

    /// <summary>′ 与 ″ 要拆对：②′ 与 ②″ 是两条，不能被拆成同一个 ②。</summary>
    [Fact]
    public void 撇号拆得对()
    {
        Assert.NotNull(Criteria.Of("②′"));
        Assert.NotNull(Criteria.Of("②″"));
        Assert.Equal("管孔净流入", Criteria.Of("②′")!.Name);
        Assert.Equal("圆盘区最高温", Criteria.Of("②″")!.Name);
    }

    /// <summary>
    /// ★★ **对照表里不许出现限值数字**。限值只有一个来源（判据自己带 Limit）；
    /// 在这里再抄一份就会出现「印出来的 ≠ 判的」—— 本仓库为这件事栽过不止一次。
    /// </summary>
    [Fact]
    public void 对照表不含限值数字()
    {
        string t = Criteria.Table();
        foreach (var lim in new[] { "10 K", "≤ 10", "5 K", "≤ 5", "72 h", "12 A", "100 mm" })
            Assert.DoesNotContain(lim, t);
        Assert.Contains("**不含限值数字**", t);
        Assert.Contains("限值见判据表", Criteria.Legend("③"));
    }

    /// <summary>找不到的代号**原样返回**，不许编一个名字出来。</summary>
    [Fact]
    public void 找不到就不编()
    {
        Assert.Null(Criteria.Of("⑨"));
        Assert.Equal("⑨", Criteria.Explain("⑨"));
        Assert.Equal("", Criteria.Legend("⑨"));
    }

    /// <summary>图例给的是「代号 = 名字（单位，方向）」，三样都要在。</summary>
    [Fact]
    public void 图例带名字单位与方向()
    {
        string g = Criteria.Legend("②′", "②″", "③");
        Assert.Contains("②′ = 管孔净流入（W，>）", g);
        Assert.Contains("②″ = 圆盘区最高温（K，≤）", g);
        Assert.Contains("③ = 法兰增量温降（K，≤）", g);
    }

    /// <summary>
    /// ★ **拿代号当表头的表，必须带图例**。这是 155 行里最大的一类，
    /// 也是唯一「放不下全名」因而必须靠图例救的一类。
    /// </summary>
    [Fact]
    public void 代号表头的表都带了图例()
    {
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));

        // 网格复核表与单调性表 —— 两张全代号表头的表
        Assert.True(prog.Split("Criteria.Legend(").Length - 1 >= 2,
            "拿代号当表头的表没有全部带上 Criteria.Legend");
        Assert.Contains("--glossary", prog);
    }

    /// <summary>散文里的裸代号要用 <see cref="Criteria.Explain"/> 展开。</summary>
    [Fact]
    public void 散文里的代号被展开()
    {
        string prog = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("Criteria.Explain(\"③\")", prog);
    }

    /// <summary>′ ″ 的含义要写在表里 —— 它不是排版符号，是「同一条线的两个视角」。</summary>
    [Fact]
    public void 撇号的含义写在表里()
    {
        string t = Criteria.Table();
        Assert.Contains("同一条安全线的两个视角", t);
    }
}
