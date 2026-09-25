using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 快门：**接合区的保温要缠多少圈** —— 2026-09-17 写成硬安全线，
//                                        2026-09-18 按用户原话**降为参考行**（Opus 5）
//
//  ══ 两句原话，先后顺序不能颠倒
//
//  用户 2026-09-17（现场）：
//    「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕(会渐成球形)」
//    「圆盘与管子接触区都是20圈以下，其它地方(舌板与管)好缠绕」
//  ⇒ 每圈 = InsulationSearch.LayerMm = 0.5 mm ⇒ 接合区 20 圈 = 10 mm。当天据此做成硬安全线。
//
//  用户 2026-09-18（主会话问「圆盘区能不能用预制保温块做到 20 mm」）：
//    「还是只给材质保温厚度方案就行」
//  ⇒ APP 的交付物是**材质与各区厚度方案**，**怎么包（缠绕还是预制保温块）由现场定**。
//    缠不缠得出来是现场工艺，不是设计可行性 —— 拿它去判「这份设计不成立」，
//    会把一批现场做得出来（预制块）的设计判死。
//  ⇒ 本条**由硬安全线降为参考行**：照常算、照常印圈数提示，**不进 AllOk／HardOk、不卡交付**。
//
//  ★★ 决 104（业主 2026-09-25「圆盘保温块最大厚度(圆盘之前说过了10mm)」）：决103 口径下本条**升回硬安全线**
//    （限值 = DesignInputs.DiscInsulCapMm，缺省 10；不封顶、不外推、不按上限硬算）；决103前 口径仍是本档 2026-09-18 的参考行，
//    单参 WrapLimits.Judge(c) 就是那个口径，本档各门照旧钉它逐位不变。决103 口径的门在 R48DiscInsulCapTests。
//    下面「门_降为参考行接到下游」「门_参考行不卡交付_注射改回硬安全线当场红」两条按此改写（分工况表两张各看各的）。
//  ⇒ 与之配套，三个被 §0.-11 压下去的数**退回 §0.-11 之前的值**：
//      DesignSpec.FlangeInsulMm              10 → 20 mm
//      界面「法兰保温厚」控件上界            10 → 60 mm（DesignSpec.FlangeInsulMaxMm）
//      InsulationSearch.Options.DiscLayerMax 20 层 → 40 层
//    这三个数**都没有出处**（§0.-11 之前一直在用的历史值）—— 照实写「无出处、需定」，不假装它有。
//
//  ══ 这几道门守的是
//    ① 三个退回值各一条：默认 20、界面上界 60、圆盘层上界 40（改回 §0.-11 那三个数当场红）；
//    ② 上界**只有一份来源**（源码钉死）—— 这一条从 §0.-11 起就在，本轮只换了它指向的那一份；
//    ③ 参考线自己不许手抄 10：它必须 = 圈数 × 层厚（层厚改了跟着改）；
//    ④ 越界 ⇒ **参考行照实报圈数 + 提示预制保温块**，**不判不可行**；
//    ⑤ 算不出来 ⇒ 判不了（判不了不算过，也不算不过 —— 参考行同样不许拿初值顶）；
//    ⑥ 管保温与舌板保温**不进清单** —— 自证门：管保温 15 mm（30 圈）、舌保温 10.4 mm 照样不进；
//    ⑦ 接到下游（分工况表两态都 Reference、对照表 Hard=false、不吃管温场名单、界面查得到全名）；
//    ⑧ **注射「改回硬安全线」⇒ 红**：同一份不过的判据，Kind 换回 HardSafety 就会把整线打成不可交付 ——
//       证明「不卡交付」这件事是真的落地了，而不是这几条断言恰好都成立。
//
//  ⚠ 「越界不外推」这根骨头**没有变**：缠过 20 圈会渐成球形，那个形状本程序没有模型。
//    改的是「超了怎么办」—— 从「判不可行」改成「报圈数 + 提示改用预制块」（预制块正是等厚层，
//    本程序算的就是等厚层 ⇒ 按预制块做，算出来的数是对的）。
//
//  2026-09-18，Opus 5
// ════════════════════════════════════════════════════════════════════════════

public class R48LWrapTurnsGateTests
{
    private static string Code(string rel)
        => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>造一份 W08 算例（形状与工况都是内置的那一份），只动圆盘保温。</summary>
    private static LineCase Case(double discMm, double[]? perPlate = null, DesignInputs? p = null)
    {
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = discMm;
        if (perPlate is not null) d.DiscInsulMm = perPlate;
        return d.BuildCase(p ?? new DesignInputs());
    }

