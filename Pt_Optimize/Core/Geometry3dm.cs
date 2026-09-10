using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PtOptimize.Core;

/// <summary>
/// 用 <c>Pt_Heater.3dm</c> 校核代码里手抄的几何常数。
///
/// 动机：<see cref="FlangePlate"/> 的 DiscRadiusMm / HoleRadiusMm / TabEndXMm /
/// TabEndHalfWidthMm / ThicknessMm 与 <see cref="DesignInputs"/> 的 TubeIdMm /
/// TubeLengthMm / WallMinMm 全部是人工从 .3dm 抄进来的，此前没有任何自动校核，
/// 而最终铂重（进而省铂结论）正是由这些数算出来的。抄错一个数，结论就是错的。
///
/// 分工：**量**在子进程 <c>Pt_Optimize.Geom</c>（那边才碰 RhinoCommon），
/// **判**在这里 —— 常数在本项目里，搬过去就成了拿副本校副本。
///
/// 质量换算与 <see cref="CoupledSolver"/> 一致：mm³ × PtDensity × 1e-6 = g。
/// </summary>
public static class Geometry3dm
{
    private const string ProbeName = "Pt_Optimize.Geom";

    // ── 子进程回传的结构（字段名与 Pt_Optimize.Geom/Program.cs 的 JSON 键一一对应）

