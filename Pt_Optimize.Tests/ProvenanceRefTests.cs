using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 定案档的出处，点名的东西必须**真的存在**（2026-08-25）。
///
/// 真事：W08/W06 的出处写着「--shape + D8 定尺寸（Core/Sizer.cs），2026-08-17」，
/// 而这三样东西全是 2026-08-20 的提交 09c8d9b 才诞生的 —— 那次提交的标题是
/// 「输出框改用 Excel 式对齐」，正文没提定案被整个换掉（3106 g → 3547 g）。
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

    /// <summary>每个定案都得有出处 —— 没有出处的定案不叫定案。</summary>
    [Fact]
    public void EveryArchiveHasProvenance()
    {
        Assert.NotEmpty(FinalDesign.Builtin);
        foreach (var d in FinalDesign.Builtin)
            Assert.False(string.IsNullOrWhiteSpace(d.Provenance), d.Name + " 没有出处");
    }

    /// <summary>出处/失效告示里点名的 *.cs 必须在仓库里找得到。</summary>
    [Fact]
    public void NamedSourceFilesExist()
    {
        string root = RepoRoot();
        foreach (var d in FinalDesign.Builtin)
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
        foreach (var d in FinalDesign.Builtin)
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
        var (f8, g8) = ProvenanceRef.Referenced(FinalDesign.W08.Provenance);
        Assert.NotEmpty(f8);
        Assert.NotEmpty(g8);
    }

    /// <summary>
    /// 已证伪的日期「2026-08-17」**只许出现在更正语境里**，不许再当作归属。
    ///
    /// ⚠ 这条规则是这样写的，而不是「一律不许出现」：更正本身就得引用那个日期，
    ///   否则读的人不知道改的是什么。也不是去匹配日期前后的标点 ——
    ///   那是「拿行号当引用」的变体，本项目明令禁止。
    /// </summary>
    [Fact]
    public void TheDisprovenDateOnlyAppearsAsACorrection()
    {
        foreach (var d in FinalDesign.Builtin)
        foreach (string text in new[] { d.Provenance, d.Invalid, d.Binding })
        {
            if (!text.Contains("2026-08-17", StringComparison.Ordinal)) continue;
            Assert.True(text.Contains("更正", StringComparison.Ordinal)
                     || text.Contains("对不上", StringComparison.Ordinal),
                d.Name + " 仍把 2026-08-17 当作归属在用");
        }
    }

    /// <summary>更正必须留在档里 —— 有人重写出处时，这条会拦下「悄悄改回去」。</summary>
    [Fact]
    public void TheCorrectionIsRecordedInTheLiveArchive()
    {
        Assert.Contains("2026-08-25 更正", FinalDesign.W08.Provenance);
        Assert.Contains("09c8d9b", FinalDesign.W08.Provenance);
    }
}
