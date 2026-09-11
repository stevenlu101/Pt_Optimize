using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **「四片等厚、r₁/r₂ 与 t₁/t₂ 一点没优化」是怎么来的**
/// （2026-09-05 用户看 `整机.3dm` 指出来的第 2 条）。
///
/// 交付件上四片一模一样：
/// <code>
///   片      板厚    舌保温  环倍率t₁  外级t₂  圆盘槽  孔径   孔拉长
///   入口    4.710   1.80    2.50      1.00    0       36.0   1.0
///   共用1   4.710   0.30    1.00      1.00    0        0.0   1.0
///   共用2   4.710   0.30    1.00      1.00    0        0.0   1.0
///   出口    4.710   0.30    1.00      1.00    0        0.0   1.0
/// </code>
/// 四片承的电流与热边界都不同，**不该长得一样**。本文件把「为什么」量出来。
/// </summary>
public class PerPlateDivergeTests
{
    private static DesignSpec Delivered()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 2.0;
        return d;
    }

    /// <summary>
    /// ★★★★★ **4.710 到底是解出来的，还是撞在下界上**。
    ///
    /// 这两件事在报表上长得一模一样，但意思完全相反：
    ///   · 解出来的 ⇒ 求解器认为四片刚好都需要这么厚（可疑，但可能）
    ///   · 撞下界   ⇒ 板厚**根本没被优化过**，报表印的是**焊接屈曲下界**
    ///
    /// 下界 = max(焊接屈曲斜率 ×(盘R − 孔R)× 安全系数, 烧穿下界 0.6)，
    /// 与电流、与判据**统统无关** —— 四片当然一样。
    /// </summary>
    [Fact]
    public void 四片等厚是撞下界还是解出来的()
    {
        var baseIn = new DesignInputs();
        var d = Delivered();
        double floor = d.DiscFloorMm(baseIn);

        var sb = new StringBuilder();
        sb.AppendLine("═══ 「四片等厚」的来源 ═══");
        sb.AppendLine($"构型 盘R {d.DiscRadiusMm}、管孔R {d.HoleRadiusMm}、"
                    + $"焊接安全系数 {baseIn.WeldSafetyFactor}、烧穿下界 {baseIn.WeldMinThicknessMm} mm");
        sb.AppendLine($"⇒ **板厚下界 DiscFloorMm = {floor:0.000} mm**");
        sb.AppendLine($"   交付件四片印的都是 4.710 mm");
        sb.AppendLine();

        bool isFloor = Math.Abs(floor - 4.710) < 0.005;
        sb.AppendLine(isFloor
            ? "⇒ **就是下界**。板厚这根旋钮在这个构型上**从来没被抬过** ——\r\n"
            + "   报表印的是「焊接屈曲要求的最小厚度」，与电流、与判据全无关系，\r\n"
            + "   四片当然一模一样。用户看到的「4 组法兰等厚」= 板厚根本没参与优化。"
            : $"⇒ 下界是 {floor:0.000}，与 4.710 不同 ⇒ 4.710 是**解出来的**，四片恰好落在同一点。");
        sb.AppendLine();

        // 逐片列一遍：环台阶半径与厚度，看它们是不是也全一样
        sb.AppendLine("逐片几何（按当前设定造出来的）：");
        sb.AppendLine("片\t板厚 mm\t内级 r₁\t外级 r₂\t内级厚\t外级厚");
        string[] nm = { "入口", "共用1", "共用2", "出口" };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            var rr = d.RingRadiiOf(j);
            double td = Math.Max(d.TabThickMm[j], floor);
            sb.AppendLine($"{nm[j]}\t{td:0.000}\t{rr[0]:0.00}\t{rr[1]:0.00}"
                        + $"\t{td * d.RingMul[j]:0.000}\t{td * d.RingMulOuter(j):0.000}");
        }
        sb.AppendLine();
        sb.AppendLine("环台阶宽度的上界（SolverOptions）：r₁ ≤ 10.0 mm、r₂ ≤ 16.0 mm，环倍率 ≤ 2.5");
        sb.AppendLine("⚠ 外级倍率 t₂ = 1.00 ⇒ 外级是**平的**，没有台阶 ⇒ r₂ 这根旋钮"
                    + "被 ChooseKnob 当场淘汰（「这一级还是平的，没有台阶可挪」）。");

        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "四片为什么等厚.txt"),
                          sb.ToString());
        Console.WriteLine(sb.ToString());

        // 本门只记录事实，不判对错 —— 但下界必须算得出来
        Assert.True(floor > 0 && !double.IsNaN(floor), "板厚下界算不出来");
    }

    /// <summary>
    /// ★★★★★ **爆炸不许被印成「这根旋钮没用」** —— 行为门（2026-09-07 督导 S2）。
    ///
    /// ⚠ 本门原来是**贴源码字面**的：`Assert.Contains("场解不收敛", body)`
    ///   加一句「ChooseKnob 里要出现 rk is null」。它只验代码**提到过**这个词，
    ///   **结构上就拦不住爆炸** —— 爆出来的是有限数（实测 −8.0e26、−2.6e102），
    ///   `rk` 不为 null，于是从第二支 `!(dSlack > 1e-9)` 走掉，印成「抬到底也没变好」。
    ///   我自己立过「不许钉措辞」的规矩，这条门就是反面教材，现在换成真跑。
    ///
    /// 实物（deliverable/对帐超时_轨迹.txt，09-06）：
    /// <code>
    ///   片2 候选：外级倍率 t₂ 抬到底也没变好（-1267 → -8.0e26）
    ///             板厚      抬到底也没变好（-1267 → -2.6e102）
    /// </code>
    ///
    /// 构型不是编的：就是当时那份被污染的 W08（巨型椭圆孔吃掉管孔那一圈）。
    /// 断言只钉一条**不变式**，不钉任何措辞或数字：
    /// **轨迹里凡是说「没变好」的那一行，括号里的数必须是个像样的物理量。**
    /// 说不出话来（场没解出来）就得说「不收敛」，那是另一条处置路径。
    /// </summary>
    [Trait("速度", "慢")]   // 真跑求解器
    [Fact]
    public void 爆炸不许被印成没变好()
    {
        // ★ R40（2026-09-11 验收慢门实跑抓到）：09-06 那个「被污染的 W08 巨孔」坏例在 J 定截面的新链（R22 之后）下
        //   第一轮就停在「法兰截面 J」—— 它没有旋钮能治（要改形状），一根旋钮都没抬、一行「没变好」都没印，
        //   本门自己的防空转断言把它判红（空集恒真）。坏例的初衷是「场爆炸不许印成没变好」，那条不变式与构型无关：
        //   换成 R36 对拍实跑里**确实印过「没变好」**的那个候选（2 段、盘Ø66／舌宽35、MaxRounds=2，
        //   deliverable/搜形状并行化_逐位对拍_2026-09-11.txt 候选 0），门就有活干了。
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10;
        d.Name = "没变好分支的实跑案例（R36 对拍候选 0）";
        d.DiscRadiusMm = 33; d.TabHalfWidthMm = 17.5;
        d.TabLengthMm = Math.Sqrt(Math.Max(0, 33 * 33 - 17.5 * 17.5)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;

        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            // ⚠ 轮数不能设太紧：第一版写 MaxRounds=2 / MaxPartialRounds=0，
            //   求解器在「整轮没有一根抬得动」就收摊，**一行「没变好」都没印**
            //   ⇒ 门在**空集上恒过**（正是督导点过的那一族）。见下面那道防空转的断言。
            FineMm = 0, FineRadiusMm = 0, MaxRounds = 2, MaxPartialRounds = 2,
        });

        // 「没变好（a→b）」里的 b：必须是个像样的物理量。
        //   判据不含阈值 —— 用**限值本身**当尺度：裕度是 limit − actual，
        //   而限值是 O(10)（③ ≤ 10 K、②″ ≤ 5 K）。差到 10^6 倍以上的不是裕度，是数值爆炸。
        var rx = new Regex(@"没变好（\s*([-+0-9.eE]+)\s*→\s*([-+0-9.eE]+)\s*）");
        var bad = new List<string>();
        foreach (var line in sr.Trace)
            foreach (Match m in rx.Matches(line))
                if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double after)
                 && (!double.IsFinite(after) || Math.Abs(after) > 1e7))
                    bad.Add($"{after:0.###e+0}：{line.Trim()}");

        // ★★★★★ **门不许什么都没验到**（2026-09-07 当场栽的）。
        //   第一版这条门是绿的，而轨迹里**一行「没变好」都没有** —— 它在空集上恒过。
        //   把 S1 临时关掉它照样绿，我才发现。⇒ 先证明这一跑确实走到了那条分支。
        int sawNoImprove = sr.Trace.Count(l => l.Contains("没变好"));
        Assert.True(sawNoImprove > 0,
            "本门这一跑**一条「没变好」都没印**，等于什么都没验到（空集恒真）。"
          + $"轨迹 {sr.Trace.Count} 行，停在：{sr.StopWhy}　"
          + "—— 要么构型没走到那条分支，要么轮数设得太紧。门必须先证明自己有活干。");

        Assert.True(bad.Count == 0,
            "轨迹把**数值爆炸**印成了「这根旋钮没变好」——"
          + "两者的处置完全相反（没用 ⇒ 淘汰候选；爆炸 ⇒ 场没解出来，该修的是上界或几何）。"
          + string.Join(" ｜ ", bad.Take(4)));
    }
}
