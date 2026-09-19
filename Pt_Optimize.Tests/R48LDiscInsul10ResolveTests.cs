using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　**圆盘保温 10 mm 下重解一次：还有没有可行点** —— 2026-09-17，Opus 5
//
//  ══ 为什么重解（而不是只重判）
//
//  重判回答的是「上一跑那份设计在新上限下还成不成立」；
//  重解回答的是「新上限下**存不存在**可行设计」。两个问题不一样 ——
//  重判不过 ≠ 没有解（旋钮还能往上抬），重判过 ≠ 这就是最小的那一份。
//
//  ══ 本轮跑什么
//
//    Solver.Solve（第一遍导航网格定位 + 第二遍细网格上重新二分求根，两遍机制在 Solve 里），
//    圆盘保温 = 生产默认（WrapLimits.JointZoneMaxMm），舌保温图纸格 = 生产默认 0.5 mm（= 包法每层）。
//    解出来之后跑三关（都在第二遍那张网格上），印铂重。
//    **没解出来就印停机设计与缺口** —— 停在哪一片、哪条判据、差多少、哪些候选被淘汰、为什么。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//
//    ① 解不出来 ⇒ **如实印停机点与缺口**；不许改上限、不许退回 20 mm 充数、不许挑一张更粗的网格。
//    ② 解出来的舌保温必须落在 {裸舌 0.30} ∪ {0.5 的倍数} 上 —— 这一条是**断言**（生产代码的病）。
//    ③ 解出来的圆盘保温必须 ≤ 接合区上限 —— 同样是**断言**（求解器不许解出一个缠不出来的设计）。
//    ④ 铂重如实报，不设门槛、不做解释性加工。
//    ⑤ 与 0.5 格那一跑（圆盘 20 mm）并列时**标明不是同一次运行**：那一跑在收紧耦合容差之前。
//    ⑥ 时间闸跑前写死，超时即切断并报「被时间闸切断」，不许装作跑完了。
// ════════════════════════════════════════════════════════════════════════════

public abstract class R48LDiscInsul10ResolveBase
{
    protected readonly ITestOutputHelper _o;
    protected R48LDiscInsul10ResolveBase(ITestOutputHelper o) { _o = o; }

    /// <summary>求解时间闸（跑前写死）。</summary>
    private static readonly TimeSpan SolveCap = TimeSpan.FromHours(4.0);

