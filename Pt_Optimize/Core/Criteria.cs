using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// **判据代号对照表**（2026-08-29，用户：「①②′②″ 这种代号要有对照表，
/// APP 内任何一处讯息与 UI 说明书内**不可以用代号说明**」）。
///
/// ══ 病在哪
///
/// 代号是**给写的人省事的**，不是给读的人用的。实测扫描全仓：
/// **197 行在判据语境里用了代号，其中 155 行没带自己的名字**。
/// 最大一类是**表头**（`②′W`、`②″K`、`③K`）—— 那里确实放不下全名，
/// 但那正是最需要图例的地方：一张全是代号的表，读的人得去别处查才看得懂，
/// 而「得去别处查」在现场就等于「不查，猜」。
///
/// ══ 单一来源
///
/// ★ 本表**不另写一份名字**。判据自己的常量已经是「代号 + 名字」
/// （<c>LineResult.Key.FlangeDip == "③ 法兰增量温降"</c>），本表从它**拆**出来。
/// 判据改名 ⇒ 对照表跟着改，不可能脱节。
/// 这也是 2026-08-29 先给 ④ 等几条补上 <c>Key</c> 常量的原因 ——
/// 没有常量就只能抄一份名字，而「同一个名字两处来源」是本仓库最常见的病。
///
/// ══ **限值不在这里**
///
/// ⚠ 本表只给「代号 = 名字（单位，方向）」，**不给限值数字**。
/// 限值只有一个来源：<c>LineCase</c>（判据自己带着 <c>Limit</c>）。
/// 在这里再写一份 10 K / 5 K，就会出现「印出来的 ≠ 判的」——
/// 本仓库为这件事栽过不止一次（见 `SingleSourceLimitTests`）。
/// 要看限值就看判据表那一列。
/// </summary>
public static class Criteria
{
    /// <summary>一条判据在对照表里的样子。</summary>
    public sealed class Entry
    {
        /// <summary>代号，如 <c>③</c>、<c>②′</c>；参考量为 <c>·</c> 开头或空。</summary>
        public string Code = "";
        /// <summary>去掉代号之后的名字，如「法兰增量温降」。</summary>
        public string Name = "";
        /// <summary>单位。</summary>
        public string Unit = "";
        /// <summary>方向：<c>≤</c> / <c>≥</c> / <c>&gt;</c>。</summary>
        public string Dir = "";
        /// <summary>一句话：它到底在管什么（给读的人，不是给写的人）。</summary>
        public string Means = "";
        /// <summary>true = 硬安全线（卡交付）；false = 参考量。</summary>
        public bool Hard;
        /// <summary>判据的完整名字（= <c>LineResult.Key</c> 里那个常量）。</summary>
        public string Key = "";
    }

    private static Entry E(string key, string unit, string dir, string means, bool hard)
    {
        // 从 Key 里**拆**出代号与名字，不另写 —— 判据改名这里自动跟着改。
        string k = key.Trim();
        string code, name;
        if (k.StartsWith("·", StringComparison.Ordinal))
        {
            name = k[1..].Trim();
            // 「· ② 法兰最高温」这种：代号是 ·②
            if (name.Length > 0 && "①②③④⑤⑥".Contains(name[0]))
            {
                code = "·" + name[0];
                name = name[1..].Trim();
            }
            else
            {
                // ★ 没有圈号的参考量**本来就没有代号**（2026-08-29 修）。
                //   此前它们全被塞成同一个「·」—— 那不是代号，是**分类标记**，
                //   于是九条参考量共用一个「代号」，Of("·") 说不清指哪条。
                //   ⇒ 空代号 = 明确表示「这条没有代号，按名字认」。
                code = "";
            }
        }
        else if (k.Length > 0 && "①②③④⑤⑥".Contains(k[0]))
        {
            int n = 1;
            if (k.Length > 1 && (k[1] == '′' || k[1] == '″')) n = 2;
            code = k[..n];
            name = k[n..].Trim();
        }
        else { code = ""; name = k; }
        return new Entry { Code = code, Name = name, Unit = unit, Dir = dir,
                           Means = means, Hard = hard, Key = key };
    }

