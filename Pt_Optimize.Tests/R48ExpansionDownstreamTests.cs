using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// **下游的门**：报告层读到的伸长必须是按改正后的数据算的（2026-09-18，Opus 5；用户 2026-09-18 原话「改」）。
///
/// 为什么门要造在这里、而不是只造在 <see cref="PtThermalExpansion"/> 里 ——
/// 本项目的教训「新状态位默认没接上」：赋了值 ≠ 用它的人读得到。G18 那一格改正后，
/// 真正会被工程师看见的数是「某牌号某长度的件升到某温度伸长多少毫米」，
/// 所以门要钉在**按牌号的公开入口**（`Elongation`／`Strain`／`LengthRatio`）上，不是钉在系数上。
///
/// 这条门**不读生产系数**：它每次直接读《鉑金热膨胀计算.xlsx》的 PtRh20 数据点（chart1 系列 idx 2 的 x／y 引用），
/// 用精确有理数最小二乘按同阶数复算、取 7 位（与 Excel 标签同口径），再按工作簿 Y、Z 列的公式算伸长。
/// ⇒ 两个方向都会红：
///   · 把工作簿 G18 改回 1.0157（或改成别的数）⇒ 门算出来的期望变、生产存的系数没变 ⇒ 红；
///   · 把 <see cref="PtThermalExpansion"/> 里的系数改回改前那一组 ⇒ 生产值变、期望没变 ⇒ 红。
///
/// 数与出处：G18 1.0157 → 1.0152，依据 B. Barter &amp; A. S. Darling, Platinum Metals Rev., 1960, 4 (4), 138-140，
/// 表在 p.139「20 % Rh」栏。
/// </summary>
public class R48ExpansionDownstreamTests
{
    private readonly ITestOutputHelper _o;
    public R48ExpansionDownstreamTests(ITestOutputHelper o) => _o = o;

    /// <summary>门自己登记的决定记录（毫米，4 位小数）：500 mm 的 PtRh20 件 0 → 1400 °C。</summary>
    private const double DecidedNewMm = 7.5972;   // 改正后（论文 1.0152）
    private const double DecidedOldMm = 7.7513;   // 改前（工作簿 1.0157）—— 只作对照，不是现在的值

    /// <summary>改正**前**存在 <c>PtThermalExpansion</c> 里的 PtRh20 系数（c0..c6，= Excel 旧标签文本）。只作对照。</summary>
    private static readonly double[] StaleCoeffs =
        { 4.206619E+00, 2.352578E-02, -6.760337E-05, 1.233031E-07, -1.256153E-10, 6.567769E-14, -1.362330E-17 };

    private static double Poly(IReadOnlyList<double> c, double t)
    {
        double acc = 0;
        for (int k = c.Count - 1; k >= 0; k--)
        {
            double p = 1;
            for (int i = 0; i < k; i++) p *= t;
            acc += c[k] * p;
        }
        return acc;
    }

