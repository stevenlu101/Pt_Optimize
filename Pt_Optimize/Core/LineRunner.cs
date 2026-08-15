using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// 整线算例的**全部输入**。管子走数值，法兰形状走 Rhino（每片可用不同 .3dm）。
/// </summary>
public sealed class LineCase
{
    // ── 管（数值输入）
    public double TubeIdMm = 50.0;
    public double WallMm = 1.0;
    public double SegLengthMm = 300.0;
    public string GradeName = "Pt";

    /// <summary>各段控温点 °C（控温点在每段中点）。长度即段数。</summary>
    public double[] SetpointC = { 1150, 1080, 1050 };
    /// <summary>各段玻璃压力水头 m</summary>
    public double[] HeadM = { 0.3, 0.6, 1.0 };

    // ── 电流：两种模式
    /// <summary>true = 用实测电流（秒级）；false = 由控温点反算（分钟级，可预测新几何）</summary>
    public bool UseMeasuredCurrent = true;

    /// <summary>
    /// ⚠ **这三个数不是实测值** —— 是模型自己在实况保温下反算出来的（§4.2g：1663/1519/1466，
    /// §4.2l：1655）。现场至今**没有给过电流实测**（§6 待补：稳态实测电流）。
    ///
    /// 保留字段名是为了不破坏算例文件，但读结果时必须记得：用它跑出来的
    /// 「实测电流模式」其实是「模型电流模式」，两边同源，**不构成任何验证**。
    /// 用户只给过定性判断「稳态电流密度远低于 10 A/mm²」，而这组数对应 J≈10.3 ——
    /// 即它们本身就被认为偏大（§4.2l）。真拿到实测值请直接覆盖本数组。
    /// （§7 的教训「示例参数被当成实测」，这里差点又犯一次。）
    /// </summary>
    public double[] MeasuredCurrentA = { 1655, 1510, 1446 };

    /// <summary>
    /// **解析几何**的法兰（长度 = 段数+1）。非空时**优先于** <see cref="FlangeFile3dm"/>。
    ///
    /// 用途：参数化搜索阶段用它遍历形状（圆盘半径、舌片长宽、阶梯厚度分布），
    /// 定下来之后再由 Rhino 出 .3dm 走 <see cref="FlangeFile3dm"/> 复核。
    /// 两条路进的是**同一个** ShellMesh → ShellCurrent → ShellThermal，
    /// 差别只在厚度场是解析给的还是量出来的（§4.2j 已做过两者的回归）。
    /// </summary>
    public FlangePlate[] FlangePlates = Array.Empty<FlangePlate>();

    // ── 法兰（每片一个 .3dm，长度 = 段数+1；可重复同一文件）
    public string[] FlangeFile3dm = Array.Empty<string>();
    public string FlangeLayer = "法兰";
    /// <summary>每片在 .3dm 中的平面 Y；NaN = 取该图层第一片</summary>
    public double[] FlangePlaneY = Array.Empty<double>();
    /// <summary>厚度场提取步长 mm（1.0 足够分辨槽与阶梯）</summary>
    public double ThicknessStepMm = 1.0;

    /// <summary>
    /// 每片的**厚度整体标度**（长度 = 片数，缺省全 1）。仅 .3dm 路径生效。
    ///
    /// 为什么需要它：.3dm 给的是**固定**厚度，而 C2（管根温差 &lt;10 K）要求
    /// 法兰厚度精确到 ±0.008 mm（ΔT 对厚度斜率约 1300 K/mm，§4.2w）——
    /// 不可能靠画图碰运气碰到。于是把厚度整体缩放当成自由度：
    /// 自动定厚求出的是「这张图纸的厚度要整体 ×k」，工程师照 k 改一版图即可。
    ///
    /// t=0 的格（轮廓外、管孔、开槽）乘任何数仍是 0，**槽与轮廓不受影响**。
    /// </summary>
    public double[] ThicknessScale = Array.Empty<double>();

    /// <summary>
    /// **逐级厚度标度**：`LevelScale[片][级]`。非空时**优先于** <see cref="ThicknessScale"/>。
    ///
    /// 为什么要分级：整体缩放只有 1 个自由度，只能调整片的热平衡（管根温差 C2），
    /// 动不了**局部过热**（C1）—— 后者取决于厚度在半径方向上怎么分配。
    /// 单位面积发热 = ρe·K²/t（K = J·t 是面电流，守恒），
    /// 所以**把某一级加厚，就按比例压低该级的单位面积发热**。
    /// 孔周那一级正是峰值所在，让优化器自己决定各级比例，才谈得上同时满足两条约束。
    ///
    /// 归级方式：把格子的原始厚度与 <see cref="LevelThicknessMm"/> 逐一比较，取最近的那一级。
    /// </summary>
    public double[][] LevelScale = Array.Empty<double[]>();

    /// <summary>`LevelThicknessMm[片][级]` = 该片各级的**原始**厚度 mm（由 PlateShapeAnalyzer 给出）</summary>
    public double[][] LevelThicknessMm = Array.Empty<double[]>();

    /// <summary>
    /// **逐片铜排夹持温度** °C（长度 = 片数；空 = 全部用 <see cref="DesignInputs.BusbarClampTempC"/>）。
    /// &lt;0 表示该片自由端（不夹冷）。
    ///
    /// 为什么必须逐片：共用片走 √3 倍电流、发热 ∝I² 是端片的 2.6 倍
    /// （实测自由端下 490 W vs 156 W）。共用片**需要**夹冷把多余的热带走，
    /// 端片则相反 —— 它连自己的散热都不够，再夹冷就只能从管子抽（§4.3c）。
    /// 四片用同一个夹持温度，等于用一把尺子量两个完全不同的东西。
    /// </summary>
    public double[] ClampTempC = Array.Empty<double>();

    // ── 网格
    public double MeshFineMm = 2.0, MeshCoarseMm = 11.0, MeshFineRadiusMm = 50.0;

    // ── 玻璃与验证
    public double GlassInC = 1150, GlassOutMeasuredC = 1130;

    // ── 判据（HANDOVER §4.2k 的判据体系）
    /// <summary>规程一：空管升温。关掉可省几秒，但那是**决定最小截面**的那条，默认开。</summary>
    public bool CheckRamp = true;
    public double RampFromC = 25, RampTargetC = 1150;

    /// <summary>
    /// 升温限时 h。**2026-08-14 由 3.0 改为 72.0** —— 3 h 是 §6 旧笔记里的假设，
    /// 而用户给的边界条件是「升温时间上限 **≤ 3 天**」（≈ 现状 20 °C/h：
    /// 25→1150 °C 需 56 h）。
    ///
    /// ⚠ 这个数不是无关紧要的显示项：`RampSolver.Reached` 的语义是
    /// **「限时内到不到」**，不是「渐近能不能到」。用 3 h 去判会把
    /// 「升得慢」误报成「升不到」—— 我据此写过一条「管壁 0.30 是硬物理墙」的结论，
    /// 是错的（见 §4.3i 的更正）。改限时后必须重跑所有带 CheckRamp 的算例。
    /// </summary>
    public double RampHours = 72.0;
    /// <summary>管根温差目标上限 K（③）。下限恒为 0：温差必须为正，即法兰比管冷。</summary>
    public double RootDeltaMaxK = 10.0;

    // ── 段↔法兰外层耦合的数值参数（见 LineRunner.Run 里为什么必须欠松弛）
    /// <summary>欠松弛因子。1.0 = 裸 Picard，在法兰倒灌的正反馈下会发散。</summary>
    public double CoupleRelax = 0.35;
    public int CoupleMaxRounds = 15;
    /// <summary>收敛判据：相邻两轮管根温度变化 K</summary>
    public double CoupleTolK = 1.0;

