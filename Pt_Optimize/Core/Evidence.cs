using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★★ R48（2026-09-15，Opus 5；常驻数值把关人第十三、十四轮）：**证据文件头** —— 每份被引作依据的探针输出，开头写明它是哪份代码、哪套配方跑出来的。
///
/// 为什么要它：deliverable 里的证据文件被生产代码注释引作依据（例如压接细带自相似那份），而探针重跑时用**同名文件整份覆盖**。
/// 生产默认一改（整面接触、散热表上限……），重跑出来的是另一套口径的数，却顶着原来的文件名 —— 被引的那份证据悄悄换了内容，引用它的注释还在原地说「依据是它」。
/// 文件头把「哪份代码」（git HEAD + Pt_Optimize/Core 相对 HEAD 的改动指纹）与「哪套配方」（网格配方、热解配方、耦合容差、共用片抽热、工况）写死在文件里，
/// <see cref="EvidenceFile"/> 据此决定能不能覆盖。
///
/// 头的格式（逐行，行首 #，首尾两行是标记）：
/// <code>
/// #证据头 v1
/// # 标题：…
/// # git HEAD：…
/// # Core 改动指纹：…
/// # 探针源码指纹：…（调用方 .cs 的相对路径 + SHA1）
/// # 网格配方：…（可多行）
/// # 热解配方：…（可多行）
/// # 耦合容差：…
/// # 共用片抽热：…
/// # 工况：…
/// #证据头结束
/// </code>
/// 头里不写时间（时间写进正文）：同一份代码、同一套配方重跑，头逐字相同，允许覆盖。
/// </summary>
public static class EvidenceHeader
{
    public const string BeginMark = "#证据头 v1";
    public const string EndMark = "#证据头结束";

    /// <summary>从 <paramref name="start"/>（缺省 = 程序所在目录）往上找含 Pt_Optimize.sln 的目录；找不到返回 null。</summary>
    public static string? RepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>跑一条 git 命令，返回标准输出的原始字节；失败返回 null 并给出原因。</summary>
    private static byte[]? Git(string root, string args, out string why)
    {
        why = "";
        try
        {
            // 2026-09-15 Opus 5（审查意见 minor）：全局参数 -c core.quotepath=off 让未跟踪文件的中文路径原样出来；diff 另加 --no-ext-diff --no-textconv --no-color（见 CoreDiffSha1）
            var psi = new ProcessStartInfo("git", "-c core.quotepath=off " + args)
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var pr = Process.Start(psi);
            if (pr is null) { why = "git 起不来"; return null; }
            using var ms = new MemoryStream();
            var copy = pr.StandardOutput.BaseStream.CopyToAsync(ms);
            string err = pr.StandardError.ReadToEnd();
            if (!pr.WaitForExit(60_000)) { try { pr.Kill(); } catch { } why = "git 超过 60 s 没返回"; return null; }
            copy.Wait(60_000);
            if (pr.ExitCode != 0) { why = $"git {args} 退出码 {pr.ExitCode}：{err.Trim()}"; return null; }
            return ms.ToArray();
        }
        catch (Exception ex) { why = "git 不可用：" + ex.Message; return null; }
    }

    /// <summary>git HEAD 的提交号；取不到返回「未知（原因）」。</summary>
    public static string GitHead(string root)
    {
        var b = Git(root, "rev-parse HEAD", out string why);
        return b is null ? $"未知（{why}）" : Encoding.UTF8.GetString(b).Trim();
    }

