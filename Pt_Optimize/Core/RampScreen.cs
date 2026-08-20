using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// ① 升温可达性的**闭式快筛** —— 唯一实现。
///
/// 物理：温控功率下升温是准静态的 ⇒「能不能升到」等价于
/// 「该温度的**稳态**工作点要多大电流、这个电流有没有越过热稳定极限」。
/// 于是不必解瞬态，一个闭式就够，微秒级。
///
/// ★ 为什么要从 AnalysisPage 里搬出来（2026-08-20）：
///   判定阈值（裕度 1.5 / 1.0）原本写死在界面的一行三元表达式里
///   （`margin > 1.5 ? "✓" : margin > 1.0 ? "⚠" : "✗"`）。那是全项目**第四处**
///   判定逻辑，而界面的阶段门禁又要用它 —— 再抄一遍就是第五处。
///
/// ⚠⚠ **这是快筛，不是交付判据。**
///   ① 的交付判定由 <see cref="LineRunner"/>.Judge 在整线耦合解里给出（限 72 h）。
///   本类只回答「这个工作点是不是明显不成立」，用来在跑分钟级整线解**之前**先挡一道。
///   两者若不一致，**一律以整线解为准**（界面的门禁按这个优先级读）。
/// </summary>
public static class RampScreen
{
    /// <summary>热稳定裕度的硬下界：越过它，稳态解本就不存在。</summary>
    public const double MarginHardMin = 1.0;

    /// <summary>裕度薄的提醒线 —— 之上算稳，之间算薄，之下算不成立。</summary>
    public const double MarginThin = 1.5;

    /// <summary>升温目标温度 °C（空管口径：升温时管内无玻璃）。</summary>
    public const double TargetC = 1150;

    public sealed record Point(
        double InsulMm,
        double WallMm,
        double LossW,
        double CurrentA,
        double TubeJAPerMm2,
        double IStabA,
        /// <summary>热稳定裕度 = I_stab / I。&lt; 1 表示越过热稳定极限。</summary>
        double Margin,
        double MassG)
    {
        /// <summary>与界面表格里那一列完全一致的判定文字 —— 阈值只存在这一处。</summary>
        public string Verdict => Margin > MarginThin ? "✓"
                               : Margin > MarginHardMin ? "⚠ 裕度薄"
                               : "✗ 越热稳定极限";

        public bool Ok => Margin > MarginHardMin;
    }

    /// <summary>
    /// 单个工作点的闭式解。
    /// 逐字搬自原 AnalysisPage.Gate1 的内层循环，物理未动。
    /// </summary>
    public static Point Evaluate(DesignInputs baseInputs, double insulMm, double wallMm,
                                 double tTargetC = TargetC)
    {
        var q = SegmentSolver.Clone(baseInputs);
        q.Layer1.ThicknessMm = insulMm; q.Layer1.Enabled = true;
        q.WallMinMm = wallMm; q.TSetC = tTargetC;

        double ri = q.TubeIdMm * 0.5e-3, ww = wallMm * 1e-3, rOut = ri + ww;
        double aM2 = Math.PI * (rOut * rOut - ri * ri), aMm2 = aM2 * 1e6;
        bool anyIns = q.Layers.Any(l => l.Enabled && l.ThicknessMm > 1e-6);
        double eps = anyIns ? q.OuterEmissivity : q.PtEmissivity;

        var tab = new LossTable(q.TAmbC, tTargetC + 300, 60,
            t => Insulation.CylinderLoss(t, q.TAmbC, rOut, q.Layers, eps,
                     q.Posture == Orientation.Vertical,
                     q.TubeLength, q.LossScale).QPerLength);

        double lossW = tab.Eval(tTargetC) * q.TubeLength;
        double rOhm = Materials.PtResistivity(tTargetC) * q.TubeLength / aM2;
        double iA = Math.Sqrt(lossW / rOhm), jA = iA / aMm2;

        // 热稳定极限：I_stab = √(βA / (dρe/dT))。越过它稳态解不存在（不是「不够好」，是没有解）。
        double beta = tab.Slope(tTargetC);
        double drho = Materials.RhoRef * (Materials.AlphaFit + 2 * Materials.BetaFit * tTargetC);
        double iStab = Math.Sqrt(Math.Max(1e-9, beta * aM2 / drho));
        double margin = iStab / Math.Max(1e-9, iA);

        double massG = aMm2 * q.TubeLengthMm * Materials.PtDensity * 1e-6;

        return new Point(insulMm, wallMm, lossW, iA, jA, iStab, margin, massG);
    }

    /// <summary>
    /// 把某个工作点包成一条判据，供界面门禁读。
    ///
    /// ⚠ Name 以 <c>LineResult.Key.Ramp</c> 开头，于是门禁能用同一个常量找到它；
    ///   但整线解一旦跑出来，门禁会**优先**取 Judge 给的那条（见 UI/Gate.FindCheck）。
    /// </summary>
    public static ConstraintOut Judge(DesignInputs baseInputs, double insulMm, double wallMm,
                                      double tTargetC = TargetC)
    {
        var p = Evaluate(baseInputs, insulMm, wallMm, tTargetC);
        return new ConstraintOut
        {
            Name = "① 升温 可达性（快筛）",
            Unit = "—",
            Kind = CheckKind.HardSafety,
            Actual = p.Margin,
            Limit = MarginHardMin,
            LessIsBetter = false,
            Ok = p.Ok,
            Where = $"保温 {insulMm:0.0} / 壁厚 {wallMm:0.00}",
            Note = $"热稳定裕度 = I_stab/I = {p.IStabA:0}/{p.CurrentA:0}。"
                 + (p.Margin <= MarginHardMin
                    ? "★★ **越过热稳定极限** ⇒ 该工作点的稳态解本就不存在，升温到不了目标。"
                      + "　【下一步】加厚纤维保温（同时降电流与 J，且保温无空间限制 —— 管侧的免费杠杆）。"
                    : p.Margin <= MarginThin
                    ? "★ 裕度薄，建议加厚保温留出余量。"
                    : "")
                 + " ⚠ 这是**闭式快筛**，① 的交付判定由整线耦合解给出（限 72 h）；两者不一致时以整线解为准。"
        };
    }
}
