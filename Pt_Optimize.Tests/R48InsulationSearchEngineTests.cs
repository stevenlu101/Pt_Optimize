using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using static PtOptimize.Core.InsulationSearch;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 E（2026-09-15 Opus 5）：保温搜索求解器骨架的**快门** —— 在手造的合成评估函数上验，不跑任何场解。
///   · 枚举结果与枚举次序无关：单调、非单调两种合成判据，打乱格点次序（多个种子）× 并发度 1／8，选中格点、可行集大小逐位相同；
///   · 并列规则确定：裕度量化后并列 ⇒ 总层数少者 ⇒ 圆盘层小者 ⇒ 舌层小者；判不了（NaN）排最后且不进可行集；
///   · 两端起点同解：耦合合成模型（工作点随别片的选择变，收缩映射）从全 0 层与全上界出发收敛到同一选择，且终点确实是不动点；
///   · 起点依赖要报出来：双稳合成模型两端各收敛到不同终点 ⇒ 不许写「与起点无关」、不许宣称可行；
///   · 周期检测：二周期、三周期合成模型报「判不了（周期）」，周期里的选择全部列出；整线失败报判不了；轮数用完报没收敛；
///   · 默认评估函数按定义算：热侧取盘峰、舌区峰、管根三者最大，冷侧取较冷端，净流入按 γ 折成开尔文、严格大于 0。
/// </summary>
public class R48InsulationSearchEngineTests
{
    /// <summary>合成格点：三条判据的归一裕度。第二、三条缺省给大值，免得它们当最小裕度把第一条的信息盖掉（最小裕度取三者最小）。</summary>
    private static PointOutcome Out(double m1, double m2 = 9.0, double m3 = 9.0) => new()
    {
        Terms =
        {
            new Criterion { Name = "甲", State = "态一", NormMargin = m1 },
            new Criterion { Name = "乙", State = "态二", NormMargin = m2 },
            new Criterion { Name = "丙", State = "态二", NormMargin = m3, Strict = true },
        },
    };

    private static List<(int Disc, int Tab)> Shuffled(List<(int Disc, int Tab)> pts, int seed)
    {
        var rng = new Random(seed);
        return pts.OrderBy(_ => rng.Next()).ToList();
    }

    private static void AssertSameChoice(PlateChoice a, PlateChoice b, string what)
    {
        Assert.True(a.Chosen.Disc == b.Chosen.Disc && a.Chosen.Tab == b.Chosen.Tab,
            $"{what}：选中 ({a.Chosen.Disc},{a.Chosen.Tab}) ≠ ({b.Chosen.Disc},{b.Chosen.Tab})");
        Assert.Equal(a.FeasibleCount, b.FeasibleCount);
        Assert.Equal(a.UndeterminedCount, b.UndeterminedCount);
        Assert.Equal(a.Chosen.MinNormMargin, b.Chosen.MinNormMargin);
        Assert.Equal(a.Chosen.Feasible, b.Chosen.Feasible);
    }

