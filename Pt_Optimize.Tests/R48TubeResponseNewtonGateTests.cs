using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 E 审查修改（2026-09-15 Opus 5）：**管侧响应的两种解法到的是同一个不动点** —— 保温搜索量 γ（管根对本片抽热的响应）改用
/// LineRunner.SolveTubeWithDrawsNewton（段间端温牛顿弦法），不用无法兰基线那份欠松弛迭代 LineRunner.SolveTubeWithDraws。
/// 起因（一次性实测，scratch，同设置）：共用片 ±2 W 扰动时欠松弛迭代 60 轮（每轮全部段解一次、约 1.7 s）按 ×25 口径的判收敛量停在 0.9～1.2 K；端片 1 轮就过 0.25 K。
///
/// ══ 公共设置（跑前定下）
///   几何 W08、板厚 0.73/1.26/1.26/0.73、环倍率 1，管保温 7.5 mm（ApplySectionFloor 重算舌片厚与下角），圆盘 10.5/7.5/7.5/11.0 mm、舌 7.0/3.0/3.5/9.0 mm
///   （= 旧小规模一跑 7.5 mm 层的终点，只作一个有代表性的非均匀工作点）；共用片抽热两侧各半；整线网格 = 保温搜索的导航档配方（Solver.ApplyCaseMesh，细区半径统一）；
///   耦合容差 0.25 K。两态各解一次整线，在其收敛态上：每片 ±2 W，牛顿（雅可比差分步 1 K、最多 10 步、容差 0.25 K）与欠松弛迭代（最多 300 轮、容差 0.25 K）各解一次。
///
/// ══ 第一版（输出 deliverable\R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt，10.9 分）跑前判读 N0～N4 与结果（原样记，不改判）：
///   N0 复现：两态逐位相同 ✓。N1 牛顿收敛：16 个全部 ≤ 2 次段解 ✓（当时牛顿的距离估计也是「真残差 × 25」）。
///   N2 同一不动点（|管根_牛顿 − 管根_迭代| ≤ 0.25 + max(0.25, 迭代判收敛量)）：**否** —— 空管两块共用片差 0.70～0.73 K，界 0.55～0.56 K；
///     带玻璃共用片差 0.32～0.34 K（界 0.5，过）；端片逐位相同。按当时写死的判读，这一版**不能**证明两者同一不动点。
///   N3 γ：端片牛顿与迭代逐位相同（带玻璃 2.4688／2.5251、空管 4.2151／4.1377 K/W）；共用片 带玻璃 牛顿 1.239／1.236 对迭代 1.071／1.076（迭代已按自己口径收敛），
///     空管 牛顿 2.086／2.072 对迭代 1.725／1.720（迭代没收敛）。N4 成本：牛顿 49 s 对迭代 3839 s。
///   跑后查因（假设，由第二版验）：两种解法的差在 ± 两个扰动上等值反号、都是迭代「落后」于牛顿 —— 像是迭代没走完，而不是另一个不动点；
///     第一版印出的雅可比非对角 0.972～0.984，放大 1/(1−g) ≈ 36～61 倍，而两边的距离估计都按 25 倍算 ⇒ 迭代「判收敛量 0.30 K」实际离不动点约 0.3/25×61 ≈ 0.73 K，与观测差吻合。
///   ⇒ 求解器改为用量出的雅可比估距离（TubeNewtonResult.DistanceK = ‖(I − J)⁻¹ 真残差‖），第二版在同一设置上用这个估计重判。
///
/// ══ 第二版（输出 deliverable\R48_保温搜索_管侧响应_两种解法对拍_第二版_2026-09-15.txt）跑前写死的判读（2026-09-15 Opus 5）
///   M0 复现：不扰动时管侧单解管根与整线逐位相同（断言）。
///   M1 牛顿收敛：16 个扰动解全部「距离估计 ‖(I − J)⁻¹ r‖ &lt; 0.25 K」且段解 ≤ 11 次（断言）。
///   M2 放大倍数：印两态 ‖(I − J)⁻¹‖∞，以及两种解法各自的真残差 ‖G(x) − x‖（都由生产段解直接算，不经雅可比）。
///   M3 同一不动点：每个扰动解，‖x_牛顿 − x_迭代‖∞ ≤ 1.5 × (距离_牛顿 + 距离_迭代) + 0.05 K，距离都 = ‖(I − J)⁻¹ 真残差‖∞
///      （1.5 倍容雅可比差分误差与非线性，0.05 K 容段解噪声；⚠ 两个数无出处）。全过 ⇒「同一不动点，迭代只是没走完」；有不过 ⇒「不在同一不动点或线性距离估计不成立」，断言失败。
///   M4 γ：逐片印两种解法的各端 γ 与差；另印「按迭代自身距离估计修正后」不做（不引入新配方），只印。
///
/// ══ 第二版跑后（2026-09-15 Opus 5，deliverable\R48_保温搜索_管侧响应_两种解法对拍_第二版_2026-09-15.txt，11.0 分；判读原样按上面执行，这里只记结果）
///   M0 两态逐位相同。M2 ‖(I − J)⁻¹‖∞ 带玻璃 36.6、空管 61.7（整线常数 25）。M1 是（端片 1 次段解、共用片 2 次）。
///   M3 是：共用片 ‖x_牛顿 − x_迭代‖ 带玻璃 0.32～0.35 K、空管 0.71～0.74 K，迭代自身按雅可比估的距离 0.36 K／0.72～0.74 K，与实差吻合 ⇒ 同一不动点、迭代没走完；端片逐位相同。
///   M4：端片 γ 两者逐位相同；共用片 牛顿 带玻璃 1.236～1.239、空管 2.072～2.086 K/W，迭代 1.071～1.076（它自己按 ×25 判收敛了）／1.720～1.725 ⇒ 迭代低 14 %／17 %。
///   成本：牛顿 49 s 对迭代 3838 s。
///   ⚠ 顺带暴露（不在本路范围）：无法兰基线用的正是这份 ×25 口径的欠松弛迭代，共用片处它「判收敛」时离不动点可能还有 1.5～2.5 倍容差（放大实为 36.6～61.7 而非 25） —— 列进交接的待查项。
///
/// ══ ⚠ 2026-09-15 Opus 5（合并）：上面两版的记录全部取自 r48_E 工作树（底板 f206e70），即 **F 合入前的配方**：压接按形心整格、3·hFine 自相似细带；
///   散热表上限「设定 + 200 K、60 节点」（合并树已改为铂熔点上限）；不计空管管腔轴向辐射（G3 默认系数 0.023）。
///   合并树里整线走压接面上定温、缺省不铺细带、铂熔点散热表、空管计管腔辐射 ⇒ 数（γ、雅可比、成本）不能拿来当合并树的结论；
///   判读 M0～M4 本身不依赖这些数。合并后**未重跑**；输出文件同名已存在时本门拒跑（不覆盖），要在合并树上重跑须换新文件名并在此重记。
/// </summary>
[Trait("速度", "慢")]
public class R48TubeResponseNewtonGateTests
{
    private readonly ITestOutputHelper _out;
    public R48TubeResponseNewtonGateTests(ITestOutputHelper o) { _out = o; }

