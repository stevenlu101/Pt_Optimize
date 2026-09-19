using System;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 G3 空管管腔轴向辐射进模型的快门（2026-09-15，Opus 5；规格出自物理把关人第十一轮与数值把关人第十四轮）。
///
/// 模型（口径见 DesignInputs.TubeCavityRadKA1150WmPerK、SegmentSolver.CavityRadKA／NeighbourConductanceWPerK）：
///   空管时一维段解的轴向导热加一份 kA_rad = 系数 × (T_set/T_标定)³（开尔文，按段控温点温度取常数），带玻璃为 0；
///   段间接头导度也加这一份（两段管腔在接头处连通），管腔那一份按邻段端温取，收敛后两侧只差 O(3ΔT/T)；前提是两段节距相同，
///   管壁那一份按各自控温点取的原有不对称仍在（第二轮审查后改，2026-09-15 Opus 5：原写「同一接头两侧同值」，与事实不符）；铂管两端封住，不计管口辐射。
///
/// 本档的门（门槛跑前写死，出处逐条写在断言旁）：
///   1. 带玻璃逐位不变：C 路三个指纹（R48EmptyTubeGateTests 的记录值、同一段指纹代码）在系数给 0／低档／高档／1.0 时都逐位相同。
///   2. 空管一维解能量守恒：低档、高档，反算电流与定电流、带两端抽热与两侧邻段 —— SegmentSolver.EnergyBalance 残差 ≤ 0.5 %（与 C 路空管快门同一门槛）。
///      同一个 Fact 里查说明：单段 SolveResult.Note 写出计入的管腔辐射；单段页报表（MainForm.Report）空管时印「工况说明」一节、带玻璃不印（第二轮审查后加，2026-09-15 Opus 5）。
///   3. 系数增大 ⇒ 端部温降的衰减长度变长（在解出的剖面上量，不读 √(kA/β) 那个式子）、端部温降变浅，都严格单调。
///   4. 接头守恒：两段不同控温点（1150|1080）的空管在接头处解到不动点，两侧流过接头的热对得上（误差不超过管壁导热按各自设定取的那份原有不对称）；
///      并印出「若管腔那一份按各自控温点取」会差多少，证明本门分得开。
///      R48 G3 审查后（2026-09-15，Opus 5）：允许误差按 q0 + q1 = (g0 − g1)/g0·q0 + g1·h 先验推出，管壁不对称只按它在接头导度里的占比计（初版按整份 |q0| 计，偏松约 5 倍）；
///      另只报数：不等长两段（300|250 mm）接头失配多少（接头导度按各自节距取，前提是节距相同才守恒）。
///   5. 整线空管工况说明写出系数、各段换算值与「不计管口辐射」；系数为 0 时写「未计入」；内径与估算条件不同时写明没换算。
/// </summary>
public class R48CavityRadiationGateTests
{
    private readonly ITestOutputHelper _o;
    public R48CavityRadiationGateTests(ITestOutputHelper o) => _o = o;

    private static DesignInputs Empty(DesignInputs p) => R48EmptyTubeGateTests.Empty(p);

    /// <summary>单段页报表：调 MainForm 私有静态 Report（页上 Run() 写输出框用的同一个函数）。</summary>
    private static string SingleSegReport(DesignInputs p, SolveResult r)
    {
        var m = typeof(PtOptimize.UI.MainForm).GetMethod("Report", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { p, r })!;
    }

