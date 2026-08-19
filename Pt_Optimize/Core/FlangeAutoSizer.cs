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
        //   可行：实测 dD/d板厚 = 62–107 W/mm（`--vary` 新定案点）。
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
        /// <summary>逐级定厚的结果：`[片][级]` 的厚度倍数（仅 SolveByLevel 填）</summary>
        public double[][]? LevelScale;
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
    public static Result SolveAuto(LineCase baseCase, Func<double, FlangePlate>? makePlate,
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

        for (int esc = 0; esc <= maxEscalations; esc++)
        {
            last = Solve(baseCase, makePlate, start, cur, progress, cancel);
            if (last.Converged) break;

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
    private static void Verify(LineCase baseCase, Func<double, FlangePlate>? makePlate,
                               Result res, Options opt,
                               IProgress<string>? progress, CancellationToken cancel)
    {
        if (res.ThicknessMm.Length == 0) return;
        progress?.Report("全精度复核最终解…");

        var lc = CloneCase(baseCase);            // 不套搜索期的降精度设置
        if (makePlate is not null)
            lc.FlangePlates = res.ThicknessMm.Select(makePlate).ToArray();
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

            res.Message += $"　【全精度复核】抽热偏离靶 {worst:0.0} W" +
                           $"（②′ {v.ValueOf(LineResult.Key.NetFlux):+0.00;−0.00} W／" +
                           $"③ {v.ValueOf(LineResult.Key.FlangeDip):0.00} K）";
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
    public static Result Solve(LineCase baseCase, Func<double, FlangePlate>? makePlate,
                               double[] initialThicknessMm, Options? opt = null,
                               IProgress<string>? progress = null,
                               CancellationToken cancel = default)
    {
        opt ??= new Options();
        var t = (double[])initialThicknessMm.Clone();
        var res = new Result { ThicknessMm = t };
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
                lc.FlangePlates = t.Select(makePlate).ToArray();      // 解析几何：t 就是厚度
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
                res.Message = "无法兰基线没算出来 ⇒ ③ 增量温降无从得知，定尺寸器**拒绝瞎调**。" +
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
            progress?.Report($"第 {it + 1} 轮：抽热最大偏差 {worst:0.0} W　" +
                             $"D {string.Join("/", draws.Select(v => v.ToString("+0.0;−0.0")))}　" +
                             $"③max {dips.Max():0.0}　厚度 " +
                             string.Join("/", t.Select(x => x.ToString("0.00"))));

            if (worst < opt.DrawTolW)
            {
                res.Converged = true;
                res.Message =
                    $"{it + 1} 轮收敛：各片抽热已落进窗口（靶 {opt.DrawTargetW:0.#} W，" +
                    $"实测 {draws.Min():+0.0;−0.0}…{draws.Max():+0.0;−0.0} W）。" +
                    $"③ 随之为 {dips.Max():0.00} K。\r\n" +
                    "  ②′>0 与 ③≤限 是同一个抽热的两侧（③ = 2.40·D 实测）⇒ **一个靶同时守住两条**。";
                return res;
            }

            int pinned = 0;
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
                t[j] = next;
            }

            // ★ 全部变量都顶在边界且还想继续往界外走 ⇒ 再迭代也不会动，立即停。
            //   早先没有这条：某算例四片全钉在 0.4 mm 下界，求解器仍跑满
            //   25 轮 × 自动升级 4 次 = 125 次整线耦合解（每次 15 轮耦合 × 7 个场解，
            //   合计约一万三千次场解），**一格算了一个多小时才吐出一个必然失败的结果**。
            if (pinned == t.Length)
            {
                bool tooThin = errW.Average() > 0;   // 还想加厚却顶在上界 / 还想削薄却顶在下界
                res.Message = $"{it + 1} 轮后全部厚度顶在" +
                              (tooThin ? $"上界 {opt.MaxThickMm:0.00}" : $"下界 {opt.MinThickMm:0.00}") +
                              $" mm 仍进不了抽热窗口（最大偏差 {worst:0.0} W）—— " +
                              "**该几何在此工况下无解，不是迭代不够**。\r\n" +
                              (tooThin
                               ? "  还想加厚 = 抽热不够 = 法兰太热、热在往管里灌（②′<0）⇒ 需要更大的过流断面或更少的发热。"
                               : "  还想削薄 = 抽热太多 = 把管根抽出深坑（③ 超限）⇒ 需要缩小法兰或加保温。");
                return res;
            }
        }

        res.Message = $"{opt.MaxIterations} 轮未收敛（抽热最大偏差 {res.History.LastOrDefault():0.0} W）。" +
                      "可能是某片已顶到厚度上下界，或该形状在此电流下无解。";
        return res;
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
    /// 传了它就走**解析路径**（不经 Rhino，快一个量级）；为 null 则走 .3dm + 厚度标度。
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
        int nf = levelThicknessMm.Length;
        var scale = new double[nf][];
        for (int j = 0; j < nf; j++)
        {
            scale[j] = new double[levelThicknessMm[j].Length];
            for (int m = 0; m < scale[j].Length; m++) scale[j][m] = 1.0;
        }

        Result last = new();
        for (int round = 0; round < outerRounds; round++)
        {
            cancel.ThrowIfCancellationRequested();

            // ── 外层：在**当前各级比例**下，求每片的整体倍数，使管根温差达标
            var overall = new double[nf];
            for (int j = 0; j < nf; j++) overall[j] = 1.0;
            var lcBase = CloneCase(baseCase);
            lcBase.LevelThicknessMm = levelThicknessMm;
            lcBase.LevelScale = scale;

            progress?.Report($"第 {round + 1}/{outerRounds} 轮 · 外层：调整每片整体厚度…");
            Func<double, FlangePlate>? mk = null;
            if (makePlateByLevel is not null)
            {
                // 解析路径：外层的标量 k 乘在**当前各级比例**上，构造该片几何
                var snap = scale.Select(a => (double[])a.Clone()).ToArray();
                int callIdx = 0;
                mk = k =>
                {
                    var lv = snap[Math.Min(callIdx++ % Math.Max(1, snap.Length), snap.Length - 1)];
                    var th = new double[lv.Length];
                    for (int m = 0; m < lv.Length; m++) th[m] = levelThicknessMm[0][m] * lv[m] * k;
                    return makePlateByLevel(th);
                };
            }
            last = SolveAuto(lcBase, mk, overall, opt, progress, cancel, 4, verify: false);
            if (last.Line is null) return last;

            // 把外层求出的整体倍数并进各级比例 —— **锁住的级不并**
            for (int j = 0; j < nf; j++)
            {
                double kj = j < last.ThicknessMm.Length ? last.ThicknessMm[j] : 1.0;
                for (int m = 0; m < scale[j].Length; m++)
                    if (!Locked(j, m)) scale[j][m] *= kj;
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
                                             opt.MinThickMm / Math.Max(1e-6, levelThicknessMm[j][m]),
                                             opt.MaxThickMm / Math.Max(1e-6, levelThicknessMm[j][m]));
                }
            }
            progress?.Report($"第 {round + 1} 轮 · 内层：各级峰值最高超管根 {worstOver:0.0} K" +
                             (hottestPlate >= 0 ? $"（第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级 {hottestC:0} °C）" : ""));

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
                last.Message =
                    $"★ 第 {hottestPlate + 1} 片第 {hottestLevel + 1} 级峰值 {hottestC:0} °C，" +
                    $"**已超铂熔点 {Materials.PtMeltC:0} °C** —— 停止迭代。" +
                    " 这不是迭代不够：该级太薄、电流被挤在窄带上，局部发热物理上就下不来。" +
                    " 常见成因：厚度梯度画反（孔边最薄），或该级恰好被开槽削掉过流截面。" +
                    " ⇒ 回 Rhino 把该级加厚 / 挪槽 / 加宽过流带，再来一轮。";
                return last;
            }
            if (last.Converged && worstOver < 15) break;
        }

        // ── 全精度复核：搜索期是粗网格 + 松耦合，最终解必须用原精度重跑一次。
        //    **报告值一律取这一次** —— 搜索期的数只用来找方向。
        progress?.Report("全精度复核最终解…");
        var lcFinal = CloneCase(baseCase);
        lcFinal.LevelThicknessMm = levelThicknessMm;
        lcFinal.LevelScale = scale;
        if (makePlateByLevel is not null)
            lcFinal.FlangePlates = Enumerable.Range(0, nf).Select(j =>
            {
                var th = new double[scale[j].Length];
                for (int m = 0; m < th.Length; m++) th[m] = levelThicknessMm[j][m] * scale[j][m];
                return makePlateByLevel(th);
            }).ToArray();
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
                last.Message += $"　【全精度复核】管根温差偏离目标 {worst:0.0} K";
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
                var bad = verify.Checks
                    .Where(c => c.Kind != CheckKind.Reference && (!c.Ok || c.Undetermined))
                    .Select(c => $"{c.Name}={c.Actual:0.0}/{c.Limit:0.0}")
                    .ToArray();
                if (bad.Length > 0)
                {
                    last.Converged = false;
                    last.Message +=
                        "；★ **③ 达标但整线判据不过**：" + string.Join("、", bad) +
                        "。本器只调**厚度**，管不到 ②′/②″ —— 那两条在完整控制律里靠" +
                        "「管孔渐变环倍率」与「逐片舌保温」调，不是加减厚度能补的。" +
                        " ⇒ 用「▶ 复现定案」比对，或回图上改几何（环 / 槽位 / 舌长）。";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 复核失败就保留搜索期的结果，并在下面注明 */ }

        // 汇报最终的各级厚度
        var sb = new System.Text.StringBuilder(last.Message);
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
    private static LineCase CloneCase(LineCase c) => new()
    {
        TubeIdMm = c.TubeIdMm, WallMm = c.WallMm, SegLengthMm = c.SegLengthMm,
        GradeName = c.GradeName, SetpointC = c.SetpointC, HeadM = c.HeadM,
        UseMeasuredCurrent = c.UseMeasuredCurrent, MeasuredCurrentA = c.MeasuredCurrentA,
        FlangeLayer = c.FlangeLayer, FlangePlaneY = c.FlangePlaneY,
        FlangeFile3dm = c.FlangeFile3dm, ThicknessScale = c.ThicknessScale,
        LevelScale = c.LevelScale, LevelThicknessMm = c.LevelThicknessMm,
        ThicknessStepMm = c.ThicknessStepMm,
        MeshFineMm = c.MeshFineMm, MeshCoarseMm = c.MeshCoarseMm,
        MeshFineRadiusMm = c.MeshFineRadiusMm,
        GlassInC = c.GlassInC, GlassOutMeasuredC = c.GlassOutMeasuredC,
        Base = c.Base, BaselineMassG = c.BaselineMassG, CheckRamp = c.CheckRamp
    };
}