    /// <summary>与界面「自动定厚」同一份选项（R48LSolverInLineGateTests 守着这一条）。</summary>
    private static SolverOptions FineOptions(double fineMm, double radiusMm)
        => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = fineMm, FineRadiusMm = radiusMm };

    /// <summary>0.5 格那一跑（圆盘 20 mm、收紧耦合容差**之前**）的结论 —— 只作对照，标明出处。</summary>
    private static (string File, string Line) Ref05(string which) => which == "W08"
        ? ("R48_L_端到端_格子0.5_W08_本次开跑于2026-09-17_164425.txt",
           "求解 138.1 分钟／场解 103 次；**不可行**，停在片0「最热铂高出热偶读数」—— 法兰侧九根旋钮穷尽（外级倍率、外级半径、环倍率、内级半径、板厚都到底），下一根杠杆是盘径与舌半宽")
        : ("R48_L_端到端_格子0.5_W06_本次开跑于2026-09-17_164520.txt",
           "求解 104.6 分钟／场解 73 次；**不可行**，停在片1「管孔净流入」—— 法兰侧候选都不成立（外级倍率抬到底没变好、外级还是平的、板厚抬到底没变好）");

    protected void Run(string which)
    {
        var seed = which == "W08" ? DesignSpec.W08.Clone()
                 : which == "W06" ? DesignSpec.W06.Clone()
                 : throw new ArgumentException(which);
        // 形状与工况来自内置设计；五个旋钮一律被求解器丢掉（解与初值无关）。圆盘保温取生产默认（= 接合区上限）。
        seed.FlangeInsulated = true;
        seed.FlangeInsulMm = WrapLimits.JointZoneMaxMm;
        seed.DiscInsulMm = Array.Empty<double>();

        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_圆盘保温10_重解_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var o0 = new SolverOptions();
        double q = Solver.KnobQuantum(o0, Solver.Knob.Insul);
        var navCase = new LineCase();
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed);
        var opt = FineOptions(reqFine, reqRadius);

        W($"R48 L 路　**圆盘保温 {WrapLimits.JointZoneMaxMm:0.#} mm 下重解一次**　输入几何「{seed.Name}」（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 为什么重解 ═══════");
        W("重判回答「上一跑那份设计在新上限下还成不成立」；重解回答「新上限下**存不存在**可行设计」。两个问题不一样。");
        W($"现场限制：{WrapLimits.SourceNote}");
        W($"接合区上限 {WrapLimits.JointZoneMaxMm:0.#} mm = {WrapLimits.MaxTurnsAtJoint} 圈 × 每圈 {InsulationSearch.LayerMm:0.#} mm；口径：{WrapLimits.ZoneNote}");
        W("");
        W("═══════ 本轮跑什么 ═══════");
        W($"求解：Solver.Solve —— 第一遍导航网格（细区 {navCase.MeshFineMm:0.0} mm／粗区 {navCase.MeshCoarseMm:0.0} mm）定位，"
          + $"第二遍细网格（细区 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）上重新二分求根。");
        W($"舌保温图纸格 {q:0.###} mm（= 包法每层 {InsulationSearch.LayerMm:0.###} mm，生产默认）；舌保温上界 {o0.InsHiMm:0.#} mm（舌板不受接合区限）。");
        W("三关：① 升温全程　② 带玻璃稳态　③ 空管到温稳态 —— 都跑在第二遍那张网格上。");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("　① 解不出来 ⇒ 如实印停机点与缺口；不改上限、不退回 20 mm、不挑更粗的网格。");
        W($"　② 解出来的舌保温必须落在 {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{q:0.###} 的倍数}} 上 —— 断言，违反即红。");
        W($"　③ 解出来的圆盘保温必须 ≤ {WrapLimits.JointZoneMaxMm:0.#} mm —— 断言，违反即红。");
        W("　④ 铂重如实报。");
        W("　⑤ 与 0.5 格那一跑（圆盘 20 mm）并列时标明不是同一次运行（那一跑在收紧耦合容差之前）。");
        W($"　⑥ 时间闸 {SolveCap.TotalHours:0.#} h，超时即切断并报「被时间闸切断」。");
        W("");
        W("═══════ 输入（内置设计只提供几何与工况；五个旋钮一律被求解器丢掉）═══════");
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"　⇒ 盘环宽 = 盘半径 − 管孔半径 = {seed.DiscRadiusMm - seed.HoleRadiusMm:0.0} mm —— **整盘都是接合区**（分不出外区）。");
        W($"圆盘保温：{seed.FlangeInsulMm:0.#} mm（{InsulationSearch.LayersText(seed.FlangeInsulMm)}）= 接合区上限。");
        W($"管保温 {seed.TubeInsulMm:0.#} mm + 端部额外 {p.EndInsulExtraMm:0.#} mm —— **不受接合区限**（2026-09-18 Opus 5 按用户原话「其它地方(舌板与管)好缠绕」改回）。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_盘10重解_{which}_{stamp}_进行中.log");
        var probe = new SlowProbe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W("");
        Flush();

        // ── 求解 ──
        W("═══════ 求解 ═══════");
        Flush();
        var (sr, solveS, cut) = SolveWithCap(seed.Clone(), p, opt, probe, SolveCap, $"{which} 盘10重解");
        W($"求解耗时 {solveS / 60:0.0} 分钟（{solveS:0} s），场解 {(sr?.Solves ?? 0)} 次。");
        if (sr is null)
        {
            W(cut ? "★ **被跑前写死的时间闸切断** ⇒ 本轮没有解出来的设计 ⇒ 三关不跑（不许拿半成品冒充结果）。"
                  : "★ **求解器抛异常或没返回** ⇒ 本轮没有解出来的设计 ⇒ 三关不跑。");
            Finish(sb, file, totalSw, which);
            Assert.Fail($"{which}：求解没返回结果（{(cut ? "时间闸切断" : "异常")}）—— 报告已落盘：{file}");
            return;
        }

        W($"结论：{(sr.Feasible ? "**全判据通过**（可行）" : "**不可行**（求解器没把全部判据补上）")}　停因：{sr.StopWhy}");
        if (sr.HitBound) W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        W($"终局网格：{(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm（第二遍求根与终局复核跑在判决的那张网格上）" : $"**没做第二遍** ⇒ 只在导航网格 {navCase.MeshFineMm:0.0} mm 上成立、**不可交付**")}");
        var solved = sr.Design;
        W("");

        W("── 停机时（或解出来时）各旋钮的值，逐片");
        W("片\t板厚 mm\t舌保温 mm\t舌保温层\t环倍率 t₁\t舌片厚 mm\t槽张角 °\t舌孔 R mm");
        for (int j = 0; j < solved.TabThickMm.Length; j++)
            W($"{j}\t{solved.TabThickMm[j]:0.00}\t{solved.TabInsulMm[j]:0.0}\t{InsulationSearch.LayersText(solved.TabInsulMm[j])}\t"
              + $"{solved.RingMul[j]:0.00}\t{solved.TongueThickMm[j]:0.00}\t{solved.SlotSpanDeg[j]:0.#}\t{solved.TabHoleRMm[j]:0.0}");
        W($"圆盘保温（整线）{solved.FlangeInsulMm:0.#} mm（{InsulationSearch.LayersText(solved.FlangeInsulMm)}）");
        W("");

        W($"── 门：解出来的舌保温落在哪儿（合法集合 = {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{q:0.###} 的倍数}}）");
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

        // ── 三关 ──
        LineResult? glass = null, empty = null;
        string rampVerdict = "没跑";
        {
            W($"═══════ 三关（都跑在{(sr.FineRefined ? "第二遍那张细网格" : "导航网格 —— 第二遍没做")}上）═══════");
            var mesh = sr.FineRefined ? opt : new SolverOptions { FineMm = navCase.MeshFineMm, FineRadiusMm = navCase.MeshFineRadiusMm };
            Flush();

            probe.Report("── ① 升温全程");
            var sw = Stopwatch.StartNew();
            RampSweepResult? ramp = null;
            try { ramp = RampSweep.Run(solved, p, new RampSweepOptions { RunClampAlt = false, Mesh = mesh }, probe); }
            catch (Exception ex) { W($"── ① 升温全程：**抛异常** {ex.GetType().Name}：{ex.Message}"); }
            sw.Stop();
            rampVerdict = ramp?.Verdict ?? "抛异常";
            W($"── ① 升温全程　结论：{rampVerdict}　耗时 {sw.Elapsed.TotalSeconds:0} s，{ramp?.Points.Length ?? 0} 个设定点");
            if (ramp is not null && ramp.VerdictDetail.Length > 0) W(ramp.VerdictDetail);
            Flush();

            probe.Report("── ② 带玻璃稳态");
            var lcG = solved.BuildCase(p);
            Solver.ApplyCaseMesh(lcG, mesh);
            sw = Stopwatch.StartNew();
            glass = SafeRun(lcG, probe);
            sw.Stop();
            W($"── ② 带玻璃稳态　网格 细区 {lcG.MeshFineMm:0.000} mm／半径 {lcG.MeshFineRadiusMm:0.0} mm，单元 {glass.MeshCells}");
            W($"结论：{Verdict(glass)}");
            W($"耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {glass.TotalMassG:0} g，耦合 {glass.CoupleRounds} 轮、容差 {glass.CoupleTolKUsed:0.000} K、剩余误差估计 {glass.CoupleRemainK:0.000} K。");
            W("  交付判据逐条：");
            foreach (var ck in glass.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(ck));
            Flush();

            probe.Report("── ③ 空管到温稳态");
            var lcE = solved.BuildCase(p, emptyTube: true);
            Solver.ApplyCaseMesh(lcE, mesh);
            sw = Stopwatch.StartNew();
            empty = SafeRun(lcE, probe);
            sw.Stop();
            W($"── ③ 空管到温稳态　结论：{Verdict(empty)}　耗时 {sw.Elapsed.TotalSeconds:0} s");
            W("  交付判据逐条：");
            foreach (var ck in empty.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(ck));
            W("");
            W($"── 铂重：管 {glass.TubeMassG:0} g + 法兰 {glass.FlangeMassG:0} g = 合计 {glass.TotalMassG:0} g（出自 ②）");
            W("");
            Flush();
        }

        // ── 缺口（解不出来时最要紧的那一段）──
        W("═══════ 缺口：离可行还差多少（② 带玻璃稳态，按裕度）═══════");
        W("判据\t实际／限值\t裕度\t位置\t判定");
        foreach (var ck in (glass?.Checks ?? Array.Empty<ConstraintOut>()).Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"{Criteria.Plain(ck.Name)}\t{Fmt(ck.Actual, "0.###")}／{ck.Limit:0.###} {ck.Unit}\t{Fmt(Slack(ck), "+0.###;-0.###")}\t{ck.Where}\t"
              + (ck.Undetermined ? "**判不了**" : ck.Ok ? "过" : "**不过**"));
        W("");

        var (refFile, refLine) = Ref05(which);
        W("═══════ 对照：0.5 格那一跑（圆盘 **20 mm**）═══════");
        W($"⚠ **不是同一次运行、也不是同一份代码**：那一跑在 §0.-10 收紧耦合容差之前，圆盘保温还是 20 mm。只作对照。");
        W($"出处 deliverable/{refFile}");
        W($"那一跑：{refLine}");
        W($"本次（圆盘 {WrapLimits.JointZoneMaxMm:0.#} mm）：求解 {solveS / 60:0.0} 分钟／场解 {sr.Solves} 次；"
          + $"{(sr.Feasible ? "**可行**" : "**不可行**")}，停因：{sr.StopWhy}");
        W("");

        W("═══════ 一句话 ═══════");
        W($"{which}（圆盘 {WrapLimits.JointZoneMaxMm:0.#} mm、舌保温格 {q:0.###}）："
          + (sr.Feasible
             ? $"解出可行设计，铂重 {Fmt(glass?.TotalMassG ?? double.NaN, "0")} g，舌保温逐片 {string.Join("/", solved.TabInsulMm.Select(v => v.ToString("0.0")))} mm；三关 ① {rampVerdict}　② {Short(glass)}　③ {Short(empty)}。"
             : $"**没有可行点** —— {sr.StopWhy}　停机那份设计的缺口见上表；三关 ① {rampVerdict}　② {Short(glass)}　③ {Short(empty)}。"));
        W("");
        W("═══════ 求解轨迹（Solver.Trace 全文）═══════");
        foreach (var t in sr.Trace) W(t);
        Finish(sb, file, totalSw, which);

        Assert.True(sr.Solves > 0, "一次场解都没跑 —— 那不是求解");
        Assert.True(offGrid.Count == 0,
            $"{which}：解出来的舌保温有片既不是裸舌也不是 {q} 的整数倍 ⇒ 现场包不出来（{string.Join("、", offGrid)}）");
        Assert.True(solved.FlangeInsulMm <= WrapLimits.JointZoneMaxMm + 1e-9,
            $"{which}：求解器解出来的圆盘保温 {solved.FlangeInsulMm} mm 超过接合区上限 {WrapLimits.JointZoneMaxMm} mm —— 那是现场缠不出来的设计");
    }

    private static (SolverResult? Sr, double Seconds, bool Cut) SolveWithCap(
        DesignSpec d, DesignInputs p, SolverOptions opt, SlowProbe probe, TimeSpan cap, string tag)
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

    private static LineResult SafeRun(LineCase lc, IProgress<string>? probe)
    {
        try { return LineRunner.Run(lc, probe); }
        catch (Exception ex) { return new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
    }

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    private static string Short(LineResult? r)
        => r is null ? "没跑" : !r.Ok ? "判不了" : !r.Converged ? "未收敛" : r.AllOk ? "过" : "不过／判不了";

    /// <summary>
    /// 判据表那一行 —— 走生产的 <see cref="Criteria.OneLine"/>，**不在门里手抄一份**。
    /// ★ 2026-09-18，Opus 5：原来这里各抄了一份判词，而手抄那份把参考量印成「过」——
    ///   于是输出里出现「过，裕度 −0.401」（旁证 A 第 2.4(c) 节）。门不许手抄生产配方。
    /// </summary>
    private static string Line(ConstraintOut c) => Criteria.OneLine(c);

    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN
         : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw, string which)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve（两遍机制在 Solve 里）；图纸格 = SolverOptions.QuantInsulMm（= InsulationSearch.LayerMm）；"
                    + "接合区上限 = WrapLimits.JointZoneMaxMm；网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；"
                    + "升温全程 = RampSweep.Run；带玻璃与空管 = LineRunner.Run。");
        sb.AppendLine($"本文件里**本次那一档的每个数**都来自这一次运行（{which}，同一进程）；标着出处文件的那几行是另一次跑，不混用。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
    }
}

// ★ 2026-09-17 Opus 5：**一份设计一个测试类** —— xUnit 的默认并行粒度是「集合」，而一个类默认就是一个集合，
//   同一个类里的两行 InlineData 会**排队跑**。这一轮两份设计各要两三个小时，排队等于把墙上时间翻倍。
//   ⇒ 逻辑留在基类（只有一份），两个薄壳各占一个集合，跑起来才是并行的。
[Trait("速度", "慢")]
public class R48LDiscInsul10ResolveW08Tests : R48LDiscInsul10ResolveBase
{
    public R48LDiscInsul10ResolveW08Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 圆盘保温10下重解_W08() => Run("W08");
}

[Trait("速度", "慢")]
public class R48LDiscInsul10ResolveW06Tests : R48LDiscInsul10ResolveBase
{
    public R48LDiscInsul10ResolveW06Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 圆盘保温10下重解_W06() => Run("W06");
}
