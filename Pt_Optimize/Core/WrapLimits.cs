using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **接合区的保温要缠多少圈** —— 全仓唯一一份缠绕圈数口径（2026-09-17 写，2026-09-18 降为参考行，Opus 5）。
///
/// ══ 出处（用户 2026-09-17，现场原话）
///
/// <code>
///   「现场法兰与管保温带一般缠绕 20 圈以下，再往上很难缠绕(会渐成球形)」
///   「圆盘与管子接触区都是 20 圈以下，其它地方(舌板与管)好缠绕」
/// </code>
/// 每圈 = <see cref="InsulationSearch.LayerMm"/>（0.5 mm，全仓唯一一份层厚）⇒ **接合区参考线 10 mm**。
///
/// ══ ★★★★★ 2026-09-18 改口：**这条线不再卡交付**（用户当日原话）
///
/// 主会话问的是「圆盘区能不能用预制保温块做到 20 mm」，用户答：
/// <code>「还是只给材质保温厚度方案就行」</code>
/// ⇒ APP 的交付物是**材质与各区厚度方案**，**怎么包由现场定**（缠、还是预制成块）。
///   缠不缠得出来是现场工艺的事，不是设计可行性的事 ——
///   拿它去判「这份设计不成立」，会把一批现场做得出来（预制块）的设计判死。
/// ⇒ 本条由**硬安全线降为参考行**（<see cref="CheckKind.Reference"/>）：
///   照常算、照常印，不进 <see cref="LineResult.AllOk"/>／<see cref="LineResult.HardOk"/>，不卡交付。
///   报告里印的是**圈数提示**：<see cref="TurnsLine"/>（「圆盘区 X mm ≈ Y 圈（每层 0.5 mm）；超过 20 圈现场需预制保温块」）。
///
/// ⚠ 与之配套，三个被 §0.-11 压下去的数**退回 §0.-11 之前的值**（用户 2026-09-18 定）：
///   <see cref="DesignSpec.FlangeInsulMm"/> 默认 10 → 20；界面「法兰保温厚」上界 10 → 60（<see cref="DesignSpec.FlangeInsulMaxMm"/>）；
///   <see cref="InsulationSearch.Options.DiscLayerMax"/> 20 层 → 40 层。
///   §0.-11 里「10 mm 下两份设计都不成立、一个可行窗口都没有」那些**实测结论原样留着**，
///   只是它们所依据的口径（上限硬卡 10 mm）已经不再是现行口径。
///
/// ══ 「接合区」指哪里（口径写死在这里，报告与界面都引这一段）
///
/// **只有圆盘**。用户原话把零件分成两半：「圆盘与管子接触区」缠不上去，「其它地方(舌板与管)好缠绕」。
///   · 计入圈数提示：**圆盘保温**（逐片 <see cref="FlangePlate.DiscInsulThickMm"/>）——
///     要绕过管–盘那个直角的就是这一圈；现模型整盘只有一个厚度（见下），所以整盘那一个厚度按接合区报圈数；
///   · **不计入：管保温、舌板保温**（含管子贴法兰那一段的端部额外保温 <see cref="DesignInputs.EndInsulExtraMm"/>）——
///     用户原话「其它地方(舌板与管)好缠绕」。本条**不去碰它们**。
///
/// ⚠⚠ **模型缺口（推理，不是实测）**：管保温沿全长**等厚**（<c>DesignInputs.Layer1.ThicknessMm</c> 一个数管全长），
///   管端靠圆盘那一小段现场会渐变减薄以让开那个直角，**模型未建**。
///   影响是**局部**的；方向上模型**偏乐观**（角部实际比模型多散一点热，模型里那一段仍按全厚保温算）。
///   —— 这一句是从「等厚模型 vs 现场渐变减薄」推出来的，没有实测支撑，标明。
///
/// ⚠⚠ **现模型表达不了「内环带薄、外区厚」**（2026-09-17 Opus 5 实测源码，不是推测）：
///   圆盘保温在热解里只有**一个厚度**（<c>DesignInputs.FlangeInsulThickMm</c>，逐片可不同 ——
///   <see cref="FlangePlate.DiscInsulThickMm"/>／<see cref="LineCase.DiscInsulEffectiveAt"/>），
///   它作用在 <c>r ≤ 盘半径</c> 的**整块圆盘**上（<see cref="ShellThermal"/> 的保温分区按半径，
///   但分的是「圆盘 / 舌片」两区，圆盘内部不再分厚度）。
///   ⇒ 圆盘只要有接合区（管孔半径 &lt; 盘半径，恒真），**整盘那一个厚度就按接合区报圈数**。
///
/// ══ 超了怎么办：**照实报圈数并提示预制保温块，不判不可行、也不外推形状**
///
/// 缠到 20 圈以上会「渐成球形」—— 那是一个**本程序没有模型**的形状（保温外形不再是等厚层，
/// 散热面积与形状因子全变）。本程序算的一直是**等厚层**：
///   · 现场若按**预制保温块**做，等厚层就是对的（这正是用户 2026-09-18 定的做法）；
///   · 现场若硬缠成球形，等厚层算出来的是「看起来正常的错数」——
///     所以这里**只报圈数并提示改用预制块**，不去算球形。
///
/// 2026-09-18，Opus 5
/// </summary>
public static class WrapLimits
{
    /// <summary>接合区**一次缠绕**能缠的最多圈数（用户 2026-09-17）。超过它现场改用预制保温块（用户 2026-09-18）。</summary>
    public const int MaxTurnsAtJoint = 20;

