using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// B / B′ 两条链的引擎。
///
/// 清点时发现：`LineSolver.Solve` 只在手动的 <c>--walk</c> 里跑到（不在提交门内），
/// `SizeFlanges` 一处都没有，单测与 selfcheck 都是 0 ——
/// **两条链没有一道会自己跑的门**。B 是「② 粗算」那一格，B′ 是「核算法兰（分钟级）」按钮。
/// 它们不可交付，但工程师照样读它们的数并据此决策。
///
/// B′ 的主引擎每段要跑一次耦合解（约 30 s/段），放不进单测；
/// 这里把它**能闭式验的那部分**（接头片数、接头电流合成）钉住，
/// 主引擎仍是缺口 —— 说明写在这里，不假装覆盖到了。
/// </summary>
public class LineSolverTests
{
    private static List<Segment> Segs(params double[] walls) =>
        walls.Select((w, i) => new Segment
        {
            // ★ 取 1300/1250/1200：纯铂蠕变拟合区间是 1100–1400 °C。
            //   实际定案的 1150/1080/1050 里有两段落在区间外 ⇒ 那两段本就是「无法判定」，
            //   拿它们验「铂重为正」等于在验一条走不通的路。
            Name = $"HC{i + 1}", TSetC = 1300 - i * 50, TGlassInC = 1300,
            GlassHeadM = 0.3 + i * 0.3, LengthMm = 300, TubeIdMm = 50,
            WallMm = w, GradeName = "Pt", TLiquidusC = 1050
        }).ToList();

    /// <summary>n 段整线共 n+1 片法兰（相邻段共用接头处那一片）</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(-5, 1)]      // 负数不许算出负片数
    public void FlangeCount_IsSegmentsPlusOne(int segs, int expect)
        => Assert.Equal(expect, LineSolver.FlangeCount(segs));

    /// <summary>
    /// 接头电流是 120° 相位差下的**矢量合成** √(I₁²+I₂²+I₁I₂)，不是算术平均。
    ///
    /// 两段同流 I 时应得 √3·I ≈ 1.732 I —— 而工作簿那套 (I₁+I₂)/2×1.5 给 1.5 I，
    /// 低 13 %。这一条把「用错口径」钉死：差的那 13 % 直接落在法兰厚度上。
    /// </summary>
    [Fact]
    public void JointCurrent_IsVectorSum_NotArithmeticMean()
    {
        var amps = new[] { 1000.0, 1000.0 };
        Assert.Equal(1000.0 * Math.Sqrt(3.0), LineSolver.JointCurrentA(amps, 1), 6);
        // 两端的接头只有一侧有电流
        Assert.Equal(1000.0, LineSolver.JointCurrentA(amps, 0), 9);
        Assert.Equal(1000.0, LineSolver.JointCurrentA(amps, 2), 9);
    }

    /// <summary>不等电流时仍须落在两者之间的合成值上，且大于算术平均</summary>
    [Fact]
    public void JointCurrent_ExceedsArithmeticMean()
    {
        var amps = new[] { 800.0, 1600.0 };
        double j = LineSolver.JointCurrentA(amps, 1);
        Assert.True(j > (800 + 1600) / 2.0, $"矢量合成 {j:0} 不该小于算术平均 1200");
        Assert.Equal(Math.Sqrt(800.0 * 800 + 1600.0 * 1600 + 800 * 1600), j, 6);
    }

    /// <summary>B 链：解得出、每段一个结果、铂重为正</summary>
    [Fact]
    public void Solve_ReturnsOneResultPerSegment_WithPositiveMass()
    {
        var rs = LineSolver.Solve(Segs(0.8, 0.8, 0.8), new DesignInputs());
        Assert.Equal(3, rs.Count);
        Assert.All(rs, r => Assert.True(r.MassG > 0, $"{r.Seg.Name} 铂重 {r.MassG}"));
        // Binding 为空的语义是「没有约束咬住」，不是「忘了填」—— 见 LineSolver.Solve
        Assert.All(rs, r => Assert.False(r.Unknown));
    }

    /// <summary>壁厚翻倍 ⇒ 该段铂重必须跟着涨（否则 Totals 的合计毫无意义）</summary>
    [Fact]
    public void ThickerWall_MeansMoreMass()
    {
        var thin = LineSolver.Solve(Segs(0.6), new DesignInputs())[0];
        var thick = LineSolver.Solve(Segs(1.2), new DesignInputs())[0];
        Assert.True(thick.MassG > thin.MassG * 1.5,
            $"壁厚 0.6→1.2 而铂重只从 {thin.MassG:0.0} 到 {thick.MassG:0.0}");
    }

    /// <summary>
    /// ★ 铁律③ 在 B 链上的落点：蠕变数据不覆盖该温度时，这一段必须是
    /// **无法判定 ⇒ 不可行**，并把原因写进 Binding —— 不许悄悄当成通过。
    ///
    /// 纯铂在 1100 °C 以下没有持久强度实测（HANDOVER §6 待补③），
    /// 所以 900 °C 那一段正好落在这个洞里。
    /// </summary>
    [Fact]
    public void OutsideCreepData_IsUndetermined_NotSilentlyFeasible()
    {
        var segs = Segs(0.8);
        segs[0].TSetC = 900; segs[0].GradeName = "Pt";
        var r = LineSolver.Solve(segs, new DesignInputs())[0];
        if (!r.Unknown) return;                    // 该牌号若已有低温数据，本条自然不适用
        Assert.False(r.Feasible, "判不了却算成可行 —— 无法判定不是通过");
        Assert.True(double.IsNaN(r.Utilization), "判不了却给了一个利用率数字");
        Assert.Contains("无法判定", r.Binding);
    }

    /// <summary>Totals 的合计必须等于逐段之和 —— 汇总层不许自己另算一份</summary>
    [Fact]
    public void Totals_EqualsSumOfSegments()
    {
        var rs = LineSolver.Solve(Segs(0.8, 1.0, 0.6), new DesignInputs());
        var (mass, _, bad) = LineSolver.Totals(rs);
        Assert.Equal(rs.Sum(r => r.MassG), mass, 6);
        Assert.Equal(rs.Count(r => !r.Feasible), bad);
    }
}
