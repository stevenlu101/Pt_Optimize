using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **四片法兰自动定厚** —— 求一组厚度，使**每一段**的管根温差都落在目标窗口内。
///
/// 为什么必须四片各自独立：n 段有 n 个约束，n+1 片有 n+1 个厚度自由度。
/// 若把四片绑成一个标度（如按 t ∝ I 分配），就只剩 1 个自由度，
/// 只能让**一段**达标，其余段实测落在 +45…+104 K（HANDOVER §4.2w）。
///
/// 解法：阻尼牛顿。段 i 的管根温差主要由它两端的片 i、i+1 决定，
/// 故片 j 的误差取相邻段误差的均值，按灵敏度走对数步：
///
///   Δln t_j = −ω · err_j / S,   S = d(ΔT)/d(ln t) ≈ 800 K
///
/// 用**对数**步长而非线性，是因为厚度跨越 0.4–6 mm 时灵敏度按 1/t 变；
/// 对数步在整个区间上步幅相当，不会在薄端过冲。
///
/// ⚠ 靶量必须**连续单调**。此前用「|ΔT| 最大那段的带符号值」，
/// 最不利段身份一切换该量就跳变，二分/牛顿全部失效（§7）。
/// 这里逐段各自算误差，不取极值，天然连续。
/// </summary>
public static class FlangeAutoSizer
{
    public sealed class Options
    {
        /// <summary>目标管根温差 K。取窗口中偏安全的一侧（0 &lt; ΔT &lt; 10）</summary>
        public double TargetK = 5.0;
        /// <summary>收敛判据：所有段的 |ΔT − 目标| 均小于此值 K</summary>
        public double TolK = 2.0;
        public int MaxIterations = 25;
        /// <summary>灵敏度 d(ΔT)/d(ln t)，K。由 --tscan 的斜率估得</summary>
        public double SensitivityK = 800.0;

        /// <summary>
        /// ★★★ **过热到这个温度就去加厚**（℃，2026-09-03）。
        ///
        /// 用户 2026-09-03：「过热（严重烧毁）表示电流密度过大，应该加大电流的截面积」。
        /// 默认取铂熔点 —— 那是**物理上不可谈判**的那条线；
        /// 比它低的过热由原有的「各级比例」层去摊，比它高就必须真的加厚。
        /// ⚠ 这不是判据，是**动手的触发点**；判据仍只有 LineRunner.Judge 那一份。
        /// </summary>
        public double OverheatRaiseFromC = Materials.PtMeltC;
        /// <summary>阻尼系数。1 = 全牛顿步（会振荡），0.6 实测稳定</summary>
        public double Damping = 0.6;
        public double MinThickMm = 0.4, MaxThickMm = 6.0;

        // ════════════════════════════════════════════════════════════════
        // ★★★★★ 靶换成**抽热窗口**（2026-08-17）。原来的靶会往烧断方向优化。
        //
        // 病：原来单边追 ③（只在 ③ 超靶时往薄里走），而注释里写着这样设计的理由是
        //     「让『越薄越省铂』自己去撞另一侧的界（②′ 净流入 > 0）」——
        //     **可是没有任何东西在那一侧拦着**。判据表事后报 ✗，而按钮已经把设计
        //     推过去了。②′ < 0 的物理含义是**热往管子里灌**，正是现场烧断的机理。
        //
        // 依据（2026-08-17 `--window` 实测，六行、D 从 +0.6 到 +144 W 吻合 5 % 以内）：
        //     ③ = γ·D，γ = 2.40 K/W
        // ⇒ ②′>0 与 ③≤10 合起来就是一句话：**0 < D ≤ 10/γ ≈ 4.2 W**。
        //   两条判据是**同一个量的两侧**，所以一个双向靶就同时守住两条，
        //   而单边靶必然只守一条。
        //
        // ⚠ `.3dm` 路上**没有舌保温这个旋钮**（LineRunner 里 tabInsulThickMm 传 NaN，
        //   现场实况是「仅圆盘保温、舌片裸露」）⇒ 只能用板厚做促动器。
        //   可行：实测 dD/d板厚 = 62–107 W/mm（`--vary` 新设计记录点）。
        // ════════════════════════════════════════════════════════════════

        /// <summary>抽热靶 W。窗口 (0, ③限/γ]，取偏安全的低侧。</summary>
        public double DrawTargetW = 2.0;
        /// <summary>收敛判据：每片 |D − 靶| 均小于此值 W。</summary>
        public double DrawTolW = 0.6;
        /// <summary>d(抽热)/d(板厚) W/mm。实测 62–107；取偏小值 ⇒ 步子偏保守，不过冲。</summary>
        public double DrawSensWPerMm = 60.0;
        /// <summary>单步对数位移上限，防止首轮从很差的初值一步跳飞</summary>
        public double MaxLogStep = 0.35;

        // ── 搜索期降精度（收敛后会自动做一次全精度复核）
        //
        // ★★ 2026-08-13 血的教训：**孔周网格一格都不能放粗**。
        //
        // 早先把 MeshFineMm 从 2.0 放到 4.0「提速 5.6 倍」，实测对照（--fidelity）：
        //
        //   设置          单元数   HC1/HC2/HC3 管根温差 K      用时
        //   全精度          243    −204 / −295 / −142         34.6 s
        //   只粗网格         68    **+24 / −14 / +26**        26.5 s   ← 符号都翻了
        //   只松耦合        243    −192 / −277 / −138         20.3 s   ← 只差 10–18 K
        //
        // 管根温差正是由**孔周**的热流决定的，而 MeshFineMm 控制的就是孔周分辨率 ——
        // 放粗它等于把被优化的那个量本身解坏。而且这笔买卖极不划算：
        // 用 300 K 的误差只换了 8 秒（法兰本来就小，全精度也才 243 单元）。
        //
        // ⇒ 现在**只放粗远场**（MeshCoarseMm）与**放松耦合容差**，孔周一动不动。
        //   实测 20.3 s vs 34.6 s，1.7 倍，诚实的提速。
        /// <summary>
        /// 搜索期的**细**网格步长 mm。★ 默认 0 = **不动**，因为它控制孔周分辨率。
        /// 除非你已用 --fidelity 验证过该形状上放粗无害，否则不要设。
        /// </summary>
        public double SearchMeshFineMm = 0;

        /// <summary>
        /// ★ R47 C（2026-09-13）：**终局细网格**口径 —— 与 <see cref="SearchMeshFineMm"/>（搜索期放粗）**分开**。
        /// 0 = 不动（用 baseCase 自己的导航网格）。&gt; 0 时整个 <see cref="SolveByLevel"/>（搜索各轮 + 全精度复核）
        /// 都在这张网格上跑：中带 FinalMeshFineMm／FinalMeshFineRadiusMm、内带 FinalMeshInnerMm／FinalMeshInnerRadiusMm，
        /// 与加密复算（MeshVerify）收敛那一档同口径 —— 求根的网格与判决的网格才是同一张（A⑬）。
        /// 病：「细网格重解」的 .3dm 分支此前 fineMm 没传进 SolveByLevel，实际在 2 mm 导航网格上再跑一次。
        /// 解析路径的 Solver 早有 FineMm/FineRadiusMm 两遍求根，这里是图纸路径的对应物。
        /// </summary>
        public double FinalMeshFineMm = 0, FinalMeshFineRadiusMm = 0, FinalMeshInnerMm = 0, FinalMeshInnerRadiusMm = 0;
        /// <summary>
        /// 搜索期的**远场**网格步长 mm。★ 2026-08-17 起默认 **0 = 不放粗**。
        ///
        /// 原值 16.0，注释写「远场放粗是安全的」。那句话是 2026-08-13 在**旧形状**
        /// （舌 90×30、细化半径 50 mm）上用 `--fidelity` 验的 —— 那时舌片窄而短，
        /// 「远场」确实只是圆盘外围的一点边角料。
        ///
        /// **新形状把这个前提废掉了**：舌片 140×60，细化半径仍是 50 mm ⇒
        /// **舌片的绝大部分现在就落在「远场」里**。把它从 11 放到 16 mm，
        /// 放粗的不再是边角料，而是发热与散热的主体。
        ///
        /// 实测（自检 E 段）：只收紧耦合容差（4→1 K）后，搜索期与全精度的抽热差
        /// 仍有 **5.2 W**（窗口才 4.2 W 宽）⇒ 主要误差不在耦合，在这里。
        ///
        /// ⇒ 不再放粗。速度由**热启动**补回来（2026-08-17 加，单次全精度复核 14 s）——
        ///   当初降精度就是为了速度，而那个理由现在有更好的解法。
        /// ⚠ 这条再次说明：**「验证过安全」是绑在当时那个构型上的**，
        ///   构型一换就要重验，不能当成永久结论。
        /// </summary>
        public double SearchMeshCoarseMm = 0;
        /// <summary>
        /// 搜索期的段↔法兰耦合轮数与容差。
        ///
        /// ★★★★★ 2026-08-17 从「5 轮 / 4.0 K」收紧到「30 轮 / 1.0 K」，理由是**量纲对不上**：
        ///
        /// 原注释写「实测只影响 10–18 K，可放松」——那是当靶还是**管根温差**（几十 K 量级）
        /// 时说的。现在靶是**抽热窗口**，整个窗口只有 0–4.2 W 宽，而
        ///     ③ = 2.40·D  ⇒  **4 K 的耦合容差 ≈ 1.7 W 的抽热误差**
        /// 也就是说搜索模型的误差**和靶值本身同量级** —— 它根本分辨不出窗口。
        ///
        /// 实测后果（自检 E 段抓到）：搜索期报「各片抽热已落进窗口，+1.7…+2.0 W」，
        /// 全精度复核却是 **②′ = −4.47 W** —— 差 6 W，比整个窗口还宽，
        /// 而 −4.47 W 的物理含义是**热往管子里灌**。
        ///
        /// 这是记忆里那条「**求解器的收敛容差必须优于判据的分辨率**」的又一次发作，
        /// 只是这次发在**搜索期**的容差上：优化器在一个分辨不出可行域的模型里找可行解。
        /// ⇒ 远场网格仍可放粗（那条是验证过的），但耦合容差必须跟上判据。
        /// </summary>
        public int SearchCoupleRounds = 30;
        public double SearchCoupleTolK = 1.0;
    }

    public sealed class Result
    {
        public double[] ThicknessMm = Array.Empty<double>();
        public LineResult? Line;
        public int Iterations;
        public bool Converged;
        public string Message = "";
        /// <summary>每轮的最大误差，供界面画收敛曲线或诊断振荡</summary>
        public readonly List<double> History = new();

        /// <summary>
        /// ★★★★★ **走过哪些分支的痕迹**（2026-09-08，见 <see cref="BranchMarks"/>）。
        /// <c>Message</c> 每轮都被覆盖，留不住「第 2 轮走过熔化那一支」这种事实；
        /// 而那正是难构造的分支唯一能被断言的东西 —— 断言**走到了**，不是断言结果好。
        /// </summary>
        public readonly List<string> Trace = new();
        /// <summary>逐级定厚的结果：`[片][级]` 的厚度倍数（仅 SolveByLevel 填）</summary>
        public double[][]? LevelScale;

