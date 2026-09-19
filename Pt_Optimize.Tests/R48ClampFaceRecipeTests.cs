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

/// <summary>
/// ★★★★ R48 F 慢探针（2026-09-15，Opus 5 写）：**压接边界改在面上定温之后，整面口径下重验收敛阶与压接细带去留**。
///
/// ══ 起因
///   物理把关人（2026-09-14 晚）：整面接触后压接区按形心整格定温、定电位，等效边界比真实内边往压接区里偏 h/2，一阶误差 ⇒ 改在面上施加（配方 ⑤，ShellMesh.ClampFaceDirichlet）。
///   数值把关人：整面口径下抽热是分量相减的差，抽热自身的比值不是阶的估计（R48_压接整面接触AB_2026-09-14.txt：片0 抽热三档 +21.967／+22.034／+22.134，
///   比 1.49；分量 夹持带走 比 0.47、其余 0.48、舌区发热 0.69 —— 这组数是工单转述的，AB 文件不在本工作树里）；
///   整面后压接内边与绝热侧边交角 90°、解正则，外圈口径下的 r^(1/2) 奇点没了 ⇒ 自相似压接细带（配方 ④）的依据不再成立，必须单独重验。
///
/// ══ 设置（工作点取第二批 B2，出处 R48_第二批保温扫描_B2_2026-09-14.txt；与 R48ClampFullFaceTests 同一设计）
///   设计：管保温 7.5；逐片圆盘保温 10.5／3.5／5／10；逐片舌保温 5／3.5／5／10.5；SizeTongues；SplitSharedFlangeDraw = true。
///   片0 1072 A、管根 1139.95 °C；片1 1737 A、管根 1120.76 °C。单片、管侧固定、等温电流场（控温点电阻率）、夹持定温。
///   调用全走生产函数：FlangeMesher.Build（压接三个开关都显式写）、ShellCurrent.SolveFor、LineRunner.PlateThermalInputs + SolvePlateThermal、
///   分级网格 MeshAdapt.RefineWholeMesh 配 MeshVerify.RequiredMeshFor 的细区半径（与 R48ClampBandSelfSimilarTests／R48ClampRecipeImpactTests 同一调用）。
///
/// ══ 各列的定义（式子写清）
///   抽热 = QFromTubeW（管孔面上的净流入）；舌区发热 = QGenW（**整片焦耳热**，列名沿用 AB 文件；面上口径下压接格 J = 0，故 = 自由格发热）；
///   夹持带走 = QToClampW（穿过压接面进铜排的热）；其余 = 抽热 − 夹持带走 + 舌区发热（能量守恒：= 自由格表面散热 + 能量残差；形心口径下还多内边压接格那半份 J 的发热）；
///   舌区峰 = TTabMaxC、盘峰 = TDiscMaxC；残差 = EnergyResidualW（另印，供核上式）。
///   比值 r(h₁,h₂,h₃) = (Q(h₃) − Q(h₂)) ÷ (Q(h₂) − Q(h₁))，h 依次减半。
///
/// ══ (a) 收敛阶 → deliverable/R48_压接面上定温_收敛阶_2026-09-15.txt
///   面上口径均匀网格 h = 1／0.5／0.25／0.125（不铺细带，均匀网格上细带无意义；与 AB 同一建网格调用）；同一趟再跑形心口径 h = 1／0.5／0.25 作对照。
///   开跑即核（断言）：片0 形心口径三档抽热对得上 AB 文件 +21.967／+22.034／+22.134（打印三位 ⇒ 容差 0.0005）—— 对不上说明设置与 AB 不同，后面的比较不作数。
///   跑前写死的判读（数值把关人）：
///     · 舌区发热 r(0.5, 0.25, 0.125) ≤ 0.6 ⇒ 只是没进渐近区；≥ 0.65 ⇒ 有次线性来源，报出来；落在 0.6～0.65 之间 ⇒ 两条都不下结论，照实报。
///     · 夹持带走的比值应较形心口径（0.47）明显变小。操作化（跑前定）：同一三档 h = 1／0.5／0.25 上，面上比值 ≤ 形心比值 − 0.10，
///       **或** 面上 0.5→0.25 的步长绝对值 ≤ 形心同档步长绝对值的一半 ⇒ 生效；两条都不满足 ⇒ 实现可能没生效（断言红）。
///
/// ══ (b) 细带去留 → deliverable/R48_压接面上定温_细带去留_2026-09-15.txt
///   面上口径；片0、片1；h = 1／0.5／0.25；三组：均匀（不铺细带）／分级不带细带（clampBandMm 0）／分级 + 自相似细带（跑的时候是生产缺省 NaN = 3·hFine；
///   判读后缺省改为不铺，本组改为显式 clampBandMm 3·hFine，数逐位不变 —— 2026-09-15 Opus 5）。
///   跑后补充（不进判读，只打印）：导航网格（BuildCase 的生产网格参数，hFine 2）上同样三组，看缺省不铺细带后导航网格离均匀 h=2 多远。
///   跑前写死的判读（数值把关人）：两片三档「分级不带细带 − 均匀」的抽热都 ≤ 0.05 W ⇒ 整面下不需要细带，改默认为不带（省格子），并在注释写明依据；
///   否则保留细带，并按整面口径重写依据。本探针只报判读，不断言结论（两种结论都合法）。
///   首跑结论（2026-09-15 02:40）：最大偏差 0.0045 W ≤ 0.05 ⇒ 生产缺省改为不铺（FlangeMesher.ClampBandPerHFine 3.0 → 0.0）。
///
/// ══ 2026-09-15 复审修（Opus 5；审查意见 minor 三条 + major 第 4 条要的定位）
///   · 判读二的输出：面上比值为负、或分母（h = 1→0.5 的步长绝对值）不大于面上四档热场能量残差的最大绝对值时，比值**不可判** ——
///     不打 ✓、不计入比值条；断言仍是「比值条（可判时）或 步长条」。另印一句「原判据按字面（只看比值）成立不成立」，照实说。
///     ⚠ 首跑（02:40）的输出已被 03:11 的重跑覆盖，「或 步长」这条是不是首跑前就写在类注释里，现在从文件上查不了（这份源码没进过版本库）—— 照实记下。
///   · 以后每次跑，除了固定文件名，另写一份「…_本次开跑于yyyy-MM-dd_HHmm.txt」（进程内开跑时刻），重跑不覆盖先前那份。
///   · 跑后补充（不进判读）：舌区峰、盘峰面上口径四档的步长与比值、末三档极差半幅 —— 看热侧判据量有没有收敛阶。
///
/// ══ (c) 锥形舌细带去留 → deliverable/R48_压接面上定温_锥形舌细带去留_2026-09-15.txt
///   起因（复审 minor）：(b) 不铺细带的依据「内边与绝热侧边交角 90°、解正则」只对平行边舌片成立；锥形舌（DesignSpec.TabTaper，舌边是舌端两角到圆盘的切线）
///   自由侧与压接内边的夹角 = 90° + 锥角，定值边与绝热边夹角 θ &gt; 90° 时角点解带弱奇点（指数 π/(2θ) &lt; 1），(b) 在 B2 直舌上验的不能外推。
///   设置：B2 同上但 TabTaper = true、舌端半宽取 15 mm 与 10 mm 两档；片电流与管根沿用 B2 的（锥形设计没有自己的扫描记录；本探针只比网格，不比设计）。
///     ⚠ 04:45 首跑用的是 W08 原舌端半宽 30 mm —— 它等于盘半径，切线就是平行边，锥角 0.00°，非空转断言当场红（两片都停在建网格之前，没出任何数）。
///       改为 15 mm（DesignSpec 类缺省）与 10 mm（盘半径 30、舌长 140 这一族里锥角能到的上沿附近）两档，判读不变。
///   面上口径；片0、片1；h = 1／0.5；三组同 (b)：均匀／分级不带细带／分级 + 3·hFine 细带；分级配方同 (b)（MeshAdapt.RefineWholeMesh + MeshVerify.RequiredMeshFor 细区半径）。
///   跑前写死的判读（沿用 (b) 的门槛）：两片两个舌宽两档「分级不带细带 − 均匀」抽热都 ≤ 0.05 W ⇒ 不铺细带的依据对这两个锥角也成立，生产缺省不变；
///   否则 ⇒ 锥形舌要恢复压接细带（生产里按舌形分支，另报，本探针不改生产）。
///   跑后补充（04:55 跑完之后加，不是跑前判读）：判读触发（最大 2.01 W）后，同一次跑里核「分级 + 细带 − 均匀」差距收没收小 —— 补救要有数撑着才报。
///
/// ══ (d) 剩下的正偏差在哪 → deliverable/R48_压接面上定温_正偏差定位_2026-09-15.txt
///   起因（复审 major 第 4 条，审查人自己标了「只是猜测，要实测」）：面上口径抽热随 h 从高处降下来，候选来源是电流场管孔仍在格上钉 V = 0。
///   本探针只做定位、不改生产：面上口径、不铺细带；片0、片1；五张网格，全部共用一份栅格（FlangeMesher.Rasterize，步长 = RasterStepFor(0.25)，
///   免得栅格步不同混进来；另印一行 Build 自带栅格的均匀 h = 1 看栅格本身差多少）：
///     均匀 h = 1、均匀 h = 0.25、「h = 1 + 中心带 0.25」两档（FlangeMesher.BuildFromField 的内带参数；R = 孔半径 + 3 mm 即孔边一圈，R = 盘半径即整个圆盘）。
///   份额 f = (Q_加密 − Q_h1) ÷ (Q_h0.25 − Q_h1)，对舌区发热（整片焦耳热 ρ(T)·J²，J 取等温电流场 —— 主要随电流场变，但带温度场的 ρ(T)，不是纯电流场量）、抽热、其余各算一个。
///   ⚠ 张量积网格：内带加密的是 |x| ≤ R 的整列与 |z| ≤ R 的整行 —— 舌片若比 R 窄，整条舌片在 z 向也变细；舌片的 x 向分辨率与压接内边不变。
///   ⚠ 孔边一圈里电流场管孔（在格上钉 V = 0）与热场孔边梯度都在，本探针分不开两者。
///   跑前写死的判读（以舌区发热的份额为主判；两片同类才下结论，否则「两片不一致，不下结论」）：
///     · 孔边一圈 f ≥ 0.6 ⇒ 焦耳热的网格误差集中在孔边一圈，电流场管孔在格上钉 V = 0 是头号嫌疑 ⇒ 值得单开一路做管孔面上定电位的条带快门与收敛阶；
///     · 否则 孔边一圈 f ≤ 0.3 且 整个圆盘 f ≥ 0.6 ⇒ 误差在圆盘本体（离孔边有距离），管孔 V = 0 不是主因；
///     · 否则 整个圆盘 f ≤ 0.3 ⇒ 误差在舌片（x 向分辨率），与管孔无关；
///     · 其余 ⇒ 混合，不下结论。抽热、其余的份额同样打印，不进判读（抽热是分量相减的差）。
///   ✗ **上面这套判读作废**（04:45 首跑之后发现，2026-09-15 Opus 5；首跑输出留在 …正偏差定位_2026-09-15_本次开跑于2026-09-15_0445.txt，那份印的「孔边一圈」不作数）：
///     它的前提是「中心带加密 ≈ 只加密孔边」，这在单舌张量积网格上**永远不成立** —— 舌片中线就在 z = 0 上，|z| ≤ R 的行必然整条穿过舌片；
///     B2 上更甚：孔半径 25.8、盘半径 30（环宽只有 4.2 mm），舌端半宽 30，R = 28.8 的带在 z 向盖住 96 % 的舌宽，两档 R（28.8／30）实际是同一张带。
///     判读的门槛没改、也不另立新门槛；重跑后只印数据直接推得出的那句（跑后读法，不是跑前判读）：
///     1 − f = 「|x| &gt; R 的列、|z| &gt; R 的行」加密带来的份额，也就是舌片 x 向分辨率（含压接内边 x = 舌尖 + 压接长）那部分；
///     其余份额 f 落在「|x| ≤ R 的 x 向分辨率（环、孔边）」与「|z| ≤ R 的 z 向分辨率（横跨舌宽与环）」两者之和里，本探针分不开，
///     电流场管孔在格上钉 V = 0 这个候选既没被证实也没被排除。
///
/// (a)～(d) 各按片拆成一个测试类（xUnit 跨类并行），同一文件的各片段落由 <see cref="Report"/> 按片顺序拼好再整体写出。
/// </summary>
public static class R48ClampFaceRecipeTests
{
    public const string ConvFile = "R48_压接面上定温_收敛阶_2026-09-15.txt";
    public const string BandFile = "R48_压接面上定温_细带去留_2026-09-15.txt";

