using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **Clone 必须逐份拷贝每一个数组字段** —— 漏一个就是静默污染源
/// （2026-09-06 被出图对账门抓到）。
///
/// 现场：`SlotSpanDeg` / `TabHoleRMm` / `TabHoleAspect` 三个数组没进 Clone
/// ⇒ MemberwiseClone 按引用共享 ⇒ 一条测试把孔设成 R32.71×3，
/// **下一条测试拿「干净的 Builtin[0]」出图，法兰实心穿过铂金管** —— 而它自己一个孔都没设过。
///
/// ⚠ 本仓库为这个形状栽过（Clone 上方 --vary 那段注释白纸黑字写着
///   「否则 W08/W06 这两个 static 实例会被就地改掉」）。同一个坑，新字段又踩一次。
/// ⇒ 本门用**反射**，不列名单：以后任何新加的数组字段漏拷都会当场红。
/// </summary>
public class CloneCopiesEveryArrayTests
{
    /// <summary>
    /// ★★★★★ 扫**每一个** Builtin 档，不是只扫第一个。
    ///
    /// ⚠ 第一版只拿 Builtin[0]（W08）当样本，而且把「空数组」直接从检查里剔掉：
    /// <code>
    ///   .Where(f => f.GetValue(src) is Array a &amp;&amp; a.Length &gt; 0)   // 空数组共享无害
    /// </code>
    /// 两条叠在一起就出了洞：<c>InvalidChecks</c> 在 W08 上恰好是空的 ⇒ 被跳过，
    /// 而 Retired08/Retired06 上是 <c>{ "⑤" }</c> ⇒ 真共享。**门看不见这一类**
    /// （2026-09-07 督导 ② 抓到）。这与 UiWiring §17′「空集恒真」是同一族：
    /// 判据在**恰好为空**的样本上恒过。
    ///
    /// ⇒ 两条一起改：
    ///   · 样本换成全部 Builtin 档 —— 一个档上为空的，别的档上未必
    ///   · 空数组不再**静默**跳过：照样列出来，只是在讯息里说明为什么可以放过
    ///     （零长数组写不进东西，共享确实无害）—— **报了但说明**，不是不报
    /// </summary>
    [Fact]
    public void 每一个数组字段都必须是新实例()
    {
        var shared = new List<string>();
        var harmless = new List<string>();

        for (int i = 0; i < DesignSpec.Builtin.Length; i++)
        {
            var src = DesignSpec.Builtin[i].Clone();     // 别动 static 本体
            var dst = src.Clone();
            foreach (var f in typeof(DesignSpec)
                        .GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.FieldType.IsArray))
            {
                if (!ReferenceEquals(f.GetValue(src), f.GetValue(dst))) continue;
                int len = (f.GetValue(src) as Array)?.Length ?? 0;
                string where = $"{DesignSpec.Builtin[i].Name}.{f.Name}";
                if (len > 0) shared.Add(where + $"（长度 {len}）");
                else harmless.Add(where);
            }
        }

        string note = harmless.Count == 0 ? ""
            : "（另有共享但**零长**的：" + string.Join("、", harmless.Distinct())
            + " —— 零长数组写不进东西，共享无害，列出来只为不让它悄悄溜过）";

        Assert.True(shared.Count == 0,
            "这些数组字段 Clone 之后**仍是同一个实例**：" + string.Join("、", shared)
          + "。改副本会就地改掉原件（含 DesignSpec.Builtin 那几个 static 档），"
          + "污染是静默的、跨用例的。请在 DesignSpec.Clone 里补上逐份拷贝。" + note);
    }

    /// <summary>★★★★ 反向实证：改副本，原件必须纹丝不动。</summary>
    [Fact]
    public void 改副本不许动到原件()
    {
        var a = DesignSpec.Builtin[0].Clone();
        var b = a.Clone();
        for (int j = 0; j < b.TabHoleRMm.Length; j++) b.TabHoleRMm[j] = 33.3;
        for (int j = 0; j < b.SlotSpanDeg.Length; j++) b.SlotSpanDeg[j] = 120;
        for (int j = 0; j < b.TabHoleAspect.Length; j++) b.TabHoleAspect[j] = 3.0;

        Assert.All(a.TabHoleRMm, v => Assert.NotEqual(33.3, v));
        Assert.All(a.SlotSpanDeg, v => Assert.NotEqual(120, v));
        Assert.All(a.TabHoleAspect, v => Assert.NotEqual(3.0, v));
    }
}
