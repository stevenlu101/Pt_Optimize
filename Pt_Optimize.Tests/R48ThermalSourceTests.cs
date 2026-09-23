using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// 热导率与比热的**出处门**（2026-09-18，Opus 5）。
///
/// 背景：用户 2026-09-17/18 定可选牌号 = Pt、Pt-Rh/90-10、Pt-Rh/80-20、Tanaka-ZGS-Pt、Tanaka-ZGS-PtRh10，
/// 并要求「铂铑 10/20 的热导率和比热：上网找带出处的数，找不到就把两个铂铑牌号也灰显直到有数」。
/// 查证（旁证 B，D:\WinForm\r48_M\deliverable\旁证_2026-09-18\B_铂铑10_20_热导率与比热_本次开跑于2026-09-18_0042.md）：
///   · Pt-10%Rh：Heraeus 材料库 HT 图有 k（14 点）与 cp（16 点），厂方标「实测+内插」「实测+外推」；
///   · Pt-20%Rh：**一个实测点都没有**（Heraeus PtRh20 页 Thermal 栏只有熔化区间；JM Tech. Rev. 四篇、
///     Touloukian TPRC Vol.1（未 OCR）、各供应商页逐条查过都没有温度函数）。
///   · 纯铂：APP 原来那两条式子在仓库里**查不到出处**；本轮比热按 Kaye &amp; Laby 第 2.3.6 小节重定，
///     导热系数沿用原式（Touloukian/TPRC 推荐值那一支）并标 700 °C 以上外推、带宽 ±8 %。
///
/// 本档钉的是「每个数指回出处」：值、来源字符串、覆盖区间、借用点名、以及**推算值不许进判定**。
/// </summary>
public class R48ThermalSourceTests
{
    private readonly ITestOutputHelper _o;
    public R48ThermalSourceTests(ITestOutputHelper o) => _o = o;

