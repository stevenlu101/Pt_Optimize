using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路：**W08 三网格对照** + **照表复原差在哪个字段**　2026-09-17，Opus 5。
//
//  ══ 两件事放同一个进程里，因为它们要的是同一样东西：**求解器亲手解出来的那份设计对象**
//
//  ① 半度差的差因定位（承 R48LSolveOrderTests）
//     那一轮已实测：四种次序下 ② 的结果**逐位相同** ⇒ 没有跨解残留状态；
//     而照端到端输出的旋钮终值表复原出来的设计，单独跑 ② 得 4.922 K（HC1|HC2），
//     端到端那一跑是 4.511 K（出口）。⇒ 差在「复原设计」那一步，**缺哪个字段还没定**。
//     本轮把 A 导航档**重新解一遍**，拿求解器返回的那个设计对象与照表复原的那份
//     **按反射逐字段比**（不是我列一张清单去比 —— 我列的清单会漏掉正是漏掉的那个）。
//     并把两份设计各跑一次 ② —— 差因要能落到数上，不能只落到字段名上。
//
//  ② W08 三网格对照（HANDOVER §0.-7「还开着」第一条）
//     W08 细网格档的第二遍**从来没有开始**：第一遍（细区 2.0 mm，细区半径已按网格无关口径
//     统一到 59）结构性停机，而生产的两遍机制规定第一遍不过就不做第二遍。
//     上一轮只有前后两张网格的证据（2.0/半径50 过、1.0/半径59 过），**中间那张没有**。
//     本轮补上：**求解器解出来的那份设计**（不是照表复原的），② 在三张网格上各判一次。
//
//  ══ 判读（**跑前写死在代码里，跑完不挪**）
//    · 三网格：只有中间那张说「不过」（前后两张都过）⇒ 导航遍统一半径 + 第一遍不过不做第二遍
//      = **生产链死结，不是设计不可行**；三张都过 ⇒ 中间那张不是停机的原因；其余按实测写。
//    · 复原差因：反射逐字段比出来的**每一处不同**都照印；再看两份设计的 ② 差多少。
//      字段全同而数不同 ⇒ 差不在设计（那就要回头查别处）；有字段不同 ⇒ 点名那几个字段。
//    · 本轮**只写结论与修法，不改生产链的判据与两遍机制**。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LThreeMeshTests
{
    private readonly ITestOutputHelper _o;
    public R48LThreeMeshTests(ITestOutputHelper o) { _o = o; }

    /// <summary>求解时间闸（跑前写死）：超过就切断，报「被时间闸切断」，不许装作跑完了。</summary>
    private static readonly TimeSpan SolveCap = TimeSpan.FromHours(2.5);

    [Fact]
    public void W08_求解复原差因与三网格对照()
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_W08_求解复原差因与三网格对照_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        string live = Path.Combine(Path.GetTempPath(), $"R48_L_三网格_{stamp}_进行中.log");
        var probe = new R48LSolveOrderTests.LiveProbe(live);

        var seed = DesignSpec.W08.Clone();
        var navCase = new LineCase();
        var (reqFine, reqRadius) = MeshVerify.RequiredMeshFor(seed);

        W("R48 L 路　**W08 三网格对照** + **照表复原差在哪个字段**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("");
        W("═══════ 本轮跑什么（同一个进程、同一份代码，不拼两份仪器输出）═══════");
        W($"第 1 步　把 A 导航档**重新解一遍**（Solver.Solve，界面「自动定厚」预设：细区 {navCase.MeshFineMm:0.0} mm／细区半径 {navCase.MeshFineRadiusMm:0.0} mm，不挖舌孔，轮数上限 40）");
        W("第 2 步　求解器返回的那份设计 **对** 照端到端输出表复原的那份 —— **按反射逐字段比**（不列清单，清单会漏）");
        W("第 3 步　两份设计各跑一次 ② 带玻璃稳态（导航网格）—— 差因要落到数上");
        W($"第 4 步　**求解器那份设计**，② 在三张网格上各判一次：（2.0, 半径 {navCase.MeshFineRadiusMm:0.0}）、（2.0, 半径 {reqRadius:0.0}）、（{reqFine:0.000}, 半径 {reqRadius:0.0}）");
        W("");
        W("── 判读（**跑前写死在代码里，跑完不挪**）");
        W("· 三网格：只有中间那张说「不过」（前后两张都过）⇒ **导航遍统一半径 + 第一遍不过不做第二遍 是生产链死结，不是设计不可行**；");
        W("　三张都过 ⇒ 中间那张不是 B 档第一遍停机的原因；其余情形按实测写，不许往死结那一支靠。");
        W("· 复原差因：反射比出来的**每一处不同**都照印；字段全同而 ② 的数不同 ⇒ 差不在设计，要回头查别处。");
        W($"· 求解时间闸（跑前写死）{SolveCap.TotalHours:0.#} h —— 超时即切断，报「被时间闸切断」，不许装作跑完了。");
        W("· 本轮只写结论与修法，**不改生产链的判据与两遍机制**。");
        W("");
        W("═══════ 输入 ═══════");
        W($"内置设计「{seed.Name}」（W08）：管内径 {seed.TubeIdMm:0.#} mm，壁厚 {seed.WallMm:0.00} mm，管保温 {seed.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed.SegLengthMm.Select(v => v.ToString("0")))} mm，设定 {string.Join("/", seed.SetpointC.Select(v => v.ToString("0")))} °C");
        W($"法兰：圆盘半径 {seed.DiscRadiusMm:0.#} mm，舌长 {seed.TabLengthMm:0.#} mm，舌半宽 {seed.TabHalfWidthMm:0.#} mm，"
          + $"舌根圆角 {seed.TabFilletMm:0.#} mm，环宽 {seed.RingWidthMm:0.#} mm，管孔半径 {seed.HoleRadiusMm:0.###} mm");
        W($"工况：夹头 {seed.ClampTempC:0} °C，压接段长 {seed.ClampLengthMm:0.#} mm，设定电流密度 J {seed.JDesignAPerMm2:0.#} A/mm²（终验限值 {seed.JCheckAPerMm2:0.#}）");
        W($"设计输入表：默认（DesignInputs 默认构造）；服役 {p.DesignLifeHours} h。");
        W($"网格无关口径要求：细区 {reqFine:0.000} mm，细区半径 {reqRadius:0.0} mm（MeshVerify.RequiredMeshFor）。");
        W($"进度活页（临时，非交付物）：{live}");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  第 1 步：重新求解
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 第 1 步：A 导航档重新求解 ═══════");
        var opt = new SolverOptions { MaxRounds = 40, AllowTabCuts = false };
        SolverResult? sr = null;
        bool cut = false;
        var solveSw = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(SolveCap))
        {
            probe.Report($"── 开始求解（A 导航档）　时间闸 {SolveCap.TotalHours:0.#} h");
            try { sr = Solver.Solve(seed.Clone(), p, opt, probe, cts.Token); }
            catch (OperationCanceledException) { cut = true; }
            catch (Exception ex) { probe.Report($"★ 求解抛异常：{ex.GetType().Name}：{ex.Message}"); }
        }
        solveSw.Stop();
        probe.Report($"── 求解结束，耗时 {solveSw.Elapsed.TotalMinutes:0.0} 分钟，场解 {sr?.Solves ?? 0} 次");
        W($"求解耗时 {solveSw.Elapsed.TotalMinutes:0.0} 分钟（{solveSw.Elapsed.TotalSeconds:0} s），场解 {sr?.Solves ?? 0} 次。");
        if (sr is null)
        {
            W(cut ? "★ **被跑前写死的时间闸切断** ⇒ 没有解出来的设计 ⇒ 后面三步都不跑（不许拿半成品冒充结果）。"
                  : "★ **求解器抛异常或没返回** ⇒ 没有解出来的设计 ⇒ 后面三步都不跑。");
            Finish(sb, file, totalSw);
            Assert.Fail("A 导航档没有解出来 —— 见输出文件");
            return;
        }
        W($"结论：{(sr.Feasible ? "**全判据通过**（可行）" : "**不可行**（求解器没把全部判据补上）")}　停因：{sr.StopWhy}");
        if (sr.HitBound) W("　⚠ 结构性停机（旋钮顶到上界／分派前提不成立／交棒）：再算一次会得到同一句话，不是「没搜到」。");
        if (sr.NullWhy.Length > 0) W($"　⚠ 最近一次场解没解出来的原因：{sr.NullWhy}");
        W($"终局网格：{(sr.FineRefined ? $"细网格 {sr.FineMmUsed:0.000} mm" : $"**没做第二遍** ⇒ 只在导航网格 {navCase.MeshFineMm:0.0} mm 上成立、**不可交付**")}");
        W($"铂重（终局复核那一次）：{Fmt(sr.MassG, "0.###")} g");
        W("");

        var solvedD = sr.Design;
        var restoredD = R48LW08NavDesign.Build();

        // ══════════════════════════════════════════════════════════════════
        //  第 2 步：逐字段比（反射，不列清单）
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 第 2 步：求解器解出来的设计　vs　照端到端输出表复原的设计（**按反射逐字段比**）═══════");
        W($"复原所依据的出处：{R48LW08NavDesign.Source}");
        W("比法：把 DesignSpec 的**每一个公开实例字段**（反射枚举，不由我列清单 —— 我列的清单会漏掉正是漏掉的那个）");
        W("　　　按 R 格式印成字符串逐一对照。标识/记录/说明类字段（名称、出处、记录值…）也照比、照印，但会标出来。");
        W("");
        var dumpS = DumpFields(solvedD);
        var dumpR = DumpFields(restoredD);
        var allKeys = dumpS.Keys.Union(dumpR.Keys).ToArray();
        var diffKeys = allKeys.Where(k => !string.Equals(Val(dumpS, k), Val(dumpR, k), StringComparison.Ordinal)).ToArray();
        W($"DesignSpec 公开实例字段共 {allKeys.Length} 个；**不同的有 {diffKeys.Length} 个**。");
        W("");
        W("字段\t求解器解出来的\t照表复原的\t属于");
        foreach (string k in diffKeys)
            W($"{k}\t{Val(dumpS, k)}\t{Val(dumpR, k)}\t{(RecordOnly.Contains(k) ? "标识/记录/说明（不进几何）" : "**进几何或进算例**")}");
        var geomDiff = diffKeys.Where(k => !RecordOnly.Contains(k)).ToArray();
        W("");
        W(geomDiff.Length == 0
          ? "⇒ **进几何的字段一个都没差** —— 那么 ② 的数若仍不同，差就不在设计上，要回头查别处。"
          : $"⇒ **进几何或进算例的字段差了 {geomDiff.Length} 个**：{string.Join("、", geomDiff)}　⇒ 照那张旋钮终值表复原出来的不是同一份设计。");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  第 3 步：两份设计各跑一次 ②
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 第 3 步：两份设计各跑一次 ② 带玻璃稳态（导航网格，只换设计）═══════");
        var gSolved = RunOn(W, "求解器解出来的设计", null, solvedD, p, probe);
        var gRestored = RunOn(W, "照表复原的设计", null, restoredD, p, probe);
        W("── 两份设计的三条热判据并列");
        W("判据\t求解器解出来的\t照表复原的\t差");
        foreach (string key in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux })
        {
            var a = Get(gSolved.R, key); var b = Get(gRestored.R, key);
            W($"{Criteria.Plain(key)}\t{Cell(a)}\t{Cell(b)}\t"
              + (a is null || b is null ? "—" : (b.Actual - a.Actual).ToString("+0.###;-0.###")));
        }
        W($"耦合剩余误差估计\t{gSolved.R.CoupleRemainK:0.000} K\t{gRestored.R.CoupleRemainK:0.000} K\t{gRestored.R.CoupleRemainK - gSolved.R.CoupleRemainK:+0.###;-0.###}");
        W($"铂重\t{gSolved.R.TotalMassG:0.###} g\t{gRestored.R.TotalMassG:0.###} g\t{gRestored.R.TotalMassG - gSolved.R.TotalMassG:+0.###;-0.###}");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  第 4 步：三网格对照（用**求解器那份设计**）
        // ══════════════════════════════════════════════════════════════════
        W("═══════ 第 4 步：三网格对照（**求解器解出来的那份设计**，设计一位不动，动的只有判决网格）═══════");
        W($"网格 ①（2.0 mm, 半径 {navCase.MeshFineRadiusMm:0.0}）＝ 整线算例缺省 —— A 导航档求根与判决用的那张（不调 ApplyCaseMesh）");
        W($"网格 ②（2.0 mm, 半径 {reqRadius:0.0}）＝ SolverOptions{{FineMm = 0, FineRadiusMm = {reqRadius:0.0}}} —— **B 档第一遍用的那张**");
        W($"网格 ③（{reqFine:0.000} mm, 半径 {reqRadius:0.0}）＝ SolverOptions{{FineMm = {reqFine:0.000}, FineRadiusMm = {reqRadius:0.0}}} —— 网格无关口径要求的那张");
        W("三张都走生产自己的网格配方 Solver.ApplyCaseMesh（全仓唯一一份），本测试不另抄一份配方。");
        W("");
        // 网格 ① 那一次就是第 3 步跑过的那一次 —— 同一次运行，不重跑、不另算
        var rows = new List<(string Tag, LineResult R, double Sec)>
        {
            ($"① 导航（2.0, 半径 {navCase.MeshFineRadiusMm:0.0}）", gSolved.R, gSolved.Sec),
        };
        W("（网格 ① 那一次 = 第 3 步「求解器解出来的设计」那一次，同一次运行、不重跑。）");
        W("");
        var m2 = RunOn(W, $"② 导航尺寸·统一半径（2.0, 半径 {reqRadius:0.0}）",
                       new SolverOptions { FineMm = 0, FineRadiusMm = reqRadius }, solvedD, p, probe);
        rows.Add((m2.Tag2, m2.R, m2.Sec));
        var m3 = RunOn(W, $"③ 细网格（{reqFine:0.000}, 半径 {reqRadius:0.0}）",
                       new SolverOptions { FineMm = reqFine, FineRadiusMm = reqRadius }, solvedD, p, probe);
        rows.Add((m3.Tag2, m3.R, m3.Sec));

        W("═══════ 三张网格并列：② 带玻璃稳态的交付判据（同一份设计）═══════");
        W("判据\t" + string.Join("\t", rows.Select(q => q.Tag + " 值/限值\t" + q.Tag + " 裕度")));
        var keys = new List<string>();
        foreach (var q in rows)
            foreach (var c in q.R.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
                if (!keys.Contains(c.Name)) keys.Add(c.Name);
        foreach (string key in keys)
            W(Criteria.Plain(key) + "\t" + string.Join("\t", rows.Select(q =>
            {
                var c = Get(q.R, key);
                return c is null ? "—\t—"
                     : $"{Fmt(c.Actual, "0.###")}/{c.Limit:0.###} {c.Unit}{(c.Undetermined ? " 判不了" : c.Ok ? "" : " **不过**")}\t{Fmt(Slack(c), "+0.###;-0.###")}";
            })));
        W("");
        W("── 三条热判据的裕度并列（换网格位移最大的那三条）");
        W("判据\t" + string.Join("\t", rows.Select(q => q.Tag)));
        foreach (string key in new[] { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux })
            W(Criteria.Plain(key) + "\t" + string.Join("\t", rows.Select(q => Cell(Get(q.R, key)))));
        W("");
        W("── 结论、铂重、网格单元");
        W("网格\t整线解\t耦合收敛\t剩余误差 K\t网格单元\t铂重 g\t② 结论");
        foreach (var q in rows)
            W($"{q.Tag}\t{(q.R.Ok ? "解出来了" : "**没解出来**")}\t{q.R.Converged}\t{q.R.CoupleRemainK:0.000}\t{q.R.MeshCells}\t"
              + $"{Fmt(q.R.TotalMassG, "0.###")}\t{(Pass(q.R) ? "**过**" : "**不过／判不了**")}");
        W("");

        // ══════════════════════════════════════════════════════════════════
        //  判读
        // ══════════════════════════════════════════════════════════════════
        bool p1 = Pass(rows[0].R), p2 = Pass(rows[1].R), p3 = Pass(rows[2].R);
        W("═══════ 判读（按跑前写死的那几条）═══════");
        W($"三网格：网格 ① {(p1 ? "过" : "不过／判不了")}　网格 ②（中间那张）{(p2 ? "过" : "不过／判不了")}　网格 ③ {(p3 ? "过" : "不过／判不了")}");
        W("");
        if (p1 && !p2 && p3)
        {
            W("⇒ **只有中间那张说不过**（前后两张都过）。按跑前写死的判读：");
            W("　**导航遍统一细区半径 + 第一遍不过就不做第二遍 —— 这是生产链上的死结，不是设计不可行。**");
            W("　同一份设计在网格无关口径要求的那张网格上是过的，却因为导航遍被放在一张");
            W("　「尺寸还是导航的 2.0 mm、细区半径却已经按细网格口径统一到 59」的**混合网格**上判，");
            W("　第一遍判它不过，于是第二遍按设计不做 —— 可行的点被一张谁都不打算交付的网格挡在门外。");
            W("");
            W("── 最小修法（两条，都只动生产链的接线，不动物理；本轮不实施，等第二步定）");
            W("　修法 A：**第一遍不统一细区半径** —— 导航遍就跑整线算例缺省的那张（2.0 mm, 半径 50）。");
            W("　　　　　改一处：Solver.Solve 里 navOpt = opt.Clone() 之后除 FineMm 外把 FineRadiusMm 也归零。");
            W("　　　　　代价：导航遍与细网格遍的细区半径不同 —— 但导航遍本来就只是「值不值得细算」的筛子，");
            W("　　　　　交付数一律出自第二遍；而现状是拿一张既不是导航、也不是交付的混合网格当筛子。");
            W("　修法 B：**第一遍不过时仍允许做第二遍** —— 把「第一遍不过 ⇒ 不做第二遍」改成");
            W("　　　　　「第一遍不过 ⇒ 第二遍照做，并印明第一遍没过」。改一处：Solve 里 okNav 那道闸。");
            W("　　　　　代价：细网格遍的机时（上一轮实测 B 档 75 分钟）会花在第一遍判不过的档上。");
            W("　两条可以并用。**哪条对，由下一步按实测定**；本轮只给证据与修法，不改生产。");
        }
        else if (p1 && p2 && p3)
        {
            W("⇒ **三张网格都过**。中间那张不是 B 档第一遍停机的原因 ——");
            W("　B 第一遍走到停机时设计已经不是这一份（轨迹里板厚 6.00、环倍率 2.50 都已抬过），");
            W("　停机要往「求解器在那张网格上走的路径」查，不是「这张网格判不了这份设计」。");
        }
        else
        {
            W("⇒ 实测不落在「只有中间那张不过」那一支，按实测写：");
            W($"　网格 ① {(p1 ? "过" : "不过")}／网格 ②（中间）{(p2 ? "过" : "不过")}／网格 ③ {(p3 ? "过" : "不过")}。");
            W("　不许把它当成「差不多就是死结」—— 判读是跑前写死的，落在哪一支就报哪一支。");
        }
        W("");
        W("复原差因：");
        W(geomDiff.Length == 0
          ? "　进几何的字段一个都没差，而两份设计的 ② 若仍不同 ⇒ 差不在设计，要回头查别处（本文件第 3 步那张表是证据）。"
          : $"　照旋钮终值表复原，漏掉的是这几个进几何/进算例的字段：**{string.Join("、", geomDiff)}**；"
            + "　后果见第 3 步那张表（同一网格、只换设计）。");
        W("");

        // ── A 段求解轨迹全文（唯一可回溯的记录）
        W("═══════ 求解轨迹（Solver.Trace 全文）═══════");
        foreach (var t in sr.Trace) W(t);

        Finish(sb, file, totalSw);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, q => Assert.True(q.R.Ok, $"{q.Tag} 的整线解没解出来：{q.R.Message}"));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  帮手
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>标识/记录/说明类字段 —— 照比照印，但不进几何、不进算例。</summary>
    private static readonly HashSet<string> RecordOnly = new()
    {
        "Name", "Provenance", "Binding", "Invalid", "InvalidChecks", "FromFile", "RecordFromOldMesh",
        "TotalMassG", "TubeMassG", "FlangeMassG", "ResidualK", "RampH", "DiscOverK", "HoleFluxW",
        "FlangeDipK", "TubeJ", "VerifiedMeshMm", "VerifiedFlangeDipK", "VerifiedHoleFluxW",
        "VerifiedDiscOverK", "VerifiedNote",
    };

    /// <summary>反射枚举 DesignSpec 的每一个公开实例字段，印成「字段 → 字符串」。清单不由人列。</summary>
    private static Dictionary<string, string> DumpFields(DesignSpec d)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in typeof(DesignSpec).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v;
            try { v = f.GetValue(d); } catch (Exception ex) { v = "（取不到：" + ex.GetType().Name + "）"; }
            map[f.Name] = Render(v);
        }
        return map;
    }

    private static string Render(object? v) => v switch
    {
        null => "null",
        double x => x.ToString("R"),
        double[] a => "[" + string.Join(", ", a.Select(x => x.ToString("R"))) + "]",
        string[] a => "[" + string.Join(", ", a) + "]",
        bool b => b.ToString(),
        _ => v.ToString() ?? "null",
    };

    private static string Val(Dictionary<string, string> m, string k) => m.TryGetValue(k, out var s) ? s : "（没有这个字段）";

    private static (string Tag2, LineResult R, double Sec) RunOn(
        Action<string> W, string tag, SolverOptions? opt, DesignSpec d, DesignInputs p, R48LSolveOrderTests.LiveProbe probe)
    {
        W($"── {tag}");
        var lc = d.Clone().BuildCase(p);
        if (opt is not null) Solver.ApplyCaseMesh(lc, opt);
        var sw = Stopwatch.StartNew();
        probe.Report($"── 开始 ②（{tag}）");
        LineResult r;
        try { r = LineRunner.Run(lc, probe); }
        catch (Exception ex) { r = new LineResult { Ok = false, Message = $"{ex.GetType().Name}：{ex.Message}" }; }
        sw.Stop();
        probe.Report($"── ② 结束（{tag}），耗时 {sw.Elapsed.TotalSeconds:0} s");
        W($"网格：细区 {lc.MeshFineMm:0.000} mm／粗区 {lc.MeshCoarseMm:0.0} mm／细区半径 {lc.MeshFineRadiusMm:0.0} mm，网格单元 {r.MeshCells}");
        W($"结论：{Verdict(r)}");
        W($"耗时 {sw.Elapsed.TotalSeconds:0} s，铂重 {r.TotalMassG:0.###} g，耦合收敛 {r.Converged}，剩余误差估计 {r.CoupleRemainK:0.000} K。");
        W("  交付判据逐条：");
        foreach (var c in r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target)) W("    " + Line(c));
        W("");
        return (tag, r, sw.Elapsed.TotalSeconds);
    }

    private static bool Pass(LineResult r) => r.Ok && r.Converged && r.AllOk;

    private static ConstraintOut? Get(LineResult r, string key)
        => r.Checks.FirstOrDefault(c => c.Name.StartsWith(key, StringComparison.Ordinal));

    private static double Slack(ConstraintOut? c)
        => c is null || double.IsNaN(c.Actual) ? double.NaN : c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit;

    private static string Cell(ConstraintOut? c)
        => c is null ? "—" : $"{Fmt(c.Actual, "0.###")}（裕 {Fmt(Slack(c), "+0.###;-0.###")}，位置 {c.Where}）";

    private static string Verdict(LineResult r)
        => !r.Ok ? $"整线解没解出来 ⇒ 判不了（{r.Message}）"
         : !r.Converged ? $"外层耦合未收敛（剩余误差估计 {r.CoupleRemainK:0.000} K）—— 场无效，判不了"
         : r.AllOk ? "全判据通过"
         : $"有判据不过或判不了：{string.Join("；", r.Failed)}";

    private static string Line(ConstraintOut c)
    {
        string kind = c.Kind switch
        {
            CheckKind.HardSafety => "卡交付（硬安全线）",
            CheckKind.Target => "卡交付（目标）",
            _ => "只作参考",
        };
        return $"{Criteria.Plain(c.Name)}：实际 {(c.Withheld ? "暂不给数" : c.Actual.ToString("0.###"))} / 限值 {c.Limit:0.###} {c.Unit}，"
             + $"{(c.Undetermined ? "**无法判定**" : c.Ok ? "过" : "**不过**")}，裕度 {Slack(c):+0.###;-0.###}，位置 {c.Where}　[{kind}]";
    }

    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt);

    private void Finish(StringBuilder sb, string file, Stopwatch totalSw)
    {
        totalSw.Stop();
        sb.AppendLine();
        sb.AppendLine($"── 总耗时 {totalSw.Elapsed.TotalHours:0.00} 小时（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");
        sb.AppendLine("出处：求解 = Solver.Solve；带玻璃稳态 = LineRunner.Run；网格配方 = Solver.ApplyCaseMesh（全仓唯一一份）；"
                    + "网格无关口径 = MeshVerify.RequiredMeshFor；照表复原的设计 = R48LW08NavDesign.Build。");
        sb.AppendLine("每个数来自同一次运行（同一进程），不拼两份输出。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
