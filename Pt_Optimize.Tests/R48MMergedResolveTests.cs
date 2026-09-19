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
//  R48 M 合并树：**现口径下把两份交付设计各重解一次**（网格修复前基线）
//  2026-09-18，Opus 5
//
//  ══ 为什么现在跑
//
//  HANDOVER §0.-16M 戊 2 挂着：「两份可交付设计没有在新口径下重解」——
//  §0.-16M 那一轮只跑过单点整线解与四行对拍，Solver.Solve 端到端一次都没跑；
//  而段解地板降了十倍之后，旧文件里的每个数都带着旧地板（第 3～4 位小数）。
//  §0.-15M 丁 4 同一条也照旧开着（默认圆盘保温退回 20 mm 之后没有重解）。
//
//  ⇒ 本轮把**现口径**下的完整生产路各走一次：
//     Solver.Solve（第一遍导航网格定位 + 第二遍判决网格上重新二分求根）
//       → 终验三关（升温全程／带玻璃稳态／空管到温，走生产件 FinalCheck.Run）
//       → 铂重 → 每片舌保温可行窗口（InsulWindow.Measure，判决网格）→ 保温方案表。
//
//  ══ ★★ 文件头写死的那句：**网格修复前基线**
//
//  同一台机器上 r48_N 正在修导航网格 30.75～31.0 那一段的病（另一棵树，本轮只读、不碰）。
//  本重解两份设计的盘半径都固定 30.0 mm ⇒ **不进那一段坏带**，所以这一跑跑得出数；
//  但导航网格的那个病修好之后，导航那一遍的定位可能会变 ⇒ 本文件的数是**修复前的基线**，
//  修完要重跑对拍，不许把它当成「修好之后也是这个数」。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//
//    ① 有可行设计 ⇒ 印各旋钮值（含圆盘保温 mm／层数）、三关、铂重、四片窗口宽（含 0 层 = 裸舌）、
//       与 §0.-9 那份 0.1 格（W08 4245 g／W06 3379 g）的差。
//    ② 没有 ⇒ 印**最接近的设计**（停机那一份）与**逐条缺口**，不改上限、不退回旧值充数、不挑更粗的网格。
//    ③ 判不了 ⇒ **点名**（哪一片、哪条判据、为什么判不了）；判不了既不当过也不当不过。
//    ④ 落格门（**断言**）：解出来的舌保温必须落在 {裸舌 InsLoMm} ∪ {每层 0.5 mm 的正整数倍} 上；
//       圆盘保温必须是每层 0.5 mm 的整数倍 —— 现场是一层一层缠的，落不到格上就是缠不出来。
//    ⑤ 时间闸跑前写死：求解 4.0 h，可行窗口用生产默认那一道（InsulWindow.Options.Cap）。
//       超时即切断并报「被时间闸切断」，不许装作跑完了。
//    ⑥ 与 §0.-11 那一跑（圆盘保温 10 mm）并列一张表时**标明不是同一次运行、也不是同一份代码**。
//
//  ⚠ 本文件不另写一条算路：求解走 Solver.Solve、三关走 FinalCheck.Run、窗口走 InsulWindow.Measure、
//    保温方案表走 InsulationPlan.Text、判词走 Solver.VerdictOf、判据行走 Criteria.OneLine ——
//    一处生产配方都不手抄（门不许手抄生产配方）。
// ════════════════════════════════════════════════════════════════════════════

public abstract class R48MMergedResolveBase
{
    protected readonly ITestOutputHelper _o;
    protected R48MMergedResolveBase(ITestOutputHelper o) { _o = o; }

    /// <summary>求解时间闸（跑前写死）。</summary>
    private static readonly TimeSpan SolveCap = TimeSpan.FromHours(4.0);

