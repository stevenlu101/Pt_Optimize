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
