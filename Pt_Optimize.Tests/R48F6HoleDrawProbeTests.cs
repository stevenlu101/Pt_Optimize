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
//  F6 探针（2026-09-23，慢，只印 + 两条断言）：**管孔抽热随网格收敛，改前／改后同树**。
//
//  仿 HANDOVER §0.-15N ④ 那张表（「管孔抽热随网格收敛（W08 片0，A 导航档终值几何，定温模式、生产配方；ShellThermal.Solve 直接解）」）的算例：
//    几何 = 误差预算设计片 0（R48ClampRecipeTests.Design 那份输入，R48F6HoleFaceGateTests.BudgetDesign 逐字搬来）；h = 2（导航）／1／0.5／0.25（整片加密 MeshAdapt.RefineWholeMesh）；
//    电流场等温（控温点）1213 A，ShellCurrent.SolveFor；热场 ShellThermal.Solve，管根 1172.63 °C（树外探针 ProbeF6Design.cs 抽热收敛_三口径 同一套调用）。
//  四列（网格层规则经 MeshRules 注射，热场锚点经 ShellThermal 只供测试的 holeAnchorOnCircle 注射）：
//    改前   = HoleFaceDirichlet false、HoleArcNormalDist false、HoleTagArcOnly false、锚点按孔格形心 —— 本树 F6 之前的口径；
//    F6a    = 只开孔面上定电位；
//    F6a+b  = 再开弧面法向距；
//    全开   = 生产（F6a+b+c+d）。
//
//  ★ 2026-09-23（F6 审查后改，findings #20／#42 阻断项）：原先唯一的断言拿 OldTable = 12.9431／12.7965／12.6883／12.7340 当「§0.-15N ④ 原表」比 ±0.0005，
//    并在这里和实施记录写「改前列与 §0.-15N ④ 逐 4 位一致」—— **不实**：那四个 4 位数出自本次改动者自己在本树上的树外探针，§0.-15N ④ 原表只有 3 位小数，
//    而且对不上：原表（HANDOVER §0.-15N ④「改后」列）单元 1026／4164／15734／61322、抽热 12.943／12.797／12.687／12.734；
//    本树改前列 单元 1026／4164／15734／**61318**、抽热 12.9431／12.7965／**12.6883**／12.7340 ⇒ h = 0.5 差 +0.0013 W（大于原先的 ±0.0005），h = 0.25 单元差 4 格。
//    原表出自 §0.-15N（2026-09-18，N 树上的临时探针，印完即删）；N 在 09-19 之后还有 §0.-15N 没写的改动（碎格并入 CellMergeFrac、J 重构截断 SliverKappaMin 等，
//    见 HANDOVER §0.-17「N 在 09-19 做的、§0.-15N 里没有写的」），本树与原表**不是同一张网格** —— 差由哪一项造成没有逐项拆过（上面两项是候选，未验证）。
//  现在两条断言（界都是「选定」的，写在这里，跑前写死）：
//    a) 确定性：改前列 与 **本树自记值**（2026-09-23 03:37 本树 Linux 镜像跑出、落在 deliverable/F6_探针_管孔抽热收敛_改前改后_W08片0_本次开跑于2026-09-23_033708.txt；
//       **本树自记，不是原表**）单元数逐个相等、抽热差 ≤ 0.0005 W。只证同一份代码同一平台复跑得出同一个数，不证与原表一致。
//       0.0005 W 选定：自记值只有 4 位小数（舍入 ±0.00005），留 10 倍给跨平台浮点差；不是推导。
//    b) 与原表对照：|改前列 − 原表| ≤ 0.002 W（原表 3 位小数；h = 0.5 那一档实差 0.0013）且单元数相对差 ≤ 0.01 %（61318 对 61322 = 0.0065 %）。
//       0.002 W 与 0.01 % 都是**选定**的（看过上面那两处差之后定的，不是推导）：它只说「改前列与原表是同一个量、同一个几何量级上的数」，
//       不说逐位一致；逐档差值照印。
//  其余只印（探针，不是门）。
//
//  上一次实跑（Linux 镜像，2026-09-23 03:37，51 s（与别的长跑同机）；证据即上面那份 033708 文件，逐字抄自文件）：
//    h     单元    抽热 W：改前   F6a      F6a+b    全开     R µΩ 改前→全开     P_gen W 改前→全开    最热铂 °C 改前→全开   JMax 改前→全开   局部热稳定裕度 改前→全开
//    2     1026    12.9431  12.1474  12.1963  12.1963   299.716 → 302.325   444.109 → 444.863   1174.61 → 1174.97   14.571 → 14.407   7.012 → 6.728
//    1     4164    12.7965  12.4921  12.5093  12.5093   300.678 → 302.089   444.255 → 444.552   1174.70 → 1174.86   15.280 → 14.385   9.798 → 8.299
//    0.5   15734   12.6883  12.5897  12.5938  12.5938   301.320 → 302.043   444.371 → 444.471   1174.84 → 1174.84   16.034 → 14.388   10.184 → 10.124
//    0.25  61318   12.7340  12.6762  12.6781  12.6781   301.620 → 302.007   444.328 → 444.387   1174.86 → 1174.82   16.152 → 14.402   10.531 → 10.523
//    步长：改前 −0.147／−0.108／+0.046（导航对 0.25 档 +0.209 W）；全开 +0.313／+0.084／+0.084（导航对 0.25 档 −0.482 W）。
//    读法：改后抽热从下方单调逼近，后两步相等 ⇒ 四档内还没进渐近区；导航档误差由 +0.21 W 变为 −0.48 W。改前 R 偏低（导航档 299.716 对 302.325 µΩ）是本表直接印的；
//    「闭合偏高」本探针没有直接印（出处在门 b／带格闭合门的证据），两处抵消是解读，不是本探针量出来的。
//    F6c 在此几何上不参与（无非弧孔面）；局部热稳定裕度 全开 − F6a+b：−0.096／−0.148／−0.069／−0.023（F6d 那一步），改前 → F6a 那一步更大（h = 1：9.798 → 8.432）。
// ════════════════════════════════════════════════════════════════════════════
public class R48F6HoleDrawProbeTests
{
    private readonly ITestOutputHelper _o;
    public R48F6HoleDrawProbeTests(ITestOutputHelper o) { _o = o; }

