using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

// ════════════════════════════════════════════════════════════════════════════
//  R48 R 路 门：**沿舌长分段的舌保温剖面**（2026-09-17，Opus 5）
//
//  改了什么：FlangePlate.TabInsulProfile / LineCase.TabInsulProfile3dmPerPlate →
//    PlateThermalSetup.TabInsulProfile → ShellThermal.Solve(tabInsulProfile:)。
//    舌片区每一格按自己形心的 x 取该段的厚度；**保温导热与外表面对流辐射的配方一字不动**
//    （仍是 DesignScreen.PlateFluxWPerM2，包不包仍按 DesignScreen.FlangeFaceInsulated）。
//
//  这几道门守的是三件事（本仓库为这三件事各栽过不止一次）：
//    ① **默认路径逐位不变** —— 剖面为 null 时新代码一行都不许改数（门 c 用「全段等厚 ≡ 单值」把它钉死：
//       剖面那条路真的走了、走出来的数与单值那条**逐位**相同 ⇒ 新机构精确退化成旧机构）。
//    ② **新状态位默认没接上** —— 赋了值 ≠ 用它的人读得到。门 f／g 造在**下游**：
//       板件上的剖面必须真的到达 PlateThermalSetup，集总模型必须吃到集总口径那个数。
//    ③ **不许静默换口径** —— 换了剖面，判据说明里必须看得见（门 h）。
//    ④ **取值必须逐格对上号**（2026-09-17 补的门 i）—— 上面三件全守住了，段号仍可以整条错位一段：
//       把 TabTableAt／TabInsulMmAt 的段号注入 +1（越界钳在末段），①②③ 那九条**全绿**，判据早被改掉。
//       本树复现（2026-09-17，Opus 5，片 0 单片场解、管根钉 1150 °C，就是门 d 印的那三条）：
//         9.1→1.1 斜坡剖面 管孔净流入 **1.371 → 29.004 W**、舌面散热 201.9 → 236.8 W；
//         冷端剥光 5.147 → 14.475 W；全段等厚 2.563 W **不动**（等厚时错位看不见 ⇒ 门 c 天生抓不住这个病）。
//       （把关人在反解线那条剖面上量到的是 0.449 → 29.23 W，同一个病、不同算例。）
//       加了门 i 之后同一份注入：九条仍绿、**门 i 当场红**（「第 210 格 形心 x=−71.799 实际 1 mm，按 At(x) 该是 3.1 mm」）。
//
//  ⚠ 工单写「门：R48LineDumpTests 六算例 SHA 与网格转储不变」。**更正（2026-09-17，Opus 5）**：
//    这个测试类**存在**，在 `D:\WinForm\r48_I\Pt_Optimize.Tests\R48LineDumpTests.cs`（I 路，**尚未合入本树**）——
//    先前本文件与 HANDOVER 写的「本仓库里不存在」只在**本工作树**这个范围内成立，写成「不存在」是把范围说没了，已改。
//    本树跑不了它（类不在这里）⇒ 本轮改用等效的三道：门 c（全段等厚逐位等于单值）＋ 门 i（逐格取值）
//    ＋ 快速套件整体不变。
//    ⚠ **合入时必然要重记那六个 SHA**：R48LineDumpTests 的记录按「LineCase／SegmentOut／FlangeOut／DesignInputs 的
//    **公开成员集合**」算，而 R1 新加的公开成员（FlangePlate.TabInsulProfile、LineCase.TabInsulProfile3dmPerPlate、
//    PlateThermalSetup.TabInsulProfile／TabInsulLumpedMm、ShellThermalResult.TabInsulProfileNote／TabInsulMmCell／
//    TabSurfaceQProbeCell／TabSurfaceQProbeC）都在转储范围内 ⇒ 六个算例的「去文字」SHA 一个都不会保持原值。
//    那**不是**数变了（默认路径逐位不变由门 c 与快套件守）；重记时按该类注释的规矩写「旧值 → 新值 ＋ 依据」，
//    依据就是这一句加门 c。
// ════════════════════════════════════════════════════════════════════════════

