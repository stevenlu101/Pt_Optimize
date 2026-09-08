using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **0.8 档对帐为什么 60 分钟跑不完 —— 把轨迹打出来看**（2026-09-06）。
///
/// 已经猜错两次，不再猜：
/// <code>
///   猜① 续轮 MaxPartialRounds     → 改成「整轮抬不动才花预算」，仍超时
///   猜② 候选数（跳过空转候选）     → 跳掉孔拉长/r₁/r₂，仍超时
/// </code>
/// ⇒ 直接跑，把 <c>SolverResult.Trace</c> 落到文件里看它每一轮在干什么。
///
/// 只跑导航网格（细网格那一遍每次贵一个量级），拿的是**轨迹**不是答案。
/// </summary>
public class ReconcileTraceTests
{
    /// <summary>同步的进度接收器（Progress&lt;T&gt; 是异步投递的，落档会乱序）。</summary>
    private sealed class FileProgress : IProgress<string>
    {
        private readonly Action<string> _f;
        public FileProgress(Action<string> f) => _f = f;
        public void Report(string v) => _f(v);
    }

    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 打出0点8档的求解轨迹()
    {
        var d = DesignSpec.Builtin[0].Clone();       // W08
        // ★ 用户 2026-09-08：每次论证验算跑 2 段（3 片法兰）；3 段的 3480.7 g 只当归档基准，不再重跑。
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        // ★ 边跑边落轨迹（2026-09-08）：此前只在跑完才写档，2.5 小时里一行都看不到，分不清慢和挂。
        string dump = Path.Combine(HandoverDoc.Root(), "deliverable", "对帐超时_轨迹.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, "═══ 0.8 档求解轨迹（2 段 3 片，导航网格；边跑边写，末尾有合计）═══" + Environment.NewLine
                              + "对照：3 段的归档基准 3480.7 g（bba08c7 之前口径，不可与 2 段直接比）" + Environment.NewLine + Environment.NewLine);
        var sw = Stopwatch.StartNew();
        var live = new FileProgress(s => File.AppendAllText(dump, $"[{sw.Elapsed.TotalMinutes,6:0.0} 分] {s}" + Environment.NewLine));
        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            FineMm = 0, FineRadiusMm = 0,
            MaxRounds = 15,
            MaxPartialRounds = 2,
        }, live);
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"═══ 合计：耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次　"
                    + $"可行 {sr.Feasible}　合计 {sr.MassG:0.0} g");
        sb.AppendLine($"停在：{sr.StopWhy}");
        File.AppendAllText(dump, sb.ToString());
        Console.WriteLine($"耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　场解 {sr.Solves} 次");
        Assert.True(sr.Trace.Count > 0, "轨迹是空的 —— Trace 没在记");

        // ★★★★★ 2026-09-08 晚，用户设计因果链落地后这条门的**意思变了两次**：
        //   R18 第一阶段（舌片与圆盘同厚）：共用片舌片截面要 4.26 mm ⇒ 焊脚 4.26 ⇒ 盘 R30 盖不住 ⇒ ⑥ 前停（曾钉零场解）。
        //   R11（舌片厚 = I/(10·舌宽) 与圆盘解耦）之后：舌片自己 2.03/3.51/2.03 mm，圆盘基板只按圆盘侧截面定
        //   ⇒ 焊脚不再被舌片抬高 ⇒ ⑥ 过 ⇒ 真解：**可行 2911.4 g**（2 段 3 片，2 轮 48 次场解 17.4 分钟，
        //   停因「第 2 轮全过；只往上走过 ⇒ 最小可行点」）。deliverable/对帐超时_轨迹.txt 留痕。
        //   本门钉：① 设计电流印出来了；② 舌片厚按 I/(J·舌宽) 定的痕在；③ 解出可行、场解 > 0、铂重是正数；
        //   ④ 舌片截面 J ≤ 10（进模型的舌片厚就是算出来的那个）；⑤ 停因不是「判不了／不收敛／⑥」。
        //   固定形状 3480.7 g（3 段）那套口径已作废：那份解舌根 J_max 37、热点 +99 K，用户 09-08：完全不可用。
        Assert.Contains(sr.Trace, s => s.Contains("设计电流", StringComparison.Ordinal));
        Assert.Contains(sr.Trace, s => s.TrimStart().StartsWith("★ 舌片厚按 I/(J·舌宽) 定", StringComparison.Ordinal));
        Assert.NotNull(sr.DesignCurrent);
        Assert.True(sr.Solves > 0, "R11 之后这个形状该真解，不该零场解就停");
        Assert.True(sr.Feasible, "R11 之后 W08 固定形状（2 段）应可行：" + sr.StopWhy);
        Assert.InRange(sr.MassG, 1000, 6000);
        for (int j = 0; j < sr.Design.FlangeCount; j++)
        {
            Assert.False(double.IsNaN(sr.Design.TongueThickMm[j]), $"片{j} 舌片厚没定");
            double iA = sr.DesignCurrent!.PlateA[j];
            var tabCuts = SectionSizing.Cuts(sr.Design.Plate(j, 0), iA, sr.Design.ClampLengthMm).Where(c => c.OnTab);
            Assert.All(tabCuts, c => Assert.True(c.JAPerMm2 <= 10.0 + 1e-9, $"片{j} {c.Where} J={c.JAPerMm2:0.00}"));
        }
        Assert.False(sr.StopWhy.Contains("判不了", StringComparison.Ordinal), $"停因不该是判不了：{sr.StopWhy}");
        Assert.False(sr.StopWhy.Contains("不收敛", StringComparison.Ordinal), $"停因不该是不收敛：{sr.StopWhy}");
        Assert.False(sr.StopWhy.Contains("⑥", StringComparison.Ordinal), $"R11 之后不该再卡 ⑥：{sr.StopWhy}");
    }
}
