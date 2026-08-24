using System;
using System.Linq;

namespace PtOptimize.Core;

/// <summary>
/// 判据 ⑤⑥ 的**唯一实现** —— 闭式、纯几何、毫秒级。
///
/// ★★ 为什么要把它从 <see cref="LineRunner"/>.Judge 里拆出来（2026-08-20）：
///
/// 这两条判据**不需要解场**：给定盘半径、管孔半径、焊脚、舌长、切点、压接段，
/// 答案就定了。而界面的阶段门禁要在**跑分钟级整线解之前**就回答
/// 「这个几何造不造得出来」（业主：「前提还是要能造能用，省铂金是在这个前提下讨论的」）。
///
/// 在此之前，判据 ⑤ 存在**两份实现**：
///   · 判据版：LineRunner.Judge 里，下界取 <c>LineCase.FreeTabMinMm</c> = 100.0
///   · 页面版：LineDesignPage.ShowPrediction 的解析层，下界是自己的常数 <c>_freeTabMinMm</c> = 100.0
/// 同一条判据、同一个限值，存了两处。今天两边碰巧一致，**没有任何东西保证明天还一致**——
/// 这正是 HANDOVER §1.8 铁律三点名的形状：「判据只有一个来源……散在三处正是连错四次的根源」。
///
/// ⇒ 合成一份。Judge 调它，界面门禁也调它 ——
///   **界面上看到的 ⑤⑥，与整线跑完给出的 ⑤⑥，是同一段代码算的。**
///
/// ⚠ 搬迁分两步做，各自单独对过账（`--cli --selfcheck` 逐档比对）：
///   ① **逐字段照搬**，行为零变化 —— 两条 Undetermined 分支、措辞、限值、
///      Kind、LessIsBetter 一律未动。已证明输出逐字节相同。
///   ② 然后单独修一条**死代码**：参考量「· 舌宽被盘径夹住」原本写在 `.3dm` 分支里，
///      而那个分支下 plates 必然是空数组 ⇒ 它从来不可能触发。已挪进解析分支。
///      这一步**会**改变输出（解析档在半宽被夹时多出一行参考量），所以与 ① 分开做 ——
///      混在一起就分不清「搬迁搬错了」和「修的那条起作用了」。
/// </summary>
public static class GeometryScreen
{
    /// <summary>自由段下界的默认值 mm —— 与 <see cref="LineCase.FreeTabMinMm"/> 同源。</summary>
    public const double FreeTabMinDefaultMm = 100.0;

