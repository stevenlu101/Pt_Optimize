using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  2026-09-23（SEG，决 97 A「段电流连续根」；业主 2026-09-23 13:3x「决 04 选导航档保留，其余先按路线甲」）
//  病与归因：deliverable/R48_耦合轮数归因_段电流二分格彩票_2026-09-23.md —— SegmentSolver.FindCurrent 用 Roots.Monotone 二分到 xTol 0.005 A 后返回末括号中点，
//    段电流只能落在约 2.6e−3 A 一格上，G(x) 分段常数，外层耦合停机轮数成首达时间彩票（29 → 60 → 119）。
//  改法：FindCurrent 这一处调用改为 Roots.MonotoneContinuous（同一括号化、同一二分，收尾在末括号内线性插值）。
//  改回参数：DesignInputs.SegCurrentContinuousRoot（生产不设 = true = 连续根；false = 老中点，逐位等于改前）。
//
//  快门（本档）各管什么、阈值出处：
//   a  单调玩具函数（解析根）：连续根与解析根之差 ≤ E = M₂·h²/(8·m₁) + ε_fp（数学推出，式子见 Roots.MonotoneContinuous 的注释；
//      h 取 xTol（末括号宽 ≤ xTol），m₁、M₂ 取 [r − xTol, r + xTol] 上 |f′| 的下界、|f″| 的上界（解析式），ε_fp = 浮点舍入：16·2⁻⁵²·(|f 的项|/m₁ + |r|)，选定的舍入余量）；
//      改回（中点）的误差在这几例上大于 E（非空转）；三种写法（Monotone、连续根、改回）求值点序列逐位相同（二分分辨率不降）；改回的返回值与 Monotone 逐位相同。
//   a2 段解改回逐位：SegmentSolver.Solve 三组输入在改回口径下 CurrentA 与管温场 SHA-256 等于改前（8b90b5f）Linux 实跑的记录；连续根与中点之差 ≤ xTol/2（数学推出：插值点在末括号内）。
//   c  段电流对目标温度的台阶上界（2026-09-23 审查后改写；原标题「电流对目标温度连续」说过了头）：目标温度扫 N 步，相邻两步的电流增量与平均增量之差 ≤ 2·E_c·(1 + 1/N) + 曲率项（推导见门里注释）。
//      E_c = M₂·h²/(8·m₁) + ε̂/m₁；m₁、M₂ 在根附近用 SolveAtCurrent 三点差分量（±1 A），ε̂ = f(I) = 中点管温 − 设定 在根附近 ±xTol 上 17 点对直线拟合的最大偏差（实测的求值噪声幅度，
//      不是严格界：17 点之外可能更大 —— 门印出来）。改回（中点）口径在同一扫描上必须超出同一界（台阶看得见，非空转）。
//      c 证的是「台阶不超过 2E_c(1+1/N) + 曲率项（E_c 含实测求值噪声 ε̂）」，也就是没有二分格那么大的台阶；**不证连续**：段解 Bvp1D／Picard 按容差停机，
//      f(I) 分段光滑、带约 1e−4 K 的跳跃（审查探针实测），ε̂ 量到的正是这类跳跃，连续根之后段电流仍有 1e−4～5e−4 A 量级的残余台阶。
//      c 只扫 TSetC 方向：这个方向上 iHi（= 0.9·I_stab，随 TSetC）与 Profile 初值（tm = TSetC）都跟着变，二分格随之平移，改回 12 步里只跨 1 次括号；
//      外层耦合真正在变的是 NeighbourTemp*／FlangeDraw*，它们不动 iHi，二分格固定 —— c 不覆盖这个方向（审查探针：沿 NeighbourTempLeftC 1294～1306 K、步长 0.25 K，
//      连续根台阶偏差 4.77e−4 A，约为按同一写法重算之界的 89 %；E_c 没在那组输入上量过）。沿耦合变量方向的 c′ 没做，列【待决定】（实施记录 §7）。
//  慢门（R48SegContinuousRootSlowTests）：b 整线 1e−6 级扰动的停机轮数差；门 f 两行开 − 关；六个转储算例改回逐位与连续根位移。
//
//  覆盖：插值收尾的数学性质、改回逐位（段解层）；门 c：TSetC 方向、默认输入（无邻段、无法兰抽热）一处的段电流台阶上界。
//  不覆盖：段电流对输入连续（不成立，见门 c 那一段）；耦合变量（NeighbourTemp*／FlangeDraw*）方向的台阶；
//          整线层（慢门）；CoupledSolver（参考工具页）那条调用 SegmentSolver.Solve 的路径没有单独门（同一个 FindCurrent，推断行为相同）；
//          Windows 上的改回逐位记录（a2 只有 Linux 记录，Windows 红并印出本机数，见门里的话）。
// ════════════════════════════════════════════════════════════════════════════
public class R48SegContinuousRootTests
{
    private readonly ITestOutputHelper _o;
    public R48SegContinuousRootTests(ITestOutputHelper o) { _o = o; }

