using System;
using System.Collections.Generic;

namespace PtOptimize.Core;

/// <summary>升温时二次侧的闭环方式。三者在冷启时给出**完全不同**的电流。</summary>
public enum RampControl
{
    /// <summary>恒流：电流不随温度变，发热随 ρe(T) 上升 ⇒ 最烫的时刻在升温**末段**</summary>
    ConstantCurrent,
    /// <summary>恒压：I = V/R(T)，冷态 ρe 只有热态的 1/4.4 ⇒ 冷启电流约 2 倍、发热约 4 倍，危险在**开头**</summary>
    ConstantVoltage,
    /// <summary>恒功率：I = √(P/R(T))，介于两者之间</summary>
    ConstantPower
}

public sealed class RampTwoNodeResult
{
    public RampControl Mode;
    public bool TubeReached;
    public double HoursToTarget = double.NaN;

    public double TTubeEndC, TFlangePeakC, TTubePeakC;
    /// <summary>整个升温过程中「法兰温度 − 管温」的最大值 K。&gt;0 即 §4.2k 的失效方向</summary>
    public double MaxFlangeMinusTubeK;
    /// <summary>上者出现的时刻 s，以及当时的管温 —— 用来判断危险窗口在开头还是末段</summary>
    public double TimeAtMaxDeltaS, TTubeAtMaxDeltaC;

    public bool FlangeMelts;
    public double MeltTimeS = double.NaN;

    public double CurrentStartA, CurrentEndA, PeakCurrentA;
    public double CapTubeJPerK, CapFlangeJPerK, CouplingWPerK;

    /// <summary>采样轨迹（供绘图/核对），列：时间 s、管温、法兰温、电流</summary>
    public List<(double t, double tt, double tf, double i)> Trace = new();
    public string Note = "";
}

/// <summary>
/// **两节点空管升温**：管 与 法兰各自一个温度节点，用孔壁导热耦合。
///
/// 为什么必须两节点 —— <see cref="RampSolver"/> 把法兰并进管子当**同一个温度**
/// （<c>NetFlangePairW(tC, …)</c> 在管温上取值），于是「法兰比管热」这个失效模式
/// 在那个模型里**结构性地不可能出现**。而 HANDOVER §4.2k 说的烧断正是这一条。
///
/// 三条让法兰在升温期跑到管子前面的机理，本模型都显式含着：
///
/// | 机理 | 在方程里的位置 |
/// |---|---|
/// | 冷态散热≈0 而发热照常 | 法兰散热项 q″(T_f) 在低温近乎为零，发热项 I²R_f 不随温度减小 |
/// | 法兰不背保温热容，管子背 | C_管 含保温层热容（常大于铂本身），C_法兰 只含薄薄一层 |
/// | 管子当不了散热器 | 耦合导度 G 由**管壁的翅片导度** √(k·A·β) 与孔壁导度串联，只有约 1 W/K |
///
/// 电学上：管与它两端的**端片**串在同一回路（同一电流）；**共用片**走的是
/// §4.2h 的叠加电流 <c>SharedFactor·I</c>，它不是回路里的串联元件，只是发热更凶。
///
/// 集总的代价：法兰盘内的径向温差被抹平。铂热扩散率 α = k/(ρc) ≈ 2.6e-5 m²/s，
/// 一分钟的扩散长度约 39 mm，与盘的径向尺寸（26→60 mm）同量级 ⇒
/// **分钟尺度上盘内确实是拉平的**，集总成立；秒级的孔周局部过热要看壳解。
/// </summary>
public static class RampTwoNode
{
    /// <summary>铂熔点 °C</summary>
    public const double PtMeltingC = 1768.0;

    public sealed class Inputs
    {
        public double WallMm = 1.0;

        // ── 法兰（由壳网格 + ShellCurrent 给出，见 CLI --ramp2；**不需要**稳态热解）
        /// <summary>单片质量 g</summary>
        public double FlangeMassG;
        /// <summary>单面面积 mm²，按「包保温 / 裸露」分开（散热两面各一份）</summary>
        public double FlangeAreaInsulMm2, FlangeAreaBareMm2;
        /// <summary>参考温度下单片电阻 Ω = QGen_ref / I_ref²</summary>
        public double FlangeResistanceRefOhm;
        public double FlangeRefTempC = 1050;
        /// <summary>共用片的电流叠加系数：§4.2h 推导为 √3；工作簿用 1.5；端片为 1</summary>
        public double SharedFactor = Math.Sqrt(3.0);

        // ── 耦合几何
        public double HoleRadiusMm = 26.0;
        /// <summary>盘的等效外半径 mm（由净面积反算），只用于孔壁导热的特征长度</summary>
        public double PlateEqOuterRadiusMm = 60.0;
        public double FlangeThickMm = 2.0;

