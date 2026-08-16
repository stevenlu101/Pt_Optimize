using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;

class Dump {
    static FieldInfo Fi(object o, string n) =>
        o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!;
    static object? F(object o, string n) => Fi(o, n).GetValue(o);
    static void Set(object o, string n, object? v) => Fi(o, n).SetValue(o, v);
    static void M(object o, string n) =>
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

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        var page = new LineDesignPage(new DesignInputs());
        var form = new Form { Width = 1200, Height = 800 };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(page); form.Controls.Add(tabs);
        form.CreateControl();
        Pump(300);

        var wall = (NumericUpDown)F(page, "_wall")!;
        var plate = (NumericUpDown[])F(page, "_tPlate")!;
        var tubeIns = (NumericUpDown)F(page, "_tubeIns")!;
        var outBox = (RichTextBox)F(page, "_out")!;

        Console.WriteLine("=== 5′ 求解中再改参数：旧的要被取消，新的要接上（上一版这条是假测试）===");
        wall.Value = 0.80m; Pump(2200);
        var cts1 = F(page, "_cts");
        Check("已开跑", cts1 is not null);
        plate[0].Value = 2.60m;                 // 求解中途改参数
        Pump(600);
        Check("旧任务已被取消", ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.IsCancellationRequested ?? true);
        Pump(4000);                              // 等它退出 + 防抖 + 重启
        var cts2 = F(page, "_cts");
        Check("新任务接上了（不是卡死在取消态）", cts2 is not null && !ReferenceEquals(cts1, cts2),
              cts2 is null ? "★ _cts 为 null —— 没重启" : "");
        // 收尾：取消掉，免得后面被它干扰
        ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel();
        Pump(3000);

        Console.WriteLine();
        Console.WriteLine("=== 7 预测层：注入一个真实已解基准，看外推数对不对 ===");
        var baseRes = LineRunner.Run(FinalDesign.W08.BuildCase(new DesignInputs(), checkRamp: false));
        Check("基准解可用", baseRes.Ok && baseRes.Converged);
        double V(string k) { foreach (var c in baseRes.Checks) if (c.Name.StartsWith(k)) return c.Actual; return double.NaN; }
        Console.WriteLine($"     基准 ③={V("③"):0.00} ②″={V("②″"):0.00} 管J={V("管 J"):0.00}");

        // 把页面控件摆到 W08 的位置，再注入基准快照
        Set(page, "_suppressAuto", true);
        wall.Value = 0.80m;
        for (int i = 0; i < 4; i++) plate[i].Value = (decimal)FinalDesign.W08.TabThickMm[i];
        tubeIns.Value = 5.0m;
        Set(page, "_suppressAuto", false);
        Set(page, "_solvedRes", baseRes);
        var snapT = page.GetType().GetNestedType("Snap", BindingFlags.NonPublic)!;
        var snap = Activator.CreateInstance(snapT)!;
        snapT.GetField("Wall")!.SetValue(snap, 0.80);
        snapT.GetField("Plate")!.SetValue(snap, FinalDesign.W08.TabThickMm.Average());
        snapT.GetField("TubeIns")!.SetValue(snap, 5.0);
        Set(page, "_solvedSnap", snap);

        // 板厚四片各 +0.10 ⇒ ③ 预期 ≈ 基准 + 123.4×0.10 ≈ +12.3
        for (int i = 0; i < 4; i++) plate[i].Value += 0.10m;
        Pump(300);
        string txt = outBox.Text;
        Console.WriteLine("     ── 预测块 ──");
        foreach (var ln in txt.Split('\n').Where(l => l.Contains("③") || l.Contains("②″") || l.Contains("管J") || l.Contains("预测")))
            Console.WriteLine("     " + ln.TrimEnd());
        Check("给出了预测值", txt.Contains("预测"));
        Check("③ 预测越限被标出来", txt.Contains("预测越限"),
              "（基准 5.5 + 12.3 ≈ 17.8 > 10，应当标）");
        Check("明确写了这是线性外推", txt.Contains("线性外推"));

        Console.WriteLine();
        Console.WriteLine("=== 8 改动过大时不给预测 ===");
        for (int i = 0; i < 4; i++) plate[i].Value += 0.50m;      // 累计 +0.60 > 阈值 0.4
        Pump(300);
        Check("超出适用范围 ⇒ 不给预测并说明原因",
              outBox.Text.Contains("超出实测雅可比的适用范围"));
        ((System.Threading.CancellationTokenSource?)F(page, "_cts"))?.Cancel();
        Pump(500);

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "★ 全部通过" : $"✗ {fail} 项不过");
        Environment.ExitCode = fail;
    }
}
