using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **下角的熔化二分只许用布尔「熔/不熔」，绝不许用温度数值**（2026-09-08，督导第 16 封）。
///
/// 「抬板厚直到走出熔化区」要解场，而熔化区里的场正是 S1 说不可信的那些：
/// 6260 °C 是拟合外推的垃圾（拟合到 3392 °C 才反号）。但「有没有越过熔点」这个**判断**方向可信。
/// ⇒ 对布尔做二分合法，对数值做插值不合法。下一个人会顺手拿峰值温度去加速收敛 —— 这道门挡它。
///
/// ⚠ 这是**禁用词**式的门（被禁的标识符本身就是要求），不是拿实现文字当行为的代理。
///   扫的是 <c>Solver.MeltFloor</c> 的**方法体**（从签名到下一个成员），不含它上面的文档注释。
/// </summary>
public class MeltFloorTests
{
    private static string Body()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        int a = s.IndexOf(") MeltFloor(", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 Solver.MeltFloor 的签名 —— 它改名了？本门要跟着改");
        int b1 = s.IndexOf("\n    /// <summary>", a, StringComparison.Ordinal);
        int b2 = s.IndexOf("\n    private static", a, StringComparison.Ordinal);
        int b3 = s.IndexOf("\n    public static", a, StringComparison.Ordinal);
        int b = new[] { b1, b2, b3 }.Where(x => x > a).DefaultIfEmpty(s.Length).Min();
        return s[a..b];
    }

    [Fact]
    public void 熔化二分只用布尔不用温度数值()
    {
        string body = Body();
        Assert.Contains(".OverMelt", body);               // 它读的必须是那个布尔
        foreach (var banned in new[] { "TMaxC", "TDiscMaxC", "TTabMaxC", "TTabEndC", "PtMeltC",
                                       "PlateSlack", "LocalStab", "QFromTubeW", "FlangeDip" })
            Assert.False(body.Contains(banned, StringComparison.Ordinal),
                $"MeltFloor 的方法体里出现了「{banned}」—— 熔化区里的温度／裕度数值是 S1 禁止引用的，"
              + "只许用「熔/不熔」这个布尔做二分。");
    }

    /// <summary>
    /// ★★★★★ 熔化是判断工具，不是旋钮（用户 2026-09-09）：熔了就停、板厚一位不动。
    /// 09-08 的版本在这里逐片二分抬板厚；按设定 J 的截面进下角后那条路不可达，处置改判断。
    /// 禁用词式的门：方法体里出现「给板厚赋值」「退回」「多遍」就是有人把旋钮加回来了。
    /// </summary>
    [Fact]
    public void 熔化只判不抬_方法体里不许给板厚赋值()
    {
        string body = Body();
        Assert.Contains("该解不存在", body);
        foreach (var banned in new[] { "TabThickMm[j] =", "TabThickMm[j]=", "TongueThickMm[j] =", "Restore(", "MaxPasses", "lo[j]", "hi[j]" })
            Assert.False(body.Contains(banned, StringComparison.Ordinal),
                $"MeltFloor 的方法体里出现了「{banned}」—— 熔化是判断工具不是旋钮（用户 2026-09-09），熔了就停、板厚一位不动");
    }

    [Fact]
    public void 下角不许读设计记录的板厚()
    {
        string body = Body();
        // 下角只能由约束推出：方法体里不许出现「读别的设计记录」的痕迹
        foreach (var banned in new[] { "Builtin", "DesignSpecStore", "LoadAll", "geometry." })
            Assert.False(body.Contains(banned, StringComparison.Ordinal),
                $"MeltFloor 里出现了「{banned}」—— 下角只能由约束算出来，不许读设计记录里的板厚（那就是种子）");
    }
}
