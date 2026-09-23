using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  网格修复第二轮（2026-09-19，Fable 5.1）：薄片格 J 峰的门 —— 复核第 1／5／6／10 条。
//    病：锥形舌（Heater1）第一轮改后 JMax 23.210 落在覆盖率 0.016 的斜边楔形格（真峰 8.457 在管孔边），三档越细越大；生产 LineRunner 直接取 JMaxAPerMm2 判「全体 J < 11」。
//    机理：最小二乘重构的两条形心连线几乎共线（角度条件数 κ′ = λmin/λmax 0.006～0.03），垂直分量是噪声；量法与门槛出处见 ShellCurrent.SliverKappaMin 的注释。
//    修法（J 的量法，不是求解器规则；电位场逐位不变）：κ′ < 0.2 的格只沿强方向重构（截断最小二乘）。网格层只并碎格（覆盖率 < 1e-3，复核第 10 条）。
//    试过的另一条路（网格层按 κ′ < 0.2 并格）：幻影峰没了，但带舌孔样本的真峰被合成格抹掉 5～10 %（门 2 红）—— 记在 HANDOVER，不采用。
//  门（门槛跑前写死）：
//    1 Heater1 无孔三档：全体 J 峰 ≤ 1.05 × 实格峰（有料面积 ≥ 半个自家矩形的格）；三档全体峰的极差 ≤ 5 %；改回（门槛传 0）⇒ 全体峰 ≥ 1.5 × 实格峰（门不空守）。
//    2 带孔／带槽形状（本仓已有的槽变体样本：舌孔 R30、盘槽）三档：真峰保留 —— 改后全体峰对改前「条件数够的格里的最大 J」（κ′ ≥ 0.2，测试侧复算）差 ≤ 5 %。
//    3 W08／W06 导航／判决／细：全体 J 峰 改前（门槛 0）vs 改后 差 ≤ 1 %。
//    4 门槛敏感性自证：0.15／0.2／0.25／0.3 下 Heater1 三档全体峰极差 ≤ 5 %（门槛不是拟合出来的）。
//    5 碎格（第 10 条）：改后非管孔格覆盖率 ≥ 1e-3；改回（MeshRules.MergeSlivers = false）⇒ 有碎格（门不空守）。
//    6 F5 轴端余数规则（第 6 条）：盘 30.01～30.49／舌半宽 30 的 z 轴最小格距 ≥ hFine/4；去掉规则（MeshRules.AxisEndRule = false）⇒ 发丝格回来（门红）。
//  每条都写进带开跑时刻的证据文件（DeliverableOut.Stamped）。
// ════════════════════════════════════════════════════════════════════════════
public class R48NSliverGateTests
{
    private readonly ITestOutputHelper _o;
    public R48NSliverGateTests(ITestOutputHelper o) { _o = o; }
    internal const string Sign = "2026-09-19，Fable 5.1";

    // 跑前写死
    const double PeakOverRealTol = 0.05;    // 门 1／2：全体峰对实格峰／真峰
    internal const double ScaleSpreadTol = 0.05;     // 门 1／4：三档极差（2026-09-23 改 internal：R48F6HoleFaceGateTests 的 J 峰门与守恒门 J 积分项直接引用，不再手抄）
    const double PhantomMin = 1.5;          // 门 1：改回后幻影峰至少这么高（否则门空守）
    const double W08PeakTol = 0.01;         // 门 3
    const double DustFrac = FlangeMesher.CellMergeFrac;
    const double KappaOn = ShellCurrent.SliverKappaMin, KappaOff = 0.0;

