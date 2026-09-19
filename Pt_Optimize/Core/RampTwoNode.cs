using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>升温时二次侧的闭环方式。三者在冷启时给出**完全不同**的电流。</summary>
public enum RampControl
{
    /// <summary>恒流：电流不随温度变，发热随 ρe(T) 上升 ⇒ 最烫的时刻在升温**末段**</summary>
    ConstantCurrent,
    /// <summary>恒压：I = V/R(T)，冷态 ρe 只有热态的 1/4.4 ⇒ 冷启电流约 2 倍、发热约 4 倍，危险在**开头**</summary>
    ConstantVoltage,
    /// <summary>恒功率：I = √(P/R(T))，介于两者之间</summary>
    ConstantPower,
    /// <summary>
    /// **以温度控制功率**（现场实际方式，用户 2026-08-11 确认）：功率是被调量，
    /// 按「管温跟住设定升温速率」实时反解 —— 冷态时散热几乎为零，所需功率因此极小，
    /// 电流只有几十安，与前三种把电流一上来就顶满是**完全不同的工况**。
    /// </summary>
    TemperatureRamp
}

public sealed class RampTwoNodeResult
{
    public RampControl Mode;
    public bool TubeReached;
    public double HoursToTarget = double.NaN;

    public double TTubeEndC, TFlangePeakC, TTubePeakC;
    /// <summary>整个升温过程中「法兰温度 − 管温」的最大值 K。&gt;0 即 §4.2k 的失效方向</summary>
    public double MaxFlangeMinusTubeK;
    /// <summary>上者出现的时刻 s，以及当时的管温 —— 用来判断危险窗口在开头还是末段</summary>
    public double TimeAtMaxDeltaS, TTubeAtMaxDeltaC;

    public bool FlangeMelts;
    public double MeltTimeS = double.NaN;

    public double CurrentStartA, CurrentEndA, PeakCurrentA;
    public double CapTubeJPerK, CapFlangeJPerK, CouplingWPerK;
    /// <summary>温控模式：电流是否被上限截住过（截住 ⇒ 跟不上设定速率）</summary>
    public bool CurrentClipped;

    /// <summary>
    /// ★ R48 G2（2026-09-15 Opus 5）：本次用的**铜排通道**（导度 W/K、夹持温度随法兰温度走的比例、冷端 °C）
    /// 与舌片区按多厚的**舌保温**算散热（NaN = 裸铂）。导度 0 = 本次没有这条通道（老调用点）。原样取自 <see cref="RampTwoNode.Inputs"/>。
    /// </summary>
    public double ClampConductanceWPerK, ClampFollowRatio, ClampColdEndC, TabInsulThickMm = double.NaN;
    /// <summary>
    /// ★ R48 G2 复审（2026-09-15 Opus 5）：本次管孔孔侧导度 W/K（NaN = 按孔壁几何式，老调用点）与表面散热的场标定系数（1 = 没标定，老调用点）。
    /// 原样取自 <see cref="RampTwoNode.Inputs.HolePlateConductanceWPerK"/> 与 <see cref="RampTwoNode.Model.SurfaceScale"/>。
    /// </summary>
    public double HolePlateConductanceWPerK = double.NaN, SurfaceScale = 1.0;

    /// <summary>
    /// ★ R48 G2（2026-09-15 Opus 5）：「法兰 − 管」峰值那一刻（<see cref="AtPeak"/>）与积分停下那一刻（<see cref="AtEnd"/>）
    /// 法兰节点与管节点的热流分量，由 <see cref="RampTwoNode.Model.Flows"/> 在那一刻的状态上算 —— 与积分用的是同一组函数。
    /// 峰值从没出现过正值时，<see cref="AtPeak"/> 是起点那一刻。
    /// </summary>
    public RampTwoNode.FlangeFlows AtPeak = new(), AtEnd = new();

    /// <summary>采样轨迹（供绘图/核对），列：时间 s、管温、法兰温、电流</summary>
    public List<(double t, double tt, double tf, double i)> Trace = new();
    public string Note = "";
}

