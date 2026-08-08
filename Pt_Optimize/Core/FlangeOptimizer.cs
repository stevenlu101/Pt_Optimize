using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 法兰联合优化：保温厚度 δ × 外径 r_o × 剖面形状。
///
/// 目标：在满足析晶约束的前提下最小化铂用量。
/// 关键约束是可制造性 —— 理想厚度 t_ideal = C/r² 必须 ≥ t_min，
/// 否则「渐变」在纸面上成立、在车间里做不出来。该边界由
///     r_o,max = √(C / t_min),   C = ρe·I²/(8π²·q″)
/// 给出；只有 r_o,max > r_i 时渐变才有可行域。
/// </summary>
public static class FlangeOptimizer
{
    public sealed class Row
    {
        public double InsulMm, RoMm;
        public FlangeShape Shape = FlangeShape.Rectangular;
        public double FluxKwM2, RoMaxMm, Phi, TMinC, MarginK;
        public double FlangeMassG, TotalMassKg, CurrentA, QRootW;
        public bool Feasible;          // 析晶裕度达标
        public bool TaperManufacturable; // r_o,max > r_i
        public string Note = "";
    }

    public static List<Row> Scan(DesignInputs baseCase,
                                 double[] insulMm, double[] roMm, FlangeShape[] shapes)
    {
        var rows = new List<Row>();
        foreach (var d in insulMm)
            foreach (var ro in roMm)
                foreach (var sh in shapes)
                {
                    var p = SegmentSolver.Clone(baseCase);
                    p.FlangeInsulThickMm = d;
                    p.FlangeInsulated = d > 1e-6;
                    p.FlangeRoMm = ro;
                    p.FlangeShapeMode = sh;
                    if (sh == FlangeShape.Rectangular)
                        p.FlangeThickMm = baseCase.FlangeThickMm;

                    SolveResult r;
                    try { r = SegmentSolver.Solve(p); }
                    catch (Exception ex)
                    { rows.Add(new Row { InsulMm = d, RoMm = ro, Shape = sh, Note = ex.Message }); continue; }
                    if (!r.Ok) { rows.Add(new Row { InsulMm = d, RoMm = ro, Shape = sh, Note = r.Message }); continue; }

                    var fa = r.FlangeA;
                    rows.Add(new Row
                    {
                        InsulMm = d,
                        RoMm = ro,
                        Shape = sh,
                        FluxKwM2 = FlangeRadial.FlangeFlux(p, p.TSetC) / 1000.0,
                        RoMaxMm = fa.ROMaxMm,
                        Phi = fa.PhiOverall,
                        TMinC = r.TMinC,
                        MarginK = r.DevitMarginMinK,
                        FlangeMassG = r.MassFlangePairKg * 1000,
                        TotalMassKg = r.MassTotalKg,
                        CurrentA = r.CurrentA,
                        QRootW = fa.QRootW,
                        Feasible = r.DevitMarginMinK >= p.DevitMarginK,
                        TaperManufacturable = fa.ROMaxMm > p.FlangeRiMm
                    });
                }
        return rows;
    }

    /// <summary>在可行解中挑铂用量最小的。无可行解返回 null。</summary>
    public static Row? Best(IEnumerable<Row> rows)
        => rows.Where(x => x.Feasible && string.IsNullOrEmpty(x.Note))
               .OrderBy(x => x.TotalMassKg)
               .FirstOrDefault();

    public static string Format(List<Row> rows, DesignInputs p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"保温",5}{"r_o",6}{"剖面",14}{"q″",8}{"r_o,max",9}" +
                      $"{"Φ",8}{"最冷",8}{"裕度",8}{"法兰",8}{"总铂",8}{"I",7}  判定");
        sb.AppendLine($"{"mm",5}{"mm",6}{"",14}{"kW/m²",8}{"mm",9}" +
                      $"{"",8}{"°C",8}{"K",8}{"g",8}{"kg",8}{"A",7}");
        sb.AppendLine(new string('-', 108));

        foreach (var x in rows)
        {
            if (!string.IsNullOrEmpty(x.Note))
            {
                sb.AppendLine($"{x.InsulMm,5:0}{x.RoMm,6:0}{ShapeName(x.Shape),14}   ✗ {x.Note}");
                continue;
            }
            string verdict = x.Feasible ? "✓ 达标"
                           : x.MarginK > 0 ? "△ 裕度不足"
                           : "✗ 析晶";
            if (x.Shape != FlangeShape.Rectangular && !x.TaperManufacturable)
                verdict += " / 渐变不可制造";

            sb.AppendLine(
                $"{x.InsulMm,5:0}{x.RoMm,6:0}{ShapeName(x.Shape),14}{x.FluxKwM2,8:0.0}" +
                $"{x.RoMaxMm,9:0.0}{x.Phi,8:0.000}{x.TMinC,8:0.0}{x.MarginK,8:+0;-0}" +
                $"{x.FlangeMassG,8:0}{x.TotalMassKg,8:0.000}{x.CurrentA,7:0}  {verdict}");
        }

        var best = Best(rows);
        sb.AppendLine();
        if (best is null)
            sb.AppendLine("★ 扫描范围内无可行解 —— 需放宽 t_min、降低 T_liq 要求，或改变管几何。");
        else
            sb.AppendLine($"★ 最小铂用量可行解：保温 {best.InsulMm:0} mm，r_o = {best.RoMm:0} mm，" +
                          $"{ShapeName(best.Shape)}，法兰 {best.FlangeMassG:0} g，总铂 {best.TotalMassKg:0.000} kg，" +
                          $"析晶裕度 +{best.MarginK:0} K");
        return sb.ToString();
    }

    private static string ShapeName(FlangeShape s) => s switch
    {
        FlangeShape.Rectangular => "等厚",
        FlangeShape.Trapezoid => "梯形渐变",
        FlangeShape.IdealTaper => "理想 1/r²",
        _ => "理想截断"
    };
}
