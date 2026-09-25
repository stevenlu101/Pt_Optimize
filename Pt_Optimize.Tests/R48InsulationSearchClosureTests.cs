using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using static PtOptimize.Core.InsulationSearch;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 E 审查修改（2026-09-15 Opus 5）：保温搜索求解器**物理层**的快门 —— 审查意见第 5 条「闭合公式、γ、端号映射、网格配方都没有门」。
/// 全部用手算数值验纯函数（不跑任何场解），外加几条源码门（整线与内层网格走 Solver 的同一份配方；γ 不再读无法兰基线；LineRunner 搬出的函数被原处调用）。
///   · 闭合：Δ较热端 = −γ_热·ΔQ₀/(1+γ_热·s)、Δ较冷端 = −γ_冷·ΔQ₀/(1+γ_热·s)、净流入修正 s·Δ较热端、峰值 κ·Δ较热端；选中格点修正为 0；分母不为正判不了；
///   · 管侧割线 −(管根₊ − 管根₋)/(2ΔQ)；扰动单片抽热按生产的 SegmentEndDraws 分到各端（共用片两侧各半／各扣一次、端片整份、出口片落在末段 B 端）；
///   · 工作点端号：入口片 = 段 0 的 A 端；出口片 = 段 n−1 的 B 端；共用片 = 段 j 的 A 端与段 j−1 的 B 端，较热端／较冷端下标对；基准调生产函数；
///   · 网格偏差折开尔文、到顶标记、闭合核对的标出规则与逐行取值、裕度量化步长默认按耦合容差折算。
/// ★ 2026-09-15 Opus 5（合并）：本文件随 E 最终版合入。合并时 WorkPointOf 多了「整线算例」参数（限值从它读）、基准与较热／较冷端改取 ThermocoupleBasis.At，
///   Options 的两个限值删去、EffectiveMarginQuantum 改为传入算例限值的方法 —— 下面两条 Fact 相应改调用，手算的期望值一个不动（默认算例限值 5 K）。
/// </summary>
public class R48InsulationSearchClosureTests
{
    [Fact]
    public void 闭合公式_手算()
    {
        // qGrid 10、qSel 4 ⇒ ΔQ₀ = 6；γ_热 2.5、γ_冷 2.0、s 0.2 ⇒ 分母 1.5
        var sh = Close(10, 4, 2.5, 2.0, 0.2, 0.5, 0.8);
        Assert.True(sh.Ok);
        Assert.Equal(-10.0, sh.RootHotK, 12);     // −2.5×6/1.5
        Assert.Equal(-8.0, sh.RootColdK, 12);     // −2.0×6/1.5
        Assert.Equal(-2.0, sh.DrawW, 12);         // 0.2×(−10)
        Assert.Equal(-5.0, sh.DiscK, 12);         // 0.5×(−10)
        Assert.Equal(-8.0, sh.TabK, 12);          // 0.8×(−10)
        // 闭合后的净流入 = qSel + ΔQ₀/(1+γ_热·s)：10 − 2 = 8 = 4 + 6/1.5（隐式解自洽）
        Assert.Equal(4 + 6 / 1.5, 10 + sh.DrawW, 12);
        // 端片两端 γ 相同 ⇒ 两端修正相同（与改前「一个 γ」的式子一致）
        var end = Close(7, 3, 3.0, 3.0, 0.25, 1, 1);
        Assert.Equal(end.RootHotK, end.RootColdK, 12);
        Assert.Equal(-3.0 * 4 / 1.75, end.RootHotK, 12);
        // 选中格点自己：全部为 0
        var self = Close(4, 4, 2.5, 2.0, 0.2, 0.5, 0.8);
        Assert.True(self.Ok);
        Assert.Equal(0.0, Math.Abs(self.RootHotK) + Math.Abs(self.RootColdK) + Math.Abs(self.DrawW) + Math.Abs(self.DiscK) + Math.Abs(self.TabK));
        // 分母不为正、不是数 ⇒ 判不了
        Assert.False(Close(10, 4, 2.5, 2.5, -0.4, 0, 0).Ok);       // 1 + 2.5×(−0.4) = 0
        Assert.False(Close(10, 4, 2.5, 2.5, double.NaN, 0, 0).Ok);
        Assert.Contains("分母", Close(10, 4, 2.5, 2.5, -1, 0, 0).Why);
        // 无闭合退路：修正全 0
        Assert.True(ClosureShift.None.Ok);
        Assert.Equal(0.0, ClosureShift.None.RootHotK + ClosureShift.None.RootColdK + ClosureShift.None.DrawW + ClosureShift.None.DiscK + ClosureShift.None.TabK);
    }

