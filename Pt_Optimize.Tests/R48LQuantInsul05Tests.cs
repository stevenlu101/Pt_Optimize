using System;
using System.IO;
using System.Linq;
using Xunit;
using PtOptimize.Core;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　门：**求解器的舌保温图纸格 = 包法每层 0.5 mm** —— 2026-09-17，Opus 5。
//
//  ══ 病（实测，不是推论）
//
//  用户 2026-09-14 定的保温是**包法**：一层一层缠，每层 0.5 mm。
//  `InsulationSearch.LayerMm = 0.5` 照此写着（全仓唯一一份层厚）。
//  而 `Solver.SolverOptions.QuantInsulMm` 写的是 **0.1** ⇒ 求解器求出来的舌保温落在
//  现场包不出来的值上。实测（deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt，
//  0.1 格那一跑）：W08 四片解出 5.10／2.30／3.60／10.40 mm —— **没有一片是 0.5 的倍数**；
//  W06（同日同跑）6.40／3.10／4.50／13.10 mm，也一片都不是。
//  ⇒ 交付图上的四个数，现场按 0.5 一层缠，缠不出来。
//  用户 2026-09-17 原话：「求解器默认格子改成 0.5」。
//
//  ══ 这组门钉什么
//
//  ① 默认格子 = 0.5，且**与包法层厚同一个来源**（不是两处各写 0.5 的巧合）；
//  ② 合法值集合 = {裸舌下角 0.30} ∪ {0.5 的正整数倍} —— 下角是「0 层」，不在格子上，也不该被挪上去；
//  ③ 向上对齐图纸格只有**一份写法**（Solver.SnapUpToGridMm），四处调用它，门也从它读；
//  ④ 回收判「本来就在下角」不许放半格 —— 放半格时 0.50 与下角 0.30 只差 0.20 < 0.25，
//     解在 0.50 的片**永远回收不到裸舌**；
//  ⑤ 二分容差必须**比格子细**（0.005 < 0.5）：早停在格子上会多给一层。
//
//  改回 0.1 ⇒ ①②⑤ 三条当场红（①：值不等；②：0.1 的倍数不全落在 0.5 上；⑤：容差与格子的关系仍在，但①先红）。
// ════════════════════════════════════════════════════════════════════════════