    /// <summary>
    /// 全表。顺序 = 硬安全线在前、参考量在后，各自按代号。
    /// ⚠ 「意思」那一列写的是**失效模式**，不是公式 —— 读的人要判的是「离哪个坏结果近」。
    /// </summary>
    public static readonly Entry[] All =
    {
        // ── 硬安全线（卡交付）
        E(LineResult.Key.Ramp,      "h",     "≤", "空管升到目标温度要多久；太慢说明整线发热不够", true),
        E(LineResult.Key.NetFlux,   "W",     ">", "热是从管子流进法兰（安全），还是倒灌进管子（**烧断的方向**）", true),
        E(LineResult.Key.DiscTemp,  "K",     "≤", "贴着管孔那一圈盘面比管子热多少；热是孔周电流拥塞顶出来的尖峰", true),
        E(LineResult.Key.FreeTab,   "mm",    "≥", "舌片伸出来、没被压接吃掉的那一段够不够长 —— 现场铜排装得下吗", true),
        E(LineResult.Key.DiscCover, "mm",    "≥", "圆盘半径够不够盖住管孔加焊脚 —— 盖不住就焊不出来", true),
        E(LineResult.Key.TubeJ,     "A/mm²", "≤", "管子自身的电流密度上限", true),
        E(LineResult.Key.SectionJ,  "A/mm²", "<", "法兰每一个必经截面（舌片各处含开孔、舌盘交界、孔缘环与各级环）的电流密度 = 升温设计电流 ÷ 截面积；按 10 定尺寸，全体要小于 11", true),

        // ── 靶（列进「必须出现」名单，但不算硬安全线）
        E(LineResult.Key.FlangeDip, "K",     "≤", "**法兰把管根拉冷了多少** —— 只算法兰的责任，不含控温点梯度", false),

        // ── 参考量（印出来，不卡交付）
        E(LineResult.Key.TubeStrength,  "—",     "≤", "管子的强度用掉了几成（Pt 持久强度实测区间外时**判不了**）", false),
        E(LineResult.Key.FlangeTopTemp, "K",     "≤", "整片法兰（含舌片）最高温比管温高多少", false),
        E(LineResult.Key.SetpointDrift, "K",     "≤", "管温偏离本段控温点多少 —— **由控温点梯度决定，法兰管不着**", false),
        E(LineResult.Key.SelfSupply,    "—",     "≥", "法兰自身发热够不够养活自身散热", false),
        E(LineResult.Key.FlangeJ,       "A/mm²", "≤", "法兰上的电流密度峰值（≠「局部热稳定」那条）", false),
        E(LineResult.Key.FlangeStab,    "×",     "≥", "整片热稳定：散热随温度涨得比发热快多少倍", false),
        E(LineResult.Key.LocalStab,     "×",     "≥", "局部热稳定：峰值点离热失控还有几倍余量", false),
        E(LineResult.Key.RampField,     "K",     "≤", "现场升温过程中「法兰−管」温差的全程最大值", false),
        E(LineResult.Key.HeatBalance,   "W",     "≈", "管子失去的热与法兰收到的热对不对得上（守恒对账）", false),
        E(LineResult.Key.HeatResidual,  "W",     "≈", "单片法兰自己的热平衡残差，应接近 0", false),
        E(LineResult.Key.GlassDrop,     "K",     "≤", "玻璃温降与现场实测差多少", false),
    };

    /// <summary>
    /// 按代号找。找不到返回 null（**不要**编一个出来）。
    ///
    /// ⚠ 空串/空白**不是代号**，一律返回 null（2026-08-29 修）：
    ///   没有圈号的参考量 <see cref="Entry.Code"/> 是空的，
    ///   若不挡住，<c>Of("")</c> 会命中其中随便一条 —— 那是个说不清指哪条的答案，
    ///   而它**看起来完全正常**。
    /// </summary>
    /// <summary>
    /// ★★★ **界面用的判据名：把代号剥掉**（2026-08-30，用户原话「工程师看不懂」）。
    ///
    /// ══ 为什么代号会流到界面上
    ///
    /// 判据的 <see cref="LineResult.Key"/> 常数**自己就带着代号**：
    /// <code>
    ///   NetFlux   = "②′管孔净流入"
    ///   DiscTemp  = "②″圆盘区最高温"
    ///   FlangeDip = "③ 法兰增量温降"
    /// </code>
    /// 而判据表直接印 <c>ConstraintOut.Name</c> ⇒ 代号是**从内核流到界面上**的，
    /// 散落在各处提示语里的那些只是支流。
    ///
    /// ══ 为什么不改 Key
    ///
    /// Key 是全仓**唯一来源**：门（<c>Flow.RequiredChecks</c>）、判据匹配
    /// （<c>Name.StartsWith(key)</c>）、命令行、测试、交接文档都靠它。
    /// 改它等于同时改识别与显示两件事 —— 而只有显示要改。
    /// ⇒ **内部身份不动，显示层剥壳**，剥壳只有这一个函数。
    ///
    /// ══ 为什么代号必须走干净，而不是「带上解释就行」
    ///
    /// 2026-08-29 做过一版「代号必须带解释」（对照表 + 展开 12 处）。**不够**：
    /// 判据代号 ⑤（舌片自由段）与页签上的**阶段号** ⑤（交付）**形状相同、含义无关**，
    /// 摆在同一个界面上必然误读。⇒ 界面侧根本不出现代号。
    /// 对照表留着给**命令行与文档**用 —— 那两处的读者是开发者。
    /// </summary>
    /// <param name="nameOrKey">判据名或 Key（<c>ConstraintOut.Name</c> 直接传进来即可）。</param>
    public static string Plain(string? nameOrKey)
    {
        if (string.IsNullOrWhiteSpace(nameOrKey)) return "";
        string k = nameOrKey.Trim();

        // 「· ② 法兰最高温」这种：先剥分类标记，再剥代号
        if (k.StartsWith("·", StringComparison.Ordinal)) k = k[1..].TrimStart();
        if (k.Length > 0 && "①②③④⑤⑥".Contains(k[0]))
        {
            int n = 1;
            if (k.Length > 1 && (k[1] == '′' || k[1] == '″')) n = 2;
            k = k[n..].TrimStart();
        }
        return k;
    }

