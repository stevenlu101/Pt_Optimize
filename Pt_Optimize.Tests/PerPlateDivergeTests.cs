using System;
using System.IO;
using System.Linq;
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
    /// ★★★★★ **场解不收敛，不许报成「这根旋钮没变好」**。
    ///
    /// 交付件的停机理由里有这一句：
    /// <code>
    ///   内级半径 r₁ 抬到底也没变好（-81.296→-∞）
    /// </code>
    /// −∞ 是 <c>PlateSlack(null, …)</c> 的返回值 ——「**场解压根没收敛**」，
    /// 不是「抬了没用」。两者的处置完全不同：
    ///   · 没用   ⇒ 淘汰这个候选，对
    ///   · 不收敛 ⇒ 上界给错了／几何被抬坏了，**要修的是上界，不是放弃这根旋钮**
    ///
    /// 把不收敛渲染成「没用」，正是「安静失败」那一族 ——
    /// 用户看到的「r₁/r₂ 完全没有优化」就是它的后果。
    /// </summary>
    [Fact]
    public void 不收敛不许报成没变好()
    {
        string s = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        int at = s.IndexOf("private static (Knob? Knob, string Why, double Before, double After) ChooseKnob",
                           StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 ChooseKnob");
        int end = s.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = end > at ? s[at..end] : s[at..];

        Assert.Contains("场解不收敛", body);
        Assert.True(body.IndexOf("rk is null", StringComparison.Ordinal) > 0,
            "ChooseKnob 没有单独判 rk is null ⇒ 不收敛与「没变好」还是同一条路，"
          + "报表照样把「几何被抬坏了」写成「这根旋钮没用」。");
    }
}
