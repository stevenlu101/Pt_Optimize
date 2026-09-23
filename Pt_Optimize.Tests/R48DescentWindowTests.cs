using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 决定性探针（2026-09-13，Opus 5 写）：**沿求解器解出的那条逐片向量往下走，可行点存在吗？**
///
/// ══ 为什么是这一条，而不是「四片同值扫一遍」
///
/// 四片同值的扫描（R48InsulWindowTests）给出：净流入在 2.0～2.1 mm 穿零，那里增量温降还有 125 K。
/// 看上去两条判据的界差得极远。**但那是个很差的代理** —— 求解器解出的**逐片**向量
/// （4.60/2.10/2.90/7.50，同一张 15646 单元的网格）却是：
/// <code>
///   管孔净流入 −1.792 W（差 1.79 W 不过）      法兰增量温降 0.573 K（限值 10，**剩 9.4 K 没用**）
/// </code>
/// 逐片自由度把保温放在治增量温降最划算的片上，于是两条判据近得多。
///
/// ⇒ 真正要问的是：**把那 9.4 K 的余量换成净流入，够不够补上 1.79 W？**
///
/// ══ 为什么这个问题现在必须先答
///
/// 已验（读码）：第二遍求根的二分下界是 `lo = Get(d, knob, j)`，即**旋钮当前值**；
/// 第二遍从第一遍（导航网格）的解出发 ⇒ **下不来**。
/// 若可行点在第一遍那个点的**下方**，求解器按构造就够不着它，还会报「只往上走过，这就是最小的可行点」。
///
/// 所以：
///   · 本探针找到可行点 ⇒ 病在求解器够不着，该修的是两遍机制（让第二遍能下来）；
///   · 找不到 ⇒ 修两遍机制是白费力气，得换旋钮，或回头质疑判据本身
///     （净流入的限值是硬零，而复核给它的数值容差是 ±0.5 W —— 判据窗口比模型分辨率还窄）。
/// **方向错就立刻停**，不要先去改求解器。
///
/// ══ 做法
///
/// 沿 λ × (4.60, 2.10, 2.90, 7.50) 往下走，λ = 1.0／0.9／0.8／0.7／0.6，
/// 每个值向下对齐到图纸格 0.1 mm（工程上做得出来的值才算数）。
/// 网格 = 复核第 2 档（0.5 mm，整张一起缩），与那两个判据值同一张。
/// 每点算完立刻落盘。
/// </summary>
[Trait("速度", "慢")]
public class R48DescentWindowTests
{
    private readonly ITestOutputHelper _out;
    public R48DescentWindowTests(ITestOutputHelper o) { _out = o; }

    /// <summary>求解器 09-13 解出的那条逐片舌保温向量（管壁 0.8）。</summary>
    private static readonly double[] Solved = { 4.60, 2.10, 2.90, 7.50 };

    /// <summary>
    /// 扫哪几个 λ。★ 2026-09-14（Opus 5）：第一轮粗扫 1.00／0.90 就把两条判据的**换边点**夹住了 ——
    /// <code>
    ///   λ=1.00  管孔净流入 −1.760 ✗　法兰增量温降  0.586 ✓
    ///   λ=0.90  管孔净流入 +3.618 ✓　法兰增量温降 16.224 ✗
    /// </code>
    /// 线性内插：增量温降 ≤10 要 λ ≥ 0.94，净流入 >0 要 λ ≤ 0.967 ⇒ **窗口 [0.94, 0.967] 非空**。
    /// 原计划的 0.80／0.70／0.60 只会离窗口越来越远（两条判据在那个方向都变差，
    /// 且四片同值扫描已在 0.30–4.60 区间证过单调），信息量低 —— 当场停掉，改扫窗口内。
    /// 保留 1.00 与 0.90 是为了让这张表自带两端的对照，别人不必翻上一轮。
    /// </summary>
    private static readonly double[] Lambdas = { 1.00, 0.97, 0.96, 0.95, 0.94, 0.90 };

