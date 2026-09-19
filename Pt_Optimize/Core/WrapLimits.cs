using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **现场能不能把这个保温缠出来** —— 全仓唯一一份缠绕圈数上限（2026-09-17，Opus 5 写）。
///
/// ══ 出处（用户 2026-09-17，现场原话）
///
/// <code>
///   「现场法兰与管保温带一般缠绕 20 圈以下，再往上很难缠绕(会渐成球形)」
///   「圆盘与管子接触区都是 20 圈以下，其它地方(舌板与管)好缠绕」
/// </code>
/// 每圈 = <see cref="InsulationSearch.LayerMm"/>（0.5 mm，全仓唯一一份层厚）⇒ **接合区上限 10 mm**。
///
/// ══ 「接合区」指哪里（口径写死在这里，报告与界面都引这一段）
///
/// 缠不上去的不是整个零件，是**保温带要绕过管–盘那个直角**的那一圈：
///   · 圆盘**靠管孔的内环带**（管孔边缘往外的那一圈盘面）；
///   · 管子**贴着法兰的端部段**（= 管保温 + 端部额外保温，<see cref="DesignInputs.EndInsulExtraMm"/> 那一段）。
/// 圆盘外区、舌板、管身（离开端部之后）**不设限** —— 那是用户原话，本判据不去碰它们。
///
/// ⚠⚠ **现模型表达不了「内环带薄、外区厚」**（2026-09-17 Opus 5 实测源码，不是推测）：
///   圆盘保温在热解里只有**一个厚度**（<c>DesignInputs.FlangeInsulThickMm</c>，逐片可不同 ——
///   <see cref="FlangePlate.DiscInsulThickMm"/>／<see cref="LineCase.DiscInsulEffectiveAt"/>），
///   它作用在 <c>r ≤ 盘半径</c> 的**整块圆盘**上（<see cref="ShellThermal"/> 的保温分区按半径，
///   但分的是「圆盘 / 舌片」两区，圆盘内部不再分厚度）。
///   ⇒ 圆盘只要有接合区（管孔半径 &lt; 盘半径，恒真），**整盘那一个厚度就按接合区判**。
///   要做到「盘大时外区超 10、内环带 ≤ 10」，得先在 PlateCurrent2D／ShellThermal 里加**逐半径厚度**——
///   那件事没做，所以这里不许假装已经能分区（W08/W06 的盘环宽只有 4.2 mm，整盘本来就都是接合区）。
///
/// ⚠ 管身同理：管保温沿全长**等厚**，端部段总厚 = 管保温 + 端部额外保温 ⇒ 管身缠厚了，
///   端部接合区跟着厚，本判据会在那里咬住。要「管身厚、端部薄」得让模型支持轴向分段减薄（没做）。
///
/// ══ 越界怎么办：**判不可行，点名，不建模球形**
///
/// 缠到 20 圈以上会「渐成球形」—— 那是一个**本程序没有模型**的形状（保温外形不再是等厚层，
/// 散热面积与形状因子全变）。凭现有的等厚层公式把它算下去，会给出一个**看起来正常的错数** ——
/// 正是本项目最忌的那一类。⇒ 不外推、不建模，直接报这条硬安全线不过，并说出是哪一片／哪一段越界。
/// </summary>
public static class WrapLimits
{
    /// <summary>接合区能缠的**最多圈数**（用户 2026-09-17）。</summary>
    public const int MaxTurnsAtJoint = 20;

    /// <summary>
    /// 接合区保温厚度上限 mm = <see cref="MaxTurnsAtJoint"/> × <see cref="InsulationSearch.LayerMm"/> = 10.0。
    /// ⚠ 不许在别处再写一个 10 —— 层厚改了这里要跟着改，手抄的那一份不会。
    /// </summary>
    public static double JointZoneMaxMm => MaxTurnsAtJoint * InsulationSearch.LayerMm;

    /// <summary>出处原话（报告、判据说明、界面都引这一份，不各抄一遍）。</summary>
    public const string SourceNote =
        "用户 2026-09-17：「现场法兰与管保温带一般缠绕20圈以下，再往上很难缠绕(会渐成球形)」"
      + "「圆盘与管子接触区都是20圈以下，其它地方(舌板与管)好缠绕」";

    /// <summary>接合区口径一句话（界面与报告引用）。</summary>
    public const string ZoneNote =
        "接合区 = 圆盘靠管孔的内环带 + 管子贴法兰的端部段；圆盘外区、舌板、管身不设限。"
      + "⚠ 现模型圆盘保温整盘只有一个厚度（不分内外环），所以整盘按接合区判；"
      + "管保温沿全长等厚，端部段总厚 = 管保温 + 端部额外保温。";

