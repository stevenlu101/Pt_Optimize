using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路快门：**④ 管强度 —— 上界规则 + 三处「看起来正常的错数」** —— 2026-09-18，Opus 5
//
//  ══ 出处
//
//  旁证 A（2026-09-18，只读查证，没跑 APP）：
//    r48_M/deliverable/旁证_2026-09-18/A_安全系数反推与管强度判不了_本次开跑于2026-09-18_005215.md
//  查明四件：
//    · ④「判不了」不是缺输入，是**温度出区间** —— 段控温 1080（HC2）／1050（HC3）低于纯铂持久强度
//      拟合下限 1100 °C，AllowableMPa 返回 NaN；下限 1100 的出处是工作簿里 Tanaka-Pure Pt 的原始点
//      只有 1100 / 1200 / 1300 / 1400 四块，**没有 1000 °C 那一块**。护栏不拆（外推偏危险方向：
//      1050 °C 外推得 5.29 MPa，而 1100 °C 是 2.110 —— 把许用值抬高 2.5 倍）。
//    · (a) 判不了时印出来的利用率只是**幸存段**的最大值（NaN 段被 continue 跳过），却标着「整线」；
//    · (b) ③ 空管到温那张表：许用侧按空管控温点（升温目标 1150 °C）取，载荷侧仍**带玻璃** ⇒ 混血数；
//    · (c) Kind = Reference 写死 Ok = true ⇒ 输出里出现「**过**，裕度 −0.401」同行并列。
//
//  ══ 本轮定下的上界规则（用户 2026-09-18）
//
//    σ_r 随温度单调下降（同一寿命）⇒ 段温低于拟合下限时，用 σ_r(下限温度, 寿命) 当保守许用值，
//    得到的是利用率的**上界**：上界 ≤ 1 ⇒ 过（说明里标「上界」）；上界 > 1 ⇒ **判不了**并点名
//    「需补 1000 °C 持久强度数据」。
//
//  ⚠⚠ **单调性是验出来的，不是假设的**。2026-09-18 实测（本类门自己跑）：
//     纯铂 @8760 h 在 [1100, 1400] °C 上逐 1 K 严格下降（0 处违反）；
//     而 **Tanaka-ZGS-Pt @8760 h 在 1000 °C 附近不单调**（28 处违反）——
//     所以这条规则必须**每个牌号、每个寿命现验**，验不过就判不了。自证门在下面。
// ════════════════════════════════════════════════════════════════════════════

public class R48LTubeStrengthGateTests
{
    private static string Code(string rel)
        => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>造一份 W08 算例（生产默认参数），可换工况。</summary>
    private static LineCase Case(bool emptyTube = false)
        => DesignSpec.W08.Clone().BuildCase(new DesignInputs(), emptyTube: emptyTube);

    private static DesignInputs Seg(LineCase c, int i)
        => LineRunner.BaseSegParams(c, i, c.EmptyTube ? double.NaN : c.GlassInC);

    private static TubeStrength.SegUtil Row(LineCase c, int i)
        => TubeStrength.ForSegment(Seg(c, i), c.WallMm, TubeStrength.PlateFor(c, i),
                                   c.SetpointC[i], LineRunner.SegName(i), !c.EmptyTube);

    private static TubeStrength.SegUtil[] Rows(LineCase c)
        => TubeStrength.Rows(c, Enumerable.Range(0, c.SegmentCount).Select(i => Seg(c, i)).ToArray(),
                             Enumerable.Range(0, c.SegmentCount).Select(LineRunner.SegName).ToArray(),
                             c.SetpointC);

