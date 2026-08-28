using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 两条「同一个数两处来源」—— 第一性原理通查 2026-08-28 查出，主进程逐条复验。
/// </summary>
public class TwoSourceGuardTests
{
    // ── ① 屈曲系数：真正在用的那个必须有名字 ──────────────────────

    /// <summary>
    /// ★ 病灶：法兰盘用的是 <c>k_b ≈ 0.43</c>（三边简支一边自由），
    ///   而被命名、被文档、被 `--welddistort` 报告表头印出来的是 <c>PlateBucklingK = 4.0</c>
    ///   （四边简支）。0.43 此前是**14 处无名字面量**。
    ///   ⇒ 任何新调用点忘写 <c>kb:</c> 就静默拿到 4.0，**下界小 9.3 倍**
    ///   （0.06 而不是 0.55 mm），不报错、格式正常、结论错。
    /// </summary>
    [Fact]
    public void 法兰用的屈曲系数有名字_而且不是那个四边简支的()
    {
        Assert.Equal(0.43, WeldDistortion.PlateBucklingKFreeEdge, 9);
        Assert.Equal(4.0, WeldDistortion.PlateBucklingK, 9);
        // 自证：两者确实不同，否则「用错了」这件事根本不存在
        Assert.NotEqual(WeldDistortion.PlateBucklingK, WeldDistortion.PlateBucklingKFreeEdge, 6);
    }

    [Fact]
    public void 不许再出现裸的_kb_0点43()
    {
        // 只查**调用点**（Core 与 Program）；WeldDistortion.cs 自己的病历注释里要引用旧写法。
        foreach (var f in new[] { "Pt_Optimize/Core/DesignSpec.cs", "Pt_Optimize/Program.cs" })
            Assert.DoesNotContain("kb: 0.43",
                File.ReadAllText(Path.Combine(HandoverDoc.Root(), f)));
    }

    [Fact]
    public void 差多少倍_把危害钉住()
    {
        // 忘写 kb 会拿到 4.0；下界 ∝ k_b 的平方根之类的关系由 ForPt 内部定，
        // 这里只钉「两者相差近一个数量级」这个**危害量级**，不钉具体公式。
        double ratio = WeldDistortion.PlateBucklingK / WeldDistortion.PlateBucklingKFreeEdge;
        Assert.True(ratio > 9.0, $"两个系数应相差近一个数量级，实际 {ratio:0.0}×");
    }

    // ── ② 管孔半径：两处来源，对不上就拒算 ────────────────────────

    /// <summary>
    /// ★ 病灶：<see cref="DesignSpec.HoleRadiusMm"/> 是 <c>WallMm + 25.0</c>（**写死的 25**），
    ///   几何、3DM 图纸、板厚工艺下界都用它；
    ///   而求解器走 <c>TubeIdMm*0.5 + WallMm</c>（跟着参数表的管内径）。
    ///   两者只在 <c>TubeIdMm = 50</c> 时相等 ⇒ 改了管内径，
    ///   **求解器动了、几何不动，且不报错**。
    ///
    /// 彻底修要把 TubeIdMm 接进几何（铁律②）—— **尚未做**。
    /// 在此之前**不许静默**：对不上就拒算。
    /// </summary>
    [Fact]
    public void 管内径与写死的孔半径对不上时_当场拒算()
    {
        var p = new DesignInputs { TubeIdMm = 60.0 };     // 半径 30 ≠ 写死的 25
        var ex = Assert.Throws<ArgumentException>(() => DesignSpec.W08.BuildCase(p));
        Assert.Contains("对不上", ex.Message);
        Assert.Contains("铁律", ex.Message);
        Assert.Contains("宁可拒算", ex.Message);
    }

    [Fact]
    public void 默认管内径下照常放行_自证这道闸不是恒抛()
    {
        var p = new DesignInputs();
        Assert.Equal(50.0, p.TubeIdMm, 9);               // 默认就是 50
        var lc = DesignSpec.W08.BuildCase(p);            // 不抛
        Assert.NotNull(lc);
    }
}
