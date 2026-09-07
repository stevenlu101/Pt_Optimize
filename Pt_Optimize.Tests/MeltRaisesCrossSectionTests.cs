using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **熔化／过热的处置只有一条：加大过流截面 A = 厚 × 宽**（用户 2026-09-03 定，
/// 2026-09-07 当场再纠正一次）。
///
/// 用户原话：「过热（严重烧毁）表示电流密度过大，应该加大电流的**截面积**、降电流密度，
/// 所以可能是**增厚或增宽**（这时就需透过搜形状／搜厚度来解决）。在 APP 里…APP 要提供一个
/// 首先能用的且铂金用量最少的方案出来，而不是一句话就判死刑结案了。」
///
/// ══ 这道门为什么要重写
///
/// <see cref="OverheatDrivesSizingTests"/> 已经把上面那条钉住了 —— 但它只读
/// <c>SolveByLevel</c> 里那一处的源文本。2026-09-07 我在**另一个入口**
/// <c>FlangeAutoSizer.Solve</c> 里新开了一个熔化分支，写成：
/// <code>
///   for (int q = 0; q &lt; t.Length; q++) t[q] = Math.Min(opt.MaxThickMm, t[q] * 1.5);   // 整片乘
///   …
///   res.Message = "**顶到厚度上界仍熔化** ⇒ 这个几何在此电流下无解（不是搜索失败，是证明）"; // 判死刑
/// </code>
/// 两条都违背已定口径（整片乘 = 替不热的片花铂；厚度到顶 ≠ 无解，宽度一次都没动过），
/// **而那道门全绿** —— 它看不见新方法里的新分支。
///
/// ⇒ 本门不钉某一处的措辞，钉的是**登记**：文件里对「熔化／过热」动手的地方
///   一共有几处、分别在哪个方法里。新开第四处 ⇒ 本门变红 ⇒ 必须来这里说明
///   它是怎么「加截面积」的。这是「判据只有一个来源」在源码层的落法。
/// </summary>
public class MeltRaisesCrossSectionTests
{
    private static string Sizer() => File.ReadAllText(Path.Combine(
        HandoverDoc.Root(), "Pt_Optimize", "Core", "FlangeAutoSizer.cs"));

    /// <summary>某个位置往前找最近的 `方法名(`，用来说清这一处在谁里面。</summary>
    private static string MethodAt(string s, int at)
    {
        int best = -1; string nm = "（找不到所属方法）";
        foreach (var key in new[] { "public static Result Solve(", "public static Result SolveAuto(",
                                    "public static Result SolveByLevel(", "private static void Verify(",
                                    "private static LineResult? EvalScale(" })
        {
            int k = s.LastIndexOf(key, at, StringComparison.Ordinal);
            if (k > best) { best = k; nm = key.Substring(key.LastIndexOf(' ') + 1).TrimEnd('('); }
        }
        return nm;
    }

    /// <summary>找出所有「条件里提到熔化/过热」的 if —— 也就是真正动手的那些分支。</summary>
    private static List<(int At, string Method, string Cond)> Handlers(string s)
    {
        var found = new List<(int, string, string)>();
        for (int i = 0; (i = s.IndexOf("if (", i, StringComparison.Ordinal)) >= 0; i += 4)
        {
            int nl = s.IndexOf(((char)10), i); if (nl < 0) nl = s.Length;
            string cond = s[i..nl];
            // 只认**条件里**提到熔/过热的；注释里出现多少次都不算动手
            if (!cond.Contains("OverMelt", StringComparison.Ordinal)
             && !cond.Contains("OverheatRaiseFromC", StringComparison.Ordinal)) continue;
            found.Add((i, MethodAt(s, i), cond.Trim()));
        }
        return found;
    }

