using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PtOptimize.Core;
using Xunit;
using Xunit.Abstractions;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 M　**终验三关接线** —— 2026-09-18，Opus 5
//
//  用户 2026-09-15/16 定的次序：升温全程 → 带玻璃稳态 → 空管到温 → 铂重。
//  接线前的实情（HANDOVER 物性接线表）：
//    · RampSweep.Run 全仓**没有生产调用方**（只有测试在调）；
//    · 空管到温稳态的分工况判据早就在，却没人在生产链上造过空管算例。
//  ⇒ 判定按三关在说话，工程师点得到的只有一关。
//
//  ══ 这几条门都是毫秒级的：判定所需的结果**注入**，受审的是接线与呈现这套生产逻辑本身
//
//   ① RampSweep.Run 有生产调用方（把「一个都没有」那条门反过来，见 R48ExpansionDownstreamTests）。
//   ② 结论顺序门：四个名字在结论块里必须按 升温全程 → 带玻璃稳态 → 空管到温 → 铂重 出现。
//   ③ 伸长报告接线门：安装报告里必须有那一节，且逐格印的就是 RampSweep 给的那个数
//      —— 改回不印（或改成自己再算一遍）⇒ 红。
//   ④ 没跑 ≠ 过：三关里没跑的那一关，结论块与报告都得写「没跑」，不许出现「过」。
//   ⑤ 界面接线：③ 页终验那一段真的调了 FinalCheck.Run，开关在参数表且默认开。
// ════════════════════════════════════════════════════════════════════════════

public class R48MThreeStateWiringTests
{
    private readonly ITestOutputHelper _o;
    public R48MThreeStateWiringTests(ITestOutputHelper o) { _o = o; }

    /// <summary>造一份「看起来像真的」的带玻璃结果 + 升温全程结果 + 空管结果（数都是注入的）。</summary>
    private static LineResult Fake(bool withRamp = true, bool withEmpty = true)
    {
        var glass = new LineResult
        {
            Ok = true, Converged = true, EmptyTube = false,
            MeshFineMm = 0.5, MeshCells = 12345,
            TubeMassG = 3000.25, FlangeMassG = 1245.08, TotalMassG = 4245.33,
        };
        if (withRamp)
        {
            var pt = new RampSweepPointResult
            {
                SetpointC = 300, ClampC = 250,
                LineRes = new LineResult { Ok = true, Converged = true, MeshFineMm = 0.5, MeshCells = 12345 },
                FieldValid = true,
                TubeJDesignAPerMm2 = 7.5, TubeJDesignLimit = 11, TubeJDesignOk = true, TubeJDesignUndetermined = false,
                Segs = new[]
                {
                    new RampSegInfo { Name = "HC1", CurrentA = 812, TubeJAPerMm2 = 6.5, TubeJLimit = 11, TubeJOk = true,
                                      TotalElongMm = 0.8765, ElongCoverage = ExpansionCoverage.InRange },
                },
                Flanges = new[]
                {
                    new RampFlangeInfo { Name = "F0", SectionJLimit = 11, SectionJOk = true, SectionJUndetermined = false,
                                         TipTotalDisplacementMm = 0.1234, TabDisplacementMm = 0.0456,
                                         DiscRadialMm = 0.0789, ClampElongMm = 0.0321,
                                         ReportCoverage = ExpansionCoverage.InRange },
                },
            };
            glass.RampSweep = new RampSweepResult
            {
                Points = new[] { pt }, Verdict = "过", VerdictDetail = "1 个设定点全部场有效。",
                Summary = "升温全程：过", ElapsedSeconds = 12.0, TAssemblyC = 20.0, Grade = "Pt",
            };
            glass.RampSeconds = 12.0;
        }
        if (withEmpty)
        {
            glass.EmptyTubeSteady = new LineResult
            { Ok = true, Converged = true, EmptyTube = true, MeshFineMm = 0.5, MeshCells = 12345 };
            glass.EmptyTubeSeconds = 34.0;
        }
        return glass;
    }

