using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ★★★ R48 R（2026-09-17，Opus 5）：**舌板沿 x 的一维稳态反解** —— 「先定目标温场，再解出每段该包多厚保温」。
///
/// ══ 方程（与二维壳解同一套物性与散热配方，一个常数都不另写）
///
/// <code>
///   d/dx( k(T)·Ac·dT/dx ) + ρ(T)·I²/Ac − q″(T, t_ins(x))·P = 0        （自由段）
/// </code>
///   · k(T) = <see cref="Materials.PtThermalK"/>（W/m·K）　ρ(T) = <see cref="Materials.PtResistivity"/>（Ω·m）
///   · q″(T, t) = <see cref="DesignScreen.PlateFluxWPerM2"/>（W/m²，**唯一配方**，包不包按 <see cref="DesignScreen.FlangeFaceInsulated"/>）
///   · Ac = 舌宽 × 舌片厚（m²）　P = 2 × 舌宽（m，两个大面）
///
/// ══ 冷端边界：**跟着生产算例走，不另立模型**
///   生产算例（DesignSpec.BuildCase → LineCase.ClampTempC）给的是**夹持温度**，
///   <see cref="ShellThermal.ClampBoundaryOf"/> 判成 <see cref="ShellThermal.ClampBoundary.FixedTemp"/> ——
///   二维里整个压接段被**钉在夹头温度**上（实测：锚点那一次压接段面积加权均温 = 450.000 °C，逐位就是夹头温度）。
///   ⚠ 工单原写「夹头导热从 W08 探针的 191 W 反推」。**那是热导边界的写法，本算例不是热导边界**
///     （锚点那一次 FlangeOut.BusGWPerK = −1 = 没有走热导）。⇒ 一维照二维来：
///     定义域只取**自由段** [压接入口, 圆盘切点]，冷端 T(压接入口) = 夹头温度（定温）。
///     那 191 W 改作**对拍量**：一维解出来的「进压接段的热」加上压接段自身的焦耳热与散热，要对得上二维的 191.5 W。
///   （若将来算例改成热导边界，<see cref="Inputs.ClampConductanceWPerK"/> ≥ 0 时本类走分布热导那一支。）
///
/// ══ 热端边界（两个目标）
///   · T(切点) = 热偶基准 − 2.5 K − δ盘（δ盘 = 二维锚点的「管根 − 切点」温差，是**测量值**）
///   · Q_in(切点) = F(切点) = 管孔净流入目标 + 圆盘偏置（圆盘那一块一维不覆盖，偏置取二维锚点的 QGenDisc − QLossDisc）
///
/// ══ 为什么是「一维解族」而不是唯一解
///   二阶方程 + 两端定温 ⇒ 给定 t_ins(x) 就唯一确定 T(x) 与 Q_in(切点)。
///   于是「Q_in = 目标」**只有一个方程**，而剖面有很多自由度 ⇒ 反解出来的是一条**解族**。
///   本类把族参数取成沿自由段的**线性斜率** Δ = t冷端 − t热端：给定 Δ，二分解 t热端 命中目标。Δ = 0 就退化成现在那个单值旋钮。
///
/// ⚠ 这是**一维模型**，不是判据。判据永远由二维整线解（<see cref="LineRunner.Run"/>）给。
///   一维只用来**找剖面**；找出来的剖面必须装回二维再判一次。
/// </summary>
public static class TabReverse1D
{
    // ══════════════════════════════════════════════════════════════════════
    //  表面热流的缓存（只为跑得动；采样的是生产配方本身，不另写算式）
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ★ 一维模型自己的数值装置：把 <see cref="DesignScreen.PlateFluxWPerM2"/> 在 (温度, 厚度) 两个方向上采成表。
    /// · 温度方向：每个厚度一张 <see cref="LossTable"/>（与 <see cref="ShellThermal"/> 同一个类、同样的节点规则）；
    /// · 厚度方向：0.05…上界 按**几何**分布（相对间距 3 %），线性插值；厚度 &lt; 0.05 一律走裸铂那张（与 FlangeFaceInsulated 同一条规则）。
    /// 为什么要它：保温面的 <c>Insulation.PlateFlux</c> 每次都要迭代外表面温度，直调跑不动（一次反解要几十万次）。
    /// ⚠ 这只影响**一维模型自己的精度**（实测厚度方向插值误差 &lt; 0.05 %），二维判据一点都不经过这里。
    /// </summary>
    public sealed class FluxTable
    {
        private readonly DesignInputs _p;
        private readonly double[] _t;                 // 厚度节点 mm（几何分布）
        private readonly LossTable?[] _tab;           // 逐厚度的温度表（懒建）
        private readonly LossTable _bare;
        private readonly double _loC, _hiC;
        private readonly int _nodes;

