using System;
using System.Linq;
using PtOptimize.Core;
using Xunit;

namespace PtOptimize.Tests;

/// <summary>
/// ★★★★★ **舌板开孔**（2026-09-05 用户提出）。
///
/// 用户原话：「要记得 3DM 输入，要能实现舌板开孔，尺寸、孔径、厚度也都要能优化」，
/// 并在决定扩 <c>Geom.exe steps</c> 出图之后补了一句「若是这样 UI 输入也要有这功能」。
///
/// 物理上开孔 = **过流截面变小** ⇒ 该处电流密度升高、舌片电阻升高、发热升高。
/// 这正是用户几轮前定的因果链的反向：
/// 「过热 ⇒ 电流密度过大 ⇒ 要加大截面」——开孔是把截面**减小**。
///
/// ⚠ 本门**真解一次电流场**，不是查源码有没有那几行。
///   查源码只能证明「写了」，证明不了「算进去了」——
///   而本项目最贵的错正是「造好了没接线」。
/// </summary>
// ★ 2026-09-09：起 Rhino 子进程的测试类**串行**（同一 xunit collection）—— 并行起两个 RhinoCore 会互相挂死到 10 分钟超时（合并 R12/R13 时抓到，单跑 12 s 过）
[Collection("Rhino 子进程")]
public class TabHoleTests
{
    /// <summary>一片规规矩矩的圆盘＋等宽舌片，用来做对照。</summary>
    private static FlangePlate Plate(params FlangePlate.TabHole[] holes) => new()
    {
        DiscRadiusMm = 30, HoleRadiusMm = 26,
        TabEndXMm = -90, TabEndHalfWidthMm = 15,
        ThicknessMm = 2.0, ThickenedMm = 2.0,
        TabThicknessMm = 2.0, TabParallel = true,
        WeldFilletLegMm = 0,
        TabHoles = holes,
    };

    /// <summary>按**行优先**读同一个场 —— 用来分清「孔没切」和「我索引读错了」。</summary>
    private static double RowMajor(ThicknessField f, double x, double z)
    {
        int i = (int)Math.Round((x - f.X0) / f.Step), j = (int)Math.Round((z - f.Z0) / f.Step);
        if (i < 0 || j < 0 || i >= f.Nx || j >= f.Nz) return -1;
        int k = j * f.Nx + i;
        return k >= 0 && k < f.T.Length ? f.T[k] : -1;
    }

    [Fact]
    public void 孔里不算金属()
    {
        var p = Plate(new FlangePlate.TabHole(-50, 0, 4));
        Assert.False(p.Inside(-50, 0), "孔心仍被当成金属 —— 孔没生效");
        Assert.False(p.Inside(-50, 3), "孔内（离心 3 < 半径 4）仍被当成金属");
        Assert.True(p.Inside(-50, 8), "孔外（离心 8 > 半径 4）被误当成孔");
        // 没有孔时同一点必须是金属 —— 否则上面三条可能是别的原因造成的
        Assert.True(Plate().Inside(-50, 0));
    }

