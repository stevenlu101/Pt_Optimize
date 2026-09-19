using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写；物理把关人确认「必须在重解之前做」）：
/// **保温边界默认按半径划 —— 圆盘整块包法兰保温，伸出去的舌片包舌保温。**
///
/// 旧口径 <c>x ≥ 切点x</c> 在舌半宽 = 盘半径时把切点放在 x = 0 ⇒ −x 那半个圆盘包的是**舌保温旋钮**。
/// 圆盘区最高温的峰在 r ≈ 29、x = −29，正好在那半边、旋钮底下 ⇒ 求解器抬舌保温等于直接给峰加保温
/// （实测 −6.965 → −49.012）。而现场安装清单写「舌保温从圆盘切点开始缠」，工人不会去缠那半个盘面。
///
/// 行为门直接调 <see cref="FlangePlate.UnderDiscInsulation"/> 与 <see cref="DesignCurrent.AreaWithinDisc"/>；
/// 求解器与整线的接线只能做源码门（ShellThermal 不持有板件）。
/// </summary>
public class R48InsulByRadiusTests
{
    private static FlangePlate Plate0()
    {
        var d = DesignSpec.W08.Clone();
        return d.Plate(0, d.DiscFloorMm(new DesignInputs()));
    }

    [Fact]
    public void 峰所在的那半个盘面归法兰保温()
    {
        var g = Plate0();
        Assert.True(double.IsNaN(g.InsulBoundaryXMm), "前提：内置档用的是默认分界");
        Assert.Equal(0.0, g.Tangent().X, 6);                     // 前提：切点在 x = 0，旧口径正是在这里失效
        // 实测峰位 r = 29.2、x = −29.0 —— 旧口径 x < 0 ⇒ 舌保温；新口径 r ≤ 30 ⇒ 法兰保温
        Assert.True(g.UnderDiscInsulation(-29.0, -3.0), "r≈29.2 在圆盘内，必须包法兰保温");
        Assert.True(g.UnderDiscInsulation(+29.0, 0.0));
        // 舌片区峰位 r = 33，在圆盘外 ⇒ 舌保温
        Assert.False(g.UnderDiscInsulation(-33.0, -1.0), "r=33 在圆盘外，是舌片");
        Assert.False(g.UnderDiscInsulation(-100.0, 0.0));
    }

    [Fact]
    public void 显式指定的分界原样按x_不许被半径规则悄悄改掉()
    {
        var g = Plate0();
        g.InsulBoundaryXMm = 1e9;                                 // 全裸
        Assert.False(g.UnderDiscInsulation(0.0, 27.0));
        Assert.True(double.IsNaN(g.InsulDiscRadiusMm), "显式分界交给求解器的必须是 NaN（按 x）");
        g.InsulBoundaryXMm = -1e9;                                // 全包
        Assert.True(g.UnderDiscInsulation(-120.0, 0.0));
        g.InsulBoundaryXMm = -10.0;                               // 显式分界：x ≥ −10 包
        Assert.True(g.UnderDiscInsulation(-5.0, 28.0));
        Assert.False(g.UnderDiscInsulation(-20.0, 0.0));
    }

    [Fact]
    public void 交给求解器的盘半径与板件判定是同一条规则()
    {
        var g = Plate0();
        Assert.Equal(g.DiscRadiusMm, g.InsulDiscRadiusMm, 9);
        double r = g.InsulDiscRadiusMm;
        // 求解器拿到 r 后按 x²+z² ≤ r² 判 —— 抽样核对与板件判定逐点一致
        for (double x = -60; x <= 40; x += 2.5)
            for (double z = -32; z <= 32; z += 2.5)
                Assert.Equal(g.UnderDiscInsulation(x, z), x * x + z * z <= r * r);
    }

    /// <summary>
    /// 盘半径无效时，板件判定与交给求解器的表达必须**真的**是同一条规则（2026-09-14 物理把关人查出：
    /// 原来 x²+z² ≤ NaN 恒假 ⇒ 板件整片悄悄包舌保温，求解器那边退回按 x，两边碰巧一致）。
    /// </summary>
    [Fact]
    public void 盘半径无效时板件与求解器走同一条退回路径()
    {
        var g = Plate0();
        g.DiscRadiusMm = double.NaN;
        Assert.True(double.IsNaN(g.InsulDiscRadiusMm), "盘半径无效 ⇒ 交给求解器的必须是 NaN（按 x）");
        double xb = g.InsulBoundaryXResolved;
        foreach (double x in new[] { -50.0, -1.0, 0.0, 1.0, 20.0 })
            Assert.Equal(double.IsNaN(xb) ? false : x >= xb, g.UnderDiscInsulation(x, 0.0));
    }

