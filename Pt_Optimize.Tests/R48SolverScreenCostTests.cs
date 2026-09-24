using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 搜形状算力工程（2026-09-24，全局解决方案第 2 版 §5）求解器侧两项的门：
///   A 同状态整线解复用（<see cref="SolverOptions.ReuseSameStateSolves"/>，缺省关）；
///   B 粗筛终局复核后不放大重做（<see cref="SolverOptions.SkipRadiusGrowthAfterFinalCheck"/>，缺省关）。
///
/// 快门（覆盖）：
///   B 源码门：跳过只截「终局复核后放大」那一支（Grew），拒答与峰位算不出两支不动；跳过块里没有重做、没有改计划与半径；
///     去掉新插入的那一块，放大段与改前（cc49836）逐字相同（SHA-256）⇒ 开关关时执行的语句与改前相同。
///   A 源码门：缓存只在开关开时碰；去掉新插入的语句，EvalRaw 与改前（cc49836）逐字相同。
///   A 指纹门：同一状态两次造算例指纹相同；板厚、舌保温、环倍率、盘半径、细区半径任一变 ⇒ 指纹不同；对象图里有委托 ⇒ 不取指纹。
///   A 缓存门：LRU 容量、命中计数。
/// 慢门（只点名跑）：A 开与关在同一设计上两跑（生产导航网格、1 轮），旋钮终值、判据表、轨迹、停因逐位相同，只有场解次数不同；并印「每轮每一步各解几次」归因。
/// 不覆盖：B 的真场行为（终局复核后真的没盖住热点那一支要整形状粗筛跑完才触发，夜跑 021617 第 3 形状用了 6 h；这里只有源码门与驱动侧注入门）；
///   A 在判决网格（FineMm &gt; 0，第二遍与回收）上的逐位（慢门只跑导航遍）。
/// </summary>
public class R48SolverScreenCostTests
{
    private readonly ITestOutputHelper _o;
    public R48SolverScreenCostTests(ITestOutputHelper o) => _o = o;

    private static string SolverSrc() => File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs")).Replace("\r\n", "\n");
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>改前（cc49836）Solver.cs 里终局复核放大段（从 while (true) 到 return res; 之前）的 SHA-256。重算：git show cc49836:Pt_Optimize/Core/Solver.cs，按本门同一规则截段。</summary>
    private const string GrowLoopShaBefore = "2e63acc8dbb5da5e881af96d8ed639cbbdaaf18c6a8509e6cd840bd20630da15";
    /// <summary>改前（cc49836）Solver.cs 里 EvalRaw 整个方法（到下一个 /// &lt;summary&gt; 之前）的 SHA-256。</summary>
    private const string EvalRawShaBefore = "ec1f08533513c24bcedf3e8bd42454969961abb882d1f2bd7e10cba3c3545b8d";

    private static string GrowLoop(string s)
    {
        int a = s.IndexOf("        while (true)\n        {\n            Finish(res, d, last, baseIn, lastOpt, cancel, progress);", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到终局复核放大段：门失去守护对象");
        int b = s.IndexOf("        return res;\n    }", a, StringComparison.Ordinal);
        return s[a..b];
    }

    private static string EvalRawBody(string s)
    {
        int a = s.IndexOf("    private static LineResult? EvalRaw(", StringComparison.Ordinal);
        Assert.True(a > 0, "找不到 EvalRaw：门失去守护对象");
        int b = s.IndexOf("    /// <summary>", a, StringComparison.Ordinal);
        return s[a..b];
    }

    [Fact]
    public void B_源码_跳过只截终局复核后放大那一支_关时与改前逐字相同()
    {
        string loop = GrowLoop(SolverSrc());
        const string cond = "if (g.Grew && opt.SkipRadiusGrowthAfterFinalCheck)";
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(loop, System.Text.RegularExpressions.Regex.Escape(cond)));
        int iGrow = loop.IndexOf("var g = MeshAdapt.GrowFineRadius(plan, pk, InnerROf(), lcF.MeshCoarseMm", StringComparison.Ordinal);
        int iCond = loop.IndexOf(cond, StringComparison.Ordinal);
        int iApply = loop.IndexOf("plan = g.Plan; res.RadiusPlan = plan;", StringComparison.Ordinal);
        Assert.True(iGrow > 0 && iCond > iGrow && iApply > iCond, "跳过判断必须在算出放大量之后、把放大写进计划与选项之前");
        int blockStart = loop.LastIndexOf("                // ★ 2026-09-24（搜形状算力工程 B", iCond, StringComparison.Ordinal);
        int blockEnd = loop.IndexOf("                    break;\n                }\n", iCond, StringComparison.Ordinal);
        Assert.True(blockStart > iGrow && blockEnd > iCond && blockEnd < iApply);
        blockEnd += "                    break;\n                }\n".Length;
        string block = loop[blockStart..blockEnd];
        foreach (string no in new[] { "FinePass(", "NavPass(", "Finish(", "plan =", "opt.RadiusPlan", "FineRadiusMm =", "Feasible", "Undetermined =", "continue;" })
            Assert.DoesNotContain(no, block);
        Assert.Contains("res.SkippedRadiusGrowthToMm = g.Plan.RadiusMm;", block);
        // 去掉新插的那一块 ⇒ 与改前逐字相同
        string before = loop[..blockStart] + loop[blockEnd..];
        Assert.Equal(GrowLoopShaBefore, Sha(before));
        Assert.False(new SolverOptions().SkipRadiusGrowthAfterFinalCheck);
    }