    /// <summary>
    /// **无法兰基线**的两端管温缓存 `[段][0=左,1=右]`（空 = 由 LineRunner 自己算）。
    ///
    /// 基线只依赖**管几何 / 保温 / 控温点**，与法兰几何无关 ⇒ 外层搜索里
    /// 只需在「管壁或管保温变了」时重算一次。不缓存的话每次整线解要多跑 4 次基线，
    /// 阶梯搜索直接慢 5 倍。
    /// </summary>
    public double[][] BaselineRootC = Array.Empty<double[]>();

    /// <summary>其余物性、保温、电气、环境沿用 DesignInputs</summary>
    public DesignInputs Base = new();

    /// <summary>现状整线铂重基准 g（--geom 校核值），用于算省铂率</summary>
    public double BaselineMassG = 7141.0;

    public int SegmentCount => SetpointC.Length;
    public int FlangeCount => LineSolver.FlangeCount(SegmentCount);
}

public sealed class SegmentOut
{
    public string Name = "";
    public double SetpointC, CurrentA, PowerW, TubeJAPerMm2;
    /// <summary>
    /// 管根温度 °C 与温差 K。
    ///
    /// ⚠⚠ 2026-08-14 修的一个**贯穿全项目**的 bug：这两个量原本只取
    /// <c>SolveResult.TFlangeAC</c>（= tm[0]，**左端**）。而每段有**两片**法兰，
    /// 右端的 <c>TFlangeBC</c> 算了却从未被用过。
    /// 实测 HC3 两端是 1040.2 / 1034.0 °C，判据只报了对应 1040.2 的 +9.8 ——
    /// **另一端差 16 K，从来没被检查过**；且最后一片法兰（出口端片）拿到的还是
    /// HC3 的**左端**温度，完全是另一头。
    /// ⇒ 现在 <see cref="RootDeltaK"/> 取**两端中较差的那个**，两端值另存。
    /// </summary>
    public double TRootC, RootDeltaK, GlassInC, GlassOutC, MassG;
    /// <summary>左端（A）与右端（B）的管根温度 °C —— 两片不同的法兰各贴一端</summary>
    public double TRootAC, TRootBC;
    /// <summary>两端各自的管根温差 K（控温点 − 该端温度）</summary>
    public double RootDeltaAK, RootDeltaBK;

    /// <summary>
    /// **无法兰基线**下两端的管温 °C（同几何、同保温、同段间耦合，仅把法兰抽热置零）。
    ///
    /// ⚠⚠ 为什么必须有它（2026-08-14/15）：原来的 C2 判「管根温度偏离**本段控温点** ≤10 K」，
    /// 可共用法兰处的管温是由**两侧控温点**定的 —— 接上段间导热后实测 HC1|HC2 接头停在
    /// **1116 °C**（1150 与 1080 的中间），偏离本段控温点 34 K，**与法兰设计无关**。
    /// 用那个靶子做优化，等于让法兰去背控温点梯度的锅：我为此调了几小时法兰几何。
    ///
    /// ⇒ C2 的正确口径是**法兰造成的增量**：`基线温度 − 实际温度`。
    ///   控温点梯度是设计要的，不是缺陷；法兰挖的坑才是法兰的责任。
    /// </summary>
    public double BaseTRootAC = double.NaN, BaseTRootBC = double.NaN;
    /// <summary>法兰造成的**增量**温降 K（正 = 法兰把该端拉冷了）。两端取较差者。</summary>
    public double FlangeDipK = double.NaN;
    public double[] X = Array.Empty<double>();
    public double[] TMetal = Array.Empty<double>();
    public double[] TGlass = Array.Empty<double>();
}

public sealed class FlangeOut
{
    public string Name = "";
    public bool Shared;
    public double CurrentA, MassG, JMaxAPerMm2, Phi, QFromTubeW, TMaxC, TMinC, TTabEndC;
    /// <summary>自身焦耳热与自身散热 W —— Φ = QGen/QLoss 的两个分子分母，判 §4.2k 时要看得见</summary>
    public double QGenW, QLossW;
    /// <summary>
    /// 从舌片末端流进铜排夹的热 W —— **铜排冷却要按这个数选型**。
    ///
    /// 由能量恒等式取：Q_夹持 = Q_发热 + Q_从管吸热 − Q_表面散热。
    /// 不逐面累加（§7：阶梯状边界会漏配对，实测偏小 14 %）。
    /// 夹持温度 &lt; 0（无夹冷）时为 0。
    /// </summary>
    public double QClampW;
    /// <summary>能量闭合残差 W —— 应接近 0，显著非零说明场解有问题</summary>
    public double EnergyResidualW;
    /// <summary>
    /// ★ 分区能量账（圆盘 / 舌片，以切点为界）—— 见 <see cref="ShellThermalResult.QGenDiscW"/>。
    /// 整片一个「发热 &lt; 散热」指不出该动哪一段，而两段的杠杆方向相反。
    /// </summary>
    public double QGenDiscW, QLossDiscW, QGenTabW, QLossTabW, TDiscMeanC, TTabMeanC;
    /// <summary>分区峰值温度 —— 判据②要用 <see cref="TDiscMaxC"/>，见 ShellThermalResult 同名注释</summary>
    public double TDiscMaxC, TTabMaxC;
    /// <summary>圆盘峰值的**位置与局部电流**，见 ShellThermalResult 同名注释（判据 ②″ 的病灶定位）</summary>
    public double DiscMaxXMm = double.NaN, DiscMaxZMm = double.NaN,
                  DiscMaxRMm = double.NaN, DiscMaxJAPerMm2 = double.NaN,
                  DiscMaxThickMm = double.NaN;
    /// <summary>本片贴着的管根温度 °C（管孔定温边界）。TMaxC − TRootC &gt; 0 即「法兰比管热」</summary>
    public double TRootC;
    public double AreaMm2, VolumeMm3;
    public int CellCount;
    public ShellMesh? Mesh;
    public double[] JField = Array.Empty<double>();
    public double[] TField = Array.Empty<double>();
    /// <summary>
    /// 各级的**局部最高温** °C（与 LineCase.LevelThicknessMm 同序）。
    /// 逐级定厚要靠它：知道是**哪一级**在过热，才知道该加厚哪一级。
    /// </summary>
    public double[] LevelTMaxC = Array.Empty<double>();
    /// <summary>各级的当前厚度 mm（已含标度），供界面与报告显示</summary>
    public double[] LevelThickMm = Array.Empty<double>();
    public string Source = "";        // 用了哪个 .3dm
}

/// <summary>
/// 判据的**分级**。HANDOVER §4.2k 之后三者不再等价：
/// 硬安全线越了就是烧断，目标越了只是不够好，参考量根本不参与判定。
/// 早先把三者混在一张表里，导致「J 越限」与「Φ&gt;1」被同等对待 —— 前者可能只是限值本身存疑
/// （§4.2i：J_allow=10 的物理依据未定），后者是确凿的失效模式。
/// </summary>
public enum CheckKind
{
    /// <summary>硬安全线：越界即失效，没有折衷余地</summary>
    HardSafety,
    /// <summary>设计目标：越界是方案不够好，可与省铂权衡</summary>
    Target,
    /// <summary>参考量：只报数，Ok 恒为 true</summary>
    Reference
}

public sealed class ConstraintOut
{
    public string Name = "", Where = "", Unit = "", Note = "";
    public double Actual, Limit;
    public bool Ok;
    /// <summary>true = 实际值须 ≤ 限值；false = 须 ≥ 限值</summary>
    public bool LessIsBetter = true;
    public CheckKind Kind = CheckKind.Target;
    /// <summary>数据不足以判定（如纯铂在 1100 °C 以下无持久强度实测，见 §6 待补 ③）</summary>
    public bool Undetermined;
}

public sealed class LineResult
{
    public SegmentOut[] Segments = Array.Empty<SegmentOut>();
    public FlangeOut[] Flanges = Array.Empty<FlangeOut>();
    public ConstraintOut[] Checks = Array.Empty<ConstraintOut>();
    public double TubeMassG, FlangeMassG, TotalMassG, BaselineMassG, SavingPct;
    public double GlassDropModelK, GlassDropMeasuredK;
    public readonly List<string> Notes = new();
    public bool Ok = true;
    /// <summary>段↔法兰外层耦合是否收敛。**为 false 时表内所有数值一律不可引用。**</summary>
    public bool Converged;
    public string Message = "";

