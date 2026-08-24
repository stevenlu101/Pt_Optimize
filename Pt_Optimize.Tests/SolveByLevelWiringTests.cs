using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// `SolveByLevel` 的**解析路径接线**（2026-08-24 补）。
///
/// 那条路（makePlateByLevel）此前没有任何调用方，里面藏着两个不会报错的错：
/// 片序号靠闭包里的计数器推、基准级厚一律取第 0 片。两者都只会让优化器
/// 对着**另一片板**求解，输出照样是一张漂亮的判据表。
/// `LevelThicknessFor` 的纯函数单测已经把公式钉住了；这里补**端到端**那一段：
/// 真跑一轮外层，把回调实际收到的厚度向量记下来对账。
///
/// ⚠ 仍**没有**覆盖「.3dm 图纸上替身被放行」那条路 —— 仓库里没有
///   「无槽 + 多级」的 .3dm，而 WriteFinal3dm 写出来是多图层的定案几何、
///   分析器要的是单图层。造一个要改 Rhino 子进程。这条缺口写在这里，不假装覆盖到了。
/// </summary>
public class SolveByLevelWiringTests
{
    [Fact]
    public void AnalyticPath_EachPlateGetsItsOwnBaseLevels()
    {
        // 四片**基准级厚各不相同** —— 旧写法一律取第 0 片，那样这条会红
        var levels = new[]
        {
            new[] { 1.0, 2.0 },
            new[] { 1.1, 2.1 },
            new[] { 1.2, 2.2 },
            new[] { 1.3, 2.3 },
        };

        var seen = new List<double[]>();
        // ★ 收满四片就**中止**：本条验的是接线，而四次回调发生在第一次昂贵求解**之前**
        //   （Solve 里 `t.Select(makePlate).ToArray()` 先把四片都造出来，才去解场）。
        //   不中止的话整条要跑 4 分 23 秒（实测），而单测套件总共才十几秒。
        var stop = new Exception("已取到四片，接线验完即止");
        FlangePlate Mk(double[] th)
        {
            seen.Add((double[])th.Clone());
            if (seen.Count >= 4) throw stop;
            return new FlangePlate
            {
                DiscRadiusMm = 30, HoleRadiusMm = 26,
                TabEndXMm = -140, TabEndHalfWidthMm = 30, TabParallel = true,
                ThicknessMm = th[^1], ThickenedMm = th[^1], TabThicknessMm = th[^1],
                DiscStepRadiiMm = new[] { 28.0 },
                DiscStepThicknessMm = new[] { th[0] },
            };
        }

        var p = new DesignInputs();
        var lc = new LineCase
        {
            Base = p, WallMm = 0.8, UseMeasuredCurrent = false, CheckRamp = false,
            SetpointC = new[] { 1150.0, 1080.0, 1050.0 },
        };

        // 只跑一轮外层、内层轮数压到最小 —— 本条验的是**接线**，不是收敛
        try
        {
            FlangeAutoSizer.SolveByLevel(lc, levels,
                new FlangeAutoSizer.Options { MaxIterations = 1 },
                null, default, outerRounds: 1, levelLocked: null, makePlateByLevel: Mk);
        }
        catch (Exception ex) when (ReferenceEquals(ex, stop)) { /* 预期的中止 */ }

        Assert.True(seen.Count >= 4, $"回调只被调了 {seen.Count} 次，凑不出四片");

        // 头四次调用应当依次对应四片：第 j 片的第 m 级 ∝ levels[j][m]
        for (int j = 0; j < 4; j++)
        {
            double r0 = seen[j][0] / levels[j][0];
            double r1 = seen[j][1] / levels[j][1];
            Assert.True(Math.Abs(r0 - r1) < 1e-9,
                $"第 {j + 1} 片两级的倍率不一致（{r0:0.000000} vs {r1:0.000000}）—— 基准取错了片");
        }

        // 自证：四片拿到的向量必须**两两不同**，否则「按片取基准」这件事没被验到
        for (int a = 0; a < 4; a++)
            for (int b = a + 1; b < 4; b++)
                Assert.False(Math.Abs(seen[a][0] - seen[b][0]) < 1e-12,
                    $"第 {a + 1} 片与第 {b + 1} 片拿到同一组厚度 —— 片序号没起作用");
    }
}
