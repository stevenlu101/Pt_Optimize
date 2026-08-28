using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 圆盘焊接下界，以及「印出来的板厚 ≠ 进模型的板厚」这个坑。
///
/// ★ 病灶（2026-08-25 查出）：`--cli --window` 那张表印的是 **基准 × 标度** 的**夹前**值，
///   而模型真正用的是 <see cref="DesignSpec.Plate"/> 里的 max(板厚, 下界)。
///   默认标度表 {0.3, 0.5, 0.7, 1.0, 1.4, 2.0} 配 W08 的基准 {0.89, 2.45, 2.35, 0.73}，
///   在 R30（下界 0.6）上：出口片 0.30/0.50/0.70 三行算出 0.22/0.37/0.51，**全被夹成 0.60**
///   —— 表上看着三个不同的设计，其实是同一个几何，而整行的 ②′/③/②″/管J/合计g
///   都是按夹后那个几何算的。表头却写着「只测不调」，等于向读者担保这一列就是用的那个数。
///
/// ⇒ 修法不是在打印处补一句 max（那是「同一个式子两处来源」），
///   而是让打印**走 Plate() 这个唯一来源**，被夹的标 *，并在表头申报下界。
///   本文件钉住这两件事：下界怎么随盘径长，以及 Plate 确实在夹。
/// </summary>
public class DiscFloorTests
{
    private static DesignSpec At(double discR)
    {
        var d = DesignSpec.W08.Clone();
        d.DiscRadiusMm = discR;
        return d;
    }

    [Fact]
    public void 小盘由烧穿下界主导_等于工艺硬底()
    {
        var p = new DesignInputs();
        double floor = At(30.0).DiscFloorMm(p);
        Assert.Equal(p.WeldMinThicknessMm, floor, 3);   // 0.6 mm，与尺寸无关
    }

    [Fact]
    public void 大盘由抗屈曲主导_下界远高于烧穿底()
    {
        var p = new DesignInputs();
        double floor = At(60.0).DiscFloorMm(p);
        // Ø120 实测 4.71 mm —— 这就是 --shape 跑 R60 时板厚被顶到 4.71 的原因
        Assert.True(floor > 4.0, $"盘R60 的下界应由屈曲主导，实际 {floor:0.00}");
        Assert.True(floor > p.WeldMinThicknessMm * 5, "屈曲下界应远高于烧穿底");
    }

    [Fact]
    public void 下界随盘径单调增_盘越大越要厚板()
    {
        var p = new DesignInputs();
        double f30 = At(30.0).DiscFloorMm(p), f45 = At(45.0).DiscFloorMm(p), f60 = At(60.0).DiscFloorMm(p);
        Assert.True(f30 <= f45 && f45 < f60, $"应单调不减：{f30:0.00} / {f45:0.00} / {f60:0.00}");
    }

    [Fact]
    public void 自证_默认标度确实会把端片压到下界以下()
    {
        // 没有这一条，下面「Plate 会夹」那条就是空转 —— 断言必须先证明坏情况真会出现。
        var p = new DesignInputs();
        var d = At(30.0);
        double floor = d.DiscFloorMm(p);
        double raw = DesignSpec.W08.TabThickMm[3] * 0.3;      // 出口片 × 最小标度
        Assert.True(raw < floor, $"标度 0.3 的出口片应低于下界：{raw:0.000} vs {floor:0.00}");
    }

    [Fact]
    public void Plate把低于下界的板厚夹上去_这就是模型真正用的值()
    {
        var p = new DesignInputs();
        var d = At(30.0);
        double floor = d.DiscFloorMm(p);
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = DesignSpec.W08.TabThickMm[j] * 0.3;

        double used = d.Plate(3, floor).ThicknessMm;
        Assert.Equal(floor, used, 6);                           // 夹后 = 下界
        Assert.True(used > d.TabThickMm[3] + 1e-9,              // 且确实与「印出来的」不同
            "夹后值必须与夹前值不同，否则这个坑根本不存在");
    }

    [Fact]
    public void 高于下界的板厚不被动_夹不是无条件的()
    {
        // 自证的另一半：若 Plate 无条件返回下界，上一条也会通过。
        var p = new DesignInputs();
        var d = At(30.0);
        double floor = d.DiscFloorMm(p);
        d.TabThickMm[1] = 2.45;
        Assert.Equal(2.45, d.Plate(1, floor).ThicknessMm, 6);
    }
}