    /// <summary>
    /// 接合区缠绕参考线 mm = <see cref="MaxTurnsAtJoint"/> × <see cref="InsulationSearch.LayerMm"/> = 10.0。
    /// ★ 2026-09-18 起这是**参考线**不是上限（用户当日「还是只给材质保温厚度方案就行」）。
    /// ⚠ 不许在别处再写一个 10 —— 层厚改了这里要跟着改，手抄的那一份不会。
    /// </summary>
    public static double JointZoneMaxMm => MaxTurnsAtJoint * InsulationSearch.LayerMm;

    /// <summary>出处原话（报告、判据说明、界面都引这一份，不各抄一遍）。</summary>
    public const string SourceNote =
        "用户 2026-09-17：「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕(会渐成球形)」"
      + "「圆盘与管子接触区都是20圈以下，其它地方(舌板与管)好缠绕」";

    /// <summary>2026-09-18 改口的出处原话（降为参考行的依据；报告与界面引这一份）。</summary>
    public const string PlanOnlyNote =
        "用户 2026-09-18：「还是只给材质保温厚度方案就行」"
      + "—— APP 只给材质与各区厚度方案，怎么包（缠绕或预制保温块）由现场定 ⇒ 本条只作参考，不卡交付。";

    /// <summary>超过一次缠绕圈数时印的那句提示（全仓唯一一份写法；圈数不手抄）。</summary>
    public static string PrefabNote => $"超过 {MaxTurnsAtJoint} 圈现场需预制保温块";

    /// <summary>厚度折成圈数（= 层数，每圈一层）。</summary>
    public static double TurnsOf(double mm) => mm / InsulationSearch.LayerMm;

    /// <summary>
    /// ★ 2026-09-18，Opus 5：**圈数提示的唯一一份写法** —— 本条说明、保温方案表、安装报告、输出框都调它。
    /// 形如「圆盘区 12.5 mm ≈ 25 圈（每层 0.5 mm）；超过 20 圈现场需预制保温块」。
    /// </summary>
    public static string TurnsLine(string where, double mm)
    {
        if (double.IsNaN(mm)) return $"{where} —（算不出来）";
        double t = TurnsOf(mm);
        return $"{where} {mm:0.###} mm ≈ {t:0.#} 圈（每层 {InsulationSearch.LayerMm:0.#} mm）"
             + (t > MaxTurnsAtJoint + 1e-9 ? $"；{PrefabNote}" : "");
    }

    /// <summary>接合区口径一句话（界面与报告引用）。</summary>
    public static string ZoneNote =>
        "接合区 = 圆盘靠管孔的内环带（只看圆盘保温）；管保温、舌板保温不看（用户原话「其它地方(舌板与管)好缠绕」）。"
      + "⚠ 现模型圆盘保温整盘只有一个厚度（不分内外环），所以整盘按接合区报圈数。"
      + $"⚠ 本条**只作参考、不卡交付**：{PlanOnlyNote}"
      + "⚠ 模型缺口（推理）：管保温沿全长等厚，管端靠圆盘那一小段现场会渐变减薄，模型未建；"
      + "影响局部，方向是模型偏乐观（角部实际比模型多散一点热）。";