    /// <summary>
    /// Pt_Optimize/Core 相对 HEAD 的改动指纹：SHA1(<c>git diff HEAD --binary -- Pt_Optimize/Core</c> 的输出 + 该目录下未跟踪文件的「路径 + 内容」)。
    /// 用 <c>diff HEAD</c> 而不是工单原文的 <c>git diff</c>：后者只比「暂存区 vs 工作树」，已暂存的改动（并行开发的工作树里全部改动都已暂存）会漏掉，
    /// 两份不同的代码会得到同一个指纹。未跟踪的新文件 git diff 也看不见，另外并进来。没有任何改动时就是空串的 SHA1。
    /// </summary>
    public static string CoreDiffSha1(string root)
    {
        // 2026-09-15 Opus 5（审查意见 minor）：加 --no-ext-diff --no-textconv --no-color —— 用户配置了外部 diff、textconv 或强制彩色输出时，指纹照样只由内容定
        var diff = Git(root, "diff HEAD --binary --no-ext-diff --no-textconv --no-color -- Pt_Optimize/Core", out string why);
        if (diff is null) return $"未知（{why}）";
        var untracked = Git(root, "ls-files --others --exclude-standard -- Pt_Optimize/Core", out why);
        if (untracked is null) return $"未知（{why}）";
        using var sha = SHA1.Create();
        using var ms = new MemoryStream();
        ms.Write(diff);
        foreach (var rel in Encoding.UTF8.GetString(untracked).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.Ordinal))
        {
            ms.Write(Encoding.UTF8.GetBytes("\n未跟踪：" + rel + "\n"));
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) ms.Write(File.ReadAllBytes(full));
        }
        return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（审查意见 minor：证据头只给 Core 做指纹）：**探针自己的源码指纹** = 「相对仓库根的路径 + SHA1(文件内容，行尾统一成 \n)」。
    /// 为什么：并行工作树里改动都没提交、HEAD 不动；只改探针自己的输入（保温值、网格档、显式开关）不改 Core 就重跑，原先的头逐字相同，
    /// 会整份覆盖被 Core 注释引作依据的同名证据 —— 正是数值把关人要挡的「被引证据悄悄换内容」。
    /// 内容哈希而不是 git diff：不管提没提交、暂没暂存都一样；行尾统一：autocrlf 不同的工作树里同一份源码得同一个指纹。
    /// 路径写相对仓库根（不在仓库里就只写文件名）：同一份探针在不同工作树里重跑，头不因绝对路径不同而不同。
    /// ⚠ 只覆盖调用方这一个 .cs：探针若从别的测试文件取输入（辅助类），那份文件的改动不在指纹里 —— 工单点名的五个探针都没有这种跨文件输入（2026-09-15 查过）。
    /// </summary>
    public static string ProbeSourceFingerprint(string callerFile, string? root)
    {
        if (string.IsNullOrEmpty(callerFile)) return "未知（没拿到调用方源文件路径）";
        string? path = File.Exists(callerFile) ? callerFile : null;
        // 编译机路径与运行机不同时（例如在别处编好拷过来），按「Pt_Optimize.Tests/…」这一段在仓库根下再找一次
        if (path is null && root is not null)
        {
            string norm = callerFile.Replace('\\', '/');
            int k = norm.LastIndexOf("/Pt_Optimize", StringComparison.Ordinal);
            if (k >= 0)
            {
                string cand = System.IO.Path.Combine(root, norm[(k + 1)..].Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(cand)) path = cand;
            }
        }
        if (path is null) return $"未知（找不到源文件 {System.IO.Path.GetFileName(callerFile)}）";
        string rel = System.IO.Path.GetFileName(path);
        if (root is not null)
        {
            string full = System.IO.Path.GetFullPath(path), r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) rel = full[r.Length..].Replace('\\', '/');
        }
        byte[] bytes = File.ReadAllBytes(path);
        string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        using var sha = SHA1.Create();
        return rel + " " + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// 组一份证据头。<paramref name="coupleTolK"/> NaN = 不适用（单片、管侧固定的探针）；<paramref name="splitSharedDraw"/>／<paramref name="emptyTube"/> null = 不适用。
    /// <paramref name="root"/> 缺省 = <see cref="RepoRoot"/>；找不到仓库时 git 两行写「未知」。
    /// <paramref name="callerFile"/> 由编译器填调用方源文件路径（<see cref="CallerFilePathAttribute"/>），**调用方不要传**；门要造「探针源码不同」的对照时才显式传。
    /// </summary>
    public static string Build(string title, IEnumerable<string> meshRecipes, IEnumerable<string> thermalRecipes,
                               double coupleTolK, bool? splitSharedDraw, bool? emptyTube,
                               IEnumerable<string>? extra = null, string? root = null,
                               [CallerFilePath] string callerFile = "")
    {
        root ??= RepoRoot();
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L(BeginMark);
        L("# 标题：" + OneLine(title));
        L("# git HEAD：" + (root is null ? "未知（找不到仓库根）" : GitHead(root)));
        L("# Core 改动指纹（Pt_Optimize/Core 相对 HEAD 的改动与未跟踪文件，SHA1）：" + (root is null ? "未知（找不到仓库根）" : CoreDiffSha1(root)));
        L("# 探针源码指纹（调用方源文件，SHA1）：" + OneLine(ProbeSourceFingerprint(callerFile, root)));
        foreach (var m in meshRecipes) L("# 网格配方：" + OneLine(m));
        foreach (var t in thermalRecipes) L("# 热解配方：" + OneLine(t));
        L("# 耦合容差：" + (double.IsNaN(coupleTolK) ? "不适用（单片、管侧固定）" : $"{coupleTolK:R} K"));
        L("# 共用片抽热：" + (splitSharedDraw is null ? "不适用" : splitSharedDraw.Value ? "各半（只算一次）" : "双扣（两段各扣一次）"));
        L("# 工况：" + (emptyTube is null ? "不适用" : emptyTube.Value ? "空管到温稳态（无玻璃）" : "带玻璃稳态"));
        foreach (var e in extra ?? Array.Empty<string>()) L("# " + OneLine(e));
        L(EndMark);
        return sb.ToString();
    }

    /// <summary>
    /// 整线探针用：网格与热解配方写**生产配方常量**（LineRunner 建网格、解热场不接受改配方，由 R48RecipeFingerprintTests 的行为门守），
    /// 耦合容差、共用片抽热、工况取自算例。跑完之后逐片实际配方另由探针印进正文（LineResult.RecipeSummary／RecipeDeviations）。
    /// </summary>
    public static string ForLineCase(string title, LineCase lc, IEnumerable<string>? extra = null, string? root = null,
                                     [CallerFilePath] string callerFile = "")
        => Build(title,
                 new[] { "生产配方常量 —— " + FlangeMesher.ProductionMeshRule.Describe() },
                 new[] { "生产配方常量 —— " + ShellThermal.ProductionThermalRule.Describe() },
                 lc.CoupleTolK, lc.Base.SplitSharedFlangeDraw, lc.EmptyTube,
                 // ★ R48 L（2026-09-17，Opus 5）：证据头里那一行「耦合容差」印的是**上限**；真正停在哪个容差上逐轮由判据裕度定
                 //   （LineRunner.CoupleTolKFor，结果里的 LineResult.CoupleTolKUsed）。不补这一行，读的人会把上限当成实际用的那个数。
                 // R48 M（2026-09-18，Fable 5.1）：认证误差口径也进头（门 R48MStopTolGateTests.门_证据头写认证误差口径）
                 // ★ 2026-09-23（SEG，决 97 A）：段电流收尾口径也进头 —— 改回（中点）跑出来的证据与生产（连续根）不是一个口径，头上要看得出来。
                 (extra ?? Array.Empty<string>()).Concat(new[] { "耦合容差口径：" + CoupleTolNote(lc), "认证误差口径：" + CertErrNote(lc), "段电流收尾：" + SegRootNote(lc) }),
                 root, callerFile);   // 调用方路径原样往下传（不传就成了本文件）

    /// <summary>R48 L（2026-09-17，Opus 5）：停机容差口径的一句话 —— 上限、是否按判据裕度收紧、比例与下限。只有这一份写法。</summary>
    /// ★ R48 M（2026-09-18，Fable 5.1）：生产口径改成绝对目标（不随裕度走）；「按判据裕度收紧」只剩注射用，头上要写明它不是生产默认。
    public static string CoupleTolNote(LineCase lc)
        => lc is null ? "不适用"
         : lc.CoupleTolFromMargin
         ? $"上限 {lc.CoupleTolK:R} K，按判据裕度收紧 = min(上限, max(下限 {lc.CoupleTolFloorK:R} K, {lc.CoupleTolMarginFrac:R} × 最小硬安全线温度裕度))（**注射口径**，不是生产默认）；实际用的那一个见结果 CoupleTolKUsed"
         : $"绝对目标 {lc.CoupleTolK:R} K（不随判据裕度走；生产口径）；实际用的那一个见结果 CoupleTolKUsed";

    /// <summary>2026-09-23（SEG，决 97 A）：段电流二分收尾口径的一句话 —— 只有这一份写法。</summary>
    public static string SegRootNote(LineCase lc)
        => lc is null ? "不适用"
         : lc.Base.SegCurrentContinuousRoot
         ? $"连续根（二分到 xTol {lc.Base.SegCurrentTolA:R} A 后，末括号内线性插值；生产口径）"
         : $"括号中点（二分到 xTol {lc.Base.SegCurrentTolA:R} A；**改回口径**，不是生产默认）";

    /// <summary>R48 M（2026-09-18，Fable 5.1）：认证误差口径的一句话 —— 只有这一份写法。</summary>
    public static string CertErrNote(LineCase lc)
        => lc is null ? "不适用"
         : "认证误差 = 放大 × 停机残差（结果 LineResult.CertErrK，只在收敛时有数）；"
         + $"放大 = max(闭式 1+ℓt/Δx × {LineRunner.StopAmpHeadroom:R}, 实测雅可比 ‖(I−J)⁻¹‖∞"
         + (lc.MeasureJacobianAmp ? "" : "（本算例关掉了实测，只用闭式 × 1.1）")
         + (lc.EndTempAmpFromDecayLength ? "" : "（历史口径：写死 25，不乘余量不取实测）")
         + ")；格点上裕度小于它的硬安全线判不了那么细";

    private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

    /// <summary>从文本开头取证据头（含首尾标记，行尾统一成 \n）；没有头返回 null。</summary>
    public static string? Extract(string text)
    {
        var t = (text ?? "").Replace("\r\n", "\n");
        if (t.StartsWith("﻿", StringComparison.Ordinal)) t = t[1..];
        if (!t.StartsWith(BeginMark + "\n", StringComparison.Ordinal)) return null;
        int end = t.IndexOf("\n" + EndMark + "\n", StringComparison.Ordinal);
        if (end < 0)
        {
            // 头在文件末尾、没有正文
            if (t.EndsWith("\n" + EndMark, StringComparison.Ordinal)) return t + "\n";
            return null;
        }
        return t[..(end + EndMark.Length + 2)];
    }
}