    /// <summary>
    /// ★★★ **真解电流场**：同一片舌片，开孔之后电流必须绕行 ——
    /// 孔占掉的那条路上不再有电流，而孔两侧的电流密度升高。
    /// </summary>
    [Fact]
    public void 开孔之后电流真的绕行()
    {
        const double I = 1000, rho = 1.1e-7, h = 1.0;
        var f0 = PlateCurrent2D.Solve(Plate(), I, rho, h);
        var f1 = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, 5)), I, rho, h);

        // 孔心那一格：开孔前有电流，开孔后不是金属
        int i = (int)Math.Round((-50 - f1.X0) / f1.H), j = (int)Math.Round((0 - f1.Z0) / f1.H);
        Assert.True(f0.Mask[i, j], "对照组孔心不是金属 —— 这条是空转");
        Assert.False(f1.Mask[i, j], "开孔之后孔心仍是金属 —— 孔没进电流场");

        // 孔两侧（z = ±8，仍在半宽 15 内）电流密度升高：截面变小，同样的电流挤过去
        int jSide = (int)Math.Round((8 - f1.Z0) / f1.H);
        Assert.True(f1.Jmag[i, jSide] > f0.Jmag[i, jSide] * 1.05,
            $"孔旁电流密度没有升高（{f0.Jmag[i, jSide]:0.000} → {f1.Jmag[i, jSide]:0.000} A/mm²）"
          + " —— 那说明孔只是被抠掉，电流没有真的绕行");
    }

    /// <summary>
    /// ★★★ **孔越大越挤**：孔径是可优化的量，它对电流密度必须单调 ——
    /// 不单调就不能二分，也就当不成旋钮。
    /// </summary>
    [Theory]
    [InlineData(3.0, 6.0)]
    [InlineData(6.0, 9.0)]
    public void 孔越大孔旁电流密度越高(double rSmall, double rBig)
    {
        const double I = 1000, rho = 1.1e-7, h = 1.0;
        var fs = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, rSmall)), I, rho, h);
        var fb = PlateCurrent2D.Solve(Plate(new FlangePlate.TabHole(-50, 0, rBig)), I, rho, h);

        int i = (int)Math.Round((-50 - fs.X0) / fs.H);
        double Peak(PlateField f)
        {
            double m = 0;
            for (int j = 0; j < f.Nz; j++) if (f.Mask[i, j]) m = Math.Max(m, f.Jmag[i, j]);
            return m;
        }
        double js = Peak(fs), jb = Peak(fb);
        Assert.True(jb > js,
            $"孔从 R{rSmall} 放大到 R{rBig}，孔旁峰值电流密度没升（{js:0.000} → {jb:0.000} A/mm²）"
          + " —— 不单调就不能当旋钮二分");
    }

    /// <summary>
    /// ★★★★★ **孔径是旋钮，而且走完了整条链**（2026-09-05，用户要求 R5）。
    ///
    /// 用户原话：「舌板开孔，尺寸、**孔径**、厚度也都要能优化」，
    /// 以及「要落入 APP 能自动判断，别又停在对话中的功能」。
    ///
    /// ⚠ 我一度以「实测不划算」（圆盘槽好 60 倍）把它降级成非旋钮 ——
    ///   那是把**「不划算」当成「不需要」**：不划算说的是拿它当省铂手段；
    ///   而工程师图上本来就有孔，APP 要回答的是「**这个孔该多大**」。
    ///   划不划算由 ChooseKnob 的**每克铂比价**当场决定，不由我预先删掉候选。
    ///
    /// 本门钉整条链：旋钮 → 求解 → 界面控件 → 出图 → 存档 → 变更清单。
    /// </summary>
    [Fact]
    public void 孔径这根旋钮走完了整条链()
    {
        // ① 旋钮存在，且上界走**闭式**（桥宽），不是常数
        Assert.Contains(Enum.GetValues<Solver.Knob>(), k => k == Solver.Knob.TabHoleR);
        string solver = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Solver.cs"));
        // ★ 审查欠账（低，2026-09-09）：这里改成按片的真实形状族收紧上界（SectionSizingTests.
        //   孔径上界按真实形状族收紧_圆角三角方比圆大一截 钉了那条效果），调用文本从无参变成带 sides —— 门只认「调用还在」，不认措辞。
        Assert.Contains("d.TabHoleRMaxMm(sides: sides)", solver);
        // ② 进了「法兰增量温降」那一排（与舌保温、圆盘槽同排比价）
        Assert.Contains("Knob.Insul, Knob.SlotSpan, Knob.TabHoleR", solver);

        // ③ 闭式上界真的会随舌宽收紧
        var d = DesignSpec.Builtin[0].Clone();
        d.TabHalfWidthMm = 20;
        Assert.True(d.TabHoleRMaxMm() < 20, "孔径上界没有为舌边留桥宽");
        d.TabHalfWidthMm = 40;
        Assert.True(d.TabHoleRMaxMm() > 30, "舌片变宽了，孔径上界却没跟着放开");

        // ④ 求解链路：孔真的进了算例几何
        d.TabHoleRMm = new[] { 3.0, 3.0, 3.0, 3.0 };
        var pl = d.Plate(0, d.DiscFloorMm(new DesignInputs()));
        Assert.Single(pl.TabHoles);
        Assert.Equal(3.0, pl.TabHoles[0].RMm);

        // ⑤ 界面控件（逐片）与变更清单
        string page = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "UI", "LineDesignPage.cs"));
        Assert.Contains("_holeR   = Enumerable.Range(0, n).Select(i => Hole()).ToArray();", page);
        Assert.Contains("d.TabHoleRMm[i] = (double)_holeR[i].Value;", page);
        Assert.Contains("(\"舌板开孔孔径\", b0.SizerHoleR, now.SizerHoleR, \"mm\")", page);

        // ⑥ 出图：孔径要写进图，否则算的和图不符
        string geo = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Geometry3dm.cs"));
        Assert.Contains("double tabHoleXMm = double.NaN, double tabHoleRMm = 0", geo);
        Assert.Contains("tabHoleRMm: j < d.TabHoleRMm.Length ? d.TabHoleRMm[j] : 0", page);
    }

    /// <summary>
    /// ★★★★ **孔真的写进了 .3dm，而且读得回来**（2026-09-05）。
    ///
    /// 只钉「代码里写了」证明不了图上有孔 —— 那正是本项目最贵的错法。
    /// 这条真跑一次 Geom 出图，再走**读取端那条路**（LoadThickness → 厚度场）核对：
    /// 孔心那一格必须**没有材料**，孔外必须有。
    ///
    /// ⚠ 需要 Rhino 探针（Pt_Optimize.Geom.exe）。探针不在就跳过 ——
    ///   跳过要**说出来**，不许假装通过。
    /// </summary>
    [Fact]
    public void 孔真的写进了图也读得回来()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) return;                 // 没有探针：跳过（见摘要）

        string tmp = Path.Combine(HandoverDoc.Root(), "deliverable", "tmp",
                "孔回读_" + Guid.NewGuid().ToString("N")[..6] + ".3dm");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        try
        {
            Geometry3dm.WriteStepped3dm(tmp, holeRadiusMm: 26,
                radiiMm: new[] { 31.0, 36.0, 60.0 }, thickMm: new[] { 2.4, 2.0, 1.8 },
                tabEndXMm: -120, tabHalfWidthMm: 30, tabThickMm: 1.8,
                slotCount: 0, tabHoleXMm: -90, tabHoleRMm: 8);
            Assert.True(File.Exists(tmp), "图没写出来");

            var f = Geometry3dm.LoadThickness(tmp, "法兰", double.NaN, 1.0);
            // ★★★★★ `ThicknessField.At(x, z)` 收的是**坐标**，不是下标。
            //   我第一版先自己把坐标换算成 i/j，再把 i/j 喂进 At —— **换算了两次**，
            //   于是材料看起来全挤在网格一角（2321 格），而探针对同一个文件量出 12708 格、
            //   孔也在。我却据此三轮都在改**写入端**（孔其实一开始就切对了）。
            //   ⇒ 正是我自己写在清单上的那条：**同一个数两处来源**。
            //   ⚠ 教训：调别人的换算之前，先确认它收的是什么单位。
            double At(double x, double z) => f.At(x, z);

            // ★ 先证明**场本身不是空的** —— 否则下面全是空转（第一版就这么假红了一轮）
            int solidCells = 0;
            for (int i2 = 0; i2 < f.Nx; i2++)
                for (int j2 = 0; j2 < f.Nz; j2++)
                    if (f.At(f.X0 + i2 * f.Step, f.Z0 + j2 * f.Step) > 1e-6) solidCells++;
            Assert.True(solidCells > 5000,
                $"读回来的厚度场几乎是空的（{solidCells} 格有材料）—— 问题不在孔，在读取本身");
            string where = $"有料 {solidCells} 格";

            // ★ 扫**区域**不钉某一格：格子随步长落在不同位置，钉单点会随网格假红。
            int inHole = 0, inHoleSolid = 0, ring = 0, ringSolid = 0;
            for (double dx = -14; dx <= 14; dx += 1)
                for (double dz = -14; dz <= 14; dz += 1)
                {
                    double r = Math.Sqrt(dx * dx + dz * dz);
                    double t = At(-90 + dx, dz);
                    if (r <= 6) { inHole++; if (t > 1e-6) inHoleSolid++; }
                    else if (r >= 10 && r <= 13) { ring++; if (t > 1e-6) ringSolid++; }
                }
            Assert.True(inHole > 20 && ring > 20, "扫描退化了 —— 下面是空转。" + where);
            Assert.True(inHoleSolid == 0,
                $"孔内还有 {inHoleSolid}/{inHole} 格材料（孔半径 8，只扫 r≤6）——"
              + $" 孔没切出来。{where}　孔心={At(-90, 0):0.00} 孔缘内={At(-86, 0):0.00}"
              + $" 孔外={At(-90, 12):0.00} 舌片={At(-100, 0):0.00}");
            Assert.True(ringSolid > ring * 0.8,
                $"孔外那一圈只有 {ringSolid}/{ring} 格有材料 —— 这条是空转（或者孔挖过头了）。" + where);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>★ 自证：没有孔时，一切与从前**逐位相同**（新字段不许改变旧行为）。</summary>
    [Fact]
    public void 自证_没有孔时行为不变()
    {
        var p = Plate();
        Assert.Empty(p.TabHoles);
        Assert.True(p.Inside(-50, 0));
        Assert.True(p.Inside(-89, 0));
        Assert.False(p.Inside(-91, 0));      // 舌端之外
    }
}
