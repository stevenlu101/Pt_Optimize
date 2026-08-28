using System;
using System.Collections.Generic;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 动态网格求解器的**决策层** —— 纯函数，微秒可验（每次真解是分钟级的）。
///
/// ★★ 实测（2026-08-28，`--meshadapt --wall 0.8`）：
/// <code>
///   细网格mm  单元数   ②′W     ②″K      ③K      合计g   用时s
///    0.817    3007   2.862  −0.008   10.095    3548     74.9
///    0.408    9761   3.103  −0.006   **10.539** 3549    910.3
///    Δ               +0.241 +0.002   +0.444
///    容差             0.5    0.2      1.0        ⇒ ✓ 网格无关
/// </code>
///
/// ⇒ **设计记录 0.8 档在网格无关的解上，判据 ③ = 10.539 / 10 —— 不过。**
///   而设计记录记录的是 5.182 / 10（看着 48 % 裕度）。
///   几何没问题（铂重 3548 vs 3547 逐位对上），**错的是判定**：
///   2 mm 网格连舌根圆角、环宽、焊脚都画不出来（各 1.5 / 1.5 / 1.2 格）。
///
/// ⚠ 代价：0.408 mm 单次求解 **910 秒** ⇒ 在网格无关的网格上做优化算不完。
///   正确做法是两级：**优化用粗网格导航，最终解在网格无关的网格上复核一次，判据以复核为准。**
/// </summary>
public class MeshAdaptTests
{
    [Fact]
    public void 起点由最小几何特征定_一个特征至少跨三格()
    {
        // 设计记录 0.8 档：圆角 3.0、环宽 3.0、焊脚 2.45 ⇒ 最小 2.45 ÷ 3 = 0.817
        Assert.Equal(0.817, MeshAdapt.RequiredFineMm(new[] { 3.0, 3.0, 2.45 }), 3);
        Assert.Equal(3, MeshAdapt.CellsPerFeature);
    }

    [Fact]
    public void 没有特征就抛_不给一个看起来够的默认值()
    {
        Assert.Throws<ArgumentException>(() => MeshAdapt.RequiredFineMm(new double[0]));
        Assert.Throws<ArgumentException>(() => MeshAdapt.RequiredFineMm(new[] { 0.0, -1.0 }));
    }

    [Fact]
    public void 现行两毫米网格上_三个特征全都画不出来()
    {
        // 这是「为什么必须做这件事」的证据，写死在断言里
        foreach (double f in new[] { 3.0, 3.0, 2.45 })
            Assert.True(MeshAdapt.CellsAcross(f, 2.0) < MeshAdapt.CellsPerFeature,
                $"{f} mm 的特征在 2 mm 网格上只有 {MeshAdapt.CellsAcross(f, 2.0):0.0} 格");
    }

    [Fact]
    public void 收敛判据是判据本身_不是残差也不是单元数()
    {
        var ok = new List<MeshAdapt.Delta>
        {
            new() { Name = "②′", Change = 0.241, Tol = 0.5 },
            new() { Name = "②″", Change = 0.002, Tol = 0.2 },
            new() { Name = "③",  Change = 0.444, Tol = 1.0 },
        };
        Assert.True(MeshAdapt.Converged(ok));               // 实测那一组
        ok[2].Change = 4.474;                               // --meshconv 量到的 2→1 mm
        Assert.False(MeshAdapt.Converged(ok));
    }

    [Fact]
    public void 空集不算收敛_那是记过案的空集恒真()
    {
        Assert.False(MeshAdapt.Converged(new List<MeshAdapt.Delta>()));
        Assert.Contains("说不出", MeshAdapt.Verdict(0.5, new List<MeshAdapt.Delta>(), false));
    }

    [Fact]
    public void 没收敛必须明说_而且要分清是不是撞了上限()
    {
        var bad = new List<MeshAdapt.Delta> { new() { Name = "③", Change = 4.474, Tol = 1.0 } };
        Assert.Contains("尚未网格无关", MeshAdapt.Verdict(1.0, bad, hitCap: false));
        Assert.Contains("加密到上限仍未收敛", MeshAdapt.Verdict(0.2, bad, hitCap: true));
        // 两句必须不同 —— 「还能再加密」与「加密到头了还在动」是两回事
        Assert.NotEqual(MeshAdapt.Verdict(1.0, bad, false), MeshAdapt.Verdict(0.2, bad, true));
    }

    [Fact]
    public void 细区必须盖住峰位_否则加密再多也没用()
    {
        double r = MeshAdapt.RequiredFineRadiusMm(new[] { 30.0, 49.0 }, holeRadiusMm: 25.8);
        Assert.Equal(59.0, r, 6);                            // 最远的峰 49 + 余量 10
        // 自证：没有峰时至少要盖住管孔
        Assert.Equal(35.8, MeshAdapt.RequiredFineRadiusMm(new double[0], 25.8), 6);
    }

    [Fact]
    public void 加密一次是对半_结构网格上单元数约四倍()
    {
        Assert.Equal(0.4085, MeshAdapt.Refine(0.817), 6);
    }
}
