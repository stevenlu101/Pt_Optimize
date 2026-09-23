using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// **动态网格求解器的决策层** —— 纯函数，因为每一次解都是分钟级的，
/// 而「下一步该多细、停没停」是纯算术，微秒可验（本项目一贯做法）。
///
/// ★★ 为什么必须有它（2026-08-28 实测，用户：「那还等啥！做」）
///
///   `--meshconv` 在设计记录 0.8 档上量到：
/// <code>
///   细网格mm  单元数   ②′W      ②″K      ③K
///   4.00       275    4.377   −0.002   13.439
///   2.00       676    1.123   −0.208    5.182   ← **设计记录用的就是这一档**
///   1.00      1866    2.655   −0.077    9.656
///   Δ(2→1)           +1.533   +0.131   **+4.474**
///   容差              0.5      0.2       1.0
/// </code>
///   ⇒ ③ 的离散误差是容差的 **4.5 倍**，②′ 是 **3 倍**，而且**不单调**
///     （③ 走 13.4 → 5.2 → 9.7，是在摆）。
///   ⇒ **判据值根本没有网格无关**。设计记录报的 ③ = 5.18/10（看着 48 % 裕度），
///     在 1 mm 上是 9.66/10（只剩 3.4 %）。2 mm 那一档恰好落在一个舒服的位置。
///
///   这条盖过本轮其它所有发现：热收支 2.66 W、夹持温度假设、屈曲宽度，
///   量级都比这个离散误差小。
///
/// ⚠ 本类**不**做逐格 AMR：ShellMesh 是**分级结构网格**
///   （细区 hFine 在 ±fineRadius 内，外面 hCoarse），能调的就是这三个。
///   所以这里做的是**自适应分区**：由几何特征与解的峰位决定细区多细、多大。
/// </summary>
public static class MeshAdapt
{
    /// <summary>
    /// 一个几何特征至少要跨几格才算「解得出来」。
    /// 3 是有限体积里表达一个台阶/圆角的最低要求（进、出、中各一格）；
    /// 少于它，那个特征在场里就是个数值噪声。
    /// </summary>
    public const int CellsPerFeature = 3;

    /// <summary>
    /// 由**几何特征**定的最粗可用网格 mm：最小特征 ÷ <see cref="CellsPerFeature"/>。
    ///
    /// ★ 直接起因：舌根圆角 R3 与环宽 3 mm 落在 2 mm 网格上各只有 **1.5 格**，
    ///   而 DesignSpec 自己写着「网格 2 mm，**小于它的圆角在场里看不出来**」，
    ///   ②″ 的峰又**可能就落在舌根凹角** ⇒ 优化器在调一个自己分辨不出来的几何。
    ///
    /// ⚠ 只看几何，不看解。它给的是**下限**：满足它只是「特征画得出来」，
    ///   离「答案不再随网格变」还差一步 —— 那要靠 <see cref="Converged"/>。
    /// </summary>
    public static double RequiredFineMm(IEnumerable<double> featureSizesMm)
    {
        var fs = (featureSizesMm ?? Array.Empty<double>()).Where(x => x > 1e-9).ToArray();
        if (fs.Length == 0)
            throw new ArgumentException("没有给任何几何特征尺寸 —— 无法判断网格够不够细。"
                + "宁可拒答，也不给一个「看起来够」的默认值。");
        return fs.Min() / CellsPerFeature;
    }

    /// <summary>
    /// **内带半径** mm（算法普查 A⑭）：孔 + 焊脚那一圈，加一点余量。
    ///
    /// 只有这一圈需要「按焊脚定的」那种极细网格；此前它被铺满整个细化区
    /// （范围由盘径/舌长定），于是最小特征的尺寸 × 最大特征的范围 —— 单元数爆掉。
    /// </summary>
    public static double InnerRadiusFor(double holeRadiusMm, double weldLegMm, double marginMm = 3.0)
    {
        if (!(holeRadiusMm > 0))
            throw new ArgumentOutOfRangeException(nameof(holeRadiusMm),
                "孔半径必须为正 —— 内带是**绕着孔**的，没有孔就没有内带。");
        return holeRadiusMm + 2.0 * Math.Max(0, weldLegMm) + Math.Max(0, marginMm);
    }

    /// <summary>
    /// ★★ **峰位落在哪** —— 分区之后必须核对的一条安全线。
    ///
    /// ②″ 的峰位是**输出**不是输入：它可能落在孔边，也可能落在舌根凹角。
    /// 峰若跑进粗区就会被算漏，**而算漏不会报错**，只会给一个偏低的 ②″ ——
    /// 正是本项目最怕的形态。
    ///
    /// 返回 null = 峰在细区里，没问题；否则返回**该原样呈现给人**的一句话。
    /// </summary>
    public static string? PeakVerdict(double peakRadiusMm, double innerRadiusMm, double fineRadiusMm,
                                      double marginMm = PeakMarginMm)
    {
        // ★ R48 续（2026-09-14，Opus 5；物理把关人查出）：原来「峰位 NaN ⇒ 返回 null」就是**判不了算过**，
        //   与当天修掉的 ⑤⑥ 同一个病。现在峰位算不出来 ⇒ 明说判不了。
        if (double.IsNaN(peakRadiusMm))
            return "★★ **峰位算不出来** ⇒ 判不了细区有没有盖住热点 —— 这次复核的温度类判据不算数。";
        if (peakRadiusMm + marginMm <= fineRadiusMm + 1e-9) return null;   // 盖住了，且留足余量
        // 这句要进界面，界面不许出现判据代号 ⇒ 写全名。
        return $"★★ **热点离细区边缘太近或落在粗区**（最远热点 r={peakRadiusMm:0.0} mm，要求 ≤ 细化半径 {fineRadiusMm:0.0} − 余量 {marginMm:0} mm）"
             + " ⇒ **这次复核的温度类判据不算数**：粗网格分辨不出那一带的梯度，报出来的值会偏低。"
             + $"（内带半径 {innerRadiusMm:0.0} mm）⇒ 请把细化半径放大到覆盖热点再复核。";
    }

    /// <summary>某个特征在给定网格上跨几格（&lt; 1 就是看不出来）。</summary>
    public static double CellsAcross(double featureMm, double fineMm) =>
        fineMm > 1e-12 ? featureMm / fineMm : double.PositiveInfinity;

    /// <summary>一次加密：对半。结构网格上单元数约 ×4，是可承受的最陡步长。</summary>
    public static double Refine(double fineMm) => fineMm * 0.5;

    /// <summary>判据在两档网格之间动了多少 —— 与各自容差比。</summary>
    public sealed class Delta
    {
        public string Name = "";
        public double Change, Tol;
        public bool Within => Math.Abs(Change) <= Tol;

        /// <summary>
        /// ★★ R48（2026-09-13，Opus 5）：**这一条判据的结论稳不稳** —— 当前值离限值的距离，除以相邻两档的变化量。
        ///
        /// 为什么要它：收敛要的是**结论可信**，不是小数点后几位不动。实测（管壁 0.8，整档加密 1.0／0.5／0.25 mm）：
        ///   管孔净流入 5.090 → 7.134 → 5.068 W，跳 ±2 W，容差 0.5 ⇒ 按数值判**永远不收敛**；
        ///   可它离限值 0 还有 5 W，再加密也翻不过去 —— 结论「过」是稳的。
        ///   法兰增量温降 22.571 → 22.371 → 21.582 K，离限值 10 还有 11.6 K，结论「不过」同样是稳的。
        /// 判据要的是这两个结论，不是那几位小数。⇒ 数值落进容差**或**结论稳，都算这一条已经算准了。
        ///
        /// NaN = 没给限值（拿不到就不用这条放行，退回只看数值）。取 3 倍是安全系数：
        /// 距离要大到「再来两三档同样幅度的摆动也翻不过限值」。
        /// </summary>
        public double MarginOverChange = double.NaN;