    public static readonly (int J, double IA, double TRoot)[] Cases = { (0, 1072.0, 1139.95), (1, 1737.0, 1120.76) };

    /// <param name="taper">(c) 用：锥形舌（DesignSpec.TabTaper = true）；缺省 false 时不碰这个字段，与 (a)(b) 跑出文件时逐字相同。</param>
    /// <param name="tabHalfWidthMm">(c) 用：舌端半宽 mm；NaN（缺省）不碰，保留 W08 的 30。</param>
    public static (DesignSpec d, DesignInputs p) B2(bool taper = false, double tabHalfWidthMm = double.NaN)
    {
        var p = new DesignInputs { SplitSharedFlangeDraw = true };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        if (taper) d.TabTaper = true;
        if (!double.IsNaN(tabHalfWidthMm)) d.TabHalfWidthMm = tabHalfWidthMm;
        d = d.Fit();
        d.SizeTongues(p);
        return (d, p);
    }

    public sealed record Row(int Cells, int ClampCells, double QTube, double QGen, double QClamp, double TTabMax, double TDiscMax, double Resid, double Secs)
    {
        /// <summary>其余 = 抽热 − 夹持带走 + 舌区发热（能量守恒拆出的散热侧，见类注释）。</summary>
        public double Rest => QTube - QClamp + QGen;
    }