/// <summary>
/// **两节点空管升温**：管 与 法兰各自一个温度节点，用孔壁导热耦合。
///
/// 为什么必须两节点 —— <see cref="RampSolver"/> 把法兰并进管子当**同一个温度**
/// （<c>NetFlangePairW(tC, …)</c> 在管温上取值），于是「法兰比管热」这个失效模式
/// 在那个模型里**结构性地不可能出现**。而 HANDOVER §4.2k 说的烧断正是这一条。
///
/// 三条让法兰在升温期跑到管子前面的机理，本模型都显式含着：
///
/// | 机理 | 在方程里的位置 |
/// |---|---|
/// | 冷态散热≈0 而发热照常 | 法兰散热项 q″(T_f) 在低温近乎为零，发热项 I²R_f 不随温度减小 |
/// | 法兰不背保温热容，管子背 | C_管 含保温层热容（常大于铂本身），C_法兰 只含薄薄一层 |
/// | 管子当不了散热器 | 耦合导度 G 由**管壁的翅片导度** √(k·A·β) 与孔壁导度串联，只有约 1 W/K |
///
/// ★ R48 G2（2026-09-15 Opus 5；常驻物理把关人第十一轮）补了两项，**必须一起补**（方向相反）：
///
/// | 补的项 | 在方程里的位置 | 没补时的量级 |
/// |---|---|---|
/// | 法兰经舌片流进铜排 | −G_夹·(T_法兰 − T_夹持(T_法兰))，G_夹 由稳态场标定（<see cref="CalibrateNode"/>） | 整面接触稳态夹持带走 159～274 W，占法兰发热 30～45 %（R48_压接整面接触AB_2026-09-14.txt） |
/// | 舌片包着舌保温 | 舌片区面积按 <see cref="Inputs.TabInsulThickMm"/> 走唯一配方 DesignScreen.PlateFluxWPerM2，舌保温热容计入法兰节点 | 按裸铂 ε 0.18 算，多算散热约 150 W 量级（物理把关人估） |
///
/// 两项的默认值（导度 0、舌保温 NaN）= 改动前的模型，逐位不变：命令行升温仪器与设计电流里的两节点对照仍走老模型
/// （命令行升温仪器没有稳态场；设计电流的对照在求解器定下角时没有场、判据表调用时有场但每段两片各跑一次、没接节点标定 —— 界面对照数已注明是旧模型；R48 G2 复审 2026-09-15 Opus 5 改正原句「那两处拿不到稳态场」）。
///
/// ★★ R48 G2 复审（2026-09-15 Opus 5；审查意见 blocker「法兰节点温度的口径前后不一致」）：第一版只把铜排通道与热容按节点格均温标定，
/// 发热的参考温度仍挂在管根温度上（节点温度下发热系统性少算 ρ(均温)/ρ(管根) ≈ 0.90）、管孔耦合仍按孔壁几何式建 ——
/// 少算的约 60 W 发热被错号的管来项补上，修后 0.29／0.09 K 是两处口径错互相抵消出来的数。现改为**一个节点口径、四项都在稳态场上标定**：
/// ⛔ 上一句后半「0.29／0.09 K 是两处口径错互相抵消出来的数」撤回（R48 G2 复审二 2026-09-15 Opus 5）：两处都改成节点口径后峰值 B2 0.288 → 0.387 K、误差预算 0.087 → 0.136 K，
///   仍落在升温起点（deliverable/R48_G2_复审_升温两节点逐项标定_{B2,误差预算}_2026-09-15.txt 丙与丁两版）—— 峰值近 0 不是抵消出来的，是整片平均的节点温度整个升温期都比管冷；
///   「少算约 60 W」按同一输出 J3 对照是 49 W（B2 标定点：配管根温度 461.69 W 对场 510.89 W，少 9.63 %）。
///
/// | 项 | 标定（节点格 = 场里不被边界钉住的格，<see cref="CalibrateNode"/>） | 老调用点（默认值，逐位不变） |
/// |---|---|---|
/// | 发热 | 参考电阻 = 节点格焦耳热 ÷ 场电流²，参考温度 = 节点温度 | 调用方给的参考电阻与参考温度 |
/// | 表面散热 | 配方（圆盘保温面 + 舌片区）× 系数 κ，κ = 节点格场散热 ÷ 配方在节点温度上的值（<see cref="Inputs.SurfaceCalibLossW"/>） | κ = 1 |
/// | 流进铜排 | G_夹 = 流进铜排的热 ÷（节点温度 − 夹持参考温度），夹持随法兰按比例升 | 没有这条通道 |
/// | 管孔 | 孔侧导度 = 管孔流入 ÷（管根温度 − 节点温度），与管壁翅片导度串联（场的孔边是定温，场里没有管侧那一段） | 孔壁几何式 k·A_孔/l |
///
/// ⇒ 模型在标定点（法兰 = 节点温度、电流 = 场电流、管节点温度 = 管根 + 管孔流入 ÷ 管壁翅片导度，<see cref="Model.TubeNodeTempAtRoot"/>）上逐项还原场的节点能量账。
/// ⚠ 单点标定：每一项都是过标定点的割线，分不开「导热」与「分布发热的偏置」（例如舌片自身焦耳热直接流进铜排、孔边电流拥塞的发热），离标定点越远越不准。
/// ⚠⚠ R48 G2 复审二（2026-09-15 Opus 5；审查意见 major）如实写明：上表「四项标定」**只在标定点上**逐项还原场，**不是**对第一版的改进 ——
///   沿升温轨迹与准静态壳解（同一片网格、同一边界，节点各项取节点格）比，比第一版偏得更远（两版输出各自的表一）：
///   | 量 | 第一版（只标铜排通道） | 复审（四项标定） |
///   |---|---|---|
///   | 集总法兰 − 壳解节点均温 最大差，B2 | 56.5 K（管温 502 °C） | 74.6 K（管温 502 °C） |
///   | 同上，误差预算 | 59.2 K | 108.9 K |
///   | 升温末段（管温约 1100 °C），B2 | +16.6 K | +34.9 K |
///   | 同上，误差预算 | +19.8 K | +39.3 K |
///   出处：第一版 deliverable/R48_G2_升温两节点补铜排通道_{B2,误差预算}_2026-09-15.txt；复审 deliverable/R48_G2_复审_升温两节点逐项标定_{B2,误差预算}_2026-09-15.txt。
///   病在管孔那一项：壳解上 Q孔 ÷（管温 − 节点均温）沿轨迹一路降到负值（误差预算管温 298→1098 °C：0.258、0.193、0.116、0.0215、−0.086 W/K，标定值 0.02166；
///   B2 0.228 → −0.008，标定值 0.1179）—— 它被分布发热的偏置主导，不是导度；同一状态只比方程（复审输出表二）误差预算管温 502 °C 集总管来 +1.97 W、壳解 +19.60 W。
///   审查粗算的仿射式 Q孔 ≈ 0.37·ΔT − 0.123·P发热（误差预算壳解 298、1098 °C 两点拟合；2026-09-15 Opus 5 按表一数复算一致）预测没参与拟合的 502/698/898 °C 得 20.7/17.3/5.6 W，壳解 19.6/15.0/3.3 W ⇒ 错的是模型形式。改法（切线导度 + 发热偏置）交物理把关人定，本类**没改**。
/// ⚠ 节点温度是整片（含越来越冷的舌片）的体积平均，**不代表片上最热处**：升温末段准静态壳解上最热铂比管温高、而整片平均仍比管冷
///   （复审输出表一：B2 管温 1101.6 °C 最热 1122.8 °C、节点均温 953.6 °C；误差预算 1098.3 °C 最热 1109.2 °C、节点均温 931.2 °C）⇒「节点温度 − 管温」这个指标看不到片上局部的危险，
///   判据表那一行在物理把关人裁决前暂不给数（LineRunner.Judge）。
///   ⛔ 原句「不代表孔边最热处：升温末段孔边已比管热」撤回（R48 G2 复审二，审查意见 major）：壳解孔面是定温边界，孔边温度就是管温；
///   最热铂在圆盘区以外（同一行 最热 1122.8 > 盘峰 1116.2 °C、1109.2 > 1108.1 °C；圆盘区按 r ≤ 盘半径圈）。
///   位置（R48 G2 复审二 2026-09-15 Opus 5 核过）：复审二探针表三（deliverable/R48_G2_复审二_升温两节点_{B2,误差预算}_{带玻璃,空管}_2026-09-15.txt）：升温末段最热格都在**舌片区、舌保温底下、紧贴圆盘外缘**（圆盘区按 r ≤ 30 mm 圈，管孔半径 25.8 mm）—— B2 带玻璃 管温 1101.6 °C 最热 1122.8 °C 在 r 35.0 mm（离管孔半径 9.2 mm，x −35，厚 3.10 mm，J 11.7 A/mm²）；误差预算带玻璃 1098.3 °C 最热 1109.2 °C 在 r 31.0 mm（5.2 mm，J 12.9）；B2 空管 1101.6 °C 最热 1137.6 °C 在 r 37.0 mm（11.2 mm）；误差预算空管 1098.3 °C 最热 1119.1 °C 在 r 33.0 mm（7.2 mm）；圆盘区峰值都在 r 29.0～29.8 mm。升温前段（管温 300～500 °C）最热格与管温相差不到 0.2 K，在孔面上或离管孔半径 1.2 mm 以内（片上别处都比管冷）。
///   ⚠⚠ 复审二还量到（同一批输出）：**空管到温稳态上两个设计的最不利片（HC2|HC3）管孔流向都与温差反号**（B2 −21.5 W、误差预算 −19.1 W），四项标定不了 ⇒
///   判据表空管那张表的升温行写算不出来；而升温轨迹的终点正是空管到温。带玻璃标定只是在一个不是轨迹终点的状态上成立。
///   ⚠ 2026-09-15 Opus 5（合并，复审后改）：出处层次 —— 本段引的 G2 各份输出（改前基线、第一版、复审、复审二）都是在 r48_G2 工作树上跑的（底板 460d3b，即 **F 合入前的配方**：
///     压接按形心整格定电位与定温、3·hFine 自相似细带；未计空管管腔轴向辐射）。合并树的生产配方（F 压接面上定温、缺省不铺细带；G3 空管计管腔辐射）下**没有重跑**，
///     这些数只读作「那份配方下量到的」，不是合并树的数；文件首跑在 r48_G2 工作树，已原样拷入本树 deliverable（2026-09-15 Opus 5（I 路） 核：deliverable/R48_合并拷入证据清单_2026-09-15.txt 第一、二批），原句「合并树里没有」作废。
///
/// 电学上：管与它两端的**端片**串在同一回路（同一电流）；**共用片**走的是
/// §4.2h 的叠加电流 <c>SharedFactor·I</c>，它不是回路里的串联元件，只是发热更凶。
///
/// 集总的代价：法兰盘内的径向温差被抹平。铂热扩散率 α = k/(ρc) ≈ 2.6e-5 m²/s，
/// 一分钟的扩散长度约 39 mm，与盘的径向尺寸（26→60 mm）同量级 ⇒
/// **分钟尺度上盘内确实是拉平的**，集总成立；秒级的孔周局部过热要看壳解。
///
/// 法兰节点温度 T_法兰 的口径（R48 G2 写明）：节点的热容是「节点所含格子的铂质量 × 比热」（外加保温热容），
/// 所以 C·dT/dt = 净热流里的 T 是这些格子的**质量加权平均温度**（铂密度均匀 ⇒ 体积加权 Σ T·面积·厚 ÷ Σ 面积·厚）。
/// 标定时从稳态场取的节点温度就是这个平均，格子范围与节点质量同一份（<see cref="CalibrateNode"/>；R48 G2 复审：四项都标定到这同一个温度上 —— 只在标定点上成立，见上面复审二那段）。
/// </summary>
public static class RampTwoNode
{
    /// <summary>铂熔点 °C</summary>
    public const double PtMeltingC = 1768.0;