    // ────────────────────────────────────────────────────────────────
    // ★★★★ 判据的**唯一来源**（2026-08-15）
    //
    // 此前判据定义散在三处：`Judge`、定尺寸器的控制律、汇总表的判定逻辑 ——
    // 同一件事写三遍。改口径时改了三次、每次都漏一处，产生
    // 「判据表说过、汇总表说不过」这种自相矛盾且**不报错**的输出（连错四次）。
    // ⇒ 现在 Judge 出结果，**其余一律只读下面这几个访问器**，不得自行重算。
    // ────────────────────────────────────────────────────────────────

    /// <summary>判据名前缀常量 —— 引用判据只准用它们，不准写字符串字面量</summary>
    public static class Key
    {
        public const string Ramp = "① 升温";
        public const string NetFlux = "②′管孔净流入";      // B：热流方向本身
        public const string DiscTemp = "②″圆盘区最高温";     // C：贴管子那一圈
        public const string FlangeDip = "③ 法兰增量温降";    // 法兰挖的坑
    }

    public ConstraintOut? Find(string keyPrefix)
        => Checks.FirstOrDefault(c => c.Name.StartsWith(keyPrefix, StringComparison.Ordinal));

    /// <summary>某条判据的实测值（找不到则 NaN）</summary>
    public double ValueOf(string keyPrefix) => Find(keyPrefix)?.Actual ?? double.NaN;

    /// <summary>全部**硬安全线**是否通过（**无法判定 ≠ 通过**）</summary>
    public bool HardOk => Checks.Where(c => c.Kind == CheckKind.HardSafety)
                                .All(c => c.Ok && !c.Undetermined);

    /// <summary>
    /// 硬安全线 + 设计目标是否全部通过 —— **可交付的唯一判定**。
    ///
    /// ⚠⚠ 「**无法判定**」一律**不算通过**。
    /// 原来写成 `c.Ok || c.Undetermined`，于是基线算不出来时 ③ 是 NaN/Undetermined，
    /// 却被计入通过 —— 实测阶梯据此报出「管壁 1.5 ✓ 全过」，而同一行的
    /// 增量降 max 是 +32（上限 10）。**判不了被当成判过了。**
    /// 这是「安静地给出可信外观的错误结果」家族的第五个成员
    /// （前四：两端抽热取平均、C2 只判左端、段间无导热、判据整条消失）。
    /// </summary>
    public bool AllOk => Converged && Checks
        .Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)
        .All(c => c.Ok && !c.Undetermined);

    /// <summary>没过的判据（含**无法判定**，标注区分）。供报告直接引用，不要另行拼装。</summary>
    public string[] Failed => Checks
        .Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target && (!c.Ok || c.Undetermined))
        .Select(c => c.Undetermined
                   ? $"{c.Name} **无法判定**"
                   : $"{c.Name} {c.Actual:0.0}/{c.Limit:0.0}").ToArray();
}

