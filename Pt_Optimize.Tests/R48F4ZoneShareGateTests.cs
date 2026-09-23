using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  F4（2026-09-23）：法兰分区热账（圆盘区／舌片区）按**有料面积份额**拆盘缘上被 r = 盘半径 切开的格 —— 门槛跑前写死，跑完不挪。
//
//  改了什么：ShellThermal.Solve 的分区账原按格心整格归一边（格心半径 > 盘半径 ⇒ 舌片区），圆盘区体积随网格落点差 −2.0～+4.6 %
//    （HANDOVER §0.-15N 门 a「按格心」那一列）。现在被切开的格按 f = FlangeMesher.MaterialFractionInCircle（经 ShellThermal.DiscZoneShare）
//    把发热、散热、面积、体积拆到两区；业主决 28（2026-09-23）：这些格的温度**两区的峰都算**。
//    「改回」参数：ShellThermal.Solve(zoneByMaterialFraction: false)／PlateThermalSetup／LineCase.ZoneByMaterialFraction = false（只供门，生产不传）。
//
//  ══ 门
//   (a) 和账（快）：R48NMeshGateTests 带格与闭合门的 18 例（W08 片 0，等温 1214 A／1150 °C 电流场 + 管根 1140 °C 的片 0 壳热解，生产配方）：
//       · 逐格：Solve 记下的份额 = ShellThermal.DiscZoneShare（逐位）；
//         ★ 审查后（F4-M1）独立判料：每个 0 < 份额 < 1 的格，测试侧精确积分（R48NMeshGateTests.CellVolExact，板件 Simpson，与份额函数的高斯求积无共享代码）
//           量得圆内、圆外各有料 > ShellThermal.ZoneShareSnapTol（1e-9，出处见该常数）× 该格有料体积；「改回 ⇒ 红」：不收整的 DiscZoneShareRaw 在同一 18 例上
//           至少有一格被独立判料判成「一侧没料却 0 < 份额 < 1」（舍入产物），否则这条空守、判红。
//         逐格逐项（发热／散热／面积／体积）|x·f + x·(1−f) − x| ≤ 4u·|x| 只是**算术自检**：式子是测试自己的浮点运算、Solve 从不算它，0 ≤ f ≤ 1 时
//           误差界 3u + O(u²) 是定理（fl(1−f)、两次乘、一次加各 ≤ u），按构造不会红，**不算门的牙**（2026-09-23 审查 F2 后改标）；
//       · 两区合计：Solve 的八本账 = 逐格按同一顺序重加（逐位）——**这是牙**；两区之和对 Σ 自由格总账 ≤ (3n+4)u·Σ|x|（n = 自由格数；
//         两次 n 项求和各 ≤ (n−1)u、逐项 1u、参照和自身 (n−1)u，合计推得 3n − 1 ≈ 3n，「+4」是余量；一阶细算约 (2n+1)u，门取得偏松、方向保守）。
//         逐位重加那条过了之后这条就退化成测试自己两个和的比较，与逐位重加重复，只在逐位那条红时才有独立信息；
//       · 决 28：圆盘区峰 = 份额 > 0 的自由格里最高温，舌片区峰 = 份额 < 1 的自由格里最高温（逐位）；两区峰取大 = 全片自由格最高温；
//       · 改回（按格心）同一输入：温度场与总账逐位不变、两区峰取大逐位不变、份额只取 0／1、分区说明与改动前逐字相同；
//       · 圆盘区体积（Solve 的 VolDiscMm3，只含自由格）对精确参考 ≤ 0.1 %（与 (b) 同一门槛同一参考）。
//       牙：Solve 八本账与逐格同序重加逐位相等；Solve 记的份额 = DiscZoneShare（逐位）；份额独立判料（上）；圆盘区体积对独立参考 ≤ 0.1 %；决 28 两区峰逐位；改回逐位。
//       覆盖：W08 片 0 的 9 个形状（盘径 5 档 × 舌半宽 30，加盘径 30 × 舌半宽 4 档）× 导航／判决两张网格 = 18 例，每例开／关各解一次（共 36 次热解），
//         ShellThermal 分区账真实跑出来的数。不覆盖：耦合整线（见 c）、别的片、W06、图纸路径。
//   (b) 体积（快）：W08／W06 × R48NMeshGateTests 门 a 快门的几何（盘径 9 档 × 舌半宽 30／28 × 片 0／1 × 导航／判决，舌半宽 > 盘半径的不成立几何跳过）：
//       圆盘区体积 Σ 份额·面积·厚度 对 R48NMeshGateTests.Reference 的 VolInDisc（板件 Inside/HalfWidth/ThicknessAt 的 Simpson，与生成器无共享代码）≤ 0.1 %
//       （门槛 = §0.-15N 门 a 的 VolTolPct，原样引用）；逐格面积／体积和账同 (a) 的 4u（算术自检，不算牙）。**改回 ⇒ 红**：同一扫描上按格心的圆盘区体积至少有一张网格超 0.1 %，否则本门空守、判红。
//       ★ 审查后（F4-M1）：同 (a) 的独立判料（每个 0 < 份额 < 1 的格圆内、圆外都要量得有料）与它的「改回 ⇒ 红」（不收整的 DiscZoneShareRaw 在这一扫描上至少一格是舍入产物）。
//       覆盖：份额函数（ShellThermal.DiscZoneShare）在全扫描上的几何；Solve 用的就是它由 (a) 在 18 例上逐格证。不覆盖：发热／散热（这一扫描不解场）、片 2／3、图纸路径（栅格份额按方格中心）。
//   (c) 硬判据逐位（慢）：W08 × 圆盘保温 10／20 mm × 判决网格（R48NMeshInjectTests.门f_对拍 同一搭法）整线解，开／关各一次：
//       最热铂高出热偶读数／管根低于热偶读数／管孔净流入三条的值逐位相同、判定位相同；逐片最热铂（ThermocoupleBasis.HottestPtC）逐位相同；
//       生产缺省确是份额账（LineCase 缺省 true，逐片分区说明带「按有料面积份额」）。同时印「开 − 关」归因档（②″、两区峰与峰位、两区发热／散热账、三条硬判据）。
//       覆盖：W08 两行（同树同平台 Linux）。不覆盖：W06、导航网格、C0 的其余 6 行、Windows。
//   (d) 热点盖没盖住（慢，只量）：W08／W06 现役设计（圆盘保温原样）判决网格整线解开／关各一次，按 MeshVerify 同一式子取最远热点半径与 MeshAdapt.PeakVerdict 那句，
//       印开／关两句与是否相同。只断言解得出来（判不了当红）；两句相同与否是实测，照报。覆盖：两份设计的判决网格一档；不覆盖 MeshVerify 的加密阶梯其余档。
// ════════════════════════════════════════════════════════════════════════════
public class R48F4ZoneShareGateTests
{
    private readonly ITestOutputHelper _o;
    public R48F4ZoneShareGateTests(ITestOutputHelper o) { _o = o; }

