using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **定尺寸器 D8**（2026-08-17）—— 给定形状，解出板厚／舌保温／环倍率。
///
/// ════════ 为什么要重写（D7 的病，实测确诊，不是推测）════════
///
/// D7 的分派是：**舌厚 → ②′（靶 2 W）／环倍率 → ②″／舌保温 → ②″ 的抗饱和接力**。
/// ⇒ **③ 没有任何旋钮。** 它在舌长 90 的老几何上从没暴露，只因为那里 ③ 天然就是 5.5。
///   换到装配可行的形状（盘 R45／舌 165）后，13 轮里 ②″ 从 +8.8 压到 +6.1，
///   而 ③ 从 149.8 只挪到 135.4（限 10）—— **控制律覆盖不到的判据不会自己变好**。
///
/// ════════ 实测（`--window`，盘 R30／舌 166×60，板厚完全不动）════════
///
///   保温×0.20：②′ +2.4/+42.5/+40.9/+57.1 W　③ +100.8/+102.8/+138.1 K　合计 4590 g
///   保温×1.00：②′ −165.8/−13.8/−1.9/+0.6 W　③ −33.8/−5.8/+1.5   K　合计 4590 g
///
/// 三件事同时被这两行钉死：
///
///  1. **②′ 与 ③ 是同一个抽热 D 的两侧**，且关系是闭式的：
///       ③(段) = γ · max(D_左, D_右)，实测 **γ = 2.40 K/W**（六行全部吻合 5 % 以内）。
///     ⇒ ②′ > 0 与 ③ ≤ 10 合起来就是一句话：**0 < D ≤ 10/γ ≈ 4.17 W**。
///       可行域是 D 的一个**区间**，不是两条打架的约束。
///  2. **舌保温是 D 的强旋钮**：同样的板厚，把 D 从 −166 W 扫到 +2.4 W。
///  3. **它一克铂都不花**（两行合计都是 4590 g）。
///
/// ════════ D8 分派 ════════
///
///   舌保温 ins_j  → **抽热 D_j**（靶 2 W，落在窗口偏安全侧 ⇒ ③ ≈ 4.8 K）　★ 免费旋钮，主力
///   环倍率 μ_j    → **②″**（快、局部作用在孔周尖峰上；D3 已验证，保留）
///   板厚   t_j    → **接力 + 省铂**：
///                    · 保温顶到下界仍在灌热 ⇒ 加厚（降发热）
///                    · 保温顶到上界仍抽太多 ⇒ 削薄
///                    · 环顶到上限仍压不住 ②″ ⇒ 加厚
///                    · 以上都不缺 ⇒ **往薄漂**（铂重 ∝ t，这是唯一花钱的旋钮）
///
/// 与 D7 相比就是把「谁管谁」换了个位：**让免费的旋钮去管硬判据，让花钱的旋钮去省钱。**
/// D5 当年提出过这个方向（「三角化分派」），但它把正在干活的环倍率**拿掉了**，
/// 于是 ②″ 失控、被 D7 回退。D8 = D5 的分派 + D3 的环，两个都留着。
///
/// ⚠ 本类**不定义任何判据**。判定一律读 <see cref="LineResult.AllOk"/>／<c>Failed</c>，
///   靶值只用来**指方向**。判据只有一个来源（<c>LineRunner.Judge</c>）——
///   「优化器在调 A、判据在判 B」这个错在本项目上连犯过四次。
/// </summary>
public sealed class SizerOptions
{
    /// <summary>抽热靶 W。窗口是 (0, DipLimitK/γ]，取偏安全的低侧。</summary>
    public double DrawTargetW = 2.0;
    /// <summary>抽热死区 W。小于它就不动旋钮 —— 否则在数值噪声上空转。</summary>
    public double DrawDeadW = 0.35;
    /// <summary>③/D 的初值 K/W（在线实测覆盖它）。2.40 = `--window` 六行实测。</summary>
    public double GammaKPerW = 2.40;
    /// <summary>②″ 靶 K。限值 5（现场控温精度），留 2 K 裕度。</summary>
    /// <summary>
    /// ②″ 的**整定裕度** K：靶 = 判据限值 − 本裕度，留出控制余量。
    ///
    /// ★★ 2026-08-28 收口：此处**原来存的是两份限值副本**
    ///   （<c>DipLimitK = 10.0</c> 与 <c>DiscOverTargetK = 3.0</c>），
    ///   而注释声称「与 LineCase.RootDeltaMaxK **同源**」—— grep 证实
    ///   本类从未读过 RootDeltaMaxK，「同源」是假的（注释描述不存在的机制）。
    ///   ③ 的限值更是承重：<c>drawMax = 限值 / γ</c> 是整个 D8 分派的硬上界，
    ///   判据一收紧而这里不动，就是本类注释自己点名「连犯过四次」的
    ///   **「优化器在调 A、判据在判 B」**。
    ///
    /// ⇒ 现在限值**只从 LineCase 读**（判据的唯一来源），本类只保留「裕度」这一个自己的量。
    ///   2.0 = 原先 5.0(限值) − 3.0(靶)，行为不变，但从此只有一处限值。
    /// </summary>
    public double DiscOverMarginK = 2.0;
    public double DiscOverDeadK = 0.30;

