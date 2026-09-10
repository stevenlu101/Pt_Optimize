using System;
using System.IO;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **孔位由场定，逐案**（R12，用户 2026-09-08 设计因果链第 ③ 步；2026-09-09 落地）。
///
/// 移除优先级 = 导热贡献 ÷ 电流密度（<see cref="RemovalPriority"/>，与 2026-09-05 那次量法同一份，
/// deliverable/移除优先级.txt）。据此逐片定：圆盘槽的槽心角、舌孔孔心。
///
/// 四道门：① 单舌片（W08／Pt_Heater1 那类）算出的槽心 ≈ 0°（与移除优先级.txt「背对舌片」一致）；
/// ② 双舌片对称进电时槽心离开 0°（落到 ±90° 一带）；③ 存档往返；④ 出图 spec 带角度、Rhino 探针在时真写图读回。
/// </summary>
// ★ 2026-09-09：起 Rhino 子进程的测试类**串行**（同一 xunit collection）—— 并行起两个 RhinoCore 会互相挂死到 10 分钟超时（合并 R12/R13 时抓到，单跑 12 s 过）
[Collection("Rhino 子进程")]
public class FieldPlacementTests
{
    /// <summary>Pt_Heater1 交接后的构型（移除优先级.txt 用的就是它）。</summary>
    internal static FlangePlate Heater1(bool twoTabs = false) => new()
    {
        DiscRadiusMm = 60, HoleRadiusMm = 26,
        TabEndXMm = -199.5, TabEndHalfWidthMm = 40,
        ThicknessMm = 2.0, ThickenedMm = 2.0, TabThicknessMm = 2.0,
        TabParallel = false, WeldFilletLegMm = 0,
        TwoTabs = twoTabs,
    };

    /// <summary>解一次场（电流 + 温度），回网格与三个场 —— 与 RemovalPriorityTests 同一条路。</summary>
    internal static (ShellMesh Mesh, double[] J, double[] T, double[] V, double QFromTubeW, double JPeak) Solve(FlangePlate plate, double currentA = 1000)
    {
        var baseIn = new DesignInputs();
        var c = new LineCase { Base = baseIn };
        var mesh = FlangeMesher.Build(plate, 0, 2.0, 8.0, 50.0, baseIn.BusbarClampLengthMm, 0, 0);
        Assert.True(mesh.CellCount > 300, $"网格只有 {mesh.CellCount} 单元 —— 测量退化");
        var cur = ShellCurrent.SolveFor(c, mesh, totalCurrentA: currentA, rhoRefOhmMm: 1.1e-4);
        var th = ShellThermal.Solve(mesh, cur.JMagAPerMm2, baseIn, tRootC: 1150, insulBoundaryX: double.NaN);
        double jPk = cur.JMagAPerMm2.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
        return (mesh, cur.JMagAPerMm2, th.T, cur.V, th.QFromTubeW, jPk);
    }

    /// <summary>① 单舌片：最该挖的在背对舌片那一侧 ⇒ 槽心 ≈ 0°。</summary>
    [Fact]
    public void 单舌片_槽心落在背对舌片的零度一带()
    {
        var (mesh, J, T, _, _, _) = Solve(Heater1());
        var p = RemovalPriority.Compute(mesh, J, T, 1150);
        // 反自证：优先级分得出高下（否则下面是空转）
        var valid = p.Where(v => !double.IsNaN(v)).ToArray();
        Assert.True(valid.Max() > valid.Min() * 2 + 1e-9, "优先级几乎是常数");
        // 与 deliverable/移除优先级.txt 一致：最高的 12 个单元全在圆盘背侧（x > 0）
        var top = Enumerable.Range(0, mesh.CellCount).Where(i => !double.IsNaN(p[i]))
                            .OrderByDescending(i => p[i]).Take(12).ToArray();
        Assert.All(top, i => Assert.True(mesh.Centroid[i].X > 0, $"单元 {i} 在 x={mesh.Centroid[i].X:0.0}，不在背侧"));

        var (deg, score) = RemovalPriority.SlotCenterDeg(mesh, p, 27, 40, 15);
        Assert.False(double.IsNaN(deg));
        Assert.True(Math.Abs(deg) <= 15, $"单舌片槽心算出 {deg:0}°（分数 {score:0.00}），不在背对舌片的 0° 一带");
    }

