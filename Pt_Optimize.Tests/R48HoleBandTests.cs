using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// R48 仪器（2026-09-13，Opus 5 写）：**管孔定温带的带宽 vs 抽热的网格收敛性**。
///
/// 嫌疑：<see cref="ShellMesh.HoleTagBandMm"/> 写死 3 mm，而被钉成管温（T = 管根、V = 0）的是
/// 「有孔边界面的那**一整格**」。网格 2 mm 时孔边只钉住一圈（定温区物理厚度约 2 mm），
/// 网格 0.5 mm 时 3 mm 带里有六圈格全被钉住（厚度约 3 mm）——**定温区域的物理尺寸随网格变**，
/// 从管子抽的热自然跟着变。真实的孔边界是法兰与管子焊在一起的那一圈**线**，
/// 定温区厚度应当随加密趋于零，而不是趋于 3 mm。
///
/// 本仪器：同一片、同一输入，网格倍率 1／0.5／0.25 各解一次，带宽分别取
///   ① 固定 3 mm（现行口径）
///   ② 1.5 × 孔周格尺寸（随网格走）
/// 看抽热在哪一种口径下收敛。只记录、不判定。
/// </summary>
[Trait("速度", "慢")]
public class R48HoleBandTests
{
    private readonly ITestOutputHelper _out;
    public R48HoleBandTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 量孔边定温带的带宽对抽热收敛性的影响()
    {
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say($"R48 孔边定温带　{DateTime.Now:yyyy-MM-dd HH:mm}　同一片同一输入，只换网格倍率与定温带口径");
        Say("");

        foreach (var (name, d, plate) in Cases())
        {
            var p = new DesignInputs();
            var lc = d.BuildCase(p, checkRamp: false);
            var g = d.Plate(plate, d.DiscFloorMm(p));
            double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
            g.HoleRadiusMm = holeR;
            // 管侧固定为该片的参考状态：先用导航网格解一次整线拿电流与管根温度
            var r0 = LineRunner.Run(d.BuildCase(p, checkRamp: false), null, default);
            Assert.True(r0.Ok, name + " 参考解不出来：" + r0.Message);
            var f0 = r0.Flanges[plate];
            double iA = f0.CurrentA, tRoot = f0.TRootC, tSet = d.SetpointC[Math.Min(plate, d.SetpointC.Length - 1)];
            double tabIns = plate < d.TabInsulMm.Length ? d.TabInsulMm[plate] : double.NaN;
            Say($"── {name}　片 {plate}：I {iA:0.0} A　管根 {tRoot:0.0} °C　控温 {tSet:0}　舌保温 {tabIns:0.0} mm"
              + $"；盘 R{g.DiscRadiusMm:0} 孔 R{holeR:0.0} 环宽 {g.DiscRadiusMm - holeR:0.0} mm");
            Say("  带宽口径        倍率   孔周格mm    单元数   定温格数   抽热W     发热W    散热W   最高温°C");

            foreach (var (bandName, bandOf) in new (string, Func<double, double>)[]
                     {
                         ("固定 3 mm（现行）", _ => 0),                 // 0 ⇒ 走 HoleTagBandMm
                         ("1.5×孔周格（随网格）", h => 1.5 * h),
                     })
            {
                double? prev = null;
                foreach (double scale in new[] { 1.0, 0.5, 0.25 })
                {
                    double hFine = lc.MeshFineMm * scale, hCoarse = lc.MeshCoarseMm * scale;
                    double hInner = lc.MeshInnerMm > 0 ? lc.MeshInnerMm * scale : 0;
                    double hHole = hInner > 0 ? hInner : hFine;          // 孔周实际格尺寸
                    var tf = FlangeMesher.Rasterize(g, Math.Max(hHole / 4, 0.05));
                    var m = FlangeMesher.BuildFromField(tf, holeR, 0, hFine, hCoarse, lc.MeshFineRadiusMm,
                                                        lc.Base.BusbarClampLengthMm, hInner, lc.MeshInnerRadiusMm,
                                                        g.TwoTabs, FlangeMesher.AnchorsOf(g).X, FlangeMesher.AnchorsOf(g).Z,
                                                        bandOf(hHole), clampBandMm: 0, clampFullFace: false);
                    // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
                    var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
                    var p2 = SegmentSolver.Clone(lc.Base);
                    p2.TSetC = tSet;
                    if (plate < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[plate];
                    var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved,
                                                symmetricInsul: false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabIns);
                    int fixedCells = Enumerable.Range(0, m.CellCount)
                        .Count(i => m.Faces.Any(f => f.B < 0 && f.Tag == ShellMesh.TagHole && f.A == i));
                    Say($"  {bandName,-18}{scale,5:0.00}  {hHole,9:0.000}  {m.CellCount,8}  {fixedCells,8}  {th.QFromTubeW,8:0.000}  {th.QGenW,8:0.0}  {th.QLossW,7:0.0}  {th.TMaxC,9:0.0}"
                      + (prev is { } pv ? $"　（较上一档 {th.QFromTubeW - pv:+0.000;-0.000}）" : ""));
                    prev = th.QFromTubeW;
                }
            }
            Say("");
        }
        Say("读法：抽热若在「随网格走」那一组里逐档收敛（相邻两档差越来越小），就说明写死 3 mm 的定温带是抽热不收敛的根源。");

        File.WriteAllText(Path.Combine(SolutionDir(), "deliverable", "R48_孔边定温带_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static (string, DesignSpec, int)[] Cases()
    {
        var w08 = DesignSpec.W08.Clone();
        var o56 = R48MidBandConvergenceTests.Disc56();
        return new[]
        {
            ("管壁 0.8 记录（环宽 4.2）", w08, 0),
            ("盘Ø56 记录（环宽 2.2）", o56, 1),
        };
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
