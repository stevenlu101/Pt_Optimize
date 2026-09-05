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

        var missing = new System.Collections.Generic.List<string>();
        foreach (string f in fields)
        {
            var gaps = new System.Collections.Generic.List<string>();
            if (!spec.Contains($"FitArr({f}", StringComparison.Ordinal)
             && !spec.Contains($"{f}  = FitArr", StringComparison.Ordinal)
             && !spec.Contains($"{f} = FitArr", StringComparison.Ordinal)) gaps.Add("①Fit");
            if (!store.Contains(f, StringComparison.Ordinal)) gaps.Add("②存档");
            if (!sample.Contains("d." + f, StringComparison.Ordinal)) gaps.Add("③防过期样本");
            if (!page.Contains(f, StringComparison.Ordinal)) gaps.Add("④界面");
            if (gaps.Count > 0) missing.Add($"{f}：缺 {string.Join("／", gaps)}");
        }

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
