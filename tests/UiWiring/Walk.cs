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
                // 容差按几何规模放宽：这张图的法兰面积是设计记录的四倍多，
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

        // ═══════════════════════════════════════════════════════════
        H("④0 舌保温扫描：一裸就净抽热、一全包又净倒灌 —— 中间那个零点在哪");
        {
            // 用户 2026-08-23：「0.5-1.0 一定有解，加厚造成抽热，那就加厚保温」。
            // ShellThermal 的参数文档也早写着这一条。此前 .3dm 路径把舌保温**写死为裸露**，
            // 所以那个零点根本不在可达范围内 —— 现在它是个可调量了。
            var ti = (NumericUpDown)F(line, "_tabIns3dm")!;
            var tp0 = (NumericUpDown[])F(line, "_tPlate")!;
            Set(line, "_suppressAuto", true);
            foreach (var n in tp0) n.Value = 1.0m;          // 图纸原尺寸
            Set(line, "_suppressAuto", false);
            double[] tins = { 0.0, 0.2, 0.4, 0.8, 1.5 };
            Console.WriteLine($"  {"舌保温 mm",11}{"③ 温降 K",12}{"②′ W",11}{"②″ K",10}{"法兰最高 °C",13}  判定");
            var rec = new System.Collections.Generic.List<(double t, double dip, double flux)>();
            foreach (double t in tins)
            {
                Set(line, "_suppressAuto", true); ti.Value = (decimal)t; Set(line, "_suppressAuto", false);
                Call(line, "RunAsync", false, false);
                if (!Wait(() => F(line, "_cts") is null, 600_000)) { Console.WriteLine($"  {t,11:0.00}　★ 超时"); continue; }
                var rt = (LineResult?)F(line, "LastResult");
                if (rt is null) { Console.WriteLine($"  {t,11:0.00}　★ 无结果"); continue; }
                double dip = rt.ValueOf(LineResult.Key.FlangeDip), fx = rt.ValueOf(LineResult.Key.NetFlux);
                rec.Add((t, dip, fx));
                Console.WriteLine($"  {(t == 0 ? "0（裸舌）" : t.ToString("0.00")),11}{dip,12:0.0}{fx,11:0.00}"
                                + $"{rt.ValueOf(LineResult.Key.DiscTemp),10:0.00}"
                                + $"{(rt.Flanges.Length > 0 ? rt.Flanges.Max(f => f.TMaxC) : 0),13:0.0}"
                                + "  " + (rt.AllOk ? "✓ 全过" : "✗ " + string.Join("/", rt.Failed.Take(1))));
            }
            OK("扫描点都算出来了", rec.Count == tins.Length, $"{rec.Count}/{tins.Length}");
            if (rec.Count >= 2)
            {
                double best = rec.Min(x => Math.Abs(x.dip));
                bool crossed = rec.Any(x => x.flux > 0) && rec.Any(x => x.flux < 0);
                OK("②′ 在扫描区间内确实过零（存在那个零点）", crossed,
                   crossed ? "抽热与倒灌两侧都出现了" : "★ 整段同号 —— 零点不在这个区间");
                OK("★ 舌保温能把 ③ 压到限值（10 K）以内", best <= 10.0,
                   best <= 10.0 ? $"最好 |③| = {best:0.0} K" : $"最好也只有 |③| = {best:0.0} K");
            }
            Set(line, "_suppressAuto", true); ti.Value = 0m; Set(line, "_suppressAuto", false);
        }

        // ═══════════════════════════════════════════════════════════
        H("④a 厚度灵敏度：厚度到底救不救得了 ③？");
        {
            // 逐级定厚跑满 30 分钟没收敛（实测）。与其等它，不如**先量清楚**
            // 厚度这个旋钮对 ③ 有多大权限 —— 若整个可行区间都远在限值之外，
            // 那就不是「迭代不够」，是**这个形状没有解**（§设计记录重解 里同一种判断）。
            var tp2 = (NumericUpDown[])F(line, "_tPlate")!;
            double[] ks = { 0.5, 1.0, 2.0, 3.0 };
            Console.WriteLine($"  {"厚度标度",10}{"③ 温降 K",12}{"②′ W",10}{"合计 g",12}{"最高温 °C",11}");
            var got = new System.Collections.Generic.List<(double k, double dip)>();
            foreach (double k in ks)
            {
                Set(line, "_suppressAuto", true);
                foreach (var n in tp2) n.Value = (decimal)k;
                Set(line, "_suppressAuto", false);
                Call(line, "RunAsync", false, false);
                if (!Wait(() => F(line, "_cts") is null, 600_000))
                { Console.WriteLine($"  ×{k:0.0}　★ 超时"); continue; }
                var rk = (LineResult?)F(line, "LastResult");
                if (rk is null) { Console.WriteLine($"  ×{k:0.0}　★ 无结果"); continue; }
                double dip = rk.ValueOf(LineResult.Key.FlangeDip);
                got.Add((k, dip));
                Console.WriteLine($"  {"×" + k.ToString("0.0"),10}{dip,12:0.0}"
                                + $"{rk.ValueOf(LineResult.Key.NetFlux),10:0.0}{rk.TotalMassG,12:0}"
                                + $"{(rk.Flanges.Length > 0 ? rk.Flanges.Max(f => f.TMaxC) : 0),11:0.0}");
            }
            OK("四个厚度点都算出来了", got.Count == ks.Length, $"{got.Count}/{ks.Length}");
            if (got.Count >= 2)
            {
                double best = got.Min(t => t.dip);
                OK("★ 厚度能把 ③ 压到限值（10 K）以内", best <= 10.0,
                   best <= 10.0 ? $"最好 {best:0.0} K"
                   : $"★ 整个扫描区间里最好也只有 {best:0.0} K —— **这个形状没有解**，"
                     + "不是迭代不够。要改的是盘径与舌长（回 Rhino 改图）");
            }
            // 复原到图纸原尺寸
            Set(line, "_suppressAuto", true);
            foreach (var n in tp2) n.Value = 1.0m;
            Set(line, "_suppressAuto", false);
        }

        // ═══════════════════════════════════════════════════════════
        H("④ 逐级定厚（.3dm 走 FlangeAutoSizer.SolveByLevel）");
        {
            Call(main, "SyncGates"); Pump(150);
            var g4 = Gate.Evaluate(StageId.定尺寸, flow);
            OK("③ 收敛之后 ④ 解锁", g4.Unlocked, g4.Unlocked ? "" : "★ " + g4.Why);
            if (!g4.Unlocked) return Done();

            var lv0 = (double[][]?)F(line, "_levels");
            Console.WriteLine($"  起点各级厚度 {string.Join("/", lv0![0].Select(v => v.ToString("0.00")))}");
            var t0 = Environment.TickCount64;
            Call(line, "RunAsync", true, false);
            bool fin = Wait(() => F(line, "_cts") is null, 1_800_000);
            OK("④ 在超时内跑完", fin, $"{(Environment.TickCount64 - t0) / 1000.0:0.0} s");

            var r4 = (LineResult?)F(line, "LastResult");
            OK("④ 之后仍有结果", r4 is not null);
            if (r4 is not null)
            {
                var sc = (double[][]?)F(line, "_levelScale");
                if (sc is { Length: > 0 })
                    Console.WriteLine($"  各级标度 {string.Join("/", sc[0].Select(v => v.ToString("0.000")))}");
                Console.WriteLine($"  ③ 温降 {r4.ValueOf(LineResult.Key.FlangeDip):0.000} K"
                                + $"　②′ {r4.ValueOf(LineResult.Key.NetFlux):0.000} W"
                                + $"　合计 {r4.TotalMassG:0.0} g");
                OK("④ 把 ③ 往下压了", r4.ValueOf(LineResult.Key.FlangeDip) < r.ValueOf(LineResult.Key.FlangeDip),
                   $"{r.ValueOf(LineResult.Key.FlangeDip):0.0} → {r4.ValueOf(LineResult.Key.FlangeDip):0.0} K");
                foreach (var c in r4.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
                    Console.WriteLine($"    {(c.Undetermined ? "⚠" : c.Ok ? "✓" : "✗")} {c.Name,-24}"
                                    + $"{(double.IsNaN(c.Actual) ? "达不到" : c.Actual.ToString("0.000")),12} / {c.Limit,-10:0.000}");
                Console.WriteLine($"  全判据 {(r4.AllOk ? "✓ 全过" : "✗ 有不过的")}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        H("⑤ 交付：出图 + 报告");
        {
            Call(main, "SyncGates"); Pump(150);
            var g5 = Gate.Evaluate(StageId.交付, flow);
            var rr = (LineResult?)F(line, "LastResult");
            OK("⑤ 的锁态与「判据全过」一致", g5.Unlocked == (rr?.AllOk == true),
               $"全过={rr?.AllOk}　⑤解锁={g5.Unlocked}");
            if (!g5.Unlocked)
            {
                Console.WriteLine("  ⇒ ⑤ 未解锁是**正确行为**。挡住它的：");
                foreach (var c in rr!.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)
                                            .Where(c => !c.Ok || c.Undetermined))
                    Console.WriteLine($"    · {c.Name}　"
                                    + (c.Undetermined ? "无法判定" : $"{c.Actual:0.000} / {c.Limit:0.000}")
                                    + (c.Note.Length > 0 ? "　" + c.Note.Split('。')[0] : ""));
                return Done();
            }

            string outDir = Path.Combine(RepoRootOf(file), "deliverable");
            Directory.CreateDirectory(outDir);
            string sub = Path.Combine(outDir, "Pt_Heater3_出图");
            if (Directory.Exists(sub)) Directory.Delete(sub, true);
            var t0 = Environment.TickCount64;
            try
            {
                Call(line, "ExportScaledTo", sub);
                var made = Directory.Exists(sub) ? Directory.GetFiles(sub, "*.3dm") : Array.Empty<string>();
                OK("3DM 出图：四片都出来了", made.Length == 4,
                   made.Length == 4
                     ? string.Join("、", made.Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length + "B"))
                       + $"　{(Environment.TickCount64 - t0)/1000.0:0.0} s"
                     : $"★ 只出了 {made.Length} 个");
                foreach (var f in made)
                    OK("  " + Path.GetFileName(f) + " 不是空文件", new FileInfo(f).Length > 1000);
            }
            catch (Exception ex) { OK("3DM 出图成功", false, ex.Message); }
        }

        return Done();
    }

    /// <summary>
    /// `--tabins0`：拿**设计记录几何**（Ø60、舌 140）把舌保温逐档减到 0，看 ③ 怎么走。
    ///
    /// ★ 为什么要单独做这个实验（用户 2026-08-23 问「舌保温 0.3–0.5 如果不保温呢」）：
    ///   `.3dm` 路径按**舌片裸露**建模（LineRunner: tabInsulThickMm = NaN，
    ///   注释「沿用现场实况『仅圆盘保温、舌片裸露』」），
    ///   而解析路径的设计记录用 0.3–0.5 mm 舌保温，APP 自己称它是「守 ②′/③ 的主力旋钮」。
    ///   ⇒ Pt_Heater3 的 ③ = 341 K 到底是**裸舌片**造成的，还是**盘 Ø120 / 舌 200 太大**？
    ///   把设计记录的舌保温减到 0，就把这两个病因分开了。
    ///
    /// ⚠ 本实验走**内核**（LineRunner.Run），不经界面 —— 它问的是物理，不是接线。
    /// </summary>
    public static int TabIns0()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var p = new DesignInputs();
        var fd = DesignSpec.Current;

        H($"舌保温 → ③：设计记录几何（{fd.Name}）　盘Ø{2 * fd.DiscRadiusMm:0}／舌 {fd.TabLengthMm:0}");
        Console.WriteLine($"  设计记录舌保温 {DesignSpec.Fmt(fd.TabInsulMm, "0.0")} mm");
        Console.WriteLine();
        Console.WriteLine($"  {"舌保温",10}{"③ 温降 K",12}{"②′ W",10}{"②″ K",10}{"合计 g",11}{"法兰最高 °C",13}  判定");

        double[] mult = { 1.0, 0.5, 0.25, 0.0 };
        foreach (double m in mult)
        {
            var d = fd.Clone();
            d.TabInsulMm = fd.TabInsulMm.Select(v => v * m).ToArray();
            var lc = d.BuildCase(p, checkRamp: true);
            var r = LineRunner.Run(lc);
            if (!r.Ok) { Console.WriteLine($"  ×{m:0.00}　★ 解不出：{r.Message}"); continue; }
            string tag = m == 0.0 ? "0（裸舌）" : DesignSpec.Fmt(d.TabInsulMm, "0.00");
            Console.WriteLine($"  {tag,10}{r.ValueOf(LineResult.Key.FlangeDip),12:0.0}"
                            + $"{r.ValueOf(LineResult.Key.NetFlux),10:0.00}"
                            + $"{r.ValueOf(LineResult.Key.DiscTemp),10:0.00}"
                            + $"{r.TotalMassG,11:0}"
                            + $"{(r.Flanges.Length > 0 ? r.Flanges.Max(f => f.TMaxC) : 0),13:0.0}"
                            + "  " + (r.AllOk ? "✓ 全过" : "✗ " + string.Join("/", r.Failed.Take(2))));
        }
        Console.WriteLine();
        Console.WriteLine("  ⇒ 与 Pt_Heater3.3dm（盘Ø120／舌 200／裸舌）的 ③ = 340.9 K 对照，");
        Console.WriteLine("    就能把「裸舌片」与「图纸太大」两个病因分开。");
        return Done();
    }

    /// <summary>
    /// `--map3dm &lt;file&gt;`：**厚度标度 × 舌保温** 的二维图，读 ③ 与 ②′。
    ///
    /// ★ 用户 2026-08-23：「把铂金厚度与保温厚度作为坐标轴，对应其温度，
    ///   在温差 &lt; 10 °C 的范围，推出一个铂金厚度与保温厚度」。
    ///
    /// 为什么要二维：两个旋钮**是耦合的**。一维扫描各自都不通 ——
    ///   · 只扫厚度（舌裸）：③ 最好 203 K，且 ×0.5 处 ②′ 已翻负；
    ///   · 只扫保温（×1.0）：②′ 全程 +71…+101 W，根本不过零。
    /// 但减薄会把舌片从「导热主导」推回「自发热主导」（截面 240 → 120 mm²，
    /// 接近设计记录的 54 mm²），**那里保温才重新有效**。两个一起扫才看得见。
    /// </summary>
    public static int Map3dm(string file)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(800);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(200);
        Set(line, "_userReady", true);

        var files = (TextBox[])F(line, "_file3dm")!;
        Set(line, "_suppressAuto", true);
        ((RadioButton)F(line, "_src3dm")!).Checked = true;
        foreach (var t in files) t.Text = file;
        ((TextBox)F(line, "_layer3dm")!).Text = "法兰";
        Set(line, "_suppressAuto", false);
        Call(line, "SyncGeomSource"); Pump(150);
        Call(line, "AnalyzeShape"); Pump(400);

        var tp = (NumericUpDown[])F(line, "_tPlate")!;
        var ti = (NumericUpDown)F(line, "_tabIns3dm")!;
        var sh = F(line, "_shape");
        // ★ 下界 k ≥ 0.6，不是 0.5（用户 2026-08-23：「0.5 以下没有意义」，理由更具体）：
        //   本图三级厚度是 1.0 @ R26–36 / 2.0 @ R36–46 / 3.0 @ R46–203，
        //   **最薄那级正是紧贴管孔、要焊到管子上的那一圈** ⇒ 受焊接工艺下界约束：
        //       k × 1.0 mm ≥ 0.6 mm  ⇒  k ≥ 0.6
        //   （舌片在 3.0 那级，与圆盘同板切出、无焊缝 ⇒ 不受该下界约束，见 §4.6。）
        //   k = 0.5 时那圈只有 0.5 mm，已经低于手工 TIG 烧穿下界 —— 数再好也造不出来。
        double[] ks = { 0.60, 0.75, 0.90, 1.00 };
        double[] ts = { 0.0, 0.5, 1.5 };

        H($"③ 温降 K 的二维图　行 = 厚度标度　列 = 舌保温 mm　（图纸 {Path.GetFileName(file)}）");
        Console.WriteLine("  三级原厚 1/2/3 mm ⇒ 标度 k 后为 k×(1/2/3)。限值 ③ ≤ 10 K，②′ 须 > 0。");
        Console.WriteLine("  ⚠ 最薄那级（R26–36，紧贴管孔、要焊管）受焊接下界约束 ⇒ k ≥ 0.60。");
        Console.WriteLine();
        Console.Write($"  {"k / 保温",10}");
        foreach (double t in ts) Console.Write($"{(t == 0 ? "裸舌" : t.ToString("0.0") + " mm"),16}");
        Console.WriteLine();

        var best = (dip: double.MaxValue, k: 0.0, t: 0.0, flux: 0.0);
        int ok10 = 0;
        foreach (double k in ks)
        {
            Console.Write($"  {"×" + k.ToString("0.00"),10}");
            foreach (double t in ts)
            {
                Set(line, "_suppressAuto", true);
                foreach (var n in tp) n.Value = (decimal)k;
                ti.Value = (decimal)t;
                Set(line, "_suppressAuto", false);
                Call(line, "RunAsync", false, false);
                if (!Wait(() => F(line, "_cts") is null, 600_000)) { Console.Write($"{"超时",16}"); continue; }
                var r = (LineResult?)F(line, "LastResult");
                if (r is null) { Console.Write($"{"—",16}"); continue; }
                double dip = r.ValueOf(LineResult.Key.FlangeDip), fx = r.ValueOf(LineResult.Key.NetFlux);
                if (Math.Abs(dip) < Math.Abs(best.dip)) best = (dip, k, t, fx);
                if (Math.Abs(dip) <= 10.0 && fx > 0) ok10++;
                // 「③/②′」一格里两个数：③ 决定判定，②′ 决定方向（负 = 往管里灌 = 烧断向）
                Console.Write($"{dip,9:0.0}/{fx,6:0.0}");
            }
            Console.WriteLine();
        }
        Console.WriteLine();
        Console.WriteLine($"  最好一格：k=×{best.k:0.00}　舌保温 {best.t:0.0} mm　⇒ ③ = {best.dip:0.0} K　②′ = {best.flux:0.0} W");
        OK("★ 网格里存在 ③ ≤ 10 K 且 ②′ > 0 的点", ok10 > 0,
           ok10 > 0 ? $"{ok10} 个"
           : $"★ 一个都没有 —— 最好 |③| = {Math.Abs(best.dip):0.0} K（限 10）。"
             + "**这个形状没有解**：要改的是舌片截面（80×3 mm² 是通往 300 °C 铜排的粗导热桥），"
             + "不是厚度也不是保温");
        return Done();
    }

    /// <summary>
    /// `--export3dm &lt;file&gt; [k]`：只做「切 .3dm → 分析 → 按标度 k 出图」，跳过所有扫描。
    /// 出完之后**用探针把输入与输出逐项量一遍**，回答「形状变没变」。
    /// </summary>
    public static int Export3dm(string file, double k)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(800);
        void Force(Control c) { _ = c.Handle; foreach (Control kk in c.Controls) Force(kk); }
        Force(main); Pump(200);
        Set(line, "_userReady", true);

        var files = (TextBox[])F(line, "_file3dm")!;
        Set(line, "_suppressAuto", true);
        ((RadioButton)F(line, "_src3dm")!).Checked = true;
        foreach (var t in files) t.Text = file;
        ((TextBox)F(line, "_layer3dm")!).Text = "法兰";
        Set(line, "_suppressAuto", false);
        Call(line, "SyncGeomSource"); Pump(150);
        Call(line, "AnalyzeShape"); Pump(400);

        var tp = (NumericUpDown[])F(line, "_tPlate")!;
        Set(line, "_suppressAuto", true);
        foreach (var n in tp) n.Value = (decimal)k;
        Set(line, "_suppressAuto", false);

        string dir = Path.Combine(RepoRootOf(file), "deliverable", "Pt_Heater3_出图");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        H($"出图：厚度 ×{k:0.00}　→ {dir}");
        Call(line, "ExportScaledTo", dir);
        var made = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.3dm").OrderBy(x => x).ToArray()
                                         : Array.Empty<string>();
        OK("四片都出来了", made.Length == 4, string.Join("、", made.Select(Path.GetFileName)));
        if (made.Length == 0) return Done();

        // ── 形状变没变：用探针量输入与输出，逐项比
        H("形状核对：输出 vs 原图（只该厚度变，轮廓/孔/槽/阶梯半径一律不动）");
        var a = Geometry3dm.LoadThickness(file, "法兰", double.NaN, 0.5);
        var b = Geometry3dm.LoadThickness(made[0], "法兰", double.NaN, 0.5);
        var sa = PlateShapeAnalyzer.Analyze(a);
        var sb = PlateShapeAnalyzer.Analyze(b);

        void Cmp(string name, double x, double y, double tol, string unit = "mm")
            => OK(name, Math.Abs(x - y) <= tol,
                  $"原 {x:0.000} → 出 {y:0.000} {unit}（差 {y - x:+0.000;-0.000}）");

        Cmp("管孔半径不变", sa.HoleRadiusMm, sb.HoleRadiusMm, 0.51);
        Cmp("圆盘外半径不变", sa.DiscRadiusMm, sb.DiscRadiusMm, 0.51);
        Cmp("舌端 X 不变", sa.TabEndXMm, sb.TabEndXMm, 0.51);
        Cmp("舌端半宽不变", sa.TabEndHalfWidthMm, sb.TabEndHalfWidthMm, 0.51);
        Cmp("净面积不变（轮廓与开槽都没动）", sa.NetAreaMm2, sb.NetAreaMm2, sa.NetAreaMm2 * 0.005, "mm²");
        OK("开槽数不变", sa.Slot.Count == sb.Slot.Count, $"原 {sa.Slot.Count} → 出 {sb.Slot.Count}");
        OK("分级数不变", sa.Levels.Count == sb.Levels.Count, $"原 {sa.Levels.Count} → 出 {sb.Levels.Count}");

        Console.WriteLine();
        Console.WriteLine($"  {"级",4}{"原厚 mm",12}{"出厚 mm",12}{"实际倍数",12}{"半径范围 mm",22}");
        for (int i = 0; i < Math.Min(sa.Levels.Count, sb.Levels.Count); i++)
        {
            var la = sa.Levels[i]; var lb = sb.Levels[i];
            Console.WriteLine($"  {i + 1,4}{la.ThicknessMm,12:0.000}{lb.ThicknessMm,12:0.000}"
                            + $"{lb.ThicknessMm / la.ThicknessMm,12:0.0000}"
                            + $"{la.RInnerMm.ToString("0.0") + " – " + la.ROuterMm.ToString("0.0"),22}");
            Cmp($"  第 {i + 1} 级半径范围内径不变", la.RInnerMm, lb.RInnerMm, 0.51);
            Cmp($"  第 {i + 1} 级厚度确实 ×{k:0.00}", lb.ThicknessMm, la.ThicknessMm * k, 0.011);
        }
        Cmp("体积按倍数缩放", sb.VolumeMm3, sa.VolumeMm3 * k, sa.VolumeMm3 * k * 0.01, "mm³");
        return Done();
    }

    /// <summary>从给定文件往上找仓库根（有 .git 的那一层）。</summary>
    static string RepoRootOf(string anyPath)
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(anyPath))!);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return Path.GetDirectoryName(Path.GetFullPath(anyPath))!;
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
            Console.WriteLine($"  当前设计记录      {DesignSpec.Current.Name}");
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

            // 按说明书教的用法走：载入设计记录 → 核算整线。
            // 直接拿出厂默认去解，等于用一个自己都说造不出来的几何去跑分钟级耦合解。
            Call(line, "LoadDesignSpec");
            Pump(400);
            Console.WriteLine($"  已载入设计记录「{DesignSpec.Current.Name}」⇒ 壁厚 {(double)w.Value:0.00} mm、"
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

            // 与设计记录对账：同一套输入，页面路径算出来的必须与记录一致
            var fd = DesignSpec.Current;
            OK("与设计记录合计铂重对得上（±1 g）",
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
            Console.WriteLine($"  起点厚度 {string.Join("/", seed.Select(v => v.ToString("0.00")))}（设计记录 ×0.75）");

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
            foreach (var fd in DesignSpec.All)
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

    // ════════════════════════════════════════════════════════════════════
    //  `UiWiring.exe --follow`
    //
    //  **完全照着链路提示走一遍**（用户 2026-08-25 验收要求）：
    //  每一步先问 `Flow.Next`「现在该点哪个」，然后就点它，直到它指向出图。
    //
    //  与 `--walk` 的分工：
    //    · `--walk` 照**写死的顺序** ①→⑤ 走，验的是「每一步算得对不对」。
    //    · `--follow` 由**提示本身**决定走哪，验的是「跟着提示走，走不走得到交付」。
    //      提示要是指到点不了的按钮、原地打转、或者漏掉必经步骤，跟着走的人一定撞上。
    //      —— 写死顺序的走查器**永远发现不了这一类**：它根本没在读提示。
    // ════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ★★★ <paramref name="loadWall"/>：**先载入这一档设计记录再走**（2026-09-02 补）。
    ///
    /// 病灶：本走查一直只从**开箱默认**出发，而那个几何很坏
    /// （实测 管孔净流入 −883.8 W、圆盘区最高温 292.1 K）⇒ 自动定厚磨很久，
    /// 2026-08-30 与 09-02 两次都在这一步**超时**，于是链路后半段
    /// （网格无关复核 → 出图）**一次都没被走到过**。
    ///
    /// 而工程师的真实第一步就是「载入设计记录」（跑单第 ① 条就是这么写的）。
    /// ⇒ 补这条不是放水，是把走查对准他真正走的那条路。
    /// 开箱默认那条仍然留着（不传参即是），它答的是另一个问题：
    /// 「从零开始，提示带不带得动人」。
    /// </summary>
    public static int Follow(string? file3dm = null, double loadWall = double.NaN)
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
        Pump(1200);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(300);

        // ── .3dm 那条路：先把 APP 切进图纸模式，再照提示走。
        //    这条路的提示分支比解析路多：要先「分析几何变数」，⑤⑥ 在分析前必然「无法判定」，
        //    而「◇ 搜形状」在这个模式下**不适用**（形状由图纸给定，不是可搜索的自由度）。
        if (file3dm is not null)
        {
            if (!File.Exists(file3dm))
            { OK("样件在", false, "★ 缺 " + file3dm); return _bad; }
            if (Geometry3dm.FindProbe() is null)
            { OK("几何探针在", false, "★ 缺 Pt_Optimize.Geom.exe"); return _bad; }
            var files = (TextBox[])F(line, "_file3dm")!;
            Set(line, "_suppressAuto", true);
            ((RadioButton)F(line, "_src3dm")!).Checked = true;
            foreach (var t in files) t.Text = file3dm;
            ((TextBox)F(line, "_layer3dm")!).Text = "法兰";
            Set(line, "_suppressAuto", false);
            Call(line, "SyncGeomSource");
            Pump(200);
            Call(main, "SyncGates"); Pump(150);
            H("输入：" + Path.GetFileName(file3dm) + "（.3dm 模式，图层「法兰」）");
        }

        bool App(string id) => (bool)typeof(LineDesignPage).GetMethod("CommandApplicable",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(line, new object[] { id })!;

        H("从**开箱默认**出发，完全照链路提示走");
        Console.WriteLine("  规则：每一步只问「提示说该点哪个」，然后就点它。不看攻略、不抄近路。");

        // ── 先载入设计记录（可选）。与 Reconcile 的第一步是同一套动作。
        if (!double.IsNaN(loadWall))
        {
            var box = (ToolStripComboBox)F(line, "_caseBox")!;
            int pick = -1;
            for (int i = 0; i < box.Items.Count; i++)
                if ((box.Items[i]?.ToString() ?? "").Contains(loadWall.ToString("0.0"), StringComparison.Ordinal))
                { pick = i; break; }
            OK($"下拉里找得到管壁 {loadWall:0.0} 那一档", pick >= 0,
               pick >= 0 ? box.Items[pick]!.ToString()! : "★ 没有这一档 —— 工程师无从开始");
            if (pick < 0) return _bad;
            box.SelectedIndex = pick; Pump(200);
            Call(line, "LoadDesignSpec"); Pump(400);
            Call(main, "SyncGates"); Pump(150);
            H($"起点：已载入设计记录（管壁 {loadWall:0.0}）—— 这是工程师的第一个动作");
        }

        var hist = new List<string>();
        string lastId = "";
        // ★ 「做了但没变」：光看「过没过」不够 —— .3dm 路实测到自动定厚**跑完一个数都没动**，
        //   而提示继续指它 ⇒ 死循环。指纹取判据实测值 + 总铂，逐步比。
        string lastFinger = "";
        int repeat = 0;

        for (int step = 1; step <= 12; step++)
        {
            var ns = Flow.Next(flow, App);
            Console.WriteLine();
            Console.WriteLine($"──── 第 {step} 步 ────");

            if (ns is null)
            {
                OK($"第 {step} 步：提示给得出下一步", false,
                   flow.Running is not null ? "★ 提示说「正在算」——但这一步不该在算"
                                            : "★ 提示是空的：跟着走的人到这里就断了");
                break;
            }

            // ⚠ 审视导引就得看**工程师真正看到的那一行** —— 面板会在 Why 前面
            //   加上「点哪个按钮、在哪一页、要多久」。头一版只打 ns.Why，
            //   于是我据此说了一句「提示没告诉人这要多久」—— **那是走查器的缺陷，不是 APP 的**。
            var cmdSpec0 = ns.CmdId.Length > 0 ? Flow.Cmd(ns.CmdId) : null;
            Console.WriteLine("  面板那一行：" + (cmdSpec0 is null
                ? "下一步 → " + ns.Why
                : $"下一步 → 点「{cmdSpec0.Text}」（{Flow.Stage(cmdSpec0.Stage).Title}，{cmdSpec0.Cost}）"
                  + Environment.NewLine + "　　　　　　" + ns.Why));
            Console.WriteLine($"  它指向　：{(ns.CmdId.Length == 0 ? "（不指按钮，只给说明）" : ns.CmdId)}");

            if (ns.CmdId.Length == 0)
            {
                // ⚠ 「只给说明、不指按钮」**不是失败**（2026-08-25 更正）：
                //   有些出路本来就不在 APP 里 —— 「回 Rhino 给圆盘分级」没有按钮，
                //   「改厚度标度 k」是页面上的数值框而不是命令。
                //   头一版把它一律判成不过，那是照**解析路**写的规则套到 .3dm 路上。
                //   真正该守的是：它**说不说得出具体动作** —— 空话才是坑。
                OK($"第 {step} 步：交回给人时说得出具体动作",
                   ns.Why.Length >= 20,
                   ns.Why.Length >= 20 ? "" : "★ 只有一句空话：" + ns.Why);
                Console.WriteLine();
                Console.WriteLine($"  ■ 指路到此**把决定权交回给人**（{step - 1} 步）——");
                Console.WriteLine("     它要的动作 APP 里没有对应按钮，这是诚实的，不是断掉。");
                Console.WriteLine($"     路径：{string.Join(" → ", hist)}");
                DumpChecks(line, "交回给人时的判据表");
                return _bad;
            }

            // ── 坑 ①：它指的按钮，此刻真的能点吗
            var spec = Flow.Commands.FirstOrDefault(c => c.Id == ns.CmdId);
            OK($"第 {step} 步：「{ns.CmdId}」这个命令真实存在", spec is not null);
            if (spec is null) break;
            var blk = Gate.Blocks(spec, flow, App);
            string blkWhy = blk switch
            {
                Gate.Block.Busy => "有链在跑（提示不该在这时候指命令）",
                Gate.Block.NotApplicable => "当前几何来源下这个命令不适用",
                Gate.Block.Gate => "阶段门禁没开",
                _ => "",
            };
            OK($"第 {step} 步：它指的按钮此刻点得动", blk == Gate.Block.None,
               blk == Gate.Block.None ? "" : $"★ 被拦：{blkWhy}　——**提示指了一个点不动的按钮**");
            if (blk != Gate.Block.None) break;

            // ── 坑 ②：提示原地打转
            repeat = ns.CmdId == lastId ? repeat + 1 : 0;
            lastId = ns.CmdId;
            hist.Add(ns.CmdId);
            OK($"第 {step} 步：提示没有原地打转", repeat < 2,
               repeat < 2 ? "" : $"★「{ns.CmdId}」连指 {repeat + 1} 次，跟着走会死循环");
            if (repeat >= 2) break;

            // ── 坑 ③：**蓝色索引**（点那一行会切页签 + 闪按钮）指得到东西吗
            //
            //   蓝键与提示文字读的是**同一个** Flow.Next ⇒ 目标一致，这半没问题。
            //   但索引的另一半是 MainForm 的 NextStepRequested：
            //     · 按 spec.Stage 找页签（_stageOf 里没有 ⇒ **不切页，静默**）
            //     · FlashCommand(spec.Text) 按**显示文字精确匹配**找按钮
            //       （找不到就 `return` ⇒ **带你切了页却什么都不闪**，站在那页上不知道点哪）
            //   两条都是静默失败，界面上看不出来 —— 所以必须在这里验。
            {
                var stageOf = (System.Collections.IDictionary)F(main, "_stageOf")!;
                bool hasTab = false;
                foreach (System.Collections.DictionaryEntry e in stageOf)
                    if (e.Value is StageId sid && sid == spec.Stage) hasTab = true;
                OK($"第 {step} 步：蓝键切得到「{spec.Stage}」那一页", hasTab,
                    hasTab ? "" : "★ _stageOf 里没有这个阶段 ⇒ 点蓝键不切页，且不报错");

                var found = new List<string>();
                foreach (TabPage tp in tabs.TabPages)
                    foreach (var ts in tp.Controls.OfType<ToolStrip>())
                        foreach (var b in ts.Items.OfType<ToolStripButton>())
                            if (b.Text == spec.Text) found.Add(tp.Text);
                OK($"第 {step} 步：蓝键闪得到「{spec.Text}」这个按钮", found.Count > 0,
                    found.Count > 0 ? "在：" + string.Join("、", found)
                                    : "★ 全窗口没有一个按钮的文字等于它 ⇒ FlashCommand 静默 return");
            }


            // ── 到终点了？
            if (ns.CmdId is "export.page3dm" or "final.export3dm")
            {
                Console.WriteLine();
                Console.WriteLine($"  ★ 提示指向出图 —— **跟着提示走，{step - 1} 步走到了交付**。");
                Console.WriteLine($"     路径：{string.Join(" → ", hist)}");
                Console.WriteLine("     （本走查不真写档，到此为止）");
                DumpChecks(line, "交付前最终判据表");
                return _bad;
            }

            // ── 点它
            Console.WriteLine($"  ⇒ 点「{spec.Text}」…");
            switch (ns.CmdId)
            {
                case "core.runLine":
                    Call(line, "RunAsync", false, false);
                    if (!Wait(() => F(line, "_cts") is null && F(line, "LastResult") is not null, 900_000))
                    { OK("整线解在预算内跑完", false, "★ 超时"); return _bad; }
                    break;
                case "core.autoThick":
                    Call(line, "RunAsync", true, false);
                    // ⚠ 60 分钟，与 Reconcile 对齐（2026-09-02）。原来是 30 分钟 ——
                    //   一个随手写的数，而它正是让链路后半段**从没被走到过**的原因。
                    if (!Wait(() => F(line, "_cts") is null, 3_600_000))
                    { OK("自动定厚在预算内跑完", false, "★ 超时"); return _bad; }
                    break;
                case "geom.analyze":
                    // 把图纸反推成各级台阶。不先做这一步就解，⑤⑥ 仍是「无法判定」，
                    // 那一分多钟等于白跑 —— 提示把它排在解之前，正是为了这个。
                    Call(line, "AnalyzeShape");
                    if (!Wait(() => F(line, "_cts") is null, 600_000))
                    { OK("分析几何变数在预算内跑完", false, "★ 超时"); return _bad; }
                    break;
                // ★★★★★ 2026-09-02 补。在此之前 switch 里**没有这一条** ——
                //   链路一指向「◆ 网格无关复核」就落进 default「走查器没实作这个命令」
                //   然后返回。⇒ **端到端从来没有走到过出图**，而我先前把这件事
                //   报成「在自动定厚超时」。那是错的：不是慢，是**结构性走不到**。
                //
                //   ⚠ 不许缩水成「跑两档就算」：⑤ 交付的门信的就是这一步，
                //     糊弄它等于把门拆了。复核该跑多久就跑多久（实测 10–40 分钟）。
                case "core.verifyMesh":
                {
                    var swV = System.Diagnostics.Stopwatch.StartNew();
                    Call(line, "VerifyMeshAsync");
                    // ⚠ VerifyMeshAsync 在前置不满足时（没解 / 解不新鲜）**直接 return
                    //   且不设 _cts** ⇒ 只等「_cts 变空」会把「根本没起跑」误判成「跑完了」。
                    //   而 _cts 是在第一个 await 之前同步赋的 ⇒ Call 返回时就该已经非空。
                    if (F(line, "_cts") is null)
                    {
                        OK($"第 {step} 步：复核真的起跑了", false,
                           "★ 它当场返回了 —— 多半是「先解出一个**当前参数**的解」那条前置没满足。"
                           + "　提示指了一个点下去没反应的按钮，那是指路的错");
                        return _bad;
                    }
                    if (!Wait(() => F(line, "_cts") is null, 2_700_000))
                    { OK("网格无关复核在 45 分钟预算内跑完", false, "★ 超时"); return _bad; }
                    Console.WriteLine($"     复核用时 {swV.Elapsed.TotalMinutes:0.0} 分");
                    // ★ 2026-09-02：把复核**自己那句结论**印出来（含收敛在哪个网格、多少单元）。
                    //   此前只印用时，而下面「输出框前 6 行」把结论截掉了 ⇒
                    //   走完之后我手上有判据值却**没有它是在哪个网格上得到的** ——
                    //   一个没有网格口径的复核值，没法跟档里存的那个比。
                    if (F(line, "_meshVerify") is MeshVerify.Result mv)
                    {
                        Console.WriteLine($"     收敛网格 {mv.FineMm:0.000} mm　{mv.Cells} 单元　"
                                        + $"收敛 {(mv.Converged ? "✓" : "✗")}");
                        Console.WriteLine("     结论：" + mv.Verdict.Replace("**", ""));
                        // ★ 直接从**复核解那个对象**印，3 位小数 —— 档里的格式就是 3 位。
                        //   走查那张判据表印 2 位，拿它往档里抄就是「再抄一份精度不足的数」，
                        //   而本档今天出的事正是抄数抄出来的。
                        if (mv.Line is { } ml)
                            foreach (string k in new[] { LineResult.Key.FlangeDip,
                                                         LineResult.Key.NetFlux,
                                                         LineResult.Key.DiscTemp })
                            {
                                var c0 = ml.Checks.FirstOrDefault(x => x.Name == k);
                                if (c0 is not null)
                                    Console.WriteLine($"     落档用　{Criteria.Plain(k)}"
                                                    + $"	{c0.Actual:0.000}	/ {c0.Limit:0.000}"
                                                    + $"	{(c0.Ok ? "过" : "**不过**")}");
                            }
                    }
                    // 复核跑完 ≠ 验过。判据仍随网格变时 MeshVerified 是 false，
                    // 出图的门照样关着 —— 那是 APP 对的，但跟着提示走的人到不了终点。
                    OK($"第 {step} 步：复核之后出图的门认账了",
                       flow.MeshVerified && flow.VerifiedFresh,
                       flow.MeshVerified ? "" : "★ 判据还在随网格变 ⇒ **没验过**，门仍然关着");
                    break;
                }
                case "shape.search":
                    Console.WriteLine("     ⚠ 提示指向「◇ 搜形状」——**几十分钟**，本走查不跑。");
                    Console.WriteLine("        这本身是一条结论：开箱默认走到这里就需要改几何，");
                    Console.WriteLine("        而提示确实把人指到了对的那个按钮（厚度救不了几何判据）。");
                    DumpChecks(line, "停在这一步时的判据表");
                    return _bad;
                default:
                    OK($"第 {step} 步：本走查认得「{ns.CmdId}」怎么点", false,
                       "★ 走查器没实作这个命令 —— 不是 APP 的错，是本走查覆盖不到");
                    return _bad;
            }

            Pump(300);
            DumpChecks(line, $"第 {step} 步之后的判据表");
            // ★ 把**输出框**里这一步写的话也抓出来 —— 定尺寸器自己会说
            //   「收敛 / 顶死 / 无解」。指路若没读它，那是指路的错；
            //   它若什么都没说，那是引擎的错。两者要分清才知道该修哪边。
            if (F(line, "_out") is Control ob && ob.Text.Length > 0)   // RichTextBox，不是 TextBox
            {
                var tail = ob.Text.Replace(((char)13).ToString(), "").Split((char)10)
                            .Where(x => x.Trim().Length > 0).Take(6).ToArray();
                Console.WriteLine("  输出框（前 6 行）：");
                foreach (var t in tail) Console.WriteLine("     " + t.Trim());
            }
            string finger = Finger(line);
            if (finger.Length > 0 && finger == lastFinger)
                OK($"第 {step} 步：这一步**起作用了**", false,
                   $"★「{ns.CmdId}」跑完，判据表与总铂**逐字未变** —— "
                   + "做了等于没做，而提示只看「过没过」，会继续指同一个按钮");
            lastFinger = finger;
        }

        Console.WriteLine();
        Console.WriteLine(_bad == 0 ? "★ 链路提示走查通过" : $"✗ 链路提示走查：{_bad} 项不过");
        return _bad;
    }

    /// <summary>判据实测值 + 总铂的指纹 —— 用来认出「这一步跑完什么都没变」。</summary>
    static string Finger(LineDesignPage line)
    {
        var r = F(line, "LastResult") as LineResult;
        if (r is null) return "";
        return string.Join("|", r.Checks.Where(c => c.Kind != CheckKind.Reference)
                                        .Select(c => c.Name + "=" + c.Actual.ToString("0.000")))
             + "|g=" + r.TotalMassG.ToString("0.00");
    }

    /// <summary>把当前判据表原样抓下来 —— 复核数据用的就是工程师看到的那张表。</summary>
    static void DumpChecks(LineDesignPage line, string title)
    {
        var r = F(line, "LastResult") as LineResult;
        if (r is null) { Console.WriteLine($"  （{title}：还没有解）"); return; }
        Console.WriteLine($"  {title}　收敛 {(r.Converged ? "✓" : "✗")}　"
                        + $"全判据 {(r.AllOk ? "✓" : "✗")}　总铂 {r.TotalMassG:0} g");
        foreach (var c in r.Checks.Where(c => c.Kind != CheckKind.Reference))
            Console.WriteLine($"     {(c.Undetermined ? "?" : c.Ok ? "✓" : "✗")} {c.Name,-26}"
                            + $"{(double.IsNaN(c.Actual) ? "判不了" : c.Actual.ToString("0.00")),10}"
                            + $" / {c.Limit,-8:0.00} {c.Where}");
        if (r.MissingChecks.Length > 0)
            OK("判据表完整", false, "★ 缺席：" + string.Join("、", r.MissingChecks));
    }

    // ════════════════════════════════════════════════════════════════════
    //  `UiWiring.exe --searchshape [quick]`
    //
    //  驱动**真的**「◇ 搜形状」，把它逐轮的记录原样抓出来。
    //
    //  两种用法，验的**不是同一件事**（用户 2026-08-25 要求两个都做）：
    //   · 不带 quick：默认轮数，几十分钟 —— 验「**答案好不好**」（真实行为）。
    //   · 带 quick  ：把每候选的筛轮压到 2 轮 —— 分钟级，验「**接线对不对**」：
    //                 一轮是不是「改形状 + 扫梯度」、变好会不会继续、
    //                 都变坏会不会停、已算过的形状会不会重算。
    //     ⚠ quick 模式下**答案没有意义**（2 轮定不出厚度），只看流程 ——
    //       这一点必须说清楚，否则下一个人会拿 quick 的铂重去汇报。
    // ════════════════════════════════════════════════════════════════════
    public static int SearchShape(bool quick)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1200);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(300);

        if (quick)
        {
            // internal 栏位跨组件看不见 ⇒ 走本档已有的反射工具（与其它节一致）
            // ⚠ 压轮数还不够：**每一轮都是一次完整整线解**（分钟级）。
            //   要回到分钟级，网格点数与外推上限也得压。
            Set(line, "SearchScreenRounds", 2);
            Set(line, "SearchFinalRounds", 2);
            Set(line, "SearchDiscs", new double[] { 30 });
            Set(line, "SearchWFrac", new double[] { 1.00 });
            Set(line, "SearchMaxExtend", 1);
            // ★ 从**设计记录**出发，而不是开箱默认。
            //   头一版从开箱默认起跑：2 轮定不出可行解 ⇒ 网格里没有全过的形状 ⇒
            //   **外推循环根本没进去**，而那正是本次最想验的那段接线。
            //   设计记录本身可行，且它的形状（盘Ø60／舌宽60）正好落在网格点上 ⇒
            //   低轮数也进得了外推。**这是为了走到那条路径，不是为了让它好看。**
            typeof(LineDesignPage).GetMethod("LoadDesignSpecFrom",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(line, new object[] { DesignSpec.Current, true });
            Pump(200);
            H("◇ 搜形状 · **接线验证**（每候选只筛 2 轮 ⇒ 分钟级）");
            Console.WriteLine("  ⚠ 本模式下**铂重没有意义**（2 轮定不出厚度）——只看流程走得对不对。");
        }
        else
            H("◇ 搜形状 · **真实一跑**（默认轮数，几十分钟）");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Call(line, "SearchShapeAsync");
        bool fin = Wait(() => F(line, "_cts") is null, quick ? 1_800_000 : 10_800_000);
        OK("搜形状在预算内跑完", fin, $"用时 {clock.Elapsed.TotalMinutes:0.0} 分");
        if (!fin) return _bad;

        string text = (F(line, "_out") as Control)?.Text ?? "";
        Console.WriteLine();
        Console.WriteLine("──── 输出框原文（工程师看到的就是这些）────");
        foreach (var l in text.Replace(((char)13).ToString(), "").Split((char)10))
            if (l.Trim().Length > 0) Console.WriteLine("  " + l.TrimEnd());

        // ── 接线断言：这几条只看**流程**，与轮数无关
        Console.WriteLine();
        OK("第 1 轮是网格（给出发点与方向）", text.Contains("网格", StringComparison.Ordinal));
        bool hasRound = text.Contains("轮 · 从 盘Ø", StringComparison.Ordinal);
        bool gridOnly = text.Contains("网格里没有可行解", StringComparison.Ordinal);
        OK("网格之后进了外推轮（或明说没有出发点）", hasRound || gridOnly,
           hasRound ? "有外推轮" : gridOnly ? "网格无可行解 —— 明说了，没有硬推" : "★ 两者都没有");
        if (hasRound)
        {
            OK("每一轮都报了**变好还是变坏**",
               text.Contains("变好，继续", StringComparison.Ordinal)
               || text.Contains("没有更好的方向", StringComparison.Ordinal));
            OK("停下时说得出为什么",
               text.Contains("没有更好的方向 ⇒ 停", StringComparison.Ordinal)
               || text.Contains("四个邻点都试过了", StringComparison.Ordinal)
               || text.Contains("变好，继续", StringComparison.Ordinal),
               "（跑满上限而停也算 —— 那时最后一轮是「变好，继续」）");
        }
        Console.WriteLine();
        Console.WriteLine(_bad == 0 ? "★ 搜形状走查通过" : $"✗ 搜形状走查：{_bad} 项不过");
        return _bad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  `UiWiring.exe --repro <盘Ø> <舌长> <半宽> <管壁> [quick]`
    //
    //  **从一个给定的起点几何出发，看 APP 自己能不能走到设计记录。**
    //  用户 2026-08-25：「先用 Pt_Heater1.3dm 为例子复现出
    //  设计记录_管壁0.6mm.3dm 与 设计记录_管壁0.8mm.3dm 的结果」。
    //
    //  这是本项目少有的**有已知答案**的验证：
    //    起点 Pt_Heater1.3dm（--geom 量得）：盘Ø120／舌长200／半宽60／板厚2.0 均匀／管壁1.0
    //    终点 设计记录 0.8：盘Ø60／舌140×60／管壁0.8 ⇒ **3547 g**
    //         设计记录 0.6：同形状／管壁0.6           ⇒ **2656 g**
    //  当年那条路是**人工**走的（回 Rhino 改环径 + 做阶梯厚度分布）；
    //  这里问的是：把起点几何交给 APP，它自己搜得回来吗。
    //
    //  ⚠ 走**解析路**而不是 .3dm 路：Pt_Heater1 分析出来是等厚板（1 级），
    //    「逐级定厚」没有可调的级（见 Flow.Next 里那条指路）。
    //    而设计记录本身就是解析设计（阶梯/环倍率都是程序生成的），.3dm 只是它的产物。
    // ════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ★★★ **对帐：命令行验过的交付结果，工程师从界面也要拿得到**（2026-08-30 用户拍板）。
    ///
    /// 用户原话：「能跑的功能所得的结果（对话时验证的交付结果），工程师使用 APP（UI 操作）
    /// 跑时，也要能得到对话时验证的交付结果。（**必须验证才能算提交**）」
    ///
    /// ══ 为什么这条会不成立
    ///
    /// 两条路**传给求解器的东西不是同一个对象**：
    /// <code>
    ///   命令行 --solve   Solver.Solve(DesignSpec.Select(args), …)   ← 直接拿设计记录
    ///   界面 自动定厚    Solver.Solve(PageToDesignSpec(), …)        ← 拿页面控件搬运出来的
    /// </code>
    /// 只有当「载入设计记录」把每一个进计算的量都灌准了，两者才等价。
    /// 而本页的历史正是**一路在补这些漏**（舌保温、环倍率、压接段、舌根圆角、环宽、
    /// 以及 2026-08-30 才补的 r₁/r₂/t₂）—— 每补一个之前，两条路都在算**不同的零件**。
    ///
    /// ⇒ 这不是「应该一样」，是**必须每次跑出来比一遍**。
    /// </summary>
    public static int Reconcile(double wall, double expectG)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1200);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(300);

        H($"对帐：管壁 {wall:0.0} 档 —— 界面跑出来的，要等于命令行验过的 {expectG:0.0} g");
        Console.WriteLine("  命令行那一边：--cli --quiet --solve --wall " + wall.ToString("0.0"));
        Console.WriteLine("  界面这一边　：载入设计记录 → 自动定厚（两者都走 Solver.Solve，求根、与初值无关）");

        // ── 一、载入设计记录（工程师的第一个动作）
        var box = (ToolStripComboBox)F(line, "_caseBox")!;
        int pick = -1;
        for (int i = 0; i < box.Items.Count; i++)
            if ((box.Items[i]?.ToString() ?? "").Contains(wall.ToString("0.0"), StringComparison.Ordinal))
            { pick = i; break; }
        OK("下拉里找得到这一档", pick >= 0,
           pick >= 0 ? box.Items[pick]!.ToString()!
                     : "★ 下拉里没有管壁 " + wall.ToString("0.0") + " 的档 —— 工程师无从开始");
        if (pick < 0) return _bad;
        box.SelectedIndex = pick; Pump(200);
        Call(line, "LoadDesignSpec"); Pump(400);

        var wallBox = (NumericUpDown)F(line, "_wall")!;
        OK("载入之后壁厚控件对得上", Math.Abs((double)wallBox.Value - wall) < 1e-9,
           $"控件 {wallBox.Value} vs 档 {wall:0.0}");

        // ── 二、点「自动定厚」（这一步就是命令行的 --solve）
        Console.WriteLine("  ⇒ 点「自动定厚」…（分钟级）");
        Call(line, "RunAsync", true, false);
        bool fin = Wait(() => F(line, "_cts") is null, 3_600_000);
        OK("自动定厚在预算内跑完", fin, fin ? "" : "★ 超时");
        if (!fin) return _bad;

        // ── 三、比数
        var last = (LineResult?)F(line, "_last");
        OK("界面这边解出来了", last is { Ok: true }, last?.Message ?? "(null)");
        if (last is not { Ok: true }) return _bad;

        double got = last.Segments.Sum(x => x.MassG) + last.Flanges.Sum(x => x.MassG);
        Console.WriteLine();
        Console.WriteLine($"  命令行验过 {expectG:0.0} g　　界面跑出 {got:0.0} g　　差 {got - expectG:+0.0;-0.0} g");
        DumpChecks(line, "界面这一边的判据表");

        // ⚠ 容差取 1 g：图纸格量化之下，同一个解不该差到 1 g。
        //   差得更多**不是「差不多」**，是两条路在算不同的东西 —— 那正是本条要抓的。
        OK("★★ 界面与命令行给出同一个交付结果", Math.Abs(got - expectG) <= 1.0,
           Math.Abs(got - expectG) <= 1.0 ? ""
             : $"★ 差 {got - expectG:+0.0;-0.0} g —— 两条路传给求解器的不是同一个零件");
        return _bad;
    }

    public static int Repro(double discD, double tabLen, double halfW, double wall, bool quick)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();

        var main = new MainForm();
        main.CreateControl();
        var tabs = (TabControl)F(main, "_tabs")!;
        var line = tabs.TabPages.OfType<LineDesignPage>().First();
        typeof(Form).GetMethod("OnLoad", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(main, new object?[] { EventArgs.Empty });
        Pump(1200);
        void Force(Control c) { _ = c.Handle; foreach (Control k in c.Controls) Force(k); }
        Force(main); Pump(300);

        H($"复现：起点 盘Ø{discD:0}／舌长{tabLen:0}／半宽{halfW:0}／管壁{wall:0.0}");
        Console.WriteLine("  终点（已知答案）：设计记录 0.8 = 3547 g　设计记录 0.6 = 2656 g");
        Console.WriteLine("  ⚠ 起点几何取自 Pt_Heater1.3dm 的 --geom 实测，不是页面默认。");

        Set(line, "_suppressAuto", true);
        ((NumericUpDown)F(line, "_discD")!).Value = (decimal)discD;
        ((NumericUpDown)F(line, "_tabLen")!).Value = (decimal)tabLen;
        ((NumericUpDown)F(line, "_tabW")!).Value = (decimal)halfW;
        ((NumericUpDown)F(line, "_wall")!).Value = (decimal)wall;
        Set(line, "_suppressAuto", false);
        Pump(200);
        Console.WriteLine($"  已设：盘Ø{((NumericUpDown)F(line, "_discD")!).Value}"
                        + $"／舌长{((NumericUpDown)F(line, "_tabLen")!).Value}"
                        + $"／半宽{((NumericUpDown)F(line, "_tabW")!).Value}"
                        + $"／管壁{((NumericUpDown)F(line, "_wall")!).Value}");

        if (quick)
        {
            Set(line, "SearchScreenRounds", 4);
            Set(line, "SearchFinalRounds", 8);
            Set(line, "SearchMaxExtend", 2);
            Console.WriteLine("  ⚠ quick：轮数压小，**答案不作数**，只看流程。");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Call(line, "SearchShapeAsync");
        bool fin = Wait(() => F(line, "_cts") is null, 14_400_000);
        OK("搜形状跑完", fin, $"用时 {clock.Elapsed.TotalMinutes:0.0} 分");

        string text = (F(line, "_out") as Control)?.Text ?? "";
        Console.WriteLine();
        Console.WriteLine("──── 输出框原文 ────");
        foreach (var l in text.Replace(((char)13).ToString(), "").Split((char)10))
            if (l.Trim().Length > 0) Console.WriteLine("  " + l.TrimEnd());

        var r = F(line, "LastResult") as LineResult;
        Console.WriteLine();
        if (r is not null)
        {
            double target = wall >= 0.7 ? 3547 : 2656;
            Console.WriteLine($"  ★ 复现结果 {r.TotalMassG:0} g　vs 设计记录记录 {target:0} g"
                            + $"　差 {r.TotalMassG - target:+0;−0} g"
                            + $"（{100 * (r.TotalMassG - target) / target:+0.0;−0.0} %）");
            Console.WriteLine($"     全判据 {(r.AllOk ? "✓" : "✗")}"
                            + (r.AllOk ? "" : "　未过：" + string.Join("；", r.Failed)));
        }
        else OK("有结果可比", false, "★ 没有解");
        return _bad;
    }
}
