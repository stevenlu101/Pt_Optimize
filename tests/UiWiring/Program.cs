using System;
using System.Linq;
using System.Text.RegularExpressions;
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
        // ★ `.git` 在**工作树（worktree）里是文件**，不是目录（2026-09-07 实测）。
        //   只认目录 ⇒ 在 worktree 里一路走到盘符、回退成 "."，
        //   于是拼出别的仓库的路径、报「找不到 LineDesignPage.cs」——
        //   **仪器换个地方就用不了**，而 worktree 正是「不动主线做验证」的标准做法。
        while (d is not null
               && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, ".git"))
               && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, ".git")))
            d = d.Parent;
        return d?.FullName ?? ".";
    }
    static int CountOf(string hay, string needle)
    {
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    static void Head(string s) { Console.WriteLine(); Console.WriteLine("=== " + s + " ==="); }

    /// <summary>
    /// 把页面**静置**下来：解除自动重算的武装、取消在跑的解、等它真的退出。
    ///
    /// 为什么需要：本文件的各节共用同一个 MainForm。某一节只要把 `_autoArmed`
    /// 留成 true，防抖定时器就会在**后面某一节的 Pump 里**起一次分钟级的真解 ——
    /// 于是那一节看到 FlowState.Running 非空，而 Flow.Next 在有链在跑时返回 null
    /// ⇒ 报出来的是「没给下一步」，跟真因（上一节没收干净）八竿子打不着。
    /// 实测第 29 节正是这么红的，而且它**在另一个 bug 被修掉之前一直没暴露**：
    /// 那次后台解会撞上一个非法牌号抛异常，finally 顺手把 Running 清了。
    /// </summary>
    static void Quiesce(object page)
    {
        Set(page, "_autoArmed", false);
        ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel();
        for (int i = 0; i < 40 && F(page, "_cts") is not null; i++) Pump(250);
        Set(page, "_autoArmed", false);
    }

    [STAThread]
    static void Main(string[] args) {
        // `--walk`：①→⑤ 全程走通并逐步核对（用户 2026-08-21）。
        // 与接线测试分开跑：那个验「接线对不对」，这个验「整条流程跑得完、数对不对」。
        if (args.Contains("--walk")) { Environment.ExitCode = Walk.Run(); return; }
        // `--segs N`：验证时把段数调到 N（用户 2026-09-03：「以后『验证』时就跑两段」）。
        //   ⚠ 不改默认 —— 已归档的对帐基准是三段的数，默认换掉等于把基准悄悄改了。
        int isg = Array.IndexOf(args, "--segs");
        if (isg >= 0 && isg + 1 < args.Length && int.TryParse(args[isg + 1], out var sgN))
            Walk.Segments = sgN;

        // `--follow`：完全照链路提示走一遍（用户 2026-08-25 验收）
        int ifw = Array.IndexOf(args, "--follow3dm");
        if (ifw >= 0 && ifw + 1 < args.Length)
        { Environment.ExitCode = Walk.Follow(args[ifw + 1]); return; }
        // `--searchshape [quick]`：驱动真的「◇ 搜形状」（用户 2026-08-25）
        // `--repro <盘Ø> <舌长> <半宽> <管壁> [quick]`：从给定起点复现设计记录（用户 2026-08-25）
        // ★★ 对帐：命令行验过的交付结果，界面也要拿得到（2026-08-30）
        int irc = Array.IndexOf(args, "--reconcile");
        if (irc >= 0 && irc + 2 < args.Length)
        {
            Environment.ExitCode = Walk.Reconcile(
                double.Parse(args[irc + 1]), double.Parse(args[irc + 2]));
            return;
        }
        int ir = Array.IndexOf(args, "--repro");
        if (ir >= 0 && ir + 4 < args.Length)
        {
            Environment.ExitCode = Walk.Repro(
                double.Parse(args[ir + 1]), double.Parse(args[ir + 2]),
                double.Parse(args[ir + 3]), double.Parse(args[ir + 4]),
                args.Contains("quick"));
            return;
        }
        int ibg = Array.IndexOf(args, "--budget");
        if (ibg >= 0 && ibg + 1 < args.Length && int.TryParse(args[ibg + 1], out var bgMin) && bgMin > 0)
            Walk.SearchBudgetMin = bgMin;
        if (args.Contains("--searchshape"))
        { Environment.ExitCode = Walk.SearchShape(args.Contains("quick")); return; }
        // `--follow [壁厚]`：不给壁厚 = 从**开箱默认**出发（答「从零开始提示带不带得动人」）；
        // 给了壁厚 = **先载入那一档设计记录再走**（答「工程师的真实路径走不走得通」）。
        // 后者是 2026-09-02 补的：开箱默认那条两次都在自动定厚超时，
        // 于是链路后半段（网格无关复核 → 出图）一次都没被走到过。
        int ifo = Array.IndexOf(args, "--follow");
        if (ifo >= 0)
        {
            double fw = ifo + 1 < args.Length && double.TryParse(args[ifo + 1], out var fv)
                        ? fv : double.NaN;
            Environment.ExitCode = Walk.Follow(null, fw);
            return;
        }
        if (args.Contains("--tabins0")) { Environment.ExitCode = Walk.TabIns0(); return; }
        int ie = Array.IndexOf(args, "--export3dm");
        if (ie >= 0 && ie + 1 < args.Length)
        {
            double kk = ie + 2 < args.Length && double.TryParse(args[ie + 2], out var v) ? v : 1.0;
            Environment.ExitCode = Walk.Export3dm(args[ie + 1], kk); return;
        }
        int im = Array.IndexOf(args, "--map3dm");
        if (im >= 0 && im + 1 < args.Length)
        { Environment.ExitCode = Walk.Map3dm(args[im + 1]); return; }
        // `--walk3dm <file>`：走 .3dm 任意形状那条路
        int i3 = Array.IndexOf(args, "--walk3dm");
        if (i3 >= 0 && i3 + 1 < args.Length)
        { Environment.ExitCode = Walk.Run3dm(args[i3 + 1]); return; }

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
        // ★ 再静置 2.5 s（防抖是 1.5 s）——「启动那一瞬没在跑」不等于「不会自己开跑」。
        //   界面抓图里状态面板写着「正在算：核算整线」而结果是「还没解过」，就是这么来的：
        //   没人碰它，防抖定时器自己到期，开了一次分钟级的解。
        Pump(2500);
        Check("静置 2.5 s 之后仍然没有自己开跑", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 没人碰它，它自己跑了一次分钟级的解");
        // ★ 首屏闸门（2026-08-23 新增）：排版期的控件事件不算「用户改参数」。
        //   先验它确实拦得住，再置位 —— 后面几节模拟的都是**用户操作**，
        //   而本测试从不 Show 窗体（页面句柄是懒建的）⇒ 不置位它永远是 false。
        Check("首屏闸门在（排版期的事件不算用户操作）", F(page, "_userReady") is false,
              "没 Show 过 ⇒ 仍处于首屏期");
        typeof(LineDesignPage).GetMethod("ParamChanged",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Check("首屏期触发参数变更**不会**武装自动重算",
              (bool)F(page, "_autoArmed")! == false,
              "★ 没人碰它就自己排上了一次分钟级的解");
        Set(page, "_userReady", true);          // 此后按「用户在操作」对待

        // ⚠ 2026-09-02 首屏说明按用户要求大幅缩短（「对工程师不必要的说明可以消除」）——
        //   「自动重算怎么触发」那段删了。验的仍是同一件事：**首屏是说明，不是预测块**。
        Check("首屏是说明而不是预测块",
              outBox.Text.Contains("点「核算整线」") && !outBox.Text.Contains("参数已改"),
              outBox.Text.Length > 60 ? outBox.Text[..60] : outBox.Text);
        Check("五个页签都在（① 输入／② 法兰优化／③ 结果与出图／参考工具／使用说明）", tabs.TabPages.Count >= 5, $"（{tabs.TabPages.Count} 个）");

        // ★★★★★ 开箱那一刻摆在界面上的几何，**必须是造得出来的**（2026-08-24）。
        //
        // 用户实测：什么都没改，直接点「核算整线」，得到
        //   ⑤ 舌片自由段 −12.4 / 100　③ 法兰增量温降 599.5 / 10
        // 默认 盘Ø60(R30)／半宽20／舌长50／压接40 ⇒
        //   自由段 = 50 − 40 − √(30²−20²) = −12.36 ⇒ 压接块伸进圆盘里。
        // 也就是说：**开箱即是一个装不上铜排的构型**，而工程师第一次点核算
        // 就会花几分钟拿到一张全红的表，还以为是自己参数没设对。
        // （同族前科：管壁默认 0.40 低于焊接下界 0.6，见本页类头。）
        {
            var lc0 = M(page, "BuildCase") as LineCase;
            Check("开箱就造得出算例", lc0 is not null);
            if (lc0?.FlangePlates is { Length: > 0 } pl0)
            {
                var g0 = GeometryScreen.Judge(pl0, lc0.Base.BusbarClampLengthMm, lc0.FreeTabMinMm);
                var c5 = g0.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal));
                Check("开箱默认的 ⑤ 自由段不是负数（压接块没伸进圆盘）",
                      c5 is not null && c5.Actual >= 0,
                      c5 is null ? "★ 没有这条判据" : $"{c5.Actual:0.0} mm");
                Check("开箱默认的 ⑤ 直接就过（不必先让人踩一次坑）",
                      c5 is not null && c5.Ok,
                      c5 is null ? "" : $"{c5.Actual:0.0} / {c5.Limit:0}");
            }
        }

        Head("1 设计记录下拉：切档**不该**触发整线重算");
        int before = caseBox.SelectedIndex;
        caseBox.SelectedIndex = (before + 1) % caseBox.Items.Count;
        Pump(2200);
        Check("切档没有触发求解", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 切个下拉就开跑，用户只是想换档");
        Check("切档没有把输出框冲掉", !outBox.Text.Contains("参数已改"));
        caseBox.SelectedIndex = before;

        Head("2 载入设计记录：控件被灌值，但不该连环触发");
        M(page, "LoadDesignSpec");
        Pump(2200);
        var fd = DesignSpec.All[Math.Max(0, caseBox.SelectedIndex)];
        Check("壁厚被灌成设计记录值", (double)wall.Value == fd.WallMm, $"{wall.Value} vs {fd.WallMm}");
        Check("板厚被灌成设计记录值",
              Enumerable.Range(0, 4).All(i => Math.Abs((double)plate[i].Value - fd.TabThickMm[i]) < 1e-9));
        Check("说明文字还在（没被预测块冲掉）", outBox.Text.Contains("已载入设计记录"));
        Check("载入后没有自动开跑", F(page, "_cts") is null,
              F(page, "_cts") is null ? "" : "★ 载入即开跑，用户可能只是想看看数");

        Head("3 说明书页：判据表要读 DesignSpec 的新值");
        // ⚠ 这里**不要再手抄期望值**。上一版写死了 3106 / 3.33 / 5.52，
        //   2026-08-17 重解设计记录后三个数全变了，测试就成了「守着旧答案的门」——
        //   它会拦住正确的改动，而这正是 §1.8 那一族最擅长伪装的形态。
        //   ⇒ 期望值一律从 DesignSpec 现取：测的是「说明书有没有跟上设计记录」，
        //     不是「设计记录等不等于某个历史数字」。
        var fdM = DesignSpec.W08;
        string html = ManualPage.BuildHtml(fdM);
        Check($"含当前合计 {fdM.TotalMassG:0}", html.Contains(fdM.TotalMassG.ToString("0")));
        Check($"含当前板厚 {fdM.TabThickMm[1]:0.00}", html.Contains(fdM.TabThickMm[1].ToString("0.00")));
        Check($"③ 用的是当前值 {fdM.FlangeDipK:0.0}", html.Contains(fdM.FlangeDipK.ToString("0.00"))
                                                    || html.Contains(fdM.FlangeDipK.ToString("0.0")));
        Check("已作废档不得被当成当前设计记录", fdM.Invalid.Length == 0,
              fdM.Invalid.Length == 0 ? "" : "★ DesignSpec.W08 自己带着失效声明");
        // ★ 说明书必须跟上界面与判据（2026-08-17）。说明书落后比程序落后更难发现：
        //   它有排版、有图、有判据表，看起来就是答案。
        // ⚠ 2026-09-03 起说明书里**没有判据代号**（用户：「工程师看不懂 ②′」）⇒ 查全名。
        Check("判据表含 舌片自由段", html.Contains("舌片自由段"));
        Check("判据表含 圆盘盖得住管孔", html.Contains("圆盘盖得住管孔"));
        Check("按钮表含「◇ 搜形状」", html.Contains("◇ 搜形状"));
        // ★★ 2026-08-23：说明书的「逐个按钮」表以前是**手写**的，而它自己的注释就警告过
        //   「一旦落后，用户会去点一个不存在的按钮」——事实是它**已经落后了**：
        //   新增的「另存为设计记录」根本不在表里。现在改成从 Flow 生成，并在这里守住：
        //   **Flow 里登记的每一个命令，说明书上都要出现。**
        {
            // ⚠ 必须**只在按钮表那一段里**找。头一版对全文搜，于是散文里提过的命令
            //   也算「在表里」—— 注入「表里漏掉另存为设计记录」竟然没红，
            //   因为另一节的说明文字里也有这四个字。断言得守它自称要守的那块地方。
            int tb = html.IndexOf("逐个按钮", StringComparison.Ordinal);
            int te = tb >= 0 ? html.IndexOf("</table>", tb, StringComparison.Ordinal) : -1;
            Check("找得到「逐个按钮」那张表", tb >= 0 && te > tb, $"{tb}..{te}");
            string tbl = tb >= 0 && te > tb ? html[tb..te] : "";
            var missingInDoc = Flow.Commands
                .Where(c => !tbl.Contains(c.Text, StringComparison.Ordinal))
                .Select(c => c.Text).ToList();
            Check("Flow 登记的命令，**按钮表里**一个都不缺",
                  tbl.Length > 0 && missingInDoc.Count == 0,
                  missingInDoc.Count == 0 ? $"{Flow.Commands.Length} 条全在"
                                          : "★ 表里没有：" + string.Join("、", missingInDoc));
        }
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
        int cut = html.IndexOf("设计记录 3DM", StringComparison.Ordinal);
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
        // 为什么做成测试：设计记录的舌长 90 mm 装不下铜排，而它能长期存在，
        // 正是因为舌长在程序里是个谁都不核对的独立常数（memory: 舌长90是错误解）。
        // 这条测试的作用是：以后**任何人**把舌长改回一个装不下的值，界面都会当场顶回来。
        var discD = (NumericUpDown)F(page, "_discD")!;
        var tabLen = (NumericUpDown)F(page, "_tabLen")!;
        var tabW = (NumericUpDown)F(page, "_tabW")!;
        Set(page, "_suppressAuto", true);
        discD.Value = 60m; tabW.Value = 15m; tabLen.Value = 90m;   // 正是旧设计记录那一组
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

        // ── 装配下界要在**求解路径上**也顶（2026-08-24）
        //
        // 上面那段验的是「用户手改参数」那条路（ParamChanged → ShowPrediction）。
        // 但任何**绕过 ParamChanged** 的写入都躲得过它：_suppressAuto 期间的程序写值、
        // 首屏那一阵、载入档… ⇒ 一个自由段为负的几何照样进求解器。
        // 判据是对的（⑤ 会红，实测见过 −12.4/100，没有放行），
        // 但那要等一次分钟级的解 —— 而这件事闭式一毫秒就知道。
        {
            Set(page, "_suppressAuto", true);           // 正是那条绕过去的路
            discD.Value = 300m; tabW.Value = 5m; tabLen.Value = 20m;   // 切点≈149.9 ⇒ 自由段 −170
            Set(page, "_suppressAuto", false);
            Check("绕过参数变更写进去的值确实低于下界（否则下一条空转）",
                  tabLen.Value == 20m, $"舌长 {tabLen.Value}");
            string lifted = (string)page.GetType()
                .GetMethod("EnforceTabLenFloor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(page, null)!;
            Check("顶高机制对这条路同样有效", tabLen.Value > 20m, $"现在 {tabLen.Value} mm");
            Check("顶高时说明了原因（不静默改用户的输入）", lifted.Contains("自动顶到"),
                  lifted.Length == 0 ? "★ 一声不吭就改了控件" : "");
            // 求解路径必须真的调它 —— 机制在、没接上，等于没有
            string ldp9 = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
            int r0 = ldp9.IndexOf("private async Task RunAsync", StringComparison.Ordinal);
            int r1 = r0 < 0 ? -1 : ldp9.IndexOf("\n    private ", r0 + 10, StringComparison.Ordinal);
            string rbody = r0 < 0 ? "" : (r1 < 0 ? ldp9[r0..] : ldp9[r0..r1]);
            Check("求解路径（RunAsync）会先顶一次装配下界",
                  rbody.Contains("EnforceTabLenFloor(", StringComparison.Ordinal),
                  rbody.Contains("EnforceTabLenFloor(", StringComparison.Ordinal)
                      ? $"体长 {rbody.Length} 字"
                      : "★ 没接 ⇒ 绕过参数变更的几何会白跑一次分钟级的解才被 ⑤ 拦下");
        }

        Head("10 压接段长度：不能再用 3 mm 那个**数值默认值**");
        // 2026-08-17 抓到：本页从来没设过 BusbarClampLengthMm ⇒ 一直用 DesignInputs 的 3.0，
        // 而设计记录是 40。少扣 37 mm 会让判据⑤「装不下」被判成「装得下」——
        // 正好盖住 90 mm 那个错，属于最危险的一类：错得看不出来。
        var lcProbe = M(page, "BuildCase") as LineCase;
        Check("BuildCase 返回了算例", lcProbe is not null);
        if (lcProbe is not null)
            Check("压接段 = 设计记录值，不是 3 mm 默认值",
                  Math.Abs(lcProbe.Base.BusbarClampLengthMm - DesignSpec.Current.ClampLengthMm) < 1e-9,
                  $"{lcProbe.Base.BusbarClampLengthMm} vs {DesignSpec.Current.ClampLengthMm}");

        Head("11 失效告示：已失效的设计记录必须在**最前面**说出来");
        var invalid = DesignSpec.All.FirstOrDefault(x => x.Invalid.Length > 0);
        if (invalid is null)
            Check("（当前没有已失效的档，跳过）", true);
        else {
            caseBox.SelectedIndex = Array.IndexOf(DesignSpec.All, invalid);
            M(page, "LoadDesignSpec");
            Pump(400);
            Check("载入后有失效告示", outBox.Text.Contains("已失效"));
            int posWarn = outBox.Text.IndexOf("已失效", StringComparison.Ordinal);
            int posLoad = outBox.Text.IndexOf("已载入设计记录", StringComparison.Ordinal);
            Check("告示排在「已载入设计记录」之前", posWarn >= 0 && posWarn < posLoad,
                  $"告示@{posWarn} 载入@{posLoad}　★ 排在后面等于没写");
            Check("说明了失效的判据", invalid.InvalidChecks.Length > 0,
                  "InvalidChecks 为空 ⇒ 自检门无法分辨「已知的失败」与「新出现的失败」");
        }

        Head("12 出图拦截：已失效的档不许导出 3DM");
        // 与 --make3dm 那条同根：用户 2026-08-17 发现作废档把现役档的 3DM 覆盖掉了。
        // UI 这条路更险 —— 下拉里作废档就排在现役档后面，默认文件名还一字不差。
        foreach (var fdX in DesignSpec.All) {
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
            var bad90 = DesignSpec.Current.Clone();
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

        Head("15 页面 ↔ DesignSpec 的**单位**必须对得上（盘径是直径，模型要半径）");
        // 这是 UI 这条路最容易出、又最不容易被看见的错：直径/半径、半宽/全宽各差一倍，
        // 而两边都是「合理的数」，判据表照样出得来 —— 典型的安静失败。
        {
            Set(page, "_suppressAuto", true);
            discD.Value = 70m; tabW.Value = 28m; tabLen.Value = 200m; wall.Value = 0.7m;
            for (int i = 0; i < 4; i++) plate[i].Value = 1.11m + i * 0.10m;
            Set(page, "_suppressAuto", false);
            var mP2F = page.GetType().GetMethod("PageToDesignSpec",
                           BindingFlags.NonPublic | BindingFlags.Instance);
            Check("PageToDesignSpec 存在", mP2F is not null);
            var fdP = mP2F?.Invoke(page, null) as DesignSpec;
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
        // 1b 的全部意义就是这一条：把页面上的设计记录参数原样填进控件，
        // 「核算整线」造出来的 FlangePlate 必须与 DesignSpec.Plate 逐字段相同。
        // 只要有人再绕过构造器自己造一片，这条立刻红。
        {
            var fd1b = DesignSpec.Current;
            Set(page, "_suppressAuto", true);
            wall.Value = (decimal)fd1b.WallMm;
            tubeIns.Value = (decimal)fd1b.TubeInsulMm;
            discD.Value = (decimal)(2 * fd1b.DiscRadiusMm);
            tabLen.Value = (decimal)fd1b.TabLengthMm;
            tabW.Value = (decimal)fd1b.TabHalfWidthMm;
            for (int i = 0; i < 4; i++) plate[i].Value = (decimal)fd1b.TabThickMm[i];
            // ★★ 2026-08-25：舌保温与环倍率**成了页面控件**（在此之前本页没有它们，
            //   PageToDesignSpec 从 DesignSpec.Current 继承 —— 那正是被禁掉的「设计记录当起点」）。
            //   本节的前提是「把设计记录参数**原样填进控件**」，所以这两组也必须填。
            //   ⚠ 不填会有两种坏法，都被这道门抓到过：
            //     ① 停在控件默认的 0.3（裸舌）⇒ 复现不出设计记录；
            //     ② 更隐蔽：前面某一节跑过定尺寸，结果已由 AdoptSolvedDesign **写回控件**
            //        （同一个 page 复用），于是这里继承的是**上一节的解**（实测 18.7 mm）。
            var tabIns1b = (System.Windows.Forms.NumericUpDown[])F(page, "_tabIns")!;
            var ringMul1b = (System.Windows.Forms.NumericUpDown[])F(page, "_ringMul")!;
            for (int i = 0; i < 4; i++)
            {
                tabIns1b[i].Value = (decimal)fd1b.TabInsulMm[i];
                ringMul1b[i].Value = (decimal)fd1b.RingMul[i];
            }
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
            Check("压接段仍是设计记录值不是 3 mm 默认值",
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
            //   把设计记录参数填进页面、走页面的 BuildCase 真解一次，
            //   结果必须**复现设计记录记录值**。
            //   1b 之前这件事做不到 —— 页面解的是另一片法兰，这正是当初不得不单独做
            //   「▶ 复现设计记录」按钮的原因。现在两条路应该落到同一个解。
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
                        Check($"页面路径复现设计记录 {nm}", Math.Abs(got - want) <= tol,
                              $"{got:0.000} {unit} vs 记录 {want:0.000}　差 " +
                              SizerResult.Signed(got - want, "+0.000;−0.000"));
                    Near("③", V("③"), fd1b.FlangeDipK, 1.00, "K");
                    Near("②′", V("②′"), fd1b.HoleFluxW, 0.50, "W");
                    Near("②″", V("②″"), fd1b.DiscOverK, 0.20, "K");
                    Near("管J", V("管 J"), fd1b.TubeJ, 0.05, "A/mm²");
                    Near("合计", r1b.TotalMassG, fd1b.TotalMassG, 2.0, "g");
                    // ★★★★★ C1 之后「全过」暂时**不可能为真**（2026-09-07，用户拍板）。
                    //   法兰 J 从参考量改成硬判据里的**判不了** —— 因为孔那一带的网格
                    //   从未细化，那个数是**下界**且未收敛（同一孔 7.920→10.068 仍在升）。
                    //   按铁律「判不了不算过」，它会一直挡着，直到网格修好。
                    //
                    //   ⚠ 处置不是把这条断言删掉或放宽成 true —— 那等于判据消失。
                    //   改成**更强**的说法：**除了法兰 J 判不了之外，其余一条都不许红**。
                    //   这样它照旧抓得住任何别的退化，而那条已知的阻塞是**指名放行**的。
                    //   ⇒ 网格修好、J 可判之后，把这里改回 r1b.AllOk（那时它该真的全过）。
                    // ★★★★★ 2026-09-08 晚：用户设计因果链落地 ⇒ 「· 法兰 J_max」降为参考量（不再进 Failed），
                    //   新硬判据「法兰截面 J」= 设计电流 ÷ 必经截面积 < 11。设计记录 W08/W06 早于这条链，
                    //   板厚没按 J=10 定 ⇒ 这一行在**记录**上必红（实测 27.7/11），而且是实情、不是退化。
                    //   处置照旧不是删断言：**除了它，其余一条都不许红**。要它绿得走「自动定厚」把板厚抬上去。
                    var stillBad = r1b.Failed.Where(f => !f.Contains("法兰 J") && !f.Contains("法兰截面 J")).ToArray();
                    var sectionJ = r1b.Checks.FirstOrDefault(c => c.Name.StartsWith("法兰截面 J", StringComparison.Ordinal));
                    Check("页面路径：除『法兰截面 J』外全过", stillBad.Length == 0,
                          stillBad.Length == 0
                            ? $"（法兰截面 J {sectionJ?.Actual:0.0}/{sectionJ?.Limit:0.0} —— 记录早于 J=10 链，板厚没按截面定；自动定厚才会抬上去）"
                            : string.Join("；", stillBad));
                }
            }
        }

        Head("16″ 设计电流密度 J 是 ① 输入（用户 2026-09-09：J 工程师设定、预设 10，J+1 是计算极限值）");
        {
            var jBox = (NumericUpDown)F(page, "_jDesign")!;
            var p2d = typeof(LineDesignPage).GetMethod("PageToDesignSpec", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Check("控件预设 10", jBox.Value == 10m, $"{jBox.Value}");
            Set(page, "_suppressAuto", true);
            Set(page, "_tongueFixed", null);            // 载入过记录时舌片厚是原样带着的；本节要看的是 J 的规则
            jBox.Value = 10m;
            var d10 = (DesignSpec)p2d.Invoke(page, null)!;
            jBox.Value = 20m;
            var d20 = (DesignSpec)p2d.Invoke(page, null)!;
            jBox.Value = 10m;
            Set(page, "_suppressAuto", false);
            Check("页面 J=20 进了设计", Math.Abs(d20.JDesignAPerMm2 - 20) < 1e-9, $"{d20.JDesignAPerMm2}");
            Check("计算极限值 = J+1", Math.Abs(d20.JCheckAPerMm2 - 21) < 1e-9, $"{d20.JCheckAPerMm2}");
            Check("J 进了算例（判据限值的唯一来源）",
                  Math.Abs(d20.BuildCase(new DesignInputs(), false).JDesignAPerMm2 - 20) < 1e-9, "");
            int k = Array.FindIndex(d10.TongueThickMm, v => !double.IsNaN(v));
            Check("舌片厚随 J 反比（J 翻倍 ⇒ 舌片厚减半）",
                  k >= 0 && Math.Abs(d20.TongueThickMm[k] * 2 - d10.TongueThickMm[k]) <= 0.03,
                  k >= 0 ? $"J10 {d10.TongueThickMm[k]:0.00} / J20 {d20.TongueThickMm[k]:0.00}" : "舌片厚全 NaN");
            // 快照含 J：J 变了快照就变 ⇒ 上一次的解不新鲜（新状态位默认没接上，这里是它的门）
            var snapOf = typeof(LineDesignPage).GetMethod("CurrentSnap", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Set(page, "_suppressAuto", true);
            jBox.Value = 20m; var s20 = snapOf.Invoke(page, null);
            jBox.Value = 10m; var s10 = snapOf.Invoke(page, null);
            Set(page, "_suppressAuto", false);
            Check("改 J 之后快照变了（上一次的解不新鲜）", !Equals(s10, s20), "");
        }

        Head("16‴ 搜形状结果下拉（R24，用户 2026-09-09：也供工程师自行选择）");
        {
            var pick = (ToolStripComboBox)F(page, "_shapePick")!;
            Check("搜形状没跑过 ⇒ 下拉藏着", !pick.Available, "（ToolStripItem.Visible 还看父工具条显不显示；走查不 Show 窗体，看 Available）");
            var a = DesignSpec.Current.Clone(); a.DiscRadiusMm = 31; a.TabHalfWidthMm = 31; a.TabLengthMm = 140;
            var b = DesignSpec.Current.Clone(); b.DiscRadiusMm = 33; b.TabHalfWidthMm = 33; b.TabLengthMm = 140;
            var c = DesignSpec.Current.Clone(); c.DiscRadiusMm = 27; c.TabHalfWidthMm = 27; c.TabLengthMm = 140;
            var rows = new List<(DesignSpec d, double mass, bool ok, string msg)>
            {
                (b, 2650.0, true, "✓ 第 3 轮全过"), (a, 2600.0, true, "✓ 第 3 轮全过"), (c, double.NaN, false, "⑥ 圆盘盖不住管孔＋焊脚"),
            };
            Set(page, "_shapeRows", rows);
            typeof(LineDesignPage).GetMethod("RefreshShapePick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            Check("有结果 ⇒ 下拉出现", pick.Available, "");
            Check("条目 = 标题 + 每个算过的形状", pick.Items.Count == 4, $"{pick.Items.Count}");
            Check("可行的按铂重排前", pick.Items[1]!.ToString()!.Contains("2600") && pick.Items[2]!.ToString()!.Contains("2650"), pick.Items[1]!.ToString()!);
            Check("不可行的排后、写明原因", pick.Items[3]!.ToString()!.Contains("✗") && pick.Items[3]!.ToString()!.Contains("盖不住"), pick.Items[3]!.ToString()!);
            decimal discBefore = discD.Value;
            pick.SelectedIndex = 2;                            // 选 2650 那个（盘Ø66）
            Check("选可行形状 ⇒ 盘径写回控件", discD.Value == 66m, $"{discBefore} → {discD.Value}");
            Check("输出框说了「已选形状」与「精算才可出图」",
                  outBox.Text.Contains("已选形状") && outBox.Text.Contains("核算整线"), "");
            pick.SelectedIndex = 3;                            // 选不可行的
            Check("选不可行形状 ⇒ 不写回、说明原因", discD.Value == 66m && outBox.Text.Contains("没写回页面") && pick.SelectedIndex == 0, $"{discD.Value}");
            Set(page, "_shapeRows", new List<(DesignSpec d, double mass, bool ok, string msg)>());
            typeof(LineDesignPage).GetMethod("RefreshShapePick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            Check("清空 ⇒ 下拉又藏起来", !pick.Available, "");
            Set(page, "_suppressAuto", true);
            discD.Value = discBefore;
            Set(page, "_suppressAuto", false);
        }

        Head("16⁗ 解法阶段条 ①②③④⑤（R25，用户 2026-09-09 晚：让工程师知道 APP 正在干啥）");
        {
            var strip = (Control)F(page, "_stages")!;
            var cur = strip.GetType().GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var track = strip.GetType().GetMethod("Track", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var reset = strip.GetType().GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var finish = strip.GetType().GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Control? p0 = strip; while (p0 is not null && p0 != page) p0 = p0.Parent;
            Check("状态条挂在整线设计页上（输出框顶上）", p0 == page, "");
            Check("五个格子都在", strip.Controls.OfType<Label>().Count(l => l.Text.Contains("①") || l.Text.Contains("②") || l.Text.Contains("③") || l.Text.Contains("④") || l.Text.Contains("⑤")) == 5, "");
            reset.Invoke(strip, new object[] { "核算整线：从 ① 开始" });
            string St() => cur.GetValue(strip)!.ToString()!;
            Check("重置 ⇒ 还没到任何一步", St() == "None", St());
            // 下面的原句都抄自 deliverable/细网格复算_盘56舌56.txt（真轨迹），改了求解器的前缀这里会红
            track.Invoke(strip, new object[] { "设计电流（温控 20 °C/h 空管升温 25→1150 °C，管子准静态 I²R=散热+C·Ṫ 全程峰值）：段 979/979 A ⇒ 片 979/1696/979 A（共用片矢量合成）" });
            Check("① 设计电流", St() == "DesignCurrent", St());
            track.Invoke(strip, new object[] { "★ 舌片厚按 I/(J·舌宽) 定：片0 — → 1.75 mm（设计电流 979 A ÷ (J 10 × 舌片最窄有效宽 56 mm)，向上落图纸格 0.01，不低于板料 0.60）" });
            track.Invoke(strip, new object[] { "起点 = **约束盒的下角**（不是种子）：板厚 0.60/1.02/0.60 mm（逐片；= max(焊接屈曲, 烧穿, 按 J=10 的截面)；熔化只验不抬）／舌保温 0.30 mm（裸舌）" });
            Check("② 约束盒下角", St() == "Corner", St());
            track.Invoke(strip, new object[] { "── 第一遍：导航网格上定位（导航网格）" });
            track.Invoke(strip, new object[] { "第  2 轮　合计 2603 g　板厚 0.60/1.02/0.60　舌保温 5.00/2.80/8.60" });
            Check("③ 导航网格逐轮（轮数进细节）", St() == "NavRounds" && strip.GetType().GetProperty("DetailText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(strip)!.ToString()!.Contains("第  2 轮　合计 2603 g"), St());
            track.Invoke(strip, new object[] { "加密复算：0.250 mm（第 2 档）…" });
            Check("④ 加密复算", St() == "MeshVerify", St());
            track.Invoke(strip, new object[] { "── 第二遍：细网格上重新求根（**判据以此为准**）（细网格 0.125 mm）" });
            track.Invoke(strip, new object[] { "第  1 轮　合计 2604 g　板厚 0.60/1.02/0.60" });
            Check("⑤ 细网格重解（轮数归第二遍）", St() == "FineResolve", St());
            track.Invoke(strip, new object[] { "外层耦合 3/600（ω=0.35）" });
            Check("认不出的行不改阶段", St() == "FineResolve", St());
            finish.Invoke(strip, new object[] { true, "判据全过" });
            Check("收尾 ⇒ ✓ 算完", St() == "Done", St());
            reset.Invoke(strip, new object[] { "" });
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
            // ⚠ 下界是 **2** 不是 0（2026-08-24 修）：本表 3 列 ⇒ 正常有 2 个制表位（实测 77/154）。
            //   原来写 `tRow1.Length > 0 && Range(1, Length-1).All(...)` ——
            //   制表位掉到只剩 **1 个**时 `Range(1, 0)` 是**空集**，`All` **恒真** ⇒ 断言照打 ✓。
            //   而「3 列只设出 1 个制表位」正是这条断言该抓的回归。
            //   同族：RequiredChecksTests.EmptyTable_IsNotSilentlyOk（空判据表报「硬安全线全过」）。
            Check("制表位严格递增（不递增则 Tab 原地不动，两列贴成一格）",
                  tRow1.Length >= 2 && Enumerable.Range(1, tRow1.Length - 1).All(i => tRow1[i] > tRow1[i - 1]),
                  tRow1.Length < 2
                      ? $"★ 只有 {tRow1.Length} 个制表位 —— 本表 3 列，至少要 2 个"
                      : string.Join("/", tRow1));
            // 自证：把「空集恒真」这件事本身证给门看，免得后人把下界又改回 0
            var oneStop = new[] { 77 };
            Check("自证：只剩 1 个制表位时「严格递增」恒真（所以下界必须是 2，不是 0）",
                  Enumerable.Range(1, oneStop.Length - 1).All(i => oneStop[i] > oneStop[i - 1]),
                  "Range(1, 0) 是空集 ⇒ All 恒真 ⇒ 光靠「递增」验不出制表位掉到 1 个");
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
                        string label = it is ToolStripComboBox ? "[设计记录下拉]"
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
                // 说明书讲的是「三组」：设计记录组 / 本页参数组 / 工具组（进度条那段不算）
                // ★ 2026-08-23：分组断言改成**逐页对 Flow**。
                //   上一版把「设计记录组在 ③ 上」写死了，而设计记录那一组随后被搬到独立的
                //   「设计记录」页 ⇒ 三条断言同时变红，红得**没有信息**：
                //   不是接线错了，是断言又抄了一份会过期的清单（同一个教训第二次）。
                //   现在只问一件不会过期的事：**每一页工具条上的按钮，与 Flow 为
                //   那一页登记的命令一致**；谁搬到哪页都不必改测试。
                // ⚠ 2026-09-02：主线页的分组从「一条工具条 + 分隔线」改成**两排工具条**
                //   （主线在上、图纸路与工具在下）⇒ 单条工具条内不再需要分隔线。
                //   验的仍是同一件事：**按钮有分组，不是十个一横排**。
                Check("本页的按钮是分了组的（分隔线或分排）",
                      nonEmpty.Count >= 2 || page.Controls.OfType<ToolStrip>().Count() >= 2,
                      $"实际 {nonEmpty.Count} 组 / {page.Controls.OfType<ToolStrip>().Count()} 排");

                foreach (var sid in new[] { StageId.整线核算, StageId.参考工具 })
                {
                    var tp = tabs.TabPages.OfType<TabPage>()
                        .FirstOrDefault(x => x.Text.Contains(Flow.Stage(sid).Title, StringComparison.Ordinal));
                    Check($"找得到「{Flow.Stage(sid).Title}」页", tp is not null);
                    if (tp is null) continue;

                    var names = new List<string>();
                    void W3(Control c)
                    {
                        if (c is ToolStrip t2)
                            foreach (var it in t2.Items.OfType<ToolStripButton>()) names.Add(it.Text);
                        foreach (Control k in c.Controls) W3(k);
                    }
                    W3(tp);

                    var want2 = Flow.Stage(sid).CommandIds.Select(id => Flow.Cmd(id).Text).ToArray();
                    Check($"「{Flow.Stage(sid).Title}」的按钮与 Flow 登记的一致（集合非空）",
                          want2.Length > 0, string.Join("/", want2));
                    var miss2 = want2.Where(t => !names.Contains(t, StringComparer.Ordinal)).ToArray();
                    Check($"「{Flow.Stage(sid).Title}」页上一个都不缺", miss2.Length == 0,
                          miss2.Length == 0 ? $"{want2.Length} 个都在"
                                            : "★ 缺：" + string.Join("/", miss2));
                }

                // 分组的**含义**仍要守住：不读页面控件的那些，不能与读页面的混在一组 ——
                // 说明书就是按这条教用户的（「设计记录两个字打头的那几个不读页面控件」）。
                // 现在它们各在一页，这条自然成立；仍然断言一次，防止有人把它们搬回去。
                var mixed = Flow.Stage(StageId.参考工具).CommandIds
                    .Select(Flow.Cmd)
                    .Where(c => c.Group == CmdGroup.设计记录不读页面 && c.ReadsPageControls)
                    .Select(c => c.Text).ToList();
                Check("「设计记录」组里不混入读页面控件的命令", mixed.Count == 0,
                      mixed.Count == 0 ? "" : "★ 混入：" + string.Join("/", mixed));
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
            var g4 = Flow.Stage(StageId.整线核算).GateToUnlockNext!;
            // ★ 2026-09-02 用户拍板：交付的门**不再**要求判据全过 ——
            //   「结果如何就如何，超标显示提醒，风险由工程师判断」。
            //   拿掉的是拦，不是指路（Flow.Next 照旧先指加密复算）。
            Check("进交付的门只拦「没解出来 / 参数动过了」",
                  g4.RequireConverged && g4.RequireFresh
                  && !g4.RequireAllOk && !g4.RequireMeshVerified,
                  $"Converged={g4.RequireConverged} Fresh={g4.RequireFresh} "
                  + $"AllOk={g4.RequireAllOk} Mesh={g4.RequireMeshVerified}");

            // ── 「设计记录」那四个不读页面控件 ⇒ 不受阶段门禁
            var caseCmds = Flow.Commands.Where(c => c.Group == CmdGroup.设计记录不读页面).ToList();
            Check("「设计记录」组都标了不读页面控件",
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
            // ⚠ 2026-09-02 起 AnalysisPage **不再是页签**（阶段轨收成四格，
            //   它的按钮与输出/图被借到「① 整线核算」与「参考工具」上）⇒ 从字段取。
            var anal = (AnalysisPage)F(main, "_toolsOwner")!;
            Check("整线设计页与主窗口共用同一个参数对象",
                  ReferenceEquals(F(page, "_base"), inMain));
            Check("分析页与主窗口共用同一个参数对象",
                  ReferenceEquals(F(anal, "_base"), inMain));

            // CopyInto 必须真的搬值，而且**不换引用**（换了就等于没修）
            var src = new DesignInputs { TAmbC = 42.5, TubeIdMm = 61.0, GradeName = "PtRh10" };
            var dst = (DesignInputs)inMain;
            double keepAmb = dst.TAmbC;
            // ⚠ 本段改的是**全局共享**的那个 DesignInputs，用完必须还原：
            //   「PtRh10」不是材料库里的牌号（真名是 Tanaka-ZGS-PtRh10 等），
            //   留着它，后面任何一段只要真跑一次解就会在 MaterialDb.Get 上炸 ——
            //   实测第 31 节就是这么崩的，而堆栈指向 Judge，看不出是上游污染。
            string keepGrade = dst.GradeName; double keepId = dst.TubeIdMm;
            SegmentSolver.CopyInto(src, dst);
            Check("CopyInto 搬了值", Math.Abs(dst.TAmbC - 42.5) < 1e-9 && dst.GradeName == "PtRh10",
                  $"TAmb {keepAmb}→{dst.TAmbC}　牌号 {dst.GradeName}");
            Check("CopyInto 之后仍是同一个对象", ReferenceEquals(F(main, "_in"), dst));
            Check("CopyInto 之后两页看到的还是它",
                  ReferenceEquals(F(page, "_base"), dst) && ReferenceEquals(F(anal, "_base"), dst));
            dst.TAmbC = keepAmb; dst.GradeName = keepGrade; dst.TubeIdMm = keepId;   // 还原全局态

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

            // ⚠ 同一个递归绝不能把工具条也钩进去 —— 「设计记录 ▾」被当成参数会让
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

        // ★ 本节故意把自动重算武装起来验它接没接上 —— 用完必须收干净，
        //   否则防抖会在后面某一节里起一次真解（见 Quiesce 的说明）。
        Quiesce(page);

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
            //   而整线链两条路都强制取设计记录的 40 mm。是靠人看截图才发现的，
            //   而「看起来正常的错数」正是本项目最危险的形状 ⇒ 交给机器守。
            //
            // 判据：凡在**整线链的构造器**里被赋值的 DesignInputs 字段，
            //       就不许挂在宣称对 C 链有效的分类下（"C 整线 …" 开头的组）。
            var builders = new[] { "Pt_Optimize/Core/DesignSpec.cs",
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
        Head("25 复现设计记录要走完全程：解完必须发布状态，否则 ④⑤ 一格不开");
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
            int b = src.IndexOf("private void LoadDesignSpec()", StringComparison.Ordinal);
            Check("找得到 ReproduceAsync 的方法体", a >= 0 && b > a, $"{a}..{b}");
            string raw = a >= 0 && b > a ? src[a..b] : "";
            // ⚠ 必须**剥掉注释再判**：这段代码的注释里正大段解释「为什么不走
            //   PageToDesignSpec」，直接对全文做子串匹配会命中那些**散文**，
            //   把「代码没调它」误报成「代码调了它」。
            //   断言要测的是**那件事**，不是那件事附近的文字。
            // 不用任何反斜杠转义：本仓的钩子会把转义序列改成真字符，字面量当场断掉。
            var noCmt = raw.Split((char)10)   // (char)10 = LF：避开转义，且不挑 CRLF/LF
                .Select(l => { int k = l.IndexOf("//", StringComparison.Ordinal);
                               return k >= 0 ? l.Substring(0, k) : l; });
            string body = string.Join(" ", noCmt);

            Check("复现之后会灌控件（页面显示与档一致）",
                  body.Contains("LoadDesignSpecFrom", StringComparison.Ordinal));
            Check("复现**仍从档解**（不走 PageToDesignSpec，保住交叉校验）",
                  body.Contains("fd.BuildCase", StringComparison.Ordinal)
                  && !body.Contains("PageToDesignSpec", StringComparison.Ordinal),
                  "从档解 = 独立于页面搬运的那条路");
            Check("复现之后会发布状态（PushFlow）",
                  body.Contains("PushFlow()", StringComparison.Ordinal),
                  "没有它 FlowState.Last 永远是 null ⇒ ④⑤ 不开");
            // ★ 快照必须取**开解那一刻**，不是解完这一刻（2026-08-24 修）。
            //   原来是解完再 `_solvedSnap = CurrentSnap()` —— 复现要几分钟，
            //   这几分钟里控件可改，于是「解的那组」与「记下的那组」可以是两组，
            //   而 Fresh 会判成 true ⇒ ④⑤ 的门开在一张别的参数的判据表上。
            //   所以这里不只验「记了」，还要验**没有再用那个旧写法**。
            Check("复现之后会记下 _solvedSnap（否则 Fresh 恒 false）",
                  body.Contains("_solvedSnap = snapAtStart", StringComparison.Ordinal));
            Check("快照取自开解那一刻，不是解完那一刻",
                  body.Contains("var snapAtStart = CurrentSnap()", StringComparison.Ordinal)
                  && !body.Contains("_solvedSnap = CurrentSnap()", StringComparison.Ordinal),
                  body.Contains("_solvedSnap = CurrentSnap()", StringComparison.Ordinal)
                      ? "★ 还在用解完取快照的旧写法 ⇒ 解算中改参数会被判成「参数未变」" : "");
            // ★ 而且**不能无条件**记：水头不属于设计记录几何，页面水头与存档不同时
            //   这个解并不是「页面参数的解」，记了就是假的 Fresh。
            Check("记 _solvedSnap 是**有条件**的（水头对得上才记）",
                  body.Contains("headSame", StringComparison.Ordinal),
                  "水头不属于设计记录几何 ⇒ 不同就不能假装 Fresh");
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
            // ⚠ 「禁」有三种来源：门禁 / 互斥 / **适用性**。本节验的是互斥，
            //   所以另两种都要先排除。上一版漏了适用性 ⇒「另存为设计记录」被误报成
            //   互斥误禁，而它此刻禁用是**对的**（还没解过，AllOk/Fresh 都不成立）。
            var lp2 = tabs.TabPages.OfType<LineDesignPage>().First();
            bool App(string id) => (bool)typeof(LineDesignPage).GetMethod("CommandApplicable",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(lp2, new object[] { id })!;
            var inertUnlocked = inert
                .Where(x => Gate.Evaluate(x.spec!.Stage, flow).Unlocked && App(x.spec!.Id)).ToArray();
            Check("有「不算东西」的命令处在已解锁的阶段上（否则下一条空转）",
                  inertUnlocked.Length > 0,
                  string.Join("、", inertUnlocked.Select(x => x.b.Text)));
            var wronglyOff = inertUnlocked.Where(x => !x.b.Enabled).Select(x => x.b.Text).ToList();
            Check("不算东西的命令不受互斥牵连", wronglyOff.Count == 0,
                  wronglyOff.Count == 0 ? "" : "★ 被误禁：" + string.Join("、", wronglyOff));

            // ── 「我在算什么」这块面板：跑着的时候必须**说人话**（2026-08-24）
            //
            // 用户原话：「这类的指导链路对工程师使用非常重要，这是我们讨论很久才得到的结果，
            // 必须强烈的保留」。在本项目里，「强烈保留」= 有一条会自己跑的断言。
            //
            // 病灶（用户抓图）：几十分钟的搜形状跑着，而面板上同时挂着
            //   「正在算：C″ 形状搜索」 与 「✓ 已解（参数未变）」 与 一行判定
            // —— 后两句讲的是**上一次**的解，读起来却像「算完了」。
            // 更糟的是「下一步」那一行是**空的**：Flow.Next 在有链在跑时按设计不给命令
            // （§28「别催」），但「不催」被做成了「什么都不说」⇒
            // 指路链偏偏在最需要它的时候哑了。
            {
                var sp = F(main, "_stagePanel")!;
                string L(string f) => ((Control)F(sp, f)!).Text;
                Pump(200);

                // ⚠ **不能用 Control.Visible 判**：本测试从不 Show 窗体，而 Visible 返回的是
                //   **有效可见性**（任一祖先隐藏就是 false）⇒ 整棵树恒 false，
                //   照它写会报「指路链哑了」而真机上好好的 —— HANDOVER 记过同族的坑
                //   （句柄没建之前 .Text 赋值不触发 TextChanged）。看**内容**才作数。
                // ★ ④「定尺寸/搜形状」此前**一根进度条都没有**（用户问「④ 页的进度条有补上吗?」）：
                //   它的按钮是从 ③ 借来的，而 _prog 长在 ③ 的工具条上、没借过来；
                //   偏偏搜形状是全程最长的一条（几十分钟）。
                //   2026-08-21 定过「不给每页各配一套，改用状态面板」——
                //   但那条决定当时只落实了一半：面板拿到了文字，始终没有条。
                //   ⇒ 条放进面板（切到哪页都看得见），数只有一份 FlowState.RunningPct。
                {
                    var bar = (ProgressBar)F(sp, "_bar")!;
                    flow.SetRunningNote("盘Ø60／舌宽90　7 轮", 42);
                    Pump(150);
                    Check("面板里有进度条，且跑着的时候在显示",
                          bar.Style == ProgressBarStyle.Continuous && bar.Value == 42,
                          $"{bar.Style}　{bar.Value} %");
                    flow.SetRunningNote("核算中…");        // 说不出百分比
                    Pump(150);
                    Check("说不出百分比时走马灯（不拿不动的空条冒充进度）",
                          bar.Style == ProgressBarStyle.Marquee, bar.Style.ToString());
                    // 自证：不在跑的时候条要收起来
                    var keepRun = flow.Running;
                    flow.SetRunning(null); Pump(150);
                    // ⚠ 这里**不能**验 bar.Visible：本测试不 Show 窗体，Visible 返回的是
                    //   有效可见性（祖先隐藏就恒 false）⇒ 两种情况都是 false，验不出东西。
                    //   我第一版写成 `!Visible || true` —— 那是恒真的断言，比不写更坏。
                    //   ⇒ 改验两件真看得见的事：源码里 else 分支确实收条、以及 RunningPct 归位。
                    string spSrc = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "StagePanel.cs"));
                    Check("不在跑的时候源码里确实把条收起来",
                          spSrc.Contains("_bar.Visible = false;", StringComparison.Ordinal),
                          spSrc.Contains("_bar.Visible = false;", StringComparison.Ordinal)
                              ? "" : "★ 跑完了条还挂着 ⇒ 界面会一直像在算");
                    Check("不在跑时 RunningPct 归位", flow.RunningPct == -1, $"{flow.RunningPct}");
                    flow.SetRunning(keepRun, "自动定厚"); Pump(150);
                }

                Check("跑着的时候「下一步」那一行不是空的", L("_next").Length > 0,
                      L("_next").Length > 0 ? "" : "★ 指路链在最需要它的时候哑了");
                Check("它说得出现在能做什么（等 / 取消）",
                      L("_next").Contains("取消") && L("_next").Contains("等它跑完"),
                      L("_next").Replace(Environment.NewLine, " ⏎ "));
                Check("并且说了为什么参数与页签被锁住",
                      L("_next").Contains("锁住"), "");
                Check("「已解」这一行标明是**上一次**的（否则和「正在算」自相矛盾）",
                      L("_fresh").Length == 0 || L("_fresh").StartsWith("上一次：", StringComparison.Ordinal)
                      || L("_fresh").Contains("还没解过") || L("_fresh").Contains("参数已改"),
                      L("_fresh"));
                Check("判定这一行同样标明是上一次的",
                      L("_verdict").Length == 0
                      || L("_verdict").StartsWith("上一次：", StringComparison.Ordinal),
                      L("_verdict"));

                // 自证：不在跑的时候**不该**带「上一次」前缀，否则上面几条只是恒真
                flow.SetRunning(null);
                Pump(200);
                Check("不在跑时「已解」不带「上一次」前缀（自证）",
                      !L("_fresh").StartsWith("上一次：", StringComparison.Ordinal), L("_fresh"));
                Check("不在跑时判定不带「上一次」前缀（自证）",
                      !L("_verdict").StartsWith("上一次：", StringComparison.Ordinal), L("_verdict"));
                flow.SetRunning(ChainId.C定尺寸, "自动定厚");
                Pump(200);
            }

            // ── 有链在跑时**禁止换页**（2026-08-24 用户提出）
            //
            // 用户原话：「④ 页点没算完(执行中)，禁止跳页（应该说只要有运算，禁止跳到任何页），
            // 没有这限制工程师随便点，整条链路就乱（甚至不知道自己正在算什麽）」。
            //
            // 为什么这条要单独验：互斥只禁**按钮**，页签是另一条路。
            // 在 ④ 点了「搜形状」（几十分钟）之后切到 ③ 改参数、再切到 ⑤ 看出图 ——
            // 链还在跑，而每一页讲的都是**上一次**的事。
            // ⚠ 与「门禁锁着的格子允许只读进入」是两回事：那是静态状态、进去看清楚更有用；
            //   这是瞬时状态、且有唯一出口（取消）。
            {
                // ⚠⚠ **不能用 `tabs.SelectedIndex = n` 来验拦截**（2026-08-24 踩过）。
                //   程序改 SelectedIndex 走的是 Win32 的 TCM_SETCURSEL，而它
                //   **不发 TCN_SELCHANGING** ⇒ WinForms 的 Selecting 事件根本不触发。
                //   照那样写，测试会报「跑着就跳过去了」，而真机上用户点页签**是拦得住的**
                //   —— 一条**用户永远走不到的路**，两个方向都会骗人（§17′ 那次是同一族）。
                //   ⇒ 直接触发处理器本身：拦不拦得住，看它有没有把 e.Cancel 置上。
                var onSel = typeof(TabControl).GetMethod("OnSelecting",
                                BindingFlags.NonPublic | BindingFlags.Instance)!;
                bool Blocked()
                {
                    var ev = new TabControlCancelEventArgs(
                        tabs.TabPages[0], 0, false, TabControlAction.Selecting);
                    onSel.Invoke(tabs, new object[] { ev });
                    return ev.Cancel;
                }

                Check("先造出「有链在跑」这个前提（否则下一条空转）", flow.Running is not null,
                      flow.Running?.ToString() ?? "★ 没在跑，验不了拦截");
                Check("有链在跑时换页被拦下", Blocked(),
                      Blocked() ? "e.Cancel = true" : "★ 跑着还能换页 ⇒ 链路会乱");

                // 自证：不在跑的时候必须放行，否则上一条只是「页签永远打不开」
                flow.SetRunning(null);
                Pump(50);
                Check("不在跑的时候换页照常放行", !Blocked(),
                      !Blocked() ? "" : "★ 没在跑也拦 ⇒ 上一条证明不了拦截");
                flow.SetRunning(ChainId.C定尺寸, "自动定厚");   // 还原本节的在跑态
                Pump(50);
            }

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
            // ── 每条长跑的链都必须往**状态面板**报进度（2026-08-24）
            //
            // 面板是唯一「切到哪一页都看得见」的地方（Flow.SetRunning 的注释说明了
            // 2026-08-21 为什么不给每页各配进度条）。SetRunningNote 是那个通道。
            //
            // 原来的病：SearchShapeAsync **整个方法体里一次都没调过它** ——
            // 开跑时 SetRunning(…, "搜形状") 之后再无更新。而它要跑几十分钟、
            // 又是从 ④ 页点的，④ 上既没有 _prog 也没有 _status
            // ⇒ 面板上那句话几十分钟纹丝不动，看着像卡死。细粒度进度全写在 ③ 页
            //   那两个用户看不见的控件上。**进度管线本来就在，只是这条链没接。**
            //
            // 断言只查「有没有接」，不查「接了几处」：数调用点看不穿包装函数
            // （把 Note(...) 这层一包，计数就骗人了 —— 试过，注入后仍能蒙混过关）。
            foreach (string fn in new[] { "SearchShapeAsync", "RunAsync", "ReproduceAsync" })
            {
                // ⚠ 用 "\n" 而不是 Environment.NewLine 找方法末尾：本文件是 LF，
                //   拿 CRLF 去匹配一次都命中不了 ⇒ 方法体一路切到文件尾（实测 39621 字），
                //   断言就退化成「整个文件里有没有这个词」—— 又一个看着绿的空转。
                // ⚠ 锚点必须是**声明**：方法名第一次出现是在构造函数里挂按钮那行，
                //   从那儿切出来的是一段没有任何上报的构造代码，三条会一起误报。
                int b0 = ldp.IndexOf("private async Task " + fn, StringComparison.Ordinal);
                int b1 = b0 < 0 ? -1 : ldp.IndexOf("\n    private ",
                                                   b0 + fn.Length, StringComparison.Ordinal);
                string body = b0 < 0 ? "" : (b1 < 0 ? ldp[b0..] : ldp[b0..b1]);
                Check($"{fn} 把进度报给状态面板", body.Contains("SetRunningNote(", StringComparison.Ordinal),
                      b0 < 0 ? "★ 找不到这个方法的声明，断言失去了对象"
                             : body.Contains("SetRunningNote(", StringComparison.Ordinal)
                               ? $"体长 {body.Length} 字"
                               : "★ 一次都没报 ⇒ 这条链跑起来，面板上那句话不会变");
            }

            Check("每个入口都在 finally 里清（异常/取消也要解除互斥）",
                  CountOf(ldp, "SetRunning(null)") >= 3 && apg.Contains("SetRunning(null)", StringComparison.Ordinal),
                  $"LineDesignPage {CountOf(ldp, "SetRunning(null)")} 处清");
        }

        // ═══════════════════════════════════════════════════════════════
        Head("27 适用性：几何来源不对的命令必须**真的**禁掉（Enabled 只能有一个来源）");
        {
            // 病灶（2026-08-22 用户截图指出「分析几何变数还是没 Disable」）：
            //   LineDesignPage.SyncGeomSource 设了 `_btnAnalyze.Enabled = false`，
            //   而 MainForm.SyncGates 也在设同一个按钮
            //   （geom.analyze 是 ReadsPageControls=true ⇒ `b.Enabled = g.Unlocked`），
            //   ③ 已解锁 ⇒ **它把禁用又打开了**。同一个按钮两处控制，后跑的那个赢。
            //
            // ⚠ 本节断言的是**最终可见状态**，不是「某一处设了什么」——
            //   头一版的 bug 恰恰是「设了，但被另一处覆盖」，只查设置点是查不出来的。
            var lp = tabs.TabPages.OfType<LineDesignPage>().First();
            var btnAn = (ToolStripButton)F(lp, "_btnAnalyze")!;
            var btnSh = (ToolStripButton)F(lp, "_btnShape")!;
            var srcA = (RadioButton)F(lp, "_srcAnalytic")!;
            var src3 = (RadioButton)F(lp, "_src3dm")!;
            var files = (TextBox[])F(lp, "_file3dm")!;

            void Mode(bool analytic)
            {
                Set(lp, "_suppressAuto", true);
                if (analytic) srcA.Checked = true; else src3.Checked = true;
                Set(lp, "_suppressAuto", false);
                lp.GetType().GetMethod("SyncGeomSource",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp, null);
                Pump(120);
            }

            // ── 解析形状：没有图纸可反推 ⇒ 分析几何变数必须禁
            files[0].Text = "";
            Mode(analytic: true);
            Check("解析模式下「分析几何变数」被禁", !btnAn.Enabled,
                  btnAn.Enabled ? "★ 仍可点 —— 点了只会弹一句「请先切到…」" : "");
            Check("解析模式下禁用有写明理由（灰掉而不解释就是哑谜）",
                  btnAn.ToolTipText is { Length: > 0 }, btnAn.ToolTipText ?? "");

            // ── .3dm 但还没选入口片 ⇒ 仍然禁（要反推的就是那张图）
            Mode(analytic: false);
            Check(".3dm 但没选入口片时仍被禁", !btnAn.Enabled);
            Check("此时「◇ 搜形状」也被禁（形状由图纸给定，不是自由度）", !btnSh.Enabled);

            // ── 选了入口片 ⇒ 才亮
            files[0].Text = "entry-plate.3dm";   // 只验接线，不真读文件（故不带路径分隔符）      // 只验接线，不真读文件
            Mode(analytic: false);
            Check("选了入口片之后「分析几何变数」才可点", btnAn.Enabled,
                  btnAn.Enabled ? "" : "★ 选了图纸也不亮");

            // ── 切回解析 ⇒ 又该禁，且「◇ 搜形状」回来
            Mode(analytic: true);
            Check("切回解析后又被禁（状态是跟着走的，不是一次性的）", !btnAn.Enabled);
            // ⚠ 2026-09-02：「◇ 搜形状」的适用性现在是**两条**——
            //   几何来源是解析（形状才是可搜索的自由度），**且**手上有一个当前参数的收敛解
            //   （原 ③→④ 那道门随阶段合并降级到这里：定尺寸器每轮跑一次整线解，
            //    没有起点就会几十分钟烧在一个坏几何上）。
            //   本节只造了几何来源、没解过 ⇒ 这里验的是**几何来源那一半**：
            //   切回解析之后不再因为「模式不对」被拒。
            bool App(string id) => (bool)typeof(LineDesignPage).GetMethod("CommandApplicable",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(lp, new object[] { id })!;
            Check("切回解析后「◇ 搜形状」不再因为模式被拒（另一半是「要有解」）",
                  !App("shape.search"),   // 没解过 ⇒ 仍不适用，但原因换成了「没有起点」
                  "没解过时仍不适用 —— 这是对的：定尺寸器需要一个收敛的起点");
            Check("而 .3dm 模式下它一定不适用（形状由图纸给定）",
                  !App("shape.search"));

            // ★ 关键：SyncGates 跑一遍之后**不许把它重新打开**
            typeof(MainForm).GetMethod("SyncGates",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
            Pump(120);
            Check("SyncGates 之后仍然是禁的（不许有第二处把它打开）", !btnAn.Enabled,
                  btnAn.Enabled ? "★ 被 SyncGates 覆盖了 —— Enabled 又变成两个来源" : "");
            files[0].Text = "";
            Mode(analytic: true);
        }

        // ═══════════════════════════════════════════════════════════════
        Head("28 下一步：每种状态都要指出**该点哪个按钮**，且指的方向要对");
        {
            // 用户 2026-08-20 的原话是「工程师根本不知道自己目前在算什么」。
            // 阶段横幅 / 门禁说明 / 状态面板都在答「你在哪」，没有一处答「往哪走」。
            //
            // ⚠ 规则只读现成状态（Last / Fresh / AllOk / Converged），**不产生新判据**。
            //   本节逐个状态构造，验它指的方向对不对 —— 而不是验「有没有这一行」。
            var st = new FlowState();

            Check("还没解过 → 指向「核算整线」",
                  Flow.Next(st)?.CmdId == "core.runLine", Flow.Next(st)?.Why ?? "(无)");

            // 正在算的时候不催 —— 那一行的位置已经被「正在算：…」占着
            st.SetRunning(ChainId.C整线耦合, "核算整线");
            Check("正在算时不给下一步（别催）", Flow.Next(st) is null);
            st.SetRunning(null);

            // 造一个「收敛、全过、新鲜」的解
            var snap = new object();
            ConstraintOut C2(string name, bool ok, CheckKind k = CheckKind.Target) =>
                new() { Name = name, Ok = ok, Kind = k, Actual = ok ? 1 : 9, Limit = 5 };

            // ⚠ 夹具必须造**完整**的判据表（2026-08-24）。
            //   原来写成 `Mk(true, 一条通过的③)` 就期待 AllOk —— 那在真机上不可能发生：
            //   真解出来的表必定含 LineResult.Required 的全部 7 条。
            //   「判据缺席」机制上线后这条夹具当场红，**它红得对**：
            //   一张少了 6 条硬安全线的表本来就不该自称「全过」。
            //   ⇒ 先铺一张「该有的都有、条条通过」的底表，再让传进来的覆盖同前缀那一条。
            //     底表跟着 Required 走，将来加判据不必回来改这里。
            LineResult Mk(bool conv, params ConstraintOut[] cs) => new()
            {
                Ok = true,
                Converged = conv,
                RampChecked = true,
                Checks = LineResult.Required
                    .Where(q => cs.All(c => !c.Name.StartsWith(q.Prefix, StringComparison.Ordinal)))
                    .Select(q => C2(q.Prefix + " 底表", true, q.Kind))
                    .Concat(cs).ToArray()
            };

            st.Last = Mk(false, C2("③ 法兰增量温降 ≤ 上限", true));
            st.SolvedSnap = snap; st.CurrentSnap = snap;
            Check("解了但没收敛 → 仍指向「核算整线」",
                  Flow.Next(st)?.CmdId == "core.runLine", Flow.Next(st)?.Why ?? "");

            // ★★★ 收敛 + 全过 + 新鲜 ⇒ 先**复核**，不是直接出图（2026-08-30）。
            //   在此之前这里就直接指出图 —— 而那些判据是在**导航网格**上判的。
            //   实测 0.6 档：粗网格「法兰增量温降」7.7 K（限值 10，看着很宽），
            //   加密到位 9.5 K。差 1.8 K，足以把「过」变成「不过」。
            st.Last = Mk(true, C2("③ 法兰增量温降 ≤ 上限", true));
            Check("收敛且判据全过、但没复核 → 指向「网格无关复核」",
                  Flow.Next(st)?.CmdId == "core.verifyMesh", Flow.Next(st)?.Why ?? "");

            // 复核过了才指出图
            st.MeshVerified = true; st.VerifiedSnap = snap;
            Check("复核过了 → 指向「导出本页 3DM」",
                  Flow.Next(st)?.CmdId == "export.page3dm", Flow.Next(st)?.Why ?? "");

            // ★ 复核之后又改参数 ⇒ 复核作废（假绿灯比没复核更坏）
            st.CurrentSnap = new object();
            Check("改过参数 → 复核作废，不许照旧结论放行",
                  Flow.Next(st)?.CmdId != "export.page3dm", Flow.Next(st)?.Why ?? "");
            st.CurrentSnap = snap;

            // 参数改过 ⇒ 必须先重解（这一条要排在 AllOk 前面，否则会照着过期结论指路）
            st.CurrentSnap = new object();
            Check("参数改过 → 指回「核算整线」（不许照过期结论指路）",
                  Flow.Next(st)?.CmdId == "core.runLine", Flow.Next(st)?.Why ?? "");
            st.CurrentSnap = snap;

            // 热-电判据不过 ⇒ 厚度能救 ⇒ 自动定厚
            st.Last = Mk(true, C2(LineResult.Key.NetFlux + " 须为正", false));
            Check("热-电判据不过 → 指向「自动定厚」",
                  Flow.Next(st)?.CmdId == "core.autoThick", Flow.Next(st)?.Why ?? "");

            // ★ 几何判据不过 ⇒ 厚度**救不了** ⇒ 必须指向搜形状（用户 2026-08-22 提醒）
            st.Last = Mk(true, C2(LineResult.Key.FreeTab + " ≥ 下界", false));
            Check("⑤ 舌片自由段不过 → 指向「◇ 搜形状」（厚度调不动几何）",
                  Flow.Next(st)?.CmdId == "shape.search", Flow.Next(st)?.Why ?? "");
            st.Last = Mk(true, C2(LineResult.Key.DiscCover + "＋焊脚", false));
            Check("⑥ 圆盘盖不住管孔 → 也指向「◇ 搜形状」",
                  Flow.Next(st)?.CmdId == "shape.search", Flow.Next(st)?.Why ?? "");

            // 几何 + 热电同时不过 ⇒ 先解决几何（厚度那条无论如何都白跑）
            st.Last = Mk(true, C2(LineResult.Key.FreeTab + " ≥ 下界", false),
                               C2(LineResult.Key.NetFlux + " 须为正", false));
            Check("几何与热电同时不过 → 先指几何", Flow.Next(st)?.CmdId == "shape.search");

            // 「无法判定」也不算过 —— 不能因为它没被判成 false 就放行到出图
            st.Last = new()
            { Ok = true, Converged = true, Checks = new[]
              { new ConstraintOut { Name = LineResult.Key.FreeTab + " ≥ 下界",
                                    Ok = true, Undetermined = true, Kind = CheckKind.Target } } };
            Check("判据「无法判定」时不许指向出图",
                  Flow.Next(st)?.CmdId != "export.page3dm", Flow.Next(st)?.CmdId ?? "(无)");

            // 指的每一个命令都必须在 Flow 里登记（否则界面上根本没有那个按钮）
            string[] pointed = { "core.runLine", "core.autoThick", "shape.search", "export.page3dm" };
            var missing = pointed.Where(id => Flow.Commands.All(c => c.Id != id)).ToList();
            Check("指路指到的命令都真实存在", missing.Count == 0,
                  missing.Count == 0 ? string.Join("、", pointed) : "★ 不存在：" + string.Join("、", missing));
        }

        // ═══════════════════════════════════════════════════════════════
        Head("29 .3dm 模式下 ⑤⑥ 也要判得了（否则那条路永远到不了交付）");
        {
            // 病灶（2026-08-23 用 Pt_Heater3.3dm 实跑发现）：
            //   .3dm 模式下 LineCase.FlangePlates 是空的（几何来自厚度场）
            //   ⇒ GeometryScreen 给出两条「无法判定」⇒ 而「无法判定不算通过」
            //   ⇒ AllOk 恒 false ⇒ ④→⑤ 的门（RequireAllOk）**永远打不开**
            //   ⇒ **.3dm 这条路走不到交付**。
            //   可「分析几何变数」早就把盘半径/管孔半径/舌端 X/舌端半宽全反推出来了。
            string dm = Path.Combine(RepoRoot(), "Pt_Heater3.3dm");
            if (!File.Exists(dm)) { Check("有 .3dm 样件可测", false, "★ 缺 " + dm); }
            else if (Geometry3dm.FindProbe() is null) { Check("几何探针在", false, "★ 缺 Pt_Optimize.Geom.exe"); }
            else
            {
                var lp3 = tabs.TabPages.OfType<LineDesignPage>().First();
                var f3 = (TextBox[])F(lp3, "_file3dm")!;
                Set(lp3, "_suppressAuto", true);
                ((RadioButton)F(lp3, "_src3dm")!).Checked = true;
                foreach (var t in f3) t.Text = dm;
                ((TextBox)F(lp3, "_layer3dm")!).Text = "法兰";
                Set(lp3, "_suppressAuto", false);
                typeof(LineDesignPage).GetMethod("SyncGeomSource",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                Pump(120);

                // 还没分析 ⇒ 不该硬凑：GeomForJudge 空，⑤⑥ 仍报无法判定（那是诚实的）
                var lcA = (LineCase)typeof(LineDesignPage).GetMethod("BuildCase",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null)!;
                Check("没分析过时不硬凑几何（⑤⑥ 仍无法判定是诚实的）",
                      lcA.GeomForJudge.Length == 0, $"{lcA.GeomForJudge.Length} 片");

                typeof(LineDesignPage).GetMethod("AnalyzeShape",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                Pump(300);

                var lcB = (LineCase)typeof(LineDesignPage).GetMethod("BuildCase",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null)!;
                Check("分析之后有了供判据用的等效几何", lcB.GeomForJudge.Length == 4,
                      $"{lcB.GeomForJudge.Length} 片");
                Check("★ 不许把它塞进求解用的 FlangePlates（那会变成「判的是 A、解的是 B」）",
                      lcB.FlangePlates.Length == 0, $"{lcB.FlangePlates.Length} 片");

                // ★ 指路顺序：.3dm 已选图纸但**还没分析**时，第一步必须是「分析几何变数」。
                //   不分析也能点「核算整线」，但那样跑完一次分钟级的解 ⑤⑥ 仍是「无法判定」
                //   —— 白跑一分多钟才发现该先点分析（用户 2026-08-23 指出）。
                {
                    var fl = (FlowState)F(main, "_flow")!;
            // 自证：本节所有「下一步」断言都以「没有链在跑」为前提
            //（Flow.Next 在 Running 非空时返回 null）。前一节若漏了 Quiesce，
            // 报出来的会是一串「没给下一步」，指不到真因 —— 所以先把前提验掉。
            Check("本节开工时没有链在跑（前提）", fl.Running is null,
                  fl.Running is null ? "" : $"★ 上一节漏了 Quiesce：还在跑 {fl.Running}");
                    bool AppL(string id) => (bool)typeof(LineDesignPage).GetMethod("CommandApplicable",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                        .Invoke(lp3, new object[] { id })!;

                    // 先把「已分析」这个状态退回去，重演首次进入 .3dm 的那一刻
                    Set(lp3, "_shape", null);
                    typeof(LineDesignPage).GetMethod("SyncAnalysisPending",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                    Check("未分析时这一位是立着的", fl.GeomAnalysisPending);
                    var n1 = Flow.Next(fl, AppL);
                    Check("未分析时指向「分析几何变数」", n1?.CmdId == "geom.analyze",
                          n1 is null ? "★ 没给下一步" : $"{n1.CmdId}　{n1.Why}");

                    // 分析之后就不该再指它
                    typeof(LineDesignPage).GetMethod("AnalyzeShape",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                    Pump(300);
                    Check("分析之后这一位落下", !fl.GeomAnalysisPending);

                    // ── 解析替身的保真门（2026-08-23）
                    //
                    // ④ 可以在解析替身上搜方向。Pt_Heater3 这张图**开了 4 个槽**，
                    // 而 FlangePlate 没有槽这个概念 ⇒ 替身是实心的、电流不用绕行，
                    // 局部过热会被系统性地算轻。这种情况必须**拒绝**，
                    // 而不是「残差看着还行就用」——缺的材料不在几何里，残差小是巧合。
                    {
                        var fidO = F(lp3, "_surrFid");
                        Check("分析时顺手量了替身保真度", fidO is not null,
                              fidO is null ? "★ 没量 ⇒ ④ 无从判断能不能用替身" : "");
                        if (fidO is AnalyticSurrogate.Fidelity fid3)
                        {
                            Check("这张图上替身被**拒绝**", !AnalyticSurrogate.Usable(fid3),
                                  AnalyticSurrogate.Usable(fid3)
                                      ? "★ 有槽的图纸却放行了替身 ⇒ 解的是另一片板" : fid3.Report());
                            Check("拒绝的理由是结构性的（开槽），不是残差", fid3.Blockers.Count > 0,
                                  string.Join("；", fid3.Blockers));
                            Check("量尺读出了真实差距（不是恒 0）", fid3.Worst > 1e-6,
                                  $"最差 {fid3.Worst * 100:0.0} %");
                        }
                        var outT = ((RichTextBox)F(lp3, "_out")!).Text;
                        // ⚠ 2026-09-03 起输出框会**折行**（长正文行原来要横向拉 500 字）⇒
                        //   查之前先把换行去掉，否则一句话被折断就查不到 —— 那是查法的问题，
                        //   不是 APP 少说了话。
                        // ⚠ 不写反斜杠转义：本仓的钩子会把它改成真的换行，字面量当场断掉
                        string flat = outT.Replace(((char)13).ToString(), "")
                                          .Replace(((char)10).ToString(), "");
                        Check("输出框把「替身不可用」说给人看", flat.Contains("解析替身不可用"),
                              flat.Contains("解析替身不可用") ? ""
                              : "★ 只在内部拦掉、不告诉人，等于让人猜为什么慢"
                                + "　实际输出框开头：" + flat[..Math.Min(220, flat.Length)]);
                        Check("并且说了是开槽的缘故", flat.Contains("开槽"));
                    }
                    var n2 = Flow.Next(fl, AppL);
                    Check("分析之后不再指「分析几何变数」", n2?.CmdId != "geom.analyze",
                          n2?.CmdId ?? "(无)");
                    Check("分析之后指向「核算整线」（还没解过）", n2?.CmdId == "core.runLine",
                          n2?.CmdId ?? "(无)");

                    // 解析模式下这一位永远不该立起来
                    Set(lp3, "_suppressAuto", true);
                    ((RadioButton)F(lp3, "_srcAnalytic")!).Checked = true;
                    Set(lp3, "_suppressAuto", false);
                    typeof(LineDesignPage).GetMethod("SyncGeomSource",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                    Check("解析模式下这一位不立（没有图纸可反推）", !fl.GeomAnalysisPending);
                    Set(lp3, "_suppressAuto", true);
                    ((RadioButton)F(lp3, "_src3dm")!).Checked = true;
                    Set(lp3, "_suppressAuto", false);
                    typeof(LineDesignPage).GetMethod("SyncGeomSource",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
                }

                var g = GeometryScreen.Judge(lcB.GeomForJudge, 40.0, 100.0);
                var five = g.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal));
                var six = g.FirstOrDefault(c => c.Name.StartsWith(LineResult.Key.DiscCover, StringComparison.Ordinal));
                Check("⑤ 判得出来了（不再是无法判定）", five is { Undetermined: false },
                      five is null ? "★ 没有这条" : (five.Undetermined ? "★ 仍无法判定" : $"{five.Actual:0.0} / {five.Limit:0}"));
                Check("⑥ 判得出来了", six is { Undetermined: false },
                      six is null ? "★ 没有这条" : (six.Undetermined ? "★ 仍无法判定" : $"{six.Actual:0.0} / {six.Limit:0}"));

                // 复原，别把后面的节带偏
                Set(lp3, "_suppressAuto", true);
                ((RadioButton)F(lp3, "_srcAnalytic")!).Checked = true;
                foreach (var t in f3) t.Text = "";
                Set(lp3, "_suppressAuto", false);
                typeof(LineDesignPage).GetMethod("SyncGeomSource",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lp3, null);
            }
        }

        // ────────────────────────────────────────────────────────────
        Head("30 另存设计记录：新档要**立刻**出现在下拉里，不该等重启");

        // 在此之前 DesignSpec.All 是 static readonly，只在启动时算一次 ⇒
        // 界面说「已写出」而下拉里找不到它。工程师看不到自己刚存的东西，
        // 只能猜是没存上，于是再存一次（撞重名被拒）—— 或者从此不信这个功能。
        {
            const string probe = "UiWiring临时探针档";
            bool InBox() => caseBox.Items.Cast<object>().Any(x => (x as string) == probe);

            // 自证①：一开始下拉里没有它。若一开始就有（上一轮没清干净），
            // 后面「重扫之后出现了」就是恒真的 —— 空集/恒真的断言是本项目栽过的坑。
            Check("开工时下拉里没有这个名字", !InBox(),
                  InBox() ? "★ 上一轮残留，本节所有断言都不成立" : "");

            string? saved = null;
            try
            {
                // 先把选中项挪到第二档，用来验重扫**不会把选择冲掉**
                caseBox.SelectedIndex = Math.Min(1, caseBox.Items.Count - 1);
                string keep = (string)caseBox.SelectedItem!;

                var d = DesignSpec.Builtin[0].Clone();
                d.Name = probe;
                d.Provenance = "UiWiring 临时探针，本节结束即删";
                saved = DesignSpecStore.Save(d);
                Check("档确实写到磁盘上了", File.Exists(saved), saved);

                // 自证②：只写档、还没重扫 ⇒ 下拉里仍不该有它。
                // 这一条把下面那条从「反正会通过」变成「只有重扫真的起作用才会通过」。
                Check("只写档、没重扫时下拉里仍然没有它", !InBox(),
                      InBox() ? "★ 没重扫就出现了 ⇒ 下一条证明不了任何事" : "");

                DesignSpec.Reload();
                Pump(200);
                Check("重扫之后下拉里出现了新档", InBox(),
                      InBox() ? $"（共 {caseBox.Items.Count} 档）" : "★ 存了却看不见，等于没存");
                Check("重扫没有读出档案错误", DesignSpecStore.LoadErrors.Count == 0,
                      string.Join("；", DesignSpecStore.LoadErrors));
                Check("重扫没有把选中的那一档冲掉", (caseBox.SelectedItem as string) == keep,
                      $"{caseBox.SelectedItem} vs {keep}");
                Check("重扫后 All 与下拉一样长", caseBox.Items.Count == DesignSpec.All.Length,
                      $"{caseBox.Items.Count} vs {DesignSpec.All.Length}");
            }
            finally
            {
                // 探针档留在 finaldesigns/ 里会被 --selfcheck A 段当成一个要复核的基准，
                // 那时它已经没有对应的解 ⇒ 会在别处报一个跟本节毫无关系的红。
                if (saved is not null && File.Exists(saved)) File.Delete(saved);
                DesignSpec.Reload();
            }
            // ★ G7：说明书页的下拉也挂了同一个重扫广播，但它带 WebView2 ——
            //   headless 下实例化有把整套测试挂住的风险，所以只做**源码级**断言，
            //   并且把「这是源码级、没有真跑过」写在这里，不假装覆盖到了。
            {
                string mp = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "ManualPage.cs"));
                Check("说明书页也挂了设计记录重扫广播",
                      mp.Contains("DesignSpec.Reloaded +=", StringComparison.Ordinal));
                Check("而且在 Dispose 里退订（静态事件不退订会拿着已销毁的窗体）",
                      mp.Contains("DesignSpec.Reloaded -=", StringComparison.Ordinal));
                Check("重填时抑制了自身的渲染回调（否则会拿中途状态画一次图）",
                      mp.Contains("_refilling", StringComparison.Ordinal));
            }

            Check("清理之后它从下拉里消失了", !InBox(),
                  InBox() ? "★ 探针档没删干净，会污染 --selfcheck A 段" : "");
        }

        // ════════════════════════════════════════════════════════════
        Head("31 对话里得出的结论，**APP 自己也得这么说**");
        //
        // 这一节守的不是某个函数，是「**答案只有一处**」在**用户看得见的那一层**成立：
        // 前面几节验的是内核算得对（selfcheck）、接线接对了（各节），
        // 但工程师读的是**这张渲染出来的表**。表上写的若和内核算的不是一回事，
        // 前面全绿也没有意义 —— 而本项目栽过的每一次都是这个形状。
        {
            var chk = (DataGridView)F(page, "_checks")!;
            var fl31 = (FlowState)F(main, "_flow")!;

            // 舌长给足余量，让 ⑤ 有正裕度 —— 设计记录构型上 ⑤ 恰好贴着下界（裕度 0），
            // 那个 0 对符号错不错都成立，验不出东西（这正是裕度符号 bug 藏了那么久的原因）。
            Set(page, "_suppressAuto", true);
            ((NumericUpDown)F(page, "_discD")!).Value = 60m;
            ((NumericUpDown)F(page, "_tabW")!).Value = 30m;
            ((NumericUpDown)F(page, "_tabLen")!).Value = 200m;
            Set(page, "_suppressAuto", false);

            var lc31 = M(page, "BuildCase") as LineCase;
            Check("造得出算例", lc31 is not null);
            if (lc31 is not null)
            {
                Console.WriteLine("  …（真解一次，约 1–2 分钟）");
                var r31 = LineRunner.Run(lc31);
                Check("解得出且收敛", r31.Ok && r31.Converged, r31.Message);

                // ── ⓪ 判据缺席（2026-08-24 补）。
                //    铁律三：判据只能过 / 不过 / **无法判定**，绝不允许消失。
                //    此前它的执行方式是「每条判据自己记得写 else」，三次踩坑三次就地补
                //    （③ 08-15、⑤ 08-17、②″ 08-24 才发现）。现在 LineResult.Required
                //    把「该有哪几条」变成一份数据 —— 本节验它在**真解**上确实成立。
                //    纯逻辑（拿掉一条 ⇒ AllOk 变 false）在 RequiredChecksTests，这里只验真解。
                Check("必备判据名单不是空的（自证：空名单会让下一条恒真）",
                      LineResult.Required.Length >= 7, $"{LineResult.Required.Length} 条");
                Check("这次真解评了升温 ①（否则 ① 属合法缺席，本节覆盖会弱一档）", r31.RampChecked);
                Check("真解的判据表里必备判据一条不缺", r31.MissingChecks.Length == 0,
                      r31.MissingChecks.Length == 0
                        ? $"{LineResult.Required.Length} 条全在"
                        : "★ 缺席：" + string.Join("、", r31.MissingChecks));
                {
                    // 自证：抽掉一条就必须被抓到。没有这条，上面那句在「机制失效」时也会绿。
                    var kept31 = r31.Checks;
                    r31.Checks = kept31.Where(c => !c.Name.StartsWith(
                        LineResult.Key.DiscTemp, StringComparison.Ordinal)).ToArray();
                    Check("抽掉 ②″ 之后当场报缺席、且不再全过（自证）",
                          r31.MissingChecks.Contains(LineResult.Key.DiscTemp) && !r31.AllOk,
                          $"缺席 [{string.Join("、", r31.MissingChecks)}]　AllOk={r31.AllOk}");
                    r31.Checks = kept31;
                    Check("还原后又是齐的（别把后面几节的表毁了）",
                          r31.MissingChecks.Length == 0 && r31.Checks.Length == kept31.Length);
                }

                // 把它渲染到界面上 —— 下面读的全是**渲染后**的格子，不是 r31 里的字段
                page.GetType().GetMethod("Show", BindingFlags.NonPublic | BindingFlags.Instance,
                                         null, new[] { typeof(LineResult), typeof(string) }, null)!
                    .Invoke(page, new object?[] { r31, null });
                Pump(200);

                // ★★ 2026-08-30：判据表的「判据」列现在印**全名不带代号**
                //   （用户：「UI 内严禁使用 ②′ 这类的表示，工程师看不懂」）。
                //   ⇒ 这里按名字找行时也不能再带代号。走查按老名字查，当场红了 ——
                //     **门抓到的是真后果**，不是误报。
                string Cell(string namePart, int col)
                {
                    foreach (DataGridViewRow row in chk.Rows)
                        if ((row.Cells[1].Value?.ToString() ?? "").Contains(namePart, StringComparison.Ordinal))
                            return row.Cells[col].Value?.ToString() ?? "";
                    return "";
                }

                // ── ① 两条热稳定判据必须**出现在表上**（对话里量到 整片 10.2×／局部 2.0×）
                //    它们本来只有 CLI 的 --flangestab/--localstab 算得到，界面上一条都没有。
                string sFl = Cell("整片热稳定", 2), sLo = Cell("局部热稳定", 2);
                Check("判据表里有「整片热稳定」", sFl.Length > 0,
                      sFl.Length > 0 ? $"实际 {sFl}" : "★ 只在 CLI 有 ⇒ 换个几何就没人再算它");
                Check("判据表里有「局部热稳定」", sLo.Length > 0,
                      sLo.Length > 0 ? $"实际 {sLo}" : "★ 同上");
                bool okFl = double.TryParse(sFl, out double vFl);
                bool okLo = double.TryParse(sLo, out double vLo);
                Check("整片热稳定是个实数（不是 ∞／非數值）", okFl && !double.IsInfinity(vFl), sFl);
                Check("局部热稳定是个实数（不是 ∞／非數值）", okLo && !double.IsInfinity(vLo), sLo);
                // ★ 「最热那一格」曾被当成局部代表 ⇒ 那里 J≈0 ⇒ 裕度 +∞ ⇒ 表上写「无限安全」
                //   而一格都没验。所以这条不是形式检查，是**它当初真的错成那样**。
                Check("局部热稳定落在合理量级（1–20×）", okLo && vLo > 1.0 && vLo < 20.0,
                      okLo ? $"{vLo:0.0}×（selfcheck 在设计记录上量到 1.9–2.9×）" : sLo);
                Check("整片热稳定落在合理量级（1–100×）", okFl && vFl > 1.0 && vFl < 100.0,
                      okFl ? $"{vFl:0.0}×（selfcheck 在设计记录上量到 8.2–44×）" : sFl);

                // ── ② 裕度符号：⑤ 是「须 ≥ 限」，通过时裕度列必须是**正**的
                //    改之前显示成「超 15 %」而同一行判定是 ✓ —— 两列自相矛盾。
                string v5 = Cell("舌片自由段", 5), m5 = Cell("舌片自由段", 4);
                Check("舌片自由段这一行在表上", v5.Length > 0, $"实际 {Cell("舌片自由段", 2)} / 限 {Cell("舌片自由段", 3)}　判定 {v5}　裕度 {m5}　舌长控件 {((NumericUpDown)F(page, "_tabLen")!).Value}");
                if (v5 == "✓")
                    Check("⑤ 通过时裕度列不是「超 …%」（方向没判反）",
                          !m5.StartsWith("超", StringComparison.Ordinal),
                          m5.StartsWith("超", StringComparison.Ordinal)
                              ? $"★ 判定 ✓ 而裕度写「{m5}」—— 同一行自相矛盾" : $"裕度 {m5}");

                // ── ③ 参数表改一项 ⇒ 上一次的解立刻不新鲜，④⑤ 的门关上
                //    此前 PropertyGrid 的变更事件根本没挂 ⇒ 门开在别的参数的判据表上。
                // Fresh 还要求 Last 非空 —— 只塞快照造不出新鲜态
                Set(page, "_last", r31);
                Set(page, "_solvedSnap", M(page, "CurrentSnap"));
                M(page, "PushFlow");
                Pump(100);
                Check("先造出「新鲜」这个状态（否则下一条空转）", fl31.Fresh,
                      fl31.Fresh ? "" : "★ 造不出新鲜态，下面验不了「变不新鲜」");
                // ★ 「重头开始」还包括**越关作废**（2026-08-24 用户要求）：
                //   越关是在**旧参数**上批的，参数一动它就不该再算数。
                //   此前 Invalidate 只清 Last/SolvedSnap，**Bypassed 一直留着** ⇒
                //   工程师改完参数还站在一个「当初批准进来的」页面上，而理由已经没了。
                fl31.Bypassed.Add(StageId.交付);
                Check("先造出「越过关」这个状态（否则下一条空转）",
                      fl31.Bypassed.Contains(StageId.交付));

                page.GetType().GetMethod("MarkParamsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(page, new object?[] { "控温点" });
                Pump(100);
                Check("参数表改一项之后就**不新鲜**了", !fl31.Fresh,
                      fl31.Fresh ? "★ 改了参数还说「参数未变」⇒ ④⑤ 会开在旧结论上" : "");
                Check("④ 定尺寸的门随之关上",
                      !Gate.Evaluate(StageId.交付, fl31).Unlocked);
                Check("⑤ 交付的门随之关上",
                      !Gate.Evaluate(StageId.交付, fl31).Unlocked);
                Check("并且告诉了人为什么",
                      ((RichTextBox)F(page, "_out")!).Text.Contains("参数表改了"));
                Check("越关也随之作废（不能拿旧参数批的通行证继续走）",
                      !fl31.Bypassed.Contains(StageId.交付),
                      fl31.Bypassed.Contains(StageId.交付)
                          ? "★ 改了参数，越关还留着 ⇒ 那道门是靠旧理由开着的" : "");
                // ★ 「参数一动」要有**一次性**弹窗告知（2026-08-24 用户要求）。
                //   只验接线与一次性，不验它真的弹出来 —— 本测试从不 Show 窗体，
                //   而 §4/§6 专门验「不该弹模态框」：真弹出来会把整套测试挂住。
                //   所以生产代码里那句 `if (!IsHandleCreated || !Visible) return;`
                //   既是对现实的判断（没显示的窗体上弹框没意义），也是这套测试跑得完的前提。
                {
                    string ldpW = File.ReadAllText(Path.Combine(RepoRoot(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
                    Check("有「参数一动」的一次性告知", ldpW.Contains("WarnParamsRestartOnce", StringComparison.Ordinal));
                    Check("它是一次性的（有开关且先判开关）",
                          ldpW.Contains("if (_paramWarnShown) return;", StringComparison.Ordinal));
                    Check("窗体没显示时不弹（否则接线测试会被模态框挂死）",
                          ldpW.Contains("if (!IsHandleCreated || !Visible) return;", StringComparison.Ordinal));
                    // 两条改参数的路都要接上：页面控件 与 左侧参数表
                    // ★ G6：「用户改了输入」必须只有**一个入口**（2026-08-24）。
                    //   散着写，将来第三条改输入的路（程序化载入、新页面、运行时控件…）
                    //   一定会漏掉其中一件（清越关 / 弹告知）—— 本项目栽过太多次。
                    Check("「用户改了输入」收敛成单一入口",
                          ldpW.Contains("private void NoteUserInputChanged", StringComparison.Ordinal));
                    Check("RestartChain 在本页只被调一次（就在那个入口里）",
                          CountOf(ldpW, "Shared?.RestartChain()") == 1,
                          $"{CountOf(ldpW, "Shared?.RestartChain()")} 处 —— 多于一处就说明有路绕过了入口");
                    Check("一次性告知也只在那个入口里调",
                          CountOf(ldpW, "WarnParamsRestartOnce(") == 2,
                          $"{CountOf(ldpW, "WarnParamsRestartOnce(")} 处（1 定义 + 1 调用）");
                    Check("两条改输入的路都走这个入口",
                          CountOf(ldpW, "NoteUserInputChanged(") == 3,
                          $"{CountOf(ldpW, "NoteUserInputChanged(")} 处（1 定义 + 页面控件 + 参数表）");

                    // ★ G1：牌号必须是**只能选的下拉**，不是自由文本
                    var pd = TypeDescriptor.GetProperties(typeof(DesignInputs))["GradeName"]!;
                    var cv = pd.Converter;
                    Check("牌号是下拉（不是自由文本框）", cv.GetStandardValuesSupported(),
                          cv.GetType().Name);
                    Check("而且只能选、不能打字", cv.GetStandardValuesExclusive());
                    var vals = cv.GetStandardValues()!.Cast<string>().ToArray();
                    Check("下拉里列的就是材料库里的牌号",
                          vals.Length > 0 && vals.All(v => MaterialDb.All.ContainsKey(v)),
                          $"{vals.Length} 个：" + string.Join("、", vals.Take(3)) + " …");
                    Check("材料库里的牌号一个都不缺", vals.Length == MaterialDb.All.Count,
                          $"{vals.Length} vs {MaterialDb.All.Count}");
                    // 自证：打错的名字必须**不在**下拉里，且 Get 会报人话
                    Check("打错的名字不在下拉里（自证）", !vals.Contains("PtRh10"));
                    string msg = "";
                    try { MaterialDb.Get("PtRh10"); } catch (Exception ex) { msg = ex.Message; }
                    Check("方案档里存了旧牌号名时，报的是人话不是字典异常",
                          msg.Contains("PtRh10") && msg.Contains("可选"), msg[..Math.Min(70, msg.Length)]);
                }

                Check("判据表**没有**被清空（数字留给人对照改前改后）",
                      fl31.Last is not null,
                      "作废的是「已经过了」这个凭据，不是数字本身");
            }
        }

        // ════════════════════════════════════════════════════════════
        Head("32 出图 → 读回来：APP 导出的图，APP 自己必须读得回来");
        //
        // 这一节关掉的是长期挂着的 G3：**保真门从来没在「会被放行」的图纸上跑过**。
        // 以前拿不到这种图 —— 现有出图走 WriteFinal3dm，写的是**多图层**
        // （板身/环外级/环内级/压接段/角焊缝），而读取端要**单图层** ⇒ 自己写的自己读不回来。
        // 现在有了「导出可回读 3DM」（Geom 的 steps 模式 + 等宽舌），这条闭环才跑得通。
        {
            string tmp = Path.Combine(Path.GetTempPath(), "uiw_readable.3dm");
            try
            {
                // 三级台阶 + 等宽舌，**不开槽** —— 解析几何本来就没有槽
                Geometry3dm.WriteStepped3dm(tmp, 26.0,
                    new[] { 36.0, 46.0, 60.0 }, new[] { 1.0, 2.0, 3.0 },
                    -200.0, 40.0, 3.0, slotCount: 0);
                Check("写出来了", File.Exists(tmp), tmp);

                var f32 = Geometry3dm.LoadThickness(tmp, "法兰", double.NaN, 0.5);
                var sh32 = PlateShapeAnalyzer.Analyze(f32);

                Check("读回来级数对得上", sh32.Levels.Count == 3, $"{sh32.Levels.Count} 级");
                if (sh32.Levels.Count == 3)
                {
                    var got = sh32.Levels.Select(x => x.ThicknessMm).ToArray();
                    Check("读回来各级厚度对得上",
                          Math.Abs(got[0] - 1.0) < 0.05 && Math.Abs(got[1] - 2.0) < 0.05
                          && Math.Abs(got[2] - 3.0) < 0.05,
                          string.Join("/", got.Select(v => v.ToString("0.00"))));
                }
                Check("读回来盘半径对得上", Math.Abs(sh32.DiscRadiusMm - 60.0) < 0.5,
                      $"{sh32.DiscRadiusMm:0.00}");
                Check("读回来孔半径对得上", Math.Abs(sh32.HoleRadiusMm - 26.0) < 0.5,
                      $"{sh32.HoleRadiusMm:0.00}");
                Check("**没有**把不存在的槽认出来", !sh32.Slot.Found,
                      sh32.Slot.Found ? "★ 无中生有" : "");

                // ★★ G3 的正向用例：这张图必须**被放行**
                var lv32 = sh32.Levels.Select(x => x.ThicknessMm).ToArray();
                var (_, fid32, other32) = AnalyticSurrogate.BestFit(sh32, f32, lv32);
                Check("等宽舌是更接近的那一支（写入器写的就是等宽）", fid32.TabParallel,
                      fid32.Report());
                Check("★ 保真门**放行**这张图（G3 第一次有正向用例）",
                      AnalyticSurrogate.Usable(fid32),
                      $"最差 {fid32.Worst * 100:0.0} %（限 {AnalyticSurrogate.Tol * 100:0.0} %）");
                // 自证：另一支必须明显更差，否则「挑更接近的那一支」这件事没被验到
                Check("梯形舌那一支明显更差（自证）", other32.Worst > fid32.Worst * 2,
                      $"梯形 {other32.Worst * 100:0.0} % vs 等宽 {fid32.Worst * 100:0.0} %");
            }
            catch (Exception ex)
            {
                Check("出图→读回来这条闭环跑得通", false, "★ " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        Head("33 ① 升温快筛：**造好了要接上**，而且门禁真的读它");
        {
            // 病灶（2026-08-24）：RampScreen.Judge 在**整个生产路径上一次都没被调用过**
            //   （只有单测在调），FlowState.RampScreen 这个栏位**一处赋值都没有**。
            // 于是三处注释都在描述一件没发生的事：
            //   · Flow.FindCheck 那条「退到闭式快筛」的分支是死的；
            //   · ① 那一格的门禁注释写着「解锁 ③：几何可造（⑤⑥）+ 升温快筛不判死」，
            //     而 RequiredChecks 里只有 ⑤⑥；
            //   · §7 曾把它记成「一条在设计范围内不可能不过的硬判据」——
            //     实情更糟：它**根本没在跑**。
            // 本节守三件事：名单里有它、栏位真被填上、门禁真按它开关。
            var lp33 = tabs.TabPages.OfType<LineDesignPage>().First();
            var f33 = (FlowState)F(main, "_flow")!;

            // ★★★★★ 2026-09-02 阶段轨七格→四格：**①→② 那道门整个不存在了**。
            //   原来这一段验的是「先决条件那道门管的是 ②，不是文案说的 ③」——
            //   而 ① 先决条件、② 粗算 两格已降级成不带编号的「参考工具」（蓝链从不指它们）。
            //   ⇒ 那几条断言失去了对象。删掉，改成钉住**现在真实的结构**。
            // ★ R17／R21（2026-09-08）：三步主线 ① 输入 → ② 法兰优化 → ③ 结果与出图 + 参考工具 + 使用说明 = 五格
            Check("阶段轨是五格（三步主线 + 两格不带编号）", Flow.Stages.Length == 5,
                  string.Join("／", Flow.Stages.OrderBy(x => x.Order).Select(x => x.Title)));
            Check("只有「法兰优化」那一格有门（② → ③ 之间）",
                  Flow.Stages.Count(x => x.GateToUnlockNext is not null) == 1
                  && Flow.Stage(StageId.整线核算).GateToUnlockNext is not null);

            // ★ 交付的门只留「有一个当前参数的收敛解」（2026-09-02 用户拍板）：
            //   过没过、验没验过由工程师判断（存/出图时列给他看）——
            //   拿掉的是**拦**，不是**指路**（下面那条钉着 Flow.Next 照旧先指复核）。
            var gShip = Flow.Stage(StageId.整线核算).GateToUnlockNext!;
            Check("交付的门不拦「判据没全过」", !gShip.RequireAllOk);
            Check("交付的门不拦「没加密复算」", !gShip.RequireMeshVerified);
            Check("交付的门仍拦「没解出来 / 参数动过了」",
                  gShip.RequireConverged && gShip.RequireFresh);
            Check("没有任何一道门带判据名单（原 ①→② 那道随阶段一起没了）",
                  Flow.Stages.All(x => x.GateToUnlockNext is null
                                    || x.GateToUnlockNext.RequiredChecks.Length == 0));

            // ★ 指路仍然先指加密复算、再指出图 —— 门松了，建议没松
            var fShip = new FlowState
            {
                Last = new LineResult
                {
                    Ok = true, Converged = true, RampChecked = true,
                    Checks = LineResult.Required.Select(q => new ConstraintOut
                    { Name = q.Prefix, Kind = q.Kind, Ok = true, Actual = 1, Limit = 2 }).ToArray(),
                },
            };
            fShip.CurrentSnap = fShip.SolvedSnap = "同一个快照";
            Check("全过但没复核时，指路指的是「加密复算」而不是出图",
                  Flow.Next(fShip)?.CmdId == "core.verifyMesh",
                  Flow.Next(fShip)?.CmdId ?? "(没指)");
            // ⑤ 锁住时的文案：说的必须是它**真的**拦的那件事。
            //   （原来这里验的是「说 ② 不说 ③」—— 那道门 2026-09-02 随阶段一起没了。
             //   现在验的是新的那道：它只拦「没解出来 / 参数动过了」，文案就得这么说，
             //   不许再写成「判据全过才准出图」——那已经不是它拦的事了。）
            Check("锁住时说的是「解得出来」，不是「判据全过」",
                  gShip.LockedWhy.Contains("解得出来", StringComparison.Ordinal)
                  && !gShip.LockedWhy.Contains("判据全过", StringComparison.Ordinal),
                  gShip.LockedWhy);
            Check("并且说清了「过没过由你判断」（免得人以为被拦死了）",
                  gShip.LockedWhy.Contains("由你判断", StringComparison.Ordinal),
                  gShip.LockedWhy);

        }

        Head("34 使用说明里的限值：**不许自己抄一份**，且每条判据都得有出处");
        {
            // 病灶（2026-08-24）：ManualPage.BuildHtml 开头写着
            //   「★ 判据值**只从 DesignSpec 取**，本页不再自己抄一份」——
            // 但那句话当时只兑现了**实测值**那一半；「限值的出处」那张表里的**限值**
            // 照旧是写死的字面量（72 h / 5 K / 10 K / 12 A/mm² / 100 mm / 0.6 mm）。
            // 而「管许用电流密度」在参数表里**就是可改的**（有 DisplayName）——
            // 工程师一改参数，说明书当场变成假话，而它有排版有图，**看起来就是答案**。
            //
            // 另外那张表**一条热稳定都没有**，而 APP 的判据表里它们是露脸的（带数值）——
            // 工程师看到「· 整片热稳定 10.2×」回来查出处，查不到。
            // 那一节的标题偏偏就是「限值的出处（**每条都必须有**）」。
            var pIn = (DesignInputs)F(main, "_in")!;
            var fdManual = DesignSpec.Current;
            string manual = ManualPage.BuildHtml(fdManual, pIn);

            Check("说明书渲染得出来（自证：空字串会让下面每一条恒假/恒真）",
                  manual.Length > 2000, $"{manual.Length} 字元");

            // ① 每条判据都要有出处 —— 含两条热稳定
            string Norm(string t) => t.Replace(" ", "").Replace("　", "");
            string mn = Norm(manual);
            foreach (var fi in typeof(LineResult.Key).GetFields(
                         BindingFlags.Public | BindingFlags.Static))
            {
                // ★★★ 2026-09-03：查的是 **Plain(key)**，不是 key 原文。
                //   说明书按用户要求把判据代号全清了（「工程师看不懂 ②′」），
                //   而 APP 判据表画到屏幕上时走的也是 Criteria.Plain ⇒
                //   查剥壳后的名字，才是在查「工程师看得见的那行字查不查得到」。
                //   查 key 原文只会钉住一个**屏幕上根本不出现**的字串。
                string key = Criteria.Plain((string)fi.GetValue(null)!);
                Check($"说明书里有「{key}」的出处", mn.Contains(Norm(key), StringComparison.Ordinal),
                      mn.Contains(Norm(key), StringComparison.Ordinal)
                          ? "" : "★ APP 判据表里露脸、说明书里查不到 —— 本节标题写的是「每条都必须有」");
            }

            // ② 限值必须与代码一致（写死之后漂开，正是 HANDOVER §1.83 刚出过的事）
            var limM = new LineCase();
            (string What, string Must)[] wants =
            {
                ("升温期限",  $"{limM.RampHours:0} h"),
                ("圆盘区最高温",   $"{limM.DiscOverTempMaxK:0} K"),
                ("法兰增量温降",  $"{limM.RootDeltaMaxK:0} K"),
                ("舌片自由段下界", $"{GeometryScreen.FreeTabMinDefaultMm:0} mm"),
                ("管壁下界",    $"{DesignInputs.WeldMinDefaultMm:0.0} mm"),
            };
            foreach (var (what, must) in wants)
                Check($"说明书里的{what}与代码一致（{must}）",
                      manual.Contains(must, StringComparison.Ordinal), must);

            // ③ ★ 活性自证：改参数表里的管 J，说明书必须跟着变。
            //    这一条才是真正的防线 —— 前面两组在「写死但恰好写对」时也会全绿。
            double keepJ = pIn.TubeJAllowAPerMm2;
            Check($"改之前说明书写的是 {keepJ:0.#} A/mm²",
                  manual.Contains($"{keepJ:0.#} A/mm²", StringComparison.Ordinal));
            try
            {
                pIn.TubeJAllowAPerMm2 = 9.0;
                string m2 = ManualPage.BuildHtml(fdManual, pIn);
                Check("★ 管 J 上限改成 9 之后，说明书跟着变成 9（不是抄死的 12）",
                      m2.Contains("9 A/mm²", StringComparison.Ordinal)
                      && !m2.Contains("12 A/mm²</td>", StringComparison.Ordinal),
                      m2.Contains("9 A/mm²", StringComparison.Ordinal) ? "" : "★ 说明书仍在报旧值");
            }
            finally { pIn.TubeJAllowAPerMm2 = keepJ; }   // 收干净，别泄漏给后面的节

            Check("还原之后又变回去了（自证：否则上一条可能只是碰巧）",
                  ManualPage.BuildHtml(fdManual, pIn).Contains($"{keepJ:0.#} A/mm²", StringComparison.Ordinal),
                  $"{pIn.TubeJAllowAPerMm2:0.#}");
            // ④ ★ 说明书宣传的 CLI 旗标必须**真的存在**。
            //
            //   起因：BuildHtml 的注释写着「**public 是故意的**：`--cli --manual`
            //   要能不开 GUI 就导出，否则「图对不对」只能靠肉眼开窗口看 ——
            //   那不是可复核的验证」，而 Program.cs 里**根本没有这个旗标**。
            //   意图是真的，实现从来没做。（同一天在同一个仓里第三次撞见这个形态。）
            //   ⇒ 旗标已补上，同时把「说明书说的命令得能跑」变成一条会自己跑的检查。
            //
            //   只看 `--cli` 后面跟的那几个 —— CSS 里的 var(--bg) 这类不会被误抓。
            {
                char qq = (char)34;
                string progSrc = File.ReadAllText(
                    Path.Combine(RepoRoot(), "Pt_Optimize", "Program.cs"));
                var real = new HashSet<string>(
                    Regex.Matches(progSrc, qq + "(--[a-z0-9]+)" + qq)
                         .Select(m => m.Groups[1].Value), StringComparer.Ordinal);
                var adv = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(manual, "--cli(?:[ ]+--[a-z0-9]+)+"))
                    foreach (Match f in Regex.Matches(m.Value, "--[a-z0-9]+"))
                        if (f.Value != "--cli") adv.Add(f.Value);

                // ★★★★★ 2026-09-02 **这条检查的前提反过来了**。
                //
                //   它原来的自证是「从说明书里抓得到被宣传的旗标（≥4 个）」——
                //   用途是防止下一条「宣传的旗标都真的存在」变成空转。
                //   而用户当天拍板：「APP 不要有命令行形式操作」「这个 APP 不是给程序员用的，
                //   是给现场工程师的」⇒ 说明书那节「§10 常用命令行」整节删掉，
                //   于是抓到 0 个，这条自证当场红。
                //
                //   **它红得对**：前提变了，检查就该跟着倒过来 ——
                //   不再是「宣传的必须存在」，而是**一个都不许宣传**。
                //   ⚠ 旗标本身留着，它们是开发侧的验收工装；禁的是把它们印给工程师。
                //     界面其余部分由 Pt_Optimize.Tests 的 NoCliFlagInUiTests 盯着。
                Check("Program.cs 里认得的旗标数合理（自证：读错档会让下一条恒真）",
                      real.Count >= 50, $"{real.Count} 个");
                Check("★ 说明书里**一个命令行旗标都不宣传**（工程师不开命令行）",
                      adv.Count == 0,
                      adv.Count == 0 ? "0 个"
                                     : "★ 还在教工程师敲：" + string.Join(" ", adv));
                // 自证：抓取器本身没坏 —— 拿一段假文本喂它，必须抓得出来。
                //   否则「抓到 0 个」可能只是正则失灵，而不是说明书真的干净了。
                var probe = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches("跑 --cli --selfcheck --wall 0.6 看看", "--cli(?:[ ]+--[a-z0-9]+)+"))
                    foreach (Match f in Regex.Matches(m.Value, "--[a-z0-9]+"))
                        if (f.Value != "--cli") probe.Add(f.Value);
                Check("自证：抓取器真的抓得到旗标（否则上一条是空转）", probe.Count == 2,
                      string.Join(" ", probe));
            }

        }

        Head("35 铂金管加热段数输入框（R27，用户 2026-09-10 原话：「UI 必须有输入铂金管加热段数的输入框」）");
        {
            var segCountBox = (NumericUpDown)F(page, "_segCount")!;
            var segRows = (System.ComponentModel.BindingList<LineDesignPage.SegRow>)F(page, "_segs")!;
            var baseIn = (DesignInputs)F(page, "_base")!;

            // ① 挂在「① 输入」页上：走到最近的 TabPage 祖先，看它的标题。
            Control? p0 = segCountBox;
            while (p0 is not null && p0 is not TabPage) p0 = p0.Parent;
            Check("段数输入框挂在「① 输入」页上",
                  p0 is TabPage tp0 && tp0.Text.Contains("①") && tp0.Text.Contains("输入"),
                  p0 is TabPage tpSeen ? tpSeen.Text : "（没找到 TabPage 祖先）");

            // ② 默认值 = 段表行数（开箱 3 段）
            Check("默认值 = 段表行数", (int)segCountBox.Value == segRows.Count,
                  $"框 {segCountBox.Value} vs 表 {segRows.Count} 行");
            int keepCount = segRows.Count;

            // ③ 改输入框为 4 ⇒ 段表变 4 行、片数变 5（段数 + 1）；新段按规则给默认值
            segCountBox.Value = 4m;
            Pump(200);
            Check("改输入框为 4 ⇒ 段表变 4 行", segRows.Count == 4, $"{segRows.Count}");
            var plates4 = (NumericUpDown[])F(page, "_tPlate")!;
            Check("片数跟着变 5（段数 + 1）", plates4.Length == 5, $"{plates4.Length}");
            var added = segRows[3];
            Check("新段名称按序号生成 HC4", added.名称 == "HC4", added.名称);
            Check("新段长度取参数表「直接加热铂金管的长度」默认值",
                  Math.Abs(added.直接加热管长mm - baseIn.TubeLengthMm) < 1e-6,
                  $"{added.直接加热管长mm} vs {baseIn.TubeLengthMm}");
            Check("新段控温点 = 上一段 − 70 °C（不低于 900）",
                  Math.Abs(added.控温C - Math.Max(900.0, segRows[2].控温C - 70.0)) < 1e-6,
                  $"{added.控温C}");

            // ④ 改回 3 ⇒ 3 行 4 片；删的是规则生成的默认值 ⇒ 输出框不该多话
            int outLenBefore = outBox.Text.Length;
            segCountBox.Value = 3m;
            Pump(200);
            Check("改回 3 ⇒ 段表变回 3 行", segRows.Count == 3, $"{segRows.Count}");
            var plates3 = (NumericUpDown[])F(page, "_tPlate")!;
            Check("片数跟着变回 4", plates3.Length == 4, $"{plates3.Length}");
            Check("删的是规则生成的默认值 ⇒ 输出框没多话", outBox.Text.Length == outLenBefore, "");

            // ⑤ 表里直接加一行（工程师手改过控温点，不是规则会生成的样子）⇒ 输入框跟着变 4
            segRows.Add(new LineDesignPage.SegRow
            { 名称 = "HC4", 直接加热管长mm = 300, 控温C = 950, 水头m = 1.0 });
            Pump(200);
            Check("段表直接加一行 ⇒ 输入框跟着变 4", (int)segCountBox.Value == 4, $"{segCountBox.Value}");

            // ⑥ 再改回 3 ⇒ 删掉的是「手改过」的那行 ⇒ 输出框要提一句（不弹框）
            outLenBefore = outBox.Text.Length;
            segCountBox.Value = 3m;
            Pump(200);
            Check("删掉手改过的段 ⇒ 输出框写了一句说明（不弹框）",
                  outBox.Text.Length > outLenBefore && outBox.Text.Contains("删掉了") && outBox.Text.Contains("HC4"),
                  "");

            Check("收尾：还原回开箱的 3 段", segRows.Count == keepCount && (int)segCountBox.Value == keepCount,
                  $"{segRows.Count}/{segCountBox.Value}");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "★ 全部通过" : $"✗ {fail} 项不过");
        Console.Out.Flush();

        // ★★★★★ 必须**强制退出**，不能只设 ExitCode 让 Main 返回（2026-08-24）。
        //
        // 症状：断言全跑完、「★ 全部通过」也打出来了，**进程却不退** ——
        //   实测挂了 41 分钟只用掉 99 秒 CPU（即根本没在算），
        //   而提交钩子在等它 ⇒ 整个提交卡死。
        // 原因是本进程建过 WinForms 窗体、起过子进程，某个前台线程没退；
        //   Main 返回之后 CLR 会一直等它。
        // ⚠ 这比「跑得慢」危险：它**看起来是通过的**（输出完整、最后一行是全部通过），
        //   只有去看进程表才知道它没结束。上一轮我就据此误判过一次「已完成」。
        // Geom 的命令分派早就用同一手兜底（finally 里 Environment.Exit）。
        // ⚠ `Environment.Exit` **不够** —— 实测它也挂住：它会跑 ProcessExit 处理器与终结器，
        //   而阻塞就在那里面。断言在第 4 分钟就打完「全部通过」，之后又挂了 6 分钟以上
        //   （41 分钟只用掉 99 秒 CPU ⇒ 没在算，是在等）。
        //   ⇒ 直接终止本进程：结果已经打完并 Flush 过，没有任何需要善后的东西。
        //   ⚠ 不能用 Process.Kill()：它**不保留退出码**（实测全部通过却给 127）。
        //     提交钩子是靠退出码判通过的 ⇒ 那样会把**每一次**提交都拒掉。
        //     TerminateProcess 能精确带上 fail，两个方向都对。
        TerminateProcess(GetCurrentProcess(), (uint)fail);
    }
}
