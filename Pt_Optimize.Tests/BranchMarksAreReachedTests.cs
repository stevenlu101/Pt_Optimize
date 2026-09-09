using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **断言「走到了」，不是断言「结果好」**（2026-09-08，督导第 11 封）。
///
/// 病：2026-09-07 一天加了三处修复，**没有一处被实测走过** ——
/// 熔化「只抬最热那一片」（自检 E 段 6 轮正常收敛，没触发）、
/// 二分 mid 判不了 ⇒ 中止、抬前/上界 判不了（0.8 档那 72 次场解全收敛，没踩到）。
/// 而 674/674 绿、自检通过，**对这三处一个字都没说**：它们在**空集上恒对**。
///
/// 手段：分支自己往轨迹写一句话（<see cref="BranchMarks"/>），门断言那句出现。
/// **不必让它真的解出好结果，只要让那条分支被执行。**
///
/// 探到的构型（2026-09-08 实测，一次场解 ~14 s）：
/// <code>
///   W08 板厚 ×1.00  峰值 1235 °C  不熔
///   W08 板厚 ×0.60  峰值 4236 °C  **熔**   ← 用它
/// </code>
/// </summary>
public class BranchMarksAreReachedTests
{
    /// <summary>W08 的一个**必然熔化**的起点（实测 4236 °C）。</summary>
    private static (DesignSpec D, double[] Thin, DesignInputs P) MeltingStart()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        var thin = d.TabThickMm.Select(v => v * 0.60).ToArray();
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = thin[j];
        return (d, thin, p);
    }

    private static FlangePlate MakePlate(DesignSpec d, DesignInputs p, double t, int j)
    {
        double floor = d.DiscFloorMm(p);
        var g = d.Plate(j, floor);
        g.ThicknessMm = t;                       // ★ 不夹到 floor —— 本门要的正是「薄到会熔」
        g.TabThicknessMm = t;
        g.DiscStepThicknessMm = new[] { t * d.RingMul[j], t * d.RingMulOuter(j) };
        return g;
    }

    /// <summary>
    /// ★★★★★ 熔化 ⇒ **只抬最热那一片**（不是整片乘）—— 这条分支真的被走到。
    /// 反自证：先断言这个起点**确实熔**，否则门是在空集上恒过。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 熔化时只抬最热那一片_这条分支走到了()
    {
        var (d, thin, p) = MeltingStart();
        var lc = d.BuildCase(p, checkRamp: false);
        lc.FlangePlates = thin.Select((t, j) => MakePlate(d, p, t, j)).ToArray();

        // ── 反自证：起点必须真的熔，不然下面断言什么都不说明
        var probe = LineRunner.Run(lc, null, default);
        Assert.True(probe.OverMelt,
            $"起点没熔（Ok={probe.Ok}，峰值 {probe.Flanges.Max(f => f.TMaxC):0} °C）—— "
          + "本门就落在空集上恒过了。构型漂了就要重新探一个会熔的起点，不许放行。");

        var res = FlangeAutoSizer.Solve(lc, (t, j) => MakePlate(d, p, t, j), (double[])thin.Clone(),
                      new FlangeAutoSizer.Options { MaxIterations = 2 }, null, default);

        Assert.Contains(res.Trace, s => s.StartsWith(BranchMarks.MeltRaiseHottest, StringComparison.Ordinal));

        // 只抬**一片**：轨迹那一行要指名是哪一片，且这一轮只有它动了
        string line = res.Trace.First(s => s.StartsWith(BranchMarks.MeltRaiseHottest, StringComparison.Ordinal));
        int moved = res.ThicknessMm.Where((v, j) => Math.Abs(v - thin[j]) > 1e-9).Count();
        Assert.True(moved <= 2,       // MaxIterations=2 ⇒ 最多两轮，每轮一片
            $"两轮里动了 {moved} 片 —— 「只抬最热那一片」被写回成整片乘了。轨迹：{line}");
    }

    /// <summary>
    /// ★★★★★ 厚度到顶仍熔 ⇒ **交棒**（不是判无解）—— 这条分支真的被走到。
    /// 手段：把工艺上界压到起点厚度，第一轮就顶死。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 厚度到顶仍熔就交棒_这条分支走到了()
    {
        var (d, thin, p) = MeltingStart();
        var lc = d.BuildCase(p, checkRamp: false);
        lc.FlangePlates = thin.Select((t, j) => MakePlate(d, p, t, j)).ToArray();

        // 上界 = 起点**最薄**的那片 ⇒ 一片都抬不动（更厚的那些 next 只会更小）⇒ 直接走交棒那一支
        var res = FlangeAutoSizer.Solve(lc, (t, j) => MakePlate(d, p, t, j), (double[])thin.Clone(),
                      new FlangeAutoSizer.Options { MaxIterations = 2, MaxThickMm = thin.Min() },
                      null, default);

        Assert.Contains(res.Trace, s => s.StartsWith(BranchMarks.MeltHandOff, StringComparison.Ordinal));
        Assert.True(res.Terminal, "交棒了却没置 Terminal ⇒ 指路层不知道这是结构性停机");
        Assert.Contains("增宽", res.TerminalWhy);
        Assert.DoesNotContain("这个几何在此电流下无解", res.TerminalWhy);
    }

    // ══════════════════════════════════════════════════════════════════
    //  熔化只判不抬（用户 2026-09-09；09-08 曾是「下角因熔化上抬」，按设定 J 的截面进下角后那条路不可达）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>同步的进度接收器（Progress&lt;T&gt; 是异步投递的，落档会乱序）。</summary>
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    /// <summary>
    /// W08 的**约束盒闭式下角**：与 <c>Solver.Solve</c> 同一算法（板厚 = max(焊接屈曲, 烧穿) 向上对齐到图纸格，
    /// 舌保温裸舌、环倍率无台阶）。⚠ 不读设计记录里的板厚 —— 下角只能由约束推出。
    /// </summary>
    private static (DesignSpec D, DesignInputs P, SolverOptions O) CornerStart()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        var o = new SolverOptions();
        double tLo = Math.Ceiling(d.DiscFloorMm(p) / o.QuantThickMm - 1e-9) * o.QuantThickMm;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = tLo; d.TabInsulMm[j] = o.InsLoMm;
            d.RingMul[j] = o.RingLo; d.RingMul2[j] = o.RingLo;
        }
        return (d, p, o);
    }

    /// <summary>
    /// ★★★★★ 熔化 ⇒ **停、不抬厚度**（用户 2026-09-09：熔化是判断工具，不是旋钮）—— 这条分支真的被走到。
    /// 构型：W08 只按屈曲/烧穿的闭式下角 0.60 mm（不套 J 截面下界）⇒ 必熔（09-08 实测峰值 6260 °C）。
    /// 反自证：起点必须真的熔（不熔 ⇒ 门落在空集上）。代价：1 次熔化区场解（第一次 RunOnce 就返回，便宜）。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 下角熔化就停_不抬厚度_这条分支走到了()
    {
        var (d, p, o) = CornerStart();
        var before = (double[])d.TabThickMm.Clone();
        var tongueBefore = (double[])d.TongueThickMm.Clone();
        var res = new SolverResult();
        var (ok, handOff, why, last) = Solver.MeltFloor(d, p, o, res);

        Assert.Contains(res.Trace, s => s.TrimStart().StartsWith(BranchMarks.MeltStop, StringComparison.Ordinal));
        Assert.False(ok, "起点没熔 ⇒ 本门落在空集上恒过了。构型漂了就要重新探一个会熔的起点，不许放行。轨迹：" + string.Join(" | ", res.Trace));
        Assert.False(handOff, "熔化不再交棒：它不是「往上走能救」的停机");
        Assert.Null(last);
        Assert.Contains("该解不存在", why);
        Assert.Contains("不抬", why);
        Assert.Contains("J=", why);
        Assert.DoesNotContain("°C", why);          // 熔化区里的温度数一个都不引用（S1）
        Assert.Equal(1, res.Solves);               // 只解一次：不二分、不验上界
        for (int j = 0; j < before.Length; j++) Assert.Equal(before[j], d.TabThickMm[j], 12);   // 板厚一位不动
        for (int j = 0; j < tongueBefore.Length; j++)
            Assert.True(double.IsNaN(tongueBefore[j]) ? double.IsNaN(d.TongueThickMm[j]) : tongueBefore[j].Equals(d.TongueThickMm[j]),
                        $"片{j} 舌片厚被动了：{tongueBefore[j]} → {d.TongueThickMm[j]}");
    }

    /// <summary>
    /// ★★★★★ 下角因 J=10 截面上抬（用户 2026-09-08 设计因果链第 ②步）—— 这条分支真的被走到，而且抬完各片最紧截面 J ≤ 10。
    /// 不解场：设计电流由升温模型（毫秒级）给，截面闭式。
    /// </summary>
    [Fact]
    public void 下角因J10截面上抬_这条分支走到了()
    {
        var (d, p, o) = CornerStart();
        var before = (double[])d.TabThickMm.Clone();
        var res = new SolverResult();
        var (raised, floors) = Solver.ApplySectionFloor(d, p, o, res, null, s => res.Trace.Add(s));

        Assert.True(raised, "闭式下角 0.60 mm 处按 J=10 一片都不用抬 —— 本门落在空集上；构型漂了要重探");
        Assert.Contains(res.Trace, s => s.TrimStart().StartsWith(BranchMarks.JFloorRaised, StringComparison.Ordinal));
        Assert.NotNull(res.DesignCurrent);
        Assert.All(res.DesignCurrent!.PlateA, a => Assert.True(a > 100, $"设计电流 {a} A 不像样"));
        double floorD = d.DiscFloorMm(p);
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            Assert.True(d.TabThickMm[j] >= before[j] - 1e-12, $"片{j} 下角**降**了：{before[j]} → {d.TabThickMm[j]}");
            Assert.Equal(floors[j], d.TabThickMm[j], 9);
            var w = SectionSizing.Worst(d.Plate(j, floorD), res.DesignCurrent.PlateA[j], d.ClampLengthMm);
            Assert.True(w.JAPerMm2 <= SectionSizing.JDesignAPerMm2 + 0.2,
                $"片{j} 抬到 {d.TabThickMm[j]:0.00} 之后最紧截面 {w.Where} 仍 J={w.JAPerMm2:0.00} > 10");
        }
        // 共用片电流 ≥ 端片 ⇒ 共用片的下角不低于端片
        int n = d.TabThickMm.Length;
        for (int j = 1; j < n - 1; j++)
            Assert.True(d.TabThickMm[j] >= Math.Min(d.TabThickMm[0], d.TabThickMm[n - 1]) - 1e-9, "共用片的 J 下角比端片还低 —— 电流合成没接上");
        Console.WriteLine(res.DesignCurrent.Describe());
        Console.WriteLine("板厚 " + string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00"))));
    }

    // ══════════════════════════════════════════════════════════════════
    //  登记门：加了痕迹却没人看 = 「造好了没接线」，本仓最常犯的一族
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★★★★ <see cref="BranchMarks"/> 每多一个常数，就必须
    /// ① Core 里真的有人**发**它　② 有测试真的**断言**过它。
    /// 少一样就红 —— 否则「留痕」本身又会变成一处「造好了没接线」。
    /// </summary>
    [Fact]
    public void 每个痕迹都要有人发也要有人看()
    {
        var marks = typeof(BranchMarks)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToArray();
        Assert.True(marks.Length >= 5, $"只反射出 {marks.Length} 个痕迹 —— 本门认不得 BranchMarks 了（空转）");

        string core = string.Concat(Directory.EnumerateFiles(
            Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core"), "*.cs")
            .Where(f => !f.EndsWith("BranchMarks.cs", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
        // ⚠ 扫测试源码时要把**欠账表自己那几行**剔掉：表里写着 nameof(BranchMarks.X)，
        //   不剔的话「登记成欠账」会被本门读成「已经有人断言它」—— 门骗自己一次（实测踩到）。
        string tests = string.Join(((char)10), Directory.EnumerateFiles(
            Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests"), "*.cs")
            .SelectMany(f => File.ReadAllLines(f))
            .Where(ln => !ln.Contains("nameof(BranchMarks.", StringComparison.Ordinal)));

        // ★★★★★ **还没造出触发构型的，必须逐条写明理由** —— 不许空着。
        //   「未知」与「不适用」是两回事（HANDOVER 通则，2026-09-08）：
        //   混成一栏会让欠账看起来像已处理。这张表就是欠账本身，它在源码里、数得出来。
        var 欠账 = new System.Collections.Generic.Dictionary<string, string>
        {
            [nameof(BranchMarks.UndeterminedAtHi)] =
                "**结构上到不了**（不是没造出来）：ChooseKnob 在上游已把「上界处不收敛」的候选"
              + "淘汰掉（Solver.cs 709-716，并自己留了痕）⇒ 被选中的旋钮其 hi 必然收敛过一次；"
              + "RaiseUntil 重解同一构型是确定性的 ⇒ rHi 不会是 null。属**防御性分支**，"
              + "留着是对的（调用图一改它就活），但不该假装它被覆盖了。",
            [nameof(BranchMarks.UndeterminedBefore)] =
                "**未造出**：要「判据值本身是 NaN」与「重解失败」两个条件同时成立。"
              + "0.8 档那 72 次场解全收敛，踩不到。欠账。",
            [nameof(BranchMarks.UndeterminedBisect)] =
                "**未造出**：要 mid 处熔/发散而 lo、hi 两端都好 —— 一个非单调的窗口。欠账。",
            [nameof(BranchMarks.EvalNotOk)] =
                "**未造出**（不是「到不了」—— 正常搜索里可达）：熔化现在由 EvalMelt 判成「该解不存在」（MeltStop，2026-09-09），"
              + "走到这里的只剩 Ok=false 而不是熔的失败（段解失败：热稳定极限内到不了控温点）；"
              + "要触发得有那样的构型，未造出。"
              + "⚠ 修下角**之前**它被 0.8 档真走到过一次（deliverable/对帐超时_轨迹.txt，2026-09-08，"
              + "痕迹后面跟着「峰值 6313 °C 已越过铂熔点」）—— 那次就是本痕迹存在的理由。欠账。",
        };

        foreach (var m in marks)
        {
            string sym = "BranchMarks." + m.Name;
            Assert.True(core.Contains(sym, StringComparison.Ordinal),
                $"{sym} 登记了，但 Core 里**没有人发它** —— 这个痕迹永远不会出现，"
              + "断言它的门就落在空集上恒过。");

            bool asserted = tests.Contains(sym, StringComparison.Ordinal);
            bool owed = 欠账.ContainsKey(m.Name);
            Assert.True(asserted || owed,
                $"{sym} 发出去了，但**没有任何测试断言它**，也没写进本门的欠账表 —— "
              + "那条分支照旧「走没走过没人知道」，留痕白留（造好了没接线）。"
              + "要么补一条行为门，要么把**为什么造不出来**写进欠账表。");

            // 反向：还清了却留在欠账表里 ⇒ 表是死的，会掩护下一笔
            if (owed && asserted)
                Assert.Fail($"{sym} 已经有测试断言它了，却还挂在欠账表上 —— "
                          + "把它从欠账表删掉，否则这张表会变成藏东西的地方。");
        }

        Console.WriteLine($"痕迹 {marks.Length} 个：已覆盖 {marks.Count(m => tests.Contains("BranchMarks." + m.Name, StringComparison.Ordinal))} 个，"
                        + $"欠账 {欠账.Count} 个");
        foreach (var kv in 欠账) Console.WriteLine($"  · {kv.Key}：{kv.Value}");
    }
}