    [Fact]
    public void 沿求解器的解往下走看可行点存不存在()
    {
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string log = DeliverableOut.Stamped("R48_下行窗口_2026-09-13.txt");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        Say($"R48 决定性探针：沿求解器的解往下走（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("管壁 0.8。方向 = λ × (4.60, 2.10, 2.90, 7.50)，向下对齐图纸格 0.1 mm。");
        Say("网格 = 复核第 2 档（0.5 mm，整张一起缩）—— 与 −1.792／0.573 那两个数同一张。");
        Say("");
        Say($"{"λ",5}{"舌保温mm",22}{"管孔净流入W",13}{"圆盘区最高温K",15}{"法兰增量温降K",15}{"截面J",8}{"全过",6}{"用时s",7}");

        var p = new DesignInputs();
        var (_, radius) = MeshVerify.RequiredMeshFor(DesignSpec.W08.Clone());
        bool anyFeasible = false;

        foreach (double lam in Lambdas)
        {
            var ins = Solved.Select(v => Math.Max(0.30, Math.Floor(v * lam * 10) / 10)).ToArray();

            var d = DesignSpec.W08.Clone();
            d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
            d.TabInsulMm = ins;
            d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
            d = d.Fit();
            d.SizeTongues(p);

            var lc = d.BuildCase(p, checkRamp: false);
            MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
            var sw = Stopwatch.StartNew();
            double last = 0;
            var tick = new Progress<string>(m =>
            {
                if (sw.Elapsed.TotalSeconds - last < 40) return;
                last = sw.Elapsed.TotalSeconds;
                Say($"     · λ={lam:0.00}　[已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
            });
            LineResult r;
            try { r = LineRunner.Run(lc, tick, default); }
            catch (Exception ex) { Say($"{lam,5:0.00}　解不出来：{ex.Message}"); continue; }
            sw.Stop();
            if (!r.Ok) { Say($"{lam,5:0.00}　解不出来：{r.Message}"); continue; }

            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            bool Ok(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Ok ?? false;
            if (r.AllOk) anyFeasible = true;
            Say($"{lam,5:0.00}{string.Join("/", ins.Select(v => v.ToString("0.0"))),22}"
              + $"{V(LineResult.Key.NetFlux),13:+0.000;-0.000}{(Ok(LineResult.Key.NetFlux) ? " " : "✗")}"
              + $"{V(LineResult.Key.DiscTemp),15:0.000}{(Ok(LineResult.Key.DiscTemp) ? " " : "✗")}"
              + $"{V(LineResult.Key.FlangeDip),15:0.000}{(Ok(LineResult.Key.FlangeDip) ? " " : "✗")}"
              + $"{V(LineResult.Key.SectionJ),8:0.00}{(r.AllOk ? "  ✓" : "  ✗"),6}{sw.Elapsed.TotalSeconds,7:0}");
        }

        Say("");
        Say(anyFeasible
            ? "★ **找到了全过的点** ⇒ 可行集非空，而求解器够不着它（第二遍只增不减）。"
            + "　该修的是两遍机制：第二遍必须能往下走，且下界要回到约束盒下角。"
            : "★ **这条线上没有全过的点** ⇒ 别去改两遍机制。要么换旋钮，"
            + "要么回头质疑判据：管孔净流入的限值是硬零，而复核给它的数值容差是 ±0.5 W，"
            + "判据窗口比模型自己的分辨率还窄 —— 那是判据设定的问题，不是设计的问题。");
        Say("");
        Say("⚠ 本扫描只沿**一条直线**走。线上没有不等于可行集为空 —— 只能说这个方向上没有。");

        Assert.True(sb.ToString().Split('\n').Count(l => l.Contains("✓") || l.Contains("✗")) >= 3,
            "扫描没产生足够数据行 —— 仪器空转了，别拿它下结论");
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
