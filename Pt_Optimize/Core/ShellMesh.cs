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
        // ★★ 剩下的边**先做几何配对**，再论边界（2026-08-29，为局部加密铺路）。
        //
        //   上面那轮是**按节点索引对**配的，它只认协调网格。局部加密（四叉树）会产生
        //   **悬挂节点**：粗单元的边是 (A,B)，两个细单元的边是 (A,M) 与 (M,B) ——
        //   三条谁也配不上谁，于是**全被判成边界面**（B = −1）。
        //   ⇒ **每一条加密界面都变成绝热墙。不报错，只是答案错。**
        //     正是本项目最怕的形态，所以在写四叉树生成器之前先把这里补上。
        //
        //   做法：轴对齐的边按「所在直线」分组，组内按区间求重叠。
        //   每个重叠出一个内部面，长度取**重叠段长**（部分面）——
        //   两段部分面加起来正好铺满粗边 ⇒ **通量守恒**。
        //   没被覆盖的残段才是真边界。
        // 临时归因：关掉几何配对，退回「剩余边全当边界面」
        if (DisableGeomPairing)
        {
            foreach (var kv in edge)
            {
                var (a0, a1) = edgeNodes[kv.Key];
                AddBoundary(kv.Value, Nodes[a0], Nodes[a1], boundaryTagger);
            }
            return;
        }
        PairLeftoverEdges(edge, edgeNodes, boundaryTagger);
    }

    /// <summary>轴对齐边的几何配对容差 mm。网格坐标由累加生成，留一点浮点余量。</summary>
    /// <summary>临时归因开关：关掉几何配对。</summary>
    public static bool DisableGeomPairing;

    private const double EdgeTolMm = 1e-7;

    /// <summary>几何配对配出了几个面 —— **协调网格上应当是 0**。诊断用。</summary>
    public int GeomPaired;

    /// <summary>
    /// 把「按节点索引配不上」的边做**几何**配对，剩下的才当边界面。
    ///
    /// ⚠ 自检：每条边必须满足 <b>已配长度 + 边界长度 = 边长</b>。
    ///   对不上就说明配对逻辑漏了或重了 —— 而那会**静默**地改变通量，
    ///   所以宁可当场炸，也不许放行。
    /// </summary>
    private void PairLeftoverEdges(Dictionary<(int, int), int> edge,
                                   Dictionary<(int, int), (int n0, int n1)> edgeNodes,
                                   Func<Vec3, int>? boundaryTagger)
    {
        // 收集：每条剩余边 → (单元, 是否竖直, 所在直线坐标, 区间[lo,hi], 两端点)
        var segs = new List<(int Cell, bool Vert, double Line, double Lo, double Hi, Vec3 P0, Vec3 P1)>();
        foreach (var kv in edge)
        {
            var (n0, n1) = edgeNodes[kv.Key];
            Vec3 p0 = Nodes[n0], p1 = Nodes[n1];
            bool vert = Math.Abs(p0.X - p1.X) <= EdgeTolMm;
            bool horz = Math.Abs(p0.Z - p1.Z) <= EdgeTolMm;
            if (!vert && !horz)
            {
                // 非轴对齐边：本生成器不产生它。出现了就当边界（并保持旧行为），
                // 但不许悄悄当成内部面。
                AddBoundary(kv.Value, p0, p1, boundaryTagger);
                continue;
            }
            double line = vert ? p0.X : p0.Z;
            double a = vert ? p0.Z : p0.X, b = vert ? p1.Z : p1.X;
            segs.Add((kv.Value, vert, line, Math.Min(a, b), Math.Max(a, b), p0, p1));
        }

        // ★ 按「所在直线」分桶再组内扫描 —— 两两比对是 O(n²)，
        //   3 万条边就是 10⁹ 次，实测直接把基准跑挂了（2026-08-29）。
        //   同一条直线上的边才可能重叠 ⇒ 分桶后组内按起点排序，只比相邻的几条。
        var byLine = new Dictionary<(bool, long), List<int>>();
        for (int i = 0; i < segs.Count; i++)
        {
            var key = (segs[i].Vert, (long)Math.Round(segs[i].Line / EdgeTolMm));
            if (!byLine.TryGetValue(key, out var lst)) byLine[key] = lst = new List<int>();
            lst.Add(i);
        }

        var covered = new double[segs.Count];
        foreach (var lst in byLine.Values)
        {
            lst.Sort((a, b) => segs[a].Lo.CompareTo(segs[b].Lo));
            for (int ii = 0; ii < lst.Count; ii++)
            for (int jj = ii + 1; jj < lst.Count; jj++)
            {
                int i = lst[ii], j = lst[jj];
                if (segs[j].Lo >= segs[i].Hi - EdgeTolMm) break;   // 已排序 ⇒ 后面的更不可能重叠
                var s = segs[i]; var t = segs[j];
                if (s.Cell == t.Cell) continue;
                double lo = Math.Max(s.Lo, t.Lo), hi = Math.Min(s.Hi, t.Hi);
                double ov = hi - lo;
                if (ov <= EdgeTolMm) continue;              // 只碰到端点不算面

                double mc = 0.5 * (lo + hi);
                var mid = s.Vert ? new Vec3(s.Line, s.P0.Y, mc) : new Vec3(mc, s.P0.Y, s.Line);
                Faces.Add(new MeshFace
                {
                    A = s.Cell, B = t.Cell, Mid = mid, Length = ov,
                    DistAB = (Centroid[t.Cell] - Centroid[s.Cell]).Norm,
                    Tag = TagInterior
                });
                covered[i] += ov; covered[j] += ov;
                GeomPaired++;   // 诊断：协调网格上这个数应当是 0
            }
        }

        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            double full = s.Hi - s.Lo;
            double rest = full - covered[i];
            if (rest > full + EdgeTolMm || covered[i] > full + EdgeTolMm)
                throw new InvalidOperationException(
                    $"面拓扑自检失败：单元 {s.Cell} 的一条边被配了 {covered[i]:0.######} mm，"
                    + $"而边长只有 {full:0.######} mm —— 配重了。"
                    + "重配会**静默**地放大通量，不许放行。");
            if (rest > EdgeTolMm) AddBoundary(s.Cell, s.P0, s.P1, boundaryTagger, rest);
        }
    }

    private void AddBoundary(int cell, Vec3 p0, Vec3 p1,
                             Func<Vec3, int>? boundaryTagger, double? lengthOverride = null)
    {
        var mid = (p0 + p1) * 0.5;
        Faces.Add(new MeshFace
        {
            A = cell, B = -1, Mid = mid,
            Length = lengthOverride ?? (p1 - p0).Norm,
            DistAB = (mid - Centroid[cell]).Norm,
            Tag = boundaryTagger?.Invoke(mid) ?? TagFree
        });
    }

    public (int interior, int boundary) FaceCounts()
        => (Faces.Count(f => f.B >= 0), Faces.Count(f => f.B < 0));

    // ── R47 F（2026-09-13）：管孔定温环的自检。**只量不判**——TagHole 的 3 mm 口径本工单不改。
    //   病：|r − 孔半径| < 3 mm 这条判定在盘 R28／孔 25.8（环宽 2.2 mm）的设计上把整段盘外缘与舌肩
    //   都钉成管孔（T = 管根、V = 0）。量出来写进判据「②′管孔净流入」的附注，让读的人看得见。

    /// <summary>生成器填：管孔半径 mm（NaN = 生成器没填）。</summary>
    public double HoleRadiusMm = double.NaN;
    /// <summary>TagHole 面里最大的半径 − 孔半径 mm（NaN = 没有 TagHole 面）。&gt;0 说明定温环越过了孔边。</summary>
    public double HoleTagMaxROverMm = double.NaN;

    /// <summary>BuildFaces 之后调一次：填 <see cref="HoleRadiusMm"/> 与 <see cref="HoleTagMaxROverMm"/>。</summary>
    public void ComputeHoleTagDiagnostics(double holeRadiusMm)
    {
        HoleRadiusMm = holeRadiusMm;
        double rMax = double.NaN;
        foreach (var f in Faces)
        {
            if (f.B >= 0 || f.Tag != TagHole) continue;
            double r = Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z);
            if (double.IsNaN(rMax) || r > rMax) rMax = r;
        }
        HoleTagMaxROverMm = double.IsNaN(rMax) ? double.NaN : rMax - holeRadiusMm;
    }

    /// <summary>被钉成管孔、但半径 &gt; 孔半径 + <paramref name="weldLegMm"/> 的边界总长 mm（焊脚以外的那段本不该是定温边）。</summary>
    public double HoleTagLengthBeyondMm(double weldLegMm)
    {
        if (double.IsNaN(HoleRadiusMm)) return double.NaN;
        double lim = HoleRadiusMm + Math.Max(0, weldLegMm), sum = 0;
        foreach (var f in Faces)
        {
            if (f.B >= 0 || f.Tag != TagHole) continue;
            double r = Math.Sqrt(f.Mid.X * f.Mid.X + f.Mid.Z * f.Mid.Z);
            if (r > lim) sum += f.Length;
        }
        return sum;
    }
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

    /// <summary>该图层里互不相连的实体**组数**（一片法兰的各级台阶算一组）。</summary>
    public int GroupCount = 1;
    /// <summary>本次量的那一组的中面 Y。</summary>
    public double PlaneY;
    /// <summary>
    /// 非空 = 提取时有值得警告的事，**必须显示给用户**。
    /// 典型：一个图层里放了四片法兰（沿 Y 排开），而调用方没说要量哪一片 ——
    /// 那是**能正常跑完**的错，不显示就没人会发现。
    /// </summary>
    public string Warning = "";

    // ── R47 A（2026-09-13）：**精确材料包络**，与栅格步长、图幅留白无关。
    //   病：BuildFromField 的网格轴从图幅（f.X0/f.Z0，含 1～2 mm 留白）起铺，舌片直边 z=±28 落在格子中间，
    //   部分覆盖格的面积按覆盖率折了、**面长没折** ⇒ 有效导电宽 56→59 mm、电阻 −6.7 %、发热 −39 W，
    //   热平衡把差额全记成「从管子抽热」+23 W（隔离实验 V8a：留白 2→0 抽热 26.4→4.01 W）。
    //   由 Geometry3dm.LoadThickness 从几何包围盒填（精确到几何）、AnalyticSurrogate.Rasterize 从解析板填。
    //   NaN = 没填 ⇒ 退回栅格里 t>0 的包络（见 MaterialEnvelope），并把这件事写进 Warning。

    /// <summary>材料在 x／z 上的精确范围 mm（NaN = 未知）。</summary>
    public double XMinMaterial = double.NaN, XMaxMaterial = double.NaN,
                  ZMinMaterial = double.NaN, ZMaxMaterial = double.NaN;

    public bool HasExactEnvelope
        => !double.IsNaN(XMinMaterial) && !double.IsNaN(XMaxMaterial)
        && !double.IsNaN(ZMinMaterial) && !double.IsNaN(ZMaxMaterial);

    /// <summary>
    /// 材料包络：有精确值用精确值；没有就取栅格里 t&gt;0 的包络（精度 = 栅格步长），
    /// 并把「用了栅格包络」写进 <see cref="Warning"/>（只写一次）。全场无材料时返回 exact=false 且四个数为 NaN。
    /// </summary>
    public (double XMin, double XMax, double ZMin, double ZMax, bool Exact) MaterialEnvelope()
    {
        if (HasExactEnvelope) return (XMinMaterial, XMaxMaterial, ZMinMaterial, ZMaxMaterial, true);
        int iMin = int.MaxValue, iMax = -1, jMin = int.MaxValue, jMax = -1;
        for (int i = 0; i < Nx; i++)
            for (int j = 0; j < Nz; j++)
                if (T[i * Nz + j] > 1e-9)
                {
                    if (i < iMin) iMin = i; if (i > iMax) iMax = i;
                    if (j < jMin) jMin = j; if (j > jMax) jMax = j;
                }
        if (iMax < 0) return (double.NaN, double.NaN, double.NaN, double.NaN, false);
        const string tag = "材料包络取自栅格";
        if (!Warning.Contains(tag))
            Warning = (Warning.Length > 0 ? Warning + "　" : "")
                    + $"{tag}（步长 {Step:0.###} mm，没有几何的精确包络）—— 网格轴的端点最多差一个栅格步。";
        return (X0 + iMin * Step, X0 + iMax * Step, Z0 + jMin * Step, Z0 + jMax * Step, false);
    }

    /// <summary>换一份厚度数组、其余（图幅、包络、警告、分组）原样带过来 —— 厚度标度那条路用。</summary>
    public ThicknessField WithThickness(double[] t) => new()
    {
        X0 = X0, Z0 = Z0, Step = Step, Nx = Nx, Nz = Nz, T = t,
        GroupCount = GroupCount, PlaneY = PlaneY, Warning = Warning,
        XMinMaterial = XMinMaterial, XMaxMaterial = XMaxMaterial,
        ZMinMaterial = ZMinMaterial, ZMaxMaterial = ZMaxMaterial,
    };

    /// <summary>最近邻取值（网格步长通常 1 mm，远细于特征尺寸，无需插值）</summary>
    public double At(double x, double z)
    {
        int i = (int)Math.Round((x - X0) / Step);
        int j = (int)Math.Round((z - Z0) / Step);
        if (i < 0 || i >= Nx || j < 0 || j >= Nz) return 0;
        return T[i * Nz + j];
    }


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
    /// <summary>
    /// **一条细化带**：区间 [<see cref="From"/>, <see cref="To"/>] 上要求网格尺寸 <see cref="H"/>。
    /// 多条带可以重叠，重叠处取**最细**的那条。
    /// </summary>
    public readonly record struct Band(double From, double To, double H);

    /// <summary>
    /// 单带版（保持旧调用不变）。
    /// </summary>
    public static double[] GradedAxis(double min, double max,
                                      double fineFrom, double fineTo,
                                      double hFine, double hCoarse, double growth = 1.3)
        => GradedAxis(min, max, new[] { new Band(fineFrom, fineTo, hFine) }, hCoarse, growth);

    /// <summary>
    /// **多带渐变轴**（2026-08-29，算法普查 A⑭）。
    ///
    /// ══ 病在哪
    ///
    /// 此前只有**一条**细化带，而它的两个参数来自**相反的两端**：
    /// <code>
    ///   尺寸 hFine     = min(舌根圆角, 环宽, 焊脚) / 3     ← 由**最小**特征定
    ///   范围 fineRadius = max(盘半径, 舌长×0.35) + 10     ← 由**最大**特征定
    /// </code>
    /// ⇒ **由焊脚（最小）定出的极细尺寸，被铺满由盘径/舌长（最大）定出的整个大区域。**
    ///
    /// 实测（0.6 档网格无关复核，2026-08-29）：fine 收到 <b>0.146 mm</b>，
    /// 而细化半径约 <b>68 mm</b> —— 68 mm 的区域全用 0.146 mm 的格子，
    /// 估算约 8 万单元，**超过 maxCells = 40000 的上限**；实跑到八小时还没出数。
    /// 而焊脚只在管孔外一圈几毫米宽的地方。
    ///
    /// ⇒ 用户 2026-08-29 提的正是这件事：「网格划分只对焊脚细分可以吗？
    ///   衔接焊脚处往外的网格逐渐放大」。**渐变本来就有**（见 growth 与提前减速），
    ///   缺的是**按特征分区**：每个特征只在**它自己所在的区域**要求它的网格。
    ///
    /// ⚠ 安全线：<b>②″ 的峰位是「输出」不是「输入」</b> —— 它可能落在孔边，
    ///   也可能落在舌根凹角。峰若跑进粗区就会被算漏，**而算漏不会报错**，
    ///   只会给一个偏低的 ②″。所以细化带必须覆盖所有已知峰位候选，
    ///   并由 <see cref="MeshAdapt.PeakInsideFine"/> 事后核对峰位落在哪。
    /// </summary>
    public static double[] GradedAxis(double min, double max,
                                      IReadOnlyList<Band> bands, double hCoarse,
                                      double growth = 1.3)
    {
        var bs = (bands ?? Array.Empty<Band>()).Where(b => b.H > 1e-9 && b.To > b.From).ToArray();
        var xs = new List<double> { min };
        double x = min, hPrev = hCoarse;
        while (x < max - 1e-9)
        {
            // 落在哪几条带里就取**最细**的那条；都不落就取远场
            double hTarget = hCoarse;
            foreach (var b in bs)
                if (x >= b.From - 1e-9 && x <= b.To + 1e-9) hTarget = Math.Min(hTarget, b.H);

            double h = Math.Clamp(hTarget, hPrev / growth, hPrev * growth);

            // 提前减速：朝**前方最近的那条带**收窄，免得一步跨进细区造成突变
            foreach (var b in bs)
                if (x < b.From)
                {
                    double gap = b.From - x;
                    h = Math.Min(h, Math.Max(b.H, gap * (growth - 1) + b.H));
                }

            h = Math.Min(h, max - x);
            if (h <= 1e-12) break;              // 防呆：步长塌成 0 会死循环
            x += h; hPrev = h;
            xs.Add(x);
        }
        return xs.ToArray();
    }

    /// <summary>
    /// ★★ R47 A+E（2026-09-13）：**从管轴中心向外铺、两侧镜像**的渐变轴。
    ///
    /// ══ 病在哪（<see cref="GradedAxis(double,double,IReadOnlyList{Band},double,double)"/> 那条老路）
    ///   · E：每条轴从端点起铺，起步 hPrev = hCoarse ⇒ 第一格被 clamp 到 hCoarse/growth = 8.46 mm，
    ///     z 轴 −28 起前六格 8.46→2.28 mm，管孔 −z 半边比 +z 半边粗 4 倍；网格上下不对称。
    ///   · A：图纸路径的端点取图幅（含留白），孔单元集合随舌长、留白变，直边落在格子中间。
    ///
    /// ══ 修法
    ///   从 <paramref name="center"/>（管轴，= 0）出发分别向 +max 与 −|min| 各铺一条：
    ///   起步 hPrev = 中心所在带的 H（孔在细带里 ⇒ 起步就是细步），沿途仍按带取最细、按 growth 渐变、
    ///   对前方的带提前减速；末格取 min(h, 余量) 贴边 ⇒ 直边 z=±zEdge 落在**节点**上，没有
    ///   「面积折了面长没折」的部分格。两条再拼成一条单调轴。
    ///   z 轴 [−R, R] 两侧完全同构 ⇒ 节点关于 0 对称；孔周第一圈格两侧同尺寸。
    ///   中心落在 [min, max] 外时夹到区间端点（那一侧长度 0），仍是单调轴。
    /// </summary>
    /// <param name="anchors">
    /// ★ R47 复修 M1（2026-09-13）：**必须落成节点的坐标**（几何锚点）。z 轴 = ±舌半宽（舌片两条直边）、
    /// x 轴 = 切点 x 与舌尖 x。此前只有端点贴边：盘半径 = 舌半宽的设计（现役两档、盘Ø56）直边恰是端点，
    /// 但舌半宽 &lt; 盘半径时 z=±w 落在格子中间 ⇒ 又是「面积折了面长没折」（工单 §1 A 的病换个地方复发）。
    /// 铺到锚点前把步长收到恰好落在锚点上（与末格贴边同一手法），锚点之后按原本想要的步长继续渐变，
    /// 免得一个短格把后面的格子都拖小。落在区间外或与端点／中心重合的锚点忽略。
    /// </param>
    public static double[] GradedAxisCentered(double min, double max,
                                              IReadOnlyList<Band> bands, double hCoarse,
                                              double growth = 1.3, double center = 0.0,
                                              IReadOnlyList<double>? anchors = null)
    {
        if (!(max > min)) throw new ArgumentException($"轴范围无效：[{min}, {max}]");
        var bs = (bands ?? Array.Empty<Band>()).Where(b => b.H > 1e-9 && b.To > b.From).ToArray();
        double c0 = Math.Clamp(center, min, max);
        var anc = (anchors ?? Array.Empty<double>())
                  .Where(a => !double.IsNaN(a) && a > min + 1e-9 && a < max - 1e-9 && Math.Abs(a - c0) > 1e-9)
                  .Distinct().OrderBy(a => a).ToArray();

        // 该点所在带里最细的 H；都不落就是远场
        double HAt(double x)
        {
            double h = hCoarse;
            foreach (var b in bs)
                if (x >= b.From - 1e-9 && x <= b.To + 1e-9) h = Math.Min(h, b.H);
            return h;
        }

        // 单侧：从 c0 朝 dir（+1／−1）铺到 end，返回**不含 c0** 的坐标序列（沿 dir 顺序）
        List<double> Side(int dir, double end)
        {
            var xs = new List<double>();
            double x = c0, hPrev = HAt(c0);
            double len = Math.Abs(end - c0);
            double s = 0;                                   // 已走的距离
            // 本侧的锚点（按前进方向排序，用「已走距离」表示）
            var sAnc = anc.Where(a => dir > 0 ? a > c0 : a < c0).Select(a => Math.Abs(a - c0)).OrderBy(v => v).ToList();
            int ia = 0;
            while (s < len - 1e-9)
            {
                double h = Math.Clamp(HAt(x), hPrev / growth, hPrev * growth);
                // 提前减速：朝**前方**的带收窄，免得一步跨进细区造成突变
                foreach (var b in bs)
                {
                    double gap = dir > 0 ? b.From - x : x - b.To;
                    if (gap > 0) h = Math.Min(h, Math.Max(b.H, gap * (growth - 1) + b.H));
                }
                double hWant = h;
                h = Math.Min(h, len - s);                   // 末格贴边：直边落在节点上
                while (ia < sAnc.Count && sAnc[ia] <= s + 1e-9) ia++;   // 已经过的锚点
                bool hitAnchor = false;
                // 锚点贴节点：锚点落在这一格里 ⇒ 把这一格收到锚点上。
                // ★ R47 第三轮 N3（2026-09-13）：**不造发丝格** —— 锚点落在计划节点 s+h 之后、但离它不到 hWant/4 时，
                //   原来是先放 s+h 这个节点、下一格再收到锚点 ⇒ 一格 0.02 mm 的发丝格（实测锚点 2.02／−16.005）。
                //   单元长宽比几百，电流场的刚度阵条件数跟着炸。改成把这个计划节点**挪到锚点上**（这一格最多 1.25·hWant，
                //   不插新节点）；锚点落在计划节点之前的情形照旧收短（最短 0.75·hWant 也不会短于 hWant/4）。
                if (ia < sAnc.Count && sAnc[ia] < s + h + 0.25 * hWant - 1e-9 && sAnc[ia] - s > 1e-9)
                { h = sAnc[ia] - s; hitAnchor = true; }
                if (h <= 1e-12) break;                      // 防呆：步长塌成 0 会死循环
                s += h;
                hPrev = hitAnchor ? hWant : h;              // 锚点之后按原本想要的步长继续，不让短格拖小后面的格
                x = c0 + dir * s;
                if (hitAnchor) { s = sAnc[ia]; x = c0 + dir * s; ia++; }   // 抹掉浮点尾巴，锚点精确
                xs.Add(x);
            }
            if (xs.Count > 0) xs[^1] = end;                 // 抹掉累加的浮点尾巴，端点精确
            return xs;
        }

        var neg = Side(-1, min);
        var pos = Side(+1, max);
        var all = new List<double>(neg.Count + pos.Count + 1);
        for (int i = neg.Count - 1; i >= 0; i--) all.Add(neg[i]);
        all.Add(c0);
        all.AddRange(pos);
        return all.ToArray();
    }

    /// <summary>
    /// 管孔边界面的判定口径：|r − 孔半径| &lt; 3 mm。**三份收成一份**（R47 F，2026-09-13：
    /// 原 Build／BuildFromField／QuadMesher 各写一遍，同一个数三处来源）。口径本身不改。
    /// </summary>
    public const double HoleTagBandMm = 3.0;

    public static bool IsHoleFace(Vec3 mid, double holeRadiusMm)
    {
        double r = Math.Sqrt(mid.X * mid.X + mid.Z * mid.Z);
        return Math.Abs(r - holeRadiusMm) < HoleTagBandMm;
    }

    /// <summary>
    /// ★★ R47（2026-09-13）：**解析板的栅格化** —— 生产路径也走它（<see cref="Build"/> = 栅格化 + <see cref="BuildFromField"/>）。
    /// 节点 t = Inside ? ThicknessAt : 0，图幅 = 精确材料包络 ± <paramref name="marginMm"/>，包络四个数照解析板填（与步长、留白无关）。
    /// <see cref="AnalyticSurrogate.Rasterize"/> 转调这里（分析器要留白做连通域填充）。
    /// </summary>
    public static ThicknessField Rasterize(FlangePlate g, double step, double marginMm = 0.0)
    {
        if (!(step > 0)) throw new ArgumentOutOfRangeException(nameof(step), "栅格步长必须为正");
        double xMinM = g.TabTipXMm, xMaxM = g.TwoTabs ? -g.TabTipXMm : g.DiscRadiusMm;
        double zHalf = Math.Max(g.DiscRadiusMm, g.TabEndHalfWidthMm);
        if (g.ExtensionMm > 1e-9) zHalf = Math.Max(zHalf, g.ExtHalfWidthMm);
        // 栅格节点一律落在步长的整数倍上（与管轴 0 对齐）：z 向栅格关于 0 对称，网格（也关于 0 对称）两侧量到的材料才一样；
        // 图幅 = 包络 ± 留白再向外取整到栅格。
        double xMin = Math.Floor((xMinM - marginMm) / step - 1e-9) * step;
        double xMax = Math.Ceiling((xMaxM + marginMm) / step + 1e-9) * step;
        double zMax = Math.Ceiling((zHalf + marginMm) / step + 1e-9) * step;
        int nx = (int)Math.Round((xMax - xMin) / step) + 1;
        int nz = (int)Math.Round((2 * zMax) / step) + 1;
        var f = new ThicknessField
        {
            X0 = xMin, Z0 = -zMax, Step = step, Nx = nx, Nz = nz, T = new double[nx * nz],
            XMinMaterial = xMinM, XMaxMaterial = xMaxM, ZMinMaterial = -zHalf, ZMaxMaterial = zHalf,
        };
        for (int i = 0; i < nx; i++)
        {
            double x = xMin + i * step;
            for (int j = 0; j < nz; j++)
            {
                double z = -zMax + j * step;
                f.T[i * nz + j] = g.Inside(x, z) ? g.ThicknessAt(x, z) : 0.0;
            }
        }
        return f;
    }

    /// <summary>
    /// 生成法兰平面网格（位于 y = yPlane 的 x–z 平面内）。
    ///
    /// ★★★ R47（2026-09-13）：**解析路径 = 栅格化 + 图纸路径**。此前两条路各有一份生成器
    /// （解析：4×4 子采样点上判 Inside、形心取厚；图纸：同样的子采样点上查最近栅格节点、子采样均厚），
    /// 隔离实验（deliverable/R47_网格诊断_2026-09-13.txt）量出两条路在同一套轴上仍差 1.8 W／电阻差 7 %：
    /// 子采样点恰落在两个栅格节点正中间（栅格步 = 网格/4 时**每个**子采样点都是平局），
    /// 平局由 Math.Round 的偶数规则裁决，把舌片直边外的一排格子判成有料（覆盖率恰 0.25，面积折了、面长没折）。
    /// 修法：只留一份生成器 —— 解析板先按「最细网格/4」栅格化（与 4×4 子采样同一分辨率），
    /// 再由 <see cref="BuildFromField"/> 对栅格做**面积积分**（每个栅格节点代表以它为心、边长 = 步长的方格；
    /// 单元覆盖面积 = Σ 方格与单元的重叠面积，厚度 = 体积积分 ÷ 覆盖面积），没有采样点、没有平局。
    /// 「两条路对得上」从此由构造保证：同一解析板 Build 与 BuildFromField(Rasterize) 逐位相同。
    /// ⚠ 这会动解析路径导航网格的数（改前／改后见 deliverable/R47_改前_导航网格_2026-09-13.txt 与 R47_改后_…）。
    /// </summary>
    /// <param name="hFine">孔周细网格尺寸 mm</param>
    /// <param name="hCoarse">远场粗网格尺寸 mm</param>
    /// <param name="fineRadius">细化半径 mm（自管轴起算）</param>
    /// <param name="clampLenMm">铜排压接长度 mm（沿舌片方向的定温边界深度）。
    /// 早先硬编码 3 mm —— 那是**数值边界不是设计值**，见 DesignInputs.BusbarClampLengthMm</param>
    /// <param name="hInner">**内带**（管孔 + 焊脚那一圈）网格尺寸 mm。
    /// ≤ 0 = 不分内带，退回单带。见 <see cref="Band"/> 的说明。</param>
    /// <param name="innerRadius">内带半径 mm（自管轴起算）。</param>
    public static ShellMesh Build(FlangePlate g, double yPlane = 0,
                                  double hFine = 2.0, double hCoarse = 11.0,
                                  double fineRadius = 45.0, double clampLenMm = 3.0,
                                  double hInner = 0, double innerRadius = 0)
    {
        double hFinest = hInner > 1e-9 && innerRadius > 1e-9 ? Math.Min(hFine, hInner) : hFine;
        var f = Rasterize(g, RasterStepFor(hFinest), 0.0);
        var (xa, za) = AnchorsOf(g);
        return BuildFromField(f, g.HoleRadiusMm, yPlane, hFine, hCoarse, fineRadius, clampLenMm,
                              hInner, innerRadius, twoTabs: g.TwoTabs, xAnchors: xa, zAnchors: za);
    }

    /// <summary>
    /// ★ R47 复修 M1：解析板的**几何锚点**（必须落成网格节点的坐标）——
    /// z：±舌端半宽、±延长段半宽（有延长段时）、±切点半宽；x：切点 x（双舌两侧）、舌端 x（有延长段时；舌尖本身是轴端点）。
    /// 与 <see cref="AnchorsFromField"/>（图纸路径从栅格推）是同一件事的两个来源。
    /// </summary>
    public static (double[] X, double[] Z) AnchorsOf(FlangePlate g)
    {
        var (xt, wt) = g.Tangent();
        var zs = new List<double> { g.TabEndHalfWidthMm, -g.TabEndHalfWidthMm, wt, -wt };
        if (g.ExtensionMm > 1e-9) { zs.Add(g.ExtHalfWidthMm); zs.Add(-g.ExtHalfWidthMm); }
        var xs = new List<double> { xt };
        if (g.TwoTabs) xs.Add(-xt);
        if (g.ExtensionMm > 1e-9) { xs.Add(g.TabEndXMm); if (g.TwoTabs) xs.Add(-g.TabEndXMm); }
        return (xs.ToArray(), zs.ToArray());
    }

    /// <summary>
    /// ★ R47 复修 M1：图纸路径的几何锚点 —— 从厚度场量舌尖那一列的材料半宽 w 与盘半径 R
    /// （<see cref="TangentFromField"/>）：z 轴含 ±w，x 轴含推得的切点 x。推不出（没有舌片）就没有锚点。
    /// </summary>
    public static (double[] X, double[] Z) AnchorsFromField(ThicknessField f)
    {
        if (!TangentFromField(f, out double xT, out double w, out _, out _)) return (Array.Empty<double>(), Array.Empty<double>());
        return (new[] { xT }, new[] { w, -w });
    }

    /// <summary>
    /// R47 B：从材料包络推舌盘分界（切点）—— 舌片**直边段**的材料半宽 w、全场最大半宽 R（盘半径）：
    /// 盘的圆弧半宽 √(R²−x²) 首次等于舌半宽 w 的地方就是舌盘分界，x = −√(R²−w²)（w ≥ R 时为 0，即盘Ø56／舌 56 那种）。
    /// 这是**等宽舌**的几何；锥形舌的切点在直线与圆相切处，会偏 —— 所以它只是没有等效片时的退路，附注里说明。
    /// 推不出（没有材料、舌尖不在管轴左侧、盘半径为 0）返回 false。
    /// （R47 复修 M1 从 LineRunner 挪到这里：网格锚点与保温分界要同一份推法；LineRunner.TangentFromField 转调。）
    ///
    /// ★ R47 第三轮 N1（2026-09-13）：**舌半宽怎么取 —— 两种推法，为什么换**
    ///   · 旧推法「舌尖第一列有料的半宽」：只对方角舌尖成立。舌尖切倒角／圆角／收窄时，第一列只剩中间一小段有料，
    ///     半宽被取小（实测倒角 3 mm：w 22 vs 真 25）⇒ z 锚点 ±22 落不到直边 ±25 上，直边又落在格子中间
    ///     （面积折了面长没折，工单 §1 A 的病换个地方复发），抽热差 20 W；切点 x 也跟着偏。
    ///   · 新推法「直边段的半宽」：沿 x 从舌尖往盘方向逐列取材料半宽（取到栅格），**同一个半宽值连续出现最长的那一段**
    ///     就是舌片直边 —— 舌尖倒角只占几列、盘的圆弧半宽逐列都在变（只有盘顶 x≈0 附近几列相同），
    ///     直边段（≥ 压接 40 + 自由段 100 mm）一定是最长的定值段。跨度并列时取出现次数多的。
    ///   切点 x 有两个来源，要**互相印证**：反推值 −√(R²−w²) 与「半宽开始超过 w 的那一列」。两者在
    ///   max(3 mm, 6 栅格步) 内一致就取反推值（没有栅格偏置；舌根圆角 R3 会让那一列早 1～2 mm，容得下）；
    ///   不一致（没有直边段：锥形舌、带肩的舌）就取那一列的实测 x，并在 how 里说明。
    /// </summary>
    public static bool TangentFromField(ThicknessField f, out double xTangent, out double tabHalfWidth, out double discRadius, out string how)
    {
        xTangent = double.NaN; tabHalfWidth = double.NaN; discRadius = double.NaN; how = "";
        var env = f.MaterialEnvelope();
        if (double.IsNaN(env.XMin) || !(env.XMin < 0)) return false;
        int nx = f.Nx, nz = f.Nz;
        double s = f.Step;
        // 逐列材料半宽，取到栅格（z 栅格与管轴对齐 ⇒ 直边 ±w 落在栅格上就是精确的整数步）；只收有料的列
        var cols = new List<(int i, long kw)>();
        double R = 0;
        for (int i = 0; i < nx; i++)
        {
            double h = double.NaN;
            for (int k = 0; k < nz; k++)
                if (f.T[i * nz + k] > 1e-9)
                {
                    double z = Math.Abs(f.Z0 + k * s);
                    if (double.IsNaN(h) || z > h) h = z;
                }
            if (double.IsNaN(h)) continue;
            cols.Add((i, (long)Math.Round(h / s)));
            if (h > R) R = h;
        }
        if (!(R > 0) || cols.Count == 0) return false;
        // 直边段 = 同一个半宽值**连续**出现最长的那一段（跨度并列取出现次数多的）
        var span = new Dictionary<long, int>(); var count = new Dictionary<long, int>();
        for (int a = 0; a < cols.Count;)
        {
            int b = a;
            while (b + 1 < cols.Count && cols[b + 1].kw == cols[a].kw && cols[b + 1].i == cols[b].i + 1) b++;
            int len = b - a + 1; long kw = cols[a].kw;
            span[kw] = Math.Max(span.GetValueOrDefault(kw), len);
            count[kw] = count.GetValueOrDefault(kw) + len;
            a = b + 1;
        }
        long kBest = span.Keys.OrderByDescending(k => span[k]).ThenByDescending(k => count[k]).First();
        double w = kBest * s;
        double wTip = cols[0].kw * s;                  // 舌尖第一列（旧推法），只为附注对照
        // 两个切点来源：反推值，与「直边段之后半宽开始超过 w 的那一列」
        double xFormula = w >= R - 1e-9 ? 0.0 : -Math.Sqrt(R * R - w * w);
        int iRun = cols.FindIndex(c => c.kw == kBest);
        double xCol = double.NaN;
        for (int a = iRun; a < cols.Count; a++)
            if (cols[a].kw > kBest) { xCol = f.X0 + cols[a].i * s; break; }
        double tol = Math.Max(3.0, 6.0 * s);
        bool consistent = double.IsNaN(xCol) ? w >= R - 1e-9 : Math.Abs(xCol - xFormula) <= tol;
        xTangent = consistent ? xFormula : xCol;
        tabHalfWidth = w; discRadius = R;
        string tipNote = Math.Abs(wTip - w) > 1e-9 ? $"；舌尖第一列只有 {wTip:0.0}，舌尖有倒角或收窄" : "";
        how = consistent
            ? $"由材料包络推得：舌半宽 {w:0.0}（直边段，最长定值段 {span[kBest]} 列{tipNote}）、盘半径 {R:0.0} mm ⇒ 舌盘分界 x = −√(R²−w²) = {xFormula:0.00}"
              + (double.IsNaN(xCol) ? "" : $"（半宽开始超过舌半宽的列在 x = {xCol:0.0}，两者一致）")
              + $"（按等宽舌算，锥形舌会偏；栅格步 {s:0.###} mm）"
            : $"由材料包络推得：舌半宽 {w:0.0}（最长定值段只有 {span[kBest]} 列{tipNote}）、盘半径 {R:0.0} mm；"
              + $"反推的切点 {xFormula:0.00} 与半宽开始超过舌半宽的列 x = {xCol:0.0} 差 {Math.Abs(xCol - xFormula):0.0} mm，"
              + $"不像等宽舌接圆盘（锥形或带肩）⇒ 舌盘分界取实测那一列 x = {xCol:0.0}（栅格步 {s:0.###} mm）";
        return true;
    }

    /// <summary>
    /// 网格最细尺寸对应的栅格步长 = 最细网格/4 —— 与旧生成器 4×4 子采样同一分辨率（R47 D：栅格步长跟着网格走）。
    /// 只此一处，LineRunner 图纸分支与 Build 都用它。解析板在内存里栅格化，没有地板；**文件路径**（Rhino 探针）用
    /// <see cref="RasterStepForFile"/>，有 <see cref="RasterStepFloorMm"/> 地板。
    /// </summary>
    public static double RasterStepFor(double hFinestMm) => hFinestMm / 4.0;

    /// <summary>
    /// ★ R47 复修 M4：**文件路径**栅格步的地板 0.05 mm。依据：Rhino 探针逐点打射线，耗时 ∝ 步⁻²
    /// （实测 deliverable/设计记录_管壁0.8mm.3dm「入口」层 170×60 mm：步 1／0.5／0.25 mm 各 6.7／8.6／15.8 s，
    /// 约 5 s 起步 + 65 µs/点 ⇒ 0.05 mm 约 4.1 M 点 ≈ 5 分钟；再往下每减半 ×4，0.025 mm 就要 20 分钟一片，一档四片、一趟六档不可接受）。
    /// 到了地板才写「已到图纸分辨率」——网格再细，栅格也不再跟着细。
    /// </summary>
    public const double RasterStepFloorMm = 0.05;

    /// <summary>文件路径的栅格步 = max(最细网格/4, 地板 0.05 mm)。</summary>
    public static double RasterStepForFile(double hFinestMm) => Math.Max(RasterStepFor(hFinestMm), RasterStepFloorMm);

    /// <summary>文件路径在这个网格上是否已到地板（栅格不再跟着网格细）。</summary>
    public static bool RasterAtFloor(double hFinestMm) => RasterStepFor(hFinestMm) < RasterStepFloorMm - 1e-12;

    /// <summary>
    /// 生成器：**由厚度场直接生成**，适用于任意 Rhino 法兰形状（开槽、阶梯、非对称皆可）；解析板经 <see cref="Rasterize"/> 也走这里。
    ///
    /// 材料判据 = 栅格 t &gt; 0；每个栅格节点代表以它为心、边长 = 栅格步的方格（节点值就是那一小块的厚度），
    /// 单元的覆盖面积与体积按方格与单元的**重叠面积**积分（R47：不再用子采样点，没有平局）。
    /// 覆盖率 &lt; 1/4 的格子丢弃（与旧口径同）。网格分级仍按孔周加密，轴从管轴中心向外铺。
    /// </summary>
    /// <param name="holeRadiusMm">管孔半径，用于边界标记与细化中心</param>
    /// <param name="hInner">内带网格尺寸 mm（管孔 + 焊脚那一圈），≤0 = 不分内带（R47 D）。</param>
    /// <param name="innerRadius">内带半径 mm。</param>
    /// <param name="twoTabs">双舌片：两端都是压接边（解析板经 Build 进来时按板的 TwoTabs 传）。</param>
    /// <param name="xAnchors">x 轴必须落成节点的坐标（null = 从厚度场推：<see cref="AnchorsFromField"/>）。</param>
    /// <param name="zAnchors">z 轴必须落成节点的坐标（null = 从厚度场推：±舌半宽）。</param>
    public static ShellMesh BuildFromField(ThicknessField f, double holeRadiusMm,
                                           double yPlane = 0,
                                           double hFine = 2.0, double hCoarse = 11.0,
                                           double fineRadius = 50.0, double clampLenMm = 4.0,
                                           double hInner = 0, double innerRadius = 0,
                                           bool twoTabs = false,
                                           IReadOnlyList<double>? xAnchors = null, IReadOnlyList<double>? zAnchors = null)
    {
        var m = new ShellMesh();
        // ★ R47 A（2026-09-13）：轴的范围取**真实材料包络**，不再取图幅 f.X0/f.Z0（那含 1～2 mm 留白，
        //   舌片直边落在格子中间 ⇒ 面积折了面长没折 ⇒ 图纸路径抽热多算 23 W，见 ThicknessField.XMinMaterial 的注释）。
        //   没有精确包络时退回栅格 t>0 的包络，ThicknessField.Warning 里会说。
        var env = f.MaterialEnvelope();
        if (!env.Exact && double.IsNaN(env.XMin))
            throw new InvalidOperationException("厚度场里没有材料（t 全为 0）—— 建不出网格。");
        double xMin = env.XMin, xMax = env.XMax, zMin = env.ZMin, zMax = env.ZMax;
        // 中带盖盘与舌根，内带（有的话）盖孔 + 焊脚那一圈，重叠处取最细
        var xBands = new List<Band> { new(-fineRadius, fineRadius, hFine) };
        var zBands = new List<Band> { new(-fineRadius, fineRadius, hFine) };
        if (hInner > 1e-9 && innerRadius > 1e-9)
        {
            xBands.Add(new Band(-innerRadius, innerRadius, hInner));
            zBands.Add(new Band(-innerRadius, innerRadius, hInner));
        }
        // ★ R47 A+E：两条轴都从管轴中心向外铺（GradedAxisCentered）—— z 轴对称，孔周第一圈就是细步，
        //   末格贴边 ⇒ 材料包络的直边落在节点上。
        // ★ R47 复修 M1：轴还要含几何锚点（z ±舌半宽、x 切点）—— 舌半宽 < 盘半径时直边不在端点上。
        //   解析板由 Build 传精确锚点；图纸路径没传就从厚度场推（舌尖那一列的材料半宽）。
        if (xAnchors is null || zAnchors is null)
        {
            var (xa, za) = AnchorsFromField(f);
            xAnchors ??= xa; zAnchors ??= za;
        }
        double[] xs = GradedAxisCentered(xMin, xMax, xBands, hCoarse, anchors: xAnchors);
        double[] zs = GradedAxisCentered(zMin, zMax, zBands, hCoarse, anchors: zAnchors);

        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], yPlane, zs[j])); }

        double s = f.Step, half = 0.5 * s;
        // 一维重叠：单元区间 [a, b] 与栅格第 k 个方格 [X0 + k·s − s/2, X0 + k·s + s/2] 的重叠长度，k 只取有重叠的那几个
        List<(int k, double ov)> Overlap(double a, double b, double origin, int count)
        {
            var lst = new List<(int, double)>();
            int k0 = Math.Max(0, (int)Math.Floor((a - origin) / s - 0.5));
            int k1 = Math.Min(count - 1, (int)Math.Ceiling((b - origin) / s + 0.5));
            for (int k = k0; k <= k1; k++)
            {
                double c = origin + k * s;
                double ov = Math.Min(b, c + half) - Math.Max(a, c - half);
                if (ov > 1e-12) lst.Add((k, ov));
            }
            return lst;
        }

        double tabTipX = xMin;      // 舌尖 = 材料的 XMinMaterial（不再是图幅左缘）
        var ovZ = new List<(int k, double ov)>[nz - 1];
        for (int j = 0; j < nz - 1; j++) ovZ[j] = Overlap(zs[j], zs[j + 1], f.Z0, f.Nz);
        for (int i = 0; i < nx - 1; i++)
        {
            var ovX = Overlap(xs[i], xs[i + 1], f.X0, f.Nx);
            for (int j = 0; j < nz - 1; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                double covered = 0, volume = 0;
                foreach (var (ix, ox) in ovX)
                    foreach (var (iz, oz) in ovZ[j])
                    {
                        double t = f.T[ix * f.Nz + iz];
                        if (t <= 1e-9) continue;
                        double a = ox * oz;
                        covered += a; volume += a * t;
                    }
                double cellArea = (x1 - x0) * (z1 - z0);
                if (covered < 0.25 * cellArea) continue;        // 覆盖不足四分之一的格子丢弃（旧口径）

                double cxm = 0.5 * (x0 + x1), czm = 0.5 * (z0 + z1);
                m.Cells.Add(new[] { nodeId[i, j], nodeId[i + 1, j], nodeId[i + 1, j + 1], nodeId[i, j + 1] });
                m.Area.Add(covered);
                m.Centroid.Add(new Vec3(cxm, yPlane, czm));
                m.Thickness.Add(volume / covered);               // 体积积分 ÷ 覆盖面积：焊脚这类比格子细的堆料按料算
                m.Part.Add(0);
            }
        }

        m.BuildFaces(mid =>
        {
            // 管孔：紧贴孔半径的那一圈边界面（槽的边界半径不同，不会误判）
            if (IsHoleFace(mid, holeRadiusMm)) return ShellMesh.TagHole;
            // 压接边：单舌在舌尖那一段；双舌两端都是
            if (twoTabs
                ? Math.Abs(mid.X) >= Math.Abs(tabTipX) - clampLenMm
                : mid.X <= tabTipX + clampLenMm) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        m.ComputeHoleTagDiagnostics(holeRadiusMm);
        return m;
    }
}
