using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **工程师的入口只有两个：UI 输入 与 .3dm 图纸**（2026-09-02 用户重申）。
///
/// 用户原话：「工程师使用的 APP **只有 3DM 与 UI 输入这两个入口**，后续全靠 APP 自己
/// 算出结果。重申：之前已经禁掉『档载入、seeds』的方式」。
///
/// 而这条规则 2026-08-25 就定过（HANDOVER §⑬，用户原话）：
/// 「把种子这种方法**彻底禁掉**，设计记录是用来**校正计算流程**，不应当被乱用。」
///
/// ══ 两处现行违例（2026-09-02 查出，都是我留下的）
///
/// <code>
///   Flow.cs 设计记录页横幅  「「载入」把档灌进页面**当起点**」  ← 「当起点」印在界面上
///   9/3 跑单第一句          「载入设计记录（选 0.8 档）」        ← 我把它写成了工程师的第一步
/// </code>
///
/// ⚠ 设计记录**不是不能用** —— 用来**校正计算流程**是它的正当用途
///   （用户明说「你可以拿来验证计算链路」）。禁的是**把它当设计的起点**。
///   所以本门不禁 `final.load` 这个命令存在，禁的是**它出现在指路上**。
/// </summary>
public class EntryPointsTests
{
    /// <summary>
    /// ★★★ 真正的不变量：**蓝色指示永远不许指向「载入设计记录」**。
    ///
    /// 指路一旦指它，「档载入」就成了工程师被引导去走的一步 —— 那正是被禁的那件事。
    /// 这条比查横幅措辞硬：措辞可以改来改去，而 <see cref="Flow.Next"/> 是链路的唯一来源。
    /// </summary>
    [Fact]
    public void 指路永远不指向载入设计记录()
    {
        string flow = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "Flow.cs"));

        // Flow.Next 的函数体里不许出现这两个命令 —— 它们是「拿现成答案」，不是「算出来」
        int a = flow.IndexOf("public static NextStep? Next(", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 Flow.Next —— 断言失去了对象，不能算通过");
        int b = flow.IndexOf("═══ 查询", a, StringComparison.Ordinal);
        Assert.True(b > a, "找不到 Flow.Next 的结尾标记");
        string body = flow[a..b];

        Assert.DoesNotContain("final.load", body);
        Assert.DoesNotContain("final.reproduce", body);
    }

    /// <summary>
    /// ★★ 走一遍**真的** <see cref="Flow.Next"/>：从开箱默认到全过，每一步指的都不能是
    /// 「载入设计记录」。上一条查的是源码文本，这一条查的是**行为**。
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]   // 还没解过
    [InlineData(true, false, false)]    // 解过、没收敛
    [InlineData(true, true, false)]     // 收敛、判据没全过
    [InlineData(true, true, true)]      // 全过（还没复核）
    public void 任何状态下指路都不指载入(bool solved, bool converged, bool allOk)
    {
        var st = new FlowState();
        if (solved)
        {
            st.Last = new LineResult
            {
                Ok = true,
                Converged = converged,
                Checks = new[]
                {
                    new ConstraintOut
                    {
                        Name = LineResult.Key.NetFlux, Kind = CheckKind.HardSafety,
                        Actual = allOk ? 1.12 : -883.8, Limit = 0.0, Ok = allOk,
                    },
                },
            };
            st.CurrentSnap = st.SolvedSnap = "同一个快照";
        }

        var ns = Flow.Next(st);
        if (ns is null) return;                       // 「正在算」时不指路，合法
        Assert.NotEqual("final.load", ns.CmdId);
        Assert.NotEqual("final.reproduce", ns.CmdId);
    }

    /// <summary>
    /// ★ 设计记录那一页要**说清自己不是起点**。措辞可以变，但这层意思必须在。
    /// </summary>
    [Fact]
    public void 设计记录页自己说清不是起点()
    {
        string banner = Flow.Stage(StageId.设计记录).Banner;
        Assert.NotEqual("", banner);
        Assert.Contains("不是设计的起点", banner);
        // 而且要指出真正的两个入口
        Assert.Contains(".3dm", banner);
        Assert.Contains("UI 参数", banner);
        // 反面：不许再把「载入」说成起点
        Assert.DoesNotContain("当起点", banner);
    }

    /// <summary>
    /// ★ 自证：<see cref="Flow.Next"/> 在这些状态下**真的指得出东西**。
    /// 全返回 null 的话，上面那条 Theory 是空转的。
    /// </summary>
    [Fact]
    public void 自证_指路在这些状态下真的有输出()
    {
        var st = new FlowState();
        var ns = Flow.Next(st);
        Assert.NotNull(ns);
        Assert.Equal("core.runLine", ns!.CmdId);   // 开箱第一步就是「先解一次整线」
    }
}
