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
            r.MinWallStrengthMm = Mechanics.MinWallForStrengthMm(p, plate, s.TSetC);

            // ★ 分清「判不了」与「没通过」（2026-08-21）。
            //   段温落在该牌号蠕变拟合区间外时 AllowMPa 是 NaN —— 这是**对的**，
            //   §7 头一条就是「纯铂外推到 900 °C 得 8648 MPa」，护栏必须在。
            //   错的是旧写法把 NaN 一路折成「利用率 999 ⇒ ✗ 强度」，
            //   把「没有数据可判」说成了「强度不够」。见 SegmentResult.Unknown。
            r.Unknown = !g.InCreepRange(s.TSetC) || double.IsNaN(r.AllowMPa) || r.AllowMPa <= 0;

            // 质量：用该牌号的密度
            double area = Math.PI * (s.WallMm * 1e-3) * (s.TubeIdMm * 1e-3 + s.WallMm * 1e-3);
            r.MassG = area * (s.LengthMm * 1e-3) * g.DensityKgM3 * 1000.0;
            r.CostRelative = r.MassG * g.CostPerKgRelative;

            if (r.Unknown)
            {
                // 利用率留 NaN 而不是塞 999：**假数字比空着更坏** ——
                // 999 会被当成「超限 999 倍」，而真相是这一格根本没有数。
                r.Utilization = double.NaN;
                r.Feasible = false;                       // 判不了 ⇒ 不算通过
                r.Binding = g.HasCreep
                    ? $"无法判定：{s.TSetC:0} °C 在 {g.Name} 蠕变拟合区间 "
                      + $"{g.CreepTMinC:0}–{g.CreepTMaxC:0} °C 之外"
                    : $"无法判定：{g.Name} 没有蠕变数据";
            }
            else
            {
                r.Utilization = r.VonMisesMPa / r.AllowMPa;
                r.Feasible = r.Utilization <= 1.0;
                r.Binding = r.Utilization > 1.0 ? "强度" : "";
            }
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

    /// <summary>
    /// 《鉑金電氣計算.xlsx》T10/T11 用的共用片电流系数。
    /// **已被拓扑推导取代**（见 <see cref="JointCurrentA"/>），仅保留供对照。
    /// </summary>
    public const double WorkbookSharedFactor = 1.5;

    /// <summary>
    /// 是否沿用工作簿的 1.5 经验系数。默认 false = 用拓扑推导的算术和。
    /// 置 true 可复现工作簿数值，用于对照差异。
    /// </summary>
    public static bool UseWorkbookSharedFactor = false;

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
    /// **由相位关系推导，不再是经验系数**（2026-08-10 用户确认接法）：
    ///
    /// 每段各有独立可控矽，**三台一次侧分别接不同相对（R-S / S-T / T-R）**，
    /// 故相邻两段的线电压相差 **120°**，阻性负载下电流也相差 120°。
    /// 四片法兰标 R/T/R/T，故意不用 S —— 避免共用片两侧出现不同相对而成为相间短路通路。
    ///
    /// 关键在**符号**：共用法兰处管子是连续的，左侧管流入 I₁、右侧管流出 I₂，
    /// 法兰注入/抽出的是**两者之差**（不是和）：
    ///
    ///   端头片（j=0 或 j=n）：I = 相邻那一段的电流
    ///   共用片：**I = |I₂ − I₁| = √(I₁² + I₂² − 2I₁I₂cos120°) = √(I₁² + I₂² + I₁I₂)**
    ///
    /// 等电流时 = **√3·I ≈ 1.732·I**。
    ///
    /// 《鉑金電氣計算.xlsx》T10/T11 的 (I₁+I₂)/2 × 1.5 就是这个 √3 的近似，**低 13 %**。
    /// 置 <see cref="UseWorkbookSharedFactor"/> = true 可复现工作簿数值作对照。
    ///
    /// **不是电磁感应**：铂在 1100 °C、50 Hz 的趋肤深度约 48 mm，而板厚仅 2 mm（无集肤效应）；
    /// 回路 X/R ≈ 0.17（对有效值影响 1.5 %）；板内涡流约 0.1 A/mm²（相对工作值约 1 %）。
    /// 三者合计不到 3 %，撑不起 1.5 —— 该系数的来源是相位矢量合成。
    ///
    /// ⚠ 二阶效应未计：相控触发角不同会使基波电流相对电压各自滞后不同角度，
    ///   实际相位差偏离 120°。三段功率差别不大时影响有限，需要更准则须做波形叠加。
    /// </summary>
    public static double JointCurrentA(IReadOnlyList<double> segmentCurrentA, int j)
    {
        int n = segmentCurrentA.Count;
        if (j <= 0) return segmentCurrentA[0];
        if (j >= n) return segmentCurrentA[n - 1];
        double il = segmentCurrentA[j - 1], ir = segmentCurrentA[j];
        return UseWorkbookSharedFactor
            ? (il + ir) / 2.0 * WorkbookSharedFactor       // 工作簿口径，仅供对照
            : Math.Sqrt(il * il + ir * ir + il * ir);      // 120° 相位差下的矢量差
    }

    /// <summary>
    /// 第 <paramref name="joint"/> 号接头那一片的厚度，以及**是被哪一段的需求定的**。
    ///
    /// 折算依据：深度平均下面电流 K = J·t 守恒 ⇒ J ∝ I/t；定尺后 J = J_allow
    /// ⇒ **t ∝ I**，于是 t_接头 = t_段 × (I_接头 / I_段)。
    /// **共用片要同时满足两侧，取两者较大值。**
    ///
    /// ★ 为什么抽出来（2026-08-24）：这段「取两侧较大」是纯算术，
    ///   可它原来内联在 <see cref="SizeFlanges"/> 里，而那个方法**每段要跑一次耦合解**
    ///   （约 30 s/段）⇒ 想验「两侧竞争」就得跑 ≥2 段。于是它一直**零覆盖**：
    ///   单段线上两端都是端片，内层循环每次只有一个合法的 k，
    ///   `if (tk > t)` 这个比较**一次都没执行过**。
    ///   抽出来之后微秒级就能验完，而慢的那半（耦合解算出 need/amps）单段测试已经覆盖。
    ///
    /// ⚠ 判错的后果不是「数字略差」：共用片被**较弱那一侧**定厚 ⇒ 偏薄
    ///   ⇒ 它违反的恰恰是定尺寸本来要满足的那条判据；而 SizedBy 会报错段名，
    ///   工程师照它去改**另一段**。
    ///
    /// <param name="needMm">各段自身要求的法兰厚度</param>
    /// <param name="ampsA">各段电流；某段解失败时为 0 ⇒ 该侧不做折算，直接用它的 need</param>
    /// </summary>
    public static (double ThicknessMm, string SizedBy) JointThickness(
        IReadOnlyList<double> needMm, IReadOnlyList<double> ampsA,
        IReadOnlyList<string> segNames, int joint)
    {
        int n = needMm.Count;
        if (n == 0 || ampsA.Count != n || segNames.Count != n)
            throw new ArgumentException(
                $"段数对不上：need {needMm.Count} / amps {ampsA.Count} / 名字 {segNames.Count}");

        double iJoint = JointCurrentA(ampsA, joint);
        double t = 0; string by = "";
        for (int k = joint - 1; k <= joint; k++)      // 左邻段、右邻段
        {
            if (k < 0 || k >= n) continue;            // 端片只有一侧
            double tk = ampsA[k] > 0 ? needMm[k] * (iJoint / ampsA[k]) : needMm[k];
            if (tk > t) { t = tk; by = segNames[k]; } // ★ 共用片取较大者
        }
        return (t, by);
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
            // 折算与「共用片取较大」只有一处实现：JointThickness（那里可以微秒级验）
            var (t, by) = JointThickness(need, amps, segs.Select(x => x.Name).ToList(), j);

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
        ThickenedMm = s.ThickenedMm,
        // R48（2026-09-14，Opus 5）：逐片圆盘保温跟着板件走 —— 漏抄它，CoupledSolver 里本片就退回整线值（手抄清单的老病，其余没抄的字段本批未动）
        DiscInsulThickMm = s.DiscInsulThickMm,
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