    const double XTol = DesignInputs.SegCurrentTolADefault;   // 0.005 A：与 FindCurrent 同一个 xTol
    const int MaxIter = 60;                                    // 与 FindCurrent 同一个 maxIter
    static readonly double EpsMach = Math.Pow(2, -52);
    static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>玩具函数：f、解析根 r、[r − xTol, r + xTol] 上 |f′| 下界 m₁ 与 |f″| 上界 M₂（解析式）、f 的项的量级（舍入用）、括号上端。</summary>
    sealed record Toy(string Name, Func<double, double> F, double Root, double M1, double M2, double Scale, double Hi);

    static IEnumerable<Toy> Toys()
    {
        double r1 = 500 * Math.Log(5);
        yield return new Toy("exp(x/500) − 5", x => Math.Exp(x / 500) - 5, r1,
            Math.Exp((r1 - XTol) / 500) / 500, Math.Exp((r1 + XTol) / 500) / 250000, 5, 2000);
        double r2 = Math.Sqrt(2e6);
        yield return new Toy("x² − 2·10⁶", x => x * x - 2e6, r2, 2 * (r2 - XTol), 2, 2e6, 2000);
        double r3 = 1000 * Math.Cbrt(1.7);
        yield return new Toy("(x/1000)³ − 1.7", x => Math.Pow(x / 1000, 3) - 1.7, r3,
            3 * (r3 - XTol) * (r3 - XTol) / 1e9, 6 * (r3 + XTol) / 1e9, 1.7, 2000);
        double r4 = 1200 * Math.Atan(1.5);
        Func<double, double> sec2 = x => 1 / (Math.Cos(x / 1200) * Math.Cos(x / 1200));
        yield return new Toy("tan(x/1200) − 1.5", x => Math.Tan(x / 1200) - 1.5, r4,
            sec2(r4 - XTol) / 1200, 2 * sec2(r4 + XTol) * Math.Tan((r4 + XTol) / 1200) / (1200.0 * 1200.0), 1.5, 1800);
    }

