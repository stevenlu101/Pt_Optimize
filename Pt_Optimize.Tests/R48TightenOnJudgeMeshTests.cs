using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 门（2026-09-14，Opus 5 写）：
/// **第二遍求根之前，必须在判决网格上把第一遍抬过头的旋钮收回来。**
///
/// ══ 病历（实测，不是推论）
///
/// 求根跑两遍：第一遍导航网格（2.0 mm）定位，第二遍细网格重新求根。
/// 但第二遍**从第一遍的解出发**，而 `RaiseUntil` 的二分下界是 `lo = Get(d, knob, j)`（旋钮当前值）
/// ⇒ 只上不下；更要命的是它开头 `if (before >= 0) ⇒ 不抬` —— 第一遍抬过头之后那条判据已经过了，
/// 第二遍**根本不碰**这根旋钮。
///
/// 管壁 0.8，同一个设计点只换网格：
/// <code>
///                 管孔净流入      法兰增量温降        逐片抽热
///   导航 2.0 mm   +3.26（过）    9.02（裕 0.98）   +3.54/+3.47/+3.26/+3.78 W
///   细网格 0.5    −1.79（不过）  0.57（裕 9.43）   −0.37/−1.29/−1.79/+0.24 W
/// </code>
/// 第一遍把舌保温二分到「增量温降 ≈ 限值 10」（停在 9.02），而同一点在算得准的网格上只有 **0.57**
/// ⇒ 保温加过头一大截，把热压进了管子。
/// 第二遍随后对「管孔净流入」试遍全部候选，**一个都不成立**
/// （t₂ −1.693→−1.698 更差；r₂ 没台阶可挪；板厚 −1.693→**−1.742** 更差）⇒ 结构性停机。
/// 而把保温降回去（4.6/2.1/2.9/7.5 → 4.4/2.0/2.7/7.2）当场全过，**一克铂不花**。
/// ⇒ 卡住解的是「只增不减」，不是设计本身。
///
/// ══ 本组门钉什么
///
/// ① 这一步存在，且**排在第二遍之前**（排在后面等于没做）；
/// ② 二分的**下端是约束盒下角**（不是第一遍的值）—— 这是「解与初值无关」的实现方式；
/// ③ **板厚不在回收名单里**（它的下角是闭式的焊接屈曲／烧穿／按 J 截面，退下去会撞穿）；
/// ④ 回收**不许把已经过的判据弄不过**（目标函数就是「那些判据仍然全过」）；
/// ⑤ 场解判不了时**当作不成立**，不许当成「还过着」（判不了 ≠ 过，本仓库反复栽的形态）。
/// </summary>
public class R48TightenOnJudgeMeshTests
{
    private static string Src()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pt_Optimize.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "Pt_Optimize", "Core", "Solver.cs"));
    }

    [Fact]
    public void 回收这一步必须排在第二遍求根之前()
    {
        string s = Src();
        int tighten = s.IndexOf("TightenOnJudgeMesh(d, baseIn, opt, res", StringComparison.Ordinal);
        int pass2 = s.IndexOf("FinePass(\"第二遍：细网格上重新求根", StringComparison.Ordinal);   // 2026-09-25：F7′（§0.-26）把第二遍求根抽成 FinePass(...)，钉子随之改（变因 = F7′；原钉 Rounds(opt, "第二遍）
        Assert.True(tighten > 0, "找不到回收那一步的调用 —— 方法写了不等于接上了（本仓库栽过两次的形态）");
        Assert.True(pass2 > 0, "找不到第二遍求根的调用 —— 门失去了守护对象，先修门");
        Assert.True(tighten < pass2,
            "回收必须排在第二遍**之前**：排在后面时，第二遍已经带着偏高的根跑完了，回收无从谈起");
    }

    [Fact]
    public void 二分的下端必须是约束盒下角_不是第一遍的值()
    {
        string s = Src();
        int i = s.IndexOf("private static void TightenOnJudgeMesh", StringComparison.Ordinal);
        Assert.True(i > 0);
        string body = s.Substring(i, Math.Min(6000, s.Length - i));
        // ★ 2026-09-17，Opus 5（J 路 J8）：名单已提成公开 <see cref="PtOptimize.Core.Solver.ReclaimableKnobs"/>（纯搬移，逐位不变）。
        //   原来这里**手抄**「(Knob.Insul,  opt.InsLoMm)」那三行源码文本 —— 现改调生产那一份（门不许手抄生产配方）。
        Assert.Contains("var tunable = ReclaimableKnobs(opt);", body);   // 本函数不许再自己另写一份名单
        // 下角来自 SolverOptions 的 InsLoMm / RingLo，与 Solve 开头写下角用的是同一处：给两个与缺省不同的值，看它们真的被带出来
        var opt = new PtOptimize.Core.SolverOptions { InsLoMm = 0.37, RingLo = 1.19 };
        var tunable = PtOptimize.Core.Solver.ReclaimableKnobs(opt);
        Assert.Equal(new[] { PtOptimize.Core.Solver.Knob.Insul, PtOptimize.Core.Solver.Knob.Ring, PtOptimize.Core.Solver.Knob.RingT2 },
                     tunable.Select(x => x.K).ToArray());
        Assert.Equal(new[] { 0.37, 1.19, 1.19 }, tunable.Select(x => x.Lo).ToArray());
        // 二分区间的下端是 lo（下角），上端是 cur（第一遍的值，只是个上界提示）
        Assert.Contains("double a = lo, b = keep;", body);
    }

    [Fact]
    public void 板厚不许进回收名单()
    {
        // ★ 2026-09-17，Opus 5（J 路 J8）：原来在源码里找「var tunable = new (Knob K, double Lo)[]」那段文本再看有没有 Knob.Thick；
        //   名单提成公开函数之后改成**行为门** —— 直接问生产那一份要名单。门槛一个字没松（板厚仍然不许在内）。
        var list = PtOptimize.Core.Solver.ReclaimableKnobs(new PtOptimize.Core.SolverOptions());
        Assert.NotEmpty(list);                                    // 非空转：真的拿到了名单
        Assert.DoesNotContain(PtOptimize.Core.Solver.Knob.Thick, list.Select(x => x.K));
    }

    [Fact]
    public void 回收不许把已经过的判据弄不过()
    {
        string s = Src();
        int i = s.IndexOf("private static void TightenOnJudgeMesh", StringComparison.Ordinal);
        string body = s.Substring(i, Math.Min(6000, s.Length - i));
        Assert.Contains("mustPass", body);
        // 一条都没过就跳过 —— 那时没有要保护的东西，该走抬升那条路
        Assert.Contains("if (mustPass.Length == 0) continue;", body);
    }

    [Fact]
    public void 场解判不了时当作不成立()
    {
        string s = Src();
        int i = s.IndexOf("bool Holds(LineResult? r, int j, string[] mustPass)", StringComparison.Ordinal);
        Assert.True(i > 0, "找不到「还过着吗」那个判断");
        string body = s.Substring(i, 400);
        Assert.Contains("if (r is null) return false;", body);
        Assert.Contains("double.IsNaN(sl)", body);
    }
}
