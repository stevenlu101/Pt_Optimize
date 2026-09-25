using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════
//  2026-09-25（业主 2026-09-25 12:5x 原话「两条腿并到同一根铜排，分叉点先给定舌长中点」；全局解决方案 §9.4 第一步、§9.6 第一条）
//  规则：侧 Y 形里「分叉点」= 舌根三角孔的铜排侧端点 = 两臂汇成一根杆的位置；钉在舌长中点 = 0.5·(切点 x ＋ 舌尖 x)（DesignSpec.TabMidXMm）。
//  孔心 x = 舌长中点 ＋ 孔朝铜排的半长（DesignSpec.TabHoleXForBusbarEndAt ⇒ TabHoleHalfLenMm ⇒ 几何件 FlangePlate.TabHole.ExtentXMm，支撑函数闭式，
//  与 Contains 同一份参数）；落 0.5 mm 图纸格（与 FieldPlacement 场定孔心同一格）。盘侧端点 = 孔心 ＋ 朝盘半长，仍由孔径与拉长比决定；朝向照 R31 的 90° 规则。
//  谁写：Solver.PinTabHoleForkAtMid —— Solve 起点（NormalizeHoleRadii 之后、第一次 SizeTongue 之前）、SetKnob 每动孔径／拉长比、FieldPlacement 每轮开头，同一个函数。
//  改回参数：DesignInputs.TabHoleBusbarEndAtTabMid（生产不设 = true；false = 场定孔心，逐位同改前）。
//
//  门（都快）：
//   a  合成 W08（R48NMeshGateTests.Design("W08")，孔径 8、拉长比 1.5、形状族 0／3／4）调 PinTabHoleForkAtMid：铜排侧端点离舌长中点 ≤ 0.5（图纸格；
//      实际只差落格的 ≤ 0.25）；两端点与几何真值一致 —— 在孔的包围盒上以 0.02 mm 网格采样 Contains，孔内点的 x 最小／最大值与「孔心 ∓ 半长」之差 ≤ 0.02·√2；
//      朝向由 R31 规则给（这三例都够不到舌根 ⇒ 形状族默认）。
//   a2 09-12 样机几何（deliverable/拍脑袋Y形_解出/核算与出图.txt：盘 Ø56、舌长 146、舌端半宽 15、锥形、三角孔 28、拉长比 0.36）：朝向 90°（够到舌根），
//      朝铜排半长 = 外接半径（圆头顶点），朝盘半长 = 0.675·外接半径（底边 + 圆角，数学推出：rc/2 + r，r = 0.35·R），铜排侧端 = 舌长中点 ± 0.25，盘侧端越过切点（宽端开到盘）。
//   a3 同一个场（Builtin[0]，LineRunner.Run 一次）跑 FieldPlacement：开着时无孔的片孔心不写（NaN）、有孔的片钉舌长中点；与 FieldPlacementTests 两门（改回口径）互为对照。
//   b  改回逐位：开关关时 PinTabHoleForkAtMid 返回 false、TabHoleXMm 逐位不变（NaN 与非 NaN 各一）；SetKnob 动孔径／拉长比后 TabHoleXMm 逐位不变；
//      FieldPlacement（无场）不写；开关开时同一调用真的写了（非空转）。
//   c  源码门：Solver.FieldPlacement 里写 TabHoleXMm 的语句只有一条，且在 `if (pin)` 之外的 else 支；PinTabHoleForkAtMid 第一句就是开关；SetKnob 与 Solve 起点都调它。
//   d  冒烟：W08 带三角孔起点、MaxRounds 1、粗筛 8 mm 走 Solver.Solve，轨迹里出现 BranchMarks.TabHoleForkAtMid 那一句，孔心终值 = 钉点（拉长比若被动过也照钉）。
//
//  覆盖：钉点公式与几何真值一致（三族）、样机几何的闭式值、FieldPlacement 开／关两支（真场）、改回逐位（函数层与 SetKnob 层）、源码接线、Solve 一轮的留痕。
//  不覆盖：钉点落进压接段的算例（只印警告，无门）；朝向规则与钉点「两候选都不自洽」的分支（没找到能触发的几何，只印）；出图（Rhino，Windows）；
//          搜形状驱动整跑；Windows 记录（本档只在 Linux 镜像跑过）。
// ════════════════════════════════════════════════════════════════════════
public class R48TabHoleForkAtMidTests
{
    private readonly ITestOutputHelper _o;
    public R48TabHoleForkAtMidTests(ITestOutputHelper o) { _o = o; }

