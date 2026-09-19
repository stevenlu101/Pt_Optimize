using System.Text;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// **分析页** —— 把本项目定论过程里的两步诊断搬进 APP，工程师不必碰命令行：
///
///   ① 升温可达性（原 CLI <c>--gate1</c>）：管壁 × 保温，逐格给稳态工作点电流与热稳定裕度。
///      这是 HANDOVER §0.0 的**第一条硬约束**，必须先过它才谈稳态。
///   ② 厚度灵敏度（原 CLI <c>--tscan</c>）：管根温差随法兰厚度的实际曲线。
///      它回答两件事：单调性成不成立、以及**要几次二分才落得进 10 K 窗口**。
///
/// ★ 为什么 ② 必须有：ΔT 对厚度的斜率约 1300 K/mm，10 K 的窗口只有 0.008 mm 宽。
///   本页直接把「所需迭代次数 = log₂(区间/所需分辨率)」算给你看 ——
///   §7 记过两次「迭代不够就误报无解」，把它摆在明面上就不会再犯。
/// </summary>
public sealed class AnalysisPage : TabPage
{
    private readonly RichTextBox _out = new();
    private readonly ScottPlot.WinForms.FormsPlot _plot = FieldPlots.NewPlot();
    private readonly ToolStripLabel _status = new("");
    private readonly ToolStripProgressBar _prog = new() { Visible = false };
    private readonly ToolStripButton _btnGate, _btnScan;
    private readonly DesignInputs _base;

    /// <summary>阶段轨共享状态。由 MainForm 注入 —— 本页两个扫描也要报「正在算什么」。</summary>
    internal FlowState? Shared { get; set; }
    private CancellationTokenSource? _cts;

    public AnalysisPage(DesignInputs baseInputs)
    {
        _base = baseInputs;
        Text = "分析";
        Padding = new Padding(2);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = UiScale.Ui() };
        _btnGate = Btn("升温可达性趋势", (_, _) => _ = RunAsync(true));
        _btnScan = Btn("厚度灵敏度扫描", (_, _) => _ = RunAsync(false));
        // ★ 2026-08-20 阶段轨：本页是「① 闸门」，只留升温可达性这一条闭式快筛。
        //   「② 厚度灵敏度」搬到「④ 定尺寸」—— 它逐点跑 LineRunner.Run，
        //   每一点都是**权威解**，属于 C 链的工具，从来就不该和闭式快筛并排。
        //   按钮仍由本页创建持有（禁用/取消逻辑在 RunAsync 里），只是挂到 ④ 的工具条上。
        tool.Items.Add(_btnGate);
        tool.Items.Add(new ToolStripSeparator());
        _prog.Style = ProgressBarStyle.Marquee;
        // ⚠ 写死像素要过 UiScale.S —— 本页原来是全项目唯一漏网的一处
        //   （MainForm、LineDesignPage 的进度条都过了）。
        _prog.Size = new Size(UiScale.S(140), UiScale.S(16));
        tool.Items.Add(_prog);
        tool.Items.Add(_status);