        /// <summary>
        /// ★★★★★ R48 续（2026-09-14，Opus 5；常驻数值讨论人核出来的三个洞，逐条堵）
        ///
        /// ══ 洞一：序列**振荡**时，|最后一次变化| 不是误差
        ///
        /// 实测三档 +0.841 → −1.693 → −1.350：Δ₁ = −2.534、Δ₂ = +0.343，**换号**。
        /// 按 Roache／ASME 的口径这叫 oscillatory convergence，其定义就是「**不在渐近区**」——
        /// 此时「Δ 小 ⇒ 收敛」与 Richardson **一律失效**，不确定度要用**振荡半幅** (max−min)/2。
        /// 这一组是 **±1.267 W**，而判词当时用的是 Δ₂ = 0.344，**差 3.7 倍**。
        /// ⇒ <see cref="Oscillating"/> 为真时，误差尺度改用 <see cref="HalfRange"/>。
        ///
        /// ══ 洞二：Δ→0 的逃生口，把 2026-08-30 修掉的假收敛放了回来
        ///
        /// <c>MarginOverChange = 距离 / |Δ|</c>。网格恰好落在 f(h) 的极值点上时 Δ→0 ⇒ 比值→∞
        /// ⇒ 无条件放行。而 <see cref="Converged"/> 在 ConclusionStable 为真时**跳过**「变化在缩小」那道检查 ——
        /// 两件事一叠，正是 08-30 那条「两级恰好跨在拐点两侧，差值小纯属巧合」的原样重演。
        /// ⇒ 振荡时不许用 ConclusionStable 放行（下面 <c>&amp;&amp; !Oscillating</c>）。
        ///
        /// ══ 洞三：3 这个常数只在 r ≤ 0.75 时成立
        ///
        /// 剩余误差 e ≈ Δ·r/(1−r)。r = 0.5 ⇒ e ≈ Δ（3 倍安全系数 3.0）；
        /// r = 0.75 ⇒ e ≈ 3Δ（安全系数**恰好 1.0**）；r = 0.8 ⇒ 不够。
        /// 而本项目实测的 r 在**一路上涨**（0.377 → 0.413 → 0.586）。
        /// ⇒ 拿到实测比值时按 <c>3·r/(1−r)/(r/(1−r))|_{r=0.5}</c> 折算，即门槛 = max(3, 3·r/(1−r))；
        /// 量不到就退回 3。**同一个做法这个仓库在外层耦合那边早就做对了**
        /// （LineRunner：<c>amp = rEst/(1−rEst)</c>，量不到才退回已知最坏）。
        /// </summary>
        public bool Oscillating;

        /// <summary>振荡半幅 (max−min)/2 —— 振荡时的不确定度尺度（见 <see cref="Oscillating"/>）。</summary>
        public double HalfRange = double.NaN;

        /// <summary>相邻两次变化之比 |Δ₂/Δ₁|，用来按 r/(1−r) 折算门槛；NaN = 量不到，退回 3。</summary>
        public double RatioR = double.NaN;

        /// <summary>结论稳所需的倍数：量得到 r 就按 r/(1−r) 折算（下限 3），量不到退回 3。
        /// 安全系数恒取 3 有出处：Roache 的 GCI 在「收敛阶只由一组三档估出、未跨组验证」时取 Fs = 3.0。</summary>
        public double StableFactor =>
            double.IsNaN(RatioR) || RatioR <= 0 || RatioR >= 0.75
                ? 3.0 : Math.Max(3.0, 3.0 * (RatioR / (1 - RatioR)));

        /// <summary>
        /// ★★★★★ **不在渐近区** —— 一票否决，比「结论稳」更靠前。
        ///
        /// Roache 的三态（R = Δ₂/Δ₁）：0 &lt; R &lt; 1 单调收敛；**R &lt; 0 振荡**；**|R| ≥ 1 发散**。
        /// 第一版只实现了振荡那一态，于是「同号 + Δ 在涨 + 离限值远」会走
        /// <see cref="ConclusionStable"/> 放行，而 <see cref="Converged"/> 又在它为真时**跳过**
        /// 「变化在缩小」那道检查 ⇒ **一边发散一边判收敛**。
        ///
        /// 三条都并到这里，理由同一条、且不引入任何新常数：
        /// **不在渐近区 ⇒ 没有任何误差估计成立 ⇒ 不能声称收敛。**
        ///
        /// · <b>振荡</b>：Δ 换号。
        /// · <b>发散或停滞</b>：|Δ₂/Δ₁| ≥ 0.75。0.75 处剩余误差 e ≈ Δ·r/(1−r) 已等于 3Δ，
        ///   安全系数恰好塌到 1；再往上是拿一个数据点做几十倍外推，那是拟合不是估计。
        ///   （另一层理由：d[r/(1−r)]/dr = 1/(1−r)²，r 越大，r 自身的误差被放得越狠 ——
        ///   r=0.5 时 ±0.05 只值 ±20 %，r=0.9 时同样 ±0.05 值 ±50 %。公式比因子先失效。）
        /// · <b>收敛得比格式能做到的还快</b>：|Δ₂/Δ₁| &lt; 0.25，即实测阶数 p = log₂(1/r) &gt; 2（本格式最高二阶）。
        ///   三档数据分不开「Δ 塌成 0（落在极值点上）」与「真的收敛极快」，能分开它们的只有阶数合不合理
        ///   （ASME V&amp;V 20 / Roache：实测阶远离形式阶 ⇒ 不在渐近区）。不否决的话
        ///   MarginOverChange = 距离/|Δ| → ∞ 会无条件放行，正是 2026-08-30 修掉的
        ///   「两级恰好跨在拐点两侧，差值小纯属巧合」的原样重演。
        ///   （2026-09-14 更正：此处原写「&lt; 0.02」，那个数是我拍的，已换成出自格式阶数的 0.25。）
        /// · 变化本身落在噪声底（≤ 0.1×容差）时以上三条都不判：那时比值是在比噪声。
        ///
        /// ⚠ 量不到 r（不足三档）时为 false —— 那时「够不够档」由 <see cref="Converged"/>
        ///   自己的「至少三档」那道门管，不在这里重复判。
        /// </summary>
        public bool NotAsymptotic =>
            // ★ 噪声底豁免（2026-09-14 补，常驻数值讨论人查出）：变化本身已经落在噪声里时，
            //   比值是在比噪声，任何「换号／不缩」的判断都没有意义。
            //   用的是本文件 Converged 里早就有的那条口径（0.1×容差），**不另立新数**。
            //   ⚠ 不补这一条，收敛得最好的那一条反而会把整趟阶梯永久否掉：
            //     实测 ②″ −0.017 → −0.008 → −0.004，|Δ₂| = 0.004，只有噪声门槛 0.02 的五分之一，
            //     再加一档随便一摆就换号。
            Math.Abs(Change) > 0.1 * Tol
            && (Oscillating
                || (!double.IsNaN(RatioR) && RatioR >= 0.75)
                // ★ 阶数合理性（2026-09-14，Opus 5；常驻数值讨论人给的判据，出处 ASME V&V 20 / Roache）：
                //   推导：误差只含同号一阶与二阶项 e(h) = a·h + b·h²（a, b ≥ 0）时，
                //   r = Δ₂/Δ₁ = (4a + 3bh)/(8a + 12bh) ∈ [0.25, 0.5]（只有一阶 r = 0.5，只有二阶 r = 0.25）。
                //   r < 0.25 只能来自异号项相消（落在极值点上）或高于二阶的项，而本格式没有 ⇒ 下界 0.25 由误差展开推出。
                //   上界 0.75 出处不同：那里剩余误差 Δ·r/(1−r) 已达 3Δ，安全系数塌到 1。
                //   按全局形式阶 1 取（r < 0.5）是错的：会把 (0.25, 0.5) 这段正常的一阶二阶混合判成有病，实测 0.377、0.413 就落在这段。
                //   局限：阶梯边界那个一阶项的系数随格子翻进翻出而跳变，单组三档偶尔落到 0.25 以下 —— 那时否决方向是保守的（多加一档）。
                //   只有三档时，「Δ 塌成 0（极值点）」与「收敛极快」在数据上分不开；唯一能分开它们的是
                //   **实测阶数合不合理**：p = log₂(1/r) 不可能远超格式的形式阶。本格式内部最多二阶、
                //   边界是阶梯的（实测 p 只有 0.77～1.41，见 R48_面上定温_收敛阶）⇒ r < 2^(−2) = 0.25
                //   即实测阶数超过二阶 ⇒ 判为不在渐近区。这个 0.25 出自离散格式本身，不是拍的。
                //   它替掉了我此前拍的「r < 0.02」，也替掉了一小时前补的「误差尺度取半幅下界」——
                //   后者会把 r 在门槛与分母里算两遍，收敛越快越难判「结论稳」（r=0.1 时实际安全系数 149）。
                || (!double.IsNaN(RatioR) && RatioR < 0.25));

