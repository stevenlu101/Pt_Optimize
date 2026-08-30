using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **实测敏感度矩阵**（2026-08-30，算法普查 A① 的拆法；清单第 6 组第 13 件）。
///
/// ══ 它要回答什么
///
/// <see cref="Solver.Allocation"/> 是一张**手写**的「判据 → 抬哪个旋钮」表。
/// 它今天靠 <see cref="Solver"/> 里那道每次都跑的前提自检兜底（抬了没用就报前提不成立），
/// 但**表本身从来没有依据** —— 而 2026-08-30 之后问题更尖锐：
///
/// <code>
///   --monotone 实测：r₁ / r₂ / t₂ 三条对 ②′ 都单调变好
///   ⇒ ②′ 现在有 **四个**候选旋钮（板厚 / r₁ / r₂ / t₂），「抬哪个」查表定不了
/// </code>
///
/// 而 `--monotone` **回答不了**这个取舍：它四片同步、跨度远大于设计点，
/// 各支不在同一个工作点上（命令自己的告示：「斜率的绝对值不作依据」）。
///
/// ⇒ 本类补的正是那三条：**同一工作点、逐片、小扰动**。
///
/// ══ 三条设计上的选择，每条都有理由
///
/// ① **判的是「裕度」不是「判据值」**。裕度用 <see cref="Solver.PlateSlack"/>（唯一来源），
///    口径统一为「**正 = 过，越大越好**」⇒ <c>∂裕度/∂旋钮 &gt; 0</c> 就是「抬它有用」，
///    三条判据一个符号约定，不用逐条记「这条越大越好还是越小越好」。
///
/// ② **以「向上」的单侧导数为准**，向下那一侧只用来验平滑性。
///    理由有两条，都不是风格问题：
///      · <see cref="Solver"/> **只往上抬**（只增不减）⇒ 决策要的本来就是向上那一侧；
///      · 多数量的基准值**就压在下界上**（舌保温 0.3、环倍率 1.00 都是下界）⇒
///        向下扰动直接出了定义域，那一侧算出来的数没有意义。
///    ⚠ 2026-08-30 第一版正是栽在这里：步长 ±1 mm 把舌保温扰到 **−0.6 mm**（负保温层），
///      十二格全报「两侧符号不一致」，看着像非线性，其实是**扰出了定义域**。
///    向下那一侧在定义域内时才做符号一致性校验；不在，就明说「没验到」，
///    **不许记成「验过且一致」**。
///
/// ③ **同时测铂重斜率，给出 ∂裕度/∂铂重**。这才是取舍要的量 ——
///    「哪个旋钮**每克铂**买到的裕度最多」。⚠ 舌保温的 ∂铂重/∂旋钮 **恒为 0**
///    （`--monotone` 实测：0.3→8 mm 整条扫描 3547 g 一动不动）⇒ 它是**免费**的，
///    单独标出来，不参与除法（除零会变成 ±∞ 或 NaN，两种都会被误读）。
///
/// ══ 这不是一张能写死的表
///
/// 灵敏度**随形状变号**（窄舌 d②″/d倍率 ≈ −1.4、宽舌 +0.056；门见 SensitivitySignTests）。
/// 所以本类是**工具**，
/// 不是常数表：对手上这个形状测一次，才有资格谈分配。
///
/// ⚠ 也**不进求解器的热路径**：一次完整测量最多 6 旋钮 × 4 片 × 2 侧 + 基线 = 49 次整线解
///   （实测 35 分钟；下界处与结构性无效的格子会少解几次）。而求解器自己跑完也才 ~43 次。
///   它是**离线的设计工具**，不是在线的决策器。
/// </summary>
public static class SensitivityMatrix
{
    /// <summary>六个可动的量。前三个是求解器现有旋钮，后三个是渐变环形状（A⑨ 放开的）。</summary>
    public enum Var { Thick, Insul, Ring, RingR1, RingR2, RingT2 }

