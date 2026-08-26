using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// 第 1 轮网格里「造不出来的盘半径」要抬到下界上。
///
/// ★ 病灶（2026-08-25）：界面网格写死 {25, 30, 35}，判据⑥ 的下界 = 25 + 2×壁厚
///   ⇒ 壁 0.6 要 26.2、壁 0.8 要 26.6，**R25 对两个现役档都会被跳过**。
///   那一点从来没算过，第 1 轮实际只探了两个盘径 —— 网格少了三分之一，而没人知道。
///   偏偏下界那一侧正是最省铂的方向。
/// </summary>
public class LiveDiscsTests
{
    private static double Floor(double wallMm) => 25.0 + 2 * wallMm;

    [Fact]
    public void 自证_写死的25对两个现役档都造不出来()
    {
        // 没有这一条，下面每一条都是空转。
        Assert.True(25.0 < Floor(FinalDesign.W08.WallMm), "壁 0.8 的下界应高于 25");
        Assert.True(25.0 < Floor(FinalDesign.W06.WallMm), "壁 0.6 的下界应高于 25");
    }

    [Fact]
    public void 壁08_25被抬到27_其余不动()
    {
        var g = ShapeSearchPlan.LiveDiscs(new[] { 25.0, 30.0, 35.0 }, Floor(0.8));
        Assert.Equal(new[] { 27.0, 30.0, 35.0 }, g);
    }

    [Fact]
    public void 壁06_抬到265()
    {
        var g = ShapeSearchPlan.LiveDiscs(new[] { 25.0, 30.0, 35.0 }, Floor(0.6));
        Assert.Equal(new[] { 26.5, 30.0, 35.0 }, g);
    }

    [Fact]
    public void 网格仍是三个点_不是被删成两个()
    {
        // 「删掉造不出来的点」也能让每个点合法 —— 那是另一种修法，会让网格只剩两点。
        // 本条钉住选的是**抬上来**：点少一个，方向就少一个依据。
        Assert.Equal(3, ShapeSearchPlan.LiveDiscs(new[] { 25.0, 30.0, 35.0 }, Floor(0.8)).Length);
    }

    [Fact]
    public void 已经合法的点原样保留()
    {
        var g = ShapeSearchPlan.LiveDiscs(new[] { 30.0, 35.0, 40.0 }, Floor(0.8));
        Assert.Equal(new[] { 30.0, 35.0, 40.0 }, g);
    }

    [Fact]
    public void 抬上来与已有点重合时去重并升序()
    {
        var g = ShapeSearchPlan.LiveDiscs(new[] { 35.0, 25.0, 27.0 }, Floor(0.8));
        Assert.Equal(new[] { 27.0, 35.0 }, g);       // 25→27，与已有的 27 合并
    }

    [Fact]
    public void 空网格不返回空集_否则就是空集恒真那一族()
    {
        var g = ShapeSearchPlan.LiveDiscs(new double[0], Floor(0.8));
        Assert.Single(g);
        Assert.Equal(27.0, g[0]);
    }
}
