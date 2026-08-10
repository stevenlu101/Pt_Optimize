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