    [Fact]
    public void 枚举次序无关_单调与非单调合成判据_打乱次序与并发度逐位相同()
    {
        var pts = GridPoints(24, 24);
        var models = new (string Name, Func<int, int, PointOutcome> F)[]
        {
            ("单调", (dd, ww) => Out(1.2 - 0.08 * dd - 0.05 * ww, -0.9 + 0.06 * dd + 0.04 * ww)),
            ("非单调", (dd, ww) => Out(Math.Sin(0.7 * dd) * Math.Cos(0.4 * ww) + 0.2, Math.Cos(0.3 * dd + 0.5 * ww) + 0.1, 0.3 - 0.01 * Math.Abs(dd - 11))),
            ("全不可行", (dd, ww) => Out(-1.0 - 0.01 * Math.Abs(dd - 7) - 0.02 * Math.Abs(ww - 3), 0.5)),
            ("带判不了", (dd, ww) => (dd + ww) % 5 == 0 ? new PointOutcome { Determined = false, Why = "合成：场没收敛" }
                                                     : Out(0.4 - 0.03 * Math.Abs(dd - 9), 0.6 - 0.02 * ww)),
        };
        foreach (var (name, f) in models)
        {
            var reference = ChoosePlate(0, pts, f, 1, 0.01);
            Assert.Equal(pts.Count, reference.Evaluated);
            foreach (int seed in new[] { 1, 7, 42, 2026 })
                foreach (int dop in new[] { 1, 8 })
                    AssertSameChoice(reference, ChoosePlate(0, Shuffled(pts, seed), f, dop, 0.01), $"{name} 种子 {seed} 并发 {dop}");
            // 逆序也一样
            var rev = Enumerable.Reverse(pts).ToList();
            AssertSameChoice(reference, ChoosePlate(0, rev, f, 4, 0.01), $"{name} 逆序");
            // 选中者确实是全序第一：任何别的格点都不排在它前面
            foreach (var o in reference.All)
                if (!(o.Disc == reference.Chosen.Disc && o.Tab == reference.Chosen.Tab))
                    Assert.True(Compare(reference.Chosen, o, 0.01) < 0, $"{name}：({o.Disc},{o.Tab}) 排在选中者前面或并列");
        }
        // 单态可行集：「单调」模型里态一只有甲、态二有乙与丙（丙恒 9 > 0）⇒ 计数按定义
        var mono = ChoosePlate(0, pts, models[0].F, 3, 0.01);
        Assert.Equal(pts.Count(t => 1.2 - 0.08 * t.Disc - 0.05 * t.Tab >= 0), mono.FeasibleIn("态一"));
        Assert.Equal(pts.Count(t => -0.9 + 0.06 * t.Disc + 0.04 * t.Tab >= 0), mono.FeasibleIn("态二"));
        Assert.Equal(pts.Count(t => 1.2 - 0.08 * t.Disc - 0.05 * t.Tab >= 0 && -0.9 + 0.06 * t.Disc + 0.04 * t.Tab >= 0), mono.FeasibleCount);
        Assert.Equal(0, mono.FeasibleIn("没有这个态"));
        // 「全不可行」报最近格点，不宣称可行
        var none = ChoosePlate(0, pts, models[2].F, 2, 0.01);
        Assert.Equal(0, none.FeasibleCount);
        Assert.False(none.IsFeasible);
        Assert.Equal((7, 3), (none.Chosen.Disc, none.Chosen.Tab));
        // 「带判不了」：判不了的格点不会被选中，计数对
        var und = ChoosePlate(0, pts, models[3].F, 2, 0.01);
        Assert.True(und.Chosen.Determined);
        Assert.Equal(pts.Count(t => (t.Disc + t.Tab) % 5 == 0), und.UndeterminedCount);
    }

    [Fact]
    public void 并列规则确定_量化并列后总层数少者_再圆盘层小者_再舌层小者()
    {
        var pts = GridPoints(6, 6);
        // 全部同裕度 ⇒ (0,0)
        var all = ChoosePlate(1, Shuffled(pts, 3), (_, _) => Out(0.5, 0.5), 4, 0.01);
        Assert.Equal((0, 0), (all.Chosen.Disc, all.Chosen.Tab));
        // (2,3)、(3,2)、(1,4)、(5,0) 同裕度最高、其余低 ⇒ 总层数都是 5 ⇒ 圆盘层最小的 (1,4)
        var top = new HashSet<(int, int)> { (2, 3), (3, 2), (1, 4), (5, 0) };
        foreach (int seed in new[] { 5, 6, 7 })
        {
            var ch = ChoosePlate(1, Shuffled(pts, seed), (dd, ww) => top.Contains((dd, ww)) ? Out(0.8, 0.9) : Out(0.2, 0.9), 3, 0.01);
            Assert.Equal((1, 4), (ch.Chosen.Disc, ch.Chosen.Tab));
        }
        // 量化：0.5001 与 0.5049 同在 [0.50, 0.51) ⇒ 并列 ⇒ 层数少的 (1,1) 赢过 (4,4)；0.5101 进下一格 ⇒ 裕度大的赢
        PointOutcome Q(int dd, int ww, double m) => dd == 1 && ww == 1 ? Out(0.5001, 1) : dd == 4 && ww == 4 ? Out(m, 1) : Out(0.1, 1);
        Assert.Equal((1, 1), (ChoosePlate(0, pts, (a, b) => Q(a, b, 0.5049), 1, 0.01).Chosen.Disc, 1));
        var win = ChoosePlate(0, pts, (a, b) => Q(a, b, 0.5101), 1, 0.01);
        Assert.Equal((4, 4), (win.Chosen.Disc, win.Chosen.Tab));
        // 严格判据：归一裕度恰为 0 不算过；非严格判据恰为 0 算过
        Assert.False(Out(0.3, 0.3, 0.0).Feasible);
        Assert.True(Out(0.0, 0.3, 0.1).Feasible);
        // NaN 裕度 ⇒ 判不了、排最后
        var nan = ChoosePlate(0, pts, (dd, ww) => dd == 0 && ww == 0 ? Out(double.NaN, 5) : Out(-3, -3), 1, 0.01);
        Assert.False(nan.Chosen.Disc == 0 && nan.Chosen.Tab == 0);
        Assert.Equal(1, nan.UndeterminedCount);
        // 格点重复 ⇒ 拒绝
        Assert.Throws<ArgumentException>(() => ChoosePlate(0, new List<(int, int)> { (0, 0), (0, 0) }, (_, _) => Out(1, 1), 1, 0.01));
    }