    /// <summary>单片解：ShellCurrent.SolveFor（等温，控温点电阻率）+ LineRunner.PlateThermalInputs／SolvePlateThermal（生产的逐片热解输入与调用）。</summary>
    public static Row Solve(LineCase lc, int j, double iA, double tRoot, ShellMesh m, string tag)
    {
        var sw = Stopwatch.StartNew();
        var ts = LineRunner.PlateThermalInputs(lc, j, iA, null);
        double tSet = ts.P2.TSetC;
        var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
        Assert.True(sc.Converged, $"{tag}：电流场没收敛");
        var th = LineRunner.SolvePlateThermal(m, sc.JMagAPerMm2, tRoot, ts);
        Assert.True(th.Converged, $"{tag}：热场没收敛（相对残差 {th.ResidualRel:E2}）");
        Assert.True(Math.Abs(th.EnergyResidualW) < 1e-3 * Math.Max(1, th.QGenW), $"{tag}：能量不闭合 残差 {th.EnergyResidualW:0.0000} W");
        return new Row(m.CellCount, m.ClampCell.Count(b => b), th.QFromTubeW, th.QGenW, th.QToClampW, th.TTabMaxC, th.TDiscMaxC, th.EnergyResidualW, sw.Elapsed.TotalSeconds);
    }

    public static string Fmt(string name, Row r)
        => $"   {name,-14} {r.Cells,8} 格（压接 {r.ClampCells,6}）  抽热 {r.QTube,9:+0.0000;-0.0000;+0.0000}  舌区发热 {r.QGen,9:0.0000}  夹持带走 {r.QClamp,9:0.0000}"
         + $"  其余 {r.Rest,9:0.0000}  舌区峰 {r.TTabMax,9:0.000}  盘峰 {r.TDiscMax,9:0.000}  残差 {r.Resid,10:+0.000000;-0.000000;+0.000000}  {r.Secs,6:0} s";

    public static double Ratio(double q1, double q2, double q3) => Math.Abs(q2 - q1) > 1e-12 ? (q3 - q2) / (q2 - q1) : double.NaN;

    /// <summary>
    /// 分片报告：每个文件按「表头 + 片0 段 + 片1 段 + 结论段」拼，任何一段更新就整体重写（两片并行跑，不许互相覆盖）。
    /// </summary>
    public static class Report
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, SortedDictionary<int, StringBuilder>> Sections = new();
        /// <summary>本进程开跑时刻（2026-09-15 Opus 5 复审修）：每份输出另写「…_本次开跑于{它}.txt」，重跑不覆盖先前那份。</summary>
        public static readonly string RunStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");

        public static Action<string> Writer(string file, int section, ITestOutputHelper o)
            => s =>
            {
                o.WriteLine(s);
                lock (Gate)
                {
                    if (!Sections.TryGetValue(file, out var secs)) Sections[file] = secs = new SortedDictionary<int, StringBuilder>();
                    if (!secs.TryGetValue(section, out var sb)) secs[section] = sb = new StringBuilder();
                    sb.AppendLine(s);
                    var all = new StringBuilder();
                    foreach (var kv in secs) all.Append(kv.Value);
                    try { File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", file), all.ToString(), new UTF8Encoding(false)); } catch { }
                    try
                    {
                        string stamped = Path.GetFileNameWithoutExtension(file) + "_本次开跑于" + RunStamp + Path.GetExtension(file);
                        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", stamped), all.ToString(), new UTF8Encoding(false));
                    }
                    catch { }
                }
            };

        /// <summary>各片交给结论段的数（并行两片都到齐才写结论）。</summary>
        public static readonly Dictionary<(string File, int J), object> Results = new();
        public static readonly object ResultsGate = new();
    }

    public static void Header(string file, ITestOutputHelper o, string title)
    {
        var say = Report.Writer(file, -1, o);
        lock (Report.ResultsGate)
        {
            if (Report.Results.ContainsKey((file, -1))) return;
            Report.Results[(file, -1)] = true;
        }
        say($"{title}（Opus 5）　开跑 {DateTime.Now:yyyy-MM-dd HH:mm}");
        say("工作点：第二批 B2 设计（R48_第二批保温扫描_B2_2026-09-14.txt）片0 1072 A／管根 1139.95 °C，片1 1737 A／管根 1120.76 °C；单片管侧固定、等温电流场、夹持定温。");
        say("列：抽热 = QFromTubeW；舌区发热 = QGenW（整片焦耳热，列名沿用 AB 文件）；夹持带走 = QToClampW；其余 = 抽热 − 夹持带走 + 舌区发热（= 自由格散热 + 残差）；残差 = EnergyResidualW。单位 W、°C。");
        say("判读与口径见 Pt_Optimize.Tests/R48ClampFaceRecipeTests.cs 类注释（跑前写死）。");
    }

    // ══ (a) 收敛阶 ═══════════════════════════════════════════════════════════════
    public sealed record ConvResult(int J, Dictionary<double, Row> Face, Dictionary<double, Row> Cent);

