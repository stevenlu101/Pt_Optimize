using System;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 求解器验证套件。
///
/// 原则：不与「凑出来的期望值」比对。每一项都对照
///   (a) 用户提供的实测数据，
///   (b) 闭式解析解，
///   (c) 制造解法(MMS)构造的精确解，或
///   (d) 守恒律 / 推导本身的自洽性。
/// </summary>
public class VerificationTests
{
    private readonly ITestOutputHelper _o;
    public VerificationTests(ITestOutputHelper o) => _o = o;

    // =========================================================
    // 1. 材料数据：对照 17 个实测点
    // =========================================================

    /// <summary>用户提供的实测电阻率表（0–1500 °C，μΩ·cm）</summary>
    private static readonly double[] MeasT =
        { 0, 20, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 };
    private static readonly double[] MeasRho =
        { 9.83, 10.6, 13.7, 17.4, 21, 24.5, 27.9, 31.2, 34.3, 37.3,
          40.3, 43.1, 45.8, 48.3, 50.8, 53.2, 55.4 };

    [Fact]
    public void Resistivity_ReproducesMeasuredSSE()
    {
        double sse = 0;
        for (int i = 0; i < MeasT.Length; i++)
        {
            double model = Materials.PtResistivity(MeasT[i]) * 1e8;   // Ω·m → μΩ·cm
            double e = MeasRho[i] - model;
            sse += e * e;
            _o.WriteLine($"T={MeasT[i],6}  实测={MeasRho[i],6}  模型={model,9:F5}  残差={e,10:E3}");
        }
        _o.WriteLine($"SSE = {sse:E9}  (表格给出 1.1487668E-02)");

        // 与用户表格中的 SSE 一致
        Assert.InRange(sse, 0.011487668 - 1e-5, 0.011487668 + 1e-5);
    }

    [Fact]
    public void Tcr_MatchesAnalyticDerivativeOfFit()
    {
        // TCR 应等于拟合式的解析对数导数，用中心差分独立复核
        for (double T = 200; T <= 1400; T += 200)
        {
            double h = 1e-4;
            double num = (Materials.PtResistivity(T + h) - Materials.PtResistivity(T - h))
                         / (2 * h) / Materials.PtResistivity(T);
            double ana = Materials.PtTcr(T);
            Assert.InRange(Math.Abs(num - ana) / ana, 0, 1e-8);
        }
        _o.WriteLine($"TCR(1300 °C) = {Materials.PtTcr(1300):E4} /K");
        Assert.InRange(Materials.PtTcr(1300), 4.70e-4, 4.77e-4);
    }

    // =========================================================
    // 2. BVP 内核：制造解法(MMS)，验证离散化确实在解所写的方程
    // =========================================================

    [Fact]
    public void Bvp1D_MMS_ObservedOrderIsSecond()
    {
        // 精确解 T_e(s) = 300 + 200·sin(πs)，方程 K·T'' − c·T + f(s) = 0
        // ⇒ f(s) = c·T_e(s) − K·T_e''(s)
        const double K = 1.0, c = 50.0, L = 1.0;
        double Te(double s) => 300 + 200 * Math.Sin(Math.PI * s);
        double Tepp(double s) => -200 * Math.PI * Math.PI * Math.Sin(Math.PI * s);
        double f(double s) => c * Te(s) - K * Tepp(s);

        var opt0 = new Bvp1D.Options { Relax = 1.0, MaxIter = 500, Tol = 1e-11 };

        double L2(int n)
        {
            var opt = new Bvp1D.Options
            { Nodes = n, Relax = opt0.Relax, MaxIter = opt0.MaxIter, Tol = opt0.Tol };
            var T = Bvp1D.Solve(0, L, _ => K, (s, t) => f(s) - c * t, (_, _) => -c,
                Bvp1D.Boundary.Dirichlet(Te(0)), Bvp1D.Boundary.Dirichlet(Te(L)),
                init: null, opt: opt);
            double sum = 0; int m = T.Length; double ds = L / (m - 1);
            for (int i = 0; i < m; i++) { double e = T[i] - Te(i * ds); sum += e * e; }
            return Math.Sqrt(sum / m);
        }

        int[] grids = { 41, 81, 161, 321, 641 };
        var errs = new double[grids.Length];
        for (int i = 0; i < grids.Length; i++)
        {
            errs[i] = L2(grids[i]);
            _o.WriteLine($"n={grids[i],5}  L2 误差={errs[i]:E4}" +
                (i > 0 ? $"   观测阶={Math.Log2(errs[i - 1] / errs[i]):F3}" : ""));
        }

        for (int i = 1; i < grids.Length; i++)
        {
            double order = Math.Log2(errs[i - 1] / errs[i]);
            Assert.InRange(order, 1.9, 2.1);   // 理论二阶
        }
    }

