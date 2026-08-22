using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Windows.Forms;
using PtOptimize.Core;
using PtOptimize.UI;

/// <summary>
/// ①→②→③→④→⑤ **全程走通**，每一步把算出来的数抓下来核对。
///   `UiWiring.exe --walk`
///
/// ★ 为什么不用 CLI 直接调内核（用户 2026-08-21：「要让 APP 整个流程都跑完，
///   APP 每个步骤跑完后抓下计算数据分析有无错误」）：
///
///   CLI 那 90 个开关走的是**另一套装配**（自己 new DesignInputs、自己拼 LineCase）。
///   界面把参数从「参数表 + 页面控件」搬进算例的那一段代码，CLI 一行都不经过 ——
///   而本项目栽过的坑里，有一多半就出在**那一段搬运**上
///   （内径被 LineCase 默认值吃掉、压接段用了 3 mm 默认值、盘径直径/半径搞混…）。
///   ⇒ 要验「APP 跑得通」，就必须驱动 APP 自己的方法。
///
/// 流程拓扑（用户给的）：
///     ① 闸门 ─┬─→ ② 粗算（解析·不可交付）   ← 末端，不解锁任何东西
///             └─→ ③ 整线核算 ★ ─→ ④ 定尺寸 ─→ ⑤ 交付
/// </summary>
static class Walk
{
    static int _bad;

    static void H(string s)
    {
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine("  " + s);
        Console.WriteLine("══════════════════════════════════════════════════════════");
    }