    /// <summary>
    /// 生产路径上的保温分界只准是 NaN（仅圆盘保温）或 ±1e9（全包/全裸）。
    /// 写进一个有限值（例如切点 0）会让板件**静默退回按 x 分** —— 正是被修掉的那种保温（2026-09-14 物理把关人查出，
    /// LineSolver.ClonePlate 会原样复制这个字段）。现役内置档钉在 NaN；仓库里的存档一份都不许带有限值。
    /// </summary>
    [Fact]
    public void 生产路径的保温分界只准是默认或全包全裸()
    {
        foreach (var d0 in new[] { DesignSpec.W08, DesignSpec.W06 })
        {
            var d = d0.Clone(); var p = new DesignInputs();
            for (int j = 0; j < d.FlangeCount; j++)
                Assert.True(double.IsNaN(d.Plate(j, d.DiscFloorMm(p)).InsulBoundaryXMm),
                    $"{d.Name} 片{j} 的保温分界不是默认 —— 会退回按 x 分");
        }
        foreach (string dir in new[] { "finaldesigns", "deliverable" })
        {
            string full = Path.Combine(Root(), dir);
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.EnumerateFiles(full, "*.json", SearchOption.AllDirectories))
            {
                string t = File.ReadAllText(f);
                var m = System.Text.RegularExpressions.Regex.Match(t, @"""InsulBoundaryXMm""\s*:\s*(-?[0-9.eE+]+)");
                if (!m.Success) continue;
                double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(Math.Abs(v) >= 1e8, $"{f} 存了有限的保温分界 {v} —— 会让这块板静默退回按 x 分保温");
            }
        }
    }

    /// <summary>
    /// 出处：FlangePlate.InsulBoundaryXMm 的注释记着 2026-08-10 用户确认的现场实况 ——
    /// 「纤维只包铂管与法兰圆，法兰圆以外均不保温」。按半径划就是这句话；这条门钉住那句出处还在。
    /// </summary>
    [Fact]
    public void 按半径划保温有现场出处()
    {
        string src = File.ReadAllText(Path.Combine(Root(), "Pt_Optimize/Core/PlateCurrent2D.cs"));
        Assert.Contains("纤维只包铂管与法兰圆，法兰圆以外均不保温", src);
    }

    [Fact]
    public void 圆盘整块面积等于圆环面积()
    {
        var g = Plate0();
        double expect = Math.PI * (g.DiscRadiusMm * g.DiscRadiusMm - g.HoleRadiusMm * g.HoleRadiusMm);
        double got = DesignCurrent.AreaWithinDisc(g);
        Assert.InRange(got, expect * 0.995, expect * 1.005);      // 舌半宽 = 盘半径，盘圆内全是板料
        // 旧口径 x ≥ 0 只数 +x 半个圆环 ⇒ 约为新口径的一半 —— 这正是被改掉的那一半
        double old = DesignCurrent.AreaFromX(g, g.InsulBoundaryXResolved);
        Assert.InRange(old / got, 0.45, 0.55);
    }

    [Fact]
    public void 整线与求解器接线都走同一条规则()
    {
        string th = Code("Pt_Optimize/Core/ShellThermal.cs");
        Assert.Contains("double insulDiscRadiusMm = double.NaN", th);
        Assert.Contains("insulated[i] = insulByRadius", th);
        Assert.Contains("bool insulOnTab = insulByRadius", th);       // 候选预筛跟着保温走

        string lr = Code("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("insulDiscRadiusMm: insulRForZone", lr);
        Assert.Contains("insulRForZone = plate.InsulDiscRadiusMm", lr);
        Assert.Contains("insulRForZone = eqJ.InsulDiscRadiusMm", lr);
        Assert.Contains("pl.UnderDiscInsulation(mesh.Centroid[k].X, mesh.Centroid[k].Z)", lr);   // 两节点参考量
        Assert.DoesNotContain("if (mesh.Centroid[k].X >= insulX) aIns", lr);                      // 不许再各抄一份 x 规则

        string dc = Code("Pt_Optimize/Core/DesignCurrent.cs");
        Assert.Contains("AreaWithinDisc(g)", dc);
    }

    private static string Code(string rel)
        => string.Join("\n", File.ReadAllText(Path.Combine(Root(), rel)).Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
