using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M　**参数表「铂材牌号」的说明必须与源码里真读的一致** —— 2026-09-18，Opus 5
//
//  病灶（合并树 HANDOVER 已登记为「界面说假话」）：
//    说明写「电阻率与持久强度均取该牌号的实测数据」，
//    而求解链的电阻率读的是 Materials.PtResistivity —— **写死纯铂**；热导率、比热同理。
//    也就是说：界面上换牌号，强度那一路会变，电、热那一路一位都不动，
//    而说明告诉工程师两者都按牌号走。**让人以为的与事实不一样**。
//
//  2026-09-18 那一轮的处置（工单：那一轮不改求解链）：
//    ① 说明改成当时的真话（DesignInputs.GradeNameNote：「电阻率、热导率、比热目前一律按纯铂算」）；
//    ② 先把差量量出来存档（下面那条慢门会写一份带开跑时刻的文件）；
//    ③ 源码门两头钉「说明 ↔ 源码」。
//
//  ★ 2026-09-23（Opus 5.5，R48 物性接线）**改门，变因 = 求解链接了线**：电阻率（含 dρ/dT、电阻温度系数）、热导率、比热一律经 PtProps.For(牌号) 取，
//    说明随之改成新的真话（「也按所选牌号」「仍按纯铂的：密度、熔点」「数据不全一起按纯铂」）。门仍然两头钉，只是钉的现状换了：
//       · 求解链里直读纯铂那几支的只许剩两处例外（DesignScreen 电阻率、RemovalPriority 热导率，原因见 R48PropsWiringGateTests 源码门）⇒ 多一处就红；
//       · 按牌号取物性的档必须恰好是接线的那 13 档（经 PtProps.For；外加安装报告与 LineSolver 只读说明文字那两档）⇒ 少一档（改回直读纯铂）就红；
//       · 说明里不许再出现「目前一律按纯铂算」，也不许回到 2026-09-18 以前那句假话；
//       · 说明里说「密度按纯铂」⇒ 核实整线链的铂重用的是 Materials.PtDensity、不是材料库里的合金密度；
//       · Core/*.cs 里绕过 PtProps 直读按牌号曲线（.ResistivityOhmM(／.ThermalKWPerMK(／.CpJPerKgK(）的只许 PtProps／MaterialDb／PtResistivityData 名单上那几处；
//       · 说明里写「焊缝屈曲下界仍按纯铂」⇔ WeldDistortion.cs 真的还读 Materials.PtCpMeanToMelt。
// ════════════════════════════════════════════════════════════════════════════

public class R48MGradeNoteTruthTests
{
    private readonly ITestOutputHelper _o;
    public R48MGradeNoteTruthTests(ITestOutputHelper o) { _o = o; }

