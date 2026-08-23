using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 逐级定厚里「第 j 片的各级厚度」。
///
/// 这段代码在 2026-08-23 之前**从未被执行过**（没有调用方传 makePlateByLevel），
/// 里面藏着两个雷，两个都不会报错、只会让优化器对着**另一片板**求解：
///   ① 片序号靠闭包里的 `callIdx++ % 片数` 推
///   ② 基准级厚一律取第 0 片的
/// 接线之前先把它们钉死 —— 死代码不是中性的，它是一个随时会被调用的错答案。
/// </summary>
public class LevelThicknessTests
{
    private static double[][] Bases() => new[]
    {
        new[] { 1.0, 2.0 },      // 片1
        new[] { 3.0, 4.0 },      // 片2
        new[] { 5.0, 6.0 },      // 片3
        new[] { 7.0, 8.0 },      // 片4
    };

    private static double[][] Scales() => new[]
    {
        new[] { 1.0, 1.0 },
        new[] { 2.0, 1.0 },
        new[] { 1.0, 3.0 },
        new[] { 0.5, 0.5 },
    };

    /// <summary>
    /// 每一片都要拿**自己**的基准。旧写法对四片一律用 `levelThicknessMm[0]` ——
    /// 这条断言在旧写法下第 2、3、4 片全错。
    /// </summary>
    [Theory]
    [InlineData(0, new[] { 1.0, 2.0 })]
    [InlineData(1, new[] { 6.0, 4.0 })]     // 3×2, 4×1
    [InlineData(2, new[] { 5.0, 18.0 })]    // 5×1, 6×3
    [InlineData(3, new[] { 3.5, 4.0 })]     // 7×0.5, 8×0.5
    public void EachPlate_UsesItsOwnBaseAndScale(int j, double[] expect)
    {
        var got = FlangeAutoSizer.LevelThicknessFor(Bases(), Scales(), j, 1.0);
        Assert.Equal(expect.Length, got.Length);
        for (int m = 0; m < expect.Length; m++)
            Assert.True(Math.Abs(got[m] - expect[m]) < 1e-12,
                $"第 {j + 1} 片第 {m + 1} 级：{got[m]} ≠ {expect[m]}");
    }

    /// <summary>
    /// 自证：四片的结果必须**两两不同**。
    /// 少了这一条，一个「永远返回第 0 片」的坏实现只要第 0 片对，就能骗过上面那组
    /// —— 除非上面每一片的期望值都不一样。这里把「不一样」本身也断言了。
    /// </summary>
    [Fact]
    public void FourPlates_AreActuallyDistinct()
    {
        var all = new double[4][];
        for (int j = 0; j < 4; j++) all[j] = FlangeAutoSizer.LevelThicknessFor(Bases(), Scales(), j, 1.0);
        for (int a = 0; a < 4; a++)
            for (int b = a + 1; b < 4; b++)
                Assert.False(Math.Abs(all[a][0] - all[b][0]) < 1e-12
                          && Math.Abs(all[a][1] - all[b][1]) < 1e-12,
                    $"第 {a + 1} 片与第 {b + 1} 片算出来一样 —— 片序号没起作用");
    }

    /// <summary>外层标量 k 按比例乘上去，不碰各级之间的比例</summary>
    [Fact]
    public void OuterScalar_ScalesAllLevelsProportionally()
    {
        var one = FlangeAutoSizer.LevelThicknessFor(Bases(), Scales(), 2, 1.0);
        var two = FlangeAutoSizer.LevelThicknessFor(Bases(), Scales(), 2, 2.0);
        for (int m = 0; m < one.Length; m++)
            Assert.True(Math.Abs(two[m] - 2.0 * one[m]) < 1e-12);
    }

    /// <summary>
    /// 级数对不上要**抛**。凑一个不会报错，只会算出另一片板的厚度 ——
    /// 而那正是本项目最怕的失效形态：数字看着正常，判据表照样漂亮。
    /// </summary>
    [Fact]
    public void LevelCountMismatch_Throws()
    {
        var bad = new[] { new[] { 1.0, 2.0, 3.0 } };
        Assert.Throws<ArgumentException>(
            () => FlangeAutoSizer.LevelThicknessFor(bad, new[] { new[] { 1.0, 1.0 } }, 0, 1.0));
    }
}
