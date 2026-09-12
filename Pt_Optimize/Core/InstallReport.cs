using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PtOptimize.Core;

/// <summary>
/// ★ R46（2026-09-12，用户原话：「APP添加一个呈现配套清单与系统安装报告」）：
/// 把一次整线解连同配套清单写成一份**给现场的安装报告**——整线概要、供电、逐片法兰、配套清单、保温、
/// 焊接与加工、升温与运行、判据结论、出图文件、待现场确认。
/// 文字用制表位表（界面输出框由 TextFmt 排成 Excel 式）；导出成 .md 时用 <see cref="ToMarkdown"/> 把制表位表转成管道表。
/// 只读结果与设计记录，**不自己重算任何数**（铁律①：判据只有一个来源）。
/// </summary>
public static class InstallReport
{
    public const string Title = "系统安装报告";

    public static string Build(LineResult r, DesignSpec d, DesignInputs p, string meshNote = "", DateTime? when = null)
    {
        if (r is null || d is null || p is null) return "";
        var sb = new StringBuilder();
        var t0 = when ?? DateTime.Now;
        var lc = d.BuildCase(p);
        int nBad = r.Checks.Count(c => c.Kind is CheckKind.HardSafety or CheckKind.Target && (!c.Ok || c.Undetermined));

        sb.AppendLine($"**{Title}** —— {d.Name}　（生成 {t0:yyyy-MM-dd HH:mm}）");
        sb.AppendLine(r.Ok && r.Converged && nBad == 0
            ? "判定：**全判据通过**" + (meshNote.Length > 0 ? "　" + meshNote : "")
            : $"判定：**{(r.Converged ? $"{nBad} 条判据没过" : "耦合未收敛")} —— 本报告不可作为安装依据**，先回「② 法兰优化」把它解到全过。");
        sb.AppendLine();

        // 1 整线概要
        sb.AppendLine("**1. 整线概要**");
        sb.AppendLine($"  {d.SetpointC.Length} 段铂管串联，{d.FlangeCount} 片法兰兼作电极（中间 {Math.Max(0, d.FlangeCount - 2)} 片共用）。");
        sb.AppendLine($"  管：内径 {p.TubeIdMm:0} mm／壁厚 {d.WallMm:0.00} mm／牌号 {p.GradeName}；管保温 {d.TubeInsulMm:0.0} mm。");
        sb.AppendLine($"  铂重：管 {r.TubeMassG:0} g + 法兰 {r.FlangeMassG:0} g = 合计 {r.TotalMassG:0} g。");
        sb.AppendLine("段\t控温点 °C\t段长 mm\t电流 A\t功率 W\t管 J A/mm²");
        for (int i = 0; i < r.Segments.Length; i++)
        {
            var s = r.Segments[i];
            double len = i < d.SegLengthMm.Length ? d.SegLengthMm[i] : double.NaN;
            sb.AppendLine($"{s.Name}\t{s.SetpointC:0}\t{len:0}\t{s.CurrentA:0}\t{s.PowerW:0}\t{s.TubeJAPerMm2:0.00}");
        }
        sb.AppendLine();

        // 2 供电
        sb.AppendLine("**2. 供电**");
        sb.AppendLine("  交流供电，相邻两段相位差 120°；共用片承担两侧电流的矢量和（√3 倍）。逐片电流见第 3 节，铜排规格见第 4 节。");
        sb.AppendLine($"  升温：空管按 {lc.RampRateKPerH:0} °C/h 升到目标温度，升温期限 {lc.RampHours:0} h；法兰尺寸按升温全程的峰值电流定（设计电流密度 {d.JDesignAPerMm2:0.#} A/mm²）。");
        sb.AppendLine();

        // 3 法兰逐片
        sb.AppendLine("**3. 法兰逐片**");
        sb.AppendLine($"  形状：{d.Describe()}");
        sb.AppendLine("片\t装在哪\t板厚 mm\t舌片厚 mm\t叉臂\t舌保温 mm\t环倍率\t圆盘槽 °\t舌孔\t铂重 g\t电流 A");
        double x = 0;
        for (int j = 0; j < r.Flanges.Length; j++)
        {
            var f = r.Flanges[j];
            string where = j == 0 ? "入口端 x=0" : j >= d.SetpointC.Length ? $"出口端 x={x:0}" : $"段间 x={x:0}";
            string hole = j < d.TabHoleRMm.Length && d.TabHoleRMm[j] > 1e-9
                ? $"{Solver.HoleShapeName(d.TabHoleSidesOf(j))} r{d.TabHoleRMm[j]:0.0}×{(j < d.TabHoleAspect.Length ? d.TabHoleAspect[j] : 1):0.00}"
                : "无";
            sb.AppendLine($"{f.Name}\t{where}\t{(j < d.TabThickMm.Length ? d.TabThickMm[j] : double.NaN):0.00}" +
                          $"\t{(j < d.TongueThickMm.Length ? d.TongueThickMm[j] : double.NaN):0.00}" +
                          $"\t{(d.HasTabArm(j) ? $"{d.TabArmThickMm[j]:0.00} mm × [{d.TabArmX0Mm[j]:0}, {d.TabArmX1Mm[j]:0}]" : "无")}" +
                          $"\t{(j < d.TabInsulMm.Length ? d.TabInsulMm[j] : double.NaN):0.0}" +
                          $"\t{(j < d.RingMul.Length ? d.RingMul[j] : 1):0.00}" +
                          $"\t{(j < d.SlotSpanDeg.Length ? d.SlotSpanDeg[j] : 0):0}" +
                          $"\t{hole}\t{f.MassG:0}\t{f.CurrentA:0}");
            if (j < d.SegLengthMm.Length) x += d.SegLengthMm[j];
        }
        sb.AppendLine();

        // 4 配套清单
        sb.AppendLine("**4. 配套清单（铜排／保温／焊接）**");
        var kit = FlangeKit.Build(r, d, p);
        sb.Append(FlangeKit.Text(kit, d, p));
        sb.AppendLine();

        // 5 保温
        sb.AppendLine("**5. 保温**");
        sb.AppendLine($"  管保温 {d.TubeInsulMm:0.0} mm（材料同参数表「② 中层」：{p.Layer1.Name}）；" +
                      (p.FlangeInsulated && p.FlangeInsulThickMm > 1e-6 ? $"圆盘双面包 {p.FlangeInsulThickMm:0.0} mm；" : "圆盘不包；") +
                      "舌片按上表逐片包，从圆盘切点到压接段前，压接段不包；" +
                      $"端部额外保温 {p.EndInsulExtraMm:0.0} mm × 长 {p.EndInsulLengthMm:0} mm。");
        sb.AppendLine("  舌保温是热平衡的主力旋钮，不花铂：各片厚度不同是算出来的，不要做成同一规格。");
        sb.AppendLine();

        // 6 焊接与加工
        sb.AppendLine("**6. 焊接与加工**");
        var thick = new List<string>();
        for (int j = 0; j < d.FlangeCount && j < d.TabThickMm.Length; j++)
        {
            var parts = new List<string> { $"板 {d.TabThickMm[j]:0.00}" };
            if (j < d.TongueThickMm.Length && !double.IsNaN(d.TongueThickMm[j]) && Math.Abs(d.TongueThickMm[j] - d.TabThickMm[j]) > 0.005) parts.Add($"舌 {d.TongueThickMm[j]:0.00}");
            if (d.HasTabArm(j)) parts.Add($"叉臂 {d.TabArmThickMm[j]:0.00}");
            if (j < d.RingMul.Length && d.RingMul[j] > 1.001) parts.Add($"环 ×{d.RingMul[j]:0.00}");
            thick.Add($"{(j == 0 ? "入口" : j == d.FlangeCount - 1 ? "出口" : "共用" + j)}：{string.Join("／", parts)} mm");
        }
        sb.AppendLine("  每片的厚度级：" + string.Join("；", thick) + "。一片里有几级厚度就要几级加工（台阶、叉臂、舌片各自一级）。");
        sb.AppendLine($"  管与圆盘：角焊缝两面各一道，焊脚 = max(板厚, 壁厚)；手工 TIG，烧穿下界 {DesignInputs.WeldMinDefaultMm:0.0} mm。舌片与圆盘同板切出，不焊。");
        sb.AppendLine($"  铜排压接：压接段 {d.ClampLengthMm:0} mm，两面夹；铜排规格与夹持温度见第 4 节。");
        sb.AppendLine();

        // 7 升温与运行
        sb.AppendLine("**7. 升温与运行**");
        var ramp = r.Checks.FirstOrDefault(c => c.Name.Contains("升温到位用时"));
        sb.AppendLine($"  升温速率 {lc.RampRateKPerH:0} °C/h；" + (ramp is not null ? $"升温到位用时（集总）{ramp.Actual:0.00} h（限 {ramp.Limit:0} h）；" : "") +
                      $"运行控温点：{string.Join("／", d.SetpointC.Select(v => v.ToString("0")))} °C。");
        sb.AppendLine("  升温期间共用片最先到温，注意法兰比管热的那一段（判据表「升温期法兰−管峰值」）。");
        sb.AppendLine();

        // 8 判据结论
        sb.AppendLine("**8. 判据表**" + (meshNote.Length > 0 ? $"（{meshNote}）" : ""));
        sb.AppendLine("判据\t实际\t限值\t单位\t判定\t位置");
        foreach (var c in r.Checks)
            sb.AppendLine($"{Criteria.Plain(c.Name)}\t{c.Actual:0.###}\t{c.Limit:0.###}\t{c.Unit}\t{(c.Undetermined ? "无法判定" : c.Ok ? "过" : "不过")}\t{c.Where}");   // 判据名走 Criteria.Plain：界面不许出现判据代号
        sb.AppendLine();

        // 9 出图与文件
        sb.AppendLine("**9. 出图与文件**");
        sb.AppendLine("  加工图：「③ 结果与出图」页「导出本页 3DM」（整机，各片各在自己的图层，写完自校三项）；本报告与 3DM 一起交给加工与现场。");
        sb.AppendLine($"  设计记录：{d.Name}；本报告由「导出安装报告」写出。");
        sb.AppendLine();

        // 10 待现场确认
        sb.AppendLine("**10. 待现场确认**");
        sb.AppendLine($"  铜排实际走线长度（现按 {p.BusbarLenToSinkMm:0} mm）与冷端温度（现按 {p.BusbarSinkTempC:0} °C）；夹持温度能否整定到上表的数；");
        sb.AppendLine($"  焊接方法（现按手工 TIG，下界 {DesignInputs.WeldMinDefaultMm:0.0} mm）；保温材料的实际厚度规格；铜排表面状态（发射率）。");
        if (r.Notes.Count > 0)
        {
            sb.AppendLine("  求解备注：");
            foreach (var n in r.Notes.Take(12)) sb.AppendLine("   · " + n.Replace("**", ""));
        }
        return sb.ToString();
    }

    /// <summary>把制表位表转成 Markdown 管道表（导出 .md 用）；其余行原样。</summary>
    public static string ToMarkdown(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var outp = new List<string>();
        bool inTable = false;
        foreach (var line in lines)
        {
            if (line.Contains('\t'))
            {
                var cells = line.Split('\t').Select(c => c.Trim().Replace("|", "／"));
                outp.Add("| " + string.Join(" | ", cells) + " |");
                if (!inTable)
                {
                    outp.Add("|" + string.Join("|", Enumerable.Repeat("---", line.Split('\t').Length)) + "|");
                    inTable = true;
                }
            }
            else
            {
                if (inTable) { outp.Add(""); inTable = false; }
                outp.Add(line.StartsWith("  ", StringComparison.Ordinal) ? line.TrimStart() : line);
            }
        }
        return string.Join("\n", outp);
    }
}
