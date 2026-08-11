using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>
/// **解析筛选层** —— 总纲（HANDOVER §0.0）那个优化问题的快速评估器。
///
/// 壳解 + 耦合解每个候选要分钟级，搜索空间有六个自由度，硬搜不现实。
/// 本类把两条硬约束都写成**闭式**，于是单个候选是微秒级，可以扫几十万个点；
/// 筛出来的少数候选再送 <see cref="LineRunner"/> 与壳解复核。
///
/// 三条闭式判据：
///
/// **C2（稳态管根温差 &lt; 10 K）——「抽热预算」**
/// 冷点深度由半无限翅片解给出（HANDOVER §6 ②）：
///
///   |ΔT_root| = D / √(k·A_管·β)
///
/// D 是法兰从管子抽走的净热 W。**把它反过来用**，C2 就成了一条对 D 的硬预算：
///
///   |D| ≤ ΔT_max · √(k·A_管·β)
///
/// 而 √(k·A·β) 只有 0.5 W/K 量级 ⇒ **预算只有几瓦**，法兰必须做到近乎完全热自给。
/// 这是整个问题真正的难点，比 C1 紧得多。
///
/// **C1a（稳态段不烧）** 逐点自热判据（§4.2t）：J ≤ √(2q″(T)/(ρe(T)·t))
///
/// **C1b（冷启不烧）** 冷态两边散热≈0，比升温率，电流约掉：
///
///   f² · (R_法兰/C_法兰) ÷ (R_管/C_管) ≤ 1
/// </summary>
public static class DesignScreen
{
    /// <summary>一片法兰的**形状因子**：把「形状」与「厚度、电流」解耦。</summary>
    public sealed class ShapeFactors
    {
        public string Name = "";
        /// <summary>净面积 mm²（单面）</summary>
        public double AreaMm2;
        /// <summary>圆盘部分 / 舌片部分的面积 mm²（按切点分）—— 分区保温用</summary>
        public double DiscAreaMm2, TabAreaMm2;
        /// <summary>电阻形状因子：R[Ω] = ρe[Ω·mm] · ShapeR / t[mm]</summary>
        public double ShapeR;
        /// <summary>峰值电流密度形状因子：J_max[A/mm²] = ShapeJ · I[A] / t[mm]</summary>
        public double ShapeJ;
        public double ShapeJMean;

        public double MassG(double tMm) => AreaMm2 * tMm * Materials.PtDensity * 1e-6;
        public double ResistanceOhm(double tMm, double tempC)
            => Materials.PtResistivity(tempC) * 1e3 * ShapeR / tMm;
        public double JMax(double currentA, double tMm) => ShapeJ * currentA / tMm;
    }

    /// <summary>
    /// 由壳网格 + 电流场提取形状因子。**每个形状只需跑一次**，之后厚度与电流的扫描全是解析的。
    /// 依据：深度平均下面电流 K = J·t 守恒 ⇒ J ∝ I/t；同理 R ∝ ρe/t。
    /// </summary>
    public static ShapeFactors Extract(ShellMesh mesh, double refCurrentA = 1000.0,
                                       double refTempC = 1050.0, double tangentX = double.NaN)
    {
        double rhoMm = Materials.PtResistivity(refTempC) * 1e3;
        var sc = ShellCurrent.Solve(mesh, refCurrentA, rhoMm, refTempC);

        // 参考厚度取网格的体积加权均厚（等厚板即板厚本身）
        double tRef = mesh.VolumeMm3 / Math.Max(1e-9, mesh.TotalArea);

        double gen = 0;
        for (int i = 0; i < mesh.CellCount; i++)
            gen += rhoMm * sc.JMagAPerMm2[i] * sc.JMagAPerMm2[i] * mesh.Thickness[i] * mesh.Area[i];
        double rRef = gen / (refCurrentA * refCurrentA);          // Ω @ tRef, refTempC

        double aDisc = 0, aTab = 0;
        if (!double.IsNaN(tangentX))
            for (int i = 0; i < mesh.CellCount; i++)
                if (mesh.Centroid[i].X >= tangentX) aDisc += mesh.Area[i]; else aTab += mesh.Area[i];

        return new ShapeFactors
        {
            AreaMm2 = mesh.TotalArea,
            DiscAreaMm2 = aDisc,
            TabAreaMm2 = aTab,
            ShapeR = rRef * tRef / rhoMm,
            ShapeJ = sc.JMaxAPerMm2 * tRef / refCurrentA,
            ShapeJMean = sc.JMeanAPerMm2 * tRef / refCurrentA
        };
    }