    /// <summary>
    /// ★★★★★ **动手的地方一共几处、在哪 —— 登记在案**。
    /// 新增一处不会静悄悄过去；它必须来这张表上报到，并说明怎么加截面积。
    /// </summary>
    [Fact]
    public void 熔化与过热的处置点必须逐处登记()
    {
        string s = Sizer();
        var hs = Handlers(s);

        // 登记表：所属方法 → 这一处凭什么算「加截面积」
        var 登记 = new Dictionary<string, string>
        {
            ["Solve"] = "解析几何路（只有厚度一根旋钮）：只加**最热那一片**的厚度；"
                      + "厚度到顶 ⇒ 如实交棒给增宽／搜形状，不许说无解",
            ["SolveByLevel"] = "逐级路：只加**最热那一级**；加到工艺上界实测；"
                             + "有用就二分最小够用；救不了转增宽（搜形状）",
        };

        var 未登记 = hs.Where(h => !登记.ContainsKey(h.Method)).ToList();
        Assert.True(未登记.Count == 0,
            "FlangeAutoSizer 里多出了没登记的熔化／过热处置点：" + ((char)10)
          + string.Join(((char)10), 未登记.Select(h => $"  · {h.Method}：{h.Cond}"))
          + ((char)10) + "—— 这正是 2026-09-07 那次绕回老问题的形状："
          + "已定口径（加截面积 = 增厚**或**增宽）有了第二个实现，而门只盯着第一个。"
          + "请到本门的登记表里写明这一处怎么加截面积，再决定要不要留它。");

        // 反向：登记了却没有实现 ⇒ 表是死的（防空转，别让这道门变成恒真）
        var 空登记 = 登记.Keys.Where(k => !hs.Any(h => h.Method == k)).ToList();
        Assert.True(空登记.Count == 0,
            "登记表里有这些方法，源码里却找不到对应的处置分支：" + string.Join("、", 空登记)
          + " —— 要么处置被删了（口径丢了），要么方法改名了（本门认不出，等于没门）。");

        Assert.True(hs.Count >= 2,
            $"只找到 {hs.Count} 处熔化／过热处置 —— 少于登记的两条路，本门多半没认出来（空转）。");
    }

    /// <summary>
    /// ★★★★ <c>Solve</c> 这一层**没有宽度旋钮**（opt 里只有 Min/MaxThickMm，
    /// 宽由调用方的 makePlate 定死）⇒ 厚度到顶时它**无权**下「无解」的结论，
    /// 只能交棒。这一条是 2026-09-03「不是一句话就判死刑结案」的直接落法。
    /// </summary>
    [Fact]
    public void 解析路厚度到顶只能交棒不许判死刑()
    {
        string s = Sizer();
        int a = s.IndexOf("if (!lr.Ok && lr.OverMelt)", StringComparison.Ordinal);
        Assert.True(a > 0, "Solve 里的熔化分支不见了 —— 熔化又会被当成普通失败一路带下去");
        int b = s.IndexOf("            if (!lr.Ok) { res.Message", a, StringComparison.Ordinal);
        Assert.True(b > a, "认不出熔化分支的结尾，本门失效");
        string blk = s[a..b];

        Assert.True(blk.Contains("增宽", StringComparison.Ordinal),
            "熔化处置里没有「增宽」这条出路 —— 截面 A = 厚 × 宽，只给厚就是把口径砍了一半。");

        // 「无解」只许以否定形式出现（本层没动过宽度，无权下这个结论）
        foreach (var (idx, _) in AllOf(blk, "无解"))
        {
            string near = blk[Math.Max(0, idx - 12)..idx];
            Assert.True(near.Contains("不是", StringComparison.Ordinal)
                     || near.Contains("不许", StringComparison.Ordinal),
                "Solve 的熔化处置里出现了正面的「无解」结论。厚度到顶只证明**厚度**救不了 —— "
              + "本层根本没有宽度旋钮，宽一次都没动过，无权替搜形状下这个结论。");
        }
    }

    private static IEnumerable<(int, string)> AllOf(string s, string k)
    { for (int i = 0; (i = s.IndexOf(k, i, StringComparison.Ordinal)) >= 0; i += k.Length) yield return (i, k); }
}
