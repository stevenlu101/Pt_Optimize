using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>★ R46（2026-09-12，用户：「APP添加一个呈现配套清单与系统安装报告」）：安装报告十节齐全、没过时首行说不可作为安装依据、导出转管道表。</summary>
public class InstallReportTests
{
    private static (LineResult r, DesignSpec d, DesignInputs p) Sample(bool allOk)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        var p = new DesignInputs();
        var r = new LineResult
        {
            Ok = true, Converged = true, TubeMassG = 1800, FlangeMassG = 900, TotalMassG = 2700,
            Segments = new[]
            {
                new SegmentOut { Name = "HC1", SetpointC = 1150, CurrentA = 979, PowerW = 5200, TubeJAPerMm2 = 7.7 },
                new SegmentOut { Name = "HC2", SetpointC = 1080, CurrentA = 979, PowerW = 4100, TubeJAPerMm2 = 7.7 },
            },
            Flanges = new[]
            {
                new FlangeOut { Name = "入口", CurrentA = 979, BusSectionForCurrentMm2 = 489.5, QClampW = 30, TTabEndC = 412, MassG = 300 },
                new FlangeOut { Name = "HC1|HC2", CurrentA = 1696, BusSectionForCurrentMm2 = 848, QClampW = 80, TTabEndC = 455, MassG = 500 },
                new FlangeOut { Name = "出口", CurrentA = 979, BusSectionForCurrentMm2 = 489.5, QClampW = 25, TTabEndC = 400, MassG = 300 },
            },
            Checks = new[]
            {
                new ConstraintOut { Name = "· 升温到位用时（集总）", Actual = 0.06, Limit = 72, Unit = "h", Ok = true, Kind = CheckKind.Reference },
                new ConstraintOut { Name = LineResult.Key.FlangeDip, Actual = allOk ? 9.7 : 12.3, Limit = 10, Unit = "K", Ok = allOk, Kind = CheckKind.Target, Where = "HC2" },
            },
        };
        return (r, d, p);
    }

    [Fact]
    public void 十节齐全_有配套清单与逐片表()
    {
        var (r, d, p) = Sample(true);
        string t = InstallReport.Build(r, d, p, "判据已加密复算到数不再变", new DateTime(2026, 9, 12, 10, 0, 0));
        foreach (string h in new[] { "1. 整线概要", "2. 供电", "3. 法兰逐片", "4. 配套清单", "5. 保温", "6. 焊接与加工", "7. 升温与运行", "8. 判据表", "9. 出图与文件", "10. 待现场确认" })
            Assert.Contains(h, t);
        Assert.Contains("全判据通过", t);
        Assert.Contains("HC1|HC2", t);
        Assert.Contains("铜排规格", t);
        Assert.Contains("2026-09-12", t);
        foreach (var code in new[] { "②′", "②″" }) Assert.DoesNotContain(code, t);
    }

    [Fact]
    public void 判据没过时首行说不可作为安装依据()
    {
        var (r, d, p) = Sample(false);
        string t = InstallReport.Build(r, d, p);
        string first = t.Split('\n')[1];
        Assert.Contains("不可作为安装依据", first);
        Assert.Contains("1 条判据没过", first);
    }

    [Fact]
    public void 导出时制表位表转成管道表()
    {
        var (r, d, p) = Sample(true);
        string md = InstallReport.ToMarkdown(InstallReport.Build(r, d, p));
        Assert.Contains("| 段 | 控温点 °C |", md);
        Assert.Contains("|---|---|", md);
        Assert.DoesNotContain("\t", md);
        Assert.Contains("| 片 |", md);
    }
}
