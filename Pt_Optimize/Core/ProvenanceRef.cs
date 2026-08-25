using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PtOptimize.Core;

/// <summary>
/// 定案档 <see cref="FinalDesign.Provenance"/> 里点名的东西，必须**真的存在**。
///
/// ★ 为什么要有这道门（2026-08-25 查出的真事）：
///   W08/W06 的出处写着「`--shape` + D8 定尺寸（Core/Sizer.cs），**2026-08-17**」，
///   而 `--shape`、`Core/Sizer.cs`、控制律代号 `D8` 三样东西**都是 2026-08-20**
///   在同一次提交（09c8d9b）里才诞生的 —— 那次提交的标题是
///   「输出框改用 Excel 式对齐」，正文一个字没提定案被换掉。
///   于是「出处」这件本该让人能复现的事，指向了一个当时不存在的工具。
///
///   日期对不对，代码里验不了（要读 git 历史，那太脆）。
///   但**点名的文件在不在、开关认不认得**，一秒就能验 —— 先把能验的钉死。
///
/// ⚠ 这道门只挡「点了不存在的东西」，挡不住「点了存在但不是它算的」。
///   后者只能靠人；所以出处里还要写清**能不能拿它复现**（见 W08/W06 的现行文字）。
/// </summary>
public static class ProvenanceRef
{
    // 刻意不用反斜杠转义（本仓库的写入链路会改写转义序列，历史上多次弄坏字面量）：
    // 用 [.] 代替 \. ，用字符类代替 \w。
    private static readonly Regex FileRx = new("[A-Za-z0-9_/]+[.]cs", RegexOptions.Compiled);
    private static readonly Regex FlagRx = new("--[a-z0-9][a-z0-9-]*", RegexOptions.Compiled);

    /// <summary>挑出出处文字里点名的代码文件（*.cs）与命令行开关（--xxx）。</summary>
    public static (string[] Files, string[] Flags) Referenced(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (new string[0], new string[0]);
        var files = FileRx.Matches(text!).Select(m => m.Value).Distinct().ToArray();
        var flags = FlagRx.Matches(text!).Select(m => m.Value).Distinct().ToArray();
        return (files, flags);
    }
}