    static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    static string Head(string title)
    {
        string load = "未读到";
        try { if (File.Exists("/proc/loadavg")) load = File.ReadAllText("/proc/loadavg").Trim(); } catch { }
        return $"{title}\n开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {System.Runtime.InteropServices.RuntimeInformation.OSDescription}（{(OperatingSystem.IsWindows() ? "Windows 记录" : "Linux 预跑，待 Windows 重录")}）"
             + $"　工作树 {HandoverDoc.Root()}（基于 8846d71，工单 甲 分叉点钉舌长中点，未提交）　同机争用：另有搜形状整夜实跑占 3 核，loadavg {load}，耗时只作量级\n";
    }

    /// <summary>W08 合成设计 + 第 j 片开孔（孔径 8、拉长比 1.5、形状族 sides）。</summary>
    static DesignSpec W08With(int j, int sides, double r = 8.0, double asp = 1.5)
    {
        var d = R48NMeshGateTests.Design("W08");
        d.TabHoleRMm[j] = r; d.TabHoleAspect[j] = asp; d.TabHoleSides[j] = sides;
        for (int k = 0; k < d.TabHoleXMm.Length; k++) d.TabHoleXMm[k] = double.NaN;   // 与 Solve 起点同：孔心由规则给
        return d;
    }

    /// <summary>几何真值：在孔的包围盒上采样 Contains，孔内点 x 的最小／最大。</summary>
    static (double XMin, double XMax, int Inside) ScanHole(FlangePlate.TabHole h, double step)
    {
        double e = h.RMm * Math.Max(1.0, h.AspectXZ) + 1.0;
        double xmin = double.PositiveInfinity, xmax = double.NegativeInfinity; int n = 0;
        for (double x = h.XMm - e; x <= h.XMm + e; x += step)
            for (double z = h.ZMm - e; z <= h.ZMm + e; z += step)
                if (h.Contains(x, z)) { n++; if (x < xmin) xmin = x; if (x > xmax) xmax = x; }
        return (xmin, xmax, n);
    }

