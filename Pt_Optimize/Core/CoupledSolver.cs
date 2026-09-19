using System;

namespace PtOptimize.Core;

/// <summary>
/// 一维管段 ↔ 二维法兰 的耦合求解。
///
/// 外层不动点迭代：
///   ① 用当前法兰抽热 D 作管端边界条件，解一维管段 → 电流 I、管根温度 T_root
///   ② 用 I、T_root 解二维法兰 → 新的抽热 D' = loss − gen（能量恒等式，精确）
///   ③ 欠松弛更新 D ← D + ω(D' − D)，回 ①，直到 |ΔD| 收敛
///
/// 二维电流场的形状与 I 无关（Laplace 线性），故只解一次，
/// 之后按 J ∝ I 整体定标，避免在迭代内重复求解。
/// </summary>
public sealed class CoupledResult
{
    public SolveResult Tube = new();
    public PlateThermalResult Flange = new();
    public PlateField Current = new();
    public double FlangeDrawW;
    public int OuterIterations;
    public double OuterDelta;
    public bool Converged;
    public double JTubeAPerMm2, JFlangeMaxAPerMm2;
    /// <summary>单片法兰质量 g。整线汇总要用它 —— n 段共 n+1 片，不是 2n 片。</summary>
    public double MassPlateG;
    public double MassTubeG, MassFlangePairG, MassTotalG;  // MassFlangePairG = 本段两端共 2 片
    public double FlangeThickMm;          // 定尺后的法兰厚度
    public int ThicknessIterations;
    public string Note = "";
}

public static class CoupledSolver
{
    /// <summary>求解进度（本解算按分钟计，UI 需要据此显示进度）</summary>
    public sealed class Progress
    {
        public int ThicknessIter, ThicknessTotal, OuterIter, OuterTotal;
        public override string ToString()
            => $"厚度定尺 {ThicknessIter}/{ThicknessTotal} · 外层 {OuterIter}/{OuterTotal}";
    }

    public static CoupledResult Solve(DesignInputs p, FlangePlate g,
                                      double h = 1.0, int maxOuter = 8, double tolW = 2.0,
                                      IProgress<Progress>? progress = null,
                                      CancellationToken cancel = default)
    {
        var res = new CoupledResult();

        // ── 法兰厚度定尺外层：J ∝ 1/t（均匀缩放板厚时电流分布形状不变，K=J·t 守恒）
        //    故 t_req = t·(J_max/J_allow) 是一步精确解，只因热场随 t 变化才需迭代。
        // 法兰厚度不再由本类反算 —— 它由 FlangeAutoSizer 按管根温差定（HANDOVER §4.2w）。
        const int tkTotal = 1;
        for (int tk = 0; tk < tkTotal; tk++)
        {
            cancel.ThrowIfCancellationRequested();
            var pr = new Progress { ThicknessIter = tk + 1, ThicknessTotal = tkTotal, OuterTotal = maxOuter };
            var probe = SolveOnce(p, g, h, maxOuter, tolW, progress, pr, cancel);
            if (!probe.Tube.Ok)
            { probe.FlangeThickMm = g.ThicknessMm; probe.ThicknessIterations = tk; return probe; }
            double tNew = g.ThicknessMm * (probe.JFlangeMaxAPerMm2 / p.JAllowAPerMm2);
            tNew = Math.Max(0.3, tNew);
            if (Math.Abs(tNew - g.ThicknessMm) / g.ThicknessMm < 2e-3)
            { probe.FlangeThickMm = g.ThicknessMm; probe.ThicknessIterations = tk; return probe; }
            g.ThicknessMm = 0.5 * g.ThicknessMm + 0.5 * tNew;
            if (g.ThickenedMm < g.ThicknessMm) g.ThickenedMm = g.ThicknessMm;
        }
        var last = SolveOnce(p, g, h, maxOuter, tolW, progress,
                             new Progress { ThicknessIter = tkTotal, ThicknessTotal = tkTotal, OuterTotal = maxOuter },
                             cancel);
        last.FlangeThickMm = g.ThicknessMm; last.ThicknessIterations = 5;
        return last;
    }

