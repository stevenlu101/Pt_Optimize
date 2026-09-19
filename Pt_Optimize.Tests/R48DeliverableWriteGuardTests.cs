using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P2-3）：**测试不许按原文件名覆盖 deliverable 里的证据**（源码扫描，快）。
///
/// 病：deliverable 下的探针输出被 Core 注释、HANDOVER、别的测试引作证据；老探针直接 File.WriteAllText／AppendAllText 原文件名，一跑就换掉被引的那份（或往里追加）。
///   合并树上实测（2026-09-15 Opus 5 I 路扫全部测试的写文件调用与 deliverable 现有文件求交集）：慢测试里 60 处按原文件名（3 处是原出图目录名）写 deliverable，其中 E 两道慢门遇文件已存在就抛（合并树里原文件在 ⇒ 必抛）；
///   G1 的 EvidenceFile 只管点名的几个。已全部改走 DeliverableOut（带开跑时刻的新文件名）。
///
/// 规则（跑前写死）：凡含写文件调用（File.WriteAllText／AppendAllText／WriteAllLines／AppendAllLines／WriteAllBytes／Create／OpenWrite、new StreamWriter、EvidenceFile.Open／Write）的测试文件，
///   里面每一处出现字面量 "deliverable" 的语句都必须满足其一：
///   ① 带开跑时刻（语句里有 本次开跑于／DateTime.Now／RunStamp／Stamp／stamp／stamped）；
///   ② 赋给的变量交给 EvidenceFile.Open／Write（同名已存在且证据头不同就改道，G1）；
///   ③ 行里注明「// 只读」（只读 deliverable 里的输入，例如图纸 .3dm、列目录找最新一跑）；或整句只是 Directory.CreateDirectory（建目录，不写文件）；
///   ④ 在下面的「快套件回归产物」名单里（按测试文件 + 文件名写死；这些是快套件每跑都重写的输出，改带时刻会让每次快套件、每次提交钩子都往 deliverable 里添 12 份新文件）。
///   另：写 deliverable 的首选入口 DeliverableOut 本身、HandoverDoc、本门不扫。
/// 不覆盖：生产写出器（Geometry3dm.WriteFinal3dm、Plot.SavePng 等）不经 File.* 的写入；不在测试项目里的写入。
/// </summary>
public class R48DeliverableWriteGuardTests
{
    private readonly ITestOutputHelper _out;
    public R48DeliverableWriteGuardTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// 快套件（速度!=慢）每跑重写的输出 —— 名单写死（2026-09-15 Opus 5 I 路）：合并树快套件前后 md5 变了的 8 份 —— 即下表前 8 项：R47_第三轮N1_倒角实验、R48_舌根热点的边界条件、孔形对比、孔位对比、开孔的电流代价、形状族_对比、按场开槽_效果、移除优先级
    ///   （2026-09-16 Opus 5：原引 deliverable/_bak_merge/snap/changed.txt，那是 r48_M 未跟踪文件、本树没有，审查意见 4；名单直接写在这里，并由 2026-09-16 快套件跑前后 md5 对比再实测一次，结果记在 HANDOVER I 路修复注记），
    /// 加上同样不带慢标记、内容逐跑相同所以 md5 不变的 4 份（盘径不动点、四片为什么等厚、R48_网格结构对比、舌片电流密度分布）。
    /// ⚠ 它们仍按原文件名覆盖（开放事项：要么搬出 deliverable，要么接受「快套件前后备份还原」的做法）；新增一份要在这里写明，不许悄悄加。
    /// </summary>
    internal static readonly (string TestFile, string Name)[] FastSuiteOutputs =
    {
        ("MeshAxisTests.cs", "R47_第三轮N1_倒角实验_2026-09-13.txt"),
        ("R48TabRootConditionTests.cs", "R48_舌根热点的边界条件_2026-09-14.txt"),
        ("HolePlacementTests.cs", "孔形对比.txt"),
        ("HolePlacementTests.cs", "孔位对比.txt"),
        ("HoleRaisesCurrentDensityTests.cs", "开孔的电流代价.txt"),
        ("ShapeFamilyTests.cs", "形状族_对比.txt"),
        ("FieldGuidedSlotTests.cs", "按场开槽_效果.txt"),
        ("RemovalPriorityTests.cs", "移除优先级.txt"),
        ("DiscRadiusFixedPointTests.cs", "盘径不动点.txt"),
        ("PerPlateDivergeTests.cs", "四片为什么等厚.txt"),
        ("R48MeshStructureTests.cs", "R48_网格结构对比_2026-09-13.txt"),
        ("TabFieldSurveyTests.cs", "舌片电流密度分布.txt"),
    };