    [Fact]
    public void 管侧割线与单片抽热扰动_按生产分配规则()
    {
        Assert.Equal(2.5, TubeSlopeKPerW(1140, 1150, 2), 12);     // 抽热多 4 W、管根低 10 K
        Assert.Equal(-1.0, TubeSlopeKPerW(1152, 1150, 1), 12);    // 符号：抽热多反而更热 ⇒ 负（求解器判不为正）

        var c = new LineCase { SetpointC = new[] { 1150.0, 1080, 1050 }, Base = new DesignInputs { SplitSharedFlangeDraw = true } };
        var baseLR = new (double L, double R)[] { (1, 2), (3, 4), (5, 6) };
        var snapshot = ((double L, double R)[])baseLR.Clone();
        // 入口片（片0）：整份落在段0 左端
        Assert.Equal(new (double, double)[] { (3, 2), (3, 4), (5, 6) }, PerturbPlateDraw(c, baseLR, 0, 2));
        // 共用片（片1）两侧各半：段0 右端 +1、段1 左端 +1
        Assert.Equal(new (double, double)[] { (1, 3), (4, 4), (5, 6) }, PerturbPlateDraw(c, baseLR, 1, 2));
        // 出口片（片3）：整份落在末段（段2）右端
        Assert.Equal(new (double, double)[] { (1, 2), (3, 4), (5, 4) }, PerturbPlateDraw(c, baseLR, 3, -2));
        // 不各半（两段各扣一次）：共用片（片2）两侧各整份
        var c2 = new LineCase { SetpointC = new[] { 1150.0, 1080, 1050 }, Base = new DesignInputs { SplitSharedFlangeDraw = false } };
        Assert.Equal(new (double, double)[] { (1, 2), (3, 6), (7, 6) }, PerturbPlateDraw(c2, baseLR, 2, 2));
        // 不改传入数组
        Assert.Equal(snapshot, baseLR);
        // 分配与生产函数一致（不在门里另写一份规则）：每段两端的增量 = SegmentEndDraws(段, 左片增量, 右片增量)
        for (int j = 0; j < 4; j++)
        {
            var p = PerturbPlateDraw(c, baseLR, j, 2);
            for (int i = 0; i < 3; i++)
            {
                var e = LineRunner.SegmentEndDraws(c, i, i == j ? 2 : 0, i + 1 == j ? 2 : 0);
                Assert.Equal(baseLR[i].L + e.L, p[i].L, 12);
                Assert.Equal(baseLR[i].R + e.R, p[i].R, 12);
            }
        }
    }

    private static LineResult SynthLine()
    {
        var r = new LineResult
        {
            Segments = new[]
            {
                new SegmentOut { SetpointC = 1150, TRootAC = 1146, TRootBC = 1152 },
                new SegmentOut { SetpointC = 1080, TRootAC = 1149, TRootBC = 1071 },
                new SegmentOut { SetpointC = 1050, TRootAC = 1060, TRootBC = 1043 },
            },
        };
        // FlangeOut.TRootC 按 RunOnce 的取法：入口 = 段0 A；共用 = max(段 j−1 B, 段 j A)；出口 = 段 n−1 B
        r.Flanges = new[]
        {
            new FlangeOut { Shared = false, CurrentA = 900, QFromTubeW = 3.0, TRootC = 1146 },
            new FlangeOut { Shared = true, CurrentA = 1500, QFromTubeW = -1.0, TRootC = Math.Max(1152, 1149) },
            new FlangeOut { Shared = true, CurrentA = 1400, QFromTubeW = 2.0, TRootC = Math.Max(1071, 1060) },
            new FlangeOut { Shared = false, CurrentA = 880, QFromTubeW = 17.0, TRootC = 1043 },
        };
        return r;
    }

