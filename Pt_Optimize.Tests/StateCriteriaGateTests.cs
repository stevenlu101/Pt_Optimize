using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;
using static PtOptimize.Core.InsulationSearch;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ K 路（2026-09-15，Opus 5）：**分工况判据**的门（审查 P1-3、P1-9、P3-4、P2-7 判据部分）。
///
/// 用户 2026-09-15 原话：「比如空管时铂过热那条也不该按 5 ℃ 卡，只要J&lt;11即可」「管内有热玻璃时电流小，法兰成为散热片」
/// 「希望设计法兰能升温，升温后减少散热的用料最少法兰」。
/// 口径（主会话）：带玻璃稳态热侧、冷侧、管孔净流入照旧卡交付；空管到温稳态这三条降为参考量（热侧是原话；冷侧与净流入是主会话按「只要J&lt;11即可」的解读，
/// 已向用户复述未遭否定），其余几条两态照旧；**场的有效性不是判据、两态都保留**（场没收敛、越过熔点、散热表超界 ⇒ 该工况判不了，判不了不算过）。
///
/// 门（跑前写死，不许为了变绿挪）：
///   ① 空管态热侧超 5 K 不影响 AllOk，带玻璃态超 5 K 影响（热侧那一条由生产的 LineRunner.ThermocoupleChecks 在合成接头数据上造，Kind 由生产的 ApplyStateCriteria 盖）；
///      另有一条真解（两段三片、开箱网格）：空管结果三条标参考、工况位接到结果上，把真结果的热侧改成超限 ⇒ 空管 AllOk 不变、带玻璃 AllOk 变假。
///   ② 源码门 + 行为一致：分工况只有 LineResult.RequiredByState 一处；InsulationSearch、MeshVerify、Criteria 只查表、不自带清单。
///   ③ 有效性守门：空管态场判不了（场没收敛／越过熔点／管表超界／压接盖孔、进盘／保温分界判不了）⇒ AllOk、HardOk 为假，Failed 点名。
///   ④ 保温搜索可行性读整线结果的全部判定：造一个「逐片三项都过、管 J 超限」的整线结果 ⇒ 终点整线判定不可行、层不可行。
///   ⑤ 保温搜索逐格点：空管态参考项不进可行集、不进排序键、算不出数不把格点打成判不了；评估函数写漏工况位时由 EvaluateView 盖上。
///   ⑥（2026-09-16 Opus 5，复审修 M1/M2）整线一态「能不能用」的判法 InsulationSearch.LineStateWhy 有行为门：Ok、Converged、FieldsConverged 都为真、只有压接盖孔 ⇒ 非空；
///      八种场判不了的情形各自点名 —— 上一轮审查注入把它改回「只看 FieldsConverged」没还原时，原来的门（只核字符串）全绿。
///   ⑦（2026-09-16 Opus 5，复审修 S4）加密复算的工厂重载造出空管算例 ⇒ 判词是拒答句、不解（Line 空、Trace 空、工厂只被叫一次）—— 此前只有源码门（注入只删 return 全绿）。
///   源码门补三处（2026-09-16，复审修 S3/S5/S6）：LineRunner.cs 每一处 new LineResult 都写工况位且恰两处（原来核一处字串，删一处不红）；层可行接线
///   `lr.FinalLineAllOk = ok` 与空管态闭合退路 `if (judged)` 两行原文 —— 这两处**只有源码门、无行为门**（RunLayer／EvaluatePoint 私有、要真解整线才走到），HANDOVER 记。
/// </summary>
// ★ 决 103（2026-09-24）：本档守的是 K 路（2026-09-15）那一套分工况口径（⑦／⑧／②′ 带玻璃稳态卡、空管态参考）—— 决 103 起它只在改回口径（CriteriaRuleSet = 决103前）下成立。
//   有意改动：合成底表与真解一律显式走改回口径（RequiredByStatePre103、ApplyStateCriteria(…, 决103前)、参数表 CriteriaRuleSet = 决103前），机制照旧逐条验；
//   生产口径（决 103）的分工况表、降级／升级说明由 R48CriteriaSwapGateTests 验。源码门的钉子按新签名改（带口径的重载）。
public class StateCriteriaGateTests
{
    private const CriteriaRuleSet Pre = CriteriaRuleSet.决103前;

    private readonly ITestOutputHelper _o;
    public StateCriteriaGateTests(ITestOutputHelper o) => _o = o;

    private static readonly string[] ThreeKeys = { LineResult.Key.NetFlux, LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc };

    /// <summary>Judge 那样构造的底表：分工况表里每条按带玻璃稳态的 Kind 造、条条通过，外加一条参考量。名字带后缀（Judge 里的名字都是「前缀 + 说明」）。</summary>
    private static List<ConstraintOut> JudgeLikeTable(params string[] except) => LineResult.RequiredByStatePre103
        .Where(q => !except.Contains(q.Prefix))
        .Select(q => new ConstraintOut { Name = q.Prefix + " 某某后缀", Kind = q.GlassKind, Ok = true, Actual = 1, Limit = 2, Note = "原注" })
        .Append(new ConstraintOut { Name = "· 某参考量", Kind = CheckKind.Reference, Ok = true, Actual = 1, Limit = 2 })
        .ToList();