        /// <summary>不在渐近区的原因，原样印给人看（空串 = 在渐近区）。</summary>
        public string WhyNotAsymptotic =>
            Oscillating ? "变化换号（振荡）"
            : double.IsNaN(RatioR) ? ""
            : RatioR >= 1.0 ? $"变化在变大（比 {RatioR:0.##}，发散）"
            : RatioR >= 0.75 ? $"变化几乎没缩（比 {RatioR:0.##} ≥ 0.75，剩余误差已达 3 倍动幅）"
            : RatioR < 0.25 ? $"变化缩得比格式能做到的还快（比 {RatioR:0.###}，实测阶数 {Math.Log(1 / Math.Max(RatioR, 1e-12), 2):0.#} > 格式最高阶 2，多半落在极值点上）"
            : "";

        /// <summary>这一条判据的结论稳不稳。**不在渐近区时一律不算稳** —— 那时连误差尺度都不成立。</summary>
        /// <summary>
        /// 离限值的距离，除以**误差尺度**（不是除以「最后一次变化」）。
        /// ★ 2026-09-14 更正：极值点那个洞的病根在**估计式**，不在序列 ——
        ///   `距离/|Δ|` 在 Δ→0 时自己发散，于是无条件放行。把分母换成 <see cref="ErrScale"/>
        ///   （振荡时是半幅、慢收敛时是 Δ·r/(1−r)，都有界）比值就不再爆，
        ///   **不需要任何「Δ 小于多少算塌了」的阈值** —— 那种阈值我原本是拍的。
        /// </summary>
        public double MarginOverErr =>
            double.IsNaN(MarginOverChange) || double.IsNaN(ErrScale) || ErrScale <= 0
                ? double.NaN
                : MarginOverChange * Math.Abs(Change) / ErrScale;

        public bool ConclusionStable => !NotAsymptotic && MarginOverErr >= StableFactor;

        /// <summary>
        /// 这一条实际该用多大的误差尺度报给人看：振荡时用半幅，否则用 |最后一次变化|。
        /// </summary>
        /// <summary>
        /// 这一条实际该用多大的误差尺度。**按态分支，不能只看一个 bool**（2026-09-14 更正）：
        /// <code>
        ///   振荡（Δ 换号）        → 末三档极差半幅（标「至少」，三点采样是下界）
        ///   慢收敛 0.75 ≤ r &lt; 1   → Δ·r/(1−r)  ← 半幅在这一态**偏小**：r=0.8 时差 3.6 倍
        ///   发散   r ≥ 1          → NaN：剩余误差无界，**任何数字都不该印**
        ///   其余                  → |最后一次变化|
        /// </code>
        /// 第一版一律用半幅，对慢收敛那一态系统性偏小 —— 误差报小了比报大危险。
        /// </summary>
        public double ErrScale
        {
            get
            {
                if (!double.IsNaN(RatioR) && RatioR >= 1.0) return double.NaN;      // 发散：不给数
                double dAbs = Math.Abs(Change);
                if (Oscillating && !double.IsNaN(HalfRange)) return Math.Max(HalfRange, dAbs);
                if (!double.IsNaN(RatioR) && RatioR >= 0.75 && RatioR < 1.0)
                    return dAbs * RatioR / (1 - RatioR);
                // ★ 一般分支就是 |Δ₂|（2026-09-14，Opus 5，第二次改这里）。
                //   一小时前我在这里加过「max(|Δ|, 半幅, 0.1×容差)」的下界，去堵「Δ 塌成 0 仍放行」。
                //   数值讨论人复核指出两处不对：
                //     · 半幅里含 Δ₁ = Δ₂/r，而门槛 StableFactor 已经按 r/(1−r) 折算过 ⇒ **r 被算了两遍**，
                //       收敛越快越保守（r=0.5 实际安全系数 4.5，r=0.1 时 149）—— 我注释里「只略保守」只在 r=0.5 成立；
                //     · 0.1×容差这个下界**在任何判决路径上都不生效**：ConclusionStable 被读到时 |Δ| 必已大于它。
                //   塌成 0 那个洞现在由 NotAsymptotic 的阶数合理性（r < 0.25）一票否决，
                //   这里不必再兜，r 也不再被算两遍。
                return dAbs;
            }
        }
    }

    /// <summary>
    /// 收敛判据 = **判据本身**：每一条判据在相邻两档网格之间的变化都落进它自己的容差。
    ///
    /// ⚠ 不用「残差」「单元数」这类替身：我们要的不是「网格看起来够密」，
    ///   而是「**这个判据的数不再随网格变**」。那才叫算出来了。
    /// ⚠ 空集不算收敛 —— 「没有判据可比」被当成「都通过了」是本项目记过案的
    ///   「空集恒真」那一型。
    /// </summary>
    /// <summary>
    /// ★★★ **「变化小」不等于「收敛」**（2026-08-30 实测推翻了旧判据）。
    ///
    /// 旧判据是「**一对**相邻档的变化都落进容差」。实测：
    /// <code>
    ///   粗阶梯  1.000 ③ 8.573 → 0.500 ③ 7.685     变化 −0.888（&lt; 1.0）⇒ 判为收敛
    ///   细阶梯  0.583 ③ 7.547 → 0.292 ③ 9.102 → 0.146 ③ 9.462
    ///   ⇒ 真值 ≈ 9.46，粗阶梯那次错了 **1.777 K —— 比容差本身还大**
    /// </code>
    ///
    /// 病因：③ 随网格**非单调**（先降后升）。粗阶梯那两级恰好跨在拐点两侧，
    /// 差值小**纯属巧合**。这正是「碰巧两级之间没动」的假收敛。
    ///
    /// ⇒ 补两条，都是从「收敛」这个词本身推出来的，不是拍的：
    /// <code>
    ///   ① **至少三档**   —— 两个差值才谈得上「趋势」，一个差值只是一个数
    ///   ② **变化在缩小** —— 收敛的定义就是余项趋零；不缩小就不是在收敛
    /// </code>
    ///
    /// ⚠ 例外：两边都已**远小于**我们分辨得出的差别（≤ 容差的十分之一）时，
    ///   比值就是在比噪声。那种情况直接算过，否则会为了噪声无限加密。
    ///
    /// ⚠ 代价要说清：粗阶梯从此至少三档。2026-08-29 那次 0.6 档「8 分钟跑完」里，
    ///   **省下的时间有一部分是假收敛买来的**（CG 的 36× 是真的，起步粗一级不是）。
    /// </summary>
    /// <param name="deltas">最新一对相邻档的变化。</param>
    /// <param name="prev">**上一对**的变化。null = 只跑过两档 ⇒ 一律不算收敛。</param>
    public static bool Converged(IReadOnlyList<Delta> deltas, IReadOnlyList<Delta>? prev = null)
    {
        if (deltas is not { Count: > 0 }) return false;
        // ★★★★★ R48 续（2026-09-14，Opus 5）：**不在渐近区 ⇒ 一票否决，排在所有放行条款之前。**
        //
        //   第一版把振荡只接到 ConclusionStable 上，而实测那一组（+0.841 → −1.693 → −1.350）
        //   的 Δ₂ = +0.344 ≤ 容差 0.5 ⇒ **Within 为真，根本不走 ConclusionStable 那条旁路**
        //   ⇒ 照样判收敛、照样印「数已经不再变了」。位置错了，等于没接。
        //   依据不是标定，是定义：**序列不在渐近区时，没有任何误差估计成立**，
        //   |最后一次变化| 不是误差，Richardson 也不成立 —— 那就不能声称「数不再变」。
        //   这条不引入任何新常数，也不必去碰出处存疑的容差。
        if (deltas.Any(d => d.NotAsymptotic)) return false;
        // R48（Opus 5）：每一条要么数值落进容差，要么结论已经稳（见 Delta.MarginOverChange）。
        if (!deltas.All(d => d.Within || d.ConclusionStable)) return false;
        if (prev is not { Count: > 0 } || prev.Count != deltas.Count) return false;   // 只有两档 ⇒ 没有趋势
        for (int i = 0; i < deltas.Count; i++)
        {
            double now = Math.Abs(deltas[i].Change), was = Math.Abs(prev[i].Change);
            if (now <= 0.1 * deltas[i].Tol) continue;      // 都在噪声里，不比了
            // R48（Opus 5）：结论已经稳的那条，不必再要求它的变化量单调缩小 ——
            //   它离限值远得翻不过去，数值摆动是离散噪声，不是「还没收敛」。
            if (deltas[i].ConclusionStable) continue;
            if (!(now < was)) return false;                // 没在缩小 ⇒ 不是收敛
        }
        return true;
    }