        public FluxTable(DesignInputs p, double maxMm)
        {
            _p = p;
            _loC = p.TAmbC; _hiC = ShellThermal.LossTableHiC;
            _nodes = ShellThermal.LossTableNodes(_loC, _hiC);
            _bare = new LossTable(_loC, _hiC, _nodes, x => DesignScreen.PlateFluxWPerM2(p, x, 0.0));
            const double lo = 0.05;
            int n = Math.Max(20, (int)Math.Ceiling(Math.Log(maxMm / lo) / Math.Log(1.03)) + 1);
            _t = new double[n];
            for (int i = 0; i < n; i++) _t[i] = lo * Math.Pow(maxMm / lo, (double)i / (n - 1));
            _tab = new LossTable?[n];
        }

        private LossTable At(int i)
            => _tab[i] ??= new LossTable(_loC, _hiC, _nodes, x => DesignScreen.PlateFluxWPerM2(_p, x, _t[i]));

        public double Q(double tempC, double insulMm)
        {
            if (!DesignScreen.FlangeFaceInsulated(insulMm)) return _bare.Eval(tempC);
            if (insulMm >= _t[^1]) return At(_t.Length - 1).Eval(tempC);
            int k = Array.BinarySearch(_t, insulMm);
            if (k >= 0) return At(k).Eval(tempC);
            k = ~k;                                   // _t[k-1] < insulMm < _t[k]
            if (k <= 0) return At(0).Eval(tempC);
            double f = (insulMm - _t[k - 1]) / (_t[k] - _t[k - 1]);
            return At(k - 1).Eval(tempC) * (1 - f) + At(k).Eval(tempC) * f;
        }

        private static readonly ConcurrentDictionary<string, FluxTable> Cache = new();

        /// <summary>按 <see cref="DesignScreen.PlateFluxWPerM2"/> 真正读到的那几个字段做键 —— 别的字段变了不该重建，这几个变了必须重建。</summary>
        public static FluxTable For(DesignInputs p, double maxMm)
            => Cache.GetOrAdd($"{p.TAmbC}|{p.PtEmissivity}|{p.OuterEmissivity}|{p.ConvCharLenM}|{p.LossScale}|"
                            + $"{p.FlangeAirVelocityMPerS}|{p.Layer1.K0}|{p.Layer1.K1}|{maxMm}", _ => new FluxTable(p, maxMm));
    }

    public sealed class Inputs
    {
        /// <summary>物性／散热配方的唯一来源（整线算例的 Base 克隆）。</summary>
        public DesignInputs P = new();
        /// <summary>本片舌板载流 A。</summary>
        public double CurrentA;
        /// <summary>舌宽 mm（= 2 × 舌半宽）与舌片厚 mm。</summary>
        public double TabWidthMm, TabThickMm;
        /// <summary>热端 x（圆盘切点）、压接入口 x、舌尖 x，单位 mm。</summary>
        public double TangentXMm, ClampEntryXMm, TabTipXMm;
        /// <summary>夹头温度 °C（定温边界）；<see cref="ClampConductanceWPerK"/> ≥ 0 时改走分布热导。</summary>
        public double ClampColdEndC, ClampConductanceWPerK = -1;
        /// <summary>热端目标：温度 °C 与净流入 W。</summary>
        public double THotTargetC, QInHotTargetW;
        /// <summary>积分步长 mm（默认 0.25）。</summary>
        public double StepMm = 0.25;
        /// <summary>保温厚度上界 mm。</summary>
        public double MaxInsulMm = 20.0;
        /// <summary>
        /// ★ **表面散热等效系数**（默认 1 = 不校准）。一维是把二维那片板压成一根杆：
        /// 横向（沿 z）的温度不均、圆角、舌根加厚带都没了 ⇒ 等效散热面积与真实的差一截。
        /// 用二维锚点标定它（让一维在锚点剖面上复现二维的热端净流入），标定值必须随结果一起报。
        /// </summary>
        public double SurfaceScale = 1.0;

