using System.Text;
using Microsoft.Web.WebView2.WinForms;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// 使用说明页：WebView2 渲染的**图文**说明。
///
/// ★ 图不是画好的图片，是**从 <see cref="FinalDesign"/> 实时生成的 SVG**。
///   理由和整个项目的其余部分一样：图片一旦静态化，就成了「同一个数存两处」——
///   定案值一改，图还留在旧构型上，而它看起来完全正常（HANDOVER §1.8 最常见的失效）。
///   现在切换定案档，图随之重画，两者不可能漂开。
///
/// WebView2 未装运行时的机器不会崩：本页给出提示并指向 docs/APP使用说明书.md，
/// 其余功能不受影响。
/// </summary>
public sealed class ManualPage : TabPage
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly ToolStripComboBox _caseBox =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill, Visible = false, Padding = new Padding(24),
        Font = new Font("Microsoft YaHei UI", 10f)
    };
    private string _tempDir = "";

    public ManualPage() : base("使用说明")
    {
        Padding = new Padding(2);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tool.Items.Add(new ToolStripLabel("图按定案档实时生成"));
        tool.Items.Add(_caseBox);
        foreach (var fd in FinalDesign.All) _caseBox.Items.Add(fd.Name);
        _caseBox.SelectedIndex = Math.Max(0, Array.IndexOf(FinalDesign.All, FinalDesign.Current));
        _caseBox.SelectedIndexChanged += (_, _) => Render();

        var open = new ToolStripButton("打开 Markdown 版")
        { DisplayStyle = ToolStripItemDisplayStyle.Text };
        open.Click += (_, _) => OpenMarkdown();
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(open);

        Controls.Add(_fallback);
        Controls.Add(_web);
        Controls.Add(tool);

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            Render();
        }
        catch (Exception ex)
        {
            // 运行时缺失是**可预期**的情况（未装 Evergreen WebView2 Runtime），
            // 不是故障 ⇒ 给出可执行的下一步，而不是弹异常。
            _web.Visible = false;
            _fallback.Visible = true;
            _fallback.Text =
                "本页需要 Microsoft Edge WebView2 运行时，本机未检测到。\r\n\r\n" +
                "说明书同时以 Markdown 存在：docs\\APP使用说明书.md（内容一致，只是没有图）。\r\n" +
                "装上 WebView2 Runtime 后重开本程序即可看图文版。\r\n\r\n" +
                "详细信息：" + ex.Message;
        }
    }

    private void OpenMarkdown()
    {
        string md = FindDoc("APP使用说明书.md");
        if (md.Length == 0)
        { MessageBox.Show(this, "找不到 docs\\APP使用说明书.md", "打开失败"); return; }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(md) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
    }

    private static string FindDoc(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "docs", name);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    private void Render()
    {
        if (_web.CoreWebView2 == null) return;
        int i = _caseBox.SelectedIndex;
        var fd = FinalDesign.All[Math.Clamp(i, 0, FinalDesign.All.Length - 1)];

        if (_tempDir.Length == 0)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Pt_Optimize_manual");
            Directory.CreateDirectory(_tempDir);
        }
        string html = Path.Combine(_tempDir, "manual.html");
        File.WriteAllText(html, BuildHtml(fd), new UTF8Encoding(false));
        _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
    }

    // ════════════════════════════════════════════════════════════════════
    //  SVG：全部按 FinalDesign 的实际尺寸画，标注也取自它
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 界面地图：主窗口分几块、五个页签各是什么。
    ///
    /// ⚠ 这张图是**手写的示意**，不像其余几张那样由 FinalDesign 生成 ——
    ///   它画的是界面结构，而界面结构在代码里（MainForm 的 SplitContainer 与 TabPages）。
    ///   ⇒ 改了页签或工具条，**必须回来改这里**。图与界面漂开时没有任何东西会报错。
    ///   （已知同源：MainForm.cs 的 TabPages 顺序、LineDesignPage/AnalysisPage 的工具条。）
    /// </summary>
    private static string SvgUi()
    {
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 620 376\" width=\"100%\" style=\"max-width:620px\">");

        string Box(double x, double y, double w, double h, string fill) =>
            $"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{w:0.#}\" height=\"{h:0.#}\" rx=\"3\" " +
            $"fill=\"{fill}\" stroke=\"var(--rule)\" stroke-width=\"1\"/>";
        string Txt(double x, double y, string t, string cls = "lbl", string an = "start") =>
            $"<text x=\"{x:0.#}\" y=\"{y:0.#}\" text-anchor=\"{an}\" class=\"{cls}\">{t}</text>";
        string Badge(double x, double y, string n) =>
            $"<circle cx=\"{x:0.#}\" cy=\"{y:0.#}\" r=\"10\" fill=\"var(--clamp)\"/>" +
            $"<text x=\"{x:0.#}\" y=\"{y + 4.5:0.#}\" text-anchor=\"middle\" " +
            $"style=\"font:bold 12px sans-serif;fill:#FFF\">{n}</text>";

        sb.Append(Box(10, 10, 600, 356, "var(--bg)"));
        // 标题栏
        sb.Append(Box(10, 10, 600, 24, "var(--card)"));
        sb.Append(Txt(20, 26, "Pt_Optimize — 铂金直接加热 整线设计与用量优化", "lbl dim"));
        // 主工具条
        sb.Append(Box(10, 34, 600, 27, "var(--card)"));
        double bx = 18;
        foreach (var s in new[] { "计算 (F5)", "│", "扫描：保温厚度", "扫描：法兰厚度",
                                  "扫描：铂发射率", "│", "保存", "读取", "导出 CSV" })
        { sb.Append(Txt(bx, 52, s, "lbl")); bx += s == "│" ? 12 : s.Length * 12.4 + 12; }
        sb.Append(Badge(596, 47, "④"));

        // 左：参数表
        sb.Append(Box(15, 66, 194, 294, "var(--card)"));
        sb.Append(Txt(25, 86, "参数表", "lbl"));
        sb.Append(Txt(25, 103, "DesignInputs，分类折叠", "lbl dim"));
        for (int i = 0; i < 7; i++)
        {
            sb.Append($"<line x1=\"25\" y1=\"{122 + i * 22}\" x2=\"110\" y2=\"{122 + i * 22}\" " +
                      "stroke=\"var(--rule)\" stroke-width=\"6\" stroke-linecap=\"round\"/>");
            sb.Append($"<line x1=\"124\" y1=\"{122 + i * 22}\" x2=\"196\" y2=\"{122 + i * 22}\" " +
                      "stroke=\"var(--dim)\" stroke-width=\"6\" stroke-linecap=\"round\" stroke-opacity=\"0.45\"/>");
        }
        sb.Append(Badge(28, 296, "①"));

        // 右上：报告
        sb.Append(Box(215, 66, 380, 122, "var(--card)"));
        sb.Append(Txt(227, 86, "报告（单段解，随「计算 (F5)」刷新）", "lbl"));
        for (int i = 0; i < 5; i++)
            sb.Append($"<line x1=\"227\" y1=\"{104 + i * 16}\" x2=\"{300 + (i * 61) % 250}\" " +
                      $"y2=\"{104 + i * 16}\" stroke=\"var(--rule)\" stroke-width=\"5\" stroke-linecap=\"round\"/>");
        sb.Append(Badge(578, 175, "②"));

        // 右下：页签区
        sb.Append(Box(215, 194, 380, 166, "var(--card)"));
        double tx = 219;
        foreach (var (t, on) in new (string, bool)[]
                 { ("整线设计", true), ("分析", false), ("分段核算", false),
                   ("轴向剖面", false), ("使用说明", false) })
        {
            double w = t.Length * 13.2 + 14;
            sb.Append($"<rect x=\"{tx:0.#}\" y=\"198\" width=\"{w:0.#}\" height=\"23\" rx=\"3\" " +
                      $"fill=\"{(on ? "var(--clamp)" : "var(--bg)")}\" stroke=\"var(--rule)\"/>");
            sb.Append($"<text x=\"{tx + w / 2:0.#}\" y=\"214\" text-anchor=\"middle\" class=\"lbl\"" +
                      (on ? " style=\"fill:#FFF\"" : "") + $">{t}</text>");
            tx += w + 4;
        }
        sb.Append(Badge(578, 210, "③"));
        // 页签内部：自己的工具条 + 输出 + 图
        sb.Append(Box(222, 228, 366, 24, "var(--bg)"));
        bx = 230;
        // ⚠ 这条内工具条只画得下几个 —— 全表在图下方。
        //   按 11.6 px/字 排完必须落在面板右边界 588 之内，加项前先算一遍：
        //   多出去的不会被裁掉，会直接画到 viewBox 外，图上看不见但确实丢了。
        foreach (var s in new[] { "核算整线", "自动定厚", "│",
                                  "定案档▾", "载入定案", "导出定案 3DM", "…" })
        { sb.Append(Txt(bx, 245, s, "lbl dim")); bx += s == "│" ? 10 : s.Length * 11.6 + 10; }
        sb.Append(Box(222, 258, 366, 46, "var(--bg)"));
        sb.Append(Txt(232, 275, "判据表 + 收敛信息（文本）", "lbl dim"));
        sb.Append(Box(222, 310, 366, 44, "var(--bg)"));
        sb.Append(Txt(232, 327, "法兰温度场 / 法兰电流密度场 / 管轴向剖面", "lbl dim"));
        sb.Append(Badge(578, 332, "⑤"));

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>法兰平面图（板面 = XZ 平面，与 3DM 的方位约定一致）。</summary>
    private static string SvgPlate(FinalDesign fd)
    {
        double R = fd.DiscRadiusMm, h = fd.HoleRadiusMm, w = fd.TabHalfWidthMm,
               L = fd.TabLengthMm, fr = fd.TabFilletMm;
        double r1 = fd.RingRadiiMm[0], r2 = fd.RingRadiiMm[1];
        // 视图：x ∈ [−L−8, R+8]，z ∈ [−R−8, R+8]
        double x0 = -L - 8, x1 = R + 8, z0 = -R - 8, z1 = R + 8;
        double s = 620.0 / (x1 - x0);
        double H = (z1 - z0) * s;
        // 画布坐标：X = (x − x0)·s，Y = (z1 − z)·s   （z 向上）
        string PX(double x) => ((x - x0) * s).ToString("0.0");
        string PY(double z) => ((z1 - z) * s).ToString("0.0");

        // 舌根圆角：圆心 (xc, ±(w+fr))，与盘圆外切、与舌边相切
        double xc = -Math.Sqrt((R + fr) * (R + fr) - (w + fr) * (w + fr));
        double k = R / (R + fr);
        double tux = xc * k, tuz = (w + fr) * k;      // 与盘圆的切点（上）

        // 轮廓路径只算一次：先填色，画完环之后**再描一次线**——
        // 否则环外级半径 (孔+6) 大于盘半径时会把盘缘整个盖住，图上看不出盘在哪。
        string outline =
            $"M {PX(tux)},{PY(-tuz)} A {R * s:0.0},{R * s:0.0} 0 1 0 {PX(tux)},{PY(tuz)} " +
            $"A {fr * s:0.0},{fr * s:0.0} 0 0 1 {PX(xc)},{PY(w)} " +
            $"L {PX(-L)},{PY(w)} L {PX(-L)},{PY(-w)} L {PX(xc)},{PY(-w)} " +
            $"A {fr * s:0.0},{fr * s:0.0} 0 0 1 {PX(tux)},{PY(-tuz)} Z";

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 620 {H:0}\" width=\"100%\" style=\"max-width:620px\">");
        sb.Append($"<defs><clipPath id=\"body\"><path d=\"{outline}\"/></clipPath></defs>");
        sb.Append($"<path d=\"{outline}\" fill=\"var(--pt)\" stroke=\"none\"/>");
        // 两级环 + 管孔
        // 环按半径生效 ⇒ 会越过盘缘延到舌片上，故**裁剪到板身轮廓内**才是真实形状
        sb.Append("<g clip-path=\"url(#body)\">");
        foreach (var (rr, col) in new[] { (r2, "var(--ring2)"), (r1, "var(--ring1)") })
            sb.Append($"<circle cx=\"{PX(0)}\" cy=\"{PY(0)}\" r=\"{rr * s:0.0}\" fill=\"{col}\" " +
                      "stroke=\"var(--ink)\" stroke-width=\"0.6\" stroke-opacity=\"0.5\"/>");
        sb.Append("</g>");
        sb.Append($"<circle cx=\"{PX(0)}\" cy=\"{PY(0)}\" r=\"{h * s:0.0}\" fill=\"var(--bg)\" " +
                  "stroke=\"var(--ink)\" stroke-width=\"1.2\"/>");
        // 盘缘（虚线）：环盖住它之后仍要让人看得见盘在哪
        sb.Append($"<circle cx=\"{PX(0)}\" cy=\"{PY(0)}\" r=\"{R * s:0.0}\" fill=\"none\" " +
                  "stroke=\"var(--dim)\" stroke-width=\"1\" stroke-dasharray=\"4 3\"/>");
        sb.Append($"<path d=\"{outline}\" fill=\"none\" stroke=\"var(--ink)\" stroke-width=\"1.4\"/>");
        // 压接段
        sb.Append($"<rect x=\"{PX(-L)}\" y=\"{PY(w)}\" width=\"{fd.ClampLengthMm * s:0.0}\" " +
                  $"height=\"{2 * w * s:0.0}\" fill=\"none\" stroke=\"var(--clamp)\" " +
                  "stroke-width=\"1.6\" stroke-dasharray=\"5 3\"/>");
        // 标注
        string T(double x, double z, string t, string anchor = "middle", string cls = "lbl") =>
            $"<text x=\"{PX(x)}\" y=\"{PY(z)}\" text-anchor=\"{anchor}\" class=\"{cls}\">{t}</text>";
        sb.Append(T(-L + fd.ClampLengthMm / 2, 0, $"压接 {fd.ClampLengthMm:0}", "middle", "lbl clamp"));
        sb.Append(T(0, -h - 5, $"管孔 Ø{2 * h:0.0}"));
        sb.Append(T(-L * 0.62, -w - 8, $"环外级 r≤{r2:0.0}", "middle"));
        sb.Append(T(-L * 0.62, -w - 20, $"环内级 r≤{r1:0.0}", "middle"));
        sb.Append(T(-L / 2 - 6, w + 6, $"舌片 {L:0} × {2 * w:0}"));
        sb.Append(T(xc - 2, -w - 7, $"舌根圆角 R{fr:0}", "end"));
        sb.Append(T(R - 6, -R + 6, $"盘 Ø{2 * R:0}", "end"));
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// 径向剖面（自管孔向外，到盘缘为止）。
    ///
    /// ⚠ 这里踩过两个坑，都写在代码里免得再犯：
    ///   ① 曾按「孔 &lt; 环内 &lt; 环外 &lt; 盘缘」的顺序画三带 —— 而实际
    ///      **环外级半径 (孔+6) 大于盘半径**，第三带宽度是负的，画不出来。
    ///      现在按真实半径裁剪：**画不出「板身」这一带本身就是结论** ——
    ///      板身在圆盘上确实不存在，只存在于舌片上。
    ///   ② 曾把厚度方向放大 6×。径向跨度只有几毫米、板厚也是几毫米，本是同一量级，
    ///      放大之后厚度变成径向跨度的两倍，整张图撑爆。**1:1 就看得清。**
    ///      那个「不放大就看不见」的假设来自想象中的「大盘薄板」，这个设计不是。
    /// </summary>

    /// <summary>
    /// 轴测 3D 示意：管 + 一片法兰（两级环 + 板身 + 舌片）。
    ///
    /// 用**斜轴测**（不是透视）：屏幕 X = x + kx·z，屏幕 Y = −(y + ky·z)。
    ///   · 板面在 XZ、厚度沿 y（与 3DM、FE 的方位约定一致）
    ///   · 管轴 = y ⇒ 管从盘面**垂直穿出**，这一点平面剖面图表达不了，正是它难懂的原因
    /// 圆按 θ 采样成路径，不用 SVG 的 ellipse —— 旋转椭圆的参数容易写错，采样不会。
    ///
    /// ⚠ 厚度方向**放大**（真实板厚只有 2 mm 上下，盘径 60，1:1 会薄成一条线）。
    ///   放大倍数标在图上，且**三级厚度用同一个倍数**，比例关系仍然真实。
    /// </summary>
    // ⚠ 格式串只许用 0/# 作占位符。写 "0.1" 时 .NET 把 1 当**字面量**、
    //   小数点被吃掉：0.8 打成 "11"、31.6 打成 "321"。这个错今天犯了三次。
    private static string SvgIso(FinalDesign fd, int plate)
    {
        double h = fd.HoleRadiusMm, R = fd.DiscRadiusMm, wall = fd.WallMm;
        double r1 = fd.RingRadiiMm[0], r2 = fd.RingRadiiMm[1];
        double t = fd.TabThickMm[plate];
        double ti = t * fd.RingMul[plate], to = t * fd.RingMulOuter(plate);
        double L = fd.TabLengthMm, w = fd.TabHalfWidthMm;
        double ri = h - wall;                       // 管内半径

        const double KX = 0.52, KY = 0.30;          // z 轴的投影方向
        const double MAG = 5.0;                     // **只放大板厚**，见下
        const double TUBE = 40;                     // 管子露出的长度（真实尺寸，不放大）
        const double S = 3.4;                       // 总缩放 px/mm

        // ⚠ 放大倍数**不能写进投影**：y 既是板厚方向、也是**管子的轴向**。
        //   第一版在 P() 里对 y 统一乘 MAG，结果管长 40 mm 被当成板厚放大 5 倍 = 200 mm，
        //   直接顶出画布、糊成一整块矩形。
        //   ⇒ 投影用真实 y；只把**板厚**在传入前乘 MAG。
        ti *= MAG; to *= MAG; t *= MAG;

        double OX = 330, OY = 230;
        string P(double x, double y, double z) =>
            $"{OX + (x + KX * z) * S:0.0},{OY - (y + KY * z) * S:0.0}";

        // 圆采样（默认整圈；给 a0/a1 则只画一段）
        string Arc(double r, double y, double a0 = 0, double a1 = 2 * Math.PI, int n = 72)
        {
            var b = new StringBuilder();
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                b.Append(i == 0 ? "M " : "L ");
                b.Append(P(r * Math.Cos(a), y, r * Math.Sin(a)));
                b.Append(' ');
            }
            return b.ToString();
        }
        // 环形顶面：外圈正向 + 内圈反向（even-odd 挖空）
        string TopRing(double rOut, double rIn, double y, string fill) =>
            $"<path d=\"{Arc(rOut, y)}Z {Arc(rIn, y)}Z\" fill-rule=\"evenodd\" fill=\"{fill}\" " +
            "stroke=\"var(--ink)\" stroke-width=\"0.7\"/>";
        // 侧壁：只画近侧半圈（本投影下 z<0 为近侧 ⇒ θ∈[π,2π]）
        string Wall(double r, double yLo, double yHi, string fill)
        {
            var b = new StringBuilder("<path d=\"");
            b.Append(Arc(r, yHi, Math.PI, 2 * Math.PI, 48));
            for (int i = 48; i >= 0; i--)
            {
                double a = Math.PI + Math.PI * i / 48;
                b.Append("L ").Append(P(r * Math.Cos(a), yLo, r * Math.Sin(a))).Append(' ');
            }
            b.Append($"Z\" fill=\"{fill}\" stroke=\"var(--ink)\" stroke-width=\"0.7\"/>");
            return b.ToString();
        }

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 620 {OY + 200:0}\" width=\"100%\" style=\"max-width:620px\">");

        // ① 管：下半段（在盘后面）
        sb.Append(Wall(h, -TUBE, -ti / 2, "var(--tubeDark)"));

        // ② 舌片（板身）：一块厚 t 的板，从盘缘伸到 x=−L
        //    先画顶面，再画近侧长边侧壁，形成板的厚度感
        string TabTop = $"M {P(-L, t / 2, w)} L {P(0, t / 2, w)} L {P(0, t / 2, -w)} L {P(-L, t / 2, -w)} Z";
        string TabSide = $"M {P(-L, t / 2, -w)} L {P(0, t / 2, -w)} L {P(0, -t / 2, -w)} L {P(-L, -t / 2, -w)} Z";
        string TabEnd = $"M {P(-L, t / 2, w)} L {P(-L, t / 2, -w)} L {P(-L, -t / 2, -w)} L {P(-L, -t / 2, w)} Z";
        sb.Append($"<path d=\"{TabSide}\" fill=\"var(--ptDark)\" stroke=\"var(--ink)\" stroke-width=\"0.7\"/>");
        sb.Append($"<path d=\"{TabEnd}\" fill=\"var(--ptDark)\" stroke=\"var(--ink)\" stroke-width=\"0.7\"/>");
        sb.Append($"<path d=\"{TabTop}\" fill=\"var(--pt)\" stroke=\"var(--ink)\" stroke-width=\"0.7\"/>");

        // ③ 盘：板身 → 环外级 → 环内级，由外向内、由薄到厚
        sb.Append(Wall(R, -t / 2, t / 2, "var(--ptDark)"));
        sb.Append(TopRing(R, r2, t / 2, "var(--pt)"));
        sb.Append(Wall(r2, -to / 2, to / 2, "var(--ring2d)"));
        sb.Append(TopRing(r2, r1, to / 2, "var(--ring2)"));
        sb.Append(Wall(r1, -ti / 2, ti / 2, "var(--ring1d)"));
        sb.Append(TopRing(r1, h, ti / 2, "var(--ring1)"));

        // ④ 管：上半段（在盘前面）+ 管口
        sb.Append(Wall(h, ti / 2, TUBE, "var(--tube)"));
        sb.Append(TopRing(h, ri, TUBE, "var(--tubeTop)"));

        // ── 引线标注
        void Lead(double x, double y, double z, double dx, double dy, string txt, string anchor = "start")
        {
            string a = P(x, y, z);
            var parts = a.Split(',');
            double ax = double.Parse(parts[0]), ay = double.Parse(parts[1]);
            sb.Append($"<line x1=\"{ax:0.0}\" y1=\"{ay:0.0}\" x2=\"{ax + dx:0.0}\" y2=\"{ay + dy:0.0}\" " +
                      "stroke=\"var(--dim)\" stroke-width=\"0.9\"/>");
            sb.Append($"<circle cx=\"{ax:0.0}\" cy=\"{ay:0.0}\" r=\"2\" fill=\"var(--dim)\"/>");
            sb.Append($"<text x=\"{ax + dx + (anchor == "end" ? -4 : 4):0.0}\" y=\"{ay + dy + 4:0.0}\" " +
                      $"text-anchor=\"{anchor}\" class=\"lbl\">{txt}</text>");
        }
        Lead(0, TUBE, -h, -60, -18, $"铂管 Ø{2 * (h - wall):0} 壁 {wall:0.0}", "end");
        Lead((h + r1) / 2, ti / 2, 0, 24, -62, $"环内级 {ti / MAG:0.00}");
        Lead((r1 + r2) / 2, to / 2, 0, 58, -38, $"环外级 {to / MAG:0.00}");
        Lead((r2 + R) / 2, t / 2, 0, 86, -12, $"板身 {t / MAG:0.00}");
        Lead(-L * 0.6, t / 2, 0, -10, 62, $"舌片 {L:0}×{2 * w:0}（接铜排）", "end");
        Lead(0, -t / 2, h, 60, 40, $"盘 Ø{2 * R:0}");

        sb.Append($"<text x=\"612\" y=\"18\" text-anchor=\"end\" class=\"lbl dim\">" +
                  $"轴测示意　厚度方向放大 {MAG:0}×（三级同倍数，比例关系真实）</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string SvgSection(FinalDesign fd, int plate)
    {
        double h = fd.HoleRadiusMm, R = fd.DiscRadiusMm, wall = fd.WallMm;
        double r1 = fd.RingRadiiMm[0], r2 = fd.RingRadiiMm[1];
        double t = fd.TabThickMm[plate];
        double ti = t * fd.RingMul[plate], to = t * fd.RingMulOuter(plate);

        // ⚠ 切到盘缘就停的版本读不懂：标题写「板厚 1.82」而图里只有 2.22 / 1.98，
        //   因为板身那一带在圆盘上根本不存在（环外级半径已越过盘缘）。
        //   ⇒ **把刀切长一点，切到舌片上**，三个厚度全都出现，读者才对得上号。
        double xEnd = Math.Max(R, r2) + 8;      // 越过盘缘，进入舌片
        double x0 = 25.0 - 1.0, x1 = xEnd;      // 左端留出管壁

        // 角焊缝：与 Core/PlateCurrent2D.ThicknessAt 同一式子，也与 3DM 里那圈回转体同一式子。
        //   hw(d) = a − √(a²−(d−a)²)，d = r − 孔R，焊脚 a = max(板厚, 壁厚)
        //   隐式写作 (d−a)² + (hw−a)² = a² ⇒ 半径 a 的**凹圆弧**，贴管壁处切线竖直。
        // ⚠ 这张剖面此前没画它。而管孔边正是它最厚的地方（环内级厚 + 2a），
        //   图上却只有 ti —— 图与所交付的件、与 FE 实际算的厚度**三者不一致**。
        double aw = Math.Max(t, wall);
        double Zone(double r) => r <= r1 ? ti : r <= r2 ? to : t;
        double Hw(double d) => (d < 0 || d >= aw) ? 0
                             : aw - Math.Sqrt(Math.Max(0, aw * aw - (d - aw) * (d - aw)));

        double tMax = Math.Max(ti + 2 * aw, Math.Max(ti, Math.Max(to, t)));
        // ⚠ 比例必须**按宽度定**，不能按厚度定。
        //   按厚度定时 s=200/2.62=76 px/mm ⇒ 原生宽度 1206 px，
        //   而显示宽度限死 620 ⇒ 整张被压到 51 %，11 px 的字缩成 5.6 px 看不清。
        //   另外两张图本来就是按宽度定的，所以只有这张糊 —— 统一过来。
        const double VIEW_W = 620.0;
        double s = VIEW_W / (x1 - x0);          // 1:1（两方向同比例），且原生尺寸=显示尺寸
        double W = VIEW_W, H = tMax * s + 96;
        double mid = (H - 34) / 2 + 10;
        string PX(double x) => ((x - x0) * s).ToString("0.0");

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W:0} {H:0}\" width=\"100%\" style=\"max-width:{VIEW_W:0}px\">");

        // 铂管壁：沿管轴（垂直于本剖面）延伸 ⇒ 画成一段竖直块，示意焊接位置
        sb.Append($"<rect x=\"{PX(25.0)}\" y=\"{mid - tMax / 2 * s - 26:0.0}\" " +
                  $"width=\"{wall * s:0.0}\" height=\"{tMax * s + 52:0.0}\" " +
                  "fill=\"var(--tube)\" stroke=\"var(--ink)\" stroke-width=\"0.9\"/>");
        sb.Append($"<text x=\"{PX(25.0 + wall / 2)}\" y=\"{mid - tMax / 2 * s - 32:0.0}\" " +
                  $"text-anchor=\"middle\" class=\"lbl dim\">铂管壁 {wall:0.0}</text>");

        // ⚠ 标注**攒着最后画**。焊肉横跨环内级那一带，若边画边标，
        //   后画的焊肉会把「环内级 / 2.62 mm」两行字盖掉一半（实测过）。
        var lbl = new StringBuilder();
        void Band(double a2, double b2, double th, string col, string lab)
        {
            if (b2 - a2 <= 1e-9) return;
            sb.Append($"<rect x=\"{PX(a2)}\" y=\"{mid - th / 2 * s:0.0}\" width=\"{(b2 - a2) * s:0.0}\" " +
                      $"height=\"{th * s:0.0}\" fill=\"{col}\" stroke=\"var(--ink)\" stroke-width=\"0.9\"/>");
            lbl.Append($"<text x=\"{PX((a2 + b2) / 2)}\" y=\"{mid - th / 2 * s - 7:0.0}\" " +
                       $"text-anchor=\"middle\" class=\"lbl\">{lab}</text>");
            lbl.Append($"<text x=\"{PX((a2 + b2) / 2)}\" y=\"{mid + th / 2 * s + 14:0.0}\" " +
                       $"text-anchor=\"middle\" class=\"lbl\">{th:0.00} mm</text>");
        }
        Band(h, r1, ti, "var(--ring1)", "环内级");
        Band(r1, r2, to, "var(--ring2)", "环外级");
        Band(r2, xEnd, t, "var(--pt)", "板身（舌片）");

        // 两面各一条焊肉。采样画（不用 SVG 的 A 指令：屏幕 y 向下，
        // 大弧/扫掠两个标志位很容易写反，而采样不会）。
        // 台阶边界 r1 落在焊脚范围内时（如管壁 0.8 的共用片 a=3.40 > 环宽 3.0），
        // 焊肉会**跨级**，底边跟着台阶掉一格 —— 采样自然带出这个台阶。
        {
            var rs = new List<double>();
            const int N = 48;
            for (int k = 0; k <= N; k++) rs.Add(h + aw * k / (double)N);
            if (r1 > h && r1 < h + aw) { rs.Add(r1 - 1e-6); rs.Add(r1 + 1e-6); }
            rs.Sort();
            foreach (int sg in new[] { 1, -1 })
            {
                var pts = new StringBuilder();
                foreach (double r in rs)                                  // 外轮廓 = 板面 + 焊肉
                    pts.Append($"{PX(r)},{mid - sg * (Zone(r) / 2 + Hw(r - h)) * s:0.0} ");
                for (int k = rs.Count - 1; k >= 0; k--)                   // 回到板面
                    pts.Append($"{PX(rs[k])},{mid - sg * Zone(rs[k]) / 2 * s:0.0} ");
                sb.Append($"<polygon points=\"{pts}\" fill=\"var(--weld)\" " +
                          "stroke=\"var(--ink)\" stroke-width=\"0.9\"/>");
            }
            sb.Append($"<text x=\"{PX(h + aw + 0.4)}\" y=\"{mid - (ti / 2 + aw * 0.72) * s:0.0}\" " +
                      $"class=\"lbl hot\">角焊缝 焊脚 a={aw:0.00}（凹圆弧 R{aw:0.00}）</text>");
        }

        // 中面
        sb.Append($"<line x1=\"0\" y1=\"{mid:0.0}\" x2=\"{W:0}\" y2=\"{mid:0.0}\" " +
                  "stroke=\"var(--dim)\" stroke-width=\"0.8\" stroke-dasharray=\"6 4\"/>");
        // 盘缘：越过它就不再是圆盘、是舌片
        sb.Append($"<line x1=\"{PX(R)}\" y1=\"{mid - tMax / 2 * s - 24:0.0}\" x2=\"{PX(R)}\" " +
                  $"y2=\"{mid + tMax / 2 * s + 24:0.0}\" stroke=\"var(--clamp)\" " +
                  "stroke-width=\"1.4\" stroke-dasharray=\"4 3\"/>");
        sb.Append($"<text x=\"{PX(R)}\" y=\"{mid + tMax / 2 * s + 38:0.0}\" text-anchor=\"middle\" " +
                  $"class=\"lbl clamp\">盘缘 r={R:0.0}　→ 右边是舌片</text>");
        // 半径刻度
        foreach (var (rv, lab) in new[] { (h, $"管孔 r={h:0.0}"), (r1, $"{r1:0.0}"), (r2, $"{r2:0.0}") })
            sb.Append($"<text x=\"{PX(rv)}\" y=\"{H - 8:0.0}\" text-anchor=\"middle\" class=\"lbl dim\">{lab}</text>");
        sb.Append($"<text x=\"{W - 4:0}\" y=\"14\" text-anchor=\"end\" class=\"lbl dim\">1:1（未放大）　横轴 = 半径 mm</text>");
        sb.Append(lbl);            // ← 分区标注最后画，见上面 Band 处的说明
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>整线布置：三段管 + 四片法兰（侧视，管轴 = Y）。</summary>
    private static string SvgLine(FinalDesign fd)
    {
        const double segLen = 300, tubeId = 50;
        double ro = tubeId / 2 + fd.WallMm, R = fd.DiscRadiusMm, L = fd.TabLengthMm;
        double y0 = -30, y1 = 3 * segLen + 30;
        double s = 620.0 / (y1 - y0);
        double H = (2 * (R + L * 0.12) + 40) * s + 46;
        double mid = H / 2;
        string PX(double y) => ((y - y0) * s).ToString("0.0");
        string PY(double r) => (mid - r * s).ToString("0.0");

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 620 {H:0}\" width=\"100%\" style=\"max-width:620px\">");
        for (int i = 0; i < 3; i++)
            sb.Append($"<rect x=\"{PX(i * segLen)}\" y=\"{PY(ro)}\" width=\"{segLen * s:0.0}\" " +
                      $"height=\"{2 * ro * s:0.0}\" fill=\"var(--tube)\" stroke=\"var(--ink)\" stroke-width=\"1\"/>");
        string[] nm = { "入口", "共用1", "共用2", "出口" };
        for (int j = 0; j < 4; j++)
        {
            double y = j * segLen, t = fd.TabThickMm[j];
            double wPx = Math.Max(3, t * s * 6);          // 法兰厚度放大以便看清
            double xPx = (y - y0) * s - wPx / 2;
            sb.Append($"<rect x=\"{xPx:0.0}\" y=\"{PY(R)}\" " +
                      $"width=\"{wPx:0.0}\" height=\"{2 * R * s:0.0}\" " +
                      "fill=\"var(--pt)\" stroke=\"var(--ink)\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{PX(y)}\" y=\"{mid - R * s - 8:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl\">{nm[j]} t{t:0.00}</text>");
        }
        string[] seg = { "HC1 1150 °C", "HC2 1080 °C", "HC3 1050 °C" };
        for (int i = 0; i < 3; i++)
            sb.Append($"<text x=\"{PX(i * segLen + segLen / 2)}\" y=\"{mid + 4:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl onTube\">{seg[i]}</text>");
        sb.Append($"<text x=\"{PX(1.5 * segLen)}\" y=\"{H - 8:0.0}\" text-anchor=\"middle\" class=\"lbl dim\">" +
                  $"管 Ø{tubeId:0} × 壁 {fd.WallMm:0.0}　三段各 {segLen:0} mm　（法兰厚度已放大以便看清）</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Bar(double actual, double limit, bool lessIsBetter = true)
    {
        double pct = Math.Abs(limit) < 1e-9 ? 60
                   : Math.Clamp((limit - actual) / Math.Abs(limit) * 100, 2, 99);
        string cls = pct < 15 ? "bar tight" : "bar";
        return $"<span class=\"{cls}\"><i style=\"width:{pct:0}%\"></i></span>" +
               $"<span class=\"pct\">{(Math.Abs(limit) < 1e-9 ? "方向安全" : pct.ToString("0") + " %")}</span>";
    }

    /// <summary>
    /// 生成说明书 HTML。**public 是故意的**：`--cli --manual` 要能不开 GUI 就导出，
    /// 否则「图对不对」只能靠肉眼开窗口看 —— 那不是可复核的验证。
    /// </summary>
    public static string BuildHtml(FinalDesign fd)
    {
        // ★ 判据值**只从 FinalDesign 取**，本页不再自己抄一份。
        //
        // 这里原来硬编码了两档各五个数，抄的是定案当天（08-15）那次运行 ——
        // 而收敛度量与 ②″ 限值都是在那之后才改的。08-16 用「▶ 复现定案」重跑发现
        // ②′ 与 ③ 两项对不上，**且两档之间的大小关系是反的**：
        // 抄的说 0.8 档 ③ 更小（5.38 < 6.29），实算是 0.8 档 ③ 更大（6.18 > 5.30）。
        // 判定结论没变（两档仍全过），但「哪一档在 ③ 上更宽裕」这句话说反了。
        // ⇒ 又一次「同一个数存两处然后悄悄漂开」。收敛到一处才不会再犯。
        var crit = new (string n, string k, double a, double l, string u)[]
        {
            ("① 升温 空管到目标",      "硬判据", fd.RampH,      72,    "h"),
            ("②″ 圆盘区最高温 − 管温", "硬判据", fd.DiscOverK,  5.00,  "K"),
            ("②′ 管孔净流入 须为正",   "硬判据", fd.HoleFluxW,  0,     "W"),
            ("③ 法兰增量温降",         "目标",   fd.FlangeDipK, 10.00, "K"),
            ("管 J 电流密度",          "硬判据", fd.TubeJ,      12.00, "A/mm²"),
        };

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html lang=""zh""><head><meta charset=""utf-8"">
<title>Pt_Optimize 使用说明</title><style>
:root{--bg:#F4F6F7;--card:#FFF;--ink:#12171A;--ink2:#3D4B53;--muted:#68767E;
--rule:#D2DADE;--pt:#E8D9A8;--ring1:#E9A159;--ring2:#F0C79A;--tube:#C9D3D8;
--clamp:#2C7A8C;--ok:#2C6B58;--hot:#C2570F;--dim:#9AA7AE;--ptDark:#C9B276;--ring1d:#C07A32;--ring2d:#CFA57A;--tubeDark:#9FAEB5;--tubeTop:#DCE4E8;--weld:#B9452F}
@media(prefers-color-scheme:dark){:root{--bg:#0E1216;--card:#161C21;--ink:#E7EEF1;
--ink2:#B3C0C7;--muted:#7E8D95;--rule:#28333A;--pt:#6B5C33;--ring1:#A6702F;--ring2:#7A5A38;
--tube:#33424A;--clamp:#4FA8BC;--ok:#6FC0A4;--hot:#F0904A;--dim:#5C6A72;--ptDark:#514429;--ring1d:#7E5423;--ring2d:#5C442A;--tubeDark:#26323A;--tubeTop:#44565F;--weld:#D9694F}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
font-family:'Microsoft YaHei UI','Segoe UI',system-ui,sans-serif;font-size:15px;line-height:1.7}
.wrap{max-width:760px;margin:0 auto;padding:28px 24px 80px}
h1{font-size:1.7rem;margin:0 0 .3rem}
h2{font-size:1.2rem;margin:2.2rem 0 .6rem;padding-bottom:.3rem;border-bottom:1px solid var(--rule)}
h3{font-size:1rem;margin:1.4rem 0 .4rem}
p,li{color:var(--ink2)}
.lede{color:var(--muted);margin:0 0 1.4rem}
.fig{background:var(--card);border:1px solid var(--rule);border-radius:4px;
padding:14px;margin:14px 0;text-align:center}
.cap{font-size:.82rem;color:var(--muted);margin-top:8px;text-align:left}
text.lbl{font:13px 'Microsoft YaHei UI',sans-serif;fill:var(--ink)}
text.dim{fill:var(--muted);font-size:11.5px}
text.clamp{fill:var(--clamp)}
text.onTube{fill:var(--ink);font-size:11.5px}
table{border-collapse:collapse;width:100%;font-size:.88rem;background:var(--card);
border:1px solid var(--rule);border-radius:4px;overflow:hidden}
th,td{padding:8px 12px;text-align:left;border-bottom:1px solid var(--rule)}
th{font-size:.76rem;color:var(--muted);background:var(--bg)}
tr:last-child td{border-bottom:none}
td.n{font-family:Consolas,monospace}
/* 首列是短标签的表：不让「分段核算」被折成「分段核／算」 */
table.nw td:first-child,table.nw th:first-child{white-space:nowrap}
.bar{display:inline-block;width:86px;height:7px;background:var(--rule);position:relative;
border-radius:1px;vertical-align:middle}
.bar i{position:absolute;left:0;top:0;bottom:0;background:var(--ok);border-radius:1px}
.bar.tight i{background:var(--hot)}
.pct{font-size:.78rem;color:var(--muted);margin-left:7px}
.note{border-left:3px solid var(--hot);padding:6px 0 6px 14px;margin:16px 0;
color:var(--ink2);font-size:.92rem;background:var(--card)}
code{font-family:Consolas,monospace;background:var(--card);padding:1px 5px;
border:1px solid var(--rule);border-radius:3px;font-size:.88em}
</style></head><body><div class=""wrap"">");

        sb.Append($"<h1>Pt_Optimize 使用说明</h1>");
        sb.Append($"<p class=\"lede\">当前定案档：<b>{fd.Name}</b>　合计 <b>{fd.TotalMassG:0} g</b>" +
                  $"（管 {fd.TubeMassG:0} + 法兰 {fd.FlangeMassG:0}）<br>" +
                  $"咬住它的：{fd.Binding}</p>");

        sb.Append("<div class=\"note\"><b>下面所有图都是按当前定案档实时画的。</b>" +
                  "换档，图跟着变。图片一旦静态化就成了「同一个数存两处」——" +
                  "定案值一改，图还留在旧构型上，而它看起来完全正常。</div>");

        // ════════════════════════════════════════════════════════════════
        //  操作说明（用户 2026-08-16：「APP 程式的操作说明放入 APP 内」）
        //  原来这部分只在 docs\APP使用说明书.md 里，程序里反而没有 ——
        //  说明书离开了它说明的那个东西，就是最容易漂开的一种「两处」。
        // ════════════════════════════════════════════════════════════════
        sb.Append("<h2>1. 上手：三件最常做的事</h2>");
        sb.Append("<table><tr><th>你想做什么</th><th>怎么做</th><th>看哪里</th></tr>" +
                  "<tr><td><b>看定案档长什么样、用多少铂</b></td>" +
                  "<td>「整线设计」页 → 选<b>定案档 ▾</b> → 点<b>载入定案</b> → 点<b>核算整线</b></td>" +
                  "<td>下方判据表（先看<b>裕度</b>列）</td></tr>" +
                  "<tr><td><b>出图纸交给加工</b></td>" +
                  "<td>「整线设计」页 → 选定案档 → 点<b>导出定案 3DM</b></td>" +
                  "<td>输出框里的 round-trip 与质量对账</td></tr>" +
                  "<tr><td><b>改个参数试试</b></td>" +
                  "<td>改左侧参数表或本页控件 → <b>核算整线</b>（分钟级，可取消）</td>" +
                  "<td>判据表 + 三张场图</td></tr></table>");
        sb.Append("<div class=\"note\"><b>「核算整线」走的是页面上的参数，不是定案档。</b>" +
                  "「载入定案」有两项控件表达不了 —— <b>管孔两级渐变环</b>与<b>逐片舌保温</b>，" +
                  "载入后输出框会把它们列出来。要<b>复现定案数</b>，用「导出定案 3DM」" +
                  "或命令行 <code>--cli --busbarplan --wall 0.6</code>。</div>");

        sb.Append("<h2>2. 界面在哪、按钮做什么</h2>");
        sb.Append($"<div class=\"fig\">{SvgUi()}" +
                  "<div class=\"cap\">窗口分三块：左边参数表、右上报告、右下页签区。" +
                  "顶上那条是<b>主工具条</b>（只管左边那张参数表）；" +
                  "<b>每个页签有自己的工具条</b>，两者互不相干 —— 这是最常见的误按。</div></div>");

        sb.Append("<table><tr><th></th><th>是什么</th><th>要点</th></tr>" +
                  "<tr><td>①</td><td><b>参数表</b>（左侧，分类折叠）</td>" +
                  "<td>单段模型的全部输入。选中某项时下方有说明。" +
                  "改这里只影响「计算 (F5)」那条链</td></tr>" +
                  "<tr><td>②</td><td><b>报告</b>（右上）</td>" +
                  "<td>单段解：铂用量／热平衡／电气／温度分布／流动。<b>不含法兰</b></td></tr>" +
                  "<tr><td>③</td><td><b>五个页签</b></td>" +
                  "<td>整线设计（主力）／分析／分段核算／轴向剖面／使用说明（本页，<b>F1</b> 直达）</td></tr>" +
                  "<tr><td>④</td><td><b>主工具条</b></td>" +
                  "<td>计算 (F5)、三个扫描、保存／读取方案、导出 CSV</td></tr>" +
                  "<tr><td>⑤</td><td><b>页签自己的工具条</b></td>" +
                  "<td>整线设计页的按钮在这里，见下表</td></tr></table>");

        sb.Append("<h3>「整线设计」页的工具条（主力页）</h3>");
        sb.Append("<table class=\"nw\"><tr><th>按钮</th><th>做什么</th><th>耗时</th></tr>" +
                  "<tr><td><b>核算整线</b></td><td>按页面参数解一次耦合场，出判据表</td><td>分钟级，可取消</td></tr>" +
                  "<tr><td><b>自动定厚</b></td><td>让优化器调四片法兰厚度</td><td>更久，可取消</td></tr>" +
                  "<tr><td>分析几何变数</td><td>报各几何量对判据的斜率（只测不调）</td><td>分钟级</td></tr>" +
                  "<tr><td>导出 .3dm</td><td>只导法兰板（旧功能，非定案构型）</td><td>秒级</td></tr>" +
                  "<tr><td><b>定案档 ▾ + 载入定案</b></td><td>把 <code>FinalDesign</code> 的某一档灌进各控件</td><td>即时</td></tr>" +
                  "<tr><td><b>导出定案 3DM</b></td><td>整机几何 + 自校（见 §7）</td><td>十几秒</td></tr></table>");

        sb.Append("<h3>其余四个页签</h3>");
        sb.Append("<table class=\"nw\"><tr><th>页签</th><th>用途</th></tr>" +
                  "<tr><td><b>分析</b></td><td>① 升温可达性、② 厚度灵敏度 —— 两个独立的专项核算</td></tr>" +
                  "<tr><td><b>分段核算</b></td><td>逐段填温度／水头／牌号／壁厚，出强度与铂重（<b>解析、即时</b>）。" +
                  "「核算法兰」把法兰算进来才是可交付的总铂</td></tr>" +
                  "<tr><td><b>轴向剖面</b></td><td>单段解的温度沿轴分布（随 F5 刷新）</td></tr>" +
                  "<tr><td><b>使用说明</b></td><td>本页。工具条上可切定案档，图跟着重画</td></tr></table>");
        sb.Append("<div class=\"note\"><b>「分段核算」页是解析的，「整线设计」页是耦合数值解。</b>" +
                  "两者数不一样很正常 —— 前者不解温度场。<b>可交付的数以「整线设计」页为准。</b></div>");

        sb.Append("<h2>3. 整线布置</h2>");
        sb.Append($"<div class=\"fig\">{SvgLine(fd)}" +
                  "<div class=\"cap\">三段铂管串联，四片法兰兼作电极。中间两片是<b>共用片</b>——" +
                  "两侧段电流相位差 120°，它承担 √3 倍电流，发热 ∝ I² ⇒ 现场失效都卡在这两片。</div></div>");

        sb.Append("<h2>4. 法兰几何</h2>");
        sb.Append($"<div class=\"fig\">{SvgPlate(fd)}" +
                  $"<div class=\"cap\">板面在 XZ 平面、厚度沿 Y（与 3DM 的方位约定一致）。" +
                  $"<b>舌根圆角 R{fd.TabFilletMm:0}</b> 不是装饰：峰值电流拥塞就发生在这个凹角上" +
                  $"（实测峰位 x≈−24.3, z≈±13）。<b>管孔两级渐变环</b>压制孔周电流集中，" +
                  $"半径与厚度都是<b>相对量</b>（相对管孔 / 相对板厚）——" +
                  $"写成绝对值时板一变厚环就静默消失，整条优化曾因此停在离最优 28 % 的地方。<br><br>" +
                  $"<b>注意盘缘那条虚线</b>：环外级的外半径 {fd.RingRadiiMm[1]:0.0} mm <b>大于盘半径 {fd.DiscRadiusMm:0.0} mm</b>，" +
                  $"而盘孔环带只有 {fd.DiscRadiusMm - fd.HoleRadiusMm:0.0} mm 宽 —— " +
                  $"<b>两级环几乎覆盖了整个圆盘</b>，「板身」在盘上几乎不存在、只存在于舌片。" +
                  $"这就是环倍率为何一直是个强旋钮：它动的不是「孔边一圈」，是整个圆盘。</div></div>");

        sb.Append($"<h3>立体示意（入口片）</h3>");
        sb.Append($"<div class=\"fig\">{SvgIso(fd, 0)}" +
                  $"<div class=\"cap\"><b>管从盘面垂直穿出</b>——这一点平面图表达不了，也是剖面图难懂的原因。" +
                  $"盘上从管孔往外是<b>两级台阶</b>：环内级 {fd.TabThickMm[0] * fd.RingMul[0]:0.00} → " +
                  $"环外级 {fd.TabThickMm[0] * fd.RingMulOuter(0):0.00} → 板身 {fd.TabThickMm[0]:0.00} mm，" +
                  $"越靠近管孔越厚。舌片伸出去接铜排。<br>" +
                  $"厚度方向放大了（真实板厚 2 mm 上下、盘径 {2 * fd.DiscRadiusMm:0}，1:1 会薄成一条线），" +
                  $"但<b>三级用同一个倍数</b>，谁比谁厚多少是真实的。</div></div>");

        sb.Append($"<h3>径向剖面（入口片，板厚 {fd.TabThickMm[0]:0.00} mm）</h3>");
        sb.Append($"<div class=\"fig\">{SvgSection(fd, 0)}" +
                  $"<div class=\"cap\">自管孔向外到盘缘，<b>1:1，未放大</b>。关于中面对称。<br>" +
                  $"环内级 = 板厚 × {fd.RingMul[0]:0.00}，环外级 = 板厚 × {fd.RingMulOuter(0):0.000}。<br>" +
                  $"横轴是<b>半径</b>：从管壁往外切一刀。三个厚度依次是环内级 {fd.TabThickMm[0] * fd.RingMul[0]:0.00}、" +
                  $"环外级 {fd.TabThickMm[0] * fd.RingMulOuter(0):0.00}、板身 {fd.TabThickMm[0]:0.00} mm。<br>" +
                  $"<b>注意板身那一带在盘缘<i>右边</i></b>：环外级外半径 {fd.RingRadiiMm[1]:0.0} mm " +
                  $"已经越过盘缘 {fd.DiscRadiusMm:0.0} mm ⇒ 圆盘上从孔到缘全被两级环占满，" +
                  $"板厚 {fd.TabThickMm[0]:0.00} mm 只出现在舌片上。这就是环倍率为何是个强旋钮：" +
                  $"它动的不是「孔边一圈」，是整个圆盘。<br>" +
                  $"<b>管孔边那两坨深色是角焊缝</b>：焊脚 a = max(板厚, 壁厚) = " +
                  $"{Math.Max(fd.TabThickMm[0], fd.WallMm):0.00} mm 的<b>凹圆弧</b>，两面各一条。" +
                  $"它是<b>叠加</b>在分区厚度上的额外金属 ⇒ 孔边真实厚度 " +
                  $"{fd.TabThickMm[0] * fd.RingMul[0] + 2 * Math.Max(fd.TabThickMm[0], fd.WallMm):0.00} mm，" +
                  $"是环内级的 {(fd.TabThickMm[0] * fd.RingMul[0] + 2 * Math.Max(fd.TabThickMm[0], fd.WallMm)) / (fd.TabThickMm[0] * fd.RingMul[0]):0.0} 倍。" +
                  $"这一段 FE 一直算着、交付 3DM 里也画着，只是这张图之前漏了。</div></div>");

        sb.Append("<h3>四片各不相同</h3><table><tr><th>片</th><th>板厚 mm</th>" +
                  "<th>环内级</th><th>环外级</th><th>舌片保温 mm</th></tr>");
        string[] nm = { "入口", "共用1", "共用2", "出口" };
        for (int j = 0; j < 4; j++)
            sb.Append($"<tr><td>{nm[j]}</td><td class=\"n\">{fd.TabThickMm[j]:0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMul[j]:0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMulOuter(j):0.00}</td>" +
                      $"<td class=\"n\">{fd.TabInsulMm[j]:0.0}</td></tr>");
        sb.Append("</table><p style=\"font-size:.88rem\">舌片保温四片差 <b>12 倍</b>：" +
                  "端片要靠保温保住发热，共用片本身发热过剩、几乎要裸露散热。<b>不能同规格。</b></p>");

        sb.Append("<h2>5. 判据表怎么读</h2>");
        sb.Append("<table><tr><th>判据</th><th>类别</th><th>实际</th><th>限值</th><th>裕度</th></tr>");
        foreach (var (n, k, a, l, u) in crit)
            sb.Append($"<tr><td>{n}</td><td>{k}</td><td class=\"n\">{a:0.000} {u}</td>" +
                      $"<td class=\"n\">{(Math.Abs(l) < 1e-9 ? "> 0" : l.ToString("0.00"))}</td>" +
                      $"<td>{Bar(a, l)}</td></tr>");
        sb.Append("</table>");
        sb.Append("<div class=\"note\"><b>裕度这一列比「✓」有用。</b>" +
                  "本项目最常见的错就是<b>贴着限值判过与不过</b>——" +
                  "曾用 0.02–0.08 K 的差别决定了 700 g 铂金，而那点温差只对应 <b>14 mW</b>、" +
                  "占段功率 5 ppm，现场任何仪器都测不出来。<br>" +
                  "看到 ✓ 先问两句：这个裕度比<b>数值噪声</b>大吗？比<b>现场能分辨的尺度</b>大吗？</div>");

        sb.Append("<h3>限值的出处（每条都必须有）</h3><table class=\"nw\">" +
                  "<tr><th>判据</th><th>限值</th><th>出处</th></tr>" +
                  "<tr><td>① 升温</td><td class=\"n\">72 h</td><td>业主「≤ 3 天」</td></tr>" +
                  "<tr><td>②′ 管孔净流入</td><td class=\"n\">&gt; 0</td><td>总纲 C2「为负即法兰比管热」——方向性判据</td></tr>" +
                  "<tr><td>②″ 圆盘峰</td><td class=\"n\">5 K</td><td>现场控温精度 ±5 K</td></tr>" +
                  "<tr><td>③ 增量温降</td><td class=\"n\">10 K</td><td>总纲 C2；<b>业主明确：贴着热偶误差定的</b></td></tr>" +
                  "<tr><td>管 J</td><td class=\"n\">12 A/mm²</td><td>现场：一般 15，管壁 0.6 时 12 是极限</td></tr>" +
                  "<tr><td>管壁下界</td><td class=\"n\">0.6 mm</td><td>手工 TIG 烧穿下界（自动 0.3、激光 0.1）</td></tr>" +
                  "</table>");
        sb.Append("<div class=\"note\"><b>③ 的 10 K：出处是热偶误差，物理依据未知。</b>" +
                  "⇒ 不得据此声称「超过 10 K 也安全」，也不得声称「真实限值是 X」。" +
                  "但有两条推论成立：<b>不该把设计点摆在 10 上</b>（摆在测量地板上的判定分不出真假）；" +
                  "<b>不该拿它当控制靶</b>（∂③/∂板厚 = +149 K/mm 是正号，会让优化器加厚板把判据推向限值）。</div>");

        sb.Append("<h2>6. 收敛信息</h2>");
        sb.Append($"<p>本档外层耦合<b>剩余误差估计 {fd.ResidualK:0.00} K</b> —— 这是「距不动点」的估计 " +
                  "<code>δ·r/(1−r)</code>，<b>不是</b>「相邻两轮变化 δ」。<br>" +
                  "曾经用 δ 当收敛判据：δ=1.36 时报「5 轮收敛」，而真实剩余误差约 34 K。" +
                  "界面在判据表上方打这一行；<b>未收敛时会打「下面每个数都不可引用」——那是字面意思。</b></p>");

        sb.Append("<h2>7. 定案 3DM</h2>");
        sb.Append("<p>「导出定案 3DM」或命令行 <code>--cli --make3dm</code>。" +
                  "内容：三段铂管 + 四片法兰（板身 / 环外级 / 环内级 / <b>角焊缝</b>）+ 压接段参考线。<br>" +
                  "<b>图层按片分，不按类型分</b> —— 交付件要能单独调出某一片；" +
                  "更硬的理由是厚度探针沿 Y 打射线，而四片正是沿 Y 排成一列，" +
                  "同层会被一次穿透、厚度<b>加起来</b>（当时实测 10.450 = 2.11+3.40+3.18+1.76，那是<b>出事那天的板厚</b>，不是现在的定案值）。</p>");
        sb.Append("<h3>写完立刻自校，三条都过才算交付件</h3><table>" +
                  "<tr><th>校验</th><th>判据</th><th>它防的是什么</th></tr>" +
                  "<tr><td>round-trip 方位</td><td>从磁盘读回，沿 Y 的跨度 = 板厚</td>" +
                  "<td>板被画到 XY 面沿 Z 拉伸（2026-08-12 出过：自己写的 .3dm 再读回来量到 <b>0 材料</b>）</td></tr>" +
                  "<tr><td>角焊缝体积</td><td>回转体实测 vs 解析积分，差 ≤ 0.5 %</td>" +
                  "<td>焊缝形状画错（曾用 12 段同心带拟合圆弧，成了一圈<b>阶梯</b>）</td></tr>" +
                  "<tr><td><b>质量对账</b></td><td>逐件 3DM 体积×ρ vs FE 网格体积×ρ，差 ≤ 2 %</td>" +
                  "<td>孔没挖、焊角没画、环多长出去 —— <b>一切几何细节</b></td></tr></table>");
        sb.Append("<div class=\"note\"><b>校验量选错，比不校验更危险。</b>" +
                  "原来只验「包围盒沿 Y = 板厚」，一路报「19 项全吻合」，" +
                  "而实际法兰只有计算值的 <b>43 %</b>（用户在 Rhino 里打开才发现）。" +
                  "那个量分辨不出孔有没有挖、焊角在不在 —— 它发的是<b>虚假的通过证</b>。<br>" +
                  "质量是唯一把所有几何细节都卷进去的标量。现在它是自动判据，不是我手算的。</div>");

        sb.Append("<h2>8. 三条铁律</h2><ol>" +
                  "<li><b>判据只有一个来源</b>：<code>LineRunner.Judge</code>。界面/命令行/报告只读结果，不得自己重算。</li>" +
                  "<li><b>几何只有一个来源</b>：<code>FinalDesign</code>。曾经辅助命令各钉着几代前的几何，跑得出漂亮的数——但那是另一个设计的数。</li>" +
                  "<li><b>判据不允许消失</b>：只能过/不过/无法判定，<b>无法判定一律不算通过</b>。</li></ol>");

        sb.Append("<h2>9. 已知坑（不看这节会重犯）</h2>");
        sb.Append("<table class=\"nw\"><tr><th>坑</th><th>形状</th><th>怎么发现的</th></tr>" +
                  "<tr><td><b>代理量当原量</b></td><td>拿邻近的量顶替判据／靶／收敛度量</td>" +
                  "<td>一天犯三次：拿段内最大偏差当管根、拿 B 当 ③ 的靶、拿会切换分支的标量当收敛度量</td></tr>" +
                  "<tr><td><b>默认值当需求</b></td><td>冻结的占位值被当成给定条件</td>" +
                  "<td>已十次。<b>每个冻结值都要扫一遍</b>，哪怕最后证明它是对的</td></tr>" +
                  "<tr><td><b>参数写绝对值</b></td><td>依赖项一动，对策静默失效或被悄悄重画</td>" +
                  "<td>渐变环写绝对半径 ⇒ 板一变厚环就消失，整条可行性阶梯从来没有环</td></tr>" +
                  "<tr><td><b>修一个漏一个</b></td><td>新旋钮写在旧旋钮的 <code>continue</code> 之后</td>" +
                  "<td>优化器报「都到位」停机，实际差 0.01 K</td></tr>" +
                  "<tr><td><b>容差粗于判据分辨率</b></td><td>判据在读求解器的残差</td>" +
                  "<td>容差 1.0 K 而 ②″ 在 0.01 K 上判过不过</td></tr>" +
                  "<tr><td><b>校验量选错</b></td><td>校验通过，但它管不到出错的那一维</td>" +
                  "<td>包围盒验不了材料，报「全吻合」而法兰差 57 %</td></tr></table>");
        sb.Append("<div class=\"note\"><b>共同点：不报错、输出格式正常、数值看着合理 —— 但结论是错的。</b><br>" +
                  "唯一可靠的抓法是<b>交叉核对</b>：任何「通过」的结论，用另一个独立的数验一遍。</div>");

        sb.Append("<h2>10. 常用命令行</h2><table><tr><th>命令</th><th>用途</th></tr>" +
                  "<tr><td class=\"n\">--cli --final2</td><td>可行性阶梯：管壁从宽到窄逐档定尺寸</td></tr>" +
                  "<tr><td class=\"n\">--cli --busbarplan --wall 0.6</td><td>铜排尺寸与位置（自检整线是否全过）</td></tr>" +
                  "<tr><td class=\"n\">--cli --make3dm</td><td>两档定案 3DM + round-trip 校验</td></tr>" +
                  "<tr><td class=\"n\">--cli --hotspot --wall 0.6</td><td>峰值位置实测（坐标、局部 J、局部厚度）</td></tr>" +
                  "</table><p style=\"font-size:.88rem\"><code>--wall</code> 给了不认识的值会<b>抛异常</b>，不会静默回退。</p>");

        sb.Append("<h2>11. 现场还需确认的数</h2>");
        sb.Append("<table class=\"nw\"><tr><th>量</th><th>现状</th><th>一旦不同，影响多大</th></tr>" +
                  "<tr><td>法兰 J 的许用值</td><td><b>无依据</b>（现取 10，已降为参考量）</td>" +
                  "<td>法兰 J 实测 34。这条一旦成为硬判据，结论大幅改变</td></tr>" +
                  "<tr><td>③ 的物理依据</td><td>只知刻度来自热偶误差 ±10 K</td>" +
                  "<td>决定能否把设计点从 10 K 往外放</td></tr>" +
                  "<tr><td>铜排表面状态</td><td>按氧化铜 ε = 0.7 算</td>" +
                  "<td>抛光铜只有 0.05，<b>差 14 倍</b>，散热段长度直接翻几倍</td></tr>" +
                  "<tr><td>焊接方法</td><td>按手工 TIG（下界 0.6 mm）</td>" +
                  "<td>自动 TIG 可到 0.3、激光 0.1，<b>差一个量级</b>；0.6 档正被它咬住</td></tr></table>");

        sb.Append("<p style=\"margin-top:40px;font-size:.82rem;color:var(--muted)\">" +
                  "本页即完整说明书，<b>不再需要去仓库读文档</b>（F1 随时回到这里）。" +
                  "<code>docs\\APP使用说明书.md</code> 只留「怎么启动、怎么构建」这类程序内没法讲的事，" +
                  "工具条上的「打开 Markdown 版」打开的就是它。<br>" +
                  "两处都写全 = 同一份内容存两份，迟早漂开 —— 本项目最常见的失效。</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
