using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 G3 空管态耦合续跑（2026-09-15，Opus 5 写；规格出自常驻数值把关人第十四轮与物理把关人第十一轮）：
/// **空管到温稳态（全线 1150 °C、管腔轴向辐射进模型）上，外层耦合容差 0.25 K 加默认放大 25 够不够；两档管腔辐射系数下结论差多少。**
///
/// ══ 设计（同第二批 B2，与 R48EmptyTubeStateTests 同一组输入）
///   W08；板厚 0.73/1.26/1.26/0.73；环倍率全 1；管保温 7.5；逐片圆盘保温 10.5/3.5/5.0/10.0；逐片舌保温 5.0/3.5/5.0/10.5；
///   舌片厚 SizeTongues；共用片抽热各半（SplitSharedFlangeDraw = true）；BuildCase(emptyTube: true)（控温点全线取升温目标）；
///   0.5 mm 整线细化、细区半径 MeshVerify.RequiredMeshFor。管腔辐射系数两档 0.023／0.056 W·m/K（1150 °C 时 kA，SegmentSolver.CavityRadKA1150Low／High），各一个 Fact，可两个进程并行。
///
/// ══ 做法
///   阶段一：容差 0.25 K、最多 4000 轮解到收敛（WarmStart 回写）；CoupleTrace 每轮记步长、真残差、剩余估计、放大系数、ω、各段两端管根。
///   阶段二：同一 LineCase 热启动，容差 1e-6 K（实际关掉）、最多 300 轮续跑；CoupleTrace 每轮记两片共用片的「管根低于热偶读数」（冷侧）、
///     片1 抽热、步长、真残差、剩余估计、放大系数、ω、Anderson 报告。续跑若提前逐位停住（步长与真残差都到 0），停住的那一点就是不动点。
///   两阶段的 res.Notes 全文、逐片最高温与散热表上限（ShellThermal.LossTableHiC，铂熔点）的差都打出来。
///   机读行 DATA|… 给「两档并列」读：四片热侧／冷侧读数、管根较热端／较冷端、热偶读数基准、抽热、最高温；各段电流、两端管根、无法兰基线、段内管腔 kA、热衰减长度。
///
/// ══ 跑前写死的判读（2026-09-15 Opus 5 写于起跑之前）
///   出处：「漂移 ≤ 0.1 K 且实测放大 ≤ 25 ⇒ 可用；实测放大 &gt; 25 ⇒ 默认值偏小」是数值把关人第十四轮；「0.1～0.5 记下不下结论、≥ 0.5 估法低估」沿用第十一轮
///   （R48CoupleContinuationTests）；「实测放大」怎么量（两种估法、够格轮的 0.02 K 门槛、中位数）是 Opus 5 跑前定的，依据写在下面。
///   记号：阶段一收敛那一轮的值记 x₀；续跑末 min(50, 续跑轮数) 轮的均值记 x∞（续跑提前逐位停住时就是停住的值）。
///   · 漂移 D = 两片共用片「管根低于热偶读数」|x∞ − x₀| 的较大者（K）。
///   · 实测放大 A = 阶段一各轮「真误差 ÷ 步长」的中位数：真误差 e_k = 各段两端管根 |T_k − T∞| 的最大值（T∞ 取续跑末段均值），步长 δ_k 即 LineRunner 的步长；
///     只取 k ≥ 3、δ_k ≥ 0.02 K 且 e_k ≥ 0.02 K 的轮（0.02 K = 内层一维解容差 0.005 K 的 4 倍，低于它的比值是在读精度地板）；
///     够格的轮少于 3 轮 ⇒ 改用阶段一 δ_k ≥ 0.02 K 各轮 ln δ 对轮号的最小二乘斜率 r，A = r/(1−r)（至少 5 轮、0 &lt; r &lt; 1）；再不够 ⇒ 放大判不了。
///     另印最大值与两种估法各自的数，只报数。
///   · D ≤ 0.1 K 且 A ≤ 25 ⇒ 「容差 0.25 K 加默认放大 25 在空管态可用」。
///   · A &gt; 25 ⇒ 「默认放大 25 在空管态偏小」—— 交接 open issue：比值记录区间放宽到 (0.5, 0.999)，或空管态用实测值 × 安全系数。
///   · A ≤ 25 而 D &gt; 0.1 K ⇒ 0.1～0.5 K 记下、不下结论；≥ 0.5 K ⇒ 剩余误差估计在空管态低估，要修估法。
///   · A 判不了 ⇒ 只按 D 说 0.25 K 在本算例够不够，并写明「默认放大够不够没量到」。
/// ══ 两档并列（第三个 Fact，只读两份输出）：同一份编译产物（核心、测试 MVID 相同）才比，差 = 高档 − 低档；只报数，给物理把关人判空管结论对管腔辐射系数敏不敏感。
/// ══ 输出：deliverable\R48_空管续跑_kA0.023_2026-09-15.txt、…_kA0.056_2026-09-15.txt、…_两档并列_2026-09-15.txt（已存在就拒绝写，不覆盖）。
///   R48 G3 审查后（2026-09-15，Opus 5）：文件名改带运行时刻 yyyy-MM-dd_HHmmss（与 R48EmptyTubeStateTests 同一做法）—— 原来只带日期，
///   文件一旦存在，三个慢 Fact 以后任何一次重跑（含换一天）都因「拒绝覆盖」而红，成了永久红。04:07 那三份证据保持原名不动。
///   两档并列改为读两档各自最新、且核心与测试 MVID 相同的一对（按文件写入时刻），读哪两份印在输出第一行。
///   Anderson 报告两阶段每轮都接在行尾（原来阶段二只在 k ≤ 3 或 k % 25 = 0 时印、阶段一不印 —— 「Anderson 让残差不单调」这类判断没法复核）。
///
/// ══ 附注：不改判，只加报数（R48 G3 第二轮审查后，2026-09-15 Opus 5）
///   撤回的东西：第一轮审查后在这里写过一段「判读更正」，把「逐轮最大」「LineRunner 两阶段比值记录的最大放大」也列为放大的触发条件，
///     多出一类跑前规则里没有的结论「默认放大 25 够不够没有量准」，还丢了「空管态用实测值 × 安全系数」这个备选。那段是看过 04:07 两次跑的数之后才写的
///     （它引的「电流二分容差地板」就是从那两次的数里发现的），违反「不许事后挪门槛」；而且自相矛盾 ——
///     它自己的前提（估法一收的轮不在渐近段、逐轮「真误差 ÷ 步长」≠ r/(1−r)）同样说明「逐轮最大」不是放大的量法。已撤回。
///   ⇒ **判读照上面「跑前写死的判读」一字不改**：A = 估法一中位数（够格轮不足 3 才用估法二）；结论只有规则里那几类。
///   下面几项只报数、不进判读；要不要据此换判据，交数值把关人裁定，裁定之前不用：
///     ① 估法一按构造量不到停机区（这条不依赖任何数）：停机要 δ × 25 ≤ 0.25 K 即 δ ≤ 0.01 K，估法一只收 δ ≥ 0.02 K 的轮，两个区间不重叠；
///        Anderson 下这些轮不在渐近段，逐轮「真误差 ÷ 步长」不等于 r/(1−r)（LineRunner 外层耦合「真残差」那段注释）。
///     ② 估法二（ln δ 斜率）、逐轮最大、LineRunner 两阶段比值记录的最大放大，各印一个数。
///     ③ 外层步长地板与 LineRunner 比值记录：续跑步长分布照印。LineRunner 的比值记录 rEst 只在相邻两轮步长比落在 (0.9, 0.999) 时更新（LineRunner.Run 外层）；
///        步长停在一层地板上时，两个几乎相等的地板步长也会让比值落进这个区间、把 rEst 推高 ⇒ 那时印出的放大不是收缩率。
///        地板来源是推断：反算电流二分容差（SegmentSolver.FindCurrent，xTol = 0.05 A）乘管根对电流的斜率约 1.2 K/A ≈ 0.04 K
///        （依据是 kA0.023 那次 HC3 电流 1065.08 → 1065.11 A 与末端管根 1171.066 → 1171.103 °C 同步变；没做单独隔离的实验）。Anderson 下放大本来就量不准。
///     ④ 停机那一刻的两个直接比值：真误差 ÷ 末轮步长、真误差 ÷ 剩余估计。
///   若数值把关人要按规则补救：前提是先把外层步长地板压到 0.01 K（= 0.25 ÷ 25）以下 —— 否则放宽比值记录区间到 (0.5, 0.999) 会让比值被地板抖动推高、停机判据过不了；
///     另一个备选是空管态用实测值 × 安全系数。
///   ⚠ 输出文件的判读行：带时刻 _064409 的两份（第一轮审查后跑的）末行「默认放大 25 够不够没有量准」按本附注**作废**，数照用；
///     04:07 起跑、不带时刻的两份末行判读与跑前规则一致，但出自更早的编译产物（DATA 行除 MVID 外与 _064409 两份逐位相同）。
///     05:03 那份两档并列读的是 04:07 那一对（旧编译产物 18c8cb2b），数照用；以同一编译产物重跑的一对与两档并列见带时刻的新文件。
/// </summary>
[Trait("速度", "慢")]
public class R48EmptyTubeContinuationTests
{
    private readonly ITestOutputHelper _out;
    public R48EmptyTubeContinuationTests(ITestOutputHelper o) { _out = o; }

