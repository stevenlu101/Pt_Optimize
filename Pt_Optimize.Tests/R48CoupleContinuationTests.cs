using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 耦合续跑（2026-09-14，Opus 5 写；规格出自常驻数值把关人第十一轮）：
/// **容差 0.25 K 收敛的那一点，离外层耦合真解到底还有多远？剩余误差估计在共用接头慢模式上低估没有？**
///
/// ══ 起因（R48_耦合容差阶梯_判读_2026-09-14.txt）
/// 容差 1.0 / 0.5 / 0.25 K 三档都收敛，剩余估计 0.831 / 0.458 / 0.112 K；端片几乎不动，
/// 共用片1 抽热 −14.239 / −14.340 / −14.417 W，共用接头（段0 B）增量温降 −34.127 / −33.873 / −33.658 K。
/// 两种解释都容得下三次运行：真误差 ∝ 剩余估计（0.25 K 档离真解约 0.07 K），或等比级数（约 1.1 K）。三次独立停机分不开。
/// 物理把关人另给机理（推理）：共用接头被拆成两段各自的端节点，用上一轮对方温度交换，导度比给出收敛比约 0.97，是慢模式；
/// 且片1 抽热漂 −0.178 W 而接头温降漂 +0.47 K，方向与「2.4 K/W × 抽热」相反 ⇒ 漂的是管子一侧。
///
/// ══ 做法
/// 同一算例（0.5 mm 整线、生产配方、管壁 0.8 落点、舌保温 4.6/2.1/2.9/7.5、圆盘 20 mm）先按 0.25 K 解到收敛（WarmStart 回写），
/// 再用同一 LineCase 热启动续跑：容差设 1e-6 K（实际关掉），固定 300 轮。LineCase.CoupleTrace 每轮记
/// 段0 B 端、段1 A 端增量温降，片1、片2 抽热，步长、真残差、剩余估计、放大系数、ω、Anderson 报告；0.25 K 那次的 Notes 全文也打出来。
///
/// ══ 跑前写死的判读（数值把关人）
///   · 段0 B 端温降续跑末 50 轮均值相对 0.25 K 收敛值漂移 ≤ 0.1 K ⇒ 剩余估计没问题，容差 0.25 K 可用；
///   · 漂移 ≥ 0.5 K ⇒ **剩余估计在共用接头慢模式上低估**，要修的是估法（放大系数按模式量），不是容差；
///   · 其间 ⇒ 记下，不下结论。漂移 &lt; 约 0.01 K 的部分已碰到内层一维解精度地板（0.005 K），不读。
/// </summary>
[Trait("速度", "慢")]
public class R48CoupleContinuationTests
{
    private readonly ITestOutputHelper _out;
    public R48CoupleContinuationTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 容差0点25K收敛后热启动续跑300轮()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_耦合续跑_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false);
        MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        lc.CoupleTolK = 0.25;
        lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000);
        Say($"R48 耦合续跑（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　0.5 mm 细区半径 {radius:0}　Anderson {lc.UseAnderson}（深度 {lc.AndersonDepth}）");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r1 = LineRunner.Run(lc, null, default);
        Assert.True(r1.Ok && r1.Converged, "0.25 K 那次没解到收敛：" + r1.Message);
        double dipB0 = r1.Segments[0].FlangeDipBK, dipA1 = r1.Segments[1].FlangeDipAK, q1 = r1.Flanges[1].QFromTubeW, q2 = r1.Flanges[2].QFromTubeW;
        Say($"阶段一（容差 0.25 K）：{sw.Elapsed.TotalMinutes:0.0} 分　剩余估计 {r1.CoupleRemainK:0.000} K　段0B {dipB0:+0.0000;-0.0000}　段1A {dipA1:+0.0000;-0.0000} K　片1 {q1:+0.0000;-0.0000}　片2 {q2:+0.0000;-0.0000} W");
        foreach (var n in r1.Notes.Where(x => x.Contains("外层耦合"))) Say("   Notes：" + n);
        Assert.True(lc.WarmStart.Length >= lc.SegmentCount, "收敛后 WarmStart 没回写，续跑无从热启动");

        lc.CoupleTolK = 1e-6;
        lc.CoupleMaxRounds = 300;
        var rows = new System.Collections.Generic.List<(int k, double b0, double a1, double q1, double q2)>();
        lc.CoupleTrace = (k, res, delta, resK, remain, amp, omega, aa) =>
        {
            rows.Add((k, res.Segments[0].FlangeDipBK, res.Segments[1].FlangeDipAK, res.Flanges[1].QFromTubeW, res.Flanges[2].QFromTubeW));
            if (k <= 10 || k % 10 == 0)
                Say($"   轮{k,3}　段0B {res.Segments[0].FlangeDipBK:+0.0000;-0.0000}　段1A {res.Segments[1].FlangeDipAK:+0.0000;-0.0000} K　片1 {res.Flanges[1].QFromTubeW:+0.0000;-0.0000}　片2 {res.Flanges[2].QFromTubeW:+0.0000;-0.0000} W"
                  + $"　δ {delta:0.0000}　真残差 {resK:0.0000}　剩余估计 {remain:0.000}（×{amp:0.0}）　ω {omega:0.00}　{sw.Elapsed.TotalMinutes:0.0} 分" + (k % 50 == 0 && aa.Length > 0 ? $"　Anderson：{aa}" : ""));
        };
        var r2 = LineRunner.Run(lc, null, default);
        Say($"阶段二（热启动续跑 {rows.Count} 轮）：{sw.Elapsed.TotalMinutes:0.0} 分　Ok {r2.Ok}　收敛 {r2.Converged}（容差 1e-6 预期不收敛）");
        foreach (var n in r2.Notes.Where(x => x.Contains("外层耦合"))) Say("   Notes：" + n);
        Assert.True(rows.Count >= 60, "续跑轮数太少，判不了");
        var tail = rows.Skip(Math.Max(0, rows.Count - 50)).ToArray();
        double mB0 = tail.Average(t => t.b0), mA1 = tail.Average(t => t.a1), mQ1 = tail.Average(t => t.q1), mQ2 = tail.Average(t => t.q2);
        double drift = Math.Abs(mB0 - dipB0);
        Say("");
        Say($"★ 末 50 轮均值：段0B {mB0:+0.0000;-0.0000}（漂 {mB0 - dipB0:+0.0000;-0.0000} K）　段1A {mA1:+0.0000;-0.0000}（漂 {mA1 - dipA1:+0.0000;-0.0000}）"
          + $"　片1 {mQ1:+0.0000;-0.0000}（漂 {mQ1 - q1:+0.0000;-0.0000} W）　片2 {mQ2:+0.0000;-0.0000}（漂 {mQ2 - q2:+0.0000;-0.0000} W）");
        Say($"   末 50 轮段0B 极差 {tail.Max(t => t.b0) - tail.Min(t => t.b0):0.0000} K（仍在走就不是渐近值）");
        Say(drift <= 0.1 ? "   ⇒ 漂移 ≤ 0.1 K：剩余估计没问题，容差 0.25 K 可用。"
          : drift >= 0.5 ? "   ⇒ 漂移 ≥ 0.5 K：**剩余估计在共用接头慢模式上低估**，要修估法，不是容差。"
          : "   ⇒ 漂移在 0.1～0.5 K 之间：记下，不下结论。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
