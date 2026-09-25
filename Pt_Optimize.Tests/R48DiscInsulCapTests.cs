using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 决 104（业主 2026-09-25「圆盘保温块最大厚度(圆盘之前说过了10mm)」；出处 2026-09-17「缠绕20圈以下」× 每圈 0.5 mm）：
/// 圆盘保温上限 <see cref="DesignInputs.DiscInsulCapMm"/>（缺省 10）做成 决103 口径的硬判据「接合区保温缠得出来」
/// （<see cref="WrapLimits.Judge(LineCase, CriteriaRuleSet)"/>），不封顶、不外推、不按上限硬算：
/// 读值口径 <see cref="LineCase.DiscInsulEffectiveAt"/> 仍回设定值（配套清单、热解、判据同源）。
/// 改回：决103前 口径、或上限 = 正无穷 ⇒ 参考行，逐位同 2026-09-18 口径。
/// 门 a 20 mm 卡并进 HardBlocked；b 10 mm 过；c 决103前 参考（逐位同 Judge(c)）；d 改回参数参考；e 分工况表两态硬、对照表硬；
/// f 读值不封顶且逐片点名；g 源码钉缺省 10、调用带口径、封顶代码已删。不覆盖：热解结果本身（慢门与实跑）。
/// </summary>
public class R48DiscInsulCapTests
{
    static LineCase Case(double whole, double[]? perPlate = null, DesignInputs? p = null)
    {
        var d = DesignSpec.W08.Clone(); d.FlangeInsulated = true; d.FlangeInsulMm = whole;
        d.DiscInsulMm = perPlate ?? Array.Empty<double>();
        return d.BuildCase(p ?? new DesignInputs(), checkRamp: false);
    }

    static void SameRow(ConstraintOut a, ConstraintOut b)
    {
        Assert.Equal(a.Name, b.Name); Assert.Equal(a.Kind, b.Kind); Assert.Equal(a.Ok, b.Ok); Assert.Equal(a.Undetermined, b.Undetermined);
        Assert.Equal(a.Actual, b.Actual); Assert.Equal(a.Limit, b.Limit); Assert.Equal(a.Where, b.Where); Assert.Equal(a.Note, b.Note);
    }

    [Fact]
    public void 门a_决103口径_20mm超上限_硬判据不过_进HardBlocked()
    {
        var lc = Case(20.0);
        Assert.Equal(CriteriaRuleSet.决103, lc.RuleSet);
        Assert.Equal(10.0, lc.Base.DiscInsulCapMm);
        var k = WrapLimits.Judge(lc, lc.RuleSet);
        Assert.Equal(LineResult.Key.WrapTurns, k.Name);
        Assert.Equal(CheckKind.HardSafety, k.Kind);
        Assert.False(k.Ok); Assert.False(k.Undetermined);
        Assert.Equal(20.0, k.Actual, 9); Assert.Equal(10.0, k.Limit, 9);
        Assert.Equal("入口 圆盘保温", k.Where);
        Assert.Contains("决 104", k.Note); Assert.Contains("不可行", k.Note); Assert.Contains("杠杆", k.Note); Assert.Contains("不外推", k.Note);
        Assert.DoesNotContain(WrapLimits.PrefabNote, k.Note);   // 超上限是不可行，不是换包法
        var r = new LineResult { Ok = true, Converged = true, RampChecked = true, RuleSet = CriteriaRuleSet.决103, Checks = new[] { k } };
        Assert.Contains(r.HardBlocked, c => c.Name == LineResult.Key.WrapTurns);
        Assert.False(r.HardOk);
    }

    [Fact]
    public void 门b_决103口径_10mm贴上限_硬判据过()
    {
        var k = WrapLimits.Judge(Case(10.0), CriteriaRuleSet.决103);
        Assert.Equal(CheckKind.HardSafety, k.Kind);
        Assert.True(k.Ok, k.Note); Assert.False(k.Undetermined);
        Assert.Equal(10.0, k.Actual, 9); Assert.Equal(10.0, k.Limit, 9);
        Assert.Contains("决 104", k.Note);
        Assert.DoesNotContain("不可行", k.Note);
    }

