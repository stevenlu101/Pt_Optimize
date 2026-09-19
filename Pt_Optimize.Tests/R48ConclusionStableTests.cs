using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R48（2026-09-13，Opus 5 写）：**收敛要的是「结论稳」，不是小数点稳**。
///
/// 实测逼出来的（管壁 0.8，整档加密 1.0／0.5／0.25 mm）：
///   管孔净流入 5.090 → 7.134 → 5.068 W，相邻两档跳 ±2 W，而它的容差是 0.5
///   ⇒ 按「数值落进容差」判，这一条**永远不收敛**，加密复算会一直跑到撞上单元上限。
///   可它离限值 0 还有 5 W —— 再加密也翻不过去，「过」这个结论是稳的。
///   法兰增量温降 22.571 → 22.371 → 21.582 K 同理：离限值 10 还有 11.6 K，「不过」也是稳的。
///
/// ⚠ 这不是把门放宽：**判据值贴近限值时它一点也不放行** —— 那时距离小于摆幅，结论真的会翻，
///   必须继续加密。本组钉的就是这条界线两边的行为。
/// </summary>
public class R48ConclusionStableTests
{
    private static MeshAdapt.Delta D(string name, double change, double tol, double margin)
        => new() { Name = name, Change = change, Tol = tol, MarginOverChange = margin };

    [Fact]
    public void 数值超容差但离限值够远_算这一条已经算准()
    {
        // 管孔净流入实测那一组：动 2.066，容差 0.5 ⇒ 数值不达标；离限值 5.068，比动幅大 2.45 倍 ⇒ 还不够
        var notYet = D("管孔净流入", 2.066, 0.5, 5.068 / 2.066);
        Assert.False(notYet.Within);
        Assert.False(notYet.ConclusionStable);        // 2.45 倍 < 3 倍 ⇒ 不放行

        // 离限值 10 倍动幅 ⇒ 结论翻不过来
        var stable = D("管孔净流入", 2.0, 0.5, 10.0);
        Assert.False(stable.Within);
        Assert.True(stable.ConclusionStable);
    }

    [Fact]
    public void 判据贴着限值时一点也不放行()
    {
        // 判据值离限值只有半个动幅 —— 再加密一档就可能翻过去，必须继续加密
        var onEdge = D("法兰增量温降", 1.0, 1.0, 0.5);
        Assert.True(onEdge.Within);                   // 数值恰好落进容差
        Assert.False(onEdge.ConclusionStable);        // 但结论不稳（0.5 倍 < 3 倍）
        // 两者是「或」的关系：Within 成立就算这一条过了，本例过的是数值那一半
        Assert.True(MeshAdapt.Converged(
            new[] { onEdge }, new[] { D("法兰增量温降", 2.0, 1.0, 0.4) }));
    }

    [Fact]
    public void 拿不到限值时退回只看数值()
    {
        var noLimit = D("圆盘区最高温", 0.9, 0.2, double.NaN);
        Assert.False(noLimit.Within);
        Assert.False(noLimit.ConclusionStable);       // NaN ⇒ 不放行（拿不到限值就不用这条）
    }

    [Fact]
    public void 结论稳的那条_不再要求变化量单调缩小()
    {
        // 这一组的变化量在**变大**（1.0 → 2.0）：按旧判据一定是「没在收敛」；
        // 但它离限值有 10 倍动幅，结论翻不过来 ⇒ 放行。
        var now = new[] { D("管孔净流入", 2.0, 0.5, 10.0) };
        var was = new[] { D("管孔净流入", 1.0, 0.5, 20.0) };
        Assert.True(MeshAdapt.Converged(now, was));

        // 同一组数，若离限值只有 1 倍动幅 ⇒ 不放行（结论会翻）
        var near = new[] { D("管孔净流入", 2.0, 0.5, 1.0) };
        Assert.False(MeshAdapt.Converged(near, was));
    }