    /// <summary>环倍率上下界。**常量版供界面控件用** —— 界面与优化器的界必须是同一个。</summary>
    public const double RingLoConst = 1.0, RingHiConst = 2.5;
    public double RingLo = RingLoConst, RingHi = RingHiConst;
    /// <summary>舌保温上下界。**常量版供界面控件用**（用户：管外纤维无空间限制）。</summary>
    public const double InsLoMmConst = 0.3, InsHiMmConst = 80.0;
    public double InsLoMm = InsLoMmConst, InsHiMm = InsHiMmConst;
    public double ThickHiMm = 6.0;
    /// <summary>省铂漂移的**最小**步长 mm/轮（步长本身由保温余量定，见 Sizer 里的阀位控制）。</summary>
    public double ThinStepMm = 0.03;
    /// <summary>省铂步长 = 保温余量 × 本增益，再夹在 [ThinStepMm, ThickStepMm]。</summary>
    public double ThinGain = 0.25;
    /// <summary>接力加厚/削薄的步长上限 mm/轮。</summary>
    public double ThickStepMm = 0.15;
    public int MaxRounds = 40;

    /// <summary>
    /// 跑满上限时，最好点已经「多少轮没再改善」就算**稳定**而非被截断。
    /// 20 轮是实测定的：R30／壁0.8 的极限环周期约 3 轮，20 轮足够穿过它好几遍。
    /// </summary>
    public const int StaleRounds = 20;
    /// <summary>false = 只求可行，不做省铂漂移（诊断用：漂移会让轨迹永远在边界上摆）。</summary>
    public bool SaveMetal = true;

    // ════════════════════════════════════════════════════════════════════
    // ★★★★★ 可造性与裕度（2026-08-17 补，起因见下）
    //
    // 病症：D8 第一版把 0.8 档解到 3512 g，写进 DesignSpec 前按图纸精度四舍五入
    // （板厚 0.8697→0.87、舌保温 0.35→0.4），再复核一次 ——
    //     ②′ = **−0.6 W**（须 > 0）、③ = **10.2 K**（限 10）⇒ **两条都不过**。
    //
    // 病因不是求解错了，是**省铂漂移会一直推到某条判据咬住为止**，
    // 于是解天然停在可行域**边界上**，裕度 ≈ 0 —— 那么「第三位小数」就决定了过不过。
    // 而图纸上不可能写 0.8697 mm 的板厚、0.35 mm 的纤维。
    //
    // ⇒ 两条一起改：
    //   ① **判据判的必须是能造出来的那个数**：每轮先把设计量化到图纸精度再解。
    //      不量化就等于「算的是 A、造的是 B」——§1.8 那一族的又一种形态。
    //   ② **收货要留裕度**：只有裕度够的点才记为「最轻的全过点」。
    //      贴着限值宣布通过，是本项目最常犯的错（判据表自己每次都在提醒这句话）。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>板厚量化步 mm（图纸精度）。</summary>
    public double QuantThickMm = 0.01;
    /// <summary>舌保温量化步 mm —— 纤维厚度现场根本做不到比这更细。</summary>
    public double QuantInsMm = 0.1;
    /// <summary>环倍率量化步。</summary>
    public double QuantRing = 0.01;

    /// <summary>收货门槛：最小抽热 W。低于它虽然 ②′ 仍 > 0，但没有裕度可言。</summary>
    public double AcceptDrawMinW = 0.8;
    /// <summary>收货门槛：③ 的上限 K（比判据限值留一档）。</summary>
    public double AcceptDipK = 8.5;
}

public sealed class SizerResult
{
    /// <summary>**最轻的那个全过点**的设计（板厚/保温/环倍率已写回）。不可行时是违反度最小的点。</summary>
    public DesignSpec Design = null!;
    /// <summary>该点的完整复核结果（含升温 ①）。</summary>
    public LineResult? Best;
    public bool Feasible;
    public double MassG = double.NaN;
    public string Message = "";
    public readonly List<string> Trace = new();
    /// <summary>实测的 ③/D 比例 K/W —— 它是**管子**的性质，与法兰形状无关，可跨形状复用。</summary>
    public double GammaKPerW = double.NaN;

    /// <summary>实际跑了几轮。</summary>
    public int RoundsUsed;
    /// <summary>**跑满上限就停了** —— 结果可能只是被截断，不是收敛。</summary>
    public bool HitRoundCap;
    /// <summary>停因，一句话。<see cref="Sizer.StopReason"/> 是它唯一的来源。</summary>
    public string StopWhy = "";

