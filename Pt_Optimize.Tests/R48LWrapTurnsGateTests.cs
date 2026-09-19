using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路快门：**接合区的保温缠不缠得出来** —— 2026-09-17，Opus 5
//
//  用户 2026-09-17 现场原话：
//    「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕(会渐成球形)」
//    「圆盘与管子接触区都是20圈以下，其它地方(舌板与管)好缠绕」
//  每圈 = InsulationSearch.LayerMm = 0.5 mm ⇒ **接合区上限 10 mm**。
//
//  病：APP 的圆盘保温默认写着 **20 mm（= 40 圈）**，而这个 20 在仓库里**查不到出处**
//      （R48DiscInsulLeverTests 当时就点了名）—— 也就是说现役两份可交付设计一直站在一个
//      **现场缠不出来**的数上，而没有任何东西会拦。
//
//  这几道门守的是：
//    ① 默认 = 上限 = 20 圈 × 每圈厚，**改回 20 当场红**；
//    ② 上限只有 WrapLimits 一份（源码钉死）：DesignSpec 不许再写 20、界面控件上限与保温搜索层上界都取它；
//    ③ 公式不手抄 10（层厚改了跟着改）；
//    ④ 越界 ⇒ 判**不可行**并点名到底是哪一片，不是判不了、更不是过；
//    ⑤ 算不出来 ⇒ 判不了（判不了不算过）；
//    ⑥ **管保温与舌板保温不受本限** —— 自证门：管保温 15 mm（30 圈）、舌保温 18.7 mm 照样过；
//    ⑦ 新判据接到下游（分工况表、对照表、不吃管温场名单、AllOk 与点名、界面查得到全名）；
//    ⑧ 注射：把圆盘保温改回 20 mm，同一份 W08 当场变「不可行」——证明这道门不是空气。
//
//  ⚠ 「越界不外推」是这条判据的骨头：缠过 20 圈会渐成球形，那个形状本程序**没有模型**
//    （保温外形不再是等厚层，散热面积与形状因子全变）。硬算下去给出的是「看起来正常的错数」。
//
//  ★★ 2026-09-18，Opus 5 **改回只卡圆盘**：2026-09-17 那一版把「管端段（管保温 + 端部额外保温）」
//    也塞进了接合区清单，理由是「模型里管保温沿全长等厚，管端那一小段跟着厚」。
//    那与用户原话「其它地方(舌板与管)好缠绕」**相反** —— 拿模型的缺口去给现场加一条现场没有的限制，
//    会把一批现场缠得出来的设计判成不可行。⇒ 清单里只剩逐片圆盘保温；
//    模型缺口（管端渐变减薄没建、方向上模型偏乐观）记在 WrapLimits.ZoneNote 里并标明是推理。
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
    //  ① 默认与上限
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 默认圆盘保温 = 接合区上限 = 20 圈 × 每圈厚。
    /// **改回 20 mm（40 圈）⇒ 这道门当场红**，那正是本轮要拦住的那个数。
    /// </summary>
    [Fact]
    public void 门_圆盘保温默认就是接合区上限_改回20当场红()
    {
        double cap = WrapLimits.JointZoneMaxMm;

        // 上限自己不许是手抄的 10：它必须 = 圈数 × 层厚（层厚改了它得跟着改）
        Assert.Equal(WrapLimits.MaxTurnsAtJoint * InsulationSearch.LayerMm, cap, 12);
        Assert.Equal(20, WrapLimits.MaxTurnsAtJoint);
        Assert.Equal(10.0, cap, 12);            // 现行层厚 0.5 下就是 10 mm（用户 2026-09-17）

        // 默认 = 上限
        Assert.Equal(cap, new DesignSpec().FlangeInsulMm, 12);
        // 反面写死：20 是旧的、无出处的那个数，默认绝不许再等于它
        Assert.NotEqual(20.0, new DesignSpec().FlangeInsulMm);

        // 内置设计一份都不许自带一个超限的圆盘保温（它们不写这个字段 ⇒ 全走默认）
        foreach (var d in new[] { DesignSpec.W08, DesignSpec.W06 })
            Assert.True(d.FlangeInsulMm <= cap + 1e-9,
                $"内置设计「{d.Name}」的圆盘保温 {d.FlangeInsulMm} mm 超过接合区上限 {cap} mm");
    }

    /// <summary>上限只有一份 —— 谁再手抄一个 10 或 20，这道门就该红。</summary>
    [Fact]
    public void 门_上界只有一份_源码钉死()
    {
        string wl = Code("Pt_Optimize/Core/WrapLimits.cs");
        Assert.Contains("public const int MaxTurnsAtJoint = 20;", wl);
        Assert.Contains("public static double JointZoneMaxMm => MaxTurnsAtJoint * InsulationSearch.LayerMm;", wl);

        // 设计侧：默认不许再写字面量 20
        string ds = Code("Pt_Optimize/Core/DesignSpec.cs");
        Assert.DoesNotContain("FlangeInsulMm = 20.0", ds);
        Assert.Contains("public double FlangeInsulMm = 10.0;", ds);

        // 界面控件的上限取生产配方，不自带一个数
        string ui = Code("Pt_Optimize/UI/LineDesignPage.cs");
        Assert.Contains("(decimal)WrapLimits.JointZoneMaxMm", ui);
        Assert.DoesNotContain("Num(20.0m, 0.0m, 60.0m, 0.5m, 1)", ui);

        // 保温搜索的圆盘层上界也取它（原来是无出处的 40）
        string ins = Code("Pt_Optimize/Core/InsulationSearch.cs");
        Assert.Contains("public int DiscLayerMax = WrapLimits.MaxTurnsAtJoint;", ins);
        Assert.Equal(WrapLimits.MaxTurnsAtJoint, new InsulationSearch.Options().DiscLayerMax);

        // 判据本体只在 LineRunner.Judge 里被调一次，不许谁再就地判一遍
        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("checks.Add(WrapLimits.Judge(c));", lr);
        Assert.Equal(1, lr.Split("WrapLimits.Judge(").Length - 1);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 判据行为
    // ────────────────────────────────────────────────────────────────────

    /// <summary>贴着上限 ⇒ 过；超一格（0.1 mm，比一圈还薄）⇒ **不可行**，且点名是哪一片。</summary>
    [Fact]
    public void 门_越界判不可行并点名()
    {
        double cap = WrapLimits.JointZoneMaxMm;

        var ok = WrapLimits.Judge(Case(cap));
        Assert.True(ok.Ok, $"贴着上限 {cap} mm 应当过，实为 {ok.Actual}／{ok.Limit}：{ok.Note}");
        Assert.False(ok.Undetermined);
        Assert.Equal(CheckKind.HardSafety, ok.Kind);

        var bad = WrapLimits.Judge(Case(cap + 0.1));
        Assert.False(bad.Ok, "超过接合区上限还报「过」—— 这正是本门要拦的");
        Assert.False(bad.Undetermined, "越界是**不可行**，不是判不了：数算得出来，是现场缠不出来");
        Assert.Equal(cap + 0.1, bad.Actual, 9);
        Assert.Equal(cap, bad.Limit, 12);
        Assert.True(bad.LessIsBetter);
        Assert.Contains("缠不出来", bad.Note);
        Assert.Contains("不外推", bad.Note);

        // 10.5 mm = 21 圈：比上限多一圈，必须不过并点名到那一片
        var over21 = WrapLimits.Judge(Case(10.5));
        Assert.Equal(21.0, 10.5 / InsulationSearch.LayerMm, 9);   // 自证：10.5 就是 21 圈
        Assert.False(over21.Ok, "圆盘保温 10.5 mm = 21 圈，超过现场能缠的 20 圈，判据却报「过」");
        Assert.False(over21.Undetermined);
        Assert.Equal(10.5, over21.Actual, 9);
        Assert.Equal("入口 圆盘保温", over21.Where);
    }

    /// <summary>逐片圆盘保温：只有一片超 ⇒ 点名**那一片**，不是整线值。</summary>
    [Fact]
    public void 门_逐片越界要点名到那一片()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        var c = Case(5.0, new[] { 5.0, 5.0, cap + 2.0, 5.0 });
        var k = WrapLimits.Judge(c);

        Assert.False(k.Ok);
        Assert.Equal(cap + 2.0, k.Actual, 9);
        Assert.Equal("共用2 圆盘保温", k.Where);       // 第三片（入口/共用1/共用2/出口）
    }

    /// <summary>
    /// ★★ 2026-09-18，Opus 5：**管保温不进接合区清单**（用户 2026-09-17 原话「其它地方(舌板与管)好缠绕」）。
    ///
    /// 自证门（少了自证这一半，这道门就是空守）：拿一份**管保温 15 mm（30 圈）＋ 端部额外 6 mm** 的算例 ——
    /// 管端总厚 21 mm，远超接合区上限 10 mm —— 圆盘保温贴着上限，本判据照样**过**。
    /// 上一版（2026-09-17）在这一份输入上会判不可行，那是拿模型缺口去给现场加限制。
    /// </summary>
    [Fact]
    public void 门_管保温不进接合区清单()
    {
        double cap = WrapLimits.JointZoneMaxMm;
        var p = new DesignInputs { EndInsulExtraMm = 6.0, EndInsulLengthMm = 30.0 };
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = cap;        // 圆盘贴着上限
        d.TubeInsulMm = 15.0;         // 管保温 30 圈
        var c = d.BuildCase(p);

        // 自证：拿来当样本的管保温真的超上限（否则下面那个「过」什么也证明不了）
        Assert.True(c.Base.Layer1.Enabled && c.Base.Layer1.ThicknessMm > cap,
            $"自证失效：算例里的管保温 {c.Base.Layer1.ThicknessMm} mm 没有超过上限 {cap} mm");
        Assert.True(c.Base.Layer1.ThicknessMm + p.EndInsulExtraMm > 2 * cap,
            "自证失效：管端总厚没有明显超限，改回旧口径时这道门不会红");

        var k = WrapLimits.Judge(c);
        Assert.True(k.Ok, "管保温 15 mm 把整条判不可行 —— 用户原话是管好缠绕，不受本限");
        Assert.False(k.Undetermined);
        Assert.Equal(cap, k.Actual, 9);                 // 最厚的那一处仍是圆盘保温
        Assert.Contains("圆盘保温", k.Where);
    }

    /// <summary>
    /// **注射**：把管保温放回接合区清单（= 2026-09-17 那一版的口径）——
    /// 同一份输入当场变「不可行」。这一条是上面那道门的反面自证：
    /// 清单里要是还留着管端那一项，这里算出来的「旧口径判词」与生产判词就会一致，断言当场红。
    /// </summary>
    [Fact]
    public void 注射_把管保温放回清单会让这份设计变不可行_生产口径必须仍然过()
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
            "注射无效：把管端补回清单之后仍没有越界 —— 这道门守的是空气");
        Assert.True(WrapLimits.Judge(c).Ok,
            "生产口径在同一份输入上也不可行 ⇒ 管保温还在清单里（或另有一处在卡它）");
    }

    /// <summary>
    /// **舌板与管身不受本限**（用户原话「其它地方(舌板与管)好缠绕」）——
    /// 自证门：W08 内置的舌保温是 18.7 mm（37 圈），远超接合区上限，本判据照样过。
    /// 少了这道门，哪天有人把舌保温也塞进接合区清单，只会让一批可行设计凭空变不可行。
    /// </summary>
    [Fact]
    public void 门_舌板保温不进接合区清单()
    {
        // 取**求解器真解出来的那份 W08**（4245 g）的逐片舌保温：出口片 10.4 mm = 21 圈，已经超过接合区上限。
        // 出处 deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt §「旋钮终值（逐片）」A 导航档。
        var d = DesignSpec.W08.Clone();
        d.FlangeInsulated = true;
        d.FlangeInsulMm = WrapLimits.JointZoneMaxMm;
        d.TabInsulMm = new[] { 5.1, 2.3, 3.6, 10.4 };
        Assert.True(d.TabInsulMm.Max() > WrapLimits.JointZoneMaxMm,
            "自证失效：拿来当样本的舌保温没有一片超上限，这道门就成了空守");

        var k = WrapLimits.Judge(d.BuildCase(new DesignInputs()));
        Assert.True(k.Ok, "舌板保温超了 10 mm 就把整条判不可行 —— 用户原话是舌板与管好缠绕，不受本限");
        Assert.False(k.Undetermined);
        Assert.Equal(WrapLimits.JointZoneMaxMm, k.Actual, 9);   // 最厚的那一处仍是圆盘保温，不是 10.4 的舌保温
        Assert.DoesNotContain("舌", k.Where);
        Assert.DoesNotContain("10.4", k.Note);
    }

    /// <summary>算不出来 ⇒ **判不了**（判不了不算过），而且要说出是哪一处算不出来。</summary>
    [Fact]
    public void 门_算不出来要判不了不许当过()
    {
        var c = Case(double.NaN);
        var k = WrapLimits.Judge(c);

        Assert.True(k.Undetermined, "圆盘保温是 NaN 还报得出结论 —— 那是凭初值判的");
        Assert.False(k.Ok, "判不了绝不许当过");
        Assert.True(double.IsNaN(k.Actual));
        Assert.Contains("无法判定", k.Note);
        Assert.Contains("入口 圆盘保温", k.Where);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 接到下游（新状态位默认没接上 —— 赋了值不等于用它的人读得到）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>分工况表、对照表、不吃管温场名单、界面全表，四处都要有它。</summary>
    [Fact]
    public void 门_新判据接到下游()
    {
        string key = LineResult.Key.WrapTurns;

        // 分工况表：两态都卡交付（纯输入，与工况无关）
        var row = LineResult.RequiredByState.Single(q => q.Prefix == key);
        Assert.Equal(CheckKind.HardSafety, row.GlassKind);
        Assert.Equal(CheckKind.HardSafety, row.EmptyTubeKind);
        Assert.False(row.NeedsRamp);
        Assert.Contains(key, LineResult.RequiredFor(false).Select(q => q.Prefix));
        Assert.Contains(key, LineResult.RequiredFor(true).Select(q => q.Prefix));

        // 对照表（工程师回查的那一张）
        var e = Criteria.All.Single(x => x.Key == key);
        Assert.True(e.Hard);
        Assert.Equal("mm", e.Unit);
        Assert.Contains("0.5", e.Means);                      // 一圈多厚，说在「意思」里
        Assert.Contains("20", e.Means);                       // 多少圈

        // 界面不许出现判据代号 ⇒ 这条新判据干脆不带代号
        Assert.Equal("", e.Code);
        Assert.Equal(key, Criteria.Plain(key));
        Assert.Contains(key, Criteria.Html(), StringComparison.Ordinal);

        // 不吃管温场：管散热表超界时它仍然判得了（纯输入）
        Assert.Contains(key, LineRunner.IndependentOfTubeField);
        Assert.Contains(key, LineRunner.IndependentOfTubeFieldFor(true));
    }

    /// <summary>
    /// 行为门：一条不过的「接合区保温缠得出来」必须让整线 **AllOk 为假**并在 Failed 里被点名。
    /// 判据接上了而 AllOk 读不到它，就是「赋了值≠用它的人读得到」那个病。
    /// </summary>
    [Fact]
    public void 门_不过时整线判不可行并点名()
    {
        LineResult Build(bool wrapOk)
        {
            var checks = LineResult.RequiredByState.Select(q => new ConstraintOut
            {
                Name = q.Prefix, Unit = "—", Kind = q.GlassKind, Ok = true, Actual = 0, Limit = 1,
            }).ToArray();
            var wrap = checks.Single(c => c.Name == LineResult.Key.WrapTurns);
            wrap.Unit = "mm"; wrap.Limit = WrapLimits.JointZoneMaxMm;
            wrap.Actual = wrapOk ? WrapLimits.JointZoneMaxMm : WrapLimits.JointZoneMaxMm + 2.0;
            wrap.Ok = wrapOk;
            return new LineResult { Ok = true, Converged = true, RampChecked = true, Checks = checks };
        }

        Assert.True(Build(true).AllOk, "自证：其余全过时应当是「可交付」，否则下面那条恒真");
        var bad = Build(false);
        Assert.False(bad.AllOk, "接合区缠不出来，整线还报可交付");
        Assert.Contains(bad.Failed, s => s.StartsWith(LineResult.Key.WrapTurns, StringComparison.Ordinal));
    }

    /// <summary>
    /// **注射**：把圆盘保温改回旧的 20 mm（40 圈），同一份 W08 当场变「缠不出来」。
    /// 这一条是上面所有门的自证 —— 门若是空气，这里会绿。
    /// </summary>
    [Fact]
    public void 注射_圆盘保温改回20当场不可行()
    {
        var now = WrapLimits.Judge(Case(new DesignSpec().FlangeInsulMm));
        Assert.True(now.Ok, "生产默认自己就不过 —— 那不是门的问题，是默认设错了");

        var old = WrapLimits.Judge(Case(20.0));
        Assert.False(old.Ok, "圆盘保温 20 mm = 40 圈，现场缠不出来，判据却报「过」");
        Assert.Equal(20.0, old.Actual, 9);
        Assert.Contains("40", $"{old.Actual / InsulationSearch.LayerMm:0.#}");   // 40 圈
    }
}
