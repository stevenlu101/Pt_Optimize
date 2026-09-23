using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 一个矩形（网格单元）里材料的积分：有料面积、体积、面积一次矩（材料形心 = 矩 ÷ 面积）。
/// 2026-09-18，Fable 5.1（网格生成根因修复）。
/// </summary>
public readonly record struct MaterialIntegral(double Area, double Volume, double MomentX, double MomentZ)
{
    public static readonly MaterialIntegral Empty = new(0, 0, 0, 0);
    public MaterialIntegral Add(MaterialIntegral o) => new(Area + o.Area, Volume + o.Volume, MomentX + o.MomentX, MomentZ + o.MomentZ);
    public MaterialIntegral Scale(double k) => new(Area * k, Volume * k, MomentX * k, MomentZ * k);
}

/// <summary>
/// ★★ 2026-09-18，Fable 5.1：**网格生成器认识的「材料场」** —— 生成器（<see cref="FlangeMesher.BuildFromMaterial"/>）只通过这四件事量材料：
///   · 精确包络（轴的端点）；
///   · 单元矩形上的**面积／体积／一次矩积分**（覆盖面积、厚度 = 体积÷面积、材料形心）；
///   · 一条轴对齐线段上的**有料长度与有料中点**（面的导电／导热截面 —— 「面积折了面长没折」的病就是没有这一件）；
///   · 矩形内有料面积里落在某个圆内的份额（保温分界圆穿过的格子怎么混）。
/// 两个实现：解析板（<see cref="AnalyticMaterial"/>，逐列精确积分，没有采样点、没有平局、没有栅格幻影）与
/// 栅格厚度场（<see cref="ThicknessField"/>，图纸路径，量法同 R47：每个栅格节点代表以它为心的方格）。
/// </summary>
public interface IMaterialField
{
    (double XMin, double XMax, double ZMin, double ZMax, bool Exact) MaterialEnvelope();
    MaterialIntegral Integrate(double x0, double x1, double z0, double z1);
    /// <summary>轴对齐线段上的有料长度与有料中点坐标：<paramref name="vertical"/> = true ⇒ 线在 x = <paramref name="line"/>，参数 a..b 是 z；否则线在 z = line，a..b 是 x。</summary>
    (double Length, double Mid) SegmentMaterial(bool vertical, double line, double a, double b);
    /// <summary>矩形内有料面积里落在 r ≤ <paramref name="radiusMm"/> 的份额；没有料 ⇒ NaN。</summary>
    double FractionInsideCircle(double x0, double x1, double z0, double z1, double radiusMm);
    /// <summary>给证据文件用的一句话。</summary>
    string Describe();
}

/// <summary>
/// ★★★ 2026-09-18，Fable 5.1：**解析板的精确材料场** —— 网格生成的病根就在这里被修掉。
///
/// ══ 病（诊断 deliverable 网格审计_1／2／3，2026-09-18）
///   老路 = 栅格化（格点上点采样 <c>Inside ? ThicknessAt : 0</c>）+ 方格面积积分。舌片直边 z = ±w 恰落在格点上时
///   （w = 30 与 0.5／0.25／0.125 三档栅格步都对齐），<c>Inside</c> 的闭区间把边上那排格点判成有料 ⇒ 两侧各半个栅格步的幻影料；
///   R47 M1 把 ±w 锚成节点后，幻影集中成一排「带格」，覆盖率 = (步/2)/(R−w) 恰好卡在 25 % 丢弃线上 ⇒ 盘径／舌半宽扫描上带格随几何翻面，
///   三条判据跳 19～26 K；再加上面长从来没折，带格成了一条与舌片并联的幻影导体。
///
/// ══ 修法（本类）
///   不采样。每个单元矩形 [x0,x1]×[z0,z1] 上：
///   · **z 向精确**：一列 x 上的材料就是 |z| ∈ [孔弦 zh(x), 半宽 H(x)] 减去开孔／开槽（<see cref="FlangePlate.HalfWidth"/> 与孔半径直接给出区间端点；
///     开孔／开槽的区间由该形状自己的 Contains 采样 + 二分定到 1e-12）；厚度沿 z 分段常数（台阶半径、加厚半径）+ 焊脚环（光滑，Gauss–Legendre 16 点）；
///   · **x 向 Gauss–Legendre 16 点复合求积**，在所有已知断点处分段（舌尖、舌端、切点、圆角两端、各半径圆与格线 z0/z1 的交点、半宽曲线与 z0/z1 的交点、开孔包围盒边）；
///     分段之间被积函数解析光滑（圆弧端点的 √ 奇点只在盘缘几个小片上，16 点 GL 误差 ~1e-4 × 小片面积）。
///   几何真值只认板件三个公开件 <see cref="FlangePlate.HalfWidth"/>／<see cref="FlangePlate.ThicknessAt"/>／各形状的 Contains —— 这里不抄一遍轮廓式子；
///   断点表只影响精度不影响口径（漏一个断点 = 那一格上 GL 跨过一个折点，误差 O(格宽²)，不会把料判到板外）。
///   厚度分段常数的假设有自检：每段中点与 1/4、3/4 点的 ThicknessAt 不等 ⇒ 该段退回 GL 求积（ThicknessAt 将来加了新分区也不会静默错）。
///
/// ══ 精度门（R48NMeshInjectTests.积分器自检）：对 W08 板全片与逐格的 Simpson 参考（x 0.005／z 0.05 mm）相对差 ≤ 1e-6；
///   带开孔／开槽的板对 64×64 子采样参考 ≤ 1e-4。
/// </summary>
public sealed class AnalyticMaterial : IMaterialField
{
    public readonly FlangePlate Plate;
    private readonly double _tip, _xMax, _rh, _R, _leg, _zHalf;
    private readonly double[] _pieces;     // 半宽曲线 H(x) 的单调分段边界（升序、含端点）
    private readonly double[] _radii;      // 厚度可能变化的半径（升序）
    private readonly Cut[] _cuts;

    private readonly struct Cut
    {
        public readonly double X0, X1, Z0, Z1;          // 包围盒（保守）
        public readonly Func<double, double, bool> Contains;
        public Cut(double x0, double x1, double z0, double z1, Func<double, double, bool> c) { X0 = x0; X1 = x1; Z0 = z0; Z1 = z1; Contains = c; }
        public bool OverlapsX(double a, double b) => b >= X0 && a <= X1;
        public bool OverlapsZ(double a, double b) => b >= Z0 && a <= Z1;
    }

