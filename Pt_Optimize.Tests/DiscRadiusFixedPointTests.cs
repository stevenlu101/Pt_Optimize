using System;
using System.IO;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **盘径是这个零件重量的主因，而它是闭式的**（2026-09-06）。
///
/// 实测链条：
/// <code>
///   盘R60、孔R26 ⇒ 无支撑宽度 34 mm
///              ⇒ 板厚下界 DiscFloorMm = 0.0693 × 34 × 2.0 = 4.709 mm
///              ⇒ 四片全被顶到 4.71（板厚旋钮从没被抬过 ⇒ 用户看到的「4 组法兰等厚」）
///              ⇒ 厚板抽热巨大 ⇒ ③ 法兰增量温降 427 K / 限 10，**差 42 倍**
/// </code>
/// 而 ⑥ 只要求「盘盖得住管孔 + 焊脚」：<c>MinDiscRadiusMm = 孔R + max(板厚, 管壁)</c>。
///
/// ⚠ 盘径与板厚**互相咬着**：缩盘径 → 无支撑宽度小 → 板厚下界降 → 焊脚小 → 需要的盘径更小。
///   所以不能只代一次，要解那个**不动点**。本文件把它解出来，只出数、不改几何 ——
///   盘径是图纸给的形状，改不改是用户的决定。
/// </summary>
public class DiscRadiusFixedPointTests
{
    /// <summary>盘径 R 时的板厚下界（焊接屈曲 vs 烧穿，取大）。</summary>
    private static double FloorAt(double discR, double holeR, DesignInputs b)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = discR;
        d.WallMm = holeR - 25.0;          // HoleRadiusMm => WallMm + 25
        return d.DiscFloorMm(b);
    }

    /// <summary>
    /// ★★★★★ 解不动点：**盘径至少要多大**，以及那时板厚下界是多少。
    /// </summary>
    [Fact]
    public void 盘径不动点()
    {
        var b = new DesignInputs();
        const double wall = 1.0, holeR = 26.0;      // Pt_Heater1：管壁 1.0 ⇒ 孔 R26

        var sb = new StringBuilder();
        sb.AppendLine("═══ Pt_Heater1：盘径的闭式下界（不动点）═══");
        sb.AppendLine($"孔R {holeR}、管壁 {wall}、焊接安全系数 {b.WeldSafetyFactor}、烧穿下界 {b.WeldMinThicknessMm}");
        sb.AppendLine();
        sb.AppendLine("⑥ 要求：盘R ≥ 孔R + max(板厚, 管壁)");
        sb.AppendLine("板厚下界：max(屈曲斜率 × (盘R − 孔R) × 安全系数, 烧穿下界)");
        sb.AppendLine("两者互相咬着 ⇒ 迭代到不动点");
        sb.AppendLine();
        sb.AppendLine("轮\t盘R mm\t板厚下界 mm\t焊脚 mm\t⑥ 要求盘R ≥");

        double R = 60.0;                              // 从图纸给的 R60 出发
        double t = 0, leg = 0, need = 0;
        for (int i = 1; i <= 12; i++)
        {
            t = FloorAt(R, holeR, b);
            leg = Math.Max(t, wall);
            need = GeometryScreen.MinDiscRadiusMm(holeR, t, wall);
            sb.AppendLine($"{i}\t{R:0.000}\t{t:0.000}\t{leg:0.000}\t{need:0.000}");
            if (Math.Abs(need - R) < 1e-6) break;
            R = need;                                 // 收到 ⑥ 的下界上，再算一遍板厚
        }

        sb.AppendLine();
        sb.AppendLine($"⇒ **不动点：盘R = {R:0.000} mm，板厚下界 = {t:0.000} mm**");
        sb.AppendLine();
        sb.AppendLine("对照：");
        sb.AppendLine("构型\t盘R mm\t板厚下界 mm\t比 R60 薄");
        double t60 = FloorAt(60.0, holeR, b);
        sb.AppendLine($"图纸给的\t60.000\t{t60:0.000}\t—");
        sb.AppendLine($"⑥ 的下界\t{R:0.000}\t{t:0.000}\t{t60 / Math.Max(t, 1e-9):0.0} 倍");
        sb.AppendLine();
        sb.AppendLine("⚠ 这是**几何下界**，不是「应该做成这样」——");
        sb.AppendLine("   盘径还要承法兰的散热与装配，缩到下界未必可用。");
        sb.AppendLine("   本表只回答一件事：**R60 不是 ⑥ 要求的，是图纸给的**。");

        // 中间几档，给一条「盘径 → 板厚下界」的曲线，便于挑折中值
        sb.AppendLine();
        sb.AppendLine("盘径 → 板厚下界（挑折中值用）：");
        sb.AppendLine("盘R mm\t板厚下界 mm\t⑥ 过不过");
        foreach (double rr in new[] { 27.0, 30.0, 35.0, 40.0, 45.0, 50.0, 60.0 })
        {
            double tt = FloorAt(rr, holeR, b);
            double nn = GeometryScreen.MinDiscRadiusMm(holeR, tt, wall);
            sb.AppendLine($"{rr:0.0}\t{tt:0.000}\t{(rr >= nn - 1e-9 ? "过" : $"不过（要 ≥ {nn:0.00}）")}");
        }

        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "盘径不动点.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());

        Assert.True(R > holeR, "不动点解出来比管孔还小 —— 迭代发散了");
        Assert.True(R < 60.0, $"不动点 {R:0.00} 不小于图纸的 60 —— 那说明 R60 就是 ⑥ 要求的，我的判断错了");
    }
}
