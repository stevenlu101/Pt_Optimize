using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48 G1（2026-09-15，Opus 5 写；依据常驻数值把关人第十四轮「其余散热表超界检测」）：法兰散热表那次（ShellThermal.LossTableHiC）之外，
/// 其余四处散热表逐个加「查表用到的温度 vs 表区间」检测，每处一条快门（构造一个会超界的输入，并有不超界的对照）。
///
/// ══ 四处表与它们的上限（逐个查过源码）
///   · SegmentSolver 管段表：上限 设定（带玻璃时与玻璃进口取大）+ 400 K；解出的管最高温超过它 ⇒ SolveResult.LossTableExceeded、Note 明写；
///     整线里 LineRunner 把吃管温的判据一律判不了（不吃管温的：舌片自由段、圆盘盖得住管孔、法兰截面 J、① 升温；实测电流模式下另加管 J）。
///     构造：空管，按 1150 °C 反算的电流、控温点却填 600 °C（表上限 1000 °C）—— 实测电流模式下控温点填低了就是这样。
///   · RampSolver 集总升温表（优先：整线「升温到位用时（集总）」参考量在用）：查表用到起点、积分最高温、参考温度（段设定）、目标温度 ⇒
///     RampResult.LossTableExceeded；整线那条参考量判不了。
///     ★ 2026-09-15 Opus 5（审查意见 minor）：表上限由「目标 + 200 K」改为「目标、参考温度、起点三者之最 + 200 K」⇒ 原构造「升温目标 900、段设定 1150」
///     是界面可填的合法输入，现在不再出表、这条参考量照常出数（对照门）；超界构造改为下限那头：参考温度 / 升温目标低于环境温度。
///     ★ 2026-09-15 Opus 5（审查意见 major）：起点与积分最高温只查上限 —— 表下限 = 环境温度，界面把环境温度填到 35 °C 时写死的起点 25 °C 低于下限，
///     原先判超界是误报（低于环境处钳住只会多扣散热、用时偏长，偏保守；推导在 RampSolver.Solve 注释）。对照门：环境 35 °C 整线，这条参考量不许判不了。
///   · RampScreen 闭式快筛表：[环境, 目标 + 300]，只在目标温度处查 ⇒ **上限按构造不会超**，能超的只有下限（目标低于环境）⇒ Point.LossTableExceeded，Judge 判不了。
///   · DesignScreen.DrawBudgetW 抽热预算表：[环境, 管温 + 300]，只在管温处取斜率 ⇒ 同上，上限不会超；管温低于环境 ⇒ 返回 NaN。
/// </summary>
public class R48LossTableRangeTests
{
    private readonly ITestOutputHelper _out;
    public R48LossTableRangeTests(ITestOutputHelper o) { _out = o; }

    private static DesignInputs EmptyTube(double tSet)
        => new() { ThroughputTPerDay = 0, HGlass = 0, TSetC = tSet, SizeWall = false };

    [Fact]
    public void 管段表_解出的管最高温超出上限要写明()
    {
        var p1150 = EmptyTube(1150);
        Assert.True(SegmentSolver.IsEmptyTube(p1150));
        var r1150 = SegmentSolver.Solve(p1150);
        Assert.True(r1150.Ok, r1150.Message);
        _out.WriteLine($"设定 1150：电流 {r1150.CurrentA:0.0} A　管最高 {r1150.TMaxC:0.0} °C　表上限 {r1150.LossTableHiC:0}　超界 {r1150.LossTableExceeded}");
        Assert.Equal(SegmentSolver.TubeLossTableHiC(p1150), r1150.LossTableHiC);
        Assert.Equal(1550.0, r1150.LossTableHiC);
        Assert.False(r1150.LossTableExceeded);                 // 对照：不超界

        var p600 = EmptyTube(600);
        var r600 = SegmentSolver.SolveAtCurrent(p600, r1150.CurrentA);
        Assert.True(r600.Ok, r600.Message);
        _out.WriteLine($"设定 600、同电流：管最高 {r600.TMaxC:0.0} °C　表上限 {r600.LossTableHiC:0}　超界 {r600.LossTableExceeded}　{r600.Note}");
        Assert.Equal(1000.0, r600.LossTableHiC);
        Assert.True(r600.TMaxC > r600.LossTableHiC, $"构造没超界：管最高 {r600.TMaxC:0.0}");
        Assert.True(r600.LossTableExceeded);
        Assert.Contains("超出管表面散热表上限", r600.Note);
        Assert.Contains(SegmentSolver.EmptyTubeNote, r600.Note);   // 空管说明还在（追加，不覆盖）
    }

