using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F7′ 细区半径**自适应**（决 29 改定，HANDOVER §0.-21 决 29 ①～⑦）—— 2026-09-23，Opus 5.5（C4′）
//  规则与放大只在 Core：MeshAdapt.FineRadiusPlanOf／GrowFineRadius／ThermalLengthMm／PlateOuterRadiusMm（全仓唯一来源），
//  入口 MeshVerify.FineRadiusPlanFor（解析设计／图纸），用计划的：MeshVerify.Run 阶梯、Solver 求根与终局复核、保温搜索、可行窗口。
//  门名一律带「F7」前缀（审查 T5：本档的门 b／c／d／e／f 与 R48NMeshGateTests、R48MStopTolCostTests、R48NMeshInjectTests 里同字母的门不是一回事）。
//  快门：F7 门 c 单向门、F7 门 d 终止门、F7 门 d₂（热点出在第 2 档，审查 L-3）、F7 门 L1（上限处判词与初值无关，审查 L-1）、F7 门 R1（导航改回开关，审查 R-1）、
//        F7 门 F1（只导航重做回下角，源码，审查 F1）、计划规则门（初值／放大量／打印／热长度同一函数）、F7 门 f 单一来源源码门。
//  慢门（R48F7AdaptiveRadiusSlowTests）：F7 门 b（初值覆盖门）、F7 门 c′、F7 门 e 改回逐位门、探针 g 初值敏感度、开 − 关归因（三支并列 ＋ 导航 50 对计划初值，审查 R-5／F5）—— 断言各自独立（审查 F2）。
// ════════════════════════════════════════════════════════════════════════════
public class R48F7AdaptiveRadiusTests
{
    private readonly ITestOutputHelper _o;
    public R48F7AdaptiveRadiusTests(ITestOutputHelper o) { _o = o; }
    internal const string Sign = R48F7FineRadiusDecoupleTests.Sign;
    static DesignInputs P0() => R48F7FineRadiusDecoupleTests.P0();
    static bool Bits(double a, double b) => R48F7FineRadiusDecoupleTests.Bits(a, b);

    // ─────────────────────────────── 合成整线结果（门 c／d 用；只有主循环读的那几项）
    static ConstraintOut C(string key, double v, double lim) => new() { Name = key, Actual = v, Limit = lim, Ok = true };
    /// <summary>合成的整线结果：三条复核判据（值恒定 ⇒ 档间变化 0，三档即收敛）、一片法兰，最远热点 = <paramref name="peakR"/>（放在舌区峰上）。</summary>
    internal static LineResult Fake(double peakR, int cells) => new()
    {
        Ok = true, Converged = true, MeshCells = cells,
        Checks = new[] { C(LineResult.Key.NetFlux, 3.0, 0), C(LineResult.Key.HotOverTc, 1.0, 5), C(LineResult.Key.ColdUnderTc, 1.0, 5) },
        Flanges = new[] { new FlangeOut { Name = "片0", DiscMaxRMm = 20.0, TabMaxRMm = peakR, LocalStabRMm = 15.0 } },
    };

    /// <summary>W08 现役设计的解析工厂（与 MeshVerify.AnalyticCaseFactory 同一条路；不解场，只造算例）。</summary>
    static (Func<double, double, LineCase> Fac, FineRadiusPlan Plan, double InnerR, double Cap) W08Factory()
    {
        var d = R48NMeshGateTests.Design("W08");
        var plan = MeshVerify.FineRadiusPlanFor(d, P0());
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
        var fac = MeshVerify.AnalyticCaseFactory(d, P0(), plan.RadiusMm, innerR);
        return (fac, plan, innerR, plan.CapMm);
    }

    static bool NonDecreasing(IEnumerable<double> xs) { double last = double.NegativeInfinity; foreach (var x in xs) { if (x < last) return false; last = x; } return true; }

