using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F7 细区半径与舌长解耦 —— 2026-09-23，Opus 5.5（C4 起稿；C4′ 按决 29 改定为「自适应」改写）
//  本档只留两道快门：F7 门 a 解耦门、改回门。自适应的门（F7 门 b 初值覆盖、c 单向、d 终止、e 改回逐位、f 单一来源、g 初值敏感度）与开 − 关归因在 R48F7AdaptiveRadiusTests.cs。
//  ⚠ C4 第一版（决 29 (1)：半径 = max(盘半径, 孔半径) + 10）在本档里的三个慢档（改前探针、开关归因兼门 b、成本门同算例）已删：
//    它们量的是作废的 (1) 规则（与热点检查相顶，见实施记录 §3.2、§10）。那批证据档的现状（审查 T4 更正「只作历史」一句）：
//    · …_141431（改前探针）= F7 门 e 逐位对拍的**活参照**，也是开 − 关归因的「改前」列；产出它的源码已存档 deliverable/R48_F7_改前探针源码_2026-09-23.cs.txt（只能在 F7 之前的树上编译）；
//    · …_151354（(1) 开关归因）= 归因的「(1) 固定 40」对照列（(1) 作废，只印）；
//    · …_142010（(1) 门 a）、…_153611（(1) 成本门）只作历史。
// ════════════════════════════════════════════════════════════════════════════
public class R48F7FineRadiusDecoupleTests
{
    private readonly ITestOutputHelper _o;
    public R48F7FineRadiusDecoupleTests(ITestOutputHelper o) { _o = o; }
    internal const string Sign = "2026-09-23，Opus 5.5（C4′，决 29 自适应）";

