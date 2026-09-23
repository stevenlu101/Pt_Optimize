using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48（2026-09-14，Opus 5；数值把关人查出逐片圆盘保温还没接全）：**下游行为门**。
///
/// 逐片圆盘保温（DesignSpec.DiscInsulMm → FlangePlate.DiscInsulThickMm；图纸路径 LineCase.DiscInsul3dmPerPlateMm）
/// 读值只有一个口径：<see cref="LineCase.DiscInsulEffectiveAt"/>。本组不查「哪几行调了它」，查**结果**：
///   · 设计甲 整线 FlangeInsulMm = 20、逐片 {5,5,5,5}；设计乙 整线 5、逐片空 ⇒ 每一片实际都是 5 mm。
///     两者 LineRunner.Run 的逐片抽热、盘峰、舌区峰、全部判据行（含整片热稳定、升温期法兰−管峰值、法兰截面 J 的两节点对照）
///     必须**逐位相同** —— 链路上任何一处还读整线的 20，这里就不同。
///   · 对照丙 整线 20、逐片空 ⇒ 必须与乙不同（证明门咬得住：圆盘保温确实改得动这些量）。
///   · 图纸路径（内存厚度场）同样一组，外加对照丙′（判据几何带值、热解不读，必须写备注）；再加一条逐片各不相同的，
///     用生产的 LineRunner.PlateThermalInputs + SolvePlateThermal 逐片重解壳，本片的保温对得上、别片的对不上（下标没错位）。
/// 快门：配套清单与安装报告印出的逐片厚度 = DesignSpec.DiscInsulMmOf = LineCase.DiscInsulEffectiveAt（同源）；
///       取值约定（规则本体 FlangePlate.DiscInsulEffective）；一维管段不读圆盘保温 ⇒ 扫描「flangeInsul」拒绝；旧耦合链按板件；
///       设计电流两节点对照的取值必填且按片；圆盘保温 0 mm 时四个消费方都按裸铂表面；源码门（Core＋UI 读者与调用点钉行数、禁用整线读法）。
/// 结果另写 deliverable/R48_逐片圆盘保温下游行为门_第二版_2026-09-14.txt 与 …_图纸路径_第二版_2026-09-14.txt（出处：本文件；
/// 2026-09-14 Opus 5：第一版两份是返工前的输出，保留不改写）。
/// </summary>
public class R48DiscInsulPerPlateGateTests
{
    private readonly ITestOutputHelper _out;
    public R48DiscInsulPerPlateGateTests(ITestOutputHelper o) { _out = o; }

    private static readonly object FileLock = new();
    private void Say(StringBuilder sb, string file, string s)
    {
        _out.WriteLine(s); sb.AppendLine(s);
        lock (FileLock) { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { } }
    }

    /// <summary>两次整线解逐位比：段、逐片、全部判据行、求解备注。返回差异清单（空 = 逐位相同）。</summary>
    internal static List<string> Diff(LineResult a, LineResult b)
    {
        var d = new List<string>();
        void D(string what, double x, double y) { if (!x.Equals(y)) d.Add($"{what}：{x:R} ≠ {y:R}"); }
        void S(string what, string x, string y) { if (!string.Equals(x, y, StringComparison.Ordinal)) d.Add($"{what}：「{x}」≠「{y}」"); }
        void B(string what, bool x, bool y) { if (x != y) d.Add($"{what}：{x} ≠ {y}"); }
        B("Ok", a.Ok, b.Ok); B("收敛", a.Converged, b.Converged); S("Message", a.Message, b.Message);
        if (a.Segments.Length != b.Segments.Length) d.Add($"段数 {a.Segments.Length} ≠ {b.Segments.Length}");
        for (int i = 0; i < Math.Min(a.Segments.Length, b.Segments.Length); i++)
        {
            var (x, y) = (a.Segments[i], b.Segments[i]);
            D($"段{i} 电流", x.CurrentA, y.CurrentA); D($"段{i} 管根A", x.TRootAC, y.TRootAC); D($"段{i} 管根B", x.TRootBC, y.TRootBC);
            D($"段{i} 增量温降", x.FlangeDipK, y.FlangeDipK);
        }
        if (a.Flanges.Length != b.Flanges.Length) d.Add($"片数 {a.Flanges.Length} ≠ {b.Flanges.Length}");
        for (int j = 0; j < Math.Min(a.Flanges.Length, b.Flanges.Length); j++)
        {
            var (x, y) = (a.Flanges[j], b.Flanges[j]);
            D($"片{j} 抽热", x.QFromTubeW, y.QFromTubeW); D($"片{j} 盘峰", x.TDiscMaxC, y.TDiscMaxC); D($"片{j} 舌区峰", x.TTabMaxC, y.TTabMaxC);
            D($"片{j} 最高温", x.TMaxC, y.TMaxC); D($"片{j} 发热", x.QGenW, y.QGenW); D($"片{j} 散热", x.QLossW, y.QLossW);
            D($"片{j} 舌端温", x.TTabEndC, y.TTabEndC); D($"片{j} 夹持带走", x.QClampW, y.QClampW);
            D($"片{j} 局部热稳定", x.LocalStabMargin, y.LocalStabMargin); D($"片{j} 能量残差", x.EnergyResidualW, y.EnergyResidualW);
            S($"片{j} 圆盘区规则", x.DiscZoneRule, y.DiscZoneRule); S($"片{j} 保温规则", x.InsulRule, y.InsulRule);
        }
        if (a.Checks.Length != b.Checks.Length) d.Add($"判据行数 {a.Checks.Length} ≠ {b.Checks.Length}");
        for (int k = 0; k < Math.Min(a.Checks.Length, b.Checks.Length); k++)
        {
            var (x, y) = (a.Checks[k], b.Checks[k]);
            S($"判据{k} 名", x.Name, y.Name);
            D($"判据「{x.Name}」实际", x.Actual, y.Actual); D($"判据「{x.Name}」限值", x.Limit, y.Limit);
            B($"判据「{x.Name}」过", x.Ok, y.Ok); B($"判据「{x.Name}」无法判定", x.Undetermined, y.Undetermined);
            S($"判据「{x.Name}」位置", x.Where, y.Where); S($"判据「{x.Name}」说明", x.Note, y.Note);
        }
        if (a.Notes.Count != b.Notes.Count) d.Add($"求解备注条数 {a.Notes.Count} ≠ {b.Notes.Count}");
        for (int k = 0; k < Math.Min(a.Notes.Count, b.Notes.Count); k++) S($"求解备注{k}", a.Notes[k], b.Notes[k]);
        return d;
    }

    private static ConstraintOut Row(LineResult r, string key)
    {
        var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
        Assert.True(c is not null, $"判据表里没有「{key}」这一行 —— 门比不到它");
        return c!;
    }

    // ════════════════════════════════════════════════════════════════
    //  慢门一：解析路径（W08、默认三段、导航网格、不加密）
    // ════════════════════════════════════════════════════════════════

    [Trait("速度", "慢")]
    [Fact]
    public void 解析路径_逐片5与整线5逐位相同_整线20对照不同()
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string file = DeliverableOut.Stamped("R48_逐片圆盘保温下游行为门_第二版_2026-09-14.txt");
        void Say(string s) => this.Say(sb, file, s);
        Say($"R48 逐片圆盘保温下游行为门 第二版（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　出处：Pt_Optimize.Tests/R48DiscInsulPerPlateGateTests.cs");
        Say("── 慢门一：解析路径　基准 DesignSpec.W08（默认段数）、LineCase 默认导航网格、BuildCase(p) 默认判升温");

