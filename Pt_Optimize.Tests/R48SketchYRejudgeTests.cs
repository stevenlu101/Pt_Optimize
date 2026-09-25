using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 2026-09-25：「拍脑袋Y形」样机（2026-09-10 手画侧 Y 形，SketchShapeTests 喂的几何；09-12 入库 d5006d8 的解出旋钮终值）按决 103 口径复判（只印不判；Linux 预跑、待 Windows 重录）。
/// 规格：deliverable/R48_全局解决方案_出解路线_2026-09-24.md §9.3「能不能直接读」表与 §9.5 第一步对照。
/// 设计复原：几何照 Pt_Optimize.Tests/SketchShapeTests.cs（Builtin[0].Clone、两段 300 mm、控温 1150/1080、管保温 10、盘半径 28、舌半宽 15、舌长 146、锥形、
///   逐片 三角孔 sides 3／孔径 28（等面积圆半径）／拉长比 0.36／转 90°／舌保温种子 3.0）；
///   旋钮终值照 deliverable/拍脑袋Y形_解出/核算与出图.txt「解出的设计」一行与同目录 拍脑袋Y形_整机.spec.json：
///   板厚 0.60/1.02/0.60、舌片厚 2.64/4.57/2.64、叉臂 3.80/6.58/3.80 mm × [−90.32, −29.40]、舌保温 5.7/3.6/11.7、孔心 x = −54.25（spec.json holeX；SketchShapeTests 的种子 −50 被求解器场定改到这里）。
///   舌片厚与叉臂三件是求解器闭式写进 DesignSpec 的派生量，复原时必须填回（R48SchemeCardW08Tests 第一跑漏填舌片厚的教训）。
/// 判决网格：MeshVerify.RequiredMeshFor 那一对；细区盖不住热点就按 MeshAdapt 规则放大重跑（最多两次），写法照 R48SchemeCardW08Tests。
/// 三关：② 带玻璃稳态、③ 空管到温稳态、① 升温全程（RampSweep）；另在决 103 前口径（CriteriaRuleSet.决103前）同网格判一次 ②，与 09-12 档的旧数并列。
/// 不覆盖：09-12 样机解的圆盘槽（spec.json slotDeg 0 = 无槽）与环倍率 1 之外的隐含状态未逐位复原；Rhino 出图；Windows。
/// </summary>
[Trait("速度", "慢")]
public class R48SketchYRejudgeTests
{
    private readonly ITestOutputHelper _o;
    public R48SketchYRejudgeTests(ITestOutputHelper o) { _o = o; }

    /// <summary>旧数（deliverable/拍脑袋Y形_解出/核算与出图.txt，09-12 口径、网格档未写）：只作对照，不入库。</summary>
    private static readonly (string Label, string NameFrag, double Old, string OldWhere)[] OldRows =
    {
        ("法兰最高温 − 管温（整片，含舌片）", "法兰最高温", 6.619, "入口"),
        ("管孔净流入（旧判法，取最小片）", "管孔净流入", 3.376, "入口"),
        ("法兰截面 J", "法兰截面 J", 9.998, "HC1|HC2 舌片 x=-46.1（叉臂 6.58）"),
        ("③ 法兰增量温降（旧判法）", "增量温降", 9.706, "HC2"),
    };