    /// <summary>
    /// 细区半径 mm：必须**盖住峰所在的地方**，否则峰落在粗区，加密再多也没用。
    /// 取「最远的那个峰 + 余量」，并有下限（管孔周围永远要在细区里）。
    /// </summary>
    /// <summary>R48 续（2026-09-14，Opus 5）：峰位离细区边缘至少留多远 mm。**定半径与判峰位用同一个数**——
    /// 此前定半径时留 10、判峰位时留 0，峰在 R−0.5 mm 处也算盖住了，而它周围的梯度已经跨在粗细交界上。</summary>
    public const double PeakMarginMm = 10.0;

    public static double RequiredFineRadiusMm(IEnumerable<double> peakRadiiMm,
                                              double holeRadiusMm, double marginMm = PeakMarginMm)
    {
        double far = holeRadiusMm;
        foreach (double r in peakRadiiMm ?? Array.Empty<double>())
            if (r > far) far = r;
        return far + marginMm;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ★★★★★ F7′（2026-09-23，Opus 5.5，C4′）：**细区半径的唯一来源 —— 自适应计划**（决 29 改定为「自适应」，HANDOVER §0.-21 决 29 ①～⑦）
    //
    //  为什么要它（改前两条路都不对）：
    //    · 旧规则 max(盘半径, 0.35·|舌长|, 孔半径) + 10：0.35 没有出处，半径随舌长连续漂；
    //    · 决 29 (1) 的开发者缺省 max(盘半径, 孔半径) + 10：与热点检查「峰 + 10 ≤ 半径」相顶 ——
    //      舌区峰 TabMaxRMm 按定义是「形心半径 &gt; 盘半径」那一区里最热的格（ShellThermal 约 1116 行 onTab），恒 &gt; 盘半径
    //      （★ 这句只在 cff38c6 单树、C3／决 28 合入之前成立；C3 合入后跨界格也计入舌区峰，TabMaxRMm 可以 ≤ 盘半径，W08 为 29.504 —— 审查 M4），
    //      检查要的却是 峰 ≤ max(盘半径, 孔半径)；凡带舌片的设计每档都判「不算数」（C4 实施记录 §3.2）。
    //  现在：半径 = 计划（本节），不是一个式子：
    //    ① 初值 r₀ = max(盘半径, 孔半径) + 余量₀，余量₀ = 该设计自己的热长度 ℓ_t（<see cref="ThermalLengthMm"/>，停机放大闭式里的同一个量、同一个函数）；
    //    ② 解出场后读最远热点 r 峰（<see cref="PeakVerdict"/> 用的同一个量、同一个阈值），r 峰 + <see cref="PeakMarginMm"/> &gt; 半径 ⇒
    //       半径 := min(上限, max(r 峰 + PeakMarginMm, 半径 + 一粗格))，重建网格再解（<see cref="GrowFineRadius"/>）；
    //    ③ 半径只增不减；上限 = 板料外缘（<see cref="PlateOuterRadiusMm(LineCase)"/>：细区是 |x|,|z| ≤ R 的方带，R 到材料包络最大半边长时整块板已全是细格，再放大网格不变）；
    //       到上限仍盖不住 ⇒ 拒答（<see cref="PeakAtCapVerdict"/>，与 PeakVerdict 同一口径「这次复核的温度类判据不算数」），不静默；
    //    ④ 初值、余量₀ 与其输入、每次放大的原因与数、终值、放大次数全在 <see cref="FineRadiusPlan"/> 里，进算例（LineCase.MeshFineRadiusPlan）、证据头与判词；
    //    ⑤ 阈值 PeakMarginMm 一个不动；⑥ 改回：tabLengthFactor = 0.35 ⇒ 旧规则逐位且不放大；adaptive = false ⇒ 新初值、不放大（只供归因把「初值」与「放大」两笔分开）；
    //    ⑦ 全仓只此一处算细区半径（门：R48F7AdaptiveRadiusTests 源码门）。
    //  初值**不影响热点检查能否放行**：盖没盖住由解后核热点 + 放大判，初值取多少都不会让一个盖不住热点的网格被当成盖住了。
    //  但初值**不是只影响成本**（审查 L-2／R-1 更正「初值只影响成本」）：放大 0 次时终值 = 初值，即判决网格的半径就是初值，
    //  它以离散误差的量级改动三条判据的数值（C4′ 实测：判决 4 行 ≤ 0.042 K／0.027 K／0.012 W，W06 盘10 半径 59→60.513 同格数差 0.057 K；导航 ≤ 0.19 K；无判词翻转），
    //  另外决定成本（放大几次、每次重做多少）。初值大于上限时截到上限（R ≥ 上限的网格与 R = 上限逐节点相同，见 CapToPlate；审查 L-1）。
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// F7′：设计的**热长度 ℓ_t** mm 与出处（初值余量₀ 用它）。**复用停机放大闭式的同一个函数** <see cref="SegmentSolver.EndTempFixedPointAt"/>：
    /// ℓ_t = √(kA/β′)，k 按牌号在温度 T 处取、A = 管截面、β′ = 该段散热表在 T 处的斜率（带玻璃再加 hg·π·D）。
    /// 闭式里 T 取**该端管根温度**（已解出的场）；初值阶段还没有场 ⇒ T 取**该段控温点** <c>TSetC</c> —— 与段解自己报的 <see cref="SegmentSolver.SolveResult"/>.DecayLengthMm 同一口径
    /// （EndTempFixedPointAt 注释：「同一个式子，取值温度不同」）。壁厚取段解同式 max(WallMinMm, 0.05) mm。各段取最大（取大的那个：初值偏大只多花格子；偏小由解后核热点放大兜住 —— 两头都不会把盖不住当盖住，但半径不同判据值会在离散误差量级内动，见上面的节注）。
    /// ⚠ 这是**管**的轴向热长度（端温扰动沿管衰减的长度），不是法兰板上的热扩散长度；业主定的是「用停机放大闭式里已在用的那个量」，
    ///   它是业主指定的量（停机放大闭式里的同一个量），**不是**板上热扩散长度的物理推导；W08／W06 上它盖住了报出的热点是实测，不外推。
    ///   它决定初值：初值决定成本，放大 0 次时也就是判决网格的半径（判据值在离散误差量级内随它动）；盖没盖住由解后核热点判，不靠它。
    /// </summary>
    public static (double Mm, string Source) ThermalLengthMm(LineCase lc)
    {
        if (lc is null) throw new ArgumentNullException(nameof(lc));
        double best = double.NaN; string why = "";
        int n = lc.SegmentCount;
        for (int i = 0; i < n; i++)
        {
            var p = LineRunner.BaseSegParams(lc, i, lc.EmptyTube ? double.NaN : lc.GlassInC);
            double wallM = Math.Max(p.WallMinMm, 0.05) * 1e-3;   // 与 SegmentSolver.Solve 的 wall 同式
            var fp = SegmentSolver.EndTempFixedPointAt(p, wallM, p.TSetC);
            if (!double.IsFinite(fp.DecayLengthMm)) continue;
            if (double.IsNaN(best) || fp.DecayLengthMm > best)
            {
                best = fp.DecayLengthMm;
                why = $"段 {LineRunner.SegName(i)} 控温点 T = {p.TSetC:0.#} °C：kA = {fp.AxialKAWmPerK:0.#####} W·m/K、β′ = {fp.BetaWPerMK:0.###} W/(m·K)"
                    + $"（管内径 {p.TubeIdMm:0.##} mm、壁 {wallM * 1e3:0.###} mm、牌号 {p.GradeName}）⇒ ℓ_t = √(kA/β′) = {fp.DecayLengthMm:0.###} mm";
            }
        }
        return (best, double.IsNaN(best)
            ? "热长度算不出来（各段 SegmentSolver.EndTempFixedPointAt 都不是有限数）"
            : $"ℓ_t（SegmentSolver.EndTempFixedPointAt，停机放大闭式 1 + ℓ_t/Δx 的同一个量；闭式在管根温度处取，初值阶段没有场 ⇒ 取控温点，同段解 DecayLengthMm 口径；{n} 段取最大）：{why}");
    }

    /// <summary>
    /// F7′：**板料外缘**（半径上限）mm 与出处 —— 算例里各片材料包络（<see cref="IMaterialField.MaterialEnvelope"/>）的最大半边长 max(|x|, |z|)。
    /// 细区是 |x| ≤ R 且 |z| ≤ R 的方带（<see cref="FlangeMesher"/> 铺 xBands／zBands），R 到这个数时整块板已全是细格，再放大网格不变 ⇒ 这就是能放大到的尽头。
    /// 解析板读 <see cref="AnalyticMaterial"/>（精确），图纸内存场读 <see cref="ThicknessField.MaterialEnvelope"/>（有精确包络用精确，否则栅格包络、差一个栅格步）；
    /// 只有 .3dm 文件路径（厚度场还没载入）时给 NaN —— 调用方要从图纸分析结果另给（<see cref="PlateOuterRadiusMm(PlateShapeAnalyzer.Shape)"/>）。
    /// </summary>
    public static (double Mm, string Source) PlateOuterRadiusMm(LineCase lc)
    {
        if (lc is null) throw new ArgumentNullException(nameof(lc));
        static double Half((double XMin, double XMax, double ZMin, double ZMax, bool Exact) e)
            => new[] { Math.Abs(e.XMin), Math.Abs(e.XMax), Math.Abs(e.ZMin), Math.Abs(e.ZMax) }.Max();
        if (lc.FlangePlates.Length > 0)
        {
            double m = lc.FlangePlates.Max(g => Half(new AnalyticMaterial(g).MaterialEnvelope()));
            return (m, $"解析板材料包络（AnalyticMaterial.MaterialEnvelope，精确）各片最大半边长 max(|x|,|z|)，{lc.FlangePlates.Length} 片取最大");
        }
        if (lc.FlangeFields.Length > 0)
        {
            var envs = lc.FlangeFields.Select(f => f.MaterialEnvelope()).ToArray();
            if (envs.Any(e => double.IsNaN(e.XMin))) return (double.NaN, "图纸厚度场里有一片没有材料，量不到板料外缘");
            return (envs.Max(Half), $"图纸厚度场材料包络（ThicknessField.MaterialEnvelope{(envs.All(e => e.Exact) ? "，精确" : "，有一片取栅格包络、差一个栅格步")}）各片最大半边长，{envs.Length} 片取最大");
        }
        return (double.NaN, "算例只有 .3dm 文件路径（厚度场还没载入），量不到板料外缘");
    }

    /// <summary>F7′：图纸分析结果给的板料外缘 mm —— max(|舌端 x|, 盘半径, 舌端半宽, 舌根半宽)（PlateShapeAnalyzer.Shape 的这四项；NaN 的项不计）。只在算例量不到包络时用。</summary>
    public static (double Mm, string Source) PlateOuterRadiusMm(PlateShapeAnalyzer.Shape sh)
    {
        if (sh is null) throw new ArgumentNullException(nameof(sh));
        var v = new[] { Math.Abs(sh.TabEndXMm), sh.DiscRadiusMm, sh.TabEndHalfWidthMm, sh.TabRootHalfWidthMm }.Where(double.IsFinite).ToArray();
        return v.Length == 0 ? (double.NaN, "图纸分析结果没有盘半径与舌端，量不到板料外缘")
                             : (v.Max(), "图纸分析结果（PlateShapeAnalyzer.Shape）max(|舌端 x|, 盘半径, 舌端半宽, 舌根半宽)");
    }

    /// <summary>
    /// F7′：**细区半径计划的规则（全仓唯一一处）**。<paramref name="tabLengthFactor"/> &gt; 0 = 改回旧规则 max(盘半径, factor·|舌长|, 孔半径) + <see cref="PeakMarginMm"/>
    /// （传 0.35 时逐位等于 F7 之前，且**不放大**）；= 0 = 生产：初值 max(盘半径, 孔半径) + ℓ_t，<paramref name="adaptive"/> = true 时解后按热点放大。
    /// <paramref name="adaptive"/> = false 只供归因门把「初值变了」与「放大了」两笔位移分开，生产不传。
    /// 热长度或上限给的是 NaN（量不到）时照建计划、在出处里写明；初值要用 ℓ_t 而它量不到 ⇒ 抛异常（不许拿一个编出来的数顶上）。
    /// </summary>
    public static FineRadiusPlan FineRadiusPlanOf(double discRadiusMm, double holeRadiusMm, double tabLengthMm,
                                                  double thermalLengthMm, string thermalLengthSource,
                                                  double capMm, string capSource,
                                                  double tabLengthFactor = 0.0, bool adaptive = true)
    {
        if (double.IsNaN(tabLengthFactor) || tabLengthFactor < 0)
            throw new ArgumentOutOfRangeException(nameof(tabLengthFactor), "舌长系数只能是 0（生产）或正数（改回对照）。");
        if (tabLengthFactor > 0)
        {
            // 改回：F7 之前两处的原式（数组与改前逐项相同 ⇒ 半径逐位相同）；不放大 ⇒ 热点不盖住时照旧由 MeshVerify.Run 判「不算数」。
            double legacy = RequiredFineRadiusMm(new[] { discRadiusMm, Math.Abs(tabLengthMm) * tabLengthFactor }, holeRadiusMm);
            return new FineRadiusPlan
            {
                DiscRadiusMm = discRadiusMm, HoleRadiusMm = holeRadiusMm, TabLengthMm = tabLengthMm, TabLengthFactor = tabLengthFactor,
                Margin0Mm = PeakMarginMm,
                Margin0Source = $"改回旧规则（只供门对照）：max(盘半径, {tabLengthFactor:R}·|舌长|, 孔半径) + PeakMarginMm {PeakMarginMm:R}",
                InitialMm = legacy, CapMm = capMm, CapSource = capSource, Adaptive = false,
            };
        }
        if (!(thermalLengthMm > 0))
            throw new InvalidOperationException("细区半径初值要用设计的热长度 ℓ_t，而它量不到（" + thermalLengthSource + "）—— 不拿编出来的数顶上。");
        var plan = new FineRadiusPlan
        {
            DiscRadiusMm = discRadiusMm, HoleRadiusMm = holeRadiusMm, TabLengthMm = tabLengthMm, TabLengthFactor = 0,
            Margin0Mm = thermalLengthMm, Margin0Source = thermalLengthSource,
            InitialMm = RequiredFineRadiusMm(new[] { discRadiusMm }, holeRadiusMm, thermalLengthMm),
            CapMm = capMm, CapSource = capSource, Adaptive = adaptive,
        };
        return adaptive ? CapToPlate(plan, capMm, capSource) : plan;
    }

    /// <summary>
    /// F7′ 审查 L-1（2026-09-23）：**初值不许超过上限**。细区是 |x|,|z| ≤ R 的方带、坐标轴范围取材料包络（ShellMesh FlangeMesher 1602–1607 行），
    /// 上限 = 包络最大半边长 ⇒ R ≥ 上限时整根轴都在细带里，R 取上限还是更大，生成的节点逐节点相同；只差配方记录的半径与「峰 + 10 ≤ R」拿哪个 R 当尺子。
    /// 不截的话，同一张全细网格，初值超过上限时判「盖住」、从下面放大到上限时判「拒答」—— 判词取决于初值（审查 L-1）。截到上限之后两条路判词相同。
    /// 只对自适应计划（改回与 adaptive = false 的归因对照照旧，不截）；已经放大过的计划不动（放大本来就截在上限内）。
    /// ⚠ 上限若取自图纸分析结果（<see cref="PlateOuterRadiusMm(PlateShapeAnalyzer.Shape)"/>，只有 .3dm 路径时），它与真实材料包络可能差一个栅格步：
    ///   那时「截断不改网格」只是近似成立（截断写进 <see cref="FineRadiusPlan.InitialUncappedMm"/> 与 Describe）。
    /// 上限量不到（NaN）⇒ 原样返回。
    /// </summary>
    public static FineRadiusPlan CapToPlate(FineRadiusPlan plan, double capMm, string capSource)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (!plan.Adaptive || plan.Steps.Length > 0 || !double.IsFinite(capMm)) return plan;
        var p = double.IsFinite(plan.CapMm) ? plan : plan with { CapMm = capMm, CapSource = capSource };
        double cap = p.CapMm;
        return p.InitialMm > cap ? p with { InitialUncappedMm = p.InitialMm, InitialMm = cap } : p;
    }