    // R48 G3 审查后（2026-09-15，Opus 5）：文件名带运行时刻（原固定 _2026-09-15.txt ⇒ 重跑永久红）
    private static string Prefix(double ka) => $"R48_空管续跑_kA{ka.ToString("0.000", CultureInfo.InvariantCulture)}_";
    private static string FileFor(double ka) => Path.Combine(Root(), "deliverable", $"{Prefix(ka)}{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");
    private static string CoreMvid => typeof(LineRunner).Assembly.ManifestModule.ModuleVersionId.ToString();
    private static string TestMvid => typeof(R48EmptyTubeContinuationTests).Assembly.ManifestModule.ModuleVersionId.ToString();

    [Fact] public void 空管续跑_管腔辐射低档() => Run(SegmentSolver.CavityRadKA1150Low);
    [Fact] public void 空管续跑_管腔辐射高档() => Run(SegmentSolver.CavityRadKA1150High);
    /// <summary>冒烟：同一段代码走开箱网格、续跑至多 5 轮，输出写系统临时目录（不进 deliverable、不读结论）—— 长跑之前先确认整条打印／判读／机读行不崩。</summary>
    [Fact] public void 空管续跑_冒烟_开箱网格() => Run(SegmentSolver.CavityRadKA1150Low, smoke: true);

