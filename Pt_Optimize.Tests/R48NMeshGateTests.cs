using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  网格生成根因修复的门（2026-09-18，Fable 5.1）—— 门槛**跑前写死**，跑完不挪。
//
//  只用改动前就有的公开接口（LineRunner.PlateMeshAnalytic／Solver.ApplyCaseMesh／ShellCurrent.Solve／LineRunner.Run／网格字段），
//  所以同一份文件在改动前的源码快照上也能跑 —— 改前／改后两组数出自同一把尺子。
//
//  ══ 门
//    (a) 体积守恒：W08／W06 × 盘径 28～34（0.25 步）× 舌半宽 30／28／26 × 导航／判决 × 每片：每片总体积、环内（r ≤ R）、舌区（x < 切点）
//        对参考积分 |ΔV|/V ≤ 0.1 %。参考积分 = 板件 Inside/HalfWidth/ThicknessAt 的二维 Simpson（与网格生成器无共享代码），
//        并对无圆角无焊脚的闭式自检（≤ 1e-3）。
//    (b) 连续性：盘径 30.00～31.50 每 0.01：相邻两点 |ΔV_mesh − ΔV_ref| ≤ 一格体积；相邻等温电阻变化 ≤ 0.2 %；舌段带格数恒 0。
//    (c) 一致门（慢）：盘径 28～34 每 0.25，导航 vs 判决整线解，三条判据差 ≤ 2 K／≤ 1 W。改前在 30.75／31.00 是 19～26 K。
//    (d) 收敛门（慢）：判决 1.0 vs 细 0.5，盘 30／31／32，三条判据差 ≤ 0.5 K（净流入 ≤ 1 W）。
//    (f) 现役数（慢）：W08／W06 × 圆盘保温 10／20 mm × 判决／导航网格，三条判据 + 铂重逐位印出，供改前／改后对拍（对拍在 R48NMeshInjectTests，改后树上）。
//    闭合：等温电流场 P_gen/P_net ∈ [0.99, 1.01]（P_net = I²·ρ/CurrentInA 是离散网络真耗散；P_gen = Σ ρJ²tA 是报出去的发热）。
//    判不了（建不出网格／不收敛）一律不当过，照印。
// ════════════════════════════════════════════════════════════════════════════
public class R48NMeshGateTests
{
    private readonly ITestOutputHelper _o;
    public R48NMeshGateTests(ITestOutputHelper o) { _o = o; }

    internal const string Sign = "2026-09-18，Fable 5.1";
    internal const double PlateCurrentA = 1214.0;   // 片0 设计电流（网格审计 ① 同一口径）
    internal const double PlateTempC = 1150.0;

    // 跑前写死的门槛
    internal const double VolTolPct = 0.1;          // (a)
    internal const double RStepTolPct = 0.2;        // (b) 相邻电阻变化
    internal const double ConsistTolK = 2.0;        // (c)
    internal const double ConsistTolW = 1.0;        // (c)
    internal const double ConvTolK = 0.5;           // (d)
    internal const double ConvTolW = 1.0;           // (d) 净流入
    internal const double ClosureLo = 0.99, ClosureHi = 1.01;

    // ─────────────────────────────────────────────── 几何真值（参考积分；与生成器无共享代码）
    internal sealed class Ref
    {
        public double Area, Vol, VolInDisc, VolOnTab;
        public double AreaClosed0, VolClosed0;
        public double XT, W, Tip, R, Rh;
    }

    static double Simpson(Func<double, double> f, double a, double b, int n)
    {
        if (b <= a) return 0;
        if (n < 2) n = 2; if ((n & 1) == 1) n++;
        double h = (b - a) / n, s = f(a) + f(b);
        for (int i = 1; i < n; i++) s += (i % 2 == 1 ? 4 : 2) * f(a + i * h);
        return s * h / 3;
    }

    static double ColInt(FlangePlate g, double x, double z0, double z1, List<double> rBreaks, double dz)
    {
        if (z1 <= z0 + 1e-15) return 0;
        var zb = new List<double> { z0, z1 };
        foreach (double r in rBreaks) if (r > Math.Abs(x)) { double z = Math.Sqrt(r * r - x * x); if (z > z0 && z < z1) zb.Add(z); }
        zb.Sort();
        double s = 0;
        for (int k = 0; k + 1 < zb.Count; k++)
        {
            double a = zb[k], b = zb[k + 1];
            if (b - a < 1e-12) continue;
            int n = Math.Max(4, (int)Math.Ceiling((b - a) / dz));
            s += Simpson(z => g.ThicknessAt(x, z), a, b, n);
        }
        return s;
    }