    /// <summary>
    /// 与界面「自动定厚」同一份选项（R48LSolverInLineGateTests 守着 MaxRounds = 40／AllowTabCuts = false
    /// ＝ 界面下拉预设「不挖舌孔」），外加第二遍**判决网格**（尺寸与半径都取 MeshVerify.RequiredMeshFor，本文件不另写配方）。
    /// </summary>
    private static SolverOptions ProductionOptions(double fineMm, double radiusMm)
        => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = fineMm, FineRadiusMm = radiusMm };

    /// <summary>§0.-11 那一跑（圆盘保温 10 mm、按裕度收紧的停机容差、旧段解地板）—— 只作对照，标明不是同一次运行。</summary>
    private static (string File, string Line) Ref10(string which) => which == "W08"
        ? ("R48_L_圆盘保温10_重解_W08_本次开跑于2026-09-18_052851.txt",
           "求解 125.0 分钟／场解 55 次；**不可行**，停在「片3 舌保温 在 13.036 处解不出来 ⇒ 二分中止」（外层耦合未收敛）；带玻璃稳态铂重 4245 g")
        : ("R48_L_圆盘保温10_重解_W06_本次开跑于2026-09-18_052851.txt",
           "求解 143.5 分钟／场解 85 次；**不可行**，停在片1「最热铂高出热偶读数」—— 法兰侧九根旋钮穷尽；带玻璃稳态铂重 3750 g");

    /// <summary>§0.-9 那两份 0.1 格的可交付设计的铂重 g（更早、另一份代码、另一次运行）—— 只作对照。</summary>
    private static double Ref01G(string which) => which == "W08" ? 4245 : 3379;

    protected void Run(string which)
    {
        var seed = which == "W08" ? DesignSpec.W08.Clone()
                 : which == "W06" ? DesignSpec.W06.Clone()
                 : throw new ArgumentException(which);
        // ★ 一个旋钮都不动：五个旋钮求解器开头一律丢掉（解与初值无关），
        //   圆盘保温、保温开关、温差预算全部取生产默认 —— 本文件不给任何「起点」。
        var p = new DesignInputs();

        string file = DeliverableOut.Stamped($"R48_M_合并树重解_网格修复前基线_{which}.txt");
        var totalSw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() => File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var o0 = new SolverOptions();
        double q = Solver.KnobQuantum(o0, Solver.Knob.Insul);
        var navCase = new LineCase();
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed);
        var opt = ProductionOptions(reqFine, reqRadius);
        var winDefaults = new InsulWindow.Options();

        W($"R48 M 合并树重解　**网格修复前基线**　输入几何「{seed.Name}」（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W();
        W("═══════ ★ 为什么标「网格修复前基线」 ═══════");
        W("同机另一棵树（r48_N）正在修导航网格 30.75～31.0 那一段的病，本轮只读、不碰它。");
        W($"本设计盘半径 {seed.DiscRadiusMm:0.#} mm，**不进那一段坏带** ⇒ 这一跑跑得出数；");
        W("但导航那一遍的定位在网格修好之后可能会变 ⇒ 本文件的每个数都是**修复前**的基线，修完要重跑对拍。");
        W("不许把它读成「修好之后也是这个数」。");
        W();
        W("═══════ 现口径（每一项都从生产件读，本文件不抄常数）═══════");
        W($"舌保温图纸格　　{q:0.###} mm（SolverOptions.QuantInsulMm ＝ 包法每层 InsulationSearch.LayerMm {InsulationSearch.LayerMm:0.###} mm）；"
          + $"合法值 = {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{q:0.###} 的正整数倍}}；舌保温上界 {o0.InsHiMm:0.#} mm");
        W($"圆盘保温　　　　**自由设计变量**：本轮取生产默认 {seed.FlangeInsulMm:0.#} mm（{InsulationSearch.LayersText(seed.FlangeInsulMm)}），"
          + $"界面上界 {DesignSpec.FlangeInsulMaxMm:0.#} mm（唯一来源 DesignSpec.FlangeInsulMaxMm）；保温开关 {(seed.FlangeInsulated ? "开" : "关")}");
        W($"接合区缠绕圈数　**只提示、不卡交付**（参考行）：{WrapLimits.TurnsLine("圆盘区", seed.FlangeInsulMm)}");
        W($"　出处：{WrapLimits.SourceNote}");
        W($"　口径：{WrapLimits.PlanOnlyNote}");
        W($"停机容差　　　　**绝对目标** {navCase.CoupleTolK:0.###} K（LineCase.CoupleTolK；按裕度收紧默认{(navCase.CoupleTolFromMargin ? "开" : "关")}）；"
          + "认证误差 = 放大 × 停机残差，随结果交下游");
        W($"温差预算　　　　管根低于热偶读数 允许值 {p.ColdUnderTcAllowK:0.#} K／最热铂高出热偶读数 允许值 {p.HotOverTcAllowK:0.#} K（参数表默认，本轮一位没动）");
        W("求解器第三态　　判不了（SolverResult.Undetermined）：**既不当过也不当不过**");
        W($"求解选项　　　　最多 {opt.MaxRounds} 轮、解法族 {(opt.AllowTabCuts ? "挖舌孔" : "不挖舌孔")}（＝界面下拉预设第 0 项）、格点走格上限 {opt.CertWalkMaxSteps} 格");
        W($"网格　　　　　　第一遍导航（细区 {navCase.MeshFineMm:0.0} mm／半径 {navCase.MeshFineRadiusMm:0.0} mm）定位；"
          + $"第二遍**判决网格**（细区 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm，MeshVerify.RequiredMeshFor）上重新二分求根");
        W("三关与可行窗口都跑在**判决网格**上（交付判定只认这一张）。");
        W();
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("　① 有可行设计 ⇒ 印各旋钮值（含圆盘保温 mm／层数）、三关、铂重、四片窗口宽（含 0 层 = 裸舌）、与 §0.-9 那份 0.1 格的差。");
        W("　② 没有 ⇒ 印最接近的设计（停机那一份）与逐条缺口；不改上限、不退回旧值充数、不挑更粗的网格。");
        W("　③ 判不了 ⇒ 点名（哪一片、哪条判据、为什么）；既不当过也不当不过。");
        W($"　④ 落格门（断言）：舌保温 ∈ {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{q:0.###} 的正整数倍}}；圆盘保温 = {q:0.###} 的整数倍。违反即红。");
        W($"　⑤ 时间闸：求解 {SolveCap.TotalHours:0.#} h；可行窗口 {winDefaults.Cap.TotalHours:0.#} h（生产默认）。超时即切断并报「被时间闸切断」。");
        W("　⑥ 与 §0.-11 那一跑（圆盘保温 10 mm）并列时标明不是同一次运行、也不是同一份代码。");
        W();
        W("═══════ 输入（内置设计只提供几何与工况；五个旋钮一律被求解器丢掉）═══════");
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"管保温 {seed.TubeInsulMm:0.#} mm ＋ 端部额外 {p.EndInsulExtraMm:0.#} mm（**不计接合区圈数** —— 用户 2026-09-17「其它地方(舌板与管)好缠绕」）");
        W($"牌号 {p.GradeName}");
        W();

        string live = Path.Combine(Path.GetTempPath(), $"R48_M_合并树重解_{which}_{DeliverableOut.RunStamp}_进行中.log");
        var probe = new SlowProbe(live);
        W($"进度活页（逐轮当场落盘；全文附在本文件末尾）：{live}");
        W();
        Flush();

        // ══════════ 求解 ══════════
        W("═══════ 求解（Solver.Solve：导航网格定位 + 判决网格上重新二分求根）═══════");
        Flush();
        var (sr, solveS, cut) = SolveWithCap(seed.Clone(), p, opt, probe, SolveCap, $"{which} 合并树重解");
        W($"求解耗时 {solveS / 60:0.0} 分钟（{solveS:0} s），场解 {(sr?.Solves ?? 0)} 次。");
        if (sr is null)
        {
            W(cut ? $"★ **被跑前写死的时间闸（{SolveCap.TotalHours:0.#} h）切断** ⇒ 本轮没有解出来的设计 ⇒ 三关、窗口、保温方案表都不跑（不许拿半成品冒充结果）。"
                  : "★ **求解器抛异常或没返回** ⇒ 本轮没有解出来的设计 ⇒ 三关、窗口、保温方案表都不跑。");
            AppendLive(sb, live);
            Finish(sb, file, totalSw, which);
            Assert.Fail($"{which}：求解没返回结果（{(cut ? "时间闸切断" : "异常")}）—— 报告已落盘：{file}");
            return;
        }

        W($"结论：{Solver.VerdictOf(sr)}");
        W($"停因：{sr.StopWhy}");
        if (sr.HitBound) W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.Undetermined) W("　⚠ **判不了**：" + sr.UndeterminedWhy.Replace("**", "") + "（既不当过也不当不过；再算一次不一定得到同一句话）");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        W($"终局网格：{(sr.FineRefined ? $"**做了第二遍** —— 细网格 {sr.FineMmUsed:0.000} mm（求根与终局复核都在判决的那张网格上）" : $"**没做第二遍** ⇒ 只在导航网格 {navCase.MeshFineMm:0.0} mm 上成立、**不可交付**")}");
        var solved = sr.Design;
        W();

        W($"── {(sr.Feasible ? "解出来的" : "停机时（＝最接近的设计）")}各旋钮的值，逐片");
        W("片\t板厚 mm\t舌保温 mm\t舌保温层\t环倍率 t₁\t外级倍率 t₂\t内级环宽 w₁ mm\t外级环宽 w₂ mm\t舌片厚 mm\t槽张角 °\t舌孔 R mm");
        for (int j = 0; j < solved.TabThickMm.Length; j++)
            W($"{j}\t{solved.TabThickMm[j]:0.00}\t{solved.TabInsulMm[j]:0.0}\t{InsulationSearch.LayersText(solved.TabInsulMm[j])}\t"
              + $"{solved.RingMul[j]:0.00}\t{F(solved.RingMul2[j], "0.00")}\t{F(solved.RingW1Mm[j], "0.0")}\t{F(solved.RingW2Mm[j], "0.0")}\t"
              + $"{F(solved.TongueThickMm[j], "0.00")}\t{solved.SlotSpanDeg[j]:0.#}\t{solved.TabHoleRMm[j]:0.0}");
        W($"圆盘保温（整线）{solved.FlangeInsulMm:0.#} mm（{InsulationSearch.LayersText(solved.FlangeInsulMm)}）　"
          + WrapLimits.TurnsLine("圆盘区", solved.FlangeInsulMm));
        W();

        // ── 落格门（④，断言）──
        W($"── 落格门：解出来的保温落在哪儿（舌保温合法集合 = {{裸舌 {o0.InsLoMm:0.00}}} ∪ {{{q:0.###} 的正整数倍}}；圆盘保温 = {q:0.###} 的整数倍）");
        var offGrid = new List<string>();
        for (int j = 0; j < solved.TabInsulMm.Length; j++)
        {
            double v = solved.TabInsulMm[j];
            bool bare = Math.Abs(v - o0.InsLoMm) < 1e-9;
            double layers = v / q;
            bool onGrid = Math.Abs(layers - Math.Round(layers)) < 1e-9;
            W($"　片{j}　舌保温 {v:0.000} mm　{(bare ? "裸舌（0 层）" : onGrid ? $"{Math.Round(layers):0} 层 × {q:0.###} mm" : "**既不是裸舌，也不是整数层** ⇒ 现场包不出来")}");
            if (!bare && !onGrid) offGrid.Add($"片{j} 舌保温 = {v:R}");
        }
        {
            double dl = solved.FlangeInsulMm / q;
            bool discOnGrid = Math.Abs(dl - Math.Round(dl)) < 1e-9;
            W($"　整线　圆盘保温 {solved.FlangeInsulMm:0.000} mm　{(discOnGrid ? $"{Math.Round(dl):0} 层 × {q:0.###} mm" : "**不是整数层** ⇒ 现场包不出来")}");
            if (!discOnGrid) offGrid.Add($"圆盘保温 = {solved.FlangeInsulMm:R}");
        }
        W(offGrid.Count == 0 ? "　⇒ 全部落在现场包得出来的格上。" : "　⇒ **有值包不出来**：" + string.Join("、", offGrid));
        W();
        Flush();

        // ══════════ 终验三关（走生产件 FinalCheck.Run；判决网格）══════════
        W($"═══════ 终验三关（都跑在**判决网格** 细区 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm 上）═══════");
        W("次序按用户 2026-09-15/16 定的：升温全程 → 带玻璃稳态 → 空管到温 → 铂重。");
        W("⚠ 第二关就是下面这一份带玻璃稳态解（不重跑）；第一、三关走生产的终验件 FinalCheck.Run。");
        Flush();

        probe.Report("── 第二关 带玻璃稳态（判决网格）");
        var swG = Stopwatch.StartNew();
        var lcG = solved.BuildCase(p);
        Solver.ApplyCaseMesh(lcG, opt);
        var glass = SafeRun(lcG, probe);
        swG.Stop();
        W($"── 第二关 带玻璃稳态　网格 细区 {lcG.MeshFineMm:0.000} mm／半径 {lcG.MeshFineRadiusMm:0.0} mm，单元 {glass.MeshCells}");
        W($"结论：{Verdict(glass)}");
        W($"耗时 {swG.Elapsed.TotalSeconds:0} s，铂重 {F(glass.TotalMassG, "0")} g，耦合 {glass.CoupleRounds} 轮、容差 {glass.CoupleTolKUsed:0.000} K、"
          + $"停机残差 {F(glass.CoupleRemainK, "0.0000")} K、放大 {F(glass.CoupleAmpUsed, "0.0")}（闭式×1.1 {F(glass.CoupleAmpClosed, "0.0")}｜雅可比 {F(glass.CoupleAmpJacobian, "0.0")}）、"
          + $"**认证误差 {F(glass.CertErrK, "0.0000")} K**");
        W("  交付判据逐条：");
        foreach (var ck in glass.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Criteria.OneLine(ck));
        Flush();

        probe.Report("── 第一关 升温全程 + 第三关 空管到温（FinalCheck.Run）");
        FinalCheckRun? fc = null;
        try { fc = FinalCheck.Run(solved, p, glass, new FinalCheckOptions { Mesh = opt }, probe, CancellationToken.None); }
        catch (Exception ex) { W($"★ 终验件 FinalCheck.Run **抛异常** {ex.GetType().Name}：{ex.Message} ⇒ 第一、三关没有结果（没跑不等于过）。"); }
        var ramp = fc?.Ramp;
        var empty = fc?.EmptyTube;
        W();
        W($"── 第一关 升温全程　结论：{(ramp is null ? "**没跑**" : ramp.Verdict)}　耗时 {F(fc?.RampSeconds ?? double.NaN, "0")} s，{ramp?.Points.Length ?? 0} 个设定点");
        if (ramp is not null && ramp.VerdictDetail.Length > 0) W(ramp.VerdictDetail);
        W($"── 第三关 空管到温　结论：{(empty is null ? "**没跑**" : Verdict(empty))}　耗时 {F(fc?.EmptyTubeSeconds ?? double.NaN, "0")} s");
        if (empty is not null)
        {
            W("  交付判据逐条：");
            foreach (var ck in empty.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Criteria.OneLine(ck));
        }
        W();
        W("── 三关结论（生产写出器 FinalCheckReport.Conclusions，界面与安装报告印的就是这一份）");
        W(FinalCheckReport.Conclusions(glass));
        W();
        W($"── 第四步 铂重：管 {F(glass.TubeMassG, "0")} g ＋ 法兰 {F(glass.FlangeMassG, "0")} g ＝ 合计 **{F(glass.TotalMassG, "0")} g**（出自第二关那一次运行）");
        W($"　对照 §0.-9 那份 0.1 格的可交付设计 {Ref01G(which):0} g ⇒ 差 {glass.TotalMassG - Ref01G(which):+0;-0} g　"
          + "⚠ **不是同一次运行、也不是同一份代码**（那一跑在 0.1 格、旧容差口径、旧段解地板、圆盘保温另一档）。");
        W();
        Flush();

        // ══════════ 缺口 ══════════
        W("═══════ 缺口：离可行还差多少（第二关 带玻璃稳态，按裕度）═══════");
        W("判据\t实际／限值\t裕度\t位置\t判定");
        foreach (var ck in glass.Checks.Where(ck => ck.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"{Criteria.Plain(ck.Name)}\t{F(ck.Actual, "0.###")}／{ck.Limit:0.###} {ck.Unit}\t{F(Slack(ck), "+0.###;-0.###")}\t{ck.Where}\t"
              + (ck.Undetermined ? "**判不了**" : ck.Ok ? "过" : "**不过**"));
        var undet = glass.Checks.Where(c => c.Undetermined && c.Kind is CheckKind.HardSafety or CheckKind.Target).ToArray();
        W(undet.Length == 0 ? "　（本表没有判不了的判据）"
                            : "　★ **判不了**（既不当过也不当不过）：" + string.Join("；", undet.Select(c => Criteria.Plain(c.Name) + " @ " + c.Where)));
        W();
        Flush();

        // ══════════ 每片舌保温可行窗口 ══════════
        W("═══════ 每片舌保温的可行窗口（InsulWindow.Measure，判决网格；生产终验默认就跑这一步）═══════");
        Flush();
        probe.Report("── 每片舌保温可行窗口（判决网格）");
        InsulWindow.Result? win = null;
        var swW = Stopwatch.StartNew();
        try { win = InsulWindow.Measure(solved, p, null, probe, CancellationToken.None); }
        catch (Exception ex) { W($"★ 可行窗口 **抛异常** {ex.GetType().Name}：{ex.Message} ⇒ 这一步没有结果。"); }
        swW.Stop();
        if (win is not null)
        {
            W(win.Report());
            W($"　（时间闸 {winDefaults.Cap.TotalHours:0.#} h，{(win.Cut ? "**撞闸被切断 ⇒ 有片不完整，判不了**" : "没撞闸")}；本步耗时 {swW.Elapsed.TotalMinutes:0.0} 分钟）");
            W("　逐片窗口宽（含能落的层数档；0 层 = 裸舌也是一种做法）：");
            foreach (var pw in win.Plates)
                W($"　　{pw.Name}　解值 {pw.SolvedMm:0.###} mm　{(pw.SolvedFeasible ? $"窗口 [{pw.LoMm:0.###}, {pw.HiMm:0.###}]，宽 {F(pw.WidthMm, "0.###")} mm{(pw.OpenLo || pw.OpenHi ? "（边界没探到）" : "")}" : $"**解值自己就不过**（{pw.SolvedWhy}）")}"
                  + $"　可落档 {(pw.LayerText.Length == 0 ? "**一个都没有**" : string.Join("／", pw.LayerText))}");
        }
        W();
        Flush();

        // ══════════ 保温方案表 ══════════
        W("═══════ 保温方案表（InsulationPlan：材质 · 各区厚度 · 折合层数 · 圈数提示；界面 ③ 页页签、输出框、安装报告 5b 同源）═══════");
        try { W(InsulationPlan.Text(glass, solved, p)); }
        catch (Exception ex) { W($"★ 保温方案表 **抛异常** {ex.GetType().Name}：{ex.Message}"); }
        W();
        Flush();

        // ══════════ 并列对照 ══════════
        var (refFile, refLine) = Ref10(which);
        W("═══════ 并列：§0.-11 那一跑（圆盘保温 **10 mm**）与本次（圆盘保温 20 mm、现口径）═══════");
        W("⚠ **不是同一次运行、也不是同一份代码**：那一跑在停机容差改成绝对目标之前、段解地板降十倍之前，圆盘保温上限当时硬卡 10 mm。只作对照。");
        W("档\t圆盘保温 mm\t求解\t结论／铂重");
        W($"§0.-11（deliverable/{refFile}）\t10.0\t{refLine}");
        W($"本次（开跑于 {DeliverableOut.RunStamp}）\t{solved.FlangeInsulMm:0.#}\t求解 {solveS / 60:0.0} 分钟／场解 {sr.Solves} 次\t{Solver.VerdictOf(sr)}；带玻璃稳态铂重 {F(glass.TotalMassG, "0")} g");
        W();

        W("═══════ 一句话 ═══════");
        W($"{which}（圆盘保温 {solved.FlangeInsulMm:0.#} mm、舌保温格 {q:0.###} mm、绝对停机容差 {navCase.CoupleTolK:0.###} K、网格修复前基线）：");
        W(sr.Undetermined
            ? $"**判不了** —— {sr.UndeterminedWhy.Replace("**", "")}；既不当过也不当不过。停机那一份的三关：升温全程 {(ramp is null ? "没跑" : ramp.Verdict)}　带玻璃稳态 {Short(glass)}　空管到温 {Short(empty)}；铂重 {F(glass.TotalMassG, "0")} g。"
            : sr.Feasible
            ? $"**解出可行设计**，铂重 {F(glass.TotalMassG, "0")} g（与 §0.-9 那份 0.1 格 {Ref01G(which):0} g 差 {glass.TotalMassG - Ref01G(which):+0;-0} g，不同次运行）；"
              + $"舌保温逐片 {string.Join("/", solved.TabInsulMm.Select(v => v.ToString("0.0")))} mm；三关 升温全程 {(ramp is null ? "没跑" : ramp.Verdict)}　带玻璃稳态 {Short(glass)}　空管到温 {Short(empty)}；"
              + $"可行窗口 {(win is null ? "没量" : win.Verdict)}"
            : $"**没有可行点** —— {sr.StopWhy}；最接近的设计与逐条缺口见上表。三关：升温全程 {(ramp is null ? "没跑" : ramp.Verdict)}　带玻璃稳态 {Short(glass)}　空管到温 {Short(empty)}；铂重 {F(glass.TotalMassG, "0")} g。");
        W();
        W("═══════ 求解轨迹（Solver.Trace 全文：每轮的旋钮值与三条判据都在里面）═══════");
        foreach (var t in sr.Trace) W(t);
        W();
        AppendLive(sb, live);
        Finish(sb, file, totalSw, which);

        // ── 断言（跑前写死的 ④；其余各条只报不判）──
        Assert.True(sr.Solves > 0, "一次场解都没跑 —— 那不是求解");
        Assert.True(offGrid.Count == 0,
            $"{which}：解出来的保温有值落不到现场的格上（{string.Join("、", offGrid)}）—— 舌保温合法集合 = {{裸舌 {o0.InsLoMm}}} ∪ {{{q} 的正整数倍}}，圆盘保温 = {q} 的整数倍");
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
         : !r.Converged ? $"外层耦合未收敛（停机残差 {r.CoupleRemainK:0.0000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    private static string Short(LineResult? r)
        => r is null ? "没跑" : !r.Ok ? "判不了" : !r.Converged ? "未收敛" : r.AllOk ? "过" : "不过／判不了";

    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN
         : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static string F(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);

    /// <summary>把进度活页全文抄进交付文件 —— 活页在临时目录，交付文件要能自证「逐轮是怎么走的」。</summary>
    private static void AppendLive(StringBuilder sb, string live)
    {
        sb.AppendLine("═══════ 进度活页全文（逐轮当场落盘的那一份，含每行的时刻与间隔）═══════");
        try { sb.AppendLine(File.ReadAllText(live)); }
        catch (Exception ex) { sb.AppendLine($"（活页读不回来：{ex.GetType().Name}：{ex.Message}）"); }
    }

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw, string which)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（跑完 {DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve（两遍机制在 Solve 里）；判词 = Solver.VerdictOf；图纸格 = SolverOptions.QuantInsulMm（＝ InsulationSearch.LayerMm）；"
                    + "网格配方 = Solver.ApplyCaseMesh；网格无关口径 = MeshVerify.RequiredMeshFor；三关 = FinalCheck.Run（升温全程 = RampSweep.Run，空管与带玻璃 = LineRunner.Run）；"
                    + "三关结论 = FinalCheckReport.Conclusions；可行窗口 = InsulWindow.Measure；保温方案表 = InsulationPlan.Text；圈数提示 = WrapLimits.TurnsLine；判据行 = Criteria.OneLine。");
        sb.AppendLine($"本文件里**本次那一档的每个数**都来自这一次运行（{which}，同一进程）；标着出处文件的那几行是另一次跑，不混用。");
        sb.AppendLine("★ 网格修复前基线：r48_N 的导航网格修复完成后要重跑对拍。　2026-09-18，Opus 5");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine($"报告已落盘：{file}");
    }
}

// ★ 一份设计一个测试类：xUnit 的默认并行粒度是「集合」，一个类默认就是一个集合 ——
//   同一个类里的两行 InlineData 会排队跑，而这一轮两份设计各要几个小时。
[Trait("速度", "慢")]
public class R48MMergedResolveW08Tests : R48MMergedResolveBase
{
    public R48MMergedResolveW08Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 合并树现口径重解_网格修复前基线_W08() => Run("W08");
}

[Trait("速度", "慢")]
public class R48MMergedResolveW06Tests : R48MMergedResolveBase
{
    public R48MMergedResolveW06Tests(ITestOutputHelper o) : base(o) { }
    [Fact] public void 合并树现口径重解_网格修复前基线_W06() => Run("W06");
}
