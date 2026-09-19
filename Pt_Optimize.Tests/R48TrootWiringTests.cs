using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

/// <summary>
/// ★★ R48 接线核对（2026-09-14，Opus 5 写）：误差预算里「管根 ±5 K 时 Δ(1→0.5) 变化 0.0～0.1 %」**太整齐**，
/// 先查管根到底有没有传进单片解 —— 抽热本身应随管根明显变（管根升 ⇒ 抽热降）。Δ 不变可以是物理（离散误差对整体平移不敏感），
/// 也可以是管根没传进去；只有抽热的绝对值跟着变，才能说是前者。同时给出数值把关人要的「片抽热对管根的灵敏度」（W/K）。
/// 设置与 R48ErrorBudgetTests 相同（整线工作点，均匀网格），h = 1 与 0.5。
/// </summary>
[Trait("速度", "慢")]
public class R48TrootWiringTests
{
    private readonly ITestOutputHelper _out;
    public R48TrootWiringTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void 管根确实传进单片解_抽热对管根的灵敏度()
    {
        var sb = new StringBuilder();
        string file = Path.Combine(Root(), "deliverable", "R48_管根接线与灵敏度_2026-09-14.txt");
        void Say(string s)
        {
            _out.WriteLine(s); sb.AppendLine(s);
            try { File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
        Say($"R48 管根接线核对与抽热灵敏度（Opus 5）　{DateTime.Now:yyyy-MM-dd HH:mm}　设置同 R48ErrorBudgetTests");
        var p = new DesignInputs();
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = new[] { 0.73, 1.26, 1.26, 0.73 };
        d.TabInsulMm = new[] { 4.60, 2.10, 2.90, 7.50 };
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d = d.Fit();
        d.SizeTongues(p);
        double[] iJ = { 1213, 1984, 1817, 1022 };
        double[] tRoot = { 1172.63, 1151.19, 1093.09, 1056.80 };
        bool moved = true;
        for (int j = 0; j < 4; j++)
            foreach (double h in new[] { 1.0, 0.5 })
            {
                var lc = d.BuildCase(p, checkRamp: false);
                var g = d.Plate(j, d.DiscFloorMm(p));
                g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
                // 口径钉住（2026-09-14 Opus 5）：生产默认已改为压接整面接触 + 自相似压接细带，本探针显式钉回改动前口径（只钉外圈、不铺细带），deliverable 里同名证据文件不换口径
                var m = FlangeMesher.Build(g, 0, h, h, 1e6, lc.Base.BusbarClampLengthMm, clampBandMm: 0, clampFullFace: false);
                double tSet = d.SetpointC[Math.Min(j, d.SetpointC.Length - 1)];
                var sc = ShellCurrent.SolveFor(lc, m, iJ[j], Materials.PtResistivity(tSet) * 1e3, tSet);
                double Q(double tr)
                {
                    var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
                    if (j < lc.ClampTempC.Length) p2.BusbarClampTempC = lc.ClampTempC[j];
                    return ShellThermal.Solve(m, sc.JMagAPerMm2, p2, tr, g.InsulBoundaryXResolved, g.TwoTabs,
                                              tabBoundaryX: g.Tangent().X, tabInsulThickMm: g.TabInsulThickMm,
                                              discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm).QFromTubeW;
                }
                double qm = Q(tRoot[j] - 5), q0 = Q(tRoot[j]), qp = Q(tRoot[j] + 5);
                if (!(Math.Abs(qp - qm) > 0.1)) moved = false;
                Say($"   片{j} h={h:0.0}：抽热 管根−5 {qm:+0.000;-0.000}　标称 {q0:+0.000;-0.000}　管根+5 {qp:+0.000;-0.000} W　灵敏度 {(qp - qm) / 10:+0.000;-0.000} W/K");
            }
        Say(moved ? "★ 抽热随管根明显变 ⇒ 管根确实传进去了；预算里 Δ 不变是离散误差对管根平移不敏感。" : "★ **抽热不随管根变 ⇒ 管根没传进去，预算里那组核对作废**");
        Assert.True(moved, "管根没传进单片解");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
