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
