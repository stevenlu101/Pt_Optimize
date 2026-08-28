using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PtOptimize.Core;

/// <summary>
/// **网格无关复核** —— 流水线的**最后一段**（2026-08-28）。
///
/// ★★ 用户 2026-08-28 要的流水线，一句话：
/// <code>
///   输入（3DM / UI 输入框）→ 优化（粗网格导航）→ **网格无关复核** → 报告 + 出图
/// </code>
///   「**我不要设计记录这种模式（这坑太大），要严格遵守第一性原理**」。
///   ⇒ 本类**不是**为了产出一个「设计记录」。它就是**这一次运行的判据以什么为准**。
///     跑完即有结果与图纸，没有谁需要去「落档」。
///
/// ★★ 为什么非要分两级（这是实测逼出来的，不是设计偏好）
///
///   `--meshadapt` 实测：网格无关要到 **0.408 mm**，单次求解 **910 秒**。
///   而 D8 一个形状 40 轮、搜形状几十个形状 ⇒ **在网格无关的网格上做优化，算不完**。
///
///   ⇒ ① 优化用粗网格**导航**（快，找方向与形状）；
///     ② 最终解在网格无关的网格上**复核一次**（慢，只跑一次）；
///     ③ **判据以复核为准** —— 优化过程中的判据值只是导航，**不是结论**。
///
///   ★ APP 此前**只有第 ① 步**，并把第 ① 步的判据值当成了结论。
///     实测后果：那两个历史档记的 ③ 是 5.182/10（看着 48 % 裕度），
///     而网格无关值是 **10.539/10 —— 不过**。
///     几何没问题（铂重 3548 vs 3547 逐位对上），**错的是判定**：
///     2 mm 网格连舌根圆角、环宽、焊脚都画不出来（1.5 / 1.5 / 1.2 格）。
///
/// ⚠ 本类**只复核，不优化**。它不改设计，只回答一句：
///   「这个设计的判据，在算得准的网格上是多少、过不过。」
/// </summary>
public static class MeshVerify
{
    public sealed class Result
    {
        /// <summary>复核停在哪个细网格 mm 上。</summary>
        public double FineMm;
        public int Cells;
        /// <summary>**网格无关的**那一次解 —— 判据以它为准，不是以导航网格那次为准。</summary>
        public LineResult? Line;
        public bool Converged;
        public bool HitCellCap;
        /// <summary>最后两档之间每条判据动了多少。</summary>
        public List<MeshAdapt.Delta> LastDeltas = new();
        /// <summary>一句话结论。**没收敛必须明说**，不许含糊。</summary>
        public string Verdict = "";
        public double SecondsTotal;
        /// <summary>逐档轨迹，供报告打印。</summary>
        public readonly List<(double Fine, int Cells, double N2p, double N2pp, double N3, double MassG, double Sec)>
            Trace = new();
    }

    /// <summary>每条判据的复核容差 —— 与 `--selfcheck` 的对账容差**同口径**，不另立一套。</summary>
    public static IReadOnlyList<MeshAdapt.Delta> TolTemplate() => new[]
    {
        new MeshAdapt.Delta { Name = "②′", Tol = 0.5 },
        new MeshAdapt.Delta { Name = "②″", Tol = 0.2 },
        new MeshAdapt.Delta { Name = "③",  Tol = 1.0 },
    };

    /// <summary>
    /// 对一个**已经算出来的设计**做网格无关复核。
    ///
    /// 起点由几何特征定（最小特征 ÷ 3），每轮对半加密，
    /// 停机由**判据本身**定（每条判据的变化落进各自容差），或撞上单元数上限。
    /// </summary>
    public static Result Run(DesignSpec d, DesignInputs baseIn,
                             int maxCells = 40000, int maxRounds = 6,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        var res = new Result();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        double weldLeg = Math.Max(d.TabThickMm.Max(), d.WallMm);
        double h = MeshAdapt.RequiredFineMm(new[] { d.TabFilletMm, d.RingWidthMm, weldLeg });
        double radius = MeshAdapt.RequiredFineRadiusMm(
            new[] { d.DiscRadiusMm, Math.Abs(d.TabLengthMm) * 0.35 }, d.HoleRadiusMm);

        (double n2p, double n2pp, double n3, double m)? prev = null;
        for (int it = 0; it < maxRounds; it++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report($"网格无关复核：{h:0.000} mm（第 {it + 1} 档）…");
            var lc = d.BuildCase(baseIn, checkRamp: true);
            lc.MeshFineMm = h;
            lc.MeshFineRadiusMm = radius;
            var swOne = System.Diagnostics.Stopwatch.StartNew();
            var r = LineRunner.Run(lc, null, cancel);
            swOne.Stop();
            if (!r.Ok)
            {
                res.Verdict = $"✗ 复核在 {h:0.000} mm 上解不出来：{r.Message}"
                            + "　⇒ **不能说这个设计过了** —— 算不出来不等于通过";
                res.SecondsTotal = sw.Elapsed.TotalSeconds;
                return res;
            }

            double V(string k) => r.Checks
                .FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            double a2p = V(LineResult.Key.NetFlux), a2pp = V(LineResult.Key.DiscTemp), a3 = V(LineResult.Key.FlangeDip);
            double mass = r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);

            res.Line = r; res.FineMm = h; res.Cells = r.MeshCells;
            res.Trace.Add((h, r.MeshCells, a2p, a2pp, a3, mass, swOne.Elapsed.TotalSeconds));

            if (prev is { } pv)
            {
                var tol = TolTemplate();
                res.LastDeltas = new List<MeshAdapt.Delta>
                {
                    new() { Name = tol[0].Name, Change = a2p - pv.n2p,   Tol = tol[0].Tol },
                    new() { Name = tol[1].Name, Change = a2pp - pv.n2pp, Tol = tol[1].Tol },
                    new() { Name = tol[2].Name, Change = a3 - pv.n3,     Tol = tol[2].Tol },
                };
                if (MeshAdapt.Converged(res.LastDeltas)) { res.Converged = true; break; }
            }
            prev = (a2p, a2pp, a3, mass);
            if (r.MeshCells > maxCells) { res.HitCellCap = true; break; }
            h = MeshAdapt.Refine(h);
        }

        res.SecondsTotal = sw.Elapsed.TotalSeconds;
        res.Verdict = MeshAdapt.Verdict(res.FineMm, res.LastDeltas, res.HitCellCap);
        return res;
    }
}
