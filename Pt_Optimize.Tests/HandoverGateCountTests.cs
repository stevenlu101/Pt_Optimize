using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// HANDOVER 里「`dotnet test` 应为 N/N」那个数**必须自己会对**。
///
/// 这个数已经烂过两次：§8 自己记着「2026-08-12 忘了改数，『应为 19/19』当了 8 天的假账」，
/// 然后又烂了一次 —— §1 写 18/18、§8 写 15/15，两处互相矛盾，而实际是 30。
/// 交接文档骗人比代码有 bug 更难查：接手的人照它跑一遍，看到数对不上，
/// **第一反应是怀疑自己的环境**，不会怀疑文档。
///
/// 所以把它从「一句要靠人记得改的散文」变成一条断言：谁加减测试而没同步文档，这里当场红。
/// </summary>
public class HandoverGateCountTests
{
    /// <summary>
    /// 从测试程序集反射数出运行器会报的用例数：[Fact] 一条，[Theory] 按数据行数。
    ///
    /// ⚠ 2026-08-24 修：原来只数 [InlineData]，于是 **[MemberData] 理论一律记 0**。
    ///   加了两个 MemberData 理论（各 7 行）之后，这里数出 94 而真跑是 108 ——
    ///   差的正是 14 行。**它没报错，只是少数了**：假如反过来（先改文档再补测试），
    ///   两个错数还会互相印证。这就是本项目说的「安静失败」在**门自己身上**的样子。
    ///   ⇒ 既补上 MemberData，也加一条「数不出行数的 Theory 一律炸」，
    ///     免得下一种数据来源（ClassData、自定义 DataAttribute）重演同一出。
    /// </summary>
    private static int ActualCaseCount()
    {
        int n = 0;
        foreach (var t in Assembly.GetExecutingAssembly().GetTypes())
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attrs = m.GetCustomAttributes().ToArray();
                if (attrs.Any(a => a.GetType().Name == "FactAttribute")) { n++; continue; }
                if (!attrs.Any(a => a.GetType().Name == "TheoryAttribute")) continue;

                int rows = attrs.Count(a => a.GetType().Name == "InlineDataAttribute");
                foreach (var a in attrs.Where(a => a.GetType().Name == "MemberDataAttribute"))
                    rows += MemberRowCount(t, a);

                Assert.True(rows > 0,
                    $"{t.Name}.{m.Name} 挂着 [Theory] 却一行数据都数不出来 —— "
                    + "多半是用了本方法还不认得的数据来源（ClassData / 自定义 DataAttribute）。"
                    + "记 0 会让总数**悄悄少算**，而少算的方向恰好是「文档看起来还对」。");
                n += rows;
            }
        return n;
    }

    /// <summary>数出一个 [MemberData(...)] 指向的静态成员会产出几行。</summary>
    private static int MemberRowCount(Type declaring, Attribute a)
    {
        var at = a.GetType();
        string name = (string)(at.GetProperty("MemberName")?.GetValue(a)
                               ?? throw new InvalidOperationException("MemberData 上读不到 MemberName"));
        var owner = at.GetProperty("MemberType")?.GetValue(a) as Type ?? declaring;
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        object? raw = owner.GetProperty(name, F)?.GetValue(null)
                   ?? owner.GetMethod(name, F)?.Invoke(null, null)
                   ?? owner.GetField(name, F)?.GetValue(null);

        if (raw is not System.Collections.IEnumerable e)
            throw new InvalidOperationException(
                $"[MemberData(「{name}」)] 在 {owner.Name} 上找不到可枚举的静态成员 —— "
                + "数不出来就不许当成 0 行");

        int k = 0;
        foreach (var _ in e) k++;
        return k;
    }

    private static string FindHandover()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "HANDOVER.md");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("往上找 8 层都没有 HANDOVER.md —— 断言失去了对象，不能算通过");
    }

    [Fact]
    public void Handover_StatedTestCount_MatchesReality()
    {
        int actual = ActualCaseCount();

        // 反自证：反射要是数出 0，说明这套反射写法失效了（比如换了测试框架），
        // 那下面「文档 == 实际」就成了拿两个错数互相印证。宁可在这里先炸。
        Assert.True(actual >= 20, $"反射只数出 {actual} 条用例，这条断言已经不认得测试了");

        string text = File.ReadAllText(FindHandover());

        // 只认「应为 N/N」这种**对当前状态的断言**；
        // 「2026-08-09 实测 18/18」那种带日期的历史记录不在此列，它当时是真的。
        var hits = Regex.Matches(text, @"应为\s*\*{0,2}(\d+)/(\d+)\*{0,2}")
                        .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                        .ToArray();

        // 空集通过是本项目反复栽的跟头（HANDOVER §7）：一个都没匹配到不是「没问题」，
        // 是「这条断言在空转」。
        Assert.True(hits.Length > 0,
            "HANDOVER 里找不到任何「应为 N/N」—— 要么写法变了、要么被删了，两种都得让这里响");

        foreach (var (a, b) in hits)
            Assert.True(a == b, $"HANDOVER 写着「应为 {a}/{b}」，分子分母就对不上");

        // 两处各说各的正是上一次的病灶（§1 说 18、§8 说 15）。
        var distinct = hits.Select(h => h.Item1).Distinct().ToArray();
        Assert.True(distinct.Length == 1,
            $"HANDOVER 里「应为 N/N」出现了不止一个数：{string.Join(" / ", distinct)} —— "
            + "同一个门有两个答案，读的人必然被骗一次");

        Assert.True(distinct[0] == actual,
            $"HANDOVER 说 dotnet test 应为 {distinct[0]}/{distinct[0]}，实际是 {actual} 条。"
            + "改了测试就同步改文档 —— 别让接手的人以为是自己环境坏了");
    }
}
