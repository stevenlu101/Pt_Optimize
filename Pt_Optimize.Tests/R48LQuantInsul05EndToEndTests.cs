using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　端到端 **图纸格 0.5**：W08 与 W06 —— 2026-09-17，Opus 5。
//
//  ══ 本轮跑什么（一个进程、一份代码、一份设计，不拼两份仪器输出）
//
//    ① 端到端求解：Solver.Solve，第一遍导航网格定位 + **第二遍细网格上重新二分求根**
//       （细区尺寸/半径取 MeshVerify.RequiredMeshFor，与「◆ 加密复算」同一个来源），
//       舌保温的图纸格 = **0.5 mm**（= 包法每层，用户 2026-09-14／2026-09-17）。
//    ② 三关：① 升温全程　② 带玻璃稳态　③ 空管到温稳态 —— 都跑在第二遍那张网格上。
//    ③ 铂重（出自 ② 那一次）。
//    ④ **可行窗口**：对解出来的设计，逐片把舌保温在解值附近 ±1.0 mm 内按 **0.1 mm** 扫
//       （其余片与其余旋钮一位不动，网格 = 判决那张），量每片「可行窗口 [下, 上]、宽 x mm」
//       与「落进的 0.5 档」。窗口比每层 0.5 mm 还窄的片**点名** —— 那是「判据窗口窄于制造精度」
//       的证据，判据不许因此放宽。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//
//    · 0.5 格子下解不出来／判不了 ⇒ **如实印**，不许改门槛、不许退回 0.1 格重跑一遍充数。
//    · 铂重变化如实报，不设门槛、不做解释性加工。
//    · 解出来的舌保温**必须**落在 {裸舌 0.30} ∪ {0.5 的倍数} 上 —— 这一条是**断言**（生产代码的病，
//      不是结果），违反即测试红。
//    · 可行窗口宽 &lt; 0.5 mm 的片一律点名；窗口里能选的层数 ≤ 1 的片另点一次。
//    · 与 0.1 格那一跑并列时，**两档不是同一次运行**：0.1 那一档的数出自 09:30 那一跑的文件
//      （文件名写在表头），而且那一跑在「内级半径上界耦合」修好之前。并列表只作对照，
//      不当同一次仪器输出。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LQuantInsul05EndToEndTests
{
    private readonly ITestOutputHelper _o;
    public R48LQuantInsul05EndToEndTests(ITestOutputHelper o) { _o = o; }

    /// <summary>求解时间闸（跑前写死）：超过就切断，报「被时间闸切断」，不许装作跑完了。</summary>
    private static readonly TimeSpan SolveCap = TimeSpan.FromHours(5.0);

    /// <summary>可行窗口扫描的时间闸（跑前写死）。</summary>
    private static readonly TimeSpan ScanCap = TimeSpan.FromHours(2.0);

    /// <summary>扫描分辨率与半幅（用户 2026-09-17：±1.0 mm 内按 0.1 mm 扫）。</summary>
    private const double ScanStepMm = 0.1, ScanHalfMm = 1.0;

    /// <summary>扫描并发度：每点一条整线解，彼此独立；跑完拿解值那一点与三关的 ② 逐位对照，证明并发没有串味。</summary>
    private static readonly int ScanDop = Math.Max(1, Math.Min(5, Environment.ProcessorCount - 2));

    private static SolverOptions FineOptions(double fineMm, double radiusMm)
        => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = fineMm, FineRadiusMm = radiusMm };

    // ── 0.1 格那一跑的数（**不是本次跑的**；出处写在表头，逐项标注档别）────────────
    private sealed record Ref01(string Tag, string File, double[] Insul, double[] Thick,
                                double Mass, double Hot, double Cold, double Flux, string Gates, string Note);

    private static Ref01[] Reference(string which) => which == "W08"
        ? new[]
        {
            new Ref01("0.1 格・导航档（可交付的那一份）", "R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt",
                      new[] { 5.1, 2.3, 3.6, 10.4 }, new[] { 0.73, 1.26, 1.26, 0.73 },
                      4245, 4.529, 4.511, 0.656, "① 过　② 过　③ 过",
                      "判决网格 = 导航 2.0 mm（那一跑**没做第二遍**）；同一份设计改判在细网格上三关也过（那一跑的 D 段）"),
            new Ref01("0.1 格・细网格档", "R48_L_端到端_细网格_W08_本次开跑于2026-09-17_093013.txt",
                      new[] { 5.1, 2.4, 3.6, 10.4 }, new[] { 0.73, 6.00, 1.26, 0.73 },
                      4357, 6.57, 3.857, -0.06, "① 过　② 不过　③ 不过",
                      "**不是解**：第一遍第 5 轮结构性停机（内级半径 r₁ 的上界当时还是常数 10 ⇒ r₁ 35.8 > r₂ 31.8 抛异常），第二遍从未开始；这一行只是停机那一刻的陈列"),
        }
        : new[]
        {
            new Ref01("0.1 格・导航档", "R48_L_端到端_细网格_W06_本次开跑于2026-09-17_093015.txt",
                      new[] { 6.5, 3.1, 4.6, 13.3 }, new[] { 0.64, 1.10, 1.10, 0.64 },
                      3378, 4.69, 4.605, 0.572, "① 过　② 过　③ 过", "判决网格 = 导航 2.0 mm"),
            new Ref01("0.1 格・细网格档（可交付的那一份）", "R48_L_端到端_细网格_W06_本次开跑于2026-09-17_093015.txt",
                      new[] { 6.4, 3.1, 4.5, 13.1 }, new[] { 0.64, 1.10, 1.10, 0.64 },
                      3379, 4.355, 4.889, 0.592, "① 过　② 过　③ 过",
                      "第二遍（细网格 1.000 mm）跑完了，求根与判决同一张网格"),
        };

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 端到端_格子0点5(string which)
    {
        var seed = which == "W08" ? DesignSpec.W08.Clone()
                 : which == "W06" ? DesignSpec.W06.Clone()
                 : throw new ArgumentException(which);
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_端到端_格子0.5_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        // 长跑：每一段跑完就落盘，中途被切断也留得住已经跑出来的东西
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var o0 = new SolverOptions();
        double q = Solver.KnobQuantum(o0, Solver.Knob.Insul);
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed, p);
        var navCase = new LineCase();

        W($"R48 L 路　端到端 **图纸格 0.5**　输入几何「{seed.Name}」（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W($"用户 2026-09-17 原话：「求解器默认格子改成 0.5」。本轮的舌保温图纸格 = {q:0.###} mm"
          + $"（= 包法每层 {InsulationSearch.LayerMm:0.###} mm，InsulationSearch.LayerMm，全仓唯一一份层厚）。");
        W("");
        W("═══════ 本轮跑什么 ═══════");
        W($"求解：Solver.Solve —— 第一遍导航网格（细区 {navCase.MeshFineMm:0.0} mm／粗区 {navCase.MeshCoarseMm:0.0} mm／细区半径 {reqRadius:0.0} mm）定位，"
          + $"第二遍**细网格**（细区 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）上重新二分求根。");
        W("三关：① 升温全程（RampSweep）　② 带玻璃稳态（LineRunner）　③ 空管到温稳态（LineRunner）—— 都跑在第二遍那张网格上。");
        W($"可行窗口：逐片把舌保温在解值 ±{ScanHalfMm:0.0} mm 内按 {ScanStepMm:0.0} mm 扫（其余一位不动），网格 = 判决那张。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("　① 0.5 格子下解不出来／判不了 ⇒ 如实印；不许改门槛、不许退回 0.1 格。");
        W("　② 解出来的舌保温必须落在 {裸舌 0.30} ∪ {0.5 的倍数} 上 —— 这是**断言**（生产代码的病），违反即红。");
        W($"　③ 可行窗口宽 < {q:0.###} mm（每层厚）的片**点名**：判据窗口窄于制造精度。判据不许因此放宽。");
        W("　④ 窗口里能选的层数 ≤ 1 的片再点一次（现场只有一种缠法，没有余地）。");
        W("　⑤ 铂重变化如实报，不设门槛、不做解释性加工。");
        W($"　时间闸：求解 {SolveCap.TotalHours:0.#} h、窗口扫描 {ScanCap.TotalHours:0.#} h —— 超时即切断，报「被时间闸切断」，不许装作跑完了。");
        W("");

        W("═══════ 输入（内置设计只提供几何与工况；五个旋钮一律被求解器丢掉）═══════");
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，"
          + $"设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"工况：夹头 {seed.ClampTempC:0} °C，压接段长 {seed.ClampLengthMm:0.#} mm，"
          + $"设定电流密度 J {seed.JDesignAPerMm2:0.#} A/mm²（终验限值 {seed.JCheckAPerMm2:0.#}）");
        W($"旋钮盒（SolverOptions 缺省）：舌保温 [{o0.InsLoMm:0.00}（裸舌 = 0 层）, {o0.InsHiMm:0.0}]，图纸格 {q:0.###}；"
          + $"板厚上界 {o0.ThickHiMm:0.0}／图纸格 {o0.QuantThickMm:0.00}；环倍率 [{o0.RingLo:0.00}, {o0.RingHi:0.00}]／图纸格 {o0.QuantRing:0.00}；"
          + $"二分容差 {o0.BisectTolMm:0.000} mm（比图纸格细 —— 早停在格子上会多给一层）。");
        W($"网格无关口径要求：细区 {reqFine:0.000} mm，细区半径 {reqRadius:0.0} mm（MeshVerify.RequiredMeshFor(设计)）。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_格子0.5_{which}_{stamp}_进行中.log");
        var probe = new Probe(live);
        W($"进度活页（临时，非交付物；逐行带时刻与自上一行的耗时）：{live}");
        W("");
        Flush();

        // ══════════════════════════════════════════════════════════════════
        //  求解
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 求解（图纸格 0.5；两遍机制在 Solver.Solve 里）═══════");
        Flush();
        var opt = FineOptions(reqFine, reqRadius);
        var (sr, solveS, cut) = SolveWithCap(seed.Clone(), p, opt, probe, SolveCap, $"{which} 格子0.5");
        W($"求解耗时 {solveS / 60:0.0} 分钟（{solveS:0} s），场解 {(sr?.Solves ?? 0)} 次。");
        if (sr is null)
        {
            W(cut ? "★ **被跑前写死的时间闸切断** ⇒ 本轮没有解出来的设计 ⇒ 三关与窗口扫描都不跑（不许拿半成品冒充结果）。"
                  : "★ **求解器抛异常或没返回** ⇒ 本轮没有解出来的设计 ⇒ 三关与窗口扫描都不跑。");
            Finish(sb, file, totalSw, which);
            Assert.Fail($"{which}：求解没返回结果（{(cut ? "时间闸切断" : "异常")}）—— 报告已落盘：{file}");
            return;
        }
        // R48 M（2026-09-18，Fable 5.1）：判词走生产唯一那一份 Solver.VerdictOf（判不了 ≠ 不可行；「再算一次会得到同一句话」只许挂在 HitBound）
        W($"结论：{Solver.VerdictOf(sr)}　停因：{sr.StopWhy}");
        if (sr.HitBound) W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.Undetermined) W("　⚠ 判不了：" + sr.UndeterminedWhy.Replace("**", "") + "（既不当过也不当不过；再算一次不一定得到同一句话）");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        W($"终局网格：{(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm（第二遍求根与终局复核跑在判决的那张网格上）" : $"**没做第二遍** ⇒ 只在导航网格 {navCase.MeshFineMm:0.0} mm 上成立、**不可交付**")}");
        var solved = sr.Design;
        W("");

        // ── 格子门：解出来的舌保温必须落在层上（或裸舌）──
        W("── 门：解出来的舌保温落在哪儿（合法集合 = {裸舌 " + $"{o0.InsLoMm:0.00}" + "} ∪ {" + $"{q:0.###}" + " 的倍数}）");
        var offGrid = new List<string>();
        for (int j = 0; j < solved.TabInsulMm.Length; j++)
        {
            double v = solved.TabInsulMm[j];
            bool bare = Math.Abs(v - o0.InsLoMm) < 1e-9;
            double layers = v / q;
            bool onGrid = Math.Abs(layers - Math.Round(layers)) < 1e-9;
            W($"　片{j}　{v:0.000} mm　{(bare ? "裸舌（0 层）" : onGrid ? $"{Math.Round(layers):0} 层 × {q:0.###} mm" : "**既不是裸舌，也不是整数层** ⇒ 现场包不出来")}");
            if (!bare && !onGrid) offGrid.Add($"片{j} = {v:R}");
        }
        W(offGrid.Count == 0 ? "　⇒ 四片全部包得出来。" : "　⇒ **有片包不出来**：" + string.Join("、", offGrid));
        W("");
        Flush();

        // ══════════════════════════════════════════════════════════════════
        //  三关
        // ══════════════════════════════════════════════════════════════════
        var g = RunThreeGates(W, Flush, solved, p, opt, probe, $"{which} 格子0.5", navCase);
        W();
        Flush();

        // ══════════════════════════════════════════════════════════════════
        //  可行窗口
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 可行窗口：逐片扫舌保温（其余一位不动，网格 = 判决那张）═══════");
        W($"扫描：解值 ±{ScanHalfMm:0.0} mm，步长 {ScanStepMm:0.0} mm（低于裸舌下界 {o0.InsLoMm:0.00} 的点不扫，记「下界外」）。");
        W($"每点一条整线解（带玻璃稳态，与 ② 同一条路径、同一张网格）；并发 {ScanDop} 条，"
          + "跑完拿解值那一点与 ② 那一次**逐位对照**（并发有没有串味，当场验，不假设）。");
        W("可行 = 整线解出来了 ∧ 外层耦合收敛 ∧ 卡交付的判据全过（LineResult.AllOk，与交付判定同一份）。");
        W("");
        Flush();
        var scan = ScanWindows(W, Flush, solved, p, opt, probe, g.Glass, q, o0.InsLoMm);
        Flush();

        // ══════════════════════════════════════════════════════════════════
        //  并列：0.1 格（上两位的，另一次跑）vs 0.5 格（本次）
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 并列：0.1 格子（**另一次跑**，出处见表内）与 0.5 格子（本次）═══════");
        W("⚠ 两档**不是同一次运行**，也不是同一份代码：0.1 那一档出自 2026-09-17 09:30 那一跑，");
        W("　那时「内级半径 r₁ 的上界要受外级 r₂ 约束」还没修（W08 就是被它停机的），「回收判下角不许放半格」也还没改。");
        W("　⇒ 这张表是**对照**，不是同一台仪器的两次读数；把差全记在格子头上是错的。");
        W("");
        W("档\t舌保温 mm（逐片）\t板厚 mm（逐片）\t铂重 g\t最热铂高出热偶读数\t管根低于热偶读数\t管孔净流入\t三关\t备注");
        foreach (var r in Reference(which))
            W($"{r.Tag}\t{string.Join("/", r.Insul.Select(v => v.ToString("0.0")))}\t{string.Join("/", r.Thick.Select(v => v.ToString("0.00")))}\t"
              + $"{r.Mass:0}\t{r.Hot:0.###}\t{r.Cold:0.###}\t{r.Flux:0.###}\t{r.Gates}\t{r.Note}（出处 deliverable/{r.File}）");
        W($"**0.5 格・本次**\t{string.Join("/", solved.TabInsulMm.Select(v => v.ToString("0.0")))}\t"
          + $"{string.Join("/", solved.TabThickMm.Select(v => v.ToString("0.00")))}\t"
          + $"{Fmt(g.Glass?.TotalMassG ?? double.NaN, "0")}\t{Fmt(Val(g.Glass, LineResult.Key.HotOverTc), "0.###")}\t"
          + $"{Fmt(Val(g.Glass, LineResult.Key.ColdUnderTc), "0.###")}\t{Fmt(Val(g.Glass, LineResult.Key.NetFlux), "0.###")}\t"
          + $"{Three(g)}\t判决网格 {(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm" : $"导航 {navCase.MeshFineMm:0.0} mm（**没做第二遍**）")}（本次运行）");
        W("");
        W("── 三关裕度（本次，② 带玻璃稳态的交付判据逐条）");
        W("判据\t值/限值\t裕度");
        foreach (var c in (g.Glass?.Checks ?? Array.Empty<ConstraintOut>()).Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"{Criteria.Plain(c.Name)}\t{Fmt(c.Actual, "0.###")}/{c.Limit:0.###} {c.Unit}\t{Fmt(Slack(c), "+0.###;-0.###")}");
        W("");
        W("── 铂重（本次）");
        W($"管 {Fmt(g.Glass?.TubeMassG ?? double.NaN, "0")} g + 法兰 {Fmt(g.Glass?.FlangeMassG ?? double.NaN, "0")} g = 合计 {Fmt(g.Glass?.TotalMassG ?? double.NaN, "0")} g（出自 ② 那一次）");
        var refDeliver = Reference(which).FirstOrDefault(r => r.Tag.Contains("可交付"));
        double mNow = g.Glass?.TotalMassG ?? double.NaN;
        W(refDeliver is null || double.IsNaN(mNow)
          ? "与 0.1 格那一跑的铂重差：**算不出**（有一边没有可交付的数）。"
          : $"与 0.1 格那一跑「{refDeliver.Tag}」的差：{mNow - refDeliver.Mass:+0;-0} g"
            + $"（{(refDeliver.Mass > 0 ? ((mNow - refDeliver.Mass) / refDeliver.Mass * 100).ToString("+0.00;-0.00") : "—")} %）"
            + " —— 如实报；两档不是同一次跑，这个差里既有格子也有那两处生产改动。");
        W("");
        Flush();

        // ══════════════════════════════════════════════════════════════════
        //  一句话
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 一句话 ═══════");
        W(OneLine(which, sr, g, reqFine, navCase, scan, q));
        W("");
        W("═══════ 可行窗口一览（再抄一遍，便于直接引用）═══════");
        foreach (string s in scan.Lines) W(s);
        W("");
        W("═══════ 求解轨迹（Solver.Trace 全文）═══════");
        foreach (var t in sr.Trace) W(t);
        Finish(sb, file, totalSw, which);

        // ── 断言：只守「生产代码的病」与「每一步都有结果」，判据不过是**结果**不是失败 ──
        Assert.True(sr.Solves > 0, "一次场解都没跑 —— 那不是求解");
        Assert.True(offGrid.Count == 0,
            $"{which}：解出来的舌保温有片既不是裸舌也不是 {q} 的整数倍 ⇒ 现场包不出来（{string.Join("、", offGrid)}）");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  可行窗口
    // ══════════════════════════════════════════════════════════════════════

    private sealed class ScanOut
    {
        public List<string> Lines = new();
        public List<int> TooNarrow = new();      // 窗口窄于每层
        public List<int> SingleChoice = new();   // 窗口里只有一种层数可选
        public bool Cut;
        public bool ParallelChecked, ParallelSame;
    }

    private static ScanOut ScanWindows(Action<string> W, Action Flush, DesignSpec solved, DesignInputs p,
                                       SolverOptions meshOpt, Probe probe, LineResult? glassAtSolved,
                                       double layerMm, double insLoMm)
    {
        var res = new ScanOut();
        int np = solved.TabInsulMm.Length;
        using var cts = new CancellationTokenSource(ScanCap);
        var swAll = Stopwatch.StartNew();

        for (int j = 0; j < np; j++)
        {
            double v0 = solved.TabInsulMm[j];
            var deltas = new List<double>();
            for (int k = -(int)Math.Round(ScanHalfMm / ScanStepMm); k <= (int)Math.Round(ScanHalfMm / ScanStepMm); k++)
                deltas.Add(Math.Round(k * ScanStepMm, 6));
            var pts = deltas.Select(dd => Math.Round(v0 + dd, 6)).ToList();

            var ok = new bool?[pts.Count];             // null = 判不了／没扫
            var why = new string[pts.Count];
            var hot = new double[pts.Count];
            var cold = new double[pts.Count];
            var flux = new double[pts.Count];
            LineResult? atSolved = null;
            for (int i = 0; i < pts.Count; i++) { hot[i] = cold[i] = flux[i] = double.NaN; why[i] = ""; }

            var swJ = Stopwatch.StartNew();
            probe.Report($"── 窗口扫描：片{j}，{pts.Count} 点（{pts[0]:0.0}…{pts[^1]:0.0} mm），并发 {ScanDop}");
            try
            {
                Parallel.For(0, pts.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = ScanDop, CancellationToken = cts.Token }, i =>
                {
                    double v = pts[i];
                    if (v < insLoMm - 1e-9) { ok[i] = null; why[i] = "下界外（低于裸舌）"; return; }
                    var d = solved.Clone();
                    d.TabInsulMm[j] = v;
                    var lc = d.BuildCase(p);
                    Solver.ApplyCaseMesh(lc, meshOpt);
                    LineResult r;
                    try { r = LineRunner.Run(lc, null); }
                    catch (Exception ex) { ok[i] = null; why[i] = $"{ex.GetType().Name}：{ex.Message}"; return; }
                    hot[i] = Val(r, LineResult.Key.HotOverTc);
                    cold[i] = Val(r, LineResult.Key.ColdUnderTc);
                    flux[i] = Val(r, LineResult.Key.NetFlux);
                    if (!r.Ok) { ok[i] = null; why[i] = "整线解没解出来：" + r.Message; return; }
                    if (!r.Converged) { ok[i] = null; why[i] = $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）"; return; }
                    ok[i] = r.AllOk;
                    if (!r.AllOk) why[i] = string.Join("／", r.Failed.Select(Criteria.Plain));
                    if (Math.Abs(v - solved.TabInsulMm[j]) < 1e-12) atSolved = r;
                });
            }
            catch (OperationCanceledException)
            {
                res.Cut = true;
                W($"★ 片{j} 的窗口扫描**被跑前写死的时间闸切断**（已跑 {swAll.Elapsed.TotalHours:0.00} h）—— 下面这一片的窗口不完整，不许当结果读。");
            }
            swJ.Stop();
            probe.Report($"── 窗口扫描：片{j} 结束，耗时 {swJ.Elapsed.TotalMinutes:0.0} 分钟");

            // 并发有没有串味：解值那一点与三关的 ② 逐位对照（同一设计、同一网格 ⇒ 必须逐位相同）
            if (!res.ParallelChecked && atSolved is not null && glassAtSolved is not null)
            {
                res.ParallelChecked = true;
                res.ParallelSame = Val(atSolved, LineResult.Key.HotOverTc).Equals(Val(glassAtSolved, LineResult.Key.HotOverTc))
                                && Val(atSolved, LineResult.Key.ColdUnderTc).Equals(Val(glassAtSolved, LineResult.Key.ColdUnderTc))
                                && Val(atSolved, LineResult.Key.NetFlux).Equals(Val(glassAtSolved, LineResult.Key.NetFlux));
                W($"并发自检（片{j} 的解值点 vs 三关的 ②，同一设计同一网格）："
                  + (res.ParallelSame ? "**逐位相同** ⇒ 并发没有串味，下面的扫描可读。"
                                      : $"**对不上** ⇒ 并发下的数不可引用！②: {Val(glassAtSolved, LineResult.Key.HotOverTc):R}／扫描: {Val(atSolved, LineResult.Key.HotOverTc):R}"));
                W("");
            }

            // 找包含解值那一点的连通可行区间
            int c = pts.FindIndex(v => Math.Abs(v - v0) < 1e-12);
            string windowText;
            double lo = double.NaN, hi = double.NaN, width = double.NaN;
            int layersInWindow = 0;
            if (c < 0 || ok[c] != true)
            {
                windowText = ok.ElementAtOrDefault(c) is null && c >= 0
                    ? $"**解值那一点自己判不了**（{why[c]}）⇒ 窗口无从谈起"
                    : "**解值那一点自己就不过** ⇒ 这一片没有以解值为中心的可行窗口（解不在可行域里，或判决网格与求根网格不是同一张）";
            }
            else
            {
                int a = c, b = c;
                while (a - 1 >= 0 && ok[a - 1] == true) a--;
                while (b + 1 < pts.Count && ok[b + 1] == true) b++;
                lo = pts[a]; hi = pts[b]; width = hi - lo;
                bool openLo = a == 0, openHi = b == pts.Count - 1;
                layersInWindow = 0;
                for (double t = Math.Ceiling(lo / layerMm - 1e-9) * layerMm; t <= hi + 1e-9; t += layerMm)
                    if (t >= insLoMm - 1e-9) layersInWindow++;
                windowText = $"[{lo:0.0}, {hi:0.0}] mm　宽 {width:0.0} mm"
                           + (openLo || openHi ? $"（{(openLo ? "下" : "")}{(openLo && openHi ? "、" : "")}{(openHi ? "上" : "")}边界**没探到** —— 扫描范围只有 ±{ScanHalfMm:0.0} mm，真实窗口更宽）" : "")
                           + $"　窗口里可选的层数 {layersInWindow} 种";
                if (!openLo && !openHi && width < layerMm - 1e-9) res.TooNarrow.Add(j);
                if (layersInWindow <= 1) res.SingleChoice.Add(j);
            }

            double layersAt = v0 / layerMm;
            string band = Math.Abs(layersAt - Math.Round(layersAt)) < 1e-9
                        ? $"第 {Math.Round(layersAt):0} 层（{v0:0.0} mm），相邻档 {Math.Max(0, Math.Round(layersAt) - 1) * layerMm:0.0} / {(Math.Round(layersAt) + 1) * layerMm:0.0} mm"
                        : $"**不在层上**（{v0:0.0} mm）";
            string line = $"片{j}　解值 {v0:0.0} mm／{band}　可行窗口 {windowText}";
            res.Lines.Add(line);
            W(line);
            W("　逐点（值 mm｜可行？｜最热铂高出热偶读数｜管根低于热偶读数｜管孔净流入｜不过的是哪条）");
            for (int i = 0; i < pts.Count; i++)
                W($"　　{pts[i]:0.0}\t{(ok[i] is null ? "判不了" : ok[i] == true ? "过" : "不过")}\t"
                  + $"{Fmt(hot[i], "0.###")}\t{Fmt(cold[i], "0.###")}\t{Fmt(flux[i], "0.###")}\t{why[i]}"
                  + (Math.Abs(pts[i] - v0) < 1e-12 ? "\t← 解值" : ""));
            if (!double.IsNaN(width))
                W($"　判读（跑前写死）：{(width < layerMm - 1e-9 ? $"**窗口 {width:0.0} mm < 每层 {layerMm:0.###} mm —— 判据窗口窄于制造精度，点名。判据不放宽。**" : $"窗口 {width:0.0} mm ≥ 每层 {layerMm:0.###} mm ⇒ 现场有得选")}"
                  + (layersInWindow <= 1 ? "　**窗口里只有一种层数可选 —— 没有余地，点名。**" : ""));
            W("");
            Flush();
        }

        W("── 窗口小结");
        // ★ 2026-09-17 Opus 5：**「没有窄的」与「一个窗口都没量到」必须分开说**。
        //   本轮 W08／W06 在 0.5 格上都没有解，四片的解值点自己就不过 ⇒ 一个窗口都没量到，
        //   而第一版这里只会印「没有片的窗口窄于每层厚」—— 读起来像好消息。那是把事情搞混。
        int measured = res.Lines.Count(s => s.Contains("可行窗口 ["));
        W(measured == 0
          ? "　**一个窗口都没量到** —— 每一片的解值点自己就不过（这一档没有解），窄不窄无从谈起。"
          : res.TooNarrow.Count == 0
            ? $"　量到窗口的 {measured} 片里，没有一片窄于每层厚（在扫到的边界之内）。"
            : "　**窗口窄于制造精度的片**：" + string.Join("、", res.TooNarrow.Select(j => $"片{j}")) + " —— 判据不许因此放宽（用户 2026-09-17）。");
        W(res.SingleChoice.Count == 0 ? "　没有片是「只有一种层数可选」。"
                                      : "　**只有一种层数可选的片**：" + string.Join("、", res.SingleChoice.Select(j => $"片{j}")));
        if (res.Cut) W("　⚠ 扫描中途被时间闸切断，上面的窗口有片不完整。");
        if (res.ParallelChecked && !res.ParallelSame) W("　⚠ 并发自检没对上 ⇒ 本节的数不可引用。");
        W("");
        return res;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  帮手（与 R48LFineMeshEndToEndTests 同型；这里只保留本轮要用的）
    // ══════════════════════════════════════════════════════════════════════

    private sealed class GateSet
    {
        public RampSweepResult? Ramp;
        public LineResult? Glass;
        public LineResult? Empty;
    }

    private static (SolverResult? Sr, double Seconds, bool Cut) SolveWithCap(
        DesignSpec d, DesignInputs p, SolverOptions opt, Probe probe, TimeSpan cap, string tag)
    {
        using var cts = new CancellationTokenSource(cap);
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始求解（{tag}）　时间闸 {cap.TotalHours:0.#} h");
        try
        {
            var sr = Solver.Solve(d, p, opt, probe, cts.Token);
            sw.Stop();
            probe.Report($"── 求解结束（{tag}），耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟，场解 {sr.Solves} 次");
            return (sr, sw.Elapsed.TotalSeconds, false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            probe.Report($"★ 求解被时间闸切断（{tag}），已跑 {sw.Elapsed.TotalHours:0.00} h");
            return (null, sw.Elapsed.TotalSeconds, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            probe.Report($"★ 求解抛异常（{tag}）：{ex.GetType().Name}：{ex.Message}");
            return (null, sw.Elapsed.TotalSeconds, false);
        }
    }

    private static GateSet RunThreeGates(Action<string> W, Action Flush, DesignSpec solved, DesignInputs p,
                                         SolverOptions meshOpt, Probe probe, string tag, LineCase navCase)
    {
        var g = new GateSet();

        W($"═══════ 三关（{tag}；都跑在第二遍那张网格上）═══════");
        W($"── ① 升温全程（准静态轨迹，空管，夹头 = 设计的夹头温度）");
        Flush();
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ① 升温全程（{tag}）");
        g.Ramp = RampSweep.Run(solved, p, new RampSweepOptions { RunClampAlt = false, Mesh = meshOpt }, probe);
        sw.Stop();
        probe.Report($"── ① 结束（{tag}）：{g.Ramp.Verdict}，耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"结论：{g.Ramp.Verdict}　耗时 {sw.Elapsed.TotalSeconds:0} s，{g.Ramp.Points.Length} 个设定点，"
          + $"网格 = 细网格（细区 {meshOpt.FineMm:0.000} mm／半径 {meshOpt.FineRadiusMm:0.0} mm）");
        if (g.Ramp.VerdictDetail.Length > 0) W(g.Ramp.VerdictDetail);
        W("设定点\t夹头\t各段实际电流 A\t管J(实际)\t管J(设计)\t截面J(实际)\t截面J(设计)\t场有效");
        foreach (var pt in g.Ramp.Points)
            W($"{pt.SetpointC:0}\t{pt.ClampC:0}\t{string.Join("/", pt.Segs.Select(s => s.CurrentA.ToString("0")))}\t"
              + $"{N(Worst(pt.Segs.Select(s => s.TubeJAPerMm2)))}\t{N(pt.TubeJDesignAPerMm2)}\t"
              + $"{N(Worst(pt.Flanges.Select(f => f.SectionJ.ActualJ)))}\t{N(Worst(pt.Flanges.Select(f => f.SectionJ.DesignJ)))}\t"
              + $"{(pt.FieldValid ? "是" : "否")}");
        W("");
        Flush();

        W($"── ② 带玻璃稳态");
        var lcGlass = solved.BuildCase(p);
        Solver.ApplyCaseMesh(lcGlass, meshOpt);
        sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ② 带玻璃稳态（{tag}）");
        g.Glass = SafeRun(lcGlass, probe);
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"网格：细区 {lcGlass.MeshFineMm:0.000} mm／粗区 {lcGlass.MeshCoarseMm:0.0} mm／细区半径 {lcGlass.MeshFineRadiusMm:0.0} mm，网格单元 {g.Glass.MeshCells}");
        W($"结论：{Verdict(g.Glass)}");
        W($"耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {g.Glass.TotalMassG:0} g，耦合收敛 {g.Glass.Converged}，剩余误差估计 {g.Glass.CoupleRemainK:0.000} K。");
        W("  交付判据逐条：");
        foreach (var c in g.Glass.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(c));
        W("");
        Flush();

        W($"── ③ 空管到温稳态");
        W("口径（生产代码的原话，LineResult.StateDowngradeNote）：");
        W("  " + LineResult.StateDowngradeNote(true));
        var lcEmpty = solved.BuildCase(p, emptyTube: true);
        Solver.ApplyCaseMesh(lcEmpty, meshOpt);
        sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ③ 空管到温稳态（{tag}）");
        g.Empty = SafeRun(lcEmpty, probe);
        sw.Stop();
        probe.Report($"── ③ 结束（{tag}），耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"网格：细区 {lcEmpty.MeshFineMm:0.000} mm／细区半径 {lcEmpty.MeshFineRadiusMm:0.0} mm，网格单元 {g.Empty.MeshCells}");
        W($"结论：{Verdict(g.Empty)}");
        W($"耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {g.Empty.TotalMassG:0} g，耦合收敛 {g.Empty.Converged}，剩余误差估计 {g.Empty.CoupleRemainK:0.000} K。");
        W("  逐条（卡交付的与只作参考的一起印）：");
        foreach (var c in g.Empty.Checks) W("    " + Line(c));
        W("");
        W($"── 铂重（{tag}）：管 {g.Glass.TubeMassG:0} g + 法兰 {g.Glass.FlangeMassG:0} g = 合计 {g.Glass.TotalMassG:0} g"
          + $"（出自 ② 那一次；③ 那一次算出 {g.Empty.TotalMassG:0} g，同一几何、应逐位相同）");
        return g;
    }

    private static string OneLine(string which, SolverResult sr, GateSet g, double reqFine,
                                  LineCase navCase, ScanOut scan, double layerMm)
    {
        string head = !sr.FineRefined
            ? $"{which}（格子 {layerMm:0.###}）：**第二遍（细网格 {reqFine:0.000} mm）没有跑** —— 第一遍就{(sr.HitBound ? "**结构性停机**" : sr.Undetermined ? "**判不了**" : "没走通")}：{sr.StopWhy}　⇒ 这一档拿不出可交付的设计。"
            : $"{which}（格子 {layerMm:0.###}）：细网格（{sr.FineMmUsed:0.000} mm）上{(sr.Feasible ? "解出可行设计" : "**没解出可行设计**")}，{Three(g)}，"
              + $"铂重 {Fmt(g.Glass?.TotalMassG ?? double.NaN, "0")} g，舌保温逐片 {string.Join("/", sr.Design.TabInsulMm.Select(v => v.ToString("0.0")))} mm"
              + $"（= {string.Join("/", sr.Design.TabInsulMm.Select(v => Math.Abs(v / layerMm - Math.Round(v / layerMm)) < 1e-9 ? Math.Round(v / layerMm).ToString("0") + " 层" : "裸舌/不在层上"))}）。";
        int measured = scan.Lines.Count(s => s.Contains("可行窗口 ["));
        string win = measured == 0
            ? "可行窗口：**一个都没量到** —— 四片的解值点自己就不过（这一档没有解）。"
            : scan.TooNarrow.Count == 0
              ? $"可行窗口：量到的 {measured} 片都不窄于每层厚（在扫到的边界之内）。"
              : "可行窗口：**" + string.Join("、", scan.TooNarrow.Select(j => $"片{j}")) + " 窄于每层厚** —— 判据窗口窄于制造精度，点名不放宽。";
        return head + "　" + win;
    }

    private static LineResult SafeRun(LineCase lc, Probe? probe)
    {
        try { return LineRunner.Run(lc, probe); }
        catch (Exception ex) { return new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
    }

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    /// <summary>
    /// 判据表那一行 —— 走生产的 <see cref="Criteria.OneLine"/>，**不在门里手抄一份**。
    /// ★ 2026-09-18，Opus 5：原来这里各抄了一份判词，而手抄那份把参考量印成「过」——
    ///   于是输出里出现「过，裕度 −0.401」（旁证 A 第 2.4(c) 节）。门不许手抄生产配方。
    /// </summary>
    private static string Line(ConstraintOut c) => Criteria.OneLine(c);

    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN
         : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    private static double Val(LineResult? r, string key) => Get(r, key)?.Actual ?? double.NaN;

    private static string Three(GateSet? g)
        => g is null ? "三关没跑"
         : $"① {g.Ramp?.Verdict ?? "—"}　② {(g.Glass?.AllOk == true ? "过" : "不过／判不了")}　③ {(g.Empty is { Ok: true, Converged: true, AllOk: true } ? "过" : "不过／判不了")}";

    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("0.00");
    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);
    private static double Worst(IEnumerable<double> vs)
    {
        double w = double.NaN;
        foreach (double v in vs) { if (double.IsNaN(v)) continue; if (double.IsNaN(w) || v > w) w = v; }
        return w;
    }

    /// <summary>进度活页：每行带时刻**与自上一行的耗时**当场落盘（长跑要放探针）。</summary>
    private sealed class Probe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _last;
        public Probe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [+{now - _last,7:0.0}s 累计{now / 60,7:0.0}min]  {value}\r\n");
                _last = now;
            }
        }
    }

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw, string which)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve（Core/Solver.cs，两遍机制在 Solve 里）；图纸格 = SolverOptions.QuantInsulMm（= InsulationSearch.LayerMm）；"
                    + "网格配方 = Solver.ApplyCaseMesh（全仓唯一一份）；网格无关口径 = MeshVerify.RequiredMeshFor；升温全程 = RampSweep.Run；带玻璃与空管 = LineRunner.Run。");
        sb.AppendLine($"本文件里 **0.5 格那一档的每个数**都来自这一次运行（{which}，同一进程）；0.1 格那几行标着出处文件，是另一次跑，不混用。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
    }
}
