using System;
using System.Linq;
using System.Text.RegularExpressions;
using PtOptimize.Core;
using Xunit;
using static PtOptimize.Core.InsulationSearch;

namespace PtOptimize.Tests;

/// <summary>
/// ★ R48 J 路（2026-09-15 Opus 5）：保温搜索两条接线的**行为门**（合并把关待办 P2-7、P2-8）。只调生产入口，不改保温搜索的判据与可行性（那是 K 路的活）。
/// </summary>
public class R48J_InsulationSearchWiringGateTests
{
    /// <summary>
    /// P2-7：默认评估函数的限值必须真的从视图（= 整线算例 LineCase）读。原门 R48InsulationSearchEngineTests「默认评估函数_按定义算」给的限值恰好就是缺省 5，
    /// 源码门只认「HotLimitK = 数字」的写法 ⇒ DefaultCriteria 里写死 5.0 两道门都挡不住。这里给 3.7／6.3（两个都不是 5、彼此也不同），限值与归一裕度都按给的数核。
    /// </summary>
    [Fact]
    public void P2_7_默认评估函数的限值取非5的算例也跟着走()
    {
        var v = new PlateStateView
        {
            Plate = 1, Shared = true, State = 0, StateName = StateGlass,
            HotLimitK = 3.7, ColdLimitK = 6.3,
            ReferenceC = ThermocoupleBasis.LogMeanC(1150, 1080),
            DiscPeakC = 1110, TabPeakC = 1118, RootHotC = 1121, RootColdC = 1112,
            NetInflowW = 1.5, GammaKPerW = 2.0,
        };
        var t = DefaultCriteria(v, new Options());
        var hot = t.Single(c => c.Name == HotName);
        var cold = t.Single(c => c.Name == ColdName);
        Assert.Equal(3.7, hot.Limit);
        Assert.Equal(6.3, cold.Limit);
        Assert.Equal((3.7 - hot.Value) / 3.7, hot.NormMargin, 12);
        Assert.Equal((6.3 - cold.Value) / 6.3, cold.NormMargin, 12);
        // 两个限值交换：热侧、冷侧各跟各的，不许共用一个数
        v.HotLimitK = 6.3; v.ColdLimitK = 3.7;
        var t2 = DefaultCriteria(v, new Options());
        Assert.Equal(6.3, t2.Single(c => c.Name == HotName).Limit);
        Assert.Equal(3.7, t2.Single(c => c.Name == ColdName).Limit);
    }

    /// <summary>
    /// P2-8：保温搜索真的把细区半径传进了网格。原来只有源码计数门（「Solver.ApplyCaseMesh(」出现 ≥ 3 次）—— 把半径那个参数传成 0，计数照样过。
    /// 探针（deliverable/J路_J10乙_保温搜索内层网格截日志_本次开跑于2026-09-15_194149.txt）：W08 内层 h = 1 mm，细区半径 59 时逐片 4122 格、不传半径（留缺省 50）3642 格，
    /// 且与板厚无关（+0／+0.5／+1.5 mm 同为这两个数）。门：跑生产入口 InsulationSearch.Run，截它自己印的「内层网格」那一行就停（不解场，约 3 s），
    /// 那一行的逐片格数必须等于「同一设计按细区半径 = MeshVerify.RequiredMeshFor 造的网格」，且不等于不传半径的格数（非空转）。
    /// </summary>
    [Fact]
    public void P2_8_保温搜索内层网格真的带着细区半径()
    {
        var p = new DesignInputs { CriteriaRuleSet = InsulationSearch.RuleSetOfSearch };   // 决 103（2026-09-24）：保温搜索只在改回口径下跑（生产口径拒答）
        var d0 = DesignSpec.W08.Clone().Fit();
        double radius = MeshVerify.RequiredMeshFor(d0, p).RadiusMm;
        const double h = 1.0;
        string? got = null;
        var stop = new InvalidOperationException("门：截到内层网格那一行，停");
        try
        {
            InsulationSearch.Run(DesignSpec.W08, p, new Options
            {
                TubeLayers = new[] { 40 }, InnerMeshMm = h, DiscLayerMax = 1, TabLayerMax = 1,
                Log = line => { if (line.Contains("内层网格 h=", StringComparison.Ordinal)) { got = line; throw stop; } },
            });
        }
        catch (Exception ex) when (ReferenceEquals(ex, stop) || ex.InnerException is not null && ReferenceEquals(ex.GetBaseException(), stop)) { }
        Assert.NotNull(got);
        var m = Regex.Match(got!, @"逐片 ([\d/]+) 格");
        Assert.True(m.Success, "内层网格那一行没有逐片格数：" + got);
        int[] logged = m.Groups[1].Value.Split('/').Select(int.Parse).ToArray();

        int[] Cells(double r)
        {
            var lc = d0.BuildCase(p, checkRamp: false);
            Solver.ApplyCaseMesh(lc, new SolverOptions { FineMm = h, FineRadiusMm = r });
            return Enumerable.Range(0, lc.FlangeCount).Select(j => LineRunner.PlateMeshAnalytic(lc, j).CellCount).ToArray();
        }
        var withR = Cells(radius);
        var without = Cells(0);
        Assert.NotEqual(withR, without);                        // 非空转：半径传不传，格数不同
        Assert.Equal(withR, logged);
    }
}
