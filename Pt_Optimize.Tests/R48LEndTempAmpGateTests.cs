using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：**段间端温不动点的停机放大倍数**口径门 —— 2026-09-16，Opus 5
//
//  病（第一版探针 deliverable\R48_L_升温管段伸长探针_本次开跑于2026-09-16_205421.txt 每点都印了对照）：
//    生产把放大倍数**写死 25**（LineCase.FixedPointAmp），而闭式 1 + ℓt/Δx 本算例实测 77～88。
//    1150 °C／夹头 100 °C 那点：25 口径说「剩余误差 0.946 K < 容差 1 K，已收敛」，
//    闭式口径是 3.039 K > 1 K「没收敛」。**那个「已收敛」是假的。**
//
//  口径（数值把关人 2026-09-16 定）：
//    ρ = 1/(1 + Δx/ℓt)，放大 = 1/(1 − ρ) = 1 + ℓt/Δx；ℓt = √(kA/β′)，k 与 β′ 都取**该端管根温度**处的值
//    （取端部值，不取段均值 —— 收缩最慢的模式在端部）。只用在段间端温那一层。
//
//  这几道门守的是：① 闭式算对（到 1e-9）；② 生产的停机判定**读的就是它**（不是 25）；
//    ③ 把它改回 25 必须**红**；④ 分辨率地板的判词点名、不许判成收敛。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// ★ 快门（进快速套件）。门不手抄生产配方：闭式那道门调的是生产的公开函数
/// （<see cref="SegmentSolver.AxialKAAt"/>／<see cref="SegmentSolver.TubeLossSlopeAt"/>），
/// 停机那道门跑的是生产的 <see cref="LineRunner.SolveTubeWithDraws"/>。
/// </summary>
public class R48LEndTempAmpGateTests
{
    private readonly ITestOutputHelper _o;
    public R48LEndTempAmpGateTests(ITestOutputHelper o) { _o = o; }