    public sealed class Box
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
    }

    public sealed class LayerMeasurement
    {
        public string Name { get; set; } = "";
        public int Objects { get; set; }
        public int Solids { get; set; }
        public double VolumeMm3 { get; set; }
        public double AreaMm2 { get; set; }
        public double[] CylinderRadiiMm { get; set; } = Array.Empty<double>();
        public double[] PlanarThicknessMm { get; set; } = Array.Empty<double>();
        public Box? Box { get; set; }

        /// <summary>单件质量 g（法兰图层含两片，按实体数均分）</summary>
        public double MassPerSolidG => Solids > 0
            ? VolumeMm3 / Solids * Materials.PtDensity * 1e-6 : 0;
    }

    public sealed class Measurement
    {
        public string File { get; set; } = "";
        public string Units { get; set; } = "";
        public double ToMm { get; set; } = 1;
        public double Tolerance { get; set; }
        public string Rhino { get; set; } = "";
        public LayerMeasurement[] Layers { get; set; } = Array.Empty<LayerMeasurement>();
        public string[] Notes { get; set; } = Array.Empty<string>();

        public LayerMeasurement? Layer(params string[] keywords)
            => Layers.FirstOrDefault(l => keywords.Any(k =>
                   l.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    // ── 子进程调用

    /// <summary>找几何量测子进程的可执行文件。先 Release 后 Debug，逐级上溯。</summary>
    public static string? FindProbe()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                foreach (var cfg in new[] { "Release", "Debug" })
                {
                    string c = Path.Combine(d.FullName, ProbeName, "bin", cfg,
                                            "net7.0-windows", ProbeName + ".exe");
                    if (File.Exists(c)) return c;
                }
        return null;
    }

    /// <summary>
    /// 把用户画的法兰**按厚度方向缩放**后另存（调 Geom 子进程的 scale 模式）。
    ///
    /// 这是「自动定厚」在 .3dm 模式下的落地口：求出倍数后**直接出改好厚度的图**，
    /// 工程师不必回 Rhino 逐级手算。轮廓、孔、槽、各级半径全部不动。
    /// </summary>
    /// <param name="scale">一个值 = 整片统一缩放；多个值 = 逐级独立
    /// （需各级在 .3dm 里是独立实体，否则子进程会明确报出来并退回统一缩放）</param>
    /// <summary>
    /// 把**设计记录构型**（<see cref="DesignSpec"/>）整机写成 .3dm：
    /// 三段铂管 + 四片法兰（板身 / 环外级 / 环内级）+ 压接段参考几何。
    ///
    /// ★ 几何定义只在 <see cref="DesignSpec"/>。这里把它序列化成规格 JSON 交给子进程渲染，
    ///   子进程**不持有任何设计记录值** —— 否则同一个数就在两个项目里各存一份，
    ///   而「抄两处然后悄悄漂开」是本项目最常见的失效（HANDOVER §1.8）。
    /// </summary>
    /// <returns>子进程 stdout（JSON 回显，用于与规格逐项比对）</returns>

    /// <summary>
    /// ★★★★★ **子进程等待必须有上限**（2026-09-07 督导 S9）。
    ///
    /// 全仓 9 处 <c>WaitForExit()</c> 一处上限都没有，其中 **7 处在生产路径上** ——
    /// 工程师点「分析几何变数」「导出本页 3DM」走的就是它。
    /// Rhino 子进程一挂，APP 就**无限期等下去、一句话不说**。
    /// ⚠ 督导写得准：「我没有实测触发过它，我只证明了没有任何东西挡着它发生」——
    ///   这两句不一样，我照原样记下来，不夸大成「已复现」。
    ///   （同一形状我自己刚栽过：一条 `cat &gt; /tmp/chk.csx` 在读 stdin，
    ///     阻塞 13.52 小时、产出为零。那是我的命令，不是产品，但病是同一种。）
    ///
    /// 本方法同时修掉**第二个雷**：原来是先 <c>ReadToEnd(stdout)</c> 再
    /// <c>ReadToEnd(stderr)</c> —— 子进程把 stderr 缓冲区写满时，父进程正卡在读 stdout，
    /// 双方互等，**这是经典死锁**，而且它同样表现为「APP 卡住不说话」。
    /// ⇒ 两条管道都改成异步收。
    ///
    /// ⚠ 超时后**必须杀掉子进程并明说是超时**。只加个数字然后静默返回，
    ///   等于把无限等待换成静默错误 —— 那更糟。
    ///   （督导引了「几何子进程必须有能力报告失败」那一节，我查了四份文档都没有该节号，
    ///     所以只留原则、不留指不到的出处 —— 凭空的出处比没有出处更坏。）
    /// </summary>
    private const int ProbeTimeoutMs = 10 * 60 * 1000;   // 10 分钟：实测最慢的一次出图 ~1 分钟，留一个量级

    private static void WaitOrKill(Process proc, int timeoutMs, string what)
    {
        if (proc.WaitForExit(timeoutMs)) { proc.WaitForExit(); return; }   // 无参那次让异步读收尾
        string pid = "?";
        try { pid = proc.Id.ToString(); } catch { }
        try { proc.Kill(entireProcessTree: true); } catch { }
        try { proc.WaitForExit(5000); } catch { }
        throw new TimeoutException(
            $"{what}：几何子进程（PID {pid}）等了 {timeoutMs / 1000.0:0} 秒还没返回，**已强制结束**。"
          + " 常见原因：本机 Rhino 8 授权过期／弹了对话框／模型过大。"
          + " ⚠ 这条是**超时**，不是「算不出来」—— 两者处置不同，别当成几何有问题。");
    }

    // ★ 2026-09-09（审查欠账「中」）：baseIn 为 null 时用 new DesignInputs()（工艺下界的开箱默认，
    //   WeldMinThicknessMm 0.6 / WeldSafetyFactor 2.0）—— 与 ShapeReview.cs 的 `baseIn ?? new DesignInputs()`
    //   同一口径；调用方拿得到真实 DesignInputs 时应该传（见 LineDesignPage._base）。
    public static string WriteFinal3dm(DesignSpec fd, string outPath,
                                       double tubeIdMm = 50.0, double segLenMm = 300.0,
                                       int segCount = 3, DesignInputs? baseIn = null)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe。先构建 {ProbeName}（需本机装 Rhino 8）。");
        var bi = baseIn ?? new DesignInputs();

        string spec = Path.Combine(Path.GetDirectoryName(outPath) ?? ".",
                                   Path.GetFileNameWithoutExtension(outPath) + ".spec.json");
        File.WriteAllText(spec, BuildFinalSpec(fd, tubeIdMm, segLenMm, segCount, bi), new UTF8Encoding(false));

        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("final");
        psi.ArgumentList.Add(spec);
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        var aOut = proc.StandardOutput.ReadToEndAsync();
        var aErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "写整机 3DM");
        string so = aOut.GetAwaiter().GetResult(), se = aErr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} final 退出码 {proc.ExitCode}：{se}{so}");

        // ★★★★★ **出图自检：把刚写出来的文件读回来，管腔里不许有料**（2026-09-06）。
        //
        //   用户 2026-09-06 开 Rhino 看出来的：法兰**实心穿过铂金管**
        //   （探针实测板身最内半径 0.000 mm，5810 个点落在管外径 R26 以内），
        //   而判据表照样全绿 —— 因为场那一侧走的是 FlangePlate.Inside(x,z)，
        //   它明写着 `r ≥ 孔半径`，**从来没见过那块料**。
        //
        //   根因不是某一个布尔失败，而是**算和画是两套几何、中间一道检查都没有**：
        //     DesignSpec ─┬─ Inside(x,z) ──→ 网格 ──→ 场 ──→ 判据表
        //                 └─ Geom/RunFinal 曲线布尔 ──→ .3dm
        //   ⇒ 补的就是这一道。它不依赖我猜对是哪个布尔坏了 —— **任何机制**导致的
        //     「图上有料而算上无料」都会被这里逮住。
        //
        //   成本：每次出图多跑几次探针（秒级），相对一次 70 分钟的求解可以忽略。
        //   ⚠ 只查最硬的那一条（管腔）。逐点全比对在 Export3dmMatchesSolvedTests 里，
        //     那里有解析几何可比；这里只有 spec，查得起的就是这一条。
        // ★ 管腔自检**已挪进 Geom 的 final 模式**（退出码 9）—— 几何在那边就在内存里，
        //   零额外进程。此处只要不吞它的退出码即可（上面那个 ExitCode != 0 的 throw）。
        //   （原来在这里对每片每部位各起一次探针 = 每次出图多 12 个 Rhino 进程，
        //     而这是工程师每点一次「导出本页 3DM」都要付的成本。）
        // ★ stderr 里的警告（[final] …）成功时也**附在回显后面**（2026-09-09 审查抓到：以前直接丢掉）
        string echo = se.Trim().Length == 0 ? so.Trim() : so.Trim() + Environment.NewLine + "⚠ 出图子进程的提醒：" + Environment.NewLine + se.Trim();
        // ★ 2026-09-09（审查欠账「中」）：BuildFinalSpec 已把低于工艺下界的板厚夹到下界出图
        //   （与 DesignSpec.Plate() 同一条 DiscFloorMm 规则），这里把「夹了哪片、从几到几」
        //   摘出来放进回显 —— 不许接进链路却不说话（同类：0.-3 节「④ 接进链路但不说话」）。
        //   没夹任何片时 ClampNote 返回空串，不额外印字。
        string clampNote = ClampNote(fd, bi);
        return clampNote.Length == 0 ? echo : clampNote + Environment.NewLine + echo;
    }

    /// <summary>
    /// ★ 2026-09-09（审查欠账「中」）：出图前板厚被夹到工艺下界（<see cref="DesignSpec.DiscFloorMm"/>）时，
    /// 把「夹了哪片、从几到几」说给工程师看。不写进 spec.json ——
    /// 那份文件是 Rhino 子进程按纯 JSON 解析的，混一段中文说明会解析失败；
    /// 这段文字只走 <see cref="WriteFinal3dm"/> 的返回值（导出回显）。
    /// </summary>
    private static string ClampNote(DesignSpec fd, DesignInputs baseIn)
    {
        double floor = fd.DiscFloorMm(baseIn);
        var names = new[] { "入口", "共用1", "共用2", "出口" };
        var lines = new List<string>();
        for (int j = 0; j < fd.TabThickMm.Length; j++)
        {
            double raw = fd.TabThickMm[j];
            if (raw < floor - 1e-9)
            {
                string nm = j < names.Length ? names[j] : $"片{j}";
                lines.Add($"{nm} {raw:0.00}→{floor:0.00} mm");
            }
        }
        return lines.Count == 0 ? "" :
            "★ 出图前已把板厚夹到工艺下界（与判据同一条规则 DesignSpec.DiscFloorMm，不是另算的数）：" +
            string.Join("；", lines) + "。";
    }

    /// <summary>
    /// ★ 整机出图的 **spec JSON**（Geom 的 final 模式读它）。抽成独立方法（2026-09-09）是为了让门能**不起 Rhino** 就验
    /// 「求解器定的槽心角／孔心／形状族有没有写进图」（R12/R13）。几何数字一律取自 <see cref="DesignSpec.HolesOf"/>／
    /// <see cref="DesignSpec.SlotsOf"/>／<see cref="DesignSpec.DiscCutsOf"/> 造出来的**同一个**对象 —— 算的与画的是同一份数，
    /// 不在这里再算一遍。
    ///
    /// ★ 2026-09-09（审查欠账「中」）：本方法原来直接用 <c>fd.TabThickMm[j]</c> 出图，而判据算的是
    /// <see cref="DesignSpec.Plate"/> 里的 <c>max(TabThickMm[j], DiscFloorMm)</c> —— 页面板厚被工程师
    /// 改到工艺下界以下时，图纸画的厚度比判据实际用的薄，「图 ≠ 算」。这里改成出图前先按
    /// **同一条** <see cref="DesignSpec.DiscFloorMm"/> 规则夹一遍，不重写第二份下界公式（铁律②）。
    /// baseIn 为 null 时用 <c>new DesignInputs()</c>（工艺下界的开箱默认），与 ShapeReview.cs 同一口径。
    /// </summary>
    public static string BuildFinalSpec(DesignSpec fd, double tubeIdMm = 50.0, double segLenMm = 300.0, int segCount = 3, DesignInputs? baseIn = null)
    {
        string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var names = new[] { "入口", "共用1", "共用2", "出口" };
        double discFloor = fd.DiscFloorMm(baseIn ?? new DesignInputs());
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"name\":\"{fd.Name}\",");
        sb.Append($"\"wallMm\":{R(fd.WallMm)},\"tubeIdMm\":{R(tubeIdMm)},");
        sb.Append($"\"segLenMm\":{R(segLenMm)},\"segCount\":{segCount},");
        sb.Append($"\"discR\":{R(fd.DiscRadiusMm)},\"holeR\":{R(fd.HoleRadiusMm)},");
        sb.Append($"\"tabX\":{R(-fd.TabLengthMm)},\"tabHW\":{R(fd.TabHalfWidthMm)},\"tabTaper\":{(fd.TabTaper ? "true" : "false")},");   // R31
        sb.Append($"\"filletR\":{R(fd.TabFilletMm)},\"clampLenMm\":{R(fd.ClampLengthMm)},");
        sb.Append($"\"ringR\":[{R(fd.RingRadiiMm[0])},{R(fd.RingRadiiMm[1])}],");
        sb.Append("\"plates\":[");
        // ★ 按实际片数（用户 2026-09-03：段数由 UI 决定）—— 写死 4 会让出图**少画片**
        for (int j = 0; j < fd.TabThickMm.Length; j++)
        {
            // ★ 2026-09-09：与 DesignSpec.Plate() 的 `td = Math.Max(TabThickMm[j], discFloorMm)` 同一条夹持规则。
            double t = Math.Max(fd.TabThickMm[j], discFloor);
            if (j > 0) sb.Append(',');
            // ★ R11（2026-09-08）：舌片自己的厚度（I/(J·舌宽)）；旧档 NaN ⇒ 与板厚同（Geom 侧缺省也是 t）
            // ★ 2026-09-09：NaN 缺省改用**夹过的** t —— 与 LineRunner.cs「double.IsNaN(pl.TabThicknessMm) ? pl.ThicknessMm : ...」
            //   同一口径（pl.ThicknessMm 就是 Plate() 里夹过的 td），不是回退到未夹的原始板厚。
            double tt = j < fd.TongueThickMm.Length && !double.IsNaN(fd.TongueThickMm[j]) ? fd.TongueThickMm[j] : t;
            sb.Append($"{{\"name\":\"{names[j]}\",\"t\":{R(t)},\"tabT\":{R(tt)},");
            if (fd.HasTabArm(j))                                          // R29：舌根加厚带（叉臂）
                sb.Append($"\"tabArmX0\":{R(fd.TabArmX0Mm[j])},\"tabArmX1\":{R(fd.TabArmX1Mm[j])},\"tabArmT\":{R(Math.Max(fd.TabArmThickMm[j], discFloor))},");
            sb.Append($"\"ring\":[{R(t * fd.RingMul[j])},{R(t * fd.RingMulOuter(j))}],");
            // ★★★★★ **槽与孔必须跟着走**（2026-09-05）。
            //   在此之前这份 spec 只有 t 和 ring ⇒ APP 自己的出图器**画不出**
            //   圆盘背侧减重槽与舌板开孔，而它们正是求解器现役的旋钮。
            //   后果：求解器解出 120° 槽 + 36 mm 孔，导出的图上一个都没有 ——
            //   「算的是一个东西、导出的是另一个东西」，两边各自都自洽，最难发现。
            //   槽带按**本片**焊脚算（焊脚 = max(板厚, 管壁)），与 DesignSpec.SlotBandMm 同一份规则。
            double weld = Math.Max(t, fd.WallMm);
            var (rin, rout) = fd.SlotBandMm(weld);
            double span = j < fd.SlotSpanDeg.Length ? fd.SlotSpanDeg[j] : 0;
            // ★ R12/R13（2026-09-09）：槽心角、圆盘形状族、舌孔形状族与逐片孔心都跟着走。
            //   数字取自造几何的**同一个**对象（HolesOf／DiscCutsOf）—— 多边形按等面积缩放后的外接半径、
            //   转角、圆角比例，画的就是算的那一个；R15 的「孔径 < 1 mm 按无孔」也由此自动带上（HolesOf 已扣）。
            sb.Append($"\"slotDeg\":{R(span)},\"slotRIn\":{R(rin)},\"slotROut\":{R(rout)},");
            sb.Append($"\"slotCenterDeg\":{R(fd.SlotCenterDegOf(j))},");
            var discCuts = fd.DiscCutsOf(j, weld);
            if (fd.DiscCutShapeOf(j) >= 1 && discCuts.Length > 0)      // 1 = 切向、2 = 顺当地电流，画法相同（方向已在对象里）
            {
                var e = discCuts[0];
                sb.Append($"\"discShape\":\"ellipse\",\"discX\":{R(e.XMm)},\"discZ\":{R(e.ZMm)},\"discR\":{R(e.RMm)},");
                sb.Append($"\"discAsp\":{R(e.AspectXZ)},\"discRot\":{R(e.RotDeg)},");
            }
            else sb.Append("\"discShape\":\"slot\",");
            var holes = fd.HolesOf(j);
            if (holes.Length > 0)
            {
                var h = holes[0];
                sb.Append($"\"holeX\":{R(h.XMm)},\"holeR\":{R(h.RMm)},\"holeAsp\":{R(h.AspectXZ)},");
                sb.Append($"\"holeSides\":{h.Sides},\"holeCorner\":{R(h.CornerFrac)},\"holeRot\":{R(h.RotDeg)}}}");
            }
            else
            {
                double hA = j < fd.TabHoleAspect.Length && fd.TabHoleAspect[j] > 0 ? fd.TabHoleAspect[j] : 1.0;
                sb.Append($"\"holeX\":{R(fd.TabHoleCenterXMm(j))},\"holeR\":0,\"holeAsp\":{R(hA)},\"holeSides\":0,\"holeCorner\":1,\"holeRot\":0}}");
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }


    /// <summary>
    /// 写一张**单图层、多级台阶**的法兰 .3dm（调 Geom 子进程的 steps 模式）。
    ///
    /// ★ 它补的是解析路径与 .3dm 路径之间**断掉的那一环**：
    ///   现有的「导出本页/设计记录 3DM」走 <see cref="WriteFinal3dm"/>，写的是**多图层**
    ///   （板身 / 环外级 / 环内级 / 压接段 / 角焊缝），而 .3dm 的读取端
    ///   （<see cref="LoadThickness"/> + <see cref="PlateShapeAnalyzer"/>）要的是**单图层**
    ///   ⇒ **APP 导出的图，APP 自己读不回来**。
    ///   有了本方法，「解析里搜出方案 → 出图 → 去 Rhino 改轮廓/挪槽 → 读回来核算」
    ///   才是一条闭环。
    ///
    /// ⚠ Geom 的 steps 模式**早就写好了**（注释写着「供逐级定厚验证」），
    ///   但一直没有 C# 包装、也没有按钮 —— 又一个「造好了没接线」。
    ///
    /// <param name="radiiMm">各级**外**半径，自小到大；最后一个即圆盘外半径</param>
    /// <param name="thickMm">与半径一一对应的厚度</param>
    /// <param name="slotCount">开槽数，**0 = 不开槽**（解析几何本来就没有槽）</param>
    /// </summary>
    public static string WriteStepped3dm(string outPath, double holeRadiusMm,
                                         IReadOnlyList<double> radiiMm, IReadOnlyList<double> thickMm,
                                         double tabEndXMm, double tabHalfWidthMm, double tabThickMm,
                                         int slotCount = 0, double slotWidthDeg = 20,
                                         double slotRInMm = double.NaN, double slotROutMm = double.NaN,
                                         double tabHoleXMm = double.NaN, double tabHoleRMm = 0,
                                         // ★★★★★ 2026-09-09 审查欠账③：五个形状族字段 —— 原来没有参数可传，
                                         //   「可回读 3DM」画的槽/孔永远是默认形状（圆、弯椭圆槽、槽心 0°），
                                         //   与算的（DesignSpec 的 SlotCenterDeg／DiscCutShape／DiscCutRotDeg／
                                         //   TabHoleSides／TabHoleXMm）不是同一份数。默认值与老档逐位相同，
                                         //   不传就是从前的行为（HANDOVER §「2026-09-09 多视角审查」）。
                                         double slotCenterDeg = 0,
                                         int discCutShape = 0, double discCutXMm = double.NaN, double discCutZMm = 0,
                                         double discCutRMm = 0, double discCutAspect = 1, double discCutRotDeg = 0,
                                         int tabHoleSides = 0, double tabHoleCornerFrac = 1.0,
                                         double tabHoleRotDeg = 0, double tabHoleAspect = 1.0,
                                         // ★ R39（2026-09-11 边角料）：舌根加厚带（R29 叉臂）—— 第 17 个参数「x0,x1,厚」，NaN = 没有（逐位如前）
                                         double tabArmX0Mm = double.NaN, double tabArmX1Mm = double.NaN, double tabArmThickMm = double.NaN)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe。先构建 {ProbeName}（需本机装 Rhino 8）。");
        if (radiiMm.Count == 0 || radiiMm.Count != thickMm.Count)
            throw new ArgumentException(
                $"半径 {radiiMm.Count} 个、厚度 {thickMm.Count} 个 —— 必须一一对应且非空");

        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("R", ic);          // Geom 那边一律按 InvariantCulture 解析（NumberStyles.Float）
        psi.ArgumentList.Add("steps");
        psi.ArgumentList.Add(outPath);
        psi.ArgumentList.Add(holeRadiusMm.ToString("R"));
        psi.ArgumentList.Add(string.Join(",", radiiMm.Select(v => v.ToString("R"))));
        psi.ArgumentList.Add(string.Join(",", thickMm.Select(v => v.ToString("R"))));
        psi.ArgumentList.Add(tabEndXMm.ToString("R"));
        psi.ArgumentList.Add(tabHalfWidthMm.ToString("R"));
        psi.ArgumentList.Add(tabThickMm.ToString("R"));
        psi.ArgumentList.Add(slotCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(slotWidthDeg.ToString("R"));
        if (!double.IsNaN(slotRInMm) && !double.IsNaN(slotROutMm))
            psi.ArgumentList.Add($"{slotRInMm.ToString("R")},{slotROutMm.ToString("R")}");
        else psi.ArgumentList.Add("0,0");          // 占位：舌型必须落在第 11 个参数上
        // 等宽舌 —— 与 FlangePlate.TabParallel 同口径。梯形是本模式的旧默认，
        // 而设计记录几何早已不用梯形（见 Geom 的 RunSteps 注释）。
        psi.ArgumentList.Add("par");
        // ★★★ 舌板开孔（2026-09-05，用户要求 R5）。第 12 个参数：孔心x,孔半径,孔边数,孔圆角比,孔转角,孔拉长——
        //   ⚠ 孔径变了图上必须跟着变 —— 求解器调了孔而图没改，
        //     工程师拿到的就是一张**与计算不符**的图。那比不开孔更糟。
        //   ★ 2026-09-09 审查欠账③：从「x,r」两个数扩成六个数（形状族），且**始终带上占位**——
        //     不然后面槽心角/圆盘挖料形状族几个参数的下标会跟着错位。
        psi.ArgumentList.Add((tabHoleRMm > 0.05 && !double.IsNaN(tabHoleXMm))
            ? $"{F(tabHoleXMm)},{F(tabHoleRMm)},{tabHoleSides.ToString(ic)},{F(tabHoleCornerFrac)},{F(tabHoleRotDeg)},{F(tabHoleAspect)}"
            : "NaN,0,0,1,0,1");
        // ★ 2026-09-09 审查欠账③：槽心角（第 13 个参数）、圆盘挖料形状族与长椭圆参数（第 14/15 个）
        psi.ArgumentList.Add(F(slotCenterDeg));
        psi.ArgumentList.Add(discCutShape.ToString(ic));
        psi.ArgumentList.Add($"{F(discCutXMm)},{F(discCutZMm)},{F(discCutRMm)},{F(discCutAspect)},{F(discCutRotDeg)}");
        // ★ R39：第 17 个参数 —— 舌根加厚带 x0,x1,厚（始终带上占位，NaN = 没有）
        psi.ArgumentList.Add($"{F(tabArmX0Mm)},{F(tabArmX1Mm)},{F(tabArmThickMm)}");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        var cOut = proc.StandardOutput.ReadToEndAsync();
        var cErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "几何子进程");
        string stdout = cOut.GetAwaiter().GetResult();
        string stderr = cErr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} steps 退出码 {proc.ExitCode}。{stderr.Trim()}");
        return stdout;
    }

    /// <summary>
    /// 「导出可回读 3DM」每一片要喂给 <see cref="WriteStepped3dm"/> 的参数。
    ///
    /// ★★★★★ 2026-09-09 审查欠账（中）：「可回读 3DM」画的不是算的那份 —— 从
    /// <c>LineDesignPage.ExportReadable3dm</c> 里抽出来单独成一个可测的静态方法，是因为
    /// 那是私有 UI 方法（弹目录选择框），测试够不到它；抽出来之后「画的是不是算的那份」
    /// 才能被回归门直接钉住，不必靠比对源码字面文本（本仓库反复吃过「门钉当时的措辞」的亏）。
    ///
    /// 修的三条（整机出图 <see cref="WriteFinal3dm"/>／<see cref="BuildFinalSpec"/> 早就是对的，这里照它的做法）：
    /// <code>
    ///   ① R15（孔径 &lt; 1 mm 不画）  老代码直接传 TabHoleRMm[j] ⇒ 改走 DesignSpec.HolesOf(j)
    ///                                  （已经按 TabHoleREffective 过滤，等面积缩放也在里面）
    ///   ② 环半径逐片                  老代码用 RingRadiiMm（= RingRadiiOf(0)，只是第 0 片的样子）
    ///                                  ⇒ 改走 RingRadiiOf(j)（逐片，RingW1Mm/RingW2Mm 逐片放开之后才不会画错台阶半径）
    ///   ③ 形状族／槽心角              SlotCenterDeg／DiscCutShape／DiscCutRotDeg／TabHoleSides／TabHoleXMm
    ///                                  老代码一个都没传给 Geom ⇒ 改走 DiscCutsOf(j,·)／SlotCenterDegOf(j)／HolesOf(j)，
    ///                                  与 BuildFinalSpec 同一份数，不是另算一遍
    /// </code>
    /// </summary>
    public readonly struct SteppedPlateArgs
    {
        /// <summary>各级**外**半径（逐片，取自 <see cref="DesignSpec.RingRadiiOf"/>），最后一个是圆盘外半径</summary>
        public double[] RadiiMm { get; init; }
        public double[] ThickMm { get; init; }
        /// <summary>圆盘基板厚度（= max(TabThickMm[j], 下界)），只用来判断舌片是否需要另起一级——不直接喂给 Geom</summary>
        public double PlateThickMm { get; init; }
        /// <summary>舌片自己的厚度（R11；NaN 时与基板同厚，这里已经取过 max(·, 下界)）</summary>
        public double TabThickMm { get; init; }
        public int SlotCount { get; init; }
        public double SlotWidthDeg { get; init; }
        public double SlotRInMm { get; init; }
        public double SlotROutMm { get; init; }
        public double SlotCenterDeg { get; init; }
        public double TabHoleXMm { get; init; }
        public double TabHoleRMm { get; init; }
        public int TabHoleSides { get; init; }
        public double TabHoleCornerFrac { get; init; }
        public double TabHoleRotDeg { get; init; }
        public double TabHoleAspect { get; init; }
        public int DiscCutShape { get; init; }
        public double DiscCutXMm { get; init; }
        public double DiscCutZMm { get; init; }
        public double DiscCutRMm { get; init; }
        public double DiscCutAspect { get; init; }
        public double DiscCutRotDeg { get; init; }
        /// <summary>R39：舌根加厚带（R29 叉臂）x0／x1／厚，NaN = 没有；厚已取过 max(·, 下界)</summary>
        public double TabArmX0Mm { get; init; }
        public double TabArmX1Mm { get; init; }
        public double TabArmThickMm { get; init; }
    }

    public static SteppedPlateArgs BuildSteppedPlateArgs(DesignSpec d, int j, double floorMm)
    {
        double td = Math.Max(d.TabThickMm[j], floorMm);
        // ② 环半径逐片 —— 不是 RingRadiiMm（那是 RingRadiiOf(0) 的别名，见该属性上的警告注释）
        var radii = d.RingRadiiOf(j).Concat(new[] { d.DiscRadiusMm }).ToArray();
        var thick = new[] { td * d.RingMul[j], td * d.RingMulOuter(j), td };
        // R11：舌片自己的厚度（NaN = 与基板同）
        double tt = j < d.TongueThickMm.Length && !double.IsNaN(d.TongueThickMm[j])
            ? Math.Max(d.TongueThickMm[j], floorMm) : td;
        double weld = Math.Max(td, d.WallMm);
        var band = d.SlotBandMm(weld);
        // ① R15 的孔径下限、等面积缩放、逐片孔心 —— HolesOf(j) 已经全做了，这里不重算
        var holes = d.HolesOf(j);
        // ③ 圆盘挖料的形状族（长椭圆时给出 X/Z/R/拉长/转角，与 BuildFinalSpec 的 discX/discZ/discR/discAsp/discRot 同一份数）
        var discCuts = d.DiscCutsOf(j, weld);

        return new SteppedPlateArgs
        {
            RadiiMm = radii, ThickMm = thick, PlateThickMm = td, TabThickMm = tt,
            SlotCount = d.SlotSpanDeg[j] > 0.5 ? 1 : 0,
            SlotWidthDeg = d.SlotSpanDeg[j],
            SlotRInMm = band.RIn, SlotROutMm = band.ROut,
            SlotCenterDeg = d.SlotCenterDegOf(j),
            TabHoleXMm = holes.Length > 0 ? holes[0].XMm : d.TabHoleCenterXMm(j),
            TabHoleRMm = holes.Length > 0 ? holes[0].RMm : 0,
            TabHoleSides = holes.Length > 0 ? holes[0].Sides : 0,
            TabHoleCornerFrac = holes.Length > 0 ? holes[0].CornerFrac : 1.0,
            TabHoleRotDeg = holes.Length > 0 ? holes[0].RotDeg : 0,
            TabHoleAspect = holes.Length > 0 ? holes[0].AspectXZ : 1.0,
            DiscCutShape = d.DiscCutShapeOf(j),
            DiscCutXMm = discCuts.Length > 0 ? discCuts[0].XMm : double.NaN,
            DiscCutZMm = discCuts.Length > 0 ? discCuts[0].ZMm : 0,
            DiscCutRMm = discCuts.Length > 0 ? discCuts[0].RMm : 0,
            DiscCutAspect = discCuts.Length > 0 ? discCuts[0].AspectXZ : 1,
            // R39：叉臂（R29）—— 与 BuildFinalSpec 同一份数（DesignSpec.TabArm*），可回读图也画出来
            TabArmX0Mm = d.HasTabArm(j) ? d.TabArmX0Mm[j] : double.NaN,
            TabArmX1Mm = d.HasTabArm(j) ? d.TabArmX1Mm[j] : double.NaN,
            TabArmThickMm = d.HasTabArm(j) ? Math.Max(d.TabArmThickMm[j], floorMm) : double.NaN,
            DiscCutRotDeg = discCuts.Length > 0 ? discCuts[0].RotDeg : 0,
        };
    }

    public static string ScalePlate3dm(string inPath, string outPath, string layer,
                                       IReadOnlyList<double> scale, double planeY = double.NaN)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe。先构建 {ProbeName}（需本机装 Rhino 8）。");
        if (scale.Count == 0) throw new ArgumentException("缩放倍数为空", nameof(scale));

        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("scale");
        psi.ArgumentList.Add(inPath);
        psi.ArgumentList.Add(outPath);
        psi.ArgumentList.Add(layer);
        psi.ArgumentList.Add(string.Join(",", scale.Select(v => v.ToString("R"))));
        if (!double.IsNaN(planeY)) psi.ArgumentList.Add(planeY.ToString("R"));

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        var cOut = proc.StandardOutput.ReadToEndAsync();
        var cErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "几何子进程");
        string stdout = cOut.GetAwaiter().GetResult();
        string stderr = cErr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} scale 退出码 {proc.ExitCode}。{stderr.Trim()}");
        return stdout;
    }

    /// <summary>
    /// 从 .3dm 提取某图层某平面的厚度场（调 Geom 子进程的 thickness 模式）。
    /// t=0 表示无材料，故轮廓、管孔、开槽三者统一表达；t&gt;0 直接给出阶梯厚度。
    /// </summary>
    public static ThicknessField MeasureThickness(string path3dm, string layer,
                                                  double planeY, double stepMm = 1.0)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe，先构建：dotnet build {ProbeName}");

        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("thickness");
        psi.ArgumentList.Add(path3dm);
        psi.ArgumentList.Add(layer);
        psi.ArgumentList.Add(planeY.ToString("R"));
        psi.ArgumentList.Add(stepMm.ToString("R"));

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        var cOut = proc.StandardOutput.ReadToEndAsync();
        var cErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "几何子进程");
        string stdout = cOut.GetAwaiter().GetResult();
        string stderr = cErr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} thickness 退出码 {proc.ExitCode}。{stderr.Trim()}");

        using var doc = JsonDocument.Parse(stdout);
        var r = doc.RootElement;
        var f = new ThicknessField
        {
            X0 = r.GetProperty("x0").GetDouble(),
            Z0 = r.GetProperty("z0").GetDouble(),
            Step = r.GetProperty("step").GetDouble(),
            Nx = r.GetProperty("nx").GetInt32(),
            Nz = r.GetProperty("nz").GetInt32(),
        };
        var arr = r.GetProperty("thickness");
        f.T = new double[arr.GetArrayLength()];
        int k = 0;
        foreach (var v in arr.EnumerateArray()) f.T[k++] = v.GetDouble();
        return f;
    }

    public static Measurement Measure(string path3dm)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException(
                $"找不到 {ProbeName}.exe。先构建量测子进程：dotnet build {ProbeName}");

        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(path3dm);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 " + probe);

        // 先读完再等待：子进程输出可能填满管道缓冲区而卡死
        var cOut = proc.StandardOutput.ReadToEndAsync();
        var cErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "几何子进程");
        string stdout = cOut.GetAwaiter().GetResult();
        string stderr = cErr.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"{ProbeName} 退出码 {proc.ExitCode}。{(stderr.Length > 0 ? stderr.Trim() : "无 stderr 输出")}");

        var m = JsonSerializer.Deserialize<Measurement>(stdout,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"{ProbeName} 的输出无法解析为 JSON");
        return m;
    }

    // ── 厚度场（任意法兰形状 → t(x,z)）

    private sealed class ThicknessDto
    {
        public double PlaneY { get; set; }
        public double X0 { get; set; }
        public double Z0 { get; set; }
        public double Step { get; set; }
        public int Nx { get; set; }
        public int Nz { get; set; }
        public double[] Thickness { get; set; } = Array.Empty<double>();
        public int GroupCount { get; set; }
        public int PartsUsed { get; set; }
    }

    /// <summary>同一 (文件, 图层, 平面, 步长) 只提一次 —— 每次提取要跑一遍 Rhino 子进程（数秒）</summary>
    private static readonly Dictionary<string, ThicknessField> _tfCache = new();

    /// <summary>
    /// 从 .3dm 提取法兰厚度场。t = 0 表示无材料（轮廓外、管孔、开槽），
    /// t &gt; 0 直接给出阶梯厚度 —— 任意形状照单全收，不需提轮廓。
    /// </summary>
    public static ThicknessField LoadThickness(string path3dm, string layer,
                                               double planeY = double.NaN, double step = 1.0)
    {
        string key = $"{Path.GetFullPath(path3dm)}|{layer}|{planeY}|{step}";
        if (_tfCache.TryGetValue(key, out var hit)) return hit;

        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe（几何量测子进程需先构建）");
        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.ArgumentList.Add("thickness");
        psi.ArgumentList.Add(path3dm);
        psi.ArgumentList.Add(layer);
        psi.ArgumentList.Add(double.IsNaN(planeY) ? "NaN" : planeY.ToString("R"));
        psi.ArgumentList.Add(step.ToString("R"));

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        var cOut = proc.StandardOutput.ReadToEndAsync();
        var cErr = proc.StandardError.ReadToEndAsync();
        WaitOrKill(proc, ProbeTimeoutMs, "几何子进程");
        string stdout = cOut.GetAwaiter().GetResult();
        string stderr = cErr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"厚度场提取失败（退出码 {proc.ExitCode}）：{stderr.Trim()}");
        // ★★ 退出码 0 **不等于**成功（2026-08-25 实测）：Pt_Optimize.Geom 的六个模式
        //   此前都写成 finally { Environment.Exit(Environment.ExitCode); }，
        //   而那个属性默认恒 0 ⇒ 它精心返回的 4（图层无实体）／2（异常）**全被抹平**。
        //   当时的表现：拿设计记录自己的图纸跑 thickness，stderr 明明写着「图层无实体：法兰」，
        //   退出码却是 0、stdout 为空，于是这里照旧往下走，崩在 System.Text.Json ——
        //   报出来的是「The input does not contain any JSON tokens」，与真因隔了三层。
        //   子进程那侧已经修好；这一侧**也要挡**：被叫方撒过一次谎，调用方就不该再只信退出码。
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                "厚度场提取没有输出（退出码 " + proc.ExitCode + "）—— 子进程说的是："
                + (stderr.Trim().Length > 0 ? stderr.Trim() : "（它什么也没说）")
                + Environment.NewLine + "文件：" + path3dm
                + Environment.NewLine + "图层：「" + layer + "」"
                + "　—— 图层名对不上是最常见的一种：设计记录 3DM 由 WriteFinal3dm 写出，"
                + "图层结构与分析器要的单图层不同。");


        var dto = JsonSerializer.Deserialize<ThicknessDto>(stdout,
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidOperationException("厚度场 JSON 解析失败");

        var f = new ThicknessField
        {
            X0 = dto.X0, Z0 = dto.Z0, Step = dto.Step,
            Nx = dto.Nx, Nz = dto.Nz, T = dto.Thickness,
            GroupCount = dto.GroupCount, PlaneY = dto.PlaneY
        };
        // ⚠ 一个图层里有好几片、而调用方又没指定量哪一片 —— 这是**能正常跑完的错**：
        //   量到的是其中一片，另外几片被无声丢掉。必须让上层看得见。
        if (f.GroupCount > 1 && double.IsNaN(planeY))
            f.Warning = $"图层「{layer}」里有 {f.GroupCount} 片互不相连的实体，" +
                        $"本次只量了其中一片（中面 Y={f.PlaneY:0.0}）。" +
                        "要指定量哪一片，请给 planeY。";
        _tfCache[key] = f;
        return f;
    }

    // ── 报告

    public static string Report(string path3dm, DesignInputs p, FlangePlate g)
    {
        var m = Measure(path3dm);
        var w = new StringBuilder();

        w.AppendLine($"文件      {m.File}");
        w.AppendLine($"模型单位  {m.Units}（→mm 系数 {m.ToMm:0.###}）  容差 {m.Tolerance:0.####}");
        w.AppendLine($"引擎      Rhino {m.Rhino}（进程内 headless，经 {ProbeName}）");
        w.AppendLine();

        w.AppendLine("=== 图层清单 ===");
        w.AppendLine($"{"图层",-16}{"对象",6}{"实体",8}{"体积 mm³",14}{"铂重 g",10}{"表面积 mm²",14}");
        foreach (var l in m.Layers)
        {
            if (l.Objects == 0) { w.AppendLine($"{l.Name,-16}{"(空)",8}"); continue; }
            w.AppendLine($"{l.Name,-16}{l.Objects,6}{l.Solids,8}{l.VolumeMm3,14:0.0}" +
                         $"{l.VolumeMm3 * Materials.PtDensity * 1e-6,10:0.0}{l.AreaMm2,14:0.0}");
        }
        w.AppendLine();

        foreach (var l in m.Layers.Where(x => x.Objects > 0))
        {
            w.AppendLine($"--- {l.Name} ---");
            if (l.Box is not null)
                w.AppendLine($"  包围盒  X[{l.Box.MinX,9:0.000},{l.Box.MaxX,9:0.000}]  " +
                             $"Y[{l.Box.MinY,9:0.000},{l.Box.MaxY,9:0.000}]  " +
                             $"Z[{l.Box.MinZ,9:0.000},{l.Box.MaxZ,9:0.000}]");
            if (l.CylinderRadiiMm.Length > 0)
                w.AppendLine("  圆柱半径 " + string.Join(" / ", l.CylinderRadiiMm.Select(r => r.ToString("0.000"))) + " mm");
            if (l.PlanarThicknessMm.Length > 0)
                w.AppendLine("  平面短边 " + string.Join(" / ", l.PlanarThicknessMm.Take(8).Select(t => t.ToString("0.000"))) + " mm");
            w.AppendLine();
        }

        if (m.Notes.Length > 0)
        {
            w.AppendLine("=== 子进程提示 ===");
            foreach (var n in m.Notes) w.AppendLine("  " + n);
            w.AppendLine();
        }

        w.AppendLine("=== 代码常数 vs 3dm ===");
        w.AppendLine($"{"量",-22}{"代码",12}{"3dm",12}{"偏差",12}  出处");

        var tube = m.Layer("铂金管", "管", "tube", "pipe");
        var flange = m.Layer("法兰", "flange");

        // ── 口径对齐：.3dm 可能是**整线**（多段管 + n+1 片法兰），而代码常数是**单段**口径。
        //    段数优先取管的实体数；退而用「法兰片数 − 1」（见 LineSolver.FlangeCount）。
        int segCount = tube is { Solids: > 0 } ? tube.Solids
                     : flange is { Solids: > 1 } ? flange.Solids - 1 : 1;
        if (segCount < 1) segCount = 1;
        w.AppendLine($"（.3dm 含 {segCount} 段管 + {flange?.Solids ?? 0} 片法兰；" +
                     $"以下按**单段**口径对照，法兰片数按整线对照）");

        double? tubeOd = tube?.CylinderRadiiMm.Length > 0 ? tube.CylinderRadiiMm.Max() * 2 : null;
        double? tubeId = tube?.CylinderRadiiMm.Length > 0 ? tube.CylinderRadiiMm.Min() * 2 : null;
        double? tubeLen = tube?.Box is not null ? LongestEdge(tube.Box) / segCount : null;

        Row(w, "管 外径 OD [mm]", p.TubeIdMm + 2 * p.WallMinMm, tubeOd, "DesignInputs.TubeIdMm + 2×WallMinMm");
        Row(w, "管 内径 ID [mm]", p.TubeIdMm, tubeId, "DesignInputs.TubeIdMm");
        Row(w, "管 壁厚 [mm]", p.WallMinMm,
            tubeOd is not null && tubeId is not null ? (tubeOd - tubeId) / 2 : null, "DesignInputs.WallMinMm");
        Row(w, "管 段长 L [mm]", p.TubeLengthMm, tubeLen, "DesignInputs.TubeLengthMm");
        Row(w, "管 铂重 [g]", TubeMassG(p), tube?.Solids > 0 ? tube.MassPerSolidG : null,
            "π((ID/2+t)²−(ID/2)²)L·d");

        // 管孔与圆盘外缘都是圆柱面：最小半径=管孔，最大半径=圆盘。
        // 不能用包围盒 —— 该模型管轴在 Y（两端各一片法兰，Y 跨距 300 是段长不是法兰尺寸），
        // 且法兰平面内 X 向含舌片（伸到 −200），只有 Z 向才是圆盘直径。
        double? holeDia = flange?.CylinderRadiiMm.Length > 0 ? flange.CylinderRadiiMm.Min() * 2 : null;
        double? discDia = flange?.CylinderRadiiMm.Length > 1 ? flange.CylinderRadiiMm.Max() * 2 : null;
        double? flangeThick = flange?.PlanarThicknessMm.Length > 0 ? flange.PlanarThicknessMm.Min() : null;

        // 舌片末端：法兰平面内伸出圆盘之外的那一端。
        // ★ 必须先排除**管轴**：整线模型里多片法兰沿管轴铺开，该方向跨距最大，
        //   若不排除会把管轴极小值（如 −600）误判成舌片末端（踩过）。
        double? tabEndX = null;
        if (flange?.Box is not null && discDia is not null)
        {
            double r = discDia.Value * 0.5;
            var ext = new[]
            {
                (Min: flange.Box.MinX, Span: flange.Box.MaxX - flange.Box.MinX),
                (Min: flange.Box.MinY, Span: flange.Box.MaxY - flange.Box.MinY),
                (Min: flange.Box.MinZ, Span: flange.Box.MaxZ - flange.Box.MinZ)
            };
            double axisSpan = ext.Max(e => e.Span);          // 管轴 = 跨距最大的那根
            foreach (var e in ext)
                if (e.Span < axisSpan - 1e-9 && e.Min < -r * 1.1
                    && (tabEndX is null || e.Min < tabEndX)) tabEndX = e.Min;
        }

        // 平面净面积：单片实体体积 ÷ 板厚。README 引的 23 591.6 mm² 就是这个量
        double? plateArea = flange is { Solids: > 0 } && flangeThick > 0
            ? flange.VolumeMm3 / flange.Solids / flangeThick.Value : null;

        Row(w, "法兰 管孔直径 [mm]", 2 * g.HoleRadiusMm, holeDia, "FlangePlate.HoleRadiusMm");
        Row(w, "法兰 圆盘直径 [mm]", 2 * g.DiscRadiusMm, discDia, "FlangePlate.DiscRadiusMm");
        Row(w, "法兰 厚度 [mm]", g.ThicknessMm, flangeThick, "FlangePlate.ThicknessMm");
        Row(w, "舌片末端 X [mm]", g.TabEndXMm, tabEndX, "FlangePlate.TabEndXMm");
        Row(w, "法兰 平面净面积 [mm²]", CoupledSolver.PlateArea(g), plateArea,
            "CoupledSolver.PlateArea（数值积分宽度分布）");
        double platePairCode = CoupledSolver.PlateArea(g) * g.ThicknessMm * Materials.PtDensity * 1e-6;
        Row(w, "法兰 单片铂重 [g]", platePairCode,
            flange?.Solids > 0 ? flange.MassPerSolidG : null, "PlateArea×t×d（单片）");

        // 法兰按整线计数：n 段 = n+1 片（相邻段共用接头处那片）。
        Row(w, $"法兰 片数（{segCount} 段整线）", LineSolver.FlangeCount(segCount), flange?.Solids,
            "LineSolver.FlangeCount(n) = n+1");
        Row(w, "法兰 成对铂重 [g]（单段）", 2 * platePairCode,
            flange?.Solids > 0 ? 2 * flange.MassPerSolidG : null,
            "CoupledSolver.MassFlangePairG（单段两端各一片）");
        Row(w, "单段总铂 [g]", TubeMassG(p) + 2 * platePairCode,
            tube is { Solids: > 0 } && flange is { Solids: > 0 }
                ? tube.MassPerSolidG + 2 * flange.MassPerSolidG : null,
            "管 + 成对法兰 = CoupledSolver.MassTotalG");
        Row(w, $"整线总铂 [g]（{segCount} 段）",
            segCount * TubeMassG(p) + LineSolver.FlangeCount(segCount) * platePairCode,
            tube is not null && flange is not null
                ? (tube.VolumeMm3 + flange.VolumeMm3) * Materials.PtDensity * 1e-6 : null,
            "n×管 + (n+1)×法兰");

        w.AppendLine();
        w.AppendLine("偏差 >0.5% 需要查：要么 .3dm 改过而代码未同步，要么当初抄错。");
        w.AppendLine("「法兰 平面净面积」这一行最关键：它同时校核了 FlangePlate 的圆盘半径、管孔半径、");
        w.AppendLine("舌片末端坐标与半宽，以及 HalfWidth/Tangent 那套解析轮廓 —— 任何一个错，面积就对不上。");
        return w.ToString();
    }

    private static double TubeMassG(DesignInputs p)
    {
        double ri = p.TubeIdMm * 0.5, ro = ri + p.WallMinMm;
        return Math.PI * (ro * ro - ri * ri) * p.TubeLengthMm * Materials.PtDensity * 1e-6;
    }

    private static double LongestEdge(Box b)
        => Math.Max(b.MaxX - b.MinX, Math.Max(b.MaxY - b.MinY, b.MaxZ - b.MinZ));

    private static void Row(StringBuilder w, string name, double code, double? actual, string src)
    {
        if (actual is null)
        {
            w.AppendLine($"{name,-22}{code,12:0.000}{"—",12}{"—",12}  {src}");
            return;
        }
        double rel = Math.Abs(code) > 1e-12 ? (actual.Value - code) / code * 100 : double.NaN;
        string flag = Math.Abs(rel) <= 0.5 ? "✓" : "✗";
        w.AppendLine($"{name,-22}{code,12:0.000}{actual.Value,12:0.000}{rel,11:+0.00;-0.00;0.00}%  {flag} {src}");
    }
}
