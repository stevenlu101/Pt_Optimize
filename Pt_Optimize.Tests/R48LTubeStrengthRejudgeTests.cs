using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路　**④ 管强度：W08 / W06 重判**（不重新求解） —— 2026-09-18，Opus 5
//
//  ══ 为什么这一跑不需要重新求解（先说清楚，免得把它当成一次求解的结果）
//
//  ④ 的两侧都**不吃场**：
//    · 载荷侧 = 几何 + 工况（管内径、壁厚、段长/支承跨距、玻璃密度/黏度/液位、产量、安全系数）；
//    · 许用侧 = 牌号 + **本段控温点** + 设计寿命 —— 控温点是算例给的（LineCase.SetpointC），不是解出来的。
//  ⇒ 拿本树最新那两份解（照 §0.-11 重判三关所用的同一份设计记录）造算例，直接重判 ④ 即可。
//  段参数走生产的 LineRunner.BaseSegParams（与求解循环同一份），不在门里手抄。
//
//  ══ 读本轮的数要记住的三件（都写在报告正文里）
//
//   ① **上界**：1080 / 1050 °C 两段低于纯铂持久强度拟合下限 1100 °C ⇒ 许用值按 1100 °C 的保守值取，
//      算出来的是利用率的**上界**（真值只会更小）。上界 ≤ 1 才算过；上界 > 1 ⇒ 判不了。
//   ② **两态载荷不同**：带玻璃稳态 = 液柱 + 流动压降 + 管内玻璃自重；
//      空管到温稳态 = 只有铂管自重（2026-09-18 修的 (b)）。两态的控温点也不同（空管全线取升温目标）。
//   ③ 这不是服役工况的全部：σ 只含自重 + 内压，热应力、装配应力不在内（与改前口径相同）。
// ════════════════════════════════════════════════════════════════════════════