    /// <summary>
    /// 逐项列出接合区上要缠的保温厚度（参考行、界面、报告共用这一份清单）。
    ///
    /// ★ 2026-09-18，Opus 5：**只有圆盘保温**。2026-09-17 那一版还把「管端段（管保温 + 端部额外保温）」
    ///   也列进来（理由是模型里管保温沿全长等厚，管端那一小段跟着厚），那与用户原话
    ///   「其它地方(舌板与管)好缠绕」**相反** —— 用模型的缺口去给现场加一条现场没有的限制，
    ///   会把一批现场缠得出来的设计判成不可行。⇒ 管保温、舌板保温**都不进这份清单**。
    ///   模型缺口（管端渐变减薄没建）记在 <see cref="ZoneNote"/> 里，标明是推理。
    /// </summary>
    public static (string Where, double Mm)[] JointItems(LineCase c)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        var items = new List<(string, double)>();
        string[] pn = { "入口", "共用1", "共用2", "出口" };
        int n = c.FlangePlates.Length > 0 ? c.FlangePlates.Length : Math.Max(0, c.FlangeCount);
        for (int j = 0; j < n; j++)
            items.Add(((j < pn.Length ? pn[j] : $"片{j + 1}") + " 圆盘保温", c.DiscInsulEffectiveAt(j)));
        return items.ToArray();
    }

    /// <summary>
    /// 本条本体：接合区最厚的那一处折成几圈。
    /// ★ 2026-09-18 起 <see cref="CheckKind.Reference"/> —— **照常算、照常印，不卡交付**（用户当日原话，见 <see cref="PlanOnlyNote"/>）。
    /// 闭式、纯输入（不吃场）⇒ 网格与工况都不影响它，两态逐位相同。
    /// **任何一项算不出来 ⇒ 整条判不了**（判不了不算过，也不算不过 —— 参考行同样不许拿初值顶）。
    /// </summary>
    public static ConstraintOut Judge(LineCase c)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        var items = JointItems(c);
        var blind = items.Where(t => double.IsNaN(t.Mm)).Select(t => t.Where).ToArray();
        string head = $"参考线 {JointZoneMaxMm:0.#} mm = {MaxTurnsAtJoint} 圈 × 每圈 {InsulationSearch.LayerMm:0.#} mm。{SourceNote}　{ZoneNote}　";

        if (items.Length == 0 || blind.Length > 0)
            return new ConstraintOut
            {
                Name = LineResult.Key.WrapTurns, Unit = "mm", Kind = CheckKind.Reference,
                Actual = double.NaN, Limit = JointZoneMaxMm, LessIsBetter = true,
                Ok = false, Undetermined = true,
                Where = blind.Length > 0 ? string.Join("、", blind) : "—",
                Note = head + (items.Length == 0
                    ? "★ **无法判定**：这个算例一片法兰都没有，接合区无从谈起。"
                    : $"★ **无法判定**：{blind.Length} 处（{string.Join("、", blind)}）的保温厚度算不出来。"
                      + "**任何一处算不出来，整条就判不了** —— 这条报的是「现场要缠几圈」，凭初值报一个圈数等于没算。"
                      + "　【下一步】回 ① 页确认这片法兰的圆盘保温厚度有值（不包就填 0），或改用解析几何路径。")
            };

        var worst = items.OrderByDescending(t => t.Mm).First();
        bool within = worst.Mm <= JointZoneMaxMm + 1e-9;
        return new ConstraintOut
        {
            Name = LineResult.Key.WrapTurns, Unit = "mm", Kind = CheckKind.Reference,
            Actual = worst.Mm, Limit = JointZoneMaxMm, LessIsBetter = true,
            Ok = within, Where = worst.Where,
            Note = head
                 + $"接合区各处：{string.Join("；", items.Select(t => TurnsLine(t.Where, t.Mm)))}。"
                 + (within
                    ? $"最厚的一处在一次缠绕能缠的 {MaxTurnsAtJoint} 圈以内。"
                    : $"★ 最厚的一处是 {worst.Where} {worst.Mm:0.###} mm = {TurnsOf(worst.Mm):0.#} 圈，{PrefabNote}。"
                      + "继续硬缠会渐成球形 —— 那个形状**本程序没有模型**（散热面积与形状因子全变），"
                      + "所以这里**不外推、不硬算**；按预制块做则本程序算的等厚层就是对的。"
                      + "　【现场怎么做由现场定】保温方案表给的是材质与各区厚度，包法（缠绕或预制块）不在本程序的交付范围内。")
        };
    }
}