    // ─────────────────────────────────────────────── 样本（与 FieldPlacementTests.Solve／探针同一套调用）
    internal static IEnumerable<(string name, FlangePlate g)> Shapes()
    {
        yield return ("Heater1 无孔", FieldPlacementTests.Heater1());
        yield return ("Heater1 舌孔R30", new FlangePlate { DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40, ThicknessMm = 4.71, ThickenedMm = 4.71, TabThicknessMm = 4.71, TabParallel = false, WeldFilletLegMm = 0, TabHoles = new[] { new FlangePlate.TabHole(-102.11, 0, 30) } });
        yield return ("Heater1 盘槽", new FlangePlate { DiscRadiusMm = 60, HoleRadiusMm = 26, TabEndXMm = -199.5, TabEndHalfWidthMm = 40, ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0, TabParallel = false, WeldFilletLegMm = 0, DiscSlots = new[] { new FlangePlate.DiscSlot(27, 40, 0, 60) } });
    }

    internal static (ShellMesh m, ShellCurrentResult sc) SolveShape(FlangePlate g, double scale, MeshRules? rules, double kappaMin)
    {
        var baseIn = new DesignInputs();
        var c = new LineCase { Base = baseIn };
        var m = FlangeMesher.BuildWith(g, rules, null, 0, 2.0 * scale, 8.0 * scale, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
        var sc = ShellCurrent.SolveFor(c, m, totalCurrentA: 1000, rhoRefOhmMm: 1.1e-4, sliverKappaMin: kappaMin);
        Assert.True(sc.Converged, "电流场没收敛");
        return (m, sc);
    }

    internal static (LineCase lc, FlangePlate g, ShellMesh m) BuildDesign(string which, double fineMm, MeshRules? rules)
    {
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design(which);
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
        var g = lc.FlangePlates[0];
        var m = LineRunner.PlateMeshAnalyticWith(lc, 0, rules, null);   // 生产配方同一份（LineRunner.PlateMeshAnalytic 的本体）
        return (lc, g, m);
    }

    static bool HoleCell(ShellMesh m, int c, double rh)
    {
        var rl = m.CellRects != null ? m.CellRects[c] : new List<(double, double, double, double)> { FlangeMesher.CellRect(m, c) };
        foreach (var (x0, x1, z0, z1) in rl)
        {
            double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1), fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            if (nx * nx + nz * nz < rh * rh && fx * fx + fz * fz > rh * rh) return true;
        }
        return false;
    }

    /// <summary>「实格」：有料面积 ≥ 自家节点矩形的一半（并过格的单元按目标格自己那个矩形，与第一轮的覆盖率口径同义）。</summary>
    static bool RealCell(ShellMesh m, int c)
    {
        var (x0, x1, z0, z1) = FlangeMesher.CellRect(m, c);
        return m.Area[c] >= 0.5 * (x1 - x0) * (z1 - z0);
    }

