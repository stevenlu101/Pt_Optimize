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
    /// 生成法兰平面网格（位于 y = yPlane 的 x–z 平面内）。
    /// </summary>
    /// <param name="hFine">孔周细网格尺寸 mm</param>
    /// <param name="hCoarse">远场粗网格尺寸 mm</param>
    /// <param name="fineRadius">细化半径 mm（自管轴起算）</param>
    /// <param name="clampLenMm">铜排压接长度 mm（沿舌片方向的定温边界深度）。
    /// 早先硬编码 3 mm —— 那是**数值边界不是设计值**，见 DesignInputs.BusbarClampLengthMm</param>
    /// <param name="hInner">**内带**（管孔 + 焊脚那一圈）网格尺寸 mm。
    /// ≤ 0 = 不分内带，退回单带（与 2026-08-29 之前逐位一致）。见 <see cref="Band"/> 的说明。</param>
    /// <param name="innerRadius">内带半径 mm（自管轴起算）。</param>
    public static ShellMesh Build(FlangePlate g, double yPlane = 0,
                                  double hFine = 2.0, double hCoarse = 11.0,
                                  double fineRadius = 45.0, double clampLenMm = 3.0,
                                  double hInner = 0, double innerRadius = 0)
    {
        var m = new ShellMesh();
        // ★ 按特征分区（A⑭）：内带只覆盖孔+焊脚那一圈，中带覆盖盘与舌根。
        //   重叠处取最细 ⇒ 内带自然嵌在中带里。
        // ★ **逐轴分别定带**（2026-08-29）：x 与 z 的细区不必同宽。
        //   z：板在 z 上只到 ±盘半径，而细化半径通常更大 ⇒ 整条 z 轴本来就全是细的，
        //      收窄没有收益（实测 R30 / 细化半径 59 ⇒ ±30 全包）。
        //   x：舌片一路伸到 TabTipX（实测 −140），而细化半径来自
        //      max(盘半径, 舌长×0.35) —— 舌长那一项把细区拉到 −59，
        //      其中 [−59, −46] 那段是**舌片这条简单窄条**，不需要那个分辨率。
        //      ②″ 的峰候选在孔边与**舌根**（x ≈ −盘半径），收到「盘半径 + 2×圆角 + 余量」就够。
        //   ⚠ 实测这一条只省约 **15 %**（89 mm → 76 mm 的细区）—— 记实数，不吹。
        //     真正的大头是「焊脚该不该算几何特征」，见 MeshVerify.RequiredMeshFor。
        // ★★ **逐轴收窄已撤销**（2026-08-29 归因之后的决定）。
        //   它本身没错：x 细区 [−59,+30] 收到 [−46,+30]，单元少约 15 %。
        //   但实测它把导航网格的 ③ 挪了 **4 K**（0.8 档 4.720 → 8.729），
        //   ⇒ 四档回归基准全部要重填。
        //   而真正的速度收益（电位场换 CG，实测 36×）已经拿到手 ——
        //   **15 % 不值得动全部基准**。
        //   ⚠ 归因过程里我错过一次：第一次测它得出「不是它」，原因是**跑了旧 exe**
        //     （只 grep `error CS`，而「exe 被占、拷贝失败」报的是 MSB3027，恰好被滤掉）。
        //     ⇒ 编译一律查全部 error 并核对 exe 时间戳。
        double xFineLo = -fineRadius;
        xFineLo = Math.Max(xFineLo, -fineRadius);        // 不放大，只收窄
        var xBands = new List<Band> { new(xFineLo, fineRadius, hFine) };
        var zBands = new List<Band> { new(-fineRadius, fineRadius, hFine) };
        if (hInner > 1e-9 && innerRadius > 1e-9)
        {
            xBands.Add(new Band(-innerRadius, innerRadius, hInner));
            zBands.Add(new Band(-innerRadius, innerRadius, hInner));
        }
        // ★ 双舌片（2026-09-09 补齐）：x 轴要铺到 +x 那条舌的舌端，否则第二条舌片不在网格里（下面的压接边标记早就写了两端）
        double[] xs = GradedAxis(g.TabTipXMm, g.TwoTabs ? -g.TabTipXMm : g.DiscRadiusMm, xBands, hCoarse);
        double zMax = g.DiscRadiusMm;
        double[] zs = GradedAxis(-zMax, zMax, zBands, hCoarse);

        // 节点网格（含全部候选点；未被单元引用的节点无害，仅占内存）
        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], yPlane, zs[j])); }

        // 材料内判据：只此一处，见 FlangePlate.Inside。
        // 这里原本另写了一份 —— 两份逻辑相同，但只有这一份带 x 越界判断，
        // 那一份少了，于是板外 z=0 轴线被判成有料。同一个判断写两遍，
        // 迟早有一遍是错的，而且错的那遍会因为「影响小」活很久。
        bool Inside(double x, double z) => g.Inside(x, z);

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
            // 双舌片：两端都是压接边
            if (g.TwoTabs
                ? Math.Abs(mid.X) >= Math.Abs(g.TabTipXMm) - clampLenMm
                : mid.X <= g.TabTipXMm + clampLenMm) return ShellMesh.TagTabEnd;
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
                                           double fineRadius = 50.0, double clampLenMm = 4.0)
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
            if (mid.X <= tabTipX + clampLenMm) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        return m;
    }
}