    /// <summary>合成链：给一个「由上一轮选择定工作点、再逐片选」的确定映射，走真骨架（ChoosePlate + FixedPoint + RunStarts）。</summary>
    private static Func<int, Selection, RoundRecord> SynthRound(int plates, int dMax, int wMax, Func<Selection, int, int, int, PointOutcome> evalAt,
                                                                List<string>? calls = null)
    {
        var pts = GridPoints(dMax, wMax);
        return (r, sel) =>
        {
            lock (pts) calls?.Add(sel.Key);
            var ch = Enumerable.Range(0, plates).Select(j => ChoosePlate(j, pts, (dd, ww) => evalAt(sel, j, dd, ww), 2, 0.01)).ToArray();
            return new RoundRecord { Choices = ch, Next = new Selection(ch.Select(c => c.Chosen.Disc).ToArray(), ch.Select(c => c.Chosen.Tab).ToArray()) };
        };
    }

    [Fact]
    public void 两端起点同解_耦合收缩模型_终点是不动点()
    {
        const int n = 4, dMax = 12, wMax = 12;
        // 第 j 片的最优圆盘层随「别片上一轮圆盘层之和」漂（系数 0.1，三片 ⇒ 收缩 0.3），最优舌层随本片上一轮圆盘层漂（系数 0.2）。
        // 手算（跑前）：全 0 层起 {2,3,4,5} → {3,4,5,6} → {4,5,5,6} → {4,5,6,7} → 不动；全上界起 {6,7,8,9} → {5,6,6,7} → {4,5,6,7} → 不动 ⇒ 同解 {4,5,6,7}。
        double[] c = { 2.2, 3.3, 4.1, 5.2 };
        PointOutcome Eval(Selection sel, int j, int dd, int ww)
        {
            double others = Enumerable.Range(0, n).Where(k => k != j).Sum(k => sel.Disc[k]);
            double tD = c[j] + 0.1 * others, tW = 2 + 0.2 * sel.Disc[j];
            return Out(1 - 0.1 * Math.Abs(dd - tD) - 0.1 * Math.Abs(ww - tW));
        }
        var round = SynthRound(n, dMax, wMax, Eval);
        var low = Selection.Uniform(n, 0, 0); var high = Selection.Uniform(n, dMax, wMax);
        foreach (bool par in new[] { false, true })
        {
            var so = RunStarts(low, high, round, 20, par);
            Assert.Equal(ChainStatus.Converged, so.Low.Status);
            Assert.Equal(ChainStatus.Converged, so.High.Status);
            Assert.True(so.StartIndependent, $"两端终点不同：{so.Low.Final!.Describe()} ／ {so.High.Final!.Describe()}");
            Assert.Contains("与起点无关", so.Verdict());
            Assert.Equal(new[] { 4, 5, 6, 7 }, so.Final!.Disc);
            // 终点是不动点：再走一轮不变
            var again = round(99, so.Final!);
            Assert.True(again.Next!.Equals(so.Final), "终点再走一轮变了 ⇒ 不是不动点");
            // 收敛判的是「第 r ≥ 2 轮与上一轮相同」
            Assert.True(so.Low.Rounds.Count >= 2);
            Assert.True(so.Low.Rounds[^1].Next!.Equals(so.Low.Rounds[^1].From));
            Assert.True(so.Feasible);
        }
    }

