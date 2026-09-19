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

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路 端到端：W08 与 W06 —— 2026-09-17，Opus 5。
//
//  ══ 链路（用户 2026-09-17 定稿）
//    内置设计只提供**几何与工况**（五个旋钮一律被求解器丢掉）
//      → Solver 求解（从约束盒下角二分求根，与初值无关）
//      → **求得的设计**过三关：① 升温全程 → ② 带玻璃稳态 → ③ 空管到温
//      → 铂重。
//
//  ⚠ 上一轮（2026-09-17 凌晨，Opus 4.6）把内置设计**原样**过三关 —— 那是**未求解的输入**，
//    不是设计结果，截面 J 27.71/11 全不过。本轮把 Solver 接进来，报告先印输入、再印解、再三关。
//
//  升温期不卡 ±5 K；没有膨胀判据，只报告膨胀量；δ=0.1 mm 退役。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LEndToEndTests
{
    private readonly ITestOutputHelper _o;
    public R48LEndToEndTests(ITestOutputHelper o) { _o = o; }

    /// <summary>
    /// 求解器选项 = **生产界面那一份**（<c>UI/LineDesignPage</c> 的「自动定厚」D8 分支）：
    /// <c>MaxRounds = 40</c>、解法族取界面预设（下拉第 0 项「不挖舌孔」⇒ <c>AllowTabCuts = false</c>）、
    /// 第二遍细网格取界面预设（<c>fineMm = 0</c>，界面注释：「按钮要等得起」⇒ <c>FineRadiusMm</c> 也是 0）。
    /// 其余一律用 <see cref="SolverOptions"/> 的缺省（盒的上下界、图纸格、二分容差都在那里，不在这里另设）。
    /// </summary>
    private static SolverOptions ProductionOptions() => new SolverOptions { MaxRounds = 40, AllowTabCuts = false };

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 端到端_求解再过三关(string which)
    {
        var seed = which == "W08" ? DesignSpec.W08.Clone()
                 : which == "W06" ? DesignSpec.W06.Clone()
                 : throw new ArgumentException(which);
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_端到端_求解再过三关_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        W($"R48 L 路　端到端：**先求解、再过三关**　输入几何「{seed.Name}」（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("链路（用户 2026-09-17 定稿）：内置输入 → **Solver 求解** → 求得的设计过 ① 升温全程 → ② 带玻璃稳态 → ③ 空管到温 → 铂重");
        W("⚠ 上一轮（Opus 4.6）把内置设计原样过三关，那是**未求解的输入**，不是设计结果。本轮补上求解这一步。");
        W();

        // ═══════ 输入 ═══════
        W("═══════ 输入（内置设计只提供几何与工况；五个旋钮一律被求解器丢掉）═══════");
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，"
          + $"设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"工况：夹头 {seed.ClampTempC:0} °C，压接段长 {seed.ClampLengthMm:0.#} mm，"
          + $"设定电流密度 J {seed.JDesignAPerMm2:0.#} A/mm²（终验限值 {seed.JCheckAPerMm2:0.#}）");
        W($"设计输入表：默认（DesignInputs 默认构造）；现场实际服役 ≤6 个月（4380 h），默认取 1 年（{p.DesignLifeHours} h）偏保守。");
        W($"★ 被丢掉的输入旋钮（只为让人看清「解与初值无关」）：板厚 {Join(seed.TabThickMm)} mm／"
          + $"舌保温 {Join(seed.TabInsulMm)} mm／环倍率 {Join(seed.RingMul)}");
        W();

        // ═══════ 求解器 ═══════
        W("═══════ 求解器（从约束盒下角二分求根，不接受起点）═══════");
        var opt = ProductionOptions();
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed);
        var navCase = new LineCase();
        W($"入口：Solver.Solve（Core/Solver.cs），选项取**生产界面自动定厚**那一份："
          + $"轮数上限 {opt.MaxRounds}、解法族「{(opt.AllowTabCuts ? "挖舌孔" : "不挖舌孔")}」（界面下拉预设）、"
          + $"第二遍细网格 {(opt.FineMm > 0 ? $"{opt.FineMm:0.000} mm" : "**不做**（界面预设，界面注释写「按钮要等得起」）")}");
        W($"网格：导航网格 = 整线算例缺省（细区 {navCase.MeshFineMm:0.0} mm／粗区 {navCase.MeshCoarseMm:0.0} mm／细区半径 {navCase.MeshFineRadiusMm:0.0} mm）；"
          + $"终局网格 = 第二遍用的那张。本形状按网格无关口径要求 {reqFine:0.000} mm（细区半径 {reqRadius:0.0} mm）—— 见下面「终局网格」一行。");
        W();

        // ★ 长跑要放探针（用户 2026-09-13）：求解与三关各是几十分钟的静默，进度逐行写进一个**临时**活页，
        //   方向不对当场看得见。活页在系统临时目录，不是交付物；交付只有上面那个带开跑时刻的 .txt。
        string live = Path.Combine(Path.GetTempPath(), $"R48_L_端到端_{which}_{stamp}_进行中.log");
        var probe = new Probe(live);
        W($"进度活页（临时，非交付物）：{live}");
        W();

        var solveSw = Stopwatch.StartNew();
        SolverResult? sr = null;
        string solveThrew = "";
        try
        {
            probe.Report($"── 开始求解 {which}");
            sr = Solver.Solve(seed.Clone(), p, opt, probe);
        }
        catch (Exception ex) { solveThrew = $"{ex.GetType().Name}：{ex.Message}"; probe.Report("★ 求解抛异常：" + solveThrew); }
        solveSw.Stop();
        probe.Report($"── 求解结束，耗时 {solveSw.Elapsed.TotalMinutes:0.0} 分钟");

        W($"求解耗时 {solveSw.Elapsed.TotalMinutes:0.0} 分钟（{solveSw.Elapsed.TotalSeconds:0} s），场解 {(sr?.Solves ?? 0)} 次。");
        if (sr is null)
        {
            W($"★ 求解器**抛异常**：{solveThrew}");
            W("⇒ 没有解出来的设计 ⇒ 三关不跑（不许拿未求解的输入冒充设计结果）。");
            Finish(sb, file, totalSw);
            Assert.Fail($"{which}：求解器抛异常 —— {solveThrew}");
            return;
        }

        var solved = sr.Design;
        W($"结论：{(sr.Feasible ? "**全判据通过**（可行）" : "**不可行**（求解器没把全部判据补上）")}"
          + $"　停因：{sr.StopWhy}");
        if (sr.HitBound)
            W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        W($"终局网格：{(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm（第二遍求根跑在判决的那张网格上）" : "**没做第二遍** ⇒ 这个解只在导航网格上成立、**不可交付**")}");
        if (!sr.FineRefined)
            W($"　（本形状按网格无关口径要求 {reqFine:0.000} mm；导航 {navCase.MeshFineMm:0.0} mm 与细网格之间的差在历史实测上到过 2.03 倍 —— "
              + "下面三关的数因此**未经细网格复核**，引用时这一句不许略掉。）");
        W();

        W("── 求解器解出来的设计（各旋钮终值，逐片）");
        // ★ 2026-09-17 Opus 5：后三列是**场定的位置量**（Solver.FieldPlacement 按收敛的场写回设计），
        //   不是旋钮，但它们属于「求解器解出来的设计」的一部分；此前这张表没有它们，照表复原出的设计不是同一个。
        W("片\t板厚 mm\t舌保温 mm\t环倍率 t₁\t外级 t₂\t内级 r₁ mm\t外级 r₂ mm\t舌片厚 mm\t槽张角 °\t舌孔 R mm\t孔拉长比\t槽心 °(场定)\t孔心 x mm(场定)\t盘槽当地电流 °(场定)");
        int np = solved.TabThickMm.Length;
        for (int j = 0; j < np; j++)
        {
            var rr = solved.RingRadiiOf(j);
            W($"{j}\t{solved.TabThickMm[j]:0.00}\t{solved.TabInsulMm[j]:0.0}\t{solved.RingMul[j]:0.00}\t{solved.RingMulOuter(j):0.00}\t"
              + $"{(rr.Length > 0 ? rr[0].ToString("0.0") : "—")}\t{(rr.Length > 1 ? rr[1].ToString("0.0") : "—")}\t"
              + $"{(j < solved.TongueThickMm.Length && !double.IsNaN(solved.TongueThickMm[j]) ? solved.TongueThickMm[j].ToString("0.00") : "同基板")}\t"
              + $"{(j < solved.SlotSpanDeg.Length ? solved.SlotSpanDeg[j].ToString("0.#") : "—")}\t"
              + $"{(j < solved.TabHoleRMm.Length ? solved.TabHoleRMm[j].ToString("0.0") : "—")}\t"
              + $"{(j < solved.TabHoleAspect.Length ? solved.TabHoleAspect[j].ToString("0.00") : "—")}\t"
              + $"{Num(j < solved.SlotCenterDeg.Length ? solved.SlotCenterDeg[j] : double.NaN, "0.###")}\t"
              + $"{Num(j < solved.TabHoleXMm.Length ? solved.TabHoleXMm[j] : double.NaN, "0.###")}\t"
              + $"{Num(j < solved.DiscCutRotDeg.Length ? solved.DiscCutRotDeg[j] : double.NaN, "0.###")}");
        }
        W("（后三列 NaN = 该片没有被场定过／走默认规则。它们进几何，复原设计时不能漏。）");
        W($"（圆盘半径 {solved.DiscRadiusMm:0.#} mm、舌长 {solved.TabLengthMm:0.#} mm、舌半宽 {solved.TabHalfWidthMm:0.#} mm —— 形状是输入，求解器不动它。）");
        W();

        // ═══════ ① 升温全程 ═══════
        W("═══════ ① 升温全程（准静态轨迹，空管，夹头 = 设计的夹头温度）═══════");
        W("J 一律两条并列：**设计电流**（20 °C/h 升温全程峰值，闭式，对所有设定点是同一个数）与**该设定点实际解出来的电流**。");
        var rampSw = Stopwatch.StartNew();
        var rampOpt = new RampSweepOptions { RunClampAlt = false };
        probe.Report("── 开始 ① 升温全程");
        var rampRes = RampSweep.Run(solved, p, rampOpt, probe);
        rampSw.Stop();
        probe.Report($"── ① 升温全程结束：{rampRes.Verdict}，耗时 {rampSw.Elapsed.TotalSeconds:0} s");
        W($"结论：{rampRes.Verdict}");
        W($"耗时 {rampSw.Elapsed.TotalSeconds:0} s，{rampRes.Points.Length} 个设定点。");
        if (rampRes.VerdictDetail.Length > 0) W(rampRes.VerdictDetail);
        if (rampRes.DisagreeNotes.Length > 0)
        {
            W("── 两条电流口径**结论不同**的地方（用户 2026-09-17：结论不同要写明）");
            foreach (var s in rampRes.DisagreeNotes) W("  " + s);
        }
        else
            W("── 两条电流口径在每一点、每一片上**结论相同**（没有一处只在其中一条上超限）。");

        W();
        W("设定点\t夹头\t各段实际电流 A\t管J(实际)\t管J(设计)\t截面J(实际)\t截面J(设计)\t场有效\t备注");
        foreach (var pt in rampRes.Points)
        {
            string currents = string.Join("/", pt.Segs.Select(s => s.CurrentA.ToString("0")));
            double worstTubeJ = Worst(pt.Segs.Select(s => s.TubeJAPerMm2));
            double worstActual = Worst(pt.Flanges.Select(f => f.SectionJ.ActualJ));
            double worstDesign = Worst(pt.Flanges.Select(f => f.SectionJ.DesignJ));
            string note = !pt.FieldValid ? pt.FieldInvalidReason
                        : pt.Flanges.Any(f => f.SectionJUndetermined) ? "截面J判不了"
                        : pt.Segs.Any(s => !s.TubeJOk) ? "管J超限"
                        : pt.Flanges.Any(f => !f.SectionJOk) ? "截面J超限"
                        : "";
            W($"{pt.SetpointC:0}\t{pt.ClampC:0}\t{currents}\t{N(worstTubeJ)}\t{N(pt.TubeJDesignAPerMm2)}\t{N(worstActual)}\t{N(worstDesign)}\t"
              + $"{(pt.FieldValid ? "是" : "否")}\t{note}");
        }
        W($"（管 J 限值 {(rampRes.Points.Length > 0 && rampRes.Points[0].Segs.Length > 0 ? rampRes.Points[0].Segs[0].TubeJLimit.ToString("0.#") : "—")} A/mm²；"
          + $"截面 J 限值 {(rampRes.Points.Length > 0 && rampRes.Points[0].Flanges.Length > 0 ? rampRes.Points[0].Flanges[0].SectionJLimit.ToString("0.#") : "—")} A/mm²。"
          + "两列都取该点最坏的那一片／那一段。）");

        var ptTop = rampRes.Points.LastOrDefault();
        if (ptTop is not null && ptTop.Flanges.Length > 0)
        {
            W();
            W($"── 最高设定点 {ptTop.SetpointC:0} °C 的逐片截面 J（两条并列）");
            W("片\t实际电流 A\t截面J(实际)\t最紧截面(实际)\t截面J(设计)\t最紧截面(设计)");
            foreach (var f in ptTop.Flanges)
            {
                var v = f.SectionJ;
                W($"{f.Name}\t{v.ActualCurrentA:0}\t{N(v.ActualJ)}\t{v.ActualWhere}\t{N(v.DesignJ)}\t{v.DesignWhere}");
            }
        }

        var pt1150 = rampRes.Points.FirstOrDefault(pt => Math.Abs(pt.SetpointC - 1150) < 1);
        if (pt1150 is not null)
        {
            W();
            W($"── 伸长报告（1150 °C，T装 = {rampOpt.TAssemblyC:0} °C；>1000 °C 标外推）");
            for (int j = 0; j < pt1150.Flanges.Length; j++)
            {
                var fi = pt1150.Flanges[j];
                string extrap = fi.IsExtrapolated ? "（外推：纯铂膨胀数据只到 1000 °C）" : "";
                W($"  片 {j}「{fi.Name}」：管根−舌板平均温差 {Signed(fi.TRootMinusTTabMeanK, "0.0")} K，"
                  + $"舌板相对管位移 {Signed(fi.TabDisplacementMm, "0.0000")} mm，"
                  + $"舌尖总位移 {Signed(fi.TipTotalDisplacementMm, "0.0000")} mm，"
                  + $"盘外缘径向伸长 {Signed(fi.DiscRadialMm, "0.0000")} mm，"
                  + $"压接段伸长 {Signed(fi.ClampElongMm, "0.0000")} mm" + extrap);
            }
            for (int i = 0; i < pt1150.Segs.Length; i++)
            {
                var si = pt1150.Segs[i];
                string extrap = si.ElongCoverage >= ExpansionCoverage.Extrapolated ? "（外推）" : "";
                W($"  段 {i}「{si.Name}」：管段总伸长 {Signed(si.TotalElongMm, "0.0000")} mm{extrap}");
            }
        }
        W();

        // ═══════ ② 带玻璃稳态 ═══════
        W("═══════ ② 带玻璃稳态（法兰设计成不成功）═══════");
        var glassSw = Stopwatch.StartNew();
        var lcGlass = solved.BuildCase(p);
        probe.Report("── 开始 ② 带玻璃稳态");
        var glassRes = LineRunner.Run(lcGlass, probe);
        glassSw.Stop();
        probe.Report($"── ② 结束，耗时 {glassSw.Elapsed.TotalSeconds:0} s");
        string glassVerdict = !glassRes.Ok ? $"整线解没解出来 ⇒ 判不了（{glassRes.Message}）"
                            : glassRes.AllOk ? "全判据通过"
                            : $"有判据不过或判不了：{string.Join("；", glassRes.Failed)}";
        W($"结论：{glassVerdict}");
        W($"耗时 {glassSw.Elapsed.TotalSeconds:0} s，铂重 {glassRes.TotalMassG:0} g，耦合收敛 {glassRes.Converged}，剩余误差估计 {glassRes.CoupleRemainK:0.000} K。");
        W("  交付判据逐条：");
        foreach (var c in glassRes.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"    {Criteria.Plain(c.Name)}：实际 {c.Actual:0.###} / 限值 {c.Limit:0.###} {c.Unit}，"
              + $"{(c.Undetermined ? "**无法判定**" : c.Ok ? "过" : "**不过**")}，位置 {c.Where}");
        W();

        // ═══════ ③ 空管到温 ═══════
        W("═══════ ③ 空管到温稳态 ═══════");
        // ★ 2026-09-17 Opus 5：K 路的**分工况判据**已合进本工作树 ⇒ 这一关按空管那一列判。
        //   口径只有一处来源（LineResult.RequiredByState），下面三行是从那张表**现取**的，不是手抄。
        W("口径（生产代码的原话，LineResult.StateDowngradeNote）：");
        W("  " + LineResult.StateDowngradeNote(true));
        W("本工况判定用的必备名单（LineResult.RequiredFor(空管到温稳态)）：");
        W("  " + string.Join("、", LineResult.RequiredFor(true).Select(q => Criteria.Plain(q.Prefix))));
        W("带玻璃稳态的必备名单（对照；② 用的是这一份）：");
        W("  " + string.Join("、", LineResult.RequiredFor(false).Select(q => Criteria.Plain(q.Prefix))));
        var emptySw = Stopwatch.StartNew();
        var lcEmpty = solved.BuildCase(p, emptyTube: true);
        probe.Report("── 开始 ③ 空管到温稳态");
        var emptyRes = LineRunner.Run(lcEmpty, probe);
        emptySw.Stop();
        probe.Report($"── ③ 结束，耗时 {emptySw.Elapsed.TotalSeconds:0} s");
        bool emptyOk = emptyRes.Ok && emptyRes.Converged && emptyRes.AllOk;
        string emptyVerdict = !emptyRes.Ok ? $"整线解没解出来 ⇒ 判不了（{emptyRes.Message}）"
                            : !emptyRes.Converged ? $"外层耦合未收敛（剩余误差估计 {emptyRes.CoupleRemainK:0.000} K）—— 场无效，判不了"
                            : emptyRes.AllOk ? "全判据通过"
                            : $"有判据不过或判不了：{string.Join("；", emptyRes.Failed)}";
        W($"结论：{emptyVerdict}");
        W($"耗时 {emptySw.Elapsed.TotalSeconds:0} s，铂重 {emptyRes.TotalMassG:0} g，耦合收敛 {emptyRes.Converged}，剩余误差估计 {emptyRes.CoupleRemainK:0.000} K。");
        W($"工况位（LineResult.EmptyTube，由整线算例盖章）：{emptyRes.EmptyTube}");
        W($"场的有效性（不是判据、两态都要过）：{(emptyRes.FieldUndeterminedReasons.Length == 0 ? "没有判不了的" : string.Join("；", emptyRes.FieldUndeterminedReasons))}");
        W($"必备名单里缺席的：{(emptyRes.MissingChecks.Length == 0 ? "无" : string.Join("、", emptyRes.MissingChecks.Select(Criteria.Plain)))}");
        W("  逐条（**卡交付的与只作参考的一起印**，值照常算 —— 参考量不是「没算」）：");
        foreach (var c in emptyRes.Checks)
        {
            string kind = c.Kind switch
            {
                CheckKind.HardSafety => "卡交付（硬安全线）",
                CheckKind.Target => "卡交付（目标）",
                _ => "只作参考",
            };
            W($"    {Criteria.Plain(c.Name)}：实际 {(c.Withheld ? "暂不给数" : c.Actual.ToString("0.###"))} / 限值 {c.Limit:0.###} {c.Unit}，"
              + $"{(c.Undetermined ? "**无法判定**" : c.Ok ? "过" : "**不过**")}，位置 {c.Where}　[{kind}]");
        }
        var refFailed = emptyRes.Checks
            .Where(c => c.Kind == CheckKind.Reference && !c.Withheld && !c.Undetermined && !c.Ok)
            .Select(c => $"{Criteria.Plain(c.Name)} {c.Actual:0.###}/{c.Limit:0.###} {c.Unit}")
            .ToArray();
        W(refFailed.Length == 0
            ? "  （本工况没有「只作参考却越限」的条目。）"
            : "  旧口径（两态都卡这三条）下会被判不过的，是这几条在空管态降为参考量的：" + string.Join("；", refFailed)
              + "　—— 值一个没变，变的只是它们在**空管到温稳态**卡不卡交付。");
        W();

        // ═══════ 铂重 ═══════
        W("═══════ 铂重（求得的设计）═══════");
        W($"管 {glassRes.TubeMassG:0} g + 法兰 {glassRes.FlangeMassG:0} g = 合计 {glassRes.TotalMassG:0} g"
          + $"　（出自 ② 那一次整线解；③ 那一次算出 {emptyRes.TotalMassG:0} g，同一几何、应逐位相同）");
        W();

        // ═══════ 一句话 ═══════
        W("═══════ 一句话 ═══════");
        W($"{which}：求解器{(sr.Feasible ? "解出可行设计" : "**没解出可行设计**")}，"
          + $"三关 ① {rampRes.Verdict}　② {(glassRes.AllOk ? "过" : "不过／判不了")}　③ {(emptyOk ? "过" : "不过／判不了")}，"
          + $"铂重 {glassRes.TotalMassG:0} g"
          + $"（{(sr.FineRefined ? $"终局网格 {sr.FineMmUsed:0.000} mm" : "**只在导航网格上**，未做细网格第二遍 ⇒ 不可交付")}）。");
        W();

        // ═══════ 求解轨迹（证据）═══════
        W("═══════ 求解轨迹（Solver.Trace 全文 —— 求根过程唯一的可回溯记录）═══════");
        foreach (var t in sr.Trace) W(t);

        Finish(sb, file, totalSw);

        // 最低限度断言
        Assert.True(rampRes.Points.Length > 0, "升温全程一个点都没跑出来");
        Assert.True(glassRes.Segments.Length > 0, "带玻璃稳态没有段结果");
        Assert.True(emptyRes.Segments.Length > 0, "空管稳态没有段结果");
        // ★ 三关吃的必须是**求解器解出来的**那份设计，不是内置输入
        Assert.Same(sr.Design, solved);
        Assert.True(sr.Solves > 0, "求解器一次场解都没跑 —— 那不是求解");
    }

    /// <summary>进度活页：每行带时刻当场落盘（长跑要放探针；活页在系统临时目录，不是交付物）。</summary>
    private sealed class Probe : IProgress<string>
    {
        private readonly string _path;
        private readonly object _lock = new();
        public Probe(string path) { _path = path; File.WriteAllText(path, $"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); }
        public void Report(string value)
        {
            lock (_lock) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss}  {value}\r\n");
        }
    }

    private static string Join(double[] v) => string.Join("/", v.Select(x => x.ToString("0.00")));
    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("0.00");
    /// <summary>NaN 印「NaN」（不是「—」）：这一列的 NaN 是**有意义的值**（走默认规则），不是「没有数」。</summary>
    private static string Num(double v, string fmt) => double.IsNaN(v) ? "NaN" : v.ToString(fmt);
    private static double Worst(IEnumerable<double> vs)
    {
        double w = double.NaN;
        foreach (double v in vs) { if (double.IsNaN(v)) continue; if (double.IsNaN(w) || v > w) w = v; }
        return w;
    }

    /// <summary>带符号数照 R48 L 第三轮定下的印法（自拼符号；.NET 分段格式会把四舍五入到零的负数印成两个符号）。</summary>
    private static string Signed(double v, string fmt)
        => double.IsNaN(v) ? "—" : (v < 0 ? "-" : "+") + Math.Abs(v).ToString(fmt);

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve（Core/Solver.cs）；升温全程 = RampSweep.Run（Core/RampSweep.cs）；"
                    + "带玻璃与空管 = LineRunner.Run（Core/LineRunner.cs）。");
        sb.AppendLine("每个数来自同一次运行，不拼两份输出。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