    /// <summary>
    /// 出 ⑥ 与 ⑤（外加可能的参考量）。顺序与原 Judge 中一致：先 ⑥ 后 ⑤。
    /// </summary>
    /// <param name="plates">解析几何的四片法兰。**空数组 = .3dm 模式** ⇒ 两条都返回
    /// 「无法判定」，绝不省略（铁律一：判据绝不允许消失）。</param>
    /// <param name="clampLenMm">压接段长度 mm（<c>DesignInputs.BusbarClampLengthMm</c>）。</param>
    /// <param name="freeMinMm">自由段下界 mm（<c>LineCase.FreeTabMinMm</c>）。</param>
    public static ConstraintOut[] Judge(FlangePlate[] plates, double clampLenMm, double freeMinMm)
    {
        var checks = new System.Collections.Generic.List<ConstraintOut>(3);
        plates ??= Array.Empty<FlangePlate>();

        // ── ⑥ 几何必须**造得出来**：圆盘要盖得住管孔，还要留得下焊脚
        //
        // 2026-08-17 抓到（跑界面「◇ 搜形状」用的那个网格时暴露）：
        //   盘 R25 + 管壁 0.8 ⇒ 管孔半径 = 25.8 > 盘半径 25 ——**孔比盘还大**，
        //   法兰压根焊不到管子上。而程序照样解、照样收敛、照样报
        //   「✓ 全判据通过　合计 3621 g」，还因为盘小、料少而**排在前面**。
        //
        // ⇒ 这是「安静失败」里最坏的一种：**不可造的几何反而看起来最优**，
        //   优化器会主动往那里跑。判据不写，搜索就一定会找到它。
        //
        // 下界取 孔半径 + 焊脚：焊脚 = max(板厚, 壁厚)，它必须落在盘面上才焊得住。
        if (plates is { Length: > 0 })
        {
            double worstRing = double.PositiveInfinity; string whereRing = "";
            string[] pn6 = { "入口", "共用1", "共用2", "出口" };
            for (int j = 0; j < plates.Length; j++)
            {
                var g6 = plates[j];
                double leg = Math.Max(g6.WeldFilletLegMm, 0);
                double ringW = g6.DiscRadiusMm - g6.HoleRadiusMm - leg;   // 焊脚外还剩多少盘
                if (ringW < worstRing) { worstRing = ringW; whereRing = j < pn6.Length ? pn6[j] : $"片{j + 1}"; }
            }
            checks.Add(new ConstraintOut
            {
                Name = "⑥ 圆盘盖得住管孔＋焊脚", Unit = "mm", Kind = CheckKind.HardSafety,
                Actual = worstRing, Limit = 0, LessIsBetter = false,
                Ok = worstRing >= 0, Where = whereRing,
                Note = "= 盘半径 − 管孔半径 − 焊脚（焊脚 = max(板厚, 壁厚)）。" +
                       (worstRing < 0
                        ? "★★ **负数 ⇒ 这个法兰造不出来**：圆盘盖不住管孔（或焊脚落在盘外），" +
                          "焊不到管子上。⚠ 这种几何**料最少**，所以优化器会主动往这里跑 —— " +
                          "判据不拦，搜索一定会找到它。" +
                          "　【下一步】放大圆盘直径，或减薄管壁（管孔半径 = 管壁 + 25）。"
                        : "圆盘在焊脚外还剩这么多料")
            });
        }
        else
        {
            // 同上：`.3dm` 模式下不能让 ⑥ 整条消失（见 ⑤ 的 else 分支）
            checks.Add(new ConstraintOut
            {
                Name = "⑥ 圆盘盖得住管孔＋焊脚", Unit = "mm", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = 0, Ok = false,
                Undetermined = true, Where = "—",
                Note = "★ **无法判定**：本次几何来自 .3dm 厚度场，没有解析的「盘半径」。" +
                       "**不要把它读成通过** —— 请在图上确认圆盘外缘比管孔至少大出一个焊脚" +
                       "（焊脚 = max(板厚, 壁厚)），否则法兰焊不到管子上。"
            });
        }

        // ── ⑤ 装配：舌片必须放得下铜排（2026-08-17 用户给出现场尺寸后新增）
        //
        // 为什么必须是**硬判据**而不是事后提醒：定案的舌长 90 mm 从来就装不下铜排 ——
        // 圆盘切点 26 + 压接段 + 自由段 已经超过 90，而程序此前
        //   ① 只把压接段当纯热电界面算（校核压接界面 J ≤ 1.0，从不问它靠什么固定）；
        //   ② `--busbarplan` 里自由段算出负数还 `Math.Abs` 取绝对值打印，
        //      把「装不下」显示成「装得下」。
        // ⇒ 一个**在设计上就不成立**的解，被当成可行解用了很久，还出了 3DM 和论文。
        //
        // 现场参考尺寸（用户 2026-08-17）：铜排长 100 mm、宽 60–80 mm；自由段基本留 100 mm。
        // 自由段不只是装配空间，它同时是**引线漏热的杠杆**（漏热 ∝ 1/ℓ，§4.3e）——
        // 太短会把管根抽冷，所以它在热学上也不该压缩。
        if (plates is { Length: > 0 })
        {
            double worstFree = double.PositiveInfinity; string whereFree = "";
            double worstTangent = 0;
            string[] pn5 = { "入口", "共用1", "共用2", "出口" };
            for (int j = 0; j < plates.Length; j++)
            {
                var g5 = plates[j];
                double tabLen = Math.Abs(g5.TabEndXMm);
                double tangent = Math.Abs(g5.Tangent().X);
                double free = tabLen - tangent - clampLenMm;
                if (free < worstFree)
                { worstFree = free; worstTangent = tangent; whereFree = j < pn5.Length ? pn5[j] : $"片{j + 1}"; }
            }
            checks.Add(new ConstraintOut
            {
                Name = "⑤ 舌片自由段 ≥ 下界", Unit = "mm", Kind = CheckKind.HardSafety,
                Actual = worstFree, Limit = freeMinMm, LessIsBetter = false,
                Ok = worstFree >= freeMinMm, Where = whereFree,
                Note = $"自由段 = 舌长 − 圆盘切点 − 压接段（{clampLenMm:0} mm）。" +
                       (worstFree < 0
                          ? "★★ **负数 ⇒ 压接段根本放不下**，压接块会伸进圆盘里。"
                          : worstFree < freeMinMm
                          ? "★ 装不下铜排：现场铜排长 100／宽 60–80 mm，自由段基本留 100 mm（用户 2026-08-17）。" +
                            "这不是余量不够，是**设计上不成立**。"
                          : "") +
                       " ⚠ 自由段同时是引线漏热的杠杆（∝1/ℓ）—— 压缩它会把管根抽冷。" +
                       (worstFree < freeMinMm
                        ? NextAction.FreeTabShort(worstTangent, clampLenMm, freeMinMm)
                        : "")
            });

            // ★★ 2026-08-20 **挪到这里**（原来写在下面的 `.3dm` 分支里）。
            //
            //   它原先待在 else 分支 —— 而进到那个分支就意味着 `plates` 是空数组，
            //   于是 `plates.Any(...)` 恒为 false ⇒ **这条判据从来没有、也不可能触发过**。
            //   而 `HalfWidthClamped` 只有解析几何才会置位（见 FlangePlate.Tangent 里那句赋值），
            //   也就是说它本来就该在这个 if 分支里。写在那边是纯粹的死代码。
            //
            //   它自己写明了为什么重要：「扫舌宽的后半段全是同一个几何，却给出一模一样的数，
            //   看着像『加宽没用』——**其实是根本没加宽**」。这个坑此前没有任何东西在拦。
            if (plates.Any(g => g.HalfWidthClamped))
                checks.Add(new ConstraintOut
                {
                    Name = "· 舌宽被盘径夹住", Unit = "—", Kind = CheckKind.Reference, Ok = true,
                    Actual = plates.Max(g => g.TabEndHalfWidthMm),
                    Limit = plates.Max(g => g.DiscRadiusMm), Where = "解析几何",
                    Note = "★ 半宽 > 盘半径 ⇒ 舌片与圆盘没有切点，半宽已被**静默夹到盘半径**。" +
                           "要真的加宽舌片，必须**同时放大圆盘** —— 这两个自由度是绑在一起的。"
                });
        }
        else
        {
            // ★★★★★ **判据绝不允许消失**（2026-08-17 补上；③ 早就有这个 else，⑤ 一直没有）。
            //
            // `.3dm` 模式下 FlangePlates 是空的（几何来自厚度场），于是上面整个 if 不执行
            // ⇒ 判据 ⑤ **整条不出现** ⇒ AllOk 少判一条还报「全过」。
            // 这与 §1.8 第 4 例（`if (有数据) checks.Add(...)` 让 ③ 整条消失）**一模一样**，
            // 只是换了个判据。铁律就写在 LineRunner ③ 那一段上：
            //   「判据消失比判据不过危险得多：不过会被看见，消失不会。」
            checks.Add(new ConstraintOut
            {
                Name = "⑤ 舌片自由段 ≥ 下界", Unit = "mm", Kind = CheckKind.HardSafety,
                Actual = double.NaN, Limit = freeMinMm, Ok = false,
                Undetermined = true, Where = "—",
                Note = "★ **无法判定**：本次几何来自 .3dm 厚度场，程序拿不到「圆盘切点」与「舌端」" +
                       "这两个解析量，算不出自由段。**不要把它读成通过** —— " +
                       "请自行在图上量：自由段 = 舌端到圆盘切点的距离 − 压接段 " +
                       $"{clampLenMm:0} mm，下界 {freeMinMm:0} mm。"
            });

            // （「· 舌宽被盘径夹住」原来写在这里 —— 那是死代码，2026-08-20 已挪到上面的
            //   解析分支。见那里的注释。）
        }

        return checks.ToArray();
    }
}
