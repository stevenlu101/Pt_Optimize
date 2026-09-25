using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ═══════════════════════════════════════════════════════════════════════════════════
//  2026-09-25 拓扑去料原型（全局解决方案 §9.4 第二步；Core/TopologyTrim.cs）的门。
//  (a) 快：合成小算例（R48PropsWiringGateTests.QuickCase 那一类）跑 1 轮、每片只去 12 格 —— 去料只在允许区、有料面积单调不增、
//      栅格截面 J 每轮都算了且满足 < J_设计 + 1（= 11，SectionSizing.JCheckOf）、浮空料块 0（栅格 4 邻接 + 网格 MeshConnectivity 两把尺）、停因非空。
//      QuickCase 的 CheckRamp 关 ⇒ 设计电流缺、截面 J 退回稳态片电流（结果标 DesignCurrentFallback），门里核这一位为真、只印它算的 J。
//  (b) 快：改回／对照 —— MaxRounds = 0 = 纯栅格化，与 R48ArcGapDiagTests 门 f 同一做法的图纸路径直跑逐位相同（铂重、逐片 QFromTubeW）。
//  (c) 慢：W08 搜形状赢家（R48SchemeCardW08Tests.Winner()，舌片厚已填）导航网格（BuildCase 缺省网格）跑 ≤ 6 轮，证据档只印不判。
//  覆盖：Core.TopologyTrim 的排除区、单点判别、栅格截面 J、退回、停因；LineRunner 图纸路径接线。不覆盖：Windows 记录（Linux 预跑）、解析板反推、
//  拓扑板第二遍细网格、升温期逐点复判、去料后叉臂式加厚、Rhino 出图。
// ═══════════════════════════════════════════════════════════════════════════════════
public class R48TopologyTrimTests
{
    private readonly ITestOutputHelper _o;
    public R48TopologyTrimTests(ITestOutputHelper o) => _o = o;

    private static string Row(TopologyTrimRound r)
        => $"第 {r.Round} 轮　{(r.Accepted ? "过" : "不过")}　铂重 {r.MassG:0.0} g（法兰 {r.FlangeMassG:0.0}）　有料 {r.AreaMm2:0.0} mm²　累计去料 {r.RemovedMm2:0.0} mm²　"
         + $"本轮去 {r.CellsRemoved} 格／退回 {r.CellsRestored} 格　栅格截面 J {r.WorstSectionJ:0.###}/{r.JLimit:0.#} @ {r.WorstSectionWhere}（运行器自己那条 {r.RunnerSectionJ:0.###}{(r.DesignCurrentFallback ? "；设计电流缺，用稳态片电流" : "")}）　"
         + $"热侧 {r.HotOverK:0.###}/{r.HotOverLimitK:0.#} K @ {r.HotOverWhere}　冷侧 {r.NetInflowW:+0.###;-0.###}/{r.NetInflowLimitW:0.#} W @ {r.NetInflowWhere}　局部热稳定 {r.LocalStab:0.###} @ {r.LocalStabWhere}　整片热稳定 {r.FlangeStab:0.###} @ {r.FlangeStabWhere}　"
         + $"浮空 栅格 {r.FloatingRaster}／网格 {r.FloatingMesh}　硬判据不过 [{string.Join("；", r.Blocked)}]　耗时 {r.Seconds:0} s　{r.StopReason}";