    // ═══════════════════════════════════════════════ 计划规则门（快）
    /// <summary>
    /// 计划规则门：① 初值 = max(盘半径, 孔半径) + ℓ_t，ℓ_t 与段解自己报的热长度 SegmentSolver.Solve(...).DecayLengthMm **逐位相同**（「同一函数、控温点口径」这句话的门）；
    /// ② 放大量 = min(上限, max(r 峰 + PeakMarginMm, 半径 + 一粗格))，盖住了不动、峰位 NaN 不动、计划不放大不动；③ 上限 = 材料包络最大半边长；④ Describe 印出初值、余量₀ 与输入、上限、放大原因与数、终值；
    /// ⑤ PeakMarginMm 仍是 10（阈值没挪）；⑥ 热长度量不到 ⇒ 抛异常（不拿编出来的数顶上）。
    /// 覆盖：MeshAdapt 的纯函数与 W08／W06 现役设计的计划。不覆盖：解场（慢门 b）。阈值：逐位；放大量按定义式逐位。
    /// </summary>
    [Fact]
    public void 门_计划规则_初值热长度同一函数_放大量_上限_打印()
    {
        Assert.Equal(10.0, MeshAdapt.PeakMarginMm);
        foreach (var which in new[] { "W08", "W06" })
        {
            var d = R48NMeshGateTests.Design(which);
            var lc = d.BuildCase(P0(), checkRamp: false);
            var (ell, src) = MeshAdapt.ThermalLengthMm(lc);
            // 段解自己报的热长度（控温点口径）逐段对，取最大 —— 与 ThermalLengthMm 逐位相同
            double segMax = double.NaN;
            for (int i = 0; i < lc.SegmentCount; i++)
            {
                var sr = SegmentSolver.Solve(LineRunner.BaseSegParams(lc, i, lc.GlassInC));
                Assert.True(sr.Ok, sr.Message);
                segMax = double.IsNaN(segMax) ? sr.DecayLengthMm : Math.Max(segMax, sr.DecayLengthMm);
            }
            Assert.True(Bits(ell, segMax), $"{which}：ThermalLengthMm {ell:R} ≠ 段解 DecayLengthMm 最大值 {segMax:R}");
            var plan = MeshVerify.FineRadiusPlanFor(d, P0());
            Assert.True(Bits(plan.InitialMm, Math.Max(d.DiscRadiusMm, d.HoleRadiusMm) + ell));
            Assert.True(plan.Adaptive); Assert.Empty(plan.Steps); Assert.Null(plan.Refused);
            var env = lc.FlangePlates.Select(g => new AnalyticMaterial(g).MaterialEnvelope())
                        .Max(e => new[] { Math.Abs(e.XMin), Math.Abs(e.XMax), Math.Abs(e.ZMin), Math.Abs(e.ZMax) }.Max());
            Assert.True(Bits(plan.CapMm, env));
            string txt = plan.Describe();
            foreach (var must in new[] { "初值", "余量₀", "ℓ_t", "kA", "β′", "上限 = 板料外缘", "放大 0 次", "终值" }) Assert.Contains(must, txt);
            _o.WriteLine($"{which}：ℓ_t = {ell:R} mm（段解 DecayLengthMm 最大值 {segMax:R}）；{txt}");
        }
        // 放大量的定义式
        var p0 = MeshAdapt.GivenFineRadiusPlan(50.0) with { CapMm = 170.0, CapSource = "门" };
        var g1 = MeshAdapt.GrowFineRadius(p0, 45.0, 31.0, 5.5, double.NaN, "门");          // 45 + 10 = 55 > 50 ⇒ max(55, 55.5) = 55.5
        Assert.True(g1.Grew && Bits(g1.Plan.RadiusMm, Math.Max(45.0 + 10.0, 50.0 + 5.5)));
        var g2 = MeshAdapt.GrowFineRadius(p0, 30.0, 31.0, 5.5, double.NaN, "门");          // 盖住了 ⇒ 不动
        Assert.False(g2.Grew); Assert.Null(g2.Verdict); Assert.Same(p0, g2.Plan);
        var g3 = MeshAdapt.GrowFineRadius(p0, 100.0, 31.0, 5.5, double.NaN, "门");         // 大步：到 r + 10 = 110
        Assert.True(Bits(g3.Plan.RadiusMm, 110.0));
        var g4 = MeshAdapt.GrowFineRadius(p0, 200.0, 31.0, 5.5, double.NaN, "门");         // 超出上限 ⇒ 截到上限
        Assert.True(g4.Grew && Bits(g4.Plan.RadiusMm, 170.0));
        var g5 = MeshAdapt.GrowFineRadius(g4.Plan, 200.0, 31.0, 5.5, double.NaN, "门");    // 已在上限 ⇒ 拒答
        Assert.True(g5.Refused && !g5.Grew); Assert.Contains("细化半径已放大到上限", g5.Verdict); Assert.Contains("这次复核的温度类判据不算数", g5.Verdict);
        var g6 = MeshAdapt.GrowFineRadius(p0, double.NaN, 31.0, 5.5, double.NaN, "门");    // 峰位 NaN ⇒ 不放大，原句
        Assert.False(g6.Grew || g6.Refused); Assert.Equal(MeshAdapt.PeakVerdict(double.NaN, 31.0, 50.0), g6.Verdict);
        var legacy = MeshAdapt.FineRadiusPlanOf(30, 25.8, 140, double.NaN, "", 170, "门", MeshVerify.LegacyTabLengthFactor);
        var g7 = MeshAdapt.GrowFineRadius(legacy, 55.0, 31.0, 5.5, double.NaN, "门");      // 改回不放大 ⇒ 原句
        Assert.False(g7.Grew || g7.Refused); Assert.Equal(MeshAdapt.PeakVerdict(55.0, 31.0, 59.0), g7.Verdict);
        var g8 = MeshAdapt.GrowFineRadius(MeshAdapt.GivenFineRadiusPlan(50.0), 55.0, 31.0, 5.5, double.NaN, "门");   // 量不到上限 ⇒ 拒答（不猜）
        Assert.True(g8.Refused); Assert.Contains("量不到板料外缘", g8.Verdict);
        Assert.Throws<InvalidOperationException>(() => MeshAdapt.FineRadiusPlanOf(30, 25.8, 140, double.NaN, "量不到", 170, "门"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshAdapt.GrowFineRadius(p0, 45.0, 31.0, 0.0, double.NaN, "门"));
        Assert.Contains("放大 1 次", g1.Plan.Describe()); Assert.Contains("r + 余量", g1.Plan.Describe());
    }

    // ═══════════════════════════════════════════════ 门 c　单向门（快）
    /// <summary>
    /// 门 c（单向）：MeshVerify 加密阶梯的主循环（生产那一份 RunCore，只把整线解换成合成结果）在「热点落在初值外」「细档上热点又往外挪」两次放大下，
    /// 每一次整线解用的半径序列**非降**；每次放大后从第一档重来（档间比较只在同一半径上）；最后盖住、判收敛、热点句为 null。
    /// 合成：第 1 档起热点 r = r₀ + 5（r + 10 &gt; r₀ ⇒ 放大）；半径放大到 R₁ 之后，第 3 档（h₀/4）热点挪到 R₁ + 3（⇒ 再放大到 R₂）；之后都盖住。
    /// 覆盖：放大 → 重来 → 半径只增不减 → 收敛的控制逻辑（判法、放大量、拒答文字全是生产那一份）。
    /// 不覆盖：真实场里热点随网格怎么挪（慢门 b 只看到一档）；Solver／保温搜索／可行窗口三处的放大（它们各自的循环不走 RunCore；列入不覆盖）。
    /// 阈值：非降（逐个 ≥ 前一个）；收敛判据是 MeshAdapt.Converged 原样。
    /// </summary>
    [Fact]
    public void 门c_单向门_合成热点两次外移_半径序列非降_放大后从第一档重来()
    {
        var (fac, plan, innerR, cap) = W08Factory();
        double h0 = 1.0, r0 = plan.RadiusMm;
        double? r1 = null;
        int calls = 0;
        LineResult Solve(LineCase lc, IProgress<string>? pr, CancellationToken ct)
        {
            calls++;
            double R = lc.MeshFineRadiusMm, h = lc.MeshFineMm;
            if (Bits(R, r0)) return Fake(r0 + 5.0, 1000 + calls);           // 热点在初值外
            r1 ??= R;
            if (Bits(R, r1.Value) && h < h0 / 4 + 1e-9) return Fake(r1.Value + 3.0, 1000 + calls);   // 细档上热点外移
            return Fake(r0 + 5.0, 1000 + calls);
        }
        var res = MeshVerify.RunCore(fac, h0, plan, innerR, int.MaxValue, 6, null, CancellationToken.None, Solve);
        var radii = res.RadiusTrace.Select(t => t.RadiusMm).ToArray();
        _o.WriteLine(string.Join("\n", res.RadiusTrace.Select(t => $"h {t.Fine:0.000}　半径 {t.RadiusMm:0.###}　峰 {t.PeakRMm:0.###}　{t.Outcome}")));
        _o.WriteLine(res.RadiusPlan!.Describe()); _o.WriteLine(res.Verdict);
        Assert.True(NonDecreasing(radii), "半径序列不是非降：" + string.Join(" → ", radii));
        Assert.Equal(2, res.RadiusPlan!.Steps.Length);
        Assert.True(Bits(res.RadiusPlan.Steps[0].ToMm, Math.Min(cap, Math.Max(r0 + 5.0 + 10.0, r0 + res.RadiusPlan.Steps[0].CoarseMm))));
        Assert.True(Bits(res.RadiusPlan.Steps[1].ToMm, Math.Min(cap, Math.Max(r1!.Value + 3.0 + 10.0, r1.Value + res.RadiusPlan.Steps[1].CoarseMm))));
        Assert.Null(res.PeakOutsideFine);
        Assert.True(res.Converged, res.Verdict);
        // 收敛那三档都在最终半径上（旧半径上的档作废，不进比较）
        Assert.Equal(3, res.Trace.Count);
        Assert.All(res.RadiusTrace.TakeLast(3), t => Assert.True(Bits(t.RadiusMm, res.RadiusPlan.RadiusMm)));
        Assert.Equal(1 + 3, res.DiscardedTiers.Count);   // 第一次放大作废 1 档，第二次作废 3 档
        Assert.Equal(1 + 3 + 3, calls);
    }

    // ═══════════════════════════════════════════════ 门 d　终止门（快）
    /// <summary>
    /// 门 d（终止）：注入一个落在**板料外缘之外**的假热点（r = 上限 + 20）⇒ 半径一步步放大到上限、再解一次仍盖不住 ⇒ 当场拒答返回：
    /// 不死循环（解的次数 = 放大次数 + 1）、不空跑完阶梯（只在第一档上发生，P8）、判词以「✗ **本次复核不算数**」开头、原句是「细化半径已放大到上限，仍盖不住热点」且含「这次复核的温度类判据不算数」、
    /// Converged = false、计划 Refused 非空、最后的半径 = 上限、半径序列非降。另验：峰位 NaN ⇒ 不放大、走旧路（跑完阶梯、判不算数）；改回计划 ⇒ 不放大、走旧路。
    /// 纯函数终止界：一粗格 5.5 mm 从 r₀ 到上限，放大步数 ≤ ⌈(上限 − r₀)/5.5⌉（= 16）。
    /// ⚠ 审查 L-3：一粗格取**当档**的 MeshCoarseMm（= 11·h/2，随档减半），16 只是「热点出在第一档」的界；第 k 档是 ⌈(上限 − r₀)/(11·h_k/2)⌉，
    ///   且每次放大要重跑已走过的 k 档 —— 由 F7 门 d₂ 钉「热点出在第 2 档」那一种（32 次放大、66 次整线解）。
    /// 覆盖：MeshVerify 主循环的拒答出口与终止性（第一档）。不覆盖：界面与命令行怎么印这句（Windows 待办）；第 3 档及以后（界按同式，未单独钉）。阈值：次数与文字逐项。
    /// </summary>
    [Fact]
    public void 门d_终止门_假热点在板缘外_放大到上限后拒答_文案正确_不死循环()
    {
        var (fac, plan, innerR, cap) = W08Factory();
        Assert.True(double.IsFinite(cap) && cap > plan.RadiusMm);
        int calls = 0;
        var res = MeshVerify.RunCore(fac, 1.0, plan, innerR, int.MaxValue, 6, null, CancellationToken.None,
                                     (lc, pr, ct) => { calls++; return Fake(cap + 20.0, 1000 + calls); });
        _o.WriteLine(string.Join("\n", res.RadiusTrace.Select(t => $"h {t.Fine:0.000}　半径 {t.RadiusMm:0.###}　峰 {t.PeakRMm:0.###}　{t.Outcome}")));
        _o.WriteLine(res.Verdict);
        var fp = res.RadiusPlan!;
        Assert.False(res.Converged);
        Assert.NotNull(fp.Refused);
        Assert.True(Bits(fp.RadiusMm, cap), $"终值 {fp.RadiusMm:R} ≠ 上限 {cap:R}");
        Assert.Equal(fp.Steps.Length + 1, calls);
        Assert.All(res.RadiusTrace, t => Assert.True(Bits(t.Fine, 1.0)));          // 只在第一档上发生：不空跑后面的档（P8）
        Assert.True(NonDecreasing(res.RadiusTrace.Select(t => t.RadiusMm)));
        Assert.StartsWith("✗ **本次复核不算数**", res.Verdict);
        Assert.StartsWith("★★ **细化半径已放大到上限，仍盖不住热点**", res.PeakOutsideFine);
        Assert.Contains("这次复核的温度类判据不算数", res.PeakOutsideFine);
        Assert.Contains($"上限 = 板料外缘 {cap:0.0} mm", res.PeakOutsideFine);

        // 峰位 NaN（某片三种热点全算不出）⇒ 不放大，走旧路：每档记那句话、跑完阶梯、判不算数
        int callsN = 0;
        var blind = MeshVerify.RunCore(fac, 1.0, plan, innerR, int.MaxValue, 3, null, CancellationToken.None, (lc, pr, ct) =>
        {
            callsN++;
            var r = Fake(30.0, 1000 + callsN);
            r.Flanges = new[] { new FlangeOut { Name = "片0", DiscMaxRMm = double.NaN, TabMaxRMm = double.NaN, LocalStabRMm = double.NaN } };
            return r;
        });
        Assert.Equal(3, callsN); Assert.Empty(blind.RadiusPlan!.Steps); Assert.False(blind.Converged);
        Assert.StartsWith("★★ **峰位算不出来**", blind.PeakOutsideFine);

        // 改回计划（旧规则、不放大）⇒ 与改前同一个行为：不放大、跑完阶梯、判不算数
        var d = R48NMeshGateTests.Design("W08");
        var legacy = MeshVerify.FineRadiusPlanFor(d, P0(), MeshVerify.LegacyTabLengthFactor);
        int callsL = 0;
        var back = MeshVerify.RunCore(fac, 1.0, legacy, innerR, int.MaxValue, 3, null, CancellationToken.None,
                                      (lc, pr, ct) => { callsL++; Assert.True(Bits(lc.MeshFineRadiusMm, 59.0)); return Fake(55.0, 1000 + callsL); });
        Assert.Equal(3, callsL); Assert.Empty(back.RadiusPlan!.Steps); Assert.False(back.Converged);
        Assert.StartsWith("★★ **热点离细区边缘太近或落在粗区**", back.PeakOutsideFine);

        // 纯函数终止界（最坏情形：热点每次都只比「半径 − 余量」多 0.01 mm、跟着半径往外挪 ⇒ 每次只长一粗格）
        var pp = plan; int steps = 0;
        while (true)
        {
            var g = MeshAdapt.GrowFineRadius(pp, pp.RadiusMm - MeshAdapt.PeakMarginMm + 0.01, innerR, 5.5, double.NaN, "界");
            pp = g.Plan;
            if (g.Refused) break;
            Assert.True(g.Grew); steps++;
            Assert.True(steps <= 1000, "放大不终止");
        }
        Assert.True(steps <= (int)Math.Ceiling((cap - plan.RadiusMm) / 5.5), $"放大 {steps} 步，超过终止界 ⌈(上限 − r₀)/一粗格⌉ = {(int)Math.Ceiling((cap - plan.RadiusMm) / 5.5)}");
        Assert.True(Bits(pp.RadiusMm, cap) && pp.Refused is not null);
        _o.WriteLine($"终止：上限 {cap:0.###}、初值 {plan.RadiusMm:0.###}、放大 {fp.Steps.Length} 次到上限、解 {calls} 次；纯函数 {steps} 步。");
    }

    // ═══════════════════════════════════════════════ F7 门 d₂　终止门·热点出在第 2 档（快，审查 L-3）
    /// <summary>
    /// F7 门 d₂（审查 L-3）：放大时的「一粗格」取**当档**算例的 MeshCoarseMm（= 11·h/2，随档减半），所以门 d 的界 ⌈(上限 − r₀)/5.5⌉ = 16 只对「热点出在第一档」成立。
    /// 本门合成「热点只在第 2 档（h₀/2）越界、且永远只比 半径 − 余量 多 0.01 mm」：每次放大只长一粗格 2.75 mm、放大后从第一档重来 ⇒
    /// 放大次数 = ⌈(上限 − r₀)/2.75⌉（W08：32），整线解 = 2·放大 + 2（66），作废档 = 2·放大（64），最后拒答；每一步记下的一粗格都是 2.75。
    /// 覆盖：MeshVerify 主循环在非第一档放大时的一粗格口径、重来与成本上界（生产那一份 RunCore）。不覆盖：Solver／保温搜索／可行窗口（它们的一粗格取固定网格，不随档变）；真实场。
    /// 阈值：次数逐项（由定义式算，不是记录值）。「一粗格取当档」是开发者缺省（路线甲，可改）：改成与档无关的量（h₀ 档粗区）是【待决定】。
    /// </summary>
    [Fact]
    public void 门d2_终止门_热点出在第2档_一粗格随档减半_放大次数与重算成本按当档一粗格()
    {
        var (fac, plan, innerR, cap) = W08Factory();
        double h0 = 1.0, r0 = plan.RadiusMm;
        int calls = 0;
        var res = MeshVerify.RunCore(fac, h0, plan, innerR, int.MaxValue, 6, null, CancellationToken.None, (lc, pr, ct) =>
        {
            calls++;
            bool tier2 = Math.Abs(lc.MeshFineMm - h0 / 2) < 1e-12;
            return Fake(tier2 ? lc.MeshFineRadiusMm - MeshAdapt.PeakMarginMm + 0.01 : 20.0, 1000 + calls);
        });
        var fp = res.RadiusPlan!;
        double c2 = fac(h0 / 2, h0 / 2).MeshCoarseMm;
        int G = (int)Math.Ceiling((cap - r0) / c2);
        _o.WriteLine($"一粗格（第 2 档）{c2:R}；上限 {cap:0.###}、初值 {r0:0.###} ⇒ 放大 {fp.Steps.Length}（界 {G}）、整线解 {calls}、作废 {res.DiscardedTiers.Count}；{res.Verdict}");
        Assert.True(Bits(c2, 11.0 * (h0 / 2) / 2.0), $"第 2 档一粗格 {c2:R} ≠ 11·h/2");
        Assert.All(fp.Steps, s => Assert.True(Bits(s.CoarseMm, c2)));
        Assert.Equal(G, fp.Steps.Length);
        Assert.Equal(2 * G + 2, calls);
        Assert.Equal(2 * G, res.DiscardedTiers.Count);
        Assert.NotNull(fp.Refused); Assert.False(res.Converged);
        Assert.True(Bits(fp.RadiusMm, cap));
        Assert.True(G > (int)Math.Ceiling((cap - r0) / 5.5), "门没牙：第 2 档的界应大于第一档的 16");
    }

    // ═══════════════════════════════════════════════ F7 门 L1　上限处判词与初值无关（快，审查 L-1）
    /// <summary>
    /// F7 门 L1（审查 L-1）：半径 ≥ 板料外缘时网格逐节点相同（细区方带铺满材料包络），判词**不许**取决于初值有没有超过上限：
    /// ① 纯函数：规则给的初值大于上限 ⇒ 截到上限（InitialUncappedMm 记原值、Describe 印出）；同一热点在「初值截到上限」与「从下面放大到上限」两条路上都判拒答；
    /// ② 调用方直接给的初值（上限未量）大于上限 ⇒ GrowFineRadius 先按算例上限截、再判 ⇒ 同样拒答；
    /// ③ 生产主循环 RunCore：给定初值 150（W08 上限 140）、热点 135 ⇒ 第一档就以 140 建网格并拒答；给定初值 120、同一热点 ⇒ 放大到 140 再拒答 —— 两条路判的都是「到上限拒答」、理由同一段；
    /// ④ 拒答原句在上限量得到时写「口径拒答、不是分辨率拒答」，不再写「梯度分辨不出」；量不到上限时保留「可能分辨不出」。
    /// 覆盖：MeshAdapt.CapToPlate／GrowFineRadius／PeakAtCapVerdict 与 RunCore 的截断。不覆盖：Solver／保温搜索／可行窗口由调用方传入超上限计划的那一支（生产只传 FineRadiusPlanFor 或复核终值，已截）；
    ///   全细网格上还要不要按「峰 + 10 ≤ 半径」拒答（【待决定】，本门按现行规则统一到拒答一侧）。阈值：判词逐项；PeakMarginMm = 10 没动。
    /// </summary>
    [Fact]
    public void 门L1_上限处判词与初值无关_初值截到上限_拒答理由照实()
    {
        const double cap = 50.0, peak = 42.0, inner = 31.0;
        // ① 规则给的初值 53.697 > 上限 50 ⇒ 截到 50
        var pA = MeshAdapt.FineRadiusPlanOf(30, 25.8, -50, 23.697, "门", cap, "门：上限");
        Assert.True(Bits(pA.InitialMm, cap) && Bits(pA.InitialUncappedMm, 30 + 23.697));
        Assert.Contains("截到上限", pA.Describe());
        var gA = MeshAdapt.GrowFineRadius(pA, peak, inner, 5.5, double.NaN, "门");
        Assert.True(gA.Refused && !gA.Grew, "初值截到上限后，同一热点必须拒答（不能因初值超上限判盖住）");
        var pB = MeshAdapt.FineRadiusPlanOf(30, 25.8, -50, 12.0, "门", cap, "门：上限");   // 初值 42 ⇒ 放大到 50 ⇒ 拒答
        var gB1 = MeshAdapt.GrowFineRadius(pB, peak, inner, 5.5, double.NaN, "门");
        Assert.True(gB1.Grew && Bits(gB1.Plan.RadiusMm, cap));
        var gB2 = MeshAdapt.GrowFineRadius(gB1.Plan, peak, inner, 5.5, double.NaN, "门");
        Assert.True(gB2.Refused);
        // ② 调用方直接给的初值、上限要从算例量
        var gC = MeshAdapt.GrowFineRadius(MeshAdapt.GivenFineRadiusPlan(30 + 23.697), peak, inner, 5.5, cap, "门");
        Assert.True(gC.Refused && Bits(gC.Plan.RadiusMm, cap) && Bits(gC.Plan.InitialUncappedMm, 30 + 23.697));
        // 改回与不放大照旧不截
        Assert.True(double.IsNaN(MeshAdapt.FineRadiusPlanOf(30, 25.8, -50, 23.697, "门", cap, "门", adaptive: false).InitialUncappedMm));
        // ④ 理由照实（上限量得到 ⇒ 口径拒答；量不到 ⇒ 保留分辨率理由）
        foreach (var v in new[] { gA.Verdict!, gB2.Verdict!, gC.Verdict! })
        {
            Assert.StartsWith("★★ **细化半径已放大到上限，仍盖不住热点**", v);
            Assert.Contains("这次复核的温度类判据不算数", v);
            Assert.Contains("口径拒答，不是分辨率拒答", v);
            Assert.DoesNotContain("分辨不出", v.Replace("不是**网格分辨不出梯度", ""));
        }
        var gN = MeshAdapt.GrowFineRadius(MeshAdapt.GivenFineRadiusPlan(50.0), 55.0, inner, 5.5, double.NaN, "门");
        Assert.True(gN.Refused); Assert.Contains("可能分辨不出", gN.Verdict); Assert.DoesNotContain("口径拒答", gN.Verdict);
        // ③ 生产主循环：初值在上限之上 与 从下面放大到上限，判的是同一件事
        var (fac, _, innerR, capW) = W08Factory();
        double hot = capW - 5.0;   // 135：hot + 10 > 140
        string Run(double r0, out FineRadiusPlan plan, out int n, out double firstR)
        {
            int k = 0; double fr = double.NaN;
            var res = MeshVerify.RunCore(fac, 1.0, MeshAdapt.GivenFineRadiusPlan(r0), innerR, int.MaxValue, 3, null, CancellationToken.None,
                                         (lc, pr, ct) => { k++; if (double.IsNaN(fr)) fr = lc.MeshFineRadiusMm; return Fake(hot, 1000 + k); });
            plan = res.RadiusPlan!; n = k; firstR = fr;
            Assert.False(res.Converged); Assert.NotNull(plan.Refused);
            return res.PeakOutsideFine!;
        }
        string vHi = Run(150.0, out var planHi, out int nHi, out double r1Hi);
        string vLo = Run(120.0, out var planLo, out int nLo, out double r1Lo);
        _o.WriteLine($"初值 150：第一档半径 {r1Hi}、解 {nHi} 次；{planHi.Describe()}\n初值 120：第一档半径 {r1Lo}、解 {nLo} 次；{planLo.Describe()}");
        Assert.True(Bits(r1Hi, capW), "初值 150 > 上限：第一档必须以上限建网格（截到上限）");
        Assert.Equal(1, nHi); Assert.Equal(2, nLo);
        Assert.True(Bits(planHi.RadiusMm, capW) && Bits(planLo.RadiusMm, capW));
        string Reason(string v) => v[v.IndexOf("⇒", StringComparison.Ordinal)..];
        Assert.Equal(Reason(vHi), Reason(vLo));
    }

    // ═══════════════════════════════════════════════ F7 门 R1　改回开关：导航落回算例缺省（快，审查 R-1）
    /// <summary>
    /// F7 门 R1（审查 R-1／T6）：P6 那一改（没给半径的导航求根、可行窗口导航支不再落回算例缺省 50）的**只供门用的改回开关**：
    /// SolverOptions.NavUsesCaseDefaultRadius ⇒ Solver 不造计划，导航选项的半径为 0，ApplyCaseMesh 后算例半径 = 缺省 50（与 cff38c6 同一条路）；
    /// 缺省（生产）⇒ 计划初值 53.697；开关随 Clone 走。InsulWindow.Options.NavUsesCaseDefaultRadius ⇒ 导航支半径记 50、不挂计划。
    /// 另验 Solver 的「调用方给定半径」大于上限时截到上限（审查 L-1）。
    /// 覆盖：计划选择（Solver.RadiusPlanOf，Solve 调同一份）与导航选项、可行窗口的网格口径记录。不覆盖：整条求根在开关下与 cff38c6 逐位（没有实场对拍；实施记录「审查后修改」写明）。阈值：逐位。
    /// </summary>
    [Fact]
    public void 门R1_改回开关_导航落回算例缺省50_生产取计划初值()
    {
        var d = R48NMeshGateTests.Design("W08");
        var lc = d.BuildCase(P0(), checkRamp: false);
        double def = new LineCase().MeshFineRadiusMm;
        Assert.Equal(50.0, def);
        var planProd = Solver.RadiusPlanOf(d, P0(), new SolverOptions(), lc);
        Assert.NotNull(planProd);
        Assert.True(Bits(planProd!.RadiusMm, MeshVerify.FineRadiusPlanFor(d, P0()).RadiusMm));
        var optBack = new SolverOptions { NavUsesCaseDefaultRadius = true };
        Assert.True(optBack.Clone().NavUsesCaseDefaultRadius);
        Assert.Null(Solver.RadiusPlanOf(d, P0(), optBack, lc));
        var nav = Solver.NavOptionsOf(optBack);
        var lcN = d.BuildCase(P0(), checkRamp: false);
        Solver.ApplyCaseMesh(lcN, nav);
        Assert.Equal(def, lcN.MeshFineRadiusMm);
        // 给了半径 ⇒ 开关不管（它只管「什么都没给」那一支）；给定半径大于上限 ⇒ 截到上限
        var given = Solver.RadiusPlanOf(d, P0(), new SolverOptions { FineRadiusMm = 150.0, NavUsesCaseDefaultRadius = true }, lc)!;
        Assert.True(Bits(given.RadiusMm, MeshAdapt.PlateOuterRadiusMm(lc).Mm) && Bits(given.InitialUncappedMm, 150.0));
        // 可行窗口导航支
        InsulWindow.Point Ok(DesignSpec x, int j, double mm) => new() { Mm = mm, Feasible = true };
        var wBack = InsulWindow.Measure(d, P0(), new InsulWindow.Options { UseNavigationMesh = true, NavUsesCaseDefaultRadius = true, Evaluate = Ok });
        Assert.Equal(def, wBack.MeshRadiusMm); Assert.Null(wBack.RadiusPlan);
        var wProd = InsulWindow.Measure(d, P0(), new InsulWindow.Options { UseNavigationMesh = true, Evaluate = Ok });
        Assert.True(Bits(wProd.MeshRadiusMm, planProd.RadiusMm)); Assert.NotNull(wProd.RadiusPlan);
    }

    // ═══════════════════════════════════════════════ F7 门 F1　只导航重做先回下角（快，源码，审查 F1）
    /// <summary>
    /// F7 门 F1（审查 F1）：终局复核后细区半径放大、只导航（FineMm = 0）重做第一遍之前，先把旋钮放回约束盒下角（与 Solve 起点同一份 ToLowerCorner）、再按 J 定截面；
    /// 起点那段语句在 ToLowerCorner 里逐字不变。覆盖：源码结构（重做分支里 ToLowerCorner → ApplySectionFloor → NavPass 的次序）。
    /// 不覆盖：实场（W08／W06 放大 0 次，这一支没有实跑到；整线解不能注入 Solver）。阈值：次序与语句逐字。
    /// </summary>
    [Fact]
    public void 门F1_只导航重做前旋钮回约束盒下角_源码()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        int def = s.IndexOf("void ToLowerCorner()", StringComparison.Ordinal);
        int body = s.IndexOf("d.TabThickMm[j] = tLo;", StringComparison.Ordinal);
        int first = s.IndexOf("ToLowerCorner();", StringComparison.Ordinal);
        Assert.True(def > 0 && body > def && first > body, "起点的下角语句不在 ToLowerCorner 里，或起点没调它");
        int redo = s.IndexOf("NavPass(\"第一遍（细区半径放大后重做）", StringComparison.Ordinal);
        int reset = s.LastIndexOf("ToLowerCorner();", redo, StringComparison.Ordinal);
        int floor = s.LastIndexOf("ApplySectionFloor(d, baseIn, opt, res, null, Log);", redo, StringComparison.Ordinal);
        Assert.True(redo > 0 && reset > first && floor > reset && floor < redo, "只导航重做前没有先回下角、再按 J 定截面");
    }

