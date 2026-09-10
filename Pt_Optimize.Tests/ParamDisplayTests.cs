using System.ComponentModel;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ 审排版（2026-09-10，deliverable/界面审排版_2026-09-09.md 的清单）：参数表不许漏出英文代号、True/False、−1 这类内部值；
/// 分类名不许是给开发者看的口气。这里钉的是 PropertyGrid 真正用的 TypeConverter 与分类常量。
/// </summary>
public class ParamDisplayTests
{
    [Fact]
    public void 枚举按中文显示_中文与原名都认()
    {
        var c = TypeDescriptor.GetConverter(typeof(SupplyMode));
        Assert.Equal("单相交流（可控矽相控）", c.ConvertToString(SupplyMode.AcPhase));
        Assert.Equal(SupplyMode.Dc, c.ConvertFromString("直流（整流）"));
        Assert.Equal(SupplyMode.AcZeroCross, c.ConvertFromString("AcZeroCross"));
        var o = TypeDescriptor.GetConverter(typeof(PtOptimize.Core.Orientation));
        Assert.Equal("水平", o.ConvertToString(PtOptimize.Core.Orientation.Horizontal));
        Assert.Equal(PtOptimize.Core.Orientation.Vertical, o.ConvertFromString("垂直"));
    }

    [Fact]
    public void 布尔显示是否()
    {
        var c = new ChineseBoolConverter();
        Assert.Equal("是", c.ConvertToString(true));
        Assert.Equal("否", c.ConvertToString(false));
        Assert.Equal(true, c.ConvertFromString("是"));
        Assert.Equal(false, c.ConvertFromString("否"));
        // 参数表里的布尔属性都挂了这个转换器
        foreach (var p in typeof(DesignInputs).GetProperties())
            if (p.PropertyType == typeof(bool))
                Assert.True(p.GetCustomAttributes(typeof(TypeConverterAttribute), true).Length > 0, p.Name + " 没挂 是/否 转换器");
    }

    [Fact]
    public void 铜排总热导的负一显示成自动_填自动或留空就是负一()
    {
        var c = new AutoOrValueConverter();
        Assert.Equal("自动（程序自己算）", c.ConvertToString(-1.0));
        Assert.Equal("12.5", c.ConvertToString(12.5));
        Assert.Equal(-1.0, c.ConvertFromString("自动"));
        Assert.Equal(-1.0, c.ConvertFromString(""));
        Assert.Equal(12.5, c.ConvertFromString("12.5"));
        var p = typeof(DesignInputs).GetProperty(nameof(DesignInputs.BusbarConductanceWPerK))!;
        Assert.True(p.GetCustomAttributes(typeof(TypeConverterAttribute), true).Length > 0);
    }

    [Fact]
    public void 分类名不带开发者口气()
    {
        Assert.DoesNotContain("✗", ParamCat.页面接管);
        Assert.DoesNotContain("✗", ParamCat.程序算出);
        Assert.StartsWith("8 只读", ParamCat.页面接管);
        Assert.StartsWith("9 只读", ParamCat.程序算出);
    }
}
