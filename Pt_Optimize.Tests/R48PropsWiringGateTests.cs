using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 物性接线　**电、热物性（ρ、dρ/dT、电阻温度系数、k、cp）按牌号进求解链** —— 2026-09-23，Opus 5.5
//
//  改了什么：求解链原来直读 Materials.PtResistivity／PtThermalK／PtCp／PtTcr（写死纯铂），现在一律经 PtProps.For(牌号) 取；
//  纯铂一支调的就是那几支原函数（同一运算次序）⇒ 纯铂的数一位都不许动。
//
//  九条门各管什么（每条只证它写的那件事）：
//   1 纯铂默认逐位不变（整线小算例，全量转储的 SHA-256，含文字；按操作系统各一份记录）。
//     不覆盖（整线小算例不经过这些路径）：升温核算那一支（本算例 checkRamp: false）；参考工具页 LineSolver.SizeFlanges → CoupledSolver → PlateThermal2D／PlateCurrent2D；
//     RampScreen.Evaluate／Judge；DesignScreen.DrawBudgetW／JLimitAPerMm2；DesignCurrent.ClosedFormResistanceOhm 的闭式那一支（rf 为空时才走，本算例每片都有场参照）。
//     这几处的纯铂逐位只有门 2 在访问口层证（访问口纯铂一支逐位等于原函数），
//     调用处的运算次序没有逐位门，只经审阅。
//   2 访问口：纯铂一支（与 Tanaka-ZGS-Pt）逐位等于原函数；Pt-Rh/90-10 在 1150 °C 等于差量文件印的数；源码里不读参考热导率、不碰热膨胀。
//   3 段解：选 Pt-Rh/90-10，电阻温度系数、时间常数、热衰减长度、电阻、中点温度、反算电流都跟着变，且变的比例等于物性比。
//   4 壳体电流与温度场：均匀温度下电流分布与牌号无关、发热按 ρ 比变；非均匀温度下电流分布变（σ(T) 按牌号）；逐格发热等于 ρ(T)·J²·t·A 逐位；
//     热解的管孔抽热与总发热随牌号变（不钉方向：最高温在管孔边，方向推不出来，见门里的注）。
//   5 升温准静态与两节点：金属热容比 = cp 比，管电阻比 = ρ 比，管侧翅片导度比 = √(k 比)。
//   6 整线说明：选 Pt-Rh/90-10 时结果说明末尾写明按牌号取、数据点区间与本算例越出之处（区间含升温起止温度：本算例不核升温，
//     但设计电流闭式每轮都从 25 °C 算到目标，25 °C 是真算过的）；段温真的变了。不覆盖：两条失败出口（第一轮失败、耦合中途失败）的说明（只有源码，没有算例）。
//   7 数据不全的牌号五项一起退回纯铂、说明写明缺什么（「持久强度仍按本牌号」只在有持久强度曲线时写）；参考热导率不读；材料库里没有的名字照样抛；
//     参考工具页逐行牌号那一路：片的说明（LineSolver.JointGradeNotes）带出两侧段的退回说明，SizeFlanges 真的赋给了 FlangeResult.GradeNotes（源码字面）。
//     不覆盖：SizeFlanges 端到端（每段一次耦合解约 30 s）、界面把 GradeNotes 印出来（UI 未编译）。
//   8 源码门：Core 里直读纯铂那几支的只剩名单上的例外（逐条有因）；生产 Core 调电流解与闭式电阻的地方都传了牌号。
//   9 整线只有一个牌号来源：LineCase.GradeName 与参数表不一致当场抛。
//  不覆盖：界面（参数表下拉、结果说明的显示）、命令行 Program.cs（它自己的公式仍按纯铂）、判据全体按新物性重跑（另立一轮）。
// ════════════════════════════════════════════════════════════════════════════

public class R48PropsWiringGateTests
{
    private readonly ITestOutputHelper _o;
    public R48PropsWiringGateTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// 门 1 的记录（小写十六进制 SHA-256；全量转储含文字，两处耗时改写成占位）。**记录取自改动前的代码**（a468063 只加本档门 1 的那一版，接线之前跑）。
    /// Linux：2026-09-23 Opus 5.5 在 Linux 镜像（.NET 8）上接线前跑两次逐位相同，接线后再跑仍相同。
    /// Windows：**还没有记录** —— Windows 上本门红并印出本机的 SHA；请在 a468063 上（只加本档）跑出那个数填进来，不要在接线后的树上记（那就成了拿自己比自己）。
    /// </summary>
    private const string LinuxRecord = "13aedf7ee75e1889abed6e9bf75293bd97de3c6ddf8bb9b001e178e7c93cdb45";
    private const string WindowsRecord = "";

