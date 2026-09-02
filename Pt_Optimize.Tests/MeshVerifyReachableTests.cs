using System.IO;
using System.Linq;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **网格无关复核必须在界面上够得着，而且出图前必须过它**（2026-08-30）。
///
/// ══ 病灶：对话里跑得漂亮，工程师点按钮却碰不到
///
/// 用户原话：「别让『对话开发中你让 APP 跑的结果很漂亮，结果工程师操作 APP 却没触发步骤』」。
/// 查下去实物就在这里：
///
/// <code>
///   MeshVerify.Run（判据可不可信的唯一准绳）
///       Program.cs   2 处   ← 命令行 --verifymesh
///       UI           0 处   ← 界面上根本调不到
///
///   Flow ⑤ 交付的门：RequireConverged / RequireAllOk / RequireFresh
///       ← 没有任何一条要求「判据经过网格无关复核」
/// </code>
///
/// ⇒ 工程师可以：点核算整线 → 收敛✓全过✓新鲜✓ → ⑤ 解锁 → 出图。
///   而那个「全过」是在**导航网格（2 mm）**上判的。
///
/// ══ 那个差有多大（实测，0.6 档同一个设计）
///
/// <code>
///   导航网格 2 mm      法兰增量温降 8.6 K
///   加密 0.5 mm        7.7 K      ← 看着余量很宽，**这是假收敛**
///   加密 0.125 mm      **9.5 K**   限值 10
/// </code>
///
/// 界面此前确实印了「未经复核、不可直接交付」，却**没有给出做复核的按钮** ——
/// 告诉人不合格却不给路，那条链在界面上是断的。
/// </summary>
public class MeshVerifyReachableTests
{
    private static string Ui(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", f));

    /// <summary>★★★ 界面必须**真的调** MeshVerify.Run —— 不是只印一句「未经复核」。</summary>
    [Fact]
    public void 界面真的调得到网格无关复核()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("MeshVerify.Run(d, _base, progress: prog, cancel: _cts.Token)", s);
        Assert.Contains("private async Task VerifyMeshAsync()", s);
        // 有按钮，且按钮接到这个方法上（造好了没接线是本仓栽过多次的形状）
        // ⚠ 名字从 Flow 读，不在这里再抄一份 —— 2026-09-02 改名「◆ 加密复算（算到数不再变）」时，
        //   全仓有 5 条测试因为各自抄了一份名字而同时红。名字只该有一个来源。
        string btn = Flow.Cmd("core.verifyMesh").Text;
        Assert.Contains("_btnVerify = Btn(" + (char)34 + btn + (char)34
                      + ", (_, _) => _ = VerifyMeshAsync());", s);
    }

