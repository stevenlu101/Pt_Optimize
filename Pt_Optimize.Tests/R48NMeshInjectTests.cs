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
//  网格生成根因修复 —— 只在改后树上能编译的门（2026-09-18，Fable 5.1；2026-09-19 第二轮复核后改）：
//    · 积分器自检：AnalyticMaterial 的单元积分对测试侧 Simpson 参考（与生成器无共享代码）；带开孔／开槽的板对子采样参考；
//    · 面长门：内部面的有料长度 ≤ 整边长；舌片中段满料面 = 整边；管孔弧面总长 = 2π·孔半径（每一段弧都在）；
//    · (e) 注入门：格心取样 + 整边面长 + 阶梯孔边 + 不并格 ⇒ 体积守恒门当场红。
//        2026-09-19（复核第 9 条）：注入不再经生产码里的 [ThreadStatic] 开关（已删），改走 MeshRules（internal，InternalsVisibleTo）+ 测试侧材料源 CenterSampledMaterial，
//        经 LineRunner.PlateMeshAnalyticWith（生产配方的本体，不手抄参数表）；
//    · (e)-(c)：注入下导航 vs 判决的**单片**热解（DrawingPathParityTests.SolveOne）—— 第一轮那版走整线解，靠全局开关串进 LineRunner.Run；开关删了，整线解没有不留钩子的注入口，改成单片；
//    · (f) 改前／改后对拍（2026-09-19 复核第 2 条补，第一轮文件头说有、其实没写）：改前判决网格四行写死（出自改前快照上跑的 网格修复_改前快照_门f_现役数_*），
//        改后重跑，位移门槛 ≤ 0.5 K／≤ 0.5 W／≤ 5 g 跑前写死；超了红着交、写明原因，不挪门槛（慢）。
// ════════════════════════════════════════════════════════════════════════════
public class R48NMeshInjectTests
{
    private readonly ITestOutputHelper _o;
    public R48NMeshInjectTests(ITestOutputHelper o) { _o = o; }
    const string Sign = R48NMeshGateTests.Sign;
    const string Sign2 = "2026-09-19，Fable 5.1";

    /// <summary>
    /// 注入用的材料源（只在测试侧）：格心在板内 ⇒ 整格有料、厚度取格心；否则空格；线段有料长度 = 整边（老口径「面长不裁剪」）。
    /// 与 2026-09-18 生产码里那半边注入逐字同义；生产码不认识这个类。
    /// </summary>
    internal sealed class CenterSampledMaterial : IMaterialField
    {
        private readonly FlangePlate _g; private readonly AnalyticMaterial _exact;
        public CenterSampledMaterial(FlangePlate g) { _g = g; _exact = new AnalyticMaterial(g); }
        public (double XMin, double XMax, double ZMin, double ZMax, bool Exact) MaterialEnvelope() => _exact.MaterialEnvelope();
        public MaterialIntegral Integrate(double x0, double x1, double z0, double z1)
        {
            double cx = 0.5 * (x0 + x1), cz = 0.5 * (z0 + z1);
            if (!_g.Inside(cx, cz)) return MaterialIntegral.Empty;
            double a = (x1 - x0) * (z1 - z0);
            return new MaterialIntegral(a, a * _g.ThicknessAt(cx, cz), a * cx, a * cz);
        }
        public (double Length, double Mid) SegmentMaterial(bool vertical, double line, double a, double b) => (Math.Abs(b - a), 0.5 * (a + b));
        public double FractionInsideCircle(double x0, double x1, double z0, double z1, double radiusMm) => _exact.FractionInsideCircle(x0, x1, z0, z1, radiusMm);
        public string Describe() => "注入：格心取样、整边面长（只给门用）";
    }

    /// <summary>注入规则：整边面长、阶梯孔边、不并格（配合 CenterSampledMaterial = 第一轮 InjectCenterSampling 的全部内容）。</summary>
    internal static readonly MeshRules InjectRules = new() { ClipFaces = false, HoleArcFaces = false, MergeHoleCorners = false, MergeSlivers = false };

