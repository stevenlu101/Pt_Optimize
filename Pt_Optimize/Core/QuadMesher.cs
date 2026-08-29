using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// **四叉树局部加密网格生成器**（2026-08-29，算法普查 A⑭ 的根治办法）。
///
/// ══ 为什么张量网格不够
///
/// 现有 <see cref="FlangeMesher.Build"/> 是**张量积网格**（xs × zs）：
/// 在孔周要 0.146 mm，就得让**整条 x 轴与整条 z 轴**都在那个位置附近细下来 ——
/// 而「那个位置附近」由 <see cref="ShellMesh.GradedAxis"/> 的**区间**表达，
/// 表达不了「半径 25.6–29 mm 的一圈」这种**环形**区域。
///
/// 实测（0.6 档，2026-08-29）：内带被迫取成半径 32 mm 的**圆盘**
/// （因为孔半径本身就 25.6 mm），分区只省下 **37 %**。
///
/// ⚠ 更要紧的是：**起步粗不改变收敛所需的网格尺寸** —— 那由物理定。
///   把焊脚移出特征表只是让阶梯从更粗的一级起步，落点反而可能更细
///   （旧 0.583→0.292→0.146；新 1.0→0.5→0.25→**0.125**）。
///   ⇒ **只有局部加密能改变「收敛点」的成本**：0.146 mm 这个尺寸只有孔周几毫米真的需要，
///     其余区域 1 mm 就够，而张量网格逼着 68 mm 半径的整片陪着细。
///
/// ══ 前置条件已经补好
///
/// 四叉树会产生**悬挂节点**，而 <see cref="ShellMesh.BuildFaces"/> 原本只按节点索引配面
/// ⇒ 每条加密界面都会被判成边界（绝热墙），**不报错，只是答案错**。
/// 2026-08-29 已改成：索引配不上的边再做**几何区间重叠**配对，
/// 每个重叠出一个**部分面**（长度取重叠段），两段加起来铺满粗边 ⇒ **通量守恒**，
/// 并带「已配长度 + 边界长度 = 边长」的自检（配重了当场炸）。
/// 见 `NonConformingFacesTests`。
///
/// ══ 已知的近似（必须说出口）
///
/// ⚠ T 形接头处，两个形心的连线**不垂直于面**，所以两点通量式在那里只有**一阶精度**。
///   这是非协调有限体积的常规代价，用 **2:1 平衡**（相邻单元层级差 ≤ 1）把它限住。
///   ⇒ 它是**近似**，不是恒等式；判据仍以网格无关复核为准。
/// </summary>
public static class QuadMesher
{
    /// <summary>相邻单元允许的最大层级差。2:1 是非协调有限体积的常规约束。</summary>
    public const int MaxLevelJump = 1;

    /// <summary>安全上限：防止 <paramref name="hAt"/> 写错时无限细分。</summary>
    public const int MaxDepth = 12;

    private sealed class Quad
    {
        public double X0, Z0, Size;
        public int Level;
        public Quad[]? Kids;
        public bool Leaf => Kids is null;
        public double Cx => X0 + Size * 0.5;
        public double Cz => Z0 + Size * 0.5;
    }

