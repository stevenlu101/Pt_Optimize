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
/// <see cref="Sizer"/>，出图走 <see cref="Geometry3dm.WriteFinal3dm"/>（解析形状）
/// 与 <see cref="Geometry3dm.ScalePlate3dm"/>（.3dm 形状），与 CLI 同源。
///
/// ⚠ 这一句 2026-08-23 前写的是「出图走 Geom<c>etry3dm.WritePlate3dm</c>」——
///   而那个入口早在 1b（2026-08-17）就被换掉了，理由见 <see cref="Export"/>：
///   它只往渲染子进程传五个数，表达不了渐变环 / 舌根圆角 / 角焊缝 / 等宽舌。
///   换掉之后没人回来改这句，于是**类头拿一个已经不存在的调用当「两边不可能不同」的保证**。
///   死方法本身无害，靠它作保的那句话才是问题。（该方法已随本次清理删除。）
///
/// 界面上只放**当前判据体系用得到的**输入（HANDOVER §0.0 的四个自由度 + 边界条件）；
/// 已作废的一维法兰模型那套参数（盘内外半径、剖面形状、梯形厚度…）已随模型删除。
/// </summary>
public sealed class LineDesignPage : TabPage
{
    // ★ 默认取**当前定案档的壁厚 0.80**，不再是 0.40（2026-08-21）。
    //   用户：「低于焊接工艺下界 0.6 mm —— 这是基本，**能造能用后才是优化铂金减重**」。
    //   0.40 低于工艺下界 0.6 ⇒ 开箱那一刻界面上摆的就是一个**焊不出来的构型**；
    //   第一次用的人直接点「核算整线」，会拿它跑几十秒，解发散到 5000 °C 以上、
    //   判据大面积不过 —— 看起来像程序坏了，其实是默认值本身不可制造。
    //   （--walk 全程验证抓到。下限仍保留 0.10：允许探索，但判据与夹持会拦住。）
    private readonly NumericUpDown _wall = Num(0.80m, 0.10m, 5.00m, 0.05m, 2);
    private readonly NumericUpDown _tubeIns = Num(10.0m, 0.0m, 100.0m, 0.5m, 1);
    private readonly NumericUpDown _clamp = Num(300m, -1m, 1200m, 10m, 0);
    /// <summary>
    /// `.3dm` 模式下的舌保温 mm。解析模式不用它（那边逐片来自 FinalDesign.TabInsulMm）。
    /// 0 = 裸舌 —— 那是此前 .3dm 路径**写死**的行为。
    /// </summary>
    private readonly NumericUpDown _tabIns3dm = Num(0.0m, 0.0m, 5.0m, 0.05m, 2);
    private readonly ComboBox _flIns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.S(110) };
    private readonly NumericUpDown _flInsT = Num(20.0m, 0.0m, 60.0m, 0.5m, 1);
    private readonly NumericUpDown _discD = Num(60m, 30m, 300m, 2m, 0);
    private readonly NumericUpDown _tabLen = Num(50m, 20m, 400m, 5m, 0);
    private readonly NumericUpDown _tabW = Num(20m, 5m, 150m, 1m, 0);
    private readonly NumericUpDown[] _tPlate =
    {
        Num(0.516m, 0.10m, 8.0m, 0.02m, 3), Num(0.855m, 0.10m, 8.0m, 0.02m, 3),
        Num(0.776m, 0.10m, 8.0m, 0.02m, 3), Num(0.426m, 0.10m, 8.0m, 0.02m, 3),
    };
    // ⚠ 文字要短到**放得下**（2026-08-20 实测截图里这两行断在半个词上：
    //   「解析形状（圆盘 + 梯形舌片，程」「Rhino .3dm 文件（任意形状：阶」）。
    //   它们已经是 AutoSize + 跨两列了 —— 截断的原因是文字本身比左栏还宽，
    //   靠布局救不回来。⇒ 可见文字缩短，完整说明挪进 ToolTip（见构造函数）。
    //   断在半个词上的标签比短标签更糟：它看着像程序出错了。
    private readonly RadioButton _srcAnalytic = new()
    { Text = "解析形状（程序生成）", AutoSize = true };
    private readonly RadioButton _src3dm = new()
    { Text = "Rhino .3dm 文件", AutoSize = true };
    private readonly TextBox[] _file3dm = { new(), new(), new(), new() };
    private readonly Control[] _row3dm = new Control[4];
    private readonly TextBox _layer3dm = new() { Text = "法兰", Width = UiScale.S(96) };
    private readonly DataGridView _segGrid = new();
    private readonly RichTextBox _out = new();
    private readonly ToolStripProgressBar _prog = new() { Visible = false, Maximum = 1000 };
    private readonly ToolStripLabel _status = new("");
    private readonly ToolStripButton _btnRun, _btnAuto, _btnExport, _btnLoadCase, _btn3dm, _btnRepro;
    /// <summary>「分析几何变数」——只在 .3dm 模式且入口片已选时可用，由 SyncGeomSource 控。</summary>
    private readonly ToolStripButton _btnAnalyze;
    /// <summary>「另存为定案档」—— 把当前的解写成 finaldesigns/*.fd.json。</summary>
    private readonly ToolStripButton _btnSaveFinal;
    private readonly ToolStripButton _btnShape;

    // ★★★★★ 改参数**自动**给答案（2026-08-16 用户：「UI 已经够复杂，不要再加按钮」）
    //
    // 做法不是加控件，而是**去掉「要记得按核算」这件事**：
    //   参数一动 → 立刻用闭式给出解析量与**外推预测**（毫秒级）→ 后台自动排队真解。
    // 「核算整线」按钮保留，作为手动重来的入口，但正常用法下不必碰它。
    //
    // ⚠ 三层必须**各自标明自己是什么**：解析（精确）／预测（未解）／已解。
    //   本项目最贵的两次错都是「数字看着正常」造成的 —— 预测值绝不能长得像解出来的。
    private readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 1500 };
    private bool _autoArmed;                 // 参数动过、还没解
    private bool _suppressAuto;              // 程序化写控件时暂闭（载入定案等）
    /// <summary>
    /// 首屏排版结束、可以把控件事件当「用户改参数」看了。
    /// 在此之前的 ValueChanged/CellValueChanged 都是**框架在排版**，不是人在改。
    /// </summary>
    private bool _userReady;

    /// <summary>
    /// 另一个模式下那组值。<see cref="_tPlate"/> 在解析模式是**板厚 mm**、
    /// 在 .3dm 模式是**厚度标度 k** —— 两个物理量共用一组控件，
    /// 所以切模式时要整组换出去，不能让一个量的数字被当成另一个量。
    /// 初值 1.0 = .3dm 模式的「按图纸原尺寸」。
    /// </summary>
    private readonly decimal[] _keepOther = { 1.0m, 1.0m, 1.0m, 1.0m };
    /// <summary>上一次 SyncGeomSource 看到的模式，用来判「是不是刚切过来」。</summary>
    private bool _lastAnalytic = true;
    private Snap? _solvedSnap;               // 上一次**真解**时的参数
    private LineResult? _solvedRes;

    /// <summary>
    /// 参与外推的那几个参数（有实测雅可比的才放进来），**外加形状**。
    ///
    /// ★ 形状是 2026-08-17 补的，起因是一个真空档：原来 `tooFar` 只管
    ///   「离上次已解的点走了多远」，**从不问「现在这个形状是不是雅可比测过的那个形状」**。
    ///   而雅可比那几个常数自己的注释就写着「只对这个工作点附近成立，
    ///   ②″ 由两个竞争峰决定，**符号会随构型变**（已经栽过一次：拿另一构型的符号外推，判反了）」。
    ///   ⇒ 换了盘径/舌长/舌宽之后再解一次，然后微调板厚，预测照样显示 ——
    ///     而它用的是**另一个构型**的斜率。这正是那条注释警告过的事，只是没人拦。
    /// </summary>
    /// ★★★★★ **必须是 record（值相等），不能是 class**（2026-08-21 --walk 抓到）。
    ///
    /// <see cref="FlowState.Fresh"/> 判「结果新不新鲜」用的是
    /// `Equals(SolvedSnap, CurrentSnap)`。这两个字段是 `object?`，
    /// 若 Snap 是普通 class 且不重写 Equals ⇒ 走的是**引用相等**；
    /// 而 <see cref="PushFlow"/> 每次都 `CurrentSnap()` 新建一个实例 ⇒
    /// **两者永远不是同一个对象 ⇒ Fresh 恒为 false**。
    ///
    /// 后果不是「偶尔多解一次」，是**整条流程被堵死**：
    /// ④⑤ 两道门都带 RequireFresh ⇒ 无论解得多好、判据全过，
    /// 它们**永远不解锁**，而且给出的理由是「参数在上次求解之后又动过了」——
    /// 一句**假话**。用户会回去反复重解，每次几十秒，永远等不到门开。
    ///
    /// 「假红灯」和本项目一直在防的「假绿灯」是同一种病：
    /// 界面说的话与实际状态对不上，而它说得像真的。
    private sealed record Snap
    {
        public double Wall, Plate, TubeIns;
        public double Disc, TabLen, TabW;
    }

    private Snap CurrentSnap() => new()
    {
        Wall = (double)_wall.Value,
        Plate = _tPlate.Average(n => (double)n.Value),
        TubeIns = (double)_tubeIns.Value,
        Disc = (double)_discD.Value,
        TabLen = (double)_tabLen.Value,
        TabW = (double)_tabW.Value
    };
    private readonly ToolStripComboBox _caseBox =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.S(210) };
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
    /// <summary>
    /// 「分析几何变数」反推出来的形状。留着是为了让 ⑤⑥ 在 .3dm 模式下也判得了 ——
    /// 见 <see cref="LineCase.GeomForJudge"/>。没分析过时为 null ⇒ ⑤⑥ 仍报「无法判定」，
    /// 那是**诚实的**：还没告诉过程序这张图长什么样。
    /// </summary>
    private PlateShapeAnalyzer.Shape? _shape;

    /// <summary>
    /// 左侧那块**输入面**（参数框、几何来源单选、段表、各级锁定…）。
    /// 有链在跑时整块禁掉 —— 见 <see cref="SetInputsEnabled"/>。
    /// 工具条（命令，含「取消」）与右侧输出**不在这块里**，所以冻住不会把人困死。
    /// </summary>
    private TableLayoutPanel? _inputPanel;

    /// <summary>
    /// 「分析几何变数」时量出来的**解析替身保真度**。null = 还没分析过。
    ///
    /// 替身是给 ④ 提速用的：逐级定厚在 .3dm 上每次评估都要起 Geom 子进程重算厚度场，
    /// 换成解析形状就是毫秒级。但只有量出来足够像才准用 —— 详见
    /// <see cref="AnalyticSurrogate"/>。这个数**要显示给工程师**，
    /// 因为「④ 为什么这么慢」的答案就在里面。
    /// </summary>
    private AnalyticSurrogate.Fidelity? _surrFid;
    /// <summary>替身选的是等宽舌还是梯形舌（由量出来的残差决定，不是猜）</summary>
    private bool _surrTabParallel = true;
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

        // 自动重算：任何输入一动就 (a) 立刻给预测 (b) 重排防抖定时器
        _autoTimer.Tick += (_, _) => { _autoTimer.Stop(); TryAutoRun(); };

        // ⚠ ToolStrip **不继承父窗体的字体**（它用 ToolStripManager 的默认字体）。
        //   所以在 Form 上设 Font 对工具条一点用都没有 —— 用户 2026-08-18 反馈
        //   「下排的字还是太小」，指的就是这一排。必须逐个显式设。
        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = UiScale.Ui() };
        _btnRun = Btn("核算整线", (_, _) => _ = RunAsync(false));
        _btnAuto = Btn("自动定厚", (_, _) => _ = RunAsync(true));
        // 1b 之后它导出的是**整机**（管 + 四片法兰）且几何与求解一致，故改名点明
        _btnExport = Btn("导出本页 3DM", (_, _) => Export());

        // ★ 定案档：直接从 Core/FinalDesign 取，**不在 UI 里再抄一份数**。
        //   两档都全判据通过，差别只在裕度与铂重（见各档的 Binding 说明）。
        RefillCaseBox();
        FinalDesign.Reloaded += OnFinalDesignsReloaded;
        _btnLoadCase = Btn("载入定案", (_, _) => LoadFinalDesign());
        _btn3dm = Btn("导出定案 3DM", (_, _) => ExportFinal3dm());
        _btnAnalyze = Btn("分析几何变数", (_, _) => AnalyzeShape());
        _btnSaveFinal = Btn("另存为定案档", (_, _) => SaveAsFinalDesign());

        // ★★★ 复现定案：**界面上唯一能跑出定案数字的按钮**（2026-08-16 用户提出）。
        //
        // 在此之前界面根本没有这条路：本页控件表达不了「管孔两级渐变环」与
        // 「逐片舌保温」，所以「载入定案 → 核算整线」跑的是一个**缺两项的构型**，
        // 数字对不上，而它照样出一张漂亮的判据表 —— §1.8「安静失败」的形状。
        // ⇒ 本按钮**完全绕过页面控件**，直接用 FinalDesign.BuildCase 造算例。
        _btnRepro = Btn("▶ 复现定案", (_, _) => _ = ReproduceAsync());
        // ⚠ 不能写 `new Font(_btnRepro.Font, Bold)` —— 那会**在这一刻捕获**按钮当时的字体
        //   （默认 9 pt），从此这个按钮就不再跟着工具条的字体走了。
        //   用户 2026-08-18 截图里「▶ 复现定案」比邻居明显小一号，就是这么来的。
        _btnRepro.Font = UiScale.Ui(FontStyle.Bold);

        // ★★★★★ 搜形状（2026-08-17，用户指出「跑得久」该用**进度条**解决，不是把功能挡在 CLI 外）。
        //
        // 我原来的理由是「一次形状网格几十分钟，挂在按钮上会变成点一下没反应半小时」——
        // 那是把**没有进度显示**当成了**功能不能进 UI**。进度管线（_prog/_status）本来就在，
        // Sizer 也早就吐 IProgress<string>，接上即可。
        //
        // ⚠ 这是本轮**唯一**新增的控件，与「UI 已经够复杂不要再加」是有冲突的，所以说明理由：
        //   功能需要一个触发点；藏成快捷键或修饰键（Shift+自动定厚）会直接违反
        //   「不看说明书也能用」。⇒ 宁可多一个**名字说得清**的按钮。
        _btnShape = Btn("◇ 搜形状", (_, _) => _ = SearchShapeAsync());

        // ★★ 2026-08-20 阶段轨：本页只留「③ 整线核算」这一格的命令。
        //
        //   搬走的四个（自动定厚 / ◇ 搜形状 → ④；导出本页 3DM / 导出定案 3DM → ⑤）
        //   **仍然由本页创建和持有** —— 只是挂到了 ④⑤ 页的工具条上（见
        //   BtnAutoThick 等几个属性）。
        //
        //   为什么这样而不是「④ 页新建按钮再回调本页方法」：那些按钮身上挂着
        //   一整套运行时状态（跑起来变「取消」、互相禁用、finally 里恢复，见 RunAsync）。
        //   重建一套按钮就等于把那套状态**抄第二份**，而两份状态迟早会漂开。
        //   ToolStripItem 本来就能挂到任何一条工具条上，让它换个位置最省事、也最不会错。
        // ★ 定案档那一组（下拉 / 复现 / 载入 / 另存 / 导出定案 3DM）**已搬到独立的
        //   「定案档」页**（2026-08-23）。它们不读页面控件、不受阶段门禁，
        //   与「你手上这个设计走到哪一步」是正交的两根轴，混在同一条工具条上正是
        //   用户最初抱怨的「不知道自己在算什么」。控件仍归本页所有（载入要灌本页控件、
        //   另存要读本页的解），只是**摆在别处** —— 见 MainForm 装配。
        //
        // 本页现在只剩「关于当前这个设计」的两个命令。
        tool.Items.Add(_btnRun);
        tool.Items.Add(_btnAnalyze);
        tool.Items.Add(new ToolStripSeparator());
        _prog.Size = new Size(UiScale.S(160), UiScale.S(16));
        tool.Items.Add(_prog);
        tool.Items.Add(_status);

        // ── 输入面板
        var input = _inputPanel = new TableLayoutPanel
        { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(6) };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.S(188)));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Head(string s)
        {
            var l = new Label
            {
                Text = s, AutoSize = true, Margin = new Padding(0, 10, 0, 4),
                Font = UiScale.Ui(FontStyle.Bold),
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
        // ⚠ 斜率一律**插值自 dDip_*／dJ_* 那组常数**，不再手抄一遍（2026-08-20）。
        //   手抄的那版已经漂开过：常数早在 2026-08-17 换成新定案点的实测值，
        //   而这三条提示还停在旧构型的 ③ +123／−221／+22.7 上 ——
        //   **同一个数存两处，迟早对不上账**（§7 头一条）。插值之后它不可能再漂。
        Row("壁厚 mm", _wall,
            "工艺下界 0.6 mm = **手工 TIG 烧穿下界**（自动 TIG 0.3、激光 0.1，差一个量级）。\n" +
            "另一条独立的界是管 J ≤ 12 A/mm²（现场给定：一般上限 15，壁 0.6 时 12 是极限）。\n" +
            "定案两档正是被这两条同点咬住（0.6）与全都留有余量（0.8）。\n" +
            $"实测斜率（--vary）：③ {dDip_dWall:+0.0;−0.0} K/mm　②″ +16.7 K/mm" +
            $"　管J {dJ_dWall:+0.00;−0.00}　管重 +3051 g/mm\n" +
            "⚠ ②″ 那条只在**这个工作点附近**成立：②″ 由两个竞争峰决定，符号会随构型翻。");
        Row("纤维保温 mm", _tubeIns,
            "无空间限制、不花铂 —— 但**不是免费的**：\n" +
            $"  ③ {dDip_dTubeIns:+0.0;−0.0} K/mm　②″ −3.1 K/mm" +
            $"　管J {dJ_dTubeIns:+0.00;−0.00} (A/mm²)/mm　（实测 --vary）\n" +
            "机理：保温厚 ⇒ 管散热少 ⇒ 电流小（利），但 β 变小而 ③=D/√(kAβ) 里 β 在分母（不利）。\n" +
            "现用的 5 mm 恰在拐点上 —— 这个值原本是没量过的默认值，碰巧是对的。");

        Head("法兰几何来源");
        // ⚠ 提示文字用 Environment.NewLine 拼，不写反斜杠转义 ——
        //   本仓有个钩子会把转义序列改成真字符，那会让字符串字面量当场断掉。
        var tipSrc = new ToolTip();
        tipSrc.SetToolTip(_srcAnalytic,
            "圆盘 + 梯形舌片，由程序按盘径/舌长/舌端半宽生成。" + Environment.NewLine
            + "只有这个模式能用「◇ 搜形状」—— 形状是可搜索的自由度。");
        tipSrc.SetToolTip(_src3dm,
            "任意形状：阶梯厚度、开槽、异形轮廓，从 Rhino .3dm 读厚度场。" + Environment.NewLine
            + "形状由图纸给定 ⇒ 判据 ⑤⑥ 拿不到解析量，会报「无法判定」（不等于通过）。");
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

            // ⚠ 这一行**必须按列宽自适应**，不能用「固定宽文本框 + 固定宽按钮」硬拼
            //   （2026-08-21 用户报「3DM 输入入口不见了」，实测就是这么丢的）：
            //
            //   旧写法是 FlowLayoutPanel{WrapContents=false} 里塞 S(150) 文本框 + S(30) 按钮。
            //   在 K=1.6 的屏上那是 240+48≈300 px，而本表第二列只有
            //   （中间栏宽 − 第一列 S(188)）≈ 220 px ⇒ **「…」按钮整个被父容器裁掉**。
            //   父表是 TableLayoutPanel，第二列是 Percent 100 ——
            //   单元格不会为超宽内容变宽，也**不会给出横向滚动条** ⇒ 按钮永远够不到，
            //   而选文件只有这一个入口 ⇒ .3dm 模式**整条链无法使用**。
            //   文本框是只读的，看起来一切正常 —— 又一次「安静地不可用」。
            //
            //   改成两列表格：文本框 Percent 100 + Dock Fill（栏窄它就窄），
            //   按钮 AutoSize（永远画得下）。这样任何缩放、任何分栏宽度都不会再丢入口。
            var pnl = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, AutoSize = true,
                Dock = DockStyle.Fill, Margin = new Padding(0),
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _file3dm[idx].ReadOnly = true;
            _file3dm[idx].Dock = DockStyle.Fill;
            _file3dm[idx].Margin = new Padding(0, 2, 2, 2);
            // 路径通常比栏宽长得多：鼠标停上去看全名，否则只看得见开头几个字符
            new ToolTip().SetToolTip(_file3dm[idx], "点右边「…」选 .3dm 文件");

            var b = new Button
            {
                Text = "…", AutoSize = true, Margin = new Padding(0, 2, 0, 2),
                MinimumSize = new Size(UiScale.S(28), UiScale.S(22)),
            };
            b.Click += (_, _) => PickFile(idx);

            pnl.Controls.Add(_file3dm[idx], 0, 0);
            pnl.Controls.Add(b, 1, 0);
            _row3dm[idx] = pnl;
            Row(names[idx] + " .3dm", pnl);
        }
        Row("舌保温 mm（.3dm）", _tabIns3dm,
            "舌片自己的保温厚度。**0 = 裸舌**，那是本路径此前写死的行为。"
            + Environment.NewLine
            + "它是守 ②′/③ 的主力旋钮：实测在定案几何上，0.4 mm ⇒ ③ = 5.2 K ✓，"
            + "而 0（裸舌）⇒ 法兰 2986 °C、往管里灌 256 W。"
            + Environment.NewLine
            + "舌片裸露占端片散热的 90 % 以上 —— 一裸就净抽热、一全包又净倒灌，中间有零点。");
        Row("图层名", _layer3dm, "厚度场从该图层提取。t=0 表示无材料 ⇒ 开槽、孔、轮廓一次拿全");
        Row("锁定的级", _lockPanel,
            "勾上的级厚度锁死，优化器只调其余级。典型用法：外圈勾上 = 外圈不动、只调内圈。" +
            "先点「分析几何变数」才会列出各级。");

        Head("法兰形状（解析模式；四片同形状，厚度各自独立）");
        Row("圆盘直径 mm", _discD,
            "缩小它是本问题里少有的「三者同向」：省铂 + 放松焊接下界 + 改善端片热平衡。\n" +
            "已缩到 Ø60。⚠ 再缩会让两级渐变环占满整个圆盘（环外半径已越过盘缘）。");
        Row("舌片长度 mm", _tabLen, "省铂宜短；但舌片越长形状数 Ψ 越小、局部越不易过热");
        Row("舌端半宽 mm", _tabW);

        Head("法兰厚度 mm / 厚度标度（可点「自动定厚」求解）");
        string tipPlate =
            "**最强的旋钮**，实测（--vary，端点均已收敛）：\n" +
            $"  ③ {dDip_dPlate:+0.0;−0.0} K/mm　②″ −14.6 K/mm　法兰重 +264 g/mm\n" +
            "⚠ ③ 是**正号** —— 加厚会把 ③ 推向限值。「哪里热就加厚哪里」在这里是反的：\n" +
            "  加厚同时降单位面积发热（∝1/t）与增强横向导热（∝t），后者把热从管根抽走。\n" +
            "共用片承 √3 倍电流、发热 3 倍 ⇒ 必须比端片厚，四片等厚不是最优。";
        for (int i = 0; i < 4; i++) Row(names[i], _tPlate[i], tipPlate);

        Head("保温与夹持");
        Row("法兰保温", _flIns, "包纤维会降低自给所需厚度；不包则法兰更凉但从管子抽热更多");
        Row("法兰保温厚 mm", _flInsT);
        Row("铜排夹持 °C", _clamp,
            "空冷即可，<0 = 无夹冷。★ 现场把自给率整定到位的唯一旋钮。\n" +
            "⚠ 压接段被铜排短接 ⇒ **那一段不发热**：舌片有效发热长度 = 舌长 − 压接长。\n" +
            "  90 mm 舌片扣掉 40 mm 只剩 50 mm —— 想靠缩短舌片省铂会先把发热段砍没。");

        Head("分段控温点");
        _segGrid.Dock = DockStyle.Top;
        _segGrid.Height = UiScale.S(110);
        _segGrid.AutoGenerateColumns = true;
        _segGrid.AllowUserToAddRows = true;
        _segGrid.DataSource = _segs;
        _segGrid.DataError += (_, e) => e.ThrowException = false;
        input.Controls.Add(_segGrid);
        input.SetColumnSpan(_segGrid, 2);

        // ── 输出
        _out.Dock = DockStyle.Fill;
        _out.Font = UiScale.Mono();

        // 排版一次挂钩、覆盖所有写入路径 —— 理由见 TextFmt.Hook 的说明。
        //
        // ⚠ 挂钩之后本页**只用** `_out.Text = …` / `+=` / `AppendText` 写输出，
        //   不再单独调 TextFmt.Write。理由**不是**「直接调会与钩子打架」——
        //   Write 自带闸门（TextFmt._writing 在进 WriteCore 之前就置上了），
        //   它写的过程中钩子不会插进来。真正的理由是：本页写输出的地方有几十处，
        //   只要留着两种写法，早晚会有几处漏掉的混在排好的内容中间 —— 那比全都不排版更难查。
        TextFmt.Hook(_out);
        _out.ReadOnly = true; _out.WordWrap = false;
        _out.BackColor = Color.FromArgb(252, 252, 250);

        foreach (var (t, c) in new (string, Control)[]
        { ("法兰温度场", _pT), ("法兰电流密度场", _pJ), ("管轴向剖面", _pAx) })
        {
            var pg = new TabPage(t) { Padding = new Padding(2) };
            pg.Controls.Add(c);
            _plots.TabPages.Add(pg);
        }

        // 判据表（Excel 式）在上、散文说明在下 —— 表归表、话归话
        InitChecksGrid();
        var textSplit = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        textSplit.Panel1.Controls.Add(_checks);
        textSplit.Panel2.Controls.Add(_out);

        var rightSplit = new SplitContainer
        { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        rightSplit.Panel1.Controls.Add(textSplit);
        rightSplit.Panel2.Controls.Add(_plots);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(input);
        main.Panel2.Controls.Add(rightSplit);

        Controls.Add(main);
        Controls.Add(tool);

        // 首屏三张图先摆空态 —— 开箱看到的不该是三个 −10…10 的空坐标轴
        FieldPlots.DrawEmpty(_pT, "还没有结果 —— 点「核算整线」");
        FieldPlots.DrawEmpty(_pJ, "还没有结果 —— 点「核算整线」");
        FieldPlots.DrawEmpty(_pAx, "还没有结果 —— 点「核算整线」");
        HookAutoRun();
        // 排版完成之后才开始把控件事件当用户操作。用 BeginInvoke 排到消息队列尾部：
        // 那时首屏的绑定与排版都已经跑完，之后的事件才真是人点出来的。
        HandleCreated += (_, _) => BeginInvoke(new Action(() => _userReady = true));

        // ★★★★★ 开箱那一刻的默认几何**必须是造得出来的**（2026-08-24 用户实测抓到）。
        //
        // 出的事：用户什么都没改，直接点「核算整线」，得到
        //   ⑤ 舌片自由段 −12.4 / 100　③ 法兰增量温降 599.5 / 10
        // 一算就对上了：默认 盘Ø60(R30)／半宽20／舌长50／压接40 ⇒
        //   自由段 = 50 − 40 − √(30²−20²) = 50 − 40 − 22.36 = **−12.36**
        // 也就是压接块伸进圆盘里 —— 这个构型根本装不上铜排，
        // 而 ③=599.5 只是这个退化几何的下游噪声，不是热学结论。
        //
        // 为什么顶高逻辑没救它：EnforceTabLenFloor 挂在 ShowPrediction 上，
        // 而 ShowPrediction 只由 ParamChanged 调，ParamChanged 又被**首屏闸门**
        // （_userReady=false，防止排版事件被当成用户操作）挡住 ⇒ 启动时一次都没顶过。
        // ⇒ 在这里顶一次：默认值从此自洽，而且用的是**同一个** TabLenFloorMm，
        //   将来盘径/半宽/压接段的默认值改了，舌长会自己跟上，不必再记得改第二处。
        string bootFloor = EnforceTabLenFloor();

        _out.Text =
            (bootFloor.Length > 0
             ? "★ 开箱默认的舌长装不下铜排，已按装配下界顶高：" + Environment.NewLine
               + bootFloor
               + "  （舌长不是自由旋钮：它 = 圆盘切点 + 压接段 + 自由段。"
               + "想要更短的舌片要改盘径或铜排尺寸。）" + Environment.NewLine + Environment.NewLine
             : "") +
            "改任何一个参数，**会自动重算**（停手约 1.5 秒后开始，分钟级，随时可取消）。\r\n" +
            "改的当下会先给两样东西：解析量（精确）与线性外推的预测值（标「预测」），\r\n" +
            "真解跑完再覆盖它们。\r\n\r\n" +
            "想直接看定案档：工具条上选「定案档 ▾」再点「▶ 复现定案」。\r\n" +
            "按 F1 有图文说明书。";
        SyncGeomSource();
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            main.SplitterDistance = Math.Min(UiScale.S(330), Math.Max(UiScale.S(240), main.Width / 3));
            rightSplit.SplitterDistance = (int)(rightSplit.Height * 0.66);
            textSplit.SplitterDistance = (int)(textSplit.Height * 0.46);   // 判据表 : 说明 ≈ 46:54
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

        // ★ 「分析几何变数」只在 **.3dm 模式且入口片已选** 时可用（2026-08-21 用户提出）。
        //   本页早就在按模式禁用文件行与盘径/舌长/舌宽了，**唯独漏了这个按钮** ——
        //   于是它在解析模式下照样可点，点了只是弹一句「请先切到…」。
        //   「能点但点了没用」正是用户最初那句抱怨的形状：
        //   「所有标签键都可以点，工程师根本不知道自己目前在算什么」。
        //   ⚠ 禁用必须**同时说明为什么**，否则灰掉的按钮就是个哑谜 —— 用 ToolTip 讲。
        bool hasEntry = !string.IsNullOrWhiteSpace(_file3dm[0].Text);
        // ⚠ **这里不设 Enabled**（2026-08-22 用户指出它没生效）。
        //   原来写 `_btnAnalyze.Enabled = !an && hasEntry;`，但 MainForm.SyncGates 也在设
        //   同一个按钮（geom.analyze 是 ReadsPageControls=true ⇒ `b.Enabled = g.Unlocked`），
        //   而 SyncGates 在切页签/状态变化时**后跑** ⇒ 它把这里的禁用又打开了。
        //   同一个按钮两处控制 —— 正是本项目反复栽的那一类。
        //   ⇒ 本页只回答「适不适用」（CommandApplicable），Enabled 只由 SyncGates 一处设。
        _btnAnalyze.ToolTipText = an
            ? "只在「Rhino .3dm 文件」模式下可用 —— 解析形状是程序生成的，没有图纸需要反推。"
            : hasEntry
                ? "读入口片 .3dm，反推出各级台阶厚度，「自动定厚」才能逐级优化。"
                : "请先选好**入口 .3dm** —— 要反推的就是那张图。";
        // ★★★ 同一组控件在两个模式下**是两个物理量**：
        //     解析模式 = 板厚 mm；.3dm 模式 = 厚度**标度 k**（无量纲，图纸整体 ×k）。
        //
        // ⚠ 旧写法只在「值 > 3」时重置为 1.0，理由大概是「看着像厚度就重置」。
        //   但薄板的厚度（0.5–2.5 mm）**恰好也像个合理的标度** ⇒ 一切就静默地错了：
        //   实测（Pt_Heater3.3dm 走一遍）页面默认 0.516/0.855/0.776/0.426 被原样当标度，
        //   1366.8 g 的图纸被缩成 874 g/片 —— **算的不是用户给的那张图**，
        //   而输出照旧报「合计 5962 g」，看不出任何异样。
        //   靠数值区分两个量，本来就分不开：0.9 既是合理的厚度也是合理的标度。
        //
        // ⇒ 两个量**各存各的**，切模式时整组交换。谁也不会被对方的值污染。
        if (an != _lastAnalytic)
        {
            for (int i = 0; i < _tPlate.Length; i++)
            {
                (_keepOther[i], _tPlate[i].Value) = (_tPlate[i].Value, _keepOther[i]);
            }
            _lastAnalytic = an;
        }

        // 几何来源变了 ⇒ 有些命令的「适不适用」跟着变 ⇒ 让 SyncGates 重算一遍。
        // 走 Notify 而不是直接改按钮：Enabled 只允许有一个来源。
        SyncAnalysisPending();
        Shared?.Notify();
    }

    /// <summary>
    /// 这条命令**在当前页面状态下适不适用**。只回答适用性，**不碰 Enabled** ——
    /// 真正的启停由 <see cref="MainForm"/> 的 SyncGates 一处决定
    /// （门禁 × 互斥 × 适用性 三者取与）。
    ///
    /// ★ 为什么要有这个：「能点但点了只弹一句『请先切到…』」正是用户最初那句
    ///   「所有标签键都可以点，工程师根本不知道自己目前在算什么」的形状。
    ///   而把 Enabled 分散到各页去设，就会出现本方法注释里那种**两处打架**。
    /// </summary>
    /// <summary>
    /// 把**当前这个解**写成 <c>finaldesigns/*.fd.json</c>。
    ///
    /// ★ 用户 2026-08-22：「不想让工程师复制粘贴，感觉不靠谱」。
    ///   此前落档要人工把十几个数抄进 FinalDesign.cs，其中五个判据值还得从判据表逐个读，
    ///   抄错一位要跑 8 分钟 --selfcheck 才知道。手抄正是「同一个数存两处」的入口。
    ///
    /// ⚠ 程序写这五个记录值**不削弱** --selfcheck：A 段验的从来不是「设计对不对」，
    ///   而是「内核以后改了还能不能算出同一个数」。那五个值本来就来自同一次运行。
    ///
    /// ⚠ Name 由用户填、Binding 留空 —— 「什么咬住了它」是工程判断，不是程序能算的，
    ///   留空比替你猜一句更诚实。
    /// </summary>
    private void SaveAsFinalDesign()
    {
        if (Shared is not { Last: { } r } f || !f.Fresh || !r.AllOk)
        {
            MessageBox.Show(this,
                "只有**判据全过**且**参数没再动过**的解才能落档。" + Environment.NewLine
                + "不成立的设计不该有一个「能落档」的形态；参数动过之后存下去的，"
                + "是上一组参数的解。",
                "还不能另存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string name = Microsoft.VisualBasic.Interaction.InputBox(
            "给这一档起个名（会成为文件名与下拉里的显示名）：",
            "另存为定案档", $"管壁 {(double)_wall.Value:0.0} · 自定");
        if (string.IsNullOrWhiteSpace(name)) return;

        var d = PageToFinalDesign();
        d.Name = name.Trim();
        d.Provenance = $"由 APP「另存为定案档」写出；解出自「{(_srcAnalytic.Checked ? "解析形状" : "Rhino .3dm")}」路径";
        d.Binding = "";                       // ← 工程判断，留给人填
        d.TotalMassG = r.TotalMassG; d.TubeMassG = r.TubeMassG; d.FlangeMassG = r.FlangeMassG;
        // 五个回归基准值：**从本次解直接取**，不经人手
        d.RampH = r.ValueOf(LineResult.Key.Ramp);
        d.DiscOverK = r.ValueOf(LineResult.Key.DiscTemp);
        d.HoleFluxW = r.ValueOf(LineResult.Key.NetFlux);
        d.FlangeDipK = r.ValueOf(LineResult.Key.FlangeDip);
        d.TubeJ = r.ValueOf(LineResult.Key.TubeJ);

        try
        {
            string path = FinalDesignStore.Save(d);
            MessageBox.Show(this,
                $"已写出：{path}" + Environment.NewLine + Environment.NewLine
                + "下一步（都要做）：" + Environment.NewLine
                + "  1. 把 binding 填上 —— 什么咬住了它（余量最小的那条）" + Environment.NewLine
                + "  2. 跑 --selfcheck，A 段这一档的差须为 0.000" + Environment.NewLine
                + "  3. 提交进 git —— 档是回归基准，变更要被 diff 记录",
                "已另存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // 重扫磁盘，让新档立刻出现在**每一个**定案档下拉里。
            // 少了这一句，界面会说「已写出」而下拉里找不到它 —— 工程师只能
            // 猜是没存上，于是再存一次（撞重名被拒），或者干脆不信这个功能。
            FinalDesign.Reload();
            if (FinalDesignStore.LoadErrors.Count > 0)
                MessageBox.Show(this,
                    "档已写出，但重扫 finaldesigns/ 时有档读不进来：" + Environment.NewLine
                    + string.Join(Environment.NewLine, FinalDesignStore.LoadErrors) + Environment.NewLine
                    + Environment.NewLine + "少一个档 = 少一组回归基准，不要放着不管。",
                    "读档有错", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "另存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 把定案档下拉重填一遍，**按档名保住当前选中的那一档** —— 不能按下标，
    /// 新档追加在内置档后面，下标会移位。
    ///
    /// 本页的下拉**没挂** SelectedIndexChanged（换档只是改选择，要点「载入定案」才生效），
    /// 所以重填不触发任何计算。哪天给它挂上了事件，这里必须同时加抑制位，
    /// 否则重扫一次就等于替用户按了一下载入。
    /// </summary>
    private void RefillCaseBox()
    {
        string keep = _caseBox.SelectedItem as string ?? "";
        _caseBox.Items.Clear();
        foreach (var fd in FinalDesign.All) _caseBox.Items.Add(fd.Name);
        int i = _caseBox.Items.IndexOf(keep);
        if (i < 0) i = System.Array.IndexOf(FinalDesign.All, FinalDesign.Current);
        _caseBox.SelectedIndex = i < 0 ? 0 : i;
    }

    private void OnFinalDesignsReloaded(object? sender, System.EventArgs e)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(new System.Action(RefillCaseBox)); return; }
        RefillCaseBox();
    }

    /// <summary>
    /// ⚠ <see cref="FinalDesign.Reloaded"/> 是**静态**事件：不退订，这个页面就永远被它拿着。
    /// 真机上只有一个实例、无所谓，但 UiWiring 一个进程里反复造窗体 ——
    /// 旧实例的处理器会跟着累加，然后往**已销毁的控件**上写，
    /// 报出来的错跟真正的病因八竿子打不着。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) FinalDesign.Reloaded -= OnFinalDesignsReloaded;
        base.Dispose(disposing);
    }

    /// <summary>
    /// 维护「.3dm 已选图纸但还没分析」这一位。**只在这一处算**，
    /// 指路（Flow.Next）只读它 —— 判据/状态一律单一来源。
    /// </summary>
    private void SyncAnalysisPending()
    {
        if (Shared is not { } f) return;
        f.GeomAnalysisPending =
            !_srcAnalytic.Checked
            && !string.IsNullOrWhiteSpace(_file3dm[0].Text)
            && _shape is null;
    }

    internal bool CommandApplicable(string cmdId) => cmdId switch
    {
        // 读 .3dm 反推台阶 —— 解析形状是程序生成的，没有图纸可反推
        "geom.analyze" => !_srcAnalytic.Checked && !string.IsNullOrWhiteSpace(_file3dm[0].Text),
        // 形状搜索只在解析模式有意义：.3dm 的形状由图纸给定，不是可搜索的自由度
        "shape.search" => _srcAnalytic.Checked,
        // 另存：存的是**当前这个解**，所以必须「判据全过」且「参数没再动过」。
        //   不成立的设计不该有一个「能落档」的形态；
        //   参数动过之后存下去的，是**上一组参数**的解 —— 那是最坏的一种档。
        "final.save" => Shared is { Fresh: true, Last.AllOk: true },
        _ => true,
    };

    private void PickFile(int idx)
    {
        using var dlg = new OpenFileDialog { Filter = "Rhino 3D 模型 (*.3dm)|*.3dm" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _file3dm[idx].Text = dlg.FileName;
        // 选完文件要重新过一遍 enable —— 否则「分析几何变数」选了图纸也不会亮
        SyncGeomSource();
        // 空着的后续片默认沿用同一文件 —— 四片常常同形状，省得点四次
        for (int k = idx + 1; k < _file3dm.Length; k++)
            if (string.IsNullOrWhiteSpace(_file3dm[k].Text)) _file3dm[k].Text = dlg.FileName;
    }

    private static NumericUpDown Num(decimal v, decimal lo, decimal hi, decimal inc, int dec)
    {
        var n = new NumericUpDown { Width = UiScale.S(96), DecimalPlaces = dec, Increment = inc };
        n.Minimum = lo; n.Maximum = hi;
        n.Value = Math.Clamp(v, lo, hi);
        return n;
    }

    /// <summary>
    /// 左边**参数表**改了一项 —— 上一次的解立刻不再新鲜（2026-08-24）。
    ///
    /// ★★★★★ 在此之前 <c>PropertyGrid.PropertyValueChanged</c> **根本没被挂过**，
    ///   而 <see cref="Snap"/> 只记 6 个页面控件（壁厚/板厚/管保温/盘径/舌长/舌宽）。
    ///   于是在参数表里改控温点、保温层、牌号、铜排夹持温度……：
    ///     · 不武装自动重算
    ///     · 不作废上一次的解
    ///     · CurrentSnap 一点不变 ⇒ <c>Fresh</c> 仍是 **true**
    ///   ⇒ 判据表还挂着旧参数的结论，而 ④⑤ 两道 RequireFresh 的门**照开**。
    ///   这不是「界面乱」，是**交付门上的假绿灯**：拿一张别的参数的判据表去出图。
    ///
    /// 处理方式：只把「新鲜」摘掉，**不清空判据表**。
    /// 数字留着让人对照，但 <see cref="Flow.Next"/> 会说
    /// 「参数在上次求解之后又动过了 —— 回 ③ 按现在这组重解」，门也随之关上。
    ///
    /// ⚠ 故意**不**触发自动重算（页面控件那条会）：参数表里有一批只是读说明时
    ///   顺手碰到的项，为它排一次分钟级的解是惊吓不是服务。两条路的差别写在这里，
    ///   免得下次有人当成漏接。
    /// </summary>
    internal void MarkParamsChanged(string what)
    {
        if (_solvedSnap is null && _last is null) return;    // 本来就没有可作废的
        _solvedSnap = null;                                   // ⇒ Fresh = false，门关上
        _solvedRes = null;                                    // 外推基准也作废（它是另一组参数的解）
        _pendingReview = null;                                // 待插入的形状体检同理
        PushFlow();
        Shared?.RestartChain();                               // ★ 连越关一起作废，见那里的说明
        WarnParamsRestartOnce(what);
        _out.Text = $"⚠ 参数表改了「{what}」—— 上一次的解**不再对应当前参数**。" + Environment.NewLine
                  + "   判据表留在下面供对照，但它是**上一组参数**的结论；" + Environment.NewLine
                  + "   ④ 定尺寸与 ⑤ 交付已经关上，请点「核算整线」按现在这组重解。"
                  + Environment.NewLine + Environment.NewLine + _out.Text;
    }

    /// <summary>
    /// 有链在跑时**冻住输入面**（2026-08-24 用户提出：「只要有运算，禁止跳到任何页」）。
    ///
    /// 互斥此前只管**按钮**（会起算的命令全禁掉），防的是三个分钟级求解同时开跑。
    /// 但输入是自由的：整线解跑着的那几分钟里，壁厚、盘径、段表、几何来源
    /// 都还能改 —— 而工程师看到的判据表、指路、门禁全是**上一次**的。
    /// 「我现在在算什么」这件事就从界面上消失了，而那正是整个阶段轨要治的病。
    ///
    /// ⇒ 跑起来就冻住输入，只留命令（取消）与只读输出。
    ///   这**不是**替代 snapAtStart 那条修正：那条保证「即使改了也不会判成新鲜」，
    ///   这条保证「压根改不了」。两条是内外两道，缺一条都还有缝
    ///   （例如程序自己在 _suppressAuto 期间写控件，就绕过了界面这一层）。
    /// </summary>
    /// <summary>「参数一动，整条链从头再走一遍」这条规则的**一次性**告知（2026-08-24 用户要求）。
    ///
    /// 为什么只弹一次：这是一条**规则**，不是一次事件。第一次讲清楚，之后由
    /// 状态面板那一行（「⚠ 参数已改 —— 下面的数是上一次的」）持续提醒就够了。
    /// 每改一次弹一次，三次之后人就只会闭着眼睛点「确定」——
    /// 那时它既没教会规则，又拖慢了操作。
    ///
    /// ⚠ 窗体没 Show 出来就不弹：接线测试从不 Show（只 CreateControl），
    ///   而那里有专门验「自动跑那次不该弹模态框」的节 —— 真弹出来会把整套测试挂住。
    ///   没显示的窗体上弹框本来也没有意义。**一次性的开关也不在这种情况下消耗掉。**
    /// </summary>
    private bool _paramWarnShown;

    private void WarnParamsRestartOnce(string what)
    {
        if (_paramWarnShown) return;
        if (!IsHandleCreated || !Visible) return;
        _paramWarnShown = true;
        // 排到消息队列尾部：别在控件的 ValueChanged 里同步弹模态框
        BeginInvoke(new Action(() => MessageBox.Show(this,
            $"你刚改了「{what}」。从这一刻起：" + Environment.NewLine + Environment.NewLine
            + "  · 上一次的解**不再对应当前参数** —— 判据表留着给你对照，" + Environment.NewLine
            + "    但它是**上一组参数**的结论；" + Environment.NewLine
            + "  · ④ 定尺寸 与 ⑤ 交付 两道门已经关上；" + Environment.NewLine
            + "  · 之前若「越关」进过某一格，那张通行证也一并作废" + Environment.NewLine
            + "    （它是按旧参数批的）。" + Environment.NewLine + Environment.NewLine
            + "要拿到当前这组参数的结论，请回「③ 整线核算」重解一次。"
            + Environment.NewLine + Environment.NewLine
            + "（本提示只出现这一次；之后由右上角状态面板持续提醒。）",
            "参数一动 —— 整条链要从头再走一遍",
            MessageBoxButtons.OK, MessageBoxIcon.Information)));
    }

    internal void SetInputsEnabled(bool on)
    {
        if (_inputPanel is not null) _inputPanel.Enabled = on;
    }

    /// <summary>
    /// 把「参数一动」接到「自动出答案」上。**不新增任何控件** ——
    /// 用户改的还是原来那些框，只是不必再记得去按「核算整线」。
    /// </summary>
    private void HookAutoRun()
    {
        void Watch(Control c)
        {
            // ⚠ **工具条整条跳过**。ToolStripComboBox 内部宿主着一个真 ComboBox，
            //   而它确实挂在 ToolStrip.Controls 上 ⇒ 递归会把「定案档 ▾」也当成参数，
            //   于是用户只是想换个档看看，就触发了一次分钟级的整线重算（实测抓到）。
            //   工具条上的东西是**命令**，不是参数。
            if (c is ToolStrip) return;
            switch (c)
            {
                case NumericUpDown n: n.ValueChanged += (_, _) => ParamChanged(); break;
                case ComboBox cb: cb.SelectedIndexChanged += (_, _) => ParamChanged(); break;
                case CheckBox ck: ck.CheckedChanged += (_, _) => ParamChanged(); break;
                case RadioButton rb: rb.CheckedChanged += (_, _) => ParamChanged(); break;
            }

            // ★★ 2026-08-20：**以后新生的控件也要自动接上。**
            //
            // 病灶：本方法在构造函数末尾跑完一次就完事，而各级「锁定」勾选框是
            // `BuildLockBoxes` 在**运行时**（点过「分析几何变数」之后）才创建的 ⇒
            // 它们永远赶不上这趟车，勾/取消锁定**不触发自动重算**。
            // 用户改了锁定却看不到任何反应，而界面没有任何异样。
            //
            // 修法不是「在 BuildLockBoxes 里记得也挂一次」—— 那只是补上今天这一处，
            // 明天第二处运行时控件照样漏。这里把「必须记得挂」这个前提整个去掉，
            // 与 MainForm.ApplyToolStripFont 的既定做法同源。
            //
            // ⚠ 上面那句 `if (c is ToolStrip) return;` 必须继续管用：ControlAdded 递归
            //   同样会走到工具条上，而「定案档 ▾」被当成参数会让切档触发分钟级重算
            //   （那个 bug 修过一次，接线测试第 1 项守着它）。
            c.ControlAdded += (_, e) => Watch(e.Control);

            foreach (Control k in c.Controls) Watch(k);
        }
        foreach (Control c in Controls) Watch(c);
        _segGrid.CellValueChanged += (_, _) => ParamChanged();
        _segGrid.RowsRemoved += (_, _) => ParamChanged();
        // 加一段是真的会改变段数与法兰片数 —— 与删一段同等重要，此前只挂了删。
        _segGrid.RowsAdded += (_, _) => ParamChanged();
    }

    /// <summary>
    /// ★★★★★ **舌长的装配下界**（2026-08-17）—— 用户第 2 项：
    /// 「APP 不能**自动**改变法兰盘直径与舌片长度吗？」
    ///
    /// 舌长根本不该是一个自由旋钮：它由三段拼出来，
    ///   舌长 = 圆盘切点 + 压接段 + 自由段
    /// 前两段是几何与工艺给的，第三段有下界（铜排装得下）。
    /// **加长只会多花铂、多发热**，所以最优解永远贴着这个下界。
    ///
    /// 定案的 90 mm 之所以能长期存在，正是因为舌长在程序里是个独立常数，
    /// 从来没人拿盘径去核对过它。⇒ 现在让盘径/舌宽一动，舌长**自己跟上来**。
    /// </summary>
    private double TabLenFloorMm()
    {
        double R = (double)_discD.Value * 0.5;
        double hw = Math.Min((double)_tabW.Value, R);
        double tangent = Math.Sqrt(Math.Max(0, R * R - hw * hw));
        return tangent + FinalDesign.Current.ClampLengthMm + FreeTabMin;
    }

    /// <summary>
    /// 自由段下界 mm —— **读判据的那一份，本页不再自己存一个**。
    ///
    /// ⚠ 2026-08-20 之前这里是 `private readonly double FreeTabMin = 100.0;`，
    ///   （字段名 `_freeTabMinMm`），而判据侧另有 `LineCase.FreeTabMinMm = 100.0`。同一条判据、同一个限值，
    ///   **存了两处**。当时两边碰巧相等，所以谁也没发现 —— 而这正是
    ///   HANDOVER §1.8 铁律三点名的形状（「散在三处正是连错四次的根源」）：
    ///   哪天有人只改了其中一处，界面会说「装得下」而判据说「装不下」，
    ///   或者反过来，且两边都言之凿凿。
    /// </summary>
    private static double FreeTabMin => GeometryScreen.FreeTabMinDefaultMm;

    /// <summary>舌长低于装配下界就**顶上去**，并说清楚为什么 —— 不静默、也不放行。</summary>
    private string EnforceTabLenFloor()
    {
        double floor = TabLenFloorMm();
        if ((double)_tabLen.Value >= floor - 1e-9) return "";
        decimal want = Math.Min(_tabLen.Maximum, Math.Ceiling((decimal)floor));
        double had = (double)_tabLen.Value;
        bool keep = _suppressAuto;
        _suppressAuto = true;                       // 这是程序在写控件，别再触发一轮
        _tabLen.Value = want;
        _suppressAuto = keep;
        return $"   ★ 舌长已由 {had:0} **自动顶到 {want:0} mm** —— 低于它铜排装不上（判据⑤）。\r\n" +
               $"     舌长 = 圆盘切点 {Math.Sqrt(Math.Max(0, Math.Pow((double)_discD.Value * 0.5, 2) - Math.Pow(Math.Min((double)_tabW.Value, (double)_discD.Value * 0.5), 2))):0.0}" +
               $" + 压接段 {FinalDesign.Current.ClampLengthMm:0} + 自由段 {FreeTabMin:0}。\r\n" +
               $"     想要更短的舌片，要改的是**盘径或铜排尺寸**，不是舌长本身。\r\n";
    }

    /// <summary>参数动了：立刻给预测，并重排防抖定时器。</summary>
    private void ParamChanged()
    {
        if (_suppressAuto) return;           // 程序在写控件，不是用户在改
        // ★ 首屏那一阵的控件事件**不是用户改的参数**（2026-08-23 界面抓图抓到）。
        //   实况：窗体一 Show 出来，_segGrid 在首次绑定/排版时抛 CellValueChanged，
        //   被当成「参数动了」⇒ 防抖 1.5 s 到期，**自己起了一次分钟级的解**。
        //   抓图里状态面板全程写着「正在算：核算整线」而结果是「还没解过」，就是它。
        //   ⚠ 接线测试没抓到，因为它只 CreateControl 不 Show —— 排版路径根本没走。
        if (!_userReady) return;

        // ★ 页面控件动了同样要「从头走一遍」（2026-08-24 用户要求）。
        //   新鲜度那一侧本来就自动成立（Snap 变了 ⇒ Fresh 变 false），
        //   但**越关**不会自己失效 —— 它是在**旧参数**上批的。
        //   放在首屏闸门之后：程序写控件、排版期的事件都不算「用户改了参数」。
        Shared?.RestartChain();
        WarnParamsRestartOnce("页面参数");

        _autoArmed = true;
        // ⚠ **立刻**取消在跑的那次，不要等防抖到期（实测发现的：原来放在 TryAutoRun 里，
        //   于是用户改完参数后，一个**结果已经作废**的求解还要再跑满 1.5 秒防抖窗口，
        //   之后才被取消 —— 白烧 CPU，还把重启又往后推了一整个窗口）。
        //   参数一动，在跑的那次就已经过期了，没有任何理由让它继续。
        _cts?.Cancel();
        _autoTimer.Stop(); _autoTimer.Start();       // 连续改只在最后一次之后跑
        ShowPrediction();
    }

    /// <summary>
    /// 防抖到期：真解一次。若上一次还在跑就先取消 —— 用户已经改了参数，
    /// 那次的结果**本来就已经过期**，跑完也没人要。
    /// </summary>
    private void TryAutoRun()
    {
        if (!_autoArmed) return;
        if (_cts is not null) { _cts.Cancel(); _autoTimer.Start(); return; }   // 等它退出再来
        _autoArmed = false;
        _ = RunAsync(false, byTimer: true);
    }

    // ★ 实测雅可比（`--vary`，端点均已收敛，管壁 0.8 定案点附近）。
    //   ⚠ 只对**这个工作点附近**成立 —— ②″ 由两个竞争峰决定，符号会随构型变
    //     （已经栽过一次：拿另一构型的符号外推，判反了）。
    //   ⇒ 外推只用来给「大概会往哪边走」，绝不当结论；超出一步就不显示。
    //
    // ★★★ 下面这三个「测点」常数是 2026-08-17 补的，**必须与上面那组斜率同时更新**。
    //   在此之前它们不存在，于是没有任何东西能回答「这组斜率是在哪个形状上测的」——
    //   换了形状照样外推。而斜率本身那句注释早就写着「符号会随构型变」。
    //   ⇒ 谁改斜率，就必须一起改这三个数；`ShowPrediction` 用它们判「本构型在不在范围内」。
    private const double JacDiscD = 60.0, JacTabLen = 140.0, JacTabW = 30.0;
    // 2026-08-17 `--vary` 在**新定案点**（盘Ø60／舌140×60／板厚 0.89/2.45/2.35/0.73）重测：
    //
    // ★ 2026-08-20 起，参数提示框与外推的「驱动项」文字都**插值自这里**，不再各抄一份。
    //   起因：这几个常数 08-17 就换成了新值，而提示框里还挂着旧构型的
    //   ③ +123／−221／+22.7 —— 三处数字对不上，谁也没发现。
    //   ⇒ 改这几个数只需改这一处；界面上所有引用它们的地方会自己跟着变。
    private const double dDip_dWall = -333.6, dJ_dWall = -5.99;
    private const double dDip_dPlate = +194.5;
    private const double dDip_dTubeIns = +34.0, dJ_dTubeIns = -0.57;

    // ★★★★★ **②″ 的外推被撤掉了**（2026-08-17），这是有实测依据的决定，不是省事。
    //
    // 旧常数（另一个构型：舌 90×30、环 1.22、板厚约两倍）：
    //     ∂②″/∂管壁 = **+16.66**　∂②″/∂板厚 = **−14.59**
    // 新定案点重测：
    //     ∂②″/∂管壁 = **−0.12**　∂②″/∂板厚 = **+0.14**
    // ⇒ **两个都翻了符号，量级掉了 100–140 倍。**
    //
    // 物理上说得通：舌片加宽一倍之后孔周电流不再拥塞，②″ = −0.21 K 而限值是 +5 ——
    // 这条判据在本构型上**根本不活跃**，所以什么都推不动它。
    //
    // ⇒ 对一个「不动的量」做线性外推，最好的情况是噪声，最坏的情况是拿**反号**的斜率
    //   告诉用户「往那边走会更好」。两者都不该发生 ⇒ 不推，只在真解里报它的实测值。
    //   这正是那句注释警告过的事：「②″ 由两个竞争峰决定，符号会随构型变」——
    //   以前只是写着，现在有两组数把它坐实了。

    /// <summary>
    /// 三层里的前两层：**解析层**（精确）与**预测层**（外推）。毫秒级，不解场。
    /// 第三层（真解）由 <see cref="TryAutoRun"/> 在后台补上，回来后覆盖本文本。
    /// </summary>
    private void ShowPrediction()
    {
        PushFlow();          // 参数一动，右上角的门禁与新鲜度立刻跟上
        var now = CurrentSnap();
        var sb = new StringBuilder();

        // ── 解析层：纯几何，闭式，精确
        double wall = now.Wall;
        double area = Math.PI * (Math.Pow(25 + wall, 2) - 625.0);          // mm²
        double tubeG = area * 300.0 * 3 * Materials.PtDensity * 1e-6;
        sb.AppendLine("── 参数已改（下面标「解析」的是精确值，标「预测」的还没解）");
        // ★ 装配先算：它是纯几何、闭式、精确，且**不合格时热学结果没有意义**
        //   （一个装不上的形状，算得再准也交不出去）⇒ 放在最前面。
        string fixedTab = EnforceTabLenFloor();
        if (fixedTab.Length > 0) sb.Append(fixedTab);
        double tanNow = Math.Sqrt(Math.Max(0, Math.Pow((double)_discD.Value * 0.5, 2)
                        - Math.Pow(Math.Min((double)_tabW.Value, (double)_discD.Value * 0.5), 2)));
        double freeNow = (double)_tabLen.Value - tanNow - FinalDesign.Current.ClampLengthMm;
        // 解析层这四行是一张表：名称 / 值 / 单位 / 说明。
        // ⚠ 两条 ⚠ 告警**必须排在整张表之后**，不能夹在行与行中间：不带 \t 的整句
        //   会被当成普通句子，**把一张表断成两截**，两截各自量各自的列宽 ——
        //   于是「自由段」那行的数值列与下面三行错开，而错的时机偏偏是告警触发的时候。
        sb.AppendLine($"   自由段\t{freeNow:0.0}\tmm\t解析（判据⑤ 下界 {FreeTabMin:0}）" +
                      (freeNow >= FreeTabMin - 1e-9 ? "　✓ 铜排装得下" : "　✗ **装不下**"));
        sb.AppendLine($"   管截面\t{area:0.0}\tmm²\t解析");
        sb.AppendLine($"   管铂重\t{tubeG:0}\tg\t解析（三段）");
        sb.AppendLine($"   管孔半径\t{wall + 25:0.0}\tmm\t解析（跟随管外径）");
        if ((double)_tabW.Value > (double)_discD.Value * 0.5 + 1e-9)
            sb.AppendLine($"   ⚠ 舌端半宽 {(double)_tabW.Value:0} > 盘半径 {(double)_discD.Value * 0.5:0}" +
                          " ⇒ 等宽舌片与圆盘没有切点，半宽会被**静默夹到盘半径**。要真加宽请同时放大盘。");
        // ⚠ 下界取自参数表，**不再写死 0.6** —— 写死等于同一个数存两处，
        //   用户按现场经验把它改成别的值时，这行警告还会拿旧数去比。
        //   顺带标明这条下界是内置默认还是人按实际经验填的（用户 2026-08-21）。
        if (wall < _base.WeldMinThicknessMm - 1e-9)
            sb.AppendLine($"   ⚠ 壁厚 {wall:0.00} 低于焊接工艺下界 "
                + $"{_base.WeldMinThicknessMm:0.00} mm（{_base.WeldMinSource}）—— 工艺上焊不出来。"
                + "能造能用是前提，减重排在它后面");

        // ── 预测层：只在**有已解基准**且**改动不太大**时才给
        if (_solvedSnap is not null && _solvedRes is { Ok: true, Converged: true })
        {
            double dW = now.Wall - _solvedSnap.Wall;
            double dP = now.Plate - _solvedSnap.Plate;
            double dI = now.TubeIns - _solvedSnap.TubeIns;
            bool tooFar = Math.Abs(dW) > 0.25 || Math.Abs(dP) > 0.4 || Math.Abs(dI) > 3.0;
            // ★ 还要问一句：**当前形状是不是雅可比测过的那个形状**（2026-08-17 补）。
            //   步长小不等于可以外推 —— 换个构型，②″ 的符号都可能翻。
            bool offShape = Math.Abs(now.Disc - JacDiscD) > 1e-6
                         || Math.Abs(now.TabLen - JacTabLen) > 1e-6
                         || Math.Abs(now.TabW - JacTabW) > 1e-6;

            double V(string k)
            { foreach (var c in _solvedRes.Checks) if (c.Name.StartsWith(k, StringComparison.Ordinal)) return c.Actual; return double.NaN; }

            sb.AppendLine();
            if (offShape)
                sb.AppendLine($"   （**本构型不在雅可比的适用范围** —— 那组斜率是在 " +
                              $"盘Ø{JacDiscD:0}／舌 {JacTabLen:0}×{2 * JacTabW:0} 上实测的，" +
                              $"而现在是 盘Ø{now.Disc:0}／舌 {now.TabLen:0}×{2 * now.TabW:0}。" +
                              "②″ 的符号会随构型翻 ⇒ **不给预测**，等真解。）");
            else if (tooFar)
                sb.AppendLine("   （改动已超出实测雅可比的适用范围 ⇒ **不给预测**，等真解）");
            else
            {
                // 五列：名称 / 基准 / 预测 / 限值 / 说明。
                // ⚠ 「预测」两个字必须留在数值同一格里 —— 它是这一屏唯一区分
                //   「已解」与「外推」的记号（见本类顶部：预测值绝不能长得像解出来的）。
                void P(string nm, double base0, double pred, double limit, string drivers)
                {
                    string verdict = pred <= limit ? "" : "⚠ 预测越限　";
                    sb.AppendLine($"   {nm}\t{base0:0.00}\t预测 {pred:0.00}\t/ {limit:0.0}\t{verdict}{drivers}");
                }
                // 驱动项的数值同样插值自常数 —— 与提示框、与外推用的斜率是**同一个来源**
                P("③", V("③"), V("③") + dDip_dWall * dW + dDip_dPlate * dP + dDip_dTubeIns * dI, 10.0,
                  $"板厚 {dDip_dPlate:+0;−0} K/mm　管壁 {dDip_dWall:+0;−0}　管保温 {dDip_dTubeIns:+0;−0}");
                P("管J", V("管 J"), V("管 J") + dJ_dWall * dW + dJ_dTubeIns * dI, 12.0,
                  $"管壁 {dJ_dWall:+0.00;−0.00}　管保温 {dJ_dTubeIns:+0.00;−0.00}");
                // ⚠ 列数必须与 P 完全一致，否则它会自成一张表、和上面两行对不齐
                sb.AppendLine($"   ②″\t{V("②″"):0.00}\t**不外推**\t/ 5.0\t" +
                              "本构型上它不活跃（实测各斜率 |·| ≤ 0.15，且符号与旧构型相反）");
                sb.AppendLine("   ⚠ 预测是**线性外推**，只说方向与量级，不是答案。");
            }
        }
        else
            sb.AppendLine("   （还没有已解的基准 ⇒ 给不了预测。第一次请等真解跑完）");

        sb.AppendLine();
        sb.AppendLine("── 正在后台重算…（改完参数停手约 1.5 秒就自动开始；点「取消」可中止）");
        _out.Text = sb.ToString();
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
        LineResult r, LineCase lc, IProgress<string> prog, CancellationToken ct,
        bool interactive)
    {
        if (r is null || !r.Ok || r.Converged) return r;
        if (!(r.Message?.Contains("慢") ?? false)) return r;      // 没在收缩 ⇒ 加轮数没用

        string need = r.Notes.FirstOrDefault(n => n.Contains("还需约")) ?? "";

        // ⚠ **自动跑的那次绝不弹模态框**。自动重算是用户改完参数就走开的场景 ——
        //   回来看到一个卡住整个界面的对话框，比没算完更糟；而且他一改参数
        //   这次结果就已作废，弹框问「要不要为它多跑」本身就没意义。
        //   ⇒ 自动跑只把建议写进输出框；要重跑，手动点「核算整线」。
        if (!interactive)
        {
            _out.Text +=
                "\r\n── ⚠ 没收敛，但残差**仍在收缩** —— 是「慢」，不是「发散」。\r\n" +
                (need.Length > 0 ? "   " + need.Replace("★ ", "") + "\r\n" : "") +
                "   要为这组参数多跑一会儿：点一下「核算整线」（手动跑才会问你要不要加轮数）。\r\n";
            return r;
        }

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
    ///
    /// ⚠ 1b（2026-08-17）之后，「核算整线」用的是**同一套几何构造器**，
    ///   把定案参数填进页面也能复现定案值（界面接线测试第 16 项每次都验，差 0.000）。
    ///   那本条为什么还留着？——因为它**完全不读页面**：
    ///   用来排除「页面上某个控件被改过而自己没注意到」。
    ///   两条路给同一个数，才说明页面没被动过手脚；给不同的数，就该查页面。
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
        // ★ 告诉阶段轨「正在跑哪条链」——右上角状态面板据此显示「正在算：…」，
        //   而且它**切到哪一页都看得见**（④ 页自己没有进度条）。
        //   同时它是互斥闸：SyncGates 会把所有会起算的命令禁掉。
        Shared?.SetRunning(ChainId.C整线耦合, "复现定案");
        var prog = new Progress<string>(s => { _status.Text = s; Shared?.SetRunningNote(s); });

        try
        {
            // ═══ 一键走完全程（2026-08-21 用户要求）：灌控件 → **仍从档解** → 发布状态 ═══
            //
            // 病灶：此前本方法只写 `_last` + `Show()`，**既不设 _solvedSnap 也不 PushFlow**
            //   ⇒ FlowState.Last 从来没被推过、Fresh 恒 false
            //   ⇒ **复现出一个全判据通过的解，④⑤ 一格都不开**，阶段轨当作什么都没发生。
            //   与 Snap 那个引用相等 bug 同族：界面状态不反映实际。
            //
            // ① 先把定案值灌进页面控件 —— 让界面显示与档一致，CurrentSnap 才对得上。
            LoadFinalDesignFrom(fd, quiet: true);

            // ★ 快照取在**灌完控件、开解之前**这一刻（2026-08-24）。
            //   原来是解完再取 —— 复现要几分钟，这几分钟里控件可改，
            //   于是「解的那组」与「记下的那组」可以是两组，而 Fresh 判成 true。
            //   ⚠ 必须在 LoadFinalDesignFrom **之后**：上一行刚把定案值灌进控件，
            //     放到它前面记的就是用户原来那组，复现完会永远判成不新鲜。
            var snapAtStart = CurrentSnap();

            // ② **仍从档解**，不走 PageToFinalDesign()。
            //    保住这条独立路径是有代价换来的：PageToFinalDesign 是一段**搬运代码**，
            //    本项目已经栽过好几次（盘径直径/半径、压接段用了 3 mm 默认值、
            //    管孔渐变环整个漏掉）。复现对账的作用就是抓这类错 ——
            //    若复现也改走页面路径，就成了**用有嫌疑的那条路去验它自己**，
            //    搬运错了两边一起错，对账照样打勾。那正是假绿灯。
            // ★ checkRamp: true —— ① 也要判。少判一条就不是「全判据通过」。
            var lc = fd.BuildCase(_base, checkRamp: true);
            var r = await Task.Run(() => LineRunner.Run(lc, prog, ct), ct);
            r = await RetryIfJustSlowAsync(r, lc, prog, ct, interactive: true);
            _last = r;
            Show(r);

            // 与 FinalDesign 记录值对账：不一致要**当场说出来**，不能等人自己发现
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("── 复现对账（本次实算 vs FinalDesign 记录值）");
            if (r.Ok)
            {
                double mt = r.TubeMassG, mf = r.FlangeMassG, all = r.TotalMassG;
                // 四列：名称 / 本次实算 / 记录值 / 差。
                // ⚠ 差值格式里用 ASCII 的 `-`，不用 U+2212 —— 右对齐靠补空格，
                //   一格里混进非 ASCII 字形就宽度不成整数倍，那一列会退回左对齐。
                void Line(string nm, double got, double want)
                {
                    double d = want > 0 ? (got - want) / want * 100 : 0;
                    sb.AppendLine($"   {nm}\t{got:0.0} g\t记录 {want:0} g\t" +
                                  $"差 {d:+0.0;-0.0} %" + (Math.Abs(d) <= 1.0 ? "" : "　⚠"));
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
            sb.AppendLine("⚠ 这条路**完全不读页面上的控件**。");
            sb.AppendLine("   1b（2026-08-17）之后「核算整线」用的是同一套几何构造器 ——");
            sb.AppendLine("   把定案参数填进页面，它也能给出上面这组数。两条路**应当一致**；");
            sb.AppendLine("   不一致就说明页面上有控件被改过，查页面，别怀疑内核。");
            _out.Text += sb.ToString();

            // ═══ ③ 发布状态 —— 没有这一步，复现出全判据通过的解 ④⑤ 也一格不开 ═══
            //
            // ⚠ 「新鲜」这个断言必须**说真话**：Fresh 的含义是
            //    「页面上这组参数就是解出这个结果的那组」。
            //    水头**不属于定案几何**（LoadFinalDesignFrom 故意不动它），
            //    而本按钮从档解、用的是 LineCase 的内核默认水头 —— 两者可能不同。
            //    此时页面参数并没有产生这个解，**不能假装 Fresh**，否则 ④ 会拿
            //    「页面工况的解」当起点，而它其实是「存档工况的解」。
            var headPage = _segs.Where(x => !string.IsNullOrWhiteSpace(x.名称))
                                .Select(x => x.水头m).ToArray();
            var headCase = lc.HeadM;
            bool headSame = headPage.Length == headCase.Length
                         && headPage.Zip(headCase).All(t => Math.Abs(t.First - t.Second) < 1e-9);

            if (r.Ok && r.Converged && headSame)
            {
                // 同 RunAsync：快照取**开解那一刻**，不是解完这一刻。
                // 复现要几分钟，这几分钟里控件可改 —— 拿解完时的控件当「解过的参数」，
                // 就会把一张别的参数的判据表标成新鲜。
                _solvedRes = r; _solvedSnap = snapAtStart;
            }
            PushFlow();

            if (r.Ok && r.Converged && !headSame)
                _out.Text +=
                    Environment.NewLine
                    + "⚠ **本次解的是存档工况，不是页面上的水头**（页面 "
                    + string.Join("/", headPage.Select(v => v.ToString("0.0"))) + " m，存档 "
                    + string.Join("/", headCase.Select(v => v.ToString("0.0"))) + " m）。"
                    + Environment.NewLine
                    + "   ⇒ 不把它记作「页面参数的解」，④ 仍需你点「核算整线」按页面工况重解一次。"
                    + Environment.NewLine
                    + "   水头是工艺量、不属于定案几何，所以「载入定案」不会覆盖它 —— 这是有意的。";

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
            Shared?.SetRunning(null);          // 清在 finally：异常/取消也必须解除互斥
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
        LoadFinalDesignFrom(FinalDesign.All[i], quiet: false);
    }

    /// <summary>
    /// 按**指定档**灌控件。<paramref name="quiet"/> = true 时**不写输出框** ——
    /// 供「▶ 复现定案」复用：它自己要在输出框里写复现对账，
    /// 不能被这里的「已载入定案档…」整段冲掉。
    ///
    /// 拆出来是为了让两个入口共用同一段灌值代码 ——
    /// 各抄一份就是「同一件事存两处然后悄悄漂开」。
    /// </summary>
    private void LoadFinalDesignFrom(FinalDesign fd, bool quiet)
    {
        decimal C(double v, NumericUpDown n) =>
            Math.Clamp((decimal)v, n.Minimum, n.Maximum);

        // ⚠ 下面是**程序**在写控件，不是用户在改 —— 闭掉自动重算，
        //   否则这一批赋值会连环触发，还会把本方法的说明文字冲掉。
        _suppressAuto = true;

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

        if (quiet) { _suppressAuto = false; return; }

        _out.Text =
            // ★ 失效告示必须在**最前面**：这一段是用户载入定案后唯一会读的文字，
            //   把「本档已失效」写在第五行等于没写（§1.8：安静失败靠的就是没人看的位置）。
            (fd.Invalid.Length > 0
                ? "═══ ★★★ 本档已失效，不可作为交付值 ★★★ ═══\r\n" + fd.Invalid + "\r\n" +
                  $"（自由段 {fd.FreeTabMm:0.0} mm）\r\n═══════════════════════════════\r\n\r\n"
                : "") +
            "已载入定案档：" + fd.Describe() + "\r\n" +
            "咬住它的：" + fd.Binding + "\r\n" +
            "出处：" + fd.Provenance + "\r\n" +
            $"外层耦合剩余误差估计 {fd.ResidualK:0.00} K（不是步长；见 HANDOVER §1.85）\r\n\r\n" +
            // ★ 这段话 2026-08-17（1b）之前是「本页表达不了两项，核算整线算的是另一片法兰」。
            //   1b 之后**不再成立**：解析模式与「复现定案」走同一个构造器，
            //   界面接线测试第 16 项每次都验「页面路径复现定案记录值」（差 0.000）。
            //   ⚠ 留着旧话比没有话更糟 —— 它会让人以为页面上的数不可信而绕开去用别的路径。
            "本页控件**没有**下面这几项，但它们已按定案值参与求解（界面上看不到）：\r\n" +
            $"   · 管孔渐变环 ×{FinalDesign.Fmt(fd.RingMul, "0.00")}" +
            (fd.RingMul[0] <= 1.001
                ? "（=1.00 即**不需要环**）\r\n"
                : $"，r ≤ 孔+{fd.RingWidthMm:0} 与 孔+{2 * fd.RingWidthMm:0} 两级\r\n") +
            $"   · 逐片舌保温 {FinalDesign.Fmt(fd.TabInsulMm, "0.0")} mm（守 ②′/③ 的主力旋钮）\r\n" +
            $"   · 压接段 {fd.ClampLengthMm:0} mm　舌根圆角 R{fd.TabFilletMm:0}　等宽舌片　管孔两面角焊缝\r\n" +
            "   ⇒ 现在点「核算整线」**就能**复现定案数字（与「▶ 复现定案」同一套几何）。\r\n" +
            "     两者的区别只剩：本按钮用页面上的水头，「复现定案」用内核默认值。";
        _suppressAuto = false;
    }

    /// <summary>
    /// ★★★ 出图前的拦截：**已声明失效的档一律不许出图**（2026-08-17）。
    ///
    /// 与 `--make3dm` 那条同根同源（用户当天发现新旧 3DM 一模一样）：
    /// 交付件不能是一个**自己声明不成立**的设计。而这条 UI 路径比 CLI 更危险 ——
    /// 下拉里作废档就排在现役档后面，隔一个位置，手一滑就选中了；
    /// 默认文件名又是 `定案_管壁0.8mm.3dm`，与现役档**一字不差**，
    /// 存到同一个目录就把好的那个盖掉，且**没有任何提示**。
    ///
    /// 单独抽成方法是为了能被界面接线测试直接调用（SaveFileDialog 是模态的，测不了）。
    /// </summary>
    public static string ExportBlockedReason(FinalDesign fd) =>
        fd.Invalid.Length == 0 ? "" :
        "★ 本档已声明失效，**不出图**。\r\n" + fd.Invalid + "\r\n" +
        $"（自由段 {fd.FreeTabMm:0.0} mm）\r\n\r\n" +
        "交付件不能是一个自己声明不成立的设计。要看它长什么样，请用「使用说明」页 —— " +
        "那里会连同失效原因一起画出来。";

    /// <summary>导出选中定案档的整机 3DM（子进程渲染 + 写完从磁盘回读自校）。</summary>
    private void ExportFinal3dm()
    {
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= FinalDesign.All.Length) return;
        var fd = FinalDesign.All[i];

        string blocked = ExportBlockedReason(fd);
        if (blocked.Length > 0) { _out.Text = blocked; return; }

        using var dlg = new SaveFileDialog
        {
            Filter = "Rhino 3DM|*.3dm",
            // 文件名带上档名：只按管壁命名时，两个同壁厚的档会写成同一个文件名而互相覆盖
            FileName = $"定案_管壁{fd.WallMm:0.0}mm_舌{fd.TabLengthMm:0}x{2 * fd.TabHalfWidthMm:0}.3dm"
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
    /// ⚠ 我此前对用户说的是「有两项表达不了」，那是**低估**。实测本页旧的 MakePlate
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
        // ⚠ 定案值一律**现取**，不写字面量。这里原来硬编码「×1.22–1.24」，
        //   而 2026-08-17 重解后定案的环倍率是 1.00（不需要环）—— 又一处会悄悄漂开的抄写。
        if (pg.DiscStepRadiiMm.Length == 0)
            miss.Add($"管孔两级渐变环（当前定案 ×{FinalDesign.Current.RingMul[0]:0.00}" +
                     (FinalDesign.Current.RingMul[0] <= 1.001 ? "，即**不需要环**）" : "）"));
        if (pg.WeldFilletLegMm <= 1e-9) miss.Add("管孔两面角焊缝（定案 焊脚 = max(板厚, 壁厚)）");
        if (double.IsNaN(pg.TabInsulThickMm)) miss.Add("逐片舌保温");
        if (!pg.TabParallel) miss.Add("等宽舌片（本页是**梯形**，定案是等宽）");
        if (pg.TabFilletMm <= 1e-9) miss.Add("舌根过渡圆角（峰值电流拥塞就在这个凹角上）");
        return miss.Count == 0 ? "" : string.Join("\r\n         · ", miss);
    }

    /// <summary>
    /// 1b 之后：解析模式解的**就是**定案那套几何，所以要报的不再是「表达不了什么」，
    /// 而是「**本页没有控件的那几项，这次实际用了什么值**」。
    ///
    /// 为什么必须报：这几项都会显著改变结果（舌保温是守 ②′/③ 的主力旋钮），
    /// 而它们在界面上看不见。看不见又在起作用的量，正是「安静失败」的温床 ——
    /// 与其藏起来，不如每次都摊开。
    /// </summary>
    private static string AnalyticUsedWhat(FinalDesign d) =>
        $"   · 压接段 {d.ClampLengthMm:0} mm（决定判据⑤ 自由段与舌片有效发热长度）\r\n" +
        $"   · 逐片舌保温 {FinalDesign.Fmt(d.TabInsulMm, "0.0")} mm（**守 ②′/③ 的主力旋钮**）\r\n" +
        $"   · 管孔渐变环 ×{FinalDesign.Fmt(d.RingMul, "0.00")}" +
        (d.RingMul[0] <= 1.001 ? "（=1.00 即不需要环）" : $"，环宽 {d.RingWidthMm:0} mm") + "\r\n" +
        $"   · 舌根圆角 R{d.TabFilletMm:0}　等宽舌片　管孔两面角焊缝（焊脚 = max(板厚, 壁厚)）\r\n" +
        $"   · 圆盘保温 {(d.FlangeInsulated ? $"{d.FlangeInsulMm:0} mm" : "不包")}（本页「法兰保温」控件）\r\n" +
        "   ⇒ 这几项本页没有控件；要改它们请用「自动定厚」/「◇ 搜形状」求解，" +
        "或改 Core/FinalDesign。";

    // ★★★ 这里原来有个 `MakePlate(double)` —— 本页自己造 FlangePlate 的那个方法。
    //   1b（2026-08-17）之后解析几何一律走 FinalDesign.Plate，它已经没有调用者。
    //
    //   **删掉而不是留着**：一个长得就像「几何构造器」的私有方法留在页面里，
    //   下一个人（包括我）要加功能时会顺手用它 —— 第二个几何来源就是这么长回来的。
    //   今天修的三条 bug 根都是「同一件事存了两处」，不能一边拆一边留个种子。
    //   要看它长什么样：git log 里有。

    /// <summary>
    /// ★★★★★ **1b：解析模式下，页面与内核共用同一个几何构造器**（2026-08-17）。
    ///
    /// 在此之前本页自己造 <c>FlangePlate</c>（旧的 MakePlate，已删），只填五个字段；
    /// 而 <see cref="FinalDesign.Plate"/> 还填渐变环、角焊缝、逐片舌保温、等宽舌片、舌根圆角。
    /// ⇒ 「核算整线」解的是**另一片法兰**，判据照实判，但那些数不能跟定案比。
    ///
    /// 「几何只有一个来源」这条铁律，在页面这里一直是破的。而 2026-08-17 一天里
    /// 抓到的三条 bug 根都是同一句：**同一件事存了两处**
    ///   · 压接段：页面用 3 mm 默认值，定案是 40（判据⑤ 因此判反）
    ///   · 3DM：作废档与现役档同名，把现役档整个覆盖
    ///   · 渐变环倍率：警告文字里硬编码「×1.22–1.24」，而定案早已是 1.00
    /// ⇒ 把页面这一处拆掉：解析几何一律走 <see cref="PageToFinalDesign"/> → <c>BuildCase</c>。
    ///
    /// ⚠ **行为会变**：同样的页面参数，「核算整线」的数会与以前不同（现在带环、带焊缝、
    ///   带舌保温）。这是**修正**不是回归 —— 以前那组数解的是一片不存在的法兰。
    ///   输出里会逐条列出本次实际用了什么值。
    ///
    /// ⚠ `.3dm` 那条路**保留旧路**：任意台阶几何 <c>FinalDesign</c> 表达不了。
    /// </summary>
    private LineCase BuildCase()
    {
        var rows = _segs.Where(s => !string.IsNullOrWhiteSpace(s.名称)).ToList();

        if (_srcAnalytic.Checked)
        {
            // ★ 与「自动定厚」「搜形状」「复现定案」走**同一个构造器**，不再另造一片
            var lcA = PageToFinalDesign().BuildCase(_base, checkRamp: true);
            // 水头是**操作条件**不是几何，FinalDesign 不带它 ⇒ 在这里补上（页面表格里有）
            lcA.HeadM = rows.Select(s => s.水头m).ToArray();
            return lcA;
        }

        var p = SegmentSolver.Clone(_base);
        p.WallMinMm = (double)_wall.Value;
        p.Layer1.ThicknessMm = (double)_tubeIns.Value;
        p.Layer1.Enabled = (double)_tubeIns.Value > 1e-6;
        p.FlangeInsulThickMm = _flIns.SelectedIndex == 0 ? 0 : (double)_flInsT.Value;
        p.FlangeInsulated = _flIns.SelectedIndex != 0;
        p.BusbarClampTempC = (double)_clamp.Value;
        // ★★★ BUG（2026-08-17 抓到）：本页从来没设过**压接段长度**，于是它一直用
        //   DesignInputs 的默认值 **3.0 mm** —— 而那个 3 mm 是 ShellMesh 自己注释里写明的
        //   「**数值边界不是设计值**」，定案用的是 40 mm。
        p.BusbarClampLengthMm = FinalDesign.Current.ClampLengthMm;

        var lc = new LineCase
        {
            Base = p,
            WallMm = (double)_wall.Value,
            UseMeasuredCurrent = false,          // 由控温反算 —— 第一性
            SetpointC = rows.Select(s => s.控温C).ToArray(),
            HeadM = rows.Select(s => s.水头m).ToArray(),
            CheckRamp = true,
        };
        {
            var files = _file3dm.Select(f => f.Text.Trim()).ToArray();
            if (files.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException("四片法兰的 .3dm 都要指定（可重复同一文件）");
            lc.FlangeFile3dm = files;
            lc.FlangeLayer = _layer3dm.Text.Trim();
            lc.ThicknessScale = _tPlate.Select(n => (double)n.Value).ToArray();
            // 0 ⇒ 传 NaN（裸舌，与从前一致）；> 0 才真的包保温
            double ti3 = (double)_tabIns3dm.Value;
            lc.TabInsul3dmMm = ti3 > 1e-9 ? ti3 : double.NaN;
            if (_levels is not null) lc.LevelThicknessMm = _levels;
            if (_levelScale is not null) lc.LevelScale = _levelScale;

            // ★ 把「分析几何变数」反推出的形状喂给 ⑤⑥（2026-08-23）。
            //   在此之前 .3dm 模式下这两条恒为「无法判定」，而无法判定不算通过
            //   ⇒ **.3dm 这条路永远解锁不了 ⑤ 交付**。可它们要的量
            //   （盘半径 / 管孔半径 / 舌端 X / 舌端半宽）分析时全都拿到了。
            //
            //   ⚠ 只喂 GeomForJudge，**不碰 FlangePlates** —— 后者是求解用的，
            //     .3dm 模式下求解走厚度场。塞进去就成了「判的是 A、解的是 B」。
            //   ⚠ 没分析过（_shape 为 null）就**不喂** ⇒ ⑤⑥ 仍报无法判定。
            //     那是诚实的：还没告诉过程序这张图长什么样。
            if (_shape is { } sh3)
            {
                // 厚度取各级里**最薄**的那一级：焊脚 = max(板厚, 壁厚)，
                // 而 ⑥ 是「盘在焊脚外还剩多少料」—— 取最薄片会给出**最宽松**的焊脚，
                // 所以这里反过来取**最厚**的一级，让 ⑥ 判在最严的那一侧。
                double tMax = sh3.Levels.Count > 0
                    ? sh3.Levels.Max(l => l.ThicknessMm) : (double)_wall.Value;
                var eq = new FlangePlate
                {
                    DiscRadiusMm = sh3.DiscRadiusMm,
                    HoleRadiusMm = sh3.HoleRadiusMm,
                    TabEndXMm = sh3.TabEndXMm,
                    TabEndHalfWidthMm = sh3.TabEndHalfWidthMm,
                    ThicknessMm = tMax,
                    TabParallel = true,
                    WeldFilletLegMm = Math.Max(tMax, (double)_wall.Value),
                };
                // 四片同图 ⇒ 四片同形。逐片各选各的 .3dm 时这里要跟着改。
                lc.GeomForJudge = new[] { eq, eq, eq, eq };
            }
        }
        return lc;
    }

    /// <summary>
    /// ★★★★★ 把本页控件读成一个 <see cref="FinalDesign"/>（2026-08-17）。
    ///
    /// 为什么需要：「自动定厚」原来调的是 <see cref="FlangeAutoSizer"/> —— 它**只有板厚一个旋钮**，
    /// 靶是 ③，而且它自己的注释就写着「管不到 ②′/②″」。
    /// 问题在于 ③ 与 ②′ 是**同一个抽热 D 的两侧**（实测 ③ = 2.40·D）：
    /// 把 ③ 往下压 = 把 D 往下压 = **把 ②′ 往负里推**，也就是往「热倒灌进管子」那个方向走
    /// —— 那正是现场烧断的机理。旧器只会在事后让判据表去说「②′ 没过」。
    ///
    /// ⇒ 改调 D8（<see cref="Sizer"/>）：舌保温守抽热窗口、环倍率守 ②″、板厚只做接力与省铂。
    ///
    /// ⚠ D8 工作在**定案那套完整几何**上（逐片舌保温、渐变环、等宽舌片、舌根圆角、角焊缝），
    ///   而本页控件表达不了其中几项（见 <see cref="PageVsFinal"/>）。
    ///   所以这里**明说**：自动定厚解的是完整构型，不是本页那片简化法兰。
    ///   与其让两套几何各解各的（那是「同一个数存两处」的老毛病），不如统一到 FinalDesign 这一套。
    /// </summary>
    private FinalDesign PageToFinalDesign()
    {
        var seed = FinalDesign.Current;
        var d = seed.Clone();
        d.Name = "本页参数";
        d.Provenance = "由「整线设计」页控件读入，D8 定尺寸";
        d.Binding = ""; d.Invalid = ""; d.InvalidChecks = Array.Empty<string>();
        d.WallMm = (double)_wall.Value;
        d.TubeInsulMm = (double)_tubeIns.Value;
        d.DiscRadiusMm = (double)_discD.Value * 0.5;
        d.TabLengthMm = (double)_tabLen.Value;
        d.TabHalfWidthMm = (double)_tabW.Value;
        d.ClampTempC = (double)_clamp.Value;
        d.ClampLengthMm = seed.ClampLengthMm;          // 本页无控件，取定案值（已在输出里注明）
        // 圆盘保温：本页**有**控件，接过去（BuildCase 里原来写死 20，已改成读字段）
        d.FlangeInsulated = _flIns.SelectedIndex != 0;
        d.FlangeInsulMm = d.FlangeInsulated ? (double)_flInsT.Value : 0;
        var rows = _segs.Where(s => !string.IsNullOrWhiteSpace(s.名称)).ToList();
        if (rows.Count > 0) d.SetpointC = rows.Select(s => s.控温C).ToArray();
        // 起点：板厚用页面上的值（起点只影响轮数，不影响解 —— 每个旋钮对自己的靶单调）
        for (int i = 0; i < d.TabThickMm.Length && i < _tPlate.Length; i++)
            d.TabThickMm[i] = (double)_tPlate[i].Value;
        return d;
    }

    /// <summary>
    /// ★★★★★ **形状搜索**（盘半径 × 舌半宽），舌长按装配算，逐个形状交给 D8 定尺寸。
    ///
    /// 用户第 2 项要的就是这个：「APP 不能**自动**改变法兰盘直径与舌片长度吗？」
    ///
    /// 三条设计决定：
    ///  ① **舌长不参与搜索**。它 = 圆盘切点 + 压接段 + 自由段下界，是装配的因变量；
    ///     加长只多花铂多发热 ⇒ 最优解永远贴着下界。真正的维度只有 盘半径 × 舌半宽。
    ///  ② **两段式**：先用少轮数把网格筛一遍（看谁有解、谁大概轻），
    ///     再只对胜出的那个形状跑足轮数。全网格都跑足轮数是纯浪费。
    ///  ③ **每个形状算完立刻把结果贴进输出框**，不等全部跑完 ——
    ///     几十分钟的任务如果只在最后才出东西，中途取消就等于全白跑。
    ///
    /// 进度用**确定式**进度条（分母 = 形状数 × 轮数），不是转圈：
    /// 转圈只说明「还活着」，说不出「还要多久」。
    /// </summary>
    private async Task SearchShapeAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }        // 再点一次 = 取消
        if (!_srcAnalytic.Checked)
        {
            _out.Text = "「搜形状」只在**解析几何**模式下可用。\r\n" +
                        ".3dm 模式下形状由图纸给定，不是可搜索的自由度 —— " +
                        "要搜形状请先切回「解析（圆盘+舌片）」。";
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnShape.Text = "取消";
        _btnRun.Enabled = _btnAuto.Enabled = _btnRepro.Enabled = false;
        Shared?.SetRunning(ChainId.C形状搜索, "搜形状");

        // 网格：盘半径 × 半宽比例。半宽 > 盘半径没有切点（等宽舌片与圆盘接不上），故按比例取。
        double[] discs = { 25, 30, 35 };
        double[] wFrac = { 0.75, 1.00 };
        const int screenRounds = 16, finalRounds = 40;
        int total = discs.Length * wFrac.Length * screenRounds + finalRounds;
        int done = 0;

        _prog.Visible = true; _prog.Style = ProgressBarStyle.Continuous;
        _prog.Maximum = total; _prog.Value = 0;

        // ★★★★★ 进度要往**状态面板**报，不能只报给本页的 _prog/_status（2026-08-24）。
        //
        // 2026-08-21 定过：不给每一页各配一套进度条（那是「同一件事多处表达」），
        // 改成右上角状态面板 —— 它切到哪一页都看得见。SetRunningNote 就是那个通道。
        // 可这条链**从来没往它写过一个字**：开跑时 SetRunning(…, "搜形状") 之后再无更新。
        // 后果：搜形状要几十分钟，而它是从 ④ 页点的，④ 上没有 _prog 也没有 _status
        // ⇒ 面板上那句「正在算：C″ 形状搜索 搜形状」几十分钟纹丝不动，
        //   既看不出还活着、也看不出到哪了。**又一个「接了一半」，而且漏的偏是最长的那条。**
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Note(string s2)
        {
            _status.Text = s2;
            int pct = _prog.Maximum > 0 ? 100 * _prog.Value / _prog.Maximum : 0;
            Shared?.SetRunningNote($"已用 {clock.Elapsed.TotalMinutes:0.0} 分　{s2}", pct);
        }
        Note("准备网格…");

        var sb = new StringBuilder();
        sb.AppendLine("=== 搜形状（盘半径 × 舌宽；舌长按装配算）===");
        sb.AppendLine($"网格 {discs.Length}×{wFrac.Length} 个形状，先各筛 {screenRounds} 轮，再对胜出者跑 {finalRounds} 轮。");
        sb.AppendLine($"自由段下界 {FreeTabMin:0} mm（判据⑤）　压接段 {FinalDesign.Current.ClampLengthMm:0} mm");
        sb.AppendLine("★ 舌长不是搜出来的，是**算出来的**：切点 + 压接段 + 自由段。");
        sb.AppendLine("随时可以点「取消」——**已经算完的形状结果不会丢**。");
        sb.AppendLine();
        // ⚠ 表头必须是 sb 的**最后一行**，后面不能垫空行：下面的数据行是随算随
        //   AppendText 贴上来的，只有与表头**连续**才会被认成同一张表；
        //   一旦断开，表头和数据各自算各自的列宽，就再也对不上了。
        sb.AppendLine("盘Ø\t舌宽\t舌长\t合计 g\t判定");
        _out.Text = sb.ToString();

        var rows = new List<(FinalDesign d, double mass, bool ok, string msg)>();
        try
        {
            foreach (double R in discs)
                foreach (double f in wFrac)
                {
                    ct.ThrowIfCancellationRequested();
                    // ★ 早筛「造不出来」的盘径（判据⑥ 会兜底，但那要先白跑十几轮）。
                    //   焊脚 = max(板厚, 壁厚) ≥ 壁厚 ⇒ 盘半径至少要 孔半径 + 壁厚 = 25 + 2×壁厚。
                    //   2026-08-17 实测：盘 R25 + 管壁 0.8 时孔半径 25.8 > 盘半径，孔比盘还大，
                    //   而这种几何**料最少**，不拦住它就会排在最前面。
                    double minDisc = 25.0 + 2 * (double)_wall.Value;
                    if (R < minDisc - 1e-9)
                    {
                        done += screenRounds; _prog.Value = Math.Min(_prog.Maximum, done);
                        Note($"跳过 盘Ø{2 * R:0}（判据⑥ 早筛）");
                        _out.AppendText($"{2 * R:0}\t—\t—\t—\t" +
                            $"跳过：管壁 {(double)_wall.Value:0.0} 时盘半径至少要 {minDisc:0.0}（判据⑥）\r\n");
                        continue;
                    }
                    double hw = R * f;
                    var seed = PageToFinalDesign();
                    seed.DiscRadiusMm = R;
                    seed.TabHalfWidthMm = hw;
                    seed.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw))
                                       + seed.ClampLengthMm + FreeTabMin;
                    string tag = $"盘Ø{2 * R:0}／舌宽{2 * hw:0}";
                    int baseDone = done;
                    var prog2 = new Progress<string>(s =>
                    {
                        // Sizer 每轮吐一行；用行首的轮号推进度条
                        if (s.Length > 4 && int.TryParse(s.AsSpan(0, 4).Trim(), out int rd))
                            _prog.Value = Math.Min(_prog.Maximum, baseDone + rd);
                        Note($"{tag}　" + s.Split('\n')[0]);
                    });
                    var sr = await Task.Run(() => Sizer.Solve(seed, _base,
                                 new SizerOptions { MaxRounds = screenRounds }, prog2, ct), ct);
                    done = baseDone + screenRounds;
                    _prog.Value = Math.Min(_prog.Maximum, done);
                    Note($"{tag} 已完成　{(double.IsNaN(sr.MassG) ? "无解" : sr.MassG.ToString("0") + " g")}");
                    rows.Add((sr.Design, sr.MassG, sr.Feasible, sr.Message));
                    // ★ 算完一个贴一个：中途取消也留得住已有结果
                    _out.AppendText(
                        $"{2 * R:0}\t{2 * hw:0}\t{sr.Design.TabLengthMm:0}\t" +
                        (double.IsNaN(sr.MassG) ? "—" : sr.MassG.ToString("0")) +
                        $"\t{(sr.Feasible ? "✓ " : "")}{sr.Message}\r\n");
                }

            var win = rows.Where(x => x.ok && !double.IsNaN(x.mass))
                          .OrderBy(x => x.mass).FirstOrDefault();
            if (win.d is null)
            {
                _out.AppendText("\r\n★ 本网格里**没有全过的形状**。上面每行的失败原因已逐条列出，" +
                                "据此扩网格（改盘径范围）或松工艺（管壁、控温点）。\r\n");
                _status.Text = "无解";
                return;
            }

            Note("精算胜出形状…");
            var fin = await Task.Run(() => Sizer.Solve(win.d, _base,
                          new SizerOptions { MaxRounds = finalRounds },
                          new Progress<string>(s =>
                          {
                              if (s.Length > 4 && int.TryParse(s.AsSpan(0, 4).Trim(), out int rd))
                                  _prog.Value = Math.Min(_prog.Maximum, done + rd);
                              Note("精算　" + s.Split('\n')[0]);
                          }), ct), ct);
            _prog.Value = _prog.Maximum;

            // 把胜出形状写回控件（这是「自动改变盘径与舌长」真正落地的地方）
            decimal C(double v, NumericUpDown n) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);
            _suppressAuto = true;
            _discD.Value = C(2 * fin.Design.DiscRadiusMm, _discD);
            _tabW.Value = C(fin.Design.TabHalfWidthMm, _tabW);
            _tabLen.Value = C(fin.Design.TabLengthMm, _tabLen);
            for (int i = 0; i < _tPlate.Length && i < fin.Design.TabThickMm.Length; i++)
                _tPlate[i].Value = C(fin.Design.TabThickMm[i], _tPlate[i]);
            _suppressAuto = false;
            _last = fin.Best;

            // 形状体检：搜出来的赢家也要说清楚它好在哪、代价在哪
            _out.AppendText("\r\n" + ShapeReview.Build(fin.Design, fin.Best,
                                                       FinalDesign.Current, fin.Message));
            _out.AppendText("\r\n★ **最轻的全过形状**（已写回上面的盘径/舌宽/舌长/板厚）\r\n" +
                $"   盘Ø{2 * fin.Design.DiscRadiusMm:0}／舌 {fin.Design.TabLengthMm:0}×{2 * fin.Design.TabHalfWidthMm:0}" +
                $"／自由段 {fin.Design.FreeTabMm:0.0} mm\r\n" +
                $"   板厚 {FinalDesign.Fmt(fin.Design.TabThickMm, "0.00")}" +
                $"　舌保温 {FinalDesign.Fmt(fin.Design.TabInsulMm, "0.0")}" +
                $"　环倍率 {FinalDesign.Fmt(fin.Design.RingMul, "0.00")}\r\n" +
                $"   合计 {fin.MassG:0} g　{fin.Message}\r\n\r\n" +
                "   ⚠ **舌保温与环倍率本页没有控件**，但它们是解的一部分（舌保温还是守 ②′/③ 的主力）。\r\n" +
                "     要照这组数出图，请把上面三行抄进 Core/FinalDesign 再走「导出定案 3DM」。\r\n" +
                "   ⚠ 筛选只跑了 " + screenRounds + " 轮，**是粗筛**：名次靠前几名接近时，" +
                "把它们各自再跑一次足轮数才算数。\r\n");
            _status.Text = "完成";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消";
            _out.AppendText("\r\n（已取消。上面已经算完的形状结果仍然有效。）\r\n");
        }
        catch (Exception ex)
        {
            _status.Text = "失败";
            _out.AppendText("\r\n✗ " + ex.Message + "\r\n");
        }
        finally
        {
            _prog.Visible = false; _prog.Style = ProgressBarStyle.Marquee;
            _btnShape.Text = "◇ 搜形状";
            _btnRun.Enabled = _btnAuto.Enabled = _btnRepro.Enabled = true;
            _cts?.Dispose(); _cts = null;
            Shared?.SetRunning(null);
        }
    }

    /// <param name="autoSize">true = 「自动定厚」，false = 「核算整线」</param>
    /// <param name="byTimer">true = 防抖定时器自动触发（用户可能已经走开）。
    /// ⚠ 与 <paramref name="autoSize"/> 是两回事，别混：前者说**做什么**，后者说**谁点的**。
    /// 只有「谁点的 = 用户」时才允许弹模态框。</param>
    private async Task RunAsync(bool autoSize, bool byTimer = false)
    {
        if (_cts is not null) { _cts.Cancel(); return; }        // 再点一次 = 取消
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnRun.Enabled = !autoSize; _btnAuto.Enabled = autoSize;
        (autoSize ? _btnAuto : _btnRun).Text = "取消";
        _prog.Visible = true; _prog.Style = ProgressBarStyle.Marquee;
        _status.Text = autoSize ? "自动定厚中…" : "核算中…";
        // ★ 「自动定厚」在 ④ 页，而进度条与状态标签都长在 ③ 上 ⇒ ④ 那边一动不动。
        //   接上状态面板（右上角，切到哪一页都看得见）才有动态提示。
        Shared?.SetRunning(autoSize ? ChainId.C定尺寸 : ChainId.C整线耦合,
                           autoSize ? "自动定厚" : "核算整线");

        // ⚠⚠ BuildCase() **必须在 try 里面**。它会抛（如「.3dm 模式但文件没填」）——
        //   放在外面时异常越过 finally ⇒ _cts 不清、按钮不恢复、进度条一直转，
        //   而且此后 TryAutoRun 每次都撞上「_cts 不为 null」而无限重排定时器
        //   ⇒ **自动重算从此永久死掉，且一声不吭**。
        //   实测复现：点一下「Rhino .3dm 文件」单选钮（还没填文件）就中招。
        LineCase lc;
        var prog = new Progress<string>(s => { _status.Text = s; Shared?.SetRunningNote(s); });

        // ★★★★★ 装配下界要在**求解路径上**也顶一次（2026-08-24）。
        //
        // EnforceTabLenFloor 此前只挂在 ShowPrediction 里，也就是只在
        // 「用户手改了参数」那条路上跑。而任何绕过 ParamChanged 的写入都躲得过它：
        // _suppressAuto 期间的程序写值、首屏那一阵、载入档…
        // ⇒ 一个自由段为负的几何照样能进求解器。
        //
        // 判据本身是**对的**：⑤ 会红（实测见过 −12.4/100），没有放行。
        // 但那要等一次分钟级的解，而这件事是闭式的、一毫秒就知道 ——
        // 让人白等几分钟才被告知「压接块伸进圆盘里了」，是**能省而没省**的代价。
        //
        // ⚠ 只在解析模式顶：.3dm 模式下盘径/舌长/舌宽是禁用的残值，顶它没有意义
        //   （那条路的几何来自图纸，⑤ 由 GeomForJudge 判）。
        string floorNote = _srcAnalytic.Checked ? EnforceTabLenFloor() : "";

        // ★★★★★ 快照必须取**开解这一刻**，不能等解完再取（2026-08-24）。
        //
        // 原来是解完之后 `_solvedSnap = CurrentSnap()` —— 读的是**那时候**的控件。
        // 整线解要几分钟，这几分钟里控件是可以改的（页签也能切）。改了之后：
        //   · r 是用**改之前**那组参数算的
        //   · _solvedSnap 记的是**改之后**那组
        //   ⇒ Fresh = Equals(SolvedSnap, CurrentSnap) 判成 **true**
        //   ⇒ 界面写「✓ 已解（参数未变）」，④⑤ 两道 RequireFresh 的门照开 ——
        //     而那张判据表根本不是这组参数的结论。**假绿灯，且言之凿凿。**
        //
        // 取开解时刻就没有这个缝：解完再比，参数动过就是不新鲜，指路会说
        // 「参数在上次求解之后又动过了 —— 回 ③ 按现在这组重解」。那是实话。
        var snapAtStart = CurrentSnap();

        try
        {
            lc = BuildCase();
            if (autoSize)
            {
                // ★★★ .3dm 模式**没有分级**时必须**当场拒绝**，不许落进下面的 D8
                //   （2026-08-21 用户看出来的）。原来这里是静默回退，后果不是
                //   「换了个算法」，是**算了另一个零件**：
                //     · D8 走 `Sizer.Solve(PageToFinalDesign(), …)`，
                //       上面刚从 .3dm 造好的 `lc` **一眼都没看** ⇒ 图纸被静默丢弃；
                //     · 盘径/舌长/舌半宽在 .3dm 模式下是**禁用**的，里面是上次的残值 ——
                //       D8 拿这组残值当几何去优化；
                //     · `_tPlate` 在 .3dm 模式下含义是**厚度标度 k**，D8 当**毫米**读、
                //       算完又把毫米数写回去 ⇒ 标度字段被覆盖坏。
                //   而输出照旧是「【D8 定尺寸】板厚 … 合计 … g」，看起来完全正常。
                if (!_srcAnalytic.Checked && !(_levels is { Length: > 0 } && _levels[0].Length > 1))
                {
                    // ⚠ 用 Environment.NewLine 拼，不写反斜杠转义 ——
                    //   本仓的钩子会把转义序列改成真字符，字面量当场断掉。
                    string nl = Environment.NewLine;
                    Show(_last, autoNote:
                        "【自动定厚：已拒绝】本页是 **Rhino .3dm 模式**，但还没有分级厚度。" + nl
                        + "   请先点「**分析几何变数**」把图纸反推成各级台阶，再回来定厚。" + nl
                        + "   ⚠ 不能替你用 D8：D8 优化的是**解析形状**（圆盘＋舌片），" + nl
                        + "      它不读你的 .3dm，盘径/舌长在本模式下又是禁用的残值 ——" + nl
                        + "      那样算出来的是**另一个零件**的厚度，数字却看不出异样。");
                    return;
                }

                if (!_srcAnalytic.Checked)
                {
                    // 逐级定厚（.3dm 任意形状）：D8 只在解析几何上工作，管不了任意台阶，
                    // 所以这条路仍用 FlangeAutoSizer。
                    // ⚠ 它**只有板厚一个旋钮**、靶是 ③，管不到 ②′/②″（它自己的注释写着）。
                    //   ⇒ 用完必须看判据表，尤其 ②′ 净流入是不是仍为正。
                    var lvl = _levels;
                    var lockMask = LockedMask();

                    // ★ 解析替身：量过、且量出来够像，才拿它搜方向（每次评估从分钟级变毫秒级）。
                    //   量不过就传 null —— 回到逐次重算厚度场那条慢路，慢总比算错强。
                    //   ⚠ 不论走哪条，SolveByLevel 的**全精度复核都回到原图纸**，
                    //     所以报出去的数始终是图纸的数（见该处注释）。
                    Func<double[], FlangePlate>? mkLevel = null;
                    if (_shape is { } shp && _surrFid is { } fid && AnalyticSurrogate.Usable(fid))
                    {
                        bool par = _surrTabParallel;
                        mkLevel = th => AnalyticSurrogate.Build(shp, th, par);
                    }
                    var r = await Task.Run(() => FlangeAutoSizer.SolveByLevel(
                        lc, lvl, new FlangeAutoSizer.Options(), prog, ct, 6, lockMask, mkLevel), ct);
                    _levelScale = r.LevelScale;
                    // ⚠ 这是**程序**在把刚解出来的厚度写回控件。不闭掉自动重算的话，
                    //   「自动定厚」一结束就会立刻再排一次整线重算 —— 算的还是它自己刚给的答案。
                    _suppressAuto = true;
                    for (int i = 0; i < _tPlate.Length && i < r.ThicknessMm.Length; i++)
                        _tPlate[i].Value = (decimal)Math.Clamp(r.ThicknessMm[i], 0.1, 8.0);
                    _suppressAuto = false;
                    _last = r.Line;
                    Show(r.Line, autoNote: r.Message + (r.Converged ? "" : "　⚠ 未收敛，下面的数不可引用") +
                        "\r\n   ⚠ 本器**只调板厚**，管不到 ②′ 净流入与 ②″ 圆盘峰 —— 请自行看判据表。" + floorNote);
                }
                else
                {
                    // ★★★ 解析几何走 **D8**（Core/Sizer）。旧的 FlangeAutoSizer 只有板厚一个旋钮、
                    //   靶是 ③，而 ③ 与 ②′ 是同一个抽热的两侧 ⇒ 它把 ③ 压下去的同时
                    //   把 ②′ 往负里推（热倒灌进管 = 烧断机理），且它自己管不到 ②′。
                    //   D8 用舌保温守抽热窗口、环倍率守 ②″、板厚只做接力与省铂。
                    var seedD8 = PageToFinalDesign();
                    var srD8 = await Task.Run(() => Sizer.Solve(seedD8, _base,
                                   new SizerOptions { MaxRounds = 40 }, prog, ct), ct);
                    _suppressAuto = true;
                    for (int i = 0; i < _tPlate.Length && i < srD8.Design.TabThickMm.Length; i++)
                        _tPlate[i].Value = (decimal)Math.Clamp(srD8.Design.TabThickMm[i], 0.1, 8.0);
                    // 舌长可能被装配下界顶高（D8 不动它，但页面上要跟着显示）
                    _suppressAuto = false;
                    _last = srD8.Best;
                    _pendingReview = ShapeReview.Build(srD8.Design, srD8.Best,
                                                       FinalDesign.Current, srD8.Message);
                    Show(srD8.Best, autoNote:
                        "【D8 定尺寸】" + srD8.Message + "\r\n" +
                        $"   板厚 {FinalDesign.Fmt(srD8.Design.TabThickMm, "0.00")}" +
                        $"　舌保温 {FinalDesign.Fmt(srD8.Design.TabInsulMm, "0.0")}" +
                        $"　环倍率 {FinalDesign.Fmt(srD8.Design.RingMul, "0.00")}" +
                        $"　合计 {srD8.MassG:0} g\r\n" +
                        "   ⚠ **只有板厚写回了本页控件** —— 舌保温与环倍率本页没有控件，\r\n" +
                        "     但它们是解的一部分（舌保温还是守 ②′/③ 的主力旋钮）。\r\n" +
                        "     要照这组数出图，请把上面三行抄进 Core/FinalDesign 再走「导出定案 3DM」。\r\n" +
                        $"   ⚠ 本次解的是**定案那套完整几何**（含渐变环/角焊缝/等宽舌片/舌根圆角），\r\n" +
                        $"     不是本页那片简化法兰 —— 压接段取定案值 {seedD8.ClampLengthMm:0} mm。");
                }
            }
            else
            {
                var r = await Task.Run(() => LineRunner.Run(lc, prog, ct), ct);
                r = await RetryIfJustSlowAsync(r, lc, prog, ct, interactive: !byTimer);
                _last = r;
                // ★ 只有**真收敛**的解才配当外推基准。拿没收敛的解做基准，
                //   预测会看着很稳而其实一路偏 —— 那正是今天那个假收敛的形状。
                if (r.Ok && r.Converged) { _solvedRes = r; _solvedSnap = snapAtStart; }
                PushFlow();
                Show(r, autoNote: floorNote);
                // ★ 把「本次实际解的是什么」打出来。看不见又在起作用的量是安静失败的温床。
                if (_srcAnalytic.Checked && lc.FlangePlates is { Length: > 0 })
                {
                    string miss = PageVsFinal(lc.FlangePlates[0]);
                    if (miss.Length > 0)
                        // 1b 之后正常不该再走到这里；留着是**兜底告警** ——
                        // 万一哪天构造器又被绕过去，这一段会立刻喊出来。
                        _out.Text +=
                            "\r\n── ⚠⚠ 本次解的**不是**定案那套几何（1b 之后不应出现）\r\n" +
                            "   缺了：\r\n         · " + miss +
                            "\r\n   ⇒ 说明有人绕过了 FinalDesign.Plate 这个唯一构造器，请查 BuildCase。\r\n";
                    else
                        _out.Text +=
                            "\r\n── 本次解的是**定案那套完整几何**（与「复现定案」同一个构造器）\r\n" +
                            AnalyticUsedWhat(PageToFinalDesign()) + "\r\n";
                }
            }
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            // ⚠ 自动触发的那次**不弹模态框**：用户可能只是点了个单选钮就走开，
            //   回来看到一个卡住整个界面的弹窗，比看到一行说明糟得多。
            if (byTimer)
                _out.Text += "\r\n── ✗ 这组参数解不出来：" + ex.Message +
                             "\r\n   （改好之后会自动再试；也可以手动点「核算整线」）\r\n";
            else
                MessageBox.Show(this, ex.Message, "求解失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _prog.Visible = false;
            _btnRun.Enabled = _btnAuto.Enabled = true;
            _btnRun.Text = "核算整线"; _btnAuto.Text = "自动定厚";
            Shared?.SetRunning(null);          // 清在 finally：异常/取消也必须解除互斥
        }
    }

    /// <summary>
    /// 待插入的形状体检报告（<see cref="ShapeReview"/>）。由定尺寸器那条路设置，
    /// <see cref="Show"/> 取用后清空 —— 放在**判定之后、明细之前**，
    /// 那是用户读完「过没过」之后最想知道「为什么、代价是什么」的位置。
    /// </summary>
    private string _pendingReview = "";

    /// <summary>
    /// ★★★★★ 判据表改成**真表格**（2026-08-18，用户：「类似 Excel 也行」）。
    ///
    /// 之前它是 RichTextBox 里的一段文字，靠补空格 / 制表位对齐。两条路都失败了，
    /// 而失败的原因是同一个：**判据名的宽度差得太远**
    /// （「① 升温 空管到目标」vs「② 法兰最高温 − 管温（整片，含舌片）」），
    /// 一旦超过预留宽度就把后面所有列推走 —— 实测同一张表里数值列落在 x≈430 / 460 / 700 三处。
    /// 再加上长注释混在同一条流里、数字与单位被折行拆开（「1768」「°C」分两行）。
    ///
    /// ⇒ 列宽该由**控件**去算，不该由我去猜。DataGridView 天然做到：
    ///   列宽自适应、行可上色（不过的标红）、可选可复制、注释挂在 ToolTip 上不占版面。
    ///   散文（结论、下一步、体检报告、逐段明细）留在下面的文本框里，各归其位。
    /// </summary>
    private readonly DataGridView _checks = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        ShowCellToolTips = true,
        BackgroundColor = Color.FromArgb(252, 252, 250),
        BorderStyle = BorderStyle.None,
        EnableHeadersVisualStyles = false,
    };

    /// <summary>
    /// 把一段长说明按**显示宽度**折行，并在中文标点后优先断开。
    ///
    /// 为什么要有它：判据的 Note 现在带着「【下一步】…」的动作说明，动辄两三百字。
    /// 直接一行灌进去，靠控件自动换行，结果是一堵没有缩进层次的墙 ——
    /// 用户看到的就是「文挡好乱」。折过行、缩进一层之后，判据表才重新变成一张**表**。
    /// </summary>
    private static List<string> WrapNote(string s, int width)
    {
        var outp = new List<string>();
        var cur = new StringBuilder();
        int w = 0;
        foreach (char c in s)
        {
            cur.Append(c); w += TextFmt.CharWidth(c);
            bool breakable = c is '。' or '；' or '，' or '）' or '：' or ' ';
            if (w >= width && breakable) { outp.Add(cur.ToString().TrimEnd()); cur.Clear(); w = 0; }
            else if (w >= width + 24) { outp.Add(cur.ToString().TrimEnd()); cur.Clear(); w = 0; }
        }
        if (cur.Length > 0) outp.Add(cur.ToString().TrimEnd());
        // 收尾：不让一行以标点**开头**（断在「）」之后会把紧跟的「。」甩到下一行行首）
        for (int i = 1; i < outp.Count; i++)
            while (outp[i].Length > 0 && outp[i][0] is '。' or '；' or '，' or '：' or '、' or '）')
            { outp[i - 1] += outp[i][0]; outp[i] = outp[i][1..].TrimStart(); }
        outp.RemoveAll(string.IsNullOrWhiteSpace);
        return outp;
    }

    /// <summary>判据表的列。列宽交给控件自适应 —— 这正是换成表格的意义。</summary>
    private void InitChecksGrid()
    {
        _checks.Font = UiScale.Ui();
        _checks.ColumnHeadersDefaultCellStyle.Font = UiScale.Ui(FontStyle.Bold);
        _checks.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 236);
        _checks.RowTemplate.Height = UiScale.S(22);
        _checks.Columns.AddRange(
            new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "类别", FillWeight = 8 },
            new DataGridViewTextBoxColumn { Name = "name", HeaderText = "判据", FillWeight = 34 },
            new DataGridViewTextBoxColumn { Name = "act", HeaderText = "实际", FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "lim", HeaderText = "限值", FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "mg", HeaderText = "裕度", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "ok", HeaderText = "判定", FillWeight = 8 },
            new DataGridViewTextBoxColumn { Name = "where", HeaderText = "位置", FillWeight = 16 });
        // 数字列右对齐 —— 表格能做到「真右对齐」，这是纯文本做不到的
        foreach (var c in new[] { "act", "lim", "mg" })
            _checks.Columns[c]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        foreach (var c in new[] { "kind", "ok" })
            _checks.Columns[c]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
    }

    /// <summary>把判据填进表格。**只读 Judge 的结果**，不在这里重算任何判定。</summary>
    /// <summary>
    /// 阶段轨共享的运行时状态（③④⑤ 共用）。由 MainForm 注入；为 null 时本页照常工作。
    /// </summary>
    internal FlowState? Shared { get; set; }

    /// <summary>
    /// 把本页的当前状态推给阶段轨。**只搬运，不计算判据** ——
    /// ⑤⑥ 一律经 <see cref="GeometryScreen"/> 出，与整线解跑的是同一段代码。
    /// </summary>
    private void PushFlow()
    {
        if (Shared is not { } f) return;
        f.CurrentSnap = CurrentSnap();
        f.SolvedSnap = _solvedSnap;
        f.Last = _last;

        // 几何闭式判据：解析模式才有解析量；.3dm 模式下 GeometryScreen 会返回两条「无法判定」
        try
        {
            var plates = _srcAnalytic.Checked
                ? PageToFinalDesign().BuildCase(_base, checkRamp: false).FlangePlates
                : System.Array.Empty<FlangePlate>();
            f.GeomScreen = GeometryScreen.Judge(
                plates, FinalDesign.Current.ClampLengthMm, FreeTabMin);
        }
        catch { /* 几何还没填全（例如 .3dm 没选文件）时不该把界面拖垮 */ }

        f.Notify();
    }

    // ── 归 ④「定尺寸」与 ⑤「交付」两格托管的命令按钮。
    //    本页仍是它们的**所有者**（跑起来改文字、互相禁用的逻辑都在 RunAsync 里），
    //    ④⑤ 只是把它们挂到自己的工具条上。⇒ 状态只有一份。
    internal ToolStripButton BtnAutoThick => _btnAuto;
    internal ToolStripButton BtnSearchShape => _btnShape;
    internal ToolStripButton BtnExportPage3dm => _btnExport;
    internal ToolStripButton BtnExportFinal3dm => _btn3dm;
    // ── 定案档那一组（2026-08-23 从 ③ 拆到独立页）。控件仍归本页所有 ——
    //    它们要读写本页的控件（载入=灌值、另存=读当前解），换个地方摆而已。
    internal ToolStripButton BtnReproduce => _btnRepro;
    internal ToolStripButton BtnLoadCase => _btnLoadCase;
    internal ToolStripComboBox CaseBox => _caseBox;
    internal ToolStripButton BtnSaveFinal => _btnSaveFinal;

    /// <summary>④⑤ 页要显示「③ 解出来的是什么」，需要读这一份状态。</summary>
    internal LineResult? LastResult => _last;

    /// <summary>
    /// 上一次的解已作废 —— 判据表清空、外推基准丢弃、输出框说明缘由。
    ///
    /// ★ 2026-08-20：给「读取方案」用。换了方案之后，判据表若还挂着旧方案的结论，
    ///   而参数表已经是新方案，就是一张**看起来完全正常的错表**。
    ///   判据宁可消失得很吵，也不能安静地留在那儿骗人。
    /// </summary>
    internal void InvalidateSolution()
    {
        _last = null;
        _solvedRes = null;
        _solvedSnap = null;
        FillChecks(null);
        Shared?.Invalidate();
        _out.Text = "读取了新方案 —— 上一次的解与判据表**已作废**。\r\n"
                  + "请点「核算整线」重新求解。\r\n";
    }

    private void FillChecks(LineResult? r)
    {
        _checks.Rows.Clear();
        if (r is null || !r.Ok) return;
        foreach (var c in r.Checks)
        {
            string kind = c.Kind == CheckKind.HardSafety ? "硬"
                        : c.Kind == CheckKind.Target ? "目标" : "参考";
            string act = double.IsNaN(c.Actual) ? "达不到" : c.Actual.ToString("0.000");
            string lim = Math.Abs(c.Limit) < 1e-9 ? "> 0" : c.Limit.ToString("0.000");
            string mg = "—";
            if (c.Kind != CheckKind.Reference && !double.IsNaN(c.Actual))
            {
                // 裕度只有一处来源：ConstraintOut.MarginPct（它看方向，见那里的说明）
                double pct = c.MarginPct;
                if (!double.IsNaN(pct))
                    mg = pct >= 0 ? $"{pct:0} %" : $"超 {-pct:0} %";
                else mg = SizerResult.Signed(c.Actual - c.Limit, "+0.00;−0.00");
            }
            string vd = c.Kind == CheckKind.Reference ? "—" : c.Undetermined ? "?" : c.Ok ? "✓" : "✗";

            // 判据名里那个手写的「· 」前缀是给纯文本用的，表格里有「类别」列了，去掉
            string nm = c.Name.StartsWith("· ", StringComparison.Ordinal) ? c.Name[2..] : c.Name;
            int i = _checks.Rows.Add(kind, nm, act, lim, mg, vd, c.Where);
            var row = _checks.Rows[i];
            // 行上色：不过=淡红、无法判定=淡黄、参考量=灰字。颜色只是**重复**判定，不产生判定。
            if (c.Kind == CheckKind.Reference)
                row.DefaultCellStyle.ForeColor = Color.FromArgb(120, 120, 120);
            else if (c.Undetermined)
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
            else if (!c.Ok)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 226, 226);
                row.DefaultCellStyle.Font = UiScale.Ui(FontStyle.Bold);
            }
            // 长注释挂 ToolTip：既不占版面，也不会把数字和单位折成两行
            if (!string.IsNullOrWhiteSpace(c.Note))
                foreach (DataGridViewCell cell in row.Cells)
                    cell.ToolTipText = TextFmt.Strip(c.Note);
        }
    }

    private void Show(LineResult? r, string? autoNote = null)
    {
        // ★★★★★ 判据表**必须在这里填**（2026-08-20 复核抓到：以前一次都没填过）。
        //
        // 判据表从纯文字改成 DataGridView 之后，文字版被删掉了，
        // 下面的报告改说「判据表见上方表格」—— 可 <see cref="FillChecks"/> **没有任何调用者**，
        // 那张表**永远是空的**。于是界面在指着一张空表说「判据在那儿」，
        // 比不给判据更糟：人会以为「没有行 = 没有不过的」。
        //
        // ⚠ 三个提前 return 的分支（无结果 / 未 Ok）也要先清表，
        //   否则上一次的判据会**留在屏幕上冒充这一次的**。所以这一行放在最前面。
        FillChecks(r);

        if (r is null) { _out.Text = "无结果"; return; }
        var sb = new StringBuilder();
        if (autoNote is not null) sb.AppendLine("【自动定厚】" + autoNote).AppendLine();
        if (!r.Ok) { _out.Text = sb + "✗ " + r.Message; return; }

        if (!r.Converged)
            sb.AppendLine("╔══ ⚠ 段↔法兰耦合未收敛 —— 以下所有数值均不可引用 ══╗").AppendLine();

        // ★★★ **结论与下一步先写**（用户第 4 项：不看说明书也能用）。
        //
        // 判据表在下面几十行外，而人是从上往下读的。原来第一屏是段/法兰的数值表，
        // 「过没过」「接下来该动哪个旋钮」要自己往下翻、翻到了还要自己翻译成动作。
        // ⇒ 把**判定**和**最该先做的那一件事**顶到最前面；细节留在原位不动。
        if (r.Converged)
        {
            var bads = r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target
                                        && (!c.Ok || c.Undetermined))
                               // 硬安全线优先，其次超限最狠的
                               .OrderBy(c => c.Kind == CheckKind.HardSafety ? 0 : 1)
                               .ThenByDescending(c => Math.Abs(c.Limit) > 1e-9
                                    ? Math.Abs(c.Actual - c.Limit) / Math.Abs(c.Limit)
                                    : Math.Abs(c.Actual - c.Limit))
                               .ToArray();
            if (bads.Length == 0)
                sb.AppendLine("★ **全判据通过。** 下面是明细。").AppendLine();
            else
            {
                sb.AppendLine($"✗ **{bads.Length} 条判据没过**：" +
                              string.Join("；", bads.Select(c => $"{c.Name.Split(' ')[0]} " +
                                  (c.Undetermined ? "无法判定" : $"{c.Actual:0.0}/{c.Limit:0.0}"))));
                var first = bads[0];
                sb.AppendLine($"　先解决这一条 ⇒ **{first.Name}**（{first.Where}）");
                // Note 里带着「【下一步】…」，把那一段单独拎出来，不让它埋在长注里
                int k = first.Note.IndexOf("【下一步】", StringComparison.Ordinal);
                if (k >= 0) sb.AppendLine("　" + first.Note[k..].Replace("；", "；\r\n　　"));
                else if (first.Undetermined) sb.AppendLine("　【下一步】先让它算得出来 —— **无法判定不等于通过**。");
                sb.AppendLine();
            }
        }

        // ★ 形状体检报告插在这里：判定已经说完，明细还没开始 ——
        //   用户读完「过没过」之后，下一个问题就是「为什么、代价是什么」。
        if (_pendingReview.Length > 0)
        { sb.AppendLine(_pendingReview); _pendingReview = ""; }

        // 段表与法兰表：一格一个 `\t`，列宽由 TextFmt 按真实像素量出来。
        //
        // ⚠ 两张表**必须被一个空行隔开**。连着写的话它们会被认成同一张表，
        //   列宽合并计算 —— 两组毫不相干的量（控温 °C 与 Φ）从此互相顶着走。
        // ⚠ 表头不再有「挤进 8 个字」这条约束（列宽跟着内容走），所以单位写全：
        //   以前的「控温」「管 J」「J_max」不看文档不知道单位，那是省版面省出来的坑。
        //   但**只补单位，不改叫法**：「衔接温差 K」本来就是全名，缩成「衔接 ΔT」是往回走。
        sb.AppendLine("段\t控温 °C\t电流 A\t管 J A/mm²\t管根 °C\t衔接温差 K\t管重 g");
        foreach (var s in r.Segments)
            sb.AppendLine($"{s.Name}\t{s.SetpointC:0}\t{s.CurrentA:0}\t{s.TubeJAPerMm2:0.00}\t" +
                          $"{s.TRootC:0.0}\t{s.RootDeltaK.ToString("+0.0;-0.0")}\t{s.MassG:0}");
        sb.AppendLine();

        sb.AppendLine("法兰\t电流 A\tJ_max A/mm²\tΦ\t抽热 W\t最高 °C\t铂重 g");
        foreach (var f in r.Flanges)
            sb.AppendLine($"{f.Name}\t{f.CurrentA:0}\t{f.JMaxAPerMm2:0.00}\t{f.Phi:0.000}\t" +
                          $"{f.QFromTubeW.ToString("+0;-0")}\t{f.TMaxC:0.0}\t{f.MassG:0}");
        sb.AppendLine();
        // ★ 收敛情况必须**跟判据一起看**：判据是在解上判的，解没收敛判据就没意义。
        //   剩余误差是「距不动点」的估计，不是「相邻两轮变化」——后者曾把没收敛的解报成收敛（§1.85）。
        foreach (var nt in r.Notes)
            if (nt.Contains("耦合") || nt.Contains("基线")) sb.AppendLine("  ⓘ " + nt);
        if (!r.Converged) sb.AppendLine("  ⚠ **未收敛 ⇒ 下面每个数都不可引用**");
        sb.AppendLine();

        // ⚠ 标记别用 ★ / ○：等宽字体（Consolas）没有这些字形，Windows 会回落到另一套字体，
        //   实测渲染成一个**黑色旗子状的方块**，而且宽度也对不上、把整列推歪。
        //   ⇒ 改用中文字：CJK 字体里一定有，宽度恰好是两个字宽（TextFmt 也按 2 算），
        //     而且不用看图例就知道什么意思。
        // ★★★ 判据表已改成**真表格控件**（_checks，见 FillChecks）——
        //   这里不再用文字排它。文字排不出来的根因写在 _checks 的注释上：
        //   判据名宽度差太远，一超预留宽度就把整行的列全推走。
        sb.AppendLine("判据表见上方表格：不过的行标红，注释在鼠标悬停里。");
        sb.AppendLine();
        sb.AppendLine($"★ 整线总铂 {r.TotalMassG:0} g（管 {r.TubeMassG:0} + 法兰 {r.FlangeMassG:0}）" +
                      $"　基准 {r.BaselineMassG:0} g　省 {r.SavingPct:0.0} %");
        sb.AppendLine($"  玻璃温降 模型 {r.GlassDropModelK:0.0} / 实测 {r.GlassDropMeasuredK:0.0} K" +
                      "　（模型唯一的现场验证点）");
        foreach (var n in r.Notes) sb.AppendLine("  " + n);
        // 照常写文本即可：排版（逐表制表位、`**…**` 加粗）由构造函数里挂的
        // TextFmt.Hook 接管 —— 与本页其余几十处写输出的地方走同一条路。
        _out.Text = sb.ToString();

        // ★ 没结果时画**空态提示**，不是空坐标轴 —— 一个 −10…10 的空轴
        //   看着像「算坏了」，而实际是「还没算」。两者要做的动作完全不同。
        if (r is null || !r.Ok)
        {
            FieldPlots.DrawEmpty(_pT, "还没有结果 —— 点「核算整线」");
            FieldPlots.DrawEmpty(_pJ, "还没有结果 —— 点「核算整线」");
            FieldPlots.DrawEmpty(_pAx, "还没有结果 —— 点「核算整线」");
            return;
        }

        // 场图取最不利那片（局部最高温）
        var worst = r.Flanges.OrderByDescending(f => f.TMaxC).FirstOrDefault();
        if (worst?.Mesh is not null)
        {
            FieldPlots.DrawShellField(_pT, worst.Mesh, worst.TField,
                $"法兰温度场　{worst.Name}", "温度", "°C", (double)_wall.Value + 25.0);
            FieldPlots.DrawShellField(_pJ, worst.Mesh, worst.JField,
                $"电流密度场　{worst.Name}", "J", "A/mm²", (double)_wall.Value + 25.0);
        }

        // ★ 2026-08-20：第三张图（「管轴向剖面」）**从建出来就没画过** ——
        //   页签在、控件在、数据也一直在，只是没人接这一行。
        //   一个永远空白的页签比没有这个页签更坏：它看起来像「这次没算出来」。
        FieldPlots.DrawLineAxialProfile(_pAx, r, _base);
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
        // ★★★ 1b 之后必须换写法（2026-08-17）。
        //
        // 原来走 `WritePlate3dm`，而那个入口**只往渲染子进程传五个数**
        //（盘半径/孔半径/舌端X/舌端半宽/厚度列表）—— 它在结构上就表达不了
        // 渐变环、舌根圆角、角焊缝、等宽舌片。
        // 1b 让页面**求解**的是完整几何，如果导出仍走它，就成了
        // 「**算的是一个东西、导出的是另一个东西**」——正是今天反复在修的那类错，
        // 而且这一种最难发现：两边各自都自洽。
        // ⇒ 改走 `WriteFinal3dm`（与「导出定案 3DM」同一个写入器，今天已验过
        //   逐件质量对账 +0.03 %），导出的就是刚才解的那套几何。
        var dExp = PageToFinalDesign();
        using var dlg = new SaveFileDialog
        {
            Filter = "Rhino 3D 模型 (*.3dm)|*.3dm",
            FileName = $"本页_盘{2 * dExp.DiscRadiusMm:0}_舌{dExp.TabLengthMm:0}x{2 * dExp.TabHalfWidthMm:0}.3dm"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            string log = Geometry3dm.WriteFinal3dm(dExp, dlg.FileName);
            _out.Text = "【导出本页 3DM】" + dlg.FileName + "\r\n" +
                        "整机（三段管 + 四片法兰），几何 = 本页参数 + 下列本页无控件项：\r\n" +
                        AnalyticUsedWhat(dExp) + "\r\n" +
                        (_last is { Ok: true } && !_last.AllOk
                          ? "⚠ **上一次核算并非全判据通过** —— 这张图只是几何，不代表方案可用。\r\n"
                          : "") +
                        log + Environment.NewLine + _out.Text;
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
        ExportScaledTo(fb.SelectedPath);
    }

    /// <summary>
    /// 按指定目录导出四片。抽出来是为了**能被接线测试直接调**
    /// （FolderBrowserDialog 是模态的，测不了）——
    /// 与 <see cref="ExportBlockedReason"/> 当初抽出来是同一个理由。
    /// </summary>
    internal void ExportScaledTo(string dir)
    {
        Directory.CreateDirectory(dir);
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
                string dst = Path.Combine(dir,
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
            _shape = sh;
            SyncAnalysisPending();          // 分析完了 ⇒ 指路不该再指它
            Shared?.Notify();
            // 四片先按同一张图的分级；各片可各自选不同 .3dm 时逐片解析亦可
            var lv = sh.Levels.Select(l => l.ThicknessMm).ToArray();
            _levels = Enumerable.Range(0, 4).Select(_ => (double[])lv.Clone()).ToArray();
            _levelScale = null;
            BuildLockBoxes(sh);

            // ── 顺手量一下解析替身像不像（毫秒级，比它省下的那次求解便宜五个数量级）
            string surrNote;
            try
            {
                var (_, fid, _) = AnalyticSurrogate.BestFit(sh, f, lv);
                _surrFid = fid; _surrTabParallel = fid.TabParallel;
                surrNote = AnalyticSurrogate.Usable(fid)
                    ? "→ **解析替身可用**（" + fid.Report() + "）"
                      + Environment.NewLine
                      + "   ⇒ 「自动定厚」将在替身上搜方向，**每次评估从分钟级变毫秒级**；"
                      + Environment.NewLine
                      + "     最终解仍回到**原图纸**几何上全精度复核，报出来的是图纸的数。"
                    : "→ **解析替身不可用**，「自动定厚」只能逐次重算厚度场（慢）。原因："
                      + Environment.NewLine
                      + (fid.Blockers.Count > 0
                         ? "     · " + string.Join(Environment.NewLine + "     · ", fid.Blockers)
                           + Environment.NewLine
                           + "     结构性差异不看残差：缺的那部分材料不在解析几何里，"
                           + "数字碰巧接近也不代表是同一片板。"
                         : $"     · 电学差最大 {fid.Worst * 100:0.0} %，超过 {AnalyticSurrogate.Tol * 100:0.0} % 的界"
                           + Environment.NewLine + "     " + fid.Report());
            }
            catch (Exception exS)
            {
                _surrFid = null;
                surrNote = "→ 替身保真度没量成：" + exS.Message + "　⇒ 「自动定厚」按慢路径走（保守）。";
            }

            _out.Text = PlateShapeAnalyzer.Format(sh) + Environment.NewLine
                      + $"→ 已记下 {lv.Length} 级厚度，「自动定厚」将让优化器自行决定各级比例。"
                      + Environment.NewLine + surrNote + Environment.NewLine
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

    /// <summary>
    /// 按解析出的分级列出「锁定」勾选框。半径大的在上，便于对应「外圈」。
    ///
    /// ⚠ 这里**不需要**再手挂 `CheckedChanged += ParamChanged` —— `_lockPanel.Controls.Add`
    ///   会触发 `HookAutoRun` 装的 `ControlAdded`，勾选框在**加进来的那一刻**就被接上了。
    ///   再挂一次的后果不是「更保险」，是**每次勾选触发两回重算**。
    /// </summary>
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