    /// <summary>与 R48NMeshGateTests.Build 同一条路，只是网格经 LineRunner.PlateMeshAnalyticWith 可注入。</summary>
    internal static (LineCase lc, FlangePlate g, ShellMesh m, double reqRadius) Build(DesignSpec d, DesignInputs p, double fineMm, bool inject, int plate = 0)
    {
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
        var g = lc.FlangePlates[Math.Min(plate, lc.FlangePlates.Length - 1)];
        var m = inject ? LineRunner.PlateMeshAnalyticWith(lc, plate, InjectRules, gg => new CenterSampledMaterial(gg))
                       : LineRunner.PlateMeshAnalyticWith(lc, plate, null, null);
        return (lc, g, m, reqRadius);
    }

    // ─────────────────────────────────────────────── 测试侧参考：子采样（任何形状都能用，精度 ~ 1/n²）
    static (double area, double vol) SubSample(FlangePlate g, double x0, double x1, double z0, double z1, int n = 64)
    {
        double a = 0, v = 0, da = (x1 - x0) * (z1 - z0) / (n * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x = x0 + (i + 0.5) * (x1 - x0) / n, z = z0 + (j + 0.5) * (z1 - z0) / n;
                if (!g.Inside(x, z)) continue;
                a += da; v += da * g.ThicknessAt(x, z);
            }
        return (a, v);
    }

    /// <summary>并过别的格的单元：格体积 ≠ 本节点矩形的积分，逐格对账时跳过（它们的体积由全片体积守恒门管）。</summary>
    static bool IsMergedCell(ShellMesh m, int c) => m.CellRects != null && c < m.CellRects.Length && m.CellRects[c].Count > 1;

