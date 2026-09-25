using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **保温方案表**（<see cref="InsulationPlan"/>）的门 —— 2026-09-18，Opus 5。
///
/// ══ 出处（用户 2026-09-18 原话）
///
/// 主会话问「圆盘区能不能用预制保温块做到 20 mm」，用户答：<c>「还是只给材质保温厚度方案就行」</c>
/// ⇒ APP 的交付物是**材质与各区厚度方案**，怎么包由现场定。
/// ⇒ 那份方案**必须在界面与报告上看得到**，否则「只给方案」这句话在 APP 里落不了地
///   （本项目最大的坑：CLI 验过的东西工程师点不到）。
///
/// ══ 这几道门守的是
/// <code>
///   ① 表里每一个数都来自**结果与设计**（LineResult / DesignSpec / DesignInputs）——
///      改设计里的厚度，表跟着变；不跟着变就是手抄了一份；
///   ② 材质名、片名都不是自己起的：材质取参数表那几层的 Name，片名取这一次真解了哪几片；
///   ③ k 的出处**没有就印「出处待补」**，有就原样印 —— 不许编一个（凭空的出处比没有出处更坏）；
///   ④ 折合层数与圈数提示只调生产那一份（InsulationSearch.LayersText／WrapLimits.TurnsLine），不手抄；
///   ⑤ **三处同源**：③ 页那张表、输出框、安装报告 5b 节都调 InsulationPlan ——
///      **注射「改回不印」⇒ 红**（安装报告里少了这一节，这道门当场响）。
/// </code>
///
/// 2026-09-18，Opus 5
/// </summary>
public class R48MInsulPlanGateTests
{
    private static string Code(string rel)
        => File.ReadAllText(Path.Combine(HandoverDoc.Root(), rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>与 FlangeKitTests 同一套样本：三片、两段，结果只填表用得到的那几格。</summary>
    private static (LineResult r, DesignSpec d, DesignInputs p) Sample()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SetpointC = new[] { 1150.0, 1080.0 }; d.SegLengthMm = new[] { 300.0, 300.0 }; d = d.Fit();
        d.FlangeInsulated = true;
        var p = new DesignInputs();
        var r = new LineResult
        {
            Ok = true, Converged = true,
            Flanges = new[]
            {
                new FlangeOut { Name = "入口" },
                new FlangeOut { Name = "共用1" },
                new FlangeOut { Name = "出口" },
            },
        };
        return (r, d, p);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ① 数从结果与设计来 —— 改了设计，表跟着改
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 厚度那一栏：管身取 <c>DesignSpec.TubeInsulMm</c>、圆盘取 <c>DiscInsulMmOf(j)</c>、舌板取 <c>TabInsulMm[j]</c>。
    /// **改设计 ⇒ 表跟着变**；表若是手抄的一份，这里当场红。
    /// </summary>
    [Fact]
    public void 门_厚度来自设计_改了设计表跟着改()
    {
        var (r, d, p) = Sample();
        d.TubeInsulMm = 7.5;
        d.FlangeInsulMm = 12.5;                       // 整线圆盘
        d.DiscInsulMm = new[] { 3.0, 12.5, 6.5 };     // 逐片盖掉（第二片留整线值）
        d.TabInsulMm = new[] { 5.1, 2.5, 10.4 };

        var rows = InsulationPlan.Build(r, d, p);

        double Thick(string zone) => rows.Single(w => w.Zone == zone).ThickMm;
        Assert.Equal(7.5, Thick("管身 内层（贴铂）"), 9);
        Assert.Equal(3.0, Thick("圆盘 入口"), 9);
        Assert.Equal(12.5, Thick("圆盘 共用1"), 9);   // 逐片给的就是整线那个数
        Assert.Equal(6.5, Thick("圆盘 出口"), 9);
        Assert.Equal(5.1, Thick("舌板 入口"), 9);
        Assert.Equal(2.5, Thick("舌板 共用1"), 9);
        Assert.Equal(10.4, Thick("舌板 出口"), 9);

        // 逐片那一栏真的走生产口径（同一个 j 两边一致）
        for (int j = 0; j < 3; j++)
            Assert.Equal(d.DiscInsulMmOf(j), rows.Single(w => w.Zone == $"圆盘 {r.Flanges[j].Name}").ThickMm, 12);

        // 再改一次，表必须跟着动（不动 = 表里的数是抄来的）
        d.TubeInsulMm = 2.0;
        Assert.Equal(2.0, InsulationPlan.Build(r, d, p).Single(w => w.Zone == "管身 内层（贴铂）").ThickMm, 9);
    }

    /// <summary>片名取自 <c>LineResult.Flanges</c>（这一次真的解了哪几片），不在表里另起一套片名。</summary>
    [Fact]
    public void 门_片名来自本次结果()
    {
        var (r, d, p) = Sample();
        r.Flanges[1].Name = "共用甲";
        var rows = InsulationPlan.Build(r, d, p);
        Assert.Contains(rows, w => w.Zone == "圆盘 共用甲");
        Assert.Contains(rows, w => w.Zone == "舌板 共用甲");
        Assert.DoesNotContain(rows, w => w.Zone == "圆盘 共用1");
    }

    /// <summary>材质名取参数表那几层的 Name —— 改了参数表，表跟着改。</summary>
    [Fact]
    public void 门_材质名来自参数表那几层()
    {
        var (r, d, p) = Sample();
        p.Layer1.Name = "某牌号纤维毡";
        var rows = InsulationPlan.Build(r, d, p);

        Assert.Equal("某牌号纤维毡", rows.Single(w => w.Zone == "管身 内层（贴铂）").Material);
        // 圆盘与舌板的保温材料在热解里就是拿 Layer1 的 k 造的（DesignScreen.PlateFluxWPerM2）⇒ 同一个名字
        Assert.All(rows.Where(w => w.Zone.StartsWith("圆盘", StringComparison.Ordinal)
                                || w.Zone.StartsWith("舌板", StringComparison.Ordinal)),
                   w => Assert.Equal("某牌号纤维毡", w.Material));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ② 出处：没有就照实说，不许编
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>InsulationLayer.KSourceNote</c> 空 ⇒ 印 <see cref="InsulationPlan.NoSourceText"/>；
    /// 填了 ⇒ **原样**印那一句。生产默认三层都是空的（这几个系数在仓库里查不到来源）。
    /// </summary>
    [Fact]
    public void 门_k的出处没有就印出处待补_有就原样印()
    {
        var (r, d, p) = Sample();

        // 生产默认：查不到来源 ⇒ 照实说
        Assert.Equal("", new InsulationLayer().KSourceNote);
        var rows0 = InsulationPlan.Build(r, d, p);
        Assert.All(rows0, w => Assert.Equal(InsulationPlan.NoSourceText, w.KSource));
        string txt0 = InsulationPlan.Text(rows0, 1150);
        Assert.Contains(InsulationPlan.NoSourceText, txt0);
        Assert.Contains("不许在报告里编一个出处", txt0);

        // 填了出处 ⇒ 原样印。只填内层① ⇒ 只有取内层① 的那几行变（管身内层、圆盘、舌板），
        // 中层那一行照旧「出处待补」—— **逐行各说各的出处**，不许一行填了就全表跟着写
        const string Src1 = "某某供应商 2026 版数据表 第 12 页";
        p.Layer1.KSourceNote = Src1;
        var rows1 = InsulationPlan.Build(r, d, p);
        Assert.All(rows1.Where(w => w.Zone != "管身 中层"), w => Assert.Equal(Src1, w.KSource));
        Assert.Equal(InsulationPlan.NoSourceText, rows1.Single(w => w.Zone == "管身 中层").KSource);
        string txt1 = InsulationPlan.Text(rows1, 1150);
        Assert.Contains(Src1, txt1);
        Assert.Contains(InsulationPlan.NoSourceText, txt1);   // 中层那一行还缺，告示照留

        // 三层都填上 ⇒ 全表都有出处，「出处待补」与那句告示一起消失
        const string Src2 = "另一家 2025 版数据表 第 3 页";
        p.Layer2.KSourceNote = Src2; p.Layer3.KSourceNote = Src2;
        string txt2 = InsulationPlan.Text(r, d, p);
        Assert.Contains(Src2, txt2);
        Assert.DoesNotContain(InsulationPlan.NoSourceText, txt2);
        Assert.DoesNotContain("不许在报告里编一个出处", txt2);
    }

    /// <summary>k(T) 那一栏调的是热解同一支函数 <c>InsulationLayer.KAt</c>，不在表里另写一个式子。</summary>
    [Fact]
    public void 门_k那一栏调热解同一支函数()
    {
        var (r, d, p) = Sample();
        double tRef = InsulationPlan.RefTempC(r, p);
        var w = InsulationPlan.Build(r, d, p).Single(x => x.Zone == "管身 内层（贴铂）");

        Assert.Contains($"k({tRef:0} °C) = {p.Layer1.KAt(tRef):0.###}", w.KText);
        Assert.Contains($"k(20 °C) = {p.Layer1.KAt(20.0):0.###}", w.KText);

        // 对照温度：有段就取最高的段控温点，没有段才退回参数表设定温度
        Assert.Equal(p.TSetC, tRef, 9);                               // 本样本没有 Segments
        var r2 = new LineResult
        {
            Ok = true, Converged = true, Flanges = r.Flanges,
            Segments = new[] { new SegmentOut { SetpointC = 900 }, new SegmentOut { SetpointC = 1150 } },
        };
        Assert.Equal(1150, InsulationPlan.RefTempC(r2, p), 9);
    }

    // ────────────────────────────────────────────────────────────────────
    //  ③ 折合层数与圈数提示：只调生产那一份
    // ────────────────────────────────────────────────────────────────────

    /// <summary>折合层数走 <c>InsulationSearch.LayersText</c>、圈数提示走 <c>WrapLimits.TurnsLine</c>，一个字都不手抄。</summary>
    [Fact]
    public void 门_层数与圈数只调生产那一份()
    {
        var (r, d, p) = Sample();
        d.FlangeInsulMm = 12.5; d.DiscInsulMm = Array.Empty<double>();
        var w = InsulationPlan.Build(r, d, p).Single(x => x.Zone == "圆盘 入口");

        Assert.Equal(InsulationSearch.LayersText(12.5), w.Layers);
        Assert.Equal("25 层", w.Layers);                                   // 自证：这一份折算真的动了
        Assert.Equal(WrapLimits.TurnsLine("圆盘 入口", 12.5), w.Turns);
        Assert.Contains(WrapLimits.PrefabNote, w.Turns);                   // 25 圈 > 20 ⇒ 提示预制块

        // 20 圈以内不带那句提示
        d.FlangeInsulMm = 9.0;
        Assert.DoesNotContain(WrapLimits.PrefabNote,
            InsulationPlan.Build(r, d, p).Single(x => x.Zone == "圆盘 入口").Turns);

        // 算不出来照实印「—」，不许拿一个数顶上
        // ⚠ 逐片那一栏写 NaN **不是**「算不出来」：生产口径里 NaN = 这一片沿用整线值
        //   （FlangePlate.DiscInsulEffective，DesignSpecStoreTests 守着）⇒ 要让它真算不出来，得整线值本身是 NaN。
        d.DiscInsulMm = Array.Empty<double>(); d.FlangeInsulMm = double.NaN;
        var nan = InsulationPlan.Build(r, d, p).Single(x => x.Zone == "圆盘 入口");
        Assert.True(double.IsNaN(nan.ThickMm), "整线圆盘保温是 NaN，表上却印出了一个厚度");
        Assert.Equal("—", nan.Layers);
        Assert.Contains("算不出来", nan.Turns);
        Assert.Contains("\t—\t", InsulationPlan.Text(new[] { nan }, 1150));
    }

    // ────────────────────────────────────────────────────────────────────
    //  ④ 三处同源 ＋ 注射「改回不印」
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ★★★ **注射「改回不印」⇒ 红**：安装报告里必须有 5b「保温方案」这一节，
    /// 且它印的就是 <see cref="InsulationPlan.Text(LineResult, DesignSpec, DesignInputs)"/> 那一份（逐字包含）。
    /// 谁把这一节删掉（= 退回 2026-09-18 之前「报告里没有保温方案」的样子），这条当场响。
    /// </summary>
    [Fact]
    public void 门_安装报告有保温方案节_改回不印当场红()
    {
        var (r, d, p) = Sample();
        d.FlangeInsulMm = 12.5;
        string rep = InstallReport.Build(r, d, p, "");

        Assert.Contains($"**5b. {InsulationPlan.SectionTitle}**", rep);
        // 逐字包含那张表本身 —— 报告里另拼一份就会在这里露馅
        string plan = InsulationPlan.Text(r, d, p);
        Assert.True(plan.Length > 200, "表本身是空的 —— 下面那条恒真");
        Assert.Contains(plan, rep);

        // 表里该有的那几区都在（管身、逐片圆盘、逐片舌板）
        Assert.Contains("管身 内层（贴铂）", rep);
        foreach (var name in new[] { "入口", "共用1", "出口" })
        {
            Assert.Contains($"圆盘 {name}", rep);
            Assert.Contains($"舌板 {name}", rep);
        }
    }

    /// <summary>
    /// 三处同源的**源码门**：③ 页那张表只摆 <c>InsulationPlan.Build</c> 给的行、
    /// 输出框与安装报告都调 <c>InsulationPlan.Text</c>，没有第二处在拼这张表。
    /// </summary>
    [Fact]
    public void 门_三处同源_源码钉死()
    {
        string ui = Code("Pt_Optimize/UI/LineDesignPage.cs");
        string rep = Code("Pt_Optimize/Core/InstallReport.cs");

        // ③ 页：表格只摆 Build 的行；空态要清空（别留上一次的）
        Assert.Contains("foreach (var w in InsulationPlan.Build(r, d, _base))", ui);
        Assert.Contains("_insulGrid.Rows.Clear();", ui);
        Assert.Contains("FillInsulPlan(r, d);", ui);
        // 输出框
        Assert.Contains("sb.Append(InsulationPlan.Text(r, dKit, _base));", ui);
        // 安装报告
        Assert.Contains("sb.Append(InsulationPlan.Text(r, d, p));", rep);
        // 页签名与节标题只有 InsulationPlan 一份写法
        Assert.Contains("InsulationPlan.Title", ui);
        Assert.Contains("InsulationPlan.SectionTitle", rep);
    }

    /// <summary>
    /// 表里不许出现判据代号（工程师看得见的字）——
    /// 这一份已经进了 <see cref="VisibleText.CoreSources"/>，本条是行为侧的复核。
    /// </summary>
    [Fact]
    public void 门_表里不出现判据代号()
    {
        var (r, d, p) = Sample();
        string txt = InsulationPlan.Text(r, d, p);
        foreach (var ch in Criteria.CodeChars)
            Assert.DoesNotContain(ch.ToString(), txt);
        Assert.Contains("Pt_Optimize/Core/InsulationPlan.cs", VisibleText.CoreSources);
    }
}
