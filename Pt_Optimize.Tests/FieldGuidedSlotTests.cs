using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **按场给的位置开槽，量效果**（2026-09-05，用户：「不做不会知道效果」）。
///
/// 前两步量出来的（deliverable/移除优先级.txt）：
///   移除优先级 = 导热贡献 q ÷ 电流密度 J
///   舌片 0.61　圆盘 6.81　**圆盘背侧（x&gt;0）8.14**
///   最高的 12 个单元全部落在 **r ≈ 27–31 mm、背对舌片那一侧**
///
/// 这一步把槽真的开出去，量三件事：
///   ③ 的驱动量 **QFromTubeW**（法兰从管子抽走的热）降多少
///   **峰值电流密度** 升多少（代价）
///   **铂重** 省多少
///
/// ⚠ 只测不判。断言只钉「测量没退化」。
/// </summary>
public class FieldGuidedSlotTests
{
    private static FlangePlate Heater1(params FlangePlate.DiscSlot[] slots) => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0,
        TabParallel = false, WeldFilletLegMm = 0,
        DiscSlots = slots,
    };

    /// <summary>
    /// 算一个构型。**每一步都可能因为槽把板割断而发散** ——
    /// 割断之后电流没有回路，解不收敛、J 变 NaN，测试宿主会直接崩掉
    /// （2026-09-05 第一版就是这么崩的，连一行结果都没留下）。
    /// ⇒ 包起来，失败也要**留下证据**：那本身就是「这条槽不能开」的实测结论。
    /// </summary>
    private static (double QFromTube, double JPeak, double VolMm3, double TMax, string Err) Run(FlangePlate g)
    {
        try
        {
            var baseIn = new DesignInputs();
            var c = new LineCase { Base = baseIn };
            var mesh = FlangeMesher.Build(g, 0, 2.0, 8.0, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
            if (mesh.CellCount < 200) return (0, 0, 0, 0, $"网格只剩 {mesh.CellCount} 单元（槽把板切碎了）");
            var cur = ShellCurrent.SolveFor(c, mesh, totalCurrentA: 1000, rhoRefOhmMm: 1.1e-4,
                                            maxIter: 8000, tol: 1e-7);
            double jPk = cur.JMagAPerMm2.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (!(jPk > 0) || double.IsNaN(jPk) || double.IsInfinity(jPk))
                return (0, 0, mesh.VolumeMm3, 0, "电流解发散（多半是槽把板割断了，电流没有回路）");
            var th = ShellThermal.Solve(mesh, cur.JMagAPerMm2, baseIn,
                                        tRootC: 1150, insulBoundaryX: double.NaN,
                                        maxIter: 20000, tol: 1e-3);
            if (double.IsNaN(th.QFromTubeW)) return (0, jPk, mesh.VolumeMm3, 0, "热解发散");
            return (th.QFromTubeW, jPk, mesh.VolumeMm3, th.TMaxC, "");
        }
        catch (Exception ex) { return (0, 0, 0, 0, ex.GetType().Name + "：" + ex.Message); }
    }

    /// <summary>
    /// ★★★★★ **必须落到 APP 上，不许停在对话里**（用户 2026-09-05 原话）。
    ///
    /// 这是本仓最大的坑（HANDOVER §0.-3）：「CLI 验过的东西工程师点不到」。
    /// 槽这根旋钮走完整条链才算数：
    ///   旋钮（Solver.Knob.SlotSpan）→ 求解（DesignSpec.Plate → BuildCase）
    ///   → **界面控件**（逐片，与另外六个旋钮同排）→ 出图 → 存档 → 变更清单
    ///
    /// ⚠ 槽控件在输入栏底部，抓图时被滚动挡住 ⇒ --uishot 看不到它。
    ///   这条门就是那张抓不到的图的替代：**真造一个页面，问它有没有那些控件**。
    /// </summary>
    [Fact]
    public void 槽这根旋钮真的落到界面上()
    {
        var page = new PtOptimize.UI.LineDesignPage(new DesignInputs());
        var tabs = new System.Windows.Forms.TabControl { Dock = System.Windows.Forms.DockStyle.Fill };
        tabs.TabPages.Add(page);
        var form = new System.Windows.Forms.Form { Width = 1100, Height = 900 };
        form.Controls.Add(tabs);
        form.CreateControl();
        System.Windows.Forms.Application.DoEvents();

        var fld = typeof(PtOptimize.UI.LineDesignPage).GetField("_slotDeg",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(fld);
        var arr = (System.Windows.Forms.NumericUpDown[])fld!.GetValue(page)!;
        Assert.True(arr.Length >= 4, $"槽控件只有 {arr.Length} 个 —— 应当逐片各一个");
        Assert.All(arr, n => Assert.Equal(0m, n.Value));      // 开箱 0 = 不开槽

        // 标题与逐片行都要在屏幕上（文字由 Head/Row 生成）
        var labels = page.Controls.Cast<System.Windows.Forms.Control>()
            .SelectMany(Flatten).OfType<System.Windows.Forms.Label>()
            .Select(l => l.Text).ToArray();
        Assert.Contains(labels, t => t.Contains("圆盘背侧减重槽", StringComparison.Ordinal));
        Assert.Contains(labels, t => t.EndsWith(" 槽", StringComparison.Ordinal));
    }

    private static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control>
        Flatten(System.Windows.Forms.Control c)
    {
        yield return c;
        foreach (System.Windows.Forms.Control k in c.Controls)
            foreach (var x in Flatten(k)) yield return x;
    }

    /// <summary>
    /// ★★★★★ **槽的默认带必须让槽真的开得出来**（2026-09-05 出图时抓到）。
    ///
    /// 内边距原来写死 1 mm。焊脚 = max(板厚, 管壁)，板厚解到 4.71 mm 时
    /// 内桥只剩 1.0 mm &lt; 下限 ⇒ `SlotSpanMaxDeg` 返回 0 ⇒
    /// **槽这根旋钮被自己的默认带卡死**，而它是治「法兰增量温降」最有效的一根。
    /// 表现极隐蔽：求解器照常报「不可行」，**只字不提「槽根本没得开」**。
    /// </summary>
    [Theory]
    [InlineData(0.6)]      // 薄板：焊脚 = 管壁
    [InlineData(2.0)]
    [InlineData(4.71)]     // 出图那次解出来的板厚 —— 就是它把槽卡死的
    [InlineData(8.0)]      // 工艺上界
    public void 任何板厚下槽都开得出来(double plateMm)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = plateMm;
        double weldLeg = Math.Max(plateMm, d.WallMm);
        double maxSpan = d.SlotSpanMaxDeg(weldLeg);
        Assert.True(maxSpan > 30,
            $"板厚 {plateMm} mm（焊脚 {weldLeg:0.00}）时槽的张角上界只有 {maxSpan:0.0}° "
          + $"—— 槽这根旋钮被默认带卡死了。带 = {d.SlotBandMm(weldLeg)}");
    }

    /// <summary>★ 存档要带得走 —— 少一根旋钮的档，复算时槽会消失而判据表照样出数。</summary>
    [Fact]
    public void 槽存得进档也读得回来()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "★槽往返★ " + Guid.NewGuid().ToString("N")[..6];
        d.SlotSpanDeg = new[] { 15.0, 45.0, 75.0, 105.0 };
        d.SlotRInMm = 29.0; d.SlotROutMm = 42.0;
        string? written = null;
        try
        {
            written = DesignSpecStore.Save(d);
            var back = DesignSpecStoreTests.Parse(File.ReadAllText(written));
            Assert.NotNull(back);
            Assert.Equal(d.SlotSpanDeg, back!.SlotSpanDeg);
            Assert.Equal(29.0, back.SlotRInMm);
            Assert.Equal(42.0, back.SlotROutMm);
        }
        finally { if (written is not null && File.Exists(written)) File.Delete(written); }
    }

    [Fact]
    public void 按场开槽的效果()
    {
        var b = Run(Heater1());
        Assert.True(b.Err.Length == 0, "基准就算不出来：" + b.Err);
        Assert.True(b.VolMm3 > 1000, "基准体积退化 —— 下面全是空转");

        var sb = new StringBuilder();
        sb.AppendLine("═══ 按场给的位置开槽：效果实测 ═══");
        sb.AppendLine("槽开在**管孔外缘、背对舌片**那一侧 —— 位置由「导热÷电流」算出来的，不是拍的。");
        sb.AppendLine();
        sb.AppendLine("槽 (r内–r外, 张角)\t抽热 W\t较基准\t峰值 J\t较基准\t体积 mm³\t较基准\t最高温 °C");
        sb.AppendLine($"（无槽·基准）\t{b.QFromTube:0.0}\t—\t{b.JPeak:0.000}\t—\t{b.VolMm3:0}\t—\t{b.TMax:0}");

        // 张角逐档放大：这就是那个**可二分的标量**
        foreach (var (rin, rout, span) in new[]
        {
            (27.0, 31.0,  60.0), (27.0, 31.0, 120.0), (27.0, 31.0, 180.0),
            (27.0, 40.0, 120.0), (27.0, 40.0, 180.0), (27.0, 55.0, 180.0),
        })
        {
            var g = Heater1(new FlangePlate.DiscSlot(rin, rout, CenterDeg: 0, SpanDeg: span));
            var r = Run(g);
            if (r.Err.Length > 0)
            { sb.AppendLine($"{rin:0}–{rout:0} mm, {span:0}°	✗ {r.Err}"); continue; }
            sb.AppendLine($"{rin:0}–{rout:0} mm, {span:0}°"
                        + $"\t{r.QFromTube:0.0}\t{100 * (r.QFromTube / b.QFromTube - 1):+0.0;-0.0} %"
                        + $"\t{r.JPeak:0.000}\t{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %"
                        + $"\t{r.VolMm3:0}\t{100 * (r.VolMm3 / b.VolMm3 - 1):+0.0;-0.0} %"
                        + $"\t{r.TMax:0}");
        }

        // 对照：同样面积开在**舌片**上（场说那里不划算）—— 验证判据分得出高下
        sb.AppendLine();
        sb.AppendLine("── 对照：把料挖在**舌片**上（场说不划算的地方）──");
        foreach (double rr in new[] { 6.0, 10.0 })
        {
            var g = Heater1();
            g.TabHoles = new[] { new FlangePlate.TabHole(-100, 0, rr) };
            var r = Run(g);
            if (r.Err.Length > 0)
            { sb.AppendLine($"舌片圆孔 R{rr:0}	✗ {r.Err}"); continue; }
            sb.AppendLine($"舌片圆孔 R{rr:0}"
                        + $"\t{r.QFromTube:0.0}\t{100 * (r.QFromTube / b.QFromTube - 1):+0.0;-0.0} %"
                        + $"\t{r.JPeak:0.000}\t{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %"
                        + $"\t{r.VolMm3:0}\t{100 * (r.VolMm3 / b.VolMm3 - 1):+0.0;-0.0} %"
                        + $"\t{r.TMax:0}");
        }

        // ══ 弯椭圆 vs 尖角扇形（用户 2026-09-05：「再加个弯椭圆（用于圆盘）」）
        sb.AppendLine();
        sb.AppendLine("── 弯椭圆（沿中弧的胶囊形，两端半圆）vs 尖角扇形 ──");
        sb.AppendLine("同一条带（27–40 mm）、同一张角，只差两端要不要圆。");
        foreach (double span in new[] { 90.0, 150.0 })
            foreach (bool round in new[] { false, true })
            {
                var g = Heater1(new FlangePlate.DiscSlot(27, 40, 0, span, RoundEnds: round));
                var r = Run(g);
                if (r.Err.Length > 0) { sb.AppendLine($"{span:0}° {(round ? "弯椭圆" : "尖角扇形")}	✗ {r.Err}"); continue; }
                sb.AppendLine($"{span:0}° {(round ? "弯椭圆" : "尖角扇形")}"
                            + $"	{r.QFromTube:0.0}	{100 * (r.QFromTube / b.QFromTube - 1):+0.0;-0.0} %"
                            + $"	{r.JPeak:0.000}	{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %"
                            + $"	{r.VolMm3:0}	{100 * (r.VolMm3 / b.VolMm3 - 1):+0.0;-0.0} %"
                            + $"	{r.TMax:0}");
            }

        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "按场开槽_效果.txt"), sb.ToString());
    }
}