    /// <summary>
    /// F7′：调用方**直接给定初值**的计划（旧入口 <c>MeshVerify.Run(工厂, h0, 半径, …)</c> 与测试用）—— 初值不经规则，其余（解后核热点、放大、上限、拒答）与生产同一条路。
    /// 上限在放大时从算例量（<see cref="PlateOuterRadiusMm(LineCase)"/>）。
    /// </summary>
    public static FineRadiusPlan GivenFineRadiusPlan(double radiusMm, bool adaptive = true)
    {
        if (!(radiusMm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusMm), "细区半径必须为正。");
        return new FineRadiusPlan
        {
            InitialMm = radiusMm, Margin0Mm = double.NaN, Adaptive = adaptive,
            Margin0Source = "调用方直接给定初值（不经 FineRadiusPlanOf 的规则；旧入口与测试）",
        };
    }

    /// <summary>
    /// F7′：解出场之后核一次「细区盖没盖住热点」，盖不住就按计划放大。**判法就是 <see cref="PeakVerdict"/>**（峰 + <see cref="PeakMarginMm"/> ≤ 半径），一个数不动。
    /// 返回：<c>Verdict</c> = null ⇒ 盖住了；否则是原样呈现给人的话。<c>Grew</c> = true ⇒ 半径已放大，调用方必须**用新半径重建网格再解**（本次的解不算数）。
    /// <c>Refused</c> = true ⇒ 放大到上限仍盖不住（或量不到上限）⇒ 拒答（Verdict 是拒答原句），不许再解。
    /// 峰位算不出（NaN）⇒ 不放大（放大治不了「判不了」），Verdict 照 PeakVerdict 原句。计划不放大（改回、adaptive = false）⇒ 原样返回 PeakVerdict，由调用方按旧口径处置。
    /// 放大量 = min(上限, max(r 峰 + PeakMarginMm, 半径 + 一粗格))：「一粗格」保证每次至少长一格、有限步内到上限（不死循环）；上限由 <paramref name="capFromCaseMm"/>（本次算例量得）与计划里的上限取有限的那个。
    /// </summary>
    public static (FineRadiusPlan Plan, string? Verdict, bool Grew, bool Refused) GrowFineRadius(
        FineRadiusPlan plan, double peakRMm, double innerRMm, double coarseMm, double capFromCaseMm, string stage)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        // 审查 L-1：自适应计划的半径若超过已知上限（调用方直接给的初值、上限要到这里才从算例量），先截到上限再判 ——
        //   R ≥ 上限的网格与 R = 上限逐节点相同（见 CapToPlate），判词不许取决于初值有没有超过上限。改回／不放大照旧，不截。
        if (plan.Adaptive) plan = CapToPlate(plan, capFromCaseMm, "本次算例的材料包络（PlateOuterRadiusMm）");
        double R = plan.RadiusMm;
        string? v = PeakVerdict(peakRMm, innerRMm, R);
        if (v is null || double.IsNaN(peakRMm) || !plan.Adaptive) return (plan, v, false, false);
        double cap = double.IsFinite(plan.CapMm) ? plan.CapMm : capFromCaseMm;
        string capSrc = double.IsFinite(plan.CapMm) ? plan.CapSource : "本次算例的材料包络（PlateOuterRadiusMm）";
        if (!double.IsFinite(cap))
        {
            string why = PeakAtCapVerdict(peakRMm, innerRMm, R, double.NaN, plan) + "（量不到板料外缘：" + plan.CapSource + "）";
            return (plan with { Refused = why, CapMm = cap }, why, false, true);
        }
        if (R >= cap - 1e-9)
        {
            string why = PeakAtCapVerdict(peakRMm, innerRMm, R, cap, plan);
            return (plan with { Refused = why, CapMm = cap, CapSource = capSrc }, why, false, true);
        }
        if (!(coarseMm > 0)) throw new ArgumentOutOfRangeException(nameof(coarseMm), "一粗格必须为正（它保证每次至少长一格、有限步内到上限）。");
        double want = Math.Max(peakRMm + PeakMarginMm, R + coarseMm);
        double to = Math.Min(cap, want);
        var step = new FineRadiusStep(stage, peakRMm, R, to, coarseMm,
            $"{stage}：最远热点 r = {peakRMm:0.00} mm，r + 余量 {PeakMarginMm:0} = {peakRMm + PeakMarginMm:0.00} > 半径 {R:0.00}"
            + $" ⇒ 半径 := min(上限 {cap:0.00}, max(r + 余量 {peakRMm + PeakMarginMm:0.00}, 半径 + 一粗格 {coarseMm:0.###} = {R + coarseMm:0.00})) = {to:0.00} mm");
        return (plan with { Steps = plan.Steps.Append(step).ToArray(), CapMm = cap, CapSource = capSrc }, v, true, false);
    }

    /// <summary>
    /// F7′：放大到上限仍盖不住热点时的拒答原句 —— 判词口径与 <see cref="PeakVerdict"/> 相同（「这次复核的温度类判据不算数」），
    /// 只是把「请把细化半径放大到覆盖热点再复核」换成已经放大到尽头这件事（再放大网格也不变，那句建议照做不了）。
    /// ★ 审查 L-1／R-4／F3（2026-09-23）：**理由分两支写，照实**。
    ///   · 上限量得到：细区已铺满板料（没有粗区、没有粗细交界）⇒ 不是「网格分辨不出梯度」；这次拒答来自热点检查规则本身
    ///     （最远热点 r + PeakMarginMm ≤ 细化半径，决 29 ③⑤ 字面执行、阈值不挪）—— 口径拒答，不是分辨率拒答。
    ///     热点 r 是离管轴的径向距离 √(x²+z²)，上限是 max(|x|,|z|) 方带的半边长：两个口径在上限处不一致（舌尖角上 r &gt; 上限，那里的热点恒被拒答）。
    ///     全细网格上还要不要按「峰 + 10 ≤ 半径」拒答，【待决定】（交业主；本句不改规则）。
    ///   · 上限量不到：细区可能没铺满板料，热点那一带可能还在粗区 ⇒ 保留「分辨不出、值可能偏低」的理由。
    /// 开头「★★ **细化半径已放大到上限，仍盖不住热点**」与「这次复核的温度类判据不算数」两段不变（门 d、计划规则门钉它们）。
    /// </summary>
    public static string PeakAtCapVerdict(double peakRadiusMm, double innerRadiusMm, double fineRadiusMm, double capMm, FineRadiusPlan plan)
        => $"★★ **细化半径已放大到上限，仍盖不住热点**（最远热点 r={peakRadiusMm:0.0} mm，要求 ≤ 细化半径 {fineRadiusMm:0.0} − 余量 {PeakMarginMm:0} mm；"
         + (double.IsFinite(capMm) ? $"上限 = 板料外缘 {capMm:0.0} mm（{(plan.CapSource.Length > 0 ? plan.CapSource : "本次算例的材料包络")}）" : "上限量不到")
         + $"；放大 {plan.Steps.Length} 次）⇒ **这次复核的温度类判据不算数**："
         + (double.IsFinite(capMm)
            ? "细区已铺满板料（没有粗区），**不是**网格分辨不出梯度；这次不放行来自热点检查规则本身（最远热点 r + 余量 ≤ 细化半径，决 29 阈值不挪）——"
              + "口径拒答，不是分辨率拒答。热点 r 是离管轴的径向距离，上限是 max(|x|,|z|) 方带的半边长"
              + (plan.CapSource.StartsWith("图纸分析结果", StringComparison.Ordinal) ? "；上限取自图纸分析结果，与真实材料包络可能差一个栅格步（「已铺满」是近似）。" : "。")
            : "上限量不到，细区可能没铺满板料，热点那一带可能还在粗区，那一带的梯度可能分辨不出，报出来的值可能偏低。")
         + $"（内带半径 {innerRadiusMm:0.0} mm）";

    /// <summary>
    /// 一句话结论。**没收敛就必须明说**，不许含糊 ——
    /// 「加密到上限仍在动」和「已经不动了」是完全不同的两件事，
    /// 而交付的人只看这一句。
    /// </summary>
    public static string Verdict(double fineMm, IReadOnlyList<Delta> deltas, bool hitCap)
    {
        if (deltas is not { Count: > 0 })
            return "**说不出是否收敛** —— 一条判据都没比到（空集不算通过）";
        // ★★ R48（2026-09-13，Opus 5）：**逐条说，不要一票否决**。
        //   此前只要有一条没落进容差，整句就写「这些判据值还带着离散误差，不能当作算准了的数」——
        //   可实测里常常是：管孔净流入还在跳 ±2 W（没算准），而法兰增量温降 21.6 K 离限值 10 有 14.7 倍动幅（结论稳得很）。
        //   把后者也说成「不能当作算准了的数」，等于让人把一个明确的「不过」当成不确定 —— 这正是「不许把事情搞混」。
        // ★★★★★ R48 续（2026-09-14，Opus 5）：**只要有一条不在渐近区，就不许印「数已经不再变了」。**
        //   那句话是对**数值**的断言，而不在渐近区时「这一次的变化」根本不是误差
        //   —— 能说的只有「符号定了」，不能说「数定了」。
        var offAsym = deltas.Where(d => d.NotAsymptotic).ToArray();
        var settled = deltas.Where(d => !d.NotAsymptotic && d.Within).ToArray();                       // 数值已经落进容差
        var stable = deltas.Where(d => !d.NotAsymptotic && !d.Within && d.ConclusionStable).ToArray();  // 数在动，但翻不过限值
        var bad = deltas.Where(d => !d.NotAsymptotic && !d.Within && !d.ConclusionStable).ToArray();    // 真的还没算准
        string One(Delta d) => $"{d.Name} {d.Change:+0.000;−0.000}/{d.Tol:0.###}";
        string StableOne(Delta d) => $"{d.Name}（动 {d.Change:+0.000;−0.000}，但离限值还有 {d.MarginOverChange:0.#} 倍这个动幅 ⇒ 结论翻不过来）";
        if (offAsym.Length > 0)
        {
            // ★ 2026-09-14：**必须分得开「还能再加一档」和「到顶了」** —— 此前这一支没有用 hitCap，
            //   于是两种完全不同的处境印出同一句话。振荡不会因为再加密就消失，
            //   所以到上限时这是**终态**，要给它一个名字：这个量在这个网格族上**判不了**。
            string why = string.Join("／", offAsym.Select(d =>
                $"{d.Name} {d.WhyNotAsymptotic}，误差尺度"
              + (double.IsNaN(d.ErrScale) ? "**给不出**（变化在变大，剩余误差无界）"
                 : $"**至少** ±{d.ErrScale:0.###}")));
            if (hitCap)
                return $"✗✗ **这几条在这个网格族上判不了**（已加密到 {fineMm:0.00} mm 并撞上单元上限）：{why}"
                     + "　⇒ 再加密也不会变好：序列在摆，不是还没收敛。"
                     + "**不要把它读成「不过」，也不要读成「过」** —— 这一关没有被检查过。"
                     + "　【下一步】① 换个网格族再验一次（改细区半径或起步档，看结论会不会跟着变）；"
                     + "② 若换族之后结论一致，那这个量对网格不敏感的那部分才可引用；"
                     + "③ 判据值贴着限值时，先问这条判据的限值有没有留够噪声裕量。";
            return "✗ **不在渐近区，不能说「数不再变了」**：" + why
                 + "　⇒ 这几条的误差**不是**这一次的变化量，Richardson 外推也不成立；"
                 + "能说的只有「符号定不定」，不能说「数定了」。"
                 + (bad.Length + settled.Length + stable.Length > 0
                    ? $"　其余：{string.Join("／", settled.Select(One).Concat(stable.Select(StableOne)).Concat(bad.Select(One)))}"
                    : "");
        }
        if (bad.Length == 0)
        {
            string head = stable.Length == 0
                ? $"✓ **数已经不再变了**：加密到 {fineMm:0.00} mm，每条判据的变化都落进各自容差"
                : $"✓ **结论已经算准了**：加密到 {fineMm:0.00} mm";
            return head
                 + (settled.Length > 0 ? $"（{string.Join("／", settled.Select(One))}）" : "")
                 + (stable.Length > 0 ? $"；{string.Join("／", stable.Select(StableOne))}" : "");
        }
        return (hitCap ? "✗ **加密到上限，数仍在变**" : "✗ **数还在变，没算到头**")
             + $"：{string.Join("／", bad.Select(d => $"{d.Name} 动了 {d.Change:+0.000;−0.000}（容差 {d.Tol:0.###}"
                 + (double.IsNaN(d.MarginOverChange) ? "" : $"，离限值只有 {d.MarginOverChange:0.#} 倍这个动幅") + "）"))}"
             + "　⇒ **这几条**还带着离散误差，不能当作算准了的数"
             + (settled.Length + stable.Length > 0
                ? $"；已经算准的：{string.Join("／", settled.Select(One).Concat(stable.Select(StableOne)))}"
                : "");
    }

    /// <summary>
    /// ★★★★★ R48（2026-09-13，Opus 5）：**「把这个算例加密到 h」只有这一处实现。**
    ///
    /// ══ 为什么要收成一处
    ///
    /// 「加密」此前在四个地方各写各的，而它们对**粗区**和**内带**的处置并不一致：
    /// <code>
    ///   MeshVerify 的每档工厂     中带 h　粗区按比例缩　内带 = 中带     ⇒ 自相似
    ///   LineDesignPage 图纸复核   同上（R48 补齐）
    ///   Solver.EvalRaw（--fine）  中带 h　**粗区留 11 mm**　**不分内带** ⇒ 细粗比 22 倍
    ///   Solver.Finish（终局复核） 同 EvalRaw
    /// </code>
    /// 于是 <b>求根在一张网格上求出根、复核拿另一张网格判</b>，标称尺寸一样、结论相反。
    /// 09-13 正式重解两个设计都撞上了（deliverable/R48_正式重解_管壁08／06_2026-09-13.txt）：
    /// 第二遍求根在 0.500 mm 上报「第 1 轮全过」，加密复核在同样的 0.500 mm 上报
    /// 管孔净流入 −1.792 与 −1.789 W（不过）。
    ///
    /// Program.cs 那段注释当年就写着「求根的网格和判决的网格必须是同一张，否则求出来的根照样不作数」，
    /// 但只把**尺寸**这一维接了过去，粗区与内带两维仍各走各的 —— 规矩是对的，接线漏了两根。
    ///
    /// ══ 配方
    ///
    /// 粗区按「新中带 ÷ 原中带」同比例缩 ⇒ 细粗比恒定，两区之间那段 growth 过渡的**结构**不随加密变；
    /// 而过渡区正压在舌片（电流主通路）上，结构一变焦耳热就跟着变 6～7 %，只有几瓦的抽热直接被淹没
    /// （实测与推演见 deliverable/R48_加密必须自相似_2026-09-13.md）。
    ///
    /// 内带设成与中带同尺寸 = **等于不分内带**：<see cref="ShellMesh.BuildFromField"/> 里两条带重叠处取最细，
    /// hInner == hFine 时内带那一条带产生不了任何新节点，<paramref name="innerRadiusMm"/> 因此是惰性的
    /// （留着这个参数是为了和分区加密的调用方共用一个签名，也给「它为什么可以是 0」一个落笔处）。
    /// </summary>
    /// <param name="lc">要改的算例；**必须是刚从 BuildCase 造出来的**（MeshFineMm 还是造出来时的值），
    ///   否则比例会以已经改过的中带为基准，缩出来的粗区不对。</param>
    /// <param name="hMm">这一档的中带尺寸 mm。</param>
    /// <param name="fineRadiusMm">中带作用半径 mm；≤ 0 表示沿用算例原值。</param>
    /// <param name="innerRadiusMm">内带半径 mm（惰性，见上）。</param>
    public static void RefineWholeMesh(LineCase lc, double hMm,
                                       double fineRadiusMm = 0, double innerRadiusMm = 0)
    {
        if (lc is null) throw new ArgumentNullException(nameof(lc));
        if (!(hMm > 0))
            throw new ArgumentOutOfRangeException(nameof(hMm), "网格尺寸必须为正。");
        lc.MeshCoarseMm *= lc.MeshFineMm > 1e-9 ? hMm / lc.MeshFineMm : 1.0;   // ← 必须排在改中带之前
        lc.MeshFineMm = hMm;
        if (fineRadiusMm > 0) lc.MeshFineRadiusMm = fineRadiusMm;
        lc.MeshInnerMm = hMm;
        lc.MeshInnerRadiusMm = innerRadiusMm;
    }
}

