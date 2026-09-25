using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **保温方案** —— 交付给现场的就是这张表：每一区**用什么材质、包多厚**（2026-09-18，Opus 5）。
///
/// ══ 出处（用户 2026-09-18 原话）
///
/// 主会话问的是「圆盘区能不能用预制保温块做到 20 mm」，用户答：
/// <code>「还是只给材质保温厚度方案就行」</code>
/// ⇒ APP 的交付物是**材质与各区厚度方案**；**怎么包（缠绕还是预制保温块）由现场定**。
///   「缠得出来」因此由硬安全线降为参考行（见 <see cref="WrapLimits"/>），
///   而**厚度方案本身**必须在界面与报告上看得到 —— 这张表就是它。
///
/// ══ 三处同一个来源（输出框、安装报告、③ 页那张表）
///
/// 与 <see cref="FlangeKit"/> 同一套做法：数只在这里算一次，三处都调 <see cref="Build"/>／<see cref="Text"/>。
///
/// ══ 每一栏打哪来（**只用生产件的数，一个都不手抄**）
///
/// <code>
///   材质名    管身  = DesignInputs.Layer1/2/3 的 Name（参数表那三层）
///             圆盘/舌板 = 与内层①同一材料 —— 这不是本档的主张，是 DesignScreen.PlateFluxWPerM2
///                        （法兰表面热流的唯一配方）拿 p.Layer1.K0/K1 造保温层这件事本身
///   k(T)      InsulationLayer.KAt(T̄)（= k0 + k1·T̄，与热解同一支函数）
///   出处      InsulationLayer.KSourceNote；**空 = 印「出处待补」**（没有出处不许编一个）
///   厚度      管身   = DesignSpec.TubeInsulMm（BuildCase 写进 Layer1.ThicknessMm 的就是它）
///             圆盘   = DesignSpec.DiscInsulMmOf(j)（与算例同一口径，FlangeKit 也取它）
///             舌板   = DesignSpec.TabInsulMm[j]（求解器的旋钮终值）
///             管端额外 = DesignInputs.EndInsulExtraMm × 长 EndInsulLengthMm
///   折合层数  InsulationSearch.LayersText（全仓唯一一份折算）
///   圈数提示  WrapLimits.TurnsLine（全仓唯一一份写法；超 20 圈就带上「现场需预制保温块」）
/// </code>
///
/// ⚠ **k 那一栏只作对照**：热解里 k 是按**各层界面的平均温度**逐层取的（<see cref="Insulation.CylinderLoss"/>），
///   不是按表上这一个温度。表上印的温度是本次结果里**最高的那个段控温点**（取自 <see cref="LineResult.Segments"/>），
///   印出来是为了让人看得出 k 随温度往哪个方向走，**不是热解用的那个数**。
///
/// ⚠ 片名取自 <see cref="LineResult.Flanges"/>（这一次真的解了哪几片），不在这里另起一套片名。
///
/// 2026-09-18，Opus 5
/// </summary>
public static class InsulationPlan
{
    /// <summary>出处栏没有东西可印时印的那四个字（全仓唯一一份写法；不许在别处编一个出处）。</summary>
    public const string NoSourceText = "出处待补";

    /// <summary>这张表的名字（③ 页那个页签、安装报告那一节、输出框那一段，三处同一个写法）。</summary>
    public const string Title = "保温方案";

    /// <summary>安装报告里这一节的标题（报告与门共用一份写法）。</summary>
    public const string SectionTitle = Title + "（材质 · 各区厚度 · 折合层数 · 圈数提示）";

    /// <summary>
    /// 表头那一句：这张表是什么、包法归谁管。
    /// ⚠ **不带 markdown 星号**：这一句同时进 ③ 页那个 <c>Label</c>，而 Label 不认 <c>**</c> ——
    ///   2026-09-18 抓图（`uishot_M_0918b/03e`）上原文就是两个星号，照实印在界面上。
    ///   要加粗到输出框里去加（那边由 TextFmt 排版）。
    /// </summary>
    public static string Head =>
        Title + " —— 交付的是材质与各区厚度，怎么包（缠绕或预制保温块）由现场定（用户 2026-09-18）。";

