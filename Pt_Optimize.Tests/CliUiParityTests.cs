using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **对话里验过的东西，必须落到工程师用的 APP 上**（2026-08-30 用户拍板）。
///
/// 用户原话：「这就是之前说过对话的知识没有落实到工程师用的 APP（**这坑实在太大了**）」。
///
/// ══ 实物有多大
///
/// <code>
///   MeshVerify.Run（网格无关复核 —— 判据可不可信的唯一准绳）
///       Program.cs  2 处   ← 我验证时走的命令行
///       UI          0 处   ← 工程师点得到的地方
///   ⑤ 交付的门也不要求它
/// </code>
/// ⇒ 工程师能拿一个**导航网格上的数**直接出图。实测那个数能差 **1.8 K**
/// （0.6 档：粗网格 7.7 K 看着余量很宽，加密到位 9.5 K，限值 10）——
/// **足以把「过」变成「不过」**。
///
/// ══ 为什么会发生
///
/// 我验证用的是 CLI，交付物是 WinForms APP。两者只在「我记得去接」时才一致，
/// 而我不记得的那些，**看起来一切正常**。这是本项目最怕的错误形态的一个新变种：
/// 不是数错了，是**数到不了要用它的人手上**。
///
/// ══ 规则
///
/// 计算分两类：
/// <code>
///   交付量    铂重、判据值、图        工程师必须拿得到，且看得懂
///   决定依据  敏感度矩阵、单调性扫描  不必知道过程，但必须**享受到效果**
///             网格无关复核
/// </code>
///
/// 决定依据只有三种处置，做砸的两种都有名字：
/// <code>
///   ① 摆成按钮    把决定推给工程师，而他没有做这个决定的依据   （违背傻瓜式）
///   ② 只留命令行  对话里验过的结论到不了他手上               （最大的那个坑）
///   ③ 接进链路    求解器自己跑，界面只留一句「凭什么这么定」   ← 要的是这个
/// </code>
/// 还有第四种：**接进链路但不说话** —— 效果有了，工程师不知道凭什么信，与「安静失败」同族。
///
/// ══ 本类是那张**对帐表**
///
/// ⚠ 它不能自动发现「你又漏接了一个」—— 那需要读心。它能做的是：
///   **逼每一条新增的能力当场表态**「它在界面上的落点是什么」，
///   而空着的落点会红。本仓库对付「会漂开的东西」一贯用名单
///   （`LineResult.Required`、`Flow.Commands`），这里同理。
///
/// **加了新的会产出结果的能力 ⇒ 到这里加一行。** 加不出落点，就是还没做完。
/// </summary>
public class CliUiParityTests
{
    /// <summary>
    /// 一条能力：命令行怎么跑、界面上落在哪、属于哪一类。
    /// </summary>
    /// <param name="Cli">命令行入口（我验证时走的那条）</param>
    /// <param name="Kind">交付量 or 决定依据</param>
    /// <param name="UiLanding">
    /// 界面落点。**不许为空**：
    /// 交付量填按钮名；决定依据填「接进链路：<i>那句人话</i>」；
    /// 确实不入交付的填「工具，不入交付：<i>为什么</i>」。
    /// </param>
    private sealed record Cap(string Cli, string Kind, string UiLanding);

    /// <summary>★ 对帐表。新增能力就来加一行 —— 加不出落点说明还没做完。</summary>
    private static readonly Cap[] Table =
    {
        new("--solve",       "交付量",
            $"按钮「{PtOptimize.UI.Flow.Cmd("core.autoThick").Text}」（同一个 Solver.Solve）—— UiWiring --reconcile 逐克对过帐"),
        new("--verifymesh",  "交付量",
            $"按钮「{PtOptimize.UI.Flow.Cmd("core.verifyMesh").Text}」，且 ⑤ 交付的门 RequireMeshVerified 要求它"),
        new("--monotone",    "决定依据",
            "接进链路：抬旋钮前实测方向，界面印「已实测：抬到上界这条判据确实变好 ⇒ 可以二分求根」"),
        new("--sensmatrix",  "决定依据",
            "接进链路：Solver.ChooseKnob 每轮当场比价，界面印「实测比价：」加各候选的每克铂效率"),
        new("--thermcg",     "决定依据",
            "工具，不入交付：换解法的配对对账，答案不变才准换 —— 界面走同一个 LineRunner，自动继承"),
        new("--cgbench",     "决定依据",
            "工具，不入交付：同上，电位场那一半"),
        new("--seedprobe",   "决定依据",
            "工具，不入交付：证明解与初值无关；界面走同一个 Solver，结论自动继承"),
        new("--glossary",    "交付量",
            "页「使用说明」里的判据对照表（Criteria.Html）"),
    };

