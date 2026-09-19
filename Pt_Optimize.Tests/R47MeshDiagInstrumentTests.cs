using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// R47 诊断仪器（2026-09-13）：同一片、同一输入（Pt_Topo meshcmp 口径），逐项换网格因素，量出
/// 「解析 Build」与「图纸 BuildFromField」在导航网格上剩下的差来自哪一项（厚度取法／覆盖率／轴），
/// 以及精确几何生成器（4×4 子采样）与栅格积分生成器在同一套新轴上各自往哪收（R47 复修 M5：老轴那份已换成新轴上的精确几何）。记录写 deliverable/R47_网格诊断_2026-09-13.txt。
/// 只记事实，不判。
/// </summary>
public class R47MeshDiagInstrumentTests
{
    /// <summary>
    /// ★ R47 复修 M5：**精确几何生成器铺在新轴上** —— R47 之前的生成器本体（4×4 子采样点上判 Inside、形心取厚，逐字照旧），
    /// 轴换成 GradedAxisCentered + 几何锚点（与 Build 同一套轴）。它是 DrawingPathParityTests 的第三方参照：
    /// Build 现在 = BuildFromField(Rasterize)，两者逐位相同是构造保证，不能拿来互验。
    /// （改前这里叫 BuildOldAxis，铺的是从端点起铺的老轴，只供对照；老轴 −z 半边不加密，其数已无效。）
    /// </summary>
    internal static ShellMesh BuildExactOnNewAxis(FlangePlate g, double hFine, double hCoarse, double fineRadius, double clampLenMm,
                                                  double hInner = 0, double innerRadius = 0)
    {
        var m = new ShellMesh();
        var xBands = new List<FlangeMesher.Band> { new(-fineRadius, fineRadius, hFine) };
        var zBands = new List<FlangeMesher.Band> { new(-fineRadius, fineRadius, hFine) };
        if (hInner > 1e-9 && innerRadius > 1e-9)
        { xBands.Add(new(-innerRadius, innerRadius, hInner)); zBands.Add(new(-innerRadius, innerRadius, hInner)); }
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        double zMax = Math.Max(g.DiscRadiusMm, g.TabEndHalfWidthMm);
        if (g.ExtensionMm > 1e-9) zMax = Math.Max(zMax, g.ExtHalfWidthMm);
        double[] xs = FlangeMesher.GradedAxisCentered(g.TabTipXMm, g.TwoTabs ? -g.TabTipXMm : g.DiscRadiusMm, xBands, hCoarse, anchors: xa);
        double[] zs = FlangeMesher.GradedAxisCentered(-zMax, zMax, zBands, hCoarse, anchors: za);
        int nx = xs.Length, nz = zs.Length;
        var nodeId = new int[nx, nz];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            { nodeId[i, j] = m.Nodes.Count; m.Nodes.Add(new Vec3(xs[i], 0, zs[j])); }
        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < nz - 1; j++)
            {
                double x0 = xs[i], x1 = xs[i + 1], z0 = zs[j], z1 = zs[j + 1];
                int hit = 0; const int ns = 4;
                for (int a = 0; a < ns; a++)
                    for (int b = 0; b < ns; b++)
                        if (g.Inside(x0 + (a + 0.5) * (x1 - x0) / ns, z0 + (b + 0.5) * (z1 - z0) / ns)) hit++;
                double frac = hit / (double)(ns * ns);
                if (frac < 0.25) continue;
                double cxm = 0.5 * (x0 + x1), czm = 0.5 * (z0 + z1);
                m.Cells.Add(new[] { nodeId[i, j], nodeId[i + 1, j], nodeId[i + 1, j + 1], nodeId[i, j + 1] });
                m.Area.Add((x1 - x0) * (z1 - z0) * frac);
                m.Centroid.Add(new Vec3(cxm, 0, czm));
                m.Thickness.Add(g.ThicknessAt(cxm, czm));
                m.Part.Add(0);
            }
        m.BuildFaces(mid =>
        {
            if (FlangeMesher.IsHoleFace(mid, g.HoleRadiusMm)) return ShellMesh.TagHole;
            if (g.TwoTabs ? Math.Abs(mid.X) >= Math.Abs(g.TabTipXMm) - clampLenMm : mid.X <= g.TabTipXMm + clampLenMm) return ShellMesh.TagTabEnd;
            return ShellMesh.TagFree;
        });
        m.ComputeHoleTagDiagnostics(g.HoleRadiusMm);
        return m;
    }

    private static string Row(string name, ShellMesh m, LineCase lc, FlangePlate g, double iA, double tRoot, double tSet, double clampC, double tabInsul)
    {
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet; p2.BusbarClampTempC = clampC;
        var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
        var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved, false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabInsul);
        var bf = m.Faces.Where(f => f.B < 0).ToArray();
        int nHole = bf.Count(f => f.Tag == ShellMesh.TagHole); double lHole = bf.Where(f => f.Tag == ShellMesh.TagHole).Sum(f => f.Length);
        double lFree = bf.Where(f => f.Tag == ShellMesh.TagFree).Sum(f => f.Length);
        double mass = m.VolumeMm3 * Materials.PtDensity * 1e-6;
        double rMicro = th.QGenW / (iA * iA) * 1e6;
        return $"{name,-40}{m.CellCount,6}　发热 {th.QGenW,6:0.0}　散热 {th.QLossW,6:0.0}　铜排 {th.QToClampW,6:0.0}　抽热 {th.QFromTubeW,7:0.000}　最高温 {th.TMaxC,6:0.0}　孔面 {nHole,3}/{lHole,5:0.0}　自由边 {lFree,6:0.0}　面积 {m.TotalArea,7:0.0}　铂 {mass,5:0.0}　J峰 {sc.JMaxAPerMm2:0.00}　R {rMicro:0.0} µΩ　残差 {th.EnergyResidualW:0.0}　电{(sc.Converged ? "✓" : "✗")}热{(th.Converged ? "✓" : "✗")}";
    }

    /// <summary>把图纸网格的厚度换成解析板形心取厚（隔离「厚度取法」这一项）。</summary>
    private static ShellMesh WithCentroidThickness(ShellMesh m, FlangePlate g)
    {
        for (int i = 0; i < m.CellCount; i++) m.Thickness[i] = g.ThicknessAt(m.Centroid[i].X, m.Centroid[i].Z);
        return m;
    }

    [Trait("速度", "慢")]
    [Fact]
    public void 量两条路在导航网格上剩下的差与旧轴新轴的收敛()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"R47 网格诊断　{DateTime.Now:yyyy-MM-dd HH:mm}　同一片同一输入，逐项换网格因素。列：单元／发热／散热／铜排／抽热／最高温／孔面数/长／自由边长／面积／铂／J峰／R／残差");
        void Design(string title, DesignSpec d, int plate, double iA, double tRoot, double tSet, double clampC, double tabInsul)
        {
            var p = new DesignInputs();
            var lc = d.BuildCase(p);
            var g = d.Plate(plate, d.DiscFloorMm(p));
            g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
            double hF = lc.MeshFineMm, hC = lc.MeshCoarseMm, rF = lc.MeshFineRadiusMm, cl = lc.Base.BusbarClampLengthMm;
            sb.AppendLine($"── {title}　片 {plate}：I {iA} A　管根 {tRoot}　夹持 {clampC}　控温 {tSet}　舌保温 {tabInsul}；盘 R{g.DiscRadiusMm} 孔 R{g.HoleRadiusMm:0.###} 舌端 x {g.TabEndXMm} 半宽 {g.TabEndHalfWidthMm} 切点 x {g.Tangent().X:0.0} 焊脚 {g.WeldFilletLegMm:0.00}；导航 hFine {hF} hCoarse {hC} fineR {rF}");
            string R(string n, ShellMesh m) { var s = Row(n, m, lc, g, iA, tRoot, tSet, clampC, tabInsul); sb.AppendLine(s); return s; }
            foreach (double k in new[] { 1.0, 0.5, 0.25 })
            {
                double step = Math.Min(1.0, hF * k / 4.0);
                var fld = AnalyticSurrogate.Rasterize(g, step, 2.0);
                R($"倍率 {k}　新轴 精确几何（4×4 子采样）", BuildExactOnNewAxis(g, hF * k, hC * k, rF, cl));
                // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
                R($"倍率 {k}　新轴 解析 Build", FlangeMesher.Build(g, 0, hF * k, hC * k, rF, cl, clampBandMm: 0, clampFullFace: false));
                R($"倍率 {k}　新轴 图纸 BuildFromField 步 {step}", FlangeMesher.BuildFromField(fld, g.HoleRadiusMm, 0, hF * k, hC * k, rF, cl, clampBandMm: 0, clampFullFace: false));
                R($"倍率 {k}　新轴 图纸 + 形心取厚", WithCentroidThickness(FlangeMesher.BuildFromField(fld, g.HoleRadiusMm, 0, hF * k, hC * k, rF, cl, clampBandMm: 0, clampFullFace: false), g));
                if (k == 1.0)
                {
                    var fld2 = AnalyticSurrogate.Rasterize(g, 0.1, 2.0);
                    R($"倍率 {k}　新轴 图纸 步 0.1", FlangeMesher.BuildFromField(fld2, g.HoleRadiusMm, 0, hF * k, hC * k, rF, cl, clampBandMm: 0, clampFullFace: false));
                    R($"倍率 {k}　新轴 图纸 步 0.1 + 形心取厚", WithCentroidThickness(FlangeMesher.BuildFromField(fld2, g.HoleRadiusMm, 0, hF * k, hC * k, rF, cl, clampBandMm: 0, clampFullFace: false), g));
                }
            }
        }
        // 盘Ø56 记录 片 1（meshcmp 口径）
        Design("盘Ø56 记录（两段）", R47NavGridInstrumentTests.Disc56TwoSegs(), 1, 1572.1, 1107.8, 1080, 450, 2.8);
        // Builtin[0] 片 0（入口片；改前抽热 1.58 W、改后 7.87 W）：电流与管根取改前导航网格的数
        var d0 = DesignSpec.Builtin[0].Clone();
        Design("DesignSpec.Builtin[0]（三段）", d0, 0, 1213.7, 1146.4, 1150, d0.ClampTempC, d0.TabInsulMm[0]);

        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "R47_网格诊断_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
        Assert.True(true);
    }
}
