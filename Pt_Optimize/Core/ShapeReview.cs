using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ **形状体检报告**（2026-08-17，用户提出）。
///
/// 用户的原话：「我给任意一个法兰形状，让 APP 自动帮我优化，并**有依据地**告诉我
/// APP 发现了什么？这个法兰的优点与缺点、或是要付出的代价。
/// **但前提还是要能造能用，省铂金是在这个前提下讨论的。**」
///
/// 这句话决定了报告的**结构**，不是措辞：
///
///  一、**先决条件先答**。能不能造、能不能用，是一个 yes/no 的闸门。
///      不过闸就到此为止 —— 不谈铂重、不谈优点。
///      （反例就在本项目里：舌长 90 mm 那版热学五条全过、铂重最轻，
///        而铜排根本装不上。把省铂放在可行性前面，就会得出那种结论。）
///  二、**每一句话都要挂一个数**。「这个法兰散热好」不是结论，
///      「②″ 裕度 104 %，峰位 r=28.1 mm、局部 J=0.00」才是。
///      本项目的教训是手推的判断错过三次（memory：法兰峰值先量再说）。
///  三、**优点与代价要成对出现**。省铂省在哪、代价付在哪，分开讲就成了广告。
///
/// ⚠ 本类**不做任何判定**，只**读** <see cref="LineResult.Checks"/> 与几何字段。
///   判据只有一个来源（<c>LineRunner.Judge</c>）—— 报告里再判一次，
///   就是本项目连错四次的那个形状（优化器在调 A、判据在判 B、报告在说 C）。
/// </summary>
public static class ShapeReview
{
    /// <summary>③/D 实测比例 K/W（`--window` 六行，D 从 +0.6 到 +144 W 吻合 5 % 以内）。</summary>
    public const double GammaKPerW = 2.40;

    /// <summary>裕度显示。数由 <see cref="ConstraintOut.MarginPct"/> 给 —— 本处不再自己算。</summary>
    private static string Pct(double pct) =>
        double.IsNaN(pct) ? "—" : $"{pct:0} %";