    // ───────────────────────────── a ─────────────────────────────
    [Fact]
    public void 门a_合成小算例_一轮去料_只在允许区_面积不增_截面J每轮有数_无浮空_停因非空()
    {
        var lc = R48PropsWiringGateTests.QuickCase();
        var o = new TopologyTrimOptions { MaxRounds = 1, CellsPerRound = 12 };
        var sw = Stopwatch.StartNew();
        var res = TopologyTrim.Run(lc, o, new SyncProgress<string>(s => _o.WriteLine(s)));
        sw.Stop();
        foreach (var r in res.Rounds) _o.WriteLine(Row(r));
        _o.WriteLine($"停因：{res.StopReason}　总耗时 {sw.Elapsed.TotalSeconds:0} s");

        Assert.True(res.Rounds.Count >= 1, "一轮都没跑");
        Assert.True(res.Rounds[0].Accepted, "第 0 轮（纯栅格化）就不过：" + res.Rounds[0].StopReason);
        Assert.False(string.IsNullOrWhiteSpace(res.StopReason), "停因为空");
        Assert.True(res.Rounds[0].DesignCurrentFallback, "QuickCase 的 CheckRamp 关，设计电流应缺、应标退回稳态片电流");
        // 截面 J 每轮都算了且满足限值（接受的轮）
        foreach (var r in res.Rounds.Where(x => x.Accepted))
        {
            Assert.False(double.IsNaN(r.WorstSectionJ), $"第 {r.Round} 轮栅格截面 J 没算出来");
            Assert.True(r.WorstSectionJ < r.JLimit, $"第 {r.Round} 轮栅格截面 J {r.WorstSectionJ} 不满足 < {r.JLimit}");
            Assert.Equal(SectionSizing.JCheckOf(lc.JDesignAPerMm2), r.JLimit);
            Assert.Equal(0, r.FloatingRaster); Assert.Equal(0, r.FloatingMesh);
        }
        // 面积单调不增
        for (int k = 1; k < res.Rounds.Count; k++) Assert.True(res.Rounds[k].AreaMm2 <= res.Rounds[k - 1].AreaMm2 + 1e-9, $"第 {k} 轮有料面积比上一轮大");
        // 真去了料才有第二轮；去料只在允许区（压接段格、孔环带之外）
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm, clampLen = lc.Base.BusbarClampLengthMm;
        int removedTotal = 0;
        for (int j = 0; j < res.Fields.Length; j++)
        {
            var f0 = res.Fields0[j]; var f = res.Fields[j];
            var excl = TopologyTrim.ExclusionMask(f0, lc.FlangePlates[j], holeR, clampLen, o);
            int removed = 0, inExcl = 0, added = 0;
            for (int q = 0; q < f0.T.Length; q++)
            {
                bool had = f0.T[q] > 1e-9, has = f.T[q] > 1e-9;
                if (had && !has) { removed++; if (excl[q]) inExcl++; }
                if (!had && has) added++;
            }
            removedTotal += removed;
            _o.WriteLine($"片 {j + 1}：去掉 {removed} 格（排除区内 {inExcl}）、多出 {added} 格；有料 {f0.AreaMm2:0.##} → {f.AreaMm2:0.##} mm²");
            Assert.Equal(0, inExcl); Assert.Equal(0, added);
            Assert.True(removed <= o.CellsPerRound, $"片 {j + 1} 去掉 {removed} 格，超过每轮上限 {o.CellsPerRound}");
            Assert.Equal(0, TopologyTrim.FloatingComponents(f, lc.FlangePlates[j], holeR, clampLen));
        }
        if (res.Rounds.Count >= 2 && res.Rounds[1].Accepted)
            Assert.True(removedTotal > 0, "第 1 轮接受了却一格没去");
        else
            _o.WriteLine("第 1 轮未接受或没生成 ⇒ 最终场 = 第 0 轮（去料 0 格）；停因见上");
        // 转储能出、含每片头
        string dump = res.DumpFields();
        Assert.Contains("## 片 1", dump); Assert.Contains("逐行厚度", dump);
    }

