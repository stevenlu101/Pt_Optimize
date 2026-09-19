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
/// ★★★★ R48 实验 c 续二（2026-09-14，Opus 5 写；规格与预测出自常驻数值把关人第七轮）：
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
/// **均匀网格抽热不收敛，是 h=2 还没进渐近区，还是另有非光滑来源？补 h = 0.25 一档。**
///
/// ══ 为什么要补（实验 c 的判读被推翻了一半）
/// 数值把关人指出：c1 在一个格宽内对称平移保温圆半径、取 9 点直线在 r=30 处的值，一阶上就等于「按面积份额混合散热」修完之后的数。
/// 这个值 片0 −13.648（h=1）→ −14.828（h=0.5），变化 −1.180 W；片2 −111.107 → −113.275，变化 −2.168 W —— 比不修（−1.036、−1.986）**还大**。
/// ⇒ 保温台阶**不是**均匀网格不收敛的主因；c3「去保温台阶后收敛」是换了热工况（抽热从 −13 翻到 +15），不是台阶没了。
/// 孔圆（c2）、舌盘厚度台阶（c4）也已排除。
///
/// ══ 跑前写死的预测（数值把关人：e(h) = a·h − b·h² 恰好拟合现有三档，两个参数三个点，没有自由度，不带误差带，只用来区分两种解释）
///   片0：0.5→0.25 的变化 ≈ −0.70 W，比 ≈ 0.68；片2：≈ −1.25 W，比 ≈ 0.63。
/// 判读：
///   · 实测 |Δ(0.5→0.25)| 明显小于 |Δ(1→0.5)|（取 ≤ 0.75 倍）⇒ 只是 h=2 还没进渐近区，本来就在收敛；
///   · 实测 |Δ(0.5→0.25)| 不小于 |Δ(1→0.5)|（≥ 1.0 倍）⇒ 还有别的非光滑来源，下一个嫌疑是 +x 半边的材料盘缘圆（覆盖不足 1/4 整格丢、栅格步随 h）；
///   · 介于 0.75～1.0 倍 ⇒ 分不开，记下来，不下结论。
///
/// 设置与实验 c 完全相同（管根 1141.8 / 1107.8 / 1057.8 / 1038.0 °C，电流 1214 / 2102 / 2102 / 1214 A，生产口径保温按半径、圆盘保温取算例实际值）。
/// h = 0.5 那一档重算一遍，必须与实验 c 文件在打印精度上一致（片0 −14.928、片2 −113.400）—— 对不上就说明设置没对齐，其余不看。
/// ⚠ 2026-09-14 14:25（Opus 5）：**本探针是记录，不是回归门**。之后生产代码加了保温分界格按有料面积份额混合散热（ShellThermal），
///   均匀网格与分界相关的值都会变（例：片0 h=0.5 −14.928 → −15.103 W），再跑会与本探针里写死的旧记录对不上、断言变红。
///   结论以当时的输出文件为准；提交前要把它改成跳过（写明原因）或删掉写死的旧值，不许改数让它变绿。
/// </summary>
[Trait("速度", "慢")]
public class R48UniformQuarterTests
{
    private readonly ITestOutputHelper _out;
    public R48UniformQuarterTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 均匀网格补四分之一毫米_区分未进渐近区与另有非光滑来源()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_均匀网格补0.25_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 均匀网格补 h = 0.25（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同实验 c");

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
        double clampLen = lc.Base.BusbarClampLengthMm;
        // 实验 c 文件（R48_实验c_温度场台阶抖动_2026-09-14.txt，13:26）生产口径的打印值
        var expC = new Dictionary<(int j, double h), double>
        {
            [(0, 2.0)] = -13.287, [(0, 1.0)] = -13.892, [(0, 0.5)] = -14.928,
            [(2, 2.0)] = -109.503, [(2, 1.0)] = -111.414, [(2, 0.5)] = -113.400,
        };
        var pred = new Dictionary<int, (double dq, double ratio)> { [0] = (-0.70, 0.68), [2] = (-1.25, 0.63) };

        bool aligned = true;
        foreach (int j in new[] { 0, 2 })
        {
            var q = new Dictionary<double, double>();
            foreach (double h in new[] { 0.5, 0.25 })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var g = d.Plate(j, d.DiscFloorMm(p));
                g.HoleRadiusMm = holeR;
                var (xa, za) = FlangeMesher.AnchorsOf(g);
                var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
                var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, h, 1e6, clampLen, h, 0, g.TwoTabs, xa, za);
                double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
                var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);
                var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
                if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                            tabBoundaryX: g.Tangent().X, tabInsulThickMm: d.TabInsulMm[j],
                                            discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
                q[h] = th.QFromTubeW;
                Say($"   片{j}　h={h:0.00}　{m.CellCount,7} 格　抽热 {th.QFromTubeW:+0.000;-0.000} W　发热 {th.QGenW:0.00}　散热 {th.QLossW:0.00}　夹持 {th.QToClampW:0.00}"
                  + $"　热场收敛 {th.Converged}　用时 {sw.Elapsed.TotalSeconds:0} s");
            }
            if (Math.Abs(q[0.5] - expC[(j, 0.5)]) > 0.0005 + 1e-9)
            {
                aligned = false;
                Say($"   ⚠ 片{j} h=0.5 重算 {q[0.5]:+0.000;-0.000} 与实验 c {expC[(j, 0.5)]:+0.000;-0.000} 对不上 —— 设置没对齐，本片其余不看");
                continue;
            }
            double d2 = expC[(j, 1.0)] - expC[(j, 2.0)], d3 = q[0.5] - expC[(j, 1.0)], d4 = q[0.25] - q[0.5];
            double k = Math.Abs(d3) > 1e-12 ? Math.Abs(d4) / Math.Abs(d3) : double.NaN;
            Say($"   片{j}：h 2→1 {d2:+0.000;-0.000}　1→0.5 {d3:+0.000;-0.000}　0.5→0.25 {d4:+0.000;-0.000} W　比 {d4 / d3:0.00}"
              + $"（预测 {pred[j].dq:+0.00;-0.00} W、比 {pred[j].ratio:0.00}）　|Δ(0.5→0.25)| ÷ |Δ(1→0.5)| = {k:0.00}");
            Say(k <= 0.75 ? "      ⇒ 只是 h=2 还没进渐近区，本来就在收敛。"
              : k >= 1.0 ? "      ⇒ 还有别的非光滑来源；下一个嫌疑：+x 半边的材料盘缘圆。"
              : "      ⇒ 介于 0.75～1.0 倍，分不开，不下结论。");
        }
        Assert.True(aligned, "h=0.5 重算与实验 c 对不上，设置没对齐");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
