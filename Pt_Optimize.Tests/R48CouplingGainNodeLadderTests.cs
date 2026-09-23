using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P0-3）：**段间耦合增益随管段节点数怎么变** —— 只测，不改停机规则。
///
/// ══ 起因
///   整线外层耦合停机要求「真残差 × LineCase.FixedPointAmp(25)」&lt; 容差（LineRunner.Run 里 ampWorst 那两行），25 = 1/(1−g) 取 g ≈ 0.96（2026-08-15 整线环路增益）。
///   E 路在 r48_E 底板（F 合入前配方）上量过段间端温不动点的雅可比：带玻璃非对角 0.9720～0.9729、空管 0.9834～0.9835（R48_保温搜索_管侧响应_两种解法对拍_2026-09-15.txt 第 3–4 行），
///   放大 ‖(I − J)⁻¹‖∞ 36.6／61.7（第二版）。审查【5b】推理：段间接头导度 = kA/Δx，段端向里看是半无限翅片 kA/ℓt ⇒ 单个接头的增益 g = 1/(1 + Δx/ℓt)，
///   Δx = 段长/(节点数 − 1) ⇒ **节点数越多 g 越近 1、放大越大**（带玻璃预测 201/401/801 节点 0.943／0.971／0.985）。合并树上没量过。
///
/// ══ 做法（跑前写死）
///   算例：B2（与 R48G2RampClampChannelTests B2 同一组输入：W08、板厚 0.73/1.26/1.26/0.73、管保温 7.5、逐片圆盘 10.5/3.5/5/10、逐片舌 5/3.5/5/10.5、各半、BuildCase 原样网格、耦合容差 0.25 K），
///   三种工况：带玻璃；空管到温稳态（控温点全线升温目标，默认管腔系数）；空管到温稳态、管腔系数置 0（与 E 底板无管腔辐射那次同口径）。
///   管段节点数 DesignInputs.Nodes 取 201／401／801（其余不动）。每个组合：LineRunner.Run 解整线；在其收敛态（算例 WarmStart 的抽热与段间端温）上，
///   用新建的同设置算例调 LineRunner.NeighbourJacobian（差分步 1 K），打印：
///   · M0 不扰动那次段解的管根与整线逐位是否相同（不同 ⇒ 这一组的雅可比不在整线收敛态上，只描述）；
///   · 雅可比矩阵、谱半径 ρ(J)（Gelfand：‖J^(2^14)‖∞^(1/2^14)，常数因子带来的相对误差 ≤ 4^(1/16384) − 1 ≈ 8.5e-5）、‖J‖∞（Jacobi 收缩比上界）；
///   · 放大 1/(1 − ρ)、‖(I − J)⁻¹‖∞（LineRunner.NeighbourAmplification），与 FixedPointAmp 的比值（= 按 ×25 停机时真实距离是估计的几倍）；
///   · 闭式：每个接头两侧段各自 g = 1/(1 + Δx/ℓt)（Δx = 该段 SegmentOut.X 节距 mm，ℓt = 该段 SegmentOut.DecayLengthMm）、1/(1 − g)，与量出的非对角元比；
///   · 整线的 CoupleRemainK 与 Notes 里的外层耦合收敛句（整线自己怎么判的）。
///   没有断言、没有门槛（只测）；整线没解出或没收敛的组合印出来、跳过雅可比。
///   输出只写 deliverable\R48_段间耦合增益_节点阶梯_本次开跑于{yyyy-MM-dd_HHmmss}.txt（新文件，不覆盖）。
/// </summary>
[Trait("速度", "慢")]
public class R48CouplingGainNodeLadderTests
{
    private readonly ITestOutputHelper _out;
    public R48CouplingGainNodeLadderTests(ITestOutputHelper o) { _out = o; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static LineCase Case(string state, int nodes)
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true, Nodes = nodes };
        if (state == "空管_管腔系数0") p.TubeCavityRadKA1150WmPerK = 0.0;
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit(); d.SizeTongues(p);
        var lc = state == "带玻璃"
            ? d.BuildCase(p, checkRamp: false)
            : d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.RampTarget);
        lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = 4000;
        return lc;
    }

    /// <summary>谱半径（Gelfand 公式，重复平方 14 次，逐次按 ∞ 范数归一、累计对数）。</summary>
    internal static double SpectralRadius(double[,] j)
    {
        int m = j.GetLength(0);
        if (m == 0) return 0;
        var a = (double[,])j.Clone();
        double logScale = 0;   // 真 J^(2^k) = a · exp(logScale)
        const int K = 14;
        for (int k = 0; k < K; k++)
        {
            var b = new double[m, m];
            for (int r = 0; r < m; r++) for (int q = 0; q < m; q++) { double s = 0; for (int t = 0; t < m; t++) s += a[r, t] * a[t, q]; b[r, q] = s; }
            logScale *= 2;
            double nrm = InfNorm(b);
            if (!(nrm > 0)) return 0;
            for (int r = 0; r < m; r++) for (int q = 0; q < m; q++) b[r, q] /= nrm;
            logScale += Math.Log(nrm);
            a = b;
        }
        return Math.Exp((logScale + Math.Log(InfNorm(a))) / Math.Pow(2, K));
    }

    internal static double InfNorm(double[,] a)
    {
        int m = a.GetLength(0); double best = 0;
        for (int r = 0; r < m; r++) { double s = 0; for (int q = 0; q < a.GetLength(1); q++) s += Math.Abs(a[r, q]); best = Math.Max(best, s); }
        return best;
    }

    [Fact]
    public void 段间耦合增益_B2_三工况_节点201_401_801()
    {
        string file = Path.Combine(HandoverDoc.Root(), "deliverable", $"R48_段间耦合增益_节点阶梯_本次开跑于{DateTime.Now.ToString("yyyy-MM-dd_HHmmss", Inv)}.txt");
        if (File.Exists(file)) throw new InvalidOperationException("输出文件已存在：" + file);
        var sb = new StringBuilder();
        var total = Stopwatch.StartNew();
        object fl = new();
        void Say(string s)
        {
            lock (fl)
            {
                string line = $"[{total.Elapsed.TotalMinutes,6:0.0} 分] {s}";
                _out.WriteLine(line); sb.AppendLine(line);
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
        }
        string root = HandoverDoc.Root();
        Say($"R48 段间耦合增益·节点阶梯（Opus 5，I 路，{DateTime.Now:yyyy-MM-dd HH:mm:ss}）　出处 Pt_Optimize.Tests/R48CouplingGainNodeLadderTests.cs　做法见类注释（跑前写死，只测不判）");
        Say($"仓库 {root}　git HEAD {EvidenceHeader.GitHead(root)}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(root)}　FixedPointAmp = {LineCase.FixedPointAmp.ToString("R", Inv)}");

        var combos = (from st in new[] { "带玻璃", "空管", "空管_管腔系数0" } from n in new[] { 201, 401, 801 } select (St: st, N: n)).ToArray();
        var lines = new string[combos.Length][];
        Parallel.For(0, combos.Length, new ParallelOptions { MaxDegreeOfParallelism = 3 }, k =>
        {
            var (st, n) = combos[k];
            var buf = new List<string>();
            var sw = Stopwatch.StartNew();
            var lc = Case(st, n);
            var r = LineRunner.Run(lc);
            double tRun = sw.Elapsed.TotalSeconds;
            string conv = r.Notes.FirstOrDefault(s => s.StartsWith("外层耦合", StringComparison.Ordinal)) ?? "（没有外层耦合收敛句）";
            buf.Add($"══ {st}　节点 {n}：整线 Ok {r.Ok}　收敛 {r.Converged}　{tRun:0} s　CoupleRemainK {r.CoupleRemainK.ToString("R", Inv)}　{conv}");
            if (!r.Ok || !r.Converged) { buf.Add("   整线没解出或没收敛 ⇒ 这一组跳过雅可比（描述）：" + r.Message); lines[k] = buf.ToArray(); Say(string.Join("\n", buf)); return; }
            int nSeg = r.Segments.Length;
            var draws = lc.WarmStart.Take(nSeg).Select(a => (a[0], a[1])).ToArray();
            var nb = lc.WarmStart.Take(nSeg).Select(a => (a[2], a[3])).ToArray();
            sw.Restart();
            var (slots, J, g0) = LineRunner.NeighbourJacobian(Case(st, n), draws, nb, 1.0);
            double tJ = sw.Elapsed.TotalSeconds;
            bool same = g0.Ok && Enumerable.Range(0, nSeg).All(i => g0.Segments[i].TRootAC.Equals(r.Segments[i].TRootAC) && g0.Segments[i].TRootBC.Equals(r.Segments[i].TRootBC));
            int m = slots.Length;
            double rho = SpectralRadius(J), inf = InfNorm(J), ampN = LineRunner.NeighbourAmplification(J);
            double ampRho = 1.0 / (1.0 - rho);
            buf.Add($"   M0 不扰动段解复现整线管根：{(same ? "逐位相同" : "对不上（雅可比不在收敛态上，只描述）")}　雅可比 {m}×{m}、{tJ:0.0} s");
            for (int row = 0; row < m; row++)
                buf.Add($"   J 行 {row}（段{slots[row].Seg}{(slots[row].Left ? "左邻" : "右邻")}）：" + string.Join(" ", Enumerable.Range(0, m).Select(q => J[row, q].ToString("+0.000000;-0.000000", Inv))));
            buf.Add($"   谱半径 ρ(J) = {rho.ToString("R", Inv)}　‖J‖∞ = {inf.ToString("R", Inv)}　1/(1−ρ) = {ampRho.ToString("R", Inv)}　‖(I−J)⁻¹‖∞ = {ampN.ToString("R", Inv)}　"
                  + $"÷ FixedPointAmp：{(ampRho / LineCase.FixedPointAmp).ToString("0.000", Inv)}（1/(1−ρ)）／{(ampN / LineCase.FixedPointAmp).ToString("0.000", Inv)}（‖(I−J)⁻¹‖∞）");
            for (int i = 0; i + 1 < nSeg; i++)
            {
                var sL = r.Segments[i]; var sR = r.Segments[i + 1];
                double dxL = sL.X.Length > 1 ? sL.X[1] - sL.X[0] : double.NaN, dxR = sR.X.Length > 1 ? sR.X[1] - sR.X[0] : double.NaN;
                double gL = 1.0 / (1.0 + dxL / sL.DecayLengthMm), gR = 1.0 / (1.0 + dxR / sR.DecayLengthMm);
                // 量出的：段 i 的右邻位（= 段 i+1 的 A 端温）对 段 i+1 的左邻位（= 段 i 的 B 端温）的偏导，与反向
                int slotRightOfI = Array.FindIndex(slots, s => s.Seg == i && !s.Left), slotLeftOfNext = Array.FindIndex(slots, s => s.Seg == i + 1 && s.Left);
                double jAofNext = slotRightOfI >= 0 && slotLeftOfNext >= 0 ? J[slotRightOfI, slotLeftOfNext] : double.NaN;   // ∂T_A(段i+1)/∂T_B(段i)
                double jBofI = slotRightOfI >= 0 && slotLeftOfNext >= 0 ? J[slotLeftOfNext, slotRightOfI] : double.NaN;      // ∂T_B(段i)/∂T_A(段i+1)
                buf.Add($"   接头 段{i}|段{i + 1}：闭式 段{i + 1} A 端 g = 1/(1+{dxR.ToString("0.0000", Inv)}/{sR.DecayLengthMm.ToString("0.000", Inv)}) = {gR.ToString("0.000000", Inv)}（1/(1−g) {(1 / (1 - gR)).ToString("0.0", Inv)}）对量出 {jAofNext.ToString("0.000000", Inv)}；"
                      + $"段{i} B 端 g = 1/(1+{dxL.ToString("0.0000", Inv)}/{sL.DecayLengthMm.ToString("0.000", Inv)}) = {gL.ToString("0.000000", Inv)}（1/(1−g) {(1 / (1 - gL)).ToString("0.0", Inv)}）对量出 {jBofI.ToString("0.000000", Inv)}");
            }
            lines[k] = buf.ToArray();
            Say(string.Join("\n", buf));
        });
        Say("");
        Say("── 汇总（按工况、节点数；数取自上面各组）");
        for (int k = 0; k < combos.Length; k++)
            Say($"   {combos[k].St,-12} 节点 {combos[k].N}：" + (lines[k]?.FirstOrDefault(s => s.Contains("谱半径")) ?? "（没有雅可比）").Trim());
    }
}