    /// <summary>门里一律用缺省工艺参数（与门 f、归因同一份输入：R48NMeshGateTests 用的也是 new DesignInputs()）。</summary>
    // 决 103（2026-09-24）：有意改动 —— 加密复算（MeshVerify）的逐档比对列只在改回口径（CriteriaRuleSet = 决103前）下成立，生产口径的带玻璃稳态拒答；
    //   F7 各门守的是细区半径计划与加密复算主循环的控制流（记录值都是改前口径下量的），参数表一律走改回口径。细区半径计划本身不读判据口径（逐位不变）。
    internal static DesignInputs P0() => new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 };

    /// <summary>改回旧规则的半径（= F7 之前的生产）。</summary>
    internal static double OldRadius(DesignSpec d) => MeshVerify.FineRadiusPlanFor(d, P0(), MeshVerify.LegacyTabLengthFactor).RadiusMm;
    /// <summary>生产计划的初值（max(盘半径, 孔半径) + ℓ_t）。</summary>
    internal static double NewInitial(DesignSpec d) => MeshVerify.FineRadiusPlanFor(d, P0()).InitialMm;

    /// <summary>F7 之前的半径公式原文（MeshVerify.cs 改前两处的写法，只给改回门对照）。</summary>
    internal static double PreF7Radius(double disc, double tabLen, double hole) => MeshAdapt.RequiredFineRadiusMm(new[] { disc, Math.Abs(tabLen) * 0.35 }, hole);
    internal static bool Bits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>手造的图纸分析结果（只有 RequiredMeshFor(Shape) 读的那几项）：一级环 孔 → 孔 + 3、盘半径、舌端 x。</summary>
    internal static PlateShapeAnalyzer.Shape ShapeOf(double disc, double hole, double tabLen)
    {
        var sh = new PlateShapeAnalyzer.Shape { HoleRadiusMm = hole, DiscRadiusMm = disc, TabEndXMm = -tabLen, StepMm = 0.1 };
        sh.Levels.Add(new PlateShapeAnalyzer.Level { ThicknessMm = 1.0, RInnerMm = hole, ROuterMm = hole + 3.0 });
        return sh;
    }

    static IEnumerable<double> TabScan() => Enumerable.Range(0, 16).Select(i => 80.0 + 10.0 * i);   // 80, 90, …, 230

    // ═══════════════════════════════════════════════ 门 a　解耦门（快）
    /// <summary>
    /// 门 a（解耦）：固定盘半径与孔半径，舌长 80 → 230 每 10 扫，生产计划的**初值**不变、恰为 max(盘半径, 孔半径) + ℓ_t（ℓ_t 只看管、与舌长无关）；
    /// 同一扫描上改回旧规则（传 LegacyTabLengthFactor）半径随舌长连续变 ⇒ 同一判法对旧规则当场红（「改回 ⇒ 红」）。
    /// 舌长 80～230 的出处：C4 详稿「固定盘径、舌长从 80 扫到 230」（开发执行计划各组详稿 C4 完成定义）；W08／W06 现役 140 在其中。
    /// 覆盖：MeshVerify.FineRadiusPlanFor 两个入口（解析设计：W08／W06 现役设计 = R48NMeshGateTests.Design；图纸分析结果：手造 Shape，盘 30／孔 25.8 与 盘 34／孔 25.6，热长度按 W08 的整线算例）。
    /// 不覆盖：放大（放大读解出来的热点，舌长会经热点位置影响终值 —— 那是自适应本来的意思，见门 b／c）；真图纸（要 Windows ＋ Rhino）；网格与解。
    /// 阈值：逐位相等（== 比较 double），不是容差 —— 初值按构造就不读舌长。
    /// </summary>
    [Fact]
    public void 门a_解耦门_舌长80到230每10_新规则细区半径不变_改回当场红()
    {
        string file = DeliverableOut.Stamped("R48_F7_门a_解耦门_舌长扫描.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("F7′ 门 a 解耦门：固定盘半径与孔半径，舌长 80～230 每 10；生产计划初值必须不变（逐位），改回旧规则当场变（改回 ⇒ 红）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W("生产初值 = MeshVerify.FineRadiusPlanFor(设计, 缺省工艺参数).InitialMm = max(盘半径, 孔半径) + ℓ_t（MeshAdapt.ThermalLengthMm）；旧规则 = 同一入口传 MeshVerify.LegacyTabLengthFactor（0.35）。");
        W();
        var bad = new List<string>();
        void Check(string what, IReadOnlyList<(double L, double nw, double old)> rows, double expect, string expectWhy)
        {
            W($"── {what}　期望初值 = {expect:R} mm（{expectWhy}）");
            W("舌长\t生产初值\t旧半径(改回)");
            foreach (var (L, nw, old) in rows) W($"{L:0}\t{nw:R}\t{old:R}");
            bool newConst = rows.All(x => Bits(x.nw, expect));
            bool oldConst = rows.All(x => Bits(x.old, rows[0].old));
            W($"生产初值不变：{(newConst ? "是" : "**否**")}　旧规则半径不变：{(oldConst ? "是（改回对照没牙）" : "否 ⇒ 同一判法对旧规则红（改回 ⇒ 红）")}　旧规则范围 {rows.Min(x => x.old):0.0} → {rows.Max(x => x.old):0.0} mm");
            W();
            if (!newConst) bad.Add(what + "：生产初值随舌长变");
            if (oldConst) bad.Add(what + "：旧规则在扫描上也不变 ⇒ 门没牙");
        }
        foreach (var which in new[] { "W08", "W06" })
        {
            var d0 = R48NMeshGateTests.Design(which);
            var (ell, ellSrc) = MeshAdapt.ThermalLengthMm(d0.BuildCase(P0(), checkRamp: false));
            var rows = TabScan().Select(L => { var d = d0.Clone(); d.TabLengthMm = L; return (L, NewInitial(d), OldRadius(d)); }).ToList();
            Check($"解析设计 {which}（盘半径 {d0.DiscRadiusMm:0.###}、孔半径 {d0.HoleRadiusMm:0.###}）", rows,
                  MeshAdapt.RequiredFineRadiusMm(new[] { d0.DiscRadiusMm }, d0.HoleRadiusMm, ell), ellSrc);
            var at140 = rows.First(x => x.L == 140.0);
            W($"{which} 舌长 140（现役）：旧 {at140.Item3:0.0}、生产初值 {at140.Item2:0.0} mm"); W();
            if (which == "W08" && !Bits(at140.Item3, 59.0)) bad.Add($"W08 舌长 140 旧规则应为 59.0（门 f 记录），实际 {at140.Item3:R}");
        }
        var lcW08 = R48NMeshGateTests.Design("W08").BuildCase(P0(), checkRamp: false);
        var (ellW08, ellW08Src) = MeshAdapt.ThermalLengthMm(lcW08);
        foreach (var (disc, hole) in new[] { (30.0, 25.8), (34.0, 25.6) })
        {
            var rows = TabScan().Select(L =>
            {
                var sh = ShapeOf(disc, hole, L);
                var nw = MeshVerify.RequiredMeshFor(sh, 0.8, lcW08);
                var old = MeshVerify.RequiredMeshFor(sh, 0.8, lcW08, tabLengthFactor: MeshVerify.LegacyTabLengthFactor);
                Assert.Null(nw.Refused); Assert.Null(old.Refused);
                return (L, nw.RadiusMm, old.RadiusMm);
            }).ToList();
            Check($"图纸分析结果（手造 Shape：盘半径 {disc}、孔半径 {hole}；热长度按 W08 整线算例）", rows, Math.Max(disc, hole) + ellW08, ellW08Src);
        }
        W($"── 不过 {bad.Count}{(bad.Count > 0 ? "：" + string.Join("；", bad) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad.Count == 0, string.Join("；", bad));
    }

    // ═══════════════════════════════════════════════ 改回门（快）
    /// <summary>
    /// 改回门：传 LegacyTabLengthFactor 时两个入口的细区半径与 F7 之前的公式原文**逐位**相同、计划不放大；细区尺寸、内带半径与拒答与开关无关；
    /// 生产（不传）在 W08／W06 现役设计上确实与旧规则不同（开关是活的）；负数与 NaN 系数抛异常；抽出的 MeshVerify.HotspotVerdict 与改前三行原文逐字同句。
    /// 覆盖：盘半径 26～36 每 0.5、舌长 −240～240 每 10（含负号舌长）、孔半径 25.6／25.8（解析设计与手造 Shape 两个入口）；热点句 7 种 NaN／远近组合。
    /// 不覆盖：改回之后网格与整线解逐位等于改前 —— 那是慢门 门 e（R48F7AdaptiveRadiusTests）的事；这里只证「半径这一个数逐位相同」。
    /// 阈值：逐位（DoubleToInt64Bits），不是容差。
    /// </summary>
    [Fact]
    public void 门_改回参数逐位等于改前公式_两个重载_热点句抽出逐字同句()
    {
        int n = 0;
        var lcW08 = R48NMeshGateTests.Design("W08").BuildCase(P0(), checkRamp: false);
        foreach (var which in new[] { "W08", "W06" })
        {
            var d0 = R48NMeshGateTests.Design(which);
            foreach (double hole in new[] { 25.6, 25.8 })
                for (double disc = 26.0; disc <= 36.0 + 1e-9; disc += 0.5)
                    for (double L = -240.0; L <= 240.0 + 1e-9; L += 10.0)
                    {
                        var d = d0.Clone(); d.DiscRadiusMm = disc; d.TabLengthMm = L; d.WallMm = hole - 0.5 * d.TubeIdMm;
                        var plan = MeshVerify.FineRadiusPlanFor(d, P0(), MeshVerify.LegacyTabLengthFactor);
                        Assert.True(Bits(plan.RadiusMm, PreF7Radius(d.DiscRadiusMm, d.TabLengthMm, d.HoleRadiusMm)), $"{which} R{disc} L{L}：改回半径 {plan.RadiusMm:R} 不等于改前公式");
                        Assert.False(plan.Adaptive);
                        var old = MeshVerify.RequiredMeshFor(d, P0(), tabLengthFactor: MeshVerify.LegacyTabLengthFactor);
                        Assert.True(Bits(old.RadiusMm, plan.RadiusMm));
                        Assert.True(Bits(old.FineMm, MeshVerify.RequiredFineMmFor(d)), "细区尺寸不该随半径规则变");
                        var sh = ShapeOf(disc, d.HoleRadiusMm, Math.Abs(L));
                        var so = MeshVerify.RequiredMeshFor(sh, 0.8, lcW08, tabLengthFactor: MeshVerify.LegacyTabLengthFactor);
                        Assert.True(Bits(so.RadiusMm, PreF7Radius(disc, Math.Abs(L), d.HoleRadiusMm)), $"Shape R{disc} L{L}：改回半径不等于改前公式");
                        // 细区尺寸、内带半径、拒答与半径规则无关：生产入口（要量热长度，较慢）每个 (盘半径, 孔半径) 比一次（舌长只进半径）
                        if (L == 140.0)
                        {
                            var sn = MeshVerify.RequiredMeshFor(sh, 0.8, lcW08);
                            Assert.True(Bits(sn.FineMm, so.FineMm) && Bits(sn.InnerRadiusMm, so.InnerRadiusMm) && sn.Refused == so.Refused);
                        }
                        n++;
                    }
            // 生产与旧规则在现役设计上不同（开关是活的）；现役旧值 = 门 f 记录的 59.0
            double rNew = NewInitial(d0), rOld = OldRadius(d0);
            Assert.True(Bits(rOld, 59.0), $"{which} 现役旧半径应为 59.0（门 f 记录），实际 {rOld:R}");
            Assert.False(Bits(rNew, rOld), $"{which} 新旧规则在现役设计上竟然相同 —— 开关没接上");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone(), P0(), tabLengthFactor: -0.35));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone(), P0(), tabLengthFactor: double.NaN));

        // 热点句：抽出的那一份与改前三行原文逐字同句（含 NaN 片、空片、远近）
        FlangeOut F(string name, double a, double b, double c) => new() { Name = name, DiscMaxRMm = a, TabMaxRMm = b, LocalStabRMm = c };
        var cases = new[]
        {
            new[] { F("片0", 29.5, 32.0, 20.0), F("片1", 28.0, double.NaN, 45.0) },
            new[] { F("片0", 29.5, double.NaN, double.NaN) },
            new[] { F("片0", double.NaN, double.NaN, double.NaN), F("片1", 29.0, 31.0, 30.0) },
            new[] { F("片0", 30.0, 30.0, 30.0) },
            new[] { F("片0", 49.0, 10.0, 10.0) },
            Array.Empty<FlangeOut>(),
            new[] { F("片0", double.NaN, 33.0, double.NaN) },
        };
        foreach (var fl in cases)
            foreach (double radius in new[] { 40.0, 59.0 })
            {
                var r = new LineResult { Flanges = fl };
                Assert.Equal(HotspotVerdictPreF7(r, 31.0, radius), MeshVerify.HotspotVerdict(r, 31.0, radius));
            }
        _o.WriteLine($"改回逐位：{n} 个（盘半径 × 舌长 × 孔半径 × 设计）组合，两个入口都逐位、计划不放大；热点句 {cases.Length * 2} 例逐字同句。");
    }

    /// <summary>改前 MeshVerify.Run 里那三行的原文（只给「抽出逐字同式」那道门对照）。</summary>
    internal static string? HotspotVerdictPreF7(LineResult r, double innerR, double radius)
    {
        var blindPeak = r.Flanges.Where(f => double.IsNaN(f.DiscMaxRMm) && double.IsNaN(f.TabMaxRMm)
                                          && double.IsNaN(f.LocalStabRMm)).Select(f => f.Name).ToArray();
        double peakR = r.Flanges.Length == 0 || blindPeak.Length > 0 ? double.NaN
                     : r.Flanges.SelectMany(f => new[] { f.DiscMaxRMm, f.TabMaxRMm, f.LocalStabRMm })
                        .Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
        string? peakBad = MeshAdapt.PeakVerdict(peakR, innerR, radius);
        if (peakBad is not null && blindPeak.Length > 0) peakBad += $"（算不出热点位置的片：{string.Join("、", blindPeak)}）";
        return peakBad;
    }
}
