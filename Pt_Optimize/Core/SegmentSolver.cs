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

    // ★ 2026-08-12：法兰的一维环形解（FlangeA/B、FlangePhi、FlangeDeficitW、
    //   FlangeFloatTempC、FlangeAreaCm2、FlangeEquivTubeMm）随 FlangeRadial 一并删除。
    //   法兰的真值一律由 ShellCurrent + ShellThermal 给出，见 LineRunner.FlangeOut。

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

        // 法兰质量不再由本求解器给出 —— 它取决于 .3dm/FlangePlate 的真实几何，
        // 由 LineRunner 按壳网格体积算（见 FlangeOut.MassG）。这里只报管本身。
        res.MassTubeKg = Materials.PtDensity * area * L;
        res.MassFlangePairKg = 0;
        res.MassTotalKg = res.MassTubeKg;

        // ── 特征量（切线斜率 + 玻璃耦合，见理论模型 §4.4「辐射线性化」与 §7.1「为什么必须是串级」）
        double rOut = ri + wall;
        var lossAt = Insulation.CylinderLoss(p.TSetC, tAmb, rOut, p.Layers,
                        EffectiveEmissivity(p), p.Posture == Orientation.Vertical, L, p.LossScale);
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
                        EffectiveEmissivity(p), p.Posture == Orientation.Vertical, L,
                        p.LossScale).QPerLength);

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

        // ── 端部额外保温（轴向不均匀）。Src/DSrc 本来就带 x，所以只需按 x 选表。
        //    实测冷坑只在两端各约 30 mm（ℓt≈22 mm），中段 ±1.4 K ⇒ 只在端部换表。
        //    机理：管按同一电流均匀自发热，稳态下 q_joule = β(T_set − T_amb)；
        //    端部把 β 压小而发热不变 ⇒ **净剩余热量填坑**。
        //    ⚠ 与「整体加厚保温」方向相反：整体加厚使 ℓt 变长、坑更深（ΔT=D/√(kAβ)）。
        // ★★ 补偿必须**渐变**，不能是台阶。
        //   第一版做成「端部 30 mm 统一加厚」，实测：冷坑确实被填（最低管温 1034→1049.5），
        //   但那 30 mm 变成了**热包**（最高 1150→1192.7），段内落差只从 17.4 降到 14.8。
        //   原因很简单：**坑是 exp(−x/ℓt) 形状的，用台阶去补必然过补一段、欠补一段。**
        //   ⇒ 把额外厚度按同样的指数形状分级（这里用 6 个子区间离散）。
        const int nz = 6;
        var endTabs = new LossTable[nz];
        double endLen = 0;
        if (p.EndInsulExtraMm > 1e-6 && p.EndInsulLengthMm > 1e-6)
        {
            endLen = p.EndInsulLengthMm * 1e-3;
            // 热扩散长度：ℓt = √(kA/β)，坑按 exp(−x/ℓt) 衰减 ⇒ 补偿同形
            double betaU = lossTab.Slope(p.TSetC) + p.HGlass * Math.PI * p.TubeId;
            double lt = Math.Sqrt(Math.Max(1e-12, kPt * area / Math.Max(1e-12, betaU)));
            for (int z = 0; z < nz; z++)
            {
                double xm = (z + 0.5) / nz * endLen;              // 子区间中点距端部
                var pz = Clone(p);
                pz.Layer1.ThicknessMm += p.EndInsulExtraMm * Math.Exp(-xm / lt);
                pz.Layer1.Enabled = true;
                endTabs[z] = TubeLossTable(pz, rOut, L);
            }
        }
        LossTable TabAt(double x)
        {
            if (endLen <= 0) return lossTab;
            double d = Math.Min(x, L - x);                        // 到最近端部的距离
            if (d >= endLen) return lossTab;
            int z = Math.Clamp((int)(d / endLen * nz), 0, nz - 1);
            return endTabs[z];
        }

        // 法兰从管根抽走的热 D。**只有两种情况**：
        //   · FlangeDrawOverrideSet = true  → 由二维壳解回灌的真值（LineRunner/CoupledSolver 走这条）
        //   · false                          → **视为 0**（裸管、无法兰的算例）
        //
        // ★ 2026-08-12：此前 false 分支会**静默回退到一维环形法兰模型 FlangeRadial**，
        //   而该模型对「圆盘 + 平面梯形舌片」几何根本不成立（Φ 算成 0.037 而真值 0.72–0.82）。
        //   这个静默回退坑过三次（§7），现已连同 FlangeRadial 一起删除 ——
        //   **不存在的代码路径不会再被误走**。
        //   D 为负是合法值（Φ>1 时法兰向管子倒灌），故用显式布尔而非看符号。
        // 两端**各自**的抽热（见 DesignInputs.FlangeDrawLeftW 的注释：原来取平均是个 bug）
        double drawL = double.IsNaN(p.FlangeDrawLeftW)
                     ? (p.FlangeDrawOverrideSet ? p.FlangeDrawOverrideW : 0.0) : p.FlangeDrawLeftW;
        double drawR = double.IsNaN(p.FlangeDrawRightW)
                     ? (p.FlangeDrawOverrideSet ? p.FlangeDrawOverrideW : 0.0) : p.FlangeDrawRightW;
        // ★★ 段间轴向导热（见 DesignInputs.NeighbourTempLeftC）。
        //    管子是连续的，段只是人为切分 ⇒ 端部通量要加 G·(T − T_邻)，
        //    G = kA/Δx 取一个节距的导度 = **连续性极限**（G 越大两端温度被拉得越紧）。
        //    不加这一项时实测同一位置断层 69 K，漏掉的热流 ~21 W 比法兰抽热还大。
        double gNb = kPt * area / Math.Max(1e-9, dx);
        double tNbL = p.NeighbourTempLeftC, tNbR = p.NeighbourTempRightC;
        bool hasL = !double.IsNaN(tNbL), hasR = !double.IsNaN(tNbR);

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
                       - TabAt(x).Eval(T) - hg * pi * (T - tgLocal[i]);
            }
            double DSrc(double x, double T)
            {
                double drho = Materials.RhoRef *
                    (Materials.AlphaFit + 2 * Materials.BetaFit * T);
                return current * current * drho / area - TabAt(x).Slope(T) - hg * pi;
            }

            var bcL = Bvp1D.Boundary.WithFlux(
                          T => drawL + (hasL ? gNb * (T - tNbL) : 0.0),
                          _ => hasL ? gNb : 0.0);
            var bcR = Bvp1D.Boundary.WithFlux(
                          T => drawR + (hasR ? gNb * (T - tNbR) : 0.0),
                          _ => hasR ? gNb : 0.0);
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
        // ⚠ 2026-08-20 删掉 FlangeMassKg 与 Phi：本方法**从来没给它们赋过值**，
        //   于是界面上那两列恒为 0。A 链结构上不含法兰（一维环形法兰模型已于
        //   2026-08-12 删除），这两个量在这里根本没有来源。
        //   字段留着只会让下一个人以为「有这个数，只是这次是 0」。
        public double Value, LossPerM, WallMm, MassKg, TMin, Margin, IA;
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
                case "flangeInsul": q.FlangeInsulThickMm = v; q.FlangeInsulated = v > 1e-6; break;
                case "eps": q.PtEmissivity = v; break;
                case "J": q.JAllowAPerMm2 = v; break;

                // ★★ 2026-08-20 补 default。原来没有这一支 ⇒ 传一个不认识的量名，
                //   循环照跑 steps 轮、每轮解的都是**同一个没被改过的基准算例**，
                //   最后吐出一张「第一列在变、其余列全同」的表。
                //   界面上「扫描：法兰厚度」传的正是这样一个不存在的量名（"flangeTf"），
                //   而它安静地这样跑了很久。
                //   —— 「安静地给出可信外观的错误结果」，本项目的头号失效模式。
                default:
                    throw new ArgumentException(
                        $"未知的扫描量「{what}」。可用：insul / flangeInsul / eps / J", nameof(what));
            }
            var r = Solve(q);
            if (!r.Ok) continue;
            rows.Add(new SweepRow
            {
                Value = v,
                LossPerM = r.LossPerMeterWPerM,
                WallMm = r.WallDesignMm,
                MassKg = r.MassTotalKg,
                TMin = r.TMinC,
                Margin = r.DevitMarginMinK,
                IA = r.CurrentA
            });
        }
        return rows;
    }

    /// <summary>
    /// ⚠ 必须允许 NaN/Infinity：本类型里用 NaN 当「未设定」哨兵
    /// （<see cref="DesignInputs.FlangeDrawLeftW"/> 等）。默认的 JsonSerializer
    /// 遇到 NaN 直接抛 ArgumentException，且报错信息完全看不出是哪个字段 ——
    /// 2026-08-14 加两端抽热字段时踩过一次。
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions CloneOpts = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static DesignInputs Clone(DesignInputs p)
        => System.Text.Json.JsonSerializer.Deserialize<DesignInputs>(
               System.Text.Json.JsonSerializer.Serialize(p, CloneOpts), CloneOpts)!;

    /// <summary>
    /// 把 <paramref name="src"/> 的全部内容覆盖进 <paramref name="dst"/> —— **不换引用**。
    ///
    /// ★★ 为什么必须有这个（2026-08-20 抓到）：
    ///   MainForm、LineDesignPage、AnalysisPage **三处持有同一个 DesignInputs 的引用**
    ///   （构造时直接传引用，没有 Clone）。而「读取方案」原本写的是 `_in = x` ——
    ///   **换引用只换得掉持有它的那一个**：参数表指向了新方案，另外两页的 `_base`
    ///   是 readonly、仍指着旧对象。
    ///
    ///   于是点完「读取」，界面显示的是新方案，「整线核算」页算的还是旧方案，
    ///   **没有任何提示**。这正是「安静地给出可信外观的错误结果」那一族。
    ///
    ///   就地覆盖之后，三处看到的永远是同一份数据 —— 接线测试用
    ///   `ReferenceEquals(page._base, main._in)` 永久守住这一族。
    /// </summary>
    public static void CopyInto(DesignInputs src, DesignInputs dst)
    {
        if (ReferenceEquals(src, dst)) return;
        foreach (var f in typeof(DesignInputs).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            f.SetValue(dst, f.GetValue(src));
        foreach (var pr in typeof(DesignInputs).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (pr.CanRead && pr.CanWrite && pr.GetIndexParameters().Length == 0)
                pr.SetValue(dst, pr.GetValue(src));
    }
}
