using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// **求解器不许有种子** —— 把「解与初值无关」钉成一道门。
///
/// 用户两次强调同一件事：
///   「不能再用种子的形式，只能用 APP 自己第一性原理计算所得到的结果」
///   「用种子这种方法绕了大弯路，**要杜绝病根**」
///
/// 病根的机械形式很具体：<see cref="Sizer"/> 是**增量行走 + 在走过的点里挑最轻的**。
/// 那种结构**必须**有个出发点，于是「种子」这个概念删不掉 —— 删掉参数也只是换个名字。
///
/// <see cref="Solver"/> 换掉的是**结构**：从约束盒的**下角**出发、每个旋钮**只增不减**、
/// 每一步的增量由**二分求根**定死。于是
///   · 出发点不是「挑」的，是约束集自己的角点（屈曲 / 烧穿 / 裸露 / 无台阶）
///   · 终点是**最小的可行点**（单调映射的最小不动点），与传进来的旋钮值无关
///
/// ⚠ 这里验的是**结构不变式**，不是数值。数值实证太慢（一次求解要跑几十次场解，
///   几分钟起），放在 `--solve --seedprobe`：同一几何、两组差得离谱的入参旋钮，
///   必须给出**逐位相同**的答案。门里跑的是下面这些「结构没被改回去」的验。
/// </summary>
public class SolverIsSeedFreeTests
{
    private static string Src(string f) =>
        File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", f));

    /// <summary>
    /// ★ 核心不变式：三个旋钮在**第一次求解之前**就被盒的下角覆盖掉。
    /// 只要这段还在 <c>Eval</c> 之前，传进来的旋钮值就到不了任何计算。
    /// </summary>
    [Fact]
    public void 三个旋钮在第一次场解之前就被下界覆盖()
    {
        string s = Src("Solver.cs");
        int overwrite = s.IndexOf("d.TabThickMm[j] = tLo;", System.StringComparison.Ordinal);
        int firstEval = s.IndexOf("Eval(d, baseIn, res, cancel)", System.StringComparison.Ordinal);

        Assert.True(overwrite > 0, "板厚没有被下界覆盖 —— 那就是拿传进来的值当起点了");
        Assert.True(firstEval > 0, "找不到求解调用");
        Assert.True(overwrite < firstEval,
            "★ 旋钮覆盖必须发生在第一次场解之前，否则传进来的值会参与计算 —— 那就是种子");

        foreach (var knob in new[] { "d.TabInsulMm[j] = opt.InsLoMm;", "d.RingMul[j]    = opt.RingLo;" })
            Assert.True(s.IndexOf(knob, System.StringComparison.Ordinal) is int i && i > 0 && i < firstEval,
                $"「{knob}」没有在首次场解前落位");
    }

    /// <summary>
    /// 起点的每一个分量都得说得出**第一性原理来源**，否则它就是挑出来的数（＝种子换皮）。
    /// </summary>
    [Fact]
    public void 起点三个分量各有出处而不是挑的()
    {
        string s = Src("Solver.cs");
        Assert.Contains("DiscFloorMm", s);                 // 板厚下界 = max(焊接屈曲, 烧穿)
        Assert.Contains("裸舌", s);                         // 保温下界 = 什么都不缠
        Assert.Contains("无台阶", s);                       // 环倍率下界 = 环不加厚
        Assert.Contains("不是种子", s);
    }

    /// <summary>
    /// ★ **只增不减**。任何一处把旋钮往回调，单调性就没了，
    /// 「收敛到最小可行点」这句话立刻不成立，解又会变得跟路径有关。
    /// </summary>
    [Fact]
    public void 旋钮只许往上走()
    {
        string s = Src("Solver.cs");

        // 二分里把 lo 复位是允许的（那是**放弃**这次抬升，回到抬之前），
        // 除此之外不该出现「往下调」的写法。
        Assert.DoesNotContain("-= ", s);
        Assert.DoesNotContain("Math.Min(Get(d", s);

        // 二分收在「不违反那一侧」，再**向上**对齐到图纸格 —— 两步都只会往上
        Assert.Contains("if (Slack(Eval(d, baseIn, res, cancel), key) >= 0) hi = mid; else lo = mid;", s);
        Assert.Contains("Math.Ceiling(hi / q - 1e-9) * q", s);
        Assert.DoesNotContain("Math.Floor(hi", s);      // 向下取整会把判据舍掉
    }