    [Fact]
    public void 工作点端号映射_入口出口共用片()
    {
        var r = SynthLine();
        var sp = new[] { 1150.0, 1080, 1050 };
        var lc = new LineCase();                                    // 2026-09-15 Opus 5（合并）：限值从整线算例读（缺省 = LineCase.ThermocoupleErrorK）
        var w0 = WorkPointOf(r, lc, 0, 0);
        Assert.Equal((lc.HotOverTcMaxK, lc.ColdUnderTcMaxK), (w0.HotLimitK, w0.ColdLimitK));
        Assert.Equal(new[] { (0, true) }, w0.Ends);
        Assert.Equal((1146.0, 1146.0), (w0.RootHotC, w0.RootColdC));
        Assert.Equal((0, 0), (w0.HotEnd, w0.ColdEnd));
        Assert.Equal(ThermocoupleBasis.ReferenceC(sp, 0), w0.ReferenceC);

        var w3 = WorkPointOf(r, lc, 1, 3);                              // 出口片：段 2 的 B 端
        Assert.Equal(new[] { (2, false) }, w3.Ends);
        Assert.Equal((1043.0, 1043.0), (w3.RootHotC, w3.RootColdC));
        Assert.Equal(17.0, w3.LineDrawW);
        Assert.Equal(880.0, w3.JointA);
        Assert.Equal(1, w3.State);

        var w1 = WorkPointOf(r, lc, 0, 1);                              // 共用片1：段1 A（1149）、段0 B（1152）
        Assert.Equal(new[] { (1, true), (0, false) }, w1.Ends);
        Assert.Equal(new[] { 1149.0, 1152.0 }, w1.EndRootsC);
        Assert.Equal(1, w1.HotEnd);                                 // 段0 B 端较热
        Assert.Equal(0, w1.ColdEnd);
        Assert.Equal(1152.0, w1.RootHotC);
        Assert.Equal(1149.0, w1.RootColdC);
        Assert.True(w1.Shared);
        Assert.Equal(ThermocoupleBasis.ReferenceC(sp, 1), w1.ReferenceC, 12);   // 两侧设定的对数平均（生产函数）

        var w2 = WorkPointOf(r, lc, 0, 2);                              // 共用片2：段2 A（1060）、段1 B（1071）
        Assert.Equal((1, 0), (w2.HotEnd, w2.ColdEnd));
        Assert.Equal((1071.0, 1060.0), (w2.RootHotC, w2.RootColdC));

        // 2026-09-15 Opus 5（合并）：基准与两端取的是 B 的唯一实现（逐位），限值跟着算例走
        foreach (var (w, j) in new[] { (w0, 0), (w1, 1), (w2, 2), (w3, 3) })
        {
            var tc = ThermocoupleBasis.At(r, j);
            Assert.Equal((tc.RefC, tc.RootHotC, tc.RootColdC), (w.ReferenceC, w.RootHotC, w.RootColdC));
        }
        var lc2 = new LineCase { HotOverTcMaxK = 3, ColdUnderTcMaxK = 4 };
        Assert.Equal((3.0, 4.0), (WorkPointOf(r, lc2, 0, 1).HotLimitK, WorkPointOf(r, lc2, 0, 1).ColdLimitK));
        Assert.Throws<ArgumentNullException>(() => WorkPointOf(r, null!, 0, 1));
        // 整线本片热解用的管根与热偶读数的较热端不逐位相同 ⇒ 抛（不许让内层解与整线不是同一个工作点）
        var bad = SynthLine();
        bad.Flanges[1].TRootC = 1151.5;
        Assert.Throws<InvalidOperationException>(() => WorkPointOf(bad, lc, 0, 1));
    }