    // ────────────────────────────────────────────────────────────────────
    //  ① 三个退回值（用户 2026-09-18）—— 各一条
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 退回值之一：<c>DesignSpec.FlangeInsulMm</c> 默认 **20 mm**（§0.-11 之前的值）。
    /// **改回 §0.-11 那个 10（= 接合区参考线）当场红。**
    /// </summary>
    [Fact]
    public void 门_圆盘保温默认退回20_改回10当场红()
    {
        Assert.Equal(20.0, new DesignSpec().FlangeInsulMm, 12);
        Assert.NotEqual(WrapLimits.JointZoneMaxMm, new DesignSpec().FlangeInsulMm);   // 不再等于参考线 10

        // 默认**超过**一次缠绕能缠的圈数 —— 这正是用户 2026-09-18 允许的那一档：
        // 20 mm = 40 圈，现场改用预制保温块，APP 只管给厚度。
        Assert.True(new DesignSpec().FlangeInsulMm > WrapLimits.JointZoneMaxMm + 1e-9,
            "默认圆盘保温没有超过参考线 —— 那下面「超了也不卡交付」几条就成了空守");
        Assert.Equal(40.0, WrapLimits.TurnsOf(new DesignSpec().FlangeInsulMm), 9);

        // 内置设计不写这个字段 ⇒ 全走默认
        foreach (var d in new[] { DesignSpec.W08, DesignSpec.W06 })
            Assert.Equal(20.0, d.FlangeInsulMm, 12);
    }