    [Fact]
    public void Bvp1D_MatchesAnalyticFinSolution()
    {
        // K·T'' − β·T = 0, T(0)=θ0, T(L)=0
        // 解析解 θ(x) = θ0·sinh(m(L−x))/sinh(mL),  m = √(β/K)
        const double K = 2.0, beta = 800.0, L = 0.3, th0 = 500.0;
        double m = Math.Sqrt(beta / K);
        double Exact(double x) => th0 * Math.Sinh(m * (L - x)) / Math.Sinh(m * L);

        var T = Bvp1D.Solve(0, L, _ => K, (_, t) => -beta * t, (_, _) => -beta,
            Bvp1D.Boundary.Dirichlet(th0), Bvp1D.Boundary.Dirichlet(0),
            init: null,
            opt: new Bvp1D.Options { Nodes = 801, Relax = 1.0, MaxIter = 500, Tol = 1e-11 });

        int n = T.Length; double dx = L / (n - 1), maxRel = 0;
        for (int i = 0; i < n; i++)
        {
            double ex = Exact(i * dx);
            if (ex > 1e-3) maxRel = Math.Max(maxRel, Math.Abs(T[i] - ex) / ex);
        }
        _o.WriteLine($"衰减长度 1/m = {1000 / m:F2} mm，最大相对误差 = {maxRel:E3}");
        Assert.True(maxRel < 1e-3, $"最大相对误差 {maxRel:E3} 超过 0.1 %");
    }

    [Fact]
    public void Bvp1D_GlobalEnergyBalanceCloses()
    {
        // 两端绝热 + 内热源 + 对环境散热，稳态下 ∫q_gen 必须等于 ∫h(T−Ta)
        const double K = 5.0, L = 1.0, h = 30.0, Ta = 25.0;
        double Qgen(double s) => 1000.0 * (1 + 0.5 * Math.Cos(2 * Math.PI * s));

        var T = Bvp1D.Solve(0, L, _ => K,
            (s, t) => Qgen(s) - h * (t - Ta), (_, _) => -h,
            Bvp1D.Boundary.Adiabatic(), Bvp1D.Boundary.Adiabatic(),
            init: null,
            opt: new Bvp1D.Options { Nodes = 401, Relax = 1.0, MaxIter = 500, Tol = 1e-11 });

        int n = T.Length; double ds = L / (n - 1), gen = 0, loss = 0;
        for (int i = 0; i < n; i++)
        {
            double w = (i == 0 || i == n - 1) ? ds * 0.5 : ds;
            gen += Qgen(i * ds) * w;
            loss += h * (T[i] - Ta) * w;
        }
        double resid = Math.Abs(gen - loss) / gen;
        _o.WriteLine($"∫生成={gen:F6} W，∫散失={loss:F6} W，相对残差={resid:E3}");
        Assert.True(resid < 1e-9, $"能量不闭合，残差 {resid:E3}");
    }

    // =========================================================
    // 3. 法兰厚度渐变：验证推导本身
    // =========================================================

    private static DesignInputs BaseCase() => new()
    {
        TSetC = 1300,
        FlangeInsulated = false,
        HGlass = 0                    // 隔离玻璃回灌，纯考察法兰本体
    };

    // =========================================================
    // 4. 整段求解器：自洽性
    // =========================================================

    [Fact]
    public void Segment_CurrentSatisfiesSetpointAndJConstraint()
    {
        // ★ 必须是**设计模式**（SizeWall = true）：下面第二条断言检的是「外层壁厚迭代
        //   把 J 压到许用值以内」，而那层迭代只在设计模式下运行。
        //   默认值早已改成校核模式（SizeWall = false，壁厚钉在实测 1.0 mm），
        //   此时 J 由现实决定 —— 现默认工况算出 11.39 > 10，那是**设计结论**
        //   （散热模型高估，见 HANDOVER §4.2l），不是求解器不自洽。
        //   本项曾因此假失败：断言的前提没了，却被读成求解器坏了。
        var p = new DesignInputs { SizeWall = true };
        var r = SegmentSolver.Solve(p);
        Assert.True(r.Ok, r.Message);

        int mid = r.TMetal.Length / 2;
        _o.WriteLine($"中点温度={r.TMetal[mid]:F3} °C（设定 {p.TSetC}），" +
                     $"I={r.CurrentA:F1} A，J={r.JActualAPerMm2:F3} A/mm²，壁厚={r.WallDesignMm:F4} mm");

        // 中层迭代必须打中控温点
        Assert.InRange(r.TMetal[mid], p.TSetC - 0.5, p.TSetC + 0.5);
        // 外层迭代必须使 J 不超许用值
        Assert.True(r.JActualAPerMm2 <= p.JAllowAPerMm2 * 1.001,
            $"J={r.JActualAPerMm2} 超过许用 {p.JAllowAPerMm2}");
        // 壁厚不得低于最小可制造值
        Assert.True(r.WallDesignMm >= p.WallMinMm - 1e-9);
    }

