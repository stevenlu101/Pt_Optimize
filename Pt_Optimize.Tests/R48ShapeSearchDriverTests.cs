using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 搜形状 Core 驱动（<see cref="ShapeSearchDriver"/>）的门：(b)(c) 是快门；(a) 冒烟用真求解器，一轮也要分钟级以上，标慢门、只点名跑。
///
/// 覆盖：
///   (a) 冒烟：真求解器、1 轮粗筛、1 轮精算、上端 = 起点表第一点 + 5（表 = 两点）、粗筛平坦区网格放粗，端到端跑一遍并印 rows。
///       只验接线（能跑完、行都有、末尾有卡或报告），不验答案；1 轮的解一律不当可行或不可行的证据。
///   (b) 源码门：驱动里没有写死的盘径表，上端来自调用方（NaN ⇒ 抛）。
///   (c) 外推与无解报告：注入假的单点求解（<see cref="ShapeSearchOptions.SolveOverride"/>，生产不传），
///       表最大值不可行、外推两步后可行 ⇒ 二分与爬山从外推到的可行点开始；全不可行 ⇒ 不二分、不爬山、不精算，报告含每形状停因。
/// 不覆盖：真实形状上的答案好坏（那是整夜实跑 R48ShapeSearchRunTests 的事，只印不判）；界面改调驱动（Windows 待办）。
/// </summary>
public class R48ShapeSearchDriverTests
{
    private readonly ITestOutputHelper _o;
    public R48ShapeSearchDriverTests(ITestOutputHelper o) => _o = o;

    private sealed class ListProgress : IProgress<string>
    {
        public readonly List<string> Lines = new();
        public void Report(string value) { lock (Lines) Lines.Add(value); }
    }