    // ─────────────────────────────── a ───────────────────────────────
    [Fact]
    public void 门a_玩具函数_连续根离解析根不超插值误差界_改回逐位等于Monotone_求值点相同()
    {
        double maxMidErr = 0, maxBound = 0;
        foreach (var t in Toys())
        {
            var seqOld = new List<double>(); var seqCont = new List<double>(); var seqRev = new List<double>();
            double old = Roots.Monotone(x => { seqOld.Add(x); return t.F(x); }, 1, t.Hi, XTol, MaxIter);
            double cont = Roots.MonotoneContinuous(x => { seqCont.Add(x); return t.F(x); }, 1, t.Hi, XTol, MaxIter);
            double rev = Roots.MonotoneContinuous(x => { seqRev.Add(x); return t.F(x); }, 1, t.Hi, XTol, MaxIter, continuous: false);

            double eInterp = t.M2 * XTol * XTol / (8 * t.M1);
            double eFp = 16 * EpsMach * (t.Scale / t.M1 + Math.Abs(t.Root));
            double bound = eInterp + eFp;
            double errCont = Math.Abs(cont - t.Root), errMid = Math.Abs(old - t.Root);
            _o.WriteLine($"{t.Name}：根 {R(t.Root)}　连续根差 {errCont:E3}　界 E = {eInterp:E3} + {eFp:E3} = {bound:E3}　中点差 {errMid:E3}（中点的界 h/2 ≤ {XTol / 2:E3}）　求值 {seqOld.Count} 次");

            Assert.True(errCont <= bound, $"{t.Name}：连续根离解析根 {errCont:E3} > 插值误差界 {bound:E3}");
            Assert.Equal(BitConverter.DoubleToInt64Bits(old), BitConverter.DoubleToInt64Bits(rev));            // 改回逐位
            Assert.Equal(seqOld.Select(BitConverter.DoubleToInt64Bits), seqCont.Select(BitConverter.DoubleToInt64Bits));   // 求值点逐位相同（二分分辨率不降）
            Assert.Equal(seqOld.Select(BitConverter.DoubleToInt64Bits), seqRev.Select(BitConverter.DoubleToInt64Bits));
            Assert.True(Math.Abs(cont - old) <= XTol / 2, "连续根跑出了末括号");
            maxMidErr = Math.Max(maxMidErr, errMid); maxBound = Math.Max(maxBound, bound);
        }
        // 非空转：同一界卡中点，至少最坏一例红（中点误差 ~ h/2 量级，界在 1e−8 量级）
        Assert.True(maxMidErr > maxBound, $"中点误差 {maxMidErr:E3} 没超过界 {maxBound:E3} —— 门分辨不出改回");

        // 带噪（非单调）函数：只证改回逐位与求值点相同（这就是 FindCurrent 的处境：残差带迭代噪声）
        Func<double, double> noisy = x => x - 1000.123 + 1e-4 * Math.Sin(1e5 * x);
        var s1 = new List<double>(); var s2 = new List<double>();
        double a = Roots.Monotone(x => { s1.Add(x); return noisy(x); }, 1, 2000, XTol, MaxIter);
        double b = Roots.MonotoneContinuous(x => { s2.Add(x); return noisy(x); }, 1, 2000, XTol, MaxIter, continuous: false);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
        Assert.Equal(s1, s2);
        // 端点恰为根：插值给出那个端点（f(lo) = 0 ⇒ 返回 lo；两端同为 0 ⇒ 中点）
        Assert.Equal(0.0, Roots.MonotoneContinuous(x => x, 0, 1, 1e-3, 60) , 12);
    }

    // ─────────────────────────────── a2 ───────────────────────────────
    /// <summary>
    /// 改前记录（Linux，.NET 8，本树改前 = 分支头 8b90b5f，SEG 在改代码前用临时探针 ZZSegPreProbe 跑的 SegmentSolver.Solve；探针取完即删，数在
    /// deliverable/R48_段电流连续根_实施记录_2026-09-23.md「改回逐位」一节）：CurrentA（R 格式）与 TMetal 逐点 R 串以换行连接的 SHA-256。
    /// </summary>
    static readonly (string Tag, Func<DesignInputs> P, string I, string ShaT)[] PreRecordsLinux =
    {
        ("默认", () => new DesignInputs(), "1817.3785245058946", "d580d00ce9178712d980495e58b586592025a57221a579f960e692b0a11b7725"),
        ("TSet1150", () => new DesignInputs { TSetC = 1150 }, "1501.0157515548196", "5674e6d3b593e8302fa3f7bb8fd06d121fc2c6856d6179607541a7b587ba6f17"),
        ("旧地板", () => new DesignInputs { SegCurrentTolA = DesignInputs.SegCurrentTolALegacy, SegPicardTolK = DesignInputs.SegPicardTolKLegacy, SegBvpTolK = DesignInputs.SegBvpTolKLegacy },
            "1817.3693389729033", "33c4838d7ae02bc6c20a3e5bae978add5388d8434538e7c74a56d69bd7ae3e4b"),
    };

    static string ShaOf(double[] a) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", a.Select(R))))).ToLowerInvariant();

