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
//  R48 L 路 端到端 **细网格第二遍**：W08 与 W06 —— 2026-09-17，Opus 5。
//
//  ══ 为什么要有这一轮
//
//  R48LEndToEndTests 走的是**界面「自动定厚」的预设**（FineMm = 0，界面注释写「按钮要等得起」）：
//  只在导航网格（细区 2.0 mm／细区半径 50 mm）上求根，求解器自己就印着
//  「⚠ **没做第二遍**（FineMm = 0）—— 这个解只在导航网格上成立、**不可交付**」。
//  而 Solver 类注释 A⑬ 记的实测是：**根的位置随网格移动**，同一族判据在两张网格上到过 2.03 倍。
//  ⇒ 那一轮的三关数**不可交付**。本轮补第二遍：按 MeshVerify.RequiredMeshFor（网格无关口径，
//  与加密复算同一个来源）取本形状要求的细区尺寸与半径，走**生产入口现有的两遍机制**
//  （UI/LineDesignPage.FineResolveAsync → RunAsync(autoSize:true, fineMm:…) → Solver.Solve，
//   第二遍在同一个约束盒上重新二分求根 —— 没有「起点」这个概念）。
//
//  ══ 本轮跑什么（每档四段，**同一个进程、同一份代码**，不拼两份仪器输出）
//
//    A 段 导航档   ：= 上一跑那一份选项（FineMm = 0）→ 三关 → 铂重
//    B 段 细网格档 ：FineMm/FineRadiusMm = RequiredMeshFor → 三关**也跑在细网格上** → 铂重
//    C 段 纯网格效应：**B 段解出来的那一份设计**，② 再在导航网格上算一次
//                    （同一设计、只换网格 ⇒ 才谈得上「这条判据能不能在导航网格上判」；
//                     A 与 B 之间设计与网格同时在动，那个差不能用来归因）
//    D 段 **交付问题**：**A 段解出来的那一份设计**（界面「自动定厚」按钮给的那一份）
//                    三关改判在细网格上 —— 设计一位不动，动的只有判决网格。
//                    它回答的是「上一跑交付的那份数，经得起网格无关口径吗」。
//    判读：跑前写死在代码里（见 <see cref="RatioGate"/> 与 CompareRow），跑完不挪。
//
//  ══ 三关口径
//    ① 升温全程（RampSweep）　② 带玻璃稳态（LineRunner）　③ 空管到温稳态（LineRunner，
//    必备名单与降级注记从 LineResult.RequiredFor / StateDowngradeNote **现取**，不手抄）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LFineMeshEndToEndTests
{
    private readonly ITestOutputHelper _o;
    public R48LFineMeshEndToEndTests(ITestOutputHelper o) { _o = o; }

    // ──────────────────────────────────────────────────────────────────────
    //  跑前写死的判读门槛（用户 2026-09-17：判读写在跑前，门槛写死、跑完不挪）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>倍数门槛：同一设计两张网格上，某条判据的裕度之比 &gt; 2 倍（或 &lt; 0.5 倍）即点名。</summary>
    private const double RatioGate = 2.0;

    /// <summary>C 段与 D 段逐条对比的判据：前四条 = 决 103（业主 2026-09-24，HANDOVER §0.-27）的稳态硬判据；后三条 = 决 103 前的硬判据（现为参考项），照旧陈列以便与 09-17～09-23 的证据档对读。
    /// 2026-09-24 加前四条（变因 = 决 103 判据换向；此前只列后三条）。交付结论不由这张表定，由三关 AllOk 定。</summary>
    private static readonly string[] CompareKeys =
    {
        LineResult.Key.HotOverContact, LineResult.Key.TubeToFlangeHeat, LineResult.Key.LocalStab, LineResult.Key.FlangeStab,
        LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux,
    };

    /// <summary>求解时间闸（导航档）：超过就切断，报「被时间闸切断」，不许装作跑完了。
    /// ★ 2026-09-23 业主决定（决 04 (b)，HANDOVER §0.-21）：2.5 h → 4.0 h。理由：W08 导航档在 4 核云端与三条长跑同机并跑时 2.5 h 内没跑完（§0.-17 庚，07:16 那跑被切断）；
    ///   绝对时长受机器影响，4 h 是给同机并跑留的余量。这是时间闸，不是判据阈值；本机空闲时若 2.5 h 内能跑完，结果不受影响。</summary>
    private static readonly TimeSpan NavCap = TimeSpan.FromHours(4.0);

    /// <summary>求解时间闸（细网格档）。</summary>
    private static readonly TimeSpan FineCap = TimeSpan.FromHours(7.0);

    /// <summary>
    /// 求解器选项 = **生产界面那一份**：
    /// 导航档 = 「自动定厚」按钮预设（<c>MaxRounds = 40</c>、不挖舌孔、<c>FineMm = 0</c>）；
    /// 细网格档 = 「◆ 细网格重解」那条路（<c>UI/LineDesignPage.FineResolveAsync</c>）——
    /// 同一份选项再把 <c>FineMm</c>／<c>FineRadiusMm</c> 换成 <see cref="MeshVerify.RequiredMeshFor"/> 给的那一对。
    /// </summary>
    private static SolverOptions NavOptions() => new SolverOptions { MaxRounds = 40, AllowTabCuts = false };

    private static SolverOptions FineOptions(double fineMm, double radiusMm)
        => new SolverOptions { MaxRounds = 40, AllowTabCuts = false, FineMm = fineMm, FineRadiusMm = radiusMm };

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 端到端_细网格第二遍(string which)
    {
        var seed = which == "W08" ? DesignSpec.W08.Clone()
                 : which == "W06" ? DesignSpec.W06.Clone()
                 : throw new ArgumentException(which);
        var p = new DesignInputs();
        // ★ 决 104（业主 2026-09-25）：圆盘保温块最大厚度 = 参数表上限（缺省 10 mm）；设计记录默认 20 会被硬判据卡死 ⇒ 种子取上限（09-25 07:25 那跑是 20 mm，作废）
        seed.FlangeInsulated = true; seed.FlangeInsulMm = Math.Min(seed.FlangeInsulMm, p.DiscInsulCapMm); seed.DiscInsulMm = Array.Empty<double>();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_端到端_细网格_{which}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed, p);
        var navCase = new LineCase();
        // ★ F7′（2026-09-23，决 29 自适应；审查 P6）：导航档的细区半径也来自细区半径计划（Solver 在 FineRadiusMm = 0 时自己取计划初值），
        //   不再是整线算例缺省 50；C 段与 A 段三关的「导航网格」照同一个半径统一（Solver.ApplyCaseMesh 导航支：尺寸与粗区不动，只统一半径）。
        var navPlan = MeshVerify.FineRadiusPlanFor(seed, p);
        var navMesh = new SolverOptions { FineMm = 0, FineRadiusMm = navPlan.RadiusMm, RadiusPlan = navPlan };

        W($"R48 L 路　端到端 **细网格第二遍**　输入几何「{seed.Name}」（{which}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("上一轮（同日凌晨）只在导航网格上求根，求解器自己印着「没做第二遍 ⇒ 不可交付」。本轮补第二遍。");
        W("");
        W("═══════ 本轮跑什么（同一个进程、同一份代码，不拼两份仪器输出）═══════");
        W($"A 段 导航档　　：Solver.Solve，细区 {navCase.MeshFineMm:0.0} mm／粗区 {navCase.MeshCoarseMm:0.0} mm（整线算例缺省）／细区半径初值 {navPlan.RadiusMm:0.0} mm（细区半径计划，F7′；解后按热点放大，终值见求解轨迹）（界面「自动定厚」预设）→ 三关 → 铂重");
        W($"B 段 细网格档　：Solver.Solve，第二遍细区 {reqFine:0.000} mm／细区半径 {reqRadius:0.0} mm"
          + "（MeshVerify.RequiredMeshFor —— 与「◆ 加密复算」同一个来源），**三关也跑在这张网格上** → 铂重");
        W("C 段 纯网格效应：**B 段解出来的那一份设计**，② 再在导航网格上算一次 —— 同一设计、只换网格。");
        W($"D 段 **交付问题**：**A 段解出来的那一份设计**（= 界面「自动定厚」按钮给的那一份、上一跑交付的那一份），"
          + $"三关改判在细网格（{reqFine:0.000} mm／半径 {reqRadius:0.0} mm）上 —— 设计一位不动，动的只有判决网格。");
        W("　　（A 与 B 之间设计与网格**同时在动**，那个差不能用来回答「导航网格能不能判这条判据」；能回答的是 C 段与 D 段。）");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W($"对 C 段与 D 段列出的判据（决 103 四条硬判据 {Criteria.Plain(LineResult.Key.HotOverContact)}／{Criteria.Plain(LineResult.Key.TubeToFlangeHeat)}／{Criteria.Plain(LineResult.Key.LocalStab)}／{Criteria.Plain(LineResult.Key.FlangeStab)}，"
          + $"另陈列决 103 前的三条参考项 {Criteria.Plain(LineResult.Key.HotOverTc)}／{Criteria.Plain(LineResult.Key.ColdUnderTc)}／{Criteria.Plain(LineResult.Key.NetFlux)}，参考项的判读不进交付结论），");
        W($"　① 裕度**符号翻转**（过↔不过）⇒ 印「导航网格不可用于该判据」；");
        W($"　② 或 |裕度(细网格)| 与 |裕度(导航)| 之比 > {RatioGate:0.0} 倍（或 < {1 / RatioGate:0.00} 倍）⇒ 同上；");
        W($"　③ 判据**值**按同一把尺子并列印出，但结论以裕度那条为准；门槛写死 {RatioGate:0.0} 倍，不许改。");
        W("　④ 铂重变化**如实报**，不设门槛、不做解释性加工。");
        W($"求解时间闸（跑前写死）：导航档 {NavCap.TotalHours:0.#} h、细网格档 {FineCap.TotalHours:0.#} h —— 超时即切断，报「被时间闸切断」，不许装作跑完了。");
        W("");

        // ═══════ 输入 ═══════
        W("═══════ 输入（内置设计只提供几何与工况；五个旋钮一律被求解器丢掉）═══════");
        W($"管：内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，"
          + $"设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"工况：夹头 {seed.ClampTempC:0} °C，压接段长 {seed.ClampLengthMm:0.#} mm，"
          + $"设定电流密度 J {seed.JDesignAPerMm2:0.#} A/mm²（终验限值 {seed.JCheckAPerMm2:0.#}）");
        W($"设计输入表：默认（DesignInputs 默认构造）；服役 {p.DesignLifeHours} h。");
        W($"网格无关口径要求：细区 {reqFine:0.000} mm，细区半径 {reqRadius:0.0} mm"
          + "（MeshVerify.RequiredMeshFor(设计, 工艺参数)：细区尺寸 = 特征尺寸（舌根圆角与环宽）÷ 3；细区半径**初值** = max(盘半径, 管孔半径) + 设计热长度 ℓ_t —— F7′（2026-09-23，决 29 自适应）起不含舌长，解后按热点放大）。");
        W("　细区半径计划：" + navPlan.Describe());
        W("　⚠ 这一对（尺寸与半径**初值**）只由形状（圆角／环宽／盘半径／管孔半径）与管的热长度（控温点、管、保温、牌号）决定，与舌长无关；"
          + "求解器一根形状旋钮都不动 ⇒ 解前解后是同一对（下面当场核对）。半径的**终值**另由解出来的热点位置决定（只增不减），各段终值印在各自的求解轨迹里。");
        W("");

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_细网格_{which}_{stamp}_进行中.log");
        var probe = new Probe(live);
        W($"进度活页（临时，非交付物；逐行带时刻与自上一行的耗时）：{live}");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  A 段：导航档
        // ══════════════════════════════════════════════════════════════════
        W("═══════ A 段：导航档（= 上一跑那一份选项）═══════");
        var navOpt = NavOptions();
        var (navSr, navSolveS, navCut) = SolveWithCap(seed.Clone(), p, navOpt, probe, NavCap, "A 导航档");
        ReportSolve(W, "A 导航档", navSr, navSolveS, navCut, reqFine, reqRadius, navCase);

        GateSet? navGates = null;
        if (navSr is { Design: not null })
        {
            navGates = RunThreeGates(W, navSr.Design, p, meshOpt: NavMeshOf(navSr, navMesh), probe, "A 导航档", navCase);
            W();
        }

        // ══════════════════════════════════════════════════════════════════
        //  B 段：细网格档
        // ══════════════════════════════════════════════════════════════════
        W("═══════ B 段：细网格档（第二遍在细网格上重新二分求根；三关也跑在这张网格上）═══════");
        var fineOpt = FineOptions(reqFine, reqRadius);
        W("⚠ **两档的「第一遍」也不是同一张网格**（Solver.Solve 里 navOpt = opt.Clone() 之后只把 FineMm 归零，"
          + "**FineRadiusMm 原样带过去**）：");
        W($"　A 档第一遍：细区 {navCase.MeshFineMm:0.0} mm，细区半径 {navPlan.RadiusMm:0.0} mm（选项里 FineRadiusMm = {navOpt.FineRadiusMm:0.#} ⇒ F7′ 起 Solver 取细区半径计划初值，不再沿用整线算例缺省 {navCase.MeshFineRadiusMm:0.#}）");
        W($"　B 档第一遍：细区 {navCase.MeshFineMm:0.0} mm，细区半径 **{fineOpt.FineRadiusMm:0.0} mm**（选项里 FineRadiusMm = {fineOpt.FineRadiusMm:0.#} ⇒ 按网格无关口径统一）");
        W("　⇒ 两档第一遍**只差细区半径这一维**（尺寸、粗区、旋钮盒、轮数上限、解法族全同）。"
          + "这一维的历史实测记在 Solver.Solve 的注释里：同一设计、同一 2.0 mm，只差它 ⇒ 管孔净流入 +3.267（半径 50）对 −6.533（半径 59），差 9.8 W、符号相反。"
          + "F7′ 起两档第一遍的半径初值同为细区半径计划的初值；放大之后各自的终值见各自的求解轨迹。");
        var (fineSr, fineSolveS, fineCut) = SolveWithCap(seed.Clone(), p, fineOpt, probe, FineCap, "B 细网格档");
        ReportSolve(W, "B 细网格档", fineSr, fineSolveS, fineCut, reqFine, reqRadius, navCase);

        GateSet? fineGates = null;
        LineResult? fineGlassOnNav = null;
        if (fineSr is { Design: not null })
        {
            // 解前解后网格口径必须是同一对（形状没被动过）——当场核对，不是假设
            var (reqFine2, reqRadius2) = MeshVerify.RequiredMeshFor(fineSr.Design, p);
            W($"解后重算网格无关口径：细区 {reqFine2:0.000} mm／半径 {reqRadius2:0.0} mm　"
              + (Math.Abs(reqFine2 - reqFine) < 1e-9 && Math.Abs(reqRadius2 - reqRadius) < 1e-9
                 ? "⇒ 与解前**逐位相同**（形状旋钮确实一根没动）"
                 : "⇒ **与解前不同** ⚠ 形状被动过，本轮细网格口径的前提不成立"));
            fineGates = RunThreeGates(W, fineSr.Design, p, meshOpt: fineOpt, probe, "B 细网格档", navCase);
            W();

            // ══════════════════════════════════════════════════════════════
            //  C 段：纯网格效应
            // ══════════════════════════════════════════════════════════════
            W("═══════ C 段：纯网格效应（**B 段那一份设计**，② 再在导航网格上算一次）═══════");
            W("同一设计、同一工况、同一份代码，只换网格 —— 这是唯一能回答「这条判据能不能在导航网格上判」的对照。");
            var cSw = Stopwatch.StartNew();
            probe.Report("── C 段：B 段的设计在导航网格上再算一次 ②");
            var lcC = fineSr.Design.BuildCase(p);
            Solver.ApplyCaseMesh(lcC, NavMeshOf(fineSr, navMesh));   // F7′：导航网格 = 整线算例缺省尺寸 ＋ 细区半径计划的半径（与求解器第一遍同一支）
            fineGlassOnNav = SafeRun(lcC, probe);
            cSw.Stop();
            probe.Report($"── C 段结束，耗时 {cSw.Elapsed.TotalSeconds:0} s");
            W($"耗时 {cSw.Elapsed.TotalSeconds:0} s，网格：细区 {lcC.MeshFineMm:0.000} mm／粗区 {lcC.MeshCoarseMm:0.0} mm／细区半径 {lcC.MeshFineRadiusMm:0.0} mm。");
            W($"整线解：{(fineGlassOnNav.Ok ? "解出来了" : "**没解出来**（" + fineGlassOnNav.Message + "）")}，"
              + $"耦合收敛 {fineGlassOnNav.Converged}，剩余误差估计 {fineGlassOnNav.CoupleRemainK:0.000} K，铂重 {fineGlassOnNav.TotalMassG:0} g。");
            W();
            W("── 三条判据：同一设计，细网格 vs 导航网格（**判读按跑前写死的门槛**）");
            W("判据\t细网格 值\t细网格 裕度\t导航 值\t导航 裕度\t裕度之比\t判读");
            foreach (string key in CompareKeys)
                W(CompareRow(key, fineGates?.Glass, fineGlassOnNav));
            W("（「裕度」= 该条判据离限值还有多远，方向按判据自己的 LessIsBetter 取，正 = 还有余量、负 = 越限；"
              + "「裕度之比」= |细网格裕度| ÷ |导航裕度|。）");
            W();
        }

        // ══════════════════════════════════════════════════════════════════
        //  D 段：**导航档解出来的那份设计**，三关改判在细网格上
        // ══════════════════════════════════════════════════════════════════
        GateSet? navOnFine = null;
        if (navSr is { Design: not null })
        {
            W("═══════ D 段：**A 段（导航档）解出来的那份设计**，三关改判在细网格上 ═══════");
            W("这一段回答的是**交付问题**：界面「自动定厚」按钮给出来的那份设计（= 上一跑交付的那一份），");
            W($"拿到网格无关口径要求的那张网格（细区 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm）上重判，还过不过。");
            W("⚠ 设计一位没动（就是 A 段解出来的那一份），动的只有判决网格 ⇒ 与 A 段那三关的差**全部归网格**。");
            navOnFine = RunThreeGates(W, navSr.Design, p, meshOpt: fineOpt, probe, "D 导航档的设计·细网格上判", navCase);
            W("");
            W("── 三条判据：**同一份（导航档解出来的）设计**，细网格 vs 导航网格（判读按跑前写死的门槛）");
            W("判据\t细网格 值\t细网格 裕度\t导航 值\t导航 裕度\t裕度之比\t判读");
            foreach (string key in CompareKeys)
                W(CompareRow(key, navOnFine.Glass, navGates?.Glass));
            W("");
        }

        // ══════════════════════════════════════════════════════════════════
        //  并列表
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 导航 vs 细网格：旋钮终值、三关裕度、铂重（并列）═══════");
        W("── 旋钮终值（逐片）");
        W("档\t片\t板厚 mm\t舌保温 mm\t环倍率 t₁\t外级 t₂\t内级 r₁ mm\t外级 r₂ mm\t舌片厚 mm\t槽张角 °\t舌孔 R mm\t孔拉长比\t槽心 °(场定)\t孔心 x mm(场定)\t盘槽当地电流 °(场定)");
        if (navSr?.Design is { } dn) foreach (string row in KnobRows(dn)) W("A 导航档\t" + row);
        if (fineSr?.Design is { } df) foreach (string row in KnobRows(df)) W("B 细网格档\t" + row);
        W("");

        W("── 三关结论与铂重（「求根网格」= 那一档求根**实际**跑完的网格；不是选项上写了什么）");
        W("档\t设计来自\t判决网格\t求根网格（实际）\t① 升温全程\t② 带玻璃稳态\t③ 空管到温稳态\t铂重 g（② 那一次）");
        W($"A 导航档\tA 求根\t导航 {navCase.MeshFineMm:0.0} mm\t" + TerminalMesh(navSr, navCase) + "\t" + GateLine(navGates));
        W($"B 细网格档\tB 求根\t细网格 {reqFine:0.000} mm\t" + TerminalMesh(fineSr, navCase) + "\t" + GateLine(fineGates));
        W($"D 同 A 的设计\tA 求根\t细网格 {reqFine:0.000} mm\t" + TerminalMesh(navSr, navCase) + "\t" + GateLine(navOnFine));
        W("");

        W("── ② 带玻璃稳态的交付判据裕度（A 与 B 并列；两档的**设计与网格同时在动**，这张表只作陈列，不用来归因 —— 归因看 C 段与 D 段）");
        W("判据\tA 导航档 值/限值\tA 裕度\tB 细网格档 值/限值\tB 裕度");
        var keys = OrderedKeys(navGates?.Glass, fineGates?.Glass);
        foreach (string key in keys) W(TwoWayRow(key, navGates?.Glass, fineGates?.Glass));
        W("");

        W("── 铂重");
        double mNav = navGates?.Glass?.TotalMassG ?? double.NaN;
        double mFine = fineGates?.Glass?.TotalMassG ?? double.NaN;
        W($"导航 {Fmt(mNav, "0")} g（管 {Fmt(navGates?.Glass?.TubeMassG ?? double.NaN, "0")} + 法兰 {Fmt(navGates?.Glass?.FlangeMassG ?? double.NaN, "0")}）");
        W($"细网格 {Fmt(mFine, "0")} g（管 {Fmt(fineGates?.Glass?.TubeMassG ?? double.NaN, "0")} + 法兰 {Fmt(fineGates?.Glass?.FlangeMassG ?? double.NaN, "0")}）");
        W(double.IsNaN(mNav) || double.IsNaN(mFine)
          ? "变化：**算不出**（有一档没出结果）"
          : $"变化：{mFine - mNav:+0;-0} g（{(mNav > 0 ? ((mFine - mNav) / mNav * 100).ToString("+0.0;-0.0") : "—")} %）—— 如实报，不设门槛。");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  一句话
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 一句话 ═══════");
        W(OneLine(which, fineSr, fineGates, fineCut, reqFine));
        W(OneLineD(which, navSr, navGates, navOnFine, reqFine, navCase));
        W("");

        W("═══════ A 段求解轨迹（Solver.Trace 全文）═══════");
        foreach (var t in navSr?.Trace ?? new List<string>()) W(t);
        W("");
        W("═══════ B 段求解轨迹（Solver.Trace 全文）═══════");
        foreach (var t in fineSr?.Trace ?? new List<string>()) W(t);

        Finish(sb, file, totalSw);

        // 最低限度断言（判据不过是**结果**，不是测试失败 —— 这里只守「每一步都有结果」）
        Assert.NotNull(navSr);
        Assert.NotNull(fineSr);
        Assert.True(fineSr!.Solves > 0, "细网格档一次场解都没跑 —— 那不是求解");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  帮手
    // ══════════════════════════════════════════════════════════════════════

    private sealed class GateSet
    {
        public RampSweepResult? Ramp;
        public LineResult? Glass;
        public LineResult? Empty;
        public double RampS, GlassS, EmptyS;
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

    private static void ReportSolve(Action<string> W, string tag, SolverResult? sr, double seconds,
                                    bool cut, double reqFine, double reqRadius, LineCase navCase)
    {
        W($"求解耗时 {seconds / 60:0.0} 分钟（{seconds:0} s），场解 {(sr?.Solves ?? 0)} 次。");
        if (sr is null)
        {
            W(cut ? $"★ **被跑前写死的时间闸切断** ⇒ {tag} 没有解出来的设计 ⇒ 这一档的三关不跑（不许拿半成品冒充结果）。"
                  : $"★ **求解器抛异常或没返回** ⇒ {tag} 没有解出来的设计 ⇒ 这一档的三关不跑。");
            W("");
            return;
        }
        // R48 M（2026-09-18，Fable 5.1）：判词走生产唯一那一份 Solver.VerdictOf（判不了 ≠ 不可行；「再算一次会得到同一句话」只许挂在 HitBound）
        W($"结论：{Solver.VerdictOf(sr)}　停因：{sr.StopWhy}");
        if (sr.HitBound) W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.Undetermined) W("　⚠ 判不了：" + sr.UndeterminedWhy.Replace("**", "") + "（既不当过也不当不过；再算一次不一定得到同一句话）");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        if (sr.RadiusPlan is { } rpS) W("细区半径（F7′ 计划终态）：" + rpS.Describe());
        W($"终局网格：{(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm（第二遍求根与终局复核跑在判决的那张网格上）" : $"**没做第二遍** ⇒ 只在导航网格 {navCase.MeshFineMm:0.0} mm 上成立、**不可交付**")}");
        if (!sr.FineRefined)
            W($"　（本形状按网格无关口径要求 {reqFine:0.000} mm／半径 {reqRadius:0.0} mm；导航与细网格之间的差在历史实测上到过 2.03 倍。）");
        W("");
    }

    /// <summary>F7′（2026-09-23）：导航网格的选项 —— 尺寸照整线算例缺省，细区半径取那一次求解的计划终值（没有就取计划初值）。</summary>
    private static SolverOptions NavMeshOf(SolverResult sr, SolverOptions navMesh)
        => sr.RadiusPlan is { } rp && rp.TabLengthFactor == 0
            ? new SolverOptions { FineMm = 0, FineRadiusMm = rp.RadiusMm, RadiusPlan = rp }
            : navMesh;

    /// <summary>三关。<paramref name="meshOpt"/> = null ⇒ 导航网格（整线算例缺省）；FineMm = 0 且给了半径 ⇒ 导航网格统一半径；FineMm &gt; 0 ⇒ 三关都跑在那张网格上。</summary>
    private static GateSet RunThreeGates(Action<string> W, DesignSpec solved, DesignInputs p,
                                         SolverOptions? meshOpt, Probe probe, string tag, LineCase navCase)
    {
        var g = new GateSet();

        // ── ① 升温全程 ──
        W($"── ① 升温全程（{tag}；准静态轨迹，空管，夹头 = 设计的夹头温度）");
        var rampOpt = new RampSweepOptions { RunClampAlt = false, Mesh = meshOpt };
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ① 升温全程（{tag}）");
        g.Ramp = RampSweep.Run(solved, p, rampOpt, probe);
        sw.Stop(); g.RampS = sw.Elapsed.TotalSeconds;
        probe.Report($"── ① 结束（{tag}）：{g.Ramp.Verdict}，耗时 {g.RampS:0} s");
        W($"结论：{g.Ramp.Verdict}　耗时 {g.RampS:0} s，{g.Ramp.Points.Length} 个设定点，"
          + $"网格 = {(meshOpt is null ? $"导航（细区 {navCase.MeshFineMm:0.0} mm）" : meshOpt.FineMm <= 0 ? $"导航（细区 {navCase.MeshFineMm:0.0} mm／半径 {meshOpt.FineRadiusMm:0.0} mm）" : $"细网格（细区 {meshOpt.FineMm:0.000} mm／半径 {meshOpt.FineRadiusMm:0.0} mm）")}");
        if (g.Ramp.VerdictDetail.Length > 0) W(g.Ramp.VerdictDetail);
        W(g.Ramp.DisagreeNotes.Length == 0
          ? "两条电流口径（设计电流／该点实际电流）在每一点、每一片上**结论相同**。"
          : "两条电流口径**结论不同**的地方：" + string.Join("　", g.Ramp.DisagreeNotes));
        W("设定点\t夹头\t各段实际电流 A\t管J(实际)\t管J(设计)\t截面J(实际)\t截面J(设计)\t场有效\t备注");
        foreach (var pt in g.Ramp.Points)
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
        W($"（管 J 限值 {(g.Ramp.Points.Length > 0 && g.Ramp.Points[0].Segs.Length > 0 ? g.Ramp.Points[0].Segs[0].TubeJLimit.ToString("0.#") : "—")} A/mm²；"
          + $"截面 J 限值 {(g.Ramp.Points.Length > 0 && g.Ramp.Points[0].Flanges.Length > 0 ? g.Ramp.Points[0].Flanges[0].SectionJLimit.ToString("0.#") : "—")} A/mm²；两列都取该点最坏的那一片／那一段。）");
        W("");

        // ── ② 带玻璃稳态 ──
        W($"── ② 带玻璃稳态（{tag}）");
        var lcGlass = solved.BuildCase(p);
        if (meshOpt is not null) Solver.ApplyCaseMesh(lcGlass, meshOpt);
        sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ② 带玻璃稳态（{tag}）");
        g.Glass = SafeRun(lcGlass, probe);
        sw.Stop(); g.GlassS = sw.Elapsed.TotalSeconds;
        probe.Report($"── ② 结束（{tag}），耗时 {g.GlassS:0} s");
        W($"网格：细区 {lcGlass.MeshFineMm:0.000} mm／粗区 {lcGlass.MeshCoarseMm:0.0} mm／细区半径 {lcGlass.MeshFineRadiusMm:0.0} mm，网格单元 {g.Glass.MeshCells}");
        W($"结论：{Verdict(g.Glass)}");
        W($"耗时 {g.GlassS:0} s，铂重 {g.Glass.TotalMassG:0} g，耦合收敛 {g.Glass.Converged}，剩余误差估计 {g.Glass.CoupleRemainK:0.000} K。");
        W("  交付判据逐条：");
        foreach (var c in g.Glass.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
            W("    " + Line(c));
        W("");

        // ── ③ 空管到温稳态 ──
        W($"── ③ 空管到温稳态（{tag}）");
        W("口径（生产代码的原话，LineResult.StateDowngradeNote）：");
        W("  " + LineResult.StateDowngradeNote(true));
        W("本工况判定用的必备名单（LineResult.RequiredFor(空管到温稳态)）：");
        W("  " + string.Join("、", LineResult.RequiredFor(true).Select(q => Criteria.Plain(q.Prefix))));
        var lcEmpty = solved.BuildCase(p, emptyTube: true);
        if (meshOpt is not null) Solver.ApplyCaseMesh(lcEmpty, meshOpt);
        sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ③ 空管到温稳态（{tag}）");
        g.Empty = SafeRun(lcEmpty, probe);
        sw.Stop(); g.EmptyS = sw.Elapsed.TotalSeconds;
        probe.Report($"── ③ 结束（{tag}），耗时 {g.EmptyS:0} s");
        W($"网格：细区 {lcEmpty.MeshFineMm:0.000} mm／粗区 {lcEmpty.MeshCoarseMm:0.0} mm／细区半径 {lcEmpty.MeshFineRadiusMm:0.0} mm，网格单元 {g.Empty.MeshCells}");
        W($"结论：{Verdict(g.Empty)}");
        W($"耗时 {g.EmptyS:0} s，铂重 {g.Empty.TotalMassG:0} g，耦合收敛 {g.Empty.Converged}，剩余误差估计 {g.Empty.CoupleRemainK:0.000} K。");
        W($"工况位（LineResult.EmptyTube，由整线算例盖章）：{g.Empty.EmptyTube}");
        W($"场的有效性（不是判据、两态都要过）：{(g.Empty.FieldUndeterminedReasons.Length == 0 ? "没有判不了的" : string.Join("；", g.Empty.FieldUndeterminedReasons))}");
        W($"必备名单里缺席的：{(g.Empty.MissingChecks.Length == 0 ? "无" : string.Join("、", g.Empty.MissingChecks.Select(Criteria.Plain)))}");
        W("  逐条（卡交付的与只作参考的一起印，值照常算）：");
        foreach (var c in g.Empty.Checks) W("    " + Line(c));
        var refFailed = g.Empty.Checks
            .Where(c => c.Kind == CheckKind.Reference && !c.Withheld && !c.Undetermined && !c.Ok)
            .Select(c => $"{Criteria.Plain(c.Name)} {c.Actual:0.###}/{c.Limit:0.###} {c.Unit}").ToArray();
        W(refFailed.Length == 0
          ? "  （本工况没有「只作参考却越限」的条目。）"
          : "  旧口径（两态都卡这三条）下会被判不过的，是这几条在空管到温稳态降为参考量的：" + string.Join("；", refFailed));
        W("");

        W($"── 铂重（{tag}）：管 {g.Glass.TubeMassG:0} g + 法兰 {g.Glass.FlangeMassG:0} g = 合计 {g.Glass.TotalMassG:0} g"
          + $"（出自 ② 那一次；③ 那一次算出 {g.Empty.TotalMassG:0} g，同一几何、应逐位相同）");
        return g;
    }

    private static LineResult SafeRun(LineCase lc, Probe probe)
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

    /// <summary>裕度：方向按判据自己的 LessIsBetter 取（不在这里另写一份方向表）。正 = 还有余量。</summary>
    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN
         : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static ConstraintOut? Get(LineResult? r, string key)
        => r?.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    /// <summary>C 段一行：同一设计、两张网格。判读按 <see cref="RatioGate"/>，跑前写死。</summary>
    private static string CompareRow(string key, LineResult? fine, LineResult? nav)
    {
        var cf = Get(fine, key);
        var cn = Get(nav, key);
        double sf = Slack(cf), sn = Slack(cn);
        string ratio = double.IsNaN(sf) || double.IsNaN(sn) || Math.Abs(sn) < 1e-12
                     ? "—" : (Math.Abs(sf) / Math.Abs(sn)).ToString("0.000");
        var reasons = new List<string>();
        if (double.IsNaN(sf) || double.IsNaN(sn)) reasons.Add("**有一边判不了** ⇒ 不许当过，也不许当「差不多」");
        else
        {
            if (Math.Sign(sf) != Math.Sign(sn)) reasons.Add("**裕度符号翻转**（过↔不过）");
            if (Math.Abs(sn) > 1e-12)
            {
                double r = Math.Abs(sf) / Math.Abs(sn);
                if (r > RatioGate || r < 1 / RatioGate) reasons.Add($"**裕度差 {r:0.00} 倍**（门槛 {RatioGate:0.0}）");
            }
            double vf = cf?.Actual ?? double.NaN, vn = cn?.Actual ?? double.NaN;
            if (!double.IsNaN(vf) && !double.IsNaN(vn))
            {
                if (Math.Sign(vf) != Math.Sign(vn) && Math.Abs(vn) > 1e-12) reasons.Add("值也**符号翻转**");
                else if (Math.Abs(vn) > 1e-12 && (Math.Abs(vf) / Math.Abs(vn) > RatioGate || Math.Abs(vf) / Math.Abs(vn) < 1 / RatioGate))
                    reasons.Add($"值差 {Math.Abs(vf) / Math.Abs(vn):0.00} 倍");
            }
        }
        string verdict = reasons.Count == 0
                       ? $"两张网格上一致（裕度之比在 {1 / RatioGate:0.00}–{RatioGate:0.0} 之间且不翻符号）"
                       : "**导航网格不可用于该判据** —— " + string.Join("；", reasons);
        return $"{Criteria.Plain(key)}\t{Fmt(cf?.Actual ?? double.NaN, "0.###")}\t{Fmt(sf, "+0.###;-0.###")}\t"
             + $"{Fmt(cn?.Actual ?? double.NaN, "0.###")}\t{Fmt(sn, "+0.###;-0.###")}\t{ratio}\t{verdict}";
    }

    private static string[] OrderedKeys(LineResult? a, LineResult? b)
    {
        var keys = new List<string>();
        foreach (var r in new[] { a, b })
            foreach (var c in r?.Checks ?? Array.Empty<ConstraintOut>())
                if (c.Kind is CheckKind.HardSafety or CheckKind.Target && !keys.Contains(c.Name)) keys.Add(c.Name);
        return keys.ToArray();
    }

    private static string TwoWayRow(string key, LineResult? nav, LineResult? fine)
    {
        var cn = Get(nav, key);
        var cf = Get(fine, key);
        string One(ConstraintOut? c) => c is null ? "—" : $"{Fmt(c.Actual, "0.###")}/{c.Limit:0.###} {c.Unit}";
        return $"{Criteria.Plain(key)}\t{One(cn)}\t{Fmt(Slack(cn), "+0.###;-0.###")}\t{One(cf)}\t{Fmt(Slack(cf), "+0.###;-0.###")}";
    }

    private static IEnumerable<string> KnobRows(DesignSpec s)
    {
        for (int j = 0; j < s.TabThickMm.Length; j++)
        {
            var rr = s.RingRadiiOf(j);
            yield return $"{j}\t{s.TabThickMm[j]:0.00}\t{s.TabInsulMm[j]:0.0}\t{s.RingMul[j]:0.00}\t{s.RingMulOuter(j):0.00}\t"
                       + $"{(rr.Length > 0 ? rr[0].ToString("0.0") : "—")}\t{(rr.Length > 1 ? rr[1].ToString("0.0") : "—")}\t"
                       + $"{(j < s.TongueThickMm.Length && !double.IsNaN(s.TongueThickMm[j]) ? s.TongueThickMm[j].ToString("0.00") : "同基板")}\t"
                       + $"{(j < s.SlotSpanDeg.Length ? s.SlotSpanDeg[j].ToString("0.#") : "—")}\t"
                       + $"{(j < s.TabHoleRMm.Length ? s.TabHoleRMm[j].ToString("0.0") : "—")}\t"
                       + $"{(j < s.TabHoleAspect.Length ? s.TabHoleAspect[j].ToString("0.00") : "—")}\t"
                       + $"{Num(j < s.SlotCenterDeg.Length ? s.SlotCenterDeg[j] : double.NaN)}\t"
                       + $"{Num(j < s.TabHoleXMm.Length ? s.TabHoleXMm[j] : double.NaN)}\t"
                       + $"{Num(j < s.DiscCutRotDeg.Length ? s.DiscCutRotDeg[j] : double.NaN)}";
        }
    }

    private static string TerminalMesh(SolverResult? sr, LineCase navCase)
        => sr is null ? "**没有解**"
         : sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm"
         : $"导航 {navCase.MeshFineMm:0.0} mm（没做第二遍）";

    private static string GateLine(GateSet? g)
        => g is null ? "—\t—\t—\t—"
         : $"{g.Ramp?.Verdict ?? "—"}\t{(g.Glass is null ? "—" : g.Glass.AllOk ? "过" : "不过／判不了")}\t"
         + $"{(g.Empty is null ? "—" : g.Empty.Ok && g.Empty.Converged && g.Empty.AllOk ? "过" : "不过／判不了")}\t"
         + $"{Fmt(g.Glass?.TotalMassG ?? double.NaN, "0")}";

    private static string Three(GateSet? g)
        => g is null ? "三关没跑"
         : $"① {g.Ramp?.Verdict ?? "—"}　② {(g.Glass?.AllOk == true ? "过" : "不过／判不了")}　③ {(g.Empty is { Ok: true, Converged: true, AllOk: true } ? "过" : "不过／判不了")}";

    private static string OneLine(string which, SolverResult? sr, GateSet? g, bool cut, double reqFine)
    {
        if (sr is null)
            return $"{which}（B 细网格档）：{(cut ? "**被跑前写死的时间闸切断**" : "**没返回结果**")} ⇒ 这一档**没有可交付的数**。";
        if (!sr.FineRefined)
            return $"{which}（B 细网格档）：**第二遍（细网格 {reqFine:0.000} mm）从来没有开始** —— "
                 + $"第一遍（导航网格，细区半径已按网格无关口径统一）就{(sr.HitBound ? "**结构性停机**" : sr.Undetermined ? "**判不了**" : "没走通")}："
                 + $"{sr.StopWhy}　⇒ 生产的两遍机制在这里按设计不做第二遍，**这一档拿不出可交付的设计**。"
                 + $"（下面 B 段那三关判的是第一遍停在哪儿的那份设计，只作陈列，不是解。）";
        return $"{which}（B 细网格档）：细网格（{sr.FineMmUsed:0.000} mm）上{(sr.Feasible ? "解出可行设计" : "**没解出可行设计**")}，{Three(g)}，"
             + $"铂重 {Fmt(g?.Glass?.TotalMassG ?? double.NaN, "0")} g（求根、终局复核与三关同一张网格）。";
    }

    /// <summary>D 段一句话：导航档解出来的那份设计，拿到细网格上重判还过不过 —— 这是**交付问题**。</summary>
    private static string OneLineD(string which, SolverResult? navSr, GateSet? onNav, GateSet? onFine,
                                   double reqFine, LineCase navCase)
    {
        if (navSr?.Design is null || onFine is null)
            return $"{which}（D 段）：导航档没有解 ⇒ 没得重判。";
        bool okNav = onNav?.Glass?.AllOk == true;
        bool okFine = onFine.Glass?.AllOk == true;
        return $"{which}（D 段，**交付问题**）：导航档（细区 {navCase.MeshFineMm:0.0} mm）解出来的那份设计，"
             + $"在导航网格上 ② {(okNav ? "过" : "不过／判不了")}、在细网格（{reqFine:0.000} mm）上 ② {(okFine ? "过" : "不过／判不了")}"
             + $"　⇒ {(okNav && !okFine ? "**导航网格上的「过」在细网格上不成立** —— 那份设计不可交付。"
                     : okNav && okFine ? "两张网格上都过。"
                     : "导航网格上本来就没过。")}"
             + $"　细网格上三关：{Three(onFine)}。";
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

    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("0.00");
    private static string Num(double v) => double.IsNaN(v) ? "NaN" : v.ToString("0.###");
    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);
    private static double Worst(IEnumerable<double> vs)
    {
        double w = double.NaN;
        foreach (double v in vs) { if (double.IsNaN(v)) continue; if (double.IsNaN(w) || v > w) w = v; }
        return w;
    }

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve（Core/Solver.cs，两遍机制在 Solve 里）；网格配方 = Solver.ApplyCaseMesh（全仓唯一一份）；"
                    + "网格无关口径 = MeshVerify.RequiredMeshFor；升温全程 = RampSweep.Run；带玻璃与空管 = LineRunner.Run。");
        sb.AppendLine("每个数来自同一次运行（A/B/C 三段同一进程），不拼两份输出。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