/// <summary>F7′（2026-09-23）：细区半径的一次放大（阶段、读到的最远热点 r、放大前后半径、当时的一粗格、原因原句）。</summary>
public sealed record FineRadiusStep(string Stage, double PeakRMm, double FromMm, double ToMm, double CoarseMm, string Why);

/// <summary>
/// ★★★★★ F7′（2026-09-23，Opus 5.5，C4′）：**细区半径计划** —— 全仓唯一的细区半径来源（规则在 <see cref="MeshAdapt.FineRadiusPlanOf"/>，放大在 <see cref="MeshAdapt.GrowFineRadius"/>）。
/// 不可变：放大返回新计划（with）。<see cref="RadiusMm"/> = 当前半径（初值或最后一次放大后的值）。
/// </summary>
public sealed record FineRadiusPlan
{
    public double DiscRadiusMm { get; init; } = double.NaN;
    public double HoleRadiusMm { get; init; } = double.NaN;
    /// <summary>只在改回旧规则时参与（生产不读舌长）。</summary>
    public double TabLengthMm { get; init; } = double.NaN;
    /// <summary>0 = 生产；&gt; 0 = 改回旧规则的舌长系数（门用）。</summary>
    public double TabLengthFactor { get; init; }
    /// <summary>余量₀ mm（生产 = 热长度 ℓ_t；改回 = PeakMarginMm）。</summary>
    public double Margin0Mm { get; init; } = double.NaN;
    /// <summary>余量₀ 的出处与输入（人话）。</summary>
    public string Margin0Source { get; init; } = "";
    public double InitialMm { get; init; } = double.NaN;
    /// <summary>审查 L-1：初值按规则算出来超过上限、被截到上限时，截断前的原值（NaN = 没截）。截断不改网格（R ≥ 上限逐节点相同），只改配方记录的半径。</summary>
    public double InitialUncappedMm { get; init; } = double.NaN;
    /// <summary>半径上限 = 板料外缘 mm（NaN = 还没量到；放大时从本次算例量）。</summary>
    public double CapMm { get; init; } = double.NaN;
    public string CapSource { get; init; } = "";
    /// <summary>解后按热点放大（生产 true；改回与 adaptive = false 的归因对照为 false）。</summary>
    public bool Adaptive { get; init; }
    public FineRadiusStep[] Steps { get; init; } = Array.Empty<FineRadiusStep>();
    /// <summary>非 null = 放大到上限仍盖不住（或量不到上限）⇒ 拒答原句。</summary>
    public string? Refused { get; init; }