    [Fact]
    public void 门a2_段解改回逐位等于改前_连续根在末括号内()
    {
        bool win = OperatingSystem.IsWindows();
        var fails = new List<string>();
        foreach (var (tag, mk, recI, recSha) in PreRecordsLinux)
        {
            var pRev = mk(); pRev.SegCurrentContinuousRoot = false;
            var pCont = mk();
            Assert.True(pCont.SegCurrentContinuousRoot, "生产默认不是连续根");
            var rRev = SegmentSolver.Solve(pRev); var rCont = SegmentSolver.Solve(pCont);
            Assert.True(rRev.Ok && rCont.Ok, rRev.Message + rCont.Message);
            string shaRev = ShaOf(rRev.TMetal);
            _o.WriteLine($"{tag}：改回 I = {R(rRev.CurrentA)}（记录 {recI}）　管温 SHA {shaRev}（记录 {recSha}）　连续根 I = {R(rCont.CurrentA)}　差 {rCont.CurrentA - rRev.CurrentA:E3} A　xTol {pCont.SegCurrentTolA}");
            Assert.True(Math.Abs(rCont.CurrentA - rRev.CurrentA) <= pCont.SegCurrentTolA / 2 + 1e-12, $"{tag}：连续根离中点 {Math.Abs(rCont.CurrentA - rRev.CurrentA):E3} A > xTol/2");
            Assert.NotEqual(R(rRev.CurrentA), R(rCont.CurrentA));   // 开关真的接进了段解
            if (!win)
            {
                if (R(rRev.CurrentA) != recI) fails.Add($"{tag}：改回 CurrentA {R(rRev.CurrentA)} ≠ 改前 {recI}");
                if (shaRev != recSha) fails.Add($"{tag}：改回管温场 SHA {shaRev} ≠ 改前 {recSha}");
            }
        }
        Assert.False(win, "Windows 上还没有改前记录：在分支头 8b90b5f（不含本改动）上跑同样三组 SegmentSolver.Solve，把 CurrentA（R）与 TMetal SHA 填进一份 Windows 记录（本机数见输出）。");
        Assert.True(fails.Count == 0, string.Join("\n", fails));
        // 参数表复制（Clone 走 JSON）要带着改回参数，否则整线注射与生产会各走一套
        var q = SegmentSolver.Clone(new DesignInputs { SegCurrentContinuousRoot = false });
        Assert.False(q.SegCurrentContinuousRoot);
    }

    // ─────────────────────────────── c ───────────────────────────────
    sealed class Sweep { public double[] T = Array.Empty<double>(), I = Array.Empty<double>(); public double MaxDev, MeanStep; }

    static Sweep Run(double t0, double dT, int n, bool continuous)
    {
        var s = new Sweep { T = new double[n + 1], I = new double[n + 1] };
        for (int k = 0; k <= n; k++)
        {
            var p = new DesignInputs { TSetC = t0 + k * dT, SegCurrentContinuousRoot = continuous };
            var r = SegmentSolver.Solve(p);
            Assert.True(r.Ok, r.Message);
            s.T[k] = p.TSetC; s.I[k] = r.CurrentA;
        }
        s.MeanStep = (s.I[n] - s.I[0]) / n;
        for (int k = 0; k < n; k++) s.MaxDev = Math.Max(s.MaxDev, Math.Abs(s.I[k + 1] - s.I[k] - s.MeanStep));
        return s;
    }

