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
/// ★★★★ R48 实验 c 续（2026-09-14，Opus 5 写；物理把关人第四轮建议的前两步）：
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
/// **抽热对「保温在哪里由厚变薄」有多敏感？敏感度沿舌片怎么分布？**
///
/// ══ 起因（出处：deliverable/R48_实验c_温度场台阶抖动_2026-09-14.txt，c1）
/// 网格不动、只挪保温分界半径：抽热斜率片0 −5.47／−5.43 W/mm，片2 −7.08／−7.03 W/mm（h=1／0.5）。
/// 物理把关人按一维面热流独立估算：舌根 1180 °C 下纤维 4.6 mm 约 31 kW/m²、20 mm 约 10.5 kW/m²，
/// 分界外挪 1 mm = 舌根半圈两面各多包 1 mm 宽厚保温 ⇒ 片0 约 3.6、片2 约 5.2 W/mm，与实测同量级（实测高约 1.4 倍）。
/// 他的推论：实际包层不会在 r = 30 断崖式变薄，过渡带是保温厚度量级 ⇒ 过渡怎么假设就差几十瓦，远大于逐片窗口的约 4 W。
///
/// ══ A 段：斜率是不是「两种保温热流差 × 弧长 × 两面」
/// 物理把关人提的对照是「盘保温 = 舌保温时斜率应接近 0」。⚠ 我没照这条做成判据：两种保温一样厚时两张损失表是**同一个函数**，
///   分界挪到哪都不改任何一格的散热，斜率按构造就是 0 —— 它只能查接线，查不了解释。
/// 改成：圆盘保温取 20 / 10 / 7.5 mm 与「= 舌保温」四档，量斜率，与一维热流差之比对照。
///   解释成立 ⇒ 斜率 ∝ [q舌(T_b) − q盘(T_b)]，四档之比与热流差之比一致（容 ±25 %：温度场会跟着变，1.4 倍的放大不必每档相同，但比例应稳定）；
///   「= 舌保温」那档斜率必须**恰为 0**（查接线；不为 0 ⇒ 探针接错，其余不看）。
///   T_b = 分界圆两侧各一格（|r − 30| ≤ h、x &lt; 0、有料）按面积平均的温度；热流差用 Insulation.PlateFlux 按 ShellThermal 同样的层参数现算
///   （⚠ 这是探针里的估算，不是门，所以没有提成公共函数）。
///
/// ══ B 段：敏感度沿舌片的分布
/// 生产口径（圆盘 20 mm）下，把分界半径放在 30 / 35 / 40 / 50 / 60 / 75 / 90 / 120 mm，各点在 ±2 mm 内取 9 点做直线（压掉约 0.4 W 的台阶抖动），报斜率。
///   ⚠ 分界半径同时决定圆盘区判据的分区（ShellThermal 里 insulOnTab 取 !insulated），所以这里只报抽热，不报圆盘区温度。
///   只记录分布，不下「台阶该放哪」的结论 —— 那是设计输入，要物理把关人与数值把关人看过、再问是否写进输入。
///
/// 设置：单片、管侧固定、均匀 h = 0.5 mm。管根温度取圆盘保温 20 mm 整线耦合那一趟的值（R48_圆盘保温杠杆_20.0mm_2026-09-14.txt：
/// 1173.49 / 1152.90 / 1093.63 / 1057.10 °C）—— 物理把关人指出实验 c 用的是旧管根，工况点离耦合状态远；电流沿用实验 a 的值（未随管根重算）。
/// ⚠ 2026-09-14 14:25（Opus 5）：**本探针是记录，不是回归门**。之后生产代码加了保温分界格按有料面积份额混合散热（ShellThermal），
///   均匀网格与分界相关的值都会变（例：片0 h=0.5 −14.928 → −15.103 W），再跑会与本探针里写死的旧记录对不上、断言变红。
///   结论以当时的输出文件为准；提交前要把它改成跳过（写明原因）或删掉写死的旧值，不许改数让它变绿。
/// </summary>
[Trait("速度", "慢")]
public class R48InsulBoundaryScanTests
{
    private readonly ITestOutputHelper _out;
    public R48InsulBoundaryScanTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 保温分界位置敏感度_斜率解释与沿舌分布()
    {
        var sb = new StringBuilder();
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        string file = DeliverableOut.Stamped("R48_保温分界敏感度_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 保温分界位置敏感度（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　单片、管侧固定、均匀 h = 0.5");

        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        var lc = d.BuildCase(p, checkRamp: false);
        double[] iA = { 1214, 2102, 2102, 1214 };
        double[] tRoot = { 1173.49, 1152.90, 1093.63, 1057.10 };
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        double clampLen = lc.Base.BusbarClampLengthMm;
        const double h = 0.5;
        Say($"管根 {string.Join("/", tRoot.Select(v => v.ToString("0.00")))} °C（圆盘保温 20 mm 整线耦合那一趟）；算例圆盘保温 {lc.Base.FlangeInsulThickMm:0.0} mm");

        int[] plates = { 0, 2 };
        var built = new Dictionary<int, (ShellMesh m, double[] jm, FlangePlate g)>();
        foreach (int j in plates)
        {
            var g = d.Plate(j, d.DiscFloorMm(p));
            g.HoleRadiusMm = holeR;
            var (xa, za) = FlangeMesher.AnchorsOf(g);
            var tf = FlangeMesher.Rasterize(g, h / 4);
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
            var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, h, 1e6, clampLen, h, 0, g.TwoTabs, xa, za, clampBandMm: 0, clampFullFace: false);
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);
            built[j] = (m, sc.JMagAPerMm2, g);
        }
        DesignInputs P2(int j, double discInsul)
        {
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
            if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
            p2.FlangeInsulThickMm = discInsul;
            return p2;
        }
        ShellThermalResult Th(int j, double insulR, double discInsul)
        {
            var (m, jm, g) = built[j];
            return ShellThermal.Solve(m, jm, P2(j, discInsul), tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                      tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j],
                                      discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: insulR);
        }
        static (double slope, double maxRes) Fit(double[] x, double[] y)
        {
            double mx = x.Average(), my = y.Average();
            double sxy = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum(), sxx = x.Sum(a => (a - mx) * (a - mx));
            double k = sxx > 0 ? sxy / sxx : 0;
            return (k, x.Zip(y, (a, b) => Math.Abs(b - (my + k * (a - mx)))).Max());
        }
        // 一维面热流（W/m²），层参数与 ShellThermal 里「法兰保温／舌片保温」两张表同样构造（探针估算，不是门）
        double Flux(DesignInputs p2, double thickMm, double tC)
        {
            var layers = new List<InsulationLayer> { new() { Name = "估算", ThicknessMm = thickMm, K0 = p2.Layer1.K0, K1 = p2.Layer1.K1, Enabled = thickMm > 1e-6 } };
            return Insulation.PlateFlux(tC, p2.TAmbC, layers, p2.OuterEmissivity, p2.ConvCharLenM, p2.LossScale, p2.FlangeAirVelocityMPerS);
        }

        // ── A 段
        Say("");
        Say("── A 段：斜率 vs 两种保温的一维热流差（分界 30 + k·h/8，k = −4…4）");
        bool wiringOk = true, ratioOk = true;
        foreach (int j in plates)
        {
            var (m, _, g) = built[j];
            double tabIns = d.TabInsulMm[j];
            var discVals = new[] { 20.0, 10.0, 7.5, tabIns };
            var ks = Enumerable.Range(-4, 9).Select(k => k * h / 8).ToArray();
            var rows = new List<(double disc, double slope, double res, double tb, double dq)>();
            foreach (double di in discVals)
            {
                var q = ks.Select(x => Th(j, g.DiscRadiusMm + x, di)).ToArray();
                var f = Fit(ks, q.Select(t => t.QFromTubeW).ToArray());
                var t0 = q[4];
                double sa = 0, st = 0;
                for (int i = 0; i < m.CellCount; i++)
                {
                    double cx = m.Centroid[i].X, cz = m.Centroid[i].Z, rr = Math.Sqrt(cx * cx + cz * cz);
                    if (cx < 0 && Math.Abs(rr - g.DiscRadiusMm) <= h) { sa += m.Area[i]; st += m.Area[i] * t0.T[i]; }
                }
                double tb = sa > 0 ? st / sa : double.NaN;
                var p2 = P2(j, di);
                double dq = Flux(p2, tabIns, tb) - Flux(p2, di, tb);   // W/m²，舌保温面热流 − 圆盘保温面热流
                rows.Add((di, f.slope, f.maxRes, tb, dq));
            }
            var r20 = rows[0];
            foreach (var r in rows)
            {
                bool isTab = Math.Abs(r.disc - tabIns) < 1e-9;
                double sRatio = Math.Abs(r20.slope) > 1e-12 ? r.slope / r20.slope : double.NaN;
                double qRatio = Math.Abs(r20.dq) > 1e-12 ? r.dq / r20.dq : double.NaN;
                // 两面 × 弧长 L 的「等效弧长」= 斜率 ÷ (−2·Δq)，单位 mm（Δq 化成 W/mm²）
                double lEff = Math.Abs(r.dq) > 1e-9 ? -r.slope / (2 * r.dq * 1e-6) : double.NaN;
                if (isTab && Math.Abs(r.slope) > 1e-9) wiringOk = false;
                if (!isTab && !(Math.Abs(sRatio - qRatio) <= 0.25 * Math.Abs(qRatio))) ratioOk = false;
                Say($"   片{j} 圆盘保温 {r.disc,4:0.0}{(isTab ? "（= 舌保温）" : "")}：斜率 {r.slope,7:+0.000;-0.000} W/mm　残差 {r.res:0.000} W　"
                  + $"分界处均温 {r.tb:0.0} °C　热流差 {r.dq / 1000:0.00} kW/m²　斜率比 {sRatio:0.000}　热流差比 {qRatio:0.000}　等效弧长 {lEff:0.0} mm");
            }
        }
        Say($"★ A 段：「= 舌保温」斜率{(wiringOk ? "恰为 0（接线对）" : "**不为 0 —— 探针接错，其余不看**")}；"
          + $"斜率比与热流差比{(ratioOk ? "都在 ±25 % 内 ⇒ 斜率 ∝ 热流差，解释成立" : "**有超出 ±25 % 的 ⇒ 斜率里还有热流差解释不了的东西**")}");

        // ── B 段
        Say("");
        Say("── B 段：生产口径（圆盘 20 mm）下，分界放在不同半径时抽热对它的斜率（各点 ±2 mm 内 9 点直线）");
        foreach (int j in plates)
        {
            var parts = new List<string>();
            foreach (double rb in new[] { 30.0, 35.0, 40.0, 50.0, 60.0, 75.0, 90.0, 120.0 })
            {
                var xs = Enumerable.Range(-4, 9).Select(k => rb + k * 0.5).ToArray();
                var q = xs.Select(x => Th(j, x, lc.Base.FlangeInsulThickMm).QFromTubeW).ToArray();
                var f = Fit(xs, q);
                parts.Add($"r={rb:0}: {f.slope:+0.00;-0.00} W/mm（抽热 {q[4]:+0.0;-0.0}，残差 {f.maxRes:0.00}）");
            }
            Say($"   片{j}：" + string.Join("　", parts));
        }
        Assert.True(wiringOk, "盘保温 = 舌保温时分界位置不该影响任何格子的散热");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
