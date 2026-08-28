using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 判据裕度的**方向**。
///
/// 这个公式曾在四处各写一遍（DesignSpec.Margin、ShapeReview 两处、整线设计页判据表），
/// 四处都写成 `(限−实)/|限|`、都没看 LessIsBetter ⇒ 对「须 ≥ 限」的判据符号是反的。
/// 现形处：⑤ 舌片自由段 114.8 / 100 是**通过且富余 14.8**，判据表却显示「超 15 %」——
/// 裕度列说越限、判定列打 ✓，同一行自相矛盾。
///
/// 设计记录上一直看不出来：舌长按「切点+压接+自由段下界」算出来，⑤ 恰好贴着 100 ⇒
/// 裕度 0 %，符号错不错都是 0。**bug 躲在这个巧合后面，直到 .3dm 那条路把它暴露。**
/// </summary>
public class MarginTests
{
    private static ConstraintOut C(double actual, double limit, bool lessIsBetter) =>
        new() { Actual = actual, Limit = limit, LessIsBetter = lessIsBetter };

    /// <summary>须 ≤ 限：实测比限小 ⇒ 正裕度</summary>
    [Theory]
    [InlineData(5.0, 10.0, 50.0)]      // ③ 温降 5 / 限 10 ⇒ 还有一半余量
    [InlineData(10.0, 10.0, 0.0)]      // 恰好贴着限
    [InlineData(12.0, 10.0, -20.0)]    // 越限 20 %
    public void LessIsBetter_PositiveWhenUnderLimit(double a, double lim, double expect)
        => Assert.Equal(expect, C(a, lim, true).MarginPct, 6);

    /// <summary>
    /// 须 ≥ 限：实测比限**大**才是安全 ⇒ 正裕度。这一组在旧写法下全部符号相反。
    /// </summary>
    [Theory]
    [InlineData(114.8, 100.0, 14.8)]   // ⑤ 自由段：正是现形的那一组
    [InlineData(100.0, 100.0, 0.0)]    // 设计记录恰好贴界 —— 旧 bug 就藏在这个 0 后面
    [InlineData(24.019, 100.0, -75.981)]  // 已作废档：真的不够
    public void GreaterIsBetter_PositiveWhenOverLimit(double a, double lim, double expect)
        => Assert.Equal(expect, C(a, lim, false).MarginPct, 6);

    /// <summary>
    /// 自证：**同一组数**，两个方向必须给相反的符号。
    /// 少了这一条，一个「永远算 (限−实)」的实现只要 LessIsBetter 那组对，就能蒙混过关。
    /// </summary>
    [Fact]
    public void TwoDirections_GiveOppositeSigns()
    {
        double a = 114.8, lim = 100.0;
        double le = C(a, lim, true).MarginPct;
        double ge = C(a, lim, false).MarginPct;
        Assert.True(le < 0 && ge > 0,
            $"须≤限得 {le:0.0} %、须≥限得 {ge:0.0} % —— 两个方向没有分开算");
        Assert.Equal(-le, ge, 9);
    }

    /// <summary>
    /// 方向性判据（限 = 0，如 ②′ 管孔净流入「须 > 0」）不适用百分比 ⇒ NaN。
    /// 不返回 NaN 就会算出 ±∞，而 ∞ 排到「优点」榜首看起来完全正常。
    /// </summary>
    [Theory]
    [InlineData(1.123, 0.0)]
    [InlineData(-591.0, 0.0)]
    public void ZeroLimit_IsNotAPercentage(double a, double lim)
        => Assert.True(double.IsNaN(C(a, lim, false).MarginPct));
}