    [Fact]
    public void A_源码_同状态复用只在开关开时碰缓存_关时与改前逐字相同()
    {
        string body = EvalRawBody(SolverSrc());
        Assert.Contains("string? reuseKey = o.ReuseSameStateSolves ? SameStateReuse.KeyOf(lc) : null;", body);
        Assert.Contains("if (reuseKey is not null && res.SameState is { } reuse && reuse.TryGet(reuseKey, out var again))", body);
        Assert.Contains("if (reuseKey is not null) (res.SameState ??= new SameStateReuse()).Put(reuseKey, r);", body);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, @"res\.SameState\b").Count);
        // 去掉新插的语句 ⇒ 与改前逐字相同
        int a = body.IndexOf("            // ★ 2026-09-24（搜形状算力工程 A", StringComparison.Ordinal);
        const string probeOff = "            o.SolveProbe?.Invoke(lc, false);\n";
        int b = body.IndexOf(probeOff, a, StringComparison.Ordinal) + probeOff.Length;
        Assert.True(a > 0 && b > a);
        string before = body[..a] + body[b..];
        before = before.Replace("            if (reuseKey is not null) (res.SameState ??= new SameStateReuse()).Put(reuseKey, r);\n", "");
        Assert.Equal(EvalRawShaBefore, Sha(before));
        Assert.False(new SolverOptions().ReuseSameStateSolves);
        Assert.Null(new SolverOptions().SolveProbe);
    }

    private static LineCase NavCase(DesignSpec d, DesignInputs p, double fineRadiusMm)
    {
        var lc = d.BuildCase(p, checkRamp: false);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = 0, FineRadiusMm = fineRadiusMm });
        return lc;
    }

    [Fact]
    public void A_指纹_同一状态相同_任一输入一变就不同_有委托不取()
    {
        var p = new DesignInputs();
        var d = R48NMeshGateTests.Design("W08");
        var sw = Stopwatch.StartNew();
        string? k0 = SameStateReuse.KeyOf(NavCase(d, p, 59));
        double ms = sw.Elapsed.TotalMilliseconds;
        string? k1 = SameStateReuse.KeyOf(NavCase(d.Clone(), p, 59));
        _o.WriteLine($"指纹 {k0}；取一次 {ms:0.0} ms（含首次反射）");
        Assert.NotNull(k0);
        Assert.Equal(k0, k1);

        var changed = new List<(string What, DesignSpec D, double R)>();
        DesignSpec C(Action<DesignSpec> f) { var x = d.Clone(); f(x); return x; }
        changed.Add(("片1 板厚 +0.01", C(x => x.TabThickMm[1] += 0.01), 59));
        changed.Add(("片3 舌保温 +0.5", C(x => x.TabInsulMm[3] += 0.5), 59));
        changed.Add(("片0 环倍率 1.5", C(x => x.RingMul[0] = 1.5), 59));
        changed.Add(("盘半径 +0.5", C(x => x.DiscRadiusMm += 0.5), 59));
        changed.Add(("细区半径 60", d.Clone(), 60));
        foreach (var (what, dd, r) in changed)
        {
            string? k = SameStateReuse.KeyOf(NavCase(dd, p, r));
            _o.WriteLine($"{what}：{k}");
            Assert.NotNull(k);
            Assert.NotEqual(k0, k);
        }
        // 最小的改动也要分得出：板厚差 1 个最低位
        var tiny = d.Clone(); tiny.TabThickMm[2] = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(tiny.TabThickMm[2]) + 1);
        Assert.NotEqual(k0, SameStateReuse.KeyOf(NavCase(tiny, p, 59)));
        // 对象图里有委托 ⇒ 不取指纹（照常解场）
        var lcT = NavCase(d, p, 59);
        lcT.CoupleTrace = (_, _, _, _, _, _, _, _) => { };
        Assert.Null(SameStateReuse.KeyOf(lcT));
    }

    [Fact]
    public void A_缓存_LRU容量与命中计数()
    {
        var c = new SameStateReuse();
        var r0 = new LineResult();
        c.Put("k0", r0);
        for (int i = 1; i <= SameStateReuse.Capacity; i++) c.Put("k" + i, new LineResult());
        Assert.False(c.TryGet("k0", out _));                   // 最久没用的被挤掉
        Assert.True(c.TryGet("k1", out _));
        Assert.True(c.TryGet("k" + SameStateReuse.Capacity, out var last));
        Assert.NotNull(last);
        Assert.Equal(2, c.Hits);
        Assert.Equal(SameStateReuse.Capacity + 1, c.Stored);
    }

    // ═══════════════════════════════════════ 慢门：开与关两跑逐位相同 ＋ 每步场解归因
    /// <summary>与驱动 Spec 同一条舌长规则（√(R²−w²) + 压接 + 自由段下界）。</summary>
    internal static DesignSpec Shape(double R, double hw)
    {
        var s = R48NMeshGateTests.Design("W08").Clone();
        s.DiscRadiusMm = R; s.TabHalfWidthMm = hw; s.TabTaper = false;
        s.TabLengthMm = Math.Sqrt(Math.Max(0, R * R - hw * hw)) + s.ClampLengthMm + GeometryScreen.FreeTabMinDefaultMm;
        return s;
    }

    /// <summary>场解调用处 → 归因标签：在 Solver.cs 里按语句原文找行号（门不手抄行号；源码改了行号跟着变）。</summary>
    internal static Dictionary<int, string> SiteLines()
    {
        var lines = File.ReadAllLines(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        var map = new Dictionary<int, string>();
        int chooseAt = Array.FindIndex(lines, l => l.Contains("private static (Knob? Knob, string Why, double Before, double After, int Shape0, double Need) ChooseKnob(", StringComparison.Ordinal));
        int raiseAt = Array.FindIndex(lines, l => l.Contains("private static (bool Ok, string Why, bool Kept, bool Undetermined) RaiseUntil(", StringComparison.Ordinal));
        int famAt = Array.FindIndex(lines, l => l.Contains("private static (LineResult? R, double HiProbe) ProbeShapeFamily(", StringComparison.Ordinal));
        int roundsAt = Array.FindIndex(lines, l => l.Contains("bool Rounds(SolverOptions o, string tag)", StringComparison.Ordinal));
        int meltAt = Array.FindIndex(lines, l => l.Contains("public static (bool Ok, bool HandOff, string Why, LineResult? Last) MeltFloor(", StringComparison.Ordinal));
        void Add(int from, string text, string label, int nth = 1)
        {
            int seen = 0;
            for (int i = Math.Max(0, from); i < lines.Length; i++)
                if (lines[i].Contains(text, StringComparison.Ordinal) && ++seen == nth) { map[i + 1] = label; return; }
            map[-map.Count - 1] = "未查到：" + label;
        }
        Add(meltAt, "var r = first ?? EvalRaw(d, baseIn, o, res, cancel, inner);", "每遍开头 验下角（兼第 1 轮基准场）");
        Add(roundsAt, "else last = Eval(d, baseIn, o, res, cancel, inner);", "每轮基准场（第 2 轮起）");
        Add(roundsAt, "last = Eval(d, baseIn, o, res, cancel, inner);", "场定孔位后重解", nth: 2);
        Add(chooseAt, "var r0 = Eval(d, baseIn, opt, res, cancel, inner);", "ChooseKnob 比价基准 r0");
        Add(chooseAt, "rk = EvalProbe(d, baseIn, opt, res, cancel, inner);", "ChooseKnob 候选抬到上界");
        Add(famAt, "var r = EvalProbe(d, baseIn, opt, res, cancel, inner);", "ChooseKnob 形状族探针");
        Add(raiseAt, "r0 = Eval(d, baseIn, opt, res, cancel, inner);", "RaiseUntil 抬前（单候选）");
        Add(raiseAt, "rHi = EvalProbe(d, baseIn, opt, res, cancel, inner);", "RaiseUntil 上界（没带过来时）");
        Add(raiseAt, "var rMid = EvalProbe(d, baseIn, opt, res, cancel, inner);", "RaiseUntil 二分中点");
        Add(raiseAt, "var r = EvalProbe(d, baseIn, opt, res, cancel, inner);", "RaiseUntil 格点（对齐／走格）");
        return map;
    }

    internal sealed class SiteCounter
    {
        private readonly Dictionary<int, string> _lines;
        public readonly List<(int Pass, int Round, string Site, bool Reused)> Events = new();
        private int _pass, _round;
        public SiteCounter(Dictionary<int, string> lines) => _lines = lines;
        public void OnLog(string s)
        {
            if (s.StartsWith("── 第一遍", StringComparison.Ordinal) || s.StartsWith("── 第二遍", StringComparison.Ordinal)) { lock (Events) { _pass++; _round = 0; } }
            else if (s.StartsWith("第 ", StringComparison.Ordinal) && s.Contains(" 轮　合计", StringComparison.Ordinal))
            {
                var t = s.Substring(2).TrimStart();
                int k = 0; while (k < t.Length && char.IsDigit(t[k])) k++;
                if (int.TryParse(t[..k], out int r)) lock (Events) _round = r;
            }
        }
        public void OnSolve(LineCase lc, bool reused)
        {
            var st = new StackTrace(true);
            string site = "其他";
            foreach (var f in st.GetFrames() ?? Array.Empty<StackFrame>())
            {
                string? file = f.GetFileName();
                if (file is null || !file.EndsWith("Solver.cs", StringComparison.Ordinal)) continue;
                if (_lines.TryGetValue(f.GetFileLineNumber(), out var lab)) { site = lab; break; }
            }
            lock (Events)
            {
                // 基准场与场定孔位后重解发生在「第 N 轮」那一行印出**之前** ⇒ 记到下一轮；验下角是第 1 轮的基准
                int round = site.StartsWith("每遍开头", StringComparison.Ordinal) ? 1
                          : site.StartsWith("每轮基准场", StringComparison.Ordinal) || site.StartsWith("场定孔位", StringComparison.Ordinal) ? _round + 1 : _round;
                Events.Add((_pass, round, site, reused));
            }
        }
    }

    internal sealed class Relay : IProgress<string>
    {
        private readonly Action<string> _a;
        public Relay(Action<string> a) => _a = a;
        public void Report(string value) => _a(value);
    }

    internal static string Table(SiteCounter c, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── {title}：每一步（行）× 每一轮（列）的整线解次数；「解/复用」= 真解场次数/同状态复用次数（没花场解）");
        var keys = c.Events.Select(e => (e.Pass, e.Round)).Distinct().OrderBy(x => x.Pass).ThenBy(x => x.Round).ToList();
        var sites = c.Events.Select(e => e.Site).Distinct().ToList();
        sb.AppendLine("步骤\t" + string.Join("\t", keys.Select(k => $"遍{k.Pass}轮{k.Round}")) + "\t合计");
        foreach (var s in sites)
        {
            var cells = keys.Select(k =>
            {
                int n = c.Events.Count(e => e.Site == s && e.Pass == k.Pass && e.Round == k.Round && !e.Reused);
                int m = c.Events.Count(e => e.Site == s && e.Pass == k.Pass && e.Round == k.Round && e.Reused);
                return $"{n}/{m}";
            });
            sb.AppendLine($"{s}\t{string.Join("\t", cells)}\t{c.Events.Count(e => e.Site == s && !e.Reused)}/{c.Events.Count(e => e.Site == s && e.Reused)}");
        }
        sb.AppendLine("合计\t" + string.Join("\t", keys.Select(k => $"{c.Events.Count(e => e.Pass == k.Pass && e.Round == k.Round && !e.Reused)}/{c.Events.Count(e => e.Pass == k.Pass && e.Round == k.Round && e.Reused)}"))
                    + $"\t{c.Events.Count(e => !e.Reused)}/{c.Events.Count(e => e.Reused)}");
        sb.AppendLine("（终局复核 Finish 另有 1 次整线解／遍末，不经 EvalRaw，不在表里，在 SolverResult.Solves 里）");
        return sb.ToString();
    }

    internal static void AssertSameBits(SolverResult a, SolverResult b, List<string> diffs)
    {
        void D(string what, object? x, object? y) { if (!Equals(x, y)) diffs.Add($"{what}：{x} ≠ {y}"); }
        long Bits(double v) => BitConverter.DoubleToInt64Bits(v);
        D("Feasible", a.Feasible, b.Feasible); D("HitBound", a.HitBound, b.HitBound); D("Undetermined", a.Undetermined, b.Undetermined);
        D("StopWhy", a.StopWhy, b.StopWhy); D("Message", a.Message, b.Message); D("MassG 位", Bits(a.MassG), Bits(b.MassG));
        void Arr(string n, double[]? x, double[]? y)
        {
            if (x is null || y is null) { D(n, x is null, y is null); return; }
            if (x.Length != y.Length) { diffs.Add($"{n} 长度 {x.Length} ≠ {y.Length}"); return; }
            for (int i = 0; i < x.Length; i++) if (Bits(x[i]) != Bits(y[i])) diffs.Add($"{n}[{i}] {x[i]:R} ≠ {y[i]:R}");
        }
        Arr("板厚", a.Design.TabThickMm, b.Design.TabThickMm); Arr("舌保温", a.Design.TabInsulMm, b.Design.TabInsulMm);
        Arr("环倍率", a.Design.RingMul, b.Design.RingMul); Arr("外级倍率", a.Design.RingMul2, b.Design.RingMul2);
        Arr("内级环宽", a.Design.RingW1Mm, b.Design.RingW1Mm); Arr("外级环宽", a.Design.RingW2Mm, b.Design.RingW2Mm);
        Arr("槽张角", a.Design.SlotSpanDeg, b.Design.SlotSpanDeg); Arr("舌孔 R", a.Design.TabHoleRMm, b.Design.TabHoleRMm);
        Arr("槽心", a.Design.SlotCenterDeg, b.Design.SlotCenterDeg); Arr("舌片厚", a.Design.TongueThickMm, b.Design.TongueThickMm);
        var ca = a.Best?.Checks ?? Array.Empty<ConstraintOut>(); var cb = b.Best?.Checks ?? Array.Empty<ConstraintOut>();
        D("判据条数", ca.Length, cb.Length);
        for (int i = 0; i < Math.Min(ca.Length, cb.Length); i++)
        {
            D($"判据[{i}] 名", ca[i].Name, cb[i].Name); D($"判据[{i}] {ca[i].Name} 值位", Bits(ca[i].Actual), Bits(cb[i].Actual));
            D($"判据[{i}] {ca[i].Name} 过", ca[i].Ok, cb[i].Ok); D($"判据[{i}] {ca[i].Name} 判不了", ca[i].Undetermined, cb[i].Undetermined);
        }
        D("轨迹行数", a.Trace.Count, b.Trace.Count);
        for (int i = 0; i < Math.Min(a.Trace.Count, b.Trace.Count); i++)
            if (a.Trace[i] != b.Trace[i]) { diffs.Add($"轨迹第 {i} 行不同：{a.Trace[i]} ≠ {b.Trace[i]}"); break; }
    }

    /// <summary>
    /// 慢门 A：同一设计（W08 种子、盘 R = w = 37、不挖舌孔、1 轮、生产导航网格；与夜跑 021617 第 1 形状同一设置，只把轮数截到 1）开与关各跑一次（两线程并跑），
    /// 断言：旋钮终值、判据表（名、值位、过、判不了）、轨迹、停因逐位相同；关的场解次数 = 开的场解次数 + 开的复用次数；开的复用次数 &gt; 0。
    /// 另印「每轮每一步各解几次」归因表进证据档（只印）。
    /// 变因（2026-09-24 当天改）：第一版用盘 R = w = 32、2 轮、粗筛平坦区网格 20 mm，同机争用下两跑 3 h 未完被时间闸切掉、没出结果；
    ///   改到已量过的设置（慢探针实测该设置第 1 轮 25 次真解 + 4 次复用），1 轮已覆盖比价基准的复用（每条违反一次）。
    /// </summary>
    [Fact]
    [Trait("速度", "慢")]
    public void A慢_开关两跑逐位相同_只少场解_并印每步场解归因()
    {
        var baseIn = new DesignInputs();
        var d = Shape(37, 37);
        var sites = SiteLines();
        string path = DeliverableOut.Stamped("R48_搜形状_粗筛场解归因_同状态复用逐位门.txt");   // 开跑时就取文件名与时刻（跑完才取会把写档时刻当开跑时刻）
        var t0 = DateTime.Now;
        string load0 = R48ShapeSearchRunTests.LoadAvg();
        SolverOptions O(bool reuse, SiteCounter c) => new SolverOptions
        {
            AllowTabCuts = false, MaxRounds = 1,
            ReuseSameStateSolves = reuse, SolveProbe = c.OnSolve,
        };
        var cOff = new SiteCounter(sites); var cOn = new SiteCounter(sites);
        var sw = Stopwatch.StartNew();
        var tOff = Task.Run(() => Solver.Solve(d, baseIn, O(false, cOff), new Relay(cOff.OnLog), CancellationToken.None));
        var tOn = Task.Run(() => Solver.Solve(d, baseIn, O(true, cOn), new Relay(cOn.OnLog), CancellationToken.None));
        Task.WaitAll(tOff, tOn);
        var off = tOff.Result; var on = tOn.Result;
        var diffs = new List<string>();
        AssertSameBits(off, on, diffs);

        var sb = new StringBuilder();
        sb.AppendLine("# 同状态复用（A）开关两跑逐位门 ＋ 每步场解归因（Linux、待 Windows 重录）");
        sb.AppendLine($"开跑 {t0:yyyy-MM-dd HH:mm:ss}　平台 {RuntimeInformation.OSDescription}／{RuntimeInformation.FrameworkDescription}／{Environment.ProcessorCount} 核　"
                    + $"提交 {R48ShapeSearchRunTests.Git("rev-parse --short HEAD")}（未提交改动在工作树）　loadavg 开跑 {load0}／跑完 {R48ShapeSearchRunTests.LoadAvg()}　同机并跑：本门自身两线程并跑（开、关各一），另有别的实施者的测试");
        sb.AppendLine($"设计：W08 种子（{d.Name}），盘半径 {d.DiscRadiusMm}、舌半宽 {d.TabHalfWidthMm}、舌长 {d.TabLengthMm:0.0}；不挖舌孔；MaxRounds 1；生产导航网格（粗筛平坦区网格 0）");
        sb.AppendLine($"墙钟 {sw.Elapsed.TotalMinutes:0.0} min（两跑并跑）");
        sb.AppendLine($"关：场解 {off.Solves}、复用 {off.SameStateReuses}；开：场解 {on.Solves}、复用 {on.SameStateReuses}；关的场解 − 开的场解 = {off.Solves - on.Solves}");
        sb.AppendLine($"停因（关）：{off.StopWhy}");
        sb.AppendLine($"逐位比较：{(diffs.Count == 0 ? "旋钮终值、判据表、轨迹、停因逐位相同" : "有差异 " + diffs.Count + " 处")}");
        foreach (var x in diffs.Take(40)) sb.AppendLine("　" + x);
        sb.AppendLine();
        sb.AppendLine(Table(cOff, "关（改前行为）"));
        sb.AppendLine(Table(cOn, "开（同状态复用）"));
        sb.AppendLine("场解调用处行号（Solver.cs，按语句原文找）：" + string.Join("；", sites.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        _o.WriteLine(sb.ToString());

        Assert.Empty(diffs);
        Assert.Equal(off.Solves, on.Solves + on.SameStateReuses);
        Assert.Equal(0, off.SameStateReuses);
        Assert.True(on.SameStateReuses > 0, "开了复用却一次没命中：归因里的重复解没接上");
    }

    /// <summary>
    /// 慢探针（只印不判）：生产导航网格（粗筛平坦区网格 0）上、与夜跑 021617 第 1 形状同一设置（W08 种子、盘 R = w = 37、不挖舌孔、2 轮）
    /// 开复用、开跳过放大，印每步场解归因；并与 021617 第 1 行（场解 49、铂重 4999.8 g、三条硬判据 16.082／4.748／−1.930）对照：
    /// 同一代码路径下「场解 + 复用」应等于 49、判据逐位（印出来的三位小数）相同。环境变量 SHAPE_ATTR_R 改盘半径（缺省 37）。
    /// </summary>
    [Fact]
    [Trait("速度", "慢")]
    public void 探针_生产导航网格_一形状两轮_每步场解归因_对照夜跑021617第1行()
    {
        var baseIn = new DesignInputs();
        double R = R48ShapeSearchRunTests.EnvD("SHAPE_ATTR_R", 37);
        var d = Shape(R, R);
        var sites = SiteLines();
        var c = new SiteCounter(sites);
        string path = DeliverableOut.Stamped("R48_搜形状_生产导航网格_场解归因探针.txt");
        using var sink = new R48ShapeSearchRunTests.Sink(path);
        sink.W("# 生产导航网格上一形状两轮的每步场解归因（Linux、待 Windows 重录；只印不判）");
        sink.W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　平台 {RuntimeInformation.OSDescription}／{RuntimeInformation.FrameworkDescription}／{Environment.ProcessorCount} 核　"
             + $"提交 {R48ShapeSearchRunTests.Git("rev-parse --short HEAD")}（未提交改动在工作树）　loadavg {R48ShapeSearchRunTests.LoadAvg()}　同机并跑：{Environment.GetEnvironmentVariable("SHAPE_CONTENTION") ?? "未申报"}");
        sink.W($"设计：W08 种子，盘半径 {R}、舌半宽 {R}、舌长 {d.TabLengthMm:0.0}；不挖舌孔；MaxRounds 2；粗筛平坦区网格 0（生产导航网格）；同状态复用 开；终局复核后不放大重做 开");
        sink.W("对照：deliverable/R48_搜形状_Core驱动_W08_本次开跑于2026-09-24_021617.txt 第 1 行（盘Ø74／舌 74，2 轮，场解 49，4999.8 g，16.082／4.748／−1.930）");
        var sw = Stopwatch.StartNew();
        var r = Solver.Solve(d, baseIn, new SolverOptions
        {
            AllowTabCuts = false, MaxRounds = 2, ReuseSameStateSolves = true, SkipRadiusGrowthAfterFinalCheck = true, SolveProbe = c.OnSolve,
        }, new Relay(s => { c.OnLog(s); if (s.StartsWith("第 ", StringComparison.Ordinal)) sink.W($"　　{s.Split('\n')[0]}　（{sw.Elapsed.TotalMinutes:0.0} 分）"); }), CancellationToken.None);
        var hot = ShapeSearchDriver.ShoulderJEstimate(R, r, r.Design);   // 只为印一个 w = R 时的估计量级
        sink.W($"墙钟 {sw.Elapsed.TotalMinutes:0.0} min；场解 {r.Solves}、同状态复用 {r.SameStateReuses}、合计 {r.Solves + r.SameStateReuses}（021617 第 1 行 49）；铂重 {r.MassG:0.0} g");
        sink.W($"三条硬判据：{HardCritValue.Of(r.Best, LineResult.Key.HotOverTc).Text()}　{HardCritValue.Of(r.Best, LineResult.Key.ColdUnderTc).Text()}　{HardCritValue.Of(r.Best, LineResult.Key.NetFlux).Text()}");
        sink.W($"停因：{r.StopWhy}");
        sink.W($"粗筛跳过放大：{(double.IsNaN(r.SkippedRadiusGrowthToMm) ? "没发生" : r.SkippedRadiusGrowthToMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm（" + r.SkippedRadiusGrowthWhy + "）")}");
        sink.W($"板厚 {DesignSpec.Fmt(r.Design.TabThickMm, "0.00")}　舌保温 {DesignSpec.Fmt(r.Design.TabInsulMm, "0.0")}　设计电流 {(r.DesignCurrent is null ? "未查到" : string.Join("/", r.DesignCurrent.PlateA.Select(a => a.ToString("0", CultureInfo.InvariantCulture))))} A；w = R 时 I/(t·2w) 估计 {hot.J:0.00}（{hot.Detail}）");
        sink.W(Table(c, "开（同状态复用、跳过放大）"));
        sink.W("场解调用处行号（Solver.cs，按语句原文找）：" + string.Join("；", sites.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Assert.True(r.Solves > 0);
    }
}
