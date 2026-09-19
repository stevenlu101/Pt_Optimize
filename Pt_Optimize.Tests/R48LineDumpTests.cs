using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 I 路（2026-09-15，Opus 5；合并把关待办 P0-2）：**整线全量转储** —— 给「E 纯搬移／带玻璃生产数逐位不变」补证据，并留作整线逐位记录门。
///
/// ══ 起因
///   LineRunner 的 E 最终版合入（SolveSegmentsInto／CaseGuardMessage／SegmentEndDraws／MeanDraw／IterateNeighbourTemps／NeighbourTempsOf 等搬移，
///   外加合并时手工搬进 SolveSegmentsInto 的 G3／G1 三行）注释里写「纯搬移、数逐位不变，全量转储为证」，但合并树上没有转储：
///   E 自己那份对拍（r48_E\deliverable\R48_E_LineRunner搬移逐位对拍_2026-09-15.txt）在 r48_E 底板 f206e70 上跑、只有 W08 解析一个设计、生成它的临时测试已删，
///   合并时搬进去的三行没进过任何转储。快套件没有整线带玻璃逐位记录门。
///
/// ══ 转储什么（跑前写死）
///   LineRunner.Run 之后，把**跑后的算例**（含 WarmStart、BaselineRootC、Base 全部公开成员）与 **LineResult 全部公开成员**（逐段 SegmentOut、逐片 FlangeOut
///   含网格 ShellMesh 与配方记录、逐条 ConstraintOut、Notes）按成员名排序递归写成文本：double／float 按 "R"（往返精度），整数、布尔、枚举原样，文字原样（换行转义），
///   基元数组 ≤ 12 个元素逐个写、更长的写「个数 + 各元素 R 串以换行连接后的 SHA-256」（逐位等价）；对象列表 ≤ 64 项逐项展开，更长的（网格的面、节点、形心）
///   逐元素照样转储、只写「项数 + 全部元素转储文本的 SHA-256 + 第 0 项」（逐位等价）；委托只写有无；同一对象第二次出现写「同上」。
///   不写任何耗时、时刻、路径 —— 同一份代码重跑文件逐字相同（确定性由同一树跑两次验）。
///   每个算例一份转储文件，SHA-256 取文件全文（UTF-8 无 BOM）；另算「去文字」SHA-256（文字成员只写占位「<文字>」，不写长度 —— 2026-09-16 Opus 5 改，原写字数、改措辞变字数也动数），给记录门用（说明文字改措辞不该动数）。
///
/// ══ 算例（跑前写死；覆盖合并把关待办点名的五类）
///   1 B2 带玻璃（与 R48G2RampClampChannelTests B2 同一组输入，耦合容差 0.25 K）；
///   2 B2 空管到温稳态、管腔轴向辐射系数置 0；3 B2 空管到温稳态、默认管腔系数（合并时搬进 SolveSegmentsInto 的管腔报数行走这一支）；
///   4 B2 带玻璃、按实测电流求解（段电流写死 1070／1000／880 A）；
///   5 盘 Ø56 两段（R47NavGridInstrumentTests.Disc56TwoSegs）解析路径、电导率随温度耦合开；
///   6 同一设计的图纸路径（内存厚度场：解析板栅格化步 0.5、留白 2，判据几何 = 同组板，逐片舌保温 = 设计值，与 R48DiscInsulPerPlateGateTests 同一做法；CheckRamp 开，同界面）。
///
/// ══ 输出（只写带开跑时刻的新文件，不覆盖 deliverable 里已有的任何文件）
///   deliverable\R48_整线全量转储_{算例}_本次开跑于{yyyy-MM-dd_HHmmss}_{进程号}.txt 与 …_SHA256汇总_本次开跑于….txt（汇总头印仓库 HEAD、Core 改动指纹与三份源文件 SHA-256，标明是哪棵树）。
///
/// ══ 判读
///   两棵树的转储逐字相同 ⇔ 所列算例上 LineRunner.Run 的全部公开输出逐位相同。不同 ⇒ 逐字段 diff、逐个说明是否属于有意改动（在证据文件里写，不在这里改判）。
///   记录门（<see cref="Records"/>）：算例的「去文字」SHA-256 与记录不同 ⇒ 断言失败。记录为空的算例只打印。改了数的有意改动要重记：写旧值 → 新值与依据。
/// </summary>
[Trait("速度", "慢")]
public class R48LineDumpTests
{
    private readonly ITestOutputHelper _out;
    public R48LineDumpTests(ITestOutputHelper o) { _out = o; }

