using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路 第二件：升温期「温差判据」的**三个候选量同一次解里全算出来** —— 2026-09-16，Opus 5
//
//  用户 2026-09-16 原话（判据口径变更）：
//    「膨胀余量是考量升温时管与法兰舌板的温差(这温差是代替<-+5C的收敛结果)，升温过程法兰与舌板只要通知膨胀多少?」
//    紧接着又定：「升温时不必依照稳态的 ±5 K(可以放大)」
//  ⇒ 升温期一律**不卡 ±5 K**（冷侧/热侧都不卡）；±5 K 只在带玻璃稳态卡。
//
//  只算、只印。不挂 UI、不接判据、不动生产求解代码（用户 09-16：「先把路走通，再挂UI」）。
//  膨胀函数走 Core/PtThermalExpansion.cs（临时拷自 r48_H，L 路一个字没改）。
//  几何与分区一律调**生产件**：FlangePlate.Tangent()／InsideInsulCircle()、FlangeMesher.MaterialFraction()／InClampSegment()
//  （手抄的门守不住手抄的病）。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// ★ 2026-09-17 Opus 5：算法已提成 Core 公开件（FlangeExpansionCalc），这里只是转调，不许第二份实现。
/// </summary>
internal static class FlangeExpansion
{
    public const double TAssemblyC = FlangeExpansionCalc.DefaultTAssemblyC;

    public static List<FlangeExpansionCalc.Column> Columns(ShellMesh m, double[] tC)
        => FlangeExpansionCalc.Columns(m, tC);

    public static double Integrate(IEnumerable<FlangeExpansionCalc.Column> cols, double xLo, double xHi, Func<double, double> f)
        => FlangeExpansionCalc.Integrate(cols, xLo, xHi, f);

    public static double MeanTempOver(IEnumerable<FlangeExpansionCalc.Column> cols, double xLo, double xHi)
        => FlangeExpansionCalc.MeanTempOver(cols, xLo, xHi);

    public static double[] DiscWeights(ShellMesh m, double discRadiusMm, out int fellBackCells)
        => FlangeExpansionCalc.DiscWeights(m, discRadiusMm, out fellBackCells);

    public static double WeightedMean(double[] w, double[] v)
        => FlangeExpansionCalc.WeightedMean(w, v);

    /// <summary>带符号定点串。门 7 钉住。</summary>
    public static string Signed(double v, int width = 0, int digits = 4)
    {
        if (double.IsNaN(v)) return "NaN".PadLeft(width);
        if (double.IsInfinity(v)) return (v > 0 ? "+inf" : "-inf").PadLeft(width);
        string body = Math.Abs(v).ToString("0." + new string('0', digits));
        bool zero = body.All(ch => ch == '0' || ch == '.');
        return ((v < 0 && !zero ? "-" : "+") + body).PadLeft(width);
    }

    public static string Plain(double v, int width = 0, int digits = 4)
        => (double.IsNaN(v) ? "NaN" : v.ToString("0." + new string('0', digits))).PadLeft(width);

    public static double Strain(string grade, double tC, ref ExpansionCoverage worst, ref string worstNote)
        => FlangeExpansionCalc.Strain(grade, tC, ref worst, ref worstNote);
}

/// <summary>
/// ★ 快门（跑得快，进快速套件）：三个积分器本身对不对。2026-09-16，Opus 5。
/// 门槛写死在断言里：解析温度场核到 1e-6 mm；圆盘加权在两档网格（h = 0.5 / 0.25）下 Q1 差 ≤ 1e-4 mm。
/// </summary>
public class R48FlangeExpansionGateTests
{
    private readonly ITestOutputHelper _o;
    public R48FlangeExpansionGateTests(ITestOutputHelper o) { _o = o; }

    private static List<FlangeExpansionCalc.Column> UniformCols(double xLo, double xHi, int n, Func<double, double> tAt)
    {
        var l = new List<FlangeExpansionCalc.Column>();
        double w = (xHi - xLo) / n;
        for (int i = 0; i < n; i++)
        {
            double a = xLo + i * w, b = a + w;
            l.Add(new FlangeExpansionCalc.Column { X0 = a, X1 = b, AreaMm2 = 1, TMeanC = tAt(0.5 * (a + b)) });
        }
        return l;
    }

    [Fact]
    public void 门1_舌长廓线积分_线性被积函数取中点律精确_且区间可切在列中间()
    {
        // 列内取常数（中点律）⇒ 被积函数对 x 线性时**逐列精确**。闭式 ∫(a+b·x)dx。
        const double xLo = -100, xHi = 0; const double a = 1e-3, b = 5e-6;
        foreach (int n in new[] { 8, 97, 1000 })
        {
            var cols = UniformCols(xLo, xHi, n, x => x);          // T̄ 直接当 x 用，f 里再线性映射
            double got = FlangeExpansion.Integrate(cols, xLo, xHi, t => a + b * t);
            double cf = a * (xHi - xLo) + b * (xHi * xHi - xLo * xLo) / 2;
            _o.WriteLine($"列数 {n}　积分 {got:0.000000000} vs 闭式 {cf:0.000000000}");
            Assert.True(Math.Abs(got - cf) <= 1e-9, $"n={n} 差 {Math.Abs(got - cf):E3} mm");
        }
        // 区间切在列中间：取 [−37.5, −12.5]，列宽 25（列边界 −100/−75/−50/−25/0）
        // ⇒ 列 [−50,−25]（中点 −37.5）重叠 12.5、列 [−25,0]（中点 −12.5）重叠 12.5。
        var c4 = UniformCols(xLo, xHi, 4, x => x);
        double part = FlangeExpansion.Integrate(c4, -37.5, -12.5, t => a + b * t);
        double cfPart = 12.5 * (a + b * -37.5) + 12.5 * (a + b * -12.5);
        _o.WriteLine($"切在列中间：{part:0.000000000} vs 手算 {cfPart:0.000000000}");
        Assert.True(Math.Abs(part - cfPart) <= 1e-12, "区间切在列中间时按重叠长度加权");
    }

    [Fact]
    public void 门2_舌长廓线积分_解析温度场走真ε_与细廓线一致到1e6分之1毫米()
    {
        // T(x) = 1100 + 650·(x/100)（x 从 0 到 −100：1100 °C 降到 450 °C），ε 走真的 PtThermalExpansion。
        // 没有闭式（ε 是拟合多项式）⇒ 参考取同一积分器在 200000 列上的值，核**离散误差**已小于 1e-6 mm。
        const double xLo = -100, xHi = 0, tRoot = 1100.0;
        double TAt(double x) => 1100 + 650 * (x / 100);
        var worst = ExpansionCoverage.InRange; string note = "";
        double eRoot = FlangeExpansion.Strain("Pt", tRoot, ref worst, ref note);
        double Run(int n)
        {
            var cols = UniformCols(xLo, xHi, n, TAt);
            var w2 = ExpansionCoverage.InRange; string n2 = "";
            return FlangeExpansion.Integrate(cols, xLo, xHi, t => FlangeExpansion.Strain("Pt", t, ref w2, ref n2) - eRoot);
        }
        double fine = Run(200000);
        foreach (int n in new[] { 400, 2000 })
        {
            double v = Run(n);
            _o.WriteLine($"列数 {n}　Q2 {v:0.000000000} mm（参考 {fine:0.000000000} mm）　差 {Math.Abs(v - fine):E3}");
            Assert.True(Math.Abs(v - fine) <= 1e-6, $"n={n} 离参考 {Math.Abs(v - fine):E3} mm");
        }
        Assert.True(fine < 0, "舌片整体冷于管根 ⇒ Q2 为负（ε 对 T 递增）");
    }

