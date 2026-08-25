using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 形状搜索的**种子**（2026-08-25）。
///
/// 病灶：`--shape` 里写死 `FinalDesign.W08.Clone()`，只覆盖 WallMm。
///  · `--wall 0.6` ⇒ 拿 **0.8 档的板厚分布**配 0.6 的管，而 ByWall(0.6) 一直在那儿没人接；
///  · 输出里**一个字都没提种子是谁** ⇒ 拿到那份控制台输出的人还原不出「这是用什么算的」。
///
/// 为什么种子要紧：Sizer 的 D8 主循环是在种子板厚上**做增量**（每轮至多 ThickStepMm，
/// 且连坏 3 轮就停），**不重新初始化**。它有多要紧要看形状：
/// 被工艺下界主导时终态与种子无关（实测盘Ø120 从 0.73 一路顶到下界 4.71），
/// 有裕度可省时就与出发点有关。⇒ 结论只到「它是输入，必须申报」为止。
/// </summary>
public class ShapeSeedTests
{
    private static FinalDesign[] Archives => FinalDesign.Builtin;

    // ── 挑档 ────────────────────────────────────────────────────

    /// <summary>★ 本案的正主：0.6 必须挑到 0.6 的档，不是 0.8 的。</summary>
    [Fact]
    public void PicksArchiveMatchingTheWall_NotAlwaysW08()
    {
        var c = ShapeSeed.Choose(0.6, null, null, Archives, FinalDesign.W08);
        Assert.False(c.WallMismatch);
        Assert.Equal(FinalDesign.W06.TabThickMm, c.Seed.TabThickMm);
        // 自证：两档的板厚确实不同，否则上一条恒真
        Assert.NotEqual(FinalDesign.W06.TabThickMm[1], FinalDesign.W08.TabThickMm[1]);
    }

    [Fact]
    public void PicksW08ForTheEightWall()
    {
        var c = ShapeSeed.Choose(0.8, null, null, Archives, FinalDesign.W06);
        Assert.False(c.WallMismatch);
        Assert.Equal(FinalDesign.W08.TabThickMm, c.Seed.TabThickMm);
    }

    /// <summary>挑不到同壁厚的档：可以回退，但**必须出声**。</summary>
    [Fact]
    public void NoArchiveForThisWall_FallsBackButSaysSo()
    {
        var c = ShapeSeed.Choose(1.0, null, null, Archives, FinalDesign.W08);
        Assert.True(c.WallMismatch);
        Assert.Contains("另一档", c.Note);
        Assert.Contains("1.0", c.Note);
        Assert.Equal(1.0, c.Seed.WallMm, 9);   // 要算的壁厚仍然是 1.0
    }

    // ── 申报 ────────────────────────────────────────────────────

    /// <summary>Note 永不为空 —— 调用方没得选，只能印。</summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void NoteIsNeverEmpty(double wall)
    {
        var c = ShapeSeed.Choose(wall, null, null, Archives, FinalDesign.W08);
        Assert.False(string.IsNullOrWhiteSpace(c.Note));
        Assert.Contains("种子", c.Note);
    }

    /// <summary>申报里要有**板厚起点**：那才是会改变答案的那个东西。</summary>
    [Fact]
    public void NoteCarriesTheStartingThickness()
    {
        var c = ShapeSeed.Choose(0.8, null, null, Archives, FinalDesign.W08);
        Assert.Contains(FinalDesign.W08.TabThickMm[1].ToString("0.00"), c.Note);
        Assert.Contains("增量", c.Note);       // 说明它为什么要紧
    }

    // ── 指定档名 ────────────────────────────────────────────────

    [Fact]
    public void SeedByName_Works()
    {
        var c = ShapeSeed.Choose(0.8, FinalDesign.W06.Name, null, Archives, FinalDesign.W08);
        Assert.Equal(FinalDesign.W06.TabThickMm, c.Seed.TabThickMm);
        Assert.True(c.WallMismatch);           // 拿 0.6 的档算 0.8 的管 ⇒ 要出声
    }