        var p = new DesignInputs();
        DesignSpec Make(double whole, double[] perPlate)
        {
            var d = DesignSpec.W08.Clone();
            d.FlangeInsulMm = whole; d.FlangeInsulated = true;
            d.DiscInsulMm = (double[])perPlate.Clone();
            return d.Fit();
        }
        int nf = DesignSpec.W08.Clone().Fit().FlangeCount;
        var dA = Make(20.0, Enumerable.Repeat(5.0, nf).ToArray());   // 甲
        var dB = Make(5.0, Array.Empty<double>());                    // 乙
        var dC = Make(20.0, Array.Empty<double>());                   // 丙（对照）
        var lcA = dA.BuildCase(p); var lcB = dB.BuildCase(p); var lcC = dC.BuildCase(p);

        // 前提：甲乙的整线值确实不同、逐片实际取值确实相同 —— 否则「逐位相同」是空转
        Assert.Equal(20.0, lcA.Base.FlangeInsulThickMm);
        Assert.Equal(5.0, lcB.Base.FlangeInsulThickMm);
        Assert.Equal(20.0, lcC.Base.FlangeInsulThickMm);
        for (int j = 0; j < nf; j++)
        {
            Assert.Equal(5.0, lcA.FlangePlates[j].DiscInsulThickMm);
            Assert.True(double.IsNaN(lcB.FlangePlates[j].DiscInsulThickMm));
            Assert.Equal(5.0, lcA.DiscInsulEffectiveAt(j));
            Assert.Equal(5.0, lcB.DiscInsulEffectiveAt(j));
            Assert.Equal(20.0, lcC.DiscInsulEffectiveAt(j));
        }
        Say($"网格：细 {lcA.MeshFineMm} ／粗 {lcA.MeshCoarseMm} ／内带 {lcA.MeshInnerMm} mm；段数 {lcA.SegmentCount}、片数 {nf}");

        LineResult Run(string name, LineCase lc)
        {
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            Say($"[{name}] Ok {r.Ok}　收敛 {r.Converged}　耗时 {sw.Elapsed.TotalSeconds:0} s　{(r.Ok ? "" : r.Message)}");
            for (int j = 0; j < r.Flanges.Length; j++)
            {
                var f = r.Flanges[j];
                Say($"    片{j} {f.Name}：抽热 {f.QFromTubeW:R} W　盘峰 {f.TDiscMaxC:R} °C　舌区峰 {f.TTabMaxC:R} °C");
            }
            foreach (var key in new[] { LineResult.Key.FlangeStab, LineResult.Key.RampField, LineResult.Key.SectionJ })
            {
                var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
                Say($"    {key}：{(c is null ? "（没有这一行）" : $"实际 {c.Actual:R}　{c.Note}")}");
            }
            return r;
        }
        var rA = Run("甲 整线20 逐片5", lcA);
        var rB = Run("乙 整线5 逐片空", lcB);
        var rC = Run("丙 整线20 逐片空（对照）", lcC);
        Assert.True(rA.Ok, rA.Message); Assert.True(rB.Ok, rB.Message); Assert.True(rC.Ok, rC.Message);
        // ★ R48 G2 复审（2026-09-15 Opus 5）：升温期法兰−管峰值那一行复核前判不了、不给数 ⇒ 升温两节点的数改从生产函数 LineRunner.FlangeLumped 取来比
        var lA = LineRunner.FlangeLumped(lcA, rA.Flanges)!; var lB = LineRunner.FlangeLumped(lcB, rB.Flanges)!; var lC = LineRunner.FlangeLumped(lcC, rC.Flanges)!;
        foreach (var (nm, l) in new[] { ("甲", lA), ("乙", lB), ("丙", lC) })
            Say($"    {nm} 升温两节点（不进判据表）：{(l.Ramp is { } rt ? $"法兰−管峰值 {rt.MaxFlangeMinusTubeK:R} K" : "算不出来 " + l.RampError)}");

        // 三条会读圆盘保温的参考行都在、都判得出（否则比的是两个 NaN）
        // ★ R48 G2 复审（2026-09-15 Opus 5）有意改断言：原 Assert.False(double.IsNaN(升温那一行.Actual)) → 现「那一行判不了」+「FlangeLumped 解出了升温两节点」。
        //   依据：审查意见 blocker「修后的数仍作为正常参考量进判据表」—— 物理把关人复核前那一行不许给数；门比的数改为 FlangeLumped 的升温结果（下面 rampDiff 与甲乙逐位）。
        foreach (var r in new[] { rA, rB })
        {
            Assert.False(Row(r, LineResult.Key.FlangeStab).Undetermined, Row(r, LineResult.Key.FlangeStab).Note);
            Assert.True(Row(r, LineResult.Key.RampField).Undetermined, Row(r, LineResult.Key.RampField).Note);
            Assert.Contains("两节点", Row(r, LineResult.Key.SectionJ).Note);
        }
        // ★ R48 G2 复审首跑（2026-09-15 Opus 5）如实写明：W08 三段最不利片（入口）场里管孔流入 −3.0 W（热从法兰流进管）、节点均温 1090 °C 低于管根 1157 °C
        //   ⇒ 管孔一项标定不了、升温两节点不解（甲乙丙三份都是）⇒ 原先比峰值的做法在本算例上没有数可比。门改为：两份都解出来就比峰值；
        //   否则比升温两节点**实际吃的**圆盘保温 —— 同一温度上的表面散热配方值（RampTwoNode.Model.FlangeSurfaceRecipeW，圆盘面按 RampP 的圆盘保温取表）。门的判读（甲≡乙、乙≠丙）没改。
        double RampProbe(LineRunner.FlangeLumpedOut l) => l.Ramp?.MaxFlangeMinusTubeK ?? new RampTwoNode.Model(l.RampP!, l.RampIn!).FlangeSurfaceRecipeW(1000.0);
        foreach (var l in new[] { lA, lB, lC }) Assert.True(l.Ramp is not null || (l.RampIn is not null && l.RampP is not null && l.RampError.Length > 0), "升温两节点既没解出来也没说为什么：" + l.RampError);
        Assert.True((lA.Ramp is null) == (lB.Ramp is null), "甲乙一个解得出一个解不出 ⇒ 链路上还有地方读整线圆盘保温");
        Assert.Equal(RampProbe(lB), RampProbe(lA));

        var diffAB = Diff(rA, rB);
        Say($"甲 vs 乙：{(diffAB.Count == 0 ? "逐位相同" : $"{diffAB.Count} 处不同")}");
        foreach (var s in diffAB.Take(40)) Say("    " + s);

        var diffBC = Diff(rB, rC);
        bool stabDiff = diffBC.Any(s => s.StartsWith($"判据「{LineResult.Key.FlangeStab}", StringComparison.Ordinal));
        bool rampDiff = (lB.Ramp is null) == (lC.Ramp is null) ? RampProbe(lB) != RampProbe(lC) : true;   // R48 G2 复审：原比判据表那一行的实际值（现在是 NaN），见上
        bool secJDiff = diffBC.Any(s => s.StartsWith($"判据「{LineResult.Key.SectionJ}」说明", StringComparison.Ordinal));
        bool drawDiff = diffBC.Any(s => s.Contains(" 抽热："));
        Say($"    升温两节点比的量：甲 {RampProbe(lA):R}　乙 {RampProbe(lB):R}　丙 {RampProbe(lC):R}（{(lB.Ramp is null ? "解不出，比 1000 °C 上的表面散热配方值 W" : "法兰−管峰值 K")}）");
        Say($"乙 vs 丙（对照，门咬不咬得住）：{diffBC.Count} 处不同；逐片抽热 {(drawDiff ? "变" : "不变")}、整片热稳定 {(stabDiff ? "变" : "不变")}、"
          + $"升温期法兰−管峰值 {(rampDiff ? "变" : "不变")}、法兰截面 J 说明（两节点对照）{(secJDiff ? "变" : "不变")}");
        foreach (var s in diffBC.Where(s => s.StartsWith("判据「" + LineResult.Key.SectionJ, StringComparison.Ordinal))) Say("    " + s);

