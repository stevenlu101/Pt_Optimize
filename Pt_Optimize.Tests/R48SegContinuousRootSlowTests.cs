using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  2026-09-23（SEG，决 97 A「段电流连续根」）慢门与「开 − 关」归因。快门、改法与改回参数见 R48SegContinuousRootTests 头注。
//
//  门 b（整线，慢）：同一整线算例 = R48MStopTolCostTests 门 c 那一份（W08 导航设计 R48LW08NavDesign、圆盘保温 20 mm 逐片留空、判决网格 MeshVerify.RequiredMeshFor、
//    生产停机口径、量雅可比），与「多调一次 Solver.ApplySectionFloor」的同一算例（= 门 f 的 SolveLine 那条路；归因报告 §5.1：首轮判据值差 2.3e−7 K、净流入差 1e−6 W、段电流 ≤ 3e−12 A）
//    两跑停机轮数差 ≤ 1 轮。
//    界 1 **没有出处，待决定**（2026-09-23 审查后改写；原来这里写「界的出处（推导，不是拟合）：连续根 ⇒ G(x) 对输入连续（段解层由快门 c 证）⇒ … 至多挪一轮」，作废）。原推导三处不成立：
//    ① G 没有真连续。连续根把段电流的二分格台阶（约 2.6e−3 A）降到 1e−4～5e−4 A 量级；残余台阶来自段解 Bvp1D／Picard 按容差停机（f(I) 分段光滑、带约 1e−4 K 跳跃，
//       审查探针实测）；壳体层迭代停机另有 ~1e−5 K 截断（归因报告 §3.1）。G 分段连续，没有真连续；快门 c 只量了 TSetC 方向、默认输入一处的台阶上界，不证连续。
//    ② 推导里「乘以轨迹的放大」没有量化；扰动是有限的 1e−6 时，不知道放大率就推不出任何有限的轮数界。
//    ③ 停机量 q_k 对 k 不单调（逐轮轨迹 门c算例_开：第 23 轮 9.7、第 24 轮 26.2、第 28 轮 68.7），就算扰动 → 0，连续性也只给出「固定轮号的差 → 0、首达轮一般不动」，推不出「至多一轮」。
//    所以 1 是另加的容许量；归因报告 §5.3(b) 只要求「≤ 某个写明出处的界」，没有给 1。门名里的「不超1轮」随业主对界的决定一起改（【待决定】）。
//    改回（中点）时 G 分段常数（2.6e−3 A 一格），扰动可以改变哪一轮翻格，首达轮是彩票（归因报告：119 对 98）。
//    「改回 ⇒ 红」对照：同一对算例在改回口径下跑，轮数差必须 > 1（归因报告的 119／98 这一对）。改回两跑还必须复现改前（Linux 记录）：门 c 生产那一行 119 轮 5.419／2.715／−0.102
//    （deliverable/R48_M_停机容差绝对目标_细网格成本_本次开跑于2026-09-23_091703.txt）、门 f 盘20 判决 98 轮 5.419／2.715／−0.102（deliverable/网格修复_门f_现役数_W08_本次开跑于2026-09-23_065617.txt）。
//    两条温度判据在两跑之间的差 ≤ 两跑认证误差之和（认证误差的定义：放大 × 停机残差 ≥ 离不动点的距离；两跑是同一不动点的两次逼近 + 2e−7 K 的扰动）。
//    这一半是**一致性核对，分辨不出改回**：首跑里改回那一对代入同一判法也过（8.3e−5／6.2e−5 K ≤ 0.021 K），开的差（7.1e−5／1.0e−4 K）不比改回小。改回对照只靠轮数差。
//    ★ 首跑（2026-09-23 14:30～15:28，Linux，…门b与门f两行开关归因_本次开跑于2026-09-23_140005.txt）**红**：开 33 对 29，差 4 > 1。阈值 1 没挪，红着交；改回对照 119 对 98（差 21）照旧成立。
//      逐轮轨迹（同名「逐轮轨迹」目录）：停机量第 3 轮相对差 2e−6，第 5 轮 3e−3，第 12～15 轮 O(1)；同期两跑三段电流之差第 1～10 轮 ≤ 2.4e−5 A（第 5 轮 1.1e−6 A），
//      第 11 轮以后才到 1.3e−4 A 以上 —— 早期分叉不是段电流跨台阶。4 轮的来源**未定位**：段解停机台阶（段电流与端温）、壳体停机截断、Anderson 放大都没有排除。
//      2.6e−3 A 整格翻格已去掉（改回差 21 轮 → 开差 4 轮，各抽一次）。补不补判别跑（SegPicardTolK、SegBvpTolK 各收紧 10 倍，只给门用）【待决定】。
//    ★ 审查后本档只改了注释与写档文字（断言、阈值、算例一处没动），在终版源码上重跑一次，见实施记录「审查后修改」节。
//  归因（门 f 两行开 − 关）：W08 盘10／盘20 判决（SolveLine 那条路）开（连续根）与关（改回）各跑一次，判据值、停机轮数、耗时并列；关的两行必须复现 065617 那两行（Linux 记录，三位小数与轮数）。
//  转储诊断：R48LineDumpTests 的六个算例在改回与连续根下各转储一次（去文字口径）。改回的去文字 SHA 去掉新增公开成员那几行之后，必须等于改前（分支头之前 dfe68a8，
//    deliverable/R48_整线全量转储_SHA256汇总_本次开跑于2026-09-23_084640_12039.txt）；去掉的行只有：本次新加的 DesignInputs.SegCurrentContinuousRoot、§0.-20 新加的 ShellMesh 四个诊断字段
//    （§0.-20 已证不动数值场）。连续根对改回的数值位移逐行印出（最大绝对位移、分类），这就是「会动全仓所有经 FindCurrent 的数」的实量。
//
//  覆盖：门 c 那一个算例（W08 盘20 判决，Linux）；门 f 的 W08 判决两行；六个转储算例。
//  不覆盖：导航档与 W06（门 f 其余六行，另跑 R48NMeshGateTests.门f_现役数 取改后数）；Windows 逐位；别的算例上轮数是否也降（机理同一，推断）；
//          门 b 的轮数界 1 没有出处（见上）；首跑的红不是「停机量恰贴阈值」那种（停机前后 32:2.678→33:0.117、28:2.137→29:0.311，都不贴 1），是过渡段放大；红照报，并印出两跑下穿前后各轮的 q/tol。
//          温度判据那一半是一致性核对，分辨不出改回（见上）。常红的门在红／绿状态上区分不出同一 Fact 里其余子项（改回复现、温度半边）将来的新红（失败信息会逐条列出）。
//          锚点（Rec 119／98／49，091703／065617；转储诊断 PreLinux 084640；NewMembers 正则）都是 8b90b5f 单树证据。与同波 C2（F3 热场）、C4（F7 细区半径 59 → 40，CostKitCase／GateFCase 经
//          MeshVerify.RequiredMeshFor）〔合并 C4′ 后改：C4′ 是决 29 自适应，半径 = 计划初值 W08 53.697（不是 40），且每份转储多 1+3 行记录（算例.MeshFineRadiusPlan／各片 Recipe.FineRadiusMm）〕、C3（算例.ZoneByMaterialFraction 1 行）、RING（每片 5 行 HoleBand*）合并后，这两条在 Linux 上一定红，原因不是 SEG，不能读成 SEG 回归；
//          门 b「改回 ⇒ 差 > 1」依赖一次彩票抽签，合并树的网格上可能抽不出来。合并后怎么处置【待决定】（实施记录 §7）。
// ════════════════════════════════════════════════════════════════════════════
[Trait("速度", "慢")]
public class R48SegContinuousRootSlowTests
{
    private readonly ITestOutputHelper _o;
    public R48SegContinuousRootSlowTests(ITestOutputHelper o) { _o = o; }

