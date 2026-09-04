using System;
using System.IO;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **搜形状：贵的是「可行点」，不是「点数」**（2026-09-04 实测）。
///
/// 实测出处 deliverable/F_测搜形状耗时.txt（给每个搜索点加了耗时列才量到）：
/// <code>
///   盘Ø120／舌80   1.3 分   8073 g   不可行（原图形状）
///   盘Ø55 ／舌41   1.1 分   2294 g   判不了（NaN）
///   盘Ø60 ／舌60  19.8 分   2584 g   ✓ 全过   ← 最轻的可行点
///   盘Ø60 ／舌45  33.0 分   2923 g   ✓ 全过
///   盘Ø70 ／舌53  24.5 分   3034 g   ✓ 全过
/// </code>
/// 失败的点 1 分钟就退出，**可行的点 20–33 分钟** ⇒ 单点差 30 倍。
///
/// ⇒ 粗筛把**平坦区**网格放粗（<see cref="SolverOptions.ScreenCoarseMm"/>）。
///
/// ⚠⚠ 本门真正要守的是**下面这条**：粗网格只许用来**筛**，
///    终点的精算必须回原网格 —— 交付的数不许出自粗网格。
///    这正是「把不准的数渲染成结论」那一类，本项目最贵的错。
/// </summary>
public class ShapeSearchCostTests
{
    private static string Src(params string[] rel) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), Path.Combine(rel)));

    /// <summary>★★★ 粗筛网格只在**没开细网格那一遍**时才生效，精算不受影响。</summary>
    [Fact]
    public void 粗筛网格不许影响精算()
    {
        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        // 必须是 else-if：FineMm > 0（精算）时**根本不看** ScreenCoarseMm
        Assert.Contains("else if (o.ScreenCoarseMm > 0)", s);
        Assert.Contains("lc.MeshCoarseMm = Math.Max(lc.MeshCoarseMm, o.ScreenCoarseMm", s);
        // 只动平坦区：孔边与台阶那圈（MeshFineMm / MeshFineRadiusMm）一格不许动
        int at = s.IndexOf("o.ScreenCoarseMm > 0", StringComparison.Ordinal);
        Assert.True(at > 0);
        string blk = s[at..Math.Min(s.Length, at + 400)];
        Assert.DoesNotContain("MeshFineMm = ", blk);
        Assert.DoesNotContain("MeshFineRadiusMm = ", blk);
    }

    /// <summary>★★ 精算那一遍必须**显式**带 FineMm（否则它会掉进粗筛那一支）。</summary>
    [Fact]
    public void 精算那一遍真的开了细网格()
    {
        string s = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        Assert.Contains("SearchScreenCoarseMm", s);
        // 粗筛点上带 ScreenCoarseMm
        Assert.Contains("ScreenCoarseMm = SearchScreenCoarseMm", s);
    }

    /// <summary>
    /// ★★★★ **超时不许被渲染成「做不到」**（2026-09-04）。
    ///
    /// 网格阶段已经找到 2584 g 全过的形状，而走查照旧报「✗ 超时」——
    /// 我据此对用户说了「APP 交不出方案」，**那是个假的失败结论**。
    /// 真界面上工程师点「取消」这些结果不会丢；是走查把「预算到了」当成了「做不到」。
    /// </summary>
    [Fact]
    public void 走查超时要报出已找到的全过形状()
    {
        string s = Src("tests", "UiWiring", "Walk.cs");
        Assert.Contains("已经找到全过的形状", s);
        Assert.Contains("不是「做不到」", s);
        // 判定要看输出框里真的有「✓ … 全过」，不是无条件安慰
        Assert.Contains("t.Contains(\"全过\", StringComparison.Ordinal)) anyOk = true;", s);
    }

    /// <summary>★ 自证：新选项默认不生效（0 = 不改网格），老行为一字不动。</summary>
    [Fact]
    public void 自证_默认不改变任何既有行为()
    {
        Assert.Equal(0.0, new SolverOptions().ScreenCoarseMm);
        Assert.Equal(0.0, new SolverOptions().Clone().ScreenCoarseMm);
    }

    /// <summary>
    /// ★★★★ **按钮名字只许有一份来源：Flow**（2026-09-04 撞到）。
    ///
    /// 我在 Flow 里把两颗按钮改名「（手动分步）」，而 LineDesignPage 里
    /// **三处写死了旧名**（构造时一处、跑完复位一处、流水线报名一处）⇒
    /// 界面接线测试当场五项红：「Flow 登记的命令界面上找不到」「界面上的按钮 Flow 没登记」。
    ///
    /// ⚠ 最阴的是「跑完复位」那一处：构造时名字是对的，**跑一次之后才被改回旧名** ——
    ///   静态看代码看不出来，抓图也未必抓得到（要先跑一次）。
    ///
    /// ⇒ 名字一律走 Flow.Cmd(id).Text。Flow.cs 自己写着 Id 才是稳定锚点。
    /// </summary>
    [Fact]
    public void 按钮名字一律从Flow读()
    {
        string s = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        foreach (string id in new[] { "core.runLine", "core.autoThick", "shape.search" })
            Assert.Contains($"Flow.Cmd(\"{id}\").Text", s);

        // 反面：这三颗按钮的名字不许再以字面量出现在**代码**里
        var code = s.Split(((char)10).ToString())
                    .Where(l => { var t = l.TrimStart();
                                  return !t.StartsWith("//", StringComparison.Ordinal)
                                      && !t.StartsWith("*", StringComparison.Ordinal); });
        foreach (string lit in new[] { "\"自动定厚\"", "\"◇ 搜形状\"" })
            Assert.DoesNotContain(code, l => l.Contains(lit, StringComparison.Ordinal));
    }

    /// <summary>
    /// ★★★★ **粗化这一刀试过了、不成立，默认必须是关的**（2026-09-04 实测）。
    ///
    /// 两条都不利，实测出处 deliverable/F_降本后.txt：
    ///   · 省不到：MeshFineRadiusMm = 50 而盘半径只有 27.5–35 ⇒ 整个圆盘都在细区，
    ///     圆盘上根本没有粗区。可行点 19.8–33.0 → 23.7–26.3 分钟，没降。
    ///   · 却改答案：粗区落在**舌片**上（舌长 158 mm）⇒ 盘Ø55/舌41 从「判不了(NaN)」
    ///     变成「⑥ 盘盖不住」。
    ///
    /// ⇒ **省不到钱却动了答案**，比单纯没用更糟。默认关掉。
    ///   哪天几何变了（盘大、舌短）它会重新成立 —— 那时要**重新量**过再打开，
    ///   而不是看着注释想当然。
    /// </summary>
    [Fact]
    public void 粗筛网格默认是关的()
    {
        string s = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        Assert.Contains("internal double SearchScreenCoarseMm = 0;", s);
        Assert.Contains("省不到钱，却动了答案", s);
    }
}
