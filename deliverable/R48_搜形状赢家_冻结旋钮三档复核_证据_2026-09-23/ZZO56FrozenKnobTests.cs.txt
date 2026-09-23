using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// (b0) 冻结旋钮复核，不解（2026-09-23 探针，不入生产）：搜形状有解版本（v6.1 10.5 节，docs/Pt_理论模型_v6.1.md:582-597）的三个形状，
/// 旋钮按当年值冻结，在今天的树上 ×1／×0.5／×0.25 三档网格（中带 2.0／1.0／0.5，粗区按 MeshAdapt.RefineWholeMesh 同比例缩：11／5.5／2.75）
/// 各算一次整线场（LineRunner.Run，不求根、不改旋钮）；细区半径取今天的计划初值 MeshVerify.RequiredMeshFor(d, p).RadiusMm，当年的 50 另印。
/// 只量不判：判词原样印 LineRunner 给的 ✓／✗／判不了，不另加判断。
/// 印法照 R47NavGridInstrumentTests.Describe（旧判法三行）并加印现行两条硬判据（⑦ 最热铂高出热偶读数、⑧ 管根低于热偶读数）与全部判据行。
/// </summary>
public class ZZO56FrozenKnobTests
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string Frag = "ZZO56Frozen_片段";

    static void SetPlates(DesignSpec d, double[] tb, double[] tg, double[] ins)
    {
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            int k = Math.Min(j, tb.Length - 1);
            d.TabThickMm[j] = tb[k]; if (j < d.TongueThickMm.Length) d.TongueThickMm[j] = tg[k];
            if (j < d.TabInsulMm.Length) d.TabInsulMm[j] = ins[k];
            if (j < d.RingMul.Length) d.RingMul[j] = 1.0;
            if (j < d.SlotSpanDeg.Length) d.SlotSpanDeg[j] = 0; if (j < d.TabHoleRMm.Length) d.TabHoleRMm[j] = 0;
        }
    }

    /// <summary>① 不挖族赢家：盘Ø56／舌宽 56／舌长 140；旋钮 v6.1:582-597（舌保温 5.2/2.9/8.8）。与 R47 探针的 Disc56TwoSegs 同一写法，只换舌保温。</summary>
    internal static DesignSpec Disc56(double[] ins, string name)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 };
        d.TubeInsulMm = 10;
        d.DiscRadiusMm = 28; d.TabHalfWidthMm = 28; d.TabLengthMm = 140;
        SetPlates(d, new[] { 0.60, 1.02, 0.60 }, new[] { 1.75, 3.03, 1.75 }, ins);
        d = d.Fit();
        d.Name = name;
        return d;
    }

    /// <summary>锥形舌 146×30 的底子（SketchShapeTests／ExportYSketch3dmTests 同一构型）。</summary>
    static DesignSpec TaperBase(string name)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = name;
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 15; d.TabLengthMm = 146; d.TabTaper = true;
        return d;
    }

    /// <summary>② 同一锥形舌不开槽（2755.1 g）：v6.1:582-597 的旋钮（板厚 0.60/1.02/0.60，舌片厚 2.64/4.57/2.64，舌保温 9.3/6.3/18.2）。</summary>
    internal static DesignSpec TaperNoSlot()
    {
        var d = TaperBase("锥形舌146×30不开槽（v6.1 2755.1 g 旋钮）");
        SetPlates(d, new[] { 0.60, 1.02, 0.60 }, new[] { 2.64, 4.57, 2.64 }, new[] { 9.3, 6.3, 18.2 });
        d = d.Fit();
        return d;
    }

    /// <summary>
    /// ③ 侧 Y 形挖孔后（2741.9 g）：几何与板厚、舌片厚、叉臂、孔心、孔拉长、边数、转角从 deliverable/拍脑袋Y形_解出/拍脑袋Y形_整机.spec.json 读；
    /// 孔径旋钮 28（spec 里的 holeR 是外接半径 = EqualAreaRadius(28, 3, 0.35)，这里反核）；舌保温 5.7/3.6/11.7 取 核算与出图.txt（spec 不带保温）。
    /// </summary>
    internal static (DesignSpec d, string src) YSlot()
    {
        var d = TaperBase("侧Y形挖孔后（v6.1 2741.9 g 旋钮）");
        string root = HandoverDoc.Root();
        string specPath = Path.Combine(root, "deliverable", "拍脑袋Y形_解出", "拍脑袋Y形_整机.spec.json");
        var src = new StringBuilder($"spec.json = {specPath}\n");
        using var doc = JsonDocument.Parse(File.ReadAllText(specPath));
        var r = doc.RootElement;
        double G(JsonElement e, string k) => e.GetProperty(k).GetDouble();
        Assert.Equal(28.0, G(r, "discR")); Assert.Equal(-146.0, G(r, "tabX")); Assert.Equal(15.0, G(r, "tabHW")); Assert.True(r.GetProperty("tabTaper").GetBoolean());
        Assert.Equal(0.8, G(r, "wallMm")); Assert.Equal(2, r.GetProperty("segCount").GetInt32());
        var plates = r.GetProperty("plates").EnumerateArray().ToArray();
        Assert.Equal(d.FlangeCount, plates.Length);
        double[] ins = { 5.7, 3.6, 11.7 };
        for (int j = 0; j < plates.Length; j++)
        {
            var p = plates[j];
            d.TabThickMm[j] = G(p, "t"); d.TongueThickMm[j] = G(p, "tabT");
            d.TabArmX0Mm[j] = G(p, "tabArmX0"); d.TabArmX1Mm[j] = G(p, "tabArmX1"); d.TabArmThickMm[j] = G(p, "tabArmT");
            d.TabInsulMm[j] = ins[j]; d.RingMul[j] = 1.0; d.SlotSpanDeg[j] = G(p, "slotDeg");
            int sides = p.GetProperty("holeSides").GetInt32();
            double corner = G(p, "holeCorner");
            double knobR = 28.0;
            double circ = FlangePlate.TabHole.EqualAreaRadius(knobR, sides, corner);
            src.AppendLine($"  片{j}：t {G(p, "t")} tabT {G(p, "tabT")} 叉臂 {G(p, "tabArmT")}×[{G(p, "tabArmX0"):0.###},{G(p, "tabArmX1"):0.###}] 孔心 {G(p, "holeX"):0.#####} 外接R(spec) {G(p, "holeR"):0.#####} 反核 EqualAreaRadius(28,{sides},{corner}) = {circ:0.#####} 拉长 {G(p, "holeAsp")} 转角 {G(p, "holeRot")} 盘槽张角 {G(p, "slotDeg")}");
            Assert.True(Math.Abs(circ - G(p, "holeR")) < 1e-3, $"孔径旋钮反核不符：{circ} vs {G(p, "holeR")}");
            d.TabHoleRMm[j] = knobR; d.TabHoleSides[j] = sides; d.TabHoleAspect[j] = G(p, "holeAsp");
            d.TabHoleXMm[j] = G(p, "holeX"); d.TabHoleRotDeg[j] = G(p, "holeRot");
            Assert.Equal(corner, DesignSpec.TabHoleCornerFracOf(sides), 6);
        }
        d = d.Fit();
        return (d, src.ToString());
    }

    static (DesignSpec d, string src) Shape(int id) => id switch
    {
        1 => (Disc56(new[] { 5.2, 2.9, 8.8 }, "盘Ø56舌56（v6.1 2603.8 g 旋钮）"), "手建：R47NavGridInstrumentTests.Disc56TwoSegs 写法，舌保温换 v6.1:582-597 的 5.2/2.9/8.8"),
        2 => (TaperNoSlot(), "手建：ExportYSketch3dmTests 构型，旋钮 v6.1:582-597"),
        3 => YSlot(),
        9 => (Disc56(new[] { 5.1, 2.8, 8.6 }, "盘Ø56 记录（两段）R47 探针旋钮（对照行）"), "R47NavGridInstrumentTests.Disc56TwoSegs 原样（舌保温 5.1/2.8/8.6）"),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    static double V(LineResult r, string k) => r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal))?.Actual ?? double.NaN;
    static string Verd(LineResult r, string k)
    {
        var c = r.Checks.FirstOrDefault(c => c.Name.StartsWith(k, StringComparison.Ordinal));
        return c is null ? "缺" : c.Undetermined ? "判不了" : c.Ok ? "✓" : "✗";
    }

    /// <summary>跑一次：shape 形状号，h 中带 mm（2／1／0.5），radius50 = true 时细区半径用当年的 50（对照行）。</summary>
    static void RunOne(int shape, double h, string tierName, bool radius50 = false, bool grow = false, double rOverride = double.NaN)
    {
        var p = new DesignInputs();
        var (d, src) = Shape(shape);
        var sb = new StringBuilder();
        string root = HandoverDoc.Root();
        sb.AppendLine($"(b0) 冻结旋钮复核片段　形状 {shape}「{d.Name}」　档 {tierName}　开跑 {DeliverableOut.RunStamp}　平台 Linux 镜像（预跑，待 Windows 重录）　git HEAD {EvidenceHeader.GitHead(root)}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(root)}　同机并跑：是（另有 Core 驱动实施在编译与冒烟，本探针三个形状三进程并跑）");
        sb.AppendLine($"形状来源：{src.TrimEnd()}");
        sb.AppendLine($"设计：{d.Describe()}");
        sb.AppendLine($"圆盘保温（整线）{d.WholeLineDiscInsulMm:0.##} mm　管保温 {d.TubeInsulMm} mm　段数 {d.SetpointC.Length}　片数 {d.FlangeCount}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = MeshVerify.FineRadiusPlanFor(d, p);
        var req = MeshVerify.RequiredMeshFor(d, p);
        var lc = d.BuildCase(p);
        double navFine = lc.MeshFineMm, navCoarse = lc.MeshCoarseMm, navR = lc.MeshFineRadiusMm;
        double radius = !double.IsNaN(rOverride) ? rOverride : radius50 ? 50.0 : req.RadiusMm;
        MeshAdapt.RefineWholeMesh(lc, h, radius, 0);
        sb.AppendLine($"网格：BuildCase 缺省 hFine {navFine} hCoarse {navCoarse} fineR {navR}（当年的 50）；本档 RefineWholeMesh(h={h}) ⇒ hFine {lc.MeshFineMm} hCoarse {lc.MeshCoarseMm:0.###} fineR {lc.MeshFineRadiusMm:0.###} hInner {lc.MeshInnerMm} innerR {lc.MeshInnerRadiusMm}");
        sb.AppendLine($"细区半径计划（今天的规则，初值，不放大）：RequiredMeshFor.RadiusMm {req.RadiusMm:0.###}（RequiredMeshFor.FineMm {req.FineMm:0.###}，只印）　{plan.Describe()}");
        var r = LineRunner.Run(lc);
        // 放大变体（对照行，生产 MeshVerify 解后放大的同一函数 MeshAdapt.GrowFineRadius；每次放大后重建算例重解，最多 6 次）
        for (int it = 0; grow && it < 6; it++)
        {
            double peak = MeshVerify.HotspotRadiusMm(r);
            var (capCase, _) = MeshAdapt.PlateOuterRadiusMm(lc);
            var g = MeshAdapt.GrowFineRadius(plan, peak, 0, lc.MeshCoarseMm, capCase, $"b0 放大第 {it + 1} 次（{h} mm）");
            sb.AppendLine($"  放大第 {it + 1} 次前：格 {r.MeshCells}　半径 {lc.MeshFineRadiusMm:0.###}　最远热点 r {peak:0.0}　③ {V(r, LineResult.Key.FlangeDip):0.000}　⑦ {V(r, LineResult.Key.HotOverTc):0.000}　⑧ {V(r, LineResult.Key.ColdUnderTc):0.000}　②′ {V(r, LineResult.Key.NetFlux):0.000}　已用 {sw.Elapsed.TotalSeconds:0} s　⇒ Grew {g.Grew} Refused {g.Refused}　{g.Verdict?.Substring(0, Math.Min(80, g.Verdict.Length))}");
            plan = g.Plan;
            if (!g.Grew) break;
            lc = d.BuildCase(p);
            MeshAdapt.RefineWholeMesh(lc, h, plan.RadiusMm, 0);
            r = LineRunner.Run(lc);
        }
        if (grow) sb.AppendLine($"  放大后计划：{plan.Describe()}");
        sw.Stop();
        double sec = sw.Elapsed.TotalSeconds;
        sb.AppendLine($"用时 {sec:0} s（争用下量得）");
        sb.Append(R47NavGridInstrumentTests.Describe(d.Name, r));
        sb.AppendLine("  全部判据行（原样，只印）：");
        foreach (var c in r.Checks)
            sb.AppendLine($"    {(c.Undetermined ? "判不了" : c.Ok ? "✓" : "✗")} [{c.Kind}] {c.Name}：{c.Actual.ToString("0.000", Inv)} / {c.Limit.ToString("0.###", Inv)} {c.Unit}　{c.Where}");
        var und = r.FieldUndeterminedReasons;
        sb.AppendLine($"  场判不了原因：{(und.Length == 0 ? "无" : string.Join("；", und))}");
        sb.AppendLine($"  硬安全线未过或判不了：{string.Join("；", r.HardBlocked.Select(c => $"{c.Name} {c.Actual:0.###}/{c.Limit:0.###}"))}");
        double peakR = MeshVerify.HotspotRadiusMm(r);
        string? peakBad = MeshVerify.HotspotVerdict(r, 0, lc.MeshFineRadiusMm);
        sb.AppendLine($"  热点位置（只印）：最远热点 r {peakR:0.0} mm；细区半径 {lc.MeshFineRadiusMm:0.0}；HotspotVerdict：{peakBad ?? "盖住"}");
        double pt = r.Segments.Sum(s => s.MassG) + r.Flanges.Sum(f => f.MassG);
        string row = string.Join("\t", new[]
        {
            "ROW", shape.ToString(Inv), tierName, r.MeshCells.ToString(Inv), lc.MeshFineRadiusMm.ToString("0.###", Inv),
            V(r, LineResult.Key.FlangeDip).ToString("0.000", Inv), Verd(r, LineResult.Key.FlangeDip),
            V(r, LineResult.Key.DiscTemp).ToString("0.000", Inv), Verd(r, LineResult.Key.DiscTemp),
            V(r, LineResult.Key.NetFlux).ToString("0.000", Inv), Verd(r, LineResult.Key.NetFlux),
            V(r, LineResult.Key.HotOverTc).ToString("0.000", Inv), Verd(r, LineResult.Key.HotOverTc),
            V(r, LineResult.Key.ColdUnderTc).ToString("0.000", Inv), Verd(r, LineResult.Key.ColdUnderTc),
            r.TotalMassG.ToString("0.0", Inv), pt.ToString("0.0", Inv), sec.ToString("0", Inv), r.AllOk.ToString(), r.Converged.ToString(),
            peakR.ToString("0.0", Inv), d.Name
        });
        sb.AppendLine(row);
        File.WriteAllText(DeliverableOut.Stamped($"{Frag}_{shape}_{tierName}.txt"), sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
    }

    [Trait("速度", "慢")][Fact] public void S1_导航() => RunOne(1, 2.0, "导航");
    [Trait("速度", "慢")][Fact] public void S1_判决() => RunOne(1, 1.0, "判决");
    [Trait("速度", "慢")][Fact] public void S1_细() => RunOne(1, 0.5, "细");
    [Trait("速度", "慢")][Fact] public void S2_导航() => RunOne(2, 2.0, "导航");
    [Trait("速度", "慢")][Fact] public void S2_判决() => RunOne(2, 1.0, "判决");
    [Trait("速度", "慢")][Fact] public void S2_细() => RunOne(2, 0.5, "细");
    [Trait("速度", "慢")][Fact] public void S3_导航() => RunOne(3, 2.0, "导航");
    [Trait("速度", "慢")][Fact] public void S3_判决() => RunOne(3, 1.0, "判决");
    [Trait("速度", "慢")][Fact] public void S3_细() => RunOne(3, 0.5, "细");
    /// <summary>对照行：R47 探针原旋钮（5.1/2.8/8.6）、细区半径 50、导航档 ⇒ 与 R47_改后_导航网格_…_144739 的 29.279 同口径。</summary>
    [Trait("速度", "慢")][Fact] public void S9_导航_R50() => RunOne(9, 2.0, "导航R50", radius50: true);
    /// <summary>对照行：v6.1 旋钮、细区半径 50、导航档（隔开「半径规则」与「舌保温旋钮」两个变因）。</summary>
    [Trait("速度", "慢")][Fact] public void S1_导航_R50() => RunOne(1, 2.0, "导航R50", radius50: true);

    /// <summary>放大对照：今天生产在解后按最远热点放大细区半径（GrowFineRadius）；这里在导航档照做一遍，看半径规则对冻结旋钮数的影响。</summary>
    [Trait("速度", "慢")][Fact] public void S2_导航_放大() => RunOne(2, 2.0, "导航放大", grow: true);
    [Trait("速度", "慢")][Fact] public void S3_导航_放大() => RunOne(3, 2.0, "导航放大", grow: true);
    /// <summary>Y 形导航档放大一次后的半径（省去重跑初值那一遍）：导航档实测最远热点 r = 47.0 mm（片段 3_导航），按 MeshAdapt.GrowFineRadius 的式子
    /// min(上限, max(r + 余量 10, 半径 53.958 + 一粗格 11)) = 64.958 mm（上限 > 65 推断：板料外缘至少到舌端 146）。</summary>
    [Trait("速度", "慢")][Fact] public void S3_导航_R放大() => RunOne(3, 2.0, "导航放大", rOverride: 64.958);
    /// <summary>锥形不开槽细档，细区半径取判决档放大终值 116.832 mm（片段 2_判决放大；细档一粗格 2.75 时 GrowFineRadius 的终值可能略不同，这里不再放大）。</summary>
    [Trait("速度", "慢")][Fact] public void S2_细_R放大() => RunOne(2, 0.5, "细放大", rOverride: 116.832);
    /// <summary>细放大一跑的最远热点 r = 107.4 mm（片段 2_细放大）仍未留足余量 ⇒ 按 GrowFineRadius 式子再放一次：max(107.4 + 10, 116.832 + 2.75) = 119.582 mm。</summary>
    [Trait("速度", "慢")][Fact] public void S2_细_R放大2() => RunOne(2, 0.5, "细放大2", rOverride: 119.582);
    /// <summary>Y 形判决档：初值半径一跑的最远热点 r = 67.8 mm（片段 3_判决，印到 0.1 mm）⇒ 按 GrowFineRadius 式子 max(67.8 + 10, 53.958 + 5.5) = 77.8 mm。</summary>
    [Trait("速度", "慢")][Fact] public void S3_判决_R放大() => RunOne(3, 1.0, "判决放大", rOverride: 77.8);
    /// <summary>Y 形细档，细区半径取判决档放大后的 77.8 mm（与 S3_判决_R放大 同一半径，档间只差网格尺寸）。</summary>
    [Trait("速度", "慢")][Fact] public void S3_细_R放大() => RunOne(3, 0.5, "细放大", rOverride: 77.8);
    [Trait("速度", "慢")][Fact] public void S2_判决_放大() => RunOne(2, 1.0, "判决放大", grow: true);

    /// <summary>汇总：读各片段最新一份，出总表与档间差（只做算术，不判）。</summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 汇总()
    {
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        var sb = new StringBuilder();
        sb.AppendLine($"R48 搜形状赢家 冻结旋钮三档复核（(b0)，只量不判）　汇总开跑 {DeliverableOut.RunStamp}");
        sb.AppendLine($"平台 Linux 镜像（预跑，待 Windows 重录）　git HEAD {EvidenceHeader.GitHead(HandoverDoc.Root())}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(HandoverDoc.Root())}　同机并跑：是");
        sb.AppendLine("工况：2 段 3 片、管壁 0.8、管保温 10、圆盘保温 20（docs/Pt_理论模型_v6.1.md:602-618；DesignSpec.FlangeInsulMm 缺省 20）。网格档：中带 2.0／1.0／0.5，粗区 11／5.5／2.75（RefineWholeMesh 同比例），内带不分；细区半径 = 今天的计划初值（不放大），对照行用当年的 50。");
        sb.AppendLine("列：形状｜档｜格数｜细区半径｜③ 法兰增量温降（旧判法）｜②″ 圆盘区最高温 − 管温（旧判法）｜②′ 管孔净流入｜⑦ 最热铂高出热偶读数｜⑧ 管根低于热偶读数｜合计铂重 g｜耗时 s（争用下量得）｜全判据｜最远热点 r");
        var rows = new System.Collections.Generic.List<string[]>();
        foreach (var f in Directory.GetFiles(dir, Frag + "_*.txt").OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal))
        {
            var line = File.ReadAllLines(f).LastOrDefault(l => l.StartsWith("ROW\t", StringComparison.Ordinal));
            if (line is null) continue;
            var c = line.Split('\t');
            rows.RemoveAll(x => x[1] == c[1] && x[2] == c[2]);   // 同形状同档取最新
            rows.Add(c.Append(Path.GetFileName(f)).ToArray());
        }
        sb.AppendLine("读法：②″、③ 两行后的 ✓ 是 LineRunner 对参考量写死的 Ok = true（LineRunner.cs:4146、:4249），不表示在旧限内；旧限 ③ 10 K、②″ 5 K 只作对照。现行硬安全线是 ⑦、⑧（限 5 K）与 ②′（须 > 0）。");
        sb.AppendLine("「热点盖住」= 最远热点 r + 余量 10 ≤ 细区半径（MeshAdapt.PeakVerdict 同式，只印）；不盖住时生产代码（MeshVerify）判「这次复核的温度类判据不算数」并放大半径。");
        var old = new System.Collections.Generic.Dictionary<string, string>
        {
            ["1"] = "v6.1:582-597 当年（0.125 mm 细网格）：③ 9.06　②′ 2.35　②″ −0.05　铂 2603.8 g",
            ["2"] = "v6.1:582-597 当年（导航 2 mm）：③ 9.97　②′ 3.22　②″ −0.11　铂 2755.1 g",
            ["3"] = "v6.1:582-597 当年（导航 2 mm）：③ 9.71　②′ 3.38　②″ −0.11　铂 2741.9 g",
            ["9"] = "R47_改后_导航网格_2026-09-13_本次开跑于2026-09-23_144739.txt:28-38（同旋钮、同 R50 导航）：③ 29.279　②′ 9.666　②″ 1.714　格 904　铂 2605.5 g",
        };
        string[][] chains = { new[] { "导航", "判决", "细" }, new[] { "导航放大", "判决放大", "细放大", "细放大2" }, new[] { "导航R50" } };
        foreach (var g in rows.GroupBy(x => x[1]).OrderBy(g => g.Key))
        {
            sb.AppendLine($"── 形状 {g.Key}「{g.First()[21]}」");
            if (old.TryGetValue(g.Key, out var o)) sb.AppendLine($"  {o}");
            foreach (var chain in chains)
            {
                string[]? prev = null;
                foreach (var t in chain)
                {
                    var c = g.FirstOrDefault(x => x[2] == t);
                    if (c is null) { if (chain[0] == "导航") sb.AppendLine($"  {t}：无片段（未跑或未跑完）"); continue; }
                    double rr = double.Parse(c[4], Inv), pk = double.Parse(c[20], Inv);
                    string cov = double.IsNaN(pk) ? "峰位算不出" : pk + MeshAdapt.PeakMarginMm <= rr + 1e-9 ? "盖住" : "不盖住";
                    sb.AppendLine($"  {t}：格 {c[3]}　R {c[4]}　③ {c[5]}　②″ {c[7]}　②′ {c[9]} {c[10]}　⑦ {c[11]} {c[12]}　⑧ {c[13]} {c[14]}　铂 {c[15]} g　{c[17]} s　全判据 {c[18]}　收敛 {c[19]}　热点 r {c[20]}（{cov}）　〔{c[22]}〕");
                    if (prev is not null)
                    {
                        double D(int i) => double.Parse(c[i], Inv) - double.Parse(prev[i], Inv);
                        sb.AppendLine($"     与上一档（{prev[2]}）差：③ {D(5):+0.000;-0.000}　②″ {D(7):+0.000;-0.000}　②′ {D(9):+0.000;-0.000}　⑦ {D(11):+0.000;-0.000}　⑧ {D(13):+0.000;-0.000}　铂 {D(15):+0.0;-0.0} g");
                    }
                    prev = c;
                }
            }
        }
        string outp = DeliverableOut.Stamped("R48_搜形状赢家_冻结旋钮三档复核.txt");
        File.WriteAllText(outp, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(sb.ToString());
    }
}
