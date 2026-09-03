using PtOptimize.Core;
using ScottPlot;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;
using ScottPlot.WinForms;

namespace PtOptimize.UI;

/// <summary>
/// 场图与剖面图（ScottPlot 5）。
///
/// 配色一律用感知均匀的顺序色阶（Viridis），不用彩虹/Jet——
/// 后者在数值上不均匀，会制造并不存在的「条带」。
/// 析晶判据（T &lt; T_liq）另用独立的等温线标出，不依赖颜色分辨。
/// </summary>
public static class FieldPlots
{
    private static readonly IColormap Ramp = new ScottPlot.Colormaps.Viridis();

    public static FormsPlot NewPlot()
    {
        var fp = new FormsPlot { Dock = DockStyle.Fill };
        fp.Plot.Axes.Bottom.Label.FontName = "Microsoft YaHei";
        fp.Plot.Axes.Left.Label.FontName = "Microsoft YaHei";
        fp.Plot.Axes.Title.Label.FontName = "Microsoft YaHei";
        return fp;
    }

    // ---------------------------------------------------------------
    // 子午面场图
    // ---------------------------------------------------------------

    public static void DrawPlate(FormsPlot fp, double[,] v, bool[,] mask,
                                 double x0, double z0, double h,
                                 string title, string label, string unit,
                                 double tubeOuterRmm = 26.0)
    {
        var plot = fp.Plot;
        plot.Clear();
        if (v.Length == 0) { fp.Refresh(); return; }

        int nx = v.GetLength(0), nz = v.GetLength(1);
        // ScottPlot 热图第 0 行画在顶部，而 z 自下而上增大 → 翻转；同时行列转置成 [z, x]
        var data = new double[nz, nx];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
                data[j, i] = mask[i, nz - 1 - j] ? v[i, nz - 1 - j] : double.NaN;

        var hm = plot.Add.Heatmap(data);
        hm.Extent = new CoordinateRect(x0, x0 + (nx - 1) * h, z0, z0 + (nz - 1) * h);
        hm.Colormap = Ramp;
        hm.Smooth = true;

        var cb = plot.Add.ColorBar(hm);
        cb.Label = $"{label} [{unit}]";
        cb.LabelStyle.FontName = "Microsoft YaHei";

        // 管孔一圈：电流全部由此交给管壁，是 J 峰值所在，画出来便于判读
        int n = 181;
        double[] cx = new double[n], cz = new double[n];
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / (n - 1);
            cx[k] = tubeOuterRmm * Math.Cos(a);
            cz[k] = tubeOuterRmm * Math.Sin(a);
        }
        var hole = plot.Add.Scatter(cx, cz);
        hole.Color = Colors.White; hole.LineWidth = 1.6f; hole.MarkerSize = 0;
        hole.LegendText = "管孔（= 铂金管外壁）";