    [Fact]
    public void 门3_管段伸长积分_与第一版探针同一个积分器_余弦场半段闭式到1e6分之1毫米()
    {
        // Q3 = 第一版那个量，积分器复用 RampElongation（同一个 assembly 里的 internal），这里只核它还在、还对。
        const double L = 300.0, A = 1e-3;
        var x = Enumerable.Range(0, 801).Select(i => i * L / 800).ToArray();
        var g = x.Select(v => A * Math.Cos(Math.PI * v / L)).ToArray();
        var (dA, dB) = RampElongation.Halves(x, g, 400);
        double cf = A * L / Math.PI;
        _o.WriteLine($"ΔL_A {dA:0.000000000} / 闭式 +{cf:0.000000000}　ΔL_B {dB:0.000000000} / 闭式 −{cf:0.000000000}");
        Assert.True(Math.Abs(dA - cf) <= 1e-6 && Math.Abs(dB + cf) <= 1e-6, "管段积分器与闭式不符");
    }

    private static (FlangePlate Plate, LineCase Case, DesignSpec Spec) Plate0()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var lc = d.BuildCase(new DesignInputs(), checkRamp: false, emptyTube: true,
                             emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        var p = lc.FlangePlates[0];
        p.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        return (p, lc, d);
    }

    [Fact]
    public void 门4_圆盘加权平均应变_两档网格下Q1差不超过万分之一毫米()
    {
        // 解析温度场 T(x,z) = 1100 − 8·r（r 自管轴起算，mm）—— 盘上 1100→860 °C，量级与实算同。
        // 两档网格 h = 0.5 / 0.25（远近场同步细化，细化半径盖住整片）⇒ Q1 = a·[ε(T管根) − ⟨ε⟩盘] 的差要 ≤ 1e-4 mm。
        var (plate, _, _) = Plate0();
        double a = plate.HoleRadiusMm, R = plate.DiscRadiusMm, tRoot = 1100.0;
        var w0 = ExpansionCoverage.InRange; string n0 = "";
        double eRoot = FlangeExpansion.Strain("Pt", tRoot, ref w0, ref n0);

        (double q1, double eps, int cells, int fb) Run(double h)
        {
            var m = FlangeMesher.Build(plate, 0, h, h, 200.0, 40.0);
            var t = new double[m.CellCount];
            for (int i = 0; i < m.CellCount; i++)
            {
                var c = m.Centroid[i];
                t[i] = 1100 - 8 * Math.Sqrt(c.X * c.X + c.Z * c.Z);
            }
            var w = FlangeExpansion.DiscWeights(m, R, out int fb);
            var wc = ExpansionCoverage.InRange; string nc = "";
            var eps = new double[m.CellCount];
            for (int i = 0; i < m.CellCount; i++) eps[i] = FlangeExpansion.Strain("Pt", t[i], ref wc, ref nc);
            double mean = FlangeExpansion.WeightedMean(w, eps);
            return (a * (eRoot - mean), mean, m.CellCount, fb);
        }
        var A5 = Run(0.5); var A25 = Run(0.25);
        _o.WriteLine($"h=0.50　单元 {A5.cells}　⟨ε⟩盘 {A5.eps:0.000000000}　Q1 {A5.q1:0.0000000} mm　份额退回 {A5.fb} 格");
        _o.WriteLine($"h=0.25　单元 {A25.cells}　⟨ε⟩盘 {A25.eps:0.000000000}　Q1 {A25.q1:0.0000000} mm　份额退回 {A25.fb} 格");
        _o.WriteLine($"两档 Q1 差 {Math.Abs(A5.q1 - A25.q1):E3} mm（门槛 1e-4）");
        Assert.Equal(0, A5.fb);                       // 解析路径有厚度场 ⇒ 份额一格都不许退回形心
        Assert.Equal(0, A25.fb);
        Assert.True(Math.Abs(A5.q1 - A25.q1) <= 1e-4,
            $"两档网格 Q1 差 {Math.Abs(A5.q1 - A25.q1):E3} mm > 1e-4 mm");
    }

    [Fact]
    public void 门5_圆盘份额确实混了分界圆上的格_不是整格0或1()
    {
        // 注入性质的门：若把份额写成整格判定（0/1），下面这条「存在 0 < f < 1 的格」必红。
        var (plate, _, _) = Plate0();
        var m = FlangeMesher.Build(plate, 0, 2.0, 11.0, 50.0, 40.0);
        int partial = 0, inside = 0, outside = 0;
        for (int i = 0; i < m.CellCount; i++)
        {
            double f = FlangeMesher.MaterialFractionInCircle(m, i, plate.DiscRadiusMm);   // 2026-09-18 Fable 5.1：解析路径没有栅格了，份额走圆的精确积分入口（生产 RampSweep 同一入口）
            Assert.False(double.IsNaN(f), $"单元 {i} 拿不到份额（没有厚度场？）");
            if (f > 1e-9 && f < 1 - 1e-9) partial++; else if (f >= 1 - 1e-9) inside++; else outside++;
        }
        _o.WriteLine($"单元 {m.CellCount}：整格在盘内 {inside}　整格在盘外 {outside}　**分界圆上混合** {partial}");
        Assert.True(partial > 0, "分界圆上一个混合格都没有 ⇒ 份额没生效（整格口径）");
    }

    [Fact]
    public void 门6_舌盘分界与压接入口取的是生产几何_不是手抄()
    {
        var (plate, lc, d) = Plate0();
        double xTan = plate.Tangent().X;
        double xClampEntry = plate.TabTipXMm + lc.Base.BusbarClampLengthMm;
        _o.WriteLine($"切点 x = {xTan:0.####} mm（盘半径 {plate.DiscRadiusMm}、舌半宽 {plate.TabEndHalfWidthMm}）　"
                   + $"舌尖 x = {plate.TabTipXMm:0.####} mm　压接长 {lc.Base.BusbarClampLengthMm} mm　压接入口 x = {xClampEntry:0.####} mm");
        // 切点与 DesignSpec 的同一个式子一致（两处不许劈叉）
        Assert.Equal(d.TangentXMm(), xTan, 9);
        // 压接入口正是 InClampSegment 的边界：入口内侧不在压接段，外侧在
        Assert.False(FlangeMesher.InClampSegment(xClampEntry + 1e-6, plate.TabTipXMm, lc.Base.BusbarClampLengthMm, plate.TwoTabs));
        Assert.True(FlangeMesher.InClampSegment(xClampEntry - 1e-6, plate.TabTipXMm, lc.Base.BusbarClampLengthMm, plate.TwoTabs));
        Assert.True(xClampEntry < xTan, "压接入口必须在切点外侧（−x 方向）");
    }

