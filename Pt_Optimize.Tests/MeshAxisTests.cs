using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 A+E（2026-09-13）：网格轴**从管轴中心向外铺、两侧镜像**的门（工单 §3「轴与图幅无关」「轴对称」「两条路对得上」的网格部分）。
///
/// 病（隔离实验 D:\WinForm\Pt_Topo\deliverable\网格隔离_meshcmp2_片1_2026-09-13.txt）：
///   · 图纸路径的轴从图幅（含 1～2 mm 留白）起铺，舌片直边落在格子中间 ⇒ 抽热 26.4 W（解析 3.41 W）；
///   · 两条路的轴都从端点起步、hPrev = hCoarse ⇒ z 轴 −28 起前六格 8.46→2.28 mm，孔周上下不对称。
/// </summary>
public class MeshAxisTests
{
    /// <summary>盘Ø56 记录的共用片（片 1），管孔按整线口径（内径/2 + 管壁）。</summary>
    internal static FlangePlate Disc56Plate1(out LineCase lc)
    {
        var p = new DesignInputs();
        var d = R47NavGridInstrumentTests.Disc56TwoSegs();
        lc = d.BuildCase(p);
        var g = d.Plate(1, d.DiscFloorMm(p));
        g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        return g;
    }

    [Fact]
    public void z轴节点关于0对称()
    {
        var bands = new[] { new FlangeMesher.Band(-50, 50, 2.0), new FlangeMesher.Band(-31, 31, 1.0) };
        double[] zs = FlangeMesher.GradedAxisCentered(-28, 28, bands, 11.0);
        int n = zs.Length;
        Assert.True(n >= 3);
        Assert.Equal(-28.0, zs[0]); Assert.Equal(28.0, zs[^1]);
        for (int i = 0; i < n; i++)
            Assert.Equal(-zs[n - 1 - i], zs[i], 9);
        Assert.Contains(zs, z => z == 0.0);
        for (int i = 1; i < n; i++) Assert.True(zs[i] > zs[i - 1], "轴必须单调");

        // 整张解析网格的节点 z 也对称
        var g = Disc56Plate1(out var lc);
        var m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
        var zSet = m.Nodes.Select(v => Math.Round(v.Z, 9)).Distinct().OrderBy(z => z).ToArray();
        for (int i = 0; i < zSet.Length; i++)
            Assert.Equal(-zSet[zSet.Length - 1 - i], zSet[i], 9);
    }

    [Fact]
    public void 孔周第一圈格两侧同尺寸_起步就是细步()
    {
        var bands = new[] { new FlangeMesher.Band(-50, 50, 2.0) };
        double[] zs = FlangeMesher.GradedAxisCentered(-28, 28, bands, 11.0);
        int k = Array.IndexOf(zs, 0.0);
        Assert.True(k > 0 && k < zs.Length - 1);
        double up = zs[k + 1] - zs[k], dn = zs[k] - zs[k - 1];
        Assert.Equal(up, dn, 9);
        Assert.Equal(2.0, up, 9);        // 中心在细带里 ⇒ 第一格就是 hFine，不再被 hCoarse/growth 顶成 8.46
        // 老路（从端点起铺）的病：第一格 = hCoarse/1.3 = 8.46 —— 这里记下来，作为对照
        double[] old = FlangeMesher.GradedAxis(-28, 28, bands, 11.0);
        Assert.True(old[1] - old[0] > 8.0, $"老轴第一格 {old[1] - old[0]:0.00} mm（病的形态）");
    }

