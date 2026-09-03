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
    private static string SvgIso(DesignSpec fd, int plate)
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
        double Zone(double r) => r <= r1 ? ti : r <= r2 ? to : t;
        double Hw(double d) => FlangePlate.WeldFilletHeightMm(d, aw);

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
            ("升温 空管到目标",        "硬判据", fd.RampH,      72,    "h",      true),
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
        sb.Append("<h2>1. 上手：三件最常做的事</h2>");
        sb.Append("<table><tr><th>你想做什么</th><th>怎么做</th><th>看哪里</th></tr>" +
                  "<tr><td><b>算一个设计要多少铂、过不过</b></td>" +
                  $"<td>{Pg(StageId.整线核算)}页填参数（或选一张 .3dm）→ 点{B("core.runLine")}</td>" +
                  "<td>下方判据表（先看<b>裕度</b>列）</td></tr>" +
                  "<tr><td><b>出图纸交给加工</b></td>" +
                  $"<td>{Pg(StageId.交付)}页点{B("export.page3dm")}</td>" +
                  "<td>输出框里的 round-trip 与质量对账</td></tr>" +
                  "<tr><td><b>改个参数试试</b></td>" +
                  "<td>改左侧参数表或本页控件 → <b>核算整线</b>（分钟级，可取消）</td>" +
                  "<td>判据表 + 三张场图</td></tr>" +
                  "<tr><td><b>给一个形状，让 APP 自己优化并说出好坏</b></td>" +
                  $"<td>{Pg(StageId.整线核算)}页填盘径/舌宽（舌长会<b>自己顶到装配下界</b>）"
                  + $" → 同一页点{B("core.autoThick")}</td>" +
                  "<td><b>形状体检报告</b>（见 §5.1）：能不能造能不能用、优点、缺点、代价</td></tr>" +
                  "<tr><td><b>连盘径都让 APP 去搜</b></td>" +
                  $"<td>{Pg(StageId.整线核算)}页点{B("shape.search")}（有进度条，随时可取消）</td>" +
                  "<td>逐个形状一行结果；结束后最轻的那个<b>写回控件</b></td></tr></table>");
        // ★ 2026-08-17（1b）改写。原文说「有两项控件表达不了、核算整线算的是另一片法兰」——
        //   1b 之后解析模式与「复现设计记录」走同一个几何构造器，界面接线测试每次都验
        //   「页面路径复现设计记录记录值」（差 0.000），旧话已经不成立。
        //   ⚠ 留着旧话比没有话更糟：它会让人以为页面上的数不可信而绕开去用别的路径。
        sb.Append("<div class=\"note\"><b>「核算整线」走的是页面上的参数，" +
                  "但几何用的是<u>和设计记录完全同一套</u>构造器</b>（2026-08-17 起）。" +
                  "所以「载入设计记录 → 核算整线」<b>能直接复现设计记录数字</b>。<br>" +
                  "本页没有控件的几项（渐变环、逐片舌保温、压接段、舌根圆角、角焊缝、等宽舌片）" +
                  "按设计记录值参与求解，<b>每次核算完输出框都会把实际用值逐条列出来</b> —— " +
                  "看不见又在起作用的量，是最容易出事的地方。<br>" +
                  "「▶ 复现设计记录」仍然保留：它<b>完全不读页面控件</b>，用于排除「页面被改过而不自知」。</div>");

        sb.Append("<h2>2. 界面在哪、按钮做什么</h2>");
        // ⚠ 图注必须跟着阶段轨改（2026-08-20）：主工具条**整条没有了**，
        //   命令按阶段散到各页；右上那块现在是状态面板，不再是单段报告。
        //   说明书落后比程序落后更难发现 —— 它有排版有图，看起来就是答案。
        string svg = SvgUi(out string omitted);
        sb.Append($"<div class=\"fig\">{svg}" +
                  "<div class=\"cap\">窗口分三块：左边<b>参数表</b>、右上<b>状态面板</b>" +
                  "（现在算哪条链、结果新不新鲜、门开没开）、右下<b>阶段轨</b>。" +
                  "命令按阶段各归各页 —— 不再有一条管全局的主工具条，" +
                  "所以「我该点哪个」由你现在站在哪一格决定。" +
                  (omitted.Length > 0 ? $"<br>（图上画不下的按钮：{omitted}）" : "") +
                  "</div></div>");

        // ★ 这张「界面地图」也从 Flow 生成 —— 2026-08-20 之前它整张写死，
        //   而阶段轨改造把页签、右上那块、主工具条**全换了**，
        //   于是它一夜之间从「说明」变成了「误导」。
        sb.Append("<table><tr><th></th><th>是什么</th><th>要点</th></tr>" +
                  "<tr><td>①</td><td><b>参数表</b>（左侧，分类折叠）</td>" +
                  "<td>分类名已标出<b>这一项对哪条链有效</b>；" +
                  "以 <b>✗</b> 打头的两组表示「在这里改了对整线链没用」——" +
                  "要么被页面控件接管，要么被求解器强制取值</td></tr>" +
                  "<tr><td>②</td><td><b>状态面板</b>（右上）</td>" +
                  "<td>现在算的是哪条链、求解器入口是谁、结果<b>还新不新鲜</b>、" +
                  "上次判定、下一格的门开没开。<b>它没有任何可点的东西</b></td></tr>" +
                  $"<tr><td>③</td><td><b>阶段轨</b>（{Flow.Stages.Length} 格）</td>" +
                  "<td>" + H(string.Join("／", Flow.Stages.OrderBy(x => x.Order).Select(x => x.Title))) +
                  "（说明书 <b>F1</b> 直达）</td></tr>" +
                  "<tr><td>④</td><td><b>每一格自己的工具条</b></td>" +
                  "<td>命令按阶段归位，<b>没有一条管全局的主工具条</b>——" +
                  "「我该点哪个」由你站在哪一格决定</td></tr></table>");

        // 逐格：这一格算哪条链、能不能交付、有哪些命令
        sb.Append("<table><tr><th>阶段</th><th>算哪条链</th><th>命令</th></tr>");
        foreach (var st in Flow.Stages.OrderBy(x => x.Order))
        {
            var chains = st.Chains.Where(c => c != ChainId.无).Select(Flow.Chain).ToArray();
            string ch = chains.Length == 0
                ? "—"
                : H(string.Join("；", chains.Select(c => $"{c.Name}（{c.EntryPoint}·{c.Cost}）")))
                  + (chains.Any(c => c.Deliverable)
                     ? " <b>★ 可交付</b>" : " <span class=\"dim\">⚠ 不可交付</span>");
            string cmds = st.CommandIds.Length == 0 ? "—"
                : string.Join("、", st.CommandIds.Select(B));
            sb.Append($"<tr><td><b>{H(st.Title)}</b></td><td>{ch}</td><td>{cmds}</td></tr>");
        }
        sb.Append("</table>");

        sb.Append($"<h3>{Pg(StageId.整线核算)}页的工具条（主力页）</h3>");
        // ════════════════════════════════════════════════════════════════
        // ★★★★★ 「整线设计」工具条：怎么用、什么时候用（2026-08-17 用户要求）
        //
        // 用户看到的是一条横排十个按钮，没有任何分组提示。而工具条上那**两条分隔线**
        // 其实已经把它们分成了三组，分组恰恰就是「何时用哪个」的答案：
        //   左组不读页面控件，中组读页面控件，右组是工具。
        // 这条区别正是当初不得不单独做「▶ 复现设计记录」的原因 —— 说明书必须先讲清它。
        // ════════════════════════════════════════════════════════════════
        // ⚠ 2026-08-17：这里原来写「照着工具条上的分隔线分组」。用户当场质疑，去抓 UI 核实：
        //   分隔线**结构上确实有三条**、位置也对，但默认渲染下只是一条 1 px 细线，
        //   在截图里几乎看不出来。**让用户去照着一条看不清的线分组是不可靠的说法**
        //   ⇒ 改成直接点名按钮。分组的依据写在按钮名字上，不写在像素上。
        sb.Append("<h3>工具条分三组（按名字记，不必去找分隔线）</h3>");
        sb.Append("<div class=\"note\"><b>先记住这一条，其余都好办：</b><br>" +
                  "<b>「设计记录」两个字打头的那几个 —— 不读页面上的控件</b>" +
                  "（设计记录 ▾ / ▶ 复现设计记录 / 导出设计记录 3DM / 载入设计记录）：" +
                  "它们只认 <code>DesignSpec</code> 里存的设计记录值，你在页面上改什么都影响不了它们。<br>" +
                  "<b>中间三个 —— 读页面控件</b>（核算整线 / 自动定厚 / ◇ 搜形状）：算的是你现在填的这组参数。<br>" +
                  "<b>最后两个是工具</b>（分析几何变数 / 导出本页 3DM）。<br><br>" +
                  "所以「同一件事两个按钮给的数不一样」通常不是 bug，是你在拿<b>左组</b>的答案" +
                  "跟<b>中组</b>的答案比 —— 而它们的输入本来就不同。" +
                  "（2026-08-17 起两组的<b>几何构造器已经统一</b>，只要页面参数等于设计记录值，两边就该给同一个数；" +
                  "给不出同一个数，就说明页面上有控件被改过。）</div>");

        sb.Append("<h3>按「我想做什么」查</h3>");
        sb.Append("<table><tr><th>我想…</th><th>点哪个</th><th>看哪里</th></tr>" +
                  "<tr><td>看现在的设计记录长什么样、用多少铂</td>" +
                  "<td>设计记录 ▾ → <b>▶ 复现设计记录</b></td><td>判据表；这条路不受页面影响，最可信</td></tr>" +
                  "<tr><td>把设计记录参数调出来当起点改</td>" +
                  "<td><b>载入设计记录</b>（灌进控件）→ 再改</td><td>输出框会列出本页没有控件的那几项用了什么值</td></tr>" +
                  "<tr><td>我改了几个参数，想知道过不过</td>" +
                  "<td><b>核算整线</b></td><td>判据表；<b>最上面先给判定与「先解决哪一条」</b></td></tr>" +
                  "<tr><td><b>给一个形状，让 APP 自己定厚并说出好坏</b></td>" +
                  "<td><b>自动定厚</b></td><td><b>形状体检报告</b>（§5.1）：能不能造能不能用 → 优点 → 缺点 → 代价</td></tr>" +
                  "<tr><td><b>连盘径/舌宽都让 APP 去搜</b></td>" +
                  "<td><b>◇ 搜形状</b></td><td>每算完一个形状出一行；结束后最轻的那个写回控件</td></tr>" +
                  "<tr><td>出加工图</td><td>设计记录构型用<b>导出设计记录 3DM</b>；本页构型用<b>导出本页 3DM</b></td>" +
                  "<td>输出框里的逐件质量对账（差应在 ±1 % 内，对不上就别出图）</td></tr>" +
                  "<tr><td>想知道某个尺寸改一点会往哪边走</td><td><b>分析几何变数</b></td>" +
                  "<td>各几何量对判据的斜率（只测不调）</td></tr></table>");

        sb.Append("<h3>典型顺序（第一次用就照这个走）</h3>");
        sb.Append("<table class=\"nw\"><tr><th>步</th><th>做什么</th><th>为什么是这个顺序</th></tr>" +
                  "<tr><td>1</td><td>设计记录 ▾ → <b>▶ 复现设计记录</b></td>" +
                  "<td>先看一眼「已知可行的答案」长什么样，后面才有比较的基准</td></tr>" +
                  "<tr><td>2</td><td><b>载入设计记录</b></td><td>把那组参数灌进控件，从一个**已知可行**的点出发改</td></tr>" +
                  "<tr><td>3</td><td>改盘径 / 舌宽 / 管壁 …</td>" +
                  "<td><b>舌长会自己顶到装配下界</b>，不用管它；改完停手约 1.5 秒会自动重算</td></tr>" +
                  "<tr><td>4</td><td><b>自动定厚</b></td><td>让求解器把三个旋钮调到位，并出体检报告</td></tr>" +
                  "<tr><td>5</td><td>（可选）<b>◇ 搜形状</b></td><td>如果连盘径都还没定，让它去搜</td></tr>" +
                  "<tr><td>6</td><td><b>导出本页 3DM</b></td><td>拿到与刚才求解<b>完全一致</b>的几何</td></tr></table>");

        sb.Append("<h3>四个常见误用</h3>");
        sb.Append("<table class=\"nw\"><tr><th>现象</th><th>其实是</th></tr>" +
                  "<tr><td>「◇ 搜形状」是灰的、点不动</td>" +
                  "<td>当前是 <b>.3dm 几何模式</b>。形状由图纸给定，不是可搜索的自由度 ⇒ "
                  + "该命令<b>不适用</b>，已按状态禁用（鼠标停上去有说明）。"
                  + "2026-08-23 之前它是「能点但点了只弹一句话」—— 那更糟</td></tr>" +
                  "<tr><td>「复现设计记录」与「核算整线」给的数不一样</td>" +
                  "<td>页面上有控件被改过（两者的几何构造器已统一，参数相同就该同数）</td></tr>" +
                  "<tr><td>选了「（已作废）…」那两档，导出 3DM 没反应</td>" +
                  "<td><b>故意拦住的</b>。已声明失效的档不许出图 —— 输出框会说明为什么失效</td></tr>" +
                  "<tr><td>判据全过，但数看着不对</td>" +
                  "<td>先看判据表<b>上方</b>那行收敛信息。写「未收敛」时，<b>下面每个数都不可引用</b>——" +
                  "那是字面意思</td></tr></table>");

        sb.Append("<h3>逐个按钮</h3>");
        // ★★ 2026-08-23：这张表改成**从 Flow 生成**。
        //   在此之前它是手写的，而它自己的注释就写着「按钮表是用户最常照着操作的一张表 ——
        //   它一旦落后，用户会去点一个不存在的按钮，或者以为某个按钮还在做它三个月前做的事」。
        //   而事实是它**已经落后了**：新增的「另存为设计记录」根本不在表里。
        //   ⇒ 改成迭代 Flow.Commands，按阶段分组。谁加按钮、谁改名，这张表自动跟上。
        sb.Append("<table class=\"nw\"><tr><th>在哪一格</th><th>按钮</th><th>做什么</th><th>耗时</th></tr>");
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
                  "① 会跑很久的按钮（核算整线／自动定厚／搜形状／复现设计记录）点下去会<b>变成「取消」</b>，" +
                  "再点一次就是中止，不必等；" +
                  "<b>其余会起算的命令这期间一律变灰</b>——三个分钟级求解同时跑，" +
                  "抢 CPU 还互相覆盖结果，分不清哪个数是谁的；" +
                  "② 进度条右边那行字会说<b>现在在算第几轮</b>，「搜形状」还是**确定式**进度条（看得出还剩多少）；" +
                  "③ <b>改参数会自动取消正在跑的那次</b> —— 参数一动，那次的结果本来就已经过期了。</p>");

        // ★ 2026-08-20/23 新增的几件事，用户看不到 git log ⇒ 得在这儿说。
        sb.Append("<h3>不用记流程：界面会告诉你下一步</h3>");
        sb.Append("<div class=\"note\">右上角状态面板底部有一行 <b>「下一步 → 点『…』」</b>，"
                + "同时把该点的那个按钮<b>加粗＋淡蓝底</b>。点那一行会<b>带你切到对应页签并让按钮闪两下</b>——"
                + "<b>它不会替你跑</b>：自动定厚会改你的输入、搜形状要跑几十分钟、交付会写文件，"
                + "这些该由你按下去。</div>");
        sb.Append("<table class=\"nw\"><tr><th>当前状态</th><th>它会指向</th></tr>"
                + "<tr><td>还没解过</td><td><b>核算整线</b></td></tr>"
                + "<tr><td>上次没收敛</td><td><b>核算整线</b>（那组数一个都不可引用）</td></tr>"
                + "<tr><td>参数在上次求解之后又动过</td><td><b>核算整线</b>——"
                + "此时的「全过」说的是<b>上一组参数</b>的事</td></tr>"
                + "<tr><td>判据没全过，卡的是热-电量（管孔净流入／圆盘区最高温／法兰增量温降／管 J）</td><td><b>自动定厚</b></td></tr>"
                + "<tr><td>判据没全过，卡的是<b>几何</b>（舌片自由段／圆盘盖得住管孔）</td>"
                + "<td><b>◇ 搜形状</b>——厚度调不动几何，白跑</td></tr>"
                + "<tr><td>.3dm 模式、图纸还没反推</td><td><b>分析几何变数</b>——"
                + "不先分析就解，「舌片自由段」与「圆盘盖得住管孔」仍是「无法判定」，白跑一次分钟级的解</td></tr>"
                + "<tr><td>判据全过且是当前参数的解</td><td><b>导出本页 3DM</b></td></tr>"
                + "<tr><td>正在算</td><td>不给——那一行改成「正在算：…」</td></tr></table>");

        // ★★ 实跑记录（2026-08-25 验收）。用户要求：「按链路提示自己跑一遍，
        //    验证提示有没有坑、数据对不对」，并把使用报告放进说明书。
        //    ⚠ 这一节的数**不是手抄的**：它来自 `UiWiring.exe --follow` 的输出，
        //      而那条命令每次都会重跑一遍。数变了就该回来改这一节。
        sb.Append("<h3>照着提示走：一次完整实跑（2026-08-25 验收）</h3>");
        sb.Append("<div class=\"note\">从<b>开箱默认</b>出发，规则只有一条："
                + "<b>每一步只看提示说该点哪个，然后就点它</b>——不看攻略、不抄近路。"
                + "结果：<b>2 步走到交付</b>。</div>");
        sb.Append("<table class=\"nw\">"
                + "<tr><th>步</th><th>提示说什么</th><th>点了什么</th><th>算出来</th></tr>"
                + "<tr><td>1</td><td>还没解过 —— 先解一次整线</td><td><b>核算整线</b></td>"
                + "<td>收敛 ✓、判据 <b>✗</b>；圆盘区最高温 296 / 5 K、管孔净流入 <b>−895</b> / 0 W；2842 g</td></tr>"
                + "<tr><td>2</td><td>卡的是热-电量 —— 用「自动定厚」</td><td><b>自动定厚</b></td>"
                + "<td>收敛 ✓、<b>判据全过</b>；管孔净流入 +1.49 W、圆盘区最高温 −0.08 K、法兰增量温降 5.67 K、管 J 7.67 A/mm²；"
                + "<b>3569 g</b></td></tr>"
                + "<tr><td>3</td><td>判据全过且是当前参数的解 —— 可以出图了</td>"
                + "<td><b>导出本页 3DM</b></td><td>（走查到此为止，不真写档）</td></tr>"
                + "</table>");
        sb.Append("<div class=\"note\">"
                + "<b>怎么读这三行</b>：开箱默认那组几何<b>本来就不该过</b>"
                + "（管孔净流入 −895 W = 热在往管子里灌，正是烧断机理）——"
                + "所以第 1 步判据红是对的，不是程序坏了。"
                + "第 2 步定尺寸把三个旋钮（板厚／舌保温／环倍率）一起调到窗口内，"
                + "管孔净流入回正、圆盘区最高温落进 ±5 K。"
                + "<b>交付前那张表与第 2 步逐字相同</b> —— "
                + "「你看到的表」和「要出的图」是同一个设计，这一条本身也在走查里验。"
                + "<br>⚠ 3569 g 比现役设计记录（0.8 档 3547 g）<b>略重</b>："
                + "定尺寸器是从<b>开箱默认</b>那个很差的起点出发的，"
                + "它保证「过」，不保证「比设计记录更省」。要更省得从更好的起点重跑，或改形状。</div>");
        sb.Append("<h4>这一版修掉的两个坑（都是跟着提示走才撞得到的）</h4>");
        sb.Append("<table class=\"nw\"><tr><th>坑</th><th>你会看到什么</th><th>已修</th></tr>"
                + "<tr><td><b>提示在「自动定厚」上死循环</b></td>"
                + "<td>定尺寸跑完明明<b>判据全过</b>，提示还在说「判据没全过，用自动定厚」，"
                + "点下去又是几十分钟，<b>永远走不到出图</b></td>"
                + "<td>定尺寸算完<b>没有把结果发布出去</b>，提示与门禁读的还是上一次那个不过的解。"
                + "更险的是：上一次若是<b>过的</b>，而定尺寸把它调坏了，门会继续开着（假绿灯）</td></tr>"
                + "<tr><td><b>「自动定厚 → 回「整线核算」重解」两步一循环</b></td>"
                + "<td>定尺寸全过 → 重解又不过（管孔净流入掉负）→ 再定尺寸又全过 → …</td>"
                + "<td>定尺寸用<b>三个</b>旋钮（板厚／舌保温／环倍率），而本页原先只带回板厚；"
                + "重解时另外两个被丢回设计记录值，管孔净流入立刻掉负。"
                + "现在三个都带回本页 ⇒ 重解<b>复现同一张表</b>，路径收得了尾</td></tr>"
                + "</table>");
        sb.Append("<div class=\"note\">"
                + "<b>舌保温与环倍率本页没有控件</b>，但它们确实参与计算 ——"
                + "所以定尺寸的输出里会<b>逐组印出来</b>，并且它们一变，"
                + "「已解（参数未变）」就会自动失效。"
                + "本项目的规矩是「<b>看不见又在起作用的量是安静失败的温床</b>」，"
                + "这两项属于「没有控件但看得见、且进新鲜度判定」。"
                + "<br><b>这份报告是机器跑出来的</b>：<code>UiWiring.exe --follow</code> "
                + "每次都会照提示重走一遍，并逐步核对"
                + "「命令存在／点得动／不打转／蓝键切得到那一页／蓝键闪得到那个按钮」。</div>");


        sb.Append("<h3>设计记录现在是**文件**，可以自己存</h3>");
        sb.Append("<div class=\"note\">"
                + "以前把一个解落成设计记录，要人工把十几个数抄进 <code>Core/DesignSpec.cs</code>，"
                + "其中五个判据记录值还得从判据表里逐个读——抄错一位要跑 8 分钟自检才知道。<br>"
                + "现在「设计记录」页有 <b>另存为设计记录</b>：把当前这个解写成 "
                + "<code>finaldesigns/*.fd.json</code>，所有数由程序填。"
                + "<b>只有判据全过、且参数没再动过时才可用</b>——"
                + "不成立的设计不该有一个「能落档」的形态。<br>"
                + "存完还要做三件事：① 把 <code>binding</code> 填上（什么咬住了它，那是工程判断，程序算不出）；"
                + "② 让我这边跑一次全档自检（A 段这一档的差须为 0.000）；"
                + "③ 提交进 git——档是回归基准，变更要被 diff 记录。<br>"
                + "存完新档<b>立刻</b>出现在两个页面的设计记录下拉里，不用重启"
                + "（2026-08-23 之前要重启——界面说「已写出」而下拉里找不到它，"
                + "看着就像没存上）。"
                + "读档失败不会静默跳过：自检门会把它算作失败——少一个档就是少一组判据。</div>");

        sb.Append("<h3>左侧参数表：分类名就写着「对哪条链有效」</h3>");
        sb.Append("<div class=\"note\">"
                + "整线链上有一批参数<b>根本不看参数表</b>：有的被「整线核算」页控件接管，有的被求解器强制取值。"
                + "所以分类名带了链号——<code>1 A·B 粗算</code>…<code>6 C 整线</code> 是真正生效的；"
                + "而 <b><code>8 ✗ 被页面/设计记录接管</code></b> 与 <b><code>9 ✗ 对整线链无效</code></b> "
                + "两组，在这里改了对「整线核算」<b>没有影响</b>——点中任一项，下方会说明是谁接管了它。<br>"
                + "⚠ 这些项<b>没有被隐藏</b>：藏起来会让没标注到的参数静默消失，"
                + "而「看不见又在起作用」比「看得见但写着无效」危险得多。</div>");

        sb.Append("<h3>用自己的 Rhino 图纸（.3dm 模式）</h3>");
        sb.Append("<div class=\"note\">"
                + "「整线核算」页「法兰几何来源」选 <b>Rhino .3dm 文件</b>，四片各选一个文件"
                + "（<b>可以重复同一个</b>），填图层名（默认「法兰」）。"
                + "此后盘径/舌长/舌宽<b>由图纸给定</b>，界面上会禁用它们。<br>"
                + "<b>顺序要对：先「分析几何变数」，再「核算整线」。</b>"
                + "分析会把厚度场反推成管孔半径、盘半径、舌端尺寸与各级台阶厚度——"
                + "「舌片自由段」与「圆盘盖得住管孔」两条几何判据要靠它才判得了。不先分析就解，"
                + "「舌片自由段」与「圆盘盖得住管孔」会是「无法判定」，而<b>无法判定不算通过</b> ⇒「交付」永远解锁不了。"
                + "（2026-08-23 之前这条路就是这样：走不到交付。）</div>");
        sb.Append("<table class=\"nw\"><tr><th>这个模式下不一样的地方</th><th>说明</th></tr>"
                + "<tr><td>四个厚度框的含义</td><td>不再是<b>板厚 mm</b>，而是<b>厚度标度 k</b>"
                + "（图纸整体 ×k，1.0 = 按原尺寸）。切换模式时两组值会自动交换——"
                + "靠数值分不开这两个量：0.9 既是合理厚度也是合理标度</td></tr>"
                + "<tr><td><b>舌保温 mm（.3dm）</b></td><td>本路径以前把舌片按<b>裸露</b>算，"
                + "而舌保温是守「管孔净流入」与「法兰增量温降」的主力旋钮 ⇒ 缺了它这条路几乎必然「法兰增量温降大幅超限」。"
                + "现在可调，<b>默认 0 仍是裸舌</b>（不改既有算例的答案）</td></tr>"
                + "<tr><td>「自动定厚」走的算法</td><td>逐级定厚（只调各级厚度比例），"
                + "不是解析几何那套 D8。<b>要先分析</b>，否则没有分级可调</td></tr>"
                + "<tr><td>「◇ 搜形状」</td><td>禁用——形状由图纸给定，不是可搜索的自由度</td></tr>"
                + "<tr><td>出图</td><td>「导出本页 3DM」逐片按各自标度另存。"
                + "<b>轮廓、管孔、开槽、各级阶梯半径全部不动，只有厚度按倍数变</b></td></tr></table>");
        sb.Append("<div class=\"note\"><b>一处近似要知道：</b>"
                + "「舌片自由段」与「圆盘盖得住管孔」用的等效几何把舌片当成<b>等宽</b>的。若你的图纸是梯形舌片，"
                + "真实的圆盘切点比等宽近似更靠内 ⇒ 真自由段<b>比报出来的更大</b>——"
                + "这个近似<b>判严不判松</b>，方向是安全的。"
                + "求解（温度场／电流场）走的是<b>真实厚度场</b>，逐格计算，不受这个近似影响。</div>");

        // ★★★ 2026-09-03 改：这一节原来叫「其余四个页签」，列的是
        //   分析／分段核算／轴向剖面／使用说明。而 2026-09-02 阶段轨由七格收成四格之后，
        //   前三个**已经不是页签**了 —— 它们是「参考工具」那一格里的面板。
        //   工程师照说明书去找「分析」页签，找不到。⇒ 标题与措辞都跟着 Flow 走。
        sb.Append($"<h3>{Pg(StageId.参考工具)}里有什么</h3>");
        sb.Append($"<p>下面三样都在{Pg(StageId.参考工具)}这一格里（不是独立页签）—— "
                + "它们不在主线上，蓝色指示从头到尾不会指它们，但要查时随时进得去。</p>");
        sb.Append("<table class=\"nw\"><tr><th>面板</th><th>用途</th></tr>" +
                  "<tr><td><b>分析</b></td><td>升温可达性、厚度灵敏度 —— 两个独立的专项核算</td></tr>" +
                  "<tr><td><b>分段核算</b></td><td>逐段填温度／水头／牌号／壁厚，出强度与铂重（<b>解析、即时</b>）。" +
                  "「核算法兰」把法兰算进来才是可交付的总铂</td></tr>" +
                  "<tr><td><b>轴向剖面</b></td><td>单段解的温度沿轴分布（随 F5 刷新）</td></tr>" +
                  "</table>");
        // ⚠ 「使用说明」是**独立页签**，不在参考工具里 —— 别把它混进上面那张表。
        sb.Append($"<p>另外，本页（{Pg(StageId.说明)}）自己就是第四格页签；"
                + "工具条上可切设计记录，图跟着重画。</p>");
        sb.Append($"<div class=\"note\"><b>{Pg(StageId.参考工具)}页上的是解析粗看，{Pg(StageId.整线核算)}是耦合数值解。</b>" +
                  $"两者数不一样很正常 —— 前者不解温度场。<b>可交付的数以{Pg(StageId.整线核算)}为准。</b></div>");

        sb.Append("<h2>3. 整线布置</h2>");
        sb.Append($"<div class=\"fig\">{SvgLine(fd)}" +
                  "<div class=\"cap\">三段铂管串联，四片法兰兼作电极。中间两片是<b>共用片</b>——" +
                  "两侧段电流相位差 120°，它承担 √3 倍电流，发热 ∝ I² ⇒ 现场失效都卡在这两片。</div></div>");

        sb.Append("<h2>4. 法兰几何</h2>");
        sb.Append($"<div class=\"fig\">{SvgPlate(fd)}" +
                  $"<div class=\"cap\">板面在 XZ 平面、厚度沿 Y（与 3DM 的方位约定一致）。" +
                  $"<b>舌根圆角 R{fd.TabFilletMm:0}</b> 不是装饰：峰值电流拥塞就发生在这个凹角上" +
                  $"（实测峰位 x≈−24.3, z≈±13）。<br><br>" +
                  (fd.RingMul[0] > 1.001
                   ? $"<b>管孔两级渐变环</b>（倍率 ×{fd.RingMul[0]:0.00}）压制孔周电流集中，" +
                     $"半径与厚度都是<b>相对量</b>（相对管孔 / 相对板厚）——" +
                     $"写成绝对值时板一变厚环就静默消失，整条优化曾因此停在离最优 28 % 的地方。<br><br>" +
                     $"<b>注意盘缘那条虚线</b>：环外级的外半径 {fd.RingRadiiMm[1]:0.0} mm " +
                     $"<b>大于盘半径 {fd.DiscRadiusMm:0.0} mm</b>，而盘孔环带只有 " +
                     $"{fd.DiscRadiusMm - fd.HoleRadiusMm:0.0} mm 宽 ⇒ <b>两级环几乎覆盖整个圆盘</b>。"
                   : $"<b>本档没有管孔渐变环</b>（倍率 1.00 = 等厚）。这是 2026-08-17 重解的结果，" +
                     $"不是漏掉了：舌片加宽到 {2 * fd.TabHalfWidthMm:0} mm 之后，电流从管孔进来有足够的" +
                     $"过流断面可走，孔周不再拥塞 —— 实测圆盘区最高温 = {fd.DiscOverK:0.00} K（限 +5 K），" +
                     $"整整低了一个数量级。<b>少一道两级台阶的机加工。</b><br>" +
                     $"⚠ 这条结论**绑在这个形状上**：舌片一窄回去，尖峰就会回来，环也要跟着回来。") +
                  $"</div></div>");

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

        // ⚠ 2026-09-03：标题与片名**按实际片数生成**。原来写死「四片」+ 四个名字 ⇒
        //   分 4 段（5 片）时说明书少列一片，而它是最容易被直接引用的一份东西。
        sb.Append($"<h3>{fd.FlangeCount} 片各不相同</h3><table><tr><th>片</th><th>板厚 mm</th>" +
                  "<th>环内级</th><th>环外级</th><th>舌片保温 mm</th></tr>");
        string Nm(int j) => j == 0 ? "入口" : j == fd.FlangeCount - 1 ? "出口" : "共用" + j;
        for (int j = 0; j < fd.FlangeCount && j < fd.TabThickMm.Length; j++)
            sb.Append($"<tr><td>{Nm(j)}</td><td class=\"n\">{fd.TabThickMm[j]:0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMul[j]:0.00}</td>" +
                      $"<td class=\"n\">{fd.TabThickMm[j] * fd.RingMulOuter(j):0.00}</td>" +
                      $"<td class=\"n\">{fd.TabInsulMm[j]:0.0}</td></tr>");
        // ⚠ 2026-08-17：这段话在**新构型上已经不成立**。旧的 90×30 窄舌片电阻大、发热多，
        //   端片才要靠 18.7 mm 纤维保住热、共用片几乎要裸露 —— 四片差 12 倍。
        //   舌片加宽到 60 mm 之后电阻降下来，四片都只要 0.3–0.6 mm（≈裸），差异消失。
        //   ⇒ 文案按当前档的**实际值**判断，不写死结论。
        double insHi = fd.TabInsulMm.Max(), insLo = fd.TabInsulMm.Min();
        sb.Append("</table><p style=\"font-size:.88rem\">" +
                  (insHi > insLo * 3
                   ? $"舌片保温四片差 <b>{insHi / Math.Max(0.01, insLo):0} 倍</b>（{insHi:0.0} vs {insLo:0.0} mm）：" +
                     "端片要靠保温保住发热，共用片本身发热过剩、几乎要裸露散热。<b>不能同规格。</b>"
                   : $"本档四片舌保温都在 {insLo:0.0}–{insHi:0.0} mm，<b>几乎等于不包</b>。" +
                     "这是宽舌片带来的：舌片加宽 ⇒ 电阻降 ⇒ 自身发热少 ⇒ 不再需要靠纤维保住热，" +
                     "反而要保持散热能力才抽得动管子里的热（判据「管孔净流入」）。" +
                     "<br><b>这个旋钮不花铂</b>（只是纤维），所以铂重只由板厚和舌片尺寸决定 —— " +
                     "它也正是守住「抽热窗口」的主力。") + "</p>");

        sb.Append("<h2>5. 判据表怎么读</h2>");

        // ★★★ 2026-08-30：加「网格无关复核」这一列。
        //   原来只有一列「实际」，而那是**导航网格（2 mm）**上的数 —— 工程师无从知道。
        //   实测：0.8 档记录 4.720 K、复核 7.950 K；0.6 档记录 6.124 K、复核 9.453 K。
        //   限值 10 ⇒ 记录说「余量 53 %」，复核说「余量 20 %」。
        //   **一张不标口径的判据表，比没有判据表更坏** —— 它有排版、有裕度条，看起来就是答案。
        double VerifOf(string name) => name.StartsWith("法兰增量温降", StringComparison.Ordinal) ? fd.VerifiedFlangeDipK
                                     : name.StartsWith("管孔净流入", StringComparison.Ordinal) ? fd.VerifiedHoleFluxW
                                     : name.StartsWith("圆盘区最高温", StringComparison.Ordinal) ? fd.VerifiedDiscOverK
                                     : double.NaN;
        bool anyVerif = !double.IsNaN(fd.VerifiedMeshMm);

        sb.Append("<table><tr><th>判据</th><th>类别</th><th>记录值<br><span class=\"m\">导航网格 2 mm</span></th>"
                + (anyVerif
                    // ⚠ 2026-09-02：这里原来写死「⚠ 待复测」——note 非空就印。
                    //   而 note 改成说「越限」之后，列头就在说一件**已经不成立**的事
                    //   （它已经复测过了）。列头不许替 note 猜结论，只负责指过去。
                    ? $"<th>加密复算后的值<br><span class=\"m\">加密到 {fd.VerifiedMeshMm:0.000} mm"
                      + (fd.VerifiedNote.Length > 0 ? "　⚠ 见表下说明" : "") + "</span></th>"
                    : "")
                + "<th>限值</th><th>裕度<br><span class=\"m\">按复核值</span></th></tr>");
        foreach (var (n, k, a, l, u, less) in crit)
        {
            double v = VerifOf(n);
            bool hasV = !double.IsNaN(v);
            // ★ 裕度条按**复核值**画（有的话）—— 裕度是给人做决定的，就该用可信的那个数。
            double forBar = hasV ? v : a;
            sb.Append($"<tr><td>{n}</td><td>{k}</td><td class=\"n\">{a:0.000} {u}</td>" +
                      (anyVerif ? $"<td class=\"n\"><b>{(hasV ? v.ToString("0.000") + " " + u : "—")}</b></td>" : "") +
                      $"<td class=\"n\">{(Math.Abs(l) < 1e-9 ? "> 0" : (less ? "≤ " : "≥ ") + l.ToString("0.00"))}</td>" +
                      $"<td>{Bar(forBar, l, less)}</td></tr>");
        }
        sb.Append("</table>");
        // ★★★ 2026-09-02：复核值**是哪一版代码测的**必须印出来。
        //   实物：0.8 档那组是 08-30 换 CG 之前跑的，而当天下午我把它填进档、
        //   本表当作「加密到位、可信」那一列显示，口径一个字没标 ——
        //   源码注释救不了，工程师看不到源码。这一段就是那个口径。
        if (anyVerif && fd.VerifiedNote.Length > 0)
            sb.Append("<div class=\"note\" style=\"border-left-width:6px\">"
                    + Md(fd.VerifiedNote) + "</div>");
        if (anyVerif)
            sb.Append("<div class=\"note\"><b>两列的口径不一样，要看右边那列。</b>" +
                      "「记录值」是<b>导航网格（2 mm）</b>上算的 —— 它是当天那次运行的历史记录；" +
                      $"「复核值」是把网格一档档加密到<b>{fd.VerifiedMeshMm:0.000} mm</b>、直到判据不再变之后的数。<br>" +
                      $"本档实测差多少：法兰增量温降 <b>{fd.FlangeDipK:0.000} → {fd.VerifiedFlangeDipK:0.000} K</b>" +
                      "（限值 10）。粗网格看着余量很宽，加密之后并不宽 —— " +
                      "<b>这个差足以把「过」变成「不过」</b>，所以出图前必须点「◆ 加密复算（算到数不再变）」。<br>" +
                      "空着「—」的那几条是<b>闭式判据</b>（几何算出来的），不随网格变，没有复核值。</div>");
        else
            sb.Append("<div class=\"note\">⚠ <b>本档还没做过加密复算</b> —— 表里的数是" +
                      "<b>导航网格（2 mm）</b>上算的。实测同类设计粗细网格能差 3 K 以上（限值 10），" +
                      "<b>不算到数不再变，就不知道这张表准不准</b>。到「整线核算」页点「◆ 加密复算（算到数不再变）」。</div>");
        sb.Append("<div class=\"note\"><b>裕度这一列比「✓」有用。</b>" +
                  "本项目最常见的错就是<b>贴着限值判过与不过</b>——" +
                  "曾用 0.02–0.08 K 的差别决定了 700 g 铂金，而那点温差只对应 <b>14 mW</b>、" +
                  "占段功率 5 ppm，现场任何仪器都测不出来。<br>" +
                  "看到 ✓ 先问两句：这个裕度比<b>数值噪声</b>大吗？比<b>现场能分辨的尺度</b>大吗？</div>");

        // ── §5.1 形状体检报告（2026-08-17 新增功能，说明书必须跟上）
        sb.Append("<h3>5.1 形状体检报告 —— 「有依据地告诉我发现了什么」</h3>");
        sb.Append("<p>点<b>自动定厚</b>或<b>◇ 搜形状</b>之后，判据表上方会多出一段<b>形状体检</b>。" +
                  "它的结构不是随便排的，而是按一条原则来的：" +
                  "<b>能造能用是先决条件，省铂金是在这个前提下才讨论的</b>（业主 2026-08-15）。</p>");
        sb.Append("<table class=\"nw\"><tr><th>段落</th><th>回答什么</th><th>为什么这样排</th></tr>" +
                  "<tr><td><b>一、能不能造能不能用</b></td><td>七条硬安全线 + 板厚 vs 工艺下界</td>" +
                  "<td><b>不过就到此为止</b>，后面完全不谈铂重。<br>" +
                  "反例就在本项目里：舌长 90 那版热学五条全过、铂重最轻，而铜排根本装不上</td></tr>" +
                  "<tr><td>二、优化后</td><td>三个旋钮的收敛值 + 总铂 + 与设计记录的差</td><td>—</td></tr>" +
                  "<tr><td><b>三、抽热窗口</b></td><td>四片的抽热 D 落在 0–4.2 W 的哪一段</td>" +
                  "<td>「管孔净流入」与「法兰增量温降」是<b>同一个量的两侧</b>（实测 法兰增量温降 = 2.40·D）。" +
                  "偏低那侧是「热往管里灌」（烧断），偏高那侧是「把管根抽出深坑」</td></tr>" +
                  "<tr><td>四、优点</td><td>按裕度排序，每条带实测值与位置</td>" +
                  "<td>「散热好」不是结论，「圆盘区最高温 裕度 104 %、峰位 r=28.1 mm、局部 J=0.00 A/mm²」才是</td></tr>" +
                  "<tr><td>五、缺点／咬住它的</td><td>裕度最紧的两条 + <b>哪个旋钮已经顶死</b></td>" +
                  "<td>旋钮余量是「还有没有回旋空间」的直接量</td></tr>" +
                  "<tr><td>六、代价</td><td>多花多少铂、换来了什么</td>" +
                  "<td>只讲省了多少就是广告。优点与代价必须成对出现</td></tr></table>");
        sb.Append("<div class=\"note\"><b>报告里不会出现没有数撑着的因果。</b>" +
                  "初版曾写「舌片更大 ⇒ 局部 J 更低、更不容易过热」，而实跑的例子是 " +
                  "163×52 对比 140×60 —— <b>更窄</b>、面积几乎持平，那句因果是凭空加的，已删。<br>" +
                  "同理，升温不算「优点」（它算的是空管，裕度 100 % 是结构性的，任何形状都这样）；" +
                  "舌片自由段不算「缺点」（舌长是<b>算出来</b>的，必然贴着下界）—— " +
                  "但会单独提醒<b>装配没有余量</b>。</div>");

        // ★★ 限值一律**从代码取**，本表不再自己抄一份（2026-08-24）。
        //
        // 本方法开头那句「判据值只从 DesignSpec 取，本页不再自己抄一份」，
        // 当时只兑现了**实测值**那一半；**限值**这一半照旧是写死的字面量。
        // 而「管许用电流密度」在参数表里就是可改的（DisplayName 摆在那儿）——
        // 工程师一改参数，说明书当场变成假话，而它有排版有图，看起来就是答案。
        // ⇒ 限值取自：LineCase 的默认值（升温期限 / ②″ / ③）、
        //   活的 DesignInputs（管 J，拿不到时退回默认）、GeometryScreen 与 DesignInputs 的常数。
        var lim = new LineCase();
        var dfl = live ?? new DesignInputs();
        // ★ 代号对照表**排在限值表之前**（2026-08-29，用户：说明书内不可以用代号说明）。
        //   顺序有意义：读的人先知道 ③ 是什么，才谈得上看 ③ 的限值是多少。
        //   ⚠ 表自己不含限值数字 —— 限值只有一个来源，就是紧接着的那张表。
        sb.Append(Criteria.Html());
        sb.Append("<h3>限值的出处（每条都必须有）</h3><table class=\"nw\">" +
                  "<tr><th>判据</th><th>限值</th><th>出处</th></tr>" +
                  $"<tr><td>升温（空管到目标）</td><td class=\"n\">{lim.RampHours:0} h</td><td>业主「≤ 3 天」</td></tr>" +
                  "<tr><td>管孔净流入</td><td class=\"n\">&gt; 0</td><td>总纲 C2「为负即法兰比管热」——方向性判据</td></tr>" +
                  $"<tr><td>圆盘区最高温</td><td class=\"n\">{lim.DiscOverTempMaxK:0} K</td><td>现场控温精度 ±5 K</td></tr>" +
                  $"<tr><td>法兰增量温降</td><td class=\"n\">{lim.RootDeltaMaxK:0} K</td><td>总纲 C2；<b>业主明确：贴着热偶误差定的</b></td></tr>" +
                  $"<tr><td>管 J</td><td class=\"n\">{dfl.TubeJAllowAPerMm2:0.#} A/mm²</td>" +
                  "<td>现场：一般 15，管壁 0.6 时 12 是极限。<b>这一项在参数表里可改</b>，本行跟着它走</td></tr>" +
                  $"<tr><td><b>舌片自由段</b></td><td class=\"n\">≥ {GeometryScreen.FreeTabMinDefaultMm:0} mm</td>" +
                  "<td><b>业主 2026-08-17</b>：现场铜排长 100／宽 60–80 mm，自由段基本留 100。" +
                  "「这些是参考值并非绝对，铜排尺寸可以定制」<br>" +
                  "⚠ 这条判据是 2026-08-17 才加的，而它<b>当场把原来的设计记录判掉了</b>" +
                  "（舌长 90 ⇒ 自由段只有 24 mm，铜排装不上）</td></tr>" +
                  "<tr><td><b>圆盘盖得住管孔＋焊脚</b></td><td class=\"n\">≥ 0</td>" +
                  "<td>可造性：盘半径 − 管孔半径 − 焊脚（= max(板厚, 壁厚)）。" +
                  "<b>不可造的几何料最少，所以优化器会主动往那里跑</b> —— " +
                  "实测盘 R25 + 管壁 0.8 时管孔半径 25.8 &gt; 盘半径，孔比盘还大，" +
                  "而它照样报「全判据通过 3621 g」并排在最前面</td></tr>" +
                  $"<tr><td>管壁下界</td><td class=\"n\">{DesignInputs.WeldMinDefaultMm:0.0} mm</td>" +
                  "<td>手工 TIG 烧穿下界（自动 0.3、激光 0.1）</td></tr>" +
                  // ★ 两条热稳定 2026-08-24 补进本表。它们在 APP 的判据表里**是露脸的**
                  //   （参考量，带数值），此前说明书里**一条都没有** ——
                  //   工程师看到「· 整片热稳定 10.2×」回来查出处，查不到。
                  //   而本节的标题就是「每条都必须有」。
                  $"<tr><td>· 整片热稳定</td><td class=\"n\">&gt; 1.0 ×</td>" +
                  "<td>精确物理：dQ_散热/dT ÷ dP_发热/dT ≤ 1 时正反馈失控。" +
                  "<b>现为参考量，不卡交付</b> —— 沿三条轴实测过，" +
                  "管孔净流入永远先红，升成硬判据改变不了任何一个判定</td></tr>" +
                  $"<tr><td>· 局部热稳定</td><td class=\"n\">&gt; 1.0 ×</td>" +
                  "<td>精确物理：J_stab ÷ J_实际（逐格取**最不稳定**点，不是最热点）。" +
                  "同上，现为参考量。设计记录两档实测 1.9–2.0×</td></tr>" +
                  $"<tr><td>· 升温期法兰−管峰值</td><td class=\"n\">{lim.RampRateKPerH:0} K/h 下 215 K</td>" +
                  "<td><b>现场升温工况</b>（温控、空管）下「法兰温度 − 管温」的全程最大值。" +
                  "<b>215 不是通过线，是现役基准</b> —— 业主 2026-08-25 确认现场就是这个量级，" +
                  "拿它去判现役等于判掉自己。<b>现为参考量，不卡交付</b>。" +
                  "⚠ 判据「升温」判的是「电流开满最快多久到」（能力），" +
                  "而它用的模型把法兰并进管子当同一个温度 —— " +
                  "<b>「法兰比管热」在那个模型里看不见</b>，而那正是烧断的机理；本条用两节点模型补上</td></tr>" +
                  "<tr><td>· 管↔法兰热收支</td><td class=\"n\">守恒时 0 W</td>" +
                  "<td><b>对账</b>：各段端部**实际扣掉**的抽热总和 − 各片**实际收到**的 QFromTubeW。" +
                  "守恒时应为 0。<b>实测四个档都非零</b>（0.8 档 +2.66 W、0.6 档 +3.74 W）—— " +
                  "外层耦合把每片的**全额**挂到相邻段端，于是<b>内部共用片被两段各扣一次</b>：" +
                  "管子失去 Q₀ + 2ΣQ内 + Q_n，法兰只收到 ΣQ。" +
                  "此前<b>没有任何一处在对账</b>，所以谁也没发现（2026-08-28 第一性原理通查）。" +
                  "<b>现为参考量，不卡交付</b> —— 改判定会动所有历史结果，先量出来再谈改不改。" +
                  "⚠ 对「法兰增量温降」的影响方向<b>尚未确定</b>：多抽热应使管根温降偏大（保守侧），" +
                  "但耦合是非线性的，要修完重跑才能定</td></tr>" +
                  "</table>");
        sb.Append("<div class=\"note\"><b>「法兰增量温降」的 10 K：出处是热偶误差，物理依据未知。</b>" +
                  "⇒ 不得据此声称「超过 10 K 也安全」，也不得声称「真实限值是 X」。" +
                  "但有两条推论成立：<b>不该把设计点摆在 10 上</b>（摆在测量地板上的判定分不出真假）；" +
                  "<b>不该拿它当控制靶</b>（∂法兰增量温降/∂板厚 = +149 K/mm 是正号，会让优化器加厚板把判据推向限值）。</div>");

        sb.Append("<h2>6. 收敛信息</h2>");
        sb.Append($"<p>本档外层耦合<b>剩余误差估计 {fd.ResidualK:0.00} K</b> —— 这是「距不动点」的估计 " +
                  "<code>δ·r/(1−r)</code>，<b>不是</b>「相邻两轮变化 δ」。<br>" +
                  "曾经用 δ 当收敛判据：δ=1.36 时报「5 轮收敛」，而真实剩余误差约 34 K。" +
                  "界面在判据表上方打这一行；<b>未收敛时会打「下面每个数都不可引用」——那是字面意思。</b></p>");

        sb.Append("<h2>7. 设计记录 3DM</h2>");
        sb.Append("<p>「设计记录」页点<b>「导出设计记录 3DM」</b>。" +
                  "内容：三段铂管 + 四片法兰（板身 / 环外级 / 环内级 / <b>角焊缝</b>）+ 压接段参考线。<br>" +
                  "<b>图层按片分，不按类型分</b> —— 交付件要能单独调出某一片；" +
                  "更硬的理由是厚度探针沿 Y 打射线，而四片正是沿 Y 排成一列，" +
                  "同层会被一次穿透、厚度<b>加起来</b>（当时实测 10.450 = 2.11+3.40+3.18+1.76，那是<b>出事那天的板厚</b>，不是现在的设计记录值）。</p>");
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
                  "<li><b>几何只有一个来源</b>：<code>DesignSpec</code>。曾经辅助命令各钉着几代前的几何，跑得出漂亮的数——但那是另一个设计的数。</li>" +
                  "<li><b>判据不允许消失</b>：只能过/不过/无法判定，<b>无法判定一律不算通过</b>。</li></ol>");

        sb.Append("<h2>9. 已知坑（不看这节会重犯）</h2>");
        sb.Append("<table class=\"nw\"><tr><th>坑</th><th>形状</th><th>怎么发现的</th></tr>" +
                  "<tr><td><b>代理量当原量</b></td><td>拿邻近的量顶替判据／靶／收敛度量</td>" +
                  "<td>一天犯三次：拿段内最大偏差当管根、拿 B 当「法兰增量温降」的靶、拿会切换分支的标量当收敛度量</td></tr>" +
                  "<tr><td><b>默认值当需求</b></td><td>冻结的占位值被当成给定条件</td>" +
                  "<td>已十次。<b>每个冻结值都要扫一遍</b>，哪怕最后证明它是对的</td></tr>" +
                  "<tr><td><b>参数写绝对值</b></td><td>依赖项一动，对策静默失效或被悄悄重画</td>" +
                  "<td>渐变环写绝对半径 ⇒ 板一变厚环就消失，整条可行性阶梯从来没有环</td></tr>" +
                  "<tr><td><b>修一个漏一个</b></td><td>新旋钮写在旧旋钮的 <code>continue</code> 之后</td>" +
                  "<td>优化器报「都到位」停机，实际差 0.01 K</td></tr>" +
                  "<tr><td><b>容差粗于判据分辨率</b></td><td>判据在读求解器的残差</td>" +
                  "<td>容差 1.0 K 而「圆盘区最高温」在 0.01 K 上判过不过</td></tr>" +
                  "<tr><td><b>校验量选错</b></td><td>校验通过，但它管不到出错的那一维</td>" +
                  "<td>包围盒验不了材料，报「全吻合」而法兰差 57 %</td></tr></table>");
        sb.Append("<div class=\"note\"><b>共同点：不报错、输出格式正常、数值看着合理 —— 但结论是错的。</b><br>" +
                  "唯一可靠的抓法是<b>交叉核对</b>：任何「通过」的结论，用另一个独立的数验一遍。</div>");

        // ★★★ 2026-09-02 **整节删除**：原来这里是「§10 常用命令行」，
        //   一张教现场工程师敲 --cli --final2 / --busbarplan / --hotspot 的表。
        //   用户拍板：「APP 不要有命令行形式操作，所需必要的计算全由 APP 程序操控」
        //   「这个 APP 不是给程序员用的是给现场工程师的」。
        //   ⇒ 说明书里教命令行，等于承认那几件事界面做不到。做得到的就该在界面上，
        //     做不到的就该去把它接上 —— 而不是把开关名印给工程师。
        //   （这些开关本身留着，它们是**我的**工装；只是不再出现在他的说明书里。）
        sb.Append("<h2>10. 现场还需确认的数</h2>");
        sb.Append("<table class=\"nw\"><tr><th>量</th><th>现状</th><th>一旦不同，影响多大</th></tr>" +
                  "<tr><td>法兰 J 的许用值</td><td><b>无依据</b>（现取 10，已降为参考量）</td>" +
                  "<td>法兰 J 实测 34。这条一旦成为硬判据，结论大幅改变</td></tr>" +
                  "<tr><td>「法兰增量温降」的物理依据</td><td>只知刻度来自热偶误差 ±10 K</td>" +
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