    // ═══════════════════════════════════════════════ 门 f　单一来源源码门（快）
    /// <summary>生产源码（Core、UI、Program.cs、Geom），去掉注释与字符串字面量后逐行给出。</summary>
    internal static IEnumerable<(string File, int Line, string Code)> ProductionCode()
    {
        string root = HandoverDoc.Root();
        var files = Directory.GetFiles(Path.Combine(root, "Pt_Optimize"), "*.cs", SearchOption.AllDirectories)
                    .Concat(Directory.Exists(Path.Combine(root, "Pt_Optimize.Geom")) ? Directory.GetFiles(Path.Combine(root, "Pt_Optimize.Geom"), "*.cs", SearchOption.AllDirectories) : Array.Empty<string>())
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
                yield return (Path.GetRelativePath(root, f).Replace('\\', '/'), i + 1, StripCode(lines[i]));
        }
    }

    /// <summary>去掉一行里的字符串／字符字面量与 // 注释（近似：够这几条规则用；/// 文档注释一并去掉）。</summary>
    internal static string StripCode(string l)
    {
        l = Regex.Replace(l, "@\"(?:[^\"]|\"\")*\"", "\"\"");
        l = Regex.Replace(l, "\\$?\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
        l = Regex.Replace(l, "'(?:[^'\\\\]|\\\\.)'", "''");
        int k = l.IndexOf("//", StringComparison.Ordinal);
        return k >= 0 ? l[..k] : l;
    }

    static readonly Regex R1TabLen035 = new(@"TabLength\w*\s*\)?\s*\*\s*0\.35|0\.35\s*\*\s*[\w.()]*TabLength");
    static readonly Regex R4MaxDiscPlus = new(@"Math\.Max\([^;]*DiscRadius[^;]*\)\s*\+");
    static readonly Regex R5Assign = new(@"\b(?:Mesh|FinalMesh)?FineRadiusMm\s*=(?![=>])");

    /// <summary>
    /// 门 f 的白名单：生产里每一处「给细区半径赋值」都要在这里登记来源（文件 + 该行必含的片段 + 为什么它不是第二个来源）。新出现一处 ⇒ 门红，逼着人来登记（或改走计划）。
    /// </summary>
    internal static readonly (string File, string Snippet, string Why)[] AssignWhitelist =
    {
        ("Pt_Optimize/Core/LineRunner.cs", "MeshFineRadiusMm = 50.0", "整线算例的缺省值（未经计划的单次场解：QuickCase／LineDump／接线门／界面「核算整线」走它）—— 不是规则；只有 Solver 的导航求根不再落回它（F7′ 取计划；门用改回 NavUsesCaseDefaultRadius 可让它落回）；图纸路径 FlangeAutoSizer 的导航搜索（图纸自动定厚）仍用它、不核热点（审查 R-3，实施记录 10.9-4／10.9-5，【待决定】）"),
        ("Pt_Optimize/Core/MeshAdapt.cs", "lc.MeshFineRadiusMm = fineRadiusMm", "RefineWholeMesh：把调用方给的半径写进算例（全仓唯一的加密配方）"),
        ("Pt_Optimize/Core/MeshVerify.cs", "lc.MeshFineRadiusMm = plan.RadiusMm", "加密复算主循环：半径只由计划给"),
        ("Pt_Optimize/Core/Solver.cs", "opt.FineRadiusMm = plan.RadiusMm", "求根：半径只由计划给（初值与放大后）"),
        ("Pt_Optimize/Core/Solver.cs", "navOpt.FineRadiusMm = opt.FineRadiusMm", "导航遍复制本次求解的半径（NavOptionsOf）"),
        ("Pt_Optimize/Core/InsulWindow.cs", "FineRadiusMm = plan.RadiusMm", "可行窗口：半径只由计划给"),
        ("Pt_Optimize/Core/InsulationSearch.cs", "FineRadiusMm = radiusMm", "保温搜索 MeshOpt：radiusMm = 计划的当前半径（Run 的 attempt 循环）"),
        ("Pt_Optimize/Core/ShellMesh.cs", "FineRadiusMm = fineRadius", "MeshRecipe 生效参数的记录（照抄建网格用的半径）"),
        ("Pt_Optimize/Core/FlangeAutoSizer.cs", "FinalMeshFineRadiusMm = 0", "图纸路径终局细网格的字段缺省（0 = 不用；调用方由加密复算的计划终值填）"),
        ("Pt_Optimize/Core/FlangeAutoSizer.cs", "MeshFineRadiusMm = c.MeshFineRadiusMm", "CloneCase：照抄算例"),
        ("Pt_Optimize/Core/LevelSolver.cs", "nav.FineRadiusMm = 0", "旧求解器（类注释：生产零调用）"),
        ("Pt_Optimize/Core/LevelSolver.cs", "lc.MeshFineRadiusMm = o.FineRadiusMm", "旧求解器（生产零调用）照抄选项"),
        ("Pt_Optimize/Program.cs", "MeshFineRadiusMm = fr,", "命令行仪器「细网格半径的等价性验证」：逐个试 45／35／30（扫的就是这个变量，不是规则）"),
        ("Pt_Optimize/Program.cs", "soOpt.FineRadiusMm = fr", "命令行 --solve --fine：fr = MeshVerify.RequiredMeshFor（计划初值），Solver 解后放大"),
        ("Pt_Optimize/Program.cs", "lcA.MeshFineRadiusMm = radiusA", "命令行 --meshadapt：radiusA = MeshVerify.FineRadiusPlanFor（计划初值；本仪器不放大，只印热点句）"),
        ("Pt_Optimize/UI/LineDesignPage.cs", "FineRadiusMm = finFineR", "搜形状精算：MeshVerify.RequiredMeshFor（计划初值），Solver 解后放大"),
        // 2026-09-24 补登记（cc49836 起本门即红在这一行：搜形状 Core 驱动 07644dc 之后新建，照搬界面精算那一行，当时没来登记）
        ("Pt_Optimize/Core/ShapeSearchDriver.cs", "FineRadiusMm = finFineR", "搜形状 Core 驱动赢家精算：MeshVerify.RequiredMeshFor（计划初值），Solver 解后放大（与界面搜形状精算同一条）"),
        ("Pt_Optimize/UI/LineDesignPage.cs", "optLv.FinalMeshFineRadiusMm = fine3dm.RadiusMm", "图纸细网格重解：加密复算计划的终值（FineResolveAsync）"),
        ("Pt_Optimize/UI/LineDesignPage.cs", "FineRadiusMm = fineRadiusD8", "自动定厚／细网格重解：MeshVerify.RequiredMeshFor（计划初值），Solver 解后放大"),
        ("Pt_Optimize/UI/LineDesignPage.cs", "FineMm = res.FineMm, FineRadiusMm = fineRadius", "终验三关：加密复算计划的终值"),
    };

    /// <summary>
    /// 门 f（单一来源）：生产源码（Core、界面、命令行、Geom；去注释与字符串）里 ① 没有「0.35 × 舌长」；② MeshAdapt.RequiredFineRadiusMm 只在 MeshAdapt.cs 里被调（计划规则内部）；
    /// ③ PeakMarginMm 只在 MeshAdapt.cs 里出现（没人拿它另拼半径）；④「Math.Max(…盘半径…) + …」只在白名单那一处（板料 z 包络，不是细区半径）；
    /// ⑤ 每一处给细区半径赋值的地方都在白名单里（登记了来源）。「改回 ⇒ 红」：同一套规则套在 F7 之前的三行原文上必须报出来。
    /// 覆盖：仓库里的全部生产 .cs（Linux 镜像读得到界面与 Program.cs 的源文件，只是不编译）。
    /// 不覆盖：命令行里老仪器直接传给网格生成器的字面半径（45／50／70 mm 那几十处单片实验，FlangeMesher.Build(…, fineRadius: 45.0) 这类）—— 它们不经算例、不算规则，列在实施记录的不覆盖里；
    ///   动态拼出来的字符串、反射；测试档（测试可以显式给半径做对照）。阈值：计数 0（或逐条在白名单）。
    /// </summary>
    [Fact]
    public void 门f_单一来源源码门_生产源码里没有第二处算细区半径()
    {
        var code = ProductionCode().ToList();
        Assert.True(code.Count > 10000, "没读到生产源码");
        var bad = new List<string>();
        foreach (var (f, n, c) in code)
        {
            if (R1TabLen035.IsMatch(c)) bad.Add($"① 0.35×舌长：{f}:{n}　{c.Trim()}");
            if (c.Contains("RequiredFineRadiusMm(", StringComparison.Ordinal) && f != "Pt_Optimize/Core/MeshAdapt.cs") bad.Add($"② RequiredFineRadiusMm 在 MeshAdapt 之外：{f}:{n}　{c.Trim()}");
            if (c.Contains("PeakMarginMm", StringComparison.Ordinal) && f != "Pt_Optimize/Core/MeshAdapt.cs") bad.Add($"③ PeakMarginMm 在 MeshAdapt 之外：{f}:{n}　{c.Trim()}");
            if (R4MaxDiscPlus.IsMatch(c) && !(f == "Pt_Optimize/Core/PlateCurrent2D.cs" && c.Contains("Math.Max(g.DiscRadiusMm, g.ExtHalfWidthMm) + h", StringComparison.Ordinal)))
                bad.Add($"④ max(盘半径, …) + …：{f}:{n}　{c.Trim()}");
            if (R5Assign.IsMatch(c) && !AssignWhitelist.Any(w => w.File == f && c.Contains(w.Snippet, StringComparison.Ordinal)))
                bad.Add($"⑤ 细区半径赋值不在白名单：{f}:{n}　{c.Trim()}");
        }
        // 白名单里每一条都真的还在（不许留死条目，免得白名单变成「以前允许过」的历史）
        foreach (var w in AssignWhitelist)
            if (!code.Any(x => x.File == w.File && R5Assign.IsMatch(x.Code) && x.Code.Contains(w.Snippet, StringComparison.Ordinal)))
                bad.Add($"白名单死条目：{w.File}「{w.Snippet}」");
        // 改回 ⇒ 红：F7 之前的三行原文
        var pre = new[]
        {
            ("Pt_Optimize/Core/MeshVerify.cs", "MeshAdapt.RequiredFineRadiusMm(new[] { d.DiscRadiusMm, Math.Abs(d.TabLengthMm) * 0.35 }, d.HoleRadiusMm));"),
            ("Pt_Optimize/Core/MeshVerify.cs", "double radius = MeshAdapt.RequiredFineRadiusMm(new[] { sh.DiscRadiusMm, tabLen * 0.35 }, sh.HoleRadiusMm);"),
            ("Pt_Optimize/Program.cs", "lcA.MeshFineRadiusMm = MeshAdapt.RequiredFineRadiusMm(new[] { fdA.DiscRadiusMm, Math.Abs(fdA.TabLengthMm) * 0.35 }, fdA.HoleRadiusMm);"),
        };
        foreach (var (f, c) in pre)
        {
            bool hit = R1TabLen035.IsMatch(c) || (c.Contains("RequiredFineRadiusMm(", StringComparison.Ordinal) && f != "Pt_Optimize/Core/MeshAdapt.cs");
            if (!hit) bad.Add($"改回对照没牙：{f}「{c}」没被规则抓到");
        }
        _o.WriteLine($"读了 {code.Count} 行生产源码；白名单 {AssignWhitelist.Length} 条；不过 {bad.Count}");
        foreach (var b in bad) _o.WriteLine(b);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  慢门：门 b（自适应）、门 e（改回逐位，对改前树）、探针 g（初值敏感度）、开 − 关归因（三支并列）
//  四条各自断言（审查 F2：改回对拍与门 b 不许写在同一条里、前一个红了盖住后一个）。解一次、四条共用（静态 Lazy，同一进程里只解一遍）。
// ════════════════════════════════════════════════════════════════════════════
[Trait("速度", "慢")]
public class R48F7AdaptiveRadiusSlowTests
{
    private readonly ITestOutputHelper _o;
    public R48F7AdaptiveRadiusSlowTests(ITestOutputHelper o) { _o = o; }
    const string Sign = R48F7FineRadiusDecoupleTests.Sign;
    static DesignInputs P0() => R48F7FineRadiusDecoupleTests.P0();
    static bool Bits(double a, double b) => R48F7FineRadiusDecoupleTests.Bits(a, b);
    static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    /// <summary>全量转储（R48LineDumpTests.Dump，含文字），两处耗时改写成占位（与改前探针同一写法）。</summary>
    static string DumpNoTiming(LineCase lc, LineResult r)
    {
        string full = Regex.Replace(R48LineDumpTests.Dump(lc, r, withText: true), @"(结果\.JacobianAmpSec = )[^\n]*", "$1<耗时>");
        return Regex.Replace(full, @"用时 [0-9.]+ s", "用时 <耗时> s");
    }

    /// <summary>
    /// 去行：F7′ 往转储里新加的两类行 —— 算例的细区半径计划（算例.MeshFineRadiusPlan…）、各片网格配方里的细区半径（…Recipe.FineRadiusMm = …）。
    /// 这两类是**记录**，LineRunner 不读它们；去掉之后与改前逐字节比，证「加字段」本身中性。
    /// </summary>
    internal static string DeLine(string dump)
        => string.Join("\n", dump.Split('\n').Where(l => !l.StartsWith("算例.MeshFineRadiusPlan", StringComparison.Ordinal)
                                                       && !Regex.IsMatch(l, @"Recipe\.FineRadiusMm = ")));

    internal const string PreProbeFile = "deliverable/R48_F7_改前探针_热点半径与新旧细区半径_W08W06_本次开跑于2026-09-23_141431.txt";
    internal static readonly string[] V1Files =
    {
        "deliverable/R48_F7_开关归因_W08_本次开跑于2026-09-23_151354.txt",
        "deliverable/R48_F7_开关归因_W06_本次开跑于2026-09-23_151354.txt",
    };

    /// <summary>审查 T2（2026-09-23）：热点句 null 的口径限定 —— 印进门 b、门 c′ 的证据档。</summary>
    internal const string T2Caveat = "【口径限定】最远热点的局部热稳定分量取 LocalStabRMm，它继承 #33 的偏近落点（全格真最小值 r ≈ 66～70 mm，见实施记录 10.4、HANDOVER #33）；"
        + "这里的 null 只对盘区峰、舌区峰和报出的局部热稳定位置成立，不是「盖住了所有热点」；决 99 修成全格后按该落点会放大（66～70 + 10 > 53.697／50.513），终值会变。";

    internal sealed class Ref { public double Radius, Hot, Cold, Flux, Mass, Sec = double.NaN, PeakR = double.NaN; public int Cells, Rounds; public string Sha = ""; }

    /// <summary>读「全精度（R 格式）与 SHA」段（改前探针与 (1) 归因档同一格式）；<paramref name="rule"/> = 那一列的「旧」「新」。主表另读耗时与最远热点。</summary>
    static Dictionary<(string, string, string), Ref> ReadRefs(IEnumerable<string> files, string rule)
    {
        var map = new Dictionary<(string, string, string), Ref>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var file in files)
            foreach (var line in File.ReadAllLines(Path.Combine(HandoverDoc.Root(), file)))
            {
                var c = line.Split('\t');
                if (c.Length >= 12 && c[3] == rule && c[4].StartsWith("半径 ", StringComparison.Ordinal))
                {
                    var k = (c[0], c[1], c[2]);
                    if (!map.TryGetValue(k, out var pr)) map[k] = pr = new Ref();
                    double V(string s, string head) => double.Parse(s.Substring(head.Length), inv);
                    pr.Radius = V(c[4], "半径 "); pr.Cells = int.Parse(c[5].Substring(2), inv);
                    pr.Hot = V(c[6], "最热铂 "); pr.Cold = V(c[7], "管根 "); pr.Flux = V(c[8], "净流入 "); pr.Mass = V(c[9], "铂重 ");
                    pr.Rounds = int.Parse(c[10].Substring(2), inv); pr.Sha = c[11].Substring(4);
                }
                // 主表：改前探针 17 列（第 4 列是细区mm）、(1) 归因 15 列（第 4 列是规则）
                else if (c.Length >= 15 && (c[0] == "W08" || c[0] == "W06") && (c[2] == "判决" || c[2] == "导航"))
                {
                    bool v1 = c[3] == "新" || c[3] == "旧";
                    if (v1 && c[3] != rule) continue;
                    var k = (c[0], c[1], c[2]);
                    if (!map.TryGetValue(k, out var pr)) map[k] = pr = new Ref();
                    if (v1) { pr.Sec = double.Parse(c[12], inv); pr.PeakR = double.Parse(c[13], inv); }
                    else { pr.Sec = double.Parse(c[13], inv); pr.PeakR = double.Parse(c[14], inv); }
                }
            }
        return map;
    }

    internal sealed class Row
    {
        public string Which = "", Tag = "", Grade = "", Branch = ""; public double Disc, FineMm, RadiusMm, R0 = double.NaN, InnerR, Sec, PeakR = double.NaN;
        public bool Ok, Converged, AllOk; public string Message = "";
        public double Hot = double.NaN, Cold = double.NaN, Flux = double.NaN, Mass = double.NaN;
        public int Cells, Rounds, Solves, Growths; public string Sha = "", ShaDeLined = "", HotspotTable = "", Verdict = "", PlanText = "", PeakOutsideFine = "";
    }

    static DesignSpec Floored(string which, double disc, DesignInputs p)
    {
        var d = R48NMeshGateTests.Design(which);
        d.FlangeInsulated = true; d.FlangeInsulMm = disc; d.DiscInsulMm = Array.Empty<double>();
        Solver.ApplySectionFloor(d, p, new SolverOptions(), new SolverResult { Design = d }, null, null);
        return d;
    }

    static void Fill(Row row, LineCase lc, LineResult r)
    {
        row.Ok = r.Ok; row.Converged = r.Converged; row.Message = r.Ok ? (r.Converged ? "" : "外层耦合未收敛") : r.Message;
        row.Cells = r.MeshCells; row.Rounds = r.CoupleRounds; row.FineMm = lc.MeshFineMm; row.RadiusMm = lc.MeshFineRadiusMm;
        if (!r.Ok) return;
        row.AllOk = r.AllOk;
        row.Hot = r.ValueOf(LineResult.Key.HotOverTc); row.Cold = r.ValueOf(LineResult.Key.ColdUnderTc); row.Flux = r.ValueOf(LineResult.Key.NetFlux); row.Mass = r.TotalMassG;
        var sb = new StringBuilder();
        foreach (var f in r.Flanges)
            sb.Append($"{f.Name}：盘区峰 r={f.DiscMaxRMm:0.00}　舌区峰 r={f.TabMaxRMm:0.00}　局部热稳定 r={f.LocalStabRMm:0.00}（{(f.LocalStabOnTab ? "舌" : "盘")}）；");
        row.HotspotTable = sb.ToString();
        row.PeakR = MeshVerify.HotspotRadiusMm(r);
        row.Verdict = MeshVerify.HotspotVerdict(r, row.InnerR, row.RadiusMm) ?? "null（盖住了）";
        string dump = DumpNoTiming(lc, r);
        row.Sha = Sha(dump); row.ShaDeLined = Sha(DeLine(dump));
    }

    /// <summary>
    /// 固定半径（不经放大）解一次：与改前探针 SolveRow 同一条路（ApplySectionFloor → BuildCase → Solver.ApplyCaseMesh → LineRunner.Run）。
    /// 算例上**不挂**计划（RadiusPlan = null），转储里只多两类记录行（DeLine 去掉的那两类）。
    /// </summary>
    static Row SolveFixed(string which, double disc, string grade, double fine, double radius, string branch, DesignInputs p)
    {
        var row = new Row { Which = which, Tag = $"盘{disc:0}", Disc = disc, Grade = grade, Branch = branch };
        var sw = Stopwatch.StartNew();
        try
        {
            var d = Floored(which, disc, p);
            row.InnerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fine, FineRadiusMm = radius });
            var r = LineRunner.Run(lc);
            row.Solves = 1;
            Fill(row, lc, r);
        }
        catch (Exception ex) { row.Ok = false; row.Message = $"{ex.GetType().Name}：{ex.Message}"; }
        row.Sec = sw.Elapsed.TotalSeconds;
        return row;
    }

    /// <summary>
    /// 自适应解：**生产的加密复算主循环**（MeshVerify.RunCore，整线解 = LineRunner.Run）只跑本档（maxRounds 1）——解、核热点、盖不住就按计划放大再解。
    /// 工厂与 SolveFixed 同一条路（门 f 的输入），网格尺寸：判决 = RequiredFineMmFor，导航 = 整线算例缺省尺寸只统一半径。
    /// </summary>
    static Row SolveAdaptive(string which, double disc, string grade, double fine, string branch, DesignInputs p, bool adaptive = true)
    {
        var row = new Row { Which = which, Tag = $"盘{disc:0}", Disc = disc, Grade = grade, Branch = branch };
        var sw = Stopwatch.StartNew();
        try
        {
            var d = Floored(which, disc, p);
            var plan = MeshVerify.FineRadiusPlanFor(d, p, adaptive: adaptive);
            row.R0 = plan.InitialMm;
            row.InnerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
            LineCase? lastLc = null; LineResult? lastR = null;
            Func<double, double, LineCase> fac = (h, hi) =>
            {
                var lc = d.Clone().BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fine, FineRadiusMm = plan.InitialMm, RadiusPlan = plan });
                return lc;
            };
            double h0 = fine > 0 ? fine : new LineCase().MeshFineMm;
            var res = MeshVerify.RunCore(fac, h0, plan, row.InnerR, int.MaxValue, 1, null, CancellationToken.None,
                                         (lc, pr, ct) => { var r = LineRunner.Run(lc, pr, ct); lastLc = lc; lastR = r; return r; });
            row.Solves = res.RadiusTrace.Count;
            var fp = res.RadiusPlan ?? plan;
            row.Growths = fp.Steps.Length; row.PlanText = fp.Describe(); row.PeakOutsideFine = res.PeakOutsideFine ?? "";
            if (lastLc is null || lastR is null) { row.Ok = false; row.Message = "主循环没解（" + res.Verdict + "）"; }
            else Fill(row, lastLc, lastR);
            if (res.RadiusTrace.Count == 0 && res.Verdict.Length > 0) row.Message += "　" + res.Verdict;
        }
        catch (Exception ex) { row.Ok = false; row.Message = $"{ex.GetType().Name}：{ex.Message}"; }
        row.Sec = sw.Elapsed.TotalSeconds;
        return row;
    }

    static readonly (string Which, double Disc, string Grade)[] Jobs =
    {
        ("W08", 10, "判决"), ("W08", 10, "导航"), ("W08", 20, "判决"), ("W08", 20, "导航"), ("W06", 10, "判决"), ("W06", 20, "判决"),
    };
    static double FineOf(string which, string grade) => grade == "判决" ? MeshVerify.RequiredFineMmFor(R48NMeshGateTests.Design(which)) : 0.0;

    static Row[] Par(int n, Func<int, Row> f)
    {
        var rows = new Row[n];
        System.Threading.Tasks.Parallel.For(0, n, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 3 }, i => rows[i] = f(i));
        return rows;
    }

    static readonly Lazy<(Row[] Rows, double Min, string Start)> Adaptive = new(() =>
    {
        var sw = Stopwatch.StartNew(); string st = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var rows = Par(Jobs.Length, i => SolveAdaptive(Jobs[i].Which, Jobs[i].Disc, Jobs[i].Grade, FineOf(Jobs[i].Which, Jobs[i].Grade), "自适应", P0()));
        return (rows, sw.Elapsed.TotalMinutes, st);
    });
    static readonly Lazy<(Row[] Rows, double Min, string Start)> Legacy = new(() =>
    {
        var sw = Stopwatch.StartNew(); string st = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var rows = Par(Jobs.Length, i =>
        {
            var d = Floored(Jobs[i].Which, Jobs[i].Disc, P0());
            double rOld = MeshVerify.FineRadiusPlanFor(d, P0(), MeshVerify.LegacyTabLengthFactor).RadiusMm;
            return SolveFixed(Jobs[i].Which, Jobs[i].Disc, Jobs[i].Grade, FineOf(Jobs[i].Which, Jobs[i].Grade), rOld, "改回", P0());
        });
        return (rows, sw.Elapsed.TotalMinutes, st);
    });
    static readonly Lazy<(Row[] Rows, double Min, string Start)> PlusTen = new(() =>
    {
        var ad = Adaptive.Value.Rows;
        var sw = Stopwatch.StartNew(); string st = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var judge = Jobs.Select((j, i) => (j, i)).Where(x => x.j.Grade == "判决").ToArray();
        var rows = Par(judge.Length, k =>
        {
            var (j, i) = judge[k];
            return SolveFixed(j.Which, j.Disc, j.Grade, FineOf(j.Which, j.Grade), ad[i].RadiusMm + 10.0, "终值+10", P0());
        });
        return (rows, sw.Elapsed.TotalMinutes, st);
    });
    static readonly Lazy<Row> NoGrow = new(() => SolveAdaptive("W08", 20, "导航", 0.0, "新初值不放大", P0(), adaptive: false));

    /// <summary>
    /// 审查 R-5／F5（2026-09-23）：P6 那一笔位移 —— 只导航的路（界面自动定厚 fineMm = 0、命令行 --solve 不带 --fine、可行窗口导航支、界面「核算整线」）改前用算例缺省 50，
    /// 改后（求根一侧）用计划初值。同设计、同导航尺寸（BuildCase 缺省 2.0／11），固定半径各解一次：50 对 r₀（W08 53.697、W06 50.513）。
    /// 只是**单次场解**的开 − 关，不是求根（求根交贴限根，根会随网格挪；求根一侧的归因没做，写进不覆盖）。
    /// </summary>
    static readonly (string Which, double Disc)[] NavJobs = { ("W08", 10), ("W08", 20), ("W06", 10), ("W06", 20) };
    static readonly Lazy<(Row[] At50, Row[] AtR0, double Min, string Start)> Nav50 = new(() =>
    {
        var sw = Stopwatch.StartNew(); string st = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        double def = new LineCase().MeshFineRadiusMm;
        var both = Par(2 * NavJobs.Length, i =>
        {
            var (w, disc) = NavJobs[i % NavJobs.Length];
            double r = i < NavJobs.Length ? def : MeshVerify.FineRadiusPlanFor(Floored(w, disc, P0()), P0()).InitialMm;
            return SolveFixed(w, disc, "导航", 0.0, r, i < NavJobs.Length ? "导航缺省50" : "导航计划初值", P0());
        });
        return (both.Take(NavJobs.Length).ToArray(), both.Skip(NavJobs.Length).ToArray(), sw.Elapsed.TotalMinutes, st);
    });

    static string Head(string title, string start)
        => $"{title}\n开跑 {start}　工作树 {HandoverDoc.Root()}　写码 {Sign}　并发 3　机器争用下（另有别的会话在跑编译与测试；负载以实测为准，见实施记录），耗时只作量级、不作判词\n"
         + $"设计：R48NMeshGateTests.Design（W08 = {R48LW08NavDesign.Source}；W06 = {R48LW06FineDesign.Source}），圆盘保温按门 f 的写法（FlangeInsulated、FlangeInsulMm = 10／20、逐片留空）；工艺参数 = DesignInputs 缺省。\n";

    static string RowLine(Row r)
        => $"{r.Which}\t{r.Tag}\t{r.Grade}\t{r.Branch}\t{r.FineMm:0.000}\t{(double.IsNaN(r.R0) ? "—" : r.R0.ToString("0.000"))}\t{r.RadiusMm:0.000}\t{r.Growths}\t{r.Solves}\t{r.Cells}\t{r.Hot:0.000}\t{r.Cold:0.000}\t{r.Flux:0.000}\t{r.Mass:0.0}\t{r.Rounds}\t{r.Sec:0}\t{r.PeakR:0.00}\t{r.Verdict}"
         + (r.Ok && r.Converged ? "" : $"\t**判不了：{r.Message}**");
    const string RowHead = "设计\t标签\t档\t支\t细区mm\t初值r₀\t终值半径\t放大次数\t解次数\t单元\t最热铂高出热偶读数K\t管根低于热偶读数K\t管孔净流入W\t铂重g\t耦合轮\t耗时s(争用下)\t最远热点r\t热点句（终值半径、本网格）";
    static string Full(Row r)
        => $"{r.Which}\t{r.Tag}\t{r.Grade}\t{r.Branch}\t半径 {R(r.RadiusMm)}\t格 {r.Cells}\t最热铂 {R(r.Hot)}\t管根 {R(r.Cold)}\t净流入 {R(r.Flux)}\t铂重 {R(r.Mass)}\t轮 {r.Rounds}\tSHA {r.Sha}\t去行SHA {r.ShaDeLined}";

    static void Write(string name, StringBuilder sb, ITestOutputHelper o)
    {
        string file = DeliverableOut.Stamped(name);
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        o.WriteLine(file); o.WriteLine(sb.ToString());
    }

    // ═══════════════════════════════════════════════ 门 b　自适应门（慢）
    /// <summary>
    /// F7 门 b（**初值覆盖门**；原名「自适应门」，审查 T1 改名）：W08／W06 × 圆盘保温 10／20 的**判决**网格，走生产的加密复算主循环（解 → 核热点 → 盖不住就放大再解）：
    /// 终值半径上的热点句必须为 null（MeshVerify.HotspotVerdict，判法 MeshAdapt.PeakVerdict、PeakMarginMm = 10 原样）、主循环不拒答；印出初值 → 放大 → 终值。
    /// W08 导航两行只印（导航网格不是判决网格）。
    /// 覆盖：W08／W06 现役设计（门 f 输入）的 4 个判决行。
    /// 不覆盖：① **本循环的放大分支**（审查 T1）：这 4 行的初值已盖住报出的热点（峰 31.50 + 10 ≤ 50.513），放大 0 次、只解 1 次；关掉放大（adaptive = false）或改回旧规则 59，
    ///   本门结果按构造相同。放大、重来、上限拒答只由 F7 门 c、门 d、门 d₂（合成）与 F7 门 c′（实场，初值强行给 35）验过。本门要红，须初值 &lt; 峰 + 10，且放大路径失效或到上限拒答。
    ///   ② **#33 的偏近落点**（审查 T2）：最远热点 31.50 取自局部热稳定 LocalStabRMm，它继承 #33 的偏近落点（全格真最小值 r ≈ 66～70，见实施记录 10.4、HANDOVER #33）；
    ///   本门的 null 只对盘区峰、舌区峰和**报出的**局部热稳定位置成立；决 99 修成全格后，按该落点本门会放大（66～70 + 10 &gt; 53.697／50.513），终值会变。
    ///   ③ 别的设计与形状扫描（R48NMeshGateTests 门c 的 25 点列入合并后慢门）；加密阶梯更细的档（本门只跑判决那一档）；
    ///   Solver／保温搜索／可行窗口三处各自的放大循环（它们与本循环共用 GrowFineRadius 与 HotspotVerdict，但循环本身没在慢门里跑）。
    /// 阈值：null（盖住了报出的位置）—— 判法一个数没动。
    /// </summary>
    [Fact]
    public void 门b_自适应门_W08W06盘10盘20判决_终值半径上热点句为null()
    {
        var (rows, min, start) = Adaptive.Value;
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        sb.Append(Head("F7′ F7 门 b 初值覆盖门（判；原名「自适应门」）：W08／W06 × 圆盘保温 10／20 × 判决（W08 另印导航）—— 生产的加密复算主循环跑本档：解 → 核热点 → 盖不住就放大再解", start));
        W($"判法：MeshVerify.HotspotVerdict（= MeshVerify.Run 每档核的那一句；MeshAdapt.PeakVerdict：峰 + PeakMarginMm {MeshAdapt.PeakMarginMm} ≤ 半径）一个数没动。");
        W(T2Caveat);
        W();
        W(RowHead);
        foreach (var r in rows) W(RowLine(r));
        W();
        W("── 细区半径计划（初值 → 放大 → 终值，原样）");
        foreach (var r in rows) W($"{r.Which} {r.Tag} {r.Grade}：{r.PlanText}");
        W();
        W("── 逐片热点位置（终值半径的网格上）　" + T2Caveat);
        foreach (var r in rows) W($"{r.Which} {r.Tag} {r.Grade}：{r.HotspotTable}");
        W();
        W("── 全精度（R 格式）与全量转储 SHA-256（含文字，耗时两处改占位；去行 SHA = 去掉 F7′ 新加的两类记录行）");
        foreach (var r in rows) W(Full(r));
        var gate = rows.Where(r => r.Grade == "判决").ToArray();
        var red = gate.Where(r => !(r.Ok && r.Converged) || r.Verdict != "null（盖住了）" || r.PeakOutsideFine.Length > 0).ToArray();
        W();
        W($"F7 门 b（判决 {gate.Length} 行）：{(red.Length == 0 ? $"终值半径上热点句全为 null（盖住了**报出的**热点位置）、主循环没拒答 ⇒ 过；放大 {gate.Sum(r => r.Growths)} 次，本门未走到放大分支（放大由 F7 门 c′／c／d／d₂ 验）；{T2Caveat}" : $"**红 {red.Length}/{gate.Length}**：" + string.Join("；", red.Select(r => $"{r.Which}{r.Tag} 峰 r={r.PeakR:0.00}、终值 {r.RadiusMm:0.0}：{r.Verdict} {r.PeakOutsideFine} {r.Message}")))}");
        W($"── 本组耗时 {min:0.0} 分钟（争用下量得、不作判词）　{Sign}");
        Write("R48_F7自适应_门b_W08W06判决.txt", sb, _o);
        Assert.True(rows.All(r => r.Ok), "有一行没解出：" + string.Join("；", rows.Where(r => !r.Ok).Select(r => r.Which + r.Tag + r.Grade + "：" + r.Message)));
        Assert.True(red.Length == 0, $"门 b 红 {red.Length}/{gate.Length}");
    }

    // ═══════════════════════════════════════════════ 门 c′　单向门·实场（慢）
    /// <summary>
    /// 门 c′（单向，实场）：生产的加密复算主循环（MeshVerify.RunCore，整线解 = LineRunner.Run）在 W08 盘20 上，**故意把初值给小**（35 mm：盖住圆盘、盖不住舌侧热点），
    /// 起步 2.0 mm、两档（2.0 → 1.0）：第一档解完读到的最远热点 + 10 &gt; 35 ⇒ 按计划放大 ⇒ 从第一档重来 ⇒ 之后每档都盖住；半径序列非降、终值上热点句为 null。
    /// 初值 35 是门为了造「热点在初值外」挑的输入（不是规则里的数；生产初值 = max(盘半径, 孔半径) + ℓ_t = 53.697）。
    /// 覆盖：真实场里「解 → 核 → 放大 → 重来 → 再核」这条路在加密复算主循环上走一遍。不覆盖：Solver／保温搜索／可行窗口三处各自的放大循环（实施记录 10.9-8）；收敛（两档不够判收敛，本门不判收敛）；
    ///   #33 的偏近落点（审查 T2）：读到的最远热点 33.14／31.50 取自 LocalStabRMm（继承 #33，真值 r ≈ 66～70），终值 46 上的 null 只对报出的位置成立，决 99 修好后放大到哪里要重跑。
    /// 阈值：半径序列非降；终值热点句 null；放大次数 ≥ 1（门要有牙：初值确实盖不住）。
    /// </summary>
    [Fact]
    public void 门c实场_W08盘20_初值给小_放大后从第一档重来_半径非降_终值盖住()
    {
        var sw = Stopwatch.StartNew(); string start = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        var p = P0();
        var d = Floored("W08", 20, p);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));
        var plan0 = MeshAdapt.GivenFineRadiusPlan(35.0);
        var fac = MeshVerify.AnalyticCaseFactory(d, p, plan0.RadiusMm, innerR);
        var res = MeshVerify.RunCore(fac, 2.0, plan0, innerR, int.MaxValue, 2, null, CancellationToken.None, (lc, pr, ct) => LineRunner.Run(lc, pr, ct));
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        sb.Append(Head("F7′ 门 c′ 单向门·实场（判）：W08 盘20，加密复算主循环，初值故意给小 35 mm，起步 2.0 mm、两档", start));
        W("每次整线解：h、半径、最远热点 r、处置");
        foreach (var t in res.RadiusTrace) W($"{t.Fine:0.000}	{t.RadiusMm:0.###}	{t.PeakRMm:0.00}	{t.Outcome}");
        W("作废的档（旧半径上解的）：" + string.Join("；", res.DiscardedTiers.Select(t => $"{t.Fine:0.000} mm／半径 {t.RadiusMm:0.###}／{t.Cells} 格／{t.Sec:0} s")));
        W("计划：" + res.RadiusPlan!.Describe());
        W("判词：" + res.Verdict);
        W($"终值半径上热点句：{(res.PeakOutsideFine ?? "null（盖住了）")}　{T2Caveat}");
        var radii = res.RadiusTrace.Select(t => t.RadiusMm).ToArray();
        bool nonDec = radii.Zip(radii.Skip(1), (a, b) => b >= a).All(x => x);
        W($"半径序列 {string.Join(" → ", radii.Select(r => r.ToString("0.###")))}：{(nonDec ? "非降" : "**有降**")}；放大 {res.RadiusPlan.Steps.Length} 次");
        W($"── 耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（争用下量得、不作判词）　{Sign}");
        Write("R48_F7自适应_门c实场_W08盘20_初值给小.txt", sb, _o);
        Assert.True(res.RadiusPlan.Steps.Length >= 1, "初值 35 竟然盖住了热点 —— 门没牙");
        Assert.True(nonDec, "半径序列有降");
        Assert.Null(res.PeakOutsideFine);
        Assert.True(res.RadiusTrace.Count >= 3, "放大后没有从第一档重来");
    }

    // ═══════════════════════════════════════════════ 门 e　改回逐位门（慢）
    /// <summary>
    /// 门 e（改回逐位，对改前树 cff38c6）：本树传改回参数（LegacyTabLengthFactor，旧规则、不放大）在门 f 的 6 行（W08 判决／导航 × 10／20、W06 判决 × 10／20）上各解一次，
    /// 与改前探针档（改前树上生产 = 旧规则）比：半径、格数、三条判据、铂重、轮数（R 格式）逐位相同，全量转储去掉 F7′ 新加的两类记录行之后 SHA-256 相同（「去行对拍证中性」）。
    /// 另印未去行的 SHA（必然不同：多了记录行），证去行确实去掉了东西。
    /// ⚠ 本门的参照是改前树 cff38c6 上的探针档：到合并树上（C2／C3／SEG 等一起动场）必红 —— 那时要在合并树的 F7′ 之前的状态上重出参照（实施记录 §5 待办）；
    ///   本门与门 b 各自断言（审查 F2），红了不盖住门 b。
    /// 覆盖：旧规则半径值（59）＋ 固定半径单次场解上新记录行中性（6 行整线全量转储，去行 SHA）。
    /// 不覆盖（审查 R-2／T6）：改回计划走 MeshVerify.RunCore、Solver.Solve、InsulationSearch 的 attempt 循环、InsulWindow.Measure 四个循环的逐位（RunCore 的改回控制流只由 F7 门 d 合成覆盖；
    ///   另三处没有逐位门）；已知改回**不**复原改前的两处：导航支半径（Solver 没给半径的导航求根、可行窗口导航扫描，改前 50，改回计划给 59 —— 要复原须另开 NavUsesCaseDefaultRadius，F7 门 R1），
    ///   保温搜索与可行窗口的「峰位算不出 ⇒ 判不了」（改前没有这一判，不看 Adaptive；【待决定】）。
    ///   参照 141431 的导航两行是改前探针**强制给 59** 的数，不是改前树生产（改前生产导航用 50）；只有 4 个判决行 = 改前树生产。别的设计；加密阶梯；图纸路径。
    /// 阈值：逐位（R 格式与 SHA）。
    /// </summary>
    [Fact]
    public void 门e_改回逐位门_改回参数六行对改前树逐位_去行SHA相同()
    {
        var (rows, min, start) = Legacy.Value;
        var pre = ReadRefs(new[] { PreProbeFile }, "旧");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        sb.Append(Head("F7′ 门 e 改回逐位门（判）：改回参数（旧规则 max(盘半径, 0.35·|舌长|, 孔半径) + 10、不放大）6 行 对 改前树 cff38c6 的探针档", start));
        W($"参照：{PreProbeFile}（改前树上跑的探针；判决 4 行 = 当时生产的旧规则网格，导航 2 行是探针强制给 59 的数 —— 改前生产的导航用算例缺省 50）。去行 = 去掉「算例.MeshFineRadiusPlan…」与「…Recipe.FineRadiusMm = …」两类记录行（LineRunner 不读它们）。");
        W("覆盖：旧规则半径值（59）＋ 固定半径单次场解上新记录行中性。不覆盖：改回计划走加密复算主循环／Solver／保温搜索／可行窗口四个循环的逐位（实施记录「审查后修改」R-2／T6）。");
        W();
        W("设计\t标签\t档\t半径 改前／本树\t格\t三条判据与铂重\t轮\t去行 SHA 本树\t改前 SHA\t未去行 SHA 本树\t判");
        var bad = new List<string>();
        foreach (var r in rows)
        {
            if (!pre.TryGetValue((r.Which, r.Tag, r.Grade), out var a)) { bad.Add($"{r.Which}{r.Tag}{r.Grade}：改前档里没有这一行"); continue; }
            bool vals = Bits(a.Radius, r.RadiusMm) && a.Cells == r.Cells && Bits(a.Hot, r.Hot) && Bits(a.Cold, r.Cold) && Bits(a.Flux, r.Flux) && Bits(a.Mass, r.Mass) && a.Rounds == r.Rounds;
            bool sha = a.Sha == r.ShaDeLined;
            if (!(vals && sha)) bad.Add($"{r.Which}{r.Tag}{r.Grade}：{(vals ? "" : "数值不逐位；")}{(sha ? "" : "去行 SHA 不同")}");
            W($"{r.Which}\t{r.Tag}\t{r.Grade}\t{R(a.Radius)}／{R(r.RadiusMm)}\t{a.Cells}／{r.Cells}\t{(vals ? "逐位相同" : $"**不同** 改前 {R(a.Hot)}/{R(a.Cold)}/{R(a.Flux)}/{R(a.Mass)} 本树 {R(r.Hot)}/{R(r.Cold)}/{R(r.Flux)}/{R(r.Mass)}")}\t{a.Rounds}／{r.Rounds}\t{r.ShaDeLined}\t{a.Sha}\t{r.Sha}\t{(vals && sha ? "逐位" : "**不同**")}");
        }
        W();
        W("── 全精度（本树改回）");
        foreach (var r in rows) W(Full(r));
        W();
        W($"门 e：{(bad.Count == 0 ? "6／6 逐位（去行之后）⇒ 过；未去行 SHA 与改前都不同 = 新加的记录行确实在转储里" : $"**红 {bad.Count}**：" + string.Join("；", bad))}");
        W($"── 本组耗时 {min:0.0} 分钟（争用下量得、不作判词）　{Sign}");
        Write("R48_F7自适应_门e_改回逐位_对改前树.txt", sb, _o);
        Assert.True(rows.All(r => r.Ok), "有一行没解出");
        Assert.True(rows.All(r => r.Sha != r.ShaDeLined), "未去行与去行 SHA 相同 —— 去行没去掉东西（新字段没进转储？）");
        Assert.True(bad.Count == 0, string.Join("；", bad));
    }

    // ═══════════════════════════════════════════════ 探针 g　初值敏感度（慢，只印）
    /// <summary>
    /// 探针 g（只印）：三条硬判据在细区半径 ∈ {自适应终值, 旧 59, 终值 + 10} 上的差 —— 给「网格无关性」一个数（半径再大 10 mm，三条判据动多少）。
    /// 旧 59 那一列取门 e 的改回行（= 改前树生产，门 e 证逐位）；终值 + 10 那一列在本树固定半径各解一次。
    /// 覆盖：W08／W06 × 10／20 判决 4 行。不覆盖：导航；更大的半径；别的设计。不判（没有跑前写死的门槛；复核容差 0.5 K／0.5 W 只并列印作对照）。
    /// </summary>
    [Fact]
    public void 探针g_初值敏感度_三条硬判据在终值_旧59_终值加10上的差_只印()
    {
        var ad = Adaptive.Value.Rows; var lg = Legacy.Value.Rows; var (p10, min, start) = PlusTen.Value;
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        sb.Append(Head("F7′ 探针 g 初值敏感度（只印）：判决网格上三条硬判据 vs 细区半径 {自适应终值, 旧 59, 终值 + 10}", start));
        W($"对照尺度（不作门槛）：复核容差 管孔净流入 {MeshVerify.NetFluxMeshTolW} W、热侧／冷侧 = 限值 × {MeshVerify.TcMeshTolFrac}（缺省 0.5 K）。");
        W();
        W("设计\t标签\t终值半径\t旧半径\t终值+10\t最热铂K 终值／旧／+10\t管根K 终值／旧／+10\t净流入W 终值／旧／+10\tmax|Δ| 旧−终值 (K/K/W)\tmax|Δ| +10−终值 (K/K/W)\t单元 终值／旧／+10");
        int k = 0;
        for (int i = 0; i < Jobs.Length; i++)
        {
            if (Jobs[i].Grade != "判决") continue;
            var a = ad[i]; var o = lg[i]; var t = p10[k++];
            W($"{a.Which}\t{a.Tag}\t{a.RadiusMm:0.###}\t{o.RadiusMm:0.###}\t{t.RadiusMm:0.###}\t{a.Hot:0.000}／{o.Hot:0.000}／{t.Hot:0.000}\t{a.Cold:0.000}／{o.Cold:0.000}／{t.Cold:0.000}\t{a.Flux:0.000}／{o.Flux:0.000}／{t.Flux:0.000}"
              + $"\t{o.Hot - a.Hot:+0.000;-0.000}/{o.Cold - a.Cold:+0.000;-0.000}/{o.Flux - a.Flux:+0.000;-0.000}\t{t.Hot - a.Hot:+0.000;-0.000}/{t.Cold - a.Cold:+0.000;-0.000}/{t.Flux - a.Flux:+0.000;-0.000}\t{a.Cells}／{o.Cells}／{t.Cells}");
        }
        W();
        W("── 全精度（终值 + 10 那一列）");
        foreach (var r in p10) W(Full(r));
        W($"── 终值+10 这一组耗时 {min:0.0} 分钟（争用下量得、不作判词）　{Sign}");
        Write("R48_F7自适应_探针g_初值敏感度.txt", sb, _o);
        Assert.True(p10.All(r => r.Ok), "终值 + 10 有一行没解出");
    }

    // ═══════════════════════════════════════════════ 开 − 关归因（慢，只印）
    /// <summary>
    /// 开 − 关归因（只印）：W08 判决／导航 × 10／20、W06 判决 × 10／20，三支并列 —— 改前树（cff38c6，= 本树改回，门 e 证逐位）／(1) 固定 40（C4 第一版，作废；数取其归因档）／自适应（本树生产）；
    /// 另一行 W08 盘20 导航「新初值、不放大」（adaptive = false）把「初值」与「放大」两笔位移分开：放大 0 次时它与自适应逐位相同（去掉计划记录行之后）。
    /// 列：三条硬判据、初值／终值半径、放大次数、格数、耦合轮数、耗时（争用下量得、不作判词）。轮数是「段电流二分格彩票」（§0.-19 庚 G2），不作收益。
    /// </summary>
    [Fact]
    public void 归因_开关三支并列_改前树_固定40_自适应_只印()
    {
        var ad = Adaptive.Value.Rows; var lg = Legacy.Value.Rows; var ng = NoGrow.Value;
        var v1 = ReadRefs(V1Files, "新");
        var pre = ReadRefs(new[] { PreProbeFile }, "旧");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        sb.Append(Head("F7′ 开 − 关归因（只印）：改前树 cff38c6 ／ (1) 固定 40（作废）／ 自适应（本树生产），三条硬判据、半径、格数、轮数、耗时", Adaptive.Value.Start));
        W($"改前树 = {PreProbeFile} 的数（本树改回逐位复现，见门 e 档）；(1) 固定 40 = {string.Join("、", V1Files)} 的「新」行（决 29 (1)，与热点检查相顶，作废；数只作对照）。");
        W();
        W("设计\t标签\t档\t半径 改前／(1)／自适应 r₀→终值\t放大\t单元 改前／(1)／自适应\t最热铂K 改前／(1)／自适应\t管根K 改前／(1)／自适应\t净流入W 改前／(1)／自适应\tΔ自适应−改前 (K/K/W)\tΔ(1)−改前 (K/K/W)\t耦合轮 改前／(1)／自适应\t耗时s 改前／(1)／自适应（争用下量得、不作判词）");
        for (int i = 0; i < Jobs.Length; i++)
        {
            var a = ad[i];
            pre.TryGetValue((a.Which, a.Tag, a.Grade), out var p);
            v1.TryGetValue((a.Which, a.Tag, a.Grade), out var q);
            string S(Ref? x, Func<Ref, double> f, string fmt) => x is null ? "—" : f(x).ToString(fmt);
            W($"{a.Which}\t{a.Tag}\t{a.Grade}\t{S(p, x => x.Radius, "0.0")}／{S(q, x => x.Radius, "0.0")}／{a.R0:0.000}→{a.RadiusMm:0.000}\t{a.Growths}\t{(p?.Cells.ToString() ?? "—")}／{(q?.Cells.ToString() ?? "—")}／{a.Cells}"
              + $"\t{S(p, x => x.Hot, "0.000")}／{S(q, x => x.Hot, "0.000")}／{a.Hot:0.000}\t{S(p, x => x.Cold, "0.000")}／{S(q, x => x.Cold, "0.000")}／{a.Cold:0.000}\t{S(p, x => x.Flux, "0.000")}／{S(q, x => x.Flux, "0.000")}／{a.Flux:0.000}"
              + (p is null ? "\t—\t—" : $"\t{a.Hot - p.Hot:+0.000;-0.000}/{a.Cold - p.Cold:+0.000;-0.000}/{a.Flux - p.Flux:+0.000;-0.000}"
                 + (q is null ? "\t—" : $"\t{q.Hot - p.Hot:+0.000;-0.000}/{q.Cold - p.Cold:+0.000;-0.000}/{q.Flux - p.Flux:+0.000;-0.000}"))
              + $"\t{(p?.Rounds.ToString() ?? "—")}／{(q?.Rounds.ToString() ?? "—")}／{a.Rounds}\t{S(p, x => x.Sec, "0")}／{S(q, x => x.Sec, "0")}／{a.Sec:0}");
        }
        W();
        var a20 = ad[Array.FindIndex(Jobs, j => j.Which == "W08" && j.Disc == 20 && j.Grade == "导航")];
        bool same = a20.Growths == 0 && ng.Growths == 0 && Bits(ng.RadiusMm, a20.RadiusMm) && ng.ShaDeLined == a20.ShaDeLined;
        W($"「初值」与「放大」分开（W08 盘20 导航）：新初值不放大 半径 {ng.RadiusMm:0.###}、放大 {ng.Growths} 次、去行 SHA {ng.ShaDeLined}；自适应 半径 {a20.RadiusMm:0.###}、放大 {a20.Growths} 次、去行 SHA {a20.ShaDeLined}"
          + $" ⇒ {(same ? "逐位相同：这一行的位移全部来自「初值」，「放大」那笔为 0" : "**不同**（放大过，或别的原因 —— 逐行看上表）")}");
        W("本树改回（= 改前树，门 e）逐行：" + string.Join("；", lg.Select(r => $"{r.Which}{r.Tag}{r.Grade} 格 {r.Cells} 轮 {r.Rounds} 耗时 {r.Sec:0}s")));
        W();
        // 审查 R-5／F5：导航 50 对计划初值（P6 那一笔；单次场解，同设计、同导航尺寸）
        var (n50, nR0, nMin, nStart) = Nav50.Value;
        W();
        W($"── 导航网格 细区半径 缺省 50 对 计划初值 r₀（审查 R-5／F5：只导航那几条路改前用 50；单次场解开 − 关，不是求根；开跑 {nStart}，{nMin:0.0} 分钟，争用下量得）");
        W("设计\t标签\t半径 50／r₀\t单元 50／r₀\t最热铂K 50／r₀\t管根K 50／r₀\t净流入W 50／r₀\tΔ(r₀−50) (K/K/W)\t整线判据全过 50／r₀（导航网格上的数，不作交付判词）\t耦合轮 50／r₀");
        for (int i = 0; i < n50.Length; i++)
        {
            var a = n50[i]; var b = nR0[i];
            W($"{a.Which}\t{a.Tag}\t{a.RadiusMm:0.###}／{b.RadiusMm:0.###}\t{a.Cells}／{b.Cells}\t{a.Hot:0.000}／{b.Hot:0.000}\t{a.Cold:0.000}／{b.Cold:0.000}\t{a.Flux:0.000}／{b.Flux:0.000}"
              + $"\t{b.Hot - a.Hot:+0.000;-0.000}/{b.Cold - a.Cold:+0.000;-0.000}/{b.Flux - a.Flux:+0.000;-0.000}\t{(a.AllOk ? "全过" : "不全过")}／{(b.AllOk ? "全过" : "不全过")}\t{a.Rounds}／{b.Rounds}"
              + (a.Ok && a.Converged && b.Ok && b.Converged ? "" : $"\t**判不了：{a.Message} {b.Message}**"));
        }
        W("── 全精度（导航 50 与 r₀）");
        foreach (var r in n50.Concat(nR0)) W(Full(r));
        W();
        W("── 全精度（自适应）");
        foreach (var r in ad) W(Full(r));
        W(Full(ng));
        W($"── 自适应组 {Adaptive.Value.Min:0.0} 分钟、改回组 {Legacy.Value.Min:0.0} 分钟（争用下量得、不作判词）　{Sign}");
        Write("R48_F7自适应_开关归因_三支并列.txt", sb, _o);
        Assert.True(ad.All(r => r.Ok) && ng.Ok, "有一行没解出");
        Assert.True(n50.All(r => r.Ok) && nR0.All(r => r.Ok), "导航 50／r₀ 有一行没解出");
    }
}
