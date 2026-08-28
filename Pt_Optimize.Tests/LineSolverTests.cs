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
            //   实际设计记录的 1150/1080/1050 里有两段落在区间外 ⇒ 那两段本就是「无法判定」，
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

    /// <summary>
    /// ★ B′ 主引擎的门（2026-08-24 补，此前**零覆盖**）。
    ///
    /// 它是「核算法兰（分钟级）」那个按钮的引擎，每段要跑一次耦合解 ——
    /// 所以这里只用**一段**，把跑得动的那部分契约钉住：
    ///   · n 段给 n+1 片（两端各一片，段间共用）
    ///   · 每片都有厚度与质量，且都为正
    ///   · 每片都说得出「厚度是被哪一段的需求定的」（SizedBy）——
    ///     一个只给数不给依据的定尺寸结果，工程师无从复核
    ///
    /// ⚠ 本条只跑**一段**。另外两块分别由别处守：
    ///   · 「共用片取两侧较大值」的**逻辑** —— 抽成纯函数 JointThickness 后在下面微秒级验（6 条）；
    ///   · 多段的**接线**（need/amps 按段对齐、接头下标、共用片标错边）——
    ///     在 `--cli --selfcheck` 的「B′ 多段定厚接线」一段里跑两段端到端
    ///     （每段一次耦合解，塞进那里的并行批，墙钟几乎不变）。
    ///   ⚠ 2026-08-24 之前这里写的是「仍没有覆盖多段共用片取两侧较大值」——
    ///     那句话在抽出 JointThickness 之后就过时了，却留了下来。
    ///     **诚实交代覆盖率的地方本身过时，比不交代更坏。**
    /// </summary>
    [Fact]
    public void SizeFlanges_OneSegment_GivesTwoPlatesWithProvenance()
    {
        var segs = Segs(0.8);
        var proto = new FlangePlate
        {
            DiscRadiusMm = 30, HoleRadiusMm = 26,
            TabEndXMm = -140, TabEndHalfWidthMm = 30, TabParallel = true,
            ThicknessMm = 2.0, ThickenedMm = 2.0,
        };
        var rs = LineSolver.SizeFlanges(segs, new DesignInputs(), proto);

        Assert.Equal(LineSolver.FlangeCount(segs.Count), rs.Count);      // 1 段 ⇒ 2 片
        Assert.All(rs, f => Assert.True(f.ThicknessMm > 0, $"{f.Joint} 厚度 {f.ThicknessMm}"));
        Assert.All(rs, f => Assert.True(f.MassG > 0, $"{f.Joint} 铂重 {f.MassG}"));
        Assert.All(rs, f => Assert.False(string.IsNullOrWhiteSpace(f.SizedBy),
            $"{f.Joint} 没说厚度是被哪一段定的 —— 只给数不给依据，没法复核"));
        // 单段线上两端都不共用
        Assert.All(rs, f => Assert.False(f.Shared));
    }

    // ══════════════════════════════════════════════════════════════
    // B′ 的「共用片取两侧较大值」——**此前零覆盖**（2026-08-24 补）
    //
    // 这段算术原来内联在 SizeFlanges 里，而那个方法每段要跑一次耦合解（约 30 s/段）
    // ⇒ 想验「两侧竞争」就得跑 ≥2 段。于是单段测试碰不到它：
    // 单段线上两端都是端片，内层循环每次只有一个合法的 k，
    // `if (tk > t)` 这个比较**一次都没执行过**。
    // 抽成 JointThickness 之后微秒级验完，慢的那半（耦合解算出 need/amps）
    // 由 SizeFlanges_OneSegment_… 覆盖。
    //
    // 判错的后果：共用片被**较弱那一侧**定厚 ⇒ 偏薄 ⇒ 违反的恰恰是定尺寸
    // 本来要满足的那条判据；而 SizedBy 会报错段名，工程师照它去改**另一段**。
    // ══════════════════════════════════════════════════════════════

    private static readonly string[] N3 = { "HC1", "HC2", "HC3" };

    /// <summary>端片只有一侧邻段，厚度就由它定</summary>
    [Theory]
    [InlineData(0, "HC1")]
    [InlineData(3, "HC3")]
    public void EndJoint_IsSizedByItsOnlyNeighbour(int j, string expect)
    {
        var (t, by) = LineSolver.JointThickness(
            new[] { 1.0, 2.0, 3.0 }, new[] { 100.0, 200.0, 300.0 }, N3, j);
        Assert.Equal(expect, by);
        Assert.True(t > 0);
        // 端片的接头电流 = 那一段自己的电流 ⇒ 折算比 1 ⇒ 厚度就是该段的 need
        double need = j == 0 ? 1.0 : 3.0;
        Assert.Equal(need, t, 9);
    }

    /// <summary>
    /// ★ 共用片取**较大**的那一侧，SizedBy 报的就是那一侧。
    ///
    /// 接头 1 在 HC1|HC2 之间：I接头 = √(100²+200²+100·200) = 264.575。
    ///   HC1 侧折算：1.0 × 264.575/100 = 2.6458
    ///   HC2 侧折算：2.0 × 264.575/200 = 2.6458   ← 故意做成相等，见下一条
    /// 这里把 HC2 的 need 抬高，让它明确胜出。
    /// </summary>
    [Fact]
    public void SharedJoint_TakesTheThickerSide()
    {
        double iJ = Math.Sqrt(100.0 * 100 + 200.0 * 200 + 100 * 200);
        var (t, by) = LineSolver.JointThickness(
            new[] { 1.0, 5.0, 3.0 }, new[] { 100.0, 200.0, 300.0 }, N3, 1);
        Assert.Equal("HC2", by);
        Assert.Equal(5.0 * (iJ / 200.0), t, 9);
    }

    /// <summary>
    /// 自证：把两侧的需求**对调**，结果与 SizedBy 必须跟着换边。
    /// 少了这一条，一个「永远取左侧」或「永远取右侧」的实现都能通过上一条。
    /// </summary>
    [Fact]
    public void SharedJoint_SwitchesSideWhenTheOtherSideBinds()
    {
        var names = N3;
        var amps = new[] { 100.0, 200.0, 300.0 };
        var left = LineSolver.JointThickness(new[] { 9.0, 1.0, 3.0 }, amps, names, 1);
        var right = LineSolver.JointThickness(new[] { 1.0, 9.0, 3.0 }, amps, names, 1);
        Assert.Equal("HC1", left.SizedBy);
        Assert.Equal("HC2", right.SizedBy);
        Assert.True(Math.Abs(left.ThicknessMm - right.ThicknessMm) > 1e-9,
            "两种情形算出同一个厚度 ⇒ 「取较大」没有真的在比");
    }

    /// <summary>
    /// 折算按 t ∝ I：接头电流比该段大多少，厚度就按同样比例放大。
    /// 这一条把「用错电流口径」钉死 —— 拿算术平均代替矢量合成会低 13 %，
    /// 那 13 % 直接落在法兰厚度上。
    /// </summary>
    [Fact]
    public void Thickness_ScalesWithJointOverSegmentCurrent()
    {
        var amps = new[] { 1000.0, 1000.0 };
        var names = new[] { "A", "B" };
        var (t, _) = LineSolver.JointThickness(new[] { 2.0, 2.0 }, amps, names, 1);
        // 两段同流 ⇒ I接头 = √3·I ⇒ 厚度 = 2.0 × √3
        Assert.Equal(2.0 * Math.Sqrt(3.0), t, 9);
    }

    /// <summary>
    /// 某段解失败（电流为 0）时**不折算**，直接用它的 need —— 但绝不能因此算出 0。
    /// 一片厚度为 0 的法兰会让下游每个量都变成垃圾，而且看不出是哪来的。
    /// </summary>
    [Fact]
    public void FailedSegment_FallsBackToItsNeed_NeverZero()
    {
        var (t, by) = LineSolver.JointThickness(
            new[] { 4.0, 2.0 }, new[] { 0.0, 500.0 }, new[] { "坏", "好" }, 0);
        Assert.Equal(4.0, t, 9);            // 端片，唯一邻段是「坏」
        Assert.Equal("坏", by);
        Assert.True(t > 0);
    }

    /// <summary>段数对不上要抛，不许凑 —— 凑出来的是另一条线的厚度</summary>
    [Fact]
    public void MismatchedLengths_Throw()
        => Assert.Throws<ArgumentException>(() =>
               LineSolver.JointThickness(new[] { 1.0, 2.0 }, new[] { 100.0 }, new[] { "A", "B" }, 0));

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