    public sealed class Inputs
    {
        public double WallMm = 1.0;

        // ── 法兰（由壳网格 + ShellCurrent 给出，见 CLI --ramp2；**不需要**稳态热解）
        /// <summary>单片质量 g</summary>
        public double FlangeMassG;
        /// <summary>
        /// 单面面积 mm²，按「包圆盘保温 / 其余」分开（散热两面各一份）。
        /// ★ R48 G2（2026-09-15 Opus 5）：「其余」（名字仍叫 Bare，免得动老调用点）就是圆盘保温圈以外的舌片区，
        /// 它按 <see cref="TabInsulThickMm"/> 算散热 —— NaN 时才是字面意义的裸露。
        /// </summary>
        public double FlangeAreaInsulMm2, FlangeAreaBareMm2;
        /// <summary>
        /// 参考温度下单片电阻 Ω = QGen_ref / I_ref²。
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：参考电阻与参考温度必须是**同一个节点口径**的一对 —— 整线（LineRunner.FlangeLumped）给
        /// 节点格焦耳热 ÷ 场电流² 与节点温度（<see cref="NodeCalibration.ResistanceRefOhm"/>、<see cref="NodeCalibration.TNodeC"/>）；
        /// 原先给「整片发热 ÷ 电流²」配「管根温度」，而方程在节点温度上取电阻 ⇒ 发热少算 ρ(均温)/ρ(管根)。
        /// </summary>
        public double FlangeResistanceRefOhm;
        public double FlangeRefTempC = 1050;
        /// <summary>共用片的电流叠加系数：§4.2h 推导为 √3；工作簿用 1.5；端片为 1</summary>
        public double SharedFactor = Math.Sqrt(3.0);

        /// <summary>
        /// ★ R48 G2（2026-09-15 Opus 5）：<see cref="FlangeAreaBareMm2"/> 那一片（圆盘保温圈以外 = 舌片区）实际包的舌保温厚度 mm。
        /// 包不包按 DesignScreen.FlangeFaceInsulated 判，散热走 DesignScreen.PlateFluxWPerM2，保温热容与圆盘保温同法（Layer1 物性、×0.5 梯度因子）。
        /// NaN = 裸铂（老调用点的行为，逐位不变）。整线里由 LineRunner.FlangeLumped 取本片热解实际用的舌保温（LineRunner.PlateThermalInputs）。
        /// </summary>
        public double TabInsulThickMm = double.NaN;

        /// <summary>
        /// ★ R48 G2（2026-09-15 Opus 5）：**铜排通道**导度 W/K —— 法兰节点经舌片流进铜排的热 = G·(T_法兰 − T_夹持)。
        /// 由稳态场标定（<see cref="CalibrateNode"/>）；0 = 没有这条通道（老调用点，逐位不变）。
        /// </summary>
        public double ClampConductanceWPerK = 0;
        /// <summary>
        /// ★ R48 G2：升温期夹持温度随法兰温度走的比例 r：T_夹持 = T_冷端 + r·(T_法兰 − T_冷端)。
        /// 依据（假设）：夹持点是舌片导热与铜排导热之间的「分压点」—— 两段导度都不随时间变、铜排热惯性相对 56 h 的升温可略时，
        /// 夹持温度离冷端的距离与法兰离冷端的距离成定比。比例由稳态场标定（稳态时正好回到场里的夹持温度）。
        /// 热导边界下场里的冷端就是参考点 ⇒ r = 0。
        /// </summary>
        public double ClampFollowRatio = 0;
        /// <summary>★ R48 G2：铜排冷端温度 °C（DesignInputs.BusbarSinkTempC，与热导边界同一个冷端）。</summary>
        public double ClampColdEndC = 25;

        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：表面散热的稳态场标定点 —— 节点格的场散热 W（两面）与节点温度 °C。
        /// 给了 ⇒ 表面散热 = κ × 配方，κ = 本值 ÷ 配方在该温度上的值（<see cref="Model.SurfaceScale"/>）：配方给温度依赖与圆盘／舌片分区，κ 把「整片一个温度」的集总误差在标定点上还原到场。
        /// 任一为 NaN ⇒ κ = 1（老调用点逐位不变）。
        /// </summary>
        public double SurfaceCalibLossW = double.NaN, SurfaceCalibTempC = double.NaN;
        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：管孔耦合的**孔侧**导度 W/K，由稳态场标定（管孔流入 ÷（管根温度 − 节点温度），<see cref="NodeCalibration.HolePlateGWPerK"/>），
        /// 与管壁翅片导度串联（<see cref="Model.GCouple"/>）。常数（与铜排通道导度同法，割线值）。NaN ⇒ 孔壁几何式 k·A_孔/l（老调用点逐位不变）。
        /// ⚠ R48 G2 复审二（2026-09-15 Opus 5）：壳解沿升温轨迹证伪了「常数」—— 同一个比值从 0.258 降到 −0.086 W/K（误差预算），见类注释复审二那段；物理把关人裁决前不改。
        /// </summary>
        public double HolePlateConductanceWPerK = double.NaN;

        // ── 耦合几何
        public double HoleRadiusMm = 26.0;
        /// <summary>盘的等效外半径 mm（由净面积反算），只用于孔壁导热的特征长度</summary>
        public double PlateEqOuterRadiusMm = 60.0;
        public double FlangeThickMm = 2.0;

        // ── 工况
        public RampControl Mode = RampControl.ConstantCurrent;
        /// <summary>设计（额定）电流 A —— 恒压/恒功率模式下用它在**目标温度**处定 V 或 P</summary>
        public double DesignCurrentA;
        public double FromC = 25, TargetC = 1150, MaxHours = 3.0;

        /// <summary>温控模式的设定升温速率 K/h（现场值 20）</summary>
        public double RampRateKPerH = 20.0;
        /// <summary>温控模式的电流上限 A（二次侧能力）。≤0 表示用 DesignCurrentA</summary>
        public double MaxCurrentA = 0;

        /// <summary>R48 G2（2026-09-15 Opus 5）：浅拷贝（字段全是值类型）—— 探针拿生产那一份输入、只关掉新补的两项去复现老口径，不手抄输入。</summary>
        public Inputs Clone() => (Inputs)MemberwiseClone();
    }

    /// <summary>
    /// ★ R48 G2（2026-09-15 Opus 5）：某一刻两个节点的热流分量 W。符号约定：
    /// 法兰节点 <c>热容吸收 = 发热 − 表面散热 − 铜排通道 + 管来</c>；管节点 <c>热容吸收 = 发热 − 散热 − 管来</c>（管来 &gt; 0 = 管子在加热法兰）。
    /// </summary>
    public sealed class FlangeFlows
    {
        public double TimeS, TTubeC, TFlangeC, CurrentA;
        /// <summary>法兰自身焦耳热（共用片按叠加电流）</summary>
        public double GenW;
        /// <summary>表面散热（两面）= 圆盘保温面 + 舌片区</summary>
        public double SurfaceW, SurfaceDiscW, SurfaceTabW;
        /// <summary>经舌片流进铜排；以及这一刻假设的夹持温度（没有通道时 NaN）</summary>
        public double ClampW, ClampTempC = double.NaN;
        /// <summary>管 → 法兰（经孔壁）</summary>
        public double FromTubeW;
        /// <summary>法兰热容吸收 = C_法兰·dT/dt，与 C_法兰 J/K</summary>
        public double StorageW, CapJPerK;
        /// <summary>管节点：发热、散热、热容吸收</summary>
        public double TubeGenW, TubeLossW, TubeStorageW;
    }