    public static ConvResult RunConvergence(int caseIdx, ITestOutputHelper o)
    {
        Header(ConvFile, o, "R48 压接面上定温：整面口径收敛阶");
        var (J, iA, tRoot) = Cases[caseIdx];
        var say = Report.Writer(ConvFile, J, o);
        var (d, p) = B2();
        var lc = d.BuildCase(p, checkRamp: false);
        var g = lc.FlangePlates[J];
        double clampLen = lc.Base.BusbarClampLengthMm;
        say("");
        say($"── 片{J}　{iA:0} A　管根 {tRoot:0.00} °C　压接长 {clampLen:0.0} mm　夹持温度 {lc.ClampTempC[J]:0} °C　舌片厚 {d.TongueThickMm[J]:0.00} mm");
        var face = new Dictionary<double, Row>();
        var cent = new Dictionary<double, Row>();
        // 片0 形心口径对得上 AB 文件整面行（工单转述，三位小数）—— 设置核对，对不上就停
        var abRec = new Dictionary<double, double> { [1.0] = 21.967, [0.5] = 22.034, [0.25] = 22.134 };
        foreach (double h in new[] { 1.0, 0.5, 0.25, 0.125 })
        {
            if (h >= 0.25)
            {
                var mC = FlangeMesher.Build(g, 0, h, h, 1e6, clampLen, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: false);
                cent[h] = Solve(lc, J, iA, tRoot, mC, $"片{J} 形心 h={h}");
                say(Fmt($"形心 h={h}", cent[h]));
                if (J == 0)
                    Assert.True(Math.Abs(cent[h].QTube - abRec[h]) <= 0.0005 + 1e-9,
                        $"片0 形心口径 h={h} 抽热 {cent[h].QTube:0.0000} 对不上 AB 文件 {abRec[h]:0.000} —— 设置与 AB 不同，后面的比较不作数");
            }
            var mF = FlangeMesher.Build(g, 0, h, h, 1e6, clampLen, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
            face[h] = Solve(lc, J, iA, tRoot, mF, $"片{J} 面上 h={h}");
            say(Fmt($"面上 h={h}", face[h]) + (cent.TryGetValue(h, out var c) ? $"　面上−形心 抽热 {face[h].QTube - c.QTube:+0.0000;-0.0000;+0.0000}" : ""));
        }

        say("");
        say("   比值 r(h₁,h₂,h₃) = (Q(h₃) − Q(h₂)) ÷ (Q(h₂) − Q(h₁))；步长 = Q(h₂) − Q(h₁)");
        var qs = new (string Name, Func<Row, double> F)[]
        {
            ("抽热", r => r.QTube), ("舌区发热", r => r.QGen), ("夹持带走", r => r.QClamp), ("其余", r => r.Rest), ("舌区峰", r => r.TTabMax), ("盘峰", r => r.TDiscMax),
        };
        foreach (var (name, F) in qs)
        {
            double f1 = F(face[1.0]), f05 = F(face[0.5]), f025 = F(face[0.25]), f0125 = F(face[0.125]);
            double c1 = F(cent[1.0]), c05 = F(cent[0.5]), c025 = F(cent[0.25]);
            say($"   {name,-6} 面上 步长 {f05 - f1,9:+0.0000;-0.0000;+0.0000} → {f025 - f05,9:+0.0000;-0.0000;+0.0000} → {f0125 - f025,9:+0.0000;-0.0000;+0.0000}"
              + $"　比 r(1,0.5,0.25) {Ratio(f1, f05, f025),7:0.000}　r(0.5,0.25,0.125) {Ratio(f05, f025, f0125),7:0.000}"
              + $"　｜形心 步长 {c05 - c1,9:+0.0000;-0.0000;+0.0000} → {c025 - c05,9:+0.0000;-0.0000;+0.0000}　比 r(1,0.5,0.25) {Ratio(c1, c05, c025),7:0.000}");
        }

        // 判读一：舌区发热
        double rGen = Ratio(face[0.5].QGen, face[0.25].QGen, face[0.125].QGen);
        string genVerdict = rGen <= 0.6 ? "≤ 0.6 ⇒ 只是没进渐近区"
                          : rGen >= 0.65 ? "≥ 0.65 ⇒ **有次线性来源**，报出来"
                          : "落在 0.6～0.65 之间 ⇒ 两条判读都不下结论，照实报";
        say("");
        say($"   ★ 判读一（舌区发热）：面上 r(0.5,0.25,0.125) = {rGen:0.000} {genVerdict}");
        // 判读二：夹持带走
        // ★ 2026-09-15 Opus 5 复审修（审查意见 minor「比值为负照打 ✓」）：面上比值为负、或分母不大于能量残差 ⇒ 比值不可判，不打 ✓、不计入比值条（见类注释）。
        double rF = Ratio(face[1.0].QClamp, face[0.5].QClamp, face[0.25].QClamp);
        double rC = Ratio(cent[1.0].QClamp, cent[0.5].QClamp, cent[0.25].QClamp);
        double stepF = Math.Abs(face[0.25].QClamp - face[0.5].QClamp), stepC = Math.Abs(cent[0.25].QClamp - cent[0.5].QClamp);
        double denomF = Math.Abs(face[0.5].QClamp - face[1.0].QClamp);
        double[] hsF = { 1.0, 0.5, 0.25, 0.125 };
        double residF = hsF.Max(h => Math.Abs(face[h].Resid));
        var why = new List<string>();
        if (double.IsNaN(rF)) why.Add("分母为 0");
        else
        {
            if (rF < 0) why.Add("比值为负（步长正负来回跳）");
            if (denomF <= residF) why.Add($"分母 |步长| {denomF:0.0000} W 不大于面上四档能量残差最大绝对值 {residF:0.0000} W");
        }
        bool rFJudgeable = why.Count == 0;
        bool byRatio = rFJudgeable && rF <= rC - 0.10, byStep = stepF <= 0.5 * stepC;
        bool literal = !double.IsNaN(rF) && rF <= rC - 0.10;
        double stepFMax = hsF.Skip(1).Select((h, k) => Math.Abs(face[h].QClamp - face[hsF[k]].QClamp)).Max();
        double stepCMin = Math.Min(Math.Abs(cent[0.5].QClamp - cent[1.0].QClamp), stepC);
        say($"   ★ 判读二（夹持带走）：同一三档 h = 1／0.5／0.25 比值 形心 {rC:0.000}（AB 转述 0.47）、面上 {rF:0.000}"
          + (rFJudgeable ? "" : $" —— **比值不可判**（{string.Join("；", why)}）")
          + $"；0.5→0.25 步长 面上 {stepF:0.0000} W、形心 {stepC:0.0000} W");
        say($"     ⇒ 比值条：{(rFJudgeable ? (byRatio ? "面上比值小 0.10 以上 ✓" : "面上比值没小 0.10") : "不可判，不计")}；步长条：{(byStep ? "面上步长不到形心的一半 ✓" : "面上步长没到形心的一半")}"
          + $" ⇒ {(byRatio || byStep ? "面上定温生效" : "**实现可能没生效**")}");
        say($"     原判据按字面（面上比值 ≤ 形心比值 − 0.10）：{(literal ? (rFJudgeable ? "成立" : "算术上成立，但比值不可判、没有意义") : "**不成立**")}。结论靠步长：形心口径两步 {Math.Abs(cent[0.5].QClamp - cent[1.0].QClamp):0.0000} → {stepC:0.0000} W，"
          + $"面上口径三步最大 {stepFMax:0.0000} W（形心最小步长是它的 {stepCMin / stepFMax:0} 倍），面上步长已到热场能量残差量级（四档最大 {residF:0.0000} W）");
        Assert.True(byRatio || byStep, $"片{J}：夹持带走的比值（面上 {rF:0.000} vs 形心 {rC:0.000}，可判 {rFJudgeable}）与步长（{stepF:0.0000} vs {stepC:0.0000} W）都没明显变小 ⇒ 面上定温可能没生效");

        // 跑后补充（2026-09-15 Opus 5 复审修，不进判读）：热侧判据量（舌区峰、盘峰）有没有收敛阶
        foreach (var (name, F) in new (string, Func<Row, double>)[] { ("舌区峰", r => r.TTabMax), ("盘峰", r => r.TDiscMax) })
        {
            double[] v = hsF.Select(h => F(face[h])).ToArray();
            double[] st = { v[1] - v[0], v[2] - v[1], v[3] - v[2] };
            double r1 = Ratio(v[0], v[1], v[2]), r2 = Ratio(v[1], v[2], v[3]);
            bool noOrder = st[0] * st[1] < 0 || st[1] * st[2] < 0 || !(Math.Abs(r1) < 1) || !(Math.Abs(r2) < 1);
            double half = (v.Skip(1).Max() - v.Skip(1).Min()) * 0.5;
            say($"   跑后补充（不进判读）{name}：面上步长 {st[0]:+0.000;-0.000;+0.000} → {st[1]:+0.000;-0.000;+0.000} → {st[2]:+0.000;-0.000;+0.000} K，比 {r1:0.00}／{r2:0.00}"
              + (noOrder ? $" ⇒ 步长换号或比值不小于 1：逐格取最大值带来的位置抖动，**没有收敛阶**；末三档极差半幅 {half:0.000} K（热侧判据的离散误差按这个抖动计，不能按抽热的比值外推）"
                         : $" ⇒ 单调且比值小于 1；末三档极差半幅 {half:0.000} K"));
        }
        return new ConvResult(J, face, cent);
    }

    // ══ (b) 细带去留 ═══════════════════════════════════════════════════════════════
    public sealed record BandResult(int J, Dictionary<(string, double), Row> Q);
    public static readonly string[] BandGroups = { "均匀", "分级不带细带", "分级+自相似细带" };

    public static BandResult RunBand(int caseIdx, ITestOutputHelper o)
    {
        Header(BandFile, o, "R48 压接面上定温：压接细带去留（整面接触 + 面上定温）");
        var (J, iA, tRoot) = Cases[caseIdx];
        var say = Report.Writer(BandFile, J, o);
        var (d, p) = B2();
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var Q = new Dictionary<(string, double), Row>();
        say("");
        say($"── 片{J}　{iA:0} A　管根 {tRoot:0.00} °C　分级网格细区半径 {radius:0.0} mm（MeshVerify.RequiredMeshFor）");
        foreach (double h in new[] { 1.0, 0.5, 0.25 })
        {
            foreach (string grp in BandGroups)
            {
                var lc = d.BuildCase(p, checkRamp: false);
                ShellMesh m;
                if (grp == "均匀")
                {
                    var g = lc.FlangePlates[J];
                    m = FlangeMesher.Build(g, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
                }
                else
                {
                    MeshAdapt.RefineWholeMesh(lc, h, radius);
                    var g = lc.FlangePlates[J];
                    m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm,
                                           clampBandMm: grp == "分级不带细带" ? 0 : 3.0 * lc.MeshFineMm, clampFullFace: true, clampFaceDirichlet: true);
                }
                Q[(grp, h)] = Solve(lc, J, iA, tRoot, m, $"片{J} {grp} h={h}");
                string diff = grp == "均匀" ? "" : $"　− 均匀：抽热 {Q[(grp, h)].QTube - Q[("均匀", h)].QTube:+0.0000;-0.0000;+0.0000} W　舌区峰 {Q[(grp, h)].TTabMax - Q[("均匀", h)].TTabMax:+0.000;-0.000;+0.000} K　盘峰 {Q[(grp, h)].TDiscMax - Q[("均匀", h)].TDiscMax:+0.000;-0.000;+0.000} K";
                say(Fmt($"{grp} h={h}", Q[(grp, h)]) + diff);
            }
        }
        // 跑后补充（2026-09-15 Opus 5，不进判读）：导航网格上同样三组 —— 均匀取 h = 2（= 导航 hFine），分级取 BuildCase 的生产网格参数
        {
            var lcN = d.BuildCase(p, checkRamp: false);
            var gN = lcN.FlangePlates[J];
            double hN = lcN.MeshFineMm;
            var uN = Solve(lcN, J, iA, tRoot, FlangeMesher.Build(gN, 0, hN, hN, 1e6, lcN.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true), $"片{J} 导航 均匀");
            var nN = Solve(lcN, J, iA, tRoot, FlangeMesher.Build(gN, 0, lcN.MeshFineMm, lcN.MeshCoarseMm, lcN.MeshFineRadiusMm, lcN.Base.BusbarClampLengthMm, lcN.MeshInnerMm, lcN.MeshInnerRadiusMm,
                                                               clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true), $"片{J} 导航 分级不带细带");
            var bN = Solve(lcN, J, iA, tRoot, FlangeMesher.Build(gN, 0, lcN.MeshFineMm, lcN.MeshCoarseMm, lcN.MeshFineRadiusMm, lcN.Base.BusbarClampLengthMm, lcN.MeshInnerMm, lcN.MeshInnerRadiusMm,
                                                               clampBandMm: 3.0 * lcN.MeshFineMm, clampFullFace: true, clampFaceDirichlet: true), $"片{J} 导航 分级+细带");
            say($"   跑后补充（不进判读）导航网格 hFine {hN:0.###}、远场 {lcN.MeshCoarseMm:0.###}、细区半径 {lcN.MeshFineRadiusMm:0.#}：");
            say(Fmt($"均匀 h={hN:0.###}", uN));
            say(Fmt("导航 不带细带", nN) + $"　− 均匀：抽热 {nN.QTube - uN.QTube:+0.0000;-0.0000;+0.0000} W　舌区峰 {nN.TTabMax - uN.TTabMax:+0.000;-0.000;+0.000} K　盘峰 {nN.TDiscMax - uN.TDiscMax:+0.000;-0.000;+0.000} K");
            say(Fmt("导航 带细带", bN) + $"　− 均匀：抽热 {bN.QTube - uN.QTube:+0.0000;-0.0000;+0.0000} W　舌区峰 {bN.TTabMax - uN.TTabMax:+0.000;-0.000;+0.000} K　盘峰 {bN.TDiscMax - uN.TDiscMax:+0.000;-0.000;+0.000} K");
        }
        double[] hs = { 1.0, 0.5, 0.25 };
        foreach (string grp in BandGroups)
            say($"   {grp,-10} 抽热 {string.Join(" / ", hs.Select(h => Q[(grp, h)].QTube.ToString("+0.0000;-0.0000;+0.0000")))} W　比 {Ratio(Q[(grp, 1.0)].QTube, Q[(grp, 0.5)].QTube, Q[(grp, 0.25)].QTube):0.000}");
        bool noBandOk = hs.All(h => Math.Abs(Q[("分级不带细带", h)].QTube - Q[("均匀", h)].QTube) <= 0.05);
        say($"   片{J}：「分级不带细带 − 均匀」抽热 {string.Join(" / ", hs.Select(h => (Q[("分级不带细带", h)].QTube - Q[("均匀", h)].QTube).ToString("+0.0000;-0.0000;+0.0000")))} W"
          + (noBandOk ? "　三档都 ≤ 0.05 ✓" : "　**有档 > 0.05**")
          + $"；「分级+自相似细带 − 均匀」{string.Join(" / ", hs.Select(h => (Q[("分级+自相似细带", h)].QTube - Q[("均匀", h)].QTube).ToString("+0.0000;-0.0000;+0.0000")))} W");
        return new BandResult(J, Q);
    }

    public static void BandVerdictIfComplete(ITestOutputHelper o)
    {
        BandResult?[] all;
        lock (Report.ResultsGate)
        {
            all = Cases.Select(c => Report.Results.TryGetValue((BandFile, c.J), out var r) ? r as BandResult : null).ToArray();
            if (all.Any(r => r is null) || Report.Results.ContainsKey((BandFile, 99))) return;
            Report.Results[(BandFile, 99)] = true;
        }
        var say = Report.Writer(BandFile, 99, o);
        double[] hs = { 1.0, 0.5, 0.25 };
        double worst = all.SelectMany(r => hs.Select(h => Math.Abs(r!.Q[("分级不带细带", h)].QTube - r.Q[("均匀", h)].QTube))).Max();
        say("");
        say($"★ 结论（跑完 {DateTime.Now:yyyy-MM-dd HH:mm}）：两片三档「分级不带细带 − 均匀」抽热最大偏差 {worst:0.0000} W（门槛 0.05 W）");
        say(worst <= 0.05
            ? "  ⇒ 整面接触 + 面上定温下不需要压接细带：生产缺省改为不带（省格子），依据写进 FlangeMesher.ClampBandPerHFine 与 BuildFromField 配方声明。"
            : "  ⇒ 保留压接细带，按整面口径重写配方 ④ 的依据（这份文件）。");
    }

    // ══ (c) 锥形舌细带去留（2026-09-15 Opus 5 复审修）═══════════════════════════════════
    public const string TaperFile = "R48_压接面上定温_锥形舌细带去留_2026-09-15.txt";
    public static readonly double[] TaperHs = { 1.0, 0.5 };
    public static readonly double[] TaperHalfWidths = { 15.0, 10.0 };
    public sealed record TaperResult(int J, Dictionary<(double W, string Grp, double H), Row> Q);

    public static TaperResult RunTaperBand(int caseIdx, ITestOutputHelper o)
    {
        Header(TaperFile, o, "R48 压接面上定温：锥形舌的压接细带去留（整面接触 + 面上定温）");
        var (J, iA, tRoot) = Cases[caseIdx];
        var say = Report.Writer(TaperFile, J, o);
        var all = new Dictionary<(double W, string Grp, double H), Row>();
        foreach (double w in TaperHalfWidths)
        {
            var q = RunTaperBandOne(J, iA, tRoot, w, say);
            foreach (var kv in q) all[(w, kv.Key.Item1, kv.Key.Item2)] = kv.Value;
        }
        return new TaperResult(J, all);
    }

    private static Dictionary<(string, double), Row> RunTaperBandOne(int J, double iA, double tRoot, double halfWidthMm, Action<string> say)
    {
        var (d, p) = B2(taper: true, tabHalfWidthMm: halfWidthMm);
        Assert.True(d.TabTaper);
        var (fineMm, radius) = MeshVerify.RequiredMeshFor(d);
        var lc0 = d.BuildCase(p, checkRamp: false);
        var g0 = lc0.FlangePlates[J];
        Assert.False(g0.TabParallel, $"片{J}：锥形设计出来的板不是锥形舌");
        var (xt, wt) = g0.Tangent();
        double taperDeg = Math.Atan2(wt - g0.TabEndHalfWidthMm, xt - g0.TabEndXMm) * 180.0 / Math.PI;
        double clampLen = lc0.Base.BusbarClampLengthMm;
        say("");
        say($"── 片{J}　{iA:0} A　管根 {tRoot:0.00} °C　盘半径 {g0.DiscRadiusMm:0.##}　舌尖 x {g0.TabTipXMm:0.##}、舌端 x {g0.TabEndXMm:0.##} 半宽 {g0.TabEndHalfWidthMm:0.##}；切点 x {xt:0.##} 半宽 {wt:0.##}"
          + $" ⇒ 锥角 {taperDeg:0.00}°；压接内边 x {g0.TabTipXMm + clampLen:0.##} 处自由侧夹角 θ = {90 + taperDeg:0.00}°，角点奇点指数 π/(2θ) = {90.0 / (90 + taperDeg):0.000}");
        say($"   舌片厚 {d.TongueThickMm[J]:0.00} mm；分级网格（MeshVerify.RequiredMeshFor）起步 {fineMm:0.###} mm、细区半径 {radius:0.0} mm");
        Assert.True(taperDeg > 0.5, $"片{J}：锥角只有 {taperDeg:0.00}°，本探针量不到「锥形」");
        var Q = new Dictionary<(string, double), Row>();
        foreach (double h in TaperHs)
        {
            foreach (string grp in BandGroups)
            {
                var lc = d.BuildCase(p, checkRamp: false);
                ShellMesh m;
                if (grp == "均匀")
                {
                    var g = lc.FlangePlates[J];
                    m = FlangeMesher.Build(g, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
                }
                else
                {
                    MeshAdapt.RefineWholeMesh(lc, h, radius);
                    var g = lc.FlangePlates[J];
                    m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm,
                                           clampBandMm: grp == "分级不带细带" ? 0 : 3.0 * lc.MeshFineMm, clampFullFace: true, clampFaceDirichlet: true);
                }
                Q[(grp, h)] = Solve(lc, J, iA, tRoot, m, $"锥形 片{J} {grp} h={h}");
                string diff = grp == "均匀" ? "" : $"　− 均匀：抽热 {Q[(grp, h)].QTube - Q[("均匀", h)].QTube:+0.0000;-0.0000;+0.0000} W　舌区峰 {Q[(grp, h)].TTabMax - Q[("均匀", h)].TTabMax:+0.000;-0.000;+0.000} K　盘峰 {Q[(grp, h)].TDiscMax - Q[("均匀", h)].TDiscMax:+0.000;-0.000;+0.000} K";
                say(Fmt($"{grp} h={h}", Q[(grp, h)]) + diff);
            }
        }
        bool ok = TaperHs.All(h => Math.Abs(Q[("分级不带细带", h)].QTube - Q[("均匀", h)].QTube) <= 0.05);
        say($"   片{J} 舌端半宽 {halfWidthMm:0.#}：「分级不带细带 − 均匀」抽热 {string.Join(" / ", TaperHs.Select(h => (Q[("分级不带细带", h)].QTube - Q[("均匀", h)].QTube).ToString("+0.0000;-0.0000;+0.0000")))} W"
          + (ok ? "　两档都 ≤ 0.05 ✓" : "　**有档 > 0.05**")
          + $"；「分级+细带 − 均匀」{string.Join(" / ", TaperHs.Select(h => (Q[("分级+自相似细带", h)].QTube - Q[("均匀", h)].QTube).ToString("+0.0000;-0.0000;+0.0000")))} W");
        return Q;
    }

    public static void TaperVerdictIfComplete(ITestOutputHelper o)
    {
        TaperResult?[] all;
        lock (Report.ResultsGate)
        {
            all = Cases.Select(c => Report.Results.TryGetValue((TaperFile, c.J), out var r) ? r as TaperResult : null).ToArray();
            if (all.Any(r => r is null) || Report.Results.ContainsKey((TaperFile, 99))) return;
            Report.Results[(TaperFile, 99)] = true;
        }
        var say = Report.Writer(TaperFile, 99, o);
        double worst = all.SelectMany(r => TaperHalfWidths.SelectMany(w => TaperHs.Select(h => Math.Abs(r!.Q[(w, "分级不带细带", h)].QTube - r.Q[(w, "均匀", h)].QTube)))).Max();
        say("");
        say($"★ 结论（跑完 {DateTime.Now:yyyy-MM-dd HH:mm}）：锥形舌两片 × 舌端半宽 {string.Join("／", TaperHalfWidths)} mm × 两档「分级不带细带 − 均匀」抽热最大偏差 {worst:0.0000} W（门槛 0.05 W，沿用 (b)）");
        say(worst <= 0.05
            ? "  ⇒ 不铺细带的依据对这两个锥角也成立：生产缺省（不铺）不变；配方 ④ 的依据写明覆盖平行边与这两个锥角。"
            : "  ⇒ **锥形舌要恢复压接细带**：生产缺省需按舌形分支（本探针不改生产，另报）。");
        // 跑后补充（2026-09-15 Opus 5，04:55 那次跑完之后加的，不是跑前判读）：跑前判读给的补救是「恢复细带」，要看同一次跑里铺了细带差距收没收小
        var keys = all.SelectMany(r => TaperHalfWidths.SelectMany(w => TaperHs.Select(h => (r: r!, w, h)))).ToArray();
        double worstBand = keys.Max(k => Math.Abs(k.r.Q[(k.w, "分级+自相似细带", k.h)].QTube - k.r.Q[(k.w, "均匀", k.h)].QTube));
        int bandWorse = keys.Count(k => Math.Abs(k.r.Q[(k.w, "分级+自相似细带", k.h)].QTube - k.r.Q[(k.w, "均匀", k.h)].QTube)
                                       > Math.Abs(k.r.Q[(k.w, "分级不带细带", k.h)].QTube - k.r.Q[(k.w, "均匀", k.h)].QTube));
        int bandOk = keys.Count(k => Math.Abs(k.r.Q[(k.w, "分级+自相似细带", k.h)].QTube - k.r.Q[(k.w, "均匀", k.h)].QTube) <= 0.05);
        say($"  跑后补充（不是跑前判读）：同一次跑「分级+细带 − 均匀」抽热最大偏差 {worstBand:0.0000} W；{keys.Length} 行里铺细带后离均匀更远的 {bandWorse} 行、落进 0.05 W 的 {bandOk} 行");
        if (worst > 0.05)
            say(bandOk == keys.Length
                ? "    ⇒ 铺细带能把差距收进门槛，上面的补救成立。"
                : "    ⇒ 铺细带**没有**把差距收进门槛 —— 跑前判读给的补救（恢复细带）不被这组数支持：差距不在压接角点，在锥形舌的分级网格本身（远场粗格里的斜边，推测，未验）；生产缺省暂不改，另开待办。");
    }

    // ══ (d) 剩下的正偏差在哪（2026-09-15 Opus 5 复审修；只定位，不改生产）═══════════════════
    public const string LocFile = "R48_压接面上定温_正偏差定位_2026-09-15.txt";
    public sealed record LocResult(int J, double FEdgeGen, double FDiscGen, double FEdgeTube, double FDiscTube, string Class);

    public static LocResult RunLocalize(int caseIdx, ITestOutputHelper o)
    {
        Header(LocFile, o, "R48 压接面上定温：剩下的正偏差在哪（中心带加密定位）");
        var (J, iA, tRoot) = Cases[caseIdx];
        var say = Report.Writer(LocFile, J, o);
        var (d, p) = B2();
        var lc = d.BuildCase(p, checkRamp: false);
        var g = lc.FlangePlates[J];
        double clampLen = lc.Base.BusbarClampLengthMm;
        double raster = FlangeMesher.RasterStepFor(0.25);
        var field = FlangeMesher.Rasterize(g, raster, 0.0);
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        ShellMesh M(double h, double hIn, double rIn)
            => FlangeMesher.BuildFromField(field, g.HoleRadiusMm, 0, h, h, 1e6, clampLen, hIn, rIn, twoTabs: g.TwoTabs, xAnchors: xa, zAnchors: za,
                                           clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true);
        double rEdge = g.HoleRadiusMm + 3.0, rDisc = g.DiscRadiusMm;
        say("");
        say($"── 片{J}　{iA:0} A　管根 {tRoot:0.00} °C　孔半径 {g.HoleRadiusMm:0.###} mm、盘半径 {rDisc:0.###} mm、舌端半宽 {g.TabEndHalfWidthMm:0.###} mm；共用栅格步 {raster:0.####} mm");
        var bOwn = Solve(lc, J, iA, tRoot, FlangeMesher.Build(g, 0, 1.0, 1.0, 1e6, clampLen, clampBandMm: 0, clampFullFace: true, clampFaceDirichlet: true), $"片{J} 均匀 h=1 自带栅格");
        say(Fmt("均匀 h=1 自带栅格", bOwn) + $"　（栅格步 {FlangeMesher.RasterStepFor(1.0):0.####} mm，只看栅格本身差多少）");
        var u1 = Solve(lc, J, iA, tRoot, M(1.0, 0, 0), $"片{J} 均匀 h=1");
        say(Fmt("均匀 h=1", u1) + $"　− 自带栅格：抽热 {u1.QTube - bOwn.QTube:+0.0000;-0.0000;+0.0000} W　舌区发热 {u1.QGen - bOwn.QGen:+0.0000;-0.0000;+0.0000} W");
        var u025 = Solve(lc, J, iA, tRoot, M(0.25, 0, 0), $"片{J} 均匀 h=0.25");
        say(Fmt("均匀 h=0.25", u025));
        var iEdge = Solve(lc, J, iA, tRoot, M(1.0, 0.25, rEdge), $"片{J} 孔边一圈");
        say(Fmt($"h=1+孔边 R{rEdge:0.#}", iEdge));
        var iDisc = Solve(lc, J, iA, tRoot, M(1.0, 0.25, rDisc), $"片{J} 整个圆盘");
        say(Fmt($"h=1+圆盘 R{rDisc:0.#}", iDisc));

        double Fr(Func<Row, double> q, Row x) { double den = q(u025) - q(u1); return Math.Abs(den) > 1e-12 ? (q(x) - q(u1)) / den : double.NaN; }
        say("   份额 f = (Q_加密 − Q_h1) ÷ (Q_h0.25 − Q_h1)");
        foreach (var (name, q) in new (string, Func<Row, double>)[] { ("舌区发热", x => x.QGen), ("抽热", x => x.QTube), ("其余", x => x.Rest) })
            say($"   {name,-6} h=1→0.25 总变化 {q(u025) - q(u1):+0.0000;-0.0000;+0.0000} W　孔边一圈 f {Fr(q, iEdge):0.000}　整个圆盘 f {Fr(q, iDisc):0.000}");
        double fEg = Fr(x => x.QGen, iEdge), fDg = Fr(x => x.QGen, iDisc);
        // 2026-09-15 Opus 5：跑前那套「孔边／圆盘／舌片」分类作废（前提在单舌张量积网格上不成立，见类注释 (d)），不再印分类，只印跑后读法
        double tabFrac = g.TabEndHalfWidthMm > 0 ? Math.Min(1.0, rEdge / g.TabEndHalfWidthMm) : double.NaN;
        say($"   片{J} 跑后读法（不是跑前判读）：R {rEdge:0.#} 的带在 z 向盖住舌宽的 {tabFrac:P0}、两档 R 只差 {rDisc - rEdge:0.#} mm ⇒ 分不开孔边与圆盘、也分不开 z 向分辨率；"
          + $"舌片 x 向分辨率（含压接内边）那部分份额 1 − f：舌区发热 {1 - fEg:+0.000;-0.000;+0.000}、抽热 {1 - Fr(x => x.QTube, iEdge):+0.000;-0.000;+0.000}");
        return new LocResult(J, fEg, fDg, Fr(x => x.QTube, iEdge), Fr(x => x.QTube, iDisc), "作废");
    }

    public static void LocVerdictIfComplete(ITestOutputHelper o)
    {
        LocResult?[] all;
        lock (Report.ResultsGate)
        {
            all = Cases.Select(c => Report.Results.TryGetValue((LocFile, c.J), out var r) ? r as LocResult : null).ToArray();
            if (all.Any(r => r is null) || Report.Results.ContainsKey((LocFile, 99))) return;
            Report.Results[(LocFile, 99)] = true;
        }
        var say = Report.Writer(LocFile, 99, o);
        say("");
        say($"★ 跑完 {DateTime.Now:yyyy-MM-dd HH:mm}：跑前判读作废（前提「中心带加密 ≈ 只加密孔边」在单舌张量积网格上不成立，见类注释 (d)），不下「孔边／圆盘／舌片」的结论。");
        say("  跑后读法：" + string.Join("；", all.Select(r => $"片{r!.J} 舌片 x 向分辨率（含压接内边）那部分份额 舌区发热 {1 - r.FEdgeGen:+0.000;-0.000;+0.000}、抽热 {1 - r.FEdgeTube:+0.000;-0.000;+0.000}")));
        say("  ⇒ h = 1 → 0.25 的变化几乎全部来自「|x| ≤ R 的 x 向分辨率（环、孔边）」与「|z| ≤ R 的 z 向分辨率（横跨舌宽与环）」，舌片 x 向与压接内边不贡献；");
        say("    电流场管孔在格上钉 V = 0 这个候选落在前者里，本探针既没证实也没排除 —— 要定论得在 ShellCurrent 里做管孔面上定电位的条带快门与收敛阶（单开一路）。");
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_收敛阶_片0
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_收敛阶_片0(ITestOutputHelper o) { _out = o; }
    [Fact] public void 面上定温收敛阶_片0() => R48ClampFaceRecipeTests.RunConvergence(0, _out);
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_收敛阶_片1
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_收敛阶_片1(ITestOutputHelper o) { _out = o; }
    [Fact] public void 面上定温收敛阶_片1() => R48ClampFaceRecipeTests.RunConvergence(1, _out);
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_细带去留_片0
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_细带去留_片0(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 面上定温细带去留_片0()
    {
        var r = R48ClampFaceRecipeTests.RunBand(0, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.BandFile, 0)] = r;
        R48ClampFaceRecipeTests.BandVerdictIfComplete(_out);
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_锥形舌细带去留_片0
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_锥形舌细带去留_片0(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 锥形舌细带去留_片0()
    {
        var r = R48ClampFaceRecipeTests.RunTaperBand(0, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.TaperFile, 0)] = r;
        R48ClampFaceRecipeTests.TaperVerdictIfComplete(_out);
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_锥形舌细带去留_片1
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_锥形舌细带去留_片1(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 锥形舌细带去留_片1()
    {
        var r = R48ClampFaceRecipeTests.RunTaperBand(1, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.TaperFile, 1)] = r;
        R48ClampFaceRecipeTests.TaperVerdictIfComplete(_out);
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_正偏差定位_片0
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_正偏差定位_片0(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 正偏差定位_片0()
    {
        var r = R48ClampFaceRecipeTests.RunLocalize(0, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.LocFile, 0)] = r;
        R48ClampFaceRecipeTests.LocVerdictIfComplete(_out);
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_正偏差定位_片1
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_正偏差定位_片1(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 正偏差定位_片1()
    {
        var r = R48ClampFaceRecipeTests.RunLocalize(1, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.LocFile, 1)] = r;
        R48ClampFaceRecipeTests.LocVerdictIfComplete(_out);
    }
}

[Trait("速度", "慢")]
public class R48ClampFaceRecipe_细带去留_片1
{
    private readonly ITestOutputHelper _out;
    public R48ClampFaceRecipe_细带去留_片1(ITestOutputHelper o) { _out = o; }
    [Fact]
    public void 面上定温细带去留_片1()
    {
        var r = R48ClampFaceRecipeTests.RunBand(1, _out);
        lock (R48ClampFaceRecipeTests.Report.ResultsGate) R48ClampFaceRecipeTests.Report.Results[(R48ClampFaceRecipeTests.BandFile, 1)] = r;
        R48ClampFaceRecipeTests.BandVerdictIfComplete(_out);
    }
}