    /// <summary>
    /// 生成。<paramref name="hAt"/> 给出「该点要求的网格尺寸 mm」——
    /// 它就是分区规则本身（孔周要多细、远场要多粗），由调用方按特征给。
    /// </summary>
    /// <param name="rootSize">根格边长 mm。必须 ≥ 远场尺寸，且能覆盖整片。</param>
    public static ShellMesh Build(FlangePlate g, double yPlane,
                                  Func<double, double, double> hAt,
                                  double rootSize, double clampLenMm)
    {
        if (g is null) throw new ArgumentNullException(nameof(g));
        if (hAt is null) throw new ArgumentNullException(nameof(hAt));
        if (!(rootSize > 0)) throw new ArgumentOutOfRangeException(nameof(rootSize));

        // ── 根格阵列覆盖整片的包围盒
        double xMin = Math.Min(g.TabTipXMm, -g.DiscRadiusMm), xMax = g.DiscRadiusMm;
        double zMax = g.DiscRadiusMm;
        int nx = (int)Math.Ceiling((xMax - xMin) / rootSize);
        int nz = (int)Math.Ceiling((2 * zMax) / rootSize);
        var roots = new List<Quad>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                roots.Add(new Quad { X0 = xMin + i * rootSize, Z0 = -zMax + j * rootSize,
                                     Size = rootSize, Level = 0 });

        // ── 按 hAt 细分
        foreach (var q in roots) Refine(q, hAt);

        // ── 2:1 平衡：反复扫，直到没有相邻层级差 > MaxLevelJump
        var leaves = new List<Quad>();
        for (int pass = 0; pass < MaxDepth + 2; pass++)
        {
            leaves.Clear();
            foreach (var q in roots) Collect(q, leaves);
            if (!Balance(roots, leaves, hAt)) break;
        }
        leaves.Clear();
        foreach (var q in roots) Collect(q, leaves);

        // ── 出网格
        var m = new ShellMesh();
        var nodeOf = new Dictionary<(long, long), int>();
        int Node(double x, double z)
        {
            var key = ((long)Math.Round(x * 1e6), (long)Math.Round(z * 1e6));
            if (nodeOf.TryGetValue(key, out int id)) return id;
            id = m.Nodes.Count; m.Nodes.Add(new Vec3(x, yPlane, z)); nodeOf[key] = id;
            return id;
        }

        foreach (var q in leaves)
        {
            double x0 = q.X0, x1 = q.X0 + q.Size, z0 = q.Z0, z1 = q.Z0 + q.Size;

            // 4×4 子采样求覆盖率 —— 与张量网格同一口径（否则净面积会算偏）
            int hit = 0; const int ns = 4;
            for (int a = 0; a < ns; a++)
                for (int b = 0; b < ns; b++)
                    if (g.Inside(x0 + (a + 0.5) * q.Size / ns, z0 + (b + 0.5) * q.Size / ns)) hit++;
            double frac = hit / (double)(ns * ns);
            if (frac < 0.25) continue;

            m.Cells.Add(new[] { Node(x0, z0), Node(x1, z0), Node(x1, z1), Node(x0, z1) });
            m.Area.Add(q.Size * q.Size * frac);
            m.Centroid.Add(new Vec3(q.Cx, yPlane, q.Cz));
            m.Thickness.Add(g.ThicknessAt(q.Cx, q.Cz));
            m.Part.Add(0);
        }

        m.BuildFaces(mid =>
        {
            double r = Math.Sqrt(mid.X * mid.X + mid.Z * mid.Z);
            if (Math.Abs(r - g.HoleRadiusMm) < 3.0) return ShellMesh.TagHole;
            if (g.TwoTabs
                ? Math.Abs(mid.X) >= Math.Abs(g.TabTipXMm) - clampLenMm
                : mid.X <= g.TabTipXMm + clampLenMm) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        return m;
    }

    /// <summary>
    /// 细分。★ <paramref name="hAt"/> 在**格心 + 四角**都取一遍，取**最小**的那个。
    ///
    /// ⚠ 只取格心是错的（2026-08-29 被 `QuadMesherTests` 抓到）：
    ///   一个**跨在分区边界上**的大格，格心落在粗区就停了，
    ///   而它的四分之一象限落在细区 —— 那一块永远没被检查过。
    ///   实测：细区（r ≤ 29）里出现了 1 mm 的格子，而要求是 0.5。
    ///   ⇒ 过渡区比要求的粗，而**过渡区正是精度要紧的地方**。
    ///   取四角的 min 是**保守**的：宁可多细，不可漏细。
    /// </summary>
    private static void Refine(Quad q, Func<double, double, double> hAt)
    {
        if (q.Level >= MaxDepth) return;
        double want = WantAt(q, hAt);
        if (!(want > 0) || q.Size <= want + 1e-9) return;
        Split(q);
        foreach (var k in q.Kids!) Refine(k, hAt);
    }

    /// <summary>本格要求的网格尺寸 = 格心与四角里**最严**的那个。</summary>
    private static double WantAt(Quad q, Func<double, double, double> hAt)
    {
        double x1 = q.X0 + q.Size, z1 = q.Z0 + q.Size;
        double w = hAt(q.Cx, q.Cz);
        foreach (var (x, z) in new[] { (q.X0, q.Z0), (x1, q.Z0), (q.X0, z1), (x1, z1) })
        {
            double v = hAt(x, z);
            if (v > 0 && (w <= 0 || v < w)) w = v;
        }
        return w;
    }

    private static void Split(Quad q)
    {
        double h = q.Size * 0.5;
        q.Kids = new[]
        {
            new Quad { X0 = q.X0,     Z0 = q.Z0,     Size = h, Level = q.Level + 1 },
            new Quad { X0 = q.X0 + h, Z0 = q.Z0,     Size = h, Level = q.Level + 1 },
            new Quad { X0 = q.X0,     Z0 = q.Z0 + h, Size = h, Level = q.Level + 1 },
            new Quad { X0 = q.X0 + h, Z0 = q.Z0 + h, Size = h, Level = q.Level + 1 },
        };
    }

    private static void Collect(Quad q, List<Quad> into)
    {
        if (q.Leaf) { into.Add(q); return; }
        foreach (var k in q.Kids!) Collect(k, into);
    }

    /// <summary>
    /// 2:1 平衡的一趟。返回 true = 这趟劈了格子（还要再扫一趟）。
    ///
    /// ⚠ 判「相邻」用的是**几何**：两个叶子若在某条边上有重叠，就是邻居。
    ///   层级差 &gt; <see cref="MaxLevelJump"/> 时劈粗的那个。
    /// </summary>
    private static bool Balance(List<Quad> roots, List<Quad> leaves, Func<double, double, double> hAt)
    {
        // ★ 两两比对是 O(n²) —— 3 万叶子就是 10⁹ 次，实测把基准跑挂了（2026-08-29）。
        //   改成：对每个叶子，在**它四条边的采样点外侧**用点定位找邻居（沿树下降，O(log n)）。
        //   采样步长取本格边长的一半 ⇒ 2:1 之下不会漏掉任何邻居。
        bool any = false;
        foreach (var q in new List<Quad>(leaves))
        {
            if (!q.Leaf || q.Level >= MaxDepth) continue;
            double s = q.Size, eps = s * 1e-6;
            double x0 = q.X0, x1 = q.X0 + s, z0 = q.Z0, z1 = q.Z0 + s;

            bool need = false;
            for (double t = s * 0.25; t < s && !need; t += s * 0.5)
            {
                need |= Deeper(roots, x0 - eps, z0 + t, q.Level);   // 左
                need |= Deeper(roots, x1 + eps, z0 + t, q.Level);   // 右
                need |= Deeper(roots, x0 + t, z0 - eps, q.Level);   // 下
                need |= Deeper(roots, x0 + t, z1 + eps, q.Level);   // 上
            }
            if (!need) continue;

            Split(q);
            // ★ 劈完要**重新按 hAt 细化新格子**（2026-08-29 补）：
            //   为平衡劈出来的子格可能落进细区，而它此前从没被 hAt 量过 ——
            //   不补这一步，细区里会留下超大的格子。
            foreach (var k in q.Kids!) Refine(k, hAt);
            any = true;
        }
        return any;
    }

    /// <summary>点 (x,z) 所在的叶子层级是否比 <paramref name="level"/> 深超过 2:1 允许量。</summary>
    private static bool Deeper(List<Quad> roots, double x, double z, int level)
    {
        var q = Locate(roots, x, z);
        return q is not null && q.Level - level > MaxLevelJump;
    }

    /// <summary>沿树下降定位点所在的叶子。落在包围盒外返回 null。</summary>
    private static Quad? Locate(List<Quad> roots, double x, double z)
    {
        Quad? cur = null;
        foreach (var rt in roots)
            if (x >= rt.X0 && x < rt.X0 + rt.Size && z >= rt.Z0 && z < rt.Z0 + rt.Size)
            { cur = rt; break; }
        while (cur is not null && !cur.Leaf)
        {
            double h = cur.Size * 0.5;
            int ix = x >= cur.X0 + h ? 1 : 0, iz = z >= cur.Z0 + h ? 1 : 0;
            cur = cur.Kids![iz * 2 + ix];
        }
        return cur;
    }

    /// <summary>两个格子是否共享一段有长度的边。</summary>
    private static bool EdgeAdjacent(Quad a, Quad b)
    {
        const double t = 1e-9;
        double ax1 = a.X0 + a.Size, az1 = a.Z0 + a.Size;
        double bx1 = b.X0 + b.Size, bz1 = b.Z0 + b.Size;

        bool xTouch = Math.Abs(ax1 - b.X0) < t || Math.Abs(bx1 - a.X0) < t;
        bool zTouch = Math.Abs(az1 - b.Z0) < t || Math.Abs(bz1 - a.Z0) < t;

        double zOv = Math.Min(az1, bz1) - Math.Max(a.Z0, b.Z0);
        double xOv = Math.Min(ax1, bx1) - Math.Max(a.X0, b.X0);

        if (xTouch && zOv > t) return true;      // 竖直公共边
        if (zTouch && xOv > t) return true;      // 水平公共边
        return false;
    }
}
