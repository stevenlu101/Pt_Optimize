using System;
using System.IO;
using PtOptimize.Core;
using ScottPlot;
using Color = ScottPlot.Color;

namespace PtOptimize.UI;

/// <summary>
/// 论文用示意图。按 Pt_Heater.3dm 的真实几何比例绘制，
/// 仅在必要处做局部放大，并在图注中注明。
/// </summary>
public static class Schematics
{
    private static readonly Color Ink = new(40, 40, 40);
    private static readonly Color Metal = new(120, 130, 145);
    private static readonly Color Glass = new(60, 160, 120);
    private static readonly Color Hot = new(210, 70, 50);
    private static readonly Color Cold = new(50, 100, 200);
    private static readonly Color Insul = new(230, 180, 90);

    private static Plot New(string title, string xl, string yl)
    {
        var p = new Plot();
        p.Axes.Bottom.Label.FontName = "Microsoft YaHei";
        p.Axes.Left.Label.FontName = "Microsoft YaHei";
        p.Axes.Title.Label.FontName = "Microsoft YaHei";
        p.Title(title);
        p.XLabel(xl);
        p.YLabel(yl);
        return p;
    }

    private static void Line(Plot p, double[] x, double[] y, Color c, float w = 2f,
                             LinePattern pat = default, string legend = "")
    {
        var s = p.Add.Scatter(x, y);
        s.LineWidth = w;
        s.MarkerSize = 0;
        s.Color = c;
        s.LinePattern = pat;
        s.LegendText = legend;
    }

    private static void Box(Plot p, double x0, double x1, double y0, double y1,
                            Color fill, double alpha, string label)
    {
        var r = p.Add.Rectangle(x0, x1, y0, y1);
        r.FillColor = fill.WithAlpha(alpha);
        r.LineColor = fill;
        r.LineWidth = 1.4f;
        if (label.Length > 0) Txt(p, label, (x0 + x1) / 2, (y0 + y1) / 2, 11);
    }

    private static void Txt(Plot p, string s, double x, double y, float size,
                            Alignment a = Alignment.MiddleCenter, Color? c = null)
    {
        var t = p.Add.Text(s, x, y);
        t.LabelFontSize = size;
        t.LabelFontName = "Microsoft YaHei";
        t.LabelAlignment = a;
        t.LabelFontColor = c ?? Ink;
    }

    private static (double[] xs, double[] up, double[] dn) Outline(FlangePlate g, int n = 400)
    {
        var xs = new double[n];
        var up = new double[n];
        var dn = new double[n];
        for (int i = 0; i < n; i++)
        {
            double x = g.TabTipXMm + (g.DiscRadiusMm - g.TabTipXMm) * i / (n - 1.0);
            xs[i] = x;
            up[i] = g.HalfWidth(x);
            dn[i] = -up[i];
        }
        return (xs, up, dn);
    }

    /// <summary>图 1：几何总览</summary>
    public static void SystemOverview(string path)
    {
        var g = new FlangePlate();
        var p = New("图 1  铂金直接加热单元几何（Pt_Heater.3dm 实测尺寸，1:1）",
                    "轴向 X [mm]", "横向 Z [mm]");
        var (xs, up, dn) = Outline(g);
        Line(p, xs, up, Metal, 2.2f);
        Line(p, xs, dn, Metal, 2.2f);

        var hx = new double[241];
        var hy = new double[241];
        for (int i = 0; i <= 240; i++)
        {
            double a = 2 * Math.PI * i / 240.0;
            hx[i] = g.HoleRadiusMm * Math.Cos(a);
            hy[i] = g.HoleRadiusMm * Math.Sin(a);
        }
        Line(p, hx, hy, Glass, 2.2f);

        Line(p, new[] { g.InsulBoundaryXMm, g.InsulBoundaryXMm }, new[] { -70.0, 70 },
             Insul, 2f, LinePattern.Dashed, "保温分界 X = −200（全包）");

        Box(p, g.TabTipXMm - 14, g.TabTipXMm, -g.TabEndHalfWidthMm, g.TabEndHalfWidthMm,
            Cold, 0.35, "铜排夹");

        Txt(p, "管孔 Ø52", 0, 0, 10, Alignment.MiddleCenter, Glass);
        Txt(p, "圆盘 Ø120", 44, 47, 10);
        Txt(p, "舌片（梯形）末端宽 80", -130, 26, 10);
        Txt(p, "→ 电流", -178, 0, 12, Alignment.MiddleCenter, Hot);

        p.Legend.IsVisible = true;
        p.Legend.FontName = "Microsoft YaHei";
        p.Axes.SetLimits(-225, 80, -78, 78);
        p.SavePng(path, 1150, 560);
    }

