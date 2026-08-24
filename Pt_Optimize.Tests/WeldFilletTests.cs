using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 角焊缝的形状式子 —— **全程序只准有一份**。
///
/// 此前它有三份：`FlangePlate.ThicknessAt`、`ManualPage` 的剖面图、
/// `Pt_Optimize.Geom` 的回转体。三处靠注释互相宣称「同一式子」，**没有任何东西在验**。
/// 而它已经漂过一次：剖面图当时**根本没画焊缝** ⇒
/// 「图 / 所交付的件 / FE 实际算的厚度」三者不一致 —— 而管孔边正是它最厚的地方。
///
/// 2026-08-24 收敛：ManualPage 改为直接调 <see cref="FlangePlate.WeldFilletHeightMm"/>。
/// Geom 那一份**合不了**（net7、独立进程、不引用 Core），由 `--make3dm` 的
/// 解析积分体积对账守着（差 &gt; 0.5 % 即判不吻合）—— 那是有据的安排，不是遗漏。
/// </summary>
public class WeldFilletTests
{
    private const double A = 0.8;      // 焊脚

    /// <summary>贴管壁处堆到满焊脚高，到焊脚外缘收成 0 —— 这两点定死了这条弧。</summary>
    [Fact]
    public void Fillet_IsFullAtTheWall_AndZeroAtTheToe()
    {
        Assert.Equal(A, FlangePlate.WeldFilletHeightMm(0, A), 12);
        Assert.Equal(0, FlangePlate.WeldFilletHeightMm(A, A), 12);
        Assert.Equal(0, FlangePlate.WeldFilletHeightMm(-1e-9, A), 12);   // 孔内不堆
        Assert.Equal(0, FlangePlate.WeldFilletHeightMm(A + 1, A), 12);   // 焊脚外不堆
        Assert.Equal(0, FlangePlate.WeldFilletHeightMm(0.3, 0), 12);     // 没有焊缝时恒 0
    }

    /// <summary>它必须是**凹**圆弧：(d−a)² + (hw−a)² = a²。写错成凸弧会多算金属。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.4)]
    [InlineData(0.79)]
    public void Fillet_LiesExactlyOnTheConcaveArc(double d)
    {
        double hw = FlangePlate.WeldFilletHeightMm(d, A);
        Assert.Equal(A * A, (d - A) * (d - A) + (hw - A) * (hw - A), 9);
        // 自证：这一段上它确实不是 0，否则上面那条恒等式恒成立（0 与 a 都在弧上）
        if (d > 1e-6 && d < A - 1e-6) Assert.True(hw > 0 && hw < A, $"hw({d}) = {hw}");
    }

    /// <summary>从管壁往外单调减 —— 反了就成了「越远越厚」。</summary>
    [Fact]
    public void Fillet_DecreasesAwayFromTheWall()
    {
        double prev = double.MaxValue;
        for (int i = 0; i <= 20; i++)
        {
            double hw = FlangePlate.WeldFilletHeightMm(i * A / 20.0, A);
            Assert.True(hw <= prev + 1e-12, $"d={i * A / 20.0} 处回升到 {hw}（上一个 {prev}）");
            prev = hw;
        }
        Assert.True(prev < 1e-12, "末端没收到 0");
    }

    /// <summary>板厚里的焊缝是**两面各一个** ⇒ 孔边总厚 = 分区厚 + 2a。</summary>
    [Fact]
    public void ThicknessAtHoleEdge_AddsBothFaces()
    {
        var g = new FlangePlate
        {
            DiscRadiusMm = 30, HoleRadiusMm = 25.8,
            TabEndXMm = -140, TabEndHalfWidthMm = 30, TabParallel = true,
            ThicknessMm = 1.2, ThickenedMm = 1.2, TabThicknessMm = 1.2,
            WeldFilletLegMm = A,
        };
        double atHole = g.ThicknessAt(g.HoleRadiusMm, 0);
        double farOut = g.ThicknessAt(g.HoleRadiusMm + 2 * A, 0);

        Assert.Equal(1.2 + 2 * A, atHole, 9);
        Assert.Equal(1.2, farOut, 9);
        // 自证：焊缝确实改变了厚度，否则上面两条在「焊缝被忽略」时也会各自成立
        Assert.True(atHole - farOut > 1e-6, "焊缝对板厚毫无影响 —— 这条断言在空转");
    }

    /// <summary>
    /// 结构性：ManualPage **不许再抄一份**。
    /// 它与 Core 同一个组件，本来就该直接调；再抄一份就是把已经修好的那次漂开重新种回去。
    /// </summary>
    [Fact]
    public void ManualPage_CallsTheSharedFormula_NotItsOwnCopy()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        string? src = null;
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "Pt_Optimize", "UI", "ManualPage.cs");
            if (File.Exists(p)) { src = File.ReadAllText(p); break; }
        }
        Assert.True(src is not null, "找不到 ManualPage.cs —— 断言失去了对象，不能算通过");
        Assert.True(src!.Length > 10000, $"ManualPage.cs 只读到 {src.Length} 字元 —— 多半读错档了");
        Assert.Contains("WeldFilletHeightMm", src, StringComparison.Ordinal);
    }
}