    /// <summary>参考积分：x 向按断点分段 Simpson（步 dx），z 向按半径断点分段 Simpson（步 dz）。只写了单舌、无开孔开槽。</summary>
    internal static Ref Reference(FlangePlate g, double dx = 0.01, double dz = 0.05)
    {
        if (g.TwoTabs) throw new InvalidOperationException("参考积分只写了单舌");
        if (g.TabHoles.Length > 0 || g.DiscSlots.Length > 0 || g.DiscCutHoles.Length > 0)
            throw new InvalidOperationException("板上有孔/槽，本参考积分不成立");
        double R = g.DiscRadiusMm, rh = g.HoleRadiusMm, tip = g.TabTipXMm;
        var (xt, w) = g.Tangent();
        double leg = g.WeldFilletLegMm;
        var rBreaks = new List<double> { rh, R };
        if (leg > 1e-9) rBreaks.Add(rh + leg);
        rBreaks.AddRange(g.DiscStepRadiiMm);
        if (g.ThickenRadiusMm > rh) rBreaks.Add(g.ThickenRadiusMm);
        var bps = new List<double> { tip, xt, -rh, rh, R, 0.0 };
        double xc = double.NaN;
        if (g.TabParallel && g.TabFilletMm > 1e-9 && w < R)
        {
            double rf = g.TabFilletMm, zc = w + rf;
            xc = -Math.Sqrt(Math.Max(0, (R + rf) * (R + rf) - zc * zc));
            bps.Add(xc); bps.Add(xc * R / (R + rf));
        }
        if (leg > 1e-9) { bps.Add(-(rh + leg)); bps.Add(rh + leg); }
        foreach (double rr in g.DiscStepRadiiMm) { bps.Add(-rr); bps.Add(rr); }
        if (g.ExtensionMm > 1e-9) bps.Add(g.TabEndXMm);
        var xs = new List<double>();
        foreach (double v in bps.Where(v => v >= tip - 1e-9 && v <= R + 1e-9).OrderBy(v => v))
            if (xs.Count == 0 || v - xs[^1] > 1e-9) xs.Add(v);
        xs[0] = tip; xs[^1] = R;
        double rectEnd = Math.Min(double.IsNaN(xc) ? xt : xc, -rh) - 1e-9;

        double Col(double xRaw, bool discOnly, out double area)
        {
            double x = Math.Clamp(xRaw, tip + 1e-12, R - 1e-12);
            double zTop = g.HalfWidth(x);
            double zh = Math.Abs(x) < rh ? Math.Sqrt(rh * rh - x * x) : 0;
            area = 0;
            if (zTop <= zh + 1e-15) return 0;
            area = 2 * (zTop - zh);
            double zR = Math.Abs(x) < R ? Math.Sqrt(R * R - x * x) : 0;
            double zTopD = Math.Min(zTop, zR);
            if (discOnly) return zTopD > zh ? 2 * ColInt(g, x, zh, zTopD, rBreaks, dz) : 0;
            return 2 * ColInt(g, x, zh, zTop, rBreaks, dz);
        }

        var rf0 = new Ref { XT = xt, W = w, Tip = tip, R = R, Rh = rh };
        for (int k = 0; k + 1 < xs.Count; k++)
        {
            double a = xs[k], b = xs[k + 1];
            if (b - a < 1e-12) continue;
            bool rect = b <= rectEnd + 1e-6 && g.ExtensionMm <= 1e-9;
            int n = rect ? 2 : Math.Max(8, (int)Math.Ceiling((b - a) / dx));
            if (rect && (Math.Abs(g.HalfWidth(a + 1e-9) - w) > 1e-9 || Math.Abs(g.HalfWidth(b - 1e-9) - w) > 1e-9))
                throw new InvalidOperationException($"矩形段 [{a},{b}] 的半宽不是 w —— 参考积分的常数段假设不成立");
            rf0.Vol += Simpson(x => Col(x, false, out _), a, b, n);
            rf0.Area += Simpson(x => { Col(x, false, out double ar); return ar; }, a, b, n);
            int nD = b <= -R - 1e-9 ? 2 : Math.Max(8, (int)Math.Ceiling((b - a) / dx));
            rf0.VolInDisc += Simpson(x => Col(x, true, out _), a, b, nD);
            if (b <= xt + 1e-12) rf0.VolOnTab += Simpson(x => Col(x, false, out _), a, b, n);
        }
        double Seg(double rho, double c) { c = Math.Clamp(c, -rho, rho); return c * Math.Sqrt(rho * rho - c * c) + rho * rho * Math.Asin(c / rho) + rho * rho * Math.PI / 2; }
        double tTab = double.IsNaN(g.TabThicknessMm) ? g.ThicknessMm : g.TabThicknessMm;
        double tDisc = g.ThicknessMm;
        rf0.AreaClosed0 = Math.PI * (R * R - rh * rh) - Seg(R, xt) + 2 * w * (xt - tip);
        rf0.VolClosed0 = tTab * (2 * w * (xt - tip) - Seg(rh, xt))
                       + tDisc * (Math.PI * R * R - Seg(R, xt) - (Math.PI * rh * rh - Seg(rh, xt)));
        return rf0;
    }

    // ─────────────────────────────────────────────── 一张网格的量表
    internal sealed class Row
    {
        public string Grade = ""; public double HFine, ReqRadius; public int Cells, Nx, Nz;
        public double VRef, VMesh, VRefDisc, VMeshDisc, VRefTab, VMeshTab;
        /// <summary>环内体积按**格心**归属（求解器分区热账现在的口径；F4 未做，只印不判）</summary>
        public double VMeshDiscCentroid;
        /// <summary>逐格：跨环格的网格格体积对该格精确积分的最大相对误差（只印）</summary>
        public double CellVolErrMax;
        public int BandKept; public double AspectMax, MinFrac;
        public double RMeshOhm, PNet, PGen, Closure, JMax, Conserv; public bool Conv; public int Iter;
        public int HoleFaces; public double HoleFaceLen;
        public string Note = "";
    }

    internal static (LineCase lc, FlangePlate g, ShellMesh m, double reqRadius) Build(DesignSpec d, DesignInputs p, double fineMm, int plate = 0)
    {
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
        var g = lc.FlangePlates[Math.Min(plate, lc.FlangePlates.Length - 1)];
        g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        var m = LineRunner.PlateMeshAnalytic(lc, plate);
        return (lc, g, m, reqRadius);
    }

    /// <summary>该格精确体积（测试侧积分：x 向 Simpson 64 段、z 向按半径断点 Simpson），<paramref name="discOnly"/> = 只算 r ≤ R 那部分。</summary>
    static double CellVolExact(FlangePlate g, double x0, double x1, double z0, double z1, bool discOnly)
    {
        double R = g.DiscRadiusMm, rh = g.HoleRadiusMm;
        var rBreaks = new List<double> { rh, R };
        if (g.WeldFilletLegMm > 1e-9) rBreaks.Add(rh + g.WeldFilletLegMm);
        rBreaks.AddRange(g.DiscStepRadiiMm);
        double Col(double x)
        {
            if (x < g.TabTipXMm || x > R) return 0;
            double zTop = g.HalfWidth(x);
            double zh = Math.Abs(x) < rh ? Math.Sqrt(rh * rh - x * x) : 0;
            if (discOnly) zTop = Math.Min(zTop, Math.Abs(x) < R ? Math.Sqrt(R * R - x * x) : 0);
            if (zTop <= zh) return 0;
            double s = 0;
            double a = Math.Max(z0, zh), b = Math.Min(z1, zTop); if (b > a) s += ColInt(g, x, a, b, rBreaks, 0.05);
            a = Math.Max(z0, -zTop); b = Math.Min(z1, -zh); if (b > a) s += ColInt(g, x, a, b, rBreaks, 0.05);
            return s;
        }
        return Simpson(Col, x0, x1, 64);
    }