    [Fact]
    public void 网格偏差折开尔文_到顶标记_量化步长默认()
    {
        Assert.Equal(2.5 * 0.5 / 1.5, MeshBiasK(10.5, 10, 2.5, 0.2), 12);
        Assert.Equal(1.25, MeshBiasK(10.5, 10, 2.5, double.NaN), 12);      // s 量不出按 0
        Assert.True(double.IsNaN(MeshBiasK(10.5, 10, 2.5, -1)));            // 分母不为正

        var o = new Options { DiscLayerMax = 24, TabLayerMax = 24 };
        Assert.Equal("", CapNote(23, 24 - 1, o));
        Assert.Contains("到顶", CapNote(24, 3, o));
        Assert.Contains("圆盘层 = 上界 24", CapNote(24, 3, o));
        Assert.DoesNotContain("舌层", CapNote(24, 3, o));
        Assert.Contains("舌层 = 上界 24", CapNote(2, 24, o));
        Assert.Contains("上界无出处", CapNote(24, 24, o));

        // 2026-09-15 Opus 5（合并）：限值不再是选项（原 new Options { HotLimitK = 2 }），改为传入算例限值；期望值原样
        var lcq = new LineCase();
        var (hq, cq) = LimitsOf(new[] { lcq, new LineCase() });
        Assert.Equal((lcq.HotOverTcMaxK, lcq.ColdUnderTcMaxK), (hq, cq));
        Assert.Equal(0.05, new Options().EffectiveMarginQuantum(hq, cq), 12);                    // 0.25 K ÷ 5 K
        Assert.Equal(0.01, new Options { MarginQuantum = 0.01 }.EffectiveMarginQuantum(hq, cq), 12);
        Assert.Equal(0.1, new Options { CoupleTolK = 0.5 }.EffectiveMarginQuantum(hq, cq), 12);
        Assert.Equal(0.125, new Options().EffectiveMarginQuantum(2, 5), 12);                     // 取较小限值
        Assert.Equal((2.0, 4.0), LimitsOf(new[] { new LineCase { HotOverTcMaxK = 2 }, new LineCase { ColdUnderTcMaxK = 4 } }));   // 两态里各取较小
        Assert.Throws<ArgumentException>(() => LimitsOf(new LineCase?[] { new LineCase(), null }));
    }

    [Fact]
    public void 闭合核对_标出规则与逐行取值()
    {
        Assert.False(ClosureFlagged(-10, -11.5, 1, 0.2));    // 误差 1.5 ≤ max(1, 2)
        Assert.True(ClosureFlagged(-10, -13, 1, 0.2));       // 误差 3 > 2
        Assert.True(ClosureFlagged(0, 1.2, 1, 0.2));         // 本片没变、串扰 1.2 > 1
        Assert.False(ClosureFlagged(0, 0.9, 1, 0.2));
        Assert.False(ClosureFlagged(double.NaN, 1, 1, 0.2));

        // 合成两轮：一片，两态。上一轮从 (0,0) 选到 (3,2)，闭合预测较热端管根 1140、本轮整线实值 1143（预测修正 −10，实际 −7，误差 +3 ⇒ 标）
        var o = new Options();
        PlateStateRound Ps(double rootHot, double rootCold, double q, double disc, double tab) => new()
        {
            Wp = new PlateWorkPoint { RootHotC = rootHot, RootColdC = rootCold },
            GridDrawAtSelW = q, GridDiscAtSelC = disc, GridTabAtSelC = tab, SelOk = true, Closed = true,
        };
        var prevDet = new RoundDetail { PS = new PlateStateRound[1, 2] };
        prevDet.PS[0, 0] = Ps(1150, 1148, 10, 1120, 1130);
        prevDet.PS[0, 1] = Ps(1160, 1158, -2, 1150, 1155);
        var chosen = new PointOutcome { Disc = 3, Tab = 2, Detail = new PointDetail
        {
            Views = new[]
            {
                new PlateStateView { RootHotC = 1140, RootColdC = 1139, NetInflowW = 6, DiscPeakC = 1112, TabPeakC = 1125 },
                new PlateStateView { RootHotC = 1161, RootColdC = 1159, NetInflowW = -2.5, DiscPeakC = 1150, TabPeakC = 1155 },
            },
        } };
        var prev = new RoundRecord { Round = 1, From = new Selection(new[] { 0 }, new[] { 0 }), LineOk = true, Detail = prevDet,
                                     Choices = new[] { new PlateChoice { Plate = 0, Chosen = chosen } } };
        var curDet = new RoundDetail { PS = new PlateStateRound[1, 2] };
        curDet.PS[0, 0] = Ps(1143, 1141, 7, 1114, 1126);
        curDet.PS[0, 1] = Ps(1161.5, 1159.2, -2.4, 1150.2, 1155.1);
        var cur = new RoundRecord { Round = 2, From = new Selection(new[] { 3 }, new[] { 2 }), LineOk = true, Detail = curDet };
        var rows = CheckClosure(prev, cur, o);
        Assert.Equal(10, rows.Count);                                    // 1 片 × 2 态 × 5 个量
        Assert.All(rows, r => Assert.True(r.PlateChanged));
        var hot = rows.Single(r => r.State == 0 && r.Quantity == "较热端管根");
        Assert.Equal(-10.0, hot.PredShift, 12);
        Assert.Equal(-7.0, hot.ActualShift, 12);
        Assert.Equal(3.0, hot.Error, 12);
        Assert.True(hot.Flag);
        var q = rows.Single(r => r.State == 0 && r.Quantity == "管孔净流入");
        Assert.Equal("W", q.Unit);
        Assert.Equal(-4.0, q.PredShift, 12);                             // 6 − 10
        Assert.Equal(-3.0, q.ActualShift, 12);                           // 7 − 10
        Assert.True(q.Flag);                                             // 误差 1 > max(0.4 W, 20 % × 4 = 0.8) ⇒ 标
        var qEmpty = rows.Single(r => r.State == 1 && r.Quantity == "管孔净流入");
        Assert.Equal(-0.5, qEmpty.PredShift, 12);                        // −2.5 − (−2)
        Assert.Equal(-0.4, qEmpty.ActualShift, 12);                      // −2.4 − (−2)
        Assert.False(qEmpty.Flag);                                       // 误差 0.1 ≤ 0.4
        var coldEmpty = rows.Single(r => r.State == 1 && r.Quantity == "较冷端管根");
        Assert.Equal(1.0, coldEmpty.PredShift, 12);
        Assert.Equal(1.2, coldEmpty.ActualShift, 12);
        Assert.False(coldEmpty.Flag);
        // 上一轮整线没解出 ⇒ 没有可比的行
        Assert.Empty(CheckClosure(new RoundRecord { LineOk = false, From = prev.From }, cur, o));
    }

