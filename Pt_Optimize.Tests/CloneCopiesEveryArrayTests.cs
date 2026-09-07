using System;
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
    [Fact]
    public void 每一个数组字段都必须是新实例()
    {
        var src = DesignSpec.Builtin[0].Clone();     // 别动 static 本体
        var dst = src.Clone();

        var shared = typeof(DesignSpec)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType.IsArray)
            .Where(f => ReferenceEquals(f.GetValue(src), f.GetValue(dst)))
            .Where(f => f.GetValue(src) is Array a && a.Length > 0)   // 空数组共享无害
            .Select(f => f.Name)
            .ToList();

        Assert.True(shared.Count == 0,
            "这些数组字段 Clone 之后**仍是同一个实例**：" + string.Join("、", shared)
          + "。改副本会就地改掉原件（含 DesignSpec.Builtin 那几个 static 档），"
          + "污染是静默的、跨用例的。请在 DesignSpec.Clone 里补上逐份拷贝。");
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