        // ── 工况
        public RampControl Mode = RampControl.ConstantCurrent;
        /// <summary>设计（额定）电流 A —— 恒压/恒功率模式下用它在**目标温度**处定 V 或 P</summary>
        public double DesignCurrentA;
        public double FromC = 25, TargetC = 1150, MaxHours = 3.0;
    }

    public static RampTwoNodeResult Solve(DesignInputs p, Inputs g)
    {
        var res = new RampTwoNodeResult { Mode = g.Mode };

        double ri = p.TubeIdMm * 0.5e-3, w = g.WallMm * 1e-3, rOut = ri + w;
        double areaTube = Math.PI * (rOut * rOut - ri * ri);          // m²
        double L = p.TubeLength;

        // ── 散热表（空管：管内没有玻璃项）
        bool insulated = false;
        foreach (var lay in p.Layers) if (lay.Enabled && lay.ThicknessMm > 1e-6) insulated = true;
        double epsTube = insulated ? p.OuterEmissivity : p.PtEmissivity;

        const double tabTop = 2000.0;      // 表的上界；超铂熔点后积分本就停了
        var tubeLoss = new LossTable(p.TAmbC, tabTop, 80,
            t => Insulation.CylinderLoss(t, p.TAmbC, rOut, p.Layers, epsTube,
                                         p.Posture == Orientation.Vertical, L, p.LossScale).QPerLength);

        double charLen = 0.05;
        var flangeInsLayers = new List<InsulationLayer>
        {
            new() { Name = "法兰保温", ThicknessMm = p.FlangeInsulThickMm,
                    K0 = p.Layer1.K0, K1 = p.Layer1.K1,
                    DensityKgM3 = p.Layer1.DensityKgM3, CpJKgK = p.Layer1.CpJKgK,
                    Enabled = p.FlangeInsulThickMm > 1e-6 }
        };
        var fluxIns = new LossTable(p.TAmbC, tabTop, 80,
            t => Insulation.PlateFlux(t, p.TAmbC, flangeInsLayers, p.OuterEmissivity, charLen, p.LossScale));
        var fluxBare = new LossTable(p.TAmbC, tabTop, 80,
            t => Insulation.FlatOuterFlux(t, p.TAmbC, p.PtEmissivity, charLen,
                                          p.LossScale, p.FlangeAirVelocityMPerS));

        // 单片散热 W（两面各一份，mm² → m²）
        double FlangeLossW(double tf)
            => 2.0 * 1e-6 * (g.FlangeAreaInsulMm2 * fluxIns.Eval(tf)
                           + g.FlangeAreaBareMm2 * fluxBare.Eval(tf));

        // ── 热容
        double massTube = Materials.PtDensity * areaTube * L;          // kg
        double massFlange = g.FlangeMassG * 1e-3;                      // kg
        double capInsulTube = 0;
        {
            double r = rOut;
            foreach (var lay in p.Layers)
            {
                if (!lay.Enabled || lay.ThicknessMm <= 1e-6) continue;
                double rNext = r + lay.ThicknessMm * 1e-3;
                // ×0.5 是梯度因子：保温内表面跟着金属走、外表面接近环境（与 RampSolver 同口径）
                capInsulTube += Math.PI * (rNext * rNext - r * r) * L
                                * lay.DensityKgM3 * lay.CpJKgK * 0.5;
                r = rNext;
            }
        }
        double capInsulFlange = 0;
        if (p.FlangeInsulThickMm > 1e-6)
        {
            double volM3 = 2.0 * (g.FlangeAreaInsulMm2 * 1e-6) * (p.FlangeInsulThickMm * 1e-3);
            capInsulFlange = volM3 * p.Layer1.DensityKgM3 * p.Layer1.CpJKgK * 0.5;
        }
        double CapTube(double t) => massTube * Materials.PtCp(t) + capInsulTube;
        double CapFlange(double t) => massFlange * Materials.PtCp(t) + capInsulFlange;

        // ── 耦合导度 G [W/K]：孔壁导热 与 管壁翅片导度 串联
        //    管侧用半无限翅片的入口导度 √(k·A·β)（HANDOVER §6 ② 的 |ΔT_dip| = D/√(kAβ) 同一式）
        double GCouple(double tMean)
        {
            double k = Materials.PtThermalK(tMean);                    // W/(m·K)
            double beta = Math.Max(1e-6, tubeLoss.Slope(tMean));       // W/(m·K)，空管无玻璃项
            double gTubeFin = Math.Sqrt(k * areaTube * beta);          // W/K（管子一侧）
            double aHole = 2 * Math.PI * (g.HoleRadiusMm * 1e-3) * (g.FlangeThickMm * 1e-3);  // m²
            double lChar = Math.Max(1e-3, (g.PlateEqOuterRadiusMm - g.HoleRadiusMm) * 0.5e-3);
            double gPlate = k * aHole / lChar;                         // W/K（法兰一侧）
            return 1.0 / (1.0 / Math.Max(1e-9, gTubeFin) + 1.0 / Math.Max(1e-9, gPlate));
        }

        // ── 电学：管与两端**端片**串联（同一电流），共用片只是发热更凶
        double RTube(double t) => Materials.PtResistivity(t) * L / areaTube;
        double rhoRef = Math.Max(1e-30, Materials.PtResistivity(g.FlangeRefTempC));
        double RFlange(double t) => g.FlangeResistanceRefOhm * Materials.PtResistivity(t) / rhoRef;
        double RCircuit(double tt, double tf) => RTube(tt) + 2.0 * RFlange(tf);

        // 恒压/恒功率的定值：取**目标温度**处跑出设计电流所需的 V 或 P
        double rAtTarget = RCircuit(g.TargetC, g.TargetC);
        double vRef = g.DesignCurrentA * rAtTarget;
        double pRef = g.DesignCurrentA * g.DesignCurrentA * rAtTarget;

        double CurrentAt(double tt, double tf) => g.Mode switch
        {
            RampControl.ConstantCurrent => g.DesignCurrentA,
            RampControl.ConstantVoltage => vRef / Math.Max(1e-12, RCircuit(tt, tf)),
            _ => Math.Sqrt(pRef / Math.Max(1e-12, RCircuit(tt, tf)))
        };

        // ── 积分（显式，步长自适应到「每步温升 ≤ 1 K」）
        double tTube = g.FromC, tFl = g.FromC, time = 0, maxSec = g.MaxHours * 3600.0;
        res.CurrentStartA = CurrentAt(tTube, tFl);
        res.PeakCurrentA = res.CurrentStartA;
        res.CapTubeJPerK = CapTube(0.5 * (g.FromC + g.TargetC));
        res.CapFlangeJPerK = CapFlange(0.5 * (g.FromC + g.TargetC));
        res.CouplingWPerK = GCouple(0.5 * (g.FromC + g.TargetC));

        double nextSample = 0;
        int guard = 0;
        while (time < maxSec && guard++ < 4_000_000)
        {
            double i = CurrentAt(tTube, tFl);
            double iFl = g.SharedFactor * i;
            res.PeakCurrentA = Math.Max(res.PeakCurrentA, i);

            double pTube = i * i * RTube(tTube);
            double pFl = iFl * iFl * RFlange(tFl);
            double qTube = tubeLoss.Eval(tTube) * L;
            double qFl = FlangeLossW(tFl);
            double qc = GCouple(0.5 * (tTube + tFl)) * (tTube - tFl);   // >0 = 管子加热法兰

            double dTt = (pTube - qTube - qc) / CapTube(tTube);
            double dTf = (pFl - qFl + qc) / CapFlange(tFl);

            if (time >= nextSample)
            { res.Trace.Add((time, tTube, tFl, i)); nextSample = time + 5.0; }

            double delta = tFl - tTube;
            if (delta > res.MaxFlangeMinusTubeK)
            {
                res.MaxFlangeMinusTubeK = delta;
                res.TimeAtMaxDeltaS = time; res.TTubeAtMaxDeltaC = tTube;
            }
            res.TTubePeakC = Math.Max(res.TTubePeakC, tTube);
            res.TFlangePeakC = Math.Max(res.TFlangePeakC, tFl);

            if (tFl >= PtMeltingC && !res.FlangeMelts)
            {
                res.FlangeMelts = true; res.MeltTimeS = time;
                res.Note = $"法兰在 {time / 60:0.0} min 处越过铂熔点（此时管温仅 {tTube:0} °C）";
                break;
            }
            if (tTube >= g.TargetC)
            {
                res.TubeReached = true; res.HoursToTarget = time / 3600.0;
                break;
            }
            if (dTt <= 0 && dTf <= 0 && tTube < g.TargetC)
            {
                res.Note = $"管在 {tTube:0.0} °C 处电功率与散热持平，升不上去";
                break;
            }

            double rate = Math.Max(Math.Abs(dTt), Math.Abs(dTf));
            double dt = Math.Clamp(1.0 / Math.Max(1e-9, rate), 0.002, 2.0);
            tTube += dTt * dt; tFl += dTf * dt; time += dt;
        }

        res.TTubeEndC = tTube;
        res.CurrentEndA = CurrentAt(tTube, tFl);
        res.Trace.Add((time, tTube, tFl, res.CurrentEndA));
        if (res.Note.Length == 0 && !res.TubeReached)
            res.Note = $"限时 {g.MaxHours:0.#} h 内管只升到 {tTube:0.0} °C";
        return res;
    }
}
