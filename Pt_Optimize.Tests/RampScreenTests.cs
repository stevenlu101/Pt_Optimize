using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// D 升温闸（① 先决条件）的引擎。
///
/// 清点时发现：`RampScreen` 只有 <c>AnalysisPage</c> 一个调用方，
/// 单测 0、UiWiring 0、selfcheck 0 —— **整条链没有一道会自己跑的门**。
/// 它却是工程师看到的第一格判定，也是唯一一条能在跑分钟级整线解**之前**
/// 说「这个工作点根本没有稳态解」的判据。
/// </summary>
public class RampScreenTests
{
    private static DesignInputs Base() => new();

    /// <summary>
    /// ★ 实测：**加厚保温会略微降低**管侧热稳定裕度，不是提高。
    ///
    /// 裕度² ∝ β/Q（β = dQ/dT）。辐射主导时 β/Q ≈ 4/T；包上保温改成导热主导后
    /// β/Q ≈ 1/(T−T∞)，更小 ⇒ 裕度降。Judge 里原来写着「【下一步】加厚纤维保温
    /// （管侧的免费杠杆）」—— **是这条断言把它抓出来的**（写这条时我以为会变大）。
    /// </summary>
    [Fact]
    public void ThickerInsulation_LowersMarginAtDesignPoint()
    {
        var p = Base();
        var bare = RampScreen.Evaluate(p, 0.0, 0.8);
        var thick = RampScreen.Evaluate(p, 150.0, 0.8);
        Assert.True(thick.LossW < bare.LossW, "散热没随保温变小");
        Assert.True(thick.CurrentA < bare.CurrentA, "电流没随保温变小");
        Assert.True(thick.Margin < bare.Margin,
            $"设计点(1150 °C)上加保温该**降低**裕度：{thick.Margin:0.000} vs {bare.Margin:0.000}");
    }

    /// <summary>
    /// ★ 保温对裕度的作用**随目标温度变号** —— 所以「加保温」不能当成通用建议。
    /// 1150 °C：裸管更好；1400 °C：包保温更好。
    /// </summary>
    [Fact]
    public void InsulationEffect_FlipsSignWithTargetTemperature()
    {
        var p = Base();
        Assert.True(RampScreen.Evaluate(p, 20.0, 0.8, 1150).Margin
                  < RampScreen.Evaluate(p, 0.0, 0.8, 1150).Margin, "1150 °C 上包保温不该更好");
        Assert.True(RampScreen.Evaluate(p, 20.0, 0.8, 1400).Margin
                  > RampScreen.Evaluate(p, 0.0, 0.8, 1400).Margin, "1400 °C 上包保温该更好");
    }

    /// <summary>
    /// ★★ 这条判据在**整个现实包络**里都过得去（实测 1.43–1.97，限 1.0）——
    /// 也就是说它**不区分设计**，检的是材料与工作温度的组合。
    ///
    /// 把这个事实钉成断言，而不是让它继续以「一道会拦人的闸」的样子留在界面上：
    /// 哪天物性或口径改了、它真的开始接近 1，这条会先响。
    /// </summary>
    [Fact]
    public void MarginStaysFarAboveOne_AcrossTheWholeEnvelope()
    {
        var p = Base();
        double lo = double.PositiveInfinity, hi = 0;
        foreach (double t in new[] { 600.0, 800.0, 1000.0, 1150.0, 1300.0, 1400.0 })
            foreach (double w in new[] { 0.6, 0.8, 1.2 })
                foreach (double ins in new[] { 0.0, 2.0, 20.0, 150.0 })
                {
                    double m = RampScreen.Evaluate(p, ins, w, t).Margin;
                    lo = Math.Min(lo, m); hi = Math.Max(hi, m);
                }
        Assert.True(lo > 1.2 && hi < 2.2,
            $"实测包络 {lo:0.00}–{hi:0.00}，与记录的 1.43–1.97 不符 —— 物性或口径变了，回去看 Judge 的说明");
    }

    /// <summary>裕度就是 I_stab/I，不是另一个量 —— 三者必须自洽</summary>
    [Fact]
    public void Margin_IsExactlyIStabOverCurrent()
    {
        var pt = RampScreen.Evaluate(Base(), 20.0, 0.8);
        Assert.True(pt.CurrentA > 0 && pt.IStabA > 0);
        Assert.Equal(pt.IStabA / pt.CurrentA, pt.Margin, 9);
    }

    /// <summary>
    /// 判定文字与 Ok 只有一处阈值。三档都要走到 —— 只验中间那档等于没验分档。
    /// </summary>
    [Theory]
    [InlineData(3.0, "✓", true)]
    [InlineData(1.2, "⚠ 裕度薄", true)]
    [InlineData(0.5, "✗ 越热稳定极限", false)]
    public void Verdict_MatchesTheSingleThresholdPair(double margin, string text, bool ok)
    {
        var pt = new RampScreen.Point(20, 0.8, 0, 100, 0, 100 * margin, margin, 0);
        Assert.Equal(text, pt.Verdict);
        Assert.Equal(ok, pt.Ok);
    }

    /// <summary>
    /// ★ 快筛与整线解**共用 ① 这个名字**，门禁靠前缀找它。
    ///
    /// 两条判据同名而答的是不同问题（快筛给热稳定裕度 I_stab/I，整线解给小时数），
    /// 所以：① 前缀必须在（否则门禁找不到），② 名字必须能区分（否则人会把
    /// 「快筛过了」读成「① 判据过了」），③ 说明里必须写明谁为准。
    /// 这三条只要少一条，就是本项目最怕的那种「看起来正常的错」。
    /// </summary>
    [Fact]
    public void ScreenCriterion_IsFindableButNotConfusableWithTheRealOne()
    {
        var c = RampScreen.Judge(Base(), 20.0, 0.8);
        Assert.StartsWith(LineResult.Key.Ramp, c.Name, StringComparison.Ordinal);
        Assert.NotEqual(LineResult.Key.Ramp, c.Name);          // 必须带得出区别的后缀
        Assert.Contains("快筛", c.Name);
        Assert.Contains("以整线解为准", c.Note);
        Assert.Equal(RampScreen.MarginHardMin, c.Limit);
        Assert.False(c.LessIsBetter);                          // 裕度是越大越好
    }

    /// <summary>
    /// 自证：这套评估**确实会随输入变**。
    /// 少了这一条，一个「永远返回同一个 Point」的坏实现能让上面几条全过。
    /// </summary>
    [Fact]
    public void Evaluate_ActuallyDependsOnWallThickness()
    {
        var a = RampScreen.Evaluate(Base(), 20.0, 0.6);
        var b = RampScreen.Evaluate(Base(), 20.0, 1.2);
        Assert.True(Math.Abs(a.MassG - b.MassG) > 1e-6, "壁厚翻倍而铂重没变");
        Assert.True(Math.Abs(a.TubeJAPerMm2 - b.TubeJAPerMm2) > 1e-9, "壁厚翻倍而 J 没变");
    }
}