    /// <summary>
    /// 退回值之二：界面「法兰保温厚 mm」控件上界 **60 mm**（§0.-11 之前的值），
    /// 且**只有一份来源** <see cref="DesignSpec.FlangeInsulMaxMm"/> —— 界面不许自带一个 60。
    /// **改回 §0.-11 那个「上界 = 接合区参考线」当场红。**
    /// </summary>
    [Fact]
    public void 门_界面法兰保温上界退回60_只有一份来源()
    {
        Assert.Equal(60.0, DesignSpec.FlangeInsulMaxMm, 12);
        Assert.True(DesignSpec.FlangeInsulMaxMm > WrapLimits.JointZoneMaxMm,
            "界面上界又被压回接合区参考线了 —— 那条线 2026-09-18 已不卡交付");

        string ui = Code("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("(decimal)DesignSpec.FlangeInsulMaxMm", ui);
        Assert.DoesNotContain("(decimal)WrapLimits.JointZoneMaxMm", ui);   // §0.-11 那一版的写法
        Assert.DoesNotContain("Num(20.0m, 0.0m, 60.0m, 0.5m, 1)", ui);     // 手抄一份 60 也不行
        // 默认值也取生产配方，不在界面另写一个 20
        Assert.Contains("(decimal)new DesignSpec().FlangeInsulMm", ui);
    }

    /// <summary>
    /// 退回值之三：<c>InsulationSearch.Options.DiscLayerMax</c> 退回 **40 层**（= 20 mm），
    /// 并**照实标「无出处、需定」** —— 参考行不再给它当出处，就不许假装它有出处。
    /// **改回 §0.-11 那个 <c>WrapLimits.MaxTurnsAtJoint</c> 当场红。**
    /// </summary>
    [Fact]
    public void 门_圆盘保温搜索层上界退回40_并照实标无出处()
    {
        Assert.Equal(40, new InsulationSearch.Options().DiscLayerMax);
        Assert.NotEqual(WrapLimits.MaxTurnsAtJoint, new InsulationSearch.Options().DiscLayerMax);

        string ins = Code("Pt_Optimize/Core/InsulationSearch.cs");
        Assert.Contains("public int DiscLayerMax = 40;", ins);
        Assert.DoesNotContain("public int DiscLayerMax = WrapLimits.MaxTurnsAtJoint;", ins);
        // 三个上界现在都是无出处的，报告抬头里要照实说
        Assert.Contains("无出处、需定", ins);
    }

    /// <summary>
    /// 参考线自己不许手抄一个 10：它必须 = 圈数 × 层厚（层厚改了它得跟着改），
    /// 而且判据本体只在 LineRunner 里被调一次，不许谁再就地判一遍。
    /// </summary>
    [Fact]
    public void 门_上界只有一份_源码钉死()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        Assert.Equal(WrapLimits.MaxTurnsAtJoint * InsulationSearch.LayerMm, cap, 12);
        Assert.Equal(20, WrapLimits.MaxTurnsAtJoint);
        Assert.Equal(10.0, cap, 12);            // 现行层厚 0.5 下就是 10 mm（用户 2026-09-17）

        string wl = Code("Pt_Optimize/Core/WrapLimits.cs");
        Assert.Contains("public const int MaxTurnsAtJoint = 20;", wl);
        Assert.Contains("public static double JointZoneMaxMm => MaxTurnsAtJoint * InsulationSearch.LayerMm;", wl);

        // 圈数提示只有一份写法（判据说明、保温方案表、安装报告、输出框都调它）
        Assert.Contains("public static string TurnsLine(", wl);
        Assert.Contains("public static double TurnsOf(double mm) => mm / InsulationSearch.LayerMm;", wl);

        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("checks.Add(WrapLimits.Judge(c, c.RuleSet));", lr);   // 决 104（2026-09-25）：调用带口径（决103 硬／决103前 参考），仍只调一次
        Assert.Equal(1, lr.Split("WrapLimits.Judge(").Length - 1);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 判据行为：参考行
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 贴着参考线 ⇒ 「在一次缠绕能缠的圈数以内」；超了 ⇒ **照实报圈数 + 提示预制保温块**，
    /// 两种情形都是 <see cref="CheckKind.Reference"/> —— 不卡交付。
    /// </summary>
    [Fact]
    public void 门_越界是参考行_报圈数并提示预制块_不判不可行()
    {
        double cap = WrapLimits.JointZoneMaxMm;

        var within = WrapLimits.Judge(Case(cap));
        Assert.Equal(CheckKind.Reference, within.Kind);
        Assert.True(within.Ok, $"贴着参考线 {cap} mm 应当在圈数以内，实为 {within.Actual}／{within.Limit}：{within.Note}");
        Assert.False(within.Undetermined);
        Assert.DoesNotContain(WrapLimits.PrefabNote, within.Note);

        // 10.5 mm = 21 圈：比参考线多一圈 —— 照实报，并点名到那一片
        var over21 = WrapLimits.Judge(Case(10.5));
        Assert.Equal(21.0, 10.5 / InsulationSearch.LayerMm, 9);   // 自证：10.5 就是 21 圈
        Assert.Equal(CheckKind.Reference, over21.Kind);
        Assert.False(over21.Ok);
        Assert.False(over21.Undetermined, "超了是**算得出来**的事实，不是判不了");
        Assert.Equal(10.5, over21.Actual, 9);
        Assert.Equal(cap, over21.Limit, 12);
        Assert.Equal("入口 圆盘保温", over21.Where);
        Assert.Contains(WrapLimits.PrefabNote, over21.Note);      // 「超过 20 圈现场需预制保温块」
        Assert.Contains("21 圈", over21.Note);
        Assert.Contains("不外推", over21.Note);                   // 球形仍然不算
        Assert.DoesNotContain("判不可行", over21.Note);

        // 生产默认（20 mm = 40 圈）也走这一支：照实报，不判死
        var atDefault = WrapLimits.Judge(Case(new DesignSpec().FlangeInsulMm));
        Assert.Equal(CheckKind.Reference, atDefault.Kind);
        Assert.Contains(WrapLimits.PrefabNote, atDefault.Note);
        Assert.Contains("40 圈", atDefault.Note);
    }

    /// <summary>圈数提示的写法只有一份，且照实折算（每层 = 每圈 = LayerMm）。</summary>
    [Fact]
    public void 门_圈数提示只有一份写法()
    {
        Assert.Equal(25.0, WrapLimits.TurnsOf(12.5), 9);

        string s = WrapLimits.TurnsLine("圆盘区", 12.5);
        Assert.Contains("圆盘区", s);
        Assert.Contains("12.5 mm", s);
        Assert.Contains("25 圈", s);
        Assert.Contains($"每层 {InsulationSearch.LayerMm:0.#} mm", s);
        Assert.Contains(WrapLimits.PrefabNote, s);

        // 20 圈以内不带那句提示
        Assert.DoesNotContain(WrapLimits.PrefabNote, WrapLimits.TurnsLine("圆盘区", 10.0));
        // 算不出来照实说，不许印一个圈数
        Assert.Contains("算不出来", WrapLimits.TurnsLine("圆盘区", double.NaN));
    }

    /// <summary>逐片圆盘保温：只有一片超 ⇒ 点名**那一片**，不是整线值。</summary>
    [Fact]
    public void 门_逐片越界要点名到那一片()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        var c = Case(5.0, new[] { 5.0, 5.0, cap + 2.0, 5.0 });
        var k = WrapLimits.Judge(c);

        Assert.Equal(CheckKind.Reference, k.Kind);
        Assert.False(k.Ok);
        Assert.Equal(cap + 2.0, k.Actual, 9);
        Assert.Equal("共用2 圆盘保温", k.Where);       // 第三片（入口/共用1/共用2/出口）
    }

