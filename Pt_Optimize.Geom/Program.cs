using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

// ============================================================================
//  几何量测子进程 —— harness 范式三段式（顺序错任一步都启动失败）
//    ① [STAThread]：任何进程建 RhinoCore 都要 STA（它建隐藏窗口/COM），
//       控制台默认 MTA → RhinoCore 构造抛 COMException E_FAIL
//    ② Resolver.Initialize()：必须早于任何 RhinoCommon 类型 JIT
//    ③ [MethodImpl(NoInlining)] Run()：真正碰 Rhino 的代码隔离在此，
//       否则会被内联进 Main，类型解析提前到 ② 之前
//
//  用法：Pt_Optimize.Geom.exe <file.3dm>      → stdout 输出 JSON
//        判定不在这里做，见 Pt_Optimize/Core/Geometry3dm.cs
// ============================================================================
internal static class GeomProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // thickness 模式：把某图层的实体投到一个平面上，逐点沿法向打射线量厚度
        //   Pt_Optimize.Geom.exe thickness <file.3dm> <图层名> <平面坐标> <网格步长>
        // 输出厚度场 JSON。厚度 0 = 该点无材料（轮廓外、管孔、开槽），
        // 非零值直接给出阶梯厚度 —— 一次扫描同时拿到轮廓、孔、槽与厚度分区。
        if (args.Length > 0 && args[0] == "thickness")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe thickness <file.3dm> <图层名> [平面Y] [步长]");
                return 64;
            }
            string f3 = args[1], layer = args[2];
            double yPlane = args.Length > 3 && double.TryParse(args[3], out var yy) ? yy : double.NaN;
            double step = args.Length > 4 && double.TryParse(args[4], out var ss) ? ss : 1.0;
            if (!File.Exists(f3)) { Console.Error.WriteLine("找不到文件：" + f3); return 66; }

            try { RhinoInside.Resolver.Initialize(); }
            catch (Exception e) { Console.Error.WriteLine("Resolver 失败：" + e.Message); return 1; }
            try { return RunThickness(f3, layer, yPlane, step); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 2; }
            finally { Console.Out.Flush(); Environment.Exit(Environment.ExitCode); }
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe <file.3dm>");
            Console.Error.WriteLine("      Pt_Optimize.Geom.exe thickness <file.3dm> <图层名> [平面Y] [步长]");
            return 64;
        }
        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("找不到文件：" + path);
            return 66;
        }

        try { RhinoInside.Resolver.Initialize(); }
        catch (Exception e)
        {
            Console.Error.WriteLine("Resolver 失败（本机是否装了 Rhino 8？）：" + e.Message);
            return 1;
        }

        try { return Run(path); }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.GetType().Name + ": " + e.Message);
            return 2;
        }
        finally
        {
            // RhinoCore 的前台线程会挡住进程自然退出，显式退（房规 §6 坑 3）
            Console.Out.Flush();
            Environment.Exit(Environment.ExitCode);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string path)
    {
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var doc = RhinoDoc.OpenHeadless(path);
            if (doc == null) { Console.Error.WriteLine("OpenHeadless 返回 null：" + path); return 3; }

            // 容差一律从文档取，不硬编码（房规 §6 坑 5）
            double tol = doc.ModelAbsoluteTolerance;

            var result = new Dictionary<string, object>
            {
                ["file"] = Path.GetFullPath(path),
                ["units"] = doc.ModelUnitSystem.ToString(),
                ["toMm"] = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters),
                ["tolerance"] = tol,
                ["rhino"] = RhinoApp.Version.ToString()
            };

            var byIndex = new Dictionary<int, Layer>();
            foreach (var ly in doc.Layers)
                byIndex[ly.Index] = new Layer { Name = ly.Name };

            var notes = new List<string>();
            var items = new List<object>();

            foreach (var ob in doc.Objects)
            {
                var geo = ob.Geometry;
                if (geo == null) continue;

                int li = ob.Attributes.LayerIndex;
                if (!byIndex.TryGetValue(li, out var s))
                    byIndex[li] = s = new Layer { Name = "<图层 " + li + ">" };

                s.Objects++;

                var bb = geo.GetBoundingBox(true);
                if (bb.IsValid) s.Grow(bb);

                var before = new { V = s.VolumeMm3, A = s.AreaMm2 };
                Accumulate(s, geo, tol, notes);

                // 逐件明细：光看图层汇总分不清「两端各一片」还是「同一端两片」
                items.Add(new Dictionary<string, object>
                {
                    ["layer"] = s.Name,
                    ["name"] = ob.Attributes.Name ?? "",
                    ["type"] = geo.GetType().Name,
                    ["volumeMm3"] = s.VolumeMm3 - before.V,
                    ["areaMm2"] = s.AreaMm2 - before.A,
                    ["centroid"] = Centroid(geo),
                    ["box"] = BoxDto(bb)
                });
            }

            result["layers"] = byIndex.Values.Select(l => l.ToDto()).ToList();
            result["objects"] = items;
            result["notes"] = notes.Distinct().ToList();

            Console.WriteLine(JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
    }

    /// <summary>把一件几何并进图层汇总。Brep 之外的类型（Extrusion/Surface）先归一化。</summary>
    private static void Accumulate(Layer s, GeometryBase geo, double tol, List<string> notes)
    {
        Brep brep = geo as Brep;
        if (brep == null && geo is Extrusion ex) brep = ex.ToBrep();
        if (brep == null && geo is Surface sf) brep = sf.ToBrep();
        if (brep == null) { notes.Add("未参与统计的几何类型：" + geo.GetType().Name); return; }

        if (brep.IsSolid)
        {
            s.Solids++;
            // Compute 失败返回 null 是常态，必须判空（房规 §6 坑 6）
            var vmp = VolumeMassProperties.Compute(brep);
            if (vmp != null) s.VolumeMm3 += Math.Abs(vmp.Volume);   // 朝向可能令体积为负
            else notes.Add("VolumeMassProperties.Compute 返回 null");
        }

        var amp = AreaMassProperties.Compute(brep);
        if (amp != null) s.AreaMm2 += Math.Abs(amp.Area);
        else notes.Add("AreaMassProperties.Compute 返回 null");

        foreach (var face in brep.Faces)
        {
            var srf = face.UnderlyingSurface();
            if (srf == null) continue;

            if (srf.TryGetCylinder(out var cyl, tol) && cyl.IsValid)
            {
                Merge(s.CylinderRadiiMm, cyl.Radius, tol);
            }
            else if (srf.TryGetPlane(out _, tol))
            {
                // 平板的一对平行平面间距即板厚：单张平面的包围盒最短边
                var fb = face.GetBoundingBox(true);
                if (!fb.IsValid) continue;
                double t = Math.Min(fb.Diagonal.X, Math.Min(fb.Diagonal.Y, fb.Diagonal.Z));
                if (t > tol) Merge(s.PlanarThicknessMm, t, tol);
            }
        }
    }

    /// <summary>
    /// 厚度场提取：在 x–z 平面上布网格，逐点沿 **Y**（管轴）打射线，
    /// 把与实体的交点按进出配对，累加得到该点的材料厚度。
    ///
    /// 一次扫描同时给出：轮廓外（t=0）、管孔（t=0）、**开槽（t=0）**、**阶梯厚度**（1/2/3…）。
    /// 求解器要的本来就是 t(x,z)，故不必提取轮廓环 —— 任意形状照单全收。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunThickness(string path, string layerName, double yPlane, double step)
    {
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var doc = RhinoDoc.OpenHeadless(path);
            if (doc == null) { Console.Error.WriteLine("OpenHeadless 返回 null"); return 3; }
            double tol = doc.ModelAbsoluteTolerance;

            // 收集该图层的 Brep（yPlane 给定时取沿 Y 最接近的那一片）
            var breps = new List<Brep>();
            var boxes = new List<BoundingBox>();
            foreach (var ob in doc.Objects)
            {
                var ly = doc.Layers.FindIndex(ob.Attributes.LayerIndex);
                if (ly == null || ly.Name.IndexOf(layerName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var geo = ob.Geometry;
                Brep b = geo as Brep;
                if (b == null && geo is Extrusion ex) b = ex.ToBrep();
                if (b == null) continue;
                breps.Add(b); boxes.Add(b.GetBoundingBox(true));
            }
            if (breps.Count == 0) { Console.Error.WriteLine("图层无实体：" + layerName); return 4; }

            int pick = 0;
            if (!double.IsNaN(yPlane))
            {
                double best = double.MaxValue;
                for (int i = 0; i < boxes.Count; i++)
                {
                    double c = 0.5 * (boxes[i].Min.Y + boxes[i].Max.Y);
                    if (Math.Abs(c - yPlane) < best) { best = Math.Abs(c - yPlane); pick = i; }
                }
            }
            var brep = breps[pick];
            var bb = boxes[pick];
            double yLo = bb.Min.Y - 10, yHi = bb.Max.Y + 10;

            double x0 = Math.Floor(bb.Min.X / step) * step - step;
            double x1 = Math.Ceiling(bb.Max.X / step) * step + step;
            double z0 = Math.Floor(bb.Min.Z / step) * step - step;
            double z1 = Math.Ceiling(bb.Max.Z / step) * step + step;
            int nx = (int)Math.Round((x1 - x0) / step) + 1;
            int nz = (int)Math.Round((z1 - z0) / step) + 1;

            var t = new double[nx * nz];
            int solidPts = 0;
            for (int i = 0; i < nx; i++)
            {
                double x = x0 + i * step;
                for (int j = 0; j < nz; j++)
                {
                    double z = z0 + j * step;
                    var lc = new LineCurve(new Line(new Point3d(x, yLo, z), new Point3d(x, yHi, z)));
                    if (!Rhino.Geometry.Intersect.Intersection.CurveBrep(
                            lc, brep, tol, out Curve[] _, out Point3d[] pts) || pts == null || pts.Length < 2)
                        continue;
                    var ys = pts.Select(q => q.Y).OrderBy(v => v).ToArray();
                    double sum = 0;
                    for (int k = 0; k + 1 < ys.Length; k += 2) sum += ys[k + 1] - ys[k];
                    if (sum > tol) { t[i * nz + j] = sum; solidPts++; }
                }
            }

            var outp = new Dictionary<string, object>
            {
                ["file"] = Path.GetFullPath(path),
                ["layer"] = layerName,
                ["planeY"] = 0.5 * (bb.Min.Y + bb.Max.Y),
                ["x0"] = x0, ["z0"] = z0, ["step"] = step, ["nx"] = nx, ["nz"] = nz,
                ["solidPoints"] = solidPts,
                ["areaMm2"] = solidPts * step * step,
                ["volumeMm3"] = t.Sum() * step * step,
                ["thickness"] = t
            };
            Console.WriteLine(JsonSerializer.Serialize(outp));
            return 0;
        }
    }

    /// <summary>体心（实体取体积质心，否则取面积质心；都拿不到返回 null）</summary>
    private static object Centroid(GeometryBase geo)
    {
        Brep brep = geo as Brep;
        if (brep == null && geo is Extrusion ex) brep = ex.ToBrep();
        if (brep == null && geo is Surface sf) brep = sf.ToBrep();
        if (brep == null) return null;

        Point3d c;
        if (brep.IsSolid)
        {
            var vmp = VolumeMassProperties.Compute(brep);
            if (vmp == null) return null;
            c = vmp.Centroid;
        }
        else
        {
            var amp = AreaMassProperties.Compute(brep);
            if (amp == null) return null;
            c = amp.Centroid;
        }
        return new Dictionary<string, double> { ["x"] = c.X, ["y"] = c.Y, ["z"] = c.Z };
    }

    private static object BoxDto(BoundingBox b) => b.IsValid
        ? new Dictionary<string, double>
        {
            ["minX"] = b.Min.X, ["minY"] = b.Min.Y, ["minZ"] = b.Min.Z,
            ["maxX"] = b.Max.X, ["maxY"] = b.Max.Y, ["maxZ"] = b.Max.Z
        }
        : null;

    /// <summary>容差内视为同一个值，避免同一半径被多张面重复登记</summary>
    private static void Merge(List<double> xs, double v, double tol)
    {
        if (xs.Any(x => Math.Abs(x - v) <= tol)) return;
        xs.Add(v);
        xs.Sort();
    }

    private sealed class Layer
    {
        public string Name = "";
        public int Objects, Solids;
        public double VolumeMm3, AreaMm2;
        public readonly List<double> CylinderRadiiMm = new List<double>();
        public readonly List<double> PlanarThicknessMm = new List<double>();
        private BoundingBox _box = BoundingBox.Unset;

        public void Grow(BoundingBox bb) => _box = _box.IsValid ? BoundingBox.Union(_box, bb) : bb;

        public object ToDto() => new Dictionary<string, object>
        {
            ["name"] = Name,
            ["objects"] = Objects,
            ["solids"] = Solids,
            ["volumeMm3"] = VolumeMm3,
            ["areaMm2"] = AreaMm2,
            ["cylinderRadiiMm"] = CylinderRadiiMm,
            ["planarThicknessMm"] = PlanarThicknessMm,
            ["box"] = _box.IsValid
                ? new Dictionary<string, double>
                {
                    ["minX"] = _box.Min.X, ["minY"] = _box.Min.Y, ["minZ"] = _box.Min.Z,
                    ["maxX"] = _box.Max.X, ["maxY"] = _box.Max.Y, ["maxZ"] = _box.Max.Z
                }
                : null
        };
    }
}