    // 本树自记（2026-09-23 03:37，本树 Linux 镜像，F6 改动者的这支探针第一次跑出的改前列；deliverable/F6_探针_管孔抽热收敛_改前改后_W08片0_本次开跑于2026-09-23_033708.txt）
    //   —— **本树自记，不是原表**；只作确定性复现的参照。
    static readonly double[] SelfTube = { 12.9431, 12.7965, 12.6883, 12.7340 };
    static readonly int[] SelfCells = { 1026, 4164, 15734, 61318 };
    // HANDOVER §0.-15N ④ 原表「改后」列（3 位小数；单元取「改前→改后」的改后）：出自 N 树 2026-09-18 的临时探针，与本树不是同一张网格（见文件头）
    static readonly double[] OrigTube = { 12.943, 12.797, 12.687, 12.734 };
    static readonly int[] OrigCells = { 1026, 4164, 15734, 61322 };
    const double SelfTolW = 0.0005;     // 选定（见文件头 a）
    const double OrigTolW = 0.002;      // 选定（见文件头 b）
    const double OrigCellRel = 1e-4;    // 选定（0.01 %，见文件头 b）

    [Trait("速度", "慢")]
    [Fact]
    public void 探针_抽热收敛_改前改后同树()
    {
        var variants = new (string name, MeshRules rules, bool anchor)[]
        {
            ("改前", new MeshRules { HoleFaceDirichlet = false, HoleArcNormalDist = false, HoleTagArcOnly = false }, false),
            ("F6a", new MeshRules { HoleFaceDirichlet = true, HoleArcNormalDist = false, HoleTagArcOnly = false }, false),
            ("F6a+b", new MeshRules { HoleFaceDirichlet = true, HoleArcNormalDist = true, HoleTagArcOnly = false }, false),
            ("全开", MeshRules.Production, true),
        };
        var sb = new StringBuilder(); void W(string t = "") { sb.AppendLine(t); }
        string file = DeliverableOut.Stamped("F6_探针_管孔抽热收敛_改前改后_W08片0.txt");
        W("F6 探针：管孔抽热随网格收敛（改前／改后同树）　误差预算设计片0　定温模式、生产配方　电流 1213 A 等温（控温点）　管根 1172.63 °C");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-23，F6");
        W($"断言（跑前写死，界都是选定的）：a) 改前列对本树自记值（033708 那次，本树自记，不是原表）单元逐个相等、抽热差 ≤ {SelfTolW} W；"
          + $"b) 改前列对 HANDOVER §0.-15N ④ 原表（3 位小数，N 树 09-18 临时探针，与本树不是同一张网格）抽热差 ≤ {OrigTolW} W、单元相对差 ≤ {OrigCellRel:P2}。其余只印。");
        W("h\t口径\t单元\t抽热W\t发热W(电)\tR µΩ\tJmax\t热场收敛\t能量残差W\t最热铂°C\t局部热稳定最小裕度\t守恒误差\t耗时s");
        var tube = new double[4, variants.Length];
        var cellsOld = new int[4];
        int hi = 0;
        foreach (double h in new[] { 2.0, 1.0, 0.5, 0.25 })
        {
            for (int v = 0; v < variants.Length; v++)
            {
                var sw = Stopwatch.StartNew();
                var (lc, g, m) = R48F6HoleFaceGateTests.BudgetMesh(h, variants[v].rules);
                double tSet = lc.SetpointC[0];
                double rho = Materials.PtResistivity(tSet) * 1e3;
                var sc = ShellCurrent.SolveFor(lc, m, 1213, rho, tSet);
                var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet; if (lc.ClampTempC.Length > 0) p2.BusbarClampTempC = lc.ClampTempC[0];
                var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, 1172.63, g.InsulBoundaryXResolved, g.TwoTabs, tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                            discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm, holeAnchorOnCircle: variants[v].anchor);
                tube[hi, v] = th.QFromTubeW;
                if (v == 0) cellsOld[hi] = m.CellCount;
                W($"{h}\t{variants[v].name}\t{m.CellCount}\t{th.QFromTubeW:0.0000}\t{sc.TotalGenW:0.000}\t{rho / sc.CurrentInA * 1e6:0.0000}\t{sc.JMaxAPerMm2:0.000}\t{th.Converged}\t{th.EnergyResidualW:E2}\t{th.TMaxC:0.00}\t{th.LocalStabMargin:0.000}（L {th.LocalStabLatLenMm:0.00}）\t{sc.ConservationError:E1}\t{sw.Elapsed.TotalSeconds:0}");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));   // 逐行落盘（慢探针，半路断了也有数）
            }
            hi++;
        }
        W();
        W("抽热汇总 W（行 = h 2／1／0.5／0.25；列 = " + string.Join("／", variants.Select(v => v.name)) + "）");
        double[] hs = { 2.0, 1.0, 0.5, 0.25 };
        for (int i = 0; i < 4; i++) W($"{hs[i]}\t" + string.Join("\t", Enumerable.Range(0, variants.Length).Select(v => tube[i, v].ToString("0.0000"))));
        for (int v = 0; v < variants.Length; v++)
            W($"{variants[v].name} 步长：{tube[1, v] - tube[0, v]:+0.000;-0.000}／{tube[2, v] - tube[1, v]:+0.000;-0.000}／{tube[3, v] - tube[2, v]:+0.000;-0.000}　导航档对 0.25 档 {tube[0, v] - tube[3, v]:+0.000;-0.000} W");
        W();
        W("改前列 对 本树自记（033708，不是原表）与 HANDOVER §0.-15N ④ 原表（3 位小数）：");
        W("h\t单元 本次\t单元 自记\t单元 原表\t单元相对差(对原表)\t抽热 本次\t抽热 自记\t本次−自记 W\t抽热 原表\t本次−原表 W");
        var badL = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            double dSelf = tube[i, 0] - SelfTube[i], dOrig = tube[i, 0] - OrigTube[i];
            double cellRel = Math.Abs(cellsOld[i] - OrigCells[i]) / (double)OrigCells[i];
            W($"{hs[i]}\t{cellsOld[i]}\t{SelfCells[i]}\t{OrigCells[i]}\t{cellRel:P4}\t{tube[i, 0]:0.0000}\t{SelfTube[i]:0.0000}\t{Math.Round(dSelf, 4) + 0.0:+0.0000;-0.0000;0.0000}\t{OrigTube[i]:0.000}\t{Math.Round(dOrig, 4) + 0.0:+0.0000;-0.0000;0.0000}");
            if (cellsOld[i] != SelfCells[i]) badL.Add($"a) h={hs[i]} 单元 {cellsOld[i]} ≠ 自记 {SelfCells[i]}");
            if (!(Math.Abs(dSelf) <= SelfTolW)) badL.Add($"a) h={hs[i]} 抽热 {tube[i, 0]:0.0000} 对自记 {SelfTube[i]:0.0000} 差 {dSelf:+0.0000;-0.0000} W > {SelfTolW}");
            if (!(Math.Abs(dOrig) <= OrigTolW)) badL.Add($"b) h={hs[i]} 抽热 {tube[i, 0]:0.0000} 对原表 {OrigTube[i]:0.000} 差 {dOrig:+0.0000;-0.0000} W > {OrigTolW}");
            if (!(cellRel <= OrigCellRel)) badL.Add($"b) h={hs[i]} 单元 {cellsOld[i]} 对原表 {OrigCells[i]} 相对差 {cellRel:P4} > {OrigCellRel:P2}");
        }
        W($"── 断言：{(badL.Count == 0 ? "a、b 都过" : "不过 " + string.Join("；", badL))}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(badL.Count == 0, string.Join("；", badL));
    }
}
