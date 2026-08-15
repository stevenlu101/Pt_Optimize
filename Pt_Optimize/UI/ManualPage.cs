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

        double tMax = Math.Max(ti, Math.Max(to, t));
        double s = 200.0 / tMax;                // 1:1，按最厚一带定比例
        double W = (x1 - x0) * s, H = tMax * s + 96;
        double mid = (H - 34) / 2 + 10;
        string PX(double x) => ((x - x0) * s).ToString("0.0");

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W:0} {H:0}\" width=\"100%\" style=\"max-width:620px\">");

        // 铂管壁：沿管轴（垂直于本剖面）延伸 ⇒ 画成一段竖直块，示意焊接位置
        sb.Append($"<rect x=\"{PX(25.0)}\" y=\"{mid - tMax / 2 * s - 26:0.0}\" " +
                  $"width=\"{wall * s:0.0}\" height=\"{tMax * s + 52:0.0}\" " +
                  "fill=\"var(--tube)\" stroke=\"var(--ink)\" stroke-width=\"0.9\"/>");
        sb.Append($"<text x=\"{PX(25.0 + wall / 2)}\" y=\"{mid - tMax / 2 * s - 32:0.0}\" " +
                  $"text-anchor=\"middle\" class=\"lbl dim\">铂管壁 {wall:0.0}</text>");

        void Band(double a2, double b2, double th, string col, string lab)
        {
            if (b2 - a2 <= 1e-9) return;
            sb.Append($"<rect x=\"{PX(a2)}\" y=\"{mid - th / 2 * s:0.0}\" width=\"{(b2 - a2) * s:0.0}\" " +
                      $"height=\"{th * s:0.0}\" fill=\"{col}\" stroke=\"var(--ink)\" stroke-width=\"0.9\"/>");
            sb.Append($"<text x=\"{PX((a2 + b2) / 2)}\" y=\"{mid - th / 2 * s - 7:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl\">{lab}</text>");
            sb.Append($"<text x=\"{PX((a2 + b2) / 2)}\" y=\"{mid + th / 2 * s + 14:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl\">{th:0.00} mm</text>");
        }
        Band(h, r1, ti, "var(--ring1)", "环内级");
        Band(r1, r2, to, "var(--ring2)", "环外级");
        Band(r2, xEnd, t, "var(--pt)", "板身（舌片）");

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
        // 两档的判据实测值（出自 --final2 D7；与 FinalDesign 同一次运行）
        var crit = fd.WallMm < 0.7
            ? new (string n, string k, double a, double l, string u)[]
              { ("① 升温 空管到目标", "硬判据", 0.08, 72, "h"),
                ("②″ 圆盘区最高温 − 管温", "硬判据", 1.17, 5.00, "K"),
                ("②′ 管孔净流入 须为正", "硬判据", 1.63, 0, "W"),
                ("③ 法兰增量温降", "目标", 6.29, 10.00, "K"),
                ("管 J 电流密度", "硬判据", 10.96, 12.00, "A/mm²") }
            : new (string n, string k, double a, double l, string u)[]
              { ("① 升温 空管到目标", "硬判据", 0.06, 72, "h"),
                ("②″ 圆盘区最高温 − 管温", "硬判据", 0.96, 5.00, "K"),
                ("②′ 管孔净流入 须为正", "硬判据", 1.79, 0, "W"),
                ("③ 法兰增量温降", "目标", 5.38, 10.00, "K"),
                ("管 J 电流密度", "硬判据", 9.51, 12.00, "A/mm²") };

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html lang=""zh""><head><meta charset=""utf-8"">
<title>Pt_Optimize 使用说明</title><style>
:root{--bg:#F4F6F7;--card:#FFF;--ink:#12171A;--ink2:#3D4B53;--muted:#68767E;
--rule:#D2DADE;--pt:#E8D9A8;--ring1:#E9A159;--ring2:#F0C79A;--tube:#C9D3D8;
--clamp:#2C7A8C;--ok:#2C6B58;--hot:#C2570F;--dim:#9AA7AE}
@media(prefers-color-scheme:dark){:root{--bg:#0E1216;--card:#161C21;--ink:#E7EEF1;
--ink2:#B3C0C7;--muted:#7E8D95;--rule:#28333A;--pt:#6B5C33;--ring1:#A6702F;--ring2:#7A5A38;
--tube:#33424A;--clamp:#4FA8BC;--ok:#6FC0A4;--hot:#F0904A;--dim:#5C6A72}}
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
text.lbl{font:11px 'Microsoft YaHei UI',sans-serif;fill:var(--ink)}
text.dim{fill:var(--muted);font-size:10px}
text.clamp{fill:var(--clamp)}
text.onTube{fill:var(--ink);font-size:10px}
table{border-collapse:collapse;width:100%;font-size:.88rem;background:var(--card);
border:1px solid var(--rule);border-radius:4px;overflow:hidden}
th,td{padding:8px 12px;text-align:left;border-bottom:1px solid var(--rule)}
th{font-size:.76rem;color:var(--muted);background:var(--bg)}
tr:last-child td{border-bottom:none}
td.n{font-family:Consolas,monospace}
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

        sb.Append("<h2>1. 整线布置</h2>");
        sb.Append($"<div class=\"fig\">{SvgLine(fd)}" +
                  "<div class=\"cap\">三段铂管串联，四片法兰兼作电极。中间两片是<b>共用片</b>——" +
                  "两侧段电流相位差 120°，它承担 √3 倍电流，发热 ∝ I² ⇒ 现场失效都卡在这两片。</div></div>");

        sb.Append("<h2>2. 法兰几何</h2>");
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

        sb.Append($"<h3>径向剖面（入口片，板厚 {fd.TabThickMm[0]:0.00} mm）</h3>");
        sb.Append($"<div class=\"fig\">{SvgSection(fd, 0)}" +
                  $"<div class=\"cap\">自管孔向外到盘缘，<b>1:1，未放大</b>。关于中面对称。<br>" +
                  $"环内级 = 板厚 × {fd.RingMul[0]:0.00}，环外级 = 板厚 × {fd.RingMulOuter(0):0.000}。<br>" +
                  $"横轴是<b>半径</b>：从管壁往外切一刀。三个厚度依次是环内级 {fd.TabThickMm[0] * fd.RingMul[0]:0.00}、" +
                  $"环外级 {fd.TabThickMm[0] * fd.RingMulOuter(0):0.00}、板身 {fd.TabThickMm[0]:0.00} mm。<br>" +
                  $"<b>注意板身那一带在盘缘<i>右边</i></b>：环外级外半径 {fd.RingRadiiMm[1]:0.0} mm " +
                  $"已经越过盘缘 {fd.DiscRadiusMm:0.0} mm ⇒ 圆盘上从孔到缘全被两级环占满，" +
                  $"板厚 {fd.TabThickMm[0]:0.00} mm 只出现在舌片上。这就是环倍率为何是个强旋钮：" +
                  $"它动的不是「孔边一圈」，是整个圆盘。</div></div>");

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

        sb.Append("<h2>3. 判据表怎么读</h2>");
        sb.Append("<table><tr><th>判据</th><th>类别</th><th>实际</th><th>限值</th><th>裕度</th></tr>");
        foreach (var (n, k, a, l, u) in crit)
            sb.Append($"<tr><td>{n}</td><td>{k}</td><td class=\"n\">{a:0.00} {u}</td>" +
                      $"<td class=\"n\">{(Math.Abs(l) < 1e-9 ? "> 0" : l.ToString("0.00"))}</td>" +
                      $"<td>{Bar(a, l)}</td></tr>");
        sb.Append("</table>");
        sb.Append("<div class=\"note\"><b>裕度这一列比「✓」有用。</b>" +
                  "本项目最常见的错就是<b>贴着限值判过与不过</b>——" +
                  "曾用 0.02–0.08 K 的差别决定了 700 g 铂金，而那点温差只对应 <b>14 mW</b>、" +
                  "占段功率 5 ppm，现场任何仪器都测不出来。<br>" +
                  "看到 ✓ 先问两句：这个裕度比<b>数值噪声</b>大吗？比<b>现场能分辨的尺度</b>大吗？</div>");

        sb.Append("<h3>限值的出处（每条都必须有）</h3><table>" +
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

        sb.Append("<h2>4. 收敛信息</h2>");
        sb.Append($"<p>本档外层耦合<b>剩余误差估计 {fd.ResidualK:0.00} K</b> —— 这是「距不动点」的估计 " +
                  "<code>δ·r/(1−r)</code>，<b>不是</b>「相邻两轮变化 δ」。<br>" +
                  "曾经用 δ 当收敛判据：δ=1.36 时报「5 轮收敛」，而真实剩余误差约 34 K。" +
                  "界面在判据表上方打这一行；<b>未收敛时会打「下面每个数都不可引用」——那是字面意思。</b></p>");

        sb.Append("<h2>5. 三条铁律</h2><ol>" +
                  "<li><b>判据只有一个来源</b>：<code>LineRunner.Judge</code>。界面/命令行/报告只读结果，不得自己重算。</li>" +
                  "<li><b>几何只有一个来源</b>：<code>FinalDesign</code>。曾经辅助命令各钉着几代前的几何，跑得出漂亮的数——但那是另一个设计的数。</li>" +
                  "<li><b>判据不允许消失</b>：只能过/不过/无法判定，<b>无法判定一律不算通过</b>。</li></ol>");

        sb.Append("<h2>6. 常用命令行</h2><table><tr><th>命令</th><th>用途</th></tr>" +
                  "<tr><td class=\"n\">--cli --final2</td><td>可行性阶梯：管壁从宽到窄逐档定尺寸</td></tr>" +
                  "<tr><td class=\"n\">--cli --busbarplan --wall 0.6</td><td>铜排尺寸与位置（自检整线是否全过）</td></tr>" +
                  "<tr><td class=\"n\">--cli --make3dm</td><td>两档定案 3DM + round-trip 校验</td></tr>" +
                  "<tr><td class=\"n\">--cli --hotspot --wall 0.6</td><td>峰值位置实测（坐标、局部 J、局部厚度）</td></tr>" +
                  "</table><p style=\"font-size:.88rem\"><code>--wall</code> 给了不认识的值会<b>抛异常</b>，不会静默回退。</p>");

        sb.Append("<p style=\"margin-top:40px;font-size:.82rem;color:var(--muted)\">" +
                  "完整版（含已知坑五类、现场待确认的四个数）见 <code>docs\\APP使用说明书.md</code>，" +
                  "工具条上有「打开 Markdown 版」。</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
