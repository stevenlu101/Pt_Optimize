using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>三维坐标 mm</summary>
public readonly struct Vec3
{
    public readonly double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>单元之间（或单元与边界之间）的一条边</summary>
public sealed class MeshFace
{
    public int A = -1, B = -1;      // 相邻单元；B = −1 表示边界面
    public double Length;           // 边长 mm
    public double DistAB;           // 两单元形心间距 mm（边界面取形心到边中点距离）
    public Vec3 Mid;
    public int Tag;                 // 边界类型，见 ShellMesh.Tag*
}

/// <summary>
/// 壳网格 —— **求解器唯一认识的几何抽象**。
///
/// 设计目的：把「网格」与「求解器」解耦。现有 <see cref="PlateCurrent2D"/> 把均匀掩膜数组
/// 和五点格式焊死在 [i,j] 索引上，导致换密度、换法兰形状、上三维都要改求解器。
/// 求解器一旦只认本类（单元面积 + 边长 + 法向 + 邻居），后续只需换**生成器**：
///
///   生成器① 现有几何（圆盘 + 梯形舌片，变步长）      ← 当前
///   生成器② 任意法兰轮廓                            ← 新法兰设计
///   生成器③ 管 + 法兰装配，管孔处共节点              ← 三维
///
/// 单元未知量放在形心（有限体积），故本类提供的是形心、面积与面拓扑，
/// 节点坐标仅用于建面与绘图。
/// </summary>
public sealed class ShellMesh
{
    public const int TagInterior = 0;
    public const int TagFree = 1;        // 自由边：自然 Neumann（电流不出去、热按表面通量另算）
    public const int TagHole = 2;        // 管孔：与铂管交界，电流/热流出入
    public const int TagTabEnd = 3;      // 舌片末端：铜排压接
    public const int TagTubeEnd = 4;     // 管段端面（三维装配时与法兰共节点）

    public readonly List<Vec3> Nodes = new();
    public readonly List<int[]> Cells = new();      // 每单元 3 或 4 个节点索引
    public readonly List<double> Area = new();      // mm²（边界单元为**被轮廓覆盖的有效面积**）
    public readonly List<Vec3> Centroid = new();
    public readonly List<double> Thickness = new(); // mm
    public readonly List<int> Part = new();         // 0 = 法兰，1 = 管
    public readonly List<MeshFace> Faces = new();

    public int CellCount => Cells.Count;
    public double TotalArea => Area.Sum();
    public double VolumeMm3 => Enumerable.Range(0, CellCount).Sum(i => Area[i] * Thickness[i]);

    /// <summary>
    /// 由单元-节点关系建立面拓扑：同一条边被两个单元共享 ⇒ 内部面；只被一个单元用到 ⇒ 边界面。
    /// </summary>
    public void BuildFaces(Func<Vec3, int>? boundaryTagger = null)
    {
        Faces.Clear();
        var edge = new Dictionary<(int, int), int>();   // 边(小,大) → 首次出现的单元
        var edgeNodes = new Dictionary<(int, int), (int n0, int n1)>();

        for (int c = 0; c < Cells.Count; c++)
        {
            var nd = Cells[c];
            for (int k = 0; k < nd.Length; k++)
            {
                int n0 = nd[k], n1 = nd[(k + 1) % nd.Length];
                var key = n0 < n1 ? (n0, n1) : (n1, n0);
                if (edge.TryGetValue(key, out int other))
                {
                    var (a, b) = (other, c);
                    var mid = (Nodes[n0] + Nodes[n1]) * 0.5;
                    Faces.Add(new MeshFace
                    {
                        A = a, B = b, Mid = mid,
                        Length = (Nodes[n1] - Nodes[n0]).Norm,
                        DistAB = (Centroid[b] - Centroid[a]).Norm,
                        Tag = TagInterior
                    });
                    edge.Remove(key);
                }
                else { edge[key] = c; edgeNodes[key] = (n0, n1); }
            }
        }
        // 剩下的都是边界面
        foreach (var kv in edge)
        {
            var (n0, n1) = edgeNodes[kv.Key];
            var mid = (Nodes[n0] + Nodes[n1]) * 0.5;
            Faces.Add(new MeshFace
            {
                A = kv.Value, B = -1, Mid = mid,
                Length = (Nodes[n1] - Nodes[n0]).Norm,
                DistAB = (mid - Centroid[kv.Value]).Norm,
                Tag = boundaryTagger?.Invoke(mid) ?? TagFree
            });
        }
    }

    public (int interior, int boundary) FaceCounts()
        => (Faces.Count(f => f.B >= 0), Faces.Count(f => f.B < 0));
}

/// <summary>
/// 厚度场 t(x, z) —— 由 <c>Pt_Optimize.Geom thickness</c> 从 .3dm 逐点射线量出。
///
/// **t = 0 表示该点无材料**，于是轮廓外、管孔、开槽三者统一用同一个判据表达；
/// t > 0 直接给出阶梯厚度。求解器要的本来就是 t(x,z)，故不必提取轮廓环 ——
/// 用户在 Rhino 里画什么形状，这里就照单全收，无需参数化、无需改代码。
/// </summary>
public sealed class ThicknessField
{
    public double X0, Z0, Step;
    public int Nx, Nz;
    public double[] T = Array.Empty<double>();

    /// <summary>最近邻取值（网格步长通常 1 mm，远细于特征尺寸，无需插值）</summary>
    public double At(double x, double z)
    {
        int i = (int)Math.Round((x - X0) / Step);
        int j = (int)Math.Round((z - Z0) / Step);
        if (i < 0 || i >= Nx || j < 0 || j >= Nz) return 0;
        return T[i * Nz + j];
    }

    public bool HasMaterial(double x, double z) => At(x, z) > 1e-9;

    public double AreaMm2 => T.Count(v => v > 1e-9) * Step * Step;
    public double VolumeMm3 => T.Sum() * Step * Step;
}

/// <summary>
/// 生成器①：现有法兰几何（圆盘 Ø2R + 平面梯形舌片）的**变步长**结构化四边形网格。
///
/// 分级依据（不是拍脑袋）：
///   · 电流周向/径向重分布的衰减长度 = 管半径 a（薄壳上第 n 阶谐波按 exp(−n·z/a) 衰减）
///   · 热衰减长度 ℓt ≈ 18 mm（模型自报）
///   两者同量级 ⇒ **孔周约 1.5a 半径范围内必须细网格**，远处可大幅放粗。
///
/// 边界单元按 4×4 子采样求**覆盖率**，面积按覆盖率折算 —— 否则粗网格的阶梯边界
/// 会把平面净面积算偏（该面积有 --geom 校核的真值 23591.608 mm²，可作硬检验）。
/// </summary>
public static class FlangeMesher
{
    /// <summary>
    /// 变步长坐标序列：[fineFrom, fineTo] 内步长 hFine，之外渐变到 hCoarse。
    /// 相邻步长比限制在 growth 以内，避免突变导致的格式精度损失。
    /// </summary>
    public static double[] GradedAxis(double min, double max,
                                      double fineFrom, double fineTo,
                                      double hFine, double hCoarse, double growth = 1.3)
    {
        var xs = new List<double> { min };
        double x = min, hPrev = hCoarse;
        while (x < max - 1e-9)
        {
            bool inFine = x >= fineFrom - 1e-9 && x <= fineTo + 1e-9;
            double hTarget = inFine ? hFine : hCoarse;
            // 限制相邻步长比
            double h = Math.Clamp(hTarget, hPrev / growth, hPrev * growth);
            // 提前减速：接近细化区时逐步收窄
            if (!inFine && x < fineFrom)
            {
                double gap = fineFrom - x;
                h = Math.Min(h, Math.Max(hFine, gap * (growth - 1) + hFine));
            }
            h = Math.Min(h, max - x);
            x += h; hPrev = h;
            xs.Add(x);
        }
        return xs.ToArray();
    }

    /// <summary>
    /// 生成法兰平面网格（位于 y = yPlane 的 x–z 平面内）。
    /// </summary>
    /// <param name="hFine">孔周细网格尺寸 mm</param>
    /// <param name="hCoarse">远场粗网格尺寸 mm</param>
    /// <param name="fineRadius">细化半径 mm（自管轴起算）</param>
    public static ShellMesh Build(FlangePlate g, double yPlane = 0,
                                  double hFine = 2.0, double hCoarse = 11.0,
                                  double fineRadius = 45.0)
    {
        var m = new ShellMesh();
        double[] xs = GradedAxis(g.TabTipXMm, g.DiscRadiusMm, -fineRadius, fineRadius, hFine, hCoarse);
        double zMax = g.DiscRadiusMm;
        double[] zs = GradedAxis(-zMax, zMax, -fineRadius, fineRadius, hFine, hCoarse);

        // 节点网格（含全部候选点；未被单元引用的节点无害，仅占内存）
        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], yPlane, zs[j])); }

        // 材料内判据：在轮廓内且不在管孔内
        bool Inside(double x, double z)
        {
            if (x < g.TabTipXMm || x > g.DiscRadiusMm) return false;
            if (Math.Abs(z) > g.HalfWidth(x)) return false;
            return x * x + z * z >= g.HoleRadiusMm * g.HoleRadiusMm;
        }

        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < nz - 1; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                // 4×4 子采样求覆盖率 —— 粗网格的阶梯边界否则会把净面积算偏
                int hit = 0; const int ns = 4;
                for (int a = 0; a < ns; a++)
                    for (int b = 0; b < ns; b++)
                    {
                        double sx = x0 + (a + 0.5) * (x1 - x0) / ns;
                        double sz = z0 + (b + 0.5) * (z1 - z0) / ns;
                        if (Inside(sx, sz)) hit++;
                    }
                double frac = hit / (double)(ns * ns);
                if (frac < 0.25) continue;                 // 覆盖不足四分之一的格子丢弃

                double cxm = 0.5 * (x0 + x1), czm = 0.5 * (z0 + z1);
                m.Cells.Add(new[] { nodeId[i, j], nodeId[i + 1, j], nodeId[i + 1, j + 1], nodeId[i, j + 1] });
                m.Area.Add((x1 - x0) * (z1 - z0) * frac);
                m.Centroid.Add(new Vec3(cxm, yPlane, czm));
                m.Thickness.Add(g.ThicknessAt(cxm, czm));
                m.Part.Add(0);
            }

        m.BuildFaces(mid =>
        {
            double r = Math.Sqrt(mid.X * mid.X + mid.Z * mid.Z);
            if (Math.Abs(r - g.HoleRadiusMm) < 3.0) return ShellMesh.TagHole;
            if (mid.X <= g.TabTipXMm + 3.0) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        return m;
    }

    /// <summary>
    /// 生成器②：**由厚度场直接生成**，适用于任意 Rhino 法兰形状（开槽、阶梯、非对称皆可）。
    ///
    /// 与生成器① 的唯一区别：材料判据从「解析轮廓」换成「厚度场 t &gt; 0」，
    /// 单元厚度也直接取自厚度场（含阶梯）。网格分级仍按孔周加密。
    /// </summary>
    /// <param name="holeRadiusMm">管孔半径，用于边界标记与细化中心</param>
    public static ShellMesh BuildFromField(ThicknessField f, double holeRadiusMm,
                                           double yPlane = 0,
                                           double hFine = 2.0, double hCoarse = 11.0,
                                           double fineRadius = 50.0)
    {
        var m = new ShellMesh();
        double xMin = f.X0, xMax = f.X0 + (f.Nx - 1) * f.Step;
        double zMin = f.Z0, zMax = f.Z0 + (f.Nz - 1) * f.Step;
        double[] xs = GradedAxis(xMin, xMax, -fineRadius, fineRadius, hFine, hCoarse);
        double[] zs = GradedAxis(zMin, zMax, -fineRadius, fineRadius, hFine, hCoarse);

        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], yPlane, zs[j])); }

        double tabTipX = xMin;
        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < nz - 1; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                // 子采样：既求覆盖率，也求该单元的**平均厚度**（阶梯跨越单元时才不失真）
                int hit = 0; double tSum = 0; const int ns = 4;
                for (int a = 0; a < ns; a++)
                    for (int b = 0; b < ns; b++)
                    {
                        double sx = x0 + (a + 0.5) * (x1 - x0) / ns;
                        double sz = z0 + (b + 0.5) * (z1 - z0) / ns;
                        double t = f.At(sx, sz);
                        if (t > 1e-9) { hit++; tSum += t; }
                    }
                if (hit < ns * ns * 0.25) continue;
                double frac = hit / (double)(ns * ns);
                double tAvg = tSum / hit;

                double cxm = 0.5 * (x0 + x1), czm = 0.5 * (z0 + z1);
                m.Cells.Add(new[] { nodeId[i, j], nodeId[i + 1, j], nodeId[i + 1, j + 1], nodeId[i, j + 1] });
                m.Area.Add((x1 - x0) * (z1 - z0) * frac);
                m.Centroid.Add(new Vec3(cxm, yPlane, czm));
                m.Thickness.Add(tAvg);
                m.Part.Add(0);
            }

        m.BuildFaces(mid =>
        {
            double r = Math.Sqrt(mid.X * mid.X + mid.Z * mid.Z);
            // 管孔：紧贴孔半径的那一圈边界面（槽的边界半径不同，不会误判）
            if (Math.Abs(r - holeRadiusMm) < 3.0) return ShellMesh.TagHole;
            if (mid.X <= tabTipX + 4.0) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        return m;
    }
}