    public AnalyticMaterial(FlangePlate g)
    {
        Plate = g ?? throw new ArgumentNullException(nameof(g));
        _tip = g.TabTipXMm; _R = g.DiscRadiusMm; _rh = g.HoleRadiusMm; _leg = g.WeldFilletLegMm;
        _xMax = g.TwoTabs ? -g.TabTipXMm : g.DiscRadiusMm;
        _zHalf = Math.Max(g.DiscRadiusMm, g.TabEndHalfWidthMm);
        if (g.ExtensionMm > 1e-9) _zHalf = Math.Max(_zHalf, g.ExtHalfWidthMm);

        // 半宽曲线的分段边界（单舌在 −x 侧）：舌尖、舌端（有延长段）、切点／圆角两端、0（圆弧顶）、盘缘
        var (xt, w) = g.Tangent();
        var left = new List<double> { _tip };
        if (g.ExtensionMm > 1e-9) left.Add(g.TabEndXMm);
        if (g.TabParallel && g.TabFilletMm > 1e-9 && g.TabEndHalfWidthMm < _R)
        {
            double rf = g.TabFilletMm, zc = g.TabEndHalfWidthMm + rf;
            double xc = -Math.Sqrt(Math.Max(0, (_R + rf) * (_R + rf) - zc * zc));
            left.Add(xc); left.Add(xc * _R / (_R + rf));
        }
        left.Add(xt);
        var all = new List<double>(left) { 0.0 };
        if (g.TwoTabs) all.AddRange(left.Select(v => -v)); else all.Add(_R);
        _pieces = Dedup(all.Where(v => v >= _tip - 1e-9 && v <= _xMax + 1e-9).Select(v => Math.Clamp(v, _tip, _xMax)));

        // 厚度断点半径：孔边（材料起点）、焊脚环外缘、台阶半径、加厚半径
        var rad = new List<double> { _rh };
        if (_leg > 1e-9) rad.Add(_rh + _leg);
        int nStep = Math.Min(g.DiscStepRadiiMm.Length, g.DiscStepThicknessMm.Length);
        for (int k = 0; k < nStep; k++) rad.Add(g.DiscStepRadiiMm[k]);
        if (g.ThickenRadiusMm > _rh) rad.Add(g.ThickenRadiusMm);
        _radii = Dedup(rad.Where(v => v > 0));

        // 开孔／开槽：包围盒保守取，判定只调形状自己的 Contains（与 Inside 同一份）
        var cuts = new List<Cut>();
        foreach (var h in g.TabHoles) cuts.Add(HoleCut(h));
        foreach (var h in g.DiscCutHoles) cuts.Add(HoleCut(h));
        foreach (var s in g.DiscSlots)
        {
            var s2 = s;
            double ro = Math.Max(s.RInMm, s.ROutMm);
            cuts.Add(new Cut(-ro, ro, -ro, ro, (x, z) => s2.Contains(x, z)));
        }
        _cuts = cuts.ToArray();

        static Cut HoleCut(FlangePlate.TabHole h)
        {
            double e = h.RMm * Math.Max(1.0, h.AspectXZ) + 1e-9;
            var h2 = h;
            return new Cut(h.XMm - e, h.XMm + e, h.ZMm - e, h.ZMm + e, (x, z) => h2.Contains(x, z));
        }
    }

    private static double[] Dedup(IEnumerable<double> v)
    {
        var o = new List<double>();
        foreach (double x in v.OrderBy(t => t))
            if (o.Count == 0 || x - o[^1] > 1e-9) o.Add(x);
        return o.ToArray();
    }

    public (double XMin, double XMax, double ZMin, double ZMax, bool Exact) MaterialEnvelope()
        => (_tip, _xMax, -_zHalf, _zHalf, true);

    public string Describe() => $"解析板精确积分（z 向精确、x 向 GL16 分段；断点 {_pieces.Length} 个、厚度半径 {_radii.Length} 个、开孔/槽 {_cuts.Length} 个）";

    // ─────────────────────────────────────────── Gauss–Legendre 16 点
    private static readonly double[] GlX =
    {
        -0.9894009349916499, -0.9445750230732326, -0.8656312023878318, -0.7554044083550030,
        -0.6178762444026438, -0.4580167776572274, -0.2816035507792589, -0.0950125098376374,
         0.0950125098376374,  0.2816035507792589,  0.4580167776572274,  0.6178762444026438,
         0.7554044083550030,  0.8656312023878318,  0.9445750230732326,  0.9894009349916499,
    };
    private static readonly double[] GlW =
    {
        0.0271524594117541, 0.0622535239386479, 0.0951585116824928, 0.1246289712555339,
        0.1495959888165767, 0.1691565193950025, 0.1826034150449236, 0.1894506104550685,
        0.1894506104550685, 0.1826034150449236, 0.1691565193950025, 0.1495959888165767,
        0.1246289712555339, 0.0951585116824928, 0.0622535239386479, 0.0271524594117541,
    };

    private static double Gl(Func<double, double> f, double a, double b)
    {
        if (b - a <= 1e-15) return 0;
        double c = 0.5 * (a + b), h = 0.5 * (b - a), s = 0;
        for (int i = 0; i < 16; i++) s += GlW[i] * f(c + h * GlX[i]);
        return s * h;
    }

    // ─────────────────────────────────────────── 一列 x 上的材料区间（z）
    /// <summary>半宽 H(x)（板件的唯一定义）。</summary>
    private double H(double x) => Plate.HalfWidth(x);

    /// <summary>孔弦半高 zh(x)：|x| &lt; 孔半径时 √(rh² − x²)，否则 0。</summary>
    private double Zh(double x) => Math.Abs(x) < _rh ? Math.Sqrt(_rh * _rh - x * x) : 0.0;

    /// <summary>列 x 上、z ∈ [z0,z1] 内的材料区间（已扣开孔／开槽），升序不重叠。</summary>
    private List<(double a, double b)> Column(double x, double z0, double z1)
    {
        var iv = new List<(double, double)>(2);
        if (x < _tip - 1e-12 || x > _xMax + 1e-12 || z1 <= z0) return iv;
        double h = H(x);
        if (h <= 0) return iv;
        double zh = Zh(x);
        if (h <= zh) return iv;
        AddIv(iv, Math.Max(z0, -h), Math.Min(z1, -zh));
        AddIv(iv, Math.Max(z0, zh), Math.Min(z1, h));
        if (_cuts.Length > 0 && iv.Count > 0)
            foreach (var c in _cuts)
                if (c.OverlapsX(x, x) && c.OverlapsZ(z0, z1))
                    iv = SubtractPredicate(iv, z => c.Contains(x, z), c.Z0, c.Z1);
        return iv;
    }

    private static void AddIv(List<(double, double)> iv, double a, double b) { if (b - a > 1e-12) iv.Add((a, b)); }

