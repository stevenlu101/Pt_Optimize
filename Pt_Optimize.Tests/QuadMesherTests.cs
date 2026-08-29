using System;
using System.Collections.Generic;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **四叉树局部加密**（2026-08-29，A⑭ 的根治办法）。
///
/// ══ 它要解决的是什么
///
/// 张量积网格（xs × zs）在孔周要 0.146 mm，就得让**整条 x 轴与整条 z 轴**都细下来 ——
/// 「孔周」这个**环形**区域，用区间表达不了。实测分区只省 **37 %**。
///
/// ⚠ 更要紧：**起步粗不改变收敛所需的网格尺寸**（那由物理定）。
///   ⇒ 只有局部加密能改变「收敛点」的成本：0.146 mm 只有孔周几毫米真的需要，
///     而张量网格逼着 68 mm 半径的整片陪着细。
///
/// ══ 本组验的三件事
///
/// ① <c>hAt</c> 真的被遵守（该细的地方细了）
/// ② **2:1 平衡**（相邻层级差 ≤ 1）—— 非协调有限体积的常规约束
/// ③ ★★ **守恒**：每个单元的「面长之和 = 周长」。
///    少了就是漏通量、多了就是放大通量 —— 两种都**不报错，只是答案错**。
/// </summary>
public class QuadMesherTests
{
    private static FlangePlate Plate() => new()
    {
        DiscRadiusMm = 30, HoleRadiusMm = 25.6,
        TabEndXMm = -60, TabEndHalfWidthMm = 20, ThicknessMm = 1.0,
        WeldFilletLegMm = 0,
    };

    /// <summary>孔周要细、远场要粗的分区规则。</summary>
    private static Func<double, double, double> Zoned(double hFine, double hCoarse, double rFine)
        => (x, z) => Math.Sqrt(x * x + z * z) <= rFine ? hFine : hCoarse;

    [Fact]
    public void 该细的地方真的细了()
    {
        var g = Plate();
        var m = QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 8.0, 4.0);

        // 孔周附近的单元边长必须 ≤ 要求值（用面积反推边长：正方格 ⇒ √面积，边界格偏小）
        var near = Enumerable.Range(0, m.CellCount)
            .Where(i => Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X
                                + m.Centroid[i].Z * m.Centroid[i].Z) < 28.0)
            .ToArray();
        Assert.NotEmpty(near);
        var bad = near.Where(i => Math.Sqrt(m.Area[i]) > 0.5 + 1e-6).Take(6)
            .Select(i => {
                var nd = m.Cells[i];
                double side = (m.Nodes[nd[1]] - m.Nodes[nd[0]]).Norm;
                double rr = Math.Sqrt(m.Centroid[i].X * m.Centroid[i].X + m.Centroid[i].Z * m.Centroid[i].Z);
                return $"r={rr:0.###} side={side:0.####} area={m.Area[i]:0.####}";
            }).ToArray();
        Assert.True(bad.Length == 0, "超大的孔周单元：" + string.Join(" | ", bad));
    }

    [Fact]
    public void 远场保持粗()
    {
        var g = Plate();
        var m = QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 8.0, 4.0);
        // 舌片远端（远离孔）应当还有较大的格子
        var far = Enumerable.Range(0, m.CellCount)
            .Where(i => m.Centroid[i].X < -45).ToArray();
        Assert.NotEmpty(far);
        Assert.True(far.Max(i => Math.Sqrt(m.Area[i])) > 1.0,
            "远场全被细化了 —— 局部加密没起作用");
    }

    /// <summary>
    /// ★★ **守恒**：每个单元的面长之和 = 它的周长。
    /// T 形接头处粗单元那条边被两段部分面铺满，加起来必须正好等于整条边。
    /// </summary>
    [Fact]
    public void 每个单元的面长之和等于周长()
    {
        var g = Plate();
        var m = QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 8.0, 4.0);

        var sum = new double[m.CellCount];
        foreach (var f in m.Faces)
        {
            if (f.A >= 0) sum[f.A] += f.Length;
            if (f.B >= 0) sum[f.B] += f.Length;
        }
        for (int i = 0; i < m.CellCount; i++)
        {
            // 单元是正方形；边长由节点坐标取（面积带覆盖率，不能直接开方）
            var nd = m.Cells[i];
            double side = (m.Nodes[nd[1]] - m.Nodes[nd[0]]).Norm;
            Assert.Equal(4 * side, sum[i], 6);
        }
    }

    /// <summary>2:1 平衡：任意两个共边的单元，边长比不超过 2。</summary>
    [Fact]
    public void 相邻单元边长比不超过二()
    {
        var g = Plate();
        var m = QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 8.0, 4.0);

        foreach (var f in m.Faces)
        {
            if (f.B < 0) continue;
            double sa = (m.Nodes[m.Cells[f.A][1]] - m.Nodes[m.Cells[f.A][0]]).Norm;
            double sb = (m.Nodes[m.Cells[f.B][1]] - m.Nodes[m.Cells[f.B][0]]).Norm;
            double ratio = Math.Max(sa, sb) / Math.Min(sa, sb);
            Assert.True(ratio <= 2.0 + 1e-6,
                $"相邻单元边长比 {ratio:0.##} > 2 —— 2:1 平衡没做到，"
                + "非协调面的一阶误差会失控");
        }
    }

    /// <summary>
    /// ★ **省了多少** —— 与张量网格同一个分区规则对比。
    /// 这是四叉树存在的全部理由，所以它必须是一条会红的门，不是一句话。
    /// </summary>
    [Fact]
    public void 比张量网格少用单元()
    {
        var g = Plate();
        // 张量网格：孔周 0.5 要求 ⇒ 细带只能用区间表达 ⇒ ±29 全细
        var tensor = FlangeMesher.Build(g, 0, 0.5, 4.0, 29.0, 4.0);
        var quad = QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 8.0, 4.0);

        Assert.True(quad.CellCount < tensor.CellCount,
            $"四叉树 {quad.CellCount} 单元 vs 张量 {tensor.CellCount} —— 没省，白做");
    }

    /// <summary>节点要去重 —— 不去重的话相邻单元不共节点，面拓扑会全判成边界。</summary>
    [Fact]
    public void 节点被共享()
    {
        var g = Plate();
        var m = QuadMesher.Build(g, 0, Zoned(1.0, 4.0, 29.0), 8.0, 4.0);
        // 每格 4 个角；若完全不共享则节点数 = 4×单元数
        Assert.True(m.Nodes.Count < 4 * m.CellCount,
            "节点没有被共享 —— 面拓扑会把所有边判成边界，界面全变绝热墙");
        Assert.True(m.FaceCounts().interior > 0, "一个内部面都没有 —— 网格是散的");
    }

    /// <summary>hAt 给非正数或 rootSize 非正时**当场炸**，不许悄悄退化成粗网格。</summary>
    [Fact]
    public void 参数不合法当场炸()
    {
        var g = Plate();
        Assert.Throws<ArgumentNullException>(() => QuadMesher.Build(g, 0, null!, 8.0, 4.0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QuadMesher.Build(g, 0, Zoned(0.5, 4.0, 29.0), 0, 4.0));
    }
}
