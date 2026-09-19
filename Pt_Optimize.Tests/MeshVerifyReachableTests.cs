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
        // ⚠ 2026-09-02：进度回调改成回 UI 线程（Progress 取不到同步上下文会退到线程池，
        //   跨线程动 UI 会随机崩）⇒ 形态变成 `m => OnUi(() => _out.AppendText(...))`。
        Assert.Contains("new Progress<string>(m => OnUi(() => _out.AppendText", s);
        // ⚠ 2026-09-02：门已经不拦「没验过」了（用户拍板：结果如何就如何，风险由工程师判断）
        //   ⇒ 这句话原来的后半「出图的门仍然关着」已经变成假话，改成说实话。
        Assert.Contains("**没验过就是没验过**。存档或出图时会把这件事列给你看", s);
    }

    /// <summary>
    /// ★★★★★ **出图的门不再拦「没复核」——改成动作当下提醒**（2026-09-02 用户拍板）。
    ///
    /// 用户原话：「计算结果是如何就如何，超标就显示提醒，最终让工程师判断合格与否
    /// （风险由工程师判断）；若工程师判断可承担风险，工程师就可储存计算结果与出图」。
    ///
    /// ⇒ 门只留「有一个当前参数的、解得出来的解」（没解出来/参数动过了不是风险，是对不上）。
    ///   「过没过、验没验过」在**存档与出图当下**列给工程师看，由他决定。
    ///   ⚠ 拿掉的是**拦**，不是**指路**：Flow.Next 照旧建议先加密复算再出图（下一条钉着）。
    /// </summary>
    [Fact]
    public void 出图的门只拦没解出来或参数动过()
    {
        var g = Flow.Stage(StageId.整线核算).GateToUnlockNext;
        Assert.NotNull(g);
        Assert.True(g!.RequireConverged, "没解出来就无从交付 —— 这条要留");
        Assert.True(g.RequireFresh, "参数动过之后交出去的是上一组参数的东西 —— 这条要留");
        Assert.False(g.RequireAllOk, "★ 判据过没过由工程师判断，不该由门否决");
        Assert.False(g.RequireMeshVerified, "★ 验没验过同上 —— 改成动作当下提醒");
        Assert.Empty(g.RequiredChecks);
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
    /// <summary>
    /// ★ R47 C（2026-09-13）：**.3dm 模式下复核用的 LineCase 走图纸路径** —— VerifyMeshAsync 不许再拿 PageToDesignSpec
    /// 造的解析板去复核（那是用禁用控件的残值、把厚度标度当板厚造的另一个零件）。
    /// 源码门：.3dm 分支经 VerifyFactory3dm 走 MeshVerify.Run(Shape, wall, factory)；工厂里 FlangePlates 为空／FlangeFile3dm 非空是拒答条件。
    /// </summary>
    [Fact]
    public void 图纸模式复核用的LineCase走图纸路径()
    {
        string s = Ui("LineDesignPage.cs");
        int v0 = s.IndexOf("private async Task VerifyMeshAsync()", StringComparison.Ordinal);
        int v1 = s.IndexOf("\n    private ", v0 + 10, StringComparison.Ordinal);
        string body = s[v0..v1];
        Assert.Contains("bool drawing = !_srcAnalytic.Checked;", body);
        Assert.Contains("DesignSpec d = drawing ? null! : PageToDesignSpec();", body);      // 图纸模式不造解析板
        Assert.Contains("VerifyFactory3dm()", body);
        Assert.Contains("MeshVerify.Run(shape3!, wall3, factory3dm!, progress: prog, cancel: _cts.Token)", body);
        // 工厂本身：从 BuildCase() 复制、只换网格四项；造出来的不是图纸路径就拒绝
        int f0 = s.IndexOf("internal (Func<double, double, LineCase>? Factory, string Why) VerifyFactory3dm()", StringComparison.Ordinal);
        Assert.True(f0 >= 0, "找不到 VerifyFactory3dm 的声明");
        int f1 = s.IndexOf("\n    private ", f0, StringComparison.Ordinal);
        string fac = s[f0..f1];
        Assert.Contains("lc0 = BuildCase();", fac);
        Assert.Contains("lc0.FlangePlates.Length > 0 || lc0.FlangeFile3dm.Length == 0", fac);
        Assert.Contains("FlangeAutoSizer.CloneCase(lc0)", fac);
        Assert.DoesNotContain("PageToDesignSpec", fac);
        // 适用性：图纸模式要分析过几何（与 Core 的 RequiredMeshFor(Shape) 拒答同一前提）
        Assert.Contains("\"core.verifyMesh\" => Shared is { Fresh: true, Last: { Ok: true } } && (_srcAnalytic.Checked || _shape is not null)", s);
    }

    /// <summary>
    /// ★ R47 C：细网格重解的 .3dm 分支把 fineMm **真传**给 SolveByLevel —— 经 FineMesh3dm → Options 终局细网格四项；
    /// 此前 RunAsync 的 .3dm 分支 `new FlangeAutoSizer.Options()` 空着，fineMm 根本没用上（在 2 mm 导航网格上再跑一次）。
    /// </summary>
    [Fact]
    public void 细网格重解的图纸分支把口径真传给SolveByLevel()
    {
        string s = Ui("LineDesignPage.cs");
        int r0 = s.IndexOf("private async Task RunAsync(", StringComparison.Ordinal);
        int r1 = s.IndexOf("\n    private ", r0 + 10, StringComparison.Ordinal);
        string run = s[r0..r1];
        Assert.Contains("FineMesh3dm? fine3dm = null", run);
        Assert.Contains("optLv.FinalMeshFineMm = fine3dm.MidMm; optLv.FinalMeshFineRadiusMm = fine3dm.RadiusMm;", run);
        Assert.Contains("optLv.FinalMeshInnerMm = fine3dm.InnerMm; optLv.FinalMeshInnerRadiusMm = fine3dm.InnerRadiusMm;", run);
        Assert.Contains("lc, lvl, optLv, prog, ct, 6, lockMask, mkLevel", run);
        Assert.DoesNotContain("lc, lvl, new FlangeAutoSizer.Options(), prog", run);
        int f0 = s.IndexOf("private async Task FineResolveAsync()", StringComparison.Ordinal);
        int f1 = s.IndexOf("\n    private ", f0 + 10, StringComparison.Ordinal);
        string fine = s[f0..f1];
        Assert.Contains("MeshVerify.RequiredMeshFor(sh, (double)_wall.Value)", fine);
        Assert.Contains("await RunAsync(autoSize: true, fine3dm: new FineMesh3dm(h0, radius, innerH, innerR));", fine);
        // Core 侧：SolveByLevel 入口套口径（搜索各轮 + 全精度复核都在那张网格上）
        string core = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "FlangeAutoSizer.cs"));
        Assert.Contains("baseCase = ApplyFinalMesh(baseCase, opt);", core);
    }

    [Fact]
    public void 本页造的按钮都挂上了()
    {
        string s = Ui("LineDesignPage.cs");
        var made = System.Text.RegularExpressions.Regex.Matches(s, @"(_btn\w+)\s*=\s*Btn\(")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(made);
        foreach (var b in made)
        {
            // ⚠ 2026-09-02 放宽的是**模式**，不是标准：主线工具条拆成两排（_tool / _tool2）
            //   之后，只认 `tool.Items.Add` 会把挂在第二排的按钮误判成「没挂」。
            //   认三种挂法：任一条工具条 Add、Insert、或作为属性交给 MainForm 挂。
            bool mounted =
                System.Text.RegularExpressions.Regex.IsMatch(
                    s, @"_?tool2?\.Items\.(Add|Insert)\([^)]*" + b + @"")
             || System.Text.RegularExpressions.Regex.IsMatch(s, @"=>\s*" + b + @"\s*;");
            Assert.True(mounted,
                $"「{b}」造出来了却没挂 —— Btn() 只造不挂，忘了 tool.Items.Add 它就不在屏幕上");
        }
    }
}