    [Fact]
    public void 带玻璃_系数给多少三个指纹都逐位不变()
    {
        // 记录值出处：R48EmptyTubeGateTests.带玻璃_逐位不变_三个算例指纹（C 路 2026-09-14 记录；合并树 G3 改前 2026-09-15 实跑同值，
        //   deliverable\R48_G3_C路快门_合并后改前_2026-09-15.txt）。算例构造与指纹代码调同一份（CaseB／Fingerprint 由那一档公开为 internal）。
        foreach (double k in new[] { 0.0, SegmentSolver.CavityRadKA1150Low, SegmentSolver.CavityRadKA1150High, 1.0 })
        {
            var a = SegmentSolver.Solve(new DesignInputs { TubeCavityRadKA1150WmPerK = k });
            var pb = R48EmptyTubeGateTests.CaseB(); pb.TubeCavityRadKA1150WmPerK = k;
            var b = SegmentSolver.SolveAtCurrent(pb, 1500);
            var c = SegmentSolver.SolveAtCurrent(new DesignInputs { HGlass = 0, TSetC = 1300, TubeCavityRadKA1150WmPerK = k }, 1400);
            _o.WriteLine($"系数 {k:0.000}：A fp=0x{R48EmptyTubeGateTests.Fingerprint(a):X16}　B fp=0x{R48EmptyTubeGateTests.Fingerprint(b):X16}　C fp=0x{R48EmptyTubeGateTests.Fingerprint(c):X16}");
            Assert.True(a.Ok && b.Ok && c.Ok);
            Assert.Equal(0x33B7908F6563A1E0UL, R48EmptyTubeGateTests.Fingerprint(a));
            Assert.Equal(0x20CA37AA8C454479UL, R48EmptyTubeGateTests.Fingerprint(b));
            Assert.Equal(0x02DA1FDB755FFD7AUL, R48EmptyTubeGateTests.Fingerprint(c));
            Assert.True(a.CavityRadKAWmPerK == 0.0 && b.CavityRadKAWmPerK == 0.0 && c.CavityRadKAWmPerK == 0.0, "带玻璃（含只 hg = 0 有流量）的管腔辐射应为 0");
            // （接头导度带玻璃逐位不变由 B 指纹守：CaseB 有左邻段 1100 °C，接头那一项在剖面里）
        }
    }