    // ─────────────────────────────── a ───────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void 门a_合成W08_铜排侧端点钉舌长中点_两端点与Contains一致(int sides)
    {
        const int j = 1;
        var d = W08With(j, sides);
        var p = new DesignInputs();
        Assert.True(p.TabHoleBusbarEndAtTabMid, "生产缺省该是开");
        var lines = new List<string>();
        bool changed = Solver.PinTabHoleForkAtMid(d, p, j, lines.Add);
        Assert.True(changed, "有孔且孔心 NaN ⇒ 必须写");
        Assert.Contains(lines, l => l.StartsWith(BranchMarks.TabHoleForkAtMid, StringComparison.Ordinal));

        double xc = d.TabHoleXMm[j];
        Assert.False(double.IsNaN(xc));
        Assert.Equal(xc, Math.Round(xc * 2) / 2, 12);                     // 落 0.5 mm 格
        double rot = d.TabHoleRotDegFor(j);
        var (toBus, toDisc) = d.TabHoleHalfLenMm(j, rot);
        double xMid = d.TabMidXMm(), xTan = d.TangentXMm(), xTip = -d.TabLengthMm;
        Assert.Equal(0.5 * (xTan + xTip), xMid, 12);
        double xBus = xc - toBus, xDisc = xc + toDisc;
        Assert.True(Math.Abs(xBus - xMid) <= 0.5 + 1e-12, $"铜排侧端 {R(xBus)} 离舌长中点 {R(xMid)} 超过图纸格 0.5");
        Assert.True(Math.Abs(xBus - xMid) <= 0.25 + 1e-12, $"落 0.5 格后铜排侧端离舌长中点只该 ≤ 0.25：{R(xBus - xMid)}");

        // 几何真值：HolesOf 造出来的孔（同一份孔心／朝向／拉长比）采样 Contains
        var holes = d.HolesOf(j);
        Assert.Single(holes);
        var h = holes[0];
        Assert.Equal(xc, h.XMm, 12); Assert.Equal(rot, h.RotDeg, 12);
        const double step = 0.02;
        var (xmin, xmax, n) = ScanHole(h, step);
        Assert.True(n > 1000, $"采样到孔内点只有 {n} 个");
        double tol = step * Math.Sqrt(2) + 1e-9;
        Assert.True(Math.Abs(xmin - xBus) <= tol, $"Contains 采样的最小 x {R(xmin)} 与 孔心 − 朝铜排半长 {R(xBus)} 差 {R(xmin - xBus)} > {R(tol)}");
        Assert.True(Math.Abs(xmax - xDisc) <= tol, $"Contains 采样的最大 x {R(xmax)} 与 孔心 ＋ 朝盘半长 {R(xDisc)} 差 {R(xmax - xDisc)} > {R(tol)}");
        // 采样点全部落在 [xBus, xDisc] 内（半长不许比真值小）
        Assert.True(xmin >= xBus - 1e-9 && xmax <= xDisc + 1e-9, "孔内采样点越出了「孔心 ∓ 半长」");

        double xClamp = -d.TabLengthMm + d.ClampLengthMm;
        var sb = new StringBuilder();
        sb.Append(Head($"R48 分叉点钉舌长中点 门 a　合成 W08（{R48LW08NavDesign.Source}）片{j} 孔径 8、拉长比 1.5、形状族 {sides}"));
        sb.AppendLine($"切点 x {R(xTan)}　舌尖 x {R(xTip)}　舌长中点 {R(xMid)}　压接段边界 {R(xClamp)}");
        sb.AppendLine($"孔心 x {R(xc)}（落 0.5 格）　朝向 {R(rot)}°　外接半径 {R(h.RMm)}　朝铜排半长 {R(toBus)}　朝盘半长 {R(toDisc)}");
        sb.AppendLine($"铜排侧端 {R(xBus)}（离舌长中点 {R(xBus - xMid)}）　盘侧端 {R(xDisc)}（切点 {R(xTan)}）");
        sb.AppendLine($"Contains 采样（步 {step}）：x ∈ [{R(xmin)}, {R(xmax)}]，孔内点 {n}；与闭式端点之差 {R(xmin - xBus)}／{R(xmax - xDisc)}，容差 {R(tol)}");
        sb.AppendLine("轨迹：" + string.Join("\n", lines));
        sb.AppendLine("覆盖：钉点公式与 Contains 采样一致（本形状族）；不覆盖：出图、真场。");
        string file = DeliverableOut.Stamped($"R48_分叉点钉舌长中点_门a_形状族{sides}_2026-09-25.txt");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
    }

