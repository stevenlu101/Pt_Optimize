using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路快门：**外层法兰↔管耦合的停机容差** —— 2026-09-17，Opus 5
//
//  病（实测出处 deliverable/R48_L_管根判据_最后一位敏感度_本次开跑于2026-09-17_151814.txt）：
//    停机容差 1 K **比判据的裕度（0.489 K）还粗** ⇒ 1 ulp 的输入差就能把「管根低于热偶读数」
//    挪 0.374 K ⇒ 这条硬安全线的「过」与「不过」分不开。
//
//  修：容差 = min(上限, max(下限, 比例 × 当前最小硬安全线温度裕度))，
//      全仓唯一读口 LineRunner.CoupleTolKFor（KnobQuantum 同法）。
//
//  这几道门守的是：
//    ① 读口只有一份 —— 主环停机两支、基线环、未收敛报告都从它读，不许谁再抄一个 c.CoupleTolK 去比；
//    ② 公式本身（裕度大 ⇒ 上限；裕度小 ⇒ 按裕度；再小 ⇒ 下限；负裕度按绝对距离）；
//    ③ 名单**不由人列** —— 从结果自己的判据表里挑「硬安全线 + 单位 K」，瓦的那条不许混进来；
//    ④ **生产默认必须比那份交付设计的裕度细**；改回 1 K ⇒ 当场红（注射）；
//    ⑤ 轮数与实际容差要写进结果、印进证据头（新状态位默认没接上）。
//  可复现那一条（1 ulp 扰动六次摆幅 ≤ 0.05 K）是慢门，在 R48LCoupleTolReproducibleTests。
// ════════════════════════════════════════════════════════════════════════════

public class R48LCoupleTolGateTests
{
    private static string Code(string rel)
        => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    private static LineResult WithChecks(params ConstraintOut[] checks) => new() { Checks = checks };

    private static ConstraintOut Cold(double actual, double limit = 5.0) => new()
    {
        Name = LineResult.Key.ColdUnderTc,
        Unit = "K",
        Kind = CheckKind.HardSafety,
        Actual = actual,
        Limit = limit,
        Ok = actual <= limit,
    };

    private static ConstraintOut Hot(double actual, double limit = 5.0) => new()
    {
        Name = LineResult.Key.HotOverTc,
        Unit = "K",
        Kind = CheckKind.HardSafety,
        Actual = actual,
        Limit = limit,
        Ok = actual <= limit,
    };