    /// <summary>
    /// ★★ 求解器**检查自己的前提**。二分只在「判据对该旋钮单调」时成立；
    /// 前提不成立却照样返回一个数，正是本项目最怕的错误形态 ——「看着正常的错数」。
    ///
    /// 这道验是被实测逼出来的：环倍率→②″ 那条分派，界面写「d②″/d倍率 ≈ −1.4」，
    /// 而 `--monotone` 实测是 **+0.056**（方向相反）。没有这道自检就会静默解错。
    /// </summary>
    [Fact]
    public void 分派前提不成立时必须报出来而不是返回一个数()
    {
        string s = Src("Solver.cs");
        Assert.Contains("分派前提不成立", s);
        Assert.Contains("after > before", s);      // 抬到上界必须让判据变好
        Assert.Contains("HitBound", s);            // 且要标成「不可行的证明」
    }

    /// <summary>判不了 = 不算过（三条铁律第 ③ 条）。求解器不能靠「判据消失」来收敛。</summary>
    [Fact]
    public void 判不了的判据算作违反()
    {
        string s = Src("Solver.cs");
        Assert.Contains("!c.Ok || c.Undetermined", s);
    }

    /// <summary>
    /// 裕度的方向必须看 <see cref="ConstraintOut.LessIsBetter"/>。
    /// 弄反了会把「越限」读成「有余量」，二分就会朝错的方向收 —— 本仓库出过这个错。
    /// </summary>
    [Fact]
    public void 裕度方向取自判据自己而不是写死()
    {
        string s = Src("Solver.cs");
        Assert.Contains("c.LessIsBetter ? c.Limit - c.Actual : c.Actual - c.Limit", s);
    }

    /// <summary>
    /// 分派表得是**可证伪的**：每条 (判据 → 旋钮) 都要经过 RaiseUntil 的前提自检。
    /// 旧 Sizer 的分派表是写死的信念，没有任何一处验它。
    /// </summary>
    [Fact]
    public void 分派表每条都要经过前提自检()
    {
        Assert.Equal(3, Solver.Allocation.Length);
        Assert.All(Solver.Allocation, a => Assert.False(string.IsNullOrWhiteSpace(a.Key)));

        // 三个旋钮各配一条，不许两条判据抢同一个旋钮（那样二分的不变式会互相破坏）
        Assert.Equal(3, Solver.Allocation.Select(a => a.Knob).Distinct().Count());

        string s = Src("Solver.cs");
        Assert.Contains("RaiseUntil(d, baseIn, opt, hit.Knob, hit.Key", s);
    }

    /// <summary>
    /// 没解决的事要**写在类注释里**，不能让人以为整个 A 类都拆完了。
    /// （四片分开解、量化、形状搜索这三条还在。）
    /// </summary>
    [Fact]
    public void 未解决的三条明写在文件里()
    {
        string s = Src("Solver.cs");
        Assert.Contains("这里**没有**解决的", s);
        Assert.Contains("四片各自的厚度还没分开解", s);
        Assert.Contains("固定步长模式搜索", s);

        // A⑤ 已拆：量化搬进求解过程里了，所以「未解决」清单里不该再有它，
        // 而**拆掉这件事本身**要留字，否则以后没人知道为什么解直接就在格子上。
        Assert.DoesNotContain("量化（0.01 mm）仍在解**之后**做", s);
        Assert.Contains("向上对齐到图纸格", s);
    }
}
