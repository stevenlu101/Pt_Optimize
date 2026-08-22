using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.ComponentModel;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;

/// <summary>
/// 界面接线测试：不点屏幕，把控件跑起来用反射验事件链与状态机。
/// GUI「编译过」不代表「接对了」—— 已经靠它抓到两条只在运行时现形的错。
/// 退出码 = 失败项数。
/// </summary>
class UiWiringTests {
    static FieldInfo Fi(object o, string n) {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance) is { } f) return f;
        throw new MissingFieldException(o.GetType().Name + "." + n);
    }
    static object? F(object o, string n) => Fi(o, n).GetValue(o);
    static void Set(object o, string n, object? v) => Fi(o, n).SetValue(o, v);
    static object? M(object o, string n) =>
        o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(o, null);
    static void Pump(int ms) {
        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }
    static int fail = 0;
    static void Check(string name, bool ok, string detail = "") {
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}{(detail.Length > 0 ? "　" + detail : "")}");
        if (!ok) fail++;
    }
    static string RepoRoot() {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, ".git")))
            d = d.Parent;
        return d?.FullName ?? ".";
    }
    static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static void Head(string s) { Console.WriteLine(); Console.WriteLine("=== " + s + " ==="); }

    [STAThread]
    static void Main(string[] args) {
        // `--walk`：①→⑤ 全程走通并逐步核对（用户 2026-08-21）。
        // 与接线测试分开跑：那个验「接线对不对」，这个验「整条流程跑得完、数对不对」。
        if (args.Contains("--walk")) { Environment.ExitCode = Walk.Run(); return; }

        // ⚠ 输出强制 UTF-8：默认走控制台代码页（简中机器上是 GBK），而 ✓(U+2713)
        //   与 ✗(U+2717) 都不在 GBK 里 —— **两个都会变成同一个 `?`**，
        //   于是重定向到文件之后，「过」和「不过」在文本上完全无法分辨。
        //   一份分不出成败的测试报告，等于没有报告。
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        // ── 用**整个 MainForm** 起，而不是单独 new 一个页 ——
        //    启动路径本身就是要验的东西（MainForm.Load 会跑 Run/RunLine）。
        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var page = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1200);

        var wall = (NumericUpDown)F(page, "_wall")!;
        var plate = (NumericUpDown[])F(page, "_tPlate")!;
        var tubeIns = (NumericUpDown)F(page, "_tubeIns")!;
        var outBox = (RichTextBox)F(page, "_out")!;
        // ★★★★★ **必须先把句柄逼出来**（2026-08-20）。
        //
        // 句柄没建之前，RichTextBox 的 `.Text` 赋值**不触发 TextChanged**
        // （TextChanged 是原生控件 EN_CHANGE 转上来的，没窗口就没通知）⇒
        // 挂在 TextChanged 上的排版一次也不会跑。本测试若不建句柄，
        // 验的就是一条**用户永远走不到的路**：它会说排版没生效，而真机上是生效的；
        // 反过来，真正的排版 bug 也会被这层假象盖住。
        // 读一下 Handle 就会建（Control.Handle 的 getter 会 CreateHandle）。
        _ = outBox.Handle;
        var caseBox = (ToolStripComboBox)F(page, "_caseBox")!;

        Head("0 启动：不该自己开跑，也不该弹任何东西");
        Check("启动后没有在跑求解", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 一打开就自己开跑了");
        Check("首屏是说明而不是预测块",
              outBox.Text.Contains("自动重算") && !outBox.Text.Contains("参数已改"));
        Check("四个页签都在", tabs.TabPages.Count >= 4, $"（{tabs.TabPages.Count} 个）");

        Head("1 定案档下拉：切档**不该**触发整线重算");
        int before = caseBox.SelectedIndex;
        caseBox.SelectedIndex = (before + 1) % caseBox.Items.Count;
        Pump(2200);
        Check("切档没有触发求解", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 切个下拉就开跑，用户只是想换档");
        Check("切档没有把输出框冲掉", !outBox.Text.Contains("参数已改"));
        caseBox.SelectedIndex = before;

        Head("2 载入定案：控件被灌值，但不该连环触发");
        M(page, "LoadFinalDesign");
        Pump(2200);
        var fd = FinalDesign.All[Math.Max(0, caseBox.SelectedIndex)];
        Check("壁厚被灌成定案值", (double)wall.Value == fd.WallMm, $"{wall.Value} vs {fd.WallMm}");
        Check("板厚被灌成定案值",
              Enumerable.Range(0, 4).All(i => Math.Abs((double)plate[i].Value - fd.TabThickMm[i]) < 1e-9));
        Check("说明文字还在（没被预测块冲掉）", outBox.Text.Contains("已载入定案档"));
        Check("载入后没有自动开跑", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 载入即开跑，用户可能只是想看看数");

        Head("3 说明书页：判据表要读 FinalDesign 的新值");
        // ⚠ 这里**不要再手抄期望值**。上一版写死了 3106 / 3.33 / 5.52，
        //   2026-08-17 重解定案后三个数全变了，测试就成了「守着旧答案的门」——
        //   它会拦住正确的改动，而这正是 §1.8 那一族最擅长伪装的形态。
        //   ⇒ 期望值一律从 FinalDesign 现取：测的是「说明书有没有跟上定案」，
        //     不是「定案等不等于某个历史数字」。
        var fdM = FinalDesign.W08;
        string html = ManualPage.BuildHtml(fdM);
        Check($"含当前合计 {fdM.TotalMassG:0}", html.Contains(fdM.TotalMassG.ToString("0")));
        Check($"含当前板厚 {fdM.TabThickMm[1]:0.00}", html.Contains(fdM.TabThickMm[1].ToString("0.00")));
        Check($"③ 用的是当前值 {fdM.FlangeDipK:0.0}", html.Contains(fdM.FlangeDipK.ToString("0.00"))
                                                    || html.Contains(fdM.FlangeDipK.ToString("0.0")));
        Check("已作废档不得被当成当前定案", fdM.Invalid.Length == 0,
              fdM.Invalid.Length == 0 ? "" : "★ FinalDesign.W08 自己带着失效声明");
        // ★ 说明书必须跟上界面与判据（2026-08-17）。说明书落后比程序落后更难发现：
        //   它有排版、有图、有判据表，看起来就是答案。
        Check("判据表含 ⑤ 自由段", html.Contains("⑤ 舌片自由段"));
        Check("判据表含 ⑥ 盘盖住孔", html.Contains("⑥ 圆盘盖得住管孔"));
        Check("按钮表含「◇ 搜形状」", html.Contains("◇ 搜形状"));
        Check("按钮表已改名「导出本页 3DM」", html.Contains("导出本页 3DM"));
        Check("有形状体检那一节", html.Contains("形状体检"));
        Check("不再说「核算整线算的是另一片法兰」", !html.Contains("算的是另一片法兰"));
        // 落后的旧文案：新构型四片舌保温都在 0.3–0.6，不该再说「差 12 倍」
        Check("舌保温文案跟着当前档走",
              fdM.TabInsulMm.Max() > fdM.TabInsulMm.Min() * 3 || !html.Contains("差 <b>12 倍</b>"));
        // 把渲染结果落盘，便于人工过目（测试不判样式，只判内容）
        var outHtml = System.IO.Path.Combine(RepoRoot(), "deliverable", "说明书_渲染样本.html");
        try { System.IO.File.WriteAllText(outHtml, html, new System.Text.UTF8Encoding(false)); } catch { }
        // ⚠ 只在**数据区**判旧值。说明书里有一段讲 2026-08-12 那次事故的文字，
        //   引的是「出事那天的板厚」2.11+3.40+3.18+1.76 —— 那是史料，不是当前值。
        //   上一版把整篇一起判，把史料当成了残留（测试写得比被测对象还粗）。
        int cut = html.IndexOf("定案 3DM", StringComparison.Ordinal);
        string dataPart = cut > 0 ? html[..cut] : html;
        Check("数据区不含旧合计 3117", !dataPart.Contains("3117"));
        Check("数据区不含旧板厚 3.40", !dataPart.Contains("3.40"));
        // 2026-08-17 重解后：已作废那两档的合计（3106 / 2388）也不许出现在数据区。
        // 它们仍会出现在**史料段落**里（有出处、标了已作废），那是允许的。
        Check("数据区不含已作废档的合计 3106", !dataPart.Contains("3106"));
        Check("史料段落保留且标明是当时的值",
              html.Contains("出事那天的板厚"));

        Head("4 未收敛时的追问：自动跑的那次**不该弹模态框**");
        // 手动跑 = 允许弹；自动跑 = 用户可能已经走开，弹框会把界面卡住
        var mi = page.GetType().GetMethod("RetryIfJustSlowAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Check("RetryIfJustSlowAsync 存在", mi is not null);
        var ps = mi?.GetParameters().Select(x => x.Name).ToArray() ?? Array.Empty<string?>();
        Check("它能区分「手动/自动」（有 interactive 之类的参数）",
              ps.Any(n => n is "interactive" or "allowPrompt"),
              "参数：" + string.Join(", ", ps));


        Head("5 段控温点表格：改了也要自动重算");
        var segGrid = (DataGridView)F(page, "_segGrid")!;
        Set(page, "_autoArmed", false);
        var segs = F(page, "_segs")!;
        var item0 = ((System.Collections.IList)segs)[0]!;
        item0.GetType().GetProperty("控温C")!.SetValue(item0, 1160.0);
        segGrid.Refresh();
        // BindingList 的属性改动不一定触发 CellValueChanged，直接看接线有没有覆盖到
        Check("段表格已挂上监听（CellValueChanged/RowsRemoved）", true, "（下面用直接触发验）");

        Head("6 切到 .3dm 模式（没填文件）：自动跑**不该**弹模态框");
        var src3dm = (RadioButton)F(page, "_src3dm")!;
        var outLen0 = outBox.Text.Length;
        src3dm.Checked = true;                    // 触发 ParamChanged → 防抖 → 自动跑 → BuildCase 抛异常
        Pump(3500);
        // 若这里弹了 MessageBox，测试会**卡死在这一行**；能走到下面就说明没弹
        Check("没有卡在模态框上（走到了这一行）", true);
        // ★ 真正要问的不是「有没有弹框」，是「界面有没有卡在跑的状态里出不来」。
        //   BuildCase() 在 try 之外抛异常 ⇒ finally 不执行 ⇒ _cts 不清、按钮不恢复、
        //   进度条一直转 —— 用户点了个单选钮，界面就永久卡住，且**没有任何提示**。
        var btnRun = (ToolStripButton)F(page, "_btnRun")!;
        var prog2 = (ToolStripProgressBar)F(page, "_prog")!;
        Check("没有卡在「求解中」状态", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ _cts 没清 —— 界面卡死在求解态");
        Check("按钮恢复可用", btnRun.Enabled, btnRun.Enabled ? "" : "★ 按钮永久禁用");
        Check("进度条已隐藏", !prog2.Visible, prog2.Visible ? "★ 进度条一直转" : "");
        Check("用户被告知发生了什么",
              outBox.Text.Length != outLen0 && (outBox.Text.Contains(".3dm") || outBox.Text.Contains("指定")),
              "★ 输出框没有任何说明，用户只看到界面卡住");
        var srcAnalytic = (RadioButton)F(page, "_srcAnalytic")!;
        srcAnalytic.Checked = true; Pump(2500);
        ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel(); Pump(800);

        Head("7 自动定厚写回厚度：不该再触发一次整线重算");
        Set(page, "_suppressAuto", true);
        for (int i = 0; i < 4; i++) plate[i].Value = 2.00m;
        Set(page, "_suppressAuto", false);
        Set(page, "_autoArmed", false);
        var mAuto = page.GetType().GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Check("RunAsync 能区分「谁点的」", mAuto.GetParameters().Any(x => x.Name == "byTimer"),
              "参数：" + string.Join(", ", mAuto.GetParameters().Select(x => x.Name)));

        Head("8 格式串体检：{x:0.1} 这类只会打出垃圾（本项目栽过三次）");
        foreach (var f in new[] { "Pt_Optimize/UI/LineDesignPage.cs", "Pt_Optimize/UI/ManualPage.cs",
                                  "Pt_Optimize/UI/MainForm.cs" }) {
            string full = System.IO.Path.Combine(RepoRoot(), f);
            if (!System.IO.File.Exists(full)) { Check(f + " 存在", false); continue; }
            // ⚠ 只认**真正的格式说明符**：冒号之后必须全是 0 # , . ; + - 空格 和数字。
            //   否则 CSS（{max-width:760px}）会被误报 —— 上一版就误报了一条。
            //   要抓的是 {x:0.1} 这种：1 不是占位符，是字面量，小数点被吃掉 ⇒ 31.6 打成 "321"。
            var bad = System.Text.RegularExpressions.Regex
                .Matches(System.IO.File.ReadAllText(full), @"\{[A-Za-z_][^{}:]*:([0#,.;+\-\s]*[1-9][0#,.;+\-\s0-9]*)\}")
                .Select(m => m.Value).Distinct().ToArray();
            Check(f + " 无可疑格式串", bad.Length == 0, bad.Length > 0 ? string.Join("  ", bad.Take(4)) : "");
        }

        Head("9 装配下界：舌长低于「切点+压接+自由段」时必须**自己顶上去**");
        // 为什么做成测试：定案的舌长 90 mm 装不下铜排，而它能长期存在，
        // 正是因为舌长在程序里是个谁都不核对的独立常数（memory: 舌长90是错误解）。
        // 这条测试的作用是：以后**任何人**把舌长改回一个装不下的值，界面都会当场顶回来。
        var discD = (NumericUpDown)F(page, "_discD")!;
        var tabLen = (NumericUpDown)F(page, "_tabLen")!;
        var tabW = (NumericUpDown)F(page, "_tabW")!;
        Set(page, "_suppressAuto", true);
        discD.Value = 60m; tabW.Value = 15m; tabLen.Value = 90m;   // 正是旧定案那一组
        Set(page, "_suppressAuto", false);
        M(page, "ShowPrediction");
        Pump(300);
        Check("舌长 90 被顶高了", tabLen.Value > 90m, $"现在 {tabLen.Value} mm");
        // 切点 √(30²−15²)=25.98，压接 40，自由段 100 ⇒ 下界 165.98 ⇒ 顶到 166
        Check("顶到的正是装配下界 166", tabLen.Value == 166m, $"现在 {tabLen.Value} mm");
        Check("并且说清楚了为什么", outBox.Text.Contains("自动顶到") && outBox.Text.Contains("铜排"));
        Check("输出里有自由段这一行", outBox.Text.Contains("自由段"));
        ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel();
        Set(page, "_autoArmed", false);
        Pump(300);

        Head("10 压接段长度：不能再用 3 mm 那个**数值默认值**");
        // 2026-08-17 抓到：本页从来没设过 BusbarClampLengthMm ⇒ 一直用 DesignInputs 的 3.0，
        // 而定案是 40。少扣 37 mm 会让判据⑤「装不下」被判成「装得下」——
        // 正好盖住 90 mm 那个错，属于最危险的一类：错得看不出来。
        var lcProbe = M(page, "BuildCase") as LineCase;
        Check("BuildCase 返回了算例", lcProbe is not null);
        if (lcProbe is not null)
            Check("压接段 = 定案值，不是 3 mm 默认值",
                  Math.Abs(lcProbe.Base.BusbarClampLengthMm - FinalDesign.Current.ClampLengthMm) < 1e-9,
                  $"{lcProbe.Base.BusbarClampLengthMm} vs {FinalDesign.Current.ClampLengthMm}");

        Head("11 失效告示：已失效的定案档必须在**最前面**说出来");
        var invalid = FinalDesign.All.FirstOrDefault(x => x.Invalid.Length > 0);
        if (invalid is null)
            Check("（当前没有已失效的档，跳过）", true);
        else {
            caseBox.SelectedIndex = Array.IndexOf(FinalDesign.All, invalid);
            M(page, "LoadFinalDesign");
            Pump(400);
            Check("载入后有失效告示", outBox.Text.Contains("已失效"));
            int posWarn = outBox.Text.IndexOf("已失效", StringComparison.Ordinal);
            int posLoad = outBox.Text.IndexOf("已载入定案档", StringComparison.Ordinal);
            Check("告示排在「已载入定案档」之前", posWarn >= 0 && posWarn < posLoad,
                  $"告示@{posWarn} 载入@{posLoad}　★ 排在后面等于没写");
            Check("说明了失效的判据", invalid.InvalidChecks.Length > 0,
                  "InvalidChecks 为空 ⇒ 自检门无法分辨「已知的失败」与「新出现的失败」");
        }

        Head("12 出图拦截：已失效的档不许导出 3DM");
        // 与 --make3dm 那条同根：用户 2026-08-17 发现作废档把现役档的 3DM 覆盖掉了。
        // UI 这条路更险 —— 下拉里作废档就排在现役档后面，默认文件名还一字不差。
        foreach (var fdX in FinalDesign.All) {
            string reason = LineDesignPage.ExportBlockedReason(fdX);
            bool shouldBlock = fdX.Invalid.Length > 0;
            Check($"{fdX.Name} → {(shouldBlock ? "拦" : "放")}",
                  (reason.Length > 0) == shouldBlock,
                  reason.Length > 0 ? "已拦" : "放行");
        }

        Head("13 不看说明书也能用：判据没过时，界面要**直接说下一步**");
        // ④ 的验收不能只验「代码里有这段文字」，要验**它真的出现在用户看的那块文本里**。
        // 做法：喂一个必然不过的算例（舌长退回 90 ⇒ 判据⑤ 不过），跑真解，读输出框。
        {
            var bad90 = FinalDesign.Current.Clone();
            bad90.TabLengthMm = 90.0;
            var lcBad = bad90.BuildCase(new DesignInputs(), checkRamp: false);
            var rBad = LineRunner.Run(lcBad);
            Check("算例确实解出来了", rBad.Ok && rBad.Converged,
                  rBad.Ok ? "" : rBad.Message);
            var ck5 = rBad.Checks.FirstOrDefault(c => c.Name.StartsWith("⑤", StringComparison.Ordinal));
            Check("判据⑤ 确实不过", ck5 is { Ok: false }, $"{ck5?.Actual:0.0}/{ck5?.Limit:0.0}");
            Check("⑤ 的 Note 里带**下一步**动作", ck5?.Note.Contains("【下一步】") ?? false);
            Check("下一步说的是装配、不是调热学旋钮",
                  ck5?.Note.Contains("调热学旋钮没有用") ?? false);
            // 把结果灌进页面的 Show()，验「结论与下一步」有没有顶到最前面
            var mShow = typeof(LineDesignPage).GetMethod("Show",
                            BindingFlags.NonPublic | BindingFlags.Instance);
            Check("Show() 存在", mShow is not null);
            mShow?.Invoke(page, new object?[] { rBad, null });

            // ★ 顺带守住「管轴向剖面」那张图（2026-08-20 之前它**从来没被画过**）。
            //   借用这里已经解出来的 rBad，不另跑一次分钟级的解。
            //   空页签的坏处是它看起来像「这次没算出来」，而不是「没接线」。
            var pAx = (ScottPlot.WinForms.FormsPlot)F(page, "_pAx")!;
            Check("「管轴向剖面」真的画了东西",
                  pAx.Plot.GetPlottables().Any(),
                  $"{pAx.Plot.GetPlottables().Count()} 个图元");

            string txt = outBox.Text;
            int posVerdict = txt.IndexOf("条判据没过", StringComparison.Ordinal);
            // ⚠ 锚点换过（2026-08-20）：原来找的是纯文字判据表的表头「判据　★=硬安全线」，
            //   而判据表已改成真表格控件（_checks），那张纯文字表**整个被删掉了** ⇒
            //   IndexOf 恒为 −1，这条断言从「验顺序」变成了「恒不过」。
            //   **断言挂了先查前提是否还在**（HANDOVER §8 记过同样的教训）。
            //   本条要守的道理没变：判定必须排在「判据细节在哪儿」之前 ——
            //   在新架构里，指路那句话就是判据细节的入口。
            int posTable = txt.IndexOf("判据表见上方表格", StringComparison.Ordinal);
            Check("开头就给出判定", posVerdict >= 0 && posVerdict < 400,
                  $"位置 {posVerdict}");
            Check("判定排在判据表**之前**", posVerdict >= 0 && posTable > posVerdict,
                  $"判定@{posVerdict} 表@{posTable}");
            Check("开头就给出「先解决哪一条」", txt.Contains("先解决这一条"));
            Check("开头就带【下一步】", txt.IndexOf("【下一步】", StringComparison.Ordinal) is int q
                                        && q >= 0 && q < 800, "");
            Check("输出里没有残留的 Markdown 星号", !txt.Contains("**"),
                  txt.Contains("**") ? "★ 还在往纯文本框里灌 Markdown 源码" : "");
            // ★★★★★ 报告说「判据表见上方表格」，那张表就**必须真的有行**（2026-08-20）。
            //   判据表从纯文字改成 DataGridView 时，控件、初始化、布局、填充方法都写好了，
            //   **唯独没有人调用 FillChecks** ⇒ 表永远是空的，而报告还在指着它。
            //   空表比没有表更坏：人会把「没有行」读成「没有不过的」。
            //   ⇒ 这条门守的是「指路的话」与「被指的地方」对得上。
            var checksGrid = (DataGridView)F(page, "_checks")!;
            Check("判据表真的被填上了", checksGrid.Rows.Count > 0,
                  $"{checksGrid.Rows.Count} 行" + (checksGrid.Rows.Count == 0
                      ? "　★ 报告在指着一张空表说「判据在那儿」" : ""));
            Check("判据表的行数与判据条数一致",
                  checksGrid.Rows.Count == rBad.Checks.Length,
                  $"表 {checksGrid.Rows.Count} 行 / 判据 {rBad.Checks.Length} 条");
            // 不过的行要标出来 —— 颜色只是**重复**判定，但它是人第一眼看的东西
            Check("有不过的行被标了底色",
                  checksGrid.Rows.Cast<DataGridViewRow>()
                      .Any(x => x.DefaultCellStyle.BackColor.R > 250
                             && x.DefaultCellStyle.BackColor.G < 240));
            // 把真实输出落盘，供人工过目 —— 排版这种事，测试只能验规则，好不好看要用眼睛
            try {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(RepoRoot(), "deliverable", "输出框_排版样本.txt"),
                    txt, new System.Text.UTF8Encoding(false));
            } catch { }
        }

        Head("14 搜形状：按钮在、不自己跑、.3dm 模式下要**明确拒绝**而不是空转");
        var btnShape = (ToolStripButton)F(page, "_btnShape")!;
        // ⚠ 断言从「可用」改成「存在」（2026-08-21）：**前提变了**。
        //   本条写于阶段轨之前，那时所有按钮永远可点。现在「◇ 搜形状」归 ④ 定尺寸，
        //   而此刻还没解过 ⇒ ④ 未解锁 ⇒ 它**应当**是禁用的 —— 那正是门禁在起作用。
        //   （在这里断言「可用」等于要求门禁失效。）它的锁态由第 24 节按门禁规则专门验。
        Check("按钮存在", btnShape is not null, btnShape?.Text ?? "");
        Check("④ 未解锁时它是禁用的（门禁在起作用）", btnShape is { Enabled: false },
              $"Enabled={btnShape?.Enabled}");
        Check("启动后没有在跑", F(page, "_cts") is null);
        {
            // 切到 .3dm 模式：形状由图纸给定，不是可搜索的自由度 ⇒ 必须说清楚，不能默默什么都不做
            var src3 = (RadioButton)F(page, "_src3dm")!;
            Set(page, "_suppressAuto", true);
            src3.Checked = true;
            Set(page, "_suppressAuto", false);
            var mSearch = page.GetType().GetMethod("SearchShapeAsync",
                              BindingFlags.NonPublic | BindingFlags.Instance);
            Check("SearchShapeAsync 存在", mSearch is not null);
            mSearch?.Invoke(page, null);
            Pump(400);
            Check("拒绝并说明了原因",
                  outBox.Text.Contains("只在") && outBox.Text.Contains("解析"),
                  outBox.Text.Length > 0 ? "" : "★ 输出框什么都没说");
            Check("没有把界面卡在求解态", F(page, "_cts") is null,
                  F(page, "_cts") is null ? "" : "★ _cts 没清");
            var srcA = (RadioButton)F(page, "_srcAnalytic")!;
            Set(page, "_suppressAuto", true);
            srcA.Checked = true;
            Set(page, "_suppressAuto", false);
            Set(page, "_autoArmed", false);
            Pump(200);
        }

        Head("15 页面 ↔ FinalDesign 的**单位**必须对得上（盘径是直径，模型要半径）");
        // 这是 UI 这条路最容易出、又最不容易被看见的错：直径/半径、半宽/全宽各差一倍，
        // 而两边都是「合理的数」，判据表照样出得来 —— 典型的安静失败。
        {
            Set(page, "_suppressAuto", true);
            discD.Value = 70m; tabW.Value = 28m; tabLen.Value = 200m; wall.Value = 0.7m;
            for (int i = 0; i < 4; i++) plate[i].Value = 1.11m + i * 0.10m;
            Set(page, "_suppressAuto", false);
            var mP2F = page.GetType().GetMethod("PageToFinalDesign",
                           BindingFlags.NonPublic | BindingFlags.Instance);
            Check("PageToFinalDesign 存在", mP2F is not null);
            var fdP = mP2F?.Invoke(page, null) as FinalDesign;
            Check("盘径 70 → 半径 35", fdP is not null && Math.Abs(fdP.DiscRadiusMm - 35) < 1e-9,
                  $"{fdP?.DiscRadiusMm}");
            Check("舌端半宽 28 原样过去", fdP is not null && Math.Abs(fdP.TabHalfWidthMm - 28) < 1e-9,
                  $"{fdP?.TabHalfWidthMm}");
            Check("舌长 200 原样过去", fdP is not null && Math.Abs(fdP.TabLengthMm - 200) < 1e-9,
                  $"{fdP?.TabLengthMm}");
            Check("管壁 0.7 原样过去", fdP is not null && Math.Abs(fdP.WallMm - 0.7) < 1e-9,
                  $"{fdP?.WallMm}");
            Check("四片板厚按序过去", fdP is not null &&
                  Enumerable.Range(0, 4).All(i => Math.Abs(fdP.TabThickMm[i] - (1.11 + i * 0.10)) < 1e-9),
                  fdP is null ? "" : string.Join("/", fdP.TabThickMm));
            Check("不继承种子的失效声明", fdP is not null && fdP.Invalid.Length == 0
                                          && fdP.InvalidChecks.Length == 0);
            // 反向：写回控件的换算必须是同一套（搜形状结束时用的就是这段）
            Check("半径 35 写回去应是直径 70", Math.Abs(2 * (fdP?.DiscRadiusMm ?? 0) - 70) < 1e-9);
            ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel();
            Set(page, "_autoArmed", false);
        }

        Head("16 【1b】页面与内核**同一个几何构造器**");
        // 1b 的全部意义就是这一条：把页面上的定案参数原样填进控件，
        // 「核算整线」造出来的 FlangePlate 必须与 FinalDesign.Plate 逐字段相同。
        // 只要有人再绕过构造器自己造一片，这条立刻红。
        {
            var fd1b = FinalDesign.Current;
            Set(page, "_suppressAuto", true);
            wall.Value = (decimal)fd1b.WallMm;
            tubeIns.Value = (decimal)fd1b.TubeInsulMm;
            discD.Value = (decimal)(2 * fd1b.DiscRadiusMm);
            tabLen.Value = (decimal)fd1b.TabLengthMm;
            tabW.Value = (decimal)fd1b.TabHalfWidthMm;
            for (int i = 0; i < 4; i++) plate[i].Value = (decimal)fd1b.TabThickMm[i];
            var srcA2 = (RadioButton)F(page, "_srcAnalytic")!;
            srcA2.Checked = true;
            Set(page, "_suppressAuto", false);
            Set(page, "_autoArmed", false);

            var mBuild = page.GetType().GetMethod("BuildCase",
                             BindingFlags.NonPublic | BindingFlags.Instance);
            var lc1b = mBuild?.Invoke(page, null) as LineCase;
            Check("BuildCase 返回算例", lc1b is not null);
            Check("解析模式下造出四片法兰", lc1b?.FlangePlates.Length == 4);
            if (lc1b is { FlangePlates.Length: 4 })
            {
                var want0 = fd1b.Plate(0, fd1b.DiscFloorMm(new DesignInputs()));
                var got0 = lc1b.FlangePlates[0];
                Check("盘半径一致", Math.Abs(got0.DiscRadiusMm - want0.DiscRadiusMm) < 1e-9);
                Check("舌端 X 一致", Math.Abs(got0.TabEndXMm - want0.TabEndXMm) < 1e-9);
                Check("舌端半宽一致", Math.Abs(got0.TabEndHalfWidthMm - want0.TabEndHalfWidthMm) < 1e-9);
                Check("★ 管孔渐变环在（1b 之前缺）",
                      got0.DiscStepRadiiMm.Length == want0.DiscStepRadiiMm.Length
                      && got0.DiscStepRadiiMm.Length > 0,
                      $"{got0.DiscStepRadiiMm.Length} 级");
                Check("★ 角焊缝在（1b 之前缺）",
                      Math.Abs(got0.WeldFilletLegMm - want0.WeldFilletLegMm) < 1e-9,
                      $"{got0.WeldFilletLegMm:0.00}");
                Check("★ 逐片舌保温在（1b 之前缺）",
                      !double.IsNaN(got0.TabInsulThickMm)
                      && Math.Abs(got0.TabInsulThickMm - want0.TabInsulThickMm) < 1e-9,
                      $"{got0.TabInsulThickMm:0.0} mm");
                Check("★ 等宽舌片（1b 之前是梯形）", got0.TabParallel && want0.TabParallel);
                Check("★ 舌根圆角在（1b 之前缺）",
                      Math.Abs(got0.TabFilletMm - want0.TabFilletMm) < 1e-9, $"R{got0.TabFilletMm:0}");
                Check("四片厚度按序对上",
                      Enumerable.Range(0, 4).All(i =>
                          Math.Abs(lc1b.FlangePlates[i].ThicknessMm
                                   - fd1b.Plate(i, fd1b.DiscFloorMm(new DesignInputs())).ThicknessMm) < 1e-9));
                // 这一条是「表达不了」清单必须已经空掉
                var mPvF = typeof(LineDesignPage).GetMethod("PageVsFinal",
                               BindingFlags.NonPublic | BindingFlags.Static);
                string missNow = mPvF?.Invoke(null, new object?[] { got0 }) as string ?? "?";
                Check("「本页表达不了」清单已空", missNow.Length == 0, missNow);
            }
            Check("压接段仍是定案值不是 3 mm 默认值",
                  lc1b is not null &&
                  Math.Abs(lc1b.Base.BusbarClampLengthMm - fd1b.ClampLengthMm) < 1e-9,
                  $"{lc1b?.Base.BusbarClampLengthMm}");
            // 法兰保温：1b 前 BuildCase 里写死 20，控件动了也没用
            Check("圆盘保温跟着页面控件走（原来写死 20）",
                  lc1b is not null && lc1b.Base.FlangeInsulThickMm > 0,
                  $"{lc1b?.Base.FlangeInsulThickMm:0.#} mm");
            Check("水头没有在换构造器时丢掉",
                  lc1b is not null && lc1b.HeadM.Length > 0, $"{lc1b?.HeadM.Length} 段");

            // ★★★★★ 1b 的**决定性**验证（慢，约 1–2 分钟，值得）：
            //   把定案参数填进页面、走页面的 BuildCase 真解一次，
            //   结果必须**复现定案记录值**。
            //   1b 之前这件事做不到 —— 页面解的是另一片法兰，这正是当初不得不单独做
            //   「▶ 复现定案」按钮的原因。现在两条路应该落到同一个解。
            //   容差沿用 --selfcheck 那一套（③ 1.0 K／②′ 0.5 W／②″ 0.2 K／管J 0.05／合计 2 g）。
            if (lc1b is not null)
            {
                Console.WriteLine("  …（真解一次，约 1–2 分钟）");
                var r1b = LineRunner.Run(lc1b);
                Check("页面路径解得出且收敛", r1b.Ok && r1b.Converged, r1b.Ok ? "" : r1b.Message);
                if (r1b is { Ok: true, Converged: true })
                {
                    double V(string k) => r1b.Checks
                        .FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
                    // ⚠ 差值要先掐负零：(-0.0).ToString("+0.000;−0.000") 会打出 "-+0.000"
                    void Near(string nm, double got, double want, double tol, string unit) =>
                        Check($"页面路径复现定案 {nm}", Math.Abs(got - want) <= tol,
                              $"{got:0.000} {unit} vs 记录 {want:0.000}　差 " +
                              SizerResult.Signed(got - want, "+0.000;−0.000"));
                    Near("③", V("③"), fd1b.FlangeDipK, 1.00, "K");
                    Near("②′", V("②′"), fd1b.HoleFluxW, 0.50, "W");
                    Near("②″", V("②″"), fd1b.DiscOverK, 0.20, "K");
                    Near("管J", V("管 J"), fd1b.TubeJ, 0.05, "A/mm²");
                    Near("合计", r1b.TotalMassG, fd1b.TotalMassG, 2.0, "g");
                    Check("页面路径也判为全过", r1b.AllOk,
                          r1b.AllOk ? "" : string.Join("；", r1b.Failed));
                }
            }
        }

        Head("17 输出框排版：不许出现 Markdown 源码，中文列宽要按显示宽度算");
        // 用户 2026-08-17 反馈「文挡好乱」。两个病：
        //   ① `**粗体**` 是 Markdown，而输出框显示纯文本 ⇒ 满屏星号；
        //   ② 列宽按字符数补齐，而中文一个字占两个西文字宽 ⇒ 每行列位置都不一样。
        {
            // ① 灌一段带 ** 的文本，读回来不该还有 **（应已渲染成粗体并去掉标记）
            outBox.Text = "普通 **要加粗的** 普通";
            Pump(300);
            Check("`**` 已被渲染掉，不再显示成星号",
                  !outBox.Text.Contains("**"), "读回：" + outBox.Text);
            Check("被标记的字还在（只是去了标记）", outBox.Text.Contains("要加粗的"));
            // 真的加粗了吗：选中那段，看字体
            int p = outBox.Text.IndexOf("要加粗的", StringComparison.Ordinal);
            if (p >= 0) {
                outBox.SelectionStart = p; outBox.SelectionLength = 4;
                Check("那段确实是粗体", outBox.SelectionFont?.Bold == true,
                      outBox.SelectionFont?.Style.ToString() ?? "取不到字体");
                outBox.SelectionStart = 0; outBox.SelectionLength = 0;
            }
            // ② 显示宽度：中文按 2 算
            var tW = typeof(LineDesignPage).Assembly.GetType("PtOptimize.UI.TextFmt");
            var mWidth = tW?.GetMethod("Width", BindingFlags.Public | BindingFlags.Static);
            var mPadR = tW?.GetMethod("PadR", BindingFlags.Public | BindingFlags.Static);
            Check("TextFmt 可用", tW is not null && mWidth is not null && mPadR is not null);
            if (mWidth is not null && mPadR is not null) {
                Check("中文宽度按 2 算", (int)mWidth.Invoke(null, new object?[] { "管壁" })! == 4,
                      $"「管壁」= {mWidth.Invoke(null, new object?[] { "管壁" })}");
                Check("西文宽度按 1 算", (int)mWidth.Invoke(null, new object?[] { "abcd" })! == 4);
                // 两个显示宽度相同的串，补齐后总宽必须一致 —— 这正是表格对齐的充要条件
                string a = (string)mPadR.Invoke(null, new object?[] { "管 J", 26 })!;
                string b = (string)mPadR.Invoke(null, new object?[] { "⑥ 圆盘盖得住管孔＋焊脚", 26 })!;
                int wa = (int)mWidth.Invoke(null, new object?[] { a })!;
                int wb = (int)mWidth.Invoke(null, new object?[] { b })!;
                Check("长短不一的判据名补齐后显示宽度相同", wa == wb, $"{wa} vs {wb}");
            }
            outBox.Clear();
        }

        Head("17′ Excel 式表格：制表位真的设上了，而且只管它自己那张表");
        // 排版改成「一格一个 Tab」之后，对齐不再靠补空格，靠的是 SelectionTabs（像素定位）。
        // 这机制前两版栽的两个坑都在**选区下标**上，而且都长着「看起来在工作」的样子：
        //   ① 下标算偏 ⇒ 只有一部分行被设上（同一张表里几行对齐、几行不对齐）；
        //   ② 选区把行尾换行也选进去 ⇒ 顺带把下一段普通句子也套上了表的制表位。
        // 两条都不会报错、也不会崩，只能靠门守。
        {
            const string tsv =
                "名称\t数值\t备注\n" +
                "甲行\t9\t甲注\n" +
                "乙行\t1150\t乙注\n" +
                "\n" +
                "这一句不是表，制表位不该管到它。";
            outBox.Text = tsv;          // ⚠ 要经 TextChanged 挂钩才会重排 ⇒ 设完 Text 必须 Pump
            Pump(300);

            // 取某段文字所在**段落**的制表位。
            // ⚠ 不按行号取：GetFirstCharIndexFromLine 数的是**显示行**，一旦哪天 WordWrap 打开
            //   就会静悄悄地取错段落 —— 拿控件文本里的锚点找位置，才与显示无关。
            int[] TabsAt(string anchor) {
                int q = outBox.Text.IndexOf(anchor, StringComparison.Ordinal);
                if (q < 0) return Array.Empty<int>();
                outBox.Select(q, anchor.Length);
                var got = outBox.SelectionTabs;
                outBox.Select(0, 0);
                return got is null ? Array.Empty<int>() : got;
            }

            var tHead = TabsAt("名称");
            var tRow1 = TabsAt("甲行");
            var tRow2 = TabsAt("乙行");
            var tProse = TabsAt("这一句不是表");

            Check("表被认出来了：数据行有制表位", tRow1.Length > 0,
                  tRow1.Length > 0 ? string.Join("/", tRow1) : "★ 制表位根本没设上");
            Check("制表位严格递增（不递增则 Tab 原地不动，两列贴成一格）",
                  tRow1.Length > 0 && Enumerable.Range(1, tRow1.Length - 1).All(i => tRow1[i] > tRow1[i - 1]),
                  string.Join("/", tRow1));
            Check("同一张表三行的制表位完全一致",
                  tHead.Length > 0 && tHead.SequenceEqual(tRow1) && tRow1.SequenceEqual(tRow2),
                  $"[{string.Join("/", tHead)}] [{string.Join("/", tRow1)}] [{string.Join("/", tRow2)}]");
            Check("表外的段落没被套上表的制表位",
                  tProse.Length == 0 || !tProse.SequenceEqual(tRow1),
                  tProse.Length == 0 ? "" : "★ 选区连行尾换行一起选了，改到了下一段：" + string.Join("/", tProse));

            // 数字列右对齐（Excel 同款默认）：窄的那格前面必须被补上空格
            var shown = outBox.Text.Replace("\r\n", "\n").Split('\n');
            var cells9 = (shown.FirstOrDefault(r => r.StartsWith("甲行", StringComparison.Ordinal)) ?? "")
                         .Split('\t');
            Check("那一行还是三格", cells9.Length == 3, string.Join(" | ", cells9));
            Check("窄的数字格前面被补了空格（数字列右对齐）",
                  cells9.Length == 3 && cells9[1].Length > 1 && cells9[1][0] == ' ',
                  cells9.Length == 3 ? $"「{cells9[1]}」" : "");
            Check("补空格没有动到数值本身",
                  cells9.Length == 3 && cells9[1].Trim() == "9", cells9.Length == 3 ? cells9[1].Trim() : "");

            // 命令行那一侧：同一份 TSV 走 Plain，不能留裸 Tab（控制台没有可设的制表位）
            // ⚠ TextFmt 是 internal，本测试在另一个程序集 ⇒ 只能反射。
            var tFmt = typeof(MainForm).Assembly.GetType("PtOptimize.UI.TextFmt");
            var mPlain = tFmt?.GetMethod("Plain", BindingFlags.Public | BindingFlags.Static);
            var mW2 = tFmt?.GetMethod("Width", BindingFlags.Public | BindingFlags.Static);
            Check("TextFmt.Plain / Width 可用", mPlain is not null && mW2 is not null);
            if (mPlain is not null && mW2 is not null) {
                var fW = mW2!;      // 局部函数里的 null 分析不跟着外面的 if 走，先落成非空局部量
                string plain = (string)mPlain.Invoke(null, new object?[] { tsv })!;
                Check("Plain 的输出里没有裸 Tab", !plain.Contains('\t'),
                      plain.Contains('\t') ? "★ 控制台每 8 格跳一次，裸 Tab 一定歪" : "");
                var pls = plain.Replace("\r\n", "\n").Split('\n');
                int ColAt(string anchor) {
                    string line = pls.FirstOrDefault(x => x.Contains(anchor, StringComparison.Ordinal)) ?? "";
                    int q = line.IndexOf(anchor, StringComparison.Ordinal);
                    return q < 0 ? -1 : (int)fW.Invoke(null, new object?[] { line[..q] })!;
                }
                int p0 = ColAt("备注"), p1 = ColAt("甲注"), p2 = ColAt("乙注");
                Check("Plain 里各行第三列的起点一致（按显示宽度算）",
                      p0 > 0 && p0 == p1 && p1 == p2, $"{p0}/{p1}/{p2}");
            }
            outBox.Clear();
        }

        Head("18 工具条实况：说明书按名字分的三组，必须与真实按钮顺序对得上");
        // 用户 2026-08-17 质疑这句话，让我去抓 UI 看。说明书里写「照着分隔线分组」，
        // 而如果分隔线根本不显示，那就是**让用户去找一个看不见的东西** —— 比不写更糟。
        {
            ToolStrip? ts = null;
            void Walk(Control c) {
                if (c is ToolStrip t && t.Items.Count > 3 && ts is null) { ts = t; return; }
                foreach (Control k in c.Controls) Walk(k);
            }
            Walk(page);
            Check("找到「整线设计」页的工具条", ts is not null);
            if (ts is not null) {
                Console.WriteLine("     实况（按左到右）：");
                var groups = new List<List<string>> { new() };
                int sepVisible = 0, sepTotal = 0;
                // ⚠ 这里必须用 `Available` 不是 `Visible`（2026-08-17 第一版写错了）：
                //   窗体没真正显示时 `Visible` 对**所有**项都是 false —— 按钮也一样，
                //   于是「实际 0 组」，看起来像分隔线全没了，其实是**检查量选错了**。
                //   `Available` 才是「若父容器显示则会显示」，无头环境下也有意义。
                //   （拿一个在测试环境里恒为 false 的量当证据，就是「校验量选错」的又一例。）
                foreach (ToolStripItem it in ts.Items) {
                    if (it is ToolStripSeparator sp) {
                        sepTotal++; if (sp.Available) sepVisible++;
                        groups.Add(new List<string>());
                        Console.WriteLine($"       ── 分隔线（Available={sp.Available}）");
                    } else {
                        string label = it is ToolStripComboBox ? "[定案档下拉]"
                                     : it.Text.Length > 0 ? it.Text
                                     : it is ToolStripProgressBar ? "[进度条]" : "[" + it.GetType().Name + "]";
                        if (it.Available) groups[^1].Add(label);
                        Console.WriteLine($"       {label}");
                    }
                }
                Check("确实有分隔线", sepTotal > 0, $"{sepTotal} 条");
                Check("分隔线都在（Available）", sepTotal > 0 && sepVisible == sepTotal,
                      $"{sepVisible}/{sepTotal}");
                var nonEmpty = groups.Where(g => g.Count > 0).ToList();
                Console.WriteLine("     ⇒ 实际分成 " + nonEmpty.Count + " 组：");
                foreach (var g in nonEmpty) Console.WriteLine("       · " + string.Join(" / ", g));
                // 说明书讲的是「三组」：定案档组 / 本页参数组 / 工具组（进度条那段不算）
                Check("与说明书说的组数对得上", nonEmpty.Count >= 3, $"实际 {nonEmpty.Count} 组");
                // ★ 说明书是按**按钮名字**分组讲的（不是让用户去找那条 1 px 的分隔线）。
                //   所以要验的是：每一组里确实是说明书点名的那些按钮。
                // ★ 2026-08-20：期望值改成**从 Flow 现取**，不再手抄按钮名。
                //
                //   原来这里写死「第一组要有 复现定案/载入定案/导出定案，第二组要有
                //   核算整线/自动定厚/搜形状」。阶段轨把「导出定案 3DM」搬去了 ⑤、
                //   「自动定厚 / 搜形状」搬去了 ④，这两条断言当场变红 ——
                //   而它们红得**没有信息**：不是接线错了，是断言自己抄了一份会过期的清单。
                //
                //   现在验的是同一条道理、但不会过期的形式：
                //   本页工具条上属于「定案不读页面」组的按钮，必须与 Flow 登记的一致；
                //   属于「页面参数」组的同理。搬到别页的按钮自然不在本页，也就不必改测试。
                var onPage = nonEmpty.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
                foreach (var grp in new[] { CmdGroup.定案不读页面, CmdGroup.页面参数 })
                {
                    var want = Flow.Commands
                        .Where(c => c.Stage == StageId.整线核算 && c.Group == grp)
                        .Select(c => c.Text).ToArray();
                    var missing = want.Where(t => !onPage.Contains(t)).ToArray();
                    Check($"本页「{grp}」组与 Flow 一致",
                          want.Length > 0 && missing.Length == 0,
                          missing.Length == 0 ? string.Join("/", want)
                                              : "★ 页上没有：" + string.Join("/", missing));
                }
                // 分组的**含义**仍要守住：不读页面控件的那些，必须与读页面的分在不同组 ——
                // 说明书就是按这条教用户的（「定案两个字打头的那几个不读页面控件」）。
                var caseGrp = nonEmpty.FirstOrDefault(g => g.Any(x => x.Contains("复现定案")));
                Check("「定案」组里不混入读页面控件的命令",
                      caseGrp is not null && !caseGrp.Any(x => x.Contains("核算整线")),
                      caseGrp is null ? "没找到定案组" : string.Join("/", caseGrp));
            }
        }

        Head("19 界面缩放：窗口与字号随屏幕，且小屏上不许抛异常");
        {
            var tU = typeof(LineDesignPage).Assembly.GetType("PtOptimize.UI.UiScale");
            Check("UiScale 可用", tU is not null);
            if (tU is not null) {
                float k = (float)tU.GetProperty("K", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                float fp = (float)tU.GetProperty("FontPt", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                Check("缩放系数在 1.0–1.7 之间", k >= 1.0f && k <= 1.7f, $"K={k:0.00}");
                Check("字号不小于 9 pt", fp >= 9f, $"{fp:0.0} pt");
                var mS = tU.GetMethod("S", BindingFlags.Public | BindingFlags.Static)!;
                Check("S(100) 随 K 放大", (int)mS.Invoke(null, new object?[] { 100 })! == (int)Math.Round(100 * k));
                // ⚠ 这一条是真抓到过的：Math.Clamp(w, 1100, wa.Width) 在小屏上 min>max ⇒ 抛异常 ⇒ 程序起不来
                var mW = tU.GetMethod("WindowSize", BindingFlags.Public | BindingFlags.Static)!;
                bool threw = false;
                try { mW.Invoke(null, new object?[] { 0.8 }); } catch { threw = true; }
                Check("WindowSize 不抛异常", !threw);
            }
            // 窗口真的按屏幕比例来了（而不是写死 1400×900）
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Check("窗口宽度跟着屏幕走（不是写死的 1400）",
                  main.Width != 1400 || wa.Width < 1600,
                  $"窗口 {main.Width}×{main.Height}　工作区 {wa.Width}×{wa.Height}");
            Check("窗口没有超出工作区", main.Width <= wa.Width && main.Height <= wa.Height);
            // 字体真的应用到了主窗口
            Check("主窗口字号 = UiScale.FontPt",
                  tU is null || Math.Abs(main.Font.SizeInPoints
                      - (float)tU.GetProperty("FontPt", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!) < 0.05,
                  $"{main.Font.SizeInPoints:0.0} pt");
            // 说明书 CSS 字号也必须跟着（WebView2 不吃 WinForms 字体）
            Check("说明书 CSS 字号已替换（没有残留占位符）", !html.Contains("MANUALFONT"));
            // ⚠ ToolStrip **不继承父窗体字体** —— 用户 2026-08-18：「下排的字还是太小」。
            //   在 Form 上设 Font 对工具条无效，必须逐个显式设；这条守住它。
            var strips = new List<ToolStrip>();
            void Collect(Control c) { if (c is ToolStrip t) strips.Add(t); foreach (Control k in c.Controls) Collect(k); }
            Collect(main);
            Check("找到工具条", strips.Count > 0, $"{strips.Count} 条");
            var small = strips.Where(t => t.Font.SizeInPoints < main.Font.SizeInPoints - 0.05).ToList();
            Check("每条工具条的字号都不小于主窗口",
                  small.Count == 0,
                  small.Count == 0 ? $"全部 {main.Font.SizeInPoints:0.0} pt"
                                   : "★ 偏小：" + string.Join("、", small.Select(t => $"{t.Font.SizeInPoints:0.0}pt")));
        }

        // ═══════════════════════════════════════════════════════════════
        Head("20 Flow 单一数据源：登记表与真界面必须**双向**对得上");
        {
            // 为什么是双向：单向断言（Flow 里的都能找到）只抓得到「界面少了个按钮」，
            // 抓不到「界面多了个没登记的按钮」—— 而后者正是漂移的常见方向：
            // 有人加了个按钮，忘了登记，于是它不属于任何链、不受门禁、说明书里也没有，
            // 却安安静静地待在工具条上等人点。
            try { Flow.SelfTest(); Check("Flow 自检（Id 唯一、命令与阶段互指）", true); }
            catch (Exception ex) { Check("Flow 自检（Id 唯一、命令与阶段互指）", false, ex.Message); }

            var btns = new List<ToolStripButton>();
            void Walk(Control c) {
                // ⚠ PropertyGrid 自带一条工具条（「按类别顺序」「按字母顺序」「属性页」），
                //   那是 WinForms 的东西、不是我们的命令 —— 断言它只会在换框架版本或
                //   换系统语言时莫名其妙地红。整棵子树跳过。
                if (c is PropertyGrid) return;
                if (c is ToolStrip ts)
                    foreach (var it in ts.Items.OfType<ToolStripButton>()) btns.Add(it);
                foreach (Control k in c.Controls) Walk(k);
            }
            Walk(main);
            Check("扫到工具条按钮", btns.Count > 0, $"{btns.Count} 个");

            // ── 方向 ①：Flow 登记的，界面上都要有
            var onScreen = btns.Select(b => b.Text).ToHashSet(StringComparer.Ordinal);
            var missing = Flow.Commands.Where(c => !onScreen.Contains(c.Text)).ToList();
            Check("Flow 登记的命令，界面上都找得到",
                  missing.Count == 0,
                  missing.Count == 0 ? $"{Flow.Commands.Length} 条全部命中"
                                     : "★ 界面上没有：" + string.Join("、", missing.Select(c => $"{c.Id}「{c.Text}」")));

            // ── 方向 ②：界面上有的，Flow 里都要登记
            var known = Flow.Commands.Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
            var unregistered = btns.Select(b => b.Text).Distinct(StringComparer.Ordinal)
                                   .Where(t => !known.Contains(t)).ToList();
            Check("界面上的按钮，Flow 里都登记了",
                  unregistered.Count == 0,
                  unregistered.Count == 0 ? "无遗漏"
                                          : "★ 没登记：" + string.Join("、", unregistered.Select(t => $"「{t}」")));

            // ── 链的权威性：可交付的**有且只有一条**
            var deliverable = Flow.Chains.Where(c => c.Deliverable).ToList();
            Check("可交付的链有且只有一条", deliverable.Count == 1,
                  deliverable.Count == 1 ? deliverable[0].Name
                                         : "★ " + string.Join("、", deliverable.Select(c => c.Name)));
            Check("可交付的那条是 C 整线耦合",
                  deliverable.Count == 1 && deliverable[0].Id == ChainId.C整线耦合);

            // ── 门禁只准引用判据常量，不准写字面量。
            //    这里反过来验：GateSpec 里出现的每个 key，都必须真是 LineResult.Key 的某个常量值。
            var keyConsts = typeof(LineResult.Key)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
            var strayKeys = Flow.Stages
                .Where(s => s.GateToUnlockNext is not null)
                .SelectMany(s => s.GateToUnlockNext!.RequiredChecks)
                .Where(k => !keyConsts.Contains(k)).ToList();
            Check("门禁引用的判据都是 LineResult.Key 常量",
                  strayKeys.Count == 0,
                  strayKeys.Count == 0 ? $"常量表 {keyConsts.Count} 条"
                                       : "★ 野字符串：" + string.Join("、", strayKeys));

            // ── ★ 这一条守的是本次改造最容易写反的地方：
            //    ④ 的用途就是把不过的判据调过来 ⇒ 判据没过但**收敛**时，④ 必须是解锁的。
            //    写成 RequireAllOk 就会把正常用法整个锁死，而且表面上毫无异样。
            var g3 = Flow.Stage(StageId.整线核算).GateToUnlockNext!;
            Check("③→④ 的门是「收敛」而不是「判据全过」",
                  g3.RequireConverged && !g3.RequireAllOk,
                  $"RequireConverged={g3.RequireConverged} RequireAllOk={g3.RequireAllOk}");
            var g4 = Flow.Stage(StageId.定尺寸).GateToUnlockNext!;
            Check("④→⑤ 的门是「判据全过」", g4.RequireAllOk);

            // ── 「定案」那四个不读页面控件 ⇒ 不受阶段门禁
            var caseCmds = Flow.Commands.Where(c => c.Group == CmdGroup.定案不读页面).ToList();
            Check("「定案」组都标了不读页面控件",
                  caseCmds.Count > 0 && caseCmds.All(c => !c.ReadsPageControls),
                  $"{caseCmds.Count} 条：" + string.Join("、", caseCmds.Select(c => c.Text)));
        }

        // ═══════════════════════════════════════════════════════════════
        Head("21 参数对象只有一份：读取方案不许把某些页甩在旧数据上");
        {
            // 病灶（2026-08-20 抓到）：LoadCase 原来写 `_in = x` —— **换引用**。
            // 而 LineDesignPage/AnalysisPage 在构造时拿到的是引用且字段是 readonly ⇒
            // 换完之后参数表指向新方案，那两页仍算旧方案，**没有任何提示**。
            var inMain = F(main, "_in")!;
            var anal = tabs.TabPages.OfType<AnalysisPage>().First();
            Check("整线设计页与主窗口共用同一个参数对象",
                  ReferenceEquals(F(page, "_base"), inMain));
            Check("分析页与主窗口共用同一个参数对象",
                  ReferenceEquals(F(anal, "_base"), inMain));

            // CopyInto 必须真的搬值，而且**不换引用**（换了就等于没修）
            var src = new DesignInputs { TAmbC = 42.5, TubeIdMm = 61.0, GradeName = "PtRh10" };
            var dst = (DesignInputs)inMain;
            double keepAmb = dst.TAmbC;
            SegmentSolver.CopyInto(src, dst);
            Check("CopyInto 搬了值", Math.Abs(dst.TAmbC - 42.5) < 1e-9 && dst.GradeName == "PtRh10",
                  $"TAmb {keepAmb}→{dst.TAmbC}　牌号 {dst.GradeName}");
            Check("CopyInto 之后仍是同一个对象", ReferenceEquals(F(main, "_in"), dst));
            Check("CopyInto 之后两页看到的还是它",
                  ReferenceEquals(F(page, "_base"), dst) && ReferenceEquals(F(anal, "_base"), dst));

            // 换了方案 ⇒ 判据表与上一次的解必须**清掉**，不能安静地留着骗人
            var checksGrid = (DataGridView)F(page, "_checks")!;
            typeof(LineDesignPage).GetMethod("InvalidateSolution",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                !.Invoke(page, null);
            Check("作废之后判据表清空", checksGrid.Rows.Count == 0, $"{checksGrid.Rows.Count} 行");
            Check("作废之后上一次的解也丢掉", F(page, "_last") is null && F(page, "_solvedSnap") is null);
        }

        // ═══════════════════════════════════════════════════════════════
        Head("22 运行时新生的控件也要接上自动重算");
        {
            // 病灶：各级「锁定」勾选框是 BuildLockBoxes 在**运行时**创建的，
            // 而 HookAutoRun 在构造函数末尾就跑完了 ⇒ 它们永远赶不上那趟车，
            // 勾/取消锁定**不触发自动重算**，而界面毫无异样。
            // 修法是让 Watch 挂 ControlAdded 递归，把「必须记得挂」这个前提整个去掉。
            var lockPanel = (Control)F(page, "_lockPanel")!;
            Set(page, "_autoArmed", false);
            var probe = new CheckBox { Text = "接线探针" };
            lockPanel.Controls.Add(probe);      // ← 运行时才出生，正是原来漏掉的那类
            probe.Checked = true;
            Check("运行时加进来的勾选框能触发自动重算",
                  (bool)F(page, "_autoArmed")! == true);
            lockPanel.Controls.Remove(probe);

            // ⚠ 同一个递归绝不能把工具条也钩进去 —— 「定案档 ▾」被当成参数会让
            //   切档触发一次分钟级重算（那个 bug 修过一次，第 1 项守着它）。
            //   这里正面验一次：工具条上的 ComboBox 不该被接线。
            Set(page, "_autoArmed", false);
            var caseBox22 = (ToolStripComboBox)F(page, "_caseBox")!;
            if (caseBox22.Items.Count > 1)
            {
                caseBox22.SelectedIndex = caseBox22.SelectedIndex == 0 ? 1 : 0;
                Pump(80);
                Check("工具条上的下拉**不算**参数（切档不触发重算）",
                      (bool)F(page, "_autoArmed")! == false);
            }

            // 段表加一行 = 真的多一段管、多两片法兰，必须触发重算
            Set(page, "_autoArmed", false);
            var segGrid22 = (DataGridView)F(page, "_segGrid")!;
            var segs22 = (System.Collections.IList)F(page, "_segs")!;
            // ★ 先把句柄逼出来，否则 DataGridView 不会真的生成行，RowsAdded 也就不会响 ——
            //   那样这条断言会「过」得毫无意义（和 outBox 那条句柄坑是同一族）。
            _ = segGrid22.Handle;
            int rows0 = segGrid22.Rows.Count;
            var rowType22 = segs22.GetType().GetGenericArguments()[0];
            segs22.Add(Activator.CreateInstance(rowType22));
            Pump(120);
            Check("段表真的多出一行（否则下一条等于没测）",
                  segGrid22.Rows.Count > rows0, $"{rows0} → {segGrid22.Rows.Count} 行");
            Check("段表加一行会触发自动重算", (bool)F(page, "_autoArmed")! == true);
            segs22.RemoveAt(segs22.Count - 1);
        }

        // ═══════════════════════════════════════════════════════════════
        Head("23 参数表：每一项都必须有链归属");
        {
            // 病灶：整线链上有一批参数**根本不看参数表** —— 有的被 ③ 页控件接管，
            // 有的被 LineRunner 强制取值，而表上完全看不出来。用户在那里改了半天，
            // 整线解一动不动。这是「不知道自己在算什么」的另一半病因。
            //
            // 归属靠 [Category] 前缀表达（编译期常量 ⇒ 天然单一来源）。
            // 这条断言守的是：**没有哪个参数会漏掉归属** —— 新加一个属性却忘了归类，
            // 它会安静地落进一个界面上不存在的分组里。
            var known = Flow.Params.Select(x => x.CategoryPrefix).ToHashSet(StringComparer.Ordinal);
            var props = typeof(DesignInputs).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
                .ToArray();
            Check("扫到可见参数", props.Length > 0, $"{props.Length} 项");

            var orphan = props
                .Select(x => (x.Name, Cat: x.GetCustomAttribute<CategoryAttribute>()?.Category ?? ""))
                .Where(t => !known.Contains(t.Cat))
                .ToList();
            Check("每个可见参数的分类都在 Flow.Params 里登记",
                  orphan.Count == 0,
                  orphan.Count == 0 ? $"{known.Count} 个分类全部登记"
                                    : "★ 没归属：" + string.Join("、", orphan.Select(t => $"{t.Name}「{t.Cat}」")));

            // ✗ 打头的两组必须**说清楚是谁接管了它** —— 只标「无效」而不说去哪儿改，
            //   等于把用户丢在原地。
            var overridden = props
                // ⚠ 认 ✗ 要用 Contains 不是 StartsWith：分类名前面还有个**数字排序前缀**
                //   （PropertyGrid 按字母序排分类，✗ 会排到最前面 —— 无效的参数反而最显眼，
                //     正好反了 ⇒ 用 1…9 显式定序，把两组 ✗ 压到最后）。
                .Where(x => (x.GetCustomAttribute<CategoryAttribute>()?.Category ?? "").Contains('✗'))
                .ToArray();
            Check("有被接管的参数", overridden.Length > 0, $"{overridden.Length} 项");
            // ★ 自动抓「参数表说有效，实际被覆盖」这一类错 ——
            //   这条不是理论洁癖：本轮 BusbarClampLengthMm 就归错了组，参数表显示 3 mm，
            //   而整线链两条路都强制取定案档的 40 mm。是靠人看截图才发现的，
            //   而「看起来正常的错数」正是本项目最危险的形状 ⇒ 交给机器守。
            //
            // 判据：凡在**整线链的构造器**里被赋值的 DesignInputs 字段，
            //       就不许挂在宣称对 C 链有效的分类下（"C 整线 …" 开头的组）。
            var builders = new[] { "Pt_Optimize/Core/FinalDesign.cs",
                                   "Pt_Optimize/UI/LineDesignPage.cs" };
            var forced = new HashSet<string>(StringComparer.Ordinal);
            bool srcOk = true;
            foreach (var f in builders)
            {
                string full = System.IO.Path.Combine(RepoRoot(), f);
                if (!System.IO.File.Exists(full)) { srcOk = false; continue; }
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             System.IO.File.ReadAllText(full), @"\bp\.(\w+)\s*(?:\.\w+\s*)?="))
                    forced.Add(m.Groups[1].Value);
            }
            Check("读得到整线链构造器源码", srcOk && forced.Count > 0, $"{forced.Count} 个被强制字段");

            var lying = props
                .Where(x => forced.Contains(x.Name))
                .Select(x => (x.Name, Cat: x.GetCustomAttribute<CategoryAttribute>()?.Category ?? ""))
                .Where(t => t.Cat.Contains("C 整线", StringComparison.Ordinal))
                .ToList();
            Check("没有参数一边被整线链强制取值、一边宣称对 C 链有效",
                  lying.Count == 0,
                  lying.Count == 0 ? "" : "★ 归错组：" + string.Join("、", lying.Select(t => $"{t.Name}「{t.Cat}」")));

            var noWhy = overridden
                .Where(x => !(x.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "").Contains("接管"))
                .Select(x => x.Name).ToList();
            Check("每个被接管的参数都写明了谁接管它",
                  noWhy.Count == 0,
                  noWhy.Count == 0 ? "" : "★ 没写：" + string.Join("、", noWhy));
        }

        // ═══════════════════════════════════════════════════════════════
        Head("24 门禁只该拦「拿不成立的解去出图」，不该拦记事本");
        {
            // 病灶（2026-08-21 用户抓到）：`保存`/`读取` 被标成 ReadsPageControls=true，
            // 于是随 ⑤ 一起锁 ⇒ **参数调了半天存不下来，非得先解出一个收敛解才准存档**。
            // 存参数和「这一版几何算没算通」毫不相干。
            //
            // 这条是**行为断言**，不是复述那个标志位：直接看真按钮的 Enabled。
            //
            // ⚠ 必须**自己把状态驱到锁态**：跑到这一节时第 16 节已经真解过一次并收敛，
            //   ④⑤ 早就解锁了 —— 头一版忘了这点，四条断言集体空转
            //   （靠「此刻确实有锁着的阶段」那条前置才发现，见 §7「空集通过的断言」）。
            typeof(LineDesignPage).GetMethod("InvalidateSolution",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                !.Invoke(page, null);
            typeof(MainForm).GetMethod("SyncGates",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                !.Invoke(main, null);
            Pump(200);

            var lockedPages = tabs.TabPages.OfType<TabPage>()
                .Where(t => t.Text.StartsWith("🔒", StringComparison.Ordinal)).ToArray();
            Check("此刻确实有锁着的阶段（否则本节等于没测）",
                  lockedPages.Length > 0,
                  lockedPages.Length > 0
                      ? string.Join("、", lockedPages.Select(t => t.Text))
                      : "★ 一格都没锁 —— 断言会空转");

            var lockedBtns = new List<ToolStripButton>();
            void W(Control c)
            {
                if (c is ToolStrip ts) foreach (var it in ts.Items.OfType<ToolStripButton>()) lockedBtns.Add(it);
                foreach (Control k in c.Controls) W(k);
            }
            foreach (var t in lockedPages) W(t);

            // 「不消费解」的命令：ReadsPageControls=false 的那些，锁着也必须能点
            var shouldStayOn = lockedBtns
                .Select(b => (b, spec: Flow.Commands.FirstOrDefault(c => c.Text == b.Text)))
                .Where(x => x.spec is { ReadsPageControls: false })
                .ToArray();
            Check("锁着的阶段上确实有「不消费解」的命令", shouldStayOn.Length > 0,
                  string.Join("、", shouldStayOn.Select(x => x.b.Text)));
            var wronglyOff = shouldStayOn.Where(x => !x.b.Enabled).Select(x => x.b.Text).ToList();
            Check("它们在锁态下仍然可点", wronglyOff.Count == 0,
                  wronglyOff.Count == 0 ? "" : "★ 被误锁：" + string.Join("、", wronglyOff));

            // 反面：消费解的命令在锁态下**必须**被禁，否则门禁形同虚设
            var mustBeOff = lockedBtns
                .Select(b => (b, spec: Flow.Commands.FirstOrDefault(c => c.Text == b.Text)))
                .Where(x => x.spec is { ReadsPageControls: true })
                .ToArray();
            Check("锁着的阶段上确实有「消费解」的命令", mustBeOff.Length > 0,
                  string.Join("、", mustBeOff.Select(x => x.b.Text)));
            var leaked = mustBeOff.Where(x => x.b.Enabled).Select(x => x.b.Text).ToList();
            Check("消费解的命令在锁态下确实被禁", leaked.Count == 0,
                  leaked.Count == 0 ? "" : "★ 没拦住：" + string.Join("、", leaked));

            // 「保存」必须在场且可用 —— 上面两条是通则，这条钉死用户报的那个具体症状
            var save = lockedBtns.FirstOrDefault(b => b.Text == "保存");
            Check("「保存」在 ⑤ 上找得到", save is not null);
            Check("「保存」不随 ⑤ 上锁", save?.Enabled == true);
        }

        // ═══════════════════════════════════════════════════════════════
        Head("25 复现定案要走完全程：解完必须发布状态，否则 ④⑤ 一格不开");
        {
            // 病灶（2026-08-21 用户提出「能否一键」时查出）：ReproduceAsync 只写
            // `_last` + `Show()`，**既不设 _solvedSnap 也不 PushFlow** ⇒
            // FlowState.Last 从没被推过、Fresh 恒 false ⇒
            // **复现出一个全判据通过的解，④⑤ 照样锁着**，阶段轨当作什么都没发生。
            //
            // 这里不真跑分钟级复现（第 16 节已经验过页面路径能复现记录值），
            // 只验**接线**：那两句在不在。方法体是编译期常量，读源码即可判定，
            // 比跑一次几十秒的解便宜得多，且不会因机器快慢而不稳。
            string src = File.ReadAllText(Path.Combine(RepoRoot(),
                "Pt_Optimize", "UI", "LineDesignPage.cs"));
            int a = src.IndexOf("private async Task ReproduceAsync", StringComparison.Ordinal);
            int b = src.IndexOf("private void LoadFinalDesign()", StringComparison.Ordinal);
            Check("找得到 ReproduceAsync 的方法体", a >= 0 && b > a, $"{a}..{b}");
            string raw = a >= 0 && b > a ? src[a..b] : "";
            // ⚠ 必须**剥掉注释再判**：这段代码的注释里正大段解释「为什么不走
            //   PageToFinalDesign」，直接对全文做子串匹配会命中那些**散文**，
            //   把「代码没调它」误报成「代码调了它」。
            //   断言要测的是**那件事**，不是那件事附近的文字。
            // 不用任何反斜杠转义：本仓的钩子会把转义序列改成真字符，字面量当场断掉。
            var noCmt = raw.Split((char)10)   // (char)10 = LF：避开转义，且不挑 CRLF/LF
                .Select(l => { int k = l.IndexOf("//", StringComparison.Ordinal);
                               return k >= 0 ? l.Substring(0, k) : l; });
            string body = string.Join(" ", noCmt);

            Check("复现之后会灌控件（页面显示与档一致）",
                  body.Contains("LoadFinalDesignFrom", StringComparison.Ordinal));
            Check("复现**仍从档解**（不走 PageToFinalDesign，保住交叉校验）",
                  body.Contains("fd.BuildCase", StringComparison.Ordinal)
                  && !body.Contains("PageToFinalDesign", StringComparison.Ordinal),
                  "从档解 = 独立于页面搬运的那条路");
            Check("复现之后会发布状态（PushFlow）",
                  body.Contains("PushFlow()", StringComparison.Ordinal),
                  "没有它 FlowState.Last 永远是 null ⇒ ④⑤ 不开");
            Check("复现之后会记下 _solvedSnap（否则 Fresh 恒 false）",
                  body.Contains("_solvedSnap = CurrentSnap()", StringComparison.Ordinal));
            // ★ 而且**不能无条件**记：水头不属于定案几何，页面水头与存档不同时
            //   这个解并不是「页面参数的解」，记了就是假的 Fresh。
            Check("记 _solvedSnap 是**有条件**的（水头对得上才记）",
                  body.Contains("headSame", StringComparison.Ordinal),
                  "水头不属于定案几何 ⇒ 不同就不能假装 Fresh");
        }

        // ═══════════════════════════════════════════════════════════════
        Head("26 有链在跑时：状态面板要说话，其它会起算的命令一律禁掉");
        {
            // 病灶（2026-08-21 用户提出）：
            //  ① FlowState.Running/RunningNote 声明了、StagePanel 也早就在读
            //     （「正在算：…（再点那个按钮 = 取消）」），**但从来没有人赋值** ⇒
            //     ④ 页点「自动定厚」要跑三分多钟，而那一页没有进度条也没有状态标签
            //     （_prog/_status 都长在 ③ 上）⇒ 界面一动不动，看着像卡死。
            //  ② ④ 上三个按钮**可以同时点** —— RunAsync 只禁自己那两个，
            //     ◇搜形状 不禁，② 厚度灵敏度 又属于另一页各禁各的。
            var flow = (FlowState)F(main, "_flow")!;

            // 先回到「没在跑」的干净态，并让 ④⑤ 解锁（否则下面分不清是门禁禁的还是互斥禁的）
            flow.SetRunning(null);
            Pump(100);
            var allBtns = new List<ToolStripButton>();
            void W2(Control c)
            {
                if (c is ToolStrip ts) foreach (var it in ts.Items.OfType<ToolStripButton>()) allBtns.Add(it);
                foreach (Control k in c.Controls) W2(k);
            }
            foreach (TabPage t in tabs.TabPages) W2(t);
            var compute = allBtns
                .Select(b => (b, spec: Flow.Commands.FirstOrDefault(c => c.Text == b.Text)))
                .Where(x => x.spec is not null && x.spec.Chain != ChainId.无)
                .ToArray();
            var inert = allBtns
                .Select(b => (b, spec: Flow.Commands.FirstOrDefault(c => c.Text == b.Text)))
                .Where(x => x.spec is not null && x.spec.Chain == ChainId.无)
                .ToArray();
            Check("扫到「会起算」的命令", compute.Length > 0,
                  string.Join("、", compute.Select(x => x.b.Text)));
            Check("扫到「不算东西」的命令（保存/读取/出图）", inert.Length > 0,
                  string.Join("、", inert.Select(x => x.b.Text)));

            // ── 开跑
            flow.SetRunning(ChainId.C定尺寸, "自动定厚");
            Pump(200);
            Check("状态面板能说出正在算哪条链", flow.Running == ChainId.C定尺寸,
                  Flow.Chain(flow.Running!.Value).Name);
            Check("进度文字能刷新", flow.RunningNote.Length > 0, flow.RunningNote);

            var stillOn = compute.Where(x => x.b.Enabled).Select(x => x.b.Text).ToList();
            Check("跑起来之后，会起算的命令全被禁", stillOn.Count == 0,
                  stillOn.Count == 0 ? $"{compute.Length} 个全禁"
                                     : "★ 还能点：" + string.Join("、", stillOn));
            // ⚠ 只能在**已解锁**的阶段上断言 —— 「导出本页 3DM」是 ChainId.无 但
            //   ReadsPageControls=true，⑤ 没解锁时它被门禁正常锁住，那是**对的**，不是误禁。
            //   头一版没分这两种「禁」，把门禁的功劳算成了互斥闸的错。
            var inertUnlocked = inert
                .Where(x => Gate.Evaluate(x.spec!.Stage, flow).Unlocked).ToArray();
            Check("有「不算东西」的命令处在已解锁的阶段上（否则下一条空转）",
                  inertUnlocked.Length > 0,
                  string.Join("、", inertUnlocked.Select(x => x.b.Text)));
            var wronglyOff = inertUnlocked.Where(x => !x.b.Enabled).Select(x => x.b.Text).ToList();
            Check("不算东西的命令不受互斥牵连", wronglyOff.Count == 0,
                  wronglyOff.Count == 0 ? "" : "★ 被误禁：" + string.Join("、", wronglyOff));

            // ── 结束：必须恢复
            flow.SetRunning(null);
            Pump(200);
            Check("跑完之后状态清掉", flow.Running is null);
            Check("跑完之后至少有一个会起算的命令恢复可点",
                  compute.Any(x => x.b.Enabled),
                  string.Join("、", compute.Where(x => x.b.Enabled).Select(x => x.b.Text)));

            // 三个入口都要报状态 —— 少一个，那条链跑起来界面就是死的
            string ldp = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
            string apg = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "AnalysisPage.cs"));
            // ⚠ 别按 "SetRunning(ChainId." 数：RunAsync 那处是三元
            //   `SetRunning(autoSize ? ChainId.C定尺寸 : ChainId.C整线耦合, …)`，数不到。
            //   改成「总数 − 清空数」，与写法无关。
            int setAll = CountOf(ldp, "SetRunning("), setNull = CountOf(ldp, "SetRunning(null)");
            Check("③/④ 的三个入口都报了状态", setAll - setNull >= 3,
                  $"LineDesignPage 里开跑 {setAll - setNull} 处 / 清空 {setNull} 处");
            Check("① 与「② 厚度灵敏度」也报状态",
                  apg.Contains("SetRunning(", StringComparison.Ordinal));
            Check("每个入口都在 finally 里清（异常/取消也要解除互斥）",
                  CountOf(ldp, "SetRunning(null)") >= 3 && apg.Contains("SetRunning(null)", StringComparison.Ordinal),
                  $"LineDesignPage {CountOf(ldp, "SetRunning(null)")} 处清");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "★ 全部通过" : $"✗ {fail} 项不过");
        Environment.ExitCode = fail;
    }
}
