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
    //  下角的第三个来源「不熔化」（2026-09-08，督导第 15/16 封）
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
    /// ★★★★★ 下角因熔化上抬 —— 这条分支真的被走到，而且**每抬一片留一痕、只增不减、抬完不熔**。
    /// 反自证：起点必须真的熔（一片都没抬 ⇒ 门落在空集上），构型漂了就换一个会熔的起点，不许放行。
    /// 代价：熔化区里的场解在第一次 RunOnce 就返回，便宜；整趟 ~13 次场解（上界 1 + 二分 ~10 + 验 2）。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 下角因熔化上抬_这条分支走到了()
    {
        var (d, p, o) = CornerStart();
        var before = (double[])d.TabThickMm.Clone();
        var res = new SolverResult();
        // ★ 轨迹落档（仪器）：跑到一半也看得见；断言在后面
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "下角因熔化上抬_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, $"═══ W08 闭式下角 {before[0]:0.00} mm 起，MeltFloor 轨迹（导航网格）═══" + Environment.NewLine);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, handOff, why, last) = Solver.MeltFloor(d, p, o, res, default,
            new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,5:0.0} 分] {s}" + Environment.NewLine)));
        File.AppendAllText(dump, $"耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {res.Solves} 次　板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))}　ok={ok} handOff={handOff} {why}" + Environment.NewLine);

        var raised = res.Trace.Where(s => s.TrimStart().StartsWith(BranchMarks.MeltFloorRaised, StringComparison.Ordinal)).ToArray();
        Assert.True(raised.Length >= 1,
            "闭式下角处一片都没熔、一片都没抬 —— 本门落在空集上恒过了。"
          + "构型漂了就要重新探一个会熔的起点，不许放行。轨迹：" + string.Join(" | ", res.Trace));
        Assert.True(ok && !handOff, "起点熔了却没走出熔化区：" + why);
        Assert.NotNull(last);
        Assert.False(last!.OverMelt, "抬完之后最后那次场解仍报熔化 —— 二分的不变式（hi 不熔）破了");

        // 抬过的片都留了痕、留了痕的片都真的抬了（一遍可能抬几片，一片可能抬几遍 ⇒ 比的是**片的集合**）；
        // 只增不减；没抬的片一位不动
        var movedSet = new System.Collections.Generic.HashSet<int>();
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            Assert.True(d.TabThickMm[j] >= before[j] - 1e-12, $"片{j} 板厚 {before[j]} → {d.TabThickMm[j]}：下角**降**了，只增不减被破了");
            if (d.TabThickMm[j] > before[j] + 1e-12) movedSet.Add(j);
        }
        var markedSet = new System.Collections.Generic.HashSet<int>(raised.Select(s =>
            int.Parse(System.Text.RegularExpressions.Regex.Match(s, "：片(\\d+) ").Groups[1].Value)));
        Assert.True(movedSet.SetEquals(markedSet),
            $"动过的片 {{{string.Join(",", movedSet)}}} 与留痕的片 {{{string.Join(",", markedSet)}}} 对不上 —— 有片被悄悄抬了或痕迹指错了片");
        foreach (var line in raised) Console.WriteLine(line);
        Console.WriteLine($"板厚 {string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00")))}　场解 {res.Solves} 次");
    }

    /// <summary>
    /// ★★★★★ 板厚抬到工艺上界仍熔 ⇒ **交棒**（不是判无解）—— 这条分支真的被走到。
    /// 手段：把厚度上界压到起点上方一格，第一次验上界就仍熔。代价：2 次熔化区场解。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 下角抬到厚度上界仍熔就交棒_这条分支走到了()
    {
        var (d, p, o) = CornerStart();
        o.ThickHiMm = d.TabThickMm[0] + 0.02;
        var before = (double[])d.TabThickMm.Clone();
        var res = new SolverResult();
        var (ok, handOff, why, _) = Solver.MeltFloor(d, p, o, res);

        Assert.Contains(res.Trace, s => s.TrimStart().StartsWith(BranchMarks.MeltFloorHandOff, StringComparison.Ordinal));
        Assert.False(ok);
        Assert.True(handOff, "交棒了却没标成结构性停机 ⇒ 指路层不知道这是「厚度到顶」");
        Assert.Contains("增宽", why);
        // 「无解」只许以否定形式出现（本层没有宽度旋钮，无权替搜形状下这个结论）
        for (int i = 0; (i = why.IndexOf("无解", i, StringComparison.Ordinal)) >= 0; i += 2)
        {
            string near = why[Math.Max(0, i - 12)..i];
            Assert.True(near.Contains("不是", StringComparison.Ordinal) || near.Contains("不许", StringComparison.Ordinal),
                "交棒讯息里出现了正面的「无解」结论：" + why);
        }
        // 交棒时板厚退回起点 —— 一个抬不动的值不许留在模型里冒充下角
        for (int j = 0; j < d.TabThickMm.Length; j++) Assert.Equal(before[j], d.TabThickMm[j], 12);
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
                "**未造出**（不是「到不了」—— 正常搜索里可达）：熔化现在由 EvalMelt 先处置（落地或副本上抬），"
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