    private sealed class Round
    {
        public int K; public double Delta, ResK, Remain, Amp, Omega;
        public double[] RootA = Array.Empty<double>(), RootB = Array.Empty<double>();
        public double Cold1, Cold2, Q1;
    }

    private void Run(double ka, bool smoke = false)
    {
        string file = smoke ? Path.Combine(Path.GetTempPath(), $"R48_空管续跑_冒烟_{DateTime.Now:yyyyMMdd_HHmmss}.txt") : FileFor(ka);
        Assert.False(File.Exists(file), $"{file} 已存在 —— 拒绝覆盖");
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        var inv = CultureInfo.InvariantCulture;
        var p = new DesignInputs { SplitSharedFlangeDraw = true, TubeCavityRadKA1150WmPerK = ka };
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.TubeInsulMm = 7.5;
        d.DiscInsulMm = new[] { 10.5, 3.5, 5.0, 10.0 };
        d.TabInsulMm = new[] { 5.0, 3.5, 5.0, 10.5 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(p, checkRamp: false, emptyTube: true);
        if (!smoke) MeshAdapt.RefineWholeMesh(lc, 0.5, radius);
        lc.CoupleTolK = 0.25;
        lc.CoupleMaxRounds = Math.Max(lc.CoupleMaxRounds, 4000);
        int n = lc.SegmentCount;
        if (smoke) Say($"⚠ 冒烟：开箱网格（细 {lc.MeshFineMm:0.###} mm）、续跑至多 5 轮 —— 只验代码路径，数不读");
        Say($"R48 G3 空管态耦合续跑（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　管腔轴向辐射系数 {lc.Base.TubeCavityRadKA1150WmPerK:0.000} W·m/K（1150 °C 时 kA）　设计同第二批 B2　0.5 mm 细区半径 {radius:0}");
        Say($"编译产物 核心 MVID {CoreMvid}　测试 MVID {TestMvid}");
        Say($"工况位 EmptyTube = {lc.EmptyTube}（来源 {lc.EmptyTubeSetpointFrom}）　控温点 {string.Join("/", lc.SetpointC.Select(v => v.ToString("0.#")))} °C　Anderson {lc.UseAnderson}（深度 {lc.AndersonDepth}）　默认放大 {LineCase.FixedPointAmp}");
        Say($"管保温 {lc.Base.Layer1.ThicknessMm:0.0}　板件圆盘保温 {string.Join("/", lc.FlangePlates.Select(g => g.DiscInsulThickMm.ToString("0.0")))}　板件舌保温 {string.Join("/", lc.FlangePlates.Select(g => g.TabInsulThickMm.ToString("0.0")))}　舌片厚 {string.Join("/", d.TongueThickMm.Select(v => v.ToString("0.00")))} mm");
        Assert.True(lc.EmptyTube, "算例不是空管 —— 接线没接上");

        var sw = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
        var tick = new Progress<string>(m =>
        {
            if (sw.Elapsed.TotalSeconds - last < 180) return;
            last = sw.Elapsed.TotalSeconds;
            Say($"     · [已跑 {sw.Elapsed.TotalSeconds:0} s] {m}");
        });

        // ── 阶段一
        var ph1 = new List<Round>();
        string Cold(LineResult res, int j) => ThermocoupleBasis.At(res, j).ColdK.ToString("+0.0000;-0.0000");
        lc.CoupleTrace = (k, res, delta, resK, remain, amp, omega, aa) =>
        {
            ph1.Add(new Round
            {
                K = k, Delta = delta, ResK = resK, Remain = remain, Amp = amp, Omega = omega,
                RootA = res.Segments.Select(s => s.TRootAC).ToArray(), RootB = res.Segments.Select(s => s.TRootBC).ToArray()
            });
            Say($"   阶段一 轮{k,4}　δ {delta:0.0000}　真残差 {resK:0.0000}　剩余估计 {remain:0.000}（×{amp:0.0}）　ω {omega:0.00}　"
              + $"共用片冷侧 {Cold(res, 1)}/{Cold(res, 2)} K　片1 抽热 {res.Flanges[1].QFromTubeW:+0.0000;-0.0000} W　{sw.Elapsed.TotalMinutes:0.0} 分"
              + (aa.Length > 0 ? $"　{aa}" : ""));   // R48 G3 审查后：每轮都印 Anderson
        };
        var r1 = LineRunner.Run(lc, tick, default);
        Say($"阶段一（容差 0.25 K）：{sw.Elapsed.TotalMinutes:0.0} 分　Ok {r1.Ok}　收敛 {r1.Converged}　剩余估计 {r1.CoupleRemainK:0.000} K　外层 {ph1.Count} 轮");
        Say("   Notes：");
        foreach (var s in r1.Notes) Say("     " + s);
        Assert.True(r1.Ok && r1.Converged, "0.25 K 那次没解到收敛 —— 本档不读：" + r1.Message);
        Assert.True(lc.WarmStart.Length >= n, "收敛后 WarmStart 没回写，续跑无从热启动");
        var j1 = Enumerable.Range(0, n + 1).Select(j => ThermocoupleBasis.At(r1, j)).ToArray();
        double[] rootA0 = r1.Segments.Select(s => s.TRootAC).ToArray(), rootB0 = r1.Segments.Select(s => s.TRootBC).ToArray();
        double delta0 = ph1.Count > 0 ? ph1[^1].Delta : double.NaN;
        PrintState("阶段一收敛点", r1, Say);

        // ── 阶段二
        lc.CoupleTolK = 1e-6;
        lc.CoupleMaxRounds = smoke ? 5 : 300;
        var ph2 = new List<Round>();
        lc.CoupleTrace = (k, res, delta, resK, remain, amp, omega, aa) =>
        {
            var rd = new Round
            {
                K = k, Delta = delta, ResK = resK, Remain = remain, Amp = amp, Omega = omega,
                RootA = res.Segments.Select(s => s.TRootAC).ToArray(), RootB = res.Segments.Select(s => s.TRootBC).ToArray(),
                Cold1 = ThermocoupleBasis.At(res, 1).ColdK, Cold2 = ThermocoupleBasis.At(res, 2).ColdK, Q1 = res.Flanges[1].QFromTubeW
            };
            ph2.Add(rd);
            Say($"   续跑 轮{k,3}　共用片冷侧 {rd.Cold1:+0.0000;-0.0000}/{rd.Cold2:+0.0000;-0.0000} K　片1 抽热 {rd.Q1:+0.0000;-0.0000} W　"
              + $"δ {delta:0.0000}　真残差 {resK:0.0000}　剩余估计 {remain:0.000}（×{amp:0.0}）　ω {omega:0.00}　{sw.Elapsed.TotalMinutes:0.0} 分"
              + (aa.Length > 0 ? $"　{aa}" : ""));   // R48 G3 审查后：每轮都印（原只 k ≤ 3 或 k % 25 = 0）
        };
        var r2 = LineRunner.Run(lc, tick, default);
        Say($"阶段二（热启动续跑 {ph2.Count} 轮）：{sw.Elapsed.TotalMinutes:0.0} 分　Ok {r2.Ok}　收敛 {r2.Converged}（容差 1e-6；逐位停住才会报收敛）");
        Say("   Notes：");
        foreach (var s in r2.Notes) Say("     " + s);
        Assert.True(r2.Ok, "续跑解不出来：" + r2.Message);
        Assert.True(ph2.Count >= 1, "续跑一轮都没跑");
        PrintState("续跑末轮", r2, Say);

        // ── 判读（规则见档头，跑前写死）
        var tail = ph2.Skip(Math.Max(0, ph2.Count - 50)).ToArray();
        double c1Inf = tail.Average(t => t.Cold1), c2Inf = tail.Average(t => t.Cold2), q1Inf = tail.Average(t => t.Q1);
        double[] tAInf = Enumerable.Range(0, n).Select(i => tail.Average(t => t.RootA[i])).ToArray();
        double[] tBInf = Enumerable.Range(0, n).Select(i => tail.Average(t => t.RootB[i])).ToArray();
        double drift = Math.Max(Math.Abs(c1Inf - j1[1].ColdK), Math.Abs(c2Inf - j1[2].ColdK));
        Say("");
        Say($"★ 续跑末 {tail.Length} 轮均值：共用片冷侧 {c1Inf:+0.0000;-0.0000}（漂 {c1Inf - j1[1].ColdK:+0.0000;-0.0000}）/{c2Inf:+0.0000;-0.0000}（漂 {c2Inf - j1[2].ColdK:+0.0000;-0.0000}）K"
          + $"　片1 抽热 {q1Inf:+0.0000;-0.0000}（漂 {q1Inf - r1.Flanges[1].QFromTubeW:+0.0000;-0.0000} W）　漂移 D = {drift:0.0000} K");
        Say($"   末段极差：共用片冷侧 {tail.Max(t => t.Cold1) - tail.Min(t => t.Cold1):0.0000}/{tail.Max(t => t.Cold2) - tail.Min(t => t.Cold2):0.0000} K（仍在走就不是渐近值）");
        double ErrAt(Round rd) => Enumerable.Range(0, n).Max(i => Math.Max(Math.Abs(rd.RootA[i] - tAInf[i]), Math.Abs(rd.RootB[i] - tBInf[i])));
        double e0 = Enumerable.Range(0, n).Max(i => Math.Max(Math.Abs(rootA0[i] - tAInf[i]), Math.Abs(rootB0[i] - tBInf[i])));
        Say($"   阶段一收敛点到续跑末段的真误差（各段两端管根最大）{e0:0.0000} K　阶段一末轮步长 {delta0:0.0000} K　阶段一报的剩余估计 {r1.CoupleRemainK:0.000} K");
        var ratios = ph1.Where(rd => rd.K >= 3 && rd.Delta >= 0.02).Select(rd => (rd.K, e: ErrAt(rd), rd.Delta)).Where(t => t.e >= 0.02).Select(t => (t.K, a: t.e / t.Delta)).ToList();
        double aMed = double.NaN, aMax = double.NaN;
        if (ratios.Count > 0)
        {
            var sorted = ratios.Select(t => t.a).OrderBy(v => v).ToArray();
            aMed = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
            aMax = sorted[^1];
        }
        Say($"   真误差÷步长（阶段一 k ≥ 3、δ ≥ 0.02 K、e ≥ 0.02 K，共 {ratios.Count} 轮）：{string.Join("　", ratios.Select(t => $"轮{t.K} {t.a:0.0}"))}");
        var fitPts = ph1.Where(rd => rd.K >= 3 && rd.Delta >= 0.02).Select(rd => (x: (double)rd.K, y: Math.Log(rd.Delta))).ToList();
        double rFit = double.NaN, aFit = double.NaN;
        if (fitPts.Count >= 5)
        {
            double mx = fitPts.Average(t => t.x), my = fitPts.Average(t => t.y);
            double sxy = fitPts.Sum(t => (t.x - mx) * (t.y - my)), sxx = fitPts.Sum(t => (t.x - mx) * (t.x - mx));
            rFit = Math.Exp(sxy / sxx);
            if (rFit > 0 && rFit < 1) aFit = rFit / (1 - rFit);
        }
        Say($"   估法一 中位数 {Fmt(aMed)}（最大 {Fmt(aMax)}）　估法二 ln δ 斜率（{fitPts.Count} 轮）r = {Fmt(rFit)} ⇒ r/(1−r) = {Fmt(aFit)}");
        double aMeas = ratios.Count >= 3 ? aMed : aFit;
        string aFrom = ratios.Count >= 3 ? "估法一" : double.IsFinite(aFit) ? "估法二（估法一够格的轮不足 3）" : "判不了";
        Say($"   实测放大 A = {Fmt(aMeas)}（取{aFrom}）　默认放大 {LineCase.FixedPointAmp}");
        // 只报数、不进判读（2026-09-15 Opus 5，冒烟之后、正式起跑之前加）：Anderson 步让迭代不是线性定常的，逐轮「真误差 ÷ 步长」只在渐近段才等于 r/(1−r)
        //   （LineRunner 外层耦合「真残差」那段注释写的就是这一点）⇒ 另给停机那一刻的两个直接比值，供数值把关人对照。
        //   R48 G3 审查后（2026-09-15，Opus 5）：末轮步长低于内层容差时也印出比值（原来印「—」），旁注步长低于内层容差。
        Say($"   （只报数）停机那一刻：真误差 ÷ 末轮步长 = {(delta0 > 0 ? Fmt(e0 / delta0) : "—")}{(delta0 < 0.005 ? $"（末轮步长 {delta0:0.0000} K 低于内层容差 0.005 K，比值只作佐证）" : "")}　"
          + $"真误差 ÷ 阶段一报的剩余估计 = {(r1.CoupleRemainK > 0 ? Fmt(e0 / r1.CoupleRemainK) : "—")}（≤ 1 = 估计盖住了真误差）");
        // R48 G3 审查后（2026-09-15，Opus 5）：LineRunner 自己的比值记录（CoupleTrace 的 amp；没量到 r 时是默认 25）与续跑步长分布 —— 只报数，档头「附注」②③（第二轮审查后改口：原写「判读更正 ② 要读」，那段已撤回，这几个数不进判读）
        double lrMax1 = ph1.Count > 0 ? ph1.Max(t => t.Amp) : double.NaN, lrMax2 = ph2.Max(t => t.Amp);
        int lrOver1 = ph1.Count(t => t.Amp > LineCase.FixedPointAmp), lrOver2 = ph2.Count(t => t.Amp > LineCase.FixedPointAmp);
        var ampHist = ph2.GroupBy(t => Math.Round(t.Amp, 1)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"×{g.Key:0.0} {g.Count()} 轮");
        Say($"   LineRunner 比值记录的放大：阶段一最大 {Fmt(lrMax1)}（> 25 的 {lrOver1} 轮）　续跑最大 {Fmt(lrMax2)}（> 25 的 {lrOver2}／{ph2.Count} 轮）　续跑各值轮数 {string.Join("　", ampHist)}");
        Say($"   续跑步长分布（停机要 δ ≤ 0.25/25 = 0.01 K）：δ < 0.005 K {ph2.Count(t => t.Delta < 0.005)} 轮　0.005～0.01 {ph2.Count(t => t.Delta >= 0.005 && t.Delta < 0.01)} 轮　"
          + $"0.01～0.03 {ph2.Count(t => t.Delta >= 0.01 && t.Delta < 0.03)} 轮　≥ 0.03 K {ph2.Count(t => t.Delta >= 0.03)} 轮　最大 {ph2.Max(t => t.Delta):0.0000} K");
        // 判读：照档头「跑前写死的判读」（第二轮审查后恢复成 04:07 起跑那一版的分支与文字，2026-09-15 Opus 5；
        //   第一轮审查后加的「没有量准」一类已撤回，理由见档头「附注」）
        string verdict;
        if (!double.IsFinite(aMeas))
            verdict = drift <= 0.1
                ? "⇒ 漂移 ≤ 0.1 K：容差 0.25 K 在本算例上够用；**实测放大判不了**（够格的轮不够），默认放大 25 在空管态够不够没量到。"
                : $"⇒ 漂移 {drift:0.000} K > 0.1 K，且实测放大判不了 —— 记下，交数值把关人。";
        else if (aMeas > LineCase.FixedPointAmp)
            verdict = $"⇒ **实测放大 {aMeas:0.0} > 25：默认放大在空管态偏小** —— 提 open issue：比值记录区间放宽到 (0.5, 0.999)，或空管态用实测值 × 安全系数。（漂移 {drift:0.000} K）";
        else if (drift <= 0.1)
            verdict = "⇒ 漂移 ≤ 0.1 K 且实测放大 ≤ 25：容差 0.25 K 加默认放大 25 在空管态可用。";
        else if (drift < 0.5)
            verdict = $"⇒ 实测放大 ≤ 25，但漂移 {drift:0.000} K 在 0.1～0.5 K 之间：记下，不下结论。";
        else
            verdict = $"⇒ 实测放大 ≤ 25，但漂移 {drift:0.000} K ≥ 0.5 K：**剩余误差估计在空管态低估**，要修估法。";
        Say($"★ 判读（跑前规则；实测放大 A {(double.IsFinite(aMeas) ? $"取{aFrom} {Fmt(aMeas)}" : "判不了")}，漂移 D {drift:0.0000} K）" + verdict);
        // 附注（只报数，不改判；档头「附注」①～④）：各估法与比值记录的数都在上面几行，这里只把读它们时要知道的三件事写在判读旁边
        Say($"   附注（只报数，不改判，交数值把关人）：① 估法一只收 δ ≥ 0.02 K 的轮，停机区是 δ ≤ 0.01 K，两区间不重叠，被统计的都是停机之前的轮；"
          + $"② 其他量法：估法二 {Fmt(aFit)}、逐轮最大 {Fmt(aMax)}、LineRunner 比值记录阶段一最大 {Fmt(lrMax1)}／续跑最大 {Fmt(lrMax2)} —— Anderson 下逐轮比值不是收缩率，这几个数不当判据；"
          + $"③ 续跑 δ ≥ 0.03 K 的有 {ph2.Count(t => t.Delta >= 0.03)}／{ph2.Count} 轮；外层步长可能有约 0.04 K 的地板（推断：反算电流二分容差 0.05 A × 管根对电流约 1.2 K/A，未单独隔离），"
          + "比值记录在地板上会被两个几乎相等的地板步长推高，那时的放大不是收缩率；若要放宽比值记录区间，先把地板压到 0.01 K 以下，另一备选是空管态用实测值 × 安全系数。");

        // 机读行（两档并列读它）：DATA|ka|coreMvid|testMvid|drift|aMeas|conv1|remain1|rounds1|rounds2|plates…|segs…
        //   plate：hotK,coldK,rootHot,rootCold,ref,q,tmax（续跑末轮）；seg：I,TRootA,TRootB,BaseA,BaseB,cavKA,decayMm（续跑末轮）
        string Num(double v) => v.ToString("R", inv);
        var plates = Enumerable.Range(0, n + 1).Select(j =>
        {
            var t = ThermocoupleBasis.At(r2, j); var f = r2.Flanges[j];
            return string.Join(",", new[] { t.HotK, t.ColdK, t.RootHotC, t.RootColdC, t.RefC, f.QFromTubeW, f.TMaxC }.Select(Num));
        });
        var segs = r2.Segments.Select(s => string.Join(",", new[] { s.CurrentA, s.TRootAC, s.TRootBC, s.BaseTRootAC, s.BaseTRootBC, s.CavityRadKAWmPerK, s.DecayLengthMm }.Select(Num)));
        Say("DATA|" + Num(ka) + "|" + CoreMvid + "|" + TestMvid + "|" + Num(drift) + "|" + Num(aMeas) + "|" + (r1.Converged ? "1" : "0") + "|" + Num(r1.CoupleRemainK)
          + "|" + ph1.Count + "|" + ph2.Count + "|" + string.Join(";", plates) + "|" + string.Join(";", segs));
    }

    /// <summary>逐片热侧／冷侧读数、管根、基准、抽热、最高温与散热表上限的差；逐段电流、管根、基线、管腔 kA、热衰减长度。</summary>
    private static void PrintState(string title, LineResult r, Action<string> Say)
    {
        Say($"   【{title}】逐片（最热铂高出热偶读数／管根低于热偶读数／管根较热端与较冷端／热偶读数基准／抽热／最高温与散热表上限 {ShellThermal.LossTableHiC:0.0} °C 的差）：");
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var t = ThermocoupleBasis.At(r, j); var f = r.Flanges[j];
            Say($"     {t.Name}　热侧 {F(t.HotK)} K（{t.HottestWhat}）　冷侧 {F(t.ColdK)} K（{t.RootColdWhere}）　管根 {t.RootHotC:0.00}/{t.RootColdC:0.00}　基准 {t.RefC:0.00}　"
              + $"抽热 {f.QFromTubeW:+0.000;-0.000} W　最高温 {f.TMaxC:0.00} °C（离上限 {ShellThermal.LossTableHiC - f.TMaxC:0.0} K）　场收敛 {f.FieldsConverged}");
        }
        Say("   逐段（电流／管根两端／无法兰基线两端／段内管腔 kA／热衰减长度）：");
        foreach (var s in r.Segments)
            Say($"     {s.Name}　设定 {s.SetpointC:0.#}　电流 {s.CurrentA:0.00} A　管根 {s.TRootAC:0.000}/{s.TRootBC:0.000}　基线 {s.BaseTRootAC:0.000}/{s.BaseTRootBC:0.000}　管腔 kA {s.CavityRadKAWmPerK:0.0000} W·m/K　热衰减长度 {s.DecayLengthMm:0.0} mm");
        static string F(double v) => double.IsNaN(v) ? "判不了" : v.ToString("+0.000;-0.000");
    }

    private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("0.000") : "—";

    [Fact]
    public void 两档并列_读结果()
    {
        string outFile = Path.Combine(Root(), "deliverable", $"R48_空管续跑_两档并列_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");   // R48 G3 审查后：带运行时刻
        Assert.False(File.Exists(outFile), $"{outFile} 已存在 —— 拒绝覆盖");
        var inv = CultureInfo.InvariantCulture;
        // R48 G3 审查后（2026-09-15，Opus 5）：两档各自按文件写入时刻从新到旧，取最新的、核心与测试 MVID 相同的一对（原来读固定文件名）
        (string file, string[] data)[] Cands(double ka) => new DirectoryInfo(Path.Combine(Root(), "deliverable"))
            .GetFiles(Prefix(ka) + "*.txt").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (name: f.Name, data: File.ReadAllLines(f.FullName).LastOrDefault(x => x.StartsWith("DATA|", StringComparison.Ordinal))?.Split('|')))
            .Where(t => t.data is not null).Select(t => (t.name, t.data!)).ToArray();
        var loC = Cands(SegmentSolver.CavityRadKA1150Low); var hiC = Cands(SegmentSolver.CavityRadKA1150High);
        Assert.True(loC.Length > 0 && hiC.Length > 0, "两档输出没齐（先跑两个续跑 Fact）");
        var pair = loC.SelectMany(l => hiC.Where(h => h.data[2] == l.data[2] && h.data[3] == l.data[3]).Take(1).Select(h => (l, h))).Take(1).ToArray();
        Assert.True(pair.Length == 1, "找不到核心与测试 MVID 都相同的一对两档输出 —— 不比");
        var (loF, hiF) = pair[0];
        var lo = loF.data; var hi = hiF.data;
        double[][] Rows(string s) => s.Split(';').Select(r => r.Split(',').Select(v => double.Parse(v, inv)).ToArray()).ToArray();
        var pl = Rows(lo[10]); var ph = Rows(hi[10]); var sl = Rows(lo[11]); var sh = Rows(hi[11]);
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say($"R48 G3 空管续跑 两档并列（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　出处 {loF.file} 与 {hiF.file}（续跑末轮），同一份编译产物（核心 MVID {lo[2]}，测试 MVID {lo[3]}）");
        // 第二轮审查后（2026-09-15，Opus 5）：判读口径恢复跑前规则，这一行跟着改（原引「判读更正」，已撤回）
        Say("判读口径：R48EmptyTubeContinuationTests 档头「跑前写死的判读」—— 两档各自的判读看各自输出的「★ 判读」行，附注只报数、不改判；"
          + "带时刻 _064409 的两份末行「默认放大 25 够不够没有量准」已作废（数照用）。");
        Say($"管腔辐射系数 {lo[1]} → {hi[1]} W·m/K；差 = 高档 − 低档。两档的漂移 {double.Parse(lo[4], inv):0.0000}/{double.Parse(hi[4], inv):0.0000} K、实测放大 {lo[5]}/{hi[5]}");
        string[] names = pl.Length == 4 ? new[] { "入口", "HC1|HC2", "HC2|HC3", "出口" } : Enumerable.Range(0, pl.Length).Select(j => $"片{j}").ToArray();
        string[] cols = { "最热铂高出热偶读数 K", "管根低于热偶读数 K", "管根较热端 °C", "管根较冷端 °C", "热偶读数基准 °C", "抽热 W", "最高温 °C" };
        for (int c = 0; c < cols.Length; c++)
            Say($"   {cols[c]}：" + string.Join("　", Enumerable.Range(0, pl.Length).Select(j => $"{names[j]} {pl[j][c]:0.000} → {ph[j][c]:0.000}（{ph[j][c] - pl[j][c]:+0.000;-0.000}）")));
        string[] scol = { "电流 A", "管根首端 °C", "管根末端 °C", "基线首端 °C", "基线末端 °C", "段内管腔 kA W·m/K", "热衰减长度 mm" };
        for (int c = 0; c < scol.Length; c++)
            Say($"   {scol[c]}：" + string.Join("　", Enumerable.Range(0, sl.Length).Select(i => $"HC{i + 1} {sl[i][c]:0.0000} → {sh[i][c]:0.0000}（{sh[i][c] - sl[i][c]:+0.0000;-0.0000}）")));
        Say("   只报数：限值 5 K 的两条判据在两档下各自过不过、差多少，由物理把关人判空管结论对管腔辐射系数敏不敏感。");
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
