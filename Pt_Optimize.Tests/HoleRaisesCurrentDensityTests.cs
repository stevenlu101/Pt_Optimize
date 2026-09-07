using System;
using System.Linq;
using System.Text;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **挖料会把电流挤到桥上 —— 得有东西挡着**（2026-09-05 用户看图看出来的）。
///
/// 交付的 `deliverable/优化后3dm/整机.3dm` 上，求解器把舌板孔挖到 R36，
/// 而舌片半宽只有 40：
/// <code>
///   挖之前   80 mm 全宽
///   挖之后    两条 4 mm 的桥   ⇒ 过流截面剩 10 %
/// </code>
/// 用户一眼看出来的就是这个 —— 而求解器不知道。
///
/// ══ 为什么求解器不知道（查实的，不是猜的）
///
/// <code>
///   Criteria.cs 的 All 表   E(LineResult.Key.FlangeJ, "A/mm²", "≤", "法兰上的电流密度峰值", false)
///                                                                                    ↑ hard = false
///                     ⇒ 「· 法兰 J_max」是**参考量，印出来不卡交付**
///   DesignSpec        TabHoleRMaxMm() => TabHalfWidthMm − 4.0
///                     ⇒ 孔径上界**只有一条几何约束**（桥宽），一个字都没提电流
/// </code>
///
/// ⚠ 而且这根旋钮**不能靠「下一轮再补」**：求解器只许往上走，
///   治 J 唯一的办法是把孔**改小** ⇒ 必须在**上界**里挡住，事后没有任何旋钮救得回来。
///   这与 ⑥「圆盘盖得住管孔」是同一个形状（没有旋钮能治 ⇒ 当场验）。
/// </summary>
public class HoleRaisesCurrentDensityTests
{
    /// <summary>Pt_Heater1 构型的单片法兰（与 <c>FieldGuidedSlotTests</c> 同一份，便于横向比）。</summary>
    private static FlangePlate Heater1(double tMm, params FlangePlate.TabHole[] holes) => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = tMm, ThickenedMm = tMm, TabThicknessMm = tMm,
        TabParallel = false, WeldFilletLegMm = 0,
        TabHoles = holes,
    };

    /// <summary>解一次电流场，返回峰值 J 与体积。发散也要留证据。</summary>
    private static (double JPeak, double VolMm3, string Err) Solve(FlangePlate g)
    {
        try
        {
            var baseIn = new DesignInputs();
            var c = new LineCase { Base = baseIn };
            var mesh = FlangeMesher.Build(g, 0, 2.0, 8.0, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
            if (mesh.CellCount < 200) return (0, 0, $"网格只剩 {mesh.CellCount} 单元（孔把板切碎了）");
            var cur = ShellCurrent.SolveFor(c, mesh, totalCurrentA: 1000, rhoRefOhmMm: 1.1e-4,
                                            maxIter: 8000, tol: 1e-7);
            double jPk = cur.JMagAPerMm2.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (!(jPk > 0) || double.IsNaN(jPk) || double.IsInfinity(jPk))
                return (0, mesh.VolumeMm3, "电流解发散（多半是孔把电流回路掐断了）");
            return (jPk, mesh.VolumeMm3, "");
        }
        catch (Exception ex) { return (0, 0, ex.GetType().Name + "：" + ex.Message); }
    }

    /// <summary>
    /// ★★★★★ **记录事实**：孔从 0 挖到几何上界，峰值电流密度涨多少。
    /// 交付件上那个 R36 的孔，代价在这张表里。
    /// </summary>
    [Fact]
    public void 孔挖到几何上界时电流密度涨多少()
    {
        const double t = 4.71, hw = 40.0;
        double xc = -102.11;                 // 与交付 spec 里的 holeX 同一个数
        double rMax = hw - 4.0;              // TabHoleRMaxMm：桥宽下限 4 mm

        var sb = new StringBuilder();
        sb.AppendLine("═══ 舌板开孔：挖掉过流截面的代价 ═══");
        sb.AppendLine($"构型 Pt_Heater1（盘 R60、管孔 R26、舌 199.5×80、板厚 {t} mm），孔心 x={xc}");
        sb.AppendLine();
        sb.AppendLine("孔径 R\t桥宽(单侧)\t过流截面剩\t峰值 J A/mm²\t相对无孔\t体积 mm³");

        var baseIn = new DesignInputs();
        var (j0, v0, e0) = Solve(Heater1(t));
        Assert.True(e0.Length == 0, "无孔基准就解不出来：" + e0);
        sb.AppendLine($"无孔\t—\t100.0 %\t{j0:0.000}\t1.00×\t{v0:0}");

        double jAtMax = double.NaN;
        foreach (double r in new[] { 10.0, 20.0, 30.0, rMax })
        {
            var g = Heater1(t, new FlangePlate.TabHole(xc, 0, r));
            var (jp, vol, err) = Solve(g);
            double bridge = hw - r;
            double frac = 100.0 * (2 * bridge) / (2 * hw);
            if (err.Length > 0) { sb.AppendLine($"{r:0.0}\t{bridge:0.0}\t{frac:0.0} %\t— {err}"); continue; }
            sb.AppendLine($"{r:0.0}\t{bridge:0.0}\t{frac:0.0} %\t{jp:0.000}"
                        + $"\t{jp / Math.Max(j0, 1e-9):0.00}×\t{vol:0}");
            if (Math.Abs(r - rMax) < 1e-9) jAtMax = jp;
        }

        sb.AppendLine();
        sb.AppendLine($"限值 JAllowAPerMm2 = {baseIn.JAllowAPerMm2:0.000} A/mm²"
                    + "（注：本表是单片 1000 A 的口径，整线实际电流由耦合解给）");
        sb.AppendLine("⚠ 「· 法兰 J_max」在 Criteria.All 里是 hard=false ⇒ **参考量，不卡交付**；");
        sb.AppendLine("   而 TabHoleRMaxMm 只算桥宽 ⇒ 孔径上界里**没有电流密度**。");
        Directory.CreateDirectory(Path.Combine(HandoverDoc.Root(), "deliverable"));
        File.WriteAllText(Path.Combine(HandoverDoc.Root(), "deliverable", "开孔的电流代价.txt"),
                          sb.ToString());
        Console.WriteLine(sb.ToString());

        Assert.False(double.IsNaN(jAtMax),
            "孔挖到几何上界 R" + rMax.ToString("0.0") + " 时场解没给出结果 —— 见上表");
        Assert.True(jAtMax > j0,
            $"挖掉九成过流截面，峰值 J 却没涨（{j0:0.000}→{jAtMax:0.000}）"
          + " —— 那说明场解压根没把孔算进去，比「没挡住」严重得多");
    }
}
