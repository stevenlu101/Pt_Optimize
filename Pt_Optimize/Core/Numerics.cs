using System;
using MathNet.Numerics.Interpolation;
using MathNet.Numerics.RootFinding;

namespace PtOptimize.Core;

/// <summary>
/// 温度 → 某物理量 的一维插值表。用三次样条，可解析求导——
/// 线性化所需的切线斜率因此是解析值，不是差分近似（见理论模型 §4.4「辐射线性化：割线与切线」）。
/// </summary>
public sealed class LossTable
{
    private readonly IInterpolation _spline;
    private readonly double _lo, _hi;
    private readonly double[] _x, _y;

    public LossTable(double tMin, double tMax, int n, Func<double, double> f)
    {
        n = Math.Max(8, n);
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = tMin + (tMax - tMin) * i / (n - 1);
            y[i] = f(x[i]);
        }
        _spline = CubicSpline.InterpolateNatural(x, y);
        _lo = tMin; _hi = tMax;
        _x = x; _y = y;
    }

    private LossTable(double[] x, double[] y, double lo, double hi)
    {
        _spline = CubicSpline.InterpolateNatural(x, y);
        _lo = lo; _hi = hi; _x = x; _y = y;
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5）：两张表按份额混合 —— 节点值 y = y_b + f·(y_a − y_b)，再建自然样条。
    /// 两张表节点 x 相同、端点条件相同（都是自然样条）时，样条对 y 线性 ⇒ 结果就是 f·a + (1−f)·b 这条样条本身，精确，不另算热流。
    /// 用途：保温分界圆穿过的格子，一部分面积包法兰保温、一部分包舌保温，金属同温、两块面积并联 ⇒ **混合热流，不混合厚度**
    /// （一维热阻对厚度非线性，按份额平均厚度再查表是另一个物理 —— 物理把关人第四轮条件 1）。
    /// 写成 y_b + f·(y_a − y_b)：两张表逐位相同时结果逐位不变（正对照门靠这一点）。
    /// 节点不同 ⇒ 抛异常，不许悄悄在不同温度网格上混。
    /// </summary>
    public static LossTable Blend(LossTable a, LossTable b, double f)
    {
        if (a._x.Length != b._x.Length || a._lo != b._lo || a._hi != b._hi)
            throw new ArgumentException("两张损失表的温度节点不同，不能按份额混合");
        for (int i = 0; i < a._x.Length; i++)
            if (a._x[i] != b._x[i]) throw new ArgumentException("两张损失表的温度节点不同，不能按份额混合");
        var y = new double[a._y.Length];
        for (int i = 0; i < y.Length; i++) y[i] = b._y[i] + f * (a._y[i] - b._y[i]);
        return new LossTable((double[])a._x.Clone(), y, a._lo, a._hi);
    }

    /// <summary>
    /// ★ R48（2026-09-15，Opus 5；常驻数值把关人第十四轮）：表覆盖的温度区间与节点数 —— <see cref="Eval"/>／<see cref="Slope"/> 超出区间**静默钳住**，
    /// 用这张表的求解器解完要自己拿解出的温度对一下（<see cref="Covers"/>），超界就把用它的判据判不了或写进结果。
    /// </summary>
    public double LoC => _lo;
    /// <summary>表的温度上限 °C（见 <see cref="LoC"/>）。</summary>
    public double HiC => _hi;
    /// <summary>表的节点数。</summary>
    public int Nodes => _x.Length;
    /// <summary>温度 <paramref name="t"/> 在不在表覆盖的闭区间里（NaN 不算在）。</summary>
    public bool Covers(double t) => t >= _lo && t <= _hi;

    public double Eval(double t) => _spline.Interpolate(Math.Clamp(t, _lo, _hi));

    /// <summary>解析一阶导数（样条微分），用于线性化的切线斜率。</summary>
    public double Slope(double t) => _spline.Differentiate(Math.Clamp(t, _lo, _hi));
}

public class RootNotBracketedException : Exception
{
    public RootNotBracketedException(string m) : base(m) { }
}

/// <summary>标量方程求根。</summary>
public static class Roots
{
    /// <summary>
    /// 单调 + 带噪 + 昂贵的函数：纯二分。
    ///
    /// 不用 Brent —— 其逆二次插值会把内层迭代残留的噪声放大，
    /// 导致求根跳到错误的分支（见 Segment_GridConvergence 的排查）。
    /// 二分只依赖符号，对噪声免疫，且每步区间严格减半。
    ///
    /// 必须先确认括号内确实跨零；无法括号化时抛异常而不是返回中点，
    /// 避免把无效结果当成解往下传。
    /// </summary>
    public static double Monotone(Func<double, double> f, double lo, double hi,
                                  double xTol, int maxIter = 200)
    {
        double fa = f(lo), fb = f(hi);
        int expand = 0;
        while (fa * fb > 0 && expand++ < 12)
        {
            double span = hi - lo;
            if (Math.Abs(fa) < Math.Abs(fb)) { lo = Math.Max(1e-9, lo - span); fa = f(lo); }
            else { hi += span; fb = f(hi); }
        }
        if (double.IsNaN(fa) || double.IsNaN(fb) || fa * fb > 0)
            throw new RootNotBracketedException(
                $"无法括号化：f({lo:G6})={fa:G6}, f({hi:G6})={fb:G6}");

        for (int i = 0; i < maxIter && (hi - lo) > xTol; i++)
        {
            double m = 0.5 * (lo + hi), fm = f(m);
            if (double.IsNaN(fm)) break;
            if (fm * fa > 0) { lo = m; fa = fm; } else { hi = m; }
        }
        return 0.5 * (lo + hi);
    }
}