    /// <summary>
    /// 从区间列表里扣掉谓词为真的部分：在 [lo,hi] 范围内按 ≤ 0.02 mm 采样谓词，采样点之间谓词翻面处二分到 1e-12。
    /// 只用于开孔／开槽（mm 级形状；比 0.02 mm 还窄的料尖会被漏掉，面积量级 1e-4 mm²）。
    /// </summary>
    private static List<(double a, double b)> SubtractPredicate(List<(double a, double b)> iv, Func<double, bool> inCut, double lo, double hi)
    {
        var outIv = new List<(double, double)>();
        foreach (var (a, b) in iv)
        {
            double sa = Math.Max(a, lo), sb = Math.Min(b, hi);
            if (sb <= sa + 1e-12) { outIv.Add((a, b)); continue; }
            // 采样
            int n = Math.Max(8, (int)Math.Ceiling((sb - sa) / 0.02));
            var pts = new List<(double s, bool cut)>(n + 3);
            for (int i = 0; i <= n; i++) { double s = sa + (sb - sa) * i / n; pts.Add((s, inCut(s))); }
            // 谓词翻面处二分
            var edges = new List<(double s, bool cutAfter)>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                if (pts[i].cut == pts[i + 1].cut) continue;
                double p = pts[i].s, q = pts[i + 1].s; bool cp = pts[i].cut;
                for (int it = 0; it < 60 && q - p > 1e-13; it++)
                { double m = 0.5 * (p + q); if (inCut(m) == cp) p = m; else q = m; }
                edges.Add((0.5 * (p + q), pts[i + 1].cut));
            }
            // 拼回：材料 = 不在切口里的部分；先处理 [a, sa)（切口盒外，全是料）
            bool inC = pts[0].cut;
            double pos = a;
            if (sa > a) { AddIv(outIv, a, sa); pos = sa; }
            bool cutNow = inC;
            foreach (var (s, cutAfter) in edges)
            {
                if (!cutNow) AddIv(outIv, pos, s);
                pos = s; cutNow = cutAfter;
            }
            if (!cutNow) AddIv(outIv, pos, sb);
            if (b > sb) AddIv(outIv, sb, b);
        }
        return outIv;
    }

    // ─────────────────────────────────────────── 列上厚度积分
    /// <summary>∫ t(x,z) dz，z ∈ [a,b]（a ≥ 0）。按厚度断点半径分段，常数段直接乘、焊脚环段 GL16。</summary>
    private double ColumnVolume(double x, double a, double b)
    {
        if (b - a <= 1e-15) return 0;
        // 分段点：各断点半径圆与该列的交点 z = √(ρ² − x²)
        var zb = new List<double> { a, b };
        foreach (double rho in _radii)
            if (rho > Math.Abs(x)) { double z = Math.Sqrt(rho * rho - x * x); if (z > a + 1e-12 && z < b - 1e-12) zb.Add(z); }
        zb.Sort();
        double v = 0;
        for (int k = 0; k + 1 < zb.Count; k++)
        {
            double p = zb[k], q = zb[k + 1];
            if (q - p <= 1e-15) continue;
            double zm = 0.5 * (p + q), rm = Math.Sqrt(x * x + zm * zm);
            bool weld = _leg > 1e-9 && rm >= _rh - 1e-12 && rm < _rh + _leg + 1e-12;
            if (!weld)
            {
                double tm = Plate.ThicknessAt(x, zm);
                double t1 = Plate.ThicknessAt(x, p + 0.25 * (q - p)), t3 = Plate.ThicknessAt(x, p + 0.75 * (q - p));
                if (Math.Abs(t1 - tm) <= 1e-12 && Math.Abs(t3 - tm) <= 1e-12) { v += tm * (q - p); continue; }
            }
            // 焊脚环（或厚度不是常数的段）：光滑 ⇒ GL16；孔边一侧的 √ 奇点靠再分两段压住
            double zs = p + 0.25 * (q - p);
            v += Gl(z => Plate.ThicknessAt(x, z), p, zs) + Gl(z => Plate.ThicknessAt(x, z), zs, q);
        }
        return v;
    }

    /// <summary>列 x 在 [z0,z1] 内的（面积、体积、z 一次矩）；<paramref name="clipR"/> &gt; 0 时另与 r ≤ clipR 相交。</summary>
    private (double A, double V, double Mz) ColumnIntegral(double x, double z0, double z1, double clipR = double.NaN)
    {
        var iv = Column(x, z0, z1);
        if (iv.Count == 0) return (0, 0, 0);
        double zc = double.PositiveInfinity;
        if (!double.IsNaN(clipR))
        {
            if (Math.Abs(x) >= clipR) return (0, 0, 0);
            zc = Math.Sqrt(clipR * clipR - x * x);
        }
        double A = 0, V = 0, Mz = 0;
        foreach (var (a0, b0) in iv)
        {
            double a = Math.Max(a0, -zc), b = Math.Min(b0, zc);
            if (b - a <= 1e-12) continue;
            A += b - a; Mz += 0.5 * (b * b - a * a);
            if (a >= 0) V += ColumnVolume(x, a, b);
            else if (b <= 0) V += ColumnVolume(x, -b, -a);
            else V += ColumnVolume(x, 0, -a) + ColumnVolume(x, 0, b);
        }
        return (A, V, Mz);
    }

    // ─────────────────────────────────────────── x 向断点
    /// <summary>在单调段 [a,b] 上求 H(x) = v 的根（有根才返回）。</summary>
    private double? CrossH(double a, double b, double v)
    {
        double ha = H(a) - v, hb = H(b) - v;
        if (ha == 0) return a; if (hb == 0) return b;
        if (ha * hb > 0) return null;
        for (int it = 0; it < 80 && b - a > 1e-13; it++)
        {
            double m = 0.5 * (a + b), hm = H(m) - v;
            if (hm == 0) return m;
            if ((hm < 0) == (ha < 0)) { a = m; ha = hm; } else { b = m; hb = hm; }
        }
        return 0.5 * (a + b);
    }

    /// <summary>矩形 [x0,x1]×[z0,z1] 的 x 向求积断点（升序、含端点）。</summary>
    private double[] XBreaks(double x0, double x1, double z0, double z1, double clipR)
    {
        var bp = new List<double> { x0, x1 };
        void Add(double v) { if (v > x0 + 1e-12 && v < x1 - 1e-12) bp.Add(v); }
        foreach (double p in _pieces) Add(p);
        // 半宽曲线与格线 z0/z1 的交点（每个单调段上二分）
        foreach (double zv in new[] { Math.Abs(z0), Math.Abs(z1) })
            for (int k = 0; k + 1 < _pieces.Length; k++)
            {
                if (_pieces[k + 1] < x0 - 1e-9 || _pieces[k] > x1 + 1e-9) continue;
                var r = CrossH(_pieces[k], _pieces[k + 1], zv);
                if (r.HasValue) Add(r.Value);
            }
        // 各半径圆（孔、厚度断点、保温圆）与 z0/z1/0 的交点
        var radii = new List<double>(_radii);
        if (!double.IsNaN(clipR)) radii.Add(clipR);
        foreach (double rho in radii)
            foreach (double zv in new[] { z0, z1, 0.0 })
                if (rho > Math.Abs(zv)) { double xr = Math.Sqrt(rho * rho - zv * zv); Add(xr); Add(-xr); }
        // 开孔／开槽包围盒边，并把盒内的段切细（≤ 0.1 mm）
        foreach (var c in _cuts)
            if (c.OverlapsX(x0, x1) && c.OverlapsZ(z0, z1))
            {
                Add(c.X0); Add(c.X1);
                double lo = Math.Max(x0, c.X0), hi = Math.Min(x1, c.X1);
                int n = (int)Math.Ceiling((hi - lo) / 0.1);
                for (int i = 1; i < n; i++) Add(lo + (hi - lo) * i / n);
            }
        return Dedup(bp);
    }

    public MaterialIntegral Integrate(double x0, double x1, double z0, double z1) => IntegrateClip(x0, x1, z0, z1, double.NaN);

    private MaterialIntegral IntegrateClip(double x0, double x1, double z0, double z1, double clipR)
    {
        if (x1 <= x0 || z1 <= z0) return MaterialIntegral.Empty;
        if (x1 < _tip || x0 > _xMax || z1 < -_zHalf || z0 > _zHalf) return MaterialIntegral.Empty;
        var bp = XBreaks(x0, x1, z0, z1, clipR);
        // 圆的极值点（x = ±ρ）：列积分在那里像 √(ρ − x) 一样导数无穷，GL 直接积会丢 1e-4 量级 ⇒ 换元 x = ρ − u²（被积函数变成 ~u²，光滑）
        var ext = new List<double>(_radii) { _R };
        if (!double.IsNaN(clipR)) ext.Add(clipR);
        bool IsExt(double v) => ext.Any(r => Math.Abs(Math.Abs(v) - r) <= 1e-9);
        double A = 0, V = 0, Mx = 0, Mz = 0;
        for (int k = 0; k + 1 < bp.Length; k++)
        {
            double a = bp[k], b = bp[k + 1];
            if (b - a <= 1e-13) continue;
            bool rightExt = b > 0 && IsExt(b), leftExt = a < 0 && IsExt(a);
            if (rightExt || leftExt)
            {
                double U = Math.Sqrt(b - a), c = 0.5 * U, h = 0.5 * U;
                for (int i = 0; i < 16; i++)
                {
                    double u = c + h * GlX[i], wgt = GlW[i] * h * 2 * u;
                    double x = rightExt ? b - u * u : a + u * u;
                    var (ca, cv, cmz) = ColumnIntegral(x, z0, z1, clipR);
                    A += wgt * ca; V += wgt * cv; Mx += wgt * x * ca; Mz += wgt * cmz;
                }
                continue;
            }
            double cc = 0.5 * (a + b), hh = 0.5 * (b - a);
            for (int i = 0; i < 16; i++)
            {
                double x = cc + hh * GlX[i], wgt = GlW[i] * hh;
                var (ca, cv, cmz) = ColumnIntegral(x, z0, z1, clipR);
                A += wgt * ca; V += wgt * cv; Mx += wgt * x * ca; Mz += wgt * cmz;
            }
        }
        return new MaterialIntegral(A, V, Mx, Mz);
    }

    public double FractionInsideCircle(double x0, double x1, double z0, double z1, double radiusMm)
    {
        var all = Integrate(x0, x1, z0, z1);
        if (all.Area <= 0) return double.NaN;
        if (!(radiusMm > 0)) return 0;
        var inA = IntegrateClip(x0, x1, z0, z1, radiusMm);
        return Math.Clamp(inA.Area / all.Area, 0, 1);
    }

    // ─────────────────────────────────────────── 线段上的有料长度
    public (double Length, double Mid) SegmentMaterial(bool vertical, double line, double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        if (b - a <= 1e-15) return (0, 0.5 * (a + b));
        List<(double a, double b)> iv = vertical ? Column(line, a, b) : Row(line, a, b);
        double len = 0, mom = 0;
        foreach (var (p, q) in iv) { len += q - p; mom += 0.5 * (p + q) * (q - p); }
        return (len, len > 0 ? mom / len : 0.5 * (a + b));
    }

    /// <summary>行 z 上、x ∈ [a,b] 内的材料区间：谓词 = tip ≤ x ≤ xMax ∧ |z| ≤ H(x) ∧ r ≥ 孔半径 ∧ 不在开孔里；提示点 + 翻面处二分。</summary>
    private List<(double a, double b)> Row(double z, double a, double b)
    {
        var iv = new List<(double, double)>();
        double az = Math.Abs(z);
        if (az > _zHalf + 1e-12) return iv;
        a = Math.Max(a, _tip); b = Math.Min(b, _xMax);
        if (b - a <= 1e-12) return iv;
        bool P(double x)
        {
            if (x < _tip || x > _xMax) return false;
            if (az > H(x)) return false;
            if (x * x + z * z < _rh * _rh) return false;
            foreach (var c in _cuts) if (c.OverlapsX(x, x) && c.OverlapsZ(z, z) && c.Contains(x, z)) return false;
            return true;
        }
        var hints = new List<double> { a, b };
        void Add(double v) { if (v > a + 1e-12 && v < b - 1e-12) hints.Add(v); }
        foreach (double p in _pieces) Add(p);
        for (int k = 0; k + 1 < _pieces.Length; k++) { var r = CrossH(_pieces[k], _pieces[k + 1], az); if (r.HasValue) Add(r.Value); }
        if (_rh > az) { double xr = Math.Sqrt(_rh * _rh - z * z); Add(xr); Add(-xr); }
        foreach (var c in _cuts)
            if (c.OverlapsZ(z, z) && c.OverlapsX(a, b))
            {
                Add(c.X0); Add(c.X1);
                double lo = Math.Max(a, c.X0), hi = Math.Min(b, c.X1);
                int n = Math.Max(8, (int)Math.Ceiling((hi - lo) / 0.02));
                for (int i = 1; i < n; i++) Add(lo + (hi - lo) * i / n);
            }
        var hs = Dedup(hints);
        // 相邻提示点之间谓词常数；在中点取值，翻面处二分定边
        int m = hs.Length - 1;
        var val = new bool[m];
        var mids = new double[m];
        for (int i = 0; i < m; i++) { mids[i] = 0.5 * (hs[i] + hs[i + 1]); val[i] = P(mids[i]); }
        double pos = a; bool cur = val.Length > 0 && val[0];
        // 起点：a 本身若在料内则从 a 起
        for (int i = 0; i + 1 < m; i++)
        {
            if (val[i] == val[i + 1]) continue;
            double p = mids[i], q = mids[i + 1]; bool vp = val[i];
            for (int it = 0; it < 60 && q - p > 1e-13; it++) { double mm = 0.5 * (p + q); if (P(mm) == vp) p = mm; else q = mm; }
            double edge = 0.5 * (p + q);
            if (cur) AddIv(iv, pos, edge);
            pos = edge; cur = val[i + 1];
        }
        if (cur) AddIv(iv, pos, b);
        return iv;
    }
}

