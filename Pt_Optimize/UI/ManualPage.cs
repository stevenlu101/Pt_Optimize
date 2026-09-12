using System.Text;
using Microsoft.Web.WebView2.WinForms;
using PtOptimize.Core;

namespace PtOptimize.UI;

/// <summary>
/// 使用说明页：WebView2 渲染的**图文**说明。
///
/// ★ 图不是画好的图片，是**从 <see cref="DesignSpec"/> 实时生成的 SVG**。
///   理由和整个项目的其余部分一样：图片一旦静态化，就成了「同一个数存两处」——
///   设计记录值一改，图还留在旧构型上，而它看起来完全正常（HANDOVER §1.8 最常见的失效）。
///   现在切换设计记录，图随之重画，两者不可能漂开。
///
/// WebView2 未装运行时的机器不会崩：本页给出提示并指向 docs/APP使用说明书.md，
/// 其余功能不受影响。
/// </summary>
public sealed class ManualPage : TabPage
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    // ★ 同 LineDesignPage：AutoSize 不关掉，Width 不生效。
    //   ⚠ 这里的 150 还是**写死的像素**，没走 UiScale ⇒ 高 DPI 下更窄。
    private readonly ToolStripComboBox _caseBox =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = UiScale.S(210) };
    private readonly Label _fallback = new()
    {
        Dock = DockStyle.Fill, Visible = false, Padding = new Padding(24),
        Font = UiScale.Ui()
    };
    private string _tempDir = "";

    /// <summary>
    /// 界面上那份**活的**参数（与参数表同一个物件）。
    /// ⚠ 说明书里的限值必须取自它，不能自己抄一份 —— 「管许用电流密度」这一项
    ///   在参数表里就是可改的，写死之后工程师一改参数，说明书当场变成假话。
    ///   null 表示拿不到（例如接线测试里直接 new ManualPage()），那就退回程序默认值。
    /// </summary>
    private readonly DesignInputs? _live;

    public ManualPage(DesignInputs? live = null) : base("使用说明")
    {
        _live = live;
        Padding = new Padding(2);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = UiScale.Ui() };
        tool.Items.Add(new ToolStripLabel("图按设计记录实时生成"));
        tool.Items.Add(_caseBox);
        RefillCaseBox();
        _caseBox.SelectedIndexChanged += (_, _) => { if (!_refilling) Render(); };
        DesignSpec.Reloaded += OnDesignSpecsReloaded;

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
        var fd = DesignSpec.All[Math.Clamp(i, 0, DesignSpec.All.Length - 1)];

        if (_tempDir.Length == 0)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Pt_Optimize_manual");
            Directory.CreateDirectory(_tempDir);
        }
        string html = Path.Combine(_tempDir, "manual.html");
        File.WriteAllText(html, BuildHtml(fd, _live), new UTF8Encoding(false));
        _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
    }

    // ════════════════════════════════════════════════════════════════════
    //  SVG：全部按 DesignSpec 的实际尺寸画，标注也取自它
    // ════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════
    //  说明书里的**名字一律从 Flow 取**，不再手抄
    //
    //  以前页签名与按钮名写死在 HTML 字符串里：界面改了而没人回来改这里时，
    //  没有任何东西会报错，说明书就开始骗人 —— 而说明书有排版、有图、有判据表，
    //  看起来就是答案，比程序落后更难被发现。
    //  现在这几个函数读的是 UI/Flow.cs，和 MainForm 建页签、各页建工具条**同一张表**。
    // ════════════════════════════════════════════════════════════════════

    private static string H(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>
    /// Flow 的文案用 <c>**</c> 标粗（与页顶横幅、判据 Note 共用同一套写法）⇒ 转成 &lt;b&gt;。
    ///
    /// ⚠ 不能像 <see cref="MainForm"/> 的横幅那样直接把 <c>**</c> 删掉：Label 没有富文本才只能删，
    ///   这里是 HTML，删掉就是把作者标出来的重点整段抹平 —— 而重点正是那些文案存在的理由。
    /// </summary>
    private static string Md(string s)
    {
        string[] parts = H(s).Split("**");
        var b = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
            b.Append(i % 2 == 1 ? "<b>" + parts[i] + "</b>" : parts[i]);
        return b.ToString();
    }

    /// <summary>按钮名。<c>Flow.Cmd</c> 查不到当场抛 —— 宁可吵着失败，也不要印一个不存在的按钮。</summary>
    private static string B(string cmdId) => "<b>" + H(Flow.Cmd(cmdId).Text) + "</b>";

    /// <summary>页签名（＝阶段名，阶段轨上那一格）。</summary>
    private static string Pg(StageId s) => "「" + H(Flow.Stage(s).Title) + "」";

    /// <summary>「在哪一格点哪个按钮」—— 格名与按钮名两个都来自 Flow。</summary>
    private static string At(string cmdId)
    {
        var c = Flow.Cmd(cmdId);
        return Pg(c.Stage) + "页 → 点" + B(cmdId);
    }

    /// <summary>
    /// 界面地图：主窗口分三块 + 阶段轨六格各带哪些命令。
    ///
    /// ★ 本图由 <see cref="Flow"/> 生成：页签名取 <c>StageSpec.Title</c>、
    ///   按钮名取 <c>CommandSpec.Text</c> —— 与真界面读的是同一张表，图与界面**不可能漂开**。
    ///   （2026-08-20 前这里是手写 SVG，注释自己写着「改了页签或工具条必须回来改这里，
    ///     图与界面漂开时没有任何东西会报错」。阶段轨改造当天那张图就整个作废了：
    ///     页签从 5 个变 6 个且全部改名、主工具条整条没了、按钮按阶段散到各页。
    ///     ⇒ 与其留一条靠人记得的警告，不如让那件事在结构上不可能发生。）
    /// </summary>
    /// <param name="omitted">
    /// 画不下而被省略的条目（空串 = 全画下了）。**调用方必须把它印在图下。**
    ///
    /// ⚠ 排不进列宽/列高的按钮若只是悄悄不画，就是一次安静失败：用户在真界面上
    ///   看到一个图上没有的按钮，只会以为自己点错了地方。旧版是按 11.6 px/字
    ///   手算「排得下几个」，算错就直接画到 viewBox 外 —— 图上看不见，但确实丢了。
    /// </param>
    private static string SvgUi(out string omitted)
    {
        var dropped = new List<string>();

        // 字号/行高。命令名 9.5 px 是量出来的：当前最长的一条「核算法兰（分钟级）」占 9 个
        // 全角宽，列内宽 88.7 px ⇒ 9.3 格，正好落进去。将来 Flow 里出现更长的名字也不会
        // 挤出框：先折行，折不下才进 dropped 并在图下注明。
        const double FSTab = 11, LHTab = 14, FSCmd = 9.5, LHCmd = 12.5;
        const double X0 = 10, TRACK_W = 600, GAP = 4;
        const int MaxTabRows = 3, MaxCmdRows = 14;      // 图还得是张图：超了就省略并注明
        double colW = (TRACK_W - GAP * 5) / 6;
        double innerW = colW - 10;

        // 文本宽度估算：ASCII 按 0.55 个字号宽，其余（汉字 / ① / ★ / ·）按 1 个。
        // ⚠ 宁可**高估**：高估只会提前触发折行与省略（看得见），低估会画到框外（看不见）。
        static List<string> Wrap(string s, double maxW, double fs)
        {
            var lines = new List<string>();
            var cur = new StringBuilder();
            double w = 0;
            foreach (char c in s)
            {
                double cw = (c < 0x80 ? 0.55 : 1.0) * fs;
                if (w + cw > maxW && cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); w = 0; }
                cur.Append(c);
                w += cw;
            }
            if (cur.Length > 0) lines.Add(cur.ToString());
            return lines;
        }

        var stages = Flow.Stages.OrderBy(s => s.Order).ToArray();

        // ── 先把所有文字排完版，才知道图该多高。六列**等高**，看起来才是一条轨。
        var tabLines = new List<string>[stages.Length];
        var cmdLines = new List<List<string>>[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            tabLines[i] = Wrap(stages[i].Title, innerW, FSTab);
            if (tabLines[i].Count > MaxTabRows)
            {
                dropped.Add($"页签名「{stages[i].Title}」只画得下前 {MaxTabRows} 行");
                tabLines[i] = tabLines[i].Take(MaxTabRows).ToList();
                tabLines[i][^1] += "…";
            }

            cmdLines[i] = new List<List<string>>();
            int rows = 0;
            for (int k = 0; k < stages[i].CommandIds.Length; k++)
            {
                var ln = Wrap(Flow.Cmd(stages[i].CommandIds[k]).Text, innerW, FSCmd);
                if (rows + ln.Count > MaxCmdRows)
                {
                    // 画不下的**逐条记账**（连同它后面的），绝不静默丢弃
                    foreach (string rest in stages[i].CommandIds.Skip(k))
                        dropped.Add($"{stages[i].Title} 的「{Flow.Cmd(rest).Text}」");
                    break;
                }
                cmdLines[i].Add(ln);
                rows += ln.Count;
            }
        }
        int tabRows = tabLines.Max(t => t.Count);
        int cmdRows = Math.Max(1, cmdLines.Max(a => a.Sum(l => l.Count)));

        double trackTop = 192;
        double tabH = 6 + tabRows * LHTab;
        double bodyTop = trackTop + tabH + 8;
        double bodyH = cmdRows * LHCmd + 10;
        double HH = bodyTop + bodyH + 8;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 620 {HH:0}\" width=\"100%\" style=\"max-width:620px\">");

        string Box(double x, double y, double w, double h, string fill) =>
            $"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{w:0.#}\" height=\"{h:0.#}\" rx=\"3\" " +
            $"fill=\"{fill}\" stroke=\"var(--rule)\" stroke-width=\"1\"/>";
        string Txt(double x, double y, string t, string cls = "lbl", string an = "start") =>
            $"<text x=\"{x:0.#}\" y=\"{y:0.#}\" text-anchor=\"{an}\" class=\"{cls}\">{H(t)}</text>";
        string TxtS(double x, double y, string t, string style, string an = "start") =>
            $"<text x=\"{x:0.#}\" y=\"{y:0.#}\" text-anchor=\"{an}\" class=\"lbl\" " +
            $"style=\"{style}\">{H(t)}</text>";
        string Badge(double x, double y, string n) =>
            $"<circle cx=\"{x:0.#}\" cy=\"{y:0.#}\" r=\"10\" fill=\"var(--clamp)\"/>" +
            $"<text x=\"{x:0.#}\" y=\"{y + 4.5:0.#}\" text-anchor=\"middle\" " +
            $"style=\"font:bold 12px sans-serif;fill:#FFF\">{n}</text>";

        // ── 上半：窗口分三块（**没有主工具条了** —— 命令按阶段散到各格自己的工具条上）
        sb.Append(Box(10, 10, 600, 158, "var(--bg)"));
        sb.Append(Box(10, 10, 600, 24, "var(--card)"));
        sb.Append(Txt(20, 26, "Pt_Optimize — 铂金直接加热 整线设计与用量优化", "lbl dim"));

        sb.Append(Box(16, 40, 176, 120, "var(--card)"));
        sb.Append(Txt(26, 58, "参数表", "lbl"));
        sb.Append(Txt(26, 74, "DesignInputs，分类折叠", "lbl dim"));
        for (int i = 0; i < 5; i++)
        {
            sb.Append($"<line x1=\"26\" y1=\"{90 + i * 13}\" x2=\"96\" y2=\"{90 + i * 13}\" " +
                      "stroke=\"var(--rule)\" stroke-width=\"5\" stroke-linecap=\"round\"/>");
            sb.Append($"<line x1=\"106\" y1=\"{90 + i * 13}\" x2=\"170\" y2=\"{90 + i * 13}\" " +
                      "stroke=\"var(--dim)\" stroke-width=\"5\" stroke-linecap=\"round\" stroke-opacity=\"0.45\"/>");
        }
        sb.Append(Badge(30, 146, "①"));

        sb.Append(Box(200, 40, 404, 56, "var(--card)"));
        sb.Append(Txt(210, 58, "状态面板", "lbl"));
        sb.Append(Txt(210, 74, "现在算的是哪条链（求解器入口 · 耗时 · 可不可交付）", "lbl dim"));
        sb.Append(Txt(210, 89, "结果新不新鲜 ／ 上次判定 ／ 门开没开、为什么没开", "lbl dim"));
        sb.Append(Badge(586, 84, "②"));

        sb.Append(Box(200, 100, 404, 60, "var(--card)"));
        sb.Append(Txt(210, 118, $"阶段轨：{Flow.Stages.Length} 格页签（下面按格放大）", "lbl"));
        sb.Append(Txt(210, 134, "每格自带页顶横幅与自己的工具条", "lbl dim"));
        sb.Append(Txt(210, 150, "锁着的格子标题带 🔒，但仍可只读进入", "lbl dim"));
        sb.Append(Badge(586, 148, "③"));

        // ── 下半：阶段轨放大。顺序、格名、每格的命令全部来自 Flow。
        sb.Append(Txt(10, 184, "阶段轨（左 → 右就是阅读顺序；每格下面列的是本格工具条上的命令）", "lbl dim"));
        for (int i = 0; i < stages.Length; i++)
        {
            double x = X0 + i * (colW + GAP);
            // 高亮哪一格 —— 不写死「整线设计」，而是问 Flow：这一格上有没有**可交付**的链。
            bool star = stages[i].Chains.Any(c => Flow.Chain(c).Deliverable);

            sb.Append($"<rect x=\"{x:0.#}\" y=\"{trackTop:0.#}\" width=\"{colW:0.#}\" " +
                      $"height=\"{tabH:0.#}\" rx=\"3\" " +
                      $"fill=\"{(star ? "var(--clamp)" : "var(--bg)")}\" stroke=\"var(--rule)\"/>");
            for (int k = 0; k < tabLines[i].Count; k++)
                sb.Append(TxtS(x + colW / 2, trackTop + 4 + (k + 1) * LHTab - 3, tabLines[i][k],
                               $"font-size:{FSTab:0.#}px" + (star ? ";fill:#FFF" : ""), "middle"));

            sb.Append(Box(x, bodyTop - 6, colW, bodyH, "var(--card)"));
            double y = bodyTop + 3;
            foreach (var ln in cmdLines[i])
                foreach (string line in ln)
                { sb.Append(TxtS(x + 5, y, line, $"font-size:{FSCmd:0.#}px")); y += LHCmd; }
        }

        sb.Append("</svg>");

        omitted = dropped.Count == 0 ? ""
                : "<br><b>⚠ 图上画不下、已省略：</b>" + H(string.Join("、", dropped)) +
                  $"（共 {dropped.Count} 条）—— 全表见下面的「逐个按钮」，一条都没少。";
        return sb.ToString();
    }

    /// <summary>法兰平面图（板面 = XZ 平面，与 3DM 的方位约定一致）。</summary>
    private static string SvgPlate(DesignSpec fd)
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
    /// <summary>舌片厚：R11（2026-09-08）起有自己的值（I/(J·舌宽)）；旧档 NaN ⇒ 与板厚同。</summary>
    private static double TongueOf(DesignSpec fd, int j) =>
        j < fd.TongueThickMm.Length && !double.IsNaN(fd.TongueThickMm[j]) ? fd.TongueThickMm[j] : fd.TabThickMm[j];

    private static string SvgIso(DesignSpec fd, int plate)
    {
        // ★ R41（2026-09-11，用户看图：「舌片/法兰/铂金管在这张图里都不是实体」）：三样都画成**有厚度的实体**——
        //   每个体三个可见面各自一个明暗（顶面亮、近侧面中、底/内壁暗），管壁也按同一倍数放大成看得见的环，
        //   管口画出内壁，管的底端封口；舌片是一块板（顶、近侧、端面），盘与两级环是带侧壁的厚板。
        double h = fd.HoleRadiusMm, R = fd.DiscRadiusMm, wall = fd.WallMm;
        // ★ 环半径夹到盘缘以内：现役记录环外级 31.8 > 盘半径 30，原来 TopRing(R, r2) 画出一个反向的环，盘身整个没了，
        //   盘看起来只是几个垫圈叠着（用户 2026-09-11 两次说「不是实体」的根子之一）。环只能占盘的一部分，盘身至少留一圈。
        double r1 = Math.Min(fd.RingRadiiMm[0], R - 2.0), r2 = Math.Min(fd.RingRadiiMm[1], R - 1.0);
        double t = fd.TabThickMm[plate];
        double ti = t * fd.RingMul[plate], to = t * fd.RingMulOuter(plate);
        bool hasRings = fd.RingMul[plate] > 1.001 || fd.RingMulOuter(plate) > 1.001;   // 倍率都是 1 = 没有台阶：整个盘就是一块厚板，别画三个同厚的垫圈
        double L = fd.TabLengthMm, w = fd.TabHalfWidthMm;

        const double KX = 0.52, KY = 0.30;          // z 轴的投影方向
        const double MAG = 7.0;                     // **只放大厚度**（板厚、管壁），见下
        const double TUBE = 40;                     // 管子露出的长度（真实尺寸，不放大）
        // 总缩放 px/mm：按舌长自适应，整块舌片都要画进画布（原来写死 3.4，舌长 163 时舌端出画布，标注被切）
        double S = Math.Min(3.4, 560.0 / (L + KX * w + R + KX * h + 24));   // 舌端近角在 z=−w，也要装进画布

        // ⚠ 放大倍数**不能写进投影**：y 既是板厚方向、也是**管子的轴向**。
        //   投影用真实 y；只把**厚度**在传入前乘 MAG。
        double tt = TongueOf(fd, plate) * MAG;   // 舌片自己的厚度（R11）
        ti *= MAG; to *= MAG; t *= MAG;
        double ri = Math.Max(h * 0.55, h - wall * MAG);   // 管内半径（管壁同倍放大，才看得见是一根有壁的管）

        double OX = 24 + (L + KX * w) * S, OY = 230;
        string P(double x, double y, double z) =>
            $"{OX + (x + KX * z) * S:0.0},{OY - (y + KY * z) * S:0.0}";

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
        const string EDGE = "stroke=\"var(--ink)\" stroke-width=\"0.8\" stroke-linejoin=\"round\"";
        // 环形顶面：外圈正向 + 内圈反向（even-odd 挖空）
        string TopRing(double rOut, double rIn, double y, string fill) =>
            $"<path d=\"{Arc(rOut, y)}Z {Arc(rIn, y)}Z\" fill-rule=\"evenodd\" fill=\"{fill}\" {EDGE}/>";
        // 实心圆面（管底封口）
        string Disk(double r, double y, string fill) => $"<path d=\"{Arc(r, y)}Z\" fill=\"{fill}\" {EDGE}/>";
        // 侧壁：near=true 画朝观察者的半圈，false 画远侧半圈（管口内壁用）。
        // ⚠ 斜投影 X = x + KX·z 下，圆柱的轮廓母线在 dX/dθ = 0 ⇒ tanθ = KX（θ ≈ 27.5°／207.5°），不在 0°／180°：
        //   原来按 [π, 2π] 画，右边少一条 0.127r 的壁（顶面悬空），左边多画了背面又用竖线闭合 —— 上下各长一个「耳朵」、
        //   中段凹进去，管看着像两头外翻的线轴，正是「壳／两个面」的观感之一（2026-09-12 审查抓到）。
        double thSil = Math.Atan(KX);            // 轮廓母线角
        string Wall(double r, double yLo, double yHi, string fill, bool near = true)
        {
            double a0 = near ? Math.PI + thSil : thSil, a1 = a0 + Math.PI;
            var b = new StringBuilder("<path d=\"");
            b.Append(Arc(r, yHi, a0, a1, 48));
            for (int i = 48; i >= 0; i--)
            {
                double a = a0 + (a1 - a0) * i / 48;
                b.Append("L ").Append(P(r * Math.Cos(a), yLo, r * Math.Sin(a))).Append(' ');
            }
            b.Append($"Z\" fill=\"{fill}\" {EDGE}/>");
            return b.ToString();
        }

        var sb = new StringBuilder();
        double W = OX + (R + KX * h) * S + 250;   // 画布宽随舌长走：右边要留得下三行标注（无台阶那行最长，170 时被裁）
        sb.Append($"<svg viewBox=\"0 0 {W:0} {OY + 200:0}\" width=\"100%\" style=\"max-width:{W:0}px\">");
        // 柱面明暗：左暗右亮，管子才像圆的
        sb.Append("<defs>" +
                  "<linearGradient id=\"gTube\" x1=\"0\" x2=\"1\" y1=\"0\" y2=\"0\">" +
                  "<stop offset=\"0\" stop-color=\"var(--tubeDark)\"/><stop offset=\"0.55\" stop-color=\"var(--tube)\"/><stop offset=\"1\" stop-color=\"var(--tubeTop)\"/></linearGradient>" +
                  "<linearGradient id=\"gTubeIn\" x1=\"0\" x2=\"1\" y1=\"0\" y2=\"0\">" +
                  "<stop offset=\"0\" stop-color=\"var(--tube)\"/><stop offset=\"1\" stop-color=\"var(--tubeDark)\"/></linearGradient>" +
                  "</defs>");

        // ★ R43（2026-09-12，用户在图上圈出「管体还是面／管体不用截开／镂空／这是两个面」并答：
        //   「一根空心实体管，再套上实体的法兰」；端片也像图里那样管从两边穿出；舌片和圆盘是同一块板切出来的）：
        //   · 管 = 空心的实体管：近侧外壁两段（盘下、盘上）+ 管口一圈壁厚环（壁厚同倍放大）+ 管腔：只露口内一小段远侧内壁、
        //     底下深色封住 —— 不截开、不镂空（之前把远侧内壁一直画到盘面，看着像一层壳）。
        //   · 法兰 = 一个体：舌片矩形与圆盘各自的顶面（同厚时在弦 x=xa 处无缝相接；舌片更厚时补一块朝 +x 的台阶面），
        //     侧壁沿轮廓的近侧连续画（舌片近侧长边 → 圆盘近侧圆弧），舌片接上来的那段圆弧没有盘缘。
        //     本投影可见的是顶面、近侧面与朝 +x 的面；舌端面朝 −x，看不见，不画。
        //   · 有台阶时环外级、环内级是叠在盘上的两级厚板：侧壁只画露出盘面的那段（整段画会盖住盘壁，又成「垫圈叠着」）。
        double tPlate = t;                       // 盘身厚（已放大）
        double tTab = tt;                        // 舌片厚（已放大）；与盘身不同厚时舌片区按自己的厚度画，交界处是一道真实的台阶
        // 舌片两条边与圆盘的交点角：平行舌 = ±(π − asin(w/R))；锥形舌 = 从舌端角点向圆盘作切线
        double thetaAttach;
        if (fd.TabTaper)
        {
            double dq = Math.Sqrt(L * L + w * w), gamma = Math.Atan2(w, -L);
            thetaAttach = gamma - Math.Acos(Math.Min(1.0, R / dq));
        }
        else thetaAttach = Math.PI - Math.Asin(Math.Min(1.0, w / R));
        double th1 = -thetaAttach, th2 = thetaAttach;          // 从近侧交点 th1 经 +x（θ=0）到远侧交点 th2
        double xa = R * Math.Cos(thetaAttach), za = R * Math.Sin(thetaAttach);   // 远侧交点 (xa, +za)，近侧 (xa, −za)

        // 圆盘近侧圆弧的侧壁：θ 从 max(th1, thSil − π) 到 thSil（朝观察者的那一半，且不含舌片接上来的那段）
        string DiscNearWall(double yLo, double yHi, string fill)
        {
            const int n = 40;
            double a0 = Math.Max(th1, thSil - Math.PI), a1 = thSil;
            var b0 = new StringBuilder("<path d=\"");
            for (int i = 0; i <= n; i++)
            {
                double ang = a0 + (a1 - a0) * i / n;
                b0.Append(i == 0 ? "M " : "L ").Append(P(R * Math.Cos(ang), yHi, R * Math.Sin(ang))).Append(' ');
            }
            for (int i = n; i >= 0; i--)
            {
                double ang = a0 + (a1 - a0) * i / n;
                b0.Append("L ").Append(P(R * Math.Cos(ang), yLo, R * Math.Sin(ang))).Append(' ');
            }
            b0.Append($"Z\" fill=\"{fill}\" {EDGE}/>");
            return b0.ToString();
        }
        string Quad(string p1, string p2, string p3, string p4, string fill) =>
            $"<path d=\"M {p1} L {p2} L {p3} L {p4} Z\" fill=\"{fill}\" {EDGE}/>";

        // 舌片顶面（按舌片厚）与圆盘顶面（按盘身厚）分开画；同厚时在弦 x=xa 处无缝相接
        string TabTop(double y) =>
            $"<path d=\"M {P(-L, y, w)} L {P(xa, y, za)} L {P(xa, y, -za)} L {P(-L, y, -w)} Z\" fill=\"var(--pt)\" {EDGE}/>";
        string DiscTop(double y)
        {
            var b0 = new StringBuilder("<path d=\"M ").Append(P(xa, y, -za)).Append(' ');
            const int n = 60;
            for (int i = 1; i <= n; i++)
            {
                double ang = th1 + (th2 - th1) * i / n;
                b0.Append("L ").Append(P(R * Math.Cos(ang), y, R * Math.Sin(ang))).Append(' ');
            }
            b0.Append($"Z\" fill=\"var(--pt)\" {EDGE}/>");
            return b0.ToString();
        }
        bool hasTab = -L < xa - 1.0;             // 舌端不出盘（几何无意义）时只画盘

        // ① 管：盘下面那一段（近侧外壁）
        sb.Append(Wall(h, -TUBE, -tPlate / 2, "url(#gTube)"));

        // ② 法兰体的侧壁：舌片近侧长边（按舌片厚）→ 圆盘近侧圆弧（按盘身厚）
        if (hasTab)
            sb.Append(Quad(P(-L, tTab / 2, -w), P(xa, tTab / 2, -za), P(xa, -tTab / 2, -za), P(-L, -tTab / 2, -w), "var(--ptDark)"));
        sb.Append(DiscNearWall(-tPlate / 2, tPlate / 2, "var(--ptDark)"));

        // ③ 顶面：舌片比盘薄（或同厚）时先舌片后圆盘（盘顶盖住弦附近被抬高的那一条）；
        //    舌片比盘厚时先圆盘，再补一块朝 +x 的台阶面，最后舌片顶面
        if (tTab <= tPlate + 1e-9)
        {
            if (hasTab) sb.Append(TabTop(tTab / 2));
            sb.Append(DiscTop(tPlate / 2));
        }
        else
        {
            sb.Append(DiscTop(tPlate / 2));
            if (hasTab)
            {
                sb.Append(Quad(P(xa, tPlate / 2, -za), P(xa, tTab / 2, -za), P(xa, tTab / 2, za), P(xa, tPlate / 2, za), "var(--ring2d)"));
                sb.Append(TabTop(tTab / 2));
            }
        }
        double topY = tPlate / 2;
        if (hasRings)
        {
            if (to > tPlate + 1e-9)
            {
                sb.Append(Wall(r2, tPlate / 2, to / 2, "var(--ring2d)"));     // 只画露出盘面的那段
                sb.Append(TopRing(r2, r1, to / 2, "var(--ring2)"));
                topY = to / 2;
            }
            if (ti > topY * 2 + 1e-9)
            {
                sb.Append(Wall(r1, topY, ti / 2, "var(--ring1d)"));
                sb.Append(TopRing(r1, h, ti / 2, "var(--ring1)"));
                topY = ti / 2;
            }
        }

        // ④ 管：盘上面那一段（近侧外壁）+ 管口：管腔底（深色）→ 口内一小段远侧内壁 → 一圈壁厚环
        double bore = 0.3 * h;                   // 只露口内这么深的内壁，够看出是空心，又不像截开的壳
        sb.Append(Wall(h, topY, TUBE, "url(#gTube)"));
        sb.Append(Disk(ri, TUBE - bore, "var(--tubeDark)"));
        sb.Append(Wall(ri, TUBE - bore, TUBE, "url(#gTubeIn)", near: false));
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
        Lead(-(h + ri) / 2, TUBE, 0, -60, -18, $"铂管 Ø{2 * (h - wall):0} 壁 {wall:0.0}", "end");
        if (hasRings)
        {
            Lead((h + r1) / 2, ti / 2, 0, 24, -62, $"环内级 {ti / MAG:0.00}");
            Lead((r1 + r2) / 2, to / 2, 0, 58, -38, $"环外级 {to / MAG:0.00}");
            Lead((r2 + R) / 2, t / 2, 0, 86, -12, $"板身 {t / MAG:0.00}");
        }
        else
            Lead((h + R) / 2, t / 2, 0, 70, -30, $"盘身厚 {t / MAG:0.00}（无台阶）");
        Lead(-L * 0.6, tt / 2, 0, -10, 62, $"舌片 {L:0}×{2 * w:0}（接铜排）", "end");
        Lead(R * Math.Cos(-Math.PI / 3), 0, R * Math.Sin(-Math.PI / 3), 60, 40, $"盘 Ø{2 * R:0}");

        sb.Append($"<text x=\"{W - 8:0}\" y=\"18\" text-anchor=\"end\" class=\"lbl dim\">" +
                  $"轴测示意　厚度方向放大 {MAG:0}×（板厚、环厚、管壁同倍，比例关系真实）</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string SvgSection(DesignSpec fd, int plate)
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

        // 角焊缝：**直接调 Core 那一份**（2026-08-24 收敛）。
        //   本处原来自己抄了一遍 `hw(d) = a − √(a²−(d−a)²)`，
        //   靠注释写着「与 Core/PlateCurrent2D.ThicknessAt 同一式子」—— 而没有任何东西在验。
        // ⚠ 这张剖面此前**根本没画焊缝**。而管孔边正是它最厚的地方（环内级厚 + 2a），
        //   图上却只有 ti —— 图与所交付的件、与 FE 实际算的厚度**三者不一致**。
        //   那次就是「同一个式子存三处然后悄悄漂开」的实例，所以这次不留第二份。
        double aw = Math.Max(t, wall);
        double tt = TongueOf(fd, plate);
        double Zone(double r) => r <= r1 ? ti : r <= r2 ? to : r <= R ? t : tt;   // 盘缘之外是舌片：R11 起舌片有自己的厚度
        double Hw(double d) => FlangePlate.WeldFilletHeightMm(d, aw);

        double tMax = Math.Max(ti + 2 * aw, Math.Max(ti, Math.Max(to, Math.Max(t, tt))));
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

    /// <summary>
    /// 整线布置：**n 段管 + n+1 片法兰**（侧视，管轴 = Y）。
    ///
    /// ⚠ 2026-09-03 改：原来整段写死「三段 300 mm、四片、HC1 1150/HC2 1080/HC3 1050」——
    ///   连控温点都是**字面量**。段数可调、每段长度可单独设定之后，这张图会**画错**：
    ///   分 4 段时它照旧画三段，而说明书是最容易被当成结论直接引用的一份东西。
    ///   ⇒ 段数、每段长度、控温点、片厚全部从 <see cref="DesignSpec"/> 读。
    /// </summary>
    private static string SvgLine(DesignSpec fd)
    {
        const double tubeId = 50;
        int nSeg = fd.SegmentCount, nPl = fd.FlangeCount;
        // 每段各自的长度（逐段，用户 2026-09-03）。缺就按第一段补，别让图裂开。
        double[] segLen = new double[nSeg];
        for (int i = 0; i < nSeg; i++)
            segLen[i] = i < fd.SegLengthMm.Length && fd.SegLengthMm[i] > 0
                      ? fd.SegLengthMm[i] : 300.0;
        // 每片法兰的轴向位置 = 前面各段长度之和
        double[] at = new double[nPl];
        for (int j = 1; j < nPl; j++) at[j] = at[j - 1] + segLen[j - 1];
        double total = at[nPl - 1];

        double ro = tubeId / 2 + fd.WallMm, R = fd.DiscRadiusMm, L = fd.TabLengthMm;
        double y0 = -30, y1 = total + 30;
        double s = 620.0 / (y1 - y0);
        double H = (2 * (R + L * 0.12) + 40) * s + 46;
        double mid = H / 2;
        string PX(double y) => ((y - y0) * s).ToString("0.0");
        string PY(double r) => (mid - r * s).ToString("0.0");

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 620 {H:0}\" width=\"100%\" style=\"max-width:620px\">");
        for (int i = 0; i < nSeg; i++)
            sb.Append($"<rect x=\"{PX(at[i])}\" y=\"{PY(ro)}\" width=\"{segLen[i] * s:0.0}\" " +
                      $"height=\"{2 * ro * s:0.0}\" fill=\"var(--tube)\" stroke=\"var(--ink)\" stroke-width=\"1\"/>");

        for (int j = 0; j < nPl; j++)
        {
            // 片名与页面同一个口径：首=入口、末=出口、中间共用k
            string nm = j == 0 ? "入口" : j == nPl - 1 ? "出口" : "共用" + j;
            double t = j < fd.TabThickMm.Length ? fd.TabThickMm[j] : 0;
            double wPx = Math.Max(3, t * s * 6);          // 法兰厚度放大以便看清
            double xPx = (at[j] - y0) * s - wPx / 2;
            sb.Append($"<rect x=\"{xPx:0.0}\" y=\"{PY(R)}\" " +
                      $"width=\"{wPx:0.0}\" height=\"{2 * R * s:0.0}\" " +
                      "fill=\"var(--pt)\" stroke=\"var(--ink)\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{PX(at[j])}\" y=\"{mid - R * s - 8:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl\">{nm} t{t:0.00}</text>");
        }

        for (int i = 0; i < nSeg; i++)
            sb.Append($"<text x=\"{PX(at[i] + segLen[i] / 2)}\" y=\"{mid + 4:0.0}\" " +
                      $"text-anchor=\"middle\" class=\"lbl onTube\">HC{i + 1} {fd.SetpointC[i]:0} °C</text>");

        // 段长：全一样就说一个数，不一样就逐段列 —— 别把「各段可以不同」这件事藏起来
        bool same = segLen.All(x => Math.Abs(x - segLen[0]) < 1e-9);
        string lenTxt = same
            ? $"{nSeg} 段各 {segLen[0]:0} mm"
            : $"{nSeg} 段：" + string.Join(" / ", segLen.Select(x => x.ToString("0"))) + " mm";
        sb.Append($"<text x=\"{PX(total / 2)}\" y=\"{H - 8:0.0}\" text-anchor=\"middle\" class=\"lbl dim\">" +
                  $"管 Ø{tubeId:0} × 壁 {fd.WallMm:0.0}　{lenTxt}　（法兰厚度已放大以便看清）</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <param name="lessIsBetter">
    /// true = 判据是「≤ 限值」（裕度 = 限−实）；false = 「≥ 下界」（裕度 = 实−限）。
    ///
    /// ★★★ 2026-08-17 修：这个参数**一直是声明了却从没用**的 —— 函数体无条件按
    ///   「越小越好」算。加进判据 ⑤（舌片自由段 **≥** 100 mm）之后当场暴露：
    ///   自由段做到 150 mm（更宽裕）会被算成 (100−150)/100 = −50 %，
    ///   显示成「**超 50 %**」—— 把一个更安全的设计显示成违规。
    ///   声明了却不用的参数比没有更危险：它让人以为这里已经考虑过方向了。
    /// </param>
    private static string Bar(double actual, double limit, bool lessIsBetter = true)
    {
        double raw = lessIsBetter ? limit - actual : actual - limit;

        // ★★★★★ 2026-09-02：**越限必须画成越限**，不许被 Clamp 抹成「余量 2 %」。
        //
        //   原来两行都会把「不过」渲染成「看起来还过」：
        //     ① Math.Clamp(raw/limit*100, 2, 99) —— ③ = 10.33 / 限 10 ⇒ raw = −0.33
        //        ⇒ Clamp(−3.3, 2, 99) = **2** ⇒ 画出一根小条子写「2 %」，
        //        读起来是「很紧但还过」，而实际是不过。
        //     ② 限值为 0 那一支（管孔净流入 须为正）**根本不看 actual**，一律 60 % +
        //        「方向安全」⇒ 净流入为负（热往管里灌，正是烧断方向）时，
        //        说明书照样写着「方向安全」。
        //   两条都是同一种病：**把失败渲染成通过**。而这张表有排版、有裕度条，
        //   看起来就是答案 —— 越像答案的东西，说错话的代价越大。
        if (raw < 0)
            return "<span class=\"bar tight\"><i style=\"width:100%\"></i></span>" +
                   "<span class=\"pct\"><b>越限</b></span>";

        // ★★ 条子宽度要下限（太窄就看不见），**数字不要** —— 2026-09-02 抓图抓到：
        //   舌片自由段 100.000 / 下界 100.00 真实余量是 **0**，而 Clamp 把标签也夹成了
        //   「余量 2 %」。那是「刚好贴着下界」被说成「还有一点」——同一族的粉饰。
        //   ⇒ 宽度用夹过的，印出来的百分比用**真值**。
        double truePct = Math.Abs(limit) < 1e-9 ? double.NaN
                       : raw / Math.Abs(limit) * 100;
        double pct = Math.Abs(limit) < 1e-9 ? 60 : Math.Clamp(truePct, 2, 99);
        string cls = pct < 15 ? "bar tight" : "bar";
        return $"<span class=\"{cls}\"><i style=\"width:{pct:0}%\"></i></span>" +
               $"<span class=\"pct\">{(double.IsNaN(truePct) ? "方向安全" : truePct.ToString("0") + " %")}</span>";
    }

    /// <summary>
    /// 生成说明书 HTML。**public 是故意的**：`--cli --manual` 要能不开 GUI 就导出，
    /// 否则「图对不对」只能靠肉眼开窗口看 —— 那不是可复核的验证。
    /// </summary>
    public static string BuildHtml(DesignSpec fd, DesignInputs? live = null)
    {
        // ★ 判据值**只从 DesignSpec 取**，本页不再自己抄一份。
        //
        // 这里原来硬编码了两档各五个数，抄的是设计记录当天（08-15）那次运行 ——
        // 而收敛度量与 ②″ 限值都是在那之后才改的。08-16 用「▶ 复现设计记录」重跑发现
        // ②′ 与 ③ 两项对不上，**且两档之间的大小关系是反的**：
        // 抄的说 0.8 档 ③ 更小（5.38 < 6.29），实算是 0.8 档 ③ 更大（6.18 > 5.30）。
        // 判定结论没变（两档仍全过），但「哪一档在 ③ 上更宽裕」这句话说反了。
        // ⇒ 又一次「同一个数存两处然后悄悄漂开」。收敛到一处才不会再犯。
        // ⚠ 2026-08-17 判据从五条加到**七条**（⑤ 装配、⑥ 可造）。
        //   本表若不跟着加，说明书就会展示一个「五条全过」的漂亮结论 ——
        //   而正是 ⑤ 把旧设计记录判掉的。**说明书漏一条判据，比程序漏一条更难被发现**：
        //   它有排版、有图，看起来就是答案。
        double tangentM = Math.Sqrt(Math.Max(0, fd.DiscRadiusMm * fd.DiscRadiusMm
                        - Math.Min(fd.TabHalfWidthMm, fd.DiscRadiusMm)
                        * Math.Min(fd.TabHalfWidthMm, fd.DiscRadiusMm)));
        double weldLegM = Math.Max(fd.TabThickMm.Max(), fd.WallMm);
        // 末位 less = 判据方向：true 是「≤ 限值」，false 是「≥ 下界」。
        // ⚠ 方向必须逐条写明 —— ⑤ 是唯一一条「越大越好」的，漏了就会把更安全的设计显示成违规。
        // ★★★ 2026-08-30：**去掉判据代号**（用户：「UI 内严禁使用 ②′ 这类的表示，
        //   工程师看不懂」）。此前这张表逐行带着 ①②′②″③⑤⑥ —— 而页签上的**阶段号**
        //   也是 ①–⑤，形状相同、含义无关（判据 ⑤ 是舌片自由段，阶段 ⑤ 是交付），
        //   摆在同一个界面上必然误读。
        var crit = new (string n, string k, double a, double l, string u, bool less)[]
        {
            ("升温到位用时（集总，参考）", "参考",  fd.RampH,      72,    "h",      true),
            ("圆盘区最高温 − 管温",    "硬判据", fd.DiscOverK,  5.00,  "K",      true),
            ("管孔净流入 须为正",      "硬判据", fd.HoleFluxW,  0,     "W",      false),
            ("法兰增量温降",           "目标",   fd.FlangeDipK, 10.00, "K",      true),
            ("管 J 电流密度",          "硬判据", fd.TubeJ,      12.00, "A/mm²",  true),
            ("舌片自由段 ≥ 下界",      "硬判据", fd.FreeTabMm,  100.0, "mm",     false),
            ("圆盘盖得住管孔＋焊脚",    "硬判据",
                fd.DiscRadiusMm - fd.HoleRadiusMm - weldLegM, 0, "mm",          false),
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
font-family:'Microsoft YaHei UI','Segoe UI',system-ui,sans-serif;font-size:MANUALFONTpx;line-height:1.75}
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
/* 首列是短标签的表：不让页签名被折成「分段核／算」这种断在半个词上的样子 */
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
        sb.Append($"<p class=\"lede\">当前设计记录：<b>{fd.Name}</b>　合计 <b>{fd.TotalMassG:0} g</b>" +
                  $"（管 {fd.TubeMassG:0} + 法兰 {fd.FlangeMassG:0}）<br>" +
                  $"咬住它的：{fd.Binding}</p>");

        // ★★★ 失效告示必须在**标题下面第一块**（2026-08-17）。
        //   说明书是最容易被当成结论直接引用的一份东西：它有排版、有图、有判据表，
        //   看起来就是「答案」。若某档已失效而说明书照常展示它的几何与铂重，
        //   那就是把一个装不上的设计包装成交付件 —— §1.8 里危害最大的一种形态。
        if (fd.Invalid.Length > 0)
            sb.Append("<div class=\"note\" style=\"border-left-width:6px\">" +
                      "<b>★★ 本档已失效，几何与铂重不可作为交付值 ★★</b><br>" +
                      System.Net.WebUtility.HtmlEncode(fd.Invalid).Replace("**", "") +
                      $"<br>自由段实测 <b>{fd.FreeTabMm:0.0} mm</b>（判据「舌片自由段」下界 100 mm）。" +
                      "<br>下面的图与判据表照常按本档画 —— <b>它们描述的是一个装不上的形状</b>，" +
                      "留在这里是为了让「哪里不成立」看得见，不是为了给它背书。</div>");

        sb.Append("<div class=\"note\"><b>下面所有图都是按当前设计记录实时画的。</b>" +
                  "换档，图跟着变。图片一旦静态化就成了「同一个数存两处」——" +
                  "设计记录值一改，图还留在旧构型上，而它看起来完全正常。</div>");

        // ════════════════════════════════════════════════════════════════
        //  操作说明（用户 2026-08-16：「APP 程式的操作说明放入 APP 内」）
        //  原来这部分只在 docs\APP使用说明书.md 里，程序里反而没有 ——
        //  说明书离开了它说明的那个东西，就是最容易漂开的一种「两处」。
        // ════════════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════════
        //  ★ R41（2026-09-11，用户工单第 3 条：「重新检查编排使用手册，对于用字遣词，让工程师能浅显易懂(简单说大白话)，
        //    对于现在的排版与例图请重新优化」）：正文整段按「先说做什么、再说为什么，一句话一个意思」重写；
        //    开发史（哪天改了什么、当时踩的坑）不进说明书（HANDOVER 里有）；程序生成的表（按钮表、判据表、
        //    判据全表、限值出处）与四张按设计记录实时画的图原样保留，只加图号与「怎么看」。
        //    被走查／测试钉住的句子（对照表两列标题、「出事那天的板厚」史料、限值取自代码）一个不动。
        // ═══════════════════════════════════════════════════════════════
        sb.Append("<h2>1. 这个软件帮你做三件事</h2>");
        sb.Append("<p>它算的是<b>铂金直接加热的一整条线</b>：几段铂管串起来通电，管与管之间用铂法兰当电极。" +
                  "你给出管子、法兰形状和工况，它回答三个问题：<b>能不能造、能不能用、要多少铂</b>。" +
                  "省铂只在前两条都过了之后才谈。</p>");
        sb.Append("<table><tr><th>你想做什么</th><th>怎么做</th><th>看哪里</th></tr>" +
                  "<tr><td><b>算一个设计要多少铂、过不过</b></td>" +
                  $"<td>{Pg(StageId.输入)}页填参数（或选一张 .3dm 图纸）→ 到{Pg(StageId.整线核算)}页点{B("core.runLine")}</td>" +
                  "<td>判据表（先看<b>裕度</b>那一列：离限值还有多远）</td></tr>" +
                  "<tr><td><b>出图纸交给加工</b></td>" +
                  $"<td>判据全过之后，到{Pg(StageId.交付)}页点{B("export.page3dm")}</td>" +
                  "<td>输出框里的「读回来对不对」和「重量对不对账」两行</td></tr>" +
                  "<tr><td><b>改个参数看看会怎样</b></td>" +
                  "<td>改左侧参数表或页面上的框，停手一秒多它会自己重算；也可以再点一次<b>核算整线</b></td>" +
                  "<td>判据表 + 三张场图</td></tr>" +
                  "<tr><td><b>给一个形状，让它自己定厚度并说出好坏</b></td>" +
                  $"<td>{Pg(StageId.输入)}页填盘径、舌宽（舌长它自己会顶到装配下界）→ {Pg(StageId.整线核算)}页点{B("core.autoThick")}</td>" +
                  "<td><b>形状体检报告</b>（第 5.1 节）：能不能造能不能用、优点、缺点、代价</td></tr>" +
                  "<tr><td><b>连盘径、舌宽、锥形、挖不挖孔都让它去搜</b></td>" +
                  $"<td>{Pg(StageId.整线核算)}页点{B("shape.search")}（有进度条，随时可取消）</td>" +
                  "<td>每算完一个形状出一行；结束后最轻的写回页面，算过的全部在「搜形状结果 ▾」里供你挑</td></tr></table>");
        sb.Append("<div class=\"note\"><b>「核算整线」读的是页面上的数。</b>页面上的几何和设计记录用的是同一套画法，" +
                  "所以「载入设计记录 → 核算整线」算出来的就是记录里的数。<br>" +
                  "页面上没有框的几项（管孔渐变环、逐片舌保温、压接段、舌根圆角、角焊缝）按设计记录值参与计算，" +
                  "<b>每次算完，输出框会把实际用了什么值逐条列出来</b>。看不见却在起作用的量最容易出事，所以一定印出来。</div>");

        sb.Append("<h2>2. 界面长什么样、按钮做什么</h2>");
        string svg = SvgUi(out string omitted);
        sb.Append($"<div class=\"fig\">{svg}" +
                  "<div class=\"cap\"><b>图 0　窗口分三块。</b>左边是<b>参数表</b>；右上是<b>状态面板</b>（现在在算什么、上次结果还算不算数、下一步点哪个）；" +
                  "右下是<b>五个页签</b>，按做事的顺序从左到右排。每个页签有自己的一排按钮，没有一条管全局的工具条 —— " +
                  "「我该点哪个」由你现在站在哪一页决定。" +
                  (omitted.Length > 0 ? $"<br>（图上画不下的按钮：{omitted}）" : "") +
                  "</div></div>");
        sb.Append("<table><tr><th></th><th>是什么</th><th>要点</th></tr>" +
                  "<tr><td>①</td><td><b>参数表</b>（左侧，分类折叠）</td>" +
                  "<td>分类名写着这一组参数<b>对整线计算有没有用</b>；名字带「只读」的两组在这里改了不算，点中任一项下方会说是谁在管它</td></tr>" +
                  "<tr><td>②</td><td><b>状态面板</b>（右上）</td>" +
                  "<td>现在算的是哪条链、结果还新不新鲜、上次判定、下一页的门开没开、<b>下一步该点哪个</b>。它上面没有可点的东西</td></tr>" +
                  $"<tr><td>③</td><td><b>页签</b>（{Flow.Stages.Length} 个）</td>" +
                  "<td>" + H(string.Join("／", Flow.Stages.OrderBy(x => x.Order).Select(x => x.Title))) +
                  "（按 <b>F1</b> 随时回到本说明）</td></tr>" +
                  "<tr><td>④</td><td><b>每页自己的按钮</b></td>" +
                  "<td>会跑很久的按钮点下去会变成「取消」，再点一次就是中止；这期间其它会起算的按钮变灰，免得两个计算互相覆盖</td></tr></table>");

        sb.Append("<table><tr><th>页签</th><th>算哪条链</th><th>按钮</th></tr>");
        foreach (var st in Flow.Stages.OrderBy(x => x.Order))
        {
            var chains = st.Chains.Where(c => c != ChainId.无).Select(Flow.Chain).ToArray();
            string ch = chains.Length == 0
                ? "—"
                : H(string.Join("；", chains.Select(c => $"{c.PlainName}（{c.Cost}）")))
                  + (chains.Any(c => c.Deliverable)
                     ? " <b>★ 算出来的数可以交付</b>" : " <span class=\"dim\">只是粗看，不可交付</span>");
            string cmds = st.CommandIds.Length == 0 ? "—"
                : string.Join("、", st.CommandIds.Select(B));
            sb.Append($"<tr><td><b>{H(st.Title)}</b></td><td>{ch}</td><td>{cmds}</td></tr>");
        }
        sb.Append("</table>");

        sb.Append("<h3>2.1 按钮分三类，记住这一条就不会点错</h3>");
        sb.Append("<div class=\"note\">" +
                  "<b>「设计记录」打头的几个 —— 不看页面上的数</b>（设计记录 ▾ / 载入设计记录 / 导出设计记录 3DM）：它们只认存好的设计记录，你在页面上改什么都影响不了它们。<br>" +
                  "<b>会起算的几个 —— 看页面上的数</b>（核算整线 / 自动定厚 / ◇ 搜形状 / 加密复算 / 细网格重解）：算的是你现在填的这组参数。<br>" +
                  "<b>其余是工具</b>（分析几何变数 / ◈ 图纸几何 → 参数 / 导出可回读 3DM / 导出本页 3DM / 保存 / 读取）。<br><br>" +
                  "所以「两个按钮给的数不一样」多半不是坏了：一个算的是存好的记录，一个算的是你刚改过的页面。" +
                  "页面上的数和记录一样时，两边给的就是同一个数。</div>");

        sb.Append("<h3>2.2 按「我想做什么」查</h3>");
        sb.Append("<table><tr><th>我想…</th><th>点哪个</th><th>看哪里</th></tr>" +
                  "<tr><td>看现在的设计记录长什么样、用多少铂</td>" +
                  "<td>设计记录 ▾ → <b>载入设计记录</b> → <b>核算整线</b></td><td>判据表；载入后页面就是记录，算出来的数与记录逐项一致（走查钉着）</td></tr>" +
                  "<tr><td>拿设计记录当起点改</td>" +
                  "<td><b>载入设计记录</b>（灌进页面）→ 改 → <b>核算整线</b></td><td>输出框会列出页面上没有框的那几项用了什么值</td></tr>" +
                  "<tr><td>我改了几个参数，想知道过不过</td>" +
                  "<td><b>核算整线</b></td><td>判据表；<b>最上面先说过没过、先解决哪一条</b></td></tr>" +
                  "<tr><td><b>给一个形状，让它自己定厚并说出好坏</b></td>" +
                  "<td><b>自动定厚</b></td><td><b>形状体检报告</b>（第 5.1 节）：能不能造能不能用 → 优点 → 缺点 → 代价</td></tr>" +
                  "<tr><td><b>盘径、舌宽、锥形、挖不挖孔都让它去搜</b></td>" +
                  "<td><b>◇ 搜形状</b></td><td>每算完一个形状出一行；最轻的写回页面，全部候选在「搜形状结果 ▾」里</td></tr>" +
                  "<tr><td>出加工图</td><td>存好的记录用<b>导出设计记录 3DM</b>；页面上这个设计用<b>导出本页 3DM</b></td>" +
                  "<td>输出框里的逐件重量对账（差应在 ±1 % 内，对不上就别拿去加工）</td></tr>" +
                  "<tr><td>想知道某个尺寸改一点会往哪边走</td><td><b>分析几何变数</b></td>" +
                  "<td>各几何量对判据的斜率（只测不调）</td></tr>" +
                  "<tr><td>换个盘径或焊法，板最薄能到多少</td><td>参考工具 ▸ <b>焊接下界小算盘</b></td>" +
                  "<td>改一格当场重算；下面一串盘径看从哪一档起由屈曲控制</td></tr></table>");

        sb.Append("<h3>2.3 第一次用，照这个顺序</h3>");
        sb.Append("<table class=\"nw\"><tr><th>步</th><th>做什么</th><th>为什么</th></tr>" +
                  "<tr><td>1</td><td>设计记录 ▾ → <b>载入设计记录</b></td>" +
                  "<td>从一个已知可行的设计出发，后面才有比较的基准</td></tr>" +
                  "<tr><td>2</td><td><b>核算整线</b></td><td>先看一眼可行的答案长什么样</td></tr>" +
                  "<tr><td>3</td><td>改盘径 / 舌宽 / 管壁 …</td>" +
                  "<td>舌长会自己顶到装配下界，不用管它；改完停手约一秒多会自动重算</td></tr>" +
                  "<tr><td>4</td><td>再点 <b>核算整线</b></td><td>它会自己走完：解一次 → 判据没过就调厚度（需要时连形状一起搜）→ 网格加密到数不再变 → 可以出图</td></tr>" +
                  "<tr><td>5</td><td>（可选）<b>◇ 搜形状</b></td><td>连盘径都还没定、或想看有没有更省铂的形状时</td></tr>" +
                  "<tr><td>6</td><td><b>导出本页 3DM</b></td><td>拿到与刚才计算<b>完全一致</b>的几何</td></tr></table>");

        sb.Append("<h3>2.4 核算整线 vs ◇ 搜形状：怎么用、何时用</h3>");
        sb.Append("<div class=\"note\"><b>先点『核算整线』，它说要搜形状时再搜；想省铂再手动搜。</b></div>");
        sb.Append("<table class=\"nw\"><tr><th></th><th>核算整线</th><th>◇ 搜形状</th></tr>" +
                  "<tr><td>回答的问题</td><td>这个设计能不能用、能不能造</td>" +
                  "<td>能用的前提下，哪个盘径／舌宽／舌形最省铂</td></tr>" +
                  "<tr><td>什么时候点</td>" +
                  "<td>每次改完「① 输入」或读完图纸，都先点它</td>" +
                  "<td>① 核算整线告诉你「厚度到头，该改形状」时（它自己会去点）；" +
                  "② 当前形状已经全过，但想看有没有更轻的；" +
                  "③ 想拿一张形状表自己挑（「搜形状结果 ▾」下拉）</td></tr>" +
                  "<tr><td>它会试什么</td><td>只调厚度、舌保温、环倍率、槽、孔这些旋钮，形状不动</td>" +
                  "<td>盘径、舌宽、平行舌或锥形舌；「解法」选「两个都算」时不挖孔、挖孔两族各搜一遍、各选各的最轻，不互相比</td></tr>" +
                  "<tr><td>花多久</td><td>十几分钟到两小时，全程有进度、随时可取消</td>" +
                  "<td>几小时（两族都算再翻倍），适合下班前点</td></tr>" +
                  "<tr><td>结束时</td><td>说「可以出图」或停在哪一步、为什么</td>" +
                  "<td>最轻的写回页面（含锥形勾选）；算过的全部在下拉里</td></tr>" +
                  "<tr><td>之后做什么</td><td>到「③ 结果与出图」出图</td>" +
                  "<td>再点一次「核算整线」精算到细网格，过了才出图</td></tr></table>");
        sb.Append("<div class=\"note\"><b>两条边界：</b>" +
                  "图纸（.3dm）模式下搜形状改不了形状，要先点「◈ 图纸几何 → 参数」；" +
                  "搜形状写回的是粗网格上的解，不能直接出图，搜完必点核算整线。<br>" +
                  "<b>不需要你决定的：</b>调哪根旋钮、调多少、网格加密几档、要不要细网格重解 —— " +
                  "都在核算整线里，①②③④⑤ 状态条会告诉你它走到哪一步。</div>");

        sb.Append("<h3>2.5 逐个按钮</h3>");
        sb.Append("<table class=\"nw\"><tr><th>在哪一页</th><th>按钮</th><th>做什么</th><th>耗时</th></tr>");
        foreach (var st in Flow.Stages.OrderBy(x => x.Order))
        {
            var cmds = st.CommandIds.Select(Flow.Cmd).ToArray();
            if (cmds.Length == 0) continue;
            for (int i = 0; i < cmds.Length; i++)
            {
                var c = cmds[i];
                sb.Append("<tr>");
                if (i == 0) sb.Append($"<td rowspan=\"{cmds.Length}\"><b>{H(st.Title)}</b></td>");
                sb.Append($"<td><b>{H(c.Text)}</b></td><td>{Md(c.Tip)}</td><td>{H(c.Cost)}</td></tr>");
            }
        }
        sb.Append("</table>");
        sb.Append("<p style=\"font-size:.88rem\"><b>三条通用的：</b>" +
                  "① 会跑很久的按钮（核算整线／自动定厚／搜形状／加密复算／细网格重解）点下去会<b>变成「取消」</b>，再点一次就是中止，不必等；" +
                  "这期间其它会起算的按钮一律变灰，免得两个计算抢机器、互相覆盖结果。" +
                  "② 进度条右边那行字会说<b>现在算到第几轮</b>；搜形状的进度条看得出还剩多少。" +
                  "③ <b>改参数会自动取消正在跑的那次</b> —— 参数一动，那次的结果本来就已经过期了。</p>");

        sb.Append("<h3>2.6 不用记流程：界面会告诉你下一步</h3>");
        sb.Append("<div class=\"note\">右上角状态面板最下面有一行 <b>「下一步 → 点『…』」</b>，该点的那个按钮同时<b>加粗加淡蓝底</b>。" +
                  "点那一行会带你切到对应页签并让按钮闪两下 —— <b>它不会替你点</b>：自动定厚会改你的输入、搜形状要跑几十分钟、出图会写文件，这些该由你按下去。</div>");
        sb.Append("<table class=\"nw\"><tr><th>现在的状态</th><th>它会指向</th></tr>"
                + "<tr><td>还没算过</td><td><b>核算整线</b></td></tr>"
                + "<tr><td>上次没收敛</td><td><b>核算整线</b>（那组数一个都不能用）</td></tr>"
                + "<tr><td>算完之后参数又动过</td><td><b>核算整线</b> —— 现在显示的「全过」说的是上一组参数</td></tr>"
                + "<tr><td>判据没全过，卡的是热和电（管孔净流入／圆盘区最高温／法兰增量温降／管 J）</td><td><b>自动定厚</b></td></tr>"
                + "<tr><td>判据没全过，卡的是<b>几何</b>（舌片自由段／圆盘盖得住管孔）</td>"
                + "<td><b>◇ 搜形状</b> —— 厚度改不动几何，白跑</td></tr>"
                + "<tr><td>.3dm 模式、图纸还没分析</td><td><b>分析几何变数</b> —— 不先分析，几何两条判据是「无法判定」，白跑一次分钟级的计算</td></tr>"
                + "<tr><td>判据全过、而且是当前参数的解</td><td><b>导出本页 3DM</b></td></tr>"
                + "<tr><td>正在算</td><td>不指 —— 那一行改成「正在算：…」</td></tr></table>");

        sb.Append("<h3>2.7 设计记录是文件，可以自己存</h3>");
        sb.Append("<div class=\"note\">"
                + "「③ 结果与出图」页的 <b>另存为设计记录</b> 把当前这个解连同判据值存成一个档（finaldesigns 文件夹里的 .fd.json），所有数由程序填，不用手抄。<br>"
                + "超标的设计也存得下：存之前会把超标的项列给你看，由你决定存不存；存下的档会记着你担了这个风险。<br>"
                + "存完新档<b>立刻</b>出现在设计记录下拉里，不用重启。读档失败不会静默跳过，会明说。</div>");

        sb.Append("<h3>2.8 左侧参数表：分类名写着改了算不算</h3>");
        sb.Append("<div class=\"note\">"
                + "整线计算有一批参数<b>不看参数表</b>：有的由「① 输入」页上的框决定，有的由程序自己算出来。"
                + "所以分类名直接写着「只读 —— 由 ① 页控件决定」「只读 —— 由程序自己算出来」，在这两组里改了不算；点中任一项，下方会说明是谁在管它。<br>"
                + "这些项<b>没有被藏起来</b>：藏起来你就不知道它们在起作用，那比「看得见但写着只读」危险得多。</div>");

        sb.Append("<h3>2.9 用自己的 Rhino 图纸（.3dm 模式）</h3>");
        sb.Append("<div class=\"note\">"
                + "「① 输入」页「法兰几何来源」选 <b>Rhino .3dm 文件</b>，选一张图（各片同一张图，厚度各自解），填图层名（默认「法兰」）。"
                + "段数在「铂金管加热段数」框里定，法兰片数 = 段数 + 1，每段管长在段表里。此后盘径／舌长／舌宽由图纸给定，页面上会禁用它们。<br>"
                + "<b>顺序要对：先「分析几何变数」，再「核算整线」。</b>"
                + "分析会从图纸的厚度场认出管孔、盘径、舌片尺寸、各级台阶厚度，也认得出锥形舌和舌根加厚段 —— "
                + "「舌片自由段」与「圆盘盖得住管孔」两条几何判据要靠它才判得了；不先分析，这两条是「无法判定」，而<b>无法判定不算通过</b>，出图那一页永远开不了。</div>");
        sb.Append("<table class=\"nw\"><tr><th>这个模式下不一样的地方</th><th>说明</th></tr>"
                + "<tr><td>四个厚度框的含义</td><td>不再是<b>板厚 mm</b>，而是<b>厚度倍数</b>（图纸整体 ×k，1.0 = 按原尺寸）。切换模式时两组值会自动交换</td></tr>"
                + "<tr><td>舌保温 mm（.3dm）</td><td>可调；默认 0 = 裸舌</td></tr>"
                + "<tr><td>「自动定厚」</td><td>逐级定厚（只调各级厚度倍数）。<b>要先分析</b>，否则没有分级可调</td></tr>"
                + "<tr><td>「◇ 搜形状」</td><td>禁用 —— 形状由图纸给定；要搜形状先点「◈ 图纸几何 → 参数」把图纸变成参数</td></tr>"
                + "<tr><td>出图</td><td>「导出本页 3DM」逐片按各自倍数另存：轮廓、管孔、开槽、各级台阶半径全不动，只有厚度按倍数变</td></tr></table>");

        sb.Append($"<h3>2.10 {Pg(StageId.参考工具)}里有什么</h3>");
        sb.Append($"<p>这一页上的东西不在主线上，「下一步」从头到尾不会指它们，但要查时随时进得去。这里算的是<b>解析粗看</b>，不解温度场；可交付的数一律以{Pg(StageId.整线核算)}页为准。</p>");
        sb.Append("<table class=\"nw\"><tr><th>子页</th><th>用途</th></tr>" +
                  "<tr><td><b>升温可达性趋势</b></td><td>5 档保温 × 4 档壁厚，闭式算升温时间与电流密度，几毫秒出结果，用来快筛</td></tr>" +
                  "<tr><td><b>分段核算</b></td><td>逐段填温度／水头／牌号／壁厚，出强度与铂重（即时）。「核算法兰」把法兰算进来才是总铂</td></tr>" +
                  "<tr><td><b>单段报告</b></td><td>「计算 (F5)」解一段管的报告，不含法兰</td></tr>" +
                  "<tr><td><b>轴向剖面</b></td><td>单段温度沿管轴的分布（随 F5 刷新）</td></tr>" +
                  "<tr><td><b>焊接下界小算盘</b></td><td>板薄到多少焊完会翘（屈曲）、焊法最薄能焊到多少（烧穿），两条取大就是圆盘板厚的工艺下界。独立的算盘，不进计算链、不改页面上的数</td></tr>" +
                  "</table>");

        sb.Append("<h2>3. 整线布置</h2>");
        sb.Append($"<div class=\"fig\">{SvgLine(fd)}" +
                  "<div class=\"cap\"><b>图 1　整线示意。</b>几段铂管串联，管与管之间的铂法兰兼作电极。中间的<b>共用片</b>两侧各接一段，" +
                  "两段电流相位差 120°，它要承担 √3 倍电流，发热是端片的 3 倍 ⇒ 现场失效多半出在共用片上。</div></div>");

        sb.Append("<h2>4. 法兰长什么样</h2>");
        sb.Append($"<div class=\"fig\">{SvgPlate(fd)}" +
                  $"<div class=\"cap\"><b>图 2　法兰平面图。</b>板面在 XZ 平面、厚度沿 Y（与 3DM 图纸的方位一致）。" +
                  $"舌根圆角 R{fd.TabFilletMm:0} 不是装饰：电流最挤的地方就在这个凹角上。<br>" +
                  (fd.RingMul[0] > 1.001 || fd.RingMulOuter(0) > 1.001
                   ? $"管孔外面有<b>两级渐变环</b>（倍率 ×{fd.RingMul[0]:0.00}）压制孔周电流集中；环的半径和厚度都是相对量（相对管孔、相对板厚），板变厚环跟着变。" +
                     $"环外级外半径 {fd.RingRadiiMm[1]:0.0} mm 大于盘半径 {fd.DiscRadiusMm:0.0} mm ⇒ 两级环几乎盖满整个圆盘。"
                   : $"本档<b>没有渐变环</b>（倍率 1.00 = 等厚）：舌片加宽到 {2 * fd.TabHalfWidthMm:0} mm 之后，电流从管孔进来有足够的截面可走，孔周不再拥挤，" +
                     $"圆盘区最高温只有 {fd.DiscOverK:0.00} K（限 +5 K）。舌片一窄回去，尖峰和环都会回来。") +
                  $"</div></div>");

        sb.Append($"<div class=\"fig\">{SvgIso(fd, 0)}" +
                  $"<div class=\"cap\"><b>图 3　入口片立体示意。</b>一根空心的实体铂管（管口那一圈是壁厚），套一片实体法兰；舌片和圆盘是同一块板切出来的，管从法兰两面穿出。" +
                  (fd.RingMul[0] > 1.001 || fd.RingMulOuter(0) > 1.001
                   ? $"盘上从管孔往外是两级台阶：环内级 {fd.TabThickMm[0] * fd.RingMul[0]:0.00} → 环外级 {fd.TabThickMm[0] * fd.RingMulOuter(0):0.00} → 板身 {fd.TabThickMm[0]:0.00} mm；"
                   : $"本档没有台阶：盘身与舌片同厚 {fd.TabThickMm[0]:0.00} mm；") +
                  "舌片伸出去接铜排。<br>" +
                  $"厚度方向放大了（真实板厚 1～2 mm，1:1 会薄成一条线），但板厚、环厚、管壁用同一个倍数，谁比谁厚多少是真的。</div></div>");

        sb.Append($"<div class=\"fig\">{SvgSection(fd, 0)}" +
                  $"<div class=\"cap\"><b>图 4　入口片径向剖面（1:1，未放大）。</b>横轴是半径：从管壁往外切一刀。" +
                  $"三个厚度依次是环内级 {fd.TabThickMm[0] * fd.RingMul[0]:0.00}、环外级 {fd.TabThickMm[0] * fd.RingMulOuter(0):0.00}、板身 {fd.TabThickMm[0]:0.00} mm。<br>" +
                  $"管孔边两坨深色是<b>角焊缝</b>：焊脚 = max(板厚, 壁厚) = {Math.Max(fd.TabThickMm[0], fd.WallMm):0.00} mm 的凹圆弧，两面各一条。" +
                  $"它是叠在板厚上的额外金属，孔边真实厚度 {fd.TabThickMm[0] * fd.RingMul[0] + 2 * Math.Max(fd.TabThickMm[0], fd.WallMm):0.00} mm。" +
                  "计算里一直算着它，交付的 3DM 里也画着它。</div></div>");

        sb.Append($"<h3>{fd.FlangeCount} 片各不相同</h3><table><tr><th>片</th><th>板厚 mm</th><th>舌片厚 mm</th>" +
                  "<th>环内级</th><th>环外级</th><th>舌片保温 mm</th></tr>");
        string Nm(int j) => j == 0 ? "入口" : j == fd.FlangeCount - 1 ? "出口" : "共用" + j;
        for (int j = 0; j < fd.FlangeCount && j < fd.TabThickMm.Length; j++)
            sb.Append($"<tr><td>{Nm(j)}</td><td class=\"n\">{fd.TabThickMm[j]:0.00}</td>" +
                      $"<td class=\"n\">{TongueOf(fd, j):0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMul[j]:0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMulOuter(j):0.00}</td>" +
                      $"<td class=\"n\">{fd.TabInsulMm[j]:0.0}</td></tr>");
        double insHi = fd.TabInsulMm.Max(), insLo = fd.TabInsulMm.Min();
        sb.Append("</table><p style=\"font-size:.88rem\">" +
                  (insHi > insLo * 3
                   ? $"舌片保温各片差 <b>{insHi / Math.Max(0.01, insLo):0} 倍</b>（{insHi:0.0} vs {insLo:0.0} mm）：" +
                     "端片要靠保温保住热，共用片本身发热过剩、几乎要裸露散热。<b>不能做成同一规格。</b>"
                   : $"本档各片舌保温都在 {insLo:0.0}–{insHi:0.0} mm，<b>几乎等于不包</b>：舌片宽、电阻小、自身发热少，反而要留着散热能力才抽得动管子里的热。" +
                     "<br>舌保温不花铂（只是纤维），所以铂重只由板厚和舌片尺寸决定。") + "</p>");

        sb.Append("<h2>5. 判据表怎么读</h2>");
        sb.Append("<p>判据表每一行是一条要过的线。<b>裕度</b>是离限值还有多远：越大越稳；贴着限值的「过」不算真过。" +
                  "「记录值」是粗网格（2 mm）上算的；「加密复算后的值」是把网格一档档加细、直到数不再变之后的数 —— <b>做决定看右边那列</b>。</p>");
        double VerifOf(string name) => name.StartsWith("法兰增量温降", StringComparison.Ordinal) ? fd.VerifiedFlangeDipK
                                     : name.StartsWith("管孔净流入", StringComparison.Ordinal) ? fd.VerifiedHoleFluxW
                                     : name.StartsWith("圆盘区最高温", StringComparison.Ordinal) ? fd.VerifiedDiscOverK
                                     : double.NaN;
        bool anyVerif = !double.IsNaN(fd.VerifiedMeshMm);

        sb.Append("<table><tr><th>判据</th><th>类别</th><th>记录值<br><span class=\"m\">导航网格 2 mm</span></th>"
                + (anyVerif
                    ? $"<th>加密复算后的值<br><span class=\"m\">加密到 {fd.VerifiedMeshMm:0.000} mm"
                      + (fd.VerifiedNote.Length > 0 ? "　⚠ 见表下说明" : "") + "</span></th>"
                    : "")
                + "<th>限值</th><th>裕度<br><span class=\"m\">按复核值</span></th></tr>");
        foreach (var (n, k, a, l, u, less) in crit)
        {
            double v = VerifOf(n);
            bool hasV = !double.IsNaN(v);
            double forBar = hasV ? v : a;
            sb.Append($"<tr><td>{n}</td><td>{k}</td><td class=\"n\">{a:0.000} {u}</td>" +
                      (anyVerif ? $"<td class=\"n\"><b>{(hasV ? v.ToString("0.000") + " " + u : "—")}</b></td>" : "") +
                      $"<td class=\"n\">{(Math.Abs(l) < 1e-9 ? "> 0" : (less ? "≤ " : "≥ ") + l.ToString("0.00"))}</td>" +
                      $"<td>{Bar(forBar, l, less)}</td></tr>");
        }
        sb.Append("</table>");
        if (anyVerif && fd.VerifiedNote.Length > 0)
            sb.Append("<div class=\"note\" style=\"border-left-width:6px\">"
                    + Md(fd.VerifiedNote) + "</div>");
        if (anyVerif)
            sb.Append("<div class=\"note\"><b>两列口径不一样，看右边那列。</b>" +
                      $"本档实测：法兰增量温降 <b>{fd.FlangeDipK:0.000} → {fd.VerifiedFlangeDipK:0.000} K</b>（限值 10）。" +
                      "粗网格看着余量宽，加密之后并不宽 —— 这个差足以把「过」变成「不过」，所以出图前必须过「◆ 加密复算（算到数不再变）」。" +
                      "「核算整线」会自己做这一步。<br>" +
                      "空着「—」的几条是几何算出来的闭式判据，不随网格变，没有复核值。</div>");
        else
            sb.Append("<div class=\"note\">⚠ <b>本档还没做过加密复算</b> —— 表里的数是粗网格（2 mm）上算的。" +
                      "同类设计粗细网格能差 3 K 以上（限值 10），不算到数不再变就不知道这张表准不准。「核算整线」会自己做这一步。</div>");
        sb.Append("<div class=\"note\"><b>看到 ✓ 先问两句：</b>这个裕度比计算噪声大吗？比现场仪器能分辨的尺度大吗？" +
                  "曾经有人用 0.02～0.08 K 的差别决定 700 g 铂金，而那点温差只对应 14 mW，现场任何仪器都测不出来。</div>");

        sb.Append("<h3>5.1 形状体检报告</h3>");
        sb.Append("<p>点<b>自动定厚</b>或<b>◇ 搜形状</b>之后，判据表上方会多出一段<b>形状体检</b>。" +
                  "顺序是定死的：<b>能造能用是先决条件，省铂只在这个前提下才谈</b>。</p>");
        sb.Append("<table class=\"nw\"><tr><th>段落</th><th>回答什么</th><th>为什么这样排</th></tr>" +
                  "<tr><td><b>一、能不能造能不能用</b></td><td>硬安全线 + 板厚 vs 工艺下界</td>" +
                  "<td>不过就到此为止，后面完全不谈铂重。例子：舌长 90 那版热学全过、铂最轻，但铜排根本装不上</td></tr>" +
                  "<tr><td>二、优化后</td><td>旋钮的收敛值 + 总铂 + 与设计记录的差</td><td>—</td></tr>" +
                  "<tr><td><b>三、抽热窗口</b></td><td>各片从管子抽走的热落在窗口的哪一段</td>" +
                  "<td>「管孔净流入」与「法兰增量温降」是同一个量的两头：抽得太少热往管里灌（烧断），抽得太多把管根拉冷</td></tr>" +
                  "<tr><td>四、优点</td><td>按裕度排序，每条带实测值与位置</td>" +
                  "<td>「散热好」不算话，「圆盘区最高温裕度 104 %、峰值在 r = 28 mm」才算</td></tr>" +
                  "<tr><td>五、缺点</td><td>裕度最紧的两条 + 哪个旋钮已经顶死</td>" +
                  "<td>旋钮顶死 = 没有回旋余地</td></tr>" +
                  "<tr><td>六、代价</td><td>多花多少铂、换来了什么</td>" +
                  "<td>只讲省了多少是广告；优点与代价成对出现</td></tr></table>");

        var lim = new LineCase();
        var dfl = live ?? new DesignInputs();
        sb.Append(Criteria.Html());
        sb.Append("<h3>限值的出处（每条都有）</h3><table class=\"nw\">" +
                  "<tr><th>判据</th><th>限值</th><th>出处</th></tr>" +
                  $"<tr><td>升温（空管到目标）</td><td class=\"n\">{lim.RampHours:0} h</td><td>业主要求「不超过 3 天」</td></tr>" +
                  "<tr><td>管孔净流入</td><td class=\"n\">&gt; 0</td><td>热要从管子流进法兰；反过来就是法兰比管子热，管子会烧</td></tr>" +
                  $"<tr><td>圆盘区最高温</td><td class=\"n\">{lim.DiscOverTempMaxK:0} K</td><td>现场控温精度 ±5 K</td></tr>" +
                  $"<tr><td>法兰增量温降</td><td class=\"n\">{lim.RootDeltaMaxK:0} K</td><td>业主定的，按热电偶误差取的数</td></tr>" +
                  $"<tr><td>管 J</td><td class=\"n\">{dfl.TubeJAllowAPerMm2:0.#} A/mm²</td>" +
                  "<td>现场经验：一般 15，管壁 0.6 时 12 是极限。<b>参数表里可改</b>，本行跟着它走</td></tr>" +
                  $"<tr><td>法兰截面 J</td><td class=\"n\">设定 J + 1</td><td>设计电流除以法兰每一个必经截面的面积；按设定 J（预设 10）定尺寸，全体要小于 J + 1</td></tr>" +
                  $"<tr><td><b>舌片自由段</b></td><td class=\"n\">≥ {GeometryScreen.FreeTabMinDefaultMm:0} mm</td>" +
                  "<td>现场铜排长 100、宽 60–80 mm，自由段留 100 才装得上（业主说这是参考值，铜排可以定制）</td></tr>" +
                  "<tr><td><b>圆盘盖得住管孔＋焊脚</b></td><td class=\"n\">≥ 0</td>" +
                  "<td>能不能焊：盘半径 − 管孔半径 − 焊脚（= max(板厚, 壁厚)）。盖不住的几何料最少，优化器会主动往那里跑，所以必须有这一条拦着</td></tr>" +
                  $"<tr><td>管壁下界</td><td class=\"n\">{DesignInputs.WeldMinDefaultMm:0.0} mm</td>" +
                  "<td>手工 TIG 烧穿下界（自动 TIG 约 0.3、激光约 0.1）；圆盘板厚下界见「焊接下界小算盘」</td></tr>" +
                  $"<tr><td>· 整片热稳定</td><td class=\"n\">&gt; 1.0 ×</td>" +
                  "<td>散热随温度涨得比发热快才不会失控。参考量，不卡交付 —— 实测它永远比管孔净流入晚红</td></tr>" +
                  $"<tr><td>· 局部热稳定</td><td class=\"n\">&gt; 1.0 ×</td>" +
                  "<td>最不稳定那一格离热失控还有几倍。参考量，设计记录实测 1.9–2.0 倍</td></tr>" +
                  $"<tr><td>· 升温期法兰−管峰值</td><td class=\"n\">{lim.RampRateKPerH:0} K/h 下 215 K</td>" +
                  "<td>现场升温时法兰比管子热多少的全程最大值。215 不是通过线，是现役设备的实际量级，只作参考</td></tr>" +
                  "<tr><td>· 管↔法兰热收支</td><td class=\"n\">守恒时 0 W</td>" +
                  "<td>管子失去的热与法兰收到的热对不对得上。参考量；实测各档都差几瓦，原因是共用片被两侧各扣一次，改判定会动所有历史结果，先量着</td></tr>" +
                  "</table>");
        sb.Append("<div class=\"note\"><b>「法兰增量温降」的 10 K 是按热电偶误差定的，不是物理极限。</b>" +
                  "所以不能说「超过 10 K 也安全」，也不能说「真实限值是多少」。能说的是：设计点别贴着 10 摆，也别拿它当调节目标。</div>");

        sb.Append("<h2>6. 收敛信息</h2>");
        sb.Append($"<p>判据表上方有一行收敛信息。本档外层耦合<b>剩余误差估计 {fd.ResidualK:0.00} K</b>，说的是「离真正的解还差多少」，不是「上一轮和这一轮差多少」。" +
                  "<b>写「未收敛」时，下面每个数都不能用</b> —— 这是字面意思。</p>");

        sb.Append("<h2>7. 出图与自校</h2>");
        sb.Append("<p>「③ 结果与出图」页点<b>导出本页 3DM</b>，或参考工具页点<b>导出设计记录 3DM</b>。" +
                  "内容：几段铂管 + 各片法兰（板身 / 环外级 / 环内级 / <b>角焊缝</b> / 舌片，有叉臂时叉臂单独一层）+ 压接段参考线。" +
                  "<b>图层按片分</b>，交付时能单独调出某一片；" +
                  "也因为量厚度是沿 Y 打射线，几片正好沿 Y 排成一列，放同一层会被一次穿透、厚度加起来（当年实测 10.450 = 2.11+3.40+3.18+1.76，那是<b>出事那天的板厚</b>，不是现在的设计记录值）。</p>");
        sb.Append("<h3>写完立刻自校，三条都过才算交付件</h3><table>" +
                  "<tr><th>校验</th><th>怎么验</th><th>防的是什么</th></tr>" +
                  "<tr><td>读回来的方位</td><td>从磁盘读回，沿 Y 的跨度 = 板厚</td>" +
                  "<td>板画到了别的平面上（出过：自己写的 .3dm 再读回来量到 0 材料）</td></tr>" +
                  "<tr><td>角焊缝体积</td><td>回转体实测 vs 解析积分，差 ≤ 0.5 %</td>" +
                  "<td>焊缝形状画错</td></tr>" +
                  "<tr><td><b>重量对账</b></td><td>逐件 3DM 体积×密度 vs 计算网格体积×密度，差 ≤ 2 %</td>" +
                  "<td>孔没挖、焊角没画、环多长出去 —— 一切几何细节</td></tr></table>");
        sb.Append("<div class=\"note\"><b>为什么盯着重量：</b>只验「包围盒沿 Y = 板厚」曾经一路报「全吻合」，而实际法兰只有计算值的 43 %。" +
                  "重量是唯一把所有几何细节都卷进去的一个数，所以它是自动判据。</div>");

        sb.Append("<h2>8. 三条规矩</h2><ol>" +
                  "<li><b>判据只有一个来源</b>：整线计算判出来的表。界面、报告只读它，不自己再算一份。</li>" +
                  "<li><b>几何只有一个来源</b>：设计记录。出的图、算的场、画的示意图都从它来。</li>" +
                  "<li><b>判据不会消失</b>：只有过、不过、无法判定三种，<b>无法判定不算通过</b>。</li></ol>");

        sb.Append("<h2>9. 常见问题</h2>");
        sb.Append("<table class=\"nw\"><tr><th>现象</th><th>其实是</th></tr>" +
                  "<tr><td>「◇ 搜形状」是灰的、点不动</td>" +
                  "<td>现在是 .3dm 图纸模式，形状由图纸给定，不是可搜的量。要搜先点「◈ 图纸几何 → 参数」（鼠标停在按钮上有说明）</td></tr>" +
                  "<tr><td>选了「（已作废）…」那两档，导出 3DM 没反应</td>" +
                  "<td>故意拦住的。已作废的档不许出图，输出框会说明为什么作废</td></tr>" +
                  "<tr><td>判据全过，但数看着不对</td>" +
                  "<td>先看判据表上方那行收敛信息。写「未收敛」时，下面每个数都不能用</td></tr>" +
                  "<tr><td>两个按钮给的数不一样</td>" +
                  "<td>一个算的是存好的设计记录，一个算的是你改过的页面（第 2.1 节）</td></tr>" +
                  "<tr><td>核算整线停在「该改形状」</td>" +
                  "<td>厚度已经调到头，判据还过不了；它会自己去搜形状，或者你到「① 输入」页改盘径、舌宽、勾锥形舌片</td></tr>" +
                  "<tr><td>搜形状结果比设计记录重</td>" +
                  "<td>它保证「过」，不保证比记录省；从更好的起点重搜，或在「搜形状结果 ▾」里挑</td></tr>" +
                  "<tr><td>加密复算后从「过」变「不过」</td>" +
                  "<td>粗网格把孔边和焊脚那一圈的梯度抹平了，细网格才是真的；点「◆ 细网格重解」在细网格上重解</td></tr></table>");
        sb.Append("<div class=\"note\"><b>别被这三样骗：</b>贴着限值的「过」（看裕度）；未收敛的数（一个都不能用）；看不见却在起作用的量（输出框会逐条印出来，看一眼）。</div>");

        sb.Append("<h2>10. 现场还需确认的数</h2>");
        sb.Append("<table class=\"nw\"><tr><th>量</th><th>现在按什么算</th><th>一旦不同，影响多大</th></tr>" +
                  "<tr><td>法兰 J 的许用值</td><td>设定 J 预设 10（可改），极限 J + 1</td>" +
                  "<td>J 每改 1，法兰截面和铂重都跟着变；这是最值得现场给一个数的量</td></tr>" +
                  "<tr><td>「法兰增量温降」的物理依据</td><td>只知道 10 K 来自热电偶误差</td>" +
                  "<td>决定能否把设计点从 10 K 往外放</td></tr>" +
                  "<tr><td>铜排表面状态</td><td>按氧化铜发射率 0.7 算</td>" +
                  "<td>抛光铜只有 0.05，差 14 倍，散热段长度直接翻几倍</td></tr>" +
                  "<tr><td>焊接方法</td><td>按手工 TIG（烧穿下界 0.6 mm）</td>" +
                  "<td>自动 TIG 可到 0.3、激光 0.1，差一个量级；薄板设计正被它咬住（见焊接下界小算盘）</td></tr></table>");

        sb.Append("<p style=\"margin-top:40px;font-size:.82rem;color:var(--muted)\">" +
                  "本页即完整说明书，<b>不再需要去仓库读文档</b>（F1 随时回到这里）。" +
                  "<code>docs\\APP使用说明书.md</code> 只留「怎么启动、怎么构建」这类程序内没法讲的事，" +
                  "工具条上的「打开 Markdown 版」打开的就是它。<br>" +
                  "两处都写全 = 同一份内容存两份，迟早漂开 —— 本项目最常见的失效。</p>");
        sb.Append("</div></body></html>");
        // ★ 说明书正文字号跟着屏幕走（2026-08-17）。原来写死 15px ——
        //   WebView2 **不吃 WinForms 的字体设置**，所以外面那些 UiScale 对它一点用都没有，
        //   必须在 CSS 里换。放在最后统一替换：内容是分段 Append 的，
        //   中途替换会漏掉后面追加的段（同一个占位符将来若出现在别处也能一起换到）。
        sb.Replace("MANUALFONT", Math.Round(15 * UiScale.K).ToString("0"));
        return sb.ToString();
    }

    /// <summary>
    /// 设计记录下拉重填，**按档名保住选中项**（新档追加在后面，下标会移位）。
    ///
    /// 本页的下拉挂着 <c>Render()</c>，清空 Items 会连带触发它 ——
    /// 用 <c>_refilling</c> 抑制，重填完只渲染一次。
    /// 不抑制的话，重填期间会拿一个**中途状态的选中项**渲染一遍图，
    /// 那张图画的是哪一档全看清空到第几个，属于典型的「看起来正常的错」。
    /// </summary>
    private bool _refilling;

    private void RefillCaseBox()
    {
        string keep = _caseBox.SelectedItem as string ?? "";
        _refilling = true;
        try
        {
            _caseBox.Items.Clear();
            foreach (var fd in DesignSpec.All) _caseBox.Items.Add(fd.Name);
            int i = _caseBox.Items.IndexOf(keep);
            if (i < 0) i = Array.IndexOf(DesignSpec.All, DesignSpec.Current);
            _caseBox.SelectedIndex = Math.Max(0, i);
        }
        finally { _refilling = false; }
    }

    private void OnDesignSpecsReloaded(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired) { BeginInvoke(new Action(Refresh2)); return; }
        Refresh2();
    }

    private void Refresh2() { RefillCaseBox(); Render(); }

    /// <summary>静态事件不退订 = 旧实例被永远拿着，见 LineDesignPage 同名方法的说明。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) DesignSpec.Reloaded -= OnDesignSpecsReloaded;
        base.Dispose(disposing);
    }
}
