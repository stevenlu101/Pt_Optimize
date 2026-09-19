using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★ R48 压接细带要不要自相似（2026-09-14，Opus 5 写；规格出自常驻数值把关人第十轮）：
/// 误差预算（deliverable/R48_离散误差预算_2026-09-14.txt）里细带固定两侧各 3 mm：h=1 时各 3 格、h=0.25 时各 12 格、h=2 时只剩 1.5 格 —— **不是自相似**。
/// 细带组比值 0.46 而均匀组 0.48～0.49，外推极限差 0.05～0.08 W，嫌疑就是它。声明网格配方之前先定。
///
/// 设置：与误差预算同一工作点（电流 1213/1984/1817/1022 A，管根 1172.63/1151.19/1093.09/1056.80 °C，出处 R48_接头电流核对_2026-09-14.txt），
/// 单片、管侧固定，四片；三组：均匀、分级 + 固定 3 mm 细带、分级 + 两侧各 3·h 细带；h = 1 / 0.5 / 0.25。
///
/// 跑前写死的判读：
///   · 三档上「自相似细带 − 均匀」都 ≤ 0.05 W，且自相似细带组比值与均匀组相差 ≤ 0.02（四片都满足）⇒ **用自相似版本声明配方**；
///   · 否则保留固定 3 mm，把 0.08 W 算进 U，并注明只在 h ≤ 1 上验证过、不许用于 2 mm。
/// </summary>
[Trait("速度", "慢")]
public class R48ClampBandSelfSimilarTests
{
    private readonly ITestOutputHelper _out;
    public R48ClampBandSelfSimilarTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 压接细带固定3mm与自相似3h对照()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_压接细带自相似_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 压接细带 固定 3 mm vs 自相似 3·h（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　工作点同误差预算");
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        var (_, radius) = MeshVerify.RequiredMeshFor(d);
        double[] iJ = { 1213, 1984, 1817, 1022 };
        double[] tRoot = { 1172.63, 1151.19, 1093.09, 1056.80 };
        double[] hs = { 1.0, 0.5, 0.25 };
        string[] groups = { "均匀", "固定3mm", "自相似3h" };

        double Solve(int j, string grp, double h)
        {
            var lcH = d.BuildCase(p, checkRamp: false);
            var g = d.Plate(j, d.DiscFloorMm(p));
            g.HoleRadiusMm = lcH.TubeIdMm * 0.5 + lcH.WallMm;
            ShellMesh m;
            // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
            //   （本文件是 FlangeMesher.BuildFromField 配方 ④ 的依据，它量的是只钉外圈口径；整面接触下的组合验证另见 R48ClampRecipeImpactTests、新文件名）
            if (grp == "均匀") m = FlangeMesher.Build(g, 0, h, h, 1e6, lcH.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false);
            else
            {
                MeshAdapt.RefineWholeMesh(lcH, h, radius);
                m = FlangeMesher.Build(g, 0, lcH.MeshFineMm, lcH.MeshCoarseMm, lcH.MeshFineRadiusMm, lcH.Base.BusbarClampLengthMm,
                                       lcH.MeshInnerMm, lcH.MeshInnerRadiusMm, clampBandMm: grp == "固定3mm" ? 3.0 : 3.0 * h, clampFullFace: false);
            }
            double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
            var sc = ShellCurrent.SolveFor(lcH, m, iJ[j], Materials.PtResistivity(tSet) * 1e3, tSet);
            var p2 = SegmentSolver.Clone(lcH.Base); p2.TSetC = tSet;
            if (j < lcH.ClampTempC.Length) p2.BusbarClampTempC = lcH.ClampTempC[j];
            var th = ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tRoot[j], g.InsulBoundaryXResolved, g.TwoTabs,
                                        tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                        discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm);
            Assert.True(th.Converged, $"片{j} {grp} h={h} 热场没收敛");
            return th.QFromTubeW;
        }

        bool selfOk = true;
        for (int j = 0; j < 4; j++)
        {
            var Q = new Dictionary<(string, double), double>();
            foreach (var grp in groups) foreach (double h in hs) Q[(grp, h)] = Solve(j, grp, h);
            double Ratio(string grp) => (Q[(grp, 0.25)] - Q[(grp, 0.5)]) / (Q[(grp, 0.5)] - Q[(grp, 1.0)]);
            Say($"── 片{j}");
            foreach (var grp in groups)
                Say($"   {grp}：{string.Join(" / ", hs.Select(h => Q[(grp, h)].ToString("+0.0000;-0.0000")))} W　比 {Ratio(grp):0.000}"
                  + (grp == "均匀" ? "" : $"　与均匀之差 {string.Join(" / ", hs.Select(h => (Q[(grp, h)] - Q[("均匀", h)]).ToString("+0.0000;-0.0000")))} W"));
            bool diffOk = hs.All(h => Math.Abs(Q[("自相似3h", h)] - Q[("均匀", h)]) <= 0.05);
            bool ratioOk = Math.Abs(Ratio("自相似3h") - Ratio("均匀")) <= 0.02;
            if (!diffOk || !ratioOk) selfOk = false;
            Say($"   自相似细带：与均匀之差 {(diffOk ? "三档都 ≤ 0.05 ✓" : "**有档 > 0.05**")}；比值差 {Math.Abs(Ratio("自相似3h") - Ratio("均匀")):0.000}{(ratioOk ? "（≤ 0.02 ✓）" : "（**> 0.02**）")}");
        }
        Say("");
        Say(selfOk ? "★ 四片都满足 ⇒ 用自相似细带（两侧各 3·h）声明配方。"
                   : "★ **有片不满足** ⇒ 保留固定 3 mm，U 计入 0.08 W，注明只在 h ≤ 1 上验证过、不许用于 2 mm。");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