    [Fact]
    public void 空管一维解_低档高档能量守恒()
    {
        foreach (double k in new[] { SegmentSolver.CavityRadKA1150Low, SegmentSolver.CavityRadKA1150High })
        {
            // 反算电流：两端抽热（左 5 W、右 −2 W）＋左邻段 1100 °C（C 路空管快门同一算例，去掉端部额外保温 —— 那条括根脆弱与本路无关，见 R48EmptyTubeGateTests）
            var pd = Empty(R48EmptyTubeGateTests.CaseB()); pd.EndInsulExtraMm = 0; pd.TubeCavityRadKA1150WmPerK = k;
            var rd = SegmentSolver.Solve(pd);
            Assert.True(rd.Ok, rd.Message);
            Assert.Equal(k, rd.CavityRadKAWmPerK);                                   // 控温点 1150 = 标定温度，换算系数恰为 1
            Assert.InRange(rd.TMetal[rd.TMetal.Length / 2], pd.TSetC - 0.5, pd.TSetC + 0.5);
            var ed = SegmentSolver.EnergyBalance(pd, rd);
            // 定电流、两侧邻段（左 1100、右 1180）、控温点 1080（换算系数 ≠ 1，接头两端导度各按各的邻段端温取）
            var pc = Empty(new DesignInputs
            {
                TSetC = 1080, FlangeDrawOverrideSet = true, FlangeDrawOverrideW = 0, FlangeDrawLeftW = 8.0, FlangeDrawRightW = -3.0,
                NeighbourTempLeftC = 1100.0, NeighbourTempRightC = 1180.0, TubeCavityRadKA1150WmPerK = k
            });
            var rc = SegmentSolver.SolveAtCurrent(pc, 1400);
            Assert.True(rc.Ok, rc.Message);
            var ec = SegmentSolver.EnergyBalance(pc, rc);
            _o.WriteLine($"系数 {k:0.000}：反算电流 I={rd.CurrentA:0.0} A　焦耳热 {ed.JouleW:0.00}　散热 {ed.SurfaceLossW:0.00}　两端流出 {ed.EndsOutW:0.00} W　残差 {ed.ResidualRel:P4}"
                       + $"　｜定电流 1400 A、控温点 1080（段内 kA {rc.CavityRadKAWmPerK:0.0000}）：焦耳热 {ec.JouleW:0.00}　散热 {ec.SurfaceLossW:0.00}　两端流出 {ec.EndsOutW:0.00} W　残差 {ec.ResidualRel:P4}");
            Assert.True(Math.Abs(ed.EndsOutW) > 1.0 && Math.Abs(ec.EndsOutW) > 1.0, "两端流出项应非零（不然没检到端部项）");
            Assert.True(Math.Abs(ed.ResidualRel) <= 0.005, $"系数 {k}：反算电流算例能量账不平 {ed.ResidualRel:P3}（限 0.5 %，同 C 路空管快门）");
            Assert.True(Math.Abs(ec.ResidualRel) <= 0.005, $"系数 {k}：定电流算例能量账不平 {ec.ResidualRel:P3}（限 0.5 %）");
            Assert.InRange(rc.CavityRadKAWmPerK, 0.85 * k, 0.87 * k);                 // (1353.15/1423.15)³ = 0.8596

            // R48 G3 审查后（2026-09-15，Opus 5）：单段说明要写出计入的管腔辐射（全文调生产函数，这里只查它真写进了结果、且含本段换算值与系数）
            Assert.Equal(SegmentSolver.EmptyTubeNoteOf(pc), rc.Note);
            Assert.StartsWith(SegmentSolver.EmptyTubeNote, rc.Note);
            Assert.Contains($"计入 {rc.CavityRadKAWmPerK:0.000} W·m/K（1150 °C 时 {k:0.000}", rc.Note);
            Assert.Contains("未经实测", rc.Note);
            Assert.Contains("不计管口辐射散热", rc.Note);

            // 第二轮审查后（2026-09-15，Opus 5）：单段页报表（MainForm.Report，页上 Run() 写进输出框的就是它）要把这句印出来 ——
            //   调页上同一个私有函数（反射，同 BannerFitsTests 的做法），不另拼字串。
            string rep = SingleSegReport(pc, rc);
            Assert.Contains("── 工况说明", rep);
            Assert.Contains($"计入 {rc.CavityRadKAWmPerK:0.000} W·m/K（1150 °C 时 {k:0.000}", rep);
            Assert.Contains("未经实测", rep);
            Assert.Contains("  铂管两端封住，不计管口辐射散热", rep);      // 括号外的「；」处断行：最后一句单独一行
            Assert.DoesNotContain("  两档估计", rep);                      // 括号里的「；」不断行
        }
        // 带玻璃：报表里没有工况说明一节（Note 为空）
        var pg = new DesignInputs();
        Assert.DoesNotContain("工况说明", SingleSegReport(pg, SegmentSolver.Solve(pg)));
        // 系数 0 的空管：说明只有「不适用」那句（没计入就不说计入）；带玻璃：说明为空
        var p0 = Empty(new DesignInputs { TubeCavityRadKA1150WmPerK = 0 });
        var r0 = SegmentSolver.Solve(p0);
        Assert.True(r0.Ok, r0.Message);
        Assert.Equal(SegmentSolver.EmptyTubeNote, r0.Note);
        Assert.Equal("", SegmentSolver.Solve(new DesignInputs()).Note);
        _o.WriteLine("单段空管说明（系数 0.056、控温点 1080）：" + SegmentSolver.EmptyTubeNoteOf(Empty(new DesignInputs { TSetC = 1080, TubeCavityRadKA1150WmPerK = SegmentSolver.CavityRadKA1150High })));
    }