    /// <summary>
    /// **C2 的抽热预算** W：管根温差不超过 deltaMaxK 时，法兰最多能从管子抽走多少热。
    ///
    ///   |D| ≤ ΔT_max · √(k·A_管·β)
    ///
    /// β 是管的单位长度散热斜率（含玻璃项），A_管 是管的导热截面。
    /// **这个数只有几瓦量级** —— 法兰必须近乎完全热自给，这是整个问题最紧的一条。
    /// </summary>
    public static double DrawBudgetW(DesignInputs p, double wallMm, double tubeTempC,
                                     double deltaMaxK = 10.0)
    {
        double ri = p.TubeIdMm * 0.5e-3, w = wallMm * 1e-3, rOut = ri + w;
        double area = Math.PI * (rOut * rOut - ri * ri);          // m²

        bool insulated = false;
        foreach (var lay in p.Layers) if (lay.Enabled && lay.ThicknessMm > 1e-6) insulated = true;
        double eps = insulated ? p.OuterEmissivity : p.PtEmissivity;

        var lossTab = new LossTable(p.TAmbC, tubeTempC + 300, 60,
            t => Insulation.CylinderLoss(t, p.TAmbC, rOut, p.Layers, eps,
                     p.Posture == Orientation.Vertical, p.TubeLength, p.LossScale).QPerLength);

        double beta = lossTab.Slope(tubeTempC) + p.HGlass * Math.PI * p.TubeId;   // W/(m·K)
        double k = Materials.PtThermalK(tubeTempC);
        return deltaMaxK * Math.Sqrt(k * area * beta);
    }

    /// <summary>
    /// 逐点自热上限 J_lim = √(2q″(T)/(ρe(T)·t))（§4.2t）。
    /// tempC 取工作温度 ⇒「不比管热」；取铂熔点 ⇒ 熔断上限。
    /// </summary>
    public static double JLimitAPerMm2(DesignInputs p, double tempC, double tMm,
                                       double insulThickMm)
    {
        double charLen = 0.05;
        double q;
        if (insulThickMm <= 1e-6)
            q = Insulation.FlatOuterFlux(tempC, p.TAmbC, p.PtEmissivity, charLen,
                                         p.LossScale, p.FlangeAirVelocityMPerS);
        else
        {
            var layers = new List<InsulationLayer>
            {
                new() { Name = "法兰保温", ThicknessMm = insulThickMm,
                        K0 = p.Layer1.K0, K1 = p.Layer1.K1, Enabled = true }
            };
            q = Insulation.PlateFlux(tempC, p.TAmbC, layers, p.OuterEmissivity, charLen, p.LossScale);
        }
        return Math.Sqrt(2.0 * q / (Materials.PtResistivity(tempC) * tMm * 1e-3)) * 1e-6;
    }

    /// <summary>单面热流密度 W/m²（法兰表面，按是否包纤维）</summary>
    public static double PlateFluxWPerM2(DesignInputs p, double tempC, double insulThickMm)
    {
        double charLen = 0.05;
        if (insulThickMm <= 1e-6)
            return Insulation.FlatOuterFlux(tempC, p.TAmbC, p.PtEmissivity, charLen,
                                            p.LossScale, p.FlangeAirVelocityMPerS);
        var layers = new List<InsulationLayer>
        {
            new() { Name = "法兰保温", ThicknessMm = insulThickMm,
                    K0 = p.Layer1.K0, K1 = p.Layer1.K1, Enabled = true }
        };
        return Insulation.PlateFlux(tempC, p.TAmbC, layers, p.OuterEmissivity, charLen, p.LossScale);
    }

