using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47（2026-09-13）**改前／改后量数的仪器**：网格轴改成「中心向外、两侧镜像」会动解析路径导航网格的数
/// （工单 §2 A+E 的 ⚠），所以基线设计的 ③／抽热／铂重／J 峰要在改前与改后各量一次、逐条列出来，
/// 不许静默改回归数。两个设计：DesignSpec.Builtin[0]（三段）与盘Ø56 记录（两段，参数照 Pt_Topo/Program.cs 顶部）。
///
/// 输出文件名由环境变量 R47_TAG 定（缺省「改后」）：deliverable/R47_{tag}_导航网格_2026-09-13.txt。
/// 真解整线（每个设计约 1–3 分钟）⇒ 标慢。
/// </summary>
public class R47NavGridInstrumentTests
{
    /// <summary>盘Ø56 记录（Pt_Topo/Program.cs 顶部的口径，逐字照抄）。</summary>
    internal static DesignSpec Disc56TwoSegs()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 };
        d.TubeInsulMm = 10;
        d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        double[] tb = { 0.60, 1.02, 0.60 }, tg = { 1.75, 3.03, 1.75 }, ins = { 5.1, 2.8, 8.6 };
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            int k = Math.Min(j, tb.Length - 1);
            d.TabThickMm[j] = tb[k]; if (j < d.TongueThickMm.Length) d.TongueThickMm[j] = tg[k];
            if (j < d.TabInsulMm.Length) d.TabInsulMm[j] = ins[k];
            if (j < d.RingMul.Length) d.RingMul[j] = 1.0;
            if (j < d.SlotSpanDeg.Length) d.SlotSpanDeg[j] = 0; if (j < d.TabHoleRMm.Length) d.TabHoleRMm[j] = 0;
        }
        d = d.Fit();
        d.Name = "盘Ø56 记录（两段）";
        return d;
    }

    internal static string Describe(string name, LineResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{name}] 收敛 {r.Converged}　全判据 {r.AllOk}　网格 {r.MeshCells} 格　合计铂重 {r.TotalMassG:0.0} g（法兰 {r.FlangeMassG:0.0}）　{(r.Ok ? "" : "✗ " + r.Message)}");
        for (int i = 0; i < r.Segments.Length; i++)
        {
            var s = r.Segments[i];
            sb.AppendLine($"  段{i + 1} {s.Name}：③ 增量温降 {s.FlangeDipK:0.000} K（A {s.FlangeDipAK:0.000} / B {s.FlangeDipBK:0.000}）　管根 {s.TRootAC:0.0}/{s.TRootBC:0.0} °C　基线 {s.BaseTRootAC:0.0}/{s.BaseTRootBC:0.0} °C　I {s.CurrentA:0.0} A");
        }
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            sb.AppendLine($"  片{j} {f.Name}：抽热 {f.QFromTubeW:0.000} W　铂重 {f.MassG:0.00} g　J峰 {f.JMaxAPerMm2:0.000} A/mm²　发热 {f.QGenW:0.0} W　最高温 {f.TMaxC:0.0} °C");
        }
        foreach (var c in r.Checks.Where(c => c.Name.StartsWith("③") || c.Name.StartsWith("②′") || c.Name.StartsWith("②″")))
            sb.AppendLine($"  判据 {c.Name}：{c.Actual:0.000} / {c.Limit:0.###} {c.Unit}　{(c.Undetermined ? "无法判定" : c.Ok ? "✓" : "✗")}　{c.Where}");
        return sb.ToString();
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 量导航网格上的基线数()
    {
        string tag = Environment.GetEnvironmentVariable("R47_TAG") ?? "改后";
        var p = new DesignInputs();
        var sb = new StringBuilder();
        sb.AppendLine($"R47 导航网格基线数（{tag}）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (var d in new[] { DesignSpec.Builtin[0].Clone(), DesignSpec.Builtin[1].Clone(), Disc56TwoSegs() })
        {
            var lc = d.BuildCase(p);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            sw.Stop();
            sb.AppendLine($"设计「{d.Name}」段数 {d.SetpointC.Length}　网格 hFine {lc.MeshFineMm} hCoarse {lc.MeshCoarseMm} fineR {lc.MeshFineRadiusMm} hInner {lc.MeshInnerMm} innerR {lc.MeshInnerRadiusMm}　用时 {sw.Elapsed.TotalSeconds:0} s");
            sb.Append(Describe(d.Name, r));
            double V(string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
            sb.AppendLine($"  记录字段口径：TotalMassG {r.TotalMassG:0.0}　TubeMassG {r.TubeMassG:0.0}　FlangeMassG {r.FlangeMassG:0.0}　RampH {V(LineResult.Key.Ramp):0.000}　DiscOverK {V(LineResult.Key.DiscTemp):0.000}　HoleFluxW {V(LineResult.Key.NetFlux):0.000}　FlangeDipK {V(LineResult.Key.FlangeDip):0.000}　TubeJ {V("管 J"):0.000}");
        }
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped($"R47_{tag}_导航网格_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
        Assert.True(true);
    }
}