    private static LineResult Result(bool emptyTube, List<ConstraintOut> table)
    {
        LineRunner.ApplyStateCriteria(emptyTube, table, Pre);
        return new LineResult { Converged = true, Ok = true, RampChecked = false, EmptyTube = emptyTube, RuleSet = Pre, Checks = table.ToArray() };
    }

    /// <summary>一段两片的合成接头：片0 圆盘峰 1157 °C（热侧 +7 K，超 5 K），片1 各项都在带里；冷侧两片 2／3 K（过）。</summary>
    private static (SegmentOut[] Segs, FlangeOut[] Fl) HotLine() =>
    (
        new[] { new SegmentOut { Name = "HC1", SetpointC = 1150, TRootAC = 1148.0, TRootBC = 1147.0, BaseTRootAC = 1148.5, BaseTRootBC = 1147.5 } },
        new[]
        {
            new FlangeOut { Name = "入口", TDiscMaxC = 1157.0, TTabMaxC = 1140.0 },
            new FlangeOut { Name = "出口", TDiscMaxC = 1149.0, TTabMaxC = 1140.0 },
        }
    );

    // ─────────────────────────────── ①

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 门1_热侧超5K_空管态不影响AllOk_带玻璃态影响(bool emptyTube)
    {
        var (segs, fl) = HotLine();
        var c = new LineCase { SetpointC = new[] { 1150.0 }, EmptyTube = emptyTube };
        var (hot, cold) = LineRunner.ThermocoupleChecks(c, segs, fl);
        // 自证：热侧真的超了、冷侧真的过了（否则下面验的是空气）
        Assert.False(hot.Undetermined);
        Assert.Equal(7.0, hot.Actual, 9);
        Assert.Equal(5.0, hot.Limit, 9);
        Assert.False(hot.Ok);
        Assert.True(cold.Ok && !cold.Undetermined);

        var table = JudgeLikeTable(LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc);
        table.Add(hot); table.Add(cold);
        var r = Result(emptyTube, table);
        _o.WriteLine($"{(emptyTube ? "空管到温稳态" : "带玻璃稳态")}：热侧 {hot.Actual:0.00}/{hot.Limit:0.00} K　Kind {hot.Kind}　AllOk {r.AllOk}　HardOk {r.HardOk}　Failed [{string.Join("；", r.Failed)}]");
        if (emptyTube)
        {
            Assert.Equal(CheckKind.Reference, hot.Kind);
            Assert.StartsWith(LineResult.StateDowngradeNote(emptyTube: true), hot.Note);
            Assert.True(r.AllOk, "空管态热侧超 5 K 把 AllOk 打成了假 —— 空管态只卡电流密度与场的有效性（用户 2026-09-15）");
            Assert.True(r.HardOk);
            Assert.Empty(r.Failed);
            Assert.DoesNotContain(hot, r.HardBlocked);
        }
        else
        {
            Assert.Equal(CheckKind.HardSafety, hot.Kind);
            Assert.DoesNotContain("空管到温稳态", hot.Note);
            Assert.False(r.AllOk, "带玻璃稳态热侧超 5 K 仍报全过");
            Assert.False(r.HardOk);
            Assert.Contains(r.Failed, f => f.Contains(Criteria.Plain(LineResult.Key.HotOverTc)));
        }
        // 说明文字进界面：不许带判据代号
        Assert.False(Criteria.HasCode(LineResult.StateDowngradeNote(true)));
        Assert.All(r.Failed, f => Assert.False(Criteria.HasCode(f), f));
    }