    internal static Row Measure(string grade, LineCase lc, FlangePlate g, ShellMesh m, Ref rf, double reqRadius, bool solve)
    {
        var row = new Row { Grade = grade, HFine = lc.MeshFineMm, ReqRadius = reqRadius, Cells = m.CellCount };
        double R = g.DiscRadiusMm; var (xt, w) = g.Tangent();
        // 舌片直边段的右端：有圆角时是圆角起点 xc（圆角区的格有料，不是带格）
        double xBand = xt;
        if (g.TabParallel && g.TabFilletMm > 1e-9 && w < R)
        { double rf2 = g.TabFilletMm, zc = w + rf2; xBand = -Math.Sqrt(Math.Max(0, (R + rf2) * (R + rf2) - zc * zc)); }
        var xs = m.Nodes.Select(v => v.X).Distinct().OrderBy(v => v).ToArray();
        var zs = m.Nodes.Select(v => v.Z).Distinct().OrderBy(v => v).ToArray();
        row.Nx = xs.Length; row.Nz = zs.Length;
        row.VMesh = m.VolumeMm3; row.VRef = rf.Vol; row.VRefDisc = rf.VolInDisc; row.VRefTab = rf.VolOnTab;
        double vd = 0, vdc = 0, vt = 0; int bandKept = 0; double aspMax = 0, minFrac = 1, cellErr = 0;
        for (int c = 0; c < m.CellCount; c++)
        {
            double vol = m.Area[c] * m.Thickness[c]; var ce = m.Centroid[c];
            var nd = m.Cells[c];
            double cx0 = Math.Min(m.Nodes[nd[0]].X, m.Nodes[nd[2]].X), cx1 = Math.Max(m.Nodes[nd[0]].X, m.Nodes[nd[2]].X);
            double cz0 = Math.Min(m.Nodes[nd[0]].Z, m.Nodes[nd[2]].Z), cz1 = Math.Max(m.Nodes[nd[0]].Z, m.Nodes[nd[2]].Z);
            if (ce.X * ce.X + ce.Z * ce.Z <= R * R) vdc += vol;                      // 按格心（求解器现在的分区口径）
            // 按精确份额：格矩形跨过 r = R 的圆才可能 0 < f < 1（最近点半径 < R < 最远角）
            double nx = Math.Clamp(0, cx0, cx1), nz = Math.Clamp(0, cz0, cz1), fx = Math.Max(Math.Abs(cx0), Math.Abs(cx1)), fz = Math.Max(Math.Abs(cz0), Math.Abs(cz1));
            if (fx * fx + fz * fz <= R * R) vd += vol;
            else if (nx * nx + nz * nz < R * R)
            {
                double vAll = CellVolExact(g, cx0, cx1, cz0, cz1, false), vIn = CellVolExact(g, cx0, cx1, cz0, cz1, true);
                if (vAll > 1e-12) { vd += vol * (vIn / vAll); cellErr = Math.Max(cellErr, Math.Abs(vol - vAll) / Math.Max(vAll, 1e-6)); }
            }
            if (ce.X < xt) vt += vol;
            if (cx1 <= xBand + 1e-9 && cz0 >= w - 1e-9) bandKept++;                     // 舌段直边（圆角起点左）落在 |z| ≥ w 的格 = 带格
            if (ce.X < xt - 5 && ce.X > g.TabTipXMm + 5) aspMax = Math.Max(aspMax, Math.Max((cx1 - cx0) / (cz1 - cz0), (cz1 - cz0) / (cx1 - cx0)));
            minFrac = Math.Min(minFrac, m.FracOf(c));
        }
        row.VMeshDisc = vd; row.VMeshDiscCentroid = vdc; row.CellVolErrMax = cellErr; row.VMeshTab = vt; row.BandKept = bandKept; row.AspectMax = aspMax; row.MinFrac = minFrac;
        var hf = m.Faces.Where(f => f.B < 0 && f.Tag == ShellMesh.TagHole).ToArray();
        row.HoleFaces = hf.Length; row.HoleFaceLen = hf.Sum(f => f.Length);
        if (solve)
        {
            double rhoRefOhmMm = Materials.PtResistivity(PlateTempC) * 1e3;
            try
            {
                var sc = ShellCurrent.Solve(m, PlateCurrentA, rhoRefOhmMm, PlateTempC);
                row.Conv = sc.Converged; row.Iter = sc.Iterations; row.JMax = sc.JMaxAPerMm2; row.Conserv = sc.ConservationError;
                row.RMeshOhm = sc.CurrentInA > 1e-30 ? rhoRefOhmMm / sc.CurrentInA : double.NaN;
                row.PNet = PlateCurrentA * PlateCurrentA * row.RMeshOhm; row.PGen = sc.TotalGenW;
                row.Closure = row.PNet > 0 ? row.PGen / row.PNet : double.NaN;
            }
            catch (Exception ex) { row.Note = $"电流场抛异常 {ex.GetType().Name}：{ex.Message}"; row.Conv = false; }
        }
        return row;
    }

