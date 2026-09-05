using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **孔位由场定**（计划第 3 件，用户 2026-09-05：
/// 「根据温度场与电流场去决定孔的大小与形状 —— 工程师拍脑袋开孔没有依据」）。
///
/// 现在的默认是「舌片自由段中点」—— 那也是拍的，只是拍得比工程师系统一点。
/// 这一步先**量**：同样大的孔放在舌片不同位置，抽热各降多少、电流密度各升多少。
///
/// 依据来自更早那次实测（deliverable/舌片电流密度分布.txt）：
/// 沿舌轴 J 从舌端 6.03 递减到盘缘 1.96 A/mm² —— 梯形舌越靠圆盘越宽、J 越低。
/// ⇒ **越靠圆盘挖越便宜**（电流代价低），而且离管子越近、截断的热流越多。
/// 本门验证这个推断，或推翻它。
///
/// ⚠ 只测不判。断言只钉「测量没退化」。
/// </summary>
public class HolePlacementTests
{
    private static FlangePlate Heater1(double holeX, double holeR) => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0,
        TabParallel = false, WeldFilletLegMm = 0,
        TabHoles = holeR > 0
            ? new[] { new FlangePlate.TabHole(holeX, 0, holeR) }
            : Array.Empty<FlangePlate.TabHole>(),
    };

    private static (double Q, double JPeak, double Vol, string Err) Run(FlangePlate g)
    {
        try
        {
            var baseIn = new DesignInputs();
            var c = new LineCase { Base = baseIn };
            var mesh = FlangeMesher.Build(g, 0, 2.0, 8.0, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
            if (mesh.CellCount < 200) return (0, 0, 0, $"网格只剩 {mesh.CellCount} 单元");
            var cur = ShellCurrent.SolveFor(c, mesh, 1000, 1.1e-4, maxIter: 8000, tol: 1e-7);
            double jPk = cur.JMagAPerMm2.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (!(jPk > 0)) return (0, 0, mesh.VolumeMm3, "电流解发散");
            var th = ShellThermal.Solve(mesh, cur.JMagAPerMm2, baseIn, 1150, double.NaN,
                                        maxIter: 20000, tol: 1e-3);
            if (double.IsNaN(th.QFromTubeW)) return (0, jPk, mesh.VolumeMm3, "热解发散");
            return (th.QFromTubeW, jPk, mesh.VolumeMm3, "");
        }
        catch (Exception ex) { return (0, 0, 0, ex.GetType().Name + "：" + ex.Message); }
    }

    [Fact]
    public void 孔开在舌片哪一段最划算()
    {
        var b = Run(Heater1(0, 0));
        Assert.True(b.Err.Length == 0 && b.Vol > 1000, "基准算不出来：" + b.Err);

        var sb = new StringBuilder();
        sb.AppendLine("═══ 孔位对比：同样 R8 的孔放在舌片不同位置 ═══");
        sb.AppendLine("几何：盘Ø120　舌长 199.5　舌端半宽 40　梯形舌　板厚 2.0 mm　1000 A");
        sb.AppendLine("盘缘切点 x ≈ -6.1；舌片从 x=-199.5 到切点。J 沿舌轴由舌端 6.03 递减到盘缘 1.96。");
        sb.AppendLine();
        sb.AppendLine("孔心 x\t距圆盘\t抽热 W\t较基准\t峰值 J\t较基准\t体积 mm³\t较基准\t每 1% 体积换到的抽热降");
        sb.AppendLine($"（无孔）\t—\t{b.Q:0.0}\t—\t{b.JPeak:0.000}\t—\t{b.Vol:0}\t—\t—");

        foreach (double x in new[] { -20.0, -50.0, -100.0, -150.0, -185.0 })
        {
            var r = Run(Heater1(x, 8));
            if (r.Err.Length > 0) { sb.AppendLine($"{x:0}\t—\t✗ {r.Err}"); continue; }
            double dQ = 100 * (r.Q / b.Q - 1), dV = 100 * (r.Vol / b.Vol - 1);
            sb.AppendLine($"{x:0}\t{-x - 6.1:0}\t{r.Q:0.0}\t{dQ:+0.00;-0.00} %"
                        + $"\t{r.JPeak:0.000}\t{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %"
                        + $"\t{r.Vol:0}\t{dV:+0.00;-0.00} %"
                        + $"\t{(Math.Abs(dV) > 1e-9 ? (dQ / dV).ToString("0.00") : "—")}");
        }

        // ══ 能不能**只解一次**就预测出最优点？
        //   试 5 个位置太贵（每个一次全解）。若能从**无孔那一次解**算出一个分数，
        //   其峰值落在实测最优附近，就只要解一次。
        //   分数 = 该处能截断的热流 ÷ 该处的电流密度代价。
        //   热流沿舌轴向管子走，近似正比于「离管子多近」；电流代价就是 J。
        {
            var g0 = Heater1(0, 0);
            var mesh0 = FlangeMesher.Build(g0, 0, 2.0, 8.0, 50.0, new DesignInputs().BusbarClampLengthMm, 0, 0);
            var cur0 = ShellCurrent.SolveFor(new LineCase { Base = new DesignInputs() }, mesh0,
                                             1000, 1.1e-4, maxIter: 8000, tol: 1e-7);
            sb.AppendLine();
            sb.AppendLine("── 只解一次能不能预测最优点（分数 = 1/离管距离 ÷ J，取舌轴附近单元）──");
            sb.AppendLine("孔心 x	该处 J	分数（越大越该挖）");
            for (double x = -20; x >= -190; x -= 10)
            {
                var near = Enumerable.Range(0, mesh0.CellCount)
                    .Where(k => Math.Abs(mesh0.Centroid[k].X - x) < 6 && Math.Abs(mesh0.Centroid[k].Z) < 10)
                    .ToArray();
                if (near.Length == 0) continue;
                double jAvg = near.Average(k => cur0.JMagAPerMm2[k]);
                double dist = Math.Abs(x);                 // 离管心的轴向距离
                double score = (1.0 / dist) / Math.Max(jAvg, 1e-9) * 1000;
                sb.AppendLine($"{x:0}	{jAvg:0.000}	{score:0.000}");
            }
        }

        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "孔位对比.txt"), sb.ToString());
    }
}
