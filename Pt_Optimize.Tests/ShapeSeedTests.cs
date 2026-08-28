using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 形状搜索的**种子**（2026-08-25）。
///
/// 病灶：`--shape` 里写死 `DesignSpec.W08.Clone()`，只覆盖 WallMm。
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
    /// <summary>板厚起点 —— 现在是**必给**的（真实输入：--thick 或图纸）。</summary>
    private static readonly double[] Th = { 1.0, 2.0, 2.0, 1.0 };

    private static DesignSpec[] Archives => DesignSpec.Builtin;

    // ── 挑档 ────────────────────────────────────────────────────

    /// <summary>★ 本案的正主：0.6 必须挑到 0.6 的档，不是 0.8 的。</summary>
    [Fact]
    public void PicksArchiveMatchingTheWall_NotAlwaysW08()
    {
        var c = ShapeSeed.Choose(0.6, null, Th, Archives, DesignSpec.W08);
        Assert.False(c.WallMismatch);
        // ★ 2026-08-25 改判：**不再**验「板厚来自 W06」—— 板厚已是必给的真实输入，
        //   拿设计记录的板厚当起点正是被禁掉的做法。这里验的是**挑对了档**：
        //   档只用来供图纸与界面都给不出的构型常数（压接段/圆角/环宽/控温点）。
        Assert.Contains(DesignSpec.W06.Name, c.Note);
        Assert.Equal(0.6, c.Seed.WallMm, 9);
        Assert.Equal(Th, c.Seed.TabThickMm);            // 板厚来自入参，不是档
        // 自证：两档确实不同名，否则上一条恒真
        Assert.NotEqual(DesignSpec.W06.Name, DesignSpec.W08.Name);
    }

    [Fact]
    public void PicksW08ForTheEightWall()
    {
        var c = ShapeSeed.Choose(0.8, null, Th, Archives, DesignSpec.W06);
        Assert.False(c.WallMismatch);
        Assert.Contains(DesignSpec.W08.Name, c.Note);
        Assert.Equal(0.8, c.Seed.WallMm, 9);
    }

    /// <summary>挑不到同壁厚的档：可以回退，但**必须出声**。</summary>
    [Fact]
    public void NoArchiveForThisWall_FallsBackButSaysSo()
    {
        var c = ShapeSeed.Choose(1.0, null, Th, Archives, DesignSpec.W08);
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
        var c = ShapeSeed.Choose(wall, null, Th, Archives, DesignSpec.W08);
        Assert.False(string.IsNullOrWhiteSpace(c.Note));
        Assert.Contains("种子", c.Note);
    }

    /// <summary>申报里要有**板厚起点**：那才是会改变答案的那个东西。</summary>
    [Fact]
    public void NoteCarriesTheStartingThickness()
    {
        var c = ShapeSeed.Choose(0.8, null, Th, Archives, DesignSpec.W08);
        Assert.Contains(Th[1].ToString("0.00"), c.Note);   // 印的是**真实入参**的板厚，不是档里的
        Assert.Contains("增量", c.Note);       // 说明它为什么要紧
    }

    // ── 指设计记录名 ────────────────────────────────────────────────

    [Fact]
    public void SeedByName_Works()
    {
        var c = ShapeSeed.Choose(0.8, DesignSpec.W06.Name, Th, Archives, DesignSpec.W08);
        Assert.Contains(DesignSpec.W06.Name, c.Note);
        Assert.True(c.WallMismatch);           // 拿 0.6 的档供构型常数、算 0.8 的管 ⇒ 要出声
    }

    /// <summary>认不出的档名 **抛**，并列出可选 —— 不静默回退（照 DesignSpec.Select 的规矩）。</summary>
    [Fact]
    public void UnknownSeedName_ThrowsAndListsOptions()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ShapeSeed.Choose(0.8, "没有这个档", Th, Archives, DesignSpec.W08));
        Assert.Contains("没有这个档", ex.Message);
        Assert.Contains(DesignSpec.W08.Name, ex.Message);
        Assert.Contains(DesignSpec.W06.Name, ex.Message);
    }

    // ── 合成种子已禁用；换起点只准用**真实图纸** ──────────────────

    /// <summary>
    /// ★★ 用户 2026-08-25：「**不能再用所谓的中性种子（以后此方法禁用）**，
    ///    是要从 UI 或是 3DM(Pt_Heater1.3dm) 输入直接算。」
    ///
    /// 我此前加过 `--seedflat`，把板厚压平成一个自己捏的数当「中性起点」。它错在三处：
    ///  · 不对应任何真实工况 —— 既不是工程师会填的，也不是图纸上的；
    ///  · 只压平**板厚**，舌保温与环倍率仍来自设计记录 ⇒ 连「中性」都名不副实；
    ///  · 于是算出来的铂重与判据**看着正常却没有归属** —— 本项目最怕的那种错。
    /// </summary>
    [Fact]
    public void 合成种子已禁用_而且是当场抛不是悄悄忽略()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("--seedflat 已**禁用**", src);        // 拦下来了
        Assert.Contains("throw new ArgumentException", src);   // 而且是抛，不是静默忽略
        Assert.DoesNotContain("seedFlatS", src);               // 旧的解析变量不许还在
    }

    /// <summary>换起点唯一允许的做法：从 .3dm 图纸起算。</summary>
    [Fact]
    public void 从图纸起算_几何与板厚都来自图纸()
    {
        var sh = new PlateShapeAnalyzer.Shape
        {
            HoleRadiusMm = 26.0, DiscRadiusMm = 59.99,
            TabEndXMm = -199.5, TabEndHalfWidthMm = 40.0,
        };
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 2.0, AreaMm2 = 23566 });

        var c = ShapeSeed.FromDrawing(sh, null, Archives, DesignSpec.W08);
        Assert.True(c.FromDrawing);
        Assert.Equal(59.99, c.Seed.DiscRadiusMm, 2);
        Assert.Equal(199.5, c.Seed.TabLengthMm, 3);
        Assert.Equal(40.0, c.Seed.TabHalfWidthMm, 3);
        Assert.Equal(1.0, c.Seed.WallMm, 6);
        Assert.All(c.Seed.TabThickMm, t => Assert.Equal(2.0, t, 9));
        // 自证：设计记录本身不是这些数，否则上面几条恒真
        Assert.NotEqual(DesignSpec.W08.DiscRadiusMm, c.Seed.DiscRadiusMm, 2);
        Assert.True(DesignSpec.W08.TabThickMm.Distinct().Count() > 1);
    }

    /// <summary>
    /// 图纸给不了的那些（舌保温／环倍率／管保温／控温点／压接段）仍取自设计记录 ——
    /// **这件事必须写在申报里**：种子里有多少来自图纸、多少来自设计记录，读的人有权知道。
    /// </summary>
    [Fact]
    public void 从图纸起算_必须说清哪些来自图纸哪些来自设计记录()
    {
        var sh = new PlateShapeAnalyzer.Shape
        {
            HoleRadiusMm = 26.0, DiscRadiusMm = 59.99,
            TabEndXMm = -199.5, TabEndHalfWidthMm = 40.0,
        };
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 2.0, AreaMm2 = 23566 });
        string note = ShapeSeed.FromDrawing(sh, null, Archives, DesignSpec.W08).Note;
        Assert.Contains("来自图纸", note);
        // ★ 2026-08-25 改判：FromDrawing 原本**整段覆盖** Choose 的申报，写的是
        //   「来自设计记录：舌保温／环倍率／管保温／控温点／压接段」—— 那句话在
        //   Choose 改成用 StartPoint 之后变成了**假的**，而且把对的那段挤掉了。
        //   现在图纸那部分**加在** Choose 的申报前面，两段都得在。
        Assert.Contains("不是设计记录", note);           // 来自 Choose：五个优化变量的起点
        Assert.Contains("StartPoint", note);
        Assert.Contains("压接段", note);               // 仍来自档的构型常数，点名
        Assert.Contains("铁律②", note);
        // 自证：那句已被推翻的话不许再出现
        Assert.DoesNotContain("来自设计记录（图纸给不了）：舌保温", note);
    }


    // ── 不许污染静态档 ──────────────────────────────────────────

    /// <summary>
    /// ★ 老坑重演防线：Clone 的注释里写着「扰动必须作用在副本上，否则 static 实例被就地改掉」。
    /// 种子被改形状、改壁厚、压平之后，两个设计记录 static 必须纹丝不动。
    /// </summary>
    [Fact]
    public void NeverMutatesTheStaticArchives()
    {
        double[] before08 = (double[])DesignSpec.W08.TabThickMm.Clone();
        double[] before06 = (double[])DesignSpec.W06.TabThickMm.Clone();
        double wallBefore = DesignSpec.W08.WallMm;

        var c = ShapeSeed.Choose(0.8, null, Th, Archives, DesignSpec.W08);
        c.Seed.TabThickMm[0] = 99;
        c.Seed.WallMm = 42;

        Assert.Equal(before08, DesignSpec.W08.TabThickMm);
        Assert.Equal(before06, DesignSpec.W06.TabThickMm);
        Assert.Equal(wallBefore, DesignSpec.W08.WallMm, 9);
    }

    /// <summary>新解不继承旧档的失效告示（否则一个新形状会挂着别人的「已作废」）。</summary>
    [Fact]
    public void FreshSeedCarriesNoInvalidNotice()
    {
        var c = ShapeSeed.Choose(DesignSpec.Retired08.WallMm, DesignSpec.Retired08.Name,
                                 Th, Archives, DesignSpec.W08);
        Assert.Equal("", c.Seed.Invalid);
        Assert.Empty(c.Seed.InvalidChecks);
        // 自证：源档确实带着失效告示，否则上面两条恒真
        Assert.False(string.IsNullOrEmpty(DesignSpec.Retired08.Invalid));
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

        // ★ --from3dm 配 --wall 时，种子的壁厚必须跟着命令行走 —— 否则
        //   「表头说 0.8、实际算 1.0」（2026-08-25 差点放过去）。而且覆盖了要出声。
        Assert.Contains("seedPickS.Seed.WallMm = wallS;", src);
        Assert.Contains("管壁按命令行覆盖", src);
    }

    // ── 五个优化变量不许来自设计记录（用户 2026-08-25「彻底禁掉」） ──────

    /// <summary>
    /// ★★ 用户 2026-08-25：「把种子这种方法彻底禁掉，**设计记录是用来校正计算流程**，
    ///    不应当被乱用」；同日又定：「这些是优化变量：板厚 / 舌保温 / 环倍率 /
    ///    管保温 / 夹持温度，优化程式需自己给出答案，**可以在 UI 输入框上给初始值**」。
    /// ⇒ 这五项的起点必须来自 StartPoint（有依据的声明式初始值）或真实输入，**不是设计记录**。
    /// </summary>
    [Fact]
    public void 四个优化变量的起点来自StartPoint_不是设计记录()
    {
        var c = ShapeSeed.Choose(0.8, null, Th, Archives, DesignSpec.W08);
        Assert.All(c.Seed.TabInsulMm, v => Assert.Equal(StartPoint.TabInsulMm, v, 9));
        Assert.All(c.Seed.RingMul, v => Assert.Equal(StartPoint.RingMul, v, 9));
        Assert.Equal(StartPoint.TubeInsulMm, c.Seed.TubeInsulMm, 9);
        Assert.Equal(StartPoint.ClampTempC, c.Seed.ClampTempC, 9);

        // 自证：设计记录的这几项**不等于**起点，否则上面四条恒真
        Assert.NotEqual(StartPoint.TabInsulMm, DesignSpec.W08.TabInsulMm[2], 9);   // 0.3 vs 0.5
        Assert.NotEqual(StartPoint.TubeInsulMm, DesignSpec.W08.TubeInsulMm, 9);    // 10 vs 5
        Assert.NotEqual(StartPoint.ClampTempC, DesignSpec.W08.ClampTempC, 9);      // 300 vs 450
    }

    [Fact]
    public void 板厚起点必给_没有就抛并指出三条真实来源()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ShapeSeed.Choose(0.8, null, null, Archives, DesignSpec.W08));
        Assert.Contains("--from3dm", ex.Message);
        Assert.Contains("--thick", ex.Message);
        Assert.Contains("界面", ex.Message);
        Assert.Contains("不会**再回退到设计记录", ex.Message);
    }

    [Fact]
    public void 板厚照命令行给的走()
    {
        var c = ShapeSeed.Choose(0.8, null, new[] { 1.11, 2.22, 3.33, 0.44 }, Archives, DesignSpec.W08);
        Assert.Equal(new[] { 1.11, 2.22, 3.33, 0.44 }, c.Seed.TabThickMm);
    }

    /// <summary>仍取自设计记录的那几项（铁律②要求）必须在申报里点名，不许闷着。</summary>
    [Fact]
    public void 申报要说清哪些仍来自设计记录()
    {
        string note = ShapeSeed.Choose(0.8, null, Th, Archives, DesignSpec.W08).Note;
        Assert.Contains("不是设计记录", note);
        Assert.Contains("压接段", note);
        Assert.Contains("铁律②", note);
    }
}