        Assert.True(diffAB.Count == 0, "逐片 5 与整线 5 的整线解不同 ⇒ 链路上还有地方读整线圆盘保温：\n" + string.Join("\n", diffAB.Take(20)));
        Assert.True(drawDiff, "对照丙（整线 20）与乙（整线 5）的逐片抽热相同 ⇒ 圆盘保温改不动热场，本门是空转");
        Assert.True(stabDiff, "对照丙与乙的整片热稳定相同 ⇒ 这一行对圆盘保温不敏感，门在它上面空转");
        Assert.True(rampDiff, "对照丙与乙的升温期法兰−管峰值相同 ⇒ 这一行对圆盘保温不敏感，门在它上面空转");
        Assert.True(secJDiff, "对照丙与乙的法兰截面 J 说明相同 ⇒ 两节点对照对圆盘保温不敏感（取整后看不出），门在它上面空转");
    }

    // ════════════════════════════════════════════════════════════════
    //  慢门二：图纸路径（内存厚度场；与 TabInsulPerPlateTests 同一做法）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 图纸路径（内存厚度场）：
    ///   · 甲′（整线 20、逐片数组 5）≡ 乙′（整线 5、数组空）逐位相同，且三条读圆盘保温的参考行（整片热稳定、升温期法兰−管峰值、法兰截面 J 的两节点对照）在图纸路径上都判得出；
    ///   · 丙′（整线 20、数组空，但**判据几何上带了逐片 5**）与乙′ 在逐片抽热和这三行上**必须不同** —— 既证明这几处在图纸路径上对圆盘保温敏感（门不空转），
    ///     也证明判据几何上的 5 没有漏进这几处（漏进哪处，哪处就与乙′ 相同）；求解备注必须逐片写明「判据几何上的值没有用上」；
    ///   · 丁′（逐片 3.5/7.5/12）：用生产的 LineRunner.PlateThermalInputs + SolvePlateThermal 在整线解的网格、电流场、管根温度上逐片重解，
    ///     显式给本片的值 ⇒ 温度场与抽热逐位相同；给别片的值 ⇒ 对不上（下标没错位）。
    ///     2026-09-14 Opus 5 改：原先这段在测试里手抄了逐片热解配方（审查意见「门不许手抄生产配方」），现调生产函数。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 图纸路径_逐片数组与整线值逐位相同_逐片各异时各片用各自的值()
    {
        var sb = new StringBuilder();
        string file = DeliverableOut.Stamped("R48_逐片圆盘保温下游行为门_图纸路径_第二版_2026-09-14.txt");
        void Say(string s) => this.Say(sb, file, s);
        Say($"R48 逐片圆盘保温下游行为门·图纸路径 第二版（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　出处：Pt_Optimize.Tests/R48DiscInsulPerPlateGateTests.cs");
        Say("── 基准 R47NavGridInstrumentTests.Disc56TwoSegs（两段三片）；解析板栅格化成厚度场（步 0.5、留白 2），GeomForJudge = 同一组板，舌保温逐片 = 设计值");

        var p = new DesignInputs();
        var d0 = R47NavGridInstrumentTests.Disc56TwoSegs();
        d0.FlangeInsulated = true;
        int nf = d0.FlangeCount;
        Assert.Equal(3, nf);
        LineCase Make(double whole, double[] perPlate, double[]? onJudgeGeom = null)
        {
            var d = d0.Clone();
            d.FlangeInsulMm = whole;
            var lc = d.BuildCase(p, checkRamp: false);
            double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
            var plates = Enumerable.Range(0, nf).Select(j => { var g = d0.Plate(j, d0.DiscFloorMm(p)); g.HoleRadiusMm = holeR; return g; }).ToArray();
            lc.FlangePlates = Array.Empty<FlangePlate>();
            lc.FlangeFields = plates.Select(g => AnalyticSurrogate.Rasterize(g, 0.5, 2.0)).ToArray();
            if (onJudgeGeom is not null) for (int j = 0; j < nf; j++) plates[j].DiscInsulThickMm = onJudgeGeom[j];
            lc.GeomForJudge = plates;
            lc.TabInsul3dmPerPlateMm = (double[])d0.TabInsulMm.Clone();
            lc.DiscInsul3dmPerPlateMm = (double[])perPlate.Clone();
            return lc;
        }
        var lcA = Make(20.0, new[] { 5.0, 5.0, 5.0 });                                        // 甲′
        var lcB = Make(5.0, Array.Empty<double>());                                            // 乙′
        var lcC = Make(20.0, Array.Empty<double>(), onJudgeGeom: new[] { 5.0, 5.0, 5.0 });     // 丙′：对照 + 判据几何带值
        double[] own = { 3.5, 7.5, 12.0 };
        var lcD = Make(20.0, own);                                                             // 丁′：逐片各异
        Assert.True(double.IsNaN(lcA.GeomForJudge[0].DiscInsulThickMm), "等效片不带逐片圆盘保温（与界面一致），取值只能来自 DiscInsul3dmPerPlateMm");
        for (int j = 0; j < nf; j++)
        {
            Assert.Equal(5.0, lcA.DiscInsulEffectiveAt(j));
            Assert.Equal(5.0, lcB.DiscInsulEffectiveAt(j));
            Assert.Equal(20.0, lcC.DiscInsulEffectiveAt(j));
            Assert.True(lcC.JudgeGeomDiscInsulIgnored(j, out double onG, out double used) && onG == 5.0 && used == 20.0);
            Assert.False(lcA.JudgeGeomDiscInsulIgnored(j, out _, out _));
            Assert.Equal(own[j], lcD.DiscInsulEffectiveAt(j));
        }
        // CloneCase（定尺寸与加密复算的副本）要带上它
        Assert.Equal(own, FlangeAutoSizer.CloneCase(lcD).DiscInsul3dmPerPlateMm);

        LineResult Run(string name, LineCase lc)
        {
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            Say($"[{name}] Ok {r.Ok}　收敛 {r.Converged}　耗时 {sw.Elapsed.TotalSeconds:0} s　{(r.Ok ? "" : r.Message)}");
            for (int j = 0; j < r.Flanges.Length; j++)
                Say($"    片{j} {r.Flanges[j].Name}：抽热 {r.Flanges[j].QFromTubeW:R} W　盘峰 {r.Flanges[j].TDiscMaxC:R} °C　舌区峰 {r.Flanges[j].TTabMaxC:R} °C");
            foreach (var key in new[] { LineResult.Key.FlangeStab, LineResult.Key.RampField, LineResult.Key.SectionJ })
            {
                var c = r.Checks.FirstOrDefault(x => x.Name.StartsWith(key, StringComparison.Ordinal));
                Say($"    {key}：{(c is null ? "（没有这一行）" : $"实际 {c.Actual:R}　{c.Note}")}");
            }
            foreach (var s in r.Notes.Where(s => s.Contains("判据几何上带了本片圆盘保温"))) Say("    备注：" + s);
            return r;
        }
        var rA = Run("甲′ 整线20 逐片5", lcA);
        var rB = Run("乙′ 整线5 逐片空", lcB);
        var rC = Run("丙′ 整线20 逐片空（对照；判据几何带逐片5）", lcC);
        var rD = Run("丁′ 整线20 逐片 3.5/7.5/12", lcD);
        Assert.True(rA.Ok, rA.Message); Assert.True(rB.Ok, rB.Message); Assert.True(rC.Ok, rC.Message); Assert.True(rD.Ok, rD.Message);
        // ★ R48 G2 复审（2026-09-15 Opus 5）：升温期法兰−管峰值那一行复核前判不了、不给数 ⇒ 升温两节点的数改从生产函数 LineRunner.FlangeLumped 取来比
        var lA = LineRunner.FlangeLumped(lcA, rA.Flanges)!; var lB = LineRunner.FlangeLumped(lcB, rB.Flanges)!; var lC = LineRunner.FlangeLumped(lcC, rC.Flanges)!;
        foreach (var (nm, l) in new[] { ("甲′", lA), ("乙′", lB), ("丙′", lC) })
            Say($"    {nm} 升温两节点（不进判据表）：{(l.Ramp is { } rt ? $"法兰−管峰值 {rt.MaxFlangeMinusTubeK:R} K" : "算不出来 " + l.RampError)}　网格取自本片场 {l.MeshFromField}");

        // 三条会读圆盘保温的参考行在图纸路径上都在、都判得出（否则「逐位相同」比的是两个 NaN）
        // ★ R48 G2 复审（2026-09-15 Opus 5）有意改断言：原 Assert.False(double.IsNaN(升温那一行.Actual)) → 现「那一行判不了」+「FlangeLumped 解出了升温两节点、网格取自本片场」。
        //   依据：审查意见 blocker（那一行复核前不许给数）与 minor（图纸路径上节点质量与标定要在同一张网格上）。
        foreach (var r in new[] { rA, rB })
        {
            Assert.False(Row(r, LineResult.Key.FlangeStab).Undetermined, Row(r, LineResult.Key.FlangeStab).Note);
            Assert.False(double.IsNaN(Row(r, LineResult.Key.FlangeStab).Actual), Row(r, LineResult.Key.FlangeStab).Note);
            Assert.True(Row(r, LineResult.Key.RampField).Undetermined, Row(r, LineResult.Key.RampField).Note);
            Assert.Contains("两节点", Row(r, LineResult.Key.SectionJ).Note);
        }
        foreach (var l in new[] { lA, lB, lC }) { Assert.True(l.Ramp is not null, "升温两节点算不出来：" + l.RampError); Assert.True(l.MeshFromField); }
        Assert.Equal(lB.Ramp!.MaxFlangeMinusTubeK, lA.Ramp!.MaxFlangeMinusTubeK);

        var diffAB = Diff(rA, rB);
        Say($"甲′ vs 乙′：{(diffAB.Count == 0 ? "逐位相同" : $"{diffAB.Count} 处不同")}");
        foreach (var s in diffAB.Take(40)) Say("    " + s);
        Assert.True(diffAB.Count == 0, "图纸路径逐片 5 与整线 5 不同 ⇒ 图纸路径上还有地方读整线圆盘保温：\n" + string.Join("\n", diffAB.Take(20)));

        var diffBC = Diff(rB, rC);
        bool stabDiff = diffBC.Any(s => s.StartsWith($"判据「{LineResult.Key.FlangeStab}", StringComparison.Ordinal));
        bool rampDiff = lB.Ramp!.MaxFlangeMinusTubeK != lC.Ramp!.MaxFlangeMinusTubeK;   // R48 G2 复审：原比判据表那一行的实际值（现在是 NaN）
        bool secJDiff = diffBC.Any(s => s.StartsWith($"判据「{LineResult.Key.SectionJ}」说明", StringComparison.Ordinal));
        bool drawDiff = diffBC.Any(s => s.Contains(" 抽热："));
        Say($"乙′ vs 丙′（对照，门咬不咬得住；判据几何上的 5 有没有漏进来）：{diffBC.Count} 处不同；逐片抽热 {(drawDiff ? "变" : "不变")}、整片热稳定 {(stabDiff ? "变" : "不变")}、"
          + $"升温期法兰−管峰值 {(rampDiff ? "变" : "不变")}、法兰截面 J 说明（两节点对照）{(secJDiff ? "变" : "不变")}");
        foreach (var s in diffBC.Where(s => s.StartsWith("判据「" + LineResult.Key.SectionJ, StringComparison.Ordinal))) Say("    " + s);
        Assert.True(drawDiff, "丙′（整线 20）与乙′（整线 5）的逐片抽热相同 ⇒ 圆盘保温在图纸路径上改不动热场，或判据几何上的 5 漏进了逐片热解");
        Assert.True(stabDiff, "丙′ 与乙′ 的整片热稳定相同 ⇒ 这一行在图纸路径上对圆盘保温不敏感，或读了判据几何上的 5");
        Assert.True(rampDiff, "丙′ 与乙′ 的升温期法兰−管峰值相同 ⇒ 这一行在图纸路径上对圆盘保温不敏感，或读了判据几何上的 5");
        Assert.True(secJDiff, "丙′ 与乙′ 的法兰截面 J 说明相同 ⇒ 两节点对照在图纸路径上对圆盘保温不敏感，或读了判据几何上的 5");
        // 判据几何带了值而没用上 ⇒ 备注逐片写明；没带、或用的就是它 ⇒ 不写
        for (int j = 0; j < nf; j++)
        {
            string name = rC.Flanges[j].Name;
            Assert.Contains(rC.Notes, s => s.Contains("判据几何上带了本片圆盘保温 5.0 mm", StringComparison.Ordinal)
                                         && s.Contains("按 20.0 mm 算", StringComparison.Ordinal)
                                         && s.Contains(name, StringComparison.Ordinal));
        }
        foreach (var r in new[] { rA, rB, rD })
            Assert.DoesNotContain(r.Notes, s => s.Contains("判据几何上带了本片圆盘保温", StringComparison.Ordinal));

        // 丁′：逐片单独重解壳 —— 网格、电流场、管根温度取整线解的（FlangeOut.Mesh／JField／TRootC；JField 是最后一次电流场，σ(T) 开着也对得上），
        //       输入组装与热解都调生产函数，只把圆盘保温显式换成本片／别片的值。
        //   2026-09-23（F3 审查后改）：整线逐片热解发热吃发热等效 J（面发热）、局部量吃重构 J；FlangeOut 上只有重构 J（JField），没有发热 J ⇒
        //   用生产的 PlateCurrentField 在同一网格、同一电流上重算电流场取 HeatJAPerMm2，并先证重算的就是整线那一份（重构 J 逐位 = JField；σ(T) 缺省关时成立，开着则本断言先红）。
        for (int j = 0; j < nf; j++)
        {
            var f = rD.Flanges[j];
            Assert.NotNull(f.Mesh);
            var scD = LineRunner.PlateCurrentField(lcD, f.Mesh!, j, f.CurrentA);
            Assert.Equal(f.JField, scD.JMagAPerMm2);
            ShellThermalResult Th(double discInsul)
            {
                var s = LineRunner.PlateThermalInputs(lcD, j, f.CurrentA, f.Mesh!.SourceField);
                s.P2.FlangeInsulThickMm = discInsul;
                return LineRunner.SolvePlateThermal(f.Mesh, scD.HeatJAPerMm2, f.TRootC, s, jLocalAPerMm2: scD.JMagAPerMm2);
            }
            var thOwn = Th(own[j]); var thOther = Th(own[(j + 1) % nf]);
            Say($"丁′ 片{j} {f.Name}：整线抽热 {f.QFromTubeW:R} W；单独重解 本片圆盘保温 {own[j]} ⇒ {thOwn.QFromTubeW:R} W；别片 {own[(j + 1) % nf]} ⇒ {thOther.QFromTubeW:R} W");
            Assert.Equal(f.QFromTubeW, thOwn.QFromTubeW);
            Assert.Equal(f.TField, thOwn.T);
            Assert.True(Math.Abs(f.QFromTubeW - thOther.QFromTubeW) > 1e-3, $"片{j} 换成别片的圆盘保温应当对不上，否则这条门没咬住");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  快门
    // ════════════════════════════════════════════════════════════════

    /// <summary>从文字里抠出「逐片 a / b / c mm」的逐片厚度（配套清单与安装报告同一写法）。</summary>
    private static List<double[]> PerPlateNumbers(string text)
        => Regex.Matches(text, @"圆盘双面包，逐片 ([0-9.]+(?: / [0-9.]+)*) mm")
                .Select(m => m.Groups[1].Value.Split(" / ").Select(double.Parse).ToArray()).ToList();

    /// <summary>
    /// 配套清单（FlangeKit.Text）与安装报告（InstallReport.Build，第 4、5 节各印一次）印出的逐片圆盘保温
    /// = DesignSpec.DiscInsulMmOf(j) = BuildCase 后 LineCase.DiscInsulEffectiveAt(j)（算例里实际用的）——三者同源。
    /// 值取 0.5 mm 的倍数（用户 2026-09-14「每层 0.5 mm」），印一位小数读回是精确的。
    /// </summary>
    [Fact]
    public void 配套清单与安装报告印的逐片圆盘保温与算例同源()
    {
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        d.DiscInsulMm = new[] { 3.5, 5.0, 7.5, 10.0 };
        d.FlangeInsulated = true;
        Assert.Equal(4, d.FlangeCount);
        var lc = d.BuildCase(p);
        int nf = d.FlangeCount;
        var r = new LineResult
        {
            Ok = true, Converged = true, TubeMassG = 1800, FlangeMassG = 1200, TotalMassG = 3000,
            Segments = Enumerable.Range(0, d.SetpointC.Length).Select(i => new SegmentOut { Name = $"HC{i + 1}", SetpointC = d.SetpointC[i], CurrentA = 979 }).ToArray(),
            Flanges = Enumerable.Range(0, nf).Select(j => new FlangeOut { Name = $"片{j}", CurrentA = 979, BusSectionForCurrentMm2 = 489.5, MassG = 300 }).ToArray(),
        };
        string kit = FlangeKit.Text(FlangeKit.Build(r, d, p), d, p);
        string rep = InstallReport.Build(r, d, p, "", new DateTime(2026, 9, 14, 12, 0, 0));
        var kitNums = PerPlateNumbers(kit);
        var repNums = PerPlateNumbers(rep);
        Assert.Single(kitNums);
        Assert.Equal(2, repNums.Count);   // 第 4 节（配套清单）＋ 第 5 节（保温）
        foreach (var arr in kitNums.Concat(repNums))
        {
            Assert.Equal(nf, arr.Length);
            for (int j = 0; j < nf; j++)
            {
                Assert.Equal(d.DiscInsulMmOf(j), arr[j]);
                Assert.Equal(lc.DiscInsulEffectiveAt(j), arr[j]);
            }
        }
        Assert.Equal(d.DiscInsulMm, Enumerable.Range(0, nf).Select(lc.DiscInsulEffectiveAt).ToArray());

        // 逐片全相同 ⇒ 印一个数；不包 ⇒ 印「圆盘不包」且算例取值全 0
        var same = d.Clone(); same.DiscInsulMm = new[] { 6.5, 6.5, 6.5, 6.5 };
        Assert.Contains("圆盘双面包 6.5 mm", FlangeKit.Text(FlangeKit.Build(r, same, p), same, p));
        var bare = d.Clone(); bare.FlangeInsulated = false;
        var lcBare = bare.BuildCase(p);
        Assert.Contains("圆盘不包", InstallReport.Build(r, bare, p));
        Assert.All(Enumerable.Range(0, nf), j => { Assert.Equal(0.0, bare.DiscInsulMmOf(j)); Assert.Equal(0.0, lcBare.DiscInsulEffectiveAt(j)); });
        // 某片 NaN ⇒ 板件按沿用整线，清单口径也回整线值（2026-09-14 前 DiscInsulMmOf 回 NaN，与算例不同源）
        var partial = d.Clone(); partial.DiscInsulMm = new[] { 3.5, double.NaN, 7.5, 10.0 };
        var lcPartial = partial.BuildCase(p);
        Assert.All(Enumerable.Range(0, nf), j => Assert.Equal(partial.DiscInsulMmOf(j), lcPartial.DiscInsulEffectiveAt(j)));
        Assert.Equal(partial.FlangeInsulMm, lcPartial.DiscInsulEffectiveAt(1));
    }

    /// <summary>
    /// LineCase.DiscInsulEffectiveAt 的取值约定（解析 = 板件；图纸 = 数组，NaN／空／越界 = 整线，整线不包 = 0），
    /// 规则本体 FlangePlate.DiscInsulEffective 与三个调用口（板件、LineCase 图纸分支、DesignSpec.DiscInsulMmOf）同源，
    /// 以及「判据几何带了逐片值而图纸路径没用上」的判定。
    /// </summary>
    [Fact]
    public void 唯一取值口径的约定()
    {
        // 规则本体
        Assert.Equal(7.0, FlangePlate.DiscInsulEffective(double.NaN, 7.0, true));
        Assert.Equal(3.0, FlangePlate.DiscInsulEffective(3.0, 7.0, true));
        Assert.Equal(0.0, FlangePlate.DiscInsulEffective(3.0, 0.0, false));
        Assert.Equal(0.0, FlangePlate.DiscInsulEffective(double.NaN, 0.0, false));
        Assert.Equal(7.0, FlangePlate.DiscInsulEffective(new[] { 3.0 }, 1, 7.0, true));    // 越界 = 沿用整线
        Assert.Equal(7.0, FlangePlate.DiscInsulEffective(null, 0, 7.0, true));

        var baseP = new DesignInputs { FlangeInsulThickMm = 9.0, FlangeInsulated = true };
        // 图纸路径
        var lc = new LineCase { Base = baseP };
        Assert.Equal(9.0, lc.DiscInsulEffectiveAt(0));
        lc.DiscInsul3dmPerPlateMm = new[] { 2.5, double.NaN, 4.0 };
        Assert.Equal(2.5, lc.DiscInsulEffectiveAt(0));
        Assert.Equal(9.0, lc.DiscInsulEffectiveAt(1));      // NaN = 沿用整线
        Assert.Equal(4.0, lc.DiscInsulEffectiveAt(2));
        // ★ 2026-09-14 Opus 5 有意改（本路自己的新测试，非既有记录值）：越界 旧 4.0（短了用最后一片）→ 新 9.0（沿用整线）。
        //   原因：审查意见「短数组约定不一致」—— DesignSpec.Plate／DiscInsulMmOf 越界是整线，图纸数组却是最后一片；统一成整线（圆盘保温有整线值可退，不猜）。
        //   依据文件：Pt_Optimize/Core/PlateCurrent2D.cs（FlangePlate.DiscInsulEffective 数组形态）。
        Assert.Equal(9.0, lc.DiscInsulEffectiveAt(5));
        lc.Base = new DesignInputs { FlangeInsulThickMm = 0.0, FlangeInsulated = false };
        Assert.Equal(0.0, lc.DiscInsulEffectiveAt(0));      // 整线不包 ⇒ 有值也 0
        // 解析路径：板件优先，数组不读
        var an = new LineCase
        {
            Base = baseP,
            FlangePlates = new[] { new FlangePlate { DiscInsulThickMm = 1.5 }, new FlangePlate() },
            DiscInsul3dmPerPlateMm = new[] { 7.0, 7.0 },
        };
        Assert.Equal(1.5, an.DiscInsulEffectiveAt(0));
        Assert.Equal(9.0, an.DiscInsulEffectiveAt(1));
        Assert.Equal(an.FlangePlates[0].DiscInsulEffectiveMm(an.Base), an.DiscInsulEffectiveAt(0));
        Assert.False(an.JudgeGeomDiscInsulIgnored(0, out _, out _));   // 解析路径不适用
        // 定尺寸／加密复算的副本带上图纸路径的逐片值
        Assert.Equal(lc.DiscInsul3dmPerPlateMm, FlangeAutoSizer.CloneCase(lc).DiscInsul3dmPerPlateMm);

        // 设计记录的短数组（没 Fit）：清单口径与算例同样按整线
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone().Fit();
        d.FlangeInsulated = true; d.FlangeInsulMm = 11.5;
        d.DiscInsulMm = new[] { 2.5, 6.0 };
        var lcD = d.BuildCase(p);
        for (int j = 0; j < d.FlangeCount; j++) Assert.Equal(d.DiscInsulMmOf(j), lcD.DiscInsulEffectiveAt(j));
        Assert.Equal(11.5, lcD.DiscInsulEffectiveAt(d.FlangeCount - 1));
        Assert.Equal(d.WholeLineDiscInsulMm, lcD.Base.FlangeInsulThickMm);

        // 图纸路径：判据几何带了逐片值而热解没用上 ⇒ true（LineRunner 据此写备注）；没带、或用的就是它 ⇒ false
        var dr = new LineCase
        {
            Base = baseP,
            GeomForJudge = new[] { new FlangePlate { DiscInsulThickMm = 5.0 }, new FlangePlate() },
        };
        Assert.True(dr.JudgeGeomDiscInsulIgnored(0, out double onG, out double used));
        Assert.Equal(5.0, onG); Assert.Equal(9.0, used);
        Assert.False(dr.JudgeGeomDiscInsulIgnored(1, out _, out _));
        dr.DiscInsul3dmPerPlateMm = new[] { 5.0, double.NaN };
        Assert.False(dr.JudgeGeomDiscInsulIgnored(0, out _, out _));
    }

    /// <summary>
    /// SegmentSolver.Sweep 的「flangeInsul」查链路：每一点只调一维管段 Solve，而 Solve **不读**圆盘保温（下面实测两端 0 与 50 mm 逐位相同）
    /// ⇒ 这一支对任何设计（含带逐片值的）都扫不出东西，2026-09-14 起拒绝并说明。哪天管段模型开始读圆盘保温，第一段会红，届时再议。
    /// </summary>
    [Fact]
    public void 一维管段不读圆盘保温_扫描flangeInsul拒绝并说明()
    {
        var p0 = new DesignInputs { FlangeInsulThickMm = 0.0, FlangeInsulated = false };
        var p1 = SegmentSolver.Clone(p0); p1.FlangeInsulThickMm = 50.0; p1.FlangeInsulated = true;
        var a = SegmentSolver.Solve(p0); var b = SegmentSolver.Solve(p1);
        Assert.True(a.Ok && b.Ok, a.Message + b.Message);
        Assert.Equal(a.CurrentA, b.CurrentA);
        Assert.Equal(a.TFlangeAC, b.TFlangeAC);
        Assert.Equal(a.TFlangeBC, b.TFlangeBC);
        Assert.Equal(a.LossPerMeterWPerM, b.LossPerMeterWPerM);
        Assert.Equal(a.TMinC, b.TMinC);
        Assert.Equal(a.DevitMarginMinK, b.DevitMarginMinK);
        Assert.Equal(a.TMetal, b.TMetal);

        var ex = Assert.Throws<ArgumentException>(() => SegmentSolver.Sweep(new DesignInputs(), "flangeInsul", 0, 20, 3));
        Assert.Contains("不起作用", ex.Message);
        Assert.Contains("整线核算", ex.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => SegmentSolver.Sweep(new DesignInputs(), "没有这个量", 0, 1, 2));
        Assert.DoesNotContain("flangeInsul", ex2.Message);   // 「可用」清单里不再列它
    }

    /// <summary>
    /// CoupledSolver（旧链，LineSolver 与 CLI 诊断用）里 PlateThermal2D 读的圆盘保温也按板件取：
    /// 板件 5 + 整线 20 与 板件空 + 整线 5 逐位相同；板件空 + 整线 20 对照不同。粗网格 h=4、外层 1 轮，只验接线不验精度。
    /// </summary>
    [Fact]
    public void 旧耦合链的二维法兰热解按板件圆盘保温()
    {
        CoupledResult Solve(double whole, double plate)
        {
            var p = new DesignInputs { FlangeInsulThickMm = whole, FlangeInsulated = true };
            var g = new FlangePlate { DiscInsulThickMm = plate };
            return CoupledSolver.Solve(p, g, h: 4.0, maxOuter: 1);
        }
        var a = Solve(20.0, 5.0); var b = Solve(5.0, double.NaN); var c = Solve(20.0, double.NaN);
        Assert.True(a.Tube.Ok && b.Tube.Ok && c.Tube.Ok);
        Assert.Equal(b.Flange.QFromTubeW, a.Flange.QFromTubeW);
        Assert.Equal(b.Flange.TMaxC, a.Flange.TMaxC);
        Assert.Equal(b.FlangeDrawW, a.FlangeDrawW);
        Assert.NotEqual(b.Flange.QFromTubeW, c.Flange.QFromTubeW);
    }

    /// <summary>
    /// 同型病灶门（源码）：Core 与 UI 里**读圆盘保温**、**吃圆盘保温的热解调用点**、**法兰表面热流配方**都钉成「每个文件几行」——
    /// 数变了（新增一处、或同一文件里又多写一处）这里就红：新读者得先想清楚逐片圆盘保温从哪取（LineCase.DiscInsulEffectiveAt 或 FlangePlate.DiscInsulEffectiveMm），
    /// 再改名单并写明为什么。另禁两种写法：`.Base.FlangeInsulThickMm`（从算例整线值直接读），和 ShellThermal／PlateThermal2D／RampTwoNode 的 Solve 直接吃 `c.Base`／`lc.Base`。
    /// 2026-09-14 Opus 5 改（审查意见「名单按整个文件放行、只扫 Core」）：原为文件名单，LineRunner、DesignCurrent 整文件放行，
    /// 在里面再写一处整线读法照样绿；现钉行数、加禁用写法、扫描扩到 UI。行为本身由两条慢门钉；这条让「又多了一处」在快套件里就看得见。
    /// </summary>
    [Fact]
    public void 源码门_Core里读圆盘保温的地方只有审过的几处()
    {
        string app = Path.Combine(HandoverDoc.Root(), "Pt_Optimize");
        // 含 FlangeInsulThickMm 的代码行数（整行注释不算）
        var readersExpected = new Dictionary<string, int>
        {
            ["Core/DesignInputs.cs"] = 1,     // 定义
            ["Core/DesignSpec.cs"] = 1,       // BuildCase 写整线值（WholeLineDiscInsulMm）
            ["Core/PlateCurrent2D.cs"] = 1,   // FlangePlate.DiscInsulEffectiveMm → 规则本体
            ["Core/LineRunner.cs"] = 3,       // DiscInsulEffectiveAt 图纸分支退整线；PlateThermalInputs 的 p2；升温两节点 pRamp
            ["Core/DesignCurrent.cs"] = 1,    // 两节点对照按片克隆
            ["Core/CoupledSolver.cs"] = 1,    // 旧链按板件克隆
            ["Core/ShellThermal.cs"] = 4,     // 热解本体：圆盘保温面判定与取值、局部热稳定两处（调用方负责给本片的值）
            ["Core/PlateThermal2D.cs"] = 2,   // 热解本体：判定与取值
            ["Core/RampTwoNode.cs"] = 4,      // 热解本体：判定与取值、保温热容判定与体积
            ["UI/AnalysisPage.cs"] = 1,       // 厚度灵敏度页写整线 20（没有逐片设计）
            ["UI/LineDesignPage.cs"] = 1,     // 「法兰保温／法兰保温厚」控件写整线值
        };
        // 调用点代码行数
        var callsExpected = new Dictionary<string, Dictionary<string, int>>
        {
            ["ShellThermal.Solve("] = new() { ["Core/LineRunner.cs"] = 1 },                              // LineRunner.SolvePlateThermal
            ["PlateThermal2D.Solve("] = new() { ["Core/CoupledSolver.cs"] = 1 },
            ["RampTwoNode.Solve("] = new() { ["Core/LineRunner.cs"] = 1, ["Core/DesignCurrent.cs"] = 1 },
            ["FlangeStability.Check("] = new() { ["Core/LineRunner.cs"] = 1 },
            ["DesignCurrent.Compute("] = new() { ["Core/LineRunner.cs"] = 1 },
            // 法兰表面热流只有 DesignScreen.PlateFluxWPerM2 一份配方（Insulation.cs 是定义处，不扫）
            ["PlateFlux("] = new() { ["Core/DesignScreen.cs"] = 1 },
            ["FlatOuterFlux("] = new() { ["Core/DesignScreen.cs"] = 1 },
        };
        var banned = new (Regex Re, string Why)[]
        {
            (new Regex(@"\.Base\.FlangeInsulThickMm"), "从算例的整线值直接读圆盘保温"),
            (new Regex(@"\b(ShellThermal|PlateThermal2D|RampTwoNode)\.Solve\([^;]*?\b\w+\.Base\b", RegexOptions.Singleline), "热解直接吃算例的整线 DesignInputs"),
        };

        var readersGot = new Dictionary<string, int>();
        var callsGot = callsExpected.Keys.ToDictionary(k => k, _ => new Dictionary<string, int>());
        var bad = new List<string>();
        int files = 0;
        foreach (var dir in new[] { "Core", "UI" })
            foreach (var path in Directory.GetFiles(Path.Combine(app, dir), "*.cs", SearchOption.AllDirectories))
            {
                files++;
                string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
                var code = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)).ToArray();
                int nRead = code.Count(l => l.Contains("FlangeInsulThickMm", StringComparison.Ordinal));
                if (nRead > 0) readersGot[rel] = nRead;
                if (rel != "Core/Insulation.cs")
                    foreach (var call in callsExpected.Keys)
                    {
                        int nCall = code.Count(l => l.Contains(call, StringComparison.Ordinal));
                        if (nCall > 0) callsGot[call][rel] = nCall;
                    }
                string joined = string.Join("\n", code);
                foreach (var (re, why) in banned)
                    foreach (Match m in re.Matches(joined))
                        bad.Add($"{rel}：{why}（{m.Value.Split('\n')[0]}）");
            }
        Assert.True(files > 60, $"只读到 {files} 个 Core／UI 源文件，路径不对");

        static string Show(Dictionary<string, int> d) => string.Join("、", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}×{kv.Value}"));
        static bool Same(Dictionary<string, int> a, Dictionary<string, int> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int v) && v == kv.Value);
        Assert.True(Same(readersExpected, readersGot),
            "读 FlangeInsulThickMm 的代码行变了（逐片圆盘保温要从唯一口径取，改名单要写明为什么）：\n  钉的 " + Show(readersExpected) + "\n  实测 " + Show(readersGot));
        foreach (var call in callsExpected.Keys)
            Assert.True(Same(callsExpected[call], callsGot[call]),
                $"「{call}」的调用点变了（吃圆盘保温的热解要按片给值；法兰表面热流只许一份配方）：\n  钉的 " + Show(callsExpected[call]) + "\n  实测 " + Show(callsGot[call]));
        Assert.True(bad.Count == 0, "禁用写法：\n" + string.Join("\n", bad));
        // 自证：禁用写法的正则真能咬住（否则正则写错了等于放行）
        Assert.Matches(banned[0].Re, "double x = c.Base.FlangeInsulThickMm;");
        Assert.Matches(banned[1].Re, "var th = ShellThermal.Solve(mesh, jm,\n c.Base, tRoot, insulX);");
        Assert.DoesNotMatch(banned[1].Re, "var st = FlangeStability.Check(c.Base, q, t);");
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5；审查意见）：DesignCurrent.Compute 的逐片圆盘保温取值改为**必填**（原默认 null 时退回板件口径，
    /// 图纸路径的判据几何板件不带逐片值 ⇒ 新调用方忘了传就静默按整线算）；并在快套件里钉住求解器用的 ForLine 这一口：
    /// 设计甲（整线 20、逐片 5）与设计乙（整线 5）的两节点对照电流逐位相同，对照丙（整线 20）不同。
    /// </summary>
    [Fact]
    public void 设计电流的两节点对照_逐片圆盘保温必填_ForLine按片取值()
    {
        var p = new DesignInputs();
        DesignSpec Make(double whole, double[] perPlate)
        {
            var d = DesignSpec.W08.Clone().Fit();
            d.FlangeInsulated = true; d.FlangeInsulMm = whole; d.DiscInsulMm = perPlate;
            return d;
        }
        var dA = Make(20.0, new[] { 5.0, 5.0, 5.0, 5.0 });
        var lc = dA.BuildCase(p, checkRamp: false);
        var ex = Assert.Throws<ArgumentNullException>(() => DesignCurrent.Compute(lc.FlangePlates, dA.WallMm, lc.Base, lc.RampFromC, lc.RampTargetC,
                                                                                 lc.RampRateKPerH, lc.SegmentCount, lc.SetpointC, _ => null, null!));
        Assert.Equal("discInsulMmAt", ex.ParamName);

        var a = DesignCurrent.ForLine(dA, p);
        var b = DesignCurrent.ForLine(Make(5.0, Array.Empty<double>()), p);
        var c = DesignCurrent.ForLine(Make(20.0, Array.Empty<double>()), p);
        _out.WriteLine($"两节点对照 A：甲 {string.Join("/", a.TwoNodeSegPeakA.Select(x => x.ToString("R")))}；乙 {string.Join("/", b.TwoNodeSegPeakA.Select(x => x.ToString("R")))}；丙 {string.Join("/", c.TwoNodeSegPeakA.Select(x => x.ToString("R")))}");
        Assert.Equal(b.TwoNodeSegPeakA, a.TwoNodeSegPeakA);
        Assert.Equal(b.SegPeakA, a.SegPeakA);
        Assert.NotEqual(b.TwoNodeSegPeakA, c.TwoNodeSegPeakA);
    }

    /// <summary>
    /// ★ R48（2026-09-14，Opus 5；审查意见「圆盘保温 0 mm 时四个消费方物理含义不一致」）：圆盘保温 0 mm（整线不包、或逐片 0 层 —— 用户 2026-09-14「每层 0.5 mm」，0 层合法）时，
    /// 壳热解 ShellThermal（含其中的局部热稳定）、旧链二维热解 PlateThermal2D（经 CoupledSolver）、升温两节点 RampTwoNode、整片热稳定 FlangeStability
    /// 取的都是**裸铂表面**：把外覆材料发射率 OuterEmissivity 从 0.45 改到 0.90，四者结果逐位不变（管子设成裸管，免得管侧用到它）。
    /// 对照：同样改动在 5 mm 圆盘保温下四者都变 —— 证明圆盘面确实进了这些结果，门不空转。
    /// 修前 ShellThermal／PlateThermal2D／RampTwoNode 的圆盘面在 0 mm 时走 PlateFlux 的「无有效层」分支 = 外覆材料表面，这里前三者会红。
    /// </summary>
    [Fact]
    public void 圆盘保温0mm时四个消费方都按裸铂表面()
    {
        DesignInputs P(double discInsulMm, double epsOuter)
        {
            var q = new DesignInputs { FlangeInsulThickMm = discInsulMm, FlangeInsulated = true, OuterEmissivity = epsOuter };
            q.Layer1.Enabled = false; q.Layer2.Enabled = false; q.Layer3.Enabled = false;   // 裸管：管侧散热不读 OuterEmissivity
            return q;
        }
        Assert.False(DesignScreen.FlangeFaceInsulated(0.0));
        Assert.False(DesignScreen.FlangeFaceInsulated(double.NaN));
        Assert.True(DesignScreen.FlangeFaceInsulated(0.5));   // 一层

        // ① ShellThermal：默认板、粗网格、均匀电流密度（只验表面口径，不验精度）；圆盘按半径包保温、舌片裸
        var g = new FlangePlate { HoleRadiusMm = 26.0 };
        // 2026-09-15 Opus 5：显式写明配方开关 —— 本文件写证据文件，受 R48ClampRecipeTests 门 g 管；这一处合入时漏写，门 g 在合入后的基线上就是红的。
        //   复审修（同日）：细带写数字 0，不写 double.NaN（NaN = 生产缺省，门 g 判红）；0 与当前生产缺省逐位相同，本门只比两种保温设置的相对差，口径不影响结论。
        var mesh = FlangeMesher.Build(g, 0, 4.0, 11.0, 50.0, 40.0, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        var jm = Enumerable.Repeat(1.0, mesh.CellCount).ToArray();
        ShellThermalResult Shell(DesignInputs q) => ShellThermal.Solve(mesh, jm, q, 1150.0, g.InsulBoundaryXResolved, false,
                                                                      tabBoundaryX: g.Tangent().X, tabInsulThickMm: double.NaN,
                                                                      discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
        // ② PlateThermal2D（经旧耦合链）
        CoupledResult Old(DesignInputs q) => CoupledSolver.Solve(q, new FlangePlate(), h: 4.0, maxOuter: 1);
        // ③ RampTwoNode：一片 8000 mm² 包保温面 + 1500 mm² 裸面。这组输入下法兰始终不比管热（法兰−管峰恒 0，比它等于空转），
        //    起作用的是法兰峰值温度与峰值电流 —— 注入对照（2026-09-14 Opus 5，只把两节点的圆盘面改回外覆表面）：0 mm 时法兰峰 728.6 / 584.7 °C，红在这一行。
        RampTwoNodeResult Ramp(DesignInputs q) => RampTwoNode.Solve(q, new RampTwoNode.Inputs
        {
            WallMm = 1.0, FlangeMassG = 300, FlangeAreaInsulMm2 = 8000, FlangeAreaBareMm2 = 1500,
            FlangeResistanceRefOhm = 2e-4, FlangeRefTempC = 1150, HoleRadiusMm = 26, PlateEqOuterRadiusMm = 60, FlangeThickMm = 1.5,
            DesignCurrentA = 900, MaxCurrentA = 3000, FromC = 25, TargetC = 1150, RampRateKPerH = 20, MaxHours = 1125.0 / 20 * 1.4,
            SharedFactor = 1.0, Mode = RampControl.TemperatureRamp,
        });
        // ④ FlangeStability：同样的面积分法
        FlangeStability.Result Stab(DesignInputs q) => FlangeStability.Check(q, 60.0, 1150.0, 8000, 1500, q.FlangeInsulThickMm,
                                                                             2 * 20 * 1.5, 50, 2 * Math.PI * 26 * 1.5, 30, double.NaN);

        foreach (double ins in new[] { 0.0, 5.0 })
        {
            var q1 = P(ins, 0.45); var q2 = P(ins, 0.90);
            var s1 = Shell(q1); var s2 = Shell(q2);
            var o1 = Old(q1); var o2 = Old(q2);
            var r1 = Ramp(q1); var r2 = Ramp(q2);
            var f1 = Stab(q1); var f2 = Stab(q2);
            _out.WriteLine($"圆盘保温 {ins} mm：壳解散热 {s1.QLossW:R} / {s2.QLossW:R} W；旧链抽热 {o1.Flange.QFromTubeW:R} / {o2.Flange.QFromTubeW:R} W；"
                         + $"两节点法兰峰 {r1.TFlangePeakC:R} / {r2.TFlangePeakC:R} °C、法兰−管峰 {r1.MaxFlangeMinusTubeK:R} / {r2.MaxFlangeMinusTubeK:R} K、峰值电流 {r1.PeakCurrentA:R} / {r2.PeakCurrentA:R} A、到温 {r1.TubeReached}；热稳定表面导数 {f1.DSurfDT:R} / {f2.DSurfDT:R} W/K（外覆 ε 0.45 / 0.90）");
            Assert.True(o1.Tube.Ok && o2.Tube.Ok);
            if (ins == 0.0)
            {
                Assert.Equal(s1.T, s2.T); Assert.Equal(s1.QLossW, s2.QLossW); Assert.Equal(s1.QFromTubeW, s2.QFromTubeW);
                Assert.Equal(s1.LocalStabMargin, s2.LocalStabMargin);
                Assert.Contains("按裸铂表面散热", s1.InsulRule);   // 保温规则说明写明（界面读得到）
                Assert.Equal(o1.Flange.QFromTubeW, o2.Flange.QFromTubeW); Assert.Equal(o1.Flange.TMaxC, o2.Flange.TMaxC);
                Assert.Equal(r1.MaxFlangeMinusTubeK, r2.MaxFlangeMinusTubeK); Assert.Equal(r1.TFlangePeakC, r2.TFlangePeakC);
                Assert.Equal(r1.PeakCurrentA, r2.PeakCurrentA);
                Assert.Equal(f1.DSurfDT, f2.DSurfDT);
            }
            else
            {
                Assert.NotEqual(s1.QLossW, s2.QLossW);
                Assert.DoesNotContain("按裸铂表面散热", s1.InsulRule);
                Assert.NotEqual(o1.Flange.QFromTubeW, o2.Flange.QFromTubeW);
                Assert.NotEqual(r1.TFlangePeakC, r2.TFlangePeakC);
                Assert.NotEqual(f1.DSurfDT, f2.DSurfDT);
            }
        }

        // 逐片 0 层进得了算例：DesignSpec 逐片 {0, 5, 5, 5} ⇒ 第 0 片实际取 0，按裸铂
        var d = DesignSpec.W08.Clone().Fit();
        d.FlangeInsulated = true; d.DiscInsulMm = new[] { 0.0, 5.0, 5.0, 5.0 };
        var lc = d.BuildCase(new DesignInputs());
        Assert.Equal(0.0, lc.DiscInsulEffectiveAt(0));
        Assert.False(DesignScreen.FlangeFaceInsulated(lc.DiscInsulEffectiveAt(0)));
        Assert.True(DesignScreen.FlangeFaceInsulated(lc.DiscInsulEffectiveAt(1)));
    }
}
