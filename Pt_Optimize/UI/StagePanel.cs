using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// 「**你现在在算什么**」—— 常驻状态面板。
///
/// ★ 用户 2026-08-20：「现在是所有标签键都可以点，工程师根本不知道自己目前在算什么」。
///
/// 界面里同时跑着六条链，耗时差四个数量级、权威性完全不同（只有 C 整线耦合可交付）。
/// 在此之前，判断「手上这个数字是哪条链算的、能不能拿去交付」需要读说明书 ——
/// 而用户的另一条诉求正是「不看说明书也能用」。这块面板把那份知识搬到屏幕上。
///
/// ⚠ **它没有任何可点的命令**（唯一的 LinkLabel 只在门锁住时出现）。
///   用户 2026-08-16 说过「UI 已经够复杂，不要再加按钮」——
///   这块面板不增加决策点，反而**消除**一个（「我该信哪个数字」）；
///   而且控件净数是负的：它合并掉了原先散在三处的状态标签
///   （MainForm._segStatus、AnalysisPage._status、LineDesignPage._status）。
///
/// 显示的每一项都来自**已有状态**，本类不做任何计算、更不产生判据。
/// </summary>
public sealed class StagePanel : Panel
{
    private readonly Label _title = new();
    private readonly Label _chain = new();
    private readonly Label _input = new();
    private readonly Label _fresh = new();
    private readonly Label _verdict = new();
    private readonly Label _banner = new();
    private readonly LinkLabel _bypass = new();
    /// <summary>「下一步 → 点『…』」。可点，但**只带你去**，不代你跑。</summary>
    private readonly LinkLabel _next = new();

    /// <summary>
    /// **全 APP 唯一的那根进度条**（2026-08-24）。
    ///
    /// 放在面板里而不是各页工具条上：面板切到哪一页都看得见，
    /// 于是 ④「定尺寸/搜形状」也有条了 —— 它此前一根都没有
    /// （它的按钮是从 ③ 借来的，进度条没借），而搜形状是全程最长的一条。
    /// 数只有一份（<see cref="FlowState.RunningPct"/>），条只有一根，不构成「同一件事两处表达」。
    /// </summary>
    private readonly ProgressBar _bar = new();
    private readonly FlowLayoutPanel _stack = new();

    private readonly FlowState _state;
    private StageId _stage = StageId.整线核算;

    /// <summary>用户点了「我知道风险，越关进入」。参数是被越的那一关。</summary>
    public event Action<StageId>? BypassRequested;
    /// <summary>用户点了「下一步」那一行 —— 参数是该点的命令 Id。**由 MainForm 负责带路，不执行。**</summary>
    public event Action<string>? NextStepRequested;
    private string _nextCmd = "";

    /// <summary>
    /// 本面板想要多高（像素）。
    ///
    /// ⚠ 必须有这个：面板高度原来是**写死**的，门一锁内容就多出三四行
    ///   （为什么锁 / 现在什么状态 / 怎么解锁），于是横幅被挤到滚动条外面 ——
    ///   实测截图里越关链接好端端显示着，而它上面那段「为什么」一个字都看不见。
    ///   **把话说了一半的提示，比不提示更容易误导。**
    /// </summary>
    public event Action<int>? HeightWanted;

    /// <summary>本页输入来自哪儿的补充说明（例如「解析几何」/「.3dm 图纸 4 片」）。</summary>
    public string InputNote { get; set; } = "";