    /// <summary>与第一版探针同一个算例（DesignSpec.Builtin[0]，两段三片、各 300 mm），数从设计表读、不手抄。</summary>
    private static DesignSpec Case0()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        return d.Fit();
    }

    private static LineCase BuildCoarse(int nodes)
    {
        var d = Case0();
        d.ClampTempC = 100.0;
        var p = new DesignInputs();
        var lc = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        lc.Base.Nodes = nodes;      // 粗网格：Δx 大 ⇒ 闭式放大远离 25，门才有分辨力，而且跑得快
        return lc;
    }

    // ────────────────────────────────────────────────────────────────────
    //  ① 闭式：放大倍数 = 1 + ℓt/Δx，到 1e-9
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 已知 ℓt/Δx 的算例：ℓt 由生产的两个公开件当场组出（kA = <see cref="SegmentSolver.AxialKAAt"/>，
    /// β′ = <see cref="SegmentSolver.TubeLossSlopeAt"/>），Δx 由节点数定死 ⇒ ℓt/Δx 是**已知数**。
    /// 断言 <see cref="SegmentSolver.EndTempFixedPointAt"/> 给的放大倍数 = 1 + ℓt/Δx 到 1e-9，
    /// 且 = 1/(1 − ρ) 到 1e-9（两种写法是同一件事）。
    /// </summary>
    [Fact]
    public void 门_放大倍数等于1加ℓt除Δx_到1e9分之1()
    {
        var lc = BuildCoarse(401);
        var p = SegmentSolver.Clone(lc.Base);
        p.TubeIdMm = lc.TubeIdMm; p.WallMinMm = lc.WallMm; p.TubeLengthMm = lc.SegLengthMm[0];
        p.TSetC = lc.SetpointC[0];
        double wall = p.WallMinMm * 1e-3;

        foreach (int nodes in new[] { 21, 41, 401, 801 })
            foreach (double tEnd in new[] { 284.26, 600.0, 1118.85, 1218.43 })
            {
                p.Nodes = nodes;
                int n = Math.Max(21, p.Nodes | 1);
                double dx = p.TubeLength / (n - 1);                                   // m
                double area = Math.PI * wall * (p.TubeId + wall);                     // m²
                double kAx = SegmentSolver.AxialKAAt(p, area, tEnd);                  // 生产件
                double beta = SegmentSolver.TubeLossSlopeAt(p, wall, tEnd);           // 生产件
                double lt = Math.Sqrt(kAx / beta);                                    // m
                double want = 1.0 + lt / dx;                                          // **已知的** 1 + ℓt/Δx

                var fp = SegmentSolver.EndTempFixedPointAt(p, wall, tEnd);
                _o.WriteLine($"节点 {n}　T端 {tEnd:0.00} °C　β′ {beta:0.0000} W/(m·K)　ℓt {lt * 1000:0.000} mm　"
                           + $"Δx {dx * 1000:0.0000} mm　1+ℓt/Δx = {want:0.000000000}　函数给 {fp.Amp:0.000000000}　ρ {fp.Rho:0.000000000}");

                Assert.Equal(want, fp.Amp, 9);
                Assert.Equal(1.0 / (1.0 - fp.Rho), fp.Amp, 9);          // 1/(1−ρ) ≡ 1+ℓt/Δx
                Assert.Equal(lt * 1000.0, fp.DecayLengthMm, 9);
                Assert.Equal(dx * 1000.0, fp.DxMm, 9);
                Assert.Equal(beta, fp.BetaWPerMK, 9);
            }
    }

    /// <summary>
    /// 结构关系（不靠某一个数）：Δx 减半 ⇒ (放大 − 1) 加倍，到 1e-9。
    /// 写死 25 这种常数过不了这一条 —— 它根本不随网格动。
    /// </summary>
    [Fact]
    public void 门_Δx减半时放大减一必须加倍()
    {
        var lc = BuildCoarse(401);
        var p = SegmentSolver.Clone(lc.Base);
        p.TubeIdMm = lc.TubeIdMm; p.WallMinMm = lc.WallMm; p.TubeLengthMm = lc.SegLengthMm[0];
        p.TSetC = lc.SetpointC[0];
        double wall = p.WallMinMm * 1e-3, tEnd = 1150.0;

        p.Nodes = 201; double a1 = SegmentSolver.EndTempFixedPointAt(p, wall, tEnd).Amp;   // Δx = L/200
        p.Nodes = 401; double a2 = SegmentSolver.EndTempFixedPointAt(p, wall, tEnd).Amp;   // Δx = L/400
        _o.WriteLine($"Δx = L/200 ⇒ 放大 {a1:0.000000}；Δx = L/400 ⇒ 放大 {a2:0.000000}；(a2−1)/(a1−1) = {(a2 - 1) / (a1 - 1):0.000000000}");
        Assert.Equal(2.0, (a2 - 1) / (a1 - 1), 9);
        Assert.True(Math.Abs(a2 - LineCase.FixedPointAmp) > 5, $"算例选得没分辨力：闭式 {a2:0.0} 太靠近写死的 25");
    }

    /// <summary>端温不是数（整线两头没有邻段那种）⇒ 放大记 NaN，不许悄悄退回一个数。</summary>
    [Fact]
    public void 门_端温不是数时放大记NaN()
    {
        var lc = BuildCoarse(41);
        var p = SegmentSolver.Clone(lc.Base);
        p.TubeIdMm = lc.TubeIdMm; p.WallMinMm = lc.WallMm; p.TubeLengthMm = lc.SegLengthMm[0];
        Assert.True(double.IsNaN(SegmentSolver.EndTempFixedPointAt(p, p.WallMinMm * 1e-3, double.NaN).Amp));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 生产的停机判定读的是它（端到端，跑生产求解）
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 端到端：<see cref="LineRunner.SolveTubeWithDraws"/>（段间端温不动点那一层）的判收敛量
    /// 必须是「残差 × 闭式放大」，不是「残差 × 25」。
    ///
    /// 怎么验而不手抄配方：同一个算例跑两遍，一遍新口径、一遍把开关关掉（= 写死 25），
    /// 两遍的迭代轨迹逐位相同（容差给 0 ⇒ 谁都不提前 break），所以
    ///     判收敛量之比 = 放大之比 = 闭式/25。
    /// 闭式那一半由段解自己报回来（<see cref="SegmentOut.EndAmpA"/>／B），与开关无关。
    /// </summary>
    private void 断言停机判定读闭式放大(bool useClosedForm)
    {
        var lc = BuildCoarse(41);
        var drawLR = new (double L, double R)[lc.SegmentCount];       // 抽热全 0：只解管侧

        lc.EndTempAmpFromDecayLength = useClosedForm;
        var it = LineRunner.SolveTubeWithDraws(lc, drawLR, null, tolK: 0.0, maxRounds: 3);
        lc.EndTempAmpFromDecayLength = false;                        // 参照：写死 25
        var itRef = LineRunner.SolveTubeWithDraws(lc, drawLR, null, tolK: 0.0, maxRounds: 3);

        Assert.NotNull(it.Last); Assert.NotNull(itRef.Last);
        Assert.True(it.Last!.Ok, it.Last.Message);
        Assert.Equal(it.Rounds, itRef.Rounds);

        // 闭式放大：段解两端各报一份，取最慢的那个（= 最大）
        double closed = it.Last.Segments.SelectMany(s => new[] { s.EndAmpA, s.EndAmpB })
                                        .Where(double.IsFinite).Max();
        foreach (var s in it.Last.Segments)
        {
            _o.WriteLine($"段「{s.Name}」　T根 {s.TRootAC:0.00}/{s.TRootBC:0.00} °C　Δx {s.NodeSpacingMm:0.0000} mm　"
                       + $"ℓt {s.EndDecayLengthAMm:0.00}/{s.EndDecayLengthBMm:0.00} mm　β′ {s.EndBetaAWPerMK:0.0000}/{s.EndBetaBWPerMK:0.0000} W/(m·K)　"
                       + $"放大 {s.EndAmpA:0.0000}/{s.EndAmpB:0.0000}");
            // 段带出来的那两个数自己要自洽：放大 = 1 + ℓt/Δx
            Assert.Equal(1.0 + s.EndDecayLengthAMm / s.NodeSpacingMm, s.EndAmpA, 9);
            Assert.Equal(1.0 + s.EndDecayLengthBMm / s.NodeSpacingMm, s.EndAmpB, 9);
        }
        _o.WriteLine($"闭式放大（两段四端取最大）{closed:0.000000}　写死 {LineCase.FixedPointAmp}　"
                   + $"判收敛量 本次 {it.JudgeK:0.000000} / 参照(25) {itRef.JudgeK:0.000000}　比值 {it.JudgeK / itRef.JudgeK:0.000000000}");

        Assert.True(Math.Abs(closed - LineCase.FixedPointAmp) > 5,
                    $"算例选得没分辨力：闭式 {closed:0.0} 太靠近写死的 25");
        Assert.True(itRef.JudgeK > 0, "参照跑的判收敛量必须是正数，否则比值没意义");
        // ★ 核心断言：判收敛量之比 = 闭式 × 余量 / 25。把放大改回写死 25 ⇒ 比值变 1 ⇒ 这一行红。
        // ★ R48 M（2026-09-18，Fable 5.1，改门写明变因）：放大 = max(闭式 × StopAmpHeadroom(1.1), 实测雅可比)；管侧单解这条路没量雅可比 ⇒ 闭式 × 1.1。
        Assert.Equal(closed * LineRunner.StopAmpHeadroom / LineCase.FixedPointAmp, it.JudgeK / itRef.JudgeK, 9);
    }

    [Fact]
    public void 门_段间端温停机判定读的是闭式放大而不是写死25() => 断言停机判定读闭式放大(useClosedForm: true);

    /// <summary>
    /// **注入**：把放大改回写死 25（开关关掉，等于把病打回去）⇒ 上面那道门必须红。
    /// 没有这一条，上面那道门可能只是「碰巧过」。
    /// </summary>
    [Fact]
    public void 门_注入_改回写死25必须红()
    {
        var ex = Assert.ThrowsAny<Exception>(() => 断言停机判定读闭式放大(useClosedForm: false));
        _o.WriteLine("注入（EndTempAmpFromDecayLength = false，等于把写死的 25 接回停机判定）⇒ 门红：");
        _o.WriteLine(ex.Message);
    }

    /// <summary>选择器：两端四个值取**最大**（收缩最慢的那个端说了算）；开关关掉时恰好给 25。</summary>
    [Fact]
    public void 门_放大倍数取两端最大_开关关掉时恰好是25()
    {
        var lc = BuildCoarse(41);
        var r = new LineResult
        {
            Segments = new[]
            {
                new SegmentOut { Name = "HC1", EndAmpA = 40.0, EndAmpB = 88.5 },
                new SegmentOut { Name = "HC2", EndAmpA = 77.25, EndAmpB = double.NaN },
            }
        };
        Assert.Equal(88.5, LineRunner.EndTempFixedPointAmpOf(lc, r), 9);
        lc.EndTempAmpFromDecayLength = false;
        Assert.Equal(LineCase.FixedPointAmp, LineRunner.EndTempFixedPointAmpOf(lc, r), 9);

        // 全 NaN ⇒ 记 NaN，不静默退回 25（NaN 乘残差还是 NaN ⇒ 判不出「已收敛」）
        lc.EndTempAmpFromDecayLength = true;
        var rNaN = new LineResult { Segments = new[] { new SegmentOut { Name = "HC1" } } };
        Assert.True(double.IsNaN(LineRunner.EndTempFixedPointAmpOf(lc, rNaN)));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 分辨率地板：判词点名，不许判成收敛
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 数字取自第一版探针实测的那一点（1150 °C／夹头 100 °C，9 轮：步长 0.0378 K、真残差 0.0001 K、容差 1 K），
    /// 放大取该点的闭式 80.3。这种情形**不是热失控**，判词要点名「判不了：分辨率地板」，并印步长/真残差/轮数。
    /// </summary>
    [Fact]
    public void 门_分辨率地板_判词点名且不许判成收敛()
    {
        string? note = LineRunner.ResolutionFloorNote(0.0378, 0.0001, 1.0, 80.3, 600);
        Assert.NotNull(note);
        _o.WriteLine(note!);
        Assert.Contains("判不了：分辨率地板", note);
        Assert.Contains("0.0378", note);        // 步长
        Assert.Contains("0.0001", note);        // 真残差
        Assert.Contains("600", note);           // 轮数
        Assert.Contains("不是热失控", note);
        Assert.DoesNotContain("收敛", note);    // 判不了就是判不了，一个「收敛」字都不许出现

        // 残差还没进容差 ⇒ 不许拿「分辨率地板」去盖真的不收敛（两边各试一次）
        Assert.Null(LineRunner.ResolutionFloorNote(3.0000, 0.0001, 1.0, 80.3, 600));
        Assert.Null(LineRunner.ResolutionFloorNote(0.0378, 2.0000, 1.0, 80.3, 600));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ④ 源码钉死：判定路上不许再出现写死的 25
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 停机判定那几行必须读 <see cref="LineRunner.EndTempFixedPointAmpOf"/>。
    /// 谁把 <c>LineCase.FixedPointAmp</c> 抄回判定路，这道门立刻红 ——
    /// 而那种改动**不会有任何报错**，只会得到一个「收敛了」的假答案（本项目最怕的错误形态）。
    /// </summary>
    [Fact]
    public void 门_判定路不许再写死25_源码钉死()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));

        // 主环：步长那条与真残差那条**共用** ampWorst
        // ★ R48 M（2026-09-18，Fable 5.1，改门写明变因）：放大改成 max(闭式 × 1.1, 实测雅可比)（StopAmpOf，唯一读口），rEst/(1−rEst) 那一支删了；
        //   守的东西没变（判定路上不许写死 25），只是闭式那一份现在经 StopAmpOf 读。
        Assert.Contains("double ampWorst = StopAmpOf(c, res, ampJac);", s);
        Assert.DoesNotContain("rEst / (1 - rEst)", s);
        // R48 L（2026-09-17，Opus 5）：容差那一侧改成唯一读口 CoupleTolKFor 算出来的 tolNow（本门只管 amp 这一侧不许写死 25）
        // ★ 2026-09-18 Opus 5（合并 J×L 改门，写明变因）：J 路把停机三条判定（步长、真残差、管根 NaN）提成了生产函数 LineRunner.CoupleConverged；
        //   合并时把它的放大与容差改成由调用方传入（tolNow / ampWorst），主环那两行局部变量 resOk 随之取消。
        //   本门原来钉 "bool resOk = resK * ampWorst < tolNow;" 这一行字面，改钉合并后的调用行 —— 守的东西没变（真残差那支必须乘当场算的 ampWorst、比当场算的 tolNow）。
        Assert.Contains("if (CoupleConverged(remain, resK, tolNow, rootNaN, ampWorst))", s);
        Assert.Contains("=> remainK < tolK && resK * amp < tolK && !rootNaN;", s);
        Assert.DoesNotContain("resK * LineCase.FixedPointAmp", s);
        Assert.Contains("double tolNow = CoupleTolKFor(c, res);", s);
        Assert.DoesNotContain("const double ampWorst = LineCase.FixedPointAmp;", s);

        // 基线环／管侧单解（SolveTubeWithDraws 走的那一份）—— R48 M：同一份 StopAmpOf
        Assert.Contains("? baseResid * StopAmpOf(c, br, jacAmp)", s);
        Assert.DoesNotContain("baseResid * LineCase.FixedPointAmp", s);

        // 未收敛那一支的剩余误差估计也不许写死 —— R48 M：同一份 StopAmpOf
        Assert.Contains("double ampNow = StopAmpOf(c, res, ampJac);", s);

        // 分辨率地板判词接在**未收敛那一支里**（不是别处）：位置要落在 if (!res.Converged) 与那一支的收尾之间，
        // 否则「判不了」有可能被挂到一次收敛上 —— 那正是本轮要根治的错误形态。
        int iBranch = s.IndexOf("if (!res.Converged)", StringComparison.Ordinal);
        int iCall = s.IndexOf("ResolutionFloorNote(delta, resKLast, tolLast, ampNow, c.CoupleMaxRounds)", StringComparison.Ordinal);
        int iEnd = s.IndexOf("res.Message = \"段↔法兰耦合未收敛（", StringComparison.Ordinal);
        Assert.True(iBranch > 0 && iCall > 0 && iEnd > 0, $"三个锚点要都找得到：{iBranch}/{iCall}/{iEnd}");
        Assert.True(iBranch < iCall && iCall < iEnd,
                    $"分辨率地板判词没落在未收敛那一支里（if 在 {iBranch}，调用在 {iCall}，收尾在 {iEnd}）");
    }
}