    [Fact]
    public void 结论按顺序印全名_不出判据代号_也不出命令行开关名()
    {
        string text = FinalCheckReport.Conclusions(Fake());
        _o.WriteLine(text);

        int i1 = text.IndexOf(FinalCheckReport.Step1, StringComparison.Ordinal);
        int i2 = text.IndexOf(FinalCheckReport.Step2, StringComparison.Ordinal);
        int i3 = text.IndexOf(FinalCheckReport.Step3, StringComparison.Ordinal);
        int i4 = text.IndexOf(FinalCheckReport.Step4, StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i2 >= 0 && i3 >= 0 && i4 >= 0,
            $"四个关名不全：{i1}/{i2}/{i3}/{i4}");
        Assert.True(i1 < i2 && i2 < i3 && i3 < i4,
            $"顺序错了：{FinalCheckReport.Step1}@{i1} → {FinalCheckReport.Step2}@{i2} → {FinalCheckReport.Step3}@{i3} → {FinalCheckReport.Step4}@{i4}");

        // 全名，不许出判据代号（连圈号都不用 —— 界面上另有阶段号与列表序号，形状一样含义无关）
        foreach (char code in Criteria.CodeChars) Assert.DoesNotContain(code.ToString(), text);
        // 界面上不许提命令行开关名；要指路只许指参数表那一项
        Assert.DoesNotContain("--", text);
    }

    [Fact]
    public void 某一关没跑就写没跑_不许写成过()
    {
        var g = Fake(withRamp: false, withEmpty: false);
        g.ThreeStateSkipped = new[]
        {
            $"{FinalCheckReport.Step1}：参数表里「{FinalCheckReport.SwitchLabel}」关掉了 —— **没跑不等于过**",
            $"{FinalCheckReport.Step3}：参数表里「{FinalCheckReport.SwitchLabel}」关掉了 —— **没跑不等于过**",
        };
        string text = FinalCheckReport.Conclusions(g);
        _o.WriteLine(text);

        foreach (string step in new[] { FinalCheckReport.Step1, FinalCheckReport.Step3 })
        {
            int i = text.IndexOf(step, StringComparison.Ordinal);
            string line = text[i..].Split('\n')[0];
            Assert.Contains("没跑", line);
            Assert.DoesNotContain("**过**", line);
        }
        Assert.Contains("没跑不等于过", text);

        // 伸长报告同理：没跑就说没跑，并指回参数表那一项（不是命令行）
        string el = FinalCheckReport.Elongation(g);
        Assert.Contains("没跑", el);
        Assert.Contains(FinalCheckReport.SwitchLabel, el);
        Assert.DoesNotContain("--", el);
    }

    [Fact]
    public void 伸长报告逐格印的就是升温全程算出来的那个数_不是报告层自己再算的()
    {
        var g = Fake();
        var seg = g.RampSweep!.Points[0].Segs[0];
        var fl = g.RampSweep!.Points[0].Flanges[0];

        string el = FinalCheckReport.Elongation(g);
        _o.WriteLine(el);

        // 逐格对：纸上印的 == 入口给的（四位小数，逐字）
        Assert.Contains(seg.TotalElongMm.ToString("0.0000"), el);
        Assert.Contains(fl.TipTotalDisplacementMm.ToString("0.0000"), el);
        Assert.Contains(fl.TabDisplacementMm.ToString("0.0000"), el);
        Assert.Contains(fl.DiscRadialMm.ToString("0.0000"), el);
        Assert.Contains(fl.ClampElongMm.ToString("0.0000"), el);
        // 基准两项（相对什么、按哪个牌号）必须印，否则这张表回答不了它自己
        Assert.Contains("20", el);
        Assert.Contains("Pt", el);

        // 反自证：把入口的数换掉，纸上必须跟着变（否则这条门什么都没守）
        seg.TotalElongMm = 9.8765;
        Assert.Contains("9.8765", FinalCheckReport.Elongation(g));
    }

    [Fact]
    public void 安装报告里有那一节_而且读的是同一份写法()
    {
        string sec = FinalCheckReport.Section(Fake());
        Assert.Contains(FinalCheckReport.SectionTitle, sec);
        Assert.Contains(FinalCheckReport.ElongTitle, sec);

        string root = HandoverDoc.Root();
        string ir = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Core", "InstallReport.cs"));
        Assert.Contains("FinalCheckReport.Section(r)", ir);
        // 报告层不许自己再拼一份结论
        Assert.DoesNotContain("第一关", ir);
    }