    [Fact]
    public void 系数增大_端部温降衰减长度变长_温降变浅()
    {
        // 长管（2000 mm，节距 1 mm）、控温点 1150、只在左端抽 20 W，右端绝热：远端 = 均匀平衡温度。电流取无抽热时反算的那一个（远端就是 1150）。
        DesignInputs Mk(double k, bool draw) => Empty(new DesignInputs
        {
            TSetC = 1150, TubeLengthMm = 2000, Nodes = 2001, TubeCavityRadKA1150WmPerK = k,
            FlangeDrawOverrideSet = true, FlangeDrawOverrideW = 0, FlangeDrawLeftW = draw ? 20.0 : 0.0, FlangeDrawRightW = 0
        });
        var r0 = SegmentSolver.Solve(Mk(0, false));
        Assert.True(r0.Ok, r0.Message);
        double current = r0.CurrentA;
        double prevLen = -1, prevDeficit = double.MaxValue;
        foreach (double k in new[] { 0.0, 0.01, SegmentSolver.CavityRadKA1150Low, SegmentSolver.CavityRadKA1150High, 0.1 })
        {
            var p = Mk(k, true);
            var r = SegmentSolver.SolveAtCurrent(p, current);
            Assert.True(r.Ok, r.Message);
            int n = r.TMetal.Length;
            double tFar = r.TMetal[n - 1];
            double d0 = tFar - r.TMetal[0];
            Assert.True(d0 > 1.0, $"系数 {k}：端部温降 {d0:0.000} K 太浅，量不了衰减长度");
            // 衰减长度：温降第一次衰到端部的 1/e 处（线性插值）
            double target = d0 / Math.E, len = double.NaN;
            for (int i = 1; i < n; i++)
            {
                double di = tFar - r.TMetal[i], dPrev = tFar - r.TMetal[i - 1];
                if (di <= target) { len = r.X[i - 1] + (r.X[i] - r.X[i - 1]) * (dPrev - target) / (dPrev - di); break; }
            }
            double tailDeficit = tFar - r.TMetal[n / 2];
            _o.WriteLine($"系数 {k:0.000}：端部温降 {d0:0.000} K　实测衰减长度 {len:0.00} mm　√(kA/β) {r.DecayLengthMm:0.00} mm（比 {len / r.DecayLengthMm:0.000}）　管中点温降 {tailDeficit:0.0000} K　远端 {tFar:0.000} °C");
            Assert.True(double.IsFinite(len), $"系数 {k}：剖面上没找到 1/e 点");
            Assert.True(tailDeficit < 0.01 * d0, $"系数 {k}：管中点温降 {tailDeficit:0.000} K 不到端部的 1 %，远端不算「远」，长度量不准");
            Assert.True(len > prevLen, $"系数 {k}：衰减长度 {len:0.00} mm 没比上一档 {prevLen:0.00} mm 长");
            Assert.True(d0 < prevDeficit, $"系数 {k}：端部温降 {d0:0.000} K 没比上一档 {prevDeficit:0.000} K 浅");
            var e = SegmentSolver.EnergyBalance(p, r);
            Assert.True(Math.Abs(e.ResidualRel) <= 0.005, $"系数 {k}：能量账不平 {e.ResidualRel:P3}");
            prevLen = len; prevDeficit = d0;
        }
    }

    /// <summary>两段空管接头不动点（段 0 控温点 1150 右端接段 1、段 1 控温点 1080 左端接段 0，外侧两端绝热无抽热，各按无邻段时反算的电流定电流）。</summary>
    private sealed class Joint
    {
        public double Xs, Ys, HRes, Q0, Q1, Area;
        public SolveResult R0 = null!, R1 = null!;
        public DesignInputs P0 = null!, P1 = null!;
    }

    private static DesignInputs JointSeg(double set, double lenMm, double nbL, double nbR, double k) => Empty(new DesignInputs
    {
        TSetC = set, TubeLengthMm = lenMm, TubeCavityRadKA1150WmPerK = k, FlangeDrawOverrideSet = true, FlangeDrawOverrideW = 0,
        FlangeDrawLeftW = 0, FlangeDrawRightW = 0, NeighbourTempLeftC = nbL, NeighbourTempRightC = nbR
    });