    /// <summary>
    /// 「去文字」SHA-256 记录（小写十六进制）。空 = 本算例还没有记录，只打印。
    /// 2026-09-15 Opus 5（I 路）首记：取自合并树快照（r48_I，Core 改动指纹 6a6c9848…）18:30 两跑（进程 8348／22408，六个算例全文与去文字 SHA-256 两跑逐位相同 = 确定性），
    ///   与对照树（r48_I_Eprev：LineRunner／Solver／InsulationSearch 换回 E 第一轮合入后的合并树备份，18:30 进程 22756；再把 ShellThermal／ShellMesh 换回复审前备份，18:41 进程 2824）两跑也逐位相同。
    ///   出处 deliverable\R48_整线全量转储_SHA256汇总_本次开跑于2026-09-15_183011_8348.txt 等，汇总见 deliverable\R48_I_证据与数值核验_2026-09-15_本次开跑于2026-09-15_1752.txt 的 I2 节。
    ///   ⚠ 记录绑定这台机器上的 .NET 8 浮点实现与本仓当前的公开成员集合：给 SegmentOut／FlangeOut／LineCase／DesignInputs 加删公开成员也会动它 —— 重记时写旧值 → 新值与依据。
    /// 2026-09-16 Opus 5（I 路修复，审查意见 5）重记：去文字口径改了（文字成员原写「<文字 N 字>」、改为只写占位「<文字>」），数没动 ——
    ///   本次跑（deliverable\R48_整线全量转储_SHA256汇总_本次开跑于2026-09-16_195416_12812.txt）六份**全文** SHA-256 与 2026-09-15 20:13 跑逐位相同（全文不经占位，不受此改影响），只有去文字 SHA 变；旧值 → 新值：
    ///   B2_带玻璃：e2c8c9dd29de26a8… → c3b312d6c2a6fe3f3f26033b4b48446f27e4c6789b8e360807af16956e439dbb（全文 7583496e68aaaa1d…，与 09-15 相同）；
    ///   B2_空管_管腔系数0：31eeb735a7c5210e… → e21f055b1b2ad6a858d62e090b12fc3c022a0c9d06bb9ad6d7da20b055d06cc1（全文 474dd97532c3470d…，与 09-15 相同）；
    ///   B2_空管_默认管腔系数：211797041de4668a… → bec1f0223070e18cc8a3ee870a2facb8ac6e038412658270bb1c0374d6c3a552（全文 10111aadc2f5494c…，与 09-15 相同）；
    ///   B2_带玻璃_实测电流：5703fc7f262167a6… → 37b7b59de3851a1656840fa59bbd4f2a29c7b1b8e39252f9fa8220a031a8924d（全文 6fd91d76b3550993…，与 09-15 相同）；
    ///   盘56两段_解析_电导率随温度：381aae3b954b75ad… → 1141272dc1cfe983772943ed369578800a8de0e9e845f72379120e3f360453a2（全文 8954723d0429c5f9…，与 09-15 相同）；
    ///   盘56两段_图纸路径：817ec7cf060b2da4… → be7d58aa13f5652a6fb30f19d2411c7c6e5b9d03d2cb5f9ae31e225b3038abfd（全文 391286134917e69e…，与 09-15 相同）；
    ///   第二跑逐位相同的确认写在 HANDOVER I 路修复注记 ⑤（文件名带开跑时刻）。
    /// 2026-09-18 Opus 5（合并 H/I/J/L/P/U 六路）**重记**：六个「去文字」SHA-256 全变。变因**逐项查过、不是猜的** ——
    ///   把合并前 09-16 那一跑的转储文件与本次逐行 diff（deliverable\R48_整线全量转储_B2_带玻璃_本次开跑于2026-09-16_200618_9276.txt 对 …_2026-09-18_120159_1904.txt），差异分三类：
    ///   ① **新增公开成员**（转储按公开成员逐个写，加一个就动 SHA）：LineCase 的 BaselineTolK／CoupleTolFromMargin／CoupleTolMarginFrac／CoupleTolFloorK／EndTempAmpFromDecayLength（L 路 §0.-10）、
    ///      DesignInputs 的 HotOverTcAllowK／ColdUnderTcAllowK／MeasureInsulWindowAtFinalCheck（U 路 §0.-13U）、LineResult 的 CoupleRounds／CoupleTolKUsed／EmptyTube／FieldUndeterminedReasons／RampSweep、
    ///      SegmentOut 的 EndAmpA/B・EndDecayLengthA/BMm・EndBetaA/BWPerMK・NodeSpacingMm（L 路）、网格的 ClampFullFaceActive。
    ///   ② **有意的生产改动**：圆盘保温缺省 20 → 10 mm（L 路 §0.-11，转储里 算例.Base.FlangeInsulThickMm 那一行）；判据表多一条「接合区保温缠得出来」（Checks 21 → 22 项，L 路 §0.-12）；
    ///      ④ 管强度由「判不了」改成按保守上界判掉（Checks[7].Undetermined true → false，L 路 §0.-12）。
    ///   ③ **停机口径收紧带来的数值位移**：外层耦合容差改判据相关、放大改当场算（L 路 §0.-10）⇒ BaselineRootC 等末几位动（例：1116.6789433096615 → 1116.6762483090697）。
    ///   ⇒ 三类都是本次合并要带进来的东西，不是回归。旧值 → 新值：
    ///   B2_带玻璃：c3b312d6… → fd371d98bd2cef8422265d556d1ec61a1c4542ad3c30d0695f17cad66720bfa1（全文 d9e4592984fc6d8e…）；
    ///   B2_空管_管腔系数0：e21f055b… → 898bb0c7e6ac1db883ac105c70568dac77d56f6d77218be5765a64cfeab621bd（全文 22b99970a383371a…）；
    ///   B2_空管_默认管腔系数：bec1f022… → c4ae1fae888a667d9bf39953310ff83c2ed565275cc9486784ea0049d576422f（全文 8e8550f6ec043080…）；
    ///   B2_带玻璃_实测电流：37b7b59d… → f78da4306127be9fc4ba3dd469796c03438ea6bb8e8972dc1e7a645cc673a17c（全文 69e3a41f46a1bace…）；
    ///   盘56两段_解析_电导率随温度：1141272d… → 98f545541794521f70abcf8eb5de5822df436f142db413e70ead000fdd7017fd（全文 c82112b59b161c59…）；
    ///   盘56两段_图纸路径：be7d58aa… → 97a638fba8e11936584b2178ce8c6867f1debd1e28716c79284eda55e0092632（全文 c3fc8ec5e004c010…）。
    ///   出处 deliverable\R48_整线全量转储_SHA256汇总_本次开跑于2026-09-18_120159_1904.txt（Core 改动指纹 a0171e82…，git HEAD 0a15af83…）；确定性由紧跟的第二跑复核（见 HANDOVER「合并 2026-09-18」）。
    /// </summary>
    private static readonly Dictionary<string, string> Records = new()
    {
        ["B2_带玻璃"] = "fd371d98bd2cef8422265d556d1ec61a1c4542ad3c30d0695f17cad66720bfa1",
        ["B2_空管_管腔系数0"] = "898bb0c7e6ac1db883ac105c70568dac77d56f6d77218be5765a64cfeab621bd",
        ["B2_空管_默认管腔系数"] = "c4ae1fae888a667d9bf39953310ff83c2ed565275cc9486784ea0049d576422f",
        ["B2_带玻璃_实测电流"] = "f78da4306127be9fc4ba3dd469796c03438ea6bb8e8972dc1e7a645cc673a17c",
        ["盘56两段_解析_电导率随温度"] = "98f545541794521f70abcf8eb5de5822df436f142db413e70ead000fdd7017fd",
        ["盘56两段_图纸路径"] = "97a638fba8e11936584b2178ce8c6867f1debd1e28716c79284eda55e0092632",
    };