        _out.Dock = DockStyle.Fill;
        _out.Font = UiScale.Mono();
        _out.ReadOnly = true; _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);
        // 本页两张表都靠制表位排；不挂钩就退回「按字符数补空格」，中文表头一多必歪，`**` 也不加粗
        TextFmt.Hook(_out);

        var split = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        split.Panel1.Controls.Add(_out);
        split.Panel2.Controls.Add(_plot);

        FieldPlots.DrawEmpty(_plot, "点「升温可达性趋势」开始");
        Controls.Add(split);
        Controls.Add(tool);
        HandleCreated += (_, _) => BeginInvoke(() => split.SplitterDistance = (int)(split.Height * 0.55));
    }

    /// <summary>归「④ 定尺寸」托管的按钮 —— 所有权仍在本页，只是挂到那边的工具条上。</summary>
    internal ToolStripButton BtnThicknessScan => _btnScan;

    // ★ 2026-09-02 阶段轨重排：本页不再是一个页签（原「① 先决条件」），
    //   它的两个按钮与输出/图分别挂到「① 整线核算」与「参考工具」上。
    //   控件仍归本页所有（跑起来改文字、互相禁用那套状态只有一份）。
    internal ToolStripButton BtnRampGate => _btnGate;
    internal Control OutBox => _out;
    internal Control PlotBox => _plot;

    private static ToolStripButton Btn(string t, EventHandler h)
    {
        var b = new ToolStripButton(t) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += h;
        return b;
    }

    private async Task RunAsync(bool gate)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // ⚠ **正在跑的那个按钮必须保持可点**（2026-08-21 修）。
        //   原来两个一起禁 ⇒ 方法开头那句「再点一次 = 取消」永远到不了，
        //   而「② 厚度灵敏度」要跑 10 次整线耦合解、五分钟起 —— **实际不可取消**。
        //   ③ 页早就是「禁另一个、正在跑的改名取消」，这里对齐它。
        (gate ? _btnScan : _btnGate).Enabled = false;
        (gate ? _btnGate : _btnScan).Text = "取消";
        _prog.Visible = true;
        _status.Text = gate ? "扫描升温可达性…" : "扫描厚度灵敏度（每点一次整线耦合解）…";
        // ★ 报给阶段轨：右上角状态面板切到哪一页都看得见，
        //   而「② 厚度灵敏度」的按钮在 ④ 页、进度条却在本页 ⇒ 不接上就完全没有提示。
        Shared?.SetRunning(gate ? ChainId.D升温闸 : ChainId.C整线耦合,
                           gate ? "升温可达性" : "厚度灵敏度（10 点，每点一次整线解）");
        try
        {
            string txt = gate
                ? await Task.Run(() => Gate1(ct), ct)
                : await Task.Run(() => TScan(ct), ct);
            _out.Text = txt;
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            MessageBox.Show(this, ex.Message, "分析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _prog.Visible = false;
            _btnGate.Enabled = _btnScan.Enabled = true;
            // 按钮名要还原 —— 否则取消之后它永远顶着「取消」二字
            // ⚠ 这两句是 finally 里的**还原** —— 漏改就会「取消一次之后按钮永远顶着取消」
            _btnGate.Text = "升温可达性趋势"; _btnScan.Text = "厚度灵敏度扫描";
            Shared?.SetRunning(null);          // 清在 finally：异常/取消也必须解除互斥
        }
    }

    /// <summary>① 升温可达性：温控功率下升温是准静态的，「能不能到」＝「该温度的稳态工作点要多大电流」</summary>
    private string Gate1(CancellationToken ct)
    {
        double tTarget = RampScreen.TargetC;   // 目标温度也归 RampScreen，别在界面再写一个 1150（2026-09-15 Opus 5（J 路）：RampScreen.TargetC 改读 LineCase.RampTargetC，不再是 const）
        var sb = new StringBuilder();
        sb.AppendLine("=== 升温可达性（管侧）===");
        sb.AppendLine($"目标 {tTarget:0} °C　空管口径（升温时管内无玻璃）");
        sb.AppendLine("温控功率下升温是准静态的 ⇒「能不能到」= 该温度的稳态工作点要多大电流");
        sb.AppendLine();
        sb.AppendLine("纤维 mm	壁厚 mm	段散热 W	电流 A	管 J A/mm²	" +
                      "I_stab A	稳定裕度	管铂 g/段	判定");

        // ★ 物理与判定阈值都在 Core/RampScreen —— 本页只负责把它排成表。
        //   2026-08-20 之前，那段闭式和 `margin > 1.5 ? "✓" : ...` 就写在这里，
        //   是全项目第四处判定逻辑；而阶段门禁也要用同一个结论，再抄一遍就是第五处。
        var xs = new List<double>(); var ys = new List<double>();
        foreach (double ins in new[] { 2.5, 5.0, 10.0, 20.0, 40.0 })
        {
            foreach (double w in new[] { 0.4, 0.6, 0.8, 1.0 })
            {
                ct.ThrowIfCancellationRequested();
                var pt = RampScreen.Evaluate(_base, ins, w, tTarget);
                sb.AppendLine($"{pt.InsulMm:0.0}	{pt.WallMm:0.0}	{pt.LossW:0}	{pt.CurrentA:0}	" +
                              $"{pt.TubeJAPerMm2:0.00}	{pt.IStabA:0}	{pt.Margin:0.00}	" +
                              $"{pt.MassG:0}	{pt.Verdict}");
                if (Math.Abs(w - 0.4) < 1e-9) { xs.Add(ins); ys.Add(pt.TubeJAPerMm2); }
            }
            // 分档之间要横线不要空行：空行会把这张表断成五张，五段各自算列宽 ⇒ 彼此对不齐
            sb.AppendLine(TextFmt.SepRow(9));
        }
        // 最后那条 SepRow 就当表的底框；与「读法：」之间空一行 ——
        // 原来分档之间的空行改成横线之后，表和散文就贴在一起了
        sb.AppendLine();
        sb.AppendLine("读法：");
        sb.AppendLine("· 管 J ∝ 1/√壁厚 —— 减薄管壁不减少电流负担，反而抬高 J");
        sb.AppendLine("· 加厚保温同时降电流与 J，且保温无空间限制 ⇒ 管侧的免费杠杆");
        sb.AppendLine("· I_stab = √(βA/(dρe/dT)) 是热稳定极限，越过它稳态解本就不存在");

        BeginInvoke(() =>
        {
            var pl = _plot.Plot; pl.Clear();
            var s = pl.Add.ScatterLine(xs.ToArray(), ys.ToArray());
            s.LineWidth = 2; s.MarkerSize = 7;
            pl.Title("管电流密度 J vs 纤维保温厚度（壁厚 0.4 mm）");
            pl.XLabel("纤维保温厚度 [mm]"); pl.YLabel("管电流密度 J [A/mm²]");
            _plot.Refresh();
        });
        return sb.ToString();
    }

    /// <summary>
    /// ② 厚度灵敏度：逐点跑整线耦合解，画出 ΔT(法兰厚度)，并据斜率算出所需迭代次数。
    /// **先画出函数再求根** —— §7 那两次误诊（先怪次数、再查出靶函数不连续）就是没做这一步。
    /// </summary>
    private string TScan(CancellationToken ct)
    {
        var ts = new[] { 0.40, 0.55, 0.70, 0.85, 1.00, 1.30, 1.60, 2.00, 2.60, 3.20 };
        var sb = new StringBuilder();
        sb.AppendLine("=== 厚度灵敏度（每点一次整线耦合解）===");
        sb.AppendLine("形状 Ø60/舌50/半宽20；共用片厚 = 端片厚 × √3；管壁 0.4、纤维 10、法兰全包 20、夹持 300 °C");
        sb.AppendLine();
        sb.AppendLine("端片 t mm\tHC1 ΔT\tHC2 ΔT\tHC3 ΔT\t法兰最高 °C\t总铂 g\t收敛");

        var p = SegmentSolver.Clone(_base);
        p.Layer1.ThicknessMm = 10; p.Layer1.Enabled = true;
        p.WallMinMm = 0.4;
        p.FlangeInsulThickMm = 20; p.FlangeInsulated = true;
        p.BusbarClampTempC = 300;

        var xs = new List<double>(); var y1 = new List<double>();
        double prevT = double.NaN, prevD = double.NaN, slope = double.NaN;
        foreach (double tE in ts)
        {
            ct.ThrowIfCancellationRequested();
            FlangePlate Mk(double t) => new()
            {
                DiscRadiusMm = 30, HoleRadiusMm = 25.4,
                TabEndXMm = -50, TabEndHalfWidthMm = 20,
                ThicknessMm = t, ThickenedMm = t, InsulBoundaryXMm = -1e9
            };
            double k = Math.Sqrt(3.0);
            var lc = new LineCase
            {
                Base = p, WallMm = 0.4, UseMeasuredCurrent = false, CheckRamp = false,
                SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
                FlangePlates = new[] { Mk(tE), Mk(tE * k), Mk(tE * k), Mk(tE) }
            };
            LineResult r;
            try { r = LineRunner.Run(lc, null, ct); } catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (!r.Ok) continue;
            double dMin = r.Segments.Min(s => s.RootDeltaK);
            sb.AppendLine($"{tE:0.00}\t{r.Segments[0].RootDeltaK:+0.0;-0.0}\t" +
                          $"{r.Segments[1].RootDeltaK:+0.0;-0.0}\t{r.Segments[2].RootDeltaK:+0.0;-0.0}\t" +
                          $"{r.Flanges.Max(f => f.TMaxC):0}\t{r.TotalMassG:0}\t" + (r.Converged ? "✓" : "✗"));
            xs.Add(tE); y1.Add(dMin);
            if (!double.IsNaN(prevT) && Math.Abs(dMin) < 200 && Math.Abs(prevD) < 200)
                slope = (dMin - prevD) / (tE - prevT);
            prevT = tE; prevD = dMin;
        }

        sb.AppendLine();
        if (!double.IsNaN(slope) && Math.Abs(slope) > 1e-9)
        {
            double need = 10.0 / Math.Abs(slope);                 // 10 K 窗口对应的厚度宽度
            int iters = (int)Math.Ceiling(Math.Log2(3.6 / need)); // 区间 [0.4,4.0]
            sb.AppendLine($"★ 近零点斜率 ≈ {Math.Abs(slope):0} K/mm ⇒ 10 K 窗口只有 {need:0.0000} mm 宽");
            sb.AppendLine($"  在 [0.4, 4.0] 上二分到该分辨率需 **{Math.Max(iters, 1)} 次**；" +
                          "「自动定厚」已按此自动升级轮次，不需要你设。");
            sb.AppendLine($"  制造含义：守住 10 K 需法兰厚公差 ±{need / 2:0.000} mm —— " +
                          "靠加工保证不现实，须用铜排空冷风量在现场整定。");
        }
        sb.AppendLine();
        sb.AppendLine("若 ΔT 不随厚度单调，则任何二分都无效。本表就是用来看这一点的。");

        BeginInvoke(() =>
        {
            var pl = _plot.Plot; pl.Clear();
            var s = pl.Add.ScatterLine(xs.ToArray(), y1.ToArray());
            s.LineWidth = 2; s.MarkerSize = 7;
            pl.Add.HorizontalLine(0).LineWidth = 1;
            pl.Add.HorizontalLine(10).LineWidth = 1;
            pl.Title("最小段管根温差 vs 法兰厚度（两条水平线之间即 C2 合格窗口）");
            pl.XLabel("端片厚度 [mm]"); pl.YLabel("min ΔT_管根 [K]");
            _plot.Refresh();
        });
        return sb.ToString();
    }
}
