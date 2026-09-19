using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ R48 L（2026-09-17，Opus 5）：**终局复核与三关，必须跑在最后一遍求根所用的那张网格上。**
///
/// ══ 病灶一：<c>Solver.Finish</c> 手抄了配方，只抄了一半
///
/// 造算例网格的配方全仓只有一份 —— <see cref="Solver.ApplyCaseMesh"/>，它有三支：
/// <code>
///   FineMm &gt; 0                      ⇒ 整张自相似加密（中带／粗区／内带一起缩）
///   FineMm ≤ 0 且 FineRadiusMm &gt; 0  ⇒ **导航档：尺寸不变，只统一细区半径**
///   ScreenCoarseMm &gt; 0              ⇒ 粗筛只放粗平坦区
/// </code>
/// <c>Finish</c> 改前手写的是 <c>if (lastOpt.FineMm &gt; 0) MeshAdapt.RefineWholeMesh(...)</c> ——
/// **只有第一支**。于是「第二遍没跑（okNav 为假，或 FineMm = 0）而 FineRadiusMm &gt; 0」的每一趟：
/// <code>
///   求根（EvalRaw → ApplyCaseMesh）：细区半径 = lastOpt.FineRadiusMm（如 59）
///   终局复核（Finish，改前）       ：什么都不做 ⇒ 退回 LineCase 默认 50
/// </code>
/// 而细区半径这一维的实测（记在 <c>Solver.Solve</c> 里 navOpt 那段）：同一设计、同一 2.0 mm，
/// 只差这一维 ⇒ 管孔净流入 **+3.267（R=50）对 −6.533（R=59）**，差 9.8 W、**符号相反**（限值 0）。
/// 两边都不报错 —— 正是本项目最怕的「看起来正常的错数」。
///
/// ⇒ 修法：<c>Finish</c> 改调 <see cref="Solver.ApplyCaseMesh"/>。**门不许手抄生产配方**，
///   所以本门守的是「Finish 真的调那个公开函数」＋「那个公开函数的导航支不是空操作」两条，
///   而不是在这里再抄一遍配方去比数。
///
/// ══ 病灶二：<c>RampSweep</c>（① 升温全程）没有网格口径这根线
///
/// 它内部 BuildCase 之后什么都不设 ⇒ 永远是整线算例缺省（细区 2.0 mm）。
/// 细网格第二遍求出来的根，若 ① 仍判在导航网格上，三关就不在同一张网格上。
/// 现在 <see cref="RampSweepOptions.Mesh"/> 为 null（默认）时一行不动 = 改前行为；
/// 给了就原样交给同一个 <see cref="Solver.ApplyCaseMesh"/>。
/// </summary>
public class R48LFinishMeshGateTests
{
    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { HandoverDoc.Root() }.Concat(parts).ToArray()));

    /// <summary>Finish 必须调公开配方，且**不许**再自己抄一份加密调用。</summary>
    [Fact]
    public void 终局复核调的是唯一那份网格配方()
    {
        string s = Src("Pt_Optimize", "Core", "Solver.cs");
        int at = s.IndexOf("private static SolverResult Finish(", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 Solver.Finish —— 断言失去了对象，不能算通过");
        // 取 Finish 的函数体（到下一个 private/public 成员为止，够覆盖 BuildCase 那几行）
        int end = s.IndexOf("\n    /// <summary>", at, StringComparison.Ordinal);
        string body = end > at ? s.Substring(at, end - at) : s.Substring(at);

        // ★ 2026-09-18 Opus 5（合并 J×L 改门，写明变因）：J 路把 Finish 里造算例那两行提成了公开的 Solver.FinishCase（门 R48J_SolverMeshAndMarkerGateTests.J1，行为逐字相同）。
        //   本门原来在 **Finish 的函数体**里找那两行，提出去之后就找不到了 ⇒ 改成「Finish 只经 FinishCase 造算例」＋「那两行在 FinishCase 体内」两段各钉一次。
        //   口径没有放宽：手抄的 MeshAdapt.RefineWholeMesh(lcF 那一行在**两段里都**不许出现。
        Assert.Contains("var lcF = FinishCase(d, baseIn, lastOpt);", body);
        Assert.DoesNotContain("MeshAdapt.RefineWholeMesh(lcF", body);

        int fc = s.IndexOf("public static LineCase FinishCase(", StringComparison.Ordinal);
        Assert.True(fc > 0, "找不到 Solver.FinishCase —— 断言失去了对象，不能算通过");
        int fe = s.IndexOf("return lcF;", fc, StringComparison.Ordinal);
        Assert.True(fe > fc, "FinishCase 的函数体读不出来");
        string fbody = s.Substring(fc, fe - fc);
        Assert.Contains("var lcF = d.BuildCase(baseIn, checkRamp: true);", fbody);
        Assert.Contains("ApplyCaseMesh(lcF, lastOpt);", fbody);
        // ★ 手抄的那一行必须已经不在了：它只覆盖 FineMm > 0 那一支
        Assert.DoesNotContain("MeshAdapt.RefineWholeMesh(lcF", fbody);
    }

    /// <summary>
    /// 行为门：<see cref="Solver.ApplyCaseMesh"/> 的**导航支**（FineMm ≤ 0 且 FineRadiusMm &gt; 0）
    /// 不是空操作 —— 它改细区半径而不动尺寸。改前 Finish 漏掉的正是这一支。
    /// </summary>
    [Fact]
    public void 导航档那一支真的改了细区半径而不动尺寸()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();
        double radius = MeshVerify.RequiredMeshFor(d).RadiusMm;

        var lc0 = d.BuildCase(p);                       // 什么都不设 = 改前 Finish 的网格
        var lc1 = d.BuildCase(p);
        Solver.ApplyCaseMesh(lc1, new SolverOptions { FineMm = 0, FineRadiusMm = radius });

        Assert.Equal(lc0.MeshFineMm, lc1.MeshFineMm, 9);        // 尺寸一位不动
        Assert.Equal(lc0.MeshCoarseMm, lc1.MeshCoarseMm, 9);    // 粗区一位不动
        Assert.Equal(radius, lc1.MeshFineRadiusMm, 9);          // 半径统一过来了
        // ★ 而这两个数**不一样** —— 不一样，漏调才会出事；一样的话本门就没有对象
        Assert.NotEqual(lc0.MeshFineRadiusMm, lc1.MeshFineRadiusMm);
    }

    /// <summary>W08／W06 这两个形状确实落在「半径要统一」的那一支上（否则上一条门是空守）。</summary>
    [Theory]
    [InlineData("W08")]
    [InlineData("W06")]
    public void 两个在跑的设计其网格无关口径半径都不等于算例缺省(string which)
    {
        var d = (which == "W08" ? DesignSpec.W08 : DesignSpec.W06).Clone();
        var (fine, radius) = MeshVerify.RequiredMeshFor(d);
        var lc = d.BuildCase(new DesignInputs());
        Assert.True(fine > 0, $"{which}：网格无关口径算不出细区尺寸");
        Assert.True(fine < lc.MeshFineMm, $"{which}：网格无关口径要求的细区 {fine} mm 竟不比导航网格 {lc.MeshFineMm} mm 细");
        Assert.NotEqual(lc.MeshFineRadiusMm, radius);
    }

    /// <summary>① 升温全程的网格口径：默认 null = 改前行为；调用方给了就原样走公开配方。</summary>
    [Fact]
    public void 升温全程的网格口径接的是同一份配方()
    {
        Assert.Null(new RampSweepOptions().Mesh);
        string s = Src("Pt_Optimize", "Core", "RampSweep.cs");
        Assert.Contains("if (opt.Mesh is not null) Solver.ApplyCaseMesh(lc, opt.Mesh);", s);
        Assert.Contains("public SolverOptions? Mesh = null;", s);
    }

    /// <summary>
    /// 行为门：给了 <see cref="RampSweepOptions.Mesh"/> 之后，升温那一关的整线算例**真的**换了网格。
    /// 用一个设定点、一张比缺省还粗的网格（6 mm）跑 —— 只验接线，不验物理。
    /// </summary>
    [Fact]
    public void 升温全程给了网格口径就真的换了网格()
    {
        var d = DesignSpec.W08.Clone();
        var p = new DesignInputs();
        var one = new double[] { 300 };

        var rDefault = RampSweep.Run(d, p, new RampSweepOptions { TrajectoryC = one, RunClampAlt = false });
        var rMeshed = RampSweep.Run(d, p, new RampSweepOptions
        {
            TrajectoryC = one,
            RunClampAlt = false,
            Mesh = new SolverOptions { FineMm = 6.0, FineRadiusMm = 59.0 },
        });

        Assert.Single(rDefault.Points);
        Assert.Single(rMeshed.Points);
        double navFine = d.BuildCase(p).MeshFineMm;
        Assert.Equal(navFine, rDefault.Points[0].LineRes.MeshFineMm, 6);
        Assert.Equal(6.0, rMeshed.Points[0].LineRes.MeshFineMm, 6);
    }
}
