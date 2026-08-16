using System.ComponentModel;
using System.IO;
using System.Text;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// **整线设计页** —— 工程师的主工作面，一页走完全流程：
/// 改参数 → 核算 → 自动定厚 → 导出 .3dm。**不需要碰命令行**。
///
/// 内核一律走 <see cref="LineRunner"/> / <see cref="FlangeAutoSizer"/> /
/// <see cref="Geometry3dm.WritePlate3dm"/>，与 CLI 同源，两边不可能跑出不同结果。
///
/// 界面上只放**当前判据体系用得到的**输入（HANDOVER §0.0 的四个自由度 + 边界条件）；
/// 已作废的一维法兰模型那套参数（盘内外半径、剖面形状、梯形厚度…）已随模型删除。
/// </summary>
public sealed class LineDesignPage : TabPage
{
    private readonly NumericUpDown _wall = Num(0.40m, 0.10m, 5.00m, 0.05m, 2);
    private readonly NumericUpDown _tubeIns = Num(10.0m, 0.0m, 100.0m, 0.5m, 1);
    private readonly NumericUpDown _clamp = Num(300m, -1m, 1200m, 10m, 0);
    private readonly ComboBox _flIns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly NumericUpDown _flInsT = Num(20.0m, 0.0m, 60.0m, 0.5m, 1);
    private readonly NumericUpDown _discD = Num(60m, 30m, 300m, 2m, 0);
    private readonly NumericUpDown _tabLen = Num(50m, 20m, 400m, 5m, 0);
    private readonly NumericUpDown _tabW = Num(20m, 5m, 150m, 1m, 0);
    private readonly NumericUpDown[] _tPlate =
    {
        Num(0.516m, 0.10m, 8.0m, 0.02m, 3), Num(0.855m, 0.10m, 8.0m, 0.02m, 3),
        Num(0.776m, 0.10m, 8.0m, 0.02m, 3), Num(0.426m, 0.10m, 8.0m, 0.02m, 3),
    };
    private readonly RadioButton _srcAnalytic = new()
    { Text = "解析形状（圆盘 + 梯形舌片，程序生成）", AutoSize = true };
    private readonly RadioButton _src3dm = new()
    { Text = "Rhino .3dm 文件（任意形状：阶梯厚度、开槽、异形轮廓）", AutoSize = true };
    private readonly TextBox[] _file3dm = { new(), new(), new(), new() };
    private readonly Control[] _row3dm = new Control[4];
    private readonly TextBox _layer3dm = new() { Text = "法兰", Width = 96 };
    private readonly DataGridView _segGrid = new();
    private readonly RichTextBox _out = new();
    private readonly ToolStripProgressBar _prog = new() { Visible = false, Maximum = 1000 };
    private readonly ToolStripLabel _status = new("");
    private readonly ToolStripButton _btnRun, _btnAuto, _btnExport, _btnLoadCase, _btn3dm, _btnRepro;
    private readonly ToolStripComboBox _caseBox =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TabControl _plots = new() { Dock = DockStyle.Fill };
    private readonly ScottPlot.WinForms.FormsPlot _pT = FieldPlots.NewPlot();
    private readonly ScottPlot.WinForms.FormsPlot _pJ = FieldPlots.NewPlot();
    private readonly ScottPlot.WinForms.FormsPlot _pAx = FieldPlots.NewPlot();

