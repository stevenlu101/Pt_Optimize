using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// R48 诊断（2026-09-13，Opus 5 写）：**中带 1.0 / 0.5 / 0.25 三档网格的结构对比**。
///
/// 为什么要它（实测逼出来的）：同一个设计、同一份输入，只把中带从 1.0 减到 0.5 再减到 0.25，
/// 逐片焦耳热是 489／**458**／488、743／**696**／741、606／**568**／604、327／**307**／326 W ——
/// **1.0 与 0.25 几乎一致，偏偏中间那档 0.5 低 6～7 %**。收敛序列不会长这样（收敛是单调逼近，
/// 不是中间掉下去再回来）⇒ 中带 0.5 这一档的**网格结构**有问题，不是场解不收敛。
///
/// 这件事要紧在：APP 的加密复算用「中带减半再算一次」当收敛判据（MeshVerify 的中带确认）。
/// 如果减半那一档的网格本身异常，这道门就会**永远判不过** —— 两个内置档的中带确认都没过
/// （管孔净流入 +6.1／+5.9 W、法兰增量温降 +10.5／+11.8 K），很可能就是这个原因。
///
/// 本诊断不解场，只建网格、量结构：节点数、孔周第一圈格尺寸、舌片中段格尺寸、
/// 最大最小格、长宽比、锚点是否落在节点上。秒级。
/// </summary>
public class R48MeshStructureTests
{
    private readonly ITestOutputHelper _out;
    public R48MeshStructureTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 对比中带三档的网格结构()
    {
        var sb = new StringBuilder();
        void Say(string s) { _out.WriteLine(s); sb.AppendLine(s); }
        Say("R48 网格结构对比（Opus 5）：同一片，只换中带尺寸，内带钉在 0.25");
        Say("");

        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.40 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        var p = new DesignInputs();
        d.SizeTongues(p);
        int plate = 1;                                     // 片1：发热最大（743 W），差异最明显
        var lc = d.BuildCase(p, checkRamp: false);
        var g = d.Plate(plate, d.DiscFloorMm(p));
        double holeR = lc.TubeIdMm * 0.5 + lc.WallMm;
        g.HoleRadiusMm = holeR;
        double innerR = MeshAdapt.InnerRadiusFor(g.HoleRadiusMm, Math.Max(d.TabThickMm.Max(), d.WallMm));

        Say($"板：盘 R{g.DiscRadiusMm:0} 孔 R{holeR:0.0} 环宽 {g.DiscRadiusMm - holeR:0.0}　舌半宽 {g.TabEndHalfWidthMm:0}"
          + $" 舌尖 x {g.TabTipXMm:0} 切点 x {g.Tangent().X:0.0}　细化半径 {lc.MeshFineRadiusMm:0} 内带半径 {innerR:0.0} 内带 0.25 粗区 {lc.MeshCoarseMm:0}");
        var (xa, za) = FlangeMesher.AnchorsOf(g);
        Say($"锚点：x {string.Join("/", xa.Select(v => v.ToString("0.##")))}　z {string.Join("/", za.Select(v => v.ToString("0.##")))}");
        Say("");

        foreach (double hMid in new[] { 1.0, 0.5, 0.25 })
        {
            var tf = FlangeMesher.Rasterize(g, Math.Max(0.25 / 4, 0.02));
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本诊断显式钉回改动前口径，
            //   deliverable/R48_网格结构对比_2026-09-13.txt 每次快套件重写都不换口径（细带会让格数多 13～14 %，那是另一张表，要用新文件名）
            var m = FlangeMesher.BuildFromField(tf, holeR, 0, hMid, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                                lc.Base.BusbarClampLengthMm, 0.25, innerR, g.TwoTabs, xa, za,
                                                clampBandMm: 0, clampFullFace: false);
            // 量：孔周第一圈格、舌片中段格、全局最大最小
            double xTabMid = -0.5 * (g.DiscRadiusMm + Math.Abs(g.TabTipXMm));
            var near = Enumerable.Range(0, m.CellCount)
                .Where(i => Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z) < holeR + 1.0).ToArray();
            var tabMid = Enumerable.Range(0, m.CellCount)
                .Where(i => Math.Abs(m.Centroid[i].X - xTabMid) < 5 && Math.Abs(m.Centroid[i].Z) < 5).ToArray();
            var disc = Enumerable.Range(0, m.CellCount)
                .Where(i => m.Centroid[i].X > 5 && Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z) < g.DiscRadiusMm).ToArray();
            double Side(int i) => Math.Sqrt(m.Area[i]);
            string Stat(int[] ids) => ids.Length == 0 ? "（无）"
                : $"{ids.Length,5} 格　格边 {ids.Min(Side):0.000}～{ids.Max(Side):0.000}　面积和 {ids.Sum(i => m.Area[i]):0}";
            Say($"── 中带 {hMid:0.00}：{m.CellCount} 格　总面积 {m.Area.Sum():0.0} mm²　总体积 {m.VolumeMm3:0.0} mm³");
            Say($"   孔周一圈：{Stat(near)}");
            Say($"   舌片中段：{Stat(tabMid)}");
            Say($"   圆盘(+x)：{Stat(disc)}");
            // 轴节点：从网格节点里把去重后的 x、z 拿出来
            var xs = m.Nodes.Select(v => Math.Round(v.X, 6)).Distinct().OrderBy(v => v).ToArray();
            var zs = m.Nodes.Select(v => Math.Round(v.Z, 6)).Distinct().OrderBy(v => v).ToArray();
            double[] Gaps(double[] a) => Enumerable.Range(0, a.Length - 1).Select(i => a[i + 1] - a[i]).ToArray();
            var gx = Gaps(xs); var gz = Gaps(zs);
            Say($"   x 轴 {xs.Length} 节点　步长 {gx.Min():0.000}～{gx.Max():0.000}；z 轴 {zs.Length} 节点　步长 {gz.Min():0.000}～{gz.Max():0.000}");
            // 锚点是否落在节点上
            string Miss(double[] anchors, double[] nodes) => string.Join("、", anchors
                .Where(a => nodes.All(n => Math.Abs(n - a) > 1e-6)).Select(a => a.ToString("0.###")));
            string mx = Miss(xa, xs), mz = Miss(za, zs);
            Say($"   锚点落空：x [{(mx.Length == 0 ? "无" : mx)}]　z [{(mz.Length == 0 ? "无" : mz)}]");
            // 舌片那条直边（z = ±舌半宽）两侧各有几格：直边落在节点上时，两侧应当对称
            double w = g.TabEndHalfWidthMm;
            int inner = zs.Count(v => v > -w + 1e-9 && v < w - 1e-9), outer = zs.Count(v => Math.Abs(v) > w + 1e-9);
            Say($"   z 节点：|z| < 舌半宽 {inner} 个，|z| > 舌半宽 {outer} 个");
            Say("");
        }
        Say("读法：三档之间除了格子变细，结构（孔周圈数、舌片段格边、锚点落点）应当一致。");
        Say("      哪一项在中带 0.5 这一档突变，就是它把焦耳热带偏了 6～7 %。");

        File.WriteAllText(Path.Combine(SolutionDir(), "deliverable", "R48_网格结构对比_2026-09-13.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