    public StagePanel(FlowState state)
    {
        _state = state;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(250, 250, 247);
        Padding = new Padding(UiScale.S(10), UiScale.S(6), UiScale.S(10), UiScale.S(6));

        // ⚠ 用 FlowLayoutPanel 而**不是**一叠 Dock=Top 的 Label（第一版就栽在这里）：
        //   Dock.Top + AutoSize 对「高度随文字行数变」的标签不可靠 ——
        //   门禁横幅是三行，实测被压成一行高，于是「为什么锁 / 怎么解锁」**整段看不见**，
        //   而越关链接却好端端地显示着。一个把话说了一半的提示，比不提示更容易误导。
        _stack.Dock = DockStyle.Fill;
        _stack.FlowDirection = FlowDirection.TopDown;
        _stack.WrapContents = false;
        _stack.AutoScroll = true;
        Controls.Add(_stack);

        foreach (var (lab, style) in new (Label, FontStyle)[]
        {
            (_title,   FontStyle.Bold),
            (_chain,   FontStyle.Regular),
            (_input,   FontStyle.Regular),
            (_fresh,   FontStyle.Bold),
            (_verdict, FontStyle.Regular),
            (_banner,  FontStyle.Regular),
            (_bypass,  FontStyle.Regular),
            (_next,    FontStyle.Bold),
        })
        {
            lab.AutoSize = true;
            lab.Font = UiScale.Ui(style);
            lab.Margin = new Padding(0, 0, 0, UiScale.S(3));
            _stack.Controls.Add(lab);
        }

        // 进度条插在「正在算」那一行之后
        _bar.Height = UiScale.S(10);
        _bar.Width = UiScale.S(320);
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Margin = new Padding(0, 0, 0, UiScale.S(5));
        _bar.Visible = false;
        _stack.Controls.Add(_bar);
        _stack.Controls.SetChildIndex(_bar, _stack.Controls.IndexOf(_input) + 1);

        _banner.ForeColor = Color.FromArgb(150, 20, 20);
        _banner.BackColor = Color.FromArgb(255, 242, 242);
        _banner.Padding = new Padding(UiScale.S(6), UiScale.S(4), UiScale.S(6), UiScale.S(4));
        // 下一步：蓝底、可点。点它**只切页签 + 让那个按钮闪一下**，不执行 ——
        // ④ 会改输入、③ 要跑几十秒、⑤ 会写文件，代跑等于把判断从人手里拿走。
        _next.LinkColor = Color.FromArgb(20, 80, 170);
        _next.ActiveLinkColor = Color.FromArgb(20, 80, 170);
        _next.BackColor = Color.FromArgb(235, 244, 255);
        _next.Padding = new Padding(UiScale.S(6), UiScale.S(4), UiScale.S(6), UiScale.S(4));
        _next.LinkClicked += (_, _) =>
        {
            if (_nextCmd.Length > 0) NextStepRequested?.Invoke(_nextCmd);
        };

        _bypass.LinkColor = Color.FromArgb(150, 20, 20);
        _bypass.Text = "我知道风险，越关进入 →";
        _bypass.Visible = false;
        _bypass.LinkClicked += (_, _) => BypassRequested?.Invoke(_stage);

        // 换行宽度跟着面板走：窗口变窄时长句要折行，而不是被裁掉
        Resize += (_, _) => SyncWrapWidth();
        SyncWrapWidth();

        _state.Changed += () =>
        {
            if (IsHandleCreated) BeginInvoke(Refresh2);
            else Refresh2();
        };
        Refresh2();
    }

    private void SyncWrapWidth()
    {
        int w = Math.Max(UiScale.S(300), ClientSize.Width - Padding.Horizontal - UiScale.S(24));
        foreach (Control c in _stack.Controls) c.MaximumSize = new Size(w, 0);
    }

    /// <summary>切到了哪一格。</summary>
    public void SetStage(StageId s) { _stage = s; Refresh2(); }

    /// <summary>
    /// 「这条命令此刻用不用得了」——由宿主（MainForm）注入，本面板不自己判。
    /// 没注入时一律当可用（面板可以脱离 MainForm 单独构造，测试就这么用）。
    /// </summary>
    public Func<string, bool>? ApplicableProbe { get; set; }
    private bool Applicable(string id) => ApplicableProbe?.Invoke(id) ?? true;

