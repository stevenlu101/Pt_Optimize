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
//  R48 L 路：升温全程（准静态）每段管的**非均匀伸长**探针 —— 2026-09-16，Opus 5
//
//  只算、只印。不改 UI、不接判据、不动生产求解代码（用户 09-16：「先把路走通，再挂UI」）。
//  膨胀函数来自 Core/PtThermalExpansion.cs —— 那一档是**临时拷自 r48_H** 的（档首注记写了源 SHA-256），
//  L 路一个字没改；H 路合入 r48_M 之后以 H 为准。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// ★ 2026-09-17 Opus 5：算法已提成 Core 公开件（TubeElongation），这里只是转调，不许第二份实现。
/// δ 判据已作废（用户 2026-09-16/17），常量只留给探针旧输出格式。
/// </summary>
internal static class RampElongation
{
    public const double DeltaMm = 0.1;
    public const double HalfDeltaMm = DeltaMm / 2;

    public static double Integrate(double[] x, double[] g, int i0, int i1)
        => TubeElongation.Integrate(x, g, i0, i1);

    public static (double A, double B) Halves(double[] x, double[] g, int m)
        => TubeElongation.Halves(x, g, m);

    public static double[] StrainDiff(double[] tMetalC, double tSetC, string grade,
                                      out ExpansionCoverage worst, out string worstNote)
        => TubeElongation.StrainDiff(tMetalC, tSetC, grade, out worst, out worstNote);

    public static string CoverageText(ExpansionCoverage c)
        => TubeElongation.CoverageText(c);
}

/// <summary>
/// ★ 快门（跑得快，进快速套件）：积分器本身对不对。2026-09-16，Opus 5。
/// 门核的是**探针自己的积分器**（不是生产配方），故直接调 <see cref="RampElongation"/> 的同一个函数。
/// </summary>
public class R48RampElongationGateTests
{
    private readonly ITestOutputHelper _o;
    public R48RampElongationGateTests(ITestOutputHelper o) { _o = o; }

    private static double[] Grid(double lMm, int n)
        => Enumerable.Range(0, n).Select(i => i * lMm / (n - 1)).ToArray();

    [Fact]
    public void 门_线性被积函数_半段与整段都等于闭式值()
    {
        // g(x) = a + b·x ⇒ 梯形积分**精确**。闭式：∫₀^{L/2} = a·L/2 + b·L²/8；∫_{L/2}^{L} = a·L/2 + 3b·L²/8。
        const double L = 300.0; const double a = 1e-4, b = 2e-7;
        foreach (int n in new[] { 21, 401, 801 })
        {
            var x = Grid(L, n); int m = n / 2;
            Assert.Equal(L / 2, x[m], 9);                       // 节点数为奇数 ⇒ 控温点落在中点
            var g = x.Select(v => a + b * v).ToArray();
            var (dA, dB) = RampElongation.Halves(x, g, m);
            double cfA = a * L / 2 + b * L * L / 8;
            double cfB = a * L / 2 + 3 * b * L * L / 8;
            _o.WriteLine($"n={n}　ΔL_A {dA:0.000000000} vs 闭式 {cfA:0.000000000}　ΔL_B {dB:0.000000000} vs 闭式 {cfB:0.000000000}");
            Assert.True(Math.Abs(dA - cfA) <= 1e-6, $"n={n} ΔL_A 差 {Math.Abs(dA - cfA):E3} mm");
            Assert.True(Math.Abs(dB - cfB) <= 1e-6, $"n={n} ΔL_B 差 {Math.Abs(dB - cfB):E3} mm");
            Assert.True(Math.Abs(dA + dB - (a * L + b * L * L / 2)) <= 1e-6, "两半段之和 ≠ 整段闭式");
        }
    }