/// <summary>
/// 一维有限体积两点边值问题内核（非线性，Picard + 三对角）：
///
///     d/ds( K(s)·dT/ds ) + S(s,T) = 0 ,   s ∈ [s0, s1]
///
/// 管子取 s = x、K = k·A；法兰取 s = r、K = k·t_f(r)·r —— 同一内核。
/// 端点用半控制体能量平衡，附加端部通量 F(T)（流出控制体为正）。
///
/// 该内核由 Pt_Optimize.Tests 用制造解法(MMS)与解析肋片解验证。
/// </summary>
public static class Bvp1D
{
    public sealed class Options
    {
        public int Nodes = 201;
        public double Relax = 0.6;         // 欠松弛：辐射 T⁴ 非线性需要
        public int MaxIter = 200;
        public double Tol = 0.01;          // K
        public double ClampLo = -273.0, ClampHi = 2200.0;
    }

    public sealed class Boundary
    {
        /// <summary>第一类边界；置 null 则用通量边界</summary>
        public double? Fixed;
        /// <summary>端部附加通量 F(T)，流出为正 [W]</summary>
        public Func<double, double>? Flux;
        /// <summary>dF/dT，用于线性化</summary>
        public Func<double, double>? DFlux;

        public static Boundary Dirichlet(double t) => new() { Fixed = t };
        public static Boundary Adiabatic() => new();
        public static Boundary WithFlux(Func<double, double> f, Func<double, double> df)
            => new() { Flux = f, DFlux = df };
    }

    /// <param name="kOf">K(s)</param>
    /// <param name="source">S(s,T)</param>
    /// <param name="dSourceDT">∂S/∂T</param>
    public static double[] Solve(
        double s0, double s1,
        Func<double, double> kOf,
        Func<double, double, double> source,
        Func<double, double, double> dSourceDT,
        Boundary left, Boundary right,
        double[]? init = null,
        Options? opt = null)
    {
        opt ??= new Options();
        int n = Math.Max(11, opt.Nodes | 1);
        double ds = (s1 - s0) / (n - 1);

        var s = new double[n];
        var T = new double[n];
        for (int i = 0; i < n; i++)
        {
            s[i] = s0 + i * ds;
            T[i] = init != null && init.Length == n ? init[i]
                 : (left.Fixed ?? right.Fixed ?? 0.0);
        }

        // 面导度 K_{i+1/2}/ds
        var g = new double[n - 1];
        for (int i = 0; i < n - 1; i++) g[i] = kOf(0.5 * (s[i] + s[i + 1])) / ds;

        var a = new double[n]; var b = new double[n];
        var c = new double[n]; var d = new double[n];

        for (int iter = 0; iter < opt.MaxIter; iter++)
        {
            for (int i = 0; i < n; i++)
            {
                double ts = T[i];
                double s0v = source(s[i], ts);
                double sp = dSourceDT(s[i], ts);

                if (i == 0 && left.Fixed.HasValue)
                { a[i] = 0; b[i] = 1; c[i] = 0; d[i] = left.Fixed.Value; continue; }
                if (i == n - 1 && right.Fixed.HasValue)
                { a[i] = 0; b[i] = 1; c[i] = 0; d[i] = right.Fixed.Value; continue; }

                if (i == 0)
                {
                    double f0 = left.Flux?.Invoke(ts) ?? 0, fp = left.DFlux?.Invoke(ts) ?? 0;
                    a[i] = 0; c[i] = g[0];
                    b[i] = -g[0] + sp * ds * 0.5 - fp;
                    d[i] = -((s0v - sp * ts) * ds * 0.5 - f0 + fp * ts);
                }
                else if (i == n - 1)
                {
                    double f0 = right.Flux?.Invoke(ts) ?? 0, fp = right.DFlux?.Invoke(ts) ?? 0;
                    a[i] = g[n - 2]; c[i] = 0;
                    b[i] = -g[n - 2] + sp * ds * 0.5 - fp;
                    d[i] = -((s0v - sp * ts) * ds * 0.5 - f0 + fp * ts);
                }
                else
                {
                    a[i] = g[i - 1]; c[i] = g[i];
                    b[i] = -(g[i - 1] + g[i]) + sp * ds;
                    d[i] = -(s0v - sp * ts) * ds;
                }
            }

            var tn = Thomas(a, b, c, d);
            double err = 0;
            for (int i = 0; i < n; i++)
            {
                double v = double.IsNaN(tn[i]) ? T[i] : Math.Clamp(tn[i], opt.ClampLo, opt.ClampHi);
                err = Math.Max(err, Math.Abs(v - T[i]));
                T[i] = (1 - opt.Relax) * T[i] + opt.Relax * v;
            }
            if (err < opt.Tol) break;
        }
        return T;
    }

    /// <summary>三对角追赶法（Thomas）。O(n)，对角占优时无条件稳定。</summary>
    public static double[] Thomas(double[] a, double[] b, double[] c, double[] d)
    {
        int n = b.Length;
        var cp = new double[n]; var dp = new double[n]; var x = new double[n];
        cp[0] = c[0] / b[0]; dp[0] = d[0] / b[0];
        for (int i = 1; i < n; i++)
        {
            double m = b[i] - a[i] * cp[i - 1];
            if (Math.Abs(m) < 1e-30) m = 1e-30;
            cp[i] = c[i] / m;
            dp[i] = (d[i] - a[i] * dp[i - 1]) / m;
        }
        x[n - 1] = dp[n - 1];
        for (int i = n - 2; i >= 0; i--) x[i] = dp[i] - cp[i] * x[i + 1];
        return x;
    }
}
