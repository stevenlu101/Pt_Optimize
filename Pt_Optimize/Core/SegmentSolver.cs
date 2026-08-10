using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

public sealed class SolveResult
{
    public bool Ok = true;
    public string Message = "";

    // 几何 / 质量
    public double WallDesignMm, WallElecMm, TubeAreaMm2, MassTubeKg, MassFlangePairKg, MassTotalKg;
    public bool WallLimitedByMinimum;

    // 电气
    public double CurrentA, VoltageV, ResistanceOhm, JActualAPerMm2, JMarginPct;
    public double PowerTotalW, LossPerMeterWPerM, PowerDensityWPerKg;

    // 温度场（管）
    public double[] X = Array.Empty<double>();
    public double[] TMetal = Array.Empty<double>();
    public double[] TGlass = Array.Empty<double>();
    public double TMinC, TMaxC, TFlangeAC, TFlangeBC, TGlassOutC;
    public double DevitMarginMinK;
    public bool DevitRisk;

    // 法兰（径向解，A = 上游端，B = 下游端）
    public FlangeRadialResult FlangeA = new(), FlangeB = new();
    public double FlangePhi, FlangeDeficitW, FlangeFloatTempC;
    public double FlangeAreaCm2, FlangeEquivTubeMm;

    // 特征量
    public double DecayLengthMm, TauMetalS, TauWithGlassS, StabilityRatio, TcrPerK, BetaWPerMK;
    public double OuterSurfaceTempC, InsulationOuterDiaMm;

    // 流动
    public double VelocityMmPerS, ResidenceS, PressureDropKPa, GlassHeadM;

    // 电解（仅直流分量）
    public bool DcMode;
    public double RGlassOhm, ILeakA, PtDissolvedGPerH, PtDissolvedKgPerYear;
    public double NaFluxGPerH, O2NlPerH;
}

public static class SegmentSolver
{
    public static SolveResult Solve(DesignInputs p)
    {
        var res = new SolveResult();
        try { Core(p, res); }
        catch (Exception ex) { res.Ok = false; res.Message = ex.Message; }
        return res;
    }

    /// <summary>
    /// **按给定电流求解**（跳过「二分电流使中点达设定温度」那一层）。
    ///
    /// 用于实测电流模式：现场钳表读到的电流是硬数据，比让模型去猜更可信，
    /// 而且省掉外层二分后单段求解从约 1 分钟降到秒级 —— 这是整线 UI 能做到
    /// 「改个参数马上看结果」的前提。壁厚不再反算（<c>SizeWall</c> 被忽略），
    /// 直接取 <see cref="DesignInputs.WallMinMm"/>。
    /// </summary>
    public static SolveResult SolveAtCurrent(DesignInputs p, double currentA)
    {
        var res = new SolveResult();
        try { Core(p, res, currentA); }
        catch (Exception ex) { res.Ok = false; res.Message = ex.Message; }
        return res;
    }

