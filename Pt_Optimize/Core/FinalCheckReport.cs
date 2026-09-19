using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★★★★★ 终验三关的**结论块**与**伸长报告**（2026-09-18，Opus 5）——
/// 输出框、安装报告都从这一份取，两处不许各写一遍。
///
/// ══ 三条规矩
///
/// ① **顺序写死**（用户 2026-09-15/16）：升温全程 → 带玻璃稳态 → 空管到温 → 铂重。
///    工程师读的是次序本身：第一关不过，后面两关的数再好看也没有意义。
///    门 <c>R48MThreeStateWiringTests</c> 按这四个名字在文本里出现的先后钉死。
/// ② **写全名，不写判据代号，也不写命令行开关名**（用户 2026-08-30「②′②″③ 工程师看不懂」）。
///    所以这里连「① ② ③」这种圈号都不用 —— 界面上另有阶段号与列表序号，形状一样含义无关。
/// ③ **没跑 ≠ 过**：某一关没跑就照实写「没跑」并说怎么打开它。
///
/// ⚠ 伸长这张表**一个数都不在这里算**：逐格原样印 <see cref="RampSegInfo.TotalElongMm"/> 等字段，
///   它们由 <see cref="RampSweep"/> 按 <see cref="PtThermalExpansion"/> 的牌号曲线算出。
///   报告层再算一遍就会有第二个来源，而那正是本项目反复栽的形态。
/// </summary>
public static class FinalCheckReport
{
    public const string Step1 = "升温全程";
    public const string Step2 = "带玻璃稳态";
    public const string Step3 = "空管到温";
    public const string Step4 = "铂重";

    /// <summary>
    /// 参数表里那一项的显示名 —— 界面上指路只许指它，不许提命令行开关。
    ///
    /// ⚠ 名字说的是「**补跑**那两关」而不是「跑齐三关」，两个理由：
    ///   ① 准确 —— 中间那一关（带玻璃稳态）本来就跑，这个开关管不着它，关掉也照样有；
    ///   ② 参数表的标签列宽是固定的，名字长了会被**裁掉**，而被裁掉的半句话比不写更坏
    ///      （R35 的教训：标签被切成「最小可制造壁厚 [mm」，单位恰恰是最不能猜的部分）。
    ///   三关的全名与次序写在这一项的说明里，结论块与安装报告也逐关写全名。
    /// </summary>
    public const string SwitchLabel = "终验时补跑升温全程与空管到温两关";

    /// <summary>安装报告里这一节的标题（报告与门共用一份写法）。</summary>
    public const string SectionTitle = "**7b. 三关结论与升温全程的伸长量**";

    /// <summary>伸长报告的抬头（报告与门共用）。</summary>
    public const string ElongTitle = "升温全程的伸长量（**只报数，不卡判据**）";

    private static string Short(LineResult? r)
        => r is null ? "没跑"
         : !r.Ok ? "判不了"
         : !r.Converged ? "判不了（外层耦合未收敛，场无效）"
         : r.AllOk ? "过" : "不过／判不了";

    private static string Detail(LineResult? r)
        => r is null ? ""
         : !r.Ok ? r.Message
         : !r.Converged ? $"剩余误差估计 {r.CoupleRemainK:0.000} K（停机容差 {r.CoupleTolKUsed:0.000} K，走了 {r.CoupleRounds} 轮）"
         : r.AllOk ? "全判据通过"
         : string.Join("；", r.Failed);

    /// <summary>
    /// 三关结论块（**按顺序**）。<paramref name="glass"/> 是带玻璃稳态那一份，
    /// 第一关与第三关从它挂着的 <see cref="LineResult.RampSweep"/>／<see cref="LineResult.EmptyTubeSteady"/> 读。
    /// </summary>
    public static string Conclusions(LineResult? glass)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**三关结论（按这个次序看：前一关不过，后面的数再好看也不算数）**");