    /// <summary>
    /// ★ R48 G2（2026-09-15 Opus 5）：两节点模型的**方程本体**（散热表、热容、耦合导度、电学、电流律、铜排通道），
    /// 从 <see cref="Solve"/> 的局部函数原样搬出成公开类 —— 门与探针在任意状态上取热流分量时调同一份，不手抄。
    /// 搬移不改算术（新补两项默认 0 时加减的是 0.0，逐位不变；R48 G2 复审加的表面标定系数默认 1.0、孔侧导度默认 NaN 走原几何式，同样逐位不变 ——
    /// 前后对照探针 R48G2RampClampChannelTests 的 J0 回归逐位核过甲乙丙三版）。
    /// </summary>
    public sealed class Model
    {
        private readonly DesignInputs _p;
        private readonly Inputs _g;
        private readonly LossTable _tubeLoss, _fluxBare, _fluxIns, _fluxTab;
        private readonly double _L, _areaTube, _massTube, _massFlange, _capInsulTube, _capInsulFlange, _capInsulTab;
        private readonly double _rhoRef, _vRef, _pRef, _iMax, _rateKPerS;
        private readonly double _surfScale;

        public Inputs In => _g;
        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：表面散热的场标定系数 κ = <see cref="Inputs.SurfaceCalibLossW"/> ÷ 配方（<see cref="FlangeSurfaceRecipeW"/>）在 <see cref="Inputs.SurfaceCalibTempC"/> 上的值；
        /// 没给标定点 = 1。配方为 0 或标定值非正时是 NaN（调用方据此判「标定不了」，LineRunner.FlangeLumped）。
        /// </summary>
        public double SurfaceScale => _surfScale;
        /// <summary>温控模式的电流上限 A</summary>
        public double CurrentCapA => _iMax;

        public Model(DesignInputs p, Inputs g)
        {
            _p = p; _g = g;
            double ri = p.TubeIdMm * 0.5e-3, w = g.WallMm * 1e-3, rOut = ri + w;
            _areaTube = Math.PI * (rOut * rOut - ri * ri);          // m²
            _L = p.TubeLength;
            double L = _L;

            // ── 散热表（空管：管内没有玻璃项）
            bool insulated = false;
            foreach (var lay in p.Layers) if (lay.Enabled && lay.ThicknessMm > 1e-6) insulated = true;
            double epsTube = insulated ? p.OuterEmissivity : p.PtEmissivity;

            const double tabTop = 2000.0;      // 表的上界；超铂熔点后积分本就停了
            _tubeLoss = new LossTable(p.TAmbC, tabTop, 80,
                t => Insulation.CylinderLoss(t, p.TAmbC, rOut, p.Layers, epsTube,
                                             p.Posture == Orientation.Vertical, L, p.LossScale).QPerLength);

            // ★ R48（2026-09-14，Opus 5；审查意见「圆盘保温 0 mm 时四个消费方物理含义不一致」）：法兰两张表面热流表改调**唯一配方**
            //   DesignScreen.PlateFluxWPerM2（特征长度 p.ConvCharLenM、包不包按 DesignScreen.FlangeFaceInsulated、两面都传风速）。
            //   修的病：保温面原先无条件走 PlateFlux，厚度 0 时退到外覆材料 ε=0.45，而同一判据表里的整片热稳定按裸铂 ε=0.18；
            //   保温面原先也不传风速（ShellThermal 传）。包着且默认风速 0 时调用参数与原来逐项相同 ⇒ 逐位不变。
            _fluxBare = new LossTable(p.TAmbC, tabTop, 80,
                t => DesignScreen.PlateFluxWPerM2(p, t, 0.0));
            _fluxIns = DesignScreen.FlangeFaceInsulated(p.FlangeInsulThickMm)
                ? new LossTable(p.TAmbC, tabTop, 80, t => DesignScreen.PlateFluxWPerM2(p, t, p.FlangeInsulThickMm))
                : _fluxBare;
            // ★ R48 G2（2026-09-15 Opus 5）：舌片区按本片舌保温（同一配方、同一判定）；不包 ⇒ 就是裸铂那张表（老口径逐位不变）。
            //   p.FlangeInsulThickMm 是调用方给的本片圆盘保温（LineRunner.FlangeLumped 按 LineCase.DiscInsulEffectiveAt 克隆；DesignCurrent 按片克隆）—— 2026-09-15 核过。
            _fluxTab = DesignScreen.FlangeFaceInsulated(g.TabInsulThickMm)
                ? new LossTable(p.TAmbC, tabTop, 80, t => DesignScreen.PlateFluxWPerM2(p, t, g.TabInsulThickMm))
                : _fluxBare;

            // ── 热容
            _massTube = Materials.PtDensity * _areaTube * L;             // kg
            _massFlange = g.FlangeMassG * 1e-3;                          // kg
            _capInsulTube = 0;
            {
                double r = rOut;
                foreach (var lay in p.Layers)
                {
                    if (!lay.Enabled || lay.ThicknessMm <= 1e-6) continue;
                    double rNext = r + lay.ThicknessMm * 1e-3;
                    // ×0.5 是梯度因子：保温内表面跟着金属走、外表面接近环境（与 RampSolver 同口径）
                    _capInsulTube += Math.PI * (rNext * rNext - r * r) * L
                                   * lay.DensityKgM3 * lay.CpJKgK * 0.5;
                    r = rNext;
                }
            }
            _capInsulFlange = 0;
            if (DesignScreen.FlangeFaceInsulated(p.FlangeInsulThickMm))   // R48（2026-09-14，Opus 5）：与散热同一判定（原 > 1e-6）
            {
                double volM3 = 2.0 * (g.FlangeAreaInsulMm2 * 1e-6) * (p.FlangeInsulThickMm * 1e-3);
                _capInsulFlange = volM3 * p.Layer1.DensityKgM3 * p.Layer1.CpJKgK * 0.5;
            }
            // ★ R48 G2（2026-09-15 Opus 5）：舌保温的热容同法计入法兰节点（与散热同一判定；不包 ⇒ 0，老口径逐位不变）
            _capInsulTab = 0;
            if (DesignScreen.FlangeFaceInsulated(g.TabInsulThickMm))
            {
                double volTabM3 = 2.0 * (g.FlangeAreaBareMm2 * 1e-6) * (g.TabInsulThickMm * 1e-3);
                _capInsulTab = volTabM3 * p.Layer1.DensityKgM3 * p.Layer1.CpJKgK * 0.5;
            }

            _rhoRef = Math.Max(1e-30, Materials.PtResistivity(g.FlangeRefTempC));

            // ★ R48 G2 复审（2026-09-15 Opus 5）：表面散热的场标定系数（见 SurfaceScale）；没给标定点 ⇒ 1.0，老口径逐位不变（x·1.0 = x）
            _surfScale = 1.0;
            if (!double.IsNaN(g.SurfaceCalibLossW) && !double.IsNaN(g.SurfaceCalibTempC))
            {
                double recipe = FlangeSurfaceRecipeW(g.SurfaceCalibTempC);
                _surfScale = recipe > 0 && g.SurfaceCalibLossW > 0 && double.IsFinite(g.SurfaceCalibLossW) ? g.SurfaceCalibLossW / recipe : double.NaN;
            }

            // 恒压/恒功率的定值：取**目标温度**处跑出设计电流所需的 V 或 P
            double rAtTarget = RCircuit(g.TargetC, g.TargetC);
            _vRef = g.DesignCurrentA * rAtTarget;
            _pRef = g.DesignCurrentA * g.DesignCurrentA * rAtTarget;

            _iMax = g.MaxCurrentA > 0 ? g.MaxCurrentA : g.DesignCurrentA;
            _rateKPerS = g.RampRateKPerH / 3600.0;
        }