    private static string Src(string rel) => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel));

    /// <summary>
    /// Pt-10%Rh 的 k(T) 与 cp(T) 逐点入库（决定记录 = 旁证 B 的 k 表与 cp 表（数字化）），
    /// 且来源字符串把来源、年份／读取日期、URL、图号都写出来。
    /// </summary>
    [Fact]
    public void 铂铑10的热导率与比热逐点入库_出处含来源与URL与图号()
    {
        // 决定记录：旁证 B 第 1 小节（Heraeus PtRh10 页 Thermal conductivity - HT 图数字化，14 点）
        var kT = new double[] { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400 };
        var kV = new double[] { 42.5, 45.3, 48.8, 51.0, 53.5, 56.0, 59.0, 61.8, 63.4, 66.0, 68.0, 70.6, 70.7, 70.8 };
        // 决定记录：旁证 B 第 2 小节（Specific Heat - HT 图数字化，16 点；分辨率只有 ±10 J/(kg·K)）
        var cT = new double[] { 25, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500 };
        var cV = new double[] { 140, 140, 140, 140, 140, 140, 140, 150, 150, 150, 150, 160, 160, 170, 170, 180 };

        var g = MaterialDb.Get("Pt-Rh/90-10");
        var k = g.ThermalKTable ?? throw new Xunit.Sdk.XunitException("Pt-Rh/90-10 没有热导率曲线");
        var c = g.CpTable ?? throw new Xunit.Sdk.XunitException("Pt-Rh/90-10 没有比热曲线");

        Assert.Equal("Pt-Rh/90-10", g.ThermalFrom);
        Assert.True(k.FromTable && c.FromTable, "铂铑 10 的两条都必须是逐点表（不是拟合式）");
        Assert.Equal(kT.Length, k.TC.Length);
        Assert.Equal(cT.Length, c.TC.Length);
        for (int i = 0; i < kT.Length; i++)
        {
            Assert.Equal(kT[i], k.TC[i], 9);
            Assert.Equal(kV[i], k.V[i], 9);
            Assert.Equal(kV[i], k.Value(kT[i]), 9);          // 表点上取值 = 表值
        }
        for (int i = 0; i < cT.Length; i++)
        {
            Assert.Equal(cT[i], c.TC[i], 9);
            Assert.Equal(cV[i], c.V[i], 9);
            Assert.Equal(cV[i], c.Value(cT[i]), 9);
        }

        // 内插：两点之间线性；区间外保端点值（1400 以上曲线已走平，外插没有依据）
        Assert.Equal(0.5 * (42.5 + 45.3), k.Value(150), 9);
        Assert.Equal(70.8, k.Value(1600), 9);
        Assert.Equal(42.5, k.Value(20), 9);
        Assert.False(k.Covers(1600), "1600 °C 在数据点之外 ⇒ Covers 必须报 false（调用方要把外推带进报告）");
        Assert.True(k.Covers(1400) && k.Covers(100));

        foreach (var (cur, what) in new[] { (k, "热导率"), (c, "比热") })
        {
            Assert.Contains("Heraeus", cur.Source);
            Assert.Contains("https://www.heraeus-precious-metals.com/", cur.Source);
            Assert.Contains("2026-09-18", cur.Source);
            Assert.Contains("HT", cur.Source);                      // 图号
            Assert.Contains("数字化", cur.Source);                  // 值是从图上读回来的，不是厂方数字表
            Assert.Equal(what, cur.Quantity);
        }
        Assert.Contains("measured and interpolated", k.Source);
        Assert.Contains("measured and extrapolated", c.Source);
        Assert.Contains("实测+内插", k.Note);
        Assert.Contains("实测+外推", c.Note);
        Assert.Contains("±10 J/(kg·K)", c.Note);                    // 分辨率必须随值带出
        Assert.Contains("常温段不可用", k.Note);
        _o.WriteLine($"Pt-10%Rh：k 14 点 {k.TMinC:0}–{k.TMaxC:0} °C、cp 16 点 {c.TMinC:0}–{c.TMaxC:0} °C；"
                   + $"k(1150) = {k.Value(1150):0.00} W/(m·K)、cp(1150) = {c.Value(1150):0.0} J/(kg·K)");
    }

    /// <summary>ZGS-PtRh10 与 Umicore-PtRh10 借 Pt-Rh/90-10、ZGS-Pt 借 Pt：值逐点相同，且借用必须被点名。</summary>
    [Fact]
    public void 同名义成分的牌号借用热导率与比热_值逐点相同且借用被点名()
    {
        foreach (var (grade, from) in new[]
                 { ("Tanaka-ZGS-Pt", "Pt"), ("Tanaka-ZGS-PtRh10", "Pt-Rh/90-10"), ("Umicore-PtRh10", "Pt-Rh/90-10") })
        {
            var g = MaterialDb.Get(grade);
            var s = MaterialDb.Get(from);
            Assert.Equal(from, g.ThermalFrom);
            Assert.Equal(g.Composition, s.Composition);
            for (double t = 0; t <= 1500; t += 50)
            {
                Assert.Equal(s.ThermalKWPerMK(t), g.ThermalKWPerMK(t), 12);
                Assert.Equal(s.CpJPerKgK(t), g.CpJPerKgK(t), 12);
            }
            var dc = MaterialDb.DataCompleteness(grade);
            Assert.True(dc.OwnThermal, $"{grade}：同名义成分借用 ⇒ 第四类算有");
            Assert.Contains(dc.Borrowed, x => x.Contains("热导率与比热：借用 " + from, StringComparison.Ordinal));
            _o.WriteLine($"{grade} ← {from}（{g.Composition}）：k(1150) = {g.ThermalKWPerMK(1150):0.00}、cp(1150) = {g.CpJPerKgK(1150):0.0}");
        }
        // FKS16 两个牌号**不借**（与热膨胀同一条保守口径）
        foreach (string n in new[] { "FKS16/Pt", "FKS16/PtRh-9010" })
        {
            Assert.False(MaterialDb.Get(n).HasThermal, $"{n}：FKS16 不借热导率与比热（借同基体还是借 ZGS 是工程判断）");
            Assert.False(MaterialDb.DataCompleteness(n).OwnThermal);
        }
    }

    /// <summary>
    /// ★ **「同名义成分才算自有」这条判断的两侧都要被走过**（2026-09-18，Opus 5）。
    ///
    /// 材料库现在 12 个牌号里的借用**全是同成分**（电阻率 4 处、膨胀 2 处、热导率与比热 3 处），
    /// 所以「跨成分借用不算自有」那一侧**一次都没被走过** —— 把判断写成「一律算自有」，
    /// 上面那些门照样全绿。这里用库里不存在的组合把假的那一侧钉死。
    /// </summary>
    [Fact]
    public void 跨名义成分的借用不算自有_库里现在没有这种组合所以门自己造()
    {
        // 真的那一侧（库里现有的）
        Assert.True(MaterialDb.BorrowedGradeCountsAsOwn("Pt", "Tanaka-ZGS-Pt"));
        Assert.True(MaterialDb.BorrowedGradeCountsAsOwn("Pt-Rh/90-10", "Umicore-PtRh10"));
        Assert.True(MaterialDb.BorrowedCurveCountsAsOwn("PtRh10", "Umicore-PtRh10"));
        Assert.True(MaterialDb.BorrowedCurveCountsAsOwn("ZGSPtRh10", "Tanaka-ZGS-PtRh10"));

        // ★ 假的那一侧：库里一个都没有，全靠门自己造
        Assert.False(MaterialDb.BorrowedGradeCountsAsOwn("Pt", "Pt-Rh/90-10"), "纯铂 → 铂铑 10 是跨成分");
        Assert.False(MaterialDb.BorrowedGradeCountsAsOwn("Pt-Rh/90-10", "Pt-Rh/80-20"), "铂铑 10 → 铂铑 20 是跨成分");
        Assert.False(MaterialDb.BorrowedGradeCountsAsOwn("Pd", "Pt"), "钯 → 铂是跨成分");
        Assert.False(MaterialDb.BorrowedCurveCountsAsOwn("PtRh20", "Tanaka-ZGS-PtRh10"), "铂铑 20 曲线 → 铂铑 10 牌号是跨成分");
        Assert.False(MaterialDb.BorrowedCurveCountsAsOwn("PtAu5", "Pt"), "铂金 5 曲线 → 纯铂是跨成分");
        // 名字打错／没登记 ⇒ 一律不算（不许因为查不到就放行）
        Assert.False(MaterialDb.BorrowedGradeCountsAsOwn("PtRh10", "Pt"), "「PtRh10」不是材料库牌号名");
        Assert.False(MaterialDb.BorrowedGradeCountsAsOwn("", "Pt"));
        Assert.False(MaterialDb.BorrowedCurveCountsAsOwn("没登记的曲线", "Pt"));

        // 每个牌号都登记了名义成分（新加牌号漏登记会在静态构造里抛，这里再钉一次「不许空」）
        foreach (var g in MaterialDb.All.Values)
            Assert.False(string.IsNullOrEmpty(g.Composition), $"{g.Name} 没有名义成分");
        _o.WriteLine("名义成分：" + string.Join("；", MaterialDb.All.Values.Select(x => $"{x.Name}={x.Composition}")));
    }

    /// <summary>
    /// ★ Pt-20%Rh：**没有实测 k、cp** ⇒ 两个牌号因缺第四类灰显；
    /// Wiedemann–Franz 推算只作参考值存着，**不入判定**（把它当数据用，齐全名单就会多两个牌号 ⇒ 本门红）。
    /// </summary>
    [Fact]
    public void 铂铑20没有实测热导率与比热_推算值只作参考不入判定()
    {
        foreach (string n in new[] { "Pt-Rh/80-20", "Umicore-PtRh20" })
        {
            var g = MaterialDb.Get(n);
            Assert.False(g.HasThermal, $"{n}：公开文献查不到实测热导率与比热 ⇒ 不许入库");
            Assert.True(double.IsNaN(g.ThermalKWPerMK(1150)) && double.IsNaN(g.CpJPerKgK(1150)),
                $"{n}：没有数据就返回 NaN，不许给个看起来正常的数");
            var dc = MaterialDb.DataCompleteness(n);
            Assert.False(dc.OwnThermal);
            Assert.False(dc.IsComplete, $"{n}：缺热导率与比热 ⇒ 不齐全（界面灰显）");
            Assert.Contains(dc.Missing, x => x.StartsWith("热导率与比热：无数据", StringComparison.Ordinal));

            var adv = g.ThermalKAdvisory ?? throw new Xunit.Sdk.XunitException($"{n}：推算参考值应存着（只是不入判定）");
            Assert.Contains("推算，非实测，不入数据齐全度判定", adv.Note);
            Assert.Contains("Wiedemann–Franz", adv.Source);
            Assert.Contains("×0.94", adv.Source);
            // 推算值按定义复算：k = 0.94·L·T/ρ(T)，ρ 取生产代码的电阻率函数（不手抄）
            for (int i = 0; i < adv.TC.Length; i++)
            {
                double t = adv.TC[i];
                double want = 0.94 * 2.44e-8 * (t + 273.15) / g.ResistivityOhmM(t);
                Assert.Equal(want, adv.V[i], 9);
            }
            _o.WriteLine($"{n}：无实测 k/cp；推算参考值 k(1150) ≈ {adv.Value(1150):0.0} W/(m·K)（不入判定）");
        }
        // 齐全名单里不许出现这两个
        var complete = MaterialDb.All.Keys.Where(x => MaterialDb.DataCompleteness(x).IsComplete).ToHashSet();
        Assert.DoesNotContain("Pt-Rh/80-20", complete);
        Assert.DoesNotContain("Umicore-PtRh20", complete);
    }

    /// <summary>
    /// 纯铂比热：由 Kaye &amp; Laby 第 2.3.6 小节 的四点**独立复算**最小二乘，与生产常数逐位对上；
    /// 并把「改前 → 改后」的幅度写进输出（改动依据）。
    /// </summary>
    [Fact]
    public void 纯铂比热_由四点最小二乘复算_与生产常数逐位对上()
    {
        // 决定记录：Kaye & Laby 第 2.3.6 小节纯铂行 273/373/573/773 K = 132/135/141/146 J/(kg·K)
        var t = new double[] { 0, 100, 300, 500 };
        var v = new double[] { 132, 135, 141, 146 };
        Assert.Equal(t, Materials.CpFitTempsC);      // 生产代码存的原始点必须就是这四点
        Assert.Equal(v, Materials.CpFitCpJKgK);

        // 门按定义自己算一遍最小二乘（不抄生产的系数）
        double n = t.Length, st = t.Sum(), sv = v.Sum();
        double stt = t.Sum(x => x * x), stv = t.Zip(v, (a, b) => a * b).Sum();
        double b = (n * stv - st * sv) / (n * stt - st * st);
        double a = (sv - b * st) / n;
        Assert.Equal(a, Materials.CpFitA, 10);
        Assert.Equal(b, Materials.CpFitB, 14);
        Assert.True(Math.Abs(a - Materials.CpFitA) <= 1e-9 * Math.Abs(a), "截距与门复算的最小二乘不符");
        Assert.True(Math.Abs(b - Materials.CpFitB) <= 1e-12 * Math.Abs(b), "斜率与门复算的最小二乘不符");

        double worst = 0;
        for (int i = 0; i < t.Length; i++) worst = Math.Max(worst, Math.Abs(Materials.PtCp(t[i]) - v[i]));
        Assert.True(worst <= 0.40, $"四点残差最大 {worst:0.000} J/(kg·K)，应 ≤ 0.40（= 该表 ±1 的分辨率之内）");

        // 改前那条式子（决定记录：2026-09-18 之前 Materials.PtCp 的式子）
        double Old(double tc) => 133.0 + 0.0135 * tc;
        double r1150 = Materials.PtCp(1150) / Old(1150);
        Assert.True(Math.Abs(Old(0) - 133.0) < 1e-12);
        Assert.InRange(r1150, 1.10, 1.12);
        Assert.True(Materials.PtCp(300) > Old(300), "新式在中高温必须高于旧式（旧式斜率只有文献的一半）");
        Assert.Equal(500.0, Materials.PtCpMeasuredMaxC);
        _o.WriteLine($"纯铂比热：新式 {Materials.CpFitA:0.0000} + {Materials.CpFitB:0.00000000}·T；"
                   + $"残差最大 {worst:0.000} J/(kg·K)；"
                   + $"0/300/500/1150/1300 °C 新 {Materials.PtCp(0):0.0}/{Materials.PtCp(300):0.0}/{Materials.PtCp(500):0.0}/{Materials.PtCp(1150):0.0}/{Materials.PtCp(1300):0.0}"
                   + $"　旧 {Old(0):0.0}/{Old(300):0.0}/{Old(500):0.0}/{Old(1150):0.0}/{Old(1300):0.0}"
                   + $"　1150 °C 处 +{(r1150 - 1) * 100:0.0} %");
    }

    /// <summary>
    /// 纯铂导热系数：0–700 °C 与 Kaye &amp; Laby 核对点一致到 ≤2.5 %；
    /// 700 °C 以上标外推；带宽 ±8 % 在 1150 °C 覆盖 Wiedemann–Franz 的 78 与另一支的 90。
    /// </summary>
    [Fact]
    public void 纯铂导热系数_与核对点一致到两点五个百分点_带宽覆盖两支()
    {
        // 决定记录：Kaye & Laby 第 2.3.7 小节纯铂行 273.2/373.2/573.2/973.2 K = 72/72/73/78 W/(m·K)
        Assert.Equal(new double[] { 0.05, 100.05, 300.05, 700.05 }, Materials.KRefTempsC);
        Assert.Equal(new double[] { 72, 72, 73, 78 }, Materials.KRefKWmK);

        double worst = 0; double worstAt = 0;
        for (int i = 0; i < Materials.KRefTempsC.Length; i++)
        {
            double rel = Math.Abs(Materials.PtThermalK(Materials.KRefTempsC[i]) - Materials.KRefKWmK[i]) / Materials.KRefKWmK[i];
            if (rel > worst) { worst = rel; worstAt = Materials.KRefTempsC[i]; }
        }
        Assert.True(worst <= 0.025, $"0–700 °C 内与核对点最大相对差 {worst:P2}（在 {worstAt:0} °C），应 ≤ 2.5 %");

        Assert.Equal(700.0, Materials.PtThermalKMeasuredMaxC);
        Assert.Equal(0.08, Materials.PtThermalKBandFrac, 12);
        double lo = Materials.PtThermalKLow(1150), hi = Materials.PtThermalKHigh(1150);
        Assert.True(lo <= 78.0, $"带宽下沿 {lo:0.0} 必须盖住 Wiedemann–Franz 的 78 W/(m·K)");
        Assert.True(hi >= 90.0, $"带宽上沿 {hi:0.0} 必须盖住另一支的 90 W/(m·K)");
        Assert.True(hi < 95.0, "上沿不采用 Terada 2005 的 95（它要求 L/L₀ ≈ 1.35，不合理）");

        // 纯铂那条曲线是**拟合式**形态：值就是求解链用的 Materials.PtThermalK，不另立一份数值来源
        var pt = MaterialDb.Get("Pt");
        Assert.False(pt.ThermalKTable!.FromTable);
        Assert.False(pt.CpTable!.FromTable);
        for (double x = 0; x <= 1500; x += 100)
        {
            Assert.Equal(Materials.PtThermalK(x), pt.ThermalKWPerMK(x), 12);
            Assert.Equal(Materials.PtCp(x), pt.CpJPerKgK(x), 12);
        }
        Assert.Contains("Kaye & Laby", pt.ThermalKTable!.Source);
        Assert.Contains("kayelaby.npl.co.uk", pt.ThermalKTable!.Source);
        Assert.Contains("推荐值外推，无本项目实测", pt.ThermalKTable!.Note);
        Assert.Contains("Kaye & Laby", pt.CpTable!.Source);
        _o.WriteLine($"纯铂导热系数：与核对点最大差 {worst:P2}（{worstAt:0} °C）；"
                   + $"1150 °C 中值 {Materials.PtThermalK(1150):0.0}，带宽 {lo:0.0}…{hi:0.0} W/(m·K)");
    }

    /// <summary>
    /// ★★ 比热改动的**影响量**：比热进设计链的唯一入口是准静态升温电流里的金属热容项。
    /// 门用生产代码自己的分项（<see cref="RampTwoNode.QuasiStaticBreakdown"/>）当场算出「改前 → 改后」
    /// 设计电流差多少 —— 不是把式子在门里抄一遍。
    /// </summary>
    [Fact]
    public void 比热改动对设计电流的影响_按生产分项当场算出量级()
    {
        var d = DesignSpec.Builtin[0];                       // 现役档 W08
        var lc = d.BuildCase(new DesignInputs(), checkRamp: false);
        const double tPeakC = 1150.0, rateKPerH = 20.0;      // 升温目标与现场速率（LineCase 的默认）
        var q = RampTwoNode.QuasiStaticBreakdown(lc.Base, lc.WallMm, tPeakC, rateKPerH);

        double cpNew = Materials.PtCp(tPeakC);
        double cpOld = 133.0 + 0.0135 * tPeakC;              // 决定记录：改前的式子
        double capMetalOld = q.CapMetalJPerK * cpOld / cpNew;
        double needOld = q.LossW + (capMetalOld + q.CapInsulJPerK) * (rateKPerH / 3600.0);
        double iOld = Math.Sqrt(needOld / q.RTubeOhm);
        double dI = q.CurrentA / iOld - 1.0;

        Assert.True(q.LossW > 0 && q.RTubeOhm > 0, "分项没算出来，断言就失去了对象");
        Assert.True(dI > 0, "比热调高 ⇒ 所需功率与电流只会**变大**，不该是零 —— 它确实进了设计链，只是量级小");
        Assert.True(dI < 1e-4, $"设计电流改变 {dI:P4}，本应在 1e-4 以下（热容项 {q.CapMetalJPerK * rateKPerH / 3600:0.00} W 对散热 {q.LossW:0} W）");
        _o.WriteLine($"现役档 W08、管壁 {lc.WallMm:0.00} mm、{tPeakC:0} °C、{rateKPerH:0} K/h：");
        _o.WriteLine($"  散热 {q.LossW:0.0} W；金属热容 {q.CapMetalJPerK:0.0} J/K（× 速率 = {q.CapMetalJPerK * rateKPerH / 3600:0.000} W）；"
                   + $"保温热容 {q.CapInsulJPerK:0.0} J/K；所需功率 {q.NeedW:0.00} W");
        _o.WriteLine($"  比热 {cpOld:0.0} → {cpNew:0.0} J/(kg·K)（+{(cpNew / cpOld - 1) * 100:0.0} %）"
                   + $"⇒ 所需功率 {needOld:0.000} → {q.NeedW:0.000} W（+{(q.NeedW / needOld - 1) * 100:0.00000} %）"
                   + $"⇒ 设计电流 {iOld:0.0000} → {q.CurrentA:0.0000} A（+{dI * 100:0.00000} %）");
        _o.WriteLine($"  ⇒ 按 J = 10 定截面，管截面与管重跟着动同一个量级：{dI:P5}");
    }

    /// <summary>
    /// ★ 比热只在这三处被读到 —— **稳态判据不读比热**（这条此前只在脑子里，没有门）。
    /// 谁把比热读进稳态链或判据，这里当场红。
    /// </summary>
    [Fact]
    public void 比热只在升温与时间常数处被读到_稳态判据不读比热()
    {
        // ★ 2026-09-23 Opus 5.5（R48 物性接线）改门，变因：比热改由 PtProps 按牌号取（纯铂一支调的仍是 Materials.PtCp），门槛（比热只进升温与时间常数）不变。
        //   原来钉「含 Materials.PtCp( 的档 = RampSolver／RampTwoNode／SegmentSolver」；现在 Materials.PtCp( 只在访问口 PtProps.cs 里，
        //   求解链读比热的地方改认任何接收者的 .Cp( 与 CpJPerKgK(（PtProps.cs、MaterialDb.cs 除外），档集合与各处字面照旧逐条钉。
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        var pure = Directory.GetFiles(core, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("Materials.PtCp(", StringComparison.Ordinal))
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "PtProps.cs" }, pure);   // 只剩访问口（Materials.cs 里的定义不带类名前缀）
        // 读比热的写法：任何接收者的 .Cp(（props.Cp／_props.Cp／PtProps.For(..).Cp／别名都算）与任何 CpJPerKgK(（绕过访问口直读按牌号曲线）。
        //   PtProps.cs（访问口本身）与 MaterialDb.cs（曲线定义处）不在扫描之列；Materials.PtCp( 由上一条单独钉（只在 PtProps.cs）。
        //   覆盖：Core/*.cs 源码字面。不覆盖：经委托或反射取比热。（2026-09-23 复审放宽匹配：原来只认 props.Cp(／_props.Cp(，别名会漏。）
        var cpRe = new System.Text.RegularExpressions.Regex(@"\.Cp\(|CpJPerKgK\(");
        var hits = Directory.GetFiles(core, "*.cs")
            .Where(f => Path.GetFileName(f) is not ("PtProps.cs" or "MaterialDb.cs"))
            .Where(f => cpRe.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "RampSolver.cs", "RampTwoNode.cs", "SegmentSolver.cs" }, hits);

        // SegmentSolver 里比热只喂两个时间常数（都是显示量，不进判据）
        string seg = Src(Path.Combine("Pt_Optimize", "Core", "SegmentSolver.cs"));
        Assert.Contains("double cMetal = Materials.PtDensity * area * props.Cp(p.TSetC);", seg);
        Assert.Equal(3, seg.Split("cMetal").Length - 1);      // 定义 1 次 + TauMetalS、TauWithGlassS 各 1 次
        Assert.Contains("res.TauMetalS = cMetal /", seg);
        // ★ 2026-09-18 Opus 5（合并 H×合并树 改门，写明变因）：合并树上这一行已由 G3 空管那一路加了空管短路（`empty ? double.NaN :`），
        //   H 树上没有那一路，所以原断言钉的是没有短路的写法。门槛不变（照样钉住「含玻璃时间常数用 cMetal + cGlass」），只把字面对齐合并树。
        Assert.Contains("res.TauWithGlassS = empty ? double.NaN : (cMetal + cGlass) /", seg);

        // 热稳定裕度不读比热（它是 β / 去稳定斜率）
        Assert.Contains("res.StabilityRatio = destab > 1e-12 ? beta / destab : 999;", seg);

        // 吃稳态场的那几档一个字都不许有
        foreach (string f in new[] { "ShellThermal.cs", "PlateThermal2D.cs", "Solver.cs", "LineSolver.cs",
                                     "Criteria.cs", "Sizer.cs", "Insulation.cs", "FlangeStability.cs" })
        {
            string s = Src(Path.Combine("Pt_Optimize", "Core", f));
            Assert.DoesNotContain("Materials.PtCp(", s);
            Assert.False(cpRe.IsMatch(s), f + " 读了比热（.Cp( 或 CpJPerKgK(），稳态链不该读比热");
        }

        // RampTwoNode 里三处：准静态电流的金属热容（**进设计链的唯一入口**）+ 两节点瞬态的管/法兰热容
        //（后两处只进 TwoNodeSegPeakA 与「法兰−管温差」参考量，DesignCurrent 档头写明它们不进尺寸链）
        string two = Src(Path.Combine("Pt_Optimize", "Core", "RampTwoNode.cs"));
        Assert.Equal(3, cpRe.Matches(two).Count);
        Assert.Contains("double capMetal = Materials.PtDensity * area * L * props.Cp(tubeTempC);", two);
        // ★ 2026-09-18 Opus 5（合并 H×合并树 改门，写明变因）：合并树上这两个式子是 G2「升温两节点补铜排通道与舌片保温」之后的写法 ——
        //   两个局部量改成了字段（_massTube／_capInsulTube…）、法兰那一式多了舌片保温热容 _capInsulTab。H 树上没有 G2，所以原断言钉的是旧字面。
        //   门槛不变（照样钉住「热容由 Materials.PtCp 逐点算」这一件事），只把字面对齐合并树。
        Assert.Contains("public double CapTube(double t) => _massTube * _props.Cp(t) + _capInsulTube;", two);
        Assert.Contains("public double CapFlange(double t) => _massFlange * _props.Cp(t) + _capInsulFlange + _capInsulTab;", two);
        Assert.Contains("=> QuasiStaticBreakdown(p, wallMm, tubeTempC, rateKPerH).CurrentA;", two);

        // RampSolver 里只有集总升温用时那一处（判据表里是参考量，见 LineRunner 的 CheckKind.Reference）
        string ramp = Src(Path.Combine("Pt_Optimize", "Core", "RampSolver.cs"));
        Assert.Equal(1, cpRe.Matches(ramp).Count);
        Assert.Contains("double CapMetal(double tC) => (massTube + massFlange) * props.Cp(tC);", ramp);
        _o.WriteLine("比热（经 PtProps）的调用点：" + string.Join("、", hits));
    }

    /// <summary>Pt-20%Rh 的电阻率拟合**待核**：注记与 ±5 % 带宽必须随牌号带出，借用的牌号也要带上。</summary>
    [Fact]
    public void 铂铑20电阻率拟合待核_注记与带宽随牌号带出()
    {
        foreach (string n in new[] { "Pt-Rh/80-20", "Umicore-PtRh20" })
        {
            var g = MaterialDb.Get(n);
            Assert.Contains("拟合待核", g.ResistivityCaveat);
            Assert.Contains("二次项", g.ResistivityCaveat);
            Assert.Contains("T₀ = 20 °C", g.ResistivityCaveat);
            Assert.Contains("旁证 B", g.ResistivityCaveat);
            Assert.Equal(0.05, g.ResistivityBandFrac, 12);
        }
        foreach (var g in MaterialDb.All.Values.Where(x => x.Composition != "Pt-20Rh"))
            Assert.True(g.ResistivityCaveat.Length == 0 && g.ResistivityBandFrac == 0,
                $"{g.Name}：本轮只给 Pt-20%Rh 记了电阻率存疑，别的牌号不许跟着被标");

        // 存疑的三条本身是可复核的事实，不是意见
        var r20 = MaterialDb.Get("Pt-Rh/80-20");
        var r10 = MaterialDb.Get("Pt-Rh/90-10");
        Assert.Equal(20.0, r20.T0C, 12);                                  // 其余牌号都是 0 °C
        Assert.Equal(0.0, r10.T0C, 12);
        Assert.True(Math.Abs(r20.Beta) > 9 * Math.Abs(r10.Beta), "二次项量级差 ≥ 9 倍（决定记录说 10 倍）");
        Assert.True(r20.ResistivityOhmM(1500) < r10.ResistivityOhmM(1500),
            "1500 °C 时本拟合让 Pt-20%Rh 的电阻率**低于** Pt-10%Rh —— 这正是待核的那一条；它若哪天被改掉，注记也该跟着改");
        _o.WriteLine($"1500 °C：Pt-20%Rh {r20.ResistivityOhmM(1500) * 1e8:0.0} µΩ·cm vs Pt-10%Rh {r10.ResistivityOhmM(1500) * 1e8:0.0} µΩ·cm"
                   + $"；二次项 {r20.Beta:0.000e+00} vs {r10.Beta:0.000e+00}；带宽 ±{r20.ResistivityBandFrac:P0}");
    }
}
