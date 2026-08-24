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
    /// 把**定案构型**（<see cref="FinalDesign"/>）整机写成 .3dm：
    /// 三段铂管 + 四片法兰（板身 / 环外级 / 环内级）+ 压接段参考几何。
    ///
    /// ★ 几何定义只在 <see cref="FinalDesign"/>。这里把它序列化成规格 JSON 交给子进程渲染，
    ///   子进程**不持有任何定案值** —— 否则同一个数就在两个项目里各存一份，
    ///   而「抄两处然后悄悄漂开」是本项目最常见的失效（HANDOVER §1.8）。
    /// </summary>
    /// <returns>子进程 stdout（JSON 回显，用于与规格逐项比对）</returns>
    public static string WriteFinal3dm(FinalDesign fd, string outPath,
                                       double tubeIdMm = 50.0, double segLenMm = 300.0,
                                       int segCount = 3)
    {
        string probe = FindProbe()
            ?? throw new FileNotFoundException($"找不到 {ProbeName}.exe。先构建 {ProbeName}（需本机装 Rhino 8）。");

        string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var names = new[] { "入口", "共用1", "共用2", "出口" };
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"name\":\"{fd.Name}\",");
        sb.Append($"\"wallMm\":{R(fd.WallMm)},\"tubeIdMm\":{R(tubeIdMm)},");
        sb.Append($"\"segLenMm\":{R(segLenMm)},\"segCount\":{segCount},");
        sb.Append($"\"discR\":{R(fd.DiscRadiusMm)},\"holeR\":{R(fd.HoleRadiusMm)},");
        sb.Append($"\"tabX\":{R(-fd.TabLengthMm)},\"tabHW\":{R(fd.TabHalfWidthMm)},");
        sb.Append($"\"filletR\":{R(fd.TabFilletMm)},\"clampLenMm\":{R(fd.ClampLengthMm)},");
        sb.Append($"\"ringR\":[{R(fd.RingRadiiMm[0])},{R(fd.RingRadiiMm[1])}],");
        sb.Append("\"plates\":[");
        for (int j = 0; j < 4; j++)
        {
            double t = fd.TabThickMm[j];
            if (j > 0) sb.Append(',');
            sb.Append($"{{\"name\":\"{names[j]}\",\"t\":{R(t)},");
            sb.Append($"\"ring\":[{R(t * fd.RingMul[j])},{R(t * fd.RingMulOuter(j))}]}}");
        }
        sb.Append("]}");

        string spec = Path.Combine(Path.GetDirectoryName(outPath) ?? ".",
                                   Path.GetFileNameWithoutExtension(outPath) + ".spec.json");
        File.WriteAllText(spec, sb.ToString(), new UTF8Encoding(false));

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
        string so = proc.StandardOutput.ReadToEnd(), se = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} final 退出码 {proc.ExitCode}：{se}{so}");
        return so.Trim();
    }

    /// <summary>
    /// 写一张**单图层、多级台阶**的法兰 .3dm（调 Geom 子进程的 steps 模式）。
    ///
    /// ★ 它补的是解析路径与 .3dm 路径之间**断掉的那一环**：
    ///   现有的「导出本页/定案 3DM」走 <see cref="WriteFinal3dm"/>，写的是**多图层**
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
                                         double slotRInMm = double.NaN, double slotROutMm = double.NaN)
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
        // 而定案几何早已不用梯形（见 Geom 的 RunSteps 注释）。
        psi.ArgumentList.Add("par");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + probe);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{ProbeName} steps 退出码 {proc.ExitCode}。{stderr.Trim()}");
        return stdout;
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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"厚度场提取失败（退出码 {proc.ExitCode}）：{stderr.Trim()}");

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