    // ───────────────────────────── b ─────────────────────────────
    [Fact]
    public void 门b_改回_零轮纯栅格化_与图纸路径直跑逐位相同()
    {
        // 直跑：与 R48ArcGapDiagTests 门 f（dHole = 0）同一做法
        var lc1 = R48PropsWiringGateTests.QuickCase();
        var plates = lc1.FlangePlates;
        lc1.FlangeFields = plates.Select(pl => AnalyticSurrogate.Rasterize(pl, 0.5, 2.0)).ToArray();
        lc1.GeomForJudge = plates;
        lc1.TabInsul3dmPerPlateMm = plates.Select(pl => pl.TabInsulThickMm).ToArray();
        lc1.FlangePlates = Array.Empty<FlangePlate>();
        var direct = LineRunner.Run(lc1);
        Assert.True(direct.Ok, direct.Message);

        var lc2 = R48PropsWiringGateTests.QuickCase();
        var res = TopologyTrim.Run(lc2, new TopologyTrimOptions { MaxRounds = 0, StepMm = 0.5, MarginMm = 2.0 });
        Assert.NotNull(res.Round0);
        var r0 = res.Round0!;
        _o.WriteLine($"直跑 {direct.TotalMassG:R} g　零轮 {r0.TotalMassG:R} g　停因：{res.StopReason}");
        Assert.Single(res.Rounds);
        Assert.True(res.Rounds[0].Accepted, res.Rounds[0].StopReason);
        Assert.Equal(BitConverter.DoubleToInt64Bits(direct.TotalMassG), BitConverter.DoubleToInt64Bits(r0.TotalMassG));
        Assert.Equal(BitConverter.DoubleToInt64Bits(direct.FlangeMassG), BitConverter.DoubleToInt64Bits(r0.FlangeMassG));
        Assert.Equal(direct.Flanges.Length, r0.Flanges.Length);
        for (int j = 0; j < direct.Flanges.Length; j++)
        {
            _o.WriteLine($"片 {j + 1}：QFromTubeW 直跑 {direct.Flanges[j].QFromTubeW:R}　零轮 {r0.Flanges[j].QFromTubeW:R}；TMax {direct.Flanges[j].TMaxC:R}／{r0.Flanges[j].TMaxC:R}");
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct.Flanges[j].QFromTubeW), BitConverter.DoubleToInt64Bits(r0.Flanges[j].QFromTubeW));
            Assert.Equal(BitConverter.DoubleToInt64Bits(direct.Flanges[j].TMaxC), BitConverter.DoubleToInt64Bits(r0.Flanges[j].TMaxC));
        }
        // 零轮场 = 纯栅格化（逐格相同）
        for (int j = 0; j < res.Fields.Length; j++)
            Assert.Equal(lc1.FlangeFields[j].T, res.Fields[j].T);
        Assert.Contains("改回值", res.StopReason);
    }

    // ───────────────────────────── c ─────────────────────────────
    [Fact]
    [Trait("速度", "慢")]
    public void 门c_W08赢家_导航网格_拓扑去料六轮_只印()
    {
        var d = R48SchemeCardW08Tests.Winner();
        var p = new DesignInputs();
        var o = new TopologyTrimOptions();
        string path = DeliverableOut.Stamped("R48_拓扑去料原型_W08赢家.txt");
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); _o.WriteLine(s); }
        string load = "未量";
        try { if (File.Exists("/proc/loadavg")) load = File.ReadAllText("/proc/loadavg").Trim(); } catch { }
        W("# 拓扑去料原型　W08 搜形状赢家　导航网格（只印不判；Linux 预跑、待 Windows 重录）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "其它")}　树 {HandoverDoc.Root()}　同机争用：/proc/loadavg = {load}（另有搜形状整夜实跑占 3 核，耗时只作量级）");
        W($"设计 {d.Name}：盘半径 {d.DiscRadiusMm:0.###}　舌半宽 {d.TabHalfWidthMm:0.###}　舌长 {d.TabLengthMm:0.###}　锥形 {d.TabTaper}　板厚 {string.Join("/", d.TabThickMm.Select(x => x.ToString("0.00")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(x => x.ToString("0.00")))}　舌保温 {string.Join("/", d.TabInsulMm.Select(x => x.ToString("0.0")))}");
        var lc = d.BuildCase(p);
        W($"算例：管内径 {lc.TubeIdMm}　壁 {lc.WallMm}　孔半径 {lc.TubeIdMm * 0.5 + lc.WallMm:0.###}　压接长 {lc.Base.BusbarClampLengthMm}　J_设计 {lc.JDesignAPerMm2}（限值 {SectionSizing.JCheckOf(lc.JDesignAPerMm2):0.#}）　导航网格 = LineCase 缺省：细区 {lc.MeshFineMm}／粗区 {lc.MeshCoarseMm}／细区半径 {lc.MeshFineRadiusMm}／内带 {lc.MeshInnerMm}（BuildCase 不改网格）　CheckRamp {lc.CheckRamp}");
        W($"选项：栅格步 {o.StepMm}　留白 {o.MarginMm}　最多 {o.MaxRounds} 轮　每轮每片去 {(o.CellsPerRound > 0 ? o.CellsPerRound.ToString() : $"{o.CellsPerRoundFrac:P0} 有料格（选定）")}　桥宽 {o.BridgeMm}（DesignSpec.TabHoleRMaxMm 缺省，出处未查到）　热稳定当硬判据 {o.TreatStabilityAsHard}　优先级按单元面积归一 {o.PriorityPerArea}");
        W($"逐片：{string.Join("；", lc.FlangePlates.Select((pl, j) => $"片 {j + 1} 盘 R {pl.DiscRadiusMm:0.##} 孔 {pl.HoleRadiusMm:0.##} 焊脚 {pl.WeldFilletLegMm:0.##} 舌端半宽 {pl.TabEndHalfWidthMm:0.##} 舌尖 x {pl.TabTipXMm:0.##} 切点 x {pl.Tangent().X:0.##} 两舌 {pl.TwoTabs}"))}");
        W();
        W("轮\t过\t铂重 g\t法兰 g\t有料 mm²\t累计去料 mm²\t本轮去格\t退回格\t栅格截面 J\t限值\t位置\t运行器截面 J\t热侧 K\t限\t位置\t冷侧 W\t限\t位置\t局部热稳定\t位置\t整片热稳定\t位置\t浮空栅格\t浮空网格\t硬判据不过\t耗时 s\t停因");
        var total = Stopwatch.StartNew();
        var progress = new SyncProgress<string>(s => _o.WriteLine(s));
        TopologyTrimResult res;
        try
        {
            res = TopologyTrim.Run(lc, o, progress);
        }
        catch (Exception ex)
        {
            W($"★ 抛异常：{ex.GetType().Name}: {ex.Message}");
            W(ex.StackTrace ?? "");
            File.WriteAllText(path, sb.ToString());
            throw;
        }
        foreach (var r in res.Rounds)
            W($"{r.Round}\t{(r.Accepted ? "过" : "不过")}\t{r.MassG:0.0}\t{r.FlangeMassG:0.0}\t{r.AreaMm2:0.0}\t{r.RemovedMm2:0.0}\t{r.CellsRemoved}\t{r.CellsRestored}\t{r.WorstSectionJ:0.###}\t{r.JLimit:0.#}\t{r.WorstSectionWhere}\t{r.RunnerSectionJ:0.###}{(r.DesignCurrentFallback ? "（设计电流缺）" : "")}\t{r.HotOverK:0.###}\t{r.HotOverLimitK:0.#}\t{r.HotOverWhere}\t{r.NetInflowW:+0.###;-0.###}\t{r.NetInflowLimitW:0.#}\t{r.NetInflowWhere}\t{r.LocalStab:0.###}\t{r.LocalStabWhere}\t{r.FlangeStab:0.###}\t{r.FlangeStabWhere}\t{r.FloatingRaster}\t{r.FloatingMesh}\t{string.Join("；", r.Blocked)}\t{r.Seconds:0}\t{r.StopReason}");
        W();
        W($"停因：{res.StopReason}");
        W($"总耗时 {total.Elapsed.TotalMinutes:0.0} 分（争用下量得）");
        foreach (var r in res.Rounds)
        {
            W($"── 第 {r.Round} 轮 逐片最紧截面：{string.Join("；", r.PlateWorstJ.Select((v, j) => $"片 {j + 1} {v:0.###} @ {r.PlateWorstWhere[j]}"))}");
        }
        if (res.Final is { } fin)
        {
            W();
            W("最终接受那一轮的判据表（运行器原样）：");
            foreach (var c in fin.Checks)
                W($"　{Criteria.Plain(c.Name)}\t{c.Actual:0.###}\t{(c.LessIsBetter ? "≤" : "≥")} {c.Limit:0.###} {c.Unit}\t{c.Kind}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Where}");
        }
        W();
        W("# 每片最终厚度场转储（X0/Z0/Step/Nx/Nz + ASCII 图 + 逐行厚度）");
        sb.AppendLine(res.DumpFields());   // 转储只进证据档，不回显到测试输出
        File.WriteAllText(path, sb.ToString());
        _o.WriteLine("证据档：" + path);
        Assert.True(res.Rounds.Count >= 1);
    }
}
