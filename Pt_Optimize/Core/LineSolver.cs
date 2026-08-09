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

    // ────────────────────────────────────────────────────────────────
    //  法兰（整线）
    //
    //  片数：**n 段 = n+1 片**，不是 2n —— 相邻两段共用接头处那一片。
    //  单段就是 2 片，与 Pt_Heater.3dm 的两个法兰实体一致（--geom 已校核）。
    //
    //  厚度：由电流密度定，不由强度定，所以**不会随管壁减薄而同比例变薄**。
    //  这是省铂率的主要稀释源 —— 单段耦合解里法兰占总铂 66%。
    // ────────────────────────────────────────────────────────────────

    /// <summary>共用法兰的电流系数。见 <see cref="JointCurrentA"/>。</summary>
    public const double SharedFlangeFactor = 1.5;

    /// <summary>法兰定尺进度（整个过程按分钟计，UI 必须显示进度）</summary>
    public sealed class FlangeProgress
    {
        public int Segment, SegmentCount;
        public string SegmentName = "", Detail = "";
        /// <summary>0–1；段内不再细分，细节看 <see cref="Detail"/></summary>
        public double Fraction => SegmentCount > 0 ? (double)Segment / SegmentCount : 0;
        public override string ToString()
            => SegmentCount == 0 ? Detail
             : Segment >= SegmentCount ? Detail
             : $"{SegmentName}（{Segment + 1}/{SegmentCount}） · {Detail}";
    }

    /// <summary>一片法兰（接头）的定尺结果</summary>
    public sealed class FlangeResult
    {
        public string Joint = "";        // 该片位于哪个接头
        public bool Shared;              // 是否由相邻两段共用
        public double CurrentA;          // 该片承担的电流
        public double ThicknessMm;
        public double MassG;
        public string SizedBy = "";      // 厚度由哪一段的需求决定
    }

    /// <summary>n 段线上的法兰片数：两端各一片，段间共用 —— n 段 n+1 片，不是 2n。</summary>
    public static int FlangeCount(int segments) => Math.Max(0, segments) + 1;

    /// <summary>
    /// 接头 j 上那片法兰承担的电流（j ∈ [0, n]，段 j−1 与段 j 之间）。
    ///
    /// 取自《鉑金電氣計算.xlsx》的实际算法（该表 3 段 4 片，与本函数口径一致）：
    ///   端头片（j=0 或 j=n）：I = 相邻那一段的电流
    ///   共用片：I = (I_左 + I_右)/2 × 1.5      ← 工作簿 T10 / T11 两格
    ///
    /// **既不是取大，也不是相加。** 等电流时 = 1.5·I，是最坏情况（两路同相纯相加 2I）的 75%
    /// —— 两段独立供电、相位未知时的设计系数。工作簿据此把共用片从 1.3 加厚到 2.0 mm
    /// （比值 1.538 ≈ 电流比 1.5），使四片法兰的 J 都落在 6.3–6.5 A/mm²。
    /// </summary>
    public static double JointCurrentA(IList<double> segmentCurrentA, int j)
    {
        int n = segmentCurrentA.Count;
        if (j <= 0) return segmentCurrentA[0];
        if (j >= n) return segmentCurrentA[n - 1];
        return (segmentCurrentA[j - 1] + segmentCurrentA[j]) / 2.0 * SharedFlangeFactor;
    }

    /// <summary>
    /// 逐接头给出法兰厚度与质量。每段跑一次耦合解（约 30 s/段）得到该段的电流与
    /// 按 J 所需的法兰厚度，再按 <see cref="JointCurrentA"/> 折算到每个接头。
    ///
    /// 折算依据：深度平均下面电流 K = J·t 守恒，故 J ∝ I/t，
    /// 定尺后 J = J_allow ⇒ **t ∝ I**，于是 t_接头 = t_段 × (I_接头 / I_段)。
    /// 共用片要同时满足两侧，取两者较大值。
    /// </summary>
    public static List<FlangeResult> SizeFlanges(IList<Segment> segs, DesignInputs baseP,
                                                 FlangePlate proto,
                                                 IProgress<FlangeProgress>? progress = null,
                                                 CancellationToken cancel = default)
    {
        int n = segs.Count;
        var need = new double[n];          // 每段自身要求的法兰厚度 mm
        var amps = new double[n];          // 每段电流 A

        for (int i = 0; i < n; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var s = segs[i];
            var sub = progress is null ? null : new Progress<CoupledSolver.Progress>(cp =>
                progress.Report(new FlangeProgress
                {
                    Segment = i, SegmentCount = n, SegmentName = s.Name, Detail = cp.ToString()
                }));
            progress?.Report(new FlangeProgress
            { Segment = i, SegmentCount = n, SegmentName = s.Name, Detail = "启动耦合解…" });
            var p = SegmentSolver.Clone(baseP);
            p.TSetC = s.TSetC; p.TGlassInC = s.TGlassInC;
            p.GlassHeadM = s.GlassHeadM; p.SupportSpanMm = s.LengthMm;
            p.TubeIdMm = s.TubeIdMm; p.TubeLengthMm = s.LengthMm;
            p.WallMinMm = s.WallMm; p.GradeName = s.GradeName;
            p.TLiquidusC = s.TLiquidusC;
            p.SizeFlangeThickness = true;

            // CoupledSolver 会就地改写 ThicknessMm，每段必须给一份独立的板
            var g = ClonePlate(proto);
            try
            {
                var c = CoupledSolver.Solve(p, g, progress: sub, cancel: cancel);
                bool ok = c.Tube.Ok && c.FlangeThickMm > 0;
                need[i] = ok ? c.FlangeThickMm : proto.ThicknessMm;
                amps[i] = ok ? c.Tube.CurrentA : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { need[i] = proto.ThicknessMm; amps[i] = 0; }
        }
        progress?.Report(new FlangeProgress
        { Segment = n, SegmentCount = n, SegmentName = "", Detail = "按接头折算厚度…" });

        var res = new List<FlangeResult>(FlangeCount(n));
        double area = CoupledSolver.PlateArea(proto);

        for (int j = 0; j <= n; j++)
        {
            double iJoint = JointCurrentA(amps, j);

            // t ∝ I：把每一侧段的定尺结果按电流比折算到本接头，共用片取较大者
            double t = 0; string by = "";
            for (int k = j - 1; k <= j; k++)
            {
                if (k < 0 || k >= n) continue;
                double tk = amps[k] > 0 ? need[k] * (iJoint / amps[k]) : need[k];
                if (tk > t) { t = tk; by = segs[k].Name; }
            }

            res.Add(new FlangeResult
            {
                Joint = j == 0 ? "入口" : j == n ? "出口" : $"{segs[j - 1].Name}|{segs[j].Name}",
                Shared = j > 0 && j < n,
                CurrentA = iJoint,
                ThicknessMm = t,
                MassG = area * t * Materials.PtDensity * 1e-6,
                SizedBy = by
            });
        }
        return res;
    }

    private static FlangePlate ClonePlate(FlangePlate s) => new()
    {
        DiscRadiusMm = s.DiscRadiusMm,
        HoleRadiusMm = s.HoleRadiusMm,
        TabEndXMm = s.TabEndXMm,
        TabEndHalfWidthMm = s.TabEndHalfWidthMm,
        ThicknessMm = s.ThicknessMm,
        InsulBoundaryXMm = s.InsulBoundaryXMm,
        ExtensionMm = s.ExtensionMm,
        ExtHalfWidthMm = s.ExtHalfWidthMm,
        ThickenRadiusMm = s.ThickenRadiusMm,
        ThickenedMm = s.ThickenedMm
    };

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