/// <summary>
/// ★★★ 2026-09-23（决 101 A，RING；业主 13:3x「其余先按路线甲」）：**图纸（栅格）路径在孔圆 r = rh 的 ± 带内按解析圆判料** —— 包在栅格厚度场外面的材料场，
/// 生成器（<see cref="FlangeMesher.BuildFromMaterialWith"/>）在 <see cref="MeshRules.HoleBandCircle"/> 开（生产）时把 <see cref="ThicknessField"/> 换成它；解析板（<see cref="AnalyticMaterial"/>）不经这里，按构造不受影响。
///
/// ══ 病（G3 归因，deliverable/R48_图纸对拍步0p5离群_归因_2026-09-23.md）
///   栅格把孔圆画成 ±s/2 的台阶；弧面却建在精确圆 rh 上（<see cref="FlangeMesher.AddHoleArcFaces"/>）。孔弧穿过的部分格里料的面积、形心、厚度、裁剪面长与精确圆不同，
///   抽热误差是（栅格步, 栅格相位）的非光滑函数：DrawingPathParityTests 在一个相位上比单调是彩票（步 0.5 在 7 个满覆盖 x 相位上 +0.023～+0.996 W，F6 前就有）；
///   孔弧缺口（§0.-20）也是同一个台阶：孔圆穿过的格量不到料被丢掉，那段弧上没有面。
///
/// ══ 材料定义（逐点；三个入口 Integrate／SegmentMaterial／FractionInsideCircle 都只从这一份定义量）
///   点 p，r = |p|，b = 带宽 <see cref="BandMm"/>：
///   · |r − rh| ≥ b（带外）：照栅格 —— 厚度 = p 所在方格的节点值（节点代表以它为心、边长 = 步的方格，与 <see cref="ThicknessField"/> 同一量法）。
///   · |r − rh| &lt; b 且 r &lt; rh：无料。
///   · |r − rh| &lt; b 且 r ≥ rh：p 所在方格有料 ⇒ 取该节点值；没料 ⇒ 取 p′ = p·(r + s)/r（沿射线外移一个栅格步 s）所在方格的节点值（还没料 ⇒ 无料）。
///     〔厚度规则是**原型选择**（G3 原型 B，归因报告 §5「厚度规则是原型里选的，不是推导出来的」），本实现照抄原型，没有更有依据的规则可换。
///      能推出的只有一条性质：图纸孔径与 rh 相同（孔内节点都没料、孔外都有料）时，p′ 的最近节点半径 ≥ r + s − s/√2 &gt; rh，必落在料上（除非那里另有真边界）——
///      「外移一个栅格步」保证带内 r ≥ rh 的点都取得到厚度；外移更少（例如原型 A 的 rh + s/2）则可能落回孔内节点。〕
///   有料 ⇔ 厚度 &gt; 1e-9（与栅格同一门槛）。
///   · FractionInsideCircle 的「在圆内」逐点判：带内的点按 r ≤ R 精确判；带外的点照栅格的量法按所在方格的节点判（<see cref="FlangePlate.InsideInsulCircle"/>）。
///     这样带外（保温分界圆一般远在带外）与栅格逐位同一口径，带内与弧面、判料同一个圆。
///
/// ══ 积分（精确积分，不用原型的 0.005 mm 子采样；与 <see cref="AnalyticMaterial"/> 同一手法）
///   · 矩形不碰带（|r − rh| &lt; b 的开环）⇒ 三个入口原样转调栅格（带外逐位照旧）。
///   · 碰带：**逐列精确 z 区间** —— 一列 x 上的断点 = 栅格方格边（半格线）、三个圆 rh − b／rh／rh + b 与该列的交点、带内「外移取厚」那段里 p′ 过半格线的点
///     （p′_x = x(1 + s/r) 过 X0+(i+½)s 处闭式 r = s·x/(c − x)；p′_z = z(1 + s/r) 对 z 单调增，二分到浮点）；段与段之间厚度分段常数 ⇒ 解析求和（Σ t·长度、Σ ½(b²−a²)）。
///     **x 向 Gauss–Legendre 16 点复合求积**，在所有列结构会变的 x 处分段：栅格竖半格线、各圆与横线（格边、栅格横半格线）的交点、
///     p′ 曲线（p′_x = 常数、p′_z = 常数两族）与横线／圆的交点及两族互交点；各圆与 p′_x 曲线在 z = 0 处有竖直切线 ⇒ 那一点两侧换元 x = t ± u²（同 AnalyticMaterial 圆极值点的做法）。
///     段内列积分是 x 的解析函数，GL16 到舍入级（门 R48HoleBandCircleGateTests.门a 对 0.005 mm 子采样原型、门c 对闭式环面积核）。
///
/// ══ 带宽：<see cref="FlangeMesher.HoleBandPerStep"/> × 栅格步（生产 1 × s）。【待决定】决 98：失配的尺用一个栅格步还是 s/√2（§0.-20 待决定 3）；
///   门可传 1/√2（MeshRules.HoleBandPerStep）对照；不发明第三个数。图纸孔径与 rh 相同时两把尺判出的料（有料与否、厚度）**按构造**逐点相同
///   （栅格台阶料只落在离孔圆 s/√2 以内，s/√2 &lt; |r − rh| &lt; s 那一环栅格本来就判对），数值相同不能用来区分两把尺；两把尺只在孔径失配时不同，失配时 s/√2 这把没量。
///   代价（2026-09-23 审查后按实测改写，原句「小于一个带宽的真实孔径失配被吸收」只讲了同心失配、且把吸收范围等同带宽）：
///   · 吸收范围不等于带宽：带内 r ≥ rh、栅格本点没料的点沿射线外移**一个栅格步 s**（与带宽 b 无关）取厚 ⇒ 孔弧缺口补不补平由 s 与节点量化定。
///     实测（实施记录 §7）：图纸孔偏大 0.6 s（导航 +0.3）与 1.2 s（判决 +0.3）孔弧缺口全补平，2 s（导航 +1.0）没补平；上界在 1.2 s～2 s 之间没量。
///   · 补料不只补同心失配：带内 r ∈ [rh, rh + b) 的**任何**栅格空节点，只要外移一个栅格步处有料就被补料 —— 与孔腔不相连的贴孔小孔、孔边缺口、落在带内的槽端都算；
///     补多少随栅格相位变，从全部抹掉到部分保留（树外探针：s = 1、半径 0.5 的圆洞在相位 0～0.3 被全部抹掉）。生产默认槽内缘（rh + 焊脚 + 6 mm）碰不到；
///     手填 SlotRInMm，或 .3dm 在孔边一步内有空洞特征时会碰到，没有门覆盖。
///   · 浮空料块：图纸孔比 rh 小（b = s 时约 −0.29 s 起），带外 r &lt; rh − b 的栅格料留在孔圆内、与板隔着一圈空带；图纸孔比 rh 大约 1.5～2.5 s 时，外移补的料外侧仍是栅格空洞、与板不连通。
///     这些料块没有电位、温度边界（生成器量出来记进 <see cref="MeshRecipe.FloatingComponents"/>，经 LineRunner.HoleArcDrawingNotes 进整线说明；不剔除）。
///   规则本身怎么改（孔圆内是否统一判无料、外移是否只补与孔腔相连的空洞）同带宽一样是「多大的失配、往哪个方向由谁吸收」的尺 ——【待决定】决 98。
/// </summary>
public sealed class HoleBandCircleField : IMaterialField
{
    /// <summary>被包的栅格厚度场（原样引用，不复制；与 <see cref="ShellMesh.SourceField"/> 是同一个实例）。</summary>
    public readonly ThicknessField Raw;
    /// <summary>判料用的孔圆半径 mm（= 生成器建弧面的那个圆）。</summary>
    public readonly double HoleRadiusMm;
    /// <summary>带宽 mm（带 = |r − rh| &lt; 它的开环）。</summary>
    public readonly double BandMm;