    [Fact]
    public void 门_余弦被积函数_与闭式一致到1e6分之1毫米_且整段为零而半段不为零()
    {
        // g(x) = A·cos(πx/L)：∫₀^{L/2} = +A·L/π，∫_{L/2}^{L} = −A·L/π ⇒ **整段 0、半段各 ±A·L/π**。
        // 这同时就是工单要的「ΔL_A = +a、ΔL_B = −a 确认整段为 0 而半段非 0」那一例。
        const double L = 300.0; const double A = 1e-3;
        var x = Grid(L, 801); int m = 801 / 2;
        var g = x.Select(v => A * Math.Cos(Math.PI * v / L)).ToArray();
        var (dA, dB) = RampElongation.Halves(x, g, m);
        double cf = A * L / Math.PI;
        _o.WriteLine($"ΔL_A {dA:0.000000000} / 闭式 +{cf:0.000000000}　ΔL_B {dB:0.000000000} / 闭式 −{cf:0.000000000}　整段 {dA + dB:E3}");
        Assert.True(Math.Abs(dA - cf) <= 1e-6, $"ΔL_A 差 {Math.Abs(dA - cf):E3} mm");
        Assert.True(Math.Abs(dB + cf) <= 1e-6, $"ΔL_B 差 {Math.Abs(dB + cf):E3} mm");
        Assert.True(Math.Abs(dA + dB) <= 1e-9, "整段应为 0");
        // 半段判据与整段判据不是一回事：整段 0 也要判**不过**（|半段| = 0.0955 > δ/2 = 0.05）
        Assert.True(Math.Max(Math.Abs(dA), Math.Abs(dB)) > RampElongation.HalfDeltaMm,
            "本例正是「段内抵消」：整段 0，半段越限 ⇒ 硬判必须不过");
    }

    [Fact]
    public void 门_解析温度场造场_走真ε_与细网格参考一致到1e6分之1毫米()
    {
        // T(x) = T设定 + ΔT·cos(πx/L)，ε 走真的 PtThermalExpansion（不是线性化）。
        // 没有闭式（ε 是拟合多项式的非线性函数）⇒ 参考取同一积分器在 20001 节点上的值，核**离散误差**已经小于 1e-6 mm。
        const double L = 300.0, tSet = 900.0, dT = 50.0;
        (double A, double B) Run(int n)
        {
            var x = Grid(L, n);
            var t = x.Select(v => tSet + dT * Math.Cos(Math.PI * v / L)).ToArray();
            var g = RampElongation.StrainDiff(t, tSet, "Pt", out var cov, out _);
            Assert.Equal(ExpansionCoverage.InRange, cov);        // 850–950 °C 全在纯铂 100–1000 °C 数据区间内
            return RampElongation.Halves(x, g, n / 2);
        }
        var fine = Run(20001);
        foreach (int n in new[] { 401, 801 })
        {
            var v = Run(n);
            _o.WriteLine($"n={n}　ΔL_A {v.A:0.000000000}（参考 {fine.A:0.000000000}）　ΔL_B {v.B:0.000000000}（参考 {fine.B:0.000000000}）");
            Assert.True(Math.Abs(v.A - fine.A) <= 1e-6, $"n={n} ΔL_A 离参考 {Math.Abs(v.A - fine.A):E3} mm");
            Assert.True(Math.Abs(v.B - fine.B) <= 1e-6, $"n={n} ΔL_B 离参考 {Math.Abs(v.B - fine.B):E3} mm");
        }
        // 符号：余弦场在 A 半段整体热于设定 ⇒ ΔL_A > 0；B 半段整体冷于设定 ⇒ ΔL_B < 0。ε 递增是前提。
        Assert.True(fine.A > 0 && fine.B < 0, "ε 对 T 递增 ⇒ 热于设定的半段伸长为正");
    }