    /// <summary>求解链（Core 下，除材料库本身与它的门以外）的档。命令行 Program.cs 不在内：它有一张按牌号的对照表，那是工具不是求解链。</summary>
    private static string[] SolveChainFiles()
    {
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MaterialDb.cs",          // 材料库自己：按牌号的入口就住在这里
            "Materials.cs",           // 纯铂那几支函数的定义处
            "PtResistivityData.cs",   // 原始表
            "PtCreepWorkbook.cs",
            "PtThermalExpansion.cs",
            "GradeChoices.cs",
            "DesignInputs.cs",        // 参数表：说明文字本身住在这里
            "PtProps.cs",             // 访问口本身，纯铂一支在这里调原函数（2026-09-23 Opus 5.5 加，R48 物性接线）
        };
        return Directory.GetFiles(core, "*.cs")
                        .Where(f => !skip.Contains(Path.GetFileName(f)))
                        .ToArray();
    }

    [Fact]
    public void 说明里写的与源码里真读的一致()
    {
        string note = DesignInputs.GradeNameNote;
        _o.WriteLine("参数表说明：" + note);

        // ── 那句假话不许回来 ──
        Assert.DoesNotContain("电阻率与持久强度均取该牌号的实测数据", note);

        // ── 现状核对（源码）──
        var byGrade = new List<string>();     // 求解链里**按牌号**取电／热物性的地方（经 PtProps.For）
        var pureP = new List<string>();       // 求解链里读**写死纯铂**那几支的地方
        foreach (string f in SolveChainFiles())
        {
            string s = File.ReadAllText(f);
            string name = Path.GetFileName(f);
            foreach (string pat in new[] { "Materials.PtResistivity(", "Materials.PtThermalK(", "Materials.PtCp(", "Materials.PtTcr(", "Materials.RhoRef *", "Materials.BetaFit" })
                if (s.Contains(pat, StringComparison.Ordinal)) pureP.Add($"{name} ← {pat.TrimEnd('(', ' ', '*')}");
            if (s.Contains("PtProps.For(", StringComparison.Ordinal)) byGrade.Add(name);
        }
        _o.WriteLine("求解链读写死纯铂那几支的地方：" + (pureP.Count == 0 ? "（一处都没有）" : string.Join("；", pureP.Distinct())));
        _o.WriteLine("求解链按牌号取电／热物性的档：" + string.Join("、", byGrade.OrderBy(x => x, StringComparer.Ordinal)));

        // 现状（2026-09-23 起）：电阻率／热导率／比热**按牌号**；直读纯铂的只剩两处有因的例外
        // 变因（Opus 5.5，R48 物性接线）：原断言「pureP 非空、byGrade 为空、说明含『目前一律按纯铂算』」钉的是接线前的现状；接线后改钉新现状。
        Assert.Equal(new[] { "DesignScreen.cs ← Materials.PtResistivity", "RemovalPriority.cs ← Materials.PtThermalK" },
                     pureP.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // 13 档求解链 + InstallReport.cs（安装报告「牌号」那一句读 PtProps.For(p).IsFallback 写明按此牌号还是退回纯铂，非纯铂时再全文印 PtProps.Note）
        //   + LineSolver.cs（复审同日加：参考工具页逐行牌号那一路，JointGradeNotes 读 PtProps.For(该行牌号).Note 带出退回说明；它不取物性值）
        Assert.Equal(new[] { "CoupledSolver.cs", "DesignCurrent.cs", "DesignScreen.cs", "FlangeStability.cs", "InstallReport.cs", "LineRunner.cs", "LineSolver.cs", "LocalStability.cs",
                             "PlateThermal2D.cs", "RampScreen.cs", "RampSolver.cs", "RampTwoNode.cs", "SegmentSolver.cs", "ShellCurrent.cs", "ShellThermal.cs" },
                     byGrade.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        string core = Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core");
        Assert.Contains("PtProps? props = null", File.ReadAllText(Path.Combine(core, "ShellCurrent.cs")));
        Assert.Contains("PtProps? props = null", File.ReadAllText(Path.Combine(core, "PlateCurrent2D.cs")));
        Assert.Contains("电阻率（含电阻温度系数）、热导率、比热也按所选牌号", note);
        Assert.Contains("仍按纯铂的：**密度**", note);
        Assert.Contains("数据不全", note);
        Assert.DoesNotContain("目前一律按纯铂算", note);
        // 按牌号的曲线只许经 PtProps 读：Core 下全体 *.cs（不止上面的求解链档）里直接调 .ResistivityOhmM(／.ThermalKWPerMK(／.CpJPerKgK( 的，
        //   只许是下面名单（逐档逐式计数，多一处少一处都红）：
        //   PtProps.cs 各 1：访问口本身，按牌号那一支；
        //   MaterialDb.cs 电阻率 1：Pt-Rh/80-20 的参考热导率（Wiedemann–Franz 推算，只作参考、不入判定，PtProps 不读它）；
        //   PtResistivityData.cs 电阻率 1：覆盖查询 Read 给出的值就是原函数。
        //   覆盖：Core/*.cs 源码字面。不覆盖：Program.cs（命令行的按牌号对照表）、UI/、经别名或反射的调用。
        //   （2026-09-23 Opus 5.5 R48 物性接线复审补上：接线前这条由「byGrade 为空」兼管，改钉新现状后它丢了，这里补回。）
        var directAllowed = new Dictionary<(string, string), int>
        {
            [("PtProps.cs", ".ResistivityOhmM(")] = 1, [("PtProps.cs", ".ThermalKWPerMK(")] = 1, [("PtProps.cs", ".CpJPerKgK(")] = 1,
            [("MaterialDb.cs", ".ResistivityOhmM(")] = 1,
            [("PtResistivityData.cs", ".ResistivityOhmM(")] = 1,
        };
        var directBad = new List<string>();
        foreach (string f in Directory.GetFiles(core, "*.cs"))
        {
            string name = Path.GetFileName(f), s = File.ReadAllText(f);
            foreach (string pat in new[] { ".ResistivityOhmM(", ".ThermalKWPerMK(", ".CpJPerKgK(" })
            {
                int cnt = s.Split(pat).Length - 1;
                int want = directAllowed.TryGetValue((name, pat), out int w) ? w : 0;
                if (cnt != want) directBad.Add($"{name} 里 {pat} {cnt} 处（应 {want}）");
            }
        }
        Assert.True(directBad.Count == 0, "绕过 PtProps 直读按牌号曲线的地方与名单不符：" + string.Join("；", directBad));

        // 焊缝屈曲下界仍按纯铂：说明里写了这一条例外 ⇔ WeldDistortion.cs 真的还读**四个**纯铂常数
        //   Materials.PtAlphaExp／PtCpMeanToMelt／PtLatentFusion／PtPoisson（谁把其中任一个接成按牌号，说明里这句就成了假话，回来改）。
        //   2026-09-23 复审：原来只查 PtCpMeanToMelt 一个，钉子比它自己的说法窄；改成四个都查、且说明里要有「仍按纯铂」四字。
        string weld = File.ReadAllText(Path.Combine(core, "WeldDistortion.cs"));
        string[] weldConsts = { "Materials.PtAlphaExp", "Materials.PtCpMeanToMelt", "Materials.PtLatentFusion", "Materials.PtPoisson" };
        var weldMissing = weldConsts.Where(k => !weld.Contains(k, StringComparison.Ordinal)).ToList();
        bool weldPure = weldMissing.Count == 0;
        bool noteSaysWeldPure = note.Contains("焊缝屈曲下界", StringComparison.Ordinal) && note.Contains("仍按纯铂", StringComparison.Ordinal);
        Assert.True(weldPure == noteSaysWeldPure,
            $"WeldDistortion.cs 读四个纯铂常数 = {weldPure}（缺：{string.Join("、", weldMissing)}），说明里写「焊缝屈曲下界…仍按纯铂」= {noteSaysWeldPure}，两者要一致");
        Assert.True(noteSaysWeldPure, "说明里应写明焊缝屈曲下界仍按纯铂（今天 WeldDistortion.ForPt 读的是四个纯铂常数）");

        // 「密度按纯铂」是真的：整线链的铂重用 Materials.PtDensity，不读材料库的合金密度
        string lineRunner = File.ReadAllText(Path.Combine(core, "LineRunner.cs"));
        Assert.Contains("Materials.PtDensity", lineRunner);
        Assert.DoesNotContain("DensityKgM3", lineRunner);

        // 持久强度那一半是真的按牌号（说明里写了「持久强度按牌号」，这里核实）
        Assert.Contains("持久强度按牌号", note);
        string mech = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Mechanics.cs"));
        string strength = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "TubeStrength.cs"));
        Assert.Contains("MaterialDb.Get(p.GradeName)", mech);
        Assert.Contains("MaterialDb.Get(p.GradeName)", strength);

        // 灰显那一句也要在说明里（工程师看到灰行时得知道那是数据的事，不是程序漏了）
        Assert.Contains("灰显", note);
        Assert.Contains("默认纯铂", note);
    }

    /// <summary>
    /// **差量表**：写死纯铂的那三支（Materials.PtResistivity／PtThermalK／PtCp）
    /// 与按牌号的那三支（PtGrade.ResistivityOhmM／ThermalKWPerMK／CpJPerKgK）逐点对照。
    /// 输出带开跑时刻的新文件，不覆盖任何旧证据。
    ///
    /// ⚠ 标**慢**不是因为它慢（毫秒级），是因为它**每跑一次就多一份带时刻的文件**：
    ///   放进快套件 ⇒ 每次 dotnet test 都在 deliverable 里多一份几乎一样的证据，
    ///   而「同一个数在仓库里有好几份」正是本项目最怕的形态（要引的时候不知道该引哪一份）。
    ///   物性是静态表，不跑也不会悄悄变 —— 真变了，上面那条快门（源码门）与 MaterialDbTests 会先红。
    ///   要重量就显式跑它：dotnet test --filter "FullyQualifiedName~R48MGradeNoteTruthTests"。
    ///   2026-09-18 实测：连跑两次，全文去掉开跑时刻后**逐行相同**。
    /// </summary>
    [Fact, Trait("速度", "慢")]
    public void 量出按牌号与纯铂的差并存档()
    {
        double[] temps = { 20, 300, 600, 900, 1100, 1150, 1300 };
        string[] grades = { "Pt", "Pt-Rh/90-10", "Tanaka-ZGS-Pt", "Tanaka-ZGS-PtRh10" };

        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        W("R48 M　**物性：按牌号 vs 求解链写死的纯铂　逐点差量**");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W();
        W("═══════ 2026-09-23 起的读法（Opus 5.5，R48 物性接线）═══════");
        W("接线前的差量：本文件量的是「若求解链按纯铂算」与按牌号的差；2026-09-23 起求解链已按牌号取（PtProps），这份差量即接线带来的物性变化。");
        W("下面「为什么量这个」一节是 2026-09-18 写的原文（当时求解链写死纯铂），保留作出处。");
        W();
        W("═══════ 为什么量这个 ═══════");
        W("参数表「铂材牌号」的说明原写「电阻率与持久强度均取该牌号的实测数据」，而求解链的电阻率读的是");
        W("Materials.PtResistivity —— **写死纯铂**（段解焦耳热、壳体电流、板件二维电流三处），热导率与比热同理。");
        W("本轮**不改求解链**（改它要连判据全体重跑），只做两件事：把说明改成现状的真话，先把差量量出来。");
        W("这份文件回答的就是：接线没接上，到底差多少。");
        W();
        W("出处（每一列都指得回去）：");
        W("  纯铂那一列 = Materials.PtResistivity / Materials.PtThermalK / Materials.PtCp（求解链现在真读的那三支）；");
        W("  按牌号那一列 = MaterialDb.Get(牌号).ResistivityOhmM / .ThermalKWPerMK / .CpJPerKgK（H 路交出来的按牌号入口）；");
        W("  相对差 % = (按牌号 − 纯铂) ÷ 纯铂 × 100。");
        W();

        var maxAbs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string g in grades)
        {
            var grade = MaterialDb.Get(g);
            var dc = MaterialDb.DataCompleteness(g);
            W($"═══════ {g} ═══════");
            W($"  四类数据齐全：{(dc.IsComplete ? "是" : "否")}"
              + (dc.Borrowed.Length > 0 ? "；同名义成分借用：" + string.Join("；", dc.Borrowed) : "")
              + (dc.Missing.Length > 0 ? "；缺：" + string.Join("；", dc.Missing) : ""));
            // 那两条曲线的出处与告诫随值带出 —— 「每个数都要能指回出处」
            if (grade.ThermalKTable is { } kt) W($"  热导率出处：{kt.Source}｜{kt.Note}");
            if (grade.CpTable is { } ct) W($"  比热出处：{ct.Source}｜{ct.Note}");
            W("温度 °C\t电阻率 纯铂 Ω·m\t电阻率 按牌号 Ω·m\t差 %\t热导率 纯铂 W/(m·K)\t热导率 按牌号\t差 %\t比热 纯铂 J/(kg·K)\t比热 按牌号\t差 %\t按牌号那两条的覆盖");
            foreach (double t in temps)
            {
                double rP = Materials.PtResistivity(t), rG = grade.ResistivityOhmM(t);
                double kP = Materials.PtThermalK(t), kG = grade.ThermalKWPerMK(t);
                double cP = Materials.PtCp(t), cG = grade.CpJPerKgK(t);
                double dR = Pct(rG, rP), dK = Pct(kG, kP), dC = Pct(cG, cP);
                Track(maxAbs, $"{g}／电阻率", dR); Track(maxAbs, $"{g}／热导率", dK); Track(maxAbs, $"{g}／比热", dC);
                var cov = new List<string>();
                if (grade.ThermalKTable is { } k2 && !k2.Covers(t)) cov.Add($"热导率在数据点 {k2.TMinC:0}–{k2.TMaxC:0} °C 之外（取端点值）");
                if (grade.CpTable is { } c2 && !c2.Covers(t)) cov.Add($"比热在数据点 {c2.TMinC:0}–{c2.TMaxC:0} °C 之外（取端点值）");
                W($"{t:0}\t{rP:0.000000E+00}\t{rG:0.000000E+00}\t{Fmt(dR)}\t{kP:0.000}\t{Fmt2(kG)}\t{Fmt(dK)}\t{cP:0.000}\t{Fmt2(cG)}\t{Fmt(dC)}\t"
                  + (cov.Count == 0 ? "区间内" : string.Join("；", cov)));
            }
            W();
        }

        W("═══════ 每个牌号每个量的最大相对差（绝对值）═══════");
        W("牌号／量\t最大相对差 %");
        foreach (var kv in maxAbs.OrderByDescending(kv => double.IsNaN(kv.Value) ? -1 : Math.Abs(kv.Value)))
            W($"{kv.Key}\t{Fmt(kv.Value)}");
        W();

        // ── 纯铂那一对：两式若在 1150 °C 差 > 1 % 要写明哪份有出处 ──
        double r1150Pure = Materials.PtResistivity(1150), r1150Grade = MaterialDb.Get("Pt").ResistivityOhmM(1150);
        double dPure = Pct(r1150Grade, r1150Pure);
        W("═══════ 纯铂两式在 1150 °C 的自洽 ═══════");
        W($"Materials.PtResistivity(1150) = {r1150Pure:0.000000E+00} Ω·m（RhoRef 9.83e-8 × (1 + AlphaFit·T + BetaFit·T²)）");
        W($"MaterialDb「Pt」.ResistivityOhmM(1150) = {r1150Grade:0.000000E+00} Ω·m（Rho0 9.83 µΩ·cm、T₀ = 0 °C、同一对系数）");
        W($"相对差 {Fmt(dPure)} %。");
        W(Math.Abs(dPure) > 1.0
          ? "⚠ **差超过 1 %** —— 两份里带出处的是《鉑金電氣計算.xlsx》R23–R44 那一行（MaterialDb 的 Pt 行照它填）；"
            + "Materials 的三个常量是同一份工作簿抄进代码的，若两者不等，以工作簿那一份为准，并回来查是谁漂开了。"
          : "两式同源（同一份《鉑金電氣計算.xlsx》的系数），相对差在 1 % 以内 ⇒ 纯铂这一支接不接线都一样，"
            + "差只出在**非纯铂牌号**上（见上表）。");
        W();
        W("═══════ 读法 ═══════");
        W("· 「按牌号」那一列给 —— 的地方＝该牌号这一类没有数据（热导率与比热只有 Pt 与 Pt-10Rh 两族有）。");
        W("· 差 % 是接线前后物性的变化（纯铂与 Tanaka-ZGS-Pt 为 0）：2026-09-23 起求解链按牌号取，选了 Pt-Rh/90-10 等牌号，电与热那一路相对纯铂就差这么多。");
        W("· 差的方向与下游的关系没在这里推（要推得连判据一起重跑）—— 本文件只给差量本身。");
        W();
        W("出处汇总：本文件每一个数都来自这一次运行（同一进程、同一份代码），没有拼接任何旧文件。");

        string file = DeliverableOut.Stamped("R48_M_物性按牌号与纯铂差量.txt");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine(sb.ToString());
        _o.WriteLine("已写出：" + file);

        // 「每一步都有结果」：文件写出来了，而且四个牌号都在里面
        Assert.True(File.Exists(file));
        string txt = File.ReadAllText(file);
        foreach (string g in grades) Assert.Contains($"═══════ {g} ═══════", txt);
        // 纯铂自己两式必须一致（同一份工作簿的系数）—— 不一致就说明仓库里漂开了
        Assert.True(Math.Abs(dPure) < 1e-9, $"纯铂两式在 1150 °C 不等：{r1150Pure:R} vs {r1150Grade:R}");
        // 非纯铂牌号必须真的有差，否则这份差量表什么都没说
        Assert.True(Math.Abs(maxAbs["Pt-Rh/90-10／电阻率"]) > 1.0, "Pt-Rh/90-10 的电阻率与纯铂差不到 1 %？复核数据");
    }

    private static double Pct(double got, double baseline)
        => double.IsNaN(got) || double.IsNaN(baseline) || Math.Abs(baseline) < 1e-300
           ? double.NaN : (got - baseline) / baseline * 100.0;

    private static string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("+0.00;−0.00;0.00");
    private static string Fmt2(double v) => double.IsNaN(v) ? "—" : v.ToString("0.000");

    private static void Track(Dictionary<string, double> d, string key, double v)
    {
        if (double.IsNaN(v)) { if (!d.ContainsKey(key)) d[key] = double.NaN; return; }
        if (!d.TryGetValue(key, out double cur) || double.IsNaN(cur) || Math.Abs(v) > Math.Abs(cur)) d[key] = v;
    }
}