    /// <param name="d">优化后的设计（几何 + 三个旋钮的收敛值）</param>
    /// <param name="r">该设计的完整求解结果（判据表从这里读）</param>
    /// <param name="reference">参照档：目前已知最轻的可行解。null = 不做比较</param>
    /// <param name="sizerNote">定尺寸器自己的话（收敛/顶死/无解），原样带上</param>
    public static string Build(FinalDesign d, LineResult? r, FinalDesign? reference = null,
                               string sizerNote = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("══ 形状体检 ══");
        sb.AppendLine($"　盘Ø{2 * d.DiscRadiusMm:0}　舌 {d.TabLengthMm:0}×{2 * d.TabHalfWidthMm:0}　" +
                      $"管壁 {d.WallMm:0.0}　压接 {d.ClampLengthMm:0}");
        if (sizerNote.Length > 0) sb.AppendLine("　定尺寸器：" + sizerNote.Split('\n')[0]);
        sb.AppendLine();

        if (r is null || !r.Ok)
        {
            sb.AppendLine("✗ 这个形状**没解出来**：" + (r?.Message ?? "求解器没有返回结果"));
            sb.AppendLine("　⇒ 先让它算得出来，再谈好坏。**算不出来不等于不可行**，也不等于可行。");
            return sb.ToString();
        }
        if (!r.Converged)
        {
            sb.AppendLine("✗ **段↔法兰耦合未收敛 ⇒ 下面任何一个数都不能引用。**");
            sb.AppendLine("　⇒ 这不是「这个形状不好」，是「还不知道它好不好」。先加轮数或换起点。");
            return sb.ToString();
        }

        // ───────────────────────────────────────────────────────────
        // 一、先决条件：能不能造、能不能用
        // ───────────────────────────────────────────────────────────
        sb.AppendLine("一、能不能造、能不能用（**先决条件；不过这一关就不谈省铂**）");
        // ⚠ 「哪些硬安全线没过」只有一个来源：LineResult.HardBlocked。
        //   这里原来是**第二份实现**（`hard.Where(!Ok || Undetermined)`），规则虽然写对了，
        //   但它在**空集上放行** —— 判据表若整条没有硬安全线，blocked 为空 ⇒ 打印
        //   「✓ 能造、能用」。缺席被读成了通过，正是铁律三点名的形态。
        //   现在缺席由 r.MissingChecks 独立报出，通过与否只问 r.HardOk。
        var hard = r.Checks.Where(c => c.Kind == CheckKind.HardSafety).ToArray();
        var blocked = r.HardBlocked;
        var missing = r.MissingChecks;
        // 实测与限值原来挤在一格里写成「12.34 / 20.00」，是补空格年代的将就：
        // 一格一个数才能各自按列右对齐，扫一眼就知道离限值还有多远。
        foreach (var c in hard)
            sb.AppendLine($"　 {(c.Undetermined ? "?" : c.Ok ? "✓" : "✗")}\t{c.Name}\t" +
                          $"{(double.IsNaN(c.Actual) ? "达不到" : c.Actual.ToString("0.00"))}\t{c.Limit:0.00}\t" +
                          $"{c.Where}");
        double floorD = d.DiscFloorMm(new DesignInputs());
        bool atWeldFloor = d.TabThickMm.Any(t => t <= floorD * 1.02);
        sb.AppendLine($"　 · 板厚 vs 工艺下界 {floorD:0.00} mm（max(焊接屈曲, 烧穿)）：" +
                      $"最薄 {d.TabThickMm.Min():0.00} mm" + (atWeldFloor ? " ← **已贴住**" : ""));

        if (missing.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"✗ **判据表不完整** —— 该出现却整条没出现：{string.Join("、", missing)}");
            sb.AppendLine("　⇒ **不要把缺席读成通过。** 这一关根本没被检查过，"
                        + "在补齐之前这个形状的好坏无从谈起。");
            return sb.ToString();
        }
        if (blocked.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"✗ **这个形状现在不能用** —— {blocked.Length} 条硬安全线没过：");
            // ⚠ 「下一步」原来单独占一行。整句不带 \t ⇒ 它会把这张表从中间截断，
            //   前后两截各自算列宽，同一个判据的名字和数字就对不齐了。
            //   ⇒ 挪成同一行的最后一格：一条判据一行，信息也不拆散。
            foreach (var c in blocked)
            {
                int k = c.Note.IndexOf("【下一步】", StringComparison.Ordinal);
                string next = k >= 0 ? c.Note[k..].Split('。')[0] + "。" : "";
                sb.AppendLine($"　 ·\t{c.Name}\t{(c.Undetermined ? "无法判定" : $"{c.Actual:0.00}")}\t" +
                              $"{(c.Undetermined ? "" : $"{c.Limit:0.00}")}\t{next}");
            }
            sb.AppendLine("　⇒ **到此为止。** 省铂是在能造能用的前提下才讨论的（用户 2026-08-15）。");
            return sb.ToString();
        }
        sb.AppendLine("　⇒ ✓ 能造、能用。下面才谈省铂与取舍。");
        sb.AppendLine();

        // ───────────────────────────────────────────────────────────
        // 二、优化后的样子
        // ───────────────────────────────────────────────────────────
        double mass = r.TotalMassG;
        sb.AppendLine("二、优化后");
        sb.AppendLine($"　 板厚 {FinalDesign.Fmt(d.TabThickMm, "0.00")}　" +
                      $"舌保温 {FinalDesign.Fmt(d.TabInsulMm, "0.0")}　" +
                      $"环倍率 {FinalDesign.Fmt(d.RingMul, "0.00")}");
        sb.AppendLine($"　 **总铂 {mass:0} g**（管 {r.TubeMassG:0} + 法兰 {r.FlangeMassG:0}）");
        // ⚠ 「本形状就是参照档」时不要打一行 +0 g —— 那是**拿自己跟自己比**，
        //   看着像一条结论，实则零信息。这类空转的输出会稀释真正的发现。
        bool isRef = reference is not null
                     && Math.Abs(d.DiscRadiusMm - reference.DiscRadiusMm) < 1e-6
                     && Math.Abs(d.TabLengthMm - reference.TabLengthMm) < 1e-6
                     && Math.Abs(d.TabHalfWidthMm - reference.TabHalfWidthMm) < 1e-6
                     && Math.Abs(d.WallMm - reference.WallMm) < 1e-6;
        if (isRef)
            sb.AppendLine($"　 （本形状**就是当前定案档「{reference!.Name}」**，故不与自己比较）");
        else if (reference is not null && reference.TotalMassG > 0)
        {
            double dm = mass - reference.TotalMassG;
            // 负号一律用 ASCII 的 '-'，不用排版用的 U+2212 '−'：右对齐靠补空格实现，
            // 只在整格都是 ASCII 时「补一个空格 = 让一个字宽」才严格成立；
            // 混进一个 U+2212，那一列就会被判成文字列、退回左对齐。
            sb.AppendLine($"　 对比「{reference.Name}」{reference.TotalMassG:0} g：" +
                          $"**{dm:+0;-0} g（{dm / reference.TotalMassG * 100:+0.0;-0.0} %）**");
        }
        sb.AppendLine();

        // ───────────────────────────────────────────────────────────
        // 三、抽热窗口：这个形状站在窗口的哪一边
        //    这是最有信息量的一段 —— ②′ 与 ③ 是同一个量的两侧，
        //    位置直接说明「离哪个失效模式近」。
        // ───────────────────────────────────────────────────────────
        var draws = r.Flanges.Select(f => f.QFromTubeW).ToArray();
        double dMax = 10.0 / GammaKPerW;
        sb.AppendLine($"三、抽热窗口（②′ 与 ③ 是同一个量的两侧；实测 ③ = {GammaKPerW:0.00}·D）");
        sb.AppendLine($"　 安全区间 0 < D ≤ {dMax:0.0} W　本形状 D = " +
                      string.Join(" / ", draws.Select(v => v.ToString("+0.0;-0.0"))) + " W");
        double dLo = draws.Min(), dHi = draws.Max();
        sb.AppendLine(dLo < 0.8
            ? $"　 ⚠ 最低那片只有 {dLo:+0.0;-0.0} W，**贴着「热往管里灌」那一侧** —— 那是烧断的方向。"
            : dHi > dMax * 0.8
            ? $"　 ⚠ 最高那片 {dHi:0.0} W，**贴着「把管根抽出深坑」那一侧**（③ 限 10 K）。"
            : $"　 ✓ 四片都落在窗口中段（{dLo:0.0}–{dHi:0.0} W），两侧都有余量。");
        sb.AppendLine();

        // ───────────────────────────────────────────────────────────
        // 四、优点 / 五、缺点与代价 —— 由**裕度**和**旋钮余量**排出来，不由我说
        // ───────────────────────────────────────────────────────────
        // ⚠ 排「优点」之前先剔掉**无信息量**的判据，否则报告会把废话当发现：
        //   · ① 升温：本模型算的是**空管**（2.5 kg 铂几分钟就热透），实测 0.06 h vs 限 72 h，
        //     裕度 100 % 是**结构性**的，任何形状都这样 ⇒ 它不构成「本形状的优点」。
        //     （这条判据是否该改口径，已单独提给用户：72 h 大概率指整炉而非空管。）
        //   · ⑤ 自由段：舌长按 切点+压接+自由段下界 **算出来**，所以它必然贴着下界。
        //     裕度 0 % 是构造使然，不是「缺点」—— 但它确实意味着装配没有余量，要说清楚。
        bool Informative(ConstraintOut c) =>
            !c.Name.StartsWith("①", StringComparison.Ordinal) &&
            !c.Name.StartsWith("⑤", StringComparison.Ordinal);

        var judged = r.Checks.Where(c => c.Kind is CheckKind.HardSafety or CheckKind.Target
                                      && c.Ok && !c.Undetermined && Math.Abs(c.Limit) > 1e-9
                                      && Informative(c))
                             .OrderByDescending(c => c.MarginPct).ToArray();

        sb.AppendLine("四、优点（按裕度从宽到紧，**每条都带实测值**）");
        foreach (var c in judged.Take(3))
            sb.AppendLine($"　 ·\t{c.Name}\t{c.Actual:0.00}\t{c.Limit:0.00}\t" +
                          $"裕度 **{Pct(c.MarginPct)}**\t{c.Where}");
        if (d.RingMul.All(m => m <= 1.001))
            sb.AppendLine("　 · **不需要管孔渐变环**（倍率 1.00）⇒ 少一道两级台阶的机加工。" +
                          "　依据：②″ = " + r.ValueOf(LineResult.Key.DiscTemp).ToString("0.00") +
                          " K（限 5）—— 孔周电流没有拥塞。");
        if (d.TabInsulMm.All(v => v <= 1.0))
            sb.AppendLine("　 · **舌片几乎不用包保温**（" + FinalDesign.Fmt(d.TabInsulMm, "0.0") +
                          " mm）⇒ 现场少一道工序，且这个旋钮不花铂。");
        sb.AppendLine();

        sb.AppendLine("五、缺点 / 咬住它的（**这些就是代价的来源**）");
        var tight = judged.Reverse().Take(2).ToArray();
        foreach (var c in tight)
            sb.AppendLine($"　 ·\t{c.Name}\t{c.Actual:0.00}\t{c.Limit:0.00}\t" +
                          $"只剩 **{Pct(c.MarginPct)}**\t{c.Where}");
        // ⑤ 单独说：它贴着下界是**构造使然**，不是缺陷；但装配确实没有余量。
        sb.AppendLine($"　 · 自由段 {d.FreeTabMm:0.0} mm **正好贴着装配下界** —— 这是构造使然" +
                      "（舌长 = 切点 + 压接段 + 自由段下界，加长只多花铂），不是设计缺陷；" +
                      "但它意味着**装配没有余量**：现场铜排若比给定尺寸大，就装不下。");
        // 旋钮余量 —— 「还有没有回旋空间」的直接量
        var pinned = new List<string>();
        if (d.TabInsulMm.Any(v => v <= 0.35)) pinned.Add("舌保温已压到下界（≈裸舌），**这一侧没有余量了**");
        if (d.TabInsulMm.Any(v => v >= 79.0)) pinned.Add("舌保温已顶到上界");
        if (d.RingMul.Any(m => m >= 2.49)) pinned.Add("环倍率已顶到上限 2.5");
        if (atWeldFloor) pinned.Add($"板厚已贴住工艺下界 {floorD:0.00} mm（再薄焊不出来）");
        if (pinned.Count > 0)
            foreach (var s in pinned) sb.AppendLine("　 · " + s);
        else
            sb.AppendLine("　 · 三个旋钮（板厚 / 舌保温 / 环倍率）都还有余量 ⇒ 这个形状还有回旋空间。");
        sb.AppendLine();

        // ───────────────────────────────────────────────────────────
        // 六、代价：多花的铂换来了什么
        // ───────────────────────────────────────────────────────────
        if (!isRef && reference is not null && reference.TotalMassG > 0)
        {
            double dm = mass - reference.TotalMassG;
            sb.AppendLine("六、代价");
            if (dm > 1)
            {
                sb.AppendLine($"　 比「{reference.Name}」多用 **{dm:0} g 铂**。换来的是：");
                if (d.FreeTabMm > reference.FreeTabMm + 1)
                    sb.AppendLine($"　 · 自由段 {d.FreeTabMm:0.0} mm（对比 {reference.FreeTabMm:0.0}）⇒ 铜排更好装");
                // ⚠ 只陈述**量得到的**差异，不做机理断言。
                //   这里原来写「舌片更大 ⇒ 局部 J 更低、更不容易过热」——
                //   而实际跑出来的例子是 163×52 对比 140×60：**更窄**、面积几乎持平，
                //   那句因果就是凭空加的。本项目手推热行为已经错过三次
                //   （memory：法兰峰值先量再说）⇒ 报告里一句没有数撑着的因果都不留。
                if (Math.Abs(d.TabHalfWidthMm - reference.TabHalfWidthMm) > 0.5 ||
                    Math.Abs(d.TabLengthMm - reference.TabLengthMm) > 0.5)
                    sb.AppendLine($"　 · 舌片尺寸变了：{d.TabLengthMm:0}×{2 * d.TabHalfWidthMm:0} " +
                                  $"对比 {reference.TabLengthMm:0}×{2 * reference.TabHalfWidthMm:0} mm" +
                                  $"（面积 {d.TabLengthMm * 2 * d.TabHalfWidthMm / 100:0} " +
                                  $"vs {reference.TabLengthMm * 2 * reference.TabHalfWidthMm / 100:0} cm²）");
                if (Math.Abs(d.DiscRadiusMm - reference.DiscRadiusMm) > 0.5)
                {
                    double floorRef = reference.DiscFloorMm(new DesignInputs());
                    sb.AppendLine($"　 · 盘径 Ø{2 * d.DiscRadiusMm:0} 对比 Ø{2 * reference.DiscRadiusMm:0}：" +
                                  $"**工艺下界随之从 {floorRef:0.00} 变到 {floorD:0.00} mm**" +
                                  "（焊接屈曲下界随盘径线性增长）——" +
                                  (floorD > floorRef + 0.01
                                   ? "盘越大，板就**不许**做得太薄，这是大盘变重的主因之一。"
                                   : ""));
                }
                // 实测的局部量：好不好由这些数说，不由尺寸推
                var hotSpot = r.Flanges.OrderByDescending(f => f.DiscMaxJAPerMm2).FirstOrDefault();
                if (hotSpot is not null)
                    sb.AppendLine($"　 · 实测孔周峰值：r={hotSpot.DiscMaxRMm:0.0} mm、" +
                                  $"局部 J={hotSpot.DiscMaxJAPerMm2:0.00} A/mm²、" +
                                  $"②″={r.ValueOf(LineResult.Key.DiscTemp):0.00} K（{hotSpot.Name}）");
                sb.AppendLine("　 ⇒ **这笔账值不值，属于业主判断**：多的是铂钱，换的是装配与裕度。");
            }
            else if (dm < -1)
                sb.AppendLine($"　 比「{reference.Name}」**省 {-dm:0} g 铂**，且先决条件同样通过 ⇒ 这个形状更优。");
            else
                sb.AppendLine($"　 与「{reference.Name}」铂重相当（差 {dm:+0;-0} g）。");
            sb.AppendLine();
        }

        sb.AppendLine("⚠ 以上每一条都只是**读判据表**，没有另立标准。要看原始判据请见下方完整表格。");
        return sb.ToString();
    }
}
