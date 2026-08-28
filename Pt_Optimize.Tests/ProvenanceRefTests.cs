using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 设计记录的出处，点名的东西必须**真的存在**（2026-08-25）。
///
/// 真事：W08/W06 的出处写着「--shape + D8 定尺寸（Core/Sizer.cs），2026-08-17」，
/// 而这三样东西全是 2026-08-20 的提交 09c8d9b 才诞生的 —— 那次提交的标题是
/// 「输出框改用 Excel 式对齐」，正文没提设计记录被整个换掉（3106 g → 3547 g）。
/// 反倒是两个**作废档**的出处「--final2 可行性阶梯 D7，2026-08-16」与仓库完全对得上。
///
/// 日期没法在代码里验（要读 git 历史，太脆）；但「点名的文件在不在、开关认不认得」
/// 一秒就能验 —— 先把能验的钉死。这是这个项目一贯的做法：
/// 能自动验的绝不留给人记。
/// </summary>
public class ProvenanceRefTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d is not null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "HANDOVER.md"))) return d.FullName;
        throw new DirectoryNotFoundException("往上找 8 层都没有仓根 —— 断言失去了对象");
    }

    private static bool FileExists(string root, string rel)
    {
        string r = rel.Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(Path.Combine(root, r))
            || File.Exists(Path.Combine(root, "Pt_Optimize", r));
    }

    /// <summary>每个设计记录都得有出处 —— 没有出处的设计记录不叫设计记录。</summary>
    [Fact]
    public void EveryArchiveHasProvenance()
    {
        Assert.NotEmpty(DesignSpec.Builtin);
        foreach (var d in DesignSpec.Builtin)
            Assert.False(string.IsNullOrWhiteSpace(d.Provenance), d.Name + " 没有出处");
    }

    /// <summary>出处/失效告示里点名的 *.cs 必须在仓库里找得到。</summary>
    [Fact]
    public void NamedSourceFilesExist()
    {
        string root = RepoRoot();
        foreach (var d in DesignSpec.Builtin)
        foreach (string text in new[] { d.Provenance, d.Invalid, d.Binding })
        {
            var (files, _) = ProvenanceRef.Referenced(text);
            foreach (string f in files)
                Assert.True(FileExists(root, f),
                    d.Name + " 点名了不存在的文件：" + f);
        }
    }

    /// <summary>出处/失效告示里点名的 --开关，Program.cs 必须真的认得。</summary>
    [Fact]
    public void NamedCliFlagsAreHandled()
    {
        string root = RepoRoot();
        string prog = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Program.cs"));
        foreach (var d in DesignSpec.Builtin)
        foreach (string text in new[] { d.Provenance, d.Invalid, d.Binding })
        {
            var (_, flags) = ProvenanceRef.Referenced(text);
            foreach (string g in flags)
                Assert.True(prog.Contains((char)34 + g + (char)34, StringComparison.Ordinal),
                    d.Name + " 点名了 Program.cs 不认得的开关：" + g);
        }
    }

    // ── 自证：上面三条不许恒真 ──────────────────────────────────

    /// <summary>
    /// ★ 检查器得真的挑得出东西来 —— 否则前面几条是在空集上「全过」，
    ///   而「空集恒真」是本项目记过案的安静失败之一。
    /// </summary>
    [Fact]
    public void Checker_ActuallyFindsReferences()
    {
        var (files, flags) = ProvenanceRef.Referenced(
            "用 --没这个开关 跑的（Core/根本不存在.cs），另见 Core/Sizer.cs 与 --shape");
        Assert.Contains("Core/Sizer.cs", files);
        Assert.Contains("--shape", flags);
        Assert.Contains(flags, g => g.Contains("--", StringComparison.Ordinal));
    }

    /// <summary>伪造一条点名不存在文件的出处 ⇒ 检查必须当场红。</summary>
    [Fact]
    public void FabricatedProvenance_IsRejected()
    {
        string root = RepoRoot();
        var (files, _) = ProvenanceRef.Referenced("由 Core/ThisFileDoesNotExist.cs 产生");
        Assert.Single(files);
        Assert.False(FileExists(root, files[0]));   // 门确实拦得住
    }

    /// <summary>现役档里真的有 .cs 与 --开关可挑 —— 否则 NamedXxx 两条在空集上恒真。</summary>
    [Fact]
    public void LiveArchivesReallyNameThings()
    {
        var (f8, g8) = ProvenanceRef.Referenced(DesignSpec.W08.Provenance);
        Assert.NotEmpty(f8);
        Assert.NotEmpty(g8);
    }

    /// <summary>
    /// ★ 2026-08-25 **本条被改写过一次，改写的理由本身就是教训**：
    ///
    /// 它原本断的是「日期 2026-08-17 已被证伪，只许出现在更正语境里」。
    /// 当天稍后用二进制实证推翻了那个结论 —— deliverable/设计记录_管壁0.8mm.3dm
    /// 生成于 2026-08-17 13:45，里面舌长 −140.0 出现 64 次、本档板厚各 114 次，
    /// 作废档的特征值一次都没有 ⇒ **本档的几何那天确实已经存在，日期是对的**。
    /// 错的只是**工具归属**（--shape / Core/Sizer.cs / D8 都是 08-20 才写的）。
    ///
    /// ⇒ 断言若把一个**结论**写死，结论一翻案，断言自己就变成假的。
    ///   所以现在钉的是一条不随结论改变的**不变量**：
    ///   **出处点名了产生它的工具，就必须同时说清这条出处能不能拿来复现。**
    ///   这一条无论日期对错都成立，也正是「出处」这件事的全部意义。
    /// </summary>
    [Fact]
    public void 出处点名了工具就必须写明能不能复现()
    {
        foreach (var d in new[] { DesignSpec.W08, DesignSpec.W06 })
        {
            bool namesTool = d.Provenance.Contains("--shape", StringComparison.Ordinal)
                          || d.Provenance.Contains("Sizer.cs", StringComparison.Ordinal);
            if (!namesTool) continue;
            Assert.True(d.Provenance.Contains("不能拿来复现", StringComparison.Ordinal),
                d.Name + " 的出处点名了工具，却没说清能不能拿它复现 —— " +
                "而今天的 --shape 种子默认就是该档本身，用它重推等于从答案出发");
        }
    }

    /// <summary>
    /// 工具归属的时间错位必须**留在档里** —— 有人重写出处时，这条拦下「悄悄改回去」。
    /// ⚠ 只钉两个**事实**（提交号与它的日期），不钉措辞：措辞会改，事实不会。
    /// </summary>
    [Fact]
    public void 时间错位这个事实必须留在现役档里()
    {
        Assert.Contains("09c8d9b", DesignSpec.W08.Provenance);
        Assert.Contains("2026-08-20", DesignSpec.W08.Provenance);
    }

    /// <summary>
    /// 「不能复现」不是推断而是**实测**：中性种子在同一形状上落在 3664 g。
    /// 这个数留在档里 —— 下次有人想重推之前，先知道会偏多少。
    /// </summary>
    [Fact]
    public void 复现偏差是实测值_留在档里()
    {
        Assert.Contains("3664", DesignSpec.W08.Provenance);
    }
}