    private static CoupledResult SolveOnce(DesignInputs p, FlangePlate g,
                                           double h, int maxOuter, double tolW,
                                           IProgress<Progress>? progress = null,
                                           Progress? pr = null,
                                           CancellationToken cancel = default)
    {
        var res = new CoupledResult();

        // ── 二维电流场：形状只解一次（Laplace 线性，与 I 无关）
        double aTubeMm2 = Math.PI * (Math.Pow(p.TubeIdMm * 0.5 + p.WallMinMm, 2)
                                     - Math.Pow(p.TubeIdMm * 0.5, 2));
        // 电流场首解用等温 σ；温度场出来后按 σ(T) 重解，二者互相迭代
        var curRef = PlateCurrent2D.Solve(g, 1000.0, Materials.PtResistivity(p.TSetC), h);
        res.Current = curRef;
        double[,]? tField = null;

        double D = 200.0;           // 法兰抽热初值 W
        double lastD = D;
        int it = 0;

        for (; it < maxOuter; it++)
        {
            cancel.ThrowIfCancellationRequested();
            if (pr is not null && progress is not null)
            { pr.OuterIter = it + 1; progress.Report(pr); }

            // ① 一维管段：两端各挂 D 瓦的定值抽热
            p.FlangeDrawOverrideW = D; p.FlangeDrawOverrideSet = true;
            var tube = SegmentSolver.Solve(p);
            if (!tube.Ok) { res.Note = tube.Message; res.Tube = tube; return res; }
            res.Tube = tube;

            // ② 二维法兰：按实际电流定标 J，再解温度场
            double I = tube.CurrentA;
            var cur = ScaleCurrent(curRef, I / 1000.0);
            // ★ R48（2026-09-14，Opus 5）：PlateThermal2D 读 p.FlangeInsulThickMm ⇒ 按本片圆盘保温取（FlangePlate.DiscInsulEffectiveMm，
            //   与 LineRunner 同一口径）再克隆 p 给它；板件没带逐片值时取回的就是 p 的整线值，数与原来相同。
            var pTh = SegmentSolver.Clone(p);
            pTh.FlangeInsulThickMm = g.DiscInsulEffectiveMm(p);
            var th = PlateThermal2D.Solve(g, cur, pTh, tRootC: tube.TFlangeAC, maxIter: 120000, tol: 1e-5);
            res.Flange = th;

            // 用新温度场按 σ(T) 重解电流场（铂 700–1300 °C 间 ρe 变化 48 %，
            // 冷区更导电会把电流拉过去，等温 σ 会算偏 J 分布）
            tField = th.T;
            curRef = PlateCurrent2D.Solve(g, 1000.0, Materials.PtResistivity(p.TSetC), h,
                                          tempField: tField, tRefC: p.TSetC);
            res.Current = curRef;
            res.JFlangeMaxAPerMm2 = cur.JMaxAPerMm2;
            res.JTubeAPerMm2 = I / aTubeMm2;

            double Dnew = th.QFromTubeW;
            res.OuterDelta = Math.Abs(Dnew - D);
            lastD = D;
            D = D + 0.6 * (Dnew - D);          // 欠松弛
            // ⚠⚠ **这条收敛判据是旧口径**（2026-08-28 普查标注，故意不改）：
            //   它拿「相邻两轮之差」直接比容差，**没乘不动点放大** 1/(1−g)。
            //   对慢收敛的不动点迭代，到真解的距离 ≈ δ×g/(1−g)，可以比 δ 大一两个量级。
            //   LineRunner 的主环是对的（δ×放大、且真残差×放大都要过），
            //   见 LineCase.FixedPointAmp 与 DesignInputs.BaselineTolAmplified。
            //
            // 为什么**不改**：本类**已被取代**（LineRunner 那条判据路根本不调它，
            //   见 LineRunner 里「已被取代的 CoupledSolver」那条注释）。
            //   它现在只服务 LineSolver 与几条 CLI 诊断。
            //   在这里换口径既动不了交付数，又会改掉那些诊断的历史值 —— 净亏。
            // ⇒ 但**必须留字**：谁把它接回判据路，就等于把旧口径也接回去了。
            //   `CoupledSolverStaysOffJudgePathTests` 盯着这件事。
            if (res.OuterDelta < tolW) { res.Converged = true; it++; break; }
        }
        _ = lastD;
        res.OuterIterations = it;
        res.FlangeDrawW = D;

        // 质量（用真实几何，不用一维环形模型）。
        // 单段两端各一片 —— 与 Pt_Heater.3dm 的两个法兰实体一致（--geom 已校核）。
        // 注意：整线不是「段数 × 2」，相邻段共用接头处那一片，n 段共 n+1 片，见 LineSolver。
        res.MassTubeG = aTubeMm2 * p.TubeLengthMm * Materials.PtDensity * 1e-6;
        // 厚度可能分区（圆盘 / 舌片 / 孔周加厚），故按**体积积分**，不能用「面积 × 单一厚度」
        res.MassPlateG = PlateVolumeMm3(g) * Materials.PtDensity * 1e-6;
        res.MassFlangePairG = 2 * res.MassPlateG;
        res.MassTotalG = res.MassTubeG + res.MassFlangePairG;
        return res;
    }