        // ── 第一关 ──
        var ramp = glass?.RampSweep;
        string rampSkip = glass?.ThreeStateSkipped.FirstOrDefault(s => s.StartsWith(Step1, StringComparison.Ordinal)) ?? "";
        if (ramp is not null)
        {
            sb.AppendLine($"第一关　{Step1}：**{ramp.Verdict}**　"
                        + $"（{ramp.Points.Length} 个设定点：{string.Join("／", ramp.Points.Select(p => p.SetpointC.ToString("0")))} °C；"
                        + $"每点判场有效、管与截面的电流密度在设计电流与该点实际电流两条上都不超限；伸长只报数）"
                        + Mesh(ramp.Points.Length > 0 ? ramp.Points[0].LineRes : null));
            if (ramp.VerdictDetail.Length > 0)
                foreach (var line in ramp.VerdictDetail.Split('\n')) sb.AppendLine("　　" + line.TrimEnd());
            if (ramp.DisagreeNotes.Length > 0)
            {
                sb.AppendLine("　　设计电流那条与该点实际电流那条**结论不同**的点：");
                foreach (var n in ramp.DisagreeNotes) sb.AppendLine("　　· " + n);
            }
        }
        else if (rampSkip.Length > 0) sb.AppendLine($"第一关　{Step1}：**没跑** —— {rampSkip[(Step1.Length + 1)..]}");
        else sb.AppendLine($"第一关　{Step1}：**没跑**（这一次没有它的结果）。**没跑不等于过**。");

        // ── 第二关 ──
        sb.AppendLine($"第二关　{Step2}：**{Short(glass)}**　（法兰设计成不成由这一关定）" + Mesh(glass));
        if (glass is not null) sb.AppendLine("　　" + Detail(glass));

        // ── 第三关 ──
        var empty = glass?.EmptyTubeSteady;
        string emptySkip = glass?.ThreeStateSkipped.FirstOrDefault(s => s.StartsWith(Step3, StringComparison.Ordinal)) ?? "";
        if (empty is not null)
        {
            sb.AppendLine($"第三关　{Step3}：**{Short(empty)}**　"
                        + "（用户 2026-09-15 定：这一关只卡电流密度与场的有效性，热侧／冷侧／管孔净流入照常算、只作参考）"
                        + Mesh(empty));
            sb.AppendLine("　　" + Detail(empty));
        }
        else if (emptySkip.Length > 0) sb.AppendLine($"第三关　{Step3}：**没跑** —— {emptySkip[(Step3.Length + 1)..]}");
        else sb.AppendLine($"第三关　{Step3}：**没跑**（这一次没有它的结果）。**没跑不等于过**。");

        // ── 然后：铂重 ──
        sb.AppendLine(glass is null
            ? $"然后　{Step4}：没有结果。"
            : $"然后　{Step4}：合计 {glass.TotalMassG:0.00} g（管 {glass.TubeMassG:0.00} g + 法兰 {glass.FlangeMassG:0.00} g）。"
              + "　⚠ 三关没全过时，铂重只是「这一份不可用的设计有多重」。");