        plot.Title(title);
        plot.XLabel("x [mm]（0 = 管轴，负向为舌片）");
        plot.YLabel("z [mm]");
        plot.Axes.SetLimits(x0, x0 + (nx - 1) * h, z0, z0 + (nz - 1) * h);
        fp.Refresh();
    }

    /// <summary>
    /// 壳网格上的标量场：每个单元按值着色画成小方块。
    /// 用于变步长/任意形状网格 —— 热图需要规则网格，这里网格不规则，故逐单元填充。
    /// </summary>
    public static void DrawShellField(FormsPlot fp, Core.ShellMesh m, double[] value,
                                      string title, string label, string unit,
                                      double holeRadiusMm = 26.0, double clipPercentile = 0.995)
    {
        var plot = fp.Plot;
        plot.Clear();
        if (m.CellCount == 0) { fp.Refresh(); return; }

        // 上限按分位数截断：J 在孔周有奇点，用最大值做色标会把其余全压成一色
        var sorted = value.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        double vMax = sorted.Length > 0 ? sorted[(int)Math.Min(sorted.Length - 1,
                                                  Math.Floor(sorted.Length * clipPercentile))] : 1;
        double vMin = sorted.Length > 0 ? sorted[0] : 0;
        if (vMax <= vMin) vMax = vMin + 1;

        for (int i = 0; i < m.CellCount; i++)
        {
            var nd = m.Cells[i];
            double xa = m.Nodes[nd[0]].X, xb = m.Nodes[nd[2]].X;
            double za = m.Nodes[nd[0]].Z, zb = m.Nodes[nd[2]].Z;
            double u = Math.Clamp((value[i] - vMin) / (vMax - vMin), 0, 1);
            var rect = plot.Add.Rectangle(Math.Min(xa, xb), Math.Max(xa, xb),
                                          Math.Min(za, zb), Math.Max(za, zb));
            rect.FillColor = Ramp.GetColor(u);
            rect.LineWidth = 0;
        }

        int n = 181; var cx = new double[n]; var cz = new double[n];
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / (n - 1);
            cx[k] = holeRadiusMm * Math.Cos(a); cz[k] = holeRadiusMm * Math.Sin(a);
        }
        var hole = plot.Add.Scatter(cx, cz);
        hole.Color = Colors.White; hole.LineWidth = 1.8f; hole.MarkerSize = 0;
        hole.LegendText = "管孔（= 铂金管外壁）";

        plot.Title($"{title}　{label} {vMin:0.00}–{vMax:0.00} {unit}（上限按 {clipPercentile:P1} 分位截断）");
        plot.XLabel("x [mm]（0 = 管轴，负向为舌片）");
        plot.YLabel("z [mm]");
        plot.ShowLegend();
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    private static void AddOutline(Plot plot, double[] xs, double[] rs, Color c, float w)
    {
        if (xs.Length < 2) return;
        var s = plot.Add.Scatter(xs, rs);
        s.LineWidth = w;
        s.MarkerSize = 0;
        s.Color = c;
        s.LegendText = string.Empty;
    }

    /// <summary>
    /// 画析晶等温线。管段温度只随 x 变 → 等温线是竖直段；
    /// 法兰温度只随 r 变 → 等温线是水平段。两者都由一维解精确定位，
    /// 不需要 marching squares。
    /// </summary>
    /// <summary>
    /// **空态**：还没有结果时画一行提示，而不是一个空坐标轴。
    ///
    /// ★ 2026-08-20：此前没数据时 ScottPlot 会画出 −10…10 的默认坐标轴，
    ///   **看着像「算坏了」而不是「还没算」** —— 用户分不出这两件事，
    ///   而它们要做的动作完全不同。提示语要直接说**下一步点什么**
    ///   （本项目既定诉求：「不看说明书也能用」）。
    /// </summary>
    public static void DrawEmpty(FormsPlot fp, string hint)
    {
        var plot = fp.Plot;
        plot.Clear();
        plot.Axes.Frameless();
        plot.HideGrid();
        plot.Axes.SetLimits(0, 1, 0, 1);
        var t = plot.Add.Text(hint, 0.5, 0.5);
        t.Alignment = Alignment.MiddleCenter;
        // ★ 跟着 DPI 走（2026-09-03 抓图）：写死 14 的话，在高 DPI 屏上这行字
        //   小到几乎看不清 —— 而它占的是首屏**最大的一块空白**，
        //   看不清就等于那块地方什么都没说。
        t.LabelFontSize = UiScale.S(15);
        t.LabelFontColor = new Color(120, 120, 120);
        // ⚠ **必须显式设中文字体**：NewPlot() 只给坐标轴与标题设了 Microsoft YaHei，
        //   Text 标注走的是另一套默认字体，中文会整串渲染成豆腐块 □□□。
        //   而豆腐块比空坐标轴更像「程序坏了」—— 那正是本方法要消除的观感。
        t.LabelFontName = "Microsoft YaHei";
        fp.Refresh();
    }

    /// <summary>
    /// **整线**轴向温度分布：三段首尾相接画成一条，段界（= 法兰所在处）标竖线。
    ///
    /// ★ 2026-08-20 补。此前「管轴向剖面」这个子页签**从建出来就没画过任何东西**
    ///   （声明、挂页签，然后 Show() 里只画了法兰的 T/J 两张场图）——
    ///   一个永远空白的页签比没有这个页签更坏：它看起来像是「这次没算出来」。
    ///
    /// 数据一直都在（<see cref="SegmentOut.X"/>/<see cref="SegmentOut.TMetal"/>/
    /// <see cref="SegmentOut.TGlass"/>），只是没人接上。
    ///
    /// 段界竖线不是装饰：法兰就在那儿，③「法兰增量温降」挖的坑正是在段界两侧，
    /// 这张图是唯一能**看见**那个坑的地方。
    /// </summary>
    public static void DrawLineAxialProfile(FormsPlot fp, LineResult r, DesignInputs p)
    {
        var plot = fp.Plot;
        plot.Clear();
        if (!r.Ok || r.Segments.Length == 0) { fp.Refresh(); return; }

        // 段偏移**从数据本身累加**，不从外面传段长进来：
        // 传进来的那个数（LineCase.SegLengthMm）与各段 X 的实际跨度是两个来源，
        // 它们一旦对不上，图会画得又连续又错 —— 而这种错没有任何东西会报。
        double x0 = 0;
        bool labelled = false;
        for (int i = 0; i < r.Segments.Length; i++)
        {
            var s = r.Segments[i];
            if (s.X.Length < 2) continue;

            var xs = s.X.Select(v => v + x0).ToArray();

            var metal = plot.Add.Scatter(xs, s.TMetal);
            metal.LineWidth = 2.2f; metal.MarkerSize = 0;
            metal.Color = new Color(20, 90, 200);
            // 三段是同一条曲线的三截，图例只写一次，否则「铂金属」会重复三行
            metal.LegendText = labelled ? string.Empty : "铂金属";

            var glass = plot.Add.Scatter(xs, s.TGlass);
            glass.LineWidth = 1.6f; glass.MarkerSize = 0;
            glass.Color = new Color(30, 160, 110);
            glass.LegendText = labelled ? string.Empty : "玻璃";

            if (labelled)
            {
                // 段界：法兰就在这儿。③「法兰增量温降」挖的坑正是在这条线两侧。
                var seam = plot.Add.VerticalLine(x0);
                seam.Color = new Color(150, 150, 150);
                seam.LineWidth = 1.0f;
                seam.LinePattern = LinePattern.Dotted;
                seam.LegendText = string.Empty;
            }
            labelled = true;

            x0 += s.X[^1] - s.X[0];
        }

        if (!labelled) { fp.Refresh(); return; }

        var liq = plot.Add.HorizontalLine(p.TLiquidusC);
        liq.Color = new Color(220, 60, 60);
        liq.LineWidth = 1.6f;
        liq.LinePattern = LinePattern.Dashed;
        liq.LegendText = $"T_liq = {p.TLiquidusC:0} °C";

        var safe = plot.Add.HorizontalLine(p.TLiquidusC + p.DevitMarginK);
        safe.Color = new Color(235, 160, 40);
        safe.LineWidth = 1.3f;
        safe.LinePattern = LinePattern.Dotted;
        safe.LegendText = $"安全线 +{p.DevitMarginK:0} K";

        plot.Title("整线轴向温度分布（竖线 = 段界，法兰所在）");
        plot.XLabel("x [mm]（全线累计）");
        plot.YLabel("温度 [°C]");
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    public static void DrawAxialProfile(FormsPlot fp, SolveResult r, DesignInputs p)
    {
        var plot = fp.Plot;
        plot.Clear();
        if (!r.Ok || r.X.Length < 2) { fp.Refresh(); return; }

        var metal = plot.Add.Scatter(r.X, r.TMetal);
        metal.LineWidth = 2.2f; metal.MarkerSize = 0;
        metal.Color = new Color(20, 90, 200);
        metal.LegendText = "铂金属";

        var glass = plot.Add.Scatter(r.X, r.TGlass);
        glass.LineWidth = 1.6f; glass.MarkerSize = 0;
        glass.Color = new Color(30, 160, 110);
        glass.LegendText = "玻璃";

        var liq = plot.Add.HorizontalLine(p.TLiquidusC);
        liq.Color = new Color(220, 60, 60);
        liq.LineWidth = 1.6f;
        liq.LinePattern = LinePattern.Dashed;
        liq.LegendText = $"T_liq = {p.TLiquidusC:0} °C";

        var safe = plot.Add.HorizontalLine(p.TLiquidusC + p.DevitMarginK);
        safe.Color = new Color(235, 160, 40);
        safe.LineWidth = 1.3f;
        safe.LinePattern = LinePattern.Dotted;
        safe.LegendText = $"安全线 +{p.DevitMarginK:0} K";

        plot.Title("轴向温度分布");
        plot.XLabel("x [mm]");
        plot.YLabel("温度 [°C]");
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    // ---------------------------------------------------------------
    // 法兰径向剖面：温度 + 厚度（实际 vs 理想）+ 局部自给率 φ
    // ---------------------------------------------------------------

    public static void DrawSavingCurve(string path, double[] wallMm, double[] massG,
                                       double wJ, double wDevit, PtOptimize.Core.DesignInputs p)
    {
        var fp = NewPlot();
        var plot = fp.Plot;
        plot.Clear();

        double tMin = Math.Max(double.IsNaN(wJ) ? 0 : wJ, double.IsNaN(wDevit) ? 0 : wDevit);
        double xLo = wallMm[0], xHi = wallMm[^1];
        double yLo = 0, yHi = 0;
        foreach (var m in massG) yHi = Math.Max(yHi, m);
        yHi *= 1.12;

        // 不可行区（t < t_min）阴影
        if (tMin > xLo)
        {
            var bad = plot.Add.Rectangle(xLo, tMin, yLo, yHi);
            bad.FillColor = new Color(220, 60, 60).WithAlpha(0.10);
            bad.LineWidth = 0;
        }

        var line = plot.Add.Scatter(wallMm, massG);
        line.LineWidth = 2.6f; line.MarkerSize = 7;
        line.Color = new Color(20, 90, 200);
        line.LegendText = "总铂重";

        if (!double.IsNaN(wJ))
        {
            var v = plot.Add.VerticalLine(wJ);
            v.Color = new Color(220, 60, 60);
            v.LineWidth = 2f; v.LinePattern = LinePattern.Dashed;
            v.LegendText = $"J = {p.JAllowAPerMm2:0.0} A/mm²  (t = {wJ:0.00} mm)";
        }
        if (!double.IsNaN(wDevit))
        {
            var v = plot.Add.VerticalLine(wDevit);
            v.Color = new Color(235, 160, 40);
            v.LineWidth = 2f; v.LinePattern = LinePattern.Dotted;
            v.LegendText = $"析晶裕度 = {p.DevitMarginK:0} K  (t = {wDevit:0.00} mm)";
        }

        plot.Title("省铂曲线 — 全包保温 + 水冷铜排夹（法兰厚度按 J 约束自动定尺）");
        plot.XLabel("铂管壁厚 [mm]");
        plot.YLabel("总铂重（管 + 一对法兰）[g]");
        plot.Axes.SetLimits(xLo, xHi, yLo, yHi);
        fp.Plot.SavePng(path, 1150, 640);
    }
}