using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **舌板开孔**（2026-09-05 用户提出）。
///
/// 用户原话：「要记得 3DM 输入，要能实现舌板开孔，尺寸、孔径、厚度也都要能优化」，
/// 并在决定扩 <c>Geom.exe steps</c> 出图之后补了一句「若是这样 UI 输入也要有这功能」。
///
/// 物理上开孔 = **过流截面变小** ⇒ 该处电流密度升高、舌片电阻升高、发热升高。
/// 这正是用户几轮前定的因果链的反向：
/// 「过热 ⇒ 电流密度过大 ⇒ 要加大截面」——开孔是把截面**减小**。
///
/// ⚠ 本门**真解一次电流场**，不是查源码有没有那几行。
///   查源码只能证明「写了」，证明不了「算进去了」——
///   而本项目最贵的错正是「造好了没接线」。
/// </summary>
public class TabHoleTests
{
    /// <summary>一片规规矩矩的圆盘＋等宽舌片，用来做对照。</summary>
    private static FlangePlate Plate(params FlangePlate.TabHole[] holes) => new()
    {
        DiscRadiusMm = 30, HoleRadiusMm = 26,
        TabEndXMm = -90, TabEndHalfWidthMm = 15,
        ThicknessMm = 2.0, ThickenedMm = 2.0,
        TabThicknessMm = 2.0, TabParallel = true,
        WeldFilletLegMm = 0,
        TabHoles = holes,
    };

    [Fact]
    public void 孔里不算金属()
    {
        var p = Plate(new FlangePlate.TabHole(-50, 0, 4));
        Assert.False(p.Inside(-50, 0), "孔心仍被当成金属 —— 孔没生效");
        Assert.False(p.Inside(-50, 3), "孔内（离心 3 < 半径 4）仍被当成金属");
        Assert.True(p.Inside(-50, 8), "孔外（离心 8 > 半径 4）被误当成孔");
        // 没有孔时同一点必须是金属 —— 否则上面三条可能是别的原因造成的
        Assert.True(Plate().Inside(-50, 0));
    }

    /// <summary>
    /// ★★★ **真解电流场**：同一片舌片，开孔之后电流必须绕行 ——
    /// 孔占掉的那条路上不再有电流，而孔两侧的电流密度升高。
    /// </summary>
    [Fact]
    public void 开孔之后电流真的绕行()
    {
        const double I = 1000, rho = 1.1e-7, h = 1.0;
        var f0 = PlateCurrent2D.Solve(Plate(), I, rho, h);
        var f1 = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, 5)), I, rho, h);

        // 孔心那一格：开孔前有电流，开孔后不是金属
        int i = (int)Math.Round((-50 - f1.X0) / f1.H), j = (int)Math.Round((0 - f1.Z0) / f1.H);
        Assert.True(f0.Mask[i, j], "对照组孔心不是金属 —— 这条是空转");
        Assert.False(f1.Mask[i, j], "开孔之后孔心仍是金属 —— 孔没进电流场");

        // 孔两侧（z = ±8，仍在半宽 15 内）电流密度升高：截面变小，同样的电流挤过去
        int jSide = (int)Math.Round((8 - f1.Z0) / f1.H);
        Assert.True(f1.Jmag[i, jSide] > f0.Jmag[i, jSide] * 1.05,
            $"孔旁电流密度没有升高（{f0.Jmag[i, jSide]:0.000} → {f1.Jmag[i, jSide]:0.000} A/mm²）"
          + " —— 那说明孔只是被抠掉，电流没有真的绕行");
    }

    /// <summary>
    /// ★★★ **孔越大越挤**：孔径是可优化的量，它对电流密度必须单调 ——
    /// 不单调就不能二分，也就当不成旋钮。
    /// </summary>
    [Theory]
    [InlineData(3.0, 6.0)]
    [InlineData(6.0, 9.0)]
    public void 孔越大孔旁电流密度越高(double rSmall, double rBig)
    {
        const double I = 1000, rho = 1.1e-7, h = 1.0;
        var fs = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, rSmall)), I, rho, h);
        var fb = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, rBig)), I, rho, h);

        int i = (int)Math.Round((-50 - fs.X0) / fs.H);
        double Peak(PlateField f)
        {
            double m = 0;
            for (int j = 0; j < f.Nz; j++) if (f.Mask[i, j]) m = Math.Max(m, f.Jmag[i, j]);
            return m;
        }
        double js = Peak(fs), jb = Peak(fb);
        Assert.True(jb > js,
            $"孔从 R{rSmall} 放大到 R{rBig}，孔旁峰值电流密度没升（{js:0.000} → {jb:0.000} A/mm²）"
          + " —— 不单调就不能当旋钮二分");
    }

    /// <summary>★ 自证：没有孔时，一切与从前**逐位相同**（新字段不许改变旧行为）。</summary>
    [Fact]
    public void 自证_没有孔时行为不变()
    {
        var p = Plate();
        Assert.Empty(p.TabHoles);
        Assert.True(p.Inside(-50, 0));
        Assert.True(p.Inside(-89, 0));
        Assert.False(p.Inside(-91, 0));      // 舌端之外
    }
}