    /// <summary>直接从 xlsx 的数据点复算 PtRh20 的 6 阶趋势线系数，取 7 位（不碰生产系数）。</summary>
    private static double[] RefitPtRh20FromWorkbook(out int order, out int points)
    {
        using var book = XlsxBook.OpenInRepo(PtThermalExpansion.WorkbookFile);
        var sh = book.Sheet(PtThermalExpansion.SheetName);
        var ser = XlsxChart.Series(book.Part("xl/charts/chart1.xml"))
            .Single(s => XlsxChart.Expand(s.YRef).Cells.Contains("H18"));   // PtRh20 那条（H 列 = 20 % Rh 的 α）
        var (_, xc) = XlsxChart.Expand(ser.XRef);
        var (_, yc) = XlsxChart.Expand(ser.YRef);
        var x = xc.Take(yc.Count).Select(sh.Num).ToList();
        var y = yc.Select(sh.Num).ToList();
        order = ser.TrendOrder ?? throw new InvalidOperationException("PtRh20 系列没有趋势线阶数");
        points = y.Count;
        var fit = ExactPolyFit.Fit(x, y, order);
        return fit.Coeffs
            .Select(q => double.Parse(q.ToExcelSci(7), NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
    }

    /// <summary>
    /// 报告层读到的伸长：500 mm 的 Pt-Rh/80-20 件 0 → 1400 °C = 7.5972 mm（按改正后的数据复算）；
    /// 改前的系数给 7.7513 mm，差 0.1541 mm；工作簿表值直接相减（1.0157 − 1.0152）× 500 mm = 0.2500 mm。
    /// 三个数都比伸缩余量 δ 默认 0.1 mm 大。
    /// </summary>
    [Fact]
    public void 下游按牌号读到的伸长等于按改正后数据复算的值_改回旧系数或旧格子都会红()
    {
        const double l0 = 500, t = 1400;
        var refit = RefitPtRh20FromWorkbook(out int order, out int points);
        Assert.True(order == 6 && points == 15, $"PtRh20 趋势线是 {order} 阶 {points} 点 —— 与登记的 6 阶 15 点不同");

        // 期望：照工作簿 Y、Z 列的公式（=V*(1+X*0.000001*U)、=Y−V）用复算系数算
        double alphaExpect = Poly(refit, t);
        double expectMm = PtThermalExpansion.HotLengthFromAlpha(l0, alphaExpect, t) - l0;

        // 生产：报告层唯一该调的入口（按牌号，不碰曲线）
        var got = PtThermalExpansion.Elongation("Pt-Rh/80-20", l0, t);
        Assert.True(got.HasValue && got.Curve == "PtRh20" && got.Link == ExpansionLink.Direct, got.Note);
        Assert.True(got.Coverage == ExpansionCoverage.InRange,
            $"1400 °C 在数据区间内、G18 改正后也不该再标形状不可信，却标 {got.Coverage}：{got.Note}");
        Assert.True(Math.Abs(got.Value - expectMm) <= 1e-9,
            $"Elongation(\"Pt-Rh/80-20\", {l0}, {t}) = {got.Value:R} mm ≠ 按工作簿数据复算的 {expectMm:R} mm"
            + "（差得动 ⇒ 要么工作簿那一格被改回去了，要么代码系数没跟着重算）");
        Assert.Equal(DecidedNewMm, Math.Round(got.Value, 4));

        // 借用它的牌号读到同一个数（报告里会点名借用）
        var borrowed = PtThermalExpansion.Elongation("Umicore-PtRh20", l0, t);
        Assert.True(borrowed.IsBorrowed && Math.Abs(borrowed.Value - got.Value) <= 0, "Umicore-PtRh20 借 PtRh20，值必须逐位相同");

        // 对照①：改前那组系数
        double staleMm = PtThermalExpansion.HotLengthFromAlpha(l0, Poly(StaleCoeffs, t), t) - l0;
        Assert.Equal(DecidedOldMm, Math.Round(staleMm, 4));
        double dCoeff = staleMm - got.Value;
        Assert.True(dCoeff > 0.15 && dCoeff < 0.16,
            $"改前／改后系数给的伸长差 {dCoeff:0.0000} mm，与登记的 0.1541 mm 不符 —— 先改登记再改码");

        // 对照②：工作簿表值直接相减（报告若拿表值算，差的是这个数）
        double dTable = l0 * (1.0157 - 1.0152);
        Assert.True(Math.Abs(dTable - 0.25) <= 1e-9, "表值差应为 0.2500 mm");

        // 三个数都得比伸缩余量 δ 默认值大 —— 这才是「必须改」的理由
        const double deltaDefaultMm = 0.1;
        Assert.True(dCoeff > deltaDefaultMm && dTable > deltaDefaultMm, "反自证：差若小于 δ 默认 0.1 mm，这条门没有存在理由");

        _o.WriteLine($"500 mm 的 Pt-Rh/80-20 件 0 → 1400 °C：改正后 {got.Value:0.000000} mm（{got.Coverage}）；"
                   + $"改前系数 {staleMm:0.000000} mm，差 {dCoeff:0.000000} mm；工作簿表值直接相减差 {dTable:0.0000} mm；"
                   + $"伸缩余量 δ 默认 {deltaDefaultMm:0.0} mm");
        _o.WriteLine($"平均 α(1400 °C) = {got.Value / l0 / t * 1e6:0.000000} ×1e-6 /K；出处：{PtThermalExpansion.Curves["PtRh20"].DataSource}");

        // 工作温度带上一路核（报告可能按段打印）：每一点都对复算系数，且都不是形状不可信
        for (double tt = 1100; tt <= 1450; tt += 50)
        {
            var v = PtThermalExpansion.Elongation("Pt-Rh/80-20", l0, tt);
            double e = PtThermalExpansion.HotLengthFromAlpha(l0, Poly(refit, tt), tt) - l0;
            Assert.True(Math.Abs(v.Value - e) <= 1e-9 && v.Coverage == ExpansionCoverage.InRange,
                $"{tt} °C：{v.Value:R} vs 复算 {e:R}（{v.Coverage}）");
            _o.WriteLine($"  {tt:0} °C：{v.Value:0.0000} mm（{v.Coverage}）");
        }
    }

    /// <summary>
    /// ⚠ 登记「还没接上」这件事本身（2026-09-18，Opus 5）：
    /// 膨胀函数目前**只**被 <c>MaterialDb</c>（映射齐全度自检）碰过，报告层（<c>InstallReport</c> 等）一个字都没调。
    /// 也就是说上面那条伸长的门守的是**入口**，不是工程师真看到的那张纸 —— 这一步没做完，别让它看起来做完了。
    /// 谁把膨胀接进报告，这条门会红，那时必须：① 改这里的白名单；② 在报告层再钉一条「纸上印的数 == 入口给的数」。
    /// </summary>
    [Fact]
    public void 膨胀函数还没有接进报告层_谁接谁回来改这条门()
    {
        string root = HandoverDoc.Root();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "Pt_Optimize", "Core", "PtThermalExpansion.cs"),
            Path.Combine(root, "Pt_Optimize", "Core", "MaterialDb.cs"),
            // ★ 2026-09-18 Opus 5（合并 H×L，写明变因）：L 路的升温全程扫描 RampSweep 已经在读膨胀函数
            //   （Core/RampSweep.cs 里 StrainDifference 与 Strain 两处）—— 那是**算的那一层**，不是工程师看的那张纸。
            //   加进白名单，同时在下面加一条「RampSweep 还没有生产调用方」，把「算得出、点不到」这件事登记住。
            Path.Combine(root, "Pt_Optimize", "Core", "RampSweep.cs"),
            // ★ 2026-09-18 Opus 5（M 路，界面接线）：报告层接上了 —— 伸长量进输出框与安装报告那一新节
            //   （标题写法只有一处：FinalCheckReport.SectionTitle）。
            //   本档**只印**（逐格原样印 RampSegInfo/RampFlangeInfo 的字段，一个数都不重算），
            //   引用 PtThermalExpansion 只出现在说明里，为的是让读报告的人查得到那张表的值从哪来。
            //   配套的第二条门（「纸上印的 == 入口给的」，改成自己再算一遍 ⇒ 红）在 R48MThreeStateWiringTests。
            Path.Combine(root, "Pt_Optimize", "Core", "FinalCheckReport.cs"),
        };
        var hits = new List<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "Pt_Optimize"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.ReadAllText(f).Contains("PtThermalExpansion", StringComparison.Ordinal)) continue;
            if (!allowed.Contains(f)) hits.Add(Path.GetRelativePath(root, f));
        }
        Assert.True(hits.Count > 0 || allowed.All(File.Exists), "反自证：白名单里的档必须真的存在");
        _o.WriteLine("生产侧引用膨胀函数的档：" + string.Join("、", allowed.Select(a => Path.GetRelativePath(root, a))));
        // ★ 2026-09-18 Opus 5（合并 H×L）：登记「算得出、工程师点不到」——
        //   合并树里 RampSweep.Run 的调用方**全部在 Pt_Optimize.Tests 里**，UI 与 Program 一处都没有。
        //   谁把升温全程接进界面／安装报告，这条会红，那时必须同时钉一条「纸上印的伸长 == PtThermalExpansion 按牌号给的值」。
        var rampCallers = new List<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "Pt_Optimize"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.ReadAllText(f).Contains("RampSweep.Run(", StringComparison.Ordinal)) rampCallers.Add(Path.GetRelativePath(root, f));
        }
        _o.WriteLine("生产侧调 RampSweep.Run 的档：" + (rampCallers.Count == 0 ? "（一个都没有 —— 升温全程还只在测试里跑）" : string.Join("、", rampCallers)));
        // ★★★★★ 2026-09-18，Opus 5（M 路，界面接线）：**这条门反过来了。**
        //   原来它登记的是「升温全程算得出、工程师点不到」（rampCallers 必须为 0）。
        //   本轮 Core/FinalCheck.cs 成了第一个生产调用方：终验按顺序跑齐三关，
        //   结论与伸长表进输出框与安装报告（③ 页 VerifyMeshAsync → FinalCheck.Run）。
        //   ⇒ 从此要求**必须有**生产调用方：谁把它拆掉（回到只有测试在调），这里当场红。
        //   配套那条「纸上印的伸长 == 入口给的那个数」在 R48MThreeStateWiringTests 里逐格钉着。
        Assert.True(rampCallers.Count > 0,
            "升温全程（RampSweep.Run）又没有生产调用方了 —— 2026-09-18 已接进终验三关（Core/FinalCheck.cs）。"
            + "拆掉它等于把「升温全程」这一关从工程师手上拿走，而界面仍按三关在说话。");
        Assert.Contains(Path.Combine("Pt_Optimize", "Core", "FinalCheck.cs"), rampCallers);

        Assert.True(hits.Count == 0,
            "膨胀函数被接进了新的生产档：" + string.Join("、", hits)
            + " —— 接进报告层是好事，但请同时：① 把这些档加进本门的白名单；"
            + "② 在报告层钉一条「报告里印出来的伸长 == PtThermalExpansion 按牌号给的值」，"
            + "否则 G18 这种改正又会停在函数里、到不了工程师手上（本项目教训「新状态位默认没接上」）");
    }
}