        public double FreeLenMm => TangentXMm - ClampEntryXMm;
        public double AcM2 => TabWidthMm * 1e-3 * TabThickMm * 1e-3;
        public double PerimM => 2.0 * TabWidthMm * 1e-3;
        public FluxTable Flux => FluxTable.For(P, MaxInsulMm);
        public double QSurf(double tempC, double insulMm) => SurfaceScale * Flux.Q(tempC, insulMm);
    }

    public sealed class Trace
    {
        public double[] XMm = Array.Empty<double>();
        public double[] TC = Array.Empty<double>();
        public double[] FluxW = Array.Empty<double>();      // F = k·Ac·T′，物理意义 = 沿 −x 输运的热 W
        public double[] InsulMm = Array.Empty<double>();
        public double THotC, QInHotW, QToClampW, QGenFreeW, QLossFreeW, TMaxC;
        /// <summary>积分中途跑飞：+1 = 冲上熔点以上（太热）、−1 = 掉到 −100 °C 以下（太冷）、0 = 正常。</summary>
        public int Runaway;
        /// <summary>打靶比较用的「热端温度」：跑飞时给 ±∞，这样二分永远有方向、不会被 NaN 卡住。</summary>
        public double HotForCompare => Runaway > 0 ? double.PositiveInfinity
                                     : Runaway < 0 ? double.NegativeInfinity
                                     : THotC;
    }

    /// <summary>
    /// 一次正向积分（RK4）：从**压接入口**（T = 夹头温度，F = fColdW）积到圆盘切点。
    /// fColdW = 自由段末端流进压接段的热 W（打靶量；与二维的 FlangeOut.QClampW 是同一个量）。
    /// </summary>
    public static Trace Forward(Inputs inp, Func<double, double> insulMmAt, double fColdW)
    {
        double x0 = inp.ClampEntryXMm * 1e-3, x1 = inp.TangentXMm * 1e-3;
        int n = Math.Max(20, (int)Math.Round((x1 - x0) / (inp.StepMm * 1e-3)));
        double h = (x1 - x0) / n;
        double ac = inp.AcM2, per = inp.PerimM, i2 = inp.CurrentA * inp.CurrentA;
        double gPerM = inp.ClampConductanceWPerK >= 0 && inp.TabTipXMm < inp.ClampEntryXMm
                     ? inp.ClampConductanceWPerK / ((inp.ClampEntryXMm - inp.TabTipXMm) * 1e-3) : 0.0;

        (double dT, double dF) D(double xM, double tC, double f)
            => (f / (Materials.PtThermalK(tC) * ac),
                -Materials.PtResistivity(tC) * i2 / ac + inp.QSurf(tC, insulMmAt(xM * 1e3)) * per);

        var xs = new double[n + 1]; var ts = new double[n + 1]; var fs = new double[n + 1]; var ins = new double[n + 1];
        double t = inp.ClampColdEndC, f = fColdW, gen = 0, loss = 0, tMax = double.NegativeInfinity;
        int runaway = 0;
        for (int k = 0; k <= n; k++)
        {
            double xM = x0 + k * h;
            xs[k] = xM * 1e3; ts[k] = t; fs[k] = f; ins[k] = insulMmAt(xs[k]);
            if (t > tMax) tMax = t;
            if (k == n) break;
            var k1 = D(xM, t, f);
            var k2 = D(xM + h / 2, t + h / 2 * k1.dT, f + h / 2 * k1.dF);
            var k3 = D(xM + h / 2, t + h / 2 * k2.dT, f + h / 2 * k2.dF);
            var k4 = D(xM + h, t + h * k3.dT, f + h * k3.dF);
            double gA = Materials.PtResistivity(t) * i2 / ac, lA = inp.QSurf(t, ins[k]) * per;
            double tN = t + h / 6 * (k1.dT + 2 * k2.dT + 2 * k3.dT + k4.dT);
            double fN = f + h / 6 * (k1.dF + 2 * k2.dF + 2 * k3.dF + k4.dF);
            double xN = xM + h;
            double gB = Materials.PtResistivity(tN) * i2 / ac, lB = inp.QSurf(tN, insulMmAt(xN * 1e3)) * per;
            gen += 0.5 * (gA + gB) * h; loss += 0.5 * (lA + lB) * h;
            t = tN; f = fN;
            // 跑飞：记方向，不当 NaN 吞掉 —— 打靶要靠这个方向才有得二分
            if (double.IsNaN(t) || double.IsInfinity(t) || t > Materials.PtMeltC * 2) { runaway = +1; break; }
            if (t < -100) { runaway = -1; break; }
        }
        return new Trace { XMm = xs, TC = ts, FluxW = fs, InsulMm = ins,
                           THotC = runaway == 0 ? ts[n] : double.NaN, QInHotW = runaway == 0 ? fs[n] : double.NaN,
                           QToClampW = fColdW, QGenFreeW = gen, QLossFreeW = loss, TMaxC = tMax, Runaway = runaway };
    }

