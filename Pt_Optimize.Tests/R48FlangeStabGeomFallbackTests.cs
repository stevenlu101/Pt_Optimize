using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 2026-09-25：整片热稳定的夹持导度「稳态场标定不出 ⇒ 舌片几何回退」
/// （<see cref="DesignInputs.FlangeStabGeomClampFallback"/>；判定 <see cref="LineRunner.GeomClampFallback"/>；只在决 103 口径下）。
/// 起因：端到端 W08（deliverable/R48_L_端到端_细网格_W08_本次开跑于2026-09-24_224007.txt）升温期 300 °C 点（夹头 450 °C，比法兰热）
/// 整片热稳定判不了 ⇒ ① 升温全程整段判不了 ⇒ 不可交付。物理：铜排通道的导度是舌片几何的性质，与热流方向无关；场标定只是它的一种估计，
/// 在铜排比法兰热的点上「热 ÷ 温差」失去意义；几何导度比场标定值偏小 ⇒ 裕度偏保守。
/// 门 a（快，合成标定对象）：四条回退条件逐条翻转各自不回退；全满足才回退；原因句带两个温度。
/// 门 b（快）：FlangeStability.Check 不给场标定值 ⇒ 夹持项 = 几何导度、ClampFromField = false、裕度有限且逐位 = MarginGeomClamp。
/// 门 c（慢，真解，只印不判 J）：W08 现役设计升温期只跑 300 °C 一点（RampSweepOptions.TrajectoryC = {300}，导航网格）：
///   决 103 + 开关开 ⇒ 该点整片热稳定有数、StabChecked；开关关 ⇒ 该点整片 NaN（与改前同）。证据档带开跑时刻。
/// 不覆盖：其余 7 个设定点；稳态工况（W08 稳态法兰远比夹头热，标定本来就成，不触发）；W06；Windows。
/// </summary>
public class R48FlangeStabGeomFallbackTests
{
    private readonly ITestOutputHelper _o;
    public R48FlangeStabGeomFallbackTests(ITestOutputHelper o) { _o = o; }

    private static RampTwoNode.NodeCalibration Calib(double tNode, double tClamp, bool clampOk = false,
                                                     ShellThermal.ClampBoundary mode = ShellThermal.ClampBoundary.FixedTemp, int cells = 100)
        => new RampTwoNode.NodeCalibration { TNodeC = tNode, TClampC = tClamp, ClampOk = clampOk, Mode = mode, NodeCells = cells };

    [Fact]
    public void 门a_回退条件_逐条翻转_全满足才回退()
    {
        var k = Calib(380, 450);   // 法兰均温比夹持低 70 K：场标定不出、温差反向
        var (f, why) = LineRunner.GeomClampFallback(k, CriteriaRuleSet.决103, true);
        Assert.True(f, why);
        Assert.Contains("380.0", why); Assert.Contains("450.0", why); Assert.Contains("几何导度", why);
        Assert.False(LineRunner.GeomClampFallback(k, CriteriaRuleSet.决103前, true).Fallback, "口径改回 ⇒ 不回退");
        Assert.False(LineRunner.GeomClampFallback(k, CriteriaRuleSet.决103, false).Fallback, "开关关 ⇒ 不回退");
        Assert.False(LineRunner.GeomClampFallback(Calib(380, 450, clampOk: true), CriteriaRuleSet.决103, true).Fallback, "标定成了 ⇒ 不回退");
        Assert.False(LineRunner.GeomClampFallback(Calib(380, 450, mode: ShellThermal.ClampBoundary.Free), CriteriaRuleSet.决103, true).Fallback, "自由端 ⇒ 不回退");
        Assert.False(LineRunner.GeomClampFallback(Calib(380, 450, cells: 0), CriteriaRuleSet.决103, true).Fallback, "没有场 ⇒ 不回退");
        Assert.False(LineRunner.GeomClampFallback(Calib(600, 450), CriteriaRuleSet.决103, true).Fallback, "温差够（150 K）而标定没成 = 其他原因 ⇒ 不回退");
        Assert.True(LineRunner.GeomClampFallback(Calib(450.5, 450), CriteriaRuleSet.决103, true).Fallback, "只差 0.5 K < CalibMinDeltaK ⇒ 回退");
        Assert.False(LineRunner.GeomClampFallback(null, CriteriaRuleSet.决103, true).Fallback);
        Assert.Equal(1.0, RampTwoNode.CalibMinDeltaK);   // 门槛没挪
    }