/// <summary>
/// ★★ R48（2026-09-15，Opus 5）：写证据文件 —— **同名文件已存在且证据头不同（或没有证据头）就不覆盖**，改写到带时间戳的新文件名，并在结果里说明。
///
/// 用法（探针逐行追加、每行整份重写的那种）：<c>var ev = EvidenceFile.Open(path, header); … ev.Write(正文)</c> ——
/// 目标路径在 <see cref="Open"/> 时定一次，之后每次 <see cref="Write(string)"/> 都写同一个文件（否则第二行又会被判「头不同」再换一个名字）。
/// 一次写完的用 <see cref="Write(string, string, string)"/>。编码 UTF-8 无 BOM，行尾照正文。
/// </summary>
public sealed class EvidenceFile
{
    /// <summary>调用方要写的路径。</summary>
    public string RequestedPath { get; }
    /// <summary>实际写的路径（没改道时 = <see cref="RequestedPath"/>）。</summary>
    public string Path { get; }
    public string Header { get; }
    /// <summary>改道了没有（同名文件已存在且证据头不同或没有证据头）。</summary>
    public bool Redirected { get; }
    /// <summary>为什么这样写（人话；没改道时说明是新文件还是同头覆盖）。</summary>
    public string Reason { get; }

    private EvidenceFile(string requested, string path, string header, bool redirected, string reason)
    { RequestedPath = requested; Path = path; Header = header; Redirected = redirected; Reason = reason; }

