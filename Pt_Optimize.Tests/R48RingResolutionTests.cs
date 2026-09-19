using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// R48 仪器（2026-09-13，Opus 5 写）：**孔到盘外缘那条环上有几个格** vs 抽热收敛性。
///
/// 假设（要证伪的）：抽热不收敛的主因是**环没被网格解析开**。
///   盘Ø56：孔 R25.8、盘 R28 ⇒ 环宽只有 2.2 mm。导航网格 2 mm 时环上**只有一格** ——
///   那一格既贴着孔（被钉成管温 T = 管根、V = 0）又贴着盘外缘（自由散热边），
///   整条环被一个格代表，环内没有任何温度梯度可言；加密到 2 格、4 格时梯度才出现 ⇒ 抽热剧变。
///   管壁 0.8：孔 R25.8、盘 R30 ⇒ 环宽 4.2 mm，导航网格就有 2 格 ⇒ 结构一直在 ⇒ 实测收敛（±1 W）。
///
/// 判据：若把网格继续细到环上 8～16 格之后抽热稳下来（相邻两档差 &lt; 0.5 W），假设成立 ——
///   那么「算不准」是**几何与网格的匹配问题**（窄环要更细的网格），不是判据或方法的问题。
///   若继续加密仍然跳，假设被证伪，主因另找（下一个嫌疑：阶梯边界的人为凹角让电流密度峰发散）。
///
/// 单片、管侧固定为参考解，所以一档只要几十秒；孔边定温带用随网格走的口径（1.5×孔周格），
/// 免得窄环上 3 mm 固定带把盘外缘也钉成管孔（那是另一个已确认的缺陷）。
/// </summary>
[Trait("速度", "慢")]
public class R48RingResolutionTests
{
    private readonly ITestOutputHelper _out;
    public R48RingResolutionTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 量环上格数与抽热收敛性()
    {
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say($"R48 环的网格解析度　{DateTime.Now:yyyy-MM-dd HH:mm}　单片、管侧固定、孔边带随网格走");
        Say("");

        foreach (var (name, d, plate) in new (string, DesignSpec, int)[]
                 {
                     ("盘Ø56（环宽 2.2）", R48MidBandConvergenceTests.Disc56(), 1),
                     ("管壁 0.8 记录（环宽 4.2）", DesignSpec.W08.Clone(), 0),
                 })
        {
            var p = new DesignInputs();
            var lc = d.BuildCase(p, checkRamp: false);
            var g = d.Plate(plate, d.DiscFloorMm(p));
            double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
            g.HoleRadiusMm = holeR;
            double ring = g.DiscRadiusMm - holeR;
            var r0 = LineRunner.Run(d.BuildCase(p, checkRamp: false), null, default);
            Assert.True(r0.Ok, name + " 参考解不出来：" + r0.Message);
            var f0 = r0.Flanges[plate];
            double iA = f0.CurrentA, tRoot = f0.TRootC, tSet = d.SetpointC[Math.Min(plate, d.SetpointC.Length - 1)];
            double tabIns = plate < d.TabInsulMm.Length ? d.TabInsulMm[plate] : double.NaN;

            Say($"── {name}　片 {plate}：环宽 {ring:0.0} mm　I {iA:0.0} A　管根 {tRoot:0.0} °C　舌保温 {tabIns:0.0} mm");
            Say("   孔周格mm  环上格数   单元数   定温格数    抽热W     发热W    散热W   铜排W    J峰   最高温°C   较上一档");
            double? prev = null, prevFixed = null;
            foreach (double h in new[] { 2.0, 1.0, 0.5, 0.25, 0.125 })
            {
                double hCoarse = Math.Max(h, lc.MeshCoarseMm * h / lc.MeshFineMm);
                var tf = FlangeMesher.Rasterize(g, Math.Max(h / 4, 0.02));
                var (xa, za) = FlangeMesher.AnchorsOf(g);
                // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
                var m = FlangeMesher.BuildFromField(tf, holeR, 0, h, hCoarse, lc.MeshFineRadiusMm,
                                                    lc.Base.BusbarClampLengthMm, 0, 0, g.TwoTabs, xa, za, 1.5 * h,
                                                    clampBandMm: 0, clampFullFace: false);
                var sc = ShellCurrent.SolveFor(lc, m, iA, Materials.PtResistivity(tSet) * 1e3, tSet);
                var p2 = SegmentSolver.Clone(lc.Base);
                p2.TSetC = tSet;
                if (plate < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[plate];
                var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot, g.InsulBoundaryXResolved,
                                            symmetricInsul: false, tabBoundaryX: g.Tangent().X, tabInsulThickMm: tabIns);
                int fixedCells = Enumerable.Range(0, m.CellCount)
                    .Count(i => m.Faces.Any(f => f.B < 0 && f.Tag == ShellMesh.TagHole && f.A == i));
                // R48：抽热的「与网格无关」口径 = 现行值 + 那圈定温孔单元自身发热 − 自身散热
                double qFixed = th.QFromTubeW + th.QHoleCellGenW - th.QHoleCellLossW;
                Say($"   {h,8:0.000}  {ring / h,8:0.0}  {m.CellCount,8}  {fixedCells,8}  {th.QFromTubeW,9:0.000}  {th.QGenW,8:0.0}  {th.QLossW,7:0.0}  {th.QToClampW,6:0.0}  {sc.JMaxAPerMm2,6:0.0}  {th.TMaxC,8:0.0}"
                  + (prev is { } pv ? $"　{th.QFromTubeW - pv:+0.000;-0.000}" : "")
                  + $"　｜孔单元 {th.HoleCellCount} 格 {th.HoleCellAreaMm2:0} mm² 自身发热 {th.QHoleCellGenW:0.00} 散热 {th.QHoleCellLossW:0.00}"
                  + $"　⇒ 修正后抽热 {qFixed:0.000}" + (prevFixed is { } pf ? $"（{qFixed - pf:+0.000;-0.000}）" : ""));
                prev = th.QFromTubeW; prevFixed = qFixed;
            }
            Say("");
        }
        Say("读法：环上格数到 8～16 之后抽热若稳下来（相邻差 < 0.5 W），说明窄环要更细的网格才算得准；");
        Say("      若照样跳，主因不在环的解析度，转查阶梯边界（电流密度峰随加密发散）。");

        File.WriteAllText(Path.Combine(SolutionDir(), "deliverable", "R48_环的解析度_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