    // 不动点：段 0 看到的邻段端温 x = 段 1 左端温度，段 1 看到的 y = 段 0 右端温度。段 0 右端温度只随 x 变、段 1 左端只随 y 变
    //   ⇒ 标量方程 h(x) = T1A(T0B(x)) − x = 0，二分到 1e-9 K（h 严格单减：两段端温对邻段温度的斜率都 &lt; 1）。
    // 这不是抄 LineRunner 的外层耦合（那边有欠松弛与 Anderson，只改到达路径）：这里要验的是**段解本身的接头通量**在不动点上守不守恒。
    private static Joint SolveJoint(double len0Mm, double len1Mm, double k)
    {
        double i0 = SegmentSolver.Solve(JointSeg(1150, len0Mm, double.NaN, double.NaN, k)).CurrentA;
        double i1 = SegmentSolver.Solve(JointSeg(1080, len1Mm, double.NaN, double.NaN, k)).CurrentA;
        SolveResult S0(double x) => SegmentSolver.SolveAtCurrent(JointSeg(1150, len0Mm, double.NaN, x, k), i0);
        SolveResult S1(double y) => SegmentSolver.SolveAtCurrent(JointSeg(1080, len1Mm, y, double.NaN, k), i1);
        double H(double x) { var a = S0(x); return S1(a.TFlangeBC).TFlangeAC - x; }
        double lo = 1000, hi = 1250;
        Assert.True(H(lo) > 0 && H(hi) < 0, $"括根失败（段长 {len0Mm}|{len1Mm}）：h({lo}) = {H(lo)}，h({hi}) = {H(hi)}");
        for (int it = 0; it < 80 && hi - lo > 1e-9; it++) { double m = 0.5 * (lo + hi); if (H(m) > 0) lo = m; else hi = m; }
        var j = new Joint { Xs = 0.5 * (lo + hi) };
        j.R0 = S0(j.Xs); j.Ys = j.R0.TFlangeBC; j.R1 = S1(j.Ys);
        Assert.True(j.R0.Ok && j.R1.Ok, j.R0.Message + j.R1.Message);
        j.P0 = JointSeg(1150, len0Mm, double.NaN, j.Xs, k); j.P1 = JointSeg(1080, len1Mm, j.Ys, double.NaN, k);
        j.Q0 = SegmentSolver.EnergyBalance(j.P0, j.R0).EndsOutW;          // 段 0 只有右端（接头）有流出
        j.Q1 = SegmentSolver.EnergyBalance(j.P1, j.R1).EndsOutW;          // 段 1 只有左端（接头）有流出
        j.HRes = j.R1.TFlangeAC - j.Xs;
        j.Area = j.R0.TubeAreaMm2 * 1e-6;                                  // 段解报出的管截面（与段解同一个）
        return j;
    }