    /// <summary>★★ 每一条都要有落点，空着就是「还没落到 APP 上」。</summary>
    [Fact]
    public void 每条能力都说得出界面落点()
    {
        Assert.NotEmpty(Table);
        foreach (var c in Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.UiLanding),
                $"「{c.Cli}」没有界面落点 —— 对话里验过的结论到不了工程师手上");
            Assert.True(c.Kind is "交付量" or "决定依据", $"「{c.Cli}」的类别写错了：{c.Kind}");
        }
    }

    /// <summary>
    /// ★★★ **交付量的落点必须是一个真按钮**，不许写「界面自动继承」这类空话。
    /// 交付量是工程师要拿走的数 —— 拿不到就是没做。
    /// </summary>
    [Fact]
    public void 交付量的落点是真按钮()
    {
        string flow = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "Flow.cs"));
        foreach (var c in Table.Where(x => x.Kind == "交付量"))
        {
            // ⚠ 落点不一定是按钮：判据对照表就是**说明书页上的一段**。
            //   要守的是「工程师够得到」，不是「必须长成按钮」。
            Assert.True(c.UiLanding.Contains("按钮") || c.UiLanding.Contains("页"),
                $"「{c.Cli}」是交付量，落点必须是工程师够得到的按钮或页：{c.UiLanding}");
            // 落点里点名的按钮，Flow 的命令表里要真有（改名会当场断，而不是悄悄漂开）
            int a = c.UiLanding.IndexOf('「');
            int b = c.UiLanding.IndexOf('」');
            if (a >= 0 && b > a)
            {
                string name = c.UiLanding[(a + 1)..b];
                Assert.True(flow.Contains($"\"{name}\"", StringComparison.Ordinal),
                    $"「{c.Cli}」说落在按钮「{name}」上，而 Flow 的命令表里没有这个按钮");
            }
        }
    }

    /// <summary>
    /// ★★★ **决定依据要么接进链路、要么明说不入交付**，不许含糊。
    /// 而「接进链路」的必须能指出**界面上那句人话** —— 接了不说话是第四种做砸法。
    /// </summary>
    [Fact]
    public void 决定依据要么接进链路要么明说不入交付()
    {
        string solver = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        foreach (var c in Table.Where(x => x.Kind == "决定依据"))
        {
            bool wired = c.UiLanding.StartsWith("接进链路：", StringComparison.Ordinal);
            bool notDelivered = c.UiLanding.StartsWith("工具，不入交付：", StringComparison.Ordinal);
            Assert.True(wired || notDelivered,
                $"「{c.Cli}」的落点既不是「接进链路」也不是「工具，不入交付」：{c.UiLanding}");

            // ★ 说了接进链路，就要在求解器里找得到它印出来的那句话
            if (!wired) continue;
            int q = c.UiLanding.IndexOf('「');
            int e = c.UiLanding.LastIndexOf('」');
            Assert.True(q >= 0 && e > q, $"「{c.Cli}」说接进链路了，却没引出界面上那句话");
            string phrase = c.UiLanding[(q + 1)..e];
            // 取前 8 个字去比对：整句里含格式串，逐字比会脆
            string head = phrase.Length > 8 ? phrase[..8] : phrase;
            Assert.True(solver.Contains(head, StringComparison.Ordinal),
                $"「{c.Cli}」说界面会印「{head}…」，而 Solver.cs 里找不到这句 —— "
                + "接了不说话，工程师不知道凭什么信这个数");
        }
    }

    /// <summary>
    /// ★★★ **每一个交付档都要有一条对帐命令** —— 不是「我记得跑」，是名单上写着。
    ///
    /// 对帐本身跑不进单元测试（一次要分钟级的整线解），但**该跑哪几档**可以钉住：
    /// 现役有几档设计记录，就该有几条 `--reconcile`。少一条 = 那一档从没验过
    /// 「界面拿不拿得到命令行验过的数」。
    ///
    /// ⚠ 0.6 是**底档**（管壁压到焊接烧穿下界），余量只有 5.5 %（0.8 有 20 %）——
    ///   越薄的余量越经不起「两条路算的不是同一个零件」。
    /// </summary>
    [Fact]
    public void 每个交付档都列了对帐命令()
    {
        // 现役设计记录（不含已作废的）
        var live = DesignSpec.All.Where(d => d.Invalid.Length == 0).ToArray();
        Assert.NotEmpty(live);

        string doc = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "HANDOVER.md"));
        foreach (var d in live)
        {
            string cmd = $"--reconcile {d.WallMm:0.0}";
            Assert.True(doc.Contains(cmd, StringComparison.Ordinal),
                $"档「{d.Name}」（管壁 {d.WallMm:0.0}）没有对帐命令 —— "
                + $"HANDOVER 里找不到「{cmd}」。那一档从没验过界面拿不拿得到命令行的数。");
        }
    }

    /// <summary>
    /// ★★ 那个**实物**不许再回来：`MeshVerify.Run` 必须在 UI 里被调到。
    /// 这一条是整条规则的由来，单独钉住。
    /// </summary>
    [Fact]
    public void 网格无关复核在界面里真的被调到()
    {
        string ui = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("MeshVerify.Run(", ui);
    }
}
