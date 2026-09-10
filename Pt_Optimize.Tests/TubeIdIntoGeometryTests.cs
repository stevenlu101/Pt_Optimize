using System;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R30（2026-09-10，用户要看大管径对比才暴露）：管内径此前在几何里写死 25.0（Ø50），改内径只有求解器跟着走 ⇒ BuildCase 拒算。
/// 现在几何只有一个来源：<see cref="DesignSpec.TubeIdMm"/> —— 管孔半径 = 内径/2 + 管壁，整线的管内径也从它取。
/// </summary>
public class TubeIdIntoGeometryTests
{
    [Fact]
    public void 管孔半径跟着设计的内径走_默认仍是Ø50()
    {
        var d = DesignSpec.Builtin[0].Clone();
        Assert.Equal(50.0, d.TubeIdMm);
        Assert.Equal(d.WallMm + 25.0, d.HoleRadiusMm, 9);          // 与从前逐位相同
        d.TubeIdMm = 80;
        Assert.Equal(d.WallMm + 40.0, d.HoleRadiusMm, 9);
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        Assert.Equal(d.WallMm + 40.0, g.HoleRadiusMm, 9);          // 法兰几何用的就是它
    }

    [Fact]
    public void 整线的管内径从设计取_不再因参数表不同而拒算()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TubeIdMm = 80; d.DiscRadiusMm = 48; d.TabHalfWidthMm = 48;
        var p = new DesignInputs { TubeIdMm = 80 };
        var lc = d.BuildCase(p, checkRamp: false);
        Assert.Equal(80.0, lc.TubeIdMm, 9);
        Assert.Equal(d.HoleRadiusMm, lc.FlangePlates[0].HoleRadiusMm, 9);
        // 参数表写 50、设计写 80 ⇒ 以设计为准（几何只有一个来源），不抛
        var lc2 = d.BuildCase(new DesignInputs { TubeIdMm = 50 }, checkRamp: false);
        Assert.Equal(80.0, lc2.TubeIdMm, 9);
    }
}
