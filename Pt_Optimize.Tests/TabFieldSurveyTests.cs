using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **先量场，再谈开孔**（2026-09-05 用户定的口径）。
///
/// 用户原话：「我希望根据**温度场与电流场**去决定孔的大小与形状 ——
/// 工程师拍脑袋开孔、跟着放大缩小找答案，**没有依据**」。
///
/// 开孔在物理上同时做两件事：
///   导热截面↓ ⇒ 法兰抽走的热↓ ⇒ **③ 法兰增量温降改善**（它现在超限 42 倍）
///   过流截面↓ ⇒ 该处电流密度↑ ⇒ **②″／熔点变差**（代价）
/// ⇒ 孔该开在**电流密度最低**处：那里挖掉代价最小。
///
/// ⚠ 但这条路成不成立，取决于一件**必须先量**的事：
///   板上到底有没有**够大的低电流区**。若 J 分布本来就均匀，
///   「按等值线开孔」会退化成一堆碎片 —— 那就该趁早知道，而不是做完再推翻。
///
/// 本门**只测不判**：把分布写进 deliverable/舌片电流密度分布.txt，
/// 断言只钉「测量本身没退化」（否则下面的结论是空转）。
/// </summary>
public class TabFieldSurveyTests
{
    /// <summary>
    /// Pt_Heater1.3dm 交接给解析路之后的那一片（实测值，见 F_成对后.txt 第 4 步）：
    /// 盘Ø120／舌长 199.5／舌半宽 40／管壁 1.0／板厚 2.0。
    /// 这正是 ③ = 427 K（超限 42 倍）的那个构型 —— 开孔要救的就是它。
    /// </summary>
    private static FlangePlate Heater1() => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0,
        TabParallel = false,          // 原图是梯形舌
        WeldFilletLegMm = 0,
    };

    [Fact]
    public void 量一次电流密度分布()
    {
        var g = Heater1();
        var f = PlateCurrent2D.Solve(g, totalCurrentA: 1000, rhoOhmM: 1.1e-7, h: 1.0);

        // ── 收集板上所有金属点的 J
        var cells = new System.Collections.Generic.List<(double X, double Z, double J)>();
        for (int i = 0; i < f.Nx; i++)
            for (int j = 0; j < f.Nz; j++)
                if (f.Mask[i, j] && f.Jmag[i, j] > 0)
                    cells.Add((f.X0 + i * f.H, f.Z0 + j * f.H, f.Jmag[i, j]));

        Assert.True(cells.Count > 500, $"只取到 {cells.Count} 个金属格 —— 测量退化了，下面的结论是空转");

        var js = cells.Select(c => c.J).OrderBy(v => v).ToArray();
        double jMin = js[0], jMax = js[^1];
        double P(double q) => js[(int)Math.Clamp(q * (js.Length - 1), 0, js.Length - 1)];
        Assert.True(jMax > jMin * 1.5, $"J 几乎是常数（{jMin:0.000}–{jMax:0.000}）—— 测量可疑");

        // ── 分区统计：舌片（x < 圆盘切点）与圆盘
        double xTan = g.Tangent().X;
        var tab = cells.Where(c => c.X < xTan).ToList();
        var disc = cells.Where(c => c.X >= xTan).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("═══ 舌片/圆盘 电流密度分布（Pt_Heater1 交接后的构型，1000 A）═══");
        sb.AppendLine($"几何：盘Ø{2 * g.DiscRadiusMm:0}　舌长 {-g.TabEndXMm:0.0}　舌端半宽 {g.TabEndHalfWidthMm:0}"
                    + $"　管孔 R{g.HoleRadiusMm:0}　板厚 {g.ThicknessMm:0.00} mm　切点 x = {xTan:0.0}");
        sb.AppendLine();
        void Stat(string name, System.Collections.Generic.List<(double X, double Z, double J)> set)
        {
            if (set.Count == 0) { sb.AppendLine($"{name}：没有格子"); return; }
            var v = set.Select(c => c.J).OrderBy(x => x).ToArray();
            double Q(double q) => v[(int)Math.Clamp(q * (v.Length - 1), 0, v.Length - 1)];
            sb.AppendLine($"{name}　格子 {set.Count}　"
                        + $"最小 {v[0]:0.000}　P10 {Q(0.10):0.000}　中位 {Q(0.50):0.000}　"
                        + $"P90 {Q(0.90):0.000}　最大 {v[^1]:0.000}　A/mm²");
            // ★ 关键问题：低电流区有多大？（低于中位数一半的格子占多少）
            double half = Q(0.50) * 0.5;
            int low = set.Count(c => c.J < half);
            sb.AppendLine($"　　低电流区（< 中位数一半 = {half:0.000}）：{low} 格 "
                        + $"= {100.0 * low / set.Count:0.0} %　⇐ **开孔的余地就在这里**");
        }
        Stat("全板　", cells);
        Stat("舌片　", tab);
        Stat("圆盘　", disc);

        // ── 沿舌轴逐段看：哪一段最均匀、哪一段有余地
        sb.AppendLine();
        sb.AppendLine("沿舌轴分段（每 20 mm 一段，从舌端往圆盘）：");
        sb.AppendLine("x 区间 mm\t格子\t最小 J\t中位 J\t最大 J\t低区占比 %");
        for (double x = g.TabEndXMm; x < xTan; x += 20)
        {
            var seg = cells.Where(c => c.X >= x && c.X < x + 20).ToList();
            if (seg.Count == 0) continue;
            var v = seg.Select(c => c.J).OrderBy(t => t).ToArray();
            double med = v[v.Length / 2];
            int low = seg.Count(c => c.J < med * 0.5);
            sb.AppendLine($"{x:0}–{x + 20:0}\t{seg.Count}\t{v[0]:0.000}\t{med:0.000}\t{v[^1]:0.000}"
                        + $"\t{100.0 * low / seg.Count:0.0}");
        }

        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "舌片电流密度分布.txt"), sb.ToString());
    }
}