    private static readonly string Stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + "_" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private static (DesignInputs P, DesignSpec D) B2()
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit(); d.SizeTongues(p);
        return (p, d);
    }

    private static LineCase CaseOf(string name)
    {
        switch (name)
        {
            case "B2_带玻璃":
            {
                var (p, d) = B2();
                var lc = d.BuildCase(p, checkRamp: false);
                lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = 4000;
                return lc;
            }
            case "B2_空管_管腔系数0":
            case "B2_空管_默认管腔系数":
            {
                var (p, d) = B2();
                if (name == "B2_空管_管腔系数0") p.TubeCavityRadKA1150WmPerK = 0.0;
                var lc = d.BuildCase(p, checkRamp: false, emptyTube: true, emptyTubeSetpoint: EmptyTubeSetpoint.RampTarget);
                lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = 4000;
                return lc;
            }
            case "B2_带玻璃_实测电流":
            {
                var (p, d) = B2();
                var lc = d.BuildCase(p, checkRamp: false);
                lc.UseMeasuredCurrent = true;
                lc.MeasuredCurrentA = new[] { 1070.0, 1000.0, 880.0 };
                lc.CoupleTolK = 0.25; lc.CoupleMaxRounds = 4000;
                return lc;
            }
            case "盘56两段_解析_电导率随温度":
            {
                var p = new DesignInputs { SigmaOfTCoupling = true };
                var d = R47NavGridInstrumentTests.Disc56TwoSegs();
                return d.BuildCase(p, checkRamp: false);
            }
            case "盘56两段_图纸路径":
            {
                var p = new DesignInputs();
                var d0 = R47NavGridInstrumentTests.Disc56TwoSegs();
                var lc = d0.BuildCase(p, checkRamp: true);
                double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
                var plates = Enumerable.Range(0, d0.FlangeCount).Select(j => { var g = d0.Plate(j, d0.DiscFloorMm(p)); g.HoleRadiusMm = holeR; return g; }).ToArray();
                lc.FlangePlates = Array.Empty<FlangePlate>();
                lc.FlangeFields = plates.Select(g => AnalyticSurrogate.Rasterize(g, 0.5, 2.0)).ToArray();
                lc.GeomForJudge = plates;
                lc.TabInsul3dmPerPlateMm = (double[])d0.TabInsulMm.Clone();
                return lc;
            }
            default: throw new ArgumentException(name);
        }
    }

    public static readonly string[] CaseNames =
    {
        "B2_带玻璃", "B2_空管_管腔系数0", "B2_空管_默认管腔系数", "B2_带玻璃_实测电流", "盘56两段_解析_电导率随温度", "盘56两段_图纸路径",
    };

    [Fact]
    public void 整线全量转储_六个算例_逐算例SHA256()
    {
        string root = HandoverDoc.Root();
        // 文件名都带本次开跑于{Stamp}（含进程号：同一秒并跑两份不撞名；不走 DeliverableOut 就是为了这个进程号）
        string sumFile = Path.Combine(root, "deliverable", $"R48_整线全量转储_SHA256汇总_本次开跑于{Stamp}.txt");
        if (File.Exists(sumFile)) throw new InvalidOperationException("汇总文件已存在：" + sumFile);
        var sum = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sum.AppendLine(s);
            File.WriteAllText(sumFile, sum.ToString(), new UTF8Encoding(false));
        }
        string Src(string rel)
        {
            string f = Path.Combine(root, rel);
            return File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant() : "（无此文件）";
        }
        Say($"R48 整线全量转储 SHA-256 汇总（Opus 5）　开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　出处 Pt_Optimize.Tests/R48LineDumpTests.cs　判读见类注释（跑前写死）");
        Say($"仓库根 {root}　git HEAD {EvidenceHeader.GitHead(root)}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(root)}");
        foreach (var rel in new[] { "Pt_Optimize/Core/LineRunner.cs", "Pt_Optimize/Core/Solver.cs", "Pt_Optimize/Core/InsulationSearch.cs", "Pt_Optimize/Core/ShellThermal.cs", "Pt_Optimize/Core/ShellMesh.cs" })
            Say($"   源文件 SHA-256　{rel}　{Src(rel)}");

        var fails = new List<string>();
        foreach (var name in CaseNames)
        {
            var lc = CaseOf(name);
            var sw = Stopwatch.StartNew();
            LineResult r;
            try { r = LineRunner.Run(lc); }
            catch (Exception ex) { Say($"{name}：LineRunner.Run 抛异常 {ex.GetType().Name}：{ex.Message}"); fails.Add(name + " 抛异常"); continue; }
            double sec = sw.Elapsed.TotalSeconds;
            var full = Dump(lc, r, withText: true);
            var numeric = Dump(lc, r, withText: false);
            string shaFull = Sha(full), shaNum = Sha(numeric);
            string file = Path.Combine(root, "deliverable", $"R48_整线全量转储_{name}_本次开跑于{Stamp}.txt");
            File.WriteAllText(file, full, new UTF8Encoding(false));
            string rec = Records.TryGetValue(name, out var v) ? v : "";
            string verdict = rec.Length == 0 ? "无记录，只打印" : rec == shaNum ? "与记录逐位相同" : "★★ 与记录不同";
            Say($"{name}：Ok {r.Ok}　收敛 {r.Converged}　{sec:0} s　转储 {full.Count(ch => ch == '\n')} 行　全文 SHA-256 {shaFull}　去文字 SHA-256 {shaNum}　记录 {(rec.Length == 0 ? "—" : rec)} ⇒ {verdict}　文件 {Path.GetFileName(file)}");
            if (rec.Length > 0 && rec != shaNum) fails.Add($"{name}：去文字 SHA-256 {shaNum} ≠ 记录 {rec}");
        }
        Assert.True(fails.Count == 0, string.Join("\n", fails));
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    // ──────────────────── 反射转储 ────────────────────

    /// <summary>跑后算例 + 结果的全量转储文本（成员名排序、double 按 R）。<paramref name="withText"/> false ⇒ 文字成员只写占位「<文字>」（不写长度）。</summary>
    public static string Dump(LineCase lc, LineResult r, bool withText)
    {
        var d = new Dumper(withText);
        d.Value("算例", lc, 0);
        d.Value("结果", r, 0);
        return d.Sb.ToString();
    }

    private sealed class Dumper
    {
        public readonly StringBuilder Sb = new();
        private readonly bool _text;
        private readonly Dictionary<object, string> _seen;
        public Dumper(bool withText) : this(withText, new Dictionary<object, string>(ReferenceEqualityComparer.Instance)) { }
        public Dumper(bool withText, Dictionary<object, string> seen) { _text = withText; _seen = seen; }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const int MaxInline = 12, MaxObjectsExpanded = 64, MaxDepth = 24;

        private static string? Scalar(object? v)
        {
            switch (v)
            {
                case null: return "null";
                case double x: return x.ToString("R", Inv);
                case float x: return x.ToString("R", Inv);
                case decimal x: return x.ToString(Inv);
                case bool x: return x ? "true" : "false";
                case char x: return ((int)x).ToString(Inv);
                case Enum e: return e.GetType().Name + "." + e.ToString();
                case sbyte or byte or short or ushort or int or uint or long or ulong: return Convert.ToString(v, Inv);
                default: return null;
            }
        }

        // 2026-09-16 Opus 5（I 路审查意见 5）：去文字原写「<文字 N 字>」，改措辞变字数也会动去文字 SHA ⇒ 改成只写占位「<文字>」（六条记录同日重记）
        private string Text(string s) => _text ? "\"" + s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\"" : "<文字>";

        public void Value(string path, object? v, int depth)
        {
            var sc = Scalar(v);
            if (sc is not null) { Sb.Append(path).Append(" = ").Append(sc).Append('\n'); return; }
            if (v is string s) { Sb.Append(path).Append(" = ").Append(Text(s)).Append('\n'); return; }
            if (v is Delegate) { Sb.Append(path).Append(" = <委托>\n"); return; }
            if (v is Type t) { Sb.Append(path).Append(" = <类型 ").Append(t.FullName).Append(">\n"); return; }
            var type = v!.GetType();
            if (!type.IsValueType)
            {
                if (_seen.TryGetValue(v, out var first)) { Sb.Append(path).Append(" = <同上 ").Append(first).Append(">\n"); return; }
                _seen[v] = path;
            }
            if (depth > MaxDepth) { Sb.Append(path).Append(" = <超过深度 ").Append(type.Name).Append(">\n"); return; }
            if (v is IEnumerable en)
            {
                var items = en.Cast<object?>().ToList();
                string shape = v is Array arr && arr.Rank > 1 ? "[" + string.Join(",", Enumerable.Range(0, arr.Rank).Select(k => arr.GetLength(k))) + "]" : "";
                var scal = items.Select(x => x is string xs ? Text(xs) : Scalar(x)).ToList();
                if (items.Count == 0) { Sb.Append(path).Append(" = [] ").Append(shape).Append('\n'); return; }
                if (scal.All(x => x is not null))
                {
                    if (items.Count <= MaxInline)
                        Sb.Append(path).Append(" = [").Append(string.Join(", ", scal)).Append("] ").Append(shape).Append('\n');
                    else
                        Sb.Append(path).Append(" = <").Append(items.Count).Append(" 个").Append(shape).Append("，SHA-256 ").Append(Sha(string.Join("\n", scal))).Append(">\n");
                    return;
                }
                if (items.Count > MaxObjectsExpanded)
                {
                    // 长的对象列表（网格的面、节点、形心……）：每个元素照样逐成员转储，但只写「个数 + 全部元素转储文本的 SHA-256 + 第 0 个元素」（逐位等价，文件不至于几百万行）
                    var sub = new Dumper(_text, _seen);
                    for (int i = 0; i < items.Count; i++) sub.Value($"[{i}]", items[i], depth + 1);
                    Sb.Append(path).Append(" = <").Append(items.Count).Append(" 项").Append(shape).Append("，逐元素转储 SHA-256 ").Append(Sha(sub.Sb.ToString())).Append(">\n");
                    var head = new Dumper(_text, new Dictionary<object, string>(ReferenceEqualityComparer.Instance));
                    head.Value($"{path}[0]", items[0], depth + 1);
                    Sb.Append(head.Sb);
                    return;
                }
                Sb.Append(path).Append(" = <").Append(items.Count).Append(" 项").Append(shape).Append(">\n");
                for (int i = 0; i < items.Count; i++) Value($"{path}[{i}]", items[i], depth + 1);
                return;
            }
            bool ours = type.Namespace?.StartsWith("PtOptimize", StringComparison.Ordinal) == true
                     || (type.IsGenericType && (type.Name.StartsWith("ValueTuple", StringComparison.Ordinal) || type.Name.StartsWith("KeyValuePair", StringComparison.Ordinal)));
            if (!ours) { Sb.Append(path).Append(" = <").Append(type.Name).Append(" ").Append(Convert.ToString(v, Inv)).Append(">\n"); return; }
            Sb.Append(path).Append(" : ").Append(type.Name).Append('\n');
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public;
            var members = type.GetFields(F).Select(f => (Name: f.Name, Get: (Func<object?>)(() => f.GetValue(v))))
                .Concat(type.GetProperties(F).Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .Select(p => (Name: p.Name, Get: (Func<object?>)(() => p.GetValue(v)))))
                .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
            foreach (var (name, get) in members)
            {
                object? mv;
                try { mv = get(); }
                catch (Exception ex) { Sb.Append(path).Append('.').Append(name).Append(" = <取值抛 ").Append((ex.InnerException ?? ex).GetType().Name).Append(">\n"); continue; }
                Value(path + "." + name, mv, depth + 1);
            }
        }
    }
}
