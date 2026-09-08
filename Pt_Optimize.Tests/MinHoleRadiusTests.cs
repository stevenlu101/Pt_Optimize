using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **「能造」清单：孔径 &lt; 1 mm 的孔不考虑**（R15，用户 2026-09-08：「孔径小于 1 mm 可以忽视」）。
///
/// 孔径旋钮的取值域从此是 {0} ∪ [1 mm, 上界]。四道门，每道钉一处会漏的地方：
///   ① 几何：(0,1) 里的孔径在 Plate() 里不是孔，出图 spec 与几何同口径（算一个画同一个）；
///   ② 上界：闭式上界不足 1 mm ⇒ 这根旋钮的上界就是 0（当场淘汰，理由由 ChooseKnob 说）；
///   ③ 二分：lo 是「无孔」时先探 1 mm，(0,1) 里一个点都不探；括号缩不下去就停；
///   ④ 写入：求解器要把孔径写进 (0,1) 时当场炸（不是静默夹住）。
/// </summary>
public class MinHoleRadiusTests
{
    [Fact]
    public void 最小孔径是用户给的数()
        => Assert.Equal(1.0, DesignSpec.TabHoleRMinMm);

    [Theory]
    [InlineData(0.5, false)]
    [InlineData(0.99, false)]
    [InlineData(1.0, true)]
    [InlineData(3.0, true)]
    public void 小于一毫米的孔在几何与出图里都不是孔(double r, bool hole)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleRMm[0] = r;
        var g = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        Assert.Equal(hole, g.TabHoles.Length == 1);
        Assert.Equal(hole ? r : 0.0, d.TabHoleREffective(0));
        // 出图 spec 与几何同一口径：几何里没有的孔，图上也不能有
        string spec = Geometry3dm.BuildFinalSpec(d);
        string holeR = spec.Split("\"holeR\":")[2].Split(',')[0];     // 第 1 个 holeR 是管孔（顶层），第 2 个才是片 0 的舌孔
        Assert.Equal(hole ? r.ToString("R") : "0", holeR);
    }

    [Fact]
    public void 上界不足一毫米时这根旋钮的上界就是零()
    {
        var d = DesignSpec.Builtin[0].Clone();
        var p = new DesignInputs(); var o = new SolverOptions();
        d.TabHalfWidthMm = 4.9;                       // 桥宽下限 4 ⇒ 桥宽闭式上界 0.9 mm
        double raw = Solver.HoleRadiusUpperRawMm(d, p, o, 0);
        Assert.True(raw > 0 && raw < 1.0, $"闭式上界 {raw:0.00} 不在 (0,1) 里，本条在空集上恒过");
        Assert.Equal(0.0, Solver.HoleRadiusUpperMm(d, p, o, 0));
        // 反向：上界够 1 mm 时不动它
        d.TabHalfWidthMm = 30;
        Assert.True(Solver.HoleRadiusUpperMm(d, p, o, 0) >= 1.0);
        Assert.Equal(Solver.HoleRadiusUpperRawMm(d, p, o, 0), Solver.HoleRadiusUpperMm(d, p, o, 0));
    }

    [Fact]
    public void 二分点不落在零到一毫米之间()
    {
        // 模拟一次二分：lo = 0（无孔）、hi = 6；谓词「r ≥ 2.3 才过」
        double lo = 0, hi = 6; var visited = new List<double>();
        for (int i = 0; i < 40 && hi - lo > 0.005; i++)
        {
            double mid = Solver.NextBisectPoint(Solver.Knob.TabHoleR, lo, hi);
            if (mid >= hi - 1e-12) break;
            visited.Add(mid);
            if (mid >= 2.3) hi = mid; else lo = mid;
        }
        Assert.NotEmpty(visited);
        Assert.All(visited, v => Assert.True(v <= 1e-12 || v >= 1.0 - 1e-12, $"探到了 {v:0.000} mm —— 落进 (0,1) 了"));
        Assert.True(hi >= 2.3 && hi < 2.31, $"二分没收到 2.3：hi={hi}");

        // 1 mm 就过 ⇒ 答案是 1 mm，且括号缩不下去时停（不许在 1 mm 上反复探）
        lo = 0; hi = 6; int probes = 0;
        for (int i = 0; i < 40 && hi - lo > 0.005; i++)
        {
            double mid = Solver.NextBisectPoint(Solver.Knob.TabHoleR, lo, hi);
            if (mid >= hi - 1e-12) break;
            probes++;
            if (mid >= 1.0) hi = mid; else lo = mid;
        }
        Assert.Equal(1.0, hi);
        Assert.Equal(1, probes);

        // 上界刚好 1 mm：唯一能探的点就是它本身；其它旋钮照旧取中点
        Assert.Equal(1.0, Solver.NextBisectPoint(Solver.Knob.TabHoleR, 0, 1.0));
        Assert.Equal(0.5, Solver.NextBisectPoint(Solver.Knob.Thick, 0, 1.0));
        Assert.Equal(2.5, Solver.NextBisectPoint(Solver.Knob.TabHoleR, 2.0, 3.0));   // lo 已是孔 ⇒ 普通中点
    }

    [Fact]
    public void 带进来的小孔按无孔并留痕()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleRMm = new[] { 0.5, 0.0, 2.0, 0.999 };
        var log = new List<string>();
        int n = Solver.NormalizeHoleRadii(d, log.Add);
        Assert.Equal(2, n);
        Assert.Equal(new[] { 0.0, 0.0, 2.0, 0.0 }, d.TabHoleRMm);
        Assert.Equal(2, log.Count);
        Assert.All(log, s => Assert.Contains("孔径 < 1 mm 的孔不考虑", s));
        // 自证：再跑一次什么都不改
        Assert.Equal(0, Solver.NormalizeHoleRadii(d, null));
    }

    /// <summary>④ 写入的门：Set 要把孔径写进 (0,1) 时必须炸 —— 这是「三处必有一处漏了」的最后一道。</summary>
    [Fact]
    public void 求解器把孔径写进零到一毫米之间会当场炸()
    {
        var set = typeof(Solver).GetMethod("Set", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(set);
        var d = DesignSpec.Builtin[0].Clone();
        var ex = Record.Exception(() => set!.Invoke(null, new object[] { d, Solver.Knob.TabHoleR, 0, 0.5 }));
        Assert.NotNull(ex);
        Assert.Contains("孔径 < 1 mm 的孔不考虑", (ex!.InnerException ?? ex).Message);
        // 0 与 ≥1 照常写
        set!.Invoke(null, new object[] { d, Solver.Knob.TabHoleR, 0, 0.0 });
        set!.Invoke(null, new object[] { d, Solver.Knob.TabHoleR, 0, 1.0 });
        Assert.Equal(1.0, d.TabHoleRMm[0]);
    }
}
