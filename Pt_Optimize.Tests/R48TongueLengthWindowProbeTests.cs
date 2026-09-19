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
//  R48 P 路 探针：**舌板长度能不能把「净流入≈0」的窗口撑开？**（2026-09-17，Opus 5）
//
//  ══ 用户原话（2026-09-17）
//    「维持电流密度不许超 11 这条规则，绕路走」。
//
//  ══ 要验的那条推理（**是推理，不是结论**；本探针就是去验它）
//    舌板是一根从 1150 °C 的管根拉到 450 °C 夹头的电流引线：
//      · 它自身的焦耳热两三百瓦（量级）；
//      · 它从管孔抽走的热 = 沿舌板导出去的热 − 它自己发的热，是**两个大数之差**，只有几瓦。
//    于是「管孔净流入」对舌保温刀刃般敏感（L 路实测斜率 −10 W/mm，可行窗口宽 0.14 mm）。
//    若改用**舌板长度**去凑「净流入≈0」：焦耳热 ∝ 长度、导热 ∝ 1/长度，两者都随长度平缓变化，
//    窗口**应当**宽得多。—— 这是推理。本探针实测它。
//
//  ══ 算例（每个数都指得回出处）
//    几何与工况：内置设计「管壁 0.8 · 留余量」（DesignSpec.W08，**从代码读，不手抄**）。
//    法兰旋钮：昨天求解器解出来的终值 —— 板厚 0.73/1.26/1.26/0.73 mm、舌片厚 2.03/3.51/3.51/2.03 mm、
//      舌保温 5.1/2.3/3.6/10.4 mm、环倍率全 1.00、无圆盘槽、无舌孔。
//      出处：D:\WinForm\r48_L\deliverable\R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt
//      的「求解器解出来的设计」表（只读该文件；本探针不重跑求解器 —— 求解一次 67 分钟，跑不起 4 × 51 次）。
//    工况：**带玻璃稳态**，生产口径 LineRunner.Run(solved.BuildCase(p))，导航网格（整线算例缺省，
//      与那份文件的 ② 同一张网格 ⇒ 可比）。判定一律用生产判据 LineResult.AllOk / Failed，门槛一个不动。
//
//  ══ 扫法
//    外层：舌板长度 ∈ {140, 170, 200, 230} mm（压接段长 40 mm 不变）。
//      ⚠ 舌长的定义（Core/DesignSpec.cs）：TabLengthMm 只经两处进几何 ——
//        ① Plate()：TabEndXMm = −TabLengthMm（舌尖位置）；
//        ② FlangeKit / GeometryScreen：压接入口 x = −TabLengthMm + ClampLengthMm，
//           自由段 = 舌长 − 圆盘切点 − 压接段。等宽舌片且舌半宽 30 = 盘半径 30 ⇒ 切点 x = 0
//           （PlateCurrent2D.Tangent，TabParallel 分支）⇒ 自由段 = 舌长 − 40。
//        舌半宽、圆盘半径、压接段长都不随舌长动 ⇒ 最窄截面的**面积**不变（= 舌片厚 × 舌宽），
//        变的只是那个截面**在哪**（x = −(舌长−40)）与舌板的长度／面积／电阻。
//    内层：只扫**入口片**（片 0）舌保温，3.0 → 8.0 mm 每 0.1 mm，共 51 档；其余三片钉在终值 2.3/3.6/10.4。
//      每档跑一次整线全耦合解，逐档落盘。
//    可行窗口 = 该舌长下 AllOk 为真的舌保温区间 [下, 上]（0.1 步进），报宽度与落进去的 0.5 mm 档。
//
//  ══ 跑前写死的判读（2026-09-17 Opus 5，**跑之前写，跑完不许改**）
//    P1 推理预期：窗口宽度随舌长**单调变宽**。
//    P2 若 230 mm 时窗口仍 < 0.5 mm ⇒ 结论写「舌长这条路撑不开窗口」。
//    P3 若某个舌长下 51 档一个 AllOk 都没有 ⇒ 写「无可行点」，**不许放宽判据**去凑一个。
//    P4 方向错（舌长越长窗口反而越窄）⇒ 不停、照记，但在结论里点名。
//    P5 窗口顶到扫描边界（下界落在 3.0 或上界落在 8.0）⇒ 报「窗口被扫描区间截断，真实宽度 ≥ 这个数」。
//    P6 每个舌长另印：入口片舌板焦耳热 QGenTabW（**取自场解的逐格焦耳热求和，不是 I²R 估算**）、
//       舌板末端流进铜排夹的热 QClampW、入口片管孔净流入 QFromTubeW —— 让「两个大数之差」看得见。
//
//  ══ 快门（断言；不过就当场失败，不许把坏数写成报告）
//    S1 扫描区间：51 档、首 3.0、末 8.0、步进 0.1（逐档核对）。
//    S2 舌长确实进了几何：每片 TabEndXMm == −舌长，自由段 == 舌长 − 40，且自由段 ≥ 下界；
//       压接入口 x == −(舌长 − 40)。
//    S3 舌长确实改变了场：整线解的最紧截面位置字符串随舌长改变（140 → x=-100、170 → x=-130 …）。
//    S4 每档吃的是同一份设计、只有片 0 的舌保温不同（逐档核对四片旋钮）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48TongueLengthWindowProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48TongueLengthWindowProbeTests(ITestOutputHelper o) { _o = o; }

    // ── 昨天求解器解出来的终值（出处见文件头；只读 r48_L 那份 .txt，不重跑求解器）
    private static readonly double[] SolvedTabThickMm = { 0.73, 1.26, 1.26, 0.73 };
    private static readonly double[] SolvedTongueThickMm = { 2.03, 3.51, 3.51, 2.03 };
    private static readonly double[] SolvedTabInsulMm = { 5.1, 2.3, 3.6, 10.4 };

    // ── L 路那一份 ② 带玻璃稳态的记录值（舌长 140、入口片舌保温 5.1；同一文件同一次运行）
    //    只作锚点比对用 —— 不是判据，也不参与判定。
    private const double AnchorMassG = 4245, AnchorHotOverTc = 4.529, AnchorNetFlux = 0.656,
                         AnchorColdUnderTc = 4.511, AnchorSectionJ = 9.983, AnchorTubeJ = 9.506;

    private const double ScanLoMm = 3.0, ScanHiMm = 8.0, ScanStepMm = 0.1;
    private const int ScanCount = 51;

    /// <summary>
    /// 锚点：舌长 140、入口片舌保温 5.1（= 求解器终值）跑一次带玻璃稳态，与 L 路那份文件的 ② 逐条比对。
    /// 对不上 ⇒ 我按表复原出来的设计**不是**昨天那个设计，后面 4 × 51 次全白跑 ⇒ 先跑这一个（约 1–2 分钟）。
    /// </summary>
    [Fact]
    public void 锚点_按表复原的设计要复现L路那次带玻璃稳态()
    {
        var p = new DesignInputs();
        var d = BuildSolvedDesign(140.0, 5.1);
        var lc = d.BuildCase(p);
        var sw = Stopwatch.StartNew();
        var res = LineRunner.Run(lc, null);
        sw.Stop();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        W("R48 P 路　锚点：按表复原的设计 vs L 路那次带玻璃稳态（舌长 140、入口片舌保温 5.1）");
        W($"跑于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W($"耗时 {sw.Elapsed.TotalSeconds:0} s　整线解出来 {res.Ok}　耦合收敛 {res.Converged}　全判据通过 {res.AllOk}");
        W();
        W("量\t本次\tL 路记录值\t差");
        W($"铂重 g\t{res.TotalMassG:0}\t{AnchorMassG:0}\t{res.TotalMassG - AnchorMassG:+0.###;-0.###;0}");
        Cmp(W, "最热铂高出热偶读数 K", res.ValueOf(LineResult.Key.HotOverTc), AnchorHotOverTc);
        Cmp(W, "管孔净流入 W", res.ValueOf(LineResult.Key.NetFlux), AnchorNetFlux);
        Cmp(W, "管根低于热偶读数 K", res.ValueOf(LineResult.Key.ColdUnderTc), AnchorColdUnderTc);
        Cmp(W, "法兰截面电流密度 A/mm²", res.ValueOf(LineResult.Key.SectionJ), AnchorSectionJ);
        Cmp(W, "管电流密度 A/mm²", res.ValueOf(LineResult.Key.TubeJ), AnchorTubeJ);
        W();
        W("逐条判据（生产判据原样，门槛一个没动）：");
        foreach (var c in res.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target))
            W($"  {Criteria.Plain(c.Name)}：实际 {c.Actual:0.###} / 限值 {c.Limit:0.###} {c.Unit}，"
              + $"{(c.Undetermined ? "**无法判定**" : c.Ok ? "过" : "**不过**")}，位置 {c.Where}");
        _o.WriteLine(sb.ToString());
        Console.WriteLine(sb.ToString());

        // 快门：几何量对不上就当场停（后面 4 小时不许在错的设计上跑）
        Assert.True(res.Ok, "锚点：整线解没解出来 —— " + res.Message);
        Assert.True(Math.Abs(res.TotalMassG - AnchorMassG) <= 2,
            $"锚点：铂重 {res.TotalMassG:0.0} g 与 L 路记录 {AnchorMassG:0} g 差得超过 2 g ⇒ 复原的不是同一个设计");
        Assert.True(Math.Abs(res.ValueOf(LineResult.Key.SectionJ) - AnchorSectionJ) <= 0.02,
            "锚点：法兰截面电流密度对不上 ⇒ 复原的不是同一个设计");
        Assert.True(Math.Abs(res.ValueOf(LineResult.Key.NetFlux) - AnchorNetFlux) <= 0.05,
            "锚点：管孔净流入对不上 ⇒ 复原的不是同一个设计（这一条最敏感，差了就别往下跑）");
    }

    private static void Cmp(Action<string> W, string name, double now, double rec)
        => W($"{name}\t{now:0.###}\t{rec:0.###}\t{now - rec:+0.###;-0.###;0}");

    /// <summary>
    /// 舌长窗口探针：固定其余三片，只扫入口片舌保温 3.0→8.0 mm 每 0.1 mm，逐档跑整线带玻璃稳态。
    /// 一个舌长一次调用、一个文件（带开跑时刻），跑完一个报一个。
    /// </summary>
    [Theory]
    [InlineData(140.0)]
    [InlineData(170.0)]
    [InlineData(200.0)]
    [InlineData(230.0)]
    public void 舌长窗口探针(double tabLenMm)
    {
        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_P_舌长窗口探针_L{tabLenMm:0}_本次开跑于{stamp}.txt");
        var totalSw = Stopwatch.StartNew();

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void Flush() { try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        void Say(string s) { W(s); _o.WriteLine(s); Console.WriteLine(s); Flush(); }

        // ══ 表头：设置、出处、跑前判读一次写清（跑完不许改）
        W($"R48 P 路　舌长窗口探针　**舌板长度 {tabLenMm:0} mm**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-17 Opus 5");
        W("问题（用户 2026-09-17「维持电流密度不许超 11 这条规则，绕路走」）：");
        W("　舌保温这根旋钮的可行窗口只有 0.14 mm（L 路实测，−10 W/mm）。改用**舌板长度**去凑「管孔净流入≈0」，");
        W("　窗口会不会宽得多？——**这是推理，本探针实测它。**");
        W();
        W("── 算例（每个数指得回出处）");
        var seed0 = DesignSpec.W08;
        W($"几何与工况：内置设计「{seed0.Name}」（DesignSpec.W08，从代码读，不手抄）——"
          + $"管内径 {seed0.TubeIdMm:0.#}／壁厚 {seed0.WallMm:0.00}／管保温 {seed0.TubeInsulMm:0.#} mm，"
          + $"段长 {string.Join("/", seed0.SegLengthMm.Select(v => v.ToString("0")))} mm，"
          + $"设定 {string.Join("/", seed0.SetpointC.Select(v => v.ToString("0")))} °C，"
          + $"圆盘半径 {seed0.DiscRadiusMm:0.#}／舌半宽 {seed0.TabHalfWidthMm:0.#}／压接段长 {seed0.ClampLengthMm:0.#} mm，"
          + $"夹头 {seed0.ClampTempC:0} °C，设定电流密度 {seed0.JDesignAPerMm2:0.#}（终验限值 {seed0.JCheckAPerMm2:0.#}）A/mm²");
        W($"法兰旋钮：昨天求解器解出来的终值 —— 板厚 {J(SolvedTabThickMm)}／舌片厚 {J(SolvedTongueThickMm)}／"
          + $"舌保温 {J(SolvedTabInsulMm)} mm，环倍率全 1.00，无圆盘槽、无舌孔。");
        W("　出处：D:\\WinForm\\r48_L\\deliverable\\R48_L_端到端_求解再过三关_W08_本次开跑于2026-09-17_012720.txt");
        W("　的「求解器解出来的设计」表。**本探针不重跑求解器**（求解一次 67 分钟，跑不起 4 × 51 次）。");
        W($"★ 舌板长度是本探针**唯一动的形状量**：{tabLenMm:0} mm（内置档是 {seed0.TabLengthMm:0} mm）。压接段长不变。");
        W("工况：带玻璃稳态，生产口径 LineRunner.Run（Core/LineRunner.cs），导航网格 = 整线算例缺省"
          + "（与 L 路那份文件的带玻璃稳态那一关同一张网格 ⇒ 可比）。判定一律用生产判据，门槛一个不动。");
        W();
        W("── 锚点复现（跑长扫之前先做的一次，2026-09-17 11:42:12，本工作树 r48_P，耗时 49 s）");
        W("　舌长 140、入口片舌保温 5.1（= 求解器终值）跑一次带玻璃稳态，与 L 路那份文件的带玻璃稳态那一关逐条比：");
        W("　　铂重 4245 vs 4245 g（差 +0.2）／最热铂高出热偶读数 4.529 vs 4.529 K（差 0）／管孔净流入 0.657 vs 0.656 W（差 +0.001）／");
        W("　　法兰截面电流密度 9.983 vs 9.983／管电流密度 9.506 vs 9.506（差都是 0）——**按表复原的就是那个设计**。");
        W("　★ 唯一对不上的一条：**管根低于热偶读数 4.922（本树）vs 4.511（L 树），差 +0.411 K，而且最坏的片不同**");
        W("　　（本树 HC1|HC2，L 树 出口）。原因是两棵树的代码不同：本探针按工单建在**合并树 r48_M** 上，");
        W("　　r48_L 另有一项段间端温不动点停机改动（放大倍数改成当场算 1+ℓt/Δx，历史口径写死 25）尚未合进来。");
        W("　　⇒ 本探针的**窗口位置**带这一截差；四个舌长都跑在**同一棵树**上 ⇒ 舌长之间的比较不受影响。");
        W("　　⚠ 这一条在锚点处只剩 5 − 4.922 = 0.078 K 裕度 ⇒ 它很可能就是本树里卡住窗口的那一条，读结果时不许略掉这句。");
        W();
        W("── 舌长怎么进的几何（Core/DesignSpec.cs 的定义，照抄字段关系，不是推测）");
        W("　其一　Plate()：舌尖 TabEndXMm = −舌长；其二　压接入口 x = −舌长 + 压接段长；");
        W("　其三　自由段 = 舌长 − 圆盘切点 − 压接段（GeometryScreen）。等宽舌片、舌半宽 30 = 盘半径 30 ⇒ 切点 x = 0");
        W("　（PlateCurrent2D.Tangent 的 TabParallel 分支）⇒ 自由段 = 舌长 − 40。");
        W("　舌半宽、盘半径、压接段长都不随舌长动 ⇒ 最窄截面的**面积**不变（舌片厚 × 舌宽），");
        W("　变的是那个截面**在哪**（x = −(舌长−40)）以及舌板的长度／面积／电阻。");
        W();
        W("── 跑前写死的判读（2026-09-17 Opus 5，跑之前写，跑完不许改）");
        W("　P1 推理预期：窗口宽度随舌长**单调变宽**。");
        W("　P2 若 230 mm 时窗口仍 < 0.5 mm ⇒「舌长这条路撑不开窗口」。");
        W("　P3 若某个舌长下 51 档一个都不通过 ⇒「无可行点」，不许放宽判据。");
        W("　P4 方向错（越长窗口越窄）⇒ 不停、照记，结论里点名。");
        W("　P5 窗口顶到扫描边界 ⇒「窗口被扫描区间截断，真实宽度 ≥ 这个数」。");
        W("　P6 每个舌长另印入口片的舌板焦耳热 / 舌板末端进铜排夹的热 / 管孔净流入。");
        W();

        // ══ 快门 S1：扫描区间
        var scan = new List<double>();
        for (int i = 0; i < ScanCount; i++) scan.Add(Math.Round(ScanLoMm + i * ScanStepMm, 1));
        Assert.Equal(ScanCount, scan.Count);
        Assert.Equal(ScanLoMm, scan[0], 9);
        Assert.Equal(ScanHiMm, scan[^1], 9);
        for (int i = 1; i < scan.Count; i++)
            Assert.True(Math.Abs(scan[i] - scan[i - 1] - ScanStepMm) < 1e-9, $"扫描步进不是 {ScanStepMm} mm：{scan[i - 1]} → {scan[i]}");
        W($"扫描：入口片舌保温 {ScanLoMm:0.0} → {ScanHiMm:0.0} mm 每 {ScanStepMm:0.0} mm，共 {ScanCount} 档；"
          + $"其余三片钉在 {SolvedTabInsulMm[1]:0.0}/{SolvedTabInsulMm[2]:0.0}/{SolvedTabInsulMm[3]:0.0} mm 不动。");

        // ══ 快门 S2：舌长确实进了几何（在跑第一个点之前先验，省得白跑）
        {
            var dChk = BuildSolvedDesign(tabLenMm, SolvedTabInsulMm[0]);
            Assert.Equal(tabLenMm, dChk.TabLengthMm, 9);
            var lcChk = dChk.BuildCase(p);
            double free0 = double.NaN, clampX0 = double.NaN;
            for (int j = 0; j < lcChk.FlangePlates.Length; j++)
            {
                var g = lcChk.FlangePlates[j];
                Assert.True(Math.Abs(g.TabEndXMm + tabLenMm) < 1e-9,
                    $"片 {j} 的舌尖 x={g.TabEndXMm:0.###} 与舌长 {tabLenMm:0} 对不上 ⇒ 舌长没进几何");
                double tangent = Math.Abs(g.Tangent().X);
                double free = Math.Abs(g.TabEndXMm) - tangent - seed0.ClampLengthMm;
                double clampX = g.TabEndXMm + seed0.ClampLengthMm;
                if (j == 0) { free0 = free; clampX0 = clampX; }
                Assert.True(Math.Abs(free - (tabLenMm - seed0.ClampLengthMm)) < 1e-6,
                    $"片 {j} 自由段 {free:0.###} ≠ 舌长−压接段 {tabLenMm - seed0.ClampLengthMm:0.###}（切点不在 0？）");
                Assert.True(Math.Abs(clampX + (tabLenMm - seed0.ClampLengthMm)) < 1e-9,
                    $"片 {j} 压接入口 x={clampX:0.###} 与舌长对不上 ⇒ 压接入口没随舌长走");
            }
            W($"快门 S2（舌长进了几何）：舌尖 x = {-tabLenMm:0.#} mm，压接入口 x = {clampX0:0.#} mm，"
              + $"自由段 = {free0:0.#} mm（= 舌长 − 压接段 {seed0.ClampLengthMm:0.#}；切点 x = 0）。");
        }
        W();

        // ══ 逐档跑
        W("── 逐档（每档一次整线带玻璃稳态全耦合解；算完立刻落盘）");
        W("舌保温 mm\t判定\t管孔净流入 W\t管根低于热偶读数 K\t最热铂高出热偶读数 K\t法兰增量温降 K\t截面电流密度\t管电流密度\t场有效\t收敛\t铂重 g\t耗时 s\t入口片舌板焦耳热 W\t入口片进铜排夹 W\t入口片管孔净流入 W\t最紧截面在哪\t没过的");
        Flush();

        var rows = new List<Row>();
        string sectionWhere0 = "";
        foreach (double ins in scan)
        {
            var d = BuildSolvedDesign(tabLenMm, ins);
            // 快门 S4：每档只有片 0 的舌保温不同，其余旋钮逐位相同
            Assert.Equal(ins, d.TabInsulMm[0], 9);
            for (int j = 1; j < SolvedTabInsulMm.Length; j++) Assert.Equal(SolvedTabInsulMm[j], d.TabInsulMm[j], 9);
            for (int j = 0; j < SolvedTabThickMm.Length; j++)
            {
                Assert.Equal(SolvedTabThickMm[j], d.TabThickMm[j], 9);
                Assert.Equal(SolvedTongueThickMm[j], d.TongueThickMm[j], 9);
                Assert.Equal(1.0, d.RingMul[j], 9);
            }

            var lc = d.BuildCase(p);
            var sw = Stopwatch.StartNew();
            LineResult res;
            string threw = "";
            try { res = LineRunner.Run(lc, null); }
            catch (Exception ex) { threw = $"{ex.GetType().Name}：{ex.Message}"; res = new LineResult { Ok = false, Message = threw }; }
            sw.Stop();

            var f0 = res.Flanges.Length > 0 ? res.Flanges[0] : null;
            var row = new Row
            {
                InsulMm = ins,
                Ok = res.Ok,
                Converged = res.Converged,
                AllOk = res.Ok && res.AllOk,
                NetFluxW = res.ValueOf(LineResult.Key.NetFlux),
                ColdUnderTcK = res.ValueOf(LineResult.Key.ColdUnderTc),
                HotOverTcK = res.ValueOf(LineResult.Key.HotOverTc),
                FlangeDipK = res.ValueOf(LineResult.Key.FlangeDip),
                SectionJ = res.ValueOf(LineResult.Key.SectionJ),
                TubeJ = res.ValueOf(LineResult.Key.TubeJ),
                // 场有效 = 每一片的电位场与温度场都收敛（FlangeOut.FieldsConverged；本树没有 LineResult 级的汇总位）
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
            rows.Add(row);
            if (sectionWhere0.Length == 0) sectionWhere0 = row.SectionWhere;

            Say($"{row.InsulMm:0.0}\t{(row.AllOk ? "过" : "不过")}\t{N(row.NetFluxW)}\t{N(row.ColdUnderTcK)}\t{N(row.HotOverTcK)}\t"
              + $"{N(row.FlangeDipK)}\t{N(row.SectionJ)}\t{N(row.TubeJ)}\t{(row.FieldOk ? "是" : "否")}\t{(row.Converged ? "是" : "否")}\t"
              + $"{row.MassG:0}\t{row.Sec:0}\t{N(row.TabGenW)}\t{N(row.ClampW)}\t{N(row.PlateNetFluxW)}\t{row.SectionWhere}\t{row.Failed}");
        }

        // ══ 快门 S3：最紧截面的位置随舌长走（= 舌长确实改了场，不只是改了个字段）
        string expectX = $"x={-(tabLenMm - seed0.ClampLengthMm):0}";
        W();
        W($"快门 S3（舌长改了场）：整线解报出来的最紧截面位置「{sectionWhere0}」，"
          + $"按定义应落在压接入口 {expectX} mm。");
        Assert.Contains(expectX, sectionWhere0);

        // ══ 窗口
        W();
        W("── 可行窗口（AllOk 为真的连续档；判定用生产判据，门槛一个没动）");
        var okRows = rows.Where(r => r.AllOk).ToList();
        var blocks = ContiguousBlocks(rows);
        if (okRows.Count == 0)
        {
            W("★ **无可行点**：51 档一档都没过（判读 P3：不许放宽判据去凑一个）。");
            W("　逐档最靠近可行的是哪几条（按没过的条目数排）：");
            foreach (var r in rows.OrderBy(r => r.Failed.Length).Take(5))
                W($"　　舌保温 {r.InsulMm:0.0} mm：{r.Failed}");
        }
        else
        {
            foreach (var (lo, hi) in blocks)
                W($"　窗口 [{lo:0.0}, {hi:0.0}] mm，宽度 {hi - lo + ScanStepMm:0.0} mm"
                  + $"（含端点，0.1 步进；{(lo <= ScanLoMm + 1e-9 || hi >= ScanHiMm - 1e-9 ? "**顶到扫描边界 ⇒ 真实宽度 ≥ 这个数**（判读 P5）" : "两端都在扫描区间内")}）"
                  + $"　落进去的 0.5 mm 档：{HalfMmGrid(lo, hi)}");
        }
        W();
        W("── 两个大数之差（判读 P6；入口片，取自整线解的场，不是 I²R 估算）");
        W("　舌板焦耳热 = FlangeOut.QGenTabW（该片最后一次热解的逐格焦耳热，按圆盘/舌片以切点分区求和）；");
        W("　进铜排夹的热 = FlangeOut.QClampW（能量恒等式 Q夹 = Q发热 + Q从管吸热 − Q表面散热）；");
        W("　管孔净流入 = FlangeOut.QFromTubeW（>0 = 从管子抽热）。");
        W("舌保温 mm\t舌板焦耳热 W\t舌板表面散热 W\t进铜排夹 W\t管孔净流入 W\t（焦耳热 − 进夹）W");
        foreach (var r in rows.Where(r => (int)Math.Round(r.InsulMm * 10) % 5 == 0))
            W($"{r.InsulMm:0.0}\t{N(r.TabGenW)}\t{N(r.TabLossW)}\t{N(r.ClampW)}\t{N(r.PlateNetFluxW)}\t{N(r.TabGenW - r.ClampW)}");
        W("（只印 0.5 mm 整档，全 51 档在上面的逐档表里。）");
        W();

        double slope = Slope(rows);
        W($"── 入口片管孔净流入对舌保温的斜率（最小二乘，全 51 档）：{slope:0.###} W/mm"
          + "（L 路在舌保温这根旋钮上实测约 −10 W/mm；两处都是带玻璃稳态、导航网格）。");
        W();
        W("── 一句话");
        W(okRows.Count == 0
            ? $"舌长 {tabLenMm:0} mm：**无可行点**（51 档全不过）。"
            : $"舌长 {tabLenMm:0} mm：可行窗口 {string.Join("、", blocks.Select(b => $"[{b.Lo:0.0}, {b.Hi:0.0}] 宽 {b.Hi - b.Lo + ScanStepMm:0.0} mm"))}，"
              + $"铂重 {okRows[0].MassG:0} g，入口片舌板焦耳热 {N(okRows[0].TabGenW)} W、进铜排夹 {N(okRows[0].ClampW)} W、"
              + $"管孔净流入 {N(okRows[0].PlateNetFluxW)} W（取窗口下端那一档）。");
        W();

        totalSw.Stop();
        W($"── 总耗时 {totalSw.Elapsed.TotalMinutes:0.0} 分钟（{DateTime.Now:yyyy-MM-dd HH:mm:ss}），{rows.Count} 次整线解。");
        W("出处：整线带玻璃稳态 = LineRunner.Run（Core/LineRunner.cs）；判定 = LineResult.AllOk / Failed（生产判据，门槛未改）。");
        W("每个数来自同一次运行，不拼两份输出。　注记：2026-09-17，Opus 5。");
        Flush();
        _o.WriteLine(sb.ToString());

        Assert.True(rows.Count == ScanCount, "扫描档数对不上");
        Assert.True(rows.Any(r => r.Ok), $"舌长 {tabLenMm:0}：51 档一次整线解都没解出来 —— 这不是「不可行」，是算不出来");
    }

    /// <summary>
    /// 按「昨天求解器解出来的终值」复原设计：只改舌长与入口片舌保温，其余照终值钉死。
    /// ⚠ 几何与工况一律从 <see cref="DesignSpec.W08"/> 读（不手抄）；只有求解器解出来的五个旋钮按表写回。
    /// </summary>
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
        d.SlotSpanDeg = new double[4];                       // 无圆盘槽（求解器终值：槽张角 0）
        d.TabHoleRMm = new double[4];                        // 无舌孔（求解器终值：孔径 0）
        d.TabHoleAspect = new[] { 1.0, 1.0, 1.0, 1.0 };
        return d;
    }

    private sealed class Row
    {
        public double InsulMm, NetFluxW, ColdUnderTcK, HotOverTcK, FlangeDipK, SectionJ, TubeJ, MassG, Sec,
                      TabGenW, TabLossW, ClampW, PlateNetFluxW;
        public bool Ok, Converged, AllOk, FieldOk;
        public string SectionWhere = "", Failed = "";
    }

    private static (double Lo, double Hi)[] ContiguousBlocks(List<Row> rows)
    {
        var outp = new List<(double, double)>();
        int i = 0;
        while (i < rows.Count)
        {
            if (!rows[i].AllOk) { i++; continue; }
            int j = i;
            while (j + 1 < rows.Count && rows[j + 1].AllOk) j++;
            outp.Add((rows[i].InsulMm, rows[j].InsulMm));
            i = j + 1;
        }
        return outp.ToArray();
    }

    /// <summary>窗口里落得进去的 0.5 mm 图纸档（现场按 0.5 mm 一层包，落不进去 = 现场包不出来）。</summary>
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
