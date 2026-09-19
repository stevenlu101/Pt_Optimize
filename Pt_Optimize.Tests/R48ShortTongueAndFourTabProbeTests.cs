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
//  R48 P 路 第二轮探针：**短方向**（舌长 80/110/125）与**四片同扫**（2026-09-17，Opus 5）
//
//  ══ 上一轮（同树同配方同网格，R48TongueLengthWindowProbeTests）得到的
//    舌长 140／170／200／230，只扫入口片舌保温 3.0–8.0 每 0.1：
//      140 → 窗口 [5.1, 5.1] 宽 0.1 mm（0.5 mm 档一个都落不进去）
//      170／200／230 → 51 档一档都不过（越长越糟）
//    原因：舌片截面积被「电流密度不许超 11」钉死（截面积 = 设计电流 ÷ 设定电流密度，与长度无关）
//    ⇒ 加长 = 纯加电阻。入口片舌板焦耳热 362 → 959 W，而进铜排夹头的热几乎不动（191 → 215 W），
//    多出来的几百瓦灌回管子。**数据指向短的方向。**
//
//  ══ 本轮两件事（探针、判据、网格、算例、步进全部沿用上一轮；门槛一个不动）
//    甲　短方向：舌长 80／110／125 mm，同样只扫入口片舌保温 3.0–8.0 每 0.1。
//    乙　四片同扫：四片舌保温从各自求解终值出发、按同一 Δ（−1.0…+1.0 步 0.1）同向平移，
//        看窗口宽度与「只扫入口片」有没有不同 —— 即回答上一轮那句限定
//        「只动入口片救不了」是不是等于「四片同调也救不了」。
//
//  ══ ★★★ 跑前就已经知道的一件硬事（从代码读出来，不是跑出来的）★★★
//    舌长有**几何下限**，它由装配判据「舌片自由段 ≥ 下界」定死：
//        自由段 = 舌长 − 圆盘切点 − 压接段            （Core/GeometryScreen.cs Judge）
//        圆盘切点 x = 0                               （等宽舌片、舌半宽 30 = 盘半径 30；
//                                                      Core/PlateCurrent2D.cs Tangent 的 TabParallel 分支）
//        压接段 = DesignSpec.ClampLengthMm = 40 mm    （W08 没有覆写它 ⇒ 用缺省）
//        自由段下界 = LineCase.FreeTabMinMm = GeometryScreen.FreeTabMinDefaultMm = 100 mm
//                                                     （现场条件：铜排长 100／宽 60–80 mm，用户 2026-08-17）
//      ⇒ **最小合法舌长 = 0 + 40 + 100 = 140.0 mm**，恰好就是上一轮跑的最短那一档。
//      ⇒ 80／110／125 三档的自由段只有 40／70／85 mm，**装配判据必定不过** ——
//        在生产判据下它们一定是「无可行点」，而且**与热学无关**。这句话写在跑之前。
//
//    那为什么还要跑？因为「装不下铜排」是**现场条件**（可以拿去和现场谈：铜排短一点、
//    压接方式换一种），而「热学上短的方向能不能把窗口撑开」是**物理**，只能实测。
//    ⇒ 本探针对短的三档同时报两件事，**分开两列、绝不混为一谈**：
//        甲 生产判定（含装配那一条）—— 这是能不能造的答案，预期三档全「无可行点」；
//        乙 除装配那一条以外的判据全过的区间 —— 这是「热学窗口」，**不是可行解**，
//           只是回答「若现场把自由段下界这条装配条件放宽，短方向值不值得去谈」。
//      ★ 乙 不是放宽门槛：门槛一个没动，判据也一条没删 —— 只是把**那一条判据单独列出来**
//        另计一栏。任何一行只要装配那一条不过，甲 一律写「不过」。
//
//  ══ 算例（与上一轮逐字相同；每个数指得回出处）
//    几何与工况：内置设计「管壁 0.8 · 留余量」（DesignSpec.W08，从代码读，不手抄）。
//    法兰旋钮：求解器解出来的终值 —— 板厚 0.73/1.26/1.26/0.73、舌片厚 2.03/3.51/3.51/2.03、
//      舌保温 5.1/2.3/3.6/10.4 mm、环倍率全 1.00、无圆盘槽、无舌孔。
//      出处：D:\WinForm\r48_L\deliverable\R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt
//      的「求解器解出来的设计」表（只读）。本探针不重跑求解器。
//    工况：带玻璃稳态，生产口径 LineRunner.Run，导航网格（整线算例缺省）⇒ 与上一轮同一张网格，可比。
//
//  ══ 跑前写死的判读（2026-09-17 Opus 5，**跑之前写，跑完不许改**）
//    Q1 最小合法舌长 = 140.0 mm（上面那笔账）⇒ 80／110／125 的生产判定必定是「无可行点」，
//       卡住的那一条必定是装配那一条（自由段 40／70／85 < 100）。若实测**不是**这样 ⇒ 我这笔账算错了，照记。
//    Q2 入口片舌板焦耳热随舌长**单调下降**。正比外推（上一轮 140 mm 的 5.1 档是 362.1 W）给出
//       125 → 323、110 → 284、80 → 207 W；但上一轮的长端实测是**超正比**的
//       （140→362、170→552、200→754、230→959，每 30 mm 涨 190–206 W，远大于正比的 78 W/30 mm），
//       原因是电阻率随温升的正反馈 ⇒ 短端预期**低于**正比外推值。三档实测若高于正比外推 ⇒ 方向反了，点名。
//    Q3 本轮要答的那个问题：热学窗口（= 判读里 乙 那一栏）随舌长变短**单调变宽**。
//       若 80 mm 的热学窗口仍 < 0.5 mm ⇒ 结论写「短方向也撑不开窗口」。
//    Q4 四片同扫：若窗口宽度仍 ≈ 0.1 mm（= 步进本身）⇒ 上一轮那句限定不实质，
//       「四片同调也救不了」；若显著变宽（≥ 0.5 mm）⇒ 上一轮的「无可行点」只对「单动入口片」成立。
//    Q5 方向与预期不符 ⇒ 不停、照记、结论里点名；不许放宽判据去凑可行点；
//       窗口顶到扫描边界 ⇒ 报「被扫描区间截断，真实宽度 ≥ 这个数」。
//
//  ══ 快门（断言；不过就当场失败，不许把坏数写成报告）
//    S1 扫描区间：短方向 51 档、首 3.0、末 8.0、步进 0.1；四片同扫 21 档、首 −1.0、末 +1.0、步进 0.1。
//    S2 舌长确实进了几何：每片舌尖 x == −舌长、压接入口 x == −(舌长−40)、自由段 == 舌长−40；
//       且**生产判据自己报出来的**那条装配判据的实测值 == 舌长−40、限值 == 100（不是我另算一份）。
//    S3 舌长确实改了场：整线解报出来的最紧截面位置随舌长走（80 → x=-40、110 → x=-70、125 → x=-85）。
//    S4 每档只有该动的旋钮动了（短方向：只有入口片舌保温；四片同扫：四片各自 = 终值 + 同一个 Δ）。
//    S5 四片同扫的 Δ=0 那一档必须逐条复现上一轮 L140 文件里舌保温 5.1 那一行
//       （同一棵树、同一张网格、同一个设计 ⇒ 对不上就是我这一轮的算例搭错了，当场停）。
//
//  注记：2026-09-17，Opus 5。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48ShortTongueAndFourTabProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48ShortTongueAndFourTabProbeTests(ITestOutputHelper o) { _o = o; }

    // ── 求解器解出来的终值（出处见文件头；只读 r48_L 那份 .txt，不重跑求解器）
    private static readonly double[] SolvedTabThickMm = { 0.73, 1.26, 1.26, 0.73 };
    private static readonly double[] SolvedTongueThickMm = { 2.03, 3.51, 3.51, 2.03 };
    private static readonly double[] SolvedTabInsulMm = { 5.1, 2.3, 3.6, 10.4 };

    private const double ScanLoMm = 3.0, ScanHiMm = 8.0, ScanStepMm = 0.1;
    private const int ScanCount = 51;

    // ── 上一轮 L140 文件里「舌保温 5.1」那一行（同树同网格同设计 ⇒ 四片同扫 Δ=0 必须复现它）
    //    出处：deliverable/R48_P_舌长窗口探针_L140_本次开跑于2026-09-17_114350.txt 第 66 行
    private const double A140MassG = 4245, A140NetFlux = 0.657, A140ColdUnder = 4.922, A140HotOver = 4.529,
                         A140SectionJ = 9.983, A140TubeJ = 9.506, A140TabGen = 362.110, A140Clamp = 191.526,
                         A140PlateNet = 1.601;

    /// <summary>
    /// 甲之零：**最小合法舌长是多少** —— 从代码读出来，不手抄，秒级。
    /// 这一条先跑：它决定 80／110／125 到底是「不合法」还是「合法但不可行」，两者写法完全不同。
    /// </summary>
    [Fact]
    public void 最小合法舌长要从代码读出来()
    {
        var p = new DesignInputs();
        var lc = BuildSolvedDesign(140.0, SolvedTabInsulMm[0]).BuildCase(p);
        double clampLen = lc.Base.BusbarClampLengthMm;       // 生产链路真正用的那一个（DesignSpec.BuildCase 写进去的）
        double freeMin = lc.FreeTabMinMm;                    // 装配判据的下界（LineCase.FreeTabMinMm）
        double tangent = Math.Abs(lc.FlangePlates[0].Tangent().X);
        double minLegal = tangent + clampLen + freeMin;

        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); _o.WriteLine(s); Console.WriteLine(s); }
        W("R48 P 路 第二轮　最小合法舌长（从代码读，不手抄）");
        W($"跑于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W($"　圆盘切点 |x| = {tangent:0.###} mm（等宽舌片、舌半宽 {DesignSpec.W08.TabHalfWidthMm:0.#} = 盘半径 {DesignSpec.W08.DiscRadiusMm:0.#}）");
        W($"　压接段 = {clampLen:0.###} mm（DesignInputs.BusbarClampLengthMm，由 DesignSpec.ClampLengthMm 写入）");
        W($"　自由段下界 = {freeMin:0.###} mm（LineCase.FreeTabMinMm；现场铜排长 100／宽 60–80 mm，用户 2026-08-17）");
        W($"⇒ **最小合法舌长 = {tangent:0.#} + {clampLen:0.#} + {freeMin:0.#} = {minLegal:0.0} mm**");
        W();
        W("三档短舌长的自由段与装配判据（判据由生产代码 GeometryScreen.Judge 自己算，不是我另算一份）：");
        foreach (double len in new[] { 80.0, 110.0, 125.0, 140.0 })
        {
            var lcS = BuildSolvedDesign(len, SolvedTabInsulMm[0]).BuildCase(p);
            var g = GeometryScreen.Judge(lcS.FlangePlates, lcS.Base.BusbarClampLengthMm, lcS.FreeTabMinMm);
            var c5 = g.First(c => c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal));
            W($"　舌长 {len:0} mm：自由段 {c5.Actual:0.#} mm，下界 {c5.Limit:0.#} mm ⇒ "
              + $"{Criteria.Plain(c5.Name)} {(c5.Ok ? "**过**" : "**不过**")}（差 {c5.Actual - c5.Limit:+0.#;-0.#;0} mm）");
        }
        W();
        W("读法：自由段是**装配条件**（铜排放得下），不是热学量。短于 140 mm 的舌长在本 APP 的生产判据下");
        W("　一律不可行，且与热学无关 —— 要改这一条得先改现场的铜排／压接方式，不是改计算。");
        W("注记：2026-09-17，Opus 5。");

        Assert.Equal(40.0, clampLen, 6);
        Assert.Equal(100.0, freeMin, 6);
        Assert.Equal(0.0, tangent, 6);
        Assert.Equal(140.0, minLegal, 6);
    }

    /// <summary>
    /// 甲：短方向窗口探针。舌长 80／110／125，只扫入口片舌保温 3.0→8.0 每 0.1 mm，逐档跑整线带玻璃稳态。
    /// 与上一轮同一份代码路径、同一张网格、同一组旋钮 —— 只有舌长不同。
    /// </summary>
    [Theory]
    [InlineData(80.0)]
    [InlineData(110.0)]
    [InlineData(125.0)]
    public void 短舌长窗口探针(double tabLenMm)
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_P_短舌长窗口探针_L{tabLenMm:0}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        var seed0 = DesignSpec.W08;
        W($"R48 P 路 第二轮　短方向窗口探针　**舌板长度 {tabLenMm:0} mm**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("上一轮（同树同配方同网格）：舌长 140 窗口 [5.1, 5.1] 宽 0.1 mm；170／200／230 无可行点 —— **越长越糟**。");
        W("本轮问：**往短的方向走，窗口会不会撑开？**");
        W();
        W("── 算例（每个数指得回出处）");
        W($"几何与工况：内置设计「{seed0.Name}」（DesignSpec.W08，从代码读，不手抄）——"
          + $"管内径 {seed0.TubeIdMm:0.#}／壁厚 {seed0.WallMm:0.00}／管保温 {seed0.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed0.SegLengthMm.Select(v => v.ToString("0")))} mm，"
          + $"设定 {string.Join("/", seed0.SetpointC.Select(v => v.ToString("0")))} °C，"
          + $"圆盘半径 {seed0.DiscRadiusMm:0.#}／舌半宽 {seed0.TabHalfWidthMm:0.#}／压接段长 {seed0.ClampLengthMm:0.#} mm，"
          + $"夹头 {seed0.ClampTempC:0} °C，设定电流密度 {seed0.JDesignAPerMm2:0.#}（终验限值 {seed0.JCheckAPerMm2:0.#}）A/mm²");
        W($"法兰旋钮：求解器解出来的终值 —— 板厚 {J(SolvedTabThickMm)}／舌片厚 {J(SolvedTongueThickMm)}／"
          + $"舌保温 {J(SolvedTabInsulMm)} mm，环倍率全 1.00，无圆盘槽、无舌孔。");
        W("　出处：D:\\WinForm\\r48_L\\deliverable\\R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt");
        W($"★ 舌板长度是唯一动的形状量：{tabLenMm:0} mm。压接段长不变。");
        W("工况：带玻璃稳态，生产口径 LineRunner.Run，导航网格（整线算例缺省）= 与上一轮同一张网格 ⇒ 可比。"
          + "判定一律用生产判据，门槛一个不动。");
        W();

        // ══ 跑之前就把「合法性」这笔账算清楚并印出来（Q1）
        var lc0 = BuildSolvedDesign(tabLenMm, SolvedTabInsulMm[0]).BuildCase(p);
        double clampLen = lc0.Base.BusbarClampLengthMm, freeMin = lc0.FreeTabMinMm;
        double tangent = Math.Abs(lc0.FlangePlates[0].Tangent().X);
        double minLegal = tangent + clampLen + freeMin, freeHere = tabLenMm - tangent - clampLen;
        W("── ★ 跑之前就知道的一件硬事：**舌长有几何下限**（从代码读出来，不是跑出来的）");
        W($"　自由段 = 舌长 − 圆盘切点 − 压接段；圆盘切点 |x| = {tangent:0.#}（等宽舌片、舌半宽 = 盘半径），"
          + $"压接段 = {clampLen:0.#} mm，自由段下界 = {freeMin:0.#} mm（现场铜排长 100／宽 60–80，用户 2026-08-17）。");
        W($"　⇒ **最小合法舌长 = {minLegal:0.0} mm**（= 上一轮跑的最短那一档）。本档舌长 {tabLenMm:0} ⇒ "
          + $"自由段只有 {freeHere:0.#} mm，比下界少 {freeMin - freeHere:0.#} mm。");
        W($"　⇒ 本档在**生产判据**下必定「无可行点」，卡住的是**装配**那一条，与热学无关。这句写在跑之前。");
        W("　那为什么还跑？「装不下铜排」是**现场条件**（可以去和现场谈铜排长度／压接方式），");
        W("　「短方向热学上能不能把窗口撑开」是**物理**，只能实测。下面两栏分开报，绝不混为一谈：");
        W($"　　甲 生产判定（含装配那一条）—— 能不能造的答案；");
        W($"　　乙 除「{Criteria.Plain(LineResult.Key.FreeTab)}」以外的判据全过 —— 叫「热学窗口」，**不是可行解**，");
        W("　　　 只回答「若现场肯把自由段这条装配条件放宽，短方向值不值得去谈」。");
        W("　★ 乙 **不是放宽门槛**：门槛一个没动、判据一条没删，只是把那一条单列一栏另计。");
        W();
        W("── 跑前写死的判读（2026-09-17 Opus 5，跑之前写，跑完不许改）");
        W("　Q1 三档的生产判定必定「无可行点」，卡住的必定是装配那一条（自由段 40／70／85 < 100）。不是这样 ⇒ 我算错了，照记。");
        W("　Q2 入口片舌板焦耳热随舌长单调下降；正比外推给 125→323、110→284、80→207 W，");
        W("　　 而长端实测是超正比的（140→362、170→552、200→754、230→959）⇒ 短端预期**低于**正比外推值。");
        W("　Q3 热学窗口随舌长变短**单调变宽**；若 80 mm 仍 < 0.5 mm ⇒「短方向也撑不开窗口」。");
        W("　Q5 方向不符照记并点名；不许放宽判据凑可行点；窗口顶到扫描边界要报「真实宽度 ≥ 这个数」。");
        W();

        // ══ 快门 S1
        var scan = new List<double>();
        for (int i = 0; i < ScanCount; i++) scan.Add(Math.Round(ScanLoMm + i * ScanStepMm, 1));
        Assert.Equal(ScanCount, scan.Count);
        Assert.Equal(ScanLoMm, scan[0], 9);
        Assert.Equal(ScanHiMm, scan[^1], 9);
        for (int i = 1; i < scan.Count; i++)
            Assert.True(Math.Abs(scan[i] - scan[i - 1] - ScanStepMm) < 1e-9, $"扫描步进不是 {ScanStepMm} mm：{scan[i - 1]} → {scan[i]}");
        W($"扫描：入口片舌保温 {ScanLoMm:0.0} → {ScanHiMm:0.0} mm 每 {ScanStepMm:0.0} mm，共 {ScanCount} 档；"
          + $"其余三片钉在 {SolvedTabInsulMm[1]:0.0}/{SolvedTabInsulMm[2]:0.0}/{SolvedTabInsulMm[3]:0.0} mm 不动（与上一轮同）。");

        // ══ 快门 S2：舌长进了几何 + 生产判据自己报的那条装配判据对得上
        {
            var dChk = BuildSolvedDesign(tabLenMm, SolvedTabInsulMm[0]);
            Assert.Equal(tabLenMm, dChk.TabLengthMm, 9);
            var lcChk = dChk.BuildCase(p);
            for (int j = 0; j < lcChk.FlangePlates.Length; j++)
            {
                var g = lcChk.FlangePlates[j];
                Assert.True(Math.Abs(g.TabEndXMm + tabLenMm) < 1e-9,
                    $"片 {j} 的舌尖 x={g.TabEndXMm:0.###} 与舌长 {tabLenMm:0} 对不上 ⇒ 舌长没进几何");
                double free = Math.Abs(g.TabEndXMm) - Math.Abs(g.Tangent().X) - clampLen;
                Assert.True(Math.Abs(free - (tabLenMm - clampLen)) < 1e-6,
                    $"片 {j} 自由段 {free:0.###} ≠ 舌长−压接段 {tabLenMm - clampLen:0.###}（切点不在 0？）");
                Assert.True(Math.Abs(g.TabEndXMm + clampLen + (tabLenMm - clampLen)) < 1e-9,
                    $"片 {j} 压接入口 x 与舌长对不上 ⇒ 压接入口没随舌长走");
            }
            var geo = GeometryScreen.Judge(lcChk.FlangePlates, clampLen, freeMin);
            var c5 = geo.First(c => c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal));
            Assert.True(Math.Abs(c5.Actual - freeHere) < 1e-6,
                $"生产判据报的自由段 {c5.Actual:0.###} 与本档算的 {freeHere:0.###} 对不上");
            Assert.Equal(freeMin, c5.Limit, 6);
            W($"快门 S2：舌尖 x = {-tabLenMm:0.#}、压接入口 x = {-(tabLenMm - clampLen):0.#}、自由段 = {freeHere:0.#} mm；"
              + $"生产判据自己报的「{Criteria.Plain(c5.Name)}」= {c5.Actual:0.#}／下界 {c5.Limit:0.#} ⇒ {(c5.Ok ? "过" : "**不过**")}（四片逐片断言）。");
        }
        W();

        W("── 逐档（每档一次整线带玻璃稳态全耦合解；算完立刻落盘）");
        W("　「生产判定」= 全部判据（含装配那一条）；「热学判定」= 除装配那一条外全过（**不是可行解**，见上）。");
        W("舌保温 mm\t生产判定\t热学判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t法兰增量温降 K\t截面电流密度\t管电流密度\t场有效\t收敛\t铂重 g\t耗时 s\t入口片舌板焦耳热 W\t入口片进铜排夹 W\t入口片管孔净流入 W\t最紧截面在哪\t没过的");
        Flush();

        var rows = new List<Row>();
        string sectionWhere0 = "";
        foreach (double ins in scan)
        {
            var d = BuildSolvedDesign(tabLenMm, ins);
            // 快门 S4
            Assert.Equal(ins, d.TabInsulMm[0], 9);
            for (int j = 1; j < SolvedTabInsulMm.Length; j++) Assert.Equal(SolvedTabInsulMm[j], d.TabInsulMm[j], 9);
            for (int j = 0; j < SolvedTabThickMm.Length; j++)
            {
                Assert.Equal(SolvedTabThickMm[j], d.TabThickMm[j], 9);
                Assert.Equal(SolvedTongueThickMm[j], d.TongueThickMm[j], 9);
                Assert.Equal(1.0, d.RingMul[j], 9);
            }
            var row = RunOne(d.BuildCase(p), ins);
            rows.Add(row);
            if (sectionWhere0.Length == 0) sectionWhere0 = row.SectionWhere;
            Say(Line(row));
        }

        // ══ 快门 S3
        string expectX = $"x={-(tabLenMm - clampLen):0}";
        W();
        W($"快门 S3（舌长改了场）：整线解报出来的最紧截面位置「{sectionWhere0}」，按定义应落在压接入口 {expectX} mm。");
        Assert.Contains(expectX, sectionWhere0);

        // ══ 两栏窗口
        W();
        W("── 甲 生产判定下的可行窗口（全部判据，门槛一个没动）");
        var prodBlocks = Blocks(rows, r => r.AllOk);
        if (prodBlocks.Length == 0)
        {
            W("★ **无可行点**：51 档一档都没过。逐档没过的条目里出现次数最多的几条：");
            foreach (var g in rows.SelectMany(r => r.Failed.Split('；', StringSplitOptions.RemoveEmptyEntries))
                                  .GroupBy(s => s.Split(' ')[0]).OrderByDescending(g => g.Count()).Take(6))
                W($"　　{g.Key}：{g.Count()} / {rows.Count} 档");
        }
        else
            foreach (var (lo, hi) in prodBlocks) W($"　窗口 [{lo:0.0}, {hi:0.0}] mm，宽度 {hi - lo + ScanStepMm:0.0} mm");

        W();
        W($"── 乙 热学窗口（除「{Criteria.Plain(LineResult.Key.FreeTab)}」以外的判据全过；**不是可行解**）");
        var thBlocks = Blocks(rows, r => r.ThermalOk);
        if (thBlocks.Length == 0)
            W("★ 热学上也没有一档全过 ⇒ 短方向连「把装配条件放宽」都救不了。");
        else
            foreach (var (lo, hi) in thBlocks)
                W($"　热学窗口 [{lo:0.0}, {hi:0.0}] mm，宽度 {hi - lo + ScanStepMm:0.0} mm"
                  + $"（{(lo <= ScanLoMm + 1e-9 || hi >= ScanHiMm - 1e-9 ? "**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**（判读 Q5）" : "两端都在扫描区间内")}）"
                  + $"　落进去的 0.5 mm 档：{HalfMmGrid(lo, hi)}");

        W();
        W("── 两个大数之差（入口片，取自整线解的场，不是 I²R 估算）");
        W("舌保温 mm\t舌板焦耳热 W\t舌板表面散热 W\t进铜排夹 W\t管孔净流入 W\t（焦耳热 − 进夹）W");
        foreach (var r in rows.Where(r => (int)Math.Round(r.InsulMm * 10) % 5 == 0))
            W($"{r.InsulMm:0.0}\t{N(r.TabGenW)}\t{N(r.TabLossW)}\t{N(r.ClampW)}\t{N(r.PlateNetFluxW)}\t{N(r.TabGenW - r.ClampW)}");
        W();
        W($"── 入口片管孔净流入对舌保温的斜率（最小二乘，全 51 档）：{Slope(rows):0.###} W/mm"
          + "（上一轮：140 mm −8.2、230 mm −18.3 W/mm；同一张网格、同一关）。");
        W();
        var at51 = rows.FirstOrDefault(r => Math.Abs(r.InsulMm - 5.1) < 1e-9);
        W("── 一句话");
        W($"舌长 {tabLenMm:0} mm：生产判定 {(prodBlocks.Length == 0 ? "**无可行点**（装配那一条钉死：自由段 " + freeHere.ToString("0") + " < " + freeMin.ToString("0") + "）" : "有窗口")}；"
          + $"热学窗口 {(thBlocks.Length == 0 ? "**也没有**" : string.Join("、", thBlocks.Select(b => $"[{b.Lo:0.0}, {b.Hi:0.0}] 宽 {b.Hi - b.Lo + ScanStepMm:0.0} mm")))}。"
          + (at51 is null ? "" : $"　舌保温 5.1 那一档（与上一轮同档可比）：入口片舌板焦耳热 {N(at51.TabGenW)} W、"
              + $"进铜排夹 {N(at51.ClampW)} W、入口片管孔净流入 {N(at51.PlateNetFluxW)} W、整线管孔净流入 {N(at51.NetFluxW)} W、"
              + $"整线最热铂高出热偶读数 {N(at51.HotOverTcK)} K、铂重 {at51.MassG:0} g。"));
        W();
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），{rows.Count} 次整线解。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk／Failed（生产判据，门槛未改）；");
        W("　装配那一条 = GeometryScreen.Judge（同一段代码，界面与这里共用）。每个数来自同一次运行，不拼两份输出。");
        W("注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.True(rows.Count == ScanCount, "扫描档数对不上");
        Assert.True(rows.Any(r => r.Ok), $"舌长 {tabLenMm:0}：51 档一次整线解都没解出来 —— 这不是「不可行」，是算不出来");
    }

    /// <summary>
    /// 乙：四片同扫。舌长 140（= 最小合法舌长，也是上一轮唯一有窗口的那一档），
    /// 四片舌保温各自从求解终值出发、按同一 Δ（−1.0 → +1.0 每 0.1）同向平移。
    /// 回答：上一轮「只动入口片救不了」是不是等于「四片同调也救不了」。
    /// </summary>
    [Fact]
    public void 四片同扫窗口探针()
    {
        const double tabLenMm = 140.0, DLo = -1.0, DHi = 1.0, DStep = 0.1;
        const int DCount = 21;
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_P_四片舌保温同扫_L140_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        W("R48 P 路 第二轮　**四片舌保温同扫**（舌长 140 mm）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("为什么做这一件：上一轮只扫**入口片**一片的舌保温，140 mm 的窗口只有 0.1 mm，170／200／230 无可行点。");
        W("　那份文件自己写着：「无可行点」的准确读法是**只动入口片这一片救不了**；要说「四片同调也救不了」，得再跑一次四片同扫。");
        W("　本探针就是那一次。");
        W("为什么落在 140 mm：工单说做 110 mm 那一档，若 110 不可行则改做窗口最宽的那个长度。");
        W($"　110 mm **不合法**（自由段 70 < 下界 100，装配判据钉死；最小合法舌长 = 圆盘切点 0 + 压接段 40 + 下界 100 = 140 mm），");
        W("　而 140／170／200／230 里只有 140 有窗口 ⇒ 落在 140 mm。");
        W();
        W("── 扫法");
        W($"　四片舌保温 = 求解器终值 {J(SolvedTabInsulMm)} mm **各自加同一个 Δ**，Δ 从 {DLo:+0.0;-0.0} 到 {DHi:+0.0;-0.0} 每 {DStep:0.0} mm，共 {DCount} 档。");
        W("　其余旋钮（板厚、舌片厚、环倍率、圆盘槽、舌孔、舌长）一个不动，与上一轮逐位相同。");
        W("　判定一律用生产判据（LineResult.AllOk／Failed），门槛一个不动；导航网格，与上一轮同一张。");
        W();
        W("── 跑前写死的判读（2026-09-17 Opus 5，跑之前写，跑完不许改）");
        W("　Q4 若窗口宽度仍 ≈ 0.1 mm（= 步进本身）⇒ 四片同调也救不了，上一轮那句限定不实质；");
        W("　　 若显著变宽（≥ 0.5 mm，即现场按 0.5 mm 一层包得出来）⇒ 上一轮的「无可行点」只对「单动入口片」成立。");
        W("　Q5 窗口顶到 Δ = ±1.0 的边界 ⇒ 报「被扫描区间截断，真实宽度 ≥ 这个数」，不外推。");
        W("　S5 Δ=0 那一档必须逐条复现上一轮 L140 文件里舌保温 5.1 那一行（同树同网格同设计）；对不上当场停。");
        W();

        // 快门 S1
        var deltas = new List<double>();
        for (int i = 0; i < DCount; i++) deltas.Add(Math.Round(DLo + i * DStep, 1));
        Assert.Equal(DCount, deltas.Count);
        Assert.Equal(DLo, deltas[0], 9);
        Assert.Equal(DHi, deltas[^1], 9);
        for (int i = 1; i < deltas.Count; i++)
            Assert.True(Math.Abs(deltas[i] - deltas[i - 1] - DStep) < 1e-9, "扫描步进不对");

        W("── 逐档（每档一次整线带玻璃稳态全耦合解；算完立刻落盘）");
        W("　（「热学判定」这一栏在本表里与「判定」必然相同 —— 舌长 140 是合法的，装配那一条本来就过；留着只为与短方向那三份表同列。）");
        W("Δ mm\t四片舌保温 mm\t入口片保温 mm\t判定\t热学判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t法兰增量温降 K\t截面电流密度\t管电流密度\t场有效\t收敛\t铂重 g\t耗时 s\t入口片舌板焦耳热 W\t入口片进铜排夹 W\t入口片管孔净流入 W\t最紧截面在哪\t没过的");
        Flush();

        var rows = new List<Row>();
        Row? zero = null;
        foreach (double dd in deltas)
        {
            var d = DesignSpec.W08.Clone();
            d.TabLengthMm = tabLenMm;
            d.TabThickMm = (double[])SolvedTabThickMm.Clone();
            d.TongueThickMm = (double[])SolvedTongueThickMm.Clone();
            d.TabInsulMm = SolvedTabInsulMm.Select(v => Math.Round(v + dd, 6)).ToArray();
            d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
            d.SlotSpanDeg = new double[4];
            d.TabHoleRMm = new double[4];
            d.TabHoleAspect = new[] { 1.0, 1.0, 1.0, 1.0 };

            // 快门 S4：四片各自 = 终值 + 同一个 Δ，其余逐位不动
            for (int j = 0; j < 4; j++)
            {
                Assert.Equal(SolvedTabInsulMm[j] + dd, d.TabInsulMm[j], 6);
                Assert.True(d.TabInsulMm[j] > 0, $"片 {j} 舌保温 {d.TabInsulMm[j]:0.0} ≤ 0 —— 扫描区间越界");
                Assert.Equal(SolvedTabThickMm[j], d.TabThickMm[j], 9);
                Assert.Equal(SolvedTongueThickMm[j], d.TongueThickMm[j], 9);
                Assert.Equal(1.0, d.RingMul[j], 9);
            }
            Assert.Equal(tabLenMm, d.TabLengthMm, 9);

            var row = RunOne(d.BuildCase(p), SolvedTabInsulMm[0] + dd);
            row.Delta = dd;
            row.Knobs = string.Join("/", d.TabInsulMm.Select(v => v.ToString("0.0")));
            rows.Add(row);
            if (Math.Abs(dd) < 1e-9) zero = row;
            Say($"{dd:+0.0;-0.0;0.0}\t{row.Knobs}\t{Line(row)}");
        }

        // ══ 快门 S5：Δ=0 必须复现上一轮 L140 的 5.1 行
        W();
        W("── 快门 S5：Δ=0（四片 = 求解终值）vs 上一轮 L140 文件里舌保温 5.1 那一行（同树、同网格、同设计）");
        Assert.NotNull(zero);
        void Cmp(string name, double now, double rec, double tol)
        {
            W($"　{name}：本次 {now:0.###}　上一轮 {rec:0.###}　差 {now - rec:+0.###;-0.###;0}");
            Assert.True(Math.Abs(now - rec) <= tol, $"S5 对不上：{name} {now:0.###} vs {rec:0.###}（容差 {tol}）⇒ 本轮算例搭错了");
        }
        Cmp("铂重 g", zero!.MassG, A140MassG, 2);
        Cmp("管孔净流入 W", zero.NetFluxW, A140NetFlux, 0.02);
        Cmp("管根低于热偶读数 K", zero.ColdUnderTcK, A140ColdUnder, 0.02);
        Cmp("最热铂高出热偶读数 K", zero.HotOverTcK, A140HotOver, 0.02);
        Cmp("法兰截面电流密度", zero.SectionJ, A140SectionJ, 0.01);
        Cmp("管电流密度", zero.TubeJ, A140TubeJ, 0.01);
        Cmp("入口片舌板焦耳热 W", zero.TabGenW, A140TabGen, 0.5);
        Cmp("入口片进铜排夹 W", zero.ClampW, A140Clamp, 0.5);
        Cmp("入口片管孔净流入 W", zero.PlateNetFluxW, A140PlateNet, 0.05);
        Assert.True(zero.AllOk, "S5：Δ=0 这一档在上一轮是**过**的，本轮却不过 ⇒ 算例搭错了");

        // ══ 窗口
        W();
        W("── 可行窗口（生产判据，门槛一个没动）");
        var blocks = Blocks(rows, r => r.AllOk);
        if (blocks.Length == 0)
            W("★ **无可行点**：21 档一档都没过（不许放宽判据去凑一个）。");
        else
            foreach (var (lo, hi) in blocks)
                W($"　窗口 Δ ∈ [{lo:+0.0;-0.0;0.0}, {hi:+0.0;-0.0;0.0}] mm，宽度 {hi - lo + DStep:0.0} mm"
                  + $"（{(lo <= DLo + 1e-9 || hi >= DHi - 1e-9 ? "**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**（判读 Q5）" : "两端都在扫描区间内")}）"
                  + $"　对应入口片舌保温 [{SolvedTabInsulMm[0] + lo:0.0}, {SolvedTabInsulMm[0] + hi:0.0}] mm，"
                  + $"落进去的 0.5 mm 档（按入口片）：{HalfMmGrid(SolvedTabInsulMm[0] + lo, SolvedTabInsulMm[0] + hi)}");
        W();
        W("── 与上一轮「只扫入口片」的同长度对照（同树、同网格、同一组其余旋钮）");
        W("　上一轮 140 mm 只动入口片：窗口 [5.1, 5.1] mm，宽 0.1 mm（步进本身），0.5 mm 档一个都落不进去。");
        W($"　本轮 140 mm 四片同调：{(blocks.Length == 0 ? "**无可行点**" : string.Join("、", blocks.Select(b => $"Δ ∈ [{b.Lo:+0.0;-0.0;0.0}, {b.Hi:+0.0;-0.0;0.0}] 宽 {b.Hi - b.Lo + DStep:0.0} mm")))}。");
        W();
        W("── 两个大数之差（入口片，取自整线解的场）");
        W("Δ mm\t四片舌保温 mm\t舌板焦耳热 W\t舌板表面散热 W\t进铜排夹 W\t管孔净流入 W\t（焦耳热 − 进夹）W");
        foreach (var r in rows.Where(r => (int)Math.Round(r.Delta * 10) % 5 == 0))
            W($"{r.Delta:+0.0;-0.0;0.0}\t{r.Knobs}\t{N(r.TabGenW)}\t{N(r.TabLossW)}\t{N(r.ClampW)}\t{N(r.PlateNetFluxW)}\t{N(r.TabGenW - r.ClampW)}");
        W();
        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），{rows.Count} 次整线解。");
        W("出处：整线带玻璃稳态 = LineRunner.Run；判定 = LineResult.AllOk／Failed（生产判据，门槛未改）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());
        Assert.Equal(DCount, rows.Count);
    }

    // ────────────────────────────────────────────────────────────────────────
    private Row RunOne(LineCase lc, double insulMm)
    {
        var sw = Stopwatch.StartNew();
        LineResult res;
        string threw = "";
        try { res = LineRunner.Run(lc, null); }
        catch (Exception ex) { threw = $"{ex.GetType().Name}：{ex.Message}"; res = new LineResult { Ok = false, Message = threw }; }
        sw.Stop();
        var f0 = res.Flanges.Length > 0 ? res.Flanges[0] : null;

        // 「热学判定」= 除装配那一条以外的判据全过。**门槛一个没动、判据一条没删** ——
        //   只是把那一条单独列出来另计一栏（它是现场装配条件，不是热学量）。
        bool thermalOk = res.Ok && res.Converged && res.MissingChecks.Length == 0
            && res.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target
                                  && !c.Name.StartsWith(LineResult.Key.FreeTab, StringComparison.Ordinal))
                         .All(c => c.Ok && !c.Undetermined);

        return new Row
        {
            InsulMm = insulMm,
            Ok = res.Ok,
            Converged = res.Converged,
            AllOk = res.Ok && res.AllOk,
            ThermalOk = thermalOk,
            NetFluxW = res.ValueOf(LineResult.Key.NetFlux),
            ColdUnderTcK = res.ValueOf(LineResult.Key.ColdUnderTc),
            HotOverTcK = res.ValueOf(LineResult.Key.HotOverTc),
            FlangeDipK = res.ValueOf(LineResult.Key.FlangeDip),
            SectionJ = res.ValueOf(LineResult.Key.SectionJ),
            TubeJ = res.ValueOf(LineResult.Key.TubeJ),
            FieldOk = res.Ok && res.Flanges.Length > 0 && res.Flanges.All(f => f.FieldsConverged),
            MassG = res.TotalMassG,
            Sec = sw.Elapsed.TotalSeconds,
            TabGenW = f0?.QGenTabW ?? double.NaN,
            TabLossW = f0?.QLossTabW ?? double.NaN,
            ClampW = f0?.QClampW ?? double.NaN,
            PlateNetFluxW = f0?.QFromTubeW ?? double.NaN,
            SectionWhere = res.Find(LineResult.Key.SectionJ)?.Where ?? "",
            Failed = threw.Length > 0 ? "抛异常：" + threw
                   : !res.Ok ? "整线解没解出来：" + res.Message
                   : string.Join("；", res.Failed),
        };
    }

    private static string Line(Row r)
        => $"{r.InsulMm:0.0}\t{(r.AllOk ? "过" : "不过")}\t{(r.ThermalOk ? "过" : "不过")}\t{N(r.NetFluxW)}\t{N(r.ColdUnderTcK)}\t{N(r.HotOverTcK)}\t"
         + $"{N(r.FlangeDipK)}\t{N(r.SectionJ)}\t{N(r.TubeJ)}\t{(r.FieldOk ? "是" : "否")}\t{(r.Converged ? "是" : "否")}\t"
         + $"{r.MassG:0}\t{r.Sec:0}\t{N(r.TabGenW)}\t{N(r.ClampW)}\t{N(r.PlateNetFluxW)}\t{r.SectionWhere}\t{r.Failed}";

    private static DesignSpec BuildSolvedDesign(double tabLenMm, double entryInsulMm)
    {
        var d = DesignSpec.W08.Clone();
        d.TabLengthMm = tabLenMm;
        d.TabThickMm = (double[])SolvedTabThickMm.Clone();
        d.TongueThickMm = (double[])SolvedTongueThickMm.Clone();
        var ins = (double[])SolvedTabInsulMm.Clone();
        ins[0] = entryInsulMm;
        d.TabInsulMm = ins;
        d.RingMul = new[] { 1.00, 1.00, 1.00, 1.00 };
        d.SlotSpanDeg = new double[4];
        d.TabHoleRMm = new double[4];
        d.TabHoleAspect = new[] { 1.0, 1.0, 1.0, 1.0 };
        return d;
    }

    private sealed class Row
    {
        public double InsulMm, Delta, NetFluxW, ColdUnderTcK, HotOverTcK, FlangeDipK, SectionJ, TubeJ, MassG, Sec,
                      TabGenW, TabLossW, ClampW, PlateNetFluxW;
        public bool Ok, Converged, AllOk, ThermalOk, FieldOk;
        public string SectionWhere = "", Failed = "", Knobs = "";
    }

    private static (double Lo, double Hi)[] Blocks(List<Row> rows, Func<Row, bool> pass)
    {
        var outp = new List<(double, double)>();
        int i = 0;
        while (i < rows.Count)
        {
            if (!pass(rows[i])) { i++; continue; }
            int j = i;
            while (j + 1 < rows.Count && pass(rows[j + 1])) j++;
            // Δ 扫描时报 Δ 区间，保温扫描时报保温区间
            bool isDelta = rows.Any(r => Math.Abs(r.Delta) > 1e-12);
            outp.Add(isDelta ? (rows[i].Delta, rows[j].Delta) : (rows[i].InsulMm, rows[j].InsulMm));
            i = j + 1;
        }
        return outp.ToArray();
    }

    private static string HalfMmGrid(double lo, double hi)
    {
        var hits = new List<string>();
        for (double v = Math.Ceiling(lo * 2 - 1e-9) / 2; v <= hi + 1e-9; v += 0.5) hits.Add(v.ToString("0.0"));
        return hits.Count == 0 ? "**一档都落不进去**" : string.Join("、", hits);
    }

    private static double Slope(List<Row> rows)
    {
        var pts = rows.Where(r => r.Ok && !double.IsNaN(r.PlateNetFluxW)).ToList();
        if (pts.Count < 2) return double.NaN;
        double mx = pts.Average(r => r.InsulMm), my = pts.Average(r => r.PlateNetFluxW);
        double num = pts.Sum(r => (r.InsulMm - mx) * (r.PlateNetFluxW - my));
        double den = pts.Sum(r => (r.InsulMm - mx) * (r.InsulMm - mx));
        return den <= 0 ? double.NaN : num / den;
    }

    private static string J(double[] v) => string.Join("/", v.Select(x => x.ToString("0.00")));
    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("0.###");
}