    public static string Name(Var v) => v switch
    {
        Var.Thick  => "板厚",
        Var.Insul  => "舌保温",
        Var.Ring   => "环倍率 t₁",
        Var.RingR1 => "环内级外扩 r₁",
        Var.RingR2 => "环外级外扩 r₂",
        Var.RingT2 => "环外级倍率 t₂",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static string Unit(Var v) => v is Var.Ring or Var.RingT2 ? "倍" : "mm";

    /// <summary>
    /// 扰动步长。**出处要说得出**，不许是拍的数：
    /// <code>
    ///   板厚 / 舌保温 / 环倍率   —— 与 `--vary` 同步长（±0.1 mm／±1 mm／±0.05）
    ///   r₁ / r₂ / t₂            —— 与界面控件的 Increment 同（±0.5 mm／±0.5 mm／±0.05）
    /// </code>
    /// ⚠ 步长不同**不影响可比性**：斜率是「每单位」的量，本来就已经除掉步长了。
    ///   步长只影响**截断误差**，而那由两侧半斜率的一致性来暴露（见类注释②）。
    /// </summary>
    public static double Step(Var v) => v switch
    {
        Var.Thick  => 0.1,
        Var.Insul  => 1.0,
        Var.Ring   => 0.05,
        Var.RingR1 => 0.5,
        Var.RingR2 => 0.5,
        Var.RingT2 => 0.05,
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>
    /// 各量的**定义域**。⚠ 这不是装饰：2026-08-30 第一次跑就栽在这上面 ——
    /// 舌保温基准值是 0.3/0.4/0.5 mm，而步长 ±1 mm ⇒ 下侧扰动到 **−0.6 mm**，
    /// 负保温层。于是十二格全报「两侧符号不一致」，看着像非线性，其实是**扰出了定义域**。
    /// </summary>
    public static double Lo(Var v, DesignSpec d, DesignInputs baseIn) => v switch
    {
        Var.Thick  => d.DiscFloorMm(baseIn),
        Var.Insul  => SizerOptions.InsLoMmConst,
        Var.Ring   => SizerOptions.RingLoConst,
        Var.RingT2 => SizerOptions.RingLoConst,
        Var.RingR1 => 0.5,
        Var.RingR2 => 1.0,
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    public static double Hi(Var v) => v switch
    {
        Var.Thick  => new SolverOptions().ThickHiMm,
        Var.Insul  => SizerOptions.InsHiMmConst,
        Var.Ring   => SizerOptions.RingHiConst,
        Var.RingT2 => SizerOptions.RingHiConst,
        Var.RingR1 => 10.0,      // --monotone 实测扫过的量程
        Var.RingR2 => 16.0,
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>
    /// ★★ **结构性无效**：这个量在这一点上**根本无从起作用**，不是「不敏感」。
    ///
    /// 渐变环两级台阶的厚度是 <c>t₁×板厚</c> 与 <c>t₂×板厚</c>。现役两档都是
    /// <c>t₁ = 1.00</c> ⇒ <c>t₂ = 1 + 0.4(t₁−1) = 1.00</c> ⇒ **三段厚度全相等，台阶不存在**。
    /// 台阶不存在时，挪它的**半径** r₁/r₂ 当然什么也不会变。
    ///
    /// ⚠ 这必须与「测出来是 0」分开报。<b>0.0000 会被读成「测过了，很小」</b>，
    ///   而真相是「这个量在此处不可能起作用」—— 两者对下一步的含义完全不同：
    ///   前者说「别动它」，后者说「先把台阶造出来，再谈它」。
    ///   （`--monotone` 扫这三支时特意先把 t₁ 钉到 1.50，就是为了让台阶存在。）
    /// </summary>
    public static string? Degenerate(DesignSpec d, Var v, int j)
    {
        if (v is not (Var.RingR1 or Var.RingR2)) return null;
        double t1 = d.RingMul[j], t2 = double.IsNaN(d.RingMul2[j]) ? d.RingMulOuter(j) : d.RingMul2[j];
        bool flat = Math.Abs(t1 - 1.0) < 1e-9 && Math.Abs(t2 - 1.0) < 1e-9;
        return flat
            ? $"**台阶不存在**（t₁ = {t1:0.00}，t₂ = {t2:0.00}，与板厚同厚）⇒ 挪它的半径不可能有任何作用。"
            + " 这是**结构性无效**，不是「不敏感」—— 要探索它，先把 t₁ 或 t₂ 抬离 1.00。"
            : null;
    }

    /// <summary>三条逐片判据，顺序固定 —— 表头与数组下标只有这一处对应关系。</summary>
    public static readonly string[] Keys =
    {
        LineResult.Key.NetFlux,      // ②′
        LineResult.Key.FlangeDip,    // ③
        LineResult.Key.DiscTemp,     // ②″
    };

    public static readonly string[] Codes = { "②′", "③", "②″" };

    /// <summary>把第 j 片的第 v 个量设成 <paramref name="value"/>。**唯一的写入口**。</summary>
    public static void Set(DesignSpec d, Var v, int j, double value)
    {
        switch (v)
        {
            case Var.Thick:  d.TabThickMm[j] = value; break;
            case Var.Insul:  d.TabInsulMm[j] = value; break;
            case Var.Ring:   d.RingMul[j] = value; break;
            case Var.RingR1: d.RingW1Mm[j] = value; break;
            case Var.RingR2: d.RingW2Mm[j] = value; break;
            case Var.RingT2: d.RingMul2[j] = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(v));
        }
    }

    /// <summary>
    /// 读第 j 片的第 v 个量。★ 后三个的模型默认是 <b>NaN = 用旧规则</b> ——
    /// 要扰动它就得先**把旧规则算出来的值坐实**，否则「基准点」根本不存在。
    /// 这一段规则**镜像** <see cref="DesignSpec.RingRadiiOf"/> / <see cref="DesignSpec.RingMulOuter"/>，
    /// 不另立一份（两边漂开时门会红）。
    /// </summary>
    public static double Get(DesignSpec d, Var v, int j) => v switch
    {
        Var.Thick  => d.TabThickMm[j],
        Var.Insul  => d.TabInsulMm[j],
        Var.Ring   => d.RingMul[j],
        Var.RingR1 => double.IsNaN(d.RingW1Mm[j]) ? d.RingWidthMm : d.RingW1Mm[j],
        Var.RingR2 => double.IsNaN(d.RingW2Mm[j]) ? 2 * d.RingWidthMm : d.RingW2Mm[j],
        Var.RingT2 => double.IsNaN(d.RingMul2[j]) ? d.RingMulOuter(j) : d.RingMul2[j],
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>一格：第 <see cref="Plate"/> 片的第 <see cref="V"/> 个量的实测导数。</summary>
    public sealed class Cell
    {
        public Var V;
        public int Plate;
        public double Base;

        /// <summary>
        /// ★★ ∂裕度/∂旋钮，**向上**的单侧导数，与 <see cref="Keys"/> 同序。正 = 抬它有用。
        ///
        /// 为什么以**向上**为准而不是中心差分：<see cref="Solver"/> **只往上抬**（只增不减），
        /// 所以决策要的就是向上那一侧。而且多数量的基准值**就压在下界上**
        /// （舌保温 0.3 = 下界、环倍率 1.00 = 下界）—— 向下扰动直接出了定义域，
        /// 那一侧算出来的数没有意义。中心差分在这种点上是**制造一个看起来正常的错数**。
        /// </summary>
        public double[] D = new double[3];
        /// <summary>向下那一侧（只在**定义域内**才算）。仅用于平滑性校验，不参与决策。</summary>
        public double[] DLo = new double[3];
        /// <summary>两侧符号一致吗。<see cref="DownInDomain"/> 为假时**没有校验过**，一律记 true 但另有说明。</summary>
        public bool[] SignAgree = new bool[3];
        /// <summary>向下那一侧在定义域内吗。假 ⇒ 平滑性没验到，不是「验过且一致」。</summary>
        public bool DownInDomain;
        /// <summary>实际用到的两个步长（可能被定义域夹过）。</summary>
        public double StepUp, StepDown;

        /// <summary>∂铂重/∂旋钮 g/单位。舌保温恒为 0（实测）。</summary>
        public double DMassG;

        /// <summary>
        /// **跨片**：同一条判据在**别的**片上被带动了多少（取绝对值最大的那片）。
        /// 用来看对角占优 —— 它若比对角项还大，「第 j 片的判据归第 j 片的旋钮管」就不成立。
        /// </summary>
        public double[] DOff = new double[3];

        /// <summary>
        /// ★★★ **带符号的整列跨片项**：<c>DCross[i][k] = ∂(第 k 片的第 i 条判据的裕度)/∂(本片的这个量)</c>。
        ///
        /// ⚠ 2026-08-30：<see cref="DOff"/> 取了**绝对值**，而「逐片二分会不会打架」问的正是**符号** ——
        /// 抬第 j 片的旋钮让别片**变好**是无害的（只增不减的求解器不会因此震荡），
        /// **变坏**才会螺旋：j 抬 → k 变差 → k 抬 → j 变差 → …
        ///
        /// 而且判据不是「几倍够不够」的感觉。逐片不动点迭代收敛的充分条件是**行和**：
        /// <code>
        ///   max_j  Σ_{k≠j} |M[k][j]| / |M[j][j]|  &lt; 1        （M[k][j] = ∂裕度_k/∂旋钮_j）
        /// </code>
        /// 三个邻片各 0.4 倍，行和就是 1.2 &gt; 1 —— 不收敛。所以必须把**整列**留下来。
        /// </summary>
        public double[][] DCross = { new double[0], new double[0], new double[0] };

        public bool Ok;
        public string Note = "";

        /// <summary>
        /// 每克铂买到的裕度。舌保温不花铂 ⇒ 返回 NaN 并由 <see cref="Free"/> 标出来，
        /// **不返回 ±∞** —— 无穷会被排序、被格式化、被读成一个数。
        /// </summary>
        public double PerGram(int i) => Free ? double.NaN : D[i] / DMassG;

        public bool Free => Math.Abs(DMassG) < 1e-9;
    }

    /// <summary>
    /// ★★★ **逐片二分会不会打架** —— 对一条判据、一个旋钮，把整张 4×4 的耦合矩阵
    /// <c>M[k][j] = ∂裕度_k/∂旋钮_j</c> 收成一个数：
    /// <code>
    ///   行和 ρ = max_j  Σ_{k≠j} |M[k][j]| / |M[j][j]|
    /// </code>
    /// <c>ρ &lt; 1</c> ⇒ 逐片各调各的会收敛（Jacobi 迭代的充分条件）。
    /// <c>ρ ≥ 1</c> ⇒ **可能不收敛** —— 一片抬完把别片推坏，别片抬完又把这片推坏。
    ///
    /// ⚠ 这是**充分条件**，不是必要条件：ρ ≥ 1 不等于一定发散，
    ///   但它意味着「逐片独立求根」这个前提**没有依据**了，不能再默认它成立。
    ///
    /// ⚠ 只在**同号会互相推坏**时才要紧。若跨片项让别片**变好**，抬一片顺带帮了别片，
    ///   只增不减的求解器不会因此震荡 —— 所以另报一份「只算有害方向」的行和。
    /// </summary>
    public static (double All, double Harmful) RowSum(IReadOnlyList<Cell> cells, Var v, int crit, int np)
    {
        double worstAll = 0, worstHarm = 0;
        for (int j = 0; j < np; j++)
        {
            var cj = cells.FirstOrDefault(c => c.V == v && c.Plate == j);
            if (cj is null || !cj.Ok || cj.DCross[crit].Length != np) return (double.NaN, double.NaN);
            double diag = cj.DCross[crit][j];
            if (double.IsNaN(diag) || Math.Abs(diag) < 1e-12) return (double.NaN, double.NaN);
            double sAll = 0, sHarm = 0;
            for (int k = 0; k < np; k++)
            {
                if (k == j) continue;
                double x = cj.DCross[crit][k];
                if (double.IsNaN(x)) continue;
                sAll += Math.Abs(x);
                // 「有害」= 把别片的裕度往**下**推（裕度口径：越大越好）
                if (x < 0) sHarm += -x;
            }
            worstAll = Math.Max(worstAll, sAll / Math.Abs(diag));
            worstHarm = Math.Max(worstHarm, sHarm / Math.Abs(diag));
        }
        return (worstAll, worstHarm);
    }

    public sealed class Result
    {
        public readonly List<Cell> Cells = new();
        public LineResult? Baseline;
        public double BaselineMassG;
        public int Solves;
        public double Seconds;
        public string Note = "";
    }

    /// <summary>
    /// 在 <paramref name="geometry"/> **当前那一点**上测。⚠ 不改动传进来的对象。
    /// </summary>
    /// <param name="vars">要测哪几个量。null = 全部六个。</param>
    public static Result Measure(DesignSpec geometry, DesignInputs baseIn,
                                 IReadOnlyList<Var>? vars = null,
                                 IProgress<string>? progress = null,
                                 CancellationToken cancel = default)
    {
        if (geometry is null) throw new ArgumentNullException(nameof(geometry));
        if (baseIn is null) throw new ArgumentNullException(nameof(baseIn));

        var use = vars ?? Enum.GetValues<Var>();
        var res = new Result();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d0 = geometry.Clone();
        int np = d0.TabThickMm.Length;

        // ★ 后三个的 NaN 要先坐实成数值，否则「基准点」不存在（见 Get 的注释）。
        for (int j = 0; j < np; j++)
            foreach (var v in new[] { Var.RingR1, Var.RingR2, Var.RingT2 })
                Set(d0, v, j, Get(d0, v, j));

        var lc0 = d0.BuildCase(baseIn, checkRamp: false);
        double dipMax = lc0.RootDeltaMaxK, discMax = lc0.DiscOverTempMaxK;

        LineResult? Run(DesignSpec d)
        {
            cancel.ThrowIfCancellationRequested();
            res.Solves++;
            try { var r = LineRunner.Run(d.BuildCase(baseIn, checkRamp: false), null, cancel); return r.Ok ? r : null; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        double[] Slacks(LineResult r, int j) =>
            Keys.Select(k => Solver.PlateSlack(r, k, j, dipMax, discMax)).ToArray();

        double Mass(LineResult r) => r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);

        progress?.Report("基线（不扰动）…");
        var b = Run(d0);
        if (b is null)
        {
            res.Note = "★ **基线解不出来** —— 整张矩阵无从谈起（判不了不算过）";
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }
        res.Baseline = b;
        res.BaselineMassG = Mass(b);

        var s0 = new double[np][];
        for (int j = 0; j < np; j++) s0[j] = Slacks(b, j);

        int total = use.Count * np, done = 0;
        foreach (var v in use)
            for (int j = 0; j < np; j++)
            {
                cancel.ThrowIfCancellationRequested();
                double h = Step(v), x0 = Get(d0, v, j);
                var c = new Cell { V = v, Plate = j, Base = x0 };
                done++;

                // ★★ 结构性无效的量**不测** —— 测出来的 0 会被读成「测过了，很小」。
                if (Degenerate(d0, v, j) is { } why)
                {
                    c.Ok = false; c.Note = why;
                    progress?.Report($"{Name(v)} · 片{j}　{done}/{total}　跳过：{why}");
                    res.Cells.Add(c);
                    continue;
                }

                // ★★ 扰动要**留在定义域内**。基准值压在下界上时（舌保温 0.3、环倍率 1.00），
                //   向下那一侧根本不存在 —— 硬扰出去算出的数没有意义。
                double lo = Lo(v, d0, baseIn), hi = Hi(v);
                double xUp = Math.Min(x0 + h, hi), xDn = Math.Max(x0 - h, lo);
                c.StepUp = xUp - x0; c.StepDown = x0 - xDn;
                if (c.StepUp <= 1e-12)
                {
                    c.Ok = false;
                    c.Note = $"已在上界 {hi:0.###} —— 向上无从扰动（而求解器只往上抬）";
                    res.Cells.Add(c);
                    continue;
                }
                c.DownInDomain = c.StepDown > 1e-12;
                progress?.Report($"{Name(v)} · 片{j}　{done}/{total}　（+{c.StepUp:0.###}"
                    + (c.DownInDomain ? $" / −{c.StepDown:0.###}" : " / 下侧出界，只测向上")
                    + $" {Unit(v)}）");

                var dHi = d0.Clone(); Set(dHi, v, j, xUp);
                var rHi = Run(dHi);
                LineResult? rLo = null;
                if (c.DownInDomain) { var dLo = d0.Clone(); Set(dLo, v, j, xDn); rLo = Run(dLo); }

                if (rHi is null)
                {
                    c.Ok = false; c.Note = "上侧解不出来 —— 判不了";
                    res.Cells.Add(c);
                    continue;
                }
                if (c.DownInDomain && rLo is null)
                {
                    c.DownInDomain = false;
                    c.Note = "下侧解不出来 ⇒ 平滑性没验到（向上那一侧仍然有效）";
                }

                var sHi = Slacks(rHi, j);
                var sLo = c.DownInDomain ? Slacks(rLo!, j) : null;
                for (int i = 0; i < 3; i++)
                {
                    c.D[i] = (sHi[i] - s0[j][i]) / c.StepUp;                 // ★ 向上，决策用的就是它
                    if (sLo is not null)
                    {
                        c.DLo[i] = (s0[j][i] - sLo[i]) / c.StepDown;
                        c.SignAgree[i] = !double.IsNaN(c.DLo[i]) && !double.IsNaN(c.D[i])
                                      && Math.Sign(c.DLo[i]) == Math.Sign(c.D[i]);
                    }
                    else { c.DLo[i] = double.NaN; c.SignAgree[i] = true; }

                    // 跨片：同一条判据在别的片上被带动多少（同样只看向上那一侧）。
                    // ★ **整列都留**，带符号 —— 只留一个绝对值最大的数，就答不了「会不会打架」。
                    var col = new double[np];
                    double off = 0;
                    for (int k = 0; k < np; k++)
                    {
                        double a = Solver.PlateSlack(rHi, Keys[i], k, dipMax, discMax);
                        double e = Solver.PlateSlack(b, Keys[i], k, dipMax, discMax);
                        col[k] = (double.IsNaN(a) || double.IsNaN(e)) ? double.NaN : (a - e) / c.StepUp;
                        if (k != j && !double.IsNaN(col[k])) off = Math.Max(off, Math.Abs(col[k]));
                    }
                    c.DCross[i] = col;
                    c.DOff[i] = off;
                }
                c.DMassG = (Mass(rHi) - res.BaselineMassG) / c.StepUp;
                c.Ok = true;
                res.Cells.Add(c);
            }

        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }
}