    [Fact]
    public void Segment_PowerEqualsOhmicAndMassEqualsMassEquation()
    {
        // P = I²R 与质量方程 m = d·P/(ρe·J²) 必须自洽
        var p = new DesignInputs();
        var r = SegmentSolver.Solve(p);

        double pOhmic = r.CurrentA * r.CurrentA * r.ResistanceOhm;
        Assert.InRange(Math.Abs(pOhmic - r.PowerTotalW) / r.PowerTotalW, 0, 1e-9);

        // 用平均温度的 ρe 反算质量，应回到几何质量
        double tMean = 0; foreach (var t in r.TMetal) tMean += t;
        tMean /= r.TMetal.Length;
        double rho = Materials.PtResistivity(tMean);
        double j = r.JActualAPerMm2 * 1e6;
        double mFromEq = Materials.PtDensity * r.PowerTotalW / (rho * j * j);
        _o.WriteLine($"几何质量={r.MassTubeKg:F5} kg，质量方程={mFromEq:F5} kg，" +
                     $"相对差={Math.Abs(mFromEq - r.MassTubeKg) / r.MassTubeKg:E3}");
        Assert.True(Math.Abs(mFromEq - r.MassTubeKg) / r.MassTubeKg < 1e-6);
    }

    [Fact]
    public void Segment_GridConvergence()
    {
        // 网格加密下最冷点温度必须收敛
        // 网格三联必须落在「差值 ≫ 求解器容差」的区间，
        // 否则 Richardson 外推测的是迭代噪声而不是离散误差。
        var vals = new double[3];
        int[] grids = { 51, 101, 201 };
        for (int i = 0; i < 3; i++)
        {
            var p = new DesignInputs { Nodes = grids[i] };
            var r = SegmentSolver.Solve(p);
            vals[i] = r.TMinC;
            _o.WriteLine($"n={grids[i],4}  T_min={vals[i]:F4}  T端={r.TMetal[0]:F2}/{r.TMetal[^1]:F2}  " +
                         $"T中={r.TMetal[r.TMetal.Length / 2]:F2}  I={r.CurrentA:F1} A  " +
                         $"壁厚={r.WallDesignMm:F4}");
        }
        // 标准网格收敛判据（GCI）：观测阶 + Richardson 外推误差，
        // 而不是对最细网格差值随手定一个绝对阈值。
        double d1 = vals[1] - vals[0], d2 = vals[2] - vals[1];
        double order = Math.Log2(Math.Abs(d1) / Math.Abs(d2));
        double exact = vals[2] + d2 / (Math.Pow(2, order) - 1);   // Richardson 外推
        double err401 = Math.Abs(vals[2] - exact);
        double err201 = Math.Abs(vals[1] - exact);

        _o.WriteLine($"Δ1={d1:F4}  Δ2={d2:F4}  观测阶={order:F3}");
        _o.WriteLine($"外推真值={exact:F4} °C   n=201 误差={err201:F3} K   n=401 误差={err401:F3} K");

        const double PicardTolK = 0.01;           // SegmentSolver.Profile 的内层容差
        if (Math.Abs(d2) < 10 * PicardTolK)
        {
            _o.WriteLine($"⚠ |Δ2| = {Math.Abs(d2):F4} K 已接近求解器容差 {PicardTolK} K，" +
                         "阶数估计不可靠，改为只检验离散误差");
        }
        else
        {
            Assert.InRange(order, 1.7, 2.3);      // 方案应为二阶
        }
        Assert.True(err401 < 1.0, $"最细网格上离散误差 {err401:F3} K 超过 1 K");
    }

    // =========================================================
    // 5. 保温模型：解析对照
    // =========================================================

    [Fact]
    public void CylinderConduction_MatchesAnalyticLogLaw()
    {
        // 单层、常导热系数、外表面强制在给定温度时，
        // q' 必须等于 2πk(T1−T2)/ln(r2/r1)
        const double k = 0.5, r1 = 0.020, thick = 0.030;
        double r2 = r1 + thick;

        // 用极高的外表面换热系数逼近「外表面 = 环境温度」
        var layer = new InsulationLayer { Name = "test", ThicknessMm = thick * 1000, K0 = k, K1 = 0 };
        var res = Insulation.CylinderLoss(1000, 25, r1, new[] { layer },
                    epsOuter: 0.9, vertical: false, verticalLength: 1, lossScale: 1.0);

        double analytic = 2 * Math.PI * k * (1000 - res.TOuterC) / Math.Log(r2 / r1);
        _o.WriteLine($"外表面温度={res.TOuterC:F3} °C，数值 q'={res.QPerLength:F4} W/m，" +
                     $"解析 q'={analytic:F4} W/m，相对差={Math.Abs(res.QPerLength - analytic) / analytic:E3}");
        Assert.True(Math.Abs(res.QPerLength - analytic) / analytic < 1e-6);
    }