    [Fact]
    public void 判词要逐条说_一条没算准不许把另一条也说成不可信()
    {
        // 实测那一组：净流入还在跳（离限值 2.45 倍动幅，没到 3 倍），增量温降离限值 14.7 倍 ⇒ 结论稳
        var deltas = new[]
        {
            D("管孔净流入", -2.066, 0.5, 2.45),
            D("圆盘区最高温", 0.010, 0.2, 500),
            D("法兰增量温降", -0.789, 1.0, 14.7),
        };
        string v = MeshAdapt.Verdict(0.25, deltas, hitCap: true);
        Assert.Contains("管孔净流入", v);
        Assert.Contains("**这几条**", v);                 // 说的是「这几条」不是「这些判据值」
        Assert.Contains("已经算准的", v);                  // 另外两条要被点名说算准了
        Assert.Contains("法兰增量温降", v);
        Assert.DoesNotContain("这些判据值**还带着离散误差**", v);   // 旧的一票否决措辞不许再出现
    }

    [Fact]
    public void 全部结论稳时_判词说结论算准而不是数不再变()
    {
        var deltas = new[]
        {
            D("管孔净流入", -2.0, 0.5, 10.0),
            D("法兰增量温降", -0.5, 1.0, 20.0),
        };
        string v = MeshAdapt.Verdict(0.25, deltas, hitCap: false);
        Assert.StartsWith("✓", v);
        Assert.Contains("结论已经算准了", v);
        Assert.Contains("结论翻不过来", v);
    }

    [Fact]
    public void 空集仍然不算收敛()
    {
        Assert.False(MeshAdapt.Converged(Array.Empty<MeshAdapt.Delta>()));
        Assert.False(MeshAdapt.Converged(new[] { D("x", 0.1, 1.0, 99) }));   // 只有一对 ⇒ 没有趋势，不算
    }

    /// <summary>
    /// ★★ R48（2026-09-13，Opus 5）：**加密必须自相似 —— 粗区也要跟着缩**。
    ///
    /// 用户 09-13 质疑「加密不收敛就不是噪声」逼出来的：加密复算此前只缩细区，粗区钉在 11 mm 不动，
    /// 细/粗尺寸比随加密从 11 倍变 22 倍再变 44 倍，两区之间那段 growth 过渡的结构跟着剧变 ——
    /// 而过渡区正压在舌片（电流主通路）上。实测同一设计整线全耦合：
    ///   只缩细区：管孔净流入 5.090／7.134／5.068 W（跳）
    ///   整张缩  ：5.919／6.196／6.325 W，法兰增量温降 22.719／22.113／21.846 K（差值比 0.47／0.44，干净的一阶收敛）
    /// 本门钉源码：两条路径（解析与图纸）的每档工厂都要缩 MeshCoarseMm。
    /// </summary>
    /// ★ R48 续（同日）：配方**收进 <c>MeshAdapt.RefineWholeMesh</c> 一处**之后，本门改钉
    ///   「两条路径的每档工厂都调那个共用配方」。原来钉的是各自手写的那行 —— 那种钉法
    ///   只保得住写过的两处，保不住第三处（求解器的 --fine 当时就漏着，当天出了事）。
    ///   配方内部「比例以原中带为基准、排在改中带之前」由 R48OneRefineRecipeTests 钉。
    [Fact]
    public void 加密要整张网格一起缩_粗区不许钉死()
    {
        foreach (var (file, who) in new[] { ("Pt_Optimize/Core/MeshVerify.cs", "解析路径"), ("Pt_Optimize/UI/LineDesignPage.cs", "图纸路径") })
        {
            string src = System.IO.File.ReadAllText(SrcPath(file));
            int i = src.IndexOf("MeshAdapt.RefineWholeMesh(", StringComparison.Ordinal);
            Assert.True(i > 0, $"{who}（{file}）的每档工厂没走共用的加密配方 —— "
                             + "加密就可能不是自相似的，判据会跟着网格结构跳");
            // 这一档的中带必须真的传进去（不是传个常数）
            int from = Math.Max(0, i - 100), to = Math.Min(src.Length, i + 200);
            Assert.Contains("hMid", src.Substring(from, to - from));
        }
    }

    private static string SrcPath(string rel)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, rel);
    }
}