    /// <summary>② 双舌片对称进电（两舌在 0°/180°）：最该挖的在 ±90° ⇒ 槽心离开 0°。同分取正角。</summary>
    [Fact]
    public void 双舌片对称进电_槽心离开零度()
    {
        var (mesh, J, T, _, _, _) = Solve(Heater1(twoTabs: true));
        var p = RemovalPriority.Compute(mesh, J, T, 1150);
        var (deg, score) = RemovalPriority.SlotCenterDeg(mesh, p, 27, 40, 15);
        Assert.False(double.IsNaN(deg));
        Console.WriteLine($"双舌片：槽心 {deg:0}°（分数 {score:0.00}）");
        Assert.True(Math.Abs(deg) >= 45, $"双舌片槽心算出 {deg:0}°（分数 {score:0.00}），还贴着 0° —— 场没有把两舌之间那一带挑出来");
        Assert.True(deg > 0, $"同分该取正角，算出 {deg:0}°");
    }

    /// <summary>
    /// ★ 不变式：算出来的槽心角，窗口里必须**真的有带内单元**（答案不许落在没有单元的角向上）。
    /// W08 那类小盘（R30）：默认槽带 r33.8–35.8 整个在盘缘之外、只压在舌根上（角向 180° ± 20°）——
    /// 这时槽本来就开不出来，但算出的角度也得落在有单元的那一带，不能是 0° 一带。
    /// （2026-09-09 对帐轨迹第 1 轮印出 −1°，第 2 轮印出 −174°，本门就是为它加的。）
    /// </summary>
    [Fact]
    public void 槽心角必须落在带内有单元的角向上()
    {
        var d = DesignSpec.Builtin[0].Clone();          // W08：盘 R30、舌 140×60
        var p0 = new DesignInputs();
        double floor = d.DiscFloorMm(p0);
        var g = d.Plate(0, floor);
        var (mesh, J, T, _, _, _) = Solve(g, 1214);
        var p = RemovalPriority.Compute(mesh, J, T, 1150);
        double weld = Math.Max(Math.Max(d.TabThickMm[0], floor), d.WallMm);
        var (rin, rout) = d.SlotBandMm(weld);
        Assert.True(rin > d.DiscRadiusMm, $"本门要的正是「槽带在盘缘之外」的构型：rin={rin:0.0} 盘R={d.DiscRadiusMm}");

        // 带内单元的角向集合
        var degs = Enumerable.Range(0, mesh.CellCount)
            .Where(i => !double.IsNaN(p[i]))
            .Select(i => (Deg: Math.Atan2(mesh.Centroid[i].Z, mesh.Centroid[i].X) * 180 / Math.PI,
                          R: Math.Sqrt(mesh.Centroid[i].X * mesh.Centroid[i].X + mesh.Centroid[i].Z * mesh.Centroid[i].Z)))
            .Where(c => c.R >= rin && c.R <= rout).Select(c => c.Deg).ToArray();
        Assert.True(degs.Length > 0, "带里一个单元都没有 —— 构型不对，本门空转");
        Console.WriteLine($"带内单元 {degs.Length} 个，角向 {degs.Min():0}°…{degs.Max():0}°（含 ±180 跨越）");

        var (deg, score) = RemovalPriority.SlotCenterDeg(mesh, p, rin, rout, 15);
        Console.WriteLine($"槽心 {deg:0}°（分数 {score:0.00}）");
        Assert.False(double.IsNaN(deg));
        bool anyInWindow = degs.Any(c => { double dd = Math.Abs(c - deg); if (dd > 180) dd = 360 - dd; return dd <= 15; });
        Assert.True(anyInWindow, $"槽心算出 {deg:0}°，可 ±15° 窗口里一个带内单元都没有（带内单元在 {degs.Min():0}°…{degs.Max():0}°）");
    }

