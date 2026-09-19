using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using PtOptimize.Core;
using PtOptimize.UI;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **几小时的任务必须看得出还活着**
/// （2026-08-29 用户：「跑这么长时间，进度条与提示要做好，容易误认死机」）。
///
/// ══ 病在哪（实测，不是设想）
///
/// <see cref="LineRunner"/> **一直在报**（`外层耦合 3/600（ω=0.35）…`、`段 2/3：反算电流…`），
/// 但 <see cref="MeshVerify"/> 与 <see cref="Solver"/> 调它时都传 <c>null</c> ——
/// **报的东西全被扔掉**。于是网格无关复核只在每档开头报一次，档内几小时静默。
///
/// 2026-08-29 实测：0.6 档复核跑到 0.146 mm 那一档时，
/// **我自己也只能靠看进程内存占用猜它还活着**。
///
/// ⚠ 这不是「体验」问题：看不出死活，人就会去 kill 掉一个其实正常的几小时任务，
///   然后重跑，然后再 kill —— 代价是整天。
///
/// ══ 三件事
///
/// ① **限流转发**内层进度（全转会淹掉日志，而淹掉等于没报）；
/// ② 每档**先说这一档有多大、预计多久**（单元数 ∝ 1/h²，每加密一档约 ×4，
///    用**上一档的实测耗时**推下一档，比拍脑袋准，推错了还会自己纠正）；
/// ③ 每档**跑完报实测耗时与单元数**，让下一次的预测有依据。
/// </summary>
public class LongRunProgressTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>★ 内层进度不许再被扔掉 —— <c>LineRunner.Run(.., null, ..)</c> 是病灶本身。</summary>
    [Fact]
    public void 内层进度不再被扔掉()
    {
        foreach (var f in new[] { "MeshVerify.cs", "Solver.cs" })
        {
            string s = Core(f);
            // ★★ 门**不许认变量名**：2026-08-29 中带确认那一处叫 `lcC`，
            //   写死 `lc` 的门就从缝里漏了过去 —— 实测静默 29 分钟没有一行输出。
            //   一条只挡得住自己当初那一行的门，等于没有门。
            var m = System.Text.RegularExpressions.Regex.Match(
                s, @"LineRunner\.Run\(\s*\w+\s*,\s*null\s*[,)]");
            Assert.False(m.Success,
                $"{f} 里还有把内层进度扔掉的调用：{m.Value} —— 几小时的任务会整段静默");
            Assert.Contains("ThrottledProgress", s);
        }
    }

    /// <summary>限流：内层一秒几十条，全转等于没报。</summary>
    [Fact]
    public void 限流器只在间隔之后放行()
    {
        var got = new List<string>();
        var t = new ThrottledProgress(new Sink(got), seconds: 3600);
        for (int i = 0; i < 50; i++) t.Report("x" + i);
        Assert.Single(got);                       // 第一条放行，其余被压
        Assert.Contains("x0", got[0]);
    }

    /// <summary>★ 被压掉几条要**说出来** —— 不说，读的人会低估内层的活跃程度。</summary>
    [Fact]
    public void 压掉的条数要报出来()
    {
        var got = new List<string>();
        var t = new ThrottledProgress(new Sink(got), seconds: 0.1);
        t.Report("a");
        for (int i = 0; i < 5; i++) t.Report("drop" + i);
        System.Threading.Thread.Sleep(150);
        t.Report("b");
        Assert.Equal(2, got.Count);
        Assert.Contains("另有 5 条未显示", got[1]);
    }

    /// <summary>每行都要带「已跑多久」—— 那是判断死活最直接的一个数。</summary>
    [Fact]
    public void 每行都带已跑时长()
    {
        var got = new List<string>();
        new ThrottledProgress(new Sink(got)).Report("hello");
        Assert.Contains("已跑", got[0]);
    }

    /// <summary>耗时说人话：几小时的任务不许只给「7200 s」。</summary>
    [Fact]
    public void 耗时说人话()
    {
        Assert.Equal("30 s", ThrottledProgress.Fmt(TimeSpan.FromSeconds(30)));
        Assert.Equal("15 分", ThrottledProgress.Fmt(TimeSpan.FromMinutes(15)));
        Assert.Equal("2 时 30 分", ThrottledProgress.Fmt(TimeSpan.FromMinutes(150)));
    }

    /// <summary>接收方为 null 时整体是空操作 —— 不许在没人听的时候还去格式化字符串。</summary>
    [Fact]
    public void 没有接收方时是空操作()
    {
        var t = new ThrottledProgress(null);
        t.Report("x");            // 不炸即可
        Assert.True(t.Elapsed >= TimeSpan.Zero);
    }

    /// <summary>
    /// ★ 每档要**先说预计多久**（由上一档实测推），跑完要**报实测**。
    /// 少了预测，人不知道该等 3 分钟还是 3 小时；少了实测，下一次预测就没依据。
    /// </summary>
    [Fact]
    public void 每档先给预计跑完报实测()
    {
        string s = Core("MeshVerify.cs");
        Assert.Contains("本档估 **~", s);
        Assert.Contains("按上两档实测耗时比 ×", s); Assert.Contains("**偏乐观**", s);
        Assert.Contains("完成 ——", s);
        Assert.Contains("ThrottledProgress.Fmt(swOne.Elapsed)", s);
    }

    /// <summary>
    /// ★★ **中间结果要落地** —— 每档的判据值当场就报，不许攒到整趟结束。
    ///
    /// 2026-08-29 实测：0.6 档细阶梯三档全部算完、已跑 4 时 22 分，
    /// 而日志里**一个判据数字都没有**（只有单元数与耗时）—— 被 kill 掉就是四小时全丢。
    /// 「看得出还活着」只解决了一半；另一半是**算出来的东西要立刻落地**。
    /// </summary>
    [Fact]
    public void 每档的判据值当场落地()
    {
        string s = Core("MeshVerify.cs");
        // R48 B（2026-09-14 Opus 5）：有意改动 —— 进度行会上界面，代号换成全名，且 ②″／③ 两列换成热偶读数基准的热侧／冷侧；依据 Pt_Optimize/Core/MeshVerify.cs。
        //   旧 "②′ {a2p:0.000} W" → 新 "管孔净流入 {a2p:0.000} W"；旧 "②″ {a2pp:0.000} K" → 新 "最热铂高出热偶读数 {a2pp:0.000} K"；旧 "③ {a3:0.000} K" → 新 "管根低于热偶读数 {a3:0.000} K"。
        Assert.Contains("管孔净流入 {a2p:0.000} W", s);
        Assert.Contains("最热铂高出热偶读数 {a2pp:0.000} K", s);
        Assert.Contains("管根低于热偶读数 {a3:0.000} K", s);
        Assert.Contains("较上一档：", s);
    }

    /// <summary>
    /// ★ 中带确认曾是整趟里**最贵的单步**（实测 ≥ 3 时 43 分），它必须报进度、必须给下界估时 ——
    /// 此前两样都没有，实测静默 29 分钟，被当成死机。
    ///
    /// ★★ R48（2026-09-13，Opus 5）改写：主循环已改成**整档一起加密**，中带不再固定，
    ///   「判据不再变」本身就覆盖了中带粗糙度这一条 ⇒ 中带确认**不再解场**（只记一句说明）。
    ///   原来那三条断言钉的是它解场时的进度与估时，对象没了就会永远红。
    ///   但**那条安全线不能跟着消失**：真正要守的是「MeshVerify 里任何一次整线场解都带着进度回调」——
    ///   哪天有人把中带确认恢复回来（或加别的长跑步骤），忘了给进度，这里照样要红。
    ///   ⇒ 断言改成钉这件更本质的事，外加钉住「中带确认现在为什么不解场」这句说明还在。
    /// </summary>
    [Fact]
    public void MeshVerify里的长跑场解都带着进度回调()
    {
        string s = Core("MeshVerify.cs");
        // ① 每一处 LineRunner.Run(...) 都不许传 null 进度：静默长跑正是当年被 kill 掉四小时的那个病
        var calls = System.Text.RegularExpressions.Regex.Matches(s, @"LineRunner\.Run\(([^;]*?)\);");
        Assert.True(calls.Count > 0, "MeshVerify 里一处整线场解都找不到 —— 文件被改过？");
        foreach (System.Text.RegularExpressions.Match c in calls)
        {
            string args = c.Groups[1].Value;
            Assert.DoesNotContain("null", args);
            Assert.True(args.Contains("inner") || args.Contains("progress"),
                $"这处场解没带进度回调：LineRunner.Run({args}) —— 长跑必须看得出还活着");
        }
        // ② 每档的耗时与估时照旧要报
        Assert.Contains("本档估", s);
        Assert.Contains("**偏乐观**", s);
        // ③ 中带确认为什么不再解场，要在代码里说清楚（将来改回分区加密时必须恢复）
        Assert.Contains("整档一起加密", s);
        Assert.Contains("若哪天主循环改回", s);
    }

    /// <summary>
    /// ★★★ **状态面板的进度条要说得出「跑到哪了」**（2026-08-30，用户抓图问出来的）。
    ///
    /// `SetRunningNote(note, pct)` 是进度通道，而此前**只有「搜形状」传了 pct**，
    /// 「自动定厚」「核算整线」「复现」都只传文字 ⇒ 面板永远画走马灯。
    /// 而信息一直都有：求解器每轮报「第 N 轮」，耦合器报「外层耦合 n/600」。
    /// **只是没人把它接到进度条上** —— 又一次「造好了没接线」。
    /// </summary>
    [Fact]
    public void 进度条说得出跑到哪了()
    {
        string ui = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        // 翻译器在，且两处进度都带上了百分比与已跑时长
        Assert.Contains("private static int PctOf(string line, int maxRounds)", ui);
        Assert.DoesNotContain("Shared?.SetRunningNote(s); });", ui);
        Assert.Contains("PctOf(s, 40)", ui);
        Assert.Contains("已跑 {clockR.Elapsed.TotalMinutes:0.0} 分", ui);
    }

    /// <summary>
    /// ★ 解析不出轮数时必须回 −1（走马灯）——
    /// **不许拿一根不动的空条冒充「有进度」**（那比走马灯更误导）。
    /// </summary>
    [Theory]
    [InlineData("第  2 轮　合计 3480 g", 40, 5)]
    [InlineData("外层耦合 3/600（ω=0.35）", 40, 0)]
    [InlineData("外层耦合 300/600", 40, 50)]
    [InlineData("正在装配几何…", 40, -1)]
    public void 解析不出轮数就走马灯(string line, int max, int want)
    {
        var m = typeof(PtOptimize.UI.LineDesignPage).GetMethod("PctOf",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
        Assert.Equal(want, (int)m!.Invoke(null, new object[] { line, max })!);
    }

    /// <summary>
    /// ★★★ **验的是「面板真的显示出来」，不是「代码里传了」**（2026-08-30）。
    ///
    /// 前两条门验的是接线（PctOf 在、调用点传了 pct）。但接线对 ≠ 屏幕上看得见 ——
    /// 今天已经栽过一次：复核按钮造好了、方法接好了、门也要求它了，
    /// **就是没挂到工具条上**。所以这一条直接问面板：条子露出来没有、值对不对。
    /// </summary>
    [Fact]
    public void 面板真的把进度画出来()
    {
        var st = new FlowState();
        // ★★ **真的造一个面板**，问它屏幕上写了什么、条子露没露 ——
        //   只验状态通道等于只验「我传了」，而今天已经栽过一次「传了但没挂上屏」。
        var panel = new StagePanel(st);

        // 走 Flow 的通道设进度（不是直接改字段 —— 那样验的是我自己写的赋值）
        st.SetRunning(ChainId.C定尺寸, PtOptimize.UI.Flow.Cmd("core.autoThick").Text);
        st.SetRunningNote("已跑 1.5 分　第  2 轮　合计 3480 g", 5);
        Assert.Equal(5, st.RunningPct);
        Assert.Contains("已跑 1.5 分", st.RunningNote);

        panel.SetStage(StageId.整线核算);   // 内含 Refresh2
        var bar = (System.Windows.Forms.ProgressBar)typeof(StagePanel)
            .GetField("_bar", System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
        var input = (System.Windows.Forms.Label)typeof(StagePanel)
            .GetField("_input", System.Reflection.BindingFlags.NonPublic
                              | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
        Assert.True(bar.Visible, "★ 条子没露出来 —— 传了进度但屏幕上看不见");
        Assert.Equal(System.Windows.Forms.ProgressBarStyle.Continuous, bar.Style);
        Assert.Equal(5, bar.Value);
        Assert.Contains("已跑 1.5 分", input.Text);
        Assert.Contains("第  2 轮", input.Text);
        // ★ 条子要够高才看得见（10 px 那版夹在两行字中间等于没有）
        Assert.True(bar.Height >= 14, $"条子只有 {bar.Height} px（Size={bar.Size}）—— FlowLayoutPanel 会拿首选高度盖掉直接设的 Height，要用 MinimumSize");

        // 说不出百分比时必须回 −1（面板据此画走马灯，而不是一根不动的空条）
        st.SetRunningNote("正在装配几何…", -1);
        Assert.Equal(-1, st.RunningPct);

        // 跑完之后不许残留假进度
        st.SetRunning(null);
        st.SetRunningNote("这条不该进去", 50);
        Assert.Equal(-1, st.RunningPct);
    }

    private sealed class Sink : IProgress<string>
    {
        private readonly List<string> _to;
        public Sink(List<string> to) => _to = to;
        public void Report(string value) => _to.Add(value);
    }

    /// <summary>
    /// ★★ R48（2026-09-13，Opus 5；用户 09-13 定的规矩）：**长跑每一轮都要有「结果」，不能只有「输入」**。
    ///
    /// 用户原话：「以后跑长测试还是要放探针，或者每轮计算口会有结果出来，以利分析是否有问题」。
    /// 求根一趟一两个小时，此前每轮只印九根旋钮与铂重（都是**输入**），
    /// 判据值（**输出**）要等收尾才出来 —— 跑到一半看不出离目标多远、方向对不对，白跑也只能等到最后才知道。
    /// 这与「九根旋钮全印」是同一族教训：那条治的是「看不见的几何」，这条治的是「看不见的进展」。
    /// </summary>
    [Fact]
    public void 求根每一轮都要印判据值与离限值多远()
    {
        string s = Core("Solver.cs");
        Assert.Contains("这一轮的判据", s);
        // 三条主判据都要有，缺一条就等于那一条的进展看不见
        Assert.Contains("LineResult.Key.NetFlux", s);
        Assert.Contains("LineResult.Key.DiscTemp", s);
        Assert.Contains("LineResult.Key.FlangeDip", s);
        // R48 B（2026-09-14 Opus 5）：加严 —— 卡交付的热侧／冷侧换成热偶读数基准，求根每轮必须印这两条（旧判法两条留作对照照印）
        Assert.Contains("LineResult.Key.HotOverTc", s);
        Assert.Contains("LineResult.Key.ColdUnderTc", s);
        Assert.Contains("逐片热偶基准", s);
        // 不只印值，还要印「离限值多远」—— 只有值的话，读的人得自己去翻限值
        Assert.Contains("裕", s);
        Assert.Contains("超", s);
        // ★ R48（2026-09-14，Opus 5）：还要印**取自哪一片／哪一段**，以及**逐片抽热**。
        //   09-14 实测：净流入取最小那片、增量温降取最差那段，而「最差是谁」会随旋钮换人 ——
        //   同一个动作（只削片2 0.2 mm）在一个基线上让增量温降一点没动、在另一个基线上直接顶穿
        //   （5.377→5.377 对 0.586→13.806）。只印判据值，读的人必然拿两点之差推错因果。
        Assert.Contains("取自 ", s);
        Assert.Contains("逐片抽热", s);
        // 这一行必须紧跟在旋钮那一行之后（同一轮的输入与输出要挨着，别隔着几十行）
        // ★ R48（2026-09-14，Opus 5）：量距离前**先把注释行剔掉**。
        //   本门原来量的是源码字符距离，于是「在两行之间补一段说明」会让门变红 ——
        //   它守的是**输出**里两行挨着，跟中间写了多少注释无关。
        //   把注释算进距离，等于惩罚写注释，而这个仓库恰恰要求注释写清楚为什么。
        //   （09-14 我补「最差是哪片会换」那段注释时当场撞上，改的是门，不是删注释。）
        string code = string.Join("\n", s.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        int knobs = code.IndexOf("孔形 {string.Join", StringComparison.Ordinal);
        int crit = code.IndexOf("这一轮的判据", StringComparison.Ordinal);
        Assert.True(knobs > 0 && crit > knobs && crit - knobs < 2000,
            "判据那一行离旋钮那一行太远 —— 同一轮的输入与输出要挨着印");
    }
}