    // ────────────────────────────────────────────────────────────────────
    //  ① 读口只有一份
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 容差在 LineRunner 里出现在四处（主环停机的两支、基线环、未收敛报告的「还要多少轮」与分辨率地板）。
    /// 四处必须都读同一个读口算出来的那个数；谁再写一个 <c>c.CoupleTolK</c> 去比大小，这道门立刻红 ——
    /// 而那种改动**不会有任何报错**，只会让一处收紧了另一处没收紧（本仓库最常见的病）。
    /// </summary>
    [Fact]
    public void 门_停机容差的读口只有一份_源码钉死()
    {
        string s = Code("Pt_Optimize/Core/LineRunner.cs");

        // 读口本身
        Assert.Contains("public static double CoupleTolKFor(LineCase c, LineResult? r)", s);
        Assert.Contains("public static double MinHardTempMarginK(LineResult? r)", s);

        // 四处都从读口拿
        Assert.Contains("double baseTolK = BaselineTolKFor(c);", s);              // 基线环（它服务的是参考量，容差另有一份）
        Assert.Contains("public static double BaselineTolKFor(LineCase c)", s);
        Assert.Contains("double tolNow = CoupleTolKFor(c, res);", s);             // 主环
        // ★ 2026-09-18 Opus 5（合并 J×L 改门，写明变因）：J 路把停机三条判定（步长、真残差、管根 NaN）提成了生产函数 LineRunner.CoupleConverged；
        //   合并时把它的放大与容差改成由调用方传入（tolNow / ampWorst），主环那两行局部变量 resOk 随之取消。
        //   本门原来钉 "bool resOk = resK * ampWorst < tolNow;" 这一行字面，改钉合并后的调用行 —— 守的东西没变（真残差那支必须乘当场算的 ampWorst、比当场算的 tolNow）。
        Assert.Contains("if (CoupleConverged(remain, resK, tolNow, rootNaN, ampWorst))", s);
        Assert.Contains("=> remainK < tolK && resK * amp < tolK && !rootNaN;", s);
        Assert.DoesNotContain("resK * LineCase.FixedPointAmp", s);
        Assert.Contains("double tolLast = double.IsNaN(lastTolK) ? CoupleTolKFor(c, res) : lastTolK;", s);
        Assert.Contains("ResolutionFloorNote(delta, resKLast, tolLast, ampNow, c.CoupleMaxRounds)", s);

        // 谁都不许再拿 c.CoupleTolK 直接比大小／做除法（那是上限，不是这次用的容差）
        Assert.DoesNotContain("< c.CoupleTolK", s);
        Assert.DoesNotContain("<= c.CoupleTolK", s);
        Assert.DoesNotContain(">= c.CoupleTolK", s);
        Assert.DoesNotContain("> c.CoupleTolK", s);
        Assert.DoesNotContain("c.CoupleTolK /", s);
        Assert.DoesNotContain("Math.Log(c.CoupleTolK", s);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 公式
    // ────────────────────────────────────────────────────────────────────

    /// <summary>裕度大 ⇒ 用上限；裕度中 ⇒ 按裕度；裕度小 ⇒ 用下限。三段都当场算给它看。</summary>
    [Fact]
    public void 门_容差公式_裕度大用上限_裕度中按裕度_裕度小用下限()
    {
        var lc = new LineCase();   // 生产默认
        Assert.True(lc.CoupleTolFromMargin, "生产默认必须是「按判据裕度收紧」");

        // 裕度 5 K（离判决线很远）⇒ 上限
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(0.0), Hot(0.0))), 12);

        // 裕度 0.489 K（只有冷侧那一条）⇒ 0.1 × 0.489 = 0.0489
        Assert.Equal(0.0489, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.511))), 9);
        // W08 那份交付设计的两条一起：冷 4.511（裕 0.489）、热 4.529（裕 **0.471**，更紧）⇒ 0.0471
        Assert.Equal(0.0471, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.511), Hot(4.529))), 9);

        // 裕度 0.05 K ⇒ 0.005 会低于下限 ⇒ 用下限 0.02
        Assert.Equal(lc.CoupleTolFloorK, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.95))), 12);

        // 已经越线（裕度 −0.3 K）：离判决线同样近 ⇒ 按**绝对距离**取 0.03，不是回到上限
        Assert.Equal(0.03, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(5.3))), 9);

        // 两条温度判据取**最小**的那个裕度
        Assert.Equal(0.02, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.9), Hot(2.0))), 9);
    }

    /// <summary>关掉开关 = 历史口径：常数，和判据裕度一点关系都没有（注射这一支靠它）。</summary>
    [Fact]
    public void 门_关掉开关就是常数口径_与裕度无关()
    {
        var lc = new LineCase { CoupleTolFromMargin = false, CoupleTolK = R48LCoupleTolKit.LegacyTolK };
        Assert.Equal(R48LCoupleTolKit.LegacyTolK, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.999))), 12);
        Assert.Equal(R48LCoupleTolKit.LegacyTolK, LineRunner.CoupleTolKFor(lc, null), 12);
    }

    /// <summary>
    /// 上限比下限还细时，**上限赢** —— 谁把上限调到 0.005 是要更细，不能被下限反手放粗。
    /// （写成 max(下限, …) 再 min(上限, …) 才对；次序写反就是这里红。）
    /// </summary>
    [Fact]
    public void 门_上限比下限还细时上限赢()
    {
        var lc = new LineCase { CoupleTolK = 0.005 };
        Assert.Equal(0.005, LineRunner.CoupleTolKFor(lc, WithChecks(Cold(4.999))), 12);
    }

    /// <summary>还没有判据可读（基线那一步）⇒ 上限，不许当成「裕度为 0」去顶着下限跑。</summary>
    [Fact]
    public void 门_还没有判据可读时用上限()
    {
        var lc = new LineCase();
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, null), 12);
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, new LineResult()), 12);
        Assert.True(double.IsNaN(LineRunner.MinHardTempMarginK(null)));
        Assert.True(double.IsNaN(LineRunner.MinHardTempMarginK(new LineResult())));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 名单不由人列
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 名单从结果自己的判据表里挑「硬安全线 + 单位 K」：
    /// 单位是瓦的（管孔净流入）**不许**混进来 —— 瓦和开尔文之间要一个没有实测的灵敏度才能换算；
    /// 参考量（Kind = Reference）也不许 —— 它不卡交付，拿它定分辨率就是让不判的东西决定判得准不准。
    /// </summary>
    [Fact]
    public void 门_名单只认硬安全线里单位为K的_瓦与参考量都不进()
    {
        var lc = new LineCase();

        var wattTight = new ConstraintOut
        {
            Name = LineResult.Key.NetFlux, Unit = "W", Kind = CheckKind.HardSafety,
            Actual = 0.001, Limit = 0.0, LessIsBetter = false, Ok = true,
        };
        var refTight = new ConstraintOut
        {
            Name = LineResult.Key.DiscTemp, Unit = "K", Kind = CheckKind.Reference,
            Actual = 9.999, Limit = 10.0, Ok = true,
        };

        // 只有瓦的那条很紧 ⇒ 挑不到温度判据 ⇒ 上限
        Assert.True(double.IsNaN(LineRunner.MinHardTempMarginK(WithChecks(wattTight))));
        Assert.Equal(lc.CoupleTolK, LineRunner.CoupleTolKFor(lc, WithChecks(wattTight)), 12);

        // 参考量很紧也不算
        Assert.True(double.IsNaN(LineRunner.MinHardTempMarginK(WithChecks(refTight))));

        // 混在一起时，只有那条硬安全线温度判据说了算
        Assert.Equal(0.489, LineRunner.MinHardTempMarginK(WithChecks(wattTight, refTight, Cold(4.511))), 9);
    }

    /// <summary>判不了 / 暂不给数 / NaN 一概跳过 —— 「判不了」不许当成「裕度 0」去把容差顶到下限。</summary>
    [Fact]
    public void 门_判不了与暂不给数与NaN一概跳过()
    {
        var undet = Cold(4.999); undet.Undetermined = true;
        var withheld = Cold(4.999); withheld.Withheld = true;
        var nan = Cold(double.NaN);

        Assert.True(double.IsNaN(LineRunner.MinHardTempMarginK(WithChecks(undet, withheld, nan))));
        Assert.Equal(0.489, LineRunner.MinHardTempMarginK(WithChecks(undet, withheld, nan, Cold(4.511))), 9);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ④ 生产默认必须比那份交付设计的裕度细（改回 1 K ⇒ 红）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **判据的分辨率必须比停机容差细。**
    /// 拿在跑的那份交付设计（W08 导航档，管根 4.511 / 限值 5 ⇒ 裕度 0.489 K）当靶子：
    /// 生产默认下的停机容差必须至少比这个裕度细 5 倍；把容差改回 1 K（改前行为）⇒ 这道门当场红。
    /// ⚠ 门槛 5 倍是本门自己写死的判读，不是从生产里抄的；裕度与限值都从出处读，不手抄。
    /// </summary>
    [Fact]
    public void 门_生产默认必须比交付设计的裕度细_改回1K当场红()
    {
        var lc = new LineCase();
        // 靶子：那份交付设计的两条硬安全线温度判据，取更紧的那一条（冷 0.489 K／热 0.471 K ⇒ 0.471）
        double margin = Math.Min(lc.ColdUnderTcMaxK - R48LW08NavDesign.NavColdUnderTcK,
                                 lc.HotOverTcMaxK - R48LW08NavDesign.NavHotOverTcK);   // 出处见 R48LW08NavDesign.Source
        Assert.True(margin > 0, "靶子设计在出处那一跑是过的，裕度必须为正");

        var r = WithChecks(Cold(R48LW08NavDesign.NavColdUnderTcK, lc.ColdUnderTcMaxK),
                           Hot(R48LW08NavDesign.NavHotOverTcK, lc.HotOverTcMaxK));

        double tol = LineRunner.CoupleTolKFor(lc, r);
        Assert.True(tol <= margin / 5.0,
            $"生产默认的停机容差 {tol:0.0000} K 没有比这份交付设计的裕度 {margin:0.###} K 细 5 倍 —— "
            + "判据的分辨率必须比停机容差细，否则「过」与「不过」分不开");

        // 注射：改回 1 K（改前行为）⇒ 容差比裕度还粗，这道门必须红
        var legacy = new LineCase();
        R48LCoupleTolKit.ApplyTol(legacy, R48LCoupleTolKit.TolMode.LegacyConst);
        double legacyTol = LineRunner.CoupleTolKFor(legacy, r);
        Assert.True(legacyTol > margin,
            $"注射（容差改回 {R48LCoupleTolKit.LegacyTolK} K）之后容差 {legacyTol} K 竟然不比裕度 {margin:0.###} K 粗 —— "
            + "那说明这道门守的是空气");
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑤ 新状态位要接到下游
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 轮数与「这次实际用的容差」必须写进结果，并印在收敛那一句里 ——
    /// 赋了值不等于用它的人读得到（收紧的成本只活在说明文字里就并列不成表）。
    /// </summary>
    [Fact]
    public void 门_轮数与实际容差要写进结果并印在收敛那一句里_源码钉死()
    {
        string s = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("res.CoupleRounds = lastRounds; res.CoupleTolKUsed = lastTolK;", s);
        Assert.Contains("lastTolK = tolNow; lastRounds = outer + 1;", s);
        Assert.Contains("**容差 {tolNow:0.000} K**", s);          // 收敛那一句
        Assert.Contains("**容差 {tolLast:0.000} K**", s);         // 未收敛那一句

        // 结果上的两个位真的存在（改名就当场红）
        var r = new LineResult();
        Assert.Equal(0, r.CoupleRounds);
        Assert.True(double.IsNaN(r.CoupleTolKUsed));
    }

    /// <summary>证据头里必须写明容差口径 —— 头上那行「耦合容差」印的是上限，不写口径就会被当成实际用的那个数。</summary>
    [Fact]
    public void 门_证据头必须写明容差口径()
    {
        var lc = DesignSpec.W08.Clone().BuildCase(new DesignInputs());
        string head = EvidenceHeader.ForLineCase("门自证", lc);
        Assert.Contains("耦合容差口径：", head);
        Assert.Contains("按判据裕度收紧", head);
        Assert.Contains("CoupleTolKUsed", head);

        R48LCoupleTolKit.ApplyTol(lc, R48LCoupleTolKit.TolMode.LegacyConst);
        string head2 = EvidenceHeader.ForLineCase("门自证", lc);
        Assert.Contains("没按判据裕度收紧", head2);
    }

    /// <summary>
    /// **基线那一层的容差按它服务的那条判据定。**
    /// 无法兰基线只进参考量「法兰增量温降」（限值 10 K、不卡交付）⇒ 1 K = 十分之一，够用；
    /// 主环服务的是两条硬安全线（裕度 0.1～0.5 K）⇒ 另有一份细的。
    /// ⚠ 这道门守的是**前提**：哪天「法兰增量温降」升回硬安全线，或基线的数被别的判据吃进去，
    ///   这个 1 K 就不够了 —— 那时这里当场红，而不是悄悄交出一份精度不够的数。
    /// 实测（同一份设计、同一张网格）：两层一起收到 0.1 K ⇒ 基线 201 s + 主环 75～92 s；基线留 1 K ⇒ 基线 19 s。
    /// </summary>
    [Fact]
    public void 门_基线容差按它服务的判据定_法兰增量温降必须还是参考量()
    {
        var e = Criteria.All.FirstOrDefault(x => x.Key == LineResult.Key.FlangeDip);
        Assert.NotNull(e);
        Assert.False(e!.Hard,
            "「法兰增量温降」升回硬安全线了 —— 那么无法兰基线的停机容差（LineCase.BaselineTolK = 1 K）"
            + "就不再是「限值的十分之一」那条理由撑得住的，必须按新判据的裕度重定，并重测机时");

        string s = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("Name = LineResult.Key.FlangeDip, Unit = \"K\", Kind = CheckKind.Reference", s);
        // 基线的数只经 ApplyBaseline 进结果（写 BaseTRoot*/FlangeDip*），没有第二条路
        Assert.Contains("if (baseline is not null) ApplyBaseline(res, baseline);", s);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(s, @"ApplyBaseline\(res, baseline\)").Count);

        var lc = new LineCase();
        Assert.Equal(1.0, LineRunner.BaselineTolKFor(lc), 12);
        Assert.True(lc.BaselineTolK > lc.CoupleTolK,
            "基线那一层比主环松才是本轮的口径；反过来就是把机时花在一个不卡交付的量上");
    }

    /// <summary>
    /// 两条**老路**留常数口径是查过之后的决定（FlangeAutoSizer 的搜索期故意放粗、
    /// InsulationSearch 的量化步长按常数容差折算），不是漏了 —— 谁把这两行删掉，
    /// 那两条路会**悄悄**变成全精度、机时不可控，而本轮一次都没实测过它们。
    /// </summary>
    [Fact]
    public void 门_两条老路留常数口径必须是显式写明的()
    {
        Assert.Contains("lc.CoupleTolFromMargin = false;", Code("Pt_Optimize/Core/FlangeAutoSizer.cs"));
        Assert.Contains("lc.CoupleTolFromMargin = false;", Code("Pt_Optimize/Core/InsulationSearch.cs"));
    }
}