    [Fact]
    public void 界面终验那一段真的调了三关_开关在参数表且默认开()
    {
        string root = HandoverDoc.Root();
        string page = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("FinalCheck.Run(", page);
        Assert.Contains("FinalCheckReport.Conclusions(", page);
        Assert.Contains("FinalCheckReport.Elongation(", page);
        Assert.Contains("RunThreeStatesAtFinalCheck", page);

        // 开关：参数表里可见可改、默认开、说明里带出处、不带判据代号也不带命令行开关名
        var p = System.ComponentModel.TypeDescriptor.GetProperties(typeof(DesignInputs))["RunThreeStatesAtFinalCheck"]!;
        Assert.False(p.IsReadOnly);
        Assert.True(p.IsBrowsable);
        Assert.True(new DesignInputs().RunThreeStatesAtFinalCheck, "默认必须是开");
        Assert.Equal(FinalCheckReport.SwitchLabel, p.DisplayName);
        string cat = p.Category ?? "";
        Assert.DoesNotContain("只读", cat);
        string desc = p.Description ?? "";
        Assert.Contains("没跑", desc);
        Assert.DoesNotContain("--", desc);
        foreach (char code in Criteria.CodeChars) Assert.DoesNotContain(code.ToString(), desc);

        // 分类要在 Flow.Params 里登记过（不登记就落进一个界面上不存在的分组）
        Assert.Contains(PtOptimize.UI.Flow.Params, s => cat.Contains(s.CategoryPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void 三关跑完的结果真的挂到了带玻璃那一份上_下游读得到()
    {
        // 「赋了值 ≠ 用它的人读得到」—— 这条门守的是下游那一端：
        // 把结果挂上去之后，结论块与安装报告必须立刻看得见它。
        var g = Fake(withRamp: false, withEmpty: false);
        Assert.DoesNotContain("过**　（", FinalCheckReport.Conclusions(g));   // 还没挂：第一关只能写没跑

        var full = Fake();
        string text = FinalCheckReport.Conclusions(full);
        Assert.Contains($"第一关　{FinalCheckReport.Step1}：**过**", text);   // 升温那一关的判词原样带出
        // 第三关：挂上之后不许再写「没跑」，而且要印出它**自己那一次**的网格（数指得回出处）
        string l3 = text[text.IndexOf($"第三关　{FinalCheckReport.Step3}", StringComparison.Ordinal)..].Split('\n')[0];
        Assert.DoesNotContain("没跑", l3);
        Assert.Contains("单元 12345", l3);
        Assert.Contains("4245.33", text);       // 铂重从带玻璃那一份读

        // FinalCheck.Run 的挂载是一处、只有一处
        string root = HandoverDoc.Root();
        string fc = File.ReadAllText(Path.Combine(root, "Pt_Optimize", "Core", "FinalCheck.cs"));
        foreach (string f in new[] { "glass.RampSweep =", "glass.EmptyTubeSteady =", "glass.ThreeStateSkipped =" })
            Assert.Contains(f, fc);
        // 牌号要全链路生效：升温那一关的膨胀按参数表的牌号取，不许停在字面量 "Pt"
        Assert.Contains("Grade = inputs.GradeName", fc);

        // 「没跑」也要走同一条路：关掉开关时理由要登记到结果上（只写「没跑」说不出怎么打开它）
        var off = new LineResult { Ok = true, Converged = true };
        FinalCheck.Run(null, new DesignInputs(), off,
                       new FinalCheckOptions { RunRamp = false, RunEmptyTube = false, SkipReason = "测试注入的理由" });
        Assert.Equal(2, off.ThreeStateSkipped.Length);
        Assert.All(off.ThreeStateSkipped, s => Assert.Contains("测试注入的理由", s));
        Assert.Null(off.RampSweep);
        Assert.Null(off.EmptyTubeSteady);
        // 反自证：要跑却没给设计 ⇒ 当场抛，不许静默跳过
        Assert.Throws<ArgumentNullException>(() =>
            FinalCheck.Run(null, new DesignInputs(), new LineResult(), new FinalCheckOptions { RunRamp = true }));
    }
}
