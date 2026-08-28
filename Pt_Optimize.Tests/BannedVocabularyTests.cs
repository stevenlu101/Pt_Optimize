using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **被禁的词与方法**（用户 2026-08-28）：
///
/// > 「把『重新跑一次』『设计记录』『设计记录』这些[词]与[方法]，
/// >   不论是程式代码、所有 MD，在专案里**完全移除，不准再用**。」
///
/// ★ 为什么禁的是**方法**而不只是词：那套模式让一份静态常数同时扮演两个角色 ——
///   「**回归基准**」与「**计算起点/兜底**」。两个角色一混，就出了本轮查到的一整串：
///   五个优化变量从它继承、板厚从它兜底、复核报告拿它当对照、
///   出处指向一个当时不存在的工具、而它自己记的判据值又是 2 mm 导航网格上的产物
///   （③ 记 5.182，网格无关值 **10.539 / 10 —— 不过**）。
///
/// ⇒ 现在的流水线里**没有这个东西**：
///   输入（3DM / UI）→ 优化（粗网格导航）→ 网格无关复核 → 报告 + 出图。
///   `DesignSpec` 只是**一份设计规格**（数据结构），
///   `Record08` 之流只是**历史记录**，用来验「内核有没有漂」，不代表「设计对不对」。
///
/// ⚠ 本条查的是**源码与文档**，不查 obj/bin（那里是编译产物）。
/// </summary>
public class BannedVocabularyTests
{
    private static readonly string[] Banned = { "重新跑一次".Replace("重新跑一次", "定" + "案"),
                                                "定" + "案档", "定" + "檔", "定" + "档",
                                                "Final" + "Design" };

    private static IEnumerable<string> SourceFiles()
    {
        string root = HandoverDoc.Root();
        foreach (var dir in new[] { "Pt_Optimize", "Pt_Optimize.Tests", "Pt_Optimize.Geom", "tests", "docs", "deliverable" })
        {
            string d = Path.Combine(root, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                string e = Path.GetExtension(f).ToLowerInvariant();
                if (e is ".cs" or ".md") yield return f;
            }
        }
        foreach (var f in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)) yield return f;
    }

    [Fact]
    public void 被禁的词不许出现在源码与文档里()
    {
        var hits = new List<string>();
        foreach (var f in SourceFiles())
        {
            string s;
            try { s = File.ReadAllText(f); } catch { continue; }
            foreach (var w in Banned)
                if (s.Contains(w))
                    hits.Add($"{Path.GetFileName(f)} 含「{w}」");
        }
        Assert.True(hits.Count == 0,
            "这些词已被用户禁用（连同它代表的那套模式）：" + string.Join("；", hits.Take(12)));
    }

    [Fact]
    public void 被禁的词不许出现在文件名里()
    {
        string root = HandoverDoc.Root();
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
             || f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            string n = Path.GetFileName(f);
            foreach (var w in Banned)
                if (n.Contains(w)) { bad.Add(n); break; }
        }
        Assert.True(bad.Count == 0, "文件名里还有被禁的词：" + string.Join("；", bad.Take(12)));
    }

    /// <summary>
    /// 自证：这道门**真的查得到东西** —— 否则它可能在一个空集上恒过
    /// （「空集恒真」是本项目记过案的形态）。
    /// </summary>
    public class 自证
    {
        [Fact]
        public void 扫到的文件不是空集()
        {
            int n = SourceFiles().Count();
            Assert.True(n > 50, $"只扫到 {n} 个文件 —— 这道门八成在空集上恒过");
        }

        [Fact]
        public void 禁用清单本身不是空的()
        {
            Assert.NotEmpty(Banned);
            Assert.All(Banned, w => Assert.False(string.IsNullOrWhiteSpace(w)));
        }
    }
}
