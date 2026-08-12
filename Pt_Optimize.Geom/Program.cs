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

        // plate 模式：把优化搜出来的**解析法兰**写成 .3dm（与 thickness 模式反向）
        //   Pt_Optimize.Geom.exe plate <out.3dm> <盘半径> <孔半径> <舌端X> <舌端半宽> <厚度1[,厚度2,…]>
        // 每个厚度出一个实体，沿 +X 依次排开、各自独立成体，图层统一为「法兰」。
        // 轮廓 = 盘圆弧（切点之外那段）+ 舌片两条直边 + 舌端直边，中心挖孔，再拉伸。
        if (args.Length > 0 && args[0] == "plate")
        {
            if (args.Length < 7)
            {
                Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe plate <out.3dm> " +
                                        "<盘半径> <孔半径> <舌端X> <舌端半宽> <厚度[,厚度…]>");
                return 64;
            }
            string outPath = args[1];
            if (!double.TryParse(args[2], out double discR) ||
                !double.TryParse(args[3], out double holeR) ||
                !double.TryParse(args[4], out double tabX) ||
                !double.TryParse(args[5], out double tabHW))
            { Console.Error.WriteLine("参数解析失败"); return 64; }
            var thicks = new List<double>();
            foreach (var s in args[6].Split(','))
                if (double.TryParse(s, out double tv)) thicks.Add(tv);
            if (thicks.Count == 0) { Console.Error.WriteLine("厚度列表为空"); return 64; }

            try { RhinoInside.Resolver.Initialize(); }
            catch (Exception e) { Console.Error.WriteLine("Resolver 失败：" + e.Message); return 1; }
            try { return RunPlate(outPath, discR, holeR, tabX, tabHW, thicks); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 2; }
            finally { Console.Out.Flush(); Environment.Exit(Environment.ExitCode); }
        }

        // scale 模式：把用户画的法兰**按厚度方向整体缩放** k 倍，另存新 .3dm
        //   Pt_Optimize.Geom.exe scale <in.3dm> <out.3dm> <图层名> <k> [平面Y]
        //
        // 为什么需要它：稳态温差对法兰厚度极敏感（约 1300 K/mm），10 K 的窗口只有
        // 0.008 mm 宽 —— 画图与加工都不可能一次画准。于是让程序算出倍数 k，
        // 再由本模式**直接出改好厚度的图**，工程师不必回 Rhino 手算每一级。
        //
        // 用**沿板法向的非均匀缩放**实现：轮廓、孔、槽、各级阶梯的半径全部不动，
        // 只有厚度乘 k，且各级之间的比例（如 3:2:1）完整保留。
        if (args.Length > 0 && args[0] == "scale")
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe scale <in.3dm> <out.3dm> <图层名> <k> [平面Y]");
                return 64;
            }
            string inP = args[1], outP = args[2], lay = args[3];
            double kScale = 1.0;
            if (!args[4].Contains(',') && (!double.TryParse(args[4], out kScale) || kScale <= 0))
            { Console.Error.WriteLine("缩放倍数 k 必须为正数"); return 64; }
            double planeYs = args.Length > 5 && double.TryParse(args[5], out var pys) ? pys : double.NaN;
            // k 可以给一个（整片统一）或多个逗号分隔（逐级独立，需各级为独立实体）
            double[]? kList = null;
            if (args[4].Contains(','))
            {
                kList = args[4].Split(',').Select(x => double.TryParse(x, out var v) ? v : 1.0).ToArray();
                kScale = kList[0];
            }
            if (!File.Exists(inP)) { Console.Error.WriteLine("找不到文件：" + inP); return 66; }

            try { RhinoInside.Resolver.Initialize(); }
            catch (Exception e) { Console.Error.WriteLine("Resolver 失败：" + e.Message); return 1; }
            try { return RunScale(inP, outP, lay, kScale, planeYs, kList); }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 2; }
            finally { Console.Out.Flush(); Environment.Exit(Environment.ExitCode); }
        }

        if (args.Length < 1)
        {
            Console.Error.WriteLine("用法：Pt_Optimize.Geom.exe <file.3dm>");
            Console.Error.WriteLine("      Pt_Optimize.Geom.exe thickness <file.3dm> <图层名> [平面Y] [步长]");
            Console.Error.WriteLine("      Pt_Optimize.Geom.exe plate <out.3dm> <盘半径> <孔半径> " +
                                    "<舌端X> <舌端半宽> <厚度[,厚度…]>");
            Console.Error.WriteLine("      Pt_Optimize.Geom.exe scale <in.3dm> <out.3dm> <图层名> <k> [平面Y]");
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

    /// <summary>
    /// 把解析法兰写成 .3dm。轮廓与 Pt_Optimize 里的 FlangePlate 完全一致：
    ///   · 圆盘半径 R，管孔半径 r0
    ///   · 舌片两条直边与圆盘**相切**，切点由 R/|P| 定（P = 舌端点）
    ///   · 舌端一条直边，半宽 tabHW
    /// 每个厚度出一个独立实体，沿 +X 排开，避免叠在一起。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunPlate(string outPath, double discR, double holeR,
                                double tabX, double tabHW, List<double> thicks)
    {
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var doc = RhinoDoc.CreateHeadless(null);
            if (doc == null) { Console.Error.WriteLine("CreateHeadless 返回 null"); return 3; }
            doc.ModelUnitSystem = UnitSystem.Millimeters;
            double tol = doc.ModelAbsoluteTolerance;

            int layer = doc.Layers.Add("法兰", System.Drawing.Color.Gold);
            if (layer < 0) layer = 0;

            // 切点：|P|·cos(θ−φ) = R，取上支
            double amp = Math.Sqrt(tabX * tabX + tabHW * tabHW);
            if (discR >= amp) { Console.Error.WriteLine("盘半径过大，切点不存在"); return 4; }
            double phi = Math.Atan2(tabHW, tabX);
            double th = phi - Math.Acos(discR / amp);
            // ★ 板面在 **XZ 平面**、厚度沿 **Y** —— 必须与 thickness 模式的读取约定一致，
            //   也与 Pt_Heater.3dm 和 Core 的 FlangePlate(x, z) 一致。
            //   （2026-08-12 修正：早先画在 XY 面、沿 Z 拉伸，与读取端差 90°，
            //     导致自己写出的 .3dm 再读回来量到 0 材料，round-trip 不成立。）
            var tp = new Point3d(discR * Math.Cos(th), 0, discR * Math.Sin(th));   // 上切点
            var tn = new Point3d(tp.X, 0, -tp.Z);                                   // 下切点
            var e1 = new Point3d(tabX, 0, tabHW);
            var e2 = new Point3d(tabX, 0, -tabHW);

            double spacing = 2.5 * discR + Math.Abs(tabX);
            int made = 0;
            for (int i = 0; i < thicks.Count; i++)
            {
                double t = thicks[i];
                // 保留的盘弧：从下切点经 +X 侧到上切点（劣弧在舌片一侧被直边取代）
                var arc = new Arc(tn, new Point3d(discR, 0, 0), tp);   // 经 +X 侧
                if (!arc.IsValid) { Console.Error.WriteLine("圆弧无效"); return 5; }

                var poly = new PolyCurve();
                poly.Append(new ArcCurve(arc));                 // 下切点 → +X → 上切点
                poly.Append(new LineCurve(tp, e1));             // 上切点 → 舌端上角
                poly.Append(new LineCurve(e1, e2));             // 舌端边
                poly.Append(new LineCurve(e2, tn));             // 舌端下角 → 下切点
                poly.MakeClosed(tol);
                if (!poly.IsClosed) { Console.Error.WriteLine("轮廓未闭合"); return 6; }

                var hole = new Circle(Plane.WorldZX, Point3d.Origin, holeR).ToNurbsCurve();
                var faces = Brep.CreatePlanarBreps(new Curve[] { poly, hole }, tol);
                if (faces == null || faces.Length == 0)
                { Console.Error.WriteLine("平面片创建失败"); return 7; }

                var solid = faces[0].Faces[0].CreateExtrusion(
                                new LineCurve(Point3d.Origin, new Point3d(0, t, 0)), true);
                if (solid == null) { Console.Error.WriteLine("拉伸失败"); return 8; }

                var xf = Transform.Translation(i * spacing, 0, 0);
                solid.Transform(xf);

                var att = new Rhino.DocObjects.ObjectAttributes { LayerIndex = layer };
                att.Name = $"法兰{i + 1}_t{t:0.000}mm";
                if (doc.Objects.AddBrep(solid, att) != Guid.Empty) made++;
            }

            if (!doc.WriteFile(outPath, new Rhino.FileIO.FileWriteOptions { FileVersion = 7 }))
            { Console.Error.WriteLine("写文件失败：" + outPath); return 9; }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = outPath, solids = made, discR, holeR, tabX, tabHW,
                thickness = thicks
            }));
            return 0;
        }
    }


    /// <summary>
    /// 按**厚度方向**缩放法兰并另存。轮廓、孔、槽、各级半径全部不动，只有厚度乘 k。
    ///
    /// 两种用法：
    ///   · 给**一个** k  → 整片统一缩放，各级之间的比例（如 3:2:1）完整保留
    ///   · 给**多个** k  → 按实体在该图层里的顺序逐个缩放，可实现「外圈不动、只调内圈」
    ///     （前提是各级在 .3dm 里是**独立实体**；若整片是一个实体，只能统一缩放，
    ///      本函数会明确报出来，不会假装做到了）
    ///
    /// 缩放基准取每个实体自身在法向上的**最小坐标**（贴着安装面那一侧），
    /// 于是加厚是往外长，安装面位置不变。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunScale(string inPath, string outPath, string layer,
                                double kUniform, double planeY, double[]? kPer = null)
    {
        using (new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.Hidden))
        {
            var doc = RhinoDoc.OpenHeadless(inPath);
            if (doc == null) { Console.Error.WriteLine("OpenHeadless 返回 null：" + inPath); return 3; }

            int li = doc.Layers.FindByFullPath(layer, -1);
            if (li < 0)
            {
                for (int i = 0; i < doc.Layers.Count; i++)
                    if (doc.Layers[i].Name.Contains(layer, StringComparison.OrdinalIgnoreCase)) { li = i; break; }
            }
            if (li < 0)
            {
                Console.Error.WriteLine($"找不到图层「{layer}」。现有图层：" +
                    string.Join("、", Enumerable.Range(0, doc.Layers.Count).Select(i => doc.Layers[i].Name)));
                return 4;
            }

            var objs = doc.Objects.FindByLayer(doc.Layers[li])?
                          .Where(o => o.Geometry is Brep || o.Geometry is Extrusion).ToList()
                       ?? new List<Rhino.DocObjects.RhinoObject>();
            if (objs.Count == 0) { Console.Error.WriteLine("该图层没有实体"); return 5; }

            Console.WriteLine($"图层「{doc.Layers[li].Name}」实体数 {objs.Count}");
            if (kPer is { Length: > 1 } && objs.Count == 1)
                Console.WriteLine("⚠ 该图层只有 1 个实体 —— 各级不是独立实体，无法逐级缩放，" +
                                  "本次按第一个 k 统一缩放。要逐级调，请在 Rhino 里把各级拆成独立实体。");

            for (int i = 0; i < objs.Count; i++)
            {
                double k = kPer is { Length: > 0 }
                         ? kPer[Math.Min(i, kPer.Length - 1)]
                         : kUniform;
                var geo = objs[i].Geometry.Duplicate();
                var bb = geo.GetBoundingBox(true);
                // 板面在 XZ，厚度沿 Y（与 thickness 模式的射线方向一致）
                double y0 = double.IsNaN(planeY) ? bb.Min.Y : planeY;
                double before = bb.Max.Y - bb.Min.Y;

                var xf = Transform.Scale(new Plane(new Point3d(0, y0, 0), Vector3d.YAxis),
                                         1.0, 1.0, k);      // 平面法向 = Y，故第三个分量是厚度方向
                geo.Transform(xf);
                var bb2 = geo.GetBoundingBox(true);
                Console.WriteLine($"  实体 {i + 1}：厚度 {before:0.000} → {bb2.Max.Y - bb2.Min.Y:0.000} mm" +
                                  $"（×{k:0.0000}）");

                doc.Objects.Replace(objs[i].Id, geo as Brep ?? ((Extrusion)geo).ToBrep());
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
            Directory.CreateDirectory(dir);
            if (!doc.SaveAs(outPath)) { Console.Error.WriteLine("保存失败：" + outPath); return 6; }
            Console.WriteLine("已写出 " + outPath);
            return 0;
        }
    }

}
