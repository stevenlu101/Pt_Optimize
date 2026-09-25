using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ 决 103（业主 2026-09-24，判据换向）的门。
///
/// 快门（合成数据，不解场）：
///   · 门f 管 J 限值 11（参数表默认、取小规则、改回 12；设计电流与升温全程读同一个限值）；
///   · 门_构造 新两条（法兰最热处高出管接触处温度、管接触处流入法兰的净热流）取哪一片、方向、判不了；
///   · 门_分工况表 生产表的硬／参考、改回表与改前逐项相同、ApplyStateCriteria 两种口径；
///   · 门_分派 求解器分派表按口径取、新判据逐片裕度的方向与限值来源、认证误差；
///   · 门_升温 升温全程汇总在决 103 口径下卡热稳定、改回口径不卡；
///   · 门_全格精算合成 ApplyLocalStabFullGrid 换值、重建那一行、判不了不动、改回不做；
///   · 门_拒答 加密复算与保温搜索在决 103 口径下拒答、改回口径照旧；
///   · 门e 源码门：Core 里引用三条旧判据的每一处都在名单里并写明为什么不是当硬判据用。
/// 慢门（整线解，带开跑时刻的证据档）：
///   · 门a 改回逐位：决103前 口径下 W08 现役（盘 30）与 R = w = 45 两例的整线判据表（名、级、值、限值、过否、判不了、位置、说明全文）、逐片、逐段、说明与改前树转储逐行相同（去挂钟）；
///   · 门b 新判据只印＋归因：两例在决 103 口径下印新两条与热稳定的值与判词，与旧三条及旧判法两条并列；决103前 vs 决103 判词并列（「开 − 关」）；
///   · 门c 局部热稳定全格：决 99 案例（W08 判决网格、细区半径 59）报出值 = 逐片重建热解后全格逐格精算的最小值（逐位）。
/// </summary>
public class R48CriteriaSwapGateTests
{
    private readonly ITestOutputHelper _o;
    public R48CriteriaSwapGateTests(ITestOutputHelper o) { _o = o; }

