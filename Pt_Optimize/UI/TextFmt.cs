using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PtOptimize.UI;

/// <summary>
/// ★★★★★ 输出框的排版 —— **内容一律写成 Excel 那种「一格一个 Tab」的表**（2026-08-20）。
///
/// ════ 三次尝试，前两次为什么不成 ════
///
/// **① 按「字符数」补空格**（`$"{name,-18}"`）。中文一个字占两个西文字宽，
///    C# 的 `,-18` 却是按**字符个数**补的 ⇒ 中文多的列排得靠右、少的靠左，每行都不一样。
///
/// **② 按「显示宽度」补空格**（中文算 2）。前提是「字体本身含中文且恰好是半角的两倍」。
///    Consolas **不含中文**，Windows 回落到另一套字体，宽度不成整数倍 ⇒ 中文越多偏得越远。
///    换成含中文的等宽字体（<see cref="UiScale.MonoFamily"/>）能救一部分，
///    但**拿字体去保证对齐，本质上是在赌**：换台机器、字体没装，表就又歪了。
///
/// **③ 制表位**（本版）。<see cref="RichTextBox.SelectionTabs"/> 是**以像素定位**的列位置，
///    与每个字的实际宽度无关 —— 换字体、混中英文都不影响。这才是控件自己的能力。
///
/// ════ 为什么这一版敢再做「逐表算列宽」 ════
///
/// 上一版（2026-08-18）退回成「一套等距制表位管全框」，理由是逐表定位试过两次都错位。
/// **错位的真因是选区下标算错了，不是逐表定位这件事本身不成立**：
/// 当时在**源字符串**上数字符位置，而 RichTextBox 内部把换行存成 `\n`、源串写的是 `\r\n`，
/// 还要再减去被吃掉的 `**` 标记 —— 每算一处就偏一点，整段推着走。
///
/// ⇒ 本版**不再在源串上数位置**：一边往控件里写，一边拿 <see cref="RichTextBox.TextLength"/>
///   记下这张表的起止。那是**控件自己的下标**，换行怎么存、标记吃掉几个字，都已经算进去了。
///
/// 逐表算列宽换来的是：列宽随内容走（表头不必再缩写成 8 个字以内），
/// 而且**每张表各管各的** —— 一张表的宽列不会把另一张表的列推歪。
///
/// ════ 调用方要做的事，只有一件 ════
///
/// 单元格之间写 <c>\t</c>，行末照常换行。**这就是 Excel 复制出来的格式**（TSV）。
/// 连续的、带 `\t` 的行自动认成同一张表；空行或普通句子就把表断开。
/// 数字列自动右对齐（判定规则见 <see cref="IsNumeric"/>），不需要在调用处指定。
///
/// 命令行/写文件那一侧走 <see cref="Plain"/>：同样的 TSV，改用空格补齐，
/// 免得控制台里出现一串裸 Tab。**同一份内容，两种渲染** —— 只有一个来源。
/// </summary>
internal static class TextFmt
{
    /// <summary>一个字符占几个西文字宽：中日韩、全角标点算 2，其余算 1。（只有 <see cref="Plain"/> 用得到）</summary>
    public static int CharWidth(char c) =>
        c >= 0x1100 && (
            c <= 0x115F ||                                   // 韩文字母
            c == 0x2329 || c == 0x232A ||
            (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||   // 中日韩部首 … 注音
            (c >= 0xAC00 && c <= 0xD7A3) ||                  // 韩文音节
            (c >= 0xF900 && c <= 0xFAFF) ||                  // 兼容汉字
            (c >= 0xFE30 && c <= 0xFE6F) ||                  // 中日韩兼容形式
            (c >= 0xFF00 && c <= 0xFF60) ||                  // 全角 ASCII
            (c >= 0xFFE0 && c <= 0xFFE6))
        ? 2 : 1;

    /// <summary>字符串的显示宽度（西文字宽为单位）。</summary>
    public static int Width(string s)
    {
        int w = 0;
        foreach (char c in s) w += CharWidth(c);
        return w;
    }

    /// <summary>按**显示宽度**左对齐补齐；已经超宽就原样返回（不截断，宁可挤也不丢信息）。</summary>
    public static string PadR(string s, int width)
    {
        int n = width - Width(s);
        return n > 0 ? s + new string(' ', n) : s;
    }

    /// <summary>按**显示宽度**右对齐补齐。</summary>
    public static string PadL(string s, int width)
    {
        int n = width - Width(s);
        return n > 0 ? new string(' ', n) + s : s;
    }

    /// <summary>去掉 Markdown 的 `**` 标记（给 CLI/纯文本用 —— 那里没有粗体可言）。</summary>
    public static string Strip(string s) => s.Replace("**", "");

    /// <summary>整行的分隔线，按显示宽度给长度。</summary>
    public static string Rule(int width, char c = '─') => new(c, Math.Max(1, width));

    /// <summary>
    /// 表内的横线：写成**一格一段**，跟着列宽走（像 Excel 的框线）。
    /// <para>
    /// ⚠ 别在表中间写一条不带 `\t` 的长横线：那会被当成普通句子，**把一张表断成两张**，
    ///   两半各自算列宽 ⇒ 表头和数据对不上。这正是本方法存在的理由。
    /// </para>
    /// </summary>
    public static string SepRow(int columns, char c = '─') =>
        string.Join("\t", Enumerable.Repeat(new string(c, 4), Math.Max(1, columns)));

    // ────────────────────────────────────────────────────────────────────────
    //  表的识别与列的度量
    // ────────────────────────────────────────────────────────────────────────

    private static bool HasTab(string s) => s.IndexOf('\t') >= 0;

    /// <summary>
    /// 整行只有横线（和空白）—— 允许它待在表里，但**既不参与列宽计算，也不参与数字列判定**。
    ///
    /// ⚠ 「至少三个横线字符」这道门槛不是凑数：没有它，一行 `-\t-\t-`
    ///   （三格都是「没有值」的短横占位符）会被误判成分隔线，
    ///   整行数据被换成一条横线 —— **数据当装饰给扔了**。
    ///   真正的分隔线不可能只有一两个字符，而占位符不可能连着三个。
    /// </summary>
    private static bool IsRuleOnly(string s)
    {
        string t = s.Trim();
        if (t.Length == 0) return false;
        int n = 0;
        foreach (char c in t)
        {
            if (c is '─' or '═' or '-' or '=') { n++; continue; }
            if (c is ' ' or '\t') continue;
            return false;
        }
        return n >= 3;
    }

    /// <summary>
    /// 这一行算不算表的一部分。
    /// 带 `\t` 的当然算；**纯横线**若紧挨着带 `\t` 的行也算 ——
    /// 否则一条手写的分隔线就把表断成两截（各自算列宽 ⇒ 前后对不上）。
    /// </summary>
    /// <summary>
    /// ★★★★★ **把过长的正文行折开**（2026-09-03 抓图抓到）。
    ///
    /// 实况：输出框 <c>WordWrap = false</c>（表格靠制表位对齐，折行会散），
    /// 而「【下一步】…」那条处置建议实测 <b>703 个字宽</b>，框只有 ~170 ⇒
    /// 横向滚动条的滑块只占 1/4 —— **工程师最该读的那一行，正是要向右拉 500 字的那一行**。
    ///
    /// ⇒ 只折**没有制表符**的行（表格行原样放过），在这一个咽喉处理，
    ///   上游谁写的都盖得住。
    ///
    /// ⚠ 必须认得 <c>**粗体**</c>：<see cref="AppendMarkup"/> 是**逐行**解析标记的，
    ///   把一对 <c>**</c> 对切开会让两半各留一个孤立标记，屏幕上直接露出星号。
    ///   ⇒ 断行时若正处在粗体里，就**收尾一个 `**`、下一行再开一个**。
    /// ⚠ 断点优先挑标点后面；挑不到才硬断。
    /// </summary>
    private static string[] WrapProse(RichTextBox box, string[] lines)
    {
        int avail = box.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8;
        var font = box.Font;
        int W(string t) => TextRenderer.MeasureText(
            t.Replace("**", ""), font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        // ★★★ 窄到放不下 30 个汉字就**一个字都不折**（2026-09-03，门当场抓到）。
        //   上一版下限写的是 120 px —— 一个 200 px 宽的框照样进来折，
        //   结果把正文折成「★ ／ 舌长装不下 ／ 铜排， ／ 已顶到装配」这种 4~5 字一行，
        //   **比横向滚动难读得多**。框还没被布局给宽度时也是这个情形。
        //   ⚠ 宽度稍后由 ClientSizeChanged 补排（见 Hook），所以这里跳过是安全的。
        if (avail < W(new string('中', 30))) return lines;

        const string BreakAfter = "，。；：、）」』】？！…·　 ";
        var outp = new List<string>();
        foreach (var line in lines)
        {
            if (HasTab(line) || line.Length == 0 || W(line) <= avail) { outp.Add(line); continue; }

            // 续行跟着首行的缩进走，读起来才是一段
            int ind = 0;
            while (ind < line.Length && (line[ind] == ' ' || line[ind] == '　')) ind++;
            string indent = line[..ind];

            var cur = new StringBuilder();
            bool boldOpen = false;
            int lastGood = -1;              // cur 里「可以在此断」的长度
            bool lastGoodBold = false;
            for (int i = ind; i < line.Length; )
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                { cur.Append("**"); boldOpen = !boldOpen; i += 2; continue; }

                cur.Append(line[i]);
                if (W(indent + cur) > avail && cur.Length > 1)
                {
                    int cut = lastGood > 0 ? lastGood : cur.Length - 1;
                    bool cutBold = lastGood > 0 ? lastGoodBold : boldOpen;
                    string head = cur.ToString(0, cut);
                    string tail = cur.ToString(cut, cur.Length - cut);
                    outp.Add(indent + head + (cutBold ? "**" : ""));
                    cur.Clear();
                    if (cutBold) cur.Append("**");
                    cur.Append(tail);
                    lastGood = -1;
                    // 断点之后 boldOpen 不变：head 补的 `**` 与新行开头的 `**` 相抵
                }
                else if (BreakAfter.IndexOf(line[i]) >= 0)
                { lastGood = cur.Length; lastGoodBold = boldOpen; }
                i++;
            }
            if (cur.Length > 0) outp.Add(indent + cur);
        }
        return outp.ToArray();
    }

    private static bool IsTableLine(string[] lines, int i) =>
        HasTab(lines[i]) ||
        (IsRuleOnly(lines[i]) && ((i > 0 && HasTab(lines[i - 1])) ||
                                  (i + 1 < lines.Length && HasTab(lines[i + 1]))));

    /// <summary>
    /// 这一格是不是「数字」——数字列右对齐，文字列左对齐（**与 Excel 同款默认**）。
    ///
    /// 判定刻意收得很紧：必须**全部由 ASCII 的数字与算术符号构成**，且至少有一个数字。
    /// 收紧的理由不是洁癖，而是右对齐要靠补空格实现，
    /// 而「补 n 个空格 = 补 n 个字符宽」**只在这一格全是 ASCII 时才严格成立**（等宽字体下）。
    /// 一旦格子里混进中文或 `✓`、`℃`、`−`(U+2212) 这类非 ASCII 字形，宽度就不是整数倍，
    /// 那一列干脆左对齐 —— **宁可不右对齐，也不要对歪**。
    /// </summary>
    private static bool IsNumeric(string cell)
    {
        string t = Strip(cell).Trim();
        if (t.Length == 0) return true;              // 空格：不反对，也不支持
        if (t == "—" || t == "–") return true;       // 「没有值」的占位符，跟着数字列走
        bool digit = false;
        foreach (char c in t)
        {
            if (c >= '0' && c <= '9') { digit = true; continue; }
            if (c is '.' or '+' or '-' or '%' or '/' or ',' or 'e' or 'E') continue;
            return false;
        }
        return digit;
    }

    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>
    /// 量一格的像素宽。`**` 标记本身不显示 ⇒ 先去掉；整格加粗的按粗体量（粗体更宽，
    /// 按细体量会**少算**，列就会被下一格的内容顶开）。
    /// </summary>
    private static int Measure(string cell, Font normal, Font bold)
    {
        string t = Strip(cell);
        bool allBold = cell.Length >= 4 && cell.StartsWith("**", StringComparison.Ordinal)
                                        && cell.EndsWith("**", StringComparison.Ordinal);
        // 格子里只有一段加粗时也按粗体量：宁可略宽，不可略窄
        bool anyBold = cell.Contains("**", StringComparison.Ordinal);
        return TextRenderer.MeasureText(t, allBold || anyBold ? bold : normal,
                                        Size.Empty, MeasureFlags).Width;
    }

    /// <summary>
    /// 把一张表排好：数字列右对齐、算出每列的制表位像素位置。
    /// 返回值是**改写过的行**（右对齐已经补进单元格里）。
    /// </summary>
    private static (List<string> rows, int[] stops) LayoutTable(
        IReadOnlyList<string> raw, Font normal, Font bold)
    {
        // ① 拆格。纯横线行先留着位置，最后按列数重排成「一格一段」。
        // ⚠ 判「是不是横线行」**不能再加 `&& !HasTab`**（2026-08-20 复核抓到）。
        //   <see cref="SepRow"/> 产出的横线**本来就是带 `\t` 的**（一格一段），
        //   加上那个条件它就落进普通数据行，于是：
        //     · 它的 `────` 进了数字列判定 ⇒ IsNumeric 认不得 U+2500 ⇒
        //       **整张表的每一列都被判成文字列，右对齐一次也不执行**；
        //     · 它还参与量宽，把窄列硬撑宽。
        //   —— 本次改造的招牌功能就这么被一个多余的条件整个关掉了，而表面上毫无异样。
        var cells = new List<string[]?>(raw.Count);
        foreach (string line in raw)
            cells.Add(IsRuleOnly(line) ? null : line.Split('\t'));

        int nc = 0;
        foreach (var r in cells) if (r is not null) nc = Math.Max(nc, r.Length);
        if (nc == 0) return (raw.ToList(), Array.Empty<int>());

        // ② 哪几列是数字列。表头（第一行）是文字，不参与判定 ——
        //    否则「电流A」这种表头会把整列判成文字列，数字就不右对齐了。
        var numeric = new bool[nc];
        for (int c = 0; c < nc; c++)
        {
            bool any = false, all = true;
            for (int i = 1; i < cells.Count; i++)
            {
                var r = cells[i];
                if (r is null || c >= r.Length) continue;
                string t = Strip(r[c]).Trim();
                if (t.Length == 0) continue;
                if (!IsNumeric(r[c])) { all = false; break; }
                if (t != "—" && t != "–") any = true;
            }
            numeric[c] = all && any;
        }

        // ③ 逐列量宽（此时还没补空格）
        int spacePx = Math.Max(1, TextRenderer.MeasureText(" ", normal, Size.Empty, MeasureFlags).Width);
        var px = new int[nc];
        foreach (var r in cells)
        {
            if (r is null) continue;
            for (int c = 0; c < r.Length && c < nc; c++)
                px[c] = Math.Max(px[c], Measure(r[c], normal, bold));
        }

        // ④ 数字列右对齐：在格子前面补空格。
        //    ASCII 格子里「一个空格 = 一个字宽」是严格的（等宽字体），补出来分毫不差；
        //    表头是中文，只能按像素凑整 —— 差不到半个空格，表头上看不出来，而数字列必须准。
        foreach (var r in cells)
        {
            if (r is null) continue;
            for (int c = 0; c < r.Length && c < nc; c++)
            {
                if (!numeric[c]) continue;
                int n = (int)Math.Round((px[c] - Measure(r[c], normal, bold)) / (double)spacePx);
                if (n > 0) r[c] = new string(' ', n) + r[c];
            }
        }

        // ⑤ 纯横线行：按列数重排，长度跟着列宽走
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] is not null) continue;
            char ch = raw[i].Trim().FirstOrDefault(x => x != ' ');
            if (ch == '\0') ch = '─';
            var seg = new string[nc];
            for (int c = 0; c < nc; c++)
                seg[c] = new string(ch, Math.Max(2, px[c] / Math.Max(1,
                            TextRenderer.MeasureText(ch.ToString(), normal, Size.Empty, MeasureFlags).Width)));
            cells[i] = seg;
        }

        // ⑥ 补空格之后重量一次，累加成制表位
        var w2 = new int[nc];
        foreach (var r in cells)
            for (int c = 0; c < r!.Length && c < nc; c++)
                w2[c] = Math.Max(w2[c], Measure(r[c], normal, bold));

        // 列间距：三个西文字宽。
        //
        // ⚠ 这个余量不只是为了好看，更是**兜底**：量宽用的是 GDI 的 TextRenderer，
        //   而实际画字的是 RichEdit —— 两者对同一串字可能差上几个像素。
        //   一旦量出来比画出来窄，文字就会**盖过制表位**，Tab 于是「原地不动」，
        //   两列直接粘成一格（2026-08-18 实测把 `10.80` 和 `2200.0` 挤成了 `10.802200.0`）。
        //   宁可空一点，也不能让列粘上。
        int gap = Math.Max(9, 3 * TextRenderer.MeasureText("0", normal, Size.Empty, MeasureFlags).Width);
        var stops = new List<int>();
        int acc = 0;
        for (int c = 0; c < nc - 1; c++)
        {
            acc += w2[c] + gap;
            // 制表位必须**严格递增**，否则 Tab「原地不动」，两列会贴成一格
            if (stops.Count > 0 && acc <= stops[^1]) acc = stops[^1] + gap;
            stops.Add(acc);
        }

        var outRows = new List<string>(cells.Count);
        foreach (var r in cells) outRows.Add(string.Join("\t", r!));
        return (outRows, stops.Take(32).ToArray());     // RichTextBox 最多 32 个制表位
    }

    // ────────────────────────────────────────────────────────────────────────
    //  写进控件
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 TSV 文本写进 RichTextBox：表按**逐表算出的制表位**排，`**…**` 真的加粗、标记不显示。
    /// </summary>
    /// <param name="append">true = 追加，false = 覆盖</param>
    public static void Write(RichTextBox box, string text, bool append = false)
    {
        // ★ 防重入的闸门放在**这里**，不放在挂钩里（2026-08-20）。
        //
        // Write 往框里写的每一次 AppendText 都会触发 TextChanged。
        // 若闸门只在 <see cref="Hook"/> 的闭包里，那么**直接调用 Write** 的那些地方
        // （挂钩没参与）就绕过了闸门 —— 每写一段又触发一次 Write，层层套下去。
        // ⇒ 闸门按「哪个框正在被写」记，Write 与 Hook 共用同一份。
        if (!_writing.Add(box)) return;
        try
        {
            // ★★★ 记住**没折过的原文**（2026-09-03）。
            //   折行要按框的宽度来，而框在**构造时还没有宽度**（布局还没跑）⇒
            //   那一次必然折不了；等布局给了宽度，手上只剩已经折过的文本，
            //   再折一次既不能变宽也不能还原。⇒ 原文留着，尺寸一变就按新宽度重排。
            _rawText[box] = append && _rawText.TryGetValue(box, out var old)
                          ? old + text : text;
            WriteCore(box, text, append);
        }
        finally { _writing.Remove(box); }
    }

    /// <summary>每个输出框最后一次写进去的**原文**（带 `**`、未折行）。</summary>
    private static readonly Dictionary<RichTextBox, string> _rawText = new();

    /// <summary>
    /// ★★★★★ **这个框此刻该以谁为准**（2026-09-03，界面接线测试当场抓到）。
    ///
    /// 加了「原文缓存 + 尺寸一变就重排」之后，输出框就有了**两个写入者**：
    /// 上游的 <c>box.Text = 新内容</c>，和我这个 ClientSizeChanged 处理器。
    /// 而赋值本身会改变滚动条 ⇒ **触发 ClientSizeChanged** ⇒ 处理器拿着
    /// 上一次的 <c>_rawText</c> 把刚写进去的内容**整段盖回去**。
    ///
    /// 实测被盖掉的是「分析几何变数」的整份报告（含「解析替身不可用 / 开槽」），
    /// 屏幕上只剩上一条「读取了新方案…」—— 人会以为分析什么都没算出来。
    ///
    /// ⇒ 判断依据：框里显示的，是不是就是 <c>_rawText</c> 渲染出来的那一份
    ///   （去掉 `**`、换行与空白之后逐字相同）。不是的话，**外面改过 ⇒ 以框为准**。
    /// </summary>
    private static string RawOf(RichTextBox box)
    {
        string shown = box.Text;
        if (_rawText.TryGetValue(box, out var raw) && Flat(raw) == Flat(shown)) return raw;
        _rawText[box] = shown;
        return shown;
    }

    /// <summary>去掉标记与所有空白 —— 只比「说了哪些字」，不比怎么排的。</summary>
    private static string Flat(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*') { i++; continue; }
            if (s[i] is '\r' or '\n' or '\t' or ' ' or '　') continue;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>正在被写的输出框。只在 UI 线程上进出，不需要加锁。</summary>
    private static readonly HashSet<RichTextBox> _writing = new();

    private static void WriteCore(RichTextBox box, string text, bool append)
    {
        box.SuspendLayout();
        if (!append) box.Clear();

        var normal = box.Font;
        using var bold = new Font(normal, FontStyle.Bold);

        var lines = WrapProse(box,
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));

        // 每张表记下「在控件里的起止 + 它自己的制表位」。
        // ★ 起止取自 box.TextLength ——**控件自己的下标**，
        //   换行怎么存、`**` 吃掉几个字，都已经算进去了（前两版就是栽在源串下标上）。
        var blocks = new List<(int start, int len, int[] stops)>();

        int i = 0;
        while (i < lines.Length)
        {
            if (!IsTableLine(lines, i))
            {
                AppendMarkup(box, lines[i], normal, bold);
                if (i + 1 < lines.Length) box.AppendText("\n");
                i++;
                continue;
            }

            int j = i;
            while (j < lines.Length && IsTableLine(lines, j)) j++;

            var (rows, stops) = LayoutTable(new ArraySegment<string>(lines, i, j - i), normal, bold);

            int s0 = box.TextLength;
            for (int k = 0; k < rows.Count; k++)
            {
                AppendMarkup(box, rows[k], normal, bold);
                if (i + k + 1 < lines.Length) box.AppendText("\n");
            }
            int s1 = box.TextLength;
            // 末尾那个换行不选进来：SelectionTabs 是**段落**属性，
            // 把换行也选上就会把下一段一起改掉。
            blocks.Add((s0, Math.Max(0, s1 - s0 - 1), stops));
            i = j;
        }

        foreach (var (s, len, stops) in blocks)
        {
            if (stops.Length == 0 || len <= 0 || s + len > box.TextLength) continue;
            box.Select(s, len);
            box.SelectionTabs = stops;
        }

        box.ResumeLayout();
        box.Select(0, 0);
        box.ScrollToCaret();
    }

    /// <summary>
    /// ★★★★★ **一次挂钩，覆盖一个输出框的所有写入路径**。
    ///
    /// 输出框的写入散在几十个方法里（`.Text =`、`+=`、`AppendText`）。
    /// 逐个改成「格式化写入」必然漏掉几处，而**漏掉的那几处会混在排好的内容中间**
    /// —— 那比全都不排版更难看出来。
    /// ⇒ 改成挂一次 <see cref="Control.TextChanged"/>：内容里只要还带着 `\t` 或 `**`，
    ///   就整框重排一遍。重排完 `**` 已经没有了，`\t` 虽然还在但制表位已经设好；
    ///   <see cref="Write"/> 自带的闸门挡住重入，不会来回震荡。
    ///
    /// ⚠ 四个输出框（整线设计 / 分析 / 主报告 / 分段核算）都走这一个方法。
    ///   排版规则只有一个来源 —— 与「判据只有一个来源」是同一条道理。
    /// </summary>
    public static void Hook(RichTextBox box)
    {
        box.TextChanged += (_, _) => Reformat(box);

        // ★★★★★ **光挂 TextChanged 是不够的**（2026-08-20，接线测试的探针抓到）。
        //
        // 实况：`handle=False` 时给 `.Text` 赋值，`fired=0` ——
        // **句柄还没建之前，RichTextBox 的 Text setter 根本不触发 TextChanged**
        // （TextBoxBase 的 TextChanged 是由原生控件的 EN_CHANGE 通知转上来的，
        //   没有窗口就没有通知）。
        //
        // 这不只是测试里的怪现象，**用户看得到**：本页在构造函数里就写了首屏那段字，
        // 那时控件还没显示、句柄还没建 ⇒ 排版一次也没跑过；等到窗口显示，
        // 文本被推给原生控件，仍然是原样，而且**这一步也不发 TextChanged**。
        // ⇒ 首屏就那么顶着一串 `**` 摆在那儿，直到用户触发下一次写入才恢复正常。
        //
        // ⇒ 句柄建好的那一刻补排一次。两个入口合起来才真正覆盖「所有写入路径」。
        box.HandleCreated += (_, _) => Reformat(box);

        // ★★★ 宽度一变就按新宽度重折（2026-09-03 抓图抓到）。
        //   构造时框还没有宽度 ⇒ 首屏那次写入折不了行，而在此之前**没有任何一处会再排**
        //   （Reformat 见到没有 `\t` 也没有 `**` 就直接返回）⇒ 首屏永远是不折行的。
        //   工程师拉窗口时同理：折出来的行宽该跟着窗口走。
        //   ⚠ 必须从**原文**重排，不能拿框里已经折过的文本再折 —— 那样只会越折越窄。
        box.ClientSizeChanged += (_, _) =>
        {
            if (_writing.Contains(box)) return;
            string raw = RawOf(box);          // ⚠ 不能直接用缓存：外面可能刚改过（见 RawOf）
            if (raw.Length > 0) Write(box, raw);
        };
    }

    /// <summary>内容里还带着 `\t` 或 `**` 就整框重排一次；正在写的时候不插手。</summary>
    private static void Reformat(RichTextBox box)
    {
        if (_writing.Contains(box)) return;
        // ★★★ 能走到这里 = **有人直接给 `.Text` 赋值**（没走 Write，`_writing` 是空的）
        //   ⇒ 框里这份就是新的原文，必须**当场认下**。
        //   ⚠ 上一版这里优先读 `_rawText`，而那份是上一次 Write 存的旧文 ⇒
        //     直接赋值进来的新内容会被旧文**整段盖掉**。实测被吞掉的正是
        //     「⚠ 参数表改了「控温点 HC1」—— 上一次的解不再对应当前参数」这条警告
        //     （MarkParamsChanged 用的就是 `_out.Text = 警告 + _out.Text`）。
        //     ——「把话说了却没送到」是本项目最贵的一类错，门当场抓到。
        string t = RawOf(box);
        if (t.IndexOf('\t') < 0 && t.IndexOf("**", StringComparison.Ordinal) < 0
            && t.Split('\n').All(l => l.Length < 200)) return;
        Write(box, t);
    }

    /// <summary>一行文本按 `**…**` 分段写入：标记之间加粗，标记本身不显示。</summary>
    private static void AppendMarkup(RichTextBox box, string line, Font normal, Font bold)
    {
        int i = 0;
        while (i < line.Length)
        {
            int m = line.IndexOf("**", i, StringComparison.Ordinal);
            if (m < 0) { AppendRun(box, line[i..], normal); return; }
            int e = line.IndexOf("**", m + 2, StringComparison.Ordinal);
            if (e < 0) { AppendRun(box, line[i..], normal); return; }

            if (m > i) AppendRun(box, line[i..m], normal);
            AppendRun(box, line[(m + 2)..e], bold);
            i = e + 2;
        }
    }

    private static void AppendRun(RichTextBox box, string s, Font f)
    {
        if (s.Length == 0) return;
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = f;
        box.AppendText(s);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  纯文本一侧（控制台、写进 .txt 的交付件）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 同一份 TSV 的**纯文本渲染**：制表位换成空格，`**` 去掉。
    ///
    /// 控制台没有可设的制表位（默认每 8 格跳一次，中文一多照样歪），
    /// 所以这一侧只能退回「按显示宽度补空格」—— 那在等宽终端里是成立的。
    /// **两侧共用同一份内容**，只是渲染方式不同：判据、数字、措辞都只有一个来源。
    /// </summary>
    public static string Plain(string tsv)
    {
        if (tsv.IndexOf('\t') < 0) return Strip(tsv);

        string nl = tsv.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = tsv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();

        int i = 0;
        while (i < lines.Length)
        {
            if (!IsTableLine(lines, i))
            {
                sb.Append(Strip(lines[i]));
                if (i + 1 < lines.Length) sb.Append(nl);
                i++;
                continue;
            }

            int j = i;
            while (j < lines.Length && IsTableLine(lines, j)) j++;

            var rows = new List<string[]?>();
            for (int k = i; k < j; k++)
                // 同 LayoutTable：横线行一律走 null 通道，别让它污染数字列判定与列宽
                rows.Add(IsRuleOnly(lines[k])
                         ? null : lines[k].Split('\t').Select(Strip).ToArray());

            int nc = 0;
            foreach (var r in rows) if (r is not null) nc = Math.Max(nc, r.Length);

            var w = new int[nc];
            foreach (var r in rows)
            {
                if (r is null) continue;
                for (int c = 0; c < r.Length && c < nc; c++) w[c] = Math.Max(w[c], Width(r[c]));
            }

            var numeric = new bool[nc];
            for (int c = 0; c < nc; c++)
            {
                bool any = false, all = true;
                for (int k = 1; k < rows.Count; k++)
                {
                    var r = rows[k];
                    if (r is null || c >= r.Length) continue;
                    string t = r[c].Trim();
                    if (t.Length == 0) continue;
                    if (!IsNumeric(r[c])) { all = false; break; }
                    if (t != "—" && t != "–") any = true;
                }
                numeric[c] = all && any;
            }

            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                if (r is null)
                {
                    char ch = lines[i + k].Trim().FirstOrDefault(x => x != ' ');
                    sb.Append(Rule(w.Sum() + 2 * Math.Max(0, nc - 1), ch == '\0' ? '─' : ch));
                }
                else
                {
                    for (int c = 0; c < r.Length && c < nc; c++)
                    {
                        bool last = c == Math.Min(r.Length, nc) - 1;
                        sb.Append(numeric[c] ? PadL(r[c], w[c]) : (last ? r[c] : PadR(r[c], w[c])));
                        if (!last) sb.Append("  ");
                    }
                }
                if (i + k + 1 < lines.Length) sb.Append(nl);
            }
            i = j;
        }
        return sb.ToString();
    }
}