    // ─────────────────────────────── a2 ───────────────────────────────
    [Fact]
    public void 门a2_样机几何_三角孔转90度_朝铜排半长等于外接半径_朝盘0p675倍_铜排侧端钉舌长中点()
    {
        // deliverable/拍脑袋Y形_解出/核算与出图.txt 与同目录 spec.json 的几何（全局解决方案 §9.2 末段）
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 28; d.TabLengthMm = 146; d.TabHalfWidthMm = 15; d.TabTaper = true; d.ClampLengthMm = 40;
        d.TabHoleRMm[0] = 28; d.TabHoleAspect[0] = 0.36; d.TabHoleSides[0] = 3;
        for (int k = 0; k < d.TabHoleXMm.Length; k++) d.TabHoleXMm[k] = double.NaN;
        var p = new DesignInputs();
        var lines = new List<string>();
        Assert.True(Solver.PinTabHoleForkAtMid(d, p, 0, lines.Add));
        double rr = FlangePlate.TabHole.EqualAreaRadius(28, 3, DesignSpec.TabHoleCornerFracOf(3));
        Assert.InRange(rr, 34.11, 34.13);                                   // 样机档记外接半径 34.12
        Assert.Equal(90.0, d.TabHoleRotDegFor(0), 9);                       // 够到舌根 ⇒ 90°（R31，没动）
        var (toBus, toDisc) = d.TabHoleHalfLenMm(0, 90.0);
        Assert.Equal(rr, toBus, 9);                                         // 圆头顶点：rc + r = R
        Assert.Equal(rr * (0.5 + 0.5 * 0.35), toDisc, 9);                   // 底边 + 圆角：rc/2 + r
        double xMid = d.TabMidXMm(), xBus = d.TabHoleXMm[0] - toBus;
        Assert.True(Math.Abs(xBus - xMid) <= 0.25 + 1e-12, $"铜排侧端 {R(xBus)} vs 舌长中点 {R(xMid)}");
        Assert.True(d.TabHoleXMm[0] + toDisc > d.TangentXMm(), "样机这一孔的宽端该越过切点（开到盘）");
        Assert.InRange(toBus + toDisc, 57.0, 57.3);                          // 样机档记槽实际范围长 57
        _o.WriteLine(string.Join("\n", lines));
        _o.WriteLine($"切点 {R(d.TangentXMm())} 舌长中点 {R(xMid)} 孔心 {R(d.TabHoleXMm[0])} 两半长 {R(toBus)}/{R(toDisc)} 孔长 {R(toBus + toDisc)}");
    }

