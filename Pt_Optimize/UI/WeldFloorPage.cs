using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// ★ R39（2026-09-11，用户：「焊接屈曲下界作为判据行做一个独立于系统外的计算小工具」）：
/// 「参考工具 ▸ 焊接下界小算盘」—— 独立于求解链之外，改一个数当场重算，不读页面控件、不写回任何地方。
/// 计算全在 <see cref="WeldFloorCalc"/>（与求解链的下角 <see cref="DesignSpec.DiscFloorMm"/> 同一口径，测试钉着）。
/// </summary>
public sealed class WeldFloorPage : UserControl
{
    private readonly NumericUpDown _dia, _id, _wall, _sf, _burn, _t0, _beta, _eta, _kb;
    private readonly TextBox _explain = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(252, 252, 250), Font = UiScale.Mono(),
    };
    private readonly DataGridView _series = GridFmt.NewGrid();
    private readonly Label _headline = new() { AutoSize = true, Font = UiScale.Ui(FontStyle.Bold), ForeColor = Color.FromArgb(20, 60, 140) };

    public WeldFloorPage()
    {
        Dock = DockStyle.Fill;
        Font = UiScale.Ui();
        _series.Name = "焊接下界盘径系列";

        NumericUpDown Num(decimal v, decimal lo, decimal hi, decimal step, int dec)
        {
            var n = new NumericUpDown
            {
                Minimum = lo, Maximum = hi, Increment = step, DecimalPlaces = dec, Value = v,
                Width = UiScale.S(90), Font = UiScale.Ui(),
            };
            n.ValueChanged += (_, _) => Recalc();
            return n;
        }
        var dflt = new DesignInputs();
        _dia  = Num(60, 30, 400, 1, 0);
        _id   = Num(50, 10, 200, 1, 0);
        _wall = Num(0.8m, 0.3m, 5, 0.1m, 2);
        _sf   = Num((decimal)dflt.WeldSafetyFactor, 1, 5, 0.1m, 1);
        _burn = Num((decimal)dflt.WeldMinThicknessMm, 0.1m, 5, 0.05m, 2);
        _t0   = Num(20, -20, 200, 5, 0);
        _beta = Num((decimal)WeldDistortion.BeadWidthRatio, 1, 5, 0.1m, 1);
        _eta  = Num((decimal)WeldDistortion.MeltEfficiency, 0.1m, 1, 0.05m, 2);
        _kb   = Num((decimal)WeldDistortion.PlateBucklingKFreeEdge, 0.1m, 10, 0.01m, 2);

        var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(UiScale.S(6)) };
        void Row(string label, Control c, string tip)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
            new ToolTip().SetToolTip(l, tip); new ToolTip().SetToolTip(c, tip);
            grid.Controls.Add(l); grid.Controls.Add(c);
        }
        Row("盘直径 mm", _dia, "圆盘外径。无支撑宽度 = 盘半径 − 管孔半径，盘越大这条带越宽、越容易翘。");
        Row("安全系数", _sf, "乘在屈曲下界上（参数表「焊接下界安全系数」，预设 2）。");
        Row("管内径 mm", _id, "管孔半径 = 管内径/2 + 管壁。");
        Row("烧穿下界 mm", _burn, "现场焊法给的硬底（手工 TIG 0.6），算不出来 —— 换焊法就改这一格。");
        Row("管壁 mm", _wall, "");
        Row("起始温度 °C", _t0, "焊前板温；只影响熔点温升那一项，很不敏感。");
        Row("焊道宽/板厚 β", _beta, "自熔对接焊常规 1.5–3。");
        Row("熔化效率 η", _eta, "电弧焊常规 0.3–0.5。");
        Row("板屈曲系数 k_b", _kb, "法兰盘内边焊死、外边自由 ⇒ 0.43（三边简支一边自由长板）；四边简支长板 4.0 只作对照，差 9.3 倍。");
        grid.Controls.Add(new Label { Text = "", AutoSize = true }); grid.Controls.Add(new Label { Text = "", AutoSize = true });

        var intro = new Label
        {
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(UiScale.S(6)),
            BackColor = Color.FromArgb(255, 250, 220), MaximumSize = new Size(UiScale.S(1100), 0),
            Text = "这是一张独立的算盘，不进求解链、不改页面上的任何数。它算的是「板薄到多少焊完会翘、焊法最薄能焊到多少」，两条取大就是圆盘板厚的工艺下界 —— " +
                   "求解器起步用的就是同一个数（约束盒的下角），所以判据表里不再单列一行。改任何一格当场重算；下面的表按盘径给一串，看从哪一档起由屈曲接管。",
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = System.Windows.Forms.Orientation.Horizontal };
        split.Panel1.Controls.Add(_explain);
        split.Panel2.Controls.Add(_series);
        _series.Dock = DockStyle.Fill;

        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        _headline.Dock = DockStyle.Top; _headline.Padding = new Padding(UiScale.S(6), UiScale.S(2), 0, UiScale.S(2));
        top.Controls.Add(grid); top.Controls.Add(_headline); top.Controls.Add(intro);
        // Dock=Top 的堆叠顺序：后加的在上 ⇒ intro 最上、headline 其次、grid 在下
        Controls.Add(split);
        Controls.Add(top);
        HandleCreated += (_, _) => { split.SplitterDistance = Math.Max(60, split.Height * 45 / 100); };
        _series.HandleCreated += (_, _) => _series.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
        _explain.TabStop = false;
        Recalc();
    }

    internal WeldFloorCalc.Inputs ReadInputs() => new()
    {
        DiscDiaMm = (double)_dia.Value, TubeIdMm = (double)_id.Value, WallMm = (double)_wall.Value,
        SafetyFactor = (double)_sf.Value, BurnThroughMm = (double)_burn.Value, StartTempC = (double)_t0.Value,
        Beta = (double)_beta.Value, EtaMelt = (double)_eta.Value, Kb = (double)_kb.Value,
    };

    private void Recalc()
    {
        var i = ReadInputs();
        var o = WeldFloorCalc.Compute(i);
        _headline.Text = $"圆盘板厚工艺下界 {o.FloorMm:0.00} mm（由{o.Governing}控制）　屈曲 {o.BucklingWithSfMm:0.00} mm（含安全系数）／烧穿 {o.BurnThroughMm:0.00} mm";
        _explain.Text = o.Explain.Replace("\n", Environment.NewLine);
        _explain.Select(0, 0);   // 写完文字别全选高亮（抓图抓到一片蓝）

        var dias = new[] { 50.0, 56, 60, 66, 72, 80, 90, 100, 120, 150 }.Where(v => v > 2 * o.HoleRadiusMm + 1).ToArray();
        var rows = WeldFloorCalc.Series(i, dias);
        string tsv = "盘直径 mm\t无支撑宽度 mm\t屈曲下界 mm\t含安全系数 mm\t烧穿下界 mm\t取大 mm\t控制\n" +
                     string.Join("\n", rows.Select(r =>
                         $"{r.discDiaMm:0}\t{r.o.WidthMm:0.0}\t{r.o.BucklingRawMm:0.000}\t{r.o.BucklingWithSfMm:0.000}\t{r.o.BurnThroughMm:0.00}\t{r.o.FloorMm:0.000}\t{r.o.Governing}"));
        GridFmt.Fill(_series, tsv);
        if (_series.IsHandleCreated) _series.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);   // 表头「盘直径 mm」也要装得下
    }
}