    [Fact]
    public void 积分器自检_W08板_全片与逐格对Simpson参考_相对差小于1e6()
    {
        var p = new DesignInputs();
        var d = R48LW08NavDesign.Build();
        foreach (double R in new[] { 30.0, 30.75, 31.0 })
        {
            var dd = d.Clone(); R48NMeshGateTests.SetRW(dd, R, 30.0);
            var (lc, g, m, _) = R48NMeshGateTests.Build(dd, p, 0);
            var mat = new AnalyticMaterial(g);
            var env = mat.MaterialEnvelope();
            var whole = mat.Integrate(env.XMin, env.XMax, env.ZMin, env.ZMax);
            var rf = R48NMeshGateTests.Reference(g, 0.005, 0.05);
            // 体积的参考：Simpson 参考在焊脚环的 √ 奇点上自带 ~1e-5 的误差（网格审计 ① 自检里闭式对账差 1.8e-5），
            //   所以 w = R（无圆角）那一档另用闭式：无圆角无焊脚闭式 + 焊脚环（只在 x ≥ 切点那半边有，1D 换元 d = 焊脚(1 − cos φ) 后光滑，Simpson 4000 段）
            double weld = 0;
            if (g.WeldFilletLegMm > 1e-9)
            {
                double leg = g.WeldFilletLegMm, rh = g.HoleRadiusMm, xt = g.Tangent().X;
                int n = 4000; double h = (Math.PI / 2) / n, sum = 0;
                for (int i = 0; i <= n; i++)
                {
                    double phi = i * h, dpos = leg * (1 - Math.Cos(phi)), hw = leg * (1 - Math.Sin(phi)), r = rh + dpos;
                    double f = 2 * hw * (2 * Math.Acos(Math.Clamp(xt / r, -1, 1))) * r * leg * Math.Sin(phi);
                    sum += (i == 0 || i == n ? 1 : (i % 2 == 1 ? 4 : 2)) * f;
                }
                weld = sum * h / 3;
            }
            bool closedOk = !(g.TabParallel && g.TabFilletMm > 1e-9 && g.Tangent().HalfW < g.DiscRadiusMm);   // 无圆角才有闭式
            double vExact = closedOk ? rf.VolClosed0 + weld : double.NaN;
            _o.WriteLine($"R{R}：全片 面积 {whole.Area:0.0000} vs 参考 {rf.Area:0.0000}（{(whole.Area - rf.Area) / rf.Area:0.0e0}）；体积 {whole.Volume:0.0000} vs Simpson 参考 {rf.Vol:0.0000}（{(whole.Volume - rf.Vol) / rf.Vol:0.0e0}）"
                         + (closedOk ? $" vs 闭式+焊脚环 {vExact:0.0000}（{(whole.Volume - vExact) / vExact:0.0e0}）" : "（有圆角，无闭式）"));
            Assert.True(Math.Abs(whole.Area - rf.Area) / rf.Area < 1e-6, "全片面积对不上参考");
            if (closedOk) Assert.True(Math.Abs(whole.Volume - vExact) / vExact < 1e-6, "全片体积对不上闭式+焊脚环");
            Assert.True(Math.Abs(whole.Volume - rf.Vol) / rf.Vol < 3e-5, "全片体积对不上 Simpson 参考（容差 = 参考自身在焊脚奇点上的误差量级）");
            // 逐格：网格每格的体积 vs 子采样 256×256。子采样对被曲线边切过的格只准到 ~周长/面积/256 ≈ 4e-3，对满格是精确的 ⇒
            //   满格（覆盖率 > 0.999）要求 1e-9，部分格（覆盖率 ≥ 5 %）要求 5e-3（这是参考的精度，不是积分器的）
            double worstFull = 0, worstPart = 0; int worstCell = -1, merged = 0;
            for (int c = 0; c < m.CellCount; c++)
            {
                var nd = m.Cells[c];
                double x0 = m.Nodes[nd[0]].X, x1 = m.Nodes[nd[2]].X, z0 = m.Nodes[nd[0]].Z, z1 = m.Nodes[nd[2]].Z;
                if (m.Frac[c] < 0.05) continue;
                if (IsMergedCell(m, c)) { merged++; continue; }   // 2026-09-19：并过格的单元（规则 A／B）格体积 ≠ 本矩形积分
                var (a, v) = SubSample(g, x0, x1, z0, z1, 256);
                double err = Math.Abs(m.Area[c] * m.Thickness[c] - v) / Math.Max(v, 1e-9);
                if (m.Frac[c] > 0.999 && Math.Abs(a - (x1 - x0) * (z1 - z0)) < 1e-9 * a) worstFull = Math.Max(worstFull, err);
                else if (err > worstPart) { worstPart = err; worstCell = c; }
            }
            _o.WriteLine($"R{R}：逐格 满格最大相对差 {worstFull:0.0e0}；部分格（覆盖率 ≥ 5 %）最大相对差 {worstPart:0.0e0}（格 {worstCell}）；并过格的单元 {merged} 个跳过");
            Assert.True(worstFull < 1e-5, $"满格体积最大相对差 {worstFull:0.0e0}（焊脚环里厚度非常数，子采样自身 ~3e-6）");
            Assert.True(worstPart < 5e-3, $"部分格体积最大相对差 {worstPart:0.0e0} ≥ 5e-3（子采样参考的精度量级）");
            // 分区份额：跨保温圆的格的份额 ∈ [0,1]，且 Σ vol·f = 环内参考（份额路径的正确性；并过格的按全部矩形量）
            double vIn = 0;
            for (int c = 0; c < m.CellCount; c++)
            {
                double f = FlangeMesher.MaterialFractionInCircle(m, c, R);
                Assert.True(!double.IsNaN(f) && f >= 0 && f <= 1, $"份额越界 {f}");
                vIn += m.Area[c] * m.Thickness[c] * f;
            }
            _o.WriteLine($"R{R}：环内 Σvol·份额 {vIn:0.0000} vs 参考 {rf.VolInDisc:0.0000}（{(vIn - rf.VolInDisc) / rf.VolInDisc:0.0e0}）");
            Assert.True(Math.Abs(vIn - rf.VolInDisc) / rf.VolInDisc < 2e-4, "环内份额对不上参考");
        }
    }