    /// <summary>
    /// ★★ **管保温不进接合区清单**（用户 2026-09-17 原话「其它地方(舌板与管)好缠绕」）。
    ///
    /// 自证门：拿一份**管保温 15 mm（30 圈）＋ 端部额外 6 mm** 的算例 —— 管端总厚 21 mm，
    /// 远超参考线 10 mm —— 圆盘保温贴着参考线，本条照样报「圈数以内」。
    /// </summary>
    [Fact]
    public void 门_管保温不进接合区清单()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        var p = new DesignInputs { EndInsulExtraMm = 6.0, EndInsulLengthMm = 30.0 };
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = cap;        // 圆盘贴着参考线
        d.TubeInsulMm = 15.0;         // 管保温 30 圈
        var c = d.BuildCase(p);

        // 自证：拿来当样本的管保温真的超参考线（否则下面那个「以内」什么也证明不了）
        Assert.True(c.Base.Layer1.Enabled && c.Base.Layer1.ThicknessMm > cap,
            $"自证失效：算例里的管保温 {c.Base.Layer1.ThicknessMm} mm 没有超过参考线 {cap} mm");
        Assert.True(c.Base.Layer1.ThicknessMm + p.EndInsulExtraMm > 2 * cap,
            "自证失效：管端总厚没有明显超线，改回旧口径时这道门不会红");