    /// <summary>报告里用的抬头：<see cref="Head"/> ＋ 那句原话的全文出处。</summary>
    public static string HeadWithSource => Head + "　出处：" + WrapLimits.PlanOnlyNote;

    public sealed class Row
    {
        /// <summary>区（管身第 n 层／圆盘（片名）／舌板（片名）／管端额外保温）。</summary>
        public string Zone = "";
        /// <summary>材质名（参数表那一层的名字）。</summary>
        public string Material = "";
        /// <summary>k(T) 这一栏的文字（含系数与对照温度上的值）。</summary>
        public string KText = "";
        /// <summary>k 的出处；没有就是 <see cref="NoSourceText"/>。</summary>
        public string KSource = NoSourceText;
        /// <summary>厚度 mm（NaN = 算不出来，照实印「—」）。</summary>
        public double ThickMm = double.NaN;
        /// <summary>折合层数（<see cref="InsulationSearch.LayersText"/> 给的那句）。</summary>
        public string Layers = "";
        /// <summary>圈数提示（<see cref="WrapLimits.TurnsLine"/> 给的那句）。</summary>
        public string Turns = "";
        /// <summary>备注（覆盖范围、模型口径等）。</summary>
        public string Note = "";
    }

    /// <summary>k 那一栏的对照温度 °C —— 本次结果里最高的段控温点；没有段就退回参数表的设定温度。</summary>
    public static double RefTempC(LineResult? r, DesignInputs p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        var sets = r?.Segments?.Where(s => s is not null && double.IsFinite(s.SetpointC)).Select(s => s.SetpointC).ToArray()
                   ?? Array.Empty<double>();
        return sets.Length > 0 ? sets.Max() : p.TSetC;
    }

    /// <summary>k 那一栏的文字（系数 + 两个温度上的值）。与热解同一支函数 <see cref="InsulationLayer.KAt"/>。</summary>
    private static string KTextOf(InsulationLayer l, double tRefC) =>
        $"k = {l.K0:0.####} + {l.K1:0.######}·T̄　⇒ k(20 °C) = {l.KAt(20.0):0.###}、k({tRefC:0} °C) = {l.KAt(tRefC):0.###} W/(m·K)";

    private static string SourceOf(InsulationLayer l) =>
        string.IsNullOrWhiteSpace(l.KSourceNote) ? NoSourceText : l.KSourceNote.Trim();

