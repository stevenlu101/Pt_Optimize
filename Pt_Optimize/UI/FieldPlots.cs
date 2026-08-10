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

    public static void DrawField(FormsPlot fp, MeridionalField f, DesignInputs p,
                                 SolveResult res, bool markLiquidus)
    {
        var plot = fp.Plot;
        plot.Clear();
        if (f.V.Length == 0) { fp.Refresh(); return; }

        int nr = f.V.GetLength(0), nx = f.V.GetLength(1);

        // ScottPlot 的 heatmap 第 0 行画在顶部，而我们的 r 是自下而上增大 → 翻转
        var data = new double[nr, nx];
        for (int j = 0; j < nr; j++)
            for (int i = 0; i < nx; i++)
                data[j, i] = f.V[nr - 1 - j, i];

        var hm = plot.Add.Heatmap(data);
        hm.Extent = new CoordinateRect(f.XMinMm, f.XMaxMm, f.YMin, f.YMax);
        hm.Colormap = Ramp;
        hm.Smooth = true;

        var cb = plot.Add.ColorBar(hm);
        cb.Label = $"{f.Label} [{f.Unit}]";
        cb.LabelStyle.FontName = "Microsoft YaHei";

        // 几何描边
        AddOutline(plot, f.TubeOutline.X, f.TubeOutline.R, Colors.White, 1.6f);
        AddOutline(plot, f.FlangeOutline.X, f.FlangeOutline.R, Colors.White, 1.6f);

        // 析晶等温线：管内为竖直线（T 只随 x 变），法兰内为水平线（T 只随 r 变）
        if (markLiquidus) AddLiquidusMarks(plot, f, p, res);

        plot.Title($"法兰—管连接区  {f.Label}");
        plot.XLabel("轴向 x [mm]（0 = 法兰端面）");
        plot.YLabel($"半径 r [mm]（管壁径向放大 {f.WallExaggeration:0}×，与轴向不等比）");
        plot.Axes.SetLimits(f.XMinMm, f.XMaxMm, f.YMin, f.YMax);
        if (f.RTickPos.Length > 0)
            plot.Axes.Left.TickGenerator =
                new ScottPlot.TickGenerators.NumericManual(f.RTickPos, f.RTickLab);
        fp.Refresh();
    }

    /// <summary>
    /// 法兰平面（x–z）的二维场热图。
    ///
    /// ★ 数据必须来自 <see cref="Core.PlateThermal2D"/> / <see cref="Core.PlateCurrent2D"/>，
    ///   **不要**用 <see cref="Core.FieldMap"/> 的子午面图去看法兰 —— 后者的法兰部分
    ///   走的是已作废的一维环形模型（HANDOVER §5），画出来好看但不对。
    ///
    /// 掩膜外的格子置 NaN，ScottPlot 渲染为透明，于是轮廓即为法兰真实外形。
    /// </summary>
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
    private static void AddLiquidusMarks(Plot plot, MeridionalField f, DesignInputs p,
                                         SolveResult res)
    {
        double riTube = p.TubeIdMm * 0.5;
        double roTube = riTube + res.WallDesignMm;
        var fa = res.FlangeA;

        foreach (var (level, col, name) in new[]
        {
            (p.TLiquidusC,                     new Color(220, 60, 60),  "T_liq"),
            (p.TLiquidusC + p.DevitMarginK,    new Color(235, 160, 40), "安全线")
        })
        {
            // 管段：T(x) 穿越 level 的位置
            double? xc = CrossingAscending(res.X, res.TMetal, level);
            if (xc is double xv && xv >= f.XMinMm && xv <= f.XMaxMm)
            {
                var s = plot.Add.Scatter(new[] { xv, xv }, new[] { f.ToY(riTube), f.ToY(roTube) });
                s.LineWidth = 2.4f; s.MarkerSize = 0; s.Color = col;
                s.LegendText = $"{name} 管内 x={xv:0.0} mm";
            }

            // 法兰：T(r) 穿越 level 的位置
            if (fa.R.Length > 1)
            {
                double? rc = CrossingAscending(Scale(fa.R, 1000), fa.T, level);
                if (rc is double rv)
                {
                    double tf = InterpAt(Scale(fa.R, 1000), Scale(fa.Thick, 1000), rv);
                    var s = plot.Add.Scatter(new[] { 0.0, tf }, new[] { f.ToY(rv), f.ToY(rv) });
                    s.LineWidth = 2.4f; s.MarkerSize = 0; s.Color = col;
                    s.LegendText = $"{name} 法兰 r={rv:0.0} mm";
                }
            }
        }
        StyleLegend(plot);
    }

    /// <summary>在等距序列上找 y 首次穿越 level 的 x（线性插值）。无穿越返回 null。</summary>
    private static double? CrossingAscending(double[] xs, double[] ys, double level)
    {
        for (int i = 0; i < ys.Length - 1; i++)
        {
            double a = ys[i] - level, b = ys[i + 1] - level;
            if (a == 0) return xs[i];
            if (a * b < 0) return xs[i] + (xs[i + 1] - xs[i]) * a / (a - b);
        }
        return null;
    }

    private static double InterpAt(double[] xs, double[] ys, double x)
    {
        for (int i = 0; i < xs.Length - 1; i++)
            if (x >= xs[i] && x <= xs[i + 1])
            {
                double t = (x - xs[i]) / (xs[i + 1] - xs[i]);
                return ys[i] * (1 - t) + ys[i + 1] * t;
            }
        return ys[^1];
    }

    // ---------------------------------------------------------------
    // 轴向剖面
    // ---------------------------------------------------------------

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
        StyleLegend(plot);
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    // ---------------------------------------------------------------
    // 法兰径向剖面：温度 + 厚度（实际 vs 理想）+ 局部自给率 φ
    // ---------------------------------------------------------------

    public static void DrawFlangeProfile(FormsPlot fp, SolveResult r, DesignInputs p)
    {
        var plot = fp.Plot;
        plot.Clear();
        var fa = r.FlangeA;
        if (fa.R.Length < 2) { fp.Refresh(); return; }

        var rMm = Scale(fa.R, 1000);

        var t = plot.Add.Scatter(rMm, fa.T);
        t.LineWidth = 2.2f; t.MarkerSize = 0;
        t.Color = new Color(20, 90, 200);
        t.LegendText = "法兰温度 T(r)";

        var liq = plot.Add.HorizontalLine(p.TLiquidusC);
        liq.Color = new Color(220, 60, 60);
        liq.LineWidth = 1.6f; liq.LinePattern = LinePattern.Dashed;
        liq.LegendText = $"T_liq = {p.TLiquidusC:0} °C";

        plot.Title($"法兰径向剖面   Φ = {fa.PhiOverall:0.000}   " +
                   $"管根抽热 = {fa.QRootW:0} W   铂重 = {fa.MassKg * 1000:0} g");
        plot.XLabel("半径 r [mm]");
        plot.YLabel("温度 [°C]");
        StyleLegend(plot);
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    public static void DrawThicknessProfile(FormsPlot fp, SolveResult r, DesignInputs p)
    {
        var plot = fp.Plot;
        plot.Clear();
        var fa = r.FlangeA;
        if (fa.R.Length < 2) { fp.Refresh(); return; }

        var rMm = Scale(fa.R, 1000);

        var act = plot.Add.Scatter(rMm, Scale(fa.Thick, 1000));
        act.LineWidth = 2.4f; act.MarkerSize = 0;
        act.Color = new Color(20, 90, 200);
        act.LegendText = "实际厚度 t_f(r)";

        var ideal = plot.Add.Scatter(rMm, Scale(fa.ThickIdeal, 1000));
        ideal.LineWidth = 2.0f; ideal.MarkerSize = 0;
        ideal.LinePattern = LinePattern.Dashed;
        ideal.Color = new Color(200, 80, 40);
        ideal.LegendText = "理想厚度 C/r²  (φ ≡ 1)";

        var tmin = plot.Add.HorizontalLine(p.FlangeThickMinMm);
        tmin.Color = new Color(120, 120, 120);
        tmin.LineWidth = 1.3f; tmin.LinePattern = LinePattern.Dotted;
        tmin.LegendText = $"最小可制造 {p.FlangeThickMinMm:0.0} mm";

        plot.Title($"法兰厚度分布   φ 内={fa.Phi[0]:0.00} 外={fa.Phi[^1]:0.00}   " +
                   $"r_o,max = {fa.ROMaxMm:0.0} mm");
        plot.XLabel("半径 r [mm]");
        plot.YLabel("厚度 [mm]");
        StyleLegend(plot);
        plot.Axes.AutoScale();
        fp.Refresh();
    }

    private static void StyleLegend(Plot plot)
    {
        plot.Legend.IsVisible = true;
        plot.Legend.FontName = "Microsoft YaHei";
        plot.Legend.Alignment = Alignment.UpperRight;
    }

    private static double[] Scale(double[] v, double k)
    {
        var o = new double[v.Length];
        for (int i = 0; i < v.Length; i++) o[i] = v[i] * k;
        return o;
    }

    // ---------------------------------------------------------------
    // 省铂曲线：铂重 vs 管壁厚度，标出两条约束边界
    // ---------------------------------------------------------------

    /// <summary>
    /// 单纵轴（铂重）。约束不另设坐标轴——按 dataviz 规则不用双轴，
    /// 改用竖直边界线 + 阴影标出不可行区。
    /// </summary>
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
        StyleLegend(plot);
        plot.Axes.SetLimits(xLo, xHi, yLo, yHi);
        fp.Plot.SavePng(path, 1150, 640);
    }
}