    [Fact]
    public void 管段表_整线里超界则吃管温的判据判不了()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p, checkRamp: false);
        // 构造输入：空管到温稳态，按 1150 °C 反算的电流当实测电流，控温点却填 600 °C（管表上限 = 600 + 400 = 1000 °C）
        var q = SegmentSolver.Clone(lc.Base);
        q.TubeIdMm = lc.TubeIdMm; q.WallMinMm = lc.WallMm; q.TubeLengthMm = lc.SegLengthMm[0]; q.SizeWall = false;
        q.TSetC = 1150; q.ThroughputTPerDay = 0; q.HGlass = 0;
        var s1150 = SegmentSolver.Solve(q);
        Assert.True(s1150.Ok, s1150.Message);
        lc.EmptyTube = true;
        lc.UseMeasuredCurrent = true;
        lc.MeasuredCurrentA = new[] { s1150.CurrentA };
        lc.SetpointC = new[] { 600.0 };
        var r = LineRunner.Run(lc);
        // ⚠ 不断言 r.Ok：表在 1000 °C 以上钳住散热 ⇒ 发热随 ρ(T) 涨而散热不涨，管温一路冲到约 2200 °C（上面单段那条门实测），
        //   法兰跟着越过熔点、整线 Ok = false —— 这正是「超界静默钳住造出假的热失控」的样子（同一电流、真散热下管温约 1150 °C）。
        //   本门要的是：判据表照样出来，而且吃管温的全部判不了，不是一张看起来正常的表。
        _out.WriteLine($"整线 Ok {r.Ok}　熔化位 {r.OverMelt}　{r.Message}");
        Assert.NotEmpty(r.Segments);
        Assert.NotEmpty(r.Checks);
        var s = r.Segments[0];
        _out.WriteLine($"段 {s.Name}：电流 {s.CurrentA:0.0} A　管最高 {s.TubeTMaxC:0.0} °C　表上限 {s.TubeLossTableHiC:0}　超界 {s.TubeLossTableExceeded}");
        Assert.True(s.TubeLossTableExceeded);
        // ★ 2026-09-15 Opus 5（审查意见 minor）：管表超界时法兰越过熔点是钳位造出来的可能 ⇒ 熔化位不置、头条说「熔不熔判不了」，Ok 仍为 false。
        //   先确认构造真的造出了「法兰越过熔点」（否则下面几条是空转）：09-15 上一版实测出口片峰值 2199 °C。
        foreach (var fo in r.Flanges) _out.WriteLine($"  {fo.Name}：峰值 {fo.TMaxC:0} °C　熔化位 {fo.OverMelt}");
        Assert.Contains(r.Flanges, fo => fo.TMaxC > Materials.PtMeltC);
        Assert.False(r.Ok);
        Assert.False(r.OverMelt, "管表超界造出的越过熔点不许置熔化位");
        Assert.All(r.Flanges, fo => Assert.False(fo.OverMelt, fo.Name + " 不许置熔化位"));
        Assert.Contains("熔不熔判不了", r.Message);
        Assert.Contains("管表面散热表的温度上限", r.Message);
        Assert.DoesNotContain("该解不存在", r.Message);
        Assert.Contains(r.Notes, n => n.StartsWith("✗ 管温超出管表面散热表的温度上限", StringComparison.Ordinal));
        // ★ 2026-09-15 Opus 5（审查意见 minor「豁免名单自相矛盾」）：「① 升温」（闭式，与截面 J 同出 DesignCurrent.Compute）进豁免名单；
        //   本算例是实测电流模式 ⇒ 「管 J」= 实测电流 ÷ 截面，也豁免。名单按模式取生产函数，不在门里另写一份。
        Assert.True(lc.UseMeasuredCurrent);
        var independentKeys = LineRunner.IndependentOfTubeFieldFor(lc.UseMeasuredCurrent);
        Assert.Contains(LineResult.Key.Ramp, independentKeys);
        Assert.Contains(LineResult.Key.TubeJ, independentKeys);
        Assert.DoesNotContain(LineResult.Key.TubeJ, LineRunner.IndependentOfTubeFieldFor(false));   // 控温反算：电流由管温场二分出来，管 J 吃管温场
        int marked = 0;
        foreach (var ck in r.Checks)
        {
            bool independent = independentKeys.Any(k => ck.Name.StartsWith(k, StringComparison.Ordinal));
            _out.WriteLine($"  {Criteria.Plain(ck.Name)}：判不了 {ck.Undetermined}　不吃管温 {independent}");
            if (independent) { Assert.DoesNotContain("管表面散热表", ck.Note); continue; }
            marked++;
            Assert.True(ck.Undetermined, ck.Name + " 应判不了");
            Assert.False(ck.Ok, ck.Name + " 判不了不算过");
            Assert.Contains("管温超出管表面散热表的温度上限", ck.Note);
        }
        Assert.True(marked >= 8, $"只标了 {marked} 条");
        // 两条硬判据在表上、没被这一遍标掉（「① 升温」本身判不判得了由它自己的闭式量定，本门不管）
        foreach (var key in new[] { LineResult.Key.Ramp, LineResult.Key.TubeJ })
        {
            var ckI = r.Checks.Single(c => c.Name.StartsWith(key, StringComparison.Ordinal));
            Assert.DoesNotContain("管表面散热表", ckI.Note);
        }
        var tubeJ = r.Checks.Single(c => c.Name.StartsWith(LineResult.Key.TubeJ, StringComparison.Ordinal));
        Assert.False(tubeJ.Undetermined, tubeJ.Note);
        Assert.Equal(s1150.CurrentA / (Math.PI * (Math.Pow(lc.TubeIdMm * 0.5 + lc.WallMm, 2) - Math.Pow(lc.TubeIdMm * 0.5, 2))), tubeJ.Actual, 9);
        Assert.False(r.AllOk);
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（审查意见 minor：「升温目标比控温点低 200 K 以上」是界面可填的合法输入，原表上限 目标 + 200 K 让这条参考量白丢；附注用了内部参数名「参考温度」）：
    /// 表上限改为 目标、参考温度、起点三者之最 + 200 K。门槛跑前写死：
    ///   ① 合法输入（目标 900、参考温度 1150）不出表，上限 = 1150 + 200；参考温度与起点都不高于目标时上限仍 = 目标 + 200（表逐位不变的前提，直接核上限的值）；
    ///   ② 超界构造改为下限那头（参考温度 20 °C 低于环境 25 °C）⇒ 超界、结构化列表标明是参考温度；
    ///   ③ 整线：目标 900、段设定 1150 ⇒ 用时照常出数，不因散热表判不了；升温目标 20 °C（低于环境）⇒ 判不了，附注用界面叫法（升温目标）、给出该怎么办，
    ///     不出现内部参数名「参考温度」，「钳住」不重复。
    /// </summary>
    [Fact]
    public void 集总升温表_查表温度出表则直接写明且整线参考量判不了()
    {
        var p = new DesignInputs();
        // ① 合法输入：目标 900、参考温度 1150 ⇒ 上限 = 1350，不出表
        var legal = RampSolver.Solve(p, 0.8, 200, 20, 1000, tRefC: 1150, fromC: 25, targetC: 900, maxHours: 72);
        var same = RampSolver.Solve(p, 0.8, 200, 20, 1000, tRefC: 1150, fromC: 25, targetC: 1150, maxHours: 72);
        _out.WriteLine($"目标 900、参考 1150：表 {legal.LossTableLoC:0}～{legal.LossTableHiC:0}　超界 {legal.LossTableExceeded}　到位 {legal.Reached}　用时 {legal.HoursToTarget:0.#####} h");
        _out.WriteLine($"目标 1150、参考 1150：表 {same.LossTableLoC:0}～{same.LossTableHiC:0}　超界 {same.LossTableExceeded}");
        Assert.Equal(1350.0, legal.LossTableHiC);
        Assert.False(legal.LossTableExceeded, legal.LossTableNote);
        Assert.Empty(legal.LossTableOut);
        Assert.Equal(1150.0 + 200, same.LossTableHiC);             // 参考温度不高于目标 ⇒ 上限仍是 目标 + 200（原式）
        Assert.False(same.LossTableExceeded);

        // ② 超界构造：参考温度 20 °C 低于环境 25 °C（表下限）
        var bad = RampSolver.Solve(p, 0.8, 200, 20, 1000, tRefC: 20, fromC: 25, targetC: 1150, maxHours: 72);
        _out.WriteLine($"参考 20：表 {bad.LossTableLoC:0}～{bad.LossTableHiC:0}　超界 {bad.LossTableExceeded}　{bad.LossTableNote}");
        Assert.True(bad.LossTableExceeded);
        Assert.Equal(new[] { (RampTableProbe.Reference, 20.0) }, bad.LossTableOut);
        Assert.NotEqual("", bad.LossTableNote);

        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        // ③a 整线合法输入：升温目标 900、段设定 1150 ⇒ 「升温到位用时（集总）」不因散热表判不了
        {
            var lc = d.BuildCase(p, checkRamp: true);
            lc.CheckRamp = true;
            lc.RampTargetC = 900;
            var r = LineRunner.Run(lc);
            Assert.True(r.Ok, r.Message);
            var ck = r.Checks.Single(c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal));
            _out.WriteLine($"整线 目标 900：{Criteria.Plain(ck.Name)} = {ck.Actual:0.###} h　判不了 {ck.Undetermined}　{ck.Note}");
            Assert.DoesNotContain("散热表", ck.Note);
            Assert.False(ck.Undetermined, ck.Note);
        }
        // ③b 整线超界：升温目标 20 °C（低于环境 25 °C）⇒ 判不了
        {
            var lc = d.BuildCase(p, checkRamp: true);
            lc.CheckRamp = true;
            lc.RampTargetC = 20;
            var r = LineRunner.Run(lc);
            Assert.True(r.Ok, r.Message);
            var ck = r.Checks.Single(c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal));
            _out.WriteLine($"整线 目标 20：{Criteria.Plain(ck.Name)} 判不了 {ck.Undetermined}　{ck.Note}");
            Assert.True(ck.Undetermined);
            Assert.False(ck.Ok);
            string mine = ck.Note.Split("（原注：")[0];
            Assert.Contains("管表面散热表没覆盖到这些温度", mine);
            Assert.Contains("升温目标 20 °C", mine);
            Assert.Contains("请核对环境温度、升温目标与各段控温点", mine);
            Assert.DoesNotContain("参考温度", mine);
            Assert.True(mine.Split("钳").Length <= 2, "「钳住」说了不止一遍：" + mine);
        }
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（审查意见 major）：**环境温度高于写死的起点 25 °C 时，集总升温用时不许误判「无法判定」**。
    /// 表下限 = 环境温度；起点低于它只会让散热被多扣（用时偏长、偏保守，推导在 RampSolver.Solve 注释，不依赖数据）⇒ 起点与积分最高温只查上限。
    /// 界面可达：「环境温度」界面可改、整线页开着升温检查、LineCase.RampFromC = 25 写死。门槛跑前写死：判不了 = false、附注里不许出现散热表字样。
    /// 2026-09-15 Opus 5（审查意见 minor 之后）：表上限 ≥ 起点 + 200 K 按构造成立，「起点高于上限」再也造不出来，原「起点 1120、目标 900」那条对照改为
    /// 核上限随起点抬高（= 1120 + 200）且不超界；两头都查的那一类（参考温度）低于环境仍判超界。
    /// </summary>
    [Fact]
    public void 集总升温表_环境温度高于起点不算超界()
    {
        // 直接：审查意见的复现输入（RampSolver.Solve(p, 0.8, 200, 20, 1000, 1150, 25, 1150, 72)）
        var p35 = new DesignInputs { TAmbC = 35 };
        var r35 = RampSolver.Solve(p35, 0.8, 200, 20, 1000, tRefC: 1150, fromC: 25, targetC: 1150, maxHours: 72);
        _out.WriteLine($"环境 35 °C：表 {r35.LossTableLoC:0}～{r35.LossTableHiC:0}　超界 {r35.LossTableExceeded}　到位 {r35.Reached}　用时 {r35.HoursToTarget:0.#####} h　{r35.LossTableNote}");
        Assert.Equal(35.0, r35.LossTableLoC);
        Assert.True(25 < r35.LossTableLoC, "构造要让起点低于表下限");
        Assert.False(r35.LossTableExceeded, r35.LossTableNote);
        Assert.Equal("", r35.LossTableNote);
        // 起点高于目标（目标 900，起点填 1120）：上限随起点抬到 1320，不再超界（2026-09-15 Opus 5，表上限规则改后）
        var hiStart = RampSolver.Solve(p35, 0.8, 200, 20, 1000, tRefC: 900, fromC: 1120, targetC: 900, maxHours: 72);
        _out.WriteLine($"起点 1120、上限 {hiStart.LossTableHiC:0}：超界 {hiStart.LossTableExceeded}　{hiStart.LossTableNote}");
        Assert.Equal(1320.0, hiStart.LossTableHiC);
        Assert.False(hiStart.LossTableExceeded, hiStart.LossTableNote);
        // 参考温度两头都查：环境 35 °C、参考温度 30 °C ⇒ 超界
        var lowRef = RampSolver.Solve(p35, 0.8, 200, 20, 1000, tRefC: 30, fromC: 25, targetC: 1150, maxHours: 72);
        Assert.True(lowRef.LossTableExceeded);
        Assert.Contains(lowRef.LossTableOut, o => o.Probe == RampTableProbe.Reference && o.TempC == 30.0);
        Assert.DoesNotContain(lowRef.LossTableOut, o => o.Probe == RampTableProbe.Start);   // 起点 25 低于下限 35，只查上限，不算

        // 整线：默认设计（W08）取一段 300 mm（与上一条门同一个缩短算例）、环境 35 °C、开升温检查 ⇒ 「升温到位用时（集总）」不许因为散热表判不了。
        //   为什么缩成一段：原病是**每一段**的起点都低于表下限、逐段同样触发，一段就走到同一条代码；W08 三段整线在快套件里单条要 1 分 14 秒
        //   （2026-09-15 Opus 5 本地实跑一次，三段版本同样通过），一段约 8 秒。
        var d = DesignSpec.W08.Clone();
        d.SetpointC = new[] { 1150.0 }; d.SegLengthMm = new[] { 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(p35, checkRamp: true);
        Assert.True(lc.CheckRamp);
        Assert.Equal(25.0, lc.RampFromC);
        Assert.Equal(35.0, lc.Base.TAmbC);
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        var ck = r.Checks.First(c => c.Name.StartsWith(LineResult.Key.RampHours, StringComparison.Ordinal));
        _out.WriteLine($"整线（环境 35 °C）：{Criteria.Plain(ck.Name)} = {ck.Actual:0.###} h　判不了 {ck.Undetermined}　{ck.Note}");
        Assert.DoesNotContain("管表面散热表", ck.Note);
        Assert.False(ck.Undetermined, ck.Note);
    }

    [Fact]
    public void 闭式快筛与抽热预算表_查表温度出了表区间就判不了()
    {
        var p = new DesignInputs();
        Assert.Equal(25.0, p.TAmbC);
        // RampScreen：目标 1150 在表里；目标 20 °C 低于环境 25 °C（表下限）⇒ 判不了
        var good = RampScreen.Evaluate(p, 20, 0.8, 1150);
        var low = RampScreen.Evaluate(p, 20, 0.8, 20);
        _out.WriteLine($"快筛：目标 1150 超界 {good.LossTableExceeded} {good.Verdict}；目标 20 超界 {low.LossTableExceeded} {low.Verdict}");
        Assert.False(good.LossTableExceeded);
        Assert.True(low.LossTableExceeded);
        Assert.False(low.Ok);
        var j = RampScreen.Judge(p, 20, 0.8, 20);
        Assert.True(j.Undetermined); Assert.False(j.Ok);
        Assert.Contains("无法判定", j.Note);
        Assert.False(RampScreen.Judge(p, 20, 0.8, 1150).Undetermined);

        // DrawBudgetW：管温 1150 有数；管温 20 °C 低于环境 ⇒ NaN
        double b = DesignScreen.DrawBudgetW(p, 0.8, 1150), bLow = DesignScreen.DrawBudgetW(p, 0.8, 20);
        _out.WriteLine($"抽热预算：管温 1150 → {b:0.000} W；管温 20 → {bLow}");
        Assert.True(double.IsFinite(b) && b > 0);
        Assert.True(double.IsNaN(bLow));
    }
}