    /// <summary>定目标路径（只定一次）。<paramref name="now"/> 缺省 = 当前时间（测试可注入）。</summary>
    public static EvidenceFile Open(string path, string header, DateTime? now = null)
    {
        string h = NormalizeHeader(header);
        if (EvidenceHeader.Extract(h) is null)
            throw new ArgumentException("证据头格式不对：要以「" + EvidenceHeader.BeginMark + "」起、以「" + EvidenceHeader.EndMark + "」止（用 EvidenceHeader.Build 组）", nameof(header));
        if (!File.Exists(path))
            return new EvidenceFile(path, path, h, false, "新文件");
        string? old;
        try { old = EvidenceHeader.Extract(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex) { old = null; _ = ex; }
        if (old is not null && old == h)
            return new EvidenceFile(path, path, h, false, "同名文件已存在且证据头逐字相同（同一份代码、同一套配方）⇒ 覆盖");
        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        string stem = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        string stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        string target = System.IO.Path.Combine(dir, $"{stem}_重跑{stamp}{ext}");
        for (int k = 2; File.Exists(target); k++) target = System.IO.Path.Combine(dir, $"{stem}_重跑{stamp}_{k}{ext}");
        string why = old is null
            ? "同名文件已存在但没有证据头（改动前写的旧证据）"
            : "同名文件已存在且证据头不同（" + DiffLines(old, h) + "）";
        return new EvidenceFile(path, target, h, true,
            $"{why} ⇒ 不覆盖，改写到 {System.IO.Path.GetFileName(target)}");
    }

    /// <summary>把证据头 + 正文整份写到 <see cref="Path"/>。</summary>
    public void Write(string body)
    {
        string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, Header + (body ?? ""), new UTF8Encoding(false));
    }

    /// <summary>一次写完：定路径 + 写。返回的对象里有实际路径与改道原因。</summary>
    public static EvidenceFile Write(string path, string header, string body)
    {
        var ev = Open(path, header);
        ev.Write(body);
        return ev;
    }

    private static string NormalizeHeader(string header)
    {
        var h = (header ?? "").Replace("\r\n", "\n");
        if (!h.EndsWith("\n", StringComparison.Ordinal)) h += "\n";
        return h;
    }

    /// <summary>两份头里不同的行（最多列三行，给改道原因用）。</summary>
    private static string DiffLines(string a, string b)
    {
        var la = a.Split('\n'); var lb = b.Split('\n');
        var onlyOld = la.Except(lb).Where(s => s.Length > 0).ToArray();
        var onlyNew = lb.Except(la).Where(s => s.Length > 0).ToArray();
        string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;
        var parts = new List<string>();
        parts.AddRange(onlyOld.Take(3).Select(s => "旧：" + Short(s)));
        parts.AddRange(onlyNew.Take(3).Select(s => "新：" + Short(s)));
        return parts.Count == 0 ? "行序不同" : string.Join("；", parts);
    }
}
