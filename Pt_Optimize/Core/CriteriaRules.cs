using System;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ 决 103（业主 2026-09-24 定，「按今天的为准，改完方案再开跑」；HANDOVER §0.-21 决 103 条）：**判据口径的总开关**。
///
/// 缺省 = <see cref="决103"/>（生产不传就是它）；<see cref="决103前"/> = 2026-09-14 起的口径（R48 B：热偶读数基准的热侧／冷侧 ± 5 K、管孔净流入须为正、
/// 两条热稳定只作参考、管 J 许用 12），**只供门与「开 − 关」归因**：改回时整线判据表、求解器分派、升温全程判定与改前逐位相同
/// （门 R48CriteriaSwapGateTests.门a_改回逐位）。放在参数表对象上是因为整线判据（LineRunner.Judge 读 <c>LineCase.Base</c>）、
/// 求解器（Solver 读 baseIn）与升温全程都只看得到它，且参数表复制（Clone 走 JSON）会带着它 —— 与段电流连续根那个改回参数同一做法。
/// </summary>
public enum CriteriaRuleSet
{
    /// <summary>决 103（2026-09-24）：稳态期带玻璃第 j 片「法兰最热处高出管接触处温度 ≤ 10 K」与「管接触处流入法兰的净热流 ≤ 0」为硬判据；
    /// 局部与整片热稳定升为硬判据（≥ 1.0，局部热稳定全格精算）；管 J ≤ 11；最热铂高出热偶读数、管根低于热偶读数、管孔净流入须为正三条降为参考。</summary>
    决103 = 0,
    /// <summary>2026-09-14 口径（决 103 前）：只供门与归因（改回逐位等于改前）。</summary>
    决103前 = 1,
}

/// <summary>
/// 决 103 新判据的常数与说明文字（全仓唯一一份；限值本身仍从参数表 → <see cref="LineCase"/> 读，这里只放默认值与出处）。
/// </summary>
public static class CriteriaRules
{
    /// <summary>
    /// 「法兰最热处高出管接触处温度」的默认限值 K。
    /// 出处：业主 2026-09-24（决 103）原话「稳态期法兰温度场比铂金管接触处温度高的方向是对的、可容许到 10 °C 以内，拉低管温的方向是错的，之前的 ±5 作废」；
    /// 与业主 2026-08-25 口述链「法兰可高于管 10 °C 以内」（HANDOVER 登记）一致。不是看过数才定的数。
    /// </summary>
    public const double HotOverContactMaxKDefault = 10.0;

    /// <summary>
    /// 管 J 的使用上限默认值 A/mm²。出处：业主 2026-09-24（决 103）「以管的最大使用电流密度 J &lt; 11 与 20 °C/h（可控硅）为限」。
    /// 决 103 起卡交付的管 J 限值 = min(参数表「管许用电流密度」（08-15 现场，全档 12）, 本上限) —— 两条上限同时成立 ⇒ 取小；
    /// 原 12 那一行留作对照照印（「· 管电流密度对原许用值（对照）」）。比较符沿用原「≤」（业主原话「&lt; 11」，恰等于 11 的情形测度为零，写明不另立）。
    /// </summary>
    public const double TubeJUseCapDefault = 11.0;

    /// <summary>「管接触处流入法兰的净热流」的限值 W：0（不给预算）。出处：业主 2026-09-24（决 103）「拉低管温的方向是错的」⇒ 法兰不得拉低管在接触处的温度 ⇔ 管孔处净热流不得由管流入法兰。</summary>
    public const double TubeToFlangeHeatMaxW = 0.0;

    /// <summary>判据表里由决 103 降为参考的三条（最热铂高出热偶读数、管根低于热偶读数、管孔净流入须为正）说明最前面加的那句（进界面，不带代号）。</summary>
    public const string DowngradeNote =
        "业主 2026-09-24 决定（稳态判据换向）：本条降为参考、照印不判；带玻璃稳态卡交付的热侧、冷侧换成「法兰最热处高出管接触处温度」「管接触处流入法兰的净热流」。";

    /// <summary>判据表里由决 103 升为带玻璃稳态硬判据的两条热稳定说明最前面加的那句。</summary>
    public const string UpgradeNote =
        "业主 2026-09-24 决定（稳态判据换向）：允许法兰略热于管之后，原来「管孔净流入永远先红」的隐性保护消失 ⇒ 本条由参考升为带玻璃稳态硬判据（须 ≥ 1.0）。";

    /// <summary>共用片的接触温度取法（进判据说明）：与旧判法「圆盘区最高温 − 管温」同一基准。</summary>
    public const string ContactBasisNote =
        "接触处温度 = 模型算出的该片管根接触温度（与旧判法「圆盘区最高温 − 管温」同一基准，即该片热解管孔定温边界上的温度）："
        + "端片取相邻那一段的端温，两段共用的那一片取两侧段端温度的较高者（热解按它定管孔边界）；热偶设定值只印对照。";

    /// <summary>这一口径下「管 J」卡交付用的限值（唯一读法：参数表 → <see cref="DesignInputs.TubeJLimitAPerMm2"/>）。</summary>
    public static double TubeJLimitOf(DesignInputs p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        return p.CriteriaRuleSet == CriteriaRuleSet.决103前
            ? p.TubeJAllowAPerMm2
            : Math.Min(p.TubeJAllowAPerMm2, p.TubeJUseCapAPerMm2);
    }
}
