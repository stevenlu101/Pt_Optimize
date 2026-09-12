using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R46（2026-09-12，用户：「APP有保温与铜排的计算结果吗? 计算出的法兰要含有配套答案(如果没有，请补齐)」）：
/// 配套清单的口径钉在这里——铜排截面取载流／导热两者之大、厚度向上取半毫米、铜排电流密度不超许用、
/// 舌保温覆盖 = 圆盘切点到压接段前、文字表每行制表位数一致、不带判据代号。
/// </summary>
public class FlangeKitTests
{
    private static (LineResult r, DesignSpec d, DesignInputs p) Sample()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        var p = new DesignInputs { BusbarJAllowAPerMm2 = 2.0, BusbarClampTempC = -1 };
        var r = new LineResult
        {
            Ok = true, Converged = true,
            Flanges = new[]
            {
                new FlangeOut { Name = "入口", CurrentA = 979, BusSectionForCurrentMm2 = 489.5, BusSectionForHeatMm2 = 120, QClampW = 30, TTabEndC = 412, BusGWPerK = 1.2, MassG = 300 },
                new FlangeOut { Name = "HC1|HC2", CurrentA = 1696, BusSectionForCurrentMm2 = 848, BusSectionForHeatMm2 = 1100, QClampW = 80, TTabEndC = 455, BusGWPerK = 2.5, MassG = 500 },
                new FlangeOut { Name = "出口", CurrentA = 979, BusSectionForCurrentMm2 = 489.5, BusSectionForHeatMm2 = 90, QClampW = 25, TTabEndC = 400, BusGWPerK = 1.2, MassG = 300 },
            },
        };
        return (r, d, p);
    }

    [Fact]
    public void 铜排截面取载流与导热之大_厚度向上取半毫米_电流密度不超许用()
    {
        var (r, d, p) = Sample();
        var rows = FlangeKit.Build(r, d, p);
        Assert.Equal(3, rows.Count);
        Assert.Equal(489.5, rows[0].SecReqMm2, 6);          // 载流大
        Assert.Equal(1100, rows[1].SecReqMm2, 6);           // 导热大 ⇒ 按导热
        Assert.Contains("按导热选", rows[1].Note);
        double width = 2 * d.TabHalfWidthMm;
        Assert.Equal(width, rows[1].BusWidthMm, 6);
        Assert.Equal(FlangeKit.RoundThickUp(1100 / width), rows[1].BusThickMm, 6);
        Assert.Equal(0, (rows[1].BusThickMm * 2) % 1, 9);   // 半毫米格
        foreach (var k in rows) Assert.True(k.JCuAPerMm2 <= p.BusbarJAllowAPerMm2 + 1e-9, $"{k.Name} J_cu {k.JCuAPerMm2}");
        Assert.False(rows[0].ClampTempIsInput);
        Assert.Equal(412, rows[0].ClampTempC, 6);           // 夹持温度由铜排热导那条边界算出 ⇒ 用 TTabEndC
    }

    [Fact]
    public void 舌保温覆盖是切点到压接段前()
    {
        var (r, d, p) = Sample();
        var k = FlangeKit.Build(r, d, p)[0];
        Assert.Equal(d.TangentXMm(), k.InsulFromXMm, 6);
        Assert.Equal(-d.TabLengthMm + d.ClampLengthMm, k.InsulToXMm, 6);
        Assert.Equal(k.InsulFromXMm - k.InsulToXMm, k.InsulLenMm, 6);
        Assert.True(k.InsulLenMm > 50, $"覆盖长 {k.InsulLenMm}");
        Assert.Equal(Math.Max(d.TabThickMm[0], d.WallMm), k.WeldLegMm, 6);
    }

    [Fact]
    public void 文字表每行制表位数一致_不带判据代号()
    {
        var (r, d, p) = Sample();
        string txt = FlangeKit.Text(FlangeKit.Build(r, d, p), d, p);
        var table = txt.Split('\n').Where(l => l.Contains('\t')).ToArray();
        Assert.Equal(4, table.Length);   // 表头 + 3 片
        int tabs = table[0].Count(c => c == '\t');
        foreach (var l in table) Assert.Equal(tabs, l.Count(c => c == '\t'));
        Assert.Contains("配套清单", txt);
        Assert.Contains("圆盘", txt);
        foreach (var code in new[] { "②′", "②″", "③ ", "⑤ ", "⑥ " }) Assert.DoesNotContain(code, txt);
    }
}