    /// <summary>
    /// **管端接合区的保温总厚** mm = 管保温 + 端部额外保温（端部段长度为 0 时没有那一段，只算管保温）。
    /// 与 <c>DesignSpec.BuildCase</c> 写进 <c>Layer1</c> 的是同一份值（<c>Layer1.ThicknessMm = TubeInsulMm</c>），
    /// 不另读 DesignSpec —— 判的是**算例里真的在用的那个数**。
    /// </summary>
    public static double TubeJointTotalMm(DesignInputs p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        double baseMm = p.Layer1.Enabled ? p.Layer1.ThicknessMm : 0.0;
        double extraMm = p.EndInsulLengthMm > 1e-9 ? p.EndInsulExtraMm : 0.0;
        return baseMm + extraMm;
    }

    /// <summary>逐项列出接合区上所有要缠的保温厚度（判据、界面、报告共用这一份清单）。</summary>
    public static (string Where, double Mm)[] JointItems(LineCase c)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        var items = new List<(string, double)>();
        string[] pn = { "入口", "共用1", "共用2", "出口" };
        int n = c.FlangePlates.Length > 0 ? c.FlangePlates.Length : Math.Max(0, c.FlangeCount);
        for (int j = 0; j < n; j++)
            items.Add(((j < pn.Length ? pn[j] : $"片{j + 1}") + " 圆盘保温", c.DiscInsulEffectiveAt(j)));
        items.Add(("管端段（管保温 + 端部额外保温）", TubeJointTotalMm(c.Base)));
        return items.ToArray();
    }

    /// <summary>
    /// 判据本体：接合区最厚的那一处 ≤ <see cref="JointZoneMaxMm"/>。
    /// 闭式、纯输入（不吃场）⇒ 网格与工况都不影响它，两态逐位相同。
    /// **任何一项算不出来 ⇒ 整条判不了**（判不了不算过 —— 与 ⑤⑥ 同一条规矩）。
    /// </summary>
    public static ConstraintOut Judge(LineCase c)
    {
        if (c is null) throw new ArgumentNullException(nameof(c));
        var items = JointItems(c);
        var blind = items.Where(t => double.IsNaN(t.Mm)).Select(t => t.Where).ToArray();
        string head = $"限值 {JointZoneMaxMm:0.#} mm = {MaxTurnsAtJoint} 圈 × 每圈 {InsulationSearch.LayerMm:0.#} mm。{SourceNote}　{ZoneNote}　";

        if (items.Length == 0 || blind.Length > 0)
            return new ConstraintOut
            {
                Name = LineResult.Key.WrapTurns, Unit = "mm", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = JointZoneMaxMm, LessIsBetter = true,
                Ok = false, Undetermined = true,
                Where = blind.Length > 0 ? string.Join("、", blind) : "—",
                Note = head + (items.Length == 0
                    ? "★ **无法判定**：这个算例一片法兰都没有，接合区无从谈起。"
                    : $"★ **无法判定**：{blind.Length} 处（{string.Join("、", blind)}）的保温厚度算不出来。"
                      + "**任何一处判不了，整条就判不了** —— 这条挡的是「现场缠不缠得出来」，凭初值报「过」等于没检查。"
                      + "　【下一步】回 ① 页确认这片法兰的圆盘保温厚度有值（不包就填 0），或改用解析几何路径。")
            };

        var worst = items.OrderByDescending(t => t.Mm).First();
        bool ok = worst.Mm <= JointZoneMaxMm + 1e-9;
        return new ConstraintOut
        {
            Name = LineResult.Key.WrapTurns, Unit = "mm", Kind = CheckKind.HardSafety,
            Actual = worst.Mm, Limit = JointZoneMaxMm, LessIsBetter = true,
            Ok = ok, Where = worst.Where,
            Note = head
                 + $"接合区各处：{string.Join("；", items.Select(t => $"{t.Where} {t.Mm:0.###} mm（{InsulationSearch.LayersText(t.Mm)}）"))}。"
                 + (ok
                    ? "缠得出来。"
                    : $"★★ **缠不出来**：{worst.Where} 要 {worst.Mm:0.###} mm = {worst.Mm / InsulationSearch.LayerMm:0.#} 圈，"
                      + $"超过现场能缠的 {MaxTurnsAtJoint} 圈。再往上会渐成球形 —— 那个形状**本程序没有模型**"
                      + "（散热面积与形状因子全变），所以这里**不外推、不硬算**，直接判不可行。"
                      + "　【下一步】① 把这一处的保温减到 " + $"{JointZoneMaxMm:0.#} mm" + " 以内，热学上的缺口改由别的旋钮补"
                      + "（板厚、环台阶、舌保温 —— 舌板与管身不受本限）；② 或者放大圆盘，让接合区之外有地方可以缠厚"
                      + "（⚠ 现模型圆盘只有一个厚度、分不了内外环，放大盘径本身不会解开这条限）。")
        };
    }
}
