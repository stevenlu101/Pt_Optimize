using System;
using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **.3dm 模式的死胡同现在有出口了。**
///
/// ★ 背景（用户 2026-08-25）：「3DM 只读几何数据（画网格的依据），为何不能带入计算？」
///   能。反推一直都在做（PlateShapeAnalyzer 印的 Ø120／舌长 199.5／半宽 40 就是），
///   只是从来没交给解析路 —— 而解析路是**唯一能改形状**的那条。
///   在此之前指路只会说「本程序能调的只有厚度，改完回 ③ 重解」，
///   而实测盘Ø120 即便板厚顶到工艺下界，②″ 与 ③ 仍差一个数量级 ⇒ **调厚度救不了**。
///
/// ⚠ 本文件同时钉住一条**教训**：改指路时我先改了 `SizerNoLevels` 那条分支，
///   但 .3dm 模式下 ⑤⑥ 恒「无法判定」⇒ `geomBlocked` 恒真 ⇒ **上一条分支先返回**，
///   我改的那条根本轮不到。「跑通了 ≠ 跑到了那条路径」—— 所以这里验的是
///   **真正会走到的那条**。
/// </summary>
public class FlowDeadEndTests
{
    /// <summary>
    /// 造一个「⑤ 无法判定」的判据表 —— 那正是 .3dm 模式的常态。
    ///
    /// ⚠ 底表必须**完整**（2026-08-24 的教训）：真解出来的表必定含 LineResult.Required
    ///   的全部条目，只造一条的夹具在真机上不可能出现，而且会让「判据缺席」机制当场红。
    /// ⚠ 还要 Converged + RampChecked + 新鲜（SolvedSnap == CurrentSnap），
    ///   否则 Flow.Next 在更早的分支就返回「回 ③ 重解」—— 我第一版正是栽在这里。
    /// </summary>
    private static FlowState Dot3dmState()
    {
        var snap = new object();
        ConstraintOut C(string name, bool ok, CheckKind k, bool undet = false) => new()
        {
            Name = name, Ok = ok, Kind = k, Undetermined = undet,
            Actual = undet ? double.NaN : (ok ? 1 : 9), Limit = 5,
        };
        var bad = C(LineResult.Key.FreeTab, false, CheckKind.HardSafety, undet: true);
        var r = new LineResult
        {
            Ok = true,
            Converged = true,
            RampChecked = true,
            Checks = LineResult.RequiredFor(emptyTube: false)   // K 路（2026-09-15 Opus 5）：必备名单按工况取；界面只解带玻璃稳态
                .Where(q => !bad.Name.StartsWith(q.Prefix, StringComparison.Ordinal))
                .Select(q => C(q.Prefix + " 底表", true, q.Kind))
                .Concat(new[] { bad }).ToArray(),
        };
        return new FlowState { Last = r, SolvedSnap = snap, CurrentSnap = snap };
    }


    /// <summary>.3dm 模式：搜形状用不了；已分析过 ⇒ 图纸几何→参数 可用。</summary>
    private static bool Applicable3dmAnalyzed(string id) => id switch
    {
        "shape.search" => false,
        "geom.toanalytic" => true,
        _ => true,
    };

    /// <summary>.3dm 模式但**还没分析** ⇒ 两个都不可用。</summary>
    private static bool Applicable3dmRaw(string id) => id switch
    {
        "shape.search" => false,
        "geom.toanalytic" => false,
        _ => true,
    };

    [Fact]
    public void 自证_这个状态确实走进几何判不了那条分支()
    {
        // 没有这一条，下面两条可能是在验别的分支 —— 而我恰恰刚栽在这上面。
        var n = Flow.Next(Dot3dmState(), Applicable3dmRaw);
        Assert.NotNull(n);
        Assert.Contains("判不了", n!.Why);
    }

    [Fact]
    public void 分析过之后_指向图纸几何转参数_而不是让人只调厚度()
    {
        var n = Flow.Next(Dot3dmState(), Applicable3dmAnalyzed);
        Assert.NotNull(n);
        Assert.Equal("geom.toanalytic", n!.CmdId);
        // 而且要说清为什么 —— 卡的是形状不是厚度
        Assert.Contains("形状", n.Why);
    }

    [Fact]
    public void 还没分析时_先指分析_绝不指一个灰按钮()
    {
        // 指向一个点不动的按钮，比不给指引更坏 —— 本项目为此栽过不止一次。
        var n = Flow.Next(Dot3dmState(), Applicable3dmRaw);
        Assert.NotNull(n);
        Assert.Equal("geom.analyze", n!.CmdId);
        Assert.True(Applicable3dmRaw(n.CmdId), "指路给出的命令必须是当下可用的");
    }

    [Fact]
    public void 解析模式不受影响_仍然指搜形状()
    {
        // 自证的另一半：若上面那条无条件返回 geom.toanalytic，本条会红。
        // ⚠ 解析模式下「图纸几何 → 参数」**不适用**（CommandApplicable：要 .3dm 模式且分析过）——
        //   夹具照实给，否则等于造了一个真机上不存在的状态（2026-09-08 R19 后这一位有分支读它）。
        var n = Flow.Next(Dot3dmState(), id => id != "geom.toanalytic");
        Assert.NotNull(n);
        Assert.Equal("shape.search", n!.CmdId);
    }

    [Fact]
    public void 新命令登记在册且只属于一个阶段()
    {
        var c = Flow.Cmd("geom.toanalytic");
        Assert.False(string.IsNullOrWhiteSpace(c.Text));
        Assert.Contains("解析", c.Tip);        // 说明里必须点明它是交给解析路
    }
}
