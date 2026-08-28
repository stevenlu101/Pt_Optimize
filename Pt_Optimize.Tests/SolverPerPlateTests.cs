using System.Collections.Generic;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **逐片求解**：段↔片的错位映射，以及「为什么四片不能绑成一个」。
///
/// 病灶（2026-08-28 实测确诊，不是推测）：第一版求解器四片同步抬，
/// `--solve --seedprobe` 实测**解不出来** —— 板厚↑修好②′却打坏③，
/// 舌保温↑修好③却打坏②′，两条判据互相追到盒顶（舌保温顶到上界 20 mm）。
/// 而它把舌保温收到 **18.826**，历史记录第一片正是 **18.7**：
/// 求解器**找对了那一片的答案**，却被「四片必须相同」逼着把它糊到另外三片上。
///
/// 四片物理上本来就不同：端片只接一段；共用片接两段，两段电流相差 120°
/// （矢量合成 √(I₁²+I₂²+I₁I₂) ≈ √3 倍），发热 ∝I² 是端片的 **2.6 倍**
/// （实测自由端下 490 W vs 156 W）。⇒ **四片同步不是简化，是错的模型。**
///
/// ★ `FlangeAutoSizer` 开头早就写着同一句：「n 段有 n 个约束，n+1 片有 n+1 个厚度自由度；
///   若把四片绑成一个标度，就只剩 1 个自由度」。`.3dm` 那条路早就知道，
///   解析路是 2026-08-28 从实测重新撞出来的。
/// </summary>
public class SolverPerPlateTests
{
    /// <summary>入口片只担段 0 的 A 端。</summary>
    [Fact]
    public void 入口片只有一侧()
    {
        var e = Solver.EndsOf(0, 3);
        Assert.Single(e);
        Assert.Equal((0, true), e[0]);
    }

    /// <summary>出口片（j = n）只担最后一段的 B 端。</summary>
    [Fact]
    public void 出口片只有一侧()
    {
        var e = Solver.EndsOf(3, 3);
        Assert.Single(e);
        Assert.Equal((2, false), e[0]);
    }

    /// <summary>共用片两侧都担：段 j 的 A 端 ＋ 段 j−1 的 B 端。</summary>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 1)]
    public void 共用片两侧都担(int j, int segA, int segB)
    {
        var e = Solver.EndsOf(j, 3);
        Assert.Equal(2, e.Length);
        Assert.Contains((segA, true), e);
        Assert.Contains((segB, false), e);
    }

    /// <summary>
    /// ★★ **不重不漏**：把所有片的结果并起来，恰好覆盖 n 段 × 2 端。
    ///
    /// 漏一个端 = 有一片的责任没人担（那条违反永远治不好）；
    /// 重一个端 = 同一个违反被两片抢着治（两片都抬 ⇒ 抬出不必要的铂重）。
    /// 这种错位一位的下标最容易写反，而写反后**跑起来一切正常，只是抬错了片**。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void 所有片合起来恰好覆盖每段每端不重不漏(int nSeg)
    {
        var seen = new List<(int, bool)>();
        for (int j = 0; j <= nSeg; j++) seen.AddRange(Solver.EndsOf(j, nSeg));

        Assert.Equal(nSeg * 2, seen.Count);                 // 不重
        Assert.Equal(nSeg * 2, seen.Distinct().Count());    // 不漏（去重后个数不变 ⇒ 无重复）
        for (int i = 0; i < nSeg; i++)
        {
            Assert.Contains((i, true), seen);
            Assert.Contains((i, false), seen);
        }
    }

    /// <summary>片号越界时给空，而不是悄悄映射到别的段。</summary>
    [Fact]
    public void 片号越界给空而不是错映()
    {
        Assert.Empty(Solver.EndsOf(-1, 3));
        Assert.Empty(Solver.EndsOf(9, 3));
        Assert.Empty(Solver.EndsOf(0, 0));
    }

    private static string Src(string dir, string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", dir, f));

    /// <summary>
    /// ★ 两端的增量温降必须**各自存下来**。
    /// 只留 <c>Math.Max(dA, dB)</c> 的话，判据说得出「有一片超了」，
    /// **说不出是哪一片** —— 而逐片解必须知道该抬哪一片的舌保温。
    /// </summary>
    [Fact]
    public void 两端的增量温降各自存下来()
    {
        string s = Src("Core", "LineRunner.cs");
        Assert.Contains("public double FlangeDipAK = double.NaN, FlangeDipBK = double.NaN;", s);
        Assert.Contains("s.FlangeDipAK = dA; s.FlangeDipBK = dB;", s);
        // 汇总量仍要留着 —— 整体判据用它，两者不是替代关系
        Assert.Contains("s.FlangeDipK = double.IsNaN(dA) || double.IsNaN(dB)", s);
    }

    /// <summary>旋钮读写必须带片号；退回「一次写全部四片」就是退回错的模型。</summary>
    [Fact]
    public void 旋钮读写带片号()
    {
        string s = Src("Core", "Solver.cs");
        Assert.Contains("private static double Get(DesignSpec d, Knob k, int j)", s);
        Assert.Contains("private static void Set(DesignSpec d, Knob k, int j, double v)", s);

        // Set 里不许再出现「遍历所有片一起写」
        Assert.DoesNotContain("for (int j = 0; j < d.TabThickMm.Length; j++)\r\n        {\r\n            switch (k)", s);
        Assert.DoesNotContain("for (int j = 0; j < d.TabThickMm.Length; j++)\n        {\n            switch (k)", s);
    }

    /// <summary>
    /// 限值只从 <c>LineCase</c> 读（判据的唯一来源）。求解器自带第二份限值，
    /// 就会出现「印出来的 ≠ 判的」—— 本仓库出过这个错。
    /// </summary>
    [Fact]
    public void 限值只从判据自己那里读()
    {
        string s = Src("Core", "Solver.cs");
        Assert.Contains("lc.RootDeltaMaxK", s);
        Assert.Contains("lc.DiscOverTempMaxK", s);
        // 不许把限值写成常数
        Assert.DoesNotContain("dipMax = 10", s);
        Assert.DoesNotContain("discMax = 5", s);
    }

    /// <summary>
    /// 上一片抬完可能把这一片捎带治好 —— 那就**不抬**。
    /// 少了这个判断，最小性就没了（会抬出不必要的铂重）。
    /// </summary>
    [Fact]
    public void 已经不违反的片不抬()
    {
        string s = Src("Core", "Solver.cs");
        Assert.Contains("已经不违反", s);
        Assert.Contains("**不抬**", s);
    }

    /// <summary>没有旋钮能治的判据（⑥ 等）要**报出来**，不能当成全过。</summary>
    [Fact]
    public void 没有旋钮的判据要报出来()
    {
        string s = Src("Core", "Solver.cs");
        Assert.Contains("没有旋钮能治", s);
        Assert.Contains("判不了不算过", s);
    }
}
