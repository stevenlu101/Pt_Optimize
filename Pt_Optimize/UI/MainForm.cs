using System.Text;
using System.Text.Json;
using PtOptimize.Core;

namespace PtOptimize.UI;

public sealed class MainForm : Form
{
    private readonly PropertyGrid _grid = new();
    private readonly RichTextBox _out = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ScottPlot.WinForms.FormsPlot _pAxial = FieldPlots.NewPlot();
    private readonly DataGridView _segGrid = new();
    private readonly BindingSource _segBind = new();
    private readonly List<Segment> _segs = new()
    {
        new Segment { Name = "HC1", TSetC = 1150, TGlassInC = 1150, GlassHeadM = 0.3, LengthMm = 300 },
        new Segment { Name = "HC2", TSetC = 1080, TGlassInC = 1140, GlassHeadM = 0.6, LengthMm = 300 },
        new Segment { Name = "HC3", TSetC = 1050, TGlassInC = 1130, GlassHeadM = 1.0, LengthMm = 300 },
    };
    private readonly RichTextBox _segOut = new();
    /// <summary>「② 粗算」分段结果的**真表格**（用户 2026-08-20：能用 Excel 格式就用）。</summary>
    private readonly DataGridView _segResult = GridFmt.NewGrid();
    private DesignInputs _in = new();
    private SolveResult? _res;

    // 法兰定尺是分钟级的耦合解，必须给进度且可取消
    private readonly ToolStripProgressBar _segProg = new() { Visible = false, Maximum = 1000 };
    private readonly ToolStripLabel _segStatus = new("") { Visible = false };
    private ToolStripButton? _flangeBtn;
    private CancellationTokenSource? _flangeCts;

    // ── 阶段轨（2026-08-20）
    /// <summary>「解」的单一来源，③④⑤ 共享。与「判据」的单一来源（LineRunner.Judge）对应。</summary>
    private readonly FlowState _flow = new();
    private StagePanel? _stagePanel;
    private LineDesignPage? _linePage;

    /// <summary>给 UiShot 逐页出图用（--cli --uishot）。</summary>
    internal TabControl Tabs => _tabs;
    private readonly Dictionary<TabPage, StageId> _stageOf = new();
    /// <summary>每一格的门禁横幅，SyncGates 按锁态显示/隐藏。</summary>
    private readonly Dictionary<StageId, Label> _banners = new();

    public MainForm()
    {
        Text = "Pt_Optimize — 铂金直接加热 整线设计与用量优化";
        // ★ 窗口与字号都随屏幕走（2026-08-17）。原来写死 1400×900 + 9 pt，
        //   在高分屏上只占约 36 % 的画面，字也小 —— 见 UiScale 的说明。
        var sz = UiScale.WindowSize();
        Width = sz.Width; Height = sz.Height;
        MinimumSize = new Size(UiScale.S(1000), UiScale.S(680));
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiScale.Ui();
        // ⚠ 这里**故意不设** AutoScaleMode.Font：字号已经由 UiScale 显式放大过一轮，
        //   再让框架按字体自动缩放一次就是**放大两次**（尺寸对不上、控件互相盖）。
        //   缩放只允许有一个来源 —— 与「判据只有一个来源」是同一条道理。
        AutoScaleMode = AutoScaleMode.None;

        // ═══════════════════════════════════════════════════════════════
        //  阶段轨（2026-08-20）
        // ═══════════════════════════════════════════════════════════════
        //
        // 用户：「目前的 UI 界面太乱了，有好几种算法链条，可以依照算链条分类
        //        （现在是所有标签键都可以点，工程师根本不知道自己目前在算什么）」
        //
        // 结构全部来自 UI/Flow.cs 这一份数据 —— 页签、按钮归属、门禁、说明书的
        // 界面地图、接线测试，四方读同一张表。**不再有第二处需要人记得同步的地方。**
        Flow.SelfTest();

        _grid.SelectedObject = _in;
        _grid.PropertySort = PropertySort.Categorized;
        _grid.HelpVisible = true;
        _grid.Dock = DockStyle.Fill;

        // ★★★★★ 参数表改一项 = 上一次的解不再新鲜（2026-08-24）。
        //   此前这个事件**从来没被挂过** —— 详见 LineDesignPage.MarkParamsChanged 的说明：
        //   改控温点/保温/牌号既不作废解、也不改 CurrentSnap（它只记 6 个页面控件）
        //   ⇒ Fresh 仍为 true ⇒ ④⑤ 的门开在一张**别的参数**的判据表上。
        _grid.PropertyValueChanged += (_, e) =>
            _linePage?.MarkParamsChanged(e.ChangedItem?.Label ?? "某一项");

        _out.Dock = DockStyle.Fill;
        _out.Font = UiScale.Mono();
        _out.ReadOnly = true;
        _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);
        // 挂一次就覆盖这个框的所有写入路径（`.Text =`、`+=`、AppendText）。
        // 逐处改成「格式化写入」必然漏掉几处，而漏掉的那几处夹在排好的内容中间最难发现。
        TextFmt.Hook(_out);

        // ── 分段输入表：每段独立的温度、水头、牌号、几何
        _segGrid.Dock = DockStyle.Fill;
        _segGrid.AutoGenerateColumns = true;
        _segGrid.AllowUserToAddRows = true;
        _segGrid.AllowUserToDeleteRows = true;
        _segGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _segBind.DataSource = _segs;
        _segGrid.DataSource = _segBind;
        _segGrid.DataError += (_, e) => e.ThrowException = false;

        _segOut.Dock = DockStyle.Fill;
        _segOut.Font = UiScale.Mono();
        _segOut.ReadOnly = true; _segOut.WordWrap = false;
        _segOut.BackColor = Color.FromArgb(252, 252, 250);
        TextFmt.Hook(_segOut);

        // ── ③ 整线核算 / ① 闸门：两页各自持有自己的控件与按钮
        var linePage = new LineDesignPage(_in) { Shared = _flow };
        var gatePage = new AnalysisPage(_in) { Shared = _flow };
        _linePage = linePage;