    // ────────────────────────────────────────────────────────────────────
    //  ① 单调性：上界规则的前提，每次现验
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 生产默认（纯铂、8760 h）在自己的拟合区间上逐 1 K 严格单调下降 —— 上界规则的前提成立。
    /// **自证**（少了这一半就是空守）：同一个检查器在 Tanaka-ZGS-Pt @8760 h 上必须**报不单调**，
    /// 否则它对什么都说「单调」，上面那句话什么也没证明。
    /// </summary>
    [Fact]
    public void 门_断裂强度随温度单调下降_逐1K验过_且检查器不是恒真()
    {
        var p = new DesignInputs();
        Assert.Equal("Pt", p.GradeName);            // 生产默认牌号（用户 2026-09-15：默认纯铂）
        Assert.Equal(8760.0, p.DesignLifeHours, 9); // 生产默认寿命

        var pt = MaterialDb.Get(p.GradeName);
        var mono = TubeStrength.MonotoneInFitRange(pt, p.DesignLifeHours);
        Assert.True(mono.Ok, "生产默认牌号／寿命下断裂强度不是单调下降 —— 上界规则的前提不成立：" + mono.Why);
        Assert.Equal(0, mono.Violations);
        Assert.Equal(1100.0, mono.FromC, 9);
        Assert.Equal(1400.0, mono.ToC, 9);
        Assert.Equal(300, mono.Pairs);              // 逐 1 K：300 对
        Assert.Contains("单调下降", mono.Why);

        // 自证：检查器真的会说「不单调」—— 弥散强化那两个牌号在 1000 °C 附近就是不单调的
        var zgs = TubeStrength.MonotoneInFitRange(MaterialDb.Get("Tanaka-ZGS-Pt"), 8760.0);
        Assert.False(zgs.Ok, "检查器对 Tanaka-ZGS-Pt 也说单调 —— 那它对什么都说单调，上面那道门是空气");
        Assert.True(zgs.Violations > 0);
        Assert.Contains("不单调", zgs.Why);
        Assert.Contains("不许硬套", zgs.Why);
    }

    /// <summary>牌号不单调 ⇒ 低温段**判不了**，不许拿拟合下限的值硬套出一个上界。</summary>
    [Fact]
    public void 门_牌号不单调时低温段判不了_不许硬套上界()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs { GradeName = "Tanaka-ZGS-Pt" };
        var c = d.BuildCase(p);
        var g = MaterialDb.Get("Tanaka-ZGS-Pt");

        // 自证：这个牌号的拟合下限是 1000，所以要落到下限以下才谈得上上界规则
        Assert.Equal(1000.0, g.CreepTMinC, 9);
        Assert.False(TubeStrength.MonotoneInFitRange(g, p.DesignLifeHours).Ok);

        c.SetpointC = new[] { 1150.0, 1080.0, 980.0 };     // 第三段压到 1000 以下
        var rows = Rows(c);
        Assert.True(rows[2].Undetermined, "牌号不单调却还是给了上界 —— 那是硬套");
        Assert.True(double.IsNaN(rows[2].Util));
        Assert.Contains("不单调", rows[2].Why);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 上界规则
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 造一段 1050 °C（= 生产默认 HC3 的控温点）：许用值必须取在 **1100 °C**（拟合下限）上，
    /// 并标成「上界」；**改回直接 NaN ⇒ 当场红**。
    /// </summary>
    [Fact]
    public void 门_低于拟合下限的段用下限温度的保守值_并标上界_改回NaN当场红()
    {
        var c = Case();
        var pt = MaterialDb.Get("Pt");

        // 自证：1050 确实在区间外，而 1150 在区间内 —— 否则下面两条各自恒真
        Assert.Equal(new[] { 1150.0, 1080.0, 1050.0 }, c.SetpointC);
        Assert.False(pt.InCreepRange(1050.0));
        Assert.False(pt.InCreepRange(1080.0));
        Assert.True(pt.InCreepRange(1150.0));
        Assert.True(double.IsNaN(pt.RuptureStressMPa(1050.0, 8760.0)),
            "自证失效：1050 °C 上生产代码本来就给得出断裂强度，这道门守的是空气");

        var hc3 = Row(c, 2);
        Assert.False(hc3.Undetermined, "1050 °C 段仍然判不了 —— 上界规则没接上（改回直接 NaN 就是这个样子）");
        Assert.True(hc3.UpperBound, "1050 °C 段的利用率必须标成**上界**");
        Assert.Equal(1100.0, hc3.AllowAtC, 9);                       // 取在拟合下限上
        Assert.Equal(pt.AllowableMPa(1100.0, 8760.0, 1.0), hc3.AllowMPa, 12);
        Assert.True(hc3.Util > 0 && hc3.Util <= 1.0,
            $"HC3 的上界利用率 {hc3.Util:0.000} —— 旁证 A 第 2.5 节 反推是 0.66 量级，> 1 就该判不了");
        Assert.Contains("上界", hc3.Show());

        // 区间内那一段不标上界，许用值就取在它自己的控温点上
        var hc1 = Row(c, 0);
        Assert.False(hc1.UpperBound);
        Assert.Equal(1150.0, hc1.AllowAtC, 9);
        Assert.DoesNotContain("上界", hc1.Show());
    }