    [Fact]
    public void 门_空管造算例时控温点会被换成升温目标_探针必须在这之后自己写回()
    {
        // DesignSpec.BuildCase：emptyTube 且默认 EmptyTubeSetpoint.RampTarget 时，lc.SetpointC 被**全线换成 RampTargetC**。
        // 这一条把那个行为钉住 —— 探针若忘了写回，跑出来的每一点都会是 1150 °C 的场，而表头却印着 300 °C。
        var p = new DesignInputs();
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var lcR = d.BuildCase(p, checkRamp: false, emptyTube: true);
        Assert.All(lcR.SetpointC, v => Assert.Equal(lcR.RampTargetC, v));
        var lcA = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
        Assert.Equal(new[] { 1150.0, 1080.0 }, lcA.SetpointC);
        // 探针用的正是 AsGiven 这一支再写回（RampTarget 那一支的工况说明会印「全线取升温目标 1150 °C」，
        // 与写回后的 300 °C 自相矛盾 —— 见探针文件头「与工单的偏离」）。RampTargetC 两支都不动。
        Assert.Equal(1150.0, lcA.RampTargetC);
        Assert.Equal(1150.0, lcR.RampTargetC);
    }
}

/// <summary>
/// ★★★★★ 慢探针：**内置设计 0 沿升温轨迹，每个设定点每段管的非均匀伸长**（2026-09-16，Opus 5）。
///
/// 算例：<c>DesignSpec.Builtin[0]</c>（管壁 0.8 · 留余量）改成两段 1150/1080 °C、各 300 mm、开箱网格
///       （与 K 路门1b、DesignCurrentTests 同一个算例，数从设计表读，不手抄），EmptyTube = true。
/// 轨迹：SetpointC 全线依次 = 300/450/600/750/900/1000/1080/1150 °C；ClampTempC = 100 °C（主）；
///       ≥600 °C 的点再跑一遍 ClampTempC = 450 °C。管腔 kA 走 DesignInputs 默认档。RampTargetC **一字不动**
///       （它喂设计电流与截面 J）。不加热容项（准静态：法兰 τ ≈ 1–2 min ≪ 升温 ~56 h）。
///
/// ══════════ 跑前判读（写在跑之前；不许跑完改判读去迁就结果） ══════════
/// J1 硬判（工单 §1）：max(|ΔL_A|, |ΔL_B|) ≤ δ/2 = 0.05 mm ⇒ 过；> 0.05 mm ⇒ 不过。
///    δ = 0.1 mm 是用户 2026-09-15 的经验估值（原话「是我拍脑袋的值」），不是推导值。
///    ΔL段 = ΔL_A + ΔL_B 并列打印；两半段反号且任一越限时注「段内抵消」——**整段小不算过**。
/// J2 三态（本段温场里有 > 1000 °C 的节点时）：工单 §1 要「|ΔL| ± 外推带宽」判三态，
///    而**带宽函数（LocalAlphaBandE6）不在本次拷入的 PtThermalExpansion 里**（纯铂膨胀数据只到 1000 °C，
///    1000 °C 以上一律 Coverage = 外推）⇒ 带宽这一半缺席 ⇒ 按「判不了不许当过、也不许当不过」，
///    这些点一律记 **判不了：外推带宽未实现**，同时照印 |ΔL| 与 δ/2 的距离，供 H 路带宽接上后复判。
/// J3 判不了名单（任一条命中即该点该段判不了）：外层耦合未收敛；越熔点；管解超散热表上限；
///    结果 Ok = false；膨胀无数据（本次牌号纯铂，不会命中）。
/// J4 量级自检（物理把关人跑前预估，±50 %）：空管**终点**（1150 °C）ΔL ≈ **+0.05 ～ +0.08 mm**；
///    **300–450 °C 段** ≈ **−0.05 ～ −0.08 mm**；带玻璃稳态约 −0.013 mm（本轮不跑带玻璃，只作量级参照）。
///    ⇒ 若实测量级与预估差 **3 倍以上**，先怀疑探针（单位 mm/m、T设定取法、节点站位、ε 符号），**不怀疑物理**。
/// J5 分辨率地板：低温段若段间耦合欠松弛 600 轮不到容差（放大约 80 倍 ⇒ 步长要压到 ≈0.0125 K），
///    记 **判不了：分辨率地板**，**不是热失控**。判词由生产给（LineRunner.ResolutionFloorNote），本档原样印出来。
///    牛顿路（LineRunner.SolveTubeWithDrawsNewton）目前只在保温搜索里，LineRunner.Run 没有开关 ⇒ 真撞上就回报。
/// J6（第二轮改的口径，2026-09-16 Opus 5）：「场有效」这一列按**新的生产停机口径**判 ——
///    放大倍数是当场算的 1 + ℓt/Δx（LineRunner.EndTempFixedPointAmpOf；数值把关人 2026-09-16 定），
///    不再是写死的 ×25。第一版探针（..._205421.txt）那一列走的是 ×25，两版对照见本档每点的「参考」行。
///    ⚠ 第一版的「参考（不进判读）」行按**段控温点温度**取 ℓt；生产按**该端管根温度**取（取端部值），
///      两者差的就是取值温度，本档两个数都印，谁大谁小看得见。
/// ══════════════════════════════════════════════════════════════════
///
/// 输出：deliverable\R48_L_升温管段伸长探针_本次开跑于2026-09-16_HHMMSS.txt（每点算完立刻落盘）
///       + 同目录 ..._场转储_... .txt（每段 X/TMetal 原样，复核用）。
/// </summary>
[Trait("速度", "慢")]
public class R48RampTubeElongationProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48RampTubeElongationProbeTests(ITestOutputHelper o) { _o = o; }

    private const string Grade = "Pt";                          // 用户 2026-09-15：纯铂是设计标准
    private static readonly double[] Trajectory = { 300, 450, 600, 750, 900, 1000, 1080, 1150 };
    private const double ClampMainC = 100.0, ClampAltC = 450.0, ClampAltFromC = 600.0;

    private static DesignSpec Case0()
    {
        var d = DesignSpec.Builtin[0].Clone();                  // ⚠ 必须 Clone：Builtin[0] 是 static 实例
        d.SetpointC = new[] { 1150.0, 1080.0 };                 // 两段三片（与 K 路门1b 同一算例）
        d.SegLengthMm = new[] { 300.0, 300.0 };
        return d.Fit();
    }

    [Fact]
    public void 升温轨迹_每段管非均匀伸长()
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_升温管段伸长探针_本次开跑于{stamp}.txt");
        string dump = Path.Combine(dir, $"R48_L_升温管段伸长探针_场转储_本次开跑于{stamp}.txt");
        var sw = Stopwatch.StartNew();

        var d0 = Case0();
        var head = new StringBuilder();
        head.AppendLine("R48 L 路　升温全程（准静态）每段管的非均匀伸长　探针输出");
        head.AppendLine($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-16 Opus 5");
        head.AppendLine("测试 Pt_Optimize.Tests/R48RampTubeElongationProbeTests.cs（本档判读写在跑之前，跑完不许改判读）");
        head.AppendLine();
        head.AppendLine("── 算例（从 DesignSpec.Builtin[0] 读，不手抄）");
        head.AppendLine($"设计名「{d0.Name}」　管壁 {d0.WallMm} mm　内径 {d0.TubeIdMm} mm　管保温 {d0.TubeInsulMm} mm");
        head.AppendLine($"段数 {d0.SegmentCount}　片数 {d0.FlangeCount}　段长 {string.Join("/", d0.SegLengthMm)} mm　"
                      + $"设计记录控温点 {string.Join("/", d0.SetpointC)} °C（本探针会把它全线改写成轨迹点）");
        head.AppendLine($"盘半径 {d0.DiscRadiusMm} mm　舌长 {d0.TabLengthMm} mm　舌半宽 {d0.TabHalfWidthMm} mm　"
                      + $"板厚 {string.Join("/", d0.TabThickMm)} mm　舌保温 {string.Join("/", d0.TabInsulMm)} mm");
        head.AppendLine("网格：开箱（LineCase 默认导航网格）。工况：空管（EmptyTube = true）。不加热容项（准静态）。");
        head.AppendLine();
        head.AppendLine("── 口径");
        head.AppendLine("ΔL_A = ∫(接头A→控温点)[ε(T(x)) − ε(T设定)]dx，ΔL_B = ∫(控温点→接头B)[…]dx，ΔL段 = ΔL_A + ΔL_B；单位 mm。");
        head.AppendLine("ε 走 PtThermalExpansion.StrainDifference(\"Pt\", T(x), \"Pt\", T设定)（该档临时拷自 r48_H，档首注记有源 SHA-256）。");
        head.AppendLine("梯形权重、端节点半格；控温点在段中点（SegmentSolver 定电流钉的是 t1[n/2]，本次已逐段核对 x[m] = L/2）。");
        head.AppendLine("T设定 = 本段控温点温度 = 本轨迹点（空管升温全线同值）。RampTargetC 一字未动。");
        head.AppendLine();
        head.AppendLine("── 与工单的偏离（一处，写明理由）");
        head.AppendLine("工单说「BuildCase 把 SetpointC 换成 RampTargetC 之后再改回轨迹点」。实际改用 EmptyTubeSetpoint.AsGiven 再写回：");
        head.AppendLine("  走 RampTarget 那一支时，结果 Notes[0] 会印「控温点 300/300 °C（全线取升温目标 1150 °C…）」—— 自相矛盾的一行。");
        head.AppendLine("  两支的 SetpointC 都被探针覆盖成同一个轨迹点，解出来的场逐位相同；差别只在那句说明文字。");
        head.AppendLine("  两支的行为都由快门 R48RampElongationGateTests 钉住。");
        head.AppendLine();
        head.AppendLine("── 跑前判读（照抄测试注释，跑完不许改）");
        head.AppendLine($"J1 硬判：max(|ΔL_A|,|ΔL_B|) ≤ δ/2 = {RampElongation.HalfDeltaMm:0.###} mm ⇒ 过；超出 ⇒ 不过。δ = {RampElongation.DeltaMm:0.###} mm 是用户经验估值，非推导。");
        head.AppendLine("   ΔL段并列印；两半段反号且任一越限时注「段内抵消」——整段小不算过。");
        head.AppendLine("J2 三态：本段温场含 >1000 °C 节点时覆盖类别 = 外推（纯铂膨胀数据只到 1000 °C）。工单 §1 要的外推带宽函数");
        head.AppendLine("   （LocalAlphaBandE6）不在本次拷入的 PtThermalExpansion 里 ⇒ 带宽这一半缺席 ⇒ 这些点记「判不了：外推带宽未实现」，");
        head.AppendLine("   照印 |ΔL| 与 δ/2 的距离供带宽接上后复判。判不了不当过，也不当不过。");
        head.AppendLine("J3 判不了名单：外层耦合未收敛／越熔点／管解超散热表上限／Ok = false／膨胀无数据。");
        head.AppendLine("J4 量级自检（物理把关人跑前预估，±50 %）：终点 1150 °C 约 +0.05～+0.08 mm；300–450 °C 段约 −0.05～−0.08 mm；");
        head.AppendLine("   带玻璃稳态约 −0.013 mm（本轮不跑，只作参照）。差 3 倍以上先怀疑探针（单位、T设定取法、节点站位、ε 符号），不怀疑物理。");
        head.AppendLine("J5 分辨率地板：低温段 600 轮不到容差 ⇒ 记「判不了：分辨率地板」，不是热失控。判词由生产给（ResolutionFloorNote），本档原样印。");
        head.AppendLine("J6（第二轮，2026-09-16 Opus 5 改口径）：「场有效」这一列按**新的生产停机口径**判 —— 放大倍数当场算 1 + ℓt/Δx");
        head.AppendLine("   （LineRunner.EndTempFixedPointAmpOf，两端各算取最大；数值把关人 2026-09-16 定），不再是写死的 ×25。");
        head.AppendLine("   第一版探针（..._205421.txt）那一列走的是 ×25 ⇒ 本档每点的「参考」行把三个数并排印：");
        head.AppendLine("     ① 生产现行（按该端管根温度取 ℓt，两端各一份）　② 第一版参考行的算法（按段控温点温度取 ℓt）　③ 旧口径写死 ×25。");
        head.AppendLine();
        head.AppendLine("── 表（每点每段一行；ΔL 单位 mm，印到 0.0001）");
        head.AppendLine("设定点 夹头 段 │ T根A     T根B     Tmax     Tmin    │ ΔL_A      ΔL_B      ΔL段      max|半段|  离δ/2    │ 覆盖     耦合轮 剩余K  场有效 │ 结论");
        File.WriteAllText(file, head.ToString(), new UTF8Encoding(true));
        File.WriteAllText(dump, $"R48 L 路 升温管段伸长探针　场转储（每段 X/TMetal 原样）　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　2026-09-16 Opus 5\r\n",
                          new UTF8Encoding(true));

        var rows = new List<string>();
        int pointNo = 0, total = Trajectory.Length + Trajectory.Count(t => t >= ClampAltFromC);
        foreach (double tSet in Trajectory)
            foreach (double clamp in tSet >= ClampAltFromC ? new[] { ClampMainC, ClampAltC } : new[] { ClampMainC })
            {
                pointNo++;
                var ptSw = Stopwatch.StartNew();
                var d = Case0();
                d.ClampTempC = clamp;                            // 唯一来源：BuildCase 由它写 Base.BusbarClampTempC 与逐片 ClampTempC
                var p = new DesignInputs();
                var lc = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven);
                double rampTargetBefore = lc.RampTargetC;
                lc.SetpointC = Enumerable.Repeat(tSet, lc.SetpointC.Length).ToArray();   // 全线同值
                Assert.Equal(rampTargetBefore, lc.RampTargetC);  // RampTargetC 不许被动（它喂设计电流/截面 J）
                Assert.All(lc.ClampTempC, v => Assert.Equal(clamp, v));
                Assert.Equal(clamp, lc.Base.BusbarClampTempC);

                int rounds = 0; double lastRemain = double.NaN, lastDelta = double.NaN, lastRes = double.NaN;
                lc.CoupleTrace = (r, _, delta, resK, remain, _, _, _) =>
                { rounds = r; lastDelta = delta; lastRes = resK; lastRemain = remain; };

                LineResult res;
                string runErr = "";
                try { res = LineRunner.Run(lc); }
                catch (Exception ex) { res = new LineResult { Ok = false, Message = ex.Message }; runErr = ex.GetType().Name; }
                ptSw.Stop();

                var sb = new StringBuilder();
                string fieldBad = !res.Ok ? $"Ok=false（{res.Message}{(runErr.Length > 0 ? " / " + runErr : "")}）"
                                : !res.Converged ? $"外层耦合未收敛（{rounds} 轮，剩余误差估计 {res.CoupleRemainK:0.000} K，容差 {lc.CoupleTolK} K）"
                                : res.OverMelt ? "越过铂熔点" : "";
                sb.AppendLine($"# 点 {pointNo}/{total}　设定点 {tSet:0} °C　夹头 {clamp:0} °C　"
                            + $"电流 {(res.Segments.Length > 0 ? string.Join("/", res.Segments.Select(s => s.CurrentA.ToString("0"))) : "—")} A　"
                            + $"耦合 {rounds} 轮（步长 {lastDelta:0.0000} K、真残差 {lastRes:0.0000} K、剩余误差估计 {(double.IsNaN(res.CoupleRemainK) ? lastRemain : res.CoupleRemainK):0.000} K / 容差 {lc.CoupleTolK} K）　"
                            + $"收敛 {res.Converged}　越熔点 {res.OverMelt}　耗时 {ptSw.Elapsed.TotalSeconds:0.0} s"
                            + (fieldBad.Length > 0 ? $"　★ 场无效：{fieldBad}" : ""));
                // ── 参考行（不进判读）：第二轮（2026-09-16 Opus 5）生产的停机放大倍数已经从写死的 ×25 改成当场算 1 + ℓt/Δx。
                //    上面那个「剩余误差估计」现在走的就是新口径。这里把三个数并排印，供与第一版探针（..._205421.txt）逐点对照：
                //      ① 生产现行：SegmentOut.EndAmpA/EndAmpB（按**该端管根温度**取 ℓt，两端各一份，判定取最大）
                //      ② 第一版参考行的算法：1 + DecayLengthMm/Δx（按**段控温点温度**取 ℓt）—— 与 ..._205421.txt 那一行同一个式子
                //      ③ 旧口径：写死 ×25（LineCase.FixedPointAmp）
                double ampProd = LineRunner.EndTempFixedPointAmpOf(lc, res);
                foreach (var s0 in res.Segments)
                {
                    if (s0.X.Length < 2) continue;
                    double dxMm = s0.X[1] - s0.X[0];
                    double ampSet = 1 + s0.DecayLengthMm / Math.Max(1e-9, dxMm);
                    sb.AppendLine($"        参考（不进判读）段「{s0.Name}」：① 生产现行（管根温度取值）T根 {s0.TRootAC:0.00}/{s0.TRootBC:0.00} °C　"
                                + $"β′ {s0.EndBetaAWPerMK:0.0000}/{s0.EndBetaBWPerMK:0.0000} W/(m·K)　ℓt {s0.EndDecayLengthAMm:0.0}/{s0.EndDecayLengthBMm:0.0} mm　"
                                + $"Δx {s0.NodeSpacingMm:0.####} mm　放大 {s0.EndAmpA:0.0}/{s0.EndAmpB:0.0}");
                    sb.AppendLine($"          ② 第一版参考行算法（控温点取值）β {s0.BetaWPerMK:0.0000} W/(m·K)　ℓt {s0.DecayLengthMm:0.0} mm　Δx {dxMm:0.####} mm　"
                                + $"1+ℓt/Δx = {ampSet:0.0} ⇒ 剩余误差估计 {lastDelta * ampSet:0.000} K　│　③ 旧口径写死 ×{LineCase.FixedPointAmp:0} ⇒ {lastDelta * LineCase.FixedPointAmp:0.000} K");
                }
                sb.AppendLine($"        本点判定用的放大倍数（生产，两段四端取最大）= {ampProd:0.000}　⇒ 停机估计 步长 {lastDelta:0.0000} × {ampProd:0.0} = {lastDelta * ampProd:0.000} K，"
                            + $"真残差 {lastRes:0.0000} × {ampProd:0.0} = {lastRes * ampProd:0.000} K，容差 {lc.CoupleTolK} K（两条都要过）");
                foreach (var nt in res.Notes.Where(t => t.Contains("分辨率地板") || t.Contains("未收敛")))
                    sb.AppendLine("        生产判词：" + nt);

                for (int i = 0; i < res.Segments.Length; i++)
                {
                    var s = res.Segments[i];
                    if (s.X.Length < 3 || s.TMetal.Length != s.X.Length)
                    { sb.AppendLine($"{tSet,6:0} {clamp,4:0} {i} │ 段解没有温场（节点 {s.X.Length}/{s.TMetal.Length}） ⇒ 判不了"); continue; }

                    int n = s.X.Length, m = n / 2;
                    double lMm = s.X[n - 1] - s.X[0];
                    bool midOk = Math.Abs(s.X[m] - (s.X[0] + lMm / 2)) <= 1e-6;
                    double tSeg = s.SetpointC;                     // 本段控温点 = 该段 T设定
                    Assert.Equal(tSet, tSeg, 6);                   // 写回生效了没有 —— 表头印 300 而场是 1150 的那种错，在这里拦住
                    var g = RampElongation.StrainDiff(s.TMetal, tSeg, Grade, out var cov, out string covNote);
                    var (dLA, dLB) = RampElongation.Halves(s.X, g, m);
                    double dLSeg = dLA + dLB, worstHalf = Math.Max(Math.Abs(dLA), Math.Abs(dLB));
                    double gap = RampElongation.HalfDeltaMm - worstHalf;   // >0 = 还有裕度
                    bool cancel = dLA * dLB < 0 && worstHalf > RampElongation.HalfDeltaMm;

                    string verdict;
                    if (fieldBad.Length > 0) verdict = "判不了：" + fieldBad;
                    else if (s.TubeLossTableExceeded) verdict = $"判不了：管解超散热表上限（最高 {s.TubeTMaxC:0} °C / 上限 {s.TubeLossTableHiC:0} °C）";
                    else if (cov == ExpansionCoverage.NoData) verdict = "判不了：膨胀无数据";
                    else if (cov == ExpansionCoverage.Extrapolated) verdict = "判不了：外推带宽未实现（J2）";
                    else if (cov == ExpansionCoverage.ShapeUntrusted) verdict = "判不了：拟合形状不可信";
                    else verdict = worstHalf <= RampElongation.HalfDeltaMm ? "过" : "不过";
                    if (cancel) verdict += "（段内抵消：两半段反号）";
                    if (!midOk) verdict += "（⚠ 控温点节点不在段中点，已按该节点位置切）";

                    string row = $"{tSet,6:0} {clamp,4:0} {i} │ {s.TRootAC,8:0.00} {s.TRootBC,8:0.00} {s.TubeTMaxC,8:0.00} "
                               + $"{s.TMetal.Min(),8:0.00} │ {dLA,9:+0.0000;-0.0000} {dLB,9:+0.0000;-0.0000} {dLSeg,9:+0.0000;-0.0000} "
                               + $"{worstHalf,9:0.0000} {gap,9:+0.0000;-0.0000} │ {RampElongation.CoverageText(cov),-8} {rounds,5} "
                               + $"{(double.IsNaN(res.CoupleRemainK) ? lastRemain : res.CoupleRemainK),6:0.000} "
                               + $"{(fieldBad.Length > 0 ? "否" : "是"),-4} │ {verdict}";
                    sb.AppendLine(row);
                    rows.Add(row);
                    if (cov >= ExpansionCoverage.Extrapolated && covNote.Length > 0)
                        sb.AppendLine($"        覆盖注记（段 {i}）：{covNote}");

                    var dsb = new StringBuilder();
                    dsb.AppendLine($"── 设定点 {tSet:0} °C　夹头 {clamp:0} °C　段 {i}「{s.Name}」　T设定 {tSeg:0.###} °C　"
                                 + $"节点 {n}　段长 {lMm:0.###} mm　控温点下标 {m}（x = {s.X[m]:0.####} mm）　"
                                 + $"电流 {s.CurrentA:0.0} A　ΔL_A {dLA:+0.000000;-0.000000}　ΔL_B {dLB:+0.000000;-0.000000}");
                    dsb.AppendLine("i\tX_mm\tTMetal_C\tepsDiff");
                    for (int k = 0; k < n; k++) dsb.AppendLine($"{k}\t{s.X[k]:0.######}\t{s.TMetal[k]:0.######}\t{g[k]:0.000000000E+00}");
                    File.AppendAllText(dump, dsb.ToString(), new UTF8Encoding(false));
                }
                if (res.Notes.Count > 0) sb.AppendLine("        工况说明：" + res.Notes[0]);
                sb.AppendLine();
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
                _o.WriteLine(sb.ToString());
                Console.WriteLine(sb.ToString());
            }

        sw.Stop();
        var tail = new StringBuilder();
        tail.AppendLine($"── 全部 {total} 点跑完，总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        tail.AppendLine("结论逐点写在上表「结论」列；判不了不当过、也不当不过。");
        File.AppendAllText(file, tail.ToString(), new UTF8Encoding(false));
        _o.WriteLine(tail.ToString());
        Console.WriteLine(tail.ToString());
        Assert.True(rows.Count > 0, "一行都没算出来");
    }
}