    /// <summary>
    /// 打靶：解「进压接段的热」使 T(切点) = 热端目标温度。T(切点) 对它单调递增。
    /// 括号先**自己撑开**（从 0 往两边倍增），撑不开就照实说撑不开 —— 不许拿一个跑飞的解当结果。
    /// </summary>
    public static (Trace tr, bool ok, string note) ShootToHotTemp(Inputs inp, Func<double, double> insulMmAt,
                                                                  double tolW = 1e-7)
    {
        double tgt = inp.THotTargetC;
        Trace At(double f) => Forward(inp, insulMmAt, f);

        // ── 撑括号：下界要 T(切点) < 目标，上界要 > 目标
        double lo = 0, hi = 0;
        var trMid = At(0);
        bool lowOk = trMid.HotForCompare < tgt, hiOk = trMid.HotForCompare > tgt;
        if (lowOk) { lo = 0; }
        if (hiOk) { hi = 0; }
        for (double step = 25; step <= 1e6 && !lowOk; step *= 2)
        { var tr = At(-step); if (tr.HotForCompare < tgt) { lo = -step; lowOk = true; } }
        for (double step = 25; step <= 1e6 && !hiOk; step *= 2)
        { var tr = At(step); if (tr.HotForCompare > tgt) { hi = step; hiOk = true; } }
        if (!lowOk) return (trMid, false, $"进压接段的热往下撑到 −1e6 W，T(切点) 仍高于目标 {tgt:0.00} °C ⇒ 括号撑不开");
        if (!hiOk) return (trMid, false, $"进压接段的热往上撑到 +1e6 W，T(切点) 仍够不到目标 {tgt:0.00} °C（中途跑飞也算）⇒ 括号撑不开");
        if (hi <= lo) return (trMid, false, "括号方向不对（T(切点) 对「进压接段的热」不单调？）");

        for (int it = 0; it < 300 && hi - lo > tolW * Math.Max(1.0, Math.Abs(hi)); it++)
        {
            double mid = 0.5 * (lo + hi);
            if (At(mid).HotForCompare > tgt) hi = mid; else lo = mid;
        }
        var trOut = At(0.5 * (lo + hi));
        return (trOut, trOut.Runaway == 0 && !double.IsNaN(trOut.THotC), trOut.Runaway == 0 ? "" : "二分收敛处仍然跑飞");
    }

    /// <summary>沿自由段的线性剖面：热端 tHot、冷端 tHot + delta（两端之外钳住，与 <see cref="TabInsulProfile"/> 同规则）。</summary>
    public static Func<double, double> LinearRamp(Inputs inp, double tHotMm, double deltaMm)
        => xMm =>
        {
            double xi = (inp.TangentXMm - Math.Clamp(xMm, inp.ClampEntryXMm, inp.TangentXMm)) / Math.Max(1e-9, inp.FreeLenMm);
            return Math.Max(0.0, tHotMm + deltaMm * xi);
        };