public class R48LQuantInsul05GateTests
{
    private static string Src(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>门 ①：默认格子 0.5，且与包法层厚同一个数。</summary>
    [Fact]
    public void 舌保温的图纸格必须等于包法每层()
    {
        var o = new SolverOptions();
        // 从生产的读口读，不在门里抄「哪根旋钮走哪张格子」
        double q = Solver.KnobQuantum(o, Solver.Knob.Insul);
        Assert.Equal(o.QuantInsulMm, q, 12);
        Assert.Equal(InsulationSearch.LayerMm, q, 12);
        Assert.Equal(0.5, q, 12);
        Assert.NotEqual(0.1, q, 12);   // 改回 0.1 ⇒ 这一条红（用户 2026-09-17：格子改成 0.5）
    }

    /// <summary>门 ②：抬升路径写出来的值一定是 0.5 的倍数；下角（裸舌）是**例外**，且必须原样留着。</summary>
    [Fact]
    public void 解出来的舌保温只能是裸舌或者0点5的倍数()
    {
        var o = new SolverOptions();
        double q = Solver.KnobQuantum(o, Solver.Knob.Insul);

        // 抬升那条路：解出的根 → SnapUpToGridMm → 一定落在 0.5 的倍数上（生产路径同一个函数）
        for (double v = o.InsLoMm; v <= o.InsHiMm + 1e-9; v += 0.0137)
        {
            double snapped = Solver.SnapUpToGridMm(v, q);
            Assert.True(snapped >= v - 1e-12, $"向上对齐反而变小了：{v} → {snapped}");
            Assert.True(snapped - v < q + 1e-12, $"向上对齐多跳了一格以上：{v} → {snapped}");
            double layers = snapped / q;
            Assert.True(Math.Abs(layers - Math.Round(layers)) < 1e-9,
                $"{v:0.####} 对齐后是 {snapped:R}，不是每层 {q} mm 的整数倍 ⇒ 现场包不出来");
        }

        // 下角是「0 层」（裸舌），它**不在**格子上 —— 这是有意的，门把这件事钉住，
        // 免得下一个人看到「0.30 不是 0.5 的倍数」就顺手把它挪到 0.5（= 不缠也算缠了一层）。
        double layersLo = o.InsLoMm / q;
        Assert.False(Math.Abs(layersLo - Math.Round(layersLo)) < 1e-9,
            "裸舌下角落在了 0.5 的格子上 —— 若确实要改下角，请连同「0 层 = 裸舌」的口径一起改，别让门默认通过");
        Assert.Equal(0.3, o.InsLoMm, 12);
    }

    /// <summary>门 ③：向上对齐图纸格全仓只有一份写法，四处都调它。</summary>
    [Fact]
    public void 向上对齐图纸格只许有一份写法()
    {
        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        Assert.Contains("public static double SnapUpToGridMm(double v, double q)", s);
        int uses = s.Split("SnapUpToGridMm(").Length - 1;
        Assert.True(uses >= 5, $"SnapUpToGridMm 只出现 {uses} 次（1 处定义 + 至少 4 处调用）—— 有对齐点没接上");
        // 手抄的那一行一处都不许留（格子从 0.1 改到 0.5 时，手抄的地方没有门守得住）
        Assert.DoesNotContain("Math.Ceiling(hi / q - 1e-9) * q", s);
        Assert.DoesNotContain("Math.Ceiling((a + b) * 0.5 / q - 1e-9) * q", s);
        Assert.DoesNotContain("Math.Ceiling(tF / q - 1e-9) * q", s);
        Assert.DoesNotContain("Math.Ceiling(d.DiscFloorMm(baseIn) / opt.QuantThickMm - 1e-9) * opt.QuantThickMm", s);
    }

    /// <summary>门 ④：回收判「本来就在下角」不许放半格 —— 0.5 的格子下那等于把最低一档焊死。</summary>
    [Fact]
    public void 回收判下角不许放半格()
    {
        var o = new SolverOptions();
        double q = Solver.KnobQuantum(o, Solver.Knob.Insul);
        double lowestOnGrid = Solver.SnapUpToGridMm(o.InsLoMm, q);   // 0.5：格子上最低的那一档

        // 先把「为什么不能放半格」算给自己看：0.50 − 0.30 = 0.20 < 半格 0.25
        Assert.True(lowestOnGrid - o.InsLoMm < q * 0.5,
            "格子上最低的一档与下角之间已经超过半格 ⇒ 这道门失去了守护对象，先核对下角与格子");

        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        int i = s.IndexOf("private static void TightenOnJudgeMesh", StringComparison.Ordinal);
        Assert.True(i > 0, "找不到回收那一步");
        string body = s.Substring(i, Math.Min(6000, s.Length - i));
        Assert.Contains("if (cur <= lo + 1e-9) continue;", body);
        Assert.DoesNotContain("if (cur <= lo + q * 0.5) continue;", body);
    }

    /// <summary>门 ⑤：二分容差必须比图纸格细 —— 早停在格子上会多给一层（不花铂，但不是最小可行点）。</summary>
    [Fact]
    public void 二分容差必须比图纸格细()
    {
        var o = new SolverOptions();
        Assert.True(o.BisectTolMm < Solver.KnobQuantum(o, Solver.Knob.Insul),
            "二分容差不比舌保温的图纸格细 ⇒ 根可能停在格子上方一格内，对齐后多给一层");
        Assert.True(o.BisectTolMm < Solver.KnobQuantum(o, Solver.Knob.Thick),
            "二分容差不比板厚的图纸格细");
        // 具体的「多给一层」长什么样：真根 2.05、早停 hi = 2.55 ⇒ 3.0 而不是 2.5
        Assert.Equal(2.5, Solver.SnapUpToGridMm(2.05, 0.5), 12);
        Assert.Equal(3.0, Solver.SnapUpToGridMm(2.55, 0.5), 12);
    }

    /// <summary>
    /// 门 ⑦：**「解出来是几层」要落到工程师点得到的地方**（记忆：对话的知识必须落到 APP 上）。
    /// 折算只有一份写法（InsulationSearch.LayersText），界面与安装报告都调它。
    /// </summary>
    [Fact]
    public void 解出来是几层必须印在界面与安装报告上()
    {
        // 折算本身
        Assert.Equal("1 层", InsulationSearch.LayersText(0.5));
        Assert.Equal("11 层", InsulationSearch.LayersText(5.5));
        Assert.Equal("裸舌 0 层", InsulationSearch.LayersText(0.3));       // 下角 = 什么都不缠
        Assert.Equal("**不在层上**", InsulationSearch.LayersText(5.1));    // 0.1 格那一跑的值，照实说
        Assert.Equal("11 层/5 层/7 层/21 层", InsulationSearch.LayersText(new[] { 5.5, 2.5, 3.5, 10.5 }));

        // 接上了没有：安装报告（现场按它缠）与界面输出框（工程师看的那两处）
        string install = Src("Pt_Optimize", "Core", "InstallReport.cs");
        Assert.Contains("InsulationSearch.LayersText(d.TabInsulMm)", install);
        Assert.Contains("按包法每层", install);
        string ui = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        int uses = ui.Split("InsulationSearch.LayersText(").Length - 1;
        Assert.True(uses >= 2, $"界面上只有 {uses} 处把舌保温折成层数 —— 两处「用了什么」的报告都要有");
        // 折算不许在别处再抄一份层厚
        Assert.DoesNotContain("/ 0.5) 层", ui);
    }

    /// <summary>门 ⑥：改回 0.1 会怎样 —— 把「0.1 格的解落不到包法层上」这件事当场量出来（注入证据，不是说法）。</summary>
    [Fact]
    public void 零点一格的解落不到包法层上()
    {
        var o = new SolverOptions();
        double layer = InsulationSearch.LayerMm;
        // 上一跑（0.1 格）W08／W06 解出来的八个值，出处：
        // deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt（A 导航档终值）
        // deliverable/R48_L_端到端_细网格_W06_本次开跑于2026-09-17_093015.txt（B 细网格档终值）
        double[] old01 = { 5.1, 2.3, 3.6, 10.4, 6.4, 3.1, 4.5, 13.1 };
        int offGrid = old01.Count(v => Math.Abs(v / layer - Math.Round(v / layer)) > 1e-9);
        // 八个里 **七个**包不出来；唯一落在层上的是 W06 片2 的 4.5（撞上的，不是求出来的 ——
        // 第一版门在这里写了 8，被自己这一条当场打红，改成实测的 7）。
        Assert.Equal(7, offGrid);
        Assert.True(Math.Abs(4.5 / layer - Math.Round(4.5 / layer)) < 1e-9, "4.5 本来就在层上");

        // 换成本轮的格子之后，同样这八个值向上对齐都落在层上
        double q = Solver.KnobQuantum(o, Solver.Knob.Insul);
        foreach (double v in old01)
        {
            double s = Solver.SnapUpToGridMm(v, q);
            Assert.True(Math.Abs(s / layer - Math.Round(s / layer)) < 1e-9, $"{v} → {s} 仍不在层上");
        }
    }
}