    /// <summary>
    /// ① 的真解版（快：两段三片、开箱网格，与 R48EmptyTubeGateTests 的双工况门同一个算例）：工况位 LineResult.EmptyTube 真接到了整线结果上（新状态位要在下游验），
    /// Judge 真按工况盖了 Kind；把真结果的热侧改成超限 ⇒ 空管 AllOk 不变、带玻璃 AllOk 为假。
    /// </summary>
    [Fact]
    public void 门1b_真解_空管态三条标参考_改热侧超限只动带玻璃的AllOk()
    {
        var p = new DesignInputs { CriteriaRuleSet = Pre };   // 决 103：改回口径下验 K 路那一套
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var lcG = d.BuildCase(p, checkRamp: false);
        var lcE = d.BuildCase(p, checkRamp: false, emptyTube: true);
        var rg = LineRunner.Run(lcG);
        var re = LineRunner.Run(lcE);
        Assert.True(rg.Ok, rg.Message);
        Assert.True(re.Ok, re.Message);
        Assert.False(rg.EmptyTube);
        Assert.True(re.EmptyTube, "工况位没接到整线结果上 —— 必备名单会按带玻璃稳态取");
        Assert.Empty(rg.MissingChecks);
        Assert.Empty(re.MissingChecks);
        foreach (var key in ThreeKeys)
        {
            var g = rg.Find(key); var e = re.Find(key);
            Assert.True(g is not null && e is not null, $"{key} 缺席");
            Assert.Equal(CheckKind.HardSafety, g!.Kind);
            Assert.DoesNotContain(LineResult.StateDowngradeNote(true), g.Note);
            Assert.Equal(CheckKind.Reference, e!.Kind);
            Assert.StartsWith(LineResult.StateDowngradeNote(true), e.Note);
            Assert.True(double.IsFinite(e.Actual), $"空管态 {key} 照常计算：实际值 {e.Actual}");   // 降参考不是不算
        }
        foreach (var q in LineResult.RequiredByStatePre103.Where(q => q.EmptyTubeKind != CheckKind.Reference))
            Assert.Equal(q.EmptyTubeKind, re.Find(q.Prefix)!.Kind);
        _o.WriteLine($"真解：带玻璃 收敛 {rg.Converged} AllOk {rg.AllOk}［{string.Join("；", rg.Failed)}］　空管 收敛 {re.Converged} AllOk {re.AllOk}［{string.Join("；", re.Failed)}］");
        _o.WriteLine("  空管态三条（参考）：" + string.Join("　", ThreeKeys.Select(k => $"{Criteria.Plain(k)} {re.Find(k)!.Actual:0.000}/{re.Find(k)!.Limit:0.###}{(re.Find(k)!.Ok ? "" : " 超")}")));
        _o.WriteLine("  带玻璃三条（硬）：" + string.Join("　", ThreeKeys.Select(k => $"{Criteria.Plain(k)} {rg.Find(k)!.Actual:0.000}/{rg.Find(k)!.Limit:0.###}{(rg.Find(k)!.Ok ? "" : " 超")}")));

        // 下游：改真结果的热侧为超限
        bool eBefore = re.AllOk;
        var eh = re.Find(LineResult.Key.HotOverTc)!;
        eh.Actual = eh.Limit + 2; eh.Ok = false;
        Assert.Equal(eBefore, re.AllOk);
        var gh = rg.Find(LineResult.Key.HotOverTc)!;
        gh.Actual = gh.Limit + 2; gh.Ok = false;
        Assert.False(rg.AllOk, "带玻璃真结果热侧改成超限仍报全过");
        Assert.Contains(gh, rg.HardBlocked);
    }

    // ─────────────────────────────── ②

