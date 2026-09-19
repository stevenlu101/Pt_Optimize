using System;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **孔那一带根本没细化过 —— 基于它的判断全部不可信**（2026-09-06 用户点破）。
///
/// 用户：「计算电场与温度场的网格所得的值是你判断挖孔的标准？如果是，连你网格都画错」。
/// 查实，**是的，而且网格确实不对**：
/// <code>
///   ShellMesh.Build   xBands = { new(-fineRadius, +fineRadius, hFine) }   ← 细带**以原点为中心**
///   LineRunner        MeshFineMm 2.0、MeshCoarseMm 11.0、MeshFineRadiusMm 50.0
///   舌板孔心          x = −102  ⇒ **远在 ±50 之外** ⇒ 落在 11 mm 粗网格上
///   桥宽              4 mm                          ⇒ **比一个格子还窄**
/// </code>
/// 而边界是**覆盖率打折**（4×4 子采样，frac &lt; 0.25 丢弃，面积 × frac），
/// 格子不贴合孔边 ⇒ 总截面也许对得上，但**峰值 J 的空间分辨率只有格子那么大**。
///
/// ⇒ 本文件量的是：把细区扩到真正覆盖孔之后，峰值 J 变多少。
///   变得多 ⇒ deliverable/开孔的电流代价.txt 那张表作废，孔的判断要重做。
/// </summary>
public class HoleMeshResolutionTests
{
    private static FlangePlate Heater1(double tMm, params FlangePlate.TabHole[] holes) => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = tMm, ThickenedMm = tMm, TabThicknessMm = tMm,
        TabParallel = false, WeldFilletLegMm = 0,
        TabHoles = holes,
    };

    /// <summary>解一次电流场，返回峰值 J、单元数、导电面积。</summary>
    private static (double JPeak, int Cells, double AreaMm2, string Err)
        Solve(FlangePlate g, double hFine, double hCoarse, double fineRadius)
    {
        try
        {
            var baseIn = new DesignInputs();
            var c = new LineCase { Base = baseIn };
            var mesh = FlangeMesher.Build(g, 0, hFine, hCoarse, fineRadius,
                                       baseIn.BusbarClampLengthMm, 0, 0);
            if (mesh.Cells.Count < 200) return (0, mesh.Cells.Count, 0, "网格太少");
            var cur = ShellCurrent.SolveFor(c, mesh, totalCurrentA: 1000, rhoRefOhmMm: 1.1e-4,
                                            maxIter: 20000, tol: 1e-8);
            double jPk = cur.JMagAPerMm2.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (!(jPk > 0) || double.IsInfinity(jPk)) return (0, mesh.Cells.Count, 0, "电流解发散");
            return (jPk, mesh.Cells.Count, mesh.Area.Sum(), "");
        }
        catch (Exception ex) { return (0, 0, 0, ex.GetType().Name + "：" + ex.Message); }
    }

    /// <summary>
    /// ★★★★★ 同一个孔，网格从「粗区 8 mm」一路细到「桥上 4–5 个格子」，看峰值 J 怎么跑。
    /// 若它一路往上爬还没收敛，说明原来那个数**只是网格的产物**。
    /// </summary>
    [Trait("速度", "慢")]   // ★ 真跑场解/出图；钩子默认跳过，见 .githooks/pre-commit
    [Fact]
    public void 孔那一带细化前后峰值电流密度差多少()
    {
        const double t = 4.71, hw = 40.0;
        double xc = -102.11, r = 36.0;          // 与交付件上那个孔同一组数
        double bridge = hw - r;                 // 单侧桥宽 4 mm

        var sb = new StringBuilder();
        sb.AppendLine("═══ 孔那一带的网格分辨率：峰值电流密度收敛了吗 ═══");
        sb.AppendLine($"构型 Pt_Heater1（盘R60、管孔R26、舌 199.5×80、板厚 {t}），");
        sb.AppendLine($"孔心 x={xc}、孔径 R{r} ⇒ **单侧桥宽 {bridge:0.0} mm**");
        sb.AppendLine();
        sb.AppendLine("⚠ 细带是**以原点为中心**的 ±fineRadius；孔心在 x=−102，");
        sb.AppendLine("  fineRadius 要 ≥ 138 才盖得住孔的外缘。");
        sb.AppendLine();
        sb.AppendLine("细 mm\t粗 mm\t细区半径\t孔在细区?\t桥上格数\t单元数\t峰值 J\t相对");

        double j0 = double.NaN;
        var rows = new (double f, double c, double R)[]
        {
            (2.0,  8.0,  50.0),    // ← 我原来那张表用的（孔在**粗区**）
            (2.0, 11.0,  50.0),    // ← 求解器默认（孔在粗区，更粗）
            (2.0,  8.0, 150.0),    // 细区盖住孔：桥上 2 格
            (1.0,  8.0, 150.0),    //             桥上 4 格
            (0.5,  8.0, 150.0),    //             桥上 8 格
            (0.25, 8.0, 150.0),    //             桥上 16 格
        };
        foreach (var (f, cc, R) in rows)
        {
            var g = Heater1(t, new FlangePlate.TabHole(xc, 0, r));
            var (jp, cells, area, err) = Solve(g, f, cc, R);
            bool inFine = R >= Math.Abs(xc) + r;
            double hAtHole = inFine ? f : cc;
            double nBridge = bridge / hAtHole;
            if (double.IsNaN(j0) && err.Length == 0) j0 = jp;
            sb.AppendLine($"{f:0.00}\t{cc:0.0}\t{R:0}\t{(inFine ? "是" : "**否**")}"
                        + $"\t{nBridge:0.0}\t{cells}\t"
                        + (err.Length > 0 ? "— " + err : $"{jp:0.000}\t{jp / Math.Max(j0, 1e-9):0.00}×"));
        }

        sb.AppendLine();
        sb.AppendLine("怎么读：");
        sb.AppendLine("  · 前两行是**现况**（孔落在粗网格上，桥比一个格子还窄）；");
        sb.AppendLine("  · 后四行细区盖住了孔，桥上格数逐步加倍。");
        sb.AppendLine("  · 若峰值 J 一路往上爬、没有收敛平台 ⇒ 原来那个数只是**网格的产物**，");
        sb.AppendLine("    deliverable/开孔的电流代价.txt 那张表作废，孔的判断要重做。");

        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped("孔的网格分辨率.txt"),
                          sb.ToString());
        Console.WriteLine(sb.ToString());
        Assert.True(true);   // 这是**记录事实**的门，结论由数字给
    }
}