    static void OK(string name, bool ok, string detail = "")
    {
        if (!ok) _bad++;
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}" + (detail.Length > 0 ? "　" + detail : ""));
    }

    static object? F(object o, string n)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f is not null) return f.GetValue(o);
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p is not null) return p.GetValue(o);
        }
        return null;
    }

    static object? Call(object o, string n, params object?[] args)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
        {
            // ⚠ 要带 Static：ExportBlockedReason 是 public static，只找实例成员会抛 MissingMethod。
            var m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.NonPublic | BindingFlags.Public);
            if (m is not null) return m.Invoke(m.IsStatic ? null : o, args);
        }
        throw new MissingMethodException(o.GetType().Name + "." + n);
    }

    static void Set(object o, string n, object? v)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f is not null) { f.SetValue(o, v); return; }
        }
        throw new MissingFieldException(o.GetType().Name + "." + n);
    }

    static void Pump(int ms)
    {
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }

    /// <summary>等一个异步页面动作跑完：靠**页面自己的状态**判断，不靠固定睡眠。</summary>
    static bool Wait(Func<bool> done, int timeoutMs)
    {
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < timeoutMs)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
            if (done()) return true;
        }
        return false;
    }

    /// <summary>
    /// `--walk3dm &lt;file&gt;`：驱动 APP 走 **.3dm 任意形状**那条路，逐步把数抓下来。
    /// 与 <see cref="Run"/> 同理 —— 走的是 APP 自己的方法，不是绕过界面直调内核。
    /// </summary>
    public static int Run3dm(string file)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        var flow = (FlowState)F(main, "_flow")!;
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1000);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(200);
        Set(line, "_userReady", true);

        H("输入：" + file);
        {
            OK("文件在", File.Exists(file), new FileInfo(file).Length + " 字节");
            OK("几何探针在（.3dm 那条路全靠它）", Geometry3dm.FindProbe() is not null,
               Geometry3dm.FindProbe() ?? "★ 找不到 Pt_Optimize.Geom.exe");
        }

        // ── 切到 .3dm 模式，四片都用同一张图（APP 支持：「每片一个文件，可重复同一文件」）
        H("① 切到 .3dm 模式并填图纸");
        var files = (TextBox[])F(line, "_file3dm")!;
        var layer = (TextBox)F(line, "_layer3dm")!;
        {
            Set(line, "_suppressAuto", true);
            ((RadioButton)F(line, "_src3dm")!).Checked = true;
            foreach (var t in files) t.Text = file;
            layer.Text = "法兰";
            Set(line, "_suppressAuto", false);
            Call(line, "SyncGeomSource");
            Pump(200);
            OK("四片图纸都填上了", files.All(t => t.Text.Length > 0));
            OK("解析形状专用的盘径/舌长/舌宽已被禁用（图纸给定，不是自由度）",
               !((NumericUpDown)F(line, "_discD")!).Enabled
               && !((NumericUpDown)F(line, "_tabLen")!).Enabled);
            var an = (ToolStripButton)F(line, "_btnAnalyze")!;
            Call(main, "SyncGates"); Pump(120);
            OK("「分析几何变数」此时可用", an.Enabled, an.ToolTipText ?? "");
            var sh = (ToolStripButton)F(line, "_btnShape")!;
            OK("「◇ 搜形状」此时被禁（形状由图纸给定）", !sh.Enabled);

            // ★★ 切到 .3dm 之后，_tPlate 的含义从**板厚 mm** 变成**厚度标度 k**。
            //    实测过一次静默出错：页面默认 0.516/0.855/0.776/0.426（毫米）被原样
            //    当成标度 ⇒ 1366.8 g 的图纸缩成 874 g/片，**算的不是用户给的那张图**，
            //    而输出照报「合计 5962 g」。靠数值区分两个量本来就分不开
            //    （0.9 既是合理厚度也是合理标度）⇒ 必须靠**模式切换**来换值。
            var tp = (NumericUpDown[])F(line, "_tPlate")!;
            var scales = tp.Select(x => (double)x.Value).ToArray();
            OK("切到 .3dm 后厚度标度回到 1.0（图纸按原尺寸算）",
               scales.All(v => Math.Abs(v - 1.0) < 1e-9),
               string.Join("/", scales.Select(v => v.ToString("0.000")))
               + (scales.All(v => Math.Abs(v - 1.0) < 1e-9) ? "" : "　★ 图纸被静默缩放了"));
        }

        // ── 分析几何变数
        H("② 分析几何变数：把图纸反推成各级台阶");
        {
            var t0 = Environment.TickCount64;
            Call(line, "AnalyzeShape");
            Pump(500);
            var box = (RichTextBox)F(line, "_out")!;
            Console.WriteLine(Indent(box.Text, 46));
            var levels = (double[][]?)F(line, "_levels");
            OK("解析出了分级", levels is { Length: > 0 } && levels[0].Length > 0,
               levels is null ? "★ 没有" : $"{levels[0].Length} 级：{string.Join("/", levels[0].Select(v => v.ToString("0.00")))}　{Environment.TickCount64 - t0} ms");
            if (levels is { Length: > 0 })
            {
                // ⚠ 厚度场里有孤立格（实测：3/2/1 mm 之外还有**一个**格子是 2.5 mm）。
                //   若它被当成一级，逐级定厚就会去优化一个 0.25 mm² 的像素。
                OK("分级数不荒谬（≤ 6）", levels[0].Length <= 6, $"{levels[0].Length} 级");
                OK("每一级都是正厚度", levels[0].All(v => v > 1e-9),
                   string.Join("/", levels[0].Select(v => v.ToString("0.000"))));
            }
            var lockBoxes = (System.Collections.Generic.List<CheckBox>?)F(line, "_lockBoxes");
            OK("各级的「锁定」勾选框已生成", lockBoxes is { Count: > 0 },
               $"{lockBoxes?.Count ?? 0} 个");
        }

        // ── 核算整线
        H("③ 核算整线（.3dm 几何）");
        LineResult? r;
        {
            var t0 = Environment.TickCount64;
            Call(line, "RunAsync", false, false);
            bool fin = Wait(() => F(line, "_cts") is null, 900_000);
            OK("在超时内跑完", fin, $"{(Environment.TickCount64 - t0) / 1000.0:0.0} s");
            r = (LineResult?)F(line, "LastResult");
            var box = (RichTextBox)F(line, "_out")!;
            if (r is null)
            {
                OK("有结果", false, "★ 没有结果对象 —— 下面是输出框说了什么");
                Console.WriteLine(Indent(box.Text, 40));
                return Done();
            }
            Console.WriteLine($"  收敛 {(r.Converged ? "✓" : "✗")}　Ok={r.Ok}　{r.Message}");
            Console.WriteLine();
            Console.WriteLine($"  {"判据",-26}{"实际",12}{"限值",12}  判定");
            foreach (var c in r.Checks)
                // NaN 直接进格式串会打出「非數值」（还跟区域设置走）——
                // APP 的判据表写的是「达不到」，这里对齐它，别另造一套。
                Console.WriteLine($"  {c.Name,-26}"
                                + $"{(double.IsNaN(c.Actual) ? "达不到" : c.Actual.ToString("0.000")),12}"
                                + $"{c.Limit,12:0.000}  "
                                + (c.Kind == CheckKind.Reference ? "—（参考）"
                                   : c.Undetermined ? "⚠ 无法判定" : c.Ok ? "✓" : "✗"));
            Console.WriteLine();
            Console.WriteLine($"  合计铂重 {r.TotalMassG:0.0} g（管 {r.TubeMassG:0.0} + 法兰 {r.FlangeMassG:0.0}）");

            OK("解出来了", r.Ok, r.Message);
            OK("收敛", r.Converged);
            OK("判据一条都没少", r.Checks.Length > 0, $"{r.Checks.Length} 条");
            // ⚠ NaN 本身不是错 —— .3dm 模式下 ⑤⑥ 拿不到解析量，NaN + Undetermined
            //   正是「无法判定」的正确表达。真正的错是 **NaN 却没标 Undetermined**：
            //   那种会被当成一个数参与比较。
            var nanNotMarked = r.Checks
                .Where(c => c.Kind != CheckKind.Reference
                         && double.IsNaN(c.Actual) && !c.Undetermined).ToArray();
            OK("算出 NaN 的判据都标了「无法判定」", nanNotMarked.Length == 0,
               nanNotMarked.Length == 0 ? "" : "★ 没标：" + string.Join("、", nanNotMarked.Select(c => c.Name)));
            // ★ .3dm 模式下 ⑤⑥ 是解析闭式量，拿不到 ⇒ 应当报「无法判定」而**不是**悄悄通过
            var geom = r.Checks.Where(c =>
                c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal)
             || c.Name.StartsWith(LineResult.Key.DiscCover, StringComparison.Ordinal)).ToArray();
            OK("⑤⑥ 两条几何判据仍在表里（不许消失）", geom.Length == 2,
               string.Join("、", geom.Select(c => $"{c.Name}={(c.Undetermined ? "无法判定" : c.Actual.ToString("0.000"))}")));
            OK("⑤⑥ 在 .3dm 模式下不冒充通过",
               geom.All(c => c.Undetermined || !c.Ok || c.Kind == CheckKind.Reference)
               || geom.All(c => !c.Undetermined),
               string.Join("、", geom.Select(c => c.Undetermined ? "无法判定" : (c.Ok ? "过" : "不过"))));
            if (r.Flanges.Length > 0)
            {
                double tmax = r.Flanges.Max(f => f.TMaxC);
                double resid = r.Flanges.Max(f => Math.Abs(f.EnergyResidualW));
                Console.WriteLine($"  最高温 {tmax:0.0} °C　能量残差 {resid:0.000} W");
                OK("最高温未超铂熔点", tmax < Materials.PtMeltC, $"{tmax:0.0} °C");
                // 容差按几何规模放宽：这张图的法兰面积是定案的四倍多，
                // 残差 0.125 W 相对 71 W 的抽热是 0.2 % —— 不是发散。
                // （--selfcheck B 段判发散用的是 50 W。）
                OK("能量残差不发散（< 1 W）", resid < 1.0, $"{resid:0.000} W");
            }
            // ★ 关键：这个状态下「下一步」指向哪个按钮？它点得动吗？
            Call(main, "SyncGates"); Pump(150);
            // ⚠ 必须把**适用性**一起传进去 —— 与 MainForm/StagePanel 的调用方式一致。
            //   不传就等于测了一条 APP 根本不走的路：头一版这么写，
            //   于是报「指着一个用不了的按钮」，而 APP 里其实已经不指了。
            bool App3(string id) => (bool)typeof(LineDesignPage).GetMethod("CommandApplicable",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(line, new object[] { id })!;
            var ns = Flow.Next(flow, App3);
            if (ns is null) OK("给出了下一步", false, "★ 没有");
            else
            {
                if (ns.CmdId.Length == 0)
                {
                    // 只给说明、不指按钮 —— .3dm 模式下几何判据判不了，要改形状得回 Rhino
                    Console.WriteLine($"  下一步 →（只有说明，不指按钮）{ns.Why}");
                    OK("没有可点的命令时，只给说明而不指一个灰按钮", true);
                    OK("不是安静失败（报全过同时给荒谬的数）",
                       !(r.Converged && r.AllOk && r.Flanges.Length > 0
                         && r.Flanges.Max(f => f.TMaxC) > Materials.PtMeltC));
                    return Done();
                }
                var spec = Flow.Cmd(ns.CmdId);
                bool applicable = App3(ns.CmdId);
                ToolStripButton? btn = null;
                foreach (TabPage tp in tabs.TabPages)
                    foreach (var ts in tp.Controls.OfType<ToolStrip>())
                        foreach (var b in ts.Items.OfType<ToolStripButton>())
                            if (b.Text == spec.Text) btn = b;
                Console.WriteLine($"  下一步 → 「{spec.Text}」　适用={applicable}　"
                                + $"可点={btn?.Enabled}　理由：{ns.Why}");
                OK("「下一步」指的按钮在当前模式下**点得动**",
                   applicable && btn?.Enabled == true,
                   applicable ? "" : "★ 指着一个在本模式下用不了的按钮");
            }

            OK("不是安静失败（报全过同时给荒谬的数）",
               !(r.Converged && r.AllOk && r.Flanges.Length > 0
                 && r.Flanges.Max(f => f.TMaxC) > Materials.PtMeltC));
        }

        return Done();
    }

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        var anal = tabs.TabPages.OfType<AnalysisPage>().First();
        var flow = (FlowState)F(main, "_flow")!;
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1200);

        // 把所有输出框的句柄逼出来，否则 .Text 读回来的可能还没排过版
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main);
        Pump(300);

        var inputs = (DesignInputs)F(main, "_in")!;

        // ═══════════════════════════════════════════════════════════
        H("数据来源（先把「这些数打哪来」钉死，后面每一步都引用它）");
        {
            var g = MaterialDb.Get(inputs.GradeName);
            Console.WriteLine($"  牌号            {g.Name}");
            Console.WriteLine($"  蠕变拟合区间    {g.CreepTMinC:0}–{g.CreepTMaxC:0} °C"
                            + $"　（{(g.RangeConfirmed ? "原始数据范围" : "⚠ 推定，须向供应商确认")}）");
            Console.WriteLine($"  设计寿命        {inputs.DesignLifeHours:0} h");
            Console.WriteLine($"  力学安全系数    {inputs.SafetyFactor:0.0}");
            Console.WriteLine($"  管许用 J        {inputs.TubeJAllowAPerMm2:0.0} A/mm²（现场给定）");
            Console.WriteLine($"  析晶裕度        {inputs.DevitMarginK:0} K　液相线 {inputs.TLiquidusC:0} °C");
            Console.WriteLine($"  当前定案档      {FinalDesign.Current.Name}");
            OK("蠕变区间是实测范围而不是推定", g.RangeConfirmed,
               g.RangeConfirmed ? "" : "★ 推定区间不能用来出交付件");
        }

        // ═══════════════════════════════════════════════════════════
        H("① 先决条件（能造 · 能升温）——  链 D，闭式，毫秒");
        {
            var t0 = Environment.TickCount64;
            // Gate1 是**同步**方法，直接返回报告文本（外层 RunAsync(gate:true) 负责写进框）。
            // ⚠ 头一版去读 _out.Text，读到的是空串 —— 因为那时没人写过它，
            //   于是误报成「① 没出结果」。取返回值才是这一步真正算出来的东西。
            string txt = (string)Call(anal, "Gate1", System.Threading.CancellationToken.None)!;
            Console.WriteLine(Indent(txt, 40));
            OK("① 出了结果", txt.Length > 100, $"{Environment.TickCount64 - t0} ms");
            OK("① 结果里带判定而不是只有数", txt.Contains('✓') || txt.Contains('✗') || txt.Contains("读法"));
        }

        // ═══════════════════════════════════════════════════════════
        H("② 粗算（解析 · 不可交付）—— 链 B，即时。★ 末端分支，不解锁任何东西");
        List<SegmentResult> rs;
        {
            Call(main, "RunLine");
            Pump(200);
            var segs = (List<Segment>)F(main, "_segs")!;
            rs = LineSolver.Solve(segs, inputs);
            var (mass, cost, bad) = LineSolver.Totals(rs);
            foreach (var r in rs)
                Console.WriteLine($"  {r.Seg.Name}  {r.Seg.TSetC,6:0} °C"
                    + $"  σ_vm {r.VonMisesMPa,7:0.000}"
                    + $"  许用 {(double.IsNaN(r.AllowMPa) ? "—" : r.AllowMPa.ToString("0.000")),7}"
                    + $"  利用率 {(double.IsNaN(r.Utilization) ? "—" : r.Utilization.ToString("0.00")),6}"
                    + $"  {(r.Feasible ? "✓" : (r.Unknown ? "⚠ " : "✗ ") + r.Binding)}");
            Console.WriteLine($"  合计管铂重 {mass:0} g");

            int unk = rs.Count(x => x.Unknown);
            OK("「判不了」没有被说成「强度不够」",
               rs.All(x => !x.Unknown || x.Binding.StartsWith("无法判定", StringComparison.Ordinal)),
               unk > 0 ? $"{unk} 段无法判定（段温在蠕变区间外）" : "本组全部落在区间内");
            OK("无法判定的段**不算通过**", rs.All(x => !x.Unknown || !x.Feasible));
            OK("无法判定的段利用率不塞假数字", rs.All(x => !x.Unknown || double.IsNaN(x.Utilization)));

            // ★ 拓扑：② 是末端，不该解锁任何东西
            bool s4 = Gate.Evaluate(StageId.定尺寸, flow).Unlocked;
            OK("② 跑完之后 ④ 仍然锁着（② 是末端分支）", !s4,
               s4 ? "★ ② 不该解锁任何阶段" : "");
        }

        // ═══════════════════════════════════════════════════════════
        H("③ 前置：出厂默认几何是不是一个能用的起点");
        {
            var w = (NumericUpDown)F(line, "_wall")!;
            double wall0 = (double)w.Value;
            Console.WriteLine($"  出厂默认壁厚 {wall0:0.00} mm　焊接工艺下界 {inputs.WeldMinThicknessMm:0.00} mm");
            // 这不是吹毛求疵：APP 自己在预测块里就写着「低于手工 TIG 烧穿下界 —— 工艺上焊不出来」。
            // 开箱即不可制造的默认值，会让第一次用的人拿到一个发散的解，还以为是程序坏了。
            OK("出厂默认壁厚不低于焊接工艺下界", wall0 >= inputs.WeldMinThicknessMm - 1e-9,
               wall0 >= inputs.WeldMinThicknessMm ? "" :
               $"★ 默认 {wall0:0.00} < 下界 {inputs.WeldMinThicknessMm:0.00} —— 开箱就是造不出来的构型");

            // 按说明书教的用法走：载入定案 → 核算整线。
            // 直接拿出厂默认去解，等于用一个自己都说造不出来的几何去跑分钟级耦合解。
            Call(line, "LoadFinalDesign");
            Pump(400);
            Console.WriteLine($"  已载入定案「{FinalDesign.Current.Name}」⇒ 壁厚 {(double)w.Value:0.00} mm、"
                + $"板厚 {string.Join("/", ((NumericUpDown[])F(line, "_tPlate")!).Select(x => ((double)x.Value).ToString("0.00")))}");
        }

        H("③ 整线核算 ★ —— 链 C，耦合数值解，分钟级。这是唯一可交付的链");
        LineResult? r3;
        {
            var t0 = Environment.TickCount64;
            Call(line, "RunAsync", false, false);
            bool fin = Wait(() => F(line, "_cts") is null && F(line, "LastResult") is not null, 600_000);
            OK("③ 在超时内跑完", fin);
            r3 = (LineResult?)F(line, "LastResult");
            OK("③ 有结果对象", r3 is not null);
            if (r3 is null) return Done();

            Console.WriteLine($"  收敛 {(r3.Converged ? "✓" : "✗")}　"
                            + $"耗时 {(Environment.TickCount64 - t0) / 1000.0:0.0} s");
            Console.WriteLine();
            Console.WriteLine($"  {"判据",-26}{"实际",12}{"限值",12}  判定");
            // ⚠ 判定列的写法必须与 APP 的判据表**一致**（LineDesignPage.FillChecks）：
            //   参考量印「—」而不是 ✓。头一版照 c.Ok 印，于是
            //   「· 法兰 J_max  35.762 / 10.000  ✓」这种行被印成通过 ——
            //   参考量按构造就是 Ok=true，实际值超限值三四倍照样打勾，读起来就是「过了」。
            //   另造一套渲染约定，本身就是「同一件事两处表达然后对不上」。
            foreach (var c in r3.Checks)
                Console.WriteLine($"  {c.Name,-26}{c.Actual,12:0.000}{c.Limit,12:0.000}  "
                                + (c.Kind == CheckKind.Reference ? "—（参考，不判定）"
                                   : c.Undetermined ? "⚠ 无法判定" : c.Ok ? "✓" : "✗"));
            Console.WriteLine();
            Console.WriteLine($"  合计铂重 {r3.TotalMassG:0.0} g");

            OK("③ 收敛", r3.Converged);
            OK("判据一条都没少", r3.Checks.Length > 0, $"{r3.Checks.Length} 条");
            // ⚠ 只看**判据**（HardSafety/Target）。参考量按构造就是 Ok=true、不参与判定，
            //   它带 Undetermined 只是标记「这个读数不可信」，不是「判过了」——
            //   LineResult.AllOk 也正是这么划的界。头一版把参考量也算进来，误报了一条。
            OK("判据里没有「既无法判定又算通过」的（铁律二）",
               r3.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)
                        .All(c => !(c.Undetermined && c.Ok)));
            var undet = r3.Checks.Where(c => c.Kind == CheckKind.Reference && c.Undetermined).ToArray();
            if (undet.Length > 0)
                Console.WriteLine($"  （参考量中有 {undet.Length} 条无法判定，不参与判定：{string.Join("、", undet.Select(c => c.Name))}）");
            // 与 --selfcheck B 段同一口径：逐片取最大，不是取平均 ——
            // 平均会把一片发散的残差摊平掉。
            double resid = r3.Flanges.Length == 0 ? 0 : r3.Flanges.Max(f => Math.Abs(f.EnergyResidualW));
            double tmax = r3.Flanges.Length == 0 ? 0 : r3.Flanges.Max(f => f.TMaxC);
            OK("能量残差在容差内", resid < 0.1, $"{resid:0.000} W");
            OK("最高温未超铂熔点", tmax < Materials.PtMeltC, $"{tmax:0.0} °C（熔点 {Materials.PtMeltC:0}）");
            OK("没有判据算出 NaN（参考量除外）",
               !r3.Checks.Any(c => c.Kind != CheckKind.Reference && double.IsNaN(c.Actual)));
            // 「安静失败」的定义与 selfcheck B 段一致：报了全过，数却荒谬
            OK("不是安静失败（报全过同时给荒谬的数）",
               !(r3.Converged && r3.AllOk && (tmax > Materials.PtMeltC || resid > 50.0)));

            // 与定案档对账：同一套输入，页面路径算出来的必须与记录一致
            var fd = FinalDesign.Current;
            OK("与定案档合计铂重对得上（±1 g）",
               Math.Abs(r3.TotalMassG - fd.TotalMassG) < 1.0,
               $"实算 {r3.TotalMassG:0.0} g vs 记录 {fd.TotalMassG:0} g");
        }

        bool allOk3 = r3.AllOk;
        Console.WriteLine($"  全判据 {(allOk3 ? "✓ 全过" : "✗ 有不过的")}");

        // ═══════════════════════════════════════════════════════════
        H("③ → ④ 的门");
        {
            Call(main, "SyncGates");
            Pump(200);
            Console.WriteLine($"  FlowState: Last={(flow.Last is null ? "null" : "有")}"
                + $"　Ok={flow.Last?.Ok}　Converged={flow.Last?.Converged}　Fresh={flow.Fresh}");
            var g4 = Gate.Evaluate(StageId.定尺寸, flow);
            OK("③ 收敛之后 ④ 解锁", g4.Unlocked, g4.Unlocked ? "" : "★ " + g4.Why);
            var gs4 = Flow.Stage(StageId.整线核算).GateToUnlockNext!;
            OK("④ 的门是「收敛」而不是「判据全过」",
               gs4.RequireConverged && !gs4.RequireAllOk,
               $"RequireConverged={gs4.RequireConverged} RequireAllOk={gs4.RequireAllOk}");
            if (!g4.Unlocked) return Done();
        }

        // ═══════════════════════════════════════════════════════════
        H("④ 定尺寸 —— 从偏薄起点出发，看它往哪个方向收");
        double[] t0Plate, t1Plate;
        {
            var tp = (NumericUpDown[])F(line, "_tPlate")!;
            t0Plate = tp.Select(x => (double)x.Value).ToArray();

            // 起点压到 ×0.75：定尺寸器**必须往回正的方向走**（②′ 抽热为正），
            // 不允许它把板越调越薄去「省铂」—— 那是往烧断的方向优化。
            Set(line, "_suppressAuto", true);
            for (int i = 0; i < tp.Length; i++)
                tp[i].Value = Math.Max(tp[i].Minimum, (decimal)(t0Plate[i] * 0.75));
            Set(line, "_suppressAuto", false);
            var seed = tp.Select(x => (double)x.Value).ToArray();
            Console.WriteLine($"  起点厚度 {string.Join("/", seed.Select(v => v.ToString("0.00")))}（定案 ×0.75）");

            var t0 = Environment.TickCount64;
            Call(line, "RunAsync", true, false);
            bool fin = Wait(() => F(line, "_cts") is null, 900_000);
            OK("④ 在超时内跑完", fin, $"{(Environment.TickCount64 - t0) / 1000.0:0.0} s");

            t1Plate = tp.Select(x => (double)x.Value).ToArray();
            Console.WriteLine($"  收敛厚度 {string.Join("/", t1Plate.Select(v => v.ToString("0.00")))}");

            var r4 = (LineResult?)F(line, "LastResult");
            OK("④ 之后仍有结果", r4 is not null);
            if (r4 is not null)
            {
                Console.WriteLine($"  ②′ 抽热 {r4.ValueOf(LineResult.Key.NetFlux):0.000} W　③ 温降 {r4.ValueOf(LineResult.Key.FlangeDip):0.000} K");
                OK("②′ 回正（> 0）—— 不许往烧断方向优化", r4.ValueOf(LineResult.Key.NetFlux) > 0,
                   $"{r4.ValueOf(LineResult.Key.NetFlux):0.000} W");
                OK("定尺寸没有把板调得比起点还薄",
                   t1Plate.Zip(seed).All(p => p.First >= p.Second - 1e-6),
                   string.Join("/", t1Plate.Zip(seed).Select(p => $"{p.First:0.00}≥{p.Second:0.00}")));
                OK("每片都不低于工艺下界",
                   t1Plate.All(v => v >= 0.60 - 1e-6),
                   $"最薄 {t1Plate.Min():0.00} mm（下界 0.60）");
            }
        }

        // ═══════════════════════════════════════════════════════════
        H("④ → ⑤ 的门");
        {
            Call(main, "SyncGates");
            Pump(200);
            var g5 = Gate.Evaluate(StageId.交付, flow);
            var gs5 = Flow.Stage(StageId.定尺寸).GateToUnlockNext!;
            OK("④→⑤ 的门是「判据全过」", gs5.RequireAllOk, $"RequireAllOk={gs5.RequireAllOk}");
            var rr = (LineResult?)F(line, "LastResult");
            bool ok = rr?.AllOk == true;
            OK($"⑤ 的锁态与「判据全过」一致", g5.Unlocked == ok,
               $"全过={ok}　⑤解锁={g5.Unlocked}" + (g5.Unlocked ? "" : "　" + g5.Why));
            if (!g5.Unlocked)
            {
                Console.WriteLine("  ⇒ ⑤ 未解锁是**正确行为**（判据没全过不许出图）。");
                Console.WriteLine("     不过的判据：");
                foreach (var c in rr!.Checks.Where(c => !c.Ok))
                    Console.WriteLine($"       · {c.Name}　{c.Actual:0.000} / {c.Limit:0.000}　{c.Note}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        H("⑤ 交付 —— 出图闸门（不真写文件，只验闸门判得对）");
        {
            // ⚠ 这两个都是 **string 且永不为 null**：`""` 才表示「放行 / 有效」。
            //   头一版写成 `why is not null` / `fd.Invalid is not null` ⇒ 两边恒为 true
            //   ⇒ `true == true` **四条断言全部空转还打 ✓**，标签也一律印成「已声明失效」。
            //   典型的「空集通过的断言」（HANDOVER §7）—— 假绿灯比没有断言更坏。
            int pass = 0, block = 0;
            foreach (var fd in FinalDesign.All)
            {
                bool declaredInvalid = fd.Invalid.Length > 0;
                bool blocked = ((string)Call(line, "ExportBlockedReason", fd)!).Length > 0;
                if (blocked) block++; else pass++;
                OK($"{fd.Name}：{(declaredInvalid ? "已声明失效 → 应拦截" : "有效档 → 应放行")}",
                   blocked == declaredInvalid,
                   blocked ? "已拦" : "放行");
            }
            // 自证：若四个档给出的结论完全一致，说明这一节根本没在分辨什么。
            OK("这一节确实分辨出了两类（否则等于没测）", pass > 0 && block > 0,
               $"放行 {pass} 个 / 拦截 {block} 个");
        }

        return Done();
    }

    static string Indent(string s, int maxLines)
        => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Take(maxLines).Select(l => "    " + l));

    static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_bad == 0 ? "★ 全程走通，逐步核对无误" : $"✗ {_bad} 项不对");
        return _bad;
    }
}