    private static string Code(string rel)
    {
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel));
        return string.Join("\n", src.Split('\n').Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void 门2_源码门_分工况只有一处实现()
    {
        string root = HandoverDoc.Root();
        var files = Directory.GetFiles(Path.Combine(root, "Pt_Optimize"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.True(files.Length > 20, "自证：源码档一个都没读到");
        // 分工况表的两列名只许出现在 LineRunner.cs（表本体 + RequiredFor + StateKindOf）
        foreach (var f in files)
        {
            string rel = Path.GetRelativePath(root, f);
            string code = Code(rel);
            if (rel.EndsWith("LineRunner.cs", StringComparison.Ordinal)) continue;
            Assert.DoesNotMatch(new Regex(@"\b(GlassKind|EmptyTubeKind)\b"), code);
        }
        string lr = Code(Path.Combine("Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("public static readonly (string Prefix, CheckKind GlassKind, CheckKind EmptyTubeKind, bool NeedsRamp)[] RequiredByState", lr);
        Assert.DoesNotMatch(new Regex(@"\)\[\]\s+Required\s*="), lr);                 // 旧的静态名单不许回来
        Assert.Contains("ApplyStateCriteria(c.EmptyTube, checks, rs);", lr);             // Judge 末尾按工况盖 Kind（决 103：带口径）
        // 2026-09-16 Opus 5（审查 S6）：原来核 "EmptyTube = c.EmptyTube }" 一处即过 —— 它在 RunOnce 与管侧单解各一处，删一处门不红。
        //   改成：LineRunner.cs 里每一处 new LineResult 都写工况位，且恰是两处（多一处构造点也要来这里登记）。
        var ctors = Regex.Matches(lr, @"new LineResult\s*\{[^}]*\}").Cast<Match>().ToArray();
        Assert.Equal(2, Regex.Matches(lr, @"new LineResult\b").Count);
        Assert.Equal(2, ctors.Length);
        Assert.All(ctors, m => Assert.Contains("EmptyTube = c.EmptyTube", m.Value));
        Assert.Contains("public string[] MissingChecks => RequiredFor(EmptyTube, RuleSet)", lr);   // 决 103：名单按结果自己的口径取
        Assert.Matches(new Regex(@"public bool AllOk => Converged && MissingChecks\.Length == 0 && FieldUndeterminedReasons\.Length == 0"), lr);
        Assert.Contains("&& FieldUndeterminedReasons.Length == 0;", lr);                 // HardOk

        // 消费者只查表、不自带清单
        foreach (var rel in new[] { Path.Combine("Pt_Optimize", "Core", "InsulationSearch.cs"), Path.Combine("Pt_Optimize", "Core", "MeshVerify.cs"), Path.Combine("Pt_Optimize", "Core", "Criteria.cs") })
        {
            string code = Code(rel);
            Assert.Contains("LineResult.StateKindOf(", code);
            Assert.DoesNotMatch(new Regex(@"(?i)emptytube\s*\?\s*CheckKind"), code);              // 按工况三元式选 Kind
            Assert.DoesNotMatch(new Regex(@"\b(s|state)\s*==\s*1\s*\?\s*CheckKind"), code);        // 按态号选 Kind
            Assert.DoesNotMatch(new Regex(@"\bKind\s*=\s*CheckKind\."), code);                    // 自己给判据项赋 Kind
        }
        string ins = Code(Path.Combine("Pt_Optimize", "Core", "InsulationSearch.cs"));
        Assert.Contains("bool emptyTube = det.Line.Cases[s]!.EmptyTube;", ins);          // 工况位取自解出本态的整线算例，不按态号猜
        Assert.Contains("bool judged = StateHasHardPlateTerms(emptyTube, ctx.O);", ins);  // 逐格点：闭合不成立只在有卡交付项的态打成判不了
        Assert.Contains("bool judged = StateHasHardPlateTerms(line.Cases[s]!.EmptyTube, ctx.O);", ins);   // 整轮：γ 只在有卡交付项的态必须为正（原来两态都要求）
        Assert.Contains("if (tubeBad.Length > 0 && judged)", ins);
        Assert.Contains("o.Terms.AddRange(EvaluateView(v, ctx.O));", ins);               // 评估函数只经 EvaluateView 调（它盖工况位）
        Assert.Equal(1, Regex.Matches(ins, @"\.Criteria\(v, ").Count);                     // 只有 EvaluateView 里那一次
        Assert.Contains("var (ok, why) = FinalLineVerdict(lz.Value);", ins);             // 终点整线判定接上了
        Assert.Contains("rep.Layers.Where(l => l.Feasible &&", ins);                     // ★ 建议层读层可行（含终点整线判定）
        Assert.Contains("whys[s] = LineStateWhy(r, s);", ins);                          // 2026-09-16（审查 M2）：整线一态能不能用只经 LineStateWhy（行为门 门2c）
        Assert.Contains("public static string LineStateWhy(LineResult? r, int s)", ins);
        Assert.Contains("var bad = r.FieldUndeterminedReasons;", ins);                   // 它读唯一一份场有效性（不许再回到只看 FieldsConverged）
        Assert.DoesNotContain("r.Flanges.Any(f => !f.FieldsConverged)", ins);
        Assert.Contains("lr.FinalLineAllOk = ok; lr.FinalLineWhy = why;", ins);          // 2026-09-16（审查 S5）：层可行接线 —— 只有这道源码门，无行为门（HANDOVER 记）
        Assert.Contains("if (judged) { o.Determined = false; o.Why = $\"{StateNames[s]}：{sh.Why}\"; return o; }", ins);   // 2026-09-16（审查 S3）：空管态闭合不成立按冻结管根报参考值 —— 只有这道源码门
        Assert.DoesNotContain("尚未落地", ins);
        string mv = Code(Path.Combine("Pt_Optimize", "Core", "MeshVerify.cs"));
        Assert.Contains("var tol = TolTemplate(lc);", mv);
        Assert.Contains("if (RefuseForState(lc) is { } stateRefused)", mv);
    }

    [Fact]
    public void 门2b_消费者跟着同一张表走_行为一致()
    {
        var opt = new Options();
        foreach (bool et in new[] { false, true })
        {
            foreach (var (name, key) in PlateTerms)
            {
                var t = new Criterion { Name = name, LineKey = key, EmptyTube = et };
                Assert.Equal(LineResult.StateKindOf(key, et, Pre), t.Kind);   // 决 103：保温搜索逐格点钉在改前口径（InsulationSearch.RuleSetOfSearch）
            }
            Assert.Equal(PlateTerms.Any(pt => LineResult.StateKindOf(pt.LineKey, et, Pre) != CheckKind.Reference), StateHasHardPlateTerms(et, opt));
            var want = MeshVerify.MeshTolerances.Where(m => LineResult.StateKindOf(m.Key, et, Pre) != CheckKind.Reference).Select(m => Criteria.Plain(m.Key)).ToArray();
            Assert.Equal(want, MeshVerify.TolTemplate(new LineCase { EmptyTube = et, Base = new DesignInputs { CriteriaRuleSet = Pre } }).Select(x => x.Name).ToArray());
            foreach (var q in LineResult.RequiredByState)
            {
                var k = et ? q.EmptyTubeKind : q.GlassKind;
                Assert.Equal(k, LineResult.StateKindOf(q.Prefix + " 带后缀的判据名", et));
                Assert.Equal(k != CheckKind.Reference, LineResult.RequiredFor(et).Any(r => r.Prefix == q.Prefix));
            }
        }
        // 现表：带玻璃三条卡、空管三条只作参考（改表时这里一起改 —— 这是「跑前写死」的口径，不是跟着表自动变的）
        Assert.True(StateHasHardPlateTerms(false, opt));
        Assert.False(StateHasHardPlateTerms(true, opt));
        Assert.Null(MeshVerify.RefuseForState(new LineCase { EmptyTube = false, Base = new DesignInputs { CriteriaRuleSet = Pre } }));   // 决 103：改回口径不拒答；生产口径带玻璃拒答（R48CriteriaSwapGateTests 门_拒答）
        string? refused = MeshVerify.RefuseForState(new LineCase { EmptyTube = true });
        Assert.NotNull(refused);
        Assert.False(Criteria.HasCode(refused), refused);
        Assert.Contains("不能据此说这个设计过了", refused);
        // 决 103：界面图例描述生产口径 ⇒ 空管态只作参考的是新两条与两条热稳定（三条旧判据两态都只作参考，不带这个标记）
        Assert.Equal(new[] { LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat, LineResult.Key.FlangeStab, LineResult.Key.LocalStab }.OrderBy(x => x, StringComparer.Ordinal),
                     Criteria.All.Where(e => e.ReferenceWhenEmptyTube).Select(e => e.Key).OrderBy(x => x, StringComparer.Ordinal));
        // 注入的评估函数：两态一律按「有卡交付的项」处理管侧响应（保守）
        Assert.True(StateHasHardPlateTerms(true, new Options { Criteria = (v, o) => new List<Criterion>() }));
    }

    // ─────────────────────────────── ③

    /// <summary>八种「场判不了」的情形（门3 与 门2c 共用；每种都只置位、不写判据文字）。2026-09-16 Opus 5 从门3 里提出来。</summary>
    private static (string What, Action<LineResult> Break, string[] Named)[] FieldBreaks() => new (string, Action<LineResult>, string[])[]
    {
        ("温度场没收敛", r => r.Flanges = new[] { new FlangeOut { Name = "入口", FieldsConverged = false, FieldNote = "温度场未收敛（步长 1.00E-002 K，40 轮）" } }, new[] { "入口", "温度场未收敛" }),
        ("电位场没收敛", r => r.Flanges = new[] { new FlangeOut { Name = "出口", FieldsConverged = false, FieldNote = "电位场未收敛（残差 3.00E-003，5000 轮）" } }, new[] { "出口", "电位场未收敛" }),
        ("法兰散热表超界", r => r.Flanges = new[] { new FlangeOut { Name = "入口", FieldsConverged = false, FieldNote = "最高温 1800 °C 超过表面散热表上限 1768 °C（铂熔点），超出部分散热被钳住" } }, new[] { "入口", "散热表上限" }),
        ("越过铂熔点", r => { r.Ok = false; r.Message = "入口：峰值 1900 °C 已越过铂熔点 1768 °C —— **该解不存在**"; r.OverMelt = true; }, new[] { "越过铂熔点" }),
        ("管表超界", r => r.Segments = new[] { new SegmentOut { Name = "HC1", TubeLossTableExceeded = true, TubeTMaxC = 1700, TubeLossTableHiC = 1550 } }, new[] { "HC1", "管表面散热表上限" }),
        ("压接段盖到管孔", r => r.Flanges = new[] { new FlangeOut { Name = "入口", ClampCoversHole = true } }, new[] { "入口", "盖到了管孔" }),
        ("压接段伸进圆盘", r => r.Flanges = new[] { new FlangeOut { Name = "出口", ClampIntoDisc = true } }, new[] { "出口", "伸进了圆盘" }),
        ("保温分界判不了", r => r.Flanges = new[] { new FlangeOut { Name = "入口", InsulBoundaryUndetermined = true } }, new[] { "入口", "保温分界判不了" }),
    };

    /// <summary>
    /// ★ 2026-09-16 Opus 5（K 路复审修，审查 M1/M2）：保温搜索整线一态「能不能用」的判法 <see cref="InsulationSearch.LineStateWhy"/> 的**行为门**。
    /// 病：上一轮审查注入把 SolveLineCore 那句改回 `else if (r.Flanges.Any(f => !f.FieldsConverged))` 后没还原，树与补丁差一行，而 8 组门全绿（门2 只核字符串）
    /// ⇒ 压接盖孔／压接进盘／管表超界／保温分界判不了在整线解上拦不住。现在直接调公开纯函数：三个位都为真、只有压接盖孔 ⇒ 必须非空且点名。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 门2c_整线一态的场判不了_LineStateWhy必须非空且点名(bool emptyTube)
    {
        int s = emptyTube ? 1 : 0;
        var okR = Result(emptyTube, JudgeLikeTable());
        Assert.Equal("", LineStateWhy(okR, s));
        Assert.Contains("没有整线结果", LineStateWhy(null, s));

        // 审查 M1 点的那一处：Ok、Converged、每片 FieldsConverged 都为真，只有压接盖孔
        var clamp = Result(emptyTube, JudgeLikeTable());
        clamp.Flanges = new[] { new FlangeOut { Name = "入口", FieldsConverged = true, ClampCoversHole = true } };
        Assert.True(clamp.Ok && clamp.Converged && clamp.Flanges.All(f => f.FieldsConverged), "自证：三个位都为真");
        string w = LineStateWhy(clamp, s);
        _o.WriteLine($"{StateNames[s]}·只有压接盖孔：「{w}」");
        Assert.NotEqual("", w);
        Assert.StartsWith(StateNames[s], w);
        Assert.Contains("场判不了", w);
        Assert.Contains("盖到了管孔", w);

        // 八种情形每种都非空、点名
        foreach (var (what, brk, named) in FieldBreaks())
        {
            var r = Result(emptyTube, JudgeLikeTable());
            brk(r);
            string why = LineStateWhy(r, s);
            _o.WriteLine($"{StateNames[s]}·{what}：「{why}」");
            Assert.NotEqual("", why);
            Assert.StartsWith(StateNames[s], why);
            foreach (var n in named) Assert.Contains(n, why);
            Assert.False(Criteria.HasCode(why), why);
        }
        // 解不出、没收敛各有自己的句（次序：解不出 → 没收敛 → 场判不了）
        var nc = Result(emptyTube, JudgeLikeTable()); nc.Converged = false; nc.Message = "段↔法兰耦合未收敛（慢，加轮数可解）";
        Assert.Contains("外层耦合没收敛", LineStateWhy(nc, s));
        var no = Result(emptyTube, JudgeLikeTable()); no.Ok = false; no.Converged = false; no.Message = "算例被拒";
        Assert.Contains("整线解不出", LineStateWhy(no, s));
        Assert.DoesNotContain("外层耦合没收敛", LineStateWhy(no, s));
    }

    /// <summary>
    /// ★ 2026-09-16 Opus 5（K 路复审修，审查 S4）：加密复算对空管到温稳态拒答的**行为门**（此前只有源码门：注入只删 `return res;` 全绿）。
    /// 工厂重载造出空管算例 ⇒ 判词 = RefuseForState 的拒答句、不解（Line 空、Trace 空、工厂只被叫一次、几秒内返回）。
    /// </summary>
    [Fact]
    public void 门2d_加密复算_工厂造空管算例_拒答且不解()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        int calls = 0;
        var sw = Stopwatch.StartNew();
        var res = MeshVerify.Run((hMid, hInner) =>
        {
            calls++;
            var lc = d.BuildCase(p, checkRamp: false, emptyTube: true);
            lc.MeshFineMm = hMid; lc.MeshInnerMm = hInner;
            return lc;
        }, 2.0, 20.0, 15.0, maxCells: 40000, maxRounds: 2);   // 两档：注入「不 return」时真去解也只要两档就红（工厂被叫两次、Trace 非空）
        _o.WriteLine($"工厂被叫 {calls} 次，{sw.Elapsed.TotalSeconds:0.0} s；判词「{res.Verdict}」");
        Assert.Equal(1, calls);
        Assert.False(res.Converged);
        Assert.Null(res.Line);
        Assert.Empty(res.Trace);
        Assert.Equal(MeshVerify.RefuseForState(new LineCase { EmptyTube = true }), res.Verdict);
        Assert.Contains("不能据此说这个设计过了", res.Verdict);
        Assert.False(Criteria.HasCode(res.Verdict), res.Verdict);
        Assert.DoesNotContain("--", res.Verdict);
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"拒答不该去解场（{sw.Elapsed.TotalSeconds:0} s）");
        // 对照：带玻璃稳态不拒答（真解走主循环，见 MeshVerifyLineCaseTests 的慢门）—— 决 103：只在改回口径下成立（生产口径带玻璃另有拒答，见 R48CriteriaSwapGateTests）
        Assert.Null(MeshVerify.RefuseForState(new LineCase { EmptyTube = false, Base = new DesignInputs { CriteriaRuleSet = Pre } }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 门3_场判不了_AllOk为假且判词点名(bool emptyTube)
    {
        LineResult Valid() => Result(emptyTube, JudgeLikeTable());
        var ctl = Valid();
        Assert.True(ctl.AllOk, "自证：底表本身该全过 —— " + string.Join("；", ctl.Failed));
        Assert.True(ctl.HardOk);
        Assert.Empty(ctl.FieldUndeterminedReasons);

        var cases = FieldBreaks();
        foreach (var (what, brk, named) in cases)
        {
            var r = Valid();
            brk(r);
            // 生产里场判不了时吃法兰场的判据会被后置遍历标成判不了 —— 这里照做，空管态这几条是参考量，拦不住 AllOk（自证下面的守门是唯一拦它的东西）
            foreach (var ck in r.Checks.Where(ck => ThreeKeys.Any(k => ck.Name.StartsWith(k, StringComparison.Ordinal))))
            { ck.Undetermined = true; ck.Ok = false; }
            bool judgedAlone = r.Converged && r.MissingChecks.Length == 0 && r.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target).All(ck => ck.Ok && !ck.Undetermined);
            if (emptyTube) Assert.True(judgedAlone, $"{what}：自证 —— 空管态只看判据表会报全过（三条都是参考），守门必须另外拦");
            Assert.False(r.AllOk, $"{(emptyTube ? "空管到温稳态" : "带玻璃稳态")}·{what}：场判不了却报全过");
            Assert.False(r.HardOk, $"{what}：HardOk 也不许为真");
            Assert.NotEmpty(r.FieldUndeterminedReasons);
            foreach (var n in named)
                Assert.Contains(r.Failed, f => f.Contains("本工况的场判不了", StringComparison.Ordinal) && f.Contains(n, StringComparison.Ordinal));
            Assert.All(r.Failed, f => Assert.False(Criteria.HasCode(f), f));
            _o.WriteLine($"{(emptyTube ? "空管" : "带玻璃")}·{what}：AllOk {r.AllOk}　Failed［{string.Join("；", r.Failed)}］");
        }
    }

    // ─────────────────────────────── ④

    private static PointOutcome Out(double m) => new() { Terms = { new Criterion { Name = "甲", State = "态一", NormMargin = m } } };

    private static StartsOutcome FeasibleStarts()
    {
        var pts = GridPoints(1, 1);
        RoundRecord Round(int r, Selection sel)
        {
            var ch = new[] { ChoosePlate(0, pts, (dd, ww) => Out(0.5 - 0.1 * (dd + ww)), 1, 0.01) };
            return new RoundRecord { Choices = ch, Next = new Selection(ch.Select(c => c.Chosen.Disc).ToArray(), ch.Select(c => c.Chosen.Tab).ToArray()) };
        }
        return RunStarts(Selection.Uniform(1, 0, 0), Selection.Uniform(1, 1, 1), Round, 5, parallel: false);
    }

    [Fact]
    public void 门4_保温搜索_管J超限而三条都过_终点整线判不可行()
    {
        LineResult Mk(bool et, Action<List<ConstraintOut>>? mut = null)
        {
            var t = JudgeLikeTable();
            mut?.Invoke(t);
            return Result(et, t);
        }
        var good = new LineSolve { ByState = new LineResult?[] { Mk(false), Mk(true) } };
        var gv = FinalLineVerdict(good);
        Assert.True(gv.Ok, "自证：两态底表都全过 —— " + gv.Why);

        void TubeJOver(List<ConstraintOut> t) { var tj = t.First(c => c.Name.StartsWith(LineResult.Key.TubeJ, StringComparison.Ordinal)); tj.Ok = false; tj.Actual = 13; tj.Limit = 12; }
        var bad = new LineSolve { ByState = new LineResult?[] { Mk(false, TubeJOver), Mk(true) } };
        foreach (var k in ThreeKeys) Assert.True(bad.ByState[0]!.Find(k)!.Ok, $"自证：{k} 应当过（逐片三项都过）");
        var bv = FinalLineVerdict(bad);
        _o.WriteLine($"管 J 超限（带玻璃）：{bv.Ok}　{bv.Why}");
        Assert.False(bv.Ok, "逐片三项都过、管 J 超限的整线结果被判成可行 —— 保温搜索只读了三条");
        Assert.Contains("管 J", bv.Why);
        Assert.Contains(InsulationSearch.StateGlass, bv.Why);

        var badE = new LineSolve { ByState = new LineResult?[] { Mk(false), Mk(true, TubeJOver) } };
        Assert.False(FinalLineVerdict(badE).Ok, "空管态管 J 超限（空管态仍是硬判据）被放过");

        // 空管态场判不了 ⇒ 终点不可行
        var eInvalid = Mk(true);
        eInvalid.Flanges = new[] { new FlangeOut { Name = "入口", FieldsConverged = false, FieldNote = "温度场未收敛" } };
        var iv = FinalLineVerdict(new LineSolve { ByState = new LineResult?[] { Mk(false), eInvalid } });
        Assert.False(iv.Ok);
        Assert.Contains("温度场未收敛", iv.Why);
        // 没收敛 ⇒ 不可行并说出来
        var nc = Mk(false); nc.Converged = false; nc.Message = "段↔法兰耦合未收敛（慢，加轮数可解）";
        var ncv = FinalLineVerdict(new LineSolve { ByState = new LineResult?[] { nc, Mk(true) } });
        Assert.False(ncv.Ok);
        Assert.Contains("外层耦合没收敛", ncv.Why);
        // 缺一态 ⇒ 不可行
        Assert.False(FinalLineVerdict(new LineSolve { ByState = new LineResult?[] { Mk(false), null } }).Ok);

        // 层可行 = 逐片可行 且 终点整线两态全过（★ 建议层只从这里取）
        var so = FeasibleStarts();
        Assert.True(so.Feasible, "自证：合成链逐片可行");
        Assert.True(new LayerResult { Starts = so, FinalLineAllOk = true }.Feasible);
        Assert.False(new LayerResult { Starts = so, FinalLineAllOk = false }.Feasible);
        Assert.False(new LayerResult { Starts = so }.Feasible);
    }

    // ─────────────────────────────── ⑤

    private static Criterion T(string key, bool et, double m) => new()
    {
        Name = PlateTerms.First(p => p.LineKey == key).Name, LineKey = key, EmptyTube = et,
        State = et ? StateEmpty : StateGlass, NormMargin = m, Strict = key == LineResult.Key.NetFlux,
    };

    private static PointOutcome P(double glassHot, double emptyHot) => new()
    {
        Terms =
        {
            T(LineResult.Key.HotOverTc, false, glassHot), T(LineResult.Key.ColdUnderTc, false, 0.5), T(LineResult.Key.NetFlux, false, 0.5),
            T(LineResult.Key.HotOverTc, true, emptyHot), T(LineResult.Key.ColdUnderTc, true, -0.3), T(LineResult.Key.NetFlux, true, double.NaN),
        },
    };

    [Fact]
    public void 门5_保温搜索逐格点_空管态参考项不进可行集与排序_工况位由EvaluateView盖()
    {
        var p = P(0.2, -2.0);
        Assert.True(p.Feasible, "空管态热侧、冷侧超限、净流入算不出，都是参考项，不许让格点不可行");
        Assert.Equal(0.2, p.MinNormMargin, 12);
        Assert.Equal(StateGlass, p.Worst!.State);
        Assert.Equal(3, p.HardTerms.Count());
        Assert.False(P(-0.1, 9.0).Feasible, "自证：带玻璃态热侧不过仍不可行");

        // 反向自证：同样的项全按带玻璃稳态盖工况位 ⇒ 空管那三项变成卡交付 ⇒ 不可行
        var asGlass = P(0.2, -2.0);
        foreach (var t in asGlass.Terms) t.EmptyTube = false;
        Assert.False(asGlass.Feasible);

        var pts = GridPoints(4, 2);
        var ch = ChoosePlate(0, pts, (dd, ww) => P(0.3 - 0.1 * dd, -5 + 3 * ww), 1, 0.01);
        Assert.Equal(0, ch.UndeterminedCount);                       // 空管净流入 NaN 是参考项，不把格点打成判不了
        Assert.Contains(StateEmpty, ch.ReferenceOnlyStates);
        Assert.False(ch.FeasibleByState.ContainsKey(StateEmpty));    // 空管态不数「单态可行」（不许印成 0 个窗口）
        Assert.Equal(pts.Count(t => 0.3 - 0.1 * t.Disc >= 0), ch.FeasibleIn(StateGlass));
        Assert.Equal((0, 0), (ch.Chosen.Disc, ch.Chosen.Tab));        // 排序只看带玻璃：空管热侧「更好」的舌层不因此被选
        Assert.Equal(pts.Count(t => 0.3 - 0.1 * t.Disc >= 0), ch.FeasibleCount);

        // 评估函数写漏工况位 ⇒ EvaluateView 按 v 盖上
        var forgetful = new Options { Criteria = (v, o) => new List<Criterion> { new() { Name = HotName, LineKey = LineResult.Key.HotOverTc, NormMargin = -1 } } };
        var ve = EvaluateView(new PlateStateView { StateName = StateEmpty, EmptyTube = true }, forgetful);
        Assert.True(ve.Single().IsReference);
        Assert.Equal(StateEmpty, ve.Single().State);
        var vg = EvaluateView(new PlateStateView { StateName = StateGlass, EmptyTube = false }, forgetful);
        Assert.False(vg.Single().IsReference);
        Assert.Contains("本态只作参考", ve.Single().Show());
        // 默认评估函数三项都写上了对应的整线判据
        var dv = DefaultCriteria(new PlateStateView { StateName = StateEmpty, EmptyTube = true, HotLimitK = 5, ColdLimitK = 5, ReferenceC = 1150, DiscPeakC = 1151, TabPeakC = 1150, RootHotC = 1150, RootColdC = 1149, NetInflowW = 1, GammaKPerW = 2 }, new Options());
        Assert.Equal(PlateTerms.Select(x => x.LineKey).OrderBy(x => x, StringComparer.Ordinal), dv.Select(x => x.LineKey).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(dv, x => Assert.True(x.IsReference));
    }
}