        /// <summary>
        /// ★★★★★ **这次停下来是「证明」，不是「迭代不够」**（2026-09-03 补）。
        ///
        /// 与 <c>SolverResult.HitBound</c> 同一个语义。没有它的时候，界面只看得到
        /// <c>Converged = false</c> —— 而「没收敛」和「再跑也是这个数」是两件事：
        /// 前者该再点一次，后者再点多少次都是同一句话。
        ///
        /// 实测（2026-09-03，Pt_Heater1.3dm 等厚板）：熔点闸停机之后，
        /// 指路继续指「自动定厚」，第二次跑完**判据表与总铂逐字未变** ——
        /// 工程师跟着蓝色指示会一直点下去。这已经是同一个死循环的**第三个入口**
        /// （前两个：等厚板被拒、解析路旋钮顶到上界）。
        ///
        /// 置为 true 的三处都是**结构性**的停：
        ///   · 熔点闸（局部发热物理上就下不来）
        ///   · 连续几轮越调越差（方向是错的）
        ///   · 残差进平台（再跑也是这个数）
        /// </summary>
        public bool Terminal;

        /// <summary>结构性停机的那一句话（给指路用，不必再从 Message 里抠）。</summary>
        public string TerminalWhy = "";
    }

    /// <summary>
    /// **自动求解**：迭代次数与阻尼由本方法自行调整，不需要调用方猜。
    ///
    /// 策略：先按默认阻尼跑；若耗尽轮次仍未达标，则判断是**振荡**还是**爬得太慢**——
    ///   · 误差不再单调下降（振荡）⇒ 阻尼减半，重来
    ///   · 误差仍在稳定下降（只是没走完）⇒ 轮次翻倍，接着跑
    /// 最多升级 <paramref name="maxEscalations"/> 次。这样「二分/迭代次数不够就误报无解」
    /// 那类错误（§7）在界面上不可能再发生 —— 不收敛只会是真的无解。
    /// </summary>
    /// <summary>
    /// 图纸厚度量化格 mm（<c>Sizer.QuantThickMm</c> 同一个数）——
    /// 厚度按 0.01 mm 出图，比这更细的差别**造不出来**。
    /// </summary>
    private const double QuantMm = 0.01;

    /// <summary>
    /// 图纸厚度量化格的一半 mm。厚度按 0.01 mm 出图（<c>Sizer.QuantThickMm</c> 同一个数），
    /// 比这更细的差别**造不出来**，所以「动了不到半格」在工程上就是「没动」。
    /// 用它当收敛/停滞的判据，比随手挑一个 1e-9 有据得多 —— 那种数只要撞上
    /// 求解器自身的噪声就会失效。
    /// </summary>
    private const double HalfQuantMm = 0.005;

    /// <summary>
    /// ★★★★★ **一个出口只能对它自己有的旋钮下结论**（2026-09-08，督导第 11/12 封）。
    ///
    /// 本类的 <c>Solve</c>/<c>SolveAuto</c> 手上**只有厚度**（<c>opt</c> 里只有
    /// <c>Min/MaxThickMm</c>，宽由调用方的 <c>makePlate</c> 定死）⇒ 厚度顶到界时
    /// 只能说「厚度这条路走到头了」，**不能说「这个几何无解」** —— 宽度一次都没动过。
    ///
    /// ⚠ 写成一处常量而不是在三个出口各写一遍：同一句话三个来源，早晚漂成三种说法
    ///   （本仓「判据只有一个来源」的同一条）。
    /// </summary>
    private const string HandOffHint =
        "本层只有**厚度**这一根旋钮（宽由调用方的 makePlate 定死），"
      + "下一根是**增宽过流带**（盘径 / 舌半宽），归「◇ 搜形状」";

    public static Result SolveAuto(LineCase baseCase, Func<double, int, FlangePlate>? makePlate,
                                   double[] initialThicknessMm, Options? opt = null,
                                   IProgress<string>? progress = null,
                                   CancellationToken cancel = default,
                                   int maxEscalations = 4,
                                   bool verify = true)
    {
        opt ??= new Options();
        var cur = new Options
        {
            TargetK = opt.TargetK, TolK = opt.TolK, MaxIterations = opt.MaxIterations,
            SensitivityK = opt.SensitivityK, Damping = opt.Damping,
            MinThickMm = opt.MinThickMm, MaxThickMm = opt.MaxThickMm, MaxLogStep = opt.MaxLogStep
        };
        var start = (double[])initialThicknessMm.Clone();
        Result last = new();
        // 上一次升级的落点 —— 用来判「这次升级到底改变了什么没有」
        double[]? prevTh = null; double prevErr = double.NaN;

        for (int esc = 0; esc <= maxEscalations; esc++)
        {
            last = Solve(baseCase, makePlate, start, cur, progress, cancel);
            if (last.Converged) break;

            // ★★★★★ 升级前先问：上一次升级**改变了什么吗**（2026-08-23 加）。
            //
            // 原来的升级判据只看「误差还在不在降」，没看这一步是否**动得了**。
            // Pt_Heater3 实测：四片厚度全被 MinThickMm 咬在 0.40，
            // 于是连退四次，每次拿回一模一样的 71.5 W / 0.40·0.40·0.40·0.40 ——
            //     轮次 50 → 阻尼 0.300 → 0.150 → 0.075
            // 而顶在界上时步长是被**截掉**的，不是冲过头：降阻尼在数学上
            // 不可能让它动。每退一次要重跑一遍分钟级的解，那一整轮
            // 8.8 分钟里约一半烧在这四次可证明无效的重试上。
            //
            // ⇒ 落点与上一次逐位相同就停。这不是「提前放弃」：
            //   同一个起点、同一个模型，再算一次只会得到同一个数。
            //
            // ★ 判「没动」看的是**厚度**，不是误差。误差是耦合解自己算出来的，
            //   带一点求解器噪声（实测同一组厚度两次跑出 D=+17.4 与 +17.3）。
            //   拿 1e-9 去要求误差逐位相等，会被这点噪声骗过去、白退一次 ——
            //   厚度才是本器的状态变量，它没动就说明这一步什么都没发生。
            //   误差那一侧只用来兜底：万一真在往下走（≥0.1 %），就还是让它继续。
            double errNow = last.History.Count > 0 ? last.History[^1] : double.NaN;
            // 同样按图纸量化格判：1e-12 那种严格相等会被耦合解自身的噪声骗过去
            // （实测同一组厚度两次跑出 D=+17.4 与 +17.3），于是白退一次升级。
            bool thickFrozen = prevTh is not null
                && last.ThicknessMm.Length == prevTh.Length
                && last.ThicknessMm.Zip(prevTh, (a, b) => Math.Abs(a - b) < HalfQuantMm).All(x => x);
            bool errStuck = double.IsNaN(errNow) || double.IsNaN(prevErr)
                || errNow > prevErr * (1 - 1e-3);
            if (thickFrozen && errStuck)
            {
                int atLo = last.ThicknessMm.Count(t => t <= cur.MinThickMm * (1 + 1e-9));
                int atHi = last.ThicknessMm.Count(t => t >= cur.MaxThickMm * (1 - 1e-9));
                last.Message =
                    $"升级 {esc} 次之后落点**一步都没动**（偏差仍 {errNow:0.0} W，厚度 "
                    + string.Join("/", last.ThicknessMm.Select(t => t.ToString("0.00")))
                    + "）⇒ 停止升级。"
                    + (atLo > 0 ? $" {atLo} 片顶在厚度**下界 {cur.MinThickMm:0.00} mm** 上 —— "
                                  + "被界咬住时步长是被截掉的，不是冲过头，**降阻尼动不了它**。"
                                  + " 要么放宽下界（工艺上能不能做更薄？），要么**厚度这条路走到头了**。"
                                  // ★ 2026-09-08：原文写「这个几何在此电流下就是**无解**」——
                                  //   而顶到界的只有**厚度**。一个出口只能对它自己有的旋钮下结论。
                                  + "（不是「这个几何无解」：" + HandOffHint + "）"
                      : atHi > 0 ? $" {atHi} 片顶在厚度**上界 {cur.MaxThickMm:0.00} mm** 上 —— 同理。"
                      : " 落点不在界上却纹丝不动，多半是靶函数在此处平坦（梯度≈0）——"
                        + "换起点或改几何，继续升级没有意义。")
                    + " " + last.Message;
                break;
            }
            prevErr = errNow; prevTh = (double[])last.ThicknessMm.Clone();

            // 判断失败模式：末段误差是否还在下降
            var h = last.History;
            bool stillDescending = h.Count >= 3 && h[^1] < h[^3] * 0.9;
            if (esc == maxEscalations)
            {
                last.Message = "自动升级 " + maxEscalations + " 次后仍未达标：" + last.Message;
                break;
            }

            start = last.ThicknessMm;                 // 从当前点继续，不从头来
            if (stillDescending)
            {
                cur.MaxIterations *= 2;
                progress?.Report($"未达标但仍在收敛 ⇒ 轮次加倍到 {cur.MaxIterations}，继续…");
            }
            else
            {
                cur.Damping *= 0.5;
                cur.MaxIterations = (int)(cur.MaxIterations * 1.5);
                progress?.Report($"出现振荡 ⇒ 阻尼降到 {cur.Damping:0.000}、轮次 {cur.MaxIterations}，重试…");
            }
        }

        // 逐级定厚在**外层**统一复核，故内层调用传 verify:false，免得每轮都跑全精度
        if (verify) Verify(baseCase, makePlate, last, opt, progress, cancel);
        return last;
    }

