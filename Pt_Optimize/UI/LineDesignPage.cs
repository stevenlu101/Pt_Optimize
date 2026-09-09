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
    // ★ 默认取**当前设计记录的壁厚 0.80**，不再是 0.40（2026-08-21）。
    //   用户：「低于焊接工艺下界 0.6 mm —— 这是基本，**能造能用后才是优化铂金减重**」。
    //   0.40 低于工艺下界 0.6 ⇒ 开箱那一刻界面上摆的就是一个**焊不出来的构型**；
    //   第一次用的人直接点「核算整线」，会拿它跑几十秒，解发散到 5000 °C 以上、
    //   判据大面积不过 —— 看起来像程序坏了，其实是默认值本身不可制造。
    //   （--walk 全程验证抓到。下限仍保留 0.10：允许探索，但判据与夹持会拦住。）
    private readonly NumericUpDown _wall = Num(0.80m, 0.10m, 5.00m, 0.05m, 2);
    private readonly NumericUpDown _tubeIns = Num((decimal)StartPoint.TubeInsulMm, 0.0m, 100.0m, 0.5m, 1);
    /// <summary>
    /// ★ 设计电流密度 J（A/mm²）—— 用户 2026-09-09：「J 让工程师设定（实况风险工程师承担）；J 是设定值，J+1 是计算极限值；J 预设值为 10」。
    /// 按它定舌片厚与各截面下界、孔径/槽张角上界；终验全体截面 &lt; J+1。进快照（改了上一次的解就不新鲜）、进设计记录。
    /// </summary>
    private readonly NumericUpDown _jDesign = Num((decimal)SectionSizing.JDesignAPerMm2, 3m, 30m, 0.5m, 1);
    private readonly NumericUpDown _clamp = Num((decimal)StartPoint.ClampTempC, -1m, 1200m, 10m, 0);

    /// <summary>
    /// 三个此前**只存在于设计记录里**的几何/工艺量（2026-08-28 补成输入）。
    ///
    /// ★ 用户：「**我不要设计记录这种模式（这坑太大），要严格遵守第一性原理**」。
    ///   设计记录同时当「回归基准」与「计算起点/兜底」两个角色，一混就出了本轮查到的一串问题。
    ///   补上这三个之后，**页面上每一个进计算的量都有输入来源**，
    ///   DesignSpec 退回它唯一正当的角色：**回归基准**。
    /// </summary>
    private readonly NumericUpDown _fillet = Num((decimal)StartPoint.TabFilletMm, 0m, 20m, 0.5m, 1);
    private readonly NumericUpDown _ringW = Num((decimal)StartPoint.RingWidthMm, 0.5m, 20m, 0.5m, 1);
    private readonly NumericUpDown _clampLen = Num((decimal)StartPoint.ClampLengthMm, 3m, 200m, 5m, 0);
    /// <summary>
    /// `.3dm` 模式下的舌保温 mm。解析模式不用它（那边逐片来自 DesignSpec.TabInsulMm）。
    /// 0 = 裸舌 —— 那是此前 .3dm 路径**写死**的行为。
    /// </summary>
    private readonly NumericUpDown _tabIns3dm = Num(0.0m, 0.0m, 5.0m, 0.05m, 2);
    private readonly ComboBox _flIns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiScale.S(110) };
    private readonly NumericUpDown _flInsT = Num(20.0m, 0.0m, 60.0m, 0.5m, 1);
    private readonly NumericUpDown _discD = Num(60m, 30m, 300m, 2m, 0);
    private readonly NumericUpDown _tabLen = Num(50m, 20m, 400m, 5m, 0);
    private readonly NumericUpDown _tabW = Num(20m, 5m, 150m, 1m, 0);
    // ★★★★★ 2026-09-02：这六个逐片数组原来都是**写死四个**的 readonly 字段。
    //   用户实测：段表加到 HC4（4 段）之后界面仍只有 4 片，而核心要 5 片
    //   （SegmentCount => SetpointC.Length，FlangeCount = n+1）⇒ 算的是另一个零件。
    //   用户 2026-09-02：「UI 段数是必须可调整的」。
    //   ⇒ 改由 RebuildPlateRows() 按当前段数生成；段表一动就重建。
    /// <summary>上一次真正画出来的片数。排版事件不改它 —— 见 SegsChanged。</summary>
    private int _plateCountShown = -1;

    /// <summary>逐片输入的容器。段数一变就整块清空重建 —— 见 <see cref="RebuildPlateRows"/>。</summary>
    /// <summary>工具条第二排：图纸路与工具。主线在第一排。</summary>
    /// <summary>工具条第一排：主线。</summary>
    private readonly ToolStrip _tool = new()
    { GripStyle = ToolStripGripStyle.Hidden, Font = UiScale.Ui(), Dock = DockStyle.Top };

    private readonly ToolStrip _tool2 = new()
    { GripStyle = ToolStripGripStyle.Hidden, Font = UiScale.Ui(), Dock = DockStyle.Top, Visible = false };

    /// <summary>
    /// ★★★★★ R17／R21（用户 2026-09-08）：主视图三步「① 输入 → ② 法兰优化 → ③ 结果与出图」。
    /// 本页（TabPage）是第 ② 步；输入控件与结果（判据表、场图）**仍由本页创建与持有**（所有状态机、写回、快照都在这里），
    /// 只是摆到 MainForm 造的「① 输入」「③ 结果与出图」两页上 —— 与设计记录那组按钮借出去是同一个做法：
    /// 重建一套控件等于把状态抄第二份，迟早漂开。
    /// </summary>
    internal Control InputHost => _inputHost;
    internal Control ResultHost => _resultHost;
    private readonly Panel _inputHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _resultHost = new() { Dock = DockStyle.Fill };
    /// <summary>
    /// 两个宿主的「停车位」：0×0 但**可见**的面板，挂在本页上。
    /// 为什么必须有：<see cref="HookAutoRun"/> 从本页 Controls 递归挂监听（ControlAdded 也递归），宿主不在树里
    /// ⇒ 输入控件一个都没接上自动重算（2026-09-09 接线测试当场抓到）；单独造本页的测试也得在树里找得到段表与旋钮。
    /// 可见才会随窗体建句柄（段表的列要句柄才生成）。MainForm 装轨时把宿主搬到 ①③ 页，停车位就空了。
    /// </summary>
    private readonly Panel _parking = new() { Size = new Size(0, 0), Location = new Point(0, 0), Visible = true };
    /// <summary>③ 页：判据表在上、场图在下。存成字段是为了按有没有结果调比例（没结果时判据表让位）。</summary>
    private readonly SplitContainer _resultSplit = new()
    { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
    /// <summary>「手动分步 ▾」：展开/收起第二排（自动定厚／搜形状／加密复算／灵敏度扫描／可回读 3DM）。
    /// 故意用 Label 不用 Button：它不是 Flow 命令，不该被门禁、指路、接线测试当成命令。</summary>
    private readonly ToolStripLabel _btnManual = new("手动分步 ▾") { IsLink = true, LinkBehavior = LinkBehavior.AlwaysUnderline };
    internal void ShowManualRow(bool on)
    {
        _tool2.Visible = on;
        _btnManual.Text = on ? "收起手动分步 ▴" : "手动分步 ▾";
    }

    private readonly TableLayoutPanel _plateBox = new()
    {
        ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
    };

    private NumericUpDown[] _tPlate = System.Array.Empty<NumericUpDown>();

    /// <summary>
    /// 舌保温 mm（逐片）—— **优化变量**（用户 2026-08-25：
    /// 「板厚 / 舌保温 / 环倍率 / 管保温 / 夹持温度…优化程式需自己给出答案，
    ///  可以在 UI 输入框上给初始值」）。
    ///
    /// ★ 此前本页**没有这两组控件**，于是 <see cref="PageToDesignSpec"/> 从
    ///   <c>DesignSpec.Current</c> 里继承 —— 而 Current 全仓只在声明处赋过值（恒为 W08）。
    ///   后果：点「载入设计记录」选 0.6 档，控件变成 0.6 的值，**舌保温却仍是 0.8 档的**
    ///   （W08 是 0.4/0.4/**0.5**/**0.3**，W06 是 0.4/0.4/**0.4**/**0.6**）
    ///   ⇒ 算的是「0.6 的管 + 0.8 的保温」，而界面还写着「点核算整线就能复现设计记录数字」。
    ///
    /// 初始值 = <see cref="SizerOptions.InsLoMm"/>（0.3 ≈ **裸舌**）：
    /// 它是旋钮自己的下界，也是一个**真实物理状态**，不是捏出来的数。
    /// </summary>
    private NumericUpDown[] _tabIns = System.Array.Empty<NumericUpDown>();

    /// <summary>
    /// 管孔渐变环倍率（逐片）—— **优化变量**。初始值 = <see cref="SizerOptions.RingLo"/>
    /// （1.00 = **无台阶**，同样是真实状态）。上界 2.5 与 SizerOptions.RingHi 一致。
    /// </summary>
    private NumericUpDown[] _ringMul = System.Array.Empty<NumericUpDown>();

    /// <summary>★ 舌片厚 mm（逐片，**只显示**）：= I/(J·舌宽) 闭式（用户 2026-09-08 R11：不是旋钮）。</summary>
    private NumericUpDown[] _tongue = System.Array.Empty<NumericUpDown>();

    /// <summary>
    /// ★★ 本页此刻代表的是**一份完整的设计**（载入设计记录／求解器解完写回）时，它的舌片厚**原样带着**，
    /// 核算就复现那份设计；工程师改了任何参数就清掉 ⇒ 舌片厚回到 I/(J·舌宽) 规则重新定。
    /// 没有它，「载入 0.8 档 → 核算」会把舌片按新规则改厚 ⇒ 复现不出记录（2026-09-08 UiWiring「页面路径复现设计记录」抓到）——
    /// 而设计记录的用途正是校正计算流程（载入 → 核算 → 对得上）。NaN = 与基板同（早于 R11 的记录）。
    /// </summary>
    private double[]? _tongueFixed;

    /// <summary>
    /// ★ R12／R13（2026-09-09）：场定的位置与求解器选的形状是**算出来的**，不是旋钮 —— 只读显示。
    /// 逐片：槽心角 <see cref="DesignSpec.SlotCenterDeg"/>、舌孔孔心 <see cref="DesignSpec.TabHoleXMm"/>、
    /// 圆盘挖料形状 <see cref="DesignSpec.DiscCutShape"/>（含长椭圆轴向 <see cref="DesignSpec.DiscCutRotDeg"/>）、舌孔形状 <see cref="DesignSpec.TabHoleSides"/>。
    /// </summary>
    private NumericUpDown[] _slotCenter = System.Array.Empty<NumericUpDown>();
    private NumericUpDown[] _holeX = System.Array.Empty<NumericUpDown>();
    private Label[] _discShape = System.Array.Empty<Label>();
    private Label[] _holeShape = System.Array.Empty<Label>();
    /// <summary>与 <see cref="_tongueFixed"/> 同一条生命周期：本页代表一份完整设计时，场定位置与形状原样带着；改参数就清掉回默认规则。</summary>
    private DesignSpec? _fixedDerived;

    /// <summary>把场定的位置与形状写进只读框（不触发重算）。</summary>
    private void ShowDerived(DesignSpec d)
    {
        bool old = _suppressAuto; _suppressAuto = true;
        try
        {
            for (int i = 0; i < _slotCenter.Length && i < d.FlangeCount; i++)
            {
                _slotCenter[i].Value = Math.Clamp((decimal)d.SlotCenterDegOf(i), _slotCenter[i].Minimum, _slotCenter[i].Maximum);
                if (i < _holeX.Length)
                    _holeX[i].Value = Math.Clamp((decimal)d.TabHoleCenterXMm(i), _holeX[i].Minimum, _holeX[i].Maximum);
                if (i < _discShape.Length)
                {
                    int sh = d.DiscCutShapeOf(i);
                    double rot = i < d.DiscCutRotDeg.Length ? d.DiscCutRotDeg[i] : double.NaN;
                    _discShape[i].Text = Solver.DiscShapeName(sh) + (sh != 0 && !double.IsNaN(rot) ? $"（轴向 {rot:0}°）" : "");
                }
                if (i < _holeShape.Length) _holeShape[i].Text = Solver.HoleShapeName(d.TabHoleSidesOf(i));
            }
        }
        finally { _suppressAuto = old; }
    }

    /// <summary>
    /// ★★★ **渐变环的形状**：内级外扩 r₁ / 外级外扩 r₂ / 外级倍率 t₂（逐片，2026-08-30 补控件）。
    ///
    /// ══ 补它的理由不是「多个输入方便」
    ///
    /// 本页早就立过一条规矩（2026-08-28）：
    /// <code>
    ///   页面上每一个进计算的量都有输入来源，
    ///   DesignSpec 不再是任何计算的起点或兜底，只剩回归基准这一个角色。
    /// </code>
    /// 而这三个是**最后三个例外**：<see cref="PageToDesignSpec"/> 从
    /// <c>DesignSpec.Current.Clone()</c> 起手，于是它们被**静默继承**自设计记录 ——
    /// 正是那条规矩点名要禁的形态。
    ///
    /// ══ 默认「继承历史规则」怎么表达
    ///
    /// 三者的模型默认值是 <b>NaN = 用旧规则</b>（r₁ = 环宽、r₂ = 2×环宽、t₂ = 1+0.4(t₁−1)），
    /// 而 NumericUpDown 表达不了 NaN。⇒ 用一个复选框切换：
    /// **不勾**（默认）= 传 NaN，框子禁用但**照样把规则算出来的值显示出来**
    /// （看得见的默认值才学得会那条规则）；**勾上** = 逐片自定，读框子。
    ///
    /// ⚠ 它们**不是求解器旋钮**（<see cref="Solver.Allocation"/> 里没有）——
    ///   `--monotone` 实测三条全单调，但对 ③ 与 圆盘区最高温 都往坏走，只对 管孔净流入 往好走，
    ///   而 管孔净流入 已经有板厚。「抬哪个」是取舍，不是查表 ⇒ 先做敏感度矩阵再说。
    ///   所以这里是**工程师手动探索**用的，改了要自己重解。
    /// </summary>
    private readonly CheckBox _ringShapeCustom = new()
    {
        Text = "逐片自定（不勾 = 用旧规则）", AutoSize = true,
    };
    private NumericUpDown[] _ringR1 = System.Array.Empty<NumericUpDown>();
    private NumericUpDown[] _ringR2 = System.Array.Empty<NumericUpDown>();
    private NumericUpDown[] _ringT2 = System.Array.Empty<NumericUpDown>();

    /// <summary>
    /// ★★★ 圆盘背侧减重槽的张角（逐片，度；0 = 不开槽）。2026-09-05 加。
    ///
    /// 位置由**场**定（deliverable/移除优先级.txt）：管孔外缘、背对舌片那一侧
    /// —— 电流基本不走那半圈，热却照样从那里抽走。
    /// 实测 27–40 mm / 180° ⇒ 抽热 −42.2 %，峰值电流密度只 +2.9 %。
    /// 与另外六个旋钮同排，工程师看得见、求解器写得回。
    /// </summary>
    private NumericUpDown[] _slotDeg = System.Array.Empty<NumericUpDown>();

    /// <summary>
    /// ★★★ 舌板开孔孔径（逐片，半径 mm；0 = 无孔）。2026-09-05，用户要求 R5：
    /// 「舌板开孔，尺寸、**孔径**、厚度也都要能优化」。
    /// ⚠ 我一度以「实测不划算」把它降级成非旋钮 —— 那是把「不划算」当成「不需要」。
    ///   工程师图上本来就有孔，APP 要回答的是「**这个孔该多大**」。
    /// </summary>
    private NumericUpDown[] _holeR = System.Array.Empty<NumericUpDown>();

    /// <summary>孔的顺流拉长比（逐片；1 = 圆）。实测 3:1 顺流时峰值电流密度 -10.6 %。</summary>
    private NumericUpDown[] _holeAsp = System.Array.Empty<NumericUpDown>();
    // ⚠ 文字要短到**放得下**（2026-08-20 实测截图里这两行断在半个词上：
    //   「解析形状（圆盘 + 梯形舌片，程」「Rhino .3dm 文件（任意形状：阶」）。
    //   它们已经是 AutoSize + 跨两列了 —— 截断的原因是文字本身比左栏还宽，
    //   靠布局救不回来。⇒ 可见文字缩短，完整说明挪进 ToolTip（见构造函数）。
    //   断在半个词上的标签比短标签更糟：它看着像程序出错了。
    private readonly RadioButton _srcAnalytic = new()
    { Text = "解析形状（程序生成）", AutoSize = true };
    private readonly RadioButton _src3dm = new()
    { Text = "Rhino .3dm 文件", AutoSize = true };
    // ★★★★★ **段数由 UI 输入框决定，图纸路也要跟着变**（用户 2026-09-03）。
    //   原来这两个是**定长 4**（= 写死 3 段）：分 4 段时逐片输入变成 5 片，
    //   而 .3dm 那边仍只有 4 个文件框 ⇒ 第 5 片没有图纸，算的是**另一个零件**。
    //   这正是用户 2026-09-02 在解析路点出的同一个 bug —— 当时只修了解析路那半边。
    private TextBox[] _file3dm = System.Array.Empty<TextBox>();
    private Control[] _row3dm = System.Array.Empty<Control>();
    /// <summary>.3dm 逐片文件行的容器 —— 段数一变就整块重建（同 <see cref="_plateBox"/>）。</summary>
    private readonly TableLayoutPanel _file3dmBox = new()
    {
        ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
    };
    private readonly TextBox _layer3dm = new() { Text = "法兰", Width = UiScale.S(96) };
    private readonly DataGridView _segGrid = new();
    private readonly RichTextBox _out = new();
    private readonly ToolStripProgressBar _prog = new() { Visible = false, Maximum = 1000 };
    private readonly ToolStripLabel _status = new("");
    private readonly ToolStripButton _btnRun, _btnAuto, _btnExport, _btnLoadCase, _btn3dm, _btnRepro;

    /// <summary>
    /// ★★★ **网格无关复核**（2026-08-30 补）。在此之前它只有命令行有。
    /// 见 <see cref="FlowState.MeshVerified"/> 那段说明：
    /// 没有它，工程师可以拿导航网格上的数直接出图，而那个数实测能差 1.8 K。
    /// </summary>
    private readonly ToolStripButton _btnVerify;
    private MeshVerify.Result? _meshVerify;
    private object? _verifiedSnap;
    /// <summary>「分析几何变数」——只在 .3dm 模式且入口片已选时可用，由 SyncGeomSource 控。</summary>
    private readonly ToolStripButton _btnAnalyze;
    private readonly ToolStripButton _btnExportRead;
    /// <summary>
    /// 「◈ 图纸几何 → 参数」—— 把 .3dm 反推出来的几何交给**解析路**（用户 2026-08-25 要求接上）。
    ///
    /// 两条输入路线此前给不出接近的答案，本质只有一条：**.3dm 路改不了形状**，
    /// 而设计记录的关键一步恰恰是改形状（Ø120 → Ø60）。反推参数这件事早就做到了
    /// （PlateShapeAnalyzer 一直在印那几个数），缺的只是**把它交过去**。
    /// </summary>
    private readonly ToolStripButton _btnToAnalytic;
    /// <summary>「另存为设计记录」—— 把当前的解写成 finaldesigns/*.fd.json。</summary>
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
    private bool _suppressAuto;              // 程序化写控件时暂闭（载入设计记录等）
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
    ///   圆盘区最高温 由两个竞争峰决定，**符号会随构型变**（已经栽过一次：拿另一构型的符号外推，判反了）」。
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
        /// <summary>设计电流密度 J（2026-09-09）：定截面的依据，改了上一次的解就不新鲜。</summary>
        public double JDesign;
        public double Disc, TabLen, TabW;
        // ★ 定尺寸器带回来的另外两个旋钮（2026-08-25）。**必须进快照** ——
        //   它们参与判据（舌保温是守 管孔净流入/③ 的主力），却没有页面控件；
        //   不进快照就会「换了旋钮而 Fresh 不变」= 假新鲜。
        public double SizerTabIns, SizerRingMul;
        /// <summary>
        /// ★★★ 2026-09-05：圆盘背侧减重槽的张角也**必须进快照**。
        ///   它是求解器治「③ 法兰增量温降」的新旋钮（实测抽热 −42 %），
        ///   与舌保温同类：参与判据、却没有页面控件。
        ///   不进快照 = **换了旋钮而 Fresh 不变** = 假新鲜。
        /// </summary>
        public double SizerSlotDeg;
        /// <summary>舌板开孔孔径（2026-09-05）。同 SizerSlotDeg：参与判据，不进快照就是假新鲜。</summary>
        public double SizerHoleR;
        /// <summary>孔的顺流拉长比 —— 同上：参与判据、没有独立快照就是假新鲜。</summary>
        public double SizerHoleAsp;
        /// <summary>2026-08-28 补：这三个也进快照 —— 它们现在是**输入**，改了就该让上一次的解不新鲜。</summary>
        public double Fillet, RingW, ClampLen;
        /// <summary>
        /// 2026-08-30 补：渐变环形状也进快照。**不进就是假新鲜** ——
        /// 它们直接进厚度分布、进判据，改了却让上一次的解还显示「新鲜」，
        /// 那正是本记录类型存在的理由。
        /// </summary>
        public bool RingCustom;
        public double RingR1, RingR2, RingT2;
    }

    /// <summary>
    /// 定尺寸器（D8）解出来的**舌保温**与**环倍率**。null = 尚未定尺寸，用设计记录值。
    ///
    /// ★★★★★ 为什么必须由本页承载（2026-08-25 `--follow` 走查逼出来的）：
    ///   D8 用**三个**旋钮找可行解（板厚 / 舌保温 / 环倍率），而本页原先只承载板厚。
    ///   于是「定尺寸 → 回 ③ 重解」这条路**必然退回失败**：重解时另外两个被丢回设计记录值，
    ///   管孔净流入 立刻掉负 ⇒ 提示又指回定尺寸 ⇒ **两步一循环，永远走不到交付**。
    ///   实测：定尺寸后 3569 g 全过 → 重解 管孔净流入 = −9.32 不过 → 再定尺寸 3565 g 全过 → …
    ///
    ///   ⚠ 它们没有控件，所以**必须在输出里印出来**（本项目规矩：
    ///     「看不见又在起作用的量是安静失败的温床」），并且**必须进 Snap**。
    /// </summary>
    private double[]? _sizerTabIns, _sizerRingMul;

    /// <summary>
    /// 把**定尺寸 / 搜形状算出来的那个设计**接管为本页的工作设计 —— 一处做完四件事：
    ///   ① 带回本页没有控件的那两个旋钮（舌保温 / 环倍率）；
    ///   ② 发布 Last；③ 收敛就标新鲜；④ 推给 FlowState。
    ///
    /// ★★★★★ 为什么收敛成一处（2026-08-25，同一个缺陷**第三次**出现）：
    ///   「自动定厚」的两条分支与「搜形状」原本各写各的，而**三处都漏了发布**
    ///   （只写 `_last` 就走）⇒ 指路与门禁读的是冻住的旧解：
    ///     · 提示会在同一个按钮上死循环，每轮几十分钟，永远走不到交付；
    ///     · ④→⑤ 那道 RequireAllOk 的门读的也是旧解；
    ///     · **最危险**：上一次解若是过的、而这一次把它调坏了，门会继续开着（假绿灯）。
    ///   逐处补第四次还会忘 —— 所以让它们**只能**从这一个入口接管。
    /// </summary>
    private void AdoptSolvedDesign(DesignSpec d, LineResult? best)
    {
        // ★★ 2026-08-25：定尺寸的结果写回**控件**，控件是舌保温/环倍率的**唯一来源**。
        //   此前另存一份 _sizerTabIns/_sizerRingMul，而 PageToDesignSpec 在它们为空时
        //   回退到 DesignSpec.Current —— 那是「同一个数两处来源 + 设计记录当起点」两个毛病叠一起。
        //   （另一份仍保留，只为 CurrentSnap 的新鲜度比对，不再参与构造设计。）
        _suppressAuto = true;
        try
        {
            // 9 根旋钮都从这里回控件 —— 缺一根就是「解出来的和画出来的不是同一件」。
            for (int j = 0; j < _tPlate.Length && j < d.TabThickMm.Length; j++)
                _tPlate[j].Value = Math.Clamp((decimal)d.TabThickMm[j],
                                              _tPlate[j].Minimum, _tPlate[j].Maximum);
            ShowTongues(d);      // R11：舌片厚是算出来的，解完照样回显示框
            _tongueFixed = (double[])d.TongueThickMm.Clone();   // 解出来的设计是完整的一份：核算复现它，不再重定舌片
            _fixedDerived = d.Clone();                          // R12/R13：场定位置与形状也原样带着
            ShowDerived(d);
            for (int j = 0; j < _tabIns.Length && j < d.TabInsulMm.Length; j++)
                _tabIns[j].Value = Math.Clamp((decimal)d.TabInsulMm[j], _tabIns[j].Minimum, _tabIns[j].Maximum);
            for (int j = 0; j < _ringMul.Length && j < d.RingMul.Length; j++)
                _ringMul[j].Value = Math.Clamp((decimal)d.RingMul[j], _ringMul[j].Minimum, _ringMul[j].Maximum);

            // ★★★ 2026-08-30（第 9 件）：t₂ 现在是**求解器旋钮**（Solver.Allocation 里 管孔净流入 的
            //   首选候选）⇒ 解出来的值必须回到控件，**并且要把「逐片自定」勾上**。
            //
            //   不勾的后果是静默的：PageToDesignSpec 在没勾时给这三个写 **NaN**（= 用旧规则）
            //   ⇒ 求解器解出 t₂ → 页面读回来时把它丢掉 → 重解得到**另一个答案**。
            //   本仓库为「传进来的旋钮值被丢弃」这一族栽过多次，这里是同一个形状。
            bool anyT2 = false;
            for (int j = 0; j < 4 && j < d.RingMul2.Length; j++)
                anyT2 |= !double.IsNaN(d.RingMul2[j]);
            // ★ r₁/r₂ 与 t₁/t₂ 是**成对**的旋钮（用户 2026-09-05「自由度成对」）——
            //   同一个勾控制这四个，只回填一半等于把另一半丢掉。
            for (int j = 0; j < 4 && j < d.RingW1Mm.Length; j++)
                anyT2 |= !double.IsNaN(d.RingW1Mm[j]) || !double.IsNaN(d.RingW2Mm[j]);
            if (anyT2)
            {
                _ringShapeCustom.Checked = true;
                for (int j = 0; j < 4 && j < d.RingMul2.Length; j++)
                    if (!double.IsNaN(d.RingMul2[j]))
                        _ringT2[j].Value = Math.Clamp((decimal)d.RingMul2[j],
                                                      _ringT2[j].Minimum, _ringT2[j].Maximum);
                for (int j = 0; j < 4 && j < d.RingW1Mm.Length && j < _ringR1.Length; j++)
                {
                    if (!double.IsNaN(d.RingW1Mm[j]))
                        _ringR1[j].Value = Math.Clamp((decimal)d.RingW1Mm[j],
                                                      _ringR1[j].Minimum, _ringR1[j].Maximum);
                    if (!double.IsNaN(d.RingW2Mm[j]))
                        _ringR2[j].Value = Math.Clamp((decimal)d.RingW2Mm[j],
                                                      _ringR2[j].Minimum, _ringR2[j].Maximum);
                }
            }

            // ★★★★★ **圆盘背侧减重槽 / 舌板开孔 / 孔的顺流拉长比也要回填**（2026-09-05）。
            //
            //   这三根是 Solver.Allocation 里治「法兰增量温降」「圆盘超温」的现役旋钮，
            //   而本方法此前**一个都没写回**。后果是静默的，而且要到出图才看得见：
            //
            //     求解器解出 圆盘槽 120°／孔径 36 → 控件仍是 0／0
            //       → PageToDesignSpec 读控件 → 导出的 .3dm **既没有槽也没有孔**
            //       → 工程师看到的是一张原始几何，却以为那就是优化结果
            //
            //   本仓库为「传进来的旋钮值被丢弃」这一族栽过多次（t₂ 是上一次），
            //   这里是同一个形状 —— 赋了值 ≠ 用它的人读得到。
            //   ⇒ 门：ExportShowsSolvedKnobsTests。
            for (int j = 0; j < _slotDeg.Length && j < d.SlotSpanDeg.Length; j++)
                _slotDeg[j].Value = Math.Clamp((decimal)d.SlotSpanDeg[j],
                                               _slotDeg[j].Minimum, _slotDeg[j].Maximum);
            for (int j = 0; j < _holeR.Length && j < d.TabHoleRMm.Length; j++)
                _holeR[j].Value = Math.Clamp((decimal)d.TabHoleRMm[j],
                                             _holeR[j].Minimum, _holeR[j].Maximum);
            for (int j = 0; j < _holeAsp.Length && j < d.TabHoleAspect.Length; j++)
                if (d.TabHoleAspect[j] > 0)
                    _holeAsp[j].Value = Math.Clamp((decimal)d.TabHoleAspect[j],
                                                   _holeAsp[j].Minimum, _holeAsp[j].Maximum);
        }
        finally { _suppressAuto = false; }
        SyncRingShape();
        _sizerTabIns = (double[])d.TabInsulMm.Clone();
        _sizerRingMul = (double[])d.RingMul.Clone();
        _last = best;
        if (best is { Ok: true, Converged: true }) { _solvedRes = best; _solvedSnap = CurrentSnap(); }
        PushFlow();
    }

    /// <summary>「搜形状」每个候选筛几轮 / 胜出者精算几轮。**只有走查器会改它**。</summary>
    /// <summary>R24：把搜形状算过的形状灌进下拉：可行的按铂重排前，不可行的排后并写明原因；一个都没有就藏起来。</summary>
    internal void RefreshShapePick()
    {
        _suppressShapePick = true;
        try
        {
            _shapePick.Items.Clear(); _shapePickRows.Clear();
            var feas = _shapeRows.Where(r => r.ok && !double.IsNaN(r.mass)).OrderBy(r => r.mass).ToList();
            var infeas = _shapeRows.Where(r => !(r.ok && !double.IsNaN(r.mass))).ToList();
            _shapePick.Items.Add($"搜形状结果 ▾　{feas.Count} 个可行形状（按铂重）—— 选一个写回页面");
            foreach (var r in feas.Concat(infeas)) { _shapePickRows.Add(r); _shapePick.Items.Add(ShapeRowText(r)); }
            _shapePick.SelectedIndex = 0;
            _shapePick.Visible = _shapeRows.Count > 0;
        }
        finally { _suppressShapePick = false; }
    }

    private static string ShapeRowText((DesignSpec d, double mass, bool ok, string msg) r)
    {
        string geo = $"盘Ø{2 * r.d.DiscRadiusMm:0}／舌 {r.d.TabLengthMm:0}×{2 * r.d.TabHalfWidthMm:0}";
        if (r.ok && !double.IsNaN(r.mass)) return $"{geo}　{r.mass:0} g　✓ 可行";
        string why = (r.msg ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (why.Length > 48) why = why[..48] + "…";
        return $"{geo}　✗ {why}";
    }

    /// <summary>
    /// R24：工程师在下拉里选了一个形状 ⇒ 几何与那次解出的旋钮写回控件（与搜形状写回赢家同一条路）。
    /// 那是导航网格上的解，写回后当「还没精算」：点「核算整线」精算到判据所在网格才可出图。不可行的只说明、不写回。
    /// </summary>
    private void PickShape()
    {
        if (_suppressShapePick || _shapePick.SelectedIndex <= 0) return;
        int k = _shapePick.SelectedIndex - 1;
        if (k >= _shapePickRows.Count) return;
        var r = _shapePickRows[k];
        if (!(r.ok && !double.IsNaN(r.mass)))
        {
            _out.AppendText("\r\n✗ 这个形状**不可行**，没写回页面：" + ShapeRowText(r) + "\r\n   原因：" + (r.msg ?? "") + "\r\n");
            _suppressShapePick = true; try { _shapePick.SelectedIndex = 0; } finally { _suppressShapePick = false; }
            return;
        }
        decimal C(double v, NumericUpDown n) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);
        _suppressAuto = true;
        try
        {
            _discD.Value = C(2 * r.d.DiscRadiusMm, _discD);
            _tabW.Value = C(r.d.TabHalfWidthMm, _tabW);
            _tabLen.Value = C(r.d.TabLengthMm, _tabLen);
        }
        finally { _suppressAuto = false; }
        _solvedRes = null; _solvedSnap = null;      // 上一次精算的是别的形状
        AdoptSolvedDesign(r.d, null);               // 板厚／舌片厚／舌保温／环／槽／孔都写回；best=null ⇒ 当「还没精算」
        _out.AppendText("\r\n★ **已选形状**（工程师选的，写回上面的盘径/舌宽/舌长与那次解出的旋钮）：" + ShapeRowText(r) + "\r\n"
                      + "   这是**导航网格**上的解 ⇒ 点「核算整线」精算到判据所在的网格，过了才可出图。\r\n");
        _status.Text = "已选形状，待精算";
        PushFlow();
    }

    internal int SearchScreenRounds = 16, SearchFinalRounds = 40;

    /// <summary>
    /// ★★★ 粗筛用的平坦区网格 mm。**默认 0 = 关掉**（2026-09-04 实测之后）。
    ///
    /// ══ 试过了，不成立 —— 省不到钱，却动了答案
    ///
    /// 先量到「贵的是可行点」（失败 1 分钟 / 可行 20–33 分钟），于是想照
    /// 过热试探那一招把平坦区放粗（MeshCoarseMm 11 → 20）。实测两条都不利：
    ///
    ///   · **省不到**：MeshFineRadiusMm = 50，而搜的盘半径只有 27.5–35 mm ⇒
    ///     **整个圆盘都在细网格区里**，圆盘上根本没有「粗区」可放粗。
    ///     实测可行点 19.8–33.0 → 23.7–26.3 分钟，没降。
    ///   · **却改答案**：粗区实际落在**舌片**上（舌长 158 mm，远超 r = 50）。
    ///     放粗它改的是舌片电阻与发热 ⇒ 盘Ø55/舌41 从「判不了（NaN）」
    ///     变成「⑥ 盘盖不住，17.2 分钟」。
    ///
    /// ⇒ 这张图上**没有便宜的粗化空间**。降本只能靠**减少可行点的求解次数**
    ///   （即改求根），不能靠粗化网格。
    ///
    /// 选项与门都留着：哪天几何变了（盘大、舌短）它会重新成立，
    /// 而那时「精算不许用粗网格」那道门已经在位。
    /// ⚠ 不是 const：走查要能调它。
    /// </summary>
    internal double SearchScreenCoarseMm = 0;

    /// <summary>
    /// 搜形状的网格与外推上限。**只有走查器会改它们**。
    /// ⚠ 每一「轮」都是一次完整整线解（分钟级）—— 所以压轮数还不够快，
    ///   要把**网格点数**也压下来，接线验证才回得到分钟级。
    /// </summary>
    internal double[] SearchDiscs = { 25, 30, 35 };
    internal double[] SearchWFrac = { 0.75, 1.00 };
    internal int SearchMaxExtend = 6;

    private Snap CurrentSnap() => new()
    {
        Wall = (double)_wall.Value,
        Plate = _tPlate.Average(n => (double)n.Value),
        TubeIns = (double)_tubeIns.Value,
        JDesign = (double)_jDesign.Value,
        Disc = (double)_discD.Value,
        TabLen = (double)_tabLen.Value,
        TabW = (double)_tabW.Value,
        // ★★ 2026-08-25：改读**控件**。此前是 `_sizerX ?? DesignSpec.Current.X` ——
        //   定尺寸没跑过时，快照记的是**设计记录**的值，而实际计算用的也是它
        //   ⇒ 「参数没变」判得对，但两边一起错。现在控件是唯一来源，快照跟着控件走，
        //   工程师动一下舌保温/环倍率，上一次的解立刻不新鲜（本来就该如此）。
        Fillet = (double)_fillet.Value,
        RingW = (double)_ringW.Value,
        ClampLen = (double)_clampLen.Value,
        SizerTabIns = _tabIns.Average(n => (double)n.Value),
        SizerRingMul = _ringMul.Average(n => (double)n.Value),
        SizerSlotDeg = _slotDeg.Length > 0 ? _slotDeg.Max(n => (double)n.Value) : 0,
        SizerHoleR = _holeR.Length > 0 ? _holeR.Max(n => (double)n.Value) : 0,
        SizerHoleAsp = _holeAsp.Length > 0 ? _holeAsp.Max(n => (double)n.Value) : 1,
        RingCustom = _ringShapeCustom.Checked,
        RingR1 = _ringR1.Average(n => (double)n.Value),
        RingR2 = _ringR2.Average(n => (double)n.Value),
        RingT2 = _ringT2.Average(n => (double)n.Value)
    };
    // ★★★ AutoSize = false **必须写**：ToolStripComboBox 的 Width 在 AutoSize 开着时
    //   会被工具条布局覆盖成默认宽（~100 px）⇒ 设了等于没设。
    //   实测（2026-09-03 抓图）：档名「管壁 0.8 · 留余量」被切成「管壁 0.」——
    //   工程师看不出选中的是 0.8 还是 0.6 档，而这个下拉决定的正是**算哪一档**。
    private readonly ToolStripComboBox _caseBox =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = UiScale.S(210) };
    /// <summary>
    /// ★ R24（用户 2026-09-09：「哪个轻由场和铂重说话 —— 也供工程师自行选择」；「梯形舌端收窄不是错，把它当成另一种解」）：
    /// 搜形状算过的**每个**形状都留着（可行的按铂重排前，不可行的排后并写明原因），工程师从这个下拉选一个写回页面。
    /// 搜形状自己仍把最轻的写回，这里是给工程师改主意的入口。选的是**导航网格上的解** ⇒ 写回后要点「核算整线」精算才可出图。
    /// 搜形状没跑过就藏着。
    /// </summary>
    private readonly ToolStripComboBox _shapePick =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = UiScale.S(320), Visible = false };
    private List<(DesignSpec d, double mass, bool ok, string msg)> _shapeRows = new();
    private readonly List<(DesignSpec d, double mass, bool ok, string msg)> _shapePickRows = new();   // 下拉里的顺序
    private bool _suppressShapePick;
    private readonly TabControl _plots = new() { Dock = DockStyle.Fill };
    private readonly ScottPlot.WinForms.FormsPlot _pT = FieldPlots.NewPlot();
    private readonly ScottPlot.WinForms.FormsPlot _pJ = FieldPlots.NewPlot();
    private readonly ScottPlot.WinForms.FormsPlot _pAx = FieldPlots.NewPlot();

    private readonly BindingList<SegRow> _segs = new()
    {
        // 长度默认取 DesignInputs.TubeLengthMm 的出厂值（300）——
        // ⚠ 这里写字面量 300 是**第二处来源**，但 BindingList 的初始化器拿不到实例字段；
        //   真正的兜底在 LineRunner.Normalize（长度 ≤ 0 就按参数表铺满），见那里的说明。
        new SegRow { 名称 = "HC1", 直接加热管长mm = 300, 控温C = 1150, 水头m = 0.3 },
        new SegRow { 名称 = "HC2", 直接加热管长mm = 300, 控温C = 1080, 水头m = 0.6 },
        new SegRow { 名称 = "HC3", 直接加热管长mm = 300, 控温C = 1050, 水头m = 1.0 },
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
    /// <summary>
    /// 段表一行 = **一段加热**。这是两种几何来源（UI 参数 / .3dm 图纸）**共用**的输入群：
    /// 分几段、每段多长、每段控到多少度 —— 与几何怎么来无关，两条路都要给
    /// （用户 2026-09-03：「不同几何方式统一的输入参数群可以统一，该分开还是要分开」）。
    /// </summary>
    public sealed class SegRow
    {
        public string 名称 { get; set; } = "";

        /// <summary>
        /// ★★★★★ **每段可以不一样**（用户 2026-09-03：「每段直接加热铂金管的长度
        /// 必须是可以单独设定的」）。原来所有段共用参数表里那一个「段长 L」。
        ///
        /// ⚠ 名字是用户指定的：界面上就叫「直接加热铂金管的长度」——
        ///   它指的是**这一段真正通电发热的那段管**，不是法兰到法兰的外形尺寸。
        /// ⚠ 它同时是该段的**支承跨距**与**该段铂重**的长度。
        /// </summary>
        [DisplayName("直接加热铂金管的长度 mm")]
        public double 直接加热管长mm { get; set; }

        [DisplayName("控温点 °C")]
        public double 控温C { get; set; }

        [DisplayName("玻璃水头 m")]
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
        var tool = _tool;   // 第一排＝主线（MainForm 会往里插自动定厚/搜形状）
        // ★ 一键跑到底（2026-09-02 用户拍板）：解 → 定厚 →（搜形状）→ 加密复算
        //   → 停在「可以出图」。决策不新写，照 Flow.Next 一直走（见 RunPipelineAsync）。
        _btnRun = Btn("核算整线", (_, _) => _ = RunPipelineAsync());
        // ★★★ 按钮名字**从 Flow 读**（2026-09-04）。写死一份的后果当场就撞上了：
        //   我在 Flow 里把它改成「自动定厚（手动分步）」，而这里还写着旧名 ⇒
        //   界面接线测试五项红（Flow 登记的命令界面上找不到、界面上的按钮 Flow 没登记）。
        //   Flow 是命令表的唯一来源，名字也该只有那一份。
        _btnAuto = Btn(Flow.Cmd("core.autoThick").Text, (_, _) => _ = RunAsync(true));
        _btnVerify = Btn("◆ 加密复算（算到数不再变）", (_, _) => _ = VerifyMeshAsync());
        // 1b 之后它导出的是**整机**（管 + 四片法兰）且几何与求解一致，故改名点明
        _btnExport = Btn("导出本页 3DM", (_, _) => Export());

        // ★ 设计记录：直接从 Core/DesignSpec 取，**不在 UI 里再抄一份数**。
        //   两档都全判据通过，差别只在裕度与铂重（见各档的 Binding 说明）。
        RefillCaseBox();
        DesignSpec.Reloaded += OnDesignSpecsReloaded;
        _btnLoadCase = Btn("载入设计记录", (_, _) => LoadDesignSpec());
        _btn3dm = Btn("导出设计记录 3DM", (_, _) => ExportFinal3dm());
        _btnAnalyze = Btn("分析几何变数", (_, _) => AnalyzeShape());
        _btnExportRead = Btn("导出可回读 3DM", (_, _) => ExportReadable3dm());
        _btnToAnalytic = Btn("◈ 图纸几何 → 参数", (_, _) => AdoptShapeToAnalytic());
        _btnSaveFinal = Btn("另存为设计记录", (_, _) => SaveAsDesignSpec());

        // ★★★ 复现设计记录：**界面上唯一能跑出设计记录数字的按钮**（2026-08-16 用户提出）。
        //
        // 在此之前界面根本没有这条路：本页控件表达不了「管孔两级渐变环」与
        // 「逐片舌保温」，所以「载入设计记录 → 核算整线」跑的是一个**缺两项的构型**，
        // 数字对不上，而它照样出一张漂亮的判据表 —— §1.8「安静失败」的形状。
        // ⇒ 本按钮**完全绕过页面控件**，直接用 DesignSpec.BuildCase 造算例。
        _btnRepro = Btn("▶ 复现设计记录", (_, _) => _ = ReproduceAsync());
        // ⚠ 不能写 `new Font(_btnRepro.Font, Bold)` —— 那会**在这一刻捕获**按钮当时的字体
        //   （默认 9 pt），从此这个按钮就不再跟着工具条的字体走了。
        //   用户 2026-08-18 截图里「▶ 复现设计记录」比邻居明显小一号，就是这么来的。
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
        _btnShape = Btn(Flow.Cmd("shape.search").Text, (_, _) => _ = SearchShapeAsync());

        // ★★ 2026-08-20 阶段轨：本页只留「③ 整线核算」这一格的命令。
        //
        //   搬走的四个（自动定厚 / ◇ 搜形状 → ④；导出本页 3DM / 导出设计记录 3DM → ⑤）
        //   **仍然由本页创建和持有** —— 只是挂到了 ④⑤ 页的工具条上（见
        //   BtnAutoThick 等几个属性）。
        //
        //   为什么这样而不是「④ 页新建按钮再回调本页方法」：那些按钮身上挂着
        //   一整套运行时状态（跑起来变「取消」、互相禁用、finally 里恢复，见 RunAsync）。
        //   重建一套按钮就等于把那套状态**抄第二份**，而两份状态迟早会漂开。
        //   ToolStripItem 本来就能挂到任何一条工具条上，让它换个位置最省事、也最不会错。
        // ★ 设计记录那一组（下拉 / 复现 / 载入 / 另存 / 导出设计记录 3DM）**已搬到独立的
        //   「设计记录」页**（2026-08-23）。它们不读页面控件、不受阶段门禁，
        //   与「你手上这个设计走到哪一步」是正交的两根轴，混在同一条工具条上正是
        //   用户最初抱怨的「不知道自己在算什么」。控件仍归本页所有（载入要灌本页控件、
        //   另存要读本页的解），只是**摆在别处** —— 见 MainForm 装配。
        //
        // 本页现在只剩「关于当前这个设计」的两个命令。
        // ── 第一排：**主线**。工程师照蓝色指示走的就是这几个，按先后排。
        //   ⚠ Btn() **只造不挂** —— 忘了 Items.Add，按钮就成了「造好了没接线」（本仓头号敌人）。
        //     2026-09-02 阶段轨重排时又栽了一次：原 ④ 页删掉，而自动定厚/搜形状/厚度灵敏度
        //     原来挂在那一页 ⇒ 三个按钮当场从屏幕上消失。抓图才看出来。
        // ★★★★★ R17／R21（2026-09-08）：第一排只剩**一件事**——「核算整线」（= 法兰优化，一路算到能出图）。
        //   分步按钮（自动定厚／搜形状／加密复算／灵敏度扫描／可回读 3DM）收进第二排，默认收起，
        //   点「手动分步 ▾」展开；蓝色指路指到其中某个按钮时 MainForm 会自动展开。
        //   图纸路的两个按钮（分析几何变数／图纸几何 → 参数）归「① 输入」页（MainForm 挂）。
        _btnRun.Font = UiScale.Ui(FontStyle.Bold);
        tool.Items.Add(_btnRun);
        tool.Items.Add(_btnManual);
        _btnManual.Click += (_, _) => ShowManualRow(!_tool2.Visible);
        tool.Items.Add(_shapePick);                                   // R24：搜形状结果，工程师自行选
        _shapePick.SelectedIndexChanged += (_, _) => PickShape();
        _prog.Size = new Size(UiScale.S(160), UiScale.S(16));
        tool.Items.Add(_prog);
        tool.Items.Add(_status);

        // ── 第二排：**手动分步**（默认收起）。顺序 = 流水线里的先后：定厚 → 搜形状 → 加密复算，再是工具。
        //   自动定厚／搜形状／厚度灵敏度由 MainForm 插进来（MountMainRow / MountSecondRow）。
        _tool2.Items.Add(_btnVerify);
        _tool2.Items.Add(new ToolStripSeparator());
        _tool2.Items.Add(_btnExportRead);

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
        //   手抄的那版已经漂开过：常数早在 2026-08-17 换成新设计记录点的实测值，
        //   而这三条提示还停在旧构型的 ③ +123／−221／+22.7 上 ——
        //   **同一个数存两处，迟早对不上账**（§7 头一条）。插值之后它不可能再漂。
        Row("壁厚 mm", _wall,
            "工艺下界 0.6 mm = **手工 TIG 烧穿下界**（自动 TIG 0.3、激光 0.1，差一个量级）。\n" +
            "另一条独立的界是管 J ≤ 12 A/mm²（现场给定：一般上限 15，壁 0.6 时 12 是极限）。\n" +
            "设计记录两档正是被这两条同点咬住（0.6）与全都留有余量（0.8）。\n" +
            $"参考斜率（0.8 档 2026-08 离线实测，**会随形状变**）：法兰增量温降 {dDip_dWall:+0.0;−0.0} K/mm　圆盘区最高温 +16.7 K/mm" +
            $"　管J {dJ_dWall:+0.00;−0.00}　管重 +3051 g/mm\n" +
            "⚠ 圆盘区最高温 那条只在**这个工作点附近**成立：圆盘区最高温 由两个竞争峰决定，符号会随构型翻。");
        Row("纤维保温 mm", _tubeIns,
            "无空间限制、不花铂 —— 但**不是免费的**：\n" +
            $"  法兰增量温降 {dDip_dTubeIns:+0.0;−0.0} K/mm　圆盘区最高温 −3.1 K/mm" +
            $"　管J {dJ_dTubeIns:+0.00;−0.00} (A/mm²)/mm　（0.8 档 2026-08 离线实测）\n" +
            "机理：保温厚 ⇒ 管散热少 ⇒ 电流小（利），但 β 变小而 法兰增量温降=D/√(kAβ) 里 β 在分母（不利）。\n" +
            "现用的 5 mm 恰在拐点上 —— 这个值原本是没量过的默认值，碰巧是对的。");
        Row("电流密度 J A/mm²", _jDesign,
            "J 是**设定值**（预设 10，用户 2026-09-08 设计因果链）；**计算极限值 = J + 1**（预设 11）。\n" +
            "按 J 定舌片厚 = 设计电流 ÷ (J × 舌片最窄有效宽) 与圆盘各截面的板厚下界（约束盒下角），孔径／减重槽的上界也受它约束；\n" +
            "终验判据「法兰截面 J」全体 < J+1。调高 ⇒ 截面变薄、铂更省，但**实况风险由工程师承担**（用户 2026-09-09）。\n" +
            "改了之后上一次的解作废，要重新核算。");

        Head("法兰几何来源");
        // ⚠ 提示文字用 Environment.NewLine 拼，不写反斜杠转义 ——
        //   本仓有个钩子会把转义序列改成真字符，那会让字符串字面量当场断掉。
        var tipSrc = new ToolTip();
        tipSrc.SetToolTip(_srcAnalytic,
            "圆盘 + 梯形舌片，由程序按盘径/舌长/舌端半宽生成。" + Environment.NewLine
            + "只有这个模式能用「◇ 搜形状」—— 形状是可搜索的自由度。");
        tipSrc.SetToolTip(_src3dm,
            "任意形状：阶梯厚度、开槽、异形轮廓，从 Rhino .3dm 读厚度场。" + Environment.NewLine
            + $"形状由图纸给定 ⇒ 判据 {Criteria.Explain("⑤")}{Criteria.Explain("⑥")} "
            + "拿不到解析量，会报「无法判定」（不等于通过）。");
        _srcAnalytic.Checked = true;
        _srcAnalytic.CheckedChanged += (_, _) => SyncGeomSource();
        input.Controls.Add(_srcAnalytic); input.SetColumnSpan(_srcAnalytic, 2);
        input.Controls.Add(_src3dm); input.SetColumnSpan(_src3dm, 2);

        // .3dm 模式：每片一个文件（可重复同一文件），厚度由图纸决定，
        // 「自动定厚」求的是厚度**整体标度 k**，即「这张图要整体 ×k」。
        // ⚠ 行数**跟着段数走**，所以整块塞进一个可重建的容器（见 RebuildFile3dmRows）。
        input.Controls.Add(_file3dmBox);
        input.SetColumnSpan(_file3dmBox, 2);
        RebuildFile3dmRows();
        Row("舌保温 mm（.3dm）", _tabIns3dm,
            "舌片自己的保温厚度。**0 = 裸舌**，那是本路径此前写死的行为。"
            + Environment.NewLine
            + $"它是守 {Criteria.Explain("管孔净流入")}/{Criteria.Explain("③")} 的主力旋钮："
            + "实测在设计记录几何上，0.4 mm ⇒ 法兰增量温降 = 5.2 K ✓，"
            + "而 0（裸舌）⇒ 法兰 2986 °C、往管里灌 256 W。"
            + Environment.NewLine
            + "舌片裸露占端片散热的 90 % 以上 —— 一裸就净抽热、一全包又净倒灌，中间有零点。");
        Row("图层名", _layer3dm, "厚度场从该图层提取。t=0 表示无材料 ⇒ 开槽、孔、轮廓一次拿全");
        Row("锁定的级", _lockPanel,
            "勾上的级厚度锁死，优化器只调其余级。典型用法：外圈勾上 = 外圈不动、只调内圈。" +
            "先点「分析几何变数」才会列出各级。");

        Head($"法兰形状（解析模式；{PlateNames().Length} 片同形状，厚度各自独立）");
        Row("圆盘直径 mm", _discD,
            "缩小它是本问题里少有的「三者同向」：省铂 + 放松焊接下界 + 改善端片热平衡。\n" +
            "⇒ **最优盘径就在下界上**，而下界是判据「圆盘盖得住管孔」，**算得出来、不用搜**：\n" +
            "　　盘半径 ≥ 管孔半径 + 焊脚　　焊脚 = max(板厚, 壁厚)　管孔半径 = 壁厚 + 25\n" +
            "　0.8 档、板厚解到 2.45 ⇒ 需要 R28.25（Ø56.5）；现在是 Ø60，还有 1.75 mm 余量。\n" +
            "⚠ 下界**跟着板厚走** —— 求解器只往上抬板厚，抬一分焊脚长一分、「圆盘盖得住管孔」的余量掉一分。\n" +
            "　解完盘径不够时，`Solver.CoverCheck` 会直接把该改到多少印出来（处方，不是抱怨）。\n" +
            "⚠ 更正（2026-08-30）：此前这里写「再缩会让两级渐变环占满整个圆盘」——\n" +
            "　那不是**硬**下界。渐变环按**离管轴的半径**分级，越过盘缘之后它继续作用在舌根，\n" +
            "　不是失效。真正拦住你的是「圆盘盖得住管孔」。");
        Row("舌片长度 mm", _tabLen, "省铂宜短；但舌片越长形状数 Ψ 越小、局部越不易过热");
        Row("舌端半宽 mm", _tabW);

        // ★★★★★ 逐片输入：**行数按段数生成**（2026-09-02）。
        //   原来是四段写死的 `for i < 4`。用户实测段表加到 HC4 之后界面仍只有 4 片，
        //   而核心要 5 片 ⇒ 算的是另一个零件。用户：「UI 段数是必须可调整的」。
        //   ⇒ 全部塞进一个可清空重建的容器，段表一动就重建（见 RebuildPlateRows）。
        input.Controls.Add(_plateBox);
        input.SetColumnSpan(_plateBox, 2);
        RebuildPlateRows();

        Head("保温与夹持");
        Row("法兰保温", _flIns, "包纤维会降低自给所需厚度；不包则法兰更凉但从管子抽热更多");
        Row("法兰保温厚 mm", _flInsT);
        Row("铜排夹持 °C", _clamp,
            "空冷即可，<0 = 无夹冷。★ 现场把自给率整定到位的唯一旋钮。\n" +
            "⚠ 压接段被铜排短接 ⇒ **那一段不发热**：舌片有效发热长度 = 舌长 − 压接长。\n" +
            "  90 mm 舌片扣掉 40 mm 只剩 50 mm —— 想靠缩短舌片省铂会先把发热段砍没。");
        Head("几何/工艺（此前只在设计记录里，2026-08-28 补成输入）");
        Row("舌根圆角 R mm", _fillet,
            "⚠ 网格 2 mm，**小于它的圆角在场里看不出来**（§1.8 的分辨率坑）—— 3 mm 只有 1.5 格。"
            + Environment.NewLine +
            "而 圆盘区最高温 的峰**可能就落在舌根凹角**：也就是说优化器在调一个自己分辨不出来的几何。"
            + Environment.NewLine +
            "要真优化它，网格得先加密。");
        Row("环宽 mm", _ringW,
            "管孔渐变环的作用长度（两级台阶在 孔+w 与 孔+2w）。" + Environment.NewLine +
            "⚠ 环**倍率**是 D8 三旋钮之一、每轮在线调，而这个**长度**此前是写死的。" + Environment.NewLine +
            "孔边电流扰动按 (a/r)² 衰减（a = 孔半径 ≈ 25.8 mm）—— 3 既没对上那个尺度，也没对上分辨率。");
        Row("压接段 mm", _clampLen,
            "**这一个有依据**：早先网格里硬编码 3 mm 是**数值边界不是设计值** ——" + Environment.NewLine +
            "3 mm × 舌宽 40 = 120 mm²、共用片 1099 A ⇒ 界面电流密度约 9 A/mm²，" + Environment.NewLine +
            "而铜排压接通常按 ≤1 A/mm² 设计，**差一个数量级**。40 是按接触面反推的工程值。" + Environment.NewLine +
            "⚠ 它直接进判据「舌片自由段」（自由段 = 舌长 − 切点 − 压接段），铂重几乎线性跟着它走。");


        Head("分段控温点");
        _segGrid.Dock = DockStyle.Top;
        _segGrid.Height = UiScale.S(110);
        _segGrid.AutoGenerateColumns = true;
        _segGrid.AllowUserToAddRows = true;
        // ★ 工程师在段表末尾加一段时，长度先给参数表里那个默认值 ——
        //   留 0 的话 LineRunner.Normalize 会兜底成同一个数，但**界面上显示 0**，
        //   人会以为这一段长度是 0（「显示的 ≠ 算的」正是本项目反复栽的形态）。
        _segGrid.DefaultValuesNeeded += (_, e) =>
        {
            e.Row.Cells[nameof(SegRow.直接加热管长mm)].Value = _base.TubeLengthMm;
            e.Row.Cells[nameof(SegRow.控温C)].Value = _segs.Count > 0
                ? _segs[^1].控温C : 1050.0;
        };
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

        // ★★★★★ R17／R21（2026-09-08）三步主视图：
        //   ① 输入页  ← _inputHost（本页的输入控件整块）
        //   ② 本页    ← 工具条 + 输出框（过程与结论的文字）
        //   ③ 结果页  ← _resultHost（判据表在上、场图在下）
        InitChecksGrid();
        _inputHost.Controls.Add(input);

        // 输出区：正文 + 顶上一条开关（明细 / 诊断，默认收起）
        var outSwitches = new FlowLayoutPanel
        { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(UiScale.S(6), 2, 0, 2) };
        outSwitches.Controls.Add(_showDetail);
        outSwitches.Controls.Add(_showDiag);
        _showDetail.CheckedChanged += (_, _) => Show(_last);
        _showDiag.CheckedChanged += (_, _) => Show(_last);
        var outHost = new Panel { Dock = DockStyle.Fill };
        outHost.Controls.Add(_out);
        outHost.Controls.Add(outSwitches);

        _resultSplit.Panel1.Controls.Add(_checks);
        _resultSplit.Panel2.Controls.Add(_plots);
        _resultHost.Controls.Add(_resultSplit);

        Controls.Add(outHost);
        // ⚠ Dock=Top 的加入顺序是**倒着**的：后加的在上面。要「主线在上、手动分步在下」，
        //   就得先加第二排、再加第一排。
        Controls.Add(_tool2);
        Controls.Add(tool);
        // 宿主先停在本页（见 _parking 的说明）；必须在 HookAutoRun 之前
        _parking.Controls.Add(_inputHost);
        _parking.Controls.Add(_resultHost);
        Controls.Add(_parking);

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
        // 而 法兰增量温降=599.5 只是这个退化几何的下游噪声，不是热学结论。
        //
        // 为什么顶高逻辑没救它：EnforceTabLenFloor 挂在 ShowPrediction 上，
        // 而 ShowPrediction 只由 ParamChanged 调，ParamChanged 又被**首屏闸门**
        // （_userReady=false，防止排版事件被当成用户操作）挡住 ⇒ 启动时一次都没顶过。
        // ⇒ 在这里顶一次：默认值从此自洽，而且用的是**同一个** TabLenFloorMm，
        //   将来盘径/半宽/压接段的默认值改了，舌长会自己跟上，不必再记得改第二处。
        string bootFloor = EnforceTabLenFloor();

        _out.Text =
            (bootFloor.Length > 0
             ? "★ 舌长装不下铜排，已顶到装配下界：" + Environment.NewLine
               + bootFloor
               + Environment.NewLine
             : "") +
            // ★ 2026-09-02 首屏说明大幅缩短（用户：「对工程师不必要的说明可以消除」）。
            //   删掉的是「自动重算怎么触发」「预测值怎么来」这类**程序内部机制**
            //   —— 工程师改完参数会看到它自己重算，不必先读一段说明。
            //   留下的那条是**程序改了他填的数**：舌长被顶高了，不说就是静默改输入。
            "在「① 输入」填好参数（或读一张 .3dm 图纸）后，点「核算整线」—— 它会一路算到能出图为止；"
          + "过程写在这里，判据表与场图在「③ 结果与出图」。";
        // ★★★ 2026-09-02 抓图抓到：首屏这段的 `**` **原样露在屏幕上**。
        //   病因与 MainForm 里记着的那条同源 —— 本框此刻**句柄还没建**，
        //   `.Text =` 不触发 TextChanged（原生控件没窗口就没有 EN_CHANGE 通知）
        //   ⇒ TextFmt.Hook 的事件永远不会跑到这一段。
        //   ⇒ 这里**直接调一次**，不押在事件时机上。
        TextFmt.Write(_out, _out.Text);
        SyncGeomSource();
        // R17：输入控件整块在「① 输入」页上占满一页（不再与结果分栏）；③ 页判据表/场图的比例由 SyncTextSplit 按有没有结果调
        _resultSplit.HandleCreated += (_, _) => BeginInvoke(() => SyncTextSplit());
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
        // ★ 灰掉必须说清为什么 —— 灰着不解释就是哑谜（2026-09-02 与适用性一起补）。
        _btnVerify.ToolTipText =
            Shared is { Fresh: true, Last: { Ok: true } }
                ? "把网格一档档加密，直到这个数不再变为止。10～40 分钟，随时可取消。"
                : Shared?.Last is { Ok: true }
                    ? "参数在上次求解之后又动过了 —— 先点「核算整线」按现在这组重解。"
                    : "还没解过 —— 先点「核算整线」。没有解就无从谈「这个数准不准」。";
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
    ///   此前落档要人工把十几个数抄进 DesignSpec.cs，其中五个判据值还得从判据表逐个读，
    ///   抄错一位要跑 8 分钟 --selfcheck 才知道。手抄正是「同一个数存两处」的入口。
    ///
    /// ⚠ 程序写这五个记录值**不削弱** --selfcheck：A 段验的从来不是「设计对不对」，
    ///   而是「内核以后改了还能不能算出同一个数」。那五个值本来就来自同一次运行。
    ///
    /// ⚠ Name 由用户填、Binding 留空 —— 「什么咬住了它」是工程判断，不是程序能算的，
    ///   留空比替你猜一句更诚实。
    /// </summary>
    private void SaveAsDesignSpec()
    {
        // ★★★★★ A1（2026-09-02 用户拍板）：**程序不再替工程师否决**。
        //
        //   用户原话：「计算结果是如何就如何，超标就显示提醒，最终让工程师判断合格与否
        //   （风险由工程师判断）；若工程师判断可承担风险，工程师就可储存计算结果与出图」。
        //
        //   改之前这里硬拦「判据全过」，而且 CommandApplicable 里那条 AllOk 连
        //   「我知道风险，越关进入」都绕不过（Blocks 里 NotApplicable 排在门禁之前）
        //   ⇒ 工程师算出一个超标的结果，**连存都存不下来**。
        //   而「储存计算结果」是 输入 → 计算 → 储存 这条链的最后一步，不该由程序否决。
        //
        //   ⚠ 仍然硬拦的只有一条：**参数动过了**。那不是风险判断 ——
        //     存下去的会是**上一组参数**的解，与他看到的那张表对不上。这是错，不是风险。
        if (Shared is not { Last: { } r } f || !f.Fresh)
        {
            MessageBox.Show(this,
                "参数在上次求解之后又动过了。" + Environment.NewLine + Environment.NewLine
                + "现在存下去的会是**上一组参数**的解 —— 与你眼前这张表对不上。"
                + "先点「核算整线」按现在这组重解一次。",
                "还不能另存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // ── 超标 / 没算准：说清楚，让人自己判断 ────────────────────────────
        var over = r.Checks.Where(c => (c.Kind is CheckKind.HardSafety or CheckKind.Target)
                                       && (!c.Ok || c.Undetermined)).ToArray();
        bool verified = _meshVerify is { Converged: true } && Equals(_verifiedSnap, CurrentSnap());
        if (over.Length > 0 || !verified)
        {
            var sbW = new System.Text.StringBuilder();
            sbW.AppendLine("这一版有下面的问题。要不要存，由你判断 —— 风险你承担。");
            sbW.AppendLine();
            if (over.Length > 0)
            {
                sbW.AppendLine($"■ {over.Length} 条判据没过：");
                foreach (var c in over)
                    sbW.AppendLine(c.Undetermined
                        ? $"    {Criteria.Plain(c.Name)}　**判不了**（{c.Where}）"
                        // ★ 带单位（用户 2026-09-02：「全名 + 单位」）。同一张表里
                        //   K / W / mm / A/mm² 四种单位混着，不写单位就读不出量级。
                        : $"    {Criteria.Plain(c.Name)}　{c.Actual:0.000} / 限 {c.Limit:0.000}"
                        + $" {Criteria.UnitOf(c.Name)}　（{c.Where}）");
                sbW.AppendLine();
            }
            sbW.AppendLine(verified
                ? "■ 这些数已经加密复算过（算到不再变），可以按它们判断。"
                : "■ 这些数是在**粗网格**上算的，还没加密复算 —— **可能偏乐观**。" + Environment.NewLine
                  + "    实测同一个设计：粗网格 法兰增量温降 4.72 K（看着余量 53 %），" + Environment.NewLine
                  + "    加密到数不再变是 10.33 K —— 已经越限。");
            sbW.AppendLine();
            sbW.AppendLine("存下来的档会**带着这段话**，下一个人打开就看得见。");
            sbW.AppendLine();
            sbW.Append("要存吗？");
            if (MessageBox.Show(this, sbW.ToString(), "这一版有问题 —— 要存吗？",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        string name = Microsoft.VisualBasic.Interaction.InputBox(
            "给这一档起个名（会成为文件名与下拉里的显示名）：",
            "另存为设计记录", $"管壁 {(double)_wall.Value:0.0} · 自定");
        if (string.IsNullOrWhiteSpace(name)) return;

        var d = PageToDesignSpec();
        d.Name = name.Trim();
        d.Provenance = $"由 APP「另存为设计记录」写出；解出自「{(_srcAnalytic.Checked ? "解析形状" : "Rhino .3dm")}」路径";
        d.Binding = "";                       // ← 工程判断，留给人填
        d.TotalMassG = r.TotalMassG; d.TubeMassG = r.TubeMassG; d.FlangeMassG = r.FlangeMassG;
        // 五个回归基准值：**从本次解直接取**，不经人手
        d.RampH = r.ValueOf(LineResult.Key.RampHours);   // R20：RampH 记的是集总升温用时（参考行），① 本身改成闭式管 J
        d.DiscOverK = r.ValueOf(LineResult.Key.DiscTemp);
        d.HoleFluxW = r.ValueOf(LineResult.Key.NetFlux);
        d.FlangeDipK = r.ValueOf(LineResult.Key.FlangeDip);
        d.TubeJ = r.ValueOf(LineResult.Key.TubeJ);

        // ★★ A2：**判据值要带口径** —— 上面那五个数是在哪张网格上算的，必须一起存。
        //   同一个设计：粗网格 4.720 K（看着余量 53 %）／加密复算后 10.329 K（越限）。
        //   不存口径，档自己说不清拿的是哪一个 —— 现役两档的记录值正是这么来的。
        if (verified && _meshVerify is { Line: { } ml } mv)
        {
            d.VerifiedMeshMm      = mv.FineMm;
            d.VerifiedFlangeDipK  = ml.ValueOf(LineResult.Key.FlangeDip);
            d.VerifiedHoleFluxW   = ml.ValueOf(LineResult.Key.NetFlux);
            d.VerifiedDiscOverK   = ml.ValueOf(LineResult.Key.DiscTemp);
        }
        // ⚠ 有问题就把话写进档 —— 「风险由工程师判断」的前提是**风险被记录下来**，
        //   否则下一个人打开时，它跟一个真正合格的设计长得一模一样。
        d.VerifiedNote = over.Length > 0 || !verified
            ? "⚠ 存档时这一版**有已知问题**，由工程师判断后仍决定保存："
              + (over.Length > 0
                    ? "　**越限/判不了**：" + string.Join("；", over.Select(c => c.Undetermined
                        ? Criteria.Plain(c.Name) + " 判不了"
                        : $"{Criteria.Plain(c.Name)} {c.Actual:0.000}/限 {c.Limit:0.000} {Criteria.UnitOf(c.Name)}"))
                    : "")
              + (verified ? "　（数已加密复算到不再变）"
                          : "　⚠ **这些数是粗网格上算的，没有加密复算，可能偏乐观**。")
            : "";

        try
        {
            string path = DesignSpecStore.Save(d);
            MessageBox.Show(this,
                $"已写出：{path}" + Environment.NewLine + Environment.NewLine
                + "下一步：" + Environment.NewLine
                + "  · 把「binding」填上 —— 什么咬住了它（余量最小的那条）。" + Environment.NewLine
                + "    这是工程判断，程序算不出来，只有你知道。" + Environment.NewLine
                + Environment.NewLine
                + "  （落档的回归自检与版本记录由开发侧完成，你不必操作。）",
                "已另存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // 重扫磁盘，让新档立刻出现在**每一个**设计记录下拉里。
            // 少了这一句，界面会说「已写出」而下拉里找不到它 —— 工程师只能
            // 猜是没存上，于是再存一次（撞重名被拒），或者干脆不信这个功能。
            DesignSpec.Reload();
            if (DesignSpecStore.LoadErrors.Count > 0)
                MessageBox.Show(this,
                    "档已写出，但重扫 finaldesigns/ 时有档读不进来：" + Environment.NewLine
                    + string.Join(Environment.NewLine, DesignSpecStore.LoadErrors) + Environment.NewLine
                    + Environment.NewLine + "少一个档 = 少一组回归基准，不要放着不管。",
                    "读档有错", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "另存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 把设计记录下拉重填一遍，**按档名保住当前选中的那一档** —— 不能按下标，
    /// 新档追加在内置档后面，下标会移位。
    ///
    /// 本页的下拉**没挂** SelectedIndexChanged（换档只是改选择，要点「载入设计记录」才生效），
    /// 所以重填不触发任何计算。哪天给它挂上了事件，这里必须同时加抑制位，
    /// 否则重扫一次就等于替用户按了一下载入。
    /// </summary>
    private void RefillCaseBox()
    {
        string keep = _caseBox.SelectedItem as string ?? "";
        _caseBox.Items.Clear();
        foreach (var fd in DesignSpec.All) _caseBox.Items.Add(fd.Name);
        int i = _caseBox.Items.IndexOf(keep);
        if (i < 0) i = System.Array.IndexOf(DesignSpec.All, DesignSpec.Current);
        _caseBox.SelectedIndex = i < 0 ? 0 : i;
    }

    private void OnDesignSpecsReloaded(object? sender, System.EventArgs e)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(new System.Action(RefillCaseBox)); return; }
        RefillCaseBox();
    }

    /// <summary>
    /// ⚠ <see cref="DesignSpec.Reloaded"/> 是**静态**事件：不退订，这个页面就永远被它拿着。
    /// 真机上只有一个实例、无所谓，但 UiWiring 一个进程里反复造窗体 ——
    /// 旧实例的处理器会跟着累加，然后往**已销毁的控件**上写，
    /// 报出来的错跟真正的病因八竿子打不着。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) DesignSpec.Reloaded -= OnDesignSpecsReloaded;
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
        // ★★★★★ 2026-09-03 放宽：**1 级（等厚板）不再拒**。
        //
        //   原来要求 ≥2 级，于是 Pt_Heater1.3dm 这种等厚板整个被拒在门外
        //   （F 端到端就是撞死在这里）。查清之后发现那道闸卡错了地方：
        //   `SolveByLevel` 是**两层**的 ——
        //     外层：每片一个整体厚度 t[j]，靶是抽热误差（②′ 抽不够 ⇒ 加厚；③ 抽太多 ⇒ 削薄）
        //     内层：每片各级的**相对比例**，压局部过热
        //   1 级时**只有内层**失效（归一化 adj/norm ≡ 1，比例永远不动），
        //   **外层照样能调整片厚度** ⇒ 拒绝整个功能是把能用的那一半也砍了。
        //
        //   ⚠ 现在只剩一种拒绝：**还没分析**（没有厚度场，无从算起）。
        f.SizerNoLevels = !_srcAnalytic.Checked && _levels is not { Length: > 0 };
    }

    internal bool CommandApplicable(string cmdId) => cmdId switch
    {
        // 读 .3dm 反推台阶 —— 解析形状是程序生成的，没有图纸可反推
        "geom.analyze" => !_srcAnalytic.Checked && !string.IsNullOrWhiteSpace(_file3dm[0].Text),
        // 形状搜索只在解析模式有意义：.3dm 的形状由图纸给定，不是可搜索的自由度（见下）
        // 图纸几何 → 参数：**得先分析过**（否则没有形状可交），且只在 .3dm 模式下才谈得上
        "geom.toanalytic" => !_srcAnalytic.Checked && _shape is not null,
        // ★★ 加密复算：**没有当前参数的解就不适用**（2026-09-02 抓图抓到）。
        //   实况：开箱进来「结果：还没解过」，而这个按钮是黑的、点得下去 ——
        //   点了只弹一句「先在本页点核算整线」。而本方法自己的说明写着：
        //   「能点但点了只弹一句『请先切到…』」正是用户最初抱怨的那个形状。
        //   ⚠ 条件与 VerifyMeshAsync 的前置**同一套**：有解、且解对应当前参数。
        //     两处不一致的话，要么灰着却能跑，要么亮着却拒绝 —— 都在骗人。
        "core.verifyMesh" => Shared is { Fresh: true, Last: { Ok: true } },

        // ★★★★★ 原 ③→④ 那道门**降级到这里**（2026-09-02 阶段轨合并）。
        //
        //   那道门的条件是「解得出来且收敛 + 参数没动过」，理由写在 Flow.cs：
        //   「定尺寸器每轮都要跑一次整线解，起点必须是一个解得出来且收敛的构型」
        //   —— 否则几十分钟全烧在一个坏几何上。
        //
        //   ⚠ 合并阶段时这道门**真的消失过**：接线测试当场报
        //   「④ 未解锁时它是禁用的　Enabled=True」。计划里我自己把它标成
        //   「本次唯一真风险」，然后还是漏了 —— 是门抓住的，不是我记得。
        //   ⚠ 这不是风险判断（那类由工程师定，见 SaveAsDesignSpec），
        //     是**没有起点就没法开始**。
        "core.autoThick" => Shared is { Fresh: true, Last: { Ok: true, Converged: true } },
        "shape.search" => _srcAnalytic.Checked
                          && Shared is { Fresh: true, Last: { Ok: true, Converged: true } },
        // 另存：存的是**当前这个解**，所以「参数没再动过」是硬条件。
        //   ⚠ 「判据全过」**不再是条件**（2026-09-02 改）。旧注释写着
        //     「不成立的设计不该有一个能落档的形态」—— 那是程序替工程师做判断。
        //     用户拍板：结果如何就如何，超标显示提醒，风险由工程师判断。
        // ★ A1（2026-09-02）：**去掉 AllOk** —— 程序不替工程师否决，超标由他判断（见 SaveAsDesignSpec）。
        //   仍要 Fresh：参数动过之后存下去的是上一组参数的解，那是错，不是风险。
        "final.save" => Shared is { Fresh: true },
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

    /// <summary>舌保温框：初始值与上下界都只有一个来源（StartPoint / SizerOptions）。</summary>
    private static NumericUpDown Ins() =>
        Num((decimal)StartPoint.TabInsulMm, (decimal)SizerOptions.InsLoMmConst,
            (decimal)SizerOptions.InsHiMmConst, 0.1m, 1);

    /// <summary>
    /// **不勾「逐片自定」时，把旧规则算出来的值显示在框里**（并禁用）。
    ///
    /// ★ 为什么要显示而不是留空：本项目既定诉求是「不看说明书也能用」。
    ///   一个空的、禁用的框只告诉人「这里不能改」，不告诉人「不改时是多少」——
    ///   而**看不见又在起作用的量是安静失败的温床**（本页自己的原话）。
    ///
    /// 规则出自 <see cref="DesignSpec.RingRadiiOf"/> 与 <see cref="DesignSpec.RingMulOuter"/>，
    /// 这里**只镜像不另立**；两边若漂开，门会红（RingShapeInputTests）。
    /// </summary>
    private void SyncRingShape()
    {
        bool custom = _ringShapeCustom.Checked;
        bool old = _suppressAuto; _suppressAuto = true;
        try
        {
            for (int j = 0; j < _ringR1.Length; j++)   // ★ 按实际片数，不写死 4
            {
                _ringR1[j].Enabled = _ringR2[j].Enabled = _ringT2[j].Enabled = custom;
                if (custom) continue;
                decimal C(double v, NumericUpDown n) => Math.Clamp((decimal)v, n.Minimum, n.Maximum);
                double w = (double)_ringW.Value, t1 = (double)_ringMul[j].Value;
                _ringR1[j].Value = C(w, _ringR1[j]);
                _ringR2[j].Value = C(2 * w, _ringR2[j]);
                _ringT2[j].Value = C(1 + (t1 - 1) * 0.4, _ringT2[j]);
            }
        }
        finally { _suppressAuto = old; }
    }

    /// <summary>环倍率框：同上。</summary>
    /// <summary>
    /// 环倍率框。下界 0.20（不是求解器的 1.00）：图纸上孔边那一级可以比基板**薄**
    /// （Pt_Heater3.3dm 就是 1.0／2.0／3.0 mm），「图纸几何 → 参数」要装得下它才叫照图纸算。
    /// 求解器自己的盒仍从 1.00（无台阶）起只增不减，两者是两回事。
    /// </summary>
    private static NumericUpDown Ring() =>
        Num((decimal)StartPoint.RingMul, 0.20m,
            (decimal)SizerOptions.RingHiConst, 0.05m, 2);

    /// <summary>舌片厚显示框（只读）：值由 <see cref="DesignSpec.SizeTongues"/> 算出，工程师改不了 —— 它不是旋钮。</summary>
    private static NumericUpDown Tongue()
    {
        var n = Num(0m, 0m, 30m, 0.01m, 2);
        n.Enabled = false;
        return n;
    }

    /// <summary>
    /// 内级外扩 r₁ 框。范围取 `--monotone` **实测扫过的量程** 1→10 mm（2026-08-30），
    /// 不是拍的数：那一段上三条判据都实测过单调，量程之外没有依据。
    /// </summary>
    /// ★ 2026-09-08 上界放到 30、下界 0.5：范围原按实测量程给，但「图纸几何 → 参数」要装得下图纸
    ///   （Pt_Heater3 内级 r₁ = 孔+10.0、外级 r₂ = 孔+20.0），装不下就是静默换零件。求解器的上界另在 SolverOptions。
    private static NumericUpDown RingR() => Num((decimal)StartPoint.RingWidthMm, 0.5m, 30m, 0.5m, 1);

    /// <summary>外级外扩 r₂ 框。同上，实测量程 4→16 mm。</summary>
    private static NumericUpDown RingR2() => Num((decimal)(2 * StartPoint.RingWidthMm), 1m, 40m, 0.5m, 1);

    /// <summary>圆盘背侧减重槽张角（度）。0 = 不开槽 —— 开箱默认，行为与从前逐位相同。</summary>
    private static NumericUpDown Slot() => Num(0m, 0m, 340m, 5m, 0);

    /// <summary>舌板开孔孔径（半径 mm）。0 = 无孔 —— 开箱默认，行为与从前逐位相同。</summary>
    /// <summary>孔径 mm：R15（用户 2026-09-08）孔径 &lt; 1 mm 的孔不考虑 ⇒ 取值域 {0} ∪ [1, 30]，步进 1；填进 (0,1) 的数按无孔算（Core 的 TabHoleREffective）。</summary>
    private static NumericUpDown Hole() => Num(0m, 0m, 30m, 1m, 2);

    /// <summary>孔的顺流拉长比。1 = 圆；>1 = 顺着电流拉长的椭圆。</summary>
    private static NumericUpDown HoleAsp() => Num(1m, 1m, 3m, 0.1m, 1);

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
        _tongueFixed = null;                                  // R11：参数动过 ⇒ 舌片厚回到 I/(J·舌宽) 规则（放在早退之前）
        _fixedDerived = null;                                 // R12/R13：场定位置与形状也回到默认规则
        if (_solvedSnap is null && _last is null) return;    // 本来就没有可作废的
        _solvedSnap = null;                                   // ⇒ Fresh = false，门关上
        _solvedRes = null;                                    // 外推基准也作废（它是另一组参数的解）
        // ⚠ 这里**不许写 null** —— 字段是非可空 string，Show 直接 .Length。
        //   2026-09-03 跑图纸路时整个进程带栈崩在这上面（详见 ParamChangedThenShowTests）。
        _pendingReview = "";                                  // 待插入的形状体检同理
        // 上一次那句「不可行」与上一次的推理过程，都是关于**上一组输入**的 ⇒ 一起作废
        _sizerInfeasible = false;
        _lastTrace = System.Array.Empty<string>();
        PushFlow();
        NoteUserInputChanged(what);      // ★ 唯一入口：越关作废 + 一次性告知
        _out.Text = $"⚠ 参数表改了「{what}」—— 上一次的解**不再对应当前参数**。" + Environment.NewLine
                  + "   判据表留在下面供对照，但它是**上一组参数**的结论；" + Environment.NewLine
                  + "   「交付」那一格已经关上，请点「核算整线」按现在这组重解。"
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

    /// <summary>
    /// **「用户改了输入」的唯一入口**（2026-08-24）。
    ///
    /// 现在有两条路会走到这里：页面控件（<see cref="ParamChanged"/>）与
    /// 左侧参数表（<see cref="MarkParamsChanged"/>）。把**共同**的那部分收在这里，
    /// 是为了防第三条路：将来谁再加一个改输入的入口（程序化载入、新页面、
    /// 运行时生成的控件…），只要调它就全都对，不必再去记「还要顺手清越关」
    /// 「还要弹一次告知」。散着写，第三条路一定会漏其中一条 —— 本项目已经栽过太多次。
    ///
    /// ⚠ 两条路**故意**保留各自的差异，不强行统一：
    ///   · 页面控件那条**保留** <c>_solvedRes</c>（线性外推的基准）——
    ///     那几个雅可比常数就是按这 6 个控件测的，改它们时外推仍然有意义；
    ///   · 参数表那条**清掉**它 —— 改控温点/保温/牌号已经出了雅可比的定义域，
    ///     再拿旧解外推就是拿另一个构型的斜率说话（§7 记过这个错）。
    /// </summary>
    private void NoteUserInputChanged(string what)
    {
        Shared?.RestartChain();          // Fresh 转 false、越关作废、④⑤ 关门
        WarnParamsRestartOnce(what);     // 一次性告知
    }

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
            + "  ·「交付」那一格的门已经关上；" + Environment.NewLine
            + "  · 之前若「越关」进过某一格，那张通行证也一并作废" + Environment.NewLine
            + "    （它是按旧参数批的）。" + Environment.NewLine + Environment.NewLine
            + "要拿到当前这组参数的结论，请回「整线核算」重解一次。"
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
            //   而它确实挂在 ToolStrip.Controls 上 ⇒ 递归会把「设计记录 ▾」也当成参数，
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
            //   同样会走到工具条上，而「设计记录 ▾」被当成参数会让切档触发分钟级重算
            //   （那个 bug 修过一次，接线测试第 1 项守着它）。
            c.ControlAdded += (_, e) => Watch(e.Control);

            foreach (Control k in c.Controls) Watch(k);
        }
        foreach (Control c in Controls) Watch(c);
        // ★★★★★ 段表一动，**逐片输入要跟着重建**（2026-09-02，用户点出的 bug）。
        //   片数 = 段数 + 1。此前界面写死四片：段表加到 HC4 之后核心要 5 片，
        //   而界面只有 4 片 ⇒ 算的是另一个零件。用户：「UI 段数是必须可调整的」。
        //   ⚠ 三个事件都要挂：改名（CellValueChanged）、删行（RowsRemoved）、
        //     加行（RowsAdded）—— 只挂前两个的话「加一段」正好漏掉。
        // ★★★★★ **片数真的变了才算「参数动了」**（2026-09-02 对帐红了才查出来）。
        //
        //   `RowsAdded` 是本次为了「加一段」新挂的，而 _segGrid 在**首次绑定/排版**时
        //   也会抛它 —— 于是排版事件被当成用户改参数，ParamChanged 里的
        //   `_cts?.Cancel()` 把**正在跑的整线解掐掉**，`_last` 永远是 null。
        //   实测后果：--reconcile 0.8 报「界面这边解出来了 (null)」，
        //   而输出框写着「参数已改…正在后台重算」——看起来像解不出来，其实是被掐了。
        //   ⚠ 这一族代码里早有记录（原注释说的是 CellValueChanged 在排版时抛），
        //     我加 RowsAdded 时没想到它也在同一条路上。
        // ★★★★★ **片数真的变了才动**（2026-09-02，探针取证之后才修对）。
        //
        //   `RowsAdded` 是为了「加一段」新挂的，而 DataGridView 在**排版/换列**时
        //   也会走 AddNewRow → RowsAdded（探针实测：10 次未被抑制的 ParamChanged
        //   全部来自这一条，调用栈是 OnColumnCollectionChanged_PostNotification）。
        //   ⇒ 排版被当成「用户改了参数」，而 ParamChanged 里有 `_cts?.Cancel()`
        //     **把正在跑的整线解掐掉** ⇒ `_last` 永远 null，
        //     而输出框写着「参数已改…正在后台重算」——看起来像解不出来，其实是被掐了。
        //
        //   ⚠ 我先修过一版「重建了才算参数变」，**没堵住** —— 那一版靠 _plateBox.Controls
        //     的状态判断，而排版期它是会变的。改成记住片数：只有这个数变了才是真的变了。
        void SegsChanged()
        {
            int n = PlateNames().Length;
            if (n == _plateCountShown) return;      // 排版事件：什么都没变
            _plateCountShown = n;
            RebuildPlateRows();
            RebuildFile3dmRows();      // ★ 图纸路的文件行同样按段数走（用户 2026-09-03）
            ParamChanged();                          // 段数真的变了 ⇒ 上一次的解作废
        }
        _segGrid.CellValueChanged += (_, _) => SegsChanged();
        _segGrid.RowsRemoved += (_, _) => SegsChanged();
        _segGrid.RowsAdded += (_, _) => SegsChanged();
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
    /// 设计记录的 90 mm 之所以能长期存在，正是因为舌长在程序里是个独立常数，
    /// 从来没人拿盘径去核对过它。⇒ 现在让盘径/舌宽一动，舌长**自己跟上来**。
    /// </summary>
    private double TabLenFloorMm()
    {
        double R = (double)_discD.Value * 0.5;
        double hw = Math.Min((double)_tabW.Value, R);
        double tangent = Math.Sqrt(Math.Max(0, R * R - hw * hw));
        return tangent + DesignSpec.Current.ClampLengthMm + FreeTabMin;
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
        return $"   ★ 舌长已由 {had:0} **自动顶到 {want:0} mm** —— 低于它铜排装不上（判据 {Criteria.Explain("⑤")}）。\r\n" +
               $"     舌长 = 圆盘切点 {Math.Sqrt(Math.Max(0, Math.Pow((double)_discD.Value * 0.5, 2) - Math.Pow(Math.Min((double)_tabW.Value, (double)_discD.Value * 0.5), 2))):0.0}" +
               $" + 压接段 {DesignSpec.Current.ClampLengthMm:0} + 自由段 {FreeTabMin:0}。\r\n" +
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
        _tongueFixed = null;                 // R11：用户改了页面参数 ⇒ 舌片厚回到 I/(J·舌宽) 规则
        _fixedDerived = null;                // R12/R13：场定位置与形状也回到默认规则

        // ★ 页面控件动了同样要「从头走一遍」（2026-08-24 用户要求）。
        //   新鲜度那一侧本来就自动成立（Snap 变了 ⇒ Fresh 变 false），
        //   但**越关**不会自己失效 —— 它是在**旧参数**上批的。
        //   放在首屏闸门之后：程序写控件、排版期的事件都不算「用户改了参数」。
        NoteUserInputChanged("页面参数");
        // ★ 同 MarkParamsChanged：参数真的动了 ⇒ 上一次那句「这组输入不可行」失效。
        //   （「搜形状」改盘径/舌宽也走这条路 ⇒ 换了形状之后厚度那条路重新可试。）
        _sizerInfeasible = false;
        _lastTrace = System.Array.Empty<string>();

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

    // ★ 实测雅可比（`--vary`，端点均已收敛，管壁 0.8 设计记录点附近）。
    //   ⚠ 只对**这个工作点附近**成立 —— 圆盘区最高温 由两个竞争峰决定，符号会随构型变
    //     （已经栽过一次：拿另一构型的符号外推，判反了）。
    //   ⇒ 外推只用来给「大概会往哪边走」，绝不当结论；超出一步就不显示。
    //
    // ★★★ 下面这三个「测点」常数是 2026-08-17 补的，**必须与上面那组斜率同时更新**。
    //   在此之前它们不存在，于是没有任何东西能回答「这组斜率是在哪个形状上测的」——
    //   换了形状照样外推。而斜率本身那句注释早就写着「符号会随构型变」。
    //   ⇒ 谁改斜率，就必须一起改这三个数；`ShowPrediction` 用它们判「本构型在不在范围内」。
    private const double JacDiscD = 60.0, JacTabLen = 140.0, JacTabW = 30.0;
    // 2026-08-17 `--vary` 在**新设计记录点**（盘Ø60／舌140×60／板厚 0.89/2.45/2.35/0.73）重测：
    //
    // ★ 2026-08-20 起，参数提示框与外推的「驱动项」文字都**插值自这里**，不再各抄一份。
    //   起因：这几个常数 08-17 就换成了新值，而提示框里还挂着旧构型的
    //   ③ +123／−221／+22.7 —— 三处数字对不上，谁也没发现。
    //   ⇒ 改这几个数只需改这一处；界面上所有引用它们的地方会自己跟着变。
    private const double dDip_dWall = -333.6, dJ_dWall = -5.99;
    private const double dDip_dPlate = +194.5;
    private const double dDip_dTubeIns = +34.0, dJ_dTubeIns = -0.57;

    // ★★★★★ **圆盘区最高温 的外推被撤掉了**（2026-08-17），这是有实测依据的决定，不是省事。
    //
    // 旧常数（另一个构型：舌 90×30、环 1.22、板厚约两倍）：
    //     ∂圆盘区最高温/∂管壁 = **+16.66**　∂圆盘区最高温/∂板厚 = **−14.59**
    // 新设计记录点重测：
    //     ∂圆盘区最高温/∂管壁 = **−0.12**　∂圆盘区最高温/∂板厚 = **+0.14**
    // ⇒ **两个都翻了符号，量级掉了 100–140 倍。**
    //
    // 物理上说得通：舌片加宽一倍之后孔周电流不再拥塞，圆盘区最高温 = −0.21 K 而限值是 +5 ——
    // 这条判据在本构型上**根本不活跃**，所以什么都推不动它。
    //
    // ⇒ 对一个「不动的量」做线性外推，最好的情况是噪声，最坏的情况是拿**反号**的斜率
    //   告诉用户「往那边走会更好」。两者都不该发生 ⇒ 不推，只在真解里报它的实测值。
    //   这正是那句注释警告过的事：「圆盘区最高温 由两个竞争峰决定，符号会随构型变」——
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
        double freeNow = (double)_tabLen.Value - tanNow - DesignSpec.Current.ClampLengthMm;
        // 解析层这四行是一张表：名称 / 值 / 单位 / 说明。
        // ⚠ 两条 ⚠ 告警**必须排在整张表之后**，不能夹在行与行中间：不带 \t 的整句
        //   会被当成普通句子，**把一张表断成两截**，两截各自量各自的列宽 ——
        //   于是「自由段」那行的数值列与下面三行错开，而错的时机偏偏是告警触发的时候。
        sb.AppendLine($"   自由段\t{freeNow:0.0}\tmm\t解析（判据「舌片自由段」 下界 {FreeTabMin:0}）" +
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
            //   步长小不等于可以外推 —— 换个构型，圆盘区最高温 的符号都可能翻。
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
                              "圆盘区最高温 的符号会随构型翻 ⇒ **不给预测**，等真解。）");
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
                // ⚠ 查找键与显示名是**两件事**：键走 LineResult.Key 常量（判据改名编译期就断），
                //   显示走 Criteria.Plain（剥掉代号）。原来两处都写字面量 "③" ——
                //   于是代号从这里漏到屏幕上，而且判据一改名它会**悄悄查不到**（返回 0）。
                P(Criteria.Plain(LineResult.Key.FlangeDip),
                  V(LineResult.Key.FlangeDip),
                  V(LineResult.Key.FlangeDip) + dDip_dWall * dW + dDip_dPlate * dP + dDip_dTubeIns * dI, 10.0,
                  $"板厚 {dDip_dPlate:+0;−0} K/mm　管壁 {dDip_dWall:+0;−0}　管保温 {dDip_dTubeIns:+0;−0}");
                P(Criteria.Plain(LineResult.Key.TubeJ),
                  V(LineResult.Key.TubeJ), V(LineResult.Key.TubeJ) + dJ_dWall * dW + dJ_dTubeIns * dI, 12.0,
                  $"管壁 {dJ_dWall:+0.00;−0.00}　管保温 {dJ_dTubeIns:+0.00;−0.00}");
                // ⚠ 列数必须与 P 完全一致，否则它会自成一张表、和上面两行对不齐
                sb.AppendLine($"   圆盘区最高温\t{V("圆盘区最高温"):0.00}\t**不外推**\t/ 5.0\t" +
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
    /// 为什么值得做（2026-08-16 实测）：默认轮数上限 200 只够贴着设计记录点用。
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
    /// ▶ 复现设计记录：按选中档的**完整几何**解一次，出判据表。
    ///
    /// 与「核算整线」的区别，一句话：
    ///   · 核算整线 —— 读**页面上的控件**（可以随便改，用来试）
    ///   · 复现设计记录 —— 读 <see cref="DesignSpec"/>，**完全不看页面**（用来复现交付数字）
    ///
    /// ⚠ 1b（2026-08-17）之后，「核算整线」用的是**同一套几何构造器**，
    ///   把设计记录参数填进页面也能复现设计记录值（界面接线测试第 16 项每次都验，差 0.000）。
    ///   那本条为什么还留着？——因为它**完全不读页面**：
    ///   用来排除「页面上某个控件被改过而自己没注意到」。
    ///   两条路给同一个数，才说明页面没被动过手脚；给不同的数，就该查页面。
    /// </summary>
    /// <summary>
    /// 输出区的两个开关：**明细**与**求解器诊断**默认收起（2026-09-02 用户：
    /// 「输出也可以简化，只保留场图与前后尺寸比较与其它你觉得重要的计算结果」）。
    /// ⚠ 收起 ≠ 删除：未收敛时诊断**强制展开**（那时它就是最重要的信息）。
    /// </summary>
    private readonly CheckBox _showDetail = new()
    { Text = "逐段 / 逐片明细", AutoSize = true, Margin = new Padding(0, 2, 12, 2) };
    private readonly CheckBox _showDiag = new()
    { Text = "求解器诊断", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

    /// <summary>流水线开跑那一刻的尺寸与铂重 —— 用来印「这次改了什么（前 → 后）」。</summary>
    private Snap? _beforeSnap;
    private double _beforeMassG = double.NaN;

    /// <summary>取消/出错时置起：流水线看到它就停，不再往下一步走。</summary>
    private bool _pipeAborted;

    /// <summary>流水线当前跑到第几步（进度文字的前缀）。空 = 不在流水线里。</summary>
    private string _pipeStep = "";

    /// <summary>
    /// ★★★★★ **【核算整线】一键跑到底**（2026-09-02 用户拍板）。
    ///
    /// 用户原话：「自动定厚 / ◇ 搜形状 / ◆ 加密复算，能自动吗？全整到核算整线」，
    /// 并选定「搜形状也自动跑，一路跑到底」，附加「要有进度条 + 状态说明，
    /// 蓝色指示保留（说明要改）」。
    ///
    /// ══ 不新造决策逻辑
    ///
    /// 「下一步该干什么」<see cref="Flow.Next"/> 早就在回答了（蓝色指示读的就是它）。
    /// 一键 = **照它一直走**，走到它说「可以出图」为止。
    /// ⇒ 自动跑与手动点走的是**同一条判断**，不会出现「链路说 A、一键做 B」。
    ///
    /// ══ 三条护栏
    ///
    /// ① **取消要断整条**：任一步被取消/出错就停，不再往下走（<see cref="_pipeAborted"/>）。
    /// ② **原地打转要停**：指纹（判据实测值 + 总铂）两轮不变就停 ——
    ///    「做了等于没做」而提示继续指同一个按钮，是走查器抓过的死循环形状。
    /// ③ **上限**：最多 8 步。撞上限要**说出来**，不许静默停在半路。
    ///
    /// ⚠ .3dm 那条路上「◈ 图纸几何 → 参数」是**改几何来源**的决定，不自动做 ——
    ///   它把设计从图纸路搬到解析路，那是人的决定，不是一步计算。
    /// </summary>
    private async Task RunPipelineAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }   // 再点一次 = 取消
        _pipeAborted = false;
        string lastFinger = "";
        int repeat = 0;

        bool App(string id) => CommandApplicable(id);
        string Finger() => _last is null ? "" :
            string.Join("|", _last.Checks.Where(c => c.Kind != CheckKind.Reference)
                                         .Select(c => c.Name + "=" + c.Actual.ToString("0.000")))
            + "|g=" + _last.TotalMassG.ToString("0.00");

        // 第 1 步永远是解一次 —— 没有解就谈不上任何判断
        _pipeStep = "第 1 步／解一次整线";
        await RunAsync(autoSize: false);
        // ★ 记下「前」——「这次改了什么」比的是**流水线动过什么**，
        //   所以基准取第 1 步解完那一刻，不是点按钮那一刻。
        _beforeSnap = CurrentSnap();
        _beforeMassG = _last?.TotalMassG ?? double.NaN;

        for (int step = 2; step <= 8 && !_pipeAborted; step++)
        {
            var ns = Flow.Next(Shared!, App);
            if (ns is null) break;

            if (ns.CmdId == "export.page3dm")
            {
                _out.AppendText(Environment.NewLine
                    + "★ 算完了：判据全过、是当前参数的解、而且已经加密复算到数不再变。"
                    + "可以到「交付」页出图。" + Environment.NewLine);
                break;
            }

            string what = ns.CmdId switch
            {
                "core.autoThick"  => Flow.Cmd("core.autoThick").Text,
                "shape.search"    => "搜形状（会改盘径与舌宽）",
                "core.verifyMesh" => "加密复算（算到数不再变）",
                "core.runLine"    => "重解一次",
                // ★★★★★ R19（用户 2026-09-08）：3DM 路要能进第 ② 步搜形状。
                //   三步流程「UI 或 3DM 输入 → 法兰优化 → 结果与出图」里，3DM 是**输入**的一种；
                //   优化本来就要改形状（② 按 J=10 定 r₁/t₁、r₂/t₂ = 搜形状），所以把图纸反推成参数
                //   不是「要人决定」，是读入图纸的方式 —— 流水线自己做，做完照常往下走。
                //   ⚠ 照图纸解的那一次（第 1 步）仍先跑：那是「改前」的基准，出图时对照用。
                "geom.analyze"    => "分析图纸（反推几何变数）",
                "geom.toanalytic" => "图纸几何 → 参数（交给解析路，形状从此可改）",
                _ => "",
            };
            if (what.Length == 0)
            {
                // 指到流水线不会做的命令（要人决定的），停下来说清楚
                _out.AppendText(Environment.NewLine + "■ 自动到此为止 —— 下一步要你决定："
                    + Environment.NewLine + "   " + ns.Why + Environment.NewLine);
                break;
            }

            _pipeStep = $"第 {step} 步／{what}";
            _out.AppendText(Environment.NewLine + "▸ " + _pipeStep + " —— " + ns.Why + Environment.NewLine);

            switch (ns.CmdId)
            {
                case "core.autoThick": await RunAsync(autoSize: true); break;
                case "core.runLine":   await RunAsync(autoSize: false); break;
                case "shape.search":   await SearchShapeAsync(); break;
                case "core.verifyMesh": await VerifyMeshAsync(); break;
                case "geom.analyze":   AnalyzeShape(); break;            // 同步（起 Geom 子进程，秒级）
                case "geom.toanalytic": AdoptShapeToAnalytic(); break;   // 同步，不起解；改的是参数
            }
            if (_pipeAborted) break;
            // ★ 这两步改的是**参数**，不起解 ⇒ 判据表本来就不该变，拿指纹判它会误报「打转」
            if (ns.CmdId is "geom.analyze" or "geom.toanalytic")
            {
                if (ns.CmdId == "geom.toanalytic" && !_srcAnalytic.Checked)
                {
                    _out.AppendText(Environment.NewLine + "■ 停在这里：图纸几何没能交给解析路（见上面的原因）。" + Environment.NewLine);
                    break;
                }
                continue;
            }

            // ★ 原地打转：做了等于没做，而指路会继续指同一个按钮 —— 停
            string f = Finger();
            repeat = (f.Length > 0 && f == lastFinger) ? repeat + 1 : 0;
            lastFinger = f;
            if (repeat >= 1)
            {
                _out.AppendText(Environment.NewLine
                    + "■ 停在这里：上一步跑完，判据表与总铂**逐字未变** —— 再走下去是死循环。"
                    + Environment.NewLine);
                break;
            }
            if (step == 8)
                _out.AppendText(Environment.NewLine
                    + "■ 走满 8 步仍没到「可以出图」—— 停下来，别让它无限跑。"
                    + Environment.NewLine);
        }
        _pipeStep = "";
        PushFlow();
    }

    private async Task ReproduceAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= DesignSpec.All.Length) return;
        var fd = DesignSpec.All[i];

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnRepro.Text = "取消"; _btnRun.Enabled = _btnAuto.Enabled = false;
        _prog.Visible = true; _prog.Style = ProgressBarStyle.Marquee;
        _status.Text = "复现中…（分钟级）";
        // ★ 告诉阶段轨「正在跑哪条链」——右上角状态面板据此显示「正在算：…」，
        //   而且它**切到哪一页都看得见**（④ 页自己没有进度条）。
        //   同时它是互斥闸：SyncGates 会把所有会起算的命令禁掉。
        Shared?.SetRunning(ChainId.C整线耦合, "复现设计记录");
        // ★ 带上百分比与已跑时长 —— 否则状态面板只会转圈（见 PctOf 的说明）。
        var clockR = System.Diagnostics.Stopwatch.StartNew();
        var prog = new Progress<string>(s => OnUi(() =>
        {
            _status.Text = s;
            // ★ 流水线里要说清「第几步／在做什么」—— 否则跑一小时只看到一行滚动的轮数
            Shared?.SetRunningNote(
                (_pipeStep.Length > 0 ? _pipeStep + "　" : "")
                + $"已跑 {clockR.Elapsed.TotalMinutes:0.0} 分　{s}", PctOf(s, 40));
        }));

        try
        {
            // ═══ 一键走完全程（2026-08-21 用户要求）：灌控件 → **仍从档解** → 发布状态 ═══
            //
            // 病灶：此前本方法只写 `_last` + `Show()`，**既不设 _solvedSnap 也不 PushFlow**
            //   ⇒ FlowState.Last 从来没被推过、Fresh 恒 false
            //   ⇒ **复现出一个全判据通过的解，④⑤ 一格都不开**，阶段轨当作什么都没发生。
            //   与 Snap 那个引用相等 bug 同族：界面状态不反映实际。
            //
            // ① 先把设计记录值灌进页面控件 —— 让界面显示与档一致，CurrentSnap 才对得上。
            LoadDesignSpecFrom(fd, quiet: true);

            // ★ 快照取在**灌完控件、开解之前**这一刻（2026-08-24）。
            //   原来是解完再取 —— 复现要几分钟，这几分钟里控件可改，
            //   于是「解的那组」与「记下的那组」可以是两组，而 Fresh 判成 true。
            //   ⚠ 必须在 LoadDesignSpecFrom **之后**：上一行刚把设计记录值灌进控件，
            //     放到它前面记的就是用户原来那组，复现完会永远判成不新鲜。
            var snapAtStart = CurrentSnap();

            // ② **仍从档解**，不走 PageToDesignSpec()。
            //    保住这条独立路径是有代价换来的：PageToDesignSpec 是一段**搬运代码**，
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

            // 与 DesignSpec 记录值对账：不一致要**当场说出来**，不能等人自己发现
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("── 复现对账（本次实算 vs DesignSpec 记录值）");
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
            sb.AppendLine("   把设计记录参数填进页面，它也能给出上面这组数。两条路**应当一致**；");
            sb.AppendLine("   不一致就说明页面上有控件被改过，查页面，别怀疑内核。");
            _out.Text += sb.ToString();

            // ═══ ③ 发布状态 —— 没有这一步，复现出全判据通过的解 ④⑤ 也一格不开 ═══
            //
            // ⚠ 「新鲜」这个断言必须**说真话**：Fresh 的含义是
            //    「页面上这组参数就是解出这个结果的那组」。
            //    水头**不属于设计记录几何**（LoadDesignSpecFrom 故意不动它），
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
                    + "   ⇒ 不把它记作「页面参数的解」，仍需你点「核算整线」按页面工况重解一次。"
                    + Environment.NewLine
                    + "   水头是工艺量、不属于设计记录几何，所以「载入设计记录」不会覆盖它 —— 这是有意的。";

            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; _pipeAborted = true; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            _pipeAborted = true;   // 出错同样停整条
            MessageBox.Show(this, ex.Message, "复现失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _prog.Visible = false;
            _btnRepro.Text = "▶ 复现设计记录";
            _btnRun.Enabled = _btnAuto.Enabled = true;
            Shared?.SetRunning(null);          // 清在 finally：异常/取消也必须解除互斥
        }
    }

    /// <summary>
    /// 把选中的设计记录灌进各控件。**值只从 <see cref="DesignSpec"/> 取**——
    /// UI 里再抄一份，就是「同一个数存两处然后悄悄漂开」（HANDOVER §1.8 最常见的失效）。
    /// </summary>
    private void LoadDesignSpec()
    {
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= DesignSpec.All.Length) return;
        LoadDesignSpecFrom(DesignSpec.All[i], quiet: false);
    }

    /// <summary>
    /// 按**指设计记录**灌控件。<paramref name="quiet"/> = true 时**不写输出框** ——
    /// 供「▶ 复现设计记录」复用：它自己要在输出框里写复现对账，
    /// 不能被这里的「已载入设计记录…」整段冲掉。
    ///
    /// 拆出来是为了让两个入口共用同一段灌值代码 ——
    /// 各抄一份就是「同一件事存两处然后悄悄漂开」。
    /// </summary>
    private void LoadDesignSpecFrom(DesignSpec fd, bool quiet)
    {
        decimal C(double v, NumericUpDown n) =>
            Math.Clamp((decimal)v, n.Minimum, n.Maximum);

        // ⚠ 下面是**程序**在写控件，不是用户在改 —— 闭掉自动重算，
        //   否则这一批赋值会连环触发，还会把本方法的说明文字冲掉。
        _suppressAuto = true;

        _wall.Value = C(fd.WallMm, _wall);
        _tubeIns.Value = C(fd.TubeInsulMm, _tubeIns);
        _jDesign.Value = C(fd.JDesignAPerMm2, _jDesign);     // 设计记录带着它的 J（旧档 = 预设 10）
        _discD.Value = C(2 * fd.DiscRadiusMm, _discD);
        _tabLen.Value = C(fd.TabLengthMm, _tabLen);
        _tabW.Value = C(fd.TabHalfWidthMm, _tabW);
        _clamp.Value = C(fd.ClampTempC, _clamp);
        for (int j = 0; j < 4 && j < _tPlate.Length; j++)
            _tPlate[j].Value = C(fd.TabThickMm[j], _tPlate[j]);
        // ★★ 舌保温与环倍率也要灌进控件（2026-08-25）。
        //   ⚠ 这**不是**「拿设计记录当起点」—— 工程师**明确点了「载入设计记录」**，
        //     那是设计记录的正当用途：**校正计算流程**（载入 → 核算 → 对得上说明链路没坏）。
        //     被禁的是**静默继承**：没人要求的时候，PageToDesignSpec 自己去 Current 里捡。
        //   ⚠ 少了这两行，载入 0.6 档之后舌保温会停在控件默认的 0.3（裸舌），
        //     「载入设计记录 → 核算整线」就复现不出该档的数 —— 那正是这道校正要验的东西。
        _clampLen.Value = C(fd.ClampLengthMm, _clampLen);
        _fillet.Value = C(fd.TabFilletMm, _fillet);
        _ringW.Value = C(fd.RingWidthMm, _ringW);
        for (int j = 0; j < 4 && j < _tabIns.Length && j < fd.TabInsulMm.Length; j++)
            _tabIns[j].Value = C(fd.TabInsulMm[j], _tabIns[j]);
        for (int j = 0; j < 4 && j < _ringMul.Length && j < fd.RingMul.Length; j++)
            _ringMul[j].Value = C(fd.RingMul[j], _ringMul[j]);
        // 渐变环形状：设计记录里**给了**（非 NaN）才勾自定并灌进去；
        // 全是 NaN（现役两档都是）⇒ 不勾，SyncRingShape 会按旧规则把值显示出来。
        bool anyRing = false;
        for (int j = 0; j < fd.RingW1Mm.Length; j++)
            anyRing |= !double.IsNaN(fd.RingW1Mm[j]) || !double.IsNaN(fd.RingW2Mm[j])
                    || !double.IsNaN(fd.RingMul2[j]);
        _ringShapeCustom.Checked = anyRing;
        if (anyRing)
            for (int j = 0; j < fd.RingW1Mm.Length && j < _ringR1.Length; j++)
            {
                if (!double.IsNaN(fd.RingW1Mm[j])) _ringR1[j].Value = C(fd.RingW1Mm[j], _ringR1[j]);
                if (!double.IsNaN(fd.RingW2Mm[j])) _ringR2[j].Value = C(fd.RingW2Mm[j], _ringR2[j]);
                if (!double.IsNaN(fd.RingMul2[j])) _ringT2[j].Value = C(fd.RingMul2[j], _ringT2[j]);
            }
        SyncRingShape();
        // 定尺寸器上一次的解也一并作废 —— 否则跨档污染（换了档，旧解的旋钮还留着）
        _sizerTabIns = null; _sizerRingMul = null;
        // R11：这份记录的舌片厚原样带着（NaN = 与基板同），核算复现的就是它；工程师一改参数就回到规则
        _tongueFixed = (double[])fd.TongueThickMm.Clone();
        ShowTongues(fd);
        _fixedDerived = fd.Clone();          // R12/R13：记录里的槽心角／形状原样带着
        ShowDerived(fd);

        // 分段控温点：只改控温点，水头保持页面上原有的值（那是工艺量，不属于设计记录几何）
        // ⚠ 名字**按段数生成**：原来是写死的 { "HC1","HC2","HC3" } ⇒ 档里有 4 段时
        //   `segNames[k]` 当场 IndexOutOfRange，程序崩在「载入设计记录」上（2026-09-03 查出）。
        // ⚠ 长度也要带过来（用户 2026-09-03：每段直接加热管长可单独设定）——
        //   只带控温点的话，载入之后长度还是页面上一组，**存进去和读出来不是同一个设计**。
        // ⚠ 档里段数比页面少 ⇒ 多出来的行要删掉，否则会挂着上一个设计的段。
        int nSeg = fd.SetpointC.Length;
        for (int k = 0; k < nSeg; k++)
        {
            double len = k < fd.SegLengthMm.Length ? fd.SegLengthMm[k] : _base.TubeLengthMm;
            if (k < _segs.Count)
            {
                _segs[k].名称 = "HC" + (k + 1);
                _segs[k].控温C = fd.SetpointC[k];
                _segs[k].直接加热管长mm = len;
            }
            else _segs.Add(new SegRow
            { 名称 = "HC" + (k + 1), 控温C = fd.SetpointC[k], 直接加热管长mm = len });
        }
        while (_segs.Count > nSeg) _segs.RemoveAt(_segs.Count - 1);
        _segGrid.Refresh();

        if (quiet) { _suppressAuto = false; return; }

        _out.Text =
            // ★ 失效告示必须在**最前面**：这一段是用户载入设计记录后唯一会读的文字，
            //   把「本档已失效」写在第五行等于没写（§1.8：安静失败靠的就是没人看的位置）。
            (fd.Invalid.Length > 0
                ? "═══ ★★★ 本档已失效，不可作为交付值 ★★★ ═══\r\n" + fd.Invalid + "\r\n" +
                  $"（自由段 {fd.FreeTabMm:0.0} mm）\r\n═══════════════════════════════\r\n\r\n"
                : "") +
            "已载入设计记录：" + fd.Describe() + "\r\n" +
            "咬住它的：" + fd.Binding + "\r\n" +
            "出处：" + fd.Provenance + "\r\n" +
            $"外层耦合剩余误差估计 {fd.ResidualK:0.00} K（不是步长；见 HANDOVER §1.85）\r\n\r\n" +
            // ★ 这段话 2026-08-17（1b）之前是「本页表达不了两项，核算整线算的是另一片法兰」。
            //   1b 之后**不再成立**：解析模式与「复现设计记录」走同一个构造器，
            //   界面接线测试第 16 项每次都验「页面路径复现设计记录记录值」（差 0.000）。
            //   ⚠ 留着旧话比没有话更糟 —— 它会让人以为页面上的数不可信而绕开去用别的路径。
            "本页控件**没有**下面这几项，但它们已按设计记录值参与求解（界面上看不到）：\r\n" +
            $"   · 管孔渐变环 ×{DesignSpec.Fmt(fd.RingMul, "0.00")}" +
            (fd.RingMul[0] <= 1.001
                ? "（=1.00 即**不需要环**）\r\n"
                : $"，r ≤ 孔+{fd.RingWidthMm:0} 与 孔+{2 * fd.RingWidthMm:0} 两级\r\n") +
            $"   · 逐片舌保温 {DesignSpec.Fmt(fd.TabInsulMm, "0.0")} mm（守 {Criteria.Explain("管孔净流入")}/{Criteria.Explain("③")} 的主力旋钮）\r\n" +
            $"   · 压接段 {fd.ClampLengthMm:0} mm　舌根圆角 R{fd.TabFilletMm:0}　等宽舌片　管孔两面角焊缝\r\n" +
            "   ⇒ 现在点「核算整线」**就能**复现设计记录数字（与「▶ 复现设计记录」同一套几何）。\r\n" +
            "     两者的区别只剩：本按钮用页面上的水头，「复现设计记录」用内核默认值。";
        _suppressAuto = false;
    }

    /// <summary>
    /// ★★★ 出图前的拦截：**已声明失效的档一律不许出图**（2026-08-17）。
    ///
    /// 与 `--make3dm` 那条同根同源（用户当天发现新旧 3DM 一模一样）：
    /// 交付件不能是一个**自己声明不成立**的设计。而这条 UI 路径比 CLI 更危险 ——
    /// 下拉里作废档就排在现役档后面，隔一个位置，手一滑就选中了；
    /// 默认文件名又是 `设计记录_管壁0.8mm.3dm`，与现役档**一字不差**，
    /// 存到同一个目录就把好的那个盖掉，且**没有任何提示**。
    ///
    /// 单独抽成方法是为了能被界面接线测试直接调用（SaveFileDialog 是模态的，测不了）。
    /// </summary>
    public static string ExportBlockedReason(DesignSpec fd) =>
        fd.Invalid.Length == 0 ? "" :
        "★ 本档已声明失效，**不出图**。\r\n" + fd.Invalid + "\r\n" +
        $"（自由段 {fd.FreeTabMm:0.0} mm）\r\n\r\n" +
        "交付件不能是一个自己声明不成立的设计。要看它长什么样，请用「使用说明」页 —— " +
        "那里会连同失效原因一起画出来。";

    /// <summary>导出选中设计记录的整机 3DM（子进程渲染 + 写完从磁盘回读自校）。</summary>
    private void ExportFinal3dm()
    {
        int i = _caseBox.SelectedIndex;
        if (i < 0 || i >= DesignSpec.All.Length) return;
        var fd = DesignSpec.All[i];

        string blocked = ExportBlockedReason(fd);
        if (blocked.Length > 0) { _out.Text = blocked; return; }

        using var dlg = new SaveFileDialog
        {
            Filter = "Rhino 3DM|*.3dm",
            // 文件名带上档名：只按管壁命名时，两个同壁厚的档会写成同一个文件名而互相覆盖
            FileName = $"设计记录_管壁{fd.WallMm:0.0}mm_舌{fd.TabLengthMm:0}x{2 * fd.TabHalfWidthMm:0}.3dm"
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

    /// <summary>
    /// ★★★ **把求解器报的话翻成看得见的进度**（2026-08-30）。
    ///
    /// 病灶：`SetRunningNote(note, pct)` 是进度通道，而**只有「搜形状」传了 pct**；
    /// 「自动定厚」「核算整线」「复现」都只传文字 ⇒ 状态面板永远画**走马灯**，
    /// 说不出「跑到哪了」。而信息其实一直都有 —— 求解器每轮都在报「第 N 轮」，
    /// 外层耦合报「外层耦合 n/600」。**只是没人把它接到进度条上。**
    ///
    /// 用户 2026-08-30 抓图问「这页为何没有进度条」，实物就是这个：
    /// 条子在（状态面板里，切到哪一页都看得见），但它转圈转到底。
    ///
    /// ⚠ 解析不出轮数时返回 −1（走马灯）——**不许拿一根不动的空条冒充「有进度」**。
    /// </summary>
    /// <param name="line">求解器/耦合器报上来的原话</param>
    /// <param name="maxRounds">这一段的轮数上限（求解器的 MaxRounds）</param>
    private static int PctOf(string line, int maxRounds)
    {
        // 「第  2 轮　合计 …」——求解器每轮开头都报
        var m = System.Text.RegularExpressions.Regex.Match(line, @"第\s*(\d+)\s*轮");
        if (m.Success && maxRounds > 0 && int.TryParse(m.Groups[1].Value, out int r))
            return (int)Math.Clamp(100.0 * r / maxRounds, 0, 99);
        // 「外层耦合 3/600（ω=0.35）」——一次场解内部的进度
        m = System.Text.RegularExpressions.Regex.Match(line, @"外层耦合\s*(\d+)\s*/\s*(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int a)
                      && int.TryParse(m.Groups[2].Value, out int b) && b > 0)
            return (int)Math.Clamp(100.0 * a / b, 0, 99);
        return -1;
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
    ///   与 <see cref="DesignSpec.Plate"/> 逐字段比对，差的是**六项**：
    ///   两级渐变环、角焊缝、逐片舌保温、等宽舌片、舌根圆角、逐片独立厚度以外的分区。
    ///   ⇒ 用本页参数「核算整线」解的是一个**结构上更简单的法兰**，不是设计记录那一片。
    ///   判据仍然照实判（没有作假），但**不要拿它的数去和设计记录值比**。
    ///   把差异**打出来**，比悄悄用设计记录默认值补上更安全：后者会让人以为自己在试
    ///   设计记录构型，实际上试的是别的东西（§1.8 的形状）。
    /// </summary>
    private static string PageVsFinal(FlangePlate pg)
    {
        var miss = new List<string>();
        // ⚠ 设计记录值一律**现取**，不写字面量。这里原来硬编码「×1.22–1.24」，
        //   而 2026-08-17 重解后设计记录的环倍率是 1.00（不需要环）—— 又一处会悄悄漂开的抄写。
        if (pg.DiscStepRadiiMm.Length == 0)
            miss.Add($"管孔两级渐变环（当前设计记录 ×{DesignSpec.Current.RingMul[0]:0.00}" +
                     (DesignSpec.Current.RingMul[0] <= 1.001 ? "，即**不需要环**）" : "）"));
        if (pg.WeldFilletLegMm <= 1e-9) miss.Add("管孔两面角焊缝（设计记录 焊脚 = max(板厚, 壁厚)）");
        if (double.IsNaN(pg.TabInsulThickMm)) miss.Add("逐片舌保温");
        if (!pg.TabParallel) miss.Add("等宽舌片（本页是**梯形**，设计记录是等宽）");
        if (pg.TabFilletMm <= 1e-9) miss.Add("舌根过渡圆角（峰值电流拥塞就在这个凹角上）");
        return miss.Count == 0 ? "" : string.Join("\r\n         · ", miss);
    }

    /// <summary>
    /// 1b 之后：解析模式解的**就是**设计记录那套几何，所以要报的不再是「表达不了什么」，
    /// 而是「**本页没有控件的那几项，这次实际用了什么值**」。
    ///
    /// 为什么必须报：这几项都会显著改变结果（舌保温是守 管孔净流入/③ 的主力旋钮），
    /// 而它们在界面上看不见。看不见又在起作用的量，正是「安静失败」的温床 ——
    /// 与其藏起来，不如每次都摊开。
    /// </summary>
    private static string AnalyticUsedWhat(DesignSpec d) =>
        $"   · 压接段 {d.ClampLengthMm:0} mm（决定判据 {Criteria.Explain("⑤")}与舌片有效发热长度）\r\n" +
        $"   · 逐片舌保温 {DesignSpec.Fmt(d.TabInsulMm, "0.0")} mm（**守 {Criteria.Explain("管孔净流入")}/{Criteria.Explain("③")} 的主力旋钮**）\r\n" +
        $"   · 管孔渐变环 ×{DesignSpec.Fmt(d.RingMul, "0.00")}" +
        (d.RingMul[0] <= 1.001 ? "（=1.00 即不需要环）" : $"，环宽 {d.RingWidthMm:0} mm") + "\r\n" +
        $"   · 舌根圆角 R{d.TabFilletMm:0}　等宽舌片　管孔两面角焊缝（焊脚 = max(板厚, 壁厚)）\r\n" +
        $"   · 圆盘保温 {(d.FlangeInsulated ? $"{d.FlangeInsulMm:0} mm" : "不包")}（本页「法兰保温」控件）\r\n" +
        "   ⇒ 这几项本页没有控件；要改它们请用「自动定厚」/「◇ 搜形状」求解，" +
        "或改 Core/DesignSpec。";

    // ★★★ 这里原来有个 `MakePlate(double)` —— 本页自己造 FlangePlate 的那个方法。
    //   1b（2026-08-17）之后解析几何一律走 DesignSpec.Plate，它已经没有调用者。
    //
    //   **删掉而不是留着**：一个长得就像「几何构造器」的私有方法留在页面里，
    //   下一个人（包括我）要加功能时会顺手用它 —— 第二个几何来源就是这么长回来的。
    //   今天修的三条 bug 根都是「同一件事存了两处」，不能一边拆一边留个种子。
    //   要看它长什么样：git log 里有。

    /// <summary>
    /// ★★★★★ **1b：解析模式下，页面与内核共用同一个几何构造器**（2026-08-17）。
    ///
    /// 在此之前本页自己造 <c>FlangePlate</c>（旧的 MakePlate，已删），只填五个字段；
    /// 而 <see cref="DesignSpec.Plate"/> 还填渐变环、角焊缝、逐片舌保温、等宽舌片、舌根圆角。
    /// ⇒ 「核算整线」解的是**另一片法兰**，判据照实判，但那些数不能跟设计记录比。
    ///
    /// 「几何只有一个来源」这条铁律，在页面这里一直是破的。而 2026-08-17 一天里
    /// 抓到的三条 bug 根都是同一句：**同一件事存了两处**
    ///   · 压接段：页面用 3 mm 默认值，设计记录是 40（判据「舌片自由段」 因此判反）
    ///   · 3DM：作废档与现役档同名，把现役档整个覆盖
    ///   · 渐变环倍率：警告文字里硬编码「×1.22–1.24」，而设计记录早已是 1.00
    /// ⇒ 把页面这一处拆掉：解析几何一律走 <see cref="PageToDesignSpec"/> → <c>BuildCase</c>。
    ///
    /// ⚠ **行为会变**：同样的页面参数，「核算整线」的数会与以前不同（现在带环、带焊缝、
    ///   带舌保温）。这是**修正**不是回归 —— 以前那组数解的是一片不存在的法兰。
    ///   输出里会逐条列出本次实际用了什么值。
    ///
    /// ⚠ `.3dm` 那条路**保留旧路**：任意台阶几何 <c>DesignSpec</c> 表达不了。
    /// </summary>
    private LineCase BuildCase()
    {
        var rows = _segs.Where(s => !string.IsNullOrWhiteSpace(s.名称)).ToList();

        if (_srcAnalytic.Checked)
        {
            // ★ 与「自动定厚」「搜形状」「复现设计记录」走**同一个构造器**，不再另造一片
            var lcA = PageToDesignSpec().BuildCase(_base, checkRamp: true);
            // 水头是**操作条件**不是几何，DesignSpec 不带它 ⇒ 在这里补上（页面表格里有）
            lcA.HeadM = rows.Select(s => s.水头m).ToArray();
            lcA.SegLengthMm = rows.Select(s => s.直接加热管长mm).ToArray();
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
        //   「**数值边界不是设计值**」，设计记录用的是 40 mm。
        p.BusbarClampLengthMm = DesignSpec.Current.ClampLengthMm;

        var lc = new LineCase
        {
            Base = p,
            WallMm = (double)_wall.Value,
            UseMeasuredCurrent = false,          // 由控温反算 —— 第一性
            SetpointC = rows.Select(s => s.控温C).ToArray(),
            HeadM = rows.Select(s => s.水头m).ToArray(),
            // ★ 每段的直接加热管长（用户 2026-09-03）。≤0 的交给 Normalize 兜底。
            SegLengthMm = rows.Select(s => s.直接加热管长mm).ToArray(),
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
    /// ★★★★★ 把本页控件读成一个 <see cref="DesignSpec"/>（2026-08-17）。
    ///
    /// 为什么需要：「自动定厚」原来调的是 <see cref="FlangeAutoSizer"/> —— 它**只有板厚一个旋钮**，
    /// 靶是 ③，而且它自己的注释就写着「管不到 管孔净流入/圆盘区最高温」。
    /// 问题在于 ③ 与 管孔净流入 是**同一个抽热 D 的两侧**（实测 法兰增量温降 = 2.40·D）：
    /// 把 ③ 往下压 = 把 D 往下压 = **把 管孔净流入 往负里推**，也就是往「热倒灌进管子」那个方向走
    /// —— 那正是现场烧断的机理。旧器只会在事后让判据表去说「管孔净流入 没过」。
    ///
    /// ⇒ 改调 D8（<see cref="Sizer"/>）：舌保温守抽热窗口、环倍率守 圆盘区最高温、板厚只做接力与省铂。
    ///
    /// ⚠ D8 工作在**设计记录那套完整几何**上（逐片舌保温、渐变环、等宽舌片、舌根圆角、角焊缝），
    ///   而本页**没有这几项的控件**（见 <see cref="PageVsFinal"/>）。
    ///   ⚠ 2026-08-25 起：没有控件不等于本页不承载它们 ——
    ///     定尺寸算完会把舌保温与环倍率存进 <see cref="_sizerTabIns"/> / <see cref="_sizerRingMul"/>，
    ///     后续 PageToDesignSpec 会带上 ⇒ 「回 ③ 重解」复现的是同一个设计。
    ///     在那之前只带板厚回来，于是重解必然退回失败，指路与定尺寸两步死循环。
    ///   所以这里**明说**：自动定厚解的是完整构型，不是本页那片简化法兰。
    ///   与其让两套几何各解各的（那是「同一个数存两处」的老毛病），不如统一到 DesignSpec 这一套。
    /// </summary>
    private DesignSpec PageToDesignSpec()
    {
        var seed = DesignSpec.Current;
        var d = seed.Clone();
        d.Name = "本页参数";
        d.Provenance = "由「整线设计」页控件读入，D8 定尺寸";
        d.Binding = ""; d.Invalid = ""; d.InvalidChecks = Array.Empty<string>();
        d.WallMm = (double)_wall.Value;
        d.TubeInsulMm = (double)_tubeIns.Value;
        d.JDesignAPerMm2 = (double)_jDesign.Value;          // ★ 用户 2026-09-09：J 由工程师设定；SizeTongues／下角／判据限值都从它来
        d.DiscRadiusMm = (double)_discD.Value * 0.5;
        d.TabLengthMm = (double)_tabLen.Value;
        d.TabHalfWidthMm = (double)_tabW.Value;
        d.ClampTempC = (double)_clamp.Value;
        // ★★★★★ 2026-08-28：这三个此前**只能取设计记录值**（「本页无控件」），
        //   现在都有控件了 ⇒ **页面上每一个进计算的量都有输入来源**，
        //   DesignSpec 不再是任何计算的起点或兜底，只剩回归基准这一个角色。
        d.ClampLengthMm = (double)_clampLen.Value;
        d.TabFilletMm = (double)_fillet.Value;
        d.RingWidthMm = (double)_ringW.Value;
        // ★★★★★ 舌保温与环倍率是**优化变量**，起点从**控件**读（用户 2026-08-25）。
        //   此前这里是「定尺寸带回来的有就用，没有才用**设计记录值**」——
        //   而 DesignSpec.Current 全仓只在声明处赋过值（恒为 W08）⇒ 载入 0.6 档之后，
        //   舌保温仍是 0.8 档的 0.4/0.4/0.5/0.3（W06 是 0.4/0.4/0.4/0.6）。
        //   现在定尺寸的结果由 AdoptSolvedDesign **写回控件**，控件是唯一来源。
        for (int i = 0; i < d.TabInsulMm.Length && i < _tabIns.Length; i++)
            d.TabInsulMm[i] = (double)_tabIns[i].Value;
        for (int i = 0; i < d.RingMul.Length && i < _ringMul.Length; i++)
            d.RingMul[i] = (double)_ringMul[i].Value;
        // ★★★ 渐变环形状（2026-08-30）：这三个此前**没有控件**，于是被静默继承自
        //   设计记录（`seed.Clone()` 带过来的）—— 本页那条「每一个进计算的量都有输入来源」
        //   的规矩，剩的最后三个例外。不勾自定 ⇒ 写 NaN，模型按旧规则算（与此前逐位相同）。
        // ★ 按实际片数（用户 2026-09-03：段数由 UI 决定）—— 写死 4 会让第 5 片保持默认值
        for (int i = 0; i < d.RingW1Mm.Length && i < _ringR1.Length; i++)
        {
            d.RingW1Mm[i] = _ringShapeCustom.Checked ? (double)_ringR1[i].Value : double.NaN;
            d.RingW2Mm[i] = _ringShapeCustom.Checked ? (double)_ringR2[i].Value : double.NaN;
            d.RingMul2[i] = _ringShapeCustom.Checked ? (double)_ringT2[i].Value : double.NaN;
            // ★ 槽张角不受「自定义环形状」那个开关管 —— 它是独立的一根旋钮，
            //   求解器会自己调它来治「③ 法兰增量温降」。控件不写进设计 = 控件是摆设。
            if (i < d.SlotSpanDeg.Length && i < _slotDeg.Length)
                d.SlotSpanDeg[i] = (double)_slotDeg[i].Value;
            if (i < d.TabHoleRMm.Length && i < _holeR.Length)
                d.TabHoleRMm[i] = (double)_holeR[i].Value;
            if (i < d.TabHoleAspect.Length && i < _holeAsp.Length)
                d.TabHoleAspect[i] = (double)_holeAsp[i].Value;
        }
        // ★★★ R12／R13：场定位置与形状**显式**写（默认规则 = NaN／0），不许从种子静默继承；
        //   本页代表一份完整设计（载入／解完）时原样带着，核算复现的就是它。
        for (int i = 0; i < d.FlangeCount; i++)
        {
            bool fx = _fixedDerived is { } f0 && f0.FlangeCount == d.FlangeCount;
            if (i < d.TabHoleXMm.Length)    d.TabHoleXMm[i]    = fx ? _fixedDerived!.TabHoleXMm[i]    : double.NaN;
            if (i < d.SlotCenterDeg.Length) d.SlotCenterDeg[i] = fx ? _fixedDerived!.SlotCenterDeg[i] : double.NaN;
            if (i < d.DiscCutRotDeg.Length) d.DiscCutRotDeg[i] = fx ? _fixedDerived!.DiscCutRotDeg[i] : double.NaN;
            if (i < d.TabHoleSides.Length)  d.TabHoleSides[i]  = fx ? _fixedDerived!.TabHoleSides[i]  : 0;
            if (i < d.DiscCutShape.Length)  d.DiscCutShape[i]  = fx ? _fixedDerived!.DiscCutShape[i]  : 0;
        }
        // 圆盘保温：本页**有**控件，接过去（BuildCase 里原来写死 20，已改成读字段）
        d.FlangeInsulated = _flIns.SelectedIndex != 0;
        d.FlangeInsulMm = d.FlangeInsulated ? (double)_flInsT.Value : 0;
        var rows = _segs.Where(s => !string.IsNullOrWhiteSpace(s.名称)).ToList();
        if (rows.Count > 0)
        {
            d.SetpointC = rows.Select(s => s.控温C).ToArray();
            d.SegLengthMm = rows.Select(s => s.直接加热管长mm).ToArray();
            // ★★★★★ 段数变了，逐片数组必须跟着变长/变短（2026-09-02 补）。
            //   `d` 是从别处克隆来的，它的逐片数组还是**旧段数**那个长度；
            //   段表加一段之后 FlangeCount 变 n+1，而数组仍是 n ——
            //   `DesignSpec.Plate(j)` 按下标取，会**越界或算出另一个零件**。
            //   ⚠ 补/删在倒数第二个位置（首=入口、末=出口），与 Fit 同一个规则。
            d.Fit();
        }
        // 起点：板厚用页面上的值。
        // ★★ 2026-08-25 更正：此处原写「起点只影响轮数，**不影响解**：每个旋钮对自己的靶单调」。
        //   **那句话是错的，已被实测推翻。** 同一形状（R30）只换板厚起点：
        //     0.8 档 3547 g vs 3664 g（+3.3 %）　0.6 档 2650 g vs 2971 g（+12.1 %）
        //   单旋钮对自己的靶单调 ≠ 耦合系统有唯一不动点；而且 Sizer 交回去的是
        //   **已访问点集上的 argmin**（bestFeas ?? bestAny），按定义就是路径相关的。
        //   ⇒ 起点是**会影响答案**的输入。详见 Core/ShapeSeed.cs 与 HANDOVER §0.0.3 ⑦。
        for (int i = 0; i < d.TabThickMm.Length && i < _tPlate.Length; i++)
            d.TabThickMm[i] = (double)_tPlate[i].Value;
        // ★★★★★ R11（用户 2026-09-08）：舌片厚**不是旋钮**，读完全部输入后按 I/(J·舌宽) 闭式定，并显示回只读框。
        //   照图纸核算也按这条 —— 图纸上舌片多厚只作对照（ShapeToAnalytic 的说明里写着）。
        try
        {
            if (_tongueFixed is { } tf && tf.Length == d.TongueThickMm.Length)
                d.TongueThickMm = (double[])tf.Clone();      // 复现一份完整设计：舌片厚原样（NaN = 与基板同，早于 R11 的记录）
            else
                d.SizeTongues(_base);
            ShowTongues(d);
            ShowDerived(d);
        }
        catch (Exception ex) { _status.Text = "舌片厚算不出：" + ex.Message; }
        return d;
    }

    /// <summary>把算出来的舌片厚写进只读框（不触发重算）。</summary>
    private void ShowTongues(DesignSpec d)
    {
        bool old = _suppressAuto; _suppressAuto = true;
        try
        {
            for (int i = 0; i < _tongue.Length && i < d.TongueThickMm.Length; i++)
            {
                // NaN = 与基板同厚（早于 R11 的记录）⇒ 显示基板厚，别让框里留着旧数
                double v = double.IsNaN(d.TongueThickMm[i])
                    ? (i < d.TabThickMm.Length ? d.TabThickMm[i] : 0) : d.TongueThickMm[i];
                _tongue[i].Value = Math.Clamp((decimal)v, _tongue[i].Minimum, _tongue[i].Maximum);
            }
        }
        finally { _suppressAuto = old; }
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
                // ★ 网格的最低点必须**造得出来**：写死的 25 对两个现役档（壁 0.6/0.8）
                //   都会被判据「圆盘盖得住管孔」 跳过 ⇒ 第 1 轮实际只探了 R30/R35（2026-08-25 查出）。
                //   抬到下界上，网格才是三个点。
                //   ⚠ 2026-08-29：这里原本手写 `25.0 + 2 * 壁厚`，与 ⑥ 的实现各写各的。
                //     数值上今天恰好相同（管孔 = 壁+25，焊脚下界 = max(烧穿 0.6, 壁)），
                //     但**没有任何东西保证明天还相同** —— 改走 ⑥ 自己的闭式反解。
                //   ⚠ 这只是**真下界**，不是最紧的下界：焊脚 = max(板厚, 壁厚)，
                //     而板厚要解完才知道，解出来通常是 1.6～2.5 mm（远大于壁厚）。
                //     最紧的那个由求解器在解完当场给（Solver.CoverCheck）。
                double wall6 = (double)_wall.Value;
                double minDiscAll = GeometryScreen.MinDiscRadiusMm(
                    holeRadiusMm: wall6 + 25.0,
                    thickMm: _base.WeldMinThicknessMm,
                    wallMm: wall6);
                double[] discs = ShapeSearchPlan.LiveDiscs(SearchDiscs, minDiscAll);
        double[] wFrac = SearchWFrac;
        // ★ 不是 const：走查器要能把它压到极小，好在**分钟级**验「接线对不对」
        //   （2026-08-25）。搜形状真跑是几十分钟 —— 那验的是「答案好不好」，
        //   与「把决策串进循环有没有串错」是两件事，不该只能靠跑满几十分钟才验得到。
        int screenRounds = SearchScreenRounds, finalRounds = SearchFinalRounds;
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
        sb.AppendLine($"盘径由判据「圆盘盖得住管孔＋焊脚」**闭式定下界**（不用搜）；下界不可行就二分。每点先筛 {screenRounds} 轮，胜出者跑 {finalRounds} 轮。");
        sb.AppendLine($"自由段下界 {FreeTabMin:0} mm（判据「舌片自由段」）　压接段 {DesignSpec.Current.ClampLengthMm:0} mm");
        sb.AppendLine("★ 舌长不是搜出来的，是**算出来的**：切点 + 压接段 + 自由段。");
        sb.AppendLine("随时可以点「取消」——**已经算完的形状结果不会丢**。");
        sb.AppendLine();
        // ⚠ 表头必须是 sb 的**最后一行**，后面不能垫空行：下面的数据行是随算随
        //   AppendText 贴上来的，只有与表头**连续**才会被认成同一张表；
        //   一旦断开，表头和数据各自算各自的列宽，就再也对不上了。
        sb.AppendLine("盘Ø\t舌宽\t舌长\t耗时 分	合计 g\t判定");
        _out.Text = sb.ToString();

        var rows = new List<(DesignSpec d, double mass, bool ok, string msg)>();
        try
        {
            // ★★★★★ **一轮 = 改一次形状（盘径/舌宽）+ 在它上面把梯度分布扫一遍**
            //   （用户 2026-08-25：「改变法兰直径与扫梯度分布算一轮」）。
            //   此前是**固定网格全枚举**：3 个盘径 × 2 个舌宽跑满就收工 ——
            //   走不出 25/30/35，也不会因为「都在变坏」提前停。
            //   现在：网格当**第 1 轮**（给基准点与方向），之后沿改善方向**外推**，
            //   一轮下来没有任何邻点更好就停。
            async Task EvalShape(double R, double hw)
            {
                    ct.ThrowIfCancellationRequested();
                    // ★ 早筛「造不出来」的盘径（判据「圆盘盖得住管孔」 会兜底，但那要先白跑十几轮）。
                    //   2026-08-17 实测：盘 R25 + 管壁 0.8 时孔半径 25.8 > 盘半径，孔比盘还大，
                    //   而这种几何**料最少**，不拦住它就会排在最前面。
                    //   ⚠ 2026-08-29：原本手写 `25.0 + 2 × 壁厚`，与 ⑥ 的实现各写各的
                    //     （而且同一个式子在本文件里有**两份**，只差变量名）⇒ 改走 ⑥ 的闭式反解。
                    //   ⚠ 这是**真下界，不是可行下界**：焊脚 = max(板厚, 壁厚)，板厚要解完才知道。
                    //     实测 0.8 档解出来最厚 2.45 ⇒ 真正需要 28.25，比这个下界高 1.65 mm。
                    //     最紧的那个由 Solver.CoverCheck 在解完当场给（连处方一起）。
                    double wall6b = (double)_wall.Value;
                    double minDisc = GeometryScreen.MinDiscRadiusMm(
                        holeRadiusMm: wall6b + 25.0, thickMm: _base.WeldMinThicknessMm, wallMm: wall6b);
                    if (R < minDisc - 1e-9)
                    {
                        done += screenRounds; _prog.Value = Math.Min(_prog.Maximum, done);
                        Note($"跳过 盘Ø{2 * R:0}（判据 {Criteria.Explain("⑥")} 早筛）");
                        _out.AppendText($"{2 * R:0}\t—\t—\t—\t" +
                            $"跳过：管壁 {(double)_wall.Value:0.0} 时盘半径至少要 {minDisc:0.0}（判据 {Criteria.Explain("⑥")}）\r\n");
                        return;
                    }
                    var seed = PageToDesignSpec();
                    seed.DiscRadiusMm = R;
                    seed.TabHalfWidthMm = hw;
                    seed.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw))
                                       + seed.ClampLengthMm + FreeTabMin;
                    string tag = $"盘Ø{2 * R:0}／舌宽{2 * hw:0}";
                    int baseDone = done;
                    int seenRound = 0;
                    var prog2 = new Progress<string>(s => OnUi(() =>
                    {
                        // Solver 每轮吐「第 N 轮…」；数它推进度条
                        if (s.StartsWith("第", StringComparison.Ordinal)) seenRound++;
                        _prog.Value = Math.Min(_prog.Maximum, baseDone + seenRound);
                        Note($"{tag}　" + s.Split('\n')[0]);
                    }));
                    // ★ 粗筛走 **Solver**（求根）而不是 Sizer（搜索）。
                    //   ⚠ 粗筛不开第二遍（FineMm = 0）：它只负责**给方向**，
                    //     胜出的那一个才做细网格求根（见下面「精算」）。
                    // ★ 计时（2026-09-04）：搜形状实测 90 分钟跑不完，而**时间花在哪没人量过** ——
                    //   网格 6 点之后还有一段邻域爬山，两者都可能是大头。
                    //   先量再改：不量就动，等于又一次「没算成本就下手」。
                    var swPt = System.Diagnostics.Stopwatch.StartNew();
                    var sr = await Task.Run(() => Solver.Solve(seed, _base,
                                 new SolverOptions { MaxRounds = screenRounds,
                                                     ScreenCoarseMm = SearchScreenCoarseMm },
                                 prog2, ct), ct);
                    swPt.Stop();
                    done = baseDone + screenRounds;
                    _prog.Value = Math.Min(_prog.Maximum, done);
                    // ★ NaN 不等于「无解」：可能是判不了、交棒（厚度到顶 ⇒ 增宽，本表正是在增宽）、⑥ 盖不住 —— 停因才说得清
                    Note($"{tag} 已完成　{(double.IsNaN(sr.MassG) ? "未解出（看停因）" : sr.MassG.ToString("0") + " g")}");
                    rows.Add((sr.Design, sr.MassG, sr.Feasible, sr.Message));
                    // ★ 算完一个贴一个：中途取消也留得住已有结果
                    _out.AppendText(
                        $"{2 * R:0}\t{2 * hw:0}\t{sr.Design.TabLengthMm:0}\t" +
                        $"{swPt.Elapsed.TotalMinutes:0.0}	" +
                        (double.IsNaN(sr.MassG) ? "—" : sr.MassG.ToString("0")) +
                        $"\t{(sr.Feasible ? "✓ " : "")}{sr.Message}" +
                        // ★ 粗筛只跑 SearchScreenRounds（16）轮，比 CLI 的 40 更容易被截断；
                        //   截断了却不说，就会被读成「这个形状不行」（2026-08-25）。
                        (sr.HitBound ? $"（⚠ {sr.StopWhy}）" : "") + "\r\n");
            }



            // ── 第 1 轮：网格粗筛。它的作用是**给出发点与方向**，不是最终答案。
            // ★ **先把工程师现在这个形状算一遍**（2026-08-25）。
            //   网格是写死的 {25,30,35}，不从页面当前盘径出发 ⇒ 手上是 R60 的图时，
            //   搜索连「你现在这个形状值多少」都不告诉他，直接跳到答案区。
            //   把当前形状当**基准点**加进第 1 轮：
            //     · 工程师看得到「从我这里到最好的，差多少」；
            //     · 外推也有了一个真实的出发点，而不是凭网格猜的。
            //   ⚠ 它可能不可行（那正是他来搜形状的原因）—— 不可行就只是表上多一行，
            //     不会成为外推的出发点（外推只从**可行**的最好点走）。
            {
                double R0now = (double)_discD.Value * 0.5, hw0now = (double)_tabW.Value;
                if (R0now > 5 && hw0now > 1)
                {
                    _out.AppendText("（先算你现在这个形状，作基准）" + Environment.NewLine);
                    await EvalShape(R0now, Math.Min(hw0now, R0now));
                }
            }
            // ══════════════════════════════════════════════════════════════════
            // ★★★★★ **盘径不用搜，⑥ 有闭式反解**（2026-09-04，用户要求「改求根」）
            //
            //   ══ 为什么原来那个 3×2 网格贵
            //
            //   实测每点计时（deliverable/F_测搜形状耗时.txt）：
            //     不可行的点 1 分钟就退出，**可行的点 20–33 分钟** —— 单点差 30 倍。
            //   网格 6 点里 3 个可行 ⇒ 光网格就 ~80 分钟，预算全耗在这里。
            //   ⚠ 二分也救不了：二分同样要落在若干**可行点**上，每个仍是 20–30 分钟。
            //
            //   ══ 真正的杠杆：卡住小盘径的那条判据，本身是闭式的
            //
            //   实测卡住的是「⑥ 圆盘盖得住管孔＋焊脚」：
            //     盘半径 27.500 mm ＜ 需要 29.023 mm
            //   而 Solver.CoverCheck 自己写着：「这是 ⑥ 的**闭式反解**，不是搜出来的
            //   —— 不用试，就是这个数」。need = 管孔 + 焊脚，焊脚 = max(板厚, 壁厚)。
            //
            //   ⇒ 解一次拿到板厚 → ⑥ 当场给出**最紧的**盘径下界 → 在那里再解一次。
            //     板厚随盘径变，所以是个不动点迭代，实测一两步就收敛。
            //
            //   ⚠ 这不是「猜下界」：need 由 GeometryScreen.MinDiscRadiusMm(plates) 算，
            //     与判据 ⑥ **同一份实现**（2026-08-29 已经把手写的那份合并掉了）。
            //   ⚠ 收敛之后仍**照常评估**该点（走 EvalShape），判据与质量都是真解出来的，
            //     闭式只用来**选在哪里解**，不用来代替解。
            double fWide = wFrac.Max();          // 舌宽先取最宽（贴着盘径），窄的稍后在最优盘径上试
            double Rsafe = discs[^1];            // 网格最大的那个盘径：先在这里解一次拿板厚
            _out.AppendText("① 先在盘Ø" + (2 * Rsafe).ToString("0")
                          + " 解一次，拿到板厚 —— 判据「圆盘盖得住管孔＋焊脚」"
                          + "据此给出**最紧的盘径下界**（闭式，不用搜）" + Environment.NewLine);
            await EvalShape(Rsafe, Rsafe * fWide);

            double Rbest = Rsafe;
            for (int fix = 0; fix < 3; fix++)
            {
                var lastD = rows.Count > 0 ? rows[^1].d : null;
                if (lastD is null) break;
                double floorMm = lastD.DiscFloorMm(_base);
                var plates = new FlangePlate[lastD.TabThickMm.Length];
                for (int j2 = 0; j2 < plates.Length; j2++) plates[j2] = lastD.Plate(j2, floorMm);
                double need = GeometryScreen.MinDiscRadiusMm(plates);
                if (double.IsNaN(need) || need <= 0) break;
                double Rnext = Math.Max(need, minDiscAll);
                // 已经贴着下界（或反而更大）⇒ 不动点到了
                if (Rnext >= Rbest - 0.05) break;
                _out.AppendText($"② 判据下界给出 盘半径 ≥ {need:0.000} mm ⇒ 在盘Ø{2 * Rnext:0.0} 再解一次"
                              + Environment.NewLine);
                _prog.Maximum += screenRounds;
                await EvalShape(Rnext, Rnext * fWide);
                if (rows.Count == 0 || !rows[^1].ok)
                {
                    // ★★★★★ 闭式下界处**不可行** ⇒ 真正卡住的不是 ⑥，是别的判据。
                    //   实测（deliverable/F_求根后.txt）：⑥ 给出 R ≥ 27.253，
                    //   而盘Ø55 上「圆盘区最高温」**判不了（NaN）** —— 盘太小，
                    //   圆盘区与孔/焊缝分不开了。
                    //
                    //   ⇒ 现在两端都是**实测**出来的：下界不可行、Rbest 可行。
                    //     这才是二分该出场的时候（此前二分是没有依据的猜）。
                    //   ⚠ 不用爬山：爬山每步只挪 ±5 mm 且不认方向，实测它从 70 走到 60
                    //     中间还绕去 70/53（3034 g，更重）。二分 3 步就到。
                    double bLo = Rnext, bHi = Rbest;      // bLo 不可行、bHi 可行
                    for (int bi = 0; bi < 3 && bHi - bLo > 1.0; bi++)
                    {
                        double mid = 0.5 * (bLo + bHi);
                        _out.AppendText($"③ 二分：{2 * bLo:0.0} 不可行 / {2 * bHi:0.0} 可行 ⇒ 试盘Ø{2 * mid:0.0}"
                                      + Environment.NewLine);
                        _prog.Maximum += screenRounds;
                        await EvalShape(mid, mid * fWide);
                        if (rows.Count > 0 && rows[^1].ok) { bHi = mid; Rbest = mid; }
                        else bLo = mid;
                    }

                    // ★★★★★ **可行边界不是最轻点**（2026-09-05 实测推翻了我的前提）
                    //
                    //   我原以为「可行区里盘径越小越轻」，于是二分到边界就收工。
                    //   实测（deliverable/F_成对后.txt）四个可行点：
                    //       盘Ø70 → 2848 g   盘Ø62 → 2619 g
                    //       盘Ø58 → 2590 g   盘Ø56 → 2611 g   ← 更小反而**更重**
                    //   ⇒ 最轻点在**区间内部**（≈58），不在边界（56）上。
                    //   先前那个「随盘径递增」是从**三个网格点**归纳出来的 —— 样本太少。
                    //
                    //   ⇒ 二分的职责改成**定可行区间**；区间内再按**质量**找极小。
                    //   用黄金分割：单峰假设下 4 个点把区间缩到 ~15 %，
                    //   而每个点仍是真解（质量与判据都不是估的）。
                    //   ⚠ 不假设严格单峰：取的是**已算过的所有可行点里最轻的那个**，
                    //     黄金分割只决定「下一个点试哪里」。多峰时最多是没找到全局最优，
                    //     不会给出一个没验过的答案。
                    double gLo = bLo, gHi = Math.Min(bHi + 6.0, Rsafe);   // 往可行侧留一点余地
                    const double Phi = 0.6180339887;
                    for (int gi = 0; gi < 4 && gHi - gLo > 1.0; gi++)
                    {
                        double x1 = gHi - Phi * (gHi - gLo), x2 = gLo + Phi * (gHi - gLo);
                        double probe = (gi % 2 == 0) ? x1 : x2;
                        if (rows.Any(r2 => r2.d is not null
                                        && Math.Abs(r2.d.DiscRadiusMm - probe) < 0.5)) { gLo += 0.5; continue; }
                        _out.AppendText($"④ 找最轻：区间 盘Ø{2 * gLo:0.0}–{2 * gHi:0.0} ⇒ 试盘Ø{2 * probe:0.0}"
                                      + Environment.NewLine);
                        _prog.Maximum += screenRounds;
                        await EvalShape(probe, probe * fWide);
                        // 缩区间：往**当前最轻**的那一侧收
                        var okRows = rows.Where(r2 => r2.ok && r2.d is not null && !double.IsNaN(r2.mass)).ToList();
                        if (okRows.Count == 0) break;
                        double Rmin = okRows.OrderBy(r2 => r2.mass).First().d!.DiscRadiusMm;
                        if (probe < Rmin) gLo = probe; else gHi = probe;
                        Rbest = Rmin;
                    }
                    break;
                }
                Rbest = Rnext;
            }

            // ③ 在最优盘径上把其余舌宽比例各试一次（舌宽是另一维，不由 ⑥ 决定）
            foreach (double f in wFrac)
                if (Math.Abs(f - fWide) > 1e-9)
                {
                    _prog.Maximum += screenRounds;
                    await EvalShape(Rbest, Rbest * f);
                }

            // ── 之后每一轮：从当前最好点出发，试四个邻点（盘径 ±5、舌宽比例 ±0.125）。
            //    有更好的就搬过去继续；**一个都没更好就停** —— 这正是用户 2026-08-25 要的
            //    「有好的方向则继续，如果都是变坏即刻停止」。
            //    ⚠ 上限 6 轮：这条链本来就是几十分钟量级，不设上限会没完。
            //      停下时**已算过的形状全都留着**（rows），不会因为中止丢结果。
            int maxExtend = SearchMaxExtend;
            string NL2 = Environment.NewLine;
            var seen = new HashSet<string>();
            foreach (var r0 in rows)
                if (r0.d is not null)
                    seen.Add(ShapeSearchPlan.Key(r0.d.DiscRadiusMm, r0.d.TabHalfWidthMm));

            double BestMass() => rows.Where(x => x.ok && !double.IsNaN(x.mass))
                                     .Select(x => x.mass).DefaultIfEmpty(double.NaN).Min();

            // ★ 步长会**收缩**（算法普查 A⑥）：没有更好 ⇒ 步长减半再试，
            //   直到 MinDiscStepMm。于是「停」这句话变成「在该分辨率上没有更好」，
            //   而不是「在碰巧的 5 mm 上没有更好」。
            double step = ShapeSearchPlan.DiscStepMm;
            for (int ext = 1; ext <= maxExtend; ext++)
            {
                ct.ThrowIfCancellationRequested();
                var cur = rows.Where(x => x.ok && !double.IsNaN(x.mass))
                              .OrderBy(x => x.mass).FirstOrDefault();
                if (cur.d is null)
                {
                    _out.AppendText("　（网格里没有可行解 ⇒ 没有出发点，不外推）" + NL2);
                    break;
                }
                double before = cur.mass, R0 = cur.d.DiscRadiusMm, hw0 = cur.d.TabHalfWidthMm;
                // ★ 「试哪几个 / 算不算变好」的规则**只有一份**：Core/ShapeSearchPlan
                //   （2026-08-25 抽出并配了 10 条微秒级门）。这里只负责跑，不再自己判。
                var todo = ShapeSearchPlan.Worth(ShapeSearchPlan.Neighbours(R0, hw0, step), seen);
                if (todo.Count == 0)
                {
                    // 邻点都算过 ⇒ 不是没方向，是**这个步长上**没新点可试 ⇒ 收缩再来
                    double nx0 = ShapeSearchPlan.Refine(step);
                    if (ShapeSearchPlan.StepExhausted(nx0))
                    {
                        _out.AppendText($"　第 {ext + 1} 轮：±{step:0.###} mm 的邻点都试过了，且步长已收到"
                                      + $"分辨率下界 {ShapeSearchPlan.MinDiscStepMm:0.###} mm ⇒ 停。" + NL2);
                        break;
                    }
                    step = nx0;
                    _out.AppendText($"　第 {ext + 1} 轮：邻点都试过了 ⇒ **步长减半到 {step:0.###} mm**，继续。" + NL2);
                    continue;
                }

                _prog.Maximum += todo.Count * screenRounds;
                _out.AppendText(NL2 + $"第 {ext + 1} 轮 · 从 盘Ø{2 * R0:0}／舌宽{2 * hw0:0}"
                              + $"（{before:0} g）出发，试 {todo.Count} 个邻点" + NL2);
                foreach (var (R2, hw2) in todo) await EvalShape(R2, hw2);

                double after = BestMass();
                bool better = ShapeSearchPlan.Improved(before, after);   // 唯一一份口径
                _out.AppendText($"　⇒ 第 {ext + 1} 轮：{before:0} → {after:0} g　"
                              + (better ? "**↓ 变好，继续**" : "**↑ 没有更好的方向 ⇒ 停**")
                              + NL2);
                Note($"第 {ext + 1} 轮 {(better ? "变好" : "无改善")}　{before:0} → {after:0} g"
                     + $"　步长 {step:0.###} mm");
                if (!better)
                {
                    // ★ 「没有更好」只说明**在这个步长上**没有更好 —— 减半再问一次。
                    //   收到分辨率下界才谈得上「局部最优」，而那个下界是声明出来的。
                    double nx = ShapeSearchPlan.Refine(step);
                    if (ShapeSearchPlan.StepExhausted(nx))
                    {
                        _out.AppendText($"　⇒ 步长已收到 {step:0.###} mm（下界 {ShapeSearchPlan.MinDiscStepMm:0.###} mm）仍无改善"
                                      + $" ⇒ **在 ±{step:0.###} mm 分辨率上是局部最优**，停。" + NL2);
                        break;
                    }
                    step = nx;
                    _out.AppendText($"　⇒ 这个步长上没有更好 ⇒ **步长减半到 {step:0.###} mm** 再问一次" + NL2);
                }
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
            // ★★ 精算走 **Solver 两遍**：第一遍导航网格定位，
            //   第二遍在**判据所在的那张网格**上重新求根（A⑬）。
            //   否则给出的是「粗网格上的刚好」——实测 ③ 在两张网格上差 **2.03 倍**。
            //   网格该多细与复核同一个来源（MeshVerify.RequiredMeshFor）。
            var (finFine, finFineR) = MeshVerify.RequiredMeshFor(win.d);
            int finRound = 0;
            var fin = await Task.Run(() => Solver.Solve(win.d, _base,
                          new SolverOptions { MaxRounds = finalRounds,
                                              FineMm = finFine, FineRadiusMm = finFineR },
                          new Progress<string>(s => OnUi(() =>
                          {
                              if (s.StartsWith("第", StringComparison.Ordinal)) finRound++;
                              _prog.Value = Math.Min(_prog.Maximum, done + finRound);
                              Note("精算　" + s.Split('\n')[0]);
                          })), ct), ct);
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
            // ★ 统一入口（见 AdoptSolvedDesign）：此前这里只写 _last ⇒
            //   舌保温/环倍率丢掉、状态没发布，与「自动定厚」是同一个病的第三例。
            AdoptSolvedDesign(fin.Design, fin.Best);

            // ★ 第二遍做没做，**必须当场说** —— 本项目的错误形态是
            //   「看着正常的错数」：只在导航网格上成立的解，
            //   数字长得和可交付的解一模一样。
            _out.AppendText(Environment.NewLine + (fin.FineRefined
                ? $"   ✓ 已做**第二遍细网格求根**（{fin.FineMmUsed:0.000} mm）"
                  + "—— 根是在**判据所在的那张网格**上求的。"
                : "   ⚠ **没做第二遍** ⇒ 这个解只在导航网格上成立，**不可交付**。")
                + Environment.NewLine);

            // 形状体检：搜出来的赢家也要说清楚它好在哪、代价在哪
            _out.AppendText("\r\n" + ShapeReview.Build(fin.Design, fin.Best,
                                                       DesignSpec.Current, fin.Message, _base));
            _out.AppendText("\r\n★ **最轻的全过形状**（已写回上面的盘径/舌宽/舌长/板厚）\r\n" +
                $"   盘Ø{2 * fin.Design.DiscRadiusMm:0}／舌 {fin.Design.TabLengthMm:0}×{2 * fin.Design.TabHalfWidthMm:0}" +
                $"／自由段 {fin.Design.FreeTabMm:0.0} mm\r\n" +
                $"   板厚 {DesignSpec.Fmt(fin.Design.TabThickMm, "0.00")}" +
                $"　舌保温 {DesignSpec.Fmt(fin.Design.TabInsulMm, "0.0")}" +
                $"　环倍率 {DesignSpec.Fmt(fin.Design.RingMul, "0.00")}\r\n" +
                $"   合计 {fin.MassG:0} g　{fin.Message}\r\n\r\n" +
                "     ★ 舌保温与环倍率本页没有控件，但**已由本页承载**（2026-08-25 起）：" + Environment.NewLine + "" +
                "       它们跟着后续求解与出图走，不必再手抄进 Core/DesignSpec。" + Environment.NewLine + "" +
                "   ⚠ 筛选只跑了 " + screenRounds + " 轮，**是粗筛**：名次靠前几名接近时，" +
                "把它们各自再跑一次足轮数才算数。\r\n");
            _status.Text = "完成";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消";
            _pipeAborted = true;   // 取消一步 = 停整条流水线
            _out.AppendText("\r\n（已取消。上面已经算完的形状结果仍然有效。）\r\n");
        }
        catch (Exception ex)
        {
            _status.Text = "失败";
            _pipeAborted = true;   // 出错同样停整条
            _out.AppendText("\r\n✗ " + ex.Message + "\r\n");
        }
        finally
        {
            _prog.Visible = false; _prog.Style = ProgressBarStyle.Marquee;
            _btnShape.Text = Flow.Cmd("shape.search").Text;
            // R24：算过的形状（取消时已算完的那些也算数）都交给下拉，工程师自行选
            _shapeRows = rows.ToList();
            RefreshShapePick();
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
                           Flow.Cmd(autoSize ? "core.autoThick" : "core.runLine").Text);

        // ⚠⚠ BuildCase() **必须在 try 里面**。它会抛（如「.3dm 模式但文件没填」）——
        //   放在外面时异常越过 finally ⇒ _cts 不清、按钮不恢复、进度条一直转，
        //   而且此后 TryAutoRun 每次都撞上「_cts 不为 null」而无限重排定时器
        //   ⇒ **自动重算从此永久死掉，且一声不吭**。
        //   实测复现：点一下「Rhino .3dm 文件」单选钮（还没填文件）就中招。
        LineCase lc;
        // ★ 带上百分比与已跑时长 —— 否则状态面板只会转圈（见 PctOf 的说明）。
        var clockR = System.Diagnostics.Stopwatch.StartNew();
        var prog = new Progress<string>(s => OnUi(() =>
        {
            _status.Text = s;
            // ★ 流水线里要说清「第几步／在做什么」—— 否则跑一小时只看到一行滚动的轮数
            Shared?.SetRunningNote(
                (_pipeStep.Length > 0 ? _pipeStep + "　" : "")
                + $"已跑 {clockR.Elapsed.TotalMinutes:0.0} 分　{s}", PctOf(s, 40));
        }));

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
                //     · D8 走 `Solver.Solve(PageToDesignSpec(), …)`（求根，2026-08-29 从 Sizer 换过来），
                //       上面刚从 .3dm 造好的 `lc` **一眼都没看** ⇒ 图纸被静默丢弃；
                //     · 盘径/舌长/舌半宽在 .3dm 模式下是**禁用**的，里面是上次的残值 ——
                //       D8 拿这组残值当几何去优化；
                //     · `_tPlate` 在 .3dm 模式下含义是**厚度标度 k**，D8 当**毫米**读、
                //       算完又把毫米数写回去 ⇒ 标度字段被覆盖坏。
                //   而输出照旧是「【D8 定尺寸】板厚 … 合计 … g」，看起来完全正常。
                // ★★★★★ 2026-09-03：只有**还没分析**才拒。等厚板（1 级）放行。
                //   理由见 SyncAnalysisPending 里那段：SolveByLevel 是两层的，
                //   1 级只让**内层**（各级比例）失效，外层（整片厚度，靶抽热误差）照常能调。
                //   原来一并拒掉，等于把能用的那一半也砍了 —— F 端到端就撞死在这。
                if (!_srcAnalytic.Checked && _levels is not { Length: > 0 })
                {
                    // ⚠ 用 Environment.NewLine 拼，不写反斜杠转义 ——
                    //   本仓的钩子会把转义序列改成真字符，字面量当场断掉。
                    string nl = Environment.NewLine;
                    Show(_last, autoNote:
                        "【自动定厚：做不了】本页是 **Rhino .3dm 模式**，" + nl
                      + "   图纸**还没反推**成几何变数 —— 请先点「**分析几何变数**」。" + nl
                      + "   ⚠ 不能替你用解析路的定尺寸：那条优化的是**解析形状**（圆盘＋舌片），"
                      + nl
                      + "      它不读你的 .3dm，盘径/舌长在本模式下又是禁用的残值 ——" + nl
                      + "      那样算出来的是**另一个零件**的厚度，数字却看不出异样。");
                    return;
                }

                if (!_srcAnalytic.Checked)
                {
                    // 逐级定厚（.3dm 任意形状）：D8 只在解析几何上工作，管不了任意台阶，
                    // 所以这条路仍用 FlangeAutoSizer。
                    // ⚠ 它**只有板厚一个旋钮**、靶是 ③，管不到 管孔净流入/圆盘区最高温（它自己的注释写着）。
                    //   ⇒ 用完必须看判据表，尤其 管孔净流入 净流入是不是仍为正。
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
                    // ★★★ 结构性停机 = **不可行的证明**，与解析路的 HitBound 同一个语义。
                    //   不接这一位，指路会继续指「自动定厚」，而再点一次是同一句话 ——
                    //   2026-09-03 实测（Pt_Heater1.3dm 熔点闸停机）判据表与总铂**逐字未变**。
                    //   这是同一个死循环的**第三个入口**（前两个：等厚板被拒、旋钮顶到上界）。
                    _sizerInfeasible = r.Terminal;
                    // ⚠ 这是**程序**在把刚解出来的厚度写回控件。不闭掉自动重算的话，
                    //   「自动定厚」一结束就会立刻再排一次整线重算 —— 算的还是它自己刚给的答案。
                    _suppressAuto = true;
                    for (int i = 0; i < _tPlate.Length && i < r.ThicknessMm.Length; i++)
                        _tPlate[i].Value = (decimal)Math.Clamp(r.ThicknessMm[i], 0.1, 8.0);
                    _suppressAuto = false;
                    _last = r.Line;
                    // ★★★★★ **必须发布到 FlowState**（2026-08-25 `--follow` 走查抓到）。
                    //
                    // 此前这两条「自动定厚」分支都只写 `_last` 就走了，**一次 PushFlow 都没有** ⇒
                    // FlowState 的 Last / CurrentSnap / SolvedSnap **三者全冻在上一次整线解**。
                    // 后果不是「显示慢一拍」，是三条：
                    //   ① 链路提示读 flow.Last（旧的、不过的）⇒ 永远说「判据没全过，用自动定厚」
                    //      ⇒ **跟着提示走的人在自动定厚上死循环，每次几十分钟，永远走不到交付**；
                    //   ② ④→⑤ 那道 RequireAllOk 的门读的也是旧解 ⇒ 这条路走不到出图；
                    //   ③ **最危险的一条**：上一次解若是**过的**，而自动定厚把它调坏了，
                    //      门会**继续开着** —— 假绿灯。
                    // 本分支（.3dm 逐级定厚）把**所有**厚度都写回了控件 ⇒ 页面状态完整代表这个解
                    // ⇒ 可以标成「已解且新鲜」。
                    if (r.Line is { Ok: true, Converged: true })
                    { _solvedRes = r.Line; _solvedSnap = CurrentSnap(); }
                    PushFlow();
                    // ★ 等厚板（1 级）要**说清只有外层在动**（2026-09-03 放行 1 级之后）。
                    //   不说的话，工程师会以为逐级比例也在被优化，而 1 级时内层恒等于没动。
                    bool oneLevel = lvl is { Length: > 0 } && lvl[0].Length == 1;
                    Show(r.Line, autoNote: r.Message + (r.Converged ? "" : "　⚠ 未收敛，下面的数不可引用") +
                        (oneLevel
                         ? Environment.NewLine
                           + "   ★ 这张图是**等厚板（只有一级）** ⇒ 本次只有**整片厚度**在调"
                           + "（各级比例无从调起，那需要图纸上有台阶）。"
                           + Environment.NewLine
                           + "   　想让它也能逐级调，回 Rhino 给圆盘分级，再重新「分析几何变数」。"
                         : "") +
                        Environment.NewLine + "   ⚠ 本器**只调板厚**，管不到 管孔净流入 净流入与 圆盘区最高温 圆盘峰 —— 请自行看判据表。" + floorNote);
                }
                else
                {
                    // ★★★ 解析几何走 **D8**（Core/Sizer）。旧的 FlangeAutoSizer 只有板厚一个旋钮、
                    //   靶是 ③，而 ③ 与 管孔净流入 是同一个抽热的两侧 ⇒ 它把 ③ 压下去的同时
                    //   把 管孔净流入 往负里推（热倒灌进管 = 烧断机理），且它自己管不到 管孔净流入。
                    //   D8 用舌保温守抽热窗口、环倍率守 圆盘区最高温、板厚只做接力与省铂。
                    var seedD8 = PageToDesignSpec();
                    // ★ 改走 **Solver**（求根，与初值无关）。Sizer 是搜索，必须有起点。
                    //   ⚠ 这里**不开第二遍**（FineMm = 0）：按钮要等得起。
                    //     结果只在导航网格上成立，下面会当场说出来。
                    var srD8 = await Task.Run(() => Solver.Solve(seedD8, _base,
                                   new SolverOptions { MaxRounds = 40 }, prog, ct), ct);
                    // ★★★ 顶到上界 = **不可行的证明** ⇒ 记下来，指路才不会把人推回同一个按钮。
                    //   ⚠ 只有 HitBound 才算证明；Feasible=false 但 HitBound=false 是「没搜到」，
                    //     那种再点一次是有意义的，不能一并堵掉。
                    //   ⚠⚠ **必须写在这里（求解一返回就写）**，不能写在下面 ——
                    //     本支唯一那次 PushFlow 藏在 AdoptSolvedDesign 里，位写晚了就赶不上，
                    //     后面再没有第二次发布。我 2026-09-03 连栽两次：先放错函数，再放错位置。
                    _sizerInfeasible = srD8.HitBound && !srD8.Feasible;
                    _lastTrace = srD8.Trace.ToArray();     // ★ 比价等推理过程带回界面
                    _suppressAuto = true;
                    for (int i = 0; i < _tPlate.Length && i < srD8.Design.TabThickMm.Length; i++)
                        _tPlate[i].Value = (decimal)Math.Clamp(srD8.Design.TabThickMm[i], 0.1, 8.0);
                    // 舌长可能被装配下界顶高（D8 不动它，但页面上要跟着显示）
                    _suppressAuto = false;
                    // ★★★★★ **把 D8 的三个旋钮都带回本页**（2026-08-25）。
                    //   只写板厚是不够的：舌保温与环倍率也是这个解的一部分，
                    //   丢掉它们再重解，管孔净流入 会掉负 ⇒ 提示指回定尺寸 ⇒ **两步死循环**。
                    //   实测：定尺寸 3569 g 全过 → 重解 管孔净流入 = −9.32 不过 → 再定尺寸 3565 g 全过 → …
                    //   带回来之后本页承载的就是 D8 那个**完整设计**，而 ③ 页与 D8 用的是
                    //   **同一个几何构造器**（UiWiring §16 逐字段钉着）⇒ 重解会复现这张表，路径收得了尾。
                    if (!srD8.FineRefined)
                        // ★★★ 2026-08-30 更正指路：原来写「要可交付请跑搜形状（它的精算会做第二遍）」——
                        //   **那是错的**。第二遍求根 ≠ 网格无关复核：
                        //     第二遍求根   在一张**固定**的细网格上重新求根
                        //     网格无关复核 一档档加密，直到判据**不再变**
                        //   两者答的不是同一个问题。而正牌按钮当天已经补上了。
                        _out.AppendText(Environment.NewLine
                            + "   ⚠ 本次**只在导航网格上求根**，这些数还没验过准不准。"
                            + "同一个设计实测：粗网格算出 法兰增量温降 7.7 K（限值 10，看着很宽），"
                            + "加密到位是 **9.5 K** —— 差 1.8 K，足以把「过」变成「不过」。"
                            + Environment.NewLine
                            + "   ⇒ **下一步点「◆ 加密复算（算到数不再变）」**（本页工具条，10～40 分钟，可取消）。"
                            + "没过这一关，「交付」的门不会开。" + Environment.NewLine);
                    AdoptSolvedDesign(srD8.Design, srD8.Best);   // 统一入口
                    // ★★★★★ 同上：必须发布，否则提示与门禁读的是冻住的旧解（见上一分支的长注释）。
                    //
                    // ⚠ 但**不能**像上一分支那样标成「新鲜」：D8 的解含**舌保温**与**环倍率**，
                    //   而本页**没有这两个控件** ⇒ 页面状态代表不了这个解。
                    //   标成新鲜就等于宣称「照本页参数出图能得到这张表」——
                    //   而照本页参数出的是**另一个设计**（少了守 管孔净流入/③ 的主力旋钮）。**那是假绿灯。**
                    //   ⇒ 只发布 Last（让判据表与提示说真话），不动 _solvedSnap；
                    //     板厚写回已经改了 CurrentSnap ⇒ Fresh 自然为 false，
                    //     提示会说「参数在上次求解之后又动过了 —— 回 ③ 重解」，这是实话。
                    //   —— 上面那段顾虑在**旋钮带回本页之后不再成立**：本页现在承载完整设计，
                    //     所以可以标成「已解且新鲜」，提示会直接指向出图。
                    //   （带回 + 发布 + 标新鲜四件事已经收进 AdoptSolvedDesign，上面那一行。）
                    _pendingReview = ShapeReview.Build(srD8.Design, srD8.Best,
                                                       DesignSpec.Current, srD8.Message, _base);
                    Show(srD8.Best, autoNote:
                        "【D8 定尺寸】" + srD8.Message + "\r\n" +
                        $"   板厚 {DesignSpec.Fmt(srD8.Design.TabThickMm, "0.00")}" +
                        $"　舌保温 {DesignSpec.Fmt(srD8.Design.TabInsulMm, "0.0")}" +
                        $"　环倍率 {DesignSpec.Fmt(srD8.Design.RingMul, "0.00")}" +
                        $"　合计 {srD8.MassG:0} g\r\n" +
                        "   ★ 三个旋钮**都已带回本页工作设计**（2026-08-25 起）：\r\n" +
                        "     板厚写进控件；舌保温与环倍率本页无控件，但已由本页承载并参与后续求解\r\n" +
                        "     ⇒「回「整线核算」重解」会**复现这张表**，不会把它们丢回设计记录值。\r\n" +
                        "     （在此之前只带板厚 ⇒ 重解必然退回失败 ⇒ 提示与定尺寸两步死循环。）\r\n" +
                        $"   ⚠ 本次解的是**设计记录那套完整几何**（含渐变环/角焊缝/等宽舌片/舌根圆角），\r\n" +
                        $"     「整线核算」页用的是**同一个几何构造器**（UiWiring §16 逐字段钉着），不是另一片简化法兰；压接段取设计记录值 {seedD8.ClampLengthMm:0} mm。");
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
                            "\r\n── ⚠⚠ 本次解的**不是**设计记录那套几何（1b 之后不应出现）\r\n" +
                            "   缺了：\r\n         · " + miss +
                            "\r\n   ⇒ 说明有人绕过了 DesignSpec.Plate 这个唯一构造器，请查 BuildCase。\r\n";
                    else
                        _out.Text +=
                            "\r\n── 本次解的是**设计记录那套完整几何**（与「复现设计记录」同一个构造器）\r\n" +
                            AnalyticUsedWhat(PageToDesignSpec()) + "\r\n";
                }
            }
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; _pipeAborted = true; }
        catch (Exception ex)
        {
            _status.Text = "失败";
            _pipeAborted = true;   // 出错同样停整条
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
            // ★ 跑完把名字**从 Flow 读回来**，不写死 —— 写死的那一份会在改名时
            //   把按钮悄悄改回旧名（跑一次之后界面才对不上，比构造时更难查）。
            _btnRun.Text = Flow.Cmd("core.runLine").Text;
            _btnAuto.Text = Flow.Cmd("core.autoThick").Text;
            Shared?.SetRunning(null);          // 清在 finally：异常/取消也必须解除互斥
        }
    }

    /// <summary>
    /// 待插入的形状体检报告（<see cref="ShapeReview"/>）。由定尺寸器那条路设置，
    /// <see cref="Show"/> 取用后清空 —— 放在**判定之后、明细之前**，
    /// 那是用户读完「过没过」之后最想知道「为什么、代价是什么」的位置。
    /// </summary>
    /// <summary>
    /// 判据表 / 说明 的上下分栏。存成字段是为了**按有没有结果调比例**：
    /// 还没解过时判据表是空的，却占着 46 % 的高度 —— 开箱第一眼看到的是一张空格子，
    /// 而该看的「怎么开始」被挤在下面。⇒ 没结果就把地方让给说明（2026-09-03 抓图）。
    /// </summary>

    private string _pendingReview = "";

    /// <summary>
    /// 定尺寸器上一次跑完是不是**把法兰侧旋钮顶到上界了仍不过**。
    /// <c>SolverResult.HitBound</c> 的语义是「**再点一次会得到同一句话**」，不是「没搜到」。
    /// ⚠ 2026-09-08 收窄：它**不等于**「这组输入不可行」—— 求解器只有法兰侧九根旋钮，
    ///   盘径与舌半宽归「◇ 搜形状」。**一个出口只能对它自己有的旋钮下结论。**
    ///
    /// ⚠ 生命周期只有一条规矩：**只有「自动定厚」跑完才写，别处一律清**。
    ///   参数一动、或按新参数重解一次，上一次那句「不可行」就不再是关于这组输入的结论了。
    /// </summary>
    private bool _sizerInfeasible;

    /// <summary>
    /// 上一次求解器跑完留下的推理过程（<c>SolverResult.Trace</c>）。
    /// 在「求解器诊断」勾上时印出来 —— 里面装着**每轮的比价**（每克铂买到多少裕度）。
    /// ⚠ 参数一动就清：它是关于**上一组参数**的推理，留着会张冠李戴。
    /// </summary>
    private string[] _lastTrace = System.Array.Empty<string>();

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
    /// <summary>
    /// ★★★ **网格无关复核** —— 把网格一档档加密，直到判据不再变（2026-08-30 补）。
    ///
    /// ══ 为什么它必须在界面上
    ///
    /// 命令行早有 `--solve --verifymesh`，而 <c>MeshVerify.Run</c> 在 UI 里的调用次数是 **0**。
    /// ⇒ 对话里跑得漂亮、工程师点按钮却碰不到 —— 而 ⑤ 交付的门也不要求它，
    ///   于是「收敛✓全过✓新鲜✓ → 出图」这条路上，**没有任何一处问过这些数准不准**。
    ///
    /// 实测那个差有多大（0.6 档，同一个设计）：
    /// <code>
    ///   导航网格 2 mm    法兰增量温降 8.6 K
    ///   加密到 0.125 mm  法兰增量温降 **9.5 K**      限值 10
    /// </code>
    /// 而中间那一档更坏：0.5 mm 上是 7.7 K，看着余量很宽 —— **那是假收敛**。
    ///
    /// ⚠ 它跑 10–40 分钟。这不是缺陷，是这件事本身的代价：判据要网格无关才算数。
    ///   随时可取消，已经跑完的档照样印出来。
    /// </summary>
    private async Task VerifyMeshAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        if (_last is not { Ok: true } || !Equals(_solvedSnap, CurrentSnap()))
        {
            _out.AppendText(Environment.NewLine + "⚠ 先在本页点「核算整线」解出一个**当前参数的**解 —— "
                + "复核验的是「这个解在更细的网格上还成不成立」，没有解就无从验起。" + Environment.NewLine);
            return;
        }

        var d = PageToDesignSpec();
        var snapAtStart = CurrentSnap();
        _cts = new CancellationTokenSource();
        _btnVerify.Text = "取消";
        _btnRun.Enabled = _btnAuto.Enabled = _btnRepro.Enabled = false;
        Shared?.SetRunning(ChainId.C整线耦合, "加密复算");
        _out.AppendText(Environment.NewLine + "◆ **加密复算**开始 —— 把网格一档档加密，直到这个数不再变为止。" + Environment.NewLine
            + "　　10～40 分钟。随时可点「取消」，已跑完的档照样留下。" + Environment.NewLine);

        var prog = new Progress<string>(m => OnUi(() => _out.AppendText("　" + m + Environment.NewLine)));
        try
        {
            var res = await Task.Run(() => MeshVerify.Run(d, _base, progress: prog, cancel: _cts.Token),
                                     _cts.Token);
            _meshVerify = res;
            _verifiedSnap = res.Converged ? snapAtStart : null;

            // ★★★★★ **把复核解出来的那组判据接过来**（2026-09-02，`--follow 0.8` 走查抓到）。
            //
            // ══ 实物
            //
            // MeshVerify.Result.Line 这个字段自己的说明写着：
            //     「**网格无关的**那一次解 —— 判据以它为准，不是以导航网格那次为准」
            // 而本方法**从来没碰过它**：只读了 Converged / Verdict / MidBandConfirm 三样。
            // ⇒ 复核跑完、门开了、可以出图了，而判据表里躺着的还是**导航网格（2 mm）**那组数。
            //
            // 实测这一档的差（0.8）：
            //     法兰增量温降   导航网格 4.72 K（余量 53 %）  →  复核 7.95 K（余量 21 %）
            //     管孔净流入     1.12 W                        →  2.63 W
            // 工程师点完复核、看着 4.72 出图 —— 而**复核本身刚刚证明那个数不可信**。
            //
            // ══ 怎么发现的
            //
            // 走查器有一条「这一步起作用了吗」的指纹（判据实测值 + 总铂）。
            // 它报了 ✗：「core.verifyMesh 跑完，判据表与总铂**逐字未变**」。
            // 我原以为那是走查器的误报（复核本来就不改解），一查才发现**反了** ——
            // 它抓到的是真的：APP 算出了权威的那组数，然后**把它扔了**。
            //
            // 这正是本项目最怕那一族的极端形态：不是数错了，是**算对了却没送到人手上**。
            //
            // ⚠ 只在**收敛**时接管：没收敛就是没验过，那组数不该顶替任何东西
            //   （而门也照样关着，见 GateSpec.RequireMeshVerified）。
            if (res.Converged && res.Line is { Ok: true })
            {
                _last = res.Line;
                _out.AppendText(Environment.NewLine
                    + $"◆ 判据表已换成**加密复算后**（{res.FineMm:0.000} mm）的值 —— "
                    + "此前显示的是导航网格（2 mm）上的数。" + Environment.NewLine);
                Show(_last);
            }
            _out.AppendText(Environment.NewLine + res.Verdict + Environment.NewLine);
            if (res.MidBandConfirm is { Length: > 0 }) _out.AppendText("　" + res.MidBandConfirm + Environment.NewLine);
            if (!res.Converged)
                _out.AppendText("⚠ **没验过** —— 判据还在随网格变，这个设计现在不能出图。" + Environment.NewLine);
        }
        catch (OperationCanceledException)
        { _pipeAborted = true;   // 取消一步 = 停整条流水线
          _out.AppendText(Environment.NewLine + "◆ 加密复算已取消 —— **没验过就是没验过**。存档或出图时会把这件事列给你看。" + Environment.NewLine); }
        catch (Exception ex)
        { _out.AppendText(Environment.NewLine + "◆ 复核出错：" + ex.Message + Environment.NewLine); }
        finally
        {
            _cts = null;
            _btnVerify.Text = "◆ 加密复算（算到数不再变）";
            _btnRun.Enabled = _btnAuto.Enabled = _btnRepro.Enabled = true;
            Shared?.SetRunning(null);
            PushFlow();
        }
    }

    /// <summary>
    /// ★★★★★ **按当前段数重建逐片输入**（2026-09-02，用户点出的 bug）。
    ///
    /// 片数 = 段数 + 1（<c>LineSolver.FlangeCount</c>）。首片是**入口**、末片是**出口**，
    /// 中间是共用片，名字跟着段表走（HC1|HC2 这种）。
    ///
    /// ⚠ 重建会**新造控件**，所以每次都要重挂 <c>Watch</c>（自动重算的监听）——
    ///   忘了就变成「改了逐片值而不会重算」，是本仓最怕的安静失败。
    /// ⚠ 旧值按下标搬过来：加一段时新出现的那片沿用**上一片共用片**的值，
    ///   与 <see cref="DesignSpec.Fit"/> 的补法一致（别让两处各补各的）。
    /// </summary>
    /// <returns>true = 真的重建了（片数变了）；false = 片数没变，只换了标签。</returns>
    /// <summary>
    /// ★★★★★ **.3dm 逐片文件行按段数重建**（用户 2026-09-03：
    /// 「不论是 UI 或是 3DM 输入，需几段加热都由 UI 输入框输入」）。
    ///
    /// 原来 <c>_file3dm</c> 是定长 4 的字段、行在构造函数里建一次 ⇒ 图纸路**永远是 3 段**。
    /// 分 4 段时逐片输入有 5 片、文件框只有 4 个 ⇒ 第 5 片没有图纸，
    /// 而判据表照样出数 —— **算的是另一个零件，数字却看不出异样**。
    ///
    /// ⚠ 已经选好的路径要留住：按**片名**留（不是按下标）。片是在倒数第二个位置增删的
    ///   （见 DesignSpec.FitArr：首=入口、末=出口），按下标留会把出口的图纸挪给共用片。
    /// </summary>
    private void RebuildFile3dmRows()
    {
        var names = PlateNames();
        // 旧值按**片名**记下来
        var keep = new Dictionary<string, string>();
        for (int i = 0; i < _file3dm.Length && i < _row3dmName.Length; i++)
            if (!string.IsNullOrWhiteSpace(_file3dm[i].Text)) keep[_row3dmName[i]] = _file3dm[i].Text;

        _file3dmBox.SuspendLayout();
        foreach (Control c in _file3dmBox.Controls.Cast<Control>().ToArray()) c.Dispose();
        _file3dmBox.Controls.Clear();
        _file3dmBox.RowStyles.Clear();
        _file3dmBox.ColumnStyles.Clear();
        _file3dmBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.S(188)));
        _file3dmBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _file3dm = new TextBox[names.Length];
        _row3dm = new Control[names.Length];
        _row3dmName = names;

        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            var tb = new TextBox { ReadOnly = true, Dock = DockStyle.Fill,
                                   Margin = new Padding(0, 2, 2, 2) };
            if (keep.TryGetValue(names[i], out var old)) tb.Text = old;
            new ToolTip().SetToolTip(tb, "点右边「…」选 .3dm 文件");
            _file3dm[idx] = tb;

            // ⚠ 这一行**必须按列宽自适应**，不能用「固定宽文本框 + 固定宽按钮」硬拼
            //   （2026-08-21 用户报「3DM 输入入口不见了」，实测就是这么丢的）：
            //   旧写法在 K=1.6 的屏上按钮整个被父容器裁掉，而选文件只有这一个入口
            //   ⇒ .3dm 模式**整条链无法使用**，文本框只读、看起来一切正常。
            //   改成两列表格：文本框 Percent 100 + Dock Fill，按钮 AutoSize（永远画得下）。
            var pnl = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, AutoSize = true,
                Dock = DockStyle.Fill, Margin = new Padding(0),
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var b = new Button
            {
                Text = "…", AutoSize = true, Margin = new Padding(0, 2, 0, 2),
                MinimumSize = new Size(UiScale.S(28), UiScale.S(22)),
            };
            b.Click += (_, _) => PickFile(idx);
            pnl.Controls.Add(tb, 0, 0);
            pnl.Controls.Add(b, 1, 0);
            _row3dm[idx] = pnl;

            var lab = new Label { Text = names[i] + " .3dm", AutoSize = true,
                                  Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) };
            _file3dmBox.Controls.Add(lab);
            _file3dmBox.Controls.Add(pnl);
        }
        _file3dmBox.ResumeLayout();
    }

    /// <summary>上一次建行时用的片名 —— 重建时按名字（不是下标）留住已选路径。</summary>
    private string[] _row3dmName = System.Array.Empty<string>();

    private bool RebuildPlateRows()
    {
        var names = PlateNames();
        int n = names.Length;
        if (_tPlate.Length == n && _plateBox.Controls.Count > 0) { RenamePlateRows(names); return false; }

        double[] Keep(NumericUpDown[] old, double dflt)
        {
            var v = old.Select(x => (double)x.Value).ToArray();
            if (v.Length == 0) return Enumerable.Repeat(dflt, n).ToArray();
            var list = new List<double>(v);
            while (list.Count < n) list.Insert(list.Count - 1, list[Math.Max(0, list.Count - 2)]);
            while (list.Count > n && list.Count > 2) list.RemoveAt(list.Count - 2);
            return list.ToArray();
        }
        // ⚠ 开箱默认必须是原来那四个值 —— 2026-09-02 我第一版用单一兜底 0.8 填满，
        //   抓图看到四片全成了 0.800：**开箱默认的设计被悄悄换掉了**。
        double[] vT = _tPlate.Length > 0 ? Keep(_tPlate, 0.8)
                    : FitDefault(new[] { 0.516, 0.855, 0.776, 0.426 }, n);
        var vI = Keep(_tabIns, 0.4); var vR = Keep(_ringMul, 1.0);
        var v1 = Keep(_ringR1, 1.0); var v2 = Keep(_ringR2, 6.0); var vT2 = Keep(_ringT2, 1.0);
        var vS = Keep(_slotDeg, 0.0);      // 圆盘背侧减重槽张角，0 = 不开槽
        var vH = Keep(_holeR, 0.0);        // 舌板开孔孔径，0 = 无孔
        var vA = Keep(_holeAsp, 1.0);      // 孔的顺流拉长比，1 = 圆

        // ★★★★★ **重建期间必须关掉自动重算**（2026-09-02 走查超时抓到）。
        //
        //   Watch 给每个容器挂了 `ControlAdded += Watch(e.Control)`，而 Watch 给
        //   NumericUpDown 挂 `ValueChanged += ParamChanged()` ⇒ 下面每给一个新控件
        //   赋一次 .Value，就排一次**分钟级的整线重算**；六组 × n 片就是几十次。
        //   实测后果：走查在「核算整线」上**跑满 15 分钟预算超时**，
        //   而 `_last` 一直是 null —— 看起来像「解不出来」，其实是被自己刷爆了。
        //   ⚠ 本页原来每一处写回控件都用 _suppressAuto 包着（AdoptSolvedDesign、
        //     LoadDesignSpec、EnforceTabLenFloor 都是），我新写这段时漏了这一条。
        bool keepSuppress = _suppressAuto;
        _suppressAuto = true;
        try
        {
        _plateBox.SuspendLayout();
        foreach (Control c in _plateBox.Controls.Cast<Control>().ToArray()) c.Dispose();
        _plateBox.Controls.Clear();

        _tPlate  = Enumerable.Range(0, n).Select(i => Num((decimal)vT[i], 0.10m, 8.0m, 0.02m, 3)).ToArray();
        _tabIns  = Enumerable.Range(0, n).Select(i => Ins()).ToArray();
        _ringMul = Enumerable.Range(0, n).Select(i => Ring()).ToArray();
        _ringR1  = Enumerable.Range(0, n).Select(i => RingR()).ToArray();
        _ringR2  = Enumerable.Range(0, n).Select(i => RingR2()).ToArray();
        _ringT2  = Enumerable.Range(0, n).Select(i => Ring()).ToArray();
        _slotDeg = Enumerable.Range(0, n).Select(i => Slot()).ToArray();
        _holeR   = Enumerable.Range(0, n).Select(i => Hole()).ToArray();
        _holeAsp = Enumerable.Range(0, n).Select(i => HoleAsp()).ToArray();
        _tongue  = Enumerable.Range(0, n).Select(i => Tongue()).ToArray();
        _slotCenter = Enumerable.Range(0, n).Select(i => { var c = Num(0m, -180m, 180m, 1m, 0); c.Enabled = false; return c; }).ToArray();
        _holeX = Enumerable.Range(0, n).Select(i => { var c = Num(0m, -1000m, 0m, 0.1m, 1); c.Enabled = false; return c; }).ToArray();
        _discShape = Enumerable.Range(0, n).Select(i => new Label { Text = Solver.DiscShapeName(0), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }).ToArray();
        _holeShape = Enumerable.Range(0, n).Select(i => new Label { Text = Solver.HoleShapeName(0), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }).ToArray();
        for (int i = 0; i < n; i++)
        {
            _tabIns[i].Value  = (decimal)Math.Clamp(vI[i],  (double)_tabIns[i].Minimum,  (double)_tabIns[i].Maximum);
            _ringMul[i].Value = (decimal)Math.Clamp(vR[i],  (double)_ringMul[i].Minimum, (double)_ringMul[i].Maximum);
            _ringR1[i].Value  = (decimal)Math.Clamp(v1[i],  (double)_ringR1[i].Minimum,  (double)_ringR1[i].Maximum);
            _ringR2[i].Value  = (decimal)Math.Clamp(v2[i],  (double)_ringR2[i].Minimum,  (double)_ringR2[i].Maximum);
            _ringT2[i].Value  = (decimal)Math.Clamp(vT2[i], (double)_ringT2[i].Minimum,  (double)_ringT2[i].Maximum);
            _slotDeg[i].Value = (decimal)Math.Clamp(vS[i],  (double)_slotDeg[i].Minimum,  (double)_slotDeg[i].Maximum);
            _holeR[i].Value   = (decimal)Math.Clamp(vH[i],  (double)_holeR[i].Minimum,   (double)_holeR[i].Maximum);
        }

        void Head(string t)
        {
            var l = new Label
            {
                Text = t, AutoSize = true, Margin = new Padding(0, 10, 0, 4),
                Font = UiScale.Ui(FontStyle.Bold), ForeColor = Color.FromArgb(40, 90, 140),
            };
            // ★★★ AutoSize 的 Label 按**单行**首选宽度算 ⇒ 比容器宽就被**静默横向裁掉**。
            //   实测（2026-09-03 抓图）：「法兰厚度 mm（4 片＝3 段＋1；可点「自动定厚」求解）」
            //   在屏幕上停在「…；可点」，括号都没闭合。
            //   ⚠ 与 MainForm.Banner 栽的是同一个坑，那边已经用「给最大宽度让它折行」修过。
            //   ⚠ 宽度取**父容器**的：_plateBox 自己是 AutoSize，拿它的宽度会绕回来。
            void Fit()
            {
                var host = _plateBox.Parent;
                if (host is null) return;
                int w = host.ClientSize.Width - _plateBox.Margin.Horizontal - l.Margin.Horizontal;
                if (w <= 0) return;
                var want = new Size(w, 0);
                if (l.MaximumSize != want) l.MaximumSize = want;   // 必须先比再设，否则布局抖
            }
            l.ParentChanged += (_, _) => Fit();
            _plateBox.Controls.Add(l); _plateBox.SetColumnSpan(l, 2);
            Fit();
            if (_plateBox.Parent is { } p0) p0.SizeChanged += (_, _) => Fit();
        }
        void Row(string label, Control c, string? tip = null)
        {
            var l = new Label
            { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) };
            // ⚠ 提示语里装着**实测**（单调性、随形状变号、每克铂买到的裕度）——
            //   2026-09-02 我重建这一区时把它们整段删掉过一次，那是把已经落进 APP 的知识又弄丢。
            if (tip is not null) new ToolTip().SetToolTip(l, tip);
            _plateBox.Controls.Add(l);
            _plateBox.Controls.Add(c);
        }

        string tipPlate =
            "**最强的旋钮**（0.8 档 2026-08 离线实测，端点均已收敛）：\n" +
            $"  法兰增量温降 {dDip_dPlate:+0.0;−0.0} K/mm　圆盘区最高温 −14.6 K/mm　法兰重 +264 g/mm\n" +
            "⚠ 法兰增量温降是**正号** —— 加厚会把它推向限值。「哪里热就加厚哪里」在这里是反的：\n" +
            "  加厚同时降单位面积发热（∝1/t）与增强横向导热（∝t），后者把热从管根抽走。\n" +
            "共用片承 √3 倍电流、发热 3 倍 ⇒ 必须比端片厚，四片等厚不是最优。";
        string tipIns =
            $"D8 里它是**免费旋钮**：主要动「从管子抽多少热」"
            + $"（判据 {Criteria.Explain("管孔净流入")} 与 {Criteria.Explain("③")}），" + Environment.NewLine +
            "而对 圆盘区最高温（圆盘区局部峰值）几乎不动 —— 所以它先调，板厚只做接力与省铂。" + Environment.NewLine +
            "初始值取下界 0.3（≈裸舌）：那是真实状态，不是捏的数。优化器会自己往上加。";
        string tipRing =
            "只压**管孔周围**的局部电流拥塞（判据 圆盘区最高温），作用范围 r ≤ 孔+6 mm。" + Environment.NewLine +
            "⚠ **这个灵敏度随形状变号，别照抄任何一个数**（2026-08-28 实测）：" + Environment.NewLine +
            "　· 窄舌形状上曾测得 d圆盘区最高温/d倍率 ≈ **−1.4** K/单位（加环压得住）；" + Environment.NewLine +
            // ★ 2026-09-02：原文在这几行里直接印命令行开关名 `--monotone` 给现场工程师看。
            //   用户拍板：APP 不留命令行形式的操作，界面上也不该出现我的工装词汇。
            //   实测结论保留（它是真的、有日期、有数），只是不再报是哪个开关跑的。
            "　· **现役宽舌形状**上，全量程实测（0.8 档，2026-08-30 首次真跑）：" + Environment.NewLine +
            "　　倍率 1.00→2.50 把 圆盘区最高温 从 **−0.208 挪到 −0.124**（限值 ≤5，越大越差）" + Environment.NewLine +
            "　　⇒ **+0.084 K，方向相反**，却多花 **137 g** 铂 —— 舌片宽了，孔周本来就不拥塞。" + Environment.NewLine +
            "　　⚠ 更正（2026-08-30）：这一行 08-29 写的时候也标着「实测」，" + Environment.NewLine +
            "　　　但那时**一次没跑过** —— 当时的 +0.08 是从别处测得的导数 +0.056 线性外推的。" + Environment.NewLine +
            "　　　跑完之后两者对上了（+0.084），但**当时那个出处是假的**。" + Environment.NewLine +
            "⇒ **你不必自己判断动不动它**：求解器每轮会对你手上这个形状当场量一次，" + Environment.NewLine +
            "　抬它有没有用、值不值那点铂，比价结果印在输出框里。上限 2.5。" + Environment.NewLine +
            "初始值 1.00 = 无台阶（真实状态）。";
        string tipShape =
            "★ 「外扩」是**从管轴量的半径**减去管孔半径，不是「画在盘上的一圈」。" + Environment.NewLine +
            "　厚度按 r 分级：r ≤ 孔+r₁ 取 t₁×板厚；孔+r₁ < r ≤ 孔+r₂ 取 t₂×板厚；再外为板厚。" + Environment.NewLine +
            "⚠ **超过盘半径之后它继续作用在舌片根部** —— 盘 Ø60 时盘面只到 孔+4.2 mm，" + Environment.NewLine +
            "　r₂ 再往外加厚的是舌根。实测把 r₂ 扫到 16 mm 仍持续见效，就是这个缘故。" + Environment.NewLine +
            "── 单调性实测（0.8 档，2026-08-30 首次跑）" + Environment.NewLine +
            "　r₁ 1→10／r₂ 4→16／t₂ 1→2：三条对 抽热D、法兰增量温降、圆盘区最高温 **全单调** ⇒ 可二分。" + Environment.NewLine +
            "　但方向是：只有 管孔净流入 变好，**法兰增量温降与圆盘区最高温都变坏** ⇒ 它们是「花铂换抽热」的旋钮，" + Environment.NewLine +
            "　不是「治判据」的旋钮。所以**没有**进求解器的分配表（那要先做敏感度矩阵）。" + Environment.NewLine +
            "── 谁在动它们（2026-08-30 起变了）" + Environment.NewLine +
            "　**t₂ 是求解器旋钮**：它是判据 管孔净流入 的**首选**候选，排在板厚前面 ——" + Environment.NewLine +
            "　实测每克铂买到的裕度是板厚的 **1.7–3.3 倍**，而每单位管孔净流入的法兰增量温降代价几乎相同。" + Environment.NewLine +
            "　「自动定厚」解完会把 t₂ 写回这里并自动勾上「逐片自定」。" + Environment.NewLine +
            "　**r₁ / r₂ 不是**求解器旋钮：t₁ = t₂ = 1.00 时台阶根本不存在，挪半径无效。" + Environment.NewLine +
            "　要让它们有意义，先把 t₁ 或 t₂ 抬离 1.00。";

        Head($"法兰厚度 mm（{n} 片＝{n - 1} 段＋1；可点「自动定厚」求解）");
        for (int i = 0; i < n; i++) Row(names[i], _tPlate[i], tipPlate);
        // ★★★★★ R11（用户 2026-09-08）：舌片厚**不是旋钮**，只显示 —— 改舌宽/开孔/工况它才变，改圆盘板厚它不变
        Head("舌片厚 mm（算出来的，不是旋钮）");
        string tipTongue =
            "舌片厚 = 设计电流 ÷ (设定 J × 舌片最窄有效宽)，闭式（用户 2026-09-08：舌片厚度是截面积 I/J ÷ 舌宽；J 在 ① 输入设定，预设 10）。" + Environment.NewLine +
            "设计电流由 20 °C/h 空管升温算出，只与管、管保温、工况有关 ⇒ 改舌宽、开孔、改工况它才变，改圆盘板厚它不变。" + Environment.NewLine +
            "不低于板料下限（烧穿 0.6 mm）；向上落到图纸格 0.01 mm。";
        for (int i = 0; i < n; i++) Row(names[i], _tongue[i], tipTongue);
        Head("逐片舌保温 mm（不花铂的旋钮）");
        for (int i = 0; i < n; i++) Row(names[i], _tabIns[i], tipIns);
        Head("管孔渐变环倍率（1.00 = 无台阶）");
        for (int i = 0; i < n; i++) Row(names[i], _ringMul[i], tipRing);
        Head("管孔渐变环形状 r₁ / r₂ / t₂");
        for (int i = 0; i < n; i++) Row($"{names[i]} r₁ mm", _ringR1[i], tipShape);
        for (int i = 0; i < n; i++) Row($"{names[i]} r₂ mm", _ringR2[i], tipShape);
        for (int i = 0; i < n; i++) Row($"{names[i]} t₂", _ringT2[i], tipShape);

        // ★★★ 圆盘背侧减重槽（2026-09-05）。位置由**场**定，不是拍的：
        //   移除优先级 = 导热贡献 ÷ 电流密度，最高处在管孔外缘、背对舌片那一侧。
        //   实测 27–40 mm / 180° ⇒ 抽热 −42.2 %，峰值电流密度只 +2.9 %，体积 −5.9 %。
        //   同样的料挖在舌片上只换到 −0.5 % —— 每 1 % 体积的收益差 60 倍。
        Head("圆盘背侧减重槽（度；0 = 不开槽）");
        string tipSlot =
            "开在管孔外缘、**背对舌片**那半圈 —— 电流从舌片进来绕过管孔，基本不走那里，"
          + "而热照样从那里被抽走。挖掉它等于**只减抽热、几乎不增电流密度**。"
          + Environment.NewLine
          + "实测（盘Ø120 构型）：180° ⇒ 法兰抽热 −37 %，峰值电流密度 +2.3 %。"
          + Environment.NewLine
          + "★ 判据「法兰增量温降」不过时，求解器会**自己调它**（与舌保温同排比价）。"
          + Environment.NewLine
          + "⚠ 上界由几何闭式定：内桥、外桥、周向桥都要留够，开过头会把圆盘割断。";
        for (int i = 0; i < n; i++) Row($"{names[i]} 槽", _slotDeg[i], tipSlot);

        // ★★★ 舌板开孔（2026-09-05，用户要求 R5）
        Head("舌板开孔孔径（半径 mm；0 = 无孔）");
        string tipHole =
            "工程师图上本来就有的孔（装配／工艺／走线）—— APP 回答的是「**这个孔该多大**」。"
          + Environment.NewLine
          + "孔越大：导热截面↓（少抽热，利于「法兰增量温降」），过流截面↓（该处电流密度↑）。"
          + Environment.NewLine
          + "★ 判据不过时求解器会**自己调它**，与舌保温、圆盘槽**同排按每克铂比价**。"
          + Environment.NewLine
          + "⚠ 实测（盘Ø120 构型）舌孔换到的抽热远少于圆盘槽 —— 划不划算由求解器当场比，"
          + "不预先替它删掉这个候选。"
          + Environment.NewLine
          + "⚠ 上界闭式：孔缘到舌边要留够桥宽。"
          + Environment.NewLine
          + "⚠ 最小孔径 1 mm（用户 2026-09-08）：填 0 = 无孔，填在 0～1 之间的数按无孔算；求解器不会探 1 mm 以下。";
        for (int i = 0; i < n; i++) Row($"{names[i]} 孔", _holeR[i], tipHole);
        string tipAsp =
            "1 = 圆孔；>1 = **顺着电流拉长**的椭圆（长轴顺流）。"
            + Environment.NewLine
            + "等面积实测：3:1 顺流 ⇒ 峰值电流密度 **-10.6 %** —— 挖了料，电流反而更顺。"
            + Environment.NewLine
            + "⚠ 横着挡电流则相反：同样面积把峰值顶高 29 %。所以只让它顺流拉长，不给转角。"
            + Environment.NewLine
            + "★ 判据「圆盘区最高温」不过时，求解器会**自己调它**，与舌保温、环倍率同排比价。";
        for (int i = 0; i < n; i++) Row($"{names[i]} 孔拉长", _holeAsp[i], tipAsp);

        // ★★★ R12／R13（2026-09-09）：位置由场定、形状由求解器比价选 —— 都是算出来的，只读显示
        Head("场定的位置与形状（算出来的，不是旋钮）");
        string tipDerived =
            "槽心角：每轮从最新收敛的场算「移除优先级 = 导热贡献 ÷ 电流密度」，槽心落在优先级最高的角向（单舌片 0° = 背对舌片；双舌片对称进电落到 ±90° 一带）。" + Environment.NewLine +
            "圆盘挖料形状：弯椭圆槽／长椭圆（切向／顺当地电流）各抬到各的上界探针比价，选中的存进设计并出图。" + Environment.NewLine +
            "舌孔孔心：场给的位置只印在求解器轨迹里（2026-09-05 实测最优在 −50、不在优先级最高处），几何仍按舌片自由段中点。" + Environment.NewLine +
            "舌孔形状：圆／圆角三角／圆角方 —— 舌片按 J=10 定厚后孔径上界不足 1 mm，这一族在求解器里到不了（R15）。";
        for (int i = 0; i < n; i++) Row($"{names[i]} 槽心角 °", _slotCenter[i], tipDerived);
        for (int i = 0; i < n; i++) Row($"{names[i]} 圆盘挖料形状", _discShape[i], tipDerived);
        for (int i = 0; i < n; i++) Row($"{names[i]} 舌孔孔心 x mm", _holeX[i], tipDerived);
        for (int i = 0; i < n; i++) Row($"{names[i]} 舌孔形状", _holeShape[i], tipDerived);

        _plateBox.ResumeLayout();
        }
        finally { _suppressAuto = keepSuppress; }
        // ★ 新造的控件**自动接上**自动重算：构造函数里的 Watch 给每个容器挂了
        //   `c.ControlAdded += (_, e) => Watch(e.Control)` ⇒ 往 _plateBox 里 Add 就会被监听。
        //   （靠这条而不是自己再调一次 Watch —— 那是构造函数里的局部函数，够不到。
        //     这条依赖有门盯着：见 SegmentCountTests。）
        SyncRingShape();
        return true;
    }

    /// <summary>把一组「按 4 片写的」默认值调到 n 片 —— 与 DesignSpec.Fit 同一个补法。</summary>
    private static double[] FitDefault(double[] a, int n)
    {
        var list = new List<double>(a);
        while (list.Count < n) list.Insert(list.Count - 1, list[list.Count - 2]);
        while (list.Count > n && list.Count > 2) list.RemoveAt(list.Count - 2);
        return list.ToArray();
    }

    /// <summary>
    /// 主线工具条的**第二排**。一排塞八个正是 2026-08-20 拆页的病因 ——
    /// 阶段轨收成两格之后按钮回到同一页，用两排分开「主线」与「图纸路/工具」。
    /// </summary>
    /// <summary>分步按钮（自动定厚／搜形状）插到**第二排**「加密复算」之前 —— 顺序 = 流水线：定厚 → 搜形状 → 复核。
    /// R17（2026-09-08）起第一排只有「核算整线」；这一排默认收起，点「手动分步 ▾」展开。</summary>
    internal void MountMainRow(ToolStripItem[] items)
    {
        int at = _tool2.Items.IndexOf(_btnVerify);
        foreach (var it in items) _tool2.Items.Insert(at++, it);
    }

    /// <summary>工具按钮追加到第二排。</summary>
    internal void MountSecondRow(ToolStripItem[] items)
    {
        foreach (var it in items) _tool2.Items.Add(it);
    }

    /// <summary>片名：首=入口、末=出口，中间按段表拼「HC1|HC2」。</summary>
    private string[] PlateNames()
    {
        var segs = _segs.Where(x => !string.IsNullOrWhiteSpace(x.名称)).Select(x => x.名称).ToArray();
        if (segs.Length == 0) segs = new[] { "HC1", "HC2", "HC3" };
        var r = new List<string> { "入口" };
        for (int i = 0; i + 1 < segs.Length; i++) r.Add($"{segs[i]}|{segs[i + 1]}");
        r.Add("出口");
        return r.ToArray();
    }

    /// <summary>段数没变、只是段改名时，只换标签，不重建控件（免得把值与监听都丢了）。</summary>
    private void RenamePlateRows(string[] names)
    {
        var labs = _plateBox.Controls.Cast<Control>().OfType<Label>()
                            .Where(l => l.Font.Bold == false).ToArray();
        int k = 0;
        foreach (string suffix in new[] { "", "", "", " r₁ mm", " r₂ mm", " t₂" })
            for (int i = 0; i < names.Length && k < labs.Length; i++, k++)
                labs[k].Text = names[i] + suffix;
    }

    /// <summary>
    /// ★★★★★ **回到 UI 线程再动控件**（2026-09-02 二分时抓到）。
    ///
    /// 实况：对帐跑到一半整个进程带着这条栈崩掉 ——
    /// <code>
    ///   ToolStripItem.OnTextChanged
    ///     at LineDesignPage.&lt;RunAsync&gt;b__0(String s)      ← 进度回调
    ///     at System.Progress`1.InvokeHandlers
    ///     at ThreadPoolWorkQueue.Dispatch                  ← **线程池线程**
    /// </code>
    /// <c>Progress&lt;T&gt;</c> 只在**构造时** <c>SynchronizationContext.Current</c> 非空才回主线程；
    /// 取不到就退到线程池 —— 于是 `_status.Text = s` 变成跨线程动 UI。
    ///
    /// ⚠ 这不是测试环境专有：它取决于构造那一刻的同步上下文，**在工程师机器上同样会随机崩**。
    ///   而且崩在进度回调里 —— 看起来像「算着算着自己没了」，最难查的那一种。
    /// ⇒ 所有进度回调一律走这里：需要就 Invoke 回去，不需要就直接跑。
    /// </summary>
    private void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(a); } catch (ObjectDisposedException) { } }
        else a();
    }

    private void PushFlow()
    {
        if (Shared is not { } f) return;
        f.CurrentSnap = CurrentSnap();
        f.SolvedSnap = _solvedSnap;
        f.Last = _last;
        // ★ 复核状态与解一样要发布。⚠ 快照单独记：改完参数还挂着上一次的复核结论
        //   是**假绿灯**，比没复核更坏（会让人以为验过了）。
        // ★★★ 定尺寸器上一次有没有宣告「这组输入不可行」。见 FlowState.SizerProvedInfeasible。
        //   ⚠ **必须放在 PushFlow，不能放在 SyncAnalysisPending** —— 后者只在两处被调
        //     （首屏、分析几何变数跑完），**解完之后一次都不会调** ⇒ 位永远推不上去。
        //     我 2026-09-03 第一版就放错了地方，走查照旧连指「自动定厚」，看起来像没修。
        f.SizerProvedInfeasible = _sizerInfeasible;
        f.MeshVerified = _meshVerify is { Converged: true };
        f.VerifiedSnap = _verifiedSnap;
        f.VerifyNote = _meshVerify?.Verdict ?? "";

        // 几何闭式判据：解析模式才有解析量；.3dm 模式下 GeometryScreen 会返回两条「无法判定」
        try
        {
            var plates = _srcAnalytic.Checked
                ? PageToDesignSpec().BuildCase(_base, checkRamp: false).FlangePlates
                : System.Array.Empty<FlangePlate>();
            f.GeomScreen = GeometryScreen.Judge(
                plates, DesignSpec.Current.ClampLengthMm, FreeTabMin);
        }
        catch { /* 几何还没填全（例如 .3dm 没选文件）时不该把界面拖垮 */ }

        // ★ ① 升温快筛（2026-08-24 接上）。它与 ⑤⑥ 是同一类东西 —— **闭式、微秒级**，
        //   所以理应在同一处算出来。此前 `FlowState.RampScreen` 这个栏位
        //   **一处赋值都没有**：`RampScreen.Judge` 在整个生产路径上从未被调用过
        //   （只有单测在调），于是
        //     · Flow.FindCheck 里那条「退到闭式快筛」的分支是**死的**，
        //       而它的注释还写着「少了这条，快筛就成了一张能绕过权威判据的通行证」；
        //     · ① 那一格的门禁注释写着「解锁 ③：几何可造（⑤⑥）+ **升温快筛不判死**」，
        //       而 GateSpec 里**只有 ⑤⑥**。
        //   三处都在描述一件没有发生的事。⇒ 接上，并把 ① 也放进 GateSpec，让注释成真。
        //
        //   ⚠ 判的是**当前这一组参数**（管壁 / 管保温），不是 ① 页那张扫描表里的某一行 ——
        //     表是给人看趋势的，门禁要的是「你正在设计的这一个点过不过」。
        //   ⚠ 管壁与管保温是**管侧**参数，与法兰几何来源无关 ⇒ .3dm 模式下同样算得出，
        //     不像 ⑤⑥ 那样会退化成「无法判定」。
        try
        {
            f.RampScreen = RampScreen.Judge(
                _base, (double)_tubeIns.Value, (double)_wall.Value);
        }
        catch { /* 同上：界面不能因为快筛算不出来就垮掉 */ }

        f.Notify();
    }

    // ── 归 ④「定尺寸」与 ⑤「交付」两格托管的命令按钮。
    //    本页仍是它们的**所有者**（跑起来改文字、互相禁用的逻辑都在 RunAsync 里），
    //    ④⑤ 只是把它们挂到自己的工具条上。⇒ 状态只有一份。
    internal ToolStripButton BtnAutoThick => _btnAuto;
    /// <summary>图纸路的两个按钮 —— 归「① 输入」页（R21）。</summary>
    internal ToolStripButton BtnAnalyze => _btnAnalyze;
    internal ToolStripButton BtnToAnalytic => _btnToAnalytic;
    internal ToolStripButton BtnSearchShape => _btnShape;
    internal ToolStripButton BtnExportPage3dm => _btnExport;
    internal ToolStripButton BtnExportFinal3dm => _btn3dm;
    // ── 设计记录那一组（2026-08-23 从 ③ 拆到独立页）。控件仍归本页所有 ——
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

    /// <summary>
    /// 判据表 : 说明 的高度比 —— **按有没有结果调**。
    ///
    /// 还没解过时判据表一行都没有，却占着近一半高度；工程师开箱第一眼落在一张空格子上，
    /// 而「填好左边的参数，点核算整线」被挤在下面。⇒ 没结果时收到 14 %，有结果再放到 46 %。
    /// ⚠ 只在**比例真的要变**时设，否则每次 Show 都会让分隔条跳一下。
    /// </summary>
    private void SyncTextSplit()
    {
        // R17：③ 页上判据表在上、场图在下。有结果时判据表占 46 %，没结果时让位给场图的空态提示
        if (_resultSplit.Parent is null || _resultSplit.Height <= 0) return;
        int want = (int)(_resultSplit.Height * (_checks.Rows.Count > 0 ? 0.46 : 0.14));
        want = Math.Max(_resultSplit.Panel1MinSize + 1,
               Math.Min(want, _resultSplit.Height - _resultSplit.Panel2MinSize - _resultSplit.SplitterWidth - 1));
        if (Math.Abs(_resultSplit.SplitterDistance - want) > UiScale.S(8))
            _resultSplit.SplitterDistance = want;
    }

    private void FillChecks(LineResult? r)
    {
        _checks.Rows.Clear();
        if (r is null || !r.Ok) { SyncTextSplit(); return; }
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
            // ★★★ **界面上不许出现判据代号**（用户 2026-08-30：「工程师看不懂」）。
            //   代号是从内核流过来的：LineResult.Key 自己就带着它（NetFlux = "②′管孔净流入"），
            //   判据表直接印 ConstraintOut.Name 就把它带上了屏。
            //   ⚠ 更要紧的是形状冲突：判据代号 ⑤（舌片自由段）与页签上的阶段号 ⑤（交付）
            //     长得一样、含义无关，摆在同一个界面上必然误读。
            //   ⇒ 显示层剥壳，只此一处；Key 本身不动（它是识别用的唯一来源）。
            string nm = Criteria.Plain(c.Name);
            // ★ 单位跟在判据名后面（用户 2026-09-02：「全名 + 单位」）。
            //   ⚠ 放在**名字**列而不是数值列：数值列是右对齐的，掺进单位就对不齐，
            //     而且「实际」与「限值」两格会把同一个单位印两遍。
            //   ⚠ 查不到单位就什么都不加（UnitOf 找不到返回空，绝不编）。
            string u = Criteria.UnitOf(c.Name);
            if (u.Length > 0) nm += "（" + u + "）";
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
        SyncTextSplit();     // 有结果了 ⇒ 判据表放回 46 %
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
                              // ★ 原来是 Split(' ')[0] —— 对「③ 法兰增量温降」取出来的是
                              //   光秃秃一个「③」，工程师看到的是「✗ 2 条判据没过：③ 9.5/10.0」。
                              string.Join("；", bads.Select(c => $"{Criteria.Plain(c.Name)} " +
                                  (c.Undetermined ? "无法判定" : $"{c.Actual:0.0}/{c.Limit:0.0}"))));
                var first = bads[0];
                sb.AppendLine($"　先解决这一条 ⇒ **{Criteria.Plain(first.Name)}**（{first.Where}）");
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
        // ═══ 结论：工程师的四个问题，按他问的顺序 ═════════════════
        //   过没过 → 多少铂 → 这个数准不准 → 这次改了什么
        //   ★ 2026-09-02 重排（用户：「输出也可以简化，只保留场图与前后尺寸比较
        //     与其它你觉得重要的计算结果」）。明细与求解器内部状态收进两个开关。
        sb.AppendLine("判据表见上方表格：不过的行标红，注释在鼠标悬停里。");
        sb.AppendLine();
        sb.AppendLine($"★ 整线总铂 {r.TotalMassG:0} g（管 {r.TubeMassG:0} + 法兰 {r.FlangeMassG:0}）"
                    + $"　基准 {r.BaselineMassG:0} g　省 {r.SavingPct:0.0} %");
        sb.AppendLine($"  玻璃温降 模型 {r.GlassDropModelK:0.0} / 实测 {r.GlassDropMeasuredK:0.0} K"
                    + "　（模型唯一的现场验证点）");

        // ★ 收敛只留**一句结论**；细节（ω / Anderson / 步长×放大）进「求解器诊断」。
        //   ⚠ 未收敛时**必须说**，而且不受开关控制 —— 那时它是最重要的信息。
        if (!r.Converged)
            sb.AppendLine("  ⚠ **未收敛 ⇒ 上面每个数都不可引用**");
        else
        {
            var cpl = r.Notes.FirstOrDefault(x => x.Contains("外层耦合", StringComparison.Ordinal));
            if (cpl is not null)
            {
                int p = cpl.IndexOf("（", StringComparison.Ordinal);
                sb.AppendLine("  收敛　" + (p > 0 ? cpl[..p] : cpl));
            }
        }

        // ═══ 这次改了什么（前 → 后）═══════════════════════════════
        //   工程师点一次「核算整线」，程序可能动了厚度、保温、环倍率、甚至盘径与舌宽。
        //   **动了他填的数就必须说** —— 不说就是静默改输入。
        if (_beforeSnap is { } b0)
        {
            var now = CurrentSnap();
            var rows = new List<(string Name, double A, double B, string U)>
            {
                ("管壁", b0.Wall, now.Wall, "mm"),
                ("板厚", b0.Plate, now.Plate, "mm"),
                ("管保温", b0.TubeIns, now.TubeIns, "mm"),
                ("盘径", 2 * b0.Disc, 2 * now.Disc, "mm"),
                ("舌长", b0.TabLen, now.TabLen, "mm"),
                ("舌端半宽", b0.TabW, now.TabW, "mm"),
                ("舌保温", b0.SizerTabIns, now.SizerTabIns, "mm"),
                ("环倍率", b0.SizerRingMul, now.SizerRingMul, ""),
                // ★ 程序自己开了槽就必须说 —— 不说等于静默改了工程师的零件。
                ("圆盘背侧减重槽", b0.SizerSlotDeg, now.SizerSlotDeg, "°"),
                ("舌板开孔孔径", b0.SizerHoleR, now.SizerHoleR, "mm"),
                ("舌板开孔顺流拉长比", b0.SizerHoleAsp, now.SizerHoleAsp, ""),
            };
            var moved = rows.Where(x => !double.IsNaN(x.A) && !double.IsNaN(x.B)
                                        && Math.Abs(x.A - x.B) > 1e-9).ToArray();
            sb.AppendLine();
            if (moved.Length == 0 && !(Math.Abs(r.TotalMassG - _beforeMassG) > 0.05))
                sb.AppendLine("◆ 这次没改动你填的任何一个数（第一次解就已经全过）。");
            else
            {
                sb.AppendLine("◆ **这次改了什么**（前 → 后）");
                foreach (var (nm, x, y, u) in moved)
                    sb.AppendLine($"　{nm}	{x:0.###} → {y:0.###} {u}	{y - x:+0.###;-0.###}");
                if (!double.IsNaN(_beforeMassG))
                    sb.AppendLine($"　⇒ 整线总铂 {_beforeMassG:0.0} → {r.TotalMassG:0.0} g"
                                + $"（{r.TotalMassG - _beforeMassG:+0.0;-0.0} g）");
            }
        }

        // ═══ 明细：默认收起 ═══════════════════════════════════════
        if (_showDetail.Checked)
        {
            sb.AppendLine();
            // ⚠ 两张表**必须被一个空行隔开**：连着写会被认成同一张表、列宽合并计算，
            //   两组毫不相干的量（控温 °C 与 Φ）从此互相顶着走。
            sb.AppendLine("段	控温 °C	电流 A	管 J A/mm²	管根 °C	衔接温差 K	管重 g");
            foreach (var sg in r.Segments)
                sb.AppendLine($"{sg.Name}	{sg.SetpointC:0}	{sg.CurrentA:0}	{sg.TubeJAPerMm2:0.00}	"
                            + $"{sg.TRootC:0.0}	{sg.RootDeltaK.ToString("+0.0;-0.0")}	{sg.MassG:0}");
            sb.AppendLine();
            sb.AppendLine("法兰	电流 A	J_max A/mm²	Φ	抽热 W	最高 °C	铂重 g");
            foreach (var f in r.Flanges)
                sb.AppendLine($"{f.Name}	{f.CurrentA:0}	{f.JMaxAPerMm2:0.00}	{f.Phi:0.000}	"
                            + $"{f.QFromTubeW.ToString("+0;-0")}	{f.TMaxC:0.0}	{f.MassG:0}");
        }

        // ═══ 求解器诊断：默认收起；**未收敛时强制展开** ══════════
        //   ⚠ 折叠不许把坏消息藏起来。
        if (_showDiag.Checked || !r.Converged)
        {
            sb.AppendLine();
            sb.AppendLine("── 求解器诊断" + (r.Converged ? "" : "（未收敛，已强制展开）"));
            // ⚠ 这里是 Notes 的**唯一**打印点。2026-08 起「外层耦合…」那一行被印了两次
            //   （一次带 ⓘ、一次在这个 foreach 里），逐字相同 —— 收敛那句已经上移到结论区。
            foreach (var n in r.Notes) sb.AppendLine("  " + n);

            // ★★★★★ **求解器自己的推理过程**（2026-09-03 接上）。
            //
            //   在此之前 SolverResult.Trace **一次都没到过界面** —— 它只进
            //   progress 回调，而那条路的终点是状态栏**一行、被下一行盖掉**。
            //   于是求解器每轮当场量出来的「比价」：
            //       板厚 补得上、每克铂买 0.545（裕度 +515.60／铂 +945.3 g）
            //       环倍率 补不上、每克铂买 5.080（裕度 +34.40／铂 +6.8 g）
            //   工程师**一次也没看见过** —— 而环倍率那个输入框的提示语上
            //   写着「抬它有没有用、值不值那点铂，比价结果**印在输出框里**」。
            //   说了没做到，比没说更坏。
            //
            //   ⚠ 放在诊断开关下面：它是**为什么这么调**，不是判定；
            //     判定在上面的结论区，两者不许混。
            if (_lastTrace.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  ── 每一轮为什么这么调（求解器当场量的）");
                const int cap = 200;
                foreach (var t in _lastTrace.Take(cap)) sb.AppendLine("  " + t);
                if (_lastTrace.Length > cap)
                    sb.AppendLine($"  …（还有 {_lastTrace.Length - cap} 行，已略）");
            }
        }
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
        // ⇒ 改走 `WriteFinal3dm`（与「导出设计记录 3DM」同一个写入器，今天已验过
        //   逐件质量对账 +0.03 %），导出的就是刚才解的那套几何。
        var dExp = PageToDesignSpec();
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
            for (int i = 0; i < _file3dm.Length && i < _tPlate.Length; i++)
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
    /// 把本页的解析几何写成**单图层、多级台阶**的 .3dm —— 一张 **APP 自己读得回来**的图。
    ///
    /// ★ 它补的是解析路径与 .3dm 路径之间断掉的那一环：
    ///   「导出本页/设计记录 3DM」走 <see cref="Geometry3dm.WriteFinal3dm"/>，写的是**多图层**
    ///   （板身 / 环外级 / 环内级 / 压接段 / 角焊缝），而读取端要**单图层**
    ///   ⇒ APP 导出的图，APP 自己读不回来。于是
    ///   「解析里搜出方案 → 出图 → 去 Rhino 改轮廓/挪槽 → 读回来核算」这条路是断的。
    ///
    /// ⚠ 舌片写成**等宽**（与 <see cref="FlangePlate.TabParallel"/> 同口径）。
    ///   Geom 的 steps 模式旧默认是梯形，而设计记录几何早就不用梯形了 ——
    ///   实测把梯形舌那张图读回来，等宽替身面积差 −12 %，保真门当场拒绝，
    ///   而它拒绝得对：错的是写入器写了 APP 已经不再设计的形状。
    ///
    /// ★★ 写完**立刻按读取端的口径量回来**（照 --make3dm 的先例）：
    ///   Geom 子进程的注释里记着一次事故 —— 自己写出的 .3dm 再读回来量到 0 材料，
    ///   文件能打开、图看着对，数是错的。不回读就等于没写。
    /// </summary>
    private void ExportReadable3dm()
    {
        if (!_srcAnalytic.Checked)
        {
            MessageBox.Show(this,
                "本命令导出的是**解析几何**。当前是 Rhino .3dm 模式 —— 图纸本来就在你手上，"
                + Environment.NewLine + "要改厚度请用「自动定厚」之后的逐级出图。",
                "导出可回读 3DM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new FolderBrowserDialog { Description = "选一个目录，四片各写一个 .3dm" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var d = PageToDesignSpec();
        double floor = d.DiscFloorMm(_base);
        var sb = new StringBuilder();
        sb.AppendLine("=== 导出可回读 3DM（单图层 · 多级台阶 · 等宽舌）===");
        sb.AppendLine("写完立刻按读取端的口径量回来 —— 不回读就等于没写。");
        sb.AppendLine();
        sb.AppendLine("片	文件	写入级厚 mm	读回级厚 mm	盘R 写/读	判定");

        string[] pn = { "入口", "共用1", "共用2", "出口" };
        int bad = 0;
        try
        {
            Cursor = Cursors.WaitCursor;
            for (int j = 0; j < d.TabThickMm.Length; j++)
            {
                double td = Math.Max(d.TabThickMm[j], floor);
                var radii = d.RingRadiiMm.Concat(new[] { d.DiscRadiusMm }).ToArray();
                var thick = new[] { td * d.RingMul[j], td * d.RingMulOuter(j), td };
                // R11：舌片自己的厚度（NaN = 与基板同）
                double tt = j < d.TongueThickMm.Length && !double.IsNaN(d.TongueThickMm[j]) ? Math.Max(d.TongueThickMm[j], floor) : td;
                string file = Path.Combine(dlg.SelectedPath,
                    $"可回读_{pn[j]}_壁{d.WallMm:0.0}.3dm");

                Geometry3dm.WriteStepped3dm(file, d.HoleRadiusMm, radii, thick,
                    -d.TabLengthMm, d.TabHalfWidthMm, tt,
                    // ★★★ 槽要真的写进图（2026-09-05）。写死 slotCount: 0 的话，
                    //   求解器开了槽、判据按有槽算，而**出的图上没有槽** ——
                    //   工程师拿着一张与计算不符的图去加工。那比不开槽更糟。
                    slotCount: d.SlotSpanDeg[j] > 0.5 ? 1 : 0,
                    slotWidthDeg: d.SlotSpanDeg[j],
                    slotRInMm: d.SlotBandMm(Math.Max(td, d.WallMm)).RIn,
                    slotROutMm: d.SlotBandMm(Math.Max(td, d.WallMm)).ROut,
                    tabHoleXMm: d.TabHoleCenterXMm(),
                    tabHoleRMm: j < d.TabHoleRMm.Length ? d.TabHoleRMm[j] : 0);

                // ── 回读校验：走的是**读取端那条路**，不是自己再算一遍
                var f = Geometry3dm.LoadThickness(file, "法兰", double.NaN, 0.5);
                var sh = PlateShapeAnalyzer.Analyze(f);
                var got = sh.Levels.Select(x => x.ThicknessMm).ToArray();
                // ★ R11：舌片另有厚度时读回来会多一级（舌片那级）⇒ 按**集合**比：写入的每个厚度都读得到，读到的每级都是写入的
                var expect = Math.Abs(tt - td) > 1e-9 ? thick.Concat(new[] { tt }).ToArray() : thick;
                bool ok = expect.All(v => got.Any(g2 => Math.Abs(g2 - v) < 0.05))
                       && got.All(g2 => expect.Any(v => Math.Abs(g2 - v) < 0.05))
                       && Math.Abs(sh.DiscRadiusMm - d.DiscRadiusMm) < 0.5;
                if (!ok) bad++;
                sb.AppendLine($"{pn[j]}	{Path.GetFileName(file)}	"
                    + string.Join("/", thick.Select(v => v.ToString("0.00"))) + "	"
                    + string.Join("/", got.Select(v => v.ToString("0.00"))) + "	"
                    + $"{d.DiscRadiusMm:0.0}/{sh.DiscRadiusMm:0.0}	{(ok ? "✓" : "✗ 对不上")}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally { Cursor = Cursors.Default; }

        sb.AppendLine();
        sb.AppendLine(bad == 0
            ? "✓ 四片都能原样读回来 —— 这些图可以直接切到「Rhino .3dm 文件」模式喂回本页。"
            : $"✗ {bad} 片读回来对不上。**别用这些图** —— 写与读两侧的口径不一致，"
              + "先查 Geom 的 steps 模式与 PlateShapeAnalyzer。");
        _out.Text = sb.ToString() + Environment.NewLine + _out.Text;
    }

    /// <summary>
    /// 把「分析几何变数」反推出来的形状**交给解析路** —— 填进盘径/舌长/舌半宽/管壁/板厚，
    /// 并切到解析模式，于是「◇ 搜形状」可用：那是全程唯一能**改形状**的东西。
    ///
    /// ★ 用户 2026-08-25：「3DM 只读几何数据（画网格的依据），为何不能带入计算？」
    ///   能。反推早就做到了（PlateShapeAnalyzer 一直在印那几个数），缺的只是这一步。
    ///   在此之前两条输入路线给不出接近的答案，本质原因只有一条：**.3dm 路改不了形状**，
    ///   而设计记录的关键一步恰恰是改形状（Ø120 → Ø60）。
    ///
    /// ⚠ 三条近似由 <see cref="ShapeToAnalytic"/> 生成并**原样呈现**，这里不许吞。
    /// ⚠ 第四条只有这里知道，必须自己说：**转过去之后几何不再跟图纸绑定**。
    ///   那正是目的，但人得知道自己跨过了这条线 —— 否则他会以为还在算那张图。
    /// </summary>
    private void AdoptShapeToAnalytic()
    {
        if (_shape is not { } sh)
        {
            Show(_last, "【图纸几何 → 参数：做不了】还没「分析几何变数」—— 没有形状可交。"
                      + Environment.NewLine + "先选 .3dm 与图层，点「分析几何变数」。");
            return;
        }

        ShapeToAnalytic.Knobs k;
        try { k = ShapeToAnalytic.From(sh); }
        catch (Exception ex) { Show(_last, "【图纸几何 → 参数：做不了】" + ex.Message); return; }

        string nl2 = Environment.NewLine;
        // ★ 「够不够像」只有一处来源：AnalyticSurrogate（它有自己的 Tol）。这里**不另立门槛**。
        string fidNote = _surrFid is { } fid
            ? (AnalyticSurrogate.Usable(fid)
               ? "· 解析替身保真度**合格**（" + fid.Report() + "）⇒ 这张图确实属于「圆盘＋舌片」那一族。"
               : "· ⚠ 解析替身保真度**不合格**（" + fid.Report() + "，限 "
                 + (AnalyticSurrogate.Tol * 100).ToString("0.0") + " %）—— 解析模型与这张图不是同一片板，"
                 + "转过去之后算的**不是原图**。仍放行（形状本来就要改），但这句话得记住。")
            : "· ⚠ 没量到替身保真度 ⇒ **不知道**解析模型像不像这张图。";

        // ★★ 控件范围夹住了就**说出来**（2026-09-08）：图纸是真实输入，被夹住等于静默换了零件。
        //   夹住的每一项都列在交接说明里；工程师看得见，才谈得上决定要不要放宽范围。
        var clamped = new List<string>();
        decimal Clamp(NumericUpDown n, double v, string name = "")
        {
            var c = Math.Clamp((decimal)v, n.Minimum, n.Maximum);
            if (name.Length > 0 && Math.Abs((double)c - v) > 1e-9)
                clamped.Add($"{name} 图纸 {v:0.###} → 控件只到 {c:0.###}");
            return c;
        }

        _suppressAuto = true;
        try
        {
            _discD.Value = Clamp(_discD, k.DiscDiameterMm, "盘Ø");
            _tabLen.Value = Clamp(_tabLen, k.TabLengthMm, "舌长");
            _tabW.Value = Clamp(_tabW, k.TabHalfWidthMm, "舌半宽");
            _wall.Value = Clamp(_wall, k.WallMm, "管壁");
            // ★★★★★ R8／R14（用户 2026-09-08）：图纸的各级厚度**逐级**进 r₁/t₁、r₂/t₂ 控件，不压平均。
            //   四个是**成对**的自由度，同一个勾管着；有台阶就勾「逐片自定」并全写，
            //   没台阶就不勾（t₁ = t₂ = 1.00 ⇒ 台阶不存在，半径无意义）。
            _ringShapeCustom.Checked = k.HasRing;
            for (int i2 = 0; i2 < _ringMul.Length; i2++)
            {
                _ringMul[i2].Value = Clamp(_ringMul[i2], k.HasRing ? k.RingMul : 1.0, i2 == 0 ? "内级倍率 t₁" : "");
                if (i2 < _ringR1.Length) _ringR1[i2].Value = Clamp(_ringR1[i2], k.HasRing ? k.RingW1Mm : (double)_ringW.Value, i2 == 0 ? "内级外扩 r₁" : "");
                if (i2 < _ringR2.Length) _ringR2[i2].Value = Clamp(_ringR2[i2], k.HasRing ? k.RingW2Mm : 2 * (double)_ringW.Value, i2 == 0 ? "外级外扩 r₂" : "");
                if (i2 < _ringT2.Length) _ringT2[i2].Value = Clamp(_ringT2[i2], k.HasRing ? k.RingMul2 : 1.0, i2 == 0 ? "外级倍率 t₂" : "");
            }
            _srcAnalytic.Checked = true;      // 切到解析 ⇒ 三个几何控件解禁、搜形状可用
            // ★★ 基板厚必须在**切到解析模式之后**才写（2026-09-08 走查抓到：写在前面被 0.516 盖掉）。
            //   _tPlate 在两个模式下是两个物理量（解析 = 板厚 mm；.3dm = 厚度标度 k），
            //   SyncGeomSource 切模式时整组交换 —— 切换前写进去的板厚会被换成留着的旧值。
            SyncGeomSource();
            for (int i2 = 0; i2 < _tPlate.Length; i2++)
                _tPlate[i2].Value = Clamp(_tPlate[i2], k.PlateThickMm, i2 == 0 ? "基板厚" : "");
        }
        finally { _suppressAuto = false; }
        SyncRingShape();
        // 参数确实变了 ⇒ 上一次的解与「越关」一律作废（否则门会开在别组参数的判据表上）
        MarkParamsChanged("图纸几何 → 参数");

        string ringLine = k.HasRing
            ? "管孔台阶：r₁ = 孔+" + k.RingW1Mm.ToString("0.0") + " mm × t₁ " + k.RingMul.ToString("0.000")
              + "　r₂ = 孔+" + k.RingW2Mm.ToString("0.0") + " mm × t₂ " + k.RingMul2.ToString("0.000")
              + "（图纸 " + k.LevelCount + " 级，逐级带过来，不取平均）"
            : "管孔台阶：无（图纸只有一级）";
        string clampLine = clamped.Count == 0 ? ""
            : "⚠ **控件范围夹住了图纸的数**：" + string.Join("；", clamped)
              + " —— 现在算的不是原图那片，要么放宽控件范围，要么回 Rhino 改图。" + nl2;

        Show(_last,
            "【图纸几何 → 参数：已交接】" + nl2
          + "盘Ø " + k.DiscDiameterMm.ToString("0.0")
          + "　舌长 " + k.TabLengthMm.ToString("0.0")
          + "　舌半宽 " + k.TabHalfWidthMm.ToString("0.0")
          + "　管壁 " + k.WallMm.ToString("0.00")
          + "　基板厚 " + k.PlateThickMm.ToString("0.00") + " mm" + nl2
          + ringLine + nl2 + nl2
          + clampLine
          + k.Note + nl2
          + fidNote + nl2
          + "· ★ **从这一刻起几何不再跟图纸绑定** —— 这正是目的：解析路能改形状，"
          + "而图纸那条路不能（它的旋钮只有各级厚度）。" + nl2 + nl2
          + "⇒ 下一步：「◇ 搜形状」现在可用了，它会改盘径与舌宽找最轻的全过解。" + nl2
          + "⚠ 想回到「照图纸算」，把几何来源切回 Rhino .3dm 即可 —— **图纸本身没有被改动**。");
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
            Shared?.Notify();
            // 四片先按同一张图的分级；各片可各自选不同 .3dm 时逐片解析亦可
            var lv = sh.Levels.Select(l => l.ThicknessMm).ToArray();
            _levels = Enumerable.Range(0, 4).Select(_ => (double[])lv.Clone()).ToArray();
            _levelScale = null;
            // ★★★★★ **必须放在 `_levels` 赋值之后**（2026-08-25 当场踩到）。
            //   本方法算两位：`GeomAnalysisPending`（看 `_shape`）与
            //   `SizerNoLevels`（看 `_levels`）。头一版把调用放在 `_shape = sh;` 紧后面，
            //   那时 `_levels` **还没赋值** ⇒ 新那位恒为「没有可调的级」，
            //   于是 Pt_Heater3（明明有多级台阶）也被指路说成「等厚板」。
            //   **把计算加进一个在它所读的数据被填之前就跑的方法** —— 本轮反复抓到的那一族，
            //   这次是我自己犯的。
            SyncAnalysisPending();          // 分析完了 ⇒ 指路不该再指「分析」，且级数已知
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
            _pipeAborted = true;   // 出错同样停整条
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
