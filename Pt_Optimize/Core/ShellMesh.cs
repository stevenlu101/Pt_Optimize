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
    public double Length;           // 边长 mm（2026-09-18 起：边上**有料**的长度）
    /// <summary>
    /// 2026-09-19 Fable 5.1：这条面所在格边（内部面 = 两格公共段）的**几何全长** mm，材料裁剪之前的。Length ÷ FullLength = 这条边上有料的份额，
    /// ShellCurrent 用它给 J 重构的方向张量加权（<see cref="ShellCurrent.SliverKappaMin"/>）：一条 0.02 mm 的料边不该和 0.5 mm 的整边一样算一个「方向」。
    /// 不裁剪时（老口径、手造网格）= Length；弧面 = 弧长。
    /// </summary>
    public double FullLength = double.NaN;
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
    /// <summary>
    /// ★ R48（2026-09-13，Opus 5 加）：每个单元的**材料覆盖率** = 有料面积 ÷ 整格面积（满格 1，边界格 &lt; 1）。
    ///
    /// 为什么要记它：<see cref="Area"/> 早就是「被轮廓覆盖的有效面积」（折算过），但相邻两格之间的
    /// **面长** <see cref="MeshFace.Length"/> 一直取的是整条格边长 —— 面积折了、面长没折。
    /// 后果：只有三成材料的边界格，它与邻格之间的导电/导热截面按整格算，电流从那里「抄近路」；
    /// 网格一细，边界格的位置与覆盖率全变 ⇒ 电流密度峰随网格发散（实测 14.88 → 20.04 → 28.85 A/mm²）、
    /// 焦耳热摆动 ±27 W，而「从管子抽的热」只有 5 W 量级 ⇒ 判据被网格噪声淹掉。
    /// （R47 只修了直边那一半：把直边锚成节点，让直边上不再有部分格；圆弧边界上的部分格一直还在。）
    ///
    /// 空表 = 没填（老调用方）⇒ 一律当 1，行为与 R48 之前逐位相同。
    /// </summary>
    public readonly List<double> Frac = new();
    /// <summary>单元 i 的材料覆盖率；没填过就是 1。</summary>
    public double FracOf(int i) => i >= 0 && i < Frac.Count ? Frac[i] : 1.0;
    public readonly List<MeshFace> Faces = new();
    /// <summary>
    /// ★ R48 实验 b（2026-09-14，Opus 5 加）：压接边界落节点的记录 —— 加了哪些 x、没加的为什么没加、几何退化的警告。
    /// 空 = 没有压接边界要处理（老生成器）。见 <see cref="FlangeMesher.BuildFromField"/>。
    /// </summary>
    public string ClampAnchorNote = "";
    /// <summary>
    /// ★ R48（2026-09-14，Opus 5 加）：这张网格是由哪张厚度场生成的（<see cref="FlangeMesher.BuildFromField"/> 填；其他生成器留 null）。
    /// 用途：保温分界圆穿过的格子要知道「格子里的料有多少落在圆内」，而料在格子里的分布只有厚度场知道。
    /// 份额的分子分母必须量同一块东西（同一张栅格），见 <see cref="FlangeMesher.MaterialFraction"/>。
    /// </summary>
    public ThicknessField? SourceField;
    /// <summary>
    /// ★ 2026-09-18，Fable 5.1：这张网格是从哪份**材料场**量出来的（<see cref="IMaterialField"/>：解析板精确积分 <see cref="AnalyticMaterial"/>，或栅格厚度场）。
    /// <see cref="FlangeMesher.BuildFromMaterial"/> 填；保温分界圆上的有料份额（<see cref="FlangeMesher.MaterialFractionInCircle"/>）与「有没有材料来源」（热解配方的混合状态）都读它，
    /// 不再读 <see cref="SourceField"/> 是不是 null（解析路径现在没有栅格，SourceField 为 null，但材料来源在）。
    /// </summary>
    public IMaterialField? Material;
    /// <summary>有材料来源（分界格能按有料份额混合）—— 老的「SourceField 非空」判定的替代，两条路径都读这一处。</summary>
    public bool HasMaterialSource => Material is not null;
    /// <summary>
    /// ★★ R48 生产配方（2026-09-14，Opus 5；物理把关人第十轮定的模型，同日由实验开关转为生产默认）：**压接段整面接触**的格子
    /// （形心落在压接段内；判定 = <see cref="FlangeMesher.InClampSegment"/>，与压接边界面的标签同一个式子）。
    /// <see cref="FlangeMesher.BuildFromField"/>（因而 <see cref="FlangeMesher.Build"/>）默认填它；非空（长度 = 单元数）时
    /// ShellCurrent 把这些格一并钉成 V=1，ShellThermal 一并当作压接格（定温，或把铜排热导按面积分摊到整个接触面）。
    /// 空 = 老口径（只钉外圈）：只有带压接标签的边界面相邻的那一圈格被当作电极与定温格（实验 b 均匀 h=2 片0 钉 68 格，整面 600 格）。
    ///   老口径只剩三处来源（2026-09-14 Opus 5 复审补第三处）：显式传 clampFullFace: false 的探针；不在生产链上的 <see cref="QuadMesher"/>（只给 --quadbench 数单元、不解场）；
    ///   **生产链上**压接段盖到管孔的退化几何（舌片比压接长还短或没有舌片）—— BuildFromField 不填、退回只钉外圈，⚠ 进 ClampAnchorNote 与 LineRunner 输出。
    /// 物理依据：铜排单位长度导电能力约为舌片的 35 倍、在 450 °C 被夹住 ⇒ 接触面整体等电位、趋近夹持温度（零阶正确模型）；
    /// 真正主要进电的内边（x = 舌尖 + 压接长）在老口径里是内部面，没被钉住。
    /// 进生产的依据：deliverable/R48_压接整面接触AB_2026-09-14.txt（B2 工作点，均匀 h = 1／0.5／0.25，整面 − 外圈）：
    ///   片0 抽热 +16.4～+17.6 W、舌区发热 −60.6～−63.4 W、舌区峰 −12.6～−12.8 K；片1 抽热 +32.1～+34.3 W ——
    ///   三档加密差值稳定（是模型差，不是网格噪声），远超跑前写死的门槛（|Δ抽热| ≥ 0.5 W 或舌区峰变化 ≥ 2 K ⇒ 改）。
    /// </summary>
    public bool[] ClampCell = System.Array.Empty<bool>();

    /// <summary>
    /// ★★ R48 F（2026-09-15，Opus 5；物理把关人 2026-09-14 晚）：**压接边界施加在面上**（默认 true）；false = 形心整格口径（A 路，供对照）。
    /// 只在整面接触（<see cref="ClampCell"/> 非空）时起作用 —— 只钉外圈的老口径逐位不变。由 <see cref="FlangeMesher.BuildFromField"/> 的同名参数写入；
    /// **ShellCurrent 与 ShellThermal 只读这一处**（两个求解器口径必须一致，放在网格上就不会一边开一边关）。
    ///
    /// 病（形心整格口径）：压接格整格定电位 V = 1、定温 = 夹持温度，等效边界落在内边那排压接格的**形心**，
    ///   比真实内边（x = 舌尖 + 压接长）往压接区里偏 h/2 ⇒ 自由舌片的电长度与热长度都偏长半格，一阶误差。
    ///   管孔边界 R48 已因同样原因改到面上（ShellThermal 的 holeFaceDirichlet／gHole），压接区照做。
    /// 面上口径：压接格不作未知数（值仍写成边界值：V = 1、定温模式下 T = 夹持温度），与之相邻的自由格之间的**内部面**上施加边界值，
    ///   面导度 = 自由格一侧的 σ·t（热：k·t）× 面长 ÷ 自由格形心到该面的距离 <see cref="CentroidToFaceMm"/>（与边界面 DistAB 同一个定义，也是孔边 gHole 用的那个距离）。
    /// 三种舌端模式（写清，2026-09-15 Opus 5）：
    ///   · 电流场：三种模式一律面上定电位（铜排等电位与热边界怎么取无关）；压接格内 J = 0（电流在铜排里走），整片发热不含压接段；
    ///   · 热场定温（铜排热导 &lt; 0 且夹持温度 ≥ 0）：面上定温，夹持带走 = 通过压接面的热流；
    ///   · 热场热导（铜排热导 ≥ 0）与自由端：热场里**没有**定温边界，压接格仍是热未知数（热导模式照旧按面积分摊铜排热导），不存在「形心还是面」的问题；
    ///     两种口径的差只经电流场（压接格 J 与内边自由格的 J）进来。
    /// ★ 2026-09-15 Opus 5（合并，复审后改）：原为公开可写字段 `public bool ClampFaceDirichlet = true;`，改为只许建网格时写（init）。
    ///   复审查出：<see cref="Recipe"/> 的「压接边界施加在压接面上」是建网格时拍的快照，而两个求解器求解时读这里的当时值 ——
    ///   建完再改（R48ClampFaceGateTests 的条带曾这么写）指纹就会说假话（热解配方只在定温模式下是量出来的，热导与自由端没人兜）。
    ///   现在只有 <see cref="FlangeMesher.BuildFromField"/> 在 new 时写入，手造网格也只能在对象初始化器里给；缺省值不变（true）。
    /// </summary>
    public bool ClampFaceDirichlet { get; init; } = true;

    /// <summary>
    /// ★★ 2026-09-15 Opus 5（J 路，合并把关待办 P2-5）：**「整面接触生效」的唯一定义** = <see cref="ClampCell"/> 长度 = 单元数 **且至少一格为真**。
    /// 此前两种定义并存：面上定温（<see cref="ClampFaceActive"/>）、两个求解器、集总模型排除压接格只看「长度 = 单元数」，网格与热解配方指纹要求「至少一格为真」；
    /// 生成器在压接段里一格形心都没有时也会写一个全假的数组（界面压接长下限 3 mm、网格 4 mm 时可达，deliverable/J路_J9_整面接触全假可达性_本次开跑于2026-09-15_190530.txt），
    /// 于是同一张网格上求解器按整面口径（舌端面积加权、集总模型扣压接长）、指纹却记「未生效」。现在全部读这一处；生成器也不再写全假的数组（见 BuildFromField）。
    /// </summary>
    public bool ClampFullFaceActive => CellCount > 0 && ClampCell.Length == CellCount && Array.IndexOf(ClampCell, true) >= 0;

    /// <summary>R48 F（2026-09-15 Opus 5）：这张网格上压接边界是否真的施加在面上 = 开关开 且 整面接触生效（<see cref="ClampFullFaceActive"/>；2026-09-15 Opus 5（J 路）：原为 ClampCell 长度 = 单元数）。</summary>
    public bool ClampFaceActive => ClampFaceDirichlet && ClampFullFaceActive;

    /// <summary>
    /// R48 F（2026-09-15 Opus 5）：单元形心到面中点的距离 mm。**边界面 <see cref="MeshFace.DistAB"/> 就是按它定义的**（AddBoundary 调这里），
    /// 面上施加边界值（压接面）时自由格一侧的半距也用它 —— 距离取法只有这一份。
    /// </summary>
    public double CentroidToFaceMm(int cell, MeshFace f) => (f.Mid - Centroid[cell]).Norm;

    /// <summary>
    /// R48 F（2026-09-15 Opus 5）：压接格集合 = 带压接标签边界面的格 ∪ <see cref="ClampCell"/>（整面接触时）。
    /// 与 ShellCurrent 钉 V = 1 的格、ShellThermal 的舌端格（tabCell）是同一个集合（两个求解器各自原有的写法不动，两边都有自检对这一份）。
    /// </summary>
    public bool[] ClampSetCells()
    {
        var s = new bool[CellCount];
        foreach (var f in Faces)
            if (f.B < 0 && f.Tag == TagTabEnd) s[f.A] = true;
        if (ClampFullFaceActive)   // 2026-09-15 Opus 5（J 路）：唯一定义（全假数组并进来也不添格，逐位不变）
            for (int i = 0; i < CellCount; i++) if (ClampCell[i]) s[i] = true;
        return s;
    }

    /// <summary>
    /// R48 F（2026-09-15 Opus 5）：面 <paramref name="f"/> 是不是**压接面** —— 内部面、恰好一侧在 <paramref name="clampSet"/> 里；是则给出不在集合里的那一侧。
    /// </summary>
    public static bool IsClampFace(MeshFace f, bool[] clampSet, out int freeCell)
    {
        freeCell = -1;
        if (f.B < 0) return false;
        bool a = clampSet[f.A], b = clampSet[f.B];
        if (a == b) return false;
        freeCell = a ? f.B : f.A;
        return true;
    }

    /// <summary>
    /// ★ 2026-09-15 Opus 5（合并）：G1 配方指纹 × F 面上定温 —— 这张网格上**压接面**的个数：<see cref="ClampFaceActive"/> 时
    /// 按 <see cref="ClampSetCells"/> 数 <see cref="IsClampFace"/> 为真的内部面（与 ShellCurrent／ShellThermal 施加面上边界值用的是同一个集合、同一个判定）；
    /// 不生效（开关关、或没有整面接触）= 0。网格配方 <see cref="MeshRecipe.ClampFaceDirichlet"/> 与热解配方的预判（ShellThermal.RecipeFor）都调这一处，不各数各的。
    /// </summary>
    public int ClampFaceCount()
    {
        if (!ClampFaceActive) return 0;
        var s = ClampSetCells();
        int k = 0;
        foreach (var f in Faces) if (IsClampFace(f, s, out _)) k++;
        return k;
    }

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5；常驻数值把关人第十三、十四轮）：**压接段盖到了管孔**（退化几何：没有舌片、或舌片比压接长还短，
    /// 形心在压接段内的格里有带管孔标签的格）。为 true 时 <see cref="ClampCell"/> 不填、退回只钉外圈，这块板的场没有物理意义。
    /// 由 <see cref="FlangeMesher.BuildFromField"/> 在要求整面接触时判定并填写（显式 clampFullFace: false 的老口径不判，恒 false）。
    /// 下游读这一位（LineRunner 把吃法兰场的判据判不了），**不许**再去 <see cref="ClampAnchorNote"/> 里按「⚠」找字 ——
    /// 那份文字只是给人看的记录，改一个标点门就瞎（2026-09-14 那版就是按「；」切分找「⚠」）。
    /// </summary>
    public bool ClampCoversHole;
    /// <summary>
    /// R48（2026-09-15，Opus 5）：**压接段伸进了圆盘**（单舌：舌尖 + 压接长 ≥ 舌盘分界 x）—— 舌片比压接长还短，这块板几何上不成立。
    /// 与 <see cref="ClampCoversHole"/> 同样由生成器判定、下游读位；配套两个数给说明文字用：压接段内边 x 与舌盘分界 x（mm，没有就是 NaN）。
    /// </summary>
    public bool ClampIntoDisc;
    public double ClampEndXMm = double.NaN, ClampTangentXMm = double.NaN;
    /// <summary>R48（2026-09-15，Opus 5）：生成器用的压接长 mm（给说明文字用；非 BuildFromField 生成的网格为 NaN）。</summary>
    public double ClampLenMm = double.NaN;

    /// <summary>
    /// ★★ R48（2026-09-15，Opus 5；数值把关人第十三轮「配方要能指回出处」）：这张网格**实际生效**的判定网格配方（结构化指纹）。
    /// <see cref="FlangeMesher.BuildFromField"/>（因而 <see cref="FlangeMesher.Build"/>）建完网格后按网格本身量出来填写，不是抄参数；
    /// 其他生成器留 null。门拿 <see cref="MeshRecipe.Rule"/> 与 <see cref="FlangeMesher.ProductionMeshRule"/> 比。
    /// </summary>
    public MeshRecipe? Recipe;

    public int CellCount => Cells.Count;
    public double TotalArea => Area.Sum();
    public double VolumeMm3 => Enumerable.Range(0, CellCount).Sum(i => Area[i] * Thickness[i]);

    /// <summary>
    /// 由单元-节点关系建立面拓扑：同一条边被两个单元共享 ⇒ 内部面；只被一个单元用到 ⇒ 边界面。
    /// </summary>
    /// <param name="clip">
    /// ★ 2026-09-18，Fable 5.1（网格生成根因修复 F2）：**面长按材料裁剪**。给出边的两端点，返回这条边上**有料的长度与有料中点**
    /// （<see cref="IMaterialField.SegmentMaterial"/>）。null = 老口径：面长 = 整条格边（手造网格、QuadMesher、注入对照用）。
    /// 有料长度为 0 的边不建面（不导电、不导热；管孔那一圈由 <see cref="FlangeMesher.AddHoleArcFaces"/> 另建弧面）。
    /// 病：面积早就按覆盖率折了，面长一直取整条格边 ⇒ 部分格与邻格之间的导电／导热截面按整格算（幻影并联导体、热短路），
    ///   带格上占电阻 1～3.6 %、圆弧格上 0.1～0.3 %（网格审计_1／2，2026-09-18）。
    /// </param>
    public void BuildFaces(Func<Vec3, int>? boundaryTagger = null, Func<Vec3, Vec3, (double Length, Vec3 Mid)>? clip = null)
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
                    double full = (Nodes[n1] - Nodes[n0]).Norm;
                    var (len, mid) = clip is null
                        ? (full, (Nodes[n0] + Nodes[n1]) * 0.5)
                        : clip(Nodes[n0], Nodes[n1]);
                    if (len > EdgeTolMm)
                        Faces.Add(new MeshFace
                        {
                            A = a, B = b, Mid = mid,
                            Length = len, FullLength = full,
                            DistAB = (Centroid[b] - Centroid[a]).Norm,
                            Tag = TagInterior
                        });
                    edge.Remove(key);
                }
                else { edge[key] = c; edgeNodes[key] = (n0, n1); }
            }
        }
        _clip = clip;
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
        // ★ R48 实测（2026-09-13，Opus 5）：这里**不调** ScaleInteriorFacesByCoverage —— 见那个方法的注释。
    }

    /// <summary>
    /// ★ R48（2026-09-13，Opus 5）：**内部面的长度按材料覆盖率折算**。
    ///
    /// 面积早就折了（<see cref="Area"/> 是有效面积），面长一直没折 —— 一个只有三成材料的边界格，
    /// 与邻格之间的导电/导热截面 <c>σ·t·L/d</c> 里的 L 却按整格边长算，等于凭空多给了两倍多的通道。
    /// 电流于是沿轮廓「抄近路」，而边界格的位置与覆盖率随网格全变
    /// ⇒ 电流密度峰随加密发散、焦耳热摆动，把只有几瓦的「从管子抽的热」淹掉（实测 ±27 W 对 5 W）。
    ///
    /// 取两侧覆盖率的**较小者**：界面上能过料的宽度由窄的那一侧决定（瓶颈），不是平均。
    ///
    /// ★★ **实测之后没有采用**（2026-09-13，Opus 5 实测）。理由是数，不是道理：
    ///   以「精确几何」（4×4 子采样判内外，面长同样不折）为参照，盘Ø56 片 1 导航网格上
    ///   抽热 精确 4.101 W／不折 4.996 W／折了 1.918 W —— **折算把结果推得离参照更远**，
    ///   倍率 0.5 与 0.25 同向（−3.443／−4.472、−0.231／−1.142）。
    ///   原因：界面上真实的材料覆盖长度要由轮廓与那条边求交得到，
    ///   min(两侧格覆盖率) 是个**过度**的下界（一个满格与一个半格相邻时，界面往往仍是满的）。
    ///   而且它没有解决当初要解决的事：电流密度峰随加密照样发散（16.42 → 20.04 → 28.85）。
    ///   ⇒ 要折就得真求交（贴体或切割单元），不能拿格覆盖率凑；那是另一件工程，不在 R48 里做。
    ///   方法留着不删：<see cref="Frac"/> 本身有诊断价值，将来真做切割单元时这是入口。
    /// </summary>
    private void ScaleInteriorFacesByCoverage()
    {
        if (Frac.Count == 0) return;
        foreach (var f in Faces)
        {
            if (f.B < 0) continue;
            double k = Math.Min(FracOf(f.A), FracOf(f.B));
            if (k < 1.0) f.Length *= k;
        }
    }

    /// <summary>轴对齐边的几何配对容差 mm。网格坐标由累加生成，留一点浮点余量。</summary>
    /// <summary>临时归因开关：关掉几何配对。</summary>
    public static bool DisableGeomPairing;

    /// <summary>BuildFaces 这一次用的面长裁剪（给 PairLeftoverEdges／AddBoundary 用；建完面就没用了）。</summary>
    private Func<Vec3, Vec3, (double Length, Vec3 Mid)>? _clip;

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
        var coveredIv = new List<(double lo, double hi)>[segs.Count];   // 2026-09-18 Fable 5.1：配上的区间本身（裁剪时残段要按真实端点建面）
        for (int i = 0; i < segs.Count; i++) coveredIv[i] = new List<(double, double)>();
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
                double len = ov;
                if (_clip != null)
                {   // 2026-09-18 Fable 5.1：重叠段也按材料裁剪（重叠段的两端点已知）
                    var q0 = s.Vert ? new Vec3(s.Line, s.P0.Y, lo) : new Vec3(lo, s.P0.Y, s.Line);
                    var q1 = s.Vert ? new Vec3(s.Line, s.P0.Y, hi) : new Vec3(hi, s.P0.Y, s.Line);
                    (len, mid) = _clip(q0, q1);
                }
                if (len > EdgeTolMm)
                    Faces.Add(new MeshFace
                    {
                        A = s.Cell, B = t.Cell, Mid = mid, Length = len, FullLength = ov,
                        DistAB = (Centroid[t.Cell] - Centroid[s.Cell]).Norm,
                        Tag = TagInterior
                    });
                covered[i] += ov; covered[j] += ov;
                coveredIv[i].Add((lo, hi)); coveredIv[j].Add((lo, hi));
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
            if (rest <= EdgeTolMm) continue;
            if (_clip is null || covered[i] <= EdgeTolMm && coveredIv[i].Count == 0)
            {   // 老口径（不裁剪）：一条面、长度 = 残段长、中点 = 整边中点（逐位不变）；裁剪但整条边都没配上：整边按材料裁
                if (_clip is null) AddBoundary(s.Cell, s.P0, s.P1, boundaryTagger, rest);
                else AddBoundary(s.Cell, s.P0, s.P1, boundaryTagger);
                continue;
            }
            // 2026-09-18 Fable 5.1：裁剪 + 部分配上（悬挂节点）⇒ 残段按真实端点逐段建面，每段再按材料裁
            var civ = coveredIv[i].OrderBy(v => v.lo).ToList();
            double pos = s.Lo;
            Vec3 At(double v) => s.Vert ? new Vec3(s.Line, s.P0.Y, v) : new Vec3(v, s.P0.Y, s.Line);
            foreach (var (lo, hi) in civ)
            {
                if (lo - pos > EdgeTolMm) AddBoundary(s.Cell, At(pos), At(lo), boundaryTagger);
                pos = Math.Max(pos, hi);
            }
            if (s.Hi - pos > EdgeTolMm) AddBoundary(s.Cell, At(pos), At(s.Hi), boundaryTagger);
        }
    }

    private void AddBoundary(int cell, Vec3 p0, Vec3 p1,
                             Func<Vec3, int>? boundaryTagger, double? lengthOverride = null)
    {
        var mid = (p0 + p1) * 0.5;
        double length = lengthOverride ?? (p1 - p0).Norm;
        double full = length;
        if (_clip != null)
        {   // 2026-09-18 Fable 5.1：边界边同样按材料裁剪；没有料的边（落在孔里、板外的阶梯边）不建面。
            //   残段（lengthOverride，悬挂节点的几何配对剩下的那一截）的端点这里拿不到，与裁剪同时出现就当场炸，不许静默给一个错长度
            //   （张量积网格没有悬挂节点，两者不会同时出现；手造的非协调网格不传裁剪）。
            if (lengthOverride.HasValue)
                throw new InvalidOperationException("面拓扑：几何配对的残段与材料裁剪同时出现 —— 残段端点未知，裁不了。");
            (length, mid) = _clip(p0, p1);
            if (length <= EdgeTolMm) return;
        }
        var face = new MeshFace
        {
            A = cell, B = -1, Mid = mid,
            Length = length, FullLength = full,
            Tag = boundaryTagger?.Invoke(mid) ?? TagFree
        };
        face.DistAB = CentroidToFaceMm(cell, face);      // R48 F（2026-09-15 Opus 5）：距离取法收成一份（原式 (mid − 形心).Norm，逐位同）
        Faces.Add(face);
    }

    public (int interior, int boundary) FaceCounts()
        => (Faces.Count(f => f.B >= 0), Faces.Count(f => f.B < 0));

    // ── R47 F（2026-09-13）：管孔定温环的自检。**只量不判**——TagHole 的 3 mm 口径本工单不改。
    //   病：|r − 孔半径| < 3 mm 这条判定在盘 R28／孔 25.8（环宽 2.2 mm）的设计上把整段盘外缘与舌肩
    //   都钉成管孔（T = 管根、V = 0）。量出来写进判据「②′管孔净流入」的附注，让读的人看得见。

    /// <summary>生成器填：管孔半径 mm（NaN = 生成器没填）。</summary>
    public double HoleRadiusMm = double.NaN;
    /// <summary>2026-09-18 Fable 5.1：并入邻格的管孔外角薄片格数（<see cref="FlangeMesher.MergeSlivers"/> 规则 A；诊断）。</summary>
    public int HoleSliversMerged;
    /// <summary>2026-09-19 Fable 5.1：按重构条件数／碎格规则并入邻格的格数（<see cref="FlangeMesher.MergeSlivers"/> 规则 B；诊断）。</summary>
    public int SliversMerged;
    /// <summary>
    /// 2026-09-19 Fable 5.1：每个单元的**矩形列表**（并入过别的格的单元有多个矩形；没并过的一个）。<see cref="Cells"/> 里的节点四边形仍只是目标格自己那一个
    ///（非凸多边形建不了面、画图也画不了），要按料量东西（管孔弧面、保温分界份额 <see cref="FlangeMesher.MaterialFractionInCircle"/>）都读这里。
    /// null = 不是 FlangeMesher.BuildFromMaterial 生成的网格（手造、QuadMesher）⇒ 读节点矩形。
    /// </summary>
    public List<(double x0, double x1, double z0, double z1)>[]? CellRects;
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
/// ★★ R48（2026-09-15，Opus 5；常驻数值把关人第十三、十四轮）：一张网格**实际生效**的判定网格配方 —— 结构化指纹，不用字符串协议。
///
/// 为什么要它：证据文件与门此前只能靠「源码里有没有写 clampFullFace」「ClampAnchorNote 里有没有『铺细步』」这类字符串判断口径，
/// 生成器里一个退回分支（压接段盖到管孔 ⇒ 不填整面接触）或一个没落成节点的锚点，字符串门都看不见。
/// ★ 2026-09-15 Opus 5（审查意见 minor：原注释说「每个量都是从网格本身量的」，不实）—— 分两类，如实写：
/// 【从建好的网格上量的】
///   · <see cref="ClampFullFace"/>：<see cref="ShellMesh.ClampCell"/> 真的填了（长度 = 单元数、至少一格为真）；热解配方的同名项用同一个定义（ShellThermal.RecipeFor），
///     热解里另由「真的并进压接格的格数 &gt; 0」量一遍；
///   · <see cref="ClampCoversHole"/>：压接段盖到管孔、整面接触退回只钉外圈（<see cref="ShellMesh.ClampCoversHole"/>：带管孔标签的边界面所在格的形心落在压接段内）；
///   · <see cref="ClampAnchorOnNode"/>：每个压接边界 x（单舌一个、双舌两个）都在建好的 x 节点里（容差 1e-9 mm）；
///   · <see cref="ClampFaceDirichlet"/>（2026-09-15 Opus 5（合并）补 F 的配方 ⑤）：压接边界**施加在压接面上**（true）还是按压接格**形心整格**（false）——
///     量法 = 网格上的压接面数 <see cref="ClampFaceCount"/> = <see cref="ShellMesh.ClampFaceCount"/> &gt; 0（开关 <see cref="ShellMesh.ClampFaceDirichlet"/> 开且整面接触生效时，
///     按两个求解器施加面上边界值的同一个集合与判定数出来；开关关、只钉外圈、压接盖孔退回时都是 0 ⇒ 形心整格）。
/// 【生效参数的记录（不是从节点间距量的）】
///   · <see cref="ClampBandMm"/>：压接边界落成节点后加进细分带的那个单侧宽度 mm（有一个边界没落成节点、或显式不铺 = 0）；<see cref="ClampBandPerHFine"/> = 它 ÷ hFine（取 9 位小数，免得 3·h÷h 的浮点尾巴让规则比不上）；
///     没从节点间距量：分级轴（GradedAxisCentered）在带的入口按增长率收步长、锚点那一格允许拉长到想要步长的 1.25 倍，带入口与锚点处的格可以比 hFine 长，
///     「步长 ≤ hFine 的范围」量出来与参数对不齐，要比就得临时编一个容差 —— 那是看了数据再定门槛，不做；
///   · <see cref="HFineMm"/>：细步参数 mm；
///   · <see cref="HoleTagBandMm"/>：孔边判定带半宽 mm（生效值：传 ≤ 0 时是 <see cref="FlangeMesher.HoleTagBandMm"/>）。
/// </summary>
public sealed class MeshRecipe
{
    public bool ClampFullFace { get; init; }
    public bool ClampCoversHole { get; init; }
    public bool ClampAnchorOnNode { get; init; }
    /// <summary>2026-09-15 Opus 5（合并）：压接边界施加在压接面上（F 配方 ⑤，生产）；false = 形心整格。见类注释。</summary>
    public bool ClampFaceDirichlet { get; init; }
    /// <summary>2026-09-15 Opus 5（合并）：网格上的压接面数（与网格尺寸有关，不进规则；规则只看 <see cref="ClampFaceDirichlet"/>）。</summary>
    public int ClampFaceCount { get; init; }
    public double ClampBandMm { get; init; } = double.NaN;
    public double HFineMm { get; init; } = double.NaN;
    public double ClampBandPerHFine { get; init; } = double.NaN;
    public double HoleTagBandMm { get; init; } = double.NaN;

