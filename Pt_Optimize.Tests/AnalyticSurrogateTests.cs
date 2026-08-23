using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 解析替身的保真门。
///
/// 这道门守的是本项目最贵的那种错：优化器在**替身**上求解，而出图与判据用原图 ——
/// 「解的是 A、判的是 B」。它不报错，只会给出一组针对另一片板的厚度。
///
/// 所以这里要证明两件**方向相反**的事，缺一不可：
///   · 同一片板，量出来必须≈0（否则这把尺子只会说「不一样」，等于没有尺子）
///   · 挖掉料的板，量出来必须显著≠0（否则它只会说「一样」，等于没有门）
/// 只验其中一个方向，是本项目反复栽过的「恒真的断言」。
/// </summary>
public class AnalyticSurrogateTests
{
    /// <summary>三级阶梯 + 等宽舌，接近 Pt_Heater3 的量级，但**没有槽**</summary>
    private static FlangePlate Stepped(bool tabParallel = true) => new()
    {
        HoleRadiusMm = 26.0,
        DiscRadiusMm = 60.0,
        TabEndXMm = -200.0,
        TabEndHalfWidthMm = 40.0,
        TabParallel = tabParallel,
        ThicknessMm = 3.0,
        ThickenedMm = 3.0,
        TabThicknessMm = 3.0,
        DiscStepRadiiMm = new[] { 36.0, 46.0 },
        DiscStepThicknessMm = new[] { 1.0, 2.0 },
        WeldFilletLegMm = 0.0,
    };

    /// <summary>
    /// 闭环：解析板 → 厚度场 → 分析器反推 → 造替身 → 量。同一片板，残差该≈0。
    ///
    /// 这一条同时验了三样东西：分析器反推得对、级→阶梯的映射没错位、量法本身没坏。
    /// 其中「级→阶梯映射」最值得单验 —— 映射错了不会抛异常，
    /// 只会造出一片阶梯半径挪了位的板，而它看起来完全正常。
    /// </summary>
    [Fact]
    public void RoundTrip_SamePlate_MeasuresNearZero()
    {
        var g = Stepped();
        var field = AnalyticSurrogate.Rasterize(g);
        var sh = PlateShapeAnalyzer.Analyze(field);

        Assert.False(sh.Slot.Found, "这片板本来就没开槽，分析器却报出了槽 ⇒ 反推坏了");
        Assert.Equal(3, sh.Levels.Count);

        var lv = sh.Levels.Select(x => x.ThicknessMm).ToArray();
        var (_, fid, _) = AnalyticSurrogate.BestFit(sh, field, lv);

        Assert.Empty(fid.Blockers);
        // 栅格化步长 0.5 mm、网格 2/11 mm ⇒ 离散误差量级百分之几，取 5 % 作上限。
        // 这个数不是「够好就行」，是「同一片板量出来必须落在离散噪声里」。
        Assert.True(fid.Worst < 0.05,
            $"同一片板量出 {fid.Worst * 100:0.0} % 的差 —— 量法坏了。{fid.Report()}");
    }

    /// <summary>
    /// 反方向：把料挖掉一块，门必须响。
    ///
    /// 直接在厚度场上抠出四个扇形槽（就是 Pt_Heater3 的形态），
    /// 再走同一条闭环 —— 替身是实心的，量出来必须显著不同。
    /// 少了这一条，上面那条即使在 `Worst` 恒为 0 的坏实现下也照样通过。
    /// </summary>
    [Fact]
    public void SlottedPlate_IsCaught_NotSilentlyAccepted()
    {
        var g = Stepped();
        var field = AnalyticSurrogate.Rasterize(g);

        // 四个 45° 扇形槽，R31–R36 —— 与 Pt_Heater3 的槽同位置同量级
        for (int i = 0; i < field.Nx; i++)
        {
            double x = field.X0 + i * field.Step;
            for (int j = 0; j < field.Nz; j++)
            {
                double z = field.Z0 + j * field.Step;
                double r = System.Math.Sqrt(x * x + z * z);
                if (r < 31.0 || r > 36.0) continue;
                double deg = System.Math.Atan2(z, x) * 180.0 / System.Math.PI;
                double m = ((deg % 90) + 90) % 90;          // 每 90° 一个槽
                if (m < 45.0) field.T[i * field.Nz + j] = 0.0;
            }
        }

        var sh = PlateShapeAnalyzer.Analyze(field);
        Assert.True(sh.Slot.Found, "板上挖了四个槽，分析器一个都没认出来 ⇒ 门根本不会响");

        var lv = sh.Levels.Select(x => x.ThicknessMm).ToArray();
        var (_, fid, _) = AnalyticSurrogate.BestFit(sh, field, lv);

        Assert.NotEmpty(fid.Blockers);
        // 结构性差异之外，电学量也必须真的变了 —— 否则「有槽」只是个标签，
        // 而标签是会被人绕过去的，量出来的数不会。
        Assert.True(fid.Worst > 0.05,
            $"挖掉四个槽之后只差 {fid.Worst * 100:0.0} % —— 这把尺子看不见槽。{fid.Report()}");
    }

    /// <summary>
    /// 级数对不上要**抛**，不许猜。
    /// 猜一个不会报错，只会算出另一片板的数 —— 那正是本项目最怕的失效形态。
    /// </summary>
    [Fact]
    public void LevelCountMismatch_Throws()
    {
        var g = Stepped();
        var sh = PlateShapeAnalyzer.Analyze(AnalyticSurrogate.Rasterize(g));
        Assert.Throws<System.ArgumentException>(
            () => AnalyticSurrogate.Build(sh, new[] { 1.0, 2.0 }, tabParallel: true));
    }
}
