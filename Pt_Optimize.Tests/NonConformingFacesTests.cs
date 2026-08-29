using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **悬挂节点处的面必须配上** —— 局部加密（四叉树）的前置条件
/// （2026-08-29，用户：「根治要走四叉树局部加密」）。
///
/// ══ 为什么这条排在写生成器之前
///
/// <see cref="ShellMesh.BuildFaces"/> 原本**只按节点索引对**配面：
/// <code>
///   var key = n0 &lt; n1 ? (n0, n1) : (n1, n0);
///   if (edge.TryGetValue(key, out int other)) → 内部面   else → 边界面
/// </code>
/// 而局部加密会产生**悬挂节点**：粗单元的边是 (A,B)，两个细单元的边是 (A,M) 与 (M,B)
/// —— **三条谁也配不上谁**，于是全被判成边界面（<c>B = −1</c>）。
///
/// ⇒ <b>每一条加密界面都会变成绝热墙。不报错，只是答案错。</b>
///   正是本项目最怕的形态。所以先补面拓扑，再谈生成器。
///
/// ══ 做法与守恒
///
/// 轴对齐的边按「所在直线」分组，组内求区间重叠；每个重叠出一个**部分面**，
/// 长度取重叠段长。两段部分面加起来正好铺满粗边 ⇒ **通量守恒**。
/// 没被覆盖的残段才是真边界。
///
/// ⚠ 自检：每条边必须满足 <b>已配长度 + 边界长度 = 边长</b>；配重了当场炸 ——
///   重配会**静默**地放大通量。
/// </summary>
public class NonConformingFacesTests
{
    /// <summary>造一个 T 形接头：左边一个粗单元，右边上下两个细单元。</summary>
    private static ShellMesh TJunction()
    {
        var m = new ShellMesh();
        // 节点：粗单元 (0,0)-(1,0)-(1,2)-(0,2)；细单元共用 x=1 那条边上的 (1,0)(1,1)(1,2)
        m.Nodes.Add(new Vec3(0, 0, 0));   // 0
        m.Nodes.Add(new Vec3(1, 0, 0));   // 1
        m.Nodes.Add(new Vec3(1, 0, 2));   // 2
        m.Nodes.Add(new Vec3(0, 0, 2));   // 3
        m.Nodes.Add(new Vec3(1, 0, 1));   // 4  ← 悬挂节点
        m.Nodes.Add(new Vec3(2, 0, 0));   // 5
        m.Nodes.Add(new Vec3(2, 0, 1));   // 6
        m.Nodes.Add(new Vec3(2, 0, 2));   // 7

        m.Cells.Add(new[] { 0, 1, 2, 3 });      // 粗：x∈[0,1], z∈[0,2]
        m.Cells.Add(new[] { 1, 5, 6, 4 });      // 细下：x∈[1,2], z∈[0,1]
        m.Cells.Add(new[] { 4, 6, 7, 2 });      // 细上：x∈[1,2], z∈[1,2]

        m.Area.AddRange(new[] { 2.0, 1.0, 1.0 });
        m.Centroid.Add(new Vec3(0.5, 0, 1.0));
        m.Centroid.Add(new Vec3(1.5, 0, 0.5));
        m.Centroid.Add(new Vec3(1.5, 0, 1.5));
        m.Thickness.AddRange(new[] { 1.0, 1.0, 1.0 });
        m.Part.AddRange(new[] { 0, 0, 0 });
        return m;
    }

    /// <summary>★★ 粗单元与两个细单元之间必须各有一个内部面 —— 否则界面是绝热墙。</summary>
    [Fact]
    public void 悬挂节点处配出两个内部面()
    {
        var m = TJunction();
        m.BuildFaces();

        var pair01 = m.Faces.Where(f => f.B >= 0 &&
            ((f.A == 0 && f.B == 1) || (f.A == 1 && f.B == 0))).ToArray();
        var pair02 = m.Faces.Where(f => f.B >= 0 &&
            ((f.A == 0 && f.B == 2) || (f.A == 2 && f.B == 0))).ToArray();

        Assert.Single(pair01);
        Assert.Single(pair02);
    }