    /// <summary>电、热物性按牌号取的牌号（纯铂之外）。口径：MaterialDb.DataCompleteness 的电阻率与热导率／比热两类都算自有。</summary>
    private static readonly string[] PerGradeExpected = { "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10", "Umicore-PtRh10" };

    /// <summary>整线小算例（与 R48EmptyTubeGateTests 的 Quick 同一做法：给定电流、耦合 2 轮）。</summary>
    internal static LineCase QuickCase(string? grade = null)
    {
        var p = new DesignInputs();
        if (grade is not null) p.GradeName = grade;
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var c = d.BuildCase(p, checkRamp: false);
        c.UseMeasuredCurrent = true; c.MeasuredCurrentA = new[] { 1200.0, 1150.0 }; c.CoupleMaxRounds = 2;
        return c;
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    /// <summary>转储里两处耗时（雅可比测量用时：结果成员 JacobianAmpSec 与说明里的「用时 x s」）每跑都变，改写成占位；其余逐字。</summary>
    private static string DumpNoTiming(LineCase lc, LineResult r)
    {
        string full = Regex.Replace(R48LineDumpTests.Dump(lc, r, withText: true), @"(结果\.JacobianAmpSec = )[^\n]*", "$1<耗时>");
        return Regex.Replace(full, @"用时 [0-9.]+ s", "用时 <耗时> s");
    }

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static void RelEq(double exp, double act, double tol, string what)
        => Assert.True(Math.Abs(act - exp) <= tol * Math.Abs(exp), $"{what}：期望 {exp:R}，实际 {act:R}（相对差 {(act - exp) / exp:E3}，容差 {tol:E0}）");

    // ───────────────────────────── 1 ─────────────────────────────
    [Fact]
    public void 门_纯铂默认逐位不变_整线小算例()
    {
        var lc = QuickCase();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineRunner.Run(lc);
        sw.Stop();
        Assert.True(r.Ok, r.Message);
        string sha = Sha(DumpNoTiming(lc, r));
        bool win = OperatingSystem.IsWindows();
        string rec = win ? WindowsRecord : LinuxRecord;
        _o.WriteLine($"纯铂整线小算例：{sw.Elapsed.TotalSeconds:0.0} s　{(win ? "Windows" : "Linux")}　SHA-256 {sha}　记录 {(rec.Length == 0 ? "（无）" : rec)}");
        Assert.DoesNotContain(r.Notes, n => n.Contains("按牌号", StringComparison.Ordinal));   // 纯铂不加牌号说明
        Assert.True(rec.Length > 0,
            $"本机（{(win ? "Windows" : "Linux")}）还没有记录。请在 a468063（接线之前）上只加本档跑本门，把印出的 SHA-256 {sha} 核对后填进 "
            + (win ? "WindowsRecord" : "LinuxRecord") + " —— 不要在接线后的树上记。");
        Assert.Equal(rec, sha);
    }

    // ───────────────────────────── 2 ─────────────────────────────
    [Fact]
    public void 门_访问口_纯铂一支调的就是原函数_逐位()
    {
        Assert.Equal(PtProps.PureGradeName, GradeChoices.DefaultGrade);
        Assert.Same(PtProps.Pure, PtProps.For("Pt"));
        Assert.True(PtProps.Pure.IsPure && !PtProps.Pure.IsFallback && PtProps.Pure.Note.Length == 0);

        var temps = Enumerable.Range(0, 1601).Select(i => (double)i).Concat(new[] { 0.05, 700.05, 1149.9, 1150.1, 1500.5 }).ToArray();
        foreach (var (name, props) in new[] { ("Pt", PtProps.Pure), ("Tanaka-ZGS-Pt", PtProps.For("Tanaka-ZGS-Pt")) })
        {
            int bad = 0; string first = "";
            foreach (double t in temps)
            {
                double drho = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * t);   // 求解链原来内联的那一式
                var pairs = new (string Q, double Exp, double Act)[]
                {
                    ("ρ", Materials.PtResistivity(t), props.Rho(t)), ("dρ/dT", drho, props.DRhoDT(t)),
                    ("电阻温度系数", Materials.PtTcr(t), props.Tcr(t)), ("k", Materials.PtThermalK(t), props.K(t)), ("cp", Materials.PtCp(t), props.Cp(t)),
                };
                foreach (var (q, e, a) in pairs)
                    if (!Same(e, a)) { bad++; if (first.Length == 0) first = $"{q} @ {t} °C：原函数 {e:R}，访问口 {a:R}"; }
            }
            _o.WriteLine($"{name}：{temps.Length} 个温度 × 5 项，与原函数不逐位相同的 {bad} 处" + (first.Length > 0 ? "，第一处 " + first : ""));
            Assert.True(bad == 0, $"{name} 的访问口与 Materials 原函数不逐位相同：{first}");
        }
        Assert.False(PtProps.For("Tanaka-ZGS-Pt").IsPure);   // 它走按牌号那一支（借纯铂的数，算术逐位相同），不是纯铂捷径

        // Pt-Rh/90-10 与纯铂在 1150 °C：等于差量文件（deliverable/R48_M_物性按牌号与纯铂差量_本次开跑于2026-09-18_143953.txt）印出的数，到它印的位数
        var rh = PtProps.For("Pt-Rh/90-10");
        Assert.Equal("4.839881E-07", rh.Rho(1150).ToString("0.000000E+00"));
        Assert.Equal("69.300", rh.K(1150).ToString("0.000"));
        Assert.Equal("160.000", rh.Cp(1150).ToString("0.000"));
        Assert.Equal("4.708026E-07", PtProps.Pure.Rho(1150).ToString("0.000000E+00"));
        Assert.Equal("83.790", PtProps.Pure.K(1150).ToString("0.000"));
        Assert.Equal("164.525", PtProps.Pure.Cp(1150).ToString("0.000"));
        // 按牌号那一支：dρ/dT 解析式对数值微分，电阻温度系数 = dρ/dT ÷ ρ
        double h = 1e-3, num = (rh.Rho(1150 + h) - rh.Rho(1150 - h)) / (2 * h);
        RelEq(num, rh.DRhoDT(1150), 1e-6, "Pt-Rh/90-10 dρ/dT 解析 vs 数值");
        RelEq(rh.DRhoDT(1150) / rh.Rho(1150), rh.Tcr(1150), 1e-15, "Pt-Rh/90-10 电阻温度系数");

        // RangeNote：纯铂 ""；90-10 从 25 °C 起 ⇒ 热导率低于数据点下限 100 °C、取端点 42.5（源自己说常温段不可用，照实写出）
        Assert.Equal("", PtProps.Pure.RangeNote(25, 1150));
        string rn = rh.RangeNote(25, 1150);
        _o.WriteLine("Pt-Rh/90-10 在 25–1150 °C 的区间说明：" + rn);
        Assert.Contains("热导率：本算例最低 25 °C 低于数据点下限 100 °C", rn);
        Assert.Contains("42.5", rn);
        Assert.DoesNotContain("比热", rn);                     // 比热数据点 25–1500 °C，25 °C 在区间内
        // 电阻率测试值疑点段：Pt-Rh/90-10 那一行 100–600 °C 疑为插补（PtResistivityData.Rows）⇒ 区间与它重叠就写出，不重叠不写
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rn);
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rh.RangeNote(300, 1300));
        Assert.Contains("电阻率测试值（Pt-Rh/90-10 行）在 100–600 °C 有疑点", rh.RangeNote(600, 1300));   // 端点相接也算重叠
        Assert.Equal("", rh.RangeNote(700, 1300));

        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "PtProps.cs"));
        Assert.DoesNotContain("ThermalKAdvisory", src);
        Assert.DoesNotContain("PtThermalExpansion", src);
    }

    // ───────────────────────────── 3 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_段解()
    {
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var a = SegmentSolver.SolveAtCurrent(pPt, 1100);
        var b = SegmentSolver.SolveAtCurrent(p90, 1100);
        Assert.True(a.Ok, a.Message); Assert.True(b.Ok, b.Message);
        Assert.False(a.EmptyTube); Assert.Equal(0.0, a.CavityRadKAWmPerK);   // 带玻璃：管腔辐射为 0 ⇒ 热衰减长度只含管壁 k

        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        double ts = pPt.TSetC;
        Assert.True(Same(P.Tcr(ts), a.TcrPerK)); Assert.True(Same(R.Tcr(ts), b.TcrPerK));
        RelEq(R.Cp(ts) / P.Cp(ts), b.TauMetalS / a.TauMetalS, 1e-12, "金属时间常数比 = cp 比");
        RelEq(Math.Sqrt(R.K(ts) / P.K(ts)), b.DecayLengthMm / a.DecayLengthMm, 1e-12, "热衰减长度比 = √(k 比)");
        foreach (var (res, pr, nm) in new[] { (a, P, "Pt"), (b, R, "Pt-Rh/90-10") })
        {
            double tMean = 0; foreach (double t in res.TMetal) tMean += t; tMean /= res.TMetal.Length;
            RelEq(pr.Rho(tMean), res.ResistanceOhm * (res.TubeAreaMm2 * 1e-6) / pPt.TubeLength, 1e-12, nm + " 电阻 × 截面 ÷ 长 = ρ(平均温度)");
        }
        int mid = a.TMetal.Length / 2;
        _o.WriteLine($"同电流 1100 A：中点 {a.TMetal[mid]:0.00} → {b.TMetal[mid]:0.00} °C；时间常数 {a.TauMetalS:0.0} → {b.TauMetalS:0.0} s；"
                   + $"衰减长度 {a.DecayLengthMm:0.00} → {b.DecayLengthMm:0.00} mm；电阻温度系数 {a.TcrPerK:E4} → {b.TcrPerK:E4}");
        Assert.True(b.TMetal[mid] > a.TMetal[mid], "同电流下 ρ 大、k 小的 Pt-Rh/90-10 中点应更热");
        Assert.Contains("按牌号「Pt-Rh/90-10」", b.Note);
        Assert.DoesNotContain("按牌号", a.Note);

        var sa = SegmentSolver.Solve(pPt); var sb = SegmentSolver.Solve(p90);
        Assert.True(sa.Ok, sa.Message); Assert.True(sb.Ok, sb.Message);
        _o.WriteLine($"反算电流（控温点 {ts:0} °C）：Pt {sa.CurrentA:0.0} A → Pt-Rh/90-10 {sb.CurrentA:0.0} A");
        Assert.True(sb.CurrentA < sa.CurrentA, "ρ 大 ⇒ 到同一控温点要的电流更小");
    }

    // ───────────────────────────── 4 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_壳体电流与热场()
    {
        var plate = new FlangePlate    // 与 RemovalPriorityTests 同一块板
        {
            DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
            ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0, TabParallel = false, WeldFilletLegMm = 0,
        };
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        var m = FlangeMesher.Build(plate, 0, 2.0, 8.0, 50.0, pPt.BusbarClampLengthMm, 0, 0);
        int n = m.CellCount;

        // 均匀 1150 °C、参考温度也取 1150 ⇒ σ ≡ 1（x/x）⇒ 电流分布与牌号无关（逐位），发热按 ρ 比
        var uni = Enumerable.Repeat(1150.0, n).ToArray();
        var ca = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, uni, props: P);
        var cb = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, uni, props: R);
        Assert.True(ca.JMagAPerMm2.Zip(cb.JMagAPerMm2).All(z => Same(z.First, z.Second)), "均匀温度下电流分布应与牌号无关（逐位）");
        RelEq(R.Rho(1150) / P.Rho(1150), cb.TotalGenW / ca.TotalGenW, 1e-12, "均匀温度下发热比 = ρ 比");
        Assert.Equal("+2.80", ((cb.TotalGenW / ca.TotalGenW - 1) * 100).ToString("+0.00"));   // 差量文件 1150 °C 电阻率：+2.80 %

        // 非均匀温度场（纯铂热解给的）⇒ σ(T) 按牌号 ⇒ 电流分布变
        var th0 = ShellThermal.Solve(m, ca.JMagAPerMm2, pPt, tRootC: 1150, insulBoundaryX: double.NaN);
        var da = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, th0.T, props: P);
        var db = ShellCurrent.Solve(m, 1000, 1.1e-4, 1150, th0.T, props: R);
        int diff = da.JMagAPerMm2.Zip(db.JMagAPerMm2).Count(z => !Same(z.First, z.Second));
        _o.WriteLine($"非均匀温度场（{th0.TMinC:0}–{th0.TMaxC:0} °C）：{n} 格里电流密度不同的 {diff} 格；Jmax {da.JMaxAPerMm2:0.000} → {db.JMaxAPerMm2:0.000}");
        Assert.True(diff > 0, "非均匀温度下 σ(T) 按牌号，电流分布应该变");

        // 热解：同一份 J、同一管根温度，逐格发热 = ρ(T)·1e3·J²·t·A（与 ShellThermal 汇总那一式逐字相同）
        var ta = ShellThermal.Solve(m, ca.JMagAPerMm2, pPt, tRootC: 1150, insulBoundaryX: double.NaN);
        var tb = ShellThermal.Solve(m, ca.JMagAPerMm2, p90, tRootC: 1150, insulBoundaryX: double.NaN);
        foreach (var (th, pr, nm) in new[] { (ta, P, "Pt"), (tb, R, "Pt-Rh/90-10") })
            for (int i = 0; i < n; i++)
                Assert.True(Same(pr.Rho(th.T[i]) * 1e3 * ca.JMagAPerMm2[i] * ca.JMagAPerMm2[i] * m.Thickness[i] * m.Area[i], th.CellGenW[i]),
                    $"{nm} 第 {i} 格发热不是按本牌号 ρ(T) 算的");
        _o.WriteLine($"热解：最高 {ta.TMaxC:0.00} → {tb.TMaxC:0.00} °C；管孔抽热 {ta.QFromTubeW:0.000} → {tb.QFromTubeW:0.000} W；发热 {ta.QGenW:0.00} → {tb.QGenW:0.00} W");
        // 设计稿原想钉「最高温 90-10 > 纯铂」（推理：发热多、导走少）—— 实跑证伪：这块板最高温就在管孔边（钉在管根 1150 °C 附近），
        //   2026-09-23 Linux 镜像实测 1146.96 → 1146.88 °C。所以不钉方向，只钉「热解真的变了」：管孔抽热与总发热都不同（实测 617.3 → 554.5 W、293.2 → 323.5 W）。
        Assert.NotEqual(ta.QFromTubeW, tb.QFromTubeW);
        Assert.NotEqual(ta.QGenW, tb.QGenW);
    }

    // ───────────────────────────── 5 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_升温两节点与准静态()
    {
        var pPt = new DesignInputs();
        var p90 = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var P = PtProps.Pure; var R = PtProps.For("Pt-Rh/90-10");
        const double t = 900;
        var qa = RampTwoNode.QuasiStaticBreakdown(pPt, 1.0, t, 50);
        var qb = RampTwoNode.QuasiStaticBreakdown(p90, 1.0, t, 50);
        RelEq(R.Cp(t) / P.Cp(t), qb.CapMetalJPerK / qa.CapMetalJPerK, 1e-12, "准静态金属热容比 = cp 比（密度仍纯铂）");
        RelEq(R.Rho(t) / P.Rho(t), qb.RTubeOhm / qa.RTubeOhm, 1e-12, "准静态管电阻比 = ρ 比");
        Assert.Equal(qa.LossW, qb.LossW);                                   // 散热与牌号无关

        // 两节点模型（输入同 R48G2RampClampChannelGateTests 的合成算例 Synthetic(1000, 0.6, 5.0)）
        RampTwoNode.Inputs In() => new()
        {
            WallMm = 1.0, FlangeMassG = 340, FlangeAreaInsulMm2 = 730, FlangeAreaBareMm2 = 4580,
            FlangeResistanceRefOhm = 1.7e-4, FlangeRefTempC = 1090, HoleRadiusMm = 26, PlateEqOuterRadiusMm = 45, FlangeThickMm = 1.2,
            SharedFactor = 1.0, DesignCurrentA = 1000, FromC = 25, TargetC = 1700, MaxHours = 40,
            Mode = RampControl.ConstantCurrent, TabInsulThickMm = 5.0, ClampConductanceWPerK = 0.6, ClampFollowRatio = 0.45, ClampColdEndC = 25,
        };
        var ma = new RampTwoNode.Model(pPt, In());
        var mb = new RampTwoNode.Model(p90, In());
        RelEq(R.Rho(t) / P.Rho(t), mb.RTube(t) / ma.RTube(t), 1e-12, "两节点管电阻比 = ρ 比");
        RelEq(Math.Sqrt(R.K(t) / P.K(t)), mb.GTubeFin(t) / ma.GTubeFin(t), 1e-12, "两节点管侧翅片导度比 = √(k 比)");
        _o.WriteLine($"{t:0} °C：金属热容 {qa.CapMetalJPerK:0.00} → {qb.CapMetalJPerK:0.00} J/K；管电阻 {qa.RTubeOhm:E4} → {qb.RTubeOhm:E4} Ω；"
                   + $"翅片导度 {ma.GTubeFin(t):0.0000} → {mb.GTubeFin(t):0.0000} W/K");
    }

    // ───────────────────────────── 6 ─────────────────────────────
    [Fact]
    public void 门_选牌号物性真的进链_整线说明()
    {
        var lc = QuickCase("Pt-Rh/90-10");
        var r = LineRunner.Run(lc);
        Assert.True(r.Ok, r.Message);
        string last = r.Notes[^1];
        _o.WriteLine("末条说明：" + last);
        Assert.StartsWith("★ 电阻率、电阻温度系数、热导率、比热按牌号「Pt-Rh/90-10」", last);
        Assert.Contains("热导率数据点 100–1400 °C", last);
        Assert.Contains("密度、熔点按纯铂", last);
        Assert.Equal(1, r.Notes.Count(n => n.Contains("按牌号「Pt-Rh/90-10」", StringComparison.Ordinal)));
        // 区间那一段 = 访问口对本算例温度区间给的（取法同 LineRunner.AddGradeNote：升温起止温度 + 段金属温度 + 解出来的片的最低／最高温）。
        // 升温起止温度不论 CheckRamp 都并入：本算例 checkRamp: false，但设计电流闭式（DesignCurrent.Compute）每轮都从 RampFromC（25 °C）算到目标，一路按牌号读 ρ、cp、k。
        var temps = new[] { lc.RampFromC, lc.RampTargetC }
            .Concat(r.Segments.SelectMany(s => s.TMetal)).Concat(r.Flanges.Where(f => f.TMaxC > 0).SelectMany(f => new[] { f.TMinC, f.TMaxC })).ToArray();
        string range = PtProps.For("Pt-Rh/90-10").RangeNote(temps.Min(), temps.Max());
        _o.WriteLine($"本算例温度区间 {temps.Min():0.0}–{temps.Max():0.0} °C；区间说明：{(range.Length == 0 ? "（都在数据点内）" : range)}");
        Assert.EndsWith(range.Length > 0 ? "；" + range : "密度、熔点按纯铂", last);
        Assert.False(lc.CheckRamp);
        Assert.Contains("热导率：本算例最低 25 °C 低于数据点下限 100 °C", last);   // 不核升温也写 25 °C 那一端（设计电流闭式算过它）

        var rp = LineRunner.Run(QuickCase());
        Assert.True(rp.Ok, rp.Message);
        _o.WriteLine($"段 0 中点：Pt {rp.Segments[0].TMetal[rp.Segments[0].TMetal.Length / 2]:0.00} °C → Pt-Rh/90-10 {r.Segments[0].TMetal[r.Segments[0].TMetal.Length / 2]:0.00} °C");
        Assert.False(rp.Segments[0].TMetal.SequenceEqual(r.Segments[0].TMetal), "选了 Pt-Rh/90-10，整线段温一位都没变 —— 没接进去");
    }

    // ───────────────────────────── 7 ─────────────────────────────
    [Fact]
    public void 门_数据不全的牌号一起退回纯铂_写明()
    {
        var perGrade = MaterialDb.All.Keys.Where(k => k != "Pt" && !PtProps.For(k).IsPure).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        _o.WriteLine("按牌号取的：" + string.Join("、", perGrade));
        Assert.Equal(PerGradeExpected.OrderBy(k => k, StringComparer.Ordinal), perGrade);
        foreach (var k in MaterialDb.All.Keys.Where(k => k != "Pt" && !perGrade.Contains(k)))
        {
            Assert.True(PtProps.For(k).IsFallback, k + " 应退回纯铂并标出");
            // 「持久强度仍按本牌号」只在本牌号真有持久强度曲线时写
            Assert.Equal(MaterialDb.Get(k).HasCreep, PtProps.For(k).Note.Contains("持久强度仍按本牌号", StringComparison.Ordinal));
        }

        var f = PtProps.For("FKS16/Pt");
        _o.WriteLine("FKS16/Pt：" + f.Note);
        Assert.True(f.IsFallback && f.IsPure);
        Assert.Contains("一起按纯铂算", f.Note);
        Assert.Contains("热导率与比热：无数据", f.Note);
        foreach (double t in new[] { 20.0, 700.0, 1150.0, 1400.0 })
            Assert.True(Same(Materials.PtResistivity(t), f.Rho(t)) && Same(Materials.PtTcr(t), f.Tcr(t))
                     && Same(Materials.PtThermalK(t), f.K(t)) && Same(Materials.PtCp(t), f.Cp(t)), $"FKS16/Pt 退回纯铂在 {t} °C 不逐位");
        // Pt-Rh/80-20 只有推算参考热导率 ⇒ 不读它，退回纯铂
        Assert.True(Same(Materials.PtThermalK(1150), PtProps.For("Pt-Rh/80-20").K(1150)));
        Assert.NotEqual(MaterialDb.Get("Pt-Rh/80-20").ThermalKAdvisory!.Value(1150), PtProps.For("Pt-Rh/80-20").K(1150));

        // 段解：FKS16/Pt 的数与纯铂逐位相同，说明里写明退回
        var sPt = SegmentSolver.Solve(new DesignInputs());
        var sF = SegmentSolver.Solve(new DesignInputs { GradeName = "FKS16/Pt" });
        Assert.True(sPt.Ok, sPt.Message); Assert.True(sF.Ok, sF.Message);
        Assert.Equal(R48EmptyTubeGateTests.Fingerprint(sPt), R48EmptyTubeGateTests.Fingerprint(sF));
        Assert.Contains("一起按纯铂算", sF.Note);

        // 逐行牌号那一路（参考工具页 SizeFlanges → CoupledSolver，p.GradeName = 该行牌号）：退回纯铂的说明随片带出（FlangeResult.GradeNotes）；纯铂行不写、没有的名字不抛
        var rows = new List<Segment> { new() { Name = "S1", GradeName = "Pt" }, new() { Name = "S2", GradeName = "FKS16/Pt" }, new() { Name = "S3", GradeName = "没有这个牌号" } };
        Assert.Empty(LineSolver.JointGradeNotes(rows, 0));
        Assert.Equal(new[] { "S2：" + f.Note }, LineSolver.JointGradeNotes(rows, 1));
        Assert.Equal(new[] { "S2：" + f.Note }, LineSolver.JointGradeNotes(rows, 2));
        Assert.Empty(LineSolver.JointGradeNotes(rows, 3));
        Assert.Contains("GradeNotes = JointGradeNotes(segs, j)", File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineSolver.cs")));

        var ex = Assert.Throws<KeyNotFoundException>(() => PtProps.For("PtRh10"));
        Assert.Contains("材料库里没有牌号", ex.Message);
    }

    // ───────────────────────────── 8 ─────────────────────────────
    [Fact]
    public void 源码门_求解链不再直读纯铂函数_例外逐条有因()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        string[] pats = { "Materials.PtResistivity(", "Materials.PtThermalK(", "Materials.PtCp(", "Materials.PtTcr(", "Materials.RhoRef *", "Materials.BetaFit" };
        // 例外（逐条有因）：
        //   PtProps.cs —— 访问口本身，纯铂一支在这里调原函数（每支 1 处；dρ/dT 那一行同时有 RhoRef * 与 BetaFit）；
        //   DesignScreen.cs 电阻率 2 处 —— ShapeFactors.ResistanceOhm 全仓无调用点；Extract 里 ρ 在形状因子里约掉、电流解等温（σ ≡ 1），与牌号无关；
        //   RemovalPriority.cs 热导率 1 处 —— 整片一个常数，只标定瓦数，去料排序与挖孔位置对它不变。
        var allowed = new Dictionary<(string, string), int>
        {
            [("PtProps.cs", "Materials.PtResistivity(")] = 1, [("PtProps.cs", "Materials.PtThermalK(")] = 1, [("PtProps.cs", "Materials.PtCp(")] = 1,
            [("PtProps.cs", "Materials.PtTcr(")] = 1, [("PtProps.cs", "Materials.RhoRef *")] = 1, [("PtProps.cs", "Materials.BetaFit")] = 1,
            [("DesignScreen.cs", "Materials.PtResistivity(")] = 2,
            [("RemovalPriority.cs", "Materials.PtThermalK(")] = 1,
        };
        var bad = new List<string>();
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            string name = Path.GetFileName(f);
            if (name == "Materials.cs") continue;   // 定义处
            string s = File.ReadAllText(f);
            foreach (string pat in pats)
            {
                int cnt = s.Split(pat).Length - 1;
                int want = allowed.TryGetValue((name, pat), out int w) ? w : 0;
                if (cnt != want) bad.Add($"{name} 里 {pat} {cnt} 处（应 {want}）");
            }
        }
        Assert.True(bad.Count == 0, "求解链直读纯铂函数的地方与例外名单不符：" + string.Join("；", bad));

        // 生产 Core 调电流解与闭式电阻的地方都传了牌号（取到分号为止的调用文本里有 props／PtProps.）；例外：DesignScreen 的 ShellCurrent.Solve（见上）
        var missing = new List<string>();
        int seen = 0;
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            string name = Path.GetFileName(f), s = File.ReadAllText(f);
            foreach (string call in new[] { "ShellCurrent.Solve(", "PlateCurrent2D.Solve(", "ClosedFormResistanceOhm(" })
            {
                for (int i = s.IndexOf(call, StringComparison.Ordinal); i >= 0; i = s.IndexOf(call, i + call.Length, StringComparison.Ordinal))
                {
                    int lineStart = s.LastIndexOf('\n', i) + 1;
                    string head = s.Substring(lineStart, i - lineStart);
                    if (head.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;       // 注释
                    if (head.Contains("public static", StringComparison.Ordinal)) continue;          // 声明
                    int end = s.IndexOf(';', i);
                    string text = s.Substring(i, end - i);
                    seen++;
                    if (name == "DesignScreen.cs" && call == "ShellCurrent.Solve(") continue;
                    if (!Regex.IsMatch(text, @"\bprops\b|PtProps\.")) missing.Add($"{name}：{text.Replace('\n', ' ')}");
                }
            }
        }
        _o.WriteLine($"Core 里电流解／闭式电阻调用 {seen} 处");
        Assert.True(seen >= 4, "一处调用都没找到 —— 门空转");
        Assert.True(missing.Count == 0, "生产 Core 调用没传牌号：" + string.Join("；", missing));
        string shell = File.ReadAllText(Path.Combine(core, "ShellCurrent.cs"));
        Assert.Contains("props: c?.Base is null ? null : PtProps.For(c)", shell);   // SolveFor（整线唯一入口）按算例牌号
    }

    // ───────────────────────────── 9 ─────────────────────────────
    [Fact]
    public void 门_整线牌号只有一个来源()
    {
        // 不一致 ⇒ Run 第一步 Normalize 当场抛（在任何求解之前）
        var c = new LineCase { Base = new DesignInputs { GradeName = "Pt" }, GradeName = "Pt-Rh/90-10" };
        var ex = Assert.Throws<InvalidOperationException>(() => LineRunner.Run(c));
        Assert.Contains("只认一个牌号来源", ex.Message);
        Assert.Throws<InvalidOperationException>(() => LineRunner.BaseSegParams(c, 0, 1150));   // 其他先 Normalize 的公开入口同一口径

        // 空 ⇒ 接参数表；段参数与法兰那一路（读 Base）拿到同一个牌号、同一个取值口
        var p = new DesignInputs { GradeName = "Pt-Rh/90-10" };
        var lc = new LineCase { Base = p, SetpointC = new[] { 1150.0 }, HeadM = new[] { 0.3 } };
        Assert.Same(PtProps.For(p), PtProps.For(lc));
        var sp = LineRunner.BaseSegParams(lc, 0, 1150);
        Assert.Equal("Pt-Rh/90-10", lc.GradeName);
        Assert.Equal("Pt-Rh/90-10", sp.GradeName);
        Assert.Same(PtProps.For(p), PtProps.For(sp));
    }
}