    /// <summary>
    /// 造这张表。片名取自 <paramref name="r"/>（这一次真的解了哪几片），厚度取自 <paramref name="d"/>／<paramref name="p"/>（与算例同一口径）。
    /// </summary>
    public static List<Row> Build(LineResult r, DesignSpec d, DesignInputs p)
    {
        if (r is null) throw new ArgumentNullException(nameof(r));
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (p is null) throw new ArgumentNullException(nameof(p));

        double tRef = RefTempC(r, p);
        var rows = new List<Row>();

        // ── 管身：参数表那三层。内层①的厚度在算例里由 DesignSpec.TubeInsulMm 写入（BuildCase），这里取同一个数。
        var tubeLayers = new (InsulationLayer L, string Where, double Mm)[]
        {
            (p.Layer1, "管身 内层（贴铂）", d.TubeInsulMm),
            (p.Layer2, "管身 中层",         p.Layer2.ThicknessMm),
            (p.Layer3, "管身 外层",         p.Layer3.ThicknessMm),
        };
        foreach (var (l, where, mm) in tubeLayers)
        {
            if (!l.Enabled || !(mm > 1e-6)) continue;
            rows.Add(new Row
            {
                Zone = where, Material = l.Name, KText = KTextOf(l, tRef), KSource = SourceOf(l),
                ThickMm = mm, Layers = InsulationSearch.LayersText(mm),
                Turns = WrapLimits.TurnsLine(where, mm),
                Note = "沿管全长等厚（模型里管保温是一个数管全长；管端靠圆盘那一小段现场会渐变减薄，模型未建）。"
                     + "管身不在接合区之列（用户 2026-09-17：「其它地方(舌板与管)好缠绕」）。",
            });
        }

        // ── 圆盘与舌板：材料 = 内层①（这一条不是本档的主张 —— DesignScreen.PlateFluxWPerM2 就是拿 Layer1 的 k 造保温层的）
        int n = r.Flanges.Length > 0 ? r.Flanges.Length : d.FlangeCount;
        for (int j = 0; j < n; j++)
        {
            string pn = j < r.Flanges.Length && r.Flanges[j] is not null && r.Flanges[j].Name.Length > 0
                        ? r.Flanges[j].Name : $"片{j + 1}";
            double disc = d.DiscInsulMmOf(j);
            rows.Add(new Row
            {
                Zone = $"圆盘 {pn}", Material = p.Layer1.Name, KText = KTextOf(p.Layer1, tRef), KSource = SourceOf(p.Layer1),
                ThickMm = disc, Layers = InsulationSearch.LayersText(disc),
                Turns = WrapLimits.TurnsLine($"圆盘 {pn}", disc),
                Note = "单面厚度，双面各包一层。现模型整盘只有一个厚度（分不了内外环）⇒ 接合区那一圈按整盘这个厚度报圈数。",
            });
        }
        for (int j = 0; j < n; j++)
        {
            string pn = j < r.Flanges.Length && r.Flanges[j] is not null && r.Flanges[j].Name.Length > 0
                        ? r.Flanges[j].Name : $"片{j + 1}";
            double tab = j < d.TabInsulMm.Length ? d.TabInsulMm[j] : double.NaN;
            rows.Add(new Row
            {
                Zone = $"舌板 {pn}", Material = p.Layer1.Name, KText = KTextOf(p.Layer1, tRef), KSource = SourceOf(p.Layer1),
                ThickMm = tab, Layers = InsulationSearch.LayersText(tab),
                Turns = WrapLimits.TurnsLine($"舌板 {pn}", tab),
                Note = "只包圆盘切点到压接段前那一段，压接段由铜排夹住不包。舌板不在接合区之列（好缠绕，不受圈数参考线约束）。",
            });
        }

        // ── 管端额外保温（有才列）
        if (p.EndInsulExtraMm > 1e-6 && p.EndInsulLengthMm > 1e-6)
            rows.Add(new Row
            {
                Zone = "管端额外保温", Material = p.Layer1.Name, KText = KTextOf(p.Layer1, tRef), KSource = SourceOf(p.Layer1),
                ThickMm = p.EndInsulExtraMm, Layers = InsulationSearch.LayersText(p.EndInsulExtraMm),
                Turns = WrapLimits.TurnsLine("管端额外保温", p.EndInsulExtraMm),
                Note = $"叠在管身保温之外，自管两端各算起 {p.EndInsulLengthMm:0} mm 内；用来填端部冷坑。"
                     + "它与管身一样不在接合区之列。",
            });

        return rows;
    }

    /// <summary>输出框与安装报告共用的那段文字（制表位表，与本仓其余表同一排法）。</summary>
    public static string Text(IReadOnlyList<Row> rows, double tRefC)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var sb = new StringBuilder();
        sb.AppendLine("  " + HeadWithSource);
        sb.AppendLine($"  k 那一栏的对照温度取 {tRefC:0} °C（本次结果里最高的段控温点）—— **只作对照**："
                    + "热解里 k 是按各层界面平均温度逐层取的，不是按这一个温度。");
        sb.AppendLine("区\t材质\t厚度 mm\t折合层数\tk(T) 与出处\t圈数提示 / 备注");
        foreach (var w in rows)
            sb.AppendLine($"{w.Zone}\t{w.Material}\t{(double.IsNaN(w.ThickMm) ? "—" : w.ThickMm.ToString("0.0"))}\t{w.Layers}"
                        + $"\t{w.KText}　出处：{w.KSource}"
                        + $"\t{w.Turns}　{w.Note}");
        if (rows.Any(w => w.KSource == NoSourceText))
            sb.AppendLine($"  ⚠ 印着「{NoSourceText}」的那几行：保温材料的 k(T) 系数在仓库里查不到来源 —— "
                        + "拿到供应商数据后填 InsulationLayer.KSourceNote，这一栏会跟着变。**不许在报告里编一个出处。**");
        return sb.ToString();
    }

    /// <summary>一步到位：造表 + 排版（输出框与安装报告都调它，省得两处各拼一遍）。</summary>
    public static string Text(LineResult r, DesignSpec d, DesignInputs p)
        => Text(Build(r, d, p), RefTempC(r, p));
}