public class R48TabInsulProfileGateTests
{
    // ── 算例：内置「管壁 0.8 · 留余量」＋ r48_L 求解器解出来的终值（只读那份 .txt，不重跑求解器）
    private static readonly double[] SolvedTabThickMm = { 0.73, 1.26, 1.26, 0.73 };
    private static readonly double[] SolvedTongueThickMm = { 2.03, 3.51, 3.51, 2.03 };
    private static readonly double[] SolvedTabInsulMm = { 5.1, 2.3, 3.6, 10.4 };

    internal static DesignSpec SolvedW08()
    {
        var d = DesignSpec.W08.Clone();
        d.TabThickMm = (double[])SolvedTabThickMm.Clone();
        d.TongueThickMm = (double[])SolvedTongueThickMm.Clone();
        d.TabInsulMm = (double[])SolvedTabInsulMm.Clone();
        d.RingMul = new[] { 1.0, 1.0, 1.0, 1.0 };
        d.SlotSpanDeg = new double[4];
        d.TabHoleRMm = new double[4];
        d.TabHoleAspect = new[] { 1.0, 1.0, 1.0, 1.0 };
        return d;
    }

    /// <summary>片 0 的板件＋生产导航网格＋一次电位场（与 R48ClampRecipeTests.SolvePlate0 同一套调用口径）。</summary>
    private static (FlangePlate g, ShellMesh m, double[] j, DesignInputs p2, double tRoot) Plate0Field()
    {
        var p = new DesignInputs();
        var d = SolvedW08();
        var lc = d.BuildCase(p, checkRamp: false);
        var g = d.Plate(0, d.DiscFloorMm(p));
        g.HoleRadiusMm = lc.TubeIdMm * 0.5 + lc.WallMm;
        var m = FlangeMesher.Build(g, 0, lc.MeshFineMm, lc.MeshCoarseMm, lc.MeshFineRadiusMm,
                                   lc.Base.BusbarClampLengthMm, lc.MeshInnerMm, lc.MeshInnerRadiusMm);
        double tSet = d.SetpointC[0];
        var sc = ShellCurrent.SolveFor(lc, m, 1213, Materials.PtResistivity(tSet) * 1e3, tSet);
        var p2 = SegmentSolver.Clone(lc.Base); p2.TSetC = tSet;
        if (lc.ClampTempC.Length > 0) p2.BusbarClampTempC = lc.ClampTempC[0];
        return (g, m, sc.JMagAPerMm2, p2, 1150.0);
    }

    private static ShellThermalResult SolveWith(FlangePlate g, ShellMesh m, double[] j, DesignInputs p2, double tRoot,
                                                double scalarMm, TabInsulProfile? prof)
        => ShellThermal.Solve(m, j, p2, tRoot, g.InsulBoundaryXResolved, g.TwoTabs,
                              tabBoundaryX: g.Tangent().X, tabInsulThickMm: scalarMm,
                              discRadiusMm: g.DiscRadiusMm, insulDiscRadiusMm: g.InsulDiscRadiusMm,
                              tabInsulProfile: prof);

    /// <summary>自由段 0 → −100 mm 等分 5 段（压接段 x &lt; −100 由「两端钳住」沿用末段）。</summary>
    internal static double[] FiveSegEdges() => TabInsulProfile.EvenEdges(0.0, -100.0, 5);

    // ══════════════════════════════════════════════════════════════════════
    //  a　剖面本身的算术：段的归属、两端钳住、边界必须递减、长度加权平均
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void a_剖面的段归属与两端钳住()
    {
        var e = FiveSegEdges();
        Assert.Equal(6, e.Length);
        Assert.Equal(0.0, e[0], 9);
        Assert.Equal(-100.0, e[5], 9);
        var pr = new TabInsulProfile(e, new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });

        Assert.Equal(5, pr.SegmentCount);
        // 段内
        Assert.Equal(1.0, pr.At(-10), 9);
        Assert.Equal(2.0, pr.At(-30), 9);
        Assert.Equal(3.0, pr.At(-50), 9);
        Assert.Equal(4.0, pr.At(-70), 9);
        Assert.Equal(5.0, pr.At(-90), 9);
        // 边界上那一点归**靠圆盘那一侧**的段（区间 [Edge[i+1], Edge[i])）
        Assert.Equal(1.0, pr.At(-20), 9);
        Assert.Equal(2.0, pr.At(-40), 9);
        Assert.Equal(5.0, pr.At(-100), 9);
        // 两端钳住：圆盘那侧（x>0，分界圆上的格子会落到这里）与压接段那侧（x<−100）
        Assert.Equal(1.0, pr.At(+30), 9);
        Assert.Equal(5.0, pr.At(-140), 9);

