using System;
using System.Collections.Generic;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 整条线的分段核算：逐段做力学与质量核算，汇总铂重与相对成本。
///
/// 强度校核是解析的（二分求 σ_vm·SF = σ_allow），不需要耦合热解，故可即时响应 UI 编辑。
/// 电流密度与析晶两条约束需要耦合解，成本高，由 <see cref="CoupledSolver"/> 单独按段跑。
/// </summary>
public static class LineSolver
{
    public static List<SegmentResult> Solve(IEnumerable<Segment> segs, DesignInputs baseP)
    {
        var res = new List<SegmentResult>();
        var plate = new FlangePlate();

        foreach (var s in segs)
        {
            var p = SegmentSolver.Clone(baseP);
            p.TSetC = s.TSetC; p.TGlassInC = s.TGlassInC;
            p.GlassHeadM = s.GlassHeadM; p.SupportSpanMm = s.LengthMm;
            p.TubeIdMm = s.TubeIdMm; p.TubeLengthMm = s.LengthMm;
            p.WallMinMm = s.WallMm; p.GradeName = s.GradeName;
            p.TLiquidusC = s.TLiquidusC;

            var g = MaterialDb.Get(s.GradeName);
            var r = new SegmentResult { Seg = s };

            var mr = Mechanics.Check(p, s.WallMm, 2.0, plate);
            r.VonMisesMPa = mr.TubeVonMisesMPa;
            r.AllowMPa = g.AllowableMPa(s.TSetC, p.DesignLifeHours, p.SafetyFactor);
            r.Utilization = r.AllowMPa > 0 ? r.VonMisesMPa / r.AllowMPa : 999;
            r.MinWallStrengthMm = Mechanics.MinWallForStrengthMm(p, plate, s.TSetC);

            // 质量：用该牌号的密度
            double area = Math.PI * (s.WallMm * 1e-3) * (s.TubeIdMm * 1e-3 + s.WallMm * 1e-3);
            r.MassG = area * (s.LengthMm * 1e-3) * g.DensityKgM3 * 1000.0;
            r.CostRelative = r.MassG * g.CostPerKgRelative;

            r.Feasible = r.Utilization <= 1.0;
            r.Binding = r.Utilization > 1.0 ? "强度" : "";
            res.Add(r);
        }
        return res;
    }

    public static (double massG, double costRel, int infeasible) Totals(List<SegmentResult> rs)
        => (rs.Sum(x => x.MassG), rs.Sum(x => x.CostRelative), rs.Count(x => !x.Feasible));

    /// <summary>为该段挑最省成本的可行牌号</summary>
    public static string BestGrade(Segment s, DesignInputs baseP, IEnumerable<string> candidates)
    {
        string best = ""; double bestCost = double.MaxValue;
        foreach (var gn in candidates)
        {
            var t = new Segment
            {
                Name = s.Name, TSetC = s.TSetC, TGlassInC = s.TGlassInC,
                GlassHeadM = s.GlassHeadM, LengthMm = s.LengthMm,
                TubeIdMm = s.TubeIdMm, WallMm = s.WallMm,
                GradeName = gn, TLiquidusC = s.TLiquidusC
            };
            var r = Solve(new[] { t }, baseP)[0];
            // 壁厚取该牌号强度允许的最小值（其余约束在耦合解里再收）
            if (double.IsNaN(r.MinWallStrengthMm)) continue;
            t.WallMm = Math.Max(r.MinWallStrengthMm, 0.3);
            var r2 = Solve(new[] { t }, baseP)[0];
            if (r2.Feasible && r2.CostRelative < bestCost)
            { bestCost = r2.CostRelative; best = gn; }
        }
        return best;
    }
}