    /// <summary>
    /// 推导（台阶的界）：记 r(T) 为光滑真根，Î(T) = r(T) + e(T)，|e| ≤ E_c（插值误差 + 求值噪声，Roots.MonotoneContinuous 注释）。
    ///   相邻两步 ΔÎ_k − Δr_k ∈ [−2E_c, 2E_c]；平均增量 (Î_N − Î_0)/N 与 r 的平均增量差 ≤ 2E_c/N；
    ///   r 在扫描段上不是直线的部分：|Δr_k − 平均 Δr| ≤ |r″|·N·ΔT²（r″ 用 T0 ± 5 K 三点差分量）。
    ///   ⇒ max_k |ΔÎ_k − 平均增量| ≤ 2E_c·(1 + 1/N) + |r″|·N·ΔT²。改回（中点）时 e 可达 h/2，台阶 ≈ 一格 ⇒ 同一界必须被超出。
    /// </summary>
    [Fact]
    public void 门c_电流对目标温度连续_扫一小步没有二分格台阶_改回台阶看得见()
    {
        var p0 = new DesignInputs();
        double t0 = p0.TSetC;
        var base0 = SegmentSolver.Solve(p0);
        Assert.True(base0.Ok, base0.Message);
        double i0 = base0.CurrentA;
        // f(I) = 中点管温 − 设定（与 FindCurrent 的 Residual 同一个量：SizeWall = false 时两条路的壁厚、截面、Profile 调用相同）
        double F(double I) { var r = SegmentSolver.SolveAtCurrent(new DesignInputs(), I); Assert.True(r.Ok, r.Message); return r.TMetal[r.TMetal.Length / 2] - t0; }
        double fm = F(i0 - 1), f0 = F(i0), fp = F(i0 + 1);
        double m1 = Math.Min(Math.Abs(f0 - fm), Math.Abs(fp - f0));   // K/A，两侧割线斜率取小
        double M2 = Math.Abs(fp - 2 * f0 + fm);                      // K/A²
        // 求值噪声：根附近 ±xTol 17 点对直线最小二乘拟合的最大偏差
        int nn = 17; var xs = new double[nn]; var ys = new double[nn];
        for (int j = 0; j < nn; j++) { xs[j] = i0 + (j - 8) * XTol / 8; ys[j] = F(xs[j]); }
        double xm = xs.Average(), ym = ys.Average();
        double slope = xs.Zip(ys, (x, y) => (x - xm) * (y - ym)).Sum() / xs.Sum(x => (x - xm) * (x - xm));
        double epsHat = xs.Zip(ys, (x, y) => Math.Abs(y - (ym + slope * (x - xm)))).Max();
        double eC = M2 * XTol * XTol / (8 * m1) + epsHat / m1;

        // r″(T)：T0 ± 5 K
        double iM = SegmentSolver.Solve(new DesignInputs { TSetC = t0 - 5 }).CurrentA, iP = SegmentSolver.Solve(new DesignInputs { TSetC = t0 + 5 }).CurrentA;
        double rpp = Math.Abs(iP - 2 * i0 + iM) / 25.0;               // A/K²
        double dIdT = (iP - iM) / 10.0;                               // A/K
        // 步长：让真根每步只挪约 0.3 格（格 ≈ xTol/2..xTol），改回时台阶才看得见
        int N = 12; double dT = 0.3 * (XTol / 2) / dIdT;
        double bound = 2 * eC * (1 + 1.0 / N) + rpp * N * dT * dT;

        var cont = Run(t0, dT, N, true);
        var rev = Run(t0, dT, N, false);
        _o.WriteLine($"根附近：I0 = {R(i0)} A　m₁ = {m1:E4} K/A　M₂ = {M2:E3} K/A²　求值噪声 ε̂ = {epsHat:E3} K（17 点，±xTol）　E_c = {eC:E3} A");
        _o.WriteLine($"dI/dT = {dIdT:E4} A/K　|r″| = {rpp:E3} A/K²　扫描 N = {N}、ΔT = {dT:E4} K　界 = 2E_c(1+1/N) + |r″|NΔT² = {bound:E3} A");
        _o.WriteLine($"连续根：最大台阶偏差 {cont.MaxDev:E3} A　平均增量 {cont.MeanStep:E3} A");
        _o.WriteLine($"改回（中点）：最大台阶偏差 {rev.MaxDev:E3} A　平均增量 {rev.MeanStep:E3} A　增量序列 " + string.Join(" ", Enumerable.Range(0, N).Select(k => (rev.I[k + 1] - rev.I[k]).ToString("E2", CultureInfo.InvariantCulture))));
        _o.WriteLine("连续根增量序列 " + string.Join(" ", Enumerable.Range(0, N).Select(k => (cont.I[k + 1] - cont.I[k]).ToString("E3", CultureInfo.InvariantCulture))));
        Assert.True(cont.MaxDev <= bound, $"连续根台阶偏差 {cont.MaxDev:E3} A > 界 {bound:E3} A");
        Assert.True(rev.MaxDev > bound, $"改回（中点）台阶偏差 {rev.MaxDev:E3} A 没超过界 {bound:E3} A —— 门分辨不出改回");
    }
}