    /// <summary>
    /// 界面里出现代号就是违规 —— 给门用的判定（<c>true</c> = 这段文字里有代号）。
    /// ⚠ 只认**判据代号**：圈号后面跟 ′ ″ 的，或圈号紧贴汉字的。
    ///   页签上的阶段号带全名（「① 先决条件（能造·能升温）」）是导航编号，不在此列 ——
    ///   但那种写法里圈号后面是空格加中文，与判据代号形状一样，
    ///   所以门只扫**判据相关**的字符串，不做全局正则。见 UiNoCodeTests。
    /// </summary>
    public static bool HasCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++)
            if ("①②③④⑤⑥".Contains(text[i])) return true;
        return false;
    }

    /// <summary>
    /// 判据的单位，按**名字**查（<c>ConstraintOut.Name</c> 直接传进来即可）。
    ///
    /// 用户 2026-09-02 那句「代号全换成全名（<b>+单位</b>）」的后半截：
    /// 判据表原来只印「429.580 / 限 10.000」，**没有一个字说这是 K 还是 W** ——
    /// 而 ③ 是 K、②′ 是 W、⑤⑥ 是 mm、管 J 是 A/mm²，四种单位混在同一张表里。
    ///
    /// ⚠ 先按全名精确匹配，匹配不上再按前缀 —— <c>Name</c> 往往比 <c>Key</c> 长
    ///   （如「③ 法兰增量温降 ≤ 上限」）。<b>找不到就返回空字符串，绝不编一个单位。</b>
    /// ⚠ 单位是「—」的（无量纲比值）不算单位，也返回空 —— 印出来只是噪音。
    /// </summary>
    public static string UnitOf(string? nameOrKey)
    {
        string p = Plain(nameOrKey);
        if (p.Length == 0) return "";
        var hit = All.FirstOrDefault(e => Plain(e.Key) == p)
               ?? All.FirstOrDefault(e => p.StartsWith(Plain(e.Key), StringComparison.Ordinal));
        string u = hit?.Unit ?? "";
        return u == "—" ? "" : u;
    }

    public static Entry? Of(string code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(e => e.Code == code);

    /// <summary>
    /// 单个代号的展开，用在**散文**里：<c>③（法兰增量温降）</c>。
    /// 找不到就原样返回代号 —— 不许编名字。
    /// </summary>
    /// ★★ 2026-08-30 起**只给名字，不给代号**（用户：「工程师看不懂」）。
    ///   此前返回「⑤（舌片自由段）」—— 那是 08-29 那版「代号必须带解释」的做法。
    ///   改这一处，所有调用点跟着对。
    ///   ⚠ 代号本身仍在 Entry.Code 上，命令行与文档照常用。
    ///   ⚠⚠ 判据的 Key 常数（LineResult.Key.NetFlux = "②′管孔净流入"）**绝不能动** ——
    ///     它是识别用的唯一来源。2026-08-30 我用正则全仓替换时把它也改了，
    ///     变成「管孔净流入管孔净流入」，四条门当场红。剥壳只准在显示层做。
    public static string Explain(string code)
    {
        var e = Of(code);
        return e is null ? code : e.Name;
    }

    /// <summary>
    /// **表格图例**：用在拿代号当列头的表底下。
    /// 只给「代号 = 名字（单位 方向）」，**不给限值数字**（限值只从判据读）。
    /// </summary>
    public static string Legend(params string[] codes)
    {
        var parts = new List<string>();
        foreach (var c in codes)
        {
            var e = Of(c);
            if (e is null) continue;
            parts.Add($"{e.Code} = {e.Name}（{e.Unit}，{e.Dir}）");
        }
        return parts.Count == 0 ? "" : "　代号：" + string.Join("｜", parts) + "　限值见判据表";
    }

    /// <summary>
    /// 说明书用的 HTML 表。**与 <see cref="Table"/> 同一份数据** ——
    /// 文本一份、HTML 一份地各写各的，正是「同一件事两处来源」。
    /// </summary>
    public static string Html()
    {
        var sb = new StringBuilder();
        sb.Append("<h3>判据全表</h3>");
        sb.Append("<p>这是 APP 会判、会印出来的<b>全部</b>判据。名字与单位跟屏幕上一模一样 —— ");
        sb.Append("判据表里那行字长什么样，这里就长什么样，不用换算、不用对照。</p>");
        sb.Append("<p>⚠ <b>本表不含限值数字</b> —— 限值只有一个来源（判据自己）。");
        sb.Append("要看限值请看下面「限值的出处」那张表，两张表不会打架。</p>");
        foreach (var hard in new[] { true, false })
        {
            sb.Append(hard ? "<h4>硬安全线（不过就不能交付）</h4>"
                           : "<h4>参考量 / 靶（印出来，不卡交付）</h4>");
            // ★★★ 2026-09-03：名字列改印 **Plain(Key)**，不再印 Key 本身。
            //   Key 自带代号（"②′管孔净流入"），而**判据表画到屏幕上时走的就是 Plain**
            //   （LineDesignPage 三处都是 Criteria.Plain(c.Name)）⇒ 印 Key 反而与屏幕对不上。
            //   ⚠ 连带改了 UiWiring 那条「说明书里查得到」的门：它原来找 Key 原文，
            //     现在找 Plain(Key) —— 找的是**工程师真的看得见的那个字串**，比原来更准。
            //   ⚠⚠ Key 常量本身一个字都没动（它是识别用的唯一来源）。
            sb.Append("<table class=\"nw\"><tr><th>判据（APP 里显示的名字）</th><th>单位</th>"
                    + "<th>方向</th><th>它在管什么</th></tr>");
            foreach (var e in All.Where(x => x.Hard == hard))
                sb.Append($"<tr><td><b>{Plain(e.Key)}</b></td><td class=\"n\">{e.Unit}</td>"
                        + $"<td class=\"n\">{e.Dir}</td><td>{e.Means}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<p>★ <b>「管孔净流入」与「圆盘区最高温」是同一条安全线的两个视角</b> —— ");
        sb.Append("前者从管子看热流方向（热该往法兰走，不该往管里灌），");
        sb.Append("后者从法兰看圆盘区温度。两条一起看才判得准，缺一条就会漏掉一个失效方向。</p>");
        return sb.ToString();
    }

    /// <summary>完整对照表，给 `--glossary` 与说明书用。</summary>
    public static string Table()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 判据代号对照表 ===");
        sb.AppendLine("⚠ 本表**不含限值数字** —— 限值只有一个来源：判据自己（LineCase）。");
        sb.AppendLine("  在这里再抄一份，就会出现「印出来的 ≠ 判的」。要看限值就看判据表那一列。");
        sb.AppendLine();
        foreach (var group in new[] { true, false })
        {
            sb.AppendLine(group ? "── 硬安全线（不过就不能交付）" : "── 参考量 / 靶（印出来，不卡交付）");
            foreach (var e in All.Where(x => x.Hard == group))
                sb.AppendLine($"  {(e.Code.Length == 0 ? "·" : e.Code),-3} {e.Key,-22} {e.Unit,-6} {e.Dir,-2}  {e.Means}");
            sb.AppendLine();
        }
        sb.AppendLine("★ 代号里的 ′ 与 ″ 不是次要标记：**②′ 与 ②″ 是同一条安全线的两个视角** ——");
        sb.AppendLine("  ②′ 从管子看热流方向，②″ 从法兰看圆盘区温度。");
        return sb.ToString();
    }
}