    public sealed class Solution
    {
        public double DeltaMm, THotMm, TColdMm;
        public bool Ok;
        public string Note = "";
        public Trace Tr = new();
        public int Iterations;
        public double QInHotW, THotC;
        public double MeanMm => 0.5 * (THotMm + TColdMm);
    }

    /// <summary>
    /// **反解**：给定族参数 Δ（冷端 − 热端，mm），二分解热端厚度 tHot 使 <c>Q_in(切点) = 目标</c>
    /// （内层再打靶把 T(切点) 钉在目标温度）。保温越厚 ⇒ 表面散得越少 ⇒ 从管子那头吸的越少 ⇒ Q_in 单调下降。
    /// </summary>
    public static Solution SolveForDelta(Inputs inp, double deltaMm, double loMm = 0.0, double hiMm = 20.0, double tolMm = 1e-4)
    {
        var sol = new Solution { DeltaMm = deltaMm };
        double lo = Math.Max(loMm, Math.Max(0.0, -deltaMm)), hi = Math.Max(hiMm, lo + 1e-6);
        double QAt(double tHot)
        {
            var (tr, ok, _) = ShootToHotTemp(inp, LinearRamp(inp, tHot, deltaMm));
            return ok ? tr.QInHotW : double.NaN;
        }
        double qLo = QAt(lo), qHi = QAt(hi), tgt = inp.QInHotTargetW;
        if (double.IsNaN(qLo) || double.IsNaN(qHi))
        { sol.Note = $"端点算不出来（热端厚 {lo:0.00}／{hi:0.00} mm 处 Q_in = {qLo:0.###}／{qHi:0.###} W）"; return sol; }
        if ((qLo - tgt) * (qHi - tgt) > 0)
        {
            sol.Note = $"热端厚度 {lo:0.00}～{hi:0.00} mm 这一段够不到目标净流入 {tgt:0.###} W（两端 {qLo:0.###} → {qHi:0.###} W）⇒ **这个 Δ 上无解**";
            sol.THotMm = Math.Abs(qLo - tgt) < Math.Abs(qHi - tgt) ? lo : hi;
            sol.TColdMm = Math.Max(0, sol.THotMm + deltaMm);
            var trE = ShootToHotTemp(inp, LinearRamp(inp, sol.THotMm, deltaMm)).tr;
            sol.Tr = trE; sol.QInHotW = trE.QInHotW; sol.THotC = trE.THotC;
            return sol;
        }
        int it = 0;
        while (hi - lo > tolMm && it < 200)
        {
            double mid = 0.5 * (lo + hi);
            double qm = QAt(mid);
            if (double.IsNaN(qm)) break;
            if ((qLo - tgt) * (qm - tgt) <= 0) { hi = mid; qHi = qm; } else { lo = mid; qLo = qm; }
            it++;
        }
        sol.Iterations = it;
        sol.THotMm = 0.5 * (lo + hi);
        sol.TColdMm = Math.Max(0.0, sol.THotMm + deltaMm);
        var (tr2, ok2, note2) = ShootToHotTemp(inp, LinearRamp(inp, sol.THotMm, deltaMm));
        sol.Tr = tr2; sol.Ok = ok2 && Math.Abs(tr2.QInHotW - tgt) < 0.05; sol.Note = ok2 ? (sol.Ok ? "" : $"二分收敛到 {tr2.QInHotW:0.###} W，离目标 {tgt:0.###} W 还差 {tr2.QInHotW - tgt:+0.###;-0.###} W") : note2;
        sol.QInHotW = tr2.QInHotW; sol.THotC = tr2.THotC;
        return sol;
    }