    /// <summary>
    /// 去掉 Markdown 的 `**` 标记。
    ///
    /// ⚠ 这些文案是与判据 Note、页顶横幅**共用**的（一份文字四处显示），而那些地方走
    ///   TextFmt.Hook，`**` 会被渲染成粗体。Label 没有那一层 ⇒ 星号会**原样漏到界面上**。
    ///   本项目栽过这个（接线测试第 17 项专门守着「输出里没有残留的 Markdown 星号」）。
    /// </summary>
    private static string Plain(string s) => s.Replace("**", "");

    /// <summary>把状态重画一遍。名字不叫 Refresh —— 那是 Control 的方法。</summary>
    public void Refresh2()
    {
        var st = Flow.Stage(_stage);
        var gate = Gate.Evaluate(_stage, _state);

        _title.Text = "现在算的是：" + st.Title;

        // 链：名字 + 求解器入口 + 耗时。入口方法名直接印出来，
        // 让「我在算什么」有一个可以拿去 grep 的答案。
        var chains = st.Chains.Where(x => x != ChainId.无).Select(Flow.Chain).ToArray();
        _chain.Text = Plain(chains.Length == 0
            ? "链：—（不算东西：存档 / 出图）"
            : "链：" + string.Join("　", chains.Select(c => $"{c.Name}（{c.EntryPoint}·{c.Cost}）"))
                    + (chains.Any(c => c.Deliverable) ? "　★ 可交付" : "　⚠ 不可交付"));

        // 正在跑什么 —— 取各页已有的进度文字，不另起一套
        if (_state.Running is { } run)
        {
            _input.Text = $"正在算：{Flow.Chain(run).Name}"
                        + (_state.RunningNote.Length > 0 ? $"　{_state.RunningNote}" : "")
                        + "　（再点那个按钮 = 取消）";
            // 说得出百分比就画实条，说不出就走马灯 —— 别拿一根不动的空条冒充「有进度」
            if (_state.RunningPct >= 0)
            { _bar.Style = ProgressBarStyle.Continuous; _bar.Maximum = 100; _bar.Value = _state.RunningPct; }
            else
            { _bar.Style = ProgressBarStyle.Marquee; }
            _bar.Visible = true;
        }
        else
        {
            _input.Text = "输入来自：" + (InputNote.Length > 0 ? InputNote : "本页控件 + 左侧参数表");
            _bar.Visible = false;
        }

        // 结果新鲜度 —— 这一条是整块面板里最要紧的：
        // 「下面这些数是这组参数算出来的吗」
        // ★ 有链在跑时，下面这两行讲的都是**上一次**的解 —— 必须说出来（2026-08-24）。
        //   用户抓图里同时出现「正在算：C″ 形状搜索」和「✓ 已解（参数未变）」：
        //   读起来像是「算完了」，其实那是上一轮的结论，新的还在跑。
        //   同一块面板里两句话互相打架，就等于没说。
        string past = _state.Running is null ? "" : "上一次：";

        if (_state.Last is null)
            { _fresh.Text = _state.Running is null ? "结果：还没解过" : "结果：还没解过（正在算第一次）";
              _fresh.ForeColor = Color.DimGray; }
        else if (!_state.Fresh)
            { _fresh.Text = "⚠ 参数已改 —— 下面的数是上一次的"; _fresh.ForeColor = Color.FromArgb(170, 90, 0); }
        else if (!_state.Last.Converged)
            { _fresh.Text = "✗ 上次解未收敛 —— 下面每个数都不可引用"; _fresh.ForeColor = Color.FromArgb(150, 20, 20); }
        else
            { _fresh.Text = past + "✓ 已解（参数未变）"; _fresh.ForeColor = Color.FromArgb(20, 110, 40); }

        // 上次判定 —— 直接引用 LineResult 的单一来源访问器，不自己数
        if (_state.Last is { Ok: true } r)
        {
            var failed = r.Failed;
            _verdict.Text = failed.Length == 0
                ? past + "判定：✓ 判据全过"
                : past + $"判定：✗ {failed.Length} 条没过　" + string.Join("；", failed.Take(2))
                  + (failed.Length > 2 ? " …" : "");
            _verdict.ForeColor = failed.Length == 0
                ? Color.FromArgb(20, 110, 40) : Color.FromArgb(150, 20, 20);
        }
        else { _verdict.Text = ""; }

        // 门禁横幅：锁住时说清「为什么 / 现在什么状态 / 怎么解锁」
        if (!gate.Unlocked)
        {
            string now = gate.Blocking is { } b
                ? $"\r\n现在的状态：{b.Name} = {b.Actual:0.0} / 限 {b.Limit:0.0}"
                  + (b.Where.Length > 0 ? $"　位置：{b.Where}" : "")
                : "";
            _banner.Text = $"🔒 {st.Title} —— 还没解锁"
                         + $"\r\n为什么：{gate.Why}{now}"
                         + (gate.How.Length > 0 ? $"\r\n怎么解锁：{gate.How}" : "");
            _banner.Visible = true;

            _bypass.Visible = true;
        }
        else if (gate.Bypassed)
        {
            // 越关是**粘着的**：只要还在这一格，就一直说着。
            // 一个能被忘掉的例外，三个月后就成了没人记得来由的默认值。
            _banner.Text = "⚠ 越关中：本页是在门没开的情况下进来的。本页所有结果不可引用。";
            _banner.Visible = true;
            _bypass.Visible = false;
        }
        else { _banner.Visible = false; _bypass.Visible = false; }

        // ── 下一步：我现在该点哪个按钮（规则在 Flow.Next，只读现成状态）
        //   ⚠ **必须放在所有分支之外**：头一版插进了 `if (locked)` 里面，
        //     于是在已解锁的 ③ 上根本不执行 —— 界面上只剩一小块空蓝底。
        var ns = Flow.Next(_state, Applicable);

        // ★★★★★ 有链在跑时，Flow.Next 按设计**不给下一个命令**（UiWiring §28「别催」）——
        //   跑着的时候催人去点下一个按钮是错的。但「不催」不等于**什么都不说**：
        //   用户抓图里，几十分钟的搜形状跑着，而这一行是空的 ⇒
        //   指路链偏偏在最需要它的时候哑了。
        //   ⇒ 这一行改成说**现在能做什么**（只有两件事），并点名那个唯一的出口。
        //   这不违反「别催」：它不指向下一阶段，它描述当下。
        if (_state.Running is { } runNow)
        {
            _nextCmd = "";
            _next.Text = $"正在算：{Plain(Flow.Chain(runNow).Name)}"
                       + (_state.RunningNote.Length > 0 ? $"　{Plain(_state.RunningNote)}" : "")
                       + Environment.NewLine
                       + "　　现在只有两件事可做：**等它跑完**，或点那个已经变成「取消」的按钮。"
                         .Replace("**", "")
                       + Environment.NewLine
                       + "　　跑着的时候参数与页签都锁住了 —— 免得算完之后分不清这张表是哪组参数的。";
            _next.Visible = true;
        }
        else if (ns is null)
        {
            _nextCmd = "";
            _next.Visible = false;
        }
        else if (ns.CmdId.Length == 0)
        {
            // 有话要说、但**没有按钮可指**（例如 .3dm 模式下几何判据判不了，
            // 要改形状得回 Rhino 改图）。这时只给说明，不给一个点不动的链接。
            _nextCmd = "";
            _next.Text = "下一步 → " + Plain(ns.Why);
            _next.Visible = true;
        }
        else
        {
            var cmd = Flow.Cmd(ns.CmdId);
            _nextCmd = ns.CmdId;
            // 连「在哪一页、要多久」一起说 —— 光说按钮名，用户还得自己找
            _next.Text = $"下一步 → 点「{Plain(cmd.Text)}」"
                       + $"（{Plain(Flow.Stage(cmd.Stage).Title)}，{Plain(cmd.Cost)}）"
                       + Environment.NewLine + "　　" + Plain(ns.Why);
            _next.Visible = true;
        }

        // 内容高度变了就要来一次 —— 锁与不锁差三四行
        HeightWanted?.Invoke(_stack.PreferredSize.Height + Padding.Vertical + UiScale.S(10));
    }
}
