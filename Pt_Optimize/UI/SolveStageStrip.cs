using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// ★ 解法阶段条（用户 2026-09-09 晚：「跑的过程把①②③④⑤的状态显示出来，让工程师知道 APP 正在干啥」）。
/// 五个格子：做过的灰勾、正在做的蓝底加粗、还没到的浅灰；右边一行细节（当前轮数／网格档／停因）。
/// 认阶段的规则在 Core（<see cref="SolveStages"/>），与命令行同一份。
/// </summary>
internal sealed class SolveStageStrip : FlowLayoutPanel
{
    private readonly Label[] _cells;
    private readonly Label _detail = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(UiScale.S(10), UiScale.S(6), 0, 0) };
    private SolveStages.Stage _current = SolveStages.Stage.None;
    private bool _finished;

    /// <summary>现在所在的阶段（走查与测试读它）。</summary>
    internal SolveStages.Stage Current => _current;
    internal string DetailText => _detail.Text;

    public SolveStageStrip()
    {
        Dock = DockStyle.Top; AutoSize = true; WrapContents = true;
        Padding = new Padding(UiScale.S(6), UiScale.S(3), UiScale.S(6), UiScale.S(3));
        BackColor = Color.FromArgb(247, 247, 247);
        var head = new Label { Text = "APP 正在做：", AutoSize = true, Font = UiScale.Ui(FontStyle.Bold), Margin = new Padding(0, UiScale.S(6), UiScale.S(4), 0) };
        Controls.Add(head);
        _cells = SolveStages.All.Select(a =>
        {
            var l = new Label
            {
                Text = a.Title, AutoSize = true, Margin = new Padding(0, UiScale.S(3), UiScale.S(4), 0),
                Padding = new Padding(UiScale.S(6), UiScale.S(3), UiScale.S(6), UiScale.S(3)),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var tip = new ToolTip(); tip.SetToolTip(l, a.What);
            Controls.Add(l);
            return l;
        }).ToArray();
        Controls.Add(_detail);
        _detail.Text = "还没跑：点「核算整线」后从第一步开始，这一行跟着报每一步";   // 审排版：未解算时右侧别空着（别写 ①，走查按带圈数字数格子）
        Paint();
    }

    /// <summary>一次新的解开始：全部回到「还没到」。</summary>
    internal void Reset(string what)
    {
        _current = SolveStages.Stage.None; _finished = false;
        _detail.Text = what;
        Paint();
    }

    /// <summary>每一行进度都喂进来；认得出就换阶段，认不出只更新细节。</summary>
    internal void Track(string line)
    {
        if (_finished || string.IsNullOrWhiteSpace(line)) return;
        var st = SolveStages.Of(line, _current);
        bool changed = st != _current;
        _current = st;
        _detail.Text = SolveStages.Detail(line.Split('\n')[0]);
        if (changed) Paint();
    }

    /// <summary>解结束：全过就打「✓ 算完」，没过就停在当前格并写停因。</summary>
    internal void Finish(bool ok, string why)
    {
        _finished = true;
        if (ok) _current = SolveStages.Stage.Done;
        _detail.Text = ok ? "✓ " + why : "■ 停在这里：" + why;
        Paint();
    }

    private void Paint()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            var id = SolveStages.All[i].Id;
            bool done = _current == SolveStages.Stage.Done || (int)id < (int)_current;
            bool now = id == _current;
            _cells[i].Font = now ? UiScale.Ui(FontStyle.Bold) : UiScale.Ui(FontStyle.Regular);
            _cells[i].BackColor = now ? Color.FromArgb(214, 232, 255) : done ? Color.FromArgb(230, 245, 230) : Color.White;
            _cells[i].ForeColor = now ? Color.FromArgb(0, 60, 140) : done ? Color.FromArgb(40, 110, 40) : Color.Gray;
            _cells[i].Text = (done ? "✓ " : "") + SolveStages.All[i].Title;
        }
        _detail.ForeColor = _finished && _current != SolveStages.Stage.Done ? Color.Firebrick : Color.DimGray;
    }
}