    private static readonly Regex WriteCall = new(@"File\.(WriteAllText|AppendAllText|WriteAllLines|AppendAllLines|WriteAllBytes|Create|OpenWrite)\s*\(|new\s+StreamWriter\s*\(|EvidenceFile\.(Open|Write)\s*\(");
    private static readonly Regex StampMark = new(@"本次开跑于|DateTime\.Now|RunStamp|\bStamp\b|\bstamp(ed)?\b");

    /// <summary>扫一份源码，返回违规行（「文件:行 内容」）；公开给注入实验用。</summary>
    internal static List<string> Violations(string fileName, string src, out int scanned)
    {
        scanned = 0;
        var bad = new List<string>();
        if (!WriteCall.IsMatch(src)) return bad;
        var lines = src.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (!l.Contains("\"deliverable\"", StringComparison.Ordinal) || l.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            scanned++;
            string stmt = l;
            for (int k = i + 1; k < lines.Length && k <= i + 3 && !stmt.Contains(';'); k++) stmt += "\n" + lines[k];
            if (StampMark.IsMatch(stmt)) continue;
            if (Regex.IsMatch(stmt, @"^\s*Directory\.CreateDirectory\(")) continue;   // 只建目录，不写文件
            if (l.Contains("// 只读", StringComparison.Ordinal)) continue;
            var v = Regex.Match(stmt, @"(?:string|var)\s+(\w+)\s*=");
            if (v.Success && Regex.IsMatch(src, @"EvidenceFile\.(Open|Write)\s*\(\s*" + Regex.Escape(v.Groups[1].Value) + @"\b")) continue;
            // 名单：语句里的文件名，或（目录变量）后面 6 行里 Path.Combine(变量, "名") 的文件名，全在名单里
            var names = Regex.Matches(stmt, "\"([^\"]+\\.(?:txt|md|csv))\"").Select(m => m.Groups[1].Value).ToList();
            if (names.Count == 0 && v.Success)
                for (int k = i + 1; k < lines.Length && k <= i + 6; k++)
                    names.AddRange(Regex.Matches(lines[k], @"Path\.Combine\(\s*" + Regex.Escape(v.Groups[1].Value) + "\\s*,\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value));
            if (names.Count > 0 && names.All(n => FastSuiteOutputs.Contains((fileName, n)))) continue;
            // ★ 2026-09-18 Opus 5（合并 I×L/P/U 补探测口，写明变因）：L／P／U 三路的探针用的是**两步写法**——
            //     string dir  = Path.Combine(HandoverDoc.Root(), "deliverable");   ← 这一行自己就以 ; 结束，行内没有时刻
            //     Directory.CreateDirectory(dir);
            //     string file = Path.Combine(dir, $"…本次开跑于{stamp}.txt");      ← 时刻在这里
            //   原探测只把「本语句 + 到分号为止」当作上下文 ⇒ 这 20 处全被判成「按原文件名写」，而它们**全都带开跑时刻**（合并时逐处看过）。
            //   这里把规则①原样延到目录变量上：本语句只是取目录时，后面 8 行里凡 Path.Combine(该变量, …) 的文件名**都**要带时刻才放行；
            //   一处没带、或一处都找不到，照旧算违规（fail closed，口径没有放宽）。
            if (v.Success)
            {
                var uses = new List<string>();
                for (int k = i + 1; k < lines.Length && k <= i + 8; k++)
                    foreach (Match mm in Regex.Matches(lines[k], @"Path\.Combine\(\s*" + Regex.Escape(v.Groups[1].Value) + @"\s*,\s*([^;]*)\)"))
                        uses.Add(mm.Groups[1].Value);
                if (uses.Count > 0 && uses.All(u => StampMark.IsMatch(u))) continue;
            }
            bad.Add($"{fileName}:{i + 1}　{l.Trim()}");
        }
        return bad;
    }

    [Fact]
    public void 测试写deliverable必须带时刻_或经EvidenceFile_或在快套件名单里()
    {
        string dir = Path.Combine(HandoverDoc.Root(), "Pt_Optimize.Tests");
        var skip = new HashSet<string> { "DeliverableOut.cs", "HandoverDoc.cs", nameof(R48DeliverableWriteGuardTests) + ".cs" };
        var bad = new List<string>();
        int scannedTotal = 0, stampedUses = 0, files = 0;
        foreach (var f in Directory.GetFiles(dir, "*.cs").OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(f);
            if (skip.Contains(name)) continue;
            string src = File.ReadAllText(f);
            stampedUses += Regex.Matches(src, @"DeliverableOut\.(Stamped|StampedDir)\(").Count;
            var v = Violations(name, src, out int scanned);
            if (scanned > 0) files++;
            scannedTotal += scanned;
            bad.AddRange(v);
        }
        _out.WriteLine($"扫到含写文件调用的测试文件里 \"deliverable\" 语句 {scannedTotal} 处（{files} 个文件）；DeliverableOut.Stamped／StampedDir 调用 {stampedUses} 处；快套件名单 {FastSuiteOutputs.Length} 份");
        Assert.True(bad.Count == 0, "这些测试按原文件名写 deliverable（会覆盖被引证据）：\n" + string.Join("\n", bad));
        // 非空转：扫描确实扫到了东西，名单每一项都还在它说的那个测试文件里
        Assert.True(scannedTotal >= 30, $"只扫到 {scannedTotal} 处，门空转");
        Assert.True(stampedUses >= 40, $"DeliverableOut 调用只有 {stampedUses} 处，门空转");
        foreach (var (tf, n) in FastSuiteOutputs)
            Assert.True(File.ReadAllText(Path.Combine(dir, tf)).Contains("\"" + n + "\"", StringComparison.Ordinal), $"快套件名单里的 {tf}「{n}」在源码里找不到了 —— 名单过期，删掉这一项");
    }

    [Fact]
    public void 注入_原文件名写deliverable会被抓到()
    {
        const string oldStyle = "class X { void M() { string file = Path.Combine(HandoverDoc.Root(), \"deliverable\", \"R48_某证据_2026-09-14.txt\");\n File.WriteAllText(file, \"x\"); } }";
        Assert.Single(Violations("X.cs", oldStyle, out _));
        const string stamped = "class X { void M() { string file = DeliverableOut.Stamped(\"R48_某证据_2026-09-14.txt\");\n File.WriteAllText(file, \"x\"); } }";
        Assert.Empty(Violations("X.cs", stamped, out _));
        const string appendInline = "class X { void M() { File.AppendAllText(Path.Combine(Root(), \"deliverable\", \"R47_某对拍.txt\"), \"x\"); } }";
        Assert.Single(Violations("X.cs", appendInline, out _));
        const string viaEvidence = "class X { void M() { string file = Path.Combine(Root(), \"deliverable\", \"R48_某.txt\");\n var ev = EvidenceFile.Open(file, h); } }";
        Assert.Empty(Violations("X.cs", viaEvidence, out _));
        const string listed = "class X { void M() { File.WriteAllText(Path.Combine(HandoverDoc.Root(), \"deliverable\", \"孔形对比.txt\"), \"x\"); } }";
        Assert.Single(Violations("X.cs", listed, out _));                       // 名单按测试文件写死：换个文件名就不算
        Assert.Empty(Violations("HolePlacementTests.cs", listed, out _));
    }
}