    private const double TMin = 1e-9;   // 有料门槛：与 ThicknessField 同一个
    private readonly double _rh, _b, _rIn, _rOut, _s, _x0, _z0;
    private readonly int _nx, _nz;
    private readonly double[] _t;

    public HoleBandCircleField(ThicknessField raw, double holeRadiusMm, double bandMm)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        if (!(holeRadiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(holeRadiusMm), "孔圆半径必须为正");
        if (!(bandMm > 0)) throw new ArgumentOutOfRangeException(nameof(bandMm), "带宽必须为正");
        if (!(raw.Step > 0)) throw new ArgumentOutOfRangeException(nameof(raw), "栅格步必须为正");
        HoleRadiusMm = holeRadiusMm; BandMm = bandMm;
        _rh = holeRadiusMm; _b = bandMm; _rIn = Math.Max(0, _rh - _b); _rOut = _rh + _b;
        _s = raw.Step; _x0 = raw.X0; _z0 = raw.Z0; _nx = raw.Nx; _nz = raw.Nz; _t = raw.T;
    }

    public (double XMin, double XMax, double ZMin, double ZMax, bool Exact) MaterialEnvelope() => Raw.MaterialEnvelope();

    public string Describe()
        => $"孔圆 r = {_rh:0.###} mm ± {_b:0.###} mm 带内按解析圆判料（r ≥ rh 有料；厚度：栅格该点有料取该点、没料沿射线外移一个栅格步取；逐列精确 z 区间 + x 向 GL16 分段）＋ {Raw.Describe()}";