    /// <summary>与网格尺寸无关的那部分（规则）—— 生产配方常量就是这个类型，门用 == 比。</summary>
    public MeshRecipeRule Rule => new(ClampFullFace, ClampFaceDirichlet, ClampAnchorOnNode, ClampBandPerHFine, HoleTagBandMm);

    /// <summary>一行文字（证据文件头与探针打印用）。</summary>
    public string Describe()
        => $"压接整面接触 {(ClampFullFace ? "生效" : "未生效")}{(ClampCoversHole ? "（压接段盖到管孔，退回只钉外圈）" : "")}"
         + $"；压接边界{(ClampFaceDirichlet ? $"施加在压接面上（{ClampFaceCount} 个面）" : "按压接格形心整格")}"   // 2026-09-15 Opus 5（合并）：F 配方 ⑤
         + $"；压接边界落成节点 {(ClampAnchorOnNode ? "是" : "否")}"
         + $"；压接细带单侧 {ClampBandMm:0.###} mm（= {ClampBandPerHFine:0.###} × hFine {HFineMm:0.###} mm）"
         + $"；孔边判定带半宽 {HoleTagBandMm:0.###} mm";
}

/// <summary>R48（2026-09-15，Opus 5）：判定网格配方里与网格尺寸无关的规则部分（<see cref="MeshRecipe.Rule"/>）。</summary>
/// <remarks>2026-09-15 Opus 5（合并）：加 <see cref="ClampFaceDirichlet"/>（F 配方 ⑤ 压接面上定温 vs 形心整格）。</remarks>
public readonly record struct MeshRecipeRule(bool ClampFullFace, bool ClampFaceDirichlet, bool ClampAnchorOnNode, double ClampBandPerHFine, double HoleTagBandMm)
{
    public string Describe()
        => $"压接整面接触 {(ClampFullFace ? "开" : "关")}；压接边界{(ClampFaceDirichlet ? "施加在压接面上" : "按压接格形心整格")}；压接边界落成节点 {(ClampAnchorOnNode ? "是" : "否")}；"
         + $"压接细带单侧 {ClampBandPerHFine:0.###} × hFine；孔边判定带半宽 {HoleTagBandMm:0.###} mm";
}