        // ── ② 快筛：把原来散在「分段核算」「轴向剖面」与**主窗口右上**的三块并成一页。
        //
        // ★ 把单段报告 `_out` 从常驻右上搬进这一页，是本次重排里最要紧的一刀：
        //   一个**不含法兰的单段解**长期占着主视野的三分之一，
        //   正是「不知道自己在算什么」的根源之一。
        var screenTool = NewTool();
        screenTool.Items.Add(Btn("计算 (F5)", (_, _) => Run()));
        screenTool.Items.Add(Btn("扫描：保温厚度", (_, _) => Sweep("insul", 0, 50, 11, "内层保温厚度 [mm]")));
        screenTool.Items.Add(Btn("扫描：铂发射率", (_, _) => Sweep("eps", 0.10, 0.30, 9, "铂表面发射率 ε")));
        screenTool.Items.Add(Btn("导出 CSV", (_, _) => ExportCsv()));
        screenTool.Items.Add(new ToolStripSeparator());
        screenTool.Items.Add(Btn("核算全线", (_, _) => RunLine()));
        screenTool.Items.Add(Btn("为各段选最省牌号", (_, _) => AutoGrade()));
        screenTool.Items.Add(Btn("按强度取最小壁厚", (_, _) => MinWalls()));
        _flangeBtn = Btn("核算法兰（分钟级）", (_, _) => _ = RunFlangesAsync());
        screenTool.Items.Add(_flangeBtn);
        _segProg.Size = new Size(UiScale.S(180), UiScale.S(16));
        screenTool.Items.Add(_segProg);
        screenTool.Items.Add(_segStatus);

        var screenInner = new TabControl { Dock = DockStyle.Fill };
        screenInner.TabPages.Add(TabWith("分段核算",
            SplitH(_segGrid, SplitH(_segResult, _segOut))));
        screenInner.TabPages.Add(TabWith("单段报告", _out));
        screenInner.TabPages.Add(TabWith("轴向剖面", _pAxial));

        var screenPage = new TabPage(Flow.Stage(StageId.粗算).Title) { Padding = new Padding(2) };
        screenPage.Controls.Add(screenInner);
        screenPage.Controls.Add(Banner(Flow.Stage(StageId.粗算).Banner));
        screenPage.Controls.Add(screenTool);

        // ── ④ 定尺寸 / ⑤ 交付：**薄页**。
        //   它们的输入就是 ③ 的解，不是新的输入 —— 所以只放命令与只读摘要。
        //   把这些按钮塞回 ③ 的工具条，正是今天「十个按钮一横排」的病因。
        var sizeTool = NewTool();
        sizeTool.Items.Add(linePage.BtnAutoThick);
        sizeTool.Items.Add(linePage.BtnSearchShape);
        sizeTool.Items.Add(new ToolStripSeparator());
        sizeTool.Items.Add(gatePage.BtnThicknessScan);
        var sizePage = new TabPage(Flow.Stage(StageId.定尺寸).Title) { Padding = new Padding(2) };
        sizePage.Controls.Add(StageHint(StageId.定尺寸));
        sizePage.Controls.Add(Banner(Flow.Stage(StageId.定尺寸).Banner));
        sizePage.Controls.Add(sizeTool);

        // ── 定案档：**不带编号的一页**，放在 ① 之前。
        //   载入/复现会灌页面控件、是给 ③ 喂起点的 ⇒ 它是入口，不是尾巴。
        //   这几条命令都 ReadsPageControls=false（另存除外），本来就豁免阶段门禁。
        var caseTool = NewTool();
        caseTool.Items.Add(new ToolStripLabel("定案档"));
        caseTool.Items.Add(linePage.CaseBox);
        caseTool.Items.Add(linePage.BtnReproduce);
        caseTool.Items.Add(linePage.BtnLoadCase);
        caseTool.Items.Add(new ToolStripSeparator());
        caseTool.Items.Add(linePage.BtnSaveFinal);
        caseTool.Items.Add(linePage.BtnExportFinal3dm);
        var casePage = new TabPage(Flow.Stage(StageId.定案档).Title) { Padding = new Padding(2) };
        casePage.Controls.Add(StageHint(StageId.定案档));
        casePage.Controls.Add(Banner(Flow.Stage(StageId.定案档).Banner));
        casePage.Controls.Add(caseTool);

        var shipTool = NewTool();
        shipTool.Items.Add(linePage.BtnExportPage3dm);
        shipTool.Items.Add(new ToolStripSeparator());
        shipTool.Items.Add(Btn("保存", (_, _) => Save()));
        shipTool.Items.Add(Btn("读取", (_, _) => LoadCase()));
        var shipPage = new TabPage(Flow.Stage(StageId.交付).Title) { Padding = new Padding(2) };
        shipPage.Controls.Add(StageHint(StageId.交付));
        shipPage.Controls.Add(Banner(Flow.Stage(StageId.交付).Banner));
        shipPage.Controls.Add(shipTool);

        // ── 按 Flow 的顺序装轨
        gatePage.Text = Flow.Stage(StageId.先决条件).Title;
        linePage.Text = Flow.Stage(StageId.整线核算).Title;
        _tabs.TabPages.Add(casePage);
        _tabs.TabPages.Add(gatePage);
        _tabs.TabPages.Add(screenPage);
        _tabs.TabPages.Add(linePage);
        _tabs.TabPages.Add(sizePage);
        _tabs.TabPages.Add(shipPage);
        _tabs.TabPages.Add(new ManualPage(_in));   // 说明书里的限值要跟着参数表走

        _stageOf[casePage] = StageId.定案档;
        _stageOf[gatePage] = StageId.先决条件;
        _stageOf[screenPage] = StageId.粗算;
        _stageOf[linePage] = StageId.整线核算;
        _stageOf[sizePage] = StageId.定尺寸;
        _stageOf[shipPage] = StageId.交付;

        // ── 状态面板：接替原来右上那块单段报告的位置
        var right = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        right.FixedPanel = FixedPanel.Panel1;

        _stagePanel = new StagePanel(_flow);
        // 指路不许指到用不了的命令上（.3dm 模式下的「◇ 搜形状」就是这种）
        _stagePanel.ApplicableProbe = id => _linePage?.CommandApplicable(id) ?? true;
        _stagePanel.BypassRequested += s =>
        {
            _flow.Bypassed.Add(s);
            _flow.Notify();
            SyncGates();
        };
        _tabs.SelectedIndexChanged += (_, _) => SyncGates();

