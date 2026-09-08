using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R11（用户 2026-09-08）：「舌片厚度不是旋钮，是截面积 I/10 ÷ 舌宽」——
/// 舌片厚 = 设计电流 ÷ (J_设计 × 舌片最窄有效宽)，闭式；与圆盘基板、各级台阶解耦。
/// 本档钉住 <see cref="DesignSpec.SizeTongues"/> 的算术（用 <see cref="DesignCurrent"/> 独立复算），
/// 以及「进模型的就是算出来的那个数」（<see cref="DesignSpec.Plate"/>）。
/// </summary>
public class DesignSpecTongueTests
{
    private static (DesignSpec D, DesignInputs P) W08TwoSeg()
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };      // 用户 09-08：验算跑 2 段（3 片）
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        return (d, p);
    }

    [Fact]
    public void 舌片厚等于设计电流除以J乘舌宽_逐片()
    {
        var (d, p) = W08TwoSeg();
        var dc = DesignCurrent.ForLine(d, p);
        d.SizeTongues(p);
        Assert.Equal(d.FlangeCount, d.TongueThickMm.Length);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            double w = 2 * d.TabHalfWidthMm;                                  // 等宽舌、无孔 ⇒ 最窄有效宽 = 舌宽
            double want = Math.Max(dc.PlateA[j] / (10.0 * w), p.WeldMinThicknessMm);
            want = Math.Ceiling(want / 0.01 - 1e-9) * 0.01;
            Assert.Equal(want, d.TongueThickMm[j], 9);
            Assert.Equal(d.TongueThickMm[j], d.Plate(j, 0).TabThicknessMm, 9);   // 进模型的就是它
            Console.WriteLine($"片{j}：I={dc.PlateA[j]:0} A　舌宽 {w:0}　舌片厚 {d.TongueThickMm[j]:0.00} mm");
        }
        Assert.True(d.TongueThickMm[1] > d.TongueThickMm[0], "共用片电流更大 ⇒ 舌片更厚");
    }

    [Fact]
    public void 舌片厚不随圆盘板厚变_随舌宽变()
    {
        var (d, p) = W08TwoSeg();
        d.SizeTongues(p);
        var a = (double[])d.TongueThickMm.Clone();
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] *= 2;
        d.SizeTongues(p);
        for (int j = 0; j < a.Length; j++) Assert.Equal(a[j], d.TongueThickMm[j], 9);   // 圆盘加厚 ⇒ 舌片不动
        d.TabHalfWidthMm /= 2;                                                           // 舌宽减半
        d.SizeTongues(p);
        for (int j = 0; j < a.Length; j++)
            Assert.InRange(d.TongueThickMm[j], 2 * a[j] - 0.011, 2 * a[j] + 0.011);     // ⇒ 舌片厚加倍（图纸格误差内）
    }

    [Fact]
    public void 舌片截面按自己的厚度算_终验J不超10()
    {
        var (d, p) = W08TwoSeg();
        var dc = DesignCurrent.ForLine(d, p);
        d.SizeTongues(p);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            var g = d.Plate(j, 0);
            var tabCuts = SectionSizing.Cuts(g, dc.PlateA[j], d.ClampLengthMm).Where(c => c.OnTab).ToList();
            Assert.NotEmpty(tabCuts);
            Assert.All(tabCuts, c => Assert.True(c.JAPerMm2 <= 10.0 + 1e-9, $"片{j} {c.Where} J={c.JAPerMm2:0.00}"));
            // 基板另有自己的下界：与舌片不再是同一个数（舌片 2 mm 级，基板可以薄到烧穿下界）
            double floor = SectionSizing.PlateThickFloorMm(g, g.ThicknessMm, dc.PlateA[j], d.ClampLengthMm);
            Console.WriteLine($"片{j}：舌片厚 {d.TongueThickMm[j]:0.00}　基板 {g.ThicknessMm:0.00} ⇒ 圆盘侧 J 下界 {floor:0.00} mm");
        }
    }

    [Fact]
    public void 存档往返_舌片厚逐位保住()
    {
        var (d, p) = W08TwoSeg();
        d.SizeTongues(p);
        d.Name = "★舌片厚往返★ " + Guid.NewGuid().ToString("N")[..6];
        string? w = null;
        try
        {
            w = DesignSpecStore.Save(d);
            var back = DesignSpecStoreTests.Parse(System.IO.File.ReadAllText(w));   // 走读取端那条路（与存档往返测试同一个入口）
            Assert.NotNull(back);
            for (int j = 0; j < d.TongueThickMm.Length; j++) Assert.Equal(d.TongueThickMm[j], back.TongueThickMm[j], 9);
        }
        finally { if (w is not null && System.IO.File.Exists(w)) System.IO.File.Delete(w); }
    }
}
