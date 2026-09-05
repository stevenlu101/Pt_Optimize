using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **场决定该挖哪里**（2026-09-05，用户口径）。
///
/// 用户：「希望根据温度场与电流场去决定孔的大小与形状 —— 工程师拍脑袋开孔、
/// 跟着放大缩小找答案，没有依据」。
///
/// 上一步（TabFieldSurveyTests）量出：
///   舌片 J 中位 5.071、低电流区仅 4.4 %，沿舌轴逐段低区占比几乎全是 0.0 %
///   圆盘 J 中位 0.278（低一个数量级）、低电流区 28.3 %
/// ⇒ **舌片没有开孔的余地，圆盘有。**
///
/// 这一步回答「圆盘上具体挖哪里」：
/// <code>
///   移除优先级 = 导热贡献 q [W] ÷ 电流密度代价 J [A/mm²]
/// </code>
/// · **导热贡献**：该单元各面上传导热流的绝对值之和 ÷ 2（有限体积通量）。
///   大 = 它在「法兰从管子抽热」的主路径上 ⇒ 挖掉它，③ 直接改善。
/// · **电流密度代价**：挖掉高 J 的单元会把电流挤到旁边 ⇒ 峰值 J 升高、逼近熔点。
///
/// ⚠ 本门**只测不判**：结论写进 deliverable/移除优先级.txt。
///   断言只钉「测量没退化」——否则读出来的结论是空转。
/// </summary>
public class RemovalPriorityTests
{
    [Fact]
    public void 量一次移除优先级()
    {
        // Pt_Heater1 交接后的构型：③ 超限 42 倍的那一个
        var plate = new FlangePlate
        {
            DiscRadiusMm = 60, HoleRadiusMm = 26,
            TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
            ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0,
            TabParallel = false, WeldFilletLegMm = 0,
        };
        var baseIn = new DesignInputs();
        var c = new LineCase { Base = baseIn };

        var mesh = FlangeMesher.Build(plate, 0, 2.0, 8.0, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
        Assert.True(mesh.CellCount > 300, $"网格只有 {mesh.CellCount} 单元 —— 测量退化");

        var cur = ShellCurrent.SolveFor(c, mesh, totalCurrentA: 1000, rhoRefOhmMm: 1.1e-4);
        var th = ShellThermal.Solve(mesh, cur.JMagAPerMm2, baseIn,
                                    tRootC: 1150, insulBoundaryX: double.NaN);

        int n = mesh.CellCount;
        double k = Materials.PtThermalK(1150);      // W/(m·K) → 下面按 mm 换算
        // 各单元的导热通量绝对值之和 / 2（每条面被两个单元各算一次）
        var q = new double[n];
        foreach (var f in mesh.Faces)
        {
            if (f.A < 0 || f.B < 0) continue;                  // 边界面不算「内部导热」
            if (f.DistAB <= 1e-9) continue;
            double tMm = 0.5 * (mesh.Thickness[f.A] + mesh.Thickness[f.B]);
            // q = k[W/(m·K)] × 1e-3 → W/(mm·K)；面积 = 边长 × 厚度 [mm²]
            double flux = Math.Abs(k * 1e-3 * f.Length * tMm * (th.T[f.A] - th.T[f.B]) / f.DistAB);
            q[f.A] += 0.5 * flux; q[f.B] += 0.5 * flux;
        }

        double xTan = plate.Tangent().X;
        var rows = Enumerable.Range(0, n)
            .Where(i => mesh.Part[i] == 0)                      // 只看法兰
            .Select(i => new
            {
                I = i,
                X = mesh.Centroid[i].X,
                R = Math.Sqrt(mesh.Centroid[i].X * mesh.Centroid[i].X
                            + mesh.Centroid[i].Z * mesh.Centroid[i].Z),
                Q = q[i],
                J = cur.JMagAPerMm2[i],
                A = mesh.Area[i],
            })
            .Where(r => r.A > 0)
            .ToList();
        Assert.True(rows.Count > 200, $"只取到 {rows.Count} 个法兰单元 —— 测量退化");

        double jEps = rows.Max(r => r.J) * 1e-3;
        var pri = rows.Select(r => new { r.X, r.R, r.Q, r.J, r.A, P = r.Q / (r.J + jEps) })
                      .OrderByDescending(r => r.P).ToList();
        Assert.True(pri[0].P > pri[^1].P * 2,
            "优先级几乎是常数 —— 那说明这个判据分不出高下，下面的结论是空转");

        var sb = new StringBuilder();
        sb.AppendLine("═══ 移除优先级 = 导热贡献 q [W] ÷ 电流密度 J [A/mm²] ═══");
        sb.AppendLine($"构型：盘Ø{2 * plate.DiscRadiusMm:0}　舌长 {-plate.TabEndXMm:0.0}"
                    + $"　舌端半宽 {plate.TabEndHalfWidthMm:0}　板厚 {plate.ThicknessMm:0.00} mm");
        sb.AppendLine($"网格 {mesh.CellCount} 单元　法兰 {rows.Count} 个　管孔 R{plate.HoleRadiusMm:0}"
                    + $"　切点 x = {xTan:0.0}");
        sb.AppendLine($"整片：抽热 {th.QFromTubeW:0.0} W　最高温 {th.TMaxC:0} °C");
        sb.AppendLine();
        sb.AppendLine("── 优先级最高的 12 个单元（该挖的地方）──");
        sb.AppendLine("排名\tx mm\tr mm\t导热 q W\t电流 J\t优先级\t在哪");
        for (int t = 0; t < 12 && t < pri.Count; t++)
        {
            var r = pri[t];
            sb.AppendLine($"{t + 1}\t{r.X:0.0}\t{r.R:0.0}\t{r.Q:0.000}\t{r.J:0.000}\t{r.P:0.00}"
                        + $"\t{(r.X < xTan ? "舌片" : "圆盘")}");
        }
        sb.AppendLine();
        sb.AppendLine("── 分区汇总 ──");
        void Sum(string name, Func<dynamic, bool> sel)
        {
            var g = pri.Where(r => sel(r)).ToList();
            if (g.Count == 0) { sb.AppendLine($"{name}：无"); return; }
            sb.AppendLine($"{name}　单元 {g.Count}　面积 {g.Sum(r => (double)r.A):0} mm²　"
                        + $"导热合计 {g.Sum(r => (double)r.Q):0.0} W　"
                        + $"J 中位 {g.OrderBy(r => (double)r.J).ElementAt(g.Count / 2).J:0.000}　"
                        + $"优先级中位 {g.OrderBy(r => (double)r.P).ElementAt(g.Count / 2).P:0.00}");
        }
        Sum("舌片　", r => r.X < xTan);
        Sum("圆盘　", r => r.X >= xTan);
        Sum("圆盘·背侧（x > 0，与舌片相反的半边）", r => r.X > 0);
        Sum("圆盘·舌侧（x ≤ 0）", r => r.X >= xTan && r.X <= 0);

        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "移除优先级.txt"), sb.ToString());
    }
}