    /// <summary>
    /// 带符号格式化，且**先把负零掐掉**。
    ///
    /// 起因（2026-08-17 实际打出来的）：`(-0.0).ToString("+0.0;−0.0")` 得到 **"-+0.0"** ——
    /// .NET 对负零仍会自己补一个 '-'，然后又套上第一段里那个字面的 '+'。
    /// IEEE 里 `-0.0 == 0.0` 为真，所以一句 `v == 0 ? 0.0 : v` 就能归一。
    /// 属于本项目已有专门测试的「格式串」family（UiWiring 第 8 项），只是那条规则查的是
    /// `{x:0.1}`，查不到这一种。
    ///
    /// ★ R48 G2 复审二（2026-09-15 Opus 5）补：上面那句只掐得掉**恰好**等于负零的数。负数按格式**取整成零**时同病 ——
    ///   net8 实测（本机临时控制台程序）：(-0.01)／(-0.04)／(-1e-9).ToString("+0.0;−0.0") 都得到 "-+0.0"（-0.05 以下正常印 "−0.1"）。
    ///   .NET 的规则是负数取整成零就换用第一段，却仍在前面补文化的负号。于是再比一次：结果恰好是「负号 + 零按本格式印出来的样子」⇒ 印零。
    ///   正常的负数（第二段自己带 '−' 或 '-'，取整后不为零）不会等于那个串，逐字不变。
    /// </summary>
    public static string Signed(double v, string fmt = "+0.0;−0.0")
    {
        double z = v == 0 ? 0.0 : v;
        string s = z.ToString(fmt);
        if (z < 0)
        {
            string zero = 0.0.ToString(fmt);
            if (s == System.Globalization.NumberFormatInfo.CurrentInfo.NegativeSign + zero) return zero;
        }
        return s;
    }
}

public static class Sizer
{
    /// <summary>
    /// 给定形状（<paramref name="seed"/> 的盘径/舌长/舌宽/管壁/压接），解出板厚、舌保温、环倍率。
    ///
    /// <paramref name="seed"/> 的板厚/保温/环倍率是**起点**。
    ///
    /// ★★ 2026-08-25 更正：此处原写「起点只影响轮数，**不影响解**：每个旋钮对自己的靶
    ///   都是单调的」。**那句话是错的，已被实测推翻。** 同一形状（盘R30）只换板厚起点：
    ///     0.8 档 3547 g vs 3664 g（+3.3 %）　0.6 档 2650 g vs 2971 g（+12.1 %）
    ///   两条理由：
    ///    · 单旋钮对自己的靶单调 **≠** 耦合系统有唯一不动点；
    ///    · 本方法交回去的是 `bestFeas ?? bestAny` —— **已访问点集上的 argmin**，
    ///      不是可行域上的 argmin ⇒ 按定义就是路径相关的。
    ///   ⇒ 起点是**会影响答案**的输入，必须申报（见 Core/ShapeSeed.cs）。
    ///   ⚠ 影响多大要分情况：形状被工艺下界主导时（如盘Ø120，抗屈曲下界 4.71 mm）
    ///     板厚被顶到下界，终态与起点无关；有裕度可省时才路径相关。
    /// </summary>
    /// <summary>
    /// 主循环为什么停 —— **纯函数**，因为它是一条判断，而 Solve 本身是分钟级的。
    ///
    /// ★ 为什么要有它（2026-08-25）：此前「这个形状无解」与「轮数不够」在输出上
    ///   长得一模一样，工程师分不出来。界面粗筛只跑 16 轮（LineDesignPage.SearchScreenRounds），
    ///   比 CLI 默认的 40 更容易被截断 —— 偏偏那是最常走的那条路。
    ///
    /// ⚠ 第三种情形（循环结束却没有停因）按理不该出现，但**不静默**：
    ///   宁可打一句「说不出为什么停」，也不要假装它收敛了。
    /// </summary>
    public static (bool HitCap, string Why) StopReason(
        int roundsUsed, int maxRounds, string earlyWhy, int bestRound = -1)
    {
        if (!string.IsNullOrEmpty(earlyWhy)) return (false, earlyWhy);
        if (roundsUsed >= maxRounds)
        {
            // ★ 跑满上限有**两种**，说成一种就是误导（2026-08-25 实测撞到）：
            //   R30／壁0.8 跑 200 轮的结果与 40 轮**逐位相同**（3547 g），
            //   轨迹是个极限环（3547 → 3553 → 3537越界 → 回来）——
            //   它早就稳了，而停因照旧说「可能只是被截断」。**那句话把人指向加轮数，
            //   而真正卡住它的是判据边界（②′ 第 3 片），加多少轮都没用。**
            int idle = bestRound >= 0 ? roundsUsed - bestRound : -1;
            if (idle >= SizerOptions.StaleRounds)
                return (true,
                    "跑满上限 " + maxRounds + " 轮，但**最好点出现在第 " + bestRound + " 轮**，"
                  + "之后 " + idle + " 轮再没改善 ⇒ **已经稳定，不是被截断**"
                  + "（加轮数没用；要更好得松判据或改形状）");
            return (true,
                "**跑满上限 " + maxRounds + " 轮就停了**，而且**最好点就在第 " + bestRound + " 轮**（仍在改善）"
              + " ⇒ 结果**可能只是被截断**，--rounds 可加大");
        }
        return (false, "说不出为什么停（第 " + roundsUsed + "/" + maxRounds + " 轮）—— 这不该发生，请报一声");
    }