    /// <summary>当前半径 mm。</summary>
    public double RadiusMm => Steps.Length == 0 ? InitialMm : Steps[^1].ToMm;

    /// <summary>一行文字（证据头、判词、界面输出框用）：初值与其输入、上限、每次放大的原因与数、终值、放大次数。</summary>
    public string Describe()
        => (TabLengthFactor > 0
                ? $"细区半径（改回旧规则、不放大）= {InitialMm:0.###} mm（{Margin0Source}）"
                : double.IsNaN(Margin0Mm)
                ? $"细区半径计划{(Adaptive ? "（自适应）" : "（不放大）")}：初值 {InitialMm:0.###} mm（{Margin0Source}）"
                : $"细区半径计划{(Adaptive ? "（自适应，决 29）" : "（新初值、不放大 —— 只供归因）")}：初值 r₀ = max(盘半径 {DiscRadiusMm:0.###}, 孔半径 {HoleRadiusMm:0.###}) + 余量₀ {Margin0Mm:0.###} = {InitialMm:0.###} mm"
                  + $"（余量₀ = 设计热长度 {Margin0Source}）")
         + (double.IsNaN(InitialUncappedMm) ? "" : $"（规则给的初值 {InitialUncappedMm:0.###} mm 超过上限，截到上限 {InitialMm:0.###} mm：R ≥ 上限时网格逐节点相同，只改配方记录的半径）")
         + $"；上限 = 板料外缘 {(double.IsFinite(CapMm) ? $"{CapMm:0.###} mm（{CapSource}）" : $"未量（{(CapSource.Length > 0 ? CapSource : "放大时从算例量")}）")}"
         + $"；放大 {Steps.Length} 次" + (Steps.Length == 0 ? "" : "：" + string.Join("；", Steps.Select((s, k) => $"[{k + 1}] {s.Why}")))
         + $"；终值 {RadiusMm:0.###} mm"
         + (Refused is null ? "" : "；**拒答**：" + Refused);
}
