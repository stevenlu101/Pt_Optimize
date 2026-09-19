using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 实验 a（2026-09-14，Opus 5 写；规格按常驻数值把关人第四轮）：
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
/// **半径效应是不是「压接段边界不在网格节点上」造成的？**
///
/// 探针一（`R48_探针一_细区半径_2026-09-14.txt`）实测：抽热随细区半径的离群点**在四片上完全一致**
/// （h=2.0 时 R59 离群、h=1.0 时 R50 离群、h=0.5 时排序 R59 &lt; R50 &lt; ∞ &lt; R90），
/// 而四片的板厚、舌保温、电流、夹持温度都不同 ⇒ 这个效应只取决于网格节点怎么摆，与物理无关。
///
/// 候选（读码）：压接面按「面中点 x ≤ 舌尖 + 压接长」判（ShellMesh.BuildFromField），
/// 而 <c>AnchorsOf</c> 只放切点 x 为节点，**没放 x = 舌尖 + 压接长**。压接段在粗区里，
/// 被钉住那一段的长度只能取到最近的节点，随分级轴从哪里（R）开始长而跳，最多一个粗格。
///
/// 另一刀（数值把关人）：直舌片上电位线性，两点通量的有限体积在任何张量网格上都精确再现线性电位，
/// 所以直段的 ΣJ²tA 本不该随 R 变。**若它随 R 变，病在电流场；若它不变，病在温度场或边界条件。**
///
/// 判读（跑之前写死；2026-09-14 第二版，按数值把关人更正）：
///   ⚠ 第一版写「舌区 ΣJ²tA 跨 R 变 ⇒ 病在电流场」**是错的**：电流场的等电位电极就是贴着压接面的那批格子
///     （ShellCurrent：TagTabEnd ⇒ isFixed = V 1），与热场钉成夹持温度的是同一批。压接边界随网格对齐一跳，
///     电极长度跟着跳，舌区 ΣJ²tA **本来就该跟着跳** —— 那一支分不开「电极对齐」与「电流场别处的离散误差」。
///   三列三条：
///     (iii) 相位 = (舌尖 x + 压接长) − 最里侧那个压接面的中点 x（带符号 mm），与那一列的格宽；
///     (ii)  舌区 ΣJ²tA（盘半径以外全部格子，含被钉住的）；
///     (i)   直段 J²t：只取 x ∈ [压接边界 + 两个粗格, −盘半径 − 两个细格] 的格子，**面积加权平均** ΣJ²tA ÷ ΣA。
///           ⚠ 第二版按「÷ 名义段长 x1−x0」算，**是测量方法错了**（2026-09-14 Opus 5 记）：按形心选整格求和、
///           却除以名义长度，窗口两端落在粗格（h=2.0 时 11 mm，窗口只有 44 mm）里时两者可差近一格。
///           证据在看新结果之前就有：窗口全在均匀细格里的 R=90 与均匀网格，h=2.0/1.0 时逐位一致到 1e-4，
///           偏差只出在窗口横跨粗细过渡的 R=50/59。均匀电流下 J²t 处处相等，面积加权平均与窗口怎么切无关。
///           直段电位线性，两点通量有限体积在任何张量网格上都精确再现 ⇒ 若对齐假说成立，它应**跨 R 平**。
///   · (iii) 与离群规律对得上、(ii) 跟着跳、(i) 平 ⇒ 电极／钉温区对齐成立 ⇒ 做实验 b；
///   · (i) 跨 R 变 ⇒ 电流场在直段之外还有别的离散问题，与对齐无关，先查它；
///   · (iii) 对不上 ⇒ 假说不成立，b 不用做。
/// 设置：单片、管侧固定、孔边判定带宽取生产默认（传 0）、保温按半径；管侧条件同探针一。
///
/// ══ 实验 b（2026-09-14，Opus 5 改；本测试现在跑的是 b）
/// 实验 a 的记录是 `R48_实验a_压接段相位_2026-09-14.txt`（13:06–13:07 那一趟，第三版口径）。
///   ⚠ 第三版写进了与第二版同一个文件名，**第二版的「直段平均 J²t」一列被覆盖，那一列指不回出处了**；
///   其余列（抽热、舌区发热、钉住格数、相位、格宽、舌区 ΣJ²tA）两版是同一段代码算的，数值把关人引用的第二版数在第三版文件里逐位查得到。
/// 之后 `FlangeMesher.BuildFromField` 把 x = 舌尖 + 压接长 落成节点（写进 ShellMesh.ClampAnchorNote）。本测试改写到实验 b 的文件，
/// 不再覆盖实验 a 的记录；再跑实验 a 已不可能（生成器变了），要对照只能读那份文件。
///
/// 实验 b 加印（数值把关人第五轮）：
///   · 边偏移 = 相位 − 格宽/2（正 = 钉住区比真实压接段短）。相位量的是面中点，钉住区的边在中点外半格；锚点接上后应全为 0。
///   · 窗口内 J²t 相对极差：直段窗口里逐格 J²t 的 (最大−最小)/最大。≥ 0.5 % ⇒ 窗口还在电极扩散区里，第 (i) 列**测量无效**，不能判平。
///   · d = 抽热(R) − 抽热(∞)，同片同 h。
/// 判读（跑之前写死；数值把关人第六轮定。预测来自实验 a 片 2：h=0.5 三个 R 共线，抽热偏差 ≈ −2.04 − 3.28 × 边偏移 W）：
///   · 任一行网格记录不是「已落成节点」，或边偏移不全为 0 ⇒ 锚点没接上，其余不看；
///   · 均匀网格（∞）的抽热与舌区 ΣJ²tA 必须与实验 a 文件逐位一致（均匀网格压接边界本来就是节点，锚点不该动它）
///     ⚠ 实验 a 只留下打印值（抽热 3 位小数、ΣJ²tA 1 位小数），所以只能在打印精度上比，比不到 1e-9；对不上 ⇒ d 与预测不可比；
///   · h=0.5 片 2 三个 d **都 ≤ −1.0 W** 且三者极差 ≤ 0.45 W ⇒ 对齐与粗格两个分量都确认
///     （−1.0 是预测值 −2.04 与门槛带 0.45 的中点，来自假说，不来自结果）；
///   · 三个 |d| 都 ≤ 0.45 W ⇒ 预测错了，只有对齐一个分量；
///   · 任一 d &lt; −4.63 W（比加锚点前最差那行还差）或三者极差 &gt; 6.55 W（加锚点前的极差）⇒ **锚点引入了新误差**；
///   · 其余 ⇒ 两种说法都不成立，记下来，先不往下做。
///   h=2.0、1.0 的 d 与极差只打印、不下判读（没有针对它们的预测）；边偏移在这两档也必须为 0。
/// ⚠ 均匀网格自己的钉住区边本来就落在节点上，锚点**改善不了**均匀网格自身不收敛（E_h），实验 c 照做。
/// ⚠ 2026-09-14 14:25（Opus 5）：**本探针是记录，不是回归门**。之后生产代码加了保温分界格按有料面积份额混合散热（ShellThermal），
///   均匀网格与分界相关的值都会变（例：片0 h=0.5 −14.928 → −15.103 W），再跑会与本探针里写死的旧记录对不上、断言变红。
///   结论以当时的输出文件为准；提交前要把它改成跳过（写明原因）或删掉写死的旧值，不许改数让它变绿。
/// </summary>
[Trait("速度", "慢")]
public class R48ClampPhaseTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampPhaseTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 实验b_压接边界落节点后钉住长度与舌区电流平方积分跨半径对照()
    {
        var sb = new StringBuilder();
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(Path.Combine(Root(), "deliverable", "R48_实验b_压接边界落节点_2026-09-14.txt"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 实验 b：压接边界落节点之后，压接段钉住长度与舌区 ΣJ²tA 跨半径对照（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("单片、管侧固定、孔边判定带宽生产默认、保温按半径。管侧条件同探针一。R=∞ 为均匀网格。");

        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1141.8, 1107.8, 1057.8, 1038.0 };
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        double coarseRatio = lc.MeshCoarseMm / lc.MeshFineMm;
        double clampLen = lc.Base.BusbarClampLengthMm;
        Say($"压接长 {clampLen:0.0} mm；圆盘保温（算例实际值）{lc.Base.FlangeInsulThickMm:0.0} mm");

        var q = new Dictionary<(int j, double h, double R), double>();
        var pin = new Dictionary<(int j, double h, double R), double>();
        var j2 = new Dictionary<(int j, double h, double R), double>();
        var lin = new Dictionary<(int j, double h, double R), double>();
        var edge = new Dictionary<(int j, double h, double R), double>();
        var winRng = new Dictionary<(int j, double h, double R), double>();
        var notAnchored = new List<string>();
        var uniMoved = new List<string>();
        // 实验 a 第三版文件（deliverable/R48_实验a_压接段相位_2026-09-14.txt，13:06）R=∞ 行的打印值：(h, 片) → (抽热 W, 舌区 ΣJ²tA)
        var expA = new Dictionary<(double h, int j), (double q, double s)>
        {
            [(2.0, 0)] = (-13.287, 1038444.4), [(2.0, 1)] = (-63.283, 1800529.5), [(2.0, 2)] = (-109.503, 1800529.5), [(2.0, 3)] = (-67.353, 1038444.4),
            [(1.0, 0)] = (-13.892, 1044890.0), [(1.0, 1)] = (-64.568, 1811705.2), [(1.0, 2)] = (-111.414, 1811705.2), [(1.0, 3)] = (-68.662, 1044890.0),
            [(0.5, 0)] = (-14.928, 1046949.9), [(0.5, 1)] = (-66.504, 1815276.9), [(0.5, 2)] = (-113.400, 1815276.9), [(0.5, 3)] = (-69.763, 1046949.9),
        };
        foreach (double h in new[] { 2.0, 1.0, 0.5 })
        {
            Say($"── h = {h:0.0} mm");
            Say("   片  半径     单元   抽热W     舌区发热W  钉住格数  相位mm   该列格宽   边偏移mm   舌区ΣJ²tA   直段平均J²t  窗口内J²t极差  d=抽热−∞");
            for (int j = 0; j < 4; j++)
            {
                var g = d.Plate(j, d.DiscFloorMm(p));
                g.HoleRadiusMm = holeR;
                var (xa, za) = FlangeMesher.AnchorsOf(g);
                var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
                double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
                var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
                if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                double tip = g.TabTipXMm, rDisc2 = g.DiscRadiusMm * g.DiscRadiusMm;
                foreach (double R in new[] { 50.0, 59.0, 90.0, double.PositiveInfinity })
                {
                    bool uni = double.IsPositiveInfinity(R);
                    var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, uni ? h : coarseRatio * h, uni ? 1e6 : R,
                                                        clampLen, h, 0, g.TwoTabs, xa, za);
                    var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);
                    var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                                tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j],
                                                discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
                    var clampFaces = m.Faces.Where(f => f.Tag == ShellMesh.TagTabEnd).ToArray();
                    var pinnedCells = clampFaces.Select(f => f.A).Distinct().ToArray();
                    // (iii) 相位：判定本身用的就是面中点，直接读它（面对象存了 Mid）
                    var inner = clampFaces.OrderByDescending(f => f.Mid.X).First();
                    double phase = (tip + clampLen) - inner.Mid.X;
                    double colW = inner.Length;                       // 侧面平行于 x，其边长即该列格宽
                    // (ii) 舌区 ΣJ²tA：盘半径以外全部格子
                    double sumJ2 = 0;
                    // (i) 直段线密度：离压接边界两个粗格、离盘边两个细格
                    double x0 = tip + clampLen + 2 * coarseRatio * h, x1 = -g.DiscRadiusMm - 2 * h, sumStraight = 0, areaStraight = 0;
                    double wMin = double.PositiveInfinity, wMax = double.NegativeInfinity;
                    for (int i = 0; i < m.CellCount; i++)
                    {
                        double cx = m.Centroid[i].X, cz = m.Centroid[i].Z;
                        double jj = sc.JMagAPerMm2[i], e = jj * jj * m.Thickness[i] * m.Area[i];
                        if (cx * cx + cz * cz > rDisc2) sumJ2 += e;
                        if (cx >= x0 && cx <= x1)
                        {
                            sumStraight += e; areaStraight += m.Area[i];
                            double jt = jj * jj * m.Thickness[i];
                            wMin = Math.Min(wMin, jt); wMax = Math.Max(wMax, jt);
                        }
                    }
                    double lineDen = areaStraight > 0 ? sumStraight / areaStraight : double.NaN;   // 面积加权平均 J²t
                    double edgeOff = phase - colW / 2;
                    double rng = wMax > 0 ? (wMax - wMin) / wMax : double.NaN;
                    q[(j, h, R)] = th.QFromTubeW; pin[(j, h, R)] = phase; j2[(j, h, R)] = sumJ2; lin[(j, h, R)] = lineDen;
                    edge[(j, h, R)] = edgeOff; winRng[(j, h, R)] = rng;
                    // 均匀网格排在最后，前三行的 d 要等它算完才有 ⇒ 每片四行解完再补印 d（见下）
                    Say($"   {j}  {(uni ? "  ∞" : R.ToString("0")),4}  {m.CellCount,7}  {th.QFromTubeW,8:+0.000;-0.000}  {th.QGenTabW,9:0.00}"
                      + $"  {pinnedCells.Length,8}  {phase,7:+0.000;-0.000}  {colW,8:0.000}  {edgeOff,9:+0.000;-0.000}  {sumJ2,11:0.0}  {lineDen,12:0.00}  {rng,12:0.0000}"
                      + (uni ? $"  {0.0,9:+0.000;-0.000}" : ""));
                    // 数值把关人第六轮第 1 条：每行都核记录，并打印压接边界两侧两列的格宽
                    var nodeX = m.Nodes.Select(v => v.X).Distinct().OrderBy(v => v).ToArray();
                    double edgeX = g.TwoTabs ? -(Math.Abs(tip) - clampLen) : tip + clampLen;
                    int ie = Array.FindIndex(nodeX, v => Math.Abs(v - edgeX) < 1e-6);
                    string widths = ie > 0 && ie < nodeX.Length - 1
                        ? $"压接侧 {nodeX[ie] - nodeX[ie - 1]:0.000} / 自由侧 {nodeX[ie + 1] - nodeX[ie]:0.000} mm"
                        : "压接边界 x 不是节点";
                    bool anchored = m.ClampAnchorNote.Contains("已落成节点") && !m.ClampAnchorNote.Contains("没加") && ie > 0;
                    if (!anchored) notAnchored.Add($"h={h:0.0} 片{j} R={(uni ? "∞" : R.ToString("0"))}");
                    Say($"        网格记录：{m.ClampAnchorNote}　两侧格宽：{widths}");
                    if (uni)
                    {
                        var (qa, sa) = expA[(h, j)];
                        bool same = Math.Abs(th.QFromTubeW - qa) <= 0.0005 + 1e-9 && Math.Abs(sumJ2 - sa) <= 0.05 + 1e-6;
                        if (!same) uniMoved.Add($"h={h:0.0} 片{j}：抽热 {th.QFromTubeW:+0.000;-0.000} vs 实验a {qa:+0.000;-0.000}，ΣJ²tA {sumJ2:0.0} vs {sa:0.0}");
                    }
                }
            }
        }

        // ── 判读（实验 b，跑之前写死，见类注释）
        Say("");
        Say("── d = 抽热(R) − 抽热(∞)，同片同 h");
        var Rs = new[] { 50.0, 59.0, 90.0, double.PositiveInfinity };
        var Rf = new[] { 50.0, 59.0, 90.0 };
        foreach (double h in new[] { 2.0, 1.0, 0.5 })
            for (int j = 0; j < 4; j++)
                Say($"   h={h:0.0} 片{j}：" + string.Join("　", Rf.Select(R => $"R{R:0} d={q[(j, h, R)] - q[(j, h, double.PositiveInfinity)]:+0.000;-0.000} W"))
                  + $"　三者极差 {Rf.Max(R => q[(j, h, R)]) - Rf.Min(R => q[(j, h, R)]):0.000} W"
                  + $"　窗口内 J²t 极差最大 {Rs.Max(R => winRng[(j, h, R)]):0.0000}");
        bool edgeZero = edge.Values.All(v => Math.Abs(v) < 1e-6) && notAnchored.Count == 0;
        double[] d05 = Rf.Select(R => q[(2, 0.5, R)] - q[(2, 0.5, double.PositiveInfinity)]).ToArray();
        double spread05 = d05.Max() - d05.Min();
        bool winValid = Rs.All(R => winRng[(2, 0.5, R)] < 0.005);
        Say("");
        Say($"★ 锚点{(notAnchored.Count == 0 ? "每行都已落成节点" : "**有没落成的行**：" + string.Join("；", notAnchored))}；"
          + $"边偏移{(edge.Values.All(v => Math.Abs(v) < 1e-6) ? "全为 0" : $"**不全为 0**（最大 |·| = {edge.Values.Max(v => Math.Abs(v)):0.000} mm）")}；"
          + $"均匀网格与实验 a {(uniMoved.Count == 0 ? "在打印精度上逐行一致" : "**对不上**：" + string.Join("；", uniMoved))}");
        Say($"   h=0.5 片2：d = {string.Join(" / ", d05.Select(v => v.ToString("+0.000;-0.000")))} W，三者极差 {spread05:0.000} W；"
          + $"窗口内 J²t 极差 {(winValid ? "都 < 0.5 %" : "**有 ≥ 0.5 % 的，直段列测量无效**")}");
        Say(!edgeZero
            ? "   ⇒ 锚点没接上，其余不看。"
            : uniMoved.Count > 0
                ? "   ⇒ 锚点动了均匀网格，d 与预测不可比，其余不看。"
                : d05.Any(v => v < -4.63) || spread05 > 6.55
                    ? "   ⇒ 锚点引入了新误差。"
                    : d05.All(v => v <= -1.0) && spread05 <= 0.45
                        ? "   ⇒ 对齐与粗格两个分量都确认；下一步查压接段那一片要多细（粗格分量只可能来自 |x| > 50 的粗格区）。"
                        : d05.All(v => Math.Abs(v) <= 0.45)
                            ? "   ⇒ 预测错了：只有对齐一个分量。"
                            : "   ⇒ 两种说法都不成立 —— 记下来，先不往下做。");
        Assert.True(q.Count == 48, "单片算例没有全部解出来，别拿它下结论");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
