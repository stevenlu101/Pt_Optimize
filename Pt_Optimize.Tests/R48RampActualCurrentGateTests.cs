using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 L 路 第五轮：**升温每一点的 J 要按该点实际电流判**，不是只按设计电流。
//  2026-09-17，Opus 5（补上一轮 Opus 4.6 留下的洞）。
//
//  病灶：RampSweep 的截面 J 直接读整线判据算好的那个数，而那个数的分子是
//  **设计电流**（20 °C/h 升温全程峰值，闭式、与工况无关）⇒ 轨迹上 8 个设定点印出来
//  是同一个 27.71，那张表回答不了「升到哪一段才超限」；而且一旦某点的实际电流
//  高过设计电流（共用片矢量合成、段间不同步时会发生），**会漏判**。
//
//  门守的是：设计电流那条过、实际电流那条超 ⇒ 升温必须「不过」并点名；
//  把口径改回只读设计电流（RampSweepOptions.SectionJFromActualCurrent = false）⇒
//  同一份输入被判成「过」，也就是本门当场变红。
// ════════════════════════════════════════════════════════════════════════════

public class R48RampActualCurrentGateTests
{
    /// <summary>生产几何：内置 0.8 档的第 0 片（走 LineCase.PlatesForJudge，与截面 J 判据同一份选择规则）。</summary>
    private static (FlangePlate Plate, double ClampLenMm, double Limit) Prod()
    {
        var lc = DesignSpec.W08.Clone().BuildCase(new DesignInputs());
        var plates = lc.PlatesForJudge();
        Assert.True(plates.Length > 0, "内置 0.8 档没有判据几何 —— 门的前提就不成立");
        return (plates[0], lc.Base.BusbarClampLengthMm, SectionSizing.JCheckOf(lc.JDesignAPerMm2));
    }

    /// <summary>把「最紧截面的 J = jWanted」倒推成电流 A（截面积正比关系，闭式一步）。</summary>
    private static double CurrentForJ(FlangePlate g, double clampLenMm, double jWanted)
    {
        const double probeA = 1000.0;
        double jProbe = SectionSizing.Worst(g, probeA, clampLenMm).JAPerMm2;
        Assert.False(double.IsNaN(jProbe) || jProbe <= 0, "探针电流下算不出截面 J —— 门的前提不成立");
        return probeA * jWanted / jProbe;
    }

    /// <summary>造一个只有一片、一段的设定点结果（其余字段用生产默认）。</summary>
    private static RampSweepPointResult Point(SectionJVerdict v, double limit, string name = "入口",
                                              double setpointC = 900, double clampC = 450)
        => new RampSweepPointResult
        {
            SetpointC = setpointC,
            ClampC = clampC,
            FieldValid = true,
            TubeJDesignAPerMm2 = 9.0, TubeJDesignLimit = 12.0, TubeJDesignOk = true, TubeJDesignUndetermined = false,
            Segs = new[] { new RampSegInfo { Name = "HC1", CurrentA = 1200, TubeJAPerMm2 = 9.4, TubeJLimit = 12.0, TubeJOk = true } },
            Flanges = new[]
            {
                new RampFlangeInfo
                {
                    Name = name, SectionJ = v, SectionJLimit = limit,
                    SectionJOk = v.Ok, SectionJUndetermined = v.Undetermined,
                }
            },
        };

