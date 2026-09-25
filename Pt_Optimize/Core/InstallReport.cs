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

    /// <summary>★ R48（2026-09-14，Opus 5）：圆盘保温按设计逐片取（DesignSpec.DiscInsulMmOf，与算例同一口径）。</summary>
    private static string DiscInsulText(DesignSpec d)
    {
        var discs = Enumerable.Range(0, d.FlangeCount).Select(d.DiscInsulMmOf).ToArray();
        return discs.All(v => v <= 1e-6) ? "圆盘不包；"
             : discs.All(v => Math.Abs(v - discs[0]) < 1e-9) ? $"圆盘双面包 {discs[0]:0.0} mm；"
             : $"圆盘双面包，逐片 {string.Join(" / ", discs.Select(v => v.ToString("0.0")))} mm（入口 … 出口）；";
    }

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
        // ★ 2026-09-15 Opus 5（J 路，合并把关待办 P2-9）：LineResult.RecipeDeviations 此前只有测试在读 —— 报告与界面都看不到「这次算的不是生产配方」。
        //   接进安装报告：非空就紧跟判定单独一句并逐片列出（这几片的数不是生产口径）。生产链路上网格与热解不接受改配方（R48RecipeFingerprintTests 的行为门守着），正常情况下为空、这句不出现。
        var recipeDevs = r.RecipeDeviations;
        if (recipeDevs.Length > 0)
            sb.AppendLine("⚠ **这次计算用的网格或热解配方与生产不同 —— 本报告的数不是生产口径，不可作为安装依据**：" + string.Join("；", recipeDevs));
        sb.AppendLine();

        // 1 整线概要
        sb.AppendLine("**1. 整线概要**");
        sb.AppendLine($"  {d.SetpointC.Length} 段铂管串联，{d.FlangeCount} 片法兰兼作电极（中间 {Math.Max(0, d.FlangeCount - 2)} 片共用）。");
        sb.AppendLine($"  管：内径 {p.TubeIdMm:0} mm／壁厚 {d.WallMm:0.00} mm／牌号 {p.GradeName}（{(PtProps.For(p).IsFallback ? "该牌号电、热数据不全，电阻率、热导率、比热按纯铂" : "电阻率、热导率、比热、持久强度按此牌号")}；铂重按纯铂密度）；管保温 {d.TubeInsulMm:0.0} mm。");
        // ★ R48 物性接线复审（2026-09-23，Opus 5.5）：选的不是纯铂时，把电、热物性按谁取／退回纯铂那一句在这里全文印出。
        //   它在结果说明里排在末尾，第 10 节「求解备注」只印说明的前 12 条，说明多于 12 条时它会被截掉。结果说明里有那一条（LineRunner.AddGradeNote，含本算例温度越出数据点之处）就印那一条，
        //   没有（例如结果不是本次整线解出来的）就印 PtProps.Note 本身。纯铂 Note 为空，这里什么都不印。
        string gradeNote = PtProps.For(p).Note;
        if (gradeNote.Length > 0)
        {
            string full = r.Notes.LastOrDefault(n => n.StartsWith("★ " + gradeNote, StringComparison.Ordinal)) is { } hit ? hit.Substring(2) : gradeNote;
            sb.AppendLine("  　电、热物性：" + full);
        }
        sb.AppendLine($"  铂重：管 {r.TubeMassG:0} g + 法兰 {r.FlangeMassG:0} g = 合计 {r.TotalMassG:0} g。");
        // ★★ 2026-09-18，Opus 5：强度那一条的**设计输入**要写在报告里 —— 数与出处一起，不许只给数。
        //   出处只有一份写法（TubeStrength 的两个常量），参数表说明引的也是它。
        sb.AppendLine($"  强度口径：设计寿命 {p.DesignLifeHours:0} h、力学安全系数 {p.SafetyFactor:0.0}；"
                    + $"许用 = 断裂强度(取值温度, 设计寿命) ÷ 安全系数。");
        sb.AppendLine("  　" + TubeStrength.LifeNote);
        sb.AppendLine("  　" + TubeStrength.SafetyFactorNote);
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
            // R47 第三轮 N5：图纸档的板厚栏是 NaN（k 在 ThicknessScale），印「图纸×k」不印 NaN
            string plateT = j < d.TabThickMm.Length && !double.IsNaN(d.TabThickMm[j]) ? d.TabThickMm[j].ToString("0.00")
                          : d.IsDrawingRecord && j < d.ThicknessScale.Length ? $"图纸×{d.ThicknessScale[j]:0.00}" : "—";
            sb.AppendLine($"{f.Name}\t{where}\t{plateT}" +
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
        // ⚠ 2026-09-18 Opus 5 更正：这句原写「材料同参数表『② 中层』」，而 p.Layer1 在参数表上是**「① 内层（贴铂）」**
        //   （DesignInputs.Layer1 的 DisplayName）。名字与实物对不上，照实改。
        sb.AppendLine($"  管保温 {d.TubeInsulMm:0.0} mm（材料同参数表「① 内层（贴铂）」：{p.Layer1.Name}）；" +
                      DiscInsulText(d) +
                      "舌片按上表逐片包，从圆盘切点到压接段前，压接段不包；" +
                      $"端部额外保温 {p.EndInsulExtraMm:0.0} mm × 长 {p.EndInsulLengthMm:0} mm。");
        sb.AppendLine("  舌保温是热平衡的主力旋钮，不花铂：各片厚度不同是算出来的，不要做成同一规格。");
        // ★ R48 L（2026-09-17，Opus 5）：现场是**一层一层缠**的，报告以前只给 mm ⇒ 层数要现场自己心算。
        //   求解器的舌保温图纸格已经就是包法每层（InsulationSearch.LayerMm），这里把同一份数折成层数印出来；
        //   层厚与折算都只有那一份写法，不在这里另抄。
        sb.AppendLine($"  按包法每层 {InsulationSearch.LayerMm:0.0} mm 折算，逐片 = {InsulationSearch.LayersText(d.TabInsulMm)}"
                    + $"（对应上表的 {DesignSpec.Fmt(d.TabInsulMm, "0.0")} mm）。"
                    + "印出「不在层上」的片说明那个厚度缠不出来 —— 别自己凑，回 APP 重解。");
        sb.AppendLine();

        // ★★★★★ 5b 保温方案（2026-09-18，Opus 5；用户当日「还是只给材质保温厚度方案就行」）
        //   交付的就是这张表：每一区用什么材质、包多厚、折合几层、要缠几圈。
        //   数与排版都只有 InsulationPlan 一份（③ 页那张表、输出框、本节同源）。
        sb.AppendLine($"**5b. {InsulationPlan.SectionTitle}**");
        sb.Append(InsulationPlan.Text(r, d, p));
        sb.AppendLine();

        // 6 焊接与加工
        sb.AppendLine("**6. 焊接与加工**");
        var thick = new List<string>();
        for (int j = 0; j < d.FlangeCount && j < d.TabThickMm.Length; j++)
        {
            var parts = new List<string> { double.IsNaN(d.TabThickMm[j]) ? (d.IsDrawingRecord && j < d.ThicknessScale.Length ? $"板 按图纸 ×{d.ThicknessScale[j]:0.00}" : "板 —") : $"板 {d.TabThickMm[j]:0.00}" };   // R47 第三轮 N5
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
        // R48 G2 复审二（2026-09-15 Opus 5）：判据表那一行复核前暂不给数 ⇒ 不再叫工程师去那一行看数
        sb.AppendLine("  升温期间共用片最先到温，注意法兰比管热的那一段（判据表「升温期法兰−管峰值」暂不给数、待复核，现场按实测盯）。");
        sb.AppendLine();

        // ★★★★★ 2026-09-18，Opus 5：**三关结论与升温全程的伸长量**（新一节）。
        //   在此之前这份报告只说得出带玻璃稳态那一关，而判定的次序是
        //   「升温全程 → 带玻璃稳态 → 空管到温 → 铂重」（用户 2026-09-15/16）。
        //   升温全程算得出来（Core/RampSweep.cs）却一直没有生产调用方、空管到温也没人造过算例 ——
        //   工程师拿到的这张纸于是只覆盖三分之一，而它**看起来是完整的**。
        //   ⚠ 只取 FinalCheckReport 那一份写法：报告层不自己拼结论、也不重算一个伸长。
        sb.Append(FinalCheckReport.Section(r));
        sb.AppendLine();

        // 8 判据结论
        sb.AppendLine("**8. 判据表**" + (meshNote.Length > 0 ? $"（{meshNote}）" : "")
                    + "　（这张表是上面**第二关 带玻璃稳态**那一关的判据；另两关的结论见第 7b 节）");
        sb.AppendLine("判据\t实际\t限值\t单位\t判定\t位置");
        // ★ R48 B（2026-09-14 Opus 5）：参考量印「参考（不卡交付）」，不印「过」—— 旧判法两条降级后还在表里，印「过」会被读成它们也把过关。
        // R48 G2 复审二（2026-09-15 Opus 5）：暂不给数的参考量（ConstraintOut.Withheld）印「参考（暂不给数）」，先于「算不出」—— 两者不是一回事。
        // R48 G2 复审二（2026-09-15 Opus 5）：没有数的「实际」印「—」，不印 NaN（本机文化下印成「非數值」，门四甲首跑看到）
        foreach (var c in r.Checks)
            sb.AppendLine($"{Criteria.Plain(c.Name)}\t{(double.IsNaN(c.Actual) ? "—" : c.Actual.ToString("0.###"))}\t{c.Limit:0.###}\t{c.Unit}\t"
                        + $"{Criteria.Verdict(c)}	{c.Where}");   // 判据名与判词都走 Criteria（2026-09-18 Opus 5 提成一份：参考量不印「过／不过」，谁都不许再手抄）
        sb.AppendLine();

        // ★★ R48 B（2026-09-14 Opus 5）：热侧／冷侧两条的**逐片读数** —— 基准怎么取、最热的是谁、管根哪一端最冷，
        //   并列「模型算的无法兰交界管温」，差超过 1 ℃ 的逐片写明。读数只走 ThermocoupleBasis（与判据同一份），不在这里重算。
        // ★★ 决 103（业主 2026-09-24）：现行卡交付的热侧／冷侧与两条热稳定的**逐片**值（判据表只给最差那片）。改回口径不印这一段（逐位同改前）。
        //   值原样取自片上字段（与判据同一份：ContactChecks、LocalStabCheck、FlangeLumped 的逐片裕度），不在这里另判。
        if (r.RuleSet == CriteriaRuleSet.决103)
        {
            var hc = r.Find(LineResult.Key.HotOverContact);
            sb.AppendLine($"  带玻璃稳态卡交付的热侧与冷侧（2026-09-24 起；接触处温度 = 模型算的该片管根接触温度，共用片取两侧段端温度的较高者）："
                        + $"法兰最热处高出管接触处 ≤ {hc?.Limit ?? double.NaN:0.###} K；管接触处流入法兰的净热流 ≤ 0 W；局部与整片热稳定 ≥ 1。");
            sb.AppendLine("片	法兰最高温 °C	管接触处 °C	高出 K	管→法兰净热流 W	局部热稳定（全格）	整片热稳定");
            for (int j = 0; j < r.Flanges.Length; j++)
            {
                var f = r.Flanges[j];
                string und = LineRunner.PlateUndeterminedWhy(r, j);
                if (und.Length > 0) { sb.AppendLine($"{f.Name}	判不了（{und}）"); continue; }
                sb.AppendLine($"{f.Name}	{f.TMaxC:0.0}	{f.TRootC:0.0}	{f.TMaxC - f.TRootC:+0.00;−0.00}	{f.QFromTubeW:+0.00;−0.00}"
                            + $"	{(double.IsNaN(f.LocalStabMargin) ? "判不了" : f.LocalStabMargin.ToString("0.00"))}"
                            + $"	{(double.IsNaN(f.FlangeStabMargin) ? "判不了" : f.FlangeStabMargin.ToString("0.00"))}");
            }
            sb.AppendLine("  下面热偶读数基准那张表只作对照（2026-09-24 起不卡交付）。");
        }
        var hotC = r.Find(LineResult.Key.HotOverTc);
        var coldC = r.Find(LineResult.Key.ColdUnderTc);
        // ★ U 路（2026-09-18，Opus 5）：允许差多少是参数表填的（默认 = 热偶在 1100 °C 的误差），本行印**本次实际用的**两个数，不抄默认值。
        sb.AppendLine($"  热偶读数基准（控温热偶在段中点；端片取本段读数，共用片取两侧读数的对数平均，按开尔文算）。"
                    + $"本次温差预算：最热铂高出读数 ≤ {lc.HotOverTcMaxK:0.###} K、管根低于读数 ≤ {lc.ColdUnderTcMaxK:0.###} K"
                    + $"（参数表「最热铂高出热偶读数 允许值」「管根低于热偶读数 允许值」；默认 = 热偶在 1100 °C 的误差）：");
        sb.AppendLine($"片\t热偶读数基准 °C\t最热铂 °C\t最热的是\t高出 K（限 {hotC?.Limit ?? double.NaN:0.#}）\t管根较冷端 °C\t低于 K（限 {coldC?.Limit ?? double.NaN:0.#}）\t模型算的无法兰交界管温 °C\t与基准差 ℃");
        var tcs = ThermocoupleBasis.All(r);
        foreach (var t in tcs)
        {
            // ★ 2026-09-15 Opus 5（J 路，合并把关待办 P1-4）：本片（或管）带「判不了」后置标记时，逐片读数照样印数就是把坏场当好场交给现场 ——
            //   与判据表同一个定义（LineRunner.PlateUndeterminedWhy），热侧、冷侧两列一律印「判不了」并写原因。
            string und = LineRunner.PlateUndeterminedWhy(r, t.Plate);
            bool hotBlind = t.HotBlind.Length > 0 || und.Length > 0, coldBlind = t.ColdBlind.Length > 0 || und.Length > 0;
            sb.AppendLine($"{t.Name}\t{t.RefC:0.00}\t{(hotBlind ? "判不了" : t.HottestC.ToString("0.0"))}\t{(und.Length > 0 ? und : t.HotBlind.Length > 0 ? t.HotBlind : t.HottestWhat)}"
                        + $"\t{(hotBlind || double.IsNaN(t.HotK) ? "—" : t.HotK.ToString("+0.00;−0.00"))}"
                        + $"\t{(coldBlind ? "判不了" : $"{t.RootColdC:0.0}（{t.RootColdWhere}）")}"
                        + $"\t{(coldBlind || double.IsNaN(t.ColdK) ? "—" : t.ColdK.ToString("+0.00;−0.00"))}"
                        + $"\t{(double.IsNaN(t.ModelJointC) ? "没有基线" : t.ModelJointC.ToString("0.0"))}"
                        + $"\t{(double.IsNaN(t.ModelMinusRefK) ? "—" : t.ModelMinusRefK.ToString("+0.0;−0.0"))}");
        }
        foreach (var t in tcs.Where(t => !double.IsNaN(t.ModelMinusRefK) && Math.Abs(t.ModelMinusRefK) > ThermocoupleBasis.ModelGapNoteK))
            sb.AppendLine($"  · {t.Name}：{ThermocoupleBasis.ModelJointNote(t)}。");
        sb.AppendLine();

        // ★ U 路（2026-09-18，Opus 5）：**每片舌保温的可行窗口**（终验量的那一份；没量就照实说没量，不留空白让人以为量过了）。
        //   只印结果自己的报告，不在这里重算、不另抄一份判定口径。
        sb.AppendLine("**8b. 每片舌保温的可行窗口（现场缠得出来吗）**");
        if (r.TabInsulWindow is { } win)
        {
            sb.Append(win.Report());
            if (!win.Manufacturable)
                sb.AppendLine("  ⇒ **本报告不可作为安装依据**：上面点名的那几片，现场缠不出落在窗口里的层数。");
        }
        else
        {
            sb.AppendLine("  本次没量（它一点要一次整线解，只在终验跑一次）。"
                        + "要它：到「① 输入」页把「终验时量每片舌保温的可行窗口」打开，再做一次加密复算到数不再变。");
            sb.AppendLine("  ⚠ 没量 ≠ 缠得出来：解出来的舌保温未必落在现场能缠的层数上。");
        }
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