    private static double[] Slots((double L, double R)[] nb, (int Seg, bool Left)[] slots) => slots.Select(s => s.Left ? nb[s.Seg].L : nb[s.Seg].R).ToArray();

    [Fact]
    public void 管侧响应第二版_牛顿与欠松弛迭代同一不动点_距离按量出的雅可比估()
    {
        string file = Path.Combine(HandoverDoc.Root(), "deliverable", "R48_保温搜索_管侧响应_两种解法对拍_第二版_2026-09-15.txt");
        if (File.Exists(file)) throw new InvalidOperationException("输出文件已存在 —— deliverable 下已有文件不许覆盖");
        var sb = new StringBuilder();
        var total = Stopwatch.StartNew();
        object fl = new();
        void Say(string s)
        {
            lock (fl)
            {
                string line = $"[{total.Elapsed.TotalMinutes,6:0.0} 分] {s}";
                _out.WriteLine(line); sb.AppendLine(line);
                try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }
        Say("R48 保温搜索·管侧响应两种解法对拍·第二版（Opus 5，2026-09-15）　出处 Pt_Optimize.Tests/R48TubeResponseNewtonGateTests.cs　判读见类注释 M0～M4（跑前写死）");

        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.TubeInsulMm = 7.5;
        Solver.ApplySectionFloor(d, p, new SolverOptions(), new SolverResult(), null, null);
        d.DiscInsulMm = new[] { 10.5, 7.5, 7.5, 11.0 };
        d.TabInsulMm = new[] { 7.0, 3.0, 3.5, 9.0 };
        double radius = MeshVerify.RequiredMeshFor(d).RadiusMm;
        const double dq = 2.0, tol = 0.25;
        LineCase NewCase(int s)
        {
            var c = d.BuildCase(p, checkRamp: false, emptyTube: s == 1, emptyTubeSetpoint: EmptyTubeSetpoint.RampTarget);
            Solver.ApplyCaseMesh(c, new SolverOptions { FineMm = 0, FineRadiusMm = radius });
            c.CoupleTolK = tol; c.CoupleMaxRounds = 4000;
            return c;
        }
        var lines = new LineResult[2]; var cases = new LineCase[2];
        Parallel.For(0, 2, s => { cases[s] = NewCase(s); lines[s] = LineRunner.Run(cases[s]); });
        for (int s = 0; s < 2; s++)
            Assert.True(lines[s].Ok && lines[s].Converged, $"{InsulationSearch.StateNames[s]}整线没解出：{lines[s].Message}");
        Say($"整线两态解完（{total.Elapsed.TotalSeconds:0} s）；细区半径 {radius:0.0} mm");

        int nSeg = lines[0].Segments.Length, nPl = lines[0].Flanges.Length;
        var jobs = (from s in Enumerable.Range(0, 2) from j in Enumerable.Range(0, nPl) from sg in new[] { +1, -1 } select (S: s, J: j, Sg: sg)).ToArray();
        var newton = new LineRunner.TubeNewtonResult[jobs.Length];
        var picard = new LineRunner.NeighbourIteration[jobs.Length];
        var tN = new double[jobs.Length]; var tP = new double[jobs.Length];
        var jac = new ((int Seg, bool Left)[] Slots, double[,] J)[2];
        var amp = new double[2];
        var draws = new (double L, double R)[2][]; var nbs = new (double L, double R)[2][];
        for (int s = 0; s < 2; s++)
        {
            draws[s] = cases[s].WarmStart.Take(nSeg).Select(a => (a[0], a[1])).ToArray();
            nbs[s] = cases[s].WarmStart.Take(nSeg).Select(a => (a[2], a[3])).ToArray();
            var sw = Stopwatch.StartNew();
            var (slots, J, g0) = LineRunner.NeighbourJacobian(NewCase(s), draws[s], nbs[s], 1.0);
            jac[s] = (slots, J);
            amp[s] = LineRunner.NeighbourAmplification(J);
            bool same = g0.Ok && Enumerable.Range(0, nSeg).All(i => g0.Segments[i].TRootAC.Equals(lines[s].Segments[i].TRootAC) && g0.Segments[i].TRootBC.Equals(lines[s].Segments[i].TRootBC));
            Say($"M0 {InsulationSearch.StateNames[s]}：不扰动管侧单解复现整线管根 {(same ? "逐位相同" : "对不上 ⇒ 本对拍作废")}（段0 A 端 {g0.Segments[0].TRootAC:R} / {lines[s].Segments[0].TRootAC:R}）；雅可比 {slots.Length}×{slots.Length}、{sw.Elapsed.TotalSeconds:0.0} s："
              + string.Join(" | ", Enumerable.Range(0, slots.Length).Select(r => string.Join(" ", Enumerable.Range(0, slots.Length).Select(q => J[r, q].ToString("+0.0000;-0.0000"))))));
            Say($"M2 {InsulationSearch.StateNames[s]}：‖(I − J)⁻¹‖∞ = {amp[s]:0.0}（整线外层耦合常数 FixedPointAmp = {LineCase.FixedPointAmp}）");
            Assert.True(same, "M0：管侧单解没有逐位复现整线管根");
        }
        Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, k =>
        {
            var (s, j, sg) = jobs[k];
            var c1 = NewCase(s);
            var pert = InsulationSearch.PerturbPlateDraw(c1, draws[s], j, sg * dq);
            var sw = Stopwatch.StartNew();
            newton[k] = LineRunner.SolveTubeWithDrawsNewton(c1, pert, nbs[s], jac[s], tol, 10);
            tN[k] = sw.Elapsed.TotalSeconds;
            sw.Restart();
            picard[k] = LineRunner.SolveTubeWithDraws(NewCase(s), pert, nbs[s], tol, 300);
            tP[k] = sw.Elapsed.TotalSeconds;
            Say($"   {InsulationSearch.StateNames[s]} 片{j} {(sg > 0 ? "+" : "−")}{dq} W：牛顿 段解 {newton[k].Evaluations} 次、真残差 {newton[k].ResidualK:0.00000} K、距离估计 {newton[k].DistanceK:0.0000} K、{tN[k]:0.0} s；"
              + $"欠松弛迭代 {picard[k].Rounds} 轮、判收敛量（×25 口径）{picard[k].JudgeK:0.0000} K、{tP[k]:0} s");
        });

        bool m1 = true, m3 = true;
        for (int s = 0; s < 2; s++)
        {
            var slots = jac[s].Slots;
            int m = slots.Length;
            var a = new double[m, m];
            for (int r = 0; r < m; r++) for (int q = 0; q < m; q++) a[r, q] = (r == q ? 1.0 : 0.0) - jac[s].J[r, q];
            for (int j = 0; j < nPl; j++)
            {
                var wp = InsulationSearch.WorkPointOf(lines[s], cases[s], s, j);   // 2026-09-15 Opus 5（合并）：工作点多了整线算例参数（限值从它读），端号不变
                int kp = Array.FindIndex(jobs, t => t.S == s && t.J == j && t.Sg > 0), km = Array.FindIndex(jobs, t => t.S == s && t.J == j && t.Sg < 0);
                double[] Roots(LineResult r) => wp.Ends.Select(e => e.AEnd ? r.Segments[e.Seg].TRootAC : r.Segments[e.Seg].TRootBC).ToArray();
                foreach (int k in new[] { kp, km })
                {
                    string tag = $"{InsulationSearch.StateNames[s]} 片{j} {(jobs[k].Sg > 0 ? "+" : "−")}";
                    if (!(newton[k].Converged && newton[k].Evaluations <= 11)) { m1 = false; Say($"M1 ✗ {tag}：牛顿没收敛 {newton[k].Message}"); }
                    if (newton[k].Last is not { Ok: true } || picard[k].Last is not { Ok: true } || newton[k].LastInput is null || picard[k].LastInput is null)
                    { m3 = false; Say($"M3 ✗ {tag}：有一方没有解或没有末轮输入，比不了"); continue; }
                    var xN = Slots(newton[k].LastInput!, slots);
                    var xP = Slots(picard[k].LastInput!, slots);
                    var rP = Slots(LineRunner.NeighbourTempsOf(picard[k].Last!.Segments), slots).Zip(xP, (g, x) => g - x).ToArray();
                    var dP = LineRunner.SolveDense(a, rP);
                    double distP = dP is null ? double.PositiveInfinity : dP.Max(v => Math.Abs(v));
                    double gap = xN.Zip(xP, (u, v) => Math.Abs(u - v)).Max();
                    double bound = 1.5 * (newton[k].DistanceK + distP) + 0.05;
                    bool ok = gap <= bound;
                    if (!ok) m3 = false;
                    var rn = Roots(newton[k].Last!); var rp = Roots(picard[k].Last!);
                    Say($"M3 {tag}：‖x_牛顿 − x_迭代‖ {gap:0.0000} K；迭代真残差 {rP.Max(v => Math.Abs(v)):0.00000} K、距离估计 {distP:0.0000} K；牛顿距离估计 {newton[k].DistanceK:0.0000} K；界 {bound:0.0000} K {(ok ? "" : "✗")}"
                      + $"　本片各端管根 牛顿 {string.Join("/", rn.Select(v => v.ToString("0.0000")))}、迭代 {string.Join("/", rp.Select(v => v.ToString("0.0000")))}");
                }
                if (newton[kp].Last is { Ok: true } && newton[km].Last is { Ok: true } && picard[kp].Last is { Ok: true } && picard[km].Last is { Ok: true })
                {
                    var gN = Roots(newton[kp].Last!).Zip(Roots(newton[km].Last!), (x, y) => InsulationSearch.TubeSlopeKPerW(x, y, dq)).ToArray();
                    var gP = Roots(picard[kp].Last!).Zip(Roots(picard[km].Last!), (x, y) => InsulationSearch.TubeSlopeKPerW(x, y, dq)).ToArray();
                    Say($"M4 {InsulationSearch.StateNames[s]} 片{j}：γ 各端 牛顿 {string.Join("/", gN.Select(v => v.ToString("0.0000")))}、迭代 {string.Join("/", gP.Select(v => v.ToString("0.0000")))} K/W、差 {string.Join("/", gN.Zip(gP, (x, y) => (x - y).ToString("+0.0000;-0.0000")))}"
                      + $"（迭代 {(picard[kp].Converged && picard[km].Converged ? "按 ×25 口径收敛" : "按 ×25 口径也没收敛")}）");
                }
            }
        }
        Say($"M1 牛顿全部收敛（距离估计 < {tol} K）且段解 ≤ 11 次：{(m1 ? "是" : "否")}");
        Say($"M3 同一不动点（‖x_牛顿 − x_迭代‖ ≤ 1.5 × 两边距离估计之和 + 0.05 K）：{(m3 ? "是 ⇒ 同一不动点，迭代只是没走完" : "否 ⇒ 不在同一不动点或线性距离估计不成立")}");
        Say($"成本：牛顿合计 {tN.Sum():0} s（段解 {newton.Sum(r => r.Evaluations)} 次 + 雅可比两态各 {jac[0].Slots.Length + 1} 次），欠松弛迭代合计 {tP.Sum():0} s（段解 {picard.Sum(r => r.Rounds)} 次）；总 {total.Elapsed.TotalMinutes:0.0} 分");
        Assert.True(m1, "M1：牛顿有扰动解没收敛");
        Assert.True(m3, "M3：两种解法不在同一不动点（或线性距离估计不成立）");
    }
}