    private readonly BindingList<SegRow> _segs = new()
    {
        new SegRow { 名称 = "HC1", 控温C = 1150, 水头m = 0.3 },
        new SegRow { 名称 = "HC2", 控温C = 1080, 水头m = 0.6 },
        new SegRow { 名称 = "HC3", 控温C = 1050, 水头m = 1.0 },
    };
    private CancellationTokenSource? _cts;
    private LineResult? _last;
    /// <summary>「分析几何变数」解析出的各级原始厚度，逐级定厚要用</summary>
    private double[][]? _levels;
    private double[][]? _levelScale;
    /// <summary>各级的「锁定」勾选框（解析几何变数后动态生成）</summary>
    private readonly List<CheckBox> _lockBoxes = new();
    private readonly FlowLayoutPanel _lockPanel = new()
    { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
    private readonly DesignInputs _base;

    /// <summary>段的可编辑行。★ 控温点默认 1150/1080/1050 —— 沿流向**递减**，
    /// 是用户给的真实工况；早先示例值 1150/1200/1250 递增，曾被当成实测（§7）。</summary>
    public sealed class SegRow
    {
        public string 名称 { get; set; } = "";
        public double 控温C { get; set; }
        public double 水头m { get; set; }
    }

    public LineDesignPage(DesignInputs baseInputs)
    {
        _base = baseInputs;
        Text = "整线设计";
        Padding = new Padding(2);

        _flIns.Items.AddRange(new object[] { "不包", "仅圆盘包", "全包" });
        _flIns.SelectedIndex = 2;
        _flIns.SelectedIndexChanged += (_, _) => _flInsT.Enabled = _flIns.SelectedIndex > 0;

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _btnRun = Btn("核算整线", (_, _) => _ = RunAsync(false));
        _btnAuto = Btn("自动定厚", (_, _) => _ = RunAsync(true));
        _btnExport = Btn("导出 .3dm", (_, _) => Export());

        // ★ 定案档：直接从 Core/FinalDesign 取，**不在 UI 里再抄一份数**。
        //   两档都全判据通过，差别只在裕度与铂重（见各档的 Binding 说明）。
        foreach (var fd in FinalDesign.All) _caseBox.Items.Add(fd.Name);
        _caseBox.SelectedIndex = System.Array.IndexOf(FinalDesign.All, FinalDesign.Current);
        if (_caseBox.SelectedIndex < 0) _caseBox.SelectedIndex = 0;
        _btnLoadCase = Btn("载入定案", (_, _) => LoadFinalDesign());
        _btn3dm = Btn("导出定案 3DM", (_, _) => ExportFinal3dm());
        var btnAnalyze = Btn("分析几何变数", (_, _) => AnalyzeShape());

        // ★★★ 复现定案：**界面上唯一能跑出定案数字的按钮**（2026-08-16 用户提出）。
        //
        // 在此之前界面根本没有这条路：本页控件表达不了「管孔两级渐变环」与
        // 「逐片舌保温」，所以「载入定案 → 核算整线」跑的是一个**缺两项的构型**，
        // 数字对不上，而它照样出一张漂亮的判据表 —— §1.8「安静失败」的形状。
        // ⇒ 本按钮**完全绕过页面控件**，直接用 FinalDesign.BuildCase 造算例。
        _btnRepro = Btn("▶ 复现定案", (_, _) => _ = ReproduceAsync());
        _btnRepro.Font = new Font(_btnRepro.Font, FontStyle.Bold);

        tool.Items.Add(new ToolStripLabel("定案档"));
        tool.Items.Add(_caseBox);
        tool.Items.Add(_btnRepro);
        tool.Items.Add(_btn3dm);
        tool.Items.Add(_btnLoadCase);
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(_btnRun);
        tool.Items.Add(_btnAuto);
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(btnAnalyze);
        tool.Items.Add(_btnExport);
        tool.Items.Add(new ToolStripSeparator());
        _prog.Size = new Size(160, 16);
        tool.Items.Add(_prog);
        tool.Items.Add(_status);

        // ── 输入面板
        var input = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(6) };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Head(string s)
        {
            var l = new Label
            {
                Text = s, AutoSize = true, Margin = new Padding(0, 10, 0, 4),
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 90, 140)
            };
            input.Controls.Add(l); input.SetColumnSpan(l, 2);
        }
        void Row(string label, Control c, string? tip = null)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) };
            if (tip is not null) new ToolTip().SetToolTip(l, tip);
            input.Controls.Add(l);
            input.Controls.Add(c);
        }

        Head("管");
        Row("壁厚 mm", _wall,
            "工艺下界 0.6 mm = **手工 TIG 烧穿下界**（自动 TIG 0.3、激光 0.1，差一个量级）。\n" +
            "另一条独立的界是管 J ≤ 12 A/mm²（现场给定：一般上限 15，壁 0.6 时 12 是极限）。\n" +
            "定案两档正是被这两条同点咬住（0.6）与全都留有余量（0.8）。");
        Row("纤维保温 mm", _tubeIns, "无空间限制。加厚同时降电流与 J，是管侧的免费杠杆");

        Head("法兰几何来源");
        _srcAnalytic.Checked = true;
        _srcAnalytic.CheckedChanged += (_, _) => SyncGeomSource();
        input.Controls.Add(_srcAnalytic); input.SetColumnSpan(_srcAnalytic, 2);
        input.Controls.Add(_src3dm); input.SetColumnSpan(_src3dm, 2);

        // .3dm 模式：每片一个文件（可重复同一文件），厚度由图纸决定，
        // 「自动定厚」求的是厚度**整体标度 k**，即「这张图要整体 ×k」。
        var names = new[] { "入口", "HC1|HC2", "HC2|HC3", "出口" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var pnl = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            _file3dm[idx].Width = 150; _file3dm[idx].ReadOnly = true;
            var b = new Button { Text = "…", Width = 30, Height = 22 };
            b.Click += (_, _) => PickFile(idx);
            pnl.Controls.Add(_file3dm[idx]); pnl.Controls.Add(b);
            _row3dm[idx] = pnl;
            Row(names[idx] + " .3dm", pnl);
        }
        Row("图层名", _layer3dm, "厚度场从该图层提取。t=0 表示无材料 ⇒ 开槽、孔、轮廓一次拿全");
        Row("锁定的级", _lockPanel,
            "勾上的级厚度锁死，优化器只调其余级。典型用法：外圈勾上 = 外圈不动、只调内圈。" +
            "先点「分析几何变数」才会列出各级。");

        Head("法兰形状（解析模式；四片同形状，厚度各自独立）");
        Row("圆盘直径 mm", _discD);
        Row("舌片长度 mm", _tabLen, "省铂宜短；但舌片越长形状数 Ψ 越小、局部越不易过热");
        Row("舌端半宽 mm", _tabW);

        Head("法兰厚度 mm / 厚度标度（可点「自动定厚」求解）");
        for (int i = 0; i < 4; i++) Row(names[i], _tPlate[i]);

        Head("保温与夹持");
        Row("法兰保温", _flIns, "包纤维会降低自给所需厚度；不包则法兰更凉但从管子抽热更多");
        Row("法兰保温厚 mm", _flInsT);
        Row("铜排夹持 °C", _clamp, "空冷即可，<0 = 无夹冷。★ 这是现场把自给率整定到位的唯一旋钮");

        Head("分段控温点");
        _segGrid.Dock = DockStyle.Top;
        _segGrid.Height = 110;
        _segGrid.AutoGenerateColumns = true;
        _segGrid.AllowUserToAddRows = true;
        _segGrid.DataSource = _segs;
        _segGrid.DataError += (_, e) => e.ThrowException = false;
        input.Controls.Add(_segGrid);
        input.SetColumnSpan(_segGrid, 2);

        // ── 输出
        _out.Dock = DockStyle.Fill;
        _out.Font = new Font("Consolas", 9.5f);
        _out.ReadOnly = true; _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);

        foreach (var (t, c) in new (string, Control)[]
        { ("法兰温度场", _pT), ("法兰电流密度场", _pJ), ("管轴向剖面", _pAx) })
        {
            var pg = new TabPage(t) { Padding = new Padding(2) };
            pg.Controls.Add(c);
            _plots.TabPages.Add(pg);
        }

        var rightSplit = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        rightSplit.Panel1.Controls.Add(_out);
        rightSplit.Panel2.Controls.Add(_plots);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(input);
        main.Panel2.Controls.Add(rightSplit);

        Controls.Add(main);
        Controls.Add(tool);
        SyncGeomSource();
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            main.SplitterDistance = 300;
            rightSplit.SplitterDistance = (int)(rightSplit.Height * 0.62);
        });
    }

    /// <summary>
    /// ⚠ **顺序不能改**：必须先设 Minimum/Maximum 再设 Value。
    /// 对象初始化器按书写顺序赋值，而 NumericUpDown 的默认上限是 100 ——
    /// 先写 Value = 300 会当场抛 ArgumentOutOfRangeException，程序启动即崩。
    /// </summary>
    /// <summary>
    /// 两种几何来源互斥：解析模式下厚度输入框是**绝对厚度 mm**；
    /// .3dm 模式下同一组框改作**厚度标度 k**（图纸厚度整体 ×k），故默认值切到 1。
    /// </summary>
    private void SyncGeomSource()
    {
        bool an = _srcAnalytic.Checked;
        _discD.Enabled = _tabLen.Enabled = _tabW.Enabled = an;
        foreach (var r in _row3dm) if (r is not null) r.Enabled = !an;
        _layer3dm.Enabled = !an;
        for (int i = 0; i < _tPlate.Length; i++)
        {
            _tPlate[i].DecimalPlaces = an ? 3 : 3;
            if (!an && _tPlate[i].Value > 3m) _tPlate[i].Value = 1.0m;   // 标度从 1 起
        }
    }

    private void PickFile(int idx)
    {
        using var dlg = new OpenFileDialog { Filter = "Rhino 3D 模型 (*.3dm)|*.3dm" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _file3dm[idx].Text = dlg.FileName;
        // 空着的后续片默认沿用同一文件 —— 四片常常同形状，省得点四次
        for (int k = idx + 1; k < _file3dm.Length; k++)
            if (string.IsNullOrWhiteSpace(_file3dm[k].Text)) _file3dm[k].Text = dlg.FileName;
    }

    private static NumericUpDown Num(decimal v, decimal lo, decimal hi, decimal inc, int dec)
    {
        var n = new NumericUpDown { Width = 96, DecimalPlaces = dec, Increment = inc };
        n.Minimum = lo; n.Maximum = hi;
        n.Value = Math.Clamp(v, lo, hi);
        return n;
    }

    /// <summary>
    /// 未收敛时的**一次性追问**：如果求解器判断是「慢」而不是「发散」，
    /// 就问一句要不要加轮数重跑。
    ///
    /// 为什么值得做（2026-08-16 实测）：默认轮数上限 200 只够贴着定案点用。
    /// 稍一改参数，环路增益 g≈0.96 把扰动放大约 25 倍，200 轮就不够了 ——
    /// 实测「管保温 1 mm」那档 200 轮报未收敛（剩余误差 76.9 K），
    /// **只把上限提到 1000、其余一律不动，第 734 轮收敛，剩余误差 0.99 K**。
    /// ⇒ 用户改个参数就看到「不可引用」，其实只差多跑几百轮。
    ///   不默认加轮数（那会让每次都慢几倍），而是**问一句** —— 决定权在用户。
    /// </summary>
    private async Task<LineResult> RetryIfJustSlowAsync(
        LineResult r, LineCase lc, IProgress<string> prog, CancellationToken ct)
    {
        if (r is null || !r.Ok || r.Converged) return r;
        if (!(r.Message?.Contains("慢") ?? false)) return r;      // 没在收缩 ⇒ 加轮数没用

        string need = r.Notes.FirstOrDefault(n => n.Contains("还需约")) ?? "";
        var ans = MessageBox.Show(this,
            "外层耦合没收敛，但残差**仍在单调收缩** —— 是「慢」，不是「发散」。\r\n\r\n" +
            (need.Length > 0 ? need.Replace("★ ", "") + "\r\n\r\n" : "") +
            $"要把轮数上限从 {lc.CoupleMaxRounds} 提到 1500 重跑一次吗？\r\n" +
            "（可能要几倍时间；随时可以点「取消」中止）",
            "要不要更耐心地再跑一次", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) return r;

        lc.CoupleMaxRounds = 1500;
        _status.Text = "加轮数重跑中…";
        var r2 = await Task.Run(() => LineRunner.Run(lc, prog, ct), ct);
        _out.Text = $"（第一次 {200} 轮未收敛，已把上限提到 1500 重跑）\r\n";
        return r2;
    }

    /// <summary>
    /// ▶ 复现定案：按选中档的**完整几何**解一次，出判据表。
    ///
    /// 与「核算整线」的区别，一句话：
    ///   · 核算整线 —— 读**页面上的控件**（可以随便改，用来试）
    ///   · 复现定案 —— 读 <see cref="FinalDesign"/>，**完全不看页面**（用来复现交付数字）
    /// 页面控件表达不了渐变环与逐片舌保温，所以只有这条路能对上定案值。
    /// </summary>
    private async Task ReproduceAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= FinalDesign.All.Length) return;
        var fd = FinalDesign.All[i];

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnRepro.Text = "取消"; _btnRun.Enabled = _btnAuto.Enabled = false;
        _prog.Visible = true; _prog.Style = ProgressBarStyle.Marquee;
        _status.Text = "复现中…（分钟级）";
        var prog = new Progress<string>(s => _status.Text = s);

        try
        {
            // ★ checkRamp: true —— ① 也要判。少判一条就不是「全判据通过」。
            var lc = fd.BuildCase(_base, checkRamp: true);
            var r = await Task.Run(() => LineRunner.Run(lc, prog, ct), ct);
            r = await RetryIfJustSlowAsync(r, lc, prog, ct);
            _last = r;
            Show(r);

            // 与 FinalDesign 记录值对账：不一致要**当场说出来**，不能等人自己发现
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("── 复现对账（本次实算 vs FinalDesign 记录值）");
            if (r.Ok)
            {
                double mt = r.TubeMassG, mf = r.FlangeMassG, all = r.TotalMassG;
                void Line(string nm, double got, double want)
                {
                    double d = want > 0 ? (got - want) / want * 100 : 0;
                    sb.AppendLine($"   {nm,-8}{got,9:0.0} g　记录 {want,7:0} g　" +
                                  $"差 {d,6:+0.0;−0.0} %" + (Math.Abs(d) <= 1.0 ? "" : "　⚠"));
                }
                Line("管", mt, fd.TubeMassG);
                Line("法兰", mf, fd.FlangeMassG);
                Line("合计", all, fd.TotalMassG);
                sb.AppendLine($"   全判据：{(r.AllOk ? "✓ 全过" : "✗ 有不过的")}　" +
                              $"（记录：{fd.Binding}）");
            }
            sb.AppendLine();
            sb.AppendLine("本次用的是 " + fd.Describe());
            sb.AppendLine("出处：" + fd.Provenance);
            sb.AppendLine("⚠ 这条路**完全不读页面上的控件** —— 页面表达不了渐变环与逐片舌保温。");
            sb.AppendLine("   想改参数试验请用「核算整线」；那条路读页面，数字不会等于定案值。");
            _out.Text += sb.ToString();
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            MessageBox.Show(this, ex.Message, "复现失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _prog.Visible = false;
            _btnRepro.Text = "▶ 复现定案";
            _btnRun.Enabled = _btnAuto.Enabled = true;
        }
    }

    /// <summary>
    /// 把选中的定案档灌进各控件。**值只从 <see cref="FinalDesign"/> 取**——
    /// UI 里再抄一份，就是「同一个数存两处然后悄悄漂开」（HANDOVER §1.8 最常见的失效）。
    /// </summary>
    private void LoadFinalDesign()
    {
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= FinalDesign.All.Length) return;
        var fd = FinalDesign.All[i];

        decimal C(double v, NumericUpDown n) =>
            Math.Clamp((decimal)v, n.Minimum, n.Maximum);

        _wall.Value = C(fd.WallMm, _wall);
        _tubeIns.Value = C(fd.TubeInsulMm, _tubeIns);
        _discD.Value = C(2 * fd.DiscRadiusMm, _discD);
        _tabLen.Value = C(fd.TabLengthMm, _tabLen);
        _tabW.Value = C(fd.TabHalfWidthMm, _tabW);
        _clamp.Value = C(fd.ClampTempC, _clamp);
        for (int j = 0; j < 4 && j < _tPlate.Length; j++)
            _tPlate[j].Value = C(fd.TabThickMm[j], _tPlate[j]);

        // 分段控温点：只改控温点，水头保持页面上原有的值（那是工艺量，不属于定案几何）
        string[] segNames = { "HC1", "HC2", "HC3" };
        for (int k = 0; k < fd.SetpointC.Length; k++)
        {
            if (k < _segs.Count) { _segs[k].名称 = segNames[k]; _segs[k].控温C = fd.SetpointC[k]; }
            else _segs.Add(new SegRow { 名称 = segNames[k], 控温C = fd.SetpointC[k] });
        }
        _segGrid.Refresh();

        _out.Text =
            "已载入定案档：" + fd.Describe() + "\r\n" +
            "咬住它的：" + fd.Binding + "\r\n" +
            "出处：" + fd.Provenance + "\r\n" +
            $"外层耦合剩余误差估计 {fd.ResidualK:0.00} K（不是步长；见 HANDOVER §1.85）\r\n\r\n" +
            "⚠ 有两项本页控件表达不了，已在内核里按定案值生效、但界面上看不到：\r\n" +
            $"   · 管孔两级渐变环：r ≤ 孔+{fd.RingWidthMm:0} → 板厚×{fd.RingMul[0]:0.00}，" +
            $"r ≤ 孔+{2 * fd.RingWidthMm:0} → 板厚×{fd.RingMulOuter(0):0.000}\r\n" +
            $"   · 逐片舌保温：{string.Join(" / ", fd.TabInsulMm)} mm（四片差 12 倍，不能同规格）\r\n" +
            "   ⇒ 想复现定案数，请用「导出定案 3DM」或命令行 --busbarplan --wall " +
            $"{fd.WallMm:0.0}；本页的「核算整线」走的是页面上这些参数。";
    }

    /// <summary>导出选中定案档的整机 3DM（子进程渲染 + 写完从磁盘回读自校）。</summary>
    private void ExportFinal3dm()
    {
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= FinalDesign.All.Length) return;
        var fd = FinalDesign.All[i];

        using var dlg = new SaveFileDialog
        {
            Filter = "Rhino 3DM|*.3dm",
            FileName = $"定案_管壁{fd.WallMm:0.0}mm.3dm"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            string echo = Geometry3dm.WriteFinal3dm(fd, dlg.FileName);
            _out.Text = "已写出 " + dlg.FileName + "\r\n\r\n" + echo + "\r\n\r\n" +
                "图层按**片**分（入口／共用1／共用2／出口 各有 板身/环外级/环内级/压接段），" +
                "另加「铂管」层三段。\r\n" +
                "回显里的 roundTrip 段是**从磁盘读回**量的包围盒：tY = 沿 Y 的跨度 = 板厚。" +
                "若某天板被画到 XY 面沿 Z 拉伸（2026-08-12 出过），tY 会变成盘直径 —— 一眼露馅。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出 3DM 失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private static ToolStripButton Btn(string t, EventHandler h)
    {
        var b = new ToolStripButton(t) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += h;
        return b;
    }

    /// <summary>
    /// 本页控件**表达不了**的法兰特征 —— 用来在输出里逐条列出来。
    ///
    /// ⚠ 我此前对用户说的是「有两项表达不了」，那是**低估**。实测 <see cref="MakePlate"/>
    ///   与 <see cref="FinalDesign.Plate"/> 逐字段比对，差的是**六项**：
    ///   两级渐变环、角焊缝、逐片舌保温、等宽舌片、舌根圆角、逐片独立厚度以外的分区。
    ///   ⇒ 用本页参数「核算整线」解的是一个**结构上更简单的法兰**，不是定案那一片。
    ///   判据仍然照实判（没有作假），但**不要拿它的数去和定案值比**。
    ///   把差异**打出来**，比悄悄用定案默认值补上更安全：后者会让人以为自己在试
    ///   定案构型，实际上试的是别的东西（§1.8 的形状）。
    /// </summary>
    private static string PageVsFinal(FlangePlate pg)
    {
        var miss = new List<string>();
        if (pg.DiscStepRadiiMm.Length == 0) miss.Add("管孔两级渐变环（定案 ×1.22–1.24）");
        if (pg.WeldFilletLegMm <= 1e-9) miss.Add("管孔两面角焊缝（定案 焊脚 = max(板厚, 壁厚)）");
        if (double.IsNaN(pg.TabInsulThickMm)) miss.Add("逐片舌保温（定案 18.7/1.6/1.4/3.9 mm，四片差 12 倍）");
        if (!pg.TabParallel) miss.Add("等宽舌片（本页是**梯形**，定案是等宽）");
        if (pg.TabFilletMm <= 1e-9) miss.Add("舌根过渡圆角（定案 R3；峰值电流拥塞就在这个凹角上）");
        return miss.Count == 0 ? "" : string.Join("\r\n         · ", miss);
    }

    private FlangePlate MakePlate(double tMm) => new()
    {
        DiscRadiusMm = (double)_discD.Value * 0.5,
        HoleRadiusMm = (double)_wall.Value + 25.0,      // LineRunner 会按管外径覆写
        TabEndXMm = -(double)_tabLen.Value,
        TabEndHalfWidthMm = (double)_tabW.Value,
        ThicknessMm = tMm, ThickenedMm = tMm,
        InsulBoundaryXMm = _flIns.SelectedIndex switch
        {
            0 => 1e9,              // 不包
            1 => double.NaN,       // 仅圆盘（取切点）
            _ => -1e9              // 全包
        }
    };

    private LineCase BuildCase()
    {
        var p = SegmentSolver.Clone(_base);
        p.WallMinMm = (double)_wall.Value;
        p.Layer1.ThicknessMm = (double)_tubeIns.Value;
        p.Layer1.Enabled = (double)_tubeIns.Value > 1e-6;
        p.FlangeInsulThickMm = _flIns.SelectedIndex == 0 ? 0 : (double)_flInsT.Value;
        p.FlangeInsulated = _flIns.SelectedIndex != 0;
        p.BusbarClampTempC = (double)_clamp.Value;

        var rows = _segs.Where(s => !string.IsNullOrWhiteSpace(s.名称)).ToList();
        var lc = new LineCase
        {
            Base = p,
            WallMm = (double)_wall.Value,
            UseMeasuredCurrent = false,          // 由控温反算 —— 第一性
            SetpointC = rows.Select(s => s.控温C).ToArray(),
            HeadM = rows.Select(s => s.水头m).ToArray(),
            CheckRamp = true,
        };
        if (_srcAnalytic.Checked)
            lc.FlangePlates = _tPlate.Select(n => MakePlate((double)n.Value)).ToArray();
        else
        {
            var files = _file3dm.Select(f => f.Text.Trim()).ToArray();
            if (files.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException("四片法兰的 .3dm 都要指定（可重复同一文件）");
            lc.FlangeFile3dm = files;
            lc.FlangeLayer = _layer3dm.Text.Trim();
            lc.ThicknessScale = _tPlate.Select(n => (double)n.Value).ToArray();
            if (_levels is not null) lc.LevelThicknessMm = _levels;
            if (_levelScale is not null) lc.LevelScale = _levelScale;
        }
        return lc;
    }

    private async Task RunAsync(bool autoSize)
    {
        if (_cts is not null) { _cts.Cancel(); return; }        // 再点一次 = 取消
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnRun.Enabled = !autoSize; _btnAuto.Enabled = autoSize;
        (autoSize ? _btnAuto : _btnRun).Text = "取消";
        _prog.Visible = true; _prog.Style = ProgressBarStyle.Marquee;
        _status.Text = autoSize ? "自动定厚中…" : "核算中…";

        var lc = BuildCase();
        var prog = new Progress<string>(s => _status.Text = s);

        try
        {
            if (autoSize)
            {
                var init = _tPlate.Select(n => (double)n.Value).ToArray();
                FlangeAutoSizer.Result r;
                if (!_srcAnalytic.Checked && _levels is { Length: > 0 } && _levels[0].Length > 1)
                {
                    // 逐级定厚：外层调每片整体厚度（管根温差），内层调各级比例（局部过热）
                    var lvl = _levels;
                    var lockMask = LockedMask();
                    r = await Task.Run(() => FlangeAutoSizer.SolveByLevel(
                        lc, lvl, new FlangeAutoSizer.Options(), prog, ct, 6, lockMask), ct);
                    _levelScale = r.LevelScale;
                }
                else
                {
                    Func<double, FlangePlate>? mk = _srcAnalytic.Checked ? MakePlate : null;
                    r = await Task.Run(() => FlangeAutoSizer.SolveAuto(
                        lc, mk, init, new FlangeAutoSizer.Options(), prog, ct), ct);
                }
                for (int i = 0; i < _tPlate.Length && i < r.ThicknessMm.Length; i++)
                    _tPlate[i].Value = (decimal)Math.Clamp(r.ThicknessMm[i], 0.1, 8.0);
                _last = r.Line;
                Show(r.Line, autoNote: r.Message + (r.Converged ? "" : "　⚠ 未收敛，下面的数不可引用"));
            }
            else
            {
                var r = await Task.Run(() => LineRunner.Run(lc, prog, ct), ct);
                r = await RetryIfJustSlowAsync(r, lc, prog, ct);
                _last = r;
                Show(r);
                // ★ 把「本次实际解的是什么」打出来。不打，用户会以为自己在试定案构型。
                if (_srcAnalytic.Checked && lc.FlangePlates is { Length: > 0 })
                {
                    string miss = PageVsFinal(lc.FlangePlates[0]);
                    if (miss.Length > 0)
                        _out.Text +=
                            "\r\n── ⚠ 本次解的**不是**定案那一片法兰\r\n" +
                            "   本页控件表达不了下面这些，已按「没有」求解：\r\n         · " + miss +
                            "\r\n   ⇒ 判据是照实判的，但**不要拿这些数去和定案值比**。" +
                            "要定案数字请点「▶ 复现定案」。\r\n";
                }
            }
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            MessageBox.Show(this, ex.Message, "求解失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _prog.Visible = false;
            _btnRun.Enabled = _btnAuto.Enabled = true;
            _btnRun.Text = "核算整线"; _btnAuto.Text = "自动定厚";
        }
    }

    private void Show(LineResult? r, string? autoNote = null)
    {
        if (r is null) { _out.Text = "无结果"; return; }
        var sb = new StringBuilder();
        if (autoNote is not null) sb.AppendLine("【自动定厚】" + autoNote).AppendLine();
        if (!r.Ok) { _out.Text = sb + "✗ " + r.Message; return; }

        if (!r.Converged)
            sb.AppendLine("╔══ ⚠ 段↔法兰耦合未收敛 —— 以下所有数值均不可引用 ══╗").AppendLine();

        sb.AppendLine($"{"段",6}{"控温",7}{"电流 A",9}{"管 J",8}{"管根 °C",10}{"衔接温差 K",12}{"管重 g",9}");
        foreach (var s in r.Segments)
            sb.AppendLine($"{s.Name,6}{s.SetpointC,7:0}{s.CurrentA,9:0}{s.TubeJAPerMm2,8:0.00}" +
                          $"{s.TRootC,10:0.0}{s.RootDeltaK,12:+0.0;-0.0}{s.MassG,9:0}");
        sb.AppendLine();
        sb.AppendLine($"{"法兰",10}{"电流 A",9}{"J_max",8}{"Φ",8}{"抽热 W",9}{"最高 °C",10}{"铂重 g",9}");
        foreach (var f in r.Flanges)
            sb.AppendLine($"{f.Name,10}{f.CurrentA,9:0}{f.JMaxAPerMm2,8:0.00}{f.Phi,8:0.000}" +
                          $"{f.QFromTubeW,9:+0;-0}{f.TMaxC,10:0.0}{f.MassG,9:0}");
        sb.AppendLine();
        // ★ 收敛情况必须**跟判据一起看**：判据是在解上判的，解没收敛判据就没意义。
        //   剩余误差是「距不动点」的估计，不是「相邻两轮变化」——后者曾把没收敛的解报成收敛（§1.85）。
        foreach (var nt in r.Notes)
            if (nt.Contains("耦合") || nt.Contains("基线")) sb.AppendLine("  ⓘ " + nt);
        if (!r.Converged) sb.AppendLine("  ⚠ **未收敛 ⇒ 下面每个数都不可引用**");
        sb.AppendLine();

        sb.AppendLine("判据　★=硬安全线，越界即失效　○=设计目标　·=参考量，只报数不判");
        sb.AppendLine("　　　裕度 = 离限值还有多远。**贴着限值判过与不过是本项目最常见的错**，");
        sb.AppendLine("　　　判之前先看它是否大于数值噪声与现场可分辨的尺度。");
        foreach (var c in r.Checks)
        {
            string mk = c.Kind == CheckKind.HardSafety ? "★" : c.Kind == CheckKind.Target ? "○" : "·";
            string vd = c.Kind == CheckKind.Reference ? "—" : c.Undetermined ? "?" : c.Ok ? "✓" : "✗";
            string act = double.IsNaN(c.Actual) ? "达不到" : c.Actual.ToString("0.000");
            // 裕度：单边上限判据用 (限−实)/限；方向性判据（限=0）只报差值本身
            string mg = "—";
            if (c.Kind != CheckKind.Reference && !double.IsNaN(c.Actual))
            {
                if (Math.Abs(c.Limit) > 1e-9)
                {
                    double pct = (c.Limit - c.Actual) / Math.Abs(c.Limit) * 100.0;
                    mg = pct >= 0 ? $"{pct,5:0}%" : $"超{-pct,4:0}%";
                }
                else mg = $"{c.Actual - c.Limit,+6:+0.00;−0.00}";
            }
            sb.AppendLine($"  {mk} {c.Name,-18}{act,12} / {c.Limit,-10:0.000} {mg,7} {vd}  {c.Where}");
            if (!string.IsNullOrEmpty(c.Note)) sb.AppendLine($"      {c.Note}");
        }
        sb.AppendLine();
        sb.AppendLine($"★ 整线总铂 {r.TotalMassG:0} g（管 {r.TubeMassG:0} + 法兰 {r.FlangeMassG:0}）" +
                      $"　基准 {r.BaselineMassG:0} g　省 {r.SavingPct:0.0} %");
        sb.AppendLine($"  玻璃温降 模型 {r.GlassDropModelK:0.0} / 实测 {r.GlassDropMeasuredK:0.0} K" +
                      "　（模型唯一的现场验证点）");
        foreach (var n in r.Notes) sb.AppendLine("  " + n);
        _out.Text = sb.ToString();

        // 场图取最不利那片（局部最高温）
        var worst = r.Flanges.OrderByDescending(f => f.TMaxC).FirstOrDefault();
        if (worst?.Mesh is not null)
        {
            FieldPlots.DrawShellField(_pT, worst.Mesh, worst.TField,
                $"法兰温度场　{worst.Name}", "温度", "°C", (double)_wall.Value + 25.0);
            FieldPlots.DrawShellField(_pJ, worst.Mesh, worst.JField,
                $"电流密度场　{worst.Name}", "J", "A/mm²", (double)_wall.Value + 25.0);
        }
    }

    /// <summary>
    /// 导出最终图纸。**两种来源分别走不同的路，但结果都是「拿去就能用的最终厚度」**：
    ///   · 解析模式 → 按当前四片厚度直接生成 .3dm
    ///   · .3dm 模式 → 把你原来的图按求出的标度**缩放另存**，
    ///     轮廓/孔/槽/各级半径不动，只有厚度乘 k —— 不需要你回 Rhino 手算每一级
    /// </summary>
    private void Export()
    {
        if (!_srcAnalytic.Checked) { ExportScaled(); return; }
        using var dlg = new SaveFileDialog
        {
            Filter = "Rhino 3D 模型 (*.3dm)|*.3dm",
            FileName = $"法兰_盘{_discD.Value:0}_舌{_tabLen.Value:0}.3dm"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            string log = Geometry3dm.WritePlate3dm(
                dlg.FileName, MakePlate(1.0),
                _tPlate.Select(n => (double)n.Value).ToArray(),
                new[] { "法兰_入口", "法兰_HC1|HC2", "法兰_HC2|HC3", "法兰_出口" });
            _out.Text = "【导出 .3dm】" + dlg.FileName + Environment.NewLine + log
                      + Environment.NewLine + _out.Text;
            _status.Text = "已导出";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + "\n\n导出需本机安装 Rhino 8。",
                            "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>.3dm 模式的导出：逐片按各自的标度缩放另存，文件名带上倍数便于追溯。</summary>
    private void ExportScaled()
    {
        using var fb = new FolderBrowserDialog { Description = "选择输出目录（四片各出一个 .3dm）" };
        if (fb.ShowDialog(this) != DialogResult.OK) return;

        var names = new[] { "入口", "共用1", "共用2", "出口" };
        var sb = new StringBuilder("【导出最终图纸】厚度已按自动定厚的结果改好，可直接用" + Environment.NewLine);
        try
        {
            Cursor = Cursors.WaitCursor;
            for (int i = 0; i < 4; i++)
            {
                string src = _file3dm[i].Text.Trim();
                if (string.IsNullOrEmpty(src)) continue;
                double k = (double)_tPlate[i].Value;
                string dst = Path.Combine(fb.SelectedPath,
                    $"{Path.GetFileNameWithoutExtension(src)}_{names[i]}_x{k:0.0000}.3dm");
                var ks = _levelScale is not null && i < _levelScale.Length && _levelScale[i].Length > 0
                       ? _levelScale[i] : new[] { k };
                string log = Geometry3dm.ScalePlate3dm(src, dst, _layer3dm.Text.Trim(), ks);
                sb.AppendLine($"— {names[i]}：厚度 ×{k:0.0000} → {Path.GetFileName(dst)}");
                foreach (var ln in log.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (ln.Trim().StartsWith("实体")) sb.AppendLine("    " + ln.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("轮廓、管孔、开槽、各级阶梯半径**全部未动**，只有厚度按倍数改变；");
            sb.AppendLine("各级之间的比例（如 3:2:1）完整保留。");
            _out.Text = sb + Environment.NewLine + _out.Text;
            _status.Text = "已导出最终图纸";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + Environment.NewLine + Environment.NewLine + "需本机安装 Rhino 8。",
                            "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>
    /// 读入口片的 .3dm，把它反推成一组几何变数（各级半径与厚度、槽数与角宽、舌片尺寸）。
    /// 这是「任意形状也能优化」的前提 —— 先有参数，才谈得上让优化器去动它们。
    /// </summary>
    private void AnalyzeShape()
    {
        string src = _file3dm[0].Text.Trim();
        if (_srcAnalytic.Checked || string.IsNullOrEmpty(src))
        {
            MessageBox.Show(this, "请先切到「Rhino .3dm 文件」并选好入口片的图纸。",
                            "分析几何变数", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            _status.Text = "提取厚度场并解析…";
            var f = Geometry3dm.LoadThickness(src, _layer3dm.Text.Trim(), double.NaN, 0.5);
            var sh = PlateShapeAnalyzer.Analyze(f);
            // 四片先按同一张图的分级；各片可各自选不同 .3dm 时逐片解析亦可
            var lv = sh.Levels.Select(l => l.ThicknessMm).ToArray();
            _levels = Enumerable.Range(0, 4).Select(_ => (double[])lv.Clone()).ToArray();
            _levelScale = null;
            BuildLockBoxes(sh);
            _out.Text = PlateShapeAnalyzer.Format(sh) + Environment.NewLine
                      + $"→ 已记下 {lv.Length} 级厚度，「自动定厚」将让优化器自行决定各级比例。"
                      + Environment.NewLine
                      + "来源：" + src + Environment.NewLine + Environment.NewLine + _out.Text;
            _status.Text = "已解析";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = "失败";
        }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>按解析出的分级列出「锁定」勾选框。半径大的在上，便于对应「外圈」。</summary>
    private void BuildLockBoxes(PlateShapeAnalyzer.Shape sh)
    {
        _lockPanel.Controls.Clear();
        _lockBoxes.Clear();
        for (int m = 0; m < sh.Levels.Count; m++)
        {
            var l = sh.Levels[m];
            var cb = new CheckBox
            {
                AutoSize = true,
                Text = $"第{m + 1}级  t={l.ThicknessMm:0.00}  R{l.RInnerMm:0}–{l.ROuterMm:0}",
                Tag = m
            };
            _lockBoxes.Add(cb);
            _lockPanel.Controls.Add(cb);
        }
        if (sh.Levels.Count == 0)
            _lockPanel.Controls.Add(new Label { AutoSize = true, Text = "（未解析到分级）" });
    }

    /// <summary>把勾选状态摊成 [片][级] 的锁定表；四片共用同一套勾选。</summary>
    private bool[][]? LockedMask()
    {
        if (_lockBoxes.Count == 0 || !_lockBoxes.Any(c => c.Checked)) return null;
        var one = _lockBoxes.Select(c => c.Checked).ToArray();
        return Enumerable.Range(0, 4).Select(_ => (bool[])one.Clone()).ToArray();
    }
}
