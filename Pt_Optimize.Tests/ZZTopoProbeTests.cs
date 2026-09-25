using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;
namespace PtOptimize.Tests;
public class ZZTopoProbeTests
{
    private readonly ITestOutputHelper _o;
    public ZZTopoProbeTests(ITestOutputHelper o) => _o = o;
    [Theory]
    [InlineData(0, 1.0, 1.0, 1.0)]
    [InlineData(1, 3.0, 3.0, 1.0)]
    [InlineData(2, 3.0, 3.0, 0.5)]
    [InlineData(3, 2.0, 2.0, 0.5)]
    [InlineData(4, 1.0, 1.0, 0.5)]
    public void 探(int k, double tabMul, double tongueMul, double iMul)
    {
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        _o.WriteLine($"原：板厚 {string.Join("/", d.TabThickMm)} 舌片厚 {string.Join("/", d.TongueThickMm)} 盘R {d.DiscRadiusMm} 舌半宽 {d.TabHalfWidthMm} 舌长 {d.TabLengthMm} 孔 {d.HoleRadiusMm} 压接 {d.ClampLengthMm} 舌保温 {string.Join("/", d.TabInsulMm)}");
        d.TabThickMm = d.TabThickMm.Select(x => x * tabMul).ToArray();
        d.TongueThickMm = d.TongueThickMm.Select(x => x * tongueMul).ToArray();
        var c = d.BuildCase(p, checkRamp: false);
        c.UseMeasuredCurrent = true; c.MeasuredCurrentA = new[] { 1200.0 * iMul, 1150.0 * iMul }; c.CoupleMaxRounds = 2;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(c);
        _o.WriteLine($"变体 {k}：{sw.Elapsed.TotalSeconds:0} s Ok {r.Ok} 铂重 {r.TotalMassG:0.0} 阻断 [{string.Join("；", r.HardBlocked.Select(x => $"{x.Name} {x.Actual:0.###}/{x.Limit:0.###}"))}]");
        foreach (var ck in r.Checks) _o.WriteLine($"  {ck.Name}\t{ck.Actual:0.###}\t{ck.Limit:0.###}\t{ck.Kind}\t{(ck.Undetermined ? "判不了" : ck.Ok ? "过" : "不过")}\t{ck.Where}");
    }
}