    [Fact]
    public void 门7_带符号数的印法_四舍五入到零一律印正号_不许印出两个符号()
    {
        // 病历（2026-09-16 Opus 5）：第一次实跑 ..._232307.txt 的「900 °C／夹头 450／片 1 的 Q2离δ」那一格印成了
        //   **-+0.0000** —— .NET 的分段格式 "+0.0000;-0.0000" 挑段看原值符号、印的却是舍入后的数，负零那一格两个符号都上了。
        // 这一条把**本来会出事的那些值**逐个钉住；同时印出分段格式在同样输入下的结果，两者一比就看得见差别。
        double[] bad = { -0.0, 0.0, -4e-7, -4e-5, -0.00004999, 4e-7,
                         0.1 - 0.10000400000000001, 0.05 - 0.05000000001 };
        foreach (double v in bad)
        {
            string mine = FlangeExpansion.Signed(v, 0, 4);
            string dotnet = v.ToString("+0.0000;-0.0000");
            _o.WriteLine($"值 {v:E3}　本档印法 [{mine}]　.NET 分段格式 [{dotnet}]");
            Assert.Equal("+0.0000", mine);                                  // 舍入到 0 ⇒ 一律 +0.0000
            Assert.False(mine.Contains("-+") || mine.Contains("+-"), "印出了两个符号");
            Assert.Equal(1, mine.Count(ch => ch == '+' || ch == '-'));      // 恰好一个符号
        }
        // 真正有值的数照常带符号；宽度补齐不改内容
        Assert.Equal("-0.2530", FlangeExpansion.Signed(-0.25301, 0, 4));
        Assert.Equal("+0.0187", FlangeExpansion.Signed(0.018653, 0, 4));
        Assert.Equal("  -0.2530", FlangeExpansion.Signed(-0.25301, 9, 4));
        Assert.Equal("NaN", FlangeExpansion.Signed(double.NaN));
        Assert.Equal("0.0509", FlangeExpansion.Plain(0.050851, 0, 4));
        Assert.Equal("NaN", FlangeExpansion.Plain(double.NaN));
    }
}

