using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R25（用户 2026-09-09 晚：「跑的过程把①②③④⑤的状态显示出来」）：认阶段的规则钉在**真轨迹的原句**上——
/// 求解器／加密复算改了前缀，这里先红，界面上的状态条才不会悄悄停在错的格子上。
/// </summary>
public class SolveStagesTests
{
    private static SolveStages.Stage Walk(params string[] lines)
    {
        var st = SolveStages.Stage.None;
        foreach (var l in lines) st = SolveStages.Of(l, st);
        return st;
    }

    [Fact]
    public void 真轨迹的原句依序走过五步()
    {
        Assert.Equal(SolveStages.Stage.DesignCurrent, Walk("设计电流（温控 20 °C/h 空管升温 25→1150 °C，管子准静态 I²R=散热+C·Ṫ 全程峰值）：段 979/979 A ⇒ 片 979/1696/979 A（共用片矢量合成）"));
        Assert.Equal(SolveStages.Stage.Corner, Walk("★ 舌片厚按 I/(J·舌宽) 定：片0 — → 1.75 mm（设计电流 979 A ÷ (J 10 × 舌片最窄有效宽 56 mm)）"));
        Assert.Equal(SolveStages.Stage.Corner, Walk("★ 下角因 J=10 截面上抬：片1 0.60 → 1.02 mm（设计电流 1696 A；最紧截面 圆盘 r=26.6 100.3 mm² ⇒ J 16.9 > 10）"));
        Assert.Equal(SolveStages.Stage.Corner, Walk("起点 = **约束盒的下角**（不是种子）：板厚 0.60/1.02/0.60 mm"));
        Assert.Equal(SolveStages.Stage.NavRounds, Walk("── 第一遍：导航网格上定位（导航网格）"));
        Assert.Equal(SolveStages.Stage.NavRounds, Walk("第  2 轮　合计 2603 g　板厚 0.60/1.02/0.60　舌保温 5.00/2.80/8.60"));
        Assert.Equal(SolveStages.Stage.MeshVerify, Walk("加密复算：0.250 mm（第 2 档）…"));
        Assert.Equal(SolveStages.Stage.MeshVerify, Walk("中带确认：把中带 0.250 → 0.125 mm 再算一次，看判据动不动"));
        Assert.Equal(SolveStages.Stage.FineResolve, Walk("── 第二遍：细网格上重新求根（**判据以此为准**）（细网格 0.125 mm）"));
        Assert.Equal(SolveStages.Stage.FineResolve, Walk("精算　第 1 轮　合计 2604 g"));
    }

    [Fact]
    public void 轮数行归当前那一遍_认不出的行不改阶段()
    {
        // 第二遍里的「第 n 轮」不许把阶段拉回 ③
        Assert.Equal(SolveStages.Stage.FineResolve,
            Walk("── 第二遍：细网格上重新求根（**判据以此为准**）（细网格 0.125 mm）", "第  1 轮　合计 2604 g"));
        // 场解内部的进度、终局复核、槽位留痕：不改阶段
        Assert.Equal(SolveStages.Stage.NavRounds,
            Walk("── 第一遍：导航网格上定位（导航网格）", "外层耦合 3/600（ω=0.35）", "     · 终局复核 …", "★ 场定孔位：片0 槽心 -115°"));
        // 还没进任何一遍就看到轮数行 ⇒ 当第一遍
        Assert.Equal(SolveStages.Stage.NavRounds, Walk("第  1 轮　合计 2603 g"));
        Assert.Equal(SolveStages.Stage.None, Walk("准备网格…", ""));
    }

    [Fact]
    public void 测试落档的时间戳不影响认法()
    {
        Assert.Equal(SolveStages.Stage.Corner, Walk("[   0.0 分] ★ 舌片厚按 I/(J·舌宽) 定：片2 — → 1.75 mm"));
        Assert.Equal(SolveStages.Stage.NavRounds, Walk("[  16.3 分] 第  2 轮　合计 2603 g　板厚 0.60/1.02/0.60"));
        Assert.Equal("第  2 轮　合计 2603 g", SolveStages.Detail("[  16.3 分] 第  2 轮　合计 2603 g　板厚 0.60/1.02/0.60　舌保温 5.00/2.80/8.60"));
    }

    /// <summary>真档案上再验一次：细网格复算档从头喂到尾，最后停在 ⑤（它做了细网格重解）或 ④（没做）。</summary>
    [Fact]
    public void 真档案从头喂到尾_停在加密复算或细网格重解()
    {
        string f = Path.Combine(HandoverDoc.Root(), "deliverable", "细网格复算_盘58舌58.txt");
        if (!File.Exists(f)) return;
        var st = Walk(File.ReadAllLines(f));
        Assert.True(st is SolveStages.Stage.MeshVerify or SolveStages.Stage.FineResolve, st.ToString());
        Assert.Equal(5, SolveStages.All.Length);
        Assert.Equal("① 设计电流", SolveStages.Title(SolveStages.Stage.DesignCurrent));
    }
}