    /// <summary>
    /// ★★ 长任务的三件套：**进度、可取消、取消了不算过**。
    /// 复核要跑 10–40 分钟，没有这三样它在界面上就是不可用的。
    /// </summary>
    /// <summary>
    /// ★★★★★ **复核算出来的那组判据必须被用上**（2026-09-02，`--follow 0.8` 走查抓到）。
    ///
    /// 上一条只验「按钮点得到」。点得到之后呢 —— <c>MeshVerify.Result.Line</c> 那个字段
    /// 自己的说明写着「**网格无关的**那一次解 —— 判据以它为准，不是以导航网格那次为准」，
    /// 而界面**从来没碰过它**（只读 Converged / Verdict / MidBandConfirm 三样）。
    ///
    /// ⇒ 复核跑完、门开了、可以出图，而判据表里躺着的还是导航网格（2 mm）那组数：
    /// <code>
    ///   0.8 档   法兰增量温降   表上 4.72 K（余量 53 %）   实际 7.95 K（余量 21 %）
    ///            管孔净流入     表上 1.12 W               实际 2.63 W
    /// </code>
    /// **算对了却没送到人手上** —— 本项目最怕那一族的极端形态。
    ///
    /// 走查器那条「这一步起作用了吗」的指纹报了 ✗（判据表逐字未变）。
    /// 我原以为是它误报，一查发现**反了**：它抓到的是真的。
    /// </summary>
    [Fact]
    public void 复核解出来的判据被接管()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("res.Line is { Ok: true }", s);
        Assert.Contains("_last = res.Line;", s);
        // 只在收敛时接管 —— 没收敛就是没验过，那组数不该顶替任何东西
        Assert.Contains("res.Converged && res.Line", s);
    }

    /// <summary>
    /// ★★ **没有解的时候，「加密复算」必须是灰的**（2026-09-02 抓图抓到）。
    ///
    /// 实况：开箱进 ③ 页，右上角写着「结果：还没解过」，而这个按钮是**黑的、点得下去** ——
    /// 点了只弹一句「先在本页点核算整线」。而 <c>CommandApplicable</c> 自己的说明写着：
    /// 「能点但点了只弹一句『请先切到…』」正是用户最初抱怨的那个形状。
    ///
    /// ⚠ 适用性条件与 <c>VerifyMeshAsync</c> 的前置必须**同一套**：
    /// 两处不一致的话，要么灰着却能跑、要么亮着却拒绝 —— 都在骗人。
    /// </summary>
    [Fact]
    public void 没有当前参数的解时加密复算是灰的()
    {
        string s = Ui("LineDesignPage.cs");
        // 适用性表里真的有这一条（落到 `_ => true` 就等于永远可点）
        Assert.Contains("\"core.verifyMesh\" => Shared is { Fresh: true, Last: { Ok: true } }", s);
        // 灰掉要说清为什么 —— 灰着不解释就是哑谜
        Assert.Contains("_btnVerify.ToolTipText", s);
        Assert.Contains("还没解过 —— 先点「核算整线」", s);
        // 与方法自己的前置对齐（VerifyMeshAsync 用 _last / _solvedSnap 判同一件事）
        Assert.Contains("_last is not { Ok: true } || !Equals(_solvedSnap, CurrentSnap())", s);
    }

    [Fact]
    public void 复核可取消且取消不算过()
    {
        string s = Ui("LineDesignPage.cs");
        Assert.Contains("if (_cts is not null) { _cts.Cancel(); return; }", s);
        Assert.Contains("new Progress<string>(m => _out.AppendText", s);
        Assert.Contains("**没验过就是没验过**，出图的门仍然关着", s);
    }

    /// <summary>
    /// ★★★ 门必须**真的要求**它。少了这一条，上面一切都只是多了个按钮，
    /// 而工程师照样能绕过去出图。
    /// </summary>
    [Fact]
    public void 出图的门要求复核过()
    {
        string s = Ui("Flow.cs");
        Assert.Contains("bool RequireMeshVerified = false);", s);
        Assert.Contains("RequireMeshVerified: true),", s);
        Assert.Contains("if (gate.RequireMeshVerified && !(st.MeshVerified && st.VerifiedFresh))", s);
    }

    /// <summary>
    /// ★★ **复核之后改参数 ⇒ 复核作废**。挂着上一次的复核结论是**假绿灯**，
    /// 比没复核更坏 —— 它会让人以为验过了。
    /// </summary>
    [Fact]
    public void 改了参数复核就作废()
    {
        var st = new FlowState();
        var snap = new object();
        st.CurrentSnap = snap;
        st.MeshVerified = true; st.VerifiedSnap = snap;
        Assert.True(st.VerifiedFresh);

        st.CurrentSnap = new object();          // 动了一下参数
        Assert.False(st.VerifiedFresh);

        // 没复核过时也不能算「新鲜」
        var st2 = new FlowState { CurrentSnap = snap };
        Assert.False(st2.VerifiedFresh);
    }

    /// <summary>
    /// ★★ 指路要**先指复核，再指出图** —— 顺序错了等于没这道关。
    /// </summary>
    [Fact]
    public void 指路先复核后出图()
    {
        string s = Ui("Flow.cs");
        int v = s.IndexOf("return new(\"core.verifyMesh\",", System.StringComparison.Ordinal);
        int e = s.IndexOf("return new(\"export.page3dm\", \"判据全过、是当前参数的解、而且已经加密复算",
                          System.StringComparison.Ordinal);
        Assert.True(v > 0 && e > v, "复核那一支必须排在出图之前");
    }

    /// <summary>
    /// ★ 拦人的时候要**说清代价与理由**，不能只说「没解锁」。
    /// 本项目既定诉求：不看说明书也能用 —— 那就得说出「点哪个、要多久、不做会怎样」。
    /// </summary>
    [Fact]
    public void 拦人时说得出代价与理由()
    {
        string s = Ui("Flow.cs");
        Assert.Contains("10～40 分钟，可取消", s);          // 代价
        Assert.Contains("差 1.8 K", s);                    // 不做会怎样（实测）
        Assert.Contains("足以把「过」变成「不过」", s);
    }

    /// <summary>
    /// ★★★ **按钮要真的挂在工具条上**（2026-08-30 差点栽在这里）。
    ///
    /// `Btn()` 这个工厂**只造不挂**（它自己的注释就写着这条警告）。复核按钮当时：
    /// 造好了 ✓　方法接好了 ✓　门也要求它了 ✓　**就是少了 `tool.Items.Add(_btnVerify)`**
    /// ⇒ 它不在屏幕上，工程师点不到，而门却拦着不让出图 —— **死路**。
    ///
    /// 「造好了没接线」是本项目头号敌人，这是它的第 N 次。
    /// ⚠ 本条不只盯复核这一个：凡是本页 `Btn(...)` 造出来的 ToolStripButton，
    ///   都必须能在源码里找到对应的挂载（`tool.Items.Add` 或 `internal ... => _btnX` 供 MainForm 挂）。
    /// </summary>
    [Fact]
    public void 本页造的按钮都挂上了()
    {
        string s = Ui("LineDesignPage.cs");
        var made = System.Text.RegularExpressions.Regex.Matches(s, @"(_btn\w+)\s*=\s*Btn\(")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(made);
        foreach (var b in made)
        {
            bool mounted = s.Contains($"tool.Items.Add({b});")
                        || System.Text.RegularExpressions.Regex.IsMatch(
                               s, @"=>\s*" + b + @"\s*;");   // 交给 MainForm 挂
            Assert.True(mounted,
                $"「{b}」造出来了却没挂 —— Btn() 只造不挂，忘了 tool.Items.Add 它就不在屏幕上");
        }
    }
}