    internal static DesignSpec Sketch()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "拍脑袋Y形（09-12 解出旋钮终值复原，决 103 复判）";
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.TubeInsulMm = 10; d.DiscRadiusMm = 28; d.TabHalfWidthMm = 15; d.TabLengthMm = 146; d.TabTaper = true;
        for (int j = 0; j < d.FlangeCount; j++)
        {
            d.TabHoleSides[j] = 3;
            d.TabHoleRMm[j] = 28;
            d.TabHoleAspect[j] = 0.36;
            d.TabHoleXMm[j] = -54.25234164499795;   // spec.json holeX（09-12 那跑 FieldPlacement 定的）
            d.TabHoleRotDeg[j] = 90;
            d.RingMul[j] = 1.0;
            d.RingMul2[j] = double.NaN;
            d.SlotSpanDeg[j] = 0;
            d.SlotCenterDeg[j] = double.NaN;
        }
        d.TabThickMm = new[] { 0.60, 1.02, 0.60 };
        d.TongueThickMm = new[] { 2.64, 4.57, 2.64 };
        d.TabArmThickMm = new[] { 3.80, 6.58, 3.80 };
        d.TabArmX0Mm = new[] { -90.31955006204096, -90.31955006204096, -90.31955006204096 };
        d.TabArmX1Mm = new[] { -29.397125871538705, -29.397125871538705, -29.397125871538705 };
        d.TabInsulMm = new[] { 5.7, 3.6, 11.7 };
        return d;
    }

    private static string Row(ConstraintOut c)
    {
        double slack = double.IsNaN(c.Actual) ? double.NaN : (c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit);
        return $"　{Criteria.Plain(c.Name)}\t{c.Actual:0.###}\t{(c.LessIsBetter ? "≤" : "≥")} {c.Limit:0.###} {c.Unit}\t裕度 {slack:+0.###;-0.###}\t{(c.Undetermined ? "判不了" : c.Ok ? "过" : "不过")}\t{c.Where}\t[{c.Kind}]";
    }

    private static string RawRow(ConstraintOut c)
        => $"　{(c.Ok ? "✓" : c.Undetermined ? "？" : "✗")} {c.Name,-30} {c.Actual,10:0.000} / {c.Limit,-8:0.###} {c.Unit}　{c.Where}　[{c.Kind}]";

    [Fact]
    public void 拍脑袋Y形样机_决103复判_判决网格三关_只印()
    {
        var d = Sketch();
        var p = new DesignInputs();
        string path = DeliverableOut.Stamped("R48_拍脑袋Y形样机_决103复判.txt");
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); }
        W("# 拍脑袋Y形样机（09-12 解出旋钮终值复原）　决 103 口径复判　判决网格三关（Linux 预跑、待 Windows 重录；只印不判，断言只守解出了）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {System.Runtime.InteropServices.RuntimeInformation.OSDescription}　树 {HandoverDoc.Root()}　同机争用：另有搜形状整夜实跑占 3 核（耗时只作量级）");
        W("工艺参数 new DesignInputs()（决 103 口径，FlangeStabGeomClampFallback 开）；对照口径 new DesignInputs { CriteriaRuleSet = 决103前 }");
        W("设计复原自 Pt_Optimize.Tests/SketchShapeTests.cs（几何）+ deliverable/拍脑袋Y形_解出/核算与出图.txt「解出的设计」+ 同目录 拍脑袋Y形_整机.spec.json（孔心、叉臂带端点）");
        W("复原后：" + d.Describe());
        W($"逐片：板厚 {DesignSpec.Fmt(d.TabThickMm, "0.00")}　舌片厚 {DesignSpec.FmtT(d.TongueThickMm)}　叉臂厚 {DesignSpec.Fmt(d.TabArmThickMm, "0.00")} × [{d.TabArmX0Mm[0]:0.00}, {d.TabArmX1Mm[0]:0.00}]　舌保温 {DesignSpec.Fmt(d.TabInsulMm, "0.0")}　孔径(等面积圆半径) {DesignSpec.Fmt(d.TabHoleRMm, "0.##")}　拉长比 {DesignSpec.Fmt(d.TabHoleAspect, "0.00")}　孔心 {DesignSpec.Fmt(d.TabHoleXMm, "0.00")}　转 {DesignSpec.Fmt(d.TabHoleRotDeg, "0")}°　sides {string.Join("/", d.TabHoleSides)}");

        // 几何闭式：孔的两端 x、切点、叉臂带、各片最紧截面 J（与网格无关）
        var g1 = d.Plate(1, d.DiscFloorMm(p));
        var h = g1.TabHoles.Length > 0 ? g1.TabHoles[0] : default;
        double xLo = double.NaN, xHi = double.NaN, zMax = 0;
        if (g1.TabHoles.Length > 0)
            for (double x = -146; x <= 0; x += 0.5) for (double z = -30; z <= 30; z += 0.5)
                if (h.Contains(x, z)) { xLo = double.IsNaN(xLo) ? x : Math.Min(xLo, x); xHi = double.IsNaN(xHi) ? x : Math.Max(xHi, x); zMax = Math.Max(zMax, Math.Abs(z)); }
        W($"孔（片1）：{h}　外接半径 {h.RMm:0.00}（等面积圆半径 28 折算）　实际范围 x ∈ [{xLo:0.0}, {xHi:0.0}]（长 {xHi - xLo:0.0}），|z| ≤ {zMax:0.0}（宽 {2 * zMax:0.0}）；切点 x = {d.TangentXMm():0.00}，切点半宽 {g1.Tangent().HalfW:0.00}；叉臂带 [{d.TabArmX0Mm[1]:0.00}, {d.TabArmX1Mm[1]:0.00}]");
        var widths = SectionSizing.TabWidths(g1, d.ClampLengthMm);
        var wMin = widths.OrderBy(t => t.WidthMm).First();
        W($"舌片最窄有效宽（片1，两臂合计） {wMin.WidthMm:0.0} mm @ x = {wMin.X:0.0}");
        var dc = DesignCurrent.ForLine(d, p);
        W("闭式截面（设计电流，决 103 口径 J_设计 = " + d.JDesignAPerMm2.ToString("0.#") + "）：" + string.Join("　", Enumerable.Range(0, d.FlangeCount).Select(j =>
        {
            var c = SectionSizing.Worst(d.Plate(j, d.DiscFloorMm(p)), dc.PlateA[j], d.ClampLengthMm);
            return $"片{j} I={dc.PlateA[j]:0} A 最紧 {c.Where} J={c.JAPerMm2:0.000}";
        })));
        W();

        var (fine, radius) = MeshVerify.RequiredMeshFor(d, p);
        var innerR = d.HoleRadiusMm;
        W($"判决网格：细区 {fine:0.000} mm／细区半径初值 {radius:0.000} mm（MeshVerify.RequiredMeshFor）");
        var progress = new SyncProgress<string>(s => _o.WriteLine(s));
        var total = Stopwatch.StartNew();

        LineResult glass = null!; double radiusUsed = radius;
        for (int grow = 0; grow < 3; grow++)
        {
            var mesh = new SolverOptions { FineMm = fine, FineRadiusMm = radiusUsed };
            var lc = d.BuildCase(p); Solver.ApplyCaseMesh(lc, mesh);
            var sw = Stopwatch.StartNew();
            glass = LineRunner.Run(lc, progress);
            W($"── ② 带玻璃稳态（决 103）　半径 {radiusUsed:0.###} mm　粗格 {lc.MeshCoarseMm:0.###} mm　耗时 {sw.Elapsed.TotalSeconds:0} s　解出 {glass.Ok}　收敛 {glass.Converged}　AllOk {glass.AllOk}　铂重 {glass.TotalMassG:0.0} g（管 {glass.TubeMassG:0.0} + 法兰 {glass.FlangeMassG:0.0}）");
            if (!glass.Ok) W("　解出失败：" + glass.Message);
            string? peak = MeshVerify.HotspotVerdict(glass, innerR, radiusUsed);
            double peakR = MeshVerify.HotspotRadiusMm(glass);
            W($"　热点 r = {peakR:0.##} mm：{(peak is null ? "细区盖住" : peak)}");
            if (peak is null || double.IsNaN(peakR)) break;
            double next = Math.Max(peakR + MeshAdapt.PeakMarginMm + MeshAdapt.GrowOvershootCoarseCells * lc.MeshCoarseMm, radiusUsed + lc.MeshCoarseMm);
            W($"　⇒ 放大到 {next:0.###} mm 重跑");
            radiusUsed = next;
        }
        W("判据（带玻璃稳态，决 103；硬判据与目标项）：");
        foreach (var c in glass.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W(Row(c));
        W("参考项：");
        foreach (var c in glass.Checks.Where(c => c.Kind == CheckKind.Reference)) W(Row(c));
        if (glass.MissingChecks.Length > 0) W("缺判：" + string.Join("、", glass.MissingChecks));
        if (glass.FieldUndeterminedReasons.Length > 0) W("场判不了：" + string.Join("；", glass.FieldUndeterminedReasons));
        W("逐片热点位置（圆盘峰 r／舌片峰 r／局部热稳定最紧 r，mm）与管孔净流入 QFromTubeW（正 = 管流入法兰）：");
        foreach (var f in glass.Flanges)
            W($"　{f.Name}\tI={f.CurrentA:0} A\tTmax={f.TMaxC:0.0} °C（盘 {f.TDiscMaxC:0.0}／舌 {f.TTabMaxC:0.0}）\t圆盘峰 r={f.DiscMaxRMm:0.0}\t舌片峰 r={f.TabMaxRMm:0.0}\t局部热稳定 r={f.LocalStabRMm:0.0}\tQFromTube={f.QFromTubeW:+0.000;-0.000} W\tJmax={f.JMaxAPerMm2:0.00}");
        W();

        var meshFinal = new SolverOptions { FineMm = fine, FineRadiusMm = radiusUsed };
        var lcE = d.BuildCase(p, emptyTube: true); Solver.ApplyCaseMesh(lcE, meshFinal);
        var swE = Stopwatch.StartNew();
        var empty = LineRunner.Run(lcE, progress);
        W($"── ③ 空管到温稳态（决 103）　半径 {radiusUsed:0.###} mm　耗时 {swE.Elapsed.TotalSeconds:0} s　解出 {empty.Ok}　收敛 {empty.Converged}　AllOk {empty.AllOk}");
        if (!empty.Ok) W("　解出失败：" + empty.Message);
        foreach (var c in empty.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W(Row(c));
        W("参考项：");
        foreach (var c in empty.Checks.Where(c => c.Kind == CheckKind.Reference)) W(Row(c));
        if (empty.MissingChecks.Length > 0) W("缺判：" + string.Join("、", empty.MissingChecks));
        W();

        var swR = Stopwatch.StartNew();
        var ramp = RampSweep.Run(d.Clone(), p, new RampSweepOptions { RunClampAlt = false, Mesh = meshFinal }, progress);
        W($"── ① 升温全程（决 103）　耗时 {swR.Elapsed.TotalSeconds:0} s　结论：{ramp.Verdict}");
        if (ramp.VerdictDetail.Length > 0) W(ramp.VerdictDetail);
        foreach (var pt in ramp.Points)
            W($"　{pt.SetpointC:0} °C（夹头 {pt.ClampC:0}）　场有效 {pt.FieldValid}{(pt.FieldValid ? "" : "（" + pt.FieldInvalidReason + "）")}　管J(设计) {pt.TubeJDesignAPerMm2:0.###}/{pt.TubeJDesignLimit:0.#}　整片热稳定 {pt.FlangeStabMargin:0.###}　局部热稳定 {pt.LocalStabMargin:0.###}　热稳定判不了 {pt.StabUndetermined}　段电流 {string.Join("/", pt.Segs.Select(s => s.CurrentA.ToString("0")))} A　段管J {string.Join("/", pt.Segs.Select(s => s.TubeJAPerMm2.ToString("0.###")))}");
        W();

        // 同一设计、同一网格，决 103 前口径判一次 ②（只供对照）
        var pPre = new DesignInputs { CriteriaRuleSet = CriteriaRuleSet.决103前 };
        var lcPre = d.BuildCase(pPre); Solver.ApplyCaseMesh(lcPre, meshFinal);
        var swP = Stopwatch.StartNew();
        var pre = LineRunner.Run(lcPre, progress);
        W($"── ② 带玻璃稳态（决 103 前口径，CriteriaRuleSet.决103前；同一设计、同一判决网格）　耗时 {swP.Elapsed.TotalSeconds:0} s　解出 {pre.Ok}　收敛 {pre.Converged}　AllOk {pre.AllOk}　铂重 {pre.TotalMassG:0.0} g");
        foreach (var c in pre.Checks) W(RawRow(c));
        W();

        W("── 旧数对照（deliverable/拍脑袋Y形_解出/核算与出图.txt，09-12 口径，网格档未写；旧 → 新）：");
        foreach (var (label, frag, old, oldWhere) in OldRows)
        {
            var cPre = pre.Checks.FirstOrDefault(c => c.Name.Contains(frag, StringComparison.Ordinal));
            var cNew = glass.Checks.FirstOrDefault(c => c.Name.Contains(frag, StringComparison.Ordinal))
                    ?? empty.Checks.FirstOrDefault(c => c.Name.Contains(frag, StringComparison.Ordinal));
            string preTxt = cPre is null ? "决103前 ② 表无此项" : $"决103前 ②「{cPre.Name}」{cPre.Actual:0.000}（{cPre.Where}）差 {cPre.Actual - old:+0.000;-0.000}";
            string newTxt = cNew is null ? "决103 表无此项" : $"决103「{cNew.Name}」{cNew.Actual:0.000}（{cNew.Where}，{(cNew.Kind)}）差 {cNew.Actual - old:+0.000;-0.000}";
            W($"　{label}：旧 {old:0.000}（{oldWhere}）→ {preTxt}；{newTxt}");
        }
        W("　管孔净流入 逐片（决 103 ②，取最大片判「管接触处流入法兰的净热流 ≤ 0」）：" + string.Join("　", glass.Flanges.Select(f => $"{f.Name} {f.QFromTubeW:+0.000;-0.000} W")));
        W("　差异原因方向（不是量化归因；量化要逐条改回对拍）：");
        W("　　网格：旧数是导航网格（同伴 49.3 分那跑明写 FineMm = 0；24.7 分这跑档内未写），本跑是判决网格（MeshVerify.RequiredMeshFor + 热点放大），细区尺寸与半径都不同；");
        W("　　F3：热场发热改按面（重构 J 的 ρJ²tA 之前口径 ⇒ 面上积分），影响舌片与孔缘发热分布；");
        W("　　F6：孔边电流场改为面上定电位（孔缘 J 与叉臂分流随之变）；");
        W("　　RING：孔环判料（焊环那一圈按料判，影响圆盘孔缘环截面与温度）；");
        W("　　F7／F7′／F7″：细区半径自适应与滞回放大（热点在细区外时放大重解）；");
        W("　　SEG 段电流连续根、夹持导度几何回退（只在决 103 口径生效；决103前那跑不回退）；");
        W("　　口径：法兰截面 J 是闭式、与网格无关，差只来自设计电流（SEG 之后段电流连续根）与 J_设计；法兰最高温 − 管温 与 管孔净流入 同一个量、不同取片规则（旧取最小片、决 103 取最大片）。");
        W();
        W($"══ 三关结论（决 103）：① {ramp.Verdict}　② {(glass.AllOk ? "过" : "不过／判不了")}　③ {(empty.Ok && empty.Converged && empty.AllOk ? "过" : "不过／判不了")}　铂重 {glass.TotalMassG:0.0} g　总耗时 {total.Elapsed.TotalMinutes:0.0} min");
        W("覆盖：本设计在判决网格上的 ②③ 稳态判据表与 ① 升温 8 设定点；决103前 ② 同网格对照。不覆盖：Rhino 出图；09-12 档隐含状态的逐位复原；Windows 记录；网格三档复核；量化归因（改回对拍）。");
        W($"结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(path, sb.ToString());
        _o.WriteLine(sb.ToString());
        Assert.True(glass.Ok, glass.Message);
    }
}