    [Fact]
    public void 门c_改回口径_决103前_参考行_逐位同Judge单参()
    {
        var lc = Case(20.0, p: new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 });
        var k = WrapLimits.Judge(lc, lc.RuleSet);
        Assert.Equal(CheckKind.Reference, k.Kind);
        Assert.False(k.Ok);
        Assert.Contains(WrapLimits.PrefabNote, k.Note);   // 2026-09-18 口径：报圈数 + 提示预制块
        Assert.DoesNotContain("决 104", k.Note);
        SameRow(WrapLimits.Judge(lc), k);
    }

    [Fact]
    public void 门d_改回参数_上限正无穷_回到参考行()
    {
        var lc = Case(20.0, p: new DesignInputs { DiscInsulCapMm = double.PositiveInfinity });
        Assert.Equal(CriteriaRuleSet.决103, lc.RuleSet);
        var k = WrapLimits.Judge(lc, lc.RuleSet);
        Assert.Equal(CheckKind.Reference, k.Kind);
        SameRow(WrapLimits.Judge(lc), k);
    }

    [Fact]
    public void 门e_分工况表_决103两态硬_决103前两态参考_对照表硬()
    {
        string key = LineResult.Key.WrapTurns;
        var row = LineResult.RequiredByState.Single(q => q.Prefix == key);
        Assert.Equal(CheckKind.HardSafety, row.GlassKind);
        Assert.Equal(CheckKind.HardSafety, row.EmptyTubeKind);
        Assert.False(row.NeedsRamp);
        Assert.Contains(key, LineResult.RequiredFor(false, CriteriaRuleSet.决103).Select(q => q.Prefix));
        Assert.Contains(key, LineResult.RequiredFor(true, CriteriaRuleSet.决103).Select(q => q.Prefix));
        var pre = LineResult.RequiredByStatePre103.Single(q => q.Prefix == key);
        Assert.Equal(CheckKind.Reference, pre.GlassKind);
        Assert.Equal(CheckKind.Reference, pre.EmptyTubeKind);
        Assert.DoesNotContain(key, LineResult.RequiredFor(false, CriteriaRuleSet.决103前).Select(q => q.Prefix));
        Assert.DoesNotContain(key, LineResult.RequiredFor(true, CriteriaRuleSet.决103前).Select(q => q.Prefix));

        // 分工况表是唯一来源：一条按参考行造的判据经 ApplyStateCriteria（决103）升为硬判据
        var ck = new ConstraintOut { Name = key, Unit = "mm", Kind = CheckKind.Reference, Ok = false, Actual = 20, Limit = 10, LessIsBetter = true };
        LineRunner.ApplyStateCriteria(false, new[] { ck }, CriteriaRuleSet.决103);
        Assert.Equal(CheckKind.HardSafety, ck.Kind);

        var e = Criteria.All.Single(x => x.Key == key);
        Assert.True(e.Hard, "对照表还把它印成参考 —— 与分工况表两个答案");
        Assert.Contains("决 104", e.Means);
        Assert.Contains("不可行", e.Means);
    }

    [Fact]
    public void 门f_读值口径不封顶_清单热解判据同源_逐片点名()
    {
        var d = DesignSpec.W08.Clone(); d.FlangeInsulated = true; d.FlangeInsulMm = 20.0; d.DiscInsulMm = Array.Empty<double>();
        var lc = d.BuildCase(new DesignInputs(), checkRamp: false);
        for (int j = 0; j < lc.FlangePlates.Length; j++)
        {
            Assert.Equal(20.0, lc.DiscInsulEffectiveAt(j), 12);
            Assert.Equal(d.DiscInsulMmOf(j), lc.DiscInsulEffectiveAt(j), 12);
        }
        var lc2 = Case(8.0, new[] { 8.0, 12.0, 8.0, 8.0 });
        Assert.Equal(12.0, lc2.DiscInsulEffectiveAt(1), 12);
        var k = WrapLimits.Judge(lc2, CriteriaRuleSet.决103);
        Assert.Equal(CheckKind.HardSafety, k.Kind);
        Assert.False(k.Ok);
        Assert.Equal(12.0, k.Actual, 9);
        Assert.Equal("共用1 圆盘保温", k.Where);
    }

    [Fact]
    public void 门g_源码_缺省10_调用带口径_封顶代码已删()
    {
        string root = HandoverDoc.Root();
        string di = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Pt_Optimize", "Core", "DesignInputs.cs"));
        Assert.Contains("DiscInsulCapMm { get; set; } = 10.0;", di);
        Assert.Contains("缠绕20圈以下", di);
        string lr = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("WrapLimits.Judge(c, c.RuleSet)", lr);
        Assert.DoesNotContain("DiscInsulCapped", lr);
        Assert.DoesNotContain("DiscInsulWasCapped", lr);
        string wl = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Pt_Optimize", "Core", "WrapLimits.cs"));
        Assert.Contains("Decision104Note", wl);
        Assert.Contains("Judge(LineCase c, CriteriaRuleSet rs)", wl);
    }
}