    const int RoundBound = 1;
    static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string LoadAvg() { try { return File.Exists("/proc/loadavg") ? File.ReadAllText("/proc/loadavg").Trim() : "—"; } catch { return "—"; } }
    static string F3(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000", CultureInfo.InvariantCulture);

    sealed class Trace { public int K; public double Delta, Res, Amp; public double[] I = Array.Empty<double>(); }

    sealed class Run
    {
        public string Tag = ""; public bool Cont; public LineResult R = new(); public double Sec; public string Err = "";
        public List<Trace> Tr = new();
        public double Hot => R.ValueOf(LineResult.Key.HotOverTc);
        public double Cold => R.ValueOf(LineResult.Key.ColdUnderTc);
        public double Flux => R.ValueOf(LineResult.Key.NetFlux);
    }

    /// <summary>门 c 那一份（R48MCostKit.Run 的写法，逐行照抄；只多一个改回参数与逐轮记录）。</summary>
    static LineCase CostKitCase(bool cont)
    {
        var d = R48MCostKit.Design(DesignSpec.W08.FlangeInsulMm);
        var mesh = R48MCostKit.Mesh(true, d);
        var lc = d.Clone().BuildCase(new DesignInputs { SegCurrentContinuousRoot = cont });
        Solver.ApplyCaseMesh(lc, mesh);
        lc.MeasureJacobianAmp = true;
        return lc;
    }

    /// <summary>门 f 那一份（R48NMeshGateTests.门f_现役数 → SolveLine 的写法，逐行照抄：先 ApplySectionFloor，再 BuildCase、ApplyCaseMesh）。</summary>
    static LineCase GateFCase(double disc, bool cont)
    {
        var d0 = R48NMeshGateTests.Design("W08");
        var reqFine = MeshVerify.RequiredFineMmFor(d0);   // （合并 C4′ 时改，变因 = 决 29 自适应：RequiredMeshFor 签名加工艺参数 DesignInputs，细区半径改为计划初值 max(盘半径, 孔半径) + 热长度，W08 53.697 mm；细步单独取 RequiredFineMmFor）
        var d = d0.Clone();
        d.FlangeInsulated = true; d.FlangeInsulMm = disc; d.DiscInsulMm = Array.Empty<double>();
        var p = new DesignInputs { SegCurrentContinuousRoot = cont };
        var dummy = new SolverResult { Design = d };
        Solver.ApplySectionFloor(d, p, new SolverOptions(), dummy, null, null);
        var (_, reqRadius) = MeshVerify.RequiredMeshFor(d, p);   // 合并 C4′ 时改：计划初值（不放大；门 f 同一口径）
        var lc = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius });
        return lc;
    }

    static Run Solve(string tag, bool cont, LineCase lc)
    {
        var run = new Run { Tag = tag, Cont = cont };
        lc.CoupleTrace = (k, res, delta, resK, remain, amp, omega, aa) =>
        {
            lock (run.Tr) run.Tr.Add(new Trace { K = k, Delta = delta, Res = resK, Amp = amp, I = res.Segments.Select(s => s.CurrentA).ToArray() });
        };
        var sw = Stopwatch.StartNew();
        try { run.R = LineRunner.Run(lc); }
        catch (Exception ex) { run.Err = $"{ex.GetType().Name}：{ex.Message}"; }
        run.Sec = sw.Elapsed.TotalSeconds;
        return run;
    }

    static string RowOf(Run q)
        => $"{q.Tag}\t{(q.Cont ? "开（连续根）" : "关（改回中点）")}\t{q.R.Converged}\t{q.R.CoupleRounds}\t{q.Sec:0}\t{(q.R.CoupleRounds > 0 ? (q.Sec / q.R.CoupleRounds).ToString("0.0", CultureInfo.InvariantCulture) : "—")}\t"
         + $"{q.R.CertErrK:0.0000}\t{R(q.Hot)}\t{R(q.Cold)}\t{R(q.Flux)}\t{F3(q.Hot)}／{F3(q.Cold)}／{F3(q.Flux)}\t{q.R.MeshCells}\t{q.Err}";

    const string RowHead = "行\t段电流收尾\t收敛\t停机轮\t耗时 s\t每轮 s\t认证误差 K\t最热铂高出热偶读数 K（R）\t管根低于热偶读数 K（R）\t管孔净流入 W（R）\t三位小数\t单元\t异常";

    [Fact]
    public void 门b_整线1e6级扰动停机轮数差不超1轮_改回红_并出门f两行开关归因()
    {
        string file = DeliverableOut.Stamped("R48_段电流连续根_门b与门f两行开关归因.txt");
        string trDir = DeliverableOut.StampedDir("R48_段电流连续根_逐轮轨迹");
        var sb = new StringBuilder(); void W(string s = "") { lock (sb) { sb.AppendLine(s); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } }
        var clk = Stopwatch.StartNew();
        W("R48 段电流连续根（决 97 A）　门 b（整线 1e−6 级扰动的停机轮数差）与门 f 两行「开 − 关」归因");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　git HEAD {EvidenceHeader.GitHead(HandoverDoc.Root())}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(HandoverDoc.Root())}　{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　并发 3（机器 4 核，同机另有其他工作组在跑、负载不受控，开跑时 /proc/loadavg = {LoadAvg()}：耗时列不作成本数，同一行开关也只看量级）");
        W(EvidenceHeader.ForLineCase("段电流连续根 门 b（生产口径那一跑的头）", CostKitCase(true)).TrimEnd());
        W($"判读（跑前写死）：① 开：门 c 算例与「多调一次 ApplySectionFloor」的同一算例停机轮数差 ≤ {RoundBound}（界 {RoundBound} 没有出处，待决定）；两条温度判据差 ≤ 两跑认证误差之和（一致性核对，分辨不出改回；改回对照只靠轮数差）。② 关（改回）：同一对轮数差 > {RoundBound}（改回 ⇒ 红）；"
          + "且（Linux）复现改前：门 c 119 轮 5.419／2.715／−0.102（…细网格成本_本次开跑于2026-09-23_091703.txt），门 f 盘20 判决 98 轮、盘10 判决 49 轮 −8.237／17.663／5.114（…门f_现役数_W08_本次开跑于2026-09-23_065617.txt）。");
        W($"逐轮轨迹目录：{trDir}");
        W();

        var jobs = new (string Tag, bool Cont, Func<LineCase> Mk)[]
        {
            ("门c算例", false, () => CostKitCase(false)),
            ("门f盘20判决（=门c算例+ApplySectionFloor）", false, () => GateFCase(20, false)),
            ("门f盘10判决", false, () => GateFCase(10, false)),
            ("门c算例", true, () => CostKitCase(true)),
            ("门f盘20判决（=门c算例+ApplySectionFloor）", true, () => GateFCase(20, true)),
            ("门f盘10判决", true, () => GateFCase(10, true)),
        };
        var runs = new ConcurrentDictionary<(string, bool), Run>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 3 }, j =>
        {
            var q = Solve(j.Tag, j.Cont, j.Mk());
            runs[(j.Tag, j.Cont)] = q;
            var tr = new StringBuilder("轮\tδ K\t真残差 K\t放大\tmax(δ,r)·放大 / 0.025\t段电流 A（R）\n");
            foreach (var t in q.Tr.OrderBy(t => t.K))
                tr.AppendLine($"{t.K}\t{t.Delta:E3}\t{t.Res:E3}\t{t.Amp:0.000}\t{Math.Max(t.Delta, t.Res) * t.Amp / 0.025:0.000}\t{string.Join(" ", t.I.Select(R))}");
            File.WriteAllText(Path.Combine(trDir, $"{j.Tag.Split('（')[0]}_{(j.Cont ? "开" : "关")}.tsv"), tr.ToString(), new UTF8Encoding(true));
            W($"· 完成 {q.Tag} {(q.Cont ? "开" : "关")}：{q.R.CoupleRounds} 轮 {q.Sec:0} s {(q.Err.Length > 0 ? q.Err : "")}（开跑后 {clk.Elapsed.TotalMinutes:0.0} 分钟）");
        });
        W();
        W(RowHead);
        foreach (var j in jobs) W(RowOf(runs[(j.Tag, j.Cont)]));
        W();

        Run cA = runs[("门c算例", true)], cB = runs[("门f盘20判决（=门c算例+ApplySectionFloor）", true)];
        Run rA = runs[("门c算例", false)], rB = runs[("门f盘20判决（=门c算例+ApplySectionFloor）", false)];
        Run c10 = runs[("门f盘10判决", true)], r10 = runs[("门f盘10判决", false)];
        int dCont = Math.Abs(cA.R.CoupleRounds - cB.R.CoupleRounds), dRev = Math.Abs(rA.R.CoupleRounds - rB.R.CoupleRounds);
        double hotTol = cA.R.CertErrK + cB.R.CertErrK;
        W($"门 b ①（开）：停机轮 {cA.R.CoupleRounds} 对 {cB.R.CoupleRounds}，差 {dCont}（界 {RoundBound}，没有出处，待决定）⇒ {(dCont <= RoundBound ? "过" : "红")}");
        W($"      两条温度判据差 {Math.Abs(cA.Hot - cB.Hot):E3}／{Math.Abs(cA.Cold - cB.Cold):E3} K（界 = 两跑认证误差之和 {hotTol:0.0000} K；一致性核对，改回那一对：{Math.Abs(rA.Hot - rB.Hot):E3}／{Math.Abs(rA.Cold - rB.Cold):E3} K 对 {rA.R.CertErrK + rB.R.CertErrK:0.0000} K，只报）；净流入差 {Math.Abs(cA.Flux - cB.Flux):E3} W（只报）");
        W($"门 b ②（关 = 改回）：停机轮 {rA.R.CoupleRounds} 对 {rB.R.CoupleRounds}，差 {dRev} ⇒ 同一判法{(dRev > RoundBound ? "红（对照成立）" : "没红（对照不成立）")}");
        W();
        W("「开 − 关」归因（同一行开减关）：");
        foreach (var (c, r) in new[] { (cA, rA), (cB, rB), (c10, r10) })
            W($"  {c.Tag}：轮 {r.R.CoupleRounds} → {c.R.CoupleRounds}；耗时 {r.Sec:0} → {c.Sec:0} s；最热铂 {c.Hot - r.Hot:+0.0000;-0.0000} K、管根 {c.Cold - r.Cold:+0.0000;-0.0000} K、净流入 {c.Flux - r.Flux:+0.0000;-0.0000} W；认证误差 {r.R.CertErrK:0.0000} → {c.R.CertErrK:0.0000} K");
        // 下穿前后各轮（门 b 红时查因用）
        foreach (var q in new[] { cA, cB })
        {
            var tail = q.Tr.OrderBy(t => t.K).Where(t => t.K >= q.R.CoupleRounds - 4).Select(t => $"{t.K}:{Math.Max(t.Delta, t.Res) * t.Amp / 0.025:0.000}");
            W($"  {q.Tag}（开）停机前后 q/tol：{string.Join(" ", tail)}");
        }
        W($"── 结束 {DateTime.Now:yyyy-MM-dd HH:mm:ss}（{clk.Elapsed.TotalMinutes:0.0} 分钟）");
        _o.WriteLine(file);

        foreach (var q in runs.Values) { Assert.True(q.Err.Length == 0, q.Tag + "：" + q.Err); Assert.True(q.R.Ok && q.R.Converged, $"{q.Tag} {(q.Cont ? "开" : "关")} 没收敛：{q.R.Message}"); }
        var fails = new List<string>();
        if (dCont > RoundBound) fails.Add($"开：停机轮 {cA.R.CoupleRounds} 对 {cB.R.CoupleRounds}，差 {dCont} > {RoundBound}（界 {RoundBound} 没有出处，待决定）");
        if (Math.Abs(cA.Hot - cB.Hot) > hotTol || Math.Abs(cA.Cold - cB.Cold) > hotTol) fails.Add($"开：两跑温度判据差超过认证误差之和 {hotTol:0.0000} K");
        if (dRev <= RoundBound) fails.Add($"改回：轮数差 {dRev} ≤ {RoundBound}，门分辨不出改回（非空转不成立）");
        if (!OperatingSystem.IsWindows())
        {
            void Rec(Run q, int k, string hot, string cold, string flux, string src)
            {
                if (q.R.CoupleRounds != k || F3(q.Hot) != hot || F3(q.Cold) != cold || F3(q.Flux) != flux)
                    fails.Add($"改回没复现改前 {q.Tag}：{q.R.CoupleRounds} 轮 {F3(q.Hot)}／{F3(q.Cold)}／{F3(q.Flux)} ≠ {k} 轮 {hot}／{cold}／{flux}（{src}）");
            }
            Rec(rA, 119, "5.419", "2.715", "-0.102", "091703");
            Rec(rB, 98, "5.419", "2.715", "-0.102", "065617");
            Rec(r10, 49, "-8.237", "17.663", "5.114", "065617");
        }
        Assert.True(fails.Count == 0, string.Join("\n", fails) + "\n见 " + file);
    }

    // ────────────────────────────── 转储诊断 ──────────────────────────────
    /// <summary>改前（dfe68a8，F6 后、§0.-20 前）Linux 去文字 SHA-256：deliverable/R48_整线全量转储_SHA256汇总_本次开跑于2026-09-23_084640_12039.txt（0900 那一跑逐位相同）。</summary>
    static readonly Dictionary<string, string> PreLinux = new()
    {
        ["B2_带玻璃"] = "9ac23197eeccb8b7097cef57622e17135a96571b089310ddf0cfb4fdc34b992e",
        ["B2_空管_管腔系数0"] = "f4e78dfac060991ba7ff8bdde04468ae79631ccaad4fed839c721741c7c173b0",
        ["B2_空管_默认管腔系数"] = "42ce199c5cfc4ff3c4e81aa59a4ff7e7ca014090caadb5f25cdeaeada7c1c572",
        ["B2_带玻璃_实测电流"] = "5c33f3852c59d23bf0eb08b3849ed3bd76e71572b893ba6418d679d3ee2c0624",
        ["盘56两段_解析_电导率随温度"] = "e21cf5506366641b6ec25bc2f01ccbf3a64125bafd98db5514ea743854159ea9",
        ["盘56两段_图纸路径"] = "99528a6fab8a47e4c95f5187f2c3bca71a1bf0c7458f383340a902e3da305059",
    };

    /// <summary>改前之后新加的公开成员（转储按公开成员逐个写，加一个就多一行）：本次的改回参数；§0.-20 的四个孔弧诊断字段（§0.-20 已证数值场一位没动）。</summary>
    static readonly Regex NewMembers = new(@"^[^\n]*\.(SegCurrentContinuousRoot|HoleArcCoverage|HoleArcMaxGapMm|HoleArcMaxGapMidDeg|HoleArcGapCount) = [^\n]*\n", RegexOptions.Multiline);

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s))).ToLowerInvariant();

    [Fact]
    public void 诊断_六个转储算例_改回逐位等于改前_连续根位移逐行印出()
    {
        string file = DeliverableOut.Stamped("R48_段电流连续根_转储六例_改回与连续根.txt");
        string dumpDir = DeliverableOut.StampedDir("R48_段电流连续根_转储六例");
        var sb = new StringBuilder(); void W(string s = "") { lock (sb) { sb.AppendLine(s); File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } }
        W("R48 段电流连续根（决 97 A）　R48LineDumpTests 六个算例：改回（中点）与连续根各转储一次（去文字口径）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　git HEAD {EvidenceHeader.GitHead(HandoverDoc.Root())}　Core 改动指纹 {EvidenceHeader.CoreDiffSha1(HandoverDoc.Root())}　{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　并发 3");
        W("判读（跑前写死）：改回的去文字转储去掉新增公开成员行（SegCurrentContinuousRoot；§0.-20 的 HoleArc 四字段）后，SHA-256 = 改前 084640 那一跑（Linux）。连续根：只印位移。");
        W($"转储目录（去文字口径，两种收尾各一份）：{dumpDir}");
        W();
        var res = new ConcurrentDictionary<(string, bool), (string Text, double Sec, bool Ok, bool Conv, int Rounds)>();
        var jobs = R48LineDumpTests.CaseNames.SelectMany(n => new[] { (n, false), (n, true) }).ToArray();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 3 }, j =>
        {
            var lc = R48LineDumpTests.CaseOf(j.Item1);
            lc.Base.SegCurrentContinuousRoot = j.Item2;
            var sw = Stopwatch.StartNew();
            var r = LineRunner.Run(lc);
            string text = R48LineDumpTests.Dump(lc, r, withText: false);
            res[j] = (text, sw.Elapsed.TotalSeconds, r.Ok, r.Converged, r.CoupleRounds);
            File.WriteAllText(Path.Combine(dumpDir, $"{j.Item1}_{(j.Item2 ? "连续根" : "改回")}.txt"), text, new UTF8Encoding(false));
            W($"· 完成 {j.Item1} {(j.Item2 ? "连续根" : "改回")}：{sw.Elapsed.TotalSeconds:0} s　{r.CoupleRounds} 轮");
        });
        W();
        var fails = new List<string>();
        foreach (var name in R48LineDumpTests.CaseNames)
        {
            var rev = res[(name, false)]; var cont = res[(name, true)];
            string strippedRev = NewMembers.Replace(rev.Text, "");
            int removed = rev.Text.Count(c => c == '\n') - strippedRev.Count(c => c == '\n');
            string shaRev = Sha(strippedRev), shaCont = Sha(cont.Text), shaContStripped = Sha(NewMembers.Replace(cont.Text, ""));
            string pre = PreLinux[name];
            W($"{name}：改回 {rev.Rounds} 轮／连续根 {cont.Rounds} 轮；改回去新增行（{removed} 行）去文字 SHA {shaRev} {(shaRev == pre ? "= 改前" : "≠ 改前 " + pre)}；连续根去文字 SHA（全）{shaCont}（去新增行 {shaContStripped}）");
            if (!OperatingSystem.IsWindows() && shaRev != pre) fails.Add($"{name}：改回去新增行 SHA {shaRev} ≠ 改前 {pre}");
            // 逐行位移
            var a = rev.Text.Split('\n'); var b = cont.Text.Split('\n');
            if (a.Length != b.Length) { W($"   行数不同：改回 {a.Length}、连续根 {b.Length}"); continue; }
            int diffLines = 0; double maxAbs = 0; string maxAt = "";
            var shown = new List<string>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                diffLines++;
                int ea = a[i].IndexOf(" = ", StringComparison.Ordinal), eb = b[i].IndexOf(" = ", StringComparison.Ordinal);
                if (ea > 0 && eb > 0 && double.TryParse(a[i][(ea + 3)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var va)
                             && double.TryParse(b[i][(eb + 3)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var vb))
                {
                    double dd = Math.Abs(vb - va);
                    if (dd > maxAbs) { maxAbs = dd; maxAt = a[i][..ea]; }
                    string key = a[i][..ea];
                    if (Regex.IsMatch(key, @"(CurrentA|CoupleRounds|Checks\[\d+\]\.Actual|BaselineRootC|TotalMassG|CertErrK)$") && shown.Count < 40)
                        shown.Add($"   {key}：{va.ToString("R", CultureInfo.InvariantCulture)} → {vb.ToString("R", CultureInfo.InvariantCulture)}（{vb - va:+0.000E+0;-0.000E+0}）");
                }
            }
            W($"   连续根对改回：{diffLines} 行不同；数值行最大绝对位移 {maxAbs:E3}（{maxAt}）");
            foreach (var s in shown) W(s);
        }
        _o.WriteLine(file);
        foreach (var kv in res) Assert.True(kv.Value.Ok, $"{kv.Key} 没解出来");
        Assert.True(fails.Count == 0, string.Join("\n", fails) + "\n见 " + file);
    }
}