        /// <summary>管单位长度散热 × 管长 W（空管）</summary>
        public double TubeLossW(double tt) => _tubeLoss.Eval(tt) * _L;

        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：表面散热的**配方值** W（两面各一份，mm² → m²）：圆盘保温面 + 舌片区，没乘场标定系数。
        /// 唯一配方 DesignScreen.PlateFluxWPerM2（圆盘按本片圆盘保温、舌片区按本片舌保温）。
        /// </summary>
        public double FlangeSurfaceRecipeW(double tf)
            => 2.0 * 1e-6 * (_g.FlangeAreaInsulMm2 * _fluxIns.Eval(tf)
                           + _g.FlangeAreaBareMm2 * _fluxTab.Eval(tf));
        /// <summary>单片表面散热 W = κ × 配方（<see cref="SurfaceScale"/>；没标定时 κ = 1，与改动前逐位相同）</summary>
        public double FlangeSurfaceW(double tf) => _surfScale * FlangeSurfaceRecipeW(tf);
        /// <summary>上式的圆盘保温面部分 W（只供分量打印）</summary>
        public double FlangeSurfaceDiscW(double tf) => _surfScale * (2.0 * 1e-6 * (_g.FlangeAreaInsulMm2 * _fluxIns.Eval(tf)));
        /// <summary>上式的舌片区部分 W（只供分量打印）</summary>
        public double FlangeSurfaceTabW(double tf) => _surfScale * (2.0 * 1e-6 * (_g.FlangeAreaBareMm2 * _fluxTab.Eval(tf)));
        /// <summary>★ R48 G2 复审：舌保温热容 J/K（两面、×0.5 梯度因子；不包 = 0）—— 门验「舌保温确实计入热容」时调它，不手抄配方</summary>
        public double CapInsulTabJPerK => _capInsulTab;
        /// <summary>★ R48 G2 复审：圆盘保温热容 J/K（同上）</summary>
        public double CapInsulDiscJPerK => _capInsulFlange;

        /// <summary>★ R48 G2：法兰温度为 tf 时假设的夹持温度 °C（见 <see cref="Inputs.ClampFollowRatio"/>）</summary>
        public double ClampTempC(double tf) => _g.ClampColdEndC + _g.ClampFollowRatio * (tf - _g.ClampColdEndC);

        /// <summary>★ R48 G2：经舌片流进铜排的热 W = G·(T_法兰 − T_夹持(T_法兰))；导度 ≤ 0 ⇒ 没有这条通道，恒 0</summary>
        public double ClampW(double tf)
            => _g.ClampConductanceWPerK > 0 ? _g.ClampConductanceWPerK * (tf - ClampTempC(tf)) : 0.0;

        public double CapTube(double t) => _massTube * Materials.PtCp(t) + _capInsulTube;
        public double CapFlange(double t) => _massFlange * Materials.PtCp(t) + _capInsulFlange + _capInsulTab;

        /// <summary>
        /// 耦合导度 G [W/K]：孔壁导热 与 管壁翅片导度 串联。
        /// 管侧用半无限翅片的入口导度 √(k·A·β)（HANDOVER §6 ② 的 |ΔT_dip| = D/√(kAβ) 同一式）
        /// </summary>
        public double GCouple(double tMean)
            => 1.0 / (1.0 / Math.Max(1e-9, GTubeFin(tMean)) + 1.0 / Math.Max(1e-9, GPlateSide(tMean)));

        /// <summary>管子一侧：半无限翅片入口导度 √(k·A·β) W/K（R48 G2 复审 2026-09-15 Opus 5 从 GCouple 拆出，算术不变）</summary>
        public double GTubeFin(double tMean)
        {
            double k = Materials.PtThermalK(tMean);                    // W/(m·K)
            double beta = Math.Max(1e-6, _tubeLoss.Slope(tMean));      // W/(m·K)，空管无玻璃项
            return Math.Sqrt(k * _areaTube * beta);                    // W/K（管子一侧）
        }

        /// <summary>
        /// 法兰一侧（孔侧）导度 W/K：给了场标定的 <see cref="Inputs.HolePlateConductanceWPerK"/> 就用它（常数）；
        /// 否则孔壁几何式 k·A_孔/l_特征（R48 G2 复审 2026-09-15 Opus 5 从 GCouple 拆出，几何式算术不变）。
        /// </summary>
        public double GPlateSide(double tMean)
        {
            if (!double.IsNaN(_g.HolePlateConductanceWPerK)) return _g.HolePlateConductanceWPerK;
            double k = Materials.PtThermalK(tMean);                    // W/(m·K)
            double aHole = 2 * Math.PI * (_g.HoleRadiusMm * 1e-3) * (_g.FlangeThickMm * 1e-3);  // m²
            double lChar = Math.Max(1e-3, (_g.PlateEqOuterRadiusMm - _g.HoleRadiusMm) * 0.5e-3);
            return k * aHole / lChar;                                  // W/K（法兰一侧）
        }

        /// <summary>
        /// ★ R48 G2 复审（2026-09-15 Opus 5）：稳态场标定点上的**管节点温度** °C —— 场的孔边定在管根温度 <paramref name="tRootC"/>，
        /// 而管节点是整根管（翅片那一段之外），两者差管壁翅片上的温降：T_管 = T_管根 + Q ÷ G_翅片((T_管 + T_法兰)/2)，取温与 <see cref="GCouple"/> 同一口径，迭代到 1e-9 K。
        /// 孔侧导度按场标定时，模型在 (T_管, <paramref name="tf"/>) 上的管来正好等于 <paramref name="qFromTubeW"/>（门与探针在标定点上对账用它）。
        /// </summary>
        public double TubeNodeTempAtRoot(double tRootC, double tf, double qFromTubeW)
        {
            double tt = tRootC;
            for (int it = 0; it < 200; it++)
            {
                double next = tRootC + qFromTubeW / Math.Max(1e-9, GTubeFin(0.5 * (tt + tf)));
                bool done = Math.Abs(next - tt) < 1e-9;
                tt = next;
                if (done) break;
            }
            return tt;
        }

        // ── 电学：管与两端**端片**串联（同一电流），共用片只是发热更凶
        public double RTube(double t) => Materials.PtResistivity(t) * _L / _areaTube;
        public double RFlange(double t) => _g.FlangeResistanceRefOhm * Materials.PtResistivity(t) / _rhoRef;
        public double RCircuit(double tt, double tf) => RTube(tt) + 2.0 * RFlange(tf);

        /// <summary>
        /// 温控模式：功率是被调量 —— 由「管子要跟住设定速率」的能量平衡反解电流
        ///   C_管·(dT/dt)_设定 = I²R_管 − Q_散热(T_管) − G·(T_管 − T_法兰)
        /// 冷态 Q_散热≈0 且 C·rate 极小 ⇒ 电流只有几十安，与顶满电流是两个世界。
        /// </summary>
        public double CurrentAt(double tt, double tf, double qLossTubeW, double qCoupleW)
        {
            switch (_g.Mode)
            {
                case RampControl.ConstantCurrent: return _g.DesignCurrentA;
                case RampControl.ConstantVoltage: return _vRef / Math.Max(1e-12, RCircuit(tt, tf));
                case RampControl.ConstantPower: return Math.Sqrt(_pRef / Math.Max(1e-12, RCircuit(tt, tf)));
                default:
                    double need = CapTube(tt) * _rateKPerS + qLossTubeW + qCoupleW;
                    double i = need <= 0 ? 0 : Math.Sqrt(need / Math.Max(1e-12, RTube(tt)));
                    return Math.Min(i, _iMax);
            }
        }