        var k = WrapLimits.Judge(c);
        Assert.True(k.Ok, "管保温 15 mm 被算进了接合区 —— 用户原话是管好缠绕，不在此列");
        Assert.False(k.Undetermined);
        Assert.Equal(cap, k.Actual, 9);                 // 最厚的那一处仍是圆盘保温
        Assert.Contains("圆盘保温", k.Where);
    }

    /// <summary>
    /// **注射**：把管保温放回接合区清单（= 2026-09-17 那一版的口径）——
    /// 同一份输入当场越线。这一条是上面那道门的反面自证。
    /// </summary>
    [Fact]
    public void 注射_把管保温放回清单会越线_生产口径必须仍在圈数以内()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        var p = new DesignInputs { EndInsulExtraMm = 6.0, EndInsulLengthMm = 30.0 };
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = cap;
        d.TubeInsulMm = 15.0;
        var c = d.BuildCase(p);

        // 生产清单：只有圆盘，没有任何一项叫「管」
        var items = WrapLimits.JointItems(c);
        Assert.All(items, t => Assert.DoesNotContain("管", t.Where));
        Assert.Equal(c.FlangePlates.Length, items.Length);

        // 注射：照 2026-09-17 那一版把管端段补回清单，重判一次
        double tubeJoint = c.Base.Layer1.ThicknessMm + (p.EndInsulLengthMm > 1e-9 ? p.EndInsulExtraMm : 0.0);
        double injectedWorst = Math.Max(items.Max(t => t.Mm), tubeJoint);
        Assert.True(injectedWorst > cap + 1e-9,
            "注射无效：把管端补回清单之后仍没有越线 —— 这道门守的是空气");
        Assert.True(WrapLimits.Judge(c).Ok,
            "生产口径在同一份输入上也越线 ⇒ 管保温还在清单里（或另有一处在算它）");
    }

    /// <summary>
    /// **舌板与管身不在此列**（用户原话「其它地方(舌板与管)好缠绕」）——
    /// 自证门：求解器解出来那份 W08 的出口片舌保温 10.4 mm（21 圈）已超参考线，本条照样不看它。
    /// </summary>
    [Fact]
    public void 门_舌板保温不进接合区清单()
    {
        // 出处 deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt §「旋钮终值（逐片）」A 导航档。
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = WrapLimits.JointZoneMaxMm;
        d.TabInsulMm = new[] { 5.1, 2.3, 3.6, 10.4 };
        Assert.True(d.TabInsulMm.Max() > WrapLimits.JointZoneMaxMm,
            "自证失效：拿来当样本的舌保温没有一片超参考线，这道门就成了空守");

        var k = WrapLimits.Judge(d.BuildCase(new DesignInputs()));
        Assert.True(k.Ok, "舌板保温被算进了接合区 —— 用户原话是舌板与管好缠绕，不在此列");
        Assert.False(k.Undetermined);
        Assert.Equal(WrapLimits.JointZoneMaxMm, k.Actual, 9);   // 最厚的那一处仍是圆盘保温，不是 10.4 的舌保温
        Assert.DoesNotContain("舌", k.Where);
        Assert.DoesNotContain("10.4", k.Note);
    }

    /// <summary>算不出来 ⇒ **判不了**（判不了不算过，也不算不过），而且要说出是哪一处算不出来。</summary>
    [Fact]
    public void 门_算不出来要判不了不许当过()
    {
        var c = Case(double.NaN);
        var k = WrapLimits.Judge(c);

        Assert.Equal(CheckKind.Reference, k.Kind);
        Assert.True(k.Undetermined, "圆盘保温是 NaN 还报得出圈数 —— 那是凭初值报的");
        Assert.False(k.Ok, "判不了绝不许当过");
        Assert.True(double.IsNaN(k.Actual));
        Assert.Contains("无法判定", k.Note);
        Assert.Contains("入口 圆盘保温", k.Where);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 接到下游（新状态位默认没接上 —— 赋了值不等于用它的人读得到）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 分工况表、对照表、不吃管温场名单、界面全表，四处要一致。
    /// 决 104（2026-09-25）改写：改回口径（决103前）那张表两态仍是参考行（2026-09-18 口径逐位不变）；
    /// 生产口径（决103）那张表两态是硬安全线，对照表也印硬安全线（两张表各看各的，读的人不被骗）。
    /// </summary>
    [Fact]
    public void 门_降为参考行接到下游()
    {
        string key = LineResult.Key.WrapTurns;

        // 改回口径（决103前）：两态都是参考行（纯输入，与工况无关）—— 2026-09-18 口径逐位不变
        var row = LineResult.RequiredByStatePre103.Single(q => q.Prefix == key);
        Assert.Equal(CheckKind.Reference, row.GlassKind);
        Assert.Equal(CheckKind.Reference, row.EmptyTubeKind);
        Assert.False(row.NeedsRamp);
        Assert.DoesNotContain(key, LineResult.RequiredFor(false, CriteriaRuleSet.决103前).Select(q => q.Prefix));
        Assert.DoesNotContain(key, LineResult.RequiredFor(true, CriteriaRuleSet.决103前).Select(q => q.Prefix));
        Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(key, false, CriteriaRuleSet.决103前));
        Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(key, true, CriteriaRuleSet.决103前));

        // 生产口径（决103，决 104 起）：两态都是硬安全线，进必备名单
        var prod = LineResult.RequiredByState.Single(q => q.Prefix == key);
        Assert.Equal(CheckKind.HardSafety, prod.GlassKind);
        Assert.Equal(CheckKind.HardSafety, prod.EmptyTubeKind);
        Assert.Contains(key, LineResult.RequiredFor(false).Select(q => q.Prefix));
        Assert.Contains(key, LineResult.RequiredFor(true).Select(q => q.Prefix));

        // 对照表（工程师回查的那一张）：生产口径 = 硬安全线，改回口径的参考写法也在「意思」里
        var e = Criteria.All.Single(x => x.Key == key);
        Assert.True(e.Hard, "对照表还把它印成参考 —— 与生产分工况表两个答案，读的人必然被骗一次");
        Assert.Equal("mm", e.Unit);
        Assert.Contains("0.5", e.Means);                      // 一圈多厚，说在「意思」里
        Assert.Contains("20", e.Means);                       // 多少圈
        Assert.Contains("决 104", e.Means);                   // 升回硬判据的出处
        Assert.Contains("预制保温块", e.Means);               // 改回口径下超了现场怎么办
        Assert.Contains("不卡交付", e.Means);                 // 改回口径是参考，说在明面上

        // 界面不许出现判据代号 ⇒ 这条判据干脆不带代号
        Assert.Equal("", e.Code);
        Assert.Equal(key, Criteria.Plain(key));
        Assert.Contains(key, Criteria.Html(), StringComparison.Ordinal);

        // 不吃管温场：管散热表超界时它仍然算得出（纯输入）
        Assert.Contains(key, LineRunner.IndependentOfTubeField);
        Assert.Contains(key, LineRunner.IndependentOfTubeFieldFor(true));
    }

    /// <summary>
    /// ★★★ 行为门 ＋ **注射**：一条**不过**的「接合区保温缠得出来」
    /// <list type="bullet">
    ///   <item>改回口径（决103前，参考行）⇒ 整线照样 <c>AllOk</c>、<c>HardOk</c>，<c>Failed</c> 里不点它的名；</item>
    ///   <item>注射「改回硬安全线」⇒ 同一份结果当场变不可交付；</item>
    ///   <item>决 104（2026-09-25）：生产口径（决103）下它本来就是硬安全线 ⇒ 同一份不过的结果不可交付。</item>
    /// </list>
    /// 少了注射这一半，上面那一半可能只是因为**别的原因**恰好成立（比如判据压根没进表）。
    /// 手造的 LineResult 默认口径 = 决103前（逐位不变），生产口径那一半显式设 RuleSet。
    /// </summary>
    [Fact]
    public void 门_参考行不卡交付_注射改回硬安全线当场红()
    {
        LineResult Build(CheckKind wrapKind, bool wrapOk, CriteriaRuleSet rs = CriteriaRuleSet.决103前)
        {
            var checks = LineResult.RequiredByStateFor(rs).Select(q => new ConstraintOut
            {
                Name = q.Prefix, Unit = "—", Kind = q.GlassKind, Ok = true, Actual = 0, Limit = 1,
            }).ToArray();
            var wrap = checks.Single(c => c.Name == LineResult.Key.WrapTurns);
            wrap.Unit = "mm"; wrap.Limit = WrapLimits.JointZoneMaxMm;
            wrap.Actual = wrapOk ? WrapLimits.JointZoneMaxMm : WrapLimits.JointZoneMaxMm + 10.0;
            wrap.Ok = wrapOk; wrap.Kind = wrapKind;
            return new LineResult { Ok = true, Converged = true, RampChecked = true, Checks = checks, RuleSet = rs };
        }

        // 自证：其余全过时是可交付，否则下面两条恒真
        Assert.True(Build(CheckKind.Reference, true).AllOk);

        // 生产口径：接合区 20 mm（40 圈）超线，整线**照样可交付**
        var asShipped = Build(CheckKind.Reference, false);
        Assert.True(asShipped.AllOk,
            "接合区超了一次缠绕的圈数就把整线判成不可交付 —— 用户 2026-09-18：只给材质与厚度方案，包法由现场定");
        Assert.True(asShipped.HardOk);
        Assert.DoesNotContain(asShipped.Failed, s => s.StartsWith(LineResult.Key.WrapTurns, StringComparison.Ordinal));

        // 注射：Kind 改回 HardSafety（= §0.-11／§0.-12 那一版），同一份结果当场不可交付
        var injected = Build(CheckKind.HardSafety, false);
        Assert.False(injected.AllOk,
            "把它改回硬安全线之后整线还报可交付 —— 那说明上面那半条什么也没证明");
        Assert.Contains(injected.Failed, s => s.StartsWith(LineResult.Key.WrapTurns, StringComparison.Ordinal));

        // 决 104：生产口径（决103）下不用注射 —— 分工况表本身就是硬安全线，同一份不过的结果不可交付；过了就可交付
        var prod = Build(CheckKind.HardSafety, false, CriteriaRuleSet.决103);
        Assert.False(prod.HardOk, "决103 口径下圆盘保温超上限还报硬安全线全过 —— 决 104 没落地");
        Assert.Contains(prod.Failed, s => s.StartsWith(LineResult.Key.WrapTurns, StringComparison.Ordinal));
        Assert.True(Build(CheckKind.HardSafety, true, CriteriaRuleSet.决103).HardOk);
    }

    /// <summary>
    /// 改口的**出处**要写在生产侧（报告与界面引的是同一份原话），且判据说明里明说不卡交付。
    /// </summary>
    [Fact]
    public void 门_改口出处写在生产侧()
    {
        Assert.Contains("2026-09-18", WrapLimits.PlanOnlyNote);
        Assert.Contains("还是只给材质保温厚度方案就行", WrapLimits.PlanOnlyNote);
        Assert.Contains("不卡交付", WrapLimits.PlanOnlyNote);
        Assert.Contains("2026-09-17", WrapLimits.SourceNote);   // 圈数那句原话还在

        // 判据本体的说明里带着这两份出处
        var k = WrapLimits.Judge(Case(new DesignSpec().FlangeInsulMm));
        Assert.Contains(WrapLimits.PlanOnlyNote, k.Note);
        Assert.Contains(WrapLimits.SourceNote, k.Note);
    }
}
