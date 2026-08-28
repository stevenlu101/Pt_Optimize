using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **把渐变环的形状放开成变量**（2026-08-28，算法普查 A 类第 ⑨ 条）。
///
/// 病灶：一个两级台阶本来要 **4 个数**（r₁、r₂、t₁、t₂），
/// 而此前只有 t₁ 是优化变量，另外三个全被写死：
///
/// <code>
///   r₁ = 孔 + w
///   r₂ = 孔 + **2**w            ← 「2 倍」写死
///   t₂ = t₁ 按 **0.4** 过渡     ← 0.4 写死
/// </code>
///
/// ⇒ 解析路上称不上「厚度梯度优化」，只是「一个受单旋钮控制、形状写死的台阶」。
/// 对照 `.3dm` 路的 <c>LevelScale[片][级]</c>：**每片每级一个自由度**，那才是逐级优化。
///
/// ★ 本组测试钉的是**放开之后默认必须一字不差地复现旧几何**。
///   放开变量最怕的不是算错，是**顺手把默认行为也改了**——
///   那样所有历史结果的可比性会在没人察觉时消失。
///   所以三个新变量都用 NaN 作「没给」，NaN 时退回历史关系；
///   而历史关系从此是**显式的兜底**，不再是藏在式子里的常数。
/// </summary>
public class RingShapeFreedomTests
{
    private static DesignSpec Spec()
    {
        var d = new DesignSpec { RingWidthMm = 3.0 };
        d.RingMul[0] = 1.00; d.RingMul[1] = 1.50; d.RingMul[2] = 2.00; d.RingMul[3] = 1.25;
        return d;
    }

    /// <summary>默认（全 NaN）时，两级半径必须还是 孔+w 与 孔+2w。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 默认半径逐位复现旧公式(int j)
    {
        var d = Spec();
        var r = d.RingRadiiOf(j);
        Assert.Equal(d.HoleRadiusMm + d.RingWidthMm, r[0], 12);
        Assert.Equal(d.HoleRadiusMm + 2 * d.RingWidthMm, r[1], 12);
    }

    /// <summary>默认（NaN）时，外级倍率必须还是 1 + 0.4(μ−1)。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 默认外级倍率逐位复现旧公式(int j)
    {
        var d = Spec();
        Assert.Equal(1 + (d.RingMul[j] - 1) * 0.4, d.RingMulOuter(j), 12);
    }

    /// <summary>给了值就用给的 —— 这三个从此是**真变量**，不是被公式绑死的从属量。</summary>
    [Fact]
    public void 给了值就压过历史关系()
    {
        var d = Spec();
        d.RingW1Mm[1] = 5.0;
        d.RingW2Mm[1] = 11.0;
        d.RingMul2[1] = 1.9;

        var r = d.RingRadiiOf(1);
        Assert.Equal(d.HoleRadiusMm + 5.0, r[0], 12);
        Assert.Equal(d.HoleRadiusMm + 11.0, r[1], 12);
        Assert.Equal(1.9, d.RingMulOuter(1), 12);

        // 只改第 1 片，别的片不许被带着动
        Assert.Equal(d.HoleRadiusMm + d.RingWidthMm, d.RingRadiiOf(0)[0], 12);
        Assert.Equal(1 + (d.RingMul[2] - 1) * 0.4, d.RingMulOuter(2), 12);
    }

    /// <summary>
    /// 外级倍率现在可以**大于**内级（旧公式做不到：0.4 过渡永远把外级拉向 1）。
    /// 这正是「放开」的意义 —— 旧公式连「外厚内薄」这种形状都表达不了。
    /// </summary>
    [Fact]
    public void 旧公式表达不了的形状现在能表达()
    {
        var d = Spec();
        d.RingMul[1] = 1.5;
        Assert.True(d.RingMulOuter(1) < d.RingMul[1]);   // 旧关系：外级必被拉向 1

        d.RingMul2[1] = 2.2;                              // 外厚内薄
        Assert.True(d.RingMulOuter(1) > d.RingMul[1]);
    }

    /// <summary>
    /// ★ 半径不递增必须**炸**，不能静默。
    /// 阶梯表按「r ≤ 各级半径」依次命中，顺序错了会取到错的那一级 ——
    /// 几何照画、温度全错，正是本项目最怕的错误形态。
    /// </summary>
    [Fact]
    public void 半径不递增当场炸而不是静默取错级()
    {
        var d = Spec();
        d.RingW1Mm[0] = 8.0;
        d.RingW2Mm[0] = 4.0;                              // 外级比内级还小
        var ex = Assert.Throws<InvalidOperationException>(() => d.RingRadiiOf(0));
        Assert.Contains("没有递增", ex.Message);

        d.RingW2Mm[0] = 8.0;                              // 相等也不行（那一级宽度为零）
        Assert.Throws<InvalidOperationException>(() => d.RingRadiiOf(0));
    }

    /// <summary>Clone 必须把三个新数组也复制 —— 漏一个就会两份设计共享同一个数组。</summary>
    [Fact]
    public void 克隆带上三个新变量()
    {
        var d = Spec();
        d.RingW1Mm[2] = 4.0; d.RingW2Mm[2] = 9.0; d.RingMul2[2] = 1.7;

        var c = d.Clone();
        Assert.Equal(4.0, c.RingW1Mm[2], 12);
        Assert.Equal(9.0, c.RingW2Mm[2], 12);
        Assert.Equal(1.7, c.RingMul2[2], 12);

        c.RingW1Mm[2] = 99.0;                             // 改副本不许动到原件
        Assert.Equal(4.0, d.RingW1Mm[2], 12);
    }

    /// <summary>
    /// 旧的 <c>RingRadiiMm</c> 属性只剩「第 0 片视角」，且求解路径不许再用它 ——
    /// 各片放开成不同形状之后，它会悄悄只反映第 0 片。
    /// </summary>
    [Fact]
    public void 旧属性降级为第零片视角()
    {
        var d = Spec();
        d.RingW1Mm[0] = 7.0;
        d.RingW2Mm[0] = 12.0;      // ★ 必须一起给：默认 r₂=2w=6 < 7，会撞上递增检查
        Assert.Equal(d.RingRadiiOf(0)[0], d.RingRadiiMm[0], 12);
        Assert.Equal(d.RingRadiiOf(0)[1], d.RingRadiiMm[1], 12);

        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "Core", "DesignSpec.cs"));
        Assert.Contains("DiscStepRadiiMm = RingRadiiOf(j),", src);   // 建模走逐片
    }
}