    /// <summary>
    /// ★ **冷端恰好裸铂**的那一条（工单 T4 点名的选法）：剖面取 t(ξ) = t热端·(1 − ξ)，
    /// 冷端严格为 0（裸铂），只剩 t热端 一个未知数，由「Q_in(切点) = 目标」定。
    /// 为什么单列一支：按 Δ 扫描去碰 t冷端 = 0 会被扫描步长与二分下界（t热端 ≥ −Δ）挡住，
    ///   碰到的是 0.05 mm 这种「刚好还算包着」的值，不是真正的裸铂。这一支直接把 t冷端 钉成 0。
    /// </summary>
    public static Solution SolveColdBare(Inputs inp, double hiMm = 20.0, double tolMm = 1e-4)
    {
        Func<double, double> Ramp(double tHot) => xMm =>
        {
            double xi = (inp.TangentXMm - Math.Clamp(xMm, inp.ClampEntryXMm, inp.TangentXMm)) / Math.Max(1e-9, inp.FreeLenMm);
            return Math.Max(0.0, tHot * (1.0 - xi));
        };
        double QAt(double tHot)
        {
            var (tr, ok, _) = ShootToHotTemp(inp, Ramp(tHot));
            return ok ? tr.QInHotW : double.NaN;
        }
        var sol = new Solution();
        double lo = 0.0, hi = hiMm, tgt = inp.QInHotTargetW;
        double qLo = QAt(lo), qHi = QAt(hi);
        if (double.IsNaN(qLo) || double.IsNaN(qHi) || (qLo - tgt) * (qHi - tgt) > 0)
        {
            sol.Note = $"冷端裸铂这一支上，热端厚 {lo:0.00}～{hi:0.00} mm 够不到目标净流入 {tgt:0.###} W（两端 {qLo:0.###} → {qHi:0.###} W）⇒ 无解";
            return sol;
        }
        int it = 0;
        while (hi - lo > tolMm && it < 200)
        {
            double mid = 0.5 * (lo + hi);
            double qm = QAt(mid);
            if (double.IsNaN(qm)) break;
            if ((qLo - tgt) * (qm - tgt) <= 0) { hi = mid; qHi = qm; } else { lo = mid; qLo = qm; }
            it++;
        }
        sol.Iterations = it;
        sol.THotMm = 0.5 * (lo + hi);
        sol.TColdMm = 0.0;
        sol.DeltaMm = -sol.THotMm;
        var (tr2, ok2, note2) = ShootToHotTemp(inp, Ramp(sol.THotMm));
        sol.Tr = tr2; sol.QInHotW = tr2.QInHotW; sol.THotC = tr2.THotC;
        sol.Ok = ok2 && Math.Abs(tr2.QInHotW - tgt) < 0.05;
        sol.Note = ok2 ? (sol.Ok ? "" : $"二分收敛到 {tr2.QInHotW:0.###} W，离目标 {tgt:0.###} W 还差 {tr2.QInHotW - tgt:+0.###;-0.###} W") : note2;
        return sol;
    }

    /// <summary>
    /// ★ 标定：解 <see cref="Inputs.SurfaceScale"/>，使一维在**给定剖面**（通常 = 二维锚点那份单值）上
    /// 复现二维的热端净流入。热端与冷端温度都钉在二维量到的值上 ⇒ 这一条方程只有一个未知数。
    /// </summary>
    public static (double scale, bool ok, string note) CalibrateSurfaceScale(Inputs inp, Func<double, double> insulMmAt,
                                                                             double qInHot2DW, double lo = 0.2, double hi = 5.0)
    {
        double F(double s)
        {
            var c = Clone(inp); c.SurfaceScale = s;
            var (tr, ok, _) = ShootToHotTemp(c, insulMmAt);
            return ok ? tr.QInHotW - qInHot2DW : double.NaN;
        }
        double fLo = F(lo), fHi = F(hi);
        if (double.IsNaN(fLo) || double.IsNaN(fHi) || fLo * fHi > 0)
            return (1.0, false, $"散热等效系数 {lo:0.00}～{hi:0.00} 之间夹不住二维的热端净流入 {qInHot2DW:0.###} W（两端偏差 {fLo:0.###}／{fHi:0.###} W）");
        for (int i = 0; i < 100 && hi - lo > 1e-7; i++)
        {
            double mid = 0.5 * (lo + hi);
            double fm = F(mid);
            if (double.IsNaN(fm)) break;
            if (fLo * fm <= 0) { hi = mid; fHi = fm; } else { lo = mid; fLo = fm; }
        }
        return (0.5 * (lo + hi), true, "");
    }