    [Fact]
    public void 接头两侧不同控温点_流过接头的热对得上()
    {
        const double k = SegmentSolver.CavityRadKA1150High;
        var j = SolveJoint(300, 300, k);
        double xs = j.Xs, ys = j.Ys, q0 = j.Q0, q1 = j.Q1, hRes = j.HRes, area = j.Area;
        // 各份导度一律调生产函数 NeighbourConductanceWPerK：管壁那一份 = 系数给 0 时的值（空管判别仍成立，管腔那一份为 0）；管腔那一份 = 总的减管壁的。
        var p0w = SegmentSolver.Clone(j.P0); p0w.TubeCavityRadKA1150WmPerK = 0;
        var p1w = SegmentSolver.Clone(j.P1); p1w.TubeCavityRadKA1150WmPerK = 0;
        double g0 = SegmentSolver.NeighbourConductanceWPerK(j.P0, area, xs), g1 = SegmentSolver.NeighbourConductanceWPerK(j.P1, area, ys);
        double gw0 = SegmentSolver.NeighbourConductanceWPerK(p0w, area, xs), gw1 = SegmentSolver.NeighbourConductanceWPerK(p1w, area, ys);
        double gc0 = g0 - gw0, gc1 = g1 - gw1;
        // 允许的误差 —— R48 G3 审查后（2026-09-15，Opus 5）改，按式子先验推出、不依赖实测数：
        //   段 0 接头流出 q0 = g0·(y − x)，段 1 接头流出 q1 = g1·(x + h − y)（h = 二分停点残余）⇒ q0 + q1 = (g0 − g1)/g0 · q0 + g1·h。
        //   |g0 − g1| ≤ |gw0 − gw1|（管壁 kPt 按各自控温点取，原有不对称）＋ max(gc0, gc1)·((1+u)³ − 1)（管腔那一份按两侧端温 T³ 取，u = |x − y|/T_min，(1+u)³ − 1 ≤ 3u(1+u)²）。
        //   ⇒ allow = (|gw0 − gw1| + max(gc0, gc1)·3u(1+u)²)/g0 · |q0| + g1·|h|。
        //   旧式（初版）是 wallAsym·|q0| + g1·|h| + 3ΔT/T·|q0|：管壁不对称按整份 |q0| 算，而管壁在接头导度里只占 gw/g ≈ 20 %（系数 0.056）⇒ 偏松约 5 倍，
        //   管腔那一份若有 0.2 W 级的不对称查不出来（审查第 5 条）。两式都印出来。
        double u = Math.Abs(xs - ys) / (Math.Min(xs, ys) + LineSolver.KelvinOffset);
        double allow = (Math.Abs(gw0 - gw1) + Math.Max(gc0, gc1) * 3 * u * (1 + u) * (1 + u)) / g0 * Math.Abs(q0) + g1 * Math.Abs(hRes) + 1e-9;
        double wallAsym = Math.Abs(Materials.PtThermalK(1150) - Materials.PtThermalK(1080)) / Materials.PtThermalK(1080);
        double dT = Math.Abs(j.R0.TFlangeBC - j.R1.TFlangeAC);
        double allowOld = wallAsym * Math.Abs(q0) + g1 * Math.Abs(hRes) + 3 * dT / (1080 + LineSolver.KelvinOffset) * Math.Abs(q0) + 1e-9;
        double identity = (g0 - g1) / g0 * q0 + g1 * hRes;   // 只报数：上面那条恒等式的右边，应与实测合计几乎相同
        // 分辨力对照（算的，不是跑的）：若管腔那一份按各自控温点取，两侧导度比差多少、接头热流就差多少
        double gOwn0 = (Materials.PtThermalK(1150) * area + SegmentSolver.CavityRadKA(j.P0));
        double gOwn1 = (Materials.PtThermalK(1080) * area + SegmentSolver.CavityRadKA(j.P1));
        double ctrl = Math.Abs(1 - gOwn1 / gOwn0) * Math.Abs(q0);
        _o.WriteLine($"接头（系数 {k:0.000}，两段各 300 mm）：不动点 x={xs:0.000000} °C（段 1 左端）　段 0 右端 {ys:0.000000} °C　ΔT {dT:0.0000} K　二分残余 {hRes:E2} K"
                   + $"　段 0 流出 {q0:+0.0000;-0.0000} W　段 1 流出 {q1:+0.0000;-0.0000} W　合计 {q0 + q1:+0.00000;-0.00000} W（允许 {allow:0.00000}；初版允许式 {allowOld:0.00000}）"
                   + $"　｜导度 W/K：段 0 {g0:0.000}（管壁 {gw0:0.000}＋管腔 {gc0:0.000}）　段 1 {g1:0.000}（管壁 {gw1:0.000}＋管腔 {gc1:0.000}）　恒等式右边 {identity:+0.00000;-0.00000} W"
                   + $"　｜对照：管腔那一份按各自控温点取会差约 {ctrl:0.000} W");
        Assert.True(Math.Abs(q0) > 5.0, $"接头热流 {q0:0.00} W 太小，本门检不出什么");
        Assert.True(Math.Abs(q0 + q1) <= allow, $"接头两侧热流对不上：{q0:+0.0000} + {q1:+0.0000} = {q0 + q1:+0.00000} W，允许 {allow:0.00000} W");
        Assert.True(ctrl > 5 * allow, $"对照差 {ctrl:0.0000} W 不到允许误差 {allow:0.0000} W 的 5 倍 —— 本门分不开两种取法");

        // ── 只报数（审查第 5 条②，2026-09-15 Opus 5）：两段不等长（300|250 mm、节点数同为 401）时，接头导度按各自节距取 ⇒ 两侧导度差节距比，接头热流失配。
        //   管壁那一份 R48 之前就这样；这里给出量级，交接 open issue。不设门槛（没有「允许失配多少」的出处）。
        var ju = SolveJoint(300, 250, k);
        double gu0 = SegmentSolver.NeighbourConductanceWPerK(ju.P0, ju.Area, ju.Xs), gu1 = SegmentSolver.NeighbourConductanceWPerK(ju.P1, ju.Area, ju.Ys);
        _o.WriteLine($"只报数·不等长 300|250 mm（系数 {k:0.000}）：段 0 流出 {ju.Q0:+0.0000;-0.0000} W　段 1 流出 {ju.Q1:+0.0000;-0.0000} W　合计 {ju.Q0 + ju.Q1:+0.0000;-0.0000} W"
                   + $"（占段 0 流出 {(ju.Q0 + ju.Q1) / ju.Q0:P1}）　导度 段 0 {gu0:0.000}／段 1 {gu1:0.000} W/K（比 {gu1 / gu0:0.0000}，节距比 300/250 = 1.2）"
                   + $"　｜同一算例系数给 0："
                   + JointLine(SolveJoint(300, 250, 0)));
    }

