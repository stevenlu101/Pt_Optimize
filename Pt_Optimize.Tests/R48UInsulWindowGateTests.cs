using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ U 路（2026-09-18，Opus 5）：**每片舌保温的可行窗口**进了生产件的门。
///
/// L 路的探针（<c>R48LInsulWindowTests</c>）09-17 量出 W08 四片的窗口宽 0.1／0.0／0.1／0.2 mm，
/// 其中共用1 那一片的窗口是 [2.3, 2.3] —— **里面一个 0.5 的倍数都没有**。
/// 那句结论只活在一份 txt 里，工程师点不到（记忆：对话的知识必须落到 APP 上）。
/// ⇒ 提成 <see cref="InsulWindow.Measure"/>，挂到整线结果、判据页与安装报告上。
///
/// 本档的门（全部毫秒级：过不过由**注入的**评估函数给，窗口的切段／落档／判定这套
/// **生产逻辑**原样受审 —— 手抄一份窗口算法来验窗口算法，守不住任何东西）：
///   ① 造窗口 [2.3, 2.3] ⇒ 一个档都落不进、结论「当前判据下无可制造设计」并点名那几片；
///   ② 造窗口 [2.2, 2.8] ⇒ 恰好落 2.5 一档，判「缠得出来」；
///   ③ 0 层合法：窗口盖住裸舌 ⇒ 那一档算数，且写成「裸舌 0 层」；
///   ④ 边界没探到要说出来，不许把扫描宽度当窗口宽；解值自己不过时照实报；
///   ⑤ 接线门：整线结果带着它、安装报告与判据页**读的就是这一份**，终验会去量 ——
///      任何一处改回不读窗口 ⇒ 红。
/// </summary>
public class R48UInsulWindowGateTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { HandoverDoc.Root() }.Concat(parts).ToArray()));

    /// <summary>一份四片的解析设计，四片舌保温都设成 <paramref name="solvedMm"/>。</summary>
    private static DesignSpec Design(double solvedMm)
    {
        var d = DesignSpec.W08.Clone();
        for (int j = 0; j < d.TabInsulMm.Length; j++) d.TabInsulMm[j] = solvedMm;
        return d;
    }

    /// <summary>注入的「这一点过不过」：落在 [lo, hi] 内就算过，否则报一条点名的判据。不解场、毫秒级。</summary>
    private static InsulWindow.Options Oracle(double lo, double hi)
        => new()
        {
            MaxDegreeOfParallelism = 1,
            Evaluate = (d, j, mm) => new InsulWindow.Point
            {
                Feasible = mm >= lo - 1e-9 && mm <= hi + 1e-9,
                Why = mm < lo ? "管根低于热偶读数 6.700/5.000" : mm > hi ? "管孔净流入 须为正 -0.100/0.000" : "",
            },
        };

    // ══════════════════════════════════════════════════════════════════════
    //  ① 窗口 [2.3, 2.3] ⇒ 无档 ⇒ 无可制造设计，点名
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 窗口里一个档都没有时_判无可制造设计并点名()
    {
        var res = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(2.3, 2.3));

        Assert.Equal(4, res.Plates.Length);
        foreach (var p in res.Plates)
        {
            Assert.True(p.SolvedFeasible, $"{p.Name} 解值那一点应当过（注入的评估函数说它过）");
            Assert.Equal(2.3, p.LoMm, 9);
            Assert.Equal(2.3, p.HiMm, 9);
            Assert.Equal(0.0, p.WidthMm, 9);
            Assert.False(p.OpenLo); Assert.False(p.OpenHi);        // 两头都探到了
            Assert.Empty(p.LayerMm);                                // 2.3 不是 0.5 的倍数，也不是裸舌
            Assert.False(p.HasLayer);
            Assert.Contains("管根低于热偶读数", p.BlockedBelow);      // 卡哪条要点名
            Assert.Contains("管孔净流入", p.BlockedAbove);
        }

        Assert.False(res.Manufacturable);
        Assert.Equal(4, res.NoLayerPlates.Length);
        Assert.Contains("当前判据下无可制造设计", res.Verdict);
        foreach (var p in res.Plates) Assert.Contains(p.Name, res.Verdict);      // 点名
        Assert.Contains("判据没有被放宽", res.Verdict);                            // 不改判据、不硬凑
        Assert.Equal(4, res.NarrowPlates.Length);                                 // 0.0 < 一层 0.5 ⇒ 也算窄
        // 报告要把「最接近的设计」与差在哪条一起给出来
        string rep = res.Report();
        Assert.Contains("2.3", rep);
        Assert.Contains("一个都没有", rep);
        Assert.False(Criteria.HasCode(rep), "报告里不许出现判据代号");
        Assert.DoesNotContain("--", rep);                                          // 界面上不许出现命令行开关名
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ② 窗口 [2.2, 2.8] ⇒ 落 2.5 一档
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 窗口盖住一个整数层时_那一档算得出来()
    {
        var res = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(2.2, 2.8));

        foreach (var p in res.Plates)
        {
            Assert.Equal(2.2, p.LoMm, 9);
            Assert.Equal(2.8, p.HiMm, 9);
            Assert.Equal(0.6, p.WidthMm, 9);
            Assert.Single(p.LayerMm);
            Assert.Equal(2.5, p.LayerMm[0], 9);
            Assert.Contains("5 层", p.LayerText[0]);                // 2.5 mm = 5 层（每层 0.5）
            Assert.False(p.NarrowerThanOneLayer(res.LayerThickMm));  // 0.6 > 0.5
        }
        Assert.True(res.Manufacturable);
        Assert.Empty(res.NoLayerPlates);
        Assert.Contains("缠得出来", res.Verdict);
        Assert.DoesNotContain("无可制造设计", res.Verdict);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 0 层（裸舌）是合法的一档
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 裸舌是合法的一档_零层不算没有档()
    {
        var o = new InsulWindow.Options();
        double bare = o.Solver.InsLoMm;                            // 0.30，不在 0.5 的格子上
        var res = InsulWindow.Measure(Design(bare), new DesignInputs(), Oracle(bare, bare + 0.15));

        foreach (var p in res.Plates)
        {
            Assert.Single(p.LayerMm);
            Assert.Equal(bare, p.LayerMm[0], 9);
            Assert.Contains("裸舌 0 层", p.LayerText[0]);
        }
        Assert.True(res.Manufacturable);

        // 下界之外的点不解、标「下界外」（不缠就是裸舌，再往下不是一个做得出来的设计）
        var below = res.Plates[0].Points.Where(x => x.Mm < bare - 1e-9).ToArray();
        Assert.NotEmpty(below);
        Assert.All(below, x => Assert.Null(x.Feasible));
        Assert.All(below, x => Assert.Contains("下界外", x.Why));

        // 落档表只有一份写法，且它自己就认得裸舌与整数层
        Assert.Equal(new[] { bare, 0.5, 1.0 }, InsulWindow.LayerGridWithin(0.0, 1.0, o.Solver));
        Assert.Empty(InsulWindow.LayerGridWithin(2.3, 2.3, o.Solver));
        Assert.Equal(new[] { 2.5 }, InsulWindow.LayerGridWithin(2.2, 2.8, o.Solver));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ④ 边界没探到／解值自己不过 —— 照实报
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 边界没探到与解值自己不过_都照实报()
    {
        // 全程可行 ⇒ 两头都没探到，不许把扫描宽度 2.0 当成窗口宽
        var open = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(-99, 99));
        foreach (var p in open.Plates)
        {
            Assert.True(p.OpenLo && p.OpenHi);
            Assert.Equal("", p.BlockedBelow);
            Assert.Equal("", p.BlockedAbove);
        }
        Assert.Contains("边界没探到", open.Report());

        // 解值那一点自己就不过 ⇒ 不给窗口，但把扫到的可行段列出来
        var bad = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(3.0, 3.2));
        foreach (var p in bad.Plates)
        {
            Assert.False(p.SolvedFeasible);
            Assert.True(double.IsNaN(p.LoMm) && double.IsNaN(p.WidthMm));
            Assert.False(p.HasLayer);
            Assert.Contains("管根低于热偶读数", p.SolvedWhy);
            Assert.Single(p.OtherFeasible);
            Assert.Equal(3.0, p.OtherFeasible[0].Lo, 9);
            Assert.Equal(3.2, p.OtherFeasible[0].Hi, 9);
        }
        Assert.Contains("当前判据下无可制造设计", bad.Verdict);   // 解值不过 ⇒ 也没有档
        Assert.Contains("解值自己就不过", bad.Report());
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ⑤ 接线门：改回不读窗口 ⇒ 红
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void 接线门_整线结果带着它_报告与判据页读的就是这一份_终验会去量()
    {
        // 整线结果带得动（默认 null = 这一次没量，不是「过了」）
        var r = new LineResult();
        Assert.Null(r.TabInsulWindow);
        var res = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(2.2, 2.8));
        r.TabInsulWindow = res;
        Assert.Same(res, r.TabInsulWindow);

        // 安装报告：读的就是这一份，没量时照实说没量（**不留空白**）
        string rep = Src("Pt_Optimize", "Core", "InstallReport.cs");
        Assert.Contains("r.TabInsulWindow is { } win", rep);
        Assert.Contains("win.Report()", rep);
        Assert.Contains("没量 ≠ 缠得出来", rep);
        Assert.Contains("本报告不可作为安装依据", rep);

        // ③ 判据页：一张小表，只读结果那一份，不重算
        string page = Src("Pt_Optimize", "UI", "LineDesignPage.cs");
        Assert.Contains("(\"舌保温可行窗口\", winHost)", page);
        Assert.Contains("private void FillInsulWindow(LineResult r)", page);
        Assert.Contains("FillInsulWindow(r);", page);
        Assert.Contains("var win = r.TabInsulWindow;", page);
        // 终验（加密复算收敛之后）去量一次；开关在参数表，不是命令行
        Assert.Contains("_base.MeasureInsulWindowAtFinalCheck && res.Converged", page);
        Assert.Contains("InsulWindow.Measure(d2, _base, null, prog, _cts.Token)", page);
        Assert.Contains("_last.TabInsulWindow = win;", page);

        // 探针改调生产件 —— 不许留第二份实现
        string probe = Src("Pt_Optimize.Tests", "R48LInsulWindowTests.cs");
        Assert.Contains("InsulWindow.Measure(", probe);
        Assert.DoesNotContain("Parallel.For", probe);
        Assert.DoesNotContain("while (a - 1 >= 0 && ok[a - 1] == true) a--;", probe);

        // 开关本身：参数表里看得见，默认开
        Assert.True(new DesignInputs().MeasureInsulWindowAtFinalCheck);
        var pi = typeof(DesignInputs).GetProperty(nameof(DesignInputs.MeasureInsulWindowAtFinalCheck))!;
        string disp = pi.GetCustomAttributes(typeof(System.ComponentModel.DisplayNameAttribute), false)
                        .Cast<System.ComponentModel.DisplayNameAttribute>().First().DisplayName;
        Assert.Equal("终验时量每片舌保温的可行窗口", disp);
        Assert.False(Criteria.HasCode(disp));
        Assert.DoesNotContain("--", disp);
    }

    /// <summary>默认口径：在**判决用的那张网格**上量；导航网格是可选的快筛，且报告要标明它不作判决。</summary>
    [Fact]
    public void 默认在判决网格上量_导航网格只作快筛且写明()
    {
        Assert.False(new InsulWindow.Options().UseNavigationMesh);
        var nav = InsulWindow.Measure(Design(2.3), new DesignInputs(),
                                      new InsulWindow.Options { UseNavigationMesh = true, MaxDegreeOfParallelism = 1, Evaluate = (d, j, mm) => new InsulWindow.Point { Feasible = true } });
        Assert.True(nav.NavigationMesh);
        Assert.Contains("只作快筛，不作判决", nav.Report());

        var fine = InsulWindow.Measure(Design(2.3), new DesignInputs(), Oracle(2.2, 2.8));
        Assert.False(fine.NavigationMesh);
        var (wantFine, wantRadius) = MeshVerify.RequiredMeshFor(Design(2.3));
        Assert.Equal(wantFine, fine.MeshFineMm, 9);          // 与交付判定同一张网格，不另抄配方
        Assert.Equal(wantRadius, fine.MeshRadiusMm, 9);
    }
}