    public static SizerResult Solve(DesignSpec seed, DesignInputs baseIn, SizerOptions opt,
                                    IProgress<string>? progress = null,
                                    CancellationToken cancel = default)
    {
        var res = new SizerResult();
        var d = seed.Clone();
        // ★ 失效声明**绝不能被继承**：种子多半是从某个已失效的设计记录克隆来的，
        //   而本次是**重新解出来的**设计，旧档为什么失效与它无关。
        //   带着别人的失效声明跑，自检门就会拿错的「声明之内」去放行真正的失败。
        d.Invalid = ""; d.InvalidChecks = Array.Empty<string>();
        int np = d.TabThickMm.Length;

        // ★ 板厚下界只有一个来源：`Plate()` 会静默顶到焊接屈曲下界，
        //   定尺寸器若用别的下界，就会「以为自己在 0.30 上搜、实际几何是 0.44」。
        double tLo = d.DiscFloorMm(baseIn);

        // 每片旋钮的割线状态
        var prevIns = new double[np]; var prevD = new double[np]; var slopeIns = new double[np];
        // ★ 板厚→抽热这条通路的斜率也必须**在线量**（2026-08-17）。
        //   第一版按 `--window` 的均值硬编码 30 W/mm，实测这里是 **62（共用片）到 107（端片）**
        //   —— 低估 2–3.5 倍 ⇒ 每步过冲 ⇒ 板厚在 ±0.15 mm 之间来回跳，十几轮不收敛。
        //   本项目对硬编码斜率已经栽过两次（1300 K/mm 是别的构型的）。
        var prevThk = new double[np]; var prevDT = new double[np]; var slopeThk = new double[np];
        for (int j = 0; j < np; j++)
        {
            prevIns[j] = double.NaN; prevD[j] = 0; slopeIns[j] = -8.0;
            prevThk[j] = double.NaN; prevDT[j] = 0; slopeThk[j] = 60.0;
        }
        var lastIns = new double[np]; var lastThk = new double[np];
        for (int j = 0; j < np; j++) { lastIns[j] = double.NaN; lastThk[j] = double.NaN; }

        double gamma = opt.GammaKPerW;
        double bestMass = double.MaxValue, bestBad = double.MaxValue;
        DesignSpec? bestFeas = null, bestAny = null;
        double[][] baseCache = Array.Empty<double[]>();
        // 相邻两轮的设计只差 0.1 mm 板厚 / 几 mm 保温 ⇒ 上一轮的不动点离这一轮很近。
        // 冷启动每轮都要从「抽热 = 0」爬回来，g≈0.96 下动辄上百轮（见 LineCase.WarmStart）。
        double[][] warm = Array.Empty<double[]>();

        void Log(string s) { res.Trace.Add(s); progress?.Report(s); }

        Log($"D8 定尺寸　形状 盘Ø{2 * d.DiscRadiusMm:0}／舌 {d.TabLengthMm:0}×{2 * d.TabHalfWidthMm:0}" +
            $"／压接 {d.ClampLengthMm:0}　自由段 {d.FreeTabMm:0.0} mm　管壁 {d.WallMm:0.0}");
        // ★ 限值只有一个来源：判据那一份（LineCase）。本类只加自己的**裕度**。
        //   限值不随设计变，循环前照种子造一次算例取出来即可。
        var lcLim = d.BuildCase(baseIn, checkRamp: false);
        double dipLimK = lcLim.RootDeltaMaxK;                                   // ③ 的限值
        double discTgtK = Math.Max(0.0, lcLim.DiscOverTempMaxK - opt.DiscOverMarginK);  // ②″ 靶 = 限值 − 裕度
        // R48 B（2026-09-14 Opus 5）：本类（旧 D8，只剩命令行 --shape 在用）的控制律是按 ②″／③ 两条写的，而这两条已降为参考量（旧判法，代号不变）⇒
        //   本类仍追它们；交付判定（AllOk）已换成热偶读数基准的 ⑦／⑧，两者可能对不上 —— 以复核那次的 AllOk 为准。界面走 Solver，不走本类。
        Log($"分派：**舌保温 → 抽热 D（靶 {opt.DrawTargetW:0.0} W）**／**环倍率 → ②″（靶 {discTgtK:0.0} K ＝ 限值 {lcLim.DiscOverTempMaxK:0.0} − 裕度 {opt.DiscOverMarginK:0.0}）**／" +
            $"**板厚 → 接力+省铂**（下界 {tLo:0.00} mm = max(焊接屈曲, 烧穿 {baseIn.WeldMinThicknessMm:0.0})）");
        Log($"{"轮",4}{"板厚 mm",22}{"舌保温 mm",24}{"环倍率",22}{"抽热D W",26}{"③max",8}{"②″max",8}{"合计g",8}{"违反度",9}");

        // 量化到图纸精度：**先量化再解**，于是判据判的就是能造出来的那个数
        static double Q(double v, double step) => step <= 0 ? v : Math.Round(v / step) * step;
        void Quantize()
        {
            for (int j = 0; j < np; j++)
            {
                d.TabThickMm[j] = Q(d.TabThickMm[j], opt.QuantThickMm);
                d.TabInsulMm[j] = Math.Max(opt.InsLoMm, Q(d.TabInsulMm[j], opt.QuantInsMm));
                d.RingMul[j] = Q(d.RingMul[j], opt.QuantRing);
            }
        }

        // ★ 逐轮记 bad（越小越好）—— 报「这一轮比上一轮好还是差」，
        //   并在**连续变差**时停下（用户 2026-08-25）。
        var badHist = new List<double>();
        int roundsUsed = 0, bestRound = 0;
        string earlyWhy = "";
        for (int round = 0; round < opt.MaxRounds; round++)
        {
            roundsUsed = round + 1;
            cancel.ThrowIfCancellationRequested();
            Quantize();
            var lc = d.BuildCase(baseIn, checkRamp: false);
            lc.BaselineRootC = baseCache;
            lc.WarmStart = warm;
            LineResult r;
            try { r = LineRunner.Run(lc, null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { res.Message = "求解异常：" + ex.Message; Log($"{round,4}  异常 {ex.Message}"); break; }
            if (!r.Ok) { res.Message = r.Message; Log($"{round,4}  ✗ {r.Message}"); break; }
            baseCache = lc.BaselineRootC;
            // ⚠ 只在**收敛**时接过热启动状态。没收敛的 x 不是不动点，
            //   拿它当下一轮的起点会把一次失败的迭代当经验往下传。
            if (r.Converged) warm = lc.WarmStart;

            var draws = r.Flanges.Select(f => f.QFromTubeW).ToArray();
            var dips = r.Segments.Select(s => s.FlangeDipK).ToArray();
            double dipMax = dips.Any(v => !double.IsNaN(v)) ? dips.Where(v => !double.IsNaN(v)).Max() : double.NaN;
            double mass = r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);
            // 违反度由 Judge 给，不自行定义
            double bad = r.Checks
                .Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target && !ck.Ok && !ck.Undetermined)
                .Sum(ck => Math.Abs(ck.Actual - ck.Limit));

            // ── ③/D 比例在线实测。它是**管子**的性质（③ = D/√(kAβ)），与法兰形状无关。
            //   只有 |D| 够大时这个比值才有信噪比 —— 小 D 时分母噪声会把 γ 打飞。
            var gs = new List<double>();
            for (int i = 0; i < r.Segments.Length; i++)
            {
                double dL = draws[i], dR = draws[i + 1];
                double dom = Math.Abs(dL) > Math.Abs(dR) ? dL : dR;
                if (!double.IsNaN(dips[i]) && Math.Abs(dom) > 1.0) gs.Add(dips[i] / dom);
            }
            if (gs.Count > 0)
            {
                gs.Sort();
                double gm = gs[gs.Count / 2];
                if (gm > 0.2 && gm < 40) gamma = 0.5 * gamma + 0.5 * gm;
            }
            double drawMax = dipLimK / Math.Max(0.2, gamma);

            Log($"{round,4}{string.Join("/", d.TabThickMm.Select(v => v.ToString("0.00"))),22}" +
                $"{string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0"))),24}" +
                $"{string.Join("/", d.RingMul.Select(v => v.ToString("0.00"))),22}" +
                $"{string.Join("/", draws.Select(v => SizerResult.Signed(v))),26}" +
                $"{SizerResult.Signed(dipMax),8}" +
                $"{SizerResult.Signed(r.ValueOf(LineResult.Key.DiscTemp), "+0.00;−0.00"),8}" +
                $"{mass,8:0}{bad,9:0.0}" + (badHist.Count == 0 ? "" : Math.Abs(bad - badHist[badHist.Count - 1]) <= 0.05 * Math.Max(1e-9, badHist[badHist.Count - 1]) ? "  ≈" : bad < badHist[badHist.Count - 1] ? "  ↓好" : "  ↑差"));

            // ★ 收货要留裕度：全过**且**抽热与 ③ 都没贴着限值。
            //   只判 AllOk 会收下裕度 ≈ 0 的点 —— 那种点四舍五入到图纸精度就不过了
            //   （实测：3512 g 那个解，板厚 0.8697→0.87、保温 0.35→0.4 之后
            //    ②′ 变 −0.6 W、③ 变 10.2 K，两条同时翻）。
            double drawMin = draws.Min();
            bool roomy = r.AllOk && drawMin >= opt.AcceptDrawMinW
                         && !double.IsNaN(dipMax) && dipMax <= opt.AcceptDipK;
            // ★ 记下**最好点出现在第几轮** —— 用来分辨「跑满上限时还在下降」
            //   与「早就稳了、只是在极限环里空转」。两者输出上此前长得一样（2026-08-25）。
            if (roomy && mass < bestMass) { bestMass = mass; bestFeas = d.Clone(); bestRound = roundsUsed; }
            if (bad < bestBad) { bestBad = bad; bestAny = d.Clone(); if (bestFeas is null) bestRound = roundsUsed; }

            // ★★★★★ **连续变差就停**（用户 2026-08-25）。
            //   此前只有「所有旋钮都到位或都顶死 ⇒ 停」—— 那管的是「动不了了」。
            //   一路越调越差是另一回事：方向错了，再跑只是把时间花在往坏里走。
            //   ⚠ 门槛取连续 3 轮：单轮回升可能是控制律的正常抖动（反饱和接力、
            //     阻尼调整都会让某一轮短暂变差），一轮就停会误杀。
            //   ⚠ 停的时候**保留 bestAny/bestFeas** —— 中止不等于丢掉已经找到的最好点。
            badHist.Add(bad);
            int worseRun = FlangeAutoSizer.WorseningRun(badHist);   // 唯一一份实现
            if (worseRun >= 3)
            {
                Log($"   ⇒ 连续 {worseRun} 轮越调越差"
                  + $"（{badHist[badHist.Count - 1 - worseRun]:0.0} → {bad:0.0}）"
                  + " —— **方向错了，停**。已找到的最好点保留在下面的复核里。");
                res.Message = $"连续 {worseRun} 轮越调越差 ⇒ 提前停（第 {round + 1} 轮）。";
                earlyWhy = $"连续 {worseRun} 轮越调越差，方向错了";
                break;
            }

            // ════════ 控制律 ════════
            bool moved = false;
            for (int j = 0; j < np; j++)
            {
                double D = draws[j];
                double e2 = r.Flanges[j].TDiscMaxC - r.Flanges[j].TRootC;

                // ── 旋钮 ①：**舌保温 → 抽热 D**（免费，主力）
                //   方向（实测，不是推的）：保温厚 ⇒ 法兰散热少 ⇒ 法兰热 ⇒ 从管里抽得少
                //   ⇒ **dD/dins < 0**。D 不够就**减**保温。
                double eD = opt.DrawTargetW - D;          // >0 ⇒ 抽热不够 ⇒ 要减保温

                // ── 在线量 d抽热/d板厚：只在**上一轮只动了板厚**时才归因，否则两条通路混在一起
                if (!double.IsNaN(lastThk[j]) && !double.IsNaN(lastIns[j])
                    && Math.Abs(d.TabThickMm[j] - lastThk[j]) > 1e-9
                    && Math.Abs(d.TabInsulMm[j] - lastIns[j]) < 1e-9
                    && !double.IsNaN(prevThk[j]))
                {
                    double smT = (D - prevDT[j]) / (d.TabThickMm[j] - prevThk[j]);
                    if (smT > 1 && smT < 2000) slopeThk[j] = 0.5 * slopeThk[j] + 0.5 * smT;
                }
                prevThk[j] = d.TabThickMm[j]; prevDT[j] = D;
                lastThk[j] = d.TabThickMm[j]; lastIns[j] = d.TabInsulMm[j];

                double sIns = slopeIns[j];
                if (!double.IsNaN(prevIns[j]) && Math.Abs(d.TabInsulMm[j] - prevIns[j]) > 1e-6)
                {
                    double sm = (D - prevD[j]) / (d.TabInsulMm[j] - prevIns[j]);
                    // 只收**符号对**且量级合理的实测斜率；否则保留上一次的估计
                    if (sm < -0.02 && sm > -400) sIns = 0.5 * sIns + 0.5 * sm;
                }
                slopeIns[j] = sIns;

                // ★★★ **旋钮分辨率**（2026-08-17）：保温做不出比 0.1 mm 更细的厚度，
                //   而在下界附近 |dD/d保温| 实测可达 ~50 W/mm ⇒ **一格就是 5 W**，
                //   而整个抽热窗口只有 0–4.2 W。**一格跨过整个窗口的旋钮不是旋钮。**
                //   ⇒ 这种时候必须换手：让**板厚**（0.01 mm 一格 ≈ 0.3 W）去做细调。
                //   不判这一条的后果是它在 0.3/0.4 mm 之间来回跳，永远落不进窗口。
                //   判据就是「一格走的比误差还远」——那一步必然过冲，只会左右横跳。
                //   （第一版写成 > 2·|eD|，实测正好卡在边界上：保温在 0.6/0.7 之间跳，
                //     抽热 +4.5 ↔ −0.4，跳了十几轮没动过。放宽的那个 2 是没有依据的。）
                bool insTooCoarse = Math.Abs(sIns) * opt.QuantInsMm > Math.Abs(eD);
                if (Math.Abs(eD) > opt.DrawDeadW && !insTooCoarse)
                {
                    prevIns[j] = d.TabInsulMm[j]; prevD[j] = D;
                    // 限幅按**相对量**：保温的量程跨 0.3–80 mm，绝对限幅在两端都不合适
                    double lim = Math.Max(0.6, 0.5 * d.TabInsulMm[j]);
                    double dIns = Math.Clamp(eD / sIns, -lim, lim);
                    double ni = Math.Clamp(d.TabInsulMm[j] + dIns, opt.InsLoMm, opt.InsHiMm);
                    if (Math.Abs(ni - d.TabInsulMm[j]) > 1e-9)
                    { d.TabInsulMm[j] = ni; moved = true; }
                }

                // ── 旋钮 ②：**环倍率 → ②″**（快、局部；`--ring` 实测 d②″/dμ ≈ −1.4 K/单位）
                bool ringSat = false;
                if (!double.IsNaN(e2))
                {
                    double ee = e2 - discTgtK;    // >0 ⇒ 盘太热
                    if (Math.Abs(ee) > opt.DiscOverDeadK)
                    {
                        // ⚠ 削环要比加环慢：加环解除判据违反，削环只是省铂，代价不对称
                        double dMul = Math.Clamp(ee / 1.4, -0.02, 0.10);
                        double nm = Math.Clamp(d.RingMul[j] + dMul, opt.RingLo, opt.RingHi);
                        if (Math.Abs(nm - d.RingMul[j]) > 1e-9) { d.RingMul[j] = nm; moved = true; }
                    }
                    ringSat = d.RingMul[j] >= opt.RingHi - 1e-9;
                }

                // ── 旋钮 ③：**板厚 → 接力 + 省铂**
                //   它是唯一花铂的旋钮 ⇒ 只在别的旋钮**用尽**时才动它去救判据，
                //   其余时候一律往薄漂。
                //
                // ★★★ 省铂那一路的**误差信号是「保温离下界还有多远」**，不是别的。
                //
                // 推理（一步就到，且每一步都有实测支撑）：
                //   · D 随板厚**增**、随保温**减**（`--window` 双向实测）；
                //   · 要满足 D ≥ 0 所需的板厚，在保温**最薄**时最小
                //     （法兰越冷 ⇒ 同样板厚抽得越多 ⇒ 允许的板厚越薄）；
                //   ⇒ **铂重最小的点，保温一定压在下界上。**
                //   ⇒ 于是「保温还没到下界」本身就是「板还可以再薄」的度量。
                //
                // 这是过程控制里的**阀位控制**：内环（保温）保住 D，
                // 外环（板厚）把阀位推向极限。两环不共线 —— 内环管**值**，外环管**阀位**。
                //   （原来写的是固定 0.06 mm/轮 的盲漂：实测 7 轮只走了 50 g，
                //     而首轮保温余量就有 5 mm，信息明明在手里没用。）
                bool insAtLo = d.TabInsulMm[j] <= opt.InsLoMm + 1e-9;
                bool insAtHi = d.TabInsulMm[j] >= opt.InsHiMm - 1e-9;
                // 保温「用不上」有两种：顶到量程端，或**一格太粗**（见上）。两种都要板厚接手。
                bool insDeadLo = insAtLo || insTooCoarse;
                bool insDeadHi = insAtHi || insTooCoarse;
                double dT = 0;
                string why = "";
                // 接手时步长按**误差 ÷ 实测斜率**定（slopeThk 在线量，见上）。
                // 再乘 0.7 的阻尼：斜率是估的，宁可两步到位也不要过冲后来回跳。
                double stepD = Math.Clamp(0.7 * eD / Math.Max(5.0, slopeThk[j]),
                                          -opt.ThickStepMm, opt.ThickStepMm);
                // ★ 交接必须**彻底**：保温用不上的时候，板厚要接管**整个** D 的调节，
                //   而不只是在窗口两端救火。第一版只在「D < 靶−死区」或「D > 上界」时才动板厚，
                //   于是抽热落在 (1.65, 4.2) 这一段里时**两个旋钮都不动** ——
                //   系统就停在 ③ ≈ 9.4 上（收货门槛 8.5）动弹不得，看着像收敛，其实是没人管。
                if (insDeadLo && Math.Abs(eD) > opt.DrawDeadW)
                {
                    dT = stepD;                                    // eD>0 ⇒ 加厚补抽热；eD<0 ⇒ 削薄（顺带省铂）
                    if (Math.Abs(dT) < opt.QuantThickMm) dT = Math.Sign(eD) * opt.QuantThickMm;
                    why = insAtLo ? "保温已到下界，板厚接手调抽热"
                                  : "保温一格太粗（一格跨过整个窗口），板厚接手细调抽热";
                }
                else if (insDeadHi && D > drawMax)
                { dT = Math.Min(stepD, -opt.QuantThickMm); why = "保温已到上界，板厚接手削抽热"; }
                else if (ringSat && !double.IsNaN(e2) && e2 > discTgtK + opt.DiscOverDeadK)
                { dT = +opt.ThickStepMm; why = "环已到上限仍压不住 ②″"; }
                // ⚠ 串级控制的**必要条件**：外环只在内环稳住之后才动。
                //   实测（第一版漏了这条）：板厚每轮都走 0.15 mm，而保温还没追上，
                //   于是第 5 轮抽热被一次性压到 −11.3/−7.6/−7.4/−10.3，第 6 轮又被顶回
                //   +7.9/+3.9/+4.0/+8.3（③ 冲到 19.9）—— **两环在同一时间尺度上打架**。
                //   加上「|eD| 已在死区内」这一条，才是真正的串级：内环管值，外环管阀位。
                else if (opt.SaveMetal && roomy && Math.Abs(eD) <= opt.DrawDeadW)
                {
                    double slack = d.TabInsulMm[j] - opt.InsLoMm;      // 保温余量 mm
                    if (slack > 0.05)
                    { dT = -Math.Clamp(slack * opt.ThinGain, opt.ThinStepMm, opt.ThickStepMm); why = "省铂"; }
                }

                if (Math.Abs(dT) > 1e-12)
                {
                    double nt = Math.Clamp(d.TabThickMm[j] + dT, tLo, opt.ThickHiMm);
                    if (Math.Abs(nt - d.TabThickMm[j]) > 1e-9)
                    {
                        d.TabThickMm[j] = nt; moved = true;
                        if (why.Length > 0 && dT > 0)
                            Log($"       └ 第{j + 1}片加厚到 {nt:0.00} mm：{why}");
                    }
                }
            }
            if (!moved) { Log("   ⇒ 所有旋钮都到位或都顶死，停"); earlyWhy = "所有旋钮都到位或都顶死"; break; }
        }

        var stop = StopReason(roundsUsed, opt.MaxRounds, earlyWhy, bestRound);
        res.RoundsUsed = roundsUsed;
        res.HitRoundCap = stop.HitCap;
        res.StopWhy = stop.Why;
        res.GammaKPerW = gamma;
        var pick = bestFeas ?? bestAny;
        if (pick is null)
        {
            res.Feasible = false;
            if (res.Message.Length == 0) res.Message = "一轮都没算出来（见上面的异常/失败信息）";
            return res;
        }

        // ── 全判据复核（**含升温 ①**）。
        // ⚠ 定尺寸循环跑的是 CheckRamp=false ⇒ ① 从未参与其中的 AllOk。
        //   判定必须落在带升温的这一次上（§1.8 第 9 例：汇总行曾写「全过」而 ① 是 ✗）。
        res.Design = pick;
        try
        {
            var full = LineRunner.Run(pick.BuildCase(baseIn, checkRamp: true), null, cancel);
            res.Best = full;
            res.Feasible = full.Ok && full.AllOk;
            res.MassG = full.Ok ? full.Segments.Sum(s => s.MassG) + full.Flanges.Sum(f => f.MassG) : double.NaN;
            res.Message = !full.Ok ? "复核失败：" + full.Message
                        : full.AllOk ? "✓ 全判据通过（含升温）"
                        : "✗ " + string.Join("；", full.Failed);
            if (full.Ok)
            {
                pick.TotalMassG = res.MassG;
                pick.TubeMassG = full.Segments.Sum(s => s.MassG);
                pick.FlangeMassG = full.Flanges.Sum(f => f.MassG);
                pick.RampH = full.ValueOf(LineResult.Key.Ramp);
                pick.DiscOverK = full.ValueOf(LineResult.Key.DiscTemp);
                pick.HoleFluxW = full.ValueOf(LineResult.Key.NetFlux);
                pick.FlangeDipK = full.ValueOf(LineResult.Key.FlangeDip);
                pick.TubeJ = full.Segments.Max(s => s.TubeJAPerMm2);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { res.Feasible = false; res.Message = "复核异常：" + ex.Message; }
        return res;
    }
}
