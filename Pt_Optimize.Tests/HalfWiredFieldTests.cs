using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **门 B：新加的逐片参数不许只接一半**（2026-09-05 用户拍板）。
///
/// 一个逐片参数要接**六处**才算数：
/// <code>
///   ① DesignSpec.Fit()      改段数时跟着增删（不接 ⇒ 越界，或算成另一个零件）
///   ② 存档 Dto              不接 ⇒ 存下来的档少一根旋钮，复算时它消失
///   ③ 防过期样本 Distinctive 不接 ⇒ 往返测试对它**空转**（漏存也验不出来）
///   ④ 界面控件              不接 ⇒ 工程师看不到、改不了
///   ⑤ 变更清单              不接 ⇒ 程序改了他的零件却不说
///   ⑥ 出图                  不接 ⇒ 算的和图不符
/// </code>
///
/// 当天实证：加 `SlotSpanDeg` 与 `TabHoleRMm`，**两次都漏**，
/// 靠别的门（防过期样本）碰巧抓回来。⇒ 直接扫字段，缺一处就红。
///
/// ⚠ 本门查的是**接线**，不是功能对不对。
/// </summary>
public class HalfWiredFieldTests
{
    /// <summary>DesignSpec 上「逐片」的 double[] 字段 —— 长度跟片数走的那些。</summary>
    private static string[] PerPlateArrays()
    {
        var d = DesignSpec.Builtin[0];
        int n = d.TabThickMm.Length;
        return typeof(DesignSpec)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(double[]))
            .Where(f => f.GetValue(d) is double[] a && a.Length == n)
            // 逐段（n-1）的不算；SegLengthMm 长度不同，天然被上一行排除
            .Select(f => f.Name)
            .ToArray();
    }

    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { HandoverDoc.Root() }.Concat(parts).ToArray()));

    [Fact]
    public void 每个逐片参数都要接满六处()
    {
        var fields = PerPlateArrays();
        Assert.True(fields.Length >= 5, $"只认出 {fields.Length} 个逐片数组 —— 扫描退化了，下面是空转");

        string spec = Src("Pt_Optimize", "Core", "DesignSpec.cs");
        string store = Src("Pt_Optimize", "Core", "DesignSpecStore.cs");
        string sample = Src("Pt_Optimize.Tests", "DesignSpecStoreTests.cs");
        string page = Src("Pt_Optimize", "UI", "LineDesignPage.cs");

        // ★★★★★ **④界面 的欠账表**（2026-09-09，R12/R13）：这五个逐片字段由 Core 侧（本次不许改 UI 的开发）加进来，
        //   ①②③⑥ 都已接，**④界面 由主开发者接**（钩子列在交接报告：只读显示「槽心角／孔心／形状」，
        //   PageToDesignSpec 读页面成设计时要把它们显式写回 NaN/0 而不是从种子静默继承）。
        //   与 BranchMarksAreReachedTests 的欠账表同一规矩：欠账要写明理由；**接上了还挂在表上 ⇒ 红**（反向断言），
        //   免得这张表变成藏东西的地方。
        // 2026-09-09 主开发者把五个字段接上界面（只读显示 + PageToDesignSpec 显式写 + 解完/载入回填）⇒ 欠账表清空
        var uiDebt = new System.Collections.Generic.Dictionary<string, string>();
        var missing = new System.Collections.Generic.List<string>();
        var stale = new System.Collections.Generic.List<string>();
        foreach (string f in fields)
        {
            var gaps = new System.Collections.Generic.List<string>();
            if (!spec.Contains($"FitArr({f}", StringComparison.Ordinal)
             && !spec.Contains($"{f}  = FitArr", StringComparison.Ordinal)
             && !spec.Contains($"{f} = FitArr", StringComparison.Ordinal)) gaps.Add("①Fit");
            if (!store.Contains(f, StringComparison.Ordinal)) gaps.Add("②存档");
            if (!sample.Contains("d." + f, StringComparison.Ordinal)) gaps.Add("③防过期样本");
            bool onPage = page.Contains(f, StringComparison.Ordinal);
            if (!onPage && !uiDebt.ContainsKey(f)) gaps.Add("④界面");
            if (onPage && uiDebt.ContainsKey(f)) stale.Add(f);
            if (gaps.Count > 0) missing.Add($"{f}：缺 {string.Join("／", gaps)}");
        }
        Assert.True(stale.Count == 0,
            "这些字段界面已经接上了，却还挂在 ④界面 的欠账表上 —— 把它们从欠账表删掉，否则这张表会变成藏东西的地方："
          + string.Join("、", stale));
        foreach (var kv in uiDebt)
            Assert.Contains(kv.Key, fields);          // 欠账表里的名字都得真的存在，否则打错字就等于偷偷放行
        Console.WriteLine($"④界面 欠账 {uiDebt.Count} 个：" + string.Join("；", uiDebt.Select(kv => kv.Key + "（" + kv.Value + "）")));

        Assert.True(missing.Count == 0,
            "这些逐片参数只接了一半 —— 少一处就是**静默丢失**："
          + Environment.NewLine + string.Join(Environment.NewLine, missing)
          + Environment.NewLine
          + "⇒ 六处清单见本文件摘要（Fit／存档／防过期样本／界面／变更清单／出图）。");
    }

    /// <summary>★ 自证：扫描真的认得出那些字段（否则上面恒绿）。</summary>
    [Fact]
    public void 自证_扫得到已知的逐片参数()
    {
        var f = PerPlateArrays();
        Assert.Contains("TabThickMm", f);
        Assert.Contains("TabInsulMm", f);
        Assert.Contains("SlotSpanDeg", f);
        Assert.Contains("TabHoleRMm", f);
    }
}
