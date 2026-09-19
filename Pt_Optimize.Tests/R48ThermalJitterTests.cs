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
/// ★★★★★ R48 实验 c（2026-09-14，Opus 5 写）：**均匀网格自己的抽热为什么不随加密收敛（E_h）？**
/// ⛔ 2026-09-14 14:19 更正（Opus 5；物理把关人第五轮查出，deliverable/R48_接头电流核对_2026-09-14.txt 核实）：
///   本探针的片电流 1214 / 2102 / 2102 / 1214 A 是**升温设计电流**，不是整线稳态接头电流（1213 / 1984 / 1817 / 1022 A，导航网格整线）；
///   焦耳热比约 1.00 / 1.12 / 1.34 / 1.41。管根也不是整线值（整线 1172.63 / 1151.19 / 1093.09 / 1056.80 °C）。
///   ⇒ 同一工作点上的配对比较结论可用，片1～片3 的绝对值（抽热、敏感度、误差量级）**不代表设计工作点**，不许拿去做规划或判据。
///
/// ══ 起因（出处：deliverable/R48_实验a_压接段相位_2026-09-14.txt，R=∞ 行）
/// 均匀网格 h = 2 / 1 / 0.5：片0 抽热 −13.287 / −13.892 / −14.928 W（两次差 −0.605、−1.036，差在变大）；
/// 片2 −109.503 / −111.414 / −113.400 W（−1.911、−1.986，不缩）。同一批行里片0 舌区发热 423.48 / 428.83 / 431.45 W
/// 两次差 +5.35、+2.62（比 0.49，一阶收敛）⇒ 电流场在收敛，病在**温度场一侧**。
/// 均匀网格的压接边界本来就落在节点上（实验 a 相位 = h/2，边偏移 0），所以实验 b 的锚点**修不到它**。
///
/// ══ 两个嫌疑，都是「圆形边界用方格台阶近似」，而方格台阶随 h 的摆法是跳的
///   甲、保温分界圆 r = 盘半径：按形心判「包法兰保温／包舌保温」，两边散热差好几倍；圆盘区的峰恰好就压在这条分界上（r ≈ 29.8～30.0）。
///   乙、管孔圆 r = 孔半径：定温边界落在台阶上，台阶离真圆最多一格，孔边温度梯度最大。
///   ⚠ 跑前量级估计（未经把关，只作预期）：甲的台阶面积误差约几 mm²，乘以两种保温的散热差，约 0.1 W 量级，
///     **不够解释 1～2 W**；乙没有估出来。估计可能是错的 —— 所以实测，不拿估计下结论。
///
/// ══ 做法（单片、管侧固定、管侧条件同实验 a；每一项只动一样东西）
///   c1 抖动·保温圆：几何与网格不动，只把保温分界半径在 盘半径 + k·h/8（k = −4…4）上扫。物理响应光滑（近似线性），
///      台阶翻格是跳的 ⇒ 对 9 个点做最小二乘直线，**最大残差 = 保温圆台阶抖动的幅度**。
///   c2 抖动·孔圆：只把孔半径在 孔半径 + k·h/8 上扫（重新栅格化、重新建网格、重解电流与温度）。同样取直线最大残差。
///      ⚠ 栅格步 h/4 比扫描步 h/8 粗 ⇒ 这个残差是「栅格 + 网格台阶」合计的抖动，与生产链路同一种离散，正是要量的；
///      孔半径还牵动焊脚位置（ThicknessAt），那是光滑的物理响应，由直线吸收。
///   c3 对照·去掉保温台阶：圆盘保温取成与该片舌保温一样厚（两张损失表同一公式、同一层材料，厚度相同即完全相同）⇒ 分界上没有台阶；
///      均匀 h = 2 / 1 / 0.5 看它收不收敛。与生产口径（按半径、圆盘保温取算例实际值）同表并排。
///   c4 对照·去掉厚度台阶（数值把关人第六轮加）：舌片按 J=10 定成 2.03／3.51 mm，圆盘 0.73／1.26 mm，舌盘分界上有 ×2.8 的厚度台阶，
///      正落在电流从舌片流进圆盘的地方；09-13 那次收敛的记录里舌片厚 = 板厚，没有这个台阶。
///      做法：**圆盘取成与舌片一样厚**（基准厚与两级环厚都取舌片厚），舌片不动。
///      ⚠ 没照「舌片取成板厚」做：那样舌片焦耳热 ×2.8（片2 约 2000 W），温度冲出损失表的上限（设定温度 + 200 °C），
///        会把表外外推混进收敛性里。圆盘加厚会改变抽热的绝对值，这里只看它收不收敛，不看数值。
///
/// ══ 判读（跑之前写死）
///   对比量 = 该片均匀网格 h=1→0.5 的抽热差 |Δ|（本次重算；与实验 a 的 R=∞ 行应逐位相同 —— 不同就先查为什么）。
///   · c1 在 h=0.5 的最大残差 ≥ |Δ|/3 ⇒ 保温圆台阶是 E_h 的主要来源之一；
///   · c2 在 h=0.5 的最大残差 ≥ |Δ|/3 ⇒ 孔圆台阶是 E_h 的主要来源之一；
///   · c3 两次差的比落在 [0.25, 0.75]、而生产口径不在 ⇒ 保温台阶以系统性方式（不只是抖动）拖住收敛；
///   · c4 两次差的比落在 [0.25, 0.75]、而生产口径不在 ⇒ 舌盘分界的厚度台阶拖住收敛；
///   · c1、c2 都 &lt; |Δ|/3 且 c3、c4 也不收敛 ⇒ 这几个嫌疑都不是，记下来另找（舌片直边、夹持定温、对流边界…），不往下修。
/// 只记录与判读，不改生产代码。
/// ⚠ 2026-09-14 14:25（Opus 5）：**本探针是记录，不是回归门**。之后生产代码加了保温分界格按有料面积份额混合散热（ShellThermal），
///   均匀网格与分界相关的值都会变（例：片0 h=0.5 −14.928 → −15.103 W），再跑会与本探针里写死的旧记录对不上、断言变红。
///   结论以当时的输出文件为准；提交前要把它改成跳过（写明原因）或删掉写死的旧值，不许改数让它变绿。
/// </summary>
[Trait("速度", "慢")]
public class R48ThermalJitterTests
{
    private readonly ITestOutputHelper _out;
    public R48ThermalJitterTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 实验c_均匀网格温度场不收敛_保温圆与孔圆台阶抖动()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_实验c_温度场台阶抖动_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 实验 c：均匀网格温度场不收敛 —— 保温圆与孔圆台阶抖动（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}");

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
        double holeR0 = lc.TubeIdMm * 0.5 + lc.WallMm;
        double clampLen = lc.Base.BusbarClampLengthMm;
        Say($"单片、管侧固定、均匀网格、管侧条件同实验 a。孔半径 {holeR0:0.000} mm，压接长 {clampLen:0.0} mm，圆盘保温（算例实际值）{lc.Base.FlangeInsulThickMm:0.0} mm");

        int[] plates = { 0, 2 };
        double[] hs = { 2.0, 1.0, 0.5 };

        (ShellMesh m, double[] j2, FlangePlate g) Build(int j, double h, double holeR, bool flatThick = false)
        {
            var g = d.Plate(j, d.DiscFloorMm(p));
            g.HoleRadiusMm = holeR;
            if (flatThick)
            {
                Assert.False(double.IsNaN(g.TabThicknessMm), "舌片厚没定出来，去厚度台阶无从谈起");
                g.ThicknessMm = g.TabThicknessMm;
                g.DiscStepThicknessMm = g.DiscStepThicknessMm.Select(_ => g.TabThicknessMm).ToArray();
            }
            var (xa, za) = FlangeMesher.AnchorsOf(g);
            var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
            var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, h, 1e6, clampLen, h, 0, g.TwoTabs, xa, za, clampBandMm: 0, clampFullFace: false);
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var sc = ShellCurrent.SolveFor(lc, m, iA[j], Materials.PtResistivity(tSet) * 1e3, tSet);
            return (m, sc.JMagAPerMm2, g);
        }
        ShellThermalResult Th(int j, ShellMesh m, double[] jMag, FlangePlate g, double insulR, double discInsul = double.NaN)
        {
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
            if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
            if (!double.IsNaN(discInsul)) p2.FlangeInsulThickMm = discInsul;
            return ShellThermal.Solve(m, jMag, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
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

        // ── c3 与基线：均匀 h = 2/1/0.5，生产口径 vs 去掉保温台阶
        Say("");
        Say("── c3 基线与对照（均匀网格）");
        Say("   片    h     单元   生产口径抽热W   去保温台阶抽热W   去厚度台阶抽热W   生产·圆盘区−管温K（峰位r）   去保温台阶·圆盘区−管温K（峰位r）");
        var qProd = new Dictionary<(int, double), double>();
        var qFlat = new Dictionary<(int, double), double>();
        var qThk = new Dictionary<(int, double), double>();
        var built = new Dictionary<(int, double), (ShellMesh m, double[] j2, FlangePlate g)>();
        foreach (int j in plates)
            foreach (double h in hs)
            {
                var b = Build(j, h, holeR0);
                built[(j, h)] = b;
                var a = Th(j, b.m, b.j2, b.g, b.g.InsulDiscRadiusMm);
                var c = Th(j, b.m, b.j2, b.g, b.g.InsulDiscRadiusMm, d.TabInsulMm[j]);
                var bt = Build(j, h, holeR0, flatThick: true);
                var t4 = Th(j, bt.m, bt.j2, bt.g, bt.g.InsulDiscRadiusMm);
                qProd[(j, h)] = a.QFromTubeW; qFlat[(j, h)] = c.QFromTubeW; qThk[(j, h)] = t4.QFromTubeW;
                Say($"   {j}  {h,4:0.0}  {b.m.CellCount,7}  {a.QFromTubeW,13:+0.000;-0.000}  {c.QFromTubeW,15:+0.000;-0.000}  {t4.QFromTubeW,15:+0.000;-0.000}"
                  + $"   {a.TDiscMaxC - tRoot[j],10:+0.000;-0.000}（r={a.DiscMaxRMm:0.00}）   {c.TDiscMaxC - tRoot[j],10:+0.000;-0.000}（r={c.DiscMaxRMm:0.00}）");
            }
        foreach (int j in plates)
        {
            string Conv(Dictionary<(int, double), double> q)
            {
                double d1 = q[(j, 1.0)] - q[(j, 2.0)], d2 = q[(j, 0.5)] - q[(j, 1.0)];
                double r = Math.Abs(d1) > 1e-12 ? d2 / d1 : double.NaN;
                return $"差 {d1:+0.000;-0.000} → {d2:+0.000;-0.000} W，比 {r:0.00}（{(r >= 0.25 && r <= 0.75 ? "落在 [0.25, 0.75]" : "不在 [0.25, 0.75]")}）";
            }
            Say($"   片{j}：生产口径 {Conv(qProd)}；去保温台阶 {Conv(qFlat)}；去厚度台阶 {Conv(qThk)}");
        }

        // ── c1 / c2：h = 1.0、0.5 上的抖动
        foreach (double h in new[] { 1.0, 0.5 })
        {
            Say("");
            Say($"── c1 / c2 抖动（均匀 h = {h:0.0}，扫描步 h/8 = {h / 8:0.0000} mm，k = −4…4）");
            foreach (int j in plates)
            {
                var ks = Enumerable.Range(-4, 9).Select(k => (double)k).ToArray();
                var xk = ks.Select(k => k * h / 8).ToArray();
                var b = built[(j, h)];
                double rIns0 = b.g.InsulDiscRadiusMm;
                var q1 = ks.Select(k => Th(j, b.m, b.j2, b.g, rIns0 + k * h / 8).QFromTubeW).ToArray();
                var f1 = Fit(xk, q1);
                Say($"   片{j} c1 保温圆半径 {rIns0:0.000}+k·h/8：抽热 {string.Join(" ", q1.Select(v => v.ToString("+0.000;-0.000")))} W");
                Say($"        直线斜率 {f1.slope:+0.00;-0.00} W/mm，最大残差 {f1.maxRes:0.000} W");
                var q2 = new double[ks.Length];
                for (int t = 0; t < ks.Length; t++)
                {
                    var bb = Build(j, h, holeR0 + ks[t] * h / 8);
                    q2[t] = Th(j, bb.m, bb.j2, bb.g, bb.g.InsulDiscRadiusMm).QFromTubeW;
                }
                var f2 = Fit(xk, q2);
                Say($"   片{j} c2 孔半径 {holeR0:0.000}+k·h/8：抽热 {string.Join(" ", q2.Select(v => v.ToString("+0.000;-0.000")))} W");
                Say($"        直线斜率 {f2.slope:+0.00;-0.00} W/mm，最大残差 {f2.maxRes:0.000} W");
                if (h == 0.5)
                {
                    double dEh = Math.Abs(qProd[(j, 0.5)] - qProd[(j, 1.0)]);
                    Say($"   片{j} 判读（h=0.5）：对比量 |Δ(1→0.5)| = {dEh:0.000} W，三分之一 = {dEh / 3:0.000} W；"
                      + $"保温圆抖动 {f1.maxRes:0.000} W {(f1.maxRes >= dEh / 3 ? "≥ ⇒ 主要来源之一" : "< ⇒ 不是主要来源")}；"
                      + $"孔圆抖动 {f2.maxRes:0.000} W {(f2.maxRes >= dEh / 3 ? "≥ ⇒ 主要来源之一" : "< ⇒ 不是主要来源")}");
                }
            }
        }
        Assert.Equal(6, qProd.Count);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