    internal const string Sign = "2026-09-23，F4（C3）";
    /// <summary>单位舍入 u = 2⁻⁵³（IEEE 754 双精度就近舍入）。</summary>
    internal const double U = 1.1102230246251565e-16;
    /// <summary>逐格和账容差（单位 u）：|x·f + x·(1−f) − x| 的误差界 3u + O(u²)，门取 4u。</summary>
    internal const double CellTolU = 4.0;
    /// <summary>圆盘区体积门槛（%）：原样取 §0.-15N 门 a 的 VolTolPct（R48NMeshGateTests.VolTolPct = 0.1，跑前写死的那一个）。</summary>
    internal const double VolTolPct = R48NMeshGateTests.VolTolPct;

    static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    static string P(double a, double b) => R48NMeshGateTests.Pct(a, b);

    // ═══════════════════════════════════════════════ (a) 和账：18 例热解，开／关
    [Fact]
    public void 门a_和账_逐格逐项_18例热解_决28两区峰_改回逐位()
    {
        string file = DeliverableOut.Stamped("R48_F4_门a_和账_18例热解_开关.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("F4 门(a) 和账　W08 片0　R48NMeshGateTests 带格与闭合门的 18 例　等温 1214 A／1150 °C 电流场 → 片 0 壳热解（LineRunner.PlateThermalInputs／SolvePlateThermal，管根 1140 °C）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：Solve 八本账 = 逐格同序重加（逐位）；份额 = DiscZoneShare（逐位）；切开格独立判料两侧各 > {ShellThermal.ZoneShareSnapTol:0e0} × 格有料体积"
          + $"（不收整的 DiscZoneShareRaw 至少一格是舍入产物，否则空守）；决 28 两区峰逐位；改回：温度场、总账、两区峰取大逐位不变；圆盘区体积对精确参考 ≤ {VolTolPct} %。");
        W($"算术自检（按构造不会红，不算牙）：逐格逐项 |x·f + x·(1−f) − x| ≤ {CellTolU}u·|x|（u = 2⁻⁵³，0 ≤ f ≤ 1 时界 3u + O(u²) 是定理）；两区合计对 Σ 自由格 ≤ (3n+4)u·Σ|x|（逐位重加过了之后与它重复）。");
        W("R\tw\t档\t单元\t自由格\t切开格\t盘发热 开\t盘发热 关\t舌发热 开\t舌发热 关\t盘散热 开\t盘散热 关\t舌散热 开\t舌散热 关\t盘面积 开\t盘面积 关\t盘体积 开\t盘体积 关\t盘体积参考\tΔ开%\tΔ关%"
          + "\t盘峰 开\t盘峰 关\t舌峰 开\t舌峰 关\t盘峰r 开\t盘峰r 关\t舌峰r 开\t舌峰r 关\t逐格最大误差(u，自检)\t合计最大误差(界的倍数，自检)\t不收整时的切开格\t其中舍入产物\t切开格独立判料最小一侧份额\t判读");
        int nBad = 0, splitTotal = 0, rawSplitTotal = 0, rawArtifactTotal = 0; var bad = new List<string>();
        double minSideAll = double.PositiveInfinity;
        double worstCellU = 0, worstSumFrac = 0;
        foreach (var (R, w, grade, fine) in R48F4Cases.Closure18())
        {
            var notes = new List<string>();
            var (lc, g, m) = R48F4Cases.ClosureMesh(R, w, fine);
            var rf = R48NMeshGateTests.Reference(g);
            double Rd = g.DiscRadiusMm;
            double rho = Materials.PtResistivity(R48NMeshGateTests.PlateTempC) * 1e3;
            var sc = ShellCurrent.SolveFor(lc, m, R48NMeshGateTests.PlateCurrentA, rho, R48NMeshGateTests.PlateTempC);
            var tsOn = LineRunner.PlateThermalInputs(lc, 0, R48NMeshGateTests.PlateCurrentA, null);
            var tsOff = LineRunner.PlateThermalInputs(lc, 0, R48NMeshGateTests.PlateCurrentA, null);
            Assert.True(tsOn.ZoneByMaterialFraction, "生产缺省必须是份额账");
            tsOff.ZoneByMaterialFraction = false;
            Assert.Equal(Rd, tsOn.DiscRadiusMm);
            var on = LineRunner.SolvePlateThermal(m, sc.JMagAPerMm2, R48F4Cases.TRootC, tsOn);
            var off = LineRunner.SolvePlateThermal(m, sc.JMagAPerMm2, R48F4Cases.TRootC, tsOff);
            int n = m.CellCount;
            if (!on.Converged || !off.Converged) notes.Add("热场没收敛 ⇒ 判不了");
            if (!on.ZoneByMaterialFraction || off.ZoneByMaterialFraction) notes.Add("结果记的分区方式与参数不符");
            if (on.ZoneDiscShare.Length != n || off.ZoneDiscShare.Length != n) notes.Add("份额数组长度不对");

            // ── 逐格：份额、和账
            double gD = 0, gT = 0, lD = 0, lT = 0, aD = 0, aT = 0, vD = 0, vT = 0, tD = 0, tT = 0;
            double sG = 0, sL = 0, sA = 0, sV = 0, absG = 0, absL = 0, absA = 0, absV = 0;
            double maxD = double.NegativeInfinity, maxT = double.NegativeInfinity, maxAll = double.NegativeInfinity;
            int nFree = 0, nSplit = 0, cellBad = 0; double cellWorstU = 0;
            for (int i = 0; i < n; i++)
            {
                double f = on.ZoneDiscShare[i], fo = off.ZoneDiscShare[i];
                if (on.BoundaryCell[i])
                {
                    if (!double.IsNaN(f) || !double.IsNaN(fo)) { cellBad++; if (cellBad <= 3) notes.Add($"格 {i} 不入账却记了份额"); }
                    if (ShellThermal.DiscZoneShare(m, i, Rd) != 0.0) notes.Add($"不入账的格 {i} 在圆盘区里（体积门只算自由格的前提不成立）");
                    continue;
                }
                nFree++;
                double fExp = ShellThermal.DiscZoneShare(m, i, Rd);
                if (!Same(f, fExp)) { cellBad++; if (cellBad <= 3) notes.Add($"格 {i} 份额 {f:R} ≠ DiscZoneShare {fExp:R}"); }
                if (!(f >= 0 && f <= 1)) { cellBad++; notes.Add($"格 {i} 份额越界 {f:R}"); }
                if (!(fo == 0.0 || fo == 1.0) || !Same(fo, ShellThermal.CentroidDiscShare(m, i, Rd))) { cellBad++; if (cellBad <= 3) notes.Add($"改回的格 {i} 份额 {fo:R} 不是格心 0／1"); }
                if (f > 0 && f < 1) nSplit++;
                double A = m.Area[i], t = m.Thickness[i], ti = on.T[i];
                foreach (double x in new[] { on.CellGenW[i], on.CellLossW[i], A, t * A })
                {
                    double err = Math.Abs(x * f + x * (1.0 - f) - x);
                    double inU = x != 0 ? err / (U * Math.Abs(x)) : 0;
                    cellWorstU = Math.Max(cellWorstU, inU);
                    if (err > CellTolU * U * Math.Abs(x)) { cellBad++; if (cellBad <= 3) notes.Add($"格 {i} 和账差 {inU:0.00}u"); }
                }
                // 与 Solve 同序同式重加（Solve：w = 1 加 x、w = 0 不加、切开格加 x·w；x·1 = x、x·0 = 0 在浮点下精确 ⇒ 逐位可比）
                double gc = on.CellGenW[i], lc2 = on.CellLossW[i], wt = 1.0 - f;
                gD += gc * f; lD += lc2 * f; aD += A * f; tD += ti * A * f; vD += t * A * f;
                gT += gc * wt; lT += lc2 * wt; aT += A * wt; tT += ti * A * wt; vT += t * A * wt;
                sG += gc; sL += lc2; sA += A; sV += t * A; absG += Math.Abs(gc); absL += Math.Abs(lc2); absA += A; absV += t * A;
                if (f > 0 && ti > maxD) maxD = ti;
                if (f < 1 && ti > maxT) maxT = ti;
                if (ti > maxAll) maxAll = ti;
            }
            if (cellBad > 0) notes.Add($"逐格不过 {cellBad} 处");
            if (on.ZoneSplitCells != nSplit) notes.Add($"ZoneSplitCells {on.ZoneSplitCells} ≠ 数出来的 {nSplit}");
            // ★ 审查后（F4-M1）：独立判料 + 不收整对照
            var (sideBad, minSide, rawSplit, rawArtifact) = IndependentSides(g, m, Rd, i => on.ZoneDiscShare[i], i => on.BoundaryCell[i]);
            if (sideBad.Count > 0) notes.Add($"{sideBad.Count} 个切开格独立判料只有一侧有料（如 {string.Join("、", sideBad.Take(3))}）");
            rawSplitTotal += rawSplit; rawArtifactTotal += rawArtifact; minSideAll = Math.Min(minSideAll, minSide);
            if (off.ZoneSplitCells != 0) notes.Add("改回仍记了切开格");
            splitTotal += nSplit;
            foreach (var (what, act, exp) in new[] { ("盘发热", on.QGenDiscW, gD), ("舌发热", on.QGenTabW, gT), ("盘散热", on.QLossDiscW, lD), ("舌散热", on.QLossTabW, lT),
                                                     ("盘面积", on.AreaDiscMm2, aD), ("舌面积", on.AreaTabMm2, aT), ("盘体积", on.VolDiscMm3, vD), ("舌体积", on.VolTabMm3, vT),
                                                     ("盘均温", on.TDiscMeanC, aD > 1e-9 ? tD / aD : double.NaN), ("舌均温", on.TTabMeanC, aT > 1e-9 ? tT / aT : double.NaN) })
                if (!Same(act, exp)) notes.Add($"{what} Solve {act:R} ≠ 逐格重加 {exp:R}");
            double sumWorst = 0;
            foreach (var (what, parts, total, abs) in new[] { ("发热", on.QGenDiscW + on.QGenTabW, sG, absG), ("散热", on.QLossDiscW + on.QLossTabW, sL, absL),
                                                              ("面积", on.AreaDiscMm2 + on.AreaTabMm2, sA, absA), ("体积", on.VolDiscMm3 + on.VolTabMm3, sV, absV) })
            {
                double bound = (3.0 * nFree + 4.0) * U * abs, err = Math.Abs(parts - total);
                sumWorst = Math.Max(sumWorst, bound > 0 ? err / bound : 0);
                if (err > bound) notes.Add($"两区{what}之和差 {err:E2} > 界 {bound:E2}");
            }
            if (nFree == n && !Same(on.QGenW, sG)) notes.Add($"没有不入账的格，整片发热 {on.QGenW:R} 却与逐格重加 {sG:R} 不逐位");
            // 决 28
            if (!Same(on.TDiscMaxC, maxD)) notes.Add($"圆盘区峰 {on.TDiscMaxC:R} ≠ 份额 > 0 的格里最高 {maxD:R}");
            if (!Same(on.TTabMaxC, maxT)) notes.Add($"舌片区峰 {on.TTabMaxC:R} ≠ 份额 < 1 的格里最高 {maxT:R}");
            if (!Same(Math.Max(on.TDiscMaxC, on.TTabMaxC), maxAll)) notes.Add("两区峰取大 ≠ 全片自由格最高");
            // 改回：场与总账逐位不变
            bool fieldSame = on.T.Length == off.T.Length && Enumerable.Range(0, n).All(i => Same(on.T[i], off.T[i]));
            if (!fieldSame) notes.Add("开／关温度场不逐位相同");
            foreach (var (what, a, b) in new[] { ("整片发热", on.QGenW, off.QGenW), ("整片散热", on.QLossW, off.QLossW), ("管孔净流入", on.QFromTubeW, off.QFromTubeW),
                                                 ("铜排带走", on.QToClampW, off.QToClampW), ("能量残差", on.EnergyResidualW, off.EnergyResidualW), ("整片最高温", on.TMaxC, off.TMaxC),
                                                 ("局部热稳定裕度", on.LocalStabMargin, off.LocalStabMargin),
                                                 ("两区峰取大", Math.Max(on.TDiscMaxC, on.TTabMaxC), Math.Max(off.TDiscMaxC, off.TTabMaxC)) })
                if (!Same(a, b)) notes.Add($"改回 {what} 不逐位：开 {a:R} 关 {b:R}");
            string oldRule = $"圆盘区按 r ≤ {Rd:0.0} mm 圈";
            if (off.DiscZoneRule != oldRule) notes.Add($"改回的分区说明不是改动前那句：「{off.DiscZoneRule}」");
            if (!on.DiscZoneRule.StartsWith(oldRule, StringComparison.Ordinal) || !on.DiscZoneRule.Contains("按有料面积份额", StringComparison.Ordinal))
                notes.Add($"份额账的分区说明不对：「{on.DiscZoneRule}」");
            // 圆盘区体积
            double dOn = (on.VolDiscMm3 - rf.VolInDisc) / rf.VolInDisc * 100, dOff = (off.VolDiscMm3 - rf.VolInDisc) / rf.VolInDisc * 100;
            if (!(Math.Abs(dOn) <= VolTolPct)) notes.Add($"圆盘区体积 {dOn:+0.000;-0.000} % 超 {VolTolPct} %");
            worstCellU = Math.Max(worstCellU, cellWorstU); worstSumFrac = Math.Max(worstSumFrac, sumWorst);
            W($"{R:0.00}\t{w:0.00}\t{grade}\t{n}\t{nFree}\t{nSplit}\t{on.QGenDiscW:0.0000}\t{off.QGenDiscW:0.0000}\t{on.QGenTabW:0.0000}\t{off.QGenTabW:0.0000}\t{on.QLossDiscW:0.0000}\t{off.QLossDiscW:0.0000}"
              + $"\t{on.QLossTabW:0.0000}\t{off.QLossTabW:0.0000}\t{on.AreaDiscMm2:0.000}\t{off.AreaDiscMm2:0.000}\t{on.VolDiscMm3:0.000}\t{off.VolDiscMm3:0.000}\t{rf.VolInDisc:0.000}\t{dOn:+0.000;-0.000}\t{dOff:+0.000;-0.000}"
              + $"\t{on.TDiscMaxC:0.000}\t{off.TDiscMaxC:0.000}\t{on.TTabMaxC:0.000}\t{off.TTabMaxC:0.000}\t{on.DiscMaxRMm:0.00}\t{off.DiscMaxRMm:0.00}\t{on.TabMaxRMm:0.00}\t{off.TabMaxRMm:0.00}"
              + $"\t{cellWorstU:0.00}\t{sumWorst:0.000}\t{rawSplit}\t{rawArtifact}\t{minSide:0.000e0}\t{(notes.Count == 0 ? "过" : "**" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { nBad++; bad.Add($"R{R:0.00} w{w:0.00} {grade}：{string.Join("；", notes.Take(4))}"); }
        }
        W();
        W($"── 18 例，不过 {nBad}；切开格合计 {splitTotal}（不收整时 {rawSplitTotal}，其中舍入产物 {rawArtifactTotal}）；切开格独立判料最小一侧份额 {minSideAll:0.000e0}（门 > {ShellThermal.ZoneShareSnapTol:0e0}）　总耗时 {sw.Elapsed.TotalSeconds:0} s（与别的测试同机争用下量得）　{Sign}");
        W($"算术自检：逐格和账最大误差 {worstCellU:0.00}u（{CellTolU}u，按构造不会红）；两区合计最大误差 = 界的 {worstSumFrac:0.000} 倍（逐位重加已过，与它重复）。");
        W("读法：「开」= 份额账（生产）；「关」= 改回按格心（只供门）。两区峰、峰位、分区账的开／关差就是 F4 在这一例上的位移（参考量，照报不评好坏）。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(splitTotal > 0, "18 例里一格都没被盘缘切开 ⇒ 本门空守");
        Assert.True(rawArtifactTotal > 0, "不收整的份额在 18 例上一格舍入产物都没有 ⇒ 独立判料这条分不出收整与不收整，空守");
        Assert.True(nBad == 0, $"和账门不过 {nBad}/18：{string.Join("　", bad.Take(6))}（{file}）");
    }

    // ═══════════════════════════════════════════════ (b) 体积：W08／W06 门 a 快门的几何扫描；改回 ⇒ 红
    [Fact]
    public void 门b_圆盘区体积对精确参考_W08W06门a快门几何_改回当场红()
    {
        string file = DeliverableOut.Stamped("R48_F4_门b_圆盘区体积_W08W06几何扫描.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("F4 门(b) 圆盘区体积　W08／W06 × R48NMeshGateTests 门 a 快门的几何（盘径 28／29／30／30.25／30.75／31／31.01／32／34 × 舌半宽 30／28 × 片 0／1 × 导航／判决）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}");
        W($"门槛（跑前写死）：圆盘区体积 Σ 份额·面积·厚度（份额 = ShellThermal.DiscZoneShare，Solve 的分区账用的同一个函数）对参考积分 VolInDisc ≤ {VolTolPct} %"
          + $"（= §0.-15N 门 a 的 VolTolPct）；逐格面积／体积和账 ≤ {CellTolU}u；改回按格心的圆盘区体积至少一张网格超 {VolTolPct} %（否则本门空守，判红）。");
        W("舌半宽 > 盘半径的几何不进门（与门 a 同一规矩）。");
        W($"★ 审查后（F4-M1）：每个 0 < 份额 < 1 的格独立判料（R48NMeshGateTests.CellVolExact）圆内、圆外各 > {ShellThermal.ZoneShareSnapTol:0e0} × 格有料体积；不收整的 DiscZoneShareRaw 在这一扫描上至少一格是舍入产物（否则空守，判红）。逐格和账 4u 是算术自检，按构造不会红，不算牙。");
        W("设计\tR\tw\t片\t档\t单元\t切开格\t参考 VolInDisc\t份额账\tΔ%\t格心账\tΔ格心%\t逐格最大误差(u，自检)\t不收整切开格\t其中舍入产物\t独立判料最小一侧份额\t判读");
        int nAll = 0, nBad = 0, nCentRed = 0, nSkip = 0, rawArtTotal = 0; var bad = new List<string>();
        double minSideAll = double.PositiveInfinity;
        double centMin = double.PositiveInfinity, centMax = double.NegativeInfinity, fracMaxAbs = 0;
        foreach (string which in new[] { "W08", "W06" })
        {
            var d0 = R48NMeshGateTests.Design(which);
            foreach (double R in new[] { 28.0, 29.0, 30.0, 30.25, 30.75, 31.0, 31.01, 32.0, 34.0 })
                foreach (double w in new[] { 30.0, 28.0 })
                    foreach (int plate in new[] { 0, 1 })
                        foreach (var (grade, fine) in new[] { ("导航", 0.0), ("判决", 1.0) })
                        {
                            string geo = $"{which}\t{R:0.00}\t{w:0.00}\t{plate}\t{grade}";
                            if (w > R + 1e-9) { nSkip++; continue; }
                            var d = d0.Clone(); R48NMeshGateTests.SetRW(d, R, w);
                            var notes = new List<string>();
                            try
                            {
                                var (lc, g, m, _) = R48NMeshGateTests.Build(d, new DesignInputs(), fine, plate);
                                var rf = R48NMeshGateTests.Reference(g);
                                double Rd = g.DiscRadiusMm, vf = 0, vc = 0, worstU = 0; int nSplit = 0;
                                var (sideBad, minSide, rawSplit, rawArtifact) = IndependentSides(g, m, Rd, i => ShellThermal.DiscZoneShare(m, i, Rd), _ => false);
                                if (sideBad.Count > 0) notes.Add($"{sideBad.Count} 个切开格独立判料只有一侧有料（如 {string.Join("、", sideBad.Take(3))}）");
                                rawArtTotal += rawArtifact; minSideAll = Math.Min(minSideAll, minSide);
                                for (int i = 0; i < m.CellCount; i++)
                                {
                                    double f = ShellThermal.DiscZoneShare(m, i, Rd), fc = ShellThermal.CentroidDiscShare(m, i, Rd);
                                    if (!(f >= 0 && f <= 1)) notes.Add($"格 {i} 份额越界 {f:R}");
                                    if (f > 0 && f < 1) nSplit++;
                                    double A = m.Area[i], v = m.Thickness[i] * A;
                                    foreach (double x in new[] { A, v })
                                    {
                                        double err = Math.Abs(x * f + x * (1.0 - f) - x);
                                        double inU = x != 0 ? err / (U * Math.Abs(x)) : 0;
                                        worstU = Math.Max(worstU, inU);
                                        if (err > CellTolU * U * Math.Abs(x)) notes.Add($"格 {i} 和账差 {inU:0.00}u");
                                    }
                                    vf += m.Thickness[i] * A * f; vc += m.Thickness[i] * A * fc;
                                }
                                double df = (vf - rf.VolInDisc) / rf.VolInDisc * 100, dc = (vc - rf.VolInDisc) / rf.VolInDisc * 100;
                                if (!(Math.Abs(df) <= VolTolPct)) notes.Add($"份额账 {df:+0.000;-0.000} %");
                                if (Math.Abs(dc) > VolTolPct) nCentRed++;
                                centMin = Math.Min(centMin, dc); centMax = Math.Max(centMax, dc); fracMaxAbs = Math.Max(fracMaxAbs, Math.Abs(df));
                                nAll++;
                                W($"{geo}\t{m.CellCount}\t{nSplit}\t{rf.VolInDisc:0.000}\t{vf:0.000}\t{df:+0.0000;-0.0000}\t{vc:0.000}\t{dc:+0.000;-0.000}\t{worstU:0.00}\t{rawSplit}\t{rawArtifact}\t{minSide:0.000e0}\t{(notes.Count == 0 ? "过" : "**不过：" + string.Join("；", notes.Take(3)) + "**")}");
                            }
                            catch (Exception ex) { notes.Add($"判不了：{ex.GetType().Name} {ex.Message}"); nAll++; W($"{geo}\t**{notes[0]}**"); }
                            if (notes.Count > 0) { nBad++; bad.Add($"{which} R{R:0.00} w{w:0.00} 片{plate} {grade}"); }
                        }
        }
        W();
        W($"合计 {nAll} 张网格，份额账不过 {nBad} 张{(nBad > 0 ? "：" + string.Join("、", bad.Take(20)) : "")}；份额账最大 |Δ| {fracMaxAbs:0.0000} %；几何不成立跳过 {nSkip} 张");
        W($"改回（按格心）：超 {VolTolPct} % 的 {nCentRed} 张，Δ 范围 {centMin:+0.000;-0.000} ～ {centMax:+0.000;-0.000} %（§0.-15N 门 a 记的是 −2.0～+4.6 %，那是全量扫描）");
        W($"独立判料：切开格最小一侧份额 {minSideAll:0.000e0}（门 > {ShellThermal.ZoneShareSnapTol:0e0}）；不收整时舍入产物合计 {rawArtTotal} 格（改回 ⇒ 红的对照，须 > 0）");
        W($"── 总耗时 {sw.Elapsed.TotalSeconds:0} s（与别的测试同机争用下量得）　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(rawArtTotal > 0, $"不收整的份额在这一扫描上一格舍入产物都没有 ⇒ 独立判料这条空守（{file}）");
        Assert.True(nCentRed > 0, $"改回按格心的圆盘区体积在这一扫描上全都 ≤ {VolTolPct} % ⇒ 本门分不出份额账与格心账，空守（{file}）");
        Assert.True(nBad == 0, $"圆盘区体积门不过 {nBad}/{nAll}：{string.Join("、", bad.Take(20))}（{file}）");
    }

    /// <summary>
    /// ★ 2026-09-23（F4 审查后，F4-M1）：独立判料。对 <paramref name="share"/> 给出 0 &lt; f &lt; 1 的每个格，用测试侧精确积分（R48NMeshGateTests.CellVolExact：
    /// 板件 HalfWidth／ThicknessAt 的 Simpson，与份额函数的高斯求积无共享代码；并过格的单元按 ShellMesh.CellRects 全部矩形）量圆内、圆外各占该格有料体积的份额，
    /// 两侧都须 &gt; <see cref="ShellThermal.ZoneShareSnapTol"/>。同时拿不收整的 <see cref="ShellThermal.DiscZoneShareRaw"/> 数「0 &lt; f &lt; 1 但独立判料一侧没料」的舍入产物（改回 ⇒ 红的对照）。
    /// </summary>
    static (List<string> bad, double minSide, int rawSplit, int rawArtifact) IndependentSides(FlangePlate g, ShellMesh m, double Rd, Func<int, double> share, Func<int, bool> skip)
    {
        var bad = new List<string>(); double minSide = double.PositiveInfinity; int rawSplit = 0, rawArtifact = 0;
        double tol = ShellThermal.ZoneShareSnapTol;
        for (int i = 0; i < m.CellCount; i++)
        {
            if (skip(i)) continue;
            double f = share(i), fr = ShellThermal.DiscZoneShareRaw(m, i, Rd);
            bool split = f > 0 && f < 1, rawIsSplit = fr > 0 && fr < 1;
            if (!split && !rawIsSplit) continue;
            var (inF, outF) = SidesExact(g, m, i);
            double side = Math.Min(inF, outF);
            if (rawIsSplit) { rawSplit++; if (!(side > tol)) rawArtifact++; }
            if (!split) continue;
            minSide = Math.Min(minSide, side);
            if (!(side > tol)) bad.Add($"格 {i} c=({m.Centroid[i].X:0.000},{m.Centroid[i].Z:0.000}) f={f:R} 圆内 {inF:0.0e0} 圆外 {outF:0.0e0}");
        }
        return (bad, minSide, rawSplit, rawArtifact);
    }

    static (double inF, double outF) SidesExact(FlangePlate g, ShellMesh m, int i)
    {
        var rects = new List<(double x0, double x1, double z0, double z1)>();
        if (m.CellRects != null && i < m.CellRects.Length && m.CellRects[i] is { Count: > 1 } rl) rects.AddRange(rl);
        else
        {
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
            foreach (int k in m.Cells[i]) { var v = m.Nodes[k]; x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z); }
            rects.Add((x0, x1, z0, z1));
        }
        double all = 0, ins = 0;
        foreach (var (x0, x1, z0, z1) in rects)
        {
            all += R48NMeshGateTests.CellVolExact(g, x0, x1, z0, z1, false);
            ins += R48NMeshGateTests.CellVolExact(g, x0, x1, z0, z1, true);
        }
        return all > 0 ? (ins / all, (all - ins) / all) : (double.NaN, double.NaN);
    }

    // ═══════════════════════════════════════════════ (c)(d) 整线：开／关
    sealed class LineRun
    {
        public string Tag = ""; public bool On; public LineCase Lc = null!; public DesignSpec D = null!; public LineResult R = null!; public double Sec;
    }

    static List<LineRun> RunLines(IEnumerable<(string tag, string which, double disc)> rows, int par)
    {
        var jobs = rows.SelectMany(r => new[] { (r, true), (r, false) }).ToList();
        var outp = new LineRun[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = par }, k =>
        {
            var ((tag, which, disc), on) = jobs[k];
            var sw = Stopwatch.StartNew();
            var (lc, d) = R48F4Cases.JudgeCase(which, disc, c => { if (!on) c.ZoneByMaterialFraction = false; });
            var r = LineRunner.Run(lc);
            outp[k] = new LineRun { Tag = tag, On = on, Lc = lc, D = d, R = r, Sec = sw.Elapsed.TotalSeconds };
            string? dumpDir = Environment.GetEnvironmentVariable("PT_F4_OUT");   // 只供改前对拍：设了就把全量转储写到那里（不进 deliverable）
            if (!string.IsNullOrEmpty(dumpDir))
            {
                Directory.CreateDirectory(dumpDir);
                File.WriteAllText(Path.Combine(dumpDir, $"line_{(on ? "on" : "off")}_{tag}.txt"), R48F4Cases.DumpLine(lc, r));
            }
        });
        return outp.ToList();
    }

    static readonly string[] HardKeys = { LineResult.Key.HotOverTc, LineResult.Key.ColdUnderTc, LineResult.Key.NetFlux };

    [Trait("速度", "慢")]
    [Fact]
    public void 门c_三条硬判据开关逐位_W08盘10盘20判决_并印开减关归因()
    {
        Assert.True(new LineCase().ZoneByMaterialFraction, "LineCase 缺省必须是份额账（生产）");
        string file = DeliverableOut.Stamped("R48_F4_开减关归因_W08盘10盘20判决.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("F4 门(c) 三条硬判据开／关逐位 + 「开 − 关」归因　W08 × 圆盘保温 10／20 mm × 判决网格（MeshVerify.RequiredMeshFor；R48NMeshInjectTests.门f_对拍 同一搭法）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　并发 2");
        W("开 = 份额账（生产缺省）；关 = LineCase.ZoneByMaterialFraction = false（改回按格心，只供门）。同树同平台同算例，只差这一位。");
        W("门槛（跑前写死）：三条硬判据（⑦ 最热铂高出热偶读数／⑧ 管根低于热偶读数／②′ 管孔净流入）的值逐位相同、判定位（过／判不了）相同；逐片最热铂逐位相同。");
        W("②″（旧判法参考量）、两区峰与峰位、两区发热／散热账是参考量：位移照报，不评好坏。");
        var runs = RunLines(new[] { ("W08_盘10", "W08", 10.0), ("W08_盘20", "W08", 20.0) }, par: 2);
        int nBad = 0; var bad = new List<string>();
        foreach (var tag in new[] { "W08_盘10", "W08_盘20" })
        {
            var on = runs.Single(x => x.Tag == tag && x.On); var off = runs.Single(x => x.Tag == tag && !x.On);
            W(); W($"═══ {tag}　开 {on.Sec:0} s／关 {off.Sec:0} s（争用下量得）　单元 {on.R.MeshCells}　耦合轮 开 {on.R.CoupleRounds}／关 {off.R.CoupleRounds}");
            var notes = new List<string>();
            if (!(on.R.Ok && on.R.Converged && off.R.Ok && off.R.Converged)) notes.Add($"判不了：开 Ok={on.R.Ok} 收敛={on.R.Converged}；关 Ok={off.R.Ok} 收敛={off.R.Converged}（{on.R.Message}｜{off.R.Message}）");
            W("判据\t开\t关\t开−关\t逐位相同\t开 判定\t关 判定\t说明文字相同");
            foreach (var key in HardKeys.Append(LineResult.Key.DiscTemp))
            {
                var a = on.R.Find(key); var b = off.R.Find(key);
                if (a is null || b is null) { notes.Add($"{key} 缺"); W($"{key}\t缺"); continue; }
                bool same = Same(a.Actual, b.Actual), verdictSame = a.Ok == b.Ok && a.Undetermined == b.Undetermined, textSame = a.Note == b.Note && a.Where == b.Where;
                W($"{key}\t{a.Actual:R}\t{b.Actual:R}\t{a.Actual - b.Actual:+0.000000;-0.000000;0}\t{(same ? "是" : "**否**")}\t{(a.Undetermined ? "判不了" : a.Ok ? "过" : "不过")}\t{(b.Undetermined ? "判不了" : b.Ok ? "过" : "不过")}\t{(textSame ? "是" : "否")}");
                if (HardKeys.Contains(key) && (!same || !verdictSame)) notes.Add($"{key} 开 {a.Actual:R} 关 {b.Actual:R}");
            }
            W("逐片\t最热铂 开\t最热铂 关\t盘峰 开\t盘峰 关\t舌峰 开\t舌峰 关\t盘峰r 开\t盘峰r 关\t舌峰r 开\t舌峰r 关\t盘发热 开\t盘发热 关\t舌发热 开\t舌发热 关\t盘散热 开\t盘散热 关\t舌散热 开\t舌散热 关\t盘均温 开\t盘均温 关\t管根");
            for (int j = 0; j < Math.Min(on.R.Flanges.Length, off.R.Flanges.Length); j++)
            {
                var a = on.R.Flanges[j]; var b = off.R.Flanges[j];
                double ha = ThermocoupleBasis.HottestPtC(a), hb = ThermocoupleBasis.HottestPtC(b);
                if (!Same(ha, hb)) notes.Add($"片{j} 最热铂 开 {ha:R} 关 {hb:R}");
                if (!a.DiscZoneRule.Contains("按有料面积份额", StringComparison.Ordinal)) notes.Add($"片{j} 生产的分区说明没写按份额：「{a.DiscZoneRule}」");
                if (b.DiscZoneRule.Contains("按有料面积份额", StringComparison.Ordinal)) notes.Add($"片{j} 改回的分区说明仍写按份额");
                W($"{a.Name}\t{ha:0.0000}\t{hb:0.0000}\t{a.TDiscMaxC:0.0000}\t{b.TDiscMaxC:0.0000}\t{a.TTabMaxC:0.0000}\t{b.TTabMaxC:0.0000}\t{a.DiscMaxRMm:0.00}\t{b.DiscMaxRMm:0.00}\t{a.TabMaxRMm:0.00}\t{b.TabMaxRMm:0.00}"
                  + $"\t{a.QGenDiscW:0.0000}\t{b.QGenDiscW:0.0000}\t{a.QGenTabW:0.0000}\t{b.QGenTabW:0.0000}\t{a.QLossDiscW:0.0000}\t{b.QLossDiscW:0.0000}\t{a.QLossTabW:0.0000}\t{b.QLossTabW:0.0000}"
                  + $"\t{a.TDiscMeanC:0.0000}\t{b.TDiscMeanC:0.0000}\t{a.TRootC:0.0000}");
            }
            W("开−关（逐片）\t盘峰\t舌峰\t盘峰r\t舌峰r\t盘发热\t舌发热\t盘散热\t舌散热");
            for (int j = 0; j < Math.Min(on.R.Flanges.Length, off.R.Flanges.Length); j++)
            {
                var a = on.R.Flanges[j]; var b = off.R.Flanges[j];
                W($"{a.Name}\t{a.TDiscMaxC - b.TDiscMaxC:+0.0000;-0.0000;0}\t{a.TTabMaxC - b.TTabMaxC:+0.0000;-0.0000;0}\t{a.DiscMaxRMm - b.DiscMaxRMm:+0.00;-0.00;0}\t{a.TabMaxRMm - b.TabMaxRMm:+0.00;-0.00;0}"
                  + $"\t{a.QGenDiscW - b.QGenDiscW:+0.0000;-0.0000;0}\t{a.QGenTabW - b.QGenTabW:+0.0000;-0.0000;0}\t{a.QLossDiscW - b.QLossDiscW:+0.0000;-0.0000;0}\t{a.QLossTabW - b.QLossTabW:+0.0000;-0.0000;0}");
            }
            W($"分区说明（片 0）开：「{on.R.Flanges.FirstOrDefault()?.DiscZoneRule}」　关：「{off.R.Flanges.FirstOrDefault()?.DiscZoneRule}」");
            W($"判读：{(notes.Count == 0 ? "过（三条硬判据与逐片最热铂开／关逐位相同）" : "**不过：" + string.Join("；", notes) + "**")}");
            if (notes.Count > 0) { nBad++; bad.Add($"{tag}：{string.Join("；", notes)}"); }
        }
        W(); W($"── 不过 {nBad}/2　总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（与别的测试同机争用下量得）　{Sign}");
        W("出处：整线解 = LineRunner.Run；判据 = LineResult.Find；逐片 = LineResult.Flanges（FlangeOut 取自 ShellThermalResult 的分区账）；最热铂 = ThermocoupleBasis.HottestPtC。");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(nBad == 0, $"硬判据开／关不逐位 {nBad}/2：{string.Join("　", bad)}（{file}）");
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 门d_热点盖没盖住那句_W08W06现役设计判决_开关实测()
    {
        string file = DeliverableOut.Stamped("R48_F4_门d_热点盖没盖住_W08W06判决_开关.txt");
        var sb = new StringBuilder(); void W(string t = "") => sb.AppendLine(t);
        var sw = Stopwatch.StartNew();
        W("F4 门(d) 「热点盖没盖住」那句（MeshAdapt.PeakVerdict）　W08／W06 现役设计（圆盘保温原样）× 判决网格　开／关各一次整线解");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 {Sign}　平台 {(OperatingSystem.IsWindows() ? "Windows" : "Linux")}　并发 2");
        W("最远热点半径 = 各片 {圆盘区峰位, 舌片区峰位, 局部热稳定最不稳格} 半径取大（与 MeshVerify 同一式子）；内带半径 = MeshAdapt.InnerRadiusFor(孔半径, max(舌厚, 管壁))；细区半径 = 本算例的 MeshFineRadiusMm。");
        W("只断言整线解得出来（判不了当红）；那句开／关相同与否是实测，照报。");
        var runs = RunLines(new[] { ("W08_现役", "W08", double.NaN), ("W06_现役", "W06", double.NaN) }, par: 2);
        int nBad = 0;
        W("设计\t开关\t最远热点 r\t细区半径\t内带半径\t那句\t耗时 s（争用下）");
        foreach (var tag in new[] { "W08_现役", "W06_现役" })
        {
            string?[] said = new string?[2];
            foreach (bool onFlag in new[] { true, false })
            {
                var x = runs.Single(q => q.Tag == tag && q.On == onFlag);
                if (!(x.R.Ok && x.R.Converged)) { nBad++; W($"{tag}\t{(onFlag ? "开" : "关")}\t**判不了：{x.R.Message}**"); continue; }
                var r = x.R;
                var blindPeak = r.Flanges.Where(f => double.IsNaN(f.DiscMaxRMm) && double.IsNaN(f.TabMaxRMm) && double.IsNaN(f.LocalStabRMm)).Select(f => f.Name).ToArray();
                double peakR = r.Flanges.Length == 0 || blindPeak.Length > 0 ? double.NaN
                             : r.Flanges.SelectMany(f => new[] { f.DiscMaxRMm, f.TabMaxRMm, f.LocalStabRMm }).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
                double innerR = MeshAdapt.InnerRadiusFor(x.D.HoleRadiusMm, Math.Max(x.D.TabThickMm.Max(), x.D.WallMm));
                double radius = x.Lc.MeshFineRadiusMm;
                string? s = MeshAdapt.PeakVerdict(peakR, innerR, radius);
                said[onFlag ? 0 : 1] = s;
                W($"{tag}\t{(onFlag ? "开" : "关")}\t{peakR:0.000}\t{radius:0.00}\t{innerR:0.00}\t{s ?? "（不说：盖住了且留足余量）"}\t{x.Sec:0}");
                // 审查后（M3）：逐片印三项半径与最远热点由哪一项决定 —— 与 F7 合并后细区半径变小时，舌峰半径（F4 会挪它）可能正好决定那句
                foreach (var f in r.Flanges)
                {
                    var items = new[] { ("圆盘区峰", f.DiscMaxRMm), ("舌片区峰", f.TabMaxRMm), ("局部热稳定", f.LocalStabRMm) };
                    var top = items.Where(t => !double.IsNaN(t.Item2)).OrderByDescending(t => t.Item2).FirstOrDefault();
                    W($"　{f.Name}\t{(onFlag ? "开" : "关")}\t圆盘区峰 r {f.DiscMaxRMm:0.000}\t舌片区峰 r {f.TabMaxRMm:0.000}\t局部热稳定 r {f.LocalStabRMm:0.000}\t本片最远：{top.Item1}{(Math.Abs(top.Item2 - peakR) < 1e-12 ? "（全线最远）" : "")}");
                }
            }
            W($"{tag}：那句开／关{(said[0] == said[1] ? "相同" : "**不同**")}");
        }
        W($"── 总耗时 {sw.Elapsed.TotalMinutes:0.0} 分钟（与别的测试同机争用下量得）　{Sign}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(file); _o.WriteLine(sb.ToString());
        Assert.True(nBad == 0, $"有 {nBad} 次整线解判不了（{file}）");
    }
}
