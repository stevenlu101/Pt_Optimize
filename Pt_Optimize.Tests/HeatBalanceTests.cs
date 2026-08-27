using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **管↔法兰热收支对账** —— 一条此前根本不存在的守恒律。
///
/// ★ 病灶（2026-08-28 第一性原理通查，主进程逐行复验 + 实测确认）：
///   <c>QFromTubeW</c> 是**一片**法兰经**整圈**管孔抽走的**总量**
///   （ShellThermal 对所有孔单元累加）。
///   而外层耦合写的是 <c>targetLR[i] = (Flanges[i].Q, Flanges[i+1].Q)</c> ——
///   把每片的**全额**挂到段 i 的两端。于是**内部共用片**同时是
///   「段 j−1 的右端」与「段 j 的左端」，**全额被扣两次**：
///   管子失去 Q₀ + 2ΣQ内 + Q_n，而法兰只收到 ΣQ。
///
///   ⚠ 与 2026-08-15 那次修复（同一段两端是两片**不同**法兰，不该取平均）
///     **不是同一件事** —— 那次修的是段内，这次是段间共用。
///
///   ⚠ 此前**没有任何一处在对账**，所以谁也没发现。
///
/// **实测**（--selfcheck A 段，四个档全部非零）：
/// <code>
///   管壁 0.8 · 留余量   热收支 **+2.66 W**
///   管壁 0.6 · 底档     热收支 **+3.74 W**
///   已作废 0.8 舌90     热收支 +3.47 W
///   已作废 0.6 舌90     热收支 +3.52 W
/// </code>
/// 结构推断给的是「差额 = Σ内部共用片的 Q」≈ 2.6 W（0.8 档），实测 2.66 W，**吻合**。
///
/// ⚠ 对判据③ 的**影响方向尚未确定**：多抽热应使管根温降偏大（保守侧），
///   但耦合是非线性的 —— 要修完重跑才能定。**本条目前只做参考量，不卡交付**：
///   改判定会动所有历史结果，先量出来再谈改不改。
/// </summary>
public class HeatBalanceTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    [Fact]
    public void 判据表里有这一条()
    {
        Assert.Equal("· 管↔法兰热收支", LineResult.Key.HeatBalance);
    }

    [Fact]
    public void 它是参考量_不参与AllOk()
    {
        // 改成硬判据会让四个档当场全红（实测都非零）⇒ 必须先是参考量。
        // 这一条钉住「现状是有意的」，将来谁要升级它，得先来改这个断言。
        string s = Core("LineRunner.cs");
        Assert.Contains("Name = LineResult.Key.HeatBalance, Unit = \"W\", Kind = CheckKind.Reference", s);
        // Required 是「缺席即翻 AllOk」的那张表 —— 参考量不该在里面
        Assert.DoesNotContain("(Key.HeatBalance", s);
    }

    [Fact]
    public void 残差必须真的被算出来_不是写死的零()
    {
        string s = Core("LineRunner.cs");
        // 两侧都要有：段端实际扣掉的（DrawAppliedW）与各片实收的（QFromTubeW 求和）
        Assert.Contains("res.DrawAppliedW += drawLR[i].L + drawLR[i].R", s);
        Assert.Contains("flanges.Sum(f2 => f2.QFromTubeW)", s);
    }

    [Fact]
    public void 自检的汇总行必须把它打出来()
    {
        // 「新判据不打出来就等于没进表」—— 热稳定当初就是这么攒起量级的。
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Program.cs"));
        Assert.Contains("热收支 {HbOf(r)}", s);
        Assert.Contains("LineResult.Key.HeatBalance", s);
    }
}
