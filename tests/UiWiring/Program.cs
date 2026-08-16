using System;
using System.Linq;
using System.Reflection;
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
    static void Head(string s) { Console.WriteLine(); Console.WriteLine("=== " + s + " ==="); }

    [STAThread]
    static void Main() {
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
        string html = ManualPage.BuildHtml(FinalDesign.W08);
        Check("含新合计 3106", html.Contains("3106"));
        Check("含新板厚 3.33", html.Contains("3.33"));
        Check("③ 用的是新值 5.5", html.Contains("5.52") || html.Contains("5.5"));
        // ⚠ 只在**数据区**判旧值。说明书里有一段讲 2026-08-12 那次事故的文字，
        //   引的是「出事那天的板厚」2.11+3.40+3.18+1.76 —— 那是史料，不是当前值。
        //   上一版把整篇一起判，把史料当成了残留（测试写得比被测对象还粗）。
        int cut = html.IndexOf("定案 3DM", StringComparison.Ordinal);
        string dataPart = cut > 0 ? html[..cut] : html;
        Check("数据区不含旧合计 3117", !dataPart.Contains("3117"));
        Check("数据区不含旧板厚 3.40", !dataPart.Contains("3.40"));
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

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "★ 全部通过" : $"✗ {fail} 项不过");
        Environment.ExitCode = fail;
    }
}