        /// <summary>★ R48 G2：在 (管温, 法兰温, 段电流) 这一状态上的全部热流分量（符号见 <see cref="FlangeFlows"/>）</summary>
        public FlangeFlows Flows(double timeS, double tt, double tf, double iSeg)
        {
            var f = new FlangeFlows { TimeS = timeS, TTubeC = tt, TFlangeC = tf, CurrentA = iSeg };
            double iFl = _g.SharedFactor * iSeg;
            f.GenW = iFl * iFl * RFlange(tf);
            f.SurfaceW = FlangeSurfaceW(tf);
            f.SurfaceDiscW = FlangeSurfaceDiscW(tf);
            f.SurfaceTabW = FlangeSurfaceTabW(tf);
            f.ClampW = ClampW(tf);
            f.ClampTempC = _g.ClampConductanceWPerK > 0 ? ClampTempC(tf) : double.NaN;
            f.FromTubeW = GCouple(0.5 * (tt + tf)) * (tt - tf);
            f.CapJPerK = CapFlange(tf);
            f.StorageW = f.GenW - f.SurfaceW - f.ClampW + f.FromTubeW;
            f.TubeGenW = iSeg * iSeg * RTube(tt);
            f.TubeLossW = TubeLossW(tt);
            f.TubeStorageW = f.TubeGenW - f.TubeLossW - f.FromTubeW;
            return f;
        }
    }

    /// <summary>
    /// ★★ R48 G2 复审（2026-09-15 Opus 5；审查意见 blocker「法兰节点温度的口径前后不一致」）：法兰节点在稳态场上的**逐项标定**结果（<see cref="CalibrateNode"/>）。
    /// 节点口径只有一个：节点格 = <see cref="NodeCell"/>，节点温度 <see cref="TNodeC"/> = 这些格的体积加权均温；
    /// 发热、表面散热、流进铜排、管孔流入四项都是**同一份格子**上的场值，标定到**同一个**节点温度上。
    /// （第一版叫 ClampCalibration／CalibrateClamp，只标铜排通道；参考电阻挂管根温度、管孔按几何式，两处口径错互相抵消。）
    /// </summary>
    public sealed class NodeCalibration
    {
        /// <summary>场里舌端边界是哪一种（与热解同一判定 ShellThermal.ClampBoundaryOf）</summary>
        public ShellThermal.ClampBoundary Mode;
        /// <summary>铜排通道这一项标定成了没有（整片热稳定只要它）。自由端 = 本来没有这条通道，导度 0，也算成。</summary>
        public bool ClampOk;
        /// <summary>四项全部标定成了没有（升温两节点要它）。不成时 <see cref="Note"/> 写原因。</summary>
        public bool Ok;
        /// <summary>节点格（长度 = 网格格数；没有场时为空）、格数、体积 mm³；场里被边界钉住的格排除了没有</summary>
        public bool[] NodeCell = Array.Empty<bool>();
        public int NodeCells; public double NodeVolumeMm3; public bool BoundaryCellsExcluded;
        /// <summary>节点温度 °C = 节点格体积加权均温（口径见类注释）</summary>
        public double TNodeC = double.NaN;
        /// <summary>场里节点的能量账 W：发热、表面散热（两面）、流进铜排、管孔流入；残差 = 发热 − 散热 − 铜排 + 管孔（排除边界格时就是场的能量残差，求和顺序不同）</summary>
        public double QGenW = double.NaN, QLossW = double.NaN, QClampW = double.NaN, QFromTubeW = double.NaN, ResidualW = double.NaN;
        /// <summary>场的管根温度 °C、场电流 A（本片接头电流）</summary>
        public double TRootC = double.NaN, CurrentA = double.NaN;
        /// <summary>铜排通道：导度 W/K = 流进铜排 ÷（节点温度 − 夹持参考温度）；夹持参考温度（定温 = 本片夹持温度；热导 = 冷端）；冷端；升温期夹持温度随法兰升的比例</summary>
        public double GEffWPerK = double.NaN, TClampC = double.NaN, TColdC = double.NaN, FollowRatio = double.NaN;
        /// <summary>发热：参考电阻 Ω = 节点发热 ÷ 场电流²，参考温度就是 <see cref="TNodeC"/></summary>
        public double ResistanceRefOhm = double.NaN;
        /// <summary>管孔：孔侧导度 W/K = 管孔流入 ÷（管根温度 − 节点温度）</summary>
        public double HolePlateGWPerK = double.NaN;
        public string Note = "";
        /// <summary>
        /// 整片热稳定用的夹持导度 W/K = G·(1 − r)：与升温两节点**同一个夹持假设**（夹持温度随法兰温度按比例升 ⇒ 流进铜排的热对法兰温度的导数是 G·(1 − r)）。
        /// 铜排通道没标定成 = NaN（整片热稳定判不了）。热导边界 r = 0 ⇒ 就是 G；自由端 = 0。
        /// </summary>
        public double StabClampWPerK => ClampOk ? GEffWPerK * (1.0 - FollowRatio) : double.NaN;
    }

    /// <summary>标定要求的最小温差 K（节点温度比夹持参考温度高、管根与节点温度之差）：温差太小，导度 = 热 ÷ 温差 会把场里零点几瓦的通量误差放大成任意大的导度。1 K 在稳态（温差上百 K）上不起作用。</summary>
    public const double CalibMinDeltaK = 1.0;