    [Fact]
    public void 门b_不给场标定值_夹持项等于几何导度_裕度有限()
    {
        var p = new DesignInputs();
        var r = FlangeStability.Check(p, qGenW: 40.0, tPlateC: 400.0,
                                      areaInsulMm2: 2000.0, areaBareMm2: 6000.0, insulThickMm: 0.0,
                                      tabSectionMm2: 2 * 30.0 * 1.26, tabLenMm: 140.0,
                                      holeSectionMm2: 2 * Math.PI * 25.8 * 1.26, discSpanMm: 30.0 - 25.8,
                                      tabInsulThickMm: 0.3, clampConductanceWPerK: null);
        _o.WriteLine(r.Note);
        Assert.False(r.ClampFromField);
        Assert.False(r.Undetermined, r.Note);
        Assert.Equal(r.DClampGeomDT, r.DClampDT);
        Assert.True(double.IsFinite(r.Margin));
        Assert.Equal(r.MarginGeomClamp, r.Margin);
        var rf = FlangeStability.Check(p, qGenW: 40.0, tPlateC: 400.0, 2000.0, 6000.0, 0.0, 2 * 30.0 * 1.26, 140.0, 2 * Math.PI * 25.8 * 1.26, 30.0 - 25.8, 0.3, clampConductanceWPerK: double.NaN);
        Assert.True(rf.Undetermined, "给 NaN 仍判不了（FlangeStability 本身不变，回退在调用方）");
    }

    [Fact]
    [Trait("速度", "慢")]
    public void 门c_真解_W08升温期300度一点_决103有数_改回NaN()
    {
        var seed = R48NMeshGateTests.Design("W08");
        string path = DeliverableOut.Stamped("R48_夹持导度几何回退_W08_升温期300度点.txt");
        var sb = new StringBuilder();
        sb.AppendLine("# 整片热稳定 夹持导度几何回退　W08 升温期 300 °C 一点（Linux、待 Windows 重录；只印不判 J）");
        sb.AppendLine($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {Environment.OSVersion}　树 {HandoverDoc.Root()}");
        sb.AppendLine("种子 = R48NMeshGateTests.Design(\"W08\")（现役设计与旋钮）；工艺参数 new DesignInputs()；网格 = 整线算例缺省（导航）；RampSweepOptions.TrajectoryC = {300}");
        var opt = new RampSweepOptions { TrajectoryC = new[] { 300.0 } };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var on = RampSweep.Run(seed.Clone(), new DesignInputs { FlangeStabGeomClampFallback = true }, opt);
        double tOn = sw.Elapsed.TotalSeconds; sw.Restart();
        var off = RampSweep.Run(seed.Clone(), new DesignInputs { FlangeStabGeomClampFallback = false }, opt);
        double tOff = sw.Elapsed.TotalSeconds;
        Assert.Single(on.Points); Assert.Single(off.Points);
        var a = on.Points[0]; var b = off.Points[0];
        string fsNoteOn = a.LineRes?.Checks?.FirstOrDefault(c => c.Name == LineResult.Key.FlangeStab)?.Note ?? "";
        string fsNoteOff = b.LineRes?.Checks?.FirstOrDefault(c => c.Name == LineResult.Key.FlangeStab)?.Note ?? "";
        sb.AppendLine($"开关开：设定 {a.SetpointC:0} °C 夹头 {a.ClampC:0} °C　场有效 {a.FieldValid}　判了热稳定 {a.StabChecked}　整片 {a.FlangeStabMargin:0.###}（{a.FlangeStabWhere}）　局部 {a.LocalStabMargin:0.###}（{a.LocalStabWhere}）　热稳定判不了 {a.StabUndetermined}　管J(设计) {a.TubeJDesignAPerMm2:0.###}/{a.TubeJDesignLimit:0.#}　耗时 {tOn:0} s");
        sb.AppendLine("　整片热稳定附注：" + fsNoteOn);
        sb.AppendLine($"开关关：设定 {b.SetpointC:0} °C 夹头 {b.ClampC:0} °C　场有效 {b.FieldValid}　判了热稳定 {b.StabChecked}　整片 {b.FlangeStabMargin:0.###}　局部 {b.LocalStabMargin:0.###}　热稳定判不了 {b.StabUndetermined}　耗时 {tOff:0} s");
        sb.AppendLine("　整片热稳定附注：" + fsNoteOff);
        sb.AppendLine($"两跑局部热稳定逐位相同：{a.LocalStabMargin.Equals(b.LocalStabMargin)}（回退只动整片那一项的夹持导度）");
        sb.AppendLine($"结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(path, sb.ToString());
        _o.WriteLine(sb.ToString());
        Assert.True(a.FieldValid, a.FieldInvalidReason);
        Assert.True(a.StabChecked);
        Assert.True(double.IsFinite(a.FlangeStabMargin), "决103 + 开关开：300 °C 点整片热稳定应有数");
        Assert.Contains("几何导度", fsNoteOn);
        Assert.True(double.IsNaN(b.FlangeStabMargin), "开关关：应与改前一样判不了");
        Assert.Equal(a.LocalStabMargin, b.LocalStabMargin);
    }
}
