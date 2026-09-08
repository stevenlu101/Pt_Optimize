using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **⑥ 没有旋钮，但它不需要搜索**（2026-08-29，第 2 组第 7 件：求解器 ↔ 搜形状联动）。
///
/// ══ 病在哪
///
/// <code>
///   ⑥  = 盘半径 − 管孔半径 − 焊脚 ≥ 0        焊脚 = max(板厚, 壁厚)
///   管孔半径 = 壁厚 + 25
/// </code>
///
/// **板厚正是求解器只往上抬的那个旋钮**。于是：
///
/// <code>
///   抬板厚（治 ②′）→ 焊脚变长 → ⑥ 的裕度一对一地掉
/// </code>
///
/// 而 <see cref="Solver"/> 里那句「抬高可能让别的判据变差 —— 由外层下一轮再抬它自己的
/// 旋钮补上」对 ⑥ **不成立**：它没有旋钮可补。求解器此前要等**三条逐片判据全过之后**
/// 才去看 ⑥ ⇒ 一整轮的板厚可能白抬。
///
/// ══ 修法：不是把求解器接进形状搜索的循环，是**把答案直接算出来**
///
/// ⑥ 是闭式的 ⇒ 对盘半径反解一次就得到确切的值，零成本、不用解场、不用试。
/// 求解器于是能在抬完板厚的当场给出**处方**（盘径要多大），而不是只报「没有旋钮能治」。
///
/// ⚠ 求解器**自己不改盘径**：盘径是形状，不是它的变量。改了就不再是「这个形状下的
///   最小可行点」。它只负责把处方交出去。
///
/// ══ 顺带修掉的一处「同一个数两处来源」
///
/// 界面搜形状的网格下界原本手写 <c>25.0 + 2 × 壁厚</c>，与 ⑥ 的实现各写各的。
/// 今天数值恰好相同（管孔 = 壁+25、焊脚下界 = max(烧穿 0.6, 壁)），
/// **没有任何东西保证明天还相同**。
/// </summary>
public class DiscCoverPrescriptionTests
{
    private static string Core(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    private static FlangePlate Plate(double discR, double holeR, double thick, double wall) => new()
    {
        DiscRadiusMm = discR, HoleRadiusMm = holeR,
        TabEndXMm = -140, TabEndHalfWidthMm = 30, ThicknessMm = thick,
        WeldFilletLegMm = Math.Max(thick, wall),
    };

    /// <summary>⑥ 的 Actual（= ringW）。</summary>
    private static double Six(params FlangePlate[] ps) =>
        GeometryScreen.Judge(ps, 40.0, GeometryScreen.FreeTabMinDefaultMm)
            .First(c => c.Name.StartsWith(LineResult.Key.DiscCover, StringComparison.Ordinal)).Actual;

    /// <summary>
    /// ★★ **反解与正解是同一个式子** —— 不抄公式，实测：
    /// 盘半径取到反解值时 ⑥ 恰好 = 0；再小一点就必须为负。
    ///
    /// 这条门的意义：两处若各写各的，漂开时**不会报错**，只会让形状搜索
    /// 在一个其实造不出来的盘径上花时间，或者相反 —— 把能造的点当成造不出来跳掉。
    /// </summary>
    [Theory]
    [InlineData(25.6, 1.75, 0.6)]     // 0.6 档、求解器解出来的典型板厚
    [InlineData(25.8, 2.45, 0.8)]     // 0.8 档、设计记录 W08 最厚的那片
    [InlineData(25.6, 0.30, 0.6)]     // 板厚 < 壁厚 ⇒ 焊脚由壁厚定
    public void 反解与判据六是同一个式子(double holeR, double thick, double wall)
    {
        double need = GeometryScreen.MinDiscRadiusMm(holeR, thick, wall);

        Assert.Equal(0.0, Six(Plate(need, holeR, thick, wall)), 9);
        Assert.True(Six(Plate(need - 0.01, holeR, thick, wall)) < 0,
            "盘半径比反解值小 0.01 mm，⑥ 竟然没变负 —— 两处的式子已经漂开了");
        Assert.True(Six(Plate(need + 0.01, holeR, thick, wall)) > 0);
    }

    /// <summary>
    /// ★★ **抬板厚一对一地吃掉 ⑥ 的裕度** —— 这就是求解器必须当场验它的全部理由。
    /// 板厚在壁厚之上时，每多 1 mm 板厚，就要多 1 mm 盘半径。
    /// </summary>
    [Fact]
    public void 抬板厚一对一吃掉裕度()
    {
        const double holeR = 25.6, wall = 0.6;
        double a = GeometryScreen.MinDiscRadiusMm(holeR, 1.00, wall);
        double b = GeometryScreen.MinDiscRadiusMm(holeR, 2.00, wall);
        Assert.Equal(1.000, b - a, 9);

        // 板厚还在壁厚之下时不吃 —— 焊脚由壁厚定，此时抬板厚不影响 ⑥
        Assert.Equal(GeometryScreen.MinDiscRadiusMm(holeR, 0.30, wall),
                     GeometryScreen.MinDiscRadiusMm(holeR, 0.50, wall), 9);
    }

    /// <summary>整组片取**最严的那一片** —— 与 <see cref="GeometryScreen.Judge"/> 取 worst 同口径。</summary>
    [Fact]
    public void 整组片取最严的那一片()
    {
        var ps = new[]
        {
            Plate(30, 25.8, 0.89, 0.8), Plate(30, 25.8, 2.45, 0.8),
            Plate(30, 25.8, 2.35, 0.8), Plate(30, 25.8, 0.73, 0.8),
        };
        Assert.Equal(25.8 + 2.45, GeometryScreen.MinDiscRadiusMm(ps), 9);
        // 与正解对上：worstRing = 盘 30 − 需要 28.25
        Assert.Equal(30.0 - (25.8 + 2.45), Six(ps), 9);
    }

    /// <summary>
    /// ★ 空数组返回 **NaN**，不返回 0 —— 0 会被读成「任何盘径都够」，
    /// 那是一条**恒真判据**，本仓库为「空集恒真」栽过。
    /// </summary>
    [Fact]
    public void 没有片时返回判不了而不是恒真()
    {
        Assert.True(double.IsNaN(GeometryScreen.MinDiscRadiusMm(Array.Empty<FlangePlate>())));
        Assert.True(double.IsNaN(GeometryScreen.MinDiscRadiusMm((FlangePlate[])null!)));
    }

    /// <summary>
    /// ★★ 求解器给的是**处方**，不是抱怨 —— 盘径要改到多大，当场说得出。
    /// </summary>
    [Fact]
    public void 盘径不够时给的是处方()
    {
        var d = DesignSpec.W08.Clone();
        var baseIn = new DesignInputs();
        d.DiscRadiusMm = 27.0;                        // < 需要的 28.25

        var (ok, why) = Solver.CoverCheck(d, baseIn);
        Assert.False(ok);
        Assert.Contains("【处方】", why);
        Assert.Contains("28.25", why);                // 确切的数，不是「放大一点」
        Assert.Contains("没有旋钮能治", why);
        Assert.Contains("闭式反解", why);             // 说清楚它不是搜出来的
    }

    /// <summary>盘径够时放行 —— 门不许两边都红。</summary>
    [Fact]
    public void 盘径够时放行()
    {
        var (ok, why) = Solver.CoverCheck(DesignSpec.W08.Clone(), new DesignInputs());
        Assert.True(ok, why);
        Assert.Equal("", why);
    }

    /// <summary>
    /// ★★ 抬板厚之后**当场**验 ⑥ —— 不许等三条逐片判据全过才发现。
    /// 等到那时，一整轮的板厚已经白抬了。
    /// </summary>
    [Fact]
    public void 抬完板厚当场验六()
    {
        string s = Core("Solver.cs");
        Assert.Contains("if (knob == Knob.Thick)", s);
        Assert.Contains("var (coverOk, coverWhy) = CoverCheck(d, baseIn);", s);
        // 而且是在**二分完、对齐到图纸格之后**验的 —— 验的必须是真正要用的那个板厚
        int snap = s.IndexOf("Set(d, knob, j, snapped);", StringComparison.Ordinal);
        int chk = s.IndexOf("var (coverOk, coverWhy)", StringComparison.Ordinal);
        Assert.True(snap >= 0 && chk > snap, "⑥ 的检查跑在量化之前 —— 验的不是真正要用的板厚");
    }

    /// <summary>
    /// ★★★ **端到端**：造不出来的几何，求解器在**任何场解之前**就停下并给处方。
    ///
    /// 上面几条验的是「代码在那儿」，这一条验的是「**它真的被走到**」——
    /// 本仓库为「造好了没接线」栽过不止一次，源码门挡不住那一种。
    ///
    /// ══ 这条门是被一次失败的实验改写的（照实记）
    ///
    /// 原本想验的是「抬板厚把 ⑥ 抬坏」那条路：盘 27.0、解完板厚需要 28.25。
    /// 实跑 2 分 57 秒，停在**另一句话**上：
    /// <code>
    ///   第 0 片的「②″圆盘区最高温」**判不了**（值是 NaN）—— 判不了不算过
    /// </code>
    /// 盘 27.0 减去孔 25.8，盘环只剩 1.2 mm ⇒ 导航网格（2 mm）**一格都落不进圆盘区**。
    ///
    /// 判据没错，错的是**顺序**：⑥ 是闭式的、零成本，却排在一次场解之后 ——
    /// 于是用户拿到的是「算不出来」，而真正的毛病是「**盘太小**」，
    /// 且那句话本来还带着能直接照做的处方。⇒ ⑥ 已前移到第一次场解之前。
    ///
    /// ⚠ 所以本条走的是**前置**那条路（瞬时、零场解）。
    ///   「抬板厚之后才越界」那条路由 <see cref="盘径不够时给的是处方"/> 与
    ///   <see cref="抬完板厚当场验六"/> 两条合起来守 —— 它要真跑就是分钟级，
    ///   放进 30 秒的套件里会让人把套件关掉，那样一条门都不剩。
    /// </summary>
    [Fact]
    public void 端到端造不出来的几何在解场之前就被停住()
    {
        var d = DesignSpec.W08.Clone();
        d.DiscRadiusMm = 25.0;                    // 孔 25.8 —— **孔比盘还大**（2026-08-17 的历史 bug）

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = Solver.Solve(d, new DesignInputs(), new SolverOptions { FineMm = 0 });
        sw.Stop();

        Assert.False(res.Feasible);
        Assert.Contains("⑥ 圆盘盖不住管孔", res.StopWhy);
        Assert.Contains("【处方】", res.StopWhy);
        // ★ 2026-09-08：下角多了「按 J=10 的截面」这一来源，起点板厚不再是 0.61（焊脚也不再是壁厚 0.8）。
        //   处方数 = 管孔 + 焊脚（焊脚 = max(起点板厚, 壁厚)），按 res.Design 里真正进模型的板厚闭式复算，不钉字面数。
        var mNeed = System.Text.RegularExpressions.Regex.Match(res.StopWhy, @"需要 ([0-9.]+) mm");
        Assert.True(mNeed.Success, "处方里没有「需要 X mm」：" + res.StopWhy);
        double need = double.Parse(mNeed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double expect = d.HoleRadiusMm + Math.Max(res.Design.TabThickMm.Max(), d.WallMm);
        Assert.Equal(expect, need, 2);

        // ★ **零场解**：这正是把 ⑥ 前移的全部理由。慢了就说明它又排到场解后面去了。
        Assert.Equal(0, res.Solves);
        Assert.True(sw.Elapsed.TotalSeconds < 5,
            $"用了 {sw.Elapsed.TotalSeconds:0.0} s —— ⑥ 是闭式的，不该解场");
    }

    /// <summary>
    /// ★ 顺序不许再倒回去：⑥ 的前置检查必须在**第一次 Eval 之前**。
    /// 一条不解场就能回答的判据，不该让一条要解场的判据先替它开口。
    /// </summary>
    [Fact]
    public void 六排在第一次场解之前()
    {
        string s = Core("Solver.cs");
        int pre = s.IndexOf("var (pre6Ok, pre6Why) = CoverCheck(d, baseIn);", StringComparison.Ordinal);
        int firstEval = s.IndexOf("last = Eval(d, baseIn, o, res, cancel, inner);", StringComparison.Ordinal);
        Assert.True(pre > 0, "⑥ 的前置检查不见了");
        Assert.True(firstEval > pre, "⑥ 排到场解后面去了 —— 那样报出来的会是「算不出来」而不是「盘太小」");
    }
    /// <summary>末尾「没有旋钮能治」那条也要带处方，只报名字等于把活推回给人。</summary>
    [Fact]
    public void 末尾也带处方()
    {
        string s = Core("Solver.cs");
        Assert.Contains("没有旋钮能治**（要改形状）", s);
        Assert.Contains("var (_, why6) = CoverCheck(d, baseIn);", s);
    }

    /// <summary>
    /// ★ 形状网格的下界不许再手写 —— 走 ⑥ 自己的闭式反解。
    ///
    /// ⚠ 这是**口径统一，数值不变**：两个现役档下两种写法给出同一个数，
    ///   下面一并验了。改动只消灭「两处来源」，不动答案。
    /// </summary>
    [Fact]
    public void 形状网格下界走唯一来源()
    {
        string ui = File.ReadAllText(Path.Combine(
            HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.DoesNotContain("25.0 + 2 * (double)_wall.Value", ui);
        Assert.Contains("GeometryScreen.MinDiscRadiusMm(", ui);

        // 数值不变：烧穿下界 0.6，两个现役档
        foreach (double wall in new[] { 0.6, 0.8 })
            Assert.Equal(25.0 + 2 * wall,
                GeometryScreen.MinDiscRadiusMm(wall + 25.0, new DesignInputs().WeldMinThicknessMm, wall), 9);
    }

    /// <summary>
    /// ★★ 实测那条 1.65 mm 的缝：形状网格的下界（26.6）离**解完板厚后**真正需要的
    /// 盘径（28.25）差 1.65 mm ⇒ 网格最低那一点解完必定被 ⑥ 判死。
    ///
    /// 这不是 bug，是**下界的性质**：板厚解完才知道最紧的那个。
    /// 门在这里的作用是把这件事**钉住**，免得日后有人把网格下界误当成可行下界。
    /// </summary>
    [Fact]
    public void 网格下界只是真下界不是可行下界()
    {
        const double wall = 0.8, holeR = wall + 25.0;
        double gridLo = GeometryScreen.MinDiscRadiusMm(holeR, new DesignInputs().WeldMinThicknessMm, wall);
        double real = GeometryScreen.MinDiscRadiusMm(holeR, DesignSpec.W08.TabThickMm.Max(), wall);

        Assert.True(gridLo < real, "网格下界不该 ≥ 实际需要 —— 那说明这条门自己已经过时");
        Assert.Equal(1.65, real - gridLo, 6);
    }
}