/// <summary>
/// ★★★★★ 慢探针：**升温轨迹上，三个候选量同一次解里全算出来**（2026-09-16，Opus 5）。
///
/// 算例／轨迹／停机口径与第一版探针（R48RampTubeElongationProbeTests）**完全一致**：
///   <c>DesignSpec.Builtin[0]</c> 改成两段 1150/1080 °C、各 300 mm、开箱网格，EmptyTube = true，不加热容项（准静态）；
///   SetpointC 全线依次 = 300/450/600/750/900/1000/1080/1150 °C，ClampTempC = 100 °C（主），≥600 °C 的点再跑一遍 450 °C；
///   停机放大倍数走**上一步修好的口径**（LineRunner.EndTempFixedPointAmpOf：当场算 1 + ℓt/Δx，两端取最大），不是写死的 ×25。
///
/// ══════════ 跑前判读（写在跑之前；不许跑完改判读去迁就结果） ══════════
/// K1 **本轮不下过／不过的总结论**。三个量都印，每个量只印 |Q| 与 δ = 0.1 mm、δ/2 = 0.05 mm 的**距离**
///    （距离 = 限值 − |Q|，正 = 还有裕度，负 = 已越过）。**判据取哪一个由用户看数定** —— 探针不替他定。
///    δ = 0.1 mm 是用户 2026-09-15 的经验估值（原话「是我拍脑袋的值」），不是推导值。
/// K2 覆盖：任一取值温度 &gt; 1000 °C ⇒ 覆盖 = 外推（纯铂膨胀数据只到 1000 °C），该量结论写
///    **「判不了：纯铂膨胀数据只到 1000 °C，外推带宽未接」**，三个量的**数值照印**。判不了不当过、也不当不过。
/// K3 判不了名单（任一命中 ⇒ 该点该片／该段判不了）：Ok = false；外层耦合未收敛；越熔点；
///    该片场未收敛（FlangeOut.FieldsConverged = false）；保温分界／压接几何判不了
///    （InsulBoundaryUndetermined / ClampCoversHole / ClampIntoDisc）；管解超散热表上限；膨胀无数据。
/// K4 量级自检（物理把关人跑前预估）：Q1 ≈ **0.005 mm**；Q2 ≈ **−0.6 ～ −0.9 mm**；
///    Q3 = **0.02 ～ 0.051 mm**（第一版实测，..._205421.txt / ..._224415.txt）。
///    ⇒ 实测与预估差 **3 倍以上先怀疑探针**（跨度取法、面积/厚度加权、ε 符号、单位 mm），**不怀疑物理**。
/// K5 升温期**不卡 ±5 K**（用户 2026-09-16：「升温时不必依照稳态的 ±5 K(可以放大)」）——
///    本档一个 ±5 K 的判定都不做；±5 K 只在带玻璃稳态卡（本轮不跑带玻璃）。
/// K6 法兰圆盘与舌板**本身不判**，只报告各自膨胀多少 mm（用户：「升温过程法兰与舌板只要通知膨胀多少?」）。
/// ══════════════════════════════════════════════════════════════════
///
/// 输出：deliverable\R48_L_升温三候选量探针_本次开跑于2026-09-16_HHMMSS.txt（每点算完立刻落盘）
///       + 同目录 ..._场转储_... .txt（每片的 x 列廓线原样，复核用）。
/// </summary>
[Trait("速度", "慢")]
public class R48RampThreeCandidateProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48RampThreeCandidateProbeTests(ITestOutputHelper o) { _o = o; }

    private const string Grade = "Pt";                          // 用户 2026-09-15：纯铂是设计标准
    private static readonly double[] Trajectory = { 300, 450, 600, 750, 900, 1000, 1080, 1150 };
    private const double ClampMainC = 100.0, ClampAltC = 450.0, ClampAltFromC = 600.0;
    private const double DeltaMm = RampElongation.DeltaMm;       // 0.1 mm
    private const double HalfDeltaMm = RampElongation.HalfDeltaMm;

    private static DesignSpec Case0()
    {
        var d = DesignSpec.Builtin[0].Clone();                  // ⚠ 必须 Clone：Builtin[0] 是 static 实例
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        return d.Fit();
    }

    private static string Cov(ExpansionCoverage c) => RampElongation.CoverageText(c);

    /// <summary>距离 = 限值 − |量|（正 = 还有裕度）。</summary>
    private static double Gap(double limit, double v) => limit - Math.Abs(v);

    // 印法只有一份（FlangeExpansion.Signed／Plain），门 7 核的就是它 —— 探针这里只是转调，不另写。
    private static string S(double v, int width = 0, int digits = 4) => FlangeExpansion.Signed(v, width, digits);
    private static string U(double v, int width = 0, int digits = 4) => FlangeExpansion.Plain(v, width, digits);

    /// <summary>某个量的极值记录（只记极值与出处，不下判定）。</summary>
    private sealed class Peak
    {
        public double Abs = -1, Value, GapDelta, GapHalf;
        public string Where = "—", Cover = "—";
        public void Offer(double v, double gapD, double gapH, string where, string cover)
        {
            if (double.IsNaN(v) || Math.Abs(v) <= Abs) return;
            Abs = Math.Abs(v); Value = v; GapDelta = gapD; GapHalf = gapH; Where = where; Cover = cover;
        }
    }

    [Fact]
    public void 升温轨迹_三个候选量与报告量()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_升温三候选量探针_本次开跑于{stamp}.txt");
        string dump = Path.Combine(dir, $"R48_L_升温三候选量探针_场转储_本次开跑于{stamp}.txt");
        var sw = Stopwatch.StartNew();

        var d0 = Case0();
        var probe0 = d0.BuildCase(new DesignInputs(), checkRamp: false, emptyTube: true,
                                  emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        var plate0 = probe0.FlangePlates[0];
        double xTan0 = plate0.Tangent().X;
        double xTip0 = plate0.TabTipXMm;
        double clampLen0 = probe0.Base.BusbarClampLengthMm;
        double xClampEntry0 = xTip0 + clampLen0;
        double aOuter0 = probe0.TubeIdMm * 0.5 + probe0.WallMm;

        var head = new StringBuilder();
        head.AppendLine("R48 L 路　升温期温差判据的**三个候选量**（同一次解里全算出来）　探针输出");
        head.AppendLine($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-16 Opus 5");
        head.AppendLine("测试 Pt_Optimize.Tests/R48RampFlangeExpansionProbeTests.cs（本档判读写在跑之前，跑完不许改判读）");
        head.AppendLine();
        head.AppendLine("── 用户 2026-09-16 原话（判据口径变更）");
        head.AppendLine("「膨胀余量是考量升温时管与法兰舌板的温差(这温差是代替<-+5C的收敛结果)，升温过程法兰与舌板只要通知膨胀多少?」");
        head.AppendLine("紧接着又定：「升温时不必依照稳态的 ±5 K(可以放大)」");
        head.AppendLine("⇒ **升温期一律不卡 ±5 K**（冷侧／热侧都不卡），温差可以远大于 5 K，由膨胀差 ≤ δ 去兜底；±5 K 只在带玻璃稳态卡。");
        head.AppendLine("  本档一个 ±5 K 的判定都不做。带玻璃稳态本轮不跑。");
        head.AppendLine();
        head.AppendLine("── 主会话解读（标明是解读，不是用户原话）");
        head.AppendLine("· 升温期的硬判据 = **管与法兰圆盘／舌板之间的温差**换算成的**膨胀差** ≤ δ（δ = 0.1 mm，用户经验估值，工程师可改）。");
        head.AppendLine("  这个温差判据在升温期**代替**稳态的 ±5 K（±5 K 是稳态收敛判据，升温期不适用）。");
        head.AppendLine("· **法兰圆盘与舌板本身不判**，只报告各自膨胀多少 mm，给现场安装用。");
        head.AppendLine("· 同一句话有三个可能的特征长度，算出来差 140 倍，**由用户看数定**。本轮任务就是把三个都算出来。");
        head.AppendLine();
        head.AppendLine("── 算例（从 DesignSpec.Builtin[0] 读，不手抄）");
        head.AppendLine($"设计名「{d0.Name}」　管壁 {d0.WallMm} mm　内径 {d0.TubeIdMm} mm　管保温 {d0.TubeInsulMm} mm");
        head.AppendLine($"段数 {d0.SegmentCount}　片数 {d0.FlangeCount}　段长 {string.Join("/", d0.SegLengthMm)} mm　"
                      + $"设计记录控温点 {string.Join("/", d0.SetpointC)} °C（本探针会把它全线改写成轨迹点）");
        head.AppendLine($"盘半径 {d0.DiscRadiusMm} mm　舌长 {d0.TabLengthMm} mm　舌半宽 {d0.TabHalfWidthMm} mm　"
                      + $"板厚 {string.Join("/", d0.TabThickMm)} mm　舌保温 {string.Join("/", d0.TabInsulMm)} mm");
        head.AppendLine("网格：开箱（LineCase 默认导航网格）。工况：空管（EmptyTube = true）。不加热容项（准静态）。");
        head.AppendLine("停机口径：上一步改好的生产口径 —— 放大倍数当场算 1 + ℓt/Δx（LineRunner.EndTempFixedPointAmpOf，两端取最大），不是写死 ×25。");
        head.AppendLine();
        head.AppendLine("── 几何跨度（一律从生产件读：FlangePlate.Tangent()／TabTipXMm、DesignInputs.BusbarClampLengthMm）");
        head.AppendLine($"管外半径 a = 内径/2 + 管壁 = {aOuter0:0.###} mm　　盘半径 R盘 = {plate0.DiscRadiusMm:0.###} mm");
        head.AppendLine($"切点（舌根）x = {xTan0:0.###} mm　　压接入口 x = 舌尖 + 压接长 = {xTip0:0.###} + {clampLen0:0.###} = {xClampEntry0:0.###} mm　　舌尖 x = {xTip0:0.###} mm");
        head.AppendLine($"⇒ Q2 的积分跨度 [{xClampEntry0:0.###}, {xTan0:0.###}]，长 {Math.Abs(xTan0 - xClampEntry0):0.###} mm。");
        head.AppendLine("⚠ **本算例舌半宽 = 盘半径（都是 30 mm）⇒ 切点落在 x = 0**（FlangePlate.Tangent() 的等宽舌公式 −√(R²−半宽²)）。");
        head.AppendLine("  也就是说「舌根」在管轴平面上，Q2 的跨度把 −x 那半个圆盘也算进去了。这不是探针取错跨度，是这个几何本身如此");
        head.AppendLine("  （PlateCurrent2D.UnderDiscInsulation 的注释记过同一件事：「−x 那半个圆盘被算成舌片」）。");
        head.AppendLine("  若用户认为「舌根」该取盘缘（x = −R盘 = −30），跨度只剩 70 mm ⇒ Q2 会小一截。**两种取法的数本档都印**（Q2 与 Q2′）。");
        head.AppendLine();
        head.AppendLine("── 三个候选量的口径（工单原文照录，实现见测试源码）");
        head.AppendLine("Q1 焊缝处径向失配 = a·[ε(T管根) − ⟨ε⟩盘]；a = 管外半径（从设计读）。");
        head.AppendLine("   ⟨ε⟩盘 = 圆盘 ε 按**格子有料面积 × 板厚**加权平均；**分界圆（r ≤ R盘）上的格按 MaterialFraction 份额混合，不按整格**");
        head.AppendLine("   （份额走生产件 FlangeMesher.MaterialFraction + FlangePlate.InsideInsulCircle，分子分母同一张栅格）。");
        head.AppendLine("Q2 舌板长度方向的膨胀差 = ∫(舌根→压接入口)[ε(T舌(s)) − ε(T管根)]ds；T舌(s) = 该 x 列上按**单元面积**加权的平均温度。");
        head.AppendLine("   Q2′ = 同一式子但舌根取盘缘 x = −R盘（对照，供用户选跨度）。");
        head.AppendLine("Q3 管段内不均匀伸长（第一版那个，现降为参考）= max(|∫(接头A→控温点)[ε(T(x))−ε(T设定)]dx|, |∫(控温点→接头B)[…]dx|)。");
        head.AppendLine("   积分器与第一版**同一个**（RampElongation.Halves），梯形权重、端节点半格，控温点在段中点。");
        head.AppendLine();
        head.AppendLine($"── 报告量（用户要的「通知膨胀多少」，**不判**）　T装 = {FlangeExpansion.TAssemblyC:0} °C（写死在本档，工程师可改）");
        head.AppendLine("盘外缘径向伸长 = R盘·[⟨ε⟩盘 − ε(T装)]　　舌板长度方向伸长 = ∫(舌根→压接入口)[ε(T舌) − ε(T装)]ds");
        head.AppendLine("压接段伸长 = L夹·[ε(T夹) − ε(T装)]（T夹 = 压接段格子按面积加权的平均温度，FlangeMesher.InClampSegment 圈格）");
        head.AppendLine("管段总伸长 = ∫段[ε(T(x)) − ε(T装)]dx（逐段）");
        head.AppendLine("舌尖相对管轴总径向位移 = ∫(x = 0 → 舌尖)[ε(T̄(x)) − ε(T装)]dx —— 沿舌轴从管轴平面积到舌尖，含盘段、舌板段与压接段。");
        head.AppendLine("⚠ **这四个报告量不许相加**：舌尖总位移是**一条路径**上的积分，= 盘段 + 舌板段 + 压接段（本档每片当场核到 1e-9 mm，逐片印在明细行里）；");
        head.AppendLine("  而「盘外缘径向伸长」是圆盘**自己沿半径**的集总胀量 R盘·[⟨ε⟩盘 − ε(T装)]，**不是这条路径上的一段**。");
        head.AppendLine($"  本算例切点在 x = {xTan0:0.###}（舌半宽 = 盘半径）⇒ 盘段长 {Math.Abs(xTan0):0.###} mm ⇒ 舌尖总位移恰好 = 舌板段 + 压接段；");
        head.AppendLine("  四个相加会多算一份盘外缘径向伸长（约 0.36 mm，1150 °C 时）。");
        head.AppendLine();
        head.AppendLine("── 温差本身（用户说的「温差」，直接看得到）");
        head.AppendLine("T管根 − ⟨T⟩盘（⟨T⟩盘 与 ⟨ε⟩盘 同一套权重：面积 × 板厚 × 份额）");
        head.AppendLine("T管根 − ⟨T⟩舌（⟨T⟩舌 = 廓线在 Q2 跨度上的**长度加权**平均温度）");
        head.AppendLine();
        head.AppendLine("── 跑前判读（照抄测试注释，跑完不许改）");
        head.AppendLine($"K1 **本轮不下过／不过的总结论**。三个量都印，每个量只印 |Q| 与 δ = {DeltaMm:0.###} mm、δ/2 = {HalfDeltaMm:0.###} mm 的距离");
        head.AppendLine("   （距离 = 限值 − |Q|，正 = 还有裕度，负 = 已越过）。**判据取哪一个由用户看数定** —— 探针不替他定。");
        head.AppendLine("   δ 是用户 2026-09-15 的经验估值（原话「是我拍脑袋的值」），不是推导值。");
        head.AppendLine("K2 覆盖：任一取值温度 > 1000 °C ⇒ 覆盖 = 外推（纯铂膨胀数据只到 1000 °C）⇒ 该量结论写");
        head.AppendLine("   「判不了：纯铂膨胀数据只到 1000 °C，外推带宽未接」，三个量的**数值照印**。判不了不当过、也不当不过。");
        head.AppendLine("   ⚠ 覆盖**分两本帐，不合帐**：判据（Q1/Q2/Q3）一本，报告量（含 ε(T装 = 20 °C)）另一本。");
        head.AppendLine("     理由：20 °C 落在「端部·定义点内插」这一档（ε(0) = 0 是定义值，0–100 °C 线性），合帐会把本来「区间内」的 Q1/Q2 一律拉成端部内插。");
        head.AppendLine("K3 判不了名单：Ok = false／外层耦合未收敛／越熔点／该片场未收敛／保温分界或压接几何判不了／管解超散热表上限／膨胀无数据。");
        head.AppendLine("K4 量级自检（物理把关人跑前预估）：Q1 ≈ 0.005 mm；Q2 ≈ −0.6 ～ −0.9 mm；Q3 = 0.02 ～ 0.051 mm（第一版实测）。");
        head.AppendLine("   差 3 倍以上**先怀疑探针**（跨度取法、面积/厚度加权、ε 符号、单位 mm），不怀疑物理。");
        head.AppendLine("K5 升温期不卡 ±5 K（用户 2026-09-16）——本档一个 ±5 K 的判定都不做。");
        head.AppendLine("K6 法兰圆盘与舌板本身不判，只报告各自膨胀多少 mm。");
        head.AppendLine();
        head.AppendLine("── 快门（跑得快，进快速套件）：R48FlangeExpansionGateTests 六条，门槛写死在断言里");
        head.AppendLine("  门1 廓线积分线性被积函数中点律精确（1e-9）、区间可切在列中间；门2 解析温度场走真 ε 与 20 万列参考一致到 1e-6 mm；");
        head.AppendLine("  门3 管段积分器（与第一版同一个）余弦场半段闭式到 1e-6 mm；门4 圆盘加权在 h = 0.5/0.25 两档网格下 Q1 差 ≤ 1e-4 mm；");
        head.AppendLine("  门5 分界圆上确实有 0 < 份额 < 1 的混合格（写成整格判定必红）；门6 切点与压接入口取的是生产几何（与 DesignSpec.TangentXMm、InClampSegment 对齐）；");
        head.AppendLine("  门7 带符号数的印法：四舍五入后是 0 一律印「+0.0000」，不许印出「-+0.0000」这种两个符号的串。");
        head.AppendLine();
        head.AppendLine("── 印法（2026-09-16 Opus 5）：带符号的数自己拼符号，不用 .NET 的分段格式 \"+0.0000;-0.0000\"。");
        head.AppendLine("  分段格式挑哪一段看**原值**符号，印出来的却是**四舍五入后**的数 ⇒ 负零那一格会印成「-+0.0000」。");
        head.AppendLine("  本档第一次实跑（..._232307.txt，900 °C／夹头 450／片 1 的「Q2离δ」那一格）就印出了这个串，已改掉并由门 7 钉住。");
        head.AppendLine();
        File.WriteAllText(file, head.ToString(), new UTF8Encoding(true));
        File.WriteAllText(dump, $"R48 L 路 升温三候选量探针　场转储（每片按 x 列的廓线：x 区间 / 有料面积 / 面积加权平均温度）　"
                              + $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　2026-09-16 Opus 5\r\n", new UTF8Encoding(true));

        var tabPlate = new StringBuilder();     // 表 ①：每点每片 —— Q1 / Q2 / 温差
        var tabRep = new StringBuilder();       // 表 ②：每点每片 —— 报告量
        var tabSeg = new StringBuilder();       // 表 ③：每点每段 —— Q3 与管段总伸长
        tabPlate.AppendLine("设定点 夹头 片 │ T管根   ⟨T⟩盘   ⟨T⟩舌    T夹    │ T根−⟨T⟩盘 T根−⟨T⟩舌 │ Q1        Q2         Q2′        │ Q1离δ    Q1离δ/2   Q2离δ    Q2离δ/2  │ 覆盖     结论");
        tabRep.AppendLine("设定点 夹头 片 │ 盘外缘径向伸长 舌板长度伸长 压接段伸长  舌尖相对管轴总径向位移 │ 覆盖");
        tabSeg.AppendLine("设定点 夹头 段 │ T根A     T根B    │ ΔL_A      ΔL_B      Q3(max|半段|) │ Q3离δ    Q3离δ/2  │ 管段总伸长 │ 覆盖     结论");

        var pkQ1 = new Peak(); var pkQ2 = new Peak(); var pkQ2b = new Peak(); var pkQ3 = new Peak();
        int pointNo = 0, total = Trajectory.Length + Trajectory.Count(t => t >= ClampAltFromC);
        int plateRows = 0, segRows = 0;
        foreach (double tSet in Trajectory)
            foreach (double clamp in tSet >= ClampAltFromC ? new[] { ClampMainC, ClampAltC } : new[] { ClampMainC })
            {
                pointNo++;
                var ptSw = Stopwatch.StartNew();
                var d = Case0();
                d.ClampTempC = clamp;
                var p = new DesignInputs();
                var lc = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
                double rampTargetBefore = lc.RampTargetC;
                lc.SetpointC = Enumerable.Repeat(tSet, lc.SetpointC.Length).ToArray();
                Assert.Equal(rampTargetBefore, lc.RampTargetC);          // RampTargetC 不许被动
                Assert.All(lc.ClampTempC, v => Assert.Equal(clamp, v));
                Assert.Equal(clamp, lc.Base.BusbarClampTempC);

                int rounds = 0; double lastRemain = double.NaN, lastDelta = double.NaN, lastRes = double.NaN;
                lc.CoupleTrace = (r, _, delta, resK, remain, _, _, _)
                    => { rounds = r; lastDelta = delta; lastRes = resK; lastRemain = remain; };

                LineResult res; string runErr = "";
                try { res = LineRunner.Run(lc); }
                catch (Exception ex) { res = new LineResult { Ok = false, Message = ex.Message }; runErr = ex.GetType().Name; }
                ptSw.Stop();

                string fieldBad = !res.Ok ? $"Ok=false（{res.Message}{(runErr.Length > 0 ? " / " + runErr : "")}）"
                                : !res.Converged ? $"外层耦合未收敛（{rounds} 轮，剩余误差估计 {res.CoupleRemainK:0.000} K，容差 {lc.CoupleTolK} K）"
                                : res.OverMelt ? "越过铂熔点" : "";
                double ampProd = LineRunner.EndTempFixedPointAmpOf(lc, res);

                var sb = new StringBuilder();
                sb.AppendLine($"# 点 {pointNo}/{total}　设定点 {tSet:0} °C　夹头 {clamp:0} °C　"
                            + $"电流 {(res.Segments.Length > 0 ? string.Join("/", res.Segments.Select(s => s.CurrentA.ToString("0"))) : "—")} A　"
                            + $"耦合 {rounds} 轮（步长 {lastDelta:0.0000} K、真残差 {lastRes:0.0000} K、判定放大 {ampProd:0.000}、"
                            + $"剩余误差估计 {(double.IsNaN(res.CoupleRemainK) ? lastRemain : res.CoupleRemainK):0.000} K / 容差 {lc.CoupleTolK} K）　"
                            + $"收敛 {res.Converged}　越熔点 {res.OverMelt}　耗时 {ptSw.Elapsed.TotalSeconds:0.0} s"
                            + (fieldBad.Length > 0 ? $"　★ 场无效：{fieldBad}" : ""));

                // ══ 每片：Q1 / Q2 / 温差 / 报告量
                for (int j = 0; j < res.Flanges.Length; j++)
                {
                    var fo = res.Flanges[j];
                    var plate = lc.FlangePlates[Math.Min(j, lc.FlangePlates.Length - 1)];
                    double R = plate.DiscRadiusMm;
                    double aOuter = lc.TubeIdMm * 0.5 + lc.WallMm;
                    double xTan = plate.Tangent().X, xTip = plate.TabTipXMm;
                    double clampLen = lc.Base.BusbarClampLengthMm;
                    double xClampEntry = xTip + clampLen;

                    string bad = fieldBad;
                    if (bad.Length == 0 && fo.Mesh is null) bad = "这一片没有网格（Mesh = null）";
                    else if (bad.Length == 0 && (fo.TField.Length != fo.Mesh!.CellCount)) bad = $"温度场与网格对不上（{fo.TField.Length}/{fo.Mesh.CellCount}）";
                    else if (bad.Length == 0 && !fo.FieldsConverged) bad = "这一片的场没收敛（FieldsConverged = false）：" + fo.FieldNote;
                    else if (bad.Length == 0 && fo.InsulBoundaryUndetermined) bad = "保温分界判不了（InsulBoundaryUndetermined）";
                    else if (bad.Length == 0 && fo.ClampCoversHole) bad = "压接段盖到管孔（ClampCoversHole）";
                    else if (bad.Length == 0 && fo.ClampIntoDisc) bad = "压接段伸进圆盘（ClampIntoDisc）";
                    if (bad.Length > 0)
                    {
                        tabPlate.AppendLine($"{tSet,6:0} {clamp,4:0} {j} │ 判不了：{bad}");
                        tabRep.AppendLine($"{tSet,6:0} {clamp,4:0} {j} │ 判不了：{bad}");
                        sb.AppendLine($"  片 {j}「{fo.Name}」判不了：{bad}");
                        plateRows++;
                        continue;
                    }

                    var mesh = fo.Mesh!;
                    var tF = fo.TField;
                    var cov = ExpansionCoverage.InRange; string covNote = "";

                    // ── Q1：焊缝处径向失配
                    double eRoot = FlangeExpansion.Strain(Grade, fo.TRootC, ref cov, ref covNote);
                    var wDisc = FlangeExpansion.DiscWeights(mesh, R, out int fellBack);
                    var epsCell = new double[mesh.CellCount];
                    for (int i = 0; i < mesh.CellCount; i++) epsCell[i] = FlangeExpansion.Strain(Grade, tF[i], ref cov, ref covNote);
                    double epsDisc = FlangeExpansion.WeightedMean(wDisc, epsCell);
                    double tDiscMean = FlangeExpansion.WeightedMean(wDisc, tF);
                    double q1 = aOuter * (eRoot - epsDisc);

                    // ── Q2：舌板长度方向的膨胀差（两种舌根取法）
                    var cols = FlangeExpansion.Columns(mesh, tF);
                    double q2 = FlangeExpansion.Integrate(cols, xClampEntry, xTan, t => FlangeExpansion.Strain(Grade, t, ref cov, ref covNote) - eRoot);
                    double q2b = FlangeExpansion.Integrate(cols, xClampEntry, -R, t => FlangeExpansion.Strain(Grade, t, ref cov, ref covNote) - eRoot);
                    double tTabMean = FlangeExpansion.MeanTempOver(cols, xClampEntry, xTan);

                    // ── 报告量（**覆盖单独记**：报告量要用 ε(T装=20 °C)，那一档是「端部·定义点内插」，
                    //    若混进判据那一份覆盖里，会把本来「区间内」的 Q1/Q2 一律拉成端部内插 —— 两件事不许合帐）
                    var covRep = cov; string covRepNote = covNote;
                    double eAsm = FlangeExpansion.Strain(Grade, FlangeExpansion.TAssemblyC, ref covRep, ref covRepNote);
                    double repDisc = R * (epsDisc - eAsm);
                    double repTab = FlangeExpansion.Integrate(cols, xClampEntry, xTan, t => FlangeExpansion.Strain(Grade, t, ref covRep, ref covRepNote) - eAsm);
                    // T夹：压接段格子按面积加权（圈格走生产件 InClampSegment）
                    double aClamp = 0, atClamp = 0;
                    for (int i = 0; i < mesh.CellCount; i++)
                        if (FlangeMesher.InClampSegment(mesh.Centroid[i].X, xTip, clampLen, plate.TwoTabs))
                        { aClamp += mesh.Area[i]; atClamp += mesh.Area[i] * tF[i]; }
                    double tClampMean = aClamp > 1e-12 ? atClamp / aClamp : double.NaN;
                    double repClamp = double.IsNaN(tClampMean) ? double.NaN
                                    : clampLen * (FlangeExpansion.Strain(Grade, tClampMean, ref covRep, ref covRepNote) - eAsm);
                    double repTipRadial = FlangeExpansion.Integrate(cols, xTip, 0.0, t => FlangeExpansion.Strain(Grade, t, ref covRep, ref covRepNote) - eAsm);
                    // ★ 路径分段核对（2026-09-16 Opus 5）：舌尖相对管轴的总位移是**一条路径**上的积分，
                    //   它 = 盘段 + 舌板段 + 压接段（都按路径积分）。「盘外缘径向伸长」是圆盘**自己沿半径**的集总胀量，
                    //   **不是这条路径上的一段** —— 把四个报告量相加会重复计（本算例切点在 x = 0，盘段长 0，
                    //   相加会多算 R盘·[⟨ε⟩盘 − ε装] ≈ 0.36 mm）。这里把恒等式当场核到 1e-9 mm，并把三段都印出来。
                    double repDiscPath = FlangeExpansion.Integrate(cols, xTan, 0.0, t => FlangeExpansion.Strain(Grade, t, ref covRep, ref covRepNote) - eAsm);
                    double repClampPath = FlangeExpansion.Integrate(cols, xTip, xClampEntry, t => FlangeExpansion.Strain(Grade, t, ref covRep, ref covRepNote) - eAsm);
                    double pathResid = repTipRadial - (repDiscPath + repTab + repClampPath);
                    Assert.True(Math.Abs(pathResid) <= 1e-9,
                        $"点 {pointNo} 片 {j}：舌尖总位移 ≠ 盘段 + 舌板段 + 压接段，残差 {pathResid:E3} mm");

                    string verdict = cov == ExpansionCoverage.NoData ? "判不了：膨胀无数据"
                                   : cov == ExpansionCoverage.Extrapolated ? "判不了：纯铂膨胀数据只到 1000 °C，外推带宽未接"
                                   : cov == ExpansionCoverage.ShapeUntrusted ? "判不了：拟合形状不可信"
                                   : "本轮不下过/不过（判据取哪一个由用户定）";
                    if (fellBack > 0) verdict += $"（⚠ {fellBack} 格拿不到份额，退回形心整格）";

                    string whereP = $"{tSet:0}/{clamp:0} 片{j}";
                    pkQ1.Offer(q1, Gap(DeltaMm, q1), Gap(HalfDeltaMm, q1), whereP, Cov(cov));
                    pkQ2.Offer(q2, Gap(DeltaMm, q2), Gap(HalfDeltaMm, q2), whereP, Cov(cov));
                    pkQ2b.Offer(q2b, Gap(DeltaMm, q2b), Gap(HalfDeltaMm, q2b), whereP, Cov(cov));
                    tabPlate.AppendLine($"{tSet,6:0} {clamp,4:0} {j} │ {U(fo.TRootC, 7, 2)} {U(tDiscMean, 7, 2)} {U(tTabMean, 7, 2)} {U(tClampMean, 7, 2)} │ "
                                      + $"{S(fo.TRootC - tDiscMean, 9, 3)} {S(fo.TRootC - tTabMean, 9, 3)} │ "
                                      + $"{S(q1, 9)} {S(q2, 10)} {S(q2b, 10)} │ "
                                      + $"{S(Gap(DeltaMm, q1), 9)} {S(Gap(HalfDeltaMm, q1), 9)} "
                                      + $"{S(Gap(DeltaMm, q2), 9)} {S(Gap(HalfDeltaMm, q2), 9)} │ "
                                      + $"{Cov(cov),-8} {verdict}");
                    tabRep.AppendLine($"{tSet,6:0} {clamp,4:0} {j} │ {S(repDisc, 14)} {S(repTab, 12)} "
                                    + $"{S(repClamp, 11)} {S(repTipRadial, 22)} │ {Cov(covRep)}");
                    plateRows++;

                    sb.AppendLine($"  片 {j}「{fo.Name}」T管根 {fo.TRootC:0.00} °C　⟨T⟩盘 {tDiscMean:0.00} °C　⟨T⟩舌 {tTabMean:0.00} °C　T夹 {tClampMean:0.00} °C");
                    sb.AppendLine($"        ε(T管根) {eRoot:0.00000000}　⟨ε⟩盘 {epsDisc:0.00000000}　ε(T装={FlangeExpansion.TAssemblyC:0}) {eAsm:0.00000000}　"
                                + $"a {aOuter:0.###} mm　R盘 {R:0.###} mm　单元 {mesh.CellCount}　列 {cols.Count}　份额退回 {fellBack} 格");
                    sb.AppendLine($"        Q1 {S(q1, 0, 6)} mm　Q2 {S(q2, 0, 6)} mm（跨度 [{xClampEntry:0.###}, {xTan:0.###}]，长 {Math.Abs(xTan - xClampEntry):0.###} mm）　"
                                + $"Q2′ {S(q2b, 0, 6)} mm（跨度 [{xClampEntry:0.###}, {-R:0.###}]，长 {Math.Abs(-R - xClampEntry):0.###} mm）");
                    sb.AppendLine($"        报告量：盘外缘径向伸长 {S(repDisc)} mm　舌板长度伸长 {S(repTab)} mm　"
                                + $"压接段伸长 {S(repClamp)} mm　舌尖相对管轴总径向位移 {S(repTipRadial)} mm　（覆盖 {Cov(covRep)}）");
                    sb.AppendLine($"        路径分段核对（**报告量不许相加**）：舌尖总位移 {S(repTipRadial, 0, 6)} = 盘段 {S(repDiscPath, 0, 6)}（长 {Math.Abs(xTan):0.###} mm）"
                                + $" + 舌板段 {S(repTab, 0, 6)} + 压接段(路径积分) {S(repClampPath, 0, 6)}，残差 {pathResid:E2} mm（门槛 1e-9）；"
                                + $"报告用的压接段是集总式 L夹·[ε(T夹)−ε(T装)] = {S(repClamp, 0, 6)}；"
                                + $"「盘外缘径向伸长」{S(repDisc, 0, 6)} 是圆盘自己沿半径的集总胀量，**不在这条路径上**，加进去会重复计。");
                    if (covNote.Length > 0) sb.AppendLine($"        覆盖注记（判据 Q1/Q2）：{Cov(cov)}　{covNote}");

                    var dsb = new StringBuilder();
                    dsb.AppendLine($"── 设定点 {tSet:0} °C　夹头 {clamp:0} °C　片 {j}「{fo.Name}」　T管根 {fo.TRootC:0.####} °C　"
                                 + $"单元 {mesh.CellCount}　列 {cols.Count}　切点 x {xTan:0.####}　压接入口 x {xClampEntry:0.####}　舌尖 x {xTip:0.####}");
                    dsb.AppendLine("k\tX0_mm\tX1_mm\tArea_mm2\tTmean_C");
                    for (int k = 0; k < cols.Count; k++)
                        dsb.AppendLine($"{k}\t{cols[k].X0:0.######}\t{cols[k].X1:0.######}\t{cols[k].AreaMm2:0.######}\t{cols[k].TMeanC:0.######}");
                    File.AppendAllText(dump, dsb.ToString(), new UTF8Encoding(false));
                }

                // ══ 每段：Q3（第一版那个量）与管段总伸长
                for (int i = 0; i < res.Segments.Length; i++)
                {
                    var s = res.Segments[i];
                    if (s.X.Length < 3 || s.TMetal.Length != s.X.Length)
                    { tabSeg.AppendLine($"{tSet,6:0} {clamp,4:0} {i} │ 判不了：段解没有温场（节点 {s.X.Length}/{s.TMetal.Length}）"); segRows++; continue; }

                    int n = s.X.Length, m = n / 2;
                    double tSeg = s.SetpointC;
                    Assert.Equal(tSet, tSeg, 6);                     // 写回生效了没有
                    var g = RampElongation.StrainDiff(s.TMetal, tSeg, Grade, out var covS, out string covSNote);
                    var (dLA, dLB) = RampElongation.Halves(s.X, g, m);
                    double q3 = Math.Max(Math.Abs(dLA), Math.Abs(dLB));
                    // 管段总伸长（相对 T装）：同一个梯形积分器，被积函数换成 ε(T(x)) − ε(T装)
                    var gAsm = RampElongation.StrainDiff(s.TMetal, FlangeExpansion.TAssemblyC, Grade, out var covA, out _);
                    double totalTube = RampElongation.Integrate(s.X, gAsm, 0, n - 1);
                    // 覆盖分两份：判据（Q3，相对 T设定）走 covS；报告量（管段总伸长，相对 T装）走 covA —— 不合帐。

                    string verdictS = fieldBad.Length > 0 ? "判不了：" + fieldBad
                                    : s.TubeLossTableExceeded ? $"判不了：管解超散热表上限（最高 {s.TubeTMaxC:0} °C / 上限 {s.TubeLossTableHiC:0} °C）"
                                    : covS == ExpansionCoverage.NoData ? "判不了：膨胀无数据"
                                    : covS == ExpansionCoverage.Extrapolated ? "判不了：纯铂膨胀数据只到 1000 °C，外推带宽未接"
                                    : covS == ExpansionCoverage.ShapeUntrusted ? "判不了：拟合形状不可信"
                                    : "本轮不下过/不过（判据取哪一个由用户定）";

                    pkQ3.Offer(q3, Gap(DeltaMm, q3), Gap(HalfDeltaMm, q3), $"{tSet:0}/{clamp:0} 段{i}", Cov(covS));
                    tabSeg.AppendLine($"{tSet,6:0} {clamp,4:0} {i} │ {U(s.TRootAC, 8, 2)} {U(s.TRootBC, 8, 2)} │ "
                                    + $"{S(dLA, 9)} {S(dLB, 9)} {U(q3, 13)} │ "
                                    + $"{S(Gap(DeltaMm, q3), 9)} {S(Gap(HalfDeltaMm, q3), 8)} │ "
                                    + $"{S(totalTube, 10)} │ {Cov(covS),-8} {verdictS}");
                    segRows++;
                    sb.AppendLine($"  段 {i}「{s.Name}」Q3 {U(q3, 0, 6)} mm（ΔL_A {S(dLA, 0, 6)}　ΔL_B {S(dLB, 0, 6)}）　"
                                + $"管段总伸长（相对 T装）{S(totalTube)} mm　覆盖 判据 {Cov(covS)}／报告量 {Cov(covA)}");
                    if (covSNote.Length > 0 && covS >= ExpansionCoverage.Extrapolated)
                        sb.AppendLine($"        覆盖注记（段 {i}）：{covSNote}");
                }
                foreach (var nt in res.Notes.Where(t => t.Contains("分辨率地板") || t.Contains("未收敛")))
                    sb.AppendLine("        生产判词：" + nt);
                sb.AppendLine();
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
                _o.WriteLine(sb.ToString());
                Console.WriteLine(sb.ToString());
            }

        sw.Stop();
        var tail = new StringBuilder();
        tail.AppendLine();
        tail.AppendLine("════════ 表 ① 每点每片：三个候选量里的 Q1 / Q2 与温差（ΔL 单位 mm，印到 0.0001） ════════");
        tail.Append(tabPlate);
        tail.AppendLine();
        tail.AppendLine($"════════ 表 ② 每点每片：报告量（不判，T装 = {FlangeExpansion.TAssemblyC:0} °C） ════════");
        tail.Append(tabRep);
        tail.AppendLine();
        tail.AppendLine("════════ 表 ③ 每点每段：Q3（管段内不均匀伸长，第一版那个量，现降为参考）与管段总伸长 ════════");
        tail.Append(tabSeg);
        tail.AppendLine();
        tail.AppendLine($"════════ 极值一览：**哪个量贴着 δ = {DeltaMm:0.###} mm** —— 只报极值与出处，不下判定（跑前判读 K1） ════════");
        tail.AppendLine("量   最大|值| mm  值           出现在           离δ        离δ/2      覆盖");
        void PeakRow(string name, Peak pk)
            => tail.AppendLine($"{name,-4} {U(pk.Abs, 10)}  {S(pk.Value, 11)}  {pk.Where,-15}  {S(pk.GapDelta, 9)}  {S(pk.GapHalf, 9)}  {pk.Cover}");
        PeakRow("Q1", pkQ1); PeakRow("Q2", pkQ2); PeakRow("Q2′", pkQ2b); PeakRow("Q3", pkQ3);
        tail.AppendLine("（Q1 = 焊缝处径向失配；Q2 = 舌板长度方向膨胀差，舌根取切点；Q2′ = 同式但舌根取盘缘；Q3 = 管段内不均匀伸长。）");
        tail.AppendLine();
        tail.AppendLine($"── 全部 {total} 点跑完（片行 {plateRows}、段行 {segRows}），总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        tail.AppendLine("本轮**不下过／不过的总结论**（跑前判读 K1）：三个量与 δ = 0.1 mm、δ/2 = 0.05 mm 的距离都在表里，判据取哪一个由用户定。");
        tail.AppendLine("判不了不当过、也不当不过。");
        File.AppendAllText(file, tail.ToString(), new UTF8Encoding(false));
        _o.WriteLine(tail.ToString());
        Console.WriteLine(tail.ToString());
        Assert.True(plateRows > 0 && segRows > 0, "一行都没算出来");
    }
}