    /// <summary>
    /// ★★ R48 G2 复审（2026-09-15 Opus 5；物理把关人第十一轮 + 审查意见 blocker「节点口径」）：**法兰节点四项从稳态场标定**，一个节点口径。
    ///
    /// · 节点格：<paramref name="excludeBoundaryCells"/> = true ⇒ 场里**不被边界钉住**的格（<paramref name="boundaryCell"/> = ShellThermalResult.BoundaryCell，与场的能量账排除格同一判定；
    ///   生产网格定温边界下就是整面接触的压接格）；false ⇒ 全部格（老口径对照用）。
    /// · 节点温度：节点格体积加权均温 —— 与两节点模型法兰节点的热容同一份格子（LineRunner.FlangeLumped 的质量、面积用 <see cref="NodeCalibration.NodeCell"/>）。
    /// · 发热 = Σ 节点格焦耳热（ShellThermalResult.CellGenW）⇒ 参考电阻 = 发热 ÷ 电流²，参考温度 = 节点温度；
    /// · 表面散热 = Σ 节点格散热（CellLossW）⇒ 交给 <see cref="Inputs.SurfaceCalibLossW"/>，模型里算 κ；
    /// · 流进铜排：排除边界格 ⇒ 场的直接通量 <paramref name="qToClampW"/>（ShellThermalResult.QToClampW，流进被钉住的压接格）；
    ///   不排除 ⇒ 被钉住的格也在节点里，它们自身「发热 − 散热」按场的记账（定温格的收支归边界）同样归铜排；
    ///   ⇒ G_夹 = 流进铜排 ÷（节点温度 − 夹持参考温度），参考温度按 ShellThermal.ClampBoundaryOf（定温 = 本片夹持温度；热导 = 冷端；自由端 = 没有通道）；
    ///   升温期夹持随法兰按比例 r = (T_夹持 − T_冷端) ÷ (T_节点 − T_冷端)（定温）；热导 r = 0。依据与局限见 <see cref="Inputs.ClampFollowRatio"/>。
    /// · 管孔：孔侧导度 = <paramref name="qFromTubeW"/> ÷（<paramref name="tRootC"/> − 节点温度）；流向与温差反号（均温比管根低、热却从法兰流进管，或反之）或温差不足 1 K ⇒ 标定不了：
    ///   整片一个温度表示不了这条热流（热经孔面流进管 ⇒ 紧挨孔面的格里必有比管根热的，而整片平均比管根冷 —— 两者不在管根温度的同一侧；R48 G2 复审二 2026-09-15 Opus 5 改写原句「孔边与整片平均不在同一侧」，只说由流向推得出的，不说最热处在哪）。
    /// ⚠ 不排除边界格时，孔边若按格定温（ShellThermal 的 holeFaceDirichlet = false）那圈孔格的收支也会被记到铜排名下；生产按面定温，不钉孔格。
    /// <paramref name="plateInputs"/> 必须是本片热解实际用的那一份（LineRunner.PlateThermalInputs(...).P2），夹持温度与铜排热导逐片不同。
    /// </summary>
    public static NodeCalibration CalibrateNode(ShellMesh? mesh, double[]? tField, double[]? cellGenW, double[]? cellLossW, bool[]? boundaryCell,
                                                double qToClampW, double qFromTubeW, double tRootC, double currentA,
                                                DesignInputs plateInputs, bool excludeBoundaryCells)
    {
        var c = new NodeCalibration
        {
            Mode = ShellThermal.ClampBoundaryOf(plateInputs), TColdC = plateInputs.BusbarSinkTempC,
            TRootC = tRootC, CurrentA = currentA, QFromTubeW = qFromTubeW, BoundaryCellsExcluded = excludeBoundaryCells,
        };
        bool free = c.Mode == ShellThermal.ClampBoundary.Free;
        int n = mesh?.CellCount ?? 0;
        bool field = mesh is not null && n > 0 && tField is not null && tField.Length == n
                  && cellGenW is not null && cellGenW.Length == n && cellLossW is not null && cellLossW.Length == n
                  && boundaryCell is not null && boundaryCell.Length == n;
        if (!field)
        {
            if (free) { c.ClampOk = true; c.GEffWPerK = 0; c.FollowRatio = 0; }
            c.Note = "本片没有带逐格发热与散热的稳态场，法兰节点标定不了" + (free ? "（舌端没有接铜排，经舌片流进铜排这一项本来就是 0）" : "");
            return c;
        }
        c.NodeCell = new bool[n];
        double v = 0, vt = 0, gen = 0, loss = 0, genB = 0, lossB = 0;
        for (int k = 0; k < n; k++)
        {
            bool b = boundaryCell![k];
            if (excludeBoundaryCells && b) continue;
            c.NodeCell[k] = true; c.NodeCells++;
            double w = mesh!.Area[k] * mesh.Thickness[k];
            v += w; vt += w * tField![k];
            gen += cellGenW![k]; loss += cellLossW![k];
            if (b) { genB += cellGenW[k]; lossB += cellLossW[k]; }
        }
        c.NodeVolumeMm3 = v;
        c.TNodeC = v > 0 ? vt / v : double.NaN;
        c.QGenW = gen; c.QLossW = loss;
        c.QClampW = qToClampW + (excludeBoundaryCells ? 0.0 : genB - lossB);
        c.ResidualW = gen - loss - c.QClampW + qFromTubeW;

        var why = new List<string>();
        // ── 流进铜排
        if (free) { c.ClampOk = true; c.GEffWPerK = 0; c.FollowRatio = 0; }
        else
        {
            bool fixedT = c.Mode == ShellThermal.ClampBoundary.FixedTemp;
            c.TClampC = fixedT ? plateInputs.BusbarClampTempC : plateInputs.BusbarSinkTempC;
            double dT = c.TNodeC - c.TClampC;
            if (!double.IsFinite(c.QClampW) || c.QClampW < 0 || !(dT >= CalibMinDeltaK))
                why.Add($"经舌片流进铜排：场里带走 {c.QClampW:0.0} W、法兰均温 {c.TNodeC:0} °C、{(fixedT ? "夹持" : "铜排冷端")} {c.TClampC:0} °C（要求带走的热不为负、法兰均温比它至少高 1 K）");
            else if (fixedT)
            {
                double span = c.TNodeC - c.TColdC;
                double r = span >= CalibMinDeltaK ? (c.TClampC - c.TColdC) / span : double.NaN;
                if (!(r >= 0 && r < 1))
                    why.Add($"升温期夹持温度的比例：夹持 {c.TClampC:0} °C 不在铜排冷端 {c.TColdC:0} °C 与法兰均温 {c.TNodeC:0} °C 之间");
                else { c.GEffWPerK = c.QClampW / dT; c.FollowRatio = r; c.ClampOk = true; }
            }
            else { c.GEffWPerK = c.QClampW / dT; c.FollowRatio = 0; c.ClampOk = true; }
        }
        // ── 发热
        bool genOk = currentA > 0 && gen > 0 && double.IsFinite(gen);
        if (genOk) c.ResistanceRefOhm = gen / (currentA * currentA);
        else why.Add($"发热：节点发热 {gen:0.0} W、电流 {currentA:0.0} A");
        // ── 表面散热（κ 在模型里算，这里只要求为正）
        bool lossOk = loss > 0 && double.IsFinite(loss);
        if (!lossOk) why.Add($"表面散热：节点散热 {loss:0.0} W");
        // ── 管孔
        double dTh = tRootC - c.TNodeC;
        double gh = Math.Abs(dTh) >= CalibMinDeltaK ? qFromTubeW / dTh : double.NaN;
        bool holeOk = gh > 0 && double.IsFinite(gh);
        if (holeOk) c.HolePlateGWPerK = gh;
        // R48 G2 复审二（2026-09-15 Opus 5）：带号数走 SizerResult.Signed（原 {x:+0.0;-0.0}，-0.05～0 之间 net8 印「-+0.0」，恰是本支会印的值）；
        //   补半句「两节点模型的缺陷，不是对设计的判断」—— 这句会进判据表，工程师看了不该以为设计出了问题。
        else why.Add($"管孔：场里管孔流入 {SizerResult.Signed(qFromTubeW)} W、管根 {tRootC:0} °C、法兰均温 {c.TNodeC:0} °C —— 流向与温差反号或温差不足 1 K，整片一个温度表示不了这条热流（两节点模型的缺陷，不是对设计的判断）");

        c.Ok = c.ClampOk && genOk && lossOk && holeOk;
        c.Note = c.Ok
            ? $"法兰节点按本片稳态场标定：均温 {c.TNodeC:0} °C、发热 {gen:0} W、表面散热 {loss:0} W、流进铜排 {c.QClampW:0} W、管孔流入 {SizerResult.Signed(qFromTubeW)} W"
            : "法兰节点标定不了：" + string.Join("；", why);
        return c;
    }

    /// <summary>
    /// **准静态升温电流**：温控功率下，管温在 tubeTempC 处、按 rateKPerH 爬坡时所需的段电流。
    ///
    ///   I²·R_管(T) = Q_散热(T) + C_管(T)·(dT/dt)
    ///
    /// 慢升温下第二项很小（20 °C/h 时约 1 W，而散热是千瓦级），所以电流几乎就是
    /// 「维持该温度的稳态电流」—— 这正是准静态的含义。
    ///
    /// **适用条件**：法兰热时间常数（约 11 min）≪ 升温全程。20 °C/h 全程 56 h，比值 300，
    /// 完全成立；若要算 3 h 快升温，本式与配套的逐点稳态壳解都**不成立**，须做真瞬态。
    /// </summary>
    public static double QuasiStaticCurrentA(DesignInputs p, double wallMm,
                                             double tubeTempC, double rateKPerH)
        => QuasiStaticBreakdown(p, wallMm, tubeTempC, rateKPerH).CurrentA;