    /// <summary>认不出的档名 **抛**，并列出可选 —— 不静默回退（照 FinalDesign.Select 的规矩）。</summary>
    [Fact]
    public void UnknownSeedName_ThrowsAndListsOptions()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ShapeSeed.Choose(0.8, "没有这个档", null, Archives, FinalDesign.W08));
        Assert.Contains("没有这个档", ex.Message);
        Assert.Contains(FinalDesign.W08.Name, ex.Message);
        Assert.Contains(FinalDesign.W06.Name, ex.Message);
    }

    // ── 压平（复现用：从等厚板出发） ─────────────────────────────

    /// <summary>Pt_Heater1 那条路的起点是**等厚**板 —— 没有这个开关就复现不了。</summary>
    [Fact]
    public void SeedFlat_MakesUniformPlate()
    {
        var c = ShapeSeed.Choose(0.8, null, 2.0, Archives, FinalDesign.W08);
        Assert.True(c.Flattened);
        Assert.All(c.Seed.TabThickMm, t => Assert.Equal(2.0, t, 9));
        Assert.Contains("压平", c.Note);
        // 自证：定案本身不是等厚的，否则这条恒真
        Assert.True(FinalDesign.W08.TabThickMm.Distinct().Count() > 1);
    }

    [Fact]
    public void SeedFlat_RejectsNonPositive()
    {
        Assert.Throws<ArgumentException>(
            () => ShapeSeed.Choose(0.8, null, 0.0, Archives, FinalDesign.W08));
    }

    // ── 不许污染静态档 ──────────────────────────────────────────

    /// <summary>
    /// ★ 老坑重演防线：Clone 的注释里写着「扰动必须作用在副本上，否则 static 实例被就地改掉」。
    /// 种子被改形状、改壁厚、压平之后，两个定案 static 必须纹丝不动。
    /// </summary>
    [Fact]
    public void NeverMutatesTheStaticArchives()
    {
        double[] before08 = (double[])FinalDesign.W08.TabThickMm.Clone();
        double[] before06 = (double[])FinalDesign.W06.TabThickMm.Clone();
        double wallBefore = FinalDesign.W08.WallMm;

        var c = ShapeSeed.Choose(0.8, null, 3.14, Archives, FinalDesign.W08);
        c.Seed.TabThickMm[0] = 99;
        c.Seed.WallMm = 42;

        Assert.Equal(before08, FinalDesign.W08.TabThickMm);
        Assert.Equal(before06, FinalDesign.W06.TabThickMm);
        Assert.Equal(wallBefore, FinalDesign.W08.WallMm, 9);
    }

    /// <summary>新解不继承旧档的失效告示（否则一个新形状会挂着别人的「已作废」）。</summary>
    [Fact]
    public void FreshSeedCarriesNoInvalidNotice()
    {
        var c = ShapeSeed.Choose(FinalDesign.Retired08.WallMm, FinalDesign.Retired08.Name,
                                 null, Archives, FinalDesign.W08);
        Assert.Equal("", c.Seed.Invalid);
        Assert.Empty(c.Seed.InvalidChecks);
        // 自证：源档确实带着失效告示，否则上面两条恒真
        Assert.False(string.IsNullOrEmpty(FinalDesign.Retired08.Invalid));
    }

    // ── 接上了没有（机制在、没接上等于没有） ─────────────────────

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "HANDOVER.md"))) return d.FullName;
        throw new DirectoryNotFoundException("往上找 8 层都没有仓根 —— 断言失去了对象");
    }

    /// <summary>
    /// `--shape` 必须**真的**用上它、并且**真的**把申报印出来。
    /// 只验「函数存在」是这个项目栽过的跟头：造好了没接线。
    /// </summary>
    [Fact]
    public void ShapeCliActuallyUsesTheSeedChooser_AndPrintsIt()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("ShapeSeed.Choose(", src);
        Assert.Contains("Console.WriteLine(seedPickS.Note)", src);
        // ⚠ 不用「老写法不许出现」来验 —— 那要靠缩进与行尾去匹配，是「拿行号当引用」的变体。
        //   验**新写法确实在用**才稳：种子必须来自 Choose 的产物，不是某个写死的档。
        Assert.Contains("var seedS = seedPickS.Seed.Clone()", src);
    }
}