    /// <summary>图 2：一维微元控制体</summary>
    public static void ControlVolume(string path)
    {
        var p = New("图 2  管段微元控制体 —— 一维稳态能量方程的推导依据", "轴向 x", "");
        p.Axes.Left.IsVisible = false;

        Box(p, 3, 7, 0, 2, Metal, 0.30, "");
        Line(p, new[] { 0.0, 10 }, new[] { 0.0, 0 }, Ink, 1.5f);
        Line(p, new[] { 0.0, 10 }, new[] { 2.0, 2 }, Ink, 1.5f);
        Line(p, new[] { 3.0, 3 }, new[] { 0.0, 2 }, Ink, 2f, LinePattern.Dashed);
        Line(p, new[] { 7.0, 7 }, new[] { 0.0, 2 }, Ink, 2f, LinePattern.Dashed);

        Txt(p, "dx", 5, -0.35, 12);
        Txt(p, "x", 3, -0.35, 11);
        Txt(p, "x + dx", 7, -0.35, 11);
        Txt(p, "导电截面积 A", 5, 1.0, 11);

        Txt(p, "Q(x) = −kA dT/dx", 1.3, 1.3, 10, Alignment.MiddleCenter, Cold);
        Txt(p, "→", 2.4, 1.0, 15, Alignment.MiddleCenter, Cold);
        Txt(p, "Q(x+dx)", 8.6, 1.3, 10, Alignment.MiddleCenter, Cold);
        Txt(p, "→", 7.6, 1.0, 15, Alignment.MiddleCenter, Cold);

        Txt(p, "焦耳生成  I²ρe(T)/A · dx", 5, 3.15, 11, Alignment.MiddleCenter, Hot);
        Txt(p, "↑", 5, 2.45, 15, Alignment.MiddleCenter, Hot);
        Txt(p, "外表面散失  q′loss(T) · dx", 5, 3.75, 11, Alignment.MiddleCenter, Insul);
        Txt(p, "↓", 5, -0.95, 15, Alignment.MiddleCenter, Glass);
        Txt(p, "传给玻璃  hg·Pi·(T − Tg) · dx", 5, -1.55, 11, Alignment.MiddleCenter, Glass);
        Txt(p, "流入 − 流出 + 生成 − 耗散 = 0", 5, 4.45, 13);

        p.Axes.SetLimits(-0.6, 10.6, -2.3, 5.1);
        p.SavePng(path, 1050, 560);
    }

    /// <summary>图 3：法兰电流路径</summary>
    public static void CurrentPath(string path)
    {
        var g = new FlangePlate();
        var p = New("图 3  法兰内电流路径 —— 全部电流由舌片单侧汇入管孔",
                    "轴向 X [mm]", "横向 Z [mm]");
        var (xs, up, dn) = Outline(g);
        Line(p, xs, up, Metal, 2f);
        Line(p, xs, dn, Metal, 2f);

        var hx = new double[241];
        var hy = new double[241];
        for (int i = 0; i <= 240; i++)
        {
            double a = 2 * Math.PI * i / 240.0;
            hx[i] = g.HoleRadiusMm * Math.Cos(a);
            hy[i] = g.HoleRadiusMm * Math.Sin(a);
        }
        Line(p, hx, hy, Glass, 2f);

        for (int k = -3; k <= 3; k++)
        {
            double z0 = k * 12.0;
            var lx = new double[60];
            var lz = new double[60];
            for (int i = 0; i < 60; i++)
            {
                double t = i / 59.0;
                lx[i] = g.TabTipXMm + (-g.HoleRadiusMm - g.TabTipXMm) * t;
                lz[i] = z0 * (1 - t * t * 0.82);
            }
            Line(p, lx, lz, Hot, 1.3f);
        }
        Txt(p, "J 最高点（孔迎流侧）", -56, 36, 10, Alignment.MiddleCenter, Hot);
        Txt(p, "等电位边（铜排压接）", -186, 54, 10, Alignment.MiddleCenter, Cold);
        Txt(p, "电流交给管壁", 34, 0, 10, Alignment.MiddleCenter, Glass);
        p.Axes.SetLimits(-225, 80, -70, 70);
        p.SavePng(path, 1150, 520);
    }