    public static Inputs Clone(Inputs a) => new()
    {
        P = a.P, CurrentA = a.CurrentA, TabWidthMm = a.TabWidthMm, TabThickMm = a.TabThickMm,
        TangentXMm = a.TangentXMm, ClampEntryXMm = a.ClampEntryXMm, TabTipXMm = a.TabTipXMm,
        ClampColdEndC = a.ClampColdEndC, ClampConductanceWPerK = a.ClampConductanceWPerK,
        THotTargetC = a.THotTargetC, QInHotTargetW = a.QInHotTargetW,
        StepMm = a.StepMm, MaxInsulMm = a.MaxInsulMm, SurfaceScale = a.SurfaceScale,
    };

    /// <summary>
    /// ★ 交叉验证用的**另一条算法**：不打靶，直接对给定的温度场逐点算「要散多少」再反查厚度。
    /// q″_需要(x) = [ ρ(T)·I²/Ac + d/dx(k·Ac·T′) ] / P，再在 q″(T, t) 上对 t 二分（q″ 随 t 单调下降）。
    /// 裸铂都不够散 ⇒ 记 −1（不许悄悄钳住）。
    /// </summary>
    public static (double[] qReq, double[] tIns) InvertFromField(Inputs inp, double[] xMm, double[] tC)
    {
        int n = xMm.Length;
        var qReq = new double[n]; var tIns = new double[n];
        double ac = inp.AcM2, per = inp.PerimM, i2 = inp.CurrentA * inp.CurrentA;
        var f = new double[n];
        for (int k = 0; k < n; k++)
        {
            double d = k == 0 ? (tC[1] - tC[0]) / ((xMm[1] - xMm[0]) * 1e-3)
                     : k == n - 1 ? (tC[n - 1] - tC[n - 2]) / ((xMm[n - 1] - xMm[n - 2]) * 1e-3)
                     : (tC[k + 1] - tC[k - 1]) / ((xMm[k + 1] - xMm[k - 1]) * 1e-3);
            f[k] = Materials.PtThermalK(tC[k]) * ac * d;
        }
        for (int k = 0; k < n; k++)
        {
            double dFdx = k == 0 ? (f[1] - f[0]) / ((xMm[1] - xMm[0]) * 1e-3)
                        : k == n - 1 ? (f[n - 1] - f[n - 2]) / ((xMm[n - 1] - xMm[n - 2]) * 1e-3)
                        : (f[k + 1] - f[k - 1]) / ((xMm[k + 1] - xMm[k - 1]) * 1e-3);
            qReq[k] = (Materials.PtResistivity(tC[k]) * i2 / ac + dFdx) / per;
            tIns[k] = InvertFlux(inp, tC[k], qReq[k]);
        }
        return (qReq, tIns);
    }

    /// <summary>在 q″(T, t) 上对厚度二分。裸铂都散不了那么多 ⇒ 回 −1；比上界还少 ⇒ 回上界（调用方要写明钳住）。</summary>
    public static double InvertFlux(Inputs inp, double tC, double qWantWPerM2)
    {
        double qBare = inp.QSurf(tC, 0.0);
        if (qWantWPerM2 >= qBare) return -1;
        double qTop = inp.QSurf(tC, inp.MaxInsulMm);
        if (qWantWPerM2 <= qTop) return inp.MaxInsulMm;
        double lo = 0.05, hi = inp.MaxInsulMm;
        if (inp.QSurf(tC, lo) <= qWantWPerM2) return 0.0;
        for (int i = 0; i < 200 && hi - lo > 1e-6; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (inp.QSurf(tC, mid) > qWantWPerM2) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>把连续剖面落成 n 段常值（取每段中点的厚度，向图纸格 0.1 mm 取整）。</summary>
    public static TabInsulProfile Quantize(Inputs inp, Func<double, double> insulMmAt, int segs, double gridMm = 0.1)
    {
        var edges = TabInsulProfile.EvenEdges(inp.TangentXMm, inp.ClampEntryXMm, segs);
        var th = new double[segs];
        for (int i = 0; i < segs; i++)
        {
            double xm = 0.5 * (edges[i] + edges[i + 1]);
            th[i] = Math.Max(0.0, Math.Round(insulMmAt(xm) / gridMm) * gridMm);
        }
        return new TabInsulProfile(edges, th);
    }
}