    // ─────────────────────────────────────────── 栅格取值
    private double Node(int i, int j) => (i < 0 || i >= _nx || j < 0 || j >= _nz) ? 0.0 : _t[i * _nz + j];
    private int Near(double v, double origin) => (int)Math.Floor((v - origin) / _s + 0.5);

    /// <summary>矩形（或线段，零宽）与带（|r − rh| &lt; b 的开环）相交。</summary>
    internal bool Touch(double x0, double x1, double z0, double z1)
    {
        double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
        double near = Math.Sqrt(nx * nx + nz * nz);
        double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
        double far = Math.Sqrt(fx * fx + fz * fz);
        return near < _rOut && far > _rIn;
    }

    /// <summary>一段有料：[A, B] 上厚度 T；Band = 这段是带内料（圆内判定按 r 精确判），否则是带外栅格料（按节点 Kx, Kz 判）。</summary>
    private readonly record struct Piece(double A, double B, double T, bool Band, int Kx, int Kz);

    // ─────────────────────────────────────────── 一条轴对齐线上的有料段（精确断点）
    /// <summary>
    /// 固定坐标 u（<paramref name="uIsX"/> = true ⇒ 线 x = u，变量 v = z；否则线 z = u，v = x），v ∈ [lo, hi] 上的有料段。
    /// <paramref name="kU"/> = 固定坐标所在方格的下标（线恰在两列方格的公共边上时，调用方两侧各给一次）。
    /// </summary>
    private void LinePieces(bool uIsX, double u, double lo, double hi, int kU, List<Piece> outp)
    {
        if (!(hi > lo)) return;
        double oV = uIsX ? _z0 : _x0;
        var bp = new List<double>(24) { lo, hi };
        int k0 = (int)Math.Ceiling((lo - oV) / _s - 0.5), k1 = (int)Math.Floor((hi - oV) / _s - 0.5);
        for (int k = k0; k <= k1; k++) { double v = oV + (k + 0.5) * _s; if (v > lo && v < hi) bp.Add(v); }
        foreach (double rho in new[] { _rIn, _rh, _rOut })
            if (rho > Math.Abs(u))
            {
                double w = Math.Sqrt(rho * rho - u * u);
                if (w > lo && w < hi) bp.Add(w);
                if (-w > lo && -w < hi) bp.Add(-w);
            }
        bp.Sort();
        for (int q = 0; q + 1 < bp.Count; q++)
        {
            double a = bp[q], b = bp[q + 1];
            if (!(b > a)) continue;
            double vm = 0.5 * (a + b);
            double r = Math.Sqrt(u * u + vm * vm);
            int kV = Near(vm, oV);
            int kx = uIsX ? kU : kV, kz = uIsX ? kV : kU;
            double t0 = Node(kx, kz);
            if (Math.Abs(r - _rh) >= _b) { if (t0 > TMin) outp.Add(new Piece(a, b, t0, false, kx, kz)); continue; }   // 带外：照栅格
            if (r < _rh) continue;                                                                                      // 带内孔圆内：无料
            if (t0 > TMin) { outp.Add(new Piece(a, b, t0, true, kx, kz)); continue; }                                  // 带内孔圆外、栅格本点有料
            if (a < 0 && b > 0) { Shifted(uIsX, u, a, 0, outp); Shifted(uIsX, u, 0, b, outp); }                        // 本点没料：沿射线外移一个栅格步取
            else Shifted(uIsX, u, a, b, outp);
        }
    }

    /// <summary>p′ 沿线的 v 分量 v(1 + s/r)（对 v 单调增）与 u 分量 u(1 + s/r)（在 v 同号的一半上单调）。</summary>
    private double PV(double u, double v) { double r = Math.Sqrt(u * u + v * v); return v * (1 + _s / r); }
    private double PU(double u, double v) { double r = Math.Sqrt(u * u + v * v); return u * (1 + _s / r); }

    /// <summary>带内、r ≥ rh、栅格本点没料的一段 [a, b]（a, b 同号或一端为 0）：厚度取 p′ = p·(r + s)/r 所在方格的节点值，断点处精确切开。</summary>
    private void Shifted(bool uIsX, double u, double a, double b, List<Piece> outp)
    {
        if (!(b > a)) return;
        double oU = uIsX ? _x0 : _z0, oV = uIsX ? _z0 : _x0;
        var bp = new List<double>(12) { a, b };
        // p′_v 过 V 向半格线 d：单调增 ⇒ 二分
        double pa = PV(u, a), pb = PV(u, b);
        int d0 = (int)Math.Ceiling((pa - oV) / _s - 0.5), d1 = (int)Math.Floor((pb - oV) / _s - 0.5);
        for (int k = d0; k <= d1; k++)
        {
            double d = oV + (k + 0.5) * _s;
            if (!(d > pa && d < pb)) continue;
            double lo = a, hi = b;
            for (int it = 0; it < 200; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (!(mid > lo && mid < hi)) break;
                if (PV(u, mid) < d) lo = mid; else hi = mid;
            }
            bp.Add(0.5 * (lo + hi));
        }
        // p′_u 过 U 向半格线 c：u(1 + s/r) = c ⇒ r = s·u/(c − u)（闭式），v 取本段的符号
        double qa = PU(u, a), qb = PU(u, b);
        double qLo = Math.Min(qa, qb), qHi = Math.Max(qa, qb);
        bool pos = a + b > 0;
        int c0 = (int)Math.Ceiling((qLo - oU) / _s - 0.5), c1 = (int)Math.Floor((qHi - oU) / _s - 0.5);
        for (int k = c0; k <= c1; k++)
        {
            double c = oU + (k + 0.5) * _s;
            if (!(c > qLo && c < qHi)) continue;
            double den = c - u;
            if (den == 0) continue;
            double rr = _s * u / den;
            if (!(rr > Math.Abs(u))) continue;
            double w = Math.Sqrt(rr * rr - u * u), v = pos ? w : -w;
            if (v > a && v < b) bp.Add(v);
        }
        bp.Sort();
        for (int q = 0; q + 1 < bp.Count; q++)
        {
            double pA = bp[q], pB = bp[q + 1];
            if (!(pB > pA)) continue;
            double vm = 0.5 * (pA + pB), r = Math.Sqrt(u * u + vm * vm), f = 1 + _s / r;
            double pu = u * f, pv = vm * f;
            int kx = uIsX ? Near(pu, _x0) : Near(pv, _x0), kz = uIsX ? Near(pv, _z0) : Near(pu, _z0);
            double t = Node(kx, kz);
            if (t > TMin) outp.Add(new Piece(pA, pB, t, true, kx, kz));
        }
    }