    internal static string Pct(double a, double b) => b != 0 ? ((a - b) / b * 100).ToString("+0.000;-0.000") : "—";
    internal static string F(double v, string fmt = "0.000") => double.IsNaN(v) ? "—" : v.ToString(fmt);
    internal static string Head =>
        "档\t细区mm\t细区半径\t节点\t单元\tV_ref\tV_mesh\tΔV%\t环内V_ref\t环内V_mesh(份额)\tΔ环内%\t环内V_mesh(格心)\tΔ环内格心%\t跨环格体积最大误差%\t舌区V_ref\t舌区V_mesh\tΔ舌区%\t带格\t舌中格长宽比\t最小覆盖率\t孔面数\t孔面总长\tR_mesh µΩ\tP_net W\tP_gen W\t闭合\tJmax\t守恒误差\tCG轮\t收敛\t备注";
    internal static string Line(Row r) =>
        $"{r.Grade}\t{r.HFine:0.000}\t{r.ReqRadius:0.00}\t{r.Nx}×{r.Nz}\t{r.Cells}\t{r.VRef:0.00}\t{r.VMesh:0.00}\t{Pct(r.VMesh, r.VRef)}"
      + $"\t{r.VRefDisc:0.00}\t{r.VMeshDisc:0.00}\t{Pct(r.VMeshDisc, r.VRefDisc)}\t{r.VMeshDiscCentroid:0.00}\t{Pct(r.VMeshDiscCentroid, r.VRefDisc)}\t{r.CellVolErrMax * 100:0.000}\t{r.VRefTab:0.00}\t{r.VMeshTab:0.00}\t{Pct(r.VMeshTab, r.VRefTab)}"
      + $"\t{r.BandKept}\t{r.AspectMax:0.0}\t{r.MinFrac:0.0000}\t{r.HoleFaces}\t{r.HoleFaceLen:0.000}"
      + $"\t{F(r.RMeshOhm * 1e6, "0.0000")}\t{F(r.PNet)}\t{F(r.PGen)}\t{F(r.Closure, "0.0000")}\t{F(r.JMax, "0.00")}\t{F(r.Conserv, "0.0e0")}\t{r.Iter}\t{(r.Conv ? "是" : "**否**")}\t{r.Note}";

    internal static DesignSpec Design(string which) => which == "W08" ? R48LW08NavDesign.Build() : R48LW06FineDesign.Build();