    private static string JointLine(Joint jj) => $"段 0 流出 {jj.Q0:+0.0000;-0.0000} W　段 1 流出 {jj.Q1:+0.0000;-0.0000} W　合计 {jj.Q0 + jj.Q1:+0.0000;-0.0000} W（占段 0 流出 {(jj.Q0 + jj.Q1) / jj.Q0:P1}）";

    [Fact]
    public void 整线空管工况说明_写出系数与换算值_不计管口辐射()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 };
        d.SegLengthMm = new[] { 300.0, 300.0 };
        d = d.Fit();
        var p = new DesignInputs();
        string ramp = LineRunner.EmptyTubeNoteFor(d.BuildCase(p, checkRamp: false, emptyTube: true));
        string asGiven = LineRunner.EmptyTubeNoteFor(d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.AsGiven));
        // 第二轮审查后（2026-09-15，Opus 5）：空管段参数调生产函数造（原手抄 pe.ThroughputTPerDay = 0; pe.HGlass = 0;，门不许手抄生产配方）
        var pe = SegmentSolver.Clone(p); LineRunner.MakeEmptyTubeSegment(pe);
        string k1080 = SegmentSolver.CavityRadKAAt(pe, 1080).ToString("0.000");
        _o.WriteLine("全线升温目标：" + ramp);
        _o.WriteLine("沿用生产控温点：" + asGiven);
        Assert.Contains("管腔内轴向辐射按附加的轴向导热计入：1150 °C 时 0.023 W·m/K，按各段控温点温度的三次方换算为 0.023/0.023 W·m/K", ramp);
        Assert.Contains($"换算为 0.023/{k1080} W·m/K", asGiven);
        Assert.Contains("接头导热也计入这一份", ramp);
        Assert.Contains("较低的一档", ramp);
        Assert.Contains("不计管口辐射散热", ramp);
        Assert.DoesNotContain("与估算条件不同", ramp);
        Assert.DoesNotContain("尚待确认", ramp);

        var p0 = new DesignInputs { TubeCavityRadKA1150WmPerK = 0 };
        string none = LineRunner.EmptyTubeNoteFor(d.BuildCase(p0, checkRamp: false, emptyTube: true));
        Assert.Contains("管腔内轴向辐射本次未计入（系数为 0）", none);
        Assert.Contains("不计管口辐射散热", none);

        var d48 = d.Clone(); d48.TubeIdMm = 48.0; d48 = d48.Fit();
        string off = LineRunner.EmptyTubeNoteFor(d48.BuildCase(p, checkRamp: false, emptyTube: true));
        _o.WriteLine("内径 48：" + off);
        Assert.Contains("内径 48 mm 与估算条件不同，系数没有换算", off);
    }
}
