using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PtOptimize.UI;

/// <summary>
/// 把界面**逐页画成 PNG** —— `Pt_Optimize.exe --cli --uishot &lt;dir&gt;`。
///
/// ★ 为什么要有它（用户 2026-08-20：「APP 改完后，自己抓 APP 内『全部』的 UI 界面，
///   分析布局是否合理」）：
///
///   靠截屏做不到这件事。截屏只能拍到**当前那一页**，而界面里一多半页签
///   在任一时刻都是不可见的；而且截屏依赖窗口没被遮挡、分辨率、主题，
///   下次再拍就对不上，没法比较改动前后。
///
///   `DrawToBitmap` 是控件自己把自己画出来 ⇒ **确定性、可重复、能抓到当前不可见的页**，
///   而且不需要人在旁边点。这和本项目「判据要能自动跑」是同一条思路：
///   凡是要靠人记得去做的检查，迟早不会被做。
///
/// ⚠ 它只画**布局**，不代表功能对 —— 功能由 tests/UiWiring 的接线测试守着。
/// </summary>
public static class UiShot
{
    public static void CaptureAll(string dir)
    {
        Directory.CreateDirectory(dir);

        var form = new MainForm();
        // 必须真的 Show：不显示的话 TabPage 的子控件不会走完布局，
        // 画出来是一堆重叠在左上角的控件 —— 那种图看着像界面坏了，其实是没排版。
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        Pump(1500);

        var tabs = form.Tabs;
        int n = 0;
        var index = new List<string>();

        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            Pump(500);

            string name = Safe(page.Text);
            string file = Path.Combine(dir, $"{++n:00}_{name}.png");
            Shoot(form, file);
            index.Add($"{Path.GetFileName(file)}　←　页签「{page.Text}」");

            // R35（2026-09-11）：页里可滚动的面板（① 页的参数栏）默认只画得出第一屏 ——
            // 舌片厚／叉臂那些行在折线以下，「源码写了不等于布局给了」，所以逐屏往下滚再各来一张。
            int sk = 0;
            foreach (var sc in Descendants(page).OfType<ScrollableControl>().Where(c => c.AutoScroll && c.VerticalScroll.Visible && c.ClientSize.Height > 0))
            {
                int total = sc.DisplayRectangle.Height, view = sc.ClientSize.Height;
                for (int y = view; y < total; y += view)
                {
                    sc.AutoScrollPosition = new Point(0, y);
                    Pump(300);
                    string sfile = Path.Combine(dir, $"{n:00}s{++sk}_{name}_往下滚{sk}.png");
                    Shoot(form, sfile);
                    index.Add($"{Path.GetFileName(sfile)}　←　页签「{page.Text}」滚到第 {sk + 1} 屏");
                }
                sc.AutoScrollPosition = new Point(0, 0);
                Pump(200);
            }

            // 嵌套页签（② 快筛里的三张、③ 整线核算里的三张场图）也要各来一张 ——
            // 它们同样是「当前看不见」的那一类。
            foreach (var inner in Descendants(page).OfType<TabControl>())
            {
                foreach (TabPage sub in inner.TabPages)
                {
                    inner.SelectedTab = sub;
                    Pump(350);
                    string sfile = Path.Combine(dir, $"{n:00}{(char)('a' + inner.TabPages.IndexOf(sub))}_{name}_{Safe(sub.Text)}.png");
                    Shoot(form, sfile);
                    index.Add($"{Path.GetFileName(sfile)}　←　「{page.Text}」▸「{sub.Text}」");
                }
            }
        }