    /// <summary>
    /// <see cref="QuasiStaticCurrentA"/> 的**分项**：散热、金属热容、保温热容、所需功率、管电阻、电流。
    /// 2026-09-18，Opus 5：提出来是为了让门能按定义算「比热改了，设计电流跟着变多少」——
    /// 门里手抄一份同样的式子，守的就是手抄的那份（本项目 R48 栽过一次）。
    /// 本函数是 <see cref="QuasiStaticCurrentA"/> 的**唯一实现**，不是它的副本。
    /// </summary>
    public readonly record struct QuasiStatic(
        double LossW, double CapMetalJPerK, double CapInsulJPerK, double NeedW, double RTubeOhm, double CurrentA);

    /// <inheritdoc cref="QuasiStatic"/>
    public static QuasiStatic QuasiStaticBreakdown(DesignInputs p, double wallMm,
                                                   double tubeTempC, double rateKPerH)
    {
        double ri = p.TubeIdMm * 0.5e-3, w = wallMm * 1e-3, rOut = ri + w;
        double area = Math.PI * (rOut * rOut - ri * ri);
        double L = p.TubeLength;

        bool insulated = false;
        foreach (var lay in p.Layers) if (lay.Enabled && lay.ThicknessMm > 1e-6) insulated = true;
        double eps = insulated ? p.OuterEmissivity : p.PtEmissivity;

        double lossW = Insulation.CylinderLoss(tubeTempC, p.TAmbC, rOut, p.Layers, eps,
                           p.Posture == Orientation.Vertical, L, p.LossScale).QPerLength * L;

        // ★ 比热进设计链的**唯一入口**就是这一项（2026-09-18，Opus 5 查明并注记）：
        //   慢升温下它只有约 1 W，而散热是千瓦级 ⇒ 比热改 10 % 只挪动设计电流 1e-5 量级。
        //   量级由门 R48ThermalSourceTests 按本函数的分项当场算出来，不写死在注释里。
        double capMetal = Materials.PtDensity * area * L * Materials.PtCp(tubeTempC);
        double capInsul = 0, r = rOut;
        foreach (var lay in p.Layers)
        {
            if (!lay.Enabled || lay.ThicknessMm <= 1e-6) continue;
            double rNext = r + lay.ThicknessMm * 1e-3;
            capInsul += Math.PI * (rNext * rNext - r * r) * L * lay.DensityKgM3 * lay.CpJKgK * 0.5;
            r = rNext;
        }

        double need = lossW + (capMetal + capInsul) * (rateKPerH / 3600.0);
        double rTube = Materials.PtResistivity(tubeTempC) * L / area;
        return new QuasiStatic(lossW, capMetal, capInsul, need, rTube,
                               need <= 0 ? 0 : Math.Sqrt(need / rTube));
    }

    public static RampTwoNodeResult Solve(DesignInputs p, Inputs g)
    {
        var res = new RampTwoNodeResult { Mode = g.Mode };
        // R48 G2（2026-09-15 Opus 5）：方程本体搬进 Model（门与探针取分量调同一份）；积分循环逐行不变，只多减一项铜排通道
        var m = new Model(p, g);
        res.ClampConductanceWPerK = g.ClampConductanceWPerK; res.ClampFollowRatio = g.ClampFollowRatio;
        res.ClampColdEndC = g.ClampColdEndC; res.TabInsulThickMm = g.TabInsulThickMm;
        res.HolePlateConductanceWPerK = g.HolePlateConductanceWPerK; res.SurfaceScale = m.SurfaceScale;   // R48 G2 复审（2026-09-15 Opus 5）
        double iMax = m.CurrentCapA;

        // ── 积分（显式，步长自适应到「每步温升 ≤ 1 K」）
        double tTube = g.FromC, tFl = g.FromC, time = 0, maxSec = g.MaxHours * 3600.0;
        res.CurrentStartA = m.CurrentAt(tTube, tFl, m.TubeLossW(tTube), 0);
        res.PeakCurrentA = res.CurrentStartA;
        res.CapTubeJPerK = m.CapTube(0.5 * (g.FromC + g.TargetC));
        res.CapFlangeJPerK = m.CapFlange(0.5 * (g.FromC + g.TargetC));
        res.CouplingWPerK = m.GCouple(0.5 * (g.FromC + g.TargetC));
        // 峰值那一刻的状态（循环结束后在它上面取分量）；峰值没出现正值时就是起点
        double pkTime = 0, pkTube = tTube, pkFl = tFl, pkI = res.CurrentStartA;

        double nextSample = 0;
        int guard = 0;
        while (time < maxSec && guard++ < 4_000_000)
        {
            double qTube = m.TubeLossW(tTube);
            double qc = m.GCouple(0.5 * (tTube + tFl)) * (tTube - tFl);   // >0 = 管子加热法兰

            double i = m.CurrentAt(tTube, tFl, qTube, qc);
            if (g.Mode == RampControl.TemperatureRamp && i >= iMax - 1e-9) res.CurrentClipped = true;
            double iFl = g.SharedFactor * i;
            res.PeakCurrentA = Math.Max(res.PeakCurrentA, i);

            double pTube = i * i * m.RTube(tTube);
            double pFl = iFl * iFl * m.RFlange(tFl);
            double qFl = m.FlangeSurfaceW(tFl);
            double qCl = m.ClampW(tFl);                                   // R48 G2：经舌片流进铜排（没有通道时恒 0.0）

            double dTt = (pTube - qTube - qc) / m.CapTube(tTube);
            double dTf = (pFl - qFl - qCl + qc) / m.CapFlange(tFl);

            if (time >= nextSample)
            { res.Trace.Add((time, tTube, tFl, i)); nextSample = time + Math.Max(5.0, maxSec / 500.0); }

            double delta = tFl - tTube;
            if (delta > res.MaxFlangeMinusTubeK)
            {
                res.MaxFlangeMinusTubeK = delta;
                res.TimeAtMaxDeltaS = time; res.TTubeAtMaxDeltaC = tTube;
                pkTime = time; pkTube = tTube; pkFl = tFl; pkI = i;
            }
            res.TTubePeakC = Math.Max(res.TTubePeakC, tTube);
            res.TFlangePeakC = Math.Max(res.TFlangePeakC, tFl);

            if (tFl >= PtMeltingC && !res.FlangeMelts)
            {
                res.FlangeMelts = true; res.MeltTimeS = time;
                res.Note = $"法兰在 {time / 60:0.0} min 处越过铂熔点（此时管温仅 {tTube:0} °C）";
                break;
            }
            if (tTube >= g.TargetC)
            {
                res.TubeReached = true; res.HoursToTarget = time / 3600.0;
                break;
            }
            if (dTt <= 0 && dTf <= 0 && tTube < g.TargetC)
            {
                res.Note = $"管在 {tTube:0.0} °C 处电功率与散热持平，升不上去";
                break;
            }

            // 步长自适应到「每步温升 ≤ 0.5 K」。温控模式全程数十小时，上限放宽到 60 s，
            // 否则光积分就要几百万步（而那时温度变化率本来就只有 20 K/h）。
            double rate = Math.Max(Math.Abs(dTt), Math.Abs(dTf));
            double dtMax = g.Mode == RampControl.TemperatureRamp ? 60.0 : 2.0;
            double dt = Math.Clamp(0.5 / Math.Max(1e-9, rate), 0.002, dtMax);
            tTube += dTt * dt; tFl += dTf * dt; time += dt;
        }

        res.TTubeEndC = tTube;
        res.CurrentEndA = m.CurrentAt(tTube, tFl, m.TubeLossW(tTube),
                                      m.GCouple(0.5 * (tTube + tFl)) * (tTube - tFl));
        res.Trace.Add((time, tTube, tFl, res.CurrentEndA));
        res.AtPeak = m.Flows(pkTime, pkTube, pkFl, pkI);
        res.AtEnd = m.Flows(time, tTube, tFl, res.CurrentEndA);
        if (res.Note.Length == 0 && !res.TubeReached)
            res.Note = $"限时 {g.MaxHours:0.#} h 内管只升到 {tTube:0.0} °C";
        return res;
    }
}
