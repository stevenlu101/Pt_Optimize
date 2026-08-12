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
    private CancellationTokenSource? _cts;

    public AnalysisPage(DesignInputs baseInputs)
    {
        _base = baseInputs;
        Text = "分析";
        Padding = new Padding(2);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _btnGate = Btn("① 升温可达性", (_, _) => _ = RunAsync(true));
        _btnScan = Btn("② 厚度灵敏度", (_, _) => _ = RunAsync(false));
        tool.Items.Add(_btnGate);
        tool.Items.Add(_btnScan);
        tool.Items.Add(new ToolStripSeparator());
        _prog.Style = ProgressBarStyle.Marquee;
        _prog.Size = new Size(140, 16);
        tool.Items.Add(_prog);
        tool.Items.Add(_status);

        _out.Dock = DockStyle.Fill;
        _out.Font = new Font("Consolas", 9.5f);
        _out.ReadOnly = true; _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);

        var split = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        split.Panel1.Controls.Add(_out);
        split.Panel2.Controls.Add(_plot);

        Controls.Add(split);
        Controls.Add(tool);
        HandleCreated += (_, _) => BeginInvoke(() => split.SplitterDistance = (int)(split.Height * 0.55));
    }

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
        _btnGate.Enabled = _btnScan.Enabled = false;
        _prog.Visible = true;
        _status.Text = gate ? "扫描升温可达性…" : "扫描厚度灵敏度（每点一次整线耦合解）…";
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
        }
    }

    /// <summary>① 升温可达性：温控功率下升温是准静态的，「能不能到」＝「该温度的稳态工作点要多大电流」</summary>
    private string Gate1(CancellationToken ct)
    {
        const double tTarget = 1150;
        var sb = new StringBuilder();
        sb.AppendLine("=== ① 升温可达性（管侧）===");
        sb.AppendLine($"目标 {tTarget:0} °C　空管口径（升温时管内无玻璃）");
        sb.AppendLine("温控功率下升温是准静态的 ⇒「能不能到」= 该温度的稳态工作点要多大电流");
        sb.AppendLine();
        sb.AppendLine($"{"纤维 mm",9}{"壁厚 mm",9}{"段散热 W",10}{"电流 A",9}{"管 J",8}" +
                      $"{"I_stab A",10}{"稳定裕度",10}{"管铂 g/段",11}  判定");

        var xs = new List<double>(); var ys = new List<double>();
        foreach (double ins in new[] { 2.5, 5.0, 10.0, 20.0, 40.0 })
        {
            foreach (double w in new[] { 0.4, 0.6, 0.8, 1.0 })
            {
                ct.ThrowIfCancellationRequested();
                var q = SegmentSolver.Clone(_base);
                q.Layer1.ThicknessMm = ins; q.Layer1.Enabled = true;
                q.WallMinMm = w; q.TSetC = tTarget;

                double ri = q.TubeIdMm * 0.5e-3, ww = w * 1e-3, rOut = ri + ww;
                double aM2 = Math.PI * (rOut * rOut - ri * ri), aMm2 = aM2 * 1e6;
                bool anyIns = q.Layers.Any(l => l.Enabled && l.ThicknessMm > 1e-6);
                double eps = anyIns ? q.OuterEmissivity : q.PtEmissivity;
                var tab = new LossTable(q.TAmbC, tTarget + 300, 60,
                    t => Insulation.CylinderLoss(t, q.TAmbC, rOut, q.Layers, eps,
                             q.Posture == PtOptimize.Core.Orientation.Vertical,
                             q.TubeLength, q.LossScale).QPerLength);
                double lossW = tab.Eval(tTarget) * q.TubeLength;
                double rOhm = Materials.PtResistivity(tTarget) * q.TubeLength / aM2;
                double iA = Math.Sqrt(lossW / rOhm), jA = iA / aMm2;
                double beta = tab.Slope(tTarget);
                double drho = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * tTarget);
                double iStab = Math.Sqrt(Math.Max(1e-9, beta * aM2 / drho));
                double margin = iStab / Math.Max(1e-9, iA);
                double massG = aMm2 * q.TubeLengthMm * Materials.PtDensity * 1e-6;
                string v = margin > 1.5 ? "✓" : margin > 1.0 ? "⚠ 裕度薄" : "✗ 越热稳定极限";
                sb.AppendLine($"{ins,9:0.0}{w,9:0.0}{lossW,10:0}{iA,9:0}{jA,8:0.00}" +
                              $"{iStab,10:0}{margin,10:0.00}{massG,11:0}  {v}");
                if (Math.Abs(w - 0.4) < 1e-9) { xs.Add(ins); ys.Add(jA); }
            }
            sb.AppendLine();
        }
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
        sb.AppendLine("=== ② 厚度灵敏度（每点一次整线耦合解）===");
        sb.AppendLine("形状 Ø60/舌50/半宽20；共用片厚 = 端片厚 × √3；管壁 0.4、纤维 10、法兰全包 20、夹持 300 °C");
        sb.AppendLine();
        sb.AppendLine($"{"端片t mm",10}{"HC1 ΔT",10}{"HC2 ΔT",10}{"HC3 ΔT",10}{"法兰最高°C",12}{"总铂 g",9}  收敛");

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
            sb.AppendLine($"{tE,10:0.00}{r.Segments[0].RootDeltaK,10:+0.0;-0.0}" +
                          $"{r.Segments[1].RootDeltaK,10:+0.0;-0.0}{r.Segments[2].RootDeltaK,10:+0.0;-0.0}" +
                          $"{r.Flanges.Max(f => f.TMaxC),12:0}{r.TotalMassG,9:0}  " + (r.Converged ? "✓" : "✗"));
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
