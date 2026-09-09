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
        Assert.Contains("d.TabHoleRMaxMm()", solver);
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

        // ⑥ 出图：孔径要写进图，否则算的和图不符。
        //    ★ 2026-09-09 审查修复：取数经 Geometry3dm.BuildSteppedPlateArgs（R15 的孔径下限
        //    在那里用 DesignSpec.HolesOf(j) 过滤），「可回读 3DM」不再直接抄 TabHoleRMm[j] 原始值
        //    ——那样会把 < 1 mm 的孔也画成真孔，见 R15小孔不画进可回读3DM 那条门。
        string geo = File.ReadAllText(Path.Combine(HandoverDoc.Root(), "Pt_Optimize", "Core", "Geometry3dm.cs"));
        Assert.Contains("double tabHoleXMm = double.NaN, double tabHoleRMm = 0", geo);
        Assert.Contains("var holes = d.HolesOf(j);", geo);
        Assert.Contains("tabHoleRMm: pa.TabHoleRMm", page);
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

    /// <summary>
    /// ★★★★★ **R15：孔径 &lt; 1 mm 不许画进「可回读 3DM」**（2026-09-09 审查欠账①）。
    ///
    /// 老代码直接把 <c>TabHoleRMm[j]</c> 原始值传给 Geom（那里只挡 ≤0.05 mm 的边角料），
    /// 而 R15 的下限是 1 mm（<see cref="DesignSpec.TabHoleRMinMm"/>）—— 算的时候 0.5 mm 那种孔
    /// 已经按无孔处理（<see cref="DesignSpec.TabHoleREffective"/>），旧的「可回读 3DM」却会把它
    /// 真的画成一个孔：图上有一个算的时候根本不存在的洞。现在取数经
    /// <see cref="Geometry3dm.BuildSteppedPlateArgs"/>（走 <see cref="DesignSpec.HolesOf"/>，
    /// R15 已经在那里过滤），这条门直接验：写出来的图除了管孔之外**没有**别的空洞。
    /// </summary>
    [Fact]
    public void R15小孔不画进可回读3DM()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) return;                 // 没有探针：跳过（见摘要）

        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++) { d.TabThickMm[j] = 2.0; d.SlotSpanDeg[j] = 0; }
        d.TabHoleRMm[0] = 0.5;                      // < TabHoleRMinMm（1.0）⇒ 算的时候按无孔处理
        double floor = d.DiscFloorMm(new DesignInputs());
        var pa = Geometry3dm.BuildSteppedPlateArgs(d, 0, floor);
        Assert.Equal(0.0, pa.TabHoleRMm);           // BuildSteppedPlateArgs 已经把它过滤成 0（HolesOf 空）

        string tmp = Path.Combine(HandoverDoc.Root(), "deliverable", "tmp",
                "R15小孔_" + Guid.NewGuid().ToString("N")[..6] + ".3dm");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        try
        {
            Geometry3dm.WriteStepped3dm(tmp, d.HoleRadiusMm, pa.RadiiMm, pa.ThickMm,
                -d.TabLengthMm, d.TabHalfWidthMm, pa.TabThickMm,
                slotCount: pa.SlotCount, slotWidthDeg: pa.SlotWidthDeg,
                slotRInMm: pa.SlotRInMm, slotROutMm: pa.SlotROutMm,
                tabHoleXMm: pa.TabHoleXMm, tabHoleRMm: pa.TabHoleRMm);
            Assert.True(File.Exists(tmp), "图没写出来");

            var areas = ExportShowsSolvedKnobsTests.HolesOf(probe, tmp, "法兰", planeY: double.NaN);
            Assert.True(areas.Count <= 1,
                $"孔径 0.5 mm（< R15 下限 1 mm）本该按无孔处理，却读到 {areas.Count} 个内部空洞"
              + $"（面积 {string.Join("、", areas.Select(a => a.ToString("0")))} mm²）—— 应只有管孔一个，"
              + "小孔被当成真孔画出来了");
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>
    /// ★★★★★ **环半径必须逐片（RingRadiiOf(j)），不是套第 0 片的 RingRadiiMm**（2026-09-09 审查欠账②）。
    ///
    /// 老代码：`d.RingRadiiMm.Concat(...)` —— <see cref="DesignSpec.RingRadiiMm"/> 的属性注释自己写着
    /// 「只给出图与显示用……求解与建模一律走 RingRadiiOf（逐片），别用这个属性」，而「可回读 3DM」
    /// 恰恰在出图这一步用错了它：恒等于 <c>RingRadiiOf(0)</c>，四片的台阶半径都画成第 0 片的样子。
    /// 四片各级**厚度**本来就是逐片算的（td·RingMul[j]），只有台阶开在哪个半径画错了——
    /// 这是最隐蔽的一种错：数值对、位置不对，光看厚度表看不出来，必须在具体半径上量。
    ///
    /// 本门给两片截然不同的 RingW1Mm/RingW2Mm（台阶半径差 10 mm 以上），在**同一个探测半径**上
    /// 读两片各自写出来的图：这个半径落在片 0 的「外级之外」、却落在片 1 的「内级以内」——
    /// 两片必须读到不同的厚度，读到相同就说明台阶半径没有逐片、都套了片 0 的样子。
    /// </summary>
    [Fact]
    public void 环半径逐片写进可回读3DM_不是套第0片的()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) return;                 // 没有探针：跳过（见摘要）

        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        {
            d.TabThickMm[j] = 2.0; d.SlotSpanDeg[j] = 0; d.TabHoleRMm[j] = 0;
            d.RingMul[j] = 2.0; d.RingMul2[j] = 1.0;    // 内级/外级厚度差够大，读出来一眼能分
        }
        // 片 0：台阶开在 孔+2 / 孔+6；片 1：台阶开在 孔+12 / 孔+16 —— 相差 10 mm 以上
        d.RingW1Mm[0] = 2.0; d.RingW2Mm[0] = 6.0;
        d.RingW1Mm[1] = 12.0; d.RingW2Mm[1] = 16.0;
        double floor = d.DiscFloorMm(new DesignInputs());
        var pa0 = Geometry3dm.BuildSteppedPlateArgs(d, 0, floor);
        var pa1 = Geometry3dm.BuildSteppedPlateArgs(d, 1, floor);
        Assert.True(pa0.RadiiMm[0] < pa1.RadiiMm[0] - 5, "构造没生效 —— 两片的 r1 该差至少 5 mm");

        // 探测半径：卡在「片0外级之外」与「片1内级之内」之间
        double rProbe = 0.5 * (pa0.RadiiMm[1] + pa1.RadiiMm[0]);
        Assert.True(rProbe > pa0.RadiiMm[1] && rProbe < pa1.RadiiMm[0],
            $"探测半径 {rProbe:0.0} 没有卡在片0外级之外、片1内级之内 —— 构造不对");

        string dir = Path.Combine(HandoverDoc.Root(), "deliverable", "tmp",
            "环半径逐片_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var pas = new[] { pa0, pa1 };
            var files = new string[2];
            for (int j = 0; j < 2; j++)
            {
                files[j] = Path.Combine(dir, $"片{j}.3dm");
                var pa = pas[j];
                Geometry3dm.WriteStepped3dm(files[j], d.HoleRadiusMm, pa.RadiiMm, pa.ThickMm,
                    -d.TabLengthMm, d.TabHalfWidthMm, pa.TabThickMm,
                    slotCount: pa.SlotCount, slotWidthDeg: pa.SlotWidthDeg,
                    slotRInMm: pa.SlotRInMm, slotROutMm: pa.SlotROutMm,
                    tabHoleXMm: pa.TabHoleXMm, tabHoleRMm: pa.TabHoleRMm);
                Assert.True(File.Exists(files[j]), "图没写出来：" + files[j]);
            }

            var f0 = Geometry3dm.LoadThickness(files[0], "法兰", double.NaN, 0.5);
            var f1 = Geometry3dm.LoadThickness(files[1], "法兰", double.NaN, 0.5);
            double t0 = f0.At(rProbe, 0), t1 = f1.At(rProbe, 0);

            Assert.True(Math.Abs(t0 - pa0.ThickMm[2]) < 0.1,
                $"片0 在 r={rProbe:0.0} 处读到厚度 {t0:0.00}，该是外级之外的基板厚 {pa0.ThickMm[2]:0.00}");
            Assert.True(Math.Abs(t1 - pa1.ThickMm[0]) < 0.1,
                $"片1 在同一个 r={rProbe:0.0} 处读到厚度 {t1:0.00}，该是内级厚 {pa1.ThickMm[0]:0.00}"
              + "（若两片台阶半径被套成同一份，这里会读到片0的基板厚 —— 逐片没生效）");
            Assert.True(Math.Abs(t0 - t1) > 0.3,
                $"两片在同一探测半径读到几乎相同的厚度（{t0:0.00} vs {t1:0.00}）—— 台阶半径没有逐片");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// ★★★★★ **五组形状族字段真的传进了出图参数**（2026-09-09 审查欠账③，纯 C#，不必起 Rhino）。
    ///
    /// 老代码里 SlotCenterDeg／DiscCutShape／DiscCutRotDeg／TabHoleSides／TabHoleXMm 一个都没传给
    /// Geom，图上永远是默认形状（圆孔、弯椭圆槽、槽心 0°）。这条门给两片截然不同的取值，断言两片
    /// 各自的 <see cref="Geometry3dm.SteppedPlateArgs"/> 与 DesignSpec 自己的闭式函数
    /// （<see cref="DesignSpec.HolesOf"/>／<see cref="DesignSpec.DiscCutsOf"/>）逐位相同，
    /// 且**两片不同**（不是都套了同一片的样子）。真的写进 .3dm 也读得回来见下一条门。
    /// </summary>
    [Fact]
    public void 五组形状族字段逐片传进出图参数()
    {
        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        { d.TabThickMm[j] = 4.0; d.SlotSpanDeg[j] = 0; d.TabHoleRMm[j] = 0; }

        // 片 0：圆角三角舌孔（显式孔心）+ 弯椭圆槽（显式槽心角）
        d.TabHoleSides[0] = 3; d.TabHoleRMm[0] = 10; d.TabHoleXMm[0] = -80;
        d.SlotSpanDeg[0] = 60; d.DiscCutShape[0] = 0; d.SlotCenterDeg[0] = 30;

        // 片 1：圆角方舌孔（不同的显式孔心）+ 长椭圆·顺当地电流（不同的槽心角与转角）
        d.TabHoleSides[1] = 4; d.TabHoleRMm[1] = 10; d.TabHoleXMm[1] = -110;
        d.SlotSpanDeg[1] = 20; d.DiscCutShape[1] = 2; d.SlotCenterDeg[1] = 150; d.DiscCutRotDeg[1] = 47;

        double floor = d.DiscFloorMm(new DesignInputs());
        var pa0 = Geometry3dm.BuildSteppedPlateArgs(d, 0, floor);
        var pa1 = Geometry3dm.BuildSteppedPlateArgs(d, 1, floor);

        double weld1 = Math.Max(pa1.PlateThickMm, d.WallMm);
        var holes0 = d.HolesOf(0); var holes1 = d.HolesOf(1);
        var cuts1 = d.DiscCutsOf(1, weld1);

        // ① 舌孔形状族与孔心逐位对得上 DesignSpec 自己的闭式（HolesOf），不是另算的
        Assert.Equal(holes0[0].RMm, pa0.TabHoleRMm, 9);
        Assert.Equal(holes0[0].Sides, pa0.TabHoleSides);
        Assert.Equal(holes0[0].XMm, pa0.TabHoleXMm, 9);
        Assert.Equal(holes1[0].RMm, pa1.TabHoleRMm, 9);
        Assert.Equal(holes1[0].Sides, pa1.TabHoleSides);
        Assert.Equal(holes1[0].XMm, pa1.TabHoleXMm, 9);
        Assert.NotEqual(pa0.TabHoleSides, pa1.TabHoleSides);
        Assert.True(Math.Abs(pa0.TabHoleXMm - pa1.TabHoleXMm) > 5, "两片孔心没有逐片 —— 都套了同一个 x");

        // ② 槽心角、圆盘挖料形状族逐位对得上（SlotCenterDegOf／DiscCutShapeOf／DiscCutsOf）
        Assert.Equal(30.0, pa0.SlotCenterDeg, 9);
        Assert.Equal(150.0, pa1.SlotCenterDeg, 9);
        Assert.Equal(0, pa0.DiscCutShape);
        Assert.Equal(2, pa1.DiscCutShape);
        Assert.Equal(cuts1[0].XMm, pa1.DiscCutXMm, 9);
        Assert.Equal(cuts1[0].ZMm, pa1.DiscCutZMm, 9);
        Assert.Equal(cuts1[0].RMm, pa1.DiscCutRMm, 9);
        Assert.Equal(cuts1[0].RotDeg, pa1.DiscCutRotDeg, 9);
        Assert.Equal(47.0, pa1.DiscCutRotDeg, 9);        // 形状 2（顺当地电流）才看 DiscCutRotDeg，这里要原样带出来
    }

    /// <summary>
    /// ★★★★★ **五组形状族字段真的写进了「可回读 3DM」也读得回来**（2026-09-09 审查欠账③，需要 Rhino 探针）。
    ///
    /// 上一条门只验到出图参数这一层、不起 Rhino 就能跑；这一条把两片都真的写成 .3dm，走**读取端
    /// 那条路**核对：① 舌孔形状族（圆角三角 vs 圆角方）——面积对得上等面积闭式；
    /// ② 圆盘挖料形状族（弯椭圆槽 vs 长椭圆）——面积也对得上闭式；
    /// ③ 槽心角——弯椭圆槽那片直接量点：槽心角处没有材料，隔了 90° 的角向仍有材料。
    /// </summary>
    [Fact]
    public void 形状族字段真的写进可回读3DM也读得回来()
    {
        string? probe = Geometry3dm.FindProbe();
        if (probe is null) return;                 // 没有探针：跳过（见摘要）

        var d = DesignSpec.Builtin[0].Clone();
        d.DiscRadiusMm = 60; d.TabLengthMm = 199.5; d.TabHalfWidthMm = 40; d.WallMm = 1.0;
        for (int j = 0; j < d.TabThickMm.Length; j++)
        { d.TabThickMm[j] = 4.0; d.SlotSpanDeg[j] = 0; d.TabHoleRMm[j] = 0; }

        // 片 0：圆角三角舌孔（显式孔心 −80）+ 弯椭圆槽（张角 60°、槽心 30°）
        d.TabHoleSides[0] = 3; d.TabHoleRMm[0] = 10; d.TabHoleXMm[0] = -80;
        d.SlotSpanDeg[0] = 60; d.DiscCutShape[0] = 0; d.SlotCenterDeg[0] = 30;

        // 片 1：圆角方舌孔（显式孔心 −110，与片 0 不同）+ 长椭圆·切向（张角取自己装得下的上界，与
        // ShapeFamilyTests.形状族真的写进了图也读得回来 同一个套路：直的椭圆装不下太大的张角）
        d.TabHoleSides[1] = 4; d.TabHoleRMm[1] = 10; d.TabHoleXMm[1] = -110;
        d.DiscCutShape[1] = 1;
        double floor = d.DiscFloorMm(new DesignInputs());
        double weld1 = Math.Max(Math.Max(d.TabThickMm[1], floor), d.WallMm);
        double spanE = d.DiscEllipseSpanMaxDeg(1, weld1);
        Assert.True(spanE >= 10, $"长椭圆装得下的张角只有 {spanE}° —— 构型不对，本门空转");
        d.SlotSpanDeg[1] = spanE; d.SlotCenterDeg[1] = 150;

        var pa0 = Geometry3dm.BuildSteppedPlateArgs(d, 0, floor);
        var pa1 = Geometry3dm.BuildSteppedPlateArgs(d, 1, floor);
        var holes0 = d.HolesOf(0); var holes1 = d.HolesOf(1);
        double wantTri = holes0[0].AreaMm2, wantSq = holes1[0].AreaMm2;
        double weld0 = Math.Max(pa0.PlateThickMm, d.WallMm);
        var (rin0, rout0) = d.SlotBandMm(weld0);
        double wantSlot = DesignSpec.SlotAreaMm2(rin0, rout0, 60);
        var cuts1 = d.DiscCutsOf(1, weld1);
        double wantEll = cuts1[0].AreaMm2;

        string dir = Path.Combine(HandoverDoc.Root(), "deliverable", "tmp",
            "形状族回读_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string f0 = Path.Combine(dir, "片0.3dm"), f1 = Path.Combine(dir, "片1.3dm");
            void Write(string file, Geometry3dm.SteppedPlateArgs pa) =>
                Geometry3dm.WriteStepped3dm(file, d.HoleRadiusMm, pa.RadiiMm, pa.ThickMm,
                    -d.TabLengthMm, d.TabHalfWidthMm, pa.TabThickMm,
                    slotCount: pa.SlotCount, slotWidthDeg: pa.SlotWidthDeg,
                    slotRInMm: pa.SlotRInMm, slotROutMm: pa.SlotROutMm,
                    tabHoleXMm: pa.TabHoleXMm, tabHoleRMm: pa.TabHoleRMm,
                    slotCenterDeg: pa.SlotCenterDeg, discCutShape: pa.DiscCutShape,
                    discCutXMm: pa.DiscCutXMm, discCutZMm: pa.DiscCutZMm, discCutRMm: pa.DiscCutRMm,
                    discCutAspect: pa.DiscCutAspect, discCutRotDeg: pa.DiscCutRotDeg,
                    tabHoleSides: pa.TabHoleSides, tabHoleCornerFrac: pa.TabHoleCornerFrac,
                    tabHoleRotDeg: pa.TabHoleRotDeg, tabHoleAspect: pa.TabHoleAspect);
            Write(f0, pa0); Write(f1, pa1);
            Assert.True(File.Exists(f0) && File.Exists(f1), "图没写出来");

            var areas0 = ExportShowsSolvedKnobsTests.HolesOf(probe, f0, "法兰", planeY: double.NaN);
            var areas1 = ExportShowsSolvedKnobsTests.HolesOf(probe, f1, "法兰", planeY: double.NaN);

            Assert.True(areas0.Any(a => Math.Abs(a - wantTri) / wantTri < 0.08),
                $"片0 找不到面积 ≈ {wantTri:0} mm² 的圆角三角孔（读到 "
              + string.Join("、", areas0.Select(a => a.ToString("0"))) + "）");
            Assert.True(areas0.Any(a => Math.Abs(a - wantSlot) / wantSlot < 0.08),
                $"片0 找不到面积 ≈ {wantSlot:0} mm² 的弯椭圆槽（读到 "
              + string.Join("、", areas0.Select(a => a.ToString("0"))) + "）");
            Assert.True(areas1.Any(a => Math.Abs(a - wantSq) / wantSq < 0.08),
                $"片1 找不到面积 ≈ {wantSq:0} mm² 的圆角方孔（读到 "
              + string.Join("、", areas1.Select(a => a.ToString("0"))) + "）");
            Assert.True(areas1.Any(a => Math.Abs(a - wantEll) / wantEll < 0.08),
                $"片1 找不到面积 ≈ {wantEll:0} mm² 的长椭圆（读到 "
              + string.Join("、", areas1.Select(a => a.ToString("0"))) + "）—— 圆盘挖料形状族没进图");

            // 槽心角：片0 弯椭圆槽心在 30°，中弧半径 rm0 处 30° 该没有材料，隔了 90° 的 120° 仍有材料
            double rm0 = 0.5 * (rin0 + rout0);
            var f0field = Geometry3dm.LoadThickness(f0, "法兰", double.NaN, 0.5);
            double t30 = f0field.At(rm0 * Math.Cos(30 * Math.PI / 180), rm0 * Math.Sin(30 * Math.PI / 180));
            double t120 = f0field.At(rm0 * Math.Cos(120 * Math.PI / 180), rm0 * Math.Sin(120 * Math.PI / 180));
            Assert.True(t30 < 1e-6, $"片0 槽心角 30° 处仍有材料（{t30:0.00}）—— 槽心角没有生效");
            Assert.True(t120 > 0.5, $"片0 隔了 90° 的 120° 处却没有材料（{t120:0.00}）—— 疑似角度算错");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