        // ★ R47 B（2026-09-13）：① 页**图纸模式**也要抓 —— 图纸路径的舌保温改用 ① 页那张逐片表，
        //   「源码写了不等于布局给了」，切到 Rhino .3dm 之后那张表与图纸段的说明行得目视。逐屏往下滚各来一张。
        {
            var rb3dm = Descendants(form).OfType<RadioButton>().FirstOrDefault(r => r.Text.Contains(".3dm", StringComparison.Ordinal));
            var inputPage = tabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text.Contains("输入", StringComparison.Ordinal));
            if (rb3dm is not null && inputPage is not null)
            {
                tabs.SelectedTab = inputPage; Pump(300);
                rb3dm.Checked = true; Pump(600);
                string name = Safe(inputPage.Text) + "_图纸模式";
                string file = Path.Combine(dir, $"01x_{name}.png");
                Shoot(form, file);
                index.Add($"{Path.GetFileName(file)}　←　页签「{inputPage.Text}」切到 Rhino .3dm（图纸模式）");
                int sk = 0;
                foreach (var sc in Descendants(inputPage).OfType<ScrollableControl>().Where(c => c.AutoScroll && c.VerticalScroll.Visible && c.ClientSize.Height > 0))
                {
                    int total = sc.DisplayRectangle.Height, view = sc.ClientSize.Height;
                    for (int y = view; y < total; y += view)
                    {
                        sc.AutoScrollPosition = new Point(0, y);
                        Pump(300);
                        string sfile = Path.Combine(dir, $"01xs{++sk}_{name}_往下滚{sk}.png");
                        Shoot(form, sfile);
                        index.Add($"{Path.GetFileName(sfile)}　←　页签「{inputPage.Text}」图纸模式滚到第 {sk + 1} 屏");
                    }
                    sc.AutoScrollPosition = new Point(0, 0);
                    Pump(200);
                }
                var rbAn = Descendants(form).OfType<RadioButton>().FirstOrDefault(r => r.Text.Contains("解析", StringComparison.Ordinal));
                if (rbAn is not null) { rbAn.Checked = true; Pump(300); }
            }
        }

        // ★★★★★ 2026-09-18，Opus 5：**牌号下拉也要抓**（数据不全的牌号灰显不可选，用户 2026-09-16）。
        //
        //   下拉是**弹出窗口**，不是窗体里的控件 —— Form.DrawToBitmap 抓不到它。
        //   抓不到就等于没抓：这一版改的正是那张列表长什么样（哪几行灰、行末写什么），
        //   而「源码写了不等于布局给了」是本项目抓 UI 的全部理由。
        //   ⇒ 把**生产那份工厂**（GradeNameEditor.BuildList）造出来的控件放进一个临时窗体单独画一张。
        //   ⚠ 画的是生产控件本身，不是仿造的示意图；灰不灰由 GradeChoices 判，这里一个字都不判。
        {
            using var host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                Text = "参数表「铂材牌号」下拉",
                ShowInTaskbar = false,
            };
            var gpanel = GradeNameEditor.BuildList(PtOptimize.Core.GradeChoices.DefaultGrade, _ => { });
            host.ClientSize = new Size(gpanel.Width, gpanel.Height);
            gpanel.Dock = DockStyle.Fill;
            host.Controls.Add(gpanel);
            host.Show();
            Pump(600);
            string gfile = Path.Combine(dir, "90_牌号下拉_数据不全的灰显不可选.png");
            Shoot(host, gfile);
            index.Add($"{Path.GetFileName(gfile)}　←　参数表「铂材牌号」下拉（全集照列；灰行 = 数据不全、不可选，行末写明缺哪几类）");
            host.Close();
        }

        // R35（用户 2026-09-11「所有 Excel 表格物件都检查一遍，不要有字体被挡住」）：窗体是真 Show 出来的，
        //   这里量的是真像素 —— 每张表的表头高与每一行行高都得装得下自己的字。走查里窗体没 Show，量不到这个。
        int grids = 0, bad = 0;
        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page; Pump(200);
            foreach (var g in Descendants(page).OfType<DataGridView>())
            {
                grids++;
                int need = GridFmt.HeaderNeedPx(g);
                var probs = new List<string>();
                if (g.ColumnHeadersHeight < need) probs.Add($"表头 {g.ColumnHeadersHeight} px < 字需 {need} px");
                foreach (DataGridViewRow r in g.Rows) if (r.Visible && r.Height < need) { probs.Add($"行 {r.Index} 高 {r.Height} px < 字需 {need} px"); break; }
                if (probs.Count > 0) { bad++; index.Add($"⚠ 表格字被裁　「{page.Text}」▸ {(string.IsNullOrEmpty(g.Name) ? "（未命名表）" : g.Name)}：{string.Join("；", probs)}"); }
            }
        }
        index.Add(bad == 0 ? $"表格自检：{grids} 张表，表头与行高都装得下字" : $"表格自检：{grids} 张表，{bad} 张字被裁（见上面 ⚠）");
        if (bad > 0) Environment.ExitCode = 3;

        File.WriteAllText(Path.Combine(dir, "索引.txt"),
            string.Join(Environment.NewLine, index), new System.Text.UTF8Encoding(false));

        form.Close();
        Console.WriteLine($"界面截图已输出到 {dir}（{index.Count} 张）");
        foreach (var l in index) Console.WriteLine("  " + l);
    }

    private static void Shoot(Form f, string path)
    {
        using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(path, ImageFormat.Png);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var k in Descendants(c)) yield return k;
        }
    }

    private static void Pump(int ms)
    {
        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalMilliseconds < ms)
        { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }

    /// <summary>页签名直接当文件名会带上 ①🔒★ 之类，挑掉不能进文件名的。</summary>
    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Where(c => !bad.Contains(c) && c != ' ').ToArray());
        return t.Length == 0 ? "页" : t;
    }
}