    private static void Core(DesignInputs p, SolveResult res, double? fixedCurrentA = null)
    {
        double ri = p.TubeId * 0.5, L = p.TubeLength, tAmb = p.TAmbC;

        // ── 外层：壁厚不动点迭代，使 J ≤ J_allow（J ∝ t^(−1/2)，一步精确）
        double wall = Math.Max(p.WallMinMm, 0.05) * 1e-3, wallElec = wall;
        double current = 0, area = 0;
        double[] tm = Array.Empty<double>(), tg = Array.Empty<double>();

        if (fixedCurrentA is double ifix)
        {
            // 电流已知：壁厚取实测值，只解一次温度场
            wall = p.WallMinMm * 1e-3;
            area = Math.PI * wall * (p.TubeId + wall);
            current = ifix;
            Profile(p, wall, area, current, out tm, out tg);
            wallElec = wall * Math.Pow(current / area / p.JAllow, 2);
        }
        else
        {
            bool wallConverged = false;
            for (int outer = 0; outer < 30; outer++)
            {
                area = Math.PI * wall * (p.TubeId + wall);
                current = FindCurrent(p, wall, area, out tm, out tg);
                double j = current / area;
                wallElec = wall * (j / p.JAllow) * (j / p.JAllow);
                double need = Math.Max(wallElec, p.WallMinMm * 1e-3);
                if (!p.SizeWall) { wall = p.WallMinMm * 1e-3; wallConverged = true; break; }
                if (Math.Abs(need - wall) / wall < 1e-3) { wall = need; wallConverged = true; break; }
                wall = 0.5 * wall + 0.5 * need;
            }
            if (!wallConverged)
                throw new InvalidOperationException(
                    $"壁厚外层迭代 30 次未收敛（最后 t = {wall * 1e3:F4} mm）。" +
                    "不返回未收敛结果——请检查输入参数。");
            area = Math.PI * wall * (p.TubeId + wall);
            current = FindCurrent(p, wall, area, out tm, out tg);
        }

        res.WallDesignMm = wall * 1e3;
        res.WallElecMm = wallElec * 1e3;
        res.WallLimitedByMinimum = wallElec * 1e3 < p.WallMinMm - 1e-6;
        res.TubeAreaMm2 = area * 1e6;

        int n = tm.Length;
        res.X = new double[n];
        for (int i = 0; i < n; i++) res.X[i] = i * L / (n - 1) * 1000.0;
        res.TMetal = tm; res.TGlass = tg;
        res.TFlangeAC = tm[0]; res.TFlangeBC = tm[n - 1];
        res.TGlassOutC = tg[n - 1];
        res.TMinC = double.MaxValue; res.TMaxC = double.MinValue;
        for (int i = 0; i < n; i++)
        { res.TMinC = Math.Min(res.TMinC, tm[i]); res.TMaxC = Math.Max(res.TMaxC, tm[i]); }
        res.DevitMarginMinK = res.TMinC - p.TLiquidusC;
        res.DevitRisk = res.DevitMarginMinK < p.DevitMarginK;

        // ── 电气
        double tMean = 0; for (int i = 0; i < n; i++) tMean += tm[i]; tMean /= n;
        double rho = Materials.PtResistivity(tMean);
        res.ResistanceOhm = rho * L / area;
        res.CurrentA = current;
        res.VoltageV = current * res.ResistanceOhm;
        res.JActualAPerMm2 = current / area * 1e-6;
        res.JMarginPct = (p.JAllowAPerMm2 - res.JActualAPerMm2) / p.JAllowAPerMm2 * 100.0;
        res.PowerTotalW = current * current * res.ResistanceOhm;
        res.LossPerMeterWPerM = res.PowerTotalW / L;
        res.PowerDensityWPerKg = rho * Math.Pow(current / area, 2) / Materials.PtDensity;

        // ── 法兰径向解（两端）
        var fluxTab = FlangeRadial.BuildFluxTable(p);
        res.FlangeA = FlangeRadial.Solve(p, current, tm[0], fluxTab);
        res.FlangeB = FlangeRadial.Solve(p, current, tm[n - 1], fluxTab);
        res.FlangePhi = res.FlangeA.PhiOverall;
        res.FlangeDeficitW = res.FlangeA.QRootW;
        res.FlangeFloatTempC = FlangeRadial.FloatTemp(p, current, fluxTab);

        double fri = p.FlangeRiMm * 1e-3, fro = p.FlangeRoMm * 1e-3;
        double faceArea = 2.0 * Math.PI * (fro * fro - fri * fri);
        res.FlangeAreaCm2 = faceArea * 1e4;
        res.FlangeEquivTubeMm = faceArea / (Math.PI * (p.TubeId + 2 * wall)) * 1000.0;
        res.MassTubeKg = Materials.PtDensity * area * L;
        res.MassFlangePairKg = res.FlangeA.MassKg + res.FlangeB.MassKg;
        res.MassTotalKg = res.MassTubeKg + res.MassFlangePairKg;

        // ── 特征量（切线斜率 + 玻璃耦合，见理论模型 §6.2.1 与 §7.1）
        double rOut = ri + wall;
        var lossAt = Insulation.CylinderLoss(p.TSetC, tAmb, rOut, p.Layers,
                        EffectiveEmissivity(p), p.Posture == Orientation.Vertical, L);
        res.OuterSurfaceTempC = lossAt.TOuterC;
        res.InsulationOuterDiaMm = lossAt.ROuter * 2000.0;

        var lossTab = TubeLossTable(p, rOut, L);
        double beta = lossTab.Slope(p.TSetC) + p.HGlass * Math.PI * p.TubeId;
        res.BetaWPerMK = beta;
        double kPt = Materials.PtThermalK(p.TSetC);
        res.DecayLengthMm = Math.Sqrt(kPt * area / Math.Max(1e-6, beta)) * 1000.0;

        double cMetal = Materials.PtDensity * area * Materials.PtCp(p.TSetC);
        double cGlass = p.GlassDensity * Math.PI * ri * ri * p.GlassCp;
        res.TauMetalS = cMetal / Math.Max(1e-6, beta);
        res.TauWithGlassS = (cMetal + cGlass) / Math.Max(1e-6, beta);

        res.TcrPerK = Materials.PtTcr(p.TSetC);
        double drhoDt = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * p.TSetC);
        double destab = current * current * drhoDt / area;
        res.StabilityRatio = destab > 1e-12 ? beta / destab : 999;

