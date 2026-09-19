using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写；用户 09-14 拍板「改」）：
/// **圆盘区按半径圈，不按切点切一刀。**
///
/// ══ 为什么改
///
/// 旧规则 <c>onTab = 双舌片 ? |x| &gt; |切点x| : x &lt; 切点x</c>。
/// 两个内置设计**舌半宽 = 盘半径**（都是 30）⇒ 切点正好在 **x = 0** ⇒ 整个 x&lt;0 半边算舌片，
/// 圆盘区只剩 +x 半边。实测（管壁 0.8，09-13 解出的那一点，导航网格）：
/// <code>
///   圆盘区 − 管温  −0.482 / −0.243 / −0.224 / −0.409 K   ← ②″ 看的就是这一列，轻松过
///   舌片区 − 管温  +16.588 / +12.872 / +9.080 / +8.434 K ← 全部超限值 5 K，却不在判据里
///   舌片区峰位 r = 31～33 mm（盘半径 30、孔半径 25.8）—— 离孔只有 5～7 mm
/// </code>
/// 代码原本排除舌片的理由是「舌片离管子几十毫米、中间还隔着圆盘」——这个形状下不成立。
/// 一条**硬安全线**在半个零件上是瞎的。
///
/// ⛔ **上面这段理由已撤回（2026-09-14，Opus 5 记）**：舌片区那个 +16.6 K 的峰在 r = 31～33、x = −31～−33，
///   保温分界 x = 0 ⇒ 它在保温底下，厚度 = 舌片厚，**就是舌片本身**——恰恰是判据有意排除的东西，不是盲区。
///   按半径分区这个改动**不撤**，真正的理由是：x&lt;0 那半圈环（r 25.8–30）几何上属于圆盘，按 x 划把它算成舌片讲不通。
///   而改完后实测，那半圈环在求解器落点上是 +7～+12 K（峰位 r≈29、x=−29），超过限值 5 ——
///   旧口径恰好瞎在这半边（出处 R48_圆盘区判据覆盖_2026-09-13.txt）。
///
/// ══ 改动边界（本组门要钉死的就是这个边界）
///
/// 同一个 <c>onTab</c> 原本被两件事共用，本次**只改第一件**：
///   ① 分区热账 + 圆盘区最高温 ⇒ 改按半径（问的是「离管子近不近」）
///   ② 局部热稳定的候选预筛 ⇒ **逐位不动**（问的是「冷却侧一样不一样」，该按保温也就是按 x）
/// 把两件事绑在一个判断上正是这个洞的根；拆开之后局部热稳定的任何数都不受影响。
///
/// ⚠ **补注（2026-09-14 下午，Opus 5 记）**：保温边界随后也改按半径（R48InsulByRadiusTests）。
///   预筛问的是「冷却侧」，冷却侧就是保温，所以它**跟着保温走**：保温按半径时取 !insulated[i]，
///   保温仍按 x 时保留旧式子逐位不变。下面那条门钉的变量名与 if 分支仍成立。
/// </summary>
public class R48DiscZoneByRadiusTests
{
    private static string Src(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    /// <summary>分区必须按半径算，且盘半径要真的接进 ShellThermal —— 赋了值不等于用它的人读得到。</summary>
    [Fact]
    public void 圆盘区按半径圈且盘半径真的接进来了()
    {
        string th = Src("Pt_Optimize/Core/ShellThermal.cs");
        Assert.Contains("double discRadiusMm = double.NaN", th);
        Assert.Contains("bool byRadius = discRadiusMm > 1e-9", th);
        Assert.Contains("> discRadiusMm", th);          // 盘外 = 舌片区

        string lr = Src("Pt_Optimize/Core/LineRunner.cs");
        // ★ R48（2026-09-14，Opus 5）有意改断言：旧「discRadiusMm: discRForZone」→ 新「s.DiscRadiusMm = discRForZone」＋「discRadiusMm: s.DiscRadiusMm」。
        //   原因：审查意见「门手抄了逐片热解配方」，逐片热解输入组装搬进 LineRunner.PlateThermalInputs、调用搬进 LineRunner.SolvePlateThermal（纯搬移），
        //   接线从局部变量改经 PlateThermalSetup 字段过手；两段合起来仍是「盘半径真的接进 ShellThermal」。依据文件：Pt_Optimize/Core/LineRunner.cs。
        Assert.Contains("s.DiscRadiusMm = discRForZone", lr);       // 接线在这（组装）
        Assert.Contains("discRadiusMm: s.DiscRadiusMm", lr);        // 接线在这（进 ShellThermal）
        Assert.Contains("discRForZone = plate.DiscRadiusMm", lr);   // 解析路径
        Assert.Contains("discRForZone = eqJ.DiscRadiusMm", lr);     // 图纸路径
    }

    /// <summary>
    /// 局部热稳定的候选预筛必须**还按保温那条旧规则**走。
    /// 顺手把它一起改掉，就是在一次改动里悄悄动了两条判据 —— 那种改法事后没人分得清谁动了谁。
    /// </summary>
    [Fact]
    public void 局部热稳定的候选筛选不许跟着改()
    {
        string th = Src("Pt_Optimize/Core/ShellThermal.cs");
        Assert.Contains("bool insulOnTab", th);
        Assert.Contains("if (insulOnTab) candT.Add((proxy, i)); else candD.Add((proxy, i));", th);
        // 候选表不许再用新口径的 onTab
        Assert.DoesNotContain("if (onTab) { ... candT", th);
    }

    /// <summary>
    /// 退回旧口径**必须看得见**。拿不到盘半径时会退回按切点分，那时圆盘区可能只盖半边 ——
    /// 静默退回正是本仓库反复栽的形态，所以规则字串要一路带到判据说明里。
    /// </summary>
    [Fact]
    public void 退回旧口径必须写进判据说明()
    {
        string th = Src("Pt_Optimize/Core/ShellThermal.cs");
        Assert.Contains("DiscZoneRule", th);
        // 2026-09-14 改：这句会进界面，原字串带修订号与「口径」这类内部词（物理把关人查出），改成工程师读得懂的话。
        Assert.Contains("可能只圈到半个圆盘", th);
        Assert.DoesNotContain("R48 口径", th);

        string lr = Src("Pt_Optimize/Core/LineRunner.cs");
        Assert.Contains("DiscZoneRule = th.DiscZoneRule", lr);      // 从场解带到逐片结果
        Assert.Contains("分区口径：{hottestDisc.DiscZoneRule}", lr); // 再带到 ②″ 的说明
    }

    /// <summary>
    /// 数值门：两个现役内置档的**舌半宽恰好等于盘半径**，正是旧口径失效的那种几何。
    /// 这一条把「为什么非改不可」钉在几何上，而不是钉在我某一次的实测数字上 ——
    /// 几何哪天变了（舌比盘窄），这条门会红，提醒重新评估这次口径变更还需不需要。
    /// </summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.6)]
    public void 现役内置档的舌半宽等于盘半径_切点落在零(double wallMm)
    {
        var d = (wallMm > 0.7 ? DesignSpec.W08 : DesignSpec.W06).Clone();
        var p = new DesignInputs();
        double floor = d.DiscFloorMm(p);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            var g = d.Plate(j, floor);
            Assert.Equal(g.DiscRadiusMm, g.TabEndHalfWidthMm, 6);
            Assert.Equal(0.0, g.Tangent().X, 6);
        }
    }
}