    [Fact]
    public void 积分器自检_带舌孔与盘槽的板_对子采样参考_相对差小于1e4()
    {
        var p = new DesignInputs();
        var d = R48LW08NavDesign.Build();
        d.TabHoleRMm = new[] { 6.0, 0, 0, 0 };
        d.SlotSpanDeg = new[] { 40.0, 0, 0, 0 };
        var (lc, g, m, _) = R48NMeshGateTests.Build(d, p, 0);
        Assert.True(g.TabHoles.Length > 0 || g.DiscSlots.Length > 0 || g.DiscCutHoles.Length > 0, "这块板本该有孔或槽");
        var mat = new AnalyticMaterial(g);
        var env = mat.MaterialEnvelope();
        // 全片：子采样 2000×800（步 ~0.09 mm）
        double xs0 = env.XMin, xs1 = env.XMax, zs0 = env.ZMin, zs1 = env.ZMax;
        int nx = 2000, nz = 800; double a = 0, v = 0, da = (xs1 - xs0) * (zs1 - zs0) / ((double)nx * nz);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                double x = xs0 + (i + 0.5) * (xs1 - xs0) / nx, z = zs0 + (j + 0.5) * (zs1 - zs0) / nz;
                if (!g.Inside(x, z)) continue;
                a += da; v += da * g.ThicknessAt(x, z);
            }
        var whole = mat.Integrate(xs0, xs1, zs0, zs1);
        _o.WriteLine($"带孔槽板：面积 {whole.Area:0.000} vs 子采样 {a:0.000}（{(whole.Area - a) / a:0.0e0}）；体积 {whole.Volume:0.000} vs {v:0.000}（{(whole.Volume - v) / v:0.0e0}）；网格体积 {m.VolumeMm3:0.000}");
        Assert.True(Math.Abs(whole.Area - a) / a < 1e-3, "面积对不上子采样（子采样自身精度 ~1e-3）");
        Assert.True(Math.Abs(whole.Volume - v) / v < 1e-3, "体积对不上子采样");
        Assert.True(Math.Abs(m.VolumeMm3 - whole.Volume) / whole.Volume < 1e-6, "网格体积 ≠ 材料场全片积分");
        // 逐格（跨孔／槽边的格）：对 512×512 子采样
        double worst = 0;
        foreach (var cut in g.TabHoles.Select(h => (h.XMm, h.ZMm, h.RMm * 2)).Concat(g.DiscSlots.Select(s => (0.0, 0.0, s.ROutMm))))
            for (int c = 0; c < m.CellCount; c++)
            {
                var nd = m.Cells[c];
                double x0 = m.Nodes[nd[0]].X, x1 = m.Nodes[nd[2]].X, z0 = m.Nodes[nd[0]].Z, z1 = m.Nodes[nd[2]].Z;
                if (Math.Abs(0.5 * (x0 + x1) - cut.Item1) > cut.Item3 || Math.Abs(0.5 * (z0 + z1) - cut.Item2) > cut.Item3) continue;
                if (m.Frac[c] < 0.2) continue;
                if (IsMergedCell(m, c)) continue;
                var (ca, cv) = SubSample(g, x0, x1, z0, z1, 512);
                worst = Math.Max(worst, Math.Abs(m.Area[c] * m.Thickness[c] - cv) / Math.Max(cv, 1e-9));
            }
        _o.WriteLine($"跨孔／槽边的格（覆盖率 ≥ 20 %）最大相对差 {worst:0.0e0}");
        Assert.True(worst < 2e-3, $"跨孔槽格体积最大相对差 {worst:0.0e0}");
    }

    /// <summary>两格在面所在直线上的公共整边长（并过格的按全部矩形取最大重叠）；两格没有公共边 ⇒ 0。</summary>
    static double SharedEdge(ShellMesh m, MeshFace f)
    {
        var ra = m.CellRects != null ? m.CellRects[f.A] : new List<(double, double, double, double)> { FlangeMesher.CellRect(m, f.A) };
        var rb = m.CellRects != null ? m.CellRects[f.B] : new List<(double, double, double, double)> { FlangeMesher.CellRect(m, f.B) };
        double best = 0;
        foreach (var (ax0, ax1, az0, az1) in ra)
            foreach (var (bx0, bx1, bz0, bz1) in rb)
            {
                double ox = Math.Min(ax1, bx1) - Math.Max(ax0, bx0), oz = Math.Min(az1, bz1) - Math.Max(az0, bz0);
                bool shareX = (Math.Abs(ax1 - bx0) <= 1e-9 || Math.Abs(bx1 - ax0) <= 1e-9) && oz > 1e-9;
                bool shareZ = (Math.Abs(az1 - bz0) <= 1e-9 || Math.Abs(bz1 - az0) <= 1e-9) && ox > 1e-9;
                if (shareX) best = Math.Max(best, oz);
                if (shareZ) best = Math.Max(best, ox);
            }
        return best;
    }

    [Fact]
    public void 面长门_有料面长不超整边_舌中段满料_管孔弧面总长等于周长()
    {
        var p = new DesignInputs();
        foreach (double R in new[] { 30.0, 30.75, 31.01 })
            foreach (double fine in new[] { 0.0, 1.0 })
            {
                var d = R48LW08NavDesign.Build(); R48NMeshGateTests.SetRW(d, R, 30.0);
                var (lc, g, m, _) = R48NMeshGateTests.Build(d, p, fine);
                double rh = g.HoleRadiusMm; var (xt, w) = g.Tangent();
                int viol = 0, noEdge = 0; double sL = 0, sE = 0;
                foreach (var f in m.Faces)
                {
                    if (f.B < 0) continue;
                    double edge = SharedEdge(m, f);   // 2026-09-19：整边长按全部矩形量（并过格的单元的节点四边形只是目标格自己那一个）
                    if (edge <= 1e-9) { noEdge++; continue; }
                    if (f.Length > edge + 1e-9) viol++;
                    if (f.Mid.X < Math.Min(xt, -rh) - 5 && f.Mid.X > g.TabTipXMm + 5 && Math.Abs(f.Mid.Z) < w - 2.5) { sL += f.Length; sE += edge; }   // 舌片中段：切点与管孔都在右边
                }
                var arcs = m.Faces.Where(f => f.B < 0 && f.Tag == ShellMesh.TagHole).ToArray();
                double arcLen = arcs.Sum(f => f.Length);
                double rOff = arcs.Max(f => Math.Abs(Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z) - rh));
                _o.WriteLine($"R{R} 细区 {lc.MeshFineMm}：面长 > 整边 {viol} 条（两格不共边 {noEdge} 条；并入 A {m.HoleSliversMerged}／B {m.SliversMerged} 格）；舌中段 Σ面长 {sL:0.000} = Σ整边 {sE:0.000}；管孔弧面 {arcs.Length} 条 总长 {arcLen:0.000} vs 2πr {2 * Math.PI * rh:0.000}，弧中点离孔圆最大 {rOff:0.0e0} mm");
                Assert.Equal(0, viol);
                Assert.Equal(0, noEdge);   // 面图收缩后每条内部面都落在两格的公共整边上
                Assert.True(Math.Abs(sL - sE) < 1e-9, "舌中段满料面的面长必须等于整边");
                Assert.True(Math.Abs(arcLen - 2 * Math.PI * rh) < 1e-6, "管孔弧面总长必须等于周长（每一段弧都要在）");
                Assert.True(rOff < 1e-9, "弧面中点必须在孔圆上");
                Assert.Equal(0.0, m.HoleTagMaxROverMm, 9);
            }
    }

    // ─────────────────────────────────────────────── (e) 注入
    [Fact]
    public void 门e_注入格心取样_体积守恒门当场红_导航vs判决电阻差当场超坏带线()
    {
        var p = new DesignInputs();
        var d0 = R48LW08NavDesign.Build();
        string file = DeliverableOut.Stamped("网格修复_门e_注入格心取样_W08.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复 门(e) 注入：MeshRules{整边面长、阶梯孔边、不并格} + 测试侧 CenterSampledMaterial（格心取样）　W08 片0　盘径 30.50／30.75／31.00／31.01");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}；2026-09-19 注入改走规则 + 材料源（复核第 9 条），断言不变");
        W($"预期（跑前写死）：注入后体积守恒门（{R48NMeshGateTests.VolTolPct} %）至少一张网格红；关掉注入后过。导航 vs 判决等温电阻差另印（坏带线 1 %，只印不判 —— 格心取样不等于改前的栅格带格，电阻差多大要实测）；(c) 那半边见 门e_注入下的单片热解。");
        W("R\t注入\t" + R48NMeshGateTests.Head + "\t判决R µΩ\te%");
        int redVol = 0, redBand = 0, greenVolFail = 0, greenBandFail = 0;
        foreach (bool inject in new[] { true, false })
            foreach (double R in new[] { 30.50, 30.75, 31.00, 31.01 })
            {
                var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, 30.0);
                var (lc, g, m, req) = Build(d, p, 0, inject);
                var rf = R48NMeshGateTests.Reference(g);
                var nav = R48NMeshGateTests.Measure("导航", lc, g, m, rf, req, solve: true);
                var dj = d0.Clone(); R48NMeshGateTests.SetRW(dj, R, 30.0);
                var (lcj, gj, mj, reqj) = Build(dj, p, 1.0, inject);
                var jud = R48NMeshGateTests.Measure("判决", lcj, gj, mj, rf, reqj, solve: true);
                double e = (nav.RMeshOhm - jud.RMeshOhm) / jud.RMeshOhm * 100;
                bool volBad = Math.Abs(nav.VMesh - nav.VRef) / nav.VRef * 100 > R48NMeshGateTests.VolTolPct
                           || Math.Abs(jud.VMesh - jud.VRef) / jud.VRef * 100 > R48NMeshGateTests.VolTolPct;
                bool bandBad = Math.Abs(e) > 1.0;
                if (inject) { if (volBad) redVol++; if (bandBad) redBand++; }
                else { if (volBad) greenVolFail++; if (bandBad) greenBandFail++; }
                W($"{R:0.00}\t{(inject ? "是" : "否")}\t{R48NMeshGateTests.Line(nav)}\t{jud.RMeshOhm * 1e6:0.0000}\t{e:+0.000;-0.000}");
            }
        W($"── 注入：体积门红 {redVol}/4 张（导航或判决）、坏带 {redBand}/4 点；关掉：体积门红 {greenVolFail}、坏带 {greenBandFail}　{Sign2}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(redVol > 0, "注入格心取样后体积守恒门竟然没红 —— 门是空守");
        Assert.True(greenVolFail == 0 && greenBandFail == 0, "关掉注入后本该全过");
    }

    /// <summary>
    /// (e) 的 (c) 半边（2026-09-19 改成单片）：注入格心取样，盘 30.75／31.00 导航 vs 判决**单片**热解（片0，电流 1214 A、控温 1150、夹持温度与舌保温取设计值），
    /// 预期（跑前写死）：注入下 R31.00 |Δ抽热(导航−判决)| ≥ 0.5 W 或 |Δ发热| ≥ 1 %；对照（不注入）两点 |Δ抽热| ≤ 0.5 W 且 |Δ发热| ≤ 1 %。
    /// 第一轮整线解版（deliverable/网格修复_门e_注入下的一致门_W08_本次开跑于2026-09-18_*）：注入 R31.00 −3.13 K／+1.76 K／+1.39 W 门外、R30.75 门内；对照两点门内。
    /// </summary>
    [Fact]
    public void 门e_注入下的单片热解_盘30p75与31p00_导航vs判决()
    {
        var d0 = R48LW08NavDesign.Build();
        var p = new DesignInputs();
        string file = DeliverableOut.Stamped("网格修复_门e_注入下的单片热解_W08.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复 门(e)-(c)（单片版）：注入格心取样后，盘 30.75／31.00 导航 vs 判决 片0 电流场 + 热场（DrawingPathParityTests.SolveOne）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign2}");
        W("预期（跑前写死）：注入下 R31.00 |Δ抽热| ≥ 0.5 W 或 |Δ发热| ≥ 1 %；对照两点 |Δ抽热| ≤ 0.5 W 且 |Δ发热| ≤ 1 %。");
        W("注入\tR\t导航单元\t判决单元\t导航发热W\t判决发热W\t导航抽热W\t判决抽热W\tΔ发热%\tΔ抽热W\t判读");
        int redInj = 0, redCtl = 0; var rows = new List<string>();
        foreach (bool inject in new[] { true, false })
            foreach (double R in new[] { 30.75, 31.00 })
            {
                var dn = d0.Clone(); R48NMeshGateTests.SetRW(dn, R, 30.0);
                var (lcn, gn, mn, _) = Build(dn, p, 0.0, inject);
                var dj = d0.Clone(); R48NMeshGateTests.SetRW(dj, R, 30.0);
                var (lcj, gj, mj, _) = Build(dj, p, 1.0, inject);
                double tSet = R48NMeshGateTests.PlateTempC, iA = R48NMeshGateTests.PlateCurrentA;
                var a = DrawingPathParityTests.SolveOne(mn, lcn, gn, iA, tSet, tSet, dn.ClampTempC, dn.TabInsulMm[0]);
                var b = DrawingPathParityTests.SolveOne(mj, lcj, gj, iA, tSet, tSet, dj.ClampTempC, dj.TabInsulMm[0]);
                double dg = (a.QGen - b.QGen) / b.QGen * 100, dq = a.QFromTube - b.QFromTube;
                bool outside = Math.Abs(dq) >= 0.5 || Math.Abs(dg) >= 1.0;
                W($"{(inject ? "是" : "否")}\t{R:0.00}\t{a.Cells}\t{b.Cells}\t{a.QGen:0.000}\t{b.QGen:0.000}\t{a.QFromTube:0.000}\t{b.QFromTube:0.000}\t{dg:+0.000;-0.000}\t{dq:+0.000;-0.000}\t{(outside ? "**门外**" : "门内")}");
                if (inject && Math.Abs(R - 31.0) < 1e-9 && outside) redInj++;
                if (!inject && outside) redCtl++;
            }
        W($"── 注入 R31.00 门外 {redInj}/1、对照门外 {redCtl}/2　{Sign2}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(redInj > 0, "注入格心取样后单片热解的导航 vs 判决差竟然没超线 —— 门是空守（或单片版量不到那个病）");
        Assert.True(redCtl == 0, "对照（不注入）本该在门内");
    }

    // ─────────────────────────────────────────────── (f) 改前／改后对拍（慢）：改前判决网格四行写死
    /// <summary>
    /// 改前值（判决网格）：出自改前快照上跑的 deliverable/网格修复_改前快照_门f_现役数_{W08,W06}_本次开跑于2026-09-18_195938.txt（三条判据 + 铂重）。
    /// 第一轮改后（同日 网格修复_门f_现役数_*_195938）另列，只作参照：W08 盘10 −8.787／18.109／5.311／4247.4、盘20 4.878／3.045／0.035／4247.4；
    /// W06 盘10 −9.680／24.024／6.060／3380.7、盘20 5.255／4.171／0.326／3380.7。
    /// </summary>
    internal static readonly Dictionary<(string which, double disc), (double hot, double cold, double flux, double mass)> PreChange = new()
    {
        [("W08", 10)] = (-9.260, 19.004, 5.792, 4245.3),
        [("W08", 20)] = (4.556, 3.786, 0.349, 4245.3),
        [("W06", 10)] = (-10.236, 24.544, 6.413, 3378.9),
        [("W06", 20)] = (4.490, 4.819, 0.597, 3378.9),
    };
    internal const double ShiftTolK = 0.5, ShiftTolW = 0.5, ShiftTolG = 5.0;   // 跑前写死（复核第 2 条）

    [Trait("速度", "慢")]
    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 门f_对拍_改前判决网格四行写死_改后位移不超0p5K_0p5W_5g(string which)
    {
        var d0 = R48NMeshGateTests.Design(which);
        string file = DeliverableOut.Stamped($"网格修复2_门f_对拍_{which}.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        W($"网格修复第二轮 门(f) 对拍　{which}（{d0.Name}）　圆盘保温 10／20 mm × 判决网格（MeshVerify.RequiredMeshFor）　带玻璃稳态 ② 三条判据 + 铂重");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign2}　并发 2");
        W($"改前值写死（出自 网格修复_改前快照_门f_现役数_{which}_本次开跑于2026-09-18_195938.txt）；位移门槛（跑前写死）|Δ| ≤ {ShiftTolK} K／{ShiftTolW} W／{ShiftTolG} g；超了红着交、写明原因，不挪。");
        W($"基准设计：{(which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source)}");
        W(); W(R48NMeshGateTests.LineHead);
        var reqFine = MeshVerify.RequiredFineMmFor(d0);
        var jobs = new List<(double disc, DesignSpec d)>();
        foreach (double disc in new[] { 10.0, 20.0 })
        {
            var d = d0.Clone();
            d.FlangeInsulated = true; d.FlangeInsulMm = disc;
            d.DiscInsulMm = Array.Empty<double>();
            jobs.Add((disc, d));
        }
        var p = new DesignInputs();
        var rows = new R48NMeshGateTests.LineRow[jobs.Count];
        System.Threading.Tasks.Parallel.For(0, jobs.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 2 },
            i => rows[i] = R48NMeshGateTests.SolveLine($"盘{jobs[i].disc:0}", jobs[i].d, p, reqFine, jobs[i].d.DiscRadiusMm, jobs[i].d.TabHalfWidthMm, "判决"));
        foreach (var r in rows) W(r.Text);
        W(); W("═══════ 对拍：改后 − 改前（判决网格）═══════");
        W("盘保温\t改前最热铂\t改后最热铂\tΔ\t改前管根\t改后管根\tΔ\t改前净流入\t改后净流入\tΔ\t改前铂重\t改后铂重\tΔ\t判读");
        int bad = 0; var badL = new List<string>();
        for (int i = 0; i < jobs.Count; i++)
        {
            var pre = PreChange[(which, jobs[i].disc)]; var r = rows[i];
            if (!(r.Ok && r.Converged)) { bad++; badL.Add($"盘{jobs[i].disc:0} 判不了：{r.Message}"); W($"盘{jobs[i].disc:0}\t判不了：{r.Message}"); continue; }
            double dh = r.Hot - pre.hot, dc = r.Cold - pre.cold, df = r.Flux - pre.flux, dm = r.Mass - pre.mass;
            var notes = new List<string>();
            if (Math.Abs(dh) > ShiftTolK) notes.Add($"最热铂 {dh:+0.000;-0.000} K");
            if (Math.Abs(dc) > ShiftTolK) notes.Add($"管根 {dc:+0.000;-0.000} K");
            if (Math.Abs(df) > ShiftTolW) notes.Add($"净流入 {df:+0.000;-0.000} W");
            if (Math.Abs(dm) > ShiftTolG) notes.Add($"铂重 {dm:+0.0;-0.0} g");
            W($"盘{jobs[i].disc:0}\t{pre.hot:0.000}\t{r.Hot:0.000}\t{dh:+0.000;-0.000}\t{pre.cold:0.000}\t{r.Cold:0.000}\t{dc:+0.000;-0.000}\t{pre.flux:0.000}\t{r.Flux:0.000}\t{df:+0.000;-0.000}\t{pre.mass:0.0}\t{r.Mass:0.0}\t{dm:+0.0;-0.0}\t{(notes.Count == 0 ? "过" : "**不过：" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { bad++; badL.Add($"盘{jobs[i].disc:0}：{string.Join("；", notes)}"); }
        }
        W($"── 不过 {bad}/{jobs.Count}{(bad > 0 ? "：" + string.Join("　", badL) : "")}　总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　{Sign2}");
        W("出处：整线解 = LineRunner.Run；网格 = Solver.ApplyCaseMesh（判决 FineMm = MeshVerify.RequiredMeshFor）；读数 = LineResult.ValueOf。改前值 = 改前快照文件里的判决行。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(bad == 0, $"门 f 对拍不过 {bad}/{jobs.Count}：{string.Join("　", badL)}（{file}）");
    }

    // ─────────────────────────────────────────────── 诊断：两个相邻盘径的导航网格哪里不同（只印）
    [Fact]
    public void 诊断_相邻盘径导航网格差异_30p99_vs_31p00()
    {
        var p = new DesignInputs();
        ShellMesh Mesh(double R, out FlangePlate g, out LineCase lc) { var d = R48LW08NavDesign.Build(); R48NMeshGateTests.SetRW(d, R, 30.0); var t = R48NMeshGateTests.Build(d, p, 0); g = t.g; lc = t.lc; return t.m; }
        var mA = Mesh(30.99, out var gA, out var lcA); var mB = Mesh(31.00, out var gB, out var lcB);
        double rho = Materials.PtResistivity(1150) * 1e3;
        var sA = ShellCurrent.Solve(mA, 1214, rho, 1150); var sB = ShellCurrent.Solve(mB, 1214, rho, 1150);
        _o.WriteLine($"30.99：{mA.CellCount} 格 R {rho / sA.CurrentInA * 1e6:0.0000} µΩ；31.00：{mB.CellCount} 格 R {rho / sB.CurrentInA * 1e6:0.0000} µΩ");
        foreach (var (m, s, tag) in new[] { (mA, sA, "30.99"), (mB, sB, "31.00") })
        {
            var pinned = new HashSet<int>();
            foreach (var f in m.Faces) if (f.B < 0 && f.Tag == ShellMesh.TagHole) pinned.Add(f.A);
            double aPin = pinned.Sum(i => m.Area[i]);
            var xs = m.Nodes.Select(v => v.X).Distinct().OrderBy(v => v).ToArray();
            _o.WriteLine($"{tag}：钉 V=0 的格 {pinned.Count} 个，面积和 {aPin:0.000} mm²；x 节点（−12～0）：{string.Join(" ", xs.Where(x => x >= -12 && x <= 0).Select(x => x.ToString("0.###")))}");
            var small = Enumerable.Range(0, m.CellCount).Where(i => m.Frac[i] < 0.01).ToArray();
            _o.WriteLine($"{tag}：覆盖率 < 1 % 的格 {small.Length} 个：{string.Join(" ", small.Take(12).Select(i => $"({m.Centroid[i].X:0.00},{m.Centroid[i].Z:0.00}) f={m.Frac[i]:0.0e0}"))}");
        }
        Assert.True(true);
    }

}