    // ─────────────────────────────────────────── 矩形积分（x 向 GL16 分段）
    private static readonly double[] GlX =   // 与 AnalyticMaterial 同一组 16 点 Gauss–Legendre 节点／权
    {
        -0.9894009349916499, -0.9445750230732326, -0.8656312023878318, -0.7554044083550030,
        -0.6178762444026438, -0.4580167776572274, -0.2816035507792589, -0.0950125098376374,
         0.0950125098376374,  0.2816035507792589,  0.4580167776572274,  0.6178762444026438,
         0.7554044083550030,  0.8656312023878318,  0.9445750230732326,  0.9894009349916499,
    };
    private static readonly double[] GlW =
    {
        0.0271524594117541, 0.0622535239386479, 0.0951585116824928, 0.1246289712555339,
        0.1495959888165767, 0.1691565193950025, 0.1826034150449236, 0.1894506104550685,
        0.1894506104550685, 0.1826034150449236, 0.1691565193950025, 0.1495959888165767,
        0.1246289712555339, 0.0951585116824928, 0.0622535239386479, 0.0271524594117541,
    };

    /// <summary>矩形里有「栅格本点没料、却在带内 r ≥ rh」的方格（要外移取厚的那部分）⇒ 要加 p′ 曲线的断点。</summary>
    private bool NeedShift(double x0, double x1, double z0, double z1)
    {
        int i0 = Near(x0, _x0), i1 = Near(x1, _x0), j0 = Near(z0, _z0), j1 = Near(z1, _z0);
        double h = 0.5 * _s;
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                if (Node(i, j) > TMin) continue;
                double cx = _x0 + i * _s, cz = _z0 + j * _s;
                double bx0 = Math.Max(x0, cx - h), bx1 = Math.Min(x1, cx + h), bz0 = Math.Max(z0, cz - h), bz1 = Math.Min(z1, cz + h);
                if (bx1 <= bx0 || bz1 <= bz0) continue;
                double nx = Math.Clamp(0, bx0, bx1), nz = Math.Clamp(0, bz0, bz1);
                double fx = Math.Max(Math.Abs(bx0), Math.Abs(bx1)), fz = Math.Max(Math.Abs(bz0), Math.Abs(bz1));
                if (Math.Sqrt(nx * nx + nz * nz) < _rOut && Math.Sqrt(fx * fx + fz * fz) > _rh) return true;
            }
        return false;
    }

    /// <summary>x 向分段点（升序、含端点）与竖直切线点（这些点两侧换元）。</summary>
    private (double[] Ev, List<double> Tang) XEvents(double x0, double x1, double z0, double z1, double clipR)
    {
        var ev = new List<double> { x0, x1 };
        var tang = new List<double>();
        void Add(double v) { if (v > x0 + 1e-12 && v < x1 - 1e-12) ev.Add(v); }
        // 栅格竖半格线
        int i0 = (int)Math.Ceiling((x0 - _x0) / _s - 0.5), i1 = (int)Math.Floor((x1 - _x0) / _s - 0.5);
        for (int i = i0; i <= i1; i++) Add(_x0 + (i + 0.5) * _s);
        // 横线：格边 + 栅格横半格线
        var hz = new List<double> { z0, z1 };
        int j0 = (int)Math.Ceiling((z0 - _z0) / _s - 0.5), j1 = (int)Math.Floor((z1 - _z0) / _s - 0.5);
        for (int j = j0; j <= j1; j++) { double z = _z0 + (j + 0.5) * _s; if (z > z0 && z < z1) hz.Add(z); }
        bool hasZero = z0 <= 0 && z1 >= 0;
        // 各圆与横线的交点；z = 0 在矩形里 ⇒ 圆的极值点 x = ±ρ 是竖直切线点
        var circ = new List<double> { _rh, _rOut };
        if (_rIn > 0) circ.Add(_rIn);
        if (!double.IsNaN(clipR) && clipR > 0) circ.Add(clipR);
        foreach (double rho in circ)
        {
            foreach (double zc in hz)
                if (rho > Math.Abs(zc)) { double xr = Math.Sqrt(rho * rho - zc * zc); Add(xr); Add(-xr); }
            if (hasZero) { Add(rho); Add(-rho); tang.Add(rho); tang.Add(-rho); }
        }
        if (NeedShift(x0, x1, z0, z1))
        {
            // p′ 两族曲线：p′_x = c（c 为 X 向半格线）、p′_z = d（Z 向半格线）；|p′ − p| = s ⇒ 只取 [x0 − s, x1 + s]、[z0 − s, z1 + s] 里的
            var cs = new List<double>(); var ds = new List<double>();
            int c0 = (int)Math.Ceiling((x0 - _s - _x0) / _s - 0.5), c1 = (int)Math.Floor((x1 + _s - _x0) / _s - 0.5);
            for (int k = c0; k <= c1; k++) cs.Add(_x0 + (k + 0.5) * _s);
            int e0 = (int)Math.Ceiling((z0 - _s - _z0) / _s - 0.5), e1 = (int)Math.Floor((z1 + _s - _z0) / _s - 0.5);
            for (int k = e0; k <= e1; k++) ds.Add(_z0 + (k + 0.5) * _s);
            var circShift = new List<double> { _rh, _rOut };
            if (!double.IsNaN(clipR) && clipR > 0) circShift.Add(clipR);
            foreach (double c in cs)
            {
                // × 横线 z = zc：x(1 + s/√(x² + zc²)) = c，对 x 单调增 ⇒ 二分
                foreach (double zc in hz)
                {
                    double F(double x) => x * (1 + _s / Math.Sqrt(x * x + zc * zc)) - c;
                    double fa = F(x0), fb = F(x1);
                    if (!(fa < 0 && fb > 0)) continue;
                    double lo = x0, hi = x1;
                    for (int it = 0; it < 200; it++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (!(mid > lo && mid < hi)) break;
                        if (F(mid) < 0) lo = mid; else hi = mid;
                    }
                    Add(0.5 * (lo + hi));
                }
                // × 圆 r = ρ：x = cρ/(ρ + s)
                foreach (double rho in circShift) { double x = c * rho / (rho + _s); if (Math.Abs(x) <= rho) Add(x); }
                // z = 0 处竖直切线：c > 0 在 x = c − s，c < 0 在 x = c + s
                if (hasZero && Math.Abs(c) > _s) { double xt = c > 0 ? c - _s : c + _s; Add(xt); tang.Add(xt); }
                // × p′_z = d：p′ = (c, d) ⇒ p = (c, d)·(r′ − s)/r′
                foreach (double d in ds)
                {
                    double rp = Math.Sqrt(c * c + d * d), r = rp - _s;
                    if (r > 0) Add(c * r / rp);
                }
            }
            foreach (double d in ds)
            {
                // × 横线 z = zc：zc(1 + s/r) = d ⇒ r = s·zc/(d − zc)
                foreach (double zc in hz)
                {
                    if (zc == 0 || d == zc) continue;
                    double rr = _s * zc / (d - zc);
                    if (rr > Math.Abs(zc)) { double w = Math.Sqrt(rr * rr - zc * zc); Add(w); Add(-w); }
                }
                // × 圆 r = ρ：z = dρ/(ρ + s)
                foreach (double rho in circShift)
                {
                    double zz = d * rho / (rho + _s);
                    if (Math.Abs(zz) < rho) { double w = Math.Sqrt(rho * rho - zz * zz); Add(w); Add(-w); }
                }
            }
        }
        ev.Sort();
        var o = new List<double>(ev.Count);
        foreach (double v in ev) if (o.Count == 0 || v - o[^1] > 1e-12) o.Add(v);
        return (o.ToArray(), tang);
    }

    /// <summary>矩形上的（面积、体积、x 矩、z 矩、圆 clipR 内的面积）。</summary>
    private (double A, double V, double Mx, double Mz, double AIn) Quad(double x0, double x1, double z0, double z1, double clipR)
    {
        var (ev, tang) = XEvents(x0, x1, z0, z1, clipR);
        bool IsTang(double v) { foreach (double t in tang) if (Math.Abs(t - v) <= 1e-9) return true; return false; }
        double A = 0, V = 0, Mx = 0, Mz = 0, AIn = 0;
        var buf = new List<Piece>(32);
        void Col(double x, double wgt)
        {
            buf.Clear();
            LinePieces(true, x, z0, z1, Near(x, _x0), buf);
            double ca = 0, cv = 0, cm = 0, cin = 0;
            double zR = !double.IsNaN(clipR) && clipR > Math.Abs(x) ? Math.Sqrt(clipR * clipR - x * x) : 0.0;
            bool clip = !double.IsNaN(clipR);
            foreach (var p in buf)
            {
                double len = p.B - p.A;
                ca += len; cv += p.T * len; cm += 0.5 * (p.B * p.B - p.A * p.A);
                if (!clip) continue;
                if (p.Band) cin += Math.Max(0, Math.Min(p.B, zR) - Math.Max(p.A, -zR));
                else if (FlangePlate.InsideInsulCircle(_x0 + p.Kx * _s, _z0 + p.Kz * _s, clipR)) cin += len;
            }
            A += wgt * ca; V += wgt * cv; Mx += wgt * x * ca; Mz += wgt * cm; AIn += wgt * cin;
        }
        void Plain(double a, double b)
        {
            double c = 0.5 * (a + b), h = 0.5 * (b - a);
            for (int i = 0; i < 16; i++) Col(c + h * GlX[i], GlW[i] * h);
        }
        void Sub(double t, double other)   // 换元 x = t ± u²，u ∈ [0, √|other − t|]
        {
            double sgn = other > t ? 1 : -1, U = Math.Sqrt(Math.Abs(other - t)), c = 0.5 * U, h = 0.5 * U;
            for (int i = 0; i < 16; i++) { double u = c + h * GlX[i]; Col(t + sgn * u * u, GlW[i] * h * 2 * u); }
        }
        for (int k = 0; k + 1 < ev.Length; k++)
        {
            double a = ev[k], b = ev[k + 1];
            if (!(b - a > 1e-13)) continue;
            bool ta = IsTang(a), tb = IsTang(b);
            if (ta && tb) { double m = 0.5 * (a + b); Sub(a, m); Sub(b, m); }
            else if (ta) Sub(a, b);
            else if (tb) Sub(b, a);
            else Plain(a, b);
        }
        return (A, V, Mx, Mz, AIn);
    }

    // ─────────────────────────────────────────── IMaterialField
    public MaterialIntegral Integrate(double x0, double x1, double z0, double z1)
    {
        if (!(x1 > x0) || !(z1 > z0) || !Touch(x0, x1, z0, z1)) return Raw.Integrate(x0, x1, z0, z1);   // 不碰带：原样转调栅格（带外逐位照旧）
        var q = Quad(x0, x1, z0, z1, double.NaN);
        return new MaterialIntegral(q.A, q.V, q.Mx, q.Mz);
    }

    public (double Length, double Mid) SegmentMaterial(bool vertical, double line, double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        bool touch = b > a && (vertical ? Touch(line, line, a, b) : Touch(a, b, line, line));
        if (!touch) return Raw.SegmentMaterial(vertical, line, a, b);
        // 线落在哪一列（行）方格里；恰在两列方格的公共边上取两侧平均 —— 与 ThicknessField.SegmentMaterial 同一约定
        double origin = vertical ? _x0 : _z0;
        double uu = (line - origin) / _s, frac = uu - Math.Floor(uu);
        var cols = Math.Abs(frac - 0.5) < 1e-9
            ? new[] { ((int)Math.Floor(uu), 0.5), ((int)Math.Floor(uu) + 1, 0.5) }
            : new[] { ((int)Math.Round(uu), 1.0) };
        double len = 0, mom = 0;
        var buf = new List<Piece>(32);
        foreach (var (k, wgt) in cols)
        {
            buf.Clear();
            LinePieces(vertical, line, a, b, k, buf);
            foreach (var p in buf) { len += wgt * (p.B - p.A); mom += wgt * 0.5 * (p.B * p.B - p.A * p.A); }
        }
        return (len, len > 0 ? mom / len : 0.5 * (a + b));
    }

    public double FractionInsideCircle(double x0, double x1, double z0, double z1, double radiusMm)
    {
        if (!(x1 > x0) || !(z1 > z0) || !Touch(x0, x1, z0, z1)) return Raw.FractionInsideCircle(x0, x1, z0, z1, radiusMm);
        var q = Quad(x0, x1, z0, z1, radiusMm);
        return q.A > 0 ? Math.Clamp(q.AIn / q.A, 0, 1) : double.NaN;
    }
}
