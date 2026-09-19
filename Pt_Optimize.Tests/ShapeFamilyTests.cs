using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **离散形状族入候选**（R13，用户 2026-09-08：圆角三角／圆角方；长椭圆与弯椭圆仅用于法兰＝圆盘区）。
///
/// 形状是逐片的**离散选择**，不是旋钮：同一挖料面积下逐个探针量 Δ裕度/Δ铂重与截面 J，
/// 由求解器现有的比价（<c>Solver.Better</c>）挑，存进 <see cref="DesignSpec.TabHoleSides"/>／<see cref="DesignSpec.DiscCutShape"/>，
/// 走完固定清单直到 Geom 出图。
///
/// 门：① 等面积（同一个旋钮值挖同样多的料）；② 长椭圆与弯椭圆槽等面积、装得下槽带；③ 截面按角度扣（长椭圆）；
/// ④ 出图 spec 带形状；⑤ 真写图读回（探针在时）；⑥ 等面积实测表落 deliverable/形状族_对比.txt；
/// ⑦（慢）求解器在造出来的算例上真的会挑形状。
/// </summary>
// ★ 2026-09-09：起 Rhino 子进程的测试类**串行**（同一 xunit collection）—— 并行起两个 RhinoCore 会互相挂死到 10 分钟超时（合并 R12/R13 时抓到，单跑 12 s 过）
[Collection("Rhino 子进程")]
public class ShapeFamilyTests
{
    /// <summary>① 同一个孔径旋钮值，圆／圆角三角／圆角方挖掉同样多的料。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void 同一个孔径值各形状等面积(int sides)
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHoleRMm[0] = 6.0; d.TabHoleAspect[0] = 1.5; d.TabHoleSides[0] = sides;
        var holes = d.HolesOf(0);
        Assert.Single(holes);
        Assert.Equal(sides, holes[0].Sides);
        double want = Math.PI * 6.0 * 6.0 * 1.5;
        Assert.Equal(want, holes[0].AreaMm2, 6);
        if (sides >= 3)
        {
            Assert.Equal(DesignSpec.TabHoleCornerFracOf(sides), holes[0].CornerFrac);
            Assert.Equal(DesignSpec.TabHoleRotDegOf(sides), holes[0].RotDeg);
            Assert.True(holes[0].RMm > 6.0, "多边形挖同样的料，外接半径该比圆大");
        }
        // 孔心在孔里、远处不在 —— 形状真的进了 Contains
        Assert.True(holes[0].Contains(holes[0].XMm, 0));
        Assert.False(holes[0].Contains(holes[0].XMm + 40, 0));
    }

    /// <summary>② 长椭圆 = 与同张角弯椭圆槽等面积的直椭圆，落在槽带中径 × 槽心角处，且装得下槽带。</summary>
    [Fact]
    public void 长椭圆与弯椭圆槽等面积且落在槽带里()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 2.0;
        // ⚠ 张角要小：长椭圆是直的，越长越往带外鼓（尖端半径 √(rm²+a²)），本构型装得下的最大张角约 40°（下面用上界自己量）
        d.SlotSpanDeg[0] = 20; d.SlotCenterDeg[0] = 30;
        double floor = d.DiscFloorMm(new DesignInputs());
        var g0 = d.Plate(0, floor);
        Assert.Single(g0.DiscSlots); Assert.Empty(g0.DiscCutHoles);

        d.DiscCutShape[0] = 1;
        var g1 = d.Plate(0, floor);
        Assert.Empty(g1.DiscSlots); Assert.Single(g1.DiscCutHoles);
        var e = g1.DiscCutHoles[0];
        double weld = Math.Max(Math.Max(2.0, floor), 1.0);
        var (rin, rout) = d.SlotBandMm(weld);
        Assert.Equal(DesignSpec.SlotAreaMm2(rin, rout, 20), e.AreaMm2, 6);
        double rm = 0.5 * (rin + rout);
        Assert.Equal(rm * Math.Cos(30 * Math.PI / 180), e.XMm, 9);
        Assert.Equal(rm * Math.Sin(30 * Math.PI / 180), e.ZMm, 9);
        Assert.Equal(120.0, e.RotDeg, 9);                       // NaN ⇒ 切向 = 槽心角 + 90°
        Assert.True(d.DiscEllipseFits(0, weld, 20));
        // 张角上界：装得下的最大张角 ≥ 20°，且小于弯椭圆槽的桥宽上界（直的装不过弯的）
        double hiE = d.DiscEllipseSpanMaxDeg(0, weld);
        Assert.True(hiE >= 20 && hiE < d.SlotSpanMaxDeg(weld), $"长椭圆张角上界 {hiE}，弯槽 {d.SlotSpanMaxDeg(weld)}");
        Assert.True(d.DiscEllipseFits(0, weld, hiE), "上界处该装得下");
        Assert.False(d.DiscEllipseFits(0, weld, hiE + 2), "上界之外还说装得下");
        // 尖端真的在带内：长半轴的尖端半径 ≤ rout
        var eHi = d.DiscEllipseOf(0, weld, hiE)!.Value;
        Assert.True(Math.Sqrt(rm * rm + eHi.RMm * eHi.AspectXZ * eHi.RMm * eHi.AspectXZ) <= rout + 0.5);
        // 形状 1（切向）不看场给的方向；形状 2（顺当地电流）才用它
        d.DiscCutRotDeg[0] = 15;
        Assert.Equal(120.0, d.Plate(0, floor).DiscCutHoles[0].RotDeg, 9);
        d.DiscCutShape[0] = 2;
        Assert.Equal(15.0, d.Plate(0, floor).DiscCutHoles[0].RotDeg, 9);
        d.DiscCutRotDeg[0] = double.NaN;                        // 方向退化 ⇒ 退回切向，不给一个假方向
        Assert.Equal(120.0, d.Plate(0, floor).DiscCutHoles[0].RotDeg, 9);
    }

    /// <summary>③ 截面按角度扣：长椭圆在中径那一圈扣掉的弧 ≈ 它的长轴（切向放置），转成径向就只剩短轴。</summary>
    [Fact]
    public void 圆盘截面按角度扣掉长椭圆()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 2.0;
        d.SlotSpanDeg[0] = 60; d.DiscCutShape[0] = 1;
        double floor = d.DiscFloorMm(new DesignInputs());
        double weld = Math.Max(Math.Max(2.0, floor), 1.0);
        var (rin, rout) = d.SlotBandMm(weld);
        double rm = 0.5 * (rin + rout);
        var e = d.Plate(0, floor).DiscCutHoles[0];
        double a = e.RMm * e.AspectXZ, b = e.RMm;
        // 独立的暴力估计：0.01° 逐点数在孔里的角度（与 ArcInsideRad 的「采样 + 二分」无关）
        static double Brute(FlangePlate.TabHole h, double r)
        {
            const int N = 36000; int k = 0;
            for (int i = 0; i < N; i++) { double t = 2 * Math.PI * i / N; if (h.Contains(r * Math.Cos(t), r * Math.Sin(t))) k++; }
            return 2 * Math.PI * k / N;
        }
        // 切向长椭圆：中径圈扣掉的弧 < 长轴 2a（圆弯出去了）、> 短轴 2b，且与暴力数出来的一致（0.5 %）
        double arcT = SectionSizing.ArcInsideRad(e, rm) * rm;
        Assert.True(arcT < 2 * a && arcT > 2 * b, $"切向弧 {arcT:0.0} 不在 (2b={2 * b:0.0}, 2a={2 * a:0.0}) 之间");
        Assert.InRange(arcT, Brute(e, rm) * rm * 0.995, Brute(e, rm) * rm * 1.005);
        // 转成径向（形状 2 + 方向 = 槽心角）：只剩短轴 2b（圆过孔心，那一段几乎是直的）
        d.DiscCutShape[0] = 2; d.DiscCutRotDeg[0] = d.SlotCenterDegOf(0);
        var eR = d.Plate(0, floor).DiscCutHoles[0];
        double arcR = SectionSizing.ArcInsideRad(eR, rm) * rm;
        Assert.InRange(arcR, 2 * b * 0.97, 2 * b * 1.03);
        Assert.InRange(arcR, Brute(eR, rm) * rm * 0.995, Brute(eR, rm) * rm * 1.005);
        // 全部截面里，中径那一圈的面积 = (整圈周长 − 扣掉的弧) × 该处厚度（厚度取板自己的，盘 R60 会被焊接屈曲下界顶到 4.7 mm）
        var gR = d.Plate(0, floor);
        var cuts = SectionSizing.Cuts(gR, 1000);
        var ring = cuts.First(c => c.Where == $"圆盘 r={rm:0.#}");
        double tRing = gR.ThicknessAt(-rm, 0);
        Assert.Equal((2 * Math.PI * rm - arcR) * tRing, ring.AreaMm2, 1);
        Assert.True(ring.AreaMm2 < 2 * Math.PI * rm * tRing * 0.99, "中径那一圈没被扣");
        // 舌片截面不受圆盘上这个孔影响
        Assert.All(cuts.Where(c => c.OnTab), c => Assert.True(c.AreaMm2 > 0));
    }

    /// <summary>舌片弦：转了角／多边形时按采样二分算，圆不转角时与闭式逐位相同。</summary>
    [Fact]
    public void 孔的弦_闭式与采样一致()
    {
        var circ = new FlangePlate.TabHole(-50, 0, 8);
        Assert.Equal(16.0, SectionSizing.ChordMm(circ, -50, 30), 9);
        Assert.Equal(2 * 8 * Math.Sqrt(1 - 0.25), SectionSizing.ChordMm(circ, -46, 30), 9);
        // 椭圆 2:1 转 90°（长轴横过来）：孔心处弦 = 2·R·asp
        var ell = new FlangePlate.TabHole(-50, 0, 8, 0, 1.0, 90, 2.0);
        Assert.Equal(32.0, SectionSizing.ChordMm(ell, -50, 30), 4);
        // 圆角方 45°（边迎流）：孔心处弦 = 边长 = 2·(rc·cos45°) + 2·rr … 只钉它在 [2·内切, 2·外接] 之间且 > 0
        double rr = FlangePlate.TabHole.EqualAreaRadius(8, 4, 0.30);
        var sq = new FlangePlate.TabHole(-50, 0, rr, 4, 0.30, 45);
        double ch = SectionSizing.ChordMm(sq, -50, 30);
        Assert.InRange(ch, 2 * rr * Math.Cos(Math.PI / 4) * 0.99, 2 * rr * 1.01);
        // 舌片有效宽随之变紧
        var g = new FlangePlate
        {
            DiscRadiusMm = 30, HoleRadiusMm = 25.8, TabEndXMm = -140, TabEndHalfWidthMm = 30,
            ThicknessMm = 1.0, TabThicknessMm = double.NaN, TabParallel = true, WeldFilletLegMm = 0,
            TabHoles = new[] { sq },
        };
        Assert.Equal(60 - ch, SectionSizing.TabMinWidthMm(g), 6);
    }

    /// <summary>④ 出图 spec 带形状族（不起 Rhino 就能验）。</summary>
    [Fact]
    public void 出图规格带形状族()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        d.TabHoleRMm[0] = 5; d.TabHoleSides[0] = 3;
        d.SlotSpanDeg[1] = 60; d.DiscCutShape[1] = 2; d.DiscCutRotDeg[1] = 77;   // 2 = 顺当地电流 ⇒ 方向 77° 写进图
        string spec = Geometry3dm.BuildFinalSpec(d);
        Assert.Contains("\"holeSides\":3,\"holeCorner\":0.35,\"holeRot\":0", spec);
        Assert.Contains("\"discShape\":\"ellipse\"", spec);
        Assert.Contains("\"discRot\":77", spec);
        Assert.Contains("\"discShape\":\"slot\"", spec);        // 没选长椭圆的片仍是槽
        // 三角的外接半径按等面积缩放后写进图（不是旋钮值 5）
        double rr = FlangePlate.TabHole.EqualAreaRadius(5, 3, 0.35);
        Assert.Contains("\"holeR\":" + rr.ToString("R"), spec);
    }

    /// <summary>
    /// ⑤ **形状真的写进了 .3dm 也读得回来**：片 0 圆角三角舌孔 + 圆盘长椭圆；探针数内部空洞：
    /// 管孔 + 长椭圆 + 三角孔 = 3 个，面积各对得上等面积闭式。⚠ 需要 Rhino 探针，不在就跳过（说出来）。
    /// </summary>
    [Fact]
    public void 形状族真的写进了图也读得回来()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) { Console.WriteLine("跳过：本机没有 Pt_Optimize.Geom.exe（需 Rhino 8）"); return; }

        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "形状族回读门";
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) d.TabThickMm[j] = 4.0;
        d.TabHoleRMm[0] = 20; d.TabHoleSides[0] = 3;
        d.DiscCutShape[0] = 1;
        // 长椭圆的张角取它自己装得下的上界（直的装不下 120°：尖端会捅出盘缘，那就不是内部空洞了）
        var (rin, rout) = d.SlotBandMm(4.0);
        double spanE = d.DiscEllipseSpanMaxDeg(0, 4.0);
        Assert.True(spanE >= 10, $"长椭圆装得下的张角只有 {spanE}° —— 构型不对，本门空转");
        d.SlotSpanDeg[0] = spanE;
        double wantTri = Math.PI * 20 * 20, wantEll = DesignSpec.SlotAreaMm2(rin, rout, spanE);

        string dir = Path.Combine(Path.GetTempPath(), "pt_shapefam_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "形状族.3dm");
            Geometry3dm.WriteFinal3dm(d, f);
            var holes0 = ExportShowsSolvedKnobsTests.HolesOf(probe, f, "入口", planeY: 0);
            Assert.True(holes0.Count >= 3,
                $"片0 只读到 {holes0.Count} 个内部空洞（面积 {string.Join("、", holes0.Select(h => h.ToString("0")))} mm²）—— 应有 管孔 + 长椭圆 + 圆角三角孔 三个");
            Assert.True(holes0.Any(h => Math.Abs(h - wantTri) / wantTri < 0.08),
                $"找不到面积 ≈ {wantTri:0} mm² 的圆角三角孔（读到 {string.Join("、", holes0.Select(h => h.ToString("0")))}）—— 等面积缩放没进图");
            Assert.True(holes0.Any(h => Math.Abs(h - wantEll) / wantEll < 0.08),
                $"找不到面积 ≈ {wantEll:0} mm² 的长椭圆（读到 {string.Join("、", holes0.Select(h => h.ToString("0")))}）—— 长椭圆没进图");
            var holes1 = ExportShowsSolvedKnobsTests.HolesOf(probe, f, "共用1", planeY: 300);
            Assert.True(holes1.Count <= 1, $"片1 没开孔，却读到 {holes1.Count} 个空洞 —— 逐片参数串了");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ⑥ **等面积形状族实测表** —— deliverable/形状族_对比.txt（像 孔形对比.txt 那样，只测不判）。
    /// Pt_Heater1 构型（盘Ø120、舌 199.5×80、板厚 2、1000 A）：舌孔四种形状（面积对齐到 R8 圆）、
    /// 圆盘两种形状（弯椭圆槽 27–40 mm/120° 与等面积长椭圆，切向／顺当地电流方向），
    /// 各量 Δ抽热、峰值 J、最紧截面 J（闭式）。另附：移除优先级沿舌轴的剖面与场定的孔心，对照 孔位对比.txt 的实测最优 x=−50。
    /// </summary>
    [Fact]
    public void 等面积形状族对比落档()
    {
        var b = FieldPlacementTests.Solve(FieldPlacementTests.Heater1());
        Assert.True(b.QFromTubeW > 0 && b.Mesh.VolumeMm3 > 1000, "基准算不出来");
        var sb = new StringBuilder();
        sb.AppendLine("═══ 形状族对比（R13，2026-09-09）：同一挖料面积下 Δ抽热／峰值 J／最紧截面 J ═══");
        sb.AppendLine("构型：盘Ø120　舌长 199.5　舌端半宽 40（梯形舌）　板厚 2.0 mm　1000 A　网格 2/8 mm");
        sb.AppendLine("截面 J = 1000 A ÷ 必经截面（闭式，SectionSizing.Worst）；峰值 J 是场的逐点峰值（诊断量）。");
        sb.AppendLine();
        sb.AppendLine($"（无孔·基准）\t抽热 {b.QFromTubeW:0.0} W\t峰值 J {b.JPeak:0.000}\t体积 {b.Mesh.VolumeMm3:0} mm³\t截面 J {SectionSizing.Worst(FieldPlacementTests.Heater1(), 1000).JAPerMm2:0.00}");
        sb.AppendLine();
        sb.AppendLine("── 舌孔形状族（面积都 ≈ 201 mm² = R8 圆，孔心 x=−50）──");
        sb.AppendLine("形状\t面积 mm²\t抽热 W\t较基准\t峰值 J\t较基准\t截面 J\t体积 mm³");
        double area = Math.PI * 64;
        var tabShapes = new (string Name, FlangePlate.TabHole H)[]
        {
            ("圆 R8",        new FlangePlate.TabHole(-50, 0, 8)),
            ("椭圆 2:1 顺流", new FlangePlate.TabHole(-50, 0, 8 / Math.Sqrt(2), 0, 1.0, 0, 2.0)),
            ("圆角三角 0°",   new FlangePlate.TabHole(-50, 0, FlangePlate.TabHole.EqualAreaRadius(8, 3, 0.35), 3, 0.35, 0)),
            ("圆角方 45°",    new FlangePlate.TabHole(-50, 0, FlangePlate.TabHole.EqualAreaRadius(8, 4, 0.30), 4, 0.30, 45)),
        };
        int rowsOk = 0;
        foreach (var (name, h) in tabShapes)
        {
            var g = FieldPlacementTests.Heater1(); g.TabHoles = new[] { h };
            try
            {
                var r = FieldPlacementTests.Solve(g);
                double jSec = SectionSizing.Worst(g, 1000).JAPerMm2;
                sb.AppendLine($"{name}\t{h.AreaMm2:0}\t{r.QFromTubeW:0.0}\t{100 * (r.QFromTubeW / b.QFromTubeW - 1):+0.00;-0.00} %"
                            + $"\t{r.JPeak:0.000}\t{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %\t{jSec:0.00}\t{r.Mesh.VolumeMm3:0}");
                rowsOk++;
            }
            catch (Exception ex) { sb.AppendLine($"{name}\t✗ {ex.GetType().Name}：{ex.Message}"); }
        }

        sb.AppendLine();
        sb.AppendLine("── 圆盘形状族（弯椭圆槽 27–40 mm/120° 与等面积长椭圆；位置 = 场定槽心）──");
        var pB = RemovalPriority.Compute(b.Mesh, b.J, b.T, 1150);
        var (thc, _) = RemovalPriority.SlotCenterDeg(b.Mesh, pB, 27, 40, 60);
        double rm = 33.5, hw = 6.5, spanRad = 120 * Math.PI / 180;
        double areaSlot = DesignSpec.SlotAreaMm2(27, 40, 120);
        double aEll = areaSlot / (Math.PI * hw);
        var (dirDeg, gmag) = RemovalPriority.CurrentDirectionDeg(b.Mesh, b.V, rm * Math.Cos(thc * Math.PI / 180), rm * Math.Sin(thc * Math.PI / 180));
        double gMax = RemovalPriority.MaxGradient(b.Mesh, b.V);
        sb.AppendLine($"场定槽心 θ = {thc:0}°（窗口 ±60°）；槽心处当地电流方向 {dirDeg:0}°，|∇V| = {gmag / Math.Max(gMax, 1e-30):0.0000} × 全片最大"
                    + (gmag >= 1e-3 * gMax ? "（方向可用）" : "（电流几乎不走，方向是噪声 ⇒ 长椭圆取切向）"));
        sb.AppendLine("形状\t面积 mm²\t抽热 W\t较基准\t峰值 J\t较基准\t截面 J\t体积 mm³");
        var discShapes = new (string Name, FlangePlate.DiscSlot[] S, FlangePlate.TabHole[] H)[]
        {
            ("弯椭圆槽 120°", new[] { new FlangePlate.DiscSlot(27, 40, thc, 120) }, Array.Empty<FlangePlate.TabHole>()),
            ("长椭圆·切向",   Array.Empty<FlangePlate.DiscSlot>(),
                new[] { new FlangePlate.TabHole(rm * Math.Cos(thc * Math.PI / 180), rm * Math.Sin(thc * Math.PI / 180), hw, 0, 1.0, thc + 90, aEll / hw) }),
            ("长椭圆·顺当地电流", Array.Empty<FlangePlate.DiscSlot>(),
                new[] { new FlangePlate.TabHole(rm * Math.Cos(thc * Math.PI / 180), rm * Math.Sin(thc * Math.PI / 180), hw, 0, 1.0,
                                                double.IsNaN(dirDeg) ? thc + 90 : dirDeg, aEll / hw) }),
        };
        foreach (var (name, s, hs) in discShapes)
        {
            var g = FieldPlacementTests.Heater1(); g.DiscSlots = s; g.DiscCutHoles = hs;
            try
            {
                var r = FieldPlacementTests.Solve(g);
                double jSec = SectionSizing.Worst(g, 1000).JAPerMm2;
                double ar = hs.Length > 0 ? hs[0].AreaMm2 : areaSlot;
                sb.AppendLine($"{name}\t{ar:0}\t{r.QFromTubeW:0.0}\t{100 * (r.QFromTubeW / b.QFromTubeW - 1):+0.00;-0.00} %"
                            + $"\t{r.JPeak:0.000}\t{100 * (r.JPeak / b.JPeak - 1):+0.0;-0.0} %\t{jSec:0.00}\t{r.Mesh.VolumeMm3:0}");
                rowsOk++;
            }
            catch (Exception ex) { sb.AppendLine($"{name}\t✗ {ex.GetType().Name}：{ex.Message}"); }
        }

        // ── 舌孔位置：移除优先级沿舌轴的剖面，与 孔位对比.txt（同 R8 孔逐点实测：−50 最好、−20 最坏）对照
        sb.AppendLine();
        sb.AppendLine("── 舌孔位置：移除优先级（导热÷电流）沿舌轴的剖面（窗口 ±8 mm）；对照 孔位对比.txt 的实测最优 x=−50 ──");
        sb.AppendLine("孔心 x\t优先级（面积加权平均）");
        double xTan = FieldPlacementTests.Heater1().Tangent().X;
        for (double x = -20; x >= -180; x -= 10)
        {
            var (_, sc) = RemovalPriority.TabHoleXMm(b.Mesh, pB, x, x, 8, 1);
            sb.AppendLine($"{x:0}\t{sc:0.000}");
        }
        var (xBest, sBest) = RemovalPriority.TabHoleXMm(b.Mesh, pB, -199.5 + 40 + 8, xTan - 8, 8);
        sb.AppendLine($"场定孔心 x = {xBest:0.0}（分数 {sBest:0.000}）；孔位对比.txt 的实测最优 x = −50（抽热 −2.22 %），−20 最坏（峰值 J +23.9 %、抽热 +0.36 %）。");
        sb.AppendLine("⇒ 「舌孔孔心 = 移除优先级最高处」这条前提**被逐点实测推翻**：优先级朝舌根单调上涨（挖舌根的料既不减抽热、还把电流挤爆），");
        sb.AppendLine("   所以求解器只把场给的 x 印在轨迹里对照、不拿它改几何（Solver.FieldPlacement）；槽心角那条没有这个问题（见下）。");

        // ── 双舌片对称进电：同一套算法给的槽心角（FieldPlacementTests 的门只钉 |θ| ≥ 45°，这里把数记下来）
        var b2 = FieldPlacementTests.Solve(FieldPlacementTests.Heater1(twoTabs: true));
        var p2 = RemovalPriority.Compute(b2.Mesh, b2.J, b2.T, 1150);
        var (th2, s2) = RemovalPriority.SlotCenterDeg(b2.Mesh, p2, 27, 40, 15);
        sb.AppendLine();
        sb.AppendLine("── 槽心角：单舌片 vs 双舌片对称进电（同一构型，两舌各在 0°/180°）──");
        sb.AppendLine($"单舌片：槽心 {RemovalPriority.SlotCenterDeg(b.Mesh, pB, 27, 40, 15).Deg:0}°（背对舌片）　双舌片：槽心 {th2:0}°（分数 {s2:0.00}；两舌之间那一带）");

        Assert.True(rowsOk >= 5, $"只算出 {rowsOk} 行 —— 表退化了");
        string outDir = Path.Combine(HandoverDoc.Root(), "deliverable");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "形状族_对比.txt"), sb.ToString());
    }

    /// <summary>
    /// ⑦ **求解器真的会挑形状**（慢：真跑求解器）。Pt_Heater1 那类构型（盘 R60，③ 超限几十倍）上「法兰增量温降」
    /// 必然违反、圆盘槽必然是候选 ⇒ 第一次打开槽之前，弯椭圆槽与长椭圆各探一针、按同一套比价挑一个，并把选中的写进设计。
    /// 断言只钉不变量：比价那一行出现、场定孔位的痕迹出现、设计里存的形状与那一行说的一致。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 求解器在造出来的算例上真的会挑圆盘形状()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.Name = "形状族比价算例";
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        d.SetpointC = new[] { 1150.0, 1080.0 };            // 2 段 3 片：省三分之一算力（HANDOVER 口径）
        d.Fit();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sr = Solver.Solve(d, new DesignInputs(), new SolverOptions
        {
            FineMm = 0, FineRadiusMm = 0, MaxRounds = 1, MaxPartialRounds = 0,
        });
        sw.Stop();
        // 轨迹落档（deliverable/形状族_求解轨迹.txt）：控制台那份会被代码页搅成乱码，档案才是留痕
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped("形状族_求解轨迹.txt"),
            $"═══ 形状族比价：求解器轨迹（{d.Name}，2 段 3 片，导航网格，MaxRounds=1）═══\n"
          + $"求解 {sw.Elapsed.TotalMinutes:0.0} 分钟，场解 {sr.Solves} 次，可行={sr.Feasible}，停因：{sr.StopWhy}\n\n"
          + string.Join("\n", sr.Trace) + "\n", new System.Text.UTF8Encoding(false));
        Console.WriteLine($"求解 {sw.Elapsed.TotalMinutes:0.0} 分钟，场解 {sr.Solves} 次");

        Assert.Contains(sr.Trace, s => s.TrimStart().StartsWith(BranchMarks.FieldPlacement, StringComparison.Ordinal));
        var cmp = sr.Trace.Where(s => s.Contains("圆盘挖料形状比价", StringComparison.Ordinal)).ToArray();
        Assert.True(cmp.Length > 0, "求解器一次都没有比过圆盘形状 —— 形状族没进候选链");
        // 选中的形状与设计里存的一致（逐片）
        var chosen = cmp.Where(s => s.Contains("⇒ 选**", StringComparison.Ordinal)).ToArray();
        Assert.True(chosen.Length > 0, "比价了但一个都没选中：\n" + string.Join("\n", cmp));
        foreach (var line in chosen)
        {
            int j = int.Parse(line.Split('片')[1].Split(' ')[0]);
            string name = line.Split("⇒ 选**")[1].Split("**")[0];
            Assert.Equal(name, Solver.DiscShapeName(sr.Design.DiscCutShapeOf(j)));
        }
    }

    /// <summary>
    /// ⑧ 审查欠账（低，2026-09-09）：**形状族没赢就不落地**。此前 <c>ProbeShapeFamily</c> 探完形状族就把
    /// 选中的形状直接写进已提交态 <c>d</c>，即使那根旋钮（孔径／槽张角）这一轮**没有真的抬起来**——
    /// 要么在 <c>ChooseKnob</c> 同一排比价里输给了另一个候选，要么胜出后 <c>RaiseUntil</c> 又没能真的
    /// 把它抬起来。修法：<c>ChooseKnob</c> 记下每个探过形状族的旋钮探前的形状，没赢的全部退回；
    /// 胜出但没抬起来的那个也在 <c>Solve</c> 的调用点退回。
    ///
    /// ⚠ 成本控制：不跑整条 <c>Solve</c>（那要几十次场解、十几分钟）——直接反射调 <c>ChooseKnob</c>
    /// 本身（它才是这处修法真正改的函数），候选只给「舌保温」「孔径」两根，约 5 次场解。
    ///
    /// ⚠ 怎么让「退回」这件事**看得见**（第一版落在空集，第二版又落在「探测族内部选中的
    /// 恰好也是默认形状」这一巧合上，两次都验证不了任何东西）：探前**人为**把片0 的孔形状
    /// 设成「圆角方」（模拟这条 bug 的真实成因——上一轮探过没赢、形状却没退回，留下的正是
    /// 这样一个杂散值）。<c>ShapeFamilyFor</c> 只在孔径&gt;0（已开口）时才认这个值当形状族唯一
    /// 成员，孔径仍是 0（没开）时它被无视、族照样是 {圆,圆角三角,圆角方} —— 但 <c>ChooseKnob</c>
    /// 探前记的 <c>Shape0</c> 就是它。若孔径这一轮没赢，探测期间被 <c>ProbeShapeFamily</c> 临时改写
    /// 过的形状必须退回到这个人为设的值，不能停在探测期间选中的那个（哪怕两者恰好一样，退回
    /// 逻辑本身也必须真的跑过）。
    /// </summary>
    [Trait("速度", "慢")]
    [Fact]
    public void 形状族没赢就不落地_这条分支走到了()
    {
        var d = DesignSpec.Builtin[0].Clone();
        var p = new DesignInputs();
        var o = new SolverOptions();
        double tLo = Math.Ceiling(d.DiscFloorMm(p) / o.QuantThickMm - 1e-9) * o.QuantThickMm;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = tLo; d.TabInsulMm[j] = o.InsLoMm;
            d.RingMul[j] = o.RingLo; d.RingMul2[j] = o.RingLo;
        }
        var res = new SolverResult();
        Solver.ApplySectionFloor(d, p, o, res, null, s => res.Trace.Add(s));   // 闭式，不解场：填 res.DesignCurrent

        const int j0 = 0;
        // ★ 舌片厚（R11，闭式 I/(J·舌宽)）此刻正好卡在 J=10 满宽处 —— 开孔立刻超 J，孔径上界恒为 0，
        //   形状族根本探不到（本门第一版就是撞在这里）。给舌片一点富余（不影响③违反：③ 由舌保温/几何主导），
        //   孔径的按-J 上界才有正数，探针才有得探。
        if (j0 < d.TongueThickMm.Length && !double.IsNaN(d.TongueThickMm[j0]))
            d.TongueThickMm[j0] *= 1.6;
        // ★ 人为埋一个「杂散形状值」（见上面类注释）：孔径仍是 0（没开），但形状字段不是默认的圆。
        const int stray = 4;   // 圆角方
        d.TabHoleSides[j0] = stray;

        var lc = d.BuildCase(p, checkRamp: false);
        // R48 B（2026-09-14 Opus 5）：有意改动 —— 求解器的冷侧／热侧换成热偶读数基准，旧判法 FlangeDip 已没有逐片裕度（PlateSlack 传进去会抛）⇒ 键与限值一起换。
        //   ⚠ 本条是慢测试，本路没跑；「起点就违反」的前提（下面 before &lt; 0）是旧判法上验过的，新判法上待慢跑确认。
        double coldMax = lc.ColdUnderTcMaxK, hotMax = lc.HotOverTcMaxK;

        var chooseKnob = typeof(Solver).GetMethod("ChooseKnob", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(chooseKnob);
        var knobs = new[] { Solver.Knob.Insul, Solver.Knob.TabHoleR };
        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var args = new object?[]
        {
            d, p, o, j0, knobs, LineResult.Key.ColdUnderTc, coldMax, hotMax, res,
            (Action<string>)(s => { log.Add(s); }), CancellationToken.None, null,
        };
        object? raw;
        try { raw = chooseKnob!.Invoke(null, args); }
        catch (Exception ex)
        {
            Assert.Fail("直接调 ChooseKnob 抛了异常（不是判据不过，是代码本身出错）：" + (ex.InnerException ?? ex));
            return;
        }
        sw.Stop();
        Assert.NotNull(raw);
        var (winner, why, before, after, shape0) = ((Solver.Knob?, string, double, double, int))raw!;
        int shapeAfter = d.TabHoleSidesOf(j0);
        // 2026-09-15 Opus 5（I 路）：原按原文件名写 deliverable（会覆盖被引证据）→ 只写带开跑时刻的新文件（DeliverableOut，门 R48DeliverableWriteGuardTests）
        File.WriteAllText(DeliverableOut.Stamped("形状族没赢不落地_轨迹.txt"),
            $"═══ 形状族没赢就不落地（W08 起点，片{j0}，「舌保温」vs「孔径」，探前人为形状={stray}，{sw.Elapsed.TotalSeconds:0.0} s）═══\n"
          + $"赢家={winner}　why={why}　before={before:0.###}　after={after:0.###}　"
          + $"ChooseKnob 返回的 Shape0={shape0}　调用后 d 里的形状={shapeAfter}\n\n"
          + string.Join("\n", log) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{sw.Elapsed.TotalSeconds:0.0} s　赢家={winner}　调用后形状={shapeAfter}（探前人为设的是 {stray}）");

        Assert.True(before < 0, $"本门要③一开始就违反，实测裕度 {before:0.000} ≥ 0 —— 构型漂了，换个更薄/更冷的起点");

        if (winner == Solver.Knob.TabHoleR)
        {
            // 孔径赢了：这次没走到「没赢」那条分支——如实记下，不假装；但顺带验一条相关不变式：
            // 赢家的 Shape0 就该是我探前人为设的那个（供 RaiseUntil 万一没抬起来时退回用）。
            Assert.Equal(stray, shape0);
            Console.WriteLine("本次孔径赢了，「没赢就不落地」这条分支这次没被走到（形状族选中的形状留在设计里是对的，留痕供参考）。");
        }
        else
        {
            // 孔径没赢（舌保温赢了，或都没赢）：探测期间被临时改写过的形状必须退回**探前人为设的那个**，
            // 不能停在 ProbeShapeFamily 探测期间选中的那个 —— 这正是本条修法要保证的。
            Assert.Equal(stray, shapeAfter);
        }
    }
}