    static string X(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string F3(double v) => double.IsNaN(v) ? "NaN" : v.ToString("0.000", CultureInfo.InvariantCulture);
    static string Load() { try { return File.ReadAllText("/proc/loadavg").Trim(); } catch { return "未查到"; } }
    static string Sh(string cmd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return "未查到（git 10 s 没返回）"; }   // 带上限（SubprocessMustNotHangTests）
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch (Exception ex) { return "未查到（" + ex.GetType().Name + "）"; }
    }
    static string Src(string rel) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel));

    // ═══════════════════════════════════════════════ 合成整线（三段四片）
    static (SegmentOut[] Segs, FlangeOut[] Fl) Line()
    {
        var segs = new[]
        {
            new SegmentOut { Name = "HC1", SetpointC = 1150, TRootAC = 1146.0, TRootBC = 1112.0 },
            new SegmentOut { Name = "HC2", SetpointC = 1080, TRootAC = 1113.5, TRootBC = 1071.5 },
            new SegmentOut { Name = "HC3", SetpointC = 1050, TRootAC = 1064.5, TRootBC = 1041.0 },
        };
        var fl = new[]
        {
            new FlangeOut { Name = "入口",    TMaxC = 1152.0, TDiscMaxC = 1149.0, TTabMaxC = 1152.0, TRootC = 1146.0, QFromTubeW = -3.0 },
            new FlangeOut { Name = "HC1|HC2", TMaxC = 1125.5, TDiscMaxC = 1125.5, TTabMaxC = 1110.0, TRootC = 1113.5, QFromTubeW = +2.5, Shared = true },
            new FlangeOut { Name = "HC2|HC3", TMaxC = 1069.0, TDiscMaxC = 1069.0, TTabMaxC = 1060.0, TRootC = 1071.5, QFromTubeW = -0.5, Shared = true },
            new FlangeOut { Name = "出口",    TMaxC = 1040.0, TDiscMaxC = 1040.0, TTabMaxC = 1038.0, TRootC = 1041.0, QFromTubeW = +0.25 },
        };
        return (segs, fl);
    }

    // ═══════════════════════════════════════════════ 门f：管 J 限值 11
    [Fact]
    public void 门f_管J限值11_参数表默认_取小规则_改回12()
    {
        var p = new DesignInputs();
        Assert.Equal(CriteriaRuleSet.决103, p.CriteriaRuleSet);                 // 生产不传 = 新口径
        Assert.Equal(12.0, p.TubeJAllowAPerMm2);                                 // 原许用值一位不动（照印对照）
        Assert.Equal(11.0, p.TubeJUseCapAPerMm2);                                // 业主 09-24「J < 11」
        Assert.Equal(11.0, p.TubeJLimitAPerMm2);
        Assert.Equal(11.0e6, p.TubeJAllow);                                      // 升温集总模型的电流上限读同一个数
        p.TubeJAllowAPerMm2 = 10.0;
        Assert.Equal(10.0, p.TubeJLimitAPerMm2);                                 // 两条上限同时成立 ⇒ 取小
        var q = new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 };
        Assert.Equal(12.0, q.TubeJLimitAPerMm2);                                 // 改回 = 原许用值（逐位同改前）
        Assert.Equal(12.0e6, q.TubeJAllow);

        // 设计电流（判据「① 升温」的限值）：决 103 = 11，改回 = 12
        var d = R48NMeshGateTests.Design("W08").Clone().Fit();
        var a = DesignCurrent.ForLine(d, new DesignInputs());
        var b = DesignCurrent.ForLine(d, new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 });
        Assert.Equal(11.0, a.TubeJAllowAPerMm2);
        Assert.Equal(12.0, b.TubeJAllowAPerMm2);
        Assert.Equal(a.RampTubeJPeakAPerMm2, b.RampTubeJPeakAPerMm2);           // 所需电流本身与限值无关（W08 没被截住）
        _o.WriteLine($"W08 升温所需管 J 峰值 {a.RampTubeJPeakAPerMm2:0.000}（决 103 限 {a.TubeJAllowAPerMm2}、改回限 {b.TubeJAllowAPerMm2}）");

        // 源码：每个读管 J 限值的地方都走 TubeJLimitAPerMm2（原许用值只在对照行与取小规则里读）
        string lr = Src("Pt_Optimize/Core/LineRunner.cs"), rs = Src("Pt_Optimize/Core/RampSweep.cs"), dc = Src("Pt_Optimize/Core/DesignCurrent.cs");
        Assert.Contains("Limit = c.Base.TubeJLimitAPerMm2, Where = worstJt.Name", lr);
        Assert.Contains("MaxCurrentA = c.Base.TubeJLimitAPerMm2 * tubeAreaMm2", lr);
        Assert.Contains("double tubeJLimit = lc.Base.TubeJLimitAPerMm2;", rs);
        Assert.Contains("double iCap = p.TubeJLimitAPerMm2 * tubeAreaMm2;", dc);
        Assert.DoesNotContain("p.TubeJAllowAPerMm2 * tubeAreaMm2", dc);
        // 对照行：名字、参考量、不在分工况表里（不卡交付）
        Assert.StartsWith("·", LineResult.Key.TubeJPre103);
        Assert.Null(LineResult.StateKindOf(LineResult.Key.TubeJPre103, false));
        Assert.Contains("Name = LineResult.Key.TubeJPre103, Unit = \"A/mm²\", Kind = CheckKind.Reference", lr);
    }

    // ═══════════════════════════════════════════════ 门：新两条的构造（合成）
    [Fact]
    public void 门_新两条构造_取最差片_方向_判不了()
    {
        var (segs, fl) = Line();
        var lc = new LineCase { Base = new DesignInputs() };
        var (hot, cold) = LineRunner.ContactChecks(lc, segs, fl);

        // 热侧：逐片 TMax − TRoot = +6.0 / +12.0 / −2.5 / −1.0 ⇒ 最差 HC1|HC2 12.0 > 10 ⇒ 不过
        Assert.Equal(LineResult.Key.HotOverContact, hot.Name);
        Assert.Equal(12.0, hot.Actual, 12);
        Assert.Equal(10.0, hot.Limit);
        Assert.Equal("HC1|HC2", hot.Where);
        Assert.False(hot.Ok); Assert.False(hot.Undetermined);
        Assert.Equal(CheckKind.HardSafety, hot.Kind);
        Assert.Contains("两段共用的那一片取两侧段端温度的较高者", hot.Note);
        // 限值只从参数表读：改成 12.5 ⇒ 过
        var lc2 = new LineCase { Base = new DesignInputs { HotOverContactAllowK = 12.5 } };
        Assert.True(LineRunner.ContactChecks(lc2, segs, fl).Hot.Ok);

        // 冷侧：逐片 QFromTube = −3.0 / +2.5 / −0.5 / +0.25 ⇒ 最差（最大）HC1|HC2 +2.5 > 0 ⇒ 不过；限值 0 W
        Assert.Equal(LineResult.Key.TubeToFlangeHeat, cold.Name);
        Assert.Equal(2.5, cold.Actual, 12);
        Assert.Equal(0.0, cold.Limit);
        Assert.True(cold.LessIsBetter);
        Assert.False(cold.Ok);
        // 全部 ≤ 0 ⇒ 过（恰为 0 也过：不许由管流入法兰，0 不是流入）
        foreach (var f in fl) f.QFromTubeW = Math.Min(f.QFromTubeW, 0.0);
        var (_, cold0) = LineRunner.ContactChecks(lc, segs, fl);
        Assert.True(cold0.Ok);
        Assert.Equal(0.0, cold0.Actual);

        // 任何一片判不了 ⇒ 整条判不了，点名
        fl[2].TMaxC = double.NaN; fl[3].QFromTubeW = double.NaN;
        var (hotN, coldN) = LineRunner.ContactChecks(lc, segs, fl);
        Assert.True(hotN.Undetermined); Assert.False(hotN.Ok); Assert.Equal("HC2|HC3", hotN.Where);
        Assert.True(coldN.Undetermined); Assert.False(coldN.Ok); Assert.Equal("出口", coldN.Where);
        // 片数与段数对不上 ⇒ 判不了
        var (hotB, coldB) = LineRunner.ContactChecks(lc, segs, fl.Take(3).ToArray());
        Assert.True(hotB.Undetermined && coldB.Undetermined);

        // 名字不许与现有任何一条互为前缀（Find 按前缀取）
        var keys = typeof(LineResult.Key).GetFields().Where(x => x.IsLiteral).Select(x => (string)x.GetRawConstantValue()!).ToArray();
        foreach (var nk in new[] { LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat, LineResult.Key.TubeJPre103 })
            foreach (var k in keys.Where(k => k != nk))
                Assert.False(nk.StartsWith(k, StringComparison.Ordinal) || k.StartsWith(nk, StringComparison.Ordinal), $"「{nk}」与「{k}」互为前缀");
        // 不带代号
        Assert.DoesNotContain(LineResult.Key.HotOverContact, c => Criteria.CodeChars.Contains(c));
        Assert.DoesNotContain(LineResult.Key.TubeToFlangeHeat, c => Criteria.CodeChars.Contains(c));
    }

    // ═══════════════════════════════════════════════ 门：分工况表（生产口径与改回）
    /// <summary>改前树（cc49836）的分工况表，逐项抄自改前源码 —— 改回口径必须与它逐项相同。</summary>
    static readonly (string, CheckKind, CheckKind, bool)[] Pre103Table =
    {
        (LineResult.Key.Ramp,        CheckKind.HardSafety, CheckKind.HardSafety, false),
        (LineResult.Key.NetFlux,     CheckKind.HardSafety, CheckKind.Reference,  false),
        (LineResult.Key.HotOverTc,   CheckKind.HardSafety, CheckKind.Reference,  false),
        (LineResult.Key.FreeTab,     CheckKind.HardSafety, CheckKind.HardSafety, false),
        (LineResult.Key.DiscCover,   CheckKind.HardSafety, CheckKind.HardSafety, false),
        (LineResult.Key.WrapTurns,   CheckKind.Reference,  CheckKind.Reference,  false),
        (LineResult.Key.TubeJ,       CheckKind.HardSafety, CheckKind.HardSafety, false),
        (LineResult.Key.SectionJ,    CheckKind.HardSafety, CheckKind.HardSafety, false),
        (LineResult.Key.ColdUnderTc, CheckKind.HardSafety, CheckKind.Reference,  false),
    };

    [Fact]
    public void 门_分工况表_生产口径换向_改回与改前逐项相同()
    {
        Assert.Equal(Pre103Table, LineResult.RequiredByStateFor(CriteriaRuleSet.决103前));
        Assert.Same(LineResult.RequiredByState, LineResult.RequiredByStateFor(CriteriaRuleSet.决103));

        string[] Hard(bool et, CriteriaRuleSet rs) => LineResult.RequiredFor(et, rs).Where(q => q.Kind == CheckKind.HardSafety)
            .Select(q => q.Prefix).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var glass = Hard(false, CriteriaRuleSet.决103);
        var want = new[] { LineResult.Key.Ramp, LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat, LineResult.Key.FreeTab,
                           LineResult.Key.DiscCover, LineResult.Key.TubeJ, LineResult.Key.SectionJ, LineResult.Key.FlangeStab, LineResult.Key.LocalStab }
                   .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(want, glass);
        // 三条旧判据两态都只作参考
        foreach (var k in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux })
        {
            Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(k, false));
            Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(k, true));
            Assert.Equal(CheckKind.HardSafety, LineResult.StateKindOf(k, false, CriteriaRuleSet.决103前));
        }
        // 空管到温稳态只卡电流密度与几何闭式（全局方案 1.3 节）
        var empty = Hard(true, CriteriaRuleSet.决103);
        Assert.Equal(new[] { LineResult.Key.Ramp, LineResult.Key.FreeTab, LineResult.Key.DiscCover, LineResult.Key.TubeJ, LineResult.Key.SectionJ }
                         .OrderBy(x => x, StringComparer.Ordinal).ToArray(), empty);

        // ApplyStateCriteria：同一组构造出来的判据条目，两种口径
        ConstraintOut[] Rows() => new[]
        {
            new ConstraintOut { Name = LineResult.Key.HotOverTc, Kind = CheckKind.HardSafety, Note = "原注" },
            new ConstraintOut { Name = LineResult.Key.HotOverContact, Kind = CheckKind.HardSafety, Note = "原注" },
            new ConstraintOut { Name = LineResult.Key.LocalStab, Kind = CheckKind.Reference, Note = "原注" },
        };
        var g = Rows(); LineRunner.ApplyStateCriteria(false, g, CriteriaRuleSet.决103);
        Assert.Equal(CheckKind.Reference, g[0].Kind); Assert.StartsWith(CriteriaRules.DowngradeNote, g[0].Note);
        Assert.Equal(CheckKind.HardSafety, g[1].Kind); Assert.Equal("原注", g[1].Note);
        Assert.Equal(CheckKind.HardSafety, g[2].Kind); Assert.StartsWith(CriteriaRules.UpgradeNote, g[2].Note);
        var e = Rows(); LineRunner.ApplyStateCriteria(true, e, CriteriaRuleSet.决103);
        Assert.StartsWith(CriteriaRules.DowngradeNote, e[0].Note);                          // 带玻璃也只作参考 ⇒ 口径换向的说明
        Assert.Equal(CheckKind.Reference, e[1].Kind); Assert.StartsWith(LineResult.StateDowngradeNote(true), e[1].Note);   // 只在空管态降级
        Assert.Equal(CheckKind.Reference, e[2].Kind); Assert.Equal("原注", e[2].Note);
        var old = Rows(); LineRunner.ApplyStateCriteria(false, old, CriteriaRuleSet.决103前);
        Assert.Equal(CheckKind.HardSafety, old[0].Kind); Assert.Equal("原注", old[0].Note);  // 改回：带玻璃稳态一条都不动
        Assert.Equal(CheckKind.Reference, old[2].Kind);

        // 手造的结果默认按改前名单（与 EmptyTube 同一个约定）；生产结果由 RunOnce 写口径（源码）
        Assert.Equal(CriteriaRuleSet.决103前, new LineResult().RuleSet);
        Assert.Contains("EmptyTube = c.EmptyTube, RuleSet = c.RuleSet };", Src("Pt_Optimize/Core/LineRunner.cs"));
        // 对照表（界面图例）跟生产表一致
        Assert.Equal(want, Criteria.All.Where(x => x.Hard).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // ═══════════════════════════════════════════════ 门：求解器分派
    [Fact]
    public void 门_求解器分派_按口径取表_新判据逐片裕度方向与限值来源()
    {
        Assert.Same(Solver.Allocation, Solver.AllocationFor(CriteriaRuleSet.决103前));
        Assert.Same(Solver.Allocation103, Solver.AllocationFor(CriteriaRuleSet.决103));
        var keys = Solver.Allocation103.Select(a => a.Key).ToArray();
        Assert.Equal(new[] { LineResult.Key.TubeToFlangeHeat, LineResult.Key.HotOverContact, LineResult.Key.LocalStab, LineResult.Key.FlangeStab }, keys);
        Assert.DoesNotContain(LineResult.Key.HotOverTc, keys);
        Assert.DoesNotContain(LineResult.Key.ColdUnderTc, keys);
        Assert.DoesNotContain(LineResult.Key.NetFlux, keys);
        // 方向（全局方案第 3 节杠杆表）：冷侧红要法兰变热 ⇒ 舌保温打头、没有板厚；热侧红要法兰变冷 ⇒ 板厚与内外级、没有舌保温；热稳定 ⇒ 板厚
        Solver.Knob[] K(string k) => Solver.Allocation103.First(a => a.Key == k).Knobs;
        Assert.Equal(Solver.Knob.Insul, K(LineResult.Key.TubeToFlangeHeat)[0]);
        Assert.DoesNotContain(Solver.Knob.Thick, K(LineResult.Key.TubeToFlangeHeat));
        Assert.Contains(Solver.Knob.Thick, K(LineResult.Key.HotOverContact));
        Assert.DoesNotContain(Solver.Knob.Insul, K(LineResult.Key.HotOverContact));
        Assert.Equal(new[] { Solver.Knob.Thick }, K(LineResult.Key.LocalStab));
        Assert.Equal(new[] { Solver.Knob.Thick }, K(LineResult.Key.FlangeStab));
        // 新冷侧继承旧冷侧（同一个病）、新热侧继承旧热侧（同一个方向）—— 候选逐根相同
        Assert.Equal(Solver.Allocation.First(a => a.Key == LineResult.Key.ColdUnderTc).Knobs, K(LineResult.Key.TubeToFlangeHeat));
        Assert.Equal(Solver.Allocation.First(a => a.Key == LineResult.Key.HotOverTc).Knobs, K(LineResult.Key.HotOverContact));

        // 逐片裕度：限值读判据表那一行（不另抄）；正 = 过
        var (segs, fl) = Line();
        fl[0].LocalStabMargin = 1.5; fl[1].LocalStabMargin = 0.8; fl[2].LocalStabMargin = double.NaN; fl[3].LocalStabMargin = 2.0;
        fl[0].FlangeStabMargin = 3.0; fl[1].FlangeStabMargin = 0.9;
        var r = new LineResult { Segments = segs, Flanges = fl, RuleSet = CriteriaRuleSet.决103, Converged = true, CoupleRemainK = 0.04 };
        r.Checks = new[]
        {
            new ConstraintOut { Name = LineResult.Key.HotOverContact, Limit = 10.0 },
            new ConstraintOut { Name = LineResult.Key.TubeToFlangeHeat, Limit = 0.0 },
            new ConstraintOut { Name = LineResult.Key.LocalStab, Limit = 1.0 },
            new ConstraintOut { Name = LineResult.Key.FlangeStab, Limit = 1.0 },
        };
        Assert.Equal(10.0 - 6.0, Solver.PlateSlack(r, LineResult.Key.HotOverContact, 0, 5, 5), 9);
        Assert.Equal(10.0 - 12.0, Solver.PlateSlack(r, LineResult.Key.HotOverContact, 1, 5, 5), 9);
        Assert.Equal(0.0 - (-3.0), Solver.PlateSlack(r, LineResult.Key.TubeToFlangeHeat, 0, 5, 5), 9);
        Assert.Equal(0.0 - 2.5, Solver.PlateSlack(r, LineResult.Key.TubeToFlangeHeat, 1, 5, 5), 9);
        Assert.Equal(0.5, Solver.PlateSlack(r, LineResult.Key.LocalStab, 0, 5, 5), 9);
        Assert.Equal(-0.2, Solver.PlateSlack(r, LineResult.Key.LocalStab, 1, 5, 5), 9);
        Assert.True(double.IsNaN(Solver.PlateSlack(r, LineResult.Key.LocalStab, 2, 5, 5)));      // 判不了
        Assert.Equal(-0.1, Solver.PlateSlack(r, LineResult.Key.FlangeStab, 1, 5, 5), 9);
        // 判据表里那一行不在 ⇒ 判不了（不编限值）
        var r2 = new LineResult { Segments = segs, Flanges = fl };
        Assert.True(double.IsNaN(Solver.PlateSlack(r2, LineResult.Key.HotOverContact, 0, 5, 5)));
        // 认证误差：新热侧（K）要；冷侧（W）与热稳定（×）不要
        Assert.Equal(0.04, Solver.CertNeed(r, LineResult.Key.HotOverContact), 12);
        Assert.Equal(0.0, Solver.CertNeed(r, LineResult.Key.TubeToFlangeHeat), 12);
        Assert.Equal(0.0, Solver.CertNeed(r, LineResult.Key.LocalStab), 12);
        // 片带「判不了」后置标记 ⇒ 新判据逐片裕度判不了
        fl[0].FieldsConverged = false;
        Assert.True(double.IsNaN(Solver.PlateSlack(r, LineResult.Key.HotOverContact, 0, 5, 5)));

        // 源码：主循环、回收、「没有旋钮能治」都读按口径取的表
        string sv = Src("Pt_Optimize/Core/Solver.cs");
        Assert.Contains("foreach (var (key, knobs) in alloc)", sv);
        Assert.Contains(".Where(c => !alloc.Any(a => c.Name.StartsWith(a.Key, StringComparison.Ordinal)))", sv);
        Assert.Contains("var alloc = AllocationFor(rs);", sv);
        Assert.DoesNotContain("foreach (var (key, knobs) in Allocation)", sv);
    }

    // ═══════════════════════════════════════════════ 门：升温全程卡热稳定
    static RampSweepPointResult Pt(bool stabChecked, bool stabOk, bool stabUndet) => new()
    {
        SetpointC = 600, ClampC = 450, FieldValid = true, TubeJDesignOk = true, TubeJDesignUndetermined = false,
        TubeJDesignAPerMm2 = 9.5, TubeJDesignLimit = 11,
        StabChecked = stabChecked, StabOk = stabOk, StabUndetermined = stabUndet, FlangeStabMargin = 0.9, LocalStabMargin = 1.2,
    };

    [Fact]
    public void 门_升温全程_决103卡热稳定_改回不卡()
    {
        Assert.Equal("过", RampSweep.Aggregate(new[] { Pt(false, false, false) }).Verdict);     // 改回：不判热稳定（字段不读）
        var (v0, d0, _) = RampSweep.Aggregate(new[] { Pt(false, false, false) });
        Assert.DoesNotContain("热稳定", d0);                                                   // 改回的原句逐字不变
        Assert.Equal("过", RampSweep.Aggregate(new[] { Pt(true, true, false) }).Verdict);
        var (v1, d1, _) = RampSweep.Aggregate(new[] { Pt(true, false, false) });
        Assert.Equal("不过", v1); Assert.Contains("热稳定 < 1", d1);
        var (v2, d2, _) = RampSweep.Aggregate(new[] { Pt(true, false, true) });
        Assert.Equal("判不了", v2); Assert.Contains("热稳定判不了", d2);
        // 源码：本点读整线判据表那两行（不另算）；改回口径不判
        string rs = Src("Pt_Optimize/Core/RampSweep.cs");
        Assert.Contains("bool stabChecked = lc.RuleSet != CriteriaRuleSet.决103前;", rs);
        Assert.Contains("ck.Name.StartsWith(LineResult.Key.LocalStab, StringComparison.Ordinal)", rs);
    }

    // ═══════════════════════════════════════════════ 门：全格精算（合成）
    [Fact]
    public void 门_全格精算合成_换值_重建那一行_判不了不动_改回不做()
    {
        FlangeOut P(string n, double top12, double full, double rFull) => new()
        {
            Name = n, LocalStabMargin = top12, LocalStabRMm = 31.5, DiscMaxRMm = 29.5, TabMaxRMm = 30.5,
            LocalStabFullGrid = () => new LocalStabScan { Margin = full, RMm = rFull, CellsEvaluated = 100, Seconds = 0.01, OnTab = true },
        };
        LineResult Res(CriteriaRuleSet rs, bool undet)
        {
            var fl = new[] { P("入口", 8.25, 1.88, 66.4), P("出口", 7.9, 2.1, 65.7) };
            var lc0 = new LineCase { Base = new DesignInputs { CriteriaRuleSet = rs } };
            var row = LineRunner.LocalStabCheck(lc0, fl);
            LineRunner.ApplyStateCriteria(false, new[] { row }, rs);
            if (undet) { row.Undetermined = true; row.Ok = false; row.Note = "★ 判不了（后置标记）"; }
            return new LineResult { Flanges = fl, Checks = new[] { row }, RuleSet = rs };
        }
        var lc = new LineCase { Base = new DesignInputs() };
        var r = Res(CriteriaRuleSet.决103, false);
        Assert.Equal(7.9, r.Checks[0].Actual);                                   // 全格之前：前 12 名口径
        LineRunner.ApplyLocalStabFullGrid(lc, r);
        Assert.Equal(1.88, r.Checks[0].Actual);                                   // 报出值 = 全格最小
        Assert.Equal(CheckKind.HardSafety, r.Checks[0].Kind);
        Assert.Contains("入口 舌 r=66.4", r.Checks[0].Where);
        Assert.Equal(8.25, r.Flanges[0].LocalStabTop12Margin); Assert.Equal(31.5, r.Flanges[0].LocalStabTop12RMm);
        Assert.Equal(66.4, r.Flanges[0].LocalStabRMm);                            // 片上落点换成全格（C4′ 热点核对读它）
        Assert.Null(r.Flanges[0].LocalStabFullGrid);                               // 用过即弃
        // C4′ 接线（决 99 报告第 5 节）：细区半径的热点核对读片上 LocalStabRMm ⇒ 全格之后最远热点 = 66.4，现役 59 盖不住 ⇒ 放大（前 12 名口径下 31.5 盖得住）
        Assert.Equal(66.4, MeshVerify.HotspotRadiusMm(r));
        Assert.NotNull(MeshVerify.HotspotVerdict(r, 27.0, 59.0));
        Assert.Null(MeshVerify.HotspotVerdict(Res(CriteriaRuleSet.决103前, false), 27.0, 59.0));
        Assert.Equal(200, r.LocalStabFullGridCells);
        Assert.Contains("前 12 名偏乐观", r.Checks[0].Note);
        // 判不了的那一行不动
        var u = Res(CriteriaRuleSet.决103, true);
        LineRunner.ApplyLocalStabFullGrid(lc, u);
        Assert.True(u.Checks[0].Undetermined); Assert.Equal("★ 判不了（后置标记）", u.Checks[0].Note);
        // 改回口径：什么都不做
        var o = Res(CriteriaRuleSet.决103前, false);
        LineRunner.ApplyLocalStabFullGrid(new LineCase { Base = new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 } }, o);
        Assert.Equal(7.9, o.Checks[0].Actual); Assert.NotNull(o.Flanges[0].LocalStabFullGrid);
        Assert.EndsWith("　⚠ 现为参考量，同上。", o.Checks[0].Note);            // 改回口径的说明逐字同改前
        Assert.Equal(CheckKind.Reference, o.Checks[0].Kind);
    }

    // ═══════════════════════════════════════════════ 门：拒答（加密复算、保温搜索）
    [Fact]
    public void 门_拒答_加密复算与保温搜索在决103口径下不拿旧三条冒充()
    {
        var d = R48NMeshGateTests.Design("W08");
        var lc = d.BuildCase(new DesignInputs());
        // 2026-09-25：比对列换成决 103 两条（MeshVerify.MeshTolerances103）⇒ 带玻璃稳态不再拒答；空管到温稳态照旧拒答（两条在该态只作参考）
        Assert.Null(MeshVerify.RefuseForState(lc));
        var cols = MeshVerify.TolTemplate(lc);
        Assert.Equal(new[] { Criteria.Plain(LineResult.Key.TubeToFlangeHeat), Criteria.Plain(LineResult.Key.HotOverContact) }, cols.Select(x => x.Name).ToArray());
        Assert.Equal(MeshVerify.NetFluxMeshTolW, cols[0].Tol);
        Assert.Equal(MeshVerify.TcMeshTolFrac * lc.HotOverContactMaxK, cols[1].Tol);
        Assert.Equal(1.0, cols[1].Tol);   // 缺省限值 10 K 的 10 %
        var lcEmpty = d.BuildCase(new DesignInputs()); lcEmpty.EmptyTube = true;
        string? whyEmpty = MeshVerify.RefuseForState(lcEmpty);
        Assert.NotNull(whyEmpty);
        Assert.Contains("不能据此说这个设计过了", whyEmpty!);
        var lcOld = d.BuildCase(new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 });
        Assert.Null(MeshVerify.RefuseForState(lcOld));                            // 改回：与改前同一个比对列
        Assert.Equal(3, MeshVerify.TolTemplate(lcOld).Count);

        var ex = Assert.Throws<InvalidOperationException>(() => InsulationSearch.Run(d, new DesignInputs(), new InsulationSearch.Options()));
        Assert.Equal(InsulationSearch.Rule103Refusal, ex.Message);
        Assert.Equal(CriteriaRuleSet.决103前, InsulationSearch.RuleSetOfSearch);
    }

    // ═══════════════════════════════════════════════ 门e：源码门
    /// <summary>
    /// Core 里每一个引用三条旧判据（最热铂高出热偶读数、管根低于热偶读数、管孔净流入）的文件都在这张名单上，并写明它**不是**把它们当硬判据用的理由。
    /// 名单外的文件一引用就红；名单上的文件若不再引用也红（名单不许留死项）。行为另由上面几道快门验（分工况表、分派、拒答）。
    /// </summary>
    static readonly (string File, string Why)[] LegacyKeyUsers =
    {
        ("Criteria.cs",           "界面图例：三条标 Hard = false、说明写「只作参考」（门_分工况表 核对照表硬线 = 生产表）"),
        ("DesignSpec.cs",         "作废档的「热学结论不可引用」声明名单（只影响自检的「声明之外的失败」分类）"),
        ("FlangeAutoSizer.cs",    "旧定尺寸器的进度与说明里印三条的值（本器只追旧判法增量温降靶，不判交付）"),
        ("InstallReport.cs",      "安装报告印热偶读数基准的对照表（印数，不判）"),
        ("InsulWindow.cs",        "舌保温窗口逐点印三条的值；过不过一律读 LineResult.AllOk（按结果自己的口径）"),
        ("InsulationSearch.cs",   "逐格点三项钉在改前口径（RuleSetOfSearch = 决103前）；决 103 口径下 Run 拒答"),
        ("LineRunner.cs",         "判据构造照算照印；Kind 由按口径取的分工况表盖（决 103 = 参考）"),
        ("MeshVerify.cs",         "改回口径（决103前）的比对列 MeshTolerances 仍是改前三条；决 103 口径走 MeshTolerances103（2026-09-25）"),
        ("SensitivityMatrix.cs",  "离线敏感度矩阵（--sensmatrix 诊断，只量不判）"),
        ("ShapeReview.cs",        "形状评审报告印值（不判）"),
        ("Sizer.cs",              "旧定尺寸器印旧判法与净流入的值"),
        ("Solver.cs",             "改回分派表 Allocation、改回口径的日志与回收名单、PlateSlack／CertNeed 的旧键支（改回口径与诊断用）"),
    };

    [Fact]
    public void 门e_源码门_Core里没有别处把三条旧判据当硬判据用()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        var pat = new[] { "Key.HotOverTc", "Key.ColdUnderTc", "Key.NetFlux", "\"⑦ 最热铂高出热偶读数\"", "\"⑧ 管根低于热偶读数\"", "\"②′管孔净流入" };
        var users = Directory.GetFiles(core, "*.cs").Where(f => { string t = File.ReadAllText(f); return pat.Any(t.Contains); })
                             .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var allowed = LegacyKeyUsers.Select(x => x.File).ToArray();
        var extra = users.Where(u => !allowed.Contains(u)).ToArray();
        Assert.True(extra.Length == 0, "名单外的文件引用了三条旧判据（要么接新判据，要么进名单并写明为什么不是当硬判据用）：" + string.Join("、", extra));
        var dead = allowed.Where(a => !users.Contains(a)).ToArray();
        Assert.True(dead.Length == 0, "名单里的文件已不引用三条旧判据，删掉这一项：" + string.Join("、", dead));

        // 判定的读口只有按口径取的那几处（不许再有不带口径就把三条当硬判据的名单）
        string lr = Src("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("ApplyStateCriteria(c.EmptyTube, checks, rs);", lr);
        Assert.Contains("public string[] MissingChecks => RequiredFor(EmptyTube, RuleSet)", lr);
        Assert.Contains("public bool HardOk => !RequiredFor(EmptyTube, RuleSet).Any(", lr);
        string ms = Src("Pt_Optimize/Core/MeshVerify.cs");
        Assert.Contains("LineResult.StateKindOf(m.Key, c.EmptyTube, c.RuleSet)", ms);
        // 行为：生产口径下三条两态都是参考；对照表不标硬
        foreach (var k in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux })
        {
            Assert.Equal(CheckKind.Reference, LineResult.StateKindOf(k, false));
            Assert.False(Criteria.All.First(e => e.Key == k).Hard);
        }
        _o.WriteLine("引用三条旧判据的 Core 文件：" + string.Join("、", users));
    }

    // ═══════════════════════════════════════════════ 慢门：整线解（门a／门b／归因共用一次解）
    sealed class Runs
    {
        public string Baseline = "", BaselineFile = "";
        public List<(string Tag, R48CriteriaSwapDump.Out Old, R48CriteriaSwapDump.Out New)> Cases = new();
        public string Stamp = "", Head = "";
    }
    static readonly Lazy<Runs> _runs = new(() =>
    {
        var R = new Runs();
        R.BaselineFile = Directory.GetFiles(Path.Combine(HandoverDoc.Root(), "deliverable"), "R48_决103_改前树转储_W08现役与RW45_导航_本次开跑于*.txt")
                                  .OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() ?? "";
        R.Baseline = R.BaselineFile.Length > 0 ? File.ReadAllText(R.BaselineFile) : "";
        R.Head = $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 Linux 镜像（{Environment.OSVersion}，.NET {Environment.Version}，{Environment.ProcessorCount} 核）　Linux、待 Windows 重录\n"
               + $"工作树 {HandoverDoc.Root()}　提交 {Sh("git", $"-C {HandoverDoc.Root()} rev-parse --short HEAD")}（分离头 + 未提交的决 103 改动）　"
               + $"开跑时 /proc/loadavg = {Load()}（同机另有实施者与读档人 ⇒ 耗时只作量级）";
        var cases = R48CriteriaSwapDump.Cases();
        var jobs = cases.SelectMany(c => new[] { (c.Tag, c.D, Old: true), (c.Tag, c.D, Old: false) }).ToArray();
        var outs = new R48CriteriaSwapDump.Out[jobs.Length];
        Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        {
            var j = jobs[i];
            outs[i] = R48CriteriaSwapDump.SolveAndDump(j.Tag, j.D.Clone(), new DesignInputs(), 0.0,
                          j.Old ? (Action<DesignInputs>)(p => p.CriteriaRuleSet = CriteriaRuleSet.决103前) : null);
        });
        for (int i = 0; i < cases.Count; i++) R.Cases.Add((cases[i].Tag, outs[2 * i], outs[2 * i + 1]));
        // 四份转储原样落盘（门 a 的「改回」一侧与门 b 的「开」一侧都可复核，不必重解）
        var dump = new StringBuilder();
        dump.AppendLine("决 103 改后树转储：W08 现役（盘 30）与 R = w = 45 × 决103前（改回）／决103（生产）　导航网格整线一次解　写法 R48CriteriaSwapDump.SolveAndDump");
        dump.AppendLine(R.Head);
        dump.AppendLine();
        foreach (var (tag, old, nw) in R.Cases)
        {
            dump.AppendLine($"════ {tag}　口径 决103前（改回）"); dump.Append(old.Text).AppendLine();
            dump.AppendLine($"════ {tag}　口径 决103（生产）"); dump.Append(nw.Text).AppendLine();
        }
        File.WriteAllText(DeliverableOut.Stamped("R48_决103_改后树转储_决103前与决103_W08现役与RW45_导航.txt"), dump.ToString(), new UTF8Encoding(true));
        return R;
    });

    static string BaselineCase(string all, string tag)
    {
        var lines = all.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(); bool on = false;
        foreach (var l in lines)
        {
            if (l.StartsWith("#案例\t", StringComparison.Ordinal)) on = l.StartsWith("#案例\t" + tag + "\t", StringComparison.Ordinal);
            if (on) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 门a_改回逐位_W08现役与RW45_决103前等于改前树()
    {
        var R = _runs.Value;
        Assert.True(R.BaselineFile.Length > 0, "deliverable 里找不到改前树转储（R48_决103_改前树转储_…）—— 门失去了对象");
        string file = DeliverableOut.Stamped("R48_决103_门a_改回逐位_W08现役与RW45_导航.txt");
        var sb = new StringBuilder();
        sb.AppendLine("决 103 门(a) 改回逐位：参数表 CriteriaRuleSet = 决103前 时，整线判据表（名、级、值 R 格式、限值、过否、判不了、暂不给数、位置、说明全文）、逐片、逐段、说明、没过名单与改前树转储逐行相同（去挂钟）");
        sb.AppendLine(R.Head.Replace("\n", Environment.NewLine));
        sb.AppendLine($"改前树转储：{Path.GetFileName(R.BaselineFile)}（cc49836 + 转储档，Core 未改）");
        int bad = 0;
        foreach (var (tag, old, _) in R.Cases)
        {
            var a = R48CriteriaSwapDump.Comparable(BaselineCase(R.Baseline, tag));
            var b = R48CriteriaSwapDump.Comparable(old.Text);
            int n = Math.Max(a.Length, b.Length), diff = 0;
            var first = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string x = i < a.Length ? a[i] : "（缺）", y = i < b.Length ? b[i] : "（缺）";
                if (x != y) { diff++; if (first.Count < 6) first.Add($"  第 {i + 1} 行\n    改前：{x}\n    改回：{y}"); }
            }
            sb.AppendLine($"{tag}：改前 {a.Length} 行、改回 {b.Length} 行，不同 {diff} 行{(diff == 0 ? "（逐位相同）" : "")}　改回耗时 {old.Sec:0} s");
            foreach (var f in first) sb.AppendLine(f);
            if (diff > 0 || a.Length == 0) bad++;
        }
        sb.AppendLine("覆盖：两例导航网格整线一次解的判据表全行、逐片 13 个量、逐段 5 个量、说明（Notes）、没过名单。不覆盖：判决网格、求解器（Solver.Solve 的分派在改回口径下走 Allocation 那一张，另由快门「门_求解器分派」验表相同）、升温全程逐点（快门「门_升温全程」验汇总逐字）、图纸路径。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(bad == 0, $"改回不逐位：{bad} 例（{file}）");
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 门b_新判据只印与归因_W08现役与RW45_决103前对决103()
    {
        var R = _runs.Value;
        string file = DeliverableOut.Stamped("R48_决103_门b_新判据与归因_W08现役与RW45_导航.txt");
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        W("决 103 门(b) 新判据（只印实测，不预设红绿）＋ 归因（决103前 = 关，决103 = 开，同一树同一算例，判词并列）");
        W(R.Head.Replace("\n", Environment.NewLine));
        W("算例：W08 现役设计（盘 30、舌半宽 30、舌长 140）与决 102 探针里 R = w = 45（舌长按搜形状规则 = 140）；旋钮全部现役值；导航网格（FineMm = 0，细区半径 = 细区半径计划）整线一次解，不求根。");
        W("写法：R48CriteriaSwapDump.SolveAndDump（= R48NMeshGateTests.SolveLine 同一路），两个口径只差参数表 CriteriaRuleSet。");
        W();
        var keysNew = new[] { LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat, LineResult.Key.LocalStab, LineResult.Key.FlangeStab, LineResult.Key.TubeJ, LineResult.Key.Ramp, LineResult.Key.SectionJ };
        var keysOld = new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux, LineResult.Key.DiscTemp, LineResult.Key.FlangeDip, LineResult.Key.TubeJPre103 };
        string Row(LineResult? r, string k)
        {
            var c = r?.Find(k);
            if (c is null) return "（不出这一行）";
            string v = c.Undetermined ? "判不了" : c.Kind == CheckKind.Reference ? (c.Ok ? "参考·在限内" : "参考·越限") : (c.Ok ? "过" : "不过");
            return $"{F3(c.Actual)}\t{(c.LessIsBetter ? "≤" : "≥")}{F3(c.Limit)}\t{c.Kind}\t{v}\t{c.Where}";
        }
        foreach (var (tag, old, nw) in R.Cases)
        {
            W($"══════ {tag}");
            if (old.R is null || nw.R is null) { W($"**抛异常**：关 {old.Error}／开 {nw.Error}"); continue; }
            var a = old.R; var b = nw.R;
            W($"整线：关 Ok={a.Ok} 收敛={a.Converged} 耦合轮 {a.CoupleRounds} 容差 {F3(a.CoupleTolKUsed)} K 铂重 {a.TotalMassG:0.0} g 耗时 {old.Sec:0} s　｜　开 Ok={b.Ok} 收敛={b.Converged} 耦合轮 {b.CoupleRounds} 容差 {F3(b.CoupleTolKUsed)} K 铂重 {b.TotalMassG:0.0} g 耗时 {nw.Sec:0} s（其中局部热稳定全格精算 {b.LocalStabFullGridCells} 格 {b.LocalStabFullGridSec:0.0} s）");
            W("判据\t关（决103前）：值\t限\t级\t判\t位置\t｜\t开（决103）：值\t限\t级\t判\t位置");
            foreach (var k in keysNew.Concat(keysOld))
                W($"{Criteria.Plain(k)}\t{Row(a, k)}\t｜\t{Row(b, k)}");
            W($"交付判定 AllOk：关 {(a.AllOk ? "全过" : "不全过")}　开 {(b.AllOk ? "全过" : "不全过")}");
            W("没过（关）：" + (a.Failed.Length == 0 ? "（无）" : string.Join("；", a.Failed)));
            W("没过（开）：" + (b.Failed.Length == 0 ? "（无）" : string.Join("；", b.Failed)));
            W("逐片（开）：" + string.Join("　", b.Flanges.Select(f =>
                $"{f.Name} 最高 {f.TMaxC:0.0}（盘峰 {f.TDiscMaxC:0.0}／舌峰 {f.TTabMaxC:0.0}）− 接触处 {f.TRootC:0.0} = {f.TMaxC - f.TRootC:+0.00;−0.00} K，管→法兰 {f.QFromTubeW:+0.00;−0.00} W，"
              + $"局稳 全格 {F3(f.LocalStabMargin)}（r {f.LocalStabRMm:0.0}）← 前 12 名 {F3(f.LocalStabTop12Margin)}（r {f.LocalStabTop12RMm:0.0}），整稳 {F3(f.FlangeStabMargin)}")));
            W("逐片（关）：" + string.Join("　", a.Flanges.Select(f =>
                $"{f.Name} 最高 − 接触处 {f.TMaxC - f.TRootC:+0.00;−0.00} K，管→法兰 {f.QFromTubeW:+0.00;−0.00} W，局稳（前 12 名）{F3(f.LocalStabMargin)}（r {f.LocalStabRMm:0.0}）")));
            // 「开 − 关」：同一个量在两个口径下的差（口径只改判定与停机容差、局部热稳定取法；场的差来自外层耦合停机容差随硬温度判据换人而变）
            double dq = b.Flanges.Zip(a.Flanges, (x, y) => Math.Abs(x.QFromTubeW - y.QFromTubeW)).Max();
            double dt = b.Flanges.Zip(a.Flanges, (x, y) => Math.Abs((x.TMaxC - x.TRootC) - (y.TMaxC - y.TRootC))).Max();
            W($"开 − 关（场量）：逐片管孔净热流差最大 {dq:0.0000} W、逐片最高 − 接触处差最大 {dt:0.0000} K（变因：外层耦合停机容差按「最小硬安全线温度裕度」取 —— 关读热偶两条、开读新热侧；以及局部热稳定全格精算不改场）");
            W();
        }
        W("预期（跑前写的，只作对照，不据此判）：R = w = 45 冷侧（管接触处流入法兰的净热流）红；W08 盘 30 待实测。");
        W("覆盖：两例的新两条值与判词、两条热稳定（开口径 = 全格）、管 J 限 11 与原 12 对照行、旧三条与旧判法两条的值与级，以及两口径判词并列。");
        W("不覆盖：判决网格、求解器求根（旋钮不动）、升温全程、空管到温稳态、图纸路径；Windows 重录。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        foreach (var (tag, old, nw) in R.Cases)
        {
            Assert.True(nw.R is { Ok: true }, $"{tag} 决 103 口径没解出来：{nw.Error}{nw.R?.Message}");
            var b = nw.R!;
            foreach (var k in new[] { LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat })
                Assert.Equal(CheckKind.HardSafety, b.Find(k)!.Kind);
            Assert.Equal(CheckKind.HardSafety, b.Find(LineResult.Key.LocalStab)!.Kind);
            Assert.Equal(CheckKind.HardSafety, b.Find(LineResult.Key.FlangeStab)!.Kind);
            Assert.Equal(CheckKind.Reference, b.Find(LineResult.Key.HotOverTc)!.Kind);
            Assert.Equal(11.0, b.Find(LineResult.Key.TubeJ)!.Limit);
            Assert.Equal(12.0, b.Find(LineResult.Key.TubeJPre103)!.Limit);
            Assert.Equal(11.0, b.Find(LineResult.Key.Ramp)!.Limit);
            Assert.Null(old.R!.Find(LineResult.Key.HotOverContact));             // 关：不出新行
            Assert.True(b.Flanges.All(f => f.LocalStabFullCells > 0), $"{tag}：有片没做全格精算");
        }
    }

    // ═══════════════════════════════════════════════ 慢门 c：局部热稳定全格（决 99 案例）
    [Trait("速度", "慢")]
    [Fact]
    public void 门c_局部热稳定全格_决99案例_报出值等于逐片重建全格最小()
    {
        string file = DeliverableOut.Stamped("R48_决103_门c_局部热稳定全格_W08判决细区59.txt");
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        W("决 103 门(c) 局部热稳定全格精算：决 99 案例（W08 现役，判决网格 h = MeshVerify.RequiredFineMmFor，细区半径 59 = 决 99 的「现役细区」），决 103 口径整线一次解；");
        W("报出值（判据表那一行、片上 LocalStabMargin）必须 = 逐片按整线输出的片电流与管根温度重建同一片热解后，全部候选格逐格精算的最小值（逐位）；重建先核前 12 名口径与整线逐位相同。");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 Linux 镜像　Linux、待 Windows 重录　工作树 {HandoverDoc.Root()}　loadavg {Load()}");
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design("W08").Clone();
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        double h = MeshVerify.RequiredFineMmFor(d);
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = h, FineRadiusMm = 59.0 });
        var sw = Stopwatch.StartNew();
        var r = LineRunner.Run(lc, null);
        double sec = sw.Elapsed.TotalSeconds;
        W($"整线：Ok={r.Ok} 收敛={r.Converged} 单元 {r.MeshCells} 细区 {lc.MeshFineMm} mm 半径 {lc.MeshFineRadiusMm} mm 耦合轮 {r.CoupleRounds} 耗时 {sec:0} s；全格精算 {r.LocalStabFullGridCells} 格 {r.LocalStabFullGridSec:0.0} s（占整线 {100 * r.LocalStabFullGridSec / sec:0.0} %）");
        Assert.True(r.Ok, r.Message);
        var row = r.Find(LineResult.Key.LocalStab)!;
        W($"判据表：{Criteria.Plain(row.Name)} {F3(row.Actual)}（{row.Where}）{row.Kind} {(row.Undetermined ? "判不了" : row.Ok ? "过" : "不过")}");
        W("片\t前12名口径\tr\t全格（整线）\tr\t重建前12名\t重建全格\t重建逐格最小\t评过格\t乐观倍数\t全格耗时s");
        bool allSame = true;
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            var mesh = LineRunner.PlateMeshAnalytic(lc, j);
            var cur = LineRunner.PlateCurrentField(lc, mesh, j, f.CurrentA);
            var ts = LineRunner.PlateThermalInputs(lc, j, f.CurrentA, null);
            var th = LineRunner.SolvePlateThermal(mesh, cur.HeatJAPerMm2, f.TRootC, ts, jLocalAPerMm2: cur.JMagAPerMm2);
            var sc = th.LocalStabFullGrid!();
            double brute = sc.CellMargin.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Min();
            bool same = X(th.LocalStabMargin) == X(f.LocalStabTop12Margin) && X(sc.Margin) == X(f.LocalStabMargin) && X(brute) == X(f.LocalStabMargin);
            allSame &= same;
            W($"{f.Name}\t{F3(f.LocalStabTop12Margin)}\t{f.LocalStabTop12RMm:0.000}\t{F3(f.LocalStabMargin)}\t{f.LocalStabRMm:0.000}\t{F3(th.LocalStabMargin)}\t{F3(sc.Margin)}\t{F3(brute)}\t{sc.CellsEvaluated}"
              + $"\t{(f.LocalStabMargin > 0 ? f.LocalStabTop12Margin / f.LocalStabMargin : double.NaN):0.000}\t{f.LocalStabFullSec:0.00}\t{(same ? "逐位相同" : "**不同**")}");
        }
        double minPlate = r.Flanges.Min(f => f.LocalStabMargin);
        W($"判据表报出 {X(row.Actual)} vs 逐片全格最小 {X(minPlate)}：{(X(row.Actual) == X(minPlate) ? "逐位相同" : "**不同**")}");
        W("对照决 99（d6ae1f6 树、同一案例入口片）：全格真最小 1.8816、落点 r 66.412；前 12 名报 8.2492、r 31.504（本树含 F6／C4′ 等后续改动，数不要求相同，只印）。");
        W("覆盖：W08 判决网格四片的全格精算与报出值一致性、前 12 名的乐观倍数、全格精算机时。不覆盖：导航网格与加密阶梯、其他几何；细区半径是否要放大到盖住真落点由 C4′（MeshVerify.HotspotVerdict 读片上 LocalStabRMm）管，本门固定 59 不放大（与决 99 同口径）。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(allSame, $"重建全格与整线报出值不逐位相同（{file}）");
        Assert.Equal(X(minPlate), X(row.Actual));
        Assert.True(r.Flanges.All(f => f.LocalStabMargin <= f.LocalStabTop12Margin), "全格最小大于前 12 名最小 —— 全格是超集，不可能");
    }
}
