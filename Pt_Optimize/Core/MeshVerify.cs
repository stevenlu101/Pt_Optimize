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

        /// <summary>
        /// **②″ 的峰落在粗区**时的原话（null = 没这回事）。
        /// 分区加密（A⑭）之后必须查：峰若在粗区，②″ 会被静默算低 ——
        /// 而算低了**不会报错**，只会给一个看着正常的数。调用方必须原样呈现。
        /// </summary>
        public string? PeakOutsideFine;

        /// <summary>
        /// **中带确认**：收敛之后额外做一次「中带也加密」的对照，看判据动不动。
        /// null = 没做；否则是该原样呈现的一句话。
        ///
        /// 为什么必须有它：只加密内带的话，判据可能收敛到一个**由中带的粗糙度决定**的
        /// 错值上 —— 而「内带加密判据不动」这个证据**看不出**这件事。
        /// </summary>
        public string? MidBandConfirm;
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
    /// <summary>
    /// **这个几何要多细的网格** —— 由几何特征算出，不是挑的数。
    ///
    /// ★ 全程序只有这一份（2026-08-28 提出来）：<see cref="Solver"/> 的第二遍求根
    ///   与本类的复核**必须用同一张网格**，否则「求根的网格」与「判决的网格」不是同一个，
    ///   求出来的根照样不作数 —— 而那正是 A⑬ 要修的病。
    /// </summary>
    /// <param name="weldAsGeometricFeature">
    /// 焊脚算不算「**几何特征**」（2026-08-29 提出来的问题，默认 true = 保持历史口径）。
    ///
    /// ★ 「每个特征 3 格」这条规则是给**几何特征**用的，而这三样不是一类东西：
    /// <code>
    ///   舌根圆角 R3 —— **几何边界**（真实轮廓曲线）      ⇒ 格子要跨过它
    ///   环宽        —— 厚度的**阶跃**（不连续）         ⇒ 格子要定位那个跳变
    ///   焊脚        —— **平滑的厚度斜坡**（凹圆弧函数）  ⇒ 只要积得准，不需要分得开边
    /// </code>
    /// 代码自己写着焊缝是厚度场不是边界：
    /// <c>weld = 2·WeldFilletHeightMm(r − 孔半径, 焊脚)</c>，直接加在 <c>ThicknessAt</c> 上。
    ///
    /// ⇒ 焊脚在表里会把网格拖细：0.6 档实测 weldLeg = 1.75 ⇒ 0.583 mm，
    ///   而圆角/环宽给的是 1.0 mm。**拿掉它，起始网格粗 1.7 倍，每档单元数少约 3 倍。**
    ///
    /// ★★ **2026-08-29 默认已翻为 false（焊脚不算几何特征）。**
    ///
    /// ⚠ 我先前说「必须实测对照才准改默认」——**那句话把守门人认错了**，在此更正：
    ///   本参数**只影响网格无关阶梯从哪一级起步**，**不改变任何给定网格上的物理**：
    ///   同一个 h，加不加焊脚进特征表，算出来的判据**逐位相同**。
    ///   ⇒ 「焊脚出表会不会把判据算偏」这个问法不成立。
    ///     真正的守门人是**收敛判据**：从 1.0 mm 起步照样要一路加密到
    ///     「判据不再变」才算数。**起步粗是少爬两级，不是少验一道。**
    ///
    /// ⚠ 唯一的真风险是通用的、与焊脚无关的：起步粗会让「碰巧两级之间没动」的
    ///   假收敛概率略升 —— 那是收敛判据本身固有的风险。
    ///   `--weldfeature` 可以退回旧口径做对照。
    /// </param>
    public static (double FineMm, double RadiusMm) RequiredMeshFor(DesignSpec d,
                                                                   bool weldAsGeometricFeature = false)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        double weldLeg = Math.Max(d.TabThickMm.Max(), d.WallMm);
        var feats = weldAsGeometricFeature
                  ? new[] { d.TabFilletMm, d.RingWidthMm, weldLeg }
                  : new[] { d.TabFilletMm, d.RingWidthMm };
        return (MeshAdapt.RequiredFineMm(feats),
                MeshAdapt.RequiredFineRadiusMm(
                    new[] { d.DiscRadiusMm, Math.Abs(d.TabLengthMm) * 0.35 }, d.HoleRadiusMm));
    }

    public static Result Run(DesignSpec d, DesignInputs baseIn,
                             int maxCells = 40000, int maxRounds = 6,
                             bool weldAsGeometricFeature = false,
                             IProgress<string>? progress = null,
                             CancellationToken cancel = default)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        var res = new Result();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var (h0, radius) = RequiredMeshFor(d, weldAsGeometricFeature);
        // ★★ 按特征分区（A⑭，2026-08-29）：**只加密内带**。
        //   此前是把「按最小特征（焊脚）定的极细尺寸」铺满「按最大特征（盘径/舌长）定的大区域」，
        //   每加密一档单元数 ×4 —— 0.6 档实测收到 0.146 mm 时约 8 万单元，
        //   **超过 maxCells 上限，跑了八小时没出数**。
        //   现在：中带（盘 + 舌根）固定在特征尺寸 h0 上；内带（孔 + 焊脚那一圈）逐档减半。
        //   ⚠ 这么做的合法性由**本循环自己**检验：判据不再变才算网格无关；
        //     并在收敛后**额外做一次「中带也加密」的确认**（见下面 confirm）。
        double weldLegV = Math.Max(d.TabThickMm.Max(), d.WallMm);
        double innerR = MeshAdapt.InnerRadiusFor(d.HoleRadiusMm, weldLegV);
        double hMid = h0;
        double h = h0;

        (double n2p, double n2pp, double n3, double m)? prev = null;
        for (int it = 0; it < maxRounds; it++)
        {
            cancel.ThrowIfCancellationRequested();
            // ★ 长任务必须说清楚「这一档有多大、大概要多久」。
            //   不说的话，人会把一个正常的几小时任务当死机 kill 掉（实测我自己就差点这么干）。
            //   单元数 ∝ 1/h²，每加密一档约 ×4；用**上一档的实测耗时**推下一档，
            //   比拍脑袋准 —— 而且推错了下一档会自己纠正。
            string eta = "";
            if (res.Trace.Count > 0)
            {
                var lastT = res.Trace[^1];
                // ★★ **按单元数线性外推是错的**（2026-08-29 实测推翻我自己的公式）。
                //   实测 0.6 档：单元数只涨约 6 倍，而**每轮外层耦合的成本涨了约 96 倍**
                //   （第 1 档 ~30 s/轮 → 第 3 档 ~48 分/轮）—— 细网格上内层线性解的
                //   迭代次数也在涨，条件数变差。⇒ 时间对单元数是**超线性**的。
                //
                //   ⇒ 有两档实测时就**用实测的耗时比**外推（它自带超线性）；
                //     只有一档时才退回「单元数 ×4」那个**下界**，并说明它偏乐观。
                //   ⚠ 这条以前看不见，因为档内是静默的 —— 是 2026-08-29 补进度提示后
                //     挣来的第一个发现。
                double grow;
                string how;
                if (res.Trace.Count >= 2 && res.Trace[^2].Sec > 1e-9)
                {
                    grow = lastT.Sec / res.Trace[^2].Sec;      // 实测的档间耗时比，自带超线性
                    how = $"按上两档实测耗时比 ×{grow:0.#}";
                }
                else
                {
                    grow = lastT.Fine > 0 ? (lastT.Fine / h) * (lastT.Fine / h) : 4.0;
                    how = $"按单元数 ×{grow:0.#}（**偏乐观** —— 实测耗时涨得比单元数快得多）";
                }
                // ⚠ ×{grow} 是**整体加密**的增长率，而分区之后只有内带在加密
                //   ⇒ 这个数是**上界**，不是估计。说成「预计」会让人以为它准，
                //     而实际往往快得多 —— 报一个偏高的数比报一个偏低的好，
                //     但**必须说清楚它是上界**，否则人会拿它当依据去排时间。
                eta = $"（{how}，上一档 {ThrottledProgress.Fmt(TimeSpan.FromSeconds(lastT.Sec))}"
                    + $" ⇒ 本档估 **~{ThrottledProgress.Fmt(TimeSpan.FromSeconds(lastT.Sec * grow))}**）";
            }
            progress?.Report($"网格无关复核：{h:0.000} mm（第 {it + 1} 档）{eta}…");
            var lc = d.BuildCase(baseIn, checkRamp: true);
            lc.MeshFineMm = hMid;                 // 中带：固定在特征尺寸
            lc.MeshFineRadiusMm = radius;
            lc.MeshInnerMm = h;                   // 内带：逐档减半的就是它
            lc.MeshInnerRadiusMm = innerR;
            var swOne = System.Diagnostics.Stopwatch.StartNew();
            // ★ 内层**一直在报**（外层耦合 n/600、段 i/n），此前这里传 null 把它全扔了。
            //   限流转发：内层一秒可能报几十条，全转会把日志淡掉（淡掉 = 等于没报）。
            var inner = new ThrottledProgress(progress, 20, $"     · {h:0.000} mm ");
            var r = LineRunner.Run(lc, inner, cancel);
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

            // ★ 峰位落在粗区就会被静默算漏 —— 每档核对一次
            double peakR = r.Flanges.Length == 0 ? double.NaN
                         : r.Flanges.Select(f => f.DiscMaxRMm)
                            .Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
            string? peakBad = MeshAdapt.PeakVerdict(peakR, innerR, radius);
            if (peakBad is not null) { progress?.Report("   " + peakBad); res.PeakOutsideFine = peakBad; }

            res.Line = r; res.FineMm = h; res.Cells = r.MeshCells;
            res.Trace.Add((h, r.MeshCells, a2p, a2pp, a3, mass, swOne.Elapsed.TotalSeconds));
            progress?.Report($"网格无关复核：{h:0.000} mm 完成 —— {r.MeshCells} 单元，用时 {ThrottledProgress.Fmt(swOne.Elapsed)}（累计 {ThrottledProgress.Fmt(sw.Elapsed)}）");

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

        // ★★ **中带确认**（A⑭ 的安全线）：只加密内带的话，判据可能收敛到一个
        //   **由中带的粗糙度决定**的错值上 —— 而「内带加密判据不动」这个证据
        //   **看不出**这件事。所以收敛之后额外做一次「中带也减半」的对照。
        //   代价：一次场解。相对于分区省下的几小时，可以忽略。
        if (res.Line is { Ok: true } && res.Trace.Count > 0)
        {
            try
            {
                progress?.Report($"中带确认：把中带 {hMid:0.000} → {hMid * 0.5:0.000} mm 再算一次，看判据动不动…");
                var lcC = d.BuildCase(baseIn, checkRamp: true);
                lcC.MeshFineMm = hMid * 0.5;
                lcC.MeshFineRadiusMm = radius;
                lcC.MeshInnerMm = res.FineMm;
                lcC.MeshInnerRadiusMm = innerR;
                var rc = LineRunner.Run(lcC, null, cancel);
                if (!rc.Ok) res.MidBandConfirm = "⚠ 中带确认解不出来：" + rc.Message + " ⇒ **这一条没验到**";
                else
                {
                    double W(LineResult x, string k) => x.Checks
                        .FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
                    var tolC = TolTemplate();
                    var dl = new List<MeshAdapt.Delta>
                    {
                        new() { Name = tolC[0].Name, Tol = tolC[0].Tol,
                                Change = W(rc, LineResult.Key.NetFlux)   - W(res.Line, LineResult.Key.NetFlux) },
                        new() { Name = tolC[1].Name, Tol = tolC[1].Tol,
                                Change = W(rc, LineResult.Key.DiscTemp)  - W(res.Line, LineResult.Key.DiscTemp) },
                        new() { Name = tolC[2].Name, Tol = tolC[2].Tol,
                                Change = W(rc, LineResult.Key.FlangeDip) - W(res.Line, LineResult.Key.FlangeDip) },
                    };
                    bool ok = dl.All(x => Math.Abs(x.Change) <= x.Tol);
                    string detail = string.Join("／", dl.Select(x => $"{x.Name} {x.Change:+0.000;-0.000}/{x.Tol:0.###}"));
                    res.MidBandConfirm = ok
                        ? $"✓ 中带确认通过：中带减半后判据变化都在容差内（{detail}）"
                          + " ⇒ **分区没有把判据算偏**。"
                        : $"★★ **中带确认没过**（{detail}）⇒ 只加密内带**不够**："
                          + "判据还受中带粗糙度影响，本次网格无关结论**不成立**。";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { res.MidBandConfirm = "⚠ 中带确认异常：" + ex.Message + " ⇒ **这一条没验到**"; }
            if (res.MidBandConfirm is not null) progress?.Report("   " + res.MidBandConfirm);
        }

        res.SecondsTotal = sw.Elapsed.TotalSeconds;
        return res;
    }
}