    [Fact]
    public void 图纸路径的轴与图幅留白无关_留白0123节点逐位相同()
    {
        var g = Disc56Plate1(out var lc);
        ShellMesh? first = null;
        foreach (double margin in new[] { 0.0, 1.0, 2.0, 3.0 })
        {
            var f = AnalyticSurrogate.Rasterize(g, 0.25, margin);
            Assert.True(f.HasExactEnvelope, "Rasterize 必须填精确材料包络");
            var m = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
            if (first is null) { first = m; continue; }
            Assert.Equal(first.Nodes.Count, m.Nodes.Count);
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                Assert.Equal(first.Nodes[i].X, m.Nodes[i].X);
                Assert.Equal(first.Nodes[i].Z, m.Nodes[i].Z);
            }
            Assert.Equal(first.CellCount, m.CellCount);
            for (int i = 0; i < m.CellCount; i++)
            {
                Assert.Equal(first.Centroid[i].X, m.Centroid[i].X);
                Assert.Equal(first.Centroid[i].Z, m.Centroid[i].Z);
            }
        }
    }

    [Fact]
    public void 同一解析板_Build与BuildFromField的管孔边界面数与总长逐位相同()
    {
        var g = Disc56Plate1(out var lc);
        var mA = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
        var f = AnalyticSurrogate.Rasterize(g, 0.25, 2.0);
        var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);

        // 轴一样 ⇒ 节点一样
        Assert.Equal(mA.Nodes.Count, mF.Nodes.Count);
        for (int i = 0; i < mA.Nodes.Count; i++)
        { Assert.Equal(mA.Nodes[i].X, mF.Nodes[i].X); Assert.Equal(mA.Nodes[i].Z, mF.Nodes[i].Z); }

        var hA = mA.Faces.Where(x => x.B < 0 && x.Tag == ShellMesh.TagHole).ToArray();
        var hF = mF.Faces.Where(x => x.B < 0 && x.Tag == ShellMesh.TagHole).ToArray();
        Assert.Equal(hA.Length, hF.Length);
        Assert.Equal(hA.Sum(x => x.Length), hF.Sum(x => x.Length), 9);
        Assert.Equal(mA.CellCount, mF.CellCount);
        // 自检字段也填了（R47 F）：盘 R28／孔 25.8 ⇒ 定温环吃到盘外缘 2.2 mm，两条路同数
        Assert.False(double.IsNaN(mA.HoleTagMaxROverMm));
        Assert.Equal(mA.HoleTagMaxROverMm, mF.HoleTagMaxROverMm, 9);
        Assert.Equal(g.HoleRadiusMm, mA.HoleRadiusMm);
    }

    [Fact]
    public void 没有精确包络时退回栅格包络并写进警告()
    {
        var g = Disc56Plate1(out var lc);
        var f = AnalyticSurrogate.Rasterize(g, 0.25, 2.0);
        f.XMinMaterial = f.XMaxMaterial = f.ZMinMaterial = f.ZMaxMaterial = double.NaN;   // 模拟旧版子进程／Pt_Topo 的场
        var env = f.MaterialEnvelope();
        Assert.False(env.Exact);
        Assert.Equal(g.TabTipXMm, env.XMin, 6);       // 栅格恰好落在整数格上时与精确值同
        Assert.Contains("材料包络取自栅格", f.Warning);
        var m = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
        Assert.True(m.CellCount > 100);
    }

    [Fact]
    public void 内带参数进了图纸路径_格数随内带加密递增()
    {
        var g = Disc56Plate1(out var lc);
        var f = AnalyticSurrogate.Rasterize(g, 0.25, 2.0);
        double innerR = MeshAdapt.InnerRadiusFor(g.HoleRadiusMm, g.WeldFilletLegMm);
        int c0 = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, 2.0, 11.0, 50.0, 40.0).CellCount;
        int c1 = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, 2.0, 11.0, 50.0, 40.0, 1.0, innerR).CellCount;
        int c2 = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, 2.0, 11.0, 50.0, 40.0, 0.5, innerR).CellCount;
        Assert.True(c1 > c0 && c2 > c1, $"格数 {c0} → {c1} → {c2} 应递增");
    }

    // ── R47 复修 M1（2026-09-13）：轴锚点 —— 舌半宽 < 盘半径时 z=±w 与切点 x 必须落成节点。
    //   审查实测（改前）：盘Ø56 片 1 舌半宽 25／22.7 的导航网格→×0.25 抽热 −29.1→−57.5／−63.7→−97.3 W（直边落在格子中间）。

    /// <summary>盘Ø56 片 1，舌端半宽改成给定值（等宽舌，切点 x = −√(R²−w²)）。</summary>
    internal static FlangePlate Disc56Plate1HalfWidth(double halfW, out LineCase lc)
    {
        var g = Disc56Plate1(out lc);
        g.TabEndHalfWidthMm = halfW; g.TabParallel = true;
        return g;
    }

    [Fact]
    public void 轴锚点_必须落成节点_单调_对称锚点给对称轴()
    {
        var bands = new[] { new FlangeMesher.Band(-50, 50, 2.0) };
        double[] zs = FlangeMesher.GradedAxisCentered(-28, 28, bands, 11.0, anchors: new[] { 23.0, -23.0, 17.3, -17.3 });
        Assert.Contains(zs, z => Math.Abs(z - 23.0) < 1e-9);
        Assert.Contains(zs, z => Math.Abs(z + 23.0) < 1e-9);
        Assert.Contains(zs, z => Math.Abs(z - 17.3) < 1e-9);
        for (int i = 1; i < zs.Length; i++) Assert.True(zs[i] > zs[i - 1], "轴必须单调");
        int n = zs.Length;
        for (int i = 0; i < n; i++) Assert.Equal(-zs[n - 1 - i], zs[i], 9);
        // 区间外／端点上／中心上的锚点忽略，不炸也不重复
        double[] xs = FlangeMesher.GradedAxisCentered(-140, 28, bands, 11.0, anchors: new[] { -200.0, 28.0, 0.0, double.NaN, -15.97 });
        Assert.Equal(xs.Length, xs.Distinct().Count());
        Assert.Contains(xs, x => Math.Abs(x + 15.97) < 1e-9);
        // 锚点之后按原本想要的步长继续：锚点前那一格可以短，后面不跟着变小
        int k = Array.FindIndex(zs, z => Math.Abs(z - 17.3) < 1e-9);
        Assert.True(zs[k + 1] - zs[k] > 1.0, $"锚点后第一格 {zs[k + 1] - zs[k]:0.00} mm 被短格拖小了");
    }

    [Fact]
    public void 舌半宽等于盘半径减5的板_Build的z节点含正负半宽_x节点含切点与舌尖()
    {
        var g = Disc56Plate1HalfWidth(23.0, out var lc);          // 盘 R28、舌半宽 23 = R−5
        Assert.Equal(-Math.Sqrt(28 * 28 - 23 * 23), g.Tangent().X, 9);
        var m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
        var zSet = m.Nodes.Select(v => v.Z).Distinct().ToArray();
        var xSet = m.Nodes.Select(v => v.X).Distinct().ToArray();
        Assert.Contains(zSet, z => Math.Abs(z - 23.0) < 1e-9);
        Assert.Contains(zSet, z => Math.Abs(z + 23.0) < 1e-9);
        Assert.Contains(xSet, x => Math.Abs(x - g.Tangent().X) < 1e-9);
        Assert.Contains(xSet, x => Math.Abs(x - g.TabTipXMm) < 1e-9);
        // 直边落在节点上 ⇒ 舌片段（切点左侧）没有部分覆盖的格：覆盖面积 = 整格面积
        for (int i = 0; i < m.CellCount; i++)
        {
            var c = m.Cells[i];
            double x0 = m.Nodes[c[0]].X, x1 = m.Nodes[c[1]].X, z0 = m.Nodes[c[0]].Z, z1 = m.Nodes[c[2]].Z;
            if (x1 <= Math.Min(g.Tangent().X, -g.HoleRadiusMm) + 1e-9 && x0 >= g.TabTipXMm - 1e-9)   // 管孔（R25.8）伸进切点（−16）左侧，孔边那几格不算
                Assert.Equal((x1 - x0) * (z1 - z0), m.Area[i], 6);
        }
        // 图纸路径（没传锚点）从厚度场自己推出同一组锚点：z ±23、x 切点
        var f = AnalyticSurrogate.Rasterize(g, 0.5, 2.0);
        var (xa, za) = FlangeMesher.AnchorsFromField(f);
        Assert.Contains(za, z => Math.Abs(z - 23.0) < 1e-9);
        Assert.Contains(xa, x => Math.Abs(x - g.Tangent().X) < 1e-6);
        var mF = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
        Assert.Contains(mF.Nodes.Select(v => v.Z), z => Math.Abs(z - 23.0) < 1e-9);
        Assert.Contains(mF.Nodes.Select(v => v.X), x => Math.Abs(x - g.Tangent().X) < 1e-6);
    }

    // ── R47 第三轮 N1（2026-09-13）：舌半宽从**直边段**推，不从舌尖第一列推。
    //   审查实测：舌尖切 3 mm 倒角的板，旧推法 w 22 vs 真 25，抽热差 20 W。

    /// <summary>在栅格场上把舌尖两角各切掉 chamfer mm 的 45° 倒角（材料 (x−tip)+(w−|z|) &lt; chamfer 的格清零）。</summary>
    internal static ThicknessField ChamferTip(ThicknessField f, double tipX, double halfW, double chamfer)
    {
        var t = (double[])f.T.Clone();
        for (int i = 0; i < f.Nx; i++)
            for (int k = 0; k < f.Nz; k++)
            {
                double x = f.X0 + i * f.Step, z = f.Z0 + k * f.Step;
                if ((x - tipX) + (halfW - Math.Abs(z)) < chamfer - 1e-9) t[i * f.Nz + k] = 0;
            }
        return f.WithThickness(t);
    }

    /// <summary>旧推法（舌尖第一列有料的半宽）—— 只在这里复刻，作对照，不许回到生产代码。</summary>
    private static (double w, double xT) FirstColumnInference(ThicknessField f)
    {
        double R = 0, w = double.NaN;
        for (int i = 0; i < f.Nx; i++)
        {
            double h = double.NaN;
            for (int k = 0; k < f.Nz; k++)
                if (f.T[i * f.Nz + k] > 1e-9) { double z = Math.Abs(f.Z0 + k * f.Step); if (double.IsNaN(h) || z > h) h = z; }
            if (double.IsNaN(h)) continue;
            if (double.IsNaN(w)) w = h;
            if (h > R) R = h;
        }
        return (w, w >= R - 1e-9 ? 0 : -Math.Sqrt(R * R - w * w));
    }

    /// <summary>
    /// 盘 R28／舌半宽 25 的板栅格化后在舌尖切 3 mm 倒角：AnchorsFromField 要给 w = 25、xT = −√(28²−25²) = −12.61（±0.5 栅格步）；
    /// 旧推法给 22（对照）。再在解析等价板上比抽热（同一输入）：直边段推法的图纸路径 vs Build 差 &lt; 1 W；
    /// 旧推法的锚点（±22）造出来的网格差多少一并印出来（审查实测 20 W）。
    /// </summary>
    [Fact]
    public void 舌尖倒角3mm的场_锚点从直边段推得w25与切点_抽热与解析板差小于1W()
    {
        var g = Disc56Plate1HalfWidth(25.0, out var lc);
        double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
        double hFinest = lc.MeshInnerMm > 1e-9 ? Math.Min(hF, lc.MeshInnerMm) : hF;
        double step = FlangeMesher.RasterStepFor(hFinest);
        var f0 = AnalyticSurrogate.Rasterize(g, step, 2.0);
        var f = ChamferTip(f0, g.TabTipXMm, 25.0, 3.0);
        Assert.True(f.T.Count(v => v > 1e-9) < f0.T.Count(v => v > 1e-9), "倒角得真的切掉料");

        var (wOld, xOld) = FirstColumnInference(f);
        Assert.True(wOld < 24.0, $"对照：旧推法本该把舌半宽取小（实测 {wOld:0.0}），否则这道门没有对象");
        Assert.True(FlangeMesher.TangentFromField(f, out double xT, out double w, out double R, out string how), how);
        Assert.Equal(25.0, w, 9);
        Assert.Equal(28.0, R, 6);
        double xTrue = -Math.Sqrt(28 * 28 - 25 * 25);
        Assert.True(Math.Abs(xT - xTrue) <= 0.5 * step + 1e-9, $"切点 {xT:0.000} vs 真 {xTrue:0.000}（栅格步 {step}）—— {how}");
        Assert.Contains("舌尖有倒角或收窄", how);
        var (xa, za) = FlangeMesher.AnchorsFromField(f);
        Assert.Contains(za, z => Math.Abs(z - 25.0) < 1e-9);
        Assert.Contains(za, z => Math.Abs(z + 25.0) < 1e-9);
        Assert.Contains(xa, x => Math.Abs(x - xTrue) <= 0.5 * step + 1e-9);

        // 抽热：解析板 Build vs 倒角场（直边段推法） vs 倒角场（旧推法锚点 ±22，对照）
        var mA = FlangeMesher.Build(g, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var mNew = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        var mOld = FlangeMesher.BuildFromField(f, g.HoleRadiusMm, 0, hF, hC, rF, cl, lc.MeshInnerMm, lc.MeshInnerRadiusMm,
                                               xAnchors: new[] { xOld }, zAnchors: new[] { wOld, -wOld });
        var a = DrawingPathParityTests.SolveOne(mA, lc, g, 1572.1, 1107.8, 1080, 450, 2.8);
        var n = DrawingPathParityTests.SolveOne(mNew, lc, g, 1572.1, 1107.8, 1080, 450, 2.8);
        var o = DrawingPathParityTests.SolveOne(mOld, lc, g, 1572.1, 1107.8, 1080, 450, 2.8);
        string line = $"R47 第三轮 N1 倒角实验（盘 R28／舌半宽 25／舌尖倒角 3 mm，栅格步 {step}）：旧推法 w={wOld:0.0} xT={xOld:0.00}；直边段推法 w={w:0.0} xT={xT:0.00}（真 {xTrue:0.00}）；" +
                      $"抽热 解析板 {a.QFromTube:0.000} W（{a.Cells} 格）／倒角场·直边段锚点 {n.QFromTube:0.000} W（{n.Cells} 格，差 {n.QFromTube - a.QFromTube:+0.000;-0.000;0.000}）／倒角场·旧锚点±{wOld:0} {o.QFromTube:0.000} W（{o.Cells} 格，差 {o.QFromTube - a.QFromTube:+0.000;-0.000;0.000}）";
        Console.WriteLine(line);
        System.IO.File.AppendAllText(System.IO.Path.Combine(HandoverDoc.Root(), "deliverable", "R47_第三轮N1_倒角实验_2026-09-13.txt"),
                                     $"{DateTime.Now:yyyy-MM-dd HH:mm}　{line}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
        Assert.True(Math.Abs(n.QFromTube - a.QFromTube) < 1.0, line);
    }

    /// <summary>
    /// R47 第三轮 N3：锚点离下一个计划节点不到 hWant/4 时把那个节点挪到锚点上，不造发丝格。
    /// 锚点 2.02（中心第一格计划节点 2.0 之后 0.02）与 −16.005（计划节点 −16 之后 0.005）：最小格 ≥ hWant/4 = 0.5，节点单调，锚点仍是节点。
    /// </summary>
    [Fact]
    public void 锚点离计划节点不到四分之一步_节点挪到锚点上_不造发丝格()
    {
        var bands = new[] { new FlangeMesher.Band(-50, 50, 2.0) };
        double[] zs = FlangeMesher.GradedAxisCentered(-28, 28, bands, 11.0, anchors: new[] { 2.02, -16.005 });
        Assert.Contains(zs, z => Math.Abs(z - 2.02) < 1e-9);
        Assert.Contains(zs, z => Math.Abs(z + 16.005) < 1e-9);
        double minCell = double.MaxValue;
        for (int i = 1; i < zs.Length; i++)
        {
            Assert.True(zs[i] > zs[i - 1], "轴必须单调");
            minCell = Math.Min(minCell, zs[i] - zs[i - 1]);
        }
        Console.WriteLine($"R47 第三轮 N3：锚点 2.02／−16.005，最小格 {minCell:0.000} mm（hWant/4 = 0.5）");
        Assert.True(minCell >= 0.5 - 1e-9, $"最小格 {minCell:0.000} < hWant/4 = 0.5（发丝格）");
        // 反证：改前的做法（先放 2.0 再收到 2.02）会给 0.02 的格 —— 这里钉的是「不再有」
        Assert.DoesNotContain(zs, z => Math.Abs(z - 2.0) < 1e-9);
        Assert.DoesNotContain(zs, z => Math.Abs(z + 16.0) < 1e-9);
    }

    /// <summary>
    /// M1 的数：舌半宽 25 与 22.7 的盘Ø56 片 1，导航网格→×0.25 的抽热（审查实测改前 −29.1→−57.5、−63.7→−97.3 W）。
    /// 只量、写文件，不判（判的是上面两条快门与 DrawingPathParityTests）。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 量舌半宽25与22p7的盘56片1导航网格与加密抽热()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"R47 复修 M1 轴锚点：盘Ø56 片 1 舌半宽 25／22.7，导航网格（×1）与 ×0.25 的抽热　{DateTime.Now:yyyy-MM-dd HH:mm}（审查实测改前：25 → −29.1/−57.5 W；22.7 → −63.7/−97.3 W）");
        foreach (double w in new[] { 25.0, 22.7 })
        {
            var g = Disc56Plate1HalfWidth(w, out var lc);
            foreach (double k in new[] { 1.0, 0.25 })
            {
                var m = FlangeMesher.Build(g, 0, lc.MeshFineMm * k, lc.MeshCoarseMm * k, lc.MeshFineRadiusMm, lc.Base.BusbarClampLengthMm);
                var (qGen, qTube, cells) = DrawingPathParityTests.SolveOne(m, lc, g, 1572.1, 1107.8, 1080, 450, 2.8);
                string line = $"舌半宽 {w}　倍率 {k}：{cells} 格　发热 {qGen:0.0} W　抽热 {qTube:0.000} W";
                Console.WriteLine(line); sb.AppendLine(line);
            }
        }
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        System.IO.File.WriteAllText(DeliverableOut.Stamped("R47_复修M1_舌半宽锚点_2026-09-13.txt"), sb.ToString(), new System.Text.UTF8Encoding(false));
    }
}