    /// <summary>孔心：自由段内取优先级最高处，且孔要整个落在自由段里（窗口两端各留半长）。</summary>
    [Fact]
    public void 舌孔孔心落在自由段内()
    {
        var (mesh, J, T, _, _, _) = Solve(Heater1());
        var p = RemovalPriority.Compute(mesh, J, T, 1150);
        double xTan = Heater1().Tangent().X, xClamp = -199.5 + new DesignInputs().BusbarClampLengthMm;
        var (x, score) = RemovalPriority.TabHoleXMm(mesh, p, xClamp + 8, xTan - 8, 8);
        Assert.False(double.IsNaN(x));
        Assert.InRange(x, xClamp + 8 - 1e-9, xTan - 8 + 1e-9);
        Assert.True(score > 0);
    }

    /// <summary>
    /// ★ 审查欠账（低，2026-09-09）：<see cref="DesignSpec.TabHoleXMm"/> 的注释此前写「R12：求解器每轮
    /// 把优先级最高处写进这里」——与代码不符：<see cref="Solver.FieldPlacement"/> 对孔心只算场给的 x
    /// 打印对照，从没写回这个数组（槽心角／长椭圆当地电流方向才真的写回并「开口后冻结」）。
    /// 本门钉住真相：真解一次场、真调一次 <see cref="Solver.FieldPlacement"/>，孔心数组必须仍是 NaN，
    /// <see cref="DesignSpec.TabHoleCenterXMm(int)"/> 落到默认规则（与 <see cref="DesignSpec.TabHoleCenterXMm()"/> 相同）。
    /// </summary>
    /// <summary>R23（2026-09-10）：孔心规则**启用**——开孔前每轮按场写回 <c>TabHoleXMm</c>，落在压接段之外、切点之内。</summary>
    [Fact]
    public void 孔心场给的位置_开孔前采用写回数组()
    {
        var d = DesignSpec.Builtin[0].Clone();
        for (int j = 0; j < d.TabHoleRMm.Length; j++) d.TabHoleRMm[j] = 0;    // 还没开孔
        var baseIn = new DesignInputs();
        var lc = d.BuildCase(baseIn, checkRamp: false);
        var last = LineRunner.Run(lc, null, default);
        Assert.True(last.Ok, "本门要一个收敛的场做基准");
        Assert.All(d.TabHoleXMm, v => Assert.True(double.IsNaN(v)));

        Solver.FieldPlacement(d, baseIn, last, log: null);

        double floor = d.DiscFloorMm(baseIn);
        for (int j = 0; j < d.FlangeCount; j++)
        {
            double x = d.TabHoleXMm[j];
            Assert.False(double.IsNaN(x), $"片{j} 开孔前场给的孔心该写回（R23）");
            double xTan = d.Plate(j, floor).Tangent().X, xClamp = -d.TabLengthMm + d.ClampLengthMm;
            Assert.InRange(x, xClamp + 2.0 - 1e-9, xTan - 2.0 + 1e-9);
        }
    }

    /// <summary>R23：孔一开，孔心就冻结（与槽心同规则）—— 已提交几何不许非单调。</summary>
    [Fact]
    public void 孔心场给的位置_已开孔就冻结不写回()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleRMm = new[] { 5.0, 5.0, 5.0, 5.0 };   // 每片都有孔 ⇒ FieldPlacement 真的会走到 RemovalPriority.TabHoleXMm 那条计算
        var baseIn = new DesignInputs();
        var lc = d.BuildCase(baseIn, checkRamp: false);
        var last = LineRunner.Run(lc, null, default);
        Assert.True(last.Ok, "本门要一个收敛的场做基准，没收敛就没法验证「只打印不写回」");
        Assert.All(d.TabHoleXMm, v => Assert.True(double.IsNaN(v), "求解前孔心本该是 NaN（默认规则）"));

