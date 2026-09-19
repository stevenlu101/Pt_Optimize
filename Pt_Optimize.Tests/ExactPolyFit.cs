using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace PtOptimize.Tests;

/// <summary>
/// 精确有理数（BigInteger 分子分母，恒约分，分母为正）。2026-09-15，Opus 5。
/// double 能**精确**转成分数（尾数 × 2^指数），所以最小二乘可以一位不丢地解。
/// </summary>
internal readonly struct Q : IComparable<Q>
{
    public readonly BigInteger N, D;
    public Q(BigInteger n, BigInteger d)
    {
        if (d.IsZero) throw new DivideByZeroException();
        if (d.Sign < 0) { n = -n; d = -d; }
        var g = BigInteger.GreatestCommonDivisor(n, d);
        if (!g.IsOne && !g.IsZero) { n /= g; d /= g; }
        N = n; D = d.IsZero ? BigInteger.One : d;
    }
    public static readonly Q Zero = new(0, 1), One = new(1, 1);
    public static implicit operator Q(int v) => new(v, 1);

    /// <summary>double → 精确分数（NaN/∞ 抛出）。</summary>
    public static Q FromDouble(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("不是有限数");
        if (v == 0) return Zero;
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else man |= 1L << 52;
        exp -= 1075;
        BigInteger n = man, d = 1;
        if (exp > 0) n <<= exp; else d <<= -exp;
        return new Q(neg ? -n : n, d);
    }

    public static Q operator +(Q a, Q b) => new(a.N * b.D + b.N * a.D, a.D * b.D);
    public static Q operator -(Q a, Q b) => new(a.N * b.D - b.N * a.D, a.D * b.D);
    public static Q operator -(Q a) => new(-a.N, a.D);
    public static Q operator *(Q a, Q b) => new(a.N * b.N, a.D * b.D);
    public static Q operator /(Q a, Q b) => new(a.N * b.D, a.D * b.N);
    public int Sign => N.Sign;
    public Q Abs() => new(BigInteger.Abs(N), D);
    public int CompareTo(Q o) => (N * o.D).CompareTo(o.N * D);
    public static bool operator <(Q a, Q b) => a.CompareTo(b) < 0;
    public static bool operator >(Q a, Q b) => a.CompareTo(b) > 0;
    public static bool operator <=(Q a, Q b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Q a, Q b) => a.CompareTo(b) >= 0;

    public static Q Pow10(int e) => e >= 0 ? new Q(BigInteger.Pow(10, e), 1) : new Q(1, BigInteger.Pow(10, -e));

    /// <summary>转 double（经 17 位十进制，够报告用；比对一律用分数本身）。</summary>
    public double ToDouble()
    {
        if (N.IsZero) return 0;
        var (mant, e) = ToSig(17);
        return double.Parse($"{(N.Sign < 0 ? "-" : "")}{mant}E{e - 16}", NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>|q| 取 sig 位有效数字（四舍五入，远离零），返回整数尾数与 q ≈ mant × 10^(e−sig+1) 的 e。</summary>
    public (BigInteger Mant, int Exp) ToSig(int sig) => ToSigWithMargin(sig).Item1;

    /// <summary>同上，另给「离舍入分界多远」（以末位单位计，0 = 正好在 .5 上，0.5 = 正好落在整数上）。</summary>
    public ((BigInteger Mant, int Exp), double Margin) ToSigWithMargin(int sig)
    {
        if (N.IsZero) return ((0, 0), 0.5);
        var a = Abs();
        int e = (int)Math.Floor(BigInteger.Log10(a.N) - BigInteger.Log10(a.D));
        // 修正 Log10 的浮点误差：保证 10^e ≤ a < 10^(e+1)
        while (a < Pow10(e)) e--;
        while (a >= Pow10(e + 1)) e++;
        var scaled = a / Pow10(e - sig + 1);             // ∈ [10^(sig−1), 10^sig)
        BigInteger fl = BigInteger.Divide(scaled.N, scaled.D);
        var frac = scaled - new Q(fl, 1);                // ∈ [0,1)
        var half = new Q(1, 2);
        BigInteger m = frac >= half ? fl + 1 : fl;
        double margin = Math.Abs((frac - half).ToDoubleCoarse());
        if (m == BigInteger.Pow(10, sig)) { m = BigInteger.Pow(10, sig - 1); e++; }
        return ((m, e), margin);
    }

    /// <summary>粗转 double（先把分子分母同时右移到 60 位以内，避免 BigInteger→double 溢出成 ∞）。只用于报告。</summary>
    private double ToDoubleCoarse()
    {
        long bits = Math.Max(BigInteger.Abs(N).GetBitLength(), D.GetBitLength());
        int shift = (int)Math.Max(0, bits - 60);
        return (double)(N >> shift) / (double)(D >> shift);
    }

    /// <summary>按 Excel 数字格式「0.000000E+00」输出（sig = 7）。</summary>
    public string ToExcelSci(int sig = 7)
    {
        var (m, e) = ToSig(sig);
        string digits = m.ToString(CultureInfo.InvariantCulture);
        string s = digits[..1] + "." + digits[1..];
        return (N.Sign < 0 ? "-" : "") + s + "E" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 多项式最小二乘的**精确解**（Excel 图表趋势线同一定义：y ≈ Σ c_k x^k，k = 0..阶数，截距自由）。2026-09-15，Opus 5。
///
/// 数值把关实测：原始幂 double 正规方程系数相对误差约 1e-8，而离第 7 位舍入分界最近的 PtRh20 c5 相对距离只有 5.7e-9
/// ⇒ 浮点正规方程**可能翻掉第 7 位**，不能用；精确有理数没有条件数问题，7×7 规模毫秒级。
/// Excel 内部用什么分解无从得知，也不需要：能验证的含义只有「真最小二乘解在显示位内一致」。
/// </summary>
internal static class ExactPolyFit
{
    public sealed record Result(Q[] Coeffs, Q RSquared)
    {
        public double[] CoeffsDouble => Coeffs.Select(c => c.ToDouble()).ToArray();
    }

    public static Result Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int order)
    {
        if (xs.Count != ys.Count) throw new ArgumentException("x、y 个数不等");
        int n = order + 1;
        var X = xs.Select(Q.FromDouble).ToArray();
        var Y = ys.Select(Q.FromDouble).ToArray();
        var A = new Q[n, n + 1];
        for (int i = 0; i < n; i++) for (int j = 0; j <= n; j++) A[i, j] = Q.Zero;
        for (int p = 0; p < X.Length; p++)
        {
            var pw = new Q[2 * n];
            pw[0] = Q.One;
            for (int k = 1; k < 2 * n; k++) pw[k] = pw[k - 1] * X[p];
            for (int i = 0; i < n; i++)
            {
                A[i, n] += pw[i] * Y[p];
                for (int j = 0; j < n; j++) A[i, j] += pw[i + j];
            }
        }
        // 精确高斯消元（主元非零即可）
        for (int col = 0; col < n; col++)
        {
            int piv = Enumerable.Range(col, n - col).First(r => A[r, col].Sign != 0);
            if (piv != col) for (int j = 0; j <= n; j++) (A[col, j], A[piv, j]) = (A[piv, j], A[col, j]);
            for (int r = 0; r < n; r++)
            {
                if (r == col || A[r, col].Sign == 0) continue;
                var f = A[r, col] / A[col, col];
                for (int j = col; j <= n; j++) A[r, j] -= f * A[col, j];
            }
        }
        var c = Enumerable.Range(0, n).Select(i => A[i, n] / A[i, i]).ToArray();

        var mean = Y.Aggregate(Q.Zero, (s, y) => s + y) / new Q(Y.Length, 1);
        Q sse = Q.Zero, sst = Q.Zero;
        for (int p = 0; p < X.Length; p++)
        {
            Q yh = Q.Zero, pw1 = Q.One;
            for (int k = 0; k < n; k++) { yh += c[k] * pw1; pw1 *= X[p]; }
            var r1 = Y[p] - yh; sse += r1 * r1;
            var d = Y[p] - mean; sst += d * d;
        }
        return new Result(c, Q.One - sse / sst);
    }

    /// <summary>精确求值 Σ c_k x^k。</summary>
    public static Q Eval(Q[] c, Q x)
    {
        Q acc = Q.Zero, pw = Q.One;
        foreach (var ck in c) { acc += ck * pw; pw *= x; }
        return acc;
    }
}
