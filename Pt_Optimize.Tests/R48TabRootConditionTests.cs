using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// R48 取数（2026-09-14，Opus 5 写）：**舌根那个热点的边界条件**。
///
/// 用户 09-14 问：「离孔 5～7 mm、比管温高 16 K 的材料该不该受『圆盘区最高温 ≤ 5 K』这条线管，
/// 你得说明这计算的边界条件（J 多大，保温多厚），我才能回答。」
///
/// 本仪器只报**那一点的边界条件**，一个判定都不做：
/// 位置、厚度、是否包在保温里、保温多厚、当地电流密度、绝对温度、以及这些数出自哪一次跑。
///
/// ⚠ 只建几何、不解场（几何量与保温分界都是闭式的），毫秒级；
///   温度与电流密度那几列来自已经跑过的场解，出处在输出里写明。
/// </summary>
public class R48TabRootConditionTests
{
    private readonly ITestOutputHelper _out;
    public R48TabRootConditionTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 报舌根热点的边界条件()
    {
        string log = Path.Combine(SolutionDir(), "deliverable", "R48_舌根热点的边界条件_2026-09-14.txt");
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); try { File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false)); } catch { } }

        Say($"R48：舌根热点的边界条件（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("设计 = 管壁 0.8，2026-09-13 求解器解出的那一点（板厚 0.73/1.26/1.26/0.73，舌保温 4.60/2.10/2.90/7.50）。");
        Say("几何与保温分界为闭式，本处现算；温度与当地电流密度来自 deliverable/R48_圆盘区判据覆盖_2026-09-13.txt");
        Say("（那一次跑在**导航网格 2.0 mm** 上 —— 粗网格，绝对值仅供定量级，不作判定依据）。");
        Say("");

        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        double floor = d.DiscFloorMm(p);

        // 那一次场解量到的舌片区峰（位置、当地 J、绝对温度）
        double[] peakR = { 33.0, 33.0, 31.0, 31.0 };
        double[] peakX = { -33.0, -33.0, -31.0, -31.0 };
        double[] peakJ = { 12.52, 11.85, 11.56, 11.25 };
        double[] peakT = { 1158.37, 1120.63, 1066.83, 1046.45 };
        double[] rootT = { 1141.8, 1107.8, 1057.8, 1038.0 };

        Say($"管内径 {lc.TubeIdMm:0.0} mm　管壁 {lc.WallMm:0.00} mm　⇒ 孔半径 {holeR:0.0} mm");
        Say($"设计电流密度 J = {d.JDesignAPerMm2:0.#} A/mm²（工程师在 ① 输入设定）；截面 J 的计算极限 = J+1 = {d.JDesignAPerMm2 + 1:0.#}");
        Say("");
        Say($"{"片",4}{"盘半径",8}{"舌半宽",8}{"舌尖x",8}{"保温分界x",11}{"舌片厚",8}{"舌保温",8}"
          + $"{"峰位r",8}{"峰位x",8}{"离孔",7}{"包在保温里",12}{"当地J",8}{"峰温C",9}{"管根C",9}{"高出管温",9}");
        for (int j = 0; j < 4; j++)
        {
            var g = d.Plate(j, floor);
            double insulX = g.InsulBoundaryXResolved;
            bool inside = peakX[j] < insulX;                  // 单舌片：x < 分界 = 包在保温里
            Say($"{j,4}{g.DiscRadiusMm,8:0.0}{g.TabEndHalfWidthMm,8:0.0}{g.TabTipXMm,8:0}"
              + $"{insulX,11:0.0}{d.TongueThickMm[j],8:0.00}{d.TabInsulMm[j],8:0.00}"
              + $"{peakR[j],8:0.0}{peakX[j],8:0.0}{peakR[j] - holeR,7:0.0}{(inside ? "是" : "**否**"),12}"
              + $"{peakJ[j],8:0.00}{peakT[j],9:0.0}{rootT[j],9:0.0}{peakT[j] - rootT[j],9:+0.00}");
        }
        Say("");
        Say("读法与注意：");
        Say("  · 「当地 J」是场解逐格的电流密度，**不是**判据「法兰截面 J」那个量（后者是整段截面的 I/A）。");
        Say($"    本设计的截面 J 判据实测 9.98 ≤ {d.JDesignAPerMm2 + 1:0.#}（取自舌片 x=−100），**没有超**；");
        Say("    舌根这一格的当地 J 高，是**拐角处电流拥挤**，两者不可直接比较。");
        Say("  · 「离孔」= 峰位 r − 孔半径，即峰点到管外壁的径向距离。");
        Say("  · 峰位 z ≈ ±1 mm（在舌片中线上），厚度 = 舌片厚（闭式 I/(J·舌宽)，不是旋钮）。");
        Say("  · 绝对温度出自导航网格 2.0 mm 那一次跑；判决网格（0.5 mm）上的值未量。");

        Assert.Contains("舌根", sb.ToString());
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