    /// <summary>图 4：多层保温径向结构</summary>
    public static void InsulationStack(string path)
    {
        var p = New("图 4  管段多层保温径向结构 —— 圆筒热阻串联", "半径 r [mm]", "");
        p.Axes.Left.IsVisible = false;
        double[] r = { 25, 26, 36, 41 };
        string[] nm = { "铂管壁 1.0", "高纯氧化铝纤维 10", "致密氧化铝套管 5" };
        Color[] cs = { Metal, Insul, new Color(160, 140, 120) };
        Box(p, 0, r[0], 0, 1, Glass, 0.20, "玻璃");
        for (int i = 0; i < 3; i++) Box(p, r[i], r[i + 1], 0, 1, cs[i], 0.30, nm[i]);
        for (int i = 0; i < r.Length; i++)
        {
            Line(p, new[] { r[i], r[i] }, new[] { 0.0, 1.15 }, Ink, 1f);
            Txt(p, $"r{i} = {r[i]:0}", r[i], 1.30, 10);
        }
        Txt(p, "R′th,i = ln(r_{i+1} / r_i) / (2π·k_i)    逐层串联相加", 22, 1.66, 12);
        Txt(p, "外表面：辐射 + 对流", 46, 0.5, 10, Alignment.MiddleLeft, Hot);
        p.Axes.SetLimits(-3, 64, -0.28, 1.90);
        p.SavePng(path, 1050, 460);
    }

    /// <summary>图 5：舌片梁模型</summary>
    public static void TabBeam(string path)
    {
        var g = new FlangePlate();
        var p = New("图 5  法兰舌片力学模型 —— 变宽度简支梁（铜排夹提供支承）",
                    "轴向 X [mm]", "");
        p.Axes.Left.IsVisible = false;
        var (xt, _) = g.Tangent();
        int n = 200;
        var xs = new double[n];
        var up = new double[n];
        var dn = new double[n];
        for (int i = 0; i < n; i++)
        {
            double x = g.TabTipXMm + (xt - g.TabTipXMm) * i / (n - 1.0);
            xs[i] = x;
            up[i] = g.HalfWidth(x) / 60.0;
            dn[i] = -up[i];
        }
        Line(p, xs, up, Metal, 2.2f);
        Line(p, xs, dn, Metal, 2.2f);

        foreach (double sx in new[] { g.TabTipXMm, xt })
        {
            Line(p, new[] { sx - 7, sx, sx + 7 }, new[] { -1.5, -1.12, -1.5 }, Cold, 2.5f);
        }
        Txt(p, "铜排夹支承", g.TabTipXMm + 4, -1.9, 10, Alignment.MiddleCenter, Cold);
        Txt(p, "圆盘支承", xt - 4, -1.9, 10, Alignment.MiddleCenter, Cold);

        for (double x = g.TabTipXMm + 10; x < xt - 6; x += 15)
        {
            Line(p, new[] { x, x }, new[] { 1.52, 1.16 }, Hot, 1.4f);
        }
        Txt(p, "自重 w(x) = ρPt·g·b(x)·t   （宽度沿程变 ⇒ 载荷与截面模量同时变）",
            (g.TabTipXMm + xt) / 2, 2.00, 11, Alignment.MiddleCenter, Hot);
        Txt(p, "σ(x) = M(x) / Z(x),   Z = b(x)·t² / 6", (g.TabTipXMm + xt) / 2, -2.5, 12);
        Txt(p, "利用率 = σ(x)·SF / σallow(T(x))   ← 温度沿程下降，须逐点扫描",
            (g.TabTipXMm + xt) / 2, -3.0, 11);
        p.Axes.SetLimits(-225, 20, -3.5, 2.5);
        p.SavePng(path, 1100, 520);
    }

    public static void All(string dir)
    {
        Directory.CreateDirectory(dir);
        SystemOverview(Path.Combine(dir, "fig01_geometry.png"));
        ControlVolume(Path.Combine(dir, "fig02_control_volume.png"));
        CurrentPath(Path.Combine(dir, "fig03_current_path.png"));
        InsulationStack(Path.Combine(dir, "fig04_insulation.png"));
        TabBeam(Path.Combine(dir, "fig05_tab_beam.png"));
    }
}