        Solver.FieldPlacement(d, baseIn, last, log: null);

        Assert.All(d.TabHoleXMm, v => Assert.True(double.IsNaN(v),
            "FieldPlacement 把场给的孔心写回了 TabHoleXMm —— 与 DesignSpec.TabHoleXMm 的注释（只打印、不采用）不符"));
        for (int j = 0; j < d.FlangeCount; j++)
            Assert.Equal(d.TabHoleCenterXMm(), d.TabHoleCenterXMm(j), 9);
    }

    /// <summary>③ 场定的位置与形状族存得进档也读得回来（NaN = 默认规则 ↔ null）。</summary>
    [Fact]
    public void 场定的位置存得进档也读得回来()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "★场定孔位往返★ " + Guid.NewGuid().ToString("N")[..6];
        d.SlotCenterDeg = new[] { 12.0, double.NaN, -88.0, 170.0 };
        d.TabHoleXMm = new[] { double.NaN, -66.5, -70.0, double.NaN };
        d.TabHoleSides = new[] { 3.0, 0.0, 4.0, 0.0 };
        d.DiscCutShape = new[] { 1.0, 0.0, 0.0, 1.0 };
        d.DiscCutRotDeg = new[] { 100.0, double.NaN, double.NaN, -45.0 };
        string? written = null;
        try
        {
            written = DesignSpecStore.Save(d);
            var back = DesignSpecStoreTests.Parse(File.ReadAllText(written));
            static void Eq(double[] a, double[] b, string what)
            {
                Assert.Equal(a.Length, b.Length);
                for (int i = 0; i < a.Length; i++)
                    Assert.True(double.IsNaN(a[i]) ? double.IsNaN(b[i]) : Math.Abs(a[i] - b[i]) < 1e-12,
                        $"{what}[{i}]：存 {a[i]} 读回 {b[i]}");
            }
            Eq(d.SlotCenterDeg, back.SlotCenterDeg, "SlotCenterDeg");
            Eq(d.TabHoleXMm, back.TabHoleXMm, "TabHoleXMm");
            Eq(d.TabHoleSides, back.TabHoleSides, "TabHoleSides");
            Eq(d.DiscCutShape, back.DiscCutShape, "DiscCutShape");
            Eq(d.DiscCutRotDeg, back.DiscCutRotDeg, "DiscCutRotDeg");
        }
        finally { if (written is not null && File.Exists(written)) File.Delete(written); }
    }

    /// <summary>旧档只有一个标量孔心（2026-09-09 之前）：读进来要铺到每一片，行为与从前逐位相同。</summary>
    [Fact]
    public void 旧档的标量孔心铺到每一片()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "★旧档孔心★ " + Guid.NewGuid().ToString("N")[..6];
        string? written = null;
        try
        {
            written = DesignSpecStore.Save(d);
            string json = File.ReadAllText(written).Replace("\"wallMm\":", "\"tabHoleXMm\": -77.5,\n  \"wallMm\":");
            var back = DesignSpecStoreTests.Parse(json);
            Assert.All(back.TabHoleXMm, v => Assert.Equal(-77.5, v));
            Assert.All(Enumerable.Range(0, back.FlangeCount), j => Assert.Equal(-77.5, back.TabHoleCenterXMm(j)));
        }
        finally { if (written is not null && File.Exists(written)) File.Delete(written); }
    }

    /// <summary>NaN = 默认规则：与 2026-09-09 之前逐位相同（槽心 0°、孔心自由段中点）。</summary>
    [Fact]
    public void 没有场定值时行为与从前逐位相同()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SlotSpanDeg[1] = 90; d.TabHoleRMm[1] = 3.0;
        var g = d.Plate(1, d.DiscFloorMm(new DesignInputs()));
        Assert.Single(g.DiscSlots);
        Assert.Equal(0.0, g.DiscSlots[0].CenterDeg);
        Assert.Single(g.TabHoles);
        Assert.Equal(d.TabHoleCenterXMm(), g.TabHoles[0].XMm);
        // 场定之后：逐片各走各的
        d.SlotCenterDeg[1] = 95; d.TabHoleXMm[1] = -66.5;
        g = d.Plate(1, d.DiscFloorMm(new DesignInputs()));
        Assert.Equal(95.0, g.DiscSlots[0].CenterDeg);
        Assert.Equal(-66.5, g.TabHoles[0].XMm);
        Assert.Equal(0.0, d.Plate(0, d.DiscFloorMm(new DesignInputs())).DiscSlots.Length);   // 别的片不受影响
    }

    /// <summary>④a 出图 spec 带槽心角与逐片孔心（不起 Rhino 就能验）。</summary>
    [Fact]
    public void 出图规格带槽心角与逐片孔心()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.SlotSpanDeg[0] = 120; d.SlotCenterDeg[0] = 90;
        d.TabHoleRMm[1] = 5; d.TabHoleXMm[1] = -66.5;
        string spec = Geometry3dm.BuildFinalSpec(d);
        Assert.Contains("\"slotCenterDeg\":90,", spec);
        Assert.Contains("\"slotCenterDeg\":0,", spec);          // 没定的片仍是 0°
        Assert.Contains("\"holeX\":-66.5,\"holeR\":5,", spec);
    }

    /// <summary>
    /// ④b **槽心角真的写进了图，而且读得回来**：把片 0 的槽心转到 90°，用厚度探针读回：
    /// 槽带中径处 90° 方向没有料、0° 方向有料（默认规则的槽在 0°，这里正好反过来）。
    /// ⚠ 需要 Rhino 探针（Pt_Optimize.Geom.exe）。探针不在就跳过 —— 跳过要说出来，不许假装通过。
    /// </summary>
    [Fact]
    public void 槽心角真的写进了图也读得回来()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Pt_Optimize.Geom.exe（需 Rhino 8）"); return; }

        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "槽心角回读门";
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.0;
        d.SlotSpanDeg[0] = 120; d.SlotCenterDeg[0] = 90;
        var (rin, rout) = d.SlotBandMm(4.0);
        double rm = 0.5 * (rin + rout);

        string dir = Path.Combine(Path.GetTempPath(), "pt_slotcenter_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "槽心.3dm");
            Geometry3dm.WriteFinal3dm(d, f);
            var tf = Geometry3dm.LoadThickness(f, "入口", 0, 1.0);
            int solid = 0;
            for (int i = 0; i < tf.Nx; i++) for (int k = 0; k < tf.Nz; k++) if (tf.At(tf.X0 + i * tf.Step, tf.Z0 + k * tf.Step) > 1e-6) solid++;
            Assert.True(solid > 5000, $"读回来的厚度场几乎是空的（{solid} 格）—— 问题不在槽，在读取");
            // 90° 方向（z 轴正向）槽带中径处：一小块都不该有料；0° 方向（x 轴正向）应有料
            int hole90 = 0, solid90 = 0, hole0 = 0, solid0 = 0;
            for (double dd = -3; dd <= 3; dd += 1)
            {
                if (tf.At(dd, rm) > 1e-6) solid90++; else hole90++;
                if (tf.At(rm, dd) > 1e-6) solid0++; else hole0++;
            }
            Assert.True(solid90 == 0, $"槽心 90° 处仍有 {solid90}/7 格料 —— 槽心角没写进图（槽还在 0°？）");
            Assert.True(hole0 == 0, $"0° 处有 {hole0}/7 格空 —— 槽画到了默认的 0° 而不是 90°");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