        // ── 流动
        double q = p.MassFlow / p.GlassDensity, aGlass = Math.PI * ri * ri;
        res.VelocityMmPerS = q / aGlass * 1000.0;
        res.ResidenceS = L / (q / aGlass);
        double dp = 128.0 * p.GlassViscosity * L * q / (Math.PI * Math.Pow(p.TubeId, 4));
        res.PressureDropKPa = dp * 1e-3;
        res.GlassHeadM = dp / (p.GlassDensity * 9.81);

        // ── 电解（交流下仅由触发不对称的直流分量驱动）
        res.DcMode = p.Supply == SupplyMode.Dc;
        res.RGlassOhm = p.GlassRho * L / aGlass;
        double iDc = res.DcMode ? current : current * p.DcOffsetPercent * 0.01;
        if (iDc > 0)
        {
            res.ILeakA = iDc * res.ResistanceOhm / (res.ResistanceOhm + res.RGlassOhm);
            double gPerC = Materials.PtMolarMass * 1000.0 / (p.PtValence * Materials.Faraday);
            res.PtDissolvedGPerH = res.ILeakA * gPerC * 3600.0;
            res.PtDissolvedKgPerYear = res.PtDissolvedGPerH * 8760.0 * 1e-3;
            res.NaFluxGPerH = res.ILeakA * (22.99 / Materials.Faraday) * 3600.0;
            res.O2NlPerH = res.ILeakA / (4.0 * Materials.Faraday) * 22.414 * 3600.0;
        }
    }

    private static double EffectiveEmissivity(DesignInputs p)
    {
        foreach (var l in p.Layers) if (l.Enabled && l.ThicknessMm > 1e-6) return p.OuterEmissivity;
        return p.PtEmissivity;
    }

    private static LossTable TubeLossTable(DesignInputs p, double rOut, double L)
        => new(p.TAmbC, Math.Max(p.TSetC, p.TGlassInC) + 400, 60,
               t => Insulation.CylinderLoss(t, p.TAmbC, rOut, p.Layers,
                        EffectiveEmissivity(p), p.Posture == Orientation.Vertical, L).QPerLength);

    /// <summary>Brent 法搜索电流，使管中点金属温度 = 设定值。</summary>
    private static double FindCurrent(DesignInputs p, double wall, double area,
                                      out double[] tm, out double[] tg)
    {
        // 搜索上界必须落在热稳定区内（理论模型 §7.4）：
        //   S = β·A / (I²·dρe/dT) > 1  ⇒  I_stab = √(β·A / (dρe/dT))
        // 越过 I_stab 后线性化系数失去对角占优，稳态解本就不存在，
        // 强行求解只会得到被数值夹断的假值。
        double rOut = p.TubeId * 0.5 + wall;
        var lossTab = TubeLossTable(p, rOut, p.TubeLength);
        double beta = lossTab.Slope(p.TSetC) + p.HGlass * Math.PI * p.TubeId;
        double drhoDt = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * p.TSetC);
        double iStab = Math.Sqrt(Math.Max(1e-9, beta * area / drhoDt));
        double iHi = 0.9 * iStab;

        double Residual(double i)
        {
            Profile(p, wall, area, i, out var t1, out _);
            return t1[t1.Length / 2] - p.TSetC;
        }

        if (Residual(iHi) < 0)
            throw new InvalidOperationException(
                $"在热稳定极限内无法达到设定温度：I_stab = {iStab:F0} A，" +
                $"该电流下中点仅 {Residual(iHi) + p.TSetC:F0} °C。" +
                "需降低散热、提高设定温度可行性，或改变几何。");

        // 残差对 I 单调递增，但每次求值要解一遍非线性 BVP，带迭代噪声 → 用二分
        double I = Roots.Monotone(Residual, 1, iHi, xTol: 0.05, maxIter: 60);

        Profile(p, wall, area, I, out tm, out tg);
        return I;
    }

    /// <summary>
    /// 金属—玻璃耦合稳态解。金属侧走 <see cref="Bvp1D"/> 内核，
    /// 玻璃侧为一阶对流方程（迎风隐式前进），二者用外层 Picard 耦合。
    /// </summary>
    public static void Profile(DesignInputs p, double wall, double area, double current,
                               out double[] tm, out double[] tg)
    {
        int n = Math.Max(21, p.Nodes | 1);
        double L = p.TubeLength, dx = L / (n - 1);
        double rOut = p.TubeId * 0.5 + wall;
        double kPt = Materials.PtThermalK(p.TSetC);
        double pi = Math.PI * p.TubeId, hg = p.HGlass, mcp = p.MassFlow * p.GlassCp;

        var lossTab = TubeLossTable(p, rOut, L);
        var fluxTab = FlangeRadial.BuildFluxTable(p);
        // 法兰缺口对管根温度的响应 D(T_root)，查表避免在迭代内反复解法兰
        // 法兰缺口 D(T_root)：耦合模式下由二维法兰模型给出定值，
        // 否则退回一维环形模型（该模型对 Pt_Heater.3dm 的圆盘+舌片几何不成立）
        // 用显式布尔判定，不看 D 的符号：Φ>1 时法兰向管子倒灌，D 为负是合法值。
        // 旧代码 `>=0` 会让那些算例静默回退到作废的一维模型（踩过，见 §7）。
        var defTab = p.FlangeDrawOverrideSet
            ? new LossTable(p.TAmbC, Math.Max(p.TSetC, p.TGlassInC) + 200, 8,
                            _ => p.FlangeDrawOverrideW)
            : new LossTable(p.TAmbC, Math.Max(p.TSetC, p.TGlassInC) + 200, 28,
                            t => FlangeRadial.Solve(p, current, t, fluxTab).QRootW);

        tm = new double[n]; tg = new double[n];
        for (int i = 0; i < n; i++) { tm[i] = p.TSetC; tg[i] = p.TGlassInC; }

        double KOf(double x) => kPt * area;
        var opt = new Bvp1D.Options { Nodes = n, Relax = 0.6, MaxIter = 40, Tol = 0.005 };

        for (int outer = 0; outer < 40; outer++)
        {
            // 玻璃：迎风隐式，沿 +x 单向前进
            tg[0] = p.TGlassInC;
            for (int i = 1; i < n; i++)
                tg[i] = (mcp * tg[i - 1] / dx + hg * pi * tm[i]) / (mcp / dx + hg * pi);

            var tgLocal = (double[])tg.Clone();
            double Src(double x, double T)
            {
                int i = Math.Clamp((int)Math.Round(x / dx), 0, n - 1);
                return current * current * Materials.PtResistivity(T) / area
                       - lossTab.Eval(T) - hg * pi * (T - tgLocal[i]);
            }
            double DSrc(double x, double T)
            {
                double drho = Materials.RhoRef *
                    (Materials.AlphaFit + 2 * Materials.BetaFit * T);
                return current * current * drho / area - lossTab.Slope(T) - hg * pi;
            }

            var bcL = Bvp1D.Boundary.WithFlux(defTab.Eval, defTab.Slope);
            var bcR = Bvp1D.Boundary.WithFlux(defTab.Eval, defTab.Slope);
            var tn = Bvp1D.Solve(0, L, KOf, Src, DSrc, bcL, bcR, init: tm, opt: opt);

            double err = 0;
            for (int i = 0; i < n; i++) err = Math.Max(err, Math.Abs(tn[i] - tm[i]));
            tm = tn;
            if (err < 0.01) break;
        }
    }

    // ---------------- 参数扫描 ----------------

    public sealed class SweepRow
    {
        public double Value, LossPerM, WallMm, MassKg, FlangeMassKg, TMin, Margin, Phi, IA;
    }

    public static List<SweepRow> Sweep(DesignInputs p, string what, double from, double to, int steps)
    {
        var rows = new List<SweepRow>();
        for (int i = 0; i < steps; i++)
        {
            double v = from + (to - from) * i / Math.Max(1, steps - 1);
            var q = Clone(p);
            switch (what)
            {
                case "insul": q.Layer1.ThicknessMm = v; q.Layer1.Enabled = v > 1e-6; break;
                case "flangeRo": q.FlangeRoMm = v; break;
                case "flangeTf": q.FlangeThickMm = v; q.FlangeThickInnerMm = v; break;
                case "flangeInsul": q.FlangeInsulThickMm = v; q.FlangeInsulated = v > 1e-6; break;
                case "eps": q.PtEmissivity = v; break;
                case "J": q.JAllowAPerMm2 = v; break;
            }
            var r = Solve(q);
            if (!r.Ok) continue;
            rows.Add(new SweepRow
            {
                Value = v,
                LossPerM = r.LossPerMeterWPerM,
                WallMm = r.WallDesignMm,
                MassKg = r.MassTotalKg,
                FlangeMassKg = r.MassFlangePairKg,
                TMin = r.TMinC,
                Margin = r.DevitMarginMinK,
                Phi = r.FlangePhi,
                IA = r.CurrentA
            });
        }
        return rows;
    }

    public static DesignInputs Clone(DesignInputs p)
        => System.Text.Json.JsonSerializer.Deserialize<DesignInputs>(
               System.Text.Json.JsonSerializer.Serialize(p))!;
}