    /// <summary>
    /// **全精度复核** —— 迭代跑在搜索精度（粗网格 + 松耦合）上，
    /// 结论必须用调用方原本的精度重算一次。
    ///
    /// ★ 为什么非做不可：搜索精度只保证**梯度方向**对，不保证数值。
    ///   不复核就把粗网格的数当结论，等于用「够用来找路的精度」去判可行性 ——
    ///   §7 记过同类教训（放宽判据后不回头验证）。
    ///
    /// 复核后**重新判定**是否达标：以全精度下的实际管根温差为准，
    /// 而不是沿用搜索期的判定。两者不一致时明说，不掩盖。
    /// </summary>
    private static void Verify(LineCase baseCase, Func<double, int, FlangePlate>? makePlate,
                               Result res, Options opt,
                               IProgress<string>? progress, CancellationToken cancel)
    {
        if (res.ThicknessMm.Length == 0) return;
        progress?.Report("全精度复核最终解…");

        var lc = CloneCase(baseCase);            // 不套搜索期的降精度设置
        if (makePlate is not null)
            lc.FlangePlates = res.ThicknessMm.Select((t, j) => makePlate(t, j)).ToArray();
        else
        {
            lc.FlangeFile3dm = baseCase.FlangeFile3dm;
            lc.ThicknessScale = (double[])res.ThicknessMm.Clone();
        }

        try
        {
            var v = LineRunner.Run(lc, progress, cancel);
            if (!v.Ok) { res.Message += "　⚠ 全精度复核失败：" + v.Message; return; }

            res.Line = v;
            // ★★★ 复核必须用**和迭代同一个**判据（2026-08-17）。
            //   我把迭代靶换成抽热窗口，却漏了这里 —— 它还在用旧的单边 ③ 度量，
            //   于是出现「复核说达标、而 ②′ = −4.47 W」这种自相矛盾的输出。
            //   **改了靶就要把所有读靶的地方一起改**，这正是今天反复在犯的那一条。
            double worst = v.Flanges.Length == 0 ? 0
                         : v.Flanges.Max(f => Math.Abs(opt.DrawTargetW - f.QFromTubeW));
            bool searchSaidOk = res.Converged;
            res.Converged = worst < opt.DrawTolW && v.Converged;

            // R48 B（2026-09-14 Opus 5）：这句进界面 ⇒ 写全名不写代号；卡交付的冷侧／热侧换成热偶读数基准，旧判法的增量温降只作对照（本类的抽热靶仍是按它的 γ 定的，本类不追新两条）。
            res.Message += $"　【全精度复核】抽热偏离靶 {worst:0.0} W" +
                           $"（管孔净流入 {v.ValueOf(LineResult.Key.NetFlux):+0.00;−0.00} W／" +
                           $"管根低于热偶读数 {v.ValueOf(LineResult.Key.ColdUnderTc):0.00} K／" +
                           $"最热铂高出热偶读数 {v.ValueOf(LineResult.Key.HotOverTc):0.00} K／" +
                           $"法兰增量温降（旧判法） {v.ValueOf(LineResult.Key.FlangeDip):0.00} K）";
            if (!v.Converged) res.Message += "；⚠ 段↔法兰耦合未收敛，数值不可引用";
            if (searchSaidOk && !res.Converged)
                res.Message += "；⚠ 搜索精度下判为达标，全精度下**不达标** —— 以本次为准";
            else if (!searchSaidOk && res.Converged)
                res.Message += "；搜索精度下未达标，全精度下达标";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { res.Message += "　⚠ 全精度复核异常：" + ex.Message; }
    }

    /// <summary>
    /// 单次迭代求解（固定轮次与阻尼）。一般用 <see cref="SolveAuto"/>。
    /// <paramref name="makePlate"/> 把厚度变成几何 —— 由调用方提供，
    /// 于是本类不关心形状（解析圆盘/舌片、阶梯、乃至 .3dm 的厚度标度都行）。
    /// </summary>
    public static Result Solve(LineCase baseCase, Func<double, int, FlangePlate>? makePlate,
                               double[] initialThicknessMm, Options? opt = null,
                               IProgress<string>? progress = null,
                               CancellationToken cancel = default)
    {
        opt ??= new Options();
        var t = (double[])initialThicknessMm.Clone();
        var res = new Result { ThicknessMm = t };
        // 每轮**入口**的厚度向量。认极限环要跟 2 轮前同相位比 —— 只留误差序列不够，
        // 误差相同未必是同一个点，而厚度相同才真是回到了原处。
        var tHist = new List<double[]>();
        // ★ 热启动跨轮传递（2026-08-17）：相邻两轮只差百分之几的厚度，
        //   上一轮的不动点离这一轮很近。这正是**取消降精度搜索**之后补速度的手段。
        //   ⚠ 只在收敛时接过 —— 没收敛的 x 不是不动点（同 Core/Sizer 的做法）。
        double[][] warmA = Array.Empty<double[]>();
        double[][] baseA = Array.Empty<double[]>();

        for (int it = 0; it < opt.MaxIterations; it++)
        {
            cancel.ThrowIfCancellationRequested();

            var lc = CloneCase(baseCase);
            lc.WarmStart = warmA;
            lc.BaselineRootC = baseA;
            // 孔周（MeshFineMm）只在显式设了才动 —— 默认不动，见 Options 里的对照表
            if (opt.SearchMeshFineMm > 0) lc.MeshFineMm = opt.SearchMeshFineMm;
            if (opt.SearchMeshCoarseMm > 0) lc.MeshCoarseMm = opt.SearchMeshCoarseMm;
            if (opt.SearchCoupleRounds > 0) lc.CoupleMaxRounds = opt.SearchCoupleRounds;
            if (opt.SearchCoupleTolK > 0) lc.CoupleTolK = opt.SearchCoupleTolK;
            if (makePlate is not null)
                lc.FlangePlates = t.Select((v, j) => makePlate(v, j)).ToArray();   // 解析几何：t 就是厚度
            else
            {
                lc.FlangeFile3dm = baseCase.FlangeFile3dm;            // .3dm：t 是厚度**标度**
                lc.ThicknessScale = (double[])t.Clone();
            }

            LineResult lr;
            try { lr = LineRunner.Run(lc, null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                res.Message = "求解异常：" + ex.Message;
                res.Iterations = it;
                return res;
            }
            // ★★★★★ **熔化 =「这一处过流截面不够」，不是「太薄了」**（2026-09-07 用户当场纠正）。
            //
            //   已定口径（用户 2026-09-03，已落在 SolveByLevel 921 行、门 OverheatDrivesSizingTests）：
            //     「过热（严重烧毁）表示电流密度过大，应该加大电流的**截面积**、降电流密度，
            //       所以可能是**增厚或增宽**（这时就需透过搜形状／搜厚度来解决）。」
            //   截面积 A = 厚 × 宽，**厚只是其中一个因子**。
            //
            //   我第一版在这里写成「各片加厚 1.5 倍」，两处都违背上面那一条：
            //     ① 整片乘 ⇒ 替不热的片也花铂（违反「能用且铂最省」）。
            //        只有**最热那一片**的截面不够，就只加那一片 ——
            //        与 SolveByLevel「哪一级热就加哪一级」同一条口径，只是本层最小粒度是片。
            //     ② 顶到厚度上界就写「这个几何在此电流下无解（是证明）」——
            //        厚度到顶只证明**厚度**救不了，**宽度这根旋钮一次都没动过**。
            //        那正是 2026-09-03 用户叫停的「一句话判死刑结案」。
            //
            //   ⚠ 本层（Solve，解析几何路）**只有厚度这一根旋钮** ——
            //     宽／形状由调用方的 makePlate 定死，opt 里也只有 Min/MaxThickMm。
            //     所以这里能做的只有「加厚最热那一片」；加不动时**必须如实交棒**，
            //     指向真有宽度旋钮的那两处（SolveByLevel 的搜形状 / Solver 的槽·孔·环宽），
            //     而不是替它们下「无解」的结论。
            if (!lr.Ok && lr.OverMelt)
            {
                // 只加**最热那一片**（本层没有「级」，最小粒度就是片）
                int jh = -1; double hot = double.NegativeInfinity;
                for (int q = 0; q < lr.Flanges.Length && q < t.Length; q++)
                    if (lr.Flanges[q].TMaxC > hot) { hot = lr.Flanges[q].TMaxC; jh = q; }

                if (jh >= 0)
                {
                    double next = Math.Min(opt.MaxThickMm, t[jh] * 1.5);
                    if (next > t[jh] + HalfQuantMm)
                    {
                        double prev = t[jh];
                        t[jh] = next;
                        res.Trace.Add($"{BranchMarks.MeltRaiseHottest}：第 {jh + 1} 片 {hot:0} °C，"
                                    + $"厚 {prev:0.00} → {next:0.00} mm");
                        res.Message = $"第 {it + 1} 轮**熔化**（第 {jh + 1} 片 {hot:0} °C）⇒ 该处过流截面不够，"
                                    + $"**只加这一片**的厚度到 {next:0.00} mm 再试"
                                    + "（截面 A = 厚 × 宽，本层只有「厚」这一根）";
                        continue;
                    }
                }

                // 厚度到顶 —— 这**只**证明厚度救不了，不是无解。
                res.Trace.Add(BranchMarks.MeltHandOff
                            + $"：{(jh >= 0 ? $"第 {jh + 1} 片 {hot:0} °C" : "各片")}，"
                            + $"厚度已在上界 {opt.MaxThickMm:0.00} mm");
                res.Terminal = true;
                res.TerminalWhy =
                    (jh >= 0 ? $"第 {jh + 1} 片 {hot:0} °C 熔化，其厚度已在工艺上界 {opt.MaxThickMm:0.00} mm 上"
                             : $"熔化，各片厚度均已在工艺上界 {opt.MaxThickMm:0.00} mm 上")
                  + " ⇒ **加厚这根旋钮到顶了**。截面 A = 厚 × 宽，"
                  + "下一根是**增宽过流带**（半径分布 / 舌半宽 / 少挖孔）—— "
                  + "本层没有那根旋钮，要走「◇ 搜形状」或求解器的槽·孔·环宽。"
                  + "**不是无解**：宽度一次都还没动过。";
                res.Message = res.TerminalWhy + "（" + lr.Message + "）";
                res.Iterations = it; return res;
            }

            if (!lr.Ok) { res.Message = lr.Message; res.Iterations = it; return res; }
            baseA = lc.BaselineRootC;                       // 基线只随管几何变，可无条件复用
            if (lr.Converged) warmA = lc.WarmStart;         // 抽热/端温只在收敛时才是不动点

            res.Line = lr;
            res.Iterations = it + 1;

            // ★★★★★ 误差量（2026-08-17 换掉，原来追的是一个**够不着的靶**）。
            //
            // 原：err = RootDeltaK − TargetK，即「偏离本段控温点」。
            // 那个量 2026-08-15 已被降为参考量，理由写在 LineRunner.Judge 里：
            //   接上段间导热后，共用法兰处的管温由**两侧控温点**决定
            //   （实测 HC1|HC2 接头停在 1116 °C = 1150 与 1080 的中间，偏离本段 34 K），
            //   **与法兰设计无关**。让法兰去背控温点梯度的锅 = 给优化器一个够不着的靶。
            // 判据体系改了，**定尺寸器一直没跟着改** —— 于是它按 34~37 K 的地板去追
            // 5 K 的靶，把四片一路削到 0.4 mm 下界仍差 40 K。
            // 实测（Pt_Heater3.3dm）：35 轮后厚度 0.44/1.12/1.10/0.42，偏差停在 39.5 K 不动。
            //
            // 现在追 ③ **法兰增量温降**（= 无法兰基线 − 实际），它按构造就把控温点梯度剔除了，
            // 正是判据实际判的那个量。
            //
            // ⚠ 但 ③ 不能当**双向**靶：∂③/∂板厚 = +149 K/mm 是**正号**，
            //   若 ③ 已经低于靶，双向控制会去**加厚**板把 ③ 抬到靶上 ——
            //   花铂金把判据推坏（HANDOVER §1.83 推论 2）。
            //   ⇒ 做成**单边**：只在 ③ 偏大时往薄里走；③ 已经够小就不动它，
            //     让「越薄越省铂」自己去撞另一侧的界（②′ 净流入 > 0）。
            var dips = lr.Segments.Select(s => s.FlangeDipK).ToArray();
            bool dipOk = dips.All(v => !double.IsNaN(v));
            if (!dipOk)
            {
                // 2026-09-14 Opus 5（复审）：这几句经 SolveByLevel 的内层（SolveAuto）拼进 Message，再进界面「自动定厚」说明 ⇒ 写全名不写代号。
                //   这里的「增量温降」是旧判法那条（③，代号不换主人），已降为参考量；本器的靶仍按它定。
                res.Message = "无法兰基线没算出来 ⇒ 法兰增量温降（旧判法）无从得知，定尺寸器**拒绝瞎调**。" +
                              "（原来会退回追「偏离本段控温点」，那是个够不着的靶。）";
                return res;
            }
            // ★★★ 误差量 = **抽热离窗口靶有多远**（双向），不再是「③ 超了多少」（单边）。
            //   逐片取，不用再从段误差插值 —— 抽热本来就是**每片**的量，一一对应。
            var draws = lr.Flanges.Select(f => f.QFromTubeW).ToArray();
            var errW = new double[t.Length];
            for (int j = 0; j < t.Length; j++)
                errW[j] = opt.DrawTargetW - (j < draws.Length ? draws[j] : opt.DrawTargetW);
            double worst = errW.Length == 0 ? 0 : errW.Max(Math.Abs);
            res.History.Add(worst);
            tHist.Add((double[])t.Clone());        // 本轮**入口**的厚度，用来认极限环
            progress?.Report($"第 {it + 1} 轮：抽热最大偏差 {worst:0.0} W　" +
                             $"D {string.Join("/", draws.Select(v => v.ToString("+0.0;−0.0")))}　" +
                             $"法兰增量温降（旧判法）最大 {dips.Max():0.0}　厚度 " +
                             string.Join("/", t.Select(x => x.ToString("0.00"))));

            if (worst < opt.DrawTolW)
            {
                res.Converged = true;
                res.Message =
                    $"{it + 1} 轮收敛：各片抽热已落进窗口（靶 {opt.DrawTargetW:0.#} W，" +
                    $"实测 {draws.Min():+0.0;−0.0}…{draws.Max():+0.0;−0.0} W）。" +
                    $"法兰增量温降（旧判法）随之为 {dips.Max():0.00} K。\r\n" +
                    "  管孔净流入 > 0 与 法兰增量温降（旧判法）≤ 限值 是同一个抽热的两侧（增量温降 = 2.40·D 实测）⇒ 一个靶守住这两条；" +
                    "卡交付的「最热铂高出热偶读数」「管根低于热偶读数」基准是热偶读数，**本器不追它们**，要看判据表。";
                return res;
            }

            // ★★★★★ 2-周期极限环（2026-08-23 加）。
            //
            // 实测（Pt_Heater3.3dm 外层第 3 轮）：厚度从第 3 轮起就在两个点之间来回跳
            //     0.54/0.97/0.89/0.51  ⇄  0.64/0.96/0.89/0.40
            // 偏差跟着跳 13.2 ⇄ 11.3，一路跳到 25 轮跑满才轮到降阻尼。
            // 中间那二十轮每一轮都是一次整线耦合解，全是白算 ——
            // **这个环第 5 轮就认得出来**。
            //
            // 与「不动点」不同的是：极限环**降阻尼真的有用**（步子迈过头了），
            // 所以这里不是终止，是**提前交棒**给 SolveAuto 的升级层去减阻尼。
            // 判据要同相位比（跟 2 轮前比，不是跟上一轮比）——
            // 隔相位比的话，高点跟低点比，永远看着像「在下降」。
            if (tHist.Count >= 3 && res.History.Count >= 3
                && worst >= res.History[^3] * (1 - 1e-3)
                && tHist[^1].Length == tHist[^3].Length
                && tHist[^1].Zip(tHist[^3], (a, b) => Math.Abs(a - b) < 1.5 * QuantMm).All(x => x))
            {
                res.Message =
                    $"{it + 1} 轮后进入**2 周期极限环**：厚度在两个点之间来回跳"
                    + $"（{string.Join("/", tHist[^2].Select(x => x.ToString("0.00")))}"
                    + $" ⇄ {string.Join("/", tHist[^1].Select(x => x.ToString("0.00")))}），"
                    + $"偏差同相位停在 {worst:0.0} W 不再下降。"
                    + "步子迈过头了 —— 交给上层减阻尼重试，继续按当前阻尼迭代只会一直跳。";
                res.Iterations = it + 1;
                return res;
            }

            int pinned = 0, frozen = 0;
            for (int j = 0; j < t.Length; j++)
            {
                // e > 0 ⇒ 抽热不够（②′ 危险）⇒ **加厚**；e < 0 ⇒ 抽太多（③ 危险）⇒ 削薄。
                // 方向来自实测 dD/d板厚 > 0（+62…+107 W/mm）。
                // ⚠ 旧代码这里是 `-Damping*e/...`（单边、只会削薄）。符号换了，是因为靶换了。
                double dtMm = errW[j] / Math.Max(1.0, opt.DrawSensWPerMm);
                double step = Math.Clamp(opt.Damping * dtMm / Math.Max(0.1, t[j]),
                                         -opt.MaxLogStep, opt.MaxLogStep);
                double want = t[j] * Math.Exp(step);
                double next = Math.Clamp(want, opt.MinThickMm, opt.MaxThickMm);
                // 想往界外走、且已经贴着那个界 ⇒ 这一片被钉死了
                if ((want < opt.MinThickMm && t[j] <= opt.MinThickMm * 1.001) ||
                    (want > opt.MaxThickMm && t[j] >= opt.MaxThickMm * 0.999)) pinned++;
                // ★ 这一片这一步**实际走了多远**。厚度按 0.01 mm 量化（图纸精度，
                //   比这更细的差别造不出来），走不满半格就等于没走。
                if (Math.Abs(next - t[j]) < HalfQuantMm) frozen++;
                t[j] = next;
            }

            // ★ 全部变量都顶在边界且还想继续往界外走 ⇒ 再迭代也不会动，立即停。
            //   早先没有这条：某算例四片全钉在 0.4 mm 下界，求解器仍跑满
            //   25 轮 × 自动升级 4 次 = 125 次整线耦合解（每次 15 轮耦合 × 7 个场解，
            //   合计约一万三千次场解），**一格算了一个多小时才吐出一个必然失败的结果**。
            // ★★★★★ 不动点：**没有一片还动得了**（2026-08-23 加）。
            //
            // 上面那条只认「四片全钉在界上」。实测（Pt_Heater3.3dm）打不中的形态是：
            //     厚度 0.40/0.85/0.75/0.40　D +3.9/+2.0/+2.0/+22.0（靶 2 W）
            // 两片钉在 0.40 下界、另两片**已经落在靶上**（步长≈0）⇒ pinned 只有 2，
            // 那条不响，于是第 5~25 轮打出了二十一行**一模一样**的数；
            // 升级一次之后又是 37 行一模一样的数。
            //
            // ⇒ 判据改成「这一步四片加起来一格都没挪动」，并且**误差连续三轮没降**。
            //   两个条件缺一不可：只看误差会误杀慢收敛（步子小但一直在降），
            //   只看步长会误杀「刚好这一轮走得小」。
            bool noGain = res.History.Count >= 4
                          && worst >= res.History[^4] * (1 - 1e-3);
            if (pinned != t.Length && frozen == t.Length && noGain)
            {
                res.Message =
                    $"{it + 1} 轮后**没有一片还动得了**：最大偏差停在 {worst:0.0} W 连续三轮没降，"
                    + "而这一步四片的厚度改动都不足图纸精度的半格（0.005 mm）。"
                    + $"　厚度 {string.Join("/", t.Select(x => x.ToString("0.00")))}"
                    + $"　抽热 {string.Join("/", draws.Select(v => v.ToString("+0.0;−0.0")))} W（靶 {opt.DrawTargetW:0.#}）。"
                    + Environment.NewLine + "  典型形态：一部分片钉在厚度界上、另一部分已经落在靶上 —— "
                    + "**这是不动点，不是迭代不够**。差的那几片只能靠改几何"
                    + "（过流断面 / 保温分区 / 舌长），加轮数或降阻尼都动不了它。";
                res.Iterations = it + 1;
                return res;
            }

            if (pinned == t.Length)
            {
                bool tooThin = errW.Average() > 0;   // 还想加厚却顶在上界 / 还想削薄却顶在下界
                res.Message = $"{it + 1} 轮后全部厚度顶在" +
                              (tooThin ? $"上界 {opt.MaxThickMm:0.00}" : $"下界 {opt.MinThickMm:0.00}") +
                              $" mm 仍进不了抽热窗口（最大偏差 {worst:0.0} W）—— " +
                              // ★ 2026-09-08：标题原写「该几何在此工况下**无解**」，而穷尽的只有厚度；
                              //   底下那两句本来就已经在指「更大的过流断面 / 缩小法兰」——**标题比正文说大了**。
                              "**厚度这条路走到头了，不是迭代不够**（" + HandOffHint + "）。\r\n" +
                              (tooThin
                               ? "  还想加厚 = 抽热不够 = 法兰太热、热在往管里灌（管孔净流入 < 0）⇒ 需要更大的过流断面或更少的发热。"
                               : "  还想削薄 = 抽热太多 = 把管根抽出深坑（法兰增量温降超限，旧判法）⇒ 需要缩小法兰或加保温。");
                return res;
            }
        }

        res.Message = $"{opt.MaxIterations} 轮未收敛（抽热最大偏差 {res.History.LastOrDefault():0.0} W）。" +
                      // ★ 2026-09-08：原文「或该形状在此电流下**无解**」—— 带了「可能/或」也仍是
                      //   替**形状**下结论，而形状不在本层手里。
                      "可能是某片已顶到厚度上下界，或**该形状要改** —— " + HandOffHint + "。";
        return res;
    }

    /// <summary>
    /// 第 <paramref name="plateIdx"/> 片在外层标量 <paramref name="k"/> 下的各级厚度
    /// = 该片的**基准级厚** × 当前各级比例 × k。
    ///
    /// 抽出来单放，是因为它同时踩过两个雷，而两个都**不会报错**：
    ///   ① 片序号原来靠闭包里的 `callIdx++ % 片数` 推 —— 只在「每轮恰好按序调 nf 次」
    ///      时才对，多调少调一次，第 2 片就拿到第 3 片的级配比；
    ///   ② 基准级厚原来一律取 `levelThicknessMm[0]`（第 0 片的）——
    ///      逐片基准不同时，其余三片全在按第 1 片的基准算。
    /// 两个雷都只会让优化器对着**别的板**求解，输出照样是一张漂亮的判据表。
    /// 循环本身要跑真解、验不动；这个函数是纯的，一条断言几微秒。
    /// </summary>
    /// <summary>
    /// 这一片还能整体加厚多少倍才碰到工艺上界。
    /// ⚠ 取**各级里最先碰界**的那一个 —— 有一级碰界就不能再整体乘了。
    /// </summary>
    private static double MaxRaiseK(double[] scaleJ, double[] levelJ, double maxThickMm)
    {
        double k = double.PositiveInfinity;
        for (int m = 0; m < scaleJ.Length && m < levelJ.Length; m++)
        {
            double cur = levelJ[m] * scaleJ[m];
            if (cur <= 1e-9) continue;
            k = Math.Min(k, maxThickMm / cur);
        }
        return double.IsInfinity(k) ? 1.0 : Math.Max(1.0, k);
    }

    /// <summary>
    /// 给一组各级倍数，算一次整线 —— **只算不调**（加厚试探用）。
    /// ⚠ 与 <see cref="LevelSolver"/> 的 Eval 同一条路：拷贝走 CloneCase，
    ///   解走 LineRunner.Run。判据永远只有那一份来源。
    /// </summary>
    private static LineResult? EvalScale(LineCase bc, double[][] levelThicknessMm,
                                         double[][] scale, CancellationToken cancel,
                                         IProgress<string>? inner, bool coarse = false)
    {
        try
        {
            var lc = CloneCase(bc);
            lc.LevelThicknessMm = levelThicknessMm;
            lc.LevelScale = scale;
            // ★★★ 试探用**更粗的网格**（2026-09-03）。
            //   第一版直接用默认网格 ⇒ 每次试探是一次**完整整线解**（分钟级），
            //   探上界 1 次 + 二分 8 步 = 9 次/轮 × 6 轮 = 多出 54 次全解 ⇒ **当场超时**。
            //   实测记录：deliverable/F_改后_加厚试探超时.txt。
            //   ⚠ 这里只回答一个是非题：「加厚之后那一级凉没凉」。
            //     那是个**趋势**判断，粗网格够用；最终判据仍走原网格（本函数只供试探）。
            if (coarse)
            {
                // ★★★★★ 2026-09-03 更正（用户点出焊缝环）：**只放粗平坦区，孔边保细**。
                //   上一版把 MeshFineMm 2 -> 4，而**焊缝环只有 leg ~ 1-2.5 mm 宽**
                //   （weld = 2·[leg - sqrt(leg^2-(d-leg)^2)]，孔边最厚、leg 内衰减到 0），
                //   4 mm 的格子根本分辨不了它，各级台阶同理。
                //   后果不是「慢一点」，是**判错哪一级最热** => 加厚加到不该加的那一级。
                //   陡梯度处不省，平坦的圆盘外缘与舌片才省。
                lc.MeshCoarseMm = Math.Max(lc.MeshCoarseMm, 20.0);
            }
            var r = LineRunner.Run(lc, inner, cancel);
            return r.Ok ? r : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }


    public static double[] LevelThicknessFor(IReadOnlyList<double[]> baseLevels,
                                             IReadOnlyList<double[]> scale,
                                             int plateIdx, double k)
    {
        if (baseLevels.Count == 0 || scale.Count == 0)
            throw new ArgumentException("基准级厚或各级比例是空的 —— 造不出这一片");
        var lv = scale[Math.Clamp(plateIdx, 0, scale.Count - 1)];
        var bs = baseLevels[Math.Clamp(plateIdx, 0, baseLevels.Count - 1)];
        if (bs.Length != lv.Length)
            throw new ArgumentException(
                $"第 {plateIdx + 1} 片：基准 {bs.Length} 级、比例 {lv.Length} 级，对不上 —— " +
                "对不上就别凑，凑出来的是另一片板的厚度");
        var th = new double[lv.Length];
        for (int m = 0; m < lv.Length; m++) th[m] = bs[m] * lv[m] * k;
        return th;
    }

    /// <summary>残差走势。两种「没到」要给**相反**的建议，所以必须分开。</summary>
    public enum Trend
    {
        /// <summary>轮数还不够判</summary>
        数据不足,
        /// <summary>还在往下走 ⇒ 是轮数不够，加轮数有用</summary>
        还在缩,
        /// <summary>进了平台或在震荡 ⇒ 再跑也是白跑，要改的是几何</summary>
        已停滞,
    }

    /// <summary>
    /// 残差序列还在不在往下走。
    ///
    /// 取最近 <paramref name="window"/> 轮的**最好值**，跟更早各轮的最好值比：
    /// 改善不足 <paramref name="minGain"/>（相对）就判停滞。
    ///
    /// ★ 比的是**最好值**而不是最后一个值 —— 这条曲线本来就会震荡，
    ///   只看最后一轮，一次向上的抖动会被读成「越跑越差」，
    ///   而一次向下的抖动会被读成「还有救」，两个方向都会骗人。
    ///
    /// 为什么要分这两种：跑满轮数没到的时候，「加大轮数再来一次」和
    /// 「回 Rhino 改几何」是完全相反的两条路。只报一句「未收敛」，
    /// 工程师只能靠猜 —— 而每猜错一次的代价是几十分钟。
    /// <summary>
    /// 从末尾往回数：**连续有几轮比上一轮更差**（hist 越小越好）。
    ///
    /// ★ 为什么抽成纯函数（2026-08-25，用户要求「一路变坏就即刻停」）：
    ///   两个定尺寸器各有一个几十分钟的循环，都要这条判断。
    ///   写在循环里就只能靠跑满几十分钟才验得到 —— 而它本身是纯算术，微秒可验。
    ///   **同一个教训今天第四次**（TrendOf / LevelThicknessFor / JointThickness 都是这么抽的）。
    ///
    /// ⚠ 严格「更差」才算，持平不算：持平归 <see cref="TrendOf"/> 的「已停滞」管，
    ///   两条规则不该抢同一件事。
    /// </summary>
    public static int WorseningRun(IReadOnlyList<double> hist)
    {
        if (hist is null) return 0;
        int n = 0;
        for (int i = hist.Count - 1; i > 0 && hist[i] > hist[i - 1]; i--) n++;
        return n;
    }

    /// </summary>
    public static Trend TrendOf(IReadOnlyList<double> hist, int window = 2, double minGain = 0.05)
    {
        if (hist is null || hist.Count < window + 1) return Trend.数据不足;
        double bestOld = double.PositiveInfinity, bestNew = double.PositiveInfinity;
        for (int i = 0; i < hist.Count - window; i++) bestOld = Math.Min(bestOld, hist[i]);
        for (int i = hist.Count - window; i < hist.Count; i++) bestNew = Math.Min(bestNew, hist[i]);
        // NaN 会让 Math.Min 传染，判不了就说判不了 —— 别拿它当「停滞」把人劝退
        if (double.IsNaN(bestOld) || double.IsNaN(bestNew)
            || double.IsInfinity(bestOld) || double.IsInfinity(bestNew)) return Trend.数据不足;
        if (bestOld <= 0) return Trend.已停滞;          // 已经压到 0，没有可改善的余地
        return bestNew < bestOld * (1 - minGain) ? Trend.还在缩 : Trend.已停滞;
    }

    /// <summary>
    /// **逐级定厚**（.3dm 专用）—— 让优化器自己决定各级厚度的比例。
    ///
    /// 两条约束由两组自由度分别负责，互不干扰，所以可以分层：
    ///
    /// | 层 | 自由度 | 管的约束 | 机理 |
    /// |---|---|---|---|
    /// | 外层 | 每片一个**整体**倍数 | C2 管根温差 | 整片发热总量 ⇒ 从管子抽多少热 |
    /// | 内层 | 每片各级的**相对**比例 | C1 局部过热 | 单位面积发热 = ρe·K²/t ⇒ 加厚哪一级，哪一级就变凉 |
    ///
    /// 内层调完后把各级比例**归一化**（几何平均拉回 1），于是整片的平均厚度不变，
    /// 外层看到的热平衡几乎不动 —— 这就是两层能解耦的原因。
    ///
    /// 内层的靶：让各级的局部峰值温度**齐平**（都压到管根温度附近）。
    /// 哪一级更热就加厚哪一级，热量被摊到其余级去。
    /// </summary>
    /// <param name="levelLocked">
    /// `[片][级]` 为 true 的级**完全不动**（厚度锁死）。典型用法：
    /// 「外圈厚度不动、只调内圈」—— 把外圈那级锁上，优化器只在其余级上找解。
    /// 全为 null = 各级都可动（优化器自行决定比例）。
    ///
    /// ⚠ 锁级会同时削掉外层的调节能力：整片热平衡只能靠**未锁的级**去凑，
    /// 若未锁的级面积占比很小，可能怎么调都够不到目标 —— 那时返回未收敛，
    /// 并在 Message 里说明是被锁死限制的，而不是物理上无解。
    /// </param>
    /// <param name="makePlateByLevel">
    /// 解析几何的逐级构造器：给一组各级厚度，返回 FlangePlate（用 DiscStepRadii/Thickness）。
    /// 传了它，搜索期就走**解析路径**；为 null 则走 .3dm + 厚度标度。
    ///
    /// ⚠ 这里原本写着「不经 Rhino，**快一个量级**」—— **实测不成立，2026-08-23 更正**。
    ///   那句话写在一段**从未被执行过**的代码上（此前没有任何调用方传这个参数）。
    ///   实测（Pt_Heater3.3dm，`--surrogate --bench`）：
    ///     · 首次读厚度场 11.4 s（起 Geom 子进程），但
    ///       <see cref="Geometry3dm.LoadThickness"/> **本来就带缓存**
    ///       （同一「文件|图层|平面|步长」只提一次）⇒ 子进程每个文件只起一次，不是每次评估。
    ///     · 之后每片：.3dm 读缓存 + 建网格 3.1 ms，解析直接建网格 2.7 ms —— **只差 13 %**。
    ///   而一次整线解要跑几百轮耦合场解，几何这一步本来就微不足道。
    ///   ⇒ 传它**几乎不会让 ④ 变快**。它的真正用处是给纯解析算例一条不碰图纸的路。
    ///   ④ 在大几何上慢，原因在别处（空转的迭代与升级，见本文件的三处不动点/极限环判据）。
    ///
    /// ⚠ 传了它也只影响**搜索期**：全精度复核在 baseCase 指着 .3dm 时一律回到原图纸，
    ///   所以报出去的数始终是图纸的数。
    /// </param>
    public static Result SolveByLevel(LineCase baseCase, double[][] levelThicknessMm,
                                      Options? opt = null, IProgress<string>? progress = null,
                                      CancellationToken cancel = default, int outerRounds = 6,
                                      bool[][]? levelLocked = null,
                                      Func<double[], FlangePlate>? makePlateByLevel = null)
    {
        bool Locked(int j, int m) => levelLocked is not null && j < levelLocked.Length
                                     && m < levelLocked[j].Length && levelLocked[j][m];
        opt ??= new Options();
        // ★ R47 C（2026-09-13）：终局细网格 —— 设了就整个求解（各轮搜索 + 全精度复核）都在那张网格上，
        //   下面所有 CloneCase(baseCase) 都从这份带网格口径的副本出发（求根与判决同一张网格）。
        baseCase = ApplyFinalMesh(baseCase, opt);
        int nf = levelThicknessMm.Length;
        var scale = new double[nf][];
        for (int j = 0; j < nf; j++)
        {
            scale[j] = new double[levelThicknessMm[j].Length];
            for (int m = 0; m < scale[j].Length; m++) scale[j][m] = 1.0;
        }
        // ★ 过热驱动的**厚度下界**（只增不减）。外层按抽热误差削薄时不许削到它以下 ——
        //   否则这一轮为压过热加的厚，下一轮就被抹掉，两层互相打架（人看到的是「跑很久、数不动」）。
        // ⚠ 必须放在 scale[j] 分配**之后**：放前面时 scale[j] 全是 null，当场 NRE。
        var overheatFloor = scale.Select(a => new double[a.Length]).ToArray();
        // ★★★ 加厚试探**按（片, 级）记**，不是全局只试一次（2026-09-03 更正）。
        //   上一版为省成本写成全局 bool：第 1 轮加厚成功 continue，
        //   第 2 轮就被跳过 => 仍然过热时**落回老熔点闸判死刑** ——
        //   省成本的那一刀把功能本身砍掉了（实测 deliverable/F_改后2.txt 第 3 步：
        //   仍然印着「回 Rhino」那句没量过的话）。
        //   按（片,级）记：同一级不重复试（重复也是同一句话），别的级仍然试得到。
        //   ⚠ 用**次数**不用 bool：加厚成功之后这一级变厚了，下一轮若仍过热，
        //     它是**新厚度下的新问题**，该再试一次（hiK 会随厚度逼近上界而收敛到 1，
        //     所以会自然停）。写成 bool 的后果实测过：第 1 轮加厚成功 continue，
        //     第 2 轮被挡掉 ⇒ 落回老熔点闸那句**没量过**的「回 Rhino」。
        var raiseTries = scale.Select(a => new int[a.Length]).ToArray();
        // ★★★ 每级只试**一次**（2026-09-04 实测定的）。
        //   放到 2 次 + 原网格复核 + 孔边保细之后，自动定厚**又超时了**
        //   （deliverable/F_改后4.txt）。功能上真正要紧的是「出口都带实测结论」，
        //   不是试几次 —— 试一次已经能给出「加厚有用／没用」这个判断。
        //   ⚠ 试探用的是**整线解**，一次就是分钟级。想再省只有一条路：
        //     只解**那一片**的场（过热是单片问题）—— 但那要重建管根温/电流/保温分界，
        //     等于把边界条件抄第二份，正是本仓最忌的「同一个数多处来源」。⇒ 不走。
        const int MaxRaiseTries = 1;

        Result last = new();
        // 每轮的内层残差（各级峰值超管根 K）。用来分「轮数不够」和「已经在原地打转」。
        var hist = new List<double>();
        int stalledAt = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 0; round < outerRounds; round++)
        {
            cancel.ThrowIfCancellationRequested();

            // ── 外层：在**当前各级比例**下，求每片的整体倍数，使管根温差达标
            var overall = new double[nf];
            for (int j = 0; j < nf; j++) overall[j] = 1.0;
            var lcBase = CloneCase(baseCase);
            lcBase.LevelThicknessMm = levelThicknessMm;
            lcBase.LevelScale = scale;

            // ★ 报用时：大几何上一轮就是分钟级，六轮跑满能到半小时。
            //   不报的话，界面上只有一句不动的「正在算」，人分不清是在算还是卡死了，
            //   于是要么白等，要么中途掐掉一个本来快好了的解。
            progress?.Report($"第 {round + 1}/{outerRounds} 轮 · 外层：调整每片整体厚度…"
                             + $"（已用 {clock.Elapsed.TotalMinutes:0.0} 分）");
            Func<double, int, FlangePlate>? mk = null;
            if (makePlateByLevel is not null)
            {
                // 解析路径：外层的标量 k 乘在**当前各级比例**上，构造第 j 片的几何。
                //
                // ★★★★★ 片序号原来是靠一个**有状态计数器** `callIdx++ % 片数` 推出来的
                //   （2026-08-23 换掉）。它只在「每一轮恰好按序调 nf 次」时才对 ——
                //   Solve 里的 `t.Select(makePlate)` 碰巧就是这样，所以它一直是**巧合成立**。
                //   任何一处多调或少调一次，第 2 片就会拿到第 3 片的级配比：
                //   不抛异常、不报错，只是给出一组针对别的板算出来的厚度。
                //   这段代码从来没被执行过，所以这个雷一直没响。
                //   ⇒ 序号改成由调用方显式传入，counter 这个东西根本不该存在。
                //
                // 另修：原式对所有片都用 `levelThicknessMm[0]`（第 0 片的基准级厚），
                //   逐片基准不同时会静默取错。现在按 j 取。
                var snap = scale.Select(a => (double[])a.Clone()).ToArray();
                mk = (k, j) => makePlateByLevel(LevelThicknessFor(levelThicknessMm, snap, j, k));
            }
            last = SolveAuto(lcBase, mk, overall, opt, progress, cancel, 4, verify: false);
            if (last.Line is null) return last;

            // 把外层求出的整体倍数并进各级比例 —— **锁住的级不并**
            for (int j = 0; j < nf; j++)
            {
                double kj = j < last.ThicknessMm.Length ? last.ThicknessMm[j] : 1.0;
                for (int m = 0; m < scale[j].Length; m++)
                    if (!Locked(j, m))
                        // ★ 不许削到**过热下界**以下（2026-09-03）：那条界是为了压住
                        //   局部熔化才加上去的，抽热误差没资格把它抹掉。
                        scale[j][m] = Math.Max(scale[j][m] * kj, overheatFloor[j][m]);
            }

            // ── 内层：按各级峰值温度重新分配比例（总平均厚度不变）
            var lr = last.Line;
            double worstOver = 0;
            // ★ 最热的那一级：用来判「还值不值得继续迭代」
            double hottestC = double.NegativeInfinity; int hottestPlate = -1, hottestLevel = -1;
            for (int j = 0; j < nf && j < lr.Flanges.Length; j++)
            {
                var f = lr.Flanges[j];
                if (f.LevelTMaxC.Length != scale[j].Length) continue;
                double baseT = f.TRootC;
                var adj = new double[scale[j].Length];
                double logSum = 0; int cnt = 0;
                for (int m = 0; m < adj.Length; m++)
                {
                    if (Locked(j, m)) { adj[m] = 1.0; continue; }      // 锁死：一步都不走
                    double tm = f.LevelTMaxC[m];
                    double e = double.IsNaN(tm) ? 0 : tm - baseT;      // >0 = 该级比管根热
                    worstOver = Math.Max(worstOver, e);
                    if (!double.IsNaN(tm) && tm > hottestC)
                    { hottestC = tm; hottestPlate = j; hottestLevel = m; }
                    // 越热越加厚：Δln t = +ω·e/S
                    double st = Math.Clamp(opt.Damping * e / opt.SensitivityK, -0.30, 0.30);
                    adj[m] = Math.Exp(st);
                    logSum += Math.Log(adj[m]); cnt++;
                }
                // 归一化：几何平均拉回 1 ⇒ 只改**比例**，不改整片平均厚度
                // 归一化只摊在**未锁**的级上（锁住的级不参与，也不该被 norm 拉动）
                double norm = cnt > 0 ? Math.Exp(logSum / cnt) : 1.0;
                for (int m = 0; m < adj.Length; m++)
                {
                    if (Locked(j, m)) continue;
                    scale[j][m] = Math.Clamp(scale[j][m] * adj[m] / norm,
                                             Math.Max(overheatFloor[j][m],
                                                      opt.MinThickMm / Math.Max(1e-6, levelThicknessMm[j][m])),
                                             opt.MaxThickMm / Math.Max(1e-6, levelThicknessMm[j][m]));
                }
            }
            // ★ 报**方向**，不只报绝对值（用户 2026-08-25）：
            //   「超管根 12.3 K」只说了现在多少，说不出「这一轮是在变好还是变坏」——
            //   而工程师盯着一条几十分钟的进度条，最想知道的正是后者。
            string dirMark = "";
            if (hist.Count > 0)
            {
                double prev = hist[hist.Count - 1];
                double d = worstOver - prev;
                dirMark = Math.Abs(d) <= 0.05 * Math.Max(1e-9, Math.Abs(prev))
                        ? "　≈ 与上一轮持平"
                        : d < 0 ? $"　↓ 比上一轮**好** {prev - worstOver:0.0} K"
                                : $"　↑ 比上一轮**差** {worstOver - prev:0.0} K";
            }
            progress?.Report($"第 {round + 1} 轮 · 内层：各级峰值最高超管根 {worstOver:0.0} K" +
                             (hottestPlate >= 0 ? $"（第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级 {hottestC:0} °C）" : "") + dirMark);

            // ═══════════════════════════════════════════════════════════════
            // ★★★★★ **过热要能驱动加厚**（用户 2026-09-03）
            //
            //   用户原话：「过热（严重烧毁）表示电流密度过大，应该加大电流的截面积、
            //   降电流密度，所以可能是增厚或增宽…APP 要提供一个首先能用的且铂金用量
            //   最少的方案出来，而不是一句话就判死刑结案了」。
            //
            //   ══ 在此之前这条通路是**断的**
            //
            //   本器两层，而**没有一层会因为「过热」去加厚**：
            //     外层 t[j]：由 errW[j]（抽热误差 ②′/③）驱动 —— 不看局部温度。
            //                ③ 抽太多时它的倾向反而是**削薄**。
            //     内层 scale[j][m]：只改**比例**，调完做几何平均归一化（adj[m]/norm）
            //                ⇒ 加厚一级必然削薄另一级，**整片平均厚度不变**；
            //                  而等厚板（1 级）连挪都没得挪（norm ≡ adj[0]）。
            //   于是一超熔点就只剩「判死刑」这一条路。
            //
            //   ══ 现在：先**实测**加厚有没有用，再决定
            //
            //   加厚到工艺上界评一次：
            //     · 凉下来了 ⇒ 厚度这根旋钮**有用** ⇒ 二分求**最小**的够用厚度
            //       （最小 = 最省铂，符合「能用且铂最少」）
            //     · 仍不凉   ⇒ 这是**实测**出来的「厚度救不了」，不是断言
            //       ⇒ 交给下一根旋钮：增宽（搜形状）。指路读 Terminal 转过去。
            //
            //   ⚠ 加厚是**只增不减**的下界（overheatFloor）：外层下一轮按抽热误差
            //     去削薄时不许削到它以下 —— 否则这一轮加的厚下一轮就被抹掉，
            //     两层互相打架，人看到的是「跑很久、数不动」。
            // ★ 一次求解只做**一轮**加厚试探（2026-09-03）：每轮都试等于把成本乘上轮数，
            //   而加出来的厚度是只增不减的下界，后续轮次会带着它继续跑，不会丢。
            if (hottestPlate >= 0 && hottestLevel >= 0
                && hottestC > opt.OverheatRaiseFromC
                && raiseTries[hottestPlate][hottestLevel] < MaxRaiseTries)
            {
                // ★★★★★ 2026-09-03 更正（用户指出）：**局部过热要加的是局部截面**，
                //   不是整片乘一个倍数。过热是逐级算出来的（LevelTMaxC[m]），
                //   哪一级热就加哪一级 —— 整片加厚等于替不热的那些级也花铂，违反「最省」。
                int jh = hottestPlate, mh = hottestLevel;
                double curK = 1.0;                       // 相对**这一级**当前厚度的倍数
                double curMm = levelThicknessMm[jh][mh] * scale[jh][mh];
                double hiK = curMm > 1e-9 ? Math.Max(1.0, opt.MaxThickMm / curMm) : 1.0;

                raiseTries[jh][mh]++;
                if (hiK <= 1.0 + 1e-9)
                {
                    // ★★★ 这也是一条**实测**结论：这一级已经在工艺上界上，再加不动了。
                    //   以前这里只报一句进度就落回老熔点闸，于是屏幕上出现的是
                    //   「局部发热物理上就下不来」那句**没量过**的断言。
                    last.Terminal = true;
                    last.TerminalWhy =
                        $"第 {jh + 1} 片第 {mh + 1} 级已经在工艺上界 {opt.MaxThickMm:0.00} mm 上"
                      + $"（实测 {hottestC:0} °C）⇒ **加厚这一级已经加不动了**。"
                      + "下一根是**加宽这一级的过流带**（半径分布）——「◇ 搜形状」。";
                    progress?.Report("  ✗ " + last.TerminalWhy);
                }
                else
                {
                    progress?.Report($"第 {jh + 1} 片第 {mh + 1} 级 {hottestC:0} °C 过热（电流密度过大）⇒ "
                                   + $"**先加厚这一级**（局部截面 ∝ 厚）：一路加到 ×{hiK:0.00}…");

                    double HotAt(double k)
                    {
                        var probe = scale.Select(a => (double[])a.Clone()).ToArray();
                        if (!Locked(jh, mh)) probe[jh][mh] *= k;    // ★ 只动过热的那一级
                        var r = EvalScale(baseCase, levelThicknessMm, probe, cancel,
                                          inner: null, coarse: true);
                        if (r is null || jh >= r.Flanges.Length) return double.PositiveInfinity;
                        var ff = r.Flanges[jh];
                        double hot = double.NegativeInfinity;
                        for (int m = 0; m < ff.LevelTMaxC.Length; m++)
                            if (!double.IsNaN(ff.LevelTMaxC[m])) hot = Math.Max(hot, ff.LevelTMaxC[m]);
                        return hot;
                    }

                    double hotHi = HotAt(hiK);
                    // ★★★ 否定分支要**在原网格上复核一次**再下结论（2026-09-03）。
                    //   粗算只用来省时间，不该拿它去**否掉一整根旋钮**：
                    //   万一粗网格把级判错了，我们会白白放弃厚度直接去改形状 ——
                    //   不会算错数，但会多花铂（违反「能用且铂最省」）。
                    //   ⚠ 只在**否定**时多花这一次解；肯定分支不加成本。
                    if (!(hotHi < hottestC - 1e-9) || hotHi > opt.OverheatRaiseFromC)
                    {
                        // ★ 只在**差一点**时才复核（粗算给的值落在阈值 1.3 倍以内）。
                        //   差得远的（如 4530 °C vs 1768 °C）粗细网格都救不回来，
                        //   多花一次整线解只是把超时买回来 —— 实测 F_改后4.txt。
                        bool nearMiss = hotHi <= opt.OverheatRaiseFromC * 1.3;
                        var probeF = nearMiss
                            ? scale.Select(a => (double[])a.Clone()).ToArray()
                            : null;
                        if (probeF is not null)
                        {
                        if (!Locked(jh, mh)) probeF[jh][mh] *= hiK;
                        var rf = EvalScale(baseCase, levelThicknessMm, probeF, cancel,
                                           inner: null, coarse: false);
                        if (rf is not null && jh < rf.Flanges.Length)
                        {
                            var ffF = rf.Flanges[jh];
                            double hotF = double.NegativeInfinity;
                            for (int m2 = 0; m2 < ffF.LevelTMaxC.Length; m2++)
                                if (!double.IsNaN(ffF.LevelTMaxC[m2]))
                                    hotF = Math.Max(hotF, ffF.LevelTMaxC[m2]);
                            progress?.Report($"     粗算说加不凉（{hotHi:0} °C）=> 原网格复核：{hotF:0} °C");
                            hotHi = hotF;
                        }
                        }
                    }
                    if (!(hotHi < hottestC - 1e-9) || hotHi > opt.OverheatRaiseFromC)
                    {
                        // ★ **实测**：加到工艺上界仍压不住 ⇒ 厚度这根走到头，换增宽
                        last.Terminal = true;
                        last.TerminalWhy =
                            $"第 {jh + 1} 片第 {mh + 1} 级加厚到工艺上界 ×{hiK:0.00}（{opt.MaxThickMm:0.00} mm）"
                          + $"，最热处 {hottestC:0} → {hotHi:0} °C，仍压不住"
                          + " ⇒ **加厚这一级救不了它**（实测，不是估的）。"
                          + "下一根是**加宽这一级的过流带**（半径分布）——「◇ 搜形状」。";
                        progress?.Report("  ✗ " + last.TerminalWhy);
                    }
                    else
                    {
                        // ★ 加厚有用 ⇒ 二分找**最小**够用的倍数（最省铂）
                        double lo = 1.0, hi = hiK;
                        // ★ 4 步够了（2026-09-03）：每步都是一次解，8 步的精度
                        //   （倍数 ±0.4 %）远细于图纸 0.01 mm 的格，白花四次解。
                        for (int it = 0; it < 3 && hi - lo > 0.08; it++)
                        {
                            double mid = 0.5 * (lo + hi);
                            if (HotAt(mid) <= opt.OverheatRaiseFromC) hi = mid; else lo = mid;
                        }
                        curK = hi;
                        if (!Locked(jh, mh)) scale[jh][mh] *= curK;
                        // 只增不减：**这一级**的下界，外层与内层以后都不许削到它以下
                        overheatFloor[jh][mh] = Math.Max(overheatFloor[jh][mh], scale[jh][mh]);
                        progress?.Report($"  ✓ 第 {mh + 1} 级加厚 ×{curK:0.00} 压住了（最小够用的那一档，"
                                       + "再薄就过热）—— 这是**这一级**的厚度下界，之后不再削到它以下");
                        continue;      // 这一轮的判定作废，按新厚度重来
                    }
                }
            }

            // ★★★★★ 熔点闸（2026-08-17 加）。
            //
            // 实测（Pt_Heater3.3dm，孔边最薄且该处还开槽）：内层「各级峰值超管根」
            // 一路 391.9 → 406.0 → 434.1 → **1799.5 K**，即该级约 2900 °C ——
            // 早已越过铂熔点 1768.2 °C，而求解器**还在继续迭代**，最后还会吐出一组厚度。
            //
            // 越过熔点之后：① 电阻率拟合只到 1500 °C，再往上是外推，数已不可信；
            //               ② 那个「解」物理上不存在，继续调比例是在优化一个熔掉的零件。
            // ⇒ 立刻停，并说清楚**哪一片哪一级、多少度**，让人能回图上去改。
            //   不停的代价不只是白算 —— 它会吐出一组看着正常的厚度。
            if (hottestC > Materials.PtMeltC)
            {
                last.Converged = false;
                last.LevelScale = scale;
                // ★ 试探次数用尽也算「量过了」：说清楚试了几次、现在多少度，
                //   而不是掉回那句没量过的断言。
                if (!last.Terminal && hottestPlate >= 0 && hottestLevel >= 0
                    && raiseTries[hottestPlate][hottestLevel] >= MaxRaiseTries)
                {
                    last.Terminal = true;
                    last.TerminalWhy =
                        $"第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级已试加厚 "
                      + $"{raiseTries[hottestPlate][hottestLevel]} 次，实测仍 {hottestC:0} °C "
                      + "⇒ **加厚这一级救不了它**。下一根是**加宽这一级的过流带**——「◇ 搜形状」。";
                }
                // ★★★ 2026-09-03：上面那一段**已经实测过加厚**了。
                //   若它已经判了「厚度这根走到头」（Terminal + TerminalWhy），
                //   这里就**不许覆盖**那句实测结论 —— 覆盖回去等于把量出来的证据
                //   换成一句没量过的断言（原文是「局部发热物理上就下不来」）。
                if (last.Terminal && last.TerminalWhy.Length > 0)
                {
                    last.Message = "★ " + last.TerminalWhy;
                    return last;
                }
                // 结构性停机：再点一次是同一句话（见 Result.Terminal）
                last.Terminal = true;
                last.TerminalWhy = $"第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级峰值 "
                                 + $"{hottestC:0} °C，超铂熔点 —— 局部发热物理上就下不来";
                last.Message =
                    $"★ 第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级峰值 {hottestC:0} °C," +
                    $"**已超铂熔点 {Materials.PtMeltC:0} °C** —— 停止迭代。" +
                    " 这不是迭代不够：该级太薄、电流被挤在窄带上，局部发热物理上就下不来。" +
                    " 常见成因：厚度梯度画反（孔边最薄），或该级恰好被开槽削掉过流截面。" +
                    " ⇒ 回 Rhino 把该级加厚 / 挪槽 / 加宽过流带，再来一轮。";
                return last;
            }
            if (last.Converged && worstOver < 15) break;

            // ── 停滞就提前收工（2026-08-23 加）
            //
            // 在此之前，无论残差还在不在动，轮数一律跑满。大几何（Pt_Heater3）上
            // 每轮要四次全解、一次分钟级 ⇒ 半小时之后吐一句「未收敛」。
            // 残差若早已进平台，那半小时里的后半段是纯粹的白算。
            hist.Add(worstOver);

            // ★★★★★ **一路都在变坏就立刻停**（用户 2026-08-25）。
            //   此前只有「进平台就收工」—— 那管的是「不动了」。
            //   但还有一种更该停的情形：**每一轮都比上一轮差**。
            //   那说明这个方向是错的，再跑下去只是把几十分钟花在往坏里走。
            //   ⚠ 门槛取「连续 3 轮」而不是 1 轮：单轮回升可能是阻尼/振荡的正常抖动，
            //     §7 记过「出现振荡 ⇒ 降阻尼重试」正是这种情况，一轮就停会误杀。
            int worsen = WorseningRun(hist);
            if (worsen >= 3)
            {
                stalledAt = round + 1;
                progress?.Report($"连续 {worsen} 轮都在变差"
                    + $"（{hist[hist.Count - 1 - worsen]:0.0} → {worstOver:0.0} K）"
                    + " —— **这个方向是错的，停下**，不再往坏里跑。");
                last.Message = (last.Message ?? "") +
                    $"　⚠ 第 {round + 1} 轮中止：连续 {worsen} 轮越调越差"
                    + $"（超管根 {hist[hist.Count - 1 - worsen]:0.0} → {worstOver:0.0} K）。"
                    + " 厚度这个旋钮在这张图上救不了法兰增量温降（旧判法）——"
                    + " ⇒ 回 Rhino 改梯度分布（孔边加厚、削薄外缘），或改环径。";
                last.Terminal = true;
                last.TerminalWhy = "连续几轮越调越差 —— 厚度这个旋钮在这张图上救不了它";
                break;
            }
            if (TrendOf(hist) == Trend.已停滞 && round < outerRounds - 1)
            {
                stalledAt = round + 1;
                progress?.Report($"残差进平台（{worstOver:0.0} K），提前收工 —— 再跑也是这个数");
                last.Terminal = true;
                last.TerminalWhy = "残差进平台 —— 再跑也是这个数";
                break;
            }
        }

        // ── 全精度复核：搜索期是粗网格 + 松耦合，最终解必须用原精度重跑一次。
        //    **报告值一律取这一次** —— 搜索期的数只用来找方向。
        progress?.Report("全精度复核最终解…");
        var lcFinal = CloneCase(baseCase);
        lcFinal.LevelThicknessMm = levelThicknessMm;
        lcFinal.LevelScale = scale;
        // ★★★★★ 搜索可以用替身，**复核必须回到原图**（2026-08-23）。
        //
        // 替身的用处只有一个：让每次评估从「起 Geom 子进程重算厚度场」变成毫秒级，
        // 好把方向找出来。但它跟图纸有百分之几的差 —— 报出去的数若也来自替身，
        // 就成了「解的是 A、**报的**也是 A」，而工程师会拿这个数去出图、去落档。
        //
        // ⇒ baseCase 指着 .3dm 时，这一步**不塞 FlangePlates**：
        //   CloneCase 已经把 FlangeFile3dm 与各级标度带过来了，
        //   于是复核走的是真图纸那条路，报出来的是图纸的数。
        bool baseIs3dm = baseCase.FlangeFile3dm is { Length: > 0 }
                         && baseCase.FlangeFile3dm.Any(x => !string.IsNullOrWhiteSpace(x));
        if (makePlateByLevel is not null && !baseIs3dm)
            lcFinal.FlangePlates = Enumerable.Range(0, nf).Select(j =>
            {
                var th = new double[scale[j].Length];
                for (int m = 0; m < th.Length; m++) th[m] = levelThicknessMm[j][m] * scale[j][m];
                return makePlateByLevel(th);
            }).ToArray();
        else if (makePlateByLevel is not null)
            progress?.Report("全精度复核回到**原图纸**几何（替身只用来搜方向）");
        try
        {
            var verify = LineRunner.Run(lcFinal, progress, cancel);
            // ★ 以全精度的实际温差**重新判定**是否达标，不沿用搜索期的判定。
            //   这里有两个不同的「收敛」，早先把后者覆盖了前者，
            //   于是输出同时出现「✓ 收敛」与「166 轮未收敛」，自相矛盾且会让人
            //   把不可行方案读成可行：
            //     · Result.Converged      = **定厚**是否达标（管根温差进没进窗口）← 判方案可行性靠它
            //     · LineResult.Converged  = **段↔法兰耦合**是否收敛（数值是否可信）
            //   两者都要满足才算数，但含义不同，不能互相覆盖。
            if (verify.Ok)
            {
                last.Line = verify;
                double worst = verify.Segments.Length == 0 ? 0
                             : verify.Segments.Max(x => Math.Max(0.0, x.FlangeDipK - opt.TargetK));
                bool searchSaidOk = last.Converged;
                last.Converged = worst < opt.TolK && verify.Converged;
                // 2026-09-14 Opus 5（复审）：这里的「管根温差」是旧判法的法兰增量温降（靶 TargetK），写明，别让人当成卡交付的冷侧。
                last.Message += $"　【全精度复核】法兰增量温降（旧判法）偏离本器的靶 {worst:0.0} K";
                if (!verify.Converged) last.Message += "；⚠ 段↔法兰耦合未收敛，数值不可引用";
                if (searchSaidOk && !last.Converged)
                    last.Message += "；⚠ 搜索精度下判为达标，全精度下**不达标** —— 以本次为准";

                // ★★★★★ 「③ 达标」**不等于**「方案可行」（2026-08-17 加）。
                //
                // 本定尺寸器只有一族旋钮（各级厚度）和一个靶（③ 增量温降）。
                // ②′ 净流入与 ②″ 圆盘峰它**够不着** —— 那两条在 `--final2` 的
                // D7 控制律里是靠**另外两个旋钮**（管孔渐变环倍率、逐片舌保温）管的，
                // 而这两个旋钮不在本器的自由度里。
                //
                // 实测（Pt_Heater3.3dm）：③ 收到 6.8 K（限 10，达标），
                // 同一个解的 ②″ = 600 K、②′ = −591 W —— 全线倒灌，方案完全不可用。
                // 若只报「③ 达标」，工程师会把它当成可行方案。
                // ⇒ 达标之后**再看一眼判据表**，不过就明说是哪几条、以及为什么不是加减厚度能解决的。
                // ★ 2026-09-14 Opus 5（复审）：这段经 autoNote 原样进界面。原文三个毛病一起改：
                //   ① 判据名印 c.Name（带代号）⇒ 走 Criteria.Plain；
                //   ② 开头恒写「③ 达标」—— 不管靶到底达没达标都这么写（bad 非空就进来）；
                //   ③ R48 B 之后同一句里会出现「③ 达标」（旧判法的增量温降）与卡交付的冷侧新判据不过，读起来自相矛盾。
                //   ⇒ 写全名、按实际写达标与否，并明说本器追的是旧判法的靶，不追热偶读数基准的两条。
                bool dipMet = worst < opt.TolK;
                var bad = verify.Checks
                    .Where(c => c.Kind != CheckKind.Reference && (!c.Ok || c.Undetermined))
                    .Select(c => $"{Criteria.Plain(c.Name)}={c.Actual:0.0}/{c.Limit:0.0}")
                    .ToArray();
                if (bad.Length > 0)
                {
                    last.Converged = false;
                    last.Message +=
                        $"；★ **本器的靶（法兰增量温降，旧判法）{(dipMet ? "达标" : "未达标")}，但整线判据不过**：" + string.Join("、", bad) +
                        "。本器只调**厚度**、只追旧判法的增量温降靶，管不到管孔净流入，也不追卡交付的「最热铂高出热偶读数」「管根低于热偶读数」" +
                        "（它们的基准是热偶读数）—— 那几条在完整求解里靠「管孔渐变环倍率」「逐片舌保温」「板厚」一起调，不是只加减厚度能补的。" +
                        " ⇒ 用「载入设计记录 → 核算整线」比对，或回图上改几何（环 / 槽位 / 舌长）。";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 复核失败就保留搜索期的结果，并在下面注明 */ }

        // 汇报最终的各级厚度
        var sb = new System.Text.StringBuilder(last.Message);
        if (!last.Converged && hist.Count > 0)
        {
            string seq = string.Join(" → ", hist.Select(x => x.ToString("0")));
            if (stalledAt > 0)
                sb.Append($"　★ 第 {stalledAt}/{outerRounds} 轮残差就进了平台（{seq} K，各级峰值超管根）"
                          + " —— 已提前收工。**加大轮数不会有用**：本器只有「各级厚度」这一族旋钮，"
                          + "它能压的已经压完了。要动的是几何 —— 回图上改环 / 槽位 / 舌长 / 盘径。");
            else if (TrendOf(hist) == Trend.还在缩)
                sb.Append($"　★ 轮数用完时残差**还在往下走**（{seq} K）—— 这次是轮数不够，不是无解。"
                          + $"把外层轮数从 {outerRounds} 调大再跑一次，很可能就到了。");
            else if (TrendOf(hist) == Trend.已停滞)
                // 恰好在最后一轮才停滞：提前收工的条件排除了最后一轮（那时已经没有可省的轮数），
                // 于是 stalledAt 是 0。少了这一支，这种情形两条走势提示一条都不打 ——
                // 「该说话时不说话」和报错数是同一类问题。
                sb.Append($"　★ 轮数用完，残差已在平台（{seq} K）—— **加大轮数不会有用**，"
                          + "本器只有「各级厚度」这一族旋钮，要动的是几何。");
        }
        if (levelLocked is not null && !last.Converged)
            sb.Append("　⚠ 有级被锁死，整片热平衡只能靠未锁的级去凑 —— " +
                      "未收敛可能是锁的限制，不一定是物理无解。可试着解锁一级再跑。");
        for (int j = 0; j < nf; j++)
        {
            sb.Append($"　片{j + 1} 各级厚度 ");
            sb.Append(string.Join("/", Enumerable.Range(0, scale[j].Length)
                        .Select(m => (levelThicknessMm[j][m] * scale[j][m]).ToString("0.000"))));
        }
        last.Message = sb.ToString();
        last.LevelScale = scale;
        return last;
    }

    /// <summary>浅拷贝算例，只换法兰几何 —— 不能直接改传入的 LineCase（界面还在用它）</summary>
    /// <summary>
    /// 浅拷一份 LineCase。**全程序只准有这一份**（2026-08-29 提为 public）：
    /// <see cref="LevelSolver"/> 也要拷，各写一份就会出现
    /// 「新加了字段，一处拷了另一处没拷」——
    /// 而那种错误**不报错**，只是默默地用了默认值。
    /// ⚠ 它是**逐字段手写**的：LineCase 新增字段时必须同步加到这里，
    ///   `LineCaseCloneTests` 盯着这件事。
    /// </summary>
    /// <summary>
    /// ★ R47 C（2026-09-13）：把 <see cref="Options.FinalMeshFineMm"/> 那组「终局细网格」口径套到算例上。
    /// 没设（≤ 0）就原样返回；设了就返回一份副本，中带／内带都换成终局口径（内带没给就跟中带走）。
    /// 单独成方法是为了让门（CliUiParityTests「细网格重解格数随 fineMm 变」）不必跑整线解就能钉住它。
    /// </summary>
    public static LineCase ApplyFinalMesh(LineCase c, Options opt)
    {
        if (opt is null || !(opt.FinalMeshFineMm > 0)) return c;
        var lc = CloneCase(c);
        // ★★★★★ R48 续（2026-09-14，Opus 5）：**这里此前不缩粗区 —— 图纸路径上同一个病还活着。**
        //
        //   原来直接写四个字段，唯独不动 MeshCoarseMm ⇒ 它留在 LineCase 默认 11 mm
        //   ⇒ 细粗比随加密从 11 变 22 变 44，正是 R48 判定为事故根因的那件事，
        //     而解析路径（Solver）09-13 就修了，**图纸路径整份留着**。
        //   同时本方法上面的注释还宣称「与加密复算同口径 —— 求根的网格与判决的网格才是同一张」，
        //   而图纸路径的**判决**那一侧（LineDesignPage 的加密复算工厂）走的是 RefineWholeMesh，
        //   粗区是按比例缩的 ⇒ 同样标称 0.5 mm，求根 22 倍、复核 5.5 倍，**注释与实现相反**。
        //   ⚠ 这一处是**生产路径**（图纸路径的「细网格重解」走 SolveByLevel → 本方法）；
        //     同一批里 LevelSolver 也有同样两处，但它生产代码零调用，另行标注。
        MeshAdapt.RefineWholeMesh(lc, opt.FinalMeshFineMm,
                                  opt.FinalMeshFineRadiusMm, opt.FinalMeshInnerRadiusMm);
        // 分区加密的老口径出口：内带与中带不同尺寸时才用得上（整档一起加密时它等于中带，这行不做事）
        if (opt.FinalMeshInnerMm > 0 && Math.Abs(opt.FinalMeshInnerMm - opt.FinalMeshFineMm) > 1e-9)
            lc.MeshInnerMm = opt.FinalMeshInnerMm;
        return lc;
    }

    public static LineCase CloneCase(LineCase c) => new()
    {
        // ⚠ SegLengthMm 现在是**逐段数组**（用户 2026-09-03）。这里必须 Clone ——
        //   与 SetpointC/HeadM 共享引用是本类原有的写法，但那两个没人改；
        //   长度会被定尺寸改吗？不会。仍复制一份，免得将来有人改了却改到了原件。
        TubeIdMm = c.TubeIdMm, WallMm = c.WallMm,
        SegLengthMm = (double[])c.SegLengthMm.Clone(),
        GradeName = c.GradeName, SetpointC = c.SetpointC, HeadM = c.HeadM,
        UseMeasuredCurrent = c.UseMeasuredCurrent, MeasuredCurrentA = c.MeasuredCurrentA,
        FlangeLayer = c.FlangeLayer, FlangePlaneY = c.FlangePlaneY,
        FlangeFile3dm = c.FlangeFile3dm, ThicknessScale = c.ThicknessScale,
        LevelScale = c.LevelScale, LevelThicknessMm = c.LevelThicknessMm,
        ThicknessStepMm = c.ThicknessStepMm,
        // R47 B（2026-09-13）：图纸路径的逐片输入也要带过去，否则定尺寸里的副本退回裸舌／默认板
        FlangeFields = c.FlangeFields, GeomForJudge = c.GeomForJudge,
        TabInsul3dmPerPlateMm = c.TabInsul3dmPerPlateMm, TabInsul3dmMm = c.TabInsul3dmMm,
        TabInsulProfile3dmPerPlate = c.TabInsulProfile3dmPerPlate,   // R48 R（2026-09-17，Opus 5）：漏抄则副本退回单值舌保温（同上一行那个坑）
        DiscInsul3dmPerPlateMm = c.DiscInsul3dmPerPlateMm,   // R48（2026-09-14，Opus 5）：图纸路径逐片圆盘保温，漏抄则副本退回整线值
        MeshFineMm = c.MeshFineMm, MeshCoarseMm = c.MeshCoarseMm,
        MeshFineRadiusMm = c.MeshFineRadiusMm,
        MeshInnerMm = c.MeshInnerMm, MeshInnerRadiusMm = c.MeshInnerRadiusMm,
        GlassInC = c.GlassInC, GlassOutMeasuredC = c.GlassOutMeasuredC,
        Base = c.Base, BaselineMassG = c.BaselineMassG, CheckRamp = c.CheckRamp,
        // ★ R48（2026-09-14，Opus 5）：工况位跟着副本走 —— 漏了它，定尺寸器里的副本悄悄变回带玻璃稳态。
        //   2026-09-14 Opus 5（R48 审查第 9 条改口）：CloneCase **不复制** BaselineRootC；本类另在跨轮处传缓存（lc.BaselineRootC = baseA，
        //   baseA = lc.BaselineRootC），那些副本都来自同一个 baseCase、工况位相同，所以不会混。以后有人把不同工况的算例接进同一次运行，那两处要带上工况。
        //   控温点来源只作记录（控温点本身随 SetpointC 已带过来）。
        EmptyTube = c.EmptyTube, EmptyTubeSetpointFrom = c.EmptyTubeSetpointFrom,
        // R48 B（2026-09-14 Opus 5）：判据限值也要带过去 —— 此前一条都没拷，副本一律退回默认值（改过限值的算例在定尺寸／加密复算里会静默换回默认）。
        HotOverTcMaxK = c.HotOverTcMaxK, ColdUnderTcMaxK = c.ColdUnderTcMaxK,
        DiscOverTempMaxK = c.DiscOverTempMaxK, RootDeltaMaxK = c.RootDeltaMaxK,
    };
}
