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
//  本轮的处置（工单：本轮不改求解链）：
//    ① 说明改成现状的真话（DesignInputs.GradeNameNote）；
//    ② 先把差量量出来存档（下面那条会写一份带开跑时刻的文件）；
//    ③ 这条源码门两头钉：
//       · 谁把电阻率／热导率／比热接成按牌号而没回来改这句话 ⇒ 红；
//       · 谁把这句话改回「电阻率与持久强度均取该牌号的实测数据」那句假话 ⇒ 红。
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
        var byGrade = new List<string>();     // 求解链里**按牌号**取电／热物性的地方
        var pureP = new List<string>();       // 求解链里读**写死纯铂**那几支的地方
        foreach (string f in SolveChainFiles())
        {
            string s = File.ReadAllText(f);
            string name = Path.GetFileName(f);
            foreach (string pat in new[] { "Materials.PtResistivity(", "Materials.PtThermalK(", "Materials.PtCp(" })
                if (s.Contains(pat, StringComparison.Ordinal)) pureP.Add($"{name} ← {pat.TrimEnd('(')}");
            foreach (string pat in new[] { ".ResistivityOhmM(", ".ThermalKWPerMK(", ".CpJPerKgK(" })
                if (s.Contains(pat, StringComparison.Ordinal)) byGrade.Add($"{name} ← {pat.Trim('.', '(')}");
        }
        _o.WriteLine("求解链读写死纯铂那几支的地方：" + string.Join("；", pureP.Distinct()));
        _o.WriteLine("求解链按牌号取电／热物性的地方：" + (byGrade.Count == 0 ? "（一处都没有）" : string.Join("；", byGrade.Distinct())));

        // 现状：电阻率／热导率／比热**还是纯铂**
        Assert.True(pureP.Count > 0, "求解链里一处都不读纯铂那几支了？那说明已经改了接线 —— 回来改这句说明");
        Assert.True(byGrade.Count == 0,
            "求解链已经按牌号取电／热物性了：" + string.Join("；", byGrade.Distinct())
            + " —— 好事，但参数表「铂材牌号」的说明（DesignInputs.GradeNameNote）还写着「目前一律按纯铂算」，"
            + "请同时把那句话改成新的真话，并把差量那份文件的结论一起更新。");
        Assert.Contains("电阻率、热导率、比热目前一律按纯铂算", note);

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
        W("· 差 % 不是误差，是**接不接线的后果**：现在求解链一律按纯铂算，选了别的牌号，电与热那一路就差这么多。");
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