        // 边界必须严格递减（舌板长在 −x）；不递减就当场抛，不许默默算出一个数
        Assert.Throws<ArgumentException>(() => new TabInsulProfile(new[] { 0.0, -20.0, -20.0 }, new[] { 1.0, 2.0 }));
        Assert.Throws<ArgumentException>(() => new TabInsulProfile(new[] { 0.0, 20.0 }, new[] { 1.0 }));
        Assert.Throws<ArgumentException>(() => new TabInsulProfile(new[] { 0.0, -20.0 }, new[] { 1.0, 2.0 }));
        Assert.Throws<ArgumentException>(() => new TabInsulProfile(new[] { 0.0, -20.0 }, new[] { double.NaN }));
    }

    [Fact]
    public void a2_长度加权平均_含两端钳住那一段()
    {
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });
        // 只在剖面自己的跨度上：(1+2+3+4+5)/5 = 3
        Assert.Equal(3.0, pr.LengthWeightedMeanMm(0.0, -100.0), 9);
        // 整条舌板 0 → −140：末段 5.0 多算 40 mm ⇒ (20*(1+2+3+4+5) + 40*5) / 140
        Assert.Equal((20.0 * 15.0 + 40.0 * 5.0) / 140.0, pr.LengthWeightedMeanMm(0.0, -140.0), 9);
        // 全段等厚 ⇒ 平均就是那个值（门 c 靠这条保证集总口径也逐位不变）
        Assert.Equal(5.1, TabInsulProfile.Uniform(FiveSegEdges(), 5.1).LengthWeightedMeanMm(0.0, -140.0), 12);
        // 平移把负厚度钳在 0
        var sh = pr.Shifted(-2.5);
        Assert.Equal(0.0, sh.ThickMm[0], 9);
        Assert.Equal(2.5, sh.ThickMm[4], 9);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  b　不给剖面 ⇒ 新机构一点都不参与（默认路径没被碰）
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void b_不给剖面时舌面只有一张表()
    {
        var p = new DesignInputs();
        var t = ShellThermal.SurfaceLossTablesFor(p, 5.1);
        Assert.Empty(t.TabSeg);
        Assert.Null(t.TabProfile);
        foreach (double x in new[] { 30.0, 0.0, -10.0, -99.0, -140.0 })
            Assert.Same(t.Tab, t.TabAt(x));
        Assert.Equal(5.1, t.TabThickAt(-50, 5.1), 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  c　★★★ 全段等于原单值 ⇒ **逐位**等于原路径（工单点名的那道门）
    // ══════════════════════════════════════════════════════════════════════
    [Fact]   // 实测九条合计 1 s（单片场解，不是整线）⇒ 不标「慢」，进快速套件、提交钩子每次都跑
    public void c_全段等厚的剖面与单值逐位相同()
    {
        var (g, m, j, p2, tRoot) = Plate0Field();
        const double v = 5.1;
        var a = SolveWith(g, m, j, p2, tRoot, v, null);
        var b = SolveWith(g, m, j, p2, tRoot, v, TabInsulProfile.Uniform(FiveSegEdges(), v));

        Assert.Equal(a.T.Length, b.T.Length);
        int diff = 0; double worst = 0; int worstI = -1;
        for (int i = 0; i < a.T.Length; i++)
            if (!a.T[i].Equals(b.T[i])) { diff++; if (Math.Abs(a.T[i] - b.T[i]) > worst) { worst = Math.Abs(a.T[i] - b.T[i]); worstI = i; } }
        Assert.True(diff == 0,
            $"全段等厚 {v} mm 的剖面与单值 {v} mm 解出来的温度场有 {diff}/{a.T.Length} 格不是**逐位**相同"
          + (worstI >= 0 ? $"（最大差 {worst:E3} K，第 {worstI} 格 x={m.Centroid[worstI].X:0.###}）" : "")
          + " ⇒ 剖面这条路不是旧路径的精确退化，默认口径有被碰到的风险。");

        // 判据用得着的那几个积分量也逐位比（场相同 ⇒ 它们本就该相同；这几条是防「场同而后处理读了别的厚度」）
        Assert.True(a.QFromTubeW.Equals(b.QFromTubeW), $"管孔净流入 {a.QFromTubeW} vs {b.QFromTubeW}");
        Assert.True(a.QGenTabW.Equals(b.QGenTabW), $"舌板焦耳热 {a.QGenTabW} vs {b.QGenTabW}");
        Assert.True(a.QLossTabW.Equals(b.QLossTabW), $"舌板表面散热 {a.QLossTabW} vs {b.QLossTabW}");
        Assert.True(a.QToClampW.Equals(b.QToClampW), $"进铜排夹 {a.QToClampW} vs {b.QToClampW}");
        Assert.True(a.TTabMaxC.Equals(b.TTabMaxC), $"舌片区最高温 {a.TTabMaxC} vs {b.TTabMaxC}");
        Assert.True(a.LocalStabMargin.Equals(b.LocalStabMargin), $"局部热稳定裕度 {a.LocalStabMargin} vs {b.LocalStabMargin}");
        // 热解配方（与算例无关的规则部分）不许因为多了一个剖面参数就变
        Assert.Equal(a.Recipe.Rule, b.Recipe.Rule);
        Assert.Equal(ShellThermal.ProductionThermalRule, b.Recipe.Rule);
        // 口径必须看得见
        Assert.Equal("", a.TabInsulProfileNote);
        Assert.Contains("舌保温分段剖面", b.TabInsulProfileNote);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  d　剖面**真的改了场**（不许是个赋了值没人读的旋钮）
    // ══════════════════════════════════════════════════════════════════════
    [Fact]   // 实测九条合计 1 s（单片场解，不是整线）⇒ 不标「慢」，进快速套件、提交钩子每次都跑
    public void d_非均匀剖面必须改变温度场与管孔净流入()
    {
        var (g, m, j, p2, tRoot) = Plate0Field();
        const double v = 5.1;
        var uni = SolveWith(g, m, j, p2, tRoot, v, TabInsulProfile.Uniform(FiveSegEdges(), v));
        // 只把**冷端那一段**剥光（其余四段照旧 5.1）——形状变了、平均厚度也变了，两条都得动
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { v, v, v, v, 0.0 });
        var nu = SolveWith(g, m, j, p2, tRoot, v, pr);

        Assert.True(nu.T.Where((t, i) => !t.Equals(uni.T[i])).Any(), "非均匀剖面解出来的温度场与等厚的**一格都没差** ⇒ 剖面没接上");
        Assert.True(Math.Abs(nu.QFromTubeW - uni.QFromTubeW) > 0.5,
            $"把冷端 20 mm 从 {v} mm 剥到裸铂，管孔净流入才从 {uni.QFromTubeW:0.###} 变到 {nu.QFromTubeW:0.###}（差 < 0.5 W）⇒ 这根旋钮在判据上等于没动");
        // 冷端变薄 ⇒ 舌板表面散热必须变多（物理方向；不对就是厚度取值取反了）
        Assert.True(nu.QLossTabW > uni.QLossTabW,
            $"冷端剥光，舌板表面散热却没变多（{nu.QLossTabW:0.#} ≤ {uni.QLossTabW:0.#} W）⇒ 厚度按 x 取错了方向");

        // ★ 顺带量一个本线关心的数（只印不判）：厚度**重新分布但平均不变**时，判据动多少
        var same = new TabInsulProfile(FiveSegEdges(), new[] { 9.1, 7.1, 5.1, 3.1, 1.1 });   // 平均仍是 5.1
        Assert.Equal(v, same.LengthWeightedMeanMm(0.0, -100.0), 9);
        var sm = SolveWith(g, m, j, p2, tRoot, v, same);
        Console.WriteLine($"[R48 R 门 d] 平均厚度同为 {v} mm：等厚 管孔净流入 {uni.QFromTubeW:0.###} W／舌面散热 {uni.QLossTabW:0.#} W；"
                        + $"9.1→1.1 斜坡 {sm.QFromTubeW:0.###} W／{sm.QLossTabW:0.#} W；冷端剥光 {nu.QFromTubeW:0.###} W／{nu.QLossTabW:0.#} W。"
                        + "（单片、管根钉 1150 °C，不是整线耦合解，只作量级参考。）");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  e　厚度真的按 x 取：热端那一段用热端的厚度，冷端那一段用冷端的
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void e_舌面热流表按x逐段取()
    {
        var p = new DesignInputs();
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { 9.0, 7.0, 5.0, 3.0, 0.0 });
        var t = ShellThermal.SurfaceLossTablesFor(p, 5.1, tabProfile: pr);
        Assert.Equal(5, t.TabSeg.Length);
        Assert.Same(pr, t.TabProfile);

        // 每一段的表必须等于「拿该段厚度单独建的那张」——逐点比，不是比引用
        for (int k = 0; k < 5; k++)
        {
            var solo = ShellThermal.SurfaceLossTablesFor(p, pr.ThickMm[k]).Tab;
            double x = -10.0 - 20.0 * k;
            var got = t.TabAt(x);
            foreach (double tc in new[] { 200.0, 600.0, 900.0, 1150.0, 1400.0 })
                Assert.True(solo.Eval(tc).Equals(got.Eval(tc)),
                    $"第 {k} 段（x={x:0}）在 {tc:0} °C 的舌面热流 {got.Eval(tc)} 与按厚度 {pr.ThickMm[k]:0.#} mm 单独建的 {solo.Eval(tc)} 不逐位相同");
        }
        // 厚度 0 的那一段必须走**裸铂**表（与单值口径的 FlangeFaceInsulated 同一条规则，不是「包了 0 mm」）
        Assert.Same(t.Bare, t.TabAt(-95));
        Assert.Equal(0.0, t.TabThickAt(-95, 5.1), 12);
        Assert.Equal(9.0, t.TabThickAt(-5, 5.1), 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  f　接上了没有（下游门）：板件的剖面必须到达 PlateThermalSetup，集总口径必须算对
    // ══════════════════════════════════════════════════════════════════════
    [Fact]
    public void f_板件上的剖面必须到达热解输入与集总口径()
    {
        var p = new DesignInputs();
        var d = SolvedW08();
        var lc = d.BuildCase(p, checkRamp: false);

        // 不给剖面：集总口径 == 单值，剖面 == null（默认口径逐位不变）
        var s0 = LineRunner.PlateThermalInputs(lc, 0, 1213, null);
        Assert.Null(s0.TabInsulProfile);
        Assert.Equal(SolvedTabInsulMm[0], s0.TabInsulThickMm, 12);
        Assert.Equal(s0.TabInsulThickMm, s0.TabInsulLumpedMm, 12);

        // 给剖面：必须**同一个实例**到达（不是被复制成别的东西、更不是被丢掉）
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { 9.0, 7.0, 5.0, 3.0, 1.0 });
        lc.FlangePlates[0].TabInsulProfile = pr;
        var s1 = LineRunner.PlateThermalInputs(lc, 0, 1213, null);
        Assert.Same(pr, s1.TabInsulProfile);

        // 集总口径 = 沿整条舌板（切点 → 舌尖）按长度加权的平均厚度
        double x0 = lc.FlangePlates[0].Tangent().X, x1 = lc.FlangePlates[0].TabEndXMm;
        Assert.Equal(pr.LengthWeightedMeanMm(x0, x1), s1.TabInsulLumpedMm, 12);
        Assert.True(s1.TabInsulLumpedMm > pr.ThickMm[^1] && s1.TabInsulLumpedMm < pr.ThickMm[0],
            "集总口径必须落在剖面的最薄与最厚之间");

        // 图纸路径：逐片剖面数组也要到达
        var lc2 = d.BuildCase(p, checkRamp: false);
        var geom = lc2.FlangePlates.ToArray();
        lc2.FlangePlates = Array.Empty<FlangePlate>();
        lc2.GeomForJudge = geom;
        lc2.TabInsul3dmPerPlateMm = (double[])SolvedTabInsulMm.Clone();
        lc2.TabInsulProfile3dmPerPlate = new TabInsulProfile?[] { pr, null, null, null };
        Assert.Same(pr, lc2.TabInsulProfile3dmAt(0));
        Assert.Null(lc2.TabInsulProfile3dmAt(1));
    }

    /// <summary>
    /// ★ 集总模型（整片热稳定、升温两节点）必须吃 <see cref="PlateThermalSetup.TabInsulLumpedMm"/>，不许再读单值。
    /// 源码门：本仓库为「新状态位默认没接上」栽过两次 —— 光有行为门不够，两处调用点也要钉住。
    /// </summary>
    [Fact]
    public void g_集总模型的两处调用点都读集总口径()
    {
        string s = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "LineRunner.cs"));
        int lumped = s.Split("o.ThermalSetup.TabInsulLumpedMm").Length - 1;
        Assert.True(lumped == 2,
            $"集总模型读集总口径的地方应该正好 2 处（整片热稳定 FlangeStability、升温两节点 RampTwoNode.Inputs），实测 {lumped} 处");
        Assert.DoesNotContain("o.ThermalSetup.TabInsulThickMm", s);
        // 场解那条必须仍然吃剖面本身
        Assert.Contains("tabInsulProfile: s.TabInsulProfile", s);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  i　★★★ 逐格门：**这一格**拿到的必须是它自己那一段的厚度与那一张表
    //
    //  为什么补（2026-09-17，Opus 5；反解线把关第二轮）：
    //    把 ShellThermal.Solve 里 TabTableAt／TabInsulMmAt 的段号注入 **+1**（整条剖面错位一段），
    //    上面九条门**全绿**，而同一算例的管孔净流入从 0.449 W 翻到 29.23 W —— 判据早被改掉，门一条都没响。
    //    病灶看得清楚：门 e 守的是「**每一段的表**建得对」（只碰 SurfaceLossTablesFor，不碰取值），
    //    门 d 守的是「场**动了**」（错位之后场当然也动），没有一条守「**这一格**取的是不是它自己那一段」。
    //    ⇒ 本条直接比逐格实测值：厚度必须 == 剖面 At(本格形心 x)，舌面表必须 == 第 IndexAt(x) 段那一张。
    //    期望值只用两样东西：剖面自己的公开取值（At／IndexAt，门 a 守着）与**生产**那份建表函数
    //    （ShellThermal.SurfaceLossTablesFor）—— 不手抄配方，手抄的门守不住手抄的病。
    // ══════════════════════════════════════════════════════════════════════
    [Fact]   // 实测十条合计 1 s（单片场解，不是整线）⇒ 不标「慢」，进快速套件、提交钩子每次都跑
    public void i_每一格的舌保温厚度与舌面表都必须按本格形心取()
    {
        var (g, m, j, p2, tRoot) = Plate0Field();
        const double scalar = 5.1;
        // 五段**两两不同**（就是本线那条反解剖面的入口片）⇒ 段号错一位，厚度与表都必然跟着错
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { 9.4, 7.3, 5.2, 3.1, 1.0 });
        var th = SolveWith(g, m, j, p2, tRoot, scalar, pr);

        Assert.Equal(m.CellCount, th.TabInsulMmCell.Length);
        Assert.Equal(m.CellCount, th.TabSurfaceQProbeCell.Length);
        double probe = th.TabSurfaceQProbeC;
        Assert.InRange(probe, p2.TAmbC, ShellThermal.LossTableHiC);

        // 生产那份建表函数，参数与 Solve 的缺省口径一致（上限 NaN、节点 0 ⇒ 生产规则）
        var tabs = ShellThermal.SurfaceLossTablesFor(p2, scalar, tabProfile: pr);

        // 反空转 ①：各段的表必须两两不同，否则「错位一段」这件事本来就看不出来
        for (int k = 1; k < pr.SegmentCount; k++)
            Assert.False(tabs.TabSeg[k].Eval(probe).Equals(tabs.TabSeg[k - 1].Eval(probe)),
                $"第 {k} 段（{pr.ThickMm[k]:0.0} mm）与第 {k - 1} 段（{pr.ThickMm[k - 1]:0.0} mm）的舌面热流在 {probe:0} °C 上相同"
              + " ⇒ 这条门对「段号错一位」是瞎的，换一条厚度两两不同的剖面");

        var perSeg = new int[pr.SegmentCount];
        int nChecked = 0;
        for (int i = 0; i < m.CellCount; i++)
        {
            double cx = m.Centroid[i].X;
            int k = pr.IndexAt(cx);
            double wantMm = DesignScreen.FlangeFaceInsulated(pr.At(cx)) ? pr.At(cx) : 0.0;
            Assert.True(wantMm.Equals(th.TabInsulMmCell[i]),
                $"第 {i} 格（形心 x={cx:0.###}）实际用的舌保温厚度是 {th.TabInsulMmCell[i]} mm，"
              + $"而按剖面 At(x) 该是 {wantMm} mm（第 {k} 段，{pr.ThickMm[k]:0.0} mm）⇒ 逐格取厚度错位了");

            if (double.IsNaN(th.TabSurfaceQProbeCell[i])) continue;   // 圆盘保温格／分界圆混合格：归属另有门守
            double wantQ = tabs.TabSeg[k].Eval(probe);
            Assert.True(wantQ.Equals(th.TabSurfaceQProbeCell[i]),
                $"第 {i} 格（形心 x={cx:0.###}）实际用的舌面热流表在 {probe:0} °C 给 {th.TabSurfaceQProbeCell[i]:E6}，"
              + $"而按第 {k} 段（{pr.ThickMm[k]:0.0} mm）该是 {wantQ:E6} W/mm² ⇒ 这一格拿错了段的表");
            perSeg[k]++; nChecked++;
        }

        // 反空转 ②：真的比到过足够多的格，而且**每一段**都比到过（错位落到没比过的那一段上，门就又瞎了）
        Assert.True(nChecked >= 200, $"逐格门只比到 {nChecked} 格（全网格 {m.CellCount} 格）⇒ 这条门在空转");
        for (int k = 0; k < pr.SegmentCount; k++)
            Assert.True(perSeg[k] > 0,
                $"第 {k} 段（x {pr.EdgeXMm[k]:0.#}～{pr.EdgeXMm[k + 1]:0.#} mm）一格都没比到 ⇒ 错位落到这一段上门看不见");
        Console.WriteLine($"[R48 R 门 i] 逐格比过 {nChecked}/{m.CellCount} 格，各段格数 {string.Join("/", perSeg)}，"
                        + $"探针温度 {probe:0} °C。（2026-09-17 本树实测：注入段号 +1 ⇒ 本门当场红，其余九条全绿、"
                        + "门 d 那条斜坡剖面的管孔净流入 1.371 → 29.004 W。）");

        // 剖面为 null ⇒ 两个数组是空的：默认路径不分配、也不许有人读到上一次的残值
        var nul = SolveWith(g, m, j, p2, tRoot, scalar, null);
        Assert.Empty(nul.TabInsulMmCell);
        Assert.Empty(nul.TabSurfaceQProbeCell);
        Assert.True(double.IsNaN(nul.TabSurfaceQProbeC));
    }

    /// <summary>★ 换了口径必须看得见（不许静默）：剖面一进来，判据说明里就得有这句。</summary>
    [Fact]   // 实测十条合计 1 s（单片场解，不是整线）⇒ 不标「慢」，进快速套件、提交钩子每次都跑
    public void h_剖面必须出现在判据说明里()
    {
        var (g, m, j, p2, tRoot) = Plate0Field();
        var pr = new TabInsulProfile(FiveSegEdges(), new[] { 9.0, 7.0, 5.0, 3.0, 1.0 });
        var th = SolveWith(g, m, j, p2, tRoot, 5.1, pr);
        Assert.Contains("舌保温分段剖面", th.TabInsulProfileNote);
        Assert.Contains("9.00 mm", th.TabInsulProfileNote);
        Assert.Contains("1.00 mm", th.TabInsulProfileNote);
    }
}
