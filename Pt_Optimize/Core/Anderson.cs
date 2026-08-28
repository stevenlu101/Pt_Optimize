using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ Anderson 加速（带安全阀与重启）—— 专治段↔法兰耦合的慢模式。
///
/// **为什么需要它**（2026-08-16 实测，非推测）：
///   段↔法兰的不动点迭代环路增益 g ≈ 0.96，剩余误差 ≈ δ/(1−g) ≈ 25δ。
///   贴着设计记录点时 200 轮够用；**只要一改参数就不够** ——
///   实测「管保温 1 mm」档 200 轮报未收敛（剩余误差 76.9 K），
///   只把轮数上限提到 1000、其余一律不动，第 734 轮才收敛（放大倍数 134）。
///   ⇒ 用户改个参数就看到「不可引用」，而这只是**慢**，不是发散。
///
/// **为什么是 Anderson 而不是别的**：
///   · 自适应 ω 实测在**打摆**（放大 121 次／回退 121 次，最后压在下限 0.15），
///     它只能调一个标量步长，治不了「多个慢模式方向」。
///   · Aitken Δ² 曾试过并**失败**：它只处理一个模式，且当时只作用在抽热向量上，
///     而慢模式是「抽热 ↔ 段间端温」耦合起来的方向（HANDOVER §1.85 已记）。
///   ⇒ Anderson 用最近 m 步的**残差差分**张成子空间做最小二乘外推，
///     天然覆盖多个方向，且 m 很小时代价可以忽略（本问题状态维数 ≤ 10）。
///
/// ⚠⚠ **最要紧的一条安全性质**：Anderson 只改变「怎么走到不动点」，
///   **不改变不动点在哪**。收敛时仍然满足 x = G(x)，与原来同一组方程。
///   ⇒ 它不可能把判据算成另一个答案；能出错的只有「没收敛却报收敛」，
///     而那由外层的剩余误差判据管（那条不经过本类）。
///   即便如此，仍加了两道闸：
///     ① 安全阀：AA 步长超过 Picard 步长的 κ 倍就**丢弃并重启历史**；
///     ② 非有限值（NaN/Inf）一律丢弃。
///   最坏情形**退化回原来的欠松弛 Picard**，不会比不用更差。
/// </summary>
public sealed class Anderson
{
    private readonly int _m;
    private readonly List<double[]> _dX = new();   // Δx
    private readonly List<double[]> _dF = new();   // ΔF，F = G(x) − x
    private double[]? _xPrev, _fPrev;
    private double[]? _w;          // 分量标度（重启后由第一组差分定下）
    private int _rejectRun;        // 连续被阀门打掉的次数

    public int Accepted, Rejected, Restarts;
    /// <summary>上一步是否真的走了 Anderson 步（false = 退回欠松弛 Picard）。
    /// 外层的 ω 自适应要据此决定**要不要动 ω** —— 见 LineRunner 里的说明。</summary>
    public bool LastAccepted { get; private set; }
    public int Depth => _dF.Count;

    public Anderson(int depth = 4) => _m = Math.Max(1, depth);

    /// <summary>
    /// 走一步。<paramref name="x"/> 当前状态，<paramref name="g"/> = G(x)。
    /// <paramref name="omega"/> 欠松弛（同时是兜底的 Picard 步长），
    /// <paramref name="kappa"/> 安全阀倍数。
    /// </summary>
    public double[] Step(double[] x, double[] g, double omega, double kappa)
    {
        int n = x.Length;
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = g[i] - x[i];

        // 兜底：原来的欠松弛 Picard 步。任何一步出问题都退回它。
        var picard = new double[n];
        for (int i = 0; i < n; i++) picard[i] = x[i] + omega * f[i];

        if (_xPrev is not null && _fPrev is not null)
        {
            var dx = new double[n];
            var df = new double[n];
            for (int i = 0; i < n; i++) { dx[i] = x[i] - _xPrev[i]; df[i] = f[i] - _fPrev[i]; }
            // ★ 分量标度：状态里混着**抽热（W）与端温（K）**两种量纲。
            //   不标度时最小二乘会被幅值大的那一组主导，另一组等于没进子空间 ——
            //   而慢模式恰恰是两者耦合的方向（早先 Aitken 只作用在抽热上而失效，同一个病）。
            //   ⇒ 重启后用**第一组差分**定下各分量的标度，之后保持不变（历史才自洽）。
            if (_w is null)
            {
                _w = new double[n];
                double rms = 0; for (int i = 0; i < n; i++) rms += df[i] * df[i];
                rms = Math.Sqrt(rms / Math.Max(1, n));
                for (int i = 0; i < n; i++) _w[i] = Math.Max(Math.Abs(df[i]), 1e-3 * Math.Max(rms, 1e-12));
            }
            _dX.Add(dx); _dF.Add(df);
            while (_dX.Count > _m) { _dX.RemoveAt(0); _dF.RemoveAt(0); }
        }
        _xPrev = (double[])x.Clone();
        _fPrev = (double[])f.Clone();
        LastAccepted = false;
        if (_dF.Count == 0 || _w is null) return picard;

        // min_γ ‖f − ΔF·γ‖（标度化）—— 正规方程 + Tikhonov
        // ⚠ 正则化强度：第一版 1e-8·tr/m **远远不够**。ΔF 列高度共线时 γ 会爆，
        //   算出的步长超过安全阀 ⇒ 实测 96/104 步被丢弃、深度永远为 0，等于没开。
        //   ⇒ 提到 1e-4·tr/m：宁可外推得保守一点，也不要每步都被阀门打掉。
        int m = _dF.Count;
        var A = new double[m, m];
        var b = new double[m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) A[i, j] = DotW(_dF[i], _dF[j], _w);
            b[i] = DotW(_dF[i], f, _w);
        }
        double tr = 0; for (int i = 0; i < m; i++) tr += A[i, i];
        if (!(tr > 0)) { Restart(); return picard; }
        double lam = 1e-4 * tr / m;
        for (int i = 0; i < m; i++) A[i, i] += lam;