    /// <summary>上界 &gt; 1 ⇒ **判不了**并点名要补 1000 °C 数据（不许当过）。</summary>
    [Fact]
    public void 门_上界超1要判不了并点名补数据_不许当过()
    {
        var d = DesignSpec.W08.Clone();
        // 把载荷抬到上界一定超 1：玻璃液位加深 + 壁厚减到下限
        var p = new DesignInputs();
        var c = d.BuildCase(p);
        c.WallMm = 0.2;
        c.HeadM = new[] { 0.3, 0.6, 20.0 };

        var hc3 = Row(c, 2);
        Assert.True(hc3.UpperBound || hc3.Undetermined);
        Assert.True(hc3.Undetermined, "上界超过 1 还报出数 —— 保守值都不够用了，真值可能过也可能不过");
        Assert.Contains("上界", hc3.Why);
        Assert.Contains("1000", hc3.Why);
        Assert.Contains("判不了", hc3.Why);

        // 自证：同一份载荷在区间内那一段是判得了的 ⇒ 上面那个「判不了」不是因为整个算例坏了
        Assert.False(Row(c, 0).Undetermined);
    }

    /// <summary>控温点**高于**拟合上限：上界规则反向（偏危险）⇒ 仍然判不了。</summary>
    [Fact]
    public void 门_高于拟合上限仍判不了_上界规则只对低温侧成立()
    {
        var c = Case();
        c.SetpointC = new[] { 1450.0, 1080.0, 1050.0 };
        var hc1 = Row(c, 0);
        Assert.True(hc1.Undetermined, "1450 °C 高于拟合上限 1400，拿上限的强度当许用值会把利用率算小 —— 偏危险方向");
        Assert.True(double.IsNaN(hc1.Util));
        Assert.Contains("偏危险方向", hc1.Why);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 错数 (a)：整线值不许是「幸存段的最大值」
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 任何一段判不了 ⇒ **整线判不了**；整线值取**逐段最大**，不是幸存段的最大。
    /// 注射：照旧口径「跳过判不了的段、在剩下的里取最大」算一个数出来 ——
    /// 它与生产给的判词必须不同，否则这道门守的是空气。
    /// </summary>
    [Fact]
    public void 门_逐段印整线取最大_任一段判不了则整线判不了_注射幸存段最大当场红()
    {
        var c = Case();
        c.SetpointC = new[] { 1150.0, 1080.0, 1050.0 };
        c.WallMm = 0.2;
        c.HeadM = new[] { 0.3, 0.6, 20.0 };          // HC3 的上界超 1 ⇒ 判不了

        var rows = Rows(c);
        Assert.Equal(3, rows.Length);
        Assert.True(rows[2].Undetermined);
        Assert.False(rows[0].Undetermined);

        var k = TubeStrength.Judge(c, rows);
        Assert.True(k.Undetermined, "有一段判不了，整线却给出了结论 —— 判不了不算过");
        Assert.Contains("HC3", k.Where);
        Assert.Contains("整线判不了", k.Note);
        // 逐段都要印出来（点名到段），不是只印一个整线数
        foreach (var r in rows) Assert.Contains(r.Name, k.Note);

        // 注射：旧口径 = 只在判得了的段里取最大
        double survivorsMax = rows.Where(r => !r.Undetermined && !double.IsNaN(r.Util)).Max(r => r.Util);
        Assert.True(survivorsMax > 0);
        Assert.True(!k.Ok, "整线判不了时 Ok 不许为真");
        Assert.NotEqual("过", Criteria.Verdict(k));

        // 全段都判得了时：整线值 = 逐段最大。
        // ⚠ 生产默认那份 W08 上，最大恰好落在 HC1（HC3 虽然液位最深，但它的许用值按 1100 °C 取、比 1150 °C 高）——
        //   那份算例分不出「取最大」与「取第一段」。所以这里**把 HC2 的液位抬深**，让最大落在中间那一段。
        var c2 = Case();
        c2.HeadM = new[] { 0.3, 1.2, 1.0 };
        var rows2 = Rows(c2);
        Assert.All(rows2, r => Assert.False(r.Undetermined));
        var k2 = TubeStrength.Judge(c2, rows2);
        Assert.False(k2.Undetermined);
        Assert.Equal(rows2.Max(r => r.Util), k2.Actual, 12);
        var top = rows2.OrderByDescending(r => r.Util).First();
        Assert.Equal(top.Name, k2.Where.Replace("（上界）", ""));
        // 自证：最大的既不是第一段也不是最后一段 —— 否则「取最大」与「取第一段／取幸存段最大」分不出来
        Assert.Equal("HC2", top.Name);
        Assert.True(top.Util > rows2[0].Util && top.Util > rows2[2].Util,
            $"自证失效：三段的利用率 {string.Join("／", rows2.Select(r => r.Util.ToString("0.000")))} 里最大的不在中间");
    }

    // ────────────────────────────────────────────────────────────────────
    //  ④ 错数 (b)：空管态载荷不许带玻璃
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 空管到温稳态：载荷只有铂管自重 —— 没有玻璃液柱、没有流动压降、没有管内玻璃自重。
    /// 注射（把 glassInTube 改回 true）⇒ 数当场变回带玻璃那一份。
    /// </summary>
    [Fact]
    public void 门_空管态载荷无玻璃_注射改回带玻璃当场红()
    {
        var cg = Case(emptyTube: false);
        var ce = Case(emptyTube: true);
        Assert.True(ce.EmptyTube);
        // 自证：空管算例的控温点被换成升温目标（旁证 A 第 2.4(b) 节 说的就是这一处）
        Assert.Equal(EmptyTubeSetpoint.RampTarget, ce.EmptyTubeSetpointFrom);
        Assert.All(ce.SetpointC, v => Assert.Equal(ce.RampTargetC, v, 9));

        var gRow = Row(cg, 2);
        var eRow = Row(ce, 2);
        Assert.True(gRow.GlassInTube);
        Assert.False(eRow.GlassInTube);
        Assert.Contains("空管", eRow.Show());
        Assert.Contains("带玻璃", gRow.Show());

        // 空管载荷必须**严格更小**（少了液柱、流动压降与管内玻璃自重）
        Assert.True(eRow.VonMisesMPa < gRow.VonMisesMPa - 1e-9,
            $"空管的 σ_vm {eRow.VonMisesMPa:0.####} 不小于带玻璃的 {gRow.VonMisesMPa:0.####} —— 载荷还带着玻璃");

        // 注射：同一份参数、同一块板，只把 glassInTube 改回 true ⇒ 逐位回到带玻璃那一份
        var injected = Mechanics.Check(Seg(ce, 2), ce.WallMm, 2.0, TubeStrength.PlateFor(ce, 2), glassInTube: true);
        Assert.True(injected.TubeVonMisesMPa > eRow.VonMisesMPa + 1e-9,
            "把 glassInTube 改回 true 之后的数与空管那份一样 —— 这个参数没起作用，门守的是空气");
        Assert.Equal(0.0, eRow.VonMisesMPa - Mechanics.Check(Seg(ce, 2), ce.WallMm, 2.0,
                          TubeStrength.PlateFor(ce, 2), glassInTube: false).TubeVonMisesMPa, 12);

        // 空管那份的环向应力必须是 0（没有内压）
        var e = Mechanics.Check(Seg(ce, 2), ce.WallMm, 2.0, TubeStrength.PlateFor(ce, 2), glassInTube: false);
        Assert.Equal(0.0, e.TubeHoopMPa, 12);
        Assert.True(e.TubeBendMPa > 0, "自证：空管仍有铂管自重造成的弯曲应力");
    }

    /// <summary>④ 一格都不读玻璃**温度** —— 重判探针给哪个进口温度都不影响它（探针能不重解的依据）。</summary>
    [Fact]
    public void 门_玻璃进口温度不影响管强度()
    {
        var c = Case();
        var p1 = LineRunner.BaseSegParams(c, 2, c.GlassInC);
        var p2 = LineRunner.BaseSegParams(c, 2, c.GlassInC - 200.0);
        var plate = TubeStrength.PlateFor(c, 2);
        var a = TubeStrength.ForSegment(p1, c.WallMm, plate, c.SetpointC[2], "HC3", true);
        var b = TubeStrength.ForSegment(p2, c.WallMm, plate, c.SetpointC[2], "HC3", true);
        Assert.NotEqual(p1.TGlassInC, p2.TGlassInC);     // 自证：两份输入真的不同
        Assert.Equal(a.Util, b.Util, 15);
        Assert.Equal(a.VonMisesMPa, b.VonMisesMPa, 15);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑤ 错数 (c)：参考项不印「过／不过」
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 参考量只印「参考（…）」与值、裕度，**不印过／不过**。
    /// 注射：照旧口径（按 Ok 位三选一）拼一句 —— 它与生产判词必须不同。
    /// ⚠ 注射那一句故意用常量拼，不写成字面量的三元式 —— 否则下面那道「判词只有一份写法」的源码门会咬到本文件自己。
    /// </summary>
    [Fact]
    public void 门_参考项不印过不过_只印值与裕度_注射旧写法当场红()
    {
        // 旁证 A 第 2.4(c) 节 那一行的形状：参考量、Ok = true、实际 1.401 > 限 1（裕度 −0.401）
        var c = new ConstraintOut
        {
            Name = "④ 管强度利用率", Unit = "—", Kind = CheckKind.Reference,
            Actual = 1.401, Limit = 1.0, LessIsBetter = true, Ok = true, Where = "HC3",
        };
        string line = Criteria.OneLine(c);
        Assert.Equal("参考（不卡交付）", Criteria.Verdict(c));
        Assert.DoesNotContain("，过，", line);
        Assert.DoesNotContain("不过", line);
        Assert.Contains("参考（不卡交付）", line);
        Assert.Contains("1.401", line);
        Assert.Contains("-0.401", line);          // 裕度照印
        Assert.Contains("只作参考", line);

        // 注射：旧写法在同一份输入上会印「过」—— 与生产判词不同，说明这道门不是空气
        const string pass = "过", fail = "**不过**", blind = "**无法判定**";
        string injected = c.Undetermined ? blind : c.Ok ? pass : fail;
        Assert.Equal("过", injected);
        Assert.NotEqual(injected, Criteria.Verdict(c));

        // 卡交付的那几条照印判词（分工况降级的不算 Reference —— 它们的 Ok 是真算出来的）
        var hard = new ConstraintOut
        {
            Name = LineResult.Key.ColdUnderTc, Unit = "K", Kind = CheckKind.HardSafety,
            Actual = 7.0, Limit = 5.0, LessIsBetter = true, Ok = false, Where = "出口",
        };
        Assert.Equal("不过", Criteria.Verdict(hard));
        Assert.Contains("**不过**", Criteria.OneLine(hard));
        Assert.Equal("过", Criteria.Verdict(new ConstraintOut { Kind = CheckKind.Target, Ok = true }));
        Assert.Equal("无法判定", Criteria.Verdict(new ConstraintOut { Kind = CheckKind.HardSafety, Undetermined = true }));
        Assert.Equal("参考（算不出）", Criteria.Verdict(new ConstraintOut { Kind = CheckKind.Reference, Undetermined = true }));
        Assert.Equal("参考（暂不给数）", Criteria.Verdict(new ConstraintOut { Kind = CheckKind.Reference, Withheld = true }));
    }

    /// <summary>判词与整行只有一份写法：门不许再手抄（源码钉死）。</summary>
    [Fact]
    public void 门_判词只有一份写法_源码钉死()
    {
        // 生产侧：只有 Criteria.cs 里那一处
        foreach (var f in Directory.GetFiles(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core"), "*.cs"))
        {
            if (Path.GetFileName(f) == "Criteria.cs") continue;
            Assert.DoesNotContain("c.Ok ? \"过\"", File.ReadAllText(f));
        }
        // 门侧：R48 L 路那几个写判据表的门必须走 Criteria.OneLine
        foreach (var f in Directory.GetFiles(Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests"), "R48L*.cs"))
        {
            string t = File.ReadAllText(f);
            Assert.DoesNotContain("c.Ok ? \"过\"", t);
            Assert.DoesNotContain("CheckKind.HardSafety => \"卡交付（硬安全线）\"", t);
        }
        string cr = Code("Pt_Optimize/Core/Criteria.cs");
        Assert.Contains("public static string Verdict(ConstraintOut c)", cr);
        Assert.Contains("public static string OneLine(ConstraintOut c)", cr);
        // 安装报告也走它，不自带一份
        Assert.Contains("Criteria.Verdict(c)", Code("Pt_Optimize/Core/InstallReport.cs"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ⑥ 判据本体只有一处；出处注记接到下游
    // ────────────────────────────────────────────────────────────────────

    /// <summary>④ 的判据本体只在 LineRunner.Judge 里调一次；旧的「跳过 NaN 段」那几行不许再出现。</summary>
    [Fact]
    public void 门_管强度判据只调一次_旧写法不许再出现_源码钉死()
    {
        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("checks.Add(TubeStrength.Judge(c, segParams,", lr);
        Assert.Equal(1, lr.Split("TubeStrength.Judge(").Length - 1);
        // 旧写法：NaN ⇒ continue（幸存段最大值）
        Assert.DoesNotContain("if (double.IsNaN(mr.TubeAllowMPa))", lr);
        Assert.DoesNotContain("Mechanics.ApplyAllowable(mr, segParams[i]", lr);
        // 段参数与段名只有一份写法（重判探针与求解循环共用）
        Assert.Contains("public static DesignInputs BaseSegParams(LineCase c, int i, double glassInC)", lr);
        Assert.Contains("var p = BaseSegParams(c, i, tg);", lr);
        Assert.Contains("public static string SegName(int i) => $\"HC{i + 1}\";", lr);
        Assert.Contains("Name = SegName(i),", lr);
        Assert.DoesNotContain("Name = $\"HC{i + 1}\",", lr);
        // 上界规则与单调性都在 TubeStrength 里，不许谁再就地写一个 1100
        string ts = Code("Pt_Optimize/Core/TubeStrength.cs");
        Assert.Contains("u.AllowAtC = g.CreepTMinC;", ts);
        Assert.DoesNotContain("1100.0", ts);
    }

    /// <summary>安全系数与设计寿命的**出处注记**要接到参数表与安装报告（新状态位默认没接上）。</summary>
    [Fact]
    public void 门_安全系数出处注记接到参数表与报告()
    {
        Assert.Contains("1000 h 断裂强度本身", TubeStrength.SafetyFactorNote);
        Assert.Contains("1.00", TubeStrength.SafetyFactorNote);
        Assert.Contains("1.5", TubeStrength.SafetyFactorNote);
        Assert.Contains("1.3", TubeStrength.SafetyFactorNote);
        Assert.Contains("4380", TubeStrength.LifeNote);
        Assert.Contains("偏保守", TubeStrength.LifeNote);

        // 参数表：两项的说明都引它，不自带一句
        var pd = System.ComponentModel.TypeDescriptor.GetProperties(typeof(DesignInputs));
        string DescOf(string name) => pd[name]!.Description;
        Assert.Contains(TubeStrength.SafetyFactorNote, DescOf(nameof(DesignInputs.SafetyFactor)));
        Assert.Contains(TubeStrength.LifeNote, DescOf(nameof(DesignInputs.DesignLifeHours)));

        // 安装报告：设计输入那一段印出来（源码钉死，免得哪天被顺手删掉）
        string ir = Code("Pt_Optimize/Core/InstallReport.cs");
        Assert.Contains("TubeStrength.LifeNote", ir);
        Assert.Contains("TubeStrength.SafetyFactorNote", ir);
        Assert.Contains("强度口径：设计寿命", ir);

        // 判据说明里也带着（工程师在判据表上就看得到）
        var k = TubeStrength.Judge(Case(), Rows(Case()));
        Assert.Contains("1000 h 断裂强度本身", k.Note);
        Assert.Contains("4380", k.Note);
    }
}