[Trait("速度", "慢")]
public class R48LTubeStrengthRejudgeTests
{
    private readonly ITestOutputHelper _o;
    public R48LTubeStrengthRejudgeTests(ITestOutputHelper o) { _o = o; }

    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 管强度重判(string which)
    {
        var d0 = which == "W08" ? R48LW08NavDesign.Build()
               : which == "W06" ? R48LW06FineDesign.Build()
               : throw new ArgumentException(which);
        string source = which == "W08" ? R48LW08NavDesign.Source : R48LW06FineDesign.Source;

        var p = new DesignInputs();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"R48_L_管强度重判_{which}_本次开跑于{stamp}.txt");
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); _o.WriteLine(s); }

        W($"R48 L 路　**④ 管强度重判**　{which}（{d0.Name}）");
        W($"开跑 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　工作树 {HandoverDoc.Root()}　写码 2026-09-18 Opus 5");
        W("");
        W("═══════ 这一份设计从哪来 ═══════");
        W($"出处：{source}");
        W("与 §0.-11「圆盘保温 10 mm 下重判三关」用的是**同一份**照表复原件（全仓唯一一份，R48LW08NavDesign／R48LW06FineDesign）。");
        W($"圆盘保温 {d0.FlangeInsulMm:0.#} mm　板厚 {string.Join("/", d0.TabThickMm.Select(v => v.ToString("0.00")))} mm　"
          + $"舌保温 {string.Join("/", d0.TabInsulMm.Select(v => v.ToString("0.0")))} mm　管壁 {d0.WallMm:0.00} mm");
        W("");
        W("═══════ **本轮不重新求解** ═══════");
        W("④ 的两侧都不吃场：载荷侧 = 几何 + 工况；许用侧 = 牌号 + 本段控温点 + 设计寿命（控温点是算例给的，不是解出来的）。");
        W("⇒ 直接按本树最新解重判。段参数走生产的 LineRunner.BaseSegParams（与求解循环同一份，门不许手抄生产配方）。");
        W("");
        W("═══════ 口径（跑前写死） ═══════");
        W($"· {TubeStrength.SafetyFactorNote}");
        W($"· {TubeStrength.LifeNote}");
        W($"· 牌号 {p.GradeName}；持久强度拟合区间 "
          + $"[{MaterialDb.Get(p.GradeName).CreepTMinC:0}, {MaterialDb.Get(p.GradeName).CreepTMaxC:0}] °C"
          + $"（出处：工作簿里该牌号的原始数据点温度范围）。");
        var mono = TubeStrength.MonotoneInFitRange(MaterialDb.Get(p.GradeName), p.DesignLifeHours);
        W($"· 单调性（上界规则的前提，本次现验）：{mono.Why}");
        Assert.True(mono.Ok, "单调性没验过就不许用上界规则：" + mono.Why);
        W("· 上界规则：段温低于拟合下限 ⇒ 许用值取 σ_r(拟合下限, 寿命)（保守）⇒ 利用率是**上界**；"
          + "上界 ≤ 1 判过，上界 > 1 判不了并点名需补 1000 °C 数据。");
        W("· 整线 = 逐段最大；**任一段判不了则整线判不了**（判不了不算过）。");
        W("");

        foreach (bool emptyTube in new[] { false, true })
        {
            var c = d0.BuildCase(p, emptyTube: emptyTube);
            string state = emptyTube ? "③ 空管到温稳态（无玻璃）" : "② 带玻璃稳态";
            W($"═══════ {state} ═══════");
            W($"控温点：{string.Join(" / ", c.SetpointC.Select(v => v.ToString("0")))} °C"
              + (emptyTube ? $"（空管按升温目标全线取 {c.RampTargetC:0} °C，LineCase.EmptyTubeSetpointFrom = {c.EmptyTubeSetpointFrom}）" : ""));
            W($"逐段玻璃水头：{string.Join(" / ", c.HeadM.Take(c.SegmentCount).Select(v => v.ToString("0.0")))} m"
              + (emptyTube ? "　⚠ 空管态**载荷不带玻璃**：没有液柱、没有流动压降、管内没有玻璃自重（2026-09-18 修）" : ""));
            W($"段长（同时是支承跨距）：{string.Join(" / ", c.SegLengthMm.Take(c.SegmentCount).Select(v => v.ToString("0")))} mm");

            var segParams = Enumerable.Range(0, c.SegmentCount)
                .Select(i => LineRunner.BaseSegParams(c, i, emptyTube ? double.NaN : c.GlassInC)).ToArray();
            var names = Enumerable.Range(0, c.SegmentCount).Select(LineRunner.SegName).ToArray();
            var rows = TubeStrength.Rows(c, segParams, names, c.SetpointC);

            W("");
            W("段\t控温 °C\t许用取在 °C\t断裂强度 MPa\tσ_vm MPa\t需 MPa\t利用率\t是上界\t判定");
            foreach (var r in rows)
                W($"{r.Name}\t{r.SetpointC:0}\t{(double.IsNaN(r.AllowAtC) ? "—" : r.AllowAtC.ToString("0"))}"
                  + $"\t{(double.IsNaN(r.AllowMPa) ? "—" : r.AllowMPa.ToString("0.###"))}"
                  + $"\t{r.VonMisesMPa:0.####}\t{r.ReqMPa:0.####}"
                  + $"\t{(double.IsNaN(r.Util) ? "—" : r.Util.ToString("0.000"))}"
                  + $"\t{(r.UpperBound ? "是" : "否")}\t{(r.Undetermined ? "判不了" : r.Util <= 1.0 ? "在限内" : "超 1")}");
            W("");
            foreach (var r in rows) W("· " + r.Show());

            var k = TubeStrength.Judge(c, rows);
            W("");
            W("整线那一行（判据表里印出来的就是这一行）：");
            W("  " + Criteria.OneLine(k));
            W($"  判词：{Criteria.Verdict(k)}　—— 参考量不印「过／不过」（2026-09-18 修的 (c)）；"
              + $"整线值 {(double.IsNaN(k.Actual) ? "—" : k.Actual.ToString("0.000"))}，位置 {k.Where}。");
            W("");
        }

        sw.Stop();
        W($"═══════ 成本 ═══════");
        W($"耗时 {sw.Elapsed.TotalSeconds:0.0} s（不求解，只重判）。");
        W("");
        W("2026-09-18　Opus 5");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        _o.WriteLine("输出：" + file);

        // ── 跑前写死的断言：逐段都要有结果，不许「判不了」被当成过
        var cg = d0.BuildCase(p);
        var rowsG = TubeStrength.Rows(cg,
            Enumerable.Range(0, cg.SegmentCount).Select(i => LineRunner.BaseSegParams(cg, i, cg.GlassInC)).ToArray(),
            Enumerable.Range(0, cg.SegmentCount).Select(LineRunner.SegName).ToArray(), cg.SetpointC);
        Assert.Equal(cg.SegmentCount, rowsG.Length);
        Assert.All(rowsG, r => Assert.False(string.IsNullOrEmpty(r.Name)));
        var kg = TubeStrength.Judge(cg, rowsG);
        Assert.False(kg.Undetermined == kg.Ok, "同一条判据不许既判不了又判过");
        Assert.True(rowsG.Any(r => r.UpperBound),
            "这两份设计的 HC2／HC3 控温点都在 1100 °C 以下，重判里一段上界都没有 ⇒ 上界规则没接上");
    }
}