    /// <summary>测试侧复算的条件数 κ′（方向张量 Σ w n̂n̂ᵀ 的 λmin/λmax，w = 有料面长 ÷ 格边几何全长，n̂ = 形心连线）—— 与 ShellCurrent 的量法同一个定义，各写各的。
    /// ★ 2026-09-23（F6a，测试侧定义同步 —— 算规则改动，写明）：孔面上定电位（ShellMesh.HoleFaceDirichlet）时孔面通量也进 J 重构，
    ///   孔面的方向 = 弧面取弧中点指向孔心 −Mid/|Mid|、直边取形心指向边中点，权重 w = 1（与生产 ShellCurrent 同口径）；老口径（整格钉）照旧只数内部面。
    /// ⚠ 这处改动**改了门 2「真峰」的定义**（F6 审查后照实写，findings #27）：门 2 的真峰 = 改前（门槛 0）κ′ ≥ 0.2 的格里的最大 J，κ′ 就由这里算。
    ///   孔面纳入之后孔边格的 κ′ 变了，被算作「条件数够」的格集合随之变：Heater1 盘槽的真峰 ×1／×0.5／×0.25 由 8.466／8.795／8.871 变为 7.763／7.927／8.467，
    ///   ×0.25 档的真峰直接就是舌端外张斜边格 8.467（(−192.8, −40.6)）（证据：网格修复2_门2_薄片格J峰_带孔带槽形状_本次开跑于2026-09-23_030308.txt 对 _031503.txt）。
    ///   门 3 的打印列「截断格改前最大 J／覆盖率」也用它。门 1、门 4 调 Measure(…, false)，不经这里，红绿与这处改动无关。
    ///   与生产还差两处口径（照实写，没补）：生产 ShellCurrent 在压接格上整格跳过 J 重构（内部面、孔面都不进），并跳过 gB 不大于 0 的孔面；这里两条都没有。
    ///   压接格的 J 生产上恒为 0，不影响峰；gB ≤ 0 的孔面生产网格上不出现（DistAB 有下限、面长 &gt; 0）。</summary>
    internal static double KappaAngle(ShellMesh m, int c)
    {
        double nxx = 0, nxz = 0, nzz = 0; bool any = false;
        foreach (var f in m.Faces)
        {
            if (f.B < 0)
            {
                if (!m.HoleFaceDirichlet || f.A != c || f.Tag != ShellMesh.TagHole) continue;
                double hx, hz;
                if (!double.IsNaN(f.ArcRadiusMm)) { double rm = Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z); hx = -f.Mid.X / rm; hz = -f.Mid.Z / rm; }
                else { var dd = f.Mid - m.Centroid[c]; double ll = Math.Max(1e-12, dd.Norm); hx = dd.X / ll; hz = dd.Z / ll; }
                nxx += hx * hx; nxz += hx * hz; nzz += hz * hz; any = true;
                continue;
            }
            if (f.A != c && f.B != c) continue;
            int o = f.A == c ? f.B : f.A;
            var d = m.Centroid[o] - m.Centroid[c]; double len = Math.Max(1e-12, d.Norm);
            double nx = d.X / len, nz = d.Z / len;
            double w = f.FullLength > 0 ? Math.Min(1.0, f.Length / f.FullLength) : 1.0;
            nxx += w * nx * nx; nxz += w * nx * nz; nzz += w * nz * nz; any = true;
        }
        if (!any) return 0;
        double tr = nxx + nzz, det = nxx * nzz - nxz * nxz;
        double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double l1 = tr / 2 + disc, l2 = tr / 2 - disc;
        return l1 > 0 ? l2 / l1 : 0;
    }

    sealed class Peak
    {
        public double All, Real, Trusted; public int AllCell, Cells, Recon, NBelow;
        public double AllFrac, AllX, AllZ;
    }
    static Peak Measure(ShellMesh m, ShellCurrentResult sc, bool trusted)
    {
        int n = m.CellCount;
        var pk = new Peak { All = sc.JMaxAPerMm2, AllCell = sc.JMaxCell, Cells = n, Recon = sc.SliverReconCells };
        pk.AllFrac = m.FracOf(sc.JMaxCell); pk.AllX = m.Centroid[sc.JMaxCell].X; pk.AllZ = m.Centroid[sc.JMaxCell].Z;
        pk.Real = Enumerable.Range(0, n).Where(i => RealCell(m, i)).Max(i => sc.JMagAPerMm2[i]);
        if (trusted)
        {
            double t = 0; int nb = 0;
            for (int i = 0; i < n; i++)
            {
                if (KappaAngle(m, i) >= KappaOn) t = Math.Max(t, sc.JMagAPerMm2[i]); else nb++;
            }
            pk.Trusted = t; pk.NBelow = nb;
        }
        return pk;
    }

    static string Pct(double a, double b) => ((a - b) / b * 100).ToString("+0.00;-0.00");

    [Fact]
    public void 门1_Heater1三档_全体J峰回到实格量级_随加密收敛_改回则幻影峰当场回来()
    {
        string file = DeliverableOut.Stamped("网格修复2_门1_薄片格J峰_Heater1三档.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 1：Heater1（锥形舌，无孔）三档 ×1／×0.5／×0.25（细 2／1／0.5 mm）　等温电流场 1000 A（FieldPlacementTests.Solve 同一套调用）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：全体 J 峰 ≤ (1 + {PeakOverRealTol}) × 实格峰（有料面积 ≥ 半个自家矩形的格）；三档全体峰极差 ≤ {ScaleSpreadTol:P0}；改回（ShellCurrent 门槛传 0）⇒ 全体峰 ≥ {PhantomMin} × 实格峰。");
        W("档\t截断\t单元\t截断重构的格\t全体J峰\t峰格覆盖率\t峰格形心\t实格峰\t全体/实格\t判读");
        var g = FieldPlacementTests.Heater1();
        var allOn = new List<double>(); int bad = 0; var badL = new List<string>();
        foreach (double s in new[] { 1.0, 0.5, 0.25 })
        {
            var (m1, s1) = SolveShape(g, s, null, KappaOn);
            var (m0, s0) = SolveShape(g, s, null, KappaOff);
            var on = Measure(m1, s1, false); var off = Measure(m0, s0, false);
            bool okOn = on.All <= (1 + PeakOverRealTol) * on.Real, okOff = off.All >= PhantomMin * off.Real;
            W($"×{s}\t开\t{on.Cells}\t{on.Recon}\t{on.All:0.000}\t{on.AllFrac:0.000}\t({on.AllX:0.0},{on.AllZ:0.0})\t{on.Real:0.000}\t{on.All / on.Real:0.000}\t{(okOn ? "过" : "**不过：全体峰超实格峰**")}");
            W($"×{s}\t关\t{off.Cells}\t{off.Recon}\t{off.All:0.000}\t{off.AllFrac:0.000}\t({off.AllX:0.0},{off.AllZ:0.0})\t{off.Real:0.000}\t{off.All / off.Real:0.000}\t{(okOff ? "幻影峰在（门不空守）" : "**关掉截断也没有幻影峰 ⇒ 门空守**")}");
            if (!okOn) { bad++; badL.Add($"×{s} 全体 {on.All:0.000} > 1.05×实格 {on.Real:0.000}"); }
            if (!okOff) { bad++; badL.Add($"×{s} 关掉截断后全体 {off.All:0.000} 没超 {PhantomMin}×实格 {off.Real:0.000}（空守）"); }
            allOn.Add(on.All);
        }
        double spread = (allOn.Max() - allOn.Min()) / allOn.Min();
        W($"三档全体峰：{string.Join(" / ", allOn.Select(v => v.ToString("0.000")))}　极差 {spread:P2}（门槛 {ScaleSpreadTol:P0}）{(spread <= ScaleSpreadTol ? "过" : "**不过**")}");
        if (spread > ScaleSpreadTol) { bad++; badL.Add($"三档极差 {spread:P2}"); }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 1 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }

    [Fact]
    public void 门2_带孔带槽形状_真峰保留_改后全体峰对改前条件数够的格里最大J差不超5percent()
    {
        string file = DeliverableOut.Stamped("网格修复2_门2_薄片格J峰_带孔带槽形状.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 2：带孔／带槽形状的真峰保留　Heater1 舌孔 R30（厚 4.71）、Heater1 盘槽（27～40，张角 60°）×1／×0.5／×0.25　等温 1000 A");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"真峰的定义：改前（门槛 0，不截断）**重构条件数够的格**（测试侧复算 κ′ ≥ {KappaOn}）里的最大 J —— 最小二乘能信的那部分格；电位场两边逐位相同，这些格的 J 改前改后一样。");
        W($"门槛（跑前写死）：改后全体 J 峰对真峰差 ≤ {PeakOverRealTol:P0}；改前全体峰对真峰另印（那就是幻影有多高）。");
        W("形状\t档\t单元\t改前全体峰\t改前实格峰\t改前真峰(κ′≥门槛)\tκ′不足格数\t改后截断格数\t改后全体峰\t峰格覆盖率\t峰格形心\t改后/真峰\t判读");
        int bad = 0; var badL = new List<string>();
        foreach (var (name, g) in Shapes().Skip(1))
            foreach (double s in new[] { 1.0, 0.5, 0.25 })
            {
                var (m0, s0) = SolveShape(g, s, null, KappaOff);
                var (m1, s1) = SolveShape(g, s, null, KappaOn);
                var off = Measure(m0, s0, true); var on = Measure(m1, s1, false);
                double r = on.All / off.Trusted;
                bool ok = Math.Abs(r - 1) <= PeakOverRealTol;
                W($"{name}\t×{s}\t{off.Cells}\t{off.All:0.000}\t{off.Real:0.000}\t{off.Trusted:0.000}\t{off.NBelow}\t{on.Recon}\t{on.All:0.000}\t{on.AllFrac:0.000}\t({on.AllX:0.0},{on.AllZ:0.0})\t{r:0.000}\t{(ok ? "过" : "**不过**")}");
                if (!ok) { bad++; badL.Add($"{name} ×{s}：改后 {on.All:0.000} vs 真峰 {off.Trusted:0.000}（{Pct(on.All, off.Trusted)} %）"); }
            }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 2 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }

    [Fact]
    public void 门3_W08与W06_导航判决细_J峰改前改后差不超1percent()
    {
        string file = DeliverableOut.Stamped("网格修复2_门3_薄片格J峰_W08W06不变.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 3：W08／W06 片0 导航／判决／细（0.5）　等温 1214 A／1150 °C　截断关（门槛 0，改前）vs 开（改后）；网格同一张");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：全体 J 峰 改前 vs 改后 差 ≤ {W08PeakTol:P0}。被截断重构的格数、它们改前的最大 J 与覆盖率另印。");
        // 2026-09-23（F6 审查后改，findings #28／#51）：原文此处印「预期：只有盘缘 J ≈ 0 的碎格」—— F6（孔面上定电位）之后已不成立：
        //   W08 导航档（h = 2）与 W06 细 0.5 档的**全体 J 峰格本身**就在截断格里（改前 14.442 → 改后 14.419，−0.155 %；16.782 → 16.653，−0.770 %，离 1 % 门槛余 0.23 个百分点），
        //   即 F6 之后这两档生产报出的 JMax 取的是截断重构值（证据 网格修复2_门3_薄片格J峰_W08W06不变_本次开跑于2026-09-23_031503.txt）。门槛不动。
        W("（预期文字 2026-09-23 改：F6 之后截断格不只是盘缘 J ≈ 0 的碎格 —— W08 导航档与 W06 细 0.5 档的全体 J 峰格本身就被截断，见「截断格改前最大J」列。）");
        W("设计\t细区mm\t单元\t最小κ′\t改前J峰\t改后J峰\tΔ%\t截断格数\t截断格改前最大J\t截断格最大覆盖率\t发热改前W\t发热改后W\t判读");
        int bad = 0; var badL = new List<string>();
        double rho = Materials.PtResistivity(R48NMeshGateTests.PlateTempC) * 1e3;
        foreach (string which in new[] { "W08", "W06" })
            foreach (double fine in new[] { 0.0, 1.0, 0.5 })
            {
                var (lc, g, m) = BuildDesign(which, fine, null);
                var s0 = ShellCurrent.Solve(m, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC, sliverKappaMin: KappaOff);
                var s1 = ShellCurrent.Solve(m, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC, sliverKappaMin: KappaOn);
                Assert.True(s0.Converged && s1.Converged);
                double jRec = 0, fRec = 0;
                for (int i = 0; i < m.CellCount; i++)
                    if (KappaAngle(m, i) < KappaOn) { jRec = Math.Max(jRec, s0.JMagAPerMm2[i]); fRec = Math.Max(fRec, m.FracOf(i)); }
                double d = (s1.JMaxAPerMm2 - s0.JMaxAPerMm2) / s0.JMaxAPerMm2;
                bool ok = Math.Abs(d) <= W08PeakTol;
                W($"{which}\t{lc.MeshFineMm:0.###}\t{m.CellCount}\t{s1.KappaAngleMin:0.0000}\t{s0.JMaxAPerMm2:0.000}\t{s1.JMaxAPerMm2:0.000}\t{d * 100:+0.000;-0.000}\t{s1.SliverReconCells}\t{jRec:0.000}\t{fRec:0.0000}\t{s0.TotalGenW:0.0000}\t{s1.TotalGenW:0.0000}\t{(ok ? "过" : "**不过**")}");
                if (!ok) { bad++; badL.Add($"{which} 细区 {lc.MeshFineMm}：{d * 100:+0.00;-0.00} %"); }
            }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 3 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }

    [Fact]
    public void 门4_门槛敏感性_0p15到0p3下Heater1三档J峰极差不超5percent()
    {
        string file = DeliverableOut.Stamped("网格修复2_门4_薄片格J峰_门槛敏感性.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 4：角度条件数门槛敏感性自证　Heater1 无孔 ×1／×0.5／×0.25　门槛 0.15／0.2（生产）／0.25／0.3");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：同一档下四个门槛的全体 J 峰极差 ≤ {ScaleSpreadTol:P0}（结果不该随门槛走 —— 门槛只要在幻影格与真格之间，落在哪里不重要）。");
        W("档\tκ′门槛\t单元\t截断格数\t全体J峰\t实格峰");
        var g = FieldPlacementTests.Heater1();
        int bad = 0; var badL = new List<string>();
        foreach (double s in new[] { 1.0, 0.5, 0.25 })
        {
            var peaks = new List<double>();
            foreach (double k in new[] { 0.15, 0.2, 0.25, 0.3 })
            {
                var (m, sc) = SolveShape(g, s, null, k);
                var pk = Measure(m, sc, false);
                W($"×{s}\t{k}\t{pk.Cells}\t{pk.Recon}\t{pk.All:0.000}\t{pk.Real:0.000}");
                peaks.Add(pk.All);
            }
            double spread = (peaks.Max() - peaks.Min()) / peaks.Min();
            W($"×{s}：四个门槛的全体峰极差 {spread:P2} {(spread <= ScaleSpreadTol ? "过" : "**不过**")}");
            if (spread > ScaleSpreadTol) { bad++; badL.Add($"×{s} 极差 {spread:P2}"); }
        }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 4 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }

    [Fact]
    public void 门5_碎格并入_改后非管孔格覆盖率不低于1e3_改回则碎格回来()
    {
        string file = DeliverableOut.Stamped("网格修复2_门5_碎格并入.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 5（复核第 10 条）：覆盖率 < 1e-3 的非管孔格并入邻格　W08 导航／判决／细 + Heater1 无孔 ×0.25");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：改后非管孔格最小覆盖率 ≥ {DustFrac}；关掉规则 B（MeshRules.MergeSlivers = false）的对照网格里至少一张有覆盖率 < {DustFrac} 的非管孔格（否则门空守）。");
        W("网格\t规则B\t单元\t并入(B/A)\t非管孔格最小覆盖率\t覆盖率<1e-3的非管孔格数\t其中最小面积mm²");
        var off = new MeshRules { MergeSlivers = false };
        int bad = 0; int dustOff = 0; var badL = new List<string>();
        var cases = new List<(string name, Func<MeshRules?, (ShellMesh m, double rh)> build)>();
        foreach (double fine in new[] { 0.0, 1.0, 0.5 })
        {
            double f = fine;
            cases.Add(($"W08 细区 {(fine == 0 ? "导航" : fine.ToString("0.0"))}", r => { var (lc, g, m) = BuildDesign("W08", f, r); return (m, g.HoleRadiusMm); }));
        }
        cases.Add(("Heater1 ×0.25", r => { var g = FieldPlacementTests.Heater1(); var (m, _) = SolveShape(g, 0.25, r, KappaOn); return (m, g.HoleRadiusMm); }));
        foreach (var (name, build) in cases)
            foreach (var (tag, rules) in new[] { ("开", (MeshRules?)null), ("关", off) })
            {
                var (m, rh) = build(rules);
                double minF = 1, minA = double.PositiveInfinity; int nDust = 0;
                for (int i = 0; i < m.CellCount; i++)
                {
                    if (HoleCell(m, i, rh)) continue;
                    minF = Math.Min(minF, m.FracOf(i));
                    if (m.FracOf(i) < DustFrac) { nDust++; minA = Math.Min(minA, m.Area[i]); }
                }
                W($"{name}\t{tag}\t{m.CellCount}\t{m.SliversMerged}/{m.HoleSliversMerged}\t{minF:0.0e0}\t{nDust}\t{(nDust > 0 ? minA.ToString("0.0e0") : "—")}");
                if (rules is null && nDust > 0) { bad++; badL.Add($"{name} 改后还有 {nDust} 个碎格"); }
                if (rules is not null) dustOff += nDust;
            }
        if (dustOff == 0) { bad++; badL.Add("关掉规则 B 的对照里一个碎格都没有 ⇒ 门空守"); }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}；对照（关）碎格合计 {dustOff}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 5 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }

    [Fact]
    public void 门6_F5轴端余数规则_盘30p05最小格距不小于四分之一细步_去掉规则则发丝格回来()
    {
        string file = DeliverableOut.Stamped("网格修复2_门6_F5轴端规则.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复第二轮 门 6（复核第 6 条）：F5 轴端余数规则　W08 片0 盘径 30.01／30.05／30.10／30.49（舌半宽 30）导航网格");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W("门槛（跑前写死）：生产规则下 z 轴最小格距 ≥ hFine/4；关掉轴端规则（MeshRules.AxisEndRule = false）后四个盘径里至少一个出现 < hFine/10 的发丝格（否则门空守）。");
        W("R\t规则\tz节点数\tz最小格距\thFine\t舌中格最大长宽比\t判读");
        var p = new DesignInputs();
        int bad = 0, hairOff = 0; var badL = new List<string>();
        foreach (double R in new[] { 30.01, 30.05, 30.10, 30.49 })
            foreach (var (tag, rules) in new[] { ("开", (MeshRules?)null), ("关", new MeshRules { AxisEndRule = false }) })
            {
                var d = R48NMeshGateTests.Design("W08"); R48NMeshGateTests.SetRW(d, R, 30.0);
                var dummy = new SolverResult { Design = d };
                Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
                var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);
                var lc = d.BuildCase(p);
                Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius });
                var m = LineRunner.PlateMeshAnalyticWith(lc, 0, rules, null);
                var zs = m.Nodes.Select(v => v.Z).Distinct().OrderBy(v => v).ToArray();
                double minDz = Enumerable.Range(1, zs.Length - 1).Min(i => zs[i] - zs[i - 1]);
                var (xt, w) = lc.FlangePlates[0].Tangent();
                double asp = 0;
                for (int c = 0; c < m.CellCount; c++)
                {
                    var (x0, x1, z0, z1) = FlangeMesher.CellRect(m, c);
                    if (m.Centroid[c].X < xt - 5 && m.Centroid[c].X > lc.FlangePlates[0].TabTipXMm + 5) asp = Math.Max(asp, Math.Max((x1 - x0) / (z1 - z0), (z1 - z0) / (x1 - x0)));
                }
                bool ok = rules is null ? minDz >= 0.25 * lc.MeshFineMm - 1e-9 : true;
                if (rules is not null && minDz < 0.1 * lc.MeshFineMm) hairOff++;
                W($"{R:0.00}\t{tag}\t{zs.Length}\t{minDz:0.000}\t{lc.MeshFineMm:0.###}\t{asp:0.0}\t{(rules is null ? (ok ? "过" : "**不过**") : (minDz < 0.1 * lc.MeshFineMm ? "发丝格（门不空守）" : "没有发丝格"))}");
                if (!ok) { bad++; badL.Add($"R{R:0.00} 最小格距 {minDz:0.000} < hFine/4"); }
            }
        if (hairOff == 0) { bad++; badL.Add("关掉轴端规则后没有发丝格 ⇒ 门空守"); }
        W($"── 不过 {bad}{(bad > 0 ? "：" + string.Join("；", badL) : "")}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 6 不过 {bad}：{string.Join("；", badL)}（{file}）");
    }
}
