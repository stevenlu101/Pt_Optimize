using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PtOptimize.UI;

/// <summary>
/// 把 TSV 文本灌进**真表格控件**（DataGridView）。
///
/// ★ 用户 2026-08-20：「能用 Excel 格式表示就用」「Excel 格式要有自动合适的格宽与格高」。
///
/// 为什么不继续用文本框 + 制表位：那套是**模拟**表格 —— 没有列宽自适应、没有行高、
/// 长内容只能折行，而且出问题时极难定位（② 页那张表就错位了，查了半天没锁定真因）。
/// DataGridView 的 AutoSizeColumnsMode / AutoSizeRowsMode 是控件原生能力，
/// 列宽行高自己算，不需要任何人去量像素。
///
/// 判据表两轮前已经这么改过并且一直好用 —— 本类是把那次的做法收成公用的一份。
/// </summary>
internal static class GridFmt
{
    public static DataGridView NewGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditProgrammatically,
            BackgroundColor = Color.FromArgb(252, 252, 250),
            BorderStyle = BorderStyle.None,
            Font = UiScale.Ui(),
            // ★ 这两行就是「自动合适的格宽与格高」——
            //   列宽按内容（含表头）算，行高按内容算，长文字会把行撑高而不是被裁掉。
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };
        g.DefaultCellStyle.Font = UiScale.Ui();
        g.ColumnHeadersDefaultCellStyle.Font = UiScale.Ui(FontStyle.Bold);
        g.DefaultCellStyle.Padding = new Padding(UiScale.S(4), UiScale.S(2), UiScale.S(4), UiScale.S(2));
        return g;
    }

    /// <summary>
    /// 灌数据。<paramref name="tsv"/> 第一行是表头，其余是数据行，一格一个 Tab。
    /// 纯横线行（TextFmt.SepRow 那种）会被跳过 —— 真表格自己有网格线，不需要画横线。
    /// </summary>
    public static void Fill(DataGridView g, string tsv)
    {
        g.SuspendLayout();
        g.Columns.Clear();
        g.Rows.Clear();

        var lines = tsv.Replace("\r\n", "\n").Split('\n')
                       .Where(l => l.Contains('\t'))
                       .Where(l => !IsRule(l))
                       .ToArray();
        if (lines.Length == 0) { g.ResumeLayout(); return; }

        var head = lines[0].Split('\t');
        foreach (var h in head)
            g.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = Plain(h),
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split('\t');
            // 列数对不上不许静默吃掉：宁可多一列空的，也不要**悄悄丢一格数据**
            var row = new string[g.Columns.Count];
            for (int i = 0; i < row.Length; i++) row[i] = i < cells.Length ? Plain(cells[i]) : "";
            g.Rows.Add(row);
        }

        // 数字列右对齐 —— 小数点对齐是这张表存在的意义之一
        for (int c = 0; c < g.Columns.Count; c++)
        {
            bool any = false, all = true;
            foreach (DataGridViewRow r in g.Rows)
            {
                string t = (r.Cells[c].Value as string ?? "").Trim();
                if (t.Length == 0 || t is "—" or "–") continue;
                if (double.TryParse(t, out _)) any = true; else { all = false; break; }
            }
            if (all && any)
                g.Columns[c].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        g.ResumeLayout();
        g.ClearSelection();
    }

    private static bool IsRule(string s)
    {
        string t = s.Replace("\t", "").Trim();
        return t.Length >= 3 && t.All(c => c is '─' or '═' or '-' or '=');
    }

    /// <summary>DataGridView 不走 TextFmt.Hook ⇒ `**` 会原样显示出来。</summary>
    private static string Plain(string s) => s.Replace("**", "").Trim();
}