        // 机时量单独一行，明写不进判读
        var cost = new List<string>();
        if (glass is not null && !double.IsNaN(glass.RampSeconds)) cost.Add($"{Step1} {glass.RampSeconds:0} s");
        if (glass is not null && !double.IsNaN(glass.EmptyTubeSeconds)) cost.Add($"{Step3} {glass.EmptyTubeSeconds:0} s");
        if (cost.Count > 0) sb.AppendLine("　耗时（机时量，不进判读）：" + string.Join("；", cost) + "。");
        return sb.ToString();
    }

    private static string Cov(ExpansionCoverage c) => c switch
    {
        ExpansionCoverage.InRange => "区间内",
        ExpansionCoverage.InRangeNearEnd => "区间内·端部",
        ExpansionCoverage.EndDefinedPointInterp => "端部·定义点内插",
        ExpansionCoverage.Extrapolated => "**外推**",
        ExpansionCoverage.ShapeUntrusted => "**拟合形状不可信**",
        ExpansionCoverage.NoData => "**无数据**",
        _ => c.ToString(),
    };

    private static string Num(double v, string fmt = "0.000") => double.IsNaN(v) ? "—" : v.ToString(fmt);

    /// <summary>某一关**实际跑在哪张网格上** —— 从那一关自己的结果里读（不从选项里猜）。</summary>
    private static string Mesh(LineResult? r)
        => r is null ? "" : $"　网格：细区 {r.MeshFineMm:0.000} mm，单元 {r.MeshCells}";

    /// <summary>
    /// 升温全程的伸长报告：**逐设定点** × 每段管、每片法兰。
    /// 一个数都不在这里算 —— 逐格原样印 <see cref="RampSweep"/> 算出来的字段（见类头 ⚠）。
    /// </summary>
    public static string Elongation(LineResult? glass)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ElongTitle);
        var ramp = glass?.RampSweep;
        if (ramp is null || ramp.Points.Length == 0)
        {
            string skip = glass?.ThreeStateSkipped.FirstOrDefault(s => s.StartsWith(Step1, StringComparison.Ordinal)) ?? "";
            sb.AppendLine(skip.Length > 0
                ? $"  本次没跑{Step1} —— {skip[(Step1.Length + 1)..]}。要它：到「① 输入」页把「{SwitchLabel}」打开，再做一次加密复算到数不再变。"
                : $"  本次没有{Step1}的结果。要它：到「① 输入」页把「{SwitchLabel}」打开，再做一次加密复算到数不再变。");
            return sb.ToString();
        }

        sb.AppendLine($"  基准：安装温度 {Num(ramp.TAssemblyC, "0.#")} °C（伸长量都是「相对安装态」的差）；"
                    + $"牌号 {(ramp.Grade.Length == 0 ? "（没记）" : ramp.Grade)}（膨胀曲线按牌号取，出处随覆盖列带出）。");
        sb.AppendLine("  用户 2026-09-16：升温期**没有膨胀判据**，只报告膨胀量 ——「铜排是软连接，夹头能吃掉」。");
        sb.AppendLine("  这几个数是给现场留余量用的：管两端的固定方式、铜排夹头的活动量、保温层的搭接量。");
        sb.AppendLine();

        sb.AppendLine("  每段管（相对安装态的总伸长）：");
        sb.AppendLine("设定点 °C\t段\t该段实际电流 A\t管段总伸长 mm\t膨胀数据覆盖");
        foreach (var p in ramp.Points)
            foreach (var s in p.Segs)
                sb.AppendLine($"{p.SetpointC:0}\t{s.Name}\t{Num(s.CurrentA, "0")}\t{Num(s.TotalElongMm, "0.0000")}\t{Cov(s.ElongCoverage)}");
        sb.AppendLine();

        sb.AppendLine("  每片法兰（舌片与圆盘，相对安装态）：");
        sb.AppendLine("设定点 °C\t片\t舌尖径向总位移 mm\t舌板相对管的位移 mm\t圆盘外缘径向伸长 mm\t压接段伸长 mm\t膨胀数据覆盖");
        foreach (var p in ramp.Points)
            foreach (var f in p.Flanges)
                sb.AppendLine($"{p.SetpointC:0}\t{f.Name}\t{Num(f.TipTotalDisplacementMm, "0.0000")}\t{Num(f.TabDisplacementMm, "0.0000")}\t"
                            + $"{Num(f.DiscRadialMm, "0.0000")}\t{Num(f.ClampElongMm, "0.0000")}\t{Cov(f.ReportCoverage)}"
                            + (f.IsExtrapolated ? "（设定点高于 1000 °C，膨胀曲线在外推段）" : ""));
        sb.AppendLine();
        sb.AppendLine("  ⚠ 「—」= 那一点算不出来（场无效或几何取不到），**不是零**。");
        return sb.ToString();
    }

    /// <summary>安装报告那一节：标题 + 结论块 + 伸长表。界面输出框用的是同样这两块。</summary>
    public static string Section(LineResult? glass)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SectionTitle);
        sb.Append(Conclusions(glass));
        sb.AppendLine();
        sb.Append(Elongation(glass));
        return sb.ToString();
    }
}
