using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Design;

using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// ★★★★★ 参数表「铂材牌号」的下拉：**全集照列，数据不全的灰显不可选**（2026-09-18，Opus 5）。
///
/// 用户 2026-09-16：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，
/// 只用数据全的铂金合金」。
///
/// ══ 为什么不能只靠 <see cref="GradeNameConverter"/>
///
/// PropertyGrid 自带的那个下拉只会把 <c>GetStandardValues</c> 给的名字**平铺**出来 ——
/// 它画不出灰行，也没有地方写「为什么不能选」。把不齐全的牌号直接从名单里拿掉，
/// 界面上就只剩「我要的那个牌号不见了」，工程师无从知道是**数据缺**还是**程序漏**。
/// ⇒ 自己画一份：全集照列、不齐全的灰、行末直接写缺哪几类、悬停给全文。
///
/// ⚠ 名单与灰不灰**一个字都不在这里判**：全部读 <see cref="GradeChoices.All"/>
///   （它读 <see cref="MaterialDb.DataCompleteness"/>）。这里只负责画和挡点击。
/// ⚠ 灰行**点了没反应**（不关下拉、不改值），并把理由写在下拉底部那一行 ——
///   点了就悄悄关掉会被读成「选上了」。
/// </summary>
public sealed class GradeNameEditor : System.Drawing.Design.UITypeEditor
{
    public override System.Drawing.Design.UITypeEditorEditStyle GetEditStyle(System.ComponentModel.ITypeDescriptorContext? context)
        => System.Drawing.Design.UITypeEditorEditStyle.DropDown;

    public override object? EditValue(System.ComponentModel.ITypeDescriptorContext? context,
                                      IServiceProvider? provider, object? value)
    {
        if (provider?.GetService(typeof(IWindowsFormsEditorService)) is not IWindowsFormsEditorService svc)
            return value;

        object? picked = value;
        var panel = BuildList(value as string, name => { picked = name; svc.CloseDropDown(); });
        svc.DropDownControl(panel);
        return picked;
    }

    /// <summary>
    /// 造出下拉里那块控件（说明行 + 牌号表 + 理由行）。
    /// **抽成公开工厂**是为了让 `--cli --uishot` 能把它单独画成 PNG ——
    /// 下拉是弹出窗口，<c>Form.DrawToBitmap</c> 抓不到它；抓不到的布局等于没抓（本项目「改 UI 必须自己抓图」）。
    /// </summary>
    /// <param name="current">当前值（高亮它）。</param>
    /// <param name="onPick">选中一个**可选**牌号时回调；灰行不会触发。</param>
    public static Panel BuildList(string? current, Action<string> onPick)
    {
        var choices = GradeChoices.All();

        var legend = new Label
        {
            Dock = DockStyle.Top,
            Text = GradeChoices.Legend,
            AutoSize = false,
            Height = UiScale.S(32),
            Padding = new Padding(4, 3, 4, 3),
            ForeColor = SystemColors.GrayText,
            BackColor = SystemColors.Info,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var why = new Label
        {
            Dock = DockStyle.Bottom,
            Text = "",
            AutoSize = false,
            Height = UiScale.S(74),   // 整句写在这里（行上只写缺哪几类）—— 留够高度，宁可空一行也不许把话裁掉
            Padding = new Padding(4, 3, 4, 3),
            ForeColor = SystemColors.GrayText,
            BackColor = SystemColors.Info,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            ItemHeight = Math.Max(18, UiScale.S(20)),
        };
        foreach (var c in choices) list.Items.Add(c);

        int cur = Array.FindIndex(choices, c => string.Equals(c.Name, current, StringComparison.Ordinal));
        if (cur >= 0) list.SelectedIndex = cur;

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= choices.Length) return;
            var c = choices[e.Index];
            bool sel = c.Selectable && (e.State & DrawItemState.Selected) != 0;
            var back = sel ? SystemColors.Highlight : SystemColors.Window;
            var fore = !c.Selectable ? SystemColors.GrayText
                     : sel ? SystemColors.HighlightText : SystemColors.WindowText;
            using var b = new SolidBrush(back);
            e.Graphics.FillRectangle(b, e.Bounds);
            using var f = new SolidBrush(fore);
            e.Graphics.DrawString(c.RowText, e.Font ?? SystemFonts.DefaultFont, f, e.Bounds.Left + 2, e.Bounds.Top + 1);
            if (!c.Selectable)   // 灰行再划一道细线：只靠颜色的区分，投影仪与色弱都吃不准
                e.Graphics.DrawLine(SystemPens.GrayText, e.Bounds.Left + 2, e.Bounds.Top + e.Bounds.Height / 2,
                                    e.Bounds.Left + 2 + (int)e.Graphics.MeasureString(c.Name, e.Font ?? SystemFonts.DefaultFont).Width,
                                    e.Bounds.Top + e.Bounds.Height / 2);
        };

        void Explain(int i)
        {
            if (i < 0 || i >= choices.Length) { why.Text = ""; return; }
            var c = choices[i];
            why.Text = c.Selectable
                ? $"「{c.Name}」四类数据齐全，可选。" + (c.Borrowed.Length > 0 ? "　同名义成分借用：" + string.Join("；", c.Borrowed) : "")
                : $"「{c.Name}」不能选 —— 缺：" + string.Join("；", c.Missing);
        }

        var tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300, ReshowDelay = 100 };
        int lastTip = -1;
        list.MouseMove += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (i == lastTip) return;
            lastTip = i;
            Explain(i);
            tip.SetToolTip(list, i >= 0 && i < choices.Length ? choices[i].Tip : "");
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i >= 0 && i < choices.Length && !choices[i].Selectable)
            {   // 键盘上下键也会走到灰行：允许「停」在上面看理由，但不许把它当成选中
                Explain(i);
                return;
            }
            Explain(i);
        };
        list.MouseDown += (_, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (i < 0 || i >= choices.Length) return;
            Explain(i);
            if (choices[i].Selectable) onPick(choices[i].Name);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            int i = list.SelectedIndex;
            if (i >= 0 && i < choices.Length && choices[i].Selectable) onPick(choices[i].Name);
        };

        var panel = new Panel
        {
            Width = UiScale.S(560),
            Height = legend.Height + why.Height + list.ItemHeight * choices.Length + 6,
        };
        panel.Controls.Add(list);
        panel.Controls.Add(legend);
        panel.Controls.Add(why);
        Explain(cur);
        return panel;
    }
}
