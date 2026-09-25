using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ 参数表「铂材牌号」下拉里**每一行长什么样**（2026-09-18，Opus 5）。
///
/// 用户 2026-09-16 原话：「数据不全(电阻/热膨胀/蠕变应力)的铂金合金先以灰色不可选展示，
/// 只用数据全的铂金合金」；2026-09-17/18 又定可选名单并补了第四类（热导率／比热）。
///
/// ══ 为什么要单独有这一份
///
/// <see cref="MaterialDb.DataCompleteness"/> 只回答「这个牌号的四类数据齐不齐」，
/// 它**不回答**「下拉里要不要列它、灰不灰、旁边写什么」。那三件事此前一处都没有 ——
/// HANDOVER 的物性接线表把它记成「未接：全仓没有调用方；界面牌号下拉列的是材料库全部牌号」。
/// 也就是说：齐全度算得出来，而工程师在界面上**点得到**一个数据不全的牌号。
/// 本项目栽过的那一族（「算得出、点不到」／「赋了值≠用它的人读得到」）就是这个形状。
///
/// ⇒ 这一份是**唯一的一处**：下拉（<see cref="GradeNameConverter"/>）、下拉的画法
///   （<c>PtOptimize.UI.GradeNameEditor</c>）、门都读它，谁都不许另抄一份名单。
///
/// ⚠ **不写死可选名单**：可选 = <see cref="GradeDataCompleteness.IsComplete"/>。
///   写死一串名字等于把「查齐全度」这件事架空 —— 数据补齐了名单不会自己长，
///   数据被撤了名单也不会自己缩。门（<c>R48MGradeDropdownTests</c>）两头都钉：
///   ① 可选集合 == 齐全集合（改回「一律可选」当场红）；
///   ② 齐全集合 == 用户 2026-09-17/18 点名的那四个（数据被谁悄悄改动也当场红）。
/// </summary>
public sealed record GradeChoice(
    string Name,
    bool Selectable,
    string[] Missing,
    string[] Borrowed)
{
    /// <summary>
    /// 缺项的**短名**（「热膨胀：工作簿没有本牌号」→「热膨胀」）。
    /// 一行放不下整句 —— 放不下就会被裁掉，而被裁掉的半句话比不写更坏（本项目 R35 的教训：
    /// 参数表标签被切成「最小可制造壁厚 [mm」，**单位恰恰是最不能猜的部分**）。
    /// ⇒ 行上只写缺哪几类，整句在下拉底部那一行与悬停提示里给全（都是 <see cref="Missing"/> 原文，不改写）。
    /// </summary>
    public string[] MissingShort => Missing.Select(m => m.Split('：')[0]).ToArray();

    /// <summary>下拉里这一行要显示的字。不可选的行末尾写缺哪几类（全名，不出代号）。</summary>
    public string RowText => Selectable
        ? Name
        : $"{Name}　—— 不可选：缺 {string.Join("、", MissingShort)}";

    /// <summary>悬停提示：可选的写借了谁，不可选的逐项点名缺什么。</summary>
    public string Tip
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Selectable
                ? $"「{Name}」四类数据齐全（电阻率、热膨胀、持久强度、热导率与比热），可选。"
                : $"「{Name}」数据不全，**不能选**。缺：");
            if (!Selectable) parts.AddRange(Missing.Select(m => "　· " + m));
            if (Borrowed.Length > 0)
            {
                parts.Add("同名义成分借用（算作本牌号自己的数据，但出处照实点名）：");
                parts.AddRange(Borrowed.Select(b => "　· " + b));
            }
            return string.Join(Environment.NewLine, parts);
        }
    }
}

/// <summary>
/// 牌号下拉的**唯一数据源**（2026-09-18，Opus 5）。见 <see cref="GradeChoice"/> 的说明。
/// </summary>
public static class GradeChoices
{
    /// <summary>
    /// 材料库**全集**，按名字排序，逐个带上「能不能选、缺什么、借了谁」。
    /// 列全集不列子集：一个看不见的牌号回答不了「为什么我要的那个不在里面」。
    /// </summary>
    public static GradeChoice[] All()
        => MaterialDb.All.Keys
           .OrderBy(x => x, StringComparer.Ordinal)
           .Select(n =>
           {
               var dc = MaterialDb.DataCompleteness(n);
               return new GradeChoice(n, dc.IsComplete, dc.Missing, dc.Borrowed);
           })
           .ToArray();

    /// <summary>可选（四类数据齐全）的牌号名。下拉的 StandardValues 与合法性校验都读它。</summary>
    public static string[] Selectable()
        => All().Where(c => c.Selectable).Select(c => c.Name).ToArray();

    /// <summary>这个名字现在能不能选（下拉之外的路径，例如打字与方案档回填，用它拦）。</summary>
    public static bool IsSelectable(string? name)
        => name is not null && MaterialDb.All.ContainsKey(name) && MaterialDb.DataCompleteness(name).IsComplete;

    /// <summary>默认牌号：**纯铂**（用户 2026-09-15「默认纯铂，工程师在 APP 特别设定才换」）。</summary>
    public const string DefaultGrade = "Pt";

    /// <summary>下拉顶上那句话（界面与门共用一份写法，不许各写各的）。</summary>
    public const string Legend =
        "灰色 = 该牌号的四类数据（电阻率／热膨胀／持久强度／热导率与比热）不全，不能选；缺哪几类写在行末。";
}