    [Fact]
    public void 源码门_网格同一份配方_γ不读基线_搬出的函数被原处调用()
    {
        string root = HandoverDoc.Root();
        string Code(string rel) => string.Join("\n", File.ReadAllText(Path.Combine(root, rel)).Split('\n')
            .Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
        string ins = Code("Pt_Optimize/Core/InsulationSearch.cs");
        Assert.DoesNotContain("MeshAdapt.RefineWholeMesh(", ins);                 // 不许再手写网格配方
        Assert.True(Regex.Matches(ins, @"Solver\.ApplyCaseMesh\(").Count >= 3, "整线、内层网格、电位场算例都要走 Solver.ApplyCaseMesh");
        Assert.DoesNotContain("BaseTRoot", ins);                                  // γ 不再锚在无法兰基线与两个角上
        Assert.Contains("LineRunner.SolveTubeWithDrawsNewton(", ins);             // γ = 当前选择的管侧响应
        Assert.Contains("LineRunner.NeighbourJacobian(", ins);

        string solver = Code("Pt_Optimize/Core/Solver.cs");
        Assert.Contains("ApplyCaseMesh(lc, o);", solver);                         // EvalRaw 调搬出的配方

        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("targetLR[i] = SegmentEndDraws(c, i, a, b);", lr);        // 整线外层耦合调搬出的分配规则
        Assert.Contains("var nbNew = NeighbourTempsOf(res.Segments);", lr);        // 段间端温定义只有一份（牛顿解读 G(x) 也用它）
        Assert.Contains("var nb2 = NeighbourTempsOf(br.Segments);", lr);
        Assert.Contains("draws[i] = MeanDraw(drawsLR[i]);", lr);
        Assert.Contains("SolveSegmentsInto(c, res, progress, cancel, drawW, drawLR, nbT, out var amps, out var segParams)", lr);
        // R48 M（2026-09-18，Fable 5.1，改门写明变因）：基线环多传一个实测雅可比放大 ampJac（与主环同一份 StopAmpOf）
        Assert.Matches(new Regex(@"IterateNeighbourTemps\(c, bnb => RunOnce\(c, null, cancel, zero, zeroLR, bnb\), null,\s*\n?\s*Math\.Max\(30, c\.CoupleMaxRounds\), baseTolK, progress, ""无法兰基线"", ampJac\)"), lr);
        // R48 L（2026-09-17，Opus 5）：基线那一调的容差改成唯一读口算出来的 baseTolK（= CoupleTolKFor(c, null)：还没有判据可读 ⇒ 上限）
        Assert.Contains("double baseTolK = BaselineTolKFor(c);", lr);
    }

    [Fact]
    public void 段间端温定义_两头为NaN_左邻取上一段B端右邻取下一段A端()
    {
        var segs = new[]
        {
            new SegmentOut { TRootAC = 1, TRootBC = 2 }, new SegmentOut { TRootAC = 3, TRootBC = 4 }, new SegmentOut { TRootAC = 5, TRootBC = 6 },
        };
        var nb = LineRunner.NeighbourTempsOf(segs);
        Assert.True(double.IsNaN(nb[0].L));
        Assert.Equal(3.0, nb[0].R);
        Assert.Equal((2.0, 5.0), nb[1]);
        Assert.Equal(4.0, nb[2].L);
        Assert.True(double.IsNaN(nb[2].R));
    }

    [Fact]
    public void 牛顿步的小方程组_列主元消去_手算()
    {
        // 2x + y − z = 8；−3x − y + 2z = −11；−2x + y + 2z = −3 ⇒ (2, 3, −1)
        var a = new double[,] { { 2, 1, -1 }, { -3, -1, 2 }, { -2, 1, 2 } };
        var x = LineRunner.SolveDense(a, new double[] { 8, -11, -3 })!;
        Assert.Equal(2.0, x[0], 10); Assert.Equal(3.0, x[1], 10); Assert.Equal(-1.0, x[2], 10);
        Assert.Equal(2.0, a[0, 0]);                                               // 不改传入矩阵
        Assert.Null(LineRunner.SolveDense(new double[,] { { 1, 2 }, { 2, 4 } }, new double[] { 1, 2 }));   // 奇异
        // 首元为 0 要换行
        var y = LineRunner.SolveDense(new double[,] { { 0, 1 }, { 1, 0 } }, new double[] { 5, 7 })!;
        Assert.Equal((7.0, 5.0), (y[0], y[1]));
    }

    /// <summary>行为门：Solver.ApplyCaseMesh 三支与 MeshAdapt.RefineWholeMesh 逐位一致（导航支只统一半径，不动尺寸与粗区）。</summary>
    [Fact]
    public void 网格配方_导航支统一半径_加密支自相似()
    {
        var d = DesignSpec.W08.Clone().Fit();
        double radius = MeshVerify.RequiredMeshFor(d, new DesignInputs()).RadiusMm;
        var nav = d.BuildCase(new DesignInputs(), checkRamp: false);
        double fine0 = nav.MeshFineMm, coarse0 = nav.MeshCoarseMm;
        Solver.ApplyCaseMesh(nav, new SolverOptions { FineMm = 0, FineRadiusMm = radius });
        Assert.Equal(fine0, nav.MeshFineMm);
        Assert.Equal(coarse0, nav.MeshCoarseMm);
        Assert.Equal(radius, nav.MeshFineRadiusMm);
        Assert.NotEqual(50.0, radius);                                            // W08 的细区半径不是 LineCase 默认 50（审查意见第 1 条的前提）

        var fine = d.BuildCase(new DesignInputs(), checkRamp: false);
        var refd = d.BuildCase(new DesignInputs(), checkRamp: false);
        Solver.ApplyCaseMesh(fine, new SolverOptions { FineMm = 1.0, FineRadiusMm = radius });
        MeshAdapt.RefineWholeMesh(refd, 1.0, radius);
        Assert.Equal((refd.MeshFineMm, refd.MeshCoarseMm, refd.MeshFineRadiusMm, refd.MeshInnerMm, refd.MeshInnerRadiusMm),
                     (fine.MeshFineMm, fine.MeshCoarseMm, fine.MeshFineRadiusMm, fine.MeshInnerMm, fine.MeshInnerRadiusMm));

        var raw = d.BuildCase(new DesignInputs(), checkRamp: false);
        double c0 = raw.MeshCoarseMm;
        Solver.ApplyCaseMesh(raw, new SolverOptions());                          // 三支都不触发 = BuildCase 原样
        Assert.Equal((fine0, c0, 50.0), (raw.MeshFineMm, raw.MeshCoarseMm, raw.MeshFineRadiusMm));
    }
}