    /// <summary>一个候选法兰的评估结果</summary>
    public sealed class PlateEval
    {
        public double ThickMm, MassG;
        public double QGenW, QLossW, DrawW;      // 自身发热 / 自身散热 / 从管子抽的热
        public double Phi;
        public double JMax, JLimWork, JLimMelt;
        public double RootDeltaK;                // 由抽热换算的管根温差
        public bool OkC2, OkC1a;
    }

    /// <summary>
    /// 评估一片法兰：给定形状、厚度、电流、管根温度、保温厚度，闭式给出两条约束的判定。
    ///
    /// 抽热用**能量恒等式** D = Q_散热 − Q_发热（HANDOVER §7：逐面累加会漏面，偏小 14 %）。
    /// 散热在管根温度处取值 —— 对**可行**方案（|ΔT| &lt; 10 K）这是自洽的。
    /// </summary>
    public static PlateEval EvalPlate(DesignInputs p, ShapeFactors s, double tMm,
                                      double currentA, double tRootC,
                                      double insulThickMm, double drawBudgetW,
                                      double deltaMaxK = 10.0)
    {
        double r = s.ResistanceOhm(tMm, tRootC);
        double gen = currentA * currentA * r;
        double loss = 2.0 * (s.AreaMm2 * 1e-6) * PlateFluxWPerM2(p, tRootC, insulThickMm);
        double draw = loss - gen;

        double jmax = s.JMax(currentA, tMm);
        return new PlateEval
        {
            ThickMm = tMm,
            MassG = s.MassG(tMm),
            QGenW = gen, QLossW = loss, DrawW = draw,
            Phi = loss > 1e-12 ? gen / loss : double.NaN,
            JMax = jmax,
            JLimWork = JLimitAPerMm2(p, tRootC, tMm, insulThickMm),
            JLimMelt = JLimitAPerMm2(p, RampTwoNode.PtMeltingC, tMm, insulThickMm),
            RootDeltaK = drawBudgetW > 0 ? draw / (drawBudgetW / deltaMaxK) : double.NaN,
            OkC2 = draw > 0 && draw <= drawBudgetW,
            OkC1a = jmax <= JLimitAPerMm2(p, tRootC, tMm, insulThickMm)
        };
    }

    /// <summary>
    /// **C1b 冷启判据**：f² · (R_法兰/C_法兰) ÷ (R_管/C_管) ≤ 1。
    /// 返回该比值；&gt;1 表示升温期法兰会跑到管子前面。热容含各自的保温层。
    /// </summary>
    public static double ColdStartRatio(DesignInputs p, ShapeFactors s, double plateThickMm,
                                        double wallMm, double sharedFactor,
                                        double plateInsulThickMm, double refTempC = 500.0)
    {
        double ri = p.TubeIdMm * 0.5e-3, w = wallMm * 1e-3, rOut = ri + w;
        double areaTube = Math.PI * (rOut * rOut - ri * ri);
        double rTube = Materials.PtResistivity(refTempC) * p.TubeLength / areaTube;

        double capTube = Materials.PtDensity * areaTube * p.TubeLength * Materials.PtCp(refTempC);
        double rr = rOut;
        foreach (var lay in p.Layers)
        {
            if (!lay.Enabled || lay.ThicknessMm <= 1e-6) continue;
            double rNext = rr + lay.ThicknessMm * 1e-3;
            capTube += Math.PI * (rNext * rNext - rr * rr) * p.TubeLength
                       * lay.DensityKgM3 * lay.CpJKgK * 0.5;
            rr = rNext;
        }

        double rPlate = s.ResistanceOhm(plateThickMm, refTempC);
        double capPlate = s.MassG(plateThickMm) * 1e-3 * Materials.PtCp(refTempC);
        if (plateInsulThickMm > 1e-6)
            capPlate += 2.0 * (s.AreaMm2 * 1e-6) * (plateInsulThickMm * 1e-3)
                        * p.Layer1.DensityKgM3 * p.Layer1.CpJKgK * 0.5;

        return sharedFactor * sharedFactor * (rPlate / capPlate) / (rTube / capTube);
    }
}