        // ★★★★★ 有链在跑时**禁止切页**（2026-08-24 用户提出）。
        //
        // 用户原话：「没有这限制工程师随便点，整条链路就乱（甚至不知道自己正在算什麽）」。
        // 实况：在 ④ 点了「搜形状」（几十分钟）之后可以立刻切到 ③ 改参数、再切到 ⑤ 看出图 ——
        // 链还在跑，而每一页讲的都是**别的**事：③ 的输出框是上一次的解、
        // ⑤ 的门禁读的是上一次的判据。于是「我在算什么」从界面上消失了。
        //
        // ⇒ 跑起来就钉在当前页。**取消键就在这一页上**（它已经变成「取消」），
        //   状态面板也在，所以钉住不会把人困死 —— 想走，先取消。
        //
        // ⚠ 不静默拒绝：点了没反应比拦住更糟。拦下时把「取消」闪两下，
        //   把眼睛引到那个唯一能让他离开的按钮上。
        _tabs.Selecting += (_, e) =>
        {
            if (_flow.Running is null) return;
            e.Cancel = true;
            // ⚠ 只 e.Cancel 就是「点了没反应」——本文件 SyncGates 那段注释点名批过这一类。
            //   所以拦下的同时必须当场说清楚：气泡出现在**点击处**（不抢状态面板的进度文字），
            //   2.5 秒自己消失；同时把「取消」闪两下 —— 那是唯一能让他离开的按钮。
            var pt = _tabs.PointToClient(Cursor.Position);
            _blockTip.ToolTipTitle = "正在算，先取消才能换页";
            _blockTip.Show($"当前在跑：{_flow.Running}"
                           + (_flow.RunningNote.Length > 0 ? $"（{_flow.RunningNote}）" : "")
                           + Environment.NewLine
                           + "换页会让你看不见自己在算什么 —— 判据表、指路、门禁显示的都是上一次的。",
                           _tabs, pt.X + 12, pt.Y + 20, 2500);
            FlashCommand("取消");
        };

        // 点「下一步」那一行：**只带路，不代跑**（2026-08-22 与用户议定）。
        //   ④ 会改输入、③ 要跑几十秒、⑤ 会写文件 —— 代跑等于把
        //   「我知道我在做什么」从工程师手里拿走，而那正是要治的病。
        _stagePanel.NextStepRequested += cmdId =>
        {
            var spec = Flow.Commands.FirstOrDefault(c => c.Id == cmdId);
            if (spec is null) return;
            var target = _stageOf.FirstOrDefault(kv => kv.Value == spec.Stage).Key;
            if (target is not null) _tabs.SelectedTab = target;
            FlashCommand(spec.Text);
        };

        // ★ 状态一变就重算门禁与互斥（2026-08-21）。
        //   没有这一句，SetRunning 只会让**状态面板**跟着变，
        //   而按钮的 Enabled 纹丝不动 —— 互斥闸等于没装。
        //   StagePanel 自己也订阅了 Changed，两边各刷各的那一部分，互不知道对方。
        _flow.Changed += () =>
        {
            // ⚠ 判据是「有没有句柄 **且** 跨没跨线程」，不是「有没有句柄」。
            //   头一版写成 `if (!IsHandleCreated) return;` —— 而 Form.CreateControl()
            //   **只在控件可见时才建句柄**，窗体没 Show 过就恒 false
            //   ⇒ 处理器每次直接 return，互斥闸整个不生效（接线测试第 26 节抓到）。
            //   StagePanel 的写法才是对的：没句柄就直接调。
            if (IsDisposed) return;
            if (IsHandleCreated && InvokeRequired) { BeginInvoke(new Action(SyncGates)); return; }
            SyncGates();
        };
        _stagePanel.HeightWanted += h =>
        {
            // 面板要多高就给多高，但留出下半部至少能看见页签与工具条
            if (right.Parent is null) return;
            int max = Math.Max(UiScale.S(90), right.Height - UiScale.S(320));
            int want = Math.Clamp(h, UiScale.S(90), max);
            try { if (Math.Abs(right.SplitterDistance - want) > 2) right.SplitterDistance = want; }
            catch { /* 窗口还没排完版时会抛，下一次刷新会补上 */ }
        };