    /// <summary>盘径／舌半宽旋钮：舌长按搜形状规则 = √(R²−w²) + 压接 + 自由段下界（与网格审计各支同一规则）。</summary>
    internal static void SetRW(DesignSpec d, double R, double w)
    {
        d.DiscRadiusMm = R; d.TabHalfWidthMm = w;
        d.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - w * w)) + d.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
    }

    static IEnumerable<double> Range(double a, double b, double step) { int n = (int)Math.Round((b - a) / step); for (int i = 0; i <= n; i++) yield return Math.Round(a + i * step, 6); }

    static string Stamp => DeliverableOut.RunStamp;

    // ═══════════════════════════════════════════════ 参考积分自检（闭式）
    [Fact]
    public void 门0_参考积分对闭式自检()
    {
        var p = new DesignInputs();
        var d = Design("W08");
        var (_, g, _, _) = Build(d.Clone(), p, 0);
        var g1 = g; g1.WeldFilletLegMm = 0; g1.DiscStepRadiiMm = Array.Empty<double>(); g1.DiscStepThicknessMm = Array.Empty<double>();
        var r1 = Reference(g1);
        _o.WriteLine($"w=R 无焊脚：面积 积分 {r1.Area:0.0000} vs 闭式 {r1.AreaClosed0:0.0000}；体积 积分 {r1.Vol:0.0000} vs 闭式 {r1.VolClosed0:0.0000}");
        Assert.True(Math.Abs(r1.Area - r1.AreaClosed0) < 1e-3 * r1.Area, "参考积分面积对不上闭式");
        Assert.True(Math.Abs(r1.Vol - r1.VolClosed0) < 1e-3 * r1.Vol, "参考积分体积对不上闭式");
        double halfRing = Math.PI * (g1.DiscRadiusMm * g1.DiscRadiusMm - g1.HoleRadiusMm * g1.HoleRadiusMm) / 2;
        double inDiscClosed = g1.TabThicknessMm * halfRing + g1.ThicknessMm * halfRing;
        Assert.True(Math.Abs(r1.VolInDisc - inDiscClosed) < 2e-4 * inDiscClosed, $"环内体积 {r1.VolInDisc} vs 闭式 {inDiscClosed}");
    }

    // ═══════════════════════════════════════════════ (a) 体积守恒门
    static (bool ok, string line) VolGate(string tag, Row r)
    {
        var bad = new List<string>();
        if (Math.Abs(r.VMesh - r.VRef) / r.VRef * 100 > VolTolPct) bad.Add($"总体积 {Pct(r.VMesh, r.VRef)} %");
        if (Math.Abs(r.VMeshDisc - r.VRefDisc) / r.VRefDisc * 100 > VolTolPct) bad.Add($"环内 {Pct(r.VMeshDisc, r.VRefDisc)} %");
        if (Math.Abs(r.VMeshTab - r.VRefTab) / r.VRefTab * 100 > VolTolPct) bad.Add($"舌区 {Pct(r.VMeshTab, r.VRefTab)} %");
        return (bad.Count == 0, $"{tag}\t{Line(r)}\t{(bad.Count == 0 ? "过" : "**不过：" + string.Join("；", bad) + "**")}");
    }

    /// <summary>(a) 全量（慢）：W08／W06 × 盘径 28～34 每 0.25 × 舌半宽 30／28／26 × 四片 × 导航／判决。</summary>
    [Trait("速度", "慢")]
    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 门a_体积守恒_全量(string which) => RunVolGate(which, Range(28.0, 34.0, 0.25), new[] { 30.0, 28.0, 26.0 }, new[] { 0, 1, 2, 3 }, "全量");

    /// <summary>(a) 快门：W08 × 九个盘径 × 舌半宽 30／28 × 片 0／1 × 导航／判决（含 w 贴格点与 31.00／31.01 那一跳）。</summary>
    [Fact]
    public void 门a_体积守恒_快门_W08() => RunVolGate("W08", new[] { 28.0, 29.0, 30.0, 30.25, 30.75, 31.0, 31.01, 32.0, 34.0 }, new[] { 30.0, 28.0 }, new[] { 0, 1 }, "快门");

    void RunVolGate(string which, IEnumerable<double> radii, double[] halfWidths, int[] plates, string tag)
    {
        var p = new DesignInputs();
        var d0 = Design(which);
        string file = DeliverableOut.Stamped($"网格修复_门a_体积守恒_{tag}_{which}.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"网格修复 门(a) 体积守恒　{which}（{d0.Name}）　{tag}");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：每片总体积、环内（r ≤ R）、舌区（x < 切点）对参考积分 |ΔV|/V ≤ {VolTolPct} %；参考积分 = 板件 Inside/HalfWidth/ThicknessAt 的 Simpson（x 0.01／z 0.05 mm）。");
        W("环内的归属：跨 r = R 的格按该格材料落在圆内的精确份额（测试侧积分）—— 量的是网格每格的体积对不对；按**格心**归属的环内体积另印一列（求解器分区热账现在的口径，F4 未做，只印不判）。");
        W("舌半宽 > 盘半径的几何（板件 HalfWidthClamped：舌片比圆盘还宽，几何上不成立）不进门，单列「几何不成立」，既不算过也不算不过。");
        W("几何\tR\tw\t片\t" + Head + "\t判读");
        int nBad = 0, nAll = 0, nSkip = 0; var badList = new List<string>();
        foreach (double R in radii)
            foreach (double w in halfWidths)
                foreach (int plate in plates)
                    foreach (var (grade, fine) in new[] { ("导航", 0.0), ("判决", 1.0) })
                    {
                        var d = d0.Clone(); SetRW(d, R, w);
                        string geo = $"{which}\t{R:0.00}\t{w:0.00}\t{plate}";
                        if (w > R + 1e-9) { W($"{geo}\t{grade}\t几何不成立（舌半宽 {w} > 盘半径 {R}，不进门）"); nSkip++; continue; }
                        try
                        {
                            var (lc, g, m, req) = Build(d, p, fine, plate);
                            var rf = Reference(g);
                            var row = Measure(grade, lc, g, m, rf, req, solve: false);
                            var (ok, line) = VolGate(geo, row);
                            W(line); nAll++;
                            if (!ok) { nBad++; badList.Add($"{which} R{R:0.00} w{w:0.00} 片{plate} {grade}"); }
                        }
                        catch (Exception ex) { W($"{geo}\t{grade}\t**判不了：{ex.GetType().Name} {ex.Message}**"); nBad++; nAll++; badList.Add($"{which} R{R:0.00} w{w:0.00} 片{plate} {grade} 判不了"); }
                    }
        W();
        W($"合计 {nAll} 张网格，不过 {nBad} 张{(nBad > 0 ? "：" + string.Join("、", badList.Take(40)) : "")}；几何不成立跳过 {nSkip} 张");
        W($"── 总耗时 {sw.Elapsed.TotalSeconds:0} s　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(nBad == 0, $"体积守恒门不过 {nBad}/{nAll}：{string.Join("、", badList.Take(20))}（{file}）");
    }

    // ═══════════════════════════════════════════════ (b) 连续性门 + 带格恒 0
    [Fact]
    public void 门b_连续性_盘径30到31p5每0p01_导航()
        => RunContinuity("盘径", "盘径 30.00～31.50 每 0.01（舌半宽 30，舌长按搜形状规则）", Range(30.0, 31.5, 0.01), (d, v) => SetRW(d, v, 30.0));

    /// <summary>2026-09-19 Fable 5.1（第二轮复核第 5 条）：同类病扫描不只盘径 —— 舌半宽 28～30 每 0.05（盘 30）与孔径（管内径）49～51 每 0.05（盘 30／舌半宽 30），同一把尺。</summary>
    [Fact]
    public void 门b_连续性_舌半宽28到30每0p05_导航()
        => RunContinuity("舌半宽", "舌半宽 28.00～30.00 每 0.05（盘径 30，舌长按搜形状规则）", Range(28.0, 30.0, 0.05), (d, v) => SetRW(d, 30.0, v));

    [Fact]
    public void 门b_连续性_孔径49到51每0p05_导航()
        => RunContinuity("孔径", "管内径 49.00～51.00 每 0.05（盘径 30、舌半宽 30）", Range(49.0, 51.0, 0.05), (d, v) => { SetRW(d, 30.0, 30.0); d.TubeIdMm = v; });

    void RunContinuity(string knob, string span, IEnumerable<double> values, Action<DesignSpec, double> set)
    {
        var p = new DesignInputs();
        var d0 = Design("W08");
        string file = DeliverableOut.Stamped($"网格修复_门b_连续性_{knob}_W08.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"网格修复 门(b) 连续性　W08 片0　{span}　导航 vs 判决网格，等温电流场");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}{(knob == "盘径" ? "" : "；本根扫描 2026-09-19 Fable 5.1 加（复核第 5 条），门槛与盘径那根同一份")}");
        W($"门槛（跑前写死）：相邻两点 |ΔV_mesh − ΔV_ref| ≤ 一格体积（= 最大格面积 × 板厚）；网格误差 e = (R_导航 − R_判决)/R_判决 的相邻变化 |Δe| ≤ {RStepTolPct} %，且 |e| ≤ 1 %（网格审计 ② 的坏带线）；舌段带格数恒 0；电流场须收敛。");
        W("⚠ 第一版曾直接判相邻 R_mesh 变化 ≤ 0.2 %：舌长 = √(R²−w²) + 压接 + 自由段在 R = w 处有 √ 奇点（R 30.00 → 30.01 舌长 +0.77 mm、电阻真变 +0.5 %），那是几何不是网格 ⇒ 改判网格误差的连续性，门槛数不变（2026-09-18 Fable 5.1）。");
        W($"{knob}\t" + Head + "\t判决R_mesh µΩ\t判决单元\te%\tΔe%\tΔV_mesh−ΔV_ref\t一格体积\t判读");
        Row? prev = null; double prevE = double.NaN; int nBad = 0; var bad = new List<string>();
        foreach (double R in values)
        {
            var d = d0.Clone(); set(d, R);
            var (lc, g, m, req) = Build(d, p, 0);
            var rf = Reference(g);
            var row = Measure("导航", lc, g, m, rf, req, solve: true);
            var dj = d0.Clone(); set(dj, R);
            var (lcj, gj, mj, reqj) = Build(dj, p, 1.0);
            var rowJ = Measure("判决", lcj, gj, mj, rf, reqj, solve: true);
            var notes = new List<string>();
            double dv = double.NaN, cellVol = double.NaN;
            double e = (row.RMeshOhm - rowJ.RMeshOhm) / rowJ.RMeshOhm * 100, de = double.NaN;
            if (prev != null)
            {
                dv = (row.VMesh - prev.VMesh) - (row.VRef - prev.VRef);
                cellVol = m.Area.Max() * g.ThicknessMm;
                if (Math.Abs(dv) > cellVol) notes.Add("体积台阶");
                de = e - prevE;
                if (Math.Abs(de) > RStepTolPct) notes.Add($"网格误差台阶 Δe {de:+0.000;-0.000} %");
            }
            if (Math.Abs(e) > 1.0) notes.Add($"坏带 e {e:+0.000;-0.000} %");
            if (row.BandKept != 0 || rowJ.BandKept != 0) notes.Add($"带格 {row.BandKept}/{rowJ.BandKept}");
            if (!row.Conv || !rowJ.Conv) notes.Add("电流场未收敛 ⇒ 判不了");
            W($"{R:0.00}\t{Line(row)}\t{F(rowJ.RMeshOhm * 1e6, "0.0000")}\t{rowJ.Cells}\t{e:+0.000;-0.000}\t{F(de)}\t{F(dv)}\t{F(cellVol)}\t{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { nBad++; bad.Add($"{knob}{R:0.00}: {string.Join("；", notes)}"); }
            prev = row; prevE = e;
        }
        W($"── 不过 {nBad} 点{(nBad > 0 ? "：" + string.Join("　", bad) : "")}　总耗时 {sw.Elapsed.TotalSeconds:0} s　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(nBad == 0, $"连续性门（{knob}）不过 {nBad} 点：{string.Join("　", bad.Take(10))}（{file}）");
    }

    /// <summary>带格恒 0 + 电功率闭合：盘径五档（30.50／30.75／31.00／31.01／31.25）与舌半宽四档（29.50／29.55／29.70／29.80）× 导航／判决。</summary>
    [Fact]
    public void 门_带格恒0_电功率闭合_导航判决()
    {
        var p = new DesignInputs();
        var d0 = Design("W08");
        string file = DeliverableOut.Stamped("网格修复_门_带格与闭合_W08.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        W("网格修复 门 带格恒 0 + 电功率闭合　W08 片0　等温 1214 A／1150 °C");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：舌段带格数 = 0；P_gen/P_net ∈ [{ClosureLo}, {ClosureHi}]；体积 ≤ {VolTolPct} %。");
        W("R\tw\t" + Head + "\t判读");
        int nBad = 0; var bad = new List<string>();
        var cases = new List<(double R, double w)>();
        foreach (double R in new[] { 30.50, 30.75, 31.00, 31.01, 31.25 }) cases.Add((R, 30.0));
        foreach (double w in new[] { 29.50, 29.55, 29.70, 29.80 }) cases.Add((30.0, w));
        foreach (var (R, w) in cases)
            foreach (var (grade, fine) in new[] { ("导航", 0.0), ("判决", 1.0) })
            {
                var d = d0.Clone(); SetRW(d, R, w);
                var (lc, g, m, req) = Build(d, p, fine);
                var rf = Reference(g);
                var row = Measure(grade, lc, g, m, rf, req, solve: true);
                var notes = new List<string>();
                if (row.BandKept != 0) notes.Add($"带格 {row.BandKept}");
                if (!(row.Closure >= ClosureLo && row.Closure <= ClosureHi)) notes.Add($"闭合 {row.Closure:0.0000}");
                if (Math.Abs(row.VMesh - row.VRef) / row.VRef * 100 > VolTolPct) notes.Add($"体积 {Pct(row.VMesh, row.VRef)} %");
                if (!row.Conv) notes.Add("未收敛");
                W($"{R:0.00}\t{w:0.00}\t{Line(row)}\t{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
                if (notes.Count > 0) { nBad++; bad.Add($"R{R:0.00} w{w:0.00} {grade}: {string.Join("；", notes)}"); }
            }
        W($"── 不过 {nBad}　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(nBad == 0, $"带格／闭合门不过 {nBad}：{string.Join("　", bad)}（{file}）");
    }

    // ═══════════════════════════════════════════════ 整线解（(c)(d)(f) 共用）
    internal sealed class LineRow
    {
        public string Tag = ""; public double R, W; public string Grade = ""; public double FineMm, RadiusMm;
        public bool Ok, Converged; public string Message = "";
        public double Hot = double.NaN, Cold = double.NaN, Flux = double.NaN, Mass = double.NaN, QGen0 = double.NaN, QTube0 = double.NaN;
        public int Cells, Rounds; public double Sec; public string Where = "";
        public string Text => Ok && Converged
            ? $"{Tag}\t{R:0.00}\t{W:0.00}\t{Grade}\t{FineMm:0.000}\t{RadiusMm:0.0}\t{Cells}\t{Hot:0.000}\t{Cold:0.000}\t{Flux:0.000}\t{Mass:0.0}\t{QGen0:0.000}\t{QTube0:0.000}\t{Rounds}\t{Sec:0}"
            : $"{Tag}\t{R:0.00}\t{W:0.00}\t{Grade}\t{FineMm:0.000}\t{RadiusMm:0.0}\t{Cells}\t**判不了：{Message}**\t\t\t\t\t\t{Rounds}\t{Sec:0}";
    }
    internal const string LineHead = "标签\tR\tw\t档\t细区mm\t细区半径\t单元\t最热铂高出热偶读数K\t管根低于热偶读数K\t管孔净流入W\t铂重g\t片0发热W\t片0抽热W\t耦合轮\t耗时s";

    internal static LineRow SolveLine(string tag, DesignSpec d, DesignInputs p, double fineMm, double R, double w, string grade, IProgress<string>? probe = null)
    {
        var row = new LineRow { Tag = tag, R = R, W = w, Grade = grade };
        var sw = Stopwatch.StartNew();
        try
        {
            var dummy = new SolverResult { Design = d };
            Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
            var (_, reqRadius) = MeshVerify.RequiredMeshFor(d);
            var lc = d.BuildCase(p);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = fineMm, FineRadiusMm = reqRadius });
            row.FineMm = lc.MeshFineMm; row.RadiusMm = lc.MeshFineRadiusMm;
            var r = LineRunner.Run(lc, probe);
            row.Ok = r.Ok; row.Converged = r.Converged; row.Message = r.Ok ? (r.Converged ? "" : "外层耦合未收敛") : r.Message;
            row.Cells = r.MeshCells; row.Rounds = r.CoupleRounds;
            if (r.Ok)
            {
                row.Hot = r.ValueOf(LineResult.Key.HotOverTc); row.Cold = r.ValueOf(LineResult.Key.ColdUnderTc); row.Flux = r.ValueOf(LineResult.Key.NetFlux);
                row.Mass = r.TotalMassG;
                if (r.Flanges.Length > 0) { row.QGen0 = r.Flanges[0].QGenW; row.QTube0 = r.Flanges[0].QFromTubeW; }
                row.Where = r.Find(LineResult.Key.ColdUnderTc)?.Where ?? "";
            }
        }
        catch (Exception ex) { row.Ok = false; row.Message = $"{ex.GetType().Name}：{ex.Message}"; }
        row.Sec = sw.Elapsed.TotalSeconds;
        return row;
    }

    /// <summary>并发整线解：同一设计逐点（每点一份 LineCase），并发度 6；每点解完立刻落盘（探针）。</summary>
    static List<LineRow> SolveMany(IReadOnlyList<(string tag, DesignSpec d, double fineMm, double R, double w, string grade)> jobs, Action<LineRow> onDone, int par = 6)
    {
        var p = new DesignInputs();
        var bag = new ConcurrentBag<LineRow>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = par }, j =>
        {
            var row = SolveLine(j.tag, j.d, p, j.fineMm, j.R, j.w, j.grade);
            bag.Add(row);
            lock (bag) onDone(row);
        });
        return bag.OrderBy(r => r.Tag, StringComparer.Ordinal).ThenBy(r => r.R).ThenBy(r => r.W).ThenBy(r => r.Grade, StringComparer.Ordinal).ToList();
    }

    static string Diff(LineRow a, LineRow b, out bool ok, double tolK, double tolW)
    {
        ok = false;
        if (!(a.Ok && a.Converged && b.Ok && b.Converged)) return "判不了（有一档没解出）";
        double dh = a.Hot - b.Hot, dc = a.Cold - b.Cold, df = a.Flux - b.Flux;
        ok = Math.Abs(dh) <= tolK && Math.Abs(dc) <= tolK && Math.Abs(df) <= tolW;
        return $"{dh:+0.000;-0.000}\t{dc:+0.000;-0.000}\t{df:+0.000;-0.000}\t{(ok ? "过" : "**不过**")}";
    }

    // ═══════════════════════════════════════════════ (c) 一致门（慢）
    [Trait("速度", "慢")]
    [Fact]
    public void 门c_一致门_导航vs判决_盘径28到34每0p25_W08()
    {
        var d0 = Design("W08");
        string stamp = Stamp;
        string file = DeliverableOut.Stamped("网格修复_门c_一致门_导航vs判决_盘径_W08.txt");
        string live = Path.Combine(Path.GetTempPath(), $"网格修复_门c_{stamp}_进行中.log");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("网格修复 门(c) 一致门　W08　盘径 28～34 每 0.25（舌半宽 30，舌长按搜形状规则；其余旋钮 = A 导航档终值）　导航 vs 判决整线解（带玻璃稳态 ②）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　并发 6 条整线解，每点解完即落盘（{live}）");
        W($"门槛（跑前写死）：同一几何 导航 − 判决：|Δ最热铂| ≤ {ConsistTolK} K、|Δ管根| ≤ {ConsistTolK} K、|Δ净流入| ≤ {ConsistTolW} W；判不了不当过。改前在 30.75／31.00 是 19～26 K（诊断 §0.-12 ②、网格审计_3）。");
        W($"基准设计：{R48LW08NavDesign.Source}");
        W();
        W(LineHead);
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        var jobs = new List<(string, DesignSpec, double, double, double, string)>();
        foreach (double R in Range(28.0, 34.0, 0.25))
        {
            var dn = d0.Clone(); SetRW(dn, R, 30.0); jobs.Add(("c", dn, 0.0, R, 30.0, "导航"));
            var dj = d0.Clone(); SetRW(dj, R, 30.0); jobs.Add(("c", dj, 1.0, R, 30.0, "判决"));
        }
        // 并发自检：第一点先串行解一次，并发批里同一点再解一次，两者必须逐位相同
        var serial = SolveLine("串行自检", d0.Clone().Also(d => SetRW(d, 30.0, 30.0)), new DesignInputs(), 0.0, 30.0, 30.0, "导航");
        var rows = SolveMany(jobs, r =>
        {
            File.AppendAllText(live, r.Text + Environment.NewLine, new UTF8Encoding(false));
            File.AppendAllText(file, r.Text + Environment.NewLine, new UTF8Encoding(false));
        });
        var par30 = rows.First(r => Math.Abs(r.R - 30.0) < 1e-9 && r.Grade == "导航");
        bool sameBits = serial.Ok == par30.Ok && serial.Hot == par30.Hot && serial.Cold == par30.Cold && serial.Flux == par30.Flux && serial.Mass == par30.Mass;
        var tail = new StringBuilder(); void T(string t = "") => tail.AppendLine(t);
        T(); T($"并发自检（R30 导航 串行 vs 并发）：{(sameBits ? "逐位相同 ⇒ 并发没有串味" : "**不同** ⇒ 并发有串味，本表作废")}　串行 {serial.Text}");
        T(); T("═══════ 对拍：导航 − 判决 ═══════");
        T("R\t导航最热铂\t判决最热铂\t导航管根\t判决管根\t导航净流入\t判决净流入\t导航铂重\t判决铂重\tΔ最热铂\tΔ管根\tΔ净流入\t判读");
        int nBad = 0; var bad = new List<string>();
        foreach (double R in Range(28.0, 34.0, 0.25))
        {
            var a = rows.First(r => Math.Abs(r.R - R) < 1e-9 && r.Grade == "导航");
            var b = rows.First(r => Math.Abs(r.R - R) < 1e-9 && r.Grade == "判决");
            string dd = Diff(a, b, out bool ok, ConsistTolK, ConsistTolW);
            T($"{R:0.00}\t{F(a.Hot)}\t{F(b.Hot)}\t{F(a.Cold)}\t{F(b.Cold)}\t{F(a.Flux)}\t{F(b.Flux)}\t{F(a.Mass, "0.0")}\t{F(b.Mass, "0.0")}\t{dd}");
            if (!ok) { nBad++; bad.Add($"R{R:0.00}"); }
        }
        T($"── 不过 {nBad}/25 点{(nBad > 0 ? "：" + string.Join(" ", bad) : "")}　总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　{Sign}");
        T("出处：整线解 = LineRunner.Run；网格 = Solver.ApplyCaseMesh（导航 FineMm = 0 只统一细区半径；判决 FineMm = 1.0）；读数 = LineResult.ValueOf。每个数出自本次运行。");
        File.AppendAllText(file, tail.ToString(), new UTF8Encoding(false));
        _o.WriteLine(file);
        Assert.True(sameBits, "并发串味，本表作废");
        Assert.True(nBad == 0, $"一致门不过 {nBad}/25：{string.Join(" ", bad)}（{file}）");
    }

    // ═══════════════════════════════════════════════ (d) 收敛门（慢）
    [Trait("速度", "慢")]
    [Fact]
    public void 门d_收敛门_判决1p0vs细0p5_盘30_31_32_W08()
    {
        var d0 = Design("W08");
        string stamp = Stamp;
        string file = DeliverableOut.Stamped("网格修复_门d_收敛门_判决vs细_W08.txt");
        string live = Path.Combine(Path.GetTempPath(), $"网格修复_门d_{stamp}_进行中.log");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("网格修复 门(d) 收敛门　W08　盘 30／31／32（舌半宽 30）　判决 1.0 mm vs 细 0.5 mm 整线解（带玻璃稳态 ②）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　并发 6");
        W($"门槛（跑前写死）：|Δ最热铂| ≤ {ConvTolK} K、|Δ管根| ≤ {ConvTolK} K、|Δ净流入| ≤ {ConvTolW} W；判不了不当过。");
        W(); W(LineHead);
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        var jobs = new List<(string, DesignSpec, double, double, double, string)>();
        foreach (double R in new[] { 30.0, 31.0, 32.0 })
        {
            var dj = d0.Clone(); SetRW(dj, R, 30.0); jobs.Add(("d", dj, 1.0, R, 30.0, "判决"));
            var df = d0.Clone(); SetRW(df, R, 30.0); jobs.Add(("d", df, 0.5, R, 30.0, "细"));
        }
        var rows = SolveMany(jobs, r =>
        {
            File.AppendAllText(live, r.Text + Environment.NewLine, new UTF8Encoding(false));
            File.AppendAllText(file, r.Text + Environment.NewLine, new UTF8Encoding(false));
        });
        var tail = new StringBuilder();
        tail.AppendLine(); tail.AppendLine("═══════ 对拍：判决 − 细 ═══════");
        tail.AppendLine("R\t判决最热铂\t细最热铂\t判决管根\t细管根\t判决净流入\t细净流入\tΔ最热铂\tΔ管根\tΔ净流入\t判读");
        int nBad = 0; var bad = new List<string>();
        foreach (double R in new[] { 30.0, 31.0, 32.0 })
        {
            var a = rows.First(r => Math.Abs(r.R - R) < 1e-9 && r.Grade == "判决");
            var b = rows.First(r => Math.Abs(r.R - R) < 1e-9 && r.Grade == "细");
            string dd = Diff(a, b, out bool ok, ConvTolK, ConvTolW);
            tail.AppendLine($"{R:0.00}\t{F(a.Hot)}\t{F(b.Hot)}\t{F(a.Cold)}\t{F(b.Cold)}\t{F(a.Flux)}\t{F(b.Flux)}\t{dd}");
            if (!ok) { nBad++; bad.Add($"R{R:0.00}"); }
        }
        tail.AppendLine($"── 不过 {nBad}/3{(nBad > 0 ? "：" + string.Join(" ", bad) : "")}　总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　{Sign}");
        File.AppendAllText(file, tail.ToString(), new UTF8Encoding(false));
        _o.WriteLine(file);
        Assert.True(nBad == 0, $"收敛门不过 {nBad}/3：{string.Join(" ", bad)}（{file}）");
    }

    // ═══════════════════════════════════════════════ (f) 现役数（慢）：只印，不判；对拍在改后树的 R48NMeshInjectTests
    [Trait("速度", "慢")]
    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 门f_现役数_圆盘保温10与20_判决与导航(string which)
    {
        var d0 = Design(which);
        string stamp = Stamp;
        string file = DeliverableOut.Stamped($"网格修复_门f_现役数_{which}.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W($"网格修复 门(f) 现役数　{which}（{d0.Name}）　圆盘保温 10／20 mm × 判决（MeshVerify.RequiredMeshFor）／导航　带玻璃稳态 ② 三条判据 + 铂重");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　并发 4");
        W("用途：改前／改后逐位对拍（对拍门在 R48NMeshInjectTests，允许位移 ≤ 0.5 K 写死；超了查原因不挪门槛）。本测试只印不判。");
        W($"基准设计：{(which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source)}");
        W(); W(LineHead);
        var (reqFine, _) = MeshVerify.RequiredMeshFor(d0);
        var jobs = new List<(string, DesignSpec, double, double, double, string)>();
        foreach (double disc in new[] { 10.0, 20.0 })
            foreach (var (grade, fine) in new[] { ("判决", reqFine), ("导航", 0.0) })
            {
                var d = d0.Clone();
                d.FlangeInsulated = true; d.FlangeInsulMm = disc;
                d.DiscInsulMm = Array.Empty<double>();   // 逐片留空 = 全线沿用这个值（与 R48LDiscInsul10GateRunTests.Clone 同一写法）
                jobs.Add(($"盘{disc:0}", d, fine, d.DiscRadiusMm, d.TabHalfWidthMm, grade));
            }
        var rows = SolveMany(jobs, _ => { }, par: 4);
        foreach (var r in rows) W(r.Text);
        W($"── 总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟　{Sign}");
        W("出处：整线解 = LineRunner.Run；网格 = Solver.ApplyCaseMesh；圆盘保温 = DesignSpec.FlangeInsulMm 与逐片 DiscInsulMm 同设；读数 = LineResult.ValueOf。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file);
        Assert.True(rows.All(r => r.Ok), "有一档没解出：" + string.Join("；", rows.Where(r => !r.Ok).Select(r => r.Text)));
    }
}

internal static class R48NMeshGateExt
{
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}