        var gam = SolveSym(A, b, m);
        if (gam is null) { Restart(); return picard; }

        // x⁺ = x + ωf − Σ γⱼ (Δxⱼ + ω ΔFⱼ)     （type-II，带松弛）
        var cand = (double[])picard.Clone();
        for (int j = 0; j < m; j++)
            for (int i = 0; i < n; i++)
                cand[i] -= gam[j] * (_dX[j][i] + omega * _dF[j][i]);

        // ⚠ 安全阀的参照系**不能挂在 ω 上**。
        //   第一版写的是「AA 步长 ≤ κ × Picard 步长」，而 Picard 步长 = ω‖F‖；
        //   自适应 ω 在难工况下会一路压到下限 0.15 ⇒ 阀门跟着紧 6.7 倍，
        //   实测「管保温 1 mm」那档 **98 步被丢弃、只接受 4 步**，Anderson 等于没开。
        //   而 Anderson 本来就该迈**比欠松弛步大得多**的步 —— 那正是它的作用。
        //   ⇒ 参照系改成残差本身 ‖F‖（与 ω 无关），κ 才有稳定含义。
        double sF = Norm(f), sA = Dist(cand, x);
        if (!Finite(cand) || sA > kappa * Math.Max(sF, 1e-12))
        {
            // ⚠ 被阀门打掉**不等于**要清空历史。第一版一拒就 Restart，
            //   于是深度永远回到 0、下一步又只有 m=1 的病态子空间、又被打掉 ——
            //   自锁。实测 96 次拒绝对应 96 次重启，末端深度 0。
            //   ⇒ 只丢**最旧**的一条（缩小子空间），连续拒 3 次才真重启。
            Rejected++;
            _rejectRun++;
            if (_rejectRun >= 3) Restart();
            else if (_dX.Count > 0) { _dX.RemoveAt(0); _dF.RemoveAt(0); }
            return picard;
        }

        Accepted++; _rejectRun = 0; LastAccepted = true;
        return cand;
    }

    public void Restart()
    { _dX.Clear(); _dF.Clear(); _xPrev = null; _fPrev = null; _w = null; _rejectRun = 0; Restarts++; }

    public string Report() => $"Anderson：接受 {Accepted}／丢弃 {Rejected}／重启 {Restarts}，末端深度 {Depth}";

    // ── 小工具（维数 ≤ 10，不必上线性代数库）
    private static double Dot(double[] a, double[] b)
    { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

    /// <summary>加权内积：各分量先除以自己的标度，消掉 W 与 K 的量纲差。</summary>
    private static double DotW(double[] a, double[] b, double[] w)
    { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i] / (w[i] * w[i]); return s; }

    private static double Norm(double[] a)
    { double s = 0; foreach (var v in a) s += v * v; return Math.Sqrt(s); }

    private static double Dist(double[] a, double[] b)
    { double s = 0; for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; } return Math.Sqrt(s); }

    private static bool Finite(double[] a)
    { foreach (var v in a) if (double.IsNaN(v) || double.IsInfinity(v)) return false; return true; }

    /// <summary>对称正定小系统：带部分主元的高斯消元。奇异则返回 null（调用方退回 Picard）。</summary>
    private static double[]? SolveSym(double[,] A, double[] b, int m)
    {
        var M = new double[m, m + 1];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) M[i, j] = A[i, j];
            M[i, m] = b[i];
        }
        for (int k = 0; k < m; k++)
        {
            int piv = k;
            for (int i = k + 1; i < m; i++) if (Math.Abs(M[i, k]) > Math.Abs(M[piv, k])) piv = i;
            if (Math.Abs(M[piv, k]) < 1e-300) return null;
            if (piv != k) for (int j = k; j <= m; j++) (M[k, j], M[piv, j]) = (M[piv, j], M[k, j]);
            for (int i = k + 1; i < m; i++)
            {
                double fct = M[i, k] / M[k, k];
                if (fct == 0) continue;
                for (int j = k; j <= m; j++) M[i, j] -= fct * M[k, j];
            }
        }
        var x = new double[m];
        for (int i = m - 1; i >= 0; i--)
        {
            double s = M[i, m];
            for (int j = i + 1; j < m; j++) s -= M[i, j] * x[j];
            x[i] = s / M[i, i];
            if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) return null;
        }
        return x;
    }
}