    // ─────────────────────────────── a3 ───────────────────────────────
    [Fact]
    public void 门a3_真场FieldPlacement_开着时无孔不写_有孔钉舌长中点()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleRMm = new[] { 0.0, 5.0, 5.0, 0.0 };
        for (int k = 0; k < d.TabHoleXMm.Length; k++) d.TabHoleXMm[k] = double.NaN;
        var baseIn = new DesignInputs();
        Assert.True(baseIn.TabHoleBusbarEndAtTabMid);
        var lc = d.BuildCase(baseIn, checkRamp: false);
        var last = LineRunner.Run(lc, null, default);
        Assert.True(last.Ok, "本门要一个收敛的场做基准");
        var lines = new List<string>();
        Solver.FieldPlacement(d, baseIn, last, lines.Add);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            if (!(d.TabHoleREffective(j) > 0)) { Assert.True(double.IsNaN(d.TabHoleXMm[j]), $"片{j} 无孔，开着时孔心不该被场写"); continue; }
            double rot = d.TabHoleRotDegFor(j);
            double xBus = d.TabHoleXMm[j] - d.TabHoleHalfLenMm(j, rot).ToBusbar;
            Assert.True(Math.Abs(xBus - d.TabMidXMm()) <= 0.25 + 1e-12, $"片{j} 铜排侧端 {R(xBus)} vs 舌长中点 {R(d.TabMidXMm())}");
        }
        Assert.Contains(lines, l => l.Contains(BranchMarks.TabHoleForkAtMid, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("只印不用", StringComparison.Ordinal));
        _o.WriteLine(string.Join("\n", lines));
    }

    // ─────────────────────────────── b ───────────────────────────────
    [Fact]
    public void 门b_改回_孔心逐位不变_开着时同一调用真的写()
    {
        const int j = 1;
        var off = new DesignInputs { TabHoleBusbarEndAtTabMid = false };
        var on = new DesignInputs();

        // 函数层：NaN 与非 NaN 各一，逐位不变
        var d = W08With(j, 3);
        Assert.False(Solver.PinTabHoleForkAtMid(d, off, j, null));
        Assert.True(Same(double.NaN, d.TabHoleXMm[j]));
        d.TabHoleXMm[j] = -66.5;
        Assert.False(Solver.PinTabHoleForkAtMid(d, off, j, null));
        Assert.True(Same(-66.5, d.TabHoleXMm[j]));
        Assert.False(Solver.FieldPlacement(d, off, null, null));
        Assert.True(Same(-66.5, d.TabHoleXMm[j]));

        // SetKnob 层：动孔径／拉长比，改回时 TabHoleXMm 逐位不变（连同别的片）
        var d2 = R48NMeshGateTests.Design("W08");
        var o = new SolverOptions(); var res = new SolverResult();
        Solver.ApplySectionFloor(d2, off, o, res, null, null);
        Assert.NotNull(res.DesignCurrent);
        long[] before = d2.TabHoleXMm.Select(BitConverter.DoubleToInt64Bits).ToArray();
        Solver.SetKnob(d2, Solver.Knob.TabHoleR, j, 8.0, off, res);
        Solver.SetKnob(d2, Solver.Knob.TabHoleAspect, j, 1.5, off, res);
        Assert.Equal(before, d2.TabHoleXMm.Select(BitConverter.DoubleToInt64Bits).ToArray());

        // 非空转：同一调用开着时写了
        var d3 = R48NMeshGateTests.Design("W08");
        var res3 = new SolverResult();
        Solver.ApplySectionFloor(d3, on, o, res3, null, null);
        double x0 = d3.TabHoleXMm[j];
        Solver.SetKnob(d3, Solver.Knob.TabHoleR, j, 8.0, on, res3);
        Assert.False(Same(x0, d3.TabHoleXMm[j]), "开着时开孔必须重钉孔心");
        double x1 = d3.TabHoleXMm[j];
        Solver.SetKnob(d3, Solver.Knob.TabHoleAspect, j, 1.5, on, res3);
        // 拉长比一动半长可能变 ⇒ 孔心可能变；不管变不变，铜排侧端仍在舌长中点
        double xBus = d3.TabHoleXMm[j] - d3.TabHoleHalfLenMm(j, d3.TabHoleRotDegFor(j)).ToBusbar;
        Assert.True(Math.Abs(xBus - d3.TabMidXMm()) <= 0.25 + 1e-12);
        Assert.True(d3.HasTabArm(j), "开孔后 SizeTongue 该给叉臂带（钉孔心在 SizeTongue 之前）");
        _o.WriteLine($"开着：孔径 8 ⇒ 孔心 {R(x1)}；拉长比 1.5 ⇒ 孔心 {R(d3.TabHoleXMm[j])}；叉臂 [{R(d3.TabArmX0Mm[j])}, {R(d3.TabArmX1Mm[j])}] 厚 {R(d3.TabArmThickMm[j])}");
    }

    // ─────────────────────────────── c ───────────────────────────────
    static string SolverSource() => File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));

    static string MethodBody(string src, string signatureStart)
    {
        int i = src.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(i >= 0, "找不到 " + signatureStart);
        int end = src.IndexOf("\n    }\n", i, StringComparison.Ordinal);
        Assert.True(end > i);
        return src.Substring(i, end - i);
    }

    [Fact]
    public void 门c_源码门_FieldPlacement开着的支不写孔心_SetKnob与Solve起点都走同一个函数()
    {
        string src = SolverSource();
        string fp = MethodBody(src, "public static bool FieldPlacement(DesignSpec d, DesignInputs baseIn, LineResult? last, Action<string>? log)");
        var writes = fp.Split('\n').Where(l => Regex.IsMatch(l, @"d\.TabHoleXMm\[j\]\s*=")).ToArray();
        Assert.Single(writes);
        Assert.Matches(@"^\s*else if \(!hasHole", writes[0]);
        // 那条 else 的 if 支就是开关支
        int wi = fp.IndexOf(writes[0], StringComparison.Ordinal);
        string prevLine = fp.Substring(0, wi).TrimEnd('\n', '\r').Split('\n').Last();
        Assert.Contains("if (pin)", prevLine);
        Assert.Contains("PinTabHoleForkAtMid(d, baseIn, j, log)", prevLine);
        Assert.Contains("bool pin = baseIn.TabHoleBusbarEndAtTabMid;", fp);

        string pin = MethodBody(src, "public static bool PinTabHoleForkAtMid(DesignSpec d, DesignInputs baseIn, int j, Action<string>? log)");
        string firstStmt = pin.Split('\n').Skip(1).Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith("{"));
        Assert.Equal("if (!baseIn.TabHoleBusbarEndAtTabMid) return false;", firstStmt);
        Assert.Contains("d.TabHoleXForBusbarEndAt(j, xMid)", pin);
        Assert.Contains("d.TabMidXMm()", pin);

        string setKnob = MethodBody(src, "public static void SetKnob(DesignSpec d, Knob k, int j, double v, DesignInputs? baseIn, SolverResult? res)");
        Assert.Contains("PinTabHoleForkAtMid(d, baseIn, j, null)", setKnob);
        Assert.True(setKnob.IndexOf("PinTabHoleForkAtMid", StringComparison.Ordinal) < setKnob.IndexOf("SizeTongue", StringComparison.Ordinal), "钉孔心要在 SizeTongue 之前");

        int solveAt = src.IndexOf("public static SolverResult Solve(DesignSpec geometry", StringComparison.Ordinal);
        int normAt = src.IndexOf("NormalizeHoleRadii(d, Log);", solveAt, StringComparison.Ordinal);
        int pinAt = src.IndexOf("PinTabHoleForkAtMid(d, baseIn, j, Log);", solveAt, StringComparison.Ordinal);
        int floorAt = src.IndexOf("ApplySectionFloor(d, baseIn, opt, res, null, Log);", solveAt, StringComparison.Ordinal);
        Assert.True(normAt > 0 && pinAt > normAt && floorAt > pinAt, "Solve 起点：NormalizeHoleRadii → 钉孔心 → ApplySectionFloor 的顺序");
    }

    // ─────────────────────────────── d ───────────────────────────────
    [Fact]
    public void 门d_冒烟_Solve一轮_轨迹出现钉点句_孔心终值等于钉点()
    {
        const int j = 1;
        var d = W08With(j, 3, r: 8.0, asp: 1.0);
        var p = new DesignInputs();
        var o = new SolverOptions { MaxRounds = 1, ScreenCoarseMm = 8, AllowTabCuts = true };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sr = Solver.Solve(d, p, o, null);
        sw.Stop();
        var hits = sr.Trace.Where(l => l.Contains(BranchMarks.TabHoleForkAtMid, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(hits);
        Assert.Contains(hits, l => l.Contains($"片{j} 孔心 x =", StringComparison.Ordinal));
        var sb = new StringBuilder();
        sb.Append(Head("R48 分叉点钉舌长中点 门 d　冒烟：W08 片1 三角孔 8 起点，MaxRounds 1、粗筛 8 mm、AllowTabCuts"));
        sb.AppendLine($"耗时 {sw.Elapsed.TotalSeconds:0.0} s（争用下）　停机 {sr.StopWhy}　轨迹行 {sr.Trace.Count}");
        sb.AppendLine("钉点句：\n" + string.Join("\n", hits));
        var dd = sr.Design ?? d;
        for (int k = 0; k < dd.FlangeCount; k++)
        {
            if (!(dd.TabHoleREffective(k) > 0)) continue;
            double rot = dd.TabHoleRotDegFor(k);
            double xBus = dd.TabHoleXMm[k] - dd.TabHoleHalfLenMm(k, rot).ToBusbar;
            sb.AppendLine($"片{k} 终值：孔径 {R(dd.TabHoleREffective(k))} 拉长比 {R(dd.TabHoleAspect[k])} 孔心 {R(dd.TabHoleXMm[k])} 铜排侧端 {R(xBus)} 舌长中点 {R(dd.TabMidXMm())}");
            Assert.True(Math.Abs(xBus - dd.TabMidXMm()) <= 0.25 + 1e-12, $"片{k} 终值铜排侧端 {R(xBus)} 离舌长中点 {R(dd.TabMidXMm())}");
        }
        sb.AppendLine("覆盖：Solve 起点钉点留痕、终值孔心 = 钉点；不覆盖：解的好坏（MaxRounds 1、粗筛网格，不是判决）。");
        string file = DeliverableOut.Stamped("R48_分叉点钉舌长中点_门d_冒烟_2026-09-25.txt");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
    }
}
