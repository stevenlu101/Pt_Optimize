using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **工程师看得见的字写在哪几个档里** —— 全仓唯一一份清单（2026-09-18，Opus 5）。
///
/// ══ 为什么要有这一份
///
/// 「界面上不许出现判据代号」那道门（<c>NoCriterionCodeInUiTests</c>）原来在门里**手抄**
/// 六个 <c>UI/*.cs</c> 的档名。手抄的清单守不住手抄的病：
/// <code>
///   2026-09-18 复核查出：工程师看得见的字**不止写在 UI 那六个档里** ——
///     Core/FinalCheckReport.cs（三关结论块，输出框与安装报告都印它）
///     Core/InstallReport.cs   （安装报告正文，导出成 .md 交给现场）
///     Core/InsulationPlan.cs  （保温方案表，③ 页 + 输出框 + 安装报告）
///     Core/FlangeKit.cs       （配套清单，③ 页 + 输出框 + 安装报告）
///     Core/GradeChoices.cs    （牌号下拉每一行的字与下拉顶上那句话）
///     UI/GradeNameEditor.cs   （下拉的画法；2026-09-18 新加的档，六个档名里根本没有它）
///   ⇒ 这几份里写出代号，那道门一个字都看不见。
/// </code>
///
/// ⇒ 清单提成**生产侧的公开常量**：门去读它，不再自己抄一份。
///   新加一个界面档（如 2026-09-18 的 <c>UI/GradeNameEditor.cs</c>）会被 <see cref="UiDir"/>
///   那一层**自动**扫到 —— 那正是上一次漏掉的那种形态；Core 侧新写一份给工程师看的正文，
///   就得在 <see cref="CoreSources"/> 里点名（门另有一条核对这几份档确实还在，改名当场红）。
///
/// ══ 谁**不在**名单里（写清楚，免得下一个人以为是漏了）
///
/// <code>
///   Core/Criteria.cs   —— 命令行那张对照表（--glossary）**照旧带代号**，读者是开发者；
///                          界面那张（Criteria.Html）由门另外逐条核过名与单位。
///   Program.cs         —— 命令行输出，读者是开发者。
///   HANDOVER.md        —— 开发文档，代号到处都是。
///   Core 其余各档      —— 里面的字要么进不了界面，要么经上面这几份转印。
/// </code>
///
/// 2026-09-18，Opus 5
/// </summary>
public static class VisibleText
{
    /// <summary>
    /// 界面档所在的目录（相对仓库根，用 <c>/</c> 分隔）。
    /// **这一层不点名**：目录下每一个 <c>.cs</c> 都算 —— 加一个新页面不必记得回来改清单
    /// （「记得改」这件事迟早不会被做，2026-09-18 的 <c>GradeNameEditor.cs</c> 就是这么漏的）。
    /// </summary>
    public const string UiDir = "Pt_Optimize/UI";

    /// <summary>
    /// <c>Core</c> 里**直接写给工程师看**的那几份正文（报告、清单、方案表、下拉名单）。
    /// 这一层必须点名：Core 里绝大多数档的字进不了界面，整个目录扫进来只会天天误报。
    /// </summary>
    public static readonly IReadOnlyList<string> CoreSources = new[]
    {
        "Pt_Optimize/Core/FinalCheckReport.cs",   // 三关结论块 + 伸长表
        "Pt_Optimize/Core/InstallReport.cs",      // 系统安装报告正文
        "Pt_Optimize/Core/InsulationPlan.cs",     // 保温方案（材质 · 各区厚度）
        "Pt_Optimize/Core/FlangeKit.cs",          // 配套清单（铜排／保温／焊接）
        "Pt_Optimize/Core/GradeChoices.cs",       // 牌号下拉每一行的字
    };

    /// <summary>
    /// 全部可见文本来源（相对仓库根，已按档名排序）：<see cref="UiDir"/> 下的每一个 <c>.cs</c>
    /// ＋ <see cref="CoreSources"/> 点名的那几份。
    /// </summary>
    /// <param name="repoRoot">仓库根（含 <c>HANDOVER.md</c> 的那一层）。</param>
    /// <exception cref="DirectoryNotFoundException">
    /// <see cref="UiDir"/> 找不到时**直接炸**：读的人拿到一份少了半边的清单，
    /// 会以为「扫过了、没问题」—— 空集当通过是本项目反复栽的那一跤。
    /// </exception>
    public static IReadOnlyList<string> Sources(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) throw new ArgumentNullException(nameof(repoRoot));

        string ui = Path.Combine(repoRoot, UiDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(ui))
            throw new DirectoryNotFoundException(
                $"可见文本来源清单：界面目录「{UiDir}」在「{repoRoot}」下不存在 —— "
                + "清单少了半边，凡是靠它扫的检查都会变成空转");

        var list = Directory.GetFiles(ui, "*.cs", SearchOption.TopDirectoryOnly)
                            .Select(f => UiDir + "/" + Path.GetFileName(f))
                            .Concat(CoreSources)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();

        if (list.Length == 0)
            throw new InvalidOperationException("可见文本来源清单是空的 —— 空集不算「扫过了」");
        return list;
    }
}
