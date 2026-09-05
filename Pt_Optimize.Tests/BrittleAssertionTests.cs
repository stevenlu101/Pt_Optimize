using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **门 A：不许写「会因为变好而红」的断言**（2026-09-05 用户拍板）。
///
/// 当天实证：加第 7 个旋钮，四个测试红了 —— 红的**不是功能坏了，是功能变好了**：
/// <code>
///   Assert.Equal(6, Enum.GetValues&lt;Solver.Knob&gt;().Length)   ← 钉个数
///   Assert.Contains("C 整线", prefixes)                          ← 钉当时的措辞
///   "按钮「自动定厚」"                                            ← 钉按钮名字
/// </code>
/// 这种红最危险：我会去改测试，**改着改着把真该守的也改松了**。
///
/// ⇒ 门只许钉**意图**。个数、名字、文案这些会随「变好」而变的东西，
///   要么从唯一来源读（`Flow.Cmd(id).Text`），要么换成不变量
///   （「每个旋钮都得有独一无二的名字」而不是「必须正好 6 个」）。
///
/// ⚠ 本门自己也遵守这条：它不钉「测试文件必须有 N 个」，只钉**写法**。
/// </summary>
public class BrittleAssertionTests
{
    /// <summary>
    /// 只盯**全局注册表**的个数 —— 那些会随「能力变强」而增长：
    /// 旋钮表、分派表、命令表、阶段表、判据表、内置设计记录。
    ///
    /// ⚠ 第一版写成「凡是 Assert.Equal(数字, ….Length) 都算」，当场抓出 14 处，
    ///   而其中大多数是**正当**的（如「4 段 ⇒ 5 片」——那正是该钉的不变量）。
    ///   门自己也犯了它要防的错：**钉了写法，没钉意图**。⇒ 收窄到注册表。
    /// </summary>
    private static readonly Regex PinCount = new(
        @"Assert\.Equal\(\s*\d+\s*,[^;]*?"
      + @"(Enum\.GetValues|Solver\.Allocation|Flow\.Commands|Flow\.Stages"
      + @"|Criteria\.All|DesignSpec\.Builtin|DesignSpec\.All)",
        RegexOptions.Compiled);

    /// <summary>豁免：确实该固定的个数（几何维度、物理常数的位数等）。写清理由才准豁免。</summary>
    private static readonly string[] Allow =
    {
        "// 门A豁免：",
    };

    private static string[] TestFiles() => Directory.GetFiles(
        Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests"), "*.cs");

    [Fact]
    public void 不许钉个数()
    {
        var bad = new System.Collections.Generic.List<string>();
        foreach (string f in TestFiles())
        {
            if (Path.GetFileName(f) == "BrittleAssertionTests.cs") continue;   // 本门自证要写出那种写法
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!PinCount.IsMatch(lines[i])) continue;
                // 上一行或本行写了豁免理由 ⇒ 放过
                string ctx = (i > 0 ? lines[i - 1] : "") + lines[i];
                if (Allow.Any(a => ctx.Contains(a, StringComparison.Ordinal))) continue;
                bad.Add($"{Path.GetFileName(f)}:{i + 1}  {lines[i].Trim()}");
            }
        }
        Assert.True(bad.Count == 0,
            "这些断言钉的是**个数**，加一个就假红（红的是「变好」不是「坏了」）："
          + Environment.NewLine + string.Join(Environment.NewLine, bad)
          + Environment.NewLine
          + "⇒ 改成钉不变量（如「每个都得有独一无二的名字」）；"
          + "确实该固定的，在上一行写 `// 门A豁免：<理由>`。");
    }

    /// <summary>
    /// 界面上的**名字**只许从 <c>Flow</c> 读，不许在测试里写死 ——
    /// 2026-09-04 我改了一个按钮名，界面接线测试当场五项红。
    /// </summary>
    [Fact]
    public void 不许在测试里写死按钮名字()
    {
        var names = new[] { "\"自动定厚\"", "\"◇ 搜形状\"", "\"核算整线\"", "\"另存为设计记录\"" };
        var bad = new System.Collections.Generic.List<string>();
        foreach (string f in TestFiles())
        {
            if (Path.GetFileName(f) == "BrittleAssertionTests.cs") continue;   // 本门自己要提到它们
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal)) continue;
                foreach (string n in names)
                    if (lines[i].Contains(n, StringComparison.Ordinal))
                        bad.Add($"{Path.GetFileName(f)}:{i + 1}  {lines[i].Trim()}");
            }
        }
        Assert.True(bad.Count == 0,
            "按钮名字写死在测试里，改名就假红：" + Environment.NewLine
          + string.Join(Environment.NewLine, bad) + Environment.NewLine
          + "⇒ 改成 Flow.Cmd(\"<命令 id>\").Text —— Flow 是命令表的唯一来源。");
    }

    /// <summary>★ 自证：正则真的认得出那种写法（否则上面两条恒绿）。</summary>
    [Fact]
    public void 自证_认得出钉个数的写法()
    {
        Assert.Matches(PinCount, "        Assert.Equal(6, Enum.GetValues<Solver.Knob>().Length);");
        Assert.Matches(PinCount, "Assert.Equal(3, Solver.Allocation.Length);");
        // ★ 正当的不许被误抓：「4 段 ⇒ 5 片」是该钉的不变量，不是脆弱断言
        Assert.DoesNotMatch(PinCount, "Assert.Equal(5, d.TabThickMm.Length);");
        Assert.DoesNotMatch(PinCount, "Assert.Equal(names.Length, names.Distinct().Count());");
    }
}
