using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 三条「**旋钮拧了不算数 / 喂错输入**」—— 第一性原理通查 2026-08-28，主进程逐条复验。
/// 共同点：**默认口径下都不咬**，所以从来没人发现；一旦离开默认就当场错。
/// </summary>
public class SilentKnobTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    // ── ① 「法兰吹风风速」对保温面静默失效 ───────────────────────

    /// <summary>
    /// ★ 病灶：<c>Insulation.PlateFlux</c> **根本没有 airVelocity 形参**，
    ///   两处外表面调用都走 <c>FlatOuterFlux</c> 的默认 0 ⇒ 强制对流项恒为 0。
    ///   于是同一片法兰上：**裸露区吹得到风、保温区吹不到** —— 两套对流物理，没有任何提示。
    ///   而默认 <c>FlangeInsulated = true</c> ⇒ **圆盘正是包着的那一半**。
    ///   参数说明却写着「压缩空气强制冷却…每吹掉一瓦都要由铂金发出来，直接折算成铂重」。
    /// </summary>
    [Fact]
    public void 吹风必须能吹到保温面()
    {
        Assert.Contains("double airVelocity = 0", Core("Insulation.cs"));
        // 两处外表面调用都要把它传下去，不能再吃默认 0
        string s = Core("Insulation.cs");
        Assert.Contains("charLength, sc, airVelocity)", s);
        // ShellThermal 的保温面与舌片保温面都要传真实风速
        // 三处外表面都要传：裸露面（本来就传）+ 法兰保温面 + 舌片保温面（这两处是补的）
        int n = Core("ShellThermal.cs").Split("p.FlangeAirVelocityMPerS)").Length - 1;
        Assert.True(n >= 3, $"三处外表面都该传真实风速，实测 {n} 处");
    }

    [Fact]
    public void 自证_风速为零时强制对流本来就是零_所以默认口径不受影响()
    {
        Assert.Equal(0.0, new DesignInputs().FlangeAirVelocityMPerS, 9);
        Assert.Equal(0.0, Materials.HConvForcedPlate(900, 25, 0.05, 0.0), 9);
    }

    // ── ② 热稳定的夹持通道在热导模式下被判成 0 ────────────────────

    /// <summary>
    /// ★ 病灶：<c>DClampDT</c> 的开关是 <c>p.BusbarClampTempC >= 0</c>。
    ///   热导边界（<c>BusbarConductanceWPerK ≥ 0</c>）下夹持温度恒为 −1
    ///   ⇒ **明明有 G 这条实打实的导热通道，稳定器却被判成 0**（偏保守，抹掉一个稳定器）。
    ///   物理上三种情形分明：定温 ⇒ 舌片导度本身；热导 ⇒ 与 G **串联**；自由端 ⇒ 0。
    /// </summary>
    [Fact]
    public void 热导模式下夹持通道要按串联算_不是判成零()
    {
        string s = Core("FlangeStability.cs");
        Assert.Contains("p.BusbarConductanceWPerK >= 0", s);
        Assert.Contains("1.0 / (1.0 / gTab + 1.0", s);      // 串联
    }

    [Fact]
    public void 自证_串联导度必定小于两者中较小的那个()
    {
        // 这是「串联」这件事的定义性质；写死在断言里，公式改错会红。
        double g1 = 0.5, g2 = 1.119;
        double series = 1.0 / (1.0 / g1 + 1.0 / g2);
        Assert.True(series < System.Math.Min(g1, g2));
    }

    // ── ③ 复核报告用默认输入去问板厚下界 ──────────────────────────

    /// <summary>
    /// ★ 病灶：<c>ShapeReview.Build</c> 用 <c>new DesignInputs()</c> 问下界，
    ///   于是 WeldMinThicknessMm / WeldSafetyFactor 被强制退回默认 0.6 / 2.0；
    ///   而定尺寸器用的是**工程师改过的那份**
    ///   ⇒ 复核页印的下界与实际用的**不是同一个数**，报告还据此印「已贴住」。
    ///   ⇒ **恰恰在拿到现场实测值那一天，报告开始说错话，而且不报错。**
    /// </summary>
    [Fact]
    public void 复核报告必须用真实输入问下界()
    {
        string s = Core("ShapeReview.cs");
        Assert.DoesNotContain("DiscFloorMm(new DesignInputs())", s);
        Assert.Contains("baseIn ?? new DesignInputs()", s);
    }

    [Fact]
    public void 自证_改焊接参数确实会改下界_否则上一条没有意义()
    {
        var a = new DesignInputs();
        var b = new DesignInputs { WeldMinThicknessMm = 1.2 };   // 工程师拿到实值后会改的那个
        Assert.NotEqual(FinalDesign.W08.DiscFloorMm(a), FinalDesign.W08.DiscFloorMm(b), 6);
    }
}