    /// <summary>
    /// ★★ **守恒**：两段部分面的长度加起来 = 粗边全长。
    /// 少了就是漏通量，多了就是放大通量 —— 两种都不报错，只是答案错。
    /// </summary>
    [Fact]
    public void 两段部分面加起来铺满粗边()
    {
        var m = TJunction();
        m.BuildFaces();

        double sum = m.Faces.Where(f => f.B >= 0 && (f.A == 0 || f.B == 0))
                            .Sum(f => f.Length);
        Assert.Equal(2.0, sum, 9);          // 粗边 z∈[0,2] 全长 2
    }

    /// <summary>细单元之间那条**协调**边照常按节点索引配上（不走几何路径）。</summary>
    [Fact]
    public void 协调边照常配上()
    {
        var m = TJunction();
        m.BuildFaces();
        Assert.Contains(m.Faces, f => f.B >= 0 &&
            ((f.A == 1 && f.B == 2) || (f.A == 2 && f.B == 1)));
    }

    /// <summary>
    /// ★ **协调网格必须逐位不变** —— 新增的几何配对只处理「索引配不上」的剩余边，
    /// 不许改动原本就配得上的那些。
    /// </summary>
    [Fact]
    public void 协调网格的面数不受影响()
    {
        var g = new FlangePlate
        {
            DiscRadiusMm = 30, HoleRadiusMm = 25.6,
            TabEndXMm = -60, TabEndHalfWidthMm = 20, ThicknessMm = 1.0,
        };
        var m = FlangeMesher.Build(g, 0, 2.0, 11.0, 45.0, 4.0);
        var (interior, boundary) = m.FaceCounts();

        Assert.True(interior > 0, "协调网格应当有内部面");
        Assert.True(boundary > 0, "协调网格应当有边界面");
        // 每个内部面被两个单元共享、每个边界面属一个单元 ⇒ 面数不会超过边总数
        int edges = m.Cells.Sum(c => c.Length);
        Assert.True(2 * interior + boundary <= edges + 1,
            "面数超过了边总数 —— 说明有边被配了不止一次");

        // ★★ 协调网格上几何配对应当**一个面都不配**。
        //   它若凭空配出内部面，那些原本是**边界面**（尤其管孔 TagHole），
        //   边界条件就被改掉了 —— 物理会全错，而且**不报错**。
        //   2026-08-29 拿它做过归因：实测为 0，排除了它。
        Assert.Equal(0, m.GeomPaired);
    }

    /// <summary>
    /// 只碰到端点（重叠长度为 0）不算面 —— 否则会凭空多出零长度的通量通道。
    /// </summary>
    [Fact]
    public void 只碰端点不算面()
    {
        var m = new ShellMesh();
        m.Nodes.Add(new Vec3(0, 0, 0)); m.Nodes.Add(new Vec3(1, 0, 0));
        m.Nodes.Add(new Vec3(1, 0, 1)); m.Nodes.Add(new Vec3(0, 0, 1));
        m.Nodes.Add(new Vec3(2, 0, 1)); m.Nodes.Add(new Vec3(2, 0, 2));
        m.Nodes.Add(new Vec3(1, 0, 2));
        m.Cells.Add(new[] { 0, 1, 2, 3 });        // x∈[0,1], z∈[0,1]
        m.Cells.Add(new[] { 2, 4, 5, 6 });        // x∈[1,2], z∈[1,2] —— 只在 (1,1) 点相接
        m.Area.AddRange(new[] { 1.0, 1.0 });
        m.Centroid.Add(new Vec3(0.5, 0, 0.5));
        m.Centroid.Add(new Vec3(1.5, 0, 1.5));
        m.Thickness.AddRange(new[] { 1.0, 1.0 });
        m.Part.AddRange(new[] { 0, 0 });
        m.BuildFaces();

        Assert.DoesNotContain(m.Faces, f => f.B >= 0);
    }
}