    // ───────────────────────────── (b) 源码门
    [Fact]
    public void b_驱动里没有写死的盘径表_上端来自调用方()
    {
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "ShapeSearchDriver.cs"));
        // 去掉注释再查（注释里引了界面原来的 {25, 30, 35}，那是病历）
        string code = Regex.Replace(src, @"//.*?$", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotMatch(new Regex(@"\{\s*25(\.0+)?\s*,\s*30(\.0+)?\s*,\s*35"), code);
        // 任何「两个以上 ≥ 10 的数」组成的数组字面量都算写死的盘径表
        Assert.DoesNotMatch(new Regex(@"\{\s*\d{2,}(\.\d+)?\s*,\s*\d{2,}(\.\d+)?"), code);
        // 缺省上端是 NaN（调用方必须给），没给就抛
        Assert.True(double.IsNaN(new ShapeSearchOptions().MaxDiscMm));
        Assert.Throws<ArgumentException>(() =>
            ShapeSearchDriver.Run(R48NMeshGateTests.Design("W08"), new DesignInputs(), new ShapeSearchOptions(), null, CancellationToken.None));
        // 跑器从环境变量取上端，并把出处印进证据头
        string run = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests", "R48ShapeSearchRunTests.cs"));
        Assert.Contains("SHAPE_MAXDISC", run);
        Assert.Contains("探针给的表，无出处", run);
    }

    // ───────────────────────────── (c) 假求解：外推两步后可行
    private static LineResult FakeLine(double hot, double cold, double flux) => new LineResult
    {
        Checks = new[]
        {
            new ConstraintOut { Name = LineResult.Key.HotOverTc, Actual = hot, Limit = 5, LessIsBetter = true, Ok = hot <= 5, Kind = CheckKind.HardSafety },
            new ConstraintOut { Name = LineResult.Key.ColdUnderTc, Actual = cold, Limit = 5, LessIsBetter = true, Ok = cold <= 5, Kind = CheckKind.HardSafety },
            new ConstraintOut { Name = LineResult.Key.NetFlux, Actual = flux, Limit = 0, LessIsBetter = false, Ok = flux > 0, Kind = CheckKind.HardSafety },
        },
    };

    /// <summary>假的单点求解：R ≥ feasFrom 可行；质量随盘径单调增（最轻在可行边界）；板厚固定 → 闭式下界可预知。</summary>
    private static Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult> Fake(
        double feasFrom, List<(double R, double hw, bool taper, SolverOptions o)> calls)
        => (d, b, o, p, t) =>
        {
            lock (calls) calls.Add((d.DiscRadiusMm, d.TabHalfWidthMm, d.TabTaper, o));
            var dd = d.Clone();
            for (int j = 0; j < dd.TabThickMm.Length; j++) dd.TabThickMm[j] = 1.0;
            bool ok = d.DiscRadiusMm >= feasFrom - 1e-9;
            double mass = 1000 + 20 * d.DiscRadiusMm + 3 * (d.DiscRadiusMm - d.TabHalfWidthMm) + (d.TabTaper ? 7 : 0);
            p?.Report("第  1 轮　假");
            return new SolverResult
            {
                Design = dd, Feasible = ok, MassG = mass, Solves = 1,
                HitBound = !ok, StopWhy = ok ? "" : $"假停因 R={d.DiscRadiusMm:0.000}",
                Message = ok ? "假：全过" : $"假：不可行 R={d.DiscRadiusMm:0.000}",
                Best = ok ? FakeLine(1, 1, 1) : FakeLine(9, 1, -1),
                FineRefined = o.FineMm > 0, FineMmUsed = o.FineMm,
            };
        };

    [Fact]
    public void c_表最大值不可行_外推两步后可行_二分与爬山从那里开始()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 50, MaxDiscSource = "门用",
            DiscGridMm = new[] { 27.0, 32.0 },           // 表最大值 32
            EvalSeedFirst = false, Lanes = 1,
            SolveOverride = Fake(40.0, calls),            // 32 与 37 不可行，42 可行
        };
        var lp = new ListProgress();
        var res = ShapeSearchDriver.Run(seed, new DesignInputs(), opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);

        Assert.Equal(new[] { 37.0, 42.0 }, res.ExtrapolatedMm.ToArray());
        Assert.Equal(42.0, res.StartDiscMm, 9);
        var phases = res.Rows.Select(r => r.Phase).ToList();
        int iExt = phases.LastIndexOf("①外推");
        int iBis = phases.IndexOf("③");
        Assert.True(iExt >= 0 && iBis > iExt, "二分必须在外推找到可行点之后");
        // ② 在闭式下界（板厚 1.0 ⇒ 孔 + max(1.0, 盘板厚下界, 壁)）上解，它 < 40 ⇒ 不可行 ⇒ 二分区间 [②, 42]
        var r2 = res.Rows.First(r => r.Phase == "②");
        Assert.False(r2.Ok);
        var r3 = res.Rows.First(r => r.Phase == "③");
        Assert.Equal(0.5 * (r2.R + 42.0), r3.R, 9);
        // 爬山从已算过的最轻可行点出发（假质量随盘径单调增 ⇒ 最轻可行点在 [40, 42]）
        var firstClimb = lp.Lines.First(l => l.StartsWith("⑥ 第 2 轮：从", StringComparison.Ordinal));
        Assert.Contains("出发", firstClimb);
        Assert.Contains(res.Rows, r => r.Phase.StartsWith("⑥邻域", StringComparison.Ordinal));
        Assert.NotNull(res.Winner);
        Assert.True(res.Winner!.R >= 40.0 - 1e-9);
        Assert.True(res.Winner.R <= 42.0 + 1e-9);
        // 精算：细网格 > 0，轮数 = FinalRounds
        var last = calls[^1];
        Assert.True(last.o.FineMm > 0);
        Assert.Equal(opt.FinalRounds, last.o.MaxRounds);
        Assert.Contains("边界保温条件", res.SchemeCard);
        Assert.Contains("管保温", res.SchemeCard);
        Assert.Contains("舌端铜排边界：定温", res.SchemeCard);   // new DesignInputs()：热导 −1、夹持温度取 DesignSpec.ClampTempC ⇒ 定温
        Assert.Contains("中层", res.SchemeCard);
        Assert.Contains("状态：判决网格精算全过", res.SchemeCard);   // 假求解：精算传了细网格 ⇒ FineRefined
        Assert.Contains("邻域停因", res.SchemeCard);
        Assert.Contains("环境温度", res.SchemeCard);
        Assert.False(res.NoFeasible);
    }

    [Fact]
    public void c_全不可行_不二分不爬山不精算_报告含每形状停因()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 45, MaxDiscSource = "门用上端出处",
            DiscGridMm = new[] { 27.0, 32.0 },
            EvalSeedFirst = true, Lanes = 1,
            SolveOverride = Fake(1e9, calls),
        };
        var lp = new ListProgress();
        var res = ShapeSearchDriver.Run(seed, new DesignInputs(), opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);

        Assert.True(res.NoFeasible);
        Assert.Null(res.Final);
        Assert.Equal(new[] { 37.0, 42.0 }, res.ExtrapolatedMm.ToArray());   // 42 + 5 > 45 ⇒ 停
        Assert.DoesNotContain(res.Rows, r => r.Phase is "③" or "④" || r.Phase.StartsWith("⑥", StringComparison.Ordinal));
        // ⑤ 照界面无条件试其余舌宽比例（界面不看 Rbest 可不可行）；Rbest = 起点表最大值 32，舌宽比 0.75
        var r5 = res.Rows.Where(r => r.Phase == "⑤舌宽").ToList();
        Assert.Single(r5);
        Assert.Equal(32.0, r5[0].R, 9);
        Assert.Equal(0.75 * 32.0, r5[0].HalfW, 9);
        Assert.Contains("没有出发点", res.ClimbStop);
        Assert.All(calls, c => Assert.Equal(opt.ScreenRounds, c.o.MaxRounds));   // 没有精算调用
        foreach (var r in res.Rows.Where(r => !r.Skipped))
            Assert.Contains($"假停因 R={r.R:0.000}", res.NoFeasibleReport);
        Assert.Contains("门用上端出处", res.NoFeasibleReport);
        Assert.Contains("最热铂高出热偶读数", res.NoFeasibleReport);
        Assert.Contains("管根低于热偶读数", res.NoFeasibleReport);
        Assert.Contains("管孔净流入", res.NoFeasibleReport);
        Assert.Contains("卡住", res.NoFeasibleReport);
        Assert.Contains(res.Rows, r => r.Phase == "基准");
    }

    [Fact]
    public void c_缺省起点表从闭式下界起每步一点到上端()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var baseIn = new DesignInputs();
        double lb = GeometryScreen.MinDiscRadiusMm(seed.HoleRadiusMm, baseIn.WeldMinThicknessMm, seed.WallMm);
        var opt = new ShapeSearchOptions { MaxDiscMm = lb + 12, EvalSeedFirst = false, Lanes = 1, SolveOverride = Fake(0, calls), MaxExtend = 1 };
        var res = ShapeSearchDriver.Run(seed, baseIn, opt, null, CancellationToken.None);
        double lo = Math.Ceiling(lb * 2 - 1e-9) / 2.0;
        Assert.Equal(lo, res.DiscGrid[0], 9);
        Assert.All(res.DiscGrid.Zip(res.DiscGrid.Skip(1)), p => Assert.Equal(ShapeSearchPlan.DiscStepMm, p.Second - p.First, 9));
        Assert.True(res.DiscGrid[^1] <= opt.MaxDiscMm + 1e-9);
        Assert.True(res.DiscGrid[^1] + ShapeSearchPlan.DiscStepMm > opt.MaxDiscMm);
        // ① 在表最大值上、舌宽取最宽（切线族 hw = R）
        var first = res.Rows.First(r => r.Phase == "①");
        Assert.Equal(res.DiscGrid[^1], first.R, 9);
        Assert.Equal(first.R, first.HalfW, 9);
    }

    // ───────────────────────────── (c) 精算没做第二遍：方案卡不许写成判决网格上全过（2026-09-23 对照核实补）
    [Fact]
    public void c_精算没做第二遍_方案卡写不可交付_不写判决网格全过()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var inner = Fake(0, calls);
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 40, MaxDiscSource = "门用", DiscGridMm = new[] { 32.0 },
            EvalSeedFirst = false, Lanes = 1, MaxExtend = 1,
            SolveOverride = (d, b, o, p, t) => { var r = inner(d, b, o, p, t); r.FineRefined = false; r.FineMmUsed = 0; return r; },
        };
        var res = ShapeSearchDriver.Run(seed, new DesignInputs(), opt, null, CancellationToken.None);
        Assert.NotNull(res.Final);
        Assert.True(res.Final!.Feasible);
        Assert.DoesNotContain("判决网格精算全过", res.SchemeCard);
        Assert.Contains("只在导航网格上", res.SchemeCard);
        Assert.Contains("★ 不可交付", res.SchemeCard);
        // MaxExtend = 1 且步长没收到下界 ⇒ 不许写局部最优
        Assert.False(res.ClimbConverged);
        Assert.Contains("不是局部最优", res.SchemeCard);
    }

    // ───────────────────────────── (a) 冒烟：真求解器
    // 慢门：真求解器一轮也是分钟级到几十分钟（实测见 deliverable/R48_搜形状_Core驱动_实施记录_2026-09-23.md 门表），进不了快套件；只点名跑。
    [Fact]
    [Trait("速度", "慢")]
    public void a_冒烟_真求解器_一轮_只验接线()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var baseIn = new DesignInputs();
        double lb = R48ShapeSearchRunTests.GridStart(seed, baseIn);   // 起点表第一点（闭式下界按 0.5 mm 向上取整）
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = lb + 5, MaxDiscSource = "冒烟：起点表第一点 + 5",
            ScreenRounds = 1, FinalRounds = 1, ScreenCoarseMm = SmokeCoarseMm,
            EvalSeedFirst = false, Lanes = 2, MaxExtend = 1,
        };
        var lp = new ListProgress();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = ShapeSearchDriver.Run(seed, baseIn, opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);
        _o.WriteLine($"冒烟总墙钟 {sw.Elapsed.TotalSeconds:0} s");
        Assert.True(res.Rows.Count > 0);
        Assert.Contains(res.Rows, r => r.Phase == "①");
        Assert.All(res.Rows.Where(r => !r.Skipped), r => Assert.NotNull(r.Result));
        Assert.True(res.NoFeasible ? res.NoFeasibleReport.Length > 0 : res.SchemeCard.Length > 0);
    }

    /// <summary>冒烟用的粗筛平坦区网格 mm：只为把一次解压到几十秒量级（选定，不交付；生产缺省 0 = 关）。</summary>
    internal const double SmokeCoarseMm = 20.0;
}