        right.Panel1.Controls.Add(_stagePanel);
        right.Panel2.Controls.Add(_tabs);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(_grid);
        main.Panel2.Controls.Add(right);

        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            try { right.SplitterDistance = UiScale.S(150); } catch { }
            SyncGates();
        });

        Controls.Add(main);

        Load += (_, _) =>
        {
            // 左侧参数表按窗口比例给宽度（原来写死 430）—— 屏越宽，标签越不该被切
            main.SplitterDistance = Math.Clamp((int)(main.Width * 0.30),
                                               UiScale.S(360), Math.Max(UiScale.S(360), main.Width - UiScale.S(520)));
            WidenPropertyGridLabels(_grid, 0.62);
            ApplyToolStripFont(this);
            Run();
            RunLine();
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) Run();
            // F1 = 帮助：跳到「使用说明」页（图文，按定案档实时生成）
            else if (e.KeyCode == Keys.F1) ShowHelp();
        };
    }

    // ═══ 阶段轨的搭建辅助 ═══════════════════════════════════════════

    private static ToolStrip NewTool() => new()
    { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(1, 1), Font = UiScale.Ui() };

    private static TabPage TabWith(string title, Control c)
    {
        var t = new TabPage(title) { Padding = new Padding(2) };
        c.Dock = DockStyle.Fill;
        t.Controls.Add(c);
        return t;
    }

    private static SplitContainer SplitH(Control top, Control bottom)
    {
        var sp = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        sp.Panel1.Controls.Add(top);
        sp.Panel2.Controls.Add(bottom);
        return sp;
    }

    /// <summary>页顶横幅 —— 文字来自 Flow.StageSpec.Banner，本处不另写一份。</summary>
    private static Label Banner(string text) => new()
    {
        Text = text.Replace("**", ""),   // 横幅是 Label，不走 TextFmt 的加粗
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = UiScale.S(34),
        Padding = new Padding(UiScale.S(8), UiScale.S(6), UiScale.S(8), UiScale.S(6)),
        BackColor = Color.FromArgb(255, 250, 225),
        ForeColor = Color.FromArgb(90, 70, 0),
        Font = UiScale.Ui(),
        Visible = text.Length > 0,
    };

    /// <summary>
    /// ④⑤ 这类薄页的正文：说明本页的输入**来自上一格的解**，不是新的输入。
    /// 真正的门禁提示在右上的状态面板里（那里才有实时判据数据）。
    /// </summary>
    private Label StageHint(StageId s)
    {
        var lab = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(UiScale.S(14)),
            Font = UiScale.Ui(),
            ForeColor = Color.FromArgb(70, 70, 70),
        };
        var spec = Flow.Stage(s);
        var sb = new StringBuilder();
        sb.AppendLine(spec.Title);
        sb.AppendLine();
        sb.AppendLine("本页的输入是「③ 整线核算」解出来的那个构型 —— 不是新的一组参数。");
        sb.AppendLine("要改参数请回 ③；本页只负责在那个解的基础上继续。");
        sb.AppendLine();
        sb.AppendLine("本页命令：");
        foreach (var c in spec.CommandIds.Select(Flow.Cmd))
            sb.AppendLine($"　· {c.Text}（{c.Cost}）—— {c.Tip.Replace("**", "")}");
        sb.AppendLine();
        sb.AppendLine("右上角的状态面板会说明：现在算的是哪条链、结果还新不新鲜、门开没开。");
        lab.Text = sb.ToString();
        _stageHints[s] = lab;
        return lab;
    }
    private readonly Dictionary<StageId, Label> _stageHints = new();

    /// <summary>拦下换页时那个说明气泡 —— 出现在点击处，2.5 秒自散，不占状态面板。</summary>
    /// <summary>正在闪的按钮 —— 防止连点叠加定时器，把底色恢复成闪烁中的那个颜色。</summary>
    private readonly HashSet<ToolStripButton> _flashing = new();

    private readonly ToolTip _blockTip = new() { IsBalloon = true, UseAnimation = true };

    /// <summary>
    /// 按门禁刷新：锁住的那一格，**命令按钮禁用 + 页签标题加锁**，但**允许只读进入**。
    ///
    /// ⚠ 不用 TabControl.Selecting + e.Cancel 硬拦 —— 那个效果是「点了没反应」，
    ///   正是用户抱怨的那一类。让人进得去、看得见为什么锁着，才叫说明白了。
    ///
    /// ★ 与「有链在跑时禁止换页」是**两回事**，别混（2026-08-24 补）：
    ///   · 本条说的是**门禁**——「这一格的前置条件还没满足」。那是个**静态**状态，
    ///     人进去看清楚为什么锁着，比被挡在外面有用。
    ///   · 那一条说的是**有活在跑**——「你现在离开，就看不见自己在算什么」。
    ///     那是个**瞬时**状态，而且有唯一出口（取消）。这一种才该硬拦，
    ///     并且拦的时候必须当场说明白（气泡 + 闪「取消」），不能只是点了没反应。
    ///   两者都用 Selecting，但**条件不同、给的话也不同**。
    /// </summary>
    /// <summary>
    /// 让某个命令按钮闪两下 —— 用户点了「下一步」之后，把眼睛引到它上面。
    /// **只改外观，不触发它的 Click。**
    /// </summary>
    private void FlashCommand(string text)
    {
        ToolStripButton? btn = null;
        foreach (TabPage p in _tabs.TabPages)
            foreach (var ts in p.Controls.OfType<ToolStrip>())
                foreach (var b in ts.Items.OfType<ToolStripButton>())
                    if (b.Text == text) btn = b;
        if (btn is null) return;

        // ★ 连点保护（2026-08-24）：拦下换页时会闪「取消」，而用户可能连点几下页签。
        //   两个定时器叠上去，第二个会把**已经闪成黄色**的那一刻当作原色存下来，
        //   收工时把按钮恢复成黄的 —— 一个永远亮着的「取消」比不闪更误导。
        if (!_flashing.Add(btn)) return;

        var keep = btn.BackColor;
        int n = 0;
        var t = new System.Windows.Forms.Timer { Interval = 180 };
        t.Tick += (_, _) =>
        {
            btn.BackColor = (n % 2 == 0) ? Color.FromArgb(255, 236, 150) : keep;
            if (++n >= 6) { t.Stop(); t.Dispose(); btn.BackColor = keep; _flashing.Remove(btn); }
        };
        t.Start();
    }

    private void SyncGates()
    {
        if (_stagePanel is null) return;

        // 「现在有没有链在跑」只有一个来源：FlowState.Running。
        // 各页自己那套 Enabled 只管得住自己页内的按钮，管不了跨页。
        bool busy = _flow.Running is not null;

        // ★★★★★ 有链在跑时，**输入面整块冻住**（2026-08-24 用户提出）。
        //
        // 互斥此前只禁「会起算的按钮」。可参数是自由的：整线解跑着的那几分钟里，
        // 左边参数表、③ 页的壁厚/盘径/段表/几何来源都还能改，而判据表、指路、
        // 门禁显示的全是**上一次**的结论 —— 工程师看到的和正在算的不是一回事。
        // 这不只是「乱」：它能让 ④⑤ 两道 RequireFresh 的门开在一张**别的参数**的判据表上。
        _grid.Enabled = !busy;
        _linePage?.SetInputsEnabled(!busy);

        // 该点哪个 —— 规则在 Flow.Next，这里只负责把它画出来。
        string nextId = Flow.Next(_flow,
            id => _linePage?.CommandApplicable(id) ?? true)?.CmdId ?? "";

        if (_tabs.SelectedTab is { } tab && _stageOf.TryGetValue(tab, out var cur))
            _stagePanel.SetStage(cur);

        foreach (var (page, sid) in _stageOf)
        {
            var g = Gate.Evaluate(sid, _flow);
            string baseTitle = Flow.Stage(sid).Title;
            string want = g.Unlocked ? (g.Bypassed ? "⚠ " + baseTitle : baseTitle) : "🔒 " + baseTitle;
            if (page.Text != want) page.Text = want;

            // 命令按钮：只管「读页面控件」的那些；「定案」组不受门禁（它们不读页面）
            foreach (var ts in page.Controls.OfType<ToolStrip>())
                foreach (var b in ts.Items.OfType<ToolStripButton>())
                {
                    var spec = Flow.Commands.FirstOrDefault(c => c.Text == b.Text);
                    if (spec is null) continue;                 // 跑起来变成「取消」的那个，别动它

                    // ★ 能不能点**只问 Flow.Blocks 一处**（2026-08-23）。
                    //   这三个因素（门禁 × 互斥 × 适用性）原本内联在这里，
                    //   而 Gate 里另有一个只算门禁的 Blocks —— 两处答案不一致，
                    //   只因那一处是死代码才没出事。现在它是唯一来源，这里只负责画。
                    var why = Gate.Blocks(spec, _flow, id => _linePage?.CommandApplicable(id) ?? true);
                    b.Enabled = why == Gate.Block.None;


                    // ★ 高亮「现在该点的那个」——眼睛直接落上去，不用先读文字。
                    //   只在它**真的能点**时才亮，否则等于指着一个灰按钮说「点这个」。
                    bool isNext = b.Enabled && spec.Id == nextId;
                    b.Font = isNext ? UiScale.Ui(FontStyle.Bold) : UiScale.Ui();
                    b.BackColor = isNext ? Color.FromArgb(214, 233, 255) : Color.Transparent;
                }
        }
        _stagePanel.Refresh2();
    }

    /// <summary>
    /// ★★★ 把**整棵控件树**里所有 ToolStrip 的字体统一设一遍（2026-08-18）。
    ///
    /// 起因：用户反馈「下排的字还是太小」。原因是 <see cref="ToolStrip"/>
    /// **不继承父窗体的 Font**（它用 ToolStripManager 的默认字体），
    /// 所以在 Form 上设 Font 对每一条工具条都无效。
    ///
    /// 为什么不逐个在构造处设、而要在这里扫一遍：
    /// 逐个设漏了一条就少一条，而**漏掉的那条夹在正确的几条中间，比全都不设更难发现**。
    /// 实测就漏了一条 —— <see cref="PropertyGrid"/> **内部自带**一条工具条
    /// （参数表上方那排小图标），那条根本不在我的代码里，逐个设永远设不到它。
    /// ⇒ 统一扫，把「必须记得设」这个前提整个去掉。
    /// </summary>
    private static void ApplyToolStripFont(Control root)
    {
        if (root is ToolStrip ts)
        {
            ts.Font = UiScale.Ui();
            // ⚠ 光设 ToolStrip.Font 不够：**已经被显式设过字体的项不再继承**
            //   （典型是 `new Font(btn.Font, Bold)` —— 它把当时的小字体固化了），
            //   而 ToolStripComboBox/TextBox 这类**宿主控件**里包着一个真控件，
            //   也不跟着走。⇒ 逐项按住，并把宿主里的控件也一并设。
            foreach (ToolStripItem it in ts.Items)
            {
                it.Font = it.Font is { Bold: true } ? UiScale.Ui(FontStyle.Bold) : UiScale.Ui();
                if (it is ToolStripControlHost host && host.Control is { } inner)
                    inner.Font = UiScale.Ui();
            }
        }
        foreach (Control c in root.Controls) ApplyToolStripFont(c);
    }

    /// <summary>
    /// ★ 把 PropertyGrid 的**标签列**加宽（2026-08-17，抓 UI 时发现的）。
    ///
    /// 实况：标签被切成「管许用电流密度 [A/m」「焊接工艺最小厚度 [mr」「端部额外保温的长度 rr」
    /// —— **被吃掉的正好是单位**，而单位是最不能靠猜的部分（A/mm² 还是 A/m？mm 还是 m？）。
    /// PropertyGrid 默认把标签列固定在一个较窄的比例上，且没有公开属性可调。
    ///
    /// ⚠ 只能走内部字段，所以**必须包在 try 里**：换 .NET 版本时字段名可能变
    ///   （.NET Framework 叫 gridView，.NET Core 叫 _gridView），
    ///   拿不到就维持默认 —— 界面难看总好过启动就崩。
    /// </summary>
    private static void WidenPropertyGridLabels(PropertyGrid grid, double frac)
    {
        try
        {
            var f = typeof(PropertyGrid).GetField("_gridView",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                 ?? typeof(PropertyGrid).GetField("gridView",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gv = f?.GetValue(grid);
            if (gv is null) return;
            var m = gv.GetType().GetMethod("MoveSplitterTo",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(gv, new object[] { (int)(grid.Width * frac) });
        }
        catch { /* 拿不到就算了：宁可标签窄，也不能因为排版让程序起不来 */ }
    }

    /// <summary>F1／「帮助」：切到使用说明页。说明书不做成单独的导出命令，
    /// 就放在程序里 —— 图是按当前定案档实时画的，导出来的静态副本会和定案值漂开。</summary>
    private void ShowHelp()
    {
        foreach (TabPage t in _tabs.TabPages)
            if (t is ManualPage) { _tabs.SelectedTab = t; return; }
    }

    private void RunLine()
    {
        _segBind.EndEdit();
        var rs = LineSolver.Solve(_segs, _in);
        var (mass, cost, bad) = LineSolver.Totals(rs);
        var sb = new StringBuilder();
        // ★★ 2026-08-20：这张表改用**真表格控件**（用户：「能用 Excel 格式表示就用」
        //   「要有自动合适的格宽与格高」）。
        //   文本框 + 制表位是在**模拟**表格：没有列宽自适应、没有行高，长内容只能折行；
        //   实测这张表的表头与数据行还错开了位，查因很费劲。
        //   DataGridView 的列宽行高是控件自己算 —— 不需要任何人去量像素。
        //   散文（材料数据 / 合计 / 注）仍留在下面的文本框里，各归各位。
        var tab = new StringBuilder();
        tab.AppendLine("段	温度 °C	水头 m	牌号	壁厚 mm	强度最小 mm	σ_vm MPa	许用 MPa	"
                    + "利用率	铂重 g	相对成本	判定");
        foreach (var r in rs)
            tab.AppendLine($"{r.Seg.Name}\t{r.Seg.TSetC:0}\t{r.Seg.GlassHeadM:0.0}\t{r.Seg.GradeName}\t"
                        + $"{r.Seg.WallMm:0.000}\t"
                        + (double.IsNaN(r.MinWallStrengthMm) ? "不可行" : r.MinWallStrengthMm.ToString("0.000"))
                        + $"\t{r.VonMisesMPa:0.000}\t"
                        // ⚠ NaN 不能直接丢进格式串：.NET 会打出「非数值 / 非數值」（还跟区域设置走），
                        //   看起来像程序出错。判不了就写「—」，让人一眼看出是**这一格没有数**。
                        + (double.IsNaN(r.AllowMPa) ? "—" : r.AllowMPa.ToString("0.000")) + "\t"
                        + (double.IsNaN(r.Utilization) ? "—" : r.Utilization.ToString("0.00")) + "\t"
                        + $"{r.MassG:0}\t{r.CostRelative:0}\t"
                        + (r.Feasible ? "✓" : (r.Unknown ? "⚠ " : "✗ ") + r.Binding));
        GridFmt.Fill(_segResult, tab.ToString());

        sb.AppendLine($"材料数据：用户实测工作簿   寿命 {_in.DesignLifeHours:0} h   安全系数 {_in.SafetyFactor:0.0}");
        sb.AppendLine($"金属价格比：Rh/Pt = 4.91（Umicore PMM 2026-08-06，Pt $1731/oz、Rh $8500/oz）");
        sb.AppendLine();
        sb.AppendLine($"合计铂重 {mass:0} g    相对成本 {cost:0}（= Σ 质量×牌号成本倍数，纯铂同质量为基准）");
        // ★ 汇总也要分清「超限」与「判不了」：两者的下一步动作是相反的。
        //   Totals 的 infeasible 把两类都算作不可行（对：判不了不算通过），
        //   但**说出来的时候必须分开**，否则又把「没有数据」说成「强度不够」，
        //   工程师照着去加厚，白费铂且治不了病。
        int unknown = rs.Count(x => x.Unknown);
        int overrun = bad - unknown;
        if (overrun > 0) sb.AppendLine($"★ {overrun} 段强度超限 —— 加厚或换牌号");
        if (unknown > 0)
        {
            sb.AppendLine($"⚠ {unknown} 段**无法判定**（不等于通过）——");
            foreach (var r in rs.Where(x => x.Unknown))
                sb.AppendLine($"    {r.Seg.Name}：{r.Binding}");
            sb.AppendLine("    下一步不是加厚：请确认该牌号蠕变数据的温度区间，");
            sb.AppendLine("    或换用覆盖该温度的牌号（如 Tanaka-ZGS-Pt 为 1000–1500 °C）。");
        }
        sb.AppendLine();
        sb.AppendLine($"★ 以上只是**管壁**。整线还有 {LineSolver.FlangeCount(_segs.Count)} 片法兰"
                    + $"（{_segs.Count} 段 → n+1 片，段间共用），厚度由电流密度定，");
        sb.AppendLine("  不随管壁减薄同比例变薄 —— 点「核算法兰」把它算进来，那才是可交付的总铂。");
        sb.AppendLine();
        sb.AppendLine("注：本页强度与质量核算是解析的（即时）。电流密度与析晶需耦合热解，");
        sb.AppendLine("    请用 --cli --matrix / --save 等批处理模式。");
        // ⚠ 这里**直接调 Write**，不靠 TextFmt.Hook 的事件（2026-08-21）。
        //   RunLine 在 MainForm.Load 里就跑一次，那时本框在 ② 页的内层页签里、
        //   句柄还没建 ⇒ `.Text =` **不触发 TextChanged**（原生控件没窗口就没有
        //   EN_CHANGE 通知），而 HandleCreated 是在更早的空文本时刻就烧掉了。
        //   结果：首屏这一框顶着一串 `**` 摆着 —— 实测截图抓到，靠人看才发现。
        //   钩子是给「散在几十处的写入」兜底的；自己这条路没必要押在事件时机上。
        //   Write 自带重入闸门，钩子在它执行期间被完整压住，两者不会打架。
        TextFmt.Write(_segOut, sb.ToString());
    }

    /// <summary>
    /// 法兰定尺：每段一次耦合解，分钟级。放后台线程 + 进度 + 可取消，
    /// 否则界面在整个过程里假死（Windows 会显示「无响应」，用户以为崩了）。
    /// </summary>
    private async Task RunFlangesAsync()
    {
        if (_flangeCts is not null) { _flangeCts.Cancel(); return; }   // 再点一次 = 取消

        _segBind.EndEdit();
        var segs = _segs.Select(s => new Segment
        {
            Name = s.Name, TSetC = s.TSetC, TGlassInC = s.TGlassInC,
            GlassHeadM = s.GlassHeadM, LengthMm = s.LengthMm,
            TubeIdMm = s.TubeIdMm, WallMm = s.WallMm,
            GradeName = s.GradeName, TLiquidusC = s.TLiquidusC
        }).ToList();
        if (segs.Count == 0) return;

        double tubeG = LineSolver.Totals(LineSolver.Solve(segs, _in)).massG;
        var plate = new FlangePlate();

        _flangeCts = new CancellationTokenSource();
        var ct = _flangeCts.Token;
        _segProg.Visible = _segStatus.Visible = true;
        _segProg.Value = 0;
        _segStatus.Text = "启动…";
        if (_flangeBtn is not null) _flangeBtn.Text = "取消";

        // Progress<T> 在构造处捕获 UI 同步上下文，回调自动回到 UI 线程
        var prog = new Progress<LineSolver.FlangeProgress>(fp =>
        {
            _segProg.Value = Math.Clamp((int)(fp.Fraction * 1000), 0, _segProg.Maximum);
            _segStatus.Text = fp.ToString();
        });

        try
        {
            var fs = await Task.Run(
                () => LineSolver.SizeFlanges(segs, _in, plate, prog, ct), ct);

            var sb = new StringBuilder(_segOut.Text);
            sb.AppendLine();
            sb.AppendLine($"=== 法兰（{segs.Count} 段 → {fs.Count} 片，段间共用）===");
            sb.AppendLine("接头\t共用\t电流 A\t厚度 mm\t铂重 g\t定尺依据");
            sb.AppendLine(TextFmt.SepRow(6));
            foreach (var f in fs)
                sb.AppendLine($"{f.Joint}\t{(f.Shared ? "是" : "—")}\t{f.CurrentA:0}\t"
                            + $"{f.ThicknessMm:0.000}\t{f.MassG:0}\t{f.SizedBy}");
            double fg = fs.Sum(x => x.MassG);
            sb.AppendLine();
            sb.AppendLine($"法兰合计 {fg:0} g   管 {tubeG:0} g   全线 {tubeG + fg:0} g   " +
                          $"法兰占 {fg / (tubeG + fg) * 100:0}%");
            sb.AppendLine("共用片电流 = (I_左+I_右)/2 × 1.5（《鉑金電氣計算.xlsx》口径）；厚度 t ∝ I。");
            // ⚠ 这里**直接调 Write**，不靠 TextFmt.Hook 的事件（2026-08-21）。
        //   RunLine 在 MainForm.Load 里就跑一次，那时本框在 ② 页的内层页签里、
        //   句柄还没建 ⇒ `.Text =` **不触发 TextChanged**（原生控件没窗口就没有
        //   EN_CHANGE 通知），而 HandleCreated 是在更早的空文本时刻就烧掉了。
        //   结果：首屏这一框顶着一串 `**` 摆着 —— 实测截图抓到，靠人看才发现。
        //   钩子是给「散在几十处的写入」兜底的；自己这条路没必要押在事件时机上。
        //   Write 自带重入闸门，钩子在它执行期间被完整压住，两者不会打架。
        TextFmt.Write(_segOut, sb.ToString());
            _segStatus.Text = "完成";
        }
        catch (OperationCanceledException) { _segStatus.Text = "已取消"; }
        catch (Exception ex)
        {
            _segStatus.Text = "失败";
            MessageBox.Show(this, ex.Message, "法兰定尺失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _flangeCts.Dispose();
            _flangeCts = null;
            _segProg.Visible = false;
            if (_flangeBtn is not null) _flangeBtn.Text = "核算法兰（分钟级）";
        }
    }

    private void AutoGrade()
    {
        _segBind.EndEdit();
        var cands = new[] { "Pt", "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "FKS16/Pt",
                            "Tanaka-ZGS-PtRh10", "FKS16/PtRh-9010" };
        foreach (var s in _segs)
        {
            var b = LineSolver.BestGrade(s, _in, cands);
            if (!string.IsNullOrEmpty(b)) s.GradeName = b;
        }
        _segBind.ResetBindings(false);
        RunLine();
    }

    private void MinWalls()
    {
        _segBind.EndEdit();
        var rs = LineSolver.Solve(_segs, _in);
        // ★ 夹的是**焊接工艺下界**，不是从前那个硬编码的 0.3（2026-08-21 修）。
        //   用户：「低于焊接工艺下界 0.6 mm —— 这是基本，能造能用后才是优化铂金减重」。
        //   旧写法拿 0.3 当底，与 WeldMinThicknessMm(0.6) 毫无关系 ⇒
        //   点一下本按钮，管壁就被设成强度解 0.477，**低于工艺下界且没有任何提示** ——
        //   为了减重把壁厚压到造不出来，正是这条原则要拦的事。
        double floor = _in.WeldMinThicknessMm;
        int clamped = 0;
        for (int i = 0; i < _segs.Count && i < rs.Count; i++)
            if (!double.IsNaN(rs[i].MinWallStrengthMm))
            {
                double want = rs[i].MinWallStrengthMm;
                if (want < floor - 1e-9) clamped++;
                _segs[i].WallMm = Math.Round(Math.Max(want, floor), 3);
            }
        if (clamped > 0)
            MessageBox.Show(
                $"{clamped} 段的强度最小壁厚低于焊接工艺下界 {floor:0.00} mm（{_in.WeldMinSource}），已顶到下界。"
                + Environment.NewLine + Environment.NewLine
                + "强度算得再薄也没用 —— 焊不出来的壁厚不是可选项。",
                "已按工艺下界夹住", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _segBind.ResetBindings(false);
        RunLine();
    }

    private static ToolStripButton Btn(string text, EventHandler h)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += h;
        return b;
    }

    private void Run()
    {
        _res = SegmentSolver.Solve(_in);
        _out.Text = _res.Ok ? Report(_in, _res) : "求解失败: " + _res.Message;
        if (!_res.Ok) return;

        // ⚠ 这里原来算了一个 xView（「视野取 5 倍热衰减长度」）却从未传给谁 ——
        //   DrawAxialProfile 自己 AutoScale。留着一个算了不用的量，
        //   下一个人会以为视野是被限制过的。
        FieldPlots.DrawAxialProfile(_pAxial, _res, _in);
    }

    private static string Report(DesignInputs p, SolveResult r)
    {
        var s = new StringBuilder();
        // H = 小节标题：**不带 \t**，于是它把上下两节断成两张表 —— 这正是要的，
        //     每节各算各的列宽，某节的长键名不会把别节的值列推歪。
        //     横线长度按显示宽度扣（中文一个字算两个字宽），别用 t.Length。
        void H(string t) { s.AppendLine(); s.AppendLine("── " + t + " " + new string('─', Math.Max(2, 58 - TextFmt.Width(t)))); }
        void L(string k, string v) => s.AppendLine($"  {k}\t{v}");

        H("铂用量  ← 目标函数");
        L("设计壁厚", $"{r.WallDesignMm:0.000} mm" +
            (r.WallLimitedByMinimum ? $"   ← 受最小壁厚限制 (电学仅需 {r.WallElecMm:0.000})"
                                    : "   ← 受电流密度限制"));
        L("供料管铂重", $"{r.MassTubeKg:0.000} kg   ({r.MassTubeKg / p.TubeLength:0.000} kg/m)");
        L("合计", $"{r.MassTotalKg:0.000} kg");
        L("比功率", $"{r.PowerDensityWPerKg:0} W/kg   (m = P / 该值)");

        H("热平衡");
        L("单位长度热损失", $"{r.LossPerMeterWPerM / 1000:0.00} kW/m");
        L("段总功率", $"{r.PowerTotalW / 1000:0.00} kW");
        L("保温外径 / 外表温度", $"Ø{r.InsulationOuterDiaMm:0.0} mm  /  {r.OuterSurfaceTempC:0} °C");
        L("热衰减长度 ℓt", $"{r.DecayLengthMm:0.0} mm");
        L("时间常数 金属/含玻璃", $"{r.TauMetalS:0} s  /  {r.TauWithGlassS / 60:0.0} min");

        H("电气");
        L("电流 I", $"{r.CurrentA:0} A");
        L("段电压 U", $"{r.VoltageV:0.00} V");
        L("段电阻 R", $"{r.ResistanceOhm * 1000:0.000} mΩ");
        L("实际电流密度 J", $"{r.JActualAPerMm2:0.00} A/mm²   (许用 {p.JAllowAPerMm2:0.0}，余量 {r.JMarginPct:0.0} %)");
        L("TCR @T_set", $"{r.TcrPerK * 1e4:0.00}e-4 /K");
        L("热稳定裕度", $"{r.StabilityRatio:0.0} ×   " + (r.StabilityRatio > 3 ? "稳定" : "⚠ 裕度不足"));

        H("温度分布 / 析晶");
        L("中段 (控温点)", $"{p.TSetC:0.0} °C");
        L("最高 / 最低", $"{r.TMaxC:0.0} / {r.TMinC:0.0} °C");
        L("法兰 A / B 端", $"{r.TFlangeAC:0.0} / {r.TFlangeBC:0.0} °C");
        L("玻璃出口", $"{r.TGlassOutC:0.0} °C");
        L("最小析晶裕度", $"{r.DevitMarginMinK:+0.0;-0.0} K   (要求 ≥ {p.DevitMarginK:0} K)");
        s.AppendLine(r.DevitRisk
            ? "  ★ 析晶风险：最冷点低于 T_liq + 裕度，位置在法兰"
            : "  ✓ 全程高于析晶安全线");

        // ★ 2026-08-12：原「法兰自给率 Φ」一段走的是已作废的一维环形模型（§5），
        //   连同 FlangeRadial 一并删除。法兰的 Φ / 抽热 / 局部最高温请看「整线设计」页，
        //   那里是二维壳解的真值。

        H("流动");
        L("流速 / 停留时间", $"{r.VelocityMmPerS:0.0} mm/s  /  {r.ResidenceS:0} s");
        L("压降", $"{r.PressureDropKPa:0.0} kPa  = {r.GlassHeadM:0.00} m 玻璃柱");

        if (r.ILeakA > 1e-9)
        {
            H("⚠ 直流分量电解（法拉第上限估计）");
            L("玻璃柱电阻", $"{r.RGlassOhm:0.0} Ω");
            L("漏电流(直流)", $"{r.ILeakA * 1000:0.00} mA");
            L("铂阳极溶解", $"{r.PtDissolvedGPerH:0.000} g/h  = {r.PtDissolvedKgPerYear:0.000} kg/年");
            L("Na⁺ 迁移", $"{r.NaFluxGPerH:0.000} g/h  → 阳极区贫碱，T_liq 局部升高");
            L("阳极析氧", $"{r.O2NlPerH:0.000} NL/h");
            s.AppendLine("  注：忽略界面极化阻抗，为上限。交流下应为 0。");
        }
        else if (p.Supply != SupplyMode.Dc)
        {
            H("电解");
            s.AppendLine("  交流供电，无净法拉第迁移。");
            s.AppendLine("  建议用钳表直流档实测二次电流直流分量，填入「直流偏置」核算。");
        }

        return s.ToString();
    }

    private void Sweep(string what, double from, double to, int steps, string label)
    {
        var rows = SegmentSolver.Sweep(_in, what, from, to, steps);
        var s = new StringBuilder();
        s.AppendLine($"扫描：{label}");
        s.AppendLine();
        // ⚠ 原来这里还有一列「Φ」。SegmentSolver.Sweep **从来没给 Phi 赋过值** ⇒
        //   那一列恒为 0.00。一个永远打 0 的列比没有这一列更坏：它看起来像个测出来的数，
        //   而「法兰自给率 = 0」恰好又是个说得通的读数。已随字段一起删。
        s.AppendLine($"{label}\t损失 kW/m\t壁厚 mm\t总铂 kg\t最冷 °C\t裕度 K\tI (A)");
        s.AppendLine(TextFmt.SepRow(7));
        foreach (var x in rows)
        {
            s.AppendLine($"{x.Value:0.00}\t{x.LossPerM / 1000:0.00}\t{x.WallMm:0.000}\t" +
                         $"{x.MassKg:0.000}\t{x.TMin:0.0}\t{x.Margin:0.0}\t{x.IA:0}");
        }
        if (rows.Count > 1)
        {
            double m0 = rows[0].MassKg, m1 = rows[^1].MassKg;
            s.AppendLine();
            s.AppendLine($"铂用量: {m0:0.000} → {m1:0.000} kg   ({(m1 - m0) / m0 * 100:+0.0;-0.0} %)");
        }
        _out.Text = s.ToString();
    }

    private void Save()
    {
        using var d = new SaveFileDialog { Filter = "Pt_Optimize 方案 (*.json)|*.json", FileName = "case.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(d.FileName,
            JsonSerializer.Serialize(_in, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadCase()
    {
        using var d = new OpenFileDialog { Filter = "Pt_Optimize 方案 (*.json)|*.json" };
        if (d.ShowDialog() != DialogResult.OK) return;
        var x = JsonSerializer.Deserialize<DesignInputs>(File.ReadAllText(d.FileName));
        if (x is null) return;

        // ★★ 2026-08-20：**就地覆盖，不换引用**（原来是 `_in = x`）。
        //   「整线设计」「分析」两页在构造时拿到的是 `_in` 的引用且是 readonly，
        //   换引用只换得掉参数表这一处 —— 那两页会继续拿旧方案算，且毫无提示。
        //   详见 SegmentSolver.CopyInto 的注释。
        SegmentSolver.CopyInto(x, _in);
        _grid.SelectedObject = _in;   // 同一个实例，但要让 PropertyGrid 重读一遍
        _grid.Refresh();

        // 换了方案 ⇒ 上一次的解与判据**全部作废**。不清掉的话，
        // 判据表会挂着旧方案的结论，而参数表已经是新方案了。
        _linePage?.InvalidateSolution();

        Run();
        RunLine();   // 原来漏了这一句 ⇒ 「分段核算」页也留着旧方案的结果
    }

    private void ExportCsv()
    {
        if (_res is null || !_res.Ok) return;
        using var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "profile.csv" };
        if (d.ShowDialog() != DialogResult.OK) return;
        var s = new StringBuilder("x_mm,T_metal_C,T_glass_C\n");
        for (int i = 0; i < _res.X.Length; i++)
            s.AppendLine($"{_res.X[i]:0.###},{_res.TMetal[i]:0.###},{_res.TGlass[i]:0.###}");
        File.WriteAllText(d.FileName, s.ToString(), Encoding.UTF8);
    }
}