/// <summary>
/// ★★ 2026-09-19，Fable 5.1（网格修复第二轮复核第 9 条）：**网格生成层的规则组合** —— 生产入口（<see cref="FlangeMesher.Build"/>／
/// <see cref="FlangeMesher.BuildFromField"/>／<see cref="FlangeMesher.BuildFromMaterial"/>）一律走 <see cref="Production"/>，别的组合只有门经 InternalsVisibleTo
/// 传给 <see cref="FlangeMesher.BuildFromMaterialWith"/> 做「改回 ⇒ 红」的对照。类是 internal、没有全局状态：此前的 [ThreadStatic] 开关
/// InjectCenterSampling 放在生产码里（生产码读得到、xUnit 并行时还串过味），已删。「格心取样」那半边注入不在这里 —— 那是材料来源的事，
/// 测试侧自己给一个 IMaterialField 实现，生成器不认识它。
/// </summary>
internal sealed class MeshRules
{
    /// <summary>F2：面长按材料裁剪（false = 整边面长，老口径）。</summary>
    public bool ClipFaces { get; init; } = true;
    /// <summary>管孔边界 = 圆弧本身（false = 阶梯孔边，老口径）。</summary>
    public bool HoleArcFaces { get; init; } = true;
    /// <summary>规则 A：管孔外角薄片格并入邻格（2026-09-18）。</summary>
    public bool MergeHoleCorners { get; init; } = true;
    /// <summary>规则 B：碎格（覆盖率 &lt; <see cref="CellMergeFrac"/>）并入邻格（2026-09-19）。楔形格的幻影 J 不在网格层治，见 ShellCurrent.SliverKappaMin。</summary>
    public bool MergeSlivers { get; init; } = true;
    /// <summary>规则 B 的碎格门槛（生产 = <see cref="FlangeMesher.CellMergeFrac"/>）。</summary>
    public double CellMergeFrac { get; init; } = FlangeMesher.CellMergeFrac;
    /// <summary>F5：轴端余数规则（计划节点之后到轴端不足 hWant/4 就拉到轴端）。</summary>
    public bool AxisEndRule { get; init; } = true;

    /// <summary>生产规则：全开、门槛取常量。</summary>
    public static readonly MeshRules Production = new();
}