/// <summary>
/// **整线求解的唯一入口** —— CLI 与 WinForms 都只调 <see cref="Run"/>，
/// 于是两边不可能跑出不同结果（此前 CLI 与 UI 各自拼装流程，是长期的不一致来源）。
///
/// 流程：
///   ① 逐段定电流：实测模式直接取；反算模式用 SegmentSolver 二分使中点达设定温度
///   ② 逐段解管温：得管根温度、轴向剖面、玻璃出口温度（串联到下一段）
///   ③ 逐片解法兰：厚度场(Rhino, 带缓存) → 变步长壳网格 → 电流场 → 温度场
///      共用片电流用 §4.2h 的 √(I₁²+I₂²+I₁I₂)
///   ④ 汇总质量、判定约束
/// </summary>
public static class LineRunner
{
    public static LineResult Run(LineCase c, IProgress<string>? progress = null,
                                 CancellationToken cancel = default)
    {
        // ── ★ 先算**无法兰基线**：同几何、同保温、同段间耦合，只把法兰抽热置零。
        //   C2 要判的是「法兰挖了多深的坑」，不是「偏离本段控温点多少」——
        //   后者在共用法兰处由两侧控温点决定，法兰管不着（见 SegmentOut.BaseTRootAC）。
        //   基线只依赖管几何/保温/控温点，与法兰热解无关 ⇒ 每个构型算一次即可。
        var baseline = new (double A, double B)[c.SegmentCount];
        string baseFailMsg = "";
        // ⚠ 缓存必须校验**内容**，不能只看长度：失败时写进去的是 (NaN,NaN)，
        //   长度照样够 ⇒ 会把一次失败永久固化。含 NaN 一律重算。
        bool cacheOk = c.BaselineRootC.Length >= c.SegmentCount;
        for (int i = 0; cacheOk && i < c.SegmentCount; i++)
            if (c.BaselineRootC[i].Length < 2 ||
                double.IsNaN(c.BaselineRootC[i][0]) || double.IsNaN(c.BaselineRootC[i][1]))
                cacheOk = false;
        if (cacheOk)
        {
            for (int i = 0; i < c.SegmentCount; i++)
                baseline[i] = (c.BaselineRootC[i][0], c.BaselineRootC[i][1]);
        }
        else
        {
            var zero = new double[c.SegmentCount];
            var zeroLR = new (double L, double R)[c.SegmentCount];
            (double L, double R)[]? bnb = null;
            LineResult? br = null;
            // ⚠ 基线的段间耦合必须与主解**同样处理**：欠松弛 + 收敛判据。
            //   原来只跑 4 轮、且端温直接赋值（**裸 Picard**）—— 而主解那边的注释
            //   早写明「裸 Picard 会发散，必须欠松弛」。两边收敛程度不同，
            //   就会差出一个**与法兰无关的系统性偏移**：实测 ③ 恒为 31.5±0.4 K，
            //   而净流入从 +1 W 到 +7 W（差 7 倍）它纹丝不动 ——
            //   **不随因变量变，就不是那个因造成的**。
            double wBase = c.CoupleRelax;
            for (int k = 0; k < 30; k++)
            {
                br = RunOnce(c, null, cancel, zero, zeroLR, bnb);
                if (!br.Ok) break;
                var nb2 = new (double L, double R)[c.SegmentCount];
                for (int i = 0; i < c.SegmentCount; i++)
                    nb2[i] = (i == 0 ? double.NaN : br.Segments[i - 1].TRootBC,
                              i == c.SegmentCount - 1 ? double.NaN : br.Segments[i + 1].TRootAC);
                if (bnb is null) { bnb = nb2; continue; }
                double dmax = 0;
                for (int i = 0; i < c.SegmentCount; i++)
                {
                    double nl = double.IsNaN(nb2[i].L) ? double.NaN
                              : (1 - wBase) * bnb[i].L + wBase * nb2[i].L;
                    double nr = double.IsNaN(nb2[i].R) ? double.NaN
                              : (1 - wBase) * bnb[i].R + wBase * nb2[i].R;
                    if (!double.IsNaN(nl)) dmax = Math.Max(dmax, Math.Abs(nl - bnb[i].L));
                    if (!double.IsNaN(nr)) dmax = Math.Max(dmax, Math.Abs(nr - bnb[i].R));
                    bnb[i] = (nl, nr);
                }
                if (dmax < c.CoupleTolK) break;
            }
            for (int i = 0; i < c.SegmentCount; i++)
                baseline[i] = br is { Ok: true }
                            ? (br.Segments[i].TRootAC, br.Segments[i].TRootBC)
                            : (double.NaN, double.NaN);
            // ★ 基线算不出来时必须**说出原因**：否则 ③ 只会显示 NaN，
            //   而查不到是哪一步挂了（这条判据一度因此被当成「通过」）。
            if (br is not { Ok: true })
                baseFailMsg = br?.Message ?? "基线子解未返回结果";
            // 回写缓存：同一 LineCase 再被调用时不必重算（外层搜索靠这个提速 5 倍）
            c.BaselineRootC = baseline.Select(b => new[] { b.A, b.B }).ToArray();
        }

        var res = RunOnce(c, progress, cancel, null, null, null, baseline);
        if (!res.Ok) return res;
        if (baseFailMsg.Length > 0) res.Notes.Add("★ 无法兰基线失败 ⇒ 判据③无法判定：" + baseFailMsg);

        // ── 外层耦合：段 ↔ 法兰。首轮段解用抽热 0，拿到壳温度场后回灌重解。
        //
        // ★ 必须**欠松弛**。这个不动点自带正反馈：法兰热 ⇒ 向管根倒灌 ⇒ 管根更热 ⇒
        //   法兰边界温度更高 ⇒ 法兰更热。裸 Picard（ω=1）在该反馈下发散，
        //   现役几何上实测三轮后管根温差还有 204 K，输出的每个数都不可信 ——
        //   而那正是曾被读成「模型判现役设备烧断」的那批数。
        //   欠松弛不改变不动点，只改变到达方式：**若加了松弛仍发散，那才是物理上的热失控**。
        double omega = c.CoupleRelax;
        double[]? draws = null;
        (double L, double R)[]? drawsLR = null;
        (double L, double R)[]? nbT = null;      // 段间端温（欠松弛，同上）
        double delta = double.NaN;
        for (int outer = 0; outer < c.CoupleMaxRounds; outer++)
        {
            cancel.ThrowIfCancellationRequested();
            var target = new double[c.SegmentCount];
            var targetLR = new (double L, double R)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                // 段 i 的两端分别是法兰 i 与 i+1，各贡献自己的抽热
                double a = res.Flanges[i].QFromTubeW, b = res.Flanges[i + 1].QFromTubeW;
                target[i] = 0.5 * (a + b);           // 仅作兼容/汇报用
                targetLR[i] = (a, b);                // ★ 两端各自回灌（原来取平均是 bug）
            }
            draws ??= new double[c.SegmentCount];
            drawsLR ??= new (double, double)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
            {
                draws[i] = (1 - omega) * draws[i] + omega * target[i];
                drawsLR[i] = ((1 - omega) * drawsLR[i].L + omega * targetLR[i].L,
                              (1 - omega) * drawsLR[i].R + omega * targetLR[i].R);
            }

            // ── 段间端温：段 i 的左邻是段 i−1 的**右**端，右邻是段 i+1 的**左**端。
            //    整线两头没有邻段 ⇒ NaN（退化为纯法兰抽热边界）。
            var nbNew = new (double L, double R)[c.SegmentCount];
            for (int i = 0; i < c.SegmentCount; i++)
                nbNew[i] = (i == 0 ? double.NaN : res.Segments[i - 1].TRootBC,
                            i == c.SegmentCount - 1 ? double.NaN : res.Segments[i + 1].TRootAC);
            nbT ??= nbNew;
            for (int i = 0; i < c.SegmentCount; i++)      // 与抽热同样欠松弛
                nbT[i] = (double.IsNaN(nbNew[i].L) ? double.NaN
                            : (double.IsNaN(nbT[i].L) ? nbNew[i].L
                               : (1 - omega) * nbT[i].L + omega * nbNew[i].L),
                          double.IsNaN(nbNew[i].R) ? double.NaN
                            : (double.IsNaN(nbT[i].R) ? nbNew[i].R
                               : (1 - omega) * nbT[i].R + omega * nbNew[i].R));

            progress?.Report($"外层耦合 {outer + 1}/{c.CoupleMaxRounds}（ω={omega:0.00}）：回灌法兰抽热 + 段间端温…");
            var next = RunOnce(c, progress, cancel, (double[])draws.Clone(),
                               ((double L, double R)[])drawsLR.Clone(),
                               ((double L, double R)[])nbT.Clone(), baseline);
            if (!next.Ok) return next;
            delta = Enumerable.Range(0, c.SegmentCount)
                .Max(i => Math.Abs(next.Segments[i].TRootC - res.Segments[i].TRootC));
            res = next;
            if (delta < c.CoupleTolK)
            {
                res.Notes.Add($"外层耦合 {outer + 1} 轮收敛（管根温差 {delta:0.00} K，ω={omega:0.00}）");
                res.Converged = true;
                break;
            }
        }
        if (!res.Converged)
        {
            res.Notes.Add($"★ 外层耦合 {c.CoupleMaxRounds} 轮未收敛（管根温差仍 {delta:0.0} K，ω={omega:0.00}）——" +
                          "本次结果的每个数都不可用：要么再降 ω / 加轮数，要么该工况确实热失控");
            res.Message = "段↔法兰耦合未收敛";
        }
        return res;
    }

    /// <summary>把无法兰基线写进结果，并算出**法兰造成的增量温降**（两端取较差者）。</summary>
    private static void ApplyBaseline(LineResult r, (double A, double B)[] baseline)
    {
        for (int i = 0; i < r.Segments.Length && i < baseline.Length; i++)
        {
            var s = r.Segments[i];
            s.BaseTRootAC = baseline[i].A; s.BaseTRootBC = baseline[i].B;
            double dA = baseline[i].A - s.TRootAC, dB = baseline[i].B - s.TRootBC;
            s.FlangeDipK = double.IsNaN(dA) || double.IsNaN(dB)
                         ? double.NaN : Math.Max(dA, dB);
        }
    }

    private static LineResult RunOnce(LineCase c, IProgress<string>? progress,
                                      CancellationToken cancel, double[]? drawIn,
                                      (double L, double R)[]? drawLR = null,
                                      (double L, double R)[]? nbT = null,
                                      (double A, double B)[]? baseline = null)
    {
        var res = new LineResult { BaselineMassG = c.BaselineMassG };
        int n = c.SegmentCount, nf = c.FlangeCount;
        if (n < 1) { res.Ok = false; res.Message = "段数不能为 0"; return res; }
        if (c.UseMeasuredCurrent && c.MeasuredCurrentA.Length < n)
        { res.Ok = false; res.Message = $"实测电流只给了 {c.MeasuredCurrentA.Length} 个，需要 {n} 个"; return res; }
        if (c.FlangeFile3dm.Length == 0 && c.FlangePlates.Length == 0)
        { res.Ok = false; res.Message = "未指定法兰几何（.3dm 或解析 FlangePlate 二选一）"; return res; }

        // 各段两端的法兰抽热 W（由壳温度场回灌）。首轮未知，置 0；
        // ★ 必须显式回灌：不设 FlangeDrawOverrideSet 时 SegmentSolver 会**静默回退到
        //   已作废的一维环形模型 FlangeRadial**（§5、§7 记过两次，这是第三次）。
        var drawW = drawIn ?? new double[n];

        // ── ①② 逐段
        var segs = new SegmentOut[n];
        var amps = new double[n];
        var segParams = new DesignInputs[n];   // 各段实际用的参数，判据 ①④ 要拿去复用
        double tg = c.GlassInC;
        for (int i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"段 {i + 1}/{n}：{(c.UseMeasuredCurrent ? "按实测电流求解" : "反算电流")}…");

            var p = SegmentSolver.Clone(c.Base);
            p.TubeIdMm = c.TubeIdMm; p.WallMinMm = c.WallMm; p.TubeLengthMm = c.SegLengthMm;
            p.SupportSpanMm = c.SegLengthMm; p.GradeName = c.GradeName;
            p.TSetC = c.SetpointC[i]; p.TGlassInC = tg;
            p.GlassHeadM = i < c.HeadM.Length ? c.HeadM[i] : 0;
            p.SizeWall = false;
            p.FlangeDrawOverrideW = drawW[i]; p.FlangeDrawOverrideSet = true;
            // 两端各挂各的（原来取平均是 bug，见 DesignInputs.FlangeDrawLeftW）
            if (drawLR is not null)
            { p.FlangeDrawLeftW = drawLR[i].L; p.FlangeDrawRightW = drawLR[i].R; }
            // 段间轴向导热：把相邻段的端温传进去（见 DesignInputs.NeighbourTempLeftC）。
            // 首轮 nbT 为 null ⇒ 退化成原来的「各解各的」，由外层迭代逐步接上。
            if (nbT is not null)
            { p.NeighbourTempLeftC = nbT[i].L; p.NeighbourTempRightC = nbT[i].R; }

            SolveResult sr;
            if (c.UseMeasuredCurrent)
            {
                // 实测模式：电流已知，不需要外层二分 —— 这是秒级的来源
                sr = SegmentSolver.SolveAtCurrent(p, c.MeasuredCurrentA[i]);
            }
            else
            {
                sr = SegmentSolver.Solve(p);
            }
            if (!sr.Ok) { res.Ok = false; res.Message = $"段 {i + 1} 求解失败：{sr.Message}"; return res; }

            amps[i] = sr.CurrentA;
            segParams[i] = p;
            double area = Math.PI * (Math.Pow(c.TubeIdMm * 0.5 + c.WallMm, 2)
                                     - Math.Pow(c.TubeIdMm * 0.5, 2));
            segs[i] = new SegmentOut
            {
                Name = $"HC{i + 1}",
                SetpointC = c.SetpointC[i],
                CurrentA = sr.CurrentA,
                PowerW = sr.PowerTotalW,
                TubeJAPerMm2 = sr.CurrentA / area,
                // 两端各算，判据取**较差**的那个（偏离控温点最多的）
                TRootAC = sr.TFlangeAC, TRootBC = sr.TFlangeBC,
                RootDeltaAK = c.SetpointC[i] - sr.TFlangeAC,
                RootDeltaBK = c.SetpointC[i] - sr.TFlangeBC,
                TRootC = Math.Abs(c.SetpointC[i] - sr.TFlangeAC)
                       >= Math.Abs(c.SetpointC[i] - sr.TFlangeBC) ? sr.TFlangeAC : sr.TFlangeBC,
                RootDeltaK = Math.Abs(c.SetpointC[i] - sr.TFlangeAC)
                           >= Math.Abs(c.SetpointC[i] - sr.TFlangeBC)
                           ? c.SetpointC[i] - sr.TFlangeAC : c.SetpointC[i] - sr.TFlangeBC,
                GlassInC = tg,
                GlassOutC = sr.TGlassOutC,
                MassG = area * c.SegLengthMm * Materials.PtDensity * 1e-6,
                X = sr.X, TMetal = sr.TMetal, TGlass = sr.TGlass
            };
            tg = sr.TGlassOutC;
        }
        res.Segments = segs;
        res.GlassDropModelK = c.GlassInC - tg;
        res.GlassDropMeasuredK = c.GlassInC - c.GlassOutMeasuredC;

        // ── ③ 逐片法兰
        var flanges = new FlangeOut[nf];
        for (int j = 0; j < nf; j++)
        {
            cancel.ThrowIfCancellationRequested();
            bool analytic = c.FlangePlates.Length > 0;
            var plate = analytic ? c.FlangePlates[Math.Min(j, c.FlangePlates.Length - 1)] : null;
            string file = analytic ? "" : c.FlangeFile3dm[Math.Min(j, c.FlangeFile3dm.Length - 1)];
            double planeY = j < c.FlangePlaneY.Length ? c.FlangePlaneY[j] : double.NaN;
            progress?.Report(analytic
                ? $"法兰 {j + 1}/{nf}：解析几何 + 建网格 + 解场…"
                : $"法兰 {j + 1}/{nf}：提厚度场 + 建网格 + 解场…");

            double holeR = c.TubeIdMm * 0.5 + c.WallMm;
            ShellMesh mesh;
            if (analytic)
            {
                // 管孔必须跟着管外径走，否则法兰与管子对不上
                plate!.HoleRadiusMm = holeR;
                mesh = FlangeMesher.Build(plate, 0, c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                                          c.Base.BusbarClampLengthMm);
            }
            else
            {
                var tf = Geometry3dm.LoadThickness(file, c.FlangeLayer, planeY, c.ThicknessStepMm);
                // 厚度标度：.3dm 的**形状**固定，但整体厚度可按比例缩放。
                // 这让「自动定厚」在 .3dm 模式下同样可用 —— 求出的不是绝对厚度，
                // 而是「你这张图纸的厚度要整体 ×k」，工程师照着改一版图即可。
                // t=0（无材料：轮廓外、管孔、开槽）乘任何数仍是 0，故槽与轮廓不受影响。
                double[]? lvS = j < c.LevelScale.Length ? c.LevelScale[j] : null;
                double[]? lvT = j < c.LevelThicknessMm.Length ? c.LevelThicknessMm[j] : null;
                bool perLevel = lvS is { Length: > 0 } && lvT is { Length: > 0 };
                double k = j < c.ThicknessScale.Length ? c.ThicknessScale[j] : 1.0;

                if (perLevel || Math.Abs(k - 1.0) > 1e-9)
                {
                    var scaled = new double[tf.T.Length];
                    for (int q = 0; q < tf.T.Length; q++)
                    {
                        double t0 = tf.T[q];
                        if (t0 <= 1e-6) { scaled[q] = 0; continue; }   // 无材料乘任何数仍是无材料
                        // ★ 两个标度**相乘**，不是互相覆盖：
                        //   ThicknessScale = 整片一个数（外层用它调热平衡 → 管根温差）
                        //   LevelScale     = 各级一个数（内层用它调比例 → 局部过热）
                        //   早先写成「有 LevelScale 就忽略 ThicknessScale」，
                        //   于是两层优化里外层那步完全不起作用。
                        double kk = k;
                        if (perLevel)
                        {
                            int best = 0;
                            for (int m = 1; m < lvT!.Length; m++)
                                if (Math.Abs(t0 - lvT[m]) < Math.Abs(t0 - lvT[best])) best = m;
                            kk *= lvS![Math.Min(best, lvS.Length - 1)];
                        }
                        scaled[q] = t0 * kk;
                    }
                    tf = new ThicknessField
                    {
                        X0 = tf.X0, Z0 = tf.Z0, Step = tf.Step,
                        Nx = tf.Nx, Nz = tf.Nz, T = scaled
                    };
                }
                mesh = FlangeMesher.BuildFromField(tf, holeR, 0,
                            c.MeshFineMm, c.MeshCoarseMm, c.MeshFineRadiusMm,
                            c.Base.BusbarClampLengthMm);
            }

            double iJoint = LineSolver.JointCurrentA(amps, j);
            var sc = ShellCurrent.Solve(mesh, iJoint,
                        Materials.PtResistivity(c.SetpointC[Math.Min(j, n - 1)]) * 1e3,
                        c.SetpointC[Math.Min(j, n - 1)]);

            // 管根温度取相邻段中较高者（保守：抽热更大）
            // ⚠ 每片法兰贴的是**具体哪一端**，不能笼统取 TRootC（那原本恒是左端）：
            //   片 0     → 段 0 的**左**端
            //   片 j（内）→ 段 j−1 的**右**端 与 段 j 的**左**端（两者相邻，取较热者：保守）
            //   片 n     → 段 n−1 的**右**端   ← 原来错取成左端
            double tRoot = j == 0 ? segs[0].TRootAC
                         : j >= n ? segs[n - 1].TRootBC
                         : Math.Max(segs[j - 1].TRootBC, segs[j].TRootAC);

            var p2 = SegmentSolver.Clone(c.Base);
            p2.TSetC = c.SetpointC[Math.Min(j, n - 1)];
            if (j < c.ClampTempC.Length) p2.BusbarClampTempC = c.ClampTempC[j];
            // 保温分界：解析几何用该片自己的分界（可为「全裸」= +∞ 之外），
            // .3dm 路径沿用现场实况「仅圆盘保温、舌片裸露」的切点。
            double insulX = analytic ? plate!.InsulBoundaryXResolved
                                     : new FlangePlate().InsulBoundaryXResolved;
            var th = ShellThermal.Solve(mesh, sc.JMagAPerMm2, p2, tRoot, insulX,
                                        symmetricInsul: analytic && plate!.TwoTabs,
                                        tabBoundaryX: analytic ? plate!.Tangent().X : double.NaN,
                                        tabInsulThickMm: analytic ? plate!.TabInsulThickMm
                                                                  : double.NaN);

            // 逐级峰值温度：按单元厚度归级，取该级内的最高温
            double[] lvTmax = Array.Empty<double>(), lvTh = Array.Empty<double>();
            if (j < c.LevelThicknessMm.Length && c.LevelThicknessMm[j] is { Length: > 0 } lvRef)
            {
                int L = lvRef.Length;
                lvTmax = new double[L]; lvTh = new double[L];
                var kk = j < c.LevelScale.Length && c.LevelScale[j] is { Length: > 0 } ks ? ks : null;
                for (int m = 0; m < L; m++)
                {
                    lvTmax[m] = double.NaN;
                    lvTh[m] = lvRef[m] * (kk is null ? 1.0 : kk[Math.Min(m, kk.Length - 1)]);
                }
                for (int q = 0; q < mesh.CellCount; q++)
                {
                    double tq = mesh.Thickness[q];
                    int best = 0;
                    for (int m = 1; m < L; m++)
                        if (Math.Abs(tq - lvTh[m]) < Math.Abs(tq - lvTh[best])) best = m;
                    if (double.IsNaN(lvTmax[best]) || th.T[q] > lvTmax[best]) lvTmax[best] = th.T[q];
                }
            }

            flanges[j] = new FlangeOut
            {
                LevelTMaxC = lvTmax, LevelThickMm = lvTh,
                Name = j == 0 ? "入口" : j >= n ? "出口" : $"{segs[j - 1].Name}|{segs[j].Name}",
                Shared = j > 0 && j < n,
                CurrentA = iJoint,
                MassG = mesh.VolumeMm3 * Materials.PtDensity * 1e-6,
                JMaxAPerMm2 = sc.JMaxAPerMm2,
                Phi = th.PhiOverall,
                QFromTubeW = th.QFromTubeW,
                QGenW = th.QGenW, QLossW = th.QLossW,
                // ★ 改用壳解的**直接通量**，不再用恒等式反推 —— 否则对账是循环论证
                QClampW = th.QToClampW,
                EnergyResidualW = th.EnergyResidualW, TRootC = tRoot,
                QGenDiscW = th.QGenDiscW, QLossDiscW = th.QLossDiscW,
                QGenTabW = th.QGenTabW, QLossTabW = th.QLossTabW,
                TDiscMeanC = th.TDiscMeanC, TTabMeanC = th.TTabMeanC,
                TDiscMaxC = th.TDiscMaxC, TTabMaxC = th.TTabMaxC,
                DiscMaxXMm = th.DiscMaxXMm, DiscMaxZMm = th.DiscMaxZMm,
                DiscMaxRMm = th.DiscMaxRMm, DiscMaxJAPerMm2 = th.DiscMaxJAPerMm2,
                DiscMaxThickMm = th.DiscMaxThickMm,
                TMaxC = th.TMaxC, TMinC = th.TMinC, TTabEndC = th.TTabEndMeanC,
                AreaMm2 = mesh.TotalArea, VolumeMm3 = mesh.VolumeMm3,
                CellCount = mesh.CellCount,
                Mesh = mesh, JField = sc.JMagAPerMm2, TField = th.T,
                Source = analytic
                    ? $"解析 Ø{2 * plate!.DiscRadiusMm:0}/舌{-plate.TabEndXMm:0}/t{plate.ThicknessMm:0.00}"
                    : System.IO.Path.GetFileName(file)
            };
            if (sc.ConservationError > 1e-3)
                res.Notes.Add($"{flanges[j].Name}：电流守恒误差 {sc.ConservationError:E2}，偏大");
            // ★ 熔点护栏：拟合到 3392 °C 才反号，求解器会给出 2900 °C 的「可行解」并闭合能量账
            if (th.OverMelt)
                res.Notes.Add($"✗ {flanges[j].Name}：峰值 {th.TMaxC:0} °C 已越过铂熔点 " +
                              $"{Materials.PtMeltC:0} —— **该解不存在**");
            else if (th.OverFitRange)
                res.Notes.Add($"⚠ {flanges[j].Name}：峰值 {th.TMaxC:0} °C 超出电阻率拟合区 " +
                              $"{Materials.PtFitMaxC:0} °C，数值系外推");
            if (!th.Converged)
                res.Notes.Add($"{flanges[j].Name}：温度场未收敛（残差 {th.Residual:E2}）");
        }
        res.Flanges = flanges;

        // ── ④ 汇总与判定
        res.TubeMassG = segs.Sum(s => s.MassG);
        res.FlangeMassG = flanges.Sum(f => f.MassG);
        res.TotalMassG = res.TubeMassG + res.FlangeMassG;
        res.SavingPct = c.BaselineMassG > 0
            ? (c.BaselineMassG - res.TotalMassG) / c.BaselineMassG * 100 : 0;

        // ★★ 次序：**数据必须在评判之前完整**。
        //   ③ 的输入是 SegmentOut.FlangeDipK，而它唯一的来源是 ApplyBaseline。
        //   原来 ApplyBaseline 在 Run() 里、RunOnce **返回之后**才调用 ——
        //   于是 Judge 评的是一个还没被填的字段，③ **永远**是 NaN/无法判定，
        //   与基线算得出算不出毫无关系（实测：基线子解 Ok=true，是次序错）。
        //   ⇒ 基线传进来，在 Judge 之前填好。
        //   不选「Judge 之后重评一次」：判据评两遍会产生「以哪遍为准」的歧义，
        //   而**同一件事有多个来源**正是本项目连错四次的结构性根源。
        if (baseline is not null) ApplyBaseline(res, baseline);
        res.Checks = Judge(c, res, segs, flanges, segParams);
        return res;
    }

    /// <summary>
    /// HANDOVER §4.2k 的判据体系（取代旧的以 J 为中心的那套）：
    ///
    /// <code>
    /// ① 升温：空管 3 h 到 1150 °C        ← 决定最小截面（额定电流）
    /// ② 法兰温度 ≤ 管温（Φ ≤ 1）         ← 硬安全线，不可越
    /// ③ 管根温差 0 &lt; ΔT ≤ 10 K          ← 优化目标，从 ② 的安全侧逼近
    /// ④ 强度利用率 ≤ 1                   ← 真实工况下极宽松
    /// </code>
    ///
    /// **③ 是单边的**：旧代码判 |ΔT| ≤ 10，于是 ΔT = −8 K（法兰比管热 8 K，正在往烧断走）
    /// 会判「✓ 通过」。ΔT ≤ 0 与 Φ &gt; 1 是同一件事的两个视角，两条都列，
    /// 因为一条按段给（看得出卡在哪段），一条按片给（看得出卡在哪片）。
    ///
    /// **J 不再单独判**（§4.2k）：它在 ① 里是额定工况的能力指标，稳态只是参考量；
    /// 且 J_allow = 10 的物理依据本身待定（§4.2i）。故降级为 Reference，只报数不判。
    /// </summary>
    private static ConstraintOut[] Judge(LineCase c, LineResult res, SegmentOut[] segs,
                                         FlangeOut[] flanges, DesignInputs[] segParams)
    {
        var checks = new List<ConstraintOut>();
        int n = segs.Length;

        // ── ① 升温：空管能否在限时内到目标温度
        //    法兰随管一起被加热，且**自身也发热**，故用本算例真实的法兰质量与自身焦耳热
        //    （RampSolver 据此反推法兰电阻）。这比 --ramp 另跑一次稳态耦合解取参考值更准。
        if (c.CheckRamp)
        {
            RampResult? worst = null; string where = "";
            for (int i = 0; i < n; i++)
            {
                double massPairG = flanges[i].MassG + flanges[i + 1].MassG;
                // ★ 折算到**段电流**基准：RampSolver 由 R_f = QGen_ref / I_ref² 反推法兰电阻，
                //   而它拿到的 I_ref 是段电流；共用片实际走的是 √3 倍的接头电流（§4.2h）。
                //   直接把共用片的 QGen 配段电流会把 R_f 高估 (√3)² = 3 倍。
                //   故逐片按各自电流折算 QGen → 段电流下的等效值，再取两片均值（RampSolver 内部 ×2）。
                double GenAtSegCurrent(FlangeOut f, double iSeg)
                    => f.CurrentA > 1e-9 ? f.QGenW * (iSeg / f.CurrentA) * (iSeg / f.CurrentA) : 0;
                double genRefW = 0.5 * (GenAtSegCurrent(flanges[i], segs[i].CurrentA)
                                        + GenAtSegCurrent(flanges[i + 1], segs[i].CurrentA));
                var rr = RampSolver.Solve(segParams[i], c.WallMm, massPairG, genRefW,
                                          segs[i].CurrentA, segs[i].SetpointC,
                                          c.RampFromC, c.RampTargetC, c.RampHours);
                // 最不利 = 升不到的优先，其次用时最长
                bool worse = worst is null
                    || (!rr.Reached && worst.Reached)
                    || (rr.Reached == worst.Reached &&
                        (rr.Reached ? rr.HoursToTarget > worst.HoursToTarget : rr.TPeakC < worst.TPeakC));
                if (worse) { worst = rr; where = segs[i].Name; }
            }
            if (worst is not null)
                checks.Add(new ConstraintOut
                {
                    Name = "① 升温 空管到目标", Unit = "h", Kind = CheckKind.HardSafety,
                    Actual = worst.Reached ? worst.HoursToTarget : double.NaN,
                    Limit = c.RampHours, Ok = worst.Reached && worst.HoursToTarget <= c.RampHours,
                    Where = where,
                    Note = worst.Reached
                        ? $"{c.RampFromC:0}→{c.RampTargetC:0} °C，J={worst.JAPerMm2:0.00}" +
                          (worst.StabilityLimited ? "（电流被热稳定极限压低，不是故障）" : "")
                        : worst.Note
                });
        }

        // ── ② 硬安全线：法兰温度 ≤ 管温（用户原话）。**按温度直接判，不用 Φ 代理**。
        //
        //    ⚠ §4.2k 写的「等价于 Φ ≤ 1」只在**舌片末端绝热**时成立。
        //      Φ = 自身发热 / 自身**表面**散热，不含铜排夹带走的导热。
        //      夹冷一开（BusbarClampTempC ≥ 0），法兰变冷 ⇒ 表面散热变小 ⇒ Φ **反而升高**，
        //      而它其实更安全了。实测到的反例：夹持 80 °C 时 HC2|HC3 片 Φ = 1.313，
        //      但管孔净流入 +47 W（仍在从管子抽热，方向安全）。
        //      故硬安全线判 T_max(法兰) − T_root(管)，Φ 降为佐证。
        var hottest = flanges.OrderByDescending(f => f.TMaxC - f.TRootC).First();
        checks.Add(new ConstraintOut
        {
            // ★ 用户 2026-08-15 拍板：判据 ② 取「**B + C 并列**」——
            //   B = ②′ 管孔净流入须为正（热流方向本身，直接量）
            //   C = ②″ 圆盘区不得高于管温（贴着管子那一圈，仍用温度）
            //   本条（**整片**逐点，含舌片）**降为参考量**：
            //   · 舌片中段离管子 50 mm，中间隔着通电+包保温+远端被 450 °C 铜排拽住的一长条铂，
            //     它比管热不代表有热流进管 —— §4.2m 早写过「拿它跟管根比是在比两个不相干的位置」。
            //   · 且它与 C2 **数学上互斥**：管孔一圈净流量≈0 ⇒ ∮q dθ=0 ⇒ q 必然有正有负
            //     ⇒ 必然存在比管热的点。要整圈都比管冷需冷点深 32 K，而 C2 只许 10 K。
            //   ⚠ 降级**不等于**放松对舌片的要求：舌片仍由**熔点**与**局部热失稳 J≤J_stab** 管着。
            Name = "· ② 法兰最高温 − 管温（整片，含舌片）", Unit = "K", Kind = CheckKind.Reference,
            Actual = hottest.TMaxC - hottest.TRootC, Limit = 0, Ok = true,
            Where = hottest.Name,
            Note = $"法兰 {hottest.TMaxC:0.0} °C vs 管根 {hottest.TRootC:0.0} °C；" +
                   $"Φ={hottest.Phi:0.000}（发热 {hottest.QGenW:0} / 表面散热 {hottest.QLossW:0} W）；" +
                   $"管孔净流入 {hottest.QFromTubeW:+0;-0} W" +
                   (hottest.TMaxC > hottest.TRootC
                        ? "。**热量向管子倒灌 —— 这是烧断的过程，不是数值不好看**" : "") +
                   (hottest.TMaxC > 1768 ? $" ★已超铂熔点 1768 °C" : "")
        });

        // ── ②″ 同一条安全线，但**只看贴着管子那一段**（圆盘区）。
        //
        // 为什么要单列而不是把 ② 改掉：② 取的是**整片**最高温。舌片包保温之后
        // （§4.3e 的新自由度）峰值就落到舌片上 —— 离管子几十毫米、中间还隔着圆盘，
        // 拿它跟管根比是在比两个不相干的位置。真正决定「热往不往管里灌」的是圆盘区。
        // **但 ② 不能因此删掉**：舌片跑多热本身仍要盯（熔点、局部失稳），
        // 而且一旦哪天圆盘重新成为峰值所在，② 与 ②″ 会自动重合。两条都报，谁不过都要交代。
        var hottestDisc = flanges.Where(f => !double.IsNaN(f.TDiscMaxC))
                                 .OrderByDescending(f => f.TDiscMaxC - f.TRootC).FirstOrDefault();
        if (hottestDisc is not null)
            checks.Add(new ConstraintOut
            {
                Name = "②″圆盘区最高温 − 管温", Unit = "K", Kind = CheckKind.HardSafety,
                Actual = hottestDisc.TDiscMaxC - hottestDisc.TRootC, Limit = 0,
                Ok = hottestDisc.TDiscMaxC <= hottestDisc.TRootC + 1e-6,
                Where = hottestDisc.Name,
                // ★ 判据必须自带**病灶位置**：只报差值时，「盘峰贴在管孔上」与
                //   「轮毂上被自身发热顶起一个尖峰」给出同一个数，却要用相反的旋钮去治。
                //   r≈管外径 且 J≈0 ⇒ 病在管侧；r 更大且 J 不为零 ⇒ 病在法兰侧。
                Note = $"圆盘区 {hottestDisc.TDiscMaxC:0.0} vs 管根 {hottestDisc.TRootC:0.0} °C；" +
                       $"峰位 r={hottestDisc.DiscMaxRMm:0.0} mm（x={hottestDisc.DiscMaxXMm:+0.0;−0.0}, " +
                       $"z={hottestDisc.DiscMaxZMm:+0.0;−0.0}）J={hottestDisc.DiscMaxJAPerMm2:0.00} " +
                       $"t={hottestDisc.DiscMaxThickMm:0.00} mm；" +
                       $"舌片区峰值 {hottestDisc.TTabMaxC:0.0} °C（另由熔点与局部失稳管）；" +
                       $"管孔净流入 {hottestDisc.QFromTubeW:+0;-0} W"
            });

        // ── ②′ 同一条安全线的管侧视角：**热不能往管子里灌**
        //
        // ⚠ 口径已改（2026-08-15）：原来判「管根温差（控温点 − 管根）须为正」，
        //   那是拿温差当「热流方向」的代理量 —— 而接上段间导热后，共用法兰处的管温
        //   由**两侧控温点**决定（实测基线本身就比本段控温点高 63 K，HC2 端被 HC1 拉起来），
        //   代理量整个被控温点梯度污染，判出 −63 K「法兰在加热管子」，其实法兰只加了几 K。
        //
        // 而「热往哪边流」有**直接量**：管孔净流入 QFromTubeW（>0 = 从管子抽热 = 安全）。
        // 直接量就在手里，没有任何理由再用代理量。
        var worstFlux = flanges.OrderBy(f => f.QFromTubeW).First();
        checks.Add(new ConstraintOut
        {
            Name = "②′管孔净流入 须为正", Unit = "W", Kind = CheckKind.HardSafety,
            Actual = worstFlux.QFromTubeW, Limit = 0, LessIsBetter = false,
            Ok = worstFlux.QFromTubeW > 0, Where = worstFlux.Name,
            Note = worstFlux.QFromTubeW <= 0
                 ? "★ 热正在往管子里灌 —— 这是烧断的过程"
                 : "法兰在从管子抽热，方向安全"
        });

        // ── ③ **法兰造成的增量温降** ≤ 上限
        //   ⚠ 口径已改（2026-08-15）：原来判「偏离本段控温点」，但接上段间导热后，
        //     共用法兰处的管温由**两侧控温点**决定（实测 HC1|HC2 接头停在 1116 °C
        //     = 1150 与 1080 的中间，偏离本段控温点 34 K），**与法兰设计无关**。
        //     让法兰去背控温点梯度的锅，等于给优化器一个它够不着的靶子。
        //   现在判的是「有法兰 vs 无法兰」的同位置之差 —— 那才是法兰的责任。
        // ⚠⚠ 判据只能「过 / 不过 / **无法判定**」，**绝不允许消失**。
        //   原来写成 `if (dips.Length > 0) checks.Add(...)` —— 基线算失败时这条
        //   整个不出现，于是 AllOk 少判一条还报「全过」。实测阶梯就这么虚报过一次：
        //   增量降 max 是 +32（上限 10）却判「✓ 全过」。
        //   **判据消失比判据不过危险得多**：不过会被看见，消失不会。
        var dips = segs.Where(s => !double.IsNaN(s.FlangeDipK)).ToArray();
        if (dips.Length > 0)
        {
            var deepest = dips.OrderByDescending(s => s.FlangeDipK).First();
            checks.Add(new ConstraintOut
            {
                Name = "③ 法兰增量温降 ≤ 上限", Unit = "K", Kind = CheckKind.Target,
                Actual = deepest.FlangeDipK, Limit = c.RootDeltaMaxK,
                Ok = deepest.FlangeDipK <= c.RootDeltaMaxK, Where = deepest.Name,
                Note = $"= 无法兰基线 − 实际（{deepest.BaseTRootAC:0.0}/{deepest.BaseTRootBC:0.0} " +
                       $"vs {deepest.TRootAC:0.0}/{deepest.TRootBC:0.0} °C）；控温点梯度不算在内"
            });
        }
        else
        {
            checks.Add(new ConstraintOut
            {
                Name = "③ 法兰增量温降 ≤ 上限", Unit = "K", Kind = CheckKind.Target,
                Actual = double.NaN, Limit = c.RootDeltaMaxK, Ok = false,
                Undetermined = true, Where = "—",
                Note = "★ **无法判定**：无法兰基线没算出来（LineCase.BaselineRootC 为空且基线子解失败）。" +
                       "不要把它读成通过。"
            });
        }

        // 旧口径降为参考量：它反映的是控温点梯度，读的时候别当成法兰的问题
        var deepestAbs = segs.OrderByDescending(s => s.RootDeltaK).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 偏离本段控温点", Unit = "K", Kind = CheckKind.Reference, Ok = true,
            Actual = deepestAbs.RootDeltaK, Limit = c.RootDeltaMaxK, Where = deepestAbs.Name,
            Note = "参考：共用法兰处主要由**两侧控温点之差**决定，不是法兰造成的"
        });

        // ── ④ 强度利用率（管）。法兰不承重 —— 铂管由氧化铝管托底（§4.2d，用户确认），
        //     故此处只校核管，不校核法兰舌片。
        double utilMax = 0; string utilWhere = ""; bool undetermined = false; string undetNote = "";
        for (int i = 0; i < n; i++)
        {
            var mr = Mechanics.Check(segParams[i], c.WallMm, 2.0, new FlangePlate());
            Mechanics.ApplyAllowable(mr, segParams[i], segs[i].SetpointC, segs[i].SetpointC);
            if (double.IsNaN(mr.TubeAllowMPa))
            {
                undetermined = true;
                var g = MaterialDb.Get(segParams[i].GradeName);
                undetNote = $"{segs[i].Name} {segs[i].SetpointC:0} °C 落在 {segParams[i].GradeName} " +
                            $"持久强度实测区间 [{g.CreepTMinC:0}, {g.CreepTMaxC:0}] °C 之外（§6 待补 ③）";
                continue;
            }
            if (mr.TubeUtil > utilMax) { utilMax = mr.TubeUtil; utilWhere = segs[i].Name; }
        }
        checks.Add(new ConstraintOut
        {
            Name = "④ 管强度利用率", Unit = "—", Kind = CheckKind.Reference,
            Actual = utilMax, Limit = 1.0, Ok = true,
            Where = undetermined ? undetNote : utilWhere,
            Undetermined = undetermined,
            // ★ 2026-08-14 用户：「蠕变、铂金加热蒸发也都不必考虑」「都留有膨胀考量」
            //   ⇒ 持久强度不再是约束，本项降为**参考量**，不再因它判不可行。
            //   同时也就不再需要「落在实测区间之外 ⇒ 无法判定」那条护栏去卡整个方案。
            //   注意：熔点与局部热失稳**不属于**被划掉的那三条，仍然是硬判据。
            Note = "参考（用户 2026-08-14：蠕变不必考虑，本项降级）；" +
                   "法兰不承重（氧化铝管托底，§4.2d），故不校核舌片"
        });

        // ── 参考量：Φ（②的佐证，夹冷时会失真，见 ② 的说明）
        var worstPhi = flanges.OrderByDescending(f => double.IsNaN(f.Phi) ? -1 : f.Phi).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰自给率 Φ_max", Unit = "—", Kind = CheckKind.Reference, Ok = true,
            Actual = worstPhi.Phi, Limit = 1.0, Where = worstPhi.Name,
            Note = "参考：仅在舌片末端绝热时才等价于 ②；夹冷时 Φ 偏高但更安全"
        });

        // ── 参考量：J 只报数不判（§4.2k）
        var worstJf = flanges.OrderByDescending(f => f.JMaxAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 法兰 J_max", Unit = "A/mm²", Kind = CheckKind.Reference, Ok = true,
            Actual = worstJf.JMaxAPerMm2, Limit = c.Base.JAllowAPerMm2, Where = worstJf.Name,
            Note = "参考：J_allow=10 的物理依据待定（§4.2i），且 §4.2j 已证高 J 不等于局部过热"
        });
        var worstJt = segs.OrderByDescending(s => s.TubeJAPerMm2).First();
        checks.Add(new ConstraintOut
        {
            Name = "· 管 J", Unit = "A/mm²", Kind = CheckKind.Reference, Ok = true,
            Actual = worstJt.TubeJAPerMm2, Limit = c.Base.JAllowAPerMm2, Where = worstJt.Name,
            Note = c.Base.LossScale == 1.0
                ? "参考：散热未标定，本值系统性偏高（§4.2l）"
                : $"参考：散热已按 LossScale={c.Base.LossScale:0.000} 标定"
        });

        // ── 现场验证点：玻璃温降。这是全模型唯一一个拿实测校准的量，必须始终露出来。
        checks.Add(new ConstraintOut
        {
            Name = "· 玻璃温降 vs 实测", Unit = "K", Kind = CheckKind.Reference, Ok = true,
            Actual = res.GlassDropModelK, Limit = res.GlassDropMeasuredK, Where = "整线",
            Note = $"偏差 {res.GlassDropModelK - res.GlassDropMeasuredK:+0.0;-0.0} K"
        });

        return checks.ToArray();
    }
}