    [Fact]
    public void LinearK_IntegralMeanEqualsMeanTemperatureValue()
    {
        // 理论模型 §6.1.1：对线性 k(T)，积分平均恒等于平均温度处的 k 值
        var layer = new InsulationLayer { K0 = 0.04, K1 = 3.0e-4 };
        double t1 = 200, t2 = 1200;
        const int N = 2000001;
        double dt = (t2 - t1) / (N - 1), integ = 0;
        for (int i = 0; i < N; i++)
        {
            double w = (i == 0 || i == N - 1) ? dt * 0.5 : dt;
            integ += layer.KAt(t1 + i * dt) * w;
        }
        double mean = integ / (t2 - t1);
        double atMean = layer.KAt(0.5 * (t1 + t2));
        _o.WriteLine($"积分平均={mean:F9}，均温处取值={atMean:F9}，差={Math.Abs(mean - atMean):E3}");
        Assert.True(Math.Abs(mean - atMean) < 1e-9);
    }

    [Fact]
    public void LossScale_ScalesHeatFlowExactly_AndLeavesInterfaceTemperaturesFixed()
    {
        // HANDOVER §4.2l 的标定系数必须是**纯倍率**：
        // 层导热与表面换热同倍缩放 ⇒ 稳态下各界面温度不变、热流严格 ×scale。
        // 若只缩表面（纤维热阻主导时几乎无效）或只缩最终结果（内外能量不闭合），本项即挂。
        var layers = new[]
        {
            new InsulationLayer { Name = "纤维", ThicknessMm = 2.5, K0 = 0.04, K1 = 3.0e-4 },
            new InsulationLayer { Name = "致密", ThicknessMm = 5.0, K0 = 25.0, K1 = -0.016 }
        };
        double r1 = 0.026;

        var b = Insulation.CylinderLoss(1150, 25, r1, layers, 0.45, false, 0.3, lossScale: 1.0);
        foreach (double s in new[] { 0.25, 0.5, 2.0 })
        {
            var c = Insulation.CylinderLoss(1150, 25, r1, layers, 0.45, false, 0.3, lossScale: s);
            double qRatio = c.QPerLength / b.QPerLength;
            double dTOut = Math.Abs(c.TOuterC - b.TOuterC);
            _o.WriteLine($"scale={s:0.00}: q'={c.QPerLength:F4} W/m，q 比值={qRatio:F6}，" +
                         $"外表面温度 {c.TOuterC:F4} vs {b.TOuterC:F4} °C（差 {dTOut:E2} K）");
            Assert.True(Math.Abs(qRatio - s) / s < 1e-4, $"scale={s} 时热流比值为 {qRatio}");
            Assert.True(dTOut < 1e-3, $"scale={s} 时外表面温度变了 {dTOut} K");
        }

        // 平板（法兰面）同口径
        double f1 = Insulation.PlateFlux(1050, 25, layers, 0.45, 0.05, lossScale: 1.0);
        double f2 = Insulation.PlateFlux(1050, 25, layers, 0.45, 0.05, lossScale: 0.4);
        _o.WriteLine($"平板：q″ {f1:F1} → {f2:F1} W/m²，比值={f2 / f1:F6}");
        Assert.True(Math.Abs(f2 / f1 - 0.4) / 0.4 < 1e-4);
    }

    // =========================================================
    // 6. 辐射线性化：切线 / 割线之比
    // =========================================================

    [Fact]
    public void RadiationTangentToSecantRatio_ApproachesFour()
    {
        // 理论模型 §6.2.1：dq″/dT 与 q″/ΔT 之比在 Ta ≪ Ts 时趋于 4
        const double eps = 0.18, Ta = 25;
        double Ts = 1300;
        double secant = Materials.HRad(eps, Ts, Ta);
        double tangent = 4 * eps * Materials.Sigma * Math.Pow(Ts + 273.15, 3);
        double ratio = tangent / secant;
        _o.WriteLine($"Ts=1300 °C: 割线={secant:F3}，切线={tangent:F3}，比值={ratio:F4}");
        Assert.InRange(ratio, 3.24, 3.26);   // 手算 3.25

        // 极限检验：Ta → 0 K 时比值 → 4
        double s0 = eps * Materials.Sigma * (Math.Pow(Ts + 273.15, 4) - 0) / (Ts + 273.15);
        Assert.InRange(tangent / s0, 3.999, 4.001);
    }
}