    // ────────────────────────────────────────────────────────────────────────
    //  a ── 设计电流下过、实际电流下超 ⇒ 不过，并点名
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void a_设计电流下过而实际电流下超_必须判不过并点名()
    {
        var (g, clamp, limit) = Prod();
        double iDesign = CurrentForJ(g, clamp, limit * 0.5);    // 设计电流 ⇒ J ≈ 5.5，过
        double iActual = CurrentForJ(g, clamp, limit * 1.5);    // 实际电流 ⇒ J ≈ 16.5，超
        double jDesign = SectionSizing.Worst(g, iDesign, clamp).JAPerMm2;
        string whereDesign = SectionSizing.Worst(g, iDesign, clamp).Where;

        var v = RampSweep.JudgeSectionJ(g, jDesign, whereDesign, iActual, clamp, limit, useActualCurrent: true);

        Assert.True(v.DesignOk, $"设计电流那条本该过：{v.DesignJ:0.00} / {limit:0.#}");
        Assert.False(v.ActualOk, $"实际电流那条本该超：{v.ActualJ:0.00} / {limit:0.#}");
        Assert.False(v.Ok, "两条里有一条超限，合并判定必须是不过");
        Assert.False(v.Undetermined, "两条都算得出来，不该是判不了");
        Assert.True(v.Disagree, "两条结论不同，必须标出来");

        var (verdict, detail, disagree) = RampSweep.Aggregate(new[] { Point(v, limit) });
        Assert.Equal("不过", verdict);
        Assert.Contains("入口", detail);                       // 点名是哪一片
        Assert.Contains("该点实际电流", detail);                 // 点名是哪一条口径
        Assert.Contains($"{iActual:0} A", detail);             // 点名那个电流
        Assert.Single(disagree);
        Assert.Contains("入口", disagree[0]);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  b ── 注入：口径改回「只读设计电流」，同一份输入就被判成过 ⇒ 本门变红
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void b_改回只读设计电流_同一份输入会被漏判成过()
    {
        var (g, clamp, limit) = Prod();
        double iDesign = CurrentForJ(g, clamp, limit * 0.5);
        double iActual = CurrentForJ(g, clamp, limit * 1.5);
        double jDesign = SectionSizing.Worst(g, iDesign, clamp).JAPerMm2;

        var vOld = RampSweep.JudgeSectionJ(g, jDesign, "（设计电流那条）", iActual, clamp, limit, useActualCurrent: false);
        var (verdictOld, _, disagreeOld) = RampSweep.Aggregate(new[] { Point(vOld, limit) });

        Assert.True(vOld.Ok, "旧口径下这一片被判成过 —— 这正是要证明的漏判");
        Assert.True(double.IsNaN(vOld.ActualJ), "旧口径根本没算实际电流那条");
        Assert.False(vOld.ActualUsed, "旧口径不该标成用了实际电流");
        Assert.Empty(disagreeOld);
        Assert.Equal("过", verdictOld);   // ← 同一份输入，新口径 a 判「不过」。改回旧口径 ⇒ a 当场变红。

        // 生产默认必须是新口径
        Assert.True(new RampSweepOptions().SectionJFromActualCurrent,
            "生产默认必须按该点实际电流并列判；这个开关只供本门注入用");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  c ── 实际电流那条走的必须是判据同一份截面配方（不许自己另写一套截面）
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void c_实际电流那条与截面判据同一份配方()
    {
        var (g, clamp, limit) = Prod();
        double iActual = CurrentForJ(g, clamp, limit * 1.3);
        var byProduction = SectionSizing.Worst(g, iActual, clamp);
        var v = RampSweep.JudgeSectionJ(g, 1.0, "无所谓", iActual, clamp, limit, useActualCurrent: true);

        Assert.Equal(byProduction.JAPerMm2, v.ActualJ, 12);
        Assert.Equal(byProduction.Where, v.ActualWhere);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  d ── 判不了不许当过
    // ────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(true, false)]    // 设计那条算不出来（NaN）
    [InlineData(false, true)]    // 实际那条没有几何
    public void d_任一条算不出来都得判不了(bool designNaN, bool noPlate)
    {
        var (g, clamp, limit) = Prod();
        double iActual = CurrentForJ(g, clamp, limit * 0.5);          // 本身不超限
        double jDesign = designNaN ? double.NaN : SectionSizing.Worst(g, iActual, clamp).JAPerMm2;

        var v = RampSweep.JudgeSectionJ(noPlate ? null : g, jDesign, "x", iActual, clamp, limit, useActualCurrent: true);
        Assert.True(v.Undetermined, "算不出来必须标判不了");
        Assert.False(v.Ok, "判不了**不许当过**");

        var (verdict, detail, _) = RampSweep.Aggregate(new[] { Point(v, limit) });
        Assert.Equal("判不了", verdict);
        Assert.Contains("入口", detail);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  e ── 管 J 两条也各判各的，超限要点名到底是哪一条口径
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void e_管J的两条口径各判各的()
    {
        var (g, clamp, limit) = Prod();
        double iOk = CurrentForJ(g, clamp, limit * 0.5);
        var vOk = RampSweep.JudgeSectionJ(g, SectionSizing.Worst(g, iOk, clamp).JAPerMm2, "x", iOk, clamp, limit, true);

        // ① 该点实际电流下管 J 超限
        var ptActual = Point(vOk, limit);
        ptActual.Segs[0].TubeJAPerMm2 = 13.5; ptActual.Segs[0].TubeJOk = false;
        var (vA, dA, _) = RampSweep.Aggregate(new[] { ptActual });
        Assert.Equal("不过", vA);
        Assert.Contains("段「HC1」管 J（该点实际电流", dA);

        // ② 设计电流下管 J 超限（实际电流那条正常）
        var ptDesign = Point(vOk, limit);
        ptDesign.TubeJDesignAPerMm2 = 13.1; ptDesign.TubeJDesignOk = false;
        var (vD, dD, _) = RampSweep.Aggregate(new[] { ptDesign });
        Assert.Equal("不过", vD);
        Assert.Contains("管 J（设计电流", dD);

        // ③ 两条都正常 ⇒ 过
        var (vP, _, _) = RampSweep.Aggregate(new[] { Point(vOk, limit) });
        Assert.Equal("过", vP);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  f ── 源码钉死：升温里不许再自己抄一份管截面积公式，管 J 只准读整线解算好的那个
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void f_升温不许自己抄一份管截面积()
    {
        string src = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "RampSweep.cs"));
        Assert.DoesNotContain("Math.Pow(lc.TubeIdMm", src);
        Assert.Contains("s.TubeJAPerMm2", src);
        // 几何选择也只准走生产那一份
        Assert.Contains("lc.PlatesForJudge()", src);
        string lr = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        Assert.Contains("c.PlatesForJudge()", lr);
        // 判据侧那句就地三元式已经提走，不许留第二份
        Assert.DoesNotContain("c.FlangePlates is { Length: > 0 } ? c.FlangePlates : c.GeomForJudge", lr);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  g ── 自证：门 a 用的那两个电流，确实一个过一个超（前提不成立就不算门）
    // ────────────────────────────────────────────────────────────────────────
    [Fact]
    public void g_自证_门用的两个电流确实一过一超()
    {
        var (g, clamp, limit) = Prod();
        double iDesign = CurrentForJ(g, clamp, limit * 0.5);
        double iActual = CurrentForJ(g, clamp, limit * 1.5);
        Assert.True(iActual > iDesign, "实际电流必须大于设计电流，否则造不出这一族病");
        Assert.True(SectionSizing.Worst(g, iDesign, clamp).JAPerMm2 < limit);
        Assert.True(SectionSizing.Worst(g, iActual, clamp).JAPerMm2 > limit);
    }
}