/// <summary>
/// 厚度场 t(x, z) —— 由 <c>Pt_Optimize.Geom thickness</c> 从 .3dm 逐点射线量出。
///
/// **t = 0 表示该点无材料**，于是轮廓外、管孔、开槽三者统一用同一个判据表达；
/// t > 0 直接给出阶梯厚度。求解器要的本来就是 t(x,z)，故不必提取轮廓环 ——
/// 用户在 Rhino 里画什么形状，这里就照单全收，无需参数化、无需改代码。
/// </summary>
public sealed class ThicknessField : IMaterialField
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

    // ── 2026-09-18，Fable 5.1：栅格厚度场作为 IMaterialField（图纸路径）。量法同 R47：每个栅格节点代表以它为心、边长 = 步的方格，
    //    节点值就是那一小块的厚度；单元积分 = Σ 方格与单元的重叠面积（× 厚度）。这条路仍是点采样，边的位置只准到 ±步/2（图纸路径未量，见 HANDOVER「网格生成 2026-09-18」）。

    public MaterialIntegral Integrate(double x0, double x1, double z0, double z1)
    {
        var ovX = FlangeMesher.RasterOverlap(x0, x1, X0, Nx, Step);
        var ovZ = FlangeMesher.RasterOverlap(z0, z1, Z0, Nz, Step);
        double half = 0.5 * Step, A = 0, V = 0, Mx = 0, Mz = 0;
        foreach (var (ix, ox) in ovX)
        {
            double cx = X0 + ix * Step;
            double xm = 0.5 * (Math.Max(x0, cx - half) + Math.Min(x1, cx + half));   // 重叠矩形的 x 中心
            foreach (var (iz, oz) in ovZ)
            {
                double t = T[ix * Nz + iz];
                if (t <= 1e-9) continue;
                double cz = Z0 + iz * Step;
                double zm = 0.5 * (Math.Max(z0, cz - half) + Math.Min(z1, cz + half));
                double a = ox * oz;
                A += a; V += a * t; Mx += a * xm; Mz += a * zm;
            }
        }
        return new MaterialIntegral(A, V, Mx, Mz);
    }

    /// <summary>栅格上一条轴对齐线段的有料长度：线落在哪一列（行）方格里就数那一列（行）；恰在两列方格的公共边上取两侧平均。</summary>
    public (double Length, double Mid) SegmentMaterial(bool vertical, double line, double a, double b)
    {
        if (b < a) (a, b) = (b, a);
        double origin = vertical ? X0 : Z0, originT = vertical ? Z0 : X0;
        int count = vertical ? Nx : Nz, countT = vertical ? Nz : Nx;
        double u = (line - origin) / Step;                 // 以方格中心为整数
        double frac = u - Math.Floor(u);
        var cols = new List<(int k, double wgt)>();
        if (Math.Abs(frac - 0.5) < 1e-9) { cols.Add(((int)Math.Floor(u), 0.5)); cols.Add(((int)Math.Floor(u) + 1, 0.5)); }
        else cols.Add(((int)Math.Round(u), 1.0));
        double len = 0, mom = 0;
        foreach (var (k, wgt) in cols)
        {
            if (k < 0 || k >= count) continue;
            foreach (var (j, ov) in FlangeMesher.RasterOverlap(a, b, originT, countT, Step))
            {
                double t = vertical ? T[k * Nz + j] : T[j * Nz + k];
                if (t <= 1e-9) continue;
                double cj = originT + j * Step;
                double lo = Math.Max(a, cj - 0.5 * Step), hi = Math.Min(b, cj + 0.5 * Step);
                len += wgt * ov; mom += wgt * ov * 0.5 * (lo + hi);
            }
        }
        return (len, len > 0 ? mom / len : 0.5 * (a + b));
    }

    public double FractionInsideCircle(double x0, double x1, double z0, double z1, double radiusMm)
    {
        var ovX = FlangeMesher.RasterOverlap(x0, x1, X0, Nx, Step);
        var ovZ = FlangeMesher.RasterOverlap(z0, z1, Z0, Nz, Step);
        double all = 0, inA = 0;
        foreach (var (ix, ox) in ovX)
            foreach (var (iz, oz) in ovZ)
            {
                if (T[ix * Nz + iz] <= 1e-9) continue;
                double a = ox * oz;
                all += a;
                if (FlangePlate.InsideInsulCircle(X0 + ix * Step, Z0 + iz * Step, radiusMm)) inA += a;
            }
        return all > 0 ? inA / all : double.NaN;
    }

    public string Describe() => $"栅格厚度场（步 {Step:0.###} mm，{Nx}×{Nz}，方格面积积分；边的位置只准到 ±步/2）";
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
        => GradedAxisCenteredWith(min, max, bands, hCoarse, growth, center, anchors, endRule: true);

    /// <summary>
    /// 2026-09-19，Fable 5.1（第二轮复核第 6 条）：本体带 F5 轴端余数规则的开关 —— 生产恒 true（上面的公开入口写死），门传 false 证明「去掉规则 ⇒ 末格长度门红」。
    /// </summary>
    internal static double[] GradedAxisCenteredWith(double min, double max,
                                                    IReadOnlyList<Band> bands, double hCoarse,
                                                    double growth, double center,
                                                    IReadOnlyList<double>? anchors, bool endRule)
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
            // ★ 2026-09-18，Fable 5.1（F5 的另一半）：离轴端不足当地步长 1/4 的锚点**不落**（轴端赢）——
            //   盘半径 30.01／舌半宽 30 时锚点 ±30 与轴端 ±30.01 只差 0.01 mm，两者都落成节点就是一排 0.01 mm 的发丝格（长宽比 200）。
            //   锚点不落不会把料算错：单元积分与面长都按材料精确裁剪（F1／F2），锚点只管把直边放到节点上省掉部分格。
            var sAnc = anc.Where(a => dir > 0 ? a > c0 : a < c0).Select(a => Math.Abs(a - c0))
                          .Where(v => len - v >= 0.25 * HAt(c0 + dir * v) - 1e-9)
                          .OrderBy(v => v).ToList();
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
                // ★ 2026-09-18，Fable 5.1（网格生成根因修复 F5）：**轴端也按锚点的规则处理** —— 计划节点 s+h 之后到轴端只剩不到 hWant/4
                //   （且中间没有别的锚点）时，把这一格直接拉到轴端（最长 1.25·hWant），不再留一格发丝格。
                //   病：N3 只护锚点不护轴端，盘半径 30.05 时 z 轴末格 0.05 mm、长宽比 220（网格审计_2 盘径扫描，2026-09-18）。
                {
                    double rem = len - (s + h);
                    bool anchorAhead = ia < sAnc.Count && sAnc[ia] < len - 1e-9;
                    if (endRule && rem > 1e-9 && rem < 0.25 * hWant - 1e-9 && !anchorAhead) h = len - s;
                }
                // 锚点贴节点：锚点落在这一格里 ⇒ 把这一格收到锚点上。
                // ★ R47 第三轮 N3（2026-09-13）：**不造发丝格** —— 锚点落在计划节点 s+h 之后、但离它不到 hWant/4 时，
                //   原来是先放 s+h 这个节点、下一格再收到锚点 ⇒ 一格 0.02 mm 的发丝格（实测锚点 2.02／−16.005）。
                //   单元长宽比几百，电流场的刚度阵条件数跟着炸。改成把这个计划节点**挪到锚点上**（这一格最多 1.25·hWant，
                //   不插新节点）；锚点落在计划节点之前的情形照旧收短（最短约 0.25·hWant）。
                //   （2026-09-14 Opus 5 更正：此处原写「最短 0.75·hWant」不对，实际约 0.25·hWant —— 数值把关人第六轮查出，只是注释错。）
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

    /// <param name="bandMm">判定带半宽 mm。≤ 0 ⇒ 用固定的 <see cref="HoleTagBandMm"/>（口径不变，老调用方逐位相同）。
    /// ★ R48 在查（Opus 5）：带宽写死 3 mm 而**被钉成管温的是「有孔边界面的那一整格」** ——
    ///   网格 2 mm 时孔边只钉住一圈（物理厚度约 2 mm），网格 0.5 mm 时 3 mm 带里有六圈格全被钉住（厚度约 3 mm）。
    ///   定温区域的物理尺寸随网格变 ⇒ 从管子抽的热跟着变，这正是抽热不随加密收敛的嫌疑来源。
    ///   传一个随网格走的带宽（例如 1.5×孔周格尺寸）就只钉紧贴孔的那一圈，物理厚度随加密趋于零（= 真实的孔边界线）。</param>
    public static bool IsHoleFace(Vec3 mid, double holeRadiusMm, double bandMm = 0)
    {
        double r = Math.Sqrt(mid.X * mid.X + mid.Z * mid.Z);
        return Math.Abs(r - holeRadiusMm) < (bandMm > 0 ? bandMm : HoleTagBandMm);
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：**x 在不在铜排压接段内** —— 单舌：x ≤ 舌尖 + 压接长；双舌：|x| ≥ |舌尖| − 压接长（两端都是压接段）。
    /// 与管孔判定 <see cref="IsHoleFace"/> 同样**收成一份**：此前同一个式子写在三处（BuildFromField 的边界面标签、
    /// BuildFromField 的整面接触格、QuadMesher 的边界面标签），式子逐字搬过来，结果逐位不变。
    /// 边界面标签传面中点 x，整面接触格（<see cref="ShellMesh.ClampCell"/>）传格子形心 x。
    /// </summary>
    public static bool InClampSegment(double x, double tabTipX, double clampLenMm, bool twoTabs)
        => twoTabs ? Math.Abs(x) >= Math.Abs(tabTipX) - clampLenMm : x <= tabTipX + clampLenMm;

    /// <summary>
    /// ★★ R48 生产配方（2026-09-14，Opus 5；数值把关人第十轮定的判读）：**压接细带自相似** —— 压接边界两侧各铺
    /// <c>ClampBandPerHFine × hFine</c> 的细步（步长 = hFine），带宽随网格一起缩，每侧恒为 3 格。
    /// 依据 deliverable/R48_压接细带自相似_2026-09-14.txt（误差预算工作点，四片 × h = 1／0.5／0.25）：
    ///   自相似 3·h 与均匀网格之差三档都 ≤ 0.028 W、收敛比值差 ≤ 0.005（门槛 0.05 W／0.02）；
    ///   固定 3 mm 细带比值 0.459～0.464 而均匀 0.482～0.491（不是自相似，h=2 时每侧只剩 1.5 格）⇒ 不用固定宽度。
    ///   ⚠ 口径（2026-09-14 Opus 5 复审补）：上面这组数是**只钉外圈**口径下量的（那时整面接触还只是实验开关）。
    ///   整面接触 + 自相似细带的组合另在 deliverable/R48_组合配方收敛_2026-09-14.txt 验过：四片三档与整面接触均匀网格之差 ≤ 0.011 W；
    ///   但整面均匀网格本身步长不单调、不在渐近区，按比值外推的误差量不能从外圈口径照搬（见 BuildFromField 配方声明 ④）。
    /// 为什么要细带（deliverable/R48_离散误差预算_2026-09-14.txt）：分级网格的压接边界落在远场粗格里，不铺细带时
    ///   分级比均匀抽热偏低（片0 −1.893／−0.957／−0.490 W，h = 1／0.5／0.25，随 h 减半）；铺细带（该文件用的是固定 3 mm）后与均匀之差四片三档都 ≤ 0.022 W。
    ///
    /// ★★ R48 F（2026-09-15，Opus 5）：**生产缺省改为不铺细带：3.0 → 0.0**（显式传正数照旧铺，老探针原义不变）。
    ///   上面那组依据全是**只钉外圈**或**形心整格**口径下量的：外圈口径下压接内边是内部面、与绝热侧边交出 r^(1/2) 奇点，形心口径下定温位置随压接格宽偏 h/2，
    ///   两者都让「压接边界处的粗格」带一阶误差，细带是在治这个。整面接触 + 面上定温（配方 ③ + ⑤）后内边与侧边交角 90°、解正则，边界落在面上，
    ///   病根没了 ⇒ 数值把关人要求单独重验，跑前写死「两片三档『分级不带细带 − 均匀』抽热都 ≤ 0.05 W ⇒ 改默认为不带」。
    ///   实测 deliverable/R48_压接面上定温_细带去留_2026-09-15.txt（第二批 B2 工作点，片0／片1 × h = 1／0.5／0.25，面上定温）：
    ///     分级不带细带 − 均匀：片0 −0.0045／+0.0006／+0.0001 W，片1 −0.0026／+0.0026／+0.0006 W（最大 0.0045 W，门槛的 1/11）；
    ///     分级 + 自相似细带 − 均匀：片0 −0.0057／−0.0015／−0.0002 W，片1 −0.0077／−0.0018／−0.0002 W —— 带细带并不更近；
    ///     格数 h = 1／0.5／0.25：不带 4122／15646／61394，带 4722／16846／63794（省 13 %／7 %／4 %）。
    ///   同一口径的收敛阶见 deliverable/R48_压接面上定温_收敛阶_2026-09-15.txt。
    ///   ⚠ 覆盖范围（2026-09-15 Opus 5 复审补，审查意见 minor）：「交角 90°、解正则」只对**平行边**舌片成立，上面的实测也只在 B2 直舌上。
    ///     锥形舌（DesignSpec.TabTaper）另验 deliverable/R48_压接面上定温_锥形舌细带去留_2026-09-15.txt（B2 改锥形，舌端半宽 15／10 mm ⇒ 锥角 6.2°／8.3°，h = 1／0.5）：
    ///     分级不带细带 − 均匀 −2.01～+0.09 W，超门槛；但分级 + 3·hFine 细带 − 均匀 同样 −0.58～−1.98 W，八行没有一行进 0.05 W ⇒ 铺细带治不了，
    ///     差距在锥形舌的分级网格本身（推测是远场粗格里的斜边，未验）。缺省照旧不铺，锥形舌分级网格的误差另开待办。
    ///   ⚠ 误差符号（同日复审补）：本配方下抽热随 h 从高处降下来，导航网格偏高约 +0.9～+1.1 W（B2 片0／片1，外推），A 路（形心 + 细带）是偏低 −0.70 W（片0）——
    ///     详见 BuildFromField 配方声明 ⑤ 末尾。
    ///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：出处层次 —— R48_压接面上定温_* 各份（细带去留、锥形舌细带去留、收敛阶、正偏差定位）都是在 r48_F 工作树上跑的（底板 f206e70），
    ///     那时法兰表面散热表上限还是「设定 + 200 K、60 节点」（合并树已改为铂熔点上限，见 ShellThermal.LossTableHiC）。细带去留比的是同一张表下两种网格之差，
    ///     表换了差值未必不变 —— 合并树上已按原判读原门槛重跑（deliverable/R48_压接面上定温_细带去留_2026-09-15_本次开跑于2026-09-15_1536.txt；2026-09-15 Opus 5（I 路） 核：头上的 Core 改动指纹 a28c71e0… 是 15:36 那一刻的树，与合并树快照（r48_M，树 4b25fe20）重算的 6a6c9848… 对不上 —— 把当前 ShellMesh.cs 里 15:41 合并复审改的两行注释（本段与配方声明 ④ 各一句）改回原文，按 EvidenceHeader.CoreDiffSha1 重算恰得 a28c71e0b52406666f2887a72e3852cea959ef16，即那次跑的代码与快照只差这两行注释、数不受影响；头上「散热表上限 1768.2 °C」印的是常量 ShellThermal.LossTableHiC，不是本跑表里读出的）：两片三档「分级不带细带 − 均匀」片0 −0.0045／+0.0006／+0.0001 W、片1 −0.0026／+0.0026／+0.0006 W，「分级 + 细带 − 均匀」与格数也与上面所引四位小数逐个相同 ⇒ 判读仍成立（≤ 0.05 W）；锥形舌细带去留、收敛阶、正偏差定位没有重跑。首跑文件在 r48_F 工作树，已原样拷入本树 deliverable（2026-09-15 Opus 5（I 路） 核：deliverable/R48_合并拷入证据清单_2026-09-15.txt，正偏差定位 04:45 首跑那份第三批补拷），原句「合并树里没有」作废。
    ///     导航网格偏差的符号（上一条）同样是 r48_F 那份配方下的，与 A 路（形心 + 细带）记录的符号相反，两者不许拼在一起读。
    /// </summary>
    public const double ClampBandPerHFine = 0.0;

    /// <summary>
    /// 压接细带单侧宽度 mm 的解析：<paramref name="clampBandMm"/> 为 NaN（缺省）⇒ 生产配方 <see cref="ClampBandPerHFine"/> × hFine（R48 F 2026-09-15 起 = 0，不铺）；
    /// 显式给数就照给的（0 或负 = 不铺，老探针「固定 3 mm」「3·h」等原义不变）。
    /// 公开出来是为了门调生产的这一份，不在测试里手抄 3×hFine（R48 2026-09-14 Opus 5）。
    /// </summary>
    public static double ResolveClampBandMm(double clampBandMm, double hFine)
        => double.IsNaN(clampBandMm) ? ClampBandPerHFine * hFine : clampBandMm;

    /// <summary>
    /// ★★ R48 生产网格配方常量（2026-09-15，Opus 5；数值把关人第十三轮）：生产链上每一片网格的 <see cref="MeshRecipe.Rule"/> 都必须等于它。
    /// 四项就是配方声明（见 <see cref="BuildFromField"/> 的注释）里与网格尺寸无关的规则：③ 压接整面接触生效、① 压接边界落成节点、
    /// ④ 压接细带单侧 <see cref="ClampBandPerHFine"/> × hFine、孔边判定带半宽 <see cref="HoleTagBandMm"/>。
    /// 门（R48RecipeFingerprintTests）拿 LineRunner.Run 算出来的每片网格与它比 —— 行为门，取代「LineRunner 源码里没写 clampFullFace」那道字符串门的主证据地位。
    /// ★ 2026-09-15 Opus 5（合并）：G1 写于 F 之前，这里原为四项、细带那项当时是 3 × hFine。合并后按**合并后的真实生产配方**记五项：
    ///   加 ⑤ 压接边界施加在压接面上（F：<see cref="ShellMesh.ClampFaceDirichlet"/> 缺省 true，生成器参数 clampFaceDirichlet 缺省 true）；
    ///   ④ 细带引用常量 <see cref="ClampBandPerHFine"/>，F 已把它改为 0（依据见该常量注释 deliverable/R48_压接面上定温_细带去留_2026-09-15.txt），这里跟着就是 0 × hFine。
    /// </summary>
    public static readonly MeshRecipeRule ProductionMeshRule = new(
        ClampFullFace: true, ClampFaceDirichlet: true, ClampAnchorOnNode: true, ClampBandPerHFine: ClampBandPerHFine, HoleTagBandMm: HoleTagBandMm);

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
    /// <param name="clampBandMm">压接细带单侧宽度 mm。缺省 NaN = 生产配方（两侧各 <see cref="ClampBandPerHFine"/>×hFine）；
    /// 显式 0 = 不铺。原样转给 <see cref="BuildFromField"/>（R48 2026-09-14 Opus 5：默认由 0 改为 NaN，见那里的配方声明）。</param>
    /// <param name="clampFullFace">压接段整面接触，缺省 true = 生产配方；显式 false = 老口径只钉外圈。原样转给 <see cref="BuildFromField"/>
    /// （R48 2026-09-14 Opus 5：此前 Build 没有这个参数，整面接触只能走 BuildFromField 打开，两条入口口径不一）。</param>
    /// <param name="clampFaceDirichlet">压接边界施加在面上（配方 ⑤），缺省 true = 生产配方；显式 false = 形心整格口径。原样转给 <see cref="BuildFromField"/>（R48 F 2026-09-15 Opus 5）。</param>
    public static ShellMesh Build(FlangePlate g, double yPlane = 0,
                                  double hFine = 2.0, double hCoarse = 11.0,
                                  double fineRadius = 45.0, double clampLenMm = 3.0,
                                  double hInner = 0, double innerRadius = 0,
                                  double clampBandMm = double.NaN, bool clampFullFace = true, bool clampFaceDirichlet = true)
    {
        // ★★★ 2026-09-18，Fable 5.1（网格生成根因修复 F1）：解析板**不再栅格化**。R47 的「解析路径 = 栅格化 + 图纸路径」把格点点采样带进了生产路径：
        //   舌片直边 z = ±w 落在格点上时闭区间 Inside 把边上那排格点判成有料 ⇒ 两侧各半个栅格步的幻影料（三档栅格步都对齐 w = 30，三档一致地错）；
        //   M1 把 ±w 锚成节点后幻影集中成一排「带格」，覆盖率恰卡在 25 % 丢弃线上 ⇒ 盘径／舌半宽扫描上带格随几何翻面、三条判据跳 19～26 K。
        //   现在解析板经 AnalyticMaterial 逐列精确积分（没有采样点、没有平局、没有幻影），图纸路径（栅格）与它走同一份 BuildFromMaterial。
        //   同一解析板 Build 与 BuildFromField(Rasterize) **不再逐位相同**（一个精确、一个是栅格近似），两条路对得上改由容差门守（DrawingPathParityTests）。
        return BuildWith(g, MeshRules.Production, null, yPlane, hFine, hCoarse, fineRadius, clampLenMm, hInner, innerRadius, clampBandMm, clampFullFace, clampFaceDirichlet);
    }

    /// <summary>
    /// ★ 2026-09-19，Fable 5.1（第二轮复核第 9 条）：<see cref="Build"/> 的本体，多两个只给门用的口：网格层规则 <paramref name="rules"/>（null = 生产）与
    /// 可替换的材料源 <paramref name="material"/>（null = 解析板精确积分 <see cref="AnalyticMaterial"/>；门传测试侧的格心取样材料做注入对照）。
    /// 锚点、孔半径、双舌等一律照 Build 的推法给，这里不另抄一份配方。
    /// </summary>
    internal static ShellMesh BuildWith(FlangePlate g, MeshRules? rules, IMaterialField? material, double yPlane = 0,
                                        double hFine = 2.0, double hCoarse = 11.0,
                                        double fineRadius = 45.0, double clampLenMm = 3.0,
                                        double hInner = 0, double innerRadius = 0,
                                        double clampBandMm = double.NaN, bool clampFullFace = true, bool clampFaceDirichlet = true)
    {
        var (xa, za) = AnchorsOf(g);
        return BuildFromMaterialWith(material ?? new AnalyticMaterial(g), g.HoleRadiusMm, rules, yPlane, hFine, hCoarse, fineRadius, clampLenMm,
                                     hInner, innerRadius, twoTabs: g.TwoTabs, xAnchors: xa, zAnchors: za,
                                     clampBandMm: clampBandMm, clampFullFace: clampFullFace, clampFaceDirichlet: clampFaceDirichlet);
    }

    /// <summary>
    /// ★ 2026-09-18，Fable 5.1（F2）：留格的门槛 —— 有料面积 &gt; 这个比例 × 整格面积就留（老口径是 25 %：丢掉的是真料，导航 −0.10～−0.30 %，
    /// 而且它把带格变成随几何翻面的开关）。1e-9 只挡浮点尘埃；薄片格的导电／导热截面由裁剪过的面长给，不靠丢格。
    /// 2026-09-19 Fable 5.1（第二轮复核第 10 条）：留下来的格里覆盖率 &lt; <see cref="CellMergeFrac"/> 的**并入邻格**（料不丢、不再单独当一个未知量），见 <see cref="MergeSlivers"/>。
    /// （2026-09-18 的 [ThreadStatic] 注入开关 InjectCenterSampling 原在这里，2026-09-19 按第二轮复核第 9 条删掉：注入改走 <see cref="MeshRules"/> + 测试侧材料源。）
    /// </summary>
    public const double CellKeepFrac = 1e-9;

    /// <summary>
    /// ★★ 2026-09-19，Fable 5.1（第二轮复核第 1／10 条）：**并入邻格的门槛之一 —— 碎格**：覆盖率（有料面积 ÷ 整格面积）低于它的格并入共有最长有料边的邻格。
    /// 出处：本套件最细的判读门槛是体积守恒 0.1 %／连续性 0.2 %（R48NMeshGateTests）；一个格里不到 0.1 % 格面积的料，无论算成独立未知量还是并进邻格，
    /// 任何门都看不见（并前并后差 ≤ 一格的 1e-3，比门槛小三个量级）；而它作独立未知量时导度只有 1e-3 格量级，只给方程组添一个几乎悬空的未知数
    /// （第一轮实测：覆盖率 5e-7、面积 2e-6 mm² 的格也留成了单元）。
    /// </summary>
    public const double CellMergeFrac = 1e-3;

    // ★ 2026-09-19，Fable 5.1（第二轮复核第 1 条）：楔形格的幻影 J 峰**不在网格层治**（试过按重构条件数 κ′ < 0.2 并格：Heater1 的幻影峰没了，
    //   但管孔边的真峰跟着被合成格抹掉 5～10 %，R48NSliverGateTests 门 2 红），治在 J 的重构量法上 —— 见 ShellCurrent.SliverKappaMin
    //   （角度条件数不足的格只沿强方向重构）。网格层只并碎格（CellMergeFrac）。

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
    ///
    /// ══ 判定网格配方声明（2026-09-14，Opus 5；2026-09-15 Opus 5 改 ④、加 ⑤）—— 下面各条都是生产默认，解析与图纸两条路径同一份：
    ///   ① 压接锚点：x = 舌尖 + 压接长 落成网格节点（记进 <see cref="ShellMesh.ClampAnchorNote"/>）。依据 deliverable/R48_实验a_压接段相位_2026-09-14.txt
    ///      （抽热离群与压接段相位同 R、偏移符号与抽热偏差符号九行全部一致）与 R48_实验b_压接边界落节点_2026-09-14.txt（落节点后四片边偏移全为 0）。
    ///   ② 分界格混合：保温分界圆穿过的格子按有料面积份额混合两种保温的散热（份额量法在 <see cref="MaterialFraction"/>，混合在 ShellThermal）。
    ///      依据 R48_实验c_温度场台阶抖动_2026-09-14.txt（形心归边时抽热随分界平移跳 0.40～0.90 W）、R48_保温分界混合_门六_2026-09-14.txt
    ///      （h=0.5 外侧单侧混合残差 ≤ 0.1 W）与 R48_保温分界混合_门七_2026-09-14.txt（h=1 没看过的片1、片3 混合残差 ≤ 形心的 1/4）。
    ///   ③ 压接整面接触：形心落在压接段内的格子全部作电极与压接格（<paramref name="clampFullFace"/> 缺省 true，见 <see cref="ShellMesh.ClampCell"/>）。
    ///      依据 R48_压接整面接触AB_2026-09-14.txt（整面 − 外圈：片0 抽热 +16.4～+17.6 W、片1 +32.1～+34.3 W，三档加密稳定）。
    ///      唯一例外：压接段盖到了管孔（退化几何，没有舌片的图纸）⇒ 不填、退回只钉外圈，⚠ 写进 ClampAnchorNote，LineRunner 接进输出。
    ///   ④ 压接细带：**2026-09-15 起生产缺省不铺**（<see cref="ClampBandPerHFine"/> 3.0 → 0.0，<paramref name="clampBandMm"/> 缺省 NaN 解析为 0）。
    ///      依据 R48_压接面上定温_细带去留_2026-09-15.txt（整面接触 + 面上定温，第二批 B2 工作点片0／片1 × h = 1／0.5／0.25：
    ///      「分级不带细带 − 均匀」抽热最大 0.0045 W，跑前写死的门槛 0.05 W；带细带并不更近）。
    ///      ⚠ 2026-09-15 Opus 5（合并，复审后改）：这份依据在 r48_F 工作树上跑（底板 f206e70，散热表上限「设定 + 200 K、60 节点」，合并树已改铂熔点上限）；合并树上按原判读重跑，差值四位小数逐个相同、判读仍成立，见 ClampBandPerHFine 注释。
    ///      ⚠ 这条依据只覆盖**平行边**舌片（2026-09-15 Opus 5 复审补）：锥形舌 R48_压接面上定温_锥形舌细带去留_2026-09-15.txt 上「分级不带细带 − 均匀」−2.01～+0.09 W 超门槛，
    ///        而「分级 + 细带 − 均匀」同样 −0.58～−1.98 W、没有一行进门槛 ⇒ 不是细带治得了的，缺省不改，锥形舌分级网格的误差另开待办（见 ClampBandPerHFine）。
    ///      下面是 2026-09-14 铺自相似细带时的依据，留作历史：
    ///      自相似压接细带：压接边界落成节点时，两侧各 3×hFine 铺细步 hFine。
    ///      依据 R48_压接细带自相似_2026-09-14.txt（**只钉外圈口径下量的**：与均匀网格差 ≤ 0.028 W、收敛比值差 ≤ 0.005；固定 3 mm 不是自相似，比值偏 0.02～0.03）。
    ///      组合口径（③ 整面接触 + ④ 自相似细带 + 分级网格）另验（2026-09-14 Opus 5 复审补）：R48_组合配方收敛_2026-09-14.txt ——
    ///      误差预算工作点四片 × h = 1／0.5／0.25，与整面接触均匀网格之差全 ≤ 0.011 W（片0 −0.0064／−0.0018／−0.0002 W，最大是片1 h=1 的 −0.0107 W）。
    ///      ⚠ 同一文件：整面接触均匀网格自己的抽热步长四片都不在渐近区（比值 −6.7／−3.0／1.47／0.95，片0 −0.0098 → +0.0652 W），
    ///        R48_离散误差预算 里按只钉外圈的收敛比值算的理查森剩余 U **不能照搬**到整面口径；
    ///        网格相对压接边界的位置也不随 h 自相似（压接边界自由侧那一格 0.686／0.723／1.230／1.146·hFine，h = 导航 2／1／0.5／0.25）。
    ///   只在压接边界真落成节点时铺细带（离端点或已有锚点不足一个最细格就不落、也不铺，都写进 ClampAnchorNote）。
    ///   ⑤ 压接面上定温（R48 F 2026-09-15 Opus 5）：整面接触时压接边界的电位 1 与夹持温度施加在压接面上，不在压接格形心（<paramref name="clampFaceDirichlet"/> 缺省 true，
    ///      见 <see cref="ShellMesh.ClampFaceDirichlet"/>）。依据：形心口径的等效边界往压接区里偏 h/2（一阶误差，物理把关人 2026-09-14 晚）；
    ///      快门 R48ClampFaceGateTests（一维条带。电流条带以管孔格形心为 V = 0 —— ShellCurrent 的管孔仍在格上钉，管孔端的面上施加是待办 ——
    ///      压接端面上口径与这个解析值相对差 ≤ 1e-9；导热条带两端都在面上（管孔 R48 已是面上），相对差 ≤ 1e-9；形心口径恰差半格。
    ///      2026-09-15 Opus 5 复审修：原句「面上口径电流、热流与解析值相对差 ≤ 1e-9」比门实际验的强，改成上面这样）；
    ///      R48_压接面上定温_收敛阶_2026-09-15.txt（第二批 B2 工作点均匀 h = 1／0.5／0.25／0.125）：面上 − 形心 抽热 片0 +0.815／+0.410／+0.206 W、片1 +1.544／+0.776／+0.389 W（随 h 减半，
    ///      两口径趋于同一极限）；面上口径抽热步长 片0 −0.338 → −0.104 → −0.030 W（比 0.31／0.29）、片1 −0.401 → −0.099 → −0.027 W（比 0.25／0.28），
    ///      夹持带走步长 ≤ 0.009 W（形心口径 0.28～0.85 W）。
    ///      ⚠ **误差符号变了**（2026-09-15 Opus 5 复审补，审查意见 major）：同一文件面上口径均匀四档按末三档比值外推的极限（外推，不是实测）
    ///        片0 ≈ 22.297 W、片1 ≈ 32.039 W。面上口径抽热随 h 从**高处**单调降下来：片0 h = 1／0.5／0.25 偏 +0.48／+0.15／+0.04 W，片1 +0.54／+0.14／+0.04 W；
    ///        导航网格不铺细带（R48_压接面上定温_细带去留_2026-09-15.txt 导航行）片0 23.1971 W 偏 +0.90、片1 33.1522 W 偏 +1.11。
    ///        形心口径是从**低处**升上来：片0 h = 1／0.5／0.25 偏 −0.33／−0.26／−0.16 W，片1 −1.01／−0.64／−0.35 W；A 路导航网格片0 21.5951 W
    ///        （R48ClampFaceGateTests 门 c 的基线树记录）偏 −0.70。
    ///        ⇒ 粗网格上面上口径的绝对误差不一定更小（片0 h = 1：0.48 对 0.33；导航网格：0.90 对 0.70）—— 形心口径的一阶负误差原先抵消了另一个正号误差；
    ///        偏差方向对「管孔净流入 > 0」是**偏乐观**的一侧。导航网格上的解本来就不可交付（Solver 第二遍与 MeshVerify 加密复算以判定网格为准，本配方不改这条），
    ///        但判定网格上仍留正偏差；同一文件的分量拆账（抽热 = 其余 + 夹持带走 − 舌区发热）：h = 1 → 极限的正偏差主要来自舌区发热（ρ(T)·J²，J 取等温电流场）从低处升上来（片0 约 −0.83 W、片1 约 −1.24 W，外推），其余散热同向偏低抵掉一部分。
    ///        离散误差预算按本配方重做时符号要写进结论。正偏差来源的定位尝试 R48_压接面上定温_正偏差定位_2026-09-15.txt：h = 1 → 0.25 的变化里
    ///        舌片 x 向分辨率（含压接内边）那部分份额 ≈ 0（舌区发热 −0.010／−0.011、抽热 +0.002／+0.000），其余落在环与孔边的 x 向分辨率、横跨舌宽的 z 向分辨率里，
    ///        张量积网格上分不开 —— 电流场管孔在格上钉 V = 0（快门 (a) 已证实电极在管孔格形心）这个候选既没被证实也没被排除，要在 ShellCurrent 里单做。
    /// </summary>
    /// <param name="holeRadiusMm">管孔半径，用于边界标记与细化中心</param>
    /// <param name="hInner">内带网格尺寸 mm（管孔 + 焊脚那一圈），≤0 = 不分内带（R47 D）。</param>
    /// <param name="innerRadius">内带半径 mm。</param>
    /// <param name="twoTabs">双舌片：两端都是压接边（解析板经 Build 进来时按板的 TwoTabs 传）。</param>
    /// <param name="xAnchors">x 轴必须落成节点的坐标（null = 从厚度场推：<see cref="AnchorsFromField"/>）。</param>
    /// <param name="zAnchors">z 轴必须落成节点的坐标（null = 从厚度场推：±舌半宽）。</param>
    /// <param name="clampBandMm">压接细带单侧宽度 mm（配方 ④）。缺省 NaN = 生产配方 <see cref="ClampBandPerHFine"/>×hFine（2026-09-15 起为 0，不铺）；
    /// 显式给正数 = 照给的宽度（老探针「固定 3 mm」「3·h」原义不变）；显式 0 = 不铺。解析见 <see cref="ResolveClampBandMm"/>。</param>
    /// <param name="clampFullFace">压接段整面接触（配方 ③）。缺省 true = 生产配方，填 <see cref="ShellMesh.ClampCell"/>；
    /// 显式 false = 老口径只钉外圈（ClampCell 留空，与改动前逐位相同）。</param>
    /// <param name="clampFaceDirichlet">压接边界施加在面上（配方 ⑤，写进 <see cref="ShellMesh.ClampFaceDirichlet"/>）。缺省 true = 生产配方；
    /// 显式 false = 形心整格口径（A 路，与改动前逐位相同）。只在整面接触时起作用（R48 F 2026-09-15 Opus 5）。</param>
    public static ShellMesh BuildFromField(ThicknessField f, double holeRadiusMm,
                                           double yPlane = 0,
                                           double hFine = 2.0, double hCoarse = 11.0,
                                           double fineRadius = 50.0, double clampLenMm = 4.0,
                                           double hInner = 0, double innerRadius = 0,
                                           bool twoTabs = false,
                                           IReadOnlyList<double>? xAnchors = null, IReadOnlyList<double>? zAnchors = null,
                                           double holeTagBandMm = 0, double clampBandMm = double.NaN, bool clampFullFace = true, bool clampFaceDirichlet = true)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));
        // 图纸路径没传锚点就从厚度场推（R47 复修 M1；解析板由 Build 传精确锚点）
        if (xAnchors is null || zAnchors is null)
        {
            var (xa, za) = AnchorsFromField(f);
            xAnchors ??= xa; zAnchors ??= za;
        }
        var m = BuildFromMaterial(f, holeRadiusMm, yPlane, hFine, hCoarse, fineRadius, clampLenMm, hInner, innerRadius, twoTabs,
                                  xAnchors, zAnchors, holeTagBandMm, clampBandMm, clampFullFace, clampFaceDirichlet);
        m.SourceField = f;
        return m;
    }

    /// <summary>
    /// ★★★ 2026-09-18，Fable 5.1（网格生成根因修复）：**唯一的生成器本体** —— 解析板（<see cref="AnalyticMaterial"/>，经 <see cref="Build"/>）与
    /// 栅格厚度场（<see cref="ThicknessField"/>，经 <see cref="BuildFromField"/>）都走这里，只通过 <see cref="IMaterialField"/> 量材料。
    ///
    /// 与 R47/R48 的 BuildFromField 相比改了四件事（判据、求解器规则、默认网格格距一个都没动）：
    ///   F1 单元的有料面积／体积／形心 = 材料场的精确积分（解析板没有栅格、没有点采样）；
    ///   F2 留格门槛 25 % → <see cref="CellKeepFrac"/>（真料不丢；带格因为没有料而自然消失）；面长 = 边上有料的长度（<see cref="ShellMesh.BuildFaces"/> 的 clip）；
    ///      单元形心 = **材料形心**（部分格的形心落在料里，分区判定、面导度距离都按料算）；
    ///   管孔边界 = 圆弧本身（<see cref="AddHoleArcFaces"/>：每个被孔圆穿过的格建一条弧面，长度 = 弧长、中点在弧上），不再是落在孔里的阶梯格边
    ///      （阶梯边有料长度为 0，按 F2 本来就建不出面；孔边定温带 3 mm 的口径不变，弧面 r = 孔半径 必在带内）；
    ///   F5 轴端按锚点规则处理（<see cref="GradedAxisCentered"/>）。
    /// 配方声明 ①～⑤ 原文见 <see cref="BuildFromField"/> 上方注释（逐字未改，仍然生效）。
    /// </summary>
    public static ShellMesh BuildFromMaterial(IMaterialField f, double holeRadiusMm,
                                              double yPlane = 0,
                                              double hFine = 2.0, double hCoarse = 11.0,
                                              double fineRadius = 50.0, double clampLenMm = 4.0,
                                              double hInner = 0, double innerRadius = 0,
                                              bool twoTabs = false,
                                              IReadOnlyList<double>? xAnchors = null, IReadOnlyList<double>? zAnchors = null,
                                              double holeTagBandMm = 0, double clampBandMm = double.NaN, bool clampFullFace = true, bool clampFaceDirichlet = true)
        => BuildFromMaterialWith(f, holeRadiusMm, MeshRules.Production, yPlane, hFine, hCoarse, fineRadius, clampLenMm, hInner, innerRadius, twoTabs,
                                 xAnchors, zAnchors, holeTagBandMm, clampBandMm, clampFullFace, clampFaceDirichlet);

    /// <summary>
    /// ★ 2026-09-19，Fable 5.1（第二轮复核第 9 条）：生成器本体带**网格层规则**（<see cref="MeshRules"/>）—— 生产入口一律传 <see cref="MeshRules.Production"/>，
    /// 门经 InternalsVisibleTo 传别的组合（关掉面长裁剪／弧面／并格／轴端规则）做「改回 ⇒ 红」对照；rules 为 null 按生产规则。别的参数与 <see cref="BuildFromMaterial"/> 逐字相同。
    /// </summary>
    internal static ShellMesh BuildFromMaterialWith(IMaterialField f, double holeRadiusMm, MeshRules? rules,
                                                    double yPlane = 0,
                                                    double hFine = 2.0, double hCoarse = 11.0,
                                                    double fineRadius = 50.0, double clampLenMm = 4.0,
                                                    double hInner = 0, double innerRadius = 0,
                                                    bool twoTabs = false,
                                                    IReadOnlyList<double>? xAnchors = null, IReadOnlyList<double>? zAnchors = null,
                                                    double holeTagBandMm = 0, double clampBandMm = double.NaN, bool clampFullFace = true, bool clampFaceDirichlet = true)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));
        rules ??= MeshRules.Production;
        // 配方 ⑤（R48 F 2026-09-15 Opus 5）：求解器从网格上读，电流与温度两边同一个口径。
        // 2026-09-15 Opus 5（合并，复审后改）：开关改为 init，建网格时一次写定（原在函数末尾 m.ClampFaceDirichlet = clampFaceDirichlet; 赋值，其间无人读它，结果逐位不变）
        var m = new ShellMesh { ClampFaceDirichlet = clampFaceDirichlet };
        string clampNote = "";
        // R48 配方指纹（2026-09-15 Opus 5）：压接边界候选 x 与实际铺下的细带单侧宽度（多个边界取最小；没有候选 = 没铺）
        double[] clampCand = Array.Empty<double>();
        double bandLaid = double.PositiveInfinity;
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
        if ((xAnchors is null || zAnchors is null) && f is ThicknessField tfAnch)
        {
            var (xa, za) = AnchorsFromField(tfAnch);
            xAnchors ??= xa; zAnchors ??= za;
        }
        // ★★ R48 实验 b（2026-09-14，Opus 5；常驻数值把关人第五轮定的位置）：**压接边界也落成节点**。
        //   病：压接面按「面中点 x ≤ 舌尖 + 压接长」判（下面 BuildFaces），这批格子同时是电流场的电极（V = 1）与热场的夹持定温区；
        //   而 x = 舌尖 + 压接长 从来不是节点 ⇒ 被钉住那一段的长度只能取到最近的节点，随分级轴从哪里开始长而跳，最多一个粗格。
        //   实验 a（deliverable/R48_实验a_压接段相位_2026-09-14.txt）：抽热随细区半径的离群在四片、三档 h 上都与相位极值同 R；
        //   片 2 九行里「钉住区边偏移」的符号与舌区 ΣJ²tA、抽热偏差的符号全部一致。
        //   为什么加在这里而不在 AnchorsOf／AnchorsFromField：压接判定用的舌尖 x 就是这里的材料包络 xMin，
        //   锚点必须与判定同一个来源、同一个压接长，否则相位会以 ±栅格步回来（数值把关人副作用表第 1 条）；
        //   一处加，解析与图纸两条路径、双舌两端一起管。
        //   不加的情形（写进 ClampAnchorNote，不静默）：离舌尖、离已有锚点不足一个最细格（免得生出发丝格，N3 只管四分之一格）。
        {
            double hMin = hInner > 1e-9 && innerRadius > 1e-9 ? Math.Min(hFine, hInner) : hFine;
            var cand = twoTabs
                ? new[] { -(Math.Abs(xMin) - clampLenMm), Math.Abs(xMin) - clampLenMm }
                : new[] { xMin + clampLenMm };
            var have = new List<double>(xAnchors ?? Array.Empty<double>());
            var notes = new List<string>();
            clampCand = cand;
            foreach (double ca in cand)
            {
                if (!(clampLenMm > 0) || double.IsNaN(ca) || ca <= xMin + 1e-9 || ca >= xMax - 1e-9)
                { notes.Add($"压接边界 x = {ca:0.###} 不在材料范围内，没加"); bandLaid = 0; continue; }
                double dTip = Math.Min(ca - xMin, xMax - ca);
                double dAnc = have.Count == 0 ? double.PositiveInfinity : have.Min(v => Math.Abs(v - ca));
                if (dTip < hMin - 1e-9 || dAnc < hMin - 1e-9)
                { notes.Add($"压接边界 x = {ca:0.###} 离{(dTip < dAnc ? "端点" : "已有锚点")}只有 {Math.Min(dTip, dAnc):0.###} mm，不足一个最细格 {hMin:0.###}，没加（被钉住长度仍按最近节点取）"); bandLaid = 0; continue; }
                have.Add(ca);
                notes.Add($"压接边界 x = {ca:0.###} 已落成节点");
                // ★ R48 生产配方 ④（2026-09-14，Opus 5；数值把关人第八轮提的细带、第十轮定的自相似）：压接边界两侧各 band 铺细步 hFine。
                //   实验 b 剩下的「分级比均匀偏低」随 h 减半、与细区半径无关，病在压接边界处的粗格整格钉温
                //   （deliverable/R48_离散误差预算_2026-09-14.txt：分级 − 均匀 片0 −1.893／−0.957／−0.490 W，铺细带后 ≤ 0.022 W）。
                //   带宽缺省 = ClampBandPerHFine × hFine（2026-09-14 定为自相似 3·hFine；R48 F 2026-09-15 Opus 5 改为 0 = 缺省不铺，依据见 ClampBandPerHFine）；
                //   显式传 0 = 不铺，显式正数照给的（老探针原义不变）。
                double band = ResolveClampBandMm(clampBandMm, hFine);
                if (band > 0)
                {
                    xBands.Add(new Band(ca - band, ca + band, hFine));
                    notes.Add($"压接边界两侧各 {band:0.###} mm 铺细步 {hFine:0.###} mm");
                }
                // R48 配方指纹（2026-09-15 Opus 5）：实际铺下的细带单侧宽度 —— 多个压接边界取最小（有一个没铺就是 0）
                bandLaid = Math.Min(bandLaid, Math.Max(0, band));
            }
            // 几何退化：压接段伸进圆盘（压接长 ≥ 舌片自由段）⇒ 下面的判定会把圆盘边界面也标成压接面。原有的毛病，与锚点无关；先记下，不静默。
            var tan = (xAnchors ?? Array.Empty<double>()).Where(v => v < 0).ToArray();   // 单舌：切点在管轴左侧，取离管轴最近的那个
            if (!twoTabs && tan.Length > 0 && xMin + clampLenMm >= tan.Max() - 1e-9)
            {
                // 2026-09-14 Opus 5 复审修：这句经 LineRunner 进界面输出框 ⇒ 改说人话（原文「圆盘边界面也会被标成压接面」是网格内部说法）。
                // 2026-09-15 Opus 5：LineRunner 不再读这句文字，改读 ShellMesh.ClampIntoDisc 与两个 x（说明文字由 LineRunner.ClampIntoDiscText 按这三个量生成）。
                notes.Add($"⚠ 压接长 {clampLenMm:0.###} mm 伸进了圆盘（压接段到 x = {xMin + clampLenMm:0.###} mm，舌片与圆盘在 x = {tan.Max():0.###} mm 相接）：舌片比压接长还短，这块板几何上不成立");
                m.ClampIntoDisc = true; m.ClampEndXMm = xMin + clampLenMm; m.ClampTangentXMm = tan.Max();
            }
            xAnchors = have;
            clampNote = string.Join("；", notes);
        }
        double[] xs = GradedAxisCenteredWith(xMin, xMax, xBands, hCoarse, 1.3, 0.0, xAnchors, endRule: rules.AxisEndRule);   // growth／center = 公开入口的缺省值，逐字
        double[] zs = GradedAxisCenteredWith(zMin, zMax, zBands, hCoarse, 1.3, 0.0, zAnchors, endRule: rules.AxisEndRule);

        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], yPlane, zs[j])); }

        double tabTipX = xMin;      // 舌尖 = 材料的 XMinMaterial（不再是图幅左缘）
        for (int i = 0; i < nx - 1; i++)
        {
            for (int j = 0; j < nz - 1; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                double cellArea = (x1 - x0) * (z1 - z0);
                MaterialIntegral I = f.Integrate(x0, x1, z0, z1);   // F1：材料场的精确积分（解析板）／方格面积积分（栅格）；注入对照由测试侧的材料源给
                if (!(I.Area > CellKeepFrac * cellArea)) continue;   // F2：只挡浮点尘埃，真料不丢；碎格在 MergeSlivers 里并入邻格

                m.Cells.Add(new[] { nodeId[i, j], nodeId[i + 1, j], nodeId[i + 1, j + 1], nodeId[i, j + 1] });
                m.Area.Add(I.Area);
                m.Centroid.Add(new Vec3(I.MomentX / I.Area, yPlane, I.MomentZ / I.Area));   // 材料形心（满格 = 格心）
                m.Thickness.Add(I.Volume / I.Area);              // 体积积分 ÷ 覆盖面积：焊脚这类比格子细的堆料按料算
                m.Part.Add(0);
                m.Frac.Add(I.Area / cellArea);                   // R48：边界格的覆盖率（诊断；并过格的按全部矩形算）
            }
        }

        int Tagger(Vec3 mid)
        {
            // 管孔：紧贴孔半径的那一圈边界面（槽的边界半径不同，不会误判）
            if (IsHoleFace(mid, holeRadiusMm, holeTagBandMm)) return ShellMesh.TagHole;
            // 压接边：单舌在舌尖那一段；双舌两端都是（R48 2026-09-14 Opus 5：式子收进 InClampSegment，逐字未改）
            if (InClampSegment(mid.X, tabTipX, clampLenMm, twoTabs)) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        }
        if (rules.ClipFaces) m.BuildFaces(Tagger, (p0, p1) => ClipSegment(f, p0, p1));   // F2：面长 = 边上有料的长度
        else m.BuildFaces(Tagger);                                                        // 注入对照：整边面长（老口径）
        // ★ 2026-09-18／19，Fable 5.1：薄片格并入邻格 —— 在面图上收缩（A 管孔外角薄片；B 重构条件数不足的楔形格与碎格），见 MergeSlivers
        var rects = MergeSlivers(m, holeRadiusMm, rules, clampCand);
        if (rules.HoleArcFaces) AddHoleArcFaces(m, holeRadiusMm, Tagger, rects);        // 管孔边界 = 圆弧本身（并入格的那段弧也在）；注入对照：阶梯孔边
        m.CellRects = rects;
        m.ComputeHoleTagDiagnostics(holeRadiusMm);
        m.Material = f;
        m.SourceField = f as ThicknessField;
        // ★ R48 生产配方 ③（2026-09-14，Opus 5）：压接段整面接触。与上面边界面标签同一个判定（同一 tabTipX、同一压接长），只是对格子形心判。
        //   边界面标签照旧打（外圈格仍带 TagTabEnd），ShellCurrent／ShellThermal 取两者的并集。侧边面中点 x = 格形心 x ⇒ 压接边界落成节点时
        //   外圈格是整面格的子集，并集就是整面；没落成节点（压接长不足一个最细格等，见 ClampAnchorNote）时舌尖那一列可能只带标签、形心在段外，并集照样把它钉住。
        // ⚠ 退化几何：压接段盖到了管孔（带管孔标签的格形心也在压接段内，例如没有舌片、材料全在压接长以内的图纸）⇒ 整面接触会把
        //   V=1 电极直接压在 V=0 的孔格上、整片钉成夹持温度，场没有意义（快套件 TabInsulPerPlateTests「推不出切点的场」就是这块板：
        //   材料 x ∈ [0, 28]、压接长 40，整面口径下段 1 求解失败）。这种板**不填** ClampCell、退回只钉外圈，并把原因写进 ClampAnchorNote（不静默）。
        //   外圈口径在这块板上同样没有物理意义，只是场解得出来、下游判据照常标「判不了」，与改动前一致。
        if (clampFullFace)
        {
            var cc = new bool[m.CellCount];
            for (int i = 0; i < m.CellCount; i++)
                cc[i] = InClampSegment(m.Centroid[i].X, tabTipX, clampLenMm, twoTabs);
            int holeInClamp = m.Faces.Where(fc => fc.B < 0 && fc.Tag == ShellMesh.TagHole && cc[fc.A]).Select(fc => fc.A).Distinct().Count();
            // ★ 2026-09-15 Opus 5（J 路，合并把关待办 P2-5）：压接段里一格形心都没有（压接长不到半个格）⇒ 不写全假的数组 —— 那等于「整面接触」没生效，
            //   与本类 ClampCell 注释「非空 = 整面接触」、MeshRecipe 注释一致；原先照写，求解器按长度判成整面口径、指纹却记未生效（两种定义）。
            if (holeInClamp == 0 && Array.IndexOf(cc, true) >= 0) m.ClampCell = cc;
            else if (holeInClamp == 0)
                clampNote += (clampNote.Length > 0 ? "；" : "") + $"压接长 {clampLenMm:0.###} mm 不到半个格，压接段里没有格子，只钉舌尖外圈";
            else
            {
                // R48（2026-09-15 Opus 5）：下游读这一位，不读下面这句文字
                m.ClampCoversHole = true;
                clampNote += (clampNote.Length > 0 ? "；" : "")
                           // 2026-09-14 Opus 5 复审修：这句经 LineRunner 进界面输出框 ⇒ 说人话，不写「整面接触」「外圈」「孔边格」这类网格内部说法
                           //   （退回只钉外圈、段内孔边格数这两件事留在上面的代码注释里；孔边格数本身只作判定，不进文字）。
                           + $"⚠ 压接长 {clampLenMm:0.###} mm 盖到了管孔：这块板没有足够长的舌片可供压接，本片电流与温度结果不可信";
            }
        }
        m.ClampAnchorNote = clampNote;
        // 配方 ⑤ 的开关已在 new ShellMesh 时写入（2026-09-15 Opus 5（合并，复审后改）：init，见函数开头）
        m.ClampLenMm = clampLenMm;
        // ★ R48 配方指纹（2026-09-15，Opus 5）：从建好的网格本身量 —— 整面接触看 ClampCell 真填了没有，锚点看压接边界 x 真在节点里没有。
        bool anchorOnNode = clampCand.Length > 0
                         && clampCand.All(ca => !double.IsNaN(ca) && xs.Any(x => Math.Abs(x - ca) <= 1e-9));
        double bandMm = clampCand.Length == 0 || double.IsPositiveInfinity(bandLaid) ? 0.0 : bandLaid;
        // 2026-09-15 Opus 5（合并）：G1 的指纹写于 F 之前，没有配方 ⑤ —— 补「压接边界施加在面上 vs 形心整格」，同样从建好的网格上量：
        //   开关写进网格（上一行之前）之后数压接面（ShellMesh.ClampFaceCount：ClampFaceActive 时按 ClampSetCells 数 IsClampFace 的内部面，与两个求解器同一个判定）。
        int clampFaces = m.ClampFaceCount();
        m.Recipe = new MeshRecipe
        {
            ClampFullFace = m.ClampFullFaceActive,   // 2026-09-15 Opus 5（J 路）：唯一定义
            ClampFaceDirichlet = clampFaces > 0,
            ClampFaceCount = clampFaces,
            ClampCoversHole = m.ClampCoversHole,
            ClampAnchorOnNode = anchorOnNode,
            ClampBandMm = bandMm,
            HFineMm = hFine,
            ClampBandPerHFine = hFine > 0 ? Math.Round(bandMm / hFine, 9) : double.NaN,
            HoleTagBandMm = holeTagBandMm > 0 ? holeTagBandMm : HoleTagBandMm,
        };
        return m;
    }

    /// <summary>
    /// 一维重叠：区间 [a, b] 与栅格第 k 个方格 [origin + k·s − s/2, origin + k·s + s/2] 的重叠长度，k 只取有重叠的那几个。
    /// （R48 2026-09-14 Opus 5：从 BuildFromField 抽出，算式逐字未改；保温分界份额 <see cref="MaterialFraction"/> 用同一份。）
    /// </summary>
    public static List<(int k, double ov)> RasterOverlap(double a, double b, double origin, int count, double s)
    {
        double half = 0.5 * s;
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

    /// <summary>2026-09-18，Fable 5.1：一条轴对齐格边上的有料长度与有料中点（BuildFaces 的 clip）。非轴对齐的边（本生成器不产生）按整边算。</summary>
    public static (double Length, Vec3 Mid) ClipSegment(IMaterialField f, Vec3 p0, Vec3 p1)
    {
        bool vert = Math.Abs(p0.X - p1.X) <= 1e-9, horz = Math.Abs(p0.Z - p1.Z) <= 1e-9;
        if (vert == horz) return ((p1 - p0).Norm, (p0 + p1) * 0.5);
        if (vert)
        {
            var (len, mid) = f.SegmentMaterial(true, p0.X, p0.Z, p1.Z);
            return (len, new Vec3(p0.X, p0.Y, mid));
        }
        else
        {
            var (len, mid) = f.SegmentMaterial(false, p0.Z, p0.X, p1.X);
            return (len, new Vec3(mid, p0.Y, p0.Z));
        }
    }

    /// <summary>
    /// ★★ 2026-09-18，Fable 5.1：**管孔边界面 = 圆弧本身**。每个被孔圆 r = <paramref name="holeRadiusMm"/> 穿过的（留下的）格，
    /// 建一条边界面：长度 = 落在该格矩形里的弧长、中点在弧上（r 恰 = 孔半径 ⇒ 管孔判定带必命中）、DistAB = 材料形心到弧中点。
    /// 为什么：老口径的管孔边界面是「与被丢格相邻的格边」—— 一段落在孔里的阶梯折线，总长比周长多约 4/π 倍、位置随格线翻面
    /// （管内径扫描上电阻台阶 0.3～0.5 %/档，网格审计_2 孔径扫描）；面长按材料裁剪之后这些阶梯边有料长度为 0，本来就建不出面，
    /// 定温边界只能落在真实的孔弧上。一个格里被切出两段弧（角上）就建两条面。
    /// 电流场仍按「带管孔标签面的格」钉 V = 0（ShellCurrent 的规则不动）；热场的面上定温 gHole = k·t·弧长 ÷ DistAB（ShellThermal 的规则不动）。
    /// </summary>
    /// <param name="rects">每格的矩形列表（并入了别的格的格有多个矩形；null = 取节点矩形）。</param>
    public static void AddHoleArcFaces(ShellMesh m, double holeRadiusMm, Func<Vec3, int> tagger, List<(double x0, double x1, double z0, double z1)>[]? rects = null)
    {
        if (!(holeRadiusMm > 0)) return;
        double rh = holeRadiusMm;
        for (int c = 0; c < m.CellCount; c++)
        {
            var rectList = rects != null && c < rects.Length && rects[c] != null ? rects[c] : new List<(double, double, double, double)> { CellRect(m, c) };
            foreach (var (x0, x1, z0, z1) in rectList)
            {
            // 快速排除：矩形离原点最近点 > rh 或最远角 < rh ⇒ 圆不穿过
            double nx = Math.Clamp(0, x0, x1), nz = Math.Clamp(0, z0, z1);
            double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            if (nx * nx + nz * nz >= rh * rh || fx * fx + fz * fz <= rh * rh) continue;
            // 圆与四条格线的交角
            var ang = new List<double> { 0, 2 * Math.PI };
            foreach (double xv in new[] { x0, x1 })
                if (Math.Abs(xv) < rh) { double a = Math.Acos(xv / rh); ang.Add(a); ang.Add(2 * Math.PI - a); }
            foreach (double zv in new[] { z0, z1 })
                if (Math.Abs(zv) < rh) { double a = Math.Asin(zv / rh); ang.Add(Norm(a)); ang.Add(Norm(Math.PI - a)); }
            ang.Sort();
            // 逐段判中点在不在矩形里，相邻的段合并
            var pieces = new List<(double a, double b)>();
            for (int k = 0; k + 1 < ang.Count; k++)
            {
                double a = ang[k], b = ang[k + 1];
                if (b - a <= 1e-12) continue;
                double tm = 0.5 * (a + b), px = rh * Math.Cos(tm), pz = rh * Math.Sin(tm);
                bool inside = px >= x0 - 1e-9 && px <= x1 + 1e-9 && pz >= z0 - 1e-9 && pz <= z1 + 1e-9;
                if (!inside) continue;
                if (pieces.Count > 0 && Math.Abs(pieces[^1].b - a) <= 1e-12) pieces[^1] = (pieces[^1].a, b);
                else pieces.Add((a, b));
            }
            // 跨 0／2π 的两段接起来
            if (pieces.Count >= 2 && pieces[0].a <= 1e-12 && Math.Abs(pieces[^1].b - 2 * Math.PI) <= 1e-12)
            {
                var first = pieces[0]; var last = pieces[^1];
                pieces.RemoveAt(pieces.Count - 1); pieces.RemoveAt(0);
                pieces.Add((last.a, first.b + 2 * Math.PI));
            }
            foreach (var (a, b) in pieces)
            {
                double len = rh * (b - a);
                if (len <= 1e-9) continue;
                double tm = 0.5 * (a + b);
                var mid = new Vec3(rh * Math.Cos(tm), m.Centroid[c].Y, rh * Math.Sin(tm));
                var face = new MeshFace { A = c, B = -1, Mid = mid, Length = len, FullLength = len, Tag = tagger(mid) };
                face.DistAB = m.CentroidToFaceMm(c, face);
                m.Faces.Add(face);
            }
            }
        }
        static double Norm(double a) { while (a < 0) a += 2 * Math.PI; while (a >= 2 * Math.PI) a -= 2 * Math.PI; return a; }
    }

    /// <summary>单元 <paramref name="c"/> 的节点矩形（并过格的单元只是目标格自己那一个；全部矩形见 <see cref="ShellMesh.CellRects"/>）。</summary>
    public static (double x0, double x1, double z0, double z1) CellRect(ShellMesh m, int c)
    {
        double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
        foreach (int n in m.Cells[c]) { var v = m.Nodes[n]; x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z); }
        return (x0, x1, z0, z1);
    }

    /// <summary>
    /// ★★ 2026-09-18／19，Fable 5.1：**薄片格并入邻格** —— 面拓扑建好之后在面图上做**收缩**（并入 = 两格合成一个未知量：面积、体积、一次矩相加，
    /// 形心、厚度随之重算；两格之间的面消失，各自与第三格的面改挂到合成格名下，长度、中点不变，形心距按新形心重算）。返回每格的矩形列表
    /// （<see cref="ShellMesh.CellRects"/>；管孔弧面按矩形建，分界份额按矩形量），并把单元表就地压实。
    ///
    /// 规则 A（2026-09-18，管孔外角薄片）：孔圆从一个格的两条相邻格边穿过、把格切成「里面的大块 + 外角的小块」时，材料只剩外角那一小块，它有料的边都被圆穿过 ⇒
    ///   两个邻格也都是被孔圆穿过的格。电流场按「带管孔面的格钉 V = 0」（ShellCurrent 的规则不动）⇒ 这一小块与它所有邻格都钉在 V = 0，
    ///   面上一点电流都没有、重构出的 J = 0 —— 下游按 q/J 排的移除优先级（RemovalPriority）把它排到最前，槽心跟着跑偏。
    ///   ⇒ 「所有有料边都被孔圆穿过」的格并入共有最长有料边的邻格（电极集合不变）。只并这种格，别的管孔格一律不动。
    /// 规则 B（2026-09-19，第二轮复核第 10 条）：不被管孔圆穿过的格里，覆盖率 &lt; <see cref="MeshRules.CellMergeFrac"/>（碎格）的，并入共有最长有料边、
    ///   同样不被孔圆穿过、且与它在压接边界同一侧的邻格；合成格覆盖率仍不足再并（最多 8 遍）。
    ///   楔形格的幻影 J 峰（复核第 1 条）曾试过也在这里按重构条件数并（κ′ &lt; 0.2）：Heater1 的幻影峰是没了，但管孔边的真峰跟着被合成格抹掉 5～10 %（2026-09-19 实测，
    ///   R48NSliverGateTests 门 2 红）—— 病在重构量法不在网格，改治在 ShellCurrent（SliverKappaMin），网格层不并楔形格。
    /// 2026-09-19 改写说明：2026-09-18 版规则 A 在建面之前按单元索引并、再由 RewireSliverEdges 把第三格的边界面改接 —— 两个相邻的并入格之间那条边它接不上
    ///   （碎格也可能相邻）；改成面图收缩后并入前后的面一条不少。规则 A 的判定逐字不变（有料边 = 建出来的面，长度就是裁剪过的有料长度）。
    /// </summary>
    internal static List<(double x0, double x1, double z0, double z1)>[] MergeSlivers(ShellMesh m, double holeRadiusMm, MeshRules rules, IReadOnlyList<double>? noCrossX)
    {
        int n = m.CellCount;
        var rects = new List<(double x0, double x1, double z0, double z1)>[n];
        for (int c = 0; c < n; c++) rects[c] = new List<(double, double, double, double)> { CellRect(m, c) };
        m.HoleSliversMerged = 0; m.SliversMerged = 0;
        if (n == 0 || !rules.ClipFaces || !(rules.MergeHoleCorners || rules.MergeSlivers)) return rects;   // 不裁面长（注入对照）就没有「有料边」可言，不并
        double rh = holeRadiusMm;
        const double tol = 1e-7;
        var alive = new bool[n]; Array.Fill(alive, true);
        var cellFaces = new List<int>[n];
        for (int c = 0; c < n; c++) cellFaces[c] = new List<int>();
        for (int k = 0; k < m.Faces.Count; k++) { var fc = m.Faces[k]; cellFaces[fc.A].Add(k); if (fc.B >= 0) cellFaces[fc.B].Add(k); }
        var dead = new bool[m.Faces.Count];
        var touched = new bool[n];
        var lines = (noCrossX ?? Array.Empty<double>()).Where(v => !double.IsNaN(v)).ToArray();

        static bool Crosses((double x0, double x1, double z0, double z1) r, double rh)
        {
            if (!(rh > 0)) return false;
            double nx = Math.Clamp(0, r.x0, r.x1), nz = Math.Clamp(0, r.z0, r.z1);
            double fx = Math.Max(Math.Abs(r.x0), Math.Abs(r.x1)), fz = Math.Max(Math.Abs(r.z0), Math.Abs(r.z1));
            return nx * nx + nz * nz < rh * rh && fx * fx + fz * fz > rh * rh;
        }
        bool HoleCell(int c) { foreach (var r in rects[c]) if (Crosses(r, rh)) return true; return false; }
        // 面中点落在格 c 的哪个矩形的哪条边上 ⇒ 那条整边（竖直?、线坐标、两端）
        bool EdgeOf(int c, MeshFace fc, out bool vert, out double line, out double a, out double b)
        {
            foreach (var r in rects[c])
            {
                bool onX0 = Math.Abs(fc.Mid.X - r.x0) <= tol, onX1 = Math.Abs(fc.Mid.X - r.x1) <= tol;
                bool onZ0 = Math.Abs(fc.Mid.Z - r.z0) <= tol, onZ1 = Math.Abs(fc.Mid.Z - r.z1) <= tol;
                if ((onX0 || onX1) && fc.Mid.Z >= r.z0 - tol && fc.Mid.Z <= r.z1 + tol) { vert = true; line = onX0 ? r.x0 : r.x1; a = r.z0; b = r.z1; return true; }
                if ((onZ0 || onZ1) && fc.Mid.X >= r.x0 - tol && fc.Mid.X <= r.x1 + tol) { vert = false; line = onZ0 ? r.z0 : r.z1; a = r.x0; b = r.x1; return true; }
            }
            vert = false; line = a = b = double.NaN; return false;
        }
        static bool EdgeCrossed(bool vert, double line, double a, double b, double rh)
        {
            double t = Math.Clamp(0, Math.Min(a, b), Math.Max(a, b));
            double near = Math.Sqrt(line * line + t * t), far = Math.Sqrt(line * line + Math.Max(a * a, b * b));
            return near < rh - 1e-12 && far > rh + 1e-12;
        }
        static int Other(MeshFace fc, int c) => fc.A == c ? fc.B : fc.A;
        int SideOf(int c, double xLine)
        {
            int s0 = 0; bool first = true;
            foreach (var r in rects[c])
            {
                int rs = r.x1 <= xLine + tol ? -1 : r.x0 >= xLine - tol ? 1 : 0;
                if (first) { s0 = rs; first = false; } else if (rs != s0) return 0;
            }
            return s0;
        }
        bool SameSide(int c, int o) { foreach (double xl in lines) if (SideOf(c, xl) != SideOf(o, xl)) return false; return true; }
        void Merge(int c, int t)
        {
            double aC = m.Area[c], aT = m.Area[t];
            double vol = aC * m.Thickness[c] + aT * m.Thickness[t];
            double mx = aC * m.Centroid[c].X + aT * m.Centroid[t].X, mz = aC * m.Centroid[c].Z + aT * m.Centroid[t].Z;
            m.Area[t] = aC + aT;
            m.Thickness[t] = vol / m.Area[t];
            m.Centroid[t] = new Vec3(mx / m.Area[t], m.Centroid[t].Y, mz / m.Area[t]);
            rects[t].AddRange(rects[c]);
            double full = 0; foreach (var r in rects[t]) full += (r.x1 - r.x0) * (r.z1 - r.z0);
            m.Frac[t] = m.Area[t] / full;
            foreach (int k in cellFaces[c])
            {
                if (dead[k]) continue;
                var fc = m.Faces[k];
                if (fc.A == c) fc.A = t;
                if (fc.B == c) fc.B = t;
                if (fc.B >= 0 && fc.A == fc.B) { dead[k] = true; continue; }   // 并入后成了同一格的内部，不成面
                cellFaces[t].Add(k);
            }
            cellFaces[c].Clear();
            alive[c] = false; touched[t] = true;
        }

        // ── 规则 A：管孔外角薄片
        if (rules.MergeHoleCorners && rh > 0)
            for (int c = 0; c < n; c++)
            {
                if (!alive[c] || !Crosses(rects[c][0], rh)) continue;
                bool anyMat = false, anyNotCrossed = false; int best = -1; double bestLen = 0;
                foreach (int k in cellFaces[c])
                {
                    if (dead[k]) continue;
                    var fc = m.Faces[k];
                    if (fc.Length <= 1e-9) continue;
                    anyMat = true;
                    if (!EdgeOf(c, fc, out bool vert, out double line, out double a, out double b) || !EdgeCrossed(vert, line, a, b, rh)) { anyNotCrossed = true; break; }
                    if (fc.B >= 0 && fc.Length > bestLen) { bestLen = fc.Length; best = Other(fc, c); }
                }
                if (!anyMat || anyNotCrossed || best < 0 || best == c) continue;   // 有一条通向未被孔圆穿过的边 ⇒ 不是外角薄片
                Merge(c, best); m.HoleSliversMerged++;
            }

        // ── 规则 B：碎格
        if (rules.MergeSlivers)
            for (int pass = 0; pass < 8; pass++)
            {
                int did = 0;
                for (int c = 0; c < n; c++)
                {
                    if (!alive[c] || HoleCell(c)) continue;
                    if (!(m.Frac[c] < rules.CellMergeFrac)) continue;
                    int best = -1; double bestLen = 0;
                    foreach (int k in cellFaces[c])
                    {
                        if (dead[k]) continue;
                        var fc = m.Faces[k];
                        if (fc.B < 0) continue;
                        int o = Other(fc, c);
                        if (o == c || !alive[o] || HoleCell(o) || !SameSide(c, o)) continue;
                        if (fc.Length > bestLen) { bestLen = fc.Length; best = o; }
                    }
                    if (best < 0) continue;
                    Merge(c, best); m.SliversMerged++; did++;
                }
                if (did == 0) break;
            }

        if (m.HoleSliversMerged + m.SliversMerged == 0) return rects;
        // 压实单元表、面重编号、并过格的面按新形心重算形心距
        var keep = Enumerable.Range(0, n).Where(c => alive[c]).ToArray();
        var newIndex = new int[n]; Array.Fill(newIndex, -1);
        for (int k = 0; k < keep.Length; k++) newIndex[keep[k]] = k;
        var cells = keep.Select(c => m.Cells[c]).ToList();
        var area = keep.Select(c => m.Area[c]).ToList();
        var cen = keep.Select(c => m.Centroid[c]).ToList();
        var th = keep.Select(c => m.Thickness[c]).ToList();
        var part = keep.Select(c => m.Part[c]).ToList();
        var frac = keep.Select(c => m.Frac[c]).ToList();
        var rr = keep.Select(c => rects[c]).ToArray();
        var tch = keep.Select(c => touched[c]).ToArray();
        m.Cells.Clear(); m.Cells.AddRange(cells);
        m.Area.Clear(); m.Area.AddRange(area);
        m.Centroid.Clear(); m.Centroid.AddRange(cen);
        m.Thickness.Clear(); m.Thickness.AddRange(th);
        m.Part.Clear(); m.Part.AddRange(part);
        m.Frac.Clear(); m.Frac.AddRange(frac);
        var faces = new List<MeshFace>(m.Faces.Count);
        for (int k = 0; k < m.Faces.Count; k++)
        {
            if (dead[k]) continue;
            var fc = m.Faces[k];
            int a2 = newIndex[fc.A], b2 = fc.B >= 0 ? newIndex[fc.B] : -1;
            if (a2 < 0 || (fc.B >= 0 && b2 < 0)) throw new InvalidOperationException("并格自检：面挂到了已并入的格上");
            fc.A = a2; fc.B = b2;
            if (tch[fc.A] || (fc.B >= 0 && tch[fc.B]))
                fc.DistAB = fc.B >= 0 ? (m.Centroid[fc.B] - m.Centroid[fc.A]).Norm : m.CentroidToFaceMm(fc.A, fc);
            faces.Add(fc);
        }
        m.Faces.Clear(); m.Faces.AddRange(faces);
        return rr;
    }

    /// <summary>
    /// ★ 2026-09-18，Fable 5.1：单元有料面积里落在 r ≤ <paramref name="radiusMm"/> 的份额 —— 保温分界圆穿过的格子怎么混（ShellThermal 调这一处）。
    /// 解析板按精确积分（<see cref="AnalyticMaterial.FractionInsideCircle"/>），栅格场按方格中心（与 R48 的 <see cref="MaterialFraction"/> 逐位相同）。
    /// 没有材料来源或格里没有料 ⇒ NaN（调用方退回形心）。
    /// </summary>
    public static double MaterialFractionInCircle(ShellMesh m, int cell, double radiusMm)
    {
        if (m.Material is null || cell < 0 || cell >= m.CellCount) return double.NaN;
        var rl = m.CellRects != null && cell < m.CellRects.Length ? m.CellRects[cell] : null;
        if (rl is null || rl.Count <= 1)
        {
            var nd = m.Cells[cell];
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
            foreach (int n in nd)
            {
                var v = m.Nodes[n];
                x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z);
            }
            return m.Material.FractionInsideCircle(x0, x1, z0, z1, radiusMm);
        }
        // 2026-09-19 Fable 5.1：并过格的单元按全部矩形量（分子分母都是料：Σ 各矩形有料面积 × 该矩形圆内份额 ÷ Σ 各矩形有料面积）
        double all = 0, inA = 0;
        foreach (var (x0, x1, z0, z1) in rl)
        {
            double a = m.Material.Integrate(x0, x1, z0, z1).Area;
            if (!(a > 0)) continue;
            double fr = m.Material.FractionInsideCircle(x0, x1, z0, z1, radiusMm);
            if (double.IsNaN(fr)) continue;
            all += a; inA += a * fr;
        }
        return all > 0 ? Math.Clamp(inA / all, 0, 1) : double.NaN;
    }

    /// <summary>
    /// ★★ R48（2026-09-14，Opus 5；常驻数值把关人第七轮定的量法）：单元 <paramref name="cell"/> 的**有料面积里**，
    /// 方格中心满足 <paramref name="inside"/> 的那部分所占的份额。
    ///
    /// 为什么分子分母都从同一张栅格量：若分子用解析的「矩形∩圆」、分母用栅格积出的有料面积，
    /// +x 半边盘缘格（料全在圆内，真值 f = 1）会因栅格多算覆盖而 f &lt; 1，整段弧上都混进一截舌保温 —— 单向偏差，
    /// 与实验 a「直段 J²t」除错长度是同一类错（分子分母量的不是同一块东西）。同一张栅格量，+x 半边 f = 1。
    ///
    /// 格子矩形取节点坐标的包围盒（BuildFromField 的格子是轴对齐矩形）。没有厚度场或格里没有料 ⇒ NaN（调用方退回形心）。
    /// </summary>
    public static double MaterialFraction(ShellMesh m, int cell, Func<double, double, bool> inside)
    {
        var f = m.SourceField;
        if (f is null || cell < 0 || cell >= m.CellCount) return double.NaN;
        var nd = m.Cells[cell];
        double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
        foreach (int n in nd)
        {
            var v = m.Nodes[n];
            x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z);
        }
        var ovX = RasterOverlap(x0, x1, f.X0, f.Nx, f.Step);
        var ovZ = RasterOverlap(z0, z1, f.Z0, f.Nz, f.Step);
        double all = 0, inA = 0;
        foreach (var (ix, ox) in ovX)
            foreach (var (iz, oz) in ovZ)
            {
                if (f.T[ix * f.Nz + iz] <= 1e-9) continue;
                double a = ox * oz;
                all += a;
                if (inside(f.X0 + ix * f.Step, f.Z0 + iz * f.Step)) inA += a;
            }
        return all > 0 ? inA / all : double.NaN;
    }
}