    /// <summary>|J| 与 I 成正比（Laplace 线性），故整体缩放即可，不必重解。</summary>
    private static PlateField ScaleCurrent(PlateField src, double k)
    {
        int nx = src.Nx, nz = src.Nz;
        var J = new double[nx, nz];
        double jmax = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                J[i, j] = src.Jmag[i, j] * k;
                if (J[i, j] > jmax) jmax = J[i, j];
            }
        return new PlateField
        {
            Mask = src.Mask, V = src.V, Jmag = J,
            X0 = src.X0, Z0 = src.Z0, H = src.H, Nx = nx, Nz = nz,
            JMaxAPerMm2 = jmax, JMeanAPerMm2 = src.JMeanAPerMm2 * k,
            CurrentInA = src.CurrentInA * k, CurrentOutA = src.CurrentOutA * k,
            ConservationError = src.ConservationError
        };
    }

    /// <summary>
    /// 法兰实体体积 mm³（沿 x 数值积分「宽度 × 当地厚度」）。
    /// 厚度分区后（圆盘/舌片各自厚度、孔周加厚）必须用它算质量，
    /// 「PlateArea × ThicknessMm」只在全片等厚时才成立。
    /// </summary>
    public static double PlateVolumeMm3(FlangePlate g, int n = 20001)
    {
        double x0 = g.TabTipXMm, x1 = g.DiscRadiusMm, dx = (x1 - x0) / (n - 1), v = 0;
        for (int i = 0; i < n; i++)
        {
            double x = x0 + i * dx, w = dx * (i == 0 || i == n - 1 ? 0.5 : 1.0);
            double hole = Math.Abs(x) <= g.HoleRadiusMm
                ? Math.Sqrt(g.HoleRadiusMm * g.HoleRadiusMm - x * x) : 0;
            double half = g.HalfWidth(x);
            if (half <= hole) continue;
            // 沿 z 方向厚度可能变（孔周加厚是圆形区域），故按 z 再积一层，取 41 点足够
            const int nz = 41;
            double dz = (half - hole) / (nz - 1), s = 0;
            for (int k = 0; k < nz; k++)
            {
                double z = hole + k * dz;
                s += g.ThicknessAt(x, z) * dz * (k == 0 || k == nz - 1 ? 0.5 : 1.0);
            }
            v += 2 * s * w;      // 上下对称
        }
        return v;
    }

    /// <summary>法兰平面净面积（数值积分宽度分布）</summary>
    public static double PlateArea(FlangePlate g, int n = 200001)
    {
        double x0 = g.TabTipXMm, x1 = g.DiscRadiusMm, dx = (x1 - x0) / (n - 1), a = 0;
        for (int i = 0; i < n; i++)
        {
            double x = x0 + i * dx, w = dx * (i == 0 || i == n - 1 ? 0.5 : 1.0);
            double hole = Math.Abs(x) <= g.HoleRadiusMm
                ? Math.Sqrt(g.HoleRadiusMm * g.HoleRadiusMm - x * x) : 0;
            a += 2 * (g.HalfWidth(x) - hole) * w;
        }
        return a;
    }
}