    [Fact]
    public void 起点就是不动点时_仍走满两轮才判收敛()
    {
        var calls = new List<string>();
        var round = SynthRound(2, 3, 3, (sel, j, dd, ww) => Out(0.5 - 0.1 * (dd + ww), 0.5), calls);   // 恒选 (0,0)
        var ch = FixedPoint("起点全 0 层", Selection.Uniform(2, 0, 0), round, 5);
        Assert.Equal(ChainStatus.Converged, ch.Status);
        Assert.Equal(2, ch.Rounds.Count);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void 起点依赖_双稳模型两端终点不同_不许宣称与起点无关()
    {
        // 片0：上一轮圆盘层 ≥ 5 ⇒ 最优在 10，否则在 0（双稳）；片1 恒选 (2,2)
        PointOutcome Eval(Selection sel, int j, int dd, int ww)
        {
            if (j == 1) return Out(0.8 - 0.1 * Math.Abs(dd - 2) - 0.1 * Math.Abs(ww - 2));
            double t = sel.Disc[0] >= 5 ? 10 : 0;
            return Out(0.8 - 0.05 * Math.Abs(dd - t) - 0.05 * ww);
        }
        var round = SynthRound(2, 10, 4, Eval);
        var so = RunStarts(Selection.Uniform(2, 0, 0), Selection.Uniform(2, 10, 4), round, 10, true);
        Assert.Equal(ChainStatus.Converged, so.Low.Status);
        Assert.Equal(ChainStatus.Converged, so.High.Status);
        Assert.False(so.StartIndependent);
        Assert.False(so.Feasible);
        Assert.Null(so.Final);
        Assert.Contains("结果依赖起点", so.Verdict());
        Assert.Equal(0, so.Low.Final!.Disc[0]);
        Assert.Equal(10, so.High.Final!.Disc[0]);
    }

    [Fact]
    public void 周期检测_二周期与三周期报判不了_周期成员全部列出()
    {
        // 二周期：片0 上一轮圆盘层为 0 ⇒ 选 8，否则选 0
        PointOutcome Two(Selection sel, int j, int dd, int ww)
        {
            double t = sel.Disc[0] == 0 ? 8 : 0;
            return Out(0.9 - 0.05 * Math.Abs(dd - t) - 0.05 * ww);
        }
        var r2 = SynthRound(1, 10, 2, Two);
        var lo = FixedPoint("起点全 0 层", Selection.Uniform(1, 0, 0), r2, 10);
        Assert.Equal(ChainStatus.Cycle, lo.Status);
        Assert.Equal(new[] { 0, 8 }, lo.CycleMembers.Select(s => s.Disc[0]).OrderBy(v => v).ToArray());
        var hi = FixedPoint("起点全上界", Selection.Uniform(1, 10, 2), r2, 10);
        Assert.Equal(ChainStatus.Cycle, hi.Status);
        Assert.Equal(new[] { 0, 8 }, hi.CycleMembers.Select(s => s.Disc[0]).OrderBy(v => v).ToArray());
        var so = new StartsOutcome { Low = lo, High = hi };
        Assert.False(so.StartIndependent);
        Assert.Contains("判不了", so.Verdict());

        // 三周期：0 → 3 → 6 → 0
        PointOutcome Three(Selection sel, int j, int dd, int ww)
        {
            int t = sel.Disc[0] switch { 0 => 3, 3 => 6, 6 => 0, _ => 0 };
            return Out(0.9 - 0.05 * Math.Abs(dd - t) - 0.05 * ww);
        }
        var c3 = FixedPoint("起点全 0 层", Selection.Uniform(1, 0, 0), SynthRound(1, 8, 1, Three), 10);
        Assert.Equal(ChainStatus.Cycle, c3.Status);
        Assert.Equal(new[] { 0, 3, 6 }, c3.CycleMembers.Select(s => s.Disc[0]).OrderBy(v => v).ToArray());
    }

    [Fact]
    public void 整线失败报判不了_轮数用完报没收敛()
    {
        var fail = FixedPoint("起点全 0 层", Selection.Uniform(2, 0, 0),
                              (r, sel) => new RoundRecord { LineOk = false, Why = "合成：外层耦合没收敛" }, 5);
        Assert.Equal(ChainStatus.Undetermined, fail.Status);
        Assert.Contains("外层耦合没收敛", fail.Why);
        // 每轮圆盘层 +1（上界 50）：轮数 4 用完仍在变
        PointOutcome Walk(Selection sel, int j, int dd, int ww) => Out(0.9 - 0.02 * Math.Abs(dd - (sel.Disc[0] + 1)) - 0.02 * ww);
        var walk = FixedPoint("起点全 0 层", Selection.Uniform(1, 0, 0), SynthRound(1, 50, 1, Walk), 4);
        Assert.Equal(ChainStatus.NotConverged, walk.Status);
        Assert.Contains("判不了", StatusText(walk.Status));
    }

    [Fact]
    public void 默认评估函数_按定义算()
    {
        var o = new Options();
        var v = new PlateStateView
        {
            Plate = 1, Shared = true, State = 0, StateName = StateGlass,
            ReferenceC = ThermocoupleBasis.LogMeanC(1150, 1080),
            DiscPeakC = 1110, TabPeakC = 1118, RootHotC = 1121, RootColdC = 1112,
            NetInflowW = 1.5, GammaKPerW = 2.0,
        };
        var t = DefaultCriteria(v, o);
        Assert.Equal(3, t.Count);
        var hot = t.Single(c => c.Name == HotName);
        Assert.Equal(1121 - v.ReferenceC, hot.Value, 12);          // 管根比两个峰都热 ⇒ 热侧取管根
        Assert.Equal((5 - hot.Value) / 5, hot.NormMargin, 12);
        var cold = t.Single(c => c.Name == ColdName);
        Assert.Equal(v.ReferenceC - 1112, cold.Value, 12);         // 冷侧取较冷端
        var inflow = t.Single(c => c.Name == InflowName);
        Assert.True(inflow.Strict);
        Assert.Equal(2.0 * 1.5 / 5, inflow.NormMargin, 12);
        // 峰比管根热 ⇒ 热侧取峰
        v.TabPeakC = 1130;
        Assert.Equal(1130 - v.ReferenceC, DefaultCriteria(v, o).Single(c => c.Name == HotName).Value, 12);
        // γ 不为正 ⇒ 净流入裕度 NaN（骨架会判成判不了）
        v.GammaKPerW = 0;
        Assert.True(double.IsNaN(DefaultCriteria(v, o).Single(c => c.Name == InflowName).NormMargin));
        // 基准：端片取本段设定，共用片取对数平均（调生产函数，不在门里手抄）
        Assert.Equal(1150, ThermocoupleBasis.ReferenceC(new[] { 1150.0, 1080, 1050 }, 0));
        Assert.Equal(ThermocoupleBasis.LogMeanC(1080, 1050), ThermocoupleBasis.ReferenceC(new[] { 1150.0, 1080, 1050 }, 2), 12);
    }

    /// <summary>源码门：搜索不许带热启动进整线（每轮冷启动），整线算例由 BuildCase 新造；判据名不带代号；三个上界的注释写明无出处。</summary>
    [Fact]
    public void 源码门_冷启动_无判据代号_上界写明无出处()
    {
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "InsulationSearch.cs"));
        var code = string.Join("\n", src.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotMatch(new Regex(@"WarmStart\s*=(?!=)"), code);
        Assert.DoesNotMatch(new Regex(@"BaselineRootC\s*=(?!=)"), code);
        Assert.Contains("BuildCase(", code);
        foreach (var name in new[] { HotName, ColdName, InflowName, StateGlass, StateEmpty })
            Assert.DoesNotMatch(new Regex("[②③④⑤⑥]|′|″"), name);
        Assert.DoesNotMatch(new Regex("[②③④⑤⑥]|′|″"), SelectionRuleText);
        foreach (var field in new[] { "TubeLayerMax", "DiscLayerMax", "TabLayerMax" })
        {
            var m = Regex.Match(src, @"///[^\n]*无出处[^\n]*\n\s*public int " + field);
            Assert.True(m.Success, $"{field} 的注释没写「无出处」");
        }
    }
}
