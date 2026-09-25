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
///       界面原算法（改回 ParallelFirstPass = false）：表最大值不可行、外推两步后可行 ⇒ 二分与爬山从外推到的可行点开始；
///       全不可行 ⇒ 不二分、不爬山、不精算，报告含每形状停因。
///   (d) 2026-09-24 并行首遍（D）：首遍对全部表点各调一次、舌宽取最宽；二分区间取自首遍（左邻不可行点、最小可行点）；
///       全不可行 ⇒ 并行一批外推到上端、② 取表最大值那一行的板厚；全可行 ⇒ 直接从下界做 ②、板厚取最小可行点那一行的。
///   (e) 2026-09-24 粗筛 8 轮与传给求解器的选项（C、B、A 的驱动侧接线）、舌宽比表与闭式肩部预筛（E）。
/// 不覆盖：真实形状上的答案好坏（那是整夜实跑 R48ShapeSearchRunTests 的事，只印不判）；界面改调驱动（Windows 待办）；
///   求解器侧的 B（不放大重做）与 A（同状态复用）本身由 R48SolverScreenCostTests 管。
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

    /// <summary>
    /// 假的单点求解：R ≥ feasFrom 可行；质量随盘径单调增（最轻在可行边界）；板厚缺省固定 1.0 → 闭式下界可预知。
    /// <paramref name="thickOf"/> 给了就按盘径给板厚（门 d 用它分辨「② 取的是哪一行的板厚」）；<paramref name="plateA"/> 给了就填设计电流（门 e 的肩部预筛用）。
    /// </summary>
    private static Func<DesignSpec, DesignInputs, SolverOptions, IProgress<string>?, CancellationToken, SolverResult> Fake(
        double feasFrom, List<(double R, double hw, bool taper, SolverOptions o)> calls,
        Func<double, double>? thickOf = null, double plateA = double.NaN)
        => (d, b, o, p, t) =>
        {
            lock (calls) calls.Add((d.DiscRadiusMm, d.TabHalfWidthMm, d.TabTaper, o));
            var dd = d.Clone();
            for (int j = 0; j < dd.TabThickMm.Length; j++) dd.TabThickMm[j] = thickOf?.Invoke(d.DiscRadiusMm) ?? 1.0;
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
                DesignCurrent = double.IsNaN(plateA) ? null
                              : new DesignCurrent.Result { PlateA = Enumerable.Repeat(plateA, dd.TabThickMm.Length).ToArray() },
            };
        };

    /// <summary>
    /// 界面原算法（改回 <see cref="ShapeSearchOptions.ParallelFirstPass"/> = false）：表最大值不可行、逐步外推两步后可行。
    /// 2026-09-24 变因：并行首遍成了缺省（D），本门改为显式走改回路径（原名「c_表最大值不可行_外推两步后可行_二分与爬山从那里开始」，断言一条没改）。
    /// </summary>
    [Fact]
    public void c_改回界面原算法_表最大值不可行_外推两步后可行_二分与爬山从那里开始()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 50, MaxDiscSource = "门用",
            DiscGridMm = new[] { 27.0, 32.0 },           // 表最大值 32
            EvalSeedFirst = false, Lanes = 1,
            ParallelFirstPass = false,                    // 改回：界面原算法
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
        // ⑤ 照界面无条件试其余舌宽比例（界面不看 Rbest 可不可行）；Rbest = 起点表最大值 32。
        // 2026-09-24 变因（E，舌宽比表 {0.75, 1.00} → {1.00, 0.875, 0.75}）：原钉「只有一行、比例 0.75」，现为两行、先 0.875 后 0.75。
        //   假求解不给设计电流 ⇒ 肩部预筛估不出 ⇒ 两行都照常求解。
        var r5 = res.Rows.Where(r => r.Phase == "⑤舌宽").ToList();
        Assert.Equal(2, r5.Count);
        Assert.All(r5, r => Assert.Equal(32.0, r.R, 9));
        Assert.Equal(0.875 * 32.0, r5[0].HalfW, 9);
        Assert.Equal(0.75 * 32.0, r5[1].HalfW, 9);
        Assert.All(r5, r => Assert.False(r.Skipped));
        // 并行首遍（缺省）：表 {27, 32} 全不可行 ⇒ 一批外推到上端 45（37、42）；② 取表最大值 32 那一行的板厚
        Assert.Contains("全不可行", res.FirstPassBranch);
        Assert.Contains("没有出发点", res.ClimbStop);
        Assert.All(calls, c => Assert.Equal(opt.ScreenRounds, c.o.MaxRounds));   // 没有精算调用
        foreach (var r in res.Rows.Where(r => !r.Skipped))
            Assert.Contains($"假停因 R={r.R:0.000}", res.NoFeasibleReport);
        Assert.Contains("门用上端出处", res.NoFeasibleReport);
        Assert.Contains("法兰最热处高出管接触处温度", res.NoFeasibleReport);   // 2026-09-25：三列换成决 103 硬判据
        Assert.Contains("管接触处流入法兰的净热流", res.NoFeasibleReport);
        Assert.Contains("局部热稳定", res.NoFeasibleReport);
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
        // ① 并行首遍（2026-09-24 缺省，D）：表里每一点都有一行，舌宽取最宽（切线族 hw = R）；按盘径从大到小提交，第一行是表最大值。
        //   变因：原钉「① 在表最大值上」一行（界面原算法只解表最大值）；现在表里每一点都解。
        var first = res.Rows.First(r => r.Phase == "①");
        Assert.Equal(res.DiscGrid[^1], first.R, 9);
        Assert.Equal(first.R, first.HalfW, 9);
        var p1 = res.Rows.Where(r => r.Phase == "①").Select(r => r.R).OrderBy(x => x).ToArray();
        Assert.Equal(res.DiscGrid, p1);
        Assert.All(res.Rows.Where(r => r.Phase == "①"), r => Assert.Equal(r.R, r.HalfW, 9));
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

    // ───────────────────────────── (d) 2026-09-24 并行首遍（D）
    /// <summary>闭式下界（与驱动 ② 同一段写法：设计的板厚 → DiscFloorMm → Plate → MinDiscRadiusMm，再与管孔＋焊脚下界取大）。</summary>
    private static double FixpointBound(DesignSpec d, DesignInputs baseIn, double minDiscAll)
    {
        double floorMm = d.DiscFloorMm(baseIn);
        var plates = new FlangePlate[d.TabThickMm.Length];
        for (int j = 0; j < plates.Length; j++) plates[j] = d.Plate(j, floorMm);
        return Math.Max(GeometryScreen.MinDiscRadiusMm(plates), minDiscAll);
    }

    [Fact]
    public void d_并行首遍_对全部表点各调一次_舌宽取最宽_二分区间取自首遍()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var baseIn = new DesignInputs();
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        double lb = GeometryScreen.MinDiscRadiusMm(seed.HoleRadiusMm, baseIn.WeldMinThicknessMm, seed.WallMm);
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = lb + 22, MaxDiscSource = "门用", EvalSeedFirst = false, Lanes = 4, MaxExtend = 1,
            SolveOverride = Fake(36.0, calls),          // 表 {27, 32, 37, 42, 47}：37 起可行
        };
        var lp = new ListProgress();
        var res = ShapeSearchDriver.Run(seed, baseIn, opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);

        Assert.Equal(new[] { 27.0, 32.0, 37.0, 42.0, 47.0 }, res.DiscGrid);
        // 首遍：前 5 次调用恰好是表里 5 点、各一次、舌宽 = 盘半径（切线族），粗筛选项
        var first5 = calls.Take(5).ToList();
        Assert.Equal(res.DiscGrid, first5.Select(c => c.R).OrderBy(x => x).ToArray());
        Assert.All(first5, c => Assert.Equal(c.R, c.hw, 9));
        Assert.All(first5, c => Assert.Equal(opt.ScreenRounds, c.o.MaxRounds));
        Assert.Equal(5, res.Rows.Count(r => r.Phase == "①"));
        foreach (double R in res.DiscGrid) Assert.Single(calls, c => Math.Abs(c.R - R) < 1e-9 && Math.Abs(c.hw - R) < 1e-9);
        // 二分区间 = [左邻不可行 32, 最小可行 37]；不做 ②；第一次二分点 = 34.5
        Assert.Contains("有可行点", res.FirstPassBranch);
        Assert.Equal(32.0, res.BisectLoMm, 9);
        Assert.Equal(37.0, res.BisectHiMm, 9);
        Assert.Equal(37.0, res.StartDiscMm, 9);
        Assert.DoesNotContain(res.Rows, r => r.Phase == "②");
        var r3 = res.Rows.First(r => r.Phase == "③");
        Assert.Equal(34.5, r3.R, 9);
        int iLast1 = res.Rows.FindLastIndex(r => r.Phase == "①"), iFirst3 = res.Rows.FindIndex(r => r.Phase == "③");
        Assert.True(iLast1 < iFirst3, "二分必须在首遍全部回来之后");
        // 最轻可行点落在 [36, 37]（假质量随盘径单调增）
        Assert.NotNull(res.Winner);
        Assert.InRange(res.Winner!.R, 36.0 - 1e-9, 37.0 + 1e-9);
    }

    [Fact]
    public void d_并行首遍_全不可行_并行一批外推到上端_二取表最大值那一行的板厚()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var baseIn = new DesignInputs();
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 50, MaxDiscSource = "门用", DiscGridMm = new[] { 27.0, 32.0 },
            EvalSeedFirst = false, Lanes = 3,
            SolveOverride = Fake(1e9, calls, thickOf: R => R / 16.0),   // 板厚随盘径变 ⇒ ② 的下界能分辨取的是哪一行
        };
        var lp = new ListProgress();
        var res = ShapeSearchDriver.Run(seed, baseIn, opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);

        Assert.True(res.NoFeasible);
        Assert.Equal(new[] { 37.0, 42.0, 47.0 }, res.ExtrapolatedMm.ToArray());      // 一批到上端，不在第一个点停
        Assert.Equal(3, res.Rows.Count(r => r.Phase == "①外推"));
        int iExt = res.Rows.FindIndex(r => r.Phase == "①外推"), iLast1 = res.Rows.FindLastIndex(r => r.Phase == "①");
        Assert.True(iLast1 < iExt, "外推必须在首遍全部回来之后");
        Assert.Contains("全不可行", res.FirstPassBranch);
        Assert.DoesNotContain(res.Rows, r => r.Phase is "③" or "④");
        // ② 的板厚取表最大值 32 那一行（不是外推到的 47）
        var top = res.Rows.First(r => r.Phase == "①" && Math.Abs(r.R - 32.0) < 1e-9);
        double want = FixpointBound(top.Design!, baseIn, res.MinDiscMm);
        var r2 = res.Rows.First(r => r.Phase == "②");
        Assert.Equal(want, r2.R, 9);
        var ext47 = res.Rows.First(r => r.Phase == "①外推" && Math.Abs(r.R - 47.0) < 1e-9);
        Assert.NotEqual(FixpointBound(ext47.Design!, baseIn, res.MinDiscMm), r2.R, 6);
        Assert.Null(res.Final);
    }

    [Fact]
    public void d_并行首遍_全可行_直接从下界做二_板厚取最小可行点()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var baseIn = new DesignInputs();
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        double lb = GeometryScreen.MinDiscRadiusMm(seed.HoleRadiusMm, baseIn.WeldMinThicknessMm, seed.WallMm);
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = lb + 12, MaxDiscSource = "门用", EvalSeedFirst = false, Lanes = 2, MaxExtend = 1,
            SolveOverride = Fake(0, calls, thickOf: R => 0.02 * R),     // 全可行；板厚随盘径变
        };
        var lp = new ListProgress();
        var res = ShapeSearchDriver.Run(seed, baseIn, opt, lp, CancellationToken.None);
        foreach (var l in lp.Lines) _o.WriteLine(l);

        Assert.Contains("全可行", res.FirstPassBranch);
        Assert.True(double.IsNaN(res.BisectLoMm));
        Assert.Equal(res.DiscGrid[0], res.StartDiscMm, 9);
        // ② 紧跟首遍，不经二分；下界取最小可行点（表首点）那一行的板厚
        int iLast1 = res.Rows.FindLastIndex(r => r.Phase == "①");
        Assert.Equal("②", res.Rows[iLast1 + 1].Phase);
        var minRow = res.Rows.First(r => r.Phase == "①" && Math.Abs(r.R - res.DiscGrid[0]) < 1e-9);
        Assert.Equal(FixpointBound(minRow.Design!, baseIn, res.MinDiscMm), res.Rows[iLast1 + 1].R, 9);
        Assert.DoesNotContain(res.Rows, r => r.Phase == "③");
    }

    // ───────────────────────────── (e) 2026-09-24 粗筛 8 轮、传给求解器的选项、舌宽比表、闭式肩部预筛
    [Fact]
    public void e_粗筛8轮_跳过放大与同状态复用只在粗筛_精算照旧()
    {
        var d = new ShapeSearchOptions();
        // 变因：ScreenRounds 16 → 8（选定，依据夜跑 001304／021617）；WFrac {0.75, 1.00} → {1.00, 0.875, 0.75}（选定，依据决 102 探针 203224 与 021617 ⑤）
        Assert.Equal(8, d.ScreenRounds);
        Assert.Equal(new[] { 1.00, 0.875, 0.75 }, d.WFrac);
        Assert.Equal(1.0 - ShapeSearchPlan.FracStep, d.WFrac[1], 12);
        Assert.True(d.ParallelFirstPass && d.ScreenSkipRadiusGrowth && d.ScreenReuseSameState && d.ShoulderJPrescreen);
        Assert.False(d.FinalReuseSameState);
        Assert.Equal(40, d.FinalRounds);
        // 求解器侧缺省全关（生产逐位不变）
        var so = new SolverOptions();
        Assert.False(so.SkipRadiusGrowthAfterFinalCheck);
        Assert.False(so.ReuseSameStateSolves);

        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 40, MaxDiscSource = "门用", DiscGridMm = new[] { 32.0 }, EvalSeedFirst = false, Lanes = 1, MaxExtend = 1,
            SolveOverride = Fake(0, calls),
        };
        var res = ShapeSearchDriver.Run(seed, new DesignInputs(), opt, null, CancellationToken.None);
        Assert.NotNull(res.Final);
        var screen = calls.Take(calls.Count - 1).ToList();
        var fin = calls[^1].o;
        Assert.All(screen, c => { Assert.Equal(8, c.o.MaxRounds); Assert.True(c.o.SkipRadiusGrowthAfterFinalCheck); Assert.True(c.o.ReuseSameStateSolves); Assert.Equal(0, c.o.FineMm); });
        Assert.Equal(40, fin.MaxRounds);
        Assert.False(fin.SkipRadiusGrowthAfterFinalCheck);
        Assert.False(fin.ReuseSameStateSolves);
        Assert.True(fin.FineMm > 0);
        Assert.Contains("粗筛设置：8 轮", res.SchemeCard);
        // 改回：三个开关关掉 ⇒ 粗筛选项回到改前
        var calls2 = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var opt2 = new ShapeSearchOptions
        {
            MaxDiscMm = 40, MaxDiscSource = "门用", DiscGridMm = new[] { 32.0 }, EvalSeedFirst = false, Lanes = 1, MaxExtend = 1,
            ScreenRounds = 16, ScreenSkipRadiusGrowth = false, ScreenReuseSameState = false, SolveOverride = Fake(0, calls2),
        };
        ShapeSearchDriver.Run(seed, new DesignInputs(), opt2, null, CancellationToken.None);
        Assert.All(calls2.Take(calls2.Count - 1), c => { Assert.Equal(16, c.o.MaxRounds); Assert.False(c.o.SkipRadiusGrowthAfterFinalCheck); Assert.False(c.o.ReuseSameStateSolves); });
    }

    [Fact]
    public void e_粗筛跳过放大_本该放大到的半径进逐形状行与方案卡()
    {
        var seed = R48NMeshGateTests.Design("W08");
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var inner = Fake(0, calls);
        var opt = new ShapeSearchOptions
        {
            MaxDiscMm = 40, MaxDiscSource = "门用", DiscGridMm = new[] { 32.0 }, EvalSeedFirst = false, Lanes = 1, MaxExtend = 1,
            SolveOverride = (d, b, o, p, t) =>
            {
                var r = inner(d, b, o, p, t);
                if (o.SkipRadiusGrowthAfterFinalCheck) { r.SkippedRadiusGrowthToMm = 71.25; r.SkippedRadiusGrowthWhy = "门用"; r.SameStateReuses = 3; }
                return r;
            },
        };
        var res = ShapeSearchDriver.Run(seed, new DesignInputs(), opt, null, CancellationToken.None);
        var row = res.Rows.First(r => r.Phase == "①");
        Assert.Equal(71.25, row.SkippedGrowthToMm, 12);
        Assert.Equal(3, row.Reuses);
        Assert.Contains("71.25（没放大、没重做", row.Line());
        Assert.Contains("粗筛跳过放大重做的形状", res.SchemeCard);
        Assert.Contains("本该放大到 71.25 mm", res.SchemeCard);
        Assert.EndsWith("同状态复用\t粗筛本该放大到 mm", ShapeRow.Header);
    }

    [Fact]
    public void e_肩部J估计_I除以t乘2w_逐片取最大_估不出给NaN()
    {
        var sr = new SolverResult { DesignCurrent = new DesignCurrent.Result { PlateA = new[] { 600.0, 1000.0 } } };
        var d = R48NMeshGateTests.Design("W08").Clone();
        d.TabThickMm = new[] { 1.0, 1.25 };
        var (j, why) = ShapeSearchDriver.ShoulderJEstimate(24.0, sr, d);
        Assert.Equal(1000.0 / (1.25 * 48.0), j, 12);
        Assert.Contains("片1", why);
        Assert.True(double.IsNaN(ShapeSearchDriver.ShoulderJEstimate(24.0, new SolverResult(), d).J));
        Assert.True(double.IsNaN(ShapeSearchDriver.ShoulderJEstimate(24.0, sr, null).J));
    }

    [Fact]
    public void e_肩部预筛_w小于R且估计J超计算极限的不进场解_改回则照常解()
    {
        var seed = R48NMeshGateTests.Design("W08");
        Assert.Equal(11.0, seed.JCheckAPerMm2, 12);          // 计算极限 = 设定 J 10 + 1（SectionSizing.JCheckOf），不是本门挑的
        // I = 600 A、t = 1.0 mm、R = 32：w = 0.875R ⇒ 600/(1×56) = 10.71 ≤ 11 照常解；w = 0.75R ⇒ 600/(1×48) = 12.5 > 11 跳过
        ShapeSearchResult RunIt(bool prescreen, List<(double R, double hw, bool taper, SolverOptions o)> calls)
            => ShapeSearchDriver.Run(seed, new DesignInputs(), new ShapeSearchOptions
            {
                MaxDiscMm = 32, MaxDiscSource = "门用", DiscGridMm = new[] { 32.0 }, EvalSeedFirst = false, Lanes = 1,
                FixpointMax = 0, MaxExtend = 0, ShoulderJPrescreen = prescreen,
                SolveOverride = Fake(0, calls, plateA: 600.0),
            }, null, CancellationToken.None);
        var calls = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var res = RunIt(true, calls);
        var r5 = res.Rows.Where(r => r.Phase == "⑤舌宽").ToList();
        Assert.Equal(2, r5.Count);
        Assert.False(r5[0].Skipped);
        Assert.Equal(0.875 * 32, r5[0].HalfW, 9);
        Assert.True(r5[1].Skipped && r5[1].Prescreened);
        Assert.Contains("闭式跳过：肩部 J", r5[1].SkipWhy);
        Assert.Contains("估计", r5[1].SkipWhy);
        Assert.Contains("12.50", r5[1].SkipWhy);
        Assert.DoesNotContain(calls, c => Math.Abs(c.hw - 0.75 * 32) < 1e-9);
        // w = R（切线族）从不预筛
        Assert.Contains(calls, c => Math.Abs(c.hw - 32) < 1e-9);
        // 改回：关掉预筛 ⇒ 0.75 照常进场解
        var calls2 = new List<(double R, double hw, bool taper, SolverOptions o)>();
        var res2 = RunIt(false, calls2);
        Assert.Contains(calls2, c => Math.Abs(c.hw - 0.75 * 32) < 1e-9);
        Assert.DoesNotContain(res2.Rows, r => r.Prescreened);
